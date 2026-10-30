// Sojourn's media helpers (sojourn_web: CEF; sojourn_video: libVLC) and the client share one surface protocol.
// Written by Claude Opus 5.5, 2026-10-26. The C# mirror is src/Sojourn.Client/Media/MediaShm.cs: keep them in step.
//
// Control: JSON lines over a named pipe the client creates (\\.\pipe\sojourn-media-<client pid>-<kind>); the helper
// connects as its client. One object per line, both ways. The client is the parent; the helper exits when the pipe
// closes.
//
// Frames and sound: one shared section per surface, created by the client (it sizes it, labels it for a low-integrity
// helper, and owns its lifetime), opened by name by the helper:
//
//   [Header, 4096 bytes][frame 0][frame 1][frame 2][audio ring]
//
// Frames: three BGRA8 buffers (stride = max_width * 4), a triple buffer. The helper writes a whole frame into a buffer
// that is neither `latest` nor `reading`, fills its FrameInfo, then publishes it by setting `latest`. The client takes
// `latest` into `reading` (rechecking `latest` after, until they agree), copies the rows FrameInfo.dirty covers, sets
// `consumed_seq` to the frame's seq and `reading` to -1. The dirty rect of a published frame covers every change
// since the frame the client last consumed, so a client that skips frames still copies everything that changed.
//
// Sound: interleaved float stereo at `audio_rate`, a single-producer single-consumer ring of `audio_capacity` frames.
// `audio_write` and `audio_read` count frames ever written and read; the helper drops what doesn't fit (the client
// reads it every 10 ms from the audio thread, so a full ring means nobody is listening).
#pragma once
#include <stdint.h>

namespace sm {

constexpr uint32_t kMagic = 0x444D4A53;   // "SJMD"
constexpr uint32_t kVersion = 1;
constexpr uint32_t kHeaderBytes = 4096;
constexpr int kBuffers = 3;

#pragma pack(push, 8)
struct FrameInfo {
    int64_t seq;          // the publish counter when this buffer was published
    int32_t width, height;
    int32_t dx, dy, dw, dh;   // dirty since the client's last consumed frame (whole frame: 0, 0, width, height)
    int64_t pts_us;       // the media's time of this frame, if known (video), else 0
};

struct Header {
    uint32_t magic, version;
    uint32_t max_width, max_height;
    uint64_t frame_offset[kBuffers];   // from the section's start
    uint64_t audio_offset;
    uint32_t audio_capacity;          // frames (stereo float)
    int32_t audio_rate;               // set by the helper before writing sound (48000 asked for)
    // helper -> client
    volatile int64_t seq;
    volatile int32_t latest;          // -1: nothing yet
    int32_t pad0;
    FrameInfo info[kBuffers];
    volatile int64_t audio_write;
    // client -> helper
    volatile int32_t reading;         // -1: none held
    int32_t pad1;
    volatile int64_t consumed_seq;
    volatile int64_t audio_read;
    // helper -> client: a heartbeat (ms since the helper started), so the client can tell a stalled writer
    volatile int64_t alive_ms;
};
#pragma pack(pop)

static_assert(sizeof(Header) <= kHeaderBytes, "header too big");

inline uint64_t FrameBytes(uint32_t w, uint32_t h) { return (uint64_t)w * h * 4; }
inline uint64_t Align(uint64_t v, uint64_t a) { return (v + a - 1) / a * a; }

// The layout the client makes for a surface of at most w x h with an audio ring of `audio_frames`.
inline uint64_t Layout(Header* h, uint32_t w, uint32_t hgt, uint32_t audio_frames) {
    uint64_t at = kHeaderBytes;
    for (int i = 0; i < kBuffers; i++) {
        if (h) h->frame_offset[i] = at;
        at = Align(at + FrameBytes(w, hgt), 65536);
    }
    if (h) {
        h->audio_offset = at;
        h->audio_capacity = audio_frames;
        h->max_width = w;
        h->max_height = hgt;
    }
    return at + (uint64_t)audio_frames * 2 * sizeof(float);
}

}  // namespace sm
