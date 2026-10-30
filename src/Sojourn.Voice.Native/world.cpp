// World audio: sounds placed in the world (objects, gestures, footsteps, UI), the parcel's music stream, and the
// decoders for internet radio (MP3 and AAC through Media Foundation). Written by Claude Opus 5.5, 2026-10-25.
//
// Everything here is mixed by the render thread into the device mix (engine.cpp's RenderFrame), so it reaches the
// echo canceller's reference with the rest and never the mic. The client (Sojourn.Client/Audio) runs SL's rules:
// which sources get one of the channels, their gains and positions; this file only plays.
//
// Spatial channels go through a spherical-head model (Brown & Duda 1998): per ear an interaural delay and a head
// shadow shelf from the angle between the source and that ear, plus a mild broadband level difference. Distance
// follows the viewer's OpenAL setup: inverse distance clamped at 1 m, rolloff 1 (5 under water). The listener's
// pose comes straight from the render loop, so the head turning is heard within a device buffer.
#include "internal.h"
#include "../Sojourn.Media.Native/common/protocol.h"

#include <mfapi.h>
#include <mftransform.h>
#include <mferror.h>
#include <wmcodecdsp.h>
#include <mmreg.h>

#include <cmath>
#include <map>
#include <memory>

namespace sv {

// ---------------------------------------------------------------------------------------------------------------
// A streaming windowed-sinc resampler (Kaiser, 32 taps, 256 interpolated phases), interleaved float.

class SincResampler {
public:
    SincResampler(int in_rate, int out_rate, int channels) : in_rate_(in_rate), out_rate_(out_rate), ch_(channels) {
        step_ = (double)in_rate / out_rate;
        double fc = 0.97 * std::min(1.0, (double)out_rate / in_rate);
        table_.resize((size_t)(kPhases + 1) * kTaps);
        auto bessel0 = [](double x) {
            double s = 1, t = 1;
            for (int k = 1; k < 30; k++) {
                t *= (x / (2 * k)) * (x / (2 * k));
                s += t;
            }
            return s;
        };
        const double beta = 8.0, i0b = bessel0(beta);
        for (int ph = 0; ph <= kPhases; ph++) {
            double frac = (double)ph / kPhases, sum = 0;
            float* row = &table_[(size_t)ph * kTaps];
            for (int m = 0; m < kTaps; m++) {
                double x = frac + (kHalf - 1) - m;  // distance from the output point to tap m
                double r = x / kHalf;
                double w = std::fabs(r) >= 1 ? 0 : bessel0(beta * std::sqrt(1 - r * r)) / i0b;
                double s = x == 0 ? 1 : std::sin(3.14159265358979 * fc * x) / (3.14159265358979 * fc * x);
                row[m] = (float)(fc * s * w);
                sum += row[m];
            }
            for (int m = 0; m < kTaps; m++) row[m] = (float)(row[m] / sum);
        }
        hist_.assign((size_t)kHalf * ch_, 0.0f);  // zeros before the first frame, so output starts with no delay
        t_ = kHalf;
    }
    int in_rate() const { return in_rate_; }
    int channels() const { return ch_; }

