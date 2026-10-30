"""A synthetic Second Life-like crowd for performance tests: dancing and walking NPC avatars on the performance grid
(local/perf-grid), each wearing rigged mesh made here (our own content): a body as a linkset of a dozen prims with
alpha-cut faces and bakes-on-mesh skin, a head, alpha-blended hair cards, a dress or top and trousers, shoes; about
200k triangles an avatar at the highest LOD, in three outfits. OpenSim's NPCs (osNpcCreate) wear them, so every one
is a real avatar to the client: its own appearance, attachments and animations.

  python tools/crowd.py build [--detail 1.0]   write local/perf-grid/crowd/crowd.oar (meshes, textures, notecards,
                                               the spawner and its script inside)
                       [--full-skins]          every skin lists 110 joints, as many SL creators' meshes do (each worn
                                               mesh then has a 110-joint palette to pose: SL's avatar cost),
                                               written as crowd-full.oar
  python tools/crowd.py load                   the light scene plus the crowd, saved as the perf grid's scene "crowd"
  python tools/crowd.py say "spawn 40 12 3"    a command to the spawner (needs a client logged in on the perf grid,
                                               agent API on --api, default 9725 as tools/perfrun.py runs it)

The spawner (a box on Atelier's lawn, owned by the estate owner, who may run OSSL's NPC functions) fills its floor by
itself when the region starts: 40 avatars between 3 and 14 m from its centre, all dancing. On chat channel 7:
"spawn N RADIUS INNER" adds N more in that ring, "clear" removes them all, "walk K" sets K of them walking about,
"dance" sets them all dancing again.
"""
import io
import math
import struct
import sys
import tarfile
import time
import uuid
import xml.etree.ElementTree as ET
import zlib
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import forest  # noqa: E402  (its LLSD, texture, object and OAR helpers)

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "local" / "perf-grid" / "crowd"
SKELETON = ROOT / "src" / "Sojourn.Render" / "Character" / "avatar_skeleton.xml"
NS = uuid.UUID("0d3c6a51-7b2e-4f1a-9a0e-6c5d2b8e4f17")
ESTATE_OWNER = "94f9ecc4-bc15-4da0-b3f1-d1e5cccbaaa4"   # Estate Keeper, the perf grid's estate owner
CENTRE = (128.0, 140.0)                                  # the dance floor, north of the perf client's start
SPAWNER = (128.0, 156.0)
INVISIBLE = "3a367d1c-bef1-6d43-7595-e88c1e3aadb3"
DEFAULT_AVATAR = "c228d1cf-4b5d-4ba8-84f4-899a0796aa97"
BOM_HEAD, BOM_UPPER, BOM_LOWER = "5a9f4a74-30f2-821c-b88d-70499d3e7183", "ae2de45c-d252-50b8-5c6e-19f39ce79317", "24daea5f-0539-cfcf-047f-fbc40b2786ba"
DANCES = [   # the built-in dance1..dance8 (OpenSim's NPCs play built-in animations by name)
    "b68a3d7c-de9e-fc87-eec8-543d787e5b0d", "928cae18-e31d-76fd-9cc9-2f55160ff818", "30047778-10ea-1af7-6881-4db7a3a5a114",
          "951469f4-c7b2-c818-9dee-ad7eea8c30b7", "4bd69a1d-1114-a0b4-625f-84e0a5237155", "cd28b69b-9c95-bb78-3f94-8d605ff1bb12",
          "a54d8ee2-28bb-80a9-7f0c-7afbbe24a5d6", "b0dc417c-1f11-af36-2e80-7e7489fa7cdc"]


def uid(*parts):
    return str(uuid.uuid5(NS, "/".join(map(str, parts))))


# --- the skeleton ------------------------------------------------------------------------------------------------

def skeleton():
    """Joint positions in the default shape's rest pose (rotations are all zero there), avatar frame: x forward,
    y left, z up, metres, mPelvis at 1.067."""
    joints = {}

    def walk(el, at):
        for b in el.findall("bone"):
            p = at + np.array([float(x) for x in b.get("pos").split()])
            joints[b.get("name")] = p
            walk(b, p)
    walk(ET.parse(SKELETON).getroot(), np.zeros(3))
    return joints


J = skeleton()
FULL_SKINS = "--full-skins" in sys.argv   # build: every skin lists 110 joints (see lods)
OAR_NAME = "crowd-full.oar" if FULL_SKINS else "crowd.oar"


# --- geometry ----------------------------------------------------------------------------------------------------

