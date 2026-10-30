// sojourn_voice: Sojourn's native voice shim over libwebrtc (LL's m144 build).
// Written by Claude Opus 5.5, 2026-10-25. The C ABI the client binds to (src/Sojourn.Client/Voice/Native.cs).
//
// One engine per process: one audio capture and one render device (or files), one APM of our own (AEC3, NS, AGC2,
// HPF) whose reverse stream is the whole device mix, and any number of peer connections sharing them.
// Strings are UTF-8. Callbacks come on libwebrtc's or the audio threads: keep them short (enqueue and return).
#pragma once
#include <stdint.h>

#ifdef SV_BUILD
#define SV_API extern "C" __declspec(dllexport)
#else
#define SV_API extern "C" __declspec(dllimport)
#endif

// level: 0 verbose, 1 info, 2 warning, 3 error
typedef void (*sv_log_fn)(int level, const char* msg);

// Peer events. a/b are strings (may be null), n an int.
enum sv_event_kind {
    SV_EV_OFFER = 1,          // a = local offer SDP (already edited for SL: opus 48k stereo, FEC)
    SV_EV_ANSWER = 2,         // a = local answer SDP (loopback tests only)
    SV_EV_CANDIDATE = 3,      // a = candidate in SL's trickle form, b = sdpMid, n = sdpMLineIndex
    SV_EV_GATHERED = 4,       // ICE gathering complete
    SV_EV_STATE = 5,          // n = PeerConnectionState (0 new, 1 connecting, 2 connected, 3 disconnected, 4 failed, 5 closed)
    SV_EV_DATA_OPEN = 6,
    SV_EV_DATA_CLOSED = 7,
    SV_EV_DATA = 8,           // a = message text
    SV_EV_RENEGOTIATE = 9,    // the connection needs a fresh offer (failed, or disconnected for 10 s)
    SV_EV_ERROR = 10,         // a = text
    SV_EV_DEVICE = 20,        // pc = 0; n = 0 capture / 1 render; a = "open" | "lost" | "failed" | "silent" (open
                              // but no audio for 1 s), b = device name
};
typedef void (*sv_event_fn)(int64_t pc, int kind, const char* a, const char* b, int n);

SV_API int sv_init(sv_log_fn log, sv_event_fn events);
SV_API void sv_shutdown(void);

// JSON array of {"id","name","default"} for capture (1) or render (0) endpoints. Returns the length needed
// (including the terminator); writes nothing past cap.
SV_API int sv_devices(int capture, char* buf, int cap);

// Opens the devices and starts the audio threads (stopping any running). Device spec: "" the default device,
// "comms" the default communications device, an endpoint id, a friendly-name substring prefixed "name:", "file:PATH" (a WAV, played
// once then silence / written as 48 kHz stereo), "loop:PATH" (a WAV mic, repeated), "none" (paced silence / discard).
SV_API int sv_audio_start(const char* capture, const char* render);
SV_API void sv_audio_stop(void);

// The person's mic toward residents: muted sends zeros, with a ramp of fade_ms.
SV_API void sv_set_mute(int muted, int fade_ms);
// The assistant's gate: while open the mic goes to the tap (after a 300 ms pre-roll) and residents get zeros.
// Closing keeps it open for tail_ms more.
SV_API void sv_set_gate(int open, int tail_ms);
// The tap: the person's own mic, post-APM, 16 kHz mono, only while the gate is open (with its pre-roll and tail).
// Nothing else ever goes into it. Enabled, it keeps the pre-roll; sv_tap_read drains what's been tapped.
SV_API void sv_set_tap(int enabled);
SV_API int sv_tap_read(int16_t* out, int max_samples);
// Output volume for voice (0..1+), and for everything.
SV_API void sv_set_voice_volume(float v);
SV_API void sv_set_output_volume(float v);
// The mic's level after processing (RMS, linear, smoothed over ~30 ms): your own voice's level, as the viewer shows it.
SV_API float sv_mic_level(void);
// Processing switches (tests): aec, ns (0 off, 1-4 low..very high), agc, hpf.
SV_API void sv_set_processing(int aec, int ns, int agc, int hpf);

// Local-only sound (the assistant's speech later, world sounds): mixed into the device output and the AEC
// reference, never into a voice channel. Interleaved int16 at any rate; channels 1 or 2. stream 0..7.
SV_API void sv_play(int stream, const int16_t* pcm, int frames, int rate, int channels);
SV_API void sv_play_clear(int stream);
SV_API int sv_play_queued_ms(int stream);

