"""The two maps and the location line on the local grid (docs/map.md).

  python tools/map.py [--dll DIR] [--headset]

Two desktop clients in Atelier: Sojourn People (:9881, SOJOURN_TEST_INPUT=1 so posted clicks count) and Sojourn Peer
(:9882, standing near, a dot on the map). Checks SojournMap where and stats, the line at the top left (a click opens
the mini-map under it), the mini-map's globe (the world map beside it), the world map's keys (+ zooms in), a spot
picked by a click, Go taking People there (Atelier East) with the pick kept, show at a named region with the place
picked, the world map put away and opened again as it was, show without a place (the mini-map), hide, friends; F12
shots of the window into local/map/a/shots (the line and maps over the view). --headset instead:
SOJOURN_VR_SCRIPT=map in Meta's XR Simulator (the wrist raised, the line pressed, the mini-map's globe, +, the map
dragged, a spot picked, the board dragged, x; the mini-map popped out and put back), with shots
(SOJOURN_PANEL_IN_EYES: the layers drawn into the eyes too). Takes the GPU for its run: take local/gpu.lock first.
"""
import json
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import people as P  # noqa: E402

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / "local" / "map"
P.BASE = BASE
A, B = 9881, 9882
W, H = 1280, 720


def call(port, op, **args):
    return P.tryapi(port, "SojournMap", op, **args)


def main(dll):
    import grid
    if "agni" in grid.URL or not grid.ready():
        sys.exit("the local grid isn't up (python tools/grid.py start)")
    for last in ("People", "Peer"):
        grid.account("Sojourn", last, "sojourn-local")
    env = dict(P.os.environ, SOJOURN_TEST_INPUT="1")
    pa = P.client("Sojourn", "People", A, "a", f"{W}x{H}", 20, dll, "uri:Atelier&124&112&22", env)
    pb = P.client("Sojourn", "Peer", B, "b", "320x180", 5, dll, "uri:Atelier&130&116&22")
    procs = [pa, pb]

    def click(x, y):
        P.subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winmouse.ps1"), "-X", str(int(x)), "-Y", str(int(y)),
                          "-Click", "-ProcessId", str(pa.pid)], capture_output=True)

    def shot(name):
        shots = BASE / "a" / "shots"
        before = set(shots.glob("*.png")) if shots.exists() else set()
        P.subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winkey.ps1"), "-ProcessId", str(pa.pid), "-Vk", "123"], capture_output=True)
        new = P.wait(lambda: set(shots.glob("*.png")) - before, 10)
        if new:
            time.sleep(0.4)
            path = sorted(new)[-1]
            dest = path.with_name(f"map-{name}.png")
            path.replace(dest)
            print(f"  shot: {dest}")

    try:
        for port in (A, B):
            up = P.wait(lambda: json.loads(P.urllib.request.urlopen(f"http://127.0.0.1:{port}/status", timeout=3).read()).get("connected"), 120)
            P.check(f"logged in on :{port}", up)
            if not up:
                return
        time.sleep(10)
        # a screen reader running on the PC (another session's tests) would put the client in reader mode
        for port in (A, B):
            P.tryapi(port, "SojournSettings", "set", screen_reader="off")

        r = call(A, "where")
        P.check("where: Atelier, a rating, a parcel", r.get("region") == "Atelier" and r.get("rating") in ("general", "moderate", "adult")
                and isinstance(r.get("parcel"), str), r)
        r = call(A, "stats")
        P.check("stats: the three parts and a verdict", all(k in r for k in ("client", "network", "region", "verdict"))
                and r["region"].get("time_dilation", 0) > 0, r.get("verdict"))

        call(A, "hide")   # (where the mini-map was is kept between runs)
        time.sleep(0.5)
        s = call(A, "state")
        line = (s.get("desktop") or {}).get("line")
        P.check("the line at the top left", line and line[0] < 40 and line[1] < 40, s.get("desktop"))
        if not line:
            return
        click((line[0] + line[2]) / 2, (line[1] + line[3]) / 2)
        time.sleep(1.5)
        s = call(A, "state")
        mini = s.get("mini") or {}
        P.check("a click on the line: the mini-map, following", mini.get("shown") is True and mini.get("following") is True
                and s["world"].get("shown") is False, s)

        def globe():
            d = call(A, "state").get("desktop") or {}
            m, mk = d.get("mini"), d.get("mini_scale") or 1
            if m:
                click(m[0] + (272 - 10 - 16) * mk, m[1] + 24 * mk)   # the mini-map's globe (top right on the desktop)
            return bool(m)

        P.check("the mini-map drawn under the line", globe(), call(A, "state").get("desktop"))
        time.sleep(1.5)
        s = call(A, "state")
        P.check("its globe: the world map up, following", s["world"].get("shown") is True and s["world"].get("following") is True, s)
        time.sleep(4)   # tiles and names
        shot("board")
        d = s.get("desktop") or {}
        board, k = d.get("world"), d.get("world_scale") or 1
        if not board:
            return
        across = s["world"]["across"]

        def at(lx, ly):
            return board[0] + lx * k, board[1] + ly * k

        click(*at(12 + 38 + 16, 24))   # +
        time.sleep(1)
        s = call(A, "state")
        P.check("+ zooms in (half as far across)", abs(s["world"]["across"] - across / 2) <= 2, (across, s["world"]["across"]))
        # pick a spot in Atelier East: the map's middle is People (Atelier 124,112); a region east is 256 m
        mpp = s["world"]["across"] / (624 - 24)
        mid_x, mid_y = 12 + (624 - 24) / 2, 48 + 440 / 2
        click(*at(mid_x + (256 + 10 - 124) / mpp, mid_y))
        time.sleep(1)
        s = call(A, "state")
        picked = s["world"].get("picked") or {}
        P.check("a click on the map picks the spot (Atelier East)", picked.get("region") == "Atelier East", s)
        shot("picked")
        click(*at(624 - 12 - 38, 488 + 28))   # Go
        moved = P.wait(lambda: call(A, "where").get("region") == "Atelier East", 30)
        P.check("Go takes People there", moved, call(A, "where"))
        s = call(A, "state")
        P.check("and the world map goes, the pick kept; the mini-map stays", s["world"].get("shown") is False
                and (s["world"].get("picked") or {}).get("region") == "Atelier East" and s["mini"].get("shown") is True, s)

        r = call(A, "show", region="Atelier Northeast", at=[100, 100], across=600, pick=True)
        s = call(A, "state")
        P.check("show at a region: the world map, the place picked", r.get("map") == "world"
                and (s["world"].get("picked") or {}).get("region") == "Atelier Northeast" and s["world"].get("following") is False, s)
        time.sleep(3)
        shot("shown")
        r = call(A, "hide", map="world")
        P.check("hide the world map", call(A, "state")["world"].get("shown") is False, r)
        globe()
        time.sleep(1.5)
        s = call(A, "state")
        P.check("opened again from the globe, as it was (the place picked, not following)", s["world"].get("shown") is True
                and (s["world"].get("picked") or {}).get("region") == "Atelier Northeast" and s["world"].get("following") is False, s)
        r = call(A, "show")
        P.check("show without a place: the mini-map", r.get("map") == "mini" and call(A, "state")["mini"].get("shown") is True, r)
        r = call(A, "hide")
        s = call(A, "state")
        P.check("hide: both away", s["world"].get("shown") is False and s["mini"].get("shown") is False, r)
        r = call(A, "friends")
        P.check("friends answers", isinstance(r.get("friends"), list), r)
    finally:
        for p in procs:
            p.terminate()
        for p in procs:
            try:
                p.wait(20)
            except Exception:
                p.kill()
        print("\nall passed" if P.ok_all else "\nsome failed")


