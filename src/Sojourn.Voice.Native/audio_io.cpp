// Audio I/O: WASAPI shared mode, event-driven, on MMCSS threads; or WAV files and paced silence for tests.
// Both directions run at 48 kHz float (WASAPI converts for us with AUTOCONVERTPCM): capture mono, render stereo,
// in 10 ms frames handed to the engine (CaptureFrame / RenderFrame).
#include "internal.h"

#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#include <avrt.h>
#include <ksmedia.h>

#include <cmath>
#include <cstdio>
#include <deque>
#include <fstream>
#include <thread>

namespace sv {

template <class T>
struct ComPtr {
    T* p = nullptr;
    ~ComPtr() { reset(); }
    void reset() {
        if (p) p->Release();
        p = nullptr;
    }
    T** put() {
        reset();
        return &p;
    }
    T* operator->() const { return p; }
    explicit operator bool() const { return p != nullptr; }
};

static std::string Utf8(const wchar_t* w) {
    if (!w) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
    std::string s(n > 0 ? n - 1 : 0, '\0');
    if (n > 1) WideCharToMultiByte(CP_UTF8, 0, w, -1, s.data(), n, nullptr, nullptr);
    return s;
}
static std::wstring Wide(const std::string& s) {
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, nullptr, 0);
    std::wstring w(n > 0 ? n - 1 : 0, L'\0');
    if (n > 1) MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, w.data(), n);
    return w;
}

struct DeviceInfo {
    std::string id, name;
    bool is_default, is_comms;
};

static std::vector<DeviceInfo> ListDevices(bool capture) {
    std::vector<DeviceInfo> out;
    ComPtr<IMMDeviceEnumerator> en;
    if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, __uuidof(IMMDeviceEnumerator),
                                (void**)en.put())))
        return out;
    auto default_id = [&](ERole role) {
        std::string r;
        ComPtr<IMMDevice> d;
        if (SUCCEEDED(en->GetDefaultAudioEndpoint(capture ? eCapture : eRender, role, d.put()))) {
            LPWSTR id = nullptr;
            if (SUCCEEDED(d->GetId(&id))) {
                r = Utf8(id);
                CoTaskMemFree(id);
            }
        }
        return r;
    };
    std::string def_id = default_id(eConsole), comms_id = default_id(eCommunications);
    ComPtr<IMMDeviceCollection> col;
    if (FAILED(en->EnumAudioEndpoints(capture ? eCapture : eRender, DEVICE_STATE_ACTIVE, col.put()))) return out;
    UINT n = 0;
    col->GetCount(&n);
    for (UINT i = 0; i < n; i++) {
        ComPtr<IMMDevice> d;
        if (FAILED(col->Item(i, d.put()))) continue;
        DeviceInfo info;
        LPWSTR id = nullptr;
        if (SUCCEEDED(d->GetId(&id))) {
            info.id = Utf8(id);
            CoTaskMemFree(id);
        }
        ComPtr<IPropertyStore> props;
        if (SUCCEEDED(d->OpenPropertyStore(STGM_READ, props.put()))) {
            PROPVARIANT v;
            PropVariantInit(&v);
            if (SUCCEEDED(props->GetValue(PKEY_Device_FriendlyName, &v)) && v.vt == VT_LPWSTR) info.name = Utf8(v.pwszVal);
            PropVariantClear(&v);
        }
        info.is_default = info.id == def_id;
        info.is_comms = info.id == comms_id;
        out.push_back(info);
    }
    return out;
}

std::string DeviceListJson(bool capture) {
    bool co = SUCCEEDED(CoInitializeEx(nullptr, COINIT_MULTITHREADED));
    auto list = ListDevices(capture);
    if (co) CoUninitialize();
    std::string j = "[";
    for (size_t i = 0; i < list.size(); i++) {
        if (i) j += ",";
        j += "{\"id\":\"" + JsonEscape(list[i].id) + "\",\"name\":\"" + JsonEscape(list[i].name) +
             "\",\"default\":" + (list[i].is_default ? "true" : "false") +
             ",\"comms\":" + (list[i].is_comms ? "true" : "false") + "}";
    }
    return j + "]";
}

