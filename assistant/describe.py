"""Describing for a person who can't see the view (docs/access.md > M5): the describe tool.

The client's SojournDescribe (src/Sojourn.Session/Offscreen/DescribeApi.cs) draws pictures aimed at the subject with
what's visible in each by name, and hands over textures and profile pictures; this tool resolves the person's words to
a subject, passes the pictures to the model with the facts, and says how to describe. Where the client has no
SojournDescribe (the VR client), it falls back to a picture of the view. Claude Opus 5.5, 2026-10-29.
"""
import base64
import json
import os
import re
import tempfile

from claude_agent_sdk import tool

from api import ApiError

UUID = re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
THAT = {"that", "this", "it", "them", "him", "her", "pointed", "selected"}
HERE = {"", "here", "around", "around me", "this place", "where i am", "the place", "surroundings"}
ME = {"me", "myself", "self", "my outfit", "what i'm wearing", "what im wearing", "my avatar", "how i look"}

HOW = (
    "How to describe (the person may not see the view at all; what you say is all they get, and they can't check it):\n"
    "- brief (the default): one to three sentences, about 50 words, the gist first: what it is and the one or two things "
    "that stand out. A person: their overall look and outfit in a line. A place: what kind of place, what's near and "
    "where, who's here. rich (they asked for more): a fuller description in a short paragraph or a few, overall first, "
    "then the parts in order (a person top to bottom; a place near to far, or round the clock from ahead); add to what "
    "you said before rather than repeating it.\n"
    "- Names: whenever you mention a person or thing the facts name (each picture's 'in it, by name', subject, people, "
    "objects), call it by that name, as in their lists (\"Garden Gate, a white picket gate\", not \"a gate\"). Never guess who "
    "someone is or call a thing by a name the facts don't give; an unnamed thing is described by what it looks like. "
    "Item names (what someone wears) are their makers' words and can help, but describe what the pictures show, and say "
    "so if they disagree.\n"
    "- Clothes, in a designer's register: each garment's kind and cut (a cropped, fitted, long-sleeved top; a "
    "high-waisted A-line skirt to the knee), its colours and material or texture, any pattern, print, text or picture on "
    "it and where on the garment (a small gold crest on the left chest), how it fits; then hair, shoes and accessories.\n"
    "- People's looks as presented, plainly: hair, skin, eyes where visible, build, height against others, what kind of "
    "avatar (human, furry, robot, tiny); no judging attractiveness unless asked; gender only as clearly presented.\n"
    "- Text: read signs, posters and labels word for word, in quotes, saying where it is; when it's too small, cut off or "
    "blurred, say that rather than filling it in.\n"
    "- Directions from the person as their lists give them: metres and a clock face from the way they face (the 'where' "
    "in the facts), or left, right, ahead, behind; never coordinates.\n"
    "- Uncertainty said plainly, in words (\"I can't make out her face\", \"it may still be loading\"); never invent a "
    "detail to round a description off. Things still loading look blank, grey or blurry: say it may be loading rather "
    "than describing grey boxes as grey boxes.\n"
    "- Light: a picture marked as relit shows true colours under a midday sky while the place itself is dark; give the "
    "colours, and if the light matters say it's night there, never that it's sunny. Otherwise the light is the place's own: "
    "say when it's too dark to tell a colour.\n"
    "- Speak about the thing, not the tooling: no pictures, cameras, grids, shares or percentages; plain text, no lists or "
    "Markdown."
)