def headset(dll):
    import grid
    import media_fixtures as mf
    if "agni" in grid.URL or not grid.ready():
        sys.exit("the local grid isn't up (python tools/grid.py start)")
    P.os.environ["SOJOURN_CLIENT_DLL"] = str(Path(dll) / "SojournVR.dll")
    base = BASE / "headset"
    proc = mf.start_client(base, A, vr=True, minutes=3, env_extra={"SOJOURN_VR_SCRIPT": "map", "SOJOURN_MAP_SHOTS": "1", "SOJOURN_PANEL_IN_EYES": "1", "SOJOURN_MAP_DEBUG": "1"})
    log = base / "console.txt"
    picked = None
    try:
        P.check("logged in (the simulator)", mf.logged_in(A, 150))
        P.tryapi(A, "SojournSettings", "set", screen_reader="off")   # (a screen reader on the PC would turn reader mode on)
        text = lambda: log.read_text(encoding="utf-8", errors="replace")
        up = P.wait(lambda: "map: mini-map on the hand" in text(), 60)
        P.check("the line pressed: the mini-map on the hand", up)
        up = P.wait(lambda: "map: world map up" in text(), 20)
        P.check("its globe pressed: the world map up", up)
        picked = P.wait(lambda: (call(A, "state")["world"].get("picked") or None), 20)
        P.check("a spot picked on the world map", picked, picked)
        s = call(A, "state")["world"]
        P.check("+ pressed and the map dragged (no longer following, 600 m across or closer)", s.get("following") is False and s.get("across", 999) <= 600, s)
        done = P.wait(lambda: "map script" in text() and "done" in text().split("map script")[-1], 40)
        s = call(A, "state")
        P.check("x pressed: the world map away, its pick kept", done and "map: world map put away" in text() and s["world"].get("shown") is False
                and s["world"].get("picked"), s)
        P.check("the mini-map popped out and put back on the hand", "map: mini-map out on its own" in text()
                and text().count("map: mini-map on the hand") >= 2 and s["mini"].get("where") == "hand", s["mini"])
        print("  shots:", sorted(str(p) for p in (base / "shots").glob("map-*.png")))
    finally:
        proc.terminate()
        try:
            proc.wait(20)
        except Exception:
            proc.kill()
        print("\nall passed" if P.ok_all else "\nsome failed")


if __name__ == "__main__":
    dll = sys.argv[sys.argv.index("--dll") + 1] if "--dll" in sys.argv else str(ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0")
    headset(dll) if "--headset" in sys.argv else main(dll)
