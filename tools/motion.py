"""The headset's walking, walls, steps, seats and looking around, tested in Meta's XR Simulator on the local grid
(docs/vr.md > The rig > Walls, steps and the region's word). A course of test pieces (a wall, steps of 0.15-0.6 m, a
0.4 m deck, a windowsill to sit on) stands on a floor prim 120 m over a quiet corner of Atelier (40, 200), and scripted
routines (Xr/VrScript.Motion.cs) walk, fly, sit, look around and point there while vr.csv records the view and the
region's avatar; each run's console is local/motion/NAME-console.log, its vr.csv NAME-vr.csv.

  python tools/motion.py course            log in, build the course (idempotent: pieces already there by name stay)
  python tools/motion.py run NAME [...]    run routines (names below, or all), each from its start; summaries after
  python tools/motion.py report CSV        summarise one run's vr.csv

Runs as Sojourn Mover (its own account, base dir local/motion, agent API :9781), so other sessions on the grid and
their settings aren't disturbed. SOJOURN_DLL picks the build to run (default: the usual Release build); lag like the
main grid's (150 ms round trip, updates every 90 ms, 1 cm noise) is on unless --no-lag; MOTION_ARGS adds client options
(--shot-every 1 saves the eyes to local/motion/shots). OpenSim, unlike SL, sends an avatar's position only when its
velocity changes, so the region's word is dead-reckoned (VrPilot.DeadReckonCap). The local grid can't make a prim
the region lets avatars through (the phantom flag and physics shape none didn't take on a root prim), so letting the
walk through such a prim (VrPilot.Collide.cs, Passable) is untested here.
"""
import csv
import json
import math
import os
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import grid  # noqa: E402

DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
DLL = Path(os.environ.get("SOJOURN_DLL", ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "Sojourn.dll"))
BASE = ROOT / "local" / "motion"
PORT = 9781
API = f"http://127.0.0.1:{PORT}"
XR_SIM = ROOT / "local" / "xrsim"

# the course: on the flattest ground course() finds (local/motion/course.json); each piece's name says what it tests
COURSE = BASE / "course.json"
X0, Y0, Z0 = 40.0, 200.0, 10.0
FLOOR_Z = 120.0   # the course's floor's middle (its top 0.25 m above)


def load_course():
    global X0, Y0, Z0
    if COURSE.exists():
        c = json.loads(COURSE.read_text())
        X0, Y0, Z0 = c["x0"], c["y0"], c["z"]




def pieces(z):
    """(name, create args) for every test piece, on ground at height z."""
    p = []
    # a wall 4 m east of the start, 6 m wide and 3 m tall
    p.append(("motion wall", dict(shape="box", pos=[X0 + 4, Y0, z + 1.5], scale=[0.2, 6, 3], color=[0.7, 0.55, 0.45])))
    # steps north of the start: blocks of rising height, each 1 m deep, 2 m apart
    for i, h in enumerate([0.15, 0.3, 0.45, 0.6]):
        p.append((f"motion step {int(h * 100)}", dict(shape="box", pos=[X0 + 3 * i, Y0 + 6, z + h / 2], scale=[1.6, 1.0, h],
                                                      color=[0.8, 0.8, 0.75])))
    # a boat-like deck south of the start: 0.4 m above the ground, 3 m x 6 m, with a 0.25 m gunwale-less edge
    p.append(("motion deck", dict(shape="box", pos=[X0, Y0 - 7, z + 0.2], scale=[3, 6, 0.4], color=[0.55, 0.4, 0.25])))
    # a windowsill to sit on (no sit target: the avatar sits on its top), east of the steps
    p.append(("motion sill", dict(shape="box", pos=[X0 + 12, Y0 + 6, z + 0.45], scale=[0.4, 1.5, 0.9], click_action="sit",
                                  color=[0.9, 0.9, 0.92])))
    return p


