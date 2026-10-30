"""Animated objects (animesh) on the local grid: a figure made of rigged mesh (tools/crowd.py's body, head and dress)
standing in Atelier as an animated object, dancing by its own script (a looping sway of our own), and the client's checks of it.

  python tools/animesh.py rez     the Animesh Dancer by Atelier's west lawn (an OAR merged in once; again replaces it)
  python tools/animesh.py test    Sojourn Tester looks at it: it moves between frames while it dances ("animesh
                                  play sway" on channel 8), holds still when stopped ("animesh stop"), and
                                  TesselObjects says it's animated; TesselBuild animated: false turns it into a still
                                  mesh again and true back; then a copy worn at the pelvis dances on its own skeleton
                                  beside the tester's (its shots kept in local/animesh for a look)

The dancer is Sojourn Tester's (so a copy can be taken and worn). Its root prim carries the Animated Mesh flag
(extra param 0x70) in the OAR; its script starts its own looping sway (an .anim made here), puts itself with its pelvis about a metre over the ground
(not when worn), and on channel 8 takes "animesh play <name>" and "animesh stop".

Written by Claude Opus 5.5, 2026-10-26.
"""
import base64
import json
import os
import subprocess
import sys
import tarfile
import time
import urllib.request
import uuid
import zlib
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import crowd  # noqa: E402
import forest  # noqa: E402

OUT = ROOT / "local" / "animesh"
NAME = "Animesh Dancer"
SPOT = (43.0, 45.5, 24.0)       # north-east of Sojourn Tester's usual start (40, 40), clear of the cabin there; settled on the ground by its script
BASE = ROOT / "local" / "tester"
PORT = 9722
W, H = 960, 540
DLL = ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "SojournVR.dll"
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"

SCRIPT = """// Animesh Dancer (tools/animesh.py): dances on its own skeleton; channel 8: "animesh play <name>", "animesh stop"
stopAll()
{
    list playing = llGetObjectAnimationNames();
    integer i;
    for (i = 0; i < llGetListLength(playing); ++i) llStopObjectAnimation(llList2String(playing, i));
}
default
{
    state_entry()
    {
        vector p = llGetPos();
        if (!llGetAttached()) llSetRegionPos(<p.x, p.y, llGround(ZERO_VECTOR) + 1.02>);
        llStartObjectAnimation("sway");
        llListen(8, "", NULL_KEY, "");
    }
    on_rez(integer n) { llResetScript(); }
    listen(integer ch, string name, key id, string msg)
    {
        if (msg == "animesh stop") { stopAll(); llOwnerSay("stopped"); }
        else if (llSubStringIndex(msg, "animesh play ") == 0)
        {
            stopAll();
            llStartObjectAnimation(llGetSubString(msg, 13, -1));
        }
    }
}
"""


def sway_anim():
    """A looping dance of our own as an LL .anim (KeyframeMotion's format): arms swinging up and down out of phase,
    elbows bending, the torso turning and the pelvis bobbing, 2 s a loop."""
    import math
    import struct
    dur, n = 2.0, 17

    def q16(x, lo, hi):
        return int(round((min(max(x, lo), hi) - lo) / (hi - lo) * 65535))

    def quat(axis, angle):
        s = math.sin(angle / 2)
        return [a * s for a in axis]   # x, y, z; w is derived (>= 0)

    joints = {
        "mShoulderLeft": lambda f: quat((1, 0, 0), -0.9 - 0.6 * math.sin(f)),
        "mShoulderRight": lambda f: quat((1, 0, 0), 0.9 + 0.6 * math.sin(f + math.pi)),
        "mElbowLeft": lambda f: quat((0, 0, 1), 0.6 + 0.4 * math.sin(2 * f)),
        "mElbowRight": lambda f: quat((0, 0, 1), -0.6 - 0.4 * math.sin(2 * f)),
        "mTorso": lambda f: quat((0, 0, 1), 0.35 * math.sin(f)),
    }
    out = struct.pack("<HHif", 1, 0, 3, dur) + b"\0" + struct.pack("<ffiffI", 0.0, dur, 1, 0.3, 0.3, 1)
    out += struct.pack("<I", len(joints) + 1)
    for name, rot in joints.items():
        out += name.encode() + b"\0" + struct.pack("<ii", 3, n)
        for k in range(n):
            tt = dur * k / (n - 1)
            x, y, z = rot(2 * math.pi * k / (n - 1))
            out += struct.pack("<HHHH", q16(tt, 0, dur), q16(x, -1, 1), q16(y, -1, 1), q16(z, -1, 1))
        out += struct.pack("<i", 0)
    out += b"mPelvis\0" + struct.pack("<iii", 3, 0, n)
    for k in range(n):
        tt = dur * k / (n - 1)
        out += struct.pack("<HHHH", q16(tt, 0, dur), q16(0, -5, 5), q16(0, -5, 5), q16(0.06 * math.sin(4 * math.pi * k / (n - 1)), -5, 5))
    return out + struct.pack("<i", 0)


