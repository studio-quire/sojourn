// The helpers' side of the protocol (protocol.h): the control pipe and a surface's shared section.
// Written by Claude Opus 5.5, 2026-10-26. Header-only; both helpers include it once.
#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cstring>
#include <functional>
#include <mutex>
#include <string>
#include <thread>

#include "json.h"
#include "protocol.h"

namespace sm {

inline std::wstring Wide(const std::string& s) {
    if (s.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), nullptr, 0);
    std::wstring w(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), &w[0], n);
    return w;
}

inline std::string Narrow(const std::wstring& w) {
    if (w.empty()) return "";
    int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), nullptr, 0, nullptr, nullptr);
    std::string s(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), &s[0], n, nullptr, nullptr);
    return s;
}

inline int64_t NowMs() { return (int64_t)GetTickCount64(); }

// The control pipe: lines in on a thread of its own, lines out from any thread.
class Pipe {
public:
    bool Connect(const std::wstring& name, int wait_ms = 10000) {
        int64_t until = NowMs() + wait_ms;
        for (;;) {
            // overlapped: a handle without it serialises I/O, so a write would wait behind the reader's pending read
            h_ = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
            if (h_ != INVALID_HANDLE_VALUE) return true;
            if (NowMs() > until) return false;
            if (GetLastError() == ERROR_PIPE_BUSY) WaitNamedPipeW(name.c_str(), 500);
            else Sleep(100);
        }
    }

    // Calls on_line for each line until the pipe closes, then on_closed. Runs on its own thread.
    void Start(std::function<void(const std::string&)> on_line, std::function<void()> on_closed) {
        reader_ = std::thread([this, on_line, on_closed] {
            std::string buf;
            char chunk[65536];
            OVERLAPPED ov{};
            ov.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            for (;;) {
                DWORD got = 0;
                ResetEvent(ov.hEvent);
                if (!ReadFile(h_, chunk, sizeof chunk, nullptr, &ov) && GetLastError() != ERROR_IO_PENDING) break;
                if (!GetOverlappedResult(h_, &ov, &got, TRUE) || got == 0) break;
                buf.append(chunk, got);
                size_t at;
                while ((at = buf.find('\n')) != std::string::npos) {
                    std::string line = buf.substr(0, at);
                    buf.erase(0, at + 1);
                    if (!line.empty() && line.back() == '\r') line.pop_back();
                    if (!line.empty()) on_line(line);
                }
                if (buf.size() > (16u << 20)) buf.clear();   // a runaway line: drop it
            }
            CloseHandle(ov.hEvent);
            on_closed();
        });
        reader_.detach();
    }

    void Send(const Json& j) {
        std::string line = j.Dump();
        line += '\n';
        std::lock_guard<std::mutex> lk(mu_);
        if (!wev_) wev_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        OVERLAPPED ov{};
        ov.hEvent = wev_;
        ResetEvent(wev_);
        DWORD wrote = 0;
        if (!WriteFile(h_, line.data(), (DWORD)line.size(), nullptr, &ov) && GetLastError() != ERROR_IO_PENDING) return;
        GetOverlappedResult(h_, &ov, &wrote, TRUE);
    }

private:
    HANDLE h_ = INVALID_HANDLE_VALUE;
    HANDLE wev_ = nullptr;
    std::thread reader_;
    std::mutex mu_;
};

// A surface's shared section, from the writing side.
class Surface {
public:
    ~Surface() { Close(); }

