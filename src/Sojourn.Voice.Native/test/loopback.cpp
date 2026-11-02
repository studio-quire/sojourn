// sv_loopback: the shim on its own, through its C ABI. Two in-process peer connections offer and answer each
// other; the mic (a WAV) goes out of one and the playout (a WAV) records what the other received.
//   sv_loopback devices
//   sv_loopback open [CAPTURE RENDER]     open the real devices (default: communications) for 2 s, silent
//   sv_loopback devices-change CAPTURE RENDER   devices lost, gone, back and the default moving (simulated), silent
//   sv_loopback run IN.wav OUT.wav SECONDS [--mute-at S] [--raw] [--gate-at S --gate-for S]
//   sv_loopback listen OUTPUT [SECONDS [FREQ]]   what reaches an output (loopback capture), 50 ms levels, nothing played
//   sv_loopback switch A B [LEVEL_DB]     a quiet tone played on output A, moved to B and back, heard on each by
//                                         loopback capture (part of a name for each)
// --raw turns our APM off (to check the path itself is transparent), --no-aec only its echo canceller. Prints JSON lines; tools/voice.py analyses.
#include "../sojourn_voice.h"

#define NOMINMAX
#include <windows.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "listen.h"

static constexpr double kTone = 1031.0;   // the switch test's tone (Hz): not a round number other sounds sit on

static std::string Esc(const std::string& s) {
    std::string r;
    for (char ch : s) r += (ch == '\\' || ch == '"') ? std::string("\\") + ch : std::string(1, ch);
    return r;
}

static std::mutex mu;
static int64_t pc_a = 0, pc_b = 0;
static std::atomic<int> connected{0}, data_open{0}, got_a{0}, got_b{0};
static std::vector<std::pair<int64_t, std::string>> pending;  // events handled on the main thread
static std::atomic<long long> tap_frames{0};
static DWORD t_start = GetTickCount();

static void OnLog(int level, const char* msg) {
    if (level >= 2) fprintf(stderr, "[%d] %s\n", level, msg);
}

struct Ev {
    int64_t pc;
    int kind;
    std::string a, b;
    int n;
};
static std::vector<Ev> queue;

static void OnEvent(int64_t pc, int kind, const char* a, const char* b, int n) {
    std::lock_guard<std::mutex> lk(mu);
    queue.push_back({pc, kind, a ? a : "", b ? b : "", n});
}


static void Drain() {
    std::vector<Ev> evs;
    {
        std::lock_guard<std::mutex> lk(mu);
        evs.swap(queue);
    }
    for (auto& e : evs) {
        int64_t other = e.pc == pc_a ? pc_b : pc_a;
        switch (e.kind) {
            case SV_EV_OFFER:
                pc_b = sv_pc_new_answerer("", e.a.c_str());
                break;
            case SV_EV_ANSWER:
                sv_pc_answer(pc_a, e.a.c_str());
                break;
            case SV_EV_CANDIDATE: {
                std::string c = e.a.rfind("candidate:", 0) == 0 ? e.a : "candidate:" + e.a;
                if (other) sv_pc_add_candidate(other, e.b.c_str(), e.n, c.c_str());
                break;
            }
            case SV_EV_STATE:
                if (e.n == 2) connected++;
                printf("{\"event\":\"state\",\"pc\":%lld,\"state\":%d}\n", (long long)e.pc, e.n);
                break;
            case SV_EV_DATA_OPEN:
                data_open++;
                sv_pc_send(e.pc, e.pc == pc_a ? "{\"from\":\"a\"}" : "{\"from\":\"b\"}");
                break;
            case SV_EV_DATA:
                if (e.pc == pc_a && e.a == "{\"from\":\"b\"}") got_a++;
                if (e.pc == pc_b && e.a == "{\"from\":\"a\"}") got_b++;
                break;
            case SV_EV_ERROR:
                printf("{\"event\":\"error\",\"pc\":%lld,\"text\":\"%s\"}\n", (long long)e.pc, e.a.c_str());
                break;
            case SV_EV_DEVICE:
            {
                std::string name;
                for (char ch : e.b) name += (ch == '\\' || ch == '"') ? std::string("\\") + ch : std::string(1, ch);
                printf("{\"event\":\"device\",\"what\":\"%s\",\"name\":\"%s\",\"dir\":%d,\"t\":%lu}\n", e.a.c_str(),
                       name.c_str(), e.n, GetTickCount() - t_start);
            }
                break;
        }
    }
    fflush(stdout);
}

