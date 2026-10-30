"""A long session's teleports on the performance grid: does loading slow down as places pile up behind you?

  python tools/teleports.py LABEL [--dll DIR] [--cold] [--stops 13] [--stay 70] [--as First/Last] [--port 9726]
                             [--base DIR] [-- extra client args]

Setup: the perf grid with the four far places: once, tools/forest.py places --load (builds them, adds their regions,
saves scene "places"); after that, put local/perf-grid/places/*.ini into local/perf-grid/bin/Regions and
`SOJOURN_GRID=perf-grid python tools/grid.py scene places`, and take the .ini files out again afterwards (other scenes
don't have the places; the regions would stand empty on ports 9744-9747).

Logs in at the forest (local/perf-grid, scene "places") and teleports round the forest
and the four far places, a stop every --stay seconds, as the person's 2026-10-27 session did over an hour. Per stop:
where the teleport said it arrived (against where it was sent), how long until what's in view had loaded (frames.csv's
pending count under half and under a tenth of the stop's peak, and under 20), and the console's last word on the
asset queues there. Results in local/perf/LABEL.log, LABEL-frames.csv, LABEL-stops.txt. SOJOURN_FETCH_LATENCY_MS and
SOJOURN_FETCH_KBPS pass through (a far grid's latency and bandwidth for asset fetches).
"""
import csv
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PERF = ROOT / "local" / "perf"
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
BELOW_NORMAL = 0x00004000
ROUTE = [("Place One", 128, 128, 40), ("Place Two", 128, 128, 40), ("Atelier", 200, 200, 30), ("Place Three", 128, 128, 40),
         ("Place Four", 128, 128, 40), ("Place One", 60, 190, 40), ("Atelier North", 128, 100, 30)]
ANSI = re.compile(r"\x1b\[[0-9;]*m")


def api(pump, op, port, timeout=40, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}/cmd", data=body, headers={"Content-Type": "application/json"})
    return json.loads(urllib.request.urlopen(req, timeout=timeout + 10).read())


def settle_times(rows, t_from, t_to):
    """From an arrival: seconds until the pending count fell under half and a tenth of its peak in the stop, and
    under 20 (None if it never did)."""
    rs = [(r[0], r[5]) for r in rows if t_from <= r[0] < t_to]
    if not rs:
        return None
    peak = max(p for _, p in rs)
    peak_at = next(t for t, p in rs if p == peak)
    out = {"peak": int(peak)}
    for name, limit in (("half", peak / 2), ("tenth", peak / 10), ("under20", 20)):
        out[name] = next((round(t - t_from, 1) for t, p in rs if t >= peak_at and p <= limit), None)
    return out


def main():
    args = sys.argv[1:]
    extra = []
    if "--" in args:
        i = args.index("--")
        args, extra = args[:i], args[i + 1:]
    label = args[0]

    def opt(name, default=None):
        return args[args.index(name) + 1] if name in args else default

    dll_dir = Path(opt("--dll", ROOT / "local" / "perf-build" / "bin" / "Sojourn.Client" / "Release" / "net10.0"))
    stops, stay = int(opt("--stops", "13")), float(opt("--stay", "70"))
    port = int(opt("--port", "9726"))
    base = Path(opt("--base", PERF / "teleports-client"))
    first, last = opt("--as", "Sojourn/Perf").split("/")
    if "--cold" in args:
        shutil.rmtree(base / "cache" / "grid", ignore_errors=True)
    base.mkdir(parents=True, exist_ok=True)
    PERF.mkdir(parents=True, exist_ok=True)
    frames, logp = PERF / f"{label}-frames.csv", PERF / f"{label}.log"
    env = dict(os.environ, SOJOURN_FRAME_LOG=str(frames))
    cmd = [str(DOTNET), str(dll_dir / "Sojourn.dll"), "--grid", "http://127.0.0.1:9740", "--first", first, "--last", last,
           "--password", "sojourn-local", "--base-dir", str(base), "--bridge-port", str(port), "--no-assistant", "--no-voice",
           "--start", "uri:Atelier&200&200&30", "--minutes", f"{(stops + 1) * stay / 60 + 2:.2f}", *extra]
    t0 = time.time()
    log = open(logp, "w", encoding="utf-8")
    p = subprocess.Popen(cmd, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=BELOW_NORMAL)
    stops_out = []
    try:
        end = time.time() + 120
        while time.time() < end:
            try:
                if json.loads(urllib.request.urlopen(f"http://127.0.0.1:{port}/status", timeout=3).read()).get("connected"):
                    break
            except OSError:
                pass
            time.sleep(1)
        time.sleep(stay)
        stops_out.append({"to": "Atelier/200/200/30 (login)", "at": 0.0})
        for k in range(stops):
            region, x, y, z = ROUTE[k % len(ROUTE)]
            asked = time.time() - t0
            try:
                r = api("LLAgent", "requestTeleport", port, regionname=region, x=x, y=y, z=z, skip_confirmation=True, timeout=30)
            except OSError as e:
                r = {"error": str(e)}
            at = time.time() - t0
            stops_out.append({"to": f"{region}/{x}/{y}/{z}", "asked": round(asked, 1), "at": round(at, 1),
                              "reply": r.get("data", r) if isinstance(r, dict) else r})
            print(f"{at:7.1f} s  -> {region}: {json.dumps(stops_out[-1]['reply'])[:200]}", flush=True)
            time.sleep(max(0.0, stay - (time.time() - t0 - at)))
    finally:
        p.terminate()
        try:
            p.wait(20)
        except subprocess.TimeoutExpired:
            p.kill()
        log.close()

    rows = [[float(x) for x in r] for r in csv.reader(open(frames)) if len(r) >= 11] if frames.exists() else []
    # the frame log's clock starts with the process; ours with its launch: close enough at these scales
    lines = [ANSI.sub("", l) for l in open(logp, encoding="utf-8", errors="replace")]
    views = [l for l in lines if "assets:" in l]
    out = [f"{label}: {len(stops_out)} stops, {stay:.0f} s each"]
    for i, s in enumerate(stops_out):
        t_from = s["at"]
        t_to = stops_out[i + 1]["at"] if i + 1 < len(stops_out) else t_from + stay
        st = settle_times(rows, t_from, t_to)
        reply = s.get("reply", {})
        pos = reply.get("pos") if isinstance(reply, dict) else None
        out.append(f"  {t_from:6.0f} s  {s['to']:<26} arrived pos {pos}  loading: {st}")
    out.append("  last asset lines:")
    out += ["    " + v.strip()[v.find("assets:"):] for v in views[-3:]]
    text = "\n".join(out)
    (PERF / f"{label}-stops.txt").write_text(text, encoding="utf-8")
    print(text)


if __name__ == "__main__":
    main()
