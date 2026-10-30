"""A synthetic Second Life-like forest for performance tests: four region OARs (made here, all our own content) dense
with what a busy SL place has and the Atelier scenes lack: thousands of objects per region, mesh trees and ferns with
alpha-masked leaves, alpha grass cards, prim builds, and thousands of distinct textures encoded the way SL's are
(J2C, 5 levels, quality layers, LRCP). Loaded into the second grid copy (local/perf-grid) as the scene "forest".

  python tools/forest.py build [--scale 1.0]      write local/perf-grid/forest/*.oar (textures and meshes inside)
  python tools/forest.py load                     load them into the four regions and save the scene (restarts)
  python tools/forest.py places [--scale 0.5] [--load]   four far regions, each a place with its own meshes and
                                                  bump-mapped clutter (places-*.oar); --load adds them to the perf grid
                                                  (Regions/Place*.ini, ports 9744-9747) beside the forest: scene "places"

Per region at scale 1: ~420 mesh trees, ~2500 ferns and bushes, ~1500 grass cards, ~500 rocks, ~30 prim builds,
~2000 clutter prims with a texture each; about 9,000 prims and 1,100 textures per region (3,500 over the four).
What it doesn't have: avatars (rigged mesh crowds), scripts, particles, media.
"""
import io
import math
import os
import random
import struct
import sys
import tarfile
import time
import uuid
import zlib
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "local" / "perf-grid" / "forest"
REGIONS = ["Atelier", "Atelier North", "Atelier East", "Atelier Northeast"]
# far regions, each a place of its own (teleports between places: tools/teleports.py)
PLACES = ["Place One", "Place Two", "Place Three", "Place Four"]
OWNER = "4f84556c-fa9c-4322-bb58-17d234547a94"   # the Atelier scenes' owner (the grid's estate owner)
NS = uuid.UUID("5b0f2c8e-6f1d-4e7a-9c3b-2a4d6e8f0a1c")


def uid(*parts):
    return str(uuid.uuid5(NS, "/".join(map(str, parts))))


# --- textures ---------------------------------------------------------------------------------------------------

def noise(rng, size, octaves=4):
    out = np.zeros((size, size), np.float32)
    amp, total = 1.0, 0.0
    for o in range(octaves):
        n = 4 * 2 ** o
        g = rng.random((n + 1, n + 1)).astype(np.float32)
        img = Image.fromarray((g * 255).astype(np.uint8)).resize((size, size), Image.BICUBIC)
        out += amp * np.asarray(img, np.float32) / 255
        total += amp
        amp *= 0.5
    return out / total


def texture(kind, seed, size):
    rng = np.random.default_rng(seed)
    n = noise(rng, size)
    hue = rng.random(3)
    if kind == "leaf":
        # leaf clusters: a binary-ish mask of blobs on transparent, green-to-yellow
        m = noise(rng, size, 3)
        yy, xx = np.mgrid[0:size, 0:size] / size
        blobs = np.zeros((size, size), np.float32)
        for _ in range(int(rng.integers(18, 40))):
            cx, cy, r = rng.random(), rng.random(), rng.uniform(0.04, 0.12)
            blobs = np.maximum(blobs, 1 - ((xx - cx) ** 2 + (yy - cy) ** 2) / r ** 2)
        a = ((blobs + 0.25 * m) > 0.55).astype(np.float32)
        base = np.array([0.18 + 0.2 * hue[0], 0.35 + 0.3 * hue[1], 0.08 + 0.1 * hue[2]])
        rgb = base[None, None, :] * (0.6 + 0.6 * n[..., None])
        return np.dstack([rgb, a])
    if kind == "grass":
        xx = np.arange(size)[None, :] / size
        yy = np.arange(size)[:, None] / size
        blades = (np.sin(xx * rng.uniform(60, 120) + n * 6) > 0.6) & (yy > 0.15 + 0.6 * n)
        rgb = np.array([0.25, 0.45 + 0.2 * hue[0], 0.12])[None, None, :] * (0.7 + 0.5 * n[..., None])
        return np.dstack([rgb, blades.astype(np.float32)])
    if kind == "veil":
        # soft blended alpha (glass, mist, cloth): forces the alpha-blend path
        rgb = (0.5 + 0.5 * hue)[None, None, :] * (0.7 + 0.3 * n[..., None])
        return np.dstack([rgb, 0.25 + 0.5 * n])
    if kind == "bark":
        yy = np.arange(size)[:, None] / size
        streak = 0.5 + 0.5 * np.sin(np.arange(size)[None, :] / size * rng.uniform(40, 90) + n * 8)
        rgb = np.array([0.30, 0.22, 0.15])[None, None, :] * (0.5 + 0.5 * streak[..., None] * (0.6 + 0.6 * n[..., None]))
        return rgb + 0 * yy[..., None]
    # surfaces: stone, wood, plaster, painted signs, with a pattern so each one differs
    xx, yy = np.mgrid[0:size, 0:size] / size
    k = rng.integers(0, 4)
    if k == 0:
        pat = (np.floor(xx * rng.integers(3, 12)) + np.floor(yy * rng.integers(3, 12))) % 2
    elif k == 1:
        pat = 0.5 + 0.5 * np.sin(xx * rng.uniform(10, 60))
    elif k == 2:
        pat = (np.abs(np.sin(yy * rng.uniform(5, 30))) > 0.9).astype(np.float32)
    else:
        pat = n
    rgb = hue[None, None, :] * (0.55 + 0.25 * pat[..., None] + 0.3 * n[..., None])
    return rgb


