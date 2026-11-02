"""Descriptions as one lean model call (docs/assistant-cost.md): the pictures SojournDescribe draws and what the lists
know, sent with short instructions straight to the Messages API through the sidecar's provider proxy, rather than as
a turn of a Claude Code session carrying its whole prompt, every tool and the conversation, with the pictures then
staying in that conversation for every later request. The sitting's exchanges still record what was said, so a
follow-up to any model knows it. Its model is the person's describing setting: economy (the default) or best.

describe.py's tool and brain.py's describe key both come here; where no direct call can be made (the Claude account,
which only Claude Code holds) or one fails, the pictures go to the session as before. With another maker's model on
OpenRouter the proxy sends the call to that model, as it does the sessions'. Claude Opus 5.5, 2026-10-31.
"""
import json
import time

import provider as providermod

# The person's describing setting (Sojourn Access's access_describing, read per description: describe.quality), economy
# or best: model and effort, chosen on captures replayed to each and judged (docs/assistant-cost.md > Descriptions).
MODELS = {"economy": ("claude-haiku-5-5", "high"), "best": ("claude-opus-5-5", "low")}
QUIET = False      # incognito: nothing about these calls in the log (brain.py sets it per request)
LAST = {}          # the last call's model, usage and seconds, for the request's timing line

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
    "- The person you're talking to: wherever the facts say 'the person themselves' (as owner, wearer, who's seated, "
    "or the subject), that's them: say 'you' and 'your', never their name. On something they sit on, say so first "
    "(\"You're riding the Night Express, ...\").\n"
    "- People's looks as presented, plainly: hair, skin, eyes where visible, build, height against others, what kind of "
    "avatar (human, furry, robot, tiny); no judging attractiveness unless asked; gender only as clearly presented.\n"
    "- Text: read signs, posters and labels word for word, in quotes, saying where it is; when it's too small, cut off or "
    "blurred, say that rather than filling it in.\n"
    "- Directions from the person as their lists give them: metres and a clock face from the way they face (the 'where' "
    "in the facts), or left, right, ahead, behind; never coordinates. Twelve o'clock is straight ahead, three on the "
    "right, six behind, nine on the left: eleven and one are ahead, two to four right, eight to ten left. Each "
    "picture's label says which way it looks; take where things are from the facts' clock rather than their place in "
    "a picture.\n"
    "- Uncertainty said plainly, in words (\"I can't make out her face\"); never invent a detail to round a description "
    "off.\n"
    "- Loading: each picture says what of its subject's look had loaded (subject_look), and a thing in it whose look "
    "hadn't says so (look). A surface whose texture hadn't loaded is drawn plain grey or white: that is not its colour, "
    "so never name a colour, pattern or material for it (no grey or white hair, clothes, skin or paint from a surface "
    "said to be unloaded). Name what had loaded as it is and say plainly which parts' colours aren't known yet (\"his "
    "sweater and hair hadn't loaded yet, so I can't tell their colours\"). When most of it hadn't, give what the shapes "
    "show (build, pose, a garment's cut where the shape shows it), say the colours aren't known yet and suggest asking "
    "again in a moment. Where a picture says the look had fully loaded, the colours are real. If two pictures of one "
    "subject differ (one unloaded, one loaded), they're the same person or thing, more loaded in one: go by the loaded "
    "one.\n"
    "- Sitting: in Second Life someone 'sitting on' a pose ball, dance ball or seat is animated by it, so they may "
    "stand, dance or lie: describe the pose the pictures show, and don't call it a contradiction.\n"
    "- Light: a picture marked as relit shows true colours under a midday sky while the place itself is dark; give the "
    "colours, and if the light matters say it's night there, never that it's sunny. Otherwise the light is the place's own: "
    "say when it's too dark to tell a colour.\n"
    "- Speak about the thing, not the tooling: the person never has the pictures, so nothing about pictures, views, "
    "frames, images as uploaded, what fills what, cameras, lists, grids, shares or percentages; say what is there, as "
    "if you stood beside them. Plain text, no lists or Markdown.\n"
    "- A HUD (something the person wears on their own screen: a vehicle's controls, a game, a meter): what it's for "
    "first, then its controls and readouts by where they sit on it (top left, along the bottom), each label read word "
    "for word, so they could find one by name; the names in its facts are its parts' own.\n"
    "- Before answering, check each detail against the pictures and facts: a colour, a count, a side, a shape, a word "
    "read. Leave out what you can't see clearly rather than guess."
)

