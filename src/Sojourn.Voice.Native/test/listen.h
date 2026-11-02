// What actually reaches an output device: WASAPI loopback capture of a render endpoint (the mix Windows sends it, from
// every app), in 50 ms blocks, each with its RMS and the level of one test frequency (Goertzel), both in dBFS. The tone
// is how a test tells its own sound from whatever else is playing there. Tests only (sv_loopback listen / switch).
#pragma once
#define NOMINMAX
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#include <ksmedia.h>

#include <atomic>
#include <cmath>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

struct ListenBlock {
    DWORD t;       // GetTickCount at the block's end
    float rms_db;  // everything on the device
    float tone_db; // the test frequency's amplitude
};

class Listener {
public:
    // want: a part of the endpoint's friendly name (the first active render endpoint with it)
    Listener(std::string want, double freq) : want_(std::move(want)), freq_(freq) {}
    ~Listener() { Stop(); }

    bool Start() {
        th_ = std::thread([this] { Run(); });
        for (int i = 0; i < 300 && !ready_.load(); i++) Sleep(10);
        return ok_.load();
    }
    void Stop() {
        stop_ = true;
        if (th_.joinable()) th_.join();
    }
    std::string Name() const { return name_; }
    std::string Format() const { return format_; }
    std::string Error() const { return error_; }
    std::vector<ListenBlock> Blocks() {
        std::lock_guard<std::mutex> lk(mu_);
        return blocks_;
    }
    // the loudest tone among blocks ending in [from, to] (GetTickCount), -200 if none
    float ToneMax(DWORD from, DWORD to) {
        float m = -200;
        for (auto& b : Blocks())
            if (b.t >= from && b.t <= to) m = std::max(m, b.tone_db);
        return m;
    }
    // the first block ending after `from` whose tone is over db (0 if none)
    DWORD FirstTone(DWORD from, float db) {
        for (auto& b : Blocks())
            if (b.t > from && b.tone_db > db) return b.t;
        return 0;
    }
    // the last block's end with the tone over db, before `to` (0 if none)
    DWORD LastTone(DWORD to, float db) {
        DWORD last = 0;
        for (auto& b : Blocks())
            if (b.t <= to && b.tone_db > db) last = b.t;
        return last;
    }

private:
    static std::string Utf8(const wchar_t* w) {
        int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
        std::string s(n > 0 ? n - 1 : 0, '\0');
        if (n > 1) WideCharToMultiByte(CP_UTF8, 0, w, -1, s.data(), n, nullptr, nullptr);
        return s;
    }

    void Fail(const std::string& e) {
        error_ = e;
        ready_ = true;
    }

    void Run() {
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        Capture();
        CoUninitialize();
        ready_ = true;
    }