def grain(arr, seed):
    """Photographic detail: without it the encoder reaches its quality long before SL's byte sizes."""
    rng = np.random.default_rng(seed + 1)
    out = arr.copy()
    out[..., :3] += rng.normal(0, 0.07, out[..., :3].shape)
    return out


def encode(arr):
    """J2C the way SL's textures come: irreversible, 5 levels, 64x64 code blocks, LRCP with quality layers whose last
    one is about 8:1 (a 1024x1024 RGB texture ~390 KB, as on the main grid)."""
    arr = np.clip(arr * 255, 0, 255).astype(np.uint8)
    img = Image.fromarray(arr, "RGBA" if arr.shape[2] == 4 else "RGB")
    buf = io.BytesIO()
    img.save(buf, "JPEG2000", no_jp2=True, irreversible=True, num_resolutions=6, codeblock_size=(64, 64),
             progression="LRCP", quality_mode="rates", quality_layers=[160, 80, 40, 20, 8])
    return buf.getvalue()


def make_texture(job):
    tid, kind, seed, size = job
    return tid, encode(grain(texture(kind, seed, size), seed))


# --- meshes (SL's mesh asset format: an LLSD binary header, then zlib'd LLSD blocks per LOD) ---------------------

def llsd(v):
    if isinstance(v, bool):
        return b"1" if v else b"0"
    if isinstance(v, int):
        return b"i" + struct.pack(">i", v)
    if isinstance(v, float):
        return b"r" + struct.pack(">d", v)
    if isinstance(v, bytes):
        return b"b" + struct.pack(">I", len(v)) + v
    if isinstance(v, str):
        e = v.encode()
        return b"s" + struct.pack(">I", len(e)) + e
    if isinstance(v, (list, tuple)):
        return b"[" + struct.pack(">I", len(v)) + b"".join(llsd(x) for x in v) + b"]"
    if isinstance(v, dict):
        out = b"{" + struct.pack(">I", len(v))
        for k, x in v.items():
            e = k.encode()
            out += b"k" + struct.pack(">I", len(e)) + e + llsd(x)
        return out + b"}"
    raise TypeError(type(v))


def q16(vals, lo, hi):
    """Interleaved little-endian u16s over a domain per column (positions, normals, UVs as SL stores them)."""
    vals = np.asarray(vals, np.float64)
    t = np.clip((vals - lo) / np.maximum(hi - lo, 1e-9), 0, 1)
    return np.round(t * 65535).astype("<u2").tobytes()


