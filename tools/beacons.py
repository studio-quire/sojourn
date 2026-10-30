"""Beacons, highlights, Highlight Transparent and the tracking beacon (batch B8) on the local grid, never the main grid.

  python tools/beacons.py scene             a platform 60 m over Atelier 96,200: a scripted box that takes touches, one
                                            that doesn't, a physical ball, a particle source, a looping sound (at a
                                            whisper), a face with media, a blended box, a fully transparent one, an
                                            invisiprim and an alpha-masked card
  python tools/beacons.py run [--dll DIR] [--stereo]
                                            Sojourn Beacon (1280x720, sound at 0) on :9876 on the platform: shots with
                                            everything off, beacons of every kind, the default highlights, Highlight
                                            Transparent, and a tracked place 40 m off, each checked by its colours

Prints each check and the shots' paths to look at (local/beacons/). Written by Claude Opus 5.5, 2026-10-29.
"""
import json
import os
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
BASE = ROOT / "local" / "beacons"
PORT = 9876
W, H = 1280, 720
X0, Y0, Z0 = 96.0, 200.0, 60.0
ALERT = "ed124764-705d-d497-167a-182cd9fa2e6c"
INVISIPRIM = "38b86f85-2575-52a9-a531-23108d8da837"
ok_all = True


def objects():
    import render_scenes as rs
    o = rs.obj
    touch = 'llSetTimerEvent(0); } touch_start(integer n) { llSay(0, "touched");'
    return [
        o("Beacon Test Floor", (X0, Y0, Z0 - 0.1), (16, 16, 0.2), color=(0.45, 0.45, 0.5, 1), solid=True),
        o("Beacon Test Touch", (X0 + 6, Y0 - 4, Z0 + 0.5), (1, 1, 1), color=(0.8, 0.8, 0.8, 1), solid=True, script=touch),
        o("Beacon Test Script", (X0 + 6, Y0 - 1.5, Z0 + 0.5), (1, 1, 1), color=(0.8, 0.8, 0.8, 1), solid=True, script="llSetTimerEvent(0);"),
        o("Beacon Test Physical", (X0 + 6, Y0 + 1, Z0 + 0.6), (0.8, 0.8, 0.8), color=(0.8, 0.8, 0.8, 1), shape="sphere", solid=True,
          script="llSetStatus(STATUS_PHYSICS, TRUE);"),
        o("Beacon Test Particles", (X0 + 6, Y0 + 3.5, Z0 + 0.5), (0.3, 0.3, 0.3), tex=rs.BLANK, color=(0.9, 0.9, 0.9, 1), shape="sphere",
          script="llParticleSystem([PSYS_SRC_BURST_RATE, 0.2, PSYS_SRC_BURST_PART_COUNT, 1, PSYS_PART_MAX_AGE, 1.0, "
                 "PSYS_SRC_PATTERN, PSYS_SRC_PATTERN_EXPLODE, PSYS_SRC_BURST_SPEED_MIN, 0.1, PSYS_SRC_BURST_SPEED_MAX, 0.2, "
                 "PSYS_PART_START_SCALE, <0.1, 0.1, 0>, PSYS_PART_START_COLOR, <1, 1, 1>]);"),
        o("Beacon Test Sound", (X0 + 9, Y0 - 3, Z0 + 0.5), (0.6, 0.6, 0.6), color=(0.8, 0.8, 0.8, 1), solid=True,
          script=f'llLoopSound("{ALERT}", 0.01);'),
        o("Beacon Test Media", (X0 + 9, Y0, Z0 + 1), (0.1, 2, 1.5), color=(0.8, 0.8, 0.8, 1), solid=True,
          script='llSetPrimMediaParams(4, [PRIM_MEDIA_HOME_URL, "about:blank", PRIM_MEDIA_CURRENT_URL, "about:blank"]);'),
        o("Beacon Test Blend", (X0 + 4, Y0 - 3, Z0 + 0.5), (1, 1, 1), tex=rs.BLANK, color=(0.9, 0.9, 1, 0.5), solid=True),
        o("Beacon Test Clear", (X0 + 4, Y0 - 1, Z0 + 0.5), (1, 1, 1), tex=rs.BLANK, color=(1, 1, 1, 0), solid=True),
        o("Beacon Test Invisiprim", (X0 + 4, Y0 + 1, Z0 + 0.5), (1, 1, 1), tex=INVISIPRIM, solid=True),
        o("Beacon Test Mask", (X0 + 4, Y0 + 3, Z0 + 0.8), (0.05, 1.5, 1.5), tex=rs.FLOWER_TEX, solid=True,
          script="llSetPrimitiveParams([PRIM_ALPHA_MODE, ALL_SIDES, PRIM_ALPHA_MODE_MASK, 128]);"),
    ]


def scene():
    import render_scenes as rs
    # (the mask card's flower texture rides in the OAR as the flowers scene's does)
    build = rs.build_oar

    def with_flowers(name, objs):
        out = build(name, objs)
        import tarfile
        import forest
        tmp = out.with_suffix(".tmp")
        with tarfile.open(out, "r:gz") as src, tarfile.open(tmp, "w:gz") as dst:
            for m in src.getmembers():
                dst.addfile(m, src.extractfile(m))
            forest.add(dst, f"assets/{rs.FLOWER_TEX}_texture.jp2", rs.flower_texture())
        tmp.replace(out)
        return out
    rs.build_oar = with_flowers
    return rs.load("beacons", objects())


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


def dom(img, c):
    """Pixels where channel c (0 red, 1 green, 2 blue) stands 40 over the other two."""
    o = [i for i in range(3) if i != c]
    return int(((img[..., c] - np.maximum(img[..., o[0]], img[..., o[1]])) > 40).sum())


