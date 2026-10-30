"""Build and run the Sojourn client (Release).

  python tools/client.py build              dotnet build -c Release, 3 nodes, below-normal priority
  python tools/client.py run [options...]   run it (builds first if there's no build); options: docs/client.md
  python tools/client.py vr [options...]    run it in the headset (--vr --shadow-cap 1536); docs/vr.md
  python tools/client.py vr-sim [options]   the same against Meta's XR Simulator (local/xrsim) instead of the
                                            system's OpenXR runtime, for this process only

--logs (any command) writes the per-frame logs to local/client/: frames.csv (SOJOURN_FRAME_LOG) and, in the
headset, vr.csv (SOJOURN_VR_LOG: head, avatar, follow state), for checking a session afterwards.

Needs the local grid up for the defaults (python tools/grid.py start). With --validate, the Khronos OpenXR core
validation layer in local/openxr-sdk is offered to the loader too, and the Vulkan validation layer in
local/vulkan-validation/layer is turned on (docs/vr.md > Testing without a headset).
"""
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
PROJECT = ROOT / "src" / "Sojourn.Client" / "Sojourn.Client.csproj"
DLL = ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "SojournVR.dll"
XR_LAYERS = ROOT / "local" / "openxr-sdk" / "x64" / "bin" / "api_layers"
VK_LAYER = ROOT / "local" / "vulkan-validation" / "layer"
XR_SIM = ROOT / "local" / "xrsim"
BELOW_NORMAL = 0x00004000


def build():
    # voice chat's native shim first (tools/voice.py builds it when its sources changed; without LL's libwebrtc in
    # local/webrtc it says so and the client runs without voice)
    if (ROOT / "local" / "webrtc" / "lib").exists():
        subprocess.run([sys.executable, str(ROOT / "tools" / "voice.py"), "build"], creationflags=BELOW_NORMAL)
    return subprocess.run([str(DOTNET), "build", str(PROJECT), "-c", "Release", "-m:3", "-v:q", "-nologo"],
                          creationflags=BELOW_NORMAL).returncode


def run(args, env=None):
    if not DLL.exists() and build() != 0:
        return 1
    env = dict(env or os.environ)
    if "--logs" in args:
        args = [a for a in args if a != "--logs"]
        env["SOJOURN_FRAME_LOG"] = str(ROOT / "local" / "client" / "frames.csv")
        env["SOJOURN_VR_LOG"] = str(ROOT / "local" / "client" / "vr.csv")
    if "--validate" in args and XR_LAYERS.exists():
        env["XR_API_LAYER_PATH"] = str(XR_LAYERS)
    if "--validate" in args and VK_LAYER.exists():
        env["VK_ADD_LAYER_PATH"] = str(VK_LAYER)
    return subprocess.run([str(DOTNET), str(DLL), *args], cwd=ROOT, env=env).returncode


def vr_args(args):
    extra = ["--vr"]
    if "--shadow-cap" not in args:
        extra += ["--shadow-cap", "1536"]
    return extra + args


def simulator_env():
    manifests = sorted(XR_SIM.glob("*/meta_openxr_simulator.json"))
    if not manifests:
        sys.exit(f"no Meta XR Simulator under {XR_SIM} (docs/vr.md says how to unpack one)")
    env = dict(os.environ)
    env["XR_RUNTIME_JSON"] = str(manifests[-1])
    return env


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "run"
    rest = sys.argv[2:]
    if cmd == "build":
        sys.exit(build())
    if cmd == "vr":   # the real headset: the person's run, in the foreground (Window/Background.cs)
        sys.exit(run(vr_args(rest), {**os.environ, "SOJOURN_FOREGROUND": "1"}))
    if cmd == "vr-sim":
        sys.exit(run(vr_args(["--xr-wait", "90", *rest]), simulator_env()))
    if cmd != "run":   # --help or a typo once started a client on the default port
        sys.exit(__doc__ if cmd in ("-h", "--help", "help") else f"unknown command {cmd!r}\n{__doc__}")
    sys.exit(run(rest))
