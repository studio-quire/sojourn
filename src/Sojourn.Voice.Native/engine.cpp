// The engine: libwebrtc's threads and factory, our audio device module, our APM, and the 10 ms capture and
// render paths with the mute, the assistant's gate and tap, and local-only playback.
//
// libwebrtc's voice engine gets no APM of its own (the factory is built without one), so nothing processes the
// mic twice. Ours sees the mic on the capture side and the final device mix (voice + local sounds) on the
// render side, so everything we play is cancelled from the mic, not only the decoded voice.
#include "internal.h"

#include <cmath>
#include <cstdio>
#include <deque>
#include <memory>

namespace sv {

static sv_log_fn g_log = nullptr;
static sv_event_fn g_events = nullptr;

void Log(int level, const std::string& msg) {
    if (g_log) g_log(level, msg.c_str());
}
void Emit(int64_t pc, int kind, const char* a, const char* b, int n) {
    if (g_events) g_events(pc, kind, a, b, n);
}

std::string JsonEscape(const std::string& s) {
    std::string o;
    o.reserve(s.size() + 8);
    for (unsigned char c : s) {
        switch (c) {
            case '"': o += "\\\""; break;
            case '\\': o += "\\\\"; break;
            case '\n': o += "\\n"; break;
            case '\r': o += "\\r"; break;
            case '\t': o += "\\t"; break;
            default:
                if (c < 0x20) {
                    char b[8];
                    snprintf(b, sizeof b, "\\u%04x", c);
                    o += b;
                } else {
                    o += (char)c;
                }
        }
    }
    return o;
}

std::atomic<int> g_render_delay_ms{30};
std::atomic<int> g_capture_delay_ms{10};
std::atomic<int64_t> g_capture_frames{0}, g_render_frames{0}, g_render_glitches{0}, g_capture_glitches{0};
std::atomic<int64_t> g_capture_file_t0{0}, g_render_file_t0{0};

int64_t UnixMs() {
    FILETIME ft;
    GetSystemTimePreciseAsFileTime(&ft);
    int64_t t = ((int64_t)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
    return (t - 116444736000000000LL) / 10000;
}

// libwebrtc's own log, warnings and errors only (it's chatty).
class LogSink : public webrtc::LogSink {
public:
    void OnLogMessage(const std::string& msg, webrtc::LoggingSeverity sev) override {
        int level = sev >= webrtc::LS_ERROR ? 3 : sev >= webrtc::LS_WARNING ? 2 : sev >= webrtc::LS_INFO ? 1 : 0;
        std::string m = msg;
        while (!m.empty() && (m.back() == '\n' || m.back() == '\r')) m.pop_back();
        Log(level, "webrtc: " + m);
    }
    void OnLogMessage(const std::string& msg) override { OnLogMessage(msg, webrtc::LS_INFO); }
};

// The device module libwebrtc sees. It never touches a device: the audio threads (audio_io.cpp) push capture
// frames through CaptureFrame and pull playout through RenderFrame; this only hands libwebrtc's transport over
// and reports a stereo-out, mono-in device that's always available.
class Adm : public webrtc::webrtc_impl::AudioDeviceModuleDefault<webrtc::AudioDeviceModule> {
public:
    std::atomic<webrtc::AudioTransport*> transport{nullptr};

    int32_t RegisterAudioCallback(webrtc::AudioTransport* t) override {
        transport.store(t);
        return 0;
    }
    int16_t PlayoutDevices() override { return 1; }
    int16_t RecordingDevices() override { return 1; }
    int32_t PlayoutIsAvailable(bool* a) override { *a = true; return 0; }
    int32_t RecordingIsAvailable(bool* a) override { *a = true; return 0; }
    int32_t InitPlayout() override { return 0; }
    int32_t InitRecording() override { return 0; }
    int32_t StartPlayout() override { playing_ = true; return 0; }
    int32_t StopPlayout() override { playing_ = false; return 0; }
    bool Playing() const override { return playing_; }
    int32_t StartRecording() override { recording_ = true; return 0; }
    int32_t StopRecording() override { recording_ = false; return 0; }
    bool Recording() const override { return recording_; }
    int32_t StereoPlayoutIsAvailable(bool* a) const override { *a = true; return 0; }
    int32_t StereoPlayout(bool* e) const override { *e = true; return 0; }
    int32_t StereoRecordingIsAvailable(bool* a) const override { *a = false; return 0; }
    int32_t StereoRecording(bool* e) const override { *e = false; return 0; }
    int32_t PlayoutDelay(uint16_t* d) const override { *d = (uint16_t)g_render_delay_ms.load(); return 0; }
    bool IsStopOnMuteModeEnabled() const override { return false; }

private:
    std::atomic<bool> playing_{false}, recording_{false};
};

// A local-only sound stream: 48 kHz stereo float, resampled linearly on the way in.
struct LocalStream {
    std::mutex mu;
    std::deque<float> fifo;  // interleaved stereo
    double phase = 0;        // fractional read position for resampling, carried across calls
    int last_rate = 0;
    float last_l = 0, last_r = 0;
};

struct Engine {
    std::unique_ptr<webrtc::Thread> network, worker, signaling;
    webrtc::scoped_refptr<webrtc::PeerConnectionFactoryInterface> factory;
    webrtc::scoped_refptr<Adm> adm;
    webrtc::scoped_refptr<webrtc::AudioProcessing> apm;
    std::unique_ptr<LogSink> log_sink;

    // capture side state (capture thread only, except the atomics)
    std::atomic<bool> muted{true};
    std::atomic<int> mute_fade_ms{100};
    std::atomic<bool> gate{false};
    std::atomic<int> gate_tail_frames{0};
    std::atomic<bool> tap{false};
    // what the tap read: 16 kHz mono, filled by the capture thread, drained by sv_tap_read (no foreign code ever
    // runs on the audio thread)
    std::mutex tap_mu;
    std::deque<int16_t> tap_out;
    float gain = 0.0f, gain_step = 0.0f, gain_target = 0.0f;
    bool gate_was_open = false;
    int tail_left = 0;
    webrtc::PushResampler<int16_t> tap_resampler{kFrame, 160, 1};
    static constexpr int kPreroll = 30;  // 300 ms
    int16_t preroll[kPreroll][160];
    int preroll_head = 0, preroll_count = 0;

    std::atomic<float> mic_level{0}, mic_peak{0}, out_level{0}, voice_level{0};
    std::atomic<float> voice_volume{1.0f}, output_volume{1.0f};

    LocalStream streams[8];

    // tests: a synthetic echo path, the device mix fed back into the mic after a delay (sv_test_echo)
    std::mutex echo_mu;
    std::vector<float> echo_ring;  // mono, 1 s
    size_t echo_write = 0;
    std::atomic<int> echo_delay{0};
    std::atomic<float> echo_gain{0};
};

static Engine* g = nullptr;

webrtc::PeerConnectionFactoryInterface* Factory() { return g ? g->factory.get() : nullptr; }
webrtc::Thread* SignalingThread() { return g ? g->signaling.get() : nullptr; }

static float Rms(const float* x, int n) {
    double s = 0;
    for (int i = 0; i < n; i++) s += (double)x[i] * x[i];
    return (float)std::sqrt(s / n);
}

static void ApplyApmConfig(int aec, int ns, int agc, int hpf) {
    webrtc::AudioProcessing::Config c;
    c.echo_canceller.enabled = aec != 0;
    c.echo_canceller.mobile_mode = false;
    c.gain_controller1.enabled = false;
    c.gain_controller2.enabled = agc != 0;
    c.gain_controller2.adaptive_digital.enabled = agc != 0;
    c.high_pass_filter.enabled = hpf != 0;
    c.noise_suppression.enabled = ns > 0;
    if (ns > 0) {
        using L = webrtc::AudioProcessing::Config::NoiseSuppression::Level;
        static const L levels[] = {L::kLow, L::kModerate, L::kHigh, L::kVeryHigh};
        c.noise_suppression.level = levels[std::min(std::max(ns, 1), 4) - 1];
    }
    c.transient_suppression.enabled = ns > 0;
    c.pipeline.multi_channel_render = true;
    c.pipeline.multi_channel_capture = false;
    g->apm->ApplyConfig(c);
}

void CaptureFrame(const float* mono) {
    if (!g) return;
    g_capture_frames++;
    float buf[kFrame];
    memcpy(buf, mono, sizeof buf);
    if (float eg = g->echo_gain.load(); eg > 0) {
        std::lock_guard<std::mutex> lk(g->echo_mu);
        size_t n = g->echo_ring.size();
        size_t start = (g->echo_write + n - (size_t)g->echo_delay.load() - kFrame) % n;
        for (int i = 0; i < kFrame; i++) buf[i] += eg * g->echo_ring[(start + i) % n];
    }

    if (g->apm) {
        g->apm->set_stream_delay_ms(g_render_delay_ms.load() + g_capture_delay_ms.load());
        float* ch[1] = {buf};
        webrtc::StreamConfig cfg(kRate, 1);
        g->apm->ProcessStream(ch, cfg, cfg, ch);
    }
    float rms = Rms(buf, kFrame);
    g->mic_level.store(g->mic_level.load() * 0.7f + rms * 0.3f);
    float peak = 0;
    for (float v : buf) peak = std::max(peak, std::fabs(v));
    g->mic_peak.store(std::max(peak, g->mic_peak.load() * 0.9f));

    int16_t pcm[kFrame];
    for (int i = 0; i < kFrame; i++) {
        float v = std::min(1.0f, std::max(-1.0f, buf[i]));
        pcm[i] = (int16_t)std::lrintf(v * 32767.0f);
    }

    // The assistant's gate and its tap.
    bool gate_now = g->gate.load();
    if (gate_now) {
        g->tail_left = -1;  // the tail is read when the gate closes (sv_set_gate(0, tail) sets it then)
    } else if (g->gate_was_open) {
        if (g->tail_left < 0) g->tail_left = g->gate_tail_frames.load();
        if (g->tail_left > 0) {
            g->tail_left--;
            gate_now = true;
        }
    }
    auto tap = [](const int16_t* pcm16k, int n) {
        std::lock_guard<std::mutex> lk(g->tap_mu);
        g->tap_out.insert(g->tap_out.end(), pcm16k, pcm16k + n);
        size_t cap = 16000 * 5;  // nobody reading for 5 s: drop the oldest
        if (g->tap_out.size() > cap) g->tap_out.erase(g->tap_out.begin(), g->tap_out.begin() + (g->tap_out.size() - cap));
    };
    if (g->tap.load()) {
        int16_t* slot = g->preroll[g->preroll_head];
        webrtc::InterleavedView<const int16_t> src(pcm, kFrame, 1);
        webrtc::InterleavedView<int16_t> dst(slot, 160, 1);
        g->tap_resampler.Resample(src, dst);
        g->preroll_head = (g->preroll_head + 1) % Engine::kPreroll;
        g->preroll_count = std::min(g->preroll_count + 1, Engine::kPreroll);
        if (gate_now && !g->gate_was_open) {
            // just opened: the pre-roll (this frame included), oldest first
            int start = (g->preroll_head - g->preroll_count + Engine::kPreroll) % Engine::kPreroll;
            for (int i = 0; i < g->preroll_count; i++) tap(g->preroll[(start + i) % Engine::kPreroll], 160);
        } else if (gate_now) {
            tap(slot, 160);
        }
    }
    g->gate_was_open = gate_now;

    // Toward residents: zero when muted or while the gate is open, with a ramp.
    float target = (g->muted.load() || gate_now) ? 0.0f : 1.0f;
    if (target != g->gain_target) {
        int ramp = gate_now ? kFrame : std::max(1, g->mute_fade_ms.load() * kRate / 1000);
        g->gain_step = (target - g->gain) / ramp;
        g->gain_target = target;
    }
    for (int i = 0; i < kFrame; i++) {
        if (g->gain != g->gain_target) {
            g->gain += g->gain_step;
            if ((g->gain_step > 0 && g->gain > g->gain_target) || (g->gain_step < 0 && g->gain < g->gain_target))
                g->gain = g->gain_target;
        }
        pcm[i] = (int16_t)(pcm[i] * g->gain);
    }

    webrtc::AudioTransport* t = g->adm ? g->adm->transport.load() : nullptr;
    if (t) {
        uint32_t new_level = 0;
        t->RecordedDataIsAvailable(pcm, kFrame, 2, 1, kRate,
                                   (uint32_t)(g_render_delay_ms.load() + g_capture_delay_ms.load()), 0, 0, false,
                                   new_level);
    }
}

void RenderFrame(float* stereo) {
    if (!g) {
        memset(stereo, 0, sizeof(float) * kFrame * 2);
        return;
    }
    g_render_frames++;
    int16_t voice[kFrame * 2];
    memset(voice, 0, sizeof voice);
    webrtc::AudioTransport* t = g->adm ? g->adm->transport.load() : nullptr;
    if (t) {
        size_t out = 0;
        int64_t elapsed = 0, ntp = 0;
        t->NeedMorePlayData(kFrame, 4, 2, kRate, voice, out, &elapsed, &ntp);
    }
    float vv = g->voice_volume.load();
    for (int i = 0; i < kFrame * 2; i++) stereo[i] = voice[i] * (vv / 32768.0f);
    g->voice_level.store(g->voice_level.load() * 0.7f + Rms(stereo, kFrame * 2) * 0.3f);

    for (auto& s : g->streams) {
        std::lock_guard<std::mutex> lk(s.mu);
        size_t n = std::min(s.fifo.size(), (size_t)kFrame * 2);
        for (size_t i = 0; i < n; i++) stereo[i] += s.fifo[i];
        s.fifo.erase(s.fifo.begin(), s.fifo.begin() + n);
    }

    WorldMix(stereo);  // world sounds and the music stream (world.cpp)

    float ov = g->output_volume.load();
    for (int i = 0; i < kFrame * 2; i++) stereo[i] = std::min(1.0f, std::max(-1.0f, stereo[i] * ov));
    g->out_level.store(g->out_level.load() * 0.7f + Rms(stereo, kFrame * 2) * 0.3f);

    if (g->echo_gain.load() > 0) {
        std::lock_guard<std::mutex> lk(g->echo_mu);
        size_t n = g->echo_ring.size();
        for (int i = 0; i < kFrame; i++) g->echo_ring[(g->echo_write + i) % n] = 0.5f * (stereo[2 * i] + stereo[2 * i + 1]);
        g->echo_write = (g->echo_write + kFrame) % n;
    }

    if (g->apm) {
        float l[kFrame], r[kFrame];
        for (int i = 0; i < kFrame; i++) {
            l[i] = stereo[2 * i];
            r[i] = stereo[2 * i + 1];
        }
        float* ch[2] = {l, r};
        webrtc::StreamConfig cfg(kRate, 2);
        g->apm->ProcessReverseStream(ch, cfg, cfg, ch);
    }
}

}  // namespace sv

using namespace sv;

SV_API int sv_init(sv_log_fn log, sv_event_fn events) {
    if (g) return 0;
    g_log = log;
    g_events = events;
    g = new Engine();
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);
    webrtc::InitializeSSL();
    g->log_sink = std::make_unique<LogSink>();
    webrtc::LogMessage::LogToDebug(webrtc::LS_NONE);
    webrtc::LogMessage::AddLogToStream(g->log_sink.get(), webrtc::LS_WARNING);

