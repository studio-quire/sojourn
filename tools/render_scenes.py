"""Rendering test scenes on the local grid (python tools/render_scenes.py ...): mirrors, mist, flowers, particles.

  python tools/render_scenes.py mirror     a platform 60 m over Atelier 64,64 with mirrors made as SL's creators make
                                           them, and the single plywood prim flagged as a mirror the user tried
  python tools/render_scenes.py mist       a platform 60 m over Atelier 96,64 with House of Legends-like mist: flat
                                           blended planes, layered, their texture drifting, and particle mist
  python tools/render_scenes.py flowers    a platform 60 m over Atelier 128,64: a dense field of blended flower cards
                                           around where you stand, and a table of glass cases and domes (the close-up
                                           layered alpha of Mirromere's flower field and the Papercut cafe, for GPU cost)
  python tools/render_scenes.py particles  a platform 60 m over Atelier 160,64: fountains of bouncing and homing
                                           particles, long-lived, near the viewer's 4096 (their CPU cost)
  python tools/render_scenes.py lights     49 point lights over the flower field (the local-light pass's cost)
  python tools/render_scenes.py statue     a platform 60 m over Atelier 224,64: shapes whose texture has an alpha channel
                                           that's all opaque (older SL uploads; Natoma's statue), a torus showing
                                           its inside if drawn blended
  python tools/render_scenes.py club       a room 60 m over Atelier 192,64 lit like a club: 100 coloured point lights
                                           and 48 projectors (Envision had 116 and 55 in view; the light passes' cost)
  python tools/render_scenes.py flexi      a platform 150 m up over Atelier 176,224 of flexible prims: a flag in the wind,
                                           strands held out level with different gravity and tension, a curtain of 48
                                           hanging strips (the cost of many), a rail swung back and forth by keyframed
                                           motion with strips hanging from it (flexies following what they hang from)
  python tools/render_scenes.py water      two open boats floating in the sea off Atelier's west shore at 64,128 (the
                                           water's at 20 m): the north one's hull holds a water exclusion prim (an
                                           invisiprim box, as "Hide water" makes it), the south one's doesn't
  python tools/render_scenes.py shore      a bank sloping into the sea off Atelier's west shore at 86,130 (the water's at
                                           20 m), like Mirromere's ponds: linksets of blended grass cards on it (big
                                           objects, so their bounding spheres reach the water), a blended cover across
                                           the water line, blended weed under the water, lily pads on it (the pre-water
                                           alpha pool's cost and picture; bench views in tools/shore-views.txt)
  python tools/render_scenes.py remove     takes them all away

Each object is its own OAR group with a script that sets what OAR XML can't easily carry (the reflection probe, the
specular map, texture animation, particles) as a creator's script would, in state_entry. Loaded with
`load oar --merge`; older versions (by name) are deleted first. SOJOURN_GRID=perf-grid puts them on the perf grid.

Mirrors (llheroprobemanager.cpp, and the community tutorials): a mirror is a glossy surface (legacy: specular
glossiness and environment 255; PBR: roughness 0) inside a box reflection probe flagged as a mirror, the probe turned
so its +Z points out of the surface and its centre plane just in front of it (a mirror's reflection is clipped at
that plane: a surface behind it isn't in its own reflection, and still takes the reflection up to ~4 cm back). The probe prim itself is made phantom and
invisible, as the viewer's build floater does when a prim becomes a probe. "Dynamic" probes reflect avatars too.

Written by Claude Opus 5.5, 2026-10-26.
"""
import math
import sys
import tarfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import forest  # noqa: E402
import grid  # noqa: E402

OUT = ROOT / "local" / "render-scenes"
TAG = "tools/render_scenes.py 7"
PLYWOOD = "89556747-24cb-43ed-920b-47caed15465f"
BLANK = "5748decc-f629-461c-9a36-a35a221fe21f"
MIST_TEX = forest.uid("render scenes", "mist texture")
FLOWER_TEX = forest.uid("render scenes", "flower texture")
BRONZE_TEX = forest.uid("render scenes", "bronze texture")

PROBE_BOX, PROBE_DYNAMIC, PROBE_MIRROR = 1, 2, 4


def q_axis(axis, deg):
    s = math.sin(math.radians(deg) / 2)
    return (axis[0] * s, axis[1] * s, axis[2] * s, math.cos(math.radians(deg) / 2))


def obj(name, pos, scale, rot=(0, 0, 0, 1), tex=PLYWOOD, color=(1, 1, 1, 1), shape="box", script="", solid=False, children=()):
    """children: (pos, rot, scale, tex, color, shape) linked to it, in world terms; the root unrotated."""
    return dict(name=name, pos=pos, scale=scale, rot=rot, tex=tex, color=color, shape=shape, script=script, solid=solid,
                children=list(children))


def probe_script(flags, extra=""):
    # PRIM_REFLECTION_PROBE (44): on, ambiance, clip distance, flags; then phantom and invisible, as the build floater does
    return f"llSetPrimitiveParams([44, TRUE, 0.0, 0.0, {flags}, PRIM_PHANTOM, TRUE, PRIM_COLOR, ALL_SIDES, <1,1,1>, 0.0{extra}]);"


GLASS = f'llSetPrimitiveParams([36, ALL_SIDES, "{BLANK}", <1,1,0>, <0,0,0>, 0.0, <1,1,1>, 255, 255]);'   # PRIM_SPECULAR