def submesh(pos, nrm, uv, tris):
    pos, nrm, uv = np.asarray(pos, np.float64), np.asarray(nrm, np.float64), np.asarray(uv, np.float64)
    nrm = nrm / np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-9)
    lo, hi = np.full(3, -0.5), np.full(3, 0.5)
    ulo, uhi = uv.min(0), uv.max(0)
    return {
        "Position": q16(pos, lo, hi),
        "Normal": q16(nrm, -1.0, 1.0),
        "TexCoord0": q16(uv, ulo, uhi),
        "TriangleList": np.asarray(tris, "<u2").tobytes(),
        "PositionDomain": {"Min": [float(x) for x in lo], "Max": [float(x) for x in hi]},
        "TexCoord0Domain": {"Min": [float(x) for x in ulo], "Max": [float(x) for x in uhi]},
    }


def cards(rng, count, center, radii, size):
    """Leaf cards: quads scattered in an ellipsoid, each facing a random way (two-sided by being doubled)."""
    pos, nrm, uv, tris = [], [], [], []
    for _ in range(count):
        d = rng.normal(size=3)
        d /= np.linalg.norm(d)
        p = center + d * radii * rng.uniform(0.3, 1.0) ** 0.5
        a = rng.normal(size=3)
        a /= np.linalg.norm(a)
        b = np.cross(a, d)
        b /= max(np.linalg.norm(b), 1e-6)
        a = np.cross(d, b)
        s = size * rng.uniform(0.7, 1.3)
        corners = [p + (-a - b) * s, p + (a - b) * s, p + (a + b) * s, p + (-a + b) * s]
        base = len(pos)
        u0 = rng.integers(0, 2) * 0.5
        for c, t in zip(corners, [(u0, 0), (u0 + 0.5, 0), (u0 + 0.5, 1), (u0, 1)]):
            pos.append(c); nrm.append(d); uv.append(t)
        for c, t in zip(corners, [(u0, 0), (u0 + 0.5, 0), (u0 + 0.5, 1), (u0, 1)]):
            pos.append(c); nrm.append(-d); uv.append(t)
        tris += [base, base + 1, base + 2, base, base + 2, base + 3]
        tris += [base + 4, base + 6, base + 5, base + 4, base + 7, base + 6]
    return pos, nrm, uv, tris


def trunk(sides, rings, r0, r1, z0, z1):
    pos, nrm, uv, tris = [], [], [], []
    for j in range(rings + 1):
        t = j / rings
        z, r = z0 + (z1 - z0) * t, r0 + (r1 - r0) * t
        for i in range(sides + 1):
            a = 2 * math.pi * i / sides
            pos.append((r * math.cos(a), r * math.sin(a), z)); nrm.append((math.cos(a), math.sin(a), 0.2)); uv.append((i / sides * 2, t * 4))
    w = sides + 1
    for j in range(rings):
        for i in range(sides):
            a, b = j * w + i, j * w + i + 1
            tris += [a, b, b + w, a, b + w, a + w]
    return pos, nrm, uv, tris


def make_mesh(job):
    """A tree (bark trunk + leaf-card canopy, two faces) or a plant (leaf cards only), four LODs."""
    mid, kind, seed = job
    rng = np.random.default_rng(seed)
    lods = {}
    for name, frac in (("high_lod", 1.0), ("medium_lod", 0.35), ("low_lod", 0.12), ("lowest_lod", 0.04)):
        r = np.random.default_rng(seed)
        if kind == "tree":
            sides = max(4, int(12 * frac ** 0.5))
            t = trunk(sides, max(2, int(8 * frac)), 0.05, 0.025, -0.5, 0.15)
            n = max(12, int(r.integers(500, 900) * frac))
            c = cards(r, n, np.array([0, 0, 0.18]), np.array([0.42, 0.42, 0.3]), 0.07 / max(frac, 0.12) ** 0.35)
            faces = [submesh(*t), submesh(*c)]
        else:
            n = max(6, int(r.integers(80, 220) * frac))
            c = cards(r, n, np.array([0, 0, -0.1]), np.array([0.4, 0.4, 0.38]), 0.12 / max(frac, 0.12) ** 0.35)
            faces = [submesh(*c)]
        lods[name] = zlib.compress(llsd(faces), 6)
    header, body, off = {}, b"", 0
    for name, blob in lods.items():
        header[name] = {"offset": off, "size": len(blob)}
        body += blob
        off += len(blob)
    return mid, llsd(header) + body


