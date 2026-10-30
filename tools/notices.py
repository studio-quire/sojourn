"""Sojourn's notices and reader on the local grid, on the desktop (python tools/notices.py ...).

  python tools/notices.py test      a client (Sojourn Notices) touches the Notices Harp and answers it by hand
  python tools/notices.py fixture   the Notices Harp (an OAR merged into Atelier, owned by Sojourn Notices)

The Notices Harp is a small box at Atelier 70,30,30 with a script that sends a script menu of eleven buttons (More >
first, as the Tidewater Harp's that never showed in the headset on 2026-10-27), a second page from More > (< Back
returns), says "playing <piece>" for a piece, and gives its notecard to whoever says "card" on channel 9. The test
presses the feed's keys with posted clicks (SOJOURN_TEST_INPUT=1), as a hand on the mouse would: More >, a piece, the
notice's x, the clear key, and a notecard's Read, which opens it in the reader.

Written by Claude Opus 5.5, 2026-10-27.
"""
import json
import os
import shutil
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

BASE = ROOT / "local" / "notices"
API_PORT = 9741
NAME = "Notices Harp"
CARD = "Notices Card"
TAG = "tools/notices.py 1"
SPOT = (70.0, 30.0, 30.0)
CARD_TEXT = "\n\n".join(f"{i}. The harp's notecard, paragraph {i}: long enough to scroll, so the reader has something to do."
                        for i in range(1, 30))

SCRIPT = """// Notices Harp (tools/notices.py): an eleven-button menu with More >, a second page, and a notecard on "card" (ch 9)
integer ch;
integer page;
list P1 = ["More >", "Stop", "Night Sail", "Lost Unicorn", "Pigeon-sized", "Stella's Garden", "The Waypost",
           "Tranquility Base", "Tidewater Harp", "Fireside Air", "Moonwater"];
list P2 = ["< Back", "Lullaby", "Reel"];
menu(key id)
{
    if (page == 0) llDialog(id, "Notices Harp: choose a piece.", P1, ch);
    else llDialog(id, "Notices Harp: more pieces.", P2, ch);
}
default
{
    state_entry() { ch = -5000 - (integer)llFrand(10000.0); llListen(ch, "", NULL_KEY, ""); llListen(9, "", NULL_KEY, "card"); }
    on_rez(integer p) { llResetScript(); }
    touch_start(integer n) { page = 0; menu(llDetectedKey(0)); }
    listen(integer c, string name, key id, string msg)
    {
        if (c == 9) { llGiveInventory(id, "%(card)s"); return; }
        if (msg == "More >") { page = 1; menu(id); }
        else if (msg == "< Back") { page = 0; menu(id); }
        else llSay(0, "playing " + msg);
    }
}
""" % dict(card=CARD)


def uid(*parts):
    import uuid
    return str(uuid.uuid5(uuid.UUID("0e9f3b52-5d4a-4c51-9a8e-7c2f6d1b3a40"), "/".join(map(str, (TAG,) + parts))))


def notecard_asset(text):
    body = text.encode("utf-8")
    return (b"Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length " + str(len(body)).encode()
            + b"\n" + body + b"}\n")


def build_oar(owner):
    import tarfile
    import audio
    import forest
    import media_fixtures as mf
    BASE.mkdir(parents=True, exist_ok=True)
    out = BASE / "notices-harp.oar"
    pid = uid("part")
    with tarfile.open(out, "w:gz") as tar:
        forest.add(tar, "archive.xml", forest.archive_xml())
        te = forest.te_bytes(mf.PLYWOOD, color=(0.85, 0.75, 0.55, 1))
        xml = forest.part(pid, 140, NAME, SPOT, (0, 0, 0, 1), (0.5, 0.5, 0.5), te)
        xml = xml.replace(forest.OWNER, owner).replace("<Description />", f"<Description>{TAG}</Description>")
        items = (audio.task_item(uid("script item"), uid("script asset"), "harp", 10, pid, owner)
                 + audio.task_item(uid("card item"), uid("card asset"), CARD, 7, pid, owner))
        xml = xml.replace("<InventorySerial>0</InventorySerial>", "<InventorySerial>2</InventorySerial>")
        xml = xml.replace("</SceneObjectPart>", "<TaskInventory>" + items + "</TaskInventory></SceneObjectPart>")
        forest.add(tar, f"objects/Notices_Harp__{pid}.xml", forest.group([xml]).encode())
        forest.add(tar, f"assets/{uid('script asset')}_script.lsl", SCRIPT.encode())
        forest.add(tar, f"assets/{uid('card asset')}_notecard.txt", notecard_asset(CARD_TEXT))
    return out


