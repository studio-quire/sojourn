"""Describing's quality and cost by model and setting (docs/assistant-cost.md > Descriptions): captures of what
SojournDescribe hands the model (tools/costlab.py capture), replayed straight to the Messages API through the studio
relay at each model and setting, then judged blind against ground truth.

  python tools/describe_lab.py scene [remove]
                                   a platform 90 m over Atelier 200,200 whose contents are known exactly
                                   (scene_objects, scene_images): a poster, a shop sign and a rules board with text,
                                   things of plain shapes and colours, one unnamed; loaded as an OAR (render_scenes.py's
                                   way) and removed by the description its objects carry, never by name
  python tools/describe_lab.py replay ARM [CAPTURE...]
                                   every capture (local/costlab/captures/*) or those named, through ARM (ARMS below:
                                   model, effort, picture size and count; the instructions are describer.py's), to
                                   local/costlab/replays/ARM/CAPTURE.json with the text, usage and dollars
  python tools/describe_lab.py table
                                   dollars, tokens and seconds per arm, from the replays
  python tools/describe_lab.py judge ARM_A ARM_B [CAPTURE...]
                                   Opus, blind (A and B shuffled per capture), against the capture's pictures, facts
                                   and ground truth (truth.md beside it): each description's errors and inventions
                                   counted and quoted, names, text read, coverage, register, then which a blind
                                   person would rather have; to local/costlab/judged/ARM_A--ARM_B.jsonl
  python tools/describe_lab.py show CAPTURE [ARM...]
                                   the descriptions side by side

Run with the assistant's venv (anthropic, pillow) for replay and judge, and Python with numpy and pillow for scene. A
capture's truth.md is written by hand from what's known of it (for the platform, exact). The relay's token is
costlab's (tools/relay.py), read where it's kept and never written out. Written by Claude Opus 5.5, 2026-10-31.
"""
import base64
import io
import json
import random
import sys
import tarfile
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(ROOT / "assistant"))

LAB = ROOT / "local" / "costlab"
CAPTURES = LAB / "captures"
REPLAYS = LAB / "replays"
JUDGED = LAB / "judged"
SCENE = LAB / "scene"
RELAY = "https://relay.studioquire.org"


# ---- the scene with known contents ----------------------------------------------------------------------------------

TAG = "tools/describe_lab.py 1"
BLANK = "5748decc-f629-461c-9a36-a35a221fe21f"
PLYWOOD = "89556747-24cb-43ed-920b-47caed15465f"
FLOOR_Z = 90.0
CX, CY = 200.0, 200.0


def scene_objects():
    """(name, shape, pos, scale, colour, {face: texture name}) in region terms; boxes' face 1 looks south (-Y)."""
    z = FLOOR_Z
    cream, brown, white = (0.93, 0.90, 0.82, 1), (0.42, 0.26, 0.14, 1), (1, 1, 1, 1)
    return [
        ("Lab Floor", "box", (CX, CY, z - 0.25), (16, 16, 0.5), white, {"all": "plywood"}),
        ("Lab Wall", "box", (CX, CY + 6, z + 1.75), (12, 0.3, 3.5), cream, {}),
        ("Jazz Poster", "box", (CX - 2, CY + 5.83, z + 1.6), (1.0, 0.04, 1.5), white, {1: "poster"}),
        ("Tea House Sign", "box", (CX + 2.5, CY + 5.83, z + 2.9), (2.4, 0.04, 0.6), white, {1: "shopsign"}),
        ("Tea House Door", "box", (CX + 2.5, CY + 5.82, z + 1.0), (1.1, 0.06, 2.0), brown, {}),
        ("Pool Rules", "box", (CX - 4.5, CY + 5.83, z + 1.5), (1.0, 0.04, 1.0), white, {1: "rules"}),
        ("Red Beach Ball", "sphere", (CX - 1, CY + 2, z + 0.3), (0.6, 0.6, 0.6), (0.85, 0.1, 0.1, 1), {}),
        ("Blue Barrel", "cylinder", (CX + 1, CY + 2.5, z + 0.5), (0.6, 0.6, 1.0), (0.1, 0.25, 0.8, 1), {}),
        ("Garden Table", "cylinder", (CX + 3, CY + 1.5, z + 0.75), (1.0, 1.0, 0.06), white, {}),
        ("Garden Table Leg", "cylinder", (CX + 3, CY + 1.5, z + 0.36), (0.1, 0.1, 0.72), white, {}),
        ("Yellow Box", "box", (CX + 3, CY + 1.5, z + 0.88), (0.2, 0.2, 0.2), (0.95, 0.85, 0.1, 1), {}),
        ("Object", "box", (CX - 3, CY + 1, z + 0.25), (0.5, 0.5, 0.5), (0.15, 0.6, 0.2, 1), {}),
    ]