    void Process(const float* in, int frames, std::vector<float>& out) {
        if (in_rate_ == out_rate_) {
            out.insert(out.end(), in, in + (size_t)frames * ch_);
            return;
        }
        hist_.insert(hist_.end(), in, in + (size_t)frames * ch_);
        Run(out);
    }
    // the last input frames' tail (zero padding)
    void Flush(std::vector<float>& out) {
        if (in_rate_ == out_rate_) return;
        hist_.insert(hist_.end(), (size_t)kHalf * ch_, 0.0f);
        Run(out);
    }

private:
    static constexpr int kHalf = 16, kTaps = 2 * kHalf, kPhases = 256;
    void Run(std::vector<float>& out) {
        int64_t have = (int64_t)(hist_.size() / ch_);
        while ((int64_t)std::floor(t_) + kHalf < have) {
            int64_t i0 = (int64_t)std::floor(t_);
            double frac = t_ - i0;
            double p = frac * kPhases;
            int ph = (int)p;
            float a = (float)(p - ph);
            const float* r0 = &table_[(size_t)ph * kTaps];
            const float* r1 = r0 + kTaps;
            const float* x = &hist_[(size_t)(i0 - kHalf + 1) * ch_];
            for (int c = 0; c < ch_; c++) {
                float s = 0;
                for (int m = 0; m < kTaps; m++) s += x[(size_t)m * ch_ + c] * (r0[m] + a * (r1[m] - r0[m]));
                out.push_back(s);
            }
            t_ += step_;
        }
        int64_t drop = (int64_t)std::floor(t_) - kHalf + 1;
        if (drop > 0) {
            hist_.erase(hist_.begin(), hist_.begin() + (size_t)drop * ch_);
            t_ -= (double)drop;
        }
    }
    int in_rate_, out_rate_, ch_;
    double step_, t_;
    std::vector<float> table_, hist_;
};

// ---------------------------------------------------------------------------------------------------------------
// Sounds and channels.

struct Buf {
    std::vector<float> data;  // 48 kHz interleaved
    int frames = 0, channels = 1;
};

constexpr int kChannels = 32;
constexpr int kRing = 64;                     // per-channel input history for the ears' delays
constexpr double kHeadRadius = 0.0875, kSoundSpeed = 343.0;
const float kHeadDelay = (float)(kHeadRadius / kSoundSpeed * kRate);   // ~12.2 samples
const float kShelfTK = (float)(kHeadRadius / (2 * kSoundSpeed) * 2 * kRate);   // tau * K of the shelf, ~12.2

struct Ear {
    float delay, alpha, gain;
};

struct Chan {
    std::shared_ptr<Buf> buf, next;
    int buf_id = 0, next_id = 0;
    int64_t pos = 0;
    bool active = false, loop = false, spatial = true, waiting = false, stopping = false;
    int master = -1;
    int loops = 0;
    int generation = 0;
    bool looped = false;  // this frame
    float gain = 0, gain_now = 0;
    double x = 0, y = 0, z = 0;
    // spatial state
    float ring[kRing] = {};
    int w = 0;
    bool fresh = true;
    Ear now[2] = {};
    float fx[2] = {}, fy[2] = {};  // the shelves' last input and output
};

struct Music {
    std::vector<float> ring;  // stereo
    size_t head = 0, count = 0;
    std::unique_ptr<SincResampler> rs;
    float gain = 0, gain_now = 0;
    bool playing = false;
    int preroll_ms = 1500;
    int64_t underruns = 0;
    std::vector<float> scratch;
};

struct World {
    std::mutex mu;
    std::map<int, std::shared_ptr<Buf>> bufs;
    int next_buf = 1;
    Chan ch[kChannels];
    double lx = 0, ly = 0, lz = 0;
    float fwd[3] = {1, 0, 0}, left[3] = {0, 1, 0}, up[3] = {0, 0, 1};
    bool have_listener = false;
    float rolloff = 1;
    float bus = 1, bus_now = 1;
    float env = 0;  // the limiter's envelope
    Music music;
    std::mutex music_mu;
    // stats
    std::atomic<int64_t> frames{0}, mix_ticks{0}, mix_max_ticks{0};
    std::atomic<float> mix_us_recent{0};   // about the last second
    std::atomic<int> active{0};
};

static World* W() {
    static World* w = new World();
    return w;
}

static int64_t Qpc() {
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart;
}
static double QpcUs(int64_t ticks) {
    static int64_t freq = [] {
        LARGE_INTEGER f;
        QueryPerformanceFrequency(&f);
        return (int64_t)f.QuadPart;
    }();
    return ticks * 1e6 / (double)freq;
}

// The ears' parameters for a source at rel (listener frame: x forward, y left, z up), metres.
static void Ears(const float rel[3], float rolloff, Ear out[2]) {
    float d = std::sqrt(rel[0] * rel[0] + rel[1] * rel[1] + rel[2] * rel[2]);
    float dist_gain = 1.0f / (1.0f + rolloff * (std::max(d, 1.0f) - 1.0f));
    float v[3] = {1, 0, 0};
    if (d > 1e-4f) {
        v[0] = rel[0] / d;
        v[1] = rel[1] / d;
        v[2] = rel[2] / d;
    }
    // inside the head the direction means nothing: fade the cues out below half a metre
    float near_w = std::min(1.0f, std::max(0.0f, (d - 0.1f) / 0.4f));
    // Brown & Duda's head shadow with the ears on the interaural axis, scaled so a source straight ahead is flat
    // (the shelf gives 0.756 there); the pinnae's shadow for sources behind, up to -5 dB in the highs, which the
    // sphere alone lacks and which tells behind from ahead
    const float amin = 0.1f, theta_min = 150.0f * 3.14159265f / 180.0f, front_norm = 1.0f / 0.7564f;
    float rear = 1.0f - 0.45f * std::max(0.0f, -v[0]);
    for (int e = 0; e < 2; e++) {
        float side = e == 0 ? 1.0f : -1.0f;  // 0 left (+y), 1 right
        float c = std::min(1.0f, std::max(-1.0f, v[1] * side));
        float theta = std::acos(c);  // 0: straight at this ear
        float alpha = ((1 + amin / 2) + (1 - amin / 2) * std::cos(theta / theta_min * 3.14159265f)) * front_norm * rear;
        float delay = theta < 1.5707963f ? -std::cos(theta) : (theta - 1.5707963f);
        delay = kHeadDelay * (1 + delay);  // 0 .. ~31 samples, kHeadDelay when the source is on the median plane
        float ild = std::pow(10.0f, 1.5f * c / 20.0f);
        out[e].alpha = 1 + near_w * (alpha - 1);
        out[e].delay = kHeadDelay + near_w * (delay - kHeadDelay);
        out[e].gain = dist_gain * (1 + near_w * (ild - 1));
    }
}

static inline float RingAt(const Chan& c, float back) {
    // cubic (Hermite) read 'back' samples behind the newest
    float p = (float)c.w - back;
    int i = (int)std::floor(p);
    float t = p - i;
    auto at = [&](int k) { return c.ring[((k % kRing) + kRing) % kRing]; };
    float xm1 = at(i - 1), x0 = at(i), x1 = at(i + 1), x2 = at(i + 2);
    float c1 = 0.5f * (x1 - xm1), c2 = xm1 - 2.5f * x0 + 2 * x1 - 0.5f * x2, c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
    return ((c3 * t + c2) * t + c1) * t + x0;
}

// Mixes one channel into bus (stereo, kFrame). Called with the world lock held.
static void MixChan(World* w, int idx, float* bus) {
    Chan& c = w->ch[idx];
    c.looped = false;
    if (!c.active || c.waiting || !c.buf) return;
    float target = c.stopping ? 0.0f : c.gain;
    float g0 = c.fresh ? target : c.gain_now;
    if (c.fresh && c.stopping) g0 = 0;
    float dg = (target - g0) / kFrame;

    Ear to[2];
    if (c.spatial) {
        float rel[3] = {0, 0, 0};
        if (w->have_listener) {
            float d[3] = {(float)(c.x - w->lx), (float)(c.y - w->ly), (float)(c.z - w->lz)};
            for (int k = 0; k < 3; k++) {
                const float* ax = k == 0 ? w->fwd : k == 1 ? w->left : w->up;
                rel[k] = d[0] * ax[0] + d[1] * ax[1] + d[2] * ax[2];
            }
        }
        Ears(rel, w->rolloff, to);
        if (c.fresh) {
            c.now[0] = to[0];
            c.now[1] = to[1];
        }
    }

    bool done = false;
    for (int n = 0; n < kFrame; n++) {
        float l = 0, r = 0;
        if (!done) {
            if (c.pos >= c.buf->frames) {
                if (c.next) {
                    c.buf = c.next;
                    c.buf_id = c.next_id;
                    c.next.reset();
                    c.next_id = 0;
                    c.pos = 0;
                    c.generation++;
                } else if (c.loop && c.buf->frames > 0) {
                    c.pos = 0;
                    c.loops++;
                    c.looped = true;
                } else {
                    done = true;
                }
            }
            if (!done) {
                const float* s = &c.buf->data[(size_t)c.pos * c.buf->channels];
                if (c.buf->channels == 2) {
                    l = s[0];
                    r = s[1];
                } else {
                    l = r = s[0];
                }
                c.pos++;
            }
        }
        float g = g0 + dg * (n + 1);
        if (!c.spatial) {
            bus[2 * n] += l * g;
            bus[2 * n + 1] += r * g;
            continue;
        }
        // spatial: the mono source through each ear's delay and shelf
        c.w = (c.w + 1) % kRing;
        c.ring[c.w] = 0.5f * (l + r);
        float f = (n + 1) / (float)kFrame;
        for (int e = 0; e < 2; e++) {
            float delay = c.now[e].delay + f * (to[e].delay - c.now[e].delay);
            float alpha = c.now[e].alpha + f * (to[e].alpha - c.now[e].alpha);
            float eg = c.now[e].gain + f * (to[e].gain - c.now[e].gain);
            float x = RingAt(c, delay + 2.0f);  // 2 samples of base latency keep the cubic read in the past
            const float k = kShelfTK, inv = 1.0f / (1 + k);
            float b0 = (1 + alpha * k) * inv, b1 = (1 - alpha * k) * inv, a1 = (1 - k) * inv;
            float y = b0 * x + b1 * c.fx[e] - a1 * c.fy[e];
            c.fx[e] = x;
            c.fy[e] = y;
            bus[2 * n + e] += y * eg * g;
        }
    }
    if (c.spatial) {
        c.now[0] = to[0];
        c.now[1] = to[1];
    }
    c.gain_now = target;
    c.fresh = false;
    if (c.stopping || (done && !c.next)) {
        c.active = false;
        c.stopping = false;
        c.buf.reset();
        c.next.reset();
        c.buf_id = c.next_id = 0;
    }
}

// ---------------------------------------------------------------------------------------------------------------
// Media sound (docs/media.md): each playing web page or video writes float stereo into a ring in its surface's shared
// section (Sojourn.Media.Native/common/protocol.h); the mix reads it here every 10 ms. Placed, its left and right
// play from the face's two sides through the same head model as world sounds, but without distance loss: loudness
// is the client's (the viewer's media volume by distance), so it matches what others hear in the same place.

struct EarPair {
    float ring[kRing] = {};
    int w = 0;
    bool fresh = true;
    Ear now[2] = {};
    float fx[2] = {}, fy[2] = {};
};

constexpr int kMediaStreams = 16;

struct MediaStream {
    sm::Header* h = nullptr;
    const float* ring = nullptr;
    float gain = 0, gain_now = 0;
    bool spatial = false;
    double pos[2][3] = {};
    EarPair ears[2];
    std::unique_ptr<SincResampler> rs;
    int rs_rate = 0;
    std::vector<float> in, out;   // scratch: ring frames read, resampled
    std::vector<float> pending;   // resampled, not yet mixed
    bool playing = false;
    int64_t underruns = 0, dropped = 0;
};

struct Media {
    std::mutex mu;
    MediaStream s[kMediaStreams];
};

static Media* M() {
    static Media* m = new Media();
    return m;
}

// One sample of a mono source through a pair of ears heading to `to` (interpolated from `ep.now` across the frame).
static inline void EarSample(EarPair& ep, const Ear to[2], float x_in, float f, float g, float* out2) {
    ep.w = (ep.w + 1) % kRing;
    ep.ring[ep.w] = x_in;
    for (int e = 0; e < 2; e++) {
        float delay = ep.now[e].delay + f * (to[e].delay - ep.now[e].delay);
        float alpha = ep.now[e].alpha + f * (to[e].alpha - ep.now[e].alpha);
        float eg = ep.now[e].gain + f * (to[e].gain - ep.now[e].gain);
        float p = (float)ep.w - (delay + 2.0f);
        int i = (int)std::floor(p);
        float t = p - i;
        auto at = [&](int k) { return ep.ring[((k % kRing) + kRing) % kRing]; };
        float xm1 = at(i - 1), x0 = at(i), x1 = at(i + 1), x2 = at(i + 2);
        float c1 = 0.5f * (x1 - xm1), c2 = xm1 - 2.5f * x0 + 2 * x1 - 0.5f * x2, c3 = 0.5f * (x2 - xm1) + 1.5f * (x0 - x1);
        float x = ((c3 * t + c2) * t + c1) * t + x0;
        const float k = kShelfTK, inv = 1.0f / (1 + k);
        float b0 = (1 + alpha * k) * inv, b1 = (1 - alpha * k) * inv, a1 = (1 - k) * inv;
        float y = b0 * x + b1 * ep.fx[e] - a1 * ep.fy[e];
        ep.fx[e] = x;
        ep.fy[e] = y;
        out2[e] += y * eg * g;
    }
}

// A point (global metres) in the listener's frame, for Ears().
static void Rel(World* w, const double p[3], float rel[3]) {
    float d[3] = {(float)(p[0] - w->lx), (float)(p[1] - w->ly), (float)(p[2] - w->lz)};
    for (int k = 0; k < 3; k++) {
        const float* ax = k == 0 ? w->fwd : k == 1 ? w->left : w->up;
        rel[k] = d[0] * ax[0] + d[1] * ax[1] + d[2] * ax[2];
    }
}

static void MixMedia(World* w, float* bus) {
    Media* m = M();
    std::lock_guard<std::mutex> lk(m->mu);
    for (int si = 0; si < kMediaStreams; si++) {
        MediaStream& st = m->s[si];
        if (!st.h) continue;
        sm::Header* h = st.h;
        int64_t wr = h->audio_write, rd = h->audio_read;
        uint32_t cap = h->audio_capacity;
        int rate = h->audio_rate > 0 ? h->audio_rate : kRate;
        int64_t avail = wr - rd;
        if (avail < 0 || avail > (int64_t)cap) { rd = wr; avail = 0; }
        // the helper's clock and the device's drift apart, and a stall leaves a backlog: past 250 ms queued, skip to 80 ms
        int64_t queued_ms = avail * 1000 / rate + (int64_t)(st.pending.size() / 2) * 1000 / kRate;
        if (queued_ms > 250) {
            int64_t keep = (int64_t)rate * 80 / 1000;
            st.dropped += avail - keep;
            rd = wr - keep;
            avail = keep;
            st.pending.clear();
        }
        if (!st.playing) {
            if (queued_ms >= 60) st.playing = true;
            else { h->audio_read = rd; continue; }
        }
        if (!st.rs || st.rs_rate != rate) {
            st.rs = std::make_unique<SincResampler>(rate, kRate, 2);
            st.rs_rate = rate;
            st.pending.clear();
        }
        // enough resampled frames for this 10 ms
        int need_in = (int)((int64_t)kFrame * rate / kRate) + 8;
        while ((int)(st.pending.size() / 2) < kFrame && avail > 0) {
            int n = (int)std::min<int64_t>(avail, need_in);
            st.in.resize((size_t)n * 2);
            for (int i = 0; i < n; i++) {
                size_t at = (size_t)((rd + i) % cap) * 2;
                st.in[2 * i] = st.ring[at];
                st.in[2 * i + 1] = st.ring[at + 1];
            }
            rd += n;
            avail -= n;
            st.out.clear();
            st.rs->Process(st.in.data(), n, st.out);
            st.pending.insert(st.pending.end(), st.out.begin(), st.out.end());
        }
        h->audio_read = rd;
        int have = (int)(st.pending.size() / 2);
        int n = std::min(have, kFrame);
        if (n < kFrame) { st.playing = false; st.underruns++; }
        float g0 = st.gain_now, dg = (st.gain - g0) / kFrame;
        Ear to[2][2];
        if (st.spatial) {
            for (int c = 0; c < 2; c++) {
                float rel[3] = {0, 0, 0};
                if (w->have_listener) Rel(w, st.pos[c], rel);
                Ears(rel, 0.0f, to[c]);   // no distance loss: the client sets the loudness
                if (st.ears[c].fresh) {
                    st.ears[c].now[0] = to[c][0];
                    st.ears[c].now[1] = to[c][1];
                    st.ears[c].fresh = false;
                }
            }
        }
        for (int i = 0; i < n; i++) {
            // what a page or stream sends is untrusted: anything not a sample (NaN, infinities, wild values) is silence,
            // so it can't stick in the ears' filters or past the gain
            float l = st.pending[2 * i], r = st.pending[2 * i + 1];
            l = std::isfinite(l) ? std::min(4.0f, std::max(-4.0f, l)) : 0.0f;
            r = std::isfinite(r) ? std::min(4.0f, std::max(-4.0f, r)) : 0.0f;
            float g = g0 + dg * (i + 1);
            if (!st.spatial) {
                bus[2 * i] += l * g;
                bus[2 * i + 1] += r * g;
            } else {
                float f = (i + 1) / (float)kFrame;
                EarSample(st.ears[0], to[0], l, f, g, &bus[2 * i]);
                EarSample(st.ears[1], to[1], r, f, g, &bus[2 * i]);
            }
        }
        if (st.spatial)
            for (int c = 0; c < 2; c++) {
                st.ears[c].now[0] = to[c][0];
                st.ears[c].now[1] = to[c][1];
            }
        st.gain_now = st.gain;
        st.pending.erase(st.pending.begin(), st.pending.begin() + (size_t)n * 2);
    }
}

void WorldMix(float* stereo) {
    World* w = W();
    int64_t t0 = Qpc();
    float bus[kFrame * 2] = {};
    int active = 0;
    {
        std::lock_guard<std::mutex> lk(w->mu);
        for (int i = 0; i < kChannels; i++) {
            if (w->ch[i].active) active++;
            MixChan(w, i, bus);
        }
        // sync slaves start when their master loops, at the master's offset (the viewer's playSynced)
        for (int i = 0; i < kChannels; i++) {
            Chan& c = w->ch[i];
            if (!c.active || !c.waiting) continue;
            int m = c.master;
            bool master_gone = m < 0 || m >= kChannels || !w->ch[m].active || w->ch[m].waiting;
            if (master_gone || w->ch[m].looped) {
                c.waiting = false;
                c.pos = master_gone || !c.buf || c.buf->frames == 0 ? 0 : w->ch[m].pos % c.buf->frames;
            }
        }
    }
    w->active.store(active);
    MixMedia(w, bus);
    {
        std::lock_guard<std::mutex> lk(w->music_mu);
        Music& m = w->music;
        float dg = (m.gain - m.gain_now) / kFrame;
        size_t cap = m.ring.size() / 2;
        if (!m.playing && cap > 0 && m.count * 1000 / kRate >= (size_t)m.preroll_ms) m.playing = true;
        if (m.playing) {
            for (int n = 0; n < kFrame; n++) {
                if (m.count == 0) {
                    m.playing = false;
                    m.underruns++;
                    break;
                }
                float g = m.gain_now + dg * (n + 1);
                bus[2 * n] += m.ring[2 * m.head] * g;
                bus[2 * n + 1] += m.ring[2 * m.head + 1] * g;
                m.head = (m.head + 1) % cap;
                m.count--;
            }
        }
        m.gain_now = m.gain;
    }
    // the bus gain (a fade around teleports), then a limiter so a crowd of sounds can't clip the device mix
    float b0 = w->bus_now, db = (w->bus - b0) / kFrame;
    const float thr = 0.89f, rel = 0.99979f;  // ~100 ms release
    float env = w->env;
    for (int n = 0; n < kFrame; n++) {
        float g = b0 + db * (n + 1);
        float l = bus[2 * n] * g, r = bus[2 * n + 1] * g;
        float peak = std::max(std::fabs(l), std::fabs(r));
        env = peak > env ? peak : env * rel + peak * (1 - rel);
        float lim = env > thr ? thr / env : 1.0f;
        stereo[2 * n] += l * lim;
        stereo[2 * n + 1] += r * lim;
    }
    w->env = env;
    w->bus_now = w->bus;
    int64_t dt = Qpc() - t0;
    w->frames++;
    w->mix_ticks += dt;
    w->mix_us_recent.store(w->mix_us_recent.load() * 0.99f + (float)QpcUs(dt) * 0.01f);
    if (dt > w->mix_max_ticks.load()) w->mix_max_ticks.store(dt);
}

// ---------------------------------------------------------------------------------------------------------------
// Media Foundation decoders for internet radio: MP3 and AAC (ADTS), the formats SL's music streams use.

struct Decoder {
    IMFTransform* mft = nullptr;
    int kind = 0;
    int out_rate = 0, out_ch = 0;
    bool out_float = false;
    bool provides_samples = false;
    DWORD out_size = 0;
    std::vector<float> out;
};

static std::mutex g_dec_mu;
static std::map<int64_t, std::unique_ptr<Decoder>> g_decs;
static int64_t g_next_dec = 1;

static bool PickOutput(Decoder* d) {
    for (DWORD i = 0;; i++) {
        IMFMediaType* t = nullptr;
        if (FAILED(d->mft->GetOutputAvailableType(0, i, &t))) break;
        GUID sub{};
        t->GetGUID(MF_MT_SUBTYPE, &sub);
        UINT32 bits = 0;
        t->GetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, &bits);
        bool fl = sub == MFAudioFormat_Float && bits == 32;
        bool pcm16 = sub == MFAudioFormat_PCM && bits == 16;
        if ((fl || pcm16) && SUCCEEDED(d->mft->SetOutputType(0, t, 0))) {
            UINT32 rate = 0, ch = 0;
            t->GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, &rate);
            t->GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, &ch);
            d->out_rate = (int)rate;
            d->out_ch = (int)ch;
            d->out_float = fl;
            t->Release();
            if (fl) break;  // float is what we want; PCM16 only if there's no float
            continue;
        }
        t->Release();
    }
    if (d->out_rate <= 0) return false;
    MFT_OUTPUT_STREAM_INFO info{};
    d->mft->GetOutputStreamInfo(0, &info);
    d->provides_samples = (info.dwFlags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
    d->out_size = std::max<DWORD>(info.cbSize, 1 << 16);
    return true;
}