    g->network = webrtc::Thread::CreateWithSocketServer();
    g->network->SetName("sv-network", nullptr);
    g->network->Start();
    g->worker = webrtc::Thread::Create();
    g->worker->SetName("sv-worker", nullptr);
    g->worker->Start();
    g->signaling = webrtc::Thread::Create();
    g->signaling->SetName("sv-signaling", nullptr);
    g->signaling->Start();

    webrtc::Environment env = webrtc::CreateEnvironment();
    g->apm = webrtc::BuiltinAudioProcessingBuilder().Build(env);
    if (g->apm) {
        webrtc::ProcessingConfig pc;
        pc.input_stream().set_sample_rate_hz(kRate);
        pc.input_stream().set_num_channels(1);
        pc.output_stream().set_sample_rate_hz(kRate);
        pc.output_stream().set_num_channels(1);
        pc.reverse_input_stream().set_sample_rate_hz(kRate);
        pc.reverse_input_stream().set_num_channels(2);
        pc.reverse_output_stream().set_sample_rate_hz(kRate);
        pc.reverse_output_stream().set_num_channels(2);
        g->apm->Initialize(pc);
        ApplyApmConfig(1, 4, 1, 1);  // the viewer's: AEC3, NS very high (+ transient), AGC2 adaptive, HPF
    } else {
        Log(3, "no audio processing module in this libwebrtc build");
    }

