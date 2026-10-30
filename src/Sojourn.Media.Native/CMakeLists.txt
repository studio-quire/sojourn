# Sojourn's media helpers: sojourn_web (CEF, the web pages) and sojourn_video (libVLC, video and audio streams).
# Built by tools/media.py into local/media/build; it stages them with their runtimes in local/media/runtime.
# CEF_ROOT: the CEF binary distribution (tools/media.py fetch puts it in local/media/cef).
cmake_minimum_required(VERSION 3.21)
set(CMAKE_CONFIGURATION_TYPES Release CACHE STRING "Release only" FORCE)
project(sojourn_media CXX)

set_property(GLOBAL PROPERTY USE_FOLDERS ON)
set(USE_SANDBOX ON CACHE BOOL "Chromium's sandbox through CEF's bootstrap" FORCE)
set(USE_ATL OFF CACHE BOOL "" FORCE)
set(CMAKE_MODULE_PATH ${CMAKE_MODULE_PATH} "${CEF_ROOT}/cmake")
find_package(CEF REQUIRED)
add_subdirectory(${CEF_LIBCEF_DLL_WRAPPER_PATH} libcef_dll_wrapper)
ADD_LOGICAL_TARGET("libcef_lib" "${CEF_LIB_DEBUG}" "${CEF_LIB_RELEASE}")

# ---- sojourn_web.dll, run by bootstrap.exe copied as sojourn_web.exe ----
set(CEF_TARGET sojourn_web)
SET_CEF_TARGET_OUT_DIR()
add_library(${CEF_TARGET} SHARED web/web.cc common/channel.h common/json.h common/protocol.h common/urls.h)
SET_LIBRARY_TARGET_PROPERTIES(${CEF_TARGET})
add_dependencies(${CEF_TARGET} libcef_dll_wrapper)
target_link_libraries(${CEF_TARGET} libcef_lib libcef_dll_wrapper ${CEF_STANDARD_LIBS})
COPY_SINGLE_FILE(${CEF_TARGET} ${CEF_BINARY_DIR}/bootstrap.exe ${CEF_TARGET_OUT_DIR}/${CEF_TARGET}.exe)

# ---- sojourn_video.exe: libVLC is loaded at run time from the folder it's given, so nothing of it is linked ----
add_executable(sojourn_video WIN32 video/video.cc common/channel.h common/json.h common/protocol.h common/urls.h)
target_compile_features(sojourn_video PRIVATE cxx_std_20)
target_compile_definitions(sojourn_video PRIVATE UNICODE _UNICODE NOMINMAX WIN32_LEAN_AND_MEAN)
set_property(TARGET sojourn_video PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded")
set_target_properties(sojourn_video PROPERTIES RUNTIME_OUTPUT_DIRECTORY_RELEASE ${CMAKE_CURRENT_BINARY_DIR}/Release)