SYSTEM = (
    "You describe Second Life for a person using a viewer that speaks for them: what you write goes straight to them "
    "(read out by their screen reader in Sojourn Access, or shown on a small panel in a headset), and for someone who "
    "can't see the view it's all they get of what you describe, so they can't check it. You're given pictures drawn "
    "for this, aimed at what they asked about, each with what's visible in it by name and where it is from them, and "
    "what the viewer's lists know. Write only the description itself, as if speaking to them: no preamble, no "
    "questions back, nothing about how you got it.\n\n" + HOW
)


def parts(look, intro):
    """The pictures with their labels, then the facts: [("text", str) | ("image", base64 png)]. Takes the pictures out
    of look (what stays is the facts)."""
    out = []
    pics = look.pop("pictures", None) or []
    for i, p in enumerate(pics, 1):
        png = p.pop("png", None)
        if not png:
            continue
        # what's in it by name, beside the picture itself, so the names stick to what's seen
        seen = "; ".join(f"{s.get('name')} ({s.get('kind')}, {s.get('in_picture')} of the picture"
                         + (f", {s['where']} from them" if s.get("where") else "")
                         + (f"; {s['look']}" if s.get("look") else "") + ")"
                         for s in (p.get("seen") or [])[:10])
        out.append(("text", f"Picture {i}: {p.get('label', '')}." + (f" In it, by name: {seen}." if seen else "")
                    + (f" {p['lighting']}." if p.get("lighting") else "")
                    + (f" The subject's look: {p['subject_look']}." if p.get("subject_look") else "")))
        out.append(("image", png))
    look["pictures"] = pics   # labels and what's seen in each, without the images
    out.append(("text", intro + "\n" + json.dumps(look, ensure_ascii=False)))
    return out


def ask_text(detail, before=None, words=None):
    """What's asked of a lean call: brief or rich, what was said already (rich adds to it), the person's own words."""
    out = f"Describe it {'briefly' if detail != 'rich' else 'in detail'} (detail: {detail})."
    if words:
        out += f" What they asked: {words}"
    if before:
        out += f"\nWhat you told them about it already (add to this, don't repeat it): {before}"
    return out


def messages(look, intro, detail, before=None, words=None):
    """The Messages API's user turn for a lean call."""
    content = []
    for kind, v in parts(look, intro):
        if kind == "image":
            content.append({"type": "image", "source": {"type": "base64", "media_type": "image/png", "data": v}})
        else:
            content.append({"type": "text", "text": v})
    content.append({"type": "text", "text": ask_text(detail, before, words)})
    return [{"role": "user", "content": content}]


def available():
    """Whether a lean call can be made: a key, a token or OpenRouter, through the proxy that holds it (the Claude
    account is Claude Code's alone, so its descriptions stay a session's turn), and the API's client installed."""
    prov = providermod.current
    if not (prov.ready and prov.proxy and prov.kind in ("anthropic", "relay", "openrouter")):
        return False
    try:
        import anthropic  # noqa: F401
    except ImportError:
        return False
    return True


async def lean(look, intro, detail, before=None, words=None, quality="economy", log=print):
    """The description in one call, or None when one can't be made here (the caller then gives the pictures to its
    session). look holds the pictures and facts as gather made them."""
    if not available():
        return None
    import anthropic
    prov = providermod.current
    model, effort = MODELS.get(quality, MODELS["economy"])
    extra = {}
    if prov.kind in ("anthropic", "relay") and not model.startswith("claude-haiku"):
        # a refusal on Opus or Sonnet is answered by the fallback model the API picks, inside the same call
        extra = {"extra_headers": {"anthropic-beta": "server-side-fallback-2026-07-01"}, "extra_body": {"fallbacks": "default"}}
    client = anthropic.AsyncAnthropic(base_url=prov.proxy.url, api_key=prov.proxy.token, max_retries=2, timeout=120)
    t0 = time.time()
    try:
        r = await client.messages.create(model=model, max_tokens=16000, system=SYSTEM,
                                         messages=messages(look, intro, detail, before, words),
                                         output_config={"effort": effort}, **extra)
    except anthropic.APIStatusError as e:
        if not QUIET:
            log(f"describer: {model} answered {e.status_code}; the session describes instead")
        return None
    except anthropic.APIConnectionError as e:
        if not QUIET:
            log(f"describer: couldn't reach the provider ({type(e).__name__}); the session describes instead")
        return None
    finally:
        await client.close()
    text = " ".join(b.text for b in r.content if b.type == "text").strip()
    usage = r.usage.to_dict() if hasattr(r.usage, "to_dict") else {}
    LAST.clear()
    LAST.update({"model": r.model, "usage": usage, "s": round(time.time() - t0, 2), "stop": r.stop_reason})
    if not QUIET:
        log(f"describer: {r.model} {effort}, {usage.get('input_tokens')} in, {usage.get('output_tokens')} out, "
            f"{LAST['s']}s{'' if r.stop_reason == 'end_turn' else ', ' + str(r.stop_reason)}")
    if r.stop_reason == "refusal" or not text:
        return "I couldn't describe that." if r.stop_reason == "refusal" else None
    return text