class Part:
    """One submesh (a face): positions, normals, UVs, triangles, and per vertex up to four (joint, weight)."""

    def __init__(self):
        self.pos, self.nrm, self.uv, self.tri, self.w = [], [], [], [], []

    def grid(self, P, N, U, Wj, Ww, closed_u=True):
        """A (rings x cols) grid of vertices into triangles; Wj/Ww: (rings, cols, 4) joints and weights."""
        rings, cols = P.shape[:2]
        base = sum(len(p) for p in self.pos)
        self.pos.append(P.reshape(-1, 3)); self.nrm.append(N.reshape(-1, 3)); self.uv.append(U.reshape(-1, 2))
        self.w.append((Wj.reshape(-1, 4), Ww.reshape(-1, 4)))
        r, c = np.meshgrid(np.arange(rings - 1), np.arange(cols - 1), indexing="ij")
        a = base + r * cols + c
        b, d = a + 1, a + cols
        e = d + 1
        # wound counter-clockwise seen from where the normals point (the viewer culls back faces)
        flat, nflat = P.reshape(-1, 3), N.reshape(-1, 3)
        ia, ib, id_ = (a - base).ravel(), (b - base).ravel(), (d - base).ravel()
        g = np.cross(flat[id_] - flat[ia], flat[ib] - flat[ia])
        if np.sum(g * nflat[ia]) < 0:
            self.tri.append(np.stack([a, b, d, b, e, d], -1).reshape(-1, 3))
        else:
            self.tri.append(np.stack([a, d, b, b, d, e], -1).reshape(-1, 3))

    def count(self):
        return sum(len(p) for p in self.pos)

    def submesh(self):
        pos = np.concatenate(self.pos); nrm = np.concatenate(self.nrm); uv = np.concatenate(self.uv)
        tri = np.concatenate(self.tri).astype(np.int64)
        wj = np.concatenate([w[0] for w in self.w]); ww = np.concatenate([w[1] for w in self.w])
        assert len(pos) < 65536, len(pos)
        nrm = nrm / np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-9)
        lo, hi = pos.min(0) - 1e-3, pos.max(0) + 1e-3
        ulo, uhi = uv.min(0), np.maximum(uv.max(0), uv.min(0) + 1e-6)
        return {
            "Position": forest.q16(pos, lo, hi),
            "Normal": forest.q16(nrm, -1.0, 1.0),
            "TexCoord0": forest.q16(uv, ulo, uhi),
            "TriangleList": tri.astype("<u2").tobytes(),
            "PositionDomain": {"Min": [float(x) for x in lo], "Max": [float(x) for x in hi]},
            "TexCoord0Domain": {"Min": [float(x) for x in ulo], "Max": [float(x) for x in uhi]},
            "Weights": weights_bytes(wj, ww),
        }, lo, hi


def weights_bytes(wj, ww):
    """The viewer's weights: per vertex up to four (joint u8, weight u16), 0xFF ending a vertex with fewer."""
    out = bytearray()
    order = np.argsort(-ww, axis=1)
    for j, w, o in zip(wj, ww, order):
        n = 0
        for k in o:
            if w[k] <= 1e-4:
                continue
            out.append(int(j[k]))
            out += struct.pack("<H", int(round(min(w[k], 1.0) * 65535)))
            n += 1
        if n < 4:
            out.append(0xFF)
    return bytes(out)


def smooth(e0, e1, x):
    t = np.clip((x - e0) / (e1 - e0), 0, 1)
    return t * t * (3 - 2 * t)


def chain_weights(names, seg, t, joint_index):
    """Weights along a joint chain: a ring on segment seg at t belongs to the segment's start joint, blending half
    into the next joint toward the segment's end and half into the previous one near its start."""
    n = len(seg)
    last = len(names) - 2
    wj = np.zeros((n, 4), np.int64)
    ww = np.zeros((n, 4))
    for i in range(n):
        s, x = seg[i], t[i]
        a = joint_index(names[s]); b = joint_index(names[s + 1])
        # the chain's last point belongs wholly to its joint, as the next prim's first ring does
        wb = smooth(0.5, 1.0, x) * (1.0 if s == last else 0.5)
        wp = 0.5 * (1 - smooth(0.0, 0.5, x)) if s > 0 else 0
        p = joint_index(names[max(s - 1, 0)])
        wj[i] = [a, b, p, a]
        ww[i] = [1 - wb - wp, wb, wp, 0]
    return wj, ww


def sweep(points, names, radii, sides, rings, joint_index, noise=0.0, seed=0, ref=np.array([1.0, 0, 0]), flare=None):
    """A tube along a polyline of points (each with a joint name for weights and (rx, ry) radii): rings x sides
    vertices with grid normals. Returns per-ring arrays to cut into faces."""
    pts = np.asarray(points, float)
    seglen = np.linalg.norm(np.diff(pts, axis=0), axis=1)
    cum = np.concatenate([[0], np.cumsum(seglen)])
    s = np.linspace(0, cum[-1], rings)
    seg = np.clip(np.searchsorted(cum, s, side="right") - 1, 0, len(pts) - 2)
    t = (s - cum[seg]) / np.maximum(seglen[seg], 1e-9)
    centre = pts[seg] + (pts[seg + 1] - pts[seg]) * t[:, None]
    rad = np.asarray(radii, float)
    r = rad[seg] + (rad[seg + 1] - rad[seg]) * t[:, None]
    tan = pts[seg + 1] - pts[seg]
    tan /= np.linalg.norm(tan, axis=1, keepdims=True)
    n = ref[None, :] - tan * (tan @ ref)[:, None]
    bad = np.linalg.norm(n, axis=1) < 1e-3
    n[bad] = np.array([0, 0, 1.0])
    n /= np.linalg.norm(n, axis=1, keepdims=True)
    b = np.cross(tan, n)
    a = np.linspace(0, 2 * np.pi, sides + 1)
    rng = np.random.default_rng(seed)
    bumps = 1 + noise * np.sin(np.outer(np.linspace(0, rng.uniform(6, 14), rings), np.ones(sides + 1)) + 5 * np.cos(a)[None, :] * rng.uniform(0.5, 1.5))
    rx = r[:, 0:1] * bumps
    ry = r[:, 1:2] * bumps
    if flare is not None:
        rx = rx * flare[:, None]; ry = ry * flare[:, None]
    P = centre[:, None, :] + rx[..., None] * np.cos(a)[None, :, None] * n[:, None, :] + ry[..., None] * np.sin(a)[None, :, None] * b[:, None, :]
    N = np.cross(np.gradient(P, axis=0), np.gradient(P, axis=1))
    # the grid's winding makes these point inward on some chains: point them away from the axis
    out = P - centre[:, None, :]
    flip = np.sum(N * out, axis=2) < 0
    N[flip] *= -1
    U = np.stack(np.meshgrid(np.linspace(0, 1, rings), np.linspace(0, 1, sides + 1), indexing="ij")[::-1], -1)
    wj, ww = chain_weights(names, seg, t, joint_index)
    Wj = np.repeat(wj[:, None, :], sides + 1, 1)
    Ww = np.repeat(ww[:, None, :], sides + 1, 1)
    return P, N, U, Wj, Ww


