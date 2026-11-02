using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Rule = Sojourn.Core.Agents.PrivateMode.Rule;

namespace Sojourn.Session.Private;

/// <summary>
/// What the assistant (or any caller) can still do while incognito is on and blind: an allowlist, because ~50 tools and
/// client_api reach every op. Each op is here because of what it returns: nothing about the world (done or not),
/// or only the person's own things (settings, their outfits, their panel). Anything that reads what's said, who is
/// around, where they are, or a picture of the view is left out, and so is anything that would act on a place or a
/// person the assistant would have to know by name (teleports, giving, paying). Things nearby are named only by the
/// spell's handles (the pointer's, the situation's). docs/assistant-incognito.md > The gate has the reasoning per group.
/// </summary>
public static class Allowlist
{
    static readonly Rule Pass = new(Result: PrivateMode.Pass);
    static readonly Rule Plain = new();
    static readonly Rule Own = new(Result: PrivateMode.Pass, Ids: true);    // the person's own inventory, by id
    static readonly Rule OwnPlain = new(Ids: true);

    static string? HandleOf(JsonObject a, string key, string kind, bool optional = false)
    {
        var v = a.Str(key);
        if (v == null) return optional ? null : $"{key}: a {kind} handle (the pointer's or the situation's)";
        return PrivateMode.IsHandle(v) && v.StartsWith(kind + "-") ? null : $"{key}: a {kind} handle (the pointer's or the situation's), not a name or id";
    }

    static string? NoPosition(JsonObject a) => a.ContainsKey("position") ? "a handle, not a position" : null;

