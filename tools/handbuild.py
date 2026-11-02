"""Building by hand (docs/building.md > By hand) in Meta's XR Simulator on the local grid: a platform high over
Atelier with a tall box, a small cube and a seat on it (the seat another account's, by a console session as Sojourn
Echo, so it isn't the tester's to move). Building by hand on through SojournHandBuild with nothing selected and the
pointer on, then a scripted right hand: a tap on the box selects it; its x arrow, z arrow, x ring, z ring, xy square
and middle orb each dragged; a tap on the cube selects that instead; a tap on the sky lets it go (the platform would
be selected: it's the tester's own); a tap on the seat sits on it (a tap on what isn't yours is the world's, as ever). After each drag: where the region has the box and
how it's turned, against what that drag means; at the end undo puts it back where it began. What it makes it takes
again.

  python tools/handbuild.py [--dll DIR] [--keep]

Runs as Sojourn Shaper (its own account, base dir local/handbuild, agent API :9783), and Sojourn Echo through the
session probe (no GPU, :9764). One client on the GPU at a time: it refuses to start while another SojournVR is running.
Shots (eyes and the mirror with the menus) go to local/handbuild/shots: hb-arrow.png (the pointer on the x arrow),
hb-ring.png (dragging the x ring), hb-after.png (seated, nothing selected).
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
PORT, ECHO_PORT = 9783, 9764
API = f"http://127.0.0.1:{PORT}"
XR_SIM = ROOT / "local" / "xrsim"
PROBE = ROOT / "src" / "Sojourn.SessionProbe" / "bin" / "Release" / "net10.0" / "Sojourn.SessionProbe.dll"
X, Y, ZF = 60.0, 60.0, 150.0   # the platform's middle (its top 0.25 m above)
TOP = ZF + 0.25
WAIT = 75                       # the routine waits this long after login for the setup below
BOX = (61.6, 61.4, TOP + 1.0)   # 0.5 x 0.5 x 2, north-east, so no axis points straight at the eye
CUBE = (60.3, 61.8, TOP + 0.15)
SEAT = (60.5, 58.4, TOP + 0.25)
SKY = (63.0, 57.0, TOP + 8)   # nothing there: a tap on the sky

DRAGS = [   # mark, the handle (kind, axis: 0 the middle, 1 an arrow, 2 a plane square, 3 a ring), where the aim slides
    ("arrow x", (1, 0), (-0.3, 0, 0)),
    ("arrow z", (1, 2), (0, 0, 0.25)),
    ("ring x", (3, 0), (0, 0, 0.15)),
    ("ring z", (3, 2), (0, 0.15, 0)),
    ("plane xy", (2, 2), (0.2, -0.2, 0)),
    ("orb", (0, 0), (0, 0.3, 0)),
]


def routine():
    steps = [f"wait {WAIT}", "face 41", "rhand 0.15 -0.3 -0.35 0.5", "wait 1",
             "mark tap box", f"rat {BOX[0]} {BOX[1]} {BOX[2] + 0.3}", "wait 1", "rtrig 1 0.15", "wait 2"]
    for mark, (k, a), (dx, dy, dz) in DRAGS:
        steps += [f"mark {mark}", f"rhandle {k} {a}", "wait 1.2"]
        if mark == "arrow x":
            steps.append("shot hb-arrow")
        steps += ["rhold 1", "wait 0.3", f"rslide {dx} {dy} {dz} 1"]
        if mark == "ring x":
            steps.append("shot hb-ring")   # (while still held)
        steps += ["wait 0.3", "rhold 0", "wait 4"]
    steps += ["mark tap cube", f"rat {CUBE[0]} {CUBE[1]} {CUBE[2]}", "wait 1", "rtrig 1 0.15", "wait 2",
              "mark tap sky", f"rat {SKY[0]} {SKY[1]} {SKY[2]}", "wait 1", "rtrig 1 0.15", "wait 2",
              "mark tap seat", f"rat {SEAT[0]} {SEAT[1]} {SEAT[2] + 0.2}", "wait 1", "rtrig 1 0.15", "wait 4", "shot hb-after",
              "mark done", "wait 60"]
    return "; ".join(steps)


def cmd(pump, op, timeout=30, port=PORT, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}/cmd", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 10) as r:
        out = json.loads(r.read())
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(f"{pump}.{op}: {out['error']}")
    return out


def wait_api(port=PORT, seconds=150):
    t0 = time.time()
    while time.time() - t0 < seconds:
        try:
            if cmd("LLAgent", "getPosition", timeout=5, port=port).get("region"):
                return True
        except Exception:
            pass
        time.sleep(2)
    return False


def console():
    try:
        return (BASE / "console.log").read_text(encoding="utf-8", errors="replace")
    except OSError:
        return ""


def wait_mark(name, seconds):
    """Until the routine's 'mark NAME' step has begun (the console says "motion N: mark  NAME")."""
    t0 = time.time()
    while time.time() - t0 < seconds:
        if re.search(r"motion \d+: mark\s+" + re.escape(name) + r"\s*$", console(), re.M):
            return True
        time.sleep(0.5)
    return False


