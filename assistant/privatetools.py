"""The tools a blind incognito session gets (docs/assistant.md > Incognito), and the `private` tool every session has.

While incognito is on and blind the client runs only an allowlist of ops (src/Sojourn.Session/Private/Allowlist.cs) and names
things nearby by blind handles (thing-..., person-..., chat-...). These are the tools that still do something there,
so the model doesn't flail on refusals: no Claude Code tools, no client_api, nothing that reads the world. Some are
tools.py's own (they only touch allowed ops); the rest are written here for handles.
"""
import json
import re

from claude_agent_sdk import create_sdk_mcp_server, tool

import tools as toolmod
from api import ApiError

text, error, S = toolmod.text, toolmod.error, toolmod.S
HANDLE = re.compile(r"(thing|person|chat)-[a-z0-9]{6}")
THAT = ("that", "this", "it", "him", "her", "them", "pointed")

# tools.py's tools that act only through allowlisted ops
SHARED = ("wear", "take_off", "say", "stand", "ask_choice", "point", "confirm_text", "settings", "assistant_settings", "panel_look", "bring_panel", "escalate")


def handle(kit, words, kinds):
    """A handle for words: a handle as given, or "that" (what the person points at). Raises ApiError otherwise."""
    w = (words or "").strip().lower()
    if HANDLE.fullmatch(w) and w.split("-")[0] in kinds:
        return w
    t = kit.target or {}
    if w in THAT and t.get("handle") and t.get("kind") in kinds:
        return t["handle"]
    raise ApiError(f"incognito: a {' or '.join(k + ' handle' for k in kinds)} (from situation or what they point at), "
                   "not a name; if it's unclear which, have them point (point)")


def private_tool(kit):
    """Incognito for any session: on (only the person turns it off), and the triggers."""

    @tool("incognito", "Incognito, for doing things privately: while it's on nothing from that time is kept, memory and jobs "
          "wait, and unless the person chose to let you see, you're told only general things (no names, words or places). "
          "action: on (only when they ask you for it now; never on your own, and never because they ask about incognito or "
          "what happened while it was on); always (it comes on by itself: person: a person (an id, a handle, or 'that') when "
          "they're nearby or in an IM, or here: true at this place; off: true takes that back). Only the person turns it "
          "off, with their own controls (their wrist, the menu), never you.",
          {"type": "object", "properties": {"action": {"type": "string", "enum": ["on", "always"]},
                                            "person": {"type": "string"}, "here": {"type": "boolean"},
                                            "off": {"type": "boolean"}}, "required": ["action"]})
    async def private(args):
        try:
            if args["action"] == "on":
                kit.did("going incognito")
                await kit.call("SojournPrivate", "set", on=True)
                return text("incognito is on: your sessions restart incognito after this turn; end it with a word or nothing")
            a = {"on": not args.get("off")}
            if args.get("here"):
                a["here"] = True
            if who := args.get("person"):
                t = kit.target or {}
                if who.strip().lower() in THAT:
                    who = t.get("handle") or t.get("worn_by") or (t.get("id") if t.get("kind") == "avatar" else None)
                    if not who:
                        return error("they aren't pointing at a person")
                a["person"] = who
            kit.did("remembering that")
            return text(await kit.call("SojournPrivate", "always", **a) or {"ok": True})
        except ApiError as e:
            return error(str(e))

    return private


