"""Batch B3b's avatars on the local grid, never the main grid: another person's viewer effects seen (a PointAt raising
their left arm, an edit beam, a spiral), their ResetSkeleton heard, ours sent and relayed, the fixes' ops, and lip sync
drawn (SOJOURN_TEST_LIPSYNC=1: everyone else speaks). The logic itself is tools/avatarfx check (no grid).

  python tools/avatars.py [--dll DIR]     Sojourn Peer (1280x720, the observer) on :9873; Sojourn Friend logged in by
                                          tools/avatarfx send (LibreMetaverse, no window) 2 m in front of it, facing it

Prints each check and the shots' paths to look at (local/avatars/). Written by Claude Opus 5.5, 2026-10-29.
"""
import json
import os
import subprocess
import sys
import threading
import time
import urllib.request
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
BASE = ROOT / "local" / "avatars"
PORT = 9873
W, H = 1280, 720
ok_all = True


def api(pump, op, timeout=20, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}/cmd", data=body, method="POST", headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 60) as r:
        out = json.loads(r.read() or b"null")
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(out["error"])
    return out


def wait(f, seconds, step=0.5):
    end = time.time() + seconds
    while time.time() < end:
        try:
            if v := f():
                return v
        except Exception:
            pass
        time.sleep(step)
    return None


def check(name, cond, detail=""):
    global ok_all
    ok_all &= bool(cond)
    print(f"{'ok  ' if cond else 'FAIL'} {name}: {str(detail)[:300]}", flush=True)


def magenta(img):
    r, g, b = img[..., 0], img[..., 1], img[..., 2]
    return int(((r - g > 60) & (b - g > 60)).sum())


