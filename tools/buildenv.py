"""Where the build tools are on this PC: the .NET SDK, Visual Studio 2022's C++ tools, CMake, the Windows SDK.

Shared by tools/setup.py and the build scripts (client.py, launcher.py, access.py, voice.py, media.py), so they work on
any Windows PC and not only the studio's, whose .NET SDK is unpacked in %LOCALAPPDATA%\\dotnet-sdk and not on PATH.
Nothing here starts a process at import; vs2022() runs vswhere when called.
"""
import os
import shutil
import subprocess
from pathlib import Path

SDK_MAJOR = "10"


def _has_sdk(dotnet):
    return any((dotnet.parent / "sdk").glob(SDK_MAJOR + ".*"))


def dotnet_candidates():
    """Every dotnet.exe worth trying, in order: SOJOURN_DOTNET, the studio's unpacked SDK, DOTNET_ROOT, the machine-wide
    install, PATH, and dotnet-install.ps1's per-user default."""
    found = []
    local = os.environ.get("LOCALAPPDATA")
    for p in (os.environ.get("SOJOURN_DOTNET"),
              local and Path(local) / "dotnet-sdk" / "dotnet.exe",
              os.environ.get("DOTNET_ROOT") and Path(os.environ["DOTNET_ROOT"]) / "dotnet.exe",
              Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "dotnet" / "dotnet.exe",
              shutil.which("dotnet"),
              local and Path(local) / "Microsoft" / "dotnet" / "dotnet.exe"):
        if p and Path(p).is_file() and Path(p).resolve() not in found:
            found.append(Path(p).resolve())
    return found


def find_dotnet():
    """The first dotnet with a .NET 10 SDK beside it (a look at its sdk folder, no process), else the first dotnet at all,
    else plain "dotnet" for the error to name."""
    cands = dotnet_candidates()
    for c in cands:
        if _has_sdk(c):
            return c
    return cands[0] if cands else Path("dotnet")


DOTNET = find_dotnet()


def sdk_versions(dotnet=None):
    """The SDK versions that dotnet lists (empty if it can't run)."""
    try:
        out = subprocess.run([str(dotnet or DOTNET), "--list-sdks"], capture_output=True, text=True, timeout=60).stdout
    except OSError:
        return []
    return [line.split()[0] for line in out.splitlines() if line.strip()]


def apphost_finds_dotnet():
    """Whether an app's own .exe (SojournVR.exe, Sojourn.exe, SojournAccess.exe) can start: its apphost looks in
    DOTNET_ROOT, then the machine-wide install, for the .NET 10 runtime. If not, run `dotnet App.dll` instead."""
    roots = [os.environ.get("DOTNET_ROOT"), Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "dotnet"]
    return any(r and any((Path(r) / "shared" / "Microsoft.NETCore.App").glob(SDK_MAJOR + ".*")) for r in roots)


VSWHERE = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Microsoft Visual Studio" / "Installer" / "vswhere.exe"
VC_COMPONENT = "Microsoft.VisualStudio.Component.VC.Tools.x86.x64"
CMAKE_GENERATOR = "Visual Studio 17 2022"   # the native parts are built and tested with VS 2022 (v143)


def vs2022():
    """The install path of a Visual Studio 2022 (any edition, Build Tools included) with the x64 C++ tools, or None."""
    if VSWHERE.exists():
        try:
            out = subprocess.run([str(VSWHERE), "-products", "*", "-version", "[17.0,18.0)", "-requires", VC_COMPONENT,
                                  "-latest", "-property", "installationPath", "-utf8"],
                                 capture_output=True, text=True, encoding="utf-8", timeout=60).stdout.strip()
        except OSError:
            out = ""
        if out and Path(out).is_dir():
            return Path(out)
    fixed = Path(r"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools")
    return fixed if (fixed / "VC" / "Tools" / "MSVC").is_dir() else None


def other_vs():
    """Visual Studio installs other than 2022 with C++ tools ("2026 (18.x)" and so on), to say why they aren't used."""
    if not VSWHERE.exists():
        return []
    try:
        out = subprocess.run([str(VSWHERE), "-products", "*", "-all", "-requires", VC_COMPONENT, "-property",
                              "installationVersion", "-utf8"], capture_output=True, text=True, timeout=60).stdout
    except OSError:
        return []
    return [v.strip() for v in out.splitlines() if v.strip() and not v.startswith("17.")]


def cmake():
    """CMake: the one Visual Studio 2022 bundles (its "C++ CMake tools" component), else one on PATH, else None."""
    vs = vs2022()
    if vs:
        bundled = vs / "Common7" / "IDE" / "CommonExtensions" / "Microsoft" / "CMake" / "CMake" / "bin" / "cmake.exe"
        if bundled.exists():
            return str(bundled)
    return shutil.which("cmake")


def windows_sdk():
    """The newest Windows 10/11 SDK's version with its headers installed (what the C++ builds need), or None."""
    base = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Windows Kits" / "10" / "Include"
    versions = sorted((p.name for p in base.glob("10.*") if (p / "um" / "windows.h").exists()),
                      key=lambda v: [int(x) for x in v.split(".") if x.isdigit()])
    return versions[-1] if versions else None