// Resolves a device spec to an IMMDevice: "" the default device, "comms" the default communications device, an
// endpoint id, or "name:substring".
static bool OpenDevice(IMMDeviceEnumerator* en, bool capture, const std::string& spec, IMMDevice** out,
                       std::string* name) {
    EDataFlow flow = capture ? eCapture : eRender;
    HRESULT hr = E_FAIL;
    if (spec.empty()) {
        hr = en->GetDefaultAudioEndpoint(flow, eConsole, out);
    } else if (spec == "comms") {
        hr = en->GetDefaultAudioEndpoint(flow, eCommunications, out);
    } else if (spec.rfind("name:", 0) == 0) {
        std::string want = spec.substr(5);
        for (auto& d : ListDevices(capture)) {
            if (d.name.find(want) != std::string::npos) {
                hr = en->GetDevice(Wide(d.id).c_str(), out);
                break;
            }
        }
    } else {
        hr = en->GetDevice(Wide(spec).c_str(), out);
    }
    if (FAILED(hr) || !*out) return false;
    ComPtr<IPropertyStore> props;
    if (SUCCEEDED((*out)->OpenPropertyStore(STGM_READ, props.put()))) {
        PROPVARIANT v;
        PropVariantInit(&v);
        if (SUCCEEDED(props->GetValue(PKEY_Device_FriendlyName, &v)) && v.vt == VT_LPWSTR) *name = Utf8(v.pwszVal);
        PropVariantClear(&v);
    }
    return true;
}

static WAVEFORMATEXTENSIBLE FloatFormat(int channels) {
    WAVEFORMATEXTENSIBLE f{};
    f.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
    f.Format.nChannels = (WORD)channels;
    f.Format.nSamplesPerSec = kRate;
    f.Format.wBitsPerSample = 32;
    f.Format.nBlockAlign = (WORD)(4 * channels);
    f.Format.nAvgBytesPerSec = kRate * f.Format.nBlockAlign;
    f.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
    f.Samples.wValidBitsPerSample = 32;
    f.dwChannelMask = channels == 1 ? SPEAKER_FRONT_CENTER : (SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT);
    f.SubFormat = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
    return f;
}

// A WAV file read whole into 48 kHz mono float.
static bool ReadWavMono48k(const std::string& path, std::vector<float>& out) {
    std::ifstream f(Wide(path), std::ios::binary);
    if (!f) return false;
    std::vector<char> data((std::istreambuf_iterator<char>(f)), {});
    if (data.size() < 44 || memcmp(data.data(), "RIFF", 4) || memcmp(data.data() + 8, "WAVE", 4)) return false;
    int fmt_tag = 0, ch = 0, rate = 0, bits = 0;
    const char* pcm = nullptr;
    size_t pcm_len = 0;
    size_t pos = 12;
    while (pos + 8 <= data.size()) {
        uint32_t len;
        memcpy(&len, data.data() + pos + 4, 4);
        const char* body = data.data() + pos + 8;
        if (!memcmp(data.data() + pos, "fmt ", 4) && len >= 16) {
            uint16_t t, c, b;
            uint32_t r;
            memcpy(&t, body, 2);
            memcpy(&c, body + 2, 2);
            memcpy(&r, body + 4, 4);
            memcpy(&b, body + 14, 2);
            fmt_tag = t;
            ch = c;
            rate = (int)r;
            bits = b;
            if (t == 0xFFFE && len >= 40) {
                uint16_t sub;
                memcpy(&sub, body + 24, 2);
                fmt_tag = sub;
            }
        } else if (!memcmp(data.data() + pos, "data", 4)) {
            pcm = body;
            pcm_len = std::min((size_t)len, data.size() - (pos + 8));
        }
        pos += 8 + len + (len & 1);
    }
    if (!pcm || ch <= 0 || rate <= 0) return false;
    int bps = bits / 8;
    size_t frames = pcm_len / (bps * ch);
    std::vector<float> mono(frames);
    for (size_t i = 0; i < frames; i++) {
        float s = 0;
        for (int c = 0; c < ch; c++) {
            const char* p = pcm + (i * ch + c) * bps;
            float v = 0;
            if (fmt_tag == 3 && bits == 32) {
                memcpy(&v, p, 4);
            } else if (bits == 16) {
                int16_t x;
                memcpy(&x, p, 2);
                v = x / 32768.0f;
            } else if (bits == 24) {
                int32_t x = (uint8_t)p[0] | ((uint8_t)p[1] << 8) | ((int8_t)p[2] << 16);
                v = x / 8388608.0f;
            } else if (bits == 32) {
                int32_t x;
                memcpy(&x, p, 4);
                v = x / 2147483648.0f;
            }
            s += v;
        }
        mono[i] = s / ch;
    }
    if (rate == kRate) {
        out = std::move(mono);
        return true;
    }
    // Linear resample to 48 kHz (test input only).
    size_t n = (size_t)((double)frames * kRate / rate);
    out.resize(n);
    for (size_t i = 0; i < n; i++) {
        double x = (double)i * rate / kRate;
        size_t i0 = (size_t)x;
        double fr = x - i0;
        float a = mono[std::min(i0, frames - 1)], b = mono[std::min(i0 + 1, frames - 1)];
        out[i] = (float)(a + (b - a) * fr);
    }
    return true;
}