    public static void Fill(PrivateMode p)
    {
        // the assistant's own surfaces: its panel and look, the person's choices about it, the hello, a word to check by
        // typing (it returns nothing; the answer is the person's own words, as a request is); the pointer blinded
        foreach (var op in new[] { "hello", "panel", "look", "model", "prefs", "dev", "say", "bringPanel", "resetMenus", "askTyped" }) p.Allow("SojournAssistant", op, Pass);
        p.Allow("SojournAssistant", "pointer", new Rule(Result: r => new JsonObject
        {
            ["target"] = p.BlindTarget((r as JsonObject)?["target"]),
            ["looking"] = p.BlindTarget((r as JsonObject)?["looking"]),
        }));
        // the person's own clothes and things to wear (not notecards, landmarks, pictures or scripts, which can carry
        // places and other people's words)
        p.Allow("SojournAssistant", "findInventory", new Rule(Result: WearablesOnly));
        // the ears' words: only what the person wears (people, places and their folders, which can carry names, kept back)
        p.Allow("SojournAssistant", "hearing", new Rule(Result: r => new JsonObject
        {
            ["worn"] = (r as JsonObject)?["worn"]?.DeepClone() ?? new JsonArray(),
            ["blind"] = true,
        }));
        foreach (var op in new[] { "status", "situation", "set", "draft", "always" }) p.Allow("SojournPrivate", op, Pass);
        // RLV's hold on the person (docs/rlv.md): what's restricted, not which objects (their names can carry people's)
        p.Allow("SojournRlv", "status", new Rule(Result: r =>
        {
            if (r?.DeepClone() is not JsonObject o) return r;
            if (o["held"] is JsonArray held)
                foreach (var h in held.OfType<JsonObject>()) { h.Remove("object"); h.Remove("name"); }
            return o;
        }));

        // settings: values are the person's own (graphics, sound, the menu's switches)
        p.Allow("SojournSettings", "get", Pass);
        p.Allow("SojournSettings", "set", Pass);
        // (only the view's two: other keys hold things like the ids of avatars hidden from view)
        var view = new Rule(Check: (a, _) => a.Str("key") is "RenderFarClip" or "CameraAngle" ? null : "key: RenderFarClip or CameraAngle", Result: PrivateMode.Pass);
        p.Allow("LLViewerControl", "get", view);
        p.Allow("LLViewerControl", "set", view);
        p.Allow("SojournAudio", "devices", Pass);

        // what the assistant shows the person: at view, wrist, room or a handle; back only what it named itself
        p.Allow("SojournShow", "show", new Rule(Result: r => r is JsonObject o && o.ContainsKey("error") ? PrivateMode.Plain(r) : new JsonObject { ["id"] = (r as JsonObject)?["id"]?.DeepClone(), ["ok"] = true }));
        p.Allow("SojournShow", "remove", Plain);
        p.Allow("SojournShow", "list", new Rule(Result: ShownList));

        // wearing
        p.Allow("LLAppearance", "wearOutfit", OwnPlain);
        p.Allow("LLAppearance", "wearItems", OwnPlain);
        p.Allow("LLAppearance", "detachItems", OwnPlain);
        p.Allow("LLAppearance", "getOutfitsList", Own);
        p.Allow("LLAppearance", "getOutfitItems", Own);
        p.Allow("TesselAgent", "attach", OwnPlain);
        p.Allow("TesselAgent", "detach", new Rule(Check: (a, _) => a.ContainsKey("object_id") ? "detach by item_id" : null, Ids: true));
        p.Allow("TesselAgent", "rebake", Plain);
        p.Allow("LLGesture", "getActiveGestures", Own);
        p.Allow("LLGesture", "startGesture", OwnPlain);
        p.Allow("LLGesture", "stopGesture", OwnPlain);
        p.Allow("LLAgent", "playAnimation", OwnPlain);
        p.Allow("LLAgent", "stopAnimation", OwnPlain);

        // the body: sit and touch by handle, stand, stop, walk a little, go home
        p.Allow("LLAgent", "requestSit", new Rule(Check: (a, _) => NoPosition(a) ?? HandleOf(a, "obj_uuid", "thing", optional: true)));
        p.Allow("LLAgent", "requestTouch", new Rule(Check: (a, _) => NoPosition(a) ?? HandleOf(a, "obj_uuid", "thing")));
        p.Allow("LLAgent", "lookAt", new Rule(Check: (a, _) => NoPosition(a) ?? (HandleOf(a, "obj_uuid", "thing") is null ? null : HandleOf(a, "obj_uuid", "person"))));
        p.Allow("LLAgent", "requestStand", Plain);
        p.Allow("LLAgent", "stopAutoPilot", Plain);
        p.Allow("LLAgent", "stopAnimating", new Rule(Check: (a, _) => a.ContainsKey("id") ? "everything at once only (no id)" : null));
        p.Allow("TesselAgent", "move", Plain);
        p.Allow("TesselAgent", "stop", Plain);
        p.Allow("TesselAgent", "goHome", Plain);
        p.Allow("LLAppViewer", "requestQuit", Plain);

        // talking: the person's own words out, into nearby chat or a conversation by its handle
        p.Allow("LLChatBar", "sendChat", Plain);
        p.Allow("TesselIM", "send", new Rule(Check: (a, _) => a.ContainsKey("group_id") ? "a conversation's handle (session_id) or a person's (to)"
            : a.ContainsKey("session_id") ? HandleOf(a, "session_id", "chat") : HandleOf(a, "to", "person")));
        p.Allow("TesselIM", "markRead", new Rule(Check: (a, _) => HandleOf(a, "session_id", "chat")));

        // keeping people out: block, mute in voice, quieter, not drawn; by handle
        p.Allow("TesselAgent", "mute", new Rule(Check: (a, _) => HandleOf(a, "id", "person") is null ? null : HandleOf(a, "id", "thing")));
        p.Allow("TesselAgent", "unmute", new Rule(Check: (a, _) => HandleOf(a, "id", "person") is null ? null : HandleOf(a, "id", "thing")));
        p.Allow("SojournVoice", "peerMute", new Rule(Check: (a, _) => HandleOf(a, "id", "person")));
        p.Allow("SojournVoice", "peerVolume", new Rule(Check: (a, _) => HandleOf(a, "id", "person")));
        p.Allow("SojournAvatars", "render", new Rule(Check: (a, _) => a.ContainsKey("name") ? "id: a person handle" : HandleOf(a, "id", "person")));
        foreach (var op in new[] { "mute", "hangup", "decline" }) p.Allow("SojournVoice", op, Plain);

        // reporting abuse: by handle, in the person's words; the confirmation (only on the panel) shows the rest
        p.Allow("SojournReport", "categories", Pass);
        p.Allow("SojournReport", "draft", new Rule(
            Check: (a, _) => a.ContainsKey("where") ? "where: not while incognito" : (a.ContainsKey("about") ? HandleOf(a, "about", "person") : null) ?? (a.ContainsKey("object") ? HandleOf(a, "object", "thing") : null),
            Result: r => r is JsonObject o && o["draft"] is { } d ? new JsonObject { ["draft"] = d.DeepClone() } : PrivateMode.Plain(r)));
        p.Allow("SojournReport", "send", OwnPlain);
        // Sojourn itself: its name, version and disclaimer (a problem report is refused: it sends the session's log)
        p.Allow("SojournRelease", "about", Pass);

        // pictures and videos for the person (never shown to the assistant), named as incognito ones
        p.Allow("SojournCapture", "snapshot", new Rule(Check: (a, _) => { a["name"] = "incognito"; return null; }));
        p.Allow("SojournCapture", "record", new Rule(Check: (a, _) => { a["name"] = "incognito"; return null; }));
        p.Allow("SojournCapture", "stop", Plain);
        p.Allow("SojournCapture", "status", Plain);

        // the HUD board in the headset, up or down (not a picture of it: HUDs show names and chat)
        p.Allow("SojournHud", "show", Plain);

        // batch T after wave 1-3 (2026-10-29): the avatar's own AO and pose stand (its sets and animations are the
        // person's own things, as their outfits are; the AO's folder is picked only outside a spell)
        foreach (var op in new[] { "status", "on", "stands", "select", "next", "previous", "play", "set", "poses", "poseStandOff" })
            p.Allow("SojournAo", op, Pass);
        p.Allow("SojournAo", "poseStand", Own);
        // the person's own sky: presets, their own and the Library's settings (not the place's: get names the parcel)
        p.Allow("SojournEnvironment", "mine", Pass);
        p.Allow("SojournEnvironment", "use", Own);
        p.Allow("SojournEnvironment", "shared", Plain);
        p.Allow("SojournEnvironment", "clouds", Pass);
        p.Allow("SojournEnvironment", "time", Pass);   // the answer names only the sky's source ("the region's day")
        // statistics at a glance name nothing (frame rates, time dilation, counts); the map board around the person is
        // shown to them, not the caller (a place named is refused: the caller can't know one)
        p.Allow("SojournMap", "stats", Pass);
        p.Allow("SojournMap", "show", new Rule(Check: (a, _) => a.ContainsKey("region") || a.ContainsKey("at") ? "the map around them only (no region or at)" : null));
        p.Allow("SojournMap", "hide", Plain);
        p.Allow("SojournBeacons", "untrack", Plain);
        // fixing how avatars look: their own, or someone by handle; resync names nothing
        static string? MeOrPerson(JsonObject a) => a.ContainsKey("name") ? "id: a person handle, or none for their own" : HandleOf(a, "id", "person", optional: true);
        p.Allow("SojournAvatars", "resetSkeleton", new Rule(Check: (a, _) => MeOrPerson(a)));
        p.Allow("SojournAvatars", "refreshAttachments", new Rule(Check: (a, _) => MeOrPerson(a)));
        p.Allow("SojournAvatars", "resyncAnimations", Plain);
        // keeping things out of view by handle, and taking back controls a script holds (their names are the
        // viewer's: forward, left...)
        p.Allow("SojournSafety", "derender", new Rule(Check: (a, _) => HandleOf(a, "id", "thing")));
        p.Allow("SojournSafety", "rerender", new Rule(Check: (a, _) => a.Bool("all", false) ? null : HandleOf(a, "id", "thing")));
        p.Allow("SojournSafety", "blockParticleOwner", new Rule(Check: (a, _) => HandleOf(a, "id", "thing")));
        p.Allow("SojournSafety", "releaseKeys", Pass);
        // away and do not disturb, with their own autoresponse words; their avatar's hover height
        p.Allow("SojournPeople", "status", Pass);
        p.Allow("SojournPeople", "setStatus", Plain);
        p.Allow("SojournPeople", "hoverHeight", Pass);
        // a 360 for the person, named as an incognito one, as pictures are
        p.Allow("SojournCapture", "panorama", new Rule(Check: (a, _) => { a["name"] = "incognito"; return null; }));

        // letting it see, everything runs as usual but this: the person's pictures and videos are still named as
        // incognito ones (the assistant's file tools never read those, during the spell or after it), and so are
        // backups of their builds (Documents\Sojourn\Backups; harness.py keeps the file tools off those too)
        p.AllowSeeing("SojournCapture", "snapshot", a => { a["name"] = "incognito"; return null; });
        p.AllowSeeing("SojournCapture", "record", a => { a["name"] = "incognito"; return null; });
        p.AllowSeeing("SojournCapture", "panorama", a => { a["name"] = "incognito"; return null; });
        p.AllowSeeing("SojournBuild", "backup", a => { a["name"] = "incognito"; return null; });
    }

