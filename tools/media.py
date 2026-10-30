"""Sojourn's media (docs/media.md): the helpers' runtimes, building them, and test fixtures.

  python tools/media.py fetch       CEF (minimal, pinned) and libVLC (pinned) into local/media/cef and local/media/vlc,
                                    checked against their published hashes; the archives are deleted afterwards
  python tools/media.py build       the helpers (src/Sojourn.Media.Native) into local/media/build, then staged with
                                    their runtimes in local/media/runtime/{web,video} (pruned; see STAGE below)
  python tools/media.py serve       a local web server on 127.0.0.1:9752 for the tests: /page.html (a page with
                                    links, a form, a scroller, a sound button), /video.mp4 (a generated test clip),
                                    /tone.mp3, /big.html (a long page)
  python tools/media.py fixtures    on the local grid: the Media Board (a prim with media on its front face, a
                                    whitelist, owner-only control), the Media TV (a video face anyone may use) and
                                    the parcel's media texture on a third prim (see fixtures())
  python tools/media.py test [NAMES]  the client against them (see test())

Shared machine: everything here runs below normal priority.
"""
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tarfile
import time
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MEDIA = ROOT / "local" / "media"
CEF = MEDIA / "cef"
VLC = MEDIA / "vlc"
BUILD = MEDIA / "build"
RUNTIME = MEDIA / "runtime"
SRC = ROOT / "src" / "Sojourn.Media.Native"
VS = Path(r"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools")
CMAKE = VS / "Common7" / "IDE" / "CommonExtensions" / "Microsoft" / "CMake" / "CMake" / "bin" / "cmake.exe"
BELOW_NORMAL = 0x00004000

# Pinned versions: update both the name and the hash together (CEF's index: cef-builds.spotifycdn.com/index.json,
# its sha1; VideoLAN publishes a .sha256 beside each archive).
CEF_NAME = "cef_binary_156.0.14+g4fa4ab5+chromium-156.0.8078.48_windows64_minimal"
CEF_SHA1 = "72dc1dd1636acc2391609fa6f410aa09f09168d4"
VLC_VERSION = "3.0.21"
VLC_SHA256 = "a0b7ec02b50adf6417eed014fb8df50af39690505a4225b85b3dc2ed17d14843"


def download(url, dest):
    print(f"fetching {url}")
    req = urllib.request.Request(url, headers={"User-Agent": "Sojourn-media-fetch"})
    with urllib.request.urlopen(req, timeout=60) as r, open(dest, "wb") as f:
        shutil.copyfileobj(r, f, 1 << 20)


def digest(path, algo):
    h = hashlib.new(algo)
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def fetch():
    dl = MEDIA / "dl"
    dl.mkdir(parents=True, exist_ok=True)
    if not (CEF / "Release" / "libcef.dll").exists():
        arc = dl / "cef_minimal.tar.bz2"
        if not arc.exists():
            download("https://cef-builds.spotifycdn.com/" + CEF_NAME.replace("+", "%2B") + ".tar.bz2", arc)
        if digest(arc, "sha1") != CEF_SHA1:
            print("CEF archive's hash doesn't match: not unpacked", file=sys.stderr)
            return 1
        tmp = MEDIA / "cef-x"
        with tarfile.open(arc) as t:
            t.extractall(tmp, filter="data")
        shutil.move(str(tmp / CEF_NAME), str(CEF))
        shutil.rmtree(tmp, ignore_errors=True)
        arc.unlink()
    if not (VLC / "libvlc.dll").exists():
        arc = dl / "vlc.zip"
        if not arc.exists():
            download(f"https://get.videolan.org/vlc/{VLC_VERSION}/win64/vlc-{VLC_VERSION}-win64.zip", arc)
        if digest(arc, "sha256") != VLC_SHA256:
            print("libVLC archive's hash doesn't match: not unpacked", file=sys.stderr)
            return 1
        tmp = MEDIA / "vlc-x"
        with zipfile.ZipFile(arc) as z:
            z.extractall(tmp)
        shutil.move(str(tmp / f"vlc-{VLC_VERSION}"), str(VLC))
        shutil.rmtree(tmp, ignore_errors=True)
        arc.unlink()
    shutil.rmtree(dl, ignore_errors=True)
    print(f"CEF in {CEF}, libVLC in {VLC}")
    return 0