    bool Open(const std::string& name) {
        map_ = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, Wide(name).c_str());
        if (!map_) return false;
        base_ = (uint8_t*)MapViewOfFile(map_, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, 0);
        if (!base_) return false;
        MEMORY_BASIC_INFORMATION mbi{};
        VirtualQuery(base_, &mbi, sizeof mbi);
        size_ = mbi.RegionSize;
        h_ = (Header*)base_;
        if (h_->magic != kMagic || h_->version != kVersion) return false;
        uint64_t need = Layout(nullptr, h_->max_width, h_->max_height, h_->audio_capacity);
        if (need > size_) return false;
        return true;
    }

    void Close() {
        if (base_) UnmapViewOfFile(base_);
        if (map_) CloseHandle(map_);
        base_ = nullptr;
        map_ = nullptr;
        h_ = nullptr;
    }

    bool Ok() const { return h_ != nullptr; }
    uint32_t MaxWidth() const { return h_ ? h_->max_width : 0; }
    uint32_t MaxHeight() const { return h_ ? h_->max_height : 0; }

    void Alive() {
        if (h_) h_->alive_ms = NowMs();
    }

    // Publishes a frame: rows of `stride` bytes, BGRA. The dirty rect is this frame's own change (the frame itself, if
    // dw <= 0); what the client gets is everything changed since the frame it last consumed.
    void Publish(const uint8_t* bgra, int w, int h, int stride, int dx, int dy, int dw, int dh, int64_t pts_us = 0) {
        if (!h_ || !bgra) return;
        w = std::min<int>(w, (int)h_->max_width);
        h = std::min<int>(h, (int)h_->max_height);
        if (w <= 0 || h <= 0) return;
        if (w != last_w_ || h != last_h_) {
            // a new size: the whole frame is new
            dx = dy = 0;
            dw = w;
            dh = h;
            hist_n_ = 0;
            last_w_ = w;
            last_h_ = h;
        }
        if (dw <= 0 || dh <= 0) { dx = dy = 0; dw = w; dh = h; }
        int64_t seq = h_->seq + 1;
        // remember this frame's own change, then the union of every change after the consumed frame
        hist_[hist_n_ % kHist] = {seq, dx, dy, dw, dh};
        hist_n_++;
        int64_t consumed = h_->consumed_seq;
        int ux0 = INT32_MAX, uy0 = INT32_MAX, ux1 = INT32_MIN, uy1 = INT32_MIN;
        bool covered = false;
        int64_t oldest_kept = INT64_MAX;
        for (int i = 0; i < std::min(hist_n_, kHist); i++) {
            const Dirty& d = hist_[i];
            oldest_kept = std::min(oldest_kept, d.seq);
            if (d.seq <= consumed) continue;
            ux0 = std::min(ux0, d.x);
            uy0 = std::min(uy0, d.y);
            ux1 = std::max(ux1, d.x + d.w);
            uy1 = std::max(uy1, d.y + d.h);
            covered = true;
        }
        // the client is further behind than the history reaches (or never consumed): send it all
        if (!covered || oldest_kept > consumed + 1) { ux0 = 0; uy0 = 0; ux1 = w; uy1 = h; }
        ux0 = std::max(0, ux0);
        uy0 = std::max(0, uy0);
        ux1 = std::min(w, ux1);
        uy1 = std::min(h, uy1);

        int latest = h_->latest, reading = h_->reading;
        int b = 0;
        while (b == latest || b == reading) b++;
        uint8_t* dst = base_ + h_->frame_offset[b];
        size_t dst_stride = (size_t)h_->max_width * 4;
        size_t row = (size_t)w * 4;
        for (int y = 0; y < h; y++) memcpy(dst + y * dst_stride, bgra + (size_t)y * stride, row);
        FrameInfo& fi = h_->info[b];
        fi.seq = seq;
        fi.width = w;
        fi.height = h;
        fi.dx = ux0;
        fi.dy = uy0;
        fi.dw = ux1 - ux0;
        fi.dh = uy1 - uy0;
        fi.pts_us = pts_us;
        MemoryBarrier();
        h_->seq = seq;
        InterlockedExchange((volatile LONG*)&h_->latest, b);
        Alive();
    }

    // Interleaved stereo float at `rate`; mono is spread to both sides. Drops what doesn't fit.
    void WriteAudio(const float* pcm, int frames, int channels, int rate) {
        if (!h_ || !pcm || frames <= 0 || h_->audio_capacity == 0) return;
        h_->audio_rate = rate;
        uint32_t cap = h_->audio_capacity;
        int64_t w = h_->audio_write, r = h_->audio_read;
        int64_t room = (int64_t)cap - (w - r);
        if (room <= 0) return;
        int n = (int)std::min<int64_t>(frames, room);
        float* ring = (float*)(base_ + h_->audio_offset);
        for (int i = 0; i < n; i++) {
            size_t at = (size_t)((w + i) % cap) * 2;
            if (channels >= 2) {
                ring[at] = pcm[(size_t)i * channels];
                ring[at + 1] = pcm[(size_t)i * channels + 1];
            } else {
                ring[at] = ring[at + 1] = pcm[i];
            }
        }
        MemoryBarrier();
        h_->audio_write = w + n;
    }

    // Planar input (CEF's packets): one pointer per channel.
    void WriteAudioPlanar(const float** planes, int frames, int channels, int rate) {
        if (!h_ || !planes || frames <= 0 || h_->audio_capacity == 0) return;
        h_->audio_rate = rate;
        uint32_t cap = h_->audio_capacity;
        int64_t w = h_->audio_write, r = h_->audio_read;
        int64_t room = (int64_t)cap - (w - r);
        if (room <= 0) return;
        int n = (int)std::min<int64_t>(frames, room);
        float* ring = (float*)(base_ + h_->audio_offset);
        for (int i = 0; i < n; i++) {
            size_t at = (size_t)((w + i) % cap) * 2;
            float l = planes[0][i], rr = channels >= 2 ? planes[1][i] : l;
            ring[at] = l;
            ring[at + 1] = rr;
        }
        MemoryBarrier();
        h_->audio_write = w + n;
    }

private:
    struct Dirty {
        int64_t seq;
        int x, y, w, h;
    };
    static constexpr int kHist = 16;
    HANDLE map_ = nullptr;
    uint8_t* base_ = nullptr;
    size_t size_ = 0;
    Header* h_ = nullptr;
    Dirty hist_[kHist] = {};
    int hist_n_ = 0;
    int last_w_ = 0, last_h_ = 0;
};

}  // namespace sm