    g->adm = webrtc::make_ref_counted<Adm>();

    webrtc::PeerConnectionFactoryDependencies deps;
    deps.network_thread = g->network.get();
    deps.worker_thread = g->worker.get();
    deps.signaling_thread = g->signaling.get();
    deps.env = env;
    deps.adm = g->adm;
    deps.audio_encoder_factory = webrtc::CreateBuiltinAudioEncoderFactory();
    deps.audio_decoder_factory = webrtc::CreateBuiltinAudioDecoderFactory();
    // no audio_processing_builder: the engine's own APM stays out of it
    webrtc::EnableMedia(deps);
    g->factory = webrtc::CreateModularPeerConnectionFactory(std::move(deps));
    if (!g->factory) {
        Log(3, "CreateModularPeerConnectionFactory failed");
        return -1;
    }
    Log(1, "voice engine up (libwebrtc m144)");
    return 0;
}

SV_API void sv_shutdown(void) {
    if (!g) return;
    AudioStop();
    PeersCloseAll();
    g->factory = nullptr;
    g->adm = nullptr;
    webrtc::LogMessage::RemoveLogToStream(g->log_sink.get());
    g->signaling->Stop();
    g->worker->Stop();
    g->network->Stop();
    webrtc::CleanupSSL();
    // The engine itself is left allocated: libwebrtc objects can still be unwinding on their threads, and the
    // process is exiting anyway.
    g_events = nullptr;
}