def uid(*parts):
    return str(uuid.uuid5(uuid.UUID("7d1e4b2a-9c3f-4e8d-a1b6-3f2e5c7d9a01"), "/".join(map(str, parts))))


def animated(extra_b64):
    """The part's ExtraParams with the Animated Mesh flag added (LLExtendedMeshParams: type 0x70, a U32, bit 0)."""
    raw = base64.b64decode(extra_b64)
    return base64.b64encode(bytes([raw[0] + 1]) + raw[1:] + b"\x70\x00\x04\x00\x00\x00\x01\x00\x00\x00").decode()


def tester_id():
    import media_fixtures as mf
    return mf.accounts()["Sojourn Tester"]


def build():
    OUT.mkdir(parents=True, exist_ok=True)
    owner = tester_id()
    assets = {}
    seed = 700
    o = crowd.OUTFITS[0]
    skin = forest.encode(crowd.skin_tex(o["skin"], seed))
    cloth = forest.encode(crowd.cloth_tex(o["cloth"], seed + 1))
    skin_id, cloth_id = uid("tex", "skin", zlib.crc32(skin)), uid("tex", "cloth", zlib.crc32(cloth))
    assets[f"{skin_id}_texture.jp2"] = skin
    assets[f"{cloth_id}_texture.jp2"] = cloth

    def mesh(name, make):
        data, size = crowd.lods(make, fracs=(0.5, 0.25, 0.12, 0.06))
        mid = uid("mesh", name, zlib.crc32(data))
        assets[f"{mid}_mesh.llmesh"] = data
        return mid, np.maximum(size, 0.05)

    prims = []
    for name, make in crowd.body_prims(seed, o["slim"]).items():
        prims.append((name, *mesh(f"body-{name}", make), forest.te_bytes(skin_id)))
    # the torso first: it's the root, whose flag makes the linkset animated
    prims.sort(key=lambda p: p[0] != "torso")
    prims.append(("head", *mesh("head", crowd.head_make(seed)), forest.te_bytes(skin_id)))
    prims.append(("dress", *mesh("dress", crowd.dress_make(seed, "dress")), forest.te_bytes(cloth_id, repeat=(4.0, 4.0))))

    root_id = uid("part", 0)
    parts = []
    for i, (pname, mid, size, te) in enumerate(prims):
        pid = root_id if i == 0 else uid("part", i)
        xml = forest.part(pid, 300 + i, NAME if i == 0 else f"{NAME}: {pname}", SPOT, (0, 0, 0, 1), tuple(float(x) for x in size), te,
                          mesh=mid, link=i + 1, offset=(0, 0, 0), root_pos=SPOT)
        if i == 0:
            start = xml.index("<ExtraParams>") + len("<ExtraParams>")
            end = xml.index("</ExtraParams>")
            xml = xml[:start] + animated(xml[start:end]) + xml[end:]
        parts.append(xml.replace(forest.OWNER, owner))
    script_id = uid("asset", "script", zlib.crc32(SCRIPT.encode()))
    import audio
    item = audio.task_item(uid("item", "script"), script_id, "dancer", 10, root_id, owner)
    parts[0] = parts[0].replace("<InventorySerial>0</InventorySerial>", "<InventorySerial>1</InventorySerial>")
    parts[0] = parts[0].replace("</SceneObjectPart>", "<TaskInventory>" + item + "</TaskInventory></SceneObjectPart>")
    assets[f"{script_id}_script.lsl"] = SCRIPT.encode()
    anim = sway_anim()
    anim_id = uid("asset", "sway", zlib.crc32(anim))
    assets[f"{anim_id}_animation.bvh"] = anim
    anim_item = audio.task_item(uid("item", "sway"), anim_id, "sway", 19, root_id, owner).replace("<Type>19</Type>", "<Type>20</Type>")
    parts[0] = parts[0].replace("<InventorySerial>1</InventorySerial>", "<InventorySerial>2</InventorySerial>").replace("</TaskInventory>", anim_item + "</TaskInventory>")
    path = OUT / "animesh.oar"
    with tarfile.open(path, "w:gz", compresslevel=1) as tar:
        forest.add(tar, "archive.xml", forest.archive_xml())
        forest.add(tar, f"objects/Animesh_Dancer__{root_id}.xml", forest.group(parts).encode())
        for name, data in assets.items():
            forest.add(tar, f"assets/{name}", data)
    return path