# --- objects (OpenSim's SceneObjectGroup XML) -------------------------------------------------------------------

USED = set()   # the assets the region being planned uses (its OAR carries those)


def te_bytes(default_tex, face_tex=None, color=(1, 1, 1, 1), repeat=(1.0, 1.0), material=0):
    """A TextureEntry: one default texture (others per face), colour, repeats; the rest at defaults."""
    USED.add(default_tex)
    USED.update((face_tex or {}).values())
    out = uuid.UUID(default_tex).bytes
    for face, tex in (face_tex or {}).items():
        out += bytes([1 << face]) + uuid.UUID(tex).bytes
    out += b"\x00"
    out += bytes(255 - int(round(c * 255)) for c in color) + b"\x00"
    out += struct.pack("<f", repeat[0]) + b"\x00" + struct.pack("<f", repeat[1]) + b"\x00"
    out += b"\x00\x00\x00" + b"\x00\x00\x00" + b"\x00\x00\x00"   # offsets, rotation
    out += bytes([material]) + b"\x00" + b"\x00\x00" + b"\x00\x00"   # material, media, glow
    out += b"\x00" * 16 + b"\x00"                                    # material id
    return out


def v3(name, x, y, z):
    return f"<{name}><X>{x:.4f}</X><Y>{y:.4f}</Y><Z>{z:.4f}</Z></{name}>"


def q4(name, q):
    return f"<{name}><X>{q[0]:.5f}</X><Y>{q[1]:.5f}</Y><Z>{q[2]:.5f}</Z><W>{q[3]:.5f}</W></{name}>"


def yaw_q(a):
    return (0.0, 0.0, math.sin(a / 2), math.cos(a / 2))


SHAPES = {   # profile curve, path curve, profile shape name
    "box": (1, 16, "Square"), "cylinder": (0, 16, "Circle"), "sphere": (5, 32, "HalfCircle"),
}


