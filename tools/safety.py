"""Safety and comfort (batch B1) on the local grid (python tools/safety.py ...).

  python tools/safety.py fixture    the Safety Rig in Atelier, owned by Sojourn Friend (an OAR merged in): the Spammer,
                                    twelve linked prims that flood on command (chat, IMs, menus, sounds, a long message,
                                    a push), the Lamp (a looped sound and particles) and the Seat (it takes the controls
                                    and the mouselook button, and dances you)
  python tools/safety.py test       desktop, as Sojourn Safety (:9765): derendering and back, until a teleport and for
                                    good; antispam on chat, IMs, menus, sounds and a long message; particles off and a
                                    blocked owner's; sounds and an animation blacklisted; keep me in place against a push;
                                    release keys
  python tools/safety.py headset    the XR Simulator (:9766): the comfort vignette while pushed (none with keep me in
                                    place, the avatar walked back to the view); aiming on the Seat (mouselook told to the
                                    region, the right trigger as the mouselook button); mirror shots in local/safety/vr/shots

The rig answers anyone on channel 7. The client's sounds play at volume 0; antispam and the vignette (off by default) are turned on for the tests. Written by Claude Opus 5.5, 2026-10-27.
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

BASE = ROOT / "local" / "safety"
DESK_PORT, VR_PORT = 9765, 9766
TAG = "tools/safety.py 5"
STAND = (74.0, 40.0)
SPAMMER = (78.0, 44.0, 25.0)
LAMP = (80.0, 38.0, 25.0)
SEAT = (76.0, 36.0, 24.5)
DANCE = "b68a3d7c-de9e-fc87-eec8-543d787e5b0d"   # the built-in dance1

SPAMMER_SCRIPT = """// Safety Spammer (tools/safety.py): every prim of the linkset sends one IM or menu on its turn; the root listens
default
{
    state_entry() { if (llGetLinkNumber() <= 1) llListen(7, "", NULL_KEY, ""); }
    on_rez(integer p) { llResetScript(); }
    listen(integer ch, string name, key id, string msg)
    {
        list a = llParseString2List(msg, [" "], []);
        string c = llList2String(a, 0);
        integer i;
        if (c == "chat") for (i = 0; i < 15; i++) llRegionSayTo(id, 0, "spam chat " + (string)i);
        else if (c == "lines") { string s = "a long one"; for (i = 0; i < 80; i++) s += "\\nline " + (string)i; llRegionSayTo(id, 0, s); }
        else if (c == "sound") for (i = 0; i < 70; i++) llTriggerSound("tone_a", 0.2);
        else if (c == "im" || c == "dialog") llMessageLinked(LINK_SET, 0, c, id);
        else if (c == "push") llPushObject(id, <(float)llList2String(a, 1), 0, 0>, ZERO_VECTOR, FALSE);
    }
    link_message(integer s, integer n, string m, key id)
    {
        if (m == "im") llInstantMessage(id, "spam im " + (string)llGetLinkNumber());
        else if (m == "dialog") llDialog(id, "spam menu " + (string)llGetLinkNumber(), ["OK"], -777);
    }
}
"""

LAMP_SCRIPT = """// Safety Lamp (tools/safety.py): a looped sound and particles while it's on ("lamp on" / "lamp off", channel 7), so it
// doesn't disturb other tests nearby the rest of the time
default
{
    state_entry() { llStopSound(); llParticleSystem([]); llListen(7, "", NULL_KEY, ""); }
    on_rez(integer p) { llResetScript(); }
    listen(integer ch, string name, key id, string msg)
    {
        if (msg == "lamp off") { llStopSound(); llParticleSystem([]); }
        else if (msg == "lamp on")
        {
            llLoopSound("beep", 0.3);
            llParticleSystem([PSYS_PART_FLAGS, PSYS_PART_EMISSIVE_MASK, PSYS_SRC_PATTERN, PSYS_SRC_PATTERN_EXPLODE,
                PSYS_SRC_BURST_RATE, 0.1, PSYS_SRC_BURST_PART_COUNT, 20, PSYS_PART_MAX_AGE, 3.0, PSYS_SRC_BURST_SPEED_MIN, 0.3,
                PSYS_SRC_BURST_SPEED_MAX, 0.6, PSYS_PART_START_COLOR, <1, 0.6, 0.2>, PSYS_PART_START_SCALE, <0.2, 0.2, 0>]);
        }
    }
}
"""

SEAT_SCRIPT = """// Safety Seat (tools/safety.py): takes the forward key and the mouselook button from whoever sits, dances them, and
// says what it hears and whether they're in mouselook
key av;
integer ml = -1;
default
{
    state_entry() { llSitTarget(<0, 0, 0.6>, ZERO_ROTATION); llListen(7, "", NULL_KEY, "ride"); }
    // "ride": round a 4 m square at 2 m/s (a ride that carries whoever sits)
    listen(integer ch, string name, key id, string msg)
    {
        llSetKeyframedMotion([<4, 0, 0>, 2.0, <0, 4, 0>, 2.0, <-4, 0, 0>, 2.0, <0, -4, 0>, 2.0], [KFM_DATA, KFM_TRANSLATION]);
    }
    on_rez(integer p) { llResetScript(); }
    changed(integer c)
    {
        if (!(c & CHANGED_LINK)) return;
        key now = llAvatarOnSitTarget();
        if (now != NULL_KEY && now != av) { av = now; ml = -1; llRequestPermissions(av, PERMISSION_TAKE_CONTROLS | PERMISSION_TRIGGER_ANIMATION); }
        else if (now == NULL_KEY) { av = NULL_KEY; llSetTimerEvent(0); }
    }
    run_time_permissions(integer p)
    {
        if (p & PERMISSION_TAKE_CONTROLS) { llTakeControls(CONTROL_FWD | CONTROL_ML_LBUTTON, TRUE, FALSE); llRegionSayTo(av, 0, "seat: controls taken"); }
        if (p & PERMISSION_TRIGGER_ANIMATION) llStartAnimation("dance1");
        llSetTimerEvent(0.5);
    }
    control(key id, integer level, integer edge)
    {
        if (edge & CONTROL_ML_LBUTTON) llRegionSayTo(id, 0, "seat: ml " + llList2String(["up", "down"], (level & CONTROL_ML_LBUTTON) != 0));
    }
    timer()
    {
        if (av == NULL_KEY) return;
        integer now = (llGetAgentInfo(av) & AGENT_MOUSELOOK) != 0;
        if (now != ml) { ml = now; llRegionSayTo(av, 0, "seat: mouselook " + llList2String(["off", "on"], now)); }
    }
}
"""

NAMES = ("Safety Spammer", "Safety Lamp", "Safety Seat")


def uid(*parts):
    import uuid
    return str(uuid.uuid5(uuid.UUID("7c1e5a90-3b2d-4f61-8e47-2a9d0c6b5f13"), "/".join(map(str, (TAG,) + parts))))


def build_oar(owner):
    import tarfile
    import audio
    import forest
    import media_fixtures as mf
    audio.make_assets()
    BASE.mkdir(parents=True, exist_ok=True)
    out = BASE / "safety-rig.oar"

    def with_items(xml, pid, items):
        xml = xml.replace(forest.OWNER, owner).replace("<Description />", f"<Description>{TAG}</Description>")
        xml = xml.replace("<InventorySerial>0</InventorySerial>", f"<InventorySerial>{len(items)}</InventorySerial>")
        return xml.replace("</SceneObjectPart>", "<TaskInventory>" + "".join(items) + "</TaskInventory></SceneObjectPart>")

    groups = []
    # the Spammer: twelve prims in a row, each with the script (only the root listens)
    parts = []
    for i in range(12):
        pid = uid("spammer", i)
        te = forest.te_bytes(mf.PLYWOOD, color=(0.8, 0.2, 0.2, 1))
        items = [audio.task_item(uid("spammer item", i), uid("spammer asset"), "spammer", 10, pid, owner)]
        if i == 0:
            items.append(audio.task_item(uid("tone item"), uid("asset", "tone_a"), "tone_a", 1, pid, owner))
        xml = forest.part(pid, 7100 + i, "Safety Spammer", SPAMMER, (0, 0, 0, 1), (0.25, 0.25, 0.25), te,
                          link=i + 1, offset=(0, 0, 0) if i == 0 else (0.3 * i, 0, 0), root_pos=SPAMMER)
        parts.append(with_items(xml, pid, items))
    groups.append(("Safety_Spammer", uid("spammer", 0), forest.group(parts)))
    # the Lamp: a looped sound and particles
    pid = uid("lamp")
    xml = forest.part(pid, 7200, "Safety Lamp", LAMP, (0, 0, 0, 1), (0.3, 0.3, 0.3), forest.te_bytes(mf.PLYWOOD, color=(0.95, 0.8, 0.3, 1)))
    xml = with_items(xml, pid, [audio.task_item(uid("lamp item"), uid("lamp asset"), "lamp", 10, pid, owner),
                                audio.task_item(uid("beep item"), uid("asset", "beep"), "beep", 1, pid, owner)])
    groups.append(("Safety_Lamp", pid, forest.group([xml])))
    # the Seat: not phantom, so it can be sat on
    pid = uid("seat")
    xml = forest.part(pid, 7300, "Safety Seat", SEAT, (0, 0, 0, 1), (0.6, 0.6, 0.3), forest.te_bytes(mf.PLYWOOD, color=(0.3, 0.5, 0.8, 1)))
    xml = with_items(xml, pid, [audio.task_item(uid("seat item"), uid("seat asset"), "seat", 10, pid, owner)]).replace("<Flags>Phantom</Flags>", "<Flags>None</Flags>")
    groups.append(("Safety_Seat", pid, forest.group([xml])))
    with tarfile.open(out, "w:gz") as tar:
        forest.add(tar, "archive.xml", forest.archive_xml())
        for name, pid, g in groups:
            forest.add(tar, f"objects/{name}__{pid}.xml", g.encode())
        forest.add(tar, f"assets/{uid('spammer asset')}_script.lsl", SPAMMER_SCRIPT.encode())
        forest.add(tar, f"assets/{uid('lamp asset')}_script.lsl", LAMP_SCRIPT.encode())
        forest.add(tar, f"assets/{uid('seat asset')}_script.lsl", SEAT_SCRIPT.encode())
        for n in ("tone_a", "beep"):
            forest.add(tar, f"assets/{uid('asset', n)}_sound.ogg", (audio.ASSETS / f"{n}.ogg").read_bytes())
    return out


def in_world():
    import sqlite3
    import grid
    c = sqlite3.connect(f"file:{grid.BIN / 'OpenSim.db'}?mode=ro", uri=True)
    try:
        q = f"select Name, Description from prims where Name in ({','.join('?' * len(NAMES))})"
        return c.execute(q, NAMES).fetchall()
    finally:
        c.close()


def fixture():
    import grid
    import media_fixtures as mf
    if not grid.start():
        print("the grid didn't start")
        return 1
    for first, last in (("Sojourn", "Friend"), ("Sojourn", "Safety")):
        print(grid.account(first, last))
    owner = mf.accounts()["Sojourn Friend"]
    have = in_world()
    if have and all(h[1] == TAG for h in have) and {h[0] for h in have} == set(NAMES):
        return 0
    grid.console('change region "Atelier"', wait=1)
    for n in NAMES:
        if any(h[0] == n for h in have):
            grid.console(f'delete object name "{n}"', wait=2, answers=("y",))
    path = build_oar(owner).resolve()
    print("\n".join(grid.console(f'load oar --merge "{path}"', wait=6)[-2:]))
    end = time.time() + 60
    while time.time() < end and {h[0] for h in in_world()} != set(NAMES):
        grid.console("backup", wait=3)
        time.sleep(2)
    return 0 if {h[0] for h in in_world()} == set(NAMES) else 1


# --- the client --------------------------------------------------------------------------------------------------

PORT = DESK_PORT


def http(path, body=None, timeout=30):
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}{path}", data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read())


def api(pump, op, timeout=30, **args):
    out = http("/cmd", {"pump": pump, "data": {"op": op, **args}, "timeout": timeout}, timeout + 20)
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(f"{pump}.{op}: {out['error']}")
    return out


def safety(op, **args):
    return api("SojournSafety", op, **args)


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


def events(since):
    return http(f"/events?since={since}&wait=0") or []


def last_seq():
    return http("/status", timeout=5).get("seq", 0)


def tp(x, y):
    for i in range(4):
        try:
            return api("LLAgent", "requestTeleport", regionname="Atelier", x=x, y=y, z=24, skip_confirmation=True, timeout=40)
        except RuntimeError as e:
            if i == 3:
                raise
            print(f"     (teleport again: {e})")
            time.sleep(4)


def say7(msg):
    api("LLChatBar", "sendChat", message=msg, channel=7)


def nearby(name, dist=40):
    r = api("TesselObjects", "nearby", dist=dist, query=name, timeout=6)
    return [o for o in r.get("objects", []) if o.get("name") == name]


def where():
    return safety("status")["position"]


def start_client(base, vr, minutes, env_extra=None):
    for _ in range(10):
        shutil.rmtree(base, ignore_errors=True)
        if not base.exists():
            break
        time.sleep(1)
    base.mkdir(parents=True, exist_ok=True)
    # the features under test on (both off by default)
    (base / "vr-settings.json").write_text(json.dumps({"confirmTouches": False, "panelHidden": True, "audioVolume": 0,
                                                       "antispam": True, "comfortVignette": "forced"}))
    env = dict(os.environ, **(env_extra or {}))
    args = ["--no-assistant", "--no-voice", "--first", "Sojourn", "--last", "Safety", "--password", "sojourn-local",
            "--bridge-port", str(PORT), "--base-dir", str(base), "--start", f"uri:Atelier&{STAND[0]:.0f}&{STAND[1]:.0f}&24",
            "--minutes", str(minutes), "--sky", "noon"]
    if vr:
        sims = sorted((ROOT / "local" / "xrsim").glob("*/meta_openxr_simulator.json"))
        if not sims:
            sys.exit("no Meta XR Simulator in local/xrsim (docs/vr.md)")
        env["XR_RUNTIME_JSON"] = str(sims[-1])
        args = ["--vr", "--xr-wait", "90", "--shadow-cap", "1536", *args]
    else:
        args = ["--window", "960x540", "--fps", "30", *args]
    dotnet = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
    dll = Path(os.environ.get("SOJOURN_DLL") or ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "Sojourn.dll")
    log = open(base / "console.txt", "w", encoding="utf-8")
    return subprocess.Popen([str(dotnet), str(dll), *args], cwd=ROOT, env=env, stdin=subprocess.DEVNULL, stdout=log,
                            stderr=subprocess.STDOUT, text=True, creationflags=0x00004000)


def logged_in(secs):
    def up():
        return http("/status", timeout=3).get("connected")
    return wait(up, secs, 1)


class Checks:
    def __init__(self):
        self.ok = True

    def __call__(self, name, cond, detail=""):
        self.ok &= bool(cond)
        print(f"{'ok  ' if cond else 'FAIL'} {name}: {detail}", flush=True)
        return cond


def chats(since, prefix):
    return [e for e in events(since) if e.get("kind") == "LLChat" and str((e.get("data") or {}).get("message", "")).startswith(prefix)]


def notices(since, name):
    return [e for e in events(since) if e.get("kind") == "ClaudeNotify" and (e.get("data") or {}).get("name") == name]


def ims(since, prefix):
    out = []
    for e in events(since):
        d = e.get("data") or {}
        if e.get("kind") == "TesselEvents" and d.get("event") == "object_im" and str(d.get("message", "")).startswith(prefix):
            out.append(e)
    return out


def flat(a, b):
    return ((a[0] - b[0]) ** 2 + (a[1] - b[1]) ** 2) ** 0.5


def test():
    if fixture():
        print("the Safety Rig isn't in the world")
        return 1
    base = BASE / "desk"
    proc = start_client(base, vr=False, minutes=12)
    ck = Checks()
    try:
        if not logged_in(150):
            print("FAIL: didn't log in")
            return 1
        lamp = wait(lambda: nearby("Safety Lamp"), 40)
        spam = wait(lambda: nearby("Safety Spammer"), 20)
        seat = wait(lambda: nearby("Safety Seat"), 20)
        if not ck("the rig is around", lamp and spam and seat, f"{len(lamp or [])} lamp, {len(spam or [])} spammer, {len(seat or [])} seat"):
            return 1
        lamp_id, seat_id = lamp[0]["id"], seat[0]["id"]

        def lamp_drawn():
            return sum(x["drawn"] for x in safety("particles")["sources"] if x["root"] == lamp_id)
        say7("lamp on")
        time.sleep(3)

        # particles: the lamp's, whose they are, off, a blocked owner's
        p = wait(lambda: (lambda r: r if any(s["root"] == lamp_id and s["drawn"] > 0 for s in r["sources"]) else None)(safety("particles")), 20)
        ck("the lamp's particles are listed and drawn", p, f"sources {p and [(s.get('name'), s['owner_name'], s['drawn']) for s in p['sources']]}")
        api("SojournSettings", "set", particles="off")
        time.sleep(1.5)
        ck("particles off: none drawn", safety("particles")["drawn"] == 0, str(safety("particles")["drawn"]))
        api("SojournSettings", "set", particles="on")
        ck("particles on again", wait(lambda: safety("particles")["drawn"] > 0, 10), "")

        # derender the lamp, and back; until a teleport; for good
        r = safety("derender", id=lamp_id)
        ck("derender", r.get("until") == "teleport", json.dumps(r))
        time.sleep(1.5)
        ck("derendered: gone from the world", not nearby("Safety Lamp"), "")
        ck("derendered: no longer a particle source", not any(x["root"] == lamp_id for x in safety("particles")["sources"]), "")
        ck("listed as hidden", any(o["id"] == lamp_id for o in safety("hidden")["objects"]), "")
        safety("rerender", id=lamp_id)
        ck("rerendered: back (asked of the region)", wait(lambda: nearby("Safety Lamp"), 15), "")
        safety("derender", id=lamp_id)
        time.sleep(1)
        tp(STAND[0] + 6, STAND[1])
        ck("a teleport ends a derender until teleport", wait(lambda: nearby("Safety Lamp"), 20), "")
        tp(STAND[0], STAND[1])
        r = safety("derender", id=lamp_id, permanent=True)
        saved = BASE / "desk"
        bl = next(saved.rglob("asset-blacklist.json"), None)
        ck("derender for good: kept with the account", r.get("until") == "always" and bl and lamp_id in bl.read_text(), str(bl))
        safety("rerender", all=True)
        ck("show all again: back and off the file", wait(lambda: nearby("Safety Lamp"), 15) and lamp_id not in bl.read_text(), "")

        # antispam, kind by kind (lifted between)
        seq = last_seq()
        say7("chat")
        time.sleep(4)
        n = len(chats(seq, "spam chat"))
        ck("antispam: chat held to 10", n == 10, f"{n} of 15 reached the client")
        ck("antispam: the feed told once", len(notices(seq, "AntiSpamBlocked")) == 1, str([e["data"]["message"] for e in notices(seq, "AntiSpamBlocked")]))
        ck("antispam: listed", any(b["kind"] == "chat" for b in safety("spam")["blocked"]), json.dumps(safety("spam")["blocked"]))
        safety("unspam", all=True)
        seq = last_seq()
        say7("lines")
        time.sleep(3)
        ck("antispam: a message of 80 lines held back", not chats(seq, "a long one") and notices(seq, "AntiSpamBlocked"), "")
        safety("unspam", all=True)
        seq = last_seq()
        say7("im")
        time.sleep(8)
        n = len(ims(seq, "spam im"))
        ck("antispam: IMs from one owner's twelve prims held to 10", n == 10, f"{n} of 12")
        safety("unspam", all=True)
        seq = last_seq()
        say7("dialog")
        time.sleep(8)
        menus = [e for e in notices(seq, "ScriptDialog")]
        ck("antispam: menus from one owner held to 10", len(menus) == 10, f"{len(menus)} of 12")
        for m in api("LLNotifications", "listChannelNotifications", channel="").get("notifications", []):
            if m.get("name") == "ScriptDialog":
                try:
                    api("LLNotifications", "cancel", uuid=m["id"])
                except RuntimeError:
                    pass
        safety("unspam", all=True)
        seq = last_seq()
        say7("sound")
        time.sleep(5)
        heard = [s for s in safety("sounds", seconds=20)["sounds"] if s["owner_name"] and s["how"] == "rezzed" and s["object"] == spam[0]["id"]]
        times = sum(s["times"] for s in heard)
        ck("antispam: sounds held to 60", times == 60 and any(e["data"]["payload"].get("queue") == "sound" for e in notices(seq, "AntiSpamBlocked")),
           f"{times} of 70 played")
        safety("unspam", all=True)

        # sounds: the lamp's loop listed, blocked (and on the list), unblocked
        loop = wait(lambda: [s for s in safety("sounds")["sounds"] if s["object"] == lamp_id and s.get("playing") == "loop"], 10)
        ck("the lamp's loop is among the sounds heard", loop, json.dumps(loop and loop[0]))
        if loop:
            snd = loop[0]["sound"]
            safety("blockSound", sound=snd)
            ck("blocked: the loop stops", wait(lambda: not [x for x in safety("sounds")["sounds"] if x["sound"] == snd and x.get("playing")], 5), "")
            ck("and is on the list", any(s["id"] == snd for s in safety("hidden")["sounds"]), "")
            safety("unblock", id=snd)
            ck("unblocked", not any(s["id"] == snd for s in safety("hidden")["sounds"]), "")

        # keep me in place: a push moves you; with it on, you come back
        here = where()
        say7("push 700")
        moved = wait(lambda: (lambda p: p if flat(p, here) > 0.6 else None)(where()), 6, 0.2)
        ck("a push moves the avatar (keep me in place off)", moved, f"from {here} to {moved}")
        time.sleep(4)
        tp(STAND[0], STAND[1])
        time.sleep(2)
        api("SojournSettings", "set", keep_in_place="on")
        time.sleep(3)
        here = where()
        say7("push 700")
        moved = wait(lambda: (lambda p: p if flat(p, here) > 0.6 else None)(where()), 6, 0.2)
        back = wait(lambda: (lambda p: p if flat(p, here) < 0.6 else None)(where()), 15, 0.3) if moved else None
        ck("keep me in place: pushed, then back", moved and back, f"{here} -> {moved} -> {back}")
        # a gentler push: walked back rather than teleported
        time.sleep(3)
        here = where()
        say7("push 120")
        moved = wait(lambda: (lambda p: p if flat(p, here) > 0.6 else None)(where()), 6, 0.2)
        far = moved
        for _ in range(10):
            p = where()
            far = p if flat(p, here) > flat(far or here, here) else far
            time.sleep(0.2)
        back = wait(lambda: (lambda p: p if flat(p, here) < 0.6 else None)(where()), 20, 0.3) if moved else None
        log = " | ".join(l for f in (BASE / "desk" / "logs").glob("*.log") for l in f.read_text(encoding="utf-8", errors="replace").splitlines() if "keep in place" in l)
        ck("keep me in place: a small push walked back", moved and back and "walking back" in log, f"{here} -> {far} -> {back}; {log[-160:]}")
        api("SojournSettings", "set", keep_in_place="off")

        # the seat: controls taken, the dance blacklisted, release keys
        seq = last_seq()
        api("LLAgent", "requestSit", obj_uuid=seat_id, timeout=20)
        ck("the seat took the controls", wait(lambda: chats(seq, "seat: controls taken"), 10), "")
        st = wait(lambda: (lambda s: s if "MouseLookLeftButton" in s["controls_taken"] else None)(safety("status")), 5)
        ck("status shows them", st, json.dumps(st and st["controls_taken"]))
        ck("dancing", wait(lambda: DANCE in json.dumps(api("LLAgent", "animations")), 8), "")
        safety("blockAnimation", id=DANCE, name="dance1")
        ck("the dance blacklisted: stopped", wait(lambda: DANCE not in json.dumps(api("LLAgent", "animations")), 8), "")
        safety("unblock", id=DANCE)
        r = safety("releaseKeys")
        ck("release keys: released what was held", "Forward" in r["released"], json.dumps(r))
        ck("and the region let go", wait(lambda: not safety("status")["controls_taken"], 8), json.dumps(safety("status")["controls_taken"]))
        api("LLAgent", "requestStand")

        # a blocked particle owner's particles go (the viewer's Block particle owner), and come back unblocked
        r = safety("blockParticleOwner", id=lamp_id)
        ck("block particle owner", r.get("name"), json.dumps(r))
        ck("their particles stop drawing", wait(lambda: lamp_drawn() == 0, 5), str(lamp_drawn()))
        api("TesselAgent", "unmute", id=r["owner"])
        ck("unblocked: they come back", wait(lambda: lamp_drawn() > 0, 8), str(lamp_drawn()))
        return 0 if ck.ok else 1
    finally:
        try:
            say7("lamp off")
            api("LLAppViewer", "requestQuit", timeout=5)
        except Exception:
            pass
        try:
            proc.wait(30)
        except subprocess.TimeoutExpired:
            proc.kill()


def headset():
    global PORT
    PORT = VR_PORT
    if fixture():
        print("the Safety Rig isn't in the world")
        return 1
    base = BASE / "vr"
    # the trigger pulled a few times late on (by then the test has seated the avatar)
    motion = "wait 50; " + "; ".join(["rtrig 1 0.4; rtrig 0 2.6"] * 30) + "; wait 30"
    proc = start_client(base, vr=True, minutes=7, env_extra={"SOJOURN_VR_SCRIPT": "motion", "SOJOURN_MOTION": motion})
    ck = Checks()

    def shot():
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winkey.ps1"), "-ProcessId", str(proc.pid), "-Vk", "123"],
                       capture_output=True)

    try:
        if not logged_in(180):
            print("FAIL: didn't log in")
            return 1
        seat = wait(lambda: nearby("Safety Seat"), 40)
        time.sleep(8)
        shot()
        # keep me in place first (no teleports of the test's own anywhere: they collided with the rig's last time):
        # pushed hard, the view holds still (no vignette) and the avatar is brought back to it
        api("SojournSettings", "set", keep_in_place="on")
        time.sleep(3)
        here = where()
        peak = 0.0
        say7("push 700")
        moved = None
        for _ in range(30):
            peak = max(peak, safety("status").get("vignette", 0))
            p = where()
            if flat(p, here) > 0.6:
                moved = p
            time.sleep(0.1)
        back = wait(lambda: (lambda p: p if flat(p, here) < 1.0 else None)(where()), 30, 0.3)
        ck("keep me in place in the headset: pushed, no vignette, back", moved and back and peak < 0.1, f"{here} -> {moved} -> {back}, peak {peak}")
        api("SojournSettings", "set", keep_in_place="off")
        time.sleep(3)
        # without it, a push carries the view a moment (server movement) before the walk to the head takes over
        peak = 0.0
        say7("push 300")
        for _ in range(20):
            peak = max(peak, safety("status").get("vignette", 0))
            time.sleep(0.1)
        ck("pushed: the view carried a moment, the vignette with it", peak > 0.1, f"peak {peak}")
        time.sleep(8)
        # aiming on the seat
        seq = last_seq()
        api("LLAgent", "requestSit", obj_uuid=seat[0]["id"], timeout=20)
        ck("seated, aiming", wait(lambda: safety("status").get("aiming"), 10), json.dumps(safety("status")))
        ck("the region hears mouselook", wait(lambda: chats(seq, "seat: mouselook on"), 10), "")
        ml = wait(lambda: chats(seq, "seat: ml down") and chats(seq, "seat: ml up"), 90, 1)
        ck("the right trigger is the mouselook button", ml, f"{len(chats(seq, 'seat: ml down'))} down, {len(chats(seq, 'seat: ml up'))} up")
        # the seat rides round a square: the vignette while it carries you, none with it off
        tp_peak, shotted, seen = 0.0, False, []
        say7("ride")
        for i in range(80):
            v = safety("status").get("vignette", 0)
            tp_peak = max(tp_peak, v)
            if v > 0.9 and not shotted:
                shot()
                shotted = True
            if i % 10 == 0:
                seen.append(where())
            time.sleep(0.1)
        ck("riding: the seat carried the avatar", max(flat(p, seen[0]) for p in seen) > 2, json.dumps(seen))
        ck("riding: the vignette (a mirror shot taken at its height)", tp_peak > 0.3 and shotted, f"peak {tp_peak}")
        time.sleep(3)
        log = (base / "console.txt").read_text(encoding="utf-8", errors="replace")
        ck("the console says when it comes and goes", "comfort vignette: on" in log and "comfort vignette: off" in log, "")
        api("SojournSettings", "set", comfort_vignette="off")
        off_peak = 0.0
        say7("ride")
        for _ in range(80):
            off_peak = max(off_peak, safety("status").get("vignette", 0))
            time.sleep(0.1)
        ck("riding with the vignette off: none", off_peak == 0, f"peak {off_peak}")
        api("SojournSettings", "set", comfort_vignette="forced")
        api("LLAgent", "requestStand")
        ck("standing, not aiming", wait(lambda: not safety("status").get("aiming"), 8), "")
        return 0 if ck.ok else 1
    finally:
        try:
            api("LLAppViewer", "requestQuit", timeout=5)
        except Exception:
            pass
        try:
            proc.wait(40)
        except subprocess.TimeoutExpired:
            proc.kill()


if __name__ == "__main__":
    what = sys.argv[1] if len(sys.argv) > 1 else "test"
    sys.exit({"fixture": fixture, "test": test, "headset": headset}[what]())
