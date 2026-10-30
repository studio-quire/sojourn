"""A timed client run on the performance grid (local/perf-grid, SOJOURN_GRID=perf-grid, the forest scene), logging
every frame, then a summary: frame and CPU times per 15 s window, what was still loading, the assets lines.

  python tools/perfrun.py LABEL [--dll DIR] [--cold] [--seconds 120] [--start Atelier/200/200/26] [--headset-size]
                          [--look X/Y/Z] [--say "spawner command" ...] [--set name=value ...] [--as First/Last]
                          [--port 9725] [--base DIR] [-- extra client args]

--cold empties the perf client's caches first (a first visit). --headset-size renders two Quest 3 eyes as the headset
does at its starting scale (1958x2138, the Quest's frusta, shadow cap 1536), vsync off, so CPU and GPU limits show.
--look turns the avatar (and the desktop view) toward a point once logged in; --say sends a line on chat channel 7
(the crowd's spawner, tools/crowd.py) then, each --say once; --set changes a setting (SojournSettings) as it starts. Results: local/perf/LABEL-frames.csv, LABEL.log, and the summary printed (and in LABEL.txt). Environment knobs pass
through (SOJOURN_FETCH_LATENCY_MS, SOJOURN_FETCH_KBPS: a far grid's latency and bandwidth for the asset fetches).
--as, --port and --base log in as another account (made with `SOJOURN_GRID=perf-grid python tools/grid.py account
First Last`), on another agent API port, with caches of its own: for two sessions working on the perf grid at once.
"""
import csv
import json
import os
import shutil
import statistics as st
import subprocess
import sys
import threading
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PERF = ROOT / "local" / "perf"
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
BELOW_NORMAL = 0x00004000


def q(xs, p):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, int(p * len(xs)))] if xs else float("nan")


def summary(path, window=15):
    rows = [[float(x) for x in r] for r in csv.reader(open(path)) if len(r) >= 11]
    if not rows:
        return "no frames"
    t0 = rows[0][0]
    out = [f"{len(rows)} frames over {rows[-1][0] - t0:.0f} s"]
    gc = len(rows[0]) >= 15   # the frame log's GC columns (gen0, gen1, gen2, pause ms) when the client writes them
    out.append("  window   frames  frame ms med/p95   cpu ms med/p95  scene  probes  record  rest  pending"
               + ("  cpu>11  gen0/s gen1/s  gc ms/s" if gc else "") + ("  loop" if len(rows[0]) >= 17 else ""))
    for k in range(0, int(rows[-1][0] - t0) + 1, window):
        rs = [r for r in rows if k <= r[0] - t0 < k + window]
        if not rs:
            continue
        out.append(f"  {k:4d}-{k + window:<4d} {len(rs):6d}  {q([r[1] for r in rs], .5):6.1f} {q([r[1] for r in rs], .95):6.1f}"
                   f"     {q([r[2] for r in rs], .5):6.1f} {q([r[2] for r in rs], .95):6.1f}  {st.mean(r[6] for r in rs):5.1f}  {st.mean(r[8] for r in rs):5.1f}"
                   f"  {st.mean(r[9] for r in rs):5.1f}  {st.mean(r[10] for r in rs):4.1f}  {st.mean(r[5] for r in rs):7.0f}"
                   + (f"  {sum(1 for r in rs if r[2] > 11.1):6d}  {sum(r[11] for r in rs) / window:6.1f} {sum(r[12] for r in rs) / window:6.2f}"
                      f"  {sum(r[14] for r in rs) / window:7.1f}" if gc and len(rs[0]) >= 15 else "")
                   # the loop's own CPU around the render (panels, name tags, input; the frame's end)
                   + (f"  {st.mean(r[15] + r[16] for r in rs):4.1f}" if len(rs[0]) >= 17 else ""))
    return "\n".join(out)


def api(op_pump, op, port=9725, **args):
    body = json.dumps({"pump": op_pump, "data": {"op": op, **args}, "timeout": 10}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}/cmd", data=body, headers={"Content-Type": "application/json"})
    return json.loads(urllib.request.urlopen(req, timeout=20).read())