// Tests: feed the device mix back into the mic after delay_ms at gain (an echo path to cancel); gain 0 stops it.
SV_API void sv_test_echo(int delay_ms, float gain);

// JSON object: levels, counters, AEC metrics. Returns length needed.
SV_API int sv_stats(char* buf, int cap);

// Peer connections. ice: newline-separated STUN/TURN URLs (may be empty). Returns an id > 0; the offer arrives
// as SV_EV_OFFER (sv_pc_new) or the answer as SV_EV_ANSWER (sv_pc_new_answerer, tests).
SV_API int64_t sv_pc_new(const char* ice);
SV_API int64_t sv_pc_new_answerer(const char* ice, const char* offer_sdp);
SV_API void sv_pc_answer(int64_t pc, const char* sdp);
SV_API void sv_pc_add_candidate(int64_t pc, const char* mid, int mline, const char* candidate);
SV_API void sv_pc_send(int64_t pc, const char* text);
SV_API void sv_pc_mute_send(int64_t pc, int muted);
SV_API void sv_pc_receive_volume(int64_t pc, float v);
SV_API void sv_pc_close(int64_t pc);

// ---- World audio (world.cpp): sounds placed in the world and the parcel's music stream, mixed into the device
// output (so into the echo canceller's reference), never into a voice channel. Positions are global metres in
// SL's frame (x east, y north, z up).

// A decoded sound (interleaved float, 1 or 2 channels, any rate; resampled to 48 kHz here). Returns its id (> 0).
SV_API int sv_buf_load(const float* pcm, int frames, int rate, int channels);
SV_API void sv_buf_free(int id);
// The listener: position, forward and up (the camera, or the head in the headset).
SV_API void sv_listener(double x, double y, double z, float fx, float fy, float fz, float ux, float uy, float uz);
// The world bus: its gain (ramped; 0 mutes world sounds and music) and the distance rolloff (1; 5 under water).
SV_API void sv_world_set(float bus_gain, float rolloff);
// Channels 0..31. flags: 1 loop, 2 spatial (else played as it is, centred). master >= 0: a sync slave that waits
// for that channel to loop and starts at its offset. Returns the play's generation.
SV_API int sv_ch_play(int ch, int buf, int flags, float gain, double x, double y, double z, int master);
SV_API void sv_ch_queue(int ch, int buf);   // plays when the current sound ends (or loops)
SV_API void sv_ch_set(int ch, float gain, double x, double y, double z, int loop);   // loop -1: unchanged
SV_API void sv_ch_stop(int ch);             // a 10 ms fade
// 4 ints per channel: state (0 idle, 1 playing, 2 waiting for its master), sound id, loops, generation.
SV_API void sv_ch_states(int* out, int n);
// The music stream: decoded audio in (any rate, 1 or 2 channels), its gain (ramped), cleared, the pre-roll it
// waits for before playing (and again after running dry); queued ms (-1 - queued while buffering).
SV_API void sv_music_push(const float* pcm, int frames, int rate, int channels);
SV_API void sv_music_gain(float gain);
SV_API void sv_music_clear(void);
SV_API void sv_music_preroll(int ms);
SV_API int sv_music_queued_ms(void);
// Decoders for internet radio through Media Foundation: kind 1 MP3, 2 AAC (ADTS). Bytes in, float samples out.
SV_API int64_t sv_dec_open(int kind, int rate, int channels);
SV_API int sv_dec_feed(int64_t h, const uint8_t* data, int n);
SV_API int sv_dec_read(int64_t h, float* out, int max_samples, int* rate, int* channels);
SV_API void sv_dec_close(int64_t h);
// JSON: active channels, the mix's time per 10 ms frame, buffers held, music queued and underruns.
SV_API int sv_world_stats(char* buf, int cap);

// ---- Media sound (world.cpp): the sound of web pages and video on faces, read from their surfaces' shared sections
// (Sojourn.Media.Native/common/protocol.h) into the device mix, so into the echo canceller's reference.
// stream 0..15. Attach reads from what's written after now; the section must stay mapped until detach returns.
SV_API int sv_media_attach(int stream, void* section);
SV_API void sv_media_detach(int stream);
// gain (ramped) and placement: spatial = the left channel from l, the right from r (global metres), no distance loss
SV_API void sv_media_set(int stream, float gain, int spatial, double lx, double ly, double lz, double rx, double ry, double rz);
SV_API int sv_media_stats(char* buf, int cap);
