"""People and me (batch B2, SojournPeople) on the local grid, never the main grid: friends' removal and rights, contact
sets, Do Not Disturb and away with their autoresponses, typing, hover height, teleport history, favourites, pay price
buttons, llMapDestination and SLurls offered on the feed, experiences' refusal where the region has none.

  python tools/people.py [--dll DIR]     two clients: Sojourn People (1280x720, SOJOURN_TEST_INPUT=1 so posted clicks
                                         answer confirmations) on :9861 and Sojourn Peer (small) on :9862, own base dirs
                                         under local/people/; both made friends first

Confirmations are pressed as tools/report.py does: an F12 shot, the confirm key found by the default look's accent
(cancel is any press beside it). Written by Claude Opus 5.5, 2026-10-27.
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
BASE = ROOT / "local" / "people"
A, B = 9861, 9862
W, H = 1280, 720
ok_all = True


def api(port, pump, op, timeout=20, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}/cmd", data=body, method="POST", headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 150) as r:
        out = json.loads(r.read() or b"null")
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(out["error"])
    return out


def tryapi(port, pump, op, **args):
    try:
        return api(port, pump, op, **args)
    except Exception as e:
        return {"exception": str(e)}


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
    print(f"{'ok  ' if cond else 'FAIL'} {name}: {str(detail)[:260]}", flush=True)


def client(first, last, port, base, size, fps, dll, start, env=None):
    (BASE / base).mkdir(parents=True, exist_ok=True)
    log = open(BASE / f"{base}-console.txt", "w", encoding="utf-8")
    return subprocess.Popen([str(DOTNET), str(Path(dll) / "SojournVR.dll"), "--first", first, "--last", last, "--password", "sojourn-local",
                             "--bridge-port", str(port), "--window", size, "--fps", str(fps), "--no-assistant", "--no-voice", "--no-sounds",
                             "--base-dir", str(BASE / base), "--start", start, "--minutes", "20"],
                            cwd=ROOT, env=env or dict(os.environ), stdout=log, stderr=subprocess.STDOUT, creationflags=0x00004000)


def main(dll):
    sys.path.insert(0, str(ROOT / "tools"))
    import grid
    if "agni" in grid.URL or not grid.ready():
        sys.exit("the local grid isn't up (python tools/grid.py start)")
    for last in ("People", "Peer"):
        grid.account("Sojourn", last, "sojourn-local")
    import socket
    for port in (A, B):
        with socket.socket() as so:
            if so.connect_ex(("127.0.0.1", port)) == 0:
                sys.exit(f"something already listens on {port}")
    env = dict(os.environ, SOJOURN_TEST_INPUT="1")
    pa = client("Sojourn", "People", A, "a", f"{W}x{H}", 20, dll, "uri:Atelier&124&112&22", env)
    pb = client("Sojourn", "Peer", B, "b", "320x180", 5, dll, "uri:Atelier&127&113&22")
    procs = [pa, pb]

    def key(vk):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winkey.ps1"), "-ProcessId", str(pa.pid), "-Vk", str(vk)], capture_output=True)

    def click(x, y):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winmouse.ps1"), "-X", str(int(x)), "-Y", str(int(y)),
                        "-Click", "-ProcessId", str(pa.pid)], capture_output=True)

    def move(x, y):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winmouse.ps1"), "-X", str(int(x)), "-Y", str(int(y)),
                        "-ProcessId", str(pa.pid)], capture_output=True)

    def shot():
        shots = BASE / "a" / "shots"
        before = set(shots.glob("*.png")) if shots.exists() else set()
        key(123)
        new = wait(lambda: set(shots.glob("*.png")) - before, 10)
        if not new:
            return None, None
        time.sleep(0.4)
        path = sorted(new)[-1]
        return path, np.asarray(Image.open(path).convert("RGB")).astype(int)

    def confirm_key():
        move(300, 500)   # off the panel: a key under the pointer is lit, in another colour
        time.sleep(0.3)
        path, img = shot()
        if img is None:
            return path, None
        mask = np.abs(img - np.array((0x23, 0x7c, 0x76))).max(axis=2) < 14
        mask[:, :W // 2] = False
        ys, xs = np.nonzero(mask)
        if len(xs) <= 50:
            return path, None
        # the key's own band: the scene can hold the same colour (a robe, a box) elsewhere on the right
        cy = np.median(ys)
        band = np.abs(ys - cy) < 12
        xs, ys = xs[band], ys[band]
        cx = np.median(xs)
        near = np.abs(xs - cx) < 120
        xs, ys = xs[near], ys[near]
        return path, (xs.mean(), ys.mean(), np.percentile(xs, 98) - np.percentile(xs, 2))

    def gated(pump, op, yes, **args):
        """Runs a gated op on A in the background and answers its confirmation: (result, shot path)."""
        out = {}
        th = threading.Thread(target=lambda: out.__setitem__("r", tryapi(A, pump, op, **args)))
        th.start()
        time.sleep(2.6)
        path, k = confirm_key()
        if k is None:
            th.join(1)
            return out.get("r", {"exception": "no confirmation seen"}), path
        x, y, w = k
        if not yes:
            x -= w + 20
        # the pointer onto the key first, then the press (as a hand would); again if it didn't take
        for _ in range(3):
            key(27)   # Esc: out of mouse steering, where a click looks rather than presses (as tools/report.py does)
            time.sleep(0.4)
            move(x, y)
            time.sleep(0.9)
            click(x, y)
            th.join(5)
            if not th.is_alive():
                break
        th.join(30)
        return out.get("r"), path

    try:
        for port, who in ((A, "People"), (B, "Peer")):
            up = wait(lambda: json.loads(urllib.request.urlopen(f"http://127.0.0.1:{port}/status", timeout=3).read()).get("connected"), 120)
            mine = f"agent API on http://127.0.0.1:{port}/" in (BASE / f"{'a' if port == A else 'b'}-console.txt").read_text(encoding="utf-8", errors="replace")
            check(f"{who} logged in, on its own port", up and mine)
            if not (up and mine):
                return
            tryapi(port, "SojournSettings", "set", screen_reader="off")   # a screen reader on the PC would turn reader mode on
        time.sleep(6)
        api(A, "SojournSettings", "set", away_after=0)   # nobody moves its view here: it would go away by itself mid-test
        a_id = api(A, "LLAgent", "getID")["id"]
        b_id = api(B, "LLAgent", "getID")["id"]

        # --- friends: make them friends first (an offer B accepts) ---
        # OpenSim keeps an unanswered offer as a half friendship and sends it again at each login (people2.py's turned-away
        # offer leaves one from B), and refuses an offer the other way with "already your friend": then the waiting
        # offer is the one to accept, or B offers.
        def offer_on(port, frm):
            return next((n for n in api(port, "LLNotifications", "listChannelNotifications", channel="Visible")["notifications"]
                         if n["name"].startswith("OfferFriendship") and frm in n["message"]), None)

        def refused():
            return any("already your friend" in n["message"] for n in api(A, "LLNotifications", "listChannelNotifications", channel="Visible")["notifications"])

        if not any(f["id"] == b_id for f in api(A, "TesselAgent", "friends")["friends"]):
            to, n = A, offer_on(A, b_id)
            if not n:
                api(A, "TesselAgent", "offerFriendship", to=b_id, message="people test")
                to, n = B, wait(lambda: offer_on(B, a_id) or (refused() and "refused"), 20)
                if n in (None, "refused"):
                    api(B, "TesselAgent", "offerFriendship", to=a_id, message="people test")
                    to, n = A, wait(lambda: offer_on(A, b_id), 20)
            check("friendship offered", n, n and f"{n['name']} on {'People' if to == A else 'Peer'}")
            if n:
                api(to, "LLNotifications", "respond", uuid=n["id"], response={"Accept": True})
        fr = wait(lambda: next((f for f in api(A, "TesselAgent", "friends")["friends"] if f["id"] == b_id), None), 20)
        check("friends", fr, fr)
        if not fr:
            return

        r = api(A, "SojournPeople", "friendRights", to=b_id)
        if r.get("rights_given") != {"online": True, "map": False, "modify": False}:   # left over from an earlier run
            gated("SojournPeople", "friendRights", True, to=b_id, online=True, map=False, modify=False)
            r = api(A, "SojournPeople", "friendRights", to=b_id)
        check("rights read without a confirmation", r.get("rights_given", {}).get("online") is not None, r)
        base_rights = r["rights_given"]

        # map on: cancelled, then confirmed
        want_map = not base_rights["map"]
        r, path = gated("SojournPeople", "friendRights", False, to=b_id, map=want_map, online=True)
        check("rights change cancelled", "didn't confirm" in json.dumps(r), r)
        r2 = api(A, "SojournPeople", "friendRights", to=b_id)
        check("cancel changed nothing", r2["rights_given"]["map"] == base_rights["map"], r2)
        r, path = gated("SojournPeople", "friendRights", True, to=b_id, map=want_map, online=True)
        check("rights change confirmed", r and r.get("rights_given", {}).get("map") == want_map, r)
        seen = wait(lambda: next((f for f in api(B, "TesselAgent", "friends")["friends"] if f["id"] == a_id and bool(f["rights_from"] & 2) == want_map), None), 12)
        check("the friend's side shows it", seen, seen)
        # edit rights: the confirmation's warning
        r, path = gated("SojournPeople", "friendRights", False, to=b_id, modify=True)
        check("edit rights asked (cancelled; the shot shows the warning)", "didn't confirm" in json.dumps(r), r)
        print("   edit-rights confirmation shot:", path)
        # online off takes the map with it
        r, _ = gated("SojournPeople", "friendRights", True, to=b_id, online=False)
        check("online off takes map off", isinstance(r, dict) and r.get("rights_given") == {"online": False, "map": False, "modify": False}, r)
        r, _ = gated("SojournPeople", "friendRights", True, to=b_id, online=True)
        check("online back", isinstance(r, dict) and r.get("rights_given", {}).get("online"), r)
        r = tryapi(A, "SojournPeople", "friendRights", to=b_id, map=True, online=False)
        check("map without online refused or dropped", "exception" in r or not r.get("rights_given", {}).get("map"), r)

        # --- contact sets ---
        api(A, "SojournPeople", "contactSet", name="Club", color="#ff8800")
        api(A, "SojournPeople", "contactSetAdd", name="club", to=b_id)
        api(A, "SojournPeople", "pseudonym", to=b_id, name="Pip")
        cs = api(A, "SojournPeople", "contactSets")
        club = next((s for s in cs["sets"] if s["name"] == "Club"), None)
        check("contact set with a member and its colour", club and club["members"] and club["members"][0]["id"] == b_id and club["color"] == [1.0, 0.533, 0.0], club)
        check("pseudonym", any(p["pseudonym"] == "Pip" for p in cs["pseudonyms"]), cs["pseudonyms"])
        r = api(A, "SojournPeople", "friendRights", to="Pip")
        check("a friend by pseudonym", r.get("id") == b_id, r.get("name"))
        r, _ = gated("SojournPeople", "contactSet", False, name="Club", remove=True)
        check("deleting a set asks (cancelled)", "didn't confirm" in json.dumps(r) and any(s["name"] == "Club" for s in api(A, "SojournPeople", "contactSets")["sets"]), r)
        api(A, "SojournPeople", "contactSetRemove", name="Club", to=b_id)
        r, _ = gated("SojournPeople", "contactSet", True, name="Club", remove=True)
        check("set deleted", r and r.get("removed") == "Club", r)
        api(A, "SojournPeople", "pseudonym", to=b_id, name="")

        # --- Do Not Disturb ---
        sid = None

        def b_lines():
            nonlocal sid
            ss = api(B, "TesselIM", "sessions")["sessions"]
            s = next((s for s in ss if s["other_id"] == a_id and s["type"] == "p2p"), None)
            if not s:
                return []
            sid = s["session_id"]
            # the whole conversation: it's kept across runs (people2.py's too), so "since before" needs every line
            return [m["message"] for m in api(B, "TesselIM", "history", session_id=sid, count=5000)["messages"]]

        def a_autoresponses():
            return [json.loads(l)["data"] for l in (BASE / "a" / "state").rglob("events.jsonl").__next__().read_text(encoding="utf-8").splitlines()
                    if '"autoresponse"' in l]

        api(A, "SojournPeople", "setResponse", kind="dnd", text="")   # the viewer's sentence (an earlier run may have set words)
        st = api(A, "SojournPeople", "setStatus", do_not_disturb=True)
        check("do not disturb on", st["do_not_disturb"], st)
        before = len(b_lines())
        api(B, "TesselIM", "send", to=a_id, message=f"are you there {int(time.time())}")
        got = wait(lambda: [m for m in b_lines()[before:] if "Do Not Disturb" in m], 12)
        check("DND autoresponse (the viewer's sentence)", got, got)
        api(B, "TesselIM", "send", to=a_id, message="and again")
        time.sleep(4)
        check("once a conversation", sum("Do Not Disturb" in m for m in b_lines()[before:]) == 1, b_lines()[before:])
        r, path = gated("SojournPeople", "setResponse", True, kind="dnd", text="In VR, back in a bit")
        check("their own words, confirmed", r and r.get("dnd_response") == "In VR, back in a bit", r)
        print("   response confirmation shot:", path)
        api(A, "SojournPeople", "setStatus", do_not_disturb=False)
        api(A, "SojournPeople", "setStatus", do_not_disturb=True)
        before = len(b_lines())
        api(B, "TesselIM", "send", to=a_id, message="third")
        got = wait(lambda: [m for m in b_lines()[before:] if m == "In VR, back in a bit"], 12)
        check("their words sent", got, got)
        busy = wait(lambda: "efcf670c" in json.dumps(tryapi(B, "SojournAvatars", "inspect", id=a_id)).lower(), 8)
        check("others see the busy animation", busy, "")
        n = len(a_autoresponses())
        check("autoresponses logged without words", n >= 2 and all("message" not in e for e in a_autoresponses()), n)
        api(A, "SojournPeople", "setStatus", do_not_disturb=False)
        held = wait(lambda: next((n for n in api(A, "LLNotifications", "listChannelNotifications", channel="Visible").get("notifications", [])
                                  if n["name"] == "DoNotDisturbHeld"), None), 3) or \
            any("DoNotDisturbHeld" in l for l in next((BASE / "a" / "state").rglob("events.jsonl")).read_text(encoding="utf-8").splitlines()[-200:])
        check("what it held is said once it's over", held, held if isinstance(held, bool) else held.get("message"))

        # --- away ---
        api(A, "SojournPeople", "setStatus", away=True)
        insp = tryapi(B, "SojournAvatars", "inspect", id=a_id)
        time.sleep(2)
        insp = tryapi(B, "SojournAvatars", "inspect", id=a_id)
        check("others see away", "fd037134" in json.dumps(insp).lower(), "")
        r, _ = gated("SojournPeople", "setResponse", True, kind="away", on=True, text="Stepped out of the headset")
        check("away reply on", r and r.get("away_response_on"), r)
        before = len(b_lines())
        api(B, "TesselIM", "send", to=a_id, message="hello away")
        got = wait(lambda: [m for m in b_lines()[before:] if m == "Stepped out of the headset"], 12)
        check("away reply sent", got, got)
        api(A, "SojournPeople", "setStatus", away=False)
        r, _ = gated("SojournPeople", "setResponse", True, kind="away", on=False)
        api(A, "SojournSettings", "set", away_after=5)

        # --- typing: B sees A's typing animation while A types nearby ---
        api(A, "SojournPeople", "typing", on=True)
        time.sleep(2)
        insp = tryapi(B, "SojournAvatars", "inspect", id=a_id)
        check("typing shows (the typing animation)", "c541c47f" in json.dumps(insp).lower(), "")
        api(A, "SojournPeople", "typing", on=False)

        # --- hover height ---
        r = api(A, "SojournPeople", "hoverHeight", height=0.3)
        check("hover height sent", r.get("sent") and r.get("hover_height") == 0.3, r)
        s = api(A, "SojournSettings", "get", name="hover_height")["settings"]["hover_height"]["value"]
        check("hover height as a setting", s == 0.3, s)
        api(A, "SojournSettings", "set", hover_height=0)

        # --- pay price buttons and llMapDestination: a scripted box of A's own ---
        script = ("default {\n  state_entry() { llSetPayPrice(PAY_HIDE, [50, 100, PAY_HIDE, PAY_HIDE]); }\n"
                  "  money(key id, integer amount) { llOwnerSay(\"paid \" + (string)amount); }\n"
                  "  touch_start(integer n) {\n    llMapDestination(\"Atelier North\", <100.0, 100.0, 30.0>, ZERO_VECTOR);\n"
                  "    llOwnerSay(\"also try secondlife:///app/teleport/Atelier%20East/60/70/30\");\n  }\n}\n")
        sc = api(A, "TesselInventory", "createScript", name="people test box", text=script)
        pos = api(A, "LLAgent", "getPosition")["region"]
        box = api(A, "TesselBuild", "create", shape="box", pos=[pos[0] + 1.5, pos[1], pos[2]], scale=[0.5, 0.5, 0.5], name="People Test Box")["id"]
        time.sleep(1)
        r, _ = gated("TesselBuild", "addToContents", True, id=box, item_id=sc["item_id"])
        check("script in the box", r and r.get("added"), r)
        time.sleep(3)
        pp = wait(lambda: (lambda x: x if x.get("buttons") else None)(api(A, "SojournPeople", "payPrices", id=box)), 15, 1)
        check("pay prices", pp and pp["buttons"] == [50, 100] and pp["any_amount"] is False, pp)
        r = tryapi(A, "TesselAgent", "pay", timeout=10, to=box, amount=30)
        check("an amount it wouldn't take refused before asking", "only L$50, L$100" in json.dumps(r), r)
        r, path = gated("TesselAgent", "pay", False, to=box, amount=50)
        # (the local grid gives no L$: there a button's amount gets as far as the balance)
        check("a button's amount asked (cancelled), or only short of L$", "didn't confirm" in json.dumps(r) or "can't afford L$50" in json.dumps(r), r)
        print("   pay confirmation shot:", path)

        api(A, "LLAgent", "requestTouch", obj_uuid=box)
        offer = wait(lambda: next((n for n in api(A, "LLNotifications", "listChannelNotifications", channel="Visible")["notifications"]
                                   if n["name"] == "PlaceOffered" and "Atelier North" in n["message"]), None), 12)
        check("llMapDestination on the feed", offer, offer and offer["message"])
        keys = wait(lambda: [k for k in api(A, "SojournFeed", "keys")["keys"] if k.get("button") == "Teleport"], 8)
        check("its Teleport key on the feed", keys, keys)
        if keys:
            key(27)
            time.sleep(0.4)
            # the pointer on the sheet holds it still (lines arriving after, like the box's own, move keys up until then)
            move(keys[0]["x"], keys[0]["y"])
            time.sleep(1.0)
            k = next(k for k in api(A, "SojournFeed", "keys")["keys"] if k.get("button") == "Teleport")
            move(k["x"], k["y"])
            time.sleep(0.9)
            click(k["x"], k["y"])
            there = wait(lambda: api(A, "TesselAgent", "parcel")["region"]["name"] == "Atelier North", 25, 1)
            check("the person's press teleports", there, api(A, "TesselAgent", "parcel")["region"]["name"])
        # a SLurl offered by the assistant (since batch B4 the person's own way is a press on the link itself, on the
        # feed or in the reader: tools/social.py)
        api(A, "SojournPeople", "offerPlace", slurl="http://maps.secondlife.com/secondlife/Atelier%20Northeast/40/50/30")
        offer = wait(lambda: next((n for n in api(A, "LLNotifications", "listChannelNotifications", channel="Visible")["notifications"]
                                   if n["name"] == "PlaceOffered" and "Atelier Northeast" in n["message"]), None), 8)
        check("a place the assistant offers", offer, offer and offer["message"])
        if offer:
            api(A, "LLNotifications", "respond", uuid=offer["id"], response={"Teleport": True})
            there = wait(lambda: api(A, "TesselAgent", "parcel")["region"]["name"] == "Atelier Northeast", 25, 1)
            check("the assistant's answer teleports too", there, api(A, "TesselAgent", "parcel")["region"]["name"])

        # --- teleport history ---
        time.sleep(4)
        th = api(A, "SojournPeople", "teleportHistory")
        regions = [p["region"] for p in th["places"]]
        check("teleport history, newest first", regions[:3] == ["Atelier Northeast", "Atelier North", "Atelier"], regions[:5])
        check("with go_to and parcels", th["places"][0].get("go_to", "").startswith("Atelier Northeast "), th["places"][0])
        api(A, "LLAgent", "requestTeleport", regionname="Atelier", x=124, y=112, z=22, skip_confirmation=True, timeout=20)

        # --- favourites ---
        time.sleep(3)
        fav = api(A, "SojournPeople", "addFavorite", name="people test favourite")
        favs = api(A, "SojournPeople", "favorites")["favorites"]
        check("here as a favourite", any(f["name"] == "people test favourite" for f in favs), [f["name"] for f in favs])
        r, _ = gated("SojournPeople", "removeFavorite", True, item_id=fav["item_id"])
        check("favourite removed (confirmed)", r and r.get("removed") == "people test favourite", r)

        # --- experiences: OpenSim has none ---
        r = tryapi(A, "SojournPeople", "experiences")
        check("experiences refused where the region has none", "no experiences" in json.dumps(r), r)

        # --- slurl parsing ---
        r = api(A, "SojournPeople", "parseSlurls", text="a secondlife://Bay%20City/10/20/30 b http://slurl.com/secondlife/Foo+Bar/1/2 secondlife:///app/region/Baz")
        check("SLurl forms", [p["go_to"] for p in r["places"]] == ["Bay City 10,20,30", "Foo Bar 1,2,0", "Baz 128,128,0"], r)

        # --- cleanup: the box, the script; and the friendship ends (confirmed) ---
        r, _ = gated("SojournPeople", "removeFriend", True, to=b_id)
        check("friend removed (confirmed)", r and r.get("removed"), r)
        gone = wait(lambda: not any(f["id"] == a_id for f in api(B, "TesselAgent", "friends")["friends"]), 12)
        check("gone from their list too", gone, "")
        r, _ = gated("TesselBuild", "delete", True, ids=[box])
    finally:
        for p in procs:
            p.terminate()
        for p in procs:
            try:
                p.wait(15)
            except subprocess.TimeoutExpired:
                p.kill()
    print("ALL OK" if ok_all else "SOME FAILED")


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--dll") + 1] if "--dll" in sys.argv else str(ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0"))
