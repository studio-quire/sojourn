"""People and me, the second part (batch B2, 2026-10-28) on the local grid, never the main grid: only friends and groups
can IM me, the reject switches (teleports, friendships, group invitations) and their sentences, inventory accepted by
itself, a group's chat snoozed, off and moderated, display names refused where the grid has none, profile notes, the
money tracker's shape.

  python tools/people2.py [--dll DIR]    the same two clients as tools/people.py (Sojourn People on :9861 with
                                         SOJOURN_TEST_INPUT=1, Sojourn Peer on :9862), not friends at the start

Gated ops on People are pressed as tools/people.py presses them; Peer's switches are set through its settings (no
gate). What only Second Life shows (money, display names, experiences, moderation as SL's chat server does it) is on
the user's checklist in docs/people.md. Written by Claude Opus 5.5, 2026-10-28.
"""
import json
import subprocess
import sys
import threading
import time
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import people as P  # noqa: E402  (api, tryapi, wait, check, client and the shared constants)

ROOT, BASE, A, B, W, H = P.ROOT, P.BASE, P.A, P.B, P.W, P.H
api, tryapi, wait, check = P.api, P.tryapi, P.wait, P.check
GROUP = "Sojourn People Test"


def events(side, since=0):
    path = next((BASE / side / "state").rglob("events.jsonl"))
    lines = path.read_text(encoding="utf-8", errors="replace").splitlines()[since:]
    return [json.loads(l) for l in lines if l.strip()]


def event_count(side):
    return len(next((BASE / side / "state").rglob("events.jsonl")).read_text(encoding="utf-8", errors="replace").splitlines())


def notices(side, since):
    return [e["data"]["name"] for e in events(side, since) if e.get("kind") == "ClaudeNotify"]