SV_API int sv_devices(int capture, char* buf, int cap) {
    std::string j = DeviceListJson(capture != 0);
    int need = (int)j.size() + 1;
    if (buf && cap > 0) {
        int n = std::min(cap - 1, (int)j.size());
        memcpy(buf, j.data(), n);
        buf[n] = 0;
    }
    return need;
}

SV_API int sv_audio_start(const char* capture, const char* render) {
    if (!g) return -1;
    return AudioStart(capture ? capture : "", render ? render : "") ? 0 : -1;
}

SV_API void sv_audio_stop(void) { AudioStop(); }

float sv::OutLevel() { return g ? g->out_level.load() : 0.0f; }

SV_API int sv_output_probe(int ms, char* buf, int cap) {
    std::string j = OutputProbe(std::min(std::max(ms, 50), 5000));
    if (buf && cap > 0) {
        int n = std::min(cap - 1, (int)j.size());
        memcpy(buf, j.data(), n);
        buf[n] = 0;
    }
    return (int)j.size() + 1;
}

SV_API int sv_device_state(int dir, char* buf, int cap) {
    std::string j = DeviceStateJson(dir);
    if (buf && cap > 0) {
        int n = std::min(cap - 1, (int)j.size());
        memcpy(buf, j.data(), n);
        buf[n] = 0;
    }
    return (int)j.size() + 1;
}