static void Drain(Decoder* d) {
    for (;;) {
        MFT_OUTPUT_DATA_BUFFER ob{};
        IMFSample* sample = nullptr;
        if (!d->provides_samples) {
            IMFMediaBuffer* mb = nullptr;
            if (FAILED(MFCreateSample(&sample)) || FAILED(MFCreateMemoryBuffer(d->out_size, &mb))) {
                if (sample) sample->Release();
                return;
            }
            sample->AddBuffer(mb);
            mb->Release();
            ob.pSample = sample;
        }
        DWORD status = 0;
        HRESULT hr = d->mft->ProcessOutput(0, 1, &ob, &status);
        if (ob.pEvents) ob.pEvents->Release();
        if (hr == MF_E_TRANSFORM_STREAM_CHANGE) {
            if (sample) sample->Release();
            d->out_rate = 0;
            if (!PickOutput(d)) return;
            continue;
        }
        if (FAILED(hr)) {  // MF_E_TRANSFORM_NEED_MORE_INPUT, or an error
            if (sample) sample->Release();
            return;
        }
        IMFSample* got = ob.pSample;
        IMFMediaBuffer* mb = nullptr;
        if (got && SUCCEEDED(got->ConvertToContiguousBuffer(&mb))) {
            BYTE* p = nullptr;
            DWORD len = 0;
            if (SUCCEEDED(mb->Lock(&p, nullptr, &len))) {
                if (d->out_float) {
                    const float* f = (const float*)p;
                    d->out.insert(d->out.end(), f, f + len / 4);
                } else {
                    const int16_t* s = (const int16_t*)p;
                    for (DWORD i = 0; i < len / 2; i++) d->out.push_back(s[i] / 32768.0f);
                }
                mb->Unlock();
            }
            mb->Release();
        }
        if (got) got->Release();
    }
}

}  // namespace sv