def cut(P, N, U, Wj, Ww, faces):
    """Split a sweep's rings into consecutive faces (sharing their boundary ring)."""
    rings = P.shape[0]
    bounds = np.linspace(0, rings - 1, faces + 1).round().astype(int)
    out = []
    for f in range(faces):
        a, b = bounds[f], bounds[f + 1] + 1
        part = Part()
        part.grid(P[a:b], N[a:b], U[a:b], Wj[a:b], Ww[a:b])
        out.append(part)
    return out


def ellipsoid(centre, radii, lat, lon, joint_index, weights, seed=0, noise=0.03):
    th = np.linspace(0, np.pi, lat)[:, None]
    ph = np.linspace(0, 2 * np.pi, lon + 1)[None, :]
    rng = np.random.default_rng(seed)
    bump = 1 + noise * np.sin(th * rng.uniform(5, 9)) * np.cos(ph * rng.integers(2, 5))
    d = np.stack([np.sin(th) * np.cos(ph), np.sin(th) * np.sin(ph), np.cos(th) * np.ones_like(ph)], -1)
    P = np.asarray(centre) + d * np.asarray(radii) * bump[..., None]
    N = d / np.asarray(radii)
    U = np.stack(np.meshgrid(np.linspace(0, 1, lat), np.linspace(0, 1, lon + 1), indexing="ij")[::-1], -1)
    wj = np.zeros(P.shape[:2] + (4,), np.int64)
    ww = np.zeros(P.shape[:2] + (4,))
    z = (P[..., 2] - P[..., 2].min()) / max(np.ptp(P[..., 2]), 1e-6)
    for k, (name, f) in enumerate(weights):
        wj[..., k] = joint_index(name)
        ww[..., k] = f(z)
    ww /= np.maximum(ww.sum(-1, keepdims=True), 1e-9)
    return P, N, U, wj, ww


def cards(rng, n, place, size, seg, joint_index, joints):
    """Hair: n strands of quads hanging from points on the scalp, seg quads each, two-sided by doubling."""
    part = Part()
    for _ in range(n):
        root, down, side = place(rng)
        length = size * rng.uniform(0.7, 1.3)
        width = 0.012 + rng.uniform(0, 0.012)
        tt = np.linspace(0, 1, seg + 1)
        spine = root[None, :] + down[None, :] * (tt[:, None] * length) + np.array([0, 0, -0.02])[None, :] * tt[:, None] ** 2
        P = np.stack([spine - side * width, spine + side * width], 1)
        nrm = np.cross(down, side)
        N = np.broadcast_to(nrm, P.shape).copy()
        U = np.stack(np.meshgrid(tt, [0.0, 1.0], indexing="ij"), -1)[..., ::-1]
        wj = np.zeros(P.shape[:2] + (4,), np.int64)
        ww = np.zeros(P.shape[:2] + (4,))
        z = P[..., 2]
        for k, name in enumerate(joints):
            wj[..., k] = joint_index(name)
        # mHead above the neck's height, the neck and chest lower down (long hair moves with the shoulders)
        neck_z = J["mNeck"][2]
        ww[..., 0] = smooth(neck_z - 0.02, neck_z + 0.06, z)
        ww[..., 1] = np.clip(1 - ww[..., 0], 0, 1) * smooth(neck_z - 0.25, neck_z, z)
        ww[..., 2] = np.clip(1 - ww[..., 0] - ww[..., 1], 0, 1)
        part.grid(P, N, U, wj, ww)
        part.grid(P[:, ::-1], -N, U[:, ::-1], wj[:, ::-1], ww[:, ::-1])
    return part


