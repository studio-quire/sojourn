"""Test fixtures on the local grid for the assistant: an inventory worth searching for Sojourn Pilot, a fountain on the
Atelier lawn with a landmark to it, and a second account (Sojourn Friend) to give and talk to.

  python tools/fixtures.py            needs the grid and a running client logged in as Sojourn Pilot (agent API :9720)

Idempotent: things already there (by name) are left alone. Uses the agent API directly, as any script could; the
gated ops it doesn't use.
"""
import json
import math
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
API = "http://127.0.0.1:9720"


def cmd(pump, op, timeout=30, **args):
    body = json.dumps({"pump": pump, "data": {"op": op, **args}, "timeout": timeout}).encode()
    req = urllib.request.Request(API + "/cmd", data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout + 10) as r:
        out = json.loads(r.read())
    if isinstance(out, dict) and out.get("error"):
        raise RuntimeError(f"{pump}.{op}: {out['error']}")
    return out


def folder(name, parent):
    r = cmd("LLInventory", "getDirectDescendants", folder_id=parent)
    for fid, f in (r.get("categories") or {}).items():
        if f["name"] == name:
            return fid
    return cmd("TesselInventory", "createFolder", name=name, parent_id=parent)["folder_id"]


def names_in(fid):
    r = cmd("LLInventory", "getDirectDescendants", folder_id=fid)
    return {i["name"] for i in (r.get("items") or {}).values()}


CLOTHES = {
    "Shirts": [("shirt", "Red Shirt", [0.78, 0.12, 0.12], "cotton, short sleeves"),
               ("shirt", "Blue Oxford Shirt", [0.2, 0.33, 0.7], "button-down"),
               ("shirt", "Green Flannel Shirt", [0.2, 0.5, 0.25], "checked flannel"),
               ("shirt", "White Linen Shirt", [0.95, 0.94, 0.9], "loose, for summer")],
    "Coats": [("jacket", "Wool Coat", [0.45, 0.45, 0.47], "grey, knee length"),
              ("jacket", "Rain Coat", [0.95, 0.8, 0.15], "yellow, with a hood"),
              ("jacket", "Long Coat", [0.08, 0.08, 0.09], "black, ankle length")],
    "Trousers": [("pants", "Blue Jeans", [0.2, 0.28, 0.5], "straight cut"),
                 ("pants", "Khaki Chinos", [0.7, 0.62, 0.45], ""),
                 ("pants", "Red Trousers", [0.7, 0.15, 0.15], "")],
    "Shoes": [("shoes", "Brown Boots", [0.35, 0.22, 0.12], "leather"),
              ("shoes", "White Sneakers", [0.95, 0.95, 0.95], "")],
}

OBJECTS = [("Red Armchair", "box", [0.9, 0.9, 0.9], [0.75, 0.15, 0.15], "a comfy armchair"),
           ("Floor Lamp", "cylinder", [0.3, 0.3, 1.6], [0.9, 0.85, 0.7], "brass, warm light"),
           ("Picnic Basket", "box", [0.5, 0.35, 0.3], [0.6, 0.45, 0.25], "wicker"),
           ("Beach Ball", "sphere", [0.6, 0.6, 0.6], [0.95, 0.3, 0.2], "striped"),
           ("Garden Bench", "box", [1.8, 0.5, 0.5], [0.45, 0.32, 0.2], "oak")]

NOTES = [("Shopping list", "Things to look for:\n- a hat for the party\n- a rug for the cabin\n- fairy lights"),
         ("Party on Saturday", "Mira's party, Saturday 8pm SLT, at the Atelier pavilion. Bring a gift."),
         ("Welcome to Sojourn", "This is the local test grid. The fountain is on the east lawn.")]