    void Capture() {
        IMMDeviceEnumerator* en = nullptr;
        if (FAILED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, __uuidof(IMMDeviceEnumerator),
                                    (void**)&en)))
            return Fail("no device enumerator");
        IMMDeviceCollection* col = nullptr;
        IMMDevice* dev = nullptr;
        en->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &col);
        UINT n = 0;
        if (col) col->GetCount(&n);
        for (UINT i = 0; i < n && !dev; i++) {
            IMMDevice* d = nullptr;
            if (FAILED(col->Item(i, &d))) continue;
            IPropertyStore* ps = nullptr;
            std::string nm;
            if (SUCCEEDED(d->OpenPropertyStore(STGM_READ, &ps))) {
                PROPVARIANT v;
                PropVariantInit(&v);
                if (SUCCEEDED(ps->GetValue(PKEY_Device_FriendlyName, &v)) && v.vt == VT_LPWSTR) nm = Utf8(v.pwszVal);
                PropVariantClear(&v);
                ps->Release();
            }
            if (nm.find(want_) != std::string::npos) {
                dev = d;
                name_ = nm;
            } else {
                d->Release();
            }
        }
        if (col) col->Release();
        en->Release();
        if (!dev) return Fail("no active output named like " + want_);
        IAudioClient* client = nullptr;
        HRESULT hr = dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, (void**)&client);
        dev->Release();
        if (FAILED(hr)) return Fail("can't activate");
        WAVEFORMATEX* mix = nullptr;
        client->GetMixFormat(&mix);
        bool is_float = mix->wFormatTag == WAVE_FORMAT_IEEE_FLOAT ||
                        (mix->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
                         ((WAVEFORMATEXTENSIBLE*)mix)->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
        int ch = mix->nChannels, rate = (int)mix->nSamplesPerSec, bits = mix->wBitsPerSample;
        format_ = std::to_string(rate) + " Hz, " + std::to_string(ch) + " ch, " + std::to_string(bits) + "-bit" +
                  (is_float ? " float" : "");
        hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 2000000, 0, mix, nullptr);
        CoTaskMemFree(mix);
        IAudioCaptureClient* cap = nullptr;
        if (SUCCEEDED(hr)) hr = client->GetService(__uuidof(IAudioCaptureClient), (void**)&cap);
        if (SUCCEEDED(hr)) hr = client->Start();
        if (FAILED(hr) || (!is_float && bits != 16)) {
            if (cap) cap->Release();
            client->Release();
            char b[64];
            snprintf(b, sizeof b, "loopback capture failed 0x%08lx (%s)", (unsigned long)hr, format_.c_str());
            return Fail(b);
        }
        ok_ = true;
        ready_ = true;
        const int block = rate / 20;
        const double w = 2 * 3.14159265358979 * freq_ / rate, coeff = 2 * std::cos(w);
        double s1 = 0, s2 = 0, sq = 0;
        int have = 0;
        DWORD last_data = GetTickCount();
        auto put = [&](float x) {
            double s0 = x + coeff * s1 - s2;
            s2 = s1;
            s1 = s0;
            sq += (double)x * x;
            if (++have < block) return;
            double power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
            float amp = (float)(2 * std::sqrt(std::max(0.0, power)) / block);
            auto db = [](double v) { return v > 1e-10 ? (float)(20 * std::log10(v)) : -200.0f; };
            std::lock_guard<std::mutex> lk(mu_);
            blocks_.push_back({GetTickCount(), db(std::sqrt(sq / block)), db(amp)});
            s1 = s2 = sq = 0;
            have = 0;
        };
        while (!stop_) {
            Sleep(10);
            UINT32 packet = 0;
            bool got = false;
            while (SUCCEEDED(cap->GetNextPacketSize(&packet)) && packet > 0) {
                BYTE* data = nullptr;
                UINT32 frames = 0;
                DWORD flags = 0;
                if (FAILED(cap->GetBuffer(&data, &frames, &flags, nullptr, nullptr))) break;
                for (UINT32 i = 0; i < frames; i++) {
                    float m = 0;
                    if (!(flags & AUDCLNT_BUFFERFLAGS_SILENT))
                        for (int c = 0; c < ch; c++)
                            m += is_float ? ((float*)data)[i * ch + c] : ((int16_t*)data)[i * ch + c] / 32768.0f;
                    put(m / ch);
                }
                cap->ReleaseBuffer(frames);
                got = true;
            }
            // nothing playing on the endpoint: loopback delivers no packets at all, so the silence is ours to count
            DWORD now = GetTickCount();
            if (got) {
                last_data = now;
            } else if (now - last_data >= 50) {
                for (int i = 0; i < block; i++) put(0);
                last_data += 50;
            }
        }
        client->Stop();
        cap->Release();
        client->Release();
    }

    std::string want_, name_, format_, error_;
    double freq_;
    std::thread th_;
    std::atomic<bool> stop_{false}, ready_{false}, ok_{false};
    std::mutex mu_;
    std::vector<ListenBlock> blocks_;
};
