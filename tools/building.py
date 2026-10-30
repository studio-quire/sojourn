"""Building (batch B6, SojournBuild and TesselBuild's new keys) on the local grid, never the main grid: a projector,
legacy normal and specular maps, PBR materials and overrides (the local grid takes ModifyMaterialParams), materials
read back, undo and redo (an edit, a creation), the script chores on a selection (list, stop, reset, recompile,
remove), land impact and script info, default and bulk permissions, an object's group and deeding, take separately,
a backup of what the account made and its restore, and pathfinding's absence; every gated op pressed as
tools/land.py does. What it makes it deletes.

  python tools/building.py [--dll DIR] [--only NAME,...]

One client, Sojourn Builder, at Atelier, with SOJOURN_TEST_INPUT=1 so posted clicks answer its confirmations. What
only Second Life can check is in docs/building.md. Written by Claude Opus 5.5, 2026-10-29.
"""
import json
import os
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import people as P  # noqa: E402
import land as L  # noqa: E402

ROOT = P.ROOT
BASE = ROOT / "local" / "building"
P.BASE = L.BASE = BASE
PORT = 9891
W, H = 1280, 720
api, tryapi, wait, check = P.api, P.tryapi, P.wait, P.check
is_err = L.is_err
BLANK_MATERIAL = "968cbad0-4dad-d64e-71b5-72bf13ad051a"
SCRIPT = 'default { state_entry() { llOwnerSay("b6 says hi"); } touch_start(integer n) { llOwnerSay("touched"); } }'