def main():
    st = json.loads(urllib.request.urlopen(API + "/status", timeout=5).read())
    if not st.get("connected"):
        sys.exit("the client isn't logged in")
    # a second account to give things to and talk with
    subprocess.run([sys.executable, str(ROOT / "tools" / "grid.py"), "account", "Sojourn", "Friend", "sojourn-local"])

    root = cmd("LLInventory", "getBasicFolderID", ft_name="root_inv")["id"]
    clothing = cmd("LLInventory", "getBasicFolderID", ft_name="clothing")["id"]
    objects = cmd("LLInventory", "getBasicFolderID", ft_name="object")["id"]
    notecards = cmd("LLInventory", "getBasicFolderID", ft_name="notecard")["id"]
    landmarks = cmd("LLInventory", "getBasicFolderID", ft_name="landmark")["id"]

    # editWearable edits what's worn: wear each new piece to colour it, then put the original outfit back
    worn_before = [w["item_id"] for w in cmd("TesselAgent", "wearables")["wearables"]]
    changed = False
    for sub, items in CLOTHES.items():
        fid = folder(sub, clothing)
        have = names_in(fid)
        for typ, name, color, desc in items:
            if name in have:
                continue
            r = cmd("TesselAgent", "createWearable", type=typ, name=name, desc=desc, folder_id=fid, wear=True, replace=True)
            time.sleep(1)
            cmd("TesselAgent", "editWearable", item_id=r["item_id"], color=color)
            changed = True
            print("made", name)
    if changed:
        now = [w["item_id"] for w in cmd("TesselAgent", "wearables")["wearables"]]
        extra = [i for i in now if i not in worn_before]
        if extra:
            cmd("LLAppearance", "detachItems", items_id=extra, timeout=60)
        cmd("LLAppearance", "wearItems", items_id=worn_before, replace=True, timeout=60)

    have = names_in(objects)
    pos = cmd("LLAgent", "getPosition")["region"]
    for name, shape, scale, color, desc in OBJECTS:
        if name in have:
            continue
        r = cmd("TesselBuild", "create", shape=shape, pos=[pos[0] + 2, pos[1], pos[2] + 1], scale=scale, name=name, desc=desc, color=color)
        time.sleep(0.5)
        cmd("TesselBuild", "take", ids=[r["id"]], folder_id=objects)
        print("made", name)

    have = names_in(notecards)
    for name, body in NOTES:
        if name not in have:
            cmd("TesselInventory", "createNotecard", name=name, text=body, folder_id=notecards)
            print("made", name)

    # the fountain: on the lawn east of the arrival point, with a landmark
    near = cmd("TesselObjects", "nearby", dist=120, limit=200, timeout=10).get("objects", [])
    fountain = next((o for o in near if o.get("name") == "Fountain"), None)
    if fountain is None:
        fx, fy = 152.0, 112.0
        cmd("LLAgent", "requestTeleport", regionname="Atelier", x=fx - 3, y=fy, z=60, skip_confirmation=True)
        time.sleep(6)
        z = cmd("LLAgent", "getPosition")["region"][2] - 0.9   # the feet: the ground there
        basin = cmd("TesselBuild", "create", shape="cylinder", pos=[fx, fy, z + 0.3], scale=[3.2, 3.2, 0.6], hollow=0.85,
                    color=[0.62, 0.6, 0.56], name="Fountain", desc="a stone fountain on the east lawn")["id"]
        water = cmd("TesselBuild", "create", shape="cylinder", pos=[fx, fy, z + 0.45], scale=[2.8, 2.8, 0.05],
                    color=[0.25, 0.45, 0.6], alpha=0.6, shiny="high", name="Fountain water")["id"]
        column = cmd("TesselBuild", "create", shape="cylinder", pos=[fx, fy, z + 1.0], scale=[0.35, 0.35, 1.4],
                     color=[0.62, 0.6, 0.56], name="Fountain column")["id"]
        bowl = cmd("TesselBuild", "create", shape="sphere", pos=[fx, fy, z + 1.75], scale=[1.0, 1.0, 0.35], hollow=0.8,
                   color=[0.62, 0.6, 0.56], name="Fountain bowl")["id"]
        time.sleep(1)
        cmd("TesselBuild", "link", ids=[basin, water, column, bowl])
        print("built the fountain")
    else:
        fx, fy = fountain["pos"][0], fountain["pos"][1]
    if "The Fountain" not in names_in(landmarks):
        cmd("LLAgent", "requestTeleport", regionname="Atelier", x=fx - 3, y=fy, z=60, skip_confirmation=True)
        time.sleep(6)
        cmd("TesselInventory", "createLandmark", name="The Fountain", desc="Atelier, east lawn", folder_id=landmarks)
        print("made landmark The Fountain")
    if "Pavilion" not in names_in(landmarks):
        cmd("LLAgent", "requestTeleport", regionname="Atelier", x=128, y=118, z=40, skip_confirmation=True)
        time.sleep(6)
        cmd("TesselInventory", "createLandmark", name="Pavilion", desc="Atelier, by the arrival point", folder_id=landmarks)
        print("made landmark Pavilion")
    cmd("LLAgent", "requestTeleport", regionname="Atelier", x=128, y=118, z=26, skip_confirmation=True)
    print("done")


if __name__ == "__main__":
    main()