class WavWriter {
public:
    bool Open(const std::string& path) {
        f_ = _wfopen(Wide(path).c_str(), L"wb");
        if (!f_) return false;
        char hdr[44] = {};
        fwrite(hdr, 1, 44, f_);
        return true;
    }
    void Write(const float* stereo, int frames) {
        if (!f_) return;
        int16_t buf[kFrame * 2];
        for (int i = 0; i < frames * 2; i++) buf[i] = (int16_t)std::lrintf(std::min(1.0f, std::max(-1.0f, stereo[i])) * 32767.0f);
        fwrite(buf, 2, frames * 2, f_);
        bytes_ += frames * 4;
        if ((bytes_ / 4) % (kRate) < (uint32_t)frames) Finish(false);  // header kept current about every second
    }
    void Finish(bool close) {
        if (!f_) return;
        long here = ftell(f_);
        fseek(f_, 0, SEEK_SET);
        auto u32 = [&](uint32_t v) { fwrite(&v, 4, 1, f_); };
        auto u16 = [&](uint16_t v) { fwrite(&v, 2, 1, f_); };
        fwrite("RIFF", 1, 4, f_);
        u32(36 + bytes_);
        fwrite("WAVEfmt ", 1, 8, f_);
        u32(16);
        u16(1);
        u16(2);
        u32(kRate);
        u32(kRate * 4);
        u16(4);
        u16(16);
        fwrite("data", 1, 4, f_);
        u32(bytes_);
        fseek(f_, here, SEEK_SET);
        fflush(f_);
        if (close) {
            fclose(f_);
            f_ = nullptr;
        }
    }

private:
    FILE* f_ = nullptr;
    uint32_t bytes_ = 0;
};

// A 10 ms pacer on a high-resolution waitable timer, scheduled on absolute times so it doesn't drift.
class Pacer {
public:
    Pacer() {
        timer_ = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
        QueryPerformanceFrequency(&freq_);
        QueryPerformanceCounter(&start_);
    }
    ~Pacer() {
        if (timer_) CloseHandle(timer_);
    }
    // Waits until tick n's time (n * 10 ms after start).
    void WaitTick(int64_t n) {
        LARGE_INTEGER now;
        QueryPerformanceCounter(&now);
        int64_t due = start_.QuadPart + n * freq_.QuadPart / 100;
        int64_t wait = due - now.QuadPart;
        if (wait <= 0) return;
        LARGE_INTEGER rel;
        rel.QuadPart = -(wait * 10000000 / freq_.QuadPart);  // 100 ns units, negative = relative
        if (timer_ && SetWaitableTimerEx(timer_, &rel, 0, nullptr, nullptr, nullptr, 0))
            WaitForSingleObject(timer_, 100);
        else
            Sleep((DWORD)(wait * 1000 / freq_.QuadPart));
    }

private:
    HANDLE timer_ = nullptr;
    LARGE_INTEGER freq_, start_;
};