def handle_points():
    """Where each rhandle step found its handle, in order."""
    out = []
    for m in re.finditer(r"motion: the handle is at (nowhere|<([^>]*)>)", console()):
        out.append(tuple(float(x) for x in re.findall(r"-?\d+(?:\.\d+)?(?:E-?\d+)?", m.group(2))) if m.group(2) else None)
    return out


fails = []


def check(what, ok, detail=""):
    print(f"  {'ok  ' if ok else 'FAIL'} {what}{(': ' + str(detail)) if detail else ''}")
    if not ok:
        fails.append(what)


def place(id_):
    o = (cmd("TesselObjects", "get", ids=[id_], fresh=True, timeout=8).get("objects") or [{}])[0]
    return o.get("pos"), o.get("rot")


# quaternions as [x, y, z, w]
def qmul(a, b):
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return [aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz]


def qinv(q):
    return [-q[0], -q[1], -q[2], q[3]]


def qaxis(axis, angle):
    s = math.sin(angle / 2)
    return [axis[0] * s, axis[1] * s, axis[2] * s, math.cos(angle / 2)]


def qangle(a, b):
    na, nb = math.sqrt(sum(x * x for x in a)), math.sqrt(sum(x * x for x in b))
    d = abs(sum(x * y for x, y in zip(a, b))) / (na * nb)
    return math.degrees(2 * math.acos(min(1.0, d)))