RULES = ["1. No running on the deck.", "2. Children under 12 must be with an adult.", "3. No glass bottles past the gate.",
         "4. Shower before entering the water.", "5. Lifeguard on duty 10 AM - 6 PM.", "6. Diving only at the deep end (3 m).",
         "7. No pets in the pool area.", "8. Report accidents to staff at once."]


def scene_images():
    """The three text panels' images (Windows' Arial), exactly as the captures' truth.md files describe them."""
    from PIL import Image, ImageDraw, ImageFont

    def font(size, bold=False):
        return ImageFont.truetype("arialbd.ttf" if bold else "arial.ttf", size)
    poster = Image.new("RGB", (512, 768), (24, 32, 72))
    d = ImageDraw.Draw(poster)
    d.ellipse((156, 330, 356, 530), fill=(236, 120, 40))
    d.rectangle((0, 430, 512, 768), fill=(14, 20, 48))
    d.text((256, 90), "SUNSET JAZZ", font=font(64, True), fill=(250, 214, 120), anchor="mm")
    d.text((256, 170), "Live at the Driftwood Pavilion", font=font(30), fill=(240, 240, 240), anchor="mm")
    d.text((256, 560), "Fridays 8 PM SLT", font=font(44, True), fill=(250, 250, 250), anchor="mm")
    d.text((256, 625), "Free entry - Dress: smart casual", font=font(26), fill=(210, 210, 230), anchor="mm")
    d.text((256, 735), "Sponsored by Harbour Lights Gallery", font=font(14), fill=(150, 150, 180), anchor="mm")
    sign = Image.new("RGB", (1024, 256), (250, 244, 230))
    d = ImageDraw.Draw(sign)
    d.rectangle((8, 8, 1015, 247), outline=(40, 90, 60), width=10)
    d.text((560, 100), "Mira's Tea House", font=font(84, True), fill=(40, 90, 60), anchor="mm")
    d.text((560, 195), "Loose-leaf teas  -  Cakes  -  Est. 2019", font=font(34), fill=(120, 80, 50), anchor="mm")
    d.ellipse((60, 80, 180, 190), fill=(200, 60, 60))
    d.rectangle((170, 110, 215, 125), fill=(200, 60, 60))
    d.rectangle((95, 60, 145, 82), fill=(200, 60, 60))
    rules = Image.new("RGB", (512, 512), (245, 245, 240))
    d = ImageDraw.Draw(rules)
    d.text((256, 40), "POOL RULES", font=font(40, True), fill=(20, 20, 120), anchor="mm")
    for i, r in enumerate(RULES):
        d.text((30, 95 + i * 26), r, font=font(15), fill=(30, 30, 30))
    d.text((256, 470), "Management: Bluewater Leisure Ltd.", font=font(11), fill=(90, 90, 90), anchor="mm")
    return {"poster": poster, "shopsign": sign, "rules": rules}


def scene_textures():
    """The text panels' images as J2C, by name (a copy of each as PNG in local/costlab/scene)."""
    import numpy as np

    import forest
    out = {}
    SCENE.mkdir(parents=True, exist_ok=True)
    for name, im in scene_images().items():
        im.save(SCENE / f"{name}.png")
        out[name] = (forest.uid("describe lab", TAG, name), forest.encode(np.asarray(im, np.float32) / 255))
    return out


def scene_in_world():
    """Our objects in the region's store, by the description they carry (never by name alone: "Object" is anyone's)."""
    import sqlite3

    import grid
    c = sqlite3.connect(f"file:{grid.BIN / 'OpenSim.db'}?mode=ro", uri=True)
    try:
        return c.execute("select UUID, Name from prims where Description = ?", (TAG,)).fetchall()
    finally:
        c.close()