struct Io {
    std::thread capture, render;
    std::atomic<bool> stop{false};
    std::string capture_spec, render_spec;
};
static Io* io = nullptr;
static std::mutex io_mu;

static void Mmcss() {
    DWORD task = 0;
    AvSetMmThreadCharacteristicsW(L"Pro Audio", &task);
}

// --- capture ---

static void CaptureFileLoop(Io* s, const std::string& path, bool loop) {
    std::vector<float> pcm;
    if (!ReadWavMono48k(path, pcm)) {
        Log(3, "capture: can't read WAV " + path);
        Emit(0, SV_EV_DEVICE, "failed", path.c_str(), 0);
    } else {
        Emit(0, SV_EV_DEVICE, "open", path.c_str(), 0);
    }
    Pacer pacer;
    g_capture_file_t0.store(UnixMs());
    size_t pos = 0;
    float frame[kFrame];
    g_capture_delay_ms.store(0);
    for (int64_t n = 1; !s->stop.load(); n++) {
        pacer.WaitTick(n);
        for (int i = 0; i < kFrame; i++) {
            if (pos >= pcm.size() && loop && !pcm.empty()) pos = 0;
            frame[i] = pos < pcm.size() ? pcm[pos++] : 0.0f;
        }
        CaptureFrame(frame);
    }
}

static void CaptureNullLoop(Io* s) {
    Pacer pacer;
    float frame[kFrame] = {};
    for (int64_t n = 1; !s->stop.load(); n++) {
        pacer.WaitTick(n);
        CaptureFrame(frame);
    }
}

// One open-run of a WASAPI capture device. Returns false if the device went away or couldn't open.
static bool CaptureWasapiRun(Io* s, IMMDeviceEnumerator* en, const std::string& spec) {
    ComPtr<IMMDevice> dev;
    std::string name;
    if (!OpenDevice(en, true, spec, dev.put(), &name)) {
        Log(2, "capture: no device " + spec);
        return false;
    }
    ComPtr<IAudioClient> client;
    if (FAILED(dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, (void**)client.put()))) {
        Log(3, "capture: can't activate " + name);
        return false;
    }
    WAVEFORMATEXTENSIBLE fmt = FloatFormat(1);
    DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                  AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
    HRESULT hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 200000, 0, (WAVEFORMATEX*)&fmt, nullptr);
    if (FAILED(hr)) {
        char b[96];
        snprintf(b, sizeof b, "capture: Initialize failed 0x%08lx on ", (unsigned long)hr);
        Log(3, b + name);
        return false;
    }
    HANDLE ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    client->SetEventHandle(ev);
    ComPtr<IAudioCaptureClient> cap;
    client->GetService(__uuidof(IAudioCaptureClient), (void**)cap.put());
    REFERENCE_TIME lat = 0;
    client->GetStreamLatency(&lat);
    hr = client->Start();
    if (FAILED(hr)) {
        Log(3, "capture: can't start " + name);
        CloseHandle(ev);
        return false;
    }
    Log(1, "capture: " + name);
    Emit(0, SV_EV_DEVICE, "open", name.c_str(), 0);

    std::vector<float> fifo;
    fifo.reserve(kFrame * 8);
    bool lost = false, silent = false;
    int timeouts = 0;
    while (!s->stop.load()) {
        DWORD w = WaitForSingleObject(ev, 200);
        if (w != WAIT_OBJECT_0) {
            // Link's virtual mic opens but delivers nothing while the headset is off
            if (++timeouts == 5 && !silent) {
                silent = true;
                Log(2, "capture: no audio from " + name);
                Emit(0, SV_EV_DEVICE, "silent", name.c_str(), 0);
            }
            continue;
        }
        timeouts = 0;
        if (silent) {
            silent = false;
            Emit(0, SV_EV_DEVICE, "open", name.c_str(), 0);
        }
        for (;;) {
            UINT32 packet = 0;
            hr = cap->GetNextPacketSize(&packet);
            if (FAILED(hr)) {
                lost = true;
                break;
            }
            if (packet == 0) break;
            BYTE* data = nullptr;
            UINT32 frames = 0;
            DWORD bf = 0;
            hr = cap->GetBuffer(&data, &frames, &bf, nullptr, nullptr);
            if (FAILED(hr)) {
                lost = true;
                break;
            }
            if (bf & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) g_capture_glitches++;
            const float* f = (const float*)data;
            for (UINT32 i = 0; i < frames; i++) fifo.push_back((bf & AUDCLNT_BUFFERFLAGS_SILENT) ? 0.0f : f[i]);
            cap->ReleaseBuffer(frames);
        }
        if (lost) break;
        size_t off = 0;
        while (fifo.size() - off >= (size_t)kFrame) {
            g_capture_delay_ms.store((int)(lat / 10000) + (int)((fifo.size() - off) * 1000 / kRate));
            CaptureFrame(fifo.data() + off);
            off += kFrame;
        }
        fifo.erase(fifo.begin(), fifo.begin() + off);
    }
    client->Stop();
    CloseHandle(ev);
    if (lost) {
        Log(2, "capture: device lost: " + name);
        Emit(0, SV_EV_DEVICE, "lost", name.c_str(), 0);
        return false;
    }
    return true;
}