using namespace sv;

// ---- sounds and channels ----

SV_API int sv_buf_load(const float* pcm, int frames, int rate, int channels) {
    if (!pcm || frames <= 0 || rate < 4000 || rate > 192000 || (channels != 1 && channels != 2)) return 0;
    auto b = std::make_shared<Buf>();
    b->channels = channels;
    SincResampler rs(rate, kRate, channels);
    b->data.reserve((size_t)((double)frames * kRate / rate + 64) * channels);
    rs.Process(pcm, frames, b->data);
    rs.Flush(b->data);
    b->frames = (int)(b->data.size() / channels);
    // the flush pads past the end; keep the length the sound had
    int want = (int)std::llround((double)frames * kRate / rate);
    if (b->frames > want) {
        b->frames = want;
        b->data.resize((size_t)want * channels);
    }
    World* w = W();
    std::lock_guard<std::mutex> lk(w->mu);
    int id = w->next_buf++;
    w->bufs[id] = b;
    return id;
}

SV_API void sv_buf_free(int id) {
    World* w = W();
    std::lock_guard<std::mutex> lk(w->mu);
    w->bufs.erase(id);  // channels playing it keep their reference until they're done
}

SV_API void sv_listener(double x, double y, double z, float fx, float fy, float fz, float ux, float uy, float uz) {
    float f[3] = {fx, fy, fz}, u[3] = {ux, uy, uz};
    float fl = std::sqrt(f[0] * f[0] + f[1] * f[1] + f[2] * f[2]);
    if (fl < 1e-6f) return;
    for (float& v : f) v /= fl;
    // up made perpendicular to forward; left = up x forward (SL's frame: x forward, y left, z up)
    float dot = u[0] * f[0] + u[1] * f[1] + u[2] * f[2];
    for (int k = 0; k < 3; k++) u[k] -= dot * f[k];
    float ul = std::sqrt(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]);
    if (ul < 1e-6f) {
        u[0] = 0;
        u[1] = 0;
        u[2] = 1;
        dot = f[2];
        for (int k = 0; k < 3; k++) u[k] -= dot * f[k];
        ul = std::sqrt(u[0] * u[0] + u[1] * u[1] + u[2] * u[2]);
        if (ul < 1e-6f) return;
    }
    for (float& v : u) v /= ul;
    float l[3] = {u[1] * f[2] - u[2] * f[1], u[2] * f[0] - u[0] * f[2], u[0] * f[1] - u[1] * f[0]};
    World* w = W();
    std::lock_guard<std::mutex> lk(w->mu);
    w->lx = x;
    w->ly = y;
    w->lz = z;
    for (int k = 0; k < 3; k++) {
        w->fwd[k] = f[k];
        w->up[k] = u[k];
        w->left[k] = l[k];
    }
    w->have_listener = true;
}