def mirror_objects():
    x0, y0, z0 = 64.0, 64.0, 60.0
    out = [obj("Mirror Test Floor", (x0, y0, z0 - 0.1), (12, 12, 0.2), color=(0.7, 0.7, 0.72, 1), solid=True)]
    # two wall mirrors facing -y (toward the middle), 5 m north: left static (the room only), right dynamic (avatars)
    face_south = q_axis((1, 0, 0), 90)   # the probe's +Z turned to -y
    for name, x, flags in (("Mirror Test Static", x0 - 1.6, PROBE_BOX | PROBE_MIRROR),
                           ("Mirror Test Dynamic", x0 + 1.6, PROBE_BOX | PROBE_MIRROR | PROBE_DYNAMIC)):
        y = y0 + 5.0
        out.append(obj(name + " Glass", (x, y, z0 + 1.3), (2.6, 0.05, 2.6), tex=BLANK, color=(0.95, 0.95, 0.95, 1), script=GLASS))
        # the probe: its centre plane 1 cm in front of the glass's front face (-y side at y - 0.025), so the glass is
        # clipped out of its own reflection and still well within the plane's falloff (tapHeroProbe)
        out.append(obj(name, (x, y - 0.035, z0 + 1.3), (2.6, 2.6, 0.6), rot=face_south, tex=BLANK, script=probe_script(flags)))
    # one facing west on the east edge: a login faces east, so the headset (which can't be turned by the API) starts
    # looking at it from the middle
    out.append(obj("Mirror Test East Glass", (x0 + 5.5, y0 + 2.0, z0 + 1.3), (0.05, 2.6, 2.6), tex=BLANK, color=(0.95, 0.95, 0.95, 1), script=GLASS))
    out.append(obj("Mirror Test East", (x0 + 5.5 - 0.035, y0 + 2.0, z0 + 1.3), (2.6, 2.6, 0.6), rot=q_axis((0, 1, 0), -90), tex=BLANK,
                   script=probe_script(PROBE_BOX | PROBE_MIRROR | PROBE_DYNAMIC)))
    # the user's attempt: one plywood cube flagged as a mirror (the viewer draws it as plywood: nothing glossy inside)
    out.append(obj("Mirror Test Plywood", (x0 + 5.0, y0 - 5.0, z0 + 0.5), (1, 1, 1), script=probe_script(PROBE_BOX | PROBE_MIRROR).replace(
        ", PRIM_PHANTOM, TRUE, PRIM_COLOR, ALL_SIDES, <1,1,1>, 0.0", "")))
    # things to see in them: behind the camera, beside it
    out.append(obj("Mirror Test Red Ball", (x0, y0 - 3.0, z0 + 0.6), (1.2, 1.2, 1.2), tex=BLANK, color=(0.85, 0.1, 0.1, 1), shape="sphere"))
    out.append(obj("Mirror Test Blue Post", (x0 - 3.0, y0 - 1.0, z0 + 1.0), (0.4, 0.4, 2.0), tex=BLANK, color=(0.15, 0.3, 0.9, 1), shape="cylinder"))
    out.append(obj("Mirror Test Yellow Crate", (x0 + 2.5, y0 - 2.0, z0 + 0.4), (0.8, 0.8, 0.8), color=(1, 0.85, 0.2, 1)))
    out.append(obj("Mirror Test Glass Pane", (x0 + 1.0, y0 - 1.0, z0 + 1.2), (1.2, 0.02, 1.6), tex=BLANK, color=(0.3, 0.9, 0.5, 0.45)))
    # particles (a dynamic mirror shows them, a static one doesn't): one fountain in front of each wall mirror, and one
    # behind where you stand, seen only in the mirrors
    sparks = ("llParticleSystem([PSYS_PART_FLAGS, PSYS_PART_EMISSIVE_MASK | PSYS_PART_INTERP_COLOR_MASK, "
              "PSYS_SRC_PATTERN, PSYS_SRC_PATTERN_ANGLE_CONE, PSYS_SRC_ANGLE_BEGIN, 0.0, PSYS_SRC_ANGLE_END, 0.4, "
              "PSYS_SRC_BURST_RATE, 0.05, PSYS_SRC_BURST_PART_COUNT, 2, PSYS_PART_MAX_AGE, 2.5, "
              "PSYS_SRC_BURST_SPEED_MIN, 0.8, PSYS_SRC_BURST_SPEED_MAX, 1.2, PSYS_SRC_ACCEL, <0,0,-0.6>, "
              "PSYS_PART_START_SCALE, <0.12, 0.12, 0>, PSYS_PART_START_COLOR, <1,0.8,0.2>, PSYS_PART_END_COLOR, <1,0.2,0.6>, "
              "PSYS_PART_START_ALPHA, 1.0, PSYS_PART_END_ALPHA, 0.2]);")
    for name, pos in (("Mirror Test Sparks Static", (x0 - 1.6, y0 + 3.2, z0 + 0.3)), ("Mirror Test Sparks Dynamic", (x0 + 1.6, y0 + 3.2, z0 + 0.3)),
                      ("Mirror Test Sparks Behind", (x0 + 0.8, y0 - 2.0, z0 + 0.3))):
        out.append(obj(name, pos, (0.15, 0.15, 0.15), tex=BLANK, color=(0.9, 0.7, 0.2, 1), shape="sphere", script=sparks))
    return out