static void CaptureWasapiLoop(Io* s, std::string spec) {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    Mmcss();
    ComPtr<IMMDeviceEnumerator> en;
    CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, __uuidof(IMMDeviceEnumerator), (void**)en.put());
    bool reported = false;
    while (!s->stop.load()) {
        if (en && CaptureWasapiRun(s, en.p, spec)) break;
        if (!reported) {
            Emit(0, SV_EV_DEVICE, "failed", spec.c_str(), 0);
            reported = true;
        }
        // Retry the same device, then fall back to the default (Link's devices vanish when it drops).
        for (int i = 0; i < 10 && !s->stop.load(); i++) Sleep(100);
        if (!spec.empty() && en) {
            ComPtr<IMMDevice> d;
            std::string n;
            if (!OpenDevice(en.p, true, spec, d.put(), &n)) spec.clear();
        }
    }
    CoUninitialize();
}

// --- render ---

static void RenderFileLoop(Io* s, const std::string& path) {
    WavWriter w;
    if (!w.Open(path)) Log(3, "render: can't write " + path);
    Pacer pacer;
    g_render_file_t0.store(UnixMs());
    float frame[kFrame * 2];
    g_render_delay_ms.store(0);
    for (int64_t n = 1; !s->stop.load(); n++) {
        pacer.WaitTick(n);
        RenderFrame(frame);
        w.Write(frame, kFrame);
    }
    w.Finish(true);
}

static void RenderNullLoop(Io* s) {
    Pacer pacer;
    float frame[kFrame * 2];
    for (int64_t n = 1; !s->stop.load(); n++) {
        pacer.WaitTick(n);
        RenderFrame(frame);
    }
}

