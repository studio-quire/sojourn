# sojourn_voice.dll over LL's prebuilt libwebrtc m144 (local/webrtc, from secondlife/3p-webrtc-build).
# Built by tools/voice.py build (Visual Studio 2022 Build Tools); output in local/voice/.
cmake_minimum_required(VERSION 3.24)
project(sojourn_voice CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# libwebrtc is a static /MT release build, so everything here is too
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")

set(WEBRTC_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../local/webrtc" CACHE PATH "LL's libwebrtc package")

add_library(sojourn_voice SHARED engine.cpp audio_io.cpp peer.cpp world.cpp sojourn_voice.h internal.h)
target_compile_definitions(sojourn_voice PRIVATE SV_BUILD WEBRTC_WIN NOMINMAX WIN32_LEAN_AND_MEAN
    _CRT_SECURE_NO_WARNINGS)
target_include_directories(sojourn_voice SYSTEM PRIVATE "${WEBRTC_ROOT}/include/webrtc"
    "${WEBRTC_ROOT}/include/webrtc/third_party/abseil-cpp")
target_compile_options(sojourn_voice PRIVATE /Zc:wchar_t /Zc:__cplusplus /permissive- /utf-8 /W3 /wd4100 /wd4996)
target_link_libraries(sojourn_voice PRIVATE "${WEBRTC_ROOT}/lib/release/webrtc.lib"
    secur32 winmm dmoguids wmcodecdspuuid msdmo strmiids iphlpapi crypt32 ws2_32 avrt ole32 propsys mfplat mfuuid)
target_link_options(sojourn_voice PRIVATE /OPT:REF /OPT:ICF)

add_executable(sv_loopback test/loopback.cpp)
target_link_libraries(sv_loopback PRIVATE sojourn_voice)