SV_API void sv_test_device(int dir, int what, const char* spec) { AudioTest(dir, what, spec ? spec : ""); }

SV_API void sv_set_mute(int muted, int fade_ms) {
    if (!g) return;
    g->mute_fade_ms.store(std::max(0, fade_ms));
    g->muted.store(muted != 0);
}

SV_API void sv_set_gate(int open, int tail_ms) {
    if (!g) return;
    g->gate_tail_frames.store(std::max(0, tail_ms) / 10);
    g->gate.store(open != 0);
}

SV_API void sv_set_tap(int enabled) {
    if (!g) return;
    g->tap.store(enabled != 0);
    if (!enabled) {
        std::lock_guard<std::mutex> lk(g->tap_mu);
        g->tap_out.clear();
    }
}

SV_API int sv_tap_read(int16_t* out, int max) {
    if (!g || !out || max <= 0) return 0;
    std::lock_guard<std::mutex> lk(g->tap_mu);
    int n = (int)std::min((size_t)max, g->tap_out.size());
    std::copy(g->tap_out.begin(), g->tap_out.begin() + n, out);
    g->tap_out.erase(g->tap_out.begin(), g->tap_out.begin() + n);
    return n;
}

SV_API void sv_set_voice_volume(float v) {
    if (g) g->voice_volume.store(std::max(0.0f, v));
}
SV_API void sv_set_output_volume(float v) {
    if (g) g->output_volume.store(std::max(0.0f, v));
}
SV_API float sv_mic_level(void) { return g ? g->mic_level.load() : 0.0f; }

SV_API void sv_set_processing(int aec, int ns, int agc, int hpf) {
    if (g && g->apm) ApplyApmConfig(aec, ns, agc, hpf);
}