def mist_objects():
    x0, y0, z0 = 96.0, 64.0, 60.0
    out = [obj("Mist Test Floor", (x0, y0, z0 - 0.1), (16, 16, 0.2), color=(0.35, 0.33, 0.3, 1), solid=True)]
    # flat blended planes, layered (the creator's sort: several thin boxes, drifting texture, fullbright)
    anim = "llSetTextureAnim(ANIM_ON | SMOOTH | LOOP, ALL_SIDES, 1, 1, 0.0, 1.0, 0.02);"
    for i, h in enumerate((0.35, 0.8, 1.25)):
        out.append(obj(f"Mist Test Layer {i + 1}", (x0, y0, z0 + h), (14, 14, 0.01), tex=MIST_TEX, color=(1, 1, 1, 0.8),
                       script=anim + "llSetPrimitiveParams([PRIM_FULLBRIGHT, ALL_SIDES, TRUE]);"))
    # and particle mist: big soft quads drifting low
    parts = ("llParticleSystem([PSYS_PART_FLAGS, PSYS_PART_INTERP_COLOR_MASK | PSYS_PART_INTERP_SCALE_MASK, "
             "PSYS_SRC_PATTERN, PSYS_SRC_PATTERN_EXPLODE, PSYS_SRC_BURST_RADIUS, 3.0, PSYS_SRC_BURST_RATE, 0.3, "
             "PSYS_SRC_BURST_PART_COUNT, 2, PSYS_PART_MAX_AGE, 8.0, PSYS_SRC_BURST_SPEED_MIN, 0.05, PSYS_SRC_BURST_SPEED_MAX, 0.15, "
             f'PSYS_SRC_TEXTURE, "{MIST_TEX}", PSYS_PART_START_SCALE, <3.0, 1.6, 0>, PSYS_PART_END_SCALE, <4.0, 2.0, 0>, '
             "PSYS_PART_START_COLOR, <1,1,1>, PSYS_PART_END_COLOR, <1,1,1>, PSYS_PART_START_ALPHA, 0.5, PSYS_PART_END_ALPHA, 0.0]);")
    out.append(obj("Mist Test Emitter", (x0 + 4.0, y0 + 3.0, z0 + 0.6), (0.2, 0.2, 0.2), tex=BLANK, color=(1, 1, 1, 0), script=parts))
    # a pane and a post to judge sorting against
    out.append(obj("Mist Test Post", (x0 - 1.5, y0 + 2.0, z0 + 1.0), (0.3, 0.3, 2.0), tex=BLANK, color=(0.8, 0.2, 0.2, 1), shape="cylinder"))
    out.append(obj("Mist Test Pane", (x0 + 1.5, y0 + 2.5, z0 + 1.0), (1.4, 0.02, 2.0), tex=BLANK, color=(0.2, 0.4, 1.0, 0.5)))
    return out


def flower_objects():
    import random
    rnd = random.Random(11)
    x0, y0, z0 = 128.0, 64.0, 60.0
    out = [obj("Flower Test Floor", (x0, y0, z0 - 0.1), (16, 16, 0.2), color=(0.3, 0.42, 0.22, 1), solid=True)]
    # clumps of two crossed cards, 0.35-0.6 m tall, 10 x 10 m around the middle (a clear spot where you stand); a row
    # is a linkset whose root is a small hidden prim
    for row in range(20):
        cards = []
        for k in range(20):
            x, y = x0 - 5 + row * 0.5 + rnd.uniform(-0.2, 0.2), y0 - 5 + k * 0.5 + rnd.uniform(-0.2, 0.2)
            if abs(x - x0) < 0.6 and abs(y - y0) < 0.6:
                continue
            h = rnd.uniform(0.35, 0.6)
            a = rnd.uniform(0, 180)
            for turn in (0, 90):
                cards.append(((x, y, z0 + h / 2), q_axis((0, 0, 1), a + turn), (h * 1.1, 0.005, h), FLOWER_TEX, (1, 1, 1, 1), "box"))
        out.append(obj(f"Flower Test Row {row + 1}", (x0 - 5 + row * 0.5, y0 - 5.5, z0 + 0.05), (0.05, 0.05, 0.05), tex=BLANK,
                       color=(1, 1, 1, 0), children=cards))
    # glass cases and domes on a table, at arm's length
    tx, ty = x0 + 0.9, y0 + 0.2
    glass = [((tx, ty, z0 + 0.8 + s / 2), (0, 0, 0, 1), (s, s * 0.8, s), BLANK, (0.8, 0.9, 1.0, 0.18), "box") for s in (0.9, 0.75, 0.6, 0.45)]
    glass += [((tx + dx, ty - 0.55, z0 + 0.95), (0, 0, 0, 1), (0.3, 0.3, 0.3), BLANK, (0.9, 0.95, 1.0, 0.22), "sphere") for dx in (-0.35, 0.0, 0.35)]
    out.append(obj("Flower Test Table", (tx, ty - 0.15, z0 + 0.4), (1.2, 1.4, 0.8), color=(0.55, 0.4, 0.3, 1), solid=True, children=glass))
    # the worst case seen close up: a dozen flowered panes one behind the other filling the view west of the middle
    # (stand at the middle facing -x): layered blended overdraw
    panes = [((x0 - 1.2 - i * 0.25, y0, z0 + 1.0), (0, 0, 0, 1), (0.01, 3.0, 2.0), FLOWER_TEX, (1, 1, 1, 1), "box") for i in range(1, 12)]
    out.append(obj("Flower Test Panes", (x0 - 1.2, y0, z0 + 1.0), (0.01, 3.0, 2.0), tex=FLOWER_TEX, children=panes))
    return out