def main(dll, only):
    import grid
    if "agni" in grid.URL or not grid.ready():
        sys.exit("the local grid isn't up (python tools/grid.py start)")
    grid.account("Sojourn", "Builder", "sojourn-local")
    env = dict(os.environ, SOJOURN_TEST_INPUT="1")
    proc = P.client("Sojourn", "Builder", PORT, "a", f"{W}x{H}", 20, dll, "uri:Atelier&118&104&22", env)
    a = L.Side(PORT, proc, "a", W)
    want = lambda name: not only or name in only
    made = []

    def B(op, **args):
        return tryapi(PORT, "SojournBuild", op, timeout=60, **args)

    def T(op, **args):
        return tryapi(PORT, "TesselBuild", op, timeout=60, **args)

    def get(id_):
        r = tryapi(PORT, "TesselObjects", "get", ids=[id_], full=True, fresh=True, timeout=5)
        return (r.get("objects") or [{}])[0] if isinstance(r, dict) else {}

    def box(**keys):
        r = T("create", **keys)
        if "id" in r:
            made.append(r["id"])
        return r

    try:
        up = wait(lambda: json.loads(P.urllib.request.urlopen(f"http://127.0.0.1:{PORT}/status", timeout=3).read()).get("connected"), 120)
        check("Builder logged in", up)
        if not up:
            return
        time.sleep(6)
        api(PORT, "SojournSettings", "set", away_after=0)
        me = api(PORT, "LLAgent", "getID")["id"]
        # what an earlier run left here (its own, by owner) goes first
        near = tryapi(PORT, "TesselObjects", "nearby", dist=12, limit=60, timeout=8).get("objects") or []
        left = [o["id"] for o in near if o.get("owner_id") == me and not o.get("root_id")]
        if left:
            r, _ = a.gated("delete", pump="TesselBuild", ids=left)
            print(f"swept {len(left)} left from an earlier run: {r}")

        # --- projector, legacy materials, PBR ------------------------------------------------------------------------
        if want("materials"):
            r = box(shape="box", scale=[1, 1, 1], projector={"texture": "blank", "fov": 60, "focus": 2, "ambiance": 0.3})
            check("create with a projector", "id" in r and not r.get("modify_error"), r)
            pid = r.get("id")
            time.sleep(1.5)
            m = B("materials", id=pid)
            pj = m.get("projector") or {}
            check("projector read back", abs(pj.get("fov", 0) - 60) < 0.5 and abs(pj.get("focus", 0) - 2) < 0.01 and abs(pj.get("ambiance", 0) - 0.3) < 0.01, m)
            r = T("modify", id=pid, normal={"texture": "blank", "repeats": [2, 2]}, specular={"texture": "blank", "glossiness": 120, "environment": 40, "color": [1, 0.5, 0.5]})
            check("legacy normal and specular", not is_err(r), r)
            time.sleep(2)
            m = B("materials", id=pid)
            leg = [f.get("legacy") for f in m.get("faces", []) if f.get("legacy")]
            check("legacy material on every face", len(leg) == 6 and all(l and l.get("normal") and l.get("specular") for l in leg),
                  json.dumps(leg[:1])[:200] if leg else m)
            if leg:
                check("legacy values", leg[0]["specular"]["glossiness"] == 120 and leg[0]["specular"]["environment"] == 40
                      and abs(leg[0]["normal"]["repeats"][0] - 2) < 0.01 and leg[0]["alpha_mode"] == "none", leg[0])
            r = T("modify", id=pid, face=1, normal=False, specular=False)
            time.sleep(2)
            m = B("materials", id=pid, face=1)
            check("legacy material off one face", not (m.get("faces") or [{}])[0].get("legacy"), m)
            # PBR: the viewer's blank material, then overrides
            r = T("modify", id=pid, pbr=BLANK_MATERIAL)
            check("PBR material applied", not is_err(r), r)
            time.sleep(2)
            m = B("materials", id=pid)
            pbr = [f.get("pbr") for f in m.get("faces", [])]
            check("PBR material read back", pbr and all(p and p.get("material_id") == BLANK_MATERIAL for p in pbr), json.dumps(pbr)[:240])
            r = T("modify", id=pid, face=0, pbr={"base_color": [1, 0, 0, 1], "metallic": 0.2, "roughness": 0.7, "alpha_mode": "mask", "alpha_cutoff": 0.4})
            check("PBR override", not is_err(r), r)
            time.sleep(2)
            m = B("materials", id=pid, face=0)
            ov = ((m.get("faces") or [{}])[0].get("pbr") or {}).get("overrides") or {}
            mat = (ov.get("materials") or [{}])[0]
            pmr = mat.get("pbrMetallicRoughness", {})
            check("PBR override read back", pmr.get("baseColorFactor", [0])[:3] == [1, 0, 0] and abs(pmr.get("metallicFactor", 0) - 0.2) < 0.01
                  and mat.get("alphaMode") == "MASK", json.dumps(ov)[:300])
            r = T("modify", id=pid, face=0, pbr={"roughness": 0.3})
            time.sleep(2)
            m = B("materials", id=pid, face=0)
            mat = ((((m.get("faces") or [{}])[0].get("pbr") or {}).get("overrides") or {}).get("materials") or [{}])[0]
            check("PBR override added to (base colour kept)", mat.get("pbrMetallicRoughness", {}).get("baseColorFactor", [0])[:3] == [1, 0, 0]
                  and abs(mat.get("pbrMetallicRoughness", {}).get("roughnessFactor", 0) - 0.3) < 0.01, json.dumps(mat)[:300])
            r = T("modify", id=pid, pbr=False)
            time.sleep(2)
            m = B("materials", id=pid)
            check("PBR material taken off", all(not f.get("pbr") for f in m.get("faces", [])), json.dumps(m)[:200])
            r = T("modify", id=pid, pbr={"metallic": 0.5})
            check("an override with no material refused", is_err(r), r)
            r = T("modify", id=pid, projector=False)
            time.sleep(1.5)
            check("projector off", "projector" not in B("materials", id=pid), "")

        # --- undo and redo ---------------------------------------------------------------------------------------------
        if want("undo"):
            r = box(shape="box", color=[0, 1, 0])
            uid = r.get("id")
            time.sleep(1.5)
            before = get(uid)
            T("modify", id=uid, color=[0, 0, 1], scale=[2, 0.5, 0.5], name="b6 undo")
            time.sleep(1.5)
            h = B("history", count=3)
            check("history shows the modify", h.get("undo") and "faces" in h["undo"][0].get("parts", []) and "scale" in h["undo"][0].get("parts", []), json.dumps(h)[:240])
            r = B("undo")
            check("undo", not is_err(r) and r.get("undone"), r)
            time.sleep(2)
            after = get(uid)
            fa, fb = (after.get("faces") or {}).get("all", {}), (before.get("faces") or {}).get("all", {})
            check("undo put the colour, size and name back", fa.get("color") == fb.get("color") and after.get("scale") == before.get("scale")
                  and after.get("name") == before.get("name"), f"{fa.get('color')} {after.get('scale')} {after.get('name')}")
            r = B("redo")
            time.sleep(2)
            again = get(uid)
            check("redo", not is_err(r) and (again.get("faces") or {}).get("all", {}).get("color") == [0, 0, 1] and again.get("name") == "b6 undo",
                  f"{r} {again.get('name')}")
            # a creation: undo deletes it, through the gate
            r = box(shape="sphere")
            sid = r.get("id")
            time.sleep(1.5)
            r, shot = a.gated("undo", pump="SojournBuild")
            time.sleep(2)
            check("undo of a creation deletes it (confirmed)", not is_err(r) and not get(sid).get("id"), r)
            if not get(sid).get("id") and sid in made:
                made.remove(sid)
            # a build: prims made and linked fold into one creation
            p1, p2 = box(shape="box"), box(shape="cylinder")
            T("link", ids=[p1.get("id"), p2.get("id")])
            time.sleep(1.5)
            h = B("history", count=1)
            check("made-then-linked is one step", "linked object of 2 prims" in json.dumps(h), json.dumps(h)[:200])

        # --- scripts in bulk ---------------------------------------------------------------------------------------------
        if want("scripts"):
            r = box(shape="box")
            sid = r.get("id")
            r = tryapi(PORT, "TesselInventory", "createScript", name="b6 script", text=SCRIPT, timeout=60)
            item = r.get("item_id")
            check("a script in inventory", item, r)
            r, _ = a.gated("addToContents", pump="TesselBuild", id=sid, item_id=item)
            r2, _ = a.gated("addToContents", pump="TesselBuild", id=sid, item_id=item, again=True)
            check("two scripts dropped in (confirmed)", not is_err(r) and not is_err(r2), f"{r} {r2}")
            time.sleep(2)
            r = B("scripts", id=sid)
            check("scripts listed", r.get("count") == 2 and r.get("running") == 2, r)
            r = B("runScripts", id=sid, running=False)
            check("set not running", r.get("changed") == 2, r)
            r = B("resetScripts", id=sid)
            check("reset", r.get("count") == 2, r)
            r = B("recompile", id=sid, target="mono", running=True)
            check("recompiled for Mono", len(r.get("compiled", [])) == 2 and not r.get("failed"), json.dumps(r)[:300])
            r = B("recompile", id=sid, target="luau")
            check("Lua refused or skipped here", is_err(r) or r.get("skipped"), json.dumps(r)[:200])
            r = B("scriptInfo", id=sid)
            check("script info", r.get("scripts") == 2, r)
            r = B("landImpact", id=sid, prims=True)
            check("land impact", (r.get("objects") or [{}])[0].get("land_impact") is not None, r)
            r, _ = a.gated("removeScripts", pump="SojournBuild", id=sid)
            check("scripts removed (confirmed)", not is_err(r) and r.get("removed") == 2, r)

        # --- permissions ---------------------------------------------------------------------------------------------------
        if want("perms"):
            r = B("defaultPerms")
            check("default permissions (the viewer's)", r.get("default_perms", {}).get("objects", {}).get("next_owner") == "T", r)
            r, _ = a.gated("setDefaultPerms", pump="SojournBuild", kind="objects", next_owner="MCT", share_with_group=False)
            check("objects' defaults set (confirmed) and sent", not is_err(r) and r.get("sent") == "told the grid", r)
            r, _ = a.gated("setDefaultPerms", pump="SojournBuild", kind="objects", next_owner="T")
            r = box(shape="box")
            bid = r.get("id")
            r = tryapi(PORT, "TesselInventory", "createNotecard", name="b6 note", text="hello", timeout=60)
            note = r.get("item_id")
            a.gated("addToContents", pump="TesselBuild", id=bid, item_id=note)
            time.sleep(1.5)
            r, _ = a.gated("bulkPerms", pump="SojournBuild", id=bid, types=["notecard"], next_owner="C")
            check("bulk permissions on contents (confirmed)", not is_err(r) and r.get("count") == 1, r)
            time.sleep(1.5)
            c = tryapi(PORT, "TesselBuild", "contents", id=bid)
            perms = ((c.get("items") or [{}])[0].get("perms") or {})
            # the floater's fair-use rule: next owner copy without transfer is allowed; next is C (and V, move)
            check("the notecard's next owner can copy, not modify or transfer", perms.get("next") in ("CV", "C"), perms)

        # --- group and deed --------------------------------------------------------------------------------------------------
        if want("group"):
            groups = tryapi(PORT, "LLAgent", "getGroups").get("groups") or []
            gid = groups[0]["id"] if groups else None
            if not gid:
                r, _ = a.gated("create", pump="TesselGroups", name=f"B6 Builders {int(time.time()) % 10000}", charter="b6 test", show_in_list=False, open_enrollment=False)
                gid = (r or {}).get("group_id")
            check("a group to use", gid, groups or r)
            if gid:
                r = box(shape="box")
                gx = r.get("id")
                r, _ = a.gated("group", pump="SojournBuild", id=gx, group_id=gid)
                check("group set (confirmed)", not is_err(r) and r.get("count") == 1, r)
                # not shared yet: the confirmation says it will be refused, and pressed it is
                r, _ = a.gated("deed", pump="SojournBuild", id=gx)
                check("deed refused while it isn't shared", is_err(r) and "shared" in json.dumps(r), r)
                a.gated("modify", pump="TesselBuild", id=gx, group_perms="MCV")   # Share with group: copy, modify and move
                time.sleep(1.5)
                r, _ = a.gated("deed", pump="SojournBuild", id=gx)
                check("deeded to the group (confirmed)", not is_err(r), r)
                if not is_err(r):
                    made.remove(gx)   # the group's now; return it below
                    tryapi(PORT, "TesselBuild", "returnObjects", ids=[gx])

        # --- take separately ---------------------------------------------------------------------------------------------------
        if want("take"):
            t1, t2 = box(shape="box", name="b6 take one"), box(shape="box", name="b6 take two")
            r = T("modify", ids=[t1.get("id"), t2.get("id")], name="b6 take")
            r = T("take", ids=[t1.get("id"), t2.get("id")], separate=True)
            check("taken separately", not is_err(r) and r.get("count") == 2, r)
            for x in (t1, t2):
                if x.get("id") in made:
                    made.remove(x["id"])
            time.sleep(2)
            inv = tryapi(PORT, "SojournAssistant", "findInventory", query="b6 take", timeout=20)
            n = sum(1 for i in inv.get("items", []) if i.get("name") == "b6 take")
            check("two items, not one", n >= 2, n)

        # --- backups ---------------------------------------------------------------------------------------------------------
        if want("backup"):
            r = box(shape="torus", scale=[1, 1, 0.5], color=[1, 0.6, 0.2], name="b6 backup root")
            br = r.get("id")
            r2 = box(shape="cylinder", scale=[0.2, 0.2, 1])
            T("link", ids=[br, r2.get("id")])
            made.remove(r2.get("id"))
            time.sleep(1.5)
            r = B("canExport", id=br)
            check("my own build can be backed up", r.get("exportable") is True and r.get("prims") == 2, r)
            r = B("backup", id=br, name="b6 test")
            check("backup written", Path(r.get("path", "")).exists(), r)
            path = r.get("path")
            r = B("restore", path=path)   # not gated here: the local grid's uploads are free, and none are needed
            check("restored (nothing to upload on the same grid)", not is_err(r) and r.get("uploaded") == 0 and r.get("root_ids"), r)
            for rid in (r or {}).get("root_ids", []):
                made.append(rid)
            time.sleep(2)
            if (r or {}).get("root_ids"):
                o = get(r["root_ids"][0])
                check("the restored root has the shape and name", o.get("shape") == get(br).get("shape") and o.get("name") == "b6 backup root" and o.get("prims") == 2,
                      f"{o.get('shape')} {o.get('name')} {o.get('prims')}")
            if path:
                Path(path).unlink(missing_ok=True)

        # --- pathfinding ---------------------------------------------------------------------------------------------------
        if want("pathfinding"):
            r = B("pathfinding")
            check("pathfinding off on OpenSim, said so", r.get("region_enabled") is False, r)
            r = B("linksets")
            check("linksets say why not", is_err(r) and "pathfinding" in json.dumps(r), r)
    finally:
        made = [m for m in made if get(m).get("id")] if made else made
        if made:
            r, _ = a.gated("delete", pump="TesselBuild", ids=made)
            print("cleanup:", r)
        proc.terminate()
        try:
            proc.wait(10)
        except Exception:
            proc.kill()
    print("ALL OK" if P.ok_all else "SOME FAILED")


if __name__ == "__main__":
    args = sys.argv[1:]
    dll = args[args.index("--dll") + 1] if "--dll" in args else str(ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0")
    only = set(args[args.index("--only") + 1].split(",")) if "--only" in args else set()
    main(dll, only)