SV_API void sv_world_set(float bus_gain, float rolloff) {
    World* w = W();
    std::lock_guard<std::mutex> lk(w->mu);
    w->bus = std::max(0.0f, bus_gain);
    w->rolloff = std::max(0.0f, rolloff);
}

// flags: 1 loop, 2 spatial. master >= 0: a sync slave, waiting for that channel to loop.
SV_API int sv_ch_play(int ch, int buf, int flags, float gain, double x, double y, double z, int master) {
    if (ch < 0 || ch >= kChannels) return -1;
    World* w = W();
    std::lock_guard<std::mutex> lk(w->mu);
    auto it = w->bufs.find(buf);
    if (it == w->bufs.end()) return -1;
    Chan& c = w->ch[ch];
    bool was = c.active && !c.stopping;
    c.buf = it->second;
    c.buf_id = buf;
    c.next.reset();
    c.next_id = 0;
    c.pos = 0;
    c.loop = (flags & 1) != 0;
    bool spatial = (flags & 2) != 0;
    if (spatial != c.spatial || !was) {
        memset(c.ring, 0, sizeof c.ring);
        c.fx[0] = c.fx[1] = c.fy[0] = c.fy[1] = 0;
        c.fresh = true;
    }
    c.spatial = spatial;
    c.gain = std::max(0.0f, gain);
    if (!was) c.gain_now = c.gain;
    c.x = x;
    c.y = y;
    c.z = z;
    c.master = master;
    c.waiting = master >= 0 && master < kChannels && master != ch;
    c.active = true;
    c.stopping = false;
    c.loops = 0;
    c.generation++;
    return c.generation;
}