def particle_objects():
    x0, y0, z0 = 160.0, 64.0, 60.0
    out = [obj("Particle Test Floor", (x0, y0, z0 - 0.1), (12, 12, 0.2), color=(0.4, 0.4, 0.45, 1), solid=True)]
    # PSYS_PART_BOUNCE_MASK and PSYS_PART_TARGET_POS_MASK have no closed form: the client steps each one frame by frame
    common = ("PSYS_SRC_BURST_RATE, 0.05, PSYS_SRC_BURST_PART_COUNT, 4, PSYS_PART_MAX_AGE, 20.0, "
              "PSYS_PART_START_SCALE, <0.08, 0.08, 0>, PSYS_PART_START_ALPHA, 0.9, PSYS_PART_END_ALPHA, 0.3, ")
    bounce = ("llParticleSystem([PSYS_PART_FLAGS, PSYS_PART_BOUNCE_MASK | PSYS_PART_EMISSIVE_MASK, PSYS_SRC_PATTERN, PSYS_SRC_PATTERN_EXPLODE, "
              "PSYS_SRC_BURST_SPEED_MIN, 0.5, PSYS_SRC_BURST_SPEED_MAX, 2.0, PSYS_SRC_ACCEL, <0,0,-2.0>, " + common +
              "PSYS_PART_START_COLOR, <0.3,0.8,1.0>]);")
    homing = ("llParticleSystem([PSYS_PART_FLAGS, PSYS_PART_TARGET_POS_MASK | PSYS_PART_EMISSIVE_MASK, PSYS_SRC_PATTERN, PSYS_SRC_PATTERN_EXPLODE, "
              "PSYS_SRC_TARGET_KEY, llGetKey(), PSYS_SRC_BURST_RADIUS, 2.5, PSYS_SRC_BURST_SPEED_MIN, 0.2, PSYS_SRC_BURST_SPEED_MAX, 0.6, " + common +
              "PSYS_PART_START_COLOR, <1.0,0.6,0.2>]);")
    for i, (dx, dy) in enumerate(((-3, 3), (3, 3), (0, -3))):
        out.append(obj(f"Particle Test Bounce {i + 1}", (x0 + dx, y0 + dy, z0 + 1.0), (0.2, 0.2, 0.2), tex=BLANK, color=(0.3, 0.8, 1, 1), shape="sphere", script=bounce))
    out.append(obj("Particle Test Homing", (x0, y0 + 4, z0 + 1.5), (0.2, 0.2, 0.2), tex=BLANK, color=(1, 0.6, 0.2, 1), shape="sphere", script=homing))
    return out


def light_objects():
    """49 point lights over the flower field (a lantern-lit garden or a cafe's bulbs): the deferred local-light pass's
    cost per light per pixel."""
    x0, y0, z0 = 128.0, 64.0, 60.0
    bulbs = [((x0 - 6 + i * 2, y0 - 6 + j * 2, z0 + 2.6), (0, 0, 0, 1), (0.12, 0.12, 0.12), BLANK, (1, 0.9, 0.7, 1), "sphere")
             for i in range(7) for j in range(7) if (i, j) != (3, 3)]
    script = "llSetLinkPrimitiveParamsFast(LINK_SET, [PRIM_POINT_LIGHT, TRUE, <1,0.85,0.6>, 1.0, 6.0, 0.75, PRIM_FULLBRIGHT, ALL_SIDES, TRUE]);"
    return [obj("Light Test Bulbs", (x0, y0, z0 + 2.6), (0.12, 0.12, 0.12), tex=BLANK, color=(1, 0.9, 0.7, 1), shape="sphere",
                script=script, children=bulbs)]


