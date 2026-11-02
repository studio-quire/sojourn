"""How soon the first description's pictures come after logging in, with the view on the processor (Mesa's lavapipe), as
on the blind tester's PC (docs/performance.md > Sojourn Access on a small PC > The first picture).

  python tools/firstpicture.py LABEL [--when cold|warm|both] [--threads 8] [--cores 8] [--sample SECONDS] [--gpu]
                               [--bin DIR] [--wait 600] [--fresh] [--burn N]
                               [--start uri:Region&x&y&z] [--as First/Last --port N] [-- extra app args]

On the perf grid's forest (SOJOURN_GRID=perf-grid python tools/grid.py start; the forest scene), as Access Soak, Access
on a hidden desktop with Vulkan's drivers hidden (local/lavapipe, LP_NUM_THREADS=--threads) and held to the first
--cores logical processors (8: four cores and their twins, the tester's 2400G's shape), no assistant
(SOJOURN_OFFSCREEN_SEEN=1 stands in for one being up), no voice. Then:
- cold: a description of yourself asked for 20 s after the view is up (as the tester did; the warm-up starts then too),
  then of the place;
- warm: waits for the view's own warm-up to finish (its log line), then the same two;
then both again (warm pictures, saved as LABEL-SUBJECT-N.png). Each look's seconds, and the view's "on the processor" log lines with their times
from the view coming up. --sample S: tools/lvpsample on the app for the first S seconds after the view is up (where the
processor's time went: LLVM compiling, the shaders it made, the rasteriser). Out: local/firstpic/LABEL.txt.

Written by Claude Opus 5.5, 2026-11-01.
"""
import json
import os
import re
import subprocess
import sys
import time
import urllib.request
from datetime import datetime
from pathlib import Path

import psutil

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import access  # noqa: E402

OUT = ROOT / "local" / "firstpic"
GRID_URI = "http://127.0.0.1:9740/"
PASSWORD = "sojourn-local"
SAMPLER = ROOT / "tools" / "lvpsample" / "bin" / "Release" / "net10.0" / "lvpsample.dll"
PDB = next(iter(sorted((ROOT / "local").glob("mesa-*-pdb"))), None)
API_PORT = 9832