    static readonly HashSet<string> Wearable = new(StringComparer.OrdinalIgnoreCase)
    {
        "shape", "skin", "hair", "eyes", "shirt", "pants", "shoes", "socks", "jacket", "gloves", "undershirt", "underpants", "skirt",
        "alpha", "tattoo", "physics", "universal", "object", "gesture", "animation",
    };

    static JsonNode? WearablesOnly(JsonNode? r)
    {
        if (r is not JsonObject o || o["items"] is not JsonArray items) return PrivateMode.Plain(r);
        var kept = new JsonArray();
        foreach (var it in items.OfType<JsonObject>().Where(i => Wearable.Contains(i.Str("type") ?? "")))
        {
            var c = new JsonObject();
            foreach (var k in new[] { "id", "name", "type", "folder", "worn", "score", "perms" }) if (it[k] is { } v) c[k] = v.DeepClone();
            kept.Add(c);
        }
        return new JsonObject { ["items"] = kept, ["note"] = "incognito: only things to wear" };
    }

    static readonly Regex Uuid = new("[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}");

    static JsonNode? ShownList(JsonNode? r)
    {
        if (r is not JsonObject o || o["shown"] is not JsonArray shown) return PrivateMode.Plain(r);
        var kept = new JsonArray();
        foreach (var e in shown.OfType<JsonObject>())
        {
            var at = e["at"] is JsonValue v && v.TryGetValue<string>(out var s) && s is "view" or "wrist" or "room" ? s : "a thing or person";
            var c = new JsonObject { ["id"] = e["id"]?.DeepClone(), ["at"] = at };
            if (e.Str("title") is { } t && !Uuid.IsMatch(t)) c["title"] = t;
            if (e["blocks"] is JsonArray b) c["blocks"] = new JsonArray(b.Select(x => x is JsonValue xv && xv.TryGetValue<string>(out var k) ? (JsonNode)JsonValue.Create(k) : JsonValue.Create("block")).ToArray());
            foreach (var k in new[] { "not_in_view", "timer_done" }) if (e[k] is { } f) c[k] = f.DeepClone();
            kept.Add(c);
        }
        return new JsonObject { ["shown"] = kept };
    }
}
