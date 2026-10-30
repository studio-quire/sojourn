"""The Sojourn launcher (src/Sojourn.Launcher, docs/launcher.md): build it, run it, draw its states.

  python tools/launcher.py build            dotnet build -c Release (seconds; the VR client builds by tools/client.py)
  python tools/launcher.py run [args...]    start it for the person (the foreground, no console), base dir local/main;
                                            args: --base-dir DIR, --desktop, and after -- the client's own
  python tools/launcher.py shots DIR        its states drawn offscreen to PNGs over made-up accounts (no window)

Run as `dotnet SojournLauncher.dll` because the apphost can't find .NET here (the SDK isn't a global install), as
tools/access.py does for Sojourn Access.
"""
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
PROJECT = ROOT / "src" / "Sojourn.Launcher" / "Sojourn.Launcher.csproj"
DLL = ROOT / "src" / "Sojourn.Launcher" / "bin" / "Release" / "net10.0-windows" / "SojournLauncher.dll"
BELOW_NORMAL = 0x00004000
CREATE_NO_WINDOW = 0x08000000


def build():
    return subprocess.run([str(DOTNET), "build", str(PROJECT), "-c", "Release", "-v:q", "-nologo"],
                          creationflags=BELOW_NORMAL).returncode


def run(args):
    if not DLL.exists() and build() != 0:
        return 1
    env = {**os.environ, "SOJOURN_FOREGROUND": "1"}   # the person's: the launcher and the client it starts take focus
    p = subprocess.Popen([str(DOTNET), str(DLL), *args], cwd=ROOT, env=env, stdin=subprocess.DEVNULL,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=CREATE_NO_WINDOW)
    print(f"the launcher started (pid {p.pid})")
    return 0


def shots(out):
    if build() != 0:
        return 1
    return subprocess.run([str(DOTNET), str(DLL), "--shots", str(Path(out).resolve())], cwd=ROOT).returncode


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "run"
    if cmd == "build":
        sys.exit(build())
    if cmd == "run":
        sys.exit(run(sys.argv[2:]))
    if cmd == "shots" and len(sys.argv) > 2:
        sys.exit(shots(sys.argv[2]))
    sys.exit(__doc__)
