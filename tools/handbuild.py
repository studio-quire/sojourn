"""Building by hand (docs/building.md > By hand) in Meta's XR Simulator on the local grid: a platform high over
Atelier with a tall box on it, building by hand turned on through SojournHandBuild with the box chosen, then a scripted
right hand grabbing it from afar with the ray (moving up 0.2 m, the stick turning it a quarter), reaching into it to
move it 0.3 m sideways, and the left hand reaching into it to lift it 0.1 m. After each: where the region has it,
what SojournHandBuild says came of it, the undo step; at the end undo puts it back where it began. What it makes it
takes again (into the test account's Objects).

  python tools/handbuild.py [--dll DIR] [--keep]

Runs as Sojourn Shaper (its own account, base dir local/handbuild, agent API :9783). One client on the GPU at a time:
it refuses to start while another SojournVR is running. Shots (eyes and the mirror with the menus) go to
local/handbuild/shots: hb-ray.png while held, hb-after.png after.
"""
import json
import math
import os
import re
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import grid  # noqa: E402
from buildenv import DOTNET  # noqa: E402

BASE = ROOT / "local" / "handbuild"
PORT = 9783
API = f"http://127.0.0.1:{PORT}"
XR_SIM = ROOT / "local" / "xrsim"
X, Y, ZF = 60.0, 60.0, 150.0   # the platform's middle (its top 0.25 m above)
TOP = ZF + 0.25
WAIT = 45                       # the routine waits this long after login for the setup below

# right hand 0.35 m ahead of the head, 0.15 right, 0.3 below, aiming level ahead (east, after face 0)
ROUTINE = "; ".join([
    f"wait {WAIT}", "face 0", "rhand 0.15 -0.3 -0.35 0.5", "raim 0 0", "wait 2", "mark ray grab",
    "rhold 1", "wait 1", "rhand 0.15 -0.1 -0.35 1.5", "wait 0.5", "rstick 1 0 1", "wait 1", "shot hb-ray", "rhold 0", "wait 5",
    # reaching into it: the grip 2 m ahead (inside the box), then 0.3 m to the right
    "mark reach", "raim -60 -40", "rhand 0.0 -0.2 -2.0 0.5", "wait 1", "rhold 1", "wait 0.5", "rhand 0.3 -0.2 -2.0 1.5", "wait 0.5",
    "rhold 0", "wait 5", "rhand 0.15 -0.3 -0.35 0.5",
    # the left hand reaching in, lifting it 0.1 m
    "mark left reach", "lhand 0.3 -0.2 -2.0 0.5", "wait 1", "lhold 1", "wait 0.5", "lhand 0.3 -0.1 -2.0 1", "wait 0.5", "lhold 0",
    "wait 5", "shot hb-after", "mark done", "wait 60",
])