def club_objects():
    """A dark room lit like a club (Envision, 2026-10-27: 116 point lights and 55 projectors in view): coloured bulbs
    along the walls and over the floor, projectors in the ceiling throwing a pattern down, a bar and some pillars to
    catch the light. Stand in the middle."""
    import random
    rnd = random.Random(7)
    x0, y0, z0 = 192.0, 64.0, 60.0
    grey = (0.35, 0.35, 0.38, 1)
    out = [obj("Club Test Floor", (x0, y0, z0 - 0.1), (20, 20, 0.2), color=(0.25, 0.25, 0.28, 1), solid=True, children=[
        ((x0, y0, z0 + 5.1), (0, 0, 0, 1), (20, 20, 0.2), PLYWOOD, (0.2, 0.2, 0.22, 1), "box"),
        ((x0 + 10, y0, z0 + 2.5), (0, 0, 0, 1), (0.2, 20, 5), PLYWOOD, grey, "box"),
        ((x0 - 10, y0, z0 + 2.5), (0, 0, 0, 1), (0.2, 20, 5), PLYWOOD, grey, "box"),
        ((x0, y0 + 10, z0 + 2.5), (0, 0, 0, 1), (20, 0.2, 5), PLYWOOD, grey, "box"),
        ((x0, y0 - 10, z0 + 2.5), (0, 0, 0, 1), (20, 0.2, 5), PLYWOOD, grey, "box"),
        ((x0 + 6, y0 + 2, z0 + 0.55), (0, 0, 0, 1), (1.0, 6, 1.1), PLYWOOD, (0.5, 0.3, 0.2, 1), "box"),
    ] + [((x0 + px, y0 + py, z0 + 2.5), (0, 0, 0, 1), (0.6, 0.6, 5), PLYWOOD, (0.7, 0.7, 0.75, 1), "cylinder")
         for px, py in ((-4, -4), (-4, 4), (4, -4), (4, 4))])]
    # 100 bulbs in four linksets of 25, each its own colour, radius 3-8 m
    colors = ["<1,0.2,0.6>", "<0.2,0.5,1>", "<0.3,1,0.4>", "<1,0.7,0.2>"]
    for g in range(4):
        bulbs = []
        for k in range(25):
            x, y = x0 + rnd.uniform(-9.5, 9.5), y0 + rnd.uniform(-9.5, 9.5)
            bulbs.append(((x, y, z0 + rnd.uniform(0.5, 4.8)), (0, 0, 0, 1), (0.1, 0.1, 0.1), BLANK, (1, 1, 1, 1), "sphere"))
        root = bulbs.pop(0)
        radius = 3 + g * 1.6
        script = (f"llSetLinkPrimitiveParamsFast(LINK_SET, [PRIM_POINT_LIGHT, TRUE, {colors[g]}, 1.0, {radius:.1f}, 0.75, "
                  f"PRIM_FULLBRIGHT, ALL_SIDES, TRUE, PRIM_COLOR, ALL_SIDES, {colors[g]}, 1.0]);")
        out.append(obj(f"Club Test Bulbs {g + 1}", root[0], root[2], tex=BLANK, shape="sphere", script=script, children=bulbs))
    # 48 projectors in the ceiling (PRIM_PROJECTOR: the flower texture, 1.0 rad, focus 0, ambiance 0.1), pointing down
    # (a projector shines along its prim's -Z), radius 8 m
    spots = [((x0 - 8.75 + i * 2.5, y0 - 8.75 + j * 3.5, z0 + 4.9), (0, 0, 0, 1), (0.15, 0.15, 0.15), BLANK, (1, 1, 1, 1), "box")
             for i in range(8) for j in range(6)]
    root = spots.pop(0)
    script = ("integer i; for (i = 1; i <= llGetNumberOfPrims(); i++) llSetLinkPrimitiveParamsFast(i, [PRIM_POINT_LIGHT, TRUE, "
              "<0.5 + 0.5 * llSin(i), 0.5 + 0.5 * llSin(i * 2.1), 0.5 + 0.5 * llSin(i * 3.7)>, 1.0, 8.0, 0.75, "
              f'42, "{FLOWER_TEX}", 1.0, 0.0, 0.1]);')
    out.append(obj("Club Test Projectors", root[0], root[2], tex=BLANK, script=script, children=spots))
    return out


def statue_objects():
    """Shapes textured with an RGBA texture whose alpha is all 255. The viewer masks those (analyzeAlphaData), so they
    draw opaque with depth; drawn blended, a concave shape shows its own far side through its near side."""
    x0, y0, z0 = 224.0, 64.0, 60.0
    torus = ("llSetPrimitiveParams([PRIM_TYPE, PRIM_TYPE_TORUS, PRIM_HOLE_DEFAULT, <0,1,0>, 0.0, <0,0,0>, <1,0.25,0>, "
             "<0,0,0>, <0,1,0>, <0,0,0>, 0.0, 1.0, 0.0]);")
    return [
        obj("Statue Test Floor", (x0, y0, z0 - 0.1), (10, 10, 0.2), color=(0.6, 0.6, 0.62, 1), solid=True),
        obj("Statue Test Torus", (x0 + 3, y0, z0 + 1.2), (2.0, 2.0, 2.0), rot=q_axis((1, 0, 0), 60), tex=BRONZE_TEX, script=torus),
        obj("Statue Test Box", (x0 + 3, y0 - 2.5, z0 + 0.6), (1.0, 1.0, 1.2), tex=BRONZE_TEX),
    ]


FLEXIBLE = 21   # PRIM_FLEXIBLE: on, softness, gravity, drag, wind, tension, force
DOWN = (1.0, 0.0, 0.0, 0.0)   # the prim's +Z pointing down: it hangs from its top


INVISIPRIM = "e97cf410-8e61-7005-ec06-629eba4cd1fb"   # IMG_ALPHA_GRAD: what the build floater's "Hide water" sets


def water_objects():
    """Open boats at the water line: floor 0.6 m under the water, sides 0.8 m over it."""
    out = []
    for name, y, hide in (("Water Test Boat Hidden", 131.0, True), ("Water Test Boat Open", 124.0, False)):
        x0, z0 = 64.0, 20.1
        hull = (0.55, 0.35, 0.2, 1)
        kids = [((x0, y - 1.25, z0), (0, 0, 0, 1), (6.0, 0.1, 1.4), BLANK, hull, "box"),
                ((x0, y + 1.25, z0), (0, 0, 0, 1), (6.0, 0.1, 1.4), BLANK, hull, "box"),
                ((x0 - 2.95, y, z0), (0, 0, 0, 1), (0.1, 2.4, 1.4), BLANK, hull, "box"),
                ((x0 + 2.95, y, z0), (0, 0, 0, 1), (0.1, 2.4, 1.4), BLANK, hull, "box")]
        if hide:
            # the exclusion box fills the hull from its floor to 0.3 m over the water
            kids.append(((x0, y, 19.95), (0, 0, 0, 1), (5.8, 2.4, 0.7), INVISIPRIM, (1, 1, 1, 1), "box"))
        out.append(obj(name, (x0, y, z0 - 0.65), (6.0, 2.6, 0.1), tex=BLANK, color=hull, solid=True, children=kids))
    return out