def part(pid, local, name, pos, rot, scale, te, shape="box", mesh=None, link=0, offset=(0, 0, 0), root_pos=None):
    import base64
    pc, path, pshape = SHAPES[shape]
    sculpt, stype, extra = "00000000-0000-0000-0000-000000000000", 0, "AA=="
    if mesh:
        USED.add(mesh)
        sculpt, stype = mesh, 5
        extra = base64.b64encode(b"\x01\x30\x00\x11\x00\x00\x00" + uuid.UUID(mesh).bytes + b"\x05").decode()
    gp = root_pos or pos
    return (
        '<SceneObjectPart xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
        f"<AllowedDrop>false</AllowedDrop><CreatorID><UUID>{OWNER}</UUID></CreatorID><FolderID><UUID>{pid}</UUID></FolderID>"
        f"<InventorySerial>0</InventorySerial><UUID><UUID>{pid}</UUID></UUID><LocalId>{local}</LocalId><Name>{name}</Name>"
        "<Material>3</Material><PassTouches>false</PassTouches><PassCollisions>false</PassCollisions><RegionHandle>0</RegionHandle>"
        "<ScriptAccessPin>0</ScriptAccessPin>" + v3("GroupPosition", *gp) + v3("OffsetPosition", *offset) + q4("RotationOffset", rot)
        + v3("Velocity", 0, 0, 0) + v3("AngularVelocity", 0, 0, 0) + v3("Acceleration", 0, 0, 0)
        + "<Description /><Color><R>0</R><G>0</G><B>0</B><A>255</A></Color><Text /><SitName /><TouchName />"
        f"<LinkNum>{link}</LinkNum><ClickAction>0</ClickAction><Shape><ProfileCurve>{pc}</ProfileCurve>"
        f"<TextureEntry>{base64.b64encode(te).decode()}</TextureEntry><ExtraParams>{extra}</ExtraParams>"
        f"<PathBegin>0</PathBegin><PathCurve>{path}</PathCurve><PathEnd>0</PathEnd><PathRadiusOffset>0</PathRadiusOffset>"
        "<PathRevolutions>0</PathRevolutions><PathScaleX>100</PathScaleX><PathScaleY>100</PathScaleY><PathShearX>0</PathShearX>"
        "<PathShearY>0</PathShearY><PathSkew>0</PathSkew><PathTaperX>0</PathTaperX><PathTaperY>0</PathTaperY><PathTwist>0</PathTwist>"
        "<PathTwistBegin>0</PathTwistBegin><PCode>9</PCode><ProfileBegin>0</ProfileBegin><ProfileEnd>0</ProfileEnd>"
        f"<ProfileHollow>0</ProfileHollow><State>0</State><LastAttachPoint>0</LastAttachPoint><ProfileShape>{pshape}</ProfileShape>"
        f"<HollowShape>Same</HollowShape><SculptTexture><UUID>{sculpt}</UUID></SculptTexture><SculptType>{stype}</SculptType>"
        "<FlexiSoftness>0</FlexiSoftness><FlexiTension>0</FlexiTension><FlexiDrag>0</FlexiDrag><FlexiGravity>0</FlexiGravity>"
        "<FlexiWind>0</FlexiWind><FlexiForceX>0</FlexiForceX><FlexiForceY>0</FlexiForceY><FlexiForceZ>0</FlexiForceZ>"
        "<LightColorR>0</LightColorR><LightColorG>0</LightColorG><LightColorB>0</LightColorB><LightColorA>1</LightColorA>"
        "<LightRadius>0</LightRadius><LightCutoff>0</LightCutoff><LightFalloff>0</LightFalloff><LightIntensity>1</LightIntensity>"
        f"<FlexiEntry>false</FlexiEntry><LightEntry>false</LightEntry><SculptEntry>{'true' if mesh else 'false'}</SculptEntry></Shape>"
        + v3("Scale", *scale)
        + "<SitTargetOrientation><X>0</X><Y>0</Y><Z>0</Z><W>1</W></SitTargetOrientation>" + v3("SitTargetPosition", 0, 0, 0)
        + v3("SitTargetPositionLL", 0, 0, 0) + "<SitTargetOrientationLL><X>0</X><Y>0</Y><Z>0</Z><W>1</W></SitTargetOrientationLL>"
        f"<ParentID>0</ParentID><CreationDate>1792141642</CreationDate><Category>0</Category><SalePrice>0</SalePrice>"
        "<ObjectSaleType>0</ObjectSaleType><OwnershipCost>0</OwnershipCost><GroupID><UUID>00000000-0000-0000-0000-000000000000</UUID></GroupID>"
        f"<OwnerID><UUID>{OWNER}</UUID></OwnerID><LastOwnerID><UUID>{OWNER}</UUID></LastOwnerID><RezzerID><UUID>{OWNER}</UUID></RezzerID>"
        "<BaseMask>647168</BaseMask><OwnerMask>647168</OwnerMask><GroupMask>0</GroupMask><EveryoneMask>0</EveryoneMask>"
        "<NextOwnerMask>532480</NextOwnerMask><Flags>Phantom</Flags><CollisionSound><UUID>00000000-0000-0000-0000-000000000000</UUID></CollisionSound>"
        "<CollisionSoundVolume>0</CollisionSoundVolume>" + v3("AttachedPos", 0, 0, 0)
        + "<TextureAnimation></TextureAnimation><ParticleSystem></ParticleSystem><PayPrice0>-2</PayPrice0><PayPrice1>-2</PayPrice1>"
        "<PayPrice2>-2</PayPrice2><PayPrice3>-2</PayPrice3><PayPrice4>-2</PayPrice4><Buoyancy>0</Buoyancy>" + v3("Force", 0, 0, 0)
        + v3("Torque", 0, 0, 0) + "<VolumeDetectActive>false</VolumeDetectActive><PhysicsShapeType>0</PhysicsShapeType>"
        + v3("CameraEyeOffset", 0, 0, 0) + v3("CameraAtOffset", 0, 0, 0)
        + "<SoundID><UUID>00000000-0000-0000-0000-000000000000</UUID></SoundID><SoundGain>0</SoundGain><SoundFlags>0</SoundFlags>"
        "<SoundRadius>0</SoundRadius><SoundQueueing>false</SoundQueueing></SceneObjectPart>"
    )


