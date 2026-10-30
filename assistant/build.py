"""Building from a spec: one call makes a whole object of many prims, placed, shaped, coloured and linked.

A spec is the object in its own frame: an origin in the region, a turn about the vertical (yaw), and parts at
positions and rotations relative to that, so a table is written as a table (legs at +-0.6 m, a top at 0.75 m) and
then put anywhere, facing any way. The first part becomes the root. Each part goes through TesselBuild create (shape,
size, placement and the rest of TesselBuild modify's keys) and, for per-face settings, modify; then everything is
linked and named. Prims are made one after another: the region hands new prims back in the order asked, and the
client matches them to requests in that order.
Rotations are Euler degrees like LSL's llEuler2Rot (x, then y, then z, about the fixed axes) or quaternions [x, y, z, w].
"""
import asyncio
import json
import math

from claude_agent_sdk import create_sdk_mcp_server, tool

from api import ApiError

PART_KEYS_SKIP = {"pos", "rot", "rot_euler", "faces", "id"}
MAX_PARTS = 256          # SL's linkset limit


def q_euler(deg):
    """LSL's llEuler2Rot: about x, then y, then z (fixed axes), as [x, y, z, w]."""
    x, y, z = (math.radians(v) / 2 for v in deg)
    cx, sx, cy, sy, cz, sz = math.cos(x), math.sin(x), math.cos(y), math.sin(y), math.cos(z), math.sin(z)
    # q = qz * qy * qx (Hamilton), applied to vectors as q v q*
    return [sx * cy * cz - cx * sy * sz, cx * sy * cz + sx * cy * sz, cx * cy * sz - sx * sy * cz, cx * cy * cz + sx * sy * sz]


def q_mul(a, b):
    """The Hamilton product a*b: rotating by b, then by a."""
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return [aw * bx + ax * bw + ay * bz - az * by, aw * by - ax * bz + ay * bw + az * bx,
            aw * bz + ax * by - ay * bx + az * bw, aw * bw - ax * bx - ay * by - az * bz]


def q_norm(q):
    n = math.sqrt(sum(c * c for c in q)) or 1
    return [c / n for c in q]


def rotate(q, v):
    x, y, z, w = q
    p = q_mul(q_mul(q, [v[0], v[1], v[2], 0]), [-x, -y, -z, w])
    return p[:3]


def part_rot(part):
    if isinstance(part.get("rot"), list) and len(part["rot"]) == 4:
        return q_norm([float(c) for c in part["rot"]])
    if isinstance(part.get("rot_euler"), list) and len(part["rot_euler"]) == 3:
        return q_euler([float(c) for c in part["rot_euler"]])
    return [0, 0, 0, 1]


def with_mirrors(parts):
    """A part with mirror: true (or {dynamic: true}, which shows avatars too) is a mirror's glass, its front its own +X
    face (its x size is its thickness): dark, blank and shiny unless the part says otherwise, and right after it the
    box reflection probe flagged as a mirror that makes it one, as the viewer's mirrors are built (docs/rendering.md >
    Mirrors): the probe's +Z out of the glass, its middle plane 1 cm in front of it, covering it with a margin. The probe
    doesn't collide (physics shape none) rather than going phantom, which would take the whole linked object with it."""
    out = []
    for p in parts:
        m = p.get("mirror")
        if not m:
            out.append(p)
            continue
        glass = {k: v for k, v in p.items() if k != "mirror"}
        glass.setdefault("texture", "blank")
        glass.setdefault("color", [0.12, 0.12, 0.12])
        glass.setdefault("shiny", "high")
        out.append(glass)
        sx, sy, sz = [float(c) for c in (p.get("scale") or [0.5, 0.5, 0.5])]
        rot = part_rot(p)
        front = rotate(rot, [sx / 2 + 0.01, 0, 0])
        pos = [float(c) for c in p.get("pos") or [0, 0, 0]]
        out.append({"shape": "box", "pos": [pos[i] + front[i] for i in range(3)], "rot": q_norm(q_mul(rot, q_euler([0, 90, 0]))),
                    "scale": [sz + 0.1, sy + 0.1, 0.5], "physics_shape": "none", "phantom": False,
                    "name": f"{p.get('name') or 'mirror'} probe",
                    "reflection_probe": {"type": "box", "mirror": True, "dynamic": bool(isinstance(m, dict) and m.get("dynamic"))}})
    return out


