"""A long Sojourn Access session that moves around, for its memory and garbage: what a blind tester's 3.5-hour ride across
many regions did on an 8 GB PC (docs/access-app.md > Memory over a long session).

  python tools/accesssoak.py LABEL [--minutes 40] [--gpu] [--unseen] [--sit] [--threads 8] [--dump-at 12,35]
                             [--trace-at 20] [--bin DIR] [--stay 45] [--as First/Last --port N] [-- extra app args]

On the perf grid's "places" scene (eight regions of SL-like density: the forest's four and the four far places; set up
as tools/teleports.py says), as Access Soak (`SOJOURN_GRID=perf-grid python tools/grid.py account Access Soak`;
--as another account and --port another agent API port for a second run beside it). The app
runs on a hidden desktop (QuireUia's launcher), its view on the processor (Vulkan's drivers hidden, local/lavapipe,
LP_NUM_THREADS=--threads; --gpu leaves the GPU), no assistant, no voice, world audio to a file. Every --stay seconds it
goes somewhere: a teleport to the next stop, or a walk by the autopilot across into the next region, so regions keep
coming and going; --sit: teleports only, each followed by sitting on the nearest thing (seated, as on a train, the
app's own code runs differently). Unless --unseen, a picture is asked for every 5 minutes (SojournDescribe's look at the place), so the
view stays drawn whole, as with an assistant up; the first after 2 minutes.

Every 10 s: the process's private bytes and working set, and the app's own memory line (heap, fragmented, what the
last full collection left, collections, pauses, allocated). --dump-at minutes: tools/heapwho.py (what holds the heap);
--trace-at minutes: tools/alloctrace.py for 20 s (where allocations come from). Out: local/access-soak/LABEL.csv, its
dumps and traces beside it, LABEL-summary.txt. --bin runs another build (default: the usual one, or SOJOURN_BUILD_ROOT's).

Written by Claude Opus 5.5, 2026-11-01.
"""
import csv
import json
import os
import re
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

import psutil

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import access  # noqa: E402  (the app's build paths, QuireUia's hidden launcher)

OUT = ROOT / "local" / "access-soak"
GRID_URI = "http://127.0.0.1:9740/"
USER, PASSWORD = "Access Soak", "sojourn-local"
API_PORT = 9830
# (region, x, y, z): teleports; ("walk", dx, dy): the autopilot that far from where it stands (into the next region)
ROUTE = [("Atelier", 200, 200, 30), ("walk", 0, 90), ("Place One", 128, 128, 40), ("Atelier East", 60, 60, 30),
         ("walk", -90, 0), ("Place Two", 128, 128, 40), ("Atelier Northeast", 128, 128, 30), ("walk", 0, -100),
         ("Place Three", 128, 128, 40), ("Atelier North", 128, 100, 30), ("walk", 100, 0), ("Place Four", 128, 128, 40)]
MEM = re.compile(r"memory (\d+) MB \(heap (\d+) MB, (\d+) fragmented, (\d+) committed; last full collection left gen2 (\d+), "
                 r"large (\d+)\); gc (\d+)/(\d+)/(\d+), paused (\d+) ms, (\d+) MB allocated")


