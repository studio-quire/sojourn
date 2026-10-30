"""Inventory and the avatar's own (batch B7: SojournAo, SojournInventory) on the local grid, never the main grid: the AO
from no folder to Firestorm's #Firestorm/#AO made on request, animations linked into states, standing, cycling,
walking, sitting with and without sit override, stands off, the AO folder locked, a ZHAO-II notecard folder played in
place, a collar relay worn (its owner-say pauses and resumes the AO), the pose stand, protected folders, an AO link
removed and a set's removal cancelled at the confirmation, a texture item's picture, and what the inventory service
answers for thumbnails and favourites (OpenSim has none: those checks are Second Life's, docs/inventory.md).

  python tools/ao.py [--dll DIR]

One client, Sojourn Dancer (made on first run; 1280x720, SOJOURN_TEST_INPUT=1 so posted clicks answer its
confirmations) on :9881 in Atelier, base dir local/ao/a. Confirmations are pressed as tools/land.py does. The AO is
left off and on Firestorm's folder. Written by Claude Opus 5.5, 2026-10-29.
"""
import json
import math
import os
import struct
import sys
import time
from pathlib import Path

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import people as P  # noqa: E402
import land as L  # noqa: E402

ROOT = P.ROOT
P.BASE = L.BASE = ROOT / "local" / "ao"
BASE = P.BASE
PORT = 9881
api, tryapi, wait, check = P.api, P.tryapi, P.wait, P.check


def anim(name, joints, dur=2.0, loop=True, priority=3):
    """A small looping LL .anim (KeyframeMotion's format): each joint swinging about an axis by an amount."""
    n = 9

    def q16(x, lo, hi):
        return int(round((min(max(x, lo), hi) - lo) / (hi - lo) * 65535))

    out = struct.pack("<HHif", 1, 0, priority, dur) + b"\0" + struct.pack("<ffiffI", 0.0, dur, 1 if loop else 0, 0.3, 0.3, 1)
    out += struct.pack("<I", len(joints))
    for jn, (axis, base, swing) in joints.items():
        out += jn.encode() + b"\0" + struct.pack("<ii", priority, n)
        for k in range(n):
            t = dur * k / (n - 1)
            ang = base + swing * math.sin(2 * math.pi * k / (n - 1))
            s = math.sin(ang / 2)
            x, y, z = (a * s for a in axis)
            out += struct.pack("<HHHH", q16(t, 0, dur), q16(x, -1, 1), q16(y, -1, 1), q16(z, -1, 1))
        out += struct.pack("<i", 0)
    p = BASE / f"{name}.anim"
    p.write_bytes(out + struct.pack("<i", 0))
    return p


ANIMS = {
    "AO Stand A": {"mShoulderLeft": ((1, 0, 0), -0.4, 0.05), "mShoulderRight": ((1, 0, 0), 0.4, 0.05)},
    "AO Stand B": {"mShoulderLeft": ((0, 0, 1), 0.9, 0.05), "mShoulderRight": ((0, 0, 1), -0.9, 0.05)},
    "AO Walk": {"mHipLeft": ((0, 1, 0), 0, 0.5), "mHipRight": ((0, 1, 0), 0, -0.5)},
    "AO Sit": {"mHipLeft": ((0, 1, 0), -1.4, 0.02), "mHipRight": ((0, 1, 0), -1.4, 0.02)},
    "AO Pose": {"mShoulderLeft": ((1, 0, 0), -1.5, 0.0), "mShoulderRight": ((1, 0, 0), 1.5, 0.0)},
}

RELAY = """default
{
    attach(key id)
    {
        if (id) { llOwnerSay("<clientAO state=off source=oc>"); llSetTimerEvent(6.0); }
    }
    timer() { llSetTimerEvent(0); llOwnerSay("<clientAO state=on source=oc>"); }
}
"""


def playing():
    r = tryapi(PORT, "LLAgent", "animations")
    return {(x.get("name") or x["id"]): x.get("started_by") for x in (r or {}).get("animations") or []}


def names_playing():
    return set(playing())


def ao(op, **args):
    return tryapi(PORT, "SojournAo", op, timeout=60, **args)


