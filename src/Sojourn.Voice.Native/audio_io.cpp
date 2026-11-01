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

enum { kCapture = 0, kRender = 1 };

// Tests (sv_test_device): a stream lost as if AUDCLNT_E_DEVICE_INVALIDATED came (its event stopping), devices gone
// (all of a direction's, or those whose name has a part), and what the default resolves to.
static std::atomic<bool> g_test_lose[2], g_test_gone[2];
static std::mutex g_test_mu;
static std::string g_test_gone_name[2], g_test_default[2];

static bool TestGone(int dir, const std::string& name) {
    if (!g_test_gone[dir].load()) return false;
    std::lock_guard<std::mutex> lk(g_test_mu);
    return g_test_gone_name[dir].empty() || name.find(g_test_gone_name[dir]) != std::string::npos;
}

static std::string DeviceId(IMMDevice* d) {
    std::string r;
    LPWSTR id = nullptr;
    if (d && SUCCEEDED(d->GetId(&id))) {
        r = Utf8(id);
        CoTaskMemFree(id);
    }
    return r;
}

// Resolves a device spec to an active IMMDevice: "" the default device, "comms" the default communications device,
// an endpoint id, or "name:substring". False if it isn't there (an id names unplugged devices too: those count as
// not there).
static bool OpenDevice(IMMDeviceEnumerator* en, bool capture, const std::string& spec, IMMDevice** out,
                       std::string* name) {
    int dir = capture ? kCapture : kRender;
    if (spec.empty()) {
        std::string fake;
        {
            std::lock_guard<std::mutex> lk(g_test_mu);
            fake = g_test_default[dir];
        }
        if (!fake.empty()) return OpenDevice(en, capture, fake, out, name);
    }
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
    DWORD state = 0;
    if (FAILED((*out)->GetState(&state)) || state != DEVICE_STATE_ACTIVE) {
        (*out)->Release();
        *out = nullptr;
        return false;
    }
    ComPtr<IPropertyStore> props;
    if (SUCCEEDED((*out)->OpenPropertyStore(STGM_READ, props.put()))) {
        PROPVARIANT v;
        PropVariantInit(&v);
        if (SUCCEEDED(props->GetValue(PKEY_Device_FriendlyName, &v)) && v.vt == VT_LPWSTR) *name = Utf8(v.pwszVal);
        PropVariantClear(&v);
    }
    if (TestGone(dir, *name)) {
        (*out)->Release();
        *out = nullptr;
        return false;
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

// --- the threads and device changes ---
//
// Each direction has one thread for the engine's life; sv_audio_start only hands it a new spec, so its caller never
// waits on a device. On WASAPI the thread follows Windows' devices: the default ("" or "comms") is followed when
// Windows moves it; a device asked for by id or name is used while it's there and the default meanwhile; a device
// that goes (unplugged, Link dropping, its format changed) is reopened on what's there now; with nothing there the
// engine keeps moving on paced silence, looking again on each change Windows reports and once a second. A lost
// device's event usually stops firing, so every wait times out and polls the stream.

// Set (auto-reset) by Windows' endpoint notifications, a new spec, stop, and the test hook.
static HANDLE g_wake[2] = {nullptr, nullptr};
static void Wake(int dir) {
    if (g_wake[dir]) SetEvent(g_wake[dir]);
}

class Notifier : public IMMNotificationClient {
public:
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&ref_); }
    ULONG STDMETHODCALLTYPE Release() override {
        ULONG r = InterlockedDecrement(&ref_);
        if (r == 0) delete this;
        return r;
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override {
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IMMNotificationClient)) {
            AddRef();
            *out = static_cast<IMMNotificationClient*>(this);
            return S_OK;
        }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    // On Windows' own threads, which mustn't block: the audio threads only get woken, and look for themselves.
    HRESULT STDMETHODCALLTYPE OnDeviceStateChanged(LPCWSTR, DWORD) override { return Both(); }
    HRESULT STDMETHODCALLTYPE OnDeviceAdded(LPCWSTR) override { return Both(); }
    HRESULT STDMETHODCALLTYPE OnDeviceRemoved(LPCWSTR) override { return Both(); }
    HRESULT STDMETHODCALLTYPE OnDefaultDeviceChanged(EDataFlow flow, ERole, LPCWSTR) override {
        if (flow != eRender) Wake(kCapture);
        if (flow != eCapture) Wake(kRender);
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE OnPropertyValueChanged(LPCWSTR, const PROPERTYKEY) override { return S_OK; }

private:
    HRESULT Both() {
        Wake(kCapture);
        Wake(kRender);
        return S_OK;
    }
    LONG ref_ = 1;
};

struct Io {
    std::thread th[2];
    std::atomic<bool> stop{false};
    std::mutex mu;
    std::string spec[2];
    std::atomic<uint64_t> gen[2] = {0, 0};
};
static Io* io = nullptr;
static std::mutex io_mu;

// What a loop runs for: until stop, or a new spec for its direction.
struct Want {
    Io* s;
    int dir;
    uint64_t gen;
    bool changed() const { return s->stop.load() || s->gen[dir].load() != gen; }
};

static bool FollowsDefault(const std::string& spec) { return spec.empty() || spec == "comms"; }

// No device: the engine keeps moving on paced silence (render: its playout runs on, so nothing piles up and nothing
// stale plays later; capture: silence, as a muted mic) until Windows reports a change or ms pass.
static void Idle(const Want& w, int ms) {
    Pacer pacer;
    float frame[kFrame * 2];
    static const float zeros[kFrame] = {};
    for (int i = 1; i <= ms / 10 && !w.changed(); i++) {
        pacer.WaitTick(i);
        if (w.dir == kRender)
            RenderFrame(frame);
        else
            CaptureFrame(zeros);
        if (WaitForSingleObject(g_wake[w.dir], 0) == WAIT_OBJECT_0) break;
    }
}

enum class Ended { Changed, Reopen, Lost, Missing, Carry };

// After a wake: whether the open device is still there, and still the one to use (the default it follows moved, or
// the device asked for is back while on the default).
static Ended Recheck(IMMDeviceEnumerator* en, int dir, IMMDevice* dev, const std::string& id, const std::string& name,
                     const std::string& wanted, const std::string& spec) {
    bool capture = dir == kCapture;
    DWORD state = 0;
    if (FAILED(dev->GetState(&state)) || state != DEVICE_STATE_ACTIVE || TestGone(dir, name)) return Ended::Lost;
    ComPtr<IMMDevice> d;
    std::string n;
    if (spec != wanted && OpenDevice(en, capture, wanted, d.put(), &n)) return Ended::Reopen;
    if (FollowsDefault(spec) && OpenDevice(en, capture, spec, d.put(), &n) && DeviceId(d.p) != id) return Ended::Reopen;
    return Ended::Carry;
}

// Waits for the stream's event or a wake, 200 ms at most; a lost stream (tests) waits on the wake alone, as a real
// lost device's event stops. Returns 0 the stream's event, 1 a wake, -1 a timeout.
static int WaitStream(HANDLE ev, int dir, bool dead) {
    HANDLE hs[2] = {ev, g_wake[dir]};
    DWORD r = dead ? (WaitForSingleObject(hs[1], 200) == WAIT_OBJECT_0 ? WAIT_OBJECT_0 + 1 : WAIT_TIMEOUT)
                   : WaitForMultipleObjects(2, hs, FALSE, 200);
    return r == WAIT_OBJECT_0 ? 0 : r == WAIT_OBJECT_0 + 1 ? 1 : -1;
}

static std::string Describe(const std::string& spec) { return spec.empty() ? "the default device" : spec; }

static void ReportMissing(bool& reported, int dir, const std::string& what) {
    if (reported) return;
    reported = true;
    Log(2, std::string(dir == kCapture ? "capture" : "render") + ": no device (" + what + ")");
    Emit(0, SV_EV_DEVICE, "failed", what.c_str(), dir);
}

// --- capture ---

static void CaptureFileLoop(const Want& w, const std::string& path, bool loop) {
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
    for (int64_t n = 1; !w.changed(); n++) {
        pacer.WaitTick(n);
        for (int i = 0; i < kFrame; i++) {
            if (pos >= pcm.size() && loop && !pcm.empty()) pos = 0;
            frame[i] = pos < pcm.size() ? pcm[pos++] : 0.0f;
        }
        CaptureFrame(frame);
    }
}

static void CaptureNullLoop(const Want& w) {
    Pacer pacer;
    float frame[kFrame] = {};
    for (int64_t n = 1; !w.changed(); n++) {
        pacer.WaitTick(n);
        CaptureFrame(frame);
    }
}

// One open-run of a WASAPI capture device, until it goes, gives way, or the spec changes.
static Ended CaptureWasapiRun(const Want& w, IMMDeviceEnumerator* en, const std::string& wanted,
                              const std::string& spec, bool& reported) {
    ComPtr<IMMDevice> dev;
    std::string name;
    if (!OpenDevice(en, true, spec, dev.put(), &name)) {
        ReportMissing(reported, kCapture, Describe(spec));
        return Ended::Missing;
    }
    std::string id = DeviceId(dev.p);
    ComPtr<IAudioClient> client;
    if (FAILED(dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, (void**)client.put()))) {
        ReportMissing(reported, kCapture, name + ": can't activate");
        return Ended::Missing;
    }
    WAVEFORMATEXTENSIBLE fmt = FloatFormat(1);
    DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                  AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
    HRESULT hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 200000, 0, (WAVEFORMATEX*)&fmt, nullptr);
    if (FAILED(hr)) {
        char b[64];
        snprintf(b, sizeof b, ": Initialize failed 0x%08lx", (unsigned long)hr);
        ReportMissing(reported, kCapture, name + b);
        return Ended::Missing;
    }
    HANDLE ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    client->SetEventHandle(ev);
    ComPtr<IAudioCaptureClient> cap;
    client->GetService(__uuidof(IAudioCaptureClient), (void**)cap.put());
    REFERENCE_TIME lat = 0;
    client->GetStreamLatency(&lat);
    if (!cap || FAILED(client->Start())) {
        CloseHandle(ev);
        ReportMissing(reported, kCapture, name + ": can't start");
        return Ended::Missing;
    }
    reported = false;
    Log(1, "capture: " + name);
    Emit(0, SV_EV_DEVICE, "open", name.c_str(), 0);

    std::vector<float> fifo;
    fifo.reserve(kFrame * 8);
    bool silent = false, dead = false;
    int timeouts = 0;
    Ended end = Ended::Changed;
    for (;;) {
        if (g_test_lose[kCapture].exchange(false)) dead = true;
        int r = WaitStream(ev, kCapture, dead);
        if (w.changed()) break;
        if (r == 1) {
            Ended e = Recheck(en, kCapture, dev.p, id, name, wanted, spec);
            if (e != Ended::Carry) {
                end = e;
                break;
            }
        }
        if (r < 0) {
            // Link's virtual mic opens but delivers nothing while the headset is off
            if (++timeouts == 5 && !silent) {
                silent = true;
                Log(2, "capture: no audio from " + name);
                Emit(0, SV_EV_DEVICE, "silent", name.c_str(), 0);
            }
        } else if (r == 0) {
            timeouts = 0;
            if (silent) {
                silent = false;
                Emit(0, SV_EV_DEVICE, "open", name.c_str(), 0);
            }
        }
        // read what's there; after a timeout this is the poll that finds a lost device
        bool lost = false;
        for (;;) {
            UINT32 packet = 0;
            hr = dead ? AUDCLNT_E_DEVICE_INVALIDATED : cap->GetNextPacketSize(&packet);
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
        if (lost) {
            end = Ended::Lost;
            break;
        }
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
    if (end == Ended::Lost) {
        reported = true;   // "lost" says it; no "failed" after it while nothing's there
        Log(2, "capture: device lost: " + name);
        Emit(0, SV_EV_DEVICE, "lost", name.c_str(), 0);
    } else if (end == Ended::Reopen) {
        Log(1, "capture: leaving " + name + " for " + (spec != wanted ? Describe(wanted) : "the new default"));
    }
    return end;
}

// --- render ---

static void RenderFileLoop(const Want& w, const std::string& path) {
    WavWriter wr;
    if (!wr.Open(path)) Log(3, "render: can't write " + path);
    Pacer pacer;
    g_render_file_t0.store(UnixMs());
    float frame[kFrame * 2];
    g_render_delay_ms.store(0);
    for (int64_t n = 1; !w.changed(); n++) {
        pacer.WaitTick(n);
        RenderFrame(frame);
        wr.Write(frame, kFrame);
    }
    wr.Finish(true);
}

static void RenderNullLoop(const Want& w) {
    Pacer pacer;
    float frame[kFrame * 2];
    for (int64_t n = 1; !w.changed(); n++) {
        pacer.WaitTick(n);
        RenderFrame(frame);
    }
}

static Ended RenderWasapiRun(const Want& w, IMMDeviceEnumerator* en, const std::string& wanted,
                             const std::string& spec, bool& reported) {
    ComPtr<IMMDevice> dev;
    std::string name;
    if (!OpenDevice(en, false, spec, dev.put(), &name)) {
        ReportMissing(reported, kRender, Describe(spec));
        return Ended::Missing;
    }
    std::string id = DeviceId(dev.p);
    ComPtr<IAudioClient> client;
    if (FAILED(dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, (void**)client.put()))) {
        ReportMissing(reported, kRender, name + ": can't activate");
        return Ended::Missing;
    }
    WAVEFORMATEXTENSIBLE fmt = FloatFormat(2);
    DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                  AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
    HRESULT hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 400000, 0, (WAVEFORMATEX*)&fmt, nullptr);
    if (FAILED(hr)) {
        char b[64];
        snprintf(b, sizeof b, ": Initialize failed 0x%08lx", (unsigned long)hr);
        ReportMissing(reported, kRender, name + b);
        return Ended::Missing;
    }
    HANDLE ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    client->SetEventHandle(ev);
    UINT32 buffer_frames = 0;
    client->GetBufferSize(&buffer_frames);
    ComPtr<IAudioRenderClient> rc;
    client->GetService(__uuidof(IAudioRenderClient), (void**)rc.put());
    REFERENCE_TIME lat = 0;
    client->GetStreamLatency(&lat);
    if (!rc) {
        CloseHandle(ev);
        ReportMissing(reported, kRender, name + ": no render client");
        return Ended::Missing;
    }

    // Keep ~30 ms queued in the device: enough to ride out a late wakeup beside the renderer, little enough for
    // voice.
    const UINT32 target = std::min<UINT32>(buffer_frames, kFrame * 3);
    std::deque<float> fifo;
    float frame[kFrame * 2];
    ULONGLONG driven = GetTickCount64();   // when the engine's playout last moved
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
        driven = GetTickCount64();
        return true;
    };
    fill(target);
    if (FAILED(client->Start())) {
        CloseHandle(ev);
        ReportMissing(reported, kRender, name + ": can't start");
        return Ended::Missing;
    }
    reported = false;
    Log(1, "render: " + name);
    Emit(0, SV_EV_DEVICE, "open", name.c_str(), 1);
    bool dead = false, stalled = false;
    int timeouts = 0;
    Ended end = Ended::Changed;
    for (;;) {
        if (g_test_lose[kRender].exchange(false)) dead = true;
        int r = WaitStream(ev, kRender, dead);
        if (w.changed()) break;
        if (r == 1) {
            Ended e = Recheck(en, kRender, dev.p, id, name, wanted, spec);
            if (e != Ended::Carry) {
                end = e;
                break;
            }
        }
        // after a timeout this is the poll that finds a lost device
        UINT32 padding = 0;
        if (dead || FAILED(client->GetCurrentPadding(&padding))) {
            end = Ended::Lost;
            break;
        }
        if (r == 0) {
            timeouts = 0;
            if (stalled) {
                stalled = false;
                Emit(0, SV_EV_DEVICE, "open", name.c_str(), 1);
            }
            if (padding == 0) g_render_glitches++;
        } else if (r < 0 && ++timeouts == 5 && !stalled) {
            stalled = true;
            Log(2, "render: " + name + " takes no audio");
            Emit(0, SV_EV_DEVICE, "silent", name.c_str(), 1);
        }
        g_render_delay_ms.store((int)(lat / 10000) + (int)((padding + fifo.size() / 2) * 1000 / kRate));
        if (padding < target && !fill(target - padding)) {
            end = Ended::Lost;
            break;
        }
        // a device that's there but takes nothing: the engine's playout runs on regardless, discarded
        if (stalled) {
            ULONGLONG now = GetTickCount64();
            int owed = (int)std::min<ULONGLONG>((now - driven) / 10, 50);
            for (int i = 0; i < owed; i++) RenderFrame(frame);
            driven += (ULONGLONG)owed * 10;
        }
    }
    client->Stop();
    CloseHandle(ev);
    if (end == Ended::Lost) {
        reported = true;
        Log(2, "render: device lost: " + name);
        Emit(0, SV_EV_DEVICE, "lost", name.c_str(), 1);
    } else if (end == Ended::Reopen) {
        Log(1, "render: leaving " + name + " for " + (spec != wanted ? Describe(wanted) : "the new default"));
    }
    return end;
}

