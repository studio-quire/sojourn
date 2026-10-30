"""The social layer (batch B4, SojournSocial) on the local grid, never the main grid: keyword alerts, mentions, a SLurl
on the feed pressed (its place offered), translation's plumbing (the test translator), the reader's links, radar
reports; and shots for the eye: tags with a group title, a contact set's ring, chat bubbles, the away and Do Not
Disturb marks, a tag hidden behind a wall and shown through it, a hover tip.

  python tools/social.py [--dll DIR]     two clients: Sojourn Peer (1280x720, the observer, SOJOURN_TEST_INPUT=1 and
                                         SOJOURN_TEST_TRANSLATOR=1) on :9871 and Sojourn People (small, the one who
                                         talks and walks) on :9872, own base dirs under local/social/

Uses tools/people.py's accounts (friends there; People owns the "Sojourn People Test" group when people2.py has run).
Prints each check, and the shots' paths to look at. Written by Claude Opus 5.5, 2026-10-28.
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
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
BASE = ROOT / "local" / "social"
A, B = 9871, 9872
W, H = 1280, 720
ok_all = True


def api(port, pump, op, timeout=20, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}/cmd", data=body, method="POST", headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 60) as r:
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
    print(f"{'ok  ' if cond else 'FAIL'} {name}: {str(detail)[:300]}", flush=True)


def client(first, last, port, base, size, fps, dll, start, env=None):
    (BASE / base).mkdir(parents=True, exist_ok=True)
    log = open(BASE / f"{base}-console.txt", "w", encoding="utf-8")
    return subprocess.Popen([str(DOTNET), str(Path(dll) / "SojournVR.dll"), "--first", first, "--last", last, "--password", "sojourn-local",
                             "--bridge-port", str(port), "--window", size, "--fps", str(fps), "--no-assistant", "--no-voice", "--no-sounds",
                             "--base-dir", str(BASE / base), "--start", start, "--minutes", "15"],
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
    env = dict(os.environ, SOJOURN_TEST_INPUT="1", SOJOURN_TEST_TRANSLATOR="1")
    pa = client("Sojourn", "Peer", A, "a", f"{W}x{H}", 20, dll, "uri:Atelier&124&112&22", env)
    pb = client("Sojourn", "People", B, "b", "320x180", 5, dll, "uri:Atelier&127&113&22")
    procs = [pa, pb]

    def ps(script, *args):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / script), *args, "-ProcessId", str(pa.pid)], capture_output=True)

    def key(vk):
        ps("winkey.ps1", "-Vk", str(vk))

    def move(x, y):
        ps("winmouse.ps1", "-X", str(int(x)), "-Y", str(int(y)))

    def click(x, y):
        ps("winmouse.ps1", "-X", str(int(x)), "-Y", str(int(y)), "-Click")

    def shot(name):
        shots = BASE / "a" / "shots"
        before = set(shots.glob("*.png")) if shots.exists() else set()
        key(123)
        new = wait(lambda: set(shots.glob("*.png")) - before, 10)
        if not new:
            return None, None
        time.sleep(0.4)
        path = sorted(new)[-1]
        dest = BASE / f"{name}.png"
        dest.write_bytes(path.read_bytes())
        print(f"     shot {name}: {dest}", flush=True)
        return dest, np.asarray(Image.open(path).convert("RGB")).astype(int)

    def lines():
        return api(A, "SojournFeed", "lines")["lines"]

    def says(text):
        api(B, "LLChatBar", "sendChat", message=text)

    def line_with(part, seconds=10):
        return wait(lambda: next((l for l in lines() if part in l["text"]), None), seconds)

    def notices_named(name):
        return [n for n in api(A, "LLNotifications", "listChannelNotifications", channel="Visible")["notifications"] if n["name"] == name]

    try:
        for port, who in ((A, "Peer"), (B, "People")):
            up = wait(lambda: json.loads(urllib.request.urlopen(f"http://127.0.0.1:{port}/status", timeout=3).read()).get("connected"), 120)
            check(f"{who} logged in", up)
        a_id = api(A, "LLAgent", "getID")["id"]
        b_id = api(B, "LLAgent", "getID")["id"]
        time.sleep(6)

        api(A, "SojournSettings", "set", chat_bubbles="on", keyword_alerts="on", mention_alerts="on", translator="test", translate_chat="on",
            translate_to="en", hover_tips="on", radar_chat_range="both", radar_region="both", tags_through_walls="off", tag_group_titles="on")
        r = api(A, "SojournSocial", "keywords", words=["pineapple"], whole_words=True)
        check("keywords set", r.get("words") == ["pineapple"], r)
        # People's group (people2.py's), active, so its title rides the tag
        groups = tryapi(B, "SojournPeople", "groupChat").get("groups", [])
        g = next((x for x in groups if x.get("name") == "Sojourn People Test"), None)
        if g:
            tryapi(B, "TesselAgent", "activateGroup", group_id=g["group_id"])
        print("     group for the title:", g and g["name"], flush=True)
        # People in a contact set of Peer's, blue
        tryapi(A, "SojournPeople", "contactSet", name="b4 test", color=[0.36, 0.66, 0.91])
        tryapi(A, "SojournPeople", "contactSetAdd", name="b4 test", to=b_id)
        tryapi(A, "LLAgent", "lookAt", position=api(B, "LLAgent", "getPosition")["region"])
        tryapi(B, "LLAgent", "lookAt", position=api(A, "LLAgent", "getPosition")["region"])

        # --- the feed: keywords, mentions, translation, links ---
        says("anyone for the pineapple party?")
        l = line_with("pineapple party")
        check("a keyword lights the line", l and l.get("alert"), l)
        says("pineapples are nice")
        l = line_with("pineapples are")
        check("whole words: 'pineapples' doesn't", l and not l.get("alert"), l)
        says(f"secondlife:///app/agent/{a_id}/mention see you there")
        l = line_with("see you there")
        check("a mention of the person: @name, lit", l and l["text"].startswith("@") and l.get("alert"), l)
        says("bonjour tout le monde")
        l = wait(lambda: next((x for x in lines() if x["text"] == "bonjour tout le monde" and x.get("translation")), None), 10)
        check("translated under the line (test translator)", l and l["translation"].startswith("[en] ruojnob"), l)
        says("en: already english")
        time.sleep(2)
        l = line_with("already english")
        check("a line already in the person's language isn't", l and not l.get("translation"), l)
        r = api(A, "SojournSocial", "translate", text="hello there")
        check("translate op (test translator)", r.get("text", "").startswith("[en]"), r)
        says("meet at secondlife:///app/teleport/Atelier%20North/40/50/30 later")
        l = line_with("meet at")
        links = wait(lambda: [k for k in api(A, "SojournFeed", "keys")["keys"] if k["kind"] == "link" and "Atelier%20North" in (k.get("button") or "")], 8)
        check("the SLurl is a link on the feed", links, links)
        if links:
            key(27)
            time.sleep(0.4)
            move(links[0]["x"], links[0]["y"])
            time.sleep(1.0)
            k = next((k for k in api(A, "SojournFeed", "keys")["keys"] if k["kind"] == "link"), links[0])
            move(k["x"], k["y"])
            time.sleep(0.6)
            shot("feed-link-pointed")
            click(k["x"], k["y"])
            offer = wait(lambda: next((n for n in notices_named("PlaceOffered") if "Atelier North" in n["message"]), None), 8)
            check("pressed, its place offered", offer, offer and offer["message"])
            if offer:
                api(A, "LLNotifications", "respond", uuid=offer["id"], response={"Cancel": True})
            move(W * 0.5, H * 0.3)
        # opening a line no longer offers its places by itself (B2's way, before links)
        time.sleep(1)
        check("nothing offered without a press", not any("Atelier North" in n["message"] for n in notices_named("PlaceOffered")))

        # --- the reader: a notecard's link pressed ---
        nc = api(A, "TesselInventory", "createNotecard", name="social test places",
                 text="Our places:\nCome by http://maps.secondlife.com/secondlife/Atelier%20Northeast/40/50/30 any time.")
        api(A, "SojournReader", "open", notecard=nc["item_id"])
        time.sleep(1.5)
        path, img = shot("reader-link")
        if img is not None:
            mask = np.abs(img - np.array((0x23, 0x7c, 0x76))).max(axis=2) < 40
            mask[:, :W // 2] = False
            ys, xs = np.nonzero(mask)
            if len(xs) > 20:
                cy = np.median(ys)
                band = np.abs(ys - cy) < 8
                x, y = xs[band].mean(), ys[band].mean()
                key(27)
                time.sleep(0.3)
                move(x, y)
                time.sleep(0.5)
                click(x, y)
                offer = wait(lambda: next((n for n in notices_named("PlaceOffered") if "Atelier Northeast" in n["message"]), None), 8)
                check("the reader's link pressed, its place offered", offer, offer and offer["message"])
                if offer:
                    api(A, "LLNotifications", "respond", uuid=offer["id"], response={"Cancel": True})
            else:
                check("the reader's link drawn in the accent", False, f"{len(xs)} accent pixels")
        api(A, "SojournReader", "close")
        move(W * 0.5, H * 0.15)

        # --- tags: title, ring, bubble; marks ---
        says("the bubble over my tag")
        time.sleep(1.5)
        shot("tags-bubble-title-ring")
        api(B, "SojournSettings", "set", away="on")
        time.sleep(2.5)
        shot("tags-away")
        api(B, "SojournSettings", "set", away="off", do_not_disturb="on")
        time.sleep(2.5)
        shot("tags-dnd")
        api(B, "SojournSettings", "set", do_not_disturb="off")

        # --- walls: a temporary wall between them ---
        pa_pos = api(A, "LLAgent", "getPosition")
        pb_pos = api(B, "LLAgent", "getPosition")
        print("     positions:", pa_pos, pb_pos, flush=True)
        try:
            ap, bp = pa_pos["region"], pb_pos["region"]
            mid = [(ap[0] + bp[0]) / 2, (ap[1] + bp[1]) / 2, max(ap[2], bp[2]) + 0.6]
            dx, dy = bp[0] - ap[0], bp[1] - ap[1]
            import math
            yaw = math.degrees(math.atan2(dy, dx))
            wall = api(A, "TesselBuild", "create", shape="box", pos=mid, scale=[0.15, 3.0, 3.6], rot_euler=[0, 0, yaw], temp=True, name="b4 wall")
            time.sleep(3)
            shot("tags-behind-wall")
            api(A, "SojournSettings", "set", tags_through_walls="on")
            time.sleep(1.5)
            shot("tags-through-wall")
            api(A, "SojournSettings", "set", tags_through_walls="off")
            tryapi(A, "TesselBuild", "take", ids=[wall["id"]])
        except Exception as e:
            check("the wall", False, e)

        # --- hover tip: names off, the pointer resting on People ---
        api(A, "SojournSettings", "set", names="off")
        key(27)
        move(W / 2, H / 2 + 40)
        time.sleep(0.3)
        move(W / 2 + 1, H / 2 + 40)
        time.sleep(1.6)
        shot("hover-tip")
        api(A, "SojournSettings", "set", names="on")
        move(W * 0.5, H * 0.1)

        # --- radar: People walks out of chat range and back, then to the next region and back ---
        before = api(A, "SojournSocial", "status")["radar_reports"]
        bp = api(B, "LLAgent", "getPosition")
        print("     B at", bp, flush=True)
        tryapi(B, "LLAgent", "requestTeleport", regionname="Atelier", x=170, y=112, z=22, skip_confirmation=True, timeout=20)
        left = wait(lambda: next((l for l in lines() if "left chat range" in l["text"]), None), 25)
        check("radar: left chat range", left, left)
        tryapi(B, "LLAgent", "requestTeleport", regionname="Atelier", x=127, y=113, z=22, skip_confirmation=True, timeout=20)
        came = wait(lambda: next((l for l in lines() if "came into chat range" in l["text"]), None), 25)
        check("radar: came into chat range", came, came)
        tryapi(B, "LLAgent", "requestTeleport", regionname="Atelier North", x=128, y=128, z=30, skip_confirmation=True, timeout=20)
        gone = wait(lambda: next((l for l in lines() if "left the region" in l["text"]), None), 30)
        check("radar: left the region", gone, gone)
        tryapi(B, "LLAgent", "requestTeleport", regionname="Atelier", x=127, y=113, z=22, skip_confirmation=True, timeout=20)
        back = wait(lambda: next((l for l in lines() if "arrived in the region" in l["text"]), None), 30)
        check("radar: arrived in the region", back, back)
        r = api(A, "SojournSocial", "radar")
        check("radar op lists People", any(p["id"] == b_id for p in r["people"]), r)
        st = api(A, "SojournSocial", "status")
        print("     status:", json.dumps(st)[:400], flush=True)
        check("reports counted", st["radar_reports"] > before, st["radar_reports"])

        # put things back as they were for the accounts
        api(A, "SojournSettings", "set", chat_bubbles="off", translate_chat="off", translator="none", radar_chat_range="off", radar_region="off")
        api(A, "SojournSocial", "keywords", words=[])
        tryapi(A, "SojournPeople", "contactSetRemove", name="b4 test", to=b_id)
    finally:
        for p in procs:
            try:
                p.terminate()
                p.wait(20)
            except Exception:
                p.kill()
    print("ALL OK" if ok_all else "SOME FAILED")
    return 0 if ok_all else 1


if __name__ == "__main__":
    dll = sys.argv[sys.argv.index("--dll") + 1] if "--dll" in sys.argv else str(ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0")
    sys.exit(main(dll))