def scene_remove():
    import grid
    grid.console('change region "Atelier"', wait=1)
    for _ in range(4):
        left = scene_in_world()
        if not left:
            return
        for uid, _ in left:
            grid.console(f"delete object id {uid}", wait=1.0, answers=("y",))
        grid.console("backup", wait=3)


def scene(remove_only=False):
    import forest
    import grid
    objs = scene_objects()
    names = [o[0] for o in objs]
    scene_remove()
    if remove_only:
        print("removed" if not scene_in_world() else f"left: {scene_in_world()}")
        return 0
    tex = scene_textures()
    path = (SCENE / "describe-lab.oar").resolve()
    with tarfile.open(path, "w:gz") as tar:
        forest.add(tar, "archive.xml", forest.archive_xml())
        for i, (name, shape, pos, scale, colour, faces) in enumerate(objs):
            pid = forest.uid("describe lab", TAG, name)
            default = PLYWOOD if faces.get("all") == "plywood" else BLANK
            face_tex = {f: tex[t][0] for f, t in faces.items() if f != "all"}
            te = forest.te_bytes(default, face_tex=face_tex, color=colour)
            xml = forest.part(pid, 9500 + i, name, pos, (0, 0, 0, 1), scale, te, shape=shape)
            xml = xml.replace("<Description />", f"<Description>{TAG}</Description>").replace("<Flags>Phantom</Flags>", "<Flags>None</Flags>")
            forest.add(tar, f"objects/{name.replace(' ', '_')}__{pid}.xml", forest.group([xml]).encode())
        for tid, data in tex.values():
            forest.add(tar, f"assets/{tid}_texture.jp2", data)
    grid.console('change region "Atelier"', wait=1)
    print("\n".join(grid.console(f'load oar --merge "{path}"', wait=5)[-2:]))
    end = time.time() + 60
    while time.time() < end and len(scene_in_world()) < len(names):
        grid.console("backup", wait=3)
        time.sleep(2)
    n = len(scene_in_world())
    print(f"{n} of {len(names)} objects in the region; stand at Atelier/{CX:.0f}/{CY - 3:.0f}/{FLOOR_Z + 1:.0f} facing north")
    return 0 if n == len(names) else 1


# ---- replaying captures -----------------------------------------------------------------------------------------------

# model, effort, the pictures' longest side (None: as drawn), how many pictures (None: all)
ARMS = {
    "haiku-low": ("claude-haiku-5-5", "low", None, None),
    "haiku-medium": ("claude-haiku-5-5", "medium", None, None),
    "haiku-high": ("claude-haiku-5-5", "high", None, None),
    "haiku-medium-512": ("claude-haiku-5-5", "medium", 512, None),
    "sonnet-low": ("claude-sonnet-5-5", "low", None, None),
    "sonnet-medium": ("claude-sonnet-5-5", "medium", None, None),
    "opus-low": ("claude-opus-5-5", "low", None, None),
    "opus-medium": ("claude-opus-5-5", "medium", None, None),
    "opus-high": ("claude-opus-5-5", "high", None, None),
    "opus-low-512": ("claude-opus-5-5", "low", 512, None),
    # after the instructions' second version (clock sides spelled out, no talk of pictures, a check before answering)
    "haiku-medium-v2": ("claude-haiku-5-5", "medium", None, None),
    "haiku-high-v2": ("claude-haiku-5-5", "high", None, None),
    "sonnet-low-v2": ("claude-sonnet-5-5", "low", None, None),
    "opus-low-v2": ("claude-opus-5-5", "low", None, None),
    "opus-medium-v2": ("claude-opus-5-5", "medium", None, None),
}
INTRO = {"view": "What there is to describe it from:", "texture": "A texture (an image as uploaded to Second Life).",
         "profile": "Their profile pictures."}


def client():
    import anthropic
    import relay
    return anthropic.Anthropic(base_url=RELAY, api_key=relay.read_secret(relay.token_file("costlab")), max_retries=3,
                               timeout=300)


def png_b64(path, size):
    from PIL import Image
    im = Image.open(path)
    if size and max(im.size) > size:
        k = size / max(im.size)
        im = im.resize((round(im.width * k), round(im.height * k)), Image.LANCZOS)
        buf = io.BytesIO()
        im.save(buf, "PNG")
        return base64.b64encode(buf.getvalue()).decode()
    return base64.b64encode(Path(path).read_bytes()).decode()


