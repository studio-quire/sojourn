"""The assistant's tools for what people ask often beyond the first set (tools.py): land, the map, people and the
person's own account, group chat, voice, the AO, the sky, undo, scripts, an inventory item's picture, and keeping
things out of view. Each groups one area's ops, takes words, 'that' and ids as tools.py's do, and leaves the rare ops
to client_api (the op list in the instructions has them). What spends, gives, deletes or changes permissions is gated
in the client, as everywhere; these tools only wait long enough for the person's press.
"""
import asyncio
import json
import re

from claude_agent_sdk import tool

from api import ApiError
from tools import GATED_TIMEOUT, Ambiguous, error, like, text

UUID = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")
THAT = ("that", "this", "it", "him", "her", "them", "pointed", "there")
ME = ("me", "myself", "mine", "my", "i", "self", "yours", "my avatar")


def schema(required, **props):
    """An input schema: props as JSON-schema dicts or short forms (str, int, float, bool, list, dict, or a list of
    allowed strings)."""
    kinds = {str: {"type": "string"}, int: {"type": "integer"}, float: {"type": "number"}, bool: {"type": "boolean"},
             list: {"type": "array", "items": {"type": "string"}}, dict: {"type": "object"}}
    out = {}
    for k, v in props.items():
        out[k] = {"type": "string", "enum": list(v)} if isinstance(v, (list, tuple)) else v if isinstance(v, dict) else kinds[v]
    return {"type": "object", "properties": out, "required": list(required)}


def strip(args, *keys):
    """The named arguments that were given (no empty ones)."""
    return {k: args[k] for k in keys if args.get(k) not in (None, "", [], {})}