# Reading a HUD's buttons (docs/access-app.md > HUDs out of sight): most HUDs are one picture with buttons painted on
# it, which its script tells apart by where it's touched, so they have no parts to name; this names what a click would
# act on and where, and the client keeps them for that look of the HUD (SojournHud save_buttons).
BUTTONS_SYSTEM = (
    "You read a Second Life HUD for a person who can't see it, so they can press its controls by name with their "
    "keyboard. You're given a picture of one HUD as it shows on their screen (drawn bigger, over a dark grey where "
    "it's see-through), its size in pixels, and what the viewer knows of it (its name, its parts' names and where "
    "named parts are on the picture).\n"
    "List every control a click would act on: buttons, tabs, arrows, icons, toggles, a slider's ends, and any labelled "
    "region a script would tell apart by where it's touched. For each give a short name a person would say: its label "
    "word for word when it has one, in ordinary capitals (BRAKE as \"Brake\"), else what it shows (\"left arrow\", \"red bell icon\"), told "
    "apart by where it is when two would share a name (\"plus, top row\"); and its box on the picture in pixels, "
    "[x, y, width, height] from the picture's top left, tight around it. Text that only shows something (a title, a "
    "speed, a score) isn't a control: give it under readouts with what it says. Check each box against the picture "
    "before answering: its middle must be on the control.\n"
    "Answer with JSON only, no other words: {\"what\": \"what the HUD is for, in a few words, without naming its "
    "controls\", \"buttons\": "
    "[{\"name\": \"...\", \"box\": [x, y, w, h]}], \"readouts\": [{\"text\": \"...\", \"box\": [x, y, w, h]}]}. If you "
    "can't make out any controls (it's blank, still loading, or only a display), give an empty buttons list and say so "
    "in what."
)


def _json_in(text):
    """The JSON object in a reply (a fence or words around it tolerated), or None."""
    a, b = text.find("{"), text.rfind("}")
    if a < 0 or b <= a:
        return None
    try:
        return json.loads(text[a:b + 1])
    except ValueError:
        return None


async def read_buttons(look, quality="economy", log=print):
    """A HUD's controls from its picture in one call: {"what", "buttons": [{"name", "box"}], "readouts"}, or None when
    no call can be made here or it gave nothing usable. look is SojournHud look's answer for one HUD."""
    if not available():
        return None
    import anthropic
    prov = providermod.current
    model, effort = MODELS.get(quality, MODELS["economy"])
    facts = {k: v for k, v in look.items() if k in ("huds", "parts", "buttons")}
    content = [
        {"type": "image", "source": {"type": "base64", "media_type": "image/png", "data": look["png"]}},
        {"type": "text", "text": f"The picture is {look.get('width')} by {look.get('height')} pixels. What the viewer "
                                 "knows (rect: [x, y, w, h] on the picture; buttons: what was read before, if anything):\n"
                                 + json.dumps(facts, ensure_ascii=False)},
    ]
    client = anthropic.AsyncAnthropic(base_url=prov.proxy.url, api_key=prov.proxy.token, max_retries=2, timeout=120)
    t0 = time.time()
    try:
        r = await client.messages.create(model=model, max_tokens=8000, system=BUTTONS_SYSTEM,
                                         messages=[{"role": "user", "content": content}], output_config={"effort": effort})
    except (anthropic.APIStatusError, anthropic.APIConnectionError) as e:
        if not QUIET:
            log(f"describer: reading a HUD's buttons failed ({type(e).__name__})")
        return None
    finally:
        await client.close()
    text = " ".join(b.text for b in r.content if b.type == "text").strip()
    usage = r.usage.to_dict() if hasattr(r.usage, "to_dict") else {}
    LAST.clear()
    LAST.update({"model": r.model, "usage": usage, "s": round(time.time() - t0, 2), "stop": r.stop_reason})
    if not QUIET:
        log(f"describer: a HUD's buttons, {r.model} {effort}, {usage.get('input_tokens')} in, "
            f"{usage.get('output_tokens')} out, {LAST['s']}s")
    got = _json_in(text)
    if not isinstance(got, dict) or not isinstance(got.get("buttons"), list):
        return None
    got["buttons"] = [b for b in got["buttons"] if isinstance(b, dict) and str(b.get("name", "")).strip()
                      and isinstance(b.get("box"), list) and len(b["box"]) == 4]
    return got