def cmd(pump, op, timeout=30, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(API + "/cmd", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 10) as r:
        out = json.loads(r.read())
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(f"{pump}.{op}: {out['error']}")
    return out


def wait_api(seconds=120):
    t0 = time.time()
    while time.time() - t0 < seconds:
        try:
            if cmd("LLAgent", "getPosition", timeout=5).get("region"):
                return True
        except Exception:
            pass
        time.sleep(2)
    return False


def go_to(x, y, z=None):
    """Teleport within Atelier (logins land where the grid likes, not at --start)."""
    try:
        cmd("LLAgent", "requestTeleport", regionname="Atelier", x=x, y=y, z=z if z is not None else Z0 + 1.5, skip_confirmation=True, timeout=20)
    except RuntimeError as e:   # a local teleport can report "failed: Teleport started" and still land
        print("  (teleport:", e, ")")
    time.sleep(5)


def client(minutes, start, env_extra, vr=True, lag=True):
    env = dict(os.environ)
    env.update(env_extra)
    if lag:
        env.update(SOJOURN_SELF_LAG_MS="150", SOJOURN_SELF_UPDATE_MS="90", SOJOURN_SELF_NOISE_MM="10")
    args = [str(DOTNET), str(DLL), "--first", "Sojourn", "--last", "Mover", "--password", "sojourn-local",
            "--base-dir", str(BASE), "--bridge-port", str(PORT), "--no-assistant", "--no-voice", "--no-media",
            "--start", f"uri:Atelier&{int(round(start[0]))}&{int(round(start[1]))}&{int(round(start[2]))}", "--minutes", f"{minutes:.2f}"]
    if vr:
        manifests = sorted(XR_SIM.glob("*/meta_openxr_simulator.json"))
        env["XR_RUNTIME_JSON"] = str(manifests[-1])
        args += ["--vr", "--xr-wait", "90", "--shadow-cap", "1024", "--xr-scale", "0.6"]
    args += os.environ.get("MOTION_ARGS", "").split()   # e.g. --shot-every 2 (shots in local/motion/shots)
    BASE.mkdir(parents=True, exist_ok=True)
    return subprocess.Popen(args, cwd=ROOT, env=env, stdout=open(BASE / "console.log", "w"), stderr=subprocess.STDOUT,
                            creationflags=0x00004000)


def course():
    print(grid.account("Sojourn", "Mover"))
    p = client(8, (X0, Y0, 30), {}, vr=False)
    try:
        if not wait_api():
            sys.exit("the client didn't log in")
        time.sleep(3)
        load_course()
        go_to(X0, Y0, Z0 + 1.5)
        time.sleep(5)
        load_course()
        go_to(X0, Y0, Z0 + 1.5)
        near0 = cmd("TesselObjects", "nearby", dist=40, limit=300, timeout=20).get("objects", [])
        if not any(o.get("name") == "motion floor" for o in near0):
            print("made floor", cmd("TesselBuild", "create", name="motion floor", shape="box", pos=[X0 + 4, Y0 - 2, FLOOR_Z],
                                    scale=[34, 34, 0.5], color=[0.6, 0.62, 0.6]).get("id"))
        go_to(X0, Y0, Z0 + 1.5)
        near = cmd("TesselObjects", "nearby", dist=30, limit=300, timeout=20).get("objects", [])
        have = {o.get("name") for o in near}
        for name, a in pieces(Z0):
            if name in have:
                continue
            r = cmd("TesselBuild", "create", name=name, **a)
            print("made", name, r.get("id"))
    finally:
        p.terminate()


# routines: (start position offset from the course origin, routine text); all start facing east (the avatar's facing
# at login is east-ish; a short recentre is part of each)
ROUTINES = {
    # walk east into the wall and hold the stick against it, then let go
    "wall": ((0, 0), "face 0; wait 2; mark into the wall; stick 0 1 4; wait 4"),
    # the same at a slant: slide along it
    "slant": ((0, -1), "face 0; wait 2; mark slanting; stick -0.5 0.86 4; wait 4"),
    # backward, sideways and diagonally back: the body keeps facing east
    "backward": ((0, 0), "face 0; wait 2; mark back; stick 0 -1 2; wait 2; mark right; stick 1 0 2; wait 2; mark diagonal; stick 0.7 -0.7 2; wait 3"),
    # north over the steps (from south of the first)
    "steps": ((0, 3), "face 90; wait 2; mark steps; stick 0 1 2; wait 4"),
    # the taller steps: 0.3, 0.45 (the model climbs it; does the region?), 0.6 (a wall to both)
    "step30": ((3, 3), "face 90; wait 2; mark step 30; stick 0 1 1.6; wait 4"),
    "step45": ((6, 3), "face 90; wait 2; mark step 45; stick 0 1 1.6; wait 4"),
    "step60": ((9, 3), "face 90; wait 2; mark step 60; stick 0 1 1.6; wait 4"),
    # south onto the deck
    "deck": ((0, -3), "face -90; wait 2; mark deck; stick 0 1 3; wait 4"),
    # fly up, glide east 3 s, let go: the gap put right
    "fly": ((0, -14), "face 0; wait 2; press b 0.2; wait 0.5; rstick 0 1 3; mark glide; stick 0 1 3; wait 8"),
    # room-scale: step 1.4 m into the wall (the head goes through it), then stand: the avatar can't follow
    "roomwall": ((3.2, 0), "face 0; wait 2; mark step into the wall; head 0 0 -1.4 1.5; wait 8"),
    # sit on the sill (pointing at it with the left hand and pulling), stand by the stick
    # (the pointer on with A, its right trigger sits)
    "sit": ((12, 4.2), "face 90; wait 2; press a 0.2; raim 0 -15; wait 0.5; mark sit; rtrig 1 0.3; wait 5; mark stand by stick; stick 0 1 0.8; wait 4"),
    # looking around: Y held, walk east through the wall, fly up, then Y back
    # walking about the arrival lawn among Atelier's buildings (the busiest place on the local grid), for the cost
    "busy": ((88, -82), "face 0; wait 2; stick 0 1 3; rstick 1 0 0.5; stick 0 1 3; rstick 1 0 0.5; stick 0 1 3; wait 2"),
    "spectate": ((0, 0), "face 0; wait 2; press y 0.8; wait 1; mark ghost walk; stick 0 1 1.2; face 180; wait 3; press b 0.2; rstick 0 1 1.5; wait 2; mark back; press y 0.2; wait 4"),
    # pointing to teleport: the left trigger held 1 s on the ground 6 m ahead
    "point": ((0, -3), "face -90; wait 2; aim 0 -25; mark point; ltrig 1 1.0; ltrig 0 0.2; wait 5"),
}


def run(names, lag=True):
    if not DLL.exists():
        sys.exit(f"no build at {DLL}")
    results = {}
    for name in names:
        (dx, dy), routine = ROUTINES[name]
        csv_path = BASE / f"{name}-vr.csv"
        if csv_path.exists():
            csv_path.unlink()
        settings = BASE / "vr-settings.json"
        if settings.exists():
            settings.unlink()
        routine = "wait 10; " + routine   # (the teleport to the start lands meanwhile)
        env = {"SOJOURN_VR_SCRIPT": "motion", "SOJOURN_MOTION": routine, "SOJOURN_VR_LOG": str(csv_path)}
        secs = sum(float(s.split()[-1]) for s in routine.split(";") if s.split()[0] in ("wait", "stick", "rstick", "press", "ltrig", "rtrig", "head"))
        p = client((secs + 75) / 60, (X0 + dx, Y0 + dy, 30), env, lag=lag)
        if wait_api(90):
            go_to(X0 + dx, Y0 + dy)
        t0 = time.time()
        log = BASE / "console.log"
        done = False
        while time.time() - t0 < secs + 150:
            time.sleep(2)
            text = log.read_text(errors="replace") if log.exists() else ""
            if "motion done" in text:
                time.sleep(2)
                done = True
                break
            if p.poll() is not None:
                break
        p.terminate()
        try:
            p.wait(20)
        except subprocess.TimeoutExpired:
            p.kill()
        (BASE / f"{name}-console.log").write_text(log.read_text(errors="replace") if log.exists() else "")
        print(f"--- {name}: {'done' if done else 'NOT DONE'}")
        results[name] = report(csv_path, name)
    return results


def report(path, name=""):
    """What the view and the avatar did: the head's path, the region's avatar, the gap, cuts, the body's facing."""
    rows = [r for r in csv.reader(open(path)) if len(r) >= 28] if Path(path).exists() else []
    if not rows:
        print("  no vr.csv rows")
        return None
    f = lambda r, i: float(r[i])
    # from the arrival at the routine's start (the teleport there), 1 s settled
    jumps = [i for i in range(1, len(rows)) if math.dist([f(rows[i], 14), f(rows[i], 15), f(rows[i], 16)],
                                                         [f(rows[i - 1], 14), f(rows[i - 1], 15), f(rows[i - 1], 16)]) > 5]
    if jumps:
        t0 = f(rows[jumps[0]], 0) + 1
        rows = [r for r in rows if f(r, 0) >= t0]
    walk = [r for r in rows if f(r, 23) > 0.25]
    out = {}
    hx = [f(r, 4) for r in rows]
    hy = [f(r, 5) for r in rows]
    print(f"  {len(rows)} frames; head x {min(hx):.2f}..{max(hx):.2f}, y {min(hy):.2f}..{max(hy):.2f}; "
          f"avatar x {min(f(r, 14) for r in rows):.2f}..{max(f(r, 14) for r in rows):.2f}")
    gaps = [math.hypot(f(r, 4) - f(r, 14), f(r, 5) - f(r, 15)) for r in rows]
    print(f"  gap head-avatar: max {max(gaps):.2f} m, at the end {gaps[-1]:.2f} m")
    if walk:
        body_off = [abs((f(r, 13) - f(r, 12) * 0 - math.degrees(0)) % 360) for r in walk]
        print(f"  walking frames {len(walk)}; body yaw {min(f(r, 13) for r in walk):.0f}..{max(f(r, 13) for r in walk):.0f} deg")
    marks = [r[26] for r in rows if r[26] != "-"]
    print(f"  cuts/slides: {sum(1 for i in range(1, len(rows)) if rows[i][26] in ('cut', 'blink', 'slide') and rows[i - 1][26] == '-')}"
          f" ({', '.join(sorted(set(marks)))})")
    heads_z = [f(r, 6) for r in rows]
    print(f"  head z {min(heads_z):.2f}..{max(heads_z):.2f}, avatar z {min(f(r, 16) for r in rows):.2f}..{max(f(r, 16) for r in rows):.2f}")
    out.update(max_gap=max(gaps), end_gap=gaps[-1])
    return out


if __name__ == "__main__":
    load_course()
    what = sys.argv[1] if len(sys.argv) > 1 else "run"
    if what == "course":
        course()
    elif what == "run":
        names = [a for a in sys.argv[2:] if not a.startswith("--")] or list(ROUTINES)
        if names == ["all"]:
            names = list(ROUTINES)
        run(names, lag="--no-lag" not in sys.argv)
    elif what == "report":
        report(sys.argv[2])