def build_more(kit):
    async def call(pump, op, wait=30, **a):
        return await kit.call(pump, op, wait, **a)

    def is_that(words):
        return (words or "").strip().lower() in THAT

    def is_me(words):
        return not (words or "").strip() or words.strip().lower() in ME

    async def someone(who):
        """A resident for words, an id or 'that' (an avatar pointed at, or the wearer of something pointed at)."""
        t = kit.target or {}
        if is_that(who) and t.get("worn_by"):
            return t["worn_by"], None
        return await kit.person(who)

    async def things(words):
        """Object ids for words: ids (a list or one), 'that' (the linkset pointed at), or a thing's name in view."""
        if isinstance(words, list):
            return [w for x in words for w in await things(x)]
        w = (words or "").strip()
        if UUID.fullmatch(w):
            return [w]
        t = kit.target or {}
        if is_that(w):
            if t.get("kind") == "avatar" or not t.get("id") or t.get("kind") == "ground":
                raise ApiError("they aren't pointing at an object: ask them to point at it (point)")
            return [t.get("root_id") or t["id"]]
        r = await call("TesselObjects", "nearby", 10, dist=96.0, limit=120, timeout=3)
        found = sorted(((like(o.get("name", ""), w), o) for o in r.get("objects", [])), key=lambda x: -x[0])
        found = [(s, o) for s, o in found if s >= 0.6]
        if found and (len(found) == 1 or found[0][0] > found[1][0]):
            return [found[0][1]["id"]]
        if not found:
            raise ApiError(f"nothing called {w!r} in view")
        raise ApiError(f"several things fit {w!r}: " + "; ".join(f"{o.get('name')} {o['id']}" for _, o in found[:6]) + " (use an id)")

    async def point_at(at):
        """[x, y] in this region for 'that' (where they point: the ground, a thing or a person), a list, or None (here)."""
        if at is None or at == "" or (isinstance(at, str) and at.strip().lower() in ("here", "me", "where i am", "where i stand")):
            return None
        if isinstance(at, list):
            return [float(v) for v in at[:2]]
        if isinstance(at, str) and at.strip()[:1] == "[":
            return [float(v) for v in json.loads(at)[:2]]
        if is_that(at):
            p = (kit.target or {}).get("position")
            if not p:
                raise ApiError("they aren't pointing at anything: leave at out for here, or ask them to point (point)")
            return [round(float(p[0]), 1), round(float(p[1]), 1)]
        ids = await things(at)
        r = await call("TesselObjects", "get", 10, ids=ids[:1], timeout=1.5)
        if not r.get("objects"):
            raise ApiError(f"can't tell where {at!r} is")
        p = r["objects"][0]["pos"]
        return [round(p[0], 1), round(p[1], 1)]

    def image(r):
        png = r.pop("png")
        return {"content": [{"type": "image", "data": png, "mimeType": "image/png"},
                            {"type": "text", "text": json.dumps(r, ensure_ascii=False)}]}

    # --- land and region --------------------------------------------------------------------------------------------

    @tool("land", "Land and the region they're in. action: about (whose land it is and how it's set: name, owner, group, "
          "area, for sale, rules; at: [x, y] in this region, 'that' for where they point, or here, the default); region "
          "(the region's type, rating and limits); covenant (the estate's rules, opened in the reader for them; you get the "
          "text too, to answer about it); access (who's allowed and banned); ban, unban, allow, unallow (who: a name, an "
          "id or 'that'; hours, none for good); eject (who, off their parcel now; ban: true keeps them out); freeze, "
          "unfreeze (who, held where they stand on the person's land); pass (buy a pass onto a pass-only parcel: here, or "
          "the one that just turned them away; at for another); buy (the parcel for sale here or at, at its price); "
          "for_sale (land for sale on the grid: max_price, min_area, type mainland|estate|auction, sort price|area|"
          "price_per_m; near: true for the map's, nearest first); settings (About Land changes: {name, desc, music_url, "
          "media_url, landing, options: {fly, scripts, push, terraform, object_entry, group_build, access_list, ...}}); lag (what's slowing the region: "
          "the lag meter's verdict, and for estate managers its busiest scripts and colliders, with ids); return (ids from "
          "lag: back to their owners); switches (the region's emergency switches: scripts_disabled, collisions_disabled, "
          "physics_disabled); terraform (how: raise, lower, flatten, smooth, roughen, revert; at: where they point on the "
          "ground, else where they stand; size 1-4 m; seconds the brush is held: 0.3 a touch, 1 the default, 3 a lot); "
          "holdings (the land they own). Spending, banning, ejecting, freezing, returning and changing settings wait for "
          "the person's press. Selling, dividing, joining, deeding and the estate's lists: SojournLand through client_api.",
          schema(["action"], action=["about", "region", "covenant", "access", "ban", "unban", "allow", "unallow", "eject",
                                     "freeze", "unfreeze", "pass", "buy", "for_sale", "settings", "lag", "return",
                                     "switches", "terraform", "holdings"],
                 at={"description": "[x, y] in this region, 'that', or here"}, who=str, hours=float, ban=bool,
                 group=str, max_price=int, min_area=int, type=str, sort=str, near=bool, changes=dict, ids=list,
                 how=["raise", "lower", "flatten", "smooth", "roughen", "revert"], size=float, seconds=float,
                 height=float, limit=int))
    async def land(args):
        act = args["action"]
        try:
            a = {}
            if act in ("about", "access", "ban", "unban", "allow", "unallow", "pass", "buy", "settings", "terraform"):
                at = await point_at(args.get("at"))
                if at is None and act == "terraform" and (kit.target or {}).get("kind") == "ground":
                    p = kit.target["position"]
                    at = [round(float(p[0]), 1), round(float(p[1]), 1)]
                if at is not None:
                    a["at"] = at
            if act in ("ban", "unban", "allow", "unallow", "eject", "freeze", "unfreeze"):
                if not args.get("who"):
                    return error("who: a name, an id or 'that'")
                pid, name = await someone(args["who"])
                kit.did({"ban": f"asking you to confirm banning {name or 'them'}", "unban": f"unbanning {name or 'them'}",
                         "allow": f"allowing {name or 'them'} in", "unallow": f"taking {name or 'them'} off the list",
                         "eject": f"asking you to confirm ejecting {name or 'them'}",
                         "freeze": f"asking you to confirm freezing {name or 'them'}",
                         "unfreeze": f"freeing {name or 'them'}"}[act])
                if act == "eject":
                    return text(await call("TesselAgent", "parcelEject", GATED_TIMEOUT, to=pid, ban=bool(args.get("ban"))))
                if act in ("freeze", "unfreeze"):
                    return text(await call("SojournLand", "freeze", GATED_TIMEOUT, to=pid, freeze=act == "freeze"))
                if args.get("hours") is not None and act in ("ban", "allow"):
                    a["hours"] = args["hours"]
                return text(await call("SojournLand", act, GATED_TIMEOUT, to=pid, **a))
            if act == "about":
                kit.status("looking up the land")
                return text(await call("SojournLand", "parcel", **a))
            if act == "region":
                return text(await call("SojournLand", "region"))
            if act == "access":
                return text(await call("SojournLand", "access", **a))
            if act == "covenant":
                kit.did("opening the covenant")
                c = await call("SojournLand", "covenant")
                words = (c.get("covenant") or "").strip()
                if words:
                    title = "Covenant" + (f": {c['estate']}" if c.get("estate") else "")
                    await call("SojournReader", "open", 10, text=words, title=title)
                    c["opened_in_reader"] = True
                return text(c)
            if act == "pass":
                kit.did("asking you to confirm the pass")
                return text(await call("SojournLand", "buyPass", GATED_TIMEOUT, **a))
            if act == "buy":
                kit.did("asking you to confirm buying the land")
                return text(await call("SojournLand", "buy", GATED_TIMEOUT, **a, **strip(args, "group")))
            if act == "for_sale":
                kit.did("looking for land for sale")
                if args.get("near"):
                    return text(await call("SojournLand", "mapItems", type="land_for_sale", limit=int(args.get("limit") or 12)))
                q = strip(args, "max_price", "min_area", "type", "sort")
                return text(await call("SojournSearch", "land", limit=int(args.get("limit") or 8), **q))
            if act == "settings":
                if not args.get("changes"):
                    return error("changes: the About Land settings to change")
                kit.did("asking you to confirm the land's settings")
                return text(await call("SojournLand", "set", GATED_TIMEOUT, **a, **args["changes"]))
            if act == "lag":
                kit.did("looking at what's slowing the region")
                limit = int(args.get("limit") or 8)
                parts = await asyncio.gather(call("SojournMap", "stats"), call("SojournLand", "topScripts", limit=limit),
                                             call("SojournLand", "topColliders", limit=limit), return_exceptions=True)
                out = {}
                for k, v in zip(("stats", "top_scripts", "top_colliders"), parts):
                    out[k] = {"unavailable": str(v)} if isinstance(v, Exception) else v
                return text(out)
            if act == "return":
                if not args.get("ids"):
                    return error("ids: the objects to return, from lag")
                kit.did("asking you to confirm returning them")
                return text(await call("SojournLand", "returnObjects", GATED_TIMEOUT, ids=args["ids"]))
            if act == "switches":
                flags = {k: v for k, v in (args.get("changes") or {}).items() if k in ("scripts_disabled", "collisions_disabled", "physics_disabled")}
                if flags:
                    kit.did("asking you to confirm the region's switches")
                return text(await call("SojournLand", "regionDebug", GATED_TIMEOUT, **flags))
            if act == "terraform":
                how = args.get("how") or "raise"
                kit.did(f"terraforming: {how}")
                return text(await call("SojournLand", "terraform", GATED_TIMEOUT, action=how, **a,
                                       **strip(args, "size", "seconds", "height")))
            if act == "holdings":
                return text(await call("SojournLand", "holdings", **strip(args, "group")))
            return error(f"unknown action {act}")
        except ApiError as e:
            return error(str(e))

    # --- the map --------------------------------------------------------------------------------------------------

    @tool("map", "Where they are, and the world map. action: where (the location line: region, parcel, position, rating, "
          "and whether flying, building, pushing, voice and scripts are allowed: \"can I fly here?\"); stats (\"is it me or "
          "the region?\": their frame rate, the network, the region's time dilation and load, and a verdict in a few "
          "words); show (the map board up in front of them: region (a name) and at [x, y] for a place, pick: true to pick "
          "it so going there is one press of its Go key, which is theirs; nothing given: around them); hide; friends "
          "(online friends, and where those who let them see are); track (a beacon leading them to a person, who, or a "
          "place, region and at; it ends on arrival); untrack.",
          schema(["action"], action=["where", "stats", "show", "hide", "friends", "track", "untrack"], region=str,
                 at={"description": "[x, y] (or [x, y, z] for track)"}, pick=bool, across=float, who=str))
    async def map_(args):
        act = args["action"]
        try:
            if act in ("where", "stats", "hide", "friends"):
                if act == "hide":
                    kit.did("putting the map away")
                return text(await call("SojournMap", act))
            if act == "show":
                kit.did("showing the map")
                a = strip(args, "region", "pick", "across")
                if args.get("at") is not None:
                    a["at"] = json.loads(args["at"]) if isinstance(args["at"], str) else args["at"]
                return text(await call("SojournMap", "show", **a))
            if act == "track":
                if args.get("who"):
                    pid, name = await someone(args["who"])
                    kit.did(f"a beacon to {name or 'them'}")
                    return text(await call("SojournBeacons", "track", avatar=pid, **({"name": name} if name else {})))
                at = args.get("at")
                at = json.loads(at) if isinstance(at, str) else at
                if not at:
                    return error("track: who, or at [x, y] (with region for another)")
                kit.did("a beacon there")
                return text(await call("SojournBeacons", "track", at=at, **strip(args, "region")))
            kit.did("taking the beacon down")
            return text(await call("SojournBeacons", "untrack"))
        except (ApiError, ValueError) as e:
            return error(str(e))

    # --- people -----------------------------------------------------------------------------------------------------

    @tool("people", "Something about one person (who: a name, an id, 'that', or me for the person's own avatar where it "
          "fits). action: block, unblock (their chat, IMs, offers and voice stop reaching the person; an object pointed "
          "at blocks the object); draw (as: always, whole whatever their complexity; never, a plain jelly doll; normal); "
          "notes (the person's private profile notes on them); set_notes (text; append: true adds a line instead of "
          "replacing them); reset_skeleton (a body left deformed by an animation or mesh; me: their own, reset for "
          "everyone who sees them); refresh_attachments (attachments missing or wrong; me: theirs attached again); "
          "resync (everyone's animations restarted together here, so people dancing together move in step; no who). "
          "Voice volume and mutes are the voice tool's; friends' rights, contact sets and pseudonyms SojournPeople's.",
          schema(["action"], action=["block", "unblock", "draw", "notes", "set_notes", "reset_skeleton",
                                     "refresh_attachments", "resync"], who=str, text=str, append=bool,
                 **{"as": ["always", "never", "normal"]}))
    async def people(args):
        act, who = args["action"], args.get("who") or ""
        try:
            if act == "resync":
                kit.did("putting everyone's animations in step")
                return text(await call("SojournAvatars", "resyncAnimations") or {"ok": True})
            if act in ("reset_skeleton", "refresh_attachments"):
                a = {}
                if not is_me(who):
                    a["id"] = (await someone(who))[0]
                kit.did("resetting the skeleton" if act == "reset_skeleton" else "refreshing attachments")
                return text(await call("SojournAvatars", "resetSkeleton" if act == "reset_skeleton" else "refreshAttachments", **a))
            if not who:
                return error("who: a name, an id or 'that'")
            t = kit.target or {}
            if act in ("block", "unblock") and is_that(who) and t.get("id") and t.get("kind") not in ("avatar", "ground") and not t.get("worn_by"):
                oid = t.get("root_id") or t["id"]
                kit.did("blocking it" if act == "block" else "unblocking it")
                if act == "block":
                    return text(await call("TesselAgent", "mute", id=oid, type="object") or {"blocked": oid})
                return text(await call("TesselAgent", "unmute", id=oid) or {"unblocked": oid})
            pid, name = await someone(who)
            if act == "block":
                kit.did(f"blocking {name or 'them'}")
                return text(await call("TesselAgent", "mute", id=pid) or {"blocked": name})
            if act == "unblock":
                kit.did(f"unblocking {name or 'them'}")
                return text(await call("TesselAgent", "unmute", id=pid) or {"unblocked": name})
            if act == "draw":
                kit.did("changing how they're drawn")
                return text(await call("SojournAvatars", "render", id=pid, **{"as": args.get("as") or "normal"}) or {"ok": True})
            if act == "notes":
                kit.status("reading your notes")
                return text(await call("SojournPeople", "notes", to=pid))
            if act == "set_notes":
                kit.did("writing your notes")
                return text(await call("SojournPeople", "setNotes", to=pid, text=args.get("text") or "", append=bool(args.get("append"))))
            return error(f"unknown action {act}")
        except ApiError as e:
            return error(str(e))

    @tool("me", "The person's own account. action: display_name (theirs, and whether it can change now); "
          "set_display_name (name, or reset: true for their username; they confirm the exact name; on Second Life once a "
          "week); money (their balance, and what they received and spent this session, from and to whom); status (away, "
          "do not disturb, and their autoresponses' words); set_response (kind: dnd, away, teleports or friendships; "
          "text: the words in their own phrasing, which they confirm; empty for the standard sentence). Away, do not "
          "disturb and the switches that turn things away (reject_teleports, friends_only_ims, ...) are settings.",
          schema(["action"], action=["display_name", "set_display_name", "money", "status", "set_response"], name=str,
                 reset=bool, kind=["dnd", "away", "teleports", "friendships"], text=str, on=bool))
    async def me(args):
        act = args["action"]
        try:
            if act == "display_name":
                return text(await call("SojournPeople", "displayName"))
            if act == "set_display_name":
                kit.did("asking you to confirm your display name")
                return text(await call("SojournPeople", "setDisplayName", GATED_TIMEOUT, **strip(args, "name", "reset")))
            if act == "money":
                return text(await call("SojournPeople", "money"))
            if act == "status":
                return text(await call("SojournPeople", "status"))
            if act == "set_response":
                kit.did("asking you to confirm the words")
                return text(await call("SojournPeople", "setResponse", GATED_TIMEOUT, kind=args.get("kind") or "dnd",
                                       text=args.get("text") or "", **strip(args, "on")))
            return error(f"unknown action {act}")
        except ApiError as e:
            return error(str(e))

    @tool("group_chat", "Their groups' chat. action: list (each group's chat, on or off, snoozed); info (group: who's in its "
          "chat, the moderators, whether they moderate); snooze (group; minutes, 15 by default, 0 ends it: its lines wait "
          "until then); on, off (receive the group's chat, or leave it and stay out); moderate (group, who, text: false "
          "stops that member's text in the chat for everyone, true allows it again; they confirm). group: its name or id.",
          schema(["action"], action=["list", "info", "snooze", "on", "off", "moderate"], group=str, minutes=float,
                 who=str, text=bool))
    async def group_chat(args):
        act = args["action"]
        try:
            if act == "list":
                return text(await call("SojournPeople", "groupChat"))
            if not args.get("group"):
                return error("group: its name or id")
            g = {"group": args["group"]}
            if act == "info":
                return text(await call("SojournPeople", "groupChat", **g))
            if act == "snooze":
                kit.did("snoozing the group's chat")
                return text(await call("SojournPeople", "snoozeGroupChat", **g, **strip(args, "minutes")))
            if act in ("on", "off"):
                kit.did("turning the group's chat " + act)
                return text(await call("SojournPeople", "setGroupChat", **g, on=act == "on"))
            if act == "moderate":
                if not args.get("who") or args.get("text") is None:
                    return error("moderate: who, and text: false (stop) or true (allow)")
                who = args["who"]
                to = (await someone(who))[0] if is_that(who) or UUID.fullmatch(who) else who
                kit.did("asking you to confirm the moderation")
                return text(await call("SojournPeople", "moderateGroupChat", GATED_TIMEOUT, **g, to=to, text=bool(args["text"])))
            return error(f"unknown action {act}")
        except ApiError as e:
            return error(str(e))

    async def group_id(words):
        if UUID.fullmatch(words or ""):
            return words, None
        gs = (await call("SojournPeople", "groupChat")).get("groups", [])
        best = max(((like(g.get("name") or "", words), g) for g in gs), key=lambda x: x[0], default=(0, None))
        if best[0] < 0.5:
            raise ApiError(f"no group of theirs called {words!r}")
        return best[1]["group_id"], best[1].get("name")

    @tool("voice", "Voice chat. action: status (who's in voice nearby or in the call, and who's speaking); mute, unmute (who: "
          "one person's voice in the person's own hearing only); volume (who, volume 0-2, 1 as they come); mute_mic (their "
          "microphone off toward everyone; turning it back on is their own button); call (who: one to one); call_group "
          "(group: its name); conference (who: several names, comma-separated); hangup; decline (a call ringing; answering "
          "is their own press); calls (the switches that turn calls away before they ring, and how many they did); "
          "moderate (who, mute: true or false: for everyone, as a moderator of the parcel, estate or call; they confirm); "
          "moderate_all (mute: everyone else).",
          schema(["action"], action=["status", "mute", "unmute", "volume", "mute_mic", "call", "call_group", "conference",
                                     "hangup", "decline", "calls", "moderate", "moderate_all"], who=str, group=str,
                 volume=float, mute=bool))
    async def voice(args):
        act = args["action"]
        try:
            if act in ("status", "calls"):
                return text(await call("SojournVoice", act))
            if act in ("hangup", "decline"):
                kit.did("leaving the call" if act == "hangup" else "declining the call")
                return text(await call("SojournVoice", act) or {"ok": True})
            if act == "mute_mic":
                kit.did("muting your mic")
                return text(await call("SojournVoice", "mute"))
            if act == "moderate_all":
                mute = args.get("mute", True)
                kit.did("asking you to confirm muting everyone" if mute else "asking you to confirm letting everyone speak")
                return text(await call("SojournVoice", "moderateAll", GATED_TIMEOUT, mute=bool(mute)))
            if act == "call_group":
                gid, gname = await group_id(args.get("group") or args.get("who") or "")
                kit.did(f"calling {gname or 'the group'}")
                return text(await call("SojournVoice", "callGroup", id=gid, **({"name": gname} if gname else {})))
            if act == "conference":
                names = [w.strip() for w in re.split(r",|\band\b", args.get("who") or "") if w.strip()]
                if len(names) < 1:
                    return error("who: the people to call, comma-separated")
                ids = [(await someone(n))[0] for n in names]
                kit.did("starting a conference call")
                return text(await call("SojournVoice", "conference", ids=ids))
            if not args.get("who"):
                return error("who: a name, an id or 'that'")
            pid, name = await someone(args["who"])
            if act in ("mute", "unmute"):
                kit.did(("muting " if act == "mute" else "unmuting ") + (name or "them"))
                await call("SojournVoice", "peerMute", id=pid, mute=act == "mute")
                return text({"muted" if act == "mute" else "unmuted": name or pid, "for": "the person only"})
            if act == "volume":
                kit.did(f"{name or 'their'} volume")
                await call("SojournVoice", "peerVolume", id=pid, volume=float(args.get("volume", 1)))
                return text({"volume": args.get("volume", 1), "who": name or pid})
            if act == "call":
                kit.did(f"calling {name or 'them'}")
                return text(await call("SojournVoice", "call", id=pid, **({"name": name} if name else {})))
            if act == "moderate":
                mute = args.get("mute", True)
                kit.did(f"asking you to confirm {'muting' if mute else 'unmuting'} {name or 'them'} for everyone")
                return text(await call("SojournVoice", "moderate", GATED_TIMEOUT, id=pid, mute=bool(mute)))
            return error(f"unknown action {act}")
        except ApiError as e:
            return error(str(e))

    # --- the avatar's own -------------------------------------------------------------------------------------------

    @tool("ao", "Their animation overrider (the client's own AO, playing their stands, walks, sits... from an AO folder) and "
          "the pose stand. action: status (on or off, the sets, the one playing, what plays now); on, off; stands (on: "
          "true or false: whether it overrides standing too); select (set: a set's name); next, previous (the next stand "
          "or animation of what plays now); play (step: an animation's name in what plays now); set (one set's states and "
          "animations); pose (the pose stand for adjusting their shape or fitting things: pose, a pose's name, or none "
          "for the first; again for another); pose_off; poses (the stand's poses). Editing sets (adding or removing "
          "animations, new sets) and picking another folder: SojournAo through client_api.",
          schema(["action"], action=["status", "on", "off", "stands", "select", "next", "previous", "play", "set", "pose",
                                     "pose_off", "poses"], set=str, step=str, pose=str, on=bool))
    async def ao(args):
        act = args["action"]
        try:
            if act in ("on", "off"):
                kit.did(f"turning your AO {act}")
                return text(await call("SojournAo", "on", on=act == "on"))
            if act == "stands":
                kit.did("changing your AO's stands")
                return text(await call("SojournAo", "stands", on=bool(args.get("on", True))))
            if act == "select":
                if not args.get("set"):
                    return error("set: a set's name (status lists them)")
                kit.did(f"AO set: {args['set']}")
                return text(await call("SojournAo", "select", name=args["set"]))
            if act in ("next", "previous"):
                kit.did("the next animation" if act == "next" else "the previous animation")
                return text(await call("SojournAo", act))
            if act == "play":
                return text(await call("SojournAo", "play", step=args.get("step") or ""))
            if act == "set":
                return text(await call("SojournAo", "set", **({"name": args["set"]} if args.get("set") else {})))
            if act == "pose":
                kit.did("the pose stand")
                return text(await call("SojournAo", "poseStand", **strip(args, "pose")))
            if act == "pose_off":
                kit.did("off the pose stand")
                return text(await call("SojournAo", "poseStandOff"))
            if act == "poses":
                return text(await call("SojournAo", "poses"))
            return text(await call("SojournAo", "status"))
        except ApiError as e:
            return error(str(e))

    @tool("environment", "The sky and water they see (their own environment, seen only by them). action: use (preset: "
          "sunrise, noon, legacy noon, sunset or midnight; or name: a sky, water or day cycle of theirs or the Library's: a "
          "sky or water replaces that part, a day cycle both); shared (back to the place's own); list (their skies, waters "
          "and day cycles and the Library's; kind and name narrow it); clouds (paused: true holds them still, false lets "
          "them move); time (their own time of day over whatever sky is in use, held still, as the viewer's Personal "
          "Lighting: hours 0-24, 6 sunrise, 12 noon, 18 sunset, 0 midnight, golden hour about 7 or 17.3, blue hour about "
          "5.4 or 18.4; or later: hours from now, negative for earlier, \"a little later\" about 0.5-1; by sun moves only the "
          "sun and moon on the sky as it is, colours kept, by day shows the day cycle (theirs, else the place's) at that time, "
          "colours and all (better for night or a big change); left out, sun over a fixed sky such as a preset, day while a day "
          "cycle shows; run: true lets the day cycle go on from there, only if they ask; sun_azimuth and sun_elevation in degrees place the sun exactly, azimuth "
          "0 east, 90 south, 180 west, 270 north; back to the place's own time is shared, a preset's own sun is using it "
          "again); now (what's in use and whose, and the time the sun stands at).",
          schema(["action"], action=["use", "shared", "list", "clouds", "time", "now"], preset=["sunrise", "noon", "legacy noon",
                                                                                                 "sunset", "midnight"],
                 name=str, kind=["sky", "water", "daycycle"], paused=bool, hours=float, later=float, by=["sun", "day"],
                 run=bool, sun_azimuth=float, sun_elevation=float))
    async def environment(args):
        act = args["action"]
        try:
            if act == "use":
                a = strip(args, "preset") or strip(args, "name")
                if not a:
                    return error("use: a preset or a name")
                kit.did("changing your sky")
                return text(await call("SojournEnvironment", "use", **a))
            if act == "shared":
                kit.did("back to the place's own sky")
                return text(await call("SojournEnvironment", "shared"))
            if act == "list":
                return text(await call("SojournEnvironment", "mine", **strip(args, "kind", "name")))
            if act == "clouds":
                kit.did("holding the clouds still" if args.get("paused", True) else "letting the clouds move")
                return text(await call("SojournEnvironment", "clouds", paused=bool(args.get("paused", True))))
            if act == "time":
                a = strip(args, "hours", "later", "by", "run", "sun_azimuth", "sun_elevation")
                if not any(k in a for k in ("hours", "later", "sun_azimuth")):
                    return error("time: hours, later, or sun_azimuth and sun_elevation")
                kit.did("moving the sun")
                return text(await call("SojournEnvironment", "time", **a))
            r = await call("SojournEnvironment", "get")
            return text({k: r[k] for k in ("sky_source", "parcel", "track", "personal", "time") if k in r})
        except ApiError as e:
            return error(str(e))

    # --- building ---------------------------------------------------------------------------------------------------

    @tool("undo", "Take back building edits, as the viewer's undo: what TesselBuild changed (modify, create, link, unlink; "
          "materials; groups), only the parts it changed; a whole build made in one call is one step. action: undo (steps, "
          "1 by default; what: 'that' or an id for that object's last edits), redo, history (what undo would take back, "
          "newest first). A creation is undone by deleting it, which the person confirms.",
          schema(["action"], action=["undo", "redo", "history"], steps=int, what=str))
    async def undo(args):
        act = args["action"]
        try:
            if act == "history":
                return text(await call("SojournBuild", "history"))
            a = {"steps": int(args.get("steps") or 1)}
            if args.get("what"):
                a["id"] = (await things(args["what"]))[0]
            kit.did("undoing it" if act == "undo" else "redoing it")
            return text(await call("SojournBuild", act, GATED_TIMEOUT, **a))
        except ApiError as e:
            return error(str(e))

    @tool("scripts", "The scripts in objects, as the Build menu's script chores. what: 'that', an object's name in view, "
          "or ids (whole linksets). action: list (each script, its prim and whether it runs); info (how many scripts and "
          "how many run); reset; stop, start (set not running or running); recompile (target: mono, the default, lsl2, "
          "luau or lsl-luau where the region runs them); remove (the person confirms). name: only scripts called that.",
          schema(["action", "what"], action=["list", "info", "reset", "stop", "start", "recompile", "remove"],
                 what={"description": "'that', a name in view, an id or a list of ids"}, name=str,
                 target=["mono", "lsl2", "luau", "lsl-luau"]))
    async def scripts(args):
        act = args["action"]
        try:
            ids = await things(args.get("what"))
            a = {"ids": ids, **strip(args, "name")}
            if act == "list":
                return text(await call("SojournBuild", "scripts", **a))
            if act == "info":
                return text(await call("SojournBuild", "scriptInfo", ids=ids))
            kit.did({"reset": "resetting the scripts", "stop": "stopping the scripts", "start": "starting the scripts",
                     "recompile": "recompiling the scripts", "remove": "asking you to confirm removing the scripts"}[act])
            if act == "reset":
                return text(await call("SojournBuild", "resetScripts", 60, **a))
            if act in ("stop", "start"):
                return text(await call("SojournBuild", "runScripts", 60, running=act == "start", **a))
            if act == "recompile":
                return text(await call("SojournBuild", "recompile", 120, target=args.get("target") or "mono", **a))
            return text(await call("SojournBuild", "removeScripts", GATED_TIMEOUT, **a))
        except (ApiError, KeyError) as e:
            return error(str(e))

    # --- inventory --------------------------------------------------------------------------------------------------

    @tool("item", "One inventory item or folder (an id, or the words for an item). action: picture (what it looks like: "
          "its thumbnail, or a texture or snapshot's own image; an error when it has none); favorite, unfavorite (the "
          "viewer's Favorites); favorites (everything marked favourite); thumbnail (set its picture: from: view, a picture "
          "of their view now; or a texture of theirs by id or words; or remove).",
          schema(["action"], action=["picture", "favorite", "unfavorite", "favorites", "thumbnail"], item=str,
                 **{"from": str}, size=int))
    async def item(args):
        act = args["action"]
        try:
            if act == "favorites":
                return text(await call("SojournInventory", "favorites", 60))
            if not args.get("item"):
                return error("item: an id or the words for it")
            try:
                iid, name = await kit.item(args["item"])
            except Ambiguous as a:
                return a.result()
            if act == "picture":
                kit.did(f"looking at {name or 'it'}")
                return image(await call("SojournInventory", "picture", 30, id=iid, size=int(args.get("size") or 256)))
            if act in ("favorite", "unfavorite"):
                kit.did("marking it" if act == "favorite" else "unmarking it")
                return text(await call("SojournInventory", "favorite", id=iid, on=act == "favorite"))
            src = (args.get("from") or "").strip()
            if not src:
                return error("thumbnail: from (view, a texture, or remove)")
            kit.did("setting its picture")
            if src.lower() in ("view", "the view", "my view"):
                return text(await call("SojournInventory", "thumbnail", 60, id=iid, view=True))
            if src.lower() in ("remove", "none"):
                return text(await call("SojournInventory", "thumbnail", 30, id=iid, remove=True))
            tex, _ = await kit.item(src, type="texture")
            return text(await call("SojournInventory", "thumbnail", 60, id=iid, item_id=tex))
        except Ambiguous as a:
            return a.result()
        except ApiError as e:
            return error(str(e))

    # --- safety -----------------------------------------------------------------------------------------------------

    @tool("keep_away", "Things that get in the person's way, kept out of it. action: hide (what: 'that' or an id: an object, "
          "or something someone wears, gone from their view at once until they teleport; for_good: true keeps it gone); "
          "unhide (what, or all: true); hidden (everything hidden and blocked); sounds (what plays and was heard lately, "
          "whose, to block one); block_sound (sound: its id, or owner: someone's worn, rezzed or gesture sounds); "
          "particles (what's emitting particles around, whose); block_particles (what: a source, its owner's particles and "
          "the rest of them blocked); release_keys (take back the controls scripts hold: a stuck vehicle or weapon "
          "keeping the sticks); bumps (who bumped, pushed or hit them). People themselves: the people tool's block.",
          schema(["action"], action=["hide", "unhide", "hidden", "sounds", "block_sound", "particles", "block_particles",
                                     "release_keys", "bumps"], what=str, for_good=bool, all=bool, sound=str, owner=str))
    async def keep_away(args):
        act = args["action"]
        try:
            if act in ("hidden", "sounds", "particles", "bumps"):
                return text(await call("SojournSafety", act))
            if act == "release_keys":
                kit.did("taking back your controls")
                return text(await call("SojournSafety", "releaseKeys"))
            if act == "unhide" and args.get("all"):
                kit.did("bringing hidden things back")
                return text(await call("SojournSafety", "rerender", all=True))
            if act == "block_sound":
                a = strip(args, "sound")
                if args.get("owner"):
                    a["owner"] = args["owner"] if UUID.fullmatch(args["owner"]) else (await someone(args["owner"]))[0]
                kit.did("blocking the sound")
                return text(await call("SojournSafety", "blockSound", **a))
            if not args.get("what"):
                return error("what: 'that' or an id")
            oid = (await things(args["what"]))[0]
            if act == "hide":
                kit.did("hiding it")
                return text(await call("SojournSafety", "derender", id=oid, permanent=bool(args.get("for_good"))))
            if act == "unhide":
                kit.did("bringing it back")
                return text(await call("SojournSafety", "rerender", id=oid))
            if act == "block_particles":
                kit.did("blocking those particles")
                return text(await call("SojournSafety", "blockParticleOwner", id=oid))
            return error(f"unknown action {act}")
        except ApiError as e:
            return error(str(e))

    return [land, map_, people, me, group_chat, voice, ao, environment, undo, scripts, item, keep_away]