def main(dll):
    import grid
    if "agni" in grid.URL or not grid.ready():
        sys.exit("the local grid isn't up (python tools/grid.py start)")
    BASE.mkdir(parents=True, exist_ok=True)
    grid.account("Sojourn", "Dancer", "sojourn-local")
    env = dict(os.environ, SOJOURN_TEST_INPUT="1")
    proc = P.client("Sojourn", "Dancer", PORT, "a", f"{L.W}x{L.H}", 20, dll, "uri:Atelier&200&200&30", env)
    side = L.Side(PORT, proc, "a", L.W)
    made = []
    try:
        up = wait(lambda: json.loads(P.urllib.request.urlopen(f"http://127.0.0.1:{PORT}/status", timeout=3).read()).get("connected"), 120)
        check("Dancer logged in", up)
        if not up:
            return
        time.sleep(6)

        # animations of our own (made once; found again by name after)
        lib = P_folder("AO test anims")
        have = {i["name"]: i for i in (api(PORT, "LLInventory", "getDirectDescendants", folder_id=lib).get("items") or {}).values()}
        items = {}
        for name, joints in ANIMS.items():
            if name in have:
                items[name] = have[name]["id"]
                continue
            r = api(PORT, "TesselAgent", "upload", timeout=120, path=str(anim(name, joints)), name=name, folder_id=lib, type="animation")
            items[name] = r.get("item_id") or r.get("id")
        check("five animations of our own", len([v for v in items.values() if v]) == 5, items)

        # 1. no Firestorm folder (a fresh account): nothing plays, and it says how to start
        check("the AO's folder read after the inventory", wait(lambda: ao("status").get("loaded") is None, 90), ao("status").get("loaded"))
        st = ao("status")
        if not (st.get("sets") or []):
            check("no AO folder: none until one is picked or made", st.get("folder", {}).get("how") == "none" and "note" in st, st.get("note"))
        # 2. a set, which makes #Firestorm/#AO on request
        if not any(s["name"] == "Test" for s in st.get("sets") or []):
            r = ao("newSet", name="Test")
            check("newSet makes #Firestorm/#AO and the set", "Test" == r.get("set"), r)
            r = ao("add", set="Test", state="Standing", item_ids=[items["AO Stand A"], items["AO Stand B"]])
            check("two stands linked into Standing", r.get("added") == ["AO Stand A", "AO Stand B"], r)
            r = ao("add", set="Test", state="Walking", item_id=items["AO Walk"])
            r = ao("add", set="Test", state="Sitting", item_id=items["AO Sit"])
            check("a walk and a sit linked", r.get("added") == ["AO Sit"], r)
        r = ao("stateFlags", set="Test", state="Standing", cycle=True, cycle_time=4)
        check("Standing cycles every 4 s (its folder renamed)", any(x["state"] == "Standing" and x["cycle"] and x["cycle_time"] == 4 for x in r.get("states") or []), r)
        r = ao("setFlags", name="Test", default=True, sit_override=False)
        check("Test is the default set", r.get("default") is True, r)
        st = ao("status")
        check("status: Firestorm's folder, the set", st.get("folder", {}).get("how") == "firestorm" and st.get("set") == "Test", st.get("folder"))
        fs_ao = st["folder"]["id"]

        # 3. on: the stands play, by the AO
        ao("on", on=True)
        got = wait(lambda: {"AO Stand A", "AO Stand B"} & {k for k, v in playing().items() if v == "my AO (SojournAo)"}, 10)
        check("on: a stand of ours plays, started by my AO", got, playing())
        first = next(iter(got)) if got else None
        changed = wait(lambda: ({"AO Stand A", "AO Stand B"} - {first}) & names_playing(), 12)
        check("4 s later the stand cycles to the other", changed, playing())

        # 4. walking: the walk replaces the stand
        tryapi(PORT, "TesselAgent", "move", forward=1, secs=3, wait=False)
        walked = wait(lambda: "AO Walk" in names_playing(), 4, 0.2)
        check("walking: the AO's walk plays", walked, playing())
        time.sleep(4)
        back = wait(lambda: {"AO Stand A", "AO Stand B"} & names_playing() and "AO Walk" not in names_playing(), 6)
        check("stopped: a stand again, the walk gone", back, playing())

        # 5. sitting on a box: the seat's sit without sit override, ours with it
        me = api(PORT, "LLAgent", "getPosition")
        pos = me["region"]
        box = api(PORT, "TesselBuild", "create", shape="box", pos=[pos[0] + 1.5, pos[1], pos[2] - 0.4], scale=[0.6, 0.6, 0.5], name="AO test seat")
        made.append(box["id"])
        time.sleep(1.5)
        api(PORT, "LLAgent", "requestSit", obj_uuid=box["id"], timeout=20)
        time.sleep(3)
        p = names_playing()
        check("sitting, no sit override: not our sit", "AO Sit" not in p and not ({"AO Stand A", "AO Stand B"} & p), p)
        api(PORT, "LLAgent", "requestStand")
        time.sleep(2)
        ao("setFlags", name="Test", sit_override=True)
        api(PORT, "LLAgent", "requestSit", obj_uuid=box["id"], timeout=20)
        sat = wait(lambda: "AO Sit" in names_playing(), 8)
        check("sit override: our sit on the seat", sat, playing())
        api(PORT, "LLAgent", "requestStand")
        time.sleep(3)
        ao("setFlags", name="Test", sit_override=False)
        check("standing again: no sit of ours left", "AO Sit" not in names_playing(), playing())

        # 6. stands off
        ao("stands", on=False)
        gone = wait(lambda: not ({"AO Stand A", "AO Stand B"} & names_playing()), 6)
        check("stands off: the region's own stand", gone, playing())
        ao("stands", on=True)
        check("stands on again", wait(lambda: {"AO Stand A", "AO Stand B"} & names_playing(), 8), playing())

        # 7. the AO folder is locked; a protected folder too (refused before anyone's asked)
        r = tryapi(PORT, "TesselInventory", "delete", id=fs_ao)
        check("the AO folder can't be deleted (locked as Firestorm locks it)", "locked as Firestorm" in json.dumps(r), r)
        r = tryapi(PORT, "SojournInventory", "protect", folder_id=lib)
        check("a folder protected", any(x["id"] == lib for x in r.get("protected") or []), r)
        r = tryapi(PORT, "TesselInventory", "update", id=lib, name="renamed")
        check("a protected folder keeps its name", "protected" in json.dumps(r), r)
        r = tryapi(PORT, "TesselInventory", "delete", id=lib, timeout=10)
        check("a protected folder can't be deleted (no confirmation asked)", "protected" in json.dumps(r), r)
        r = tryapi(PORT, "SojournInventory", "unprotect", folder_id=lib)
        check("unprotected", not any(x["id"] == lib for x in r.get("protected") or []), r)

        # 8. the pose stand pauses the AO and holds the pose
        r = ao("poseStand", item_id=items["AO Pose"])
        posed = wait(lambda: "AO Pose" in names_playing(), 6)
        check("pose stand: the pose plays", posed, playing())
        check("the AO paused while posing", "pose" in ao("status").get("paused_by", []) and not ({"AO Stand A", "AO Stand B"} & names_playing()), playing())
        ao("poseStandOff")
        check("pose stand off: the AO's stand back", wait(lambda: {"AO Stand A", "AO Stand B"} & names_playing() and "AO Pose" not in names_playing(), 10), playing())

        # 9. a collar's relay, worn: its owner-say pauses the AO, 6 s later resumes it
        relay_ok = relay_test(side, made)
        if relay_ok is not None:
            check("the relay's 'off' paused the AO", relay_ok[0], relay_ok[2])
            check("its 'on' resumed it", relay_ok[1], relay_ok[2])

        # 10. a ZHAO-II notecard folder played in place
        zf = P_folder("Scripted AO")
        zitems = {i["name"] for i in (api(PORT, "LLInventory", "getDirectDescendants", folder_id=zf).get("items") or {}).values()}
        if "AO Stand B" not in zitems:
            api(PORT, "TesselInventory", "copy", id=items["AO Stand B"], dest_id=zf)
            api(PORT, "TesselInventory", "copy", id=items["AO Walk"], dest_id=zf)
        if "ZHAO-II config" not in zitems:
            api(PORT, "TesselInventory", "createNotecard", name="ZHAO-II config", folder_id=zf, text="# a scripted AO's card\n[ Standing ]AO Stand B\n[ Walking ]AO Walk\n")
        time.sleep(1)
        r = ao("folder", folder=zf)
        check("a ZHAO-II folder: one set, read in place", r.get("folder", {}).get("kind") == "ZHAO-II notecard" and len(r.get("sets") or []) == 1, r)
        check("its stand plays", wait(lambda: "AO Stand B" in names_playing() and "AO Stand A" not in names_playing(), 8), playing())
        r = ao("folder", folder="firestorm")
        check("back on Firestorm's folder", r.get("folder", {}).get("how") == "firestorm", r.get("folder"))

        # 11. removing waits for the person: a step removed (pressed), a set's removal cancelled
        r, _ = side.gated("remove", yes=True, pump="SojournAo", set="Test", state="Standing", step="AO Stand B")
        check("an AO link removed after the press", isinstance(r, dict) and "AO Stand B" in (r.get("removed") or []), r)
        steps = [x["name"] for s in ao("set", name="Test").get("states") or [] if s["state"] == "Standing" for x in s["steps"]]
        check("Standing keeps its other stand (the animation itself kept)", "AO Stand B" not in steps and "AO Stand A" in steps, steps)
        ao("add", set="Test", state="Standing", item_id=items["AO Stand B"])
        r, _ = side.gated("removeSet", yes=False, pump="SojournAo", name="Test")
        check("a set's removal cancelled at the confirmation", "didn't confirm" in json.dumps(r) or "declined" in json.dumps(r) or "cancel" in json.dumps(r), r)
        check("the set is still there", any(s["name"] == "Test" for s in ao("status").get("sets") or []))

        # 12. pictures, thumbnails, favourites
        png = BASE / "thumb.png"
        Image.new("RGB", (128, 128), (40, 120, 200)).save(png)
        tex = next((i for i in (api(PORT, "LLInventory", "getDirectDescendants", folder_id=lib).get("items") or {}).values() if i["name"] == "AO thumb"), None)
        tex_id = tex["id"] if tex else (api(PORT, "TesselAgent", "upload", timeout=120, path=str(png), name="AO thumb", folder_id=lib, type="texture").get("item_id"))
        r = tryapi(PORT, "SojournInventory", "picture", id=tex_id, size=64)
        check("a texture's picture is itself", r.get("source") == "the texture itself" and r.get("png") and r.get("width") == 64, {k: v for k, v in (r or {}).items() if k != "png"})
        r = tryapi(PORT, "SojournInventory", "meta", id=items["AO Walk"])
        check("thumbnails and favourites: the service's answer (none on OpenSim)", "favorite" in (r or {}) or "inventory service" in json.dumps(r), r)
        r = tryapi(PORT, "SojournInventory", "favorite", id=items["AO Walk"], on=True)
        check("a favourite: set, or the grid says it can't", r.get("favorite") is True or "inventory service" in json.dumps(r), r)
        r = tryapi(PORT, "SojournInventory", "thumbnail", id=items["AO Walk"], item_id=tex_id)
        check("a thumbnail: set, or the grid says it can't", r.get("thumbnail_id") or "can't take thumbnails" in json.dumps(r) or "inventory service" in json.dumps(r), r)
    finally:
        try:
            tryapi(PORT, "SojournAo", "on", on=False)
            tryapi(PORT, "SojournAo", "poseStandOff")
            for oid in made:
                side.gated("delete", yes=True, pump="TesselBuild", ids=[oid])
        except Exception as e:
            print("cleanup:", e)
        proc.terminate()
        try:
            proc.wait(20)
        except Exception:
            proc.kill()
    print("all passed" if P.ok_all else "SOME FAILED")