# What of libVLC's plugins the video helper gets: no windows, devices, capture, local files, encoders, scripting or
# anything that writes. Folders are dropped whole; single plugins by name. Everything else stays.
VLC_DROP_DIRS = {"gui", "visualization", "stream_out", "access_output", "mux", "lua", "services_discovery", "control",
                 "keystore", "meta_engine", "text_renderer", "spu", "video_splitter", "logger"}
VLC_DROP = {
    # access: local files and folders, cameras, screen and audio capture, discs, file shares
    "libfilesystem_plugin.dll", "libdshow_plugin.dll", "libscreen_plugin.dll", "libaccess_wasapi_plugin.dll",
    "libcdda_plugin.dll", "libdvdnav_plugin.dll", "libdvdread_plugin.dll", "liblibbluray_plugin.dll",
    "libbluray-awt-j2se-1.3.2.jar", "libbluray-j2se-1.3.2.jar", "libvcd_plugin.dll", "libdtv_plugin.dll",
    "libsatip_plugin.dll", "libsftp_plugin.dll", "libsmb_plugin.dll", "libnfs_plugin.dll", "libftp_plugin.dll",
    "libvnc_plugin.dll", "libvdr_plugin.dll", "libdcp_plugin.dll", "libattachment_plugin.dll", "libshm_plugin.dll",
    "libaccess_imem_plugin.dll", "libimem_plugin.dll",
    # outputs: only memory (vmem, amem) and the dummies
    "libcaca_plugin.dll", "libdirect3d11_plugin.dll", "libdirect3d9_plugin.dll", "libdirectdraw_plugin.dll",
    "libdrawable_plugin.dll", "libflaschen_plugin.dll", "libgl_plugin.dll", "libglinterop_dxva2_plugin.dll",
    "libglwin32_plugin.dll", "libwgl_plugin.dll", "libwingdi_plugin.dll", "libwinhibit_plugin.dll", "libyuv_plugin.dll",
    "libafile_plugin.dll", "libdirectsound_plugin.dll", "libmmdevice_plugin.dll", "libwasapi_plugin.dll",
    "libwaveout_plugin.dll",
    # codecs: encoders (x264/x265 are GPL), MIDI synthesis, subtitles, odd hardware
    "libx264_plugin.dll", "libx26410b_plugin.dll", "libx265_plugin.dll", "libtwolame_plugin.dll",
    "libfluidsynth_plugin.dll", "libcrystalhd_plugin.dll", "libsdl_image_plugin.dll", "liblibass_plugin.dll",
    "libsubsdec_plugin.dll", "libsubstx3g_plugin.dll", "libsubsusf_plugin.dll", "libkate_plugin.dll",
    "libzvbi_plugin.dll", "libaribsub_plugin.dll", "libaom_plugin.dll", "libspdif_plugin.dll",
    # demuxers that read folders, dump to files, or play chiptunes and subtitles
    "libdirectory_demux_plugin.dll", "libdemuxdump_plugin.dll", "libdemux_chromecast_plugin.dll",
    "libsubtitle_plugin.dll", "libvobsub_plugin.dll", "libdemux_stl_plugin.dll", "libgme_plugin.dll",
    "libsid_plugin.dll", "libmod_plugin.dll",
    # misc: network services, add-ons (they write files), exporters, loggers
    "libaddonsfsstorage_plugin.dll", "libaddonsvorepository_plugin.dll", "libaudioscrobbler_plugin.dll",
    "libexport_plugin.dll", "libfingerprinter_plugin.dll", "liblogger_plugin.dll", "libstats_plugin.dll",
    "libvod_rtsp_plugin.dll", "librecord_plugin.dll",
}


def stale():
    stamp = BUILD / "built.stamp"
    if not stamp.exists() or not (RUNTIME / "web" / "sojourn_web.exe").exists() or not (RUNTIME / "video" / "sojourn_video.exe").exists():
        return True
    built = stamp.stat().st_mtime
    return any(p.stat().st_mtime > built for p in SRC.rglob("*") if p.is_file())