def in_world():
    import sqlite3
    import grid
    c = sqlite3.connect(f"file:{grid.BIN / 'OpenSim.db'}?mode=ro", uri=True)
    try:
        return c.execute("select count(*) from prims where Name = ?", (NAME,)).fetchone()[0]
    finally:
        c.close()


def rez():
    import grid
    path = build().resolve()
    grid.console('change region "Atelier"', wait=1)
    if in_world():
        grid.console(f'delete object name "{NAME}"', wait=2, answers=("y",))
    print("\n".join(grid.console(f'load oar --merge "{path}"', wait=8)[-2:]))
    end = time.time() + 60
    while time.time() < end and not in_world():
        grid.console("backup", wait=3)
        time.sleep(2)
    print("in the world" if in_world() else "not in the world")
    return 0 if in_world() else 1


def api(pump, op, timeout=15, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{PORT}/cmd", data=body, method="POST", headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 60) as r:
        out = json.loads(r.read() or b"null")
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(f"{pump}.{op}: {out['error']}")
    return out


def wait(f, seconds):
    end = time.time() + seconds
    while time.time() < end:
        try:
            if v := f():
                return v
        except Exception:
            pass
        time.sleep(0.5)
    return None


def test():
    if subprocess.run([sys.executable, str(ROOT / "tools" / "client.py"), "build"], capture_output=True).returncode:
        sys.exit("the client didn't build")
    if not in_world() and rez():
        return 1
    account = (BASE / "account.txt").read_text().split("/")[1].split()[0]
    console = open(OUT / "console.txt", "w", encoding="utf-8")
    env = dict(os.environ, SOJOURN_AVATAR_DEBUG=os.environ.get("SOJOURN_AVATAR_DEBUG", "0"))
    proc = subprocess.Popen([str(DOTNET), str(DLL), "--first", "Sojourn", "--last", "Tester", "--password", account,
                             "--bridge-port", str(PORT), "--window", f"{W}x{H}", "--fps", "30", "--no-assistant",
                             "--base-dir", str(BASE), "--start", "uri:Atelier&41&40&24"], cwd=ROOT, env=env,
                            stdout=console, stderr=subprocess.STDOUT)
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print(f"{'ok  ' if cond else 'FAIL'} {name}: {detail}", flush=True)

    def key(vk):
        subprocess.run(["powershell", "-NoProfile", "-File", str(ROOT / "tools" / "winkey.ps1"), "-ProcessId", str(proc.pid), "-Vk", str(vk)], capture_output=True)

    def shot(label):
        shots = BASE / "shots"
        before = set(shots.glob("*.png")) if shots.exists() else set()
        key(123)
        new = wait(lambda: set(shots.glob("*.png")) - before, 10)
        if not new:
            return None
        time.sleep(0.3)
        src = sorted(new)[-1]
        dst = OUT / f"shot-{label}.png"
        dst.write_bytes(src.read_bytes())
        src.unlink()
        return np.asarray(Image.open(dst).convert("RGB")).astype(int)

    def motion(label):
        a = shot(f"{label}-a")
        time.sleep(0.6)
        b = shot(f"{label}-b")
        if a is None or b is None:
            return None
        # changed pixels around the middle of the view, where the dancer stands, leaving out the sky and the water
        # (bluer than red in both frames: their clouds and waves move too)
        def warm(x):
            return x[..., 0] > x[..., 2] + 15
        changed = (np.abs(a - b).max(axis=2) > 24) & (warm(a) | warm(b))
        return int(changed[H // 4:H * 4 // 5, W * 3 // 8:W * 5 // 8].sum())

    def say(text):
        api("LLChatBar", "sendChat", message=text, channel=8)

    try:
        if not wait(lambda: json.loads(urllib.request.urlopen(f"http://127.0.0.1:{PORT}/status", timeout=3).read()).get("connected"), 90):
            check("login", False, "didn't log in")
            return 1
        dancer = wait(lambda: next((x for x in api("TesselObjects", "nearby", dist=30, query="Animesh").get("objects", []) if x.get("name") == NAME), None), 30)
        check("the dancer nearby", dancer, dancer and dancer["id"])
        if not dancer:
            return 1
        got = api("TesselObjects", "get", id=dancer["id"])
        obj = (got.get("objects") or [got])[0] if isinstance(got, dict) else {}
        check("reads as animated", obj.get("animated") is True, f"animated {obj.get('animated')}")
        # face it (the desktop view turns with the avatar)
        api("LLAgent", "lookAt", obj_uuid=dancer["id"])
        key(0x56)   # V: first person, so the tester's own avatar isn't in the way
        time.sleep(12)   # meshes, the skeleton's dance
        say("animesh play sway")
        time.sleep(3)
        moving = motion("dancing")
        check("it dances (pixels change between frames)", moving is not None and moving > 150, f"{moving}")
        say("animesh stop")
        time.sleep(3)
        still = motion("stopped")
        check("stopped, it holds still", still is not None and still < max(60, (moving or 0) * 0.25), f"{still}")
        api("TesselBuild", "modify", id=dancer["id"], animated=False)
        time.sleep(3)
        got = api("TesselObjects", "get", id=dancer["id"])
        obj = (got.get("objects") or [got])[0]
        check("animated off reads back", obj.get("animated") is None, f"animated {obj.get('animated')}")
        shot("not-animated")
        api("TesselBuild", "modify", id=dancer["id"], animated=True)
        time.sleep(2)
        say("animesh play sway")
        time.sleep(3)
        again = motion("again")
        check("animated again, it dances", again is not None and again > 150, f"{again}")

        # worn: a copy at the pelvis, its own skeleton dancing on the tester's
        def items():
            got = api("LLInventory", "collectDescendantsIf", name=NAME, type="object")
            got = got.get("items") or {}
            return {i.get("item_id") or i.get("id"): i for i in (got.values() if isinstance(got, dict) else got)}
        before = items()
        api("TesselBuild", "take", ids=[dancer["id"]], copy=True, timeout=20)   # a fresh copy: the fixture may have changed
        item = wait(lambda: next((i for k, i in items().items() if k not in before), None), 20)
        check("a copy in inventory", item, item and item.get("name"))
        if item:
            iid = item.get("item_id") or item.get("id")
            try:
                api("TesselAgent", "attach", item_id=iid, point="Pelvis", timeout=20)
            except RuntimeError as e:
                if "already worn" not in str(e):
                    raise
            # third person again, facing away from the one in the world: what moves is the worn one
            key(0x56)
            here = api("LLAgent", "getPosition")["region"]
            api("LLAgent", "lookAt", position=[here[0] - 10, here[1] - 10, here[2]])
            time.sleep(10)
            say("animesh play sway")
            time.sleep(3)
            worn = motion("worn")
            check("worn, it dances on its own skeleton", worn is not None and worn > 150, f"{worn}")
            api("TesselAgent", "detach", item_id=iid, timeout=20)
        log = (OUT / "console.txt").read_text(encoding="utf-8", errors="replace")
        errors = [l for l in log.splitlines() if "Exception" in l]
        check("no exceptions", not errors, "; ".join(errors[:3]))
        print(f"shots in {OUT}")
    finally:
        proc.terminate()
        try:
            proc.wait(15)
        except subprocess.TimeoutExpired:
            proc.kill()
        console.close()
    print("all ok" if ok else "some failed")
    return 0 if ok else 1


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "test"
    sys.exit({"rez": rez, "test": test, "build": lambda: print(build()) or 0}[cmd]())