def group(parts):
    root, rest = parts[0], parts[1:]
    return "<SceneObjectGroup>" + root + ("<OtherParts>" + "".join(rest) + "</OtherParts>" if rest else "<OtherParts />") + "</SceneObjectGroup>"


def height(x, y, seed):
    return 24.0 + 3.0 * math.sin(x / 37.0 + seed) * math.cos(y / 41.0 - seed) + 1.5 * math.sin((x + y) / 19.0 + 2 * seed)


def terrain_r32(seed):
    return b"".join(struct.pack("<f", height(x, y, seed)) for y in range(256) for x in range(256))


# --- the scene --------------------------------------------------------------------------------------------------

def plan(scale, regions_planned=REGIONS, places=False):
    """Every texture and mesh, and per region the objects (as XML), deterministically. places: the far regions' kind
    (tools/forest.py places): each its own meshes (as an SL place has, rather than the forest's 40 shared), and half
    its clutter bump-mapped from its own texture (shiny/bump, common on older SL builds)."""
    textures, meshes = {}, {}

    def tex(kind, key, size):
        tid = uid("tex", kind, key)
        textures[tid] = (kind, int(uuid.UUID(tid)) % (2 ** 31), size)
        return tid

    tree_kinds = [(uid("mesh", "tree", i), tex("bark", i, 512), tex("leaf", f"tree{i}", 1024)) for i in range(16)]
    plant_kinds = [(uid("mesh", "plant", i), tex("leaf", f"plant{i}", 512)) for i in range(24)]
    for m, _, _ in tree_kinds:
        meshes[m] = ("tree", int(uuid.UUID(m)) % (2 ** 31))
    for m, _ in plant_kinds:
        meshes[m] = ("plant", int(uuid.UUID(m)) % (2 ** 31))
    grass = [tex("grass", i, 512) for i in range(8)]
    rocks = [tex("surface", f"rock{i}", 512) for i in range(20)]
    veils = [tex("veil", i, 256) for i in range(6)]

    regions = {}
    for ri, region in enumerate(regions_planned):
        seed = ri + (10 if places else 0)
        rng = random.Random(1000 + seed)
        objs = []
        USED.clear()
        local = [3000000 + seed * 200000]
        if places:
            tree_kinds = [(uid("mesh", region, "tree", i), tex("bark", i % 8, 512), tex("leaf", f"tree{i % 16}", 1024)) for i in range(int(80 * scale))]
            plant_kinds = [(uid("mesh", region, "plant", i), tex("leaf", f"plant{i % 24}", 512)) for i in range(int(600 * scale))]
            for m, _, _ in tree_kinds:
                meshes[m] = ("tree", int(uuid.UUID(m)) % (2 ** 31))
            for m, _ in plant_kinds:
                meshes[m] = ("plant", int(uuid.UUID(m)) % (2 ** 31))

        def nid():
            local[0] += 1
            return local[0]

        def place(n, fn):
            for k in range(int(n * scale)):
                x, y = rng.uniform(4, 252), rng.uniform(4, 252)
                objs.append(fn(k, x, y, height(x, y, seed)))

        def tree(k, x, y, z):
            m, bark, leaf = rng.choice(tree_kinds)
            w, h = rng.uniform(6, 12), rng.uniform(10, 22)
            pid = uid("obj", region, "tree", k)
            return group([part(pid, nid(), f"forest:tree{k}", (x, y, z + h / 2), yaw_q(rng.uniform(0, 6.28)), (w, w, h),
                               te_bytes(bark, {1: leaf}), mesh=m)])

        def plant(k, x, y, z):
            m, leaf = rng.choice(plant_kinds)
            s = rng.uniform(0.8, 3.0)
            pid = uid("obj", region, "plant", k)
            return group([part(pid, nid(), f"forest:plant{k}", (x, y, z + s * 0.4), yaw_q(rng.uniform(0, 6.28)), (s, s, s * 0.8),
                               te_bytes(leaf), mesh=m)])

        def grasscard(k, x, y, z):
            s = rng.uniform(1.5, 4)
            pid = uid("obj", region, "grass", k)
            return group([part(pid, nid(), f"forest:grass{k}", (x, y, z + 0.4), yaw_q(rng.uniform(0, 6.28)), (s, 0.02, 0.9),
                               te_bytes(rng.choice(grass)))])

        def rock(k, x, y, z):
            s = rng.uniform(0.6, 3.5)
            pid = uid("obj", region, "rock", k)
            return group([part(pid, nid(), f"forest:rock{k}", (x, y, z), yaw_q(rng.uniform(0, 6.28)), (s, s * 0.8, s * 0.6),
                               te_bytes(rng.choice(rocks), repeat=(2.0, 2.0)), shape="sphere")])

        def build(k, x, y, z):
            # a ruin or cabin: walls, floor and beams as prims, ~80 of them, its own handful of textures
            own = [tex("surface", f"{region}/build{k}/{i}", rng.choice([256, 512, 512, 1024])) for i in range(7)]
            pid = uid("obj", region, "build", k)
            parts = [part(pid, nid(), f"forest:build{k}", (x, y, z + 0.2), (0, 0, 0, 1), (10, 8, 0.4), te_bytes(own[0], repeat=(4.0, 4.0)))]
            n = 1
            for i in range(79):
                ox, oy, oz = rng.uniform(-5, 5), rng.uniform(-4, 4), rng.uniform(0.3, 6)
                sc = (rng.uniform(0.2, 3), rng.uniform(0.2, 3), rng.uniform(0.2, 3))
                shape = "box" if i % 5 else "cylinder"
                t = own[1 + i % 6] if i % 11 else rng.choice(veils)
                n += 1
                parts.append(part(uid("obj", region, "build", k, i), nid(), f"forest:build{k}/{i}", (ox, oy, oz), (0, 0, 0, 1), sc,
                                  te_bytes(t), shape=shape, link=n, offset=(ox, oy, oz), root_pos=(x, y, z + 0.2)))
            parts[0] = parts[0].replace("<LinkNum>0</LinkNum>", "<LinkNum>1</LinkNum>")
            return group(parts)

        def clutter(k, x, y, z):
            s = rng.uniform(0.3, 2.0)
            t = tex("surface", f"{region}/clutter{k}", rng.choice([256, 512, 512, 512, 1024]))
            pid = uid("obj", region, "clutter", k)
            return group([part(pid, nid(), f"forest:clutter{k}", (x, y, z + s / 2), yaw_q(rng.uniform(0, 6.28)), (s, s * rng.uniform(0.3, 1), s),
                               te_bytes(t, material=1 if places and k % 2 else 0), shape=rng.choice(["box", "box", "cylinder", "sphere"]))])

        place(420, tree)
        place(2500, plant)
        place(1500, grasscard)
        place(500, rock)
        place(30, build)
        place(2000, clutter)
        regions[region] = (objs, set(USED))
    return textures, meshes, regions