def build(force=False):
    if not (CEF / "Release" / "libcef.dll").exists() or not (VLC / "libvlc.dll").exists():
        print("no CEF or libVLC in local/media: python tools/media.py fetch", file=sys.stderr)
        return 1
    if not force and not stale():
        return 0
    cmake = str(CMAKE) if CMAKE.exists() else "cmake"
    BUILD.mkdir(parents=True, exist_ok=True)
    if not (BUILD / "CMakeCache.txt").exists():
        r = subprocess.run([cmake, "-S", str(SRC), "-B", str(BUILD), "-G", "Visual Studio 17 2022", "-A", "x64",
                            f"-DCEF_ROOT={CEF.as_posix()}"], creationflags=BELOW_NORMAL)
        if r.returncode:
            return r.returncode
    r = subprocess.run([cmake, "--build", str(BUILD), "--config", "Release", "--", "/m:3", "/v:m", "/nologo"],
                       creationflags=BELOW_NORMAL)
    if r.returncode:
        return r.returncode
    stage()
    (BUILD / "built.stamp").write_text(time.strftime("%Y-%m-%d %H:%M:%S"))
    return 0


def copy_tree(src, dst, skip=lambda p: False):
    for p in src.rglob("*"):
        rel = p.relative_to(src)
        if skip(rel):
            continue
        t = dst / rel
        if p.is_dir():
            t.mkdir(parents=True, exist_ok=True)
        elif not t.exists() or t.stat().st_mtime < p.stat().st_mtime or t.stat().st_size != p.stat().st_size:
            t.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(p, t)


def stage():
    """local/media/runtime/web: the helper, CEF's binaries and resources (English only); runtime/video: the helper,
    libVLC and the plugins it may use. Pruned files that were staged before are removed."""
    web = RUNTIME / "web"
    web.mkdir(parents=True, exist_ok=True)
    out = BUILD / "Release"
    for f in ("sojourn_web.exe", "sojourn_web.dll"):
        shutil.copy2(out / f, web / f)
    copy_tree(CEF / "Release", web, skip=lambda r: r.suffix == ".lib" or r.name in ("bootstrap.exe", "bootstrapc.exe"))
    copy_tree(CEF / "Resources", web, skip=lambda r: r.parts[0] == "locales" and len(r.parts) > 1 and r.name != "en-US.pak")
    # the sandbox's lowest processes (LPAC) need to read the folder they run from
    subprocess.run(["icacls", str(web), "/grant", "*S-1-15-2-2:(OI)(CI)(RX)", "/Q"], stdout=subprocess.DEVNULL)

    video = RUNTIME / "video"
    video.mkdir(parents=True, exist_ok=True)
    shutil.copy2(out / "sojourn_video.exe", video / "sojourn_video.exe")
    vlc = video / "vlc"
    vlc.mkdir(parents=True, exist_ok=True)
    for f in ("libvlc.dll", "libvlccore.dll", "COPYING.txt", "AUTHORS.txt", "README.txt"):
        shutil.copy2(VLC / f, vlc / f)

    def drop(rel):
        return (len(rel.parts) >= 1 and rel.parts[0] in VLC_DROP_DIRS) or rel.name in VLC_DROP

    copy_tree(VLC / "plugins", vlc / "plugins", skip=drop)
    for p in list((vlc / "plugins").rglob("*")):
        rel = p.relative_to(vlc / "plugins")
        if p.exists() and drop(rel):
            shutil.rmtree(p) if p.is_dir() else p.unlink()
    # libVLC's plugin cache, so starting a player doesn't scan every plugin
    cache_gen = VLC / "vlc-cache-gen.exe"
    if cache_gen.exists():
        subprocess.run([str(cache_gen), str(vlc / "plugins")], creationflags=BELOW_NORMAL,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    def size(d):
        return sum(p.stat().st_size for p in d.rglob("*") if p.is_file()) / 1048576

    print(f"staged {web} ({size(web):.0f} MB), {video} ({size(video):.0f} MB)")


def main(argv):
    cmd = argv[0] if argv else "build"
    if cmd == "fetch":
        return fetch()
    if cmd == "build":
        return build(force="--force" in argv)
    if cmd == "stage":
        stage()
        return 0
    if cmd == "serve":
        from media_fixtures import serve
        return serve()
    if cmd == "fixtures":
        from media_fixtures import fixtures
        return fixtures()
    if cmd == "test" and argv[1:] == ["headset"]:
        from media_fixtures import headset
        return headset()
    if cmd == "perf":
        from media_fixtures import perf
        return perf(argv[1:])
    if cmd == "test":
        from media_fixtures import test
        return test(argv[1:])
    print(__doc__)
    return 1


if __name__ == "__main__":
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    sys.exit(main(sys.argv[1:]))