def in_world():
    import sqlite3
    import grid
    c = sqlite3.connect(f"file:{grid.BIN / 'OpenSim.db'}?mode=ro", uri=True)
    try:
        return c.execute("select Description from prims where Name = ?", (NAME,)).fetchall()
    finally:
        c.close()


def fixture():
    import grid
    import media_fixtures as mf
    print(grid.account("Sojourn", "Notices"))
    owner = mf.accounts()["Sojourn Notices"]
    have = in_world()
    if have and all(h[0] == TAG for h in have):
        return 0
    grid.console('change region "Atelier"', wait=1)
    if have:
        grid.console(f'delete object name "{NAME}"', wait=2, answers=("y",))
    path = build_oar(owner).resolve()
    print("\n".join(grid.console(f'load oar --merge "{path}"', wait=5)[-2:]))
    end = time.time() + 60
    while time.time() < end and not in_world():
        grid.console("backup", wait=3)
        time.sleep(2)
    return 0 if in_world() else 1


def api(pump, op, timeout=30, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{API_PORT}/cmd", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 20) as r:
        out = json.loads(r.read())
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(f"{pump}.{op}: {out['error']}")
    return out


def wait(f, secs, step=0.4):
    end = time.time() + secs
    while time.time() < end:
        try:
            v = f()
            if v:
                return v
        except Exception:
            pass
        time.sleep(step)
    return None


def test():
    if fixture():
        print("the Notices Harp isn't in the world")
        return 1
    dll = Path(os.environ.get("SOJOURN_DLL") or ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "SojournVR.dll")
    shutil.rmtree(BASE / "client", ignore_errors=True)
    (BASE / "client").mkdir(parents=True)
    (BASE / "client" / "vr-settings.json").write_text(json.dumps({"confirmTouches": False, "panelHidden": True}))
    env = dict(os.environ, SOJOURN_TEST_INPUT="1")
    dotnet = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
    log = open(BASE / "console.txt", "w", encoding="utf-8")
    W, H = 960, 600
    proc = subprocess.Popen([str(dotnet), str(dll), "--window", f"{W}x{H}", "--fps", "20", "--no-assistant", "--no-voice", "--no-sounds",
                             "--first", "Sojourn", "--last", "Notices", "--password", "sojourn-local", "--bridge-port", str(API_PORT),
                             "--base-dir", str(BASE / "client"), "--start", "uri:Atelier&66&30&24", "--minutes", "8", "--sky", "noon"],
                            cwd=ROOT, env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, text=True,
                            creationflags=0x00004000)
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print(f"{'ok  ' if cond else 'FAIL'} {name}: {detail}", flush=True)

    def click(x, y):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winmouse.ps1"), "-X", str(int(x)), "-Y", str(int(y)),
                        "-Click", "-ProcessId", str(proc.pid)], capture_output=True)

    def move(x, y):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winmouse.ps1"), "-X", str(int(x)), "-Y", str(int(y)),
                        "-ProcessId", str(proc.pid)], capture_output=True)

    def keys():
        return api("SojournFeed", "keys")["keys"]

    def answers():
        return [k for k in keys() if k["kind"] == "answer"]

    def press(k):
        # the pointer onto the key first, a frame or two before the press (as a hand would), and a notice's keys arm a
        # moment after it shows
        move(k["x"], k["y"])
        time.sleep(0.8)
        click(k["x"], k["y"])

    try:
        up = wait(lambda: api("LLAgent", "getPosition", timeout=5), 120, 1)
        check("logged in", up, str(up)[:80])
        harp_id = wait(lambda: next((o["id"] for o in api("TesselObjects", "nearby", dist=60, query=NAME)["objects"] if o.get("name") == NAME), None), 60, 1)
        check("the harp is in view", harp_id, str(harp_id))
        api("LLAgent", "requestTouch", obj_uuid=harp_id)
        first = wait(lambda: answers() if len(answers()) == 11 else None, 30)
        check("its menu is on the feed with eleven keys", first, f"{len(first or [])} keys")
        if first:
            more = next(k for k in first if k["button"] == "More >")
            bottom = max(k["y"] for k in first)
            check("More > at the bottom left, as the viewer lays a menu out", more["y"] == bottom and more["x"] == min(k["x"] for k in first if k["y"] == bottom),
                  f"More > at {more['x']},{more['y']}")
            check("a key to put it away", any(k["kind"] == "dismiss" for k in keys()))
            press(more)
            # at once, though the pointer is still on the sheet (new lines wait while it's in use; what a press brings doesn't)
            page2 = wait(lambda: [k for k in answers() if k["button"] == "< Back"], 8)
            check("More > brought the next page in its place", page2 and len(answers()) == 3, f"{[k['button'] for k in answers()]}")
            if page2:
                lull = next(k for k in answers() if k["button"] == "Lullaby")
                press(lull)
                gone = wait(lambda: not answers(), 15)
                check("a piece answered it: the notice left the feed", gone)
                pending = api("LLNotifications", "listChannelNotifications", channel="Visible")["notifications"]
                check("and nothing waits", not [n for n in pending if n.get("name") == "ScriptDialog"], f"{len(pending)} waiting")
        # put away by its x
        api("LLAgent", "requestTouch", obj_uuid=harp_id)
        wait(lambda: len(answers()) == 11, 20)
        x = wait(lambda: next((k for k in keys() if k["kind"] == "dismiss"), None), 10)
        if x:
            press(x)
            check("its x put it away unanswered", wait(lambda: not answers(), 10))
            pending = api("LLNotifications", "listChannelNotifications", channel="Visible")["notifications"]
            check("and it no longer waits", not [n for n in pending if n.get("name") == "ScriptDialog"])
        # the clear key: two waiting, the pointer on the sheet, all put away
        api("LLNotifications", "requestAdd", name="SystemMessage", substitutions={"MESSAGE": "first test notice"})
        api("LLNotifications", "requestAdd", name="SystemMessage", substitutions={"MESSAGE": "second test notice"})
        ks = wait(lambda: [k for k in keys() if k["kind"] == "dismiss"] if len([k for k in keys() if k["kind"] == "dismiss"]) >= 2 else None, 10)
        if ks:
            move(ks[0]["x"] - 60, ks[0]["y"])
            clear = wait(lambda: next((k for k in keys() if k["kind"] == "clearall"), None), 5)
            check("the clear key shows with the pointer on the sheet", clear)
            if clear:
                press(clear)
                check("clear put every waiting notice away", wait(lambda: api("SojournFeed", "notices")["waiting"] == 0, 10))
        # a notecard given: Read opens it in the reader
        api("LLChatBar", "sendChat", message="card", channel=9)
        read = wait(lambda: next((k for k in keys() if k["kind"] == "read"), None), 20)
        check("a notecard given has a Read key", read)
        if read:
            press(read)
            state = wait(lambda: (lambda st: st if st.get("open") else None)(api("SojournReader", "state")), 25)
            check("Read opened it in the reader", state and state.get("kind") == "notecard" and state.get("title") == CARD, str(state))
            if not state:
                try:
                    found = api("SojournAssistant", "findInventory", query=CARD, type="notecard")
                    print("     inventory:", str(found)[:300])
                    item = found["items"][0]["id"]
                    print("     readNotecard:", str(api("TesselInventory", "readNotecard", item_id=item))[:200])
                except Exception as e:
                    print("     ", e)
            api("SojournReader", "close")
        # the reader for the assistant: text, then replies
        r = api("SojournReader", "open", text="\n\n".join(f"Paragraph {i}." for i in range(80)), title="Long text")
        check("the assistant's text opens", r.get("open") and r.get("kind") == "text", str(r))
        check("too much text is refused", raises(lambda: api("SojournReader", "open", text="x" * 200_001)))
        api("SojournReader", "close")
        check("closed", not api("SojournReader", "state")["open"])
    finally:
        proc.terminate()
        try:
            proc.wait(20)
        except subprocess.TimeoutExpired:
            proc.kill()
    print("all good" if ok else "something failed (local/notices/console.txt)")
    return 0 if ok else 1


def raises(f):
    try:
        f()
        return False
    except Exception:
        return True


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "test"
    sys.exit({"fixture": fixture, "test": test}[cmd]())
