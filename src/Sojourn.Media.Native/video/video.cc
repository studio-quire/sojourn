// sojourn_video: Sojourn's video and audio-only media (mp4, HLS, RTSP, MP3...) through libVLC, as the viewer's
// media_plugin_libvlc plays them. Written by Claude Opus 5.5, 2026-10-26.
//
// The client starts this process at low integrity in a job of its own (no children, kill on close): libVLC parses
// untrusted streams with nothing like Chromium's sandbox around it, so the process is kept from writing to the
// person's files or driving their windows. It reads only the URLs the client passes (never file:), decodes into the
// surface's shared section (BGRA frames, float stereo sound; common/protocol.h) and reports its state on the pipe.
#include <windows.h>

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <deque>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "../common/channel.h"
#include "../common/urls.h"

// ---- the part of libVLC 3.0's C API we use, bound at run time ----
extern "C" {
typedef struct libvlc_instance_t libvlc_instance_t;
typedef struct libvlc_media_t libvlc_media_t;
typedef struct libvlc_media_player_t libvlc_media_player_t;
typedef void* (*libvlc_video_lock_cb)(void* opaque, void** planes);
typedef void (*libvlc_video_unlock_cb)(void* opaque, void* picture, void* const* planes);
typedef void (*libvlc_video_display_cb)(void* opaque, void* picture);
typedef unsigned (*libvlc_video_format_cb)(void** opaque, char* chroma, unsigned* width, unsigned* height, unsigned* pitches, unsigned* lines);
typedef void (*libvlc_video_cleanup_cb)(void* opaque);
typedef void (*libvlc_audio_play_cb)(void* data, const void* samples, unsigned count, int64_t pts);
typedef void (*libvlc_audio_pause_cb)(void* data, int64_t pts);
typedef void (*libvlc_audio_resume_cb)(void* data, int64_t pts);
typedef void (*libvlc_audio_flush_cb)(void* data, int64_t pts);
typedef void (*libvlc_audio_drain_cb)(void* data);
typedef struct libvlc_log_t libvlc_log_t;
typedef void (*libvlc_log_cb)(void* data, int level, const libvlc_log_t* ctx, const char* fmt, va_list args);
}