def root_folder():
    return api(PORT, "LLInventory", "getBasicFolderID", ft_name="root_inv")["id"]


def P_folder(name):
    root = root_folder()
    for fid, f in (api(PORT, "LLInventory", "getDirectDescendants", folder_id=root).get("categories") or {}).items():
        if f["name"] == name:
            return fid
    return api(PORT, "TesselInventory", "createFolder", name=name, parent_id=root)["folder_id"]


def relay_test(side, made):
    """A box with the relay's script, taken and worn: (paused after its off, resumed after its on, what played)."""
    script = api(PORT, "TesselInventory", "createScript", name="AO relay test", text=RELAY)
    me = api(PORT, "LLAgent", "getPosition")
    pos = me["region"]
    box = api(PORT, "TesselBuild", "create", shape="box", pos=[pos[0] - 1.5, pos[1], pos[2]], scale=[0.1, 0.1, 0.1], name="AO relay test")
    time.sleep(1.5)
    r, _ = side.gated("addToContents", yes=True, pump="TesselBuild", id=box["id"], item_id=script["item_id"])
    if not isinstance(r, dict) or "exception" in r or "error" in r:
        check("relay script into the box (pressed)", False, r)
        made.append(box["id"])
        return None
    time.sleep(2)
    t = api(PORT, "TesselBuild", "take", ids=[box["id"]], timeout=30)
    time.sleep(3)
    objs = api(PORT, "LLInventory", "collectDescendantsIf", folder_id=root_folder(), name="AO relay test", type="object")
    item = next(iter((objs.get("items") or {}).values()), None)
    if not item:
        check("relay box taken into inventory", False, t)
        return None
    api(PORT, "TesselAgent", "attach", item_id=item["id"], timeout=30)
    paused = wait(lambda: "collar" in ao("status").get("paused_by", []), 10, 0.3)
    p = playing()
    resumed = wait(lambda: "collar" not in ao("status").get("paused_by", []), 12, 0.3)
    tryapi(PORT, "TesselAgent", "detach", item_id=item["id"])
    return bool(paused), bool(resumed), p


if __name__ == "__main__":
    dll = sys.argv[sys.argv.index("--dll") + 1] if "--dll" in sys.argv else str(ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0")
    main(dll)