def load_capture(name, size=None, count=None):
    d = CAPTURES / name
    look = json.loads((d / "look.json").read_text(encoding="utf-8"))
    pics = look.get("pictures") or []
    if count:
        pics = pics[:count]
    for p in pics:
        if p.get("file"):
            p["png"] = png_b64(d / p.pop("file"), size)
    look["pictures"] = pics
    of = (look.pop("captured", None) or {}).get("of", "view")
    return look, INTRO.get(of, INTRO["view"])


def brief_of(name):
    """A rich capture's brief counterpart (lab-here-rich -> lab-here), whose description it adds to."""
    return name[:-5] if name.endswith("-rich") else None


def replay(cl, arm, name):
    import costlab
    import describer
    model, effort, size, count = ARMS[arm]
    out = REPLAYS / arm / f"{name}.json"
    if out.exists():
        return json.loads(out.read_text(encoding="utf-8"))
    look, intro = load_capture(name, size, count)
    detail = look.get("detail") or "brief"
    before = None
    if brief_of(name) and (REPLAYS / arm / f"{brief_of(name)}.json").exists():
        before = json.loads((REPLAYS / arm / f"{brief_of(name)}.json").read_text(encoding="utf-8"))["text"]
    t0 = time.time()
    r = cl.messages.create(model=model, max_tokens=16000, system=describer.SYSTEM,
                           messages=describer.messages(look, intro, detail, before),
                           output_config={"effort": effort})
    took = time.time() - t0
    text = " ".join(b.text for b in r.content if b.type == "text").strip()
    usage = r.usage.to_dict() if hasattr(r.usage, "to_dict") else dict(r.usage)
    rec = {"arm": arm, "capture": name, "model": model, "effort": effort, "size": size, "count": count, "text": text,
           "stop": r.stop_reason, "usage": usage, "usd": costlab.price_of(model, usage), "s": round(took, 1), "before": before}
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(rec, indent=1, ensure_ascii=False), encoding="utf-8")
    return rec


def captures(names):
    every = sorted(p.name for p in CAPTURES.iterdir() if (p / "look.json").exists())
    chosen = names or every
    # briefs before their rich counterparts, so "more" can add to what the same arm said
    return sorted(chosen, key=lambda n: (n.endswith("-rich"), n))


def replay_all(arm, names):
    cl = client()
    for name in captures(names):
        try:
            r = replay(cl, arm, name)
            print(f"{arm} {name}: ${r['usd']:.4f} {r['s']}s in {r['usage'].get('input_tokens')} out {r['usage'].get('output_tokens')}: "
                  f"{r['text'][:160]}")
        except Exception as e:
            print(f"{arm} {name}: {type(e).__name__}: {str(e)[:300]}")


def table():
    rows = []
    for d in sorted(REPLAYS.iterdir()):
        recs = [json.loads(f.read_text(encoding="utf-8")) for f in d.glob("*.json")]
        if not recs:
            continue
        n = len(recs)
        brief = [r for r in recs if not r["capture"].endswith("-rich")]
        rich = [r for r in recs if r["capture"].endswith("-rich")]

        def avg(rs, f):
            return sum(f(r) for r in rs) / len(rs) if rs else 0
        rows.append((d.name, n, avg(recs, lambda r: r["usage"]["input_tokens"]), avg(recs, lambda r: r["usage"]["output_tokens"]),
                     avg(brief, lambda r: r["usd"]), avg(rich, lambda r: r["usd"]), avg(recs, lambda r: r["s"]),
                     avg(brief, lambda r: len(r["text"].split())), avg(rich, lambda r: len(r["text"].split()))))
    print(f"{'arm':<18}{'n':>3}{'in':>7}{'out':>6}{'$ brief':>10}{'$ rich':>9}{'s':>6}{'words b':>9}{'words r':>9}")
    for r in rows:
        print(f"{r[0]:<18}{r[1]:>3}{r[2]:>7.0f}{r[3]:>6.0f}{r[4]:>10.5f}{r[5]:>9.5f}{r[6]:>6.1f}{r[7]:>9.0f}{r[8]:>9.0f}")