int main(int argc, char** argv) {
    SetConsoleOutputCP(CP_UTF8);
    if (argc >= 2 && !strcmp(argv[1], "devices")) {
        sv_init(OnLog, OnEvent);
        for (int cap = 1; cap >= 0; cap--) {
            int n = sv_devices(cap, nullptr, 0);
            std::string s(n, '\0');
            sv_devices(cap, s.data(), n);
            s.resize(strlen(s.c_str()));
            printf("{\"%s\":%s}\n", cap ? "capture" : "render", s.c_str());
        }
        return 0;
    }
    if (argc >= 2 && !strcmp(argv[1], "open")) {
        // the real devices, playing silence for a moment: do they open, do frames flow?
        sv_init(OnLog, OnEvent);
        sv_audio_start(argc > 2 ? argv[2] : "", argc > 3 ? argv[3] : "");
        DWORD t0 = GetTickCount();
        while (GetTickCount() - t0 < (argc > 4 ? (DWORD)atoi(argv[4]) : 2000u)) {
            Drain();
            Sleep(10);
        }
        char stats[2048];
        sv_stats(stats, sizeof stats);
        printf("{\"event\":\"done\",\"stats\":%s}\n", stats);
        sv_audio_stop();
        sv_shutdown();
        return 0;
    }
    if (argc >= 4 && !strcmp(argv[1], "devices-change")) {
        // Device changes, simulated through sv_test_device on real devices: CAPTURE and RENDER are specs of devices
        // asked for (to lose and get back); render is also run on the default, moved to RENDER and back. Nothing is
        // played: the engine's mix is silence. Each step prints its device events, the frames that ran and the CPU
        // the process used.
        sv_init(OnLog, OnEvent);
        const char* cap = argv[2];
        const char* ren = argv[3];
        auto ms_of = [](auto f) {
            LARGE_INTEGER a, b, fq;
            QueryPerformanceFrequency(&fq);
            QueryPerformanceCounter(&a);
            f();
            QueryPerformanceCounter(&b);
            return (b.QuadPart - a.QuadPart) * 1000.0 / fq.QuadPart;
        };
        auto cpu_ms = [] {
            FILETIME c, x, k, u;
            GetProcessTimes(GetCurrentProcess(), &c, &x, &k, &u);
            auto v = [](FILETIME f) { return ((long long)f.dwHighDateTime << 32 | f.dwLowDateTime) / 10000.0; };
            return v(k) + v(u);
        };
        auto step = [&](const char* name, int ms) {
            printf("{\"event\":\"begin\",\"step\":\"%s\",\"t\":%lu}\n", name, GetTickCount() - t_start);
            fflush(stdout);
            double c0 = cpu_ms();
            DWORD s = GetTickCount();
            while (GetTickCount() - s < (DWORD)ms) {
                Drain();
                Sleep(10);
            }
            Drain();
            char stats[2048];
            sv_stats(stats, sizeof stats);
            printf("{\"event\":\"end\",\"step\":\"%s\",\"t\":%lu,\"cpu_ms\":%.1f,\"stats\":%s}\n", name,
                   GetTickCount() - t_start, cpu_ms() - c0, stats);
            fflush(stdout);
        };
        double start_ms = ms_of([&] { sv_audio_start(cap, ren); });
        step("open", 1500);
        sv_test_device(1, 0, "");
        step("render invalidated", 1500);
        sv_test_device(0, 0, "");
        step("capture invalidated", 1500);
        sv_test_device(1, 1, ren);
        step("render device gone", 1500);
        sv_test_device(1, 2, "");
        step("render device back", 1500);
        sv_test_device(0, 1, "");
        sv_test_device(1, 1, "");
        step("all gone", 3000);
        sv_test_device(0, 2, "");
        sv_test_device(1, 2, "");
        step("all back", 1500);
        double restart_ms = ms_of([&] { sv_audio_start(cap, ""); });
        step("default", 1500);
        sv_test_device(1, 3, ren);
        step("default moved", 1500);
        sv_test_device(1, 3, "");
        step("default back", 1500);
        sv_test_device(0, 1, "");
        sv_test_device(1, 1, "");
        step("gone at the end", 1200);
        double stop_ms = ms_of([] { sv_audio_stop(); });
        sv_test_device(0, 2, "");
        sv_test_device(1, 2, "");
        Drain();
        printf("{\"event\":\"done\",\"start_ms\":%.2f,\"restart_ms\":%.2f,\"stop_ms\":%.2f}\n", start_ms, restart_ms,
               stop_ms);
        fflush(stdout);
        sv_shutdown();
        return 0;
    }
    if (argc >= 3 && !strcmp(argv[1], "listen")) {
        // what reaches an output (any app's sound): 50 ms blocks of its level and a test tone's, for SECONDS
        Listener l(argv[2], argc > 4 ? atof(argv[4]) : kTone);
        if (!l.Start()) {
            printf("{\"event\":\"error\",\"text\":\"%s\"}\n", l.Error().c_str());
            return 1;
        }
        printf("{\"event\":\"listening\",\"name\":\"%s\",\"format\":\"%s\"}\n", Esc(l.Name()).c_str(), l.Format().c_str());
        fflush(stdout);
        DWORD t0 = GetTickCount(), secs = argc > 3 ? (DWORD)(atof(argv[3]) * 1000) : 5000u;
        size_t shown = 0;
        while (GetTickCount() - t0 < secs + 100) {
            Sleep(50);
            auto bs = l.Blocks();
            for (; shown < bs.size(); shown++)
                printf("{\"t\":%lu,\"rms_db\":%.1f,\"tone_db\":%.1f}\n", bs[shown].t - t0, bs[shown].rms_db, bs[shown].tone_db);
            fflush(stdout);
        }
        return 0;
    }
    if (argc >= 4 && !strcmp(argv[1], "switch")) {
        // The output moved between two real devices while a quiet tone plays (LEVEL dBFS, -60 by default), each
        // device listened to by loopback capture: does the tone arrive on the new one and leave the old? There and
        // back. Prints a JSON line per phase with the tone's level on each device and when it arrived and left.
        double level_db = argc > 4 ? atof(argv[4]) : -60;
        Listener la(argv[2], kTone), lb(argv[3], kTone);
        if (!la.Start() || !lb.Start()) {
            printf("{\"event\":\"error\",\"text\":\"%s %s\"}\n", Esc(la.Error()).c_str(), Esc(lb.Error()).c_str());
            return 1;
        }
        printf("{\"event\":\"listening\",\"a\":\"%s\",\"a_format\":\"%s\",\"b\":\"%s\",\"b_format\":\"%s\"}\n",
               Esc(la.Name()).c_str(), la.Format().c_str(), Esc(lb.Name()).c_str(), lb.Format().c_str());
        sv_init(OnLog, OnEvent);
        Sleep(600);
        DWORD t_base = GetTickCount();
        float base_a = la.ToneMax(t_base - 500, t_base), base_b = lb.ToneMax(t_base - 500, t_base);
        // the tone, kept ~200 ms ahead in the shim's local stream
        std::atomic<bool> playing{true};
        std::thread feed([&] {
            const int rate = 48000, chunk = 4800;
            std::vector<int16_t> pcm(chunk);
            double amp = std::pow(10.0, level_db / 20) * 32767, ph = 0, step = 2 * 3.14159265358979 * kTone / rate;
            while (playing) {
                if (sv_play_queued_ms(0) < 200) {
                    for (auto& s : pcm) {
                        s = (int16_t)std::lrint(std::sin(ph) * amp);
                        ph += step;
                    }
                    sv_play(0, pcm.data(), chunk, rate, 1);
                }
                Sleep(20);
            }
        });
        std::string spec_a = std::string("name:") + la.Name(), spec_b = std::string("name:") + lb.Name();
        // a phase: the output put on `to` (by spec, or by `act`), then 1.5 s; the tone counts as there 15 dB over
        // what was there before
        auto phase = [&](const char* name, const std::string& spec, Listener& to, Listener& from, float base_to,
                         float base_from, void (*act)() = nullptr) {
            DWORD t0 = GetTickCount();
            if (act)
                act();
            else
                sv_audio_start("none", spec.c_str());
            for (int i = 0; i < 150; i++) {
                Drain();
                Sleep(10);
            }
            DWORD t1 = GetTickCount();
            DWORD arrived = to.FirstTone(t0, base_to + 15), left = from.LastTone(t1, base_from + 15);
            char stats[2048], state[1024], probe[256];
            sv_stats(stats, sizeof stats);
            sv_device_state(1, state, sizeof state);
            sv_output_probe(400, probe, sizeof probe);   // the shim's own check of what reaches the output
            printf("{\"event\":\"phase\",\"phase\":\"%s\",\"to_db\":%.1f,\"from_db\":%.1f,\"arrived_ms\":%ld,"
                   "\"left_ms\":%ld,\"state\":%s,\"probe\":%s,\"stats\":%s}\n",
                   name, to.ToneMax(t1 - 500, t1), from.ToneMax(t1 - 500, t1),
                   arrived ? (long)(arrived - t0) : -1L, left ? (long)left - (long)t0 : -1L, state, probe, stats);
            fflush(stdout);
        };
        printf("{\"event\":\"baseline\",\"a_db\":%.1f,\"b_db\":%.1f,\"level_db\":%.1f}\n", base_a, base_b, level_db);
        phase("onto a", spec_a, la, lb, base_a, base_b);
        phase("a to b", spec_b, lb, la, base_b, base_a);
        phase("b to a", spec_a, la, lb, base_a, base_b);
        // B refusing to start (sv_test_device 4) while A stands in for Windows' default: asked for B, the sound stays
        // on A (the default meanwhile) with the reason; then B starts again and, at Windows' next device change (the
        // test hook wakes the thread as one would), the sound goes to B
        sv_test_device(1, 3, spec_a.c_str());
        sv_test_device(1, 4, spec_b.c_str());
        phase("b refuses", spec_b, la, lb, base_a, base_b);
        phase("b starts again", spec_b, lb, la, base_b, base_a, [] { sv_test_device(1, 4, ""); });
        sv_test_device(1, 3, "");
        playing = false;
        feed.join();
        sv_play_clear(0);
        sv_audio_stop();
        Drain();
        printf("{\"event\":\"done\"}\n");
        fflush(stdout);
        sv_shutdown();
        return 0;
    }
    if (argc >= 5 && !strcmp(argv[1], "chirp")) {
        // the echo path of real devices (the headset's speakers into its own mic): a sweep played out, the mic
        // recorded, first with no processing (the echo as it is), then with the echo canceller on (what's left)
        std::string dir = argv[4];
        sv_init(OnLog, OnEvent);
        sv_set_tap(1);
        if (argc > 5 && !strcmp(argv[5], "synthetic")) sv_test_echo(120, 0.5f);   // checks the tool itself
        sv_audio_start(argv[2], argv[3]);
        Sleep(2500);   // Link's mic takes a moment to open
        Drain();
        const int rate = 48000;
        std::vector<int16_t> sweep(rate * 3 / 2);
        double phase = 0;
        for (size_t i = 0; i < sweep.size(); i++) {
            double t = (double)i / sweep.size();
            double f = 300.0 * pow(8000.0 / 300.0, t);  // logarithmic, 300 Hz to 8 kHz
            phase += 2 * 3.14159265358979 * f / rate;
            double env = std::min(1.0, std::min(t * 20, (1 - t) * 20));
            sweep[i] = (int16_t)(sin(phase) * env * 0.25 * 32767);
        }
        // the canceller learns from broadband sound: a sweep excites one band at a time, so its pass plays noise bursts
        std::vector<int16_t> noise(sweep.size());
        uint32_t seed = 12345;
        for (size_t i = 0; i < noise.size(); i++) {
            seed = seed * 1664525u + 1013904223u;
            double t = (double)i / noise.size();
            double env = std::min(1.0, std::min(t * 20, (1 - t) * 20));
            noise[i] = (int16_t)(((int)(seed >> 16) - 32768) * 0.12 * env);
        }
        auto take = [&](const char* name, int plays, int gap_ms, const std::vector<int16_t>& sound) {
            std::vector<int16_t> rec;
            int16_t buf[1600];
            sv_set_gate(1, 0);
            for (int k = 0; k < plays; k++) {
                sv_play(0, sound.data(), (int)sound.size(), rate, 1);
                DWORD t0 = GetTickCount();
                while (GetTickCount() - t0 < (DWORD)(1500 + gap_ms)) {
                    int n = sv_tap_read(buf, 1600);
                    rec.insert(rec.end(), buf, buf + n);
                    Sleep(10);
                }
            }
            sv_set_gate(0, 0);
            Sleep(100);
            int n;
            while ((n = sv_tap_read(buf, 1600)) > 0) rec.insert(rec.end(), buf, buf + n);
            std::string path = dir + "/" + name;
            FILE* f = fopen(path.c_str(), "wb");
            if (!f) return;
            uint32_t bytes = (uint32_t)rec.size() * 2, v32;
            uint16_t v16;
            fwrite("RIFF", 1, 4, f); v32 = 36 + bytes; fwrite(&v32, 4, 1, f);
            fwrite("WAVEfmt ", 1, 8, f); v32 = 16; fwrite(&v32, 4, 1, f);
            v16 = 1; fwrite(&v16, 2, 1, f); v16 = 1; fwrite(&v16, 2, 1, f);
            v32 = 16000; fwrite(&v32, 4, 1, f); v32 = 32000; fwrite(&v32, 4, 1, f);
            v16 = 2; fwrite(&v16, 2, 1, f); v16 = 16; fwrite(&v16, 2, 1, f);
            fwrite("data", 1, 4, f); fwrite(&bytes, 4, 1, f);
            fwrite(rec.data(), 2, rec.size(), f);
            fclose(f);
        };
        // processed first: the canceller has run from the start, as it does in the client
        take("processed.wav", 4, 500, noise);
        sv_set_processing(0, 0, 0, 0);
        take("raw.wav", 1, 1000, sweep);
        char stats[2048];
        sv_stats(stats, sizeof stats);
        printf("{\"event\":\"done\",\"stats\":%s}\n", stats);
        sv_audio_stop();
        sv_shutdown();
        return 0;
    }
    if (argc < 5 || strcmp(argv[1], "run")) {
        fprintf(stderr, "usage: sv_loopback devices | open [CAPTURE RENDER] | listen OUTPUT [SECONDS [FREQ]] | switch A B [LEVEL_DB] | chirp CAPTURE RENDER DIR | run IN.wav OUT.wav SECONDS [--mute-at S] "
                        "[--raw|--no-aec] [--gate-at S --gate-for S]\n");
        return 2;
    }
    std::string in = argv[2], out = argv[3];
    double seconds = atof(argv[4]), mute_at = -1, gate_at = -1, gate_for = 0;
    bool raw = false, no_aec = false;
    for (int i = 5; i < argc; i++) {
        if (!strcmp(argv[i], "--mute-at") && i + 1 < argc) mute_at = atof(argv[++i]);
        else if (!strcmp(argv[i], "--gate-at") && i + 1 < argc) gate_at = atof(argv[++i]);
        else if (!strcmp(argv[i], "--gate-for") && i + 1 < argc) gate_for = atof(argv[++i]);
        else if (!strcmp(argv[i], "--raw")) raw = true;
        else if (!strcmp(argv[i], "--no-aec")) no_aec = true;
    }
    if (sv_init(OnLog, OnEvent) != 0) return 1;
    if (raw) sv_set_processing(0, 0, 0, 0);
    // in a loopback the playout is the mic delayed, the reverse of an echo, which AEC3 would suppress
    if (no_aec) sv_set_processing(0, 4, 1, 1);
    sv_set_tap(1);

    // Connect first, then start the audio, so the mic file's start isn't lost to the handshake.
    pc_a = sv_pc_new("");
    DWORD t0 = GetTickCount();
    while ((connected < 2 || data_open < 2) && GetTickCount() - t0 < 10000) {
        Drain();
        Sleep(5);
    }
    printf("{\"event\":\"connected\",\"ms\":%lu,\"peers\":%d,\"data\":%d}\n", GetTickCount() - t0, connected.load(),
           data_open.load());
    if (connected < 2) {
        sv_shutdown();
        return 1;
    }
    sv_pc_mute_send(pc_a, 0);  // a sends the mic; b stays disabled, so the playout is only what b received
    sv_set_mute(0, 0);
    sv_audio_start(("file:" + in).c_str(), ("file:" + out).c_str());

    DWORD start = GetTickCount();
    bool muted = false, gated = false, ungated = false;
    while ((GetTickCount() - start) / 1000.0 < seconds) {
        double t = (GetTickCount() - start) / 1000.0;
        if (mute_at >= 0 && t >= mute_at && !muted) {
            sv_set_mute(1, 100);
            muted = true;
            printf("{\"event\":\"muted\",\"t\":%.3f}\n", t);
        }
        if (gate_at >= 0 && t >= gate_at && !gated) {
            sv_set_gate(1, 0);
            gated = true;
            printf("{\"event\":\"gate\",\"t\":%.3f}\n", t);
        }
        if (gated && !ungated && t >= gate_at + gate_for) {
            sv_set_gate(0, 250);
            ungated = true;
            printf("{\"event\":\"ungate\",\"t\":%.3f}\n", t);
        }
        Drain();
        int16_t tapped[1600];
        tap_frames += sv_tap_read(tapped, 1600);
        Sleep(10);
    }
    char stats[1024];
    sv_stats(stats, sizeof stats);
    sv_audio_stop();
    {
        int16_t tapped[1600];
        int n;
        while ((n = sv_tap_read(tapped, 1600)) > 0) tap_frames += n;
    }
    Drain();
    printf("{\"event\":\"done\",\"data_a\":%d,\"data_b\":%d,\"tap_frames\":%lld,\"stats\":%s}\n", got_a.load(),
           got_b.load(), tap_frames.load(), stats);
    fflush(stdout);
    sv_pc_close(pc_a);
    sv_pc_close(pc_b);
    sv_shutdown();
    return 0;
}
