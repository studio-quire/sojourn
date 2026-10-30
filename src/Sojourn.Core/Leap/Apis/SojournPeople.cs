using System.Text.Json.Nodes;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Leap;

/// <summary>People and me (batch B2): friends' removal and rights, contact sets and pseudonyms, Away and Do Not
/// Disturb with the person's own autoresponse, typing, hover height, experiences, teleport history and favourites, pay
/// price buttons, places offered on the feed (2026-10-27); the reject switches and "only friends and groups can IM me",
/// a group's chat snoozed, off or moderated, money received and spent, the display name and profile notes
/// (2026-10-28). Agents/PeopleOps.cs, Presence.cs, Places.cs, ExperienceOps.cs, Rejects.cs, GroupChat.cs, Money.cs,
/// NamesAndNotes.cs. Claude Opus 5.5.</summary>
public sealed class SojournPeople : SessionApi
{
    public override string Name => "SojournPeople";
    public override string Desc => "Friends' rights and removal, contact sets, away and do not disturb, typing, hover height, experiences, teleport history, "
        + "favourites, pay prices, places offered, offers turned away, group chat snooze/off/moderation, money received and spent, display name, notes";

    public SojournPeople(AgentSession s) : base(s)
    {
        var p = s.People;
        var me = s.Presence;
        var places = s.Places;
        var xp = s.Experiences;
        var rj = s.Rejects;
        var gc = s.GroupChat;
        var nn = s.NamesAndNotes;

        // friends
        Op("friendRights", "{to (a friend's id or name), online?, map?, modify?} -> {rights_given, rights_had}: what a friend may do (see "
            + "me online, find me on the map, edit my objects) and what they let me; any of the three changes what I give them "
            + "(map needs online); the person confirms", (a, ct) => J(p.RightsAsync(a, ct)));
        Op("removeFriend", "{to} -> {removed}: end a friendship (edit rights are taken back first); the person confirms", (a, ct) => J(p.RemoveFriendAsync(a, ct)));
        Op("contactSets", "{name?} -> {sets: [{name, color, members}], pseudonyms}: the person's contact sets (Firestorm's: named, "
            + "coloured groups of people, friends or not, kept on this computer) and pseudonyms", a => p.ContactSets(a));
        Op("contactSet", "{name, color? ([r,g,b] 0-1 or \"#rrggbb\"), rename?, remove?} -> {name, color}: make or change a set (remove: delete it; "
            + "the person confirms that)", a => p.ContactSet(a));
        Op("contactSetAdd", "{name (the set), to | ids} -> {members}: people into a set", a => p.Members(a, add: true));
        Op("contactSetRemove", "{name (the set), to | ids} -> {members}: people out of a set", a => p.Members(a, add: false));
        Op("pseudonym", "{to, name} -> {pseudonym}: a name the person calls someone by, shown to them instead (empty clears it)", a => p.SetPseudonym(a));

        // presence
        Op("status", "-> {away, away_automatic, do_not_disturb, away_after_minutes, dnd_response, away_response_on, away_response, "
            + "typing_sent, typing_animation}", _ => me.Status());
        Op("setStatus", "{away?, do_not_disturb?} -> status: Away (the away animation; automatic after away_after_minutes without them, "
            + "which their being back clears) and Do Not Disturb (IMs kept but not shown, an autoresponse once per conversation, "
            + "objects' IMs and offers turned away)", a =>
        {
            if (a.Bool("away") is { } away) me.SetAway(away);
            if ((a.Bool("do_not_disturb") ?? a.Bool("dnd")) is { } dnd) me.SetDoNotDisturb(dnd);
            return me.Status();
        });
        Op("setResponse", "{kind: dnd|away|teleports|friendships, text, on? (away only: send it while away)} -> status: an "
            + "autoresponse's words, which must be the person's own (what they said it should say); the person confirms the exact "
            + "text; empty text: back to the standard sentence. teleports and friendships: what the reject switches answer with",
            a =>
        {
            var kind = (a.Str("kind") ?? "dnd").Trim().ToLowerInvariant();
            if (kind is not ("teleports" or "friendships")) return me.SetResponse(a);
            var text = (a.Str("text") ?? "").Trim();
            if (text.Length > Presence.MaxResponse) throw new LeapError($"{Presence.MaxResponse} characters at most");
            rj.SetResponse(kind, text);
            return rj.Status();
        });
        Op("typing", "{on, to? (an id: their IM; none: nearby)}: show others the person is writing (runs out after 30 s unless sent again)", a =>
        {
            me.Typing(a.Bool("on", true), a.Uuid("to"));
            return new JsonObject { ["typing"] = me.IsTyping };
        });
        Op("hoverHeight", "{height? (metres, -2 to 2)} -> {hover_height, sent?}: raise or lower the person's avatar so its feet meet "
            + "the floor (kept with the account)", async (a, ct) =>
            a.Num("height") is { } h ? await me.SetHoverHeightAsync(h, ct) : new JsonObject { ["hover_height"] = Math.Round(me.HoverHeight, 3) });

        // what's turned away
        Op("rejects", "-> {friends_only_ims, reject_teleports (off|all|not_friends), reject_friendships, reject_group_invites, "
            + "auto_accept_inventory (off|viewer|all), teleports_response, friendships_response, turned_away_this_session}: the person's "
            + "switches (each also a SojournSettings setting, section you)", _ => rj.Status());

        // display name and notes
        Op("displayName", "-> {display_name, username, is_default, can_change_now, next_change?}: the person's display name (Second "
            + "Life only: a change once a week)", (_, ct) => J(nn.ReadAsync(ct)));
        Op("setDisplayName", "{name | reset: true} -> {display_name, next_change}: change the person's display name (31 characters at "
            + "most; their username or reset: back to it); the person confirms the exact name", (a, ct) => J(nn.SetAsync(a, ct)));
        Op("notes", "{to (an id, or a friend's name)} -> {notes}: the person's private notes on someone (their profile's Notes)",
            (a, ct) => J(nn.NotesAsync(a, ct)));
        Op("setNotes", "{to, text, append?} -> {notes, was}: replace the notes (or add a line with append); was: what they said before",
            (a, ct) => J(nn.SetNotesAsync(a, ct)));

        // a group's chat
        Op("groupChat", "{group? (id or name)} -> one group's {chat_on, snoozed_until?, in_chat, i_moderate, members: [{id, name, moderator, "
            + "text_stopped}]}, or every group's {chat_on, snoozed_minutes_left?}", a =>
            a.Has("group") || a.Has("group_id") || a.Has("name") ? gc.Info(gc.Group(a)) : gc.List());
        Op("snoozeGroupChat", "{group, minutes? (15 by default; 0 ends it)} -> group: its lines aren't shown until then, and the first "
            + "after it brings the chat back (Firestorm's snooze)", a => gc.Snooze(gc.Group(a), a.Num("minutes")));
        Op("setGroupChat", "{group, on} -> group: receive this group's chat or not (off: its chat is left and stays left; kept on the "
            + "account's block list, so Firestorm honours it too)", a => gc.SetChat(gc.Group(a), a.Bool("on") ?? throw new LeapError("need on: true or false")));
        Op("moderateGroupChat", "{group, to (a member's id or name), text: false|true} -> {text_allowed}: as a moderator, stop or allow "
            + "one member's text in the group's chat for everyone; the person confirms", (a, ct) => J(gc.ModerateAsync(gc.Group(a), a, ct)));

        // experiences
        Op("experiences", "{mine?} -> {allowed, blocked, mine?, admin?}: [{id, name, about, rating, owner}]", (a, ct) => J(xp.ListsAsync(a, ct)));
        Op("experience", "{id} -> {name, about, rating, owner, group, permission}", (a, ct) => J(xp.ProfileAsync(a, ct)));
        Op("findExperiences", "{text, page?} -> {experiences}", (a, ct) => J(xp.SearchAsync(a, ct)));
        Op("setExperience", "{id, permission: allow|block|forget}: allow lets its scripts act without asking wherever it runs (the "
            + "person confirms); forget makes it ask again", (a, ct) => J(xp.SetAsync(a, ct)));
        Op("regionExperiences", "-> {allowed, trusted, blocked}: this region's (its estate managers see them)", (_, ct) => J(xp.RegionAsync(ct)));

        // places
        Op("teleportHistory", "{count=20} -> {places: [{region, pos, parcel, when, go_to, this_session, here?}]}: where the person "
            + "arrived by teleport or at login, newest first (\"take me back\": the second one)", a => places.HistoryJson(a));
        Op("clearTeleportHistory", "-> {cleared}: the person confirms", _ => places.ClearHistory());
        Op("favorites", "-> {favorites: [{item_id, name}]}: the favourites bar (landmarks in Favorites; teleport with TesselAgent teleportLandmark)",
            (_, ct) => J(p.FavoritesAsync(ct)));
        Op("addFavorite", "{item_id?, name?} -> item: here as a favourite (a landmark made in Favorites), or a landmark of mine copied there",
            (a, ct) => J(p.AddFavoriteAsync(a, ct)));
        Op("removeFavorite", "{item_id} -> {removed}: to Trash; the person confirms", (a, ct) => J(p.RemoveFavoriteAsync(a, ct)));
        Op("offerPlace", "{slurl | region (+ pos [x,y,z]), from?} -> {offered}: put a place on the person's feed with a Teleport key for "
            + "them to press (a SLurl from chat or a notecard, somewhere found)", a =>
        {
            var found = a.Str("slurl") is { } url ? Places.Parse(url).FirstOrDefault()
                : a.Str("region") is { Length: > 0 } r ? new Places.Place(r, a.Vec3("pos") ?? new LibreMetaverse.Vector3(128, 128, 0)) : null;
            if (found is null) throw new LeapError("need a slurl (secondlife:///app/teleport/Region/x/y/z, maps.secondlife.com/...) or a region");
            places.Offer(found, "assistant", a.Str("from") ?? "");
            return new JsonObject { ["offered"] = found.GoTo };
        });
        Op("parseSlurls", "{text} -> {places: [{region, go_to}]}: the places a text links to", a => new JsonObject
        {
            ["places"] = new JsonArray(Places.Parse(a.Str("text")).Select(x => (JsonNode)new JsonObject { ["region"] = x.Region, ["go_to"] = x.GoTo }).ToArray()),
        });

        // money
        Op("payPrices", "{id (an object)} -> {any_amount, suggested?, buttons}: the amounts its pay window offers (llSetPayPrice); with "
            + "any_amount false it takes only the buttons' amounts", (a, ct) => J(p.PayPricesJsonAsync(a, ct)));
        Op("money", "{count=20} -> {balance, total_received, total_spent, received, spent, payers: [{name, total, times}]}: this session's "
            + "payments (Firestorm's money tracker)", a => s.Money.Tracker(Math.Clamp(a.Int("count", 20), 1, 200)));
    }

    static async Task<JsonNode?> J(Task<JsonObject> t) => await t;
}
