"""Set up a complete build of Sojourn and Sojourn Access from a fresh clone, on Windows 10 or 11 (x64). BUILDING.md at
the repo's root is the guide, written for the agent doing the build.

  python tools/setup.py --check            what's installed, what's missing and where to get it, what's built, and
                                           what a run would do; changes nothing and downloads nothing
  python tools/setup.py                    everything for both apps
  python tools/setup.py --for sojourn      the VR viewer and its launcher, voice, media, the assistant
  python tools/setup.py --for access       Sojourn Access, voice, NVDA's controller client, the assistant
  python tools/setup.py --skip media,assistant
                                           leave optional parts out: voice, media, nvda, assistant
  python tools/setup.py --shortcuts        also put Start menu shortcuts to the built apps (Sojourn, Sojourn Access)

The parts, in the order they're built:
  voice      voice chat and world audio: LL's libwebrtc fetched (pinned, hash-checked) and the native shim built
             (tools/voice.py; Visual Studio 2022's C++ tools and CMake). Before the apps, which copy its DLL.
  media      web pages and video on surfaces, VR viewer only: CEF and libVLC fetched (pinned, hash-checked) and the
             helpers built (tools/media.py; the same C++ tools)
  nvda       NVDA's controller client, Access only (tools/screenreader.py)
  sojourn    the VR viewer (SojournVR) and its launcher (Sojourn.exe): dotnet build, Release
  access     Sojourn Access: dotnet build, Release
  assistant  the assistant's Python side: a venv in local/assistant-venv with the Agent SDK (which carries its own
             Claude Code), local hearing and spoken replies, and their models (tools/assistant.py setup; ~1.5 GB)

Safe to run again: each part skips what's done (downloads are kept only when whole and checked, builds are
incremental), so after a failure, fix its cause and rerun. Ends with a summary: each part built, already there, skipped
(and why) or failed (and what to do). Exit code 0 when everything asked for is there, 1 otherwise. Runs at below-normal
priority. Needs nothing outside this repository: no studio paths, no local grid.

Written by Claude Opus 5.5, 2026-10-31.
"""
import argparse
import os
import platform
import shutil
import subprocess
import sys
import traceback
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

if sys.version_info < (3, 11):
    sys.exit(f"tools/setup.py needs Python 3.11 or newer (this is {platform.python_version()}): "
             "winget install Python.Python.3.12")

import buildenv  # noqa: E402

BELOW_NORMAL = 0x00004000

CLIENT_PROJECT = ROOT / "src" / "Sojourn.Client" / "Sojourn.Client.csproj"
CLIENT_DLL = ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "SojournVR.dll"
LAUNCHER_BIN = ROOT / "src" / "Sojourn.Launcher" / "bin" / "Release" / "net10.0-windows"
ACCESS_BIN = ROOT / "src" / "Sojourn.Access" / "bin" / "Release" / "net10.0-windows10.0.22621.0"
NVDA_DLL = ROOT / "local" / "nvda" / "x64" / "nvdaControllerClient.dll"
VENV_PY = ROOT / "local" / "assistant-venv" / "Scripts" / "python.exe"
KOKORO = ROOT / "local" / "assistant" / "kokoro" / "voices-v1.0.bin"
PARAKEET = ROOT / "local" / "assistant" / "parakeet" / "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8" / "encoder.int8.onnx"
VENV_MODULES = ["claude_agent_sdk", "faster_whisper", "soundcard", "numpy", "kokoro_onnx", "sherpa_onnx", "websockets"]

GET = {
    "dotnet": "the .NET 10 SDK: `winget install Microsoft.DotNet.SDK.10`, or https://dotnet.microsoft.com/download/dotnet/10.0",
    "vs": ("Visual Studio 2022 Build Tools with the C++ workload: `winget install Microsoft.VisualStudio.2022.BuildTools "
           "--override \"--wait --passive --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended\"`, or "
           "https://visualstudio.microsoft.com/downloads/ (Tools for Visual Studio > Build Tools for Visual Studio 2022; "
           "tick \"Desktop development with C++\")"),
    "cmake": ("CMake: the \"C++ CMake tools for Windows\" component of Visual Studio 2022 (the C++ workload's recommended "
              "parts), or `winget install Kitware.CMake`"),
    "winsdk": "a Windows 10/11 SDK: part of the C++ workload's recommended parts (Visual Studio Installer > Modify)",
    "python": "uv (`winget install astral-sh.uv`; it fetches Python 3.12 itself) or Python 3.12 (`winget install Python.Python.3.12`)",
    "vulkan": "a GPU driver with Vulkan 1.3, from NVIDIA, AMD or Intel",
}