SV_API void sv_ch_queue(int ch, int buf) {
    if (ch < 0 || ch >= kChannels) return;
    World* w = W();
    std::lock_guard<std::mutex> lk(w->mu);
    Chan& c = w->ch[ch];
    auto it = w->bufs.find(buf);
    if (!c.active || it == w->bufs.end()) return;
    c.next = it->second;
    c.next_id = buf;
}

SV_API void sv_ch_set(int ch, float gain, double x, double y, double z, int loop) {
    if (ch < 0 || ch >= kChannels) return;
    World* w = W();
    std::lock_guard<std::mutex> lk(w->mu);
    Chan& c = w->ch[ch];
    c.gain = std::max(0.0f, gain);
    c.x = x;
    c.y = y;
    c.z = z;
    if (loop >= 0) c.loop = loop != 0;
}

SV_API void sv_ch_stop(int ch) {
    if (ch < 0 || ch >= kChannels) return;
    World* w = W();
    std::lock_guard<std::mutex> lk(w->mu);
    Chan& c = w->ch[ch];
    if (!c.active) return;
    if (c.waiting) {
        c.active = false;
        c.buf.reset();
        c.next.reset();
        c.buf_id = c.next_id = 0;
        return;
    }
    c.stopping = true;  // ramps to silence over the next 10 ms
}

// Per channel, 4 ints: state (0 idle, 1 playing, 2 waiting for its master), the sound playing, loops so far, the
// play's generation (sv_ch_play's return; a queued sound starting adds one).
SV_API void sv_ch_states(int* out, int n) {
    World* w = W();
    std::lock_guard<std::mutex> lk(w->mu);
    for (int i = 0; i < kChannels && i < n; i++) {
        const Chan& c = w->ch[i];
        out[4 * i] = !c.active || c.stopping ? 0 : c.waiting ? 2 : 1;
        out[4 * i + 1] = c.buf_id;
        out[4 * i + 2] = c.loops;
        out[4 * i + 3] = c.generation;
    }
}