def api(pump, op, timeout=30, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{API_PORT}/cmd", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 30) as r:
        return json.loads(r.read())


def connected():
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{API_PORT}/status", timeout=5) as r:
            return json.loads(r.read()).get("connected")
    except OSError:
        return False


def log_lines(log):
    try:
        return log.read_text(encoding="utf-8", errors="replace").splitlines()
    except OSError:
        return []


def stamp(line):
    m = re.match(r"(\d\d:\d\d:\d\d)", line)
    return datetime.strptime(m.group(1), "%H:%M:%S") if m else None


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
    API_PORT = int(opt("--port", API_PORT))
    when = opt("--when", "cold")
    user = opt("--as", "Access/Soak").replace("/", " ")
    threads, cores = opt("--threads", "8"), int(opt("--cores", "8"))
    app_bin = Path(opt("--bin", access.APP_BIN)).resolve()
    wait_s = float(opt("--wait", "600"))
    OUT.mkdir(parents=True, exist_ok=True)
    # shared by every run, so the asset cache stays warm as on a PC that's been to places before; --fresh: a new one,
    # everything fetched and decoded while the first pictures are drawn, as on arriving somewhere new
    base = OUT / (f"{label}-app" if "--fresh" in args else "app")
    env = dict(os.environ, SOJOURN_TEST_NO_NVDA="1", SOJOURN_TEST_NO_CLIPBOARD="1", SOJOURN_OFFSCREEN_SEEN="1",
               SOJOURN_SOFTWARE_VULKAN=str(access.LAVAPIPE), LP_NUM_THREADS=threads)
    if "--gpu" not in args:
        env["VK_LOADER_DRIVERS_DISABLE"] = "*"
    cmd = [access.DOTNET, app_bin / "SojournAccess.dll", "--grid", GRID_URI, "--user", user, "--password", PASSWORD,
           "--start", opt("--start", "uri:Atelier&128&140&30"), "--port", API_PORT, "--base", base, "--no-assistant", "--reader", "on",
           "--autologin", "--no-voice", *extra]
    t_launch = time.time()
    pid = access.launch_hidden(cmd, env=env, cwd=app_bin)
    proc = psutil.Process(pid)
    if cores < psutil.cpu_count():
        proc.cpu_affinity(list(range(cores)))
    out = [f"{label}: threads {threads}, cores {cores}, when {when}, {app_bin}"]
    # --burn N: N processes spinning at normal priority on the same processors from the launch on, standing in for what
    # else wants the processor after login on a slow PC (arrival's decoding, the assistant's start, the screen reader)
    burners = []
    for _ in range(int(opt("--burn", "0"))):
        b = subprocess.Popen([sys.executable, "-c", "while True: pass"])
        psutil.Process(b.pid).cpu_affinity(list(range(min(cores, psutil.cpu_count()))))
        burners.append(b)

    def say(s):
        out.append(s)
        print(s, flush=True)

    sampler = None
    try:
        if not access.wait(connected, 180, 1):
            say("not connected after 180 s")
            return 1
        say(f"connected {time.time() - t_launch:.1f} s after launch")
        log = None
        up_line = None
        end = time.time() + 300
        while time.time() < end and up_line is None:
            logs = sorted((base / "logs").glob("session-*.log"), key=lambda p: p.stat().st_mtime)
            if logs and logs[-1].stat().st_mtime >= t_launch - 5:
                log = logs[-1]
                up_line = next((l for l in log_lines(log) if "the view out of sight is up" in l), None)
            time.sleep(0.5)
        if up_line is None:
            say("the view never came up")
            return 1
        t_up = time.time()
        say(f"view up {t_up - t_launch:.1f} s after launch: {up_line[:160]}")
        if "--sample" in args and SAMPLER.exists():
            sampler = subprocess.Popen([access.DOTNET, SAMPLER, str(pid), opt("--sample"), "--symbols", str(PDB or ""),
                                        "--csv", str(OUT / f"{label}-sample.csv")],
                                       stdout=open(OUT / f"{label}-sample.txt", "w", encoding="utf-8"), stderr=subprocess.STDOUT)
        if when == "warm":
            end = time.time() + wait_s
            while time.time() < end and not any("drawn once ahead of any picture" in l for l in log_lines(log)):
                time.sleep(1)
            say(f"warm-up done {time.time() - t_up:.1f} s after the view came up")
        else:
            time.sleep(max(0.0, t_up + 20 - time.time()))   # (when the tester asked, 20 s after it was up)

        def look(subject, save=False):
            t0 = time.time()
            try:
                r = api("SojournDescribe", "look", wait_s, subject=subject, detail="brief")
                pics = r.get("pictures") or []
                if save:   # (the warm pictures, to look at: LABEL-SUBJECT-N.png)
                    import base64
                    for i, p in enumerate(pics):
                        if p.get("png"):
                            (OUT / f"{label}-{subject}-{i}.png").write_bytes(base64.b64decode(p["png"]))
                note = "; ".join(str(n) for n in (r.get("notes") or []))[:160]
                say(f"  look {subject}: {time.time() - t0:.1f} s, {len(pics)} pictures"
                    f" ({', '.join(f'{p.get('width')}x{p.get('height')}' for p in pics)}){' - ' + note if note else ''}")
            except Exception as e:
                say(f"  look {subject}: failed after {time.time() - t0:.1f} s: {e}")

        say(f"first looks, {time.time() - t_up:.1f} s after the view came up:")
        look("me")
        look("here")
        say("again:")
        look("me", save=True)
        look("here", save=True)
        if sampler:
            sampler.wait(timeout=float(opt("--sample")) + 120)
        t0 = stamp(up_line)
        say("the view's log:")
        for l in log_lines(log):
            if "on the processor" in l or "dearest" in l:
                t = stamp(l)
                rel = f"{(t - t0).total_seconds():6.0f} s" if t and t0 else "?"
                say(f"  {rel}  {l[9:][:260]}")
    finally:
        try:
            proc.kill()
        except psutil.Error:
            pass
        for p in psutil.process_iter(["cmdline"]):
            try:
                if str(base).lower() in " ".join(p.info["cmdline"] or []).lower():
                    p.kill()
            except psutil.Error:
                pass
        if sampler and sampler.poll() is None:
            sampler.kill()
        for b in burners:
            b.kill()
        (OUT / f"{label}.txt").write_text("\n".join(out), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