PARTS = ["voice", "media", "nvda", "sojourn", "access", "assistant"]
OPTIONAL = {"voice", "media", "nvda", "assistant"}
FOR = {"sojourn": {"voice", "media", "sojourn", "assistant"},
       "access": {"voice", "nvda", "access", "assistant"}}
FOR["both"] = FOR["sojourn"] | FOR["access"]


def say(line=""):
    print(line, flush=True)


# ---- what's on this PC ---------------------------------------------------------------------------------------------

class Env:
    """The prerequisites, looked up once."""

    def __init__(self):
        self.windows = platform.system() == "Windows"
        self.x64 = platform.machine().upper() in ("AMD64", "X86_64")
        self.build = sys.getwindowsversion().build if self.windows else 0
        self.dotnet = buildenv.DOTNET
        self.sdks = buildenv.sdk_versions(self.dotnet)
        self.sdk10 = [v for v in self.sdks if v.startswith(buildenv.SDK_MAJOR + ".")]
        if not self.sdk10:   # another dotnet on this PC may have it (find_dotnet looks at folders, not versions)
            for c in buildenv.dotnet_candidates():
                vs = [v for v in buildenv.sdk_versions(c) if v.startswith(buildenv.SDK_MAJOR + ".")]
                if vs:
                    self.dotnet, self.sdk10 = c, vs
                    break
        self.vs = buildenv.vs2022()
        self.other_vs = [] if self.vs else buildenv.other_vs()
        self.cmake = buildenv.cmake()
        self.winsdk = buildenv.windows_sdk()
        self.uv = shutil.which("uv")
        self.py312 = bool(shutil.which("py")) and subprocess.run(["py", "-3.12", "-c", ""], capture_output=True).returncode == 0
        self.vulkan = (Path(os.environ.get("SystemRoot", r"C:\Windows")) / "System32" / "vulkan-1.dll").exists()
        self.openxr = openxr_runtime()
        self.free_gb = shutil.disk_usage(ROOT).free / 2**30
        self.git = shutil.which("git")

    def native_missing(self):
        """Short names of what the C++ builds lack (where to get each is in the report above the summary)."""
        return [name for ok, name in ((self.vs, "Visual Studio 2022's C++ tools"), (self.cmake, "CMake"),
                                      (self.winsdk, "a Windows SDK")) if not ok]

    @property
    def venv_python_ok(self):
        return bool(self.uv or self.py312 or (3, 11) <= sys.version_info[:2] <= (3, 13) or VENV_PY.exists())


def openxr_runtime():
    """The active OpenXR runtime's manifest path (Meta's for Quest Link, SteamVR's, ...), or None."""
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Khronos\OpenXR\1") as k:
            return winreg.QueryValueEx(k, "ActiveRuntime")[0]
    except OSError:
        return None


def runtime_name(manifest):
    m = manifest.lower()
    if "oculus" in m or "meta" in m:
        return "Meta's (Quest Link)"
    if "steamvr" in m or "steamxr" in m:
        return "SteamVR's"
    if "mixedreality" in m:
        return "Windows Mixed Reality's"
    return "one"