// ---- the music stream ----

SV_API void sv_music_push(const float* pcm, int frames, int rate, int channels) {
    if (!pcm || frames <= 0 || rate <= 0 || (channels != 1 && channels != 2)) return;
    World* w = W();
    std::lock_guard<std::mutex> lk(w->music_mu);
    Music& m = w->music;
    if (m.ring.empty()) m.ring.assign((size_t)kRate * 2 * 20, 0.0f);  // 20 s
    if (!m.rs || m.rs->in_rate() != rate || m.rs->channels() != channels)
        m.rs = std::make_unique<SincResampler>(rate, kRate, channels);
    m.scratch.clear();
    m.rs->Process(pcm, frames, m.scratch);
    size_t cap = m.ring.size() / 2;
    size_t out_frames = m.scratch.size() / channels;
    for (size_t i = 0; i < out_frames; i++) {
        float l = m.scratch[i * channels], r = channels == 2 ? m.scratch[i * channels + 1] : l;
        if (m.count == cap) {  // full: drop the oldest
            m.head = (m.head + 1) % cap;
            m.count--;
        }
        size_t at = (m.head + m.count) % cap;
        m.ring[2 * at] = l;
        m.ring[2 * at + 1] = r;
        m.count++;
    }
}

SV_API void sv_music_gain(float gain) {
    World* w = W();
    std::lock_guard<std::mutex> lk(w->music_mu);
    w->music.gain = std::max(0.0f, gain);
}

SV_API void sv_music_clear(void) {
    World* w = W();
    std::lock_guard<std::mutex> lk(w->music_mu);
    w->music.head = w->music.count = 0;
    w->music.playing = false;
    w->music.rs.reset();
}

SV_API void sv_music_preroll(int ms) {
    World* w = W();
    std::lock_guard<std::mutex> lk(w->music_mu);
    w->music.preroll_ms = std::max(0, ms);
}

// queued ms, or -1 while it's buffering (not yet playing)
SV_API int sv_music_queued_ms(void) {
    World* w = W();
    std::lock_guard<std::mutex> lk(w->music_mu);
    int ms = (int)(w->music.count * 1000 / kRate);
    return w->music.playing ? ms : -1 - ms;
}

// ---- decoders ----

// kind 1 MP3, 2 AAC in ADTS; rate and channels from the stream's first frame header. Returns a handle > 0, or 0.
SV_API int64_t sv_dec_open(int kind, int rate, int channels) {
    static std::once_flag mf_once;
    static bool mf_ok = false;
    HRESULT co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    (void)co;
    std::call_once(mf_once, [] { mf_ok = SUCCEEDED(MFStartup(MF_VERSION, MFSTARTUP_LITE)); });
    if (!mf_ok) return 0;
    auto d = std::make_unique<Decoder>();
    d->kind = kind;
    const CLSID& clsid = kind == 1 ? CLSID_CMP3DecMediaObject : CLSID_CMSAACDecMFT;
    if (FAILED(CoCreateInstance(clsid, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&d->mft)))) {
        Log(2, kind == 1 ? "music: no MP3 decoder" : "music: no AAC decoder");
        return 0;
    }
    IMFMediaType* in = nullptr;
    MFCreateMediaType(&in);
    in->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
    in->SetGUID(MF_MT_SUBTYPE, kind == 1 ? MFAudioFormat_MP3 : MFAudioFormat_AAC);
    in->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, (UINT32)rate);
    in->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, (UINT32)channels);
    if (kind == 2) {
        in->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        in->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 1);  // ADTS
        in->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0xFE);
        // HEAACWAVEINFO's fields after the WAVEFORMATEX: payload type 1 (ADTS), profile 0xFE, no AudioSpecificConfig
        BYTE user[12] = {1, 0, 0xFE, 0, 0, 0, 0, 0, 0, 0, 0, 0};
        in->SetBlob(MF_MT_USER_DATA, user, sizeof user);
    }
    HRESULT hr = d->mft->SetInputType(0, in, 0);
    in->Release();
    if (FAILED(hr) || !PickOutput(d.get())) {
        char b[96];
        snprintf(b, sizeof b, "music: decoder setup failed 0x%08lx", (unsigned long)hr);
        Log(2, b);
        d->mft->Release();
        return 0;
    }
    d->mft->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0);
    d->mft->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0);
    std::lock_guard<std::mutex> lk(g_dec_mu);
    int64_t h = g_next_dec++;
    g_decs[h] = std::move(d);
    return h;
}

static Decoder* FindDec(int64_t h) {
    std::lock_guard<std::mutex> lk(g_dec_mu);
    auto it = g_decs.find(h);
    return it == g_decs.end() ? nullptr : it->second.get();
}

// Compressed bytes in, any amount. 0 ok, < 0 a decoder error.
SV_API int sv_dec_feed(int64_t h, const uint8_t* data, int n) {
    Decoder* d = FindDec(h);
    if (!d || !data || n <= 0) return -1;
    IMFSample* s = nullptr;
    IMFMediaBuffer* mb = nullptr;
    if (FAILED(MFCreateSample(&s)) || FAILED(MFCreateMemoryBuffer((DWORD)n, &mb))) {
        if (s) s->Release();
        return -2;
    }
    BYTE* p = nullptr;
    mb->Lock(&p, nullptr, nullptr);
    memcpy(p, data, n);
    mb->Unlock();
    mb->SetCurrentLength((DWORD)n);
    s->AddBuffer(mb);
    mb->Release();
    HRESULT hr = d->mft->ProcessInput(0, s, 0);
    if (hr == MF_E_NOTACCEPTING) {
        Drain(d);
        hr = d->mft->ProcessInput(0, s, 0);
    }
    s->Release();
    Drain(d);
    return SUCCEEDED(hr) ? 0 : -3;
}