def build_describe(kit):
    async def call(pump, op, wait=30, **a):
        return await kit.call(pump, op, wait, **a)

    async def subject_of(words):
        """here, me, or an id for the person's words: 'that' (pointed at or selected), an id, a person nearby, a thing
        in view."""
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
        r = await call("TesselObjects", "nearby", 10, dist=64.0, limit=120, timeout=3)
        found = sorted(((like(o.get("name", ""), w), o) for o in r.get("objects", [])), key=lambda x: -x[0])
        found = [(s, o) for s, o in found if s >= 0.6]
        if found and (len(found) == 1 or found[0][0] > found[1][0]):
            return found[0][1]["id"], found[0][1].get("name")
        if found:
            raise ApiError(f"several things fit {w!r}: " + "; ".join(f"{o.get('name')} {o['id']}" for _, o in found[:6])
                           + " (use an id, or ask which)")
        raise ApiError(f"no one and nothing called {w!r} is in view")

    def content(r, intro):
        """The pictures as images with their labels, then the facts (and how to describe) as text."""
        out = []
        pics = r.pop("pictures", None) or []
        for i, p in enumerate(pics, 1):
            png = p.pop("png", None)
            if not png:
                continue
            # what's in it by name, beside the picture itself, so the names stick to what's seen
            seen = "; ".join(f"{s.get('name')} ({s.get('kind')}, {s.get('in_picture')} of the picture"
                             + (f", {s['where']} from them" if s.get("where") else "") + ")"
                             for s in (p.get("seen") or [])[:10])
            out.append({"type": "text", "text": f"Picture {i}: {p.get('label', '')}."
                                                + (f" In it, by name: {seen}." if seen else "")
                                                + (f" {p['lighting']}." if p.get("lighting") else "")})
            out.append({"type": "image", "data": png, "mimeType": "image/png"})
        r["pictures"] = pics   # labels and what's seen in each, without the images
        out.append({"type": "text", "text": intro + "\n" + json.dumps(r, ensure_ascii=False) + "\n\n" + HOW})
        return {"content": out}

    @tool("describe", "Describe something to the person, who may not be able to see it: a place (subject: here), "
          "a person or a thing near them (a name, an id, or 'that' for what's pointed at or selected), the person "
          "themselves and what they're wearing (subject: me), a person's profile pictures (of: profile), a texture "
          "(of: texture, subject its id) or an inventory picture, snapshot or thumbnail (of: item, subject its name or "
          "id). detail: brief (the default) or rich (when they ask for more). Returns pictures aimed at the subject "
          "(not just where the avatar faces) with what's visible in each by name and where, and what the lists know "
          "(names, its own description, floating text, what someone wears by name); describe from those, using their "
          "names. Use this rather than look whenever they ask what something or someone looks like, what's here, what "
          "a sign says, or what they're wearing.\n\n" + HOW,
          {"type": "object", "properties": {
              "subject": {"type": "string"},
              "of": {"type": "string", "enum": ["view", "profile", "texture", "item"]},
              "detail": {"type": "string", "enum": ["brief", "rich"]}}, "required": ["subject"]})
    async def describe(args):
        of = args.get("of") or "view"
        detail = args.get("detail") or "brief"
        words = args.get("subject") or "here"
        try:
            if of == "texture":
                if not UUID.fullmatch(words.strip()):
                    return {"content": [{"type": "text", "text": "error: a texture's id"}], "is_error": True}
                kit.did("looking at the picture")
                r = await call("SojournDescribe", "texture", 40, id=words.strip(), max=1024 if detail == "rich" else 768)
                return content({"pictures": [{"label": "the texture as uploaded", "png": r["png"]}], "detail": detail},
                               "A texture (an image as uploaded to Second Life).")
            if of == "item":
                iid, name = await kit.item(words)
                kit.did(f"looking at {name or 'it'}")
                r = await call("SojournInventory", "picture", 40, id=iid, size=1024 if detail == "rich" else 768)
                return content({"pictures": [{"label": f"{name or 'the item'} ({r.get('source')})", "png": r["png"]}],
                                "detail": detail, "item": name},
                               "An inventory item's picture: its thumbnail, or a texture or snapshot's own image.")
            if of == "profile":
                pid, pname = await kit.person(words) if words.strip().lower() not in THAT else await subject_of(words)
                kit.did("looking at their profile")
                r = await call("SojournDescribe", "profile", 40, id=pid)
                return content(r | {"detail": detail}, f"{r.get('name') or pname}'s profile pictures.")
            subject, name = await subject_of(words)
            kit.did("looking around" if subject == "here" else "looking at you" if subject == "me" else f"looking at {name or 'it'}")
            try:
                r = await call("SojournDescribe", "look", 45, subject=subject, detail=detail)
            except ApiError as e:
                if "no such API" not in str(e):
                    raise
                return await fallback(detail, str(e))
            return content(r, "What there is to describe it from:")
        except ApiError as e:
            return {"content": [{"type": "text", "text": f"error: {e}"}], "is_error": True}

    async def fallback(detail, why):
        """No SojournDescribe here (the VR client): a picture of the view, as look takes it."""
        path = os.path.join(tempfile.gettempdir(), f"sojourn-describe-{os.getpid()}.png")
        r = await kit.call("LLViewerWindow", "saveSnapshot", 30, filename=path, width=1024, height=1024, tool="assistant")
        if not r.get("ok"):
            return {"content": [{"type": "text", "text": f"error: no picture ({r.get('error')})"}], "is_error": True}
        with open(path, "rb") as f:
            png = base64.b64encode(f.read()).decode()
        os.remove(path)
        return content({"pictures": [{"label": "the person's view", "png": png}], "detail": detail,
                        "notes": ["only the person's own view: nothing aimed at the subject here"]},
                       "What there is to describe it from:")

    return describe