namespace {

struct Vlc {
    libvlc_instance_t* (*new_)(int, const char* const*);
    void (*release)(libvlc_instance_t*);
    void (*set_user_agent)(libvlc_instance_t*, const char*, const char*);
    libvlc_media_t* (*media_new_location)(libvlc_instance_t*, const char*);
    void (*media_add_option)(libvlc_media_t*, const char*);
    void (*media_release)(libvlc_media_t*);
    char* (*media_get_meta)(libvlc_media_t*, int);
    void (*free_)(void*);
    libvlc_media_player_t* (*player_new_from_media)(libvlc_media_t*);
    void (*player_release)(libvlc_media_player_t*);
    int (*play)(libvlc_media_player_t*);
    void (*set_pause)(libvlc_media_player_t*, int);
    void (*stop)(libvlc_media_player_t*);
    int64_t (*get_time)(libvlc_media_player_t*);
    void (*set_time)(libvlc_media_player_t*, int64_t);
    int64_t (*get_length)(libvlc_media_player_t*);
    int (*get_state)(libvlc_media_player_t*);
    void (*video_set_callbacks)(libvlc_media_player_t*, libvlc_video_lock_cb, libvlc_video_unlock_cb, libvlc_video_display_cb, void*);
    void (*video_set_format_callbacks)(libvlc_media_player_t*, libvlc_video_format_cb, libvlc_video_cleanup_cb);
    void (*audio_set_callbacks)(libvlc_media_player_t*, libvlc_audio_play_cb, libvlc_audio_pause_cb, libvlc_audio_resume_cb,
                                libvlc_audio_flush_cb, libvlc_audio_drain_cb, void*);
    void (*audio_set_format)(libvlc_media_player_t*, const char*, unsigned, unsigned);
    libvlc_media_t* (*player_get_media)(libvlc_media_player_t*);
    void (*log_set)(libvlc_instance_t*, libvlc_log_cb, void*);
    int (*video_get_size)(libvlc_media_player_t*, unsigned, unsigned*, unsigned*);
    int64_t (*clock)(void);
};

Vlc g_vlc;
libvlc_instance_t* g_inst = nullptr;
sm::Pipe g_pipe;
std::atomic<bool> g_quit{false};
std::string g_ua = "Sojourn";
bool g_private_ok = false;

template <typename T>
bool Bind(HMODULE m, T& fn, const char* name) {
    fn = (T)GetProcAddress(m, name);
    return fn != nullptr;
}

bool LoadVlc(const std::wstring& dir) {
    SetDllDirectoryW(dir.c_str());
    HMODULE core = LoadLibraryExW((dir + L"\\libvlccore.dll").c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    HMODULE m = LoadLibraryExW((dir + L"\\libvlc.dll").c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    if (!core || !m) return false;
    bool ok = true;
    ok &= Bind(m, g_vlc.new_, "libvlc_new");
    ok &= Bind(m, g_vlc.release, "libvlc_release");
    ok &= Bind(m, g_vlc.set_user_agent, "libvlc_set_user_agent");
    ok &= Bind(m, g_vlc.media_new_location, "libvlc_media_new_location");
    ok &= Bind(m, g_vlc.media_add_option, "libvlc_media_add_option");
    ok &= Bind(m, g_vlc.media_release, "libvlc_media_release");
    ok &= Bind(m, g_vlc.media_get_meta, "libvlc_media_get_meta");
    ok &= Bind(m, g_vlc.free_, "libvlc_free");
    ok &= Bind(m, g_vlc.player_new_from_media, "libvlc_media_player_new_from_media");
    ok &= Bind(m, g_vlc.player_release, "libvlc_media_player_release");
    ok &= Bind(m, g_vlc.play, "libvlc_media_player_play");
    ok &= Bind(m, g_vlc.set_pause, "libvlc_media_player_set_pause");
    ok &= Bind(m, g_vlc.stop, "libvlc_media_player_stop");
    ok &= Bind(m, g_vlc.get_time, "libvlc_media_player_get_time");
    ok &= Bind(m, g_vlc.set_time, "libvlc_media_player_set_time");
    ok &= Bind(m, g_vlc.get_length, "libvlc_media_player_get_length");
    ok &= Bind(m, g_vlc.get_state, "libvlc_media_player_get_state");
    ok &= Bind(m, g_vlc.video_set_callbacks, "libvlc_video_set_callbacks");
    ok &= Bind(m, g_vlc.video_set_format_callbacks, "libvlc_video_set_format_callbacks");
    ok &= Bind(m, g_vlc.audio_set_callbacks, "libvlc_audio_set_callbacks");
    ok &= Bind(m, g_vlc.audio_set_format, "libvlc_audio_set_format");
    ok &= Bind(m, g_vlc.player_get_media, "libvlc_media_player_get_media");
    ok &= Bind(m, g_vlc.log_set, "libvlc_log_set");
    ok &= Bind(m, g_vlc.video_get_size, "libvlc_video_get_size");
    ok &= Bind(m, g_vlc.clock, "libvlc_clock");
    return ok;
}

void Send(const sm::Json& j) { g_pipe.Send(j); }

// libVLC's own messages at warning and above (or everything, with --sojourn-vlc-debug) as log events
int g_log_level = 3;
void VlcLog(void*, int level, const libvlc_log_t*, const char* fmt, va_list args) {
    if (level < g_log_level) return;
    char buf[1024];
    vsnprintf(buf, sizeof buf, fmt, args);
    sm::Json j = sm::Json::Object();
    j.Set("ev", "log").Set("level", level).Set("msg", std::string(buf));
    Send(j);
}

const char* StateName(int s) {
    switch (s) {
        case 1: return "opening";
        case 2: return "buffering";
        case 3: return "playing";
        case 4: return "paused";
        case 5: return "stopped";
        case 6: return "ended";
        case 7: return "error";
        default: return "idle";
    }
}

// ---------------------------------------------------------------------------------------------------------------

class Player {
public:
    Player(int id, uint32_t maxw, uint32_t maxh) : id_(id), maxw_(maxw), maxh_(maxh) {}
    ~Player() { Close(); }

    sm::Surface shm;
    std::atomic<bool> audio{true};

    bool Open(const std::string& url, bool loop) {
        libvlc_media_t* m = g_vlc.media_new_location(g_inst, url.c_str());
        if (!m) return false;
        if (loop) g_vlc.media_add_option(m, ":input-repeat=65535");
        g_vlc.media_add_option(m, ":no-sub-autodetect-file");
        mp_ = g_vlc.player_new_from_media(m);
        media_ = m;
        if (!mp_) return false;
        g_vlc.video_set_format_callbacks(mp_, &Player::Format, &Player::Cleanup);
        g_vlc.video_set_callbacks(mp_, &Player::Lock, &Player::Unlock, &Player::Display, this);
        // amem hands over signed 16-bit whatever format is asked for (seen: "FL32" came as S16), so ask for that and
        // make floats here
        g_vlc.audio_set_format(mp_, "S16N", 48000, 2);
        g_vlc.audio_set_callbacks(mp_, &Player::Play, &Player::Flush, nullptr, &Player::Flush, nullptr, this);
        pacing_ = true;
        pacer_ = std::thread([this] { Pace(); });
        return g_vlc.play(mp_) == 0;
    }

    void Close() {
        if (mp_) {
            g_vlc.stop(mp_);
            g_vlc.player_release(mp_);
            mp_ = nullptr;
        }
        pacing_ = false;
        if (pacer_.joinable()) pacer_.join();
        if (media_) {
            g_vlc.media_release(media_);
            media_ = nullptr;
        }
        shm.Close();
    }

    void Command(const sm::Json& c) {
        std::string op = c.Str("op");
        if (!mp_) return;
        if (op == "play") {
            int s = g_vlc.get_state(mp_);
            if (s == 4) g_vlc.set_pause(mp_, 0);
            else if (s == 5 || s == 6 || s == 7 || s == 0) g_vlc.play(mp_);
        } else if (op == "pause") g_vlc.set_pause(mp_, 1);
        else if (op == "stop") g_vlc.stop(mp_);
        else if (op == "seek") g_vlc.set_time(mp_, (int64_t)c.Num("ms"));
        else if (op == "audio") audio = c.Flag("on", true);
    }

    // state, size and time to the client, twice a second when something changed
    void Report() {
        if (!mp_) return;
        int s = g_vlc.get_state(mp_);
        int64_t t = g_vlc.get_time(mp_), len = g_vlc.get_length(mp_);
        bool changed = s != state_ || std::llabs(t - reported_t_) >= 1000 || len != len_ || w_ != reported_w_ || h_ != reported_h_;
        if (!changed) return;
        state_ = s;
        reported_t_ = t;
        len_ = len;
        reported_w_ = w_;
        reported_h_ = h_;
        sm::Json j = sm::Json::Object();
        j.Set("ev", "state").Set("id", id_).Set("state", StateName(s)).Set("ms", (double)t).Set("length", (double)len)
            .Set("w", (int)(vis_w_ ? vis_w_ : w_)).Set("h", (int)(vis_h_ ? vis_h_ : h_));
        if (!title_sent_ && s == 3 && media_) {
            title_sent_ = true;
            if (char* title = g_vlc.media_get_meta(media_, 0)) {
                j.Set("title", std::string(title));
                g_vlc.free_(title);
            }
        }
        Send(j);
    }

private:
    // the decoded size, capped at the section's (keeping the aspect: the client's renderer scales it onto the face)
    static unsigned Format(void** opaque, char* chroma, unsigned* width, unsigned* height, unsigned* pitches, unsigned* lines) {
        Player* p = (Player*)*opaque;
        unsigned w = *width, h = *height;
        if (w == 0 || h == 0) { w = 640; h = 360; }
        double s = std::min(1.0, std::min((double)p->maxw_ / w, (double)p->maxh_ / h));
        w = std::max(2u, (unsigned)(w * s) & ~1u);
        h = std::max(2u, (unsigned)(h * s) & ~1u);
        memcpy(chroma, "RV32", 4);
        *width = w;
        *height = h;
        pitches[0] = w * 4;
        lines[0] = h;
        p->w_ = w;
        p->h_ = h;
        p->scale_ = s;
        p->vis_w_ = p->vis_h_ = 0;
        p->frame_.assign((size_t)w * h * 4, 0);
        return 1;
    }
    static void Cleanup(void*) {}
    static void* Lock(void* opaque, void** planes) {
        Player* p = (Player*)opaque;
        planes[0] = p->frame_.data();
        return nullptr;
    }
    static void Unlock(void* opaque, void*, void* const*) {
        // RV32's fourth byte is padding: made opaque so the face isn't drawn see-through
        Player* p = (Player*)opaque;
        uint32_t* px = (uint32_t*)p->frame_.data();
        size_t n = (size_t)p->w_ * p->h_;
        for (size_t i = 0; i < n; i++) px[i] |= 0xFF000000u;
    }
    // the buffer can be taller than the picture (the decoder's padded rows): only the visible part is published
    static void Display(void* opaque, void*) {
        Player* p = (Player*)opaque;
        if (p->vis_w_ == 0 && p->mp_) {
            unsigned vw = 0, vh = 0;
            if (g_vlc.video_get_size(p->mp_, 0, &vw, &vh) == 0 && vw > 0 && vh > 0) {
                p->vis_w_ = std::max(2u, std::min(p->w_, (unsigned)(vw * p->scale_ + 0.5) & ~1u));
                p->vis_h_ = std::max(2u, std::min(p->h_, (unsigned)(vh * p->scale_ + 0.5) & ~1u));
            }
        }
        unsigned w = p->vis_w_ ? p->vis_w_ : p->w_, h = p->vis_h_ ? p->vis_h_ : p->h_;
        p->shm.Publish(p->frame_.data(), (int)w, (int)h, (int)p->w_ * 4, 0, 0, 0, 0);
    }
    // libVLC hands sound over well ahead of its time (its network caching, a second or so): it's held here and
    // passed to the client's ring when due, so the mixer's queue stays short and the sound stays with the picture
    static void Play(void* data, const void* samples, unsigned count, int64_t pts) {
        Player* p = (Player*)data;
        if (!p->audio) { p->shm.Alive(); return; }
        const int16_t* in = (const int16_t*)samples;
        std::vector<float> pcm((size_t)count * 2);
        for (size_t i = 0; i < pcm.size(); i++) pcm[i] = in[i] * (1.0f / 32768.0f);
        std::lock_guard<std::mutex> lk(p->amu_);
        p->aq_.push_back({pts, std::move(pcm)});
    }
    // a seek or a pause: what was held is dropped (after a pause its times are past)
    static void Flush(void* data, int64_t) {
        Player* p = (Player*)data;
        std::lock_guard<std::mutex> lk(p->amu_);
        p->aq_.clear();
    }
    void Pace() {
        while (pacing_) {
            int64_t now = g_vlc.clock();
            for (;;) {
                std::vector<float> due;
                {
                    std::lock_guard<std::mutex> lk(amu_);
                    if (aq_.empty() || aq_.front().pts > now + 40000) break;   // 40 ms ahead: the mixer's prebuffer
                    due = std::move(aq_.front().pcm);
                    aq_.pop_front();
                }
                shm.WriteAudio(due.data(), (int)(due.size() / 2), 2, 48000);
            }
            Sleep(5);
        }
    }

    int id_;
    uint32_t maxw_, maxh_;
    libvlc_media_player_t* mp_ = nullptr;
    libvlc_media_t* media_ = nullptr;
    std::vector<uint8_t> frame_;
    struct Held { int64_t pts; std::vector<float> pcm; };
    std::mutex amu_;
    std::deque<Held> aq_;
    std::atomic<bool> pacing_{false};
    std::thread pacer_;
    unsigned w_ = 0, h_ = 0, vis_w_ = 0, vis_h_ = 0;
    double scale_ = 1;
    int state_ = -1;
    int64_t reported_t_ = -100000, len_ = -2;
    unsigned reported_w_ = 0, reported_h_ = 0;
    bool title_sent_ = false;
};

std::mutex g_mu;
std::map<int, std::unique_ptr<Player>> g_players;

void Handle(const std::string& line) {
    sm::Json c = sm::Json::Parse(line);
    std::string op = c.Str("op");
    int id = c.Int("id");
    if (op == "quit") { g_quit = true; return; }
    if (op == "open") {
        std::string url = c.Str("url");
        // the client checks too; this process won't open anything but the network's media, wherever the URL came from
        sm::Url u = sm::ParseUrl(url);
        static const char* schemes[] = {"http", "https", "rtsp", "rtmp", "rtmps", "mms", "mmsh", "rtp", "udp"};
        bool scheme_ok = false;
        for (auto s : schemes) scheme_ok |= u.ok && u.scheme == s;
        std::string why = !scheme_ok ? "scheme" : (!g_private_ok && sm::PrivateHost(u.host)) ? "private address" : "";
        if (!why.empty()) {
            sm::Json j = sm::Json::Object();
            j.Set("ev", "blocked").Set("id", id).Set("url", url).Set("why", why);
            Send(j);
            return;
        }
        auto p = std::make_unique<Player>(id, (uint32_t)c.Int("maxw", 1920), (uint32_t)c.Int("maxh", 1080));
        if (!p->shm.Open(c.Str("shm"))) {
            sm::Json j = sm::Json::Object();
            j.Set("ev", "error").Set("id", id).Set("text", "the surface's section didn't open");
            Send(j);
            return;
        }
        p->audio = c.Flag("audio", true);
        bool ok = p->Open(url, c.Flag("loop"));
        std::lock_guard<std::mutex> lk(g_mu);
        g_players[id] = std::move(p);
        if (!ok) {
            sm::Json j = sm::Json::Object();
            j.Set("ev", "error").Set("id", id).Set("text", "libVLC didn't start it");
            Send(j);
        }
        return;
    }
    std::unique_ptr<Player> closing;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        auto it = g_players.find(id);
        if (it == g_players.end()) return;
        if (op == "close") {
            closing = std::move(it->second);
            g_players.erase(it);
        } else it->second->Command(c);
    }
    if (closing) {
        closing->Close();   // outside the lock: stopping waits for libVLC's threads
        sm::Json j = sm::Json::Object();
        j.Set("ev", "closed").Set("id", id);
        Send(j);
    }
}

std::wstring Arg(const std::wstring& cmd, const std::wstring& name) {
    std::wstring key = L"--" + name + L"=";
    size_t at = cmd.find(key);
    if (at == std::wstring::npos) return L"";
    at += key.size();
    bool quoted = at < cmd.size() && cmd[at] == L'"';
    if (quoted) at++;
    size_t end = cmd.find(quoted ? L'"' : L' ', at);
    return cmd.substr(at, end == std::wstring::npos ? std::wstring::npos : end - at);
}

}  // namespace

int WINAPI wWinMain(HINSTANCE, HINSTANCE, LPWSTR, int) {
    std::wstring cmd = GetCommandLineW();
    std::wstring pipe = Arg(cmd, L"sojourn-pipe"), vlc = Arg(cmd, L"sojourn-vlc");
    std::string ua = sm::Narrow(Arg(cmd, L"sojourn-ua"));
    if (!ua.empty()) g_ua = ua;
    g_private_ok = cmd.find(L"--sojourn-private-ok") != std::wstring::npos;
    if (pipe.empty() || vlc.empty()) return 2;
    if (!g_pipe.Connect(pipe)) return 3;
    if (!LoadVlc(vlc)) {
        sm::Json j = sm::Json::Object();
        j.Set("ev", "fatal").Set("text", "libVLC didn't load from " + sm::Narrow(vlc));
        Send(j);
        return 4;
    }
    // quiet, no windows of its own, no lookups beyond the stream, no subtitles from files beside it
    const char* args[] = {"--quiet", "--no-video-title-show", "--no-osd", "--no-stats", "--no-snapshot-preview", "--no-spu",
                          "--no-metadata-network-access", "--no-sub-autodetect-file", "--no-media-library", "--http-reconnect",
                          "--network-caching=1000", "--no-lua", "--ignore-config"};
    g_inst = g_vlc.new_((int)(sizeof args / sizeof args[0]), args);
    if (!g_inst) {
        // an older libVLC may not know one of these: try with the essentials only
        const char* basic[] = {"--quiet", "--no-video-title-show", "--ignore-config"};
        g_inst = g_vlc.new_(3, basic);
    }
    if (!g_inst) {
        sm::Json j = sm::Json::Object();
        j.Set("ev", "fatal").Set("text", "libvlc_new failed");
        Send(j);
        return 5;
    }
    g_vlc.set_user_agent(g_inst, g_ua.c_str(), g_ua.c_str());
    if (cmd.find(L"--sojourn-vlc-debug") != std::wstring::npos) g_log_level = 0;
    g_vlc.log_set(g_inst, &VlcLog, nullptr);
    g_pipe.Start([](const std::string& line) { Handle(line); }, [] { g_quit = true; });
    sm::Json ready = sm::Json::Object();
    ready.Set("ev", "ready");
    Send(ready);
    while (!g_quit) {
        Sleep(250);
        std::lock_guard<std::mutex> lk(g_mu);
        for (auto& kv : g_players) {
            kv.second->Report();
            kv.second->shm.Alive();
        }
    }
    std::map<int, std::unique_ptr<Player>> all;
    {
        std::lock_guard<std::mutex> lk(g_mu);
        all.swap(g_players);
    }
    all.clear();
    g_vlc.release(g_inst);
    return 0;
}