def api(op_pump, op, timeout=30, **args):
    body = json.dumps({"pump": op_pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{API_PORT}/cmd", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 20) as r:
        return json.loads(r.read())


def connected():
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{API_PORT}/status", timeout=5) as r:
            return json.loads(r.read()).get("connected")
    except OSError:
        return False


def last_memory(log):
    try:
        text = log.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return None
    found = MEM.findall(text[-200_000:])
    return [int(x) for x in found[-1]] if found else None


def main():
    args = sys.argv[1:]
    extra = []
    if "--" in args:
        i = args.index("--")
        args, extra = args[:i], args[i + 1:]
    label = args[0]

    def opt(name, default=None):
        return args[args.index(name) + 1] if name in args else default

    global API_PORT
    minutes, stay = float(opt("--minutes", "40")), float(opt("--stay", "45"))
    API_PORT = int(opt("--port", API_PORT))
    user = opt("--as", USER.replace(" ", "/")).replace("/", " ")
    dumps = [float(x) for x in opt("--dump-at", "").split(",") if x]
    traces = [float(x) for x in opt("--trace-at", "").split(",") if x]
    app_bin = Path(opt("--bin", access.APP_BIN))
    OUT.mkdir(parents=True, exist_ok=True)
    base = OUT / f"{label}-app"
    env = dict(os.environ, SOJOURN_TEST_NO_NVDA="1", SOJOURN_TEST_NO_CLIPBOARD="1",
               SOJOURN_VOICE_OUT_FILE=str(OUT / f"{label}-heard.wav"), SOJOURN_SOFTWARE_VULKAN=str(access.LAVAPIPE),
               LP_NUM_THREADS=opt("--threads", "8"))
    if "--gpu" not in args:
        env["VK_LOADER_DRIVERS_DISABLE"] = "*"
    cmd = [access.DOTNET, app_bin / "SojournAccess.dll", "--grid", GRID_URI, "--user", user, "--password", PASSWORD,
           "--start", "uri:Atelier&200&200&30", "--port", API_PORT, "--base", base, "--no-assistant", "--reader", "on",
           "--autologin", "--no-voice", *extra]
    pid = access.launch_hidden(cmd, env=env, cwd=app_bin)
    proc = psutil.Process(pid)
    print(f"app pid {pid}", flush=True)
    t0 = time.time()
    rows, notes = [], []
    try:
        if not access.wait(connected, 120, 1):
            print("not connected after 120 s")
            return 1
        log = max((base / "logs").glob("session-*.log"), key=lambda p: p.stat().st_mtime)
        step, next_move, next_look = 0, time.time() + stay, time.time() + 120
        end = t0 + minutes * 60
        while time.time() < end and proc.is_running():
            now = time.time()
            m = (now - t0) / 60
            if dumps and m >= dumps[0]:
                dumps.pop(0)
                r = subprocess.run([sys.executable, str(ROOT / "tools" / "heapwho.py"), "--pid", str(pid), "--label", f"{label}-{m:.0f}m", "--top", "40"],
                                   capture_output=True, text=True)
                (OUT / f"{label}-heap-{m:.0f}m.txt").write_text(r.stdout + r.stderr, encoding="utf-8")
                notes.append(f"{m:5.1f} min: heap dump")
            if traces and m >= traces[0]:
                traces.pop(0)
                r = subprocess.run([sys.executable, str(ROOT / "tools" / "alloctrace.py"), "20", "--pid", str(pid), "--label", f"{label}-{m:.0f}m", "--top", "40"],
                                   capture_output=True, text=True)
                (OUT / f"{label}-alloc-{m:.0f}m.txt").write_text(r.stdout + r.stderr, encoding="utf-8")
                notes.append(f"{m:5.1f} min: allocation trace")
            if now >= next_move:
                what = ROUTE[step % len(ROUTE)]
                step += 1
                try:
                    if what[0] == "walk" and "--sit" in args:
                        notes.append(f"{m:5.1f} min: (no walk, seated)")
                    elif what[0] == "walk":
                        g = api("LLAgent", "getPosition")["global"]
                        api("LLAgent", "startAutoPilot", target_global=[g[0] + what[1], g[1] + what[2], g[2]], stop_distance=2)
                        notes.append(f"{m:5.1f} min: walk {what[1]:+},{what[2]:+}")
                    else:
                        r = api("LLAgent", "requestTeleport", 40, regionname=what[0], x=what[1], y=what[2], z=what[3], skip_confirmation=True)
                        notes.append(f"{m:5.1f} min: teleport {what[0]}: {json.dumps(r.get('data', r))[:80]}")
                        if "--sit" in args:
                            time.sleep(3)
                            near = api("LLAgent", "getNearbyObjectsList", dist=12).get("result") or []
                            sat = None
                            for o in near[:6]:
                                try:
                                    sat = api("LLAgent", "requestSit", 20, obj_uuid=o["id"]).get("sitting_on")
                                except Exception:
                                    sat = None
                                if sat:
                                    break
                            notes.append(f"{m:5.1f} min: sat on {sat}")
                except Exception as e:
                    notes.append(f"{m:5.1f} min: {what}: {type(e).__name__} {e}")
                print(notes[-1], flush=True)
                next_move = time.time() + stay
            if "--unseen" not in args and now >= next_look:
                t1 = time.time()
                try:
                    r = api("SojournDescribe", "look", 120, subject="here", detail="brief")
                    notes.append(f"{m:5.1f} min: a look, {time.time() - t1:.1f} s, {len(r.get('pictures') or [])} pictures")
                except Exception as e:
                    notes.append(f"{m:5.1f} min: a look failed: {e}")
                print(notes[-1], flush=True)
                next_look = time.time() + 300
            try:
                mi = proc.memory_info()
                cpu = proc.cpu_times()
                row = [round(time.time() - t0, 1), mi.private // 2**20, mi.rss // 2**20, round(cpu.user + cpu.system, 1)] + (last_memory(log) or [])
                rows.append(row)
            except psutil.Error:
                break
            time.sleep(10)
    finally:
        try:
            proc.kill()
        except psutil.Error:
            pass
        # (the app's dotnet host: anything left of it on our base dir)
        for p in psutil.process_iter(["cmdline"]):
            if str(base).lower() in " ".join(p.info["cmdline"] or []).lower():
                p.kill()
    head = ["s", "private_mb", "ws_mb", "cpu_s", "log_ws", "heap", "frag", "committed", "gen2_left", "loh_left", "gc0", "gc1", "gc2", "paused_ms", "allocated_mb"]
    with open(OUT / f"{label}.csv", "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(head)
        w.writerows(rows)
    full = [r for r in rows if len(r) == len(head)]
    out = [f"{label}: {len(rows)} samples over {(rows[-1][0] if rows else 0) / 60:.1f} min"]
    if len(full) >= 2:
        a, b = full[0], full[-1]
        secs = b[0] - a[0]
        out.append(f"  private bytes {a[1]} -> {b[1]} MB (most {max(r[1] for r in full)}), working set {a[2]} -> {b[2]} MB (most {max(r[2] for r in full)})")
        out.append(f"  heap {a[5]} -> {b[5]} MB (most {max(r[5] for r in full)}); last full collection left gen2 {b[8]}, large {b[9]} MB (most {max(r[8] + r[9] for r in full)} together)")
        out.append(f"  allocated {(b[14] - a[14]) / secs:.1f} MB/s; collections {b[10] - a[10]}/{b[11] - a[11]}/{b[12] - a[12]}, "
                   f"paused {(b[13] - a[13]) / secs * 60:.0f} ms a minute; CPU {(b[3] - a[3]) / secs * 100:.0f}% of a core")
        # the quieter half (no look, no arrival): the steady rate
        for lo, hi in ((0.25, 0.5), (0.5, 0.75), (0.75, 1.0)):
            p, q = full[int(lo * (len(full) - 1))], full[int(hi * (len(full) - 1))]
            if q[0] > p[0]:
                out.append(f"  {lo * 100:.0f}-{hi * 100:.0f}%: allocated {(q[14] - p[14]) / (q[0] - p[0]):.1f} MB/s, heap {q[5]} MB, "
                           f"private {q[1]} MB, paused {(q[13] - p[13]) / (q[0] - p[0]) * 60:.0f} ms a minute")
    out += ["  " + n for n in notes]
    text = "\n".join(out)
    (OUT / f"{label}-summary.txt").write_text(text, encoding="utf-8")
    print(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