def show(name, arms):
    arms = arms or sorted(p.name for p in REPLAYS.iterdir())
    truth = CAPTURES / name / "truth.md"
    if truth.exists():
        print("TRUTH: " + truth.read_text(encoding="utf-8").strip() + "\n")
    for arm in arms:
        f = REPLAYS / arm / f"{name}.json"
        if f.exists():
            r = json.loads(f.read_text(encoding="utf-8"))
            print(f"--- {arm} (${r['usd']:.4f}, {r['s']}s)\n{r['text']}\n")


# ---- judging, blind ---------------------------------------------------------------------------------------------------

JUDGE_MODEL = "claude-opus-5-5"
JUDGE_SYSTEM = (
    "You check descriptions written for a blind Second Life resident. They can't see the scene and can't check what "
    "they're told, so a wrong or invented detail is worse than a missing one. You get what the describer got (the "
    "pictures, each with what's visible in it by name, and what the viewer's lists know), the ground truth where the "
    "scene is known exactly, what was asked (brief: one to three sentences, the gist; rich: fuller, adding to a brief "
    "description given before), and two descriptions, A and B, in no particular order.\n\n"
    "For each, list every problem, quoting the words:\n"
    "- errors: anything that contradicts the pictures, the facts or the ground truth (a wrong colour, shape, count, "
    "direction, distance, a misread word);\n"
    "- inventions: specific details that nothing given supports (a thing that isn't there, a material or text made up), "
    "as opposed to fair plain inferences;\n"
    "- names: a named person or thing called something else or left generic when the facts name it, or a name given to "
    "something the facts don't name;\n"
    "- text: signs and labels read wrong, or unreadable text filled in instead of said to be unreadable;\n"
    "- tooling: talk of pictures, cameras, lists, tools or percentages instead of the thing itself;\n"
    "- missed: what stands out that a person would want to know and it leaves out (for brief, only the most important).\n"
    "Mark each error or invention major (it would mislead them about something they'd care about or act on) or minor. "
    "Then score coverage (1-5: how much of what matters it conveys for the detail asked) and register (1-5: length and "
    "plainness right for a screen reader, natural to listen to, the asked length), and say which a blind person would "
    "rather be given, A or B, or tie when there's no real difference. Be exact and fair; judge only what's written."
)


def judge_schema():
    item = {"type": "object", "properties": {"quote": {"type": "string"}, "why": {"type": "string"},
                                              "severity": {"type": "string", "enum": ["major", "minor"]}},
            "required": ["quote", "why", "severity"], "additionalProperties": False}
    plain = {"type": "object", "properties": {"quote": {"type": "string"}, "why": {"type": "string"}},
             "required": ["quote", "why"], "additionalProperties": False}
    one = {"type": "object", "properties": {
        "errors": {"type": "array", "items": item}, "inventions": {"type": "array", "items": item},
        "names": {"type": "array", "items": plain}, "text": {"type": "array", "items": plain},
        "tooling": {"type": "array", "items": plain}, "missed": {"type": "array", "items": {"type": "string"}},
        "coverage": {"type": "integer"}, "register": {"type": "integer"}},
        "required": ["errors", "inventions", "names", "text", "tooling", "missed", "coverage", "register"],
        "additionalProperties": False}
    return {"type": "object", "properties": {"a": one, "b": one, "prefer": {"type": "string", "enum": ["A", "B", "tie"]},
                                             "why": {"type": "string"}},
            "required": ["a", "b", "prefer", "why"], "additionalProperties": False}