def report_env(env):
    say("This PC")

    def row(ok, what, detail):
        say(f"  {'ok  ' if ok is True else 'MISS' if ok is False else 'note'}  {what}: {detail}")

    row(env.windows and env.x64 and env.build >= 10240, "Windows",
        f"{platform.release()} build {env.build}, {platform.machine()}" + ("" if env.x64 else " (x64 needed)"))
    row(True, "Python running this", platform.python_version())
    if env.sdk10:
        row(True, ".NET 10 SDK", f"{', '.join(env.sdk10)} ({env.dotnet})")
    else:
        row(False, ".NET 10 SDK", ("none" if not env.sdks else f"only {', '.join(env.sdks)}") + f"; get {GET['dotnet']}")
    if env.vs:
        row(True, "Visual Studio 2022 C++ tools", str(env.vs))
    else:
        row(False, "Visual Studio 2022 C++ tools", "none (needed for voice and media); " + GET["vs"]
            + (f" (found Visual Studio {', '.join(env.other_vs)} with C++, but the native parts are built with 2022; "
               "the two install side by side)" if env.other_vs else ""))
    row(bool(env.cmake), "CMake", env.cmake or f"none (needed for voice and media); {GET['cmake']}")
    row(bool(env.winsdk), "Windows SDK", env.winsdk or f"none (needed for voice and media); {GET['winsdk']}")
    if VENV_PY.exists():
        row(True, "Python for the assistant", "its venv is there")
    elif env.uv:
        row(True, "Python for the assistant", f"uv ({env.uv}) makes a 3.12 venv")
    elif env.py312:
        row(True, "Python for the assistant", "Python 3.12 (py -3.12)")
    elif env.venv_python_ok:
        row(True, "Python for the assistant", f"this Python ({platform.python_version()})")
    else:
        row(False, "Python for the assistant", f"none of 3.11-3.13 or uv (needed for the assistant); {GET['python']}")
    row(env.vulkan, "Vulkan", "the loader is installed (version checked when an app starts)" if env.vulkan
        else f"no vulkan-1.dll; {GET['vulkan']}")
    if env.openxr:
        row(None, "OpenXR runtime", f"{runtime_name(env.openxr)}: {env.openxr} (what --vr uses)")
    else:
        row(None, "OpenXR runtime", "none active (only needed in the headset: Meta Horizon Link for a Quest, or SteamVR, "
                                    "set as the OpenXR runtime)")
    row(env.free_gb >= 8 or None, "Free disk", f"{env.free_gb:.0f} GB on this drive (a full setup takes about 6 GB)")
    if "onedrive" in str(ROOT).lower():
        row(None, "Location", f"{ROOT} is in OneDrive, which would sync gigabytes of build output: a folder outside it "
                              "is better")
    say()


# ---- the parts -----------------------------------------------------------------------------------------------------

class Result:
    def __init__(self, state, detail=""):
        self.state, self.detail = state, detail   # built, there, skipped, blocked, failed, would


def blocked(env, part):
    """What stops a part from being built on this PC, or []."""
    if part in ("voice", "media"):
        return env.native_missing()
    if part in ("sojourn", "access"):
        return [] if env.sdk10 else ["the .NET 10 SDK"]
    if part == "assistant":
        return [] if env.venv_python_ok else ["uv or Python 3.12"]
    return []


def state(part):
    """(done, detail) for what's on disk now, without changing anything."""
    if part == "voice":
        import voice
        if not voice.webrtc_present():
            return False, "LL's libwebrtc not fetched yet (~80 MB, ~800 MB unpacked)"
        return (not voice.stale(), "built" if not voice.stale() else "libwebrtc there; the shim not built")
    if part == "media":
        import media
        if not (media.CEF / "Release" / "libcef.dll").exists() or not (media.VLC / "libvlc.dll").exists():
            return False, "CEF and libVLC not fetched yet (~300 MB)"
        return (not media.stale(), "built" if not media.stale() else "CEF and libVLC there; the helpers not built")
    if part == "nvda":
        return NVDA_DLL.exists(), "there" if NVDA_DLL.exists() else "not fetched yet (a small zip)"
    if part == "sojourn":
        there = CLIENT_DLL.exists() and (LAUNCHER_BIN / "Sojourn.dll").exists()
        return there, "built (a run rebuilds what changed)" if there else "not built"
    if part == "access":
        there = (ACCESS_BIN / "SojournAccess.dll").exists()
        return there, "built (a run rebuilds what changed)" if there else "not built"
    if part == "assistant":
        if not VENV_PY.exists():
            return False, "no venv yet"
        probe = ("import importlib.util as u, sys; missing = [m for m in sys.argv[1:] if u.find_spec(m) is None]; "
                 "print(' '.join(missing)); sys.exit(1 if missing else 0)")
        r = subprocess.run([str(VENV_PY), "-c", probe, *VENV_MODULES], capture_output=True, text=True)
        models = KOKORO.exists() and PARAKEET.exists()
        if r.returncode == 0 and models:
            return True, "venv and models there"
        return False, ", ".join(filter(None, [r.stdout.strip() and f"packages missing: {r.stdout.strip()}",
                                              not models and "models not fetched"]))
    raise ValueError(part)