def run(dll, stereo):
    import grid
    if "agni" in grid.URL or not grid.ready():
        sys.exit("the local grid isn't up (python tools/grid.py start)")
    grid.account("Sojourn", "Beacon", "sojourn-local")
    (BASE / "a").mkdir(parents=True, exist_ok=True)
    log = open(BASE / "a-console.txt", "w", encoding="utf-8")
    args = [str(DOTNET), str(Path(dll) / "SojournVR.dll"), "--first", "Sojourn", "--last", "Beacon", "--password", "sojourn-local",
            "--bridge-port", str(PORT), "--window", f"{W}x{H}", "--fps", "20", "--no-assistant", "--no-voice",
            "--base-dir", str(BASE / "a"), "--start", f"uri:Atelier&{int(X0 - 2)}&{int(Y0)}&{int(Z0 + 1)}", "--minutes", "6"]
    if stereo:
        args.append("--stereo")
    pa = subprocess.Popen(args, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, creationflags=0x00004000)

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

    def setting(**kv):
        api("SojournSettings", "set", **kv)
        time.sleep(1.5)

    try:
        up = wait(lambda: json.loads(urllib.request.urlopen(f"http://127.0.0.1:{PORT}/status", timeout=3).read()).get("connected"), 120)
        check("logged in", up)
        if not up:
            return
        setting(volume=0, camera="first_person", field_of_view=60, beacons="off", highlight_transparent="off", names="off")
        api("LLAgent", "lookAt", position=[X0 + 6, Y0, Z0 + 0.5])
        time.sleep(12)   # (the platform's textures and scripts settle)
        off = shot("off")
        base = [dom(off, c) for c in range(3)] if off is not None else [0, 0, 0]

        setting(beacons="on", beacon_lines="on", beacons_scripted="on", beacons_physical="on", beacons_sounds="on", beacons_particles="on",
                beacons_media="on")
        lines = shot("lines")
        if lines is not None:
            # the beacons' own colours, linear as the viewer draws them: red (scripts), green (physical), white (media),
            # light blue (particles), yellow (sounds), at half over what's behind
            # (the lines are faint, as the viewer's: an eighth of their colour through walls, half where seen)
            got = [dom(lines, c) for c in range(3)]
            check("beacons drawn: red highlights, the physical ball's green line", got[0] > base[0] + 10000 and got[1] > base[1] + 100, dict(off=base, on=got))
        st = api("SojournBeacons", "state")
        check("state lists the kinds (scripted turned touch off)", "scripted" in st["kinds"] and "touch" not in st["kinds"], st)

        setting(beacon_lines="off", beacons_touch="on", beacons_sounds="off", beacons_particles="off", beacons_media="off")
        st = api("SojournBeacons", "state")
        check("beacon_lines off keeps highlights on; touch turned scripted off", st["highlights"] and not st["lines"] and "scripted" not in st["kinds"], st)
        hl = shot("highlights")
        if hl is not None and off is not None:
            got = [dom(hl, c) for c in range(3)]
            check("highlights tint the touch box and the physical ball", got[0] > base[0] + 3000, dict(off=base, on=got))

        setting(beacons="off", highlight_transparent="on")
        tr = shot("transparent")
        if tr is not None and off is not None:
            got = [dom(tr, c) for c in range(3)]
            check("Highlight Transparent: blended red, masked blue, invisiprim green",
                  got[0] > base[0] + 8000 and got[1] > base[1] + 3000 and got[2] > base[2] + 4000, dict(off=base, on=got))
        setting(highlight_transparent="off")

        here = api("LLAgent", "getPosition")["region"]
        # north-east, over the open lawn and the sea (east is behind the tree)
        api("LLAgent", "lookAt", position=[here[0] + 24, here[1] + 32, here[2] + 3])
        time.sleep(3)
        bare = shot("untracked")
        t = api("SojournBeacons", "track", at=[here[0] + 24, here[1] + 32, here[2]], name="Over there")
        check("track a place", t.get("tracking", {}).get("name") == "Over there", t)
        time.sleep(2)
        tk = shot("tracking")
        st = api("SojournBeacons", "state")
        check("tracking: where and how far", abs((st.get("tracking") or {}).get("distance", 0) - 40) < 3, st.get("tracking"))
        if tk is not None:
            got, was = [dom(tk, c) for c in range(3)], [dom(bare, c) for c in range(3)] if bare is not None else [0, 0, 0]
            # (Atelier's trees hide much of it; the blue below is mostly against the sky, so only the red is counted)
            check("the column drawn", got[0] > was[0] + 1000, dict(before=was, after=got))
        # walking there ends it (within 3 m)
        api("SojournBeacons", "track", at=[here[0] + 1, here[1], here[2]], name="Here")
        time.sleep(1.5)
        check("arriving ends it", api("SojournBeacons", "state").get("tracking") is None)
        api("SojournBeacons", "untrack")
    finally:
        try:
            api("LLAppViewer", "requestQuit", timeout=5)
        except Exception:
            pass
        try:
            pa.wait(20)
        except subprocess.TimeoutExpired:
            pa.kill()
    print("all ok" if ok_all else "SOME FAILED", flush=True)


if __name__ == "__main__":
    what = sys.argv[1] if len(sys.argv) > 1 else ""
    if what == "scene":
        sys.exit(scene())
    if what == "run":
        dll = sys.argv[sys.argv.index("--dll") + 1] if "--dll" in sys.argv else str(ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0")
        run(dll, "--stereo" in sys.argv)
        sys.exit(0 if ok_all else 1)
    sys.exit(__doc__)