def shore_objects():
    """A bank from 19 m at x 76 to 23 m at x 96, 40 m along y, sloping into the sea (docs/rendering.md > Blended or
    masked: the pre-water pool)."""
    import random
    rnd = random.Random(7)
    y0 = 130.0

    def ground(x):
        return 19.0 + (x - 76.0) * 0.2

    slope = q_axis((0, 1, 0), -math.degrees(math.atan(0.2)))   # +x up the bank
    out = [obj("Shore Test Bank", (86.0, y0, ground(86.0) - 0.26), (20.4, 40.0, 0.5), rot=slope,
               color=(0.32, 0.3, 0.22, 1), solid=True)]
    # a blended cover over the bank, across the water line (a creator's mud and moss decal)
    out.append(obj("Shore Test Cover", (86.0, y0, ground(86.0) + 0.02), (20.4, 40.0, 0.01), rot=slope, tex=BLANK,
                   color=(0.35, 0.42, 0.25, 0.55)))
    # grass: strips 4 m wide up the bank, each a linkset of crossed cards (an SL grass mesh's reach)
    for strip in range(10):
        cards = []
        for k in range(100):
            x, y = rnd.uniform(77.0, 95.0), y0 - 20 + strip * 4 + rnd.uniform(0.2, 3.8)
            h = rnd.uniform(0.6, 1.1)
            a = rnd.uniform(0, 180)
            for turn in (0, 90):
                cards.append(((x, y, ground(x) + h / 2), q_axis((0, 0, 1), a + turn), (h * 2.0, 0.005, h), FLOWER_TEX, (0.7, 1, 0.6, 1), "box"))
        out.append(obj(f"Shore Test Grass {strip + 1}", (86.0, y0 - 18 + strip * 4, ground(86.0)), (0.05, 0.05, 0.05), tex=BLANK,
                       color=(1, 1, 1, 0), children=cards))
    # weed under the water off the bank's foot, and lily pads on the surface
    for half in range(2):
        weed = []
        for k in range(40):
            x, y = rnd.uniform(66.0, 76.0), y0 - 20 + half * 20 + rnd.uniform(0, 20)
            h = rnd.uniform(1.0, 2.2)
            weed.append(((x, y, 19.85 - h / 2), q_axis((0, 0, 1), rnd.uniform(0, 180)), (h * 0.6, 0.005, h), FLOWER_TEX, (0.4, 0.8, 0.5, 1), "box"))
        out.append(obj(f"Shore Test Weed {half + 1}", (71.0, y0 - 10 + half * 20, 18.0), (0.05, 0.05, 0.05), tex=BLANK,
                       color=(1, 1, 1, 0), children=weed))
    pads = [((rnd.uniform(64.0, 77.0), rnd.uniform(y0 - 18, y0 + 18), 20.03), (0, 0, 0, 1), (r, r, 0.01), FLOWER_TEX, (0.6, 0.9, 0.5, 0.9), "cylinder")
            for r in (rnd.uniform(0.4, 1.2) for _ in range(40))]
    out.append(obj("Shore Test Pads", (70.0, y0, 20.03), (0.05, 0.05, 0.05), tex=BLANK, color=(1, 1, 1, 0), children=pads))
    return out