def build(kit, with_escalate, with_help=False):
    """The blind incognito session's tools as an in-process MCP server named like the normal one (so the same allow rule)."""
    shared = toolmod.tool_list(kit, with_escalate, only=SHARED)

    @tool("situation", "The general picture, from the client: how many people are talking nearby, new IMs and from how many "
          "people (each conversation as a chat handle), someone speaking in voice, whether it's their own land, waiting "
          "menus or confirmations, how long incognito has been on, and what they point at as a handle. Never names, "
          "words or where. Each call resets what counts as new.", S())
    async def situation(args):
        try:
            return text(await kit.call("SojournPrivate", "situation", 5))
        except ApiError as e:
            return error(str(e))

    @tool("message", "Send an IM as the person, only what they asked to send, in their words: to a conversation (a chat "
          "handle from situation, as a reply) or a person (a person handle, or 'that' for the one they point at).",
          S(["to", "text"], to=str, text=str))
    async def message(args):
        try:
            h = handle(kit, args["to"], ("chat", "person"))
            kit.did("sending it")
            await kit.call("TesselIM", "send", **({"session_id": h} if h.startswith("chat-") else {"to": h}), message=args["text"])
            return text("sent")
        except ApiError as e:
            return error(str(e))

    @tool("mark_read", "Mark a conversation (a chat handle) as read.", S(["chat"], chat=str))
    async def mark_read(args):
        try:
            await kit.call("TesselIM", "markRead", session_id=handle(kit, args["chat"], ("chat",)))
            return text("done")
        except ApiError as e:
            return error(str(e))

    @tool("sit", "Sit on a thing (a thing handle, or 'that'), or on the ground with nothing given.", S(thing=str))
    async def sit(args):
        try:
            a = {"obj_uuid": handle(kit, args["thing"], ("thing",))} if args.get("thing") else {}
            kit.did("sitting down")
            await kit.call("LLAgent", "requestSit", **a)
            return text("sitting")
        except ApiError as e:
            return error(str(e))

    @tool("touch", "Touch (click) a thing: a thing handle, or 'that'. Others' things may wait for the person's press.",
          S(["thing"], thing=str))
    async def touch(args):
        try:
            h = handle(kit, args["thing"], ("thing",))
            kit.did("touching it")
            await kit.call("LLAgent", "requestTouch", toolmod.GATED_TIMEOUT, obj_uuid=h)
            return text("touched")
        except ApiError as e:
            return error(str(e))

    @tool("go_home", "Teleport the person home.", S())
    async def go_home(args):
        try:
            kit.did("going home")
            await kit.call("TesselAgent", "goHome")
            return text("on the way home")
        except ApiError as e:
            return error(str(e))

    @tool("stop_animating", "Stop everything animating the person (a dance machine, a pose; seated, they stand up), like the "
          "viewer's \"Stop animating me\".", S())
    async def stop_animating(args):
        try:
            kit.did("stopping your animations")
            await kit.call("LLAgent", "stopAnimating")
            return text("stopped")
        except ApiError as e:
            return error(str(e))

    @tool("outfits", "The person's saved outfits: list them (ids and names), or wear one by its id (add: true keeps what's "
          "on).", S(["action"], action=str, id=str, add=bool))
    async def outfits(args):
        try:
            if args["action"] == "list":
                return text(await kit.call("LLAppearance", "getOutfitsList", 30))
            if not args.get("id"):
                return error("id: an outfit's id from the list")
            kit.did("changing outfit")
            await kit.call("LLAppearance", "wearOutfit", 60, folder_id=args["id"], append=bool(args.get("add")))
            return text("worn")
        except ApiError as e:
            return error(str(e))

    @tool("gestures", "The person's active gestures: list them, or play one by its id.", S(["action"], action=str, id=str))
    async def gestures(args):
        try:
            if args["action"] == "list":
                return text(await kit.call("LLGesture", "getActiveGestures"))
            kit.did("playing the gesture")
            await kit.call("LLGesture", "startGesture", id=args.get("id"))
            return text("playing")
        except ApiError as e:
            return error(str(e))

    @tool("people", "Keep someone at a distance, or bring them back, by a person handle (or 'that'). action: block, unblock "
          "(their chat, IMs, offers and voice stop reaching the person; a thing handle blocks an object), voice_mute, "
          "voice_unmute (only in the person's own hearing), voice_volume (volume 0-2, 1 as they come), draw (as: normal, "
          "always (whole whatever their complexity) or never (a plain shape)); reset_skeleton, refresh_attachments (a "
          "deformed or half-loaded avatar: who, or me for their own); resync (everyone's animations in step, no who).",
          {"type": "object", "properties": {
              "action": {"type": "string", "enum": ["block", "unblock", "voice_mute", "voice_unmute", "voice_volume", "draw",
                                                    "reset_skeleton", "refresh_attachments", "resync"]},
              "who": {"type": "string"}, "volume": {"type": "number"}, "as": {"type": "string"}}, "required": ["action"]})
    async def people(args):
        act = args["action"]
        try:
            if act == "resync":
                kit.did("putting everyone's animations in step")
                await kit.call("SojournAvatars", "resyncAnimations")
                return text("done")
            if act in ("reset_skeleton", "refresh_attachments"):
                who = (args.get("who") or "me").strip().lower()
                a = {} if who in ("me", "mine", "myself", "my", "") else {"id": handle(kit, who, ("person",))}
                kit.did("resetting the skeleton" if act == "reset_skeleton" else "refreshing attachments")
                await kit.call("SojournAvatars", "resetSkeleton" if act == "reset_skeleton" else "refreshAttachments", **a)
                return text("done")
            h = handle(kit, args.get("who"), ("person", "thing") if act in ("block", "unblock") else ("person",))
            kit.did({"block": "blocking", "unblock": "unblocking", "draw": "changing how they're drawn"}.get(act, "changing their voice"))
            if act in ("block", "unblock"):
                await kit.call("TesselAgent", "mute" if act == "block" else "unmute", id=h)
            elif act in ("voice_mute", "voice_unmute"):
                await kit.call("SojournVoice", "peerMute", id=h, mute=act == "voice_mute")
            elif act == "voice_volume":
                await kit.call("SojournVoice", "peerVolume", id=h, volume=float(args.get("volume", 1)))
            else:
                await kit.call("SojournAvatars", "render", id=h, **{"as": args.get("as") or "normal"})
            return text("done")
        except ApiError as e:
            return error(str(e))

    @tool("voice", "The person's voice chat: mute (their mic toward everyone; unmuting is their own button), hangup (leave a "
          "call), decline (a call that's ringing; answering is their own press).",
          {"type": "object", "properties": {"action": {"type": "string", "enum": ["mute", "hangup", "decline"]}},
           "required": ["action"]})
    async def voice(args):
        try:
            kit.did({"mute": "muting your mic", "hangup": "leaving the call", "decline": "declining the call"}[args["action"]])
            await kit.call("SojournVoice", args["action"])
            return text("done")
        except (ApiError, KeyError) as e:
            return error(str(e))

    @tool("report", "Report abuse to Linden Lab when the person asks to: about (a person handle or 'that') and/or object (a "
          "thing handle), category (words from action: categories), summary (one line) and details, both in the person's "
          "own words, picture (their view goes with it unless they don't want one). The panel shows them all of it and "
          "only their own press sends it; the call waits for them.",
          {"type": "object", "properties": {
              "action": {"type": "string", "enum": ["report", "categories"]}, "about": {"type": "string"},
              "object": {"type": "string"}, "category": {"type": "string"}, "summary": {"type": "string"},
              "details": {"type": "string"}, "picture": {"type": "boolean"}}, "required": ["action"]})
    async def report(args):
        try:
            if args["action"] == "categories":
                return text(await kit.call("SojournReport", "categories", 10))
            d = {k: args[k] for k in ("category", "summary", "details", "picture") if args.get(k) is not None}
            if args.get("about"):
                d["about"] = handle(kit, args["about"], ("person",))
            if args.get("object"):
                d["object"] = handle(kit, args["object"], ("thing",))
            kit.did("drafting the report")
            r = await kit.call("SojournReport", "draft", 30, **d)
            kit.did("asking you to confirm the report")
            return text(await kit.call("SojournReport", "send", toolmod.GATED_TIMEOUT, draft=r["draft"]))
        except (ApiError, KeyError) as e:
            return error(str(e))

    @tool("capture", "Save a picture or a short video of the person's view for them (in their Pictures or Videos folder, "
          "named incognito; you never see it). action: picture, video (seconds 1-120), stop.",
          {"type": "object", "properties": {"action": {"type": "string", "enum": ["picture", "video", "stop"]},
                                            "seconds": {"type": "number"}}, "required": ["action"]})
    async def capture(args):
        try:
            if args["action"] == "picture":
                kit.did("taking a picture")
                await kit.call("SojournCapture", "snapshot", 30)
            elif args["action"] == "video":
                s = float(args.get("seconds") or 15)
                kit.did(f"recording {s:g} s")
                await kit.call("SojournCapture", "record", int(s) + 90, seconds=s)
            else:
                await kit.call("SojournCapture", "stop", 90)
            return text("saved")
        except ApiError as e:
            return error(str(e))

    @tool("hud", "The person's HUDs: show or hide them (the headset's HUD board, or the desktop's).",
          {"type": "object", "properties": {"action": {"type": "string", "enum": ["show", "hide"]}}, "required": ["action"]})
    async def hud(args):
        try:
            await kit.call("SojournHud", "show", on=args["action"] == "show")
            return text("done")
        except ApiError as e:
            return error(str(e))

    @tool("show", "Put something on screen for the person: a timer, a checklist, a list or a few words. Information only "
          "(cards have a close key). The same id again changes it; remove: true takes it away (id \"all\": everything). "
          "at: view, wrist, room, or a thing or person handle (a label over it). title, text (a label's words), blocks (up "
          "to 6): {text, style?}, {list: [text | {text, done?}], numbered?}, {table: [[cells]], header?}, {timer: "
          "seconds}, {stopwatch: true}, {progress, of?, label?}. for: seconds until it goes by itself.",
          {"type": "object", "properties": {"id": {"type": "string"}, "at": {"type": "string"}, "title": {"type": "string"},
                                            "text": {"type": "string"}, "blocks": {"description": "a list of blocks"},
                                            "for": {"type": "number"}, "remove": {"type": "boolean"}}})
    async def show(args):
        a = dict(args)
        try:
            if isinstance(a.get("blocks"), str) and a["blocks"].strip()[:1] in "[{":
                a["blocks"] = json.loads(a["blocks"])
            if isinstance(a.get("blocks"), dict):
                a["blocks"] = [a["blocks"]]
            if not a.get("id"):
                if not any(a.get(k) for k in ("at", "title", "text", "blocks")):
                    return text(await kit.call("SojournShow", "list"))
                a["id"] = "-".join(toolmod.norm(a.get("title") or a.get("text") or "shown")[:4]) or "shown"
            if a.pop("remove", False):
                return text(await kit.unshow(a["id"]))
            if a.get("at") and a["at"] not in ("view", "wrist", "room"):
                a["at"] = handle(kit, a["at"], ("thing", "person"))
            kit.did("showing it")
            return text(await kit.call("SojournShow", "show", **a))
        except (ApiError, ValueError) as e:
            return error(str(e))

    @tool("ao", "Their animation overrider (the client's AO) and the pose stand. action: status (on or off, its sets, what "
          "plays); on, off; stands (on: whether it overrides standing too); select (set: a set's name from status); next, "
          "previous (the next stand or animation); pose (the pose stand, for adjusting their shape: pose, a name from "
          "poses); pose_off; poses.",
          {"type": "object", "properties": {
              "action": {"type": "string", "enum": ["status", "on", "off", "stands", "select", "next", "previous", "pose",
                                                    "pose_off", "poses"]},
              "set": {"type": "string"}, "pose": {"type": "string"}, "on": {"type": "boolean"}}, "required": ["action"]})
    async def ao(args):
        act = args["action"]
        try:
            if act in ("on", "off"):
                kit.did(f"turning your AO {act}")
                return text(await kit.call("SojournAo", "on", on=act == "on"))
            if act == "stands":
                return text(await kit.call("SojournAo", "stands", on=bool(args.get("on", True))))
            if act == "select":
                kit.did("changing your AO's set")
                return text(await kit.call("SojournAo", "select", name=args.get("set") or ""))
            if act == "pose":
                kit.did("the pose stand")
                return text(await kit.call("SojournAo", "poseStand", 30, **({"pose": args["pose"]} if args.get("pose") else {})))
            op = {"pose_off": "poseStandOff"}.get(act, act)
            return text(await kit.call("SojournAo", op, 30))
        except ApiError as e:
            return error(str(e))

    @tool("environment", "The sky and water they see, their own (seen only by them). action: use (preset: sunrise, noon, "
          "legacy noon, sunset, midnight; or name: one of theirs or the Library's); shared (back to the place's own); list "
          "(their skies, waters and day cycles); clouds (paused: true holds them still); time (their own time of day, held "
          "still: hours 0-24, 6 sunrise, 12 noon, 18 sunset, golden hour about 17.3; or later: hours, negative earlier; by sun "
          "moves only the sun on the sky as it is, by day shows the day cycle at that time; run: true only if they ask for "
          "the day to go on).",
          {"type": "object", "properties": {
              "action": {"type": "string", "enum": ["use", "shared", "list", "clouds", "time"]}, "preset": {"type": "string"},
              "name": {"type": "string"}, "paused": {"type": "boolean"}, "hours": {"type": "number"},
              "later": {"type": "number"}, "by": {"type": "string", "enum": ["sun", "day"]}, "run": {"type": "boolean"}},
           "required": ["action"]})
    async def environment(args):
        act = args["action"]
        try:
            if act == "use":
                a = {"preset": args["preset"]} if args.get("preset") else {"name": args.get("name") or ""}
                kit.did("changing your sky")
                return text(await kit.call("SojournEnvironment", "use", 30, **a))
            if act == "clouds":
                return text(await kit.call("SojournEnvironment", "clouds", paused=bool(args.get("paused", True))))
            if act == "time":
                a = {k: args[k] for k in ("hours", "later", "by", "run") if args.get(k) is not None}
                kit.did("moving the sun")
                return text(await kit.call("SojournEnvironment", "time", 30, **a))
            return text(await kit.call("SojournEnvironment", "mine" if act == "list" else "shared", 30))
        except ApiError as e:
            return error(str(e))

    @tool("map", "action: stats (\"is it me or the region?\": their frame rate, the network, the region's time dilation and "
          "load, a verdict; names nothing); show (the map board up around them, for them to look at); hide.",
          {"type": "object", "properties": {"action": {"type": "string", "enum": ["stats", "show", "hide"]}}, "required": ["action"]})
    async def map_(args):
        try:
            return text(await kit.call("SojournMap", args["action"]))
        except ApiError as e:
            return error(str(e))

    @tool("keep_away", "Things in the person's way. action: hide (a thing handle or 'that': gone from their view until they "
          "teleport), unhide (a thing handle, or all: true), block_particles (a thing handle: its owner's particles and the "
          "rest of them blocked), release_keys (take back the controls a script holds: a stuck vehicle or weapon).",
          {"type": "object", "properties": {
              "action": {"type": "string", "enum": ["hide", "unhide", "block_particles", "release_keys"]},
              "what": {"type": "string"}, "all": {"type": "boolean"}}, "required": ["action"]})
    async def keep_away(args):
        act = args["action"]
        try:
            if act == "release_keys":
                kit.did("taking back your controls")
                return text(await kit.call("SojournSafety", "releaseKeys"))
            if act == "unhide" and args.get("all"):
                await kit.call("SojournSafety", "rerender", all=True)
                return text("done")
            h = handle(kit, args.get("what"), ("thing",))
            kit.did({"hide": "hiding it", "unhide": "bringing it back", "block_particles": "blocking those particles"}[act])
            await kit.call("SojournSafety", {"hide": "derender", "unhide": "rerender", "block_particles": "blockParticleOwner"}[act], id=h)
            return text("done")
        except (ApiError, KeyError) as e:
            return error(str(e))

    tools = shared + [situation, message, mark_read, sit, touch, go_home, stop_animating, outfits, gestures, people,
                      voice, report, capture, hud, show, ao, environment, map_, keep_away, private_tool(kit)]
    return create_sdk_mcp_server("sojourn", tools=tools), [f"mcp__sojourn__{t.name}" for t in tools]