def judge(cl, arm_a, arm_b, name, rng):
    import describer
    ra = json.loads((REPLAYS / arm_a / f"{name}.json").read_text(encoding="utf-8"))
    rb = json.loads((REPLAYS / arm_b / f"{name}.json").read_text(encoding="utf-8"))
    swap = rng.random() < 0.5
    first, second = (rb, ra) if swap else (ra, rb)
    look, intro = load_capture(name)
    detail = look.get("detail") or "brief"
    content = []
    for kind, v in describer.parts(look, intro):
        content.append({"type": "image", "source": {"type": "base64", "media_type": "image/png", "data": v}}
                       if kind == "image" else {"type": "text", "text": v})
    truth = CAPTURES / name / "truth.md"
    content.append({"type": "text", "text":
                    "Ground truth: " + (truth.read_text(encoding="utf-8").strip() if truth.exists() else "none beyond the above.")
                    + f"\n\nAsked: {detail}."
                    + (f" Said before (the brief description it adds to): A's: {first['before']!r}; B's: {second['before']!r}."
                       if first.get("before") or second.get("before") else "")
                    + f"\n\nDescription A:\n{first['text']}\n\nDescription B:\n{second['text']}"})
    r = cl.messages.create(model=JUDGE_MODEL, max_tokens=16000, system=JUDGE_SYSTEM,
                           messages=[{"role": "user", "content": content}],
                           output_config={"effort": "high", "format": {"type": "json_schema", "schema": judge_schema()}})
    text = next(b.text for b in r.content if b.type == "text")
    v = json.loads(text)
    # back to the arms' own names
    by = {"A": arm_b if swap else arm_a, "B": arm_a if swap else arm_b}
    return {"capture": name, "a_arm": by["A"], "b_arm": by["B"], arm_a: v["b"] if swap else v["a"],
            arm_b: v["a"] if swap else v["b"], "prefer": by.get(v["prefer"], "tie"), "why": v["why"],
            "usd": __import__("costlab").price_of(JUDGE_MODEL, r.usage.to_dict())}


def judge_all(arm_a, arm_b, names):
    cl = client()
    out = JUDGED / f"{arm_a}--{arm_b}.jsonl"
    out.parent.mkdir(parents=True, exist_ok=True)
    done = {json.loads(l)["capture"] for l in out.read_text(encoding="utf-8").splitlines()} if out.exists() else set()
    rng = random.Random(f"{arm_a}|{arm_b}")
    for name in captures(names):
        if name in done or not (REPLAYS / arm_a / f"{name}.json").exists() or not (REPLAYS / arm_b / f"{name}.json").exists():
            rng.random()
            continue
        try:
            v = judge(cl, arm_a, arm_b, name, rng)
        except Exception as e:
            print(f"{name}: {type(e).__name__}: {str(e)[:300]}")
            continue
        with open(out, "a", encoding="utf-8") as f:
            f.write(json.dumps(v, ensure_ascii=False) + "\n")
        print(f"{name}: prefer {v['prefer']}; {arm_a} {tally(v[arm_a])}; {arm_b} {tally(v[arm_b])} (${v['usd']:.3f})")
    summary(out, arm_a, arm_b)


def tally(j):
    major = sum(1 for e in j["errors"] + j["inventions"] if e["severity"] == "major")
    minor = sum(1 for e in j["errors"] + j["inventions"] if e["severity"] == "minor")
    return (f"major {major}, minor {minor}, names {len(j['names'])}, text {len(j['text'])}, tooling {len(j['tooling'])}, "
            f"coverage {j['coverage']}, register {j['register']}")


def summary(path, arm_a, arm_b):
    vs = [json.loads(l) for l in Path(path).read_text(encoding="utf-8").splitlines()]
    if not vs:
        return
    for arm in (arm_a, arm_b):
        js = [v[arm] for v in vs]
        major = sum(1 for j in js for e in j["errors"] + j["inventions"] if e["severity"] == "major")
        minor = sum(1 for j in js for e in j["errors"] + j["inventions"] if e["severity"] == "minor")
        print(f"  {arm}: {len(js)} judged; major {major}, minor {minor}, names {sum(len(j['names']) for j in js)}, "
              f"text {sum(len(j['text']) for j in js)}, tooling {sum(len(j['tooling']) for j in js)}, "
              f"coverage {sum(j['coverage'] for j in js) / len(js):.2f}, register {sum(j['register'] for j in js) / len(js):.2f}, "
              f"preferred {sum(1 for v in vs if v['prefer'] == arm)}")
    print(f"  ties {sum(1 for v in vs if v['prefer'] == 'tie')}; judging ${sum(v['usd'] for v in vs):.3f}")


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    a = sys.argv[1:]
    cmd = a[0] if a else ""
    if cmd == "scene":
        sys.exit(scene("remove" in a))
    if cmd == "replay":
        replay_all(a[1], a[2:])
    elif cmd == "table":
        table()
    elif cmd == "judge":
        judge_all(a[1], a[2], a[3:])
    elif cmd == "show":
        show(a[1], a[2:])
    else:
        print(__doc__)


if __name__ == "__main__":
    main()