// A WASAPI direction, until its spec changes: the device asked for while it's there, else the default; reopened as
// devices come and go.
static void WasapiLoop(const Want& w, IMMDeviceEnumerator* en, const std::string& wanted) {
    bool capture = w.dir == kCapture;
    const char* dirname = capture ? "capture" : "render";
    DWORD task = 0;
    HANDLE mm = AvSetMmThreadCharacteristicsW(L"Pro Audio", &task);
    bool reported = false, away = false;
    while (!w.changed()) {
        std::string spec = wanted;
        if (!FollowsDefault(wanted)) {
            ComPtr<IMMDevice> d;
            std::string n;
            bool there = OpenDevice(en, capture, wanted, d.put(), &n);
            if (!there) spec.clear();
            if (!there && !away) Log(2, std::string(dirname) + ": " + wanted + " isn't there: the default meanwhile");
            away = !there;
        }
        ULONGLONG t0 = GetTickCount64();
        Ended e = capture ? CaptureWasapiRun(w, en, wanted, spec, reported) : RenderWasapiRun(w, en, wanted, spec, reported);
        if (e == Ended::Changed) break;
        if (e == Ended::Reopen) continue;
        // lost after a good run: straight onto what's there now (Windows has usually moved the default already);
        // lost at once, or nothing there: wait for a change, a second at most
        if (e == Ended::Lost && GetTickCount64() - t0 > 1000) continue;
        Idle(w, 1000);
    }
    if (mm) AvRevertMmThreadCharacteristics(mm);
}