def dotnet_build(project):
    say(f"dotnet build {project.relative_to(ROOT)} -c Release")
    # errors only: a first build prints hundreds of compiler and NuGet warnings that would bury them
    return subprocess.run([str(buildenv.DOTNET), "build", str(project), "-c", "Release", "-m:3", "-v:q", "-nologo",
                           "-clp:ErrorsOnly"], creationflags=BELOW_NORMAL).returncode


def build(part):
    """Build one part; returns (ok, detail)."""
    if part == "voice":
        import voice
        if voice.fetch():
            return False, "fetching libwebrtc failed (above); run again (a whole download is kept)"
        if voice.build():
            return False, "the shim's build failed (above): docs/voice.md > Building"
        return True, f"{voice.DLL.relative_to(ROOT)}"
    if part == "media":
        import media
        if media.fetch():
            return False, "fetching CEF or libVLC failed (above); run again"
        if media.build():
            return False, "the helpers' build failed (above): docs/media.md"
        return True, "local/media/runtime (web and video helpers)"
    if part == "nvda":
        import screenreader
        try:
            rc = screenreader.setup()
        except SystemExit as e:   # its checksum refusal
            return False, str(e)
        return rc == 0, str(NVDA_DLL.relative_to(ROOT))
    if part == "sojourn":
        import launcher
        if dotnet_build(CLIENT_PROJECT):
            return False, "the VR viewer's build failed (above)"
        if dotnet_build(launcher.PROJECT):
            return False, "the launcher's build failed (above)"
        return True, f"{(LAUNCHER_BIN / 'Sojourn.exe').relative_to(ROOT)} (the launcher) and SojournVR"
    if part == "access":
        import access
        if dotnet_build(access.APP_PROJECT):
            return False, "Sojourn Access's build failed (above)"
        return True, str((ACCESS_BIN / "SojournAccess.exe").relative_to(ROOT))
    if part == "assistant":
        import assistant
        try:
            assistant.setup()
        except SystemExit as e:
            return False, str(e)
        except subprocess.CalledProcessError as e:
            return False, f"{' '.join(map(str, e.cmd))} failed (above); run again"
        done, detail = state("assistant")
        return done, "local/assistant-venv and its models" if done else detail
    raise ValueError(part)


# ---- shortcuts -----------------------------------------------------------------------------------------------------

def shortcuts(apps):
    """Start menu shortcuts: the app's own exe when its apphost can find .NET 10, else dotnet with its dll."""
    folder = Path(os.environ["APPDATA"]) / "Microsoft" / "Windows" / "Start Menu" / "Programs"
    made = []
    for name, bin_dir, exe in apps:
        if not (bin_dir / exe).exists():
            continue
        if buildenv.apphost_finds_dotnet():
            target, args = str(bin_dir / exe), ""
        else:
            target, args = str(buildenv.DOTNET), f'"{bin_dir / Path(exe).with_suffix(".dll")}"'
        lnk = folder / f"{name}.lnk"
        ps = ("$s = (New-Object -ComObject WScript.Shell).CreateShortcut($env:LNK); $s.TargetPath = $env:TARGET; "
              "$s.Arguments = $env:ARGS; $s.WorkingDirectory = $env:WORKDIR; $s.IconLocation = $env:ICON; $s.Save()")
        env = {**os.environ, "LNK": str(lnk), "TARGET": target, "ARGS": args, "WORKDIR": str(bin_dir),
               "ICON": str(bin_dir / exe)}
        if subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", ps], env=env).returncode == 0:
            made.append(str(lnk))
    return made