// Decoded audio out: up to max interleaved samples; *rate and *channels say what they are.
SV_API int sv_dec_read(int64_t h, float* out, int max, int* rate, int* channels) {
    Decoder* d = FindDec(h);
    if (!d || !out || max <= 0) return 0;
    int n = (int)std::min((size_t)max, d->out.size());
    if (d->out_ch > 0) n -= n % d->out_ch;
    memcpy(out, d->out.data(), sizeof(float) * n);
    d->out.erase(d->out.begin(), d->out.begin() + n);
    if (rate) *rate = d->out_rate;
    if (channels) *channels = d->out_ch;
    return n;
}

SV_API void sv_dec_close(int64_t h) {
    std::unique_ptr<Decoder> d;
    {
        std::lock_guard<std::mutex> lk(g_dec_mu);
        auto it = g_decs.find(h);
        if (it == g_decs.end()) return;
        d = std::move(it->second);
        g_decs.erase(it);
    }
    if (d->mft) d->mft->Release();
}

SV_API int sv_world_stats(char* buf, int cap) {
    World* w = W();
    int64_t frames = w->frames.load();
    double avg = frames > 0 ? QpcUs(w->mix_ticks.load()) / frames : 0;
    size_t bufs, bytes = 0;
    {
        std::lock_guard<std::mutex> lk(w->mu);
        bufs = w->bufs.size();
        for (auto& kv : w->bufs) bytes += kv.second->data.size() * sizeof(float);
    }
    int music_ms;
    int64_t underruns;
    {
        std::lock_guard<std::mutex> lk(w->music_mu);
        music_ms = (int)(w->music.count * 1000 / kRate);
        underruns = w->music.underruns;
    }
    char tmp[512];
    snprintf(tmp, sizeof tmp,
             "{\"channels_active\":%d,\"mix_us_avg\":%.1f,\"mix_us_recent\":%.1f,\"mix_us_max\":%.1f,\"frames\":%lld,\"buffers\":%zu,"
             "\"buffer_mb\":%.1f,\"music_queued_ms\":%d,\"music_underruns\":%lld}",
             w->active.load(), avg, (double)w->mix_us_recent.load(), QpcUs(w->mix_max_ticks.load()), (long long)frames, bufs, bytes / 1048576.0,
             music_ms, (long long)underruns);
    int len = (int)strlen(tmp);
    if (buf && cap > 0) {
        int n = std::min(cap - 1, len);
        memcpy(buf, tmp, n);
        buf[n] = 0;
    }
    return len + 1;
}

// ---- media sound ----

// Mixes the sound ring of a surface's shared section (protocol.h) as stream 0..15, from what's written after now.
// The section must stay mapped until sv_media_detach returns.
SV_API int sv_media_attach(int stream, void* section) {
    if (stream < 0 || stream >= kMediaStreams || !section) return -1;
    auto* h = (sm::Header*)section;
    if (h->magic != sm::kMagic || h->version != sm::kVersion || h->audio_capacity == 0) return -2;
    Media* m = M();
    std::lock_guard<std::mutex> lk(m->mu);
    MediaStream& st = m->s[stream];
    st = MediaStream();
    st.h = h;
    st.ring = (const float*)((uint8_t*)section + h->audio_offset);
    h->audio_read = h->audio_write;
    return 0;
}

SV_API void sv_media_detach(int stream) {
    if (stream < 0 || stream >= kMediaStreams) return;
    Media* m = M();
    std::lock_guard<std::mutex> lk(m->mu);
    m->s[stream] = MediaStream();
}

// Gain (ramped over 10 ms; 0 silences it) and placement: spatial, the left channel from (lx, ly, lz) and the right
// from (rx, ry, rz), global metres; else played as it is, centred.
SV_API void sv_media_set(int stream, float gain, int spatial, double lx, double ly, double lz, double rx, double ry, double rz) {
    if (stream < 0 || stream >= kMediaStreams) return;
    Media* m = M();
    std::lock_guard<std::mutex> lk(m->mu);
    MediaStream& st = m->s[stream];
    st.gain = std::max(0.0f, gain);
    bool sp = spatial != 0;
    if (sp != st.spatial) {
        st.ears[0] = EarPair();
        st.ears[1] = EarPair();
    }
    st.spatial = sp;
    st.pos[0][0] = lx;
    st.pos[0][1] = ly;
    st.pos[0][2] = lz;
    st.pos[1][0] = rx;
    st.pos[1][1] = ry;
    st.pos[1][2] = rz;
}

// JSON array, one object per attached stream: queued ms, playing, underruns, frames dropped, gain.
SV_API int sv_media_stats(char* buf, int cap) {
    std::string out = "[";
    Media* m = M();
    {
        std::lock_guard<std::mutex> lk(m->mu);
        for (int i = 0; i < kMediaStreams; i++) {
            MediaStream& st = m->s[i];
            if (!st.h) continue;
            int rate = st.h->audio_rate > 0 ? st.h->audio_rate : kRate;
            int64_t q = (st.h->audio_write - st.h->audio_read) * 1000 / rate;
            char tmp[200];
            snprintf(tmp, sizeof tmp, "%s{\"stream\":%d,\"queued_ms\":%lld,\"playing\":%s,\"underruns\":%lld,\"dropped\":%lld,\"gain\":%.3f}",
                     out.size() > 1 ? "," : "", i, (long long)q, st.playing ? "true" : "false", (long long)st.underruns,
                     (long long)st.dropped, st.gain);
            out += tmp;
        }
    }
    out += "]";
    int len = (int)out.size();
    if (buf && cap > 0) {
        int n = std::min(cap - 1, len);
        memcpy(buf, out.data(), n);
        buf[n] = 0;
    }
    return len + 1;
}
