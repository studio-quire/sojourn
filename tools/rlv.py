"""RLV/RLVa on the local grid (python tools/rlv.py ...): docs/rlv.md.

  python tools/rlv.py fixture   the RLV Rig and the RLV Cuff in Atelier at 60-64, 40, owned by Sojourn Rlv (an OAR merged in):
                                both relay their owner's "rig do @..." / "cuff do @..." lines on channel 7 as llOwnerSay, so the
                                tests speak as an object; the Rig also tells its owner what it hears on channel 0 (the owner's own
                                chat) and on the reply channels 1001-1006 ("heard <ch>: <text>"), and says or emotes on cue
  python tools/rlv.py test      a console host (Sojourn.SessionProbe, no GPU) as Sojourn Rlv on :9768 with RLV on: version
                                replies, @getstatus and @clear, @notify, teleports (@tploc, @tplm, @tplocal), sitting
                                (@sit, @unsit, @sit:<id>=force, @unsit=force, @getsitid), chat (@sendchat, @redirchat,
                                @chatshout, @sendchannel, @recvchat, @recvemote), @shownames on what's heard, the gate's
                                words for the assistant (SojournRlv status), and the #RLV folder: @getinv, @attach:<folder>=force,
                                @getattach, @getinvworn, @getpath, @detach=n holding the Cuff on against the agent API,
                                @remattach and @detach:<folder>=force, the Cuff's restrictions going with it when it comes off
  python tools/rlv.py eyes      the VR client on the desktop in stereo as Sojourn Rlv (:9769), both eyes through the headset's
                                per-eye path: a vision sphere (@setsphere) darkening the far half of each eye, and @clear; its
                                pictures in local/rlv/eyes; takes local/gpu.lock for its minute

Written by Claude Opus 5.5, 2026-11-01.
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

BASE = ROOT / "local" / "rlv"
PORT = 9768
VR_PORT = 9769
TAG = "tools/rlv.py 1"
STAND = (62.0, 36.0)
RIG = (60.0, 40.0, 24.5)
CUFF = (64.0, 40.0, 24.5)
NAMES = ("RLV Rig", "RLV Cuff")
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
BELOW_NORMAL = 0x00004000

SCRIPT = """// RLV fixture (tools/rlv.py): its owner's "<rig|cuff> do <line>" on channel 7 becomes llOwnerSay(line), so a test can
// speak as an object; the Rig tells its owner what it hears on channel 0 from them and on the reply channels
string who()
{
    if (llSubStringIndex(llGetObjectName(), "Rig") >= 0) return "rig";
    return "cuff";
}
start()
{
    llListen(7, "", llGetOwner(), "");
    if (who() == "rig")
    {
        llListen(0, "", llGetOwner(), "");
        integer i;
        for (i = 1001; i <= 1006; i++) llListen(i, "", NULL_KEY, "");
    }
}
default
{
    state_entry() { llSitTarget(<0, 0, 0.6>, ZERO_ROTATION); start(); }
    on_rez(integer p) { llResetScript(); }
    attach(key id) { if (id != NULL_KEY) llResetScript(); }
    listen(integer ch, string name, key id, string msg)
    {
        if (ch == 7)
        {
            string me = who();
            if (llSubStringIndex(msg, me + " ") != 0) return;
            string cmd = llGetSubString(msg, llStringLength(me) + 1, -1);
            if (llSubStringIndex(cmd, "do ") == 0) llOwnerSay(llGetSubString(cmd, 3, -1));
            else if (cmd == "say") llSay(0, "public words from the rig");
            else if (cmd == "emote") llSay(0, "/me waves at everyone");
            return;
        }
        llOwnerSay("heard " + (string)ch + ": " + msg);
    }
}
"""


def uid(*parts):
    import uuid
    return str(uuid.uuid5(uuid.UUID("3f6b2a71-9c0e-4d58-a1b7-6e2c8d4f9a10"), "/".join(map(str, (TAG,) + parts))))


def build_oar(owner):
    import tarfile
    import forest
    import audio
    import media_fixtures as mf
    BASE.mkdir(parents=True, exist_ok=True)
    out = BASE / "rlv-rig.oar"
    groups = []
    for name, pos, color in ((NAMES[0], RIG, (0.6, 0.2, 0.7, 1)), (NAMES[1], CUFF, (0.2, 0.2, 0.2, 1))):
        pid = uid(name)
        xml = forest.part(pid, 7600 + len(groups), name, pos, (0, 0, 0, 1), (0.5, 0.5, 0.3), forest.te_bytes(mf.PLYWOOD, color=color))
        xml = xml.replace(forest.OWNER, owner).replace("<Description />", f"<Description>{TAG}</Description>")
        xml = xml.replace("<InventorySerial>0</InventorySerial>", "<InventorySerial>1</InventorySerial>")
        xml = xml.replace("<Flags>Phantom</Flags>", "<Flags>None</Flags>")
        xml = xml.replace("</SceneObjectPart>", "<TaskInventory>" + audio.task_item(uid(name, "item"), uid("script"), "rlv fixture", 10, pid, owner)
                          + "</TaskInventory></SceneObjectPart>")
        groups.append((name.replace(" ", "_"), pid, forest.group([xml])))
    with tarfile.open(out, "w:gz") as tar:
        forest.add(tar, "archive.xml", forest.archive_xml())
        for name, pid, g in groups:
            forest.add(tar, f"objects/{name}__{pid}.xml", g.encode())
        forest.add(tar, f"assets/{uid('script')}_script.lsl", SCRIPT.encode())
    return out


def in_world():
    import sqlite3
    import grid
    c = sqlite3.connect(f"file:{grid.BIN / 'OpenSim.db'}?mode=ro", uri=True)
    try:
        return c.execute(f"select Name, Description from prims where Name in ({','.join('?' * len(NAMES))})", NAMES).fetchall()
    finally:
        c.close()


def fixture():
    import grid
    import media_fixtures as mf
    if not grid.start():
        print("the grid didn't start")
        return 1
    print(grid.account("Sojourn", "Rlv"))
    owner = mf.accounts()["Sojourn Rlv"]
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


# --- the client ------------------------------------------------------------------------------------------------------

def http(path, body=None, timeout=30, port=None):
    req = urllib.request.Request(f"http://127.0.0.1:{port or PORT}{path}", data=json.dumps(body).encode() if body is not None else None,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read())


def call(pump, op, timeout=30, **args):
    """An op: its result, or {"error": ...} (not raised: refusals are what's tested)."""
    out = http("/cmd", {"pump": pump, "data": {"op": op, **args}, "timeout": timeout}, timeout + 20)
    return out if isinstance(out, dict) else {"result": out}


def api(pump, op, timeout=30, **args):
    out = call(pump, op, timeout, **args)
    if out.get("error"):
        raise RuntimeError(f"{pump}.{op}: {out['error']}")
    return out


def events(since):
    return http(f"/events?since={since}&wait=0") or []


def last_seq():
    return http("/status", timeout=5).get("seq", 0)


def wait(f, secs, step=0.3):
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


class Checks:
    def __init__(self):
        self.ok = True

    def __call__(self, name, cond, detail=""):
        self.ok &= bool(cond)
        print(f"{'ok  ' if cond else 'FAIL'} {name}: {detail}", flush=True)
        return cond


def chat_lines(since):
    return [(e.get("data") or {}) for e in events(since) if e.get("kind") == "LLChat"]


def heard(since, prefix, secs=6, count=1):
    """The Rig's "heard <ch>: ..." lines (owner-says) since a point."""
    def look():
        return [d["message"] for d in chat_lines(since) if str(d.get("message", "")).startswith(prefix)]
    return wait(lambda: (lambda got: got if len(got) >= count else None)(look()), secs) or look()


def say7(line):
    api("LLChatBar", "sendChat", message=line, channel=7)


def rig(cmd):
    say7("rig do " + cmd)


def cuff(cmd):
    say7("cuff do " + cmd)


def nearby(name, dist=40):
    r = api("TesselObjects", "nearby", dist=dist, query=name, timeout=6)
    return [o for o in r.get("objects", []) if o.get("name") == name]


def start_probe(base, minutes):
    for _ in range(10):
        shutil.rmtree(base, ignore_errors=True)
        if not base.exists():
            break
        time.sleep(1)
    base.mkdir(parents=True, exist_ok=True)
    (base / "vr-settings.json").write_text(json.dumps({"rlv": True, "confirmTouches": False}))
    # SOJOURN_PROBE_DLL: a console host built elsewhere (a worktree, while the tree has someone's half-done edit)
    dll = Path(os.environ.get("SOJOURN_PROBE_DLL") or ROOT / "src" / "Sojourn.SessionProbe" / "bin" / "Release" / "net10.0" / "Sojourn.SessionProbe.dll")
    if not os.environ.get("SOJOURN_PROBE_DLL"):
        project = ROOT / "src" / "Sojourn.SessionProbe" / "Sojourn.SessionProbe.csproj"
        if subprocess.run([str(DOTNET), "build", str(project), "-c", "Release", "-m:2", "-v:q", "-nologo", "-clp:ErrorsOnly", "-nodeReuse:false"],
                          creationflags=BELOW_NORMAL).returncode != 0:
            sys.exit("build failed")
    log = open(base / "console.txt", "w", encoding="utf-8", errors="replace")
    args = [str(DOTNET), str(dll), "--first", "Sojourn", "--last", "Rlv", "--port", str(PORT),
            "--start", f"uri:Atelier&{STAND[0]:.0f}&{STAND[1]:.0f}&24", "--base", str(base), "--seconds", str(minutes * 60),
            # (a line no one says: the host stays up until --seconds, as the test needs, rather than leaving once its checks pass)
            "--expect", "the rlv test keeps the host up"]
    return subprocess.Popen(args, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, creationflags=BELOW_NORMAL)


def folder(parent, name):
    out = api("LLInventory", "getDirectDescendants", folder_id=parent, timeout=20)
    for cid, c in (out.get("categories") or {}).items():
        if c.get("name") == name:
            return cid
    return api("TesselInventory", "createFolder", name=name, parent_id=parent)["folder_id"]


def items(folder_id):
    return [dict(v, id=k) for k, v in (api("LLInventory", "getDirectDescendants", folder_id=folder_id, timeout=20).get("items") or {}).items()]


def test():
    if fixture():
        print("fixture failed")
        return 1
    import grid
    if not grid.ready():
        print("the grid isn't up")
        return 1
    proc = start_probe(BASE / "probe", 8)
    ok = Checks()
    try:
        if not wait(lambda: http("/status", timeout=3).get("connected"), 120, 1):
            ok("logged in", False, "no")
            return 1
        time.sleep(4)
        st = api("SojournRlv", "status")
        ok("RLV on for this login", st.get("on") is True, json.dumps(st)[:120])
        rigs = nearby(NAMES[0])
        cuffs = nearby(NAMES[1])
        if not ok("fixtures in view", rigs and cuffs, f"{len(rigs)} rig, {len(cuffs)} cuff"):
            return 1
        rig_id, cuff_id = rigs[0]["id"], cuffs[0]["id"]

        # --- versions and replies ---------------------------------------------------------------------------------
        s = last_seq()
        rig("@version=1001")
        got = heard(s, "heard 1001:")
        ok("@version", got and got[0] == "heard 1001: RestrainedLife viewer v3.4.3 (RLVa 2.4.2)", got[:1])
        s = last_seq()
        rig("@versionnew=1001,versionnum=1002")
        got = heard(s, "heard 100", 8, count=2)
        ok("@versionnew, @versionnum", "heard 1001: RestrainedLove viewer v3.4.3 (RLVa 2.4.2)" in got and "heard 1002: 3040300" in got, got)
        s = last_seq()
        rig("@tploc=n,tplm=n,notify:1003;sat=add,fly=n")
        rig("@getstatus=1001")
        got = heard(s, "heard 1001:")
        ok("@getstatus", got and got[0] == "heard 1001: /tploc/tplm/notify:1003;sat/fly", got[:1])
        ok("commands kept out of chat", not [d for d in chat_lines(s) if str(d.get("message", "")).startswith("@")], "")

        # --- teleports ----------------------------------------------------------------------------------------------
        r = call("TesselAgent", "goHome")
        ok("@tploc refuses home, saying why", "@tploc" in str(r.get("error", "")) and "RLV Rig" in str(r.get("error", "")), r.get("error"))
        r = call("LLAgent", "requestTeleport", regionname="Workshop", x=128, y=128, z=30, skip_confirmation=True, timeout=20)
        ok("@tploc refuses another region", "@tploc" in str(r.get("error", "")), r.get("error"))
        r = call("LLAgent", "requestTeleport", regionname="Atelier", x=STAND[0] + 1, y=STAND[1] + 1, z=24, skip_confirmation=True, timeout=30)
        ok("a local teleport still goes", not r.get("error"), r.get("error") or r)
        rig("@tplocal:0.5=n")
        time.sleep(1)
        r = call("LLAgent", "requestTeleport", regionname="Atelier", x=STAND[0] + 6, y=STAND[1], z=24, skip_confirmation=True, timeout=20)
        ok("@tplocal:0.5 refuses 6 m", "@tplocal" in str(r.get("error", "")), r.get("error"))
        st = api("SojournRlv", "status")
        ok("SojournRlv status names the holder", any(h.get("name") == "RLV Rig" for h in st.get("held", [])) and "@tploc" in str(st["can"]["teleport"]),
           str(st.get("can", {}).get("teleport")))
        rig("@clear=tp")
        time.sleep(1)

        # --- sitting ------------------------------------------------------------------------------------------------
        s = last_seq()
        rig(f"@sit:{rig_id}=force")
        time.sleep(4)
        rig("@getsitid=1001")
        got = heard(s, "heard 1001:")
        ok("@sit:<id>=force seats, @getsitid answers", got and got[-1] == f"heard 1001: {rig_id}", got[-1:])
        got = heard(s, "heard 1003:")
        ok("@notify hears the sit", any(f"sat object legally {rig_id}" in g for g in got), got)
        rig("@unsit=n")
        time.sleep(1)
        r = call("LLAgent", "requestStand")
        ok("@unsit refuses standing", "@unsit" in str(r.get("error", "")), r.get("error"))
        rig("@unsit=y,sit=n")
        time.sleep(1)
        s = last_seq()
        rig("@unsit=force")
        time.sleep(2)
        rig("@getsitid=1001")
        got = heard(s, "heard 1001:")
        ok("@unsit=force stands", got and got[-1] == "heard 1001: 00000000-0000-0000-0000-000000000000", got[-1:])
        r = call("LLAgent", "requestSit", obj_uuid=rig_id, timeout=10)
        ok("@sit refuses sitting", "@sit" in str(r.get("error", "")), r.get("error"))
        rig("@sit=y")

        # --- chat out -------------------------------------------------------------------------------------------------
        s = last_seq()
        rig("@sendchat=n")
        time.sleep(0.5)
        api("LLChatBar", "sendChat", message="hello there")
        got = heard(s, "heard 0:")
        ok("@sendchat turns chat to ...", got and got[-1] == "heard 0: ...", got)
        s = last_seq()
        api("LLChatBar", "sendChat", message="((out of character))")
        got = heard(s, "heard 0:")
        ok("OOC still goes", got and got[-1] == "heard 0: ((out of character))", got)
        s = last_seq()
        rig("@redirchat:1004=n")
        time.sleep(0.5)
        api("LLChatBar", "sendChat", message="a secret")
        got = heard(s, "heard 100")
        ok("@redirchat sends it to its channel", "heard 1004: a secret" in got and not heard(s, "heard 0:", 1), got)
        rig("@clear=chat")
        rig("@redirchat:1004=y")
        s = last_seq()
        rig("@sendchannel=n,sendchannel:1005=add,sendchannel:7=add")
        time.sleep(0.5)
        api("LLChatBar", "sendChat", message="to six", channel=1006)
        api("LLChatBar", "sendChat", message="to five", channel=1005)
        got = heard(s, "heard 100", 4)
        ok("@sendchannel blocks all but its exception", "heard 1005: to five" in got and not any(g.startswith("heard 1006") for g in got), got)
        rig("@clear=sendchannel")

        # --- chat in ------------------------------------------------------------------------------------------------
        rig("@recvchat=n,recvemote=n")
        time.sleep(0.5)
        s = last_seq()
        say7("rig say")
        say7("rig emote")
        time.sleep(3)
        lines = [d.get("message") for d in chat_lines(s) if d.get("from") == "RLV Rig"]
        ok("@recvchat and @recvemote hide what's said", "..." in lines and " ..." in lines, lines)
        rig("@clear=recv")

        # --- the #RLV folder ----------------------------------------------------------------------------------------
        root = api("LLInventory", "getBasicFolderID", ft_name="root_inv")["id"]
        rlv = folder(root, "#RLV")
        cuffs_folder = folder(rlv, "cuffs")
        have = [i for i in items(cuffs_folder) if "RLV Cuff" in i.get("name", "")]
        if not have:
            # a copy taken lands in Objects (OpenSim doesn't take a folder for a copy): moved into #RLV/cuffs, named for its point
            objects = api("LLInventory", "getBasicFolderID", ft_name="object")["id"]
            found = [i for i in items(objects) if i.get("name") == "RLV Cuff"]
            if not found:
                api("TesselBuild", "take", ids=[cuff_id], copy=True, timeout=30)
                found = wait(lambda: [i for i in items(objects) if i.get("name") == "RLV Cuff"], 20, 1) or []
            if found:
                api("TesselInventory", "move", id=found[0]["id"], folder_id=cuffs_folder)
                api("TesselInventory", "update", id=found[0]["id"], name="RLV Cuff (left hand)")
            have = wait(lambda: [i for i in items(cuffs_folder) if "RLV Cuff" in i.get("name", "")], 10, 1) or []
        ok("the cuff in #RLV/cuffs", have, [i.get("name") for i in have])
        cuff_item = have[0]["id"] if have else ""
        def worn_cuff():
            return [o for p in api("TesselAgent", "attachPoints").get("points", []) for o in p.get("objects", []) if o.get("item_id") == cuff_item]
        time.sleep(3)
        s = last_seq()
        rig("@getinv=1001")
        got = heard(s, "heard 1001:")
        ok("@getinv lists #RLV's folders", got and "cuffs" in got[-1].split(": ", 1)[1].split(","), got[-1:])
        s = last_seq()
        rig("@attach:cuffs=force")
        worn = wait(worn_cuff, 20, 1)
        ok("@attach:cuffs=force wears it", worn, json.dumps(worn)[:200] if worn else "nothing")
        time.sleep(2)
        rig("@getattach:left hand=1001,getinvworn:cuffs=1002,getpath:left hand=1003")
        got = heard(s, "heard 100", 8, count=3)
        ok("@getattach:left hand", "heard 1001: 1" in got, got)
        ok("@getinvworn", "heard 1002: |30" in got, got)
        ok("@getpath", "heard 1003: cuffs" in got, got)
        worn_id = worn_obj = None
        for p in worn_cuff():
            worn_id, worn_obj = p.get("item_id"), p.get("object_id")
        print(f"     (worn: item {worn_id}, object {worn_obj})")
        # across a teleport to another region, the worn cuff's restrictions stay (everything worn leaves the world model
        # for a few seconds there)
        cuff("@fly=n")
        time.sleep(1)
        api("LLAgent", "requestTeleport", regionname="Atelier East", x=128, y=128, z=30, skip_confirmation=True, timeout=40)
        time.sleep(25)
        st = api("SojournRlv", "status")
        ok("a teleport to another region keeps the worn cuff's hold", any(h.get("name", "").startswith("RLV Cuff (") and "fly" in h.get("restrictions", [])
                                                                          for h in st.get("held", [])), json.dumps(st.get("held"))[:200])
        api("LLAgent", "requestTeleport", regionname="Atelier", x=STAND[0], y=STAND[1], z=24, skip_confirmation=True, timeout=40)
        time.sleep(6)
        # the worn cuff locks itself on
        cuff("@detach=n")
        time.sleep(1)
        r = call("TesselAgent", "detach", item_id=worn_id, timeout=15)
        ok("@detach=n holds it on against the agent API", "@detach" in str(r.get("error", "")), r.get("error"))
        s = last_seq()
        rig("@detach:cuffs=force")
        time.sleep(3)
        still = worn_cuff()
        ok("@detach:<folder>=force doesn't take a locked cuff off", still, "")
        cuff("@detach=y,fly=n")
        time.sleep(1)
        rig("@detach:cuffs=force")
        gone = wait(lambda: not worn_cuff(), 15, 1)
        ok("unlocked, @detach:<folder>=force takes it off", gone, "")
        time.sleep(3)
        st = api("SojournRlv", "status")
        # (an attachment counts as off once it's been missing three seconds in a region the avatar's been in for 15)
        st = wait(lambda: (lambda s: s if not any(h.get("object") == worn_obj for h in s.get("held", [])) else None)(api("SojournRlv", "status")), 30, 1) or st
        ok("its restrictions went with it", worn_obj and not any(h.get("object") == worn_obj for h in st.get("held", [])), json.dumps(st.get("held"))[:200])
        rig("@clear")
        cuff("@clear")   # (the one in the world heard the same lines)
        time.sleep(1)
        st = api("SojournRlv", "status")
        ok("@clear lets go", not st.get("held"), json.dumps(st.get("held")))
    finally:
        try:
            proc.terminate()
            proc.wait(timeout=20)
        except Exception:
            proc.kill()
    print("rlv: PASS" if ok.ok else "rlv: FAILED")
    return 0 if ok.ok else 1


def eyes():
    """The VR client on the desktop in stereo (both eyes through the headset's per-eye path, no XR runtime): a vision
    sphere, clear within 2 m and black from 6 m, darkens the far half of both eyes and lifts again on @clear."""
    if fixture():
        return 1
    import safety
    from PIL import Image, ImageStat
    lock = ROOT / "local" / "gpu.lock"
    if lock.exists() and time.time() - lock.stat().st_mtime < 1800:
        print(f"the GPU is taken: {lock.read_text(errors='replace')[:200]}")
        return 1
    lock.write_text("tools/rlv.py eyes (RLV vision spheres, a minute or two)")
    base = BASE / "eyes"
    global PORT
    PORT = safety.PORT = VR_PORT
    ok = Checks()
    proc = None
    try:
        for _ in range(10):
            shutil.rmtree(base, ignore_errors=True)
            if not base.exists():
                break
            time.sleep(1)
        base.mkdir(parents=True, exist_ok=True)
        (base / "vr-settings.json").write_text(json.dumps({"rlv": True, "confirmTouches": False, "panelHidden": True, "audioVolume": 0}))
        dotnet = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
        dll = Path(os.environ.get("SOJOURN_DLL") or ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "SojournVR.dll")
        args = ["--window", "1280x480", "--stereo", "--eye", "640x480", "--fps", "20", "--no-assistant", "--no-voice", "--no-media",
                "--first", "Sojourn", "--last", "Rlv", "--password", "sojourn-local", "--bridge-port", str(VR_PORT), "--base-dir", str(base),
                "--start", f"uri:Atelier&{STAND[0]:.0f}&{STAND[1]:.0f}&24", "--minutes", "4", "--sky", "noon"]
        log = open(base / "console.txt", "w", encoding="utf-8", errors="replace")
        proc = subprocess.Popen([str(dotnet), str(dll), *args], cwd=ROOT, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                                text=True, creationflags=BELOW_NORMAL)
        if not wait(lambda: http("/status", timeout=3).get("connected"), 150, 1):
            ok("logged in", False, "")
            return 1
        time.sleep(12)   # the scene loading in

        def shot(name):
            shots = base / "shots"
            before = set(shots.glob("*.png")) if shots.exists() else set()
            subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winkey.ps1"), "-ProcessId", str(proc.pid), "-Vk", "123"],
                           capture_output=True)
            got = wait(lambda: sorted(p for p in shots.glob("*.png") if p not in before and "-eye" not in p.name) if shots.exists() else None, 15, 0.5)
            if not got:
                return None, None
            time.sleep(1)
            left = got[0]
            right = left.with_name(left.stem + "-eye1.png")
            left.rename(base / f"{name}-left.png")
            if right.exists():
                right.rename(base / f"{name}-right.png")
            return base / f"{name}-left.png", base / f"{name}-right.png"

        def top_mean(path):
            im = Image.open(path).convert("L")
            w, h = im.size
            return ImageStat.Stat(im.crop((0, 0, w, h // 2))).mean[0]

        clear = shot("clear")
        ok("a picture of both eyes", clear[0] and clear[1] and clear[1].exists(), str(clear))
        rig("@setsphere=n,setsphere_distmin:2=force,setsphere_distmax:6=force,setsphere_valuemin:0=force,setsphere_valuemax:1=force")
        time.sleep(2)
        dark = shot("sphere")
        if clear[0] and dark[0] and dark[1].exists():
            a, b = top_mean(clear[0]), top_mean(dark[0])
            c, d = top_mean(clear[1]), top_mean(dark[1])
            ok("the far half darkens in the left eye", b < a * 0.25, f"{a:.0f} -> {b:.0f}")
            ok("and in the right eye", d < c * 0.25, f"{c:.0f} -> {d:.0f}")
        rig("@clear")
        time.sleep(2)
        back = shot("cleared")
        if clear[0] and back[0]:
            a, e = top_mean(clear[0]), top_mean(back[0])
            ok("@clear lifts it", abs(e - a) < max(12, a * 0.2), f"{a:.0f} -> {e:.0f}")
        print(f"     pictures in {base}")

        # --- the way out against RLV: seated and held (@unsit, @tploc, @standtp), Ctrl+Shift+H -----------------------
        # (Atelier's parcel doesn't let the tester set a home, so the grid refuses the teleport home itself: what's checked
        # is that the way out stands them up and asks for home whatever RLV says, and that @standtp doesn't take them back)
        rig_id = nearby(NAMES[0])[0]["id"]
        rig(f"@sit:{rig_id}=force")
        time.sleep(3)
        rig("@unsit=n,tploc=n,standtp=n")
        time.sleep(1)
        r = call("TesselAgent", "goHome")
        ok("home refused for the agent API", any(k in str(r.get("error", "")) for k in ("@tploc", "@unsit")), r.get("error"))
        r = call("LLAgent", "requestStand")
        ok("standing refused by @unsit", "@unsit" in str(r.get("error", "")), r.get("error"))
        seated_at = api("LLAgent", "getPosition").get("global")
        mark = len((base / "console.txt").read_text(encoding="utf-8", errors="replace"))
        keys = [subprocess.Popen(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winkey.ps1"), "-ProcessId", str(proc.pid),
                                  "-Vk", str(vk), "-HoldMs", "1800"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) for vk in (17, 16)]
        time.sleep(0.6)
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winkey.ps1"), "-ProcessId", str(proc.pid), "-Vk", "72", "-HoldMs", "150"],
                       capture_output=True)
        for k in keys:
            k.wait(timeout=20)
        time.sleep(6)
        log_text = (base / "console.txt").read_text(encoding="utf-8", errors="replace")[mark:]
        st = api("SojournRlv", "status")
        ok("the way out stands them up through @unsit", st["can"]["stand"] is True and "unsit" in json.dumps(st.get("held")), str(st["can"]["stand"]))
        ok("and asks for home through @tploc", "teleport: asked (home" in log_text, "")
        ok("the way out says what held them", "way out under RLV" in log_text, "")
        ok("@standtp doesn't take them back", "asked by a script" not in log_text, "")
        print(f"     (seated at {seated_at}, now {api('LLAgent', 'getPosition').get('global')})")
    finally:
        if proc:
            try:
                proc.terminate()
                proc.wait(timeout=20)
            except Exception:
                proc.kill()
        lock.unlink(missing_ok=True)
    print("rlv eyes: PASS" if ok.ok else "rlv eyes: FAILED")
    return 0 if ok.ok else 1


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "test"
    if cmd == "fixture":
        sys.exit(fixture())
    if cmd == "test":
        sys.exit(test())
    if cmd == "eyes":
        sys.exit(eyes())
    sys.exit(__doc__)


if __name__ == "__main__":
    main()