def flexi_objects():
    """Flexible prims (llflexibleobject.cpp): each set by its root's script with PRIM_FLEXIBLE, which makes the path
    flexible as the viewer's checkbox does."""
    x0, y0, z0 = 176.0, 224.0, 150.0
    def flex(link, soft=2, grav=0.3, drag=2.0, wind=0.0, tension=1.0, force=(0, 0, 0)):
        return (f"llSetLinkPrimitiveParamsFast({link}, [{FLEXIBLE}, TRUE, {soft}, {grav}, {drag}, {wind}, {tension}, "
                f"<{force[0]},{force[1]},{force[2]}>]);")
    out = [obj("Flexi Test Floor", (x0, y0, z0 - 0.1), (14, 14, 0.2), color=(0.55, 0.57, 0.6, 1), solid=True)]
    # a flag on a pole: its Z along +X, blowing in the region's wind
    out.append(obj("Flexi Test Flag", (x0 - 5, y0 + 5, z0 + 1.6), (0.08, 0.08, 3.2), shape="cylinder", color=(0.8, 0.8, 0.8, 1),
                   script=flex(2, soft=3, grav=0.3, drag=1.0, wind=6.0, tension=1.5),
                   children=[((x0 - 5 + 0.8, y0 + 5, z0 + 2.7), q_axis((0, 1, 0), 90), (1.0, 0.02, 1.6), BLANK, (0.8, 0.15, 0.15, 1), "box")]))
    # strands held out level from a bar: gravity up the row, tension down it
    strands, lsl = [], []
    for k in range(8):
        y = y0 - 3 + k * 0.8
        strands.append(((x0 - 2 + 0.75, y, z0 + 1.5), q_axis((0, 1, 0), 90), (0.06, 0.06, 1.5), BLANK, (0.2 + k * 0.1, 0.5, 0.9 - k * 0.08, 1), "cylinder"))
        lsl.append(flex(k + 2, soft=k % 4, grav=0.3 + k * 1.2, drag=2.0, tension=max(0.5, 8 - k)))
    out.append(obj("Flexi Test Strands", (x0 - 2, y0 - 0.2, z0 + 1.5), (0.1, 6.8, 0.1), color=(0.4, 0.4, 0.42, 1), script=" ".join(lsl), children=strands))
    # a curtain: 48 strips hanging from a rail, for the cost of many (a skirt or a head of prim hair is 20-60)
    strips = []
    for k in range(48):
        y = y0 - 5 + k * 0.21
        strips.append(((x0 + 3, y, z0 + 2.6 - 1.0), DOWN, (0.03, 0.2, 2.0), BLANK, (0.9, 0.85 - (k % 6) * 0.08, 0.6, 1), "box"))
    out.append(obj("Flexi Test Curtain", (x0 + 3, y0, z0 + 2.65), (0.1, 10.5, 0.1), color=(0.4, 0.4, 0.42, 1),
                   script=flex("LINK_ALL_CHILDREN", soft=2, grav=0.3, drag=2.0, wind=1.0, tension=1.0), children=strips))
    # a rail swung 4 m back and forth by keyframed motion, three strips hanging from it like a cape
    cape = [((x0 + 6, y0 + 3 + d, z0 + 2.4 - 0.8), DOWN, (0.4, 0.02, 1.6), BLANK, (0.2, 0.25, 0.7, 1), "box") for d in (-0.45, 0, 0.45)]
    out.append(obj("Flexi Test Swinging Rail", (x0 + 6, y0 + 3, z0 + 2.45), (0.1, 1.5, 0.1), color=(0.4, 0.4, 0.42, 1),
                   script=flex("LINK_ALL_CHILDREN", soft=2, grav=0.3, drag=2.0, tension=1.0)
                   + " llSetKeyframedMotion([<0,-4,0>, 1.6, <0,4,0>, 1.6], [KFM_MODE, KFM_LOOP, KFM_DATA, KFM_TRANSLATION]);", children=cape))
    return out


def bronze_texture():
    """A bronze-like texture saved with an alpha channel that's 255 everywhere."""
    import numpy as np
    rng = np.random.default_rng(3)
    size = 256
    base = np.array([0.45, 0.32, 0.18], np.float32)
    rgb = base * rng.uniform(0.7, 1.2, (size, size, 1)).astype(np.float32)
    return forest.encode(np.dstack([np.clip(rgb, 0, 1), np.ones((size, size), np.float32)]))


def flower_texture():
    """Petals and stems on transparent, with soft (blended) edges and translucent petals: the viewer won't mask it."""
    import numpy as np
    rng = np.random.default_rng(5)
    size = 256
    yy, xx = np.mgrid[0:size, 0:size] / size
    a = np.zeros((size, size), np.float32)
    rgb = np.zeros((size, size, 3), np.float32)
    for _ in range(9):
        cx = rng.uniform(0.1, 0.9)
        stem = np.clip(1 - np.abs(xx - cx - 0.05 * np.sin(yy * 9)) / 0.012, 0, 1) * (yy > rng.uniform(0.25, 0.5))
        a = np.maximum(a, stem * 0.95)
        rgb = np.where(stem[..., None] > 0.1, np.array([0.2, 0.45, 0.15]), rgb)
        bx, by, r = cx, rng.uniform(0.15, 0.45), rng.uniform(0.05, 0.11)
        d = np.sqrt((xx - bx) ** 2 + (yy - by) ** 2)
        ang = np.arctan2(yy - by, xx - bx)
        petal = np.clip((r * (0.75 + 0.25 * np.cos(5 * ang)) - d) / 0.02, 0, 1)
        col = [np.array([0.95, 0.85, 0.2]), np.array([0.9, 0.3, 0.5]), np.array([0.6, 0.4, 0.95]), np.array([1.0, 1.0, 1.0])][int(rng.integers(0, 4))]
        rgb = np.where(petal[..., None] > 0.05, col, rgb)
        a = np.maximum(a, petal * 0.85)
    return forest.encode(np.dstack([rgb, a]))


def mist_texture():
    """Soft white mist with blended alpha, tiling (noise summed over wrapped octaves)."""
    import numpy as np
    rng = np.random.default_rng(7)
    size = 256
    acc = np.zeros((size, size), np.float32)
    for k, amp in ((4, 1.0), (8, 0.5), (16, 0.25), (32, 0.12)):
        g = rng.random((k, k)).astype(np.float32)
        # bilinear upsample with wrap, so the texture tiles as it drifts
        yy, xx = np.mgrid[0:size, 0:size] * (k / size)
        x0, y0 = np.floor(xx).astype(int), np.floor(yy).astype(int)
        fx, fy = xx - x0, yy - y0
        x1, y1 = (x0 + 1) % k, (y0 + 1) % k
        x0, y0 = x0 % k, y0 % k
        v = (g[y0, x0] * (1 - fx) * (1 - fy) + g[y0, x1] * fx * (1 - fy) + g[y1, x0] * (1 - fx) * fy + g[y1, x1] * fx * fy)
        acc += amp * v
    acc /= acc.max()
    a = np.clip((acc - 0.25) * 0.9, 0, 0.6)
    rgb = np.ones((size, size, 3), np.float32) * 0.92
    return forest.encode(np.dstack([rgb, a]))