static void DirThread(Io* s, int dir) {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    ComPtr<IMMDeviceEnumerator> en;
    Notifier* note = nullptr;
    while (!s->stop.load()) {
        std::string spec;
        uint64_t gen;
        {
            std::lock_guard<std::mutex> lk(s->mu);
            spec = s->spec[dir];
            gen = s->gen[dir].load();
        }
        Want w{s, dir, gen};
        bool loop = spec.rfind("loop:", 0) == 0;
        if (spec == "none") {
            dir == kCapture ? CaptureNullLoop(w) : RenderNullLoop(w);
        } else if (spec.rfind("file:", 0) == 0 || (loop && dir == kCapture)) {
            dir == kCapture ? CaptureFileLoop(w, spec.substr(5), loop) : RenderFileLoop(w, spec.substr(5));
        } else {
            if (!en && SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                                  __uuidof(IMMDeviceEnumerator), (void**)en.put()))) {
                note = new Notifier();
                if (FAILED(en->RegisterEndpointNotificationCallback(note))) {
                    Log(2, "audio: no device notifications; devices are looked for once a second");
                    note->Release();
                    note = nullptr;
                }
            }
            if (en)
                WasapiLoop(w, en.p, spec);
            else
                Idle(w, 1000);
        }
    }
    if (note) {
        en->UnregisterEndpointNotificationCallback(note);
        note->Release();
    }
    en.reset();
    CoUninitialize();
}