def lods(make, fracs=(1.0, 0.5, 0.25, 0.1)):
    """make(frac) -> list of Parts per LOD; the asset with its skin over every joint the parts weight to."""
    # --full-skins: as many of SL's creators export them, every skin lists the skeleton's joints in the skeleton's
    # order whether the mesh weights to them or not (the first 110, the uploader's limit, then any other it uses), so
    # every worn mesh carries a palette that size and a body's pieces share one list
    names = list(J)[:110] if FULL_SKINS else []

    def joint_index(name):
        if name not in names:
            names.append(name)
        return names.index(name)

    blocks, lo, hi = {}, np.full(3, np.inf), np.full(3, -np.inf)
    for key, frac in zip(("high_lod", "medium_lod", "low_lod", "lowest_lod"), fracs):
        faces = []
        for part in make(frac, joint_index):
            sm, a, b = part.submesh()
            faces.append(sm)
            lo, hi = np.minimum(lo, a), np.maximum(hi, b)
        blocks[key] = zlib.compress(forest.llsd(faces), 6)
    ibm = []
    for n in names:
        p = J[n]
        ibm.append([1.0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 1.0, 0, -float(p[0]), -float(p[1]), -float(p[2]), 1.0])
    skin = {"joint_names": names, "inverse_bind_matrix": ibm,
            "bind_shape_matrix": [1.0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 1.0, 0, 0, 0, 0, 1.0]}
    blocks["skin"] = zlib.compress(forest.llsd(skin), 6)
    header, body, off = {}, b"", 0
    for name, blob in blocks.items():
        header[name] = {"offset": off, "size": len(blob)}
        body += blob
        off += len(blob)
    return forest.llsd(header) + body, (hi - lo)


# --- the outfits -------------------------------------------------------------------------------------------------

