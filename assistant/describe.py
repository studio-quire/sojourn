"""Describing for a person who can't see the view (docs/access.md > M5): the describe tool, and what Sojourn Access's
describe key (Control+D) runs without a session (brain.py describe_key).

The client's SojournDescribe (src/Sojourn.Session/Offscreen/DescribeApi.cs) draws pictures aimed at the subject with
what's visible in each by name, and hands over textures and profile pictures; gather resolves the person's words to a
subject and fetches them. Then describer.py writes the description in one lean call (docs/assistant-cost.md), or, where
that can't be made, the pictures go to the session with how to describe. Where the client has no SojournDescribe (the
VR client), it falls back to a picture of the view. Claude Opus 5.5, 2026-10-29; lean calls 2026-10-31.
"""
import base64
import os
import re
import tempfile
import time

from claude_agent_sdk import tool

import describer
from api import PICTURE_WAIT, ApiError

UUID = re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
THAT = {"that", "this", "it", "them", "him", "her", "pointed", "selected"}
HERE = {"", "here", "around", "around me", "this place", "where i am", "the place", "surroundings"}
ME = {"me", "myself", "self", "my outfit", "what i'm wearing", "what im wearing", "my avatar", "how i look"}
HOW = describer.HOW
AGAIN = 600   # a rich description adds to a brief one of the same subject from this recently (Access's own window)


async def subject_of(kit, words):
    """here, me, or an id for the person's words: 'that' (pointed at or selected), an id, a person nearby, a thing in
    view."""
    w = (words or "").strip()
    lw = w.lower().rstrip(".?!")
    if lw in HERE:
        return "here", None
    if lw in ME:
        return "me", None
    if UUID.fullmatch(w):
        return w, None
    t = kit.target or {}
    if lw in THAT:
        if t.get("kind") == "avatar" and t.get("id"):
            return t["id"], t.get("name")
        if t.get("id") and t.get("kind") != "ground":
            return t["id"], t.get("name")
        raise ApiError("nothing is pointed at or selected: ask which person or thing they mean")
    from tools import like
    best = (0.0, None, None)
    for p in await kit.people(dist=64):
        for n in (p.get("display_name"), p.get("username")):
            s = like(n, w)
            if s > best[0]:
                best = (s, p.get("id"), n)
    if best[0] >= 0.6:
        return best[1], best[2]
    r = await kit.call("TesselObjects", "nearby", 10, dist=64.0, limit=120, timeout=3)
    found = sorted(((like(o.get("name", ""), w), o) for o in r.get("objects", [])), key=lambda x: -x[0])
    found = [(s, o) for s, o in found if s >= 0.6]
    if found and (len(found) == 1 or found[0][0] > found[1][0]):
        return found[0][1]["id"], found[0][1].get("name")
    if found:
        raise ApiError(f"several things fit {w!r}: " + "; ".join(f"{o.get('name')} {o['id']}" for _, o in found[:6])
                       + " (use an id, or ask which)")
    raise ApiError(f"no one and nothing called {w!r} is in view")


def keep_for_lab(look, of, key):
    """SOJOURN_DESCRIBE_CAPTURE=dir (off unless set; never in incognito): what each description was made from, saved
    as tools/costlab.py capture saves it, for tools/describe_lab.py to replay on real Second Life content. The pictures
    can show other people, so the folder stays on the PC it was made on (docs/assistant-cost.md > Real captures)."""
    root = os.environ.get("SOJOURN_DESCRIBE_CAPTURE")
    if not root or describer.QUIET:
        return
    import json
    out = os.path.join(root, time.strftime("%Y%m%d-%H%M%S-") + re.sub(r"[^\w-]+", "_", key)[:48] + f"-{look.get('detail')}")
    os.makedirs(out, exist_ok=True)
    keep = {k: v for k, v in look.items() if k != "pictures"}
    keep["pictures"] = []
    for i, p in enumerate(look.get("pictures") or [], 1):
        q = {k: v for k, v in p.items() if k != "png"}
        if p.get("png"):
            with open(os.path.join(out, f"pic{i}.png"), "wb") as f:
                f.write(base64.b64decode(p["png"]))
            q["file"] = f"pic{i}.png"
        keep["pictures"].append(q)
    keep["captured"] = {"subject": key, "of": of, "at": time.strftime("%Y-%m-%d %H:%M")}
    with open(os.path.join(out, "look.json"), "w", encoding="utf-8") as f:
        json.dump(keep, f, ensure_ascii=False, indent=1)


async def gather(kit, of, words, detail):
    """What there is to describe it from: (look, intro, key), look holding the pictures (base64 PNGs) and the facts,
    key naming the subject (for adding to what was said of it before). Raises ApiError."""
    look, intro, key = await _gather(kit, of, words, detail)
    try:
        keep_for_lab(look, of, key)
    except OSError as e:
        kit.log(f"describe capture: {e}")
    return look, intro, key