bool AudioStart(const std::string& capture, const std::string& render) {
    std::lock_guard<std::mutex> lk(io_mu);
    for (auto& h : g_wake)
        if (!h) h = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!io) {
        io = new Io();
        io->spec[kCapture] = capture;
        io->spec[kRender] = render;
        io->th[kCapture] = std::thread(DirThread, io, kCapture);
        io->th[kRender] = std::thread(DirThread, io, kRender);
        return true;
    }
    // the threads carry on: a direction whose spec changed reopens, the other isn't touched
    const std::string specs[2] = {capture, render};
    for (int d = 0; d < 2; d++) {
        {
            std::lock_guard<std::mutex> lk2(io->mu);
            if (io->spec[d] == specs[d]) continue;
            io->spec[d] = specs[d];
            io->gen[d]++;
        }
        Wake(d);
    }
    return true;
}

void AudioStop() {
    std::lock_guard<std::mutex> lk(io_mu);
    if (!io) return;
    io->stop.store(true);
    Wake(kCapture);
    Wake(kRender);
    // The threads see stop within 200 ms; one stuck in a driver's call is left behind (with its Io) rather than
    // holding up the caller.
    bool stuck = false;
    for (auto& t : io->th) {
        if (!t.joinable()) continue;
        if (WaitForSingleObject((HANDLE)t.native_handle(), 3000) == WAIT_OBJECT_0) {
            t.join();
        } else {
            t.detach();
            stuck = true;
        }
    }
    if (stuck)
        Log(3, "audio: a device thread didn't stop within 3 s; left behind");
    else
        delete io;
    io = nullptr;
}

void AudioTest(int dir, int what, const std::string& spec) {
    if (dir != kCapture && dir != kRender) return;
    {
        std::lock_guard<std::mutex> lk(g_test_mu);
        switch (what) {
            case 0: g_test_lose[dir].store(true); break;
            case 1:
                g_test_gone_name[dir] = spec.rfind("name:", 0) == 0 ? spec.substr(5) : spec;
                g_test_gone[dir].store(true);
                break;
            case 2: g_test_gone[dir].store(false); break;
            case 3: g_test_default[dir] = spec; break;
        }
    }
    Wake(dir);
}

}  // namespace sv