SV_API void sv_test_echo(int delay_ms, float gain) {
    if (!g) return;
    std::lock_guard<std::mutex> lk(g->echo_mu);
    g->echo_ring.assign(kRate, 0.0f);
    g->echo_write = 0;
    g->echo_delay.store(std::min(std::max(delay_ms, 0), 900) * kRate / 1000);
    g->echo_gain.store(std::max(0.0f, gain));
}

SV_API void sv_play(int stream, const int16_t* pcm, int frames, int rate, int channels) {
    if (!g || stream < 0 || stream >= 8 || frames <= 0 || rate <= 0 || (channels != 1 && channels != 2)) return;
    LocalStream& s = g->streams[stream];
    std::lock_guard<std::mutex> lk(s.mu);
    if (s.last_rate != rate) {
        s.phase = 0;
        s.last_rate = rate;
    }
    // Linear interpolation from rate to 48 kHz; position carried across calls so chunks join seamlessly.
    double step = (double)rate / kRate;
    auto at = [&](int i, int c) -> float {
        if (i < 0) return c == 0 ? s.last_l : s.last_r;
        int ch = channels == 2 ? c : 0;
        return pcm[i * channels + ch] / 32768.0f;
    };
    double pos = s.phase;  // in input frames, relative to pcm[0]; -1 < pos means between last sample and pcm[0]
    while (pos < frames - 1 + 1e-9) {
        int i0 = (int)std::floor(pos);
        double f = pos - i0;
        for (int c = 0; c < 2; c++) {
            float a = at(i0, c), b = at(std::min(i0 + 1, frames - 1), c);
            s.fifo.push_back((float)(a + (b - a) * f));
        }
        pos += step;
    }
    s.phase = pos - frames;
    s.last_l = at(frames - 1, 0);
    s.last_r = at(frames - 1, 1);
}

SV_API void sv_play_clear(int stream) {
    if (!g || stream < 0 || stream >= 8) return;
    std::lock_guard<std::mutex> lk(g->streams[stream].mu);
    g->streams[stream].fifo.clear();
}

SV_API int sv_play_queued_ms(int stream) {
    if (!g || stream < 0 || stream >= 8) return 0;
    std::lock_guard<std::mutex> lk(g->streams[stream].mu);
    return (int)(g->streams[stream].fifo.size() / 2 * 1000 / kRate);
}

SV_API int sv_stats(char* buf, int cap) {
    if (!g) return 0;
    char tmp[1280];
    auto db = [](float x) { return x > 1e-6f ? 20.0f * std::log10(x) : -120.0f; };
    double erl = -1, erle = -1;
    int delay = -1;
    if (g->apm) {
        auto st = g->apm->GetStatistics(true);   // "remote tracks": the echo metrics are only kept with them
        if (st.echo_return_loss) erl = *st.echo_return_loss;
        if (st.echo_return_loss_enhancement) erle = *st.echo_return_loss_enhancement;
        if (st.delay_ms) delay = *st.delay_ms;
    }
    snprintf(tmp, sizeof tmp,
             "{\"mic_db\":%.1f,\"mic_peak_db\":%.1f,\"voice_db\":%.1f,\"out_db\":%.1f,\"muted\":%s,\"gate\":%s,"
             "\"gain\":%.3f,\"capture_frames\":%lld,\"render_frames\":%lld,\"capture_glitches\":%lld,"
             "\"render_glitches\":%lld,\"render_delay_ms\":%d,\"capture_delay_ms\":%d,\"aec_delay_ms\":%d,"
             "\"erl\":%.1f,\"erle\":%.1f,\"capture_file_t0\":%lld,\"render_file_t0\":%lld}",
             db(g->mic_level.load()), db(g->mic_peak.load()), db(g->voice_level.load()), db(g->out_level.load()),
             g->muted.load() ? "true" : "false", g->gate_was_open ? "true" : "false", g->gain,
             (long long)g_capture_frames.load(), (long long)g_render_frames.load(),
             (long long)g_capture_glitches.load(), (long long)g_render_glitches.load(), g_render_delay_ms.load(),
             g_capture_delay_ms.load(), delay, erl, erle, (long long)g_capture_file_t0.load(),
             (long long)g_render_file_t0.load());
    int len = (int)strlen(tmp);
    if (buf && cap > 0) {
        int n = std::min(cap - 1, len);
        memcpy(buf, tmp, n);
        buf[n] = 0;
    }
    return len + 1;
}