def body_prims(seed, slim):
    """The mesh body as a linkset: torso, arms, legs, hands, feet; each a prim of several alpha-cut faces."""
    k = slim
    torso = ([J["mPelvis"] - [0, 0, 0.13], J["mPelvis"], J["mTorso"], J["mChest"], J["mNeck"], J["mHead"] - [0, 0, 0.03]],
             ["mPelvis", "mPelvis", "mTorso", "mChest", "mNeck", "mNeck"],
             [(0.12, 0.17 * k), (0.12, 0.175 * k), (0.105, 0.15 * k), (0.12, 0.17 * k), (0.06, 0.06), (0.05, 0.05)])
    prims = {"torso": (torso, 96, 120, 8)}
    for side, s in (("Left", 1), ("Right", -1)):
        wrist = J[f"mWrist{side}"]
        prims[f"arm{side}"] = (([J[f"mCollar{side}"] + [0, 0, -0.02], J[f"mShoulder{side}"], J[f"mElbow{side}"], wrist],
                                [f"mCollar{side}", f"mShoulder{side}", f"mElbow{side}", f"mWrist{side}"],
                                [(0.05, 0.06), (0.052 * k, 0.05 * k), (0.04 * k, 0.04 * k), (0.03, 0.03)]), 56, 90, 6)
        prims[f"hand{side}"] = (([wrist, wrist + [0, s * 0.09, 0], wrist + [0, s * 0.18, -0.01]],
                                 [f"mWrist{side}", f"mWrist{side}", f"mWrist{side}"],
                                 [(0.02, 0.045), (0.018, 0.05), (0.008, 0.035)]), 40, 40, 4)
        prims[f"leg{side}"] = (([J[f"mHip{side}"] + [0, 0, 0.06], J[f"mHip{side}"], J[f"mKnee{side}"], J[f"mAnkle{side}"]],
                                [f"mHip{side}", f"mHip{side}", f"mKnee{side}", f"mAnkle{side}"],
                                [(0.09, 0.09 * k), (0.085, 0.08 * k), (0.055, 0.05), (0.04, 0.035)]), 64, 120, 6)
        prims[f"foot{side}"] = (([J[f"mAnkle{side}"], J[f"mFoot{side}"], J[f"mToe{side}"]],
                                 [f"mAnkle{side}", f"mFoot{side}", f"mToe{side}"],
                                 [(0.04, 0.035), (0.03, 0.045), (0.015, 0.035)]), 40, 40, 4)
    out = {}
    for name, ((pts, names, radii), sides, rings, faces) in prims.items():
        def make(frac, ji, pts=pts, names=names, radii=radii, sides=sides, rings=rings, faces=faces, name=name):
            ref = np.array([0, 0, 1.0]) if name.startswith(("arm", "hand")) else np.array([1.0, 0, 0])
            P = sweep(pts, names, radii, max(8, int(sides * frac ** 0.5)), max(6, int(rings * frac ** 0.5)), ji,
                      noise=0.04, seed=seed + len(name), ref=ref)
            return cut(*P, max(1, faces if frac >= 0.5 else faces // 2))
        out[name] = make
    return out


def head_make(seed):
    def make(frac, ji):
        c = J["mHead"] + np.array([0.02, 0, 0.075])
        P = ellipsoid(c, (0.105, 0.085, 0.12), max(12, int(150 * frac ** 0.5)), max(16, int(180 * frac ** 0.5)), ji,
                      [("mHead", lambda z: smooth(0.05, 0.25, z)), ("mNeck", lambda z: 1 - smooth(0.05, 0.25, z))], seed=seed)
        return cut(*P, 3)
    return make


def hair_make(seed, long):
    def make(frac, ji):
        rng = np.random.default_rng(seed)
        top = J["mHead"] + np.array([0.0, 0, 0.13])

        def place(r):
            a = r.uniform(0, 2 * np.pi)
            e = r.uniform(0.15, 1.2)
            d = np.array([np.cos(a) * np.sin(e), np.sin(a) * np.sin(e), np.cos(e)])
            root = J["mHead"] + np.array([0.02, 0, 0.075]) + d * np.array([0.112, 0.092, 0.127])
            down = np.array([d[0] * 0.35, d[1] * 0.35, -1.0])
            if d[0] > 0.5:   # bangs, shorter
                down = np.array([0.4, d[1] * 0.3, -0.6])
            down /= np.linalg.norm(down)
            side = np.cross(down, d)
            side /= max(np.linalg.norm(side), 1e-6)
            return root, down, side
        n = max(40, int(1400 * frac))
        parts = []
        per = (n + 2) // 3
        for f in range(3):
            parts.append(cards(rng, per, place, 0.45 if long else 0.2, max(3, int(10 * frac ** 0.5)), ji, ["mHead", "mNeck", "mChest"]))
        del top
        return parts
    return make


def dress_make(seed, kind):
    def make(frac, ji):
        if kind == "dress":
            pts = [J["mChest"] + [0, 0, 0.12], J["mChest"], J["mTorso"], J["mPelvis"], J["mPelvis"] - [0, 0, 0.25], J["mPelvis"] - [0, 0, 0.5]]
            names = ["mChest", "mChest", "mTorso", "mPelvis", "mPelvis", "mPelvis"]
            radii = [(0.13, 0.18), (0.13, 0.18), (0.115, 0.16), (0.135, 0.19), (0.2, 0.25), (0.27, 0.32)]
        else:
            pts = [J["mChest"] + [0, 0, 0.14], J["mChest"], J["mTorso"], J["mPelvis"], J["mPelvis"] - [0, 0, 0.1]]
            names = ["mChest", "mChest", "mTorso", "mPelvis", "mPelvis"]
            radii = [(0.135, 0.185), (0.135, 0.185), (0.12, 0.165), (0.135, 0.19), (0.14, 0.19)]
        P = sweep(pts, names, radii, max(12, int(110 * frac ** 0.5)), max(8, int(110 * frac ** 0.5)), ji, noise=0.05, seed=seed)
        parts = cut(*P, 3)
        if kind != "dress":
            for side in ("Left", "Right"):
                Q = sweep([J[f"mHip{side}"] + [0, 0, 0.07], J[f"mHip{side}"], J[f"mKnee{side}"], J[f"mAnkle{side}"] + [0, 0, 0.04]],
                          [f"mHip{side}", f"mHip{side}", f"mKnee{side}", f"mAnkle{side}"],
                          [(0.1, 0.1), (0.095, 0.09), (0.065, 0.06), (0.055, 0.05)], max(10, int(64 * frac ** 0.5)), max(8, int(100 * frac ** 0.5)), ji,
                          noise=0.06, seed=seed + 7)
                parts += cut(*Q, 1)
        return parts
    return make


def shoes_make(seed):
    def make(frac, ji):
        parts = []
        for side in ("Left", "Right"):
            P = sweep([J[f"mAnkle{side}"] + [0, 0, 0.05], J[f"mAnkle{side}"], J[f"mFoot{side}"], J[f"mToe{side}"] + [0.02, 0, 0]],
                      [f"mAnkle{side}", f"mAnkle{side}", f"mFoot{side}", f"mToe{side}"],
                      [(0.05, 0.045), (0.05, 0.045), (0.04, 0.055), (0.02, 0.045)], max(8, int(48 * frac ** 0.5)), max(6, int(48 * frac ** 0.5)), ji,
                      noise=0.03, seed=seed)
            parts += cut(*P, 1)
        return parts
    return make


OUTFITS = [
    {"skin": (0.86, 0.68, 0.56), "hair": (0.25, 0.15, 0.08), "long": True, "kind": "dress", "cloth": (0.6, 0.1, 0.2), "slim": 0.95},
    {"skin": (0.55, 0.38, 0.26), "hair": (0.05, 0.05, 0.05), "long": False, "kind": "top", "cloth": (0.15, 0.3, 0.6), "slim": 1.05},
    {"skin": (0.95, 0.82, 0.72), "hair": (0.85, 0.7, 0.35), "long": True, "kind": "top", "cloth": (0.2, 0.5, 0.25), "slim": 1.0},
]


# --- textures ----------------------------------------------------------------------------------------------------

def skin_tex(rgb, seed, size=512):
    rng = np.random.default_rng(seed)
    n = forest.noise(rng, size)
    return np.dstack([np.full((size, size), c) * (0.92 + 0.12 * n) for c in rgb] + [np.ones((size, size))])


def cloth_tex(rgb, seed, size=1024):
    rng = np.random.default_rng(seed)
    n = forest.noise(rng, size)
    yy, xx = np.mgrid[0:size, 0:size] / size
    weave = 0.5 + 0.5 * np.sin(xx * 300) * np.sin(yy * 300)
    return np.dstack([np.full((size, size), c) * (0.7 + 0.2 * weave + 0.2 * n) for c in rgb])


def hair_tex(rgb, seed, size=512):
    """Strands with soft alpha (the blended hair most SL hair is): a gradient across each card and along it."""
    rng = np.random.default_rng(seed)
    yy, xx = np.mgrid[0:size, 0:size] / size
    strands = 0.5 + 0.5 * np.sin(xx * rng.uniform(80, 140) + 3 * forest.noise(rng, size))
    a = np.clip((1 - np.abs(xx - 0.5) * 2) ** 0.6 * (0.6 + 0.4 * strands) * (1 - yy ** 3), 0, 1)
    rgb = np.dstack([np.full((size, size), c) * (0.6 + 0.6 * strands) for c in rgb])
    return np.dstack([rgb, a])


def plain_tex(rgb, size=256):
    return np.dstack([np.full((size, size), c) for c in rgb])


# --- objects and appearance --------------------------------------------------------------------------------------

def attachment_xml(name, prims, owner):
    """A linkset of rigged mesh prims (root first) as an object asset: what an NPC rezzes to wear."""
    root_id = uid("att", name, 0)
    parts = []
    for i, prim in enumerate(prims):
        # (name, rigged mesh, scale, te) or, for plain prims, (name, None, scale, te, offset, shape)
        pname, mesh, scale, te = prim[:4]
        offset, shape = (prim[4], prim[5]) if len(prim) > 4 else ((0, 0, 0), "box")
        pid = root_id if i == 0 else uid("att", name, i)
        xml = forest.part(pid, 1 + i, f"{name}:{pname}", (0, 0, 0), (0, 0, 0, 1), tuple(float(x) for x in scale), te, shape=shape, mesh=mesh,
                          link=(i + 1) if len(prims) > 1 else 0, offset=offset if i else (0, 0, 0), root_pos=(0, 0, 0))
        parts.append(xml.replace(forest.OWNER, owner))
    # object assets take OpenSim's original XML format (the root in RootPart, each child in a Part), not the OAR's Xml2
    return ("<SceneObjectGroup><RootPart>" + parts[0] + "</RootPart><OtherParts>"
            + "".join("<Part>" + p + "</Part>" for p in parts[1:]) + "</OtherParts></SceneObjectGroup>")


def beads(n, radius, texture, size, axis="z"):
    """A ring of n small prims (alternating spheres and cylinders) about the attachment point."""
    out = []
    for k in range(n):
        a = 2 * math.pi * k / n
        if axis == "z":
            off = (radius * math.cos(a) + 0.03, radius * math.sin(a), -0.02)
        else:
            off = (0.0, radius * math.cos(a), radius * math.sin(a))
        shape = "sphere" if k % 2 == 0 else "cylinder"
        out.append((f"bead{k}", None, (size, size, size * (1 if shape == "sphere" else 1.6)),
                    forest.te_bytes(texture, material=0x80), off, shape))
    return out


def notecard(text):
    body = text.encode()
    return (b"Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n}\nText length " + str(len(body)).encode() + b"\n" + body + b"}\n")


def appearance(textures, attachments):
    """An NPC appearance notecard (OpenSim's AvatarAppearance.Pack, as osAgentSaveAppearance writes it)."""
    def u(x):
        return f"<uuid>{x}</uuid>"
    tex = [DEFAULT_AVATAR] * 45
    for i, t in textures.items():
        tex[i] = t
    vp = bytes([127] * 218)
    import base64
    atts = "".join(f"<map><key>point</key><integer>{p}</integer><key>item</key>{u(uid('item', a))}<key>asset</key>{u(a)}</map>"
                   for p, a in attachments)
    wear = "".join("<array />" for _ in range(16))
    return ("<llsd><map><key>serial</key><integer>1</integer><key>height</key><real>1.75</real>"
            f"<key>wearables</key><array>{wear}</array>"
            f"<key>textures</key><array>{''.join(u(t) for t in tex)}</array>"
            f"<key>visualparams</key><binary encoding=\"base64\">{base64.b64encode(vp).decode()}</binary>"
            f"<key>attachments</key><array>{atts}</array></map></llsd>")


SCRIPT = """// Crowd spawner (tools/crowd.py): NPCs wearing the outfits in this prim's notecards, dancing or walking.
list npcs;
list walkers;
integer OUTFITS = %(outfits)d;
list DANCES = [%(dances)s];
vector CENTRE = %(centre)s;

spawn(integer n, float radius, float inner)
{
    integer i;
    npcs = llList2ListStrided(osGetNPCList(), 0, -1, 3);
    for (i = 0; i < n; i++)
    {
        float a = llFrand(TWO_PI);
        float r = inner + llSqrt(llFrand(1.0)) * (radius - inner);
        vector p = CENTRE + <r * llCos(a), r * llSin(a), 0>;
        p.z = llGround(p - llGetPos()) + 1.0;
        integer k = llGetListLength(npcs);
        key npc = osNpcCreate("Crowd", (string)(k + 1), p, "outfit" + (string)(k %% OUTFITS));
        npcs += npc;
        osNpcSetRot(npc, llEuler2Rot(<0, 0, llFrand(TWO_PI)>));
        osNpcPlayAnimation(npc, llList2String(DANCES, k %% llGetListLength(DANCES)));
    }
    llSetObjectDesc("crowd " + (string)llGetListLength(npcs));
}

clear()
{
    // every NPC in the region, not only this script's list (it doesn't survive a region restart)
    list all = osGetNPCList();
    integer i;
    for (i = 0; i < llGetListLength(all); i += 3) osNpcRemove(llList2Key(all, i));
    npcs = [];
    walkers = [];
    llSetTimerEvent(0);
}

dance()
{
    integer i;
    npcs = llList2ListStrided(osGetNPCList(), 0, -1, 3);
    for (i = 0; i < llGetListLength(npcs); i++)
    {
        key npc = llList2Key(npcs, i);
        osNpcStopMoveToTarget(npc);
        osNpcPlayAnimation(npc, llList2String(DANCES, i %% llGetListLength(DANCES)));
    }
    walkers = [];
    llSetTimerEvent(0);
}

walk()
{
    integer i;
    for (i = 0; i < llGetListLength(walkers); i++)
    {
        float a = llFrand(TWO_PI);
        float r = 3 + llFrand(11);
        vector p = CENTRE + <r * llCos(a), r * llSin(a), 0>;
        p.z = llGround(p - llGetPos()) + 1.0;
        osNpcMoveToTarget(llList2Key(walkers, i), p, OS_NPC_NO_FLY);
    }
}

default
{
    state_entry()
    {
        llListen(7, "", NULL_KEY, "");
        llSetObjectDesc("starting");
        llSetTimerEvent(0);
        llSleep(15);
        clear();
        spawn(40, 14, 3);
    }
    changed(integer c)
    {
        if (c & CHANGED_REGION_START) llResetScript();
    }
    listen(integer ch, string name, key id, string msg)
    {
        llSetObjectDesc("heard " + msg);
        list a = llParseString2List(msg, [" "], []);
        string c = llList2String(a, 0);
        if (c == "spawn") spawn((integer)llList2String(a, 1), (float)llList2String(a, 2), (float)llList2String(a, 3));
        else if (c == "clear") { clear(); llSetObjectDesc("cleared " + (string)llGetListLength(osGetNPCList())); }
        else if (c == "dance") dance();
        else if (c == "walk")
        {
            integer k = (integer)llList2String(a, 1);
            integer i;
            walkers = [];
            npcs = llList2ListStrided(osGetNPCList(), 0, -1, 3);
            for (i = 0; i < k && i < llGetListLength(npcs); i++)
            {
                key npc = llList2Key(npcs, i);
                osNpcStopAnimation(npc, llList2String(DANCES, i %% llGetListLength(DANCES)));
                walkers += npc;
            }
            walk();
            llSetTimerEvent(6);
        }
    }
    timer()
    {
        llSetObjectDesc("walking " + (string)llGetListLength(walkers));
        walk();
    }
}
"""


def build(detail):
    if sys.platform == "win32":
        import ctypes
        ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)
    OUT.mkdir(parents=True, exist_ok=True)
    assets = {}       # file name in the OAR -> bytes
    notecards = []
    t0 = time.time()
    tris = 0
    for oi, o in enumerate(OUTFITS):
        seed = 100 * (oi + 1)
        # every asset's id follows its bytes: OpenSim keeps the first asset an id ever had, so changed content needs new ids
        made = {
            "head": skin_tex(o["skin"], seed), "upper": skin_tex(o["skin"], seed + 1), "lower": skin_tex(o["skin"], seed + 2),
            "eyes": plain_tex((0.3, 0.4, 0.5), 128), "hair": hair_tex(o["hair"], seed + 3), "cloth": cloth_tex(o["cloth"], seed + 4),
            "trousers": cloth_tex((0.15, 0.15, 0.2), seed + 5), "jewel": cloth_tex((0.8, 0.7, 0.3), seed + 7, 256),
            "shoes": cloth_tex((0.1, 0.08, 0.06), seed + 6, 512),
        }
        tex = {}
        for k, (name, arr) in enumerate(made.items()):
            data = forest.encode(arr if name in ("eyes", "hair") else forest.grain(arr, seed + k))
            tex[name] = uid("tex", oi, name, zlib.crc32(data))
            assets[f"{tex[name]}_texture.jp2"] = data

        def mesh(name, make):
            nonlocal tris
            data, size = lods(make)
            mid = uid("mesh", oi, name, zlib.crc32(data))
            assets[f"{mid}_mesh.llmesh"] = data
            return mid, np.maximum(size, 0.05)

        def frac_make(m):
            return lambda frac, ji: m(min(1.0, frac * detail), ji)

        # the body: a prim per limb, its faces showing the bakes (upper on the torso and arms, lower on the legs and feet)
        body = []
        for name, make in body_prims(seed, o["slim"]).items():
            mid, size = mesh(f"body-{name}", frac_make(make))
            bake = BOM_LOWER if name.startswith(("leg", "foot")) else BOM_UPPER
            body.append((name, mid, size, forest.te_bytes(bake)))
        head_id, head_size = mesh("head", frac_make(head_make(seed)))
        hair_id, hair_size = mesh("hair", frac_make(hair_make(seed, o["long"])))
        cloth_id, cloth_size = mesh("cloth", frac_make(dress_make(seed, o["kind"])))
        shoes_id, shoes_size = mesh("shoes", frac_make(shoes_make(seed)))
        objs = [
            ("body", 2, body),
            ("head", 2, [("head", head_id, head_size, forest.te_bytes(BOM_HEAD))]),
            ("hair", 2, [("hair", hair_id, hair_size, forest.te_bytes(tex["hair"]))]),
            ("cloth", 6, [("cloth", cloth_id, cloth_size, forest.te_bytes(tex["cloth"], {3: tex["trousers"], 4: tex["trousers"]}, repeat=(4.0, 4.0)))]),
            ("shoes", 7, [("shoes", shoes_id, shoes_size, forest.te_bytes(tex["shoes"]))]),
            # jewellery as most of it is in SL: dozens of small plain prims, shiny (a necklace on the neck, bracelets)
            ("necklace", 39, beads(40, 0.075, tex["jewel"], 0.018)),
            ("bracelet-left", 21, beads(16, 0.045, tex["jewel"], 0.012, axis="x")),
            ("bracelet-right", 19, beads(16, 0.045, tex["jewel"], 0.012, axis="x")),
        ]
        attach = []
        for name, point, prims in objs:
            xml = attachment_xml(f"crowd{oi}-{name}", prims, ESTATE_OWNER).encode()
            aid = uid("object", oi, name, zlib.crc32(xml))
            assets[f"{aid}_object.xml"] = xml
            attach.append((point, aid))
        card = appearance({8: tex["head"], 9: tex["upper"], 10: tex["lower"], 11: tex["eyes"], 19: INVISIBLE, 20: INVISIBLE}, attach)
        nid = uid("notecard", oi, zlib.crc32(card.encode()))   # assets never change in place: a new text, a new id
        assets[f"{nid}_notecard.txt"] = notecard(card)
        notecards.append((f"outfit{oi}", nid))
        print(f"outfit {oi}: {time.time() - t0:.0f} s", flush=True)

    # the spawner: its script and the notecards
    import audio  # its task inventory helper
    part_id = uid("spawner")
    items = [audio.task_item(uid("item", n), a, n, 7, part_id, ESTATE_OWNER) for n, a in notecards]
    script = SCRIPT % {"outfits": len(OUTFITS), "dances": ", ".join(f'"dance{i + 1}"' for i in range(len(DANCES))),
                       "centre": f"<{CENTRE[0]:.1f}, {CENTRE[1]:.1f}, 0>"}
    script_id = uid("asset", "script", zlib.crc32(script.encode()))
    items.append(audio.task_item(uid("item", "script"), script_id, "crowd", 10, part_id, ESTATE_OWNER))
    assets[f"{script_id}_script.lsl"] = script.encode()
    te = forest.te_bytes("89556747-24cb-43ed-920b-47caed15465f", color=(0.3, 0.8, 0.9, 1))
    xml = forest.part(part_id, 1, "Crowd Spawner", (SPAWNER[0], SPAWNER[1], 26.0), (0, 0, 0, 1), (0.6, 0.6, 0.6), te)
    xml = xml.replace(forest.OWNER, ESTATE_OWNER).replace("<InventorySerial>0</InventorySerial>", "<InventorySerial>1</InventorySerial>")
    xml = xml.replace("</SceneObjectPart>", "<TaskInventory>" + "".join(items) + "</TaskInventory></SceneObjectPart>")
    path = OUT / OAR_NAME
    with tarfile.open(path, "w:gz", compresslevel=1) as tar:
        forest.add(tar, "archive.xml", forest.archive_xml())
        forest.add(tar, f"objects/Crowd_Spawner__{part_id}.xml", forest.group([xml]).encode())
        for name, data in assets.items():
            forest.add(tar, f"assets/{name}", data)
    print(f"{path}: {len(assets)} assets, {path.stat().st_size >> 20} MB, {time.time() - t0:.0f} s")


def load():
    import os
    import shutil
    os.environ["SOJOURN_GRID"] = "perf-grid"
    import grid
    ini = grid.BIN / "config" / "crowd.ini"
    ini.parent.mkdir(exist_ok=True)
    ini.write_text("; Written by tools/crowd.py: room for the crowd's NPCs.\n[NPC]\n    Enabled = true\n    MaxNumberNPCsPerScene = 120\n")
    print(grid.scene("light"))
    grid.console('change region "Atelier"', wait=1)
    print("\n".join(grid.console(f'load oar --merge "{(OUT / "crowd.oar").resolve()}"', wait=5)[-3:]))
    end = time.time() + 600
    while time.time() < end:
        log = (grid.GRID / "console.log").read_text(errors="replace")
        tail = log[log.rfind("Loading archive to region"):]
        if "Successfully loaded archive" in tail:
            break
        if "Aborting load" in tail:
            sys.exit(tail[:800])
        time.sleep(3)
    grid.stop_and_wait()
    dest = grid.GRID / "scenes" / "crowd"
    dest.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(grid.BIN / "OpenSim.db", dest / "OpenSim.db")
    print("saved as scene crowd;", "up" if grid.start() else "failed to restart")


def say(text, port=9725):
    import json
    import urllib.request
    body = json.dumps({"pump": "LLChatBar", "data": {"op": "sendChat", "message": text, "channel": 7, "type": "shout"}, "timeout": 10}).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}/cmd", data=body, headers={"Content-Type": "application/json"})
    print(urllib.request.urlopen(req, timeout=20).read().decode())


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "build"
    if cmd == "build":
        build(float(sys.argv[sys.argv.index("--detail") + 1]) if "--detail" in sys.argv else 1.0)
    elif cmd == "load":
        load()
    elif cmd == "say":
        say(sys.argv[2], int(sys.argv[sys.argv.index("--api") + 1]) if "--api" in sys.argv else 9725)