def build_oar(name, objects):
    import audio
    OUT.mkdir(parents=True, exist_ok=True)
    out = OUT / f"{name}.oar"
    with tarfile.open(out, "w:gz") as tar:
        forest.add(tar, "archive.xml", forest.archive_xml())
        for i, o in enumerate(objects):
            pid = forest.uid("render scenes", TAG, o["name"])
            te = forest.te_bytes(o["tex"], color=o["color"])
            xml = forest.part(pid, 9000 + i, o["name"], o["pos"], o["rot"], o["scale"], te, shape=o["shape"], link=1 if o["children"] else 0)
            xml = xml.replace("<Description />", f"<Description>{TAG}</Description>")
            if o["solid"]:
                xml = xml.replace("<Flags>Phantom</Flags>", "<Flags>None</Flags>")
            if o["script"]:
                asset = forest.uid("render scenes", TAG, o["name"], "script")
                item = audio.task_item(forest.uid("render scenes", TAG, o["name"], "item"), asset, "setup", 10, pid, forest.OWNER)
                xml = xml.replace("<InventorySerial>0</InventorySerial>", "<InventorySerial>1</InventorySerial>")
                xml = xml.replace("</SceneObjectPart>", "<TaskInventory>" + item + "</TaskInventory></SceneObjectPart>")
                lsl = "default\n{\n    state_entry()\n    {\n        " + o["script"] + "\n    }\n    on_rez(integer p) { llResetScript(); }\n}\n"
                forest.add(tar, f"assets/{asset}_script.lsl", lsl.encode())
            parts = [xml]
            for k, (cpos, crot, cscale, ctex, ccolor, cshape) in enumerate(o["children"]):
                cid = forest.uid("render scenes", TAG, o["name"], k)
                off = tuple(c - r for c, r in zip(cpos, o["pos"]))   # the root is unrotated: its frame is the region's
                child = forest.part(cid, 20000 + i * 100 + k, o["name"], cpos, crot, cscale, forest.te_bytes(ctex, color=ccolor),
                                    shape=cshape, link=k + 2, offset=off, root_pos=o["pos"])
                parts.append(child.replace("<Description />", f"<Description>{TAG}</Description>"))
            forest.add(tar, f"objects/{o['name'].replace(' ', '_')}__{pid}.xml", forest.group(parts).encode())
        if name == "mist":
            forest.add(tar, f"assets/{MIST_TEX}_texture.jp2", mist_texture())
        if name in ("flowers", "club", "shore"):
            forest.add(tar, f"assets/{FLOWER_TEX}_texture.jp2", flower_texture())
        if name == "statue":
            forest.add(tar, f"assets/{BRONZE_TEX}_texture.jp2", bronze_texture())
    return out


def in_world(names):
    import sqlite3
    c = sqlite3.connect(f"file:{grid.BIN / 'OpenSim.db'}?mode=ro", uri=True)
    try:
        q = f"select Name, Description from prims where Name in ({','.join('?' * len(names))})"
        return c.execute(q, names).fetchall()
    finally:
        c.close()


def remove(names):
    grid.console('change region "Atelier"', wait=1)
    # one delete sometimes leaves a copy behind: again until the region's store has none
    for _ in range(4):
        left = sorted({n for n, _ in in_world(names)})
        if not left:
            return
        for n in left:
            grid.console(f'delete object name "{n}"', wait=1.5, answers=("y",))
        grid.console("backup", wait=3)


def load(name, objects):
    """Objects are counted by name (a linkset's prims share its name)."""
    names = [o["name"] for o in objects]
    prims = sum(1 + len(o["children"]) for o in objects)
    have = in_world(names)
    if len(have) == prims and all(d == TAG for _, d in have):
        print(f"{name}: already there")
        return 0
    remove(names)
    path = build_oar(name, objects).resolve()
    grid.console('change region "Atelier"', wait=1)
    print("\n".join(grid.console(f'load oar --merge "{path}"', wait=5)[-2:]))
    end = time.time() + 60
    while time.time() < end and len(in_world(names)) < prims:
        grid.console("backup", wait=3)
        time.sleep(2)
    n = len(in_world(names))
    print(f"{name}: {n} of {prims} prims in the region")
    return 0 if n == prims else 1


if __name__ == "__main__":
    what = sys.argv[1] if len(sys.argv) > 1 else ""
    if what == "mirror":
        sys.exit(load("mirror", mirror_objects()))
    if what == "mist":
        sys.exit(load("mist", mist_objects()))
    if what == "flowers":
        sys.exit(load("flowers", flower_objects()))
    if what == "lights":
        sys.exit(load("lights", light_objects()))
    if what == "particles":
        sys.exit(load("particles", particle_objects()))
    if what == "statue":
        sys.exit(load("statue", statue_objects()))
    if what == "club":
        sys.exit(load("club", club_objects()))
    if what == "flexi":
        sys.exit(load("flexi", flexi_objects()))
    if what == "water":
        sys.exit(load("water", water_objects()))
    if what == "shore":
        sys.exit(load("shore", shore_objects()))
    if what == "remove":
        remove([o["name"] for o in mirror_objects() + mist_objects() + flower_objects() + particle_objects() + light_objects() + club_objects()
                + statue_objects() + flexi_objects() + water_objects() + shore_objects()])
        sys.exit(0)
    sys.exit(__doc__)
