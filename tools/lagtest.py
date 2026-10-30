"""A controlled lag test in the headset (docs/vr.md > The rig, late frames): the local grid, a quiet place, CPU lag
dialled in on purpose, and the switches that might matter, with the pacing measures logged every 10 s.

  python tools/lagtest.py run [--lag 6-14] [--sim] [--minutes N]   start it (the headset over Link; --sim: Meta's
                                                                  XR Simulator instead); console and logs in local/lagtest
  python tools/lagtest.py set lag 8|6-14|off                       change the lag live (also menu > view > test lag)
  python tools/lagtest.py set wait on|off                          wait for each frame's GPU work before handing its images
                                                                  to the headset (on by default: the jitter fix; off for diagnosis)
  python tools/lagtest.py set mirror on|off                        the desktop window's mirror (menu > view > desktop mirror)
  python tools/lagtest.py set asw on|off                           Meta's frame synthesis (menu > view > frame synthesis)
  python tools/lagtest.py set depth on|off                         depth to the compositor (menu > view > depth reprojection)
  python tools/lagtest.py report [LOG]                             the run's pacing, a line per 10 s

The lag is CPU spun in each headset frame between xrWaitFrame and the render (Xr/LagHook.cs), so frames run long the
way a heavy scene's do while the GPU stays light. The two diagnostics test the whole-view candidates: the compositor
reading an eye image before our GPU work lands in it (an old image at a new pose: turning the wait off brought the
whole-view shake back; it's on by default since 2026-10-27), and the frame's one submission waiting on the 60 Hz
desktop window's image (mirror off). At 90 Hz (11.1 ms a frame) 6-14 ms puts frames between one and two
periods (the medium regime, where they alternate); 20-30 is heavy. Runs as Sojourn Lagger with its own base dir, so
the person's settings elsewhere aren't touched; the agent API is on :9793.
"""
import argparse
import json
import os
import re
import subprocess
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / "local" / "lagtest"
PORT = 9793
NAMES = {"lag": "test_lag", "wait": "test_wait_gpu", "mirror": "test_mirror", "asw": "frame_synthesis", "depth": "reprojection_depth"}


def run(a):
    sys.path.insert(0, str(ROOT / "tools"))
    import client
    import grid
    if not grid.ready():
        sys.exit("the local grid isn't up: python tools/grid.py start")
    grid.account("Sojourn", "Lagger")
    BASE.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, SOJOURN_LAG_TEST="1", SOJOURN_FRAME_LOG=str(BASE / "frames.csv"), SOJOURN_VR_LOG=str(BASE / "vr.csv"))
    if a.lag and a.lag != "off":
        env["SOJOURN_LAG_CPU_MS"] = a.lag
    args = ["--first", "Sojourn", "--last", "Lagger", "--password", "sojourn-local", "--base-dir", str(BASE),
            "--bridge-port", str(PORT), "--no-assistant", "--no-voice", "--no-media"]
    if a.minutes:
        args += ["--minutes", str(a.minutes)]
    if a.sim:
        env.update(client.simulator_env())
        env.pop("SOJOURN_FOREGROUND", None)
        return client.run(client.vr_args(["--xr-wait", "90", *args]), env)
    env["SOJOURN_FOREGROUND"] = "1"   # the person's run
    return client.run(client.vr_args(args), env)


def set_(a):
    name = NAMES.get(a.name, a.name)
    body = {"pump": "SojournSettings", "data": {"op": "set", name: a.value}, "timeout": 10}
    r = urllib.request.Request(f"http://127.0.0.1:{PORT}/cmd", data=json.dumps(body).encode(), method="POST",
                               headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(r, timeout=30) as resp:
        print(resp.read().decode())


def report(a):
    log = Path(a.log) if a.log else max((BASE / "logs").glob("session-*.log"), key=lambda p: p.stat().st_mtime)
    text = re.sub(r"\x1b\[[0-9;]*m", "", log.read_text(encoding="utf-8", errors="replace"))
    print(log)
    fps = None
    for line in text.splitlines():
        t = line[:8]
        if m := re.search(r"VR (\d+)/(\d+) fps, cpu ([\d.]+) ms.*gpu ([\d.]+) ms.*mirror (\w+), its image not free at submit in (\d+) of (\d+)", line):
            fps = f"{m[1]:>3} fps cpu {m[3]:>5} gpu {m[4]:>5} mirror {m[5]:3} ({m[6]}/{m[7]} waited)"
        elif "VR pacing:" in line:
            def g(p, d="-"):
                m = re.search(p, line)
                return m[1] if m else d
            steps = g(r"display steps (1: \d+%, 2: \d+%, 3\+: \d+%)")
            print(f"{t} {fps or '':60} steps {steps:22} changed {g(r'changed (\d+)'):>4}  woken {g(r'woken ([\d.]+ ms ahead \([\d-]+\))')}  "
                  f"lag {g(r'test lag ([\w.-]+) ms', 'off'):6} depth {g(r'; depth (\w+)'):3} wait {'off' if 'NOT waiting on the GPU' in line else 'on'}  "
                  f"err {g(r'as it was \(mean/p95/max\) ([^;]+?); prediction')}  moved {g(r'prediction moved by end ([^;]+)$')}")
        elif "VR runtime:" in line:
            print(f"{t}   runtime: {line.split('VR runtime: ', 1)[1]}")
        elif "frame synthesis:" in line:
            print(f"{t}   {line.split('] ', 1)[-1]}")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("run")
    r.add_argument("--lag", default="off")
    r.add_argument("--sim", action="store_true")
    r.add_argument("--minutes", type=float)
    s = sub.add_parser("set")
    s.add_argument("name")
    s.add_argument("value")
    p = sub.add_parser("report")
    p.add_argument("log", nargs="?")
    a = ap.parse_args()
    return {"run": run, "set": set_, "report": report}[a.cmd](a) or 0


if __name__ == "__main__":
    sys.exit(main())