def main(dll):
    sys.path.insert(0, str(ROOT / "tools"))
    import grid
    if "agni" in grid.URL or not grid.ready():
        sys.exit("the local grid isn't up (python tools/grid.py start)")
    for last in ("Peer", "Friend"):
        grid.account("Sojourn", last, "sojourn-local")
    BASE.mkdir(parents=True, exist_ok=True)
    (BASE / "a").mkdir(exist_ok=True)
    built = subprocess.run([str(DOTNET), "build", str(ROOT / "tools" / "avatarfx"), f"-p:SojournOut={dll}", "-o", str(BASE / "fx"), "-v:q", "-nologo"],
                           capture_output=True, text=True, creationflags=0x00004000)
    if built.returncode != 0:
        sys.exit("tools/avatarfx didn't build: " + built.stdout[-2000:])
    env = dict(os.environ, SOJOURN_TEST_LIPSYNC="1")
    log = open(BASE / "a-console.txt", "w", encoding="utf-8")
    pa = subprocess.Popen([str(DOTNET), str(Path(dll) / "Sojourn.dll"), "--first", "Sojourn", "--last", "Peer", "--password", "sojourn-local",
                           "--bridge-port", str(PORT), "--window", f"{W}x{H}", "--fps", "20", "--no-assistant", "--no-voice", "--no-sounds",
                           "--base-dir", str(BASE / "a"), "--start", "uri:Atelier&124&112&22", "--minutes", "8"],
                          cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, creationflags=0x00004000)
    procs = [pa]

    def key(vk):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winkey.ps1"), "-Vk", str(vk), "-ProcessId", str(pa.pid)],
                       capture_output=True)

    def shot(name):
        shots = BASE / "a" / "shots"
        before = set(shots.glob("*.png")) if shots.exists() else set()
        key(123)
        new = wait(lambda: set(shots.glob("*.png")) - before, 10)
        if not new:
            print(f"     no shot for {name}", flush=True)
            return None
        time.sleep(0.4)
        path = sorted(new)[-1]
        dest = BASE / f"{name}.png"
        dest.write_bytes(path.read_bytes())
        print(f"     shot {name}: {dest}", flush=True)
        return np.asarray(Image.open(path).convert("RGB")).astype(int)

    def console():
        return (BASE / "a-console.txt").read_text(encoding="utf-8", errors="replace")

    try:
        up = wait(lambda: json.loads(urllib.request.urlopen(f"http://127.0.0.1:{PORT}/status", timeout=3).read()).get("connected"), 120)
        check("Peer logged in", up)
        me = api("LLAgent", "getID")["id"]
        here = api("LLAgent", "getPosition")["region"]
        # Friend, by LibreMetaverse, 2 m to the west, facing east (toward Peer) as a new arrival does
        fx_log = []
        fx = subprocess.Popen([str(DOTNET), str(BASE / "fx" / "avatarfx.dll"), "send", grid.URL, "Sojourn", "Friend", "sojourn-local", f"uri:Atelier&{round(here[0]) - 2}&{round(here[1])}&{round(here[2])}", me, "7"],
                              cwd=ROOT, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, creationflags=0x00004000)
        procs.append(fx)
        stages = {}

        def read():
            for line in fx.stdout:
                line = line.strip()
                fx_log.append(line)
                if line.startswith("stage "):
                    stages[line[6:]] = time.time()
        threading.Thread(target=read, daemon=True).start()
        friend = wait(lambda: next((l[10:] for l in fx_log if l.startswith("logged in ")), None), 90)
        check("Friend logged in (avatarfx)", friend, fx_log[-3:])
        if not friend:
            return
        fpos = wait(lambda: next(([float(v) for v in l.split()[1:4]] for l in fx_log if l.startswith("at ")), None), 10) or [here[0] - 2, here[1], here[2]]
        print(f"     Friend at {fpos}, Peer at {here}", flush=True)
        api("SojournSettings", "set", camera="first_person", field_of_view=40)
        wait(lambda: "idle" in stages, 30)
        there = wait(lambda: next((a for a in api("SojournAvatars", "list")["avatars"] if a["id"] == friend), None), 15)
        if there:
            api("LLAgent", "lookAt", position=[fpos[0], fpos[1], fpos[2] + 0.4])
        time.sleep(3)
        there = next((a for a in api("SojournAvatars", "list")["avatars"] if a["id"] == friend), None)
        check("Friend drawn", there, there)
        idle = shot("idle")

        wait(lambda: "pointat" in stages, 20)
        time.sleep(2.5)
        shot("pointat")
        wait(lambda: "beam" in stages, 20)
        time.sleep(1.5)
        beam = shot("beam")
        if beam is not None and idle is not None:
            check("an edit beam drawn (magenta particles)", magenta(beam) > 200 and magenta(idle) < 50, f"{magenta(idle)} -> {magenta(beam)} px")
        api("SojournSettings", "set", selection_beams="off")
        time.sleep(1)
        off = shot("beam-off")
        if off is not None:
            check("selection_beams off hides it", magenta(off) < 50, f"{magenta(off)} px")
        api("SojournSettings", "set", selection_beams="on")
        wait(lambda: "spiral" in stages, 20)
        time.sleep(1.5)
        spiral = shot("spiral")
        if spiral is not None:
            check("a spiral drawn", magenta(spiral) > 100, f"{magenta(spiral)} px")
        wait(lambda: "clear" in stages, 20)
        time.sleep(2)
        shot("clear")

        wait(lambda: "reset" in stages, 20)
        heard = wait(lambda: f"avatars: {friend} skeleton reset" in console(), 10)
        check("Friend's own ResetSkeleton heard and done here", heard)
        r = api("SojournAvatars", "resetSkeleton")
        check("resetSkeleton (mine) sent", r.get("sent") and r.get("name") == "you", r)
        relayed = wait(lambda: any(l.startswith(f"reset-received {me}") for l in fx_log), 10)
        check("ours relayed by the region to Friend", relayed, [l for l in fx_log if l.startswith("reset-received")])
        r = api("SojournAvatars", "resetSkeleton", id=friend, animations=True)
        check("resetSkeleton (Friend's, with animations), here only", not r.get("sent"), r)
        check("done", wait(lambda: f"avatars: {friend} skeleton and animations reset" in console(), 5))
        r = api("SojournAvatars", "refreshAttachments")
        check("refreshAttachments (mine): worn things attached again", "attached" in r, r)
        r = api("SojournAvatars", "refreshAttachments", id=friend)
        check("refreshAttachments (Friend's): asked of the region", r.get("asked", 0) >= 1, r)
        r = api("SojournAvatars", "resyncAnimations")
        check("resyncAnimations", wait(lambda: "avatars: animations resynced" in console(), 5), r)

        # lip sync: shots for the eye (Friend's mouth opening and closing across lips-*, closed in lips-off-*; the
        # earlier shots show it too); a pixel test would mostly measure the idle animation's sway
        lips = [shot(f"lips-{i}") for i in range(3)]
        api("SojournSettings", "set", lip_sync="off")
        time.sleep(1)
        shut = [shot(f"lips-off-{i}") for i in range(2)]
        api("SojournSettings", "set", lip_sync="on", camera="third_person")
        check("lip sync shots taken (look at them)", all(x is not None for x in lips + shut))
        errors = [l for l in console().splitlines() if "Exception" in l or "VK_ERROR" in l]
        check("no exceptions or Vulkan errors in the console", not errors, errors[:3])
    finally:
        for p in procs:
            try:
                p.terminate()
                p.wait(10)
            except Exception:
                p.kill()
        print("all passed" if ok_all else "some failed", flush=True)


if __name__ == "__main__":
    dll = sys.argv[sys.argv.index("--dll") + 1] if "--dll" in sys.argv else str(ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0")
    main(dll)
    sys.exit(0 if ok_all else 1)