def sub(a, b):
    return [x - y for x, y in zip(a, b)]


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
    if not PROBE.exists():
        sys.exit(f"build the session probe first ({PROBE.name}: python tools/probe.py builds it)")
    print(grid.account("Sojourn", "Shaper"))
    grid.account("Sojourn", "Echo", "sojourn-local")
    manifests = sorted(XR_SIM.glob("*/meta_openxr_simulator.json"))
    if not manifests:
        sys.exit(f"no Meta XR Simulator under {XR_SIM}")
    env = dict(os.environ, XR_RUNTIME_JSON=str(manifests[-1]), SOJOURN_VR_SCRIPT="motion", SOJOURN_MOTION=routine())
    BASE.mkdir(parents=True, exist_ok=True)
    (BASE / "client").mkdir(exist_ok=True)
    args = [str(DOTNET), str(dll), "--first", "Sojourn", "--last", "Shaper", "--password", "sojourn-local",
            "--base-dir", str(BASE), "--bridge-port", str(PORT), "--no-assistant", "--no-voice", "--no-media",
            "--start", f"uri:Atelier&{int(X)}&{int(Y)}&{int(TOP + 2)}", "--minutes", "6",
            "--vr", "--xr-wait", "90", "--shadow-cap", "1024", "--xr-scale", "0.6"]
    proc = subprocess.Popen(args, cwd=ROOT, env=env, stdout=open(BASE / "console.log", "w", encoding="utf-8"),
                            stderr=subprocess.STDOUT, creationflags=0x00004000)
    echo = None
    made, seat, seat_sits = [], None, False
    try:
        if not wait_api():
            sys.exit("the client didn't log in")
        t_login = time.time()
        print("logged in; setting up")
        try:
            cmd("LLAgent", "requestTeleport", regionname="Atelier", x=X, y=Y, z=TOP + 1.2, skip_confirmation=True, timeout=20)
        except RuntimeError:
            pass
        time.sleep(3)
        near = cmd("TesselObjects", "nearby", dist=20, limit=100, timeout=10).get("objects") or []
        old = [o["id"] for o in near if (o.get("name") or "").startswith("handbuild ") and not o.get("root_id")
               and o.get("name") != "handbuild seat"]
        if old:
            cmd("TesselBuild", "take", ids=old, timeout=30)
            print(f"  took {len(old)} left from an earlier run")
            time.sleep(2)
        made.append(cmd("TesselBuild", "create", name="handbuild platform", shape="box", pos=[X, Y, ZF], scale=[10, 10, 0.5],
                        color=[0.6, 0.62, 0.6])["id"])
        # Sojourn Echo, a console session on the platform, makes the seat (so it's someone else's)
        echo = subprocess.Popen([str(DOTNET), str(PROBE), "--first", "Sojourn", "--last", "Echo", "--port", str(ECHO_PORT),
                                 "--start", f"uri:Atelier&{int(X - 3)}&{int(Y - 3)}&{int(TOP + 1)}", "--base", str(BASE / "echo"),
                                 "--seconds", "420", "--expect", "(never said)"], cwd=ROOT, stdout=open(BASE / "echo.log", "w", encoding="utf-8"),
                                stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, creationflags=0x00004000)
        try:
            cmd("LLAgent", "requestTeleport", regionname="Atelier", x=X, y=Y, z=TOP + 1.2, skip_confirmation=True, timeout=20)
        except RuntimeError as e:
            print("  (teleport:", e, ")")
        time.sleep(4)
        me = cmd("LLAgent", "getPosition")["region"]
        print(f"  standing at {me[0]:.2f}, {me[1]:.2f}, {me[2]:.2f}")
        box = cmd("TesselBuild", "create", name="handbuild box", shape="box", pos=list(BOX), scale=[0.5, 0.5, 2.0], color=[0.8, 0.5, 0.3])["id"]
        made.append(box)
        cube = cmd("TesselBuild", "create", name="handbuild cube", shape="box", pos=list(CUBE), scale=[0.3, 0.3, 0.3], color=[0.3, 0.5, 0.8])["id"]
        made.append(cube)
        if wait_api(ECHO_PORT, 60):
            try:
                cmd("LLAgent", "requestTeleport", port=ECHO_PORT, regionname="Atelier", x=X - 3, y=Y - 3, z=TOP + 1.2, skip_confirmation=True, timeout=20)
            except RuntimeError:
                pass
            time.sleep(2)
            try:
                for o in cmd("TesselObjects", "nearby", port=ECHO_PORT, dist=20, limit=100, timeout=10).get("objects") or []:
                    if o.get("name") == "handbuild seat" and not o.get("root_id"):
                        cmd("TesselBuild", "take", port=ECHO_PORT, ids=[o["id"]], timeout=30)
                seat = cmd("TesselBuild", "create", port=ECHO_PORT, name="handbuild seat", shape="box", pos=list(SEAT),
                           scale=[0.6, 0.6, 0.5], click_action="sit")["id"]
            except RuntimeError as e:
                print("  (Echo couldn't make the seat:", e, ")")
        else:
            print("  (Sojourn Echo didn't log in: the seat check is skipped)")
        time.sleep(2)
        if seat:
            # (its click action comes after it's made: until the tester's client has it, a tap on it touches)
            for i in range(10):
                so = (cmd("TesselObjects", "get", ids=[seat], fresh=True, timeout=8).get("objects") or [{}])[0]
                if so.get("click_action") == "sit":
                    break
                if i % 3 == 2:
                    # (on the local grid a click action often never reaches another client, and a colour change after it
                    # clears it even for its maker, 2026-11-02: so no colour, and the sit check only when it arrives)
                    cmd("TesselBuild", "modify", port=ECHO_PORT, id=seat, click_action="sit", timeout=20)
                time.sleep(1.5)
            eo = (cmd("TesselObjects", "get", port=ECHO_PORT, ids=[seat], fresh=True, timeout=8).get("objects") or [{}])[0]
            ep = cmd("LLAgent", "getPosition", port=ECHO_PORT).get("region")
            print(f"  the seat: {so.get('name')} at {so.get('pos')}, click action {so.get('click_action')} (Echo, at {ep}, sees {eo.get('click_action')})")
            if so.get("click_action") != "sit":
                # (then a tap on it touches rather than sits, and there's nothing to see)
                print("  (the seat's click action never arrived: the sit check is skipped)")
                seat_sits = False
            else:
                seat_sits = True
        cmd("SojournSettings", "set", pointer="on")
        st = cmd("SojournHandBuild", "start")
        check("building by hand on, nothing selected", st.get("on") and not st.get("selected"), st)
        p0, r0 = place(box)
        print(f"  box at {p0}")
        left = WAIT + 6 - (time.time() - t_login)
        print(f"  the routine starts in {left:.0f} s")
        if left < 3:
            print("  (setup ran late: the routine may have started already)")

        def state_at(mark):
            if not wait_mark(mark, 150):
                check(f"reached '{mark}'", False)
                return None
            return cmd("SojournHandBuild", "state")

        marks = [m for m, _, _ in DRAGS] + ["tap cube"]
        st = state_at(marks[0])
        check("a tap on the box selects it", ((st or {}).get("selected") or {}).get("id") == box, (st or {}).get("selected"))
        pos, rot = place(box)
        for i, (mark, (k, a), slide) in enumerate(DRAGS):
            st = state_at(marks[i + 1])
            p1, r1 = place(box)
            last = (st or {}).get("last") or {}
            moved, turned = sub(p1, pos), qangle(r1, rot)
            print(f"  {mark}: moved {[round(x, 3) for x in moved]}, turned {turned:.1f} deg ({last.get('result')})")
            pts = handle_points()
            if mark.startswith("arrow"):
                axis = [1 if j == a else 0 for j in range(3)]
                along = sum(m * x for m, x in zip(moved, axis))
                want = sum(s * x for s, x in zip(slide, axis))
                off = math.sqrt(max(0.0, sum(m * m for m in moved) - along * along))
                check(f"{mark}: {want:+.2f} m along it and nowhere else", abs(along - want) < 0.03 and off < 0.005 and turned < 0.5, (round(along, 3), round(off, 4)))
            elif mark == "plane xy":
                check("plane xy: across x and y as the aim went, not up", abs(moved[0] - slide[0]) < 0.03 and abs(moved[1] - slide[1]) < 0.03
                      and abs(moved[2]) < 0.005 and turned < 0.5, moved)
            elif mark == "orb":
                # the middle rides the ray at the distance it was grabbed at, from the hand
                m = re.findall(r"dragging its free \(.*?\), the ray from (-?[\d.]+), (-?[\d.]+), (-?[\d.]+) ([\d.]+) m", console())
                p = pts[i] if i < len(pts) else None
                if m and p:
                    hand, t = [float(x) for x in m[-1][:3]], float(m[-1][3])
                    aim = sub([p[0] + slide[0], p[1] + slide[1], p[2] + slide[2]], hand)
                    n = math.sqrt(sum(x * x for x in aim))
                    want = sub([hand[j] + aim[j] / n * t for j in range(3)], p)
                    check("orb: moved freely, the middle where the ray now is at the distance grabbed", math.dist(moved, want) < 0.03 and turned < 0.5,
                          f"{[round(x, 3) for x in moved]} for {[round(x, 3) for x in want]}")
                else:
                    check("orb: its drag found in the console", False)
            elif mark.startswith("ring"):
                axis = [1.0 if j == a else 0.0 for j in range(3)]
                q = qmul(r1, qinv(rot))
                ang = 2 * math.acos(max(-1.0, min(1.0, abs(q[3]))))
                n = math.sqrt(q[0] ** 2 + q[1] ** 2 + q[2] ** 2) or 1
                about = abs(sum(c / n * x for c, x in zip(q[:3], axis)))
                check(f"{mark}: turned about {mark[-1]} only", ang > math.radians(4) and about > 0.99, f"{math.degrees(ang):.1f} deg, axis {[round(c / n, 3) for c in q[:3]]}")
                p = pts[i] if i < len(pts) else None
                if mark == "ring x" and p:
                    # the ring faces the eye: the turn is the angle swept round the middle by the point aimed at
                    c = [pos[0], pos[1], pos[2]]
                    v0, v1 = sub(p, c), sub([p[0] + slide[0], p[1] + slide[1], p[2] + slide[2]], c)
                    want = math.atan2(v0[1] * v1[2] - v0[2] * v1[1], v0[1] * v1[1] + v0[2] * v1[2])
                    expect = qmul(qaxis(axis, want), rot)
                    check("ring x: by the angle the aim swept round it", qangle(r1, expect) < 2, f"{qangle(r1, expect):.1f} deg off ({math.degrees(want):.1f} wanted)")
                if mark == "ring x" and p is None:
                    check("ring x: its handle found", False)
            check(f"{mark}: the region kept it there", last.get("result") == "placed", last.get("result"))
            pos, rot = p1, r1
        hist = cmd("SojournBuild", "history", count=10)
        hand_steps = [u for u in hist.get("undo", []) if "by hand" in (u.get("what") or "")]
        check("six undo steps for the six drags", len(hand_steps) >= 6, [u.get("what") for u in hist.get("undo", [])][:8])

        st = state_at("tap sky")
        check("a tap on the cube selects it instead", ((st or {}).get("selected") or {}).get("id") == cube, (st or {}).get("selected"))
        st = state_at("tap seat")
        check("a tap on the sky lets it go", not (st or {}).get("selected"), (st or {}).get("selected"))
        st = state_at("done")
        if seat:
            check("a tap on someone else's thing doesn't select it", not (st or {}).get("selected"), (st or {}).get("selected"))
        if seat and seat_sits:
            time.sleep(1)
            sitting = cmd("LLAgent", "animations").get("sitting")
            check("a tap on someone else's seat sits on it (the world's tap, as ever)", sitting, sitting)
        check("no errors in the console", not re.search(r"building by hand: couldn't|Unhandled", console()))

        r = cmd("SojournBuild", "undo", steps=6, id=box, timeout=30)
        print(f"  undo: {r}")
        time.sleep(2)
        p4, r4 = place(box)
        check("undo puts it back", p4 and all(abs(a - b) < 0.02 for a, b in zip(p4, p0)) and qangle(r4, r0) < 2, (p0, p4, f"{qangle(r4, r0):.1f} deg"))
        # the assistant's straighten: tilt it, then level it, then reset it
        cmd("TesselBuild", "modify", id=box, rot_euler=[20, 10, 35], timeout=20)
        time.sleep(1.5)
        before, _ = place(box)
        s = cmd("SojournHandBuild", "straighten", id=box, level=True)
        time.sleep(1.5)
        p5, r5 = place(box)
        check("straighten level: no tilt, heading kept", abs(r5[0]) < 0.01 and abs(r5[1]) < 0.01 and abs(math.degrees(2 * math.atan2(r5[2], r5[3])) % 360 - 35) < 3, (r5, s))
        cmd("SojournHandBuild", "straighten", id=box)
        time.sleep(1.5)
        p6, r6 = place(box)
        check("straighten: no rotation, in the same place", qangle(r6, [0, 0, 0, 1]) < 1 and math.dist(p6, p5) < 0.03, (p6, r6))
        hist = cmd("SojournBuild", "history", count=3)
        check("straightening is an undo step", any("straightened" in (u.get("what") or "") for u in hist.get("undo", [])), [u.get("what") for u in hist.get("undo", [])])
        st = cmd("SojournHandBuild", "stop")
        check("off again", not st.get("on"))
    finally:
        if made and not keep:
            try:
                cmd("TesselBuild", "take", ids=made, timeout=30)
            except Exception as e:
                print("  (taking what was made:", e, ")")
        if seat and not keep:
            try:
                cmd("TesselBuild", "take", port=ECHO_PORT, ids=[seat], timeout=30)
            except Exception as e:
                print("  (taking the seat:", e, ")")
        time.sleep(2)
        for p in (proc, echo):
            if p is None:
                continue
            p.terminate()
            try:
                p.wait(20)
            except subprocess.TimeoutExpired:
                p.kill()
    print("\nhand build:", "all passed" if not fails else f"{len(fails)} failed: {', '.join(fails)}")
    sys.exit(1 if fails else 0)


if __name__ == "__main__":
    main()