def after_login(look, says, sets=(), port=9725):
    """Once the client is in the region: face a point, then send the spawner its lines."""
    end = time.time() + 90
    while time.time() < end:
        try:
            if json.loads(urllib.request.urlopen(f"http://127.0.0.1:{port}/status", timeout=3).read()).get("connected"):
                break
        except OSError:
            pass
        time.sleep(1)
    time.sleep(3)
    try:
        if sets:
            values = {}
            for kv in sets:
                k, v = kv.split("=", 1)
                values[k] = float(v) if v.replace(".", "", 1).isdigit() else v
            print("set:", api("SojournSettings", "set", port, **values).get("error", "ok"), flush=True)
        if look:
            x, y, z = (float(v) for v in look.split("/"))
            api("LLAgent", "lookAt", port, position=[x, y, z])
        for s in says:
            api("LLChatBar", "sendChat", port, message=s, channel=7, type="shout")
            time.sleep(2)
    except OSError as e:
        print("after login:", e, flush=True)


def main():
    args = sys.argv[1:]
    extra = []
    says, sets = [], []
    while "--say" in args:
        i = args.index("--say")
        says.append(args[i + 1])
        del args[i:i + 2]
    while "--set" in args:
        i = args.index("--set")
        sets.append(args[i + 1])
        del args[i:i + 2]
    if "--" in args:
        i = args.index("--")
        args, extra = args[:i], args[i + 1:]
    label = args[0]

    def opt(name, default=None):
        return args[args.index(name) + 1] if name in args else default

    dll_dir = Path(opt("--dll", ROOT / "local" / "perf-build" / "bin" / "Sojourn.Client" / "Release" / "net10.0"))
    seconds = float(opt("--seconds", "120"))
    region, x, y, z = opt("--start", "Atelier/200/200/26").split("/")
    base = Path(opt("--base", PERF / "client"))
    first, last = opt("--as", "Sojourn/Perf").split("/")
    port = int(opt("--port", "9725"))
    if "--cold" in args:
        for d in ("cache/grid",):
            shutil.rmtree(base / d, ignore_errors=True)
    base.mkdir(parents=True, exist_ok=True)
    frames = PERF / f"{label}-frames.csv"
    logp = PERF / f"{label}.log"
    env = dict(os.environ, SOJOURN_FRAME_LOG=str(frames))
    cmd = [str(DOTNET), str(dll_dir / "Sojourn.dll"), "--grid", "http://127.0.0.1:9740", "--first", first, "--last", last,
           "--password", "sojourn-local", "--base-dir", str(base), "--bridge-port", str(port), "--no-assistant", "--no-voice",
           "--start", f"uri:{region}&{x}&{y}&{z}", "--minutes", f"{seconds / 60:.3f}"]
    if "--headset-size" in args:
        cmd += ["--stereo", "--quest-eyes", "--eye", "1958x2138", "--fov", "98", "--shadow-cap", "1536", "--no-vsync", "--window", "1600x900"]
    if "--vr-sim" in args:
        # Meta's XR Simulator with the rig's scripted routine (stick walks, turns, flying), logging the rig per frame
        sims = sorted((ROOT / "local" / "xrsim").glob("*/meta_openxr_simulator.json"))
        env.update(XR_RUNTIME_JSON=str(sims[-1]), SOJOURN_VR_SCRIPT=os.environ.get("SOJOURN_VR_SCRIPT", "1"), SOJOURN_VR_LOG=str(PERF / f"{label}-vr.csv"))
        cmd += ["--vr", "--xr-wait", "90", "--shadow-cap", "1536"]
    cmd += extra
    # OpenSim refuses a login while the last run's presence lingers ("login failed: key"): wait and try again
    for attempt in range(6):
        t = time.time()
        with open(logp, "w", encoding="utf-8") as log:
            p = subprocess.Popen(cmd, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=BELOW_NORMAL)
            if opt("--look") or says or sets:
                threading.Thread(target=after_login, args=(opt("--look"), says, sets, port), daemon=True).start()
            refused = False
            while p.poll() is None:
                time.sleep(1)
                if time.time() - t < 40 and "login failed" in logp.read_text(encoding="utf-8", errors="replace"):
                    refused = True
                    p.kill()
                    p.wait()
                    break
        if not refused:
            break
        print("login refused; trying again in 20 s", flush=True)
        time.sleep(20)
    text = f"{label}: exit {p.returncode} after {time.time() - t:.0f} s\n" + summary(frames)
    assets = [l.strip() for l in open(logp, encoding="utf-8", errors="replace") if "assets:" in l]
    if assets:
        text += "\n  " + "\n  ".join(assets[-4:])
    gpu = [l.strip() for l in open(logp, encoding="utf-8", errors="replace") if " gpu: " in l]
    if gpu:
        text += "\n  " + "\n  ".join(gpu[-3:])
    (PERF / f"{label}.txt").write_text(text, encoding="utf-8")
    print(text)


if __name__ == "__main__":
    if sys.argv[1:2] == ["summary"]:
        print(summary(sys.argv[2]))
    else:
        main()