# ---- the run -------------------------------------------------------------------------------------------------------

def how_to_run(results):
    ok = {p for p, r in results.items() if r.state in ("built", "there")}
    lines = []
    if "sojourn" in ok:
        exe = LAUNCHER_BIN / "Sojourn.exe"
        lines.append("Sojourn: start the launcher, " + (f"{exe}" if buildenv.apphost_finds_dotnet()
                     else f"\"{buildenv.DOTNET}\" \"{LAUNCHER_BIN / 'Sojourn.dll'}\"")
                     + " (or `python tools/launcher.py run`); the person signs in there, and Launch goes into the headset")
    if "access" in ok:
        lines.append("Sojourn Access: " + (f"{ACCESS_BIN / 'SojournAccess.exe'}" if buildenv.apphost_finds_dotnet()
                     else f"\"{buildenv.DOTNET}\" \"{ACCESS_BIN / 'SojournAccess.dll'}\""))
    return lines


def main():
    ap = argparse.ArgumentParser(description="Set up a complete Sojourn build (BUILDING.md).")
    ap.add_argument("--check", action="store_true", help="report only: change nothing, download nothing")
    ap.add_argument("--for", dest="app", choices=["sojourn", "access", "both"], default="both")
    ap.add_argument("--skip", default="", help="comma-separated optional parts to leave out: voice, media, nvda, assistant")
    ap.add_argument("--shortcuts", action="store_true", help="Start menu shortcuts to the built apps")
    a = ap.parse_args()

    skip = {s.strip() for s in a.skip.split(",") if s.strip()}
    bad = skip - OPTIONAL
    if bad:
        ap.error(f"can't skip {', '.join(sorted(bad))}: only {', '.join(sorted(OPTIONAL))} are optional")
    wanted = [p for p in PARTS if p in FOR[a.app]]

    say(f"Sojourn setup in {ROOT} (for {a.app}{'; check only' if a.check else ''})")
    say()
    env = Env()
    report_env(env)
    buildenv.DOTNET = env.dotnet   # a dotnet with the 10 SDK, if another was found first

    results = {}
    for part in wanted:
        if part in skip:
            results[part] = Result("skipped", "--skip")
            continue
        done, detail = state(part)
        why = blocked(env, part)
        if a.check:
            if why:
                results[part] = Result("blocked", "needs " + ", ".join(why) + " (above)")
            else:
                results[part] = Result("there" if done else "would", detail if done else f"would build ({detail})")
            continue
        if why:
            results[part] = Result("blocked", "needs " + ", ".join(why) + " (above)")
            continue
        if part in ("sojourn", "access") and results.get("voice") and results["voice"].state in ("blocked", "failed"):
            say(f"(building {part} without voice: its DLL isn't there)")
        if done and part in ("voice", "media", "nvda", "assistant"):
            results[part] = Result("there", detail)
            continue
        say(f"== {part} ==")
        try:
            ok, detail = build(part)
        except Exception as e:   # a network error, a full disk: say it and carry on with the other parts
            traceback.print_exc()
            ok, detail = False, f"{type(e).__name__}: {e}; fix it and run again"
        results[part] = Result("built" if ok else "failed", detail)
        say()

    say("Summary")
    width = max(map(len, results))
    for part, r in results.items():
        say(f"  {part:<{width}}  {r.state:<8}  {r.detail}")
    if not a.check:
        lacking = [p for p, r in results.items() if r.state in ("blocked", "failed")]
        for line in how_to_run(results):
            say("  " + line)
        if a.shortcuts:
            made = shortcuts([("Sojourn", LAUNCHER_BIN, "Sojourn.exe"), ("Sojourn Access", ACCESS_BIN, "SojournAccess.exe")])
            say("  shortcuts: " + (", ".join(made) if made else "none made"))
        if lacking:
            say(f"  not complete: {', '.join(lacking)}. Fix what's named above and run this again; finished parts are kept.")
        return 1 if lacking else 0
    return 1 if any(r.state == "blocked" for r in results.values()) else 0


if __name__ == "__main__":
    sys.exit(main())