static bool RenderWasapiRun(Io* s, IMMDeviceEnumerator* en, const std::string& spec) {
    ComPtr<IMMDevice> dev;
    std::string name;
    if (!OpenDevice(en, false, spec, dev.put(), &name)) return false;
    ComPtr<IAudioClient> client;
    if (FAILED(dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, (void**)client.put()))) return false;
    WAVEFORMATEXTENSIBLE fmt = FloatFormat(2);
    DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                  AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
    HRESULT hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 400000, 0, (WAVEFORMATEX*)&fmt, nullptr);
    if (FAILED(hr)) {
        char b[96];
        snprintf(b, sizeof b, "render: Initialize failed 0x%08lx on ", (unsigned long)hr);
        Log(3, b + name);
        return false;
    }
    HANDLE ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    client->SetEventHandle(ev);
    UINT32 buffer_frames = 0;
    client->GetBufferSize(&buffer_frames);
    ComPtr<IAudioRenderClient> rc;
    client->GetService(__uuidof(IAudioRenderClient), (void**)rc.put());
    REFERENCE_TIME lat = 0;
    client->GetStreamLatency(&lat);

    // Keep ~30 ms queued in the device: enough to ride out a late wakeup beside the renderer, little enough for
    // voice.
    const UINT32 target = std::min<UINT32>(buffer_frames, kFrame * 3);
    std::deque<float> fifo;
    float frame[kFrame * 2];
    auto fill = [&](UINT32 want) -> bool {
        BYTE* data = nullptr;
        if (FAILED(rc->GetBuffer(want, &data))) return false;
        float* out = (float*)data;
        for (UINT32 i = 0; i < want; i++) {
            if (fifo.empty()) {
                RenderFrame(frame);
                fifo.insert(fifo.end(), frame, frame + kFrame * 2);
            }
            out[2 * i] = fifo.front();
            fifo.pop_front();
            out[2 * i + 1] = fifo.front();
            fifo.pop_front();
        }
        rc->ReleaseBuffer(want, 0);
        return true;
    };
    fill(target);
    client->Start();
    Log(1, "render: " + name);
    Emit(0, SV_EV_DEVICE, "open", name.c_str(), 1);
    bool lost = false;
    while (!s->stop.load()) {
        DWORD w = WaitForSingleObject(ev, 200);
        if (w != WAIT_OBJECT_0) continue;
        UINT32 padding = 0;
        if (FAILED(client->GetCurrentPadding(&padding))) {
            lost = true;
            break;
        }
        if (padding == 0) g_render_glitches++;
        g_render_delay_ms.store((int)(lat / 10000) + (int)((padding + fifo.size() / 2) * 1000 / kRate));
        if (padding < target && !fill(target - padding)) {
            lost = true;
            break;
        }
    }
    client->Stop();
    CloseHandle(ev);
    if (lost) {
        Log(2, "render: device lost: " + name);
        Emit(0, SV_EV_DEVICE, "lost", name.c_str(), 1);
        return false;
    }
    return true;
}

static void RenderWasapiLoop(Io* s, std::string spec) {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    Mmcss();
    ComPtr<IMMDeviceEnumerator> en;
    CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, __uuidof(IMMDeviceEnumerator), (void**)en.put());
    bool reported = false;
    while (!s->stop.load()) {
        if (en && RenderWasapiRun(s, en.p, spec)) break;
        if (!reported) {
            Emit(0, SV_EV_DEVICE, "failed", spec.c_str(), 1);
            reported = true;
        }
        // Keep the engine's playout moving while there's no device, so its buffers don't pile up.
        Pacer pacer;
        float frame[kFrame * 2];
        for (int i = 1; i <= 100 && !s->stop.load(); i++) {
            pacer.WaitTick(i);
            RenderFrame(frame);
        }
        if (!spec.empty() && en) {
            ComPtr<IMMDevice> d;
            std::string n;
            if (!OpenDevice(en.p, false, spec, d.put(), &n)) spec.clear();
        }
    }
    CoUninitialize();
}

bool AudioStart(const std::string& capture, const std::string& render) {
    AudioStop();
    std::lock_guard<std::mutex> lk(io_mu);
    io = new Io();
    io->capture_spec = capture;
    io->render_spec = render;
    Io* s = io;
    if (capture == "none")
        s->capture = std::thread(CaptureNullLoop, s);
    else if (capture.rfind("file:", 0) == 0)
        s->capture = std::thread(CaptureFileLoop, s, capture.substr(5), false);
    else if (capture.rfind("loop:", 0) == 0)
        s->capture = std::thread(CaptureFileLoop, s, capture.substr(5), true);
    else
        s->capture = std::thread(CaptureWasapiLoop, s, capture);

    if (render == "none")
        s->render = std::thread(RenderNullLoop, s);
    else if (render.rfind("file:", 0) == 0)
        s->render = std::thread(RenderFileLoop, s, render.substr(5));
    else
        s->render = std::thread(RenderWasapiLoop, s, render);
    return true;
}

void AudioStop() {
    std::lock_guard<std::mutex> lk(io_mu);
    if (!io) return;
    io->stop.store(true);
    if (io->capture.joinable()) io->capture.join();
    if (io->render.joinable()) io->render.join();
    delete io;
    io = nullptr;
}

}  // namespace sv