def main(dll):
    import grid
    if "agni" in grid.URL or not grid.ready():
        sys.exit("the local grid isn't up (python tools/grid.py start)")
    for last in ("People", "Peer"):
        grid.account("Sojourn", last, "sojourn-local")
    import os
    env = dict(os.environ, SOJOURN_TEST_INPUT="1")
    pa = P.client("Sojourn", "People", A, "a", f"{W}x{H}", 20, dll, "uri:Atelier&124&112&22", env)
    pb = P.client("Sojourn", "Peer", B, "b", "320x180", 5, dll, "uri:Atelier&127&113&22")
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
        move(300, 500)
        time.sleep(0.3)
        path, img = shot()
        if img is None:
            return path, None
        mask = np.abs(img - np.array((0x23, 0x7c, 0x76))).max(axis=2) < 14
        mask[:, :W // 2] = False
        ys, xs = np.nonzero(mask)
        if len(xs) <= 50:
            return path, None
        cy = np.median(ys)
        band = np.abs(ys - cy) < 12
        xs, ys = xs[band], ys[band]
        cx = np.median(xs)
        near = np.abs(xs - cx) < 120
        xs, ys = xs[near], ys[near]
        return path, (xs.mean(), ys.mean(), np.percentile(xs, 98) - np.percentile(xs, 2))

    def gated(pump, op, yes, **args):
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
        for _ in range(3):
            key(27)
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
            up = wait(lambda: json.loads(P.urllib.request.urlopen(f"http://127.0.0.1:{port}/status", timeout=3).read()).get("connected"), 120)
            check(f"{who} logged in", up)
            if not up:
                return
            tryapi(port, "SojournSettings", "set", screen_reader="off")   # a screen reader on the PC would turn reader mode on
        time.sleep(6)
        api(A, "SojournSettings", "set", away_after=0)
        a_id = api(A, "LLAgent", "getID")["id"]
        b_id = api(B, "LLAgent", "getID")["id"]
        # a clean start: not friends, every switch off
        if any(f["id"] == b_id for f in api(A, "TesselAgent", "friends")["friends"]):
            gated("SojournPeople", "removeFriend", True, to=b_id)
            wait(lambda: not any(f["id"] == b_id for f in api(A, "TesselAgent", "friends")["friends"]), 10)
        for port in (A, B):
            api(port, "SojournSettings", "set", friends_only_ims="off", reject_teleports="off", reject_friendships="off",
                reject_group_invites="off", auto_accept_inventory="off")

        def b_lines():
            ss = api(B, "TesselIM", "sessions")["sessions"]
            s = next((s for s in ss if s["other_id"] == a_id and s["type"] == "p2p"), None)
            return [m["message"] for m in api(B, "TesselIM", "history", session_id=s["session_id"], count=5000)["messages"]] if s else []

        def a_heard(text):
            for s in api(A, "TesselIM", "sessions")["sessions"]:
                if any(m["message"] == text for m in api(A, "TesselIM", "history", session_id=s["session_id"], count=5000)["messages"]):
                    return True
            return False

        # --- only friends and groups can IM me ---
        api(A, "SojournSettings", "set", friends_only_ims="on")
        mark = event_count("a")
        hush = f"not a friend {int(time.time())}"
        api(B, "TesselIM", "send", to=a_id, message=hush)
        time.sleep(4)
        check("a non-friend's IM isn't shown", not a_heard(hush), "")
        check("a quiet notice says why, once", notices("a", mark).count("IMNotShownNotFriend") == 1, notices("a", mark))
        api(B, "TesselIM", "send", to=a_id, message=hush + " again")
        api(B, "TesselAgent", "offerTeleport", to=a_id, message="come over")
        api(B, "TesselAgent", "requestTeleport", to=a_id, message="let me come")
        time.sleep(4)
        n = notices("a", mark)
        check("still once a conversation; their lures and requests dropped",
              n.count("IMNotShownNotFriend") == 1 and "TeleportOffered" not in n and "TeleportRequest" not in n, n)
        api(A, "SojournSettings", "set", friends_only_ims="off")
        ok_msg = f"friends only off {int(time.time())}"
        api(B, "TesselIM", "send", to=a_id, message=ok_msg)
        check("off again: shown", wait(lambda: a_heard(ok_msg), 10), "")

        # --- turn away teleport offers ---
        api(A, "SojournSettings", "set", reject_teleports="all")
        mark = event_count("a")
        before = len(b_lines())
        api(B, "TesselAgent", "offerTeleport", to=a_id, message="come over")
        got = wait(lambda: [m for m in b_lines()[before:] if "reject all teleport offers" in m], 10)
        check("teleport offer turned away with the sentence", got and "TeleportOffered" not in notices("a", mark), got)
        r, path = gated("SojournPeople", "setResponse", True, kind="teleports", text="No teleports right now, thanks")
        check("their own words for it, confirmed", r and r.get("teleports_response") == "No teleports right now, thanks", r)
        print("   words confirmation shot:", path)
        before = len(b_lines())
        api(B, "TesselAgent", "requestTeleport", to=a_id, message="let me come")
        got = wait(lambda: [m for m in b_lines()[before:] if m == "No teleports right now, thanks"], 10)
        check("a teleport request turned away with their words", got and "TeleportRequest" not in notices("a", mark), got)
        api(A, "SojournSettings", "set", reject_teleports="not_friends")
        api(B, "TesselAgent", "offerTeleport", to=a_id, message="come over")
        time.sleep(3)
        check("not_friends still turns away a non-friend", "TeleportOffered" not in notices("a", mark), "")
        api(A, "SojournPeople", "setResponse", kind="teleports", text="")
        api(A, "SojournSettings", "set", reject_teleports="off")
        api(B, "TesselAgent", "offerTeleport", to=a_id, message="come over")
        check("off: the offer arrives", wait(lambda: "TeleportOffered" in notices("a", mark), 10), "")
        st = api(A, "SojournPeople", "rejects")
        check("counted this session", st["turned_away_this_session"].get("teleports", 0) >= 3, st["turned_away_this_session"])

        # --- turn away friendship offers ---
        api(A, "SojournSettings", "set", reject_friendships="on")
        mark, mark_b = event_count("a"), event_count("b")
        before = len(b_lines())
        api(B, "TesselAgent", "offerFriendship", to=a_id, message="friends?")
        got = wait(lambda: [m for m in b_lines()[before:] if "reject all friendship requests" in m], 10)
        check("friendship offer turned away with the sentence", got and not any(x.startswith("OfferFriendship") for x in notices("a", mark)), got)
        check("and declined (beyond Firestorm: nothing left pending)", wait(lambda: "FriendshipDeclined" in notices("b", mark_b), 10), notices("b", mark_b))
        api(A, "SojournSettings", "set", reject_friendships="off")
        # a re-offer afterwards reaches People (the grid kept no half friendship to refuse it with), declined here
        api(B, "TesselAgent", "offerFriendship", to=a_id, message="friends now?")
        n = wait(lambda: next((n for n in api(A, "LLNotifications", "listChannelNotifications", channel="Visible")["notifications"]
                               if n["name"].startswith("OfferFriendship") and b_id in n["message"] and "friends now?" in n["message"]), None), 10)
        check("a re-offer afterwards arrives", n, n and n["name"])
        if n:
            api(A, "LLNotifications", "respond", uuid=n["id"], response={"Decline": True})

        # --- inventory accepted by itself (on Peer: People's give is the gated one, pressed here) ---
        api(B, "SojournSettings", "set", auto_accept_inventory="viewer")
        mark_b = event_count("b")
        nc = api(A, "TesselInventory", "createNotecard", name="people2 gift", text="hello")
        r, _ = gated("TesselAgent", "give", True, to=b_id, item_id=nc["item_id"])
        got = wait(lambda: "InvOfferAutoAcceptUser" in notices("b", mark_b), 12)
        check("a notecard kept without asking, a notice says so", got and "UserGiveItem" not in notices("b", mark_b), notices("b", mark_b))
        api(B, "SojournSettings", "set", auto_accept_inventory="off")
        mark_b = event_count("b")
        r, _ = gated("TesselAgent", "give", True, to=b_id, item_id=nc["item_id"])
        check("off: offered as before", wait(lambda: "UserGiveItem" in notices("b", mark_b), 12), notices("b", mark_b))
        for n in api(B, "LLNotifications", "listChannelNotifications", channel="Visible")["notifications"]:
            if n["name"] == "UserGiveItem":
                api(B, "LLNotifications", "respond", uuid=n["id"], response={"Discard": True})

        # --- a group: People's own, Peer invited (People confirms making it) ---
        mine = [g for g in api(A, "SojournPeople", "groupChat")["groups"] if g["name"] == GROUP]
        if mine:
            gid = mine[0]["group_id"]
        else:
            r, _ = gated("TesselGroups", "create", True, name=GROUP, open_enrollment=False)
            check("group made (confirmed)", r and r.get("group_id"), r)
            gid = r["group_id"]
            wait(lambda: any(g["group_id"] == gid for g in api(A, "SojournPeople", "groupChat")["groups"]), 15)

        def b_in_group():
            return any(g["group_id"] == gid for g in api(B, "SojournPeople", "groupChat")["groups"])

        def invite_b():
            mark_b = event_count("b")
            api(A, "TesselGroups", "invite", group_id=gid, to=b_id)
            return mark_b

        if b_in_group():
            r, _ = gated("TesselGroups", "eject", True, group_id=gid, member=b_id)
            wait(lambda: not b_in_group(), 15)
        # turned away first, then joined
        api(B, "SojournSettings", "set", reject_group_invites="on")
        mark_b = invite_b()
        time.sleep(5)
        check("a group invitation turned away", "JoinGroup" not in notices("b", mark_b), notices("b", mark_b))
        api(B, "SojournSettings", "set", reject_group_invites="off")
        mark_b = invite_b()
        inv = wait(lambda: next((n for n in api(B, "LLNotifications", "listChannelNotifications", channel="Visible")["notifications"]
                                 if n["name"] == "JoinGroup"), None), 12)
        check("off: the invitation arrives", inv, "")
        if inv:
            api(B, "LLNotifications", "respond", uuid=inv["id"], response={"Join": True})
        check("Peer in the group", wait(b_in_group, 20), "")

        def b_group_lines():
            ss = api(B, "TesselIM", "sessions")["sessions"]
            s = next((s for s in ss if s["session_id"] == gid), None)
            return [m["message"] for m in api(B, "TesselIM", "history", session_id=gid, count=5000)["messages"]] if s else []

        def say(text):
            api(A, "TesselIM", "send", group_id=gid, message=text)

        t = int(time.time())
        say(f"hello group {t}")
        check("group chat reaches Peer", wait(lambda: f"hello group {t}" in b_group_lines(), 15), b_group_lines()[-3:])

        # snooze
        r = api(B, "SojournPeople", "snoozeGroupChat", group=GROUP, minutes=1)
        check("snoozed", r.get("snoozed_minutes_left", 0) > 0.5, r)
        say(f"while snoozed {t}")
        time.sleep(4)
        check("its lines not shown while snoozed", f"while snoozed {t}" not in b_group_lines(), "")
        api(B, "SojournPeople", "snoozeGroupChat", group=gid, minutes=0)
        say(f"after the snooze {t}")
        check("back after it ends", wait(lambda: f"after the snooze {t}" in b_group_lines(), 12), "")
        r = api(B, "SojournPeople", "snoozeGroupChat", group=gid, minutes=0.05)
        time.sleep(4)
        say(f"after it ran out {t}")
        check("a snooze that ran out ends at the next line", wait(lambda: f"after it ran out {t}" in b_group_lines(), 12), "")

        # receive group chat off
        r = api(B, "SojournPeople", "setGroupChat", group=gid, on=False)
        check("chat off", r.get("chat_on") is False, r)
        mutes = json.dumps(tryapi(B, "TesselAgent", "mutes"))
        check("kept on the block list as Group:<id>", f"Group:{gid}" in mutes, "")
        say(f"chat off {t}")
        time.sleep(4)
        check("its lines not shown while off", f"chat off {t}" not in b_group_lines(), "")
        api(B, "SojournPeople", "setGroupChat", group=gid, on=True)
        say(f"chat on {t}")
        on_again = wait(lambda: f"chat on {t}" in b_group_lines(), 12)
        if not on_again:   # left the session while off: a line of Peer's own joins it again, as the viewer's would
            api(B, "TesselIM", "send", group_id=gid, message="back")
            say(f"chat on again {t}")
            on_again = wait(lambda: f"chat on again {t}" in b_group_lines(), 12)
        check("on again: shown", on_again, b_group_lines()[-3:])

        # moderation: People (the owner) stops Peer's text, then allows it
        api(B, "TesselIM", "send", group_id=gid, message="Peer here")
        time.sleep(3)
        info = api(A, "SojournPeople", "groupChat", group=gid)
        check("People moderates its group's chat", info.get("i_moderate"), {k: info.get(k) for k in ("in_chat", "i_moderate")})
        r, path = gated("SojournPeople", "moderateGroupChat", False, group=gid, to=b_id, text=False)
        check("moderation asks (cancelled)", "didn't confirm" in json.dumps(r), r)
        r, path = gated("SojournPeople", "moderateGroupChat", True, group=gid, to=b_id, text=False)
        # OpenSim's ChatSessionRequest here is the WebRTC voice module's, which knows no "mute update": a 400 after the
        # press is as far as this grid goes (SL's chat server is on the user's checklist)
        opensim_400 = "refused it (400)" in json.dumps(r)
        check("Peer's text stopped (confirmed), or OpenSim's 400 after the press", opensim_400 or (isinstance(r, dict) and r.get("text_allowed") is False), r)
        print("   moderation confirmation shot:", path)
        mark = event_count("a")
        api(B, "TesselIM", "send", group_id=gid, message=f"muted {t}")
        time.sleep(4)
        heard = any(e.get("data", {}).get("message") == f"muted {t}" for e in events("a", mark))
        info = api(A, "SojournPeople", "groupChat", group=gid)
        peer = next((m for m in info.get("members", []) if m["id"] == b_id), {})
        print(f"   OpenSim: Peer's line after the stop {'still arrived' if heard else 'did not arrive'}; member list says text_stopped={peer.get('text_stopped')}")
        r, _ = gated("SojournPeople", "moderateGroupChat", True, group=gid, to=b_id, text=True)
        check("allowed again (confirmed), or OpenSim's 400", "refused it (400)" in json.dumps(r) or (isinstance(r, dict) and r.get("text_allowed") is True), r)

        # --- display name: OpenSim serves names with a week's lockout of its own (it has no SetDisplayName) ---
        r = tryapi(A, "SojournPeople", "displayName")
        check("display name read", "username" in r and "can_change_now" in r, r)
        t0 = time.time()
        r = tryapi(A, "SojournPeople", "setDisplayName", name="Someone New")
        refused_fast = "exception" in r and time.time() - t0 < 5
        check("a change that can't go is refused at once, not asked", refused_fast, r)

        # --- notes ---
        # (replacing notes that say something else waits for the person's press, as a delete; none there, or an append, doesn't)
        if tryapi(A, "SojournPeople", "notes", to=b_id).get("notes"):   # an earlier run's
            r, _ = gated("SojournPeople", "setNotes", True, to=b_id, text="")
            check("an earlier run's notes cleared (confirmed)", isinstance(r, dict) and r.get("notes") == "", r)
        text = f"met at the people test {t}"
        r = tryapi(A, "SojournPeople", "setNotes", to=b_id, text=text, timeout=25)
        got = tryapi(A, "SojournPeople", "notes", to=b_id)
        check("notes where there were none, without asking; kept on the grid and read back", got.get("notes") == text, {"set": r, "read": got})
        r = tryapi(A, "SojournPeople", "setNotes", to=b_id, text="second line", append=True, timeout=25)
        check("append adds a line without asking, was says what it had", r.get("notes") == text + "\nsecond line" and r.get("was") == text, r)
        r, path = gated("SojournPeople", "setNotes", False, to=b_id, text="something else")
        check("replacing them asks (cancelled), nothing changed", "didn't confirm" in json.dumps(r)
              and tryapi(A, "SojournPeople", "notes", to=b_id).get("notes") == text + "\nsecond line", r)
        print("   notes confirmation shot:", path)
        r, _ = gated("SojournPeople", "setNotes", True, to=b_id, text="something else")
        check("replaced (confirmed), was the old text", isinstance(r, dict) and r.get("notes") == "something else" and r.get("was") == text + "\nsecond line", r)
        r = tryapi(A, "SojournPeople", "setNotes", to=b_id, text="something else", timeout=25)
        check("the same text again, without asking", r.get("notes") == "something else", r)

        # --- money: the tracker's shape (the local grid moves no L$) ---
        m = api(A, "SojournPeople", "money")
        check("money tracker", all(k in m for k in ("received", "spent", "payers", "total_received")), m)
    finally:
        for p in procs:
            p.terminate()
        for p in procs:
            try:
                p.wait(15)
            except subprocess.TimeoutExpired:
                p.kill()
    print("ALL OK" if P.ok_all else "SOME FAILED")


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--dll") + 1] if "--dll" in sys.argv else str(ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0"))
