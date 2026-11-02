// Internal declarations shared by the shim's sources.
#pragma once
#ifndef WEBRTC_WIN
#define WEBRTC_WIN 1
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif

#pragma warning(push)
#pragma warning(disable : 4996 4068 4244 4267 4100 4127 4201 4324 4702)
#include "api/scoped_refptr.h"
#include "api/make_ref_counted.h"
#include "api/environment/environment_factory.h"
#include "api/peer_connection_interface.h"
#include "api/create_modular_peer_connection_factory.h"
#include "api/enable_media.h"
#include "api/media_stream_interface.h"
#include "api/data_channel_interface.h"
#include "api/jsep.h"
#include "api/audio/audio_device.h"
#include "api/audio/audio_processing.h"
#include "api/audio/builtin_audio_processing_builder.h"
#include "api/audio_codecs/builtin_audio_decoder_factory.h"
#include "api/audio_codecs/builtin_audio_encoder_factory.h"
#include "api/task_queue/default_task_queue_factory.h"
#include "api/set_local_description_observer_interface.h"
#include "api/set_remote_description_observer_interface.h"
#include "modules/audio_device/include/audio_device_default.h"
#include "common_audio/resampler/include/push_resampler.h"
#include "rtc_base/ssl_adapter.h"
#include "rtc_base/thread.h"
#include "rtc_base/logging.h"
#pragma warning(pop)

#include <winsock2.h>
#include <windows.h>
#include <atomic>
#include <string>
#include <vector>
#include <mutex>

#include "sojourn_voice.h"

namespace sv {

constexpr int kRate = 48000;
constexpr int kFrame = 480;  // 10 ms

void Log(int level, const std::string& msg);
void Emit(int64_t pc, int kind, const char* a, const char* b, int n);
std::string JsonEscape(const std::string& s);

// The engine's per-10 ms entry points, called by the audio threads.
void CaptureFrame(const float* mono);  // kFrame samples, [-1, 1]
void RenderFrame(float* stereo);       // fills kFrame * 2 interleaved
extern std::atomic<int> g_render_delay_ms;
extern std::atomic<int> g_capture_delay_ms;
extern std::atomic<int64_t> g_capture_frames, g_render_frames, g_render_glitches, g_capture_glitches;
// when a file mic / file speaker started (Unix ms), so tests can line two processes' files up
extern std::atomic<int64_t> g_capture_file_t0, g_render_file_t0;
int64_t UnixMs();

// World audio (world.cpp): adds the world's sounds and the music stream into a render frame.
void WorldMix(float* stereo);

// Audio I/O (audio_io.cpp).
bool AudioStart(const std::string& capture, const std::string& render);
void AudioStop();
void AudioTest(int dir, int what, const std::string& spec);
std::string DeviceListJson(bool capture);
std::string DeviceStateJson(int dir);
std::string OutputProbe(int ms);
float OutLevel();   // the device mix's level after the output volume (RMS, smoothed over ~30 ms)

// Peers (peer.cpp).
webrtc::PeerConnectionFactoryInterface* Factory();
webrtc::Thread* SignalingThread();
void PeersCloseAll();

}  // namespace sv