async def _gather(kit, of, words, detail):
    if of == "texture":
        if not UUID.fullmatch(words.strip()):
            raise ApiError("a texture's id")
        kit.did("looking at the picture")
        r = await kit.call("SojournDescribe", "texture", 40, id=words.strip(), max=1024 if detail == "rich" else 768)
        return ({"pictures": [{"label": "the texture as uploaded", "png": r["png"]}], "detail": detail},
                "A texture (an image as uploaded to Second Life).", f"texture {words.strip()}")
    if of == "item":
        iid, name = await kit.item(words)
        kit.did(f"looking at {name or 'it'}")
        r = await kit.call("SojournInventory", "picture", 40, id=iid, size=1024 if detail == "rich" else 768)
        return ({"pictures": [{"label": f"{name or 'the item'} ({r.get('source')})", "png": r["png"]}], "detail": detail,
                 "item": name}, "An inventory item's picture: its thumbnail, or a texture or snapshot's own image.", f"item {iid}")
    if of == "profile":
        pid, pname = await kit.person(words) if words.strip().lower() not in THAT else await subject_of(kit, words)
        kit.did("looking at their profile")
        r = await kit.call("SojournDescribe", "profile", 40, id=pid)
        return r | {"detail": detail}, f"{r.get('name') or pname}'s profile pictures.", f"profile {pid}"
    subject, name = await subject_of(kit, words)
    kit.did("looking around" if subject == "here" else "looking at you" if subject == "me" else f"looking at {name or 'it'}")
    try:
        # (the client bounds its own wait: 26 s on a GPU, up to minutes for a first picture drawn on the processor)
        r = await kit.call("SojournDescribe", "look", PICTURE_WAIT, subject=subject, detail=detail)
    except ApiError as e:
        if "no such API" not in str(e):
            raise
        return await fallback(kit, detail), "What there is to describe it from:", "view"
    return r, "What there is to describe it from:", f"view {subject}"


async def fallback(kit, detail):
    """No SojournDescribe here (the VR client): a picture of the view, as look takes it."""
    path = os.path.join(tempfile.gettempdir(), f"sojourn-describe-{os.getpid()}.png")
    r = await kit.call("LLViewerWindow", "saveSnapshot", 30, filename=path, width=1024, height=1024, tool="assistant")
    if not r.get("ok"):
        raise ApiError(f"no picture ({r.get('error')})")
    with open(path, "rb") as f:
        png = base64.b64encode(f.read()).decode()
    os.remove(path)
    return {"pictures": [{"label": "the person's view", "png": png}], "detail": detail,
            "notes": ["only the person's own view: nothing aimed at the subject here"]}


async def quality(kit):
    """The person's describing setting (Sojourn Access's access_describing: economy or best); SOJOURN_DESCRIBING for
    tests. Economy where there's no such setting (the VR client)."""
    if v := os.environ.get("SOJOURN_DESCRIBING"):
        return v
    try:
        r = await kit.call("SojournSettings", "get", 3, name="access_describing")
        return (r.get("settings") or {}).get("access_describing", {}).get("value") or "economy"
    except ApiError:
        return "economy"


def said_before(kit, key, detail):
    """For a rich description, the last one of the same subject this sitting (recently), which it adds to."""
    if detail != "rich":
        return None
    t, text = (getattr(kit, "described", None) or {}).get(key, (0, None))
    return text if time.time() - t < AGAIN else None


def remember(kit, key, text):
    if getattr(kit, "described", None) is None:
        kit.described = {}
    kit.described[key] = (time.time(), text)


def build_describe(kit):
    @tool("describe", "Describe something to the person, who may not be able to see it: a place (subject: here), "
          "a person or a thing near them (a name, an id, or 'that' for what's pointed at or selected), the person "
          "themselves and what they're wearing (subject: me), a person's profile pictures (of: profile), a texture "
          "(of: texture, subject its id) or an inventory picture, snapshot or thumbnail (of: item, subject its name or "
          "id). The subject is the one person or thing asked about, by its name as the lists give it (\"Jazz Poster\"), "
          "never here for one thing in the place: pictures are drawn aimed at it, a sign's or poster's image whole for "
          "its text. detail: brief (the default) or rich (when they ask for more); words: their question when it's about "
          "a part of the subject (\"what does her shirt say?\"). Use this rather than look whenever they ask what "
          "something or someone looks like, what's here, what a sign says, or what they're wearing.",
          {"type": "object", "properties": {
              "subject": {"type": "string"},
              "of": {"type": "string", "enum": ["view", "profile", "texture", "item"]},
              "detail": {"type": "string", "enum": ["brief", "rich"]},
              "words": {"type": "string"}}, "required": ["subject"]})
    async def describe(args):
        of = args.get("of") or "view"
        detail = args.get("detail") or "brief"
        words = args.get("subject") or "here"
        try:
            look, intro, key = await gather(kit, of, words, detail)
        except ApiError as e:
            return {"content": [{"type": "text", "text": f"error: {e}"}], "is_error": True}
        before = said_before(kit, key, detail)
        text = await describer.lean(look, intro, detail, before=before, words=args.get("words"), quality=await quality(kit),
                                    log=kit.log)
        if text:
            remember(kit, key, text)
            return {"content": [{"type": "text", "text": "The description, for the person: pass it on whole, as your "
                                 "reply, word for word, adding nothing before or after.\n\n" + text}]}
        out = []
        for kind, v in describer.parts(look, intro):
            out.append({"type": "image", "data": v, "mimeType": "image/png"} if kind == "image" else {"type": "text", "text": v})
        out.append({"type": "text", "text": HOW})
        return {"content": out}

    return describe