def cmd(pump, op, timeout=30, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(API + "/cmd", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 10) as r:
        out = json.loads(r.read())
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(f"{pump}.{op}: {out['error']}")
    return out


def wait_api(seconds=150):
    t0 = time.time()
    while time.time() - t0 < seconds:
        try:
            if cmd("LLAgent", "getPosition", timeout=5).get("region"):
                return True
        except Exception:
            pass
        time.sleep(2)
    return False


def console_has(pattern):
    try:
        return re.search(pattern, (BASE / "console.log").read_text(encoding="utf-8", errors="replace"), re.M) is not None
    except OSError:
        return False


def wait_mark(name, seconds):
    """Until the routine's 'mark NAME' step has begun (the console says "motion N: mark  NAME")."""
    t0 = time.time()
    while time.time() - t0 < seconds:
        if console_has(r"motion \d+: mark\s+" + re.escape(name) + r"\s*$"):
            return True
        time.sleep(0.5)
    return False


fails = []


def check(what, ok, detail=""):
    print(f"  {'ok  ' if ok else 'FAIL'} {what}{(': ' + str(detail)) if detail else ''}")
    if not ok:
        fails.append(what)


def place(id_):
    o = (cmd("TesselObjects", "get", ids=[id_], fresh=True, timeout=8).get("objects") or [{}])[0]
    return o.get("pos"), o.get("rot")


def yaw_deg(q):
    x, y, z, w = q
    return math.degrees(math.atan2(2 * (w * z + x * y), 1 - 2 * (y * y + z * z))) % 360


def others_running():
    """Another VR client (SojournVR.exe, or SojournVR.dll under dotnet as the tools run it)."""
    out = subprocess.run(["powershell", "-NoProfile", "-Command",
                          "Get-CimInstance Win32_Process | Where-Object { $_.Name -in 'SojournVR.exe', 'dotnet.exe' -and $_.CommandLine -match 'SojournVR' } "
                          "| Measure-Object | ForEach-Object Count"],
                         capture_output=True, text=True).stdout.strip()
    return out not in ("", "0")


def main():
    dll = Path(sys.argv[sys.argv.index("--dll") + 1]) / "SojournVR.dll" if "--dll" in sys.argv else \
        ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "SojournVR.dll"
    keep = "--keep" in sys.argv
    if not grid.ready():
        sys.exit("the local grid isn't up (python tools/grid.py start)")
    if others_running():
        sys.exit("another SojournVR is running: one client on the GPU at a time")
    print(grid.account("Sojourn", "Shaper"))
    manifests = sorted(XR_SIM.glob("*/meta_openxr_simulator.json"))
    if not manifests:
        sys.exit(f"no Meta XR Simulator under {XR_SIM}")
    env = dict(os.environ, XR_RUNTIME_JSON=str(manifests[-1]), SOJOURN_VR_SCRIPT="motion", SOJOURN_MOTION=ROUTINE)
    BASE.mkdir(parents=True, exist_ok=True)
    args = [str(DOTNET), str(dll), "--first", "Sojourn", "--last", "Shaper", "--password", "sojourn-local",
            "--base-dir", str(BASE), "--bridge-port", str(PORT), "--no-assistant", "--no-voice", "--no-media",
            "--start", f"uri:Atelier&{int(X)}&{int(Y)}&{int(TOP + 2)}", "--minutes", "5",
            "--vr", "--xr-wait", "90", "--shadow-cap", "1024", "--xr-scale", "0.6"]
    proc = subprocess.Popen(args, cwd=ROOT, env=env, stdout=open(BASE / "console.log", "w", encoding="utf-8"),
                            stderr=subprocess.STDOUT, creationflags=0x00004000)
    made = []
    try:
        if not wait_api():
            sys.exit("the client didn't log in")
        t_login = time.time()
        print("logged in; setting up")
        # what an earlier run left (stopped before it could take them) goes first
        try:
            cmd("LLAgent", "requestTeleport", regionname="Atelier", x=X, y=Y, z=TOP + 1.2, skip_confirmation=True, timeout=20)
        except RuntimeError:
            pass
        time.sleep(3)
        near = cmd("TesselObjects", "nearby", dist=20, limit=100, timeout=10).get("objects") or []
        old = [o["id"] for o in near if (o.get("name") or "").startswith("handbuild ") and not o.get("root_id")]
        if old:
            cmd("TesselBuild", "take", ids=old, timeout=30)
            print(f"  took {len(old)} left from an earlier run")
            time.sleep(2)
        made.append(cmd("TesselBuild", "create", name="handbuild platform", shape="box", pos=[X, Y, ZF], scale=[10, 10, 0.5],
                        color=[0.6, 0.62, 0.6])["id"])
        try:
            cmd("LLAgent", "requestTeleport", regionname="Atelier", x=X, y=Y, z=TOP + 1.2, skip_confirmation=True, timeout=20)
        except RuntimeError as e:
            print("  (teleport:", e, ")")
        time.sleep(4)
        me = cmd("LLAgent", "getPosition")["region"]
        print(f"  standing at {me[0]:.2f}, {me[1]:.2f}, {me[2]:.2f}")
        box = cmd("TesselBuild", "create", name="handbuild box", shape="box", pos=[me[0] + 2.0, me[1], TOP + 1.0], scale=[0.5, 0.5, 2.0],
                  color=[0.8, 0.5, 0.3])["id"]
        made.append(box)
        time.sleep(2)
        st = cmd("SojournHandBuild", "start", id=box)
        check("building by hand on, the box chosen", st.get("on") and (st.get("chosen") or {}).get("id") == box, st)
        p0, r0 = place(box)
        print(f"  box at {p0}")
        left = WAIT + 5 - (time.time() - t_login)
        print(f"  the routine starts in {left:.0f} s")
        if left < 3:
            print("  (setup ran late: the routine may have started already)")

        def after(mark):
            if not wait_mark(mark, 120):
                check(f"reached '{mark}'", False)
                return None
            return cmd("SojournHandBuild", "state")

        # the ray grab: up 0.2 m, a quarter turn clockwise from above
        st = after("reach")
        last = (st or {}).get("last") or {}
        p1, r1 = place(box)
        print(f"  after the ray grab: {last}")
        check("ray grab: moved up about 0.2 m", p1 and abs(p1[2] - p0[2] - 0.2) < 0.05 and abs(p1[0] - p0[0]) < 0.05, (p0, p1))
        turned = (yaw_deg(r1) - yaw_deg(r0)) % 360
        check("ray grab: turned a quarter clockwise by the stick", abs(turned - 270) < 12, f"{turned:.1f} deg")
        check("ray grab: the region kept it there", last.get("result") == "placed", last.get("result"))
        hist = cmd("SojournBuild", "history", count=5)
        check("an undo step for it", any("by hand" in (u.get("what") or "") for u in hist.get("undo", [])), [u.get("what") for u in hist.get("undo", [])])

        # reaching into it: 0.3 m to the right (south, facing east)
        st = after("left reach")
        p2, _ = place(box)
        print(f"  after the reach: {(st or {}).get('last')}")
        check("reach: moved about 0.3 m south", p2 and abs((p1[1] - p2[1]) - 0.3) < 0.05 and abs(p2[2] - p1[2]) < 0.05, (p1, p2))

        # the left hand: lifted 0.1 m; and pointing to teleport didn't take the pull
        st = after("done")
        p3, _ = place(box)
        print(f"  after the left reach: {(st or {}).get('last')}")
        check("left reach: lifted about 0.1 m", p3 and abs(p3[2] - p2[2] - 0.1) < 0.04, (p2, p3))
        me2 = cmd("LLAgent", "getPosition")["region"]
        check("the left pull didn't teleport", abs(me2[0] - me[0]) < 0.5 and abs(me2[1] - me[1]) < 0.5, me2)
        check("no errors in the console", not console_has("building by hand: couldn't|Unhandled"))

        # undo, three steps: back where it began
        r = cmd("SojournBuild", "undo", steps=3, id=box, timeout=30)
        print(f"  undo: {r}")
        time.sleep(2)
        p4, r4 = place(box)
        check("undo puts it back", p4 and all(abs(a - b) < 0.02 for a, b in zip(p4, p0)) and abs((yaw_deg(r4) - yaw_deg(r0) + 180) % 360 - 180) < 2, (p0, p4))
        st = cmd("SojournHandBuild", "stop")
        check("off again", not st.get("on"))
    finally:
        if made and not keep:
            try:
                cmd("TesselBuild", "take", ids=made, timeout=30)
            except Exception as e:
                print("  (taking what was made:", e, ")")
        time.sleep(2)
        proc.terminate()
        try:
            proc.wait(20)
        except subprocess.TimeoutExpired:
            proc.kill()
    print("\nhand build:", "all passed" if not fails else f"{len(fails)} failed: {', '.join(fails)}")
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
