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
    "- People's looks as presented, plainly: hair, skin, eyes where visible, build, height against others, what kind of "
    "avatar (human, furry, robot, tiny); no judging attractiveness unless asked; gender only as clearly presented.\n"
    "- Text: read signs, posters and labels word for word, in quotes, saying where it is; when it's too small, cut off or "
    "blurred, say that rather than filling it in.\n"
    "- Directions from the person as their lists give them: metres and a clock face from the way they face (the 'where' "
    "in the facts), or left, right, ahead, behind; never coordinates. Twelve o'clock is straight ahead, three on the "
    "right, six behind, nine on the left: eleven and one are ahead, two to four right, eight to ten left. Each "
    "picture's label says which way it looks; take where things are from the facts' clock rather than their place in "
    "a picture.\n"
    "- Uncertainty said plainly, in words (\"I can't make out her face\", \"it may still be loading\"); never invent a "
    "detail to round a description off. Things still loading look blank, grey or blurry: say it may be loading rather "
    "than describing grey boxes as grey boxes.\n"
    "- Light: a picture marked as relit shows true colours under a midday sky while the place itself is dark; give the "
    "colours, and if the light matters say it's night there, never that it's sunny. Otherwise the light is the place's own: "
    "say when it's too dark to tell a colour.\n"
    "- Speak about the thing, not the tooling: the person never has the pictures, so nothing about pictures, views, "
    "frames, images as uploaded, what fills what, cameras, lists, grids, shares or percentages; say what is there, as "
    "if you stood beside them. Plain text, no lists or Markdown.\n"
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
                         + (f", {s['where']} from them" if s.get("where") else "") + ")"
                         for s in (p.get("seen") or [])[:10])
        out.append(("text", f"Picture {i}: {p.get('label', '')}." + (f" In it, by name: {seen}." if seen else "")
                    + (f" {p['lighting']}." if p.get("lighting") else "")))
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