def place(spec):
    """Each part's region position and rotation."""
    origin = [float(c) for c in spec.get("origin") or [0, 0, 0]]
    yaw = q_euler([0, 0, float(spec.get("yaw") or 0)])
    out = []
    for p in spec["parts"]:
        local = [float(c) for c in p.get("pos") or [0, 0, 0]]
        off = rotate(yaw, local)
        out.append(([origin[i] + off[i] for i in range(3)], q_norm(q_mul(yaw, part_rot(p)))))
    return out


def build_tools(kit):

    @tool("build", "Make an object of up to 256 prims from a spec, in one call: placed, shaped, coloured and linked "
          "(the first part is the root). spec: {name, origin: [x,y,z] in the region (where the object's own 0,0,0 "
          "goes), yaw: degrees about the vertical (0: its +x faces east), link: true, parts: [{shape: box | cylinder | "
          "prism | sphere | torus | tube | ring | cone | pyramid, pos: [x,y,z] in the object's frame (metres), scale: "
          "[x,y,z], rot_euler: [x,y,z] degrees (like llEuler2Rot) or rot: [x,y,z,w], then any of TesselBuild modify's "
          "keys for the whole prim (color [r,g,b] 0-1, which tints the texture, so plywood stays plywood; alpha, "
          "texture (asset or item id, or blank, plywood, transparent), repeats, glow, fullbright, shiny (none, low, "
          "medium, high), mirror (true, or {dynamic: true} to show avatars too: the part is a mirror's glass facing its "
          "own +x, its x size its thickness, dark and shiny unless you say; its reflection probe is added after it), "
          "reflection_probe ({type: sphere|box, ambiance, clip_distance, dynamic, mirror}: a probe goes invisible), hollow, profile_cut, path_cut, twist, taper, top_shear, hole_size, radius_offset, revolutions, skew, "
          "tex_offset, tex_rotation, bump, texgen, name, desc, light, phantom, material, physics_shape; materials: pbr "
          "(a material item's id, or {material, then overrides over it: base_color, metallic, roughness, emissive, "
          "alpha_mode, base_color_texture, normal_texture, ...}), normal and specular maps ({texture, ...}), alpha_mode (none, blend, mask with "
          "alpha_cutoff, emissive), projector ({texture, fov, focus, ambiance}: a light that casts a picture)), and faces: "
          "[{face: n, ...face keys}] for single faces}]}. A box's faces: 0 top, 1-4 sides, 5 bottom. Returns "
          "{root_id, ids (in part order, a mirror's probe right after it: link numbers follow), failed}. Prims are made one by one (~0.3-1 s each).",
          {"type": "object", "properties": {"spec": {"type": "object"}}, "required": ["spec"]})
    async def build(args):
        spec = args.get("spec") or {}
        if isinstance(spec, str):
            try:
                spec = json.loads(spec)
            except ValueError:
                return {"content": [{"type": "text", "text": "error: spec isn't JSON"}], "is_error": True}
        parts = with_mirrors(spec.get("parts") or [])
        spec = {**spec, "parts": parts}
        if not parts or len(parts) > MAX_PARTS:
            return {"content": [{"type": "text", "text": f"error: give 1-{MAX_PARTS} parts"}], "is_error": True}
        name = spec.get("name") or "Object"
        ids, failed = [], []
        for i, ((pos, rot), part) in enumerate(zip(place(spec), parts)):
            kit.status(f"making {name}: part {i + 1} of {len(parts)}")
            args_ = {k: v for k, v in part.items() if k not in PART_KEYS_SKIP}
            args_.update(pos=pos, rot=rot)
            args_.setdefault("name", f"{name} part {i + 1}" if i else name)
            try:
                r = await kit.call("TesselBuild", "create", 30, **args_)
                ids.append(r.get("id"))
                if r.get("modify_error"):
                    failed.append({"part": i, "error": r["modify_error"]})
                for f in part.get("faces") or []:
                    try:
                        await kit.call("TesselBuild", "modify", 20, id=r["id"], **f)
                    except ApiError as e:
                        failed.append({"part": i, "face": f.get("face"), "error": str(e)})
            except ApiError as e:
                ids.append(None)
                failed.append({"part": i, "error": str(e)})
        made = [x for x in ids if x]
        root = made[0] if made else None
        if spec.get("link", True) and len(made) > 1:
            kit.status(f"linking {name}")
            await asyncio.sleep(0.5)   # let the last prims' updates land first
            try:
                r = await kit.call("TesselBuild", "link", 30, ids=made)
                root = r.get("root_id") or root
            except ApiError as e:
                failed.append({"link": str(e)})
        return {"content": [{"type": "text", "text": json.dumps({"root_id": root, "ids": ids, "failed": failed})}]}

    return [build]


def build_server(kit):
    return create_sdk_mcp_server("build", tools=build_tools(kit)), ["mcp__build__build"]