def archive_xml():
    return ('<?xml version="1.0" encoding="utf-16"?>\n<archive major_version="0" minor_version="8">\n  <creation_info>\n'
            f'    <datetime>{int(time.time())}</datetime>\n    <id>{uid("archive")}</id>\n  </creation_info>\n'
            '  <assets_included>True</assets_included>\n  <region_info>\n    <size_in_meters>256,256</size_in_meters>\n'
            '  </region_info>\n</archive>\n').encode("utf-8")   # (OpenSim's own say utf-16 and are utf-8)


def add(tar, name, data):
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mtime = int(time.time())
    tar.addfile(info, io.BytesIO(data))


def build(scale, places=False):
    if os.name == "nt":   # a shared machine: below normal, and the worker processes inherit it
        import ctypes
        ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)
    OUT.mkdir(parents=True, exist_ok=True)
    textures, meshes, regions = plan(scale, PLACES if places else REGIONS, places)
    print(f"{len(textures)} textures, {len(meshes)} meshes, {sum(len(v[0]) for v in regions.values())} objects")
    cache = OUT / "assets"
    cache.mkdir(exist_ok=True)
    todo = [(t, *spec) for t, spec in textures.items() if not (cache / f"{t}_texture.jp2").exists()]
    t0 = time.time()
    with ProcessPoolExecutor(max_workers=3) as pool:
        for i, (tid, data) in enumerate(pool.map(make_texture, todo, chunksize=8)):
            (cache / f"{tid}_texture.jp2").write_bytes(data)
            if i % 250 == 0:
                print(f"  textures {i}/{len(todo)} ({time.time() - t0:.0f} s)", flush=True)
        for mid, data in pool.map(make_mesh, [(m, *spec) for m, spec in meshes.items()]):
            (cache / f"{mid}_mesh.llmesh").write_bytes(data)
    # which assets each region uses: an OAR carries only its own
    for ri, (region, (objs, ids)) in enumerate(regions.items()):
        used = [f for f in cache.iterdir() if f.name.split("_")[0] in ids]
        path = OUT / f"{'places' if places else 'forest'}-{ri}.oar"
        with tarfile.open(path, "w:gz", compresslevel=1) as tar:
            add(tar, "archive.xml", archive_xml())
            add(tar, f"terrains/{region}.r32", terrain_r32(ri + (10 if places else 0)))
            for k, o in enumerate(objs):
                add(tar, f"objects/forest_{k:05d}__{uid('objfile', region, k)}.xml", o.encode())
            for f in used:
                add(tar, f"assets/{f.name}", f.read_bytes())
        print(f"{path.name}: {region}, {len(objs)} objects, {len(used)} assets, {path.stat().st_size >> 20} MB")


def place_regions():
    """The far regions' definitions on the perf grid: apart from the forest and from each other (no neighbours),
    ports 9744-9747."""
    regions_dir = ROOT / "local" / "perf-grid" / "bin" / "Regions"
    for i, region in enumerate(PLACES):
        (regions_dir / f"{region.replace(' ', '')}.ini").write_text("\n".join([
            f"[{region}]", f"RegionUUID = {uid('region', region)}", f"Location = {1010 + 3 * i},1010",
            "InternalAddress = 0.0.0.0", f"InternalPort = {9744 + i}", "AllowAlternatePorts = False",
            "ExternalHostName = 127.0.0.1", ""]))


def load(places=False):
    os.environ["SOJOURN_GRID"] = "perf-grid"
    sys.path.insert(0, str(ROOT / "tools"))
    import grid
    import shutil
    if places:
        # the forest's four as they are, and the places beside them
        grid.stop_and_wait()
        place_regions()
        shutil.copyfile(grid.GRID / "scenes" / "forest" / "OpenSim.db", grid.BIN / "OpenSim.db")
    if not grid.start():
        sys.exit("perf grid didn't start")
    for ri, region in enumerate(PLACES if places else REGIONS):
        path = (OUT / f"{'places' if places else 'forest'}-{ri}.oar").resolve()
        grid.console(f'change region "{region}"', wait=1)
        print(region, "\n".join(grid.console(f'load oar "{path}"', wait=5)[-3:]))
        # loading runs in the background; wait for the console to say so
        end = time.time() + 1800
        while time.time() < end:
            log = (grid.GRID / "console.log").read_text(errors="replace")
            tail = log[log.rfind("Loading archive to region"):]
            if "Successfully loaded archive" in tail:
                print("  loaded", flush=True)
                break
            if "Aborting load" in tail or "load errors" in tail:
                sys.exit(f"{region}: {tail[:600]}")
            time.sleep(5)
    grid.stop_and_wait()
    name = "places" if places else "forest"
    dest = grid.GRID / "scenes" / name
    dest.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(grid.BIN / "OpenSim.db", dest / "OpenSim.db")
    print(f"saved as scene {name};", "up" if grid.start() else "failed to restart")


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "build"
    if cmd == "build":
        s = float(sys.argv[sys.argv.index("--scale") + 1]) if "--scale" in sys.argv else 1.0
        build(s)
    elif cmd == "load":
        load()
    elif cmd == "places":
        s = float(sys.argv[sys.argv.index("--scale") + 1]) if "--scale" in sys.argv else 0.5
        build(s, places=True)
        if "--load" in sys.argv:
            load(places=True)
