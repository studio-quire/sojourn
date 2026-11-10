using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// The estate by her own keys, for its owner and managers (docs/access-app.md > About Land > The estate): About Land's
/// Estate tab (EstateRows: its lists, sending people home, ejecting someone from the estate, a restart with its
/// countdown, a message to the region or the estate), the same as typed commands in About Land's box, and Eject from
/// estate and Ban from estate on a person's menu where she manages the estate. Everything here acts on others, so each
/// waits for her confirmation in Notices, as About Land's eject and ban do. Over SojournLand's estate ops (EstateOps),
/// which the region refuses to anyone else; whether she manages it is the region's word at its handshake. Claude Opus
/// 5.5, 2026-11-09.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>A restart she asked for, while it counts down: the region and when it goes.</summary>
    (string Region, DateTime At)? _restart;

    bool ManagesEstate => Host?.Session.Estate.CanManage == true;

    string RegionHere => _ops.Client?.Network.CurrentSim?.Name ?? "this region";

    string NotManager(string doing) => $"Only this estate's owner and its managers can {doing}, and you're neither in {RegionHere}.";

    bool InRegion(UUID id) => Host?.Session.Region?.AvatarById(id) != null;

    // --- the Estate tab ------------------------------------------------------------------------------------------------

    /// <summary>What the Estate tab reads: who owns the estate (its covenant's word, for anyone) and, for its owner and
    /// managers, its lists and switches.</summary>
    async Task LoadEstateAsync(LandLook l, bool again)
    {
        l.ManagesEstate = ManagesEstate;
        if (again || l.Covenant == null) { var c = await _ops.ReadAsync("SojournLand", "covenant"); if (PersonOps.Error(c) == null) l.Covenant = c; }
        if (!l.ManagesEstate) { l.Estate = null; l.EstateWhy = null; return; }
        if (!again && (l.Estate != null || l.EstateWhy != null)) return;
        var r = await _ops.ReadAsync("SojournLand", "estate");
        if (PersonOps.Error(r) is { } e) { l.Estate = null; l.EstateWhy = Theirs(e); } else { l.Estate = r; l.EstateWhy = null; }
    }

    /// <summary>The Estate tab shown again, with the lists an op just read back, or read afresh.</summary>
    async Task EstateShownAsync(JsonObject? lists = null)
    {
        if (_land is not { } l || l.Region != RegionHere) return;
        if (lists?["banned"] != null || lists?["managers"] != null) { l.Estate = lists; l.EstateWhy = null; }
        else if (LandTabName == "Estate") await LoadEstateAsync(l, again: true);
        if (LandTabName == "Estate") ShowLandRows();
    }

    /// <summary>The estate tab said: who owns it, whether she manages it, its switches and how many are on each list
    /// (its tools and the names are in the list, a Tab away).</summary>
    string EstateSaid(LandLook l)
    {
        var rows = EstateRows.For(l).Where(r => r.Tag is BuildRow or EstateListHead).Select(r => r.Tag is EstateListHead ? r.Words[..r.Words.IndexOf('.')] : r.Words);
        return $"{string.Join("; ", rows)}.{(l.ManagesEstate ? " Tab for the list: its tools, and who's on its lists." : "")}";
    }

    /// <summary>Enter on one of the tab's tools.</summary>
    async Task EstateActAsync(EstateAction a)
    {
        switch (a.Key)
        {
            case "home_one" or "kick":
                var who = Dialogs.Pick(this, a.Key == "kick" ? "Eject from the estate" : "Send home", await PeopleHereToPickAsync(), FindSomeoneAsync);
                if (who == null) { Reader?.Answer("Nobody picked.", keep: false); return; }
                if (a.Key == "kick") await EstateEjectAsync(who.Id, who.Name); else await SendHomeAsync(who.Id, who.Name);
                return;
            case "home_all": await SendEveryoneHomeAsync(); return;
            case "restart": await RestartAsync(120); return;
            case "cancel_restart": await CancelRestartAsync(); return;
            case "message_region" or "message_estate": await EstateMessageAsync(a.Key == "message_estate", null); return;
        }
    }

    /// <summary>People in this region for a picker (the estate's tools reach people here); anyone else by typing.</summary>
    async Task<List<Dialogs.Someone>> PeopleHereToPickAsync() =>
        (await _ops.PeopleAsync(512)).Where(p => InRegion(p.Id)).Select(p => new Dialogs.Someone(p.Id, p.Name, _ops.Where(p.Pos) ?? "here")).ToList();

    /// <summary>Enter on a list's heading: someone (or one of her groups) onto it.</summary>
    async Task EstateAddAsync(EstateListHead h)
    {
        if (!ManagesEstate) { Answer(NotManager("change its lists")); return; }
        if (h.List == "managers" && _land is { } l && !EstateRows.OwnsEstate(l)) { Answer("Only the estate's owner adds or removes its managers."); return; }
        if (h.List == "allowed_groups")
        {
            var groups = Host?.Session.State.Groups.Values.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList() ?? new();
            if (groups.Count == 0) { Answer("You're in no groups: a group is allowed into the estate from among yours."); return; }
            if (Dialogs.Choose(this, "Allow a group into the estate", "Groups", groups.Select(g => g.Name).ToList(), 0) is not { } pick) { Reader?.Answer("Nothing changed.", keep: false); return; }
            await EstateListAddAsync("allowed_groups", groups[pick].ID, groups[pick].Name);
            return;
        }
        var title = h.List switch { "banned" => "Ban from the estate", "managers" => "A manager of the estate", _ => "Allow into the estate" };
        var who = Dialogs.Pick(this, title, await PeopleToPickAsync(), FindSomeoneAsync);
        if (who == null) { Reader?.Answer("Nobody picked.", keep: false); return; }
        await EstateListAddAsync(h.List, who.Id, who.Name);
    }

    /// <summary>The estate's lists as they are now (the tab's, else read), or null (said).</summary>
    async Task<JsonObject?> EstateListsAsync()
    {
        if (_land is { Estate: { } shown } l && l.Region == RegionHere) return shown;
        var r = await _ops.ReadAsync("SojournLand", "estate");
        if (PersonOps.Error(r) is { } e) { Answer($"The estate's lists didn't come: {Theirs(e)}."); return null; }
        return r;
    }

    static bool OnList(JsonObject lists, string list, UUID id) => (lists[list] as JsonArray ?? new()).OfType<JsonObject>().Any(o => o.Str("id") == id.ToString());

    /// <summary>Someone (or a group) onto one of the estate's lists, through her confirmation. A ban with kick also sends
    /// them home if they're here (the person menu's Ban from estate).</summary>
    async Task EstateListAddAsync(string list, UUID id, string name, bool kick = false)
    {
        if (!ManagesEstate) { Answer(NotManager(list == "banned" ? "ban people from it" : "change its lists")); return; }
        if (id == _ops.Client?.Self.AgentID && list == "banned") { Answer("That's you."); return; }
        if (await EstateListsAsync() is not { } lists) return;
        if (list == "banned" && (OnList(lists, "managers", id) || lists.Str("owner_id") == id.ToString()))
        {
            Answer($"{name} {(lists.Str("owner_id") == id.ToString() ? "owns" : "manages")} this estate: its owner and managers can't be banned from it.");
            return;
        }
        if (OnList(lists, list, id)) { Answer($"{name} is on the estate's {EstateRows.ListWords(list)} already."); return; }
        var args = new JsonObject { ["list"] = list, [list == "allowed_groups" ? "group" : "to"] = id.ToString() };
        if (kick) args["kick"] = true;
        var r = await ConfirmedAsync("SojournLand", "estateAdd", args);
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not changed: {Theirs(e)}."); return; }
        var sent = (r["kicked"] as JsonArray)?.Count > 0 ? " and sent them home"
            : (r["still_here"] as JsonArray)?.Count > 0 ? ", and asked it to send them home; the region still shows them here for now" : "";
        Answer(list switch
        {
            "banned" => $"Banned {name} from the estate{sent}.",
            "managers" => $"{name} manages the estate now.",
            "allowed_groups" => $"The group {name} may enter the estate now.",
            _ => $"{name} is on the estate's allowed residents now.",
        });
        await EstateShownAsync(r);
    }

    /// <summary>Delete on someone listed: off that list, through her confirmation.</summary>
    async Task EstateRemoveAsync(EstatePerson p)
    {
        if (!ManagesEstate) { Answer(NotManager("change its lists")); return; }
        if (p.List == "managers" && _land is { } l && !EstateRows.OwnsEstate(l)) { Answer("Only the estate's owner adds or removes its managers."); return; }
        var r = await ConfirmedAsync("SojournLand", "estateRemove", new JsonObject { ["list"] = p.List, [p.List == "allowed_groups" ? "group" : "to"] = p.Id.ToString() });
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not changed: {Theirs(e)}."); return; }
        Answer(p.List switch
        {
            "banned" => $"{p.Name} isn't banned from the estate now.",
            "managers" => $"{p.Name} doesn't manage the estate now.",
            "allowed_groups" => $"The group {p.Name} is off the estate's allowed groups.",
            _ => $"{p.Name} is off the estate's allowed residents.",
        });
        await EstateShownAsync(r);
    }

    // --- a person's menu, and the tools ------------------------------------------------------------------------------

    /// <summary>Eject from estate (the estate's Kick): sent home, out of every region of the estate.</summary>
    async Task EstateEjectAsync(UUID id, string name)
    {
        if (!ManagesEstate) { Answer(NotManager("eject people from it")); return; }
        if (!InRegion(id)) { Answer($"{name} isn't in {RegionHere}."); return; }
        var r = await ConfirmedAsync("SojournLand", "kick", new JsonObject { ["to"] = id.ToString() });
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not ejected: {Theirs(e)}."); return; }
        // (the region's list can keep someone a while after: OpenSim's kick logs them out, and drops them only when their
        // connection times out)
        Answer((r["still_here"] as JsonArray)?.Count > 0 ? $"Asked the estate to eject {name}; the region still shows them here for now."
            : $"Ejected {name} from the estate: they've been sent home.");
    }

    /// <summary>Ban from estate: onto its banned residents, and sent home if they're here.</summary>
    Task EstateBanAsync(UUID id, string name) => EstateListAddAsync("banned", id, name, kick: true);

    async Task SendHomeAsync(UUID id, string name)
    {
        if (!ManagesEstate) { Answer(NotManager("send people home")); return; }
        if (!InRegion(id)) { Answer($"{name} isn't in {RegionHere}."); return; }
        var r = await ConfirmedAsync("SojournLand", "sendHome", new JsonObject { ["to"] = id.ToString() });
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not sent home: {Theirs(e)}."); return; }
        Answer((r["still_here"] as JsonArray)?.Count > 0 ? $"Asked to send {name} home; the region still shows them here for now." : $"Sent {name} home.");
    }

    async Task SendEveryoneHomeAsync()
    {
        if (!ManagesEstate) { Answer(NotManager("send people home")); return; }
        var me = _ops.Client?.Self.AgentID;
        if (Host?.Session.Region?.Avatars.Values.Any(a => a.ID != me) != true) { Answer($"Nobody but you is in {RegionHere}."); return; }
        var r = await ConfirmedAsync("SojournLand", "sendHome", new JsonObject { ["everyone"] = true });
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not sent home: {Theirs(e)}."); return; }
        var sent = (r["sent_home"] as JsonArray ?? new()).Select(n => n?.ToString() ?? "").Where(n => n.Length > 0).ToList();
        var still = (r["still_here"] as JsonArray ?? new()).Select(n => n?.ToString() ?? "").Where(n => n.Length > 0).ToList();
        Answer((sent.Count == 0 ? "Asked to send everyone home." : $"Sent {Listed(sent)} home.") + (still.Count > 0 ? $" Still here: {Listed(still)}." : ""));
    }

    static string Listed(List<string> names) => names.Count <= 1 ? string.Join("", names) : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    static string SecondsWords(int s) => s % 60 == 0 ? (s == 60 ? "1 minute" : $"{s / 60} minutes") : $"{s} seconds";

    /// <summary>The region restarted in so many seconds (30 to 240; the viewer's is 2 minutes), through her confirmation;
    /// the region warns everyone in it and counts down, and the tab's row counts too, Enter there cancelling it.</summary>
    async Task RestartAsync(int seconds)
    {
        if (!ManagesEstate) { Answer(NotManager("restart its regions")); return; }
        if (seconds is < 30 or > 240) { Answer("A restart is 30 seconds to 4 minutes away: restart in 2 minutes, say."); return; }
        var region = RegionHere;
        var r = await ConfirmedAsync("SojournLand", "restart", new JsonObject { ["delay"] = seconds });
        if (r == null) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not restarting: {Theirs(e)}."); return; }
        _restart = (region, DateTime.UtcNow.AddSeconds(r.Int("restart_in_seconds", seconds)));
        Answer($"{region} restarts in {SecondsWords(seconds)}: everyone in it is warned, and whoever is still there then is logged out, you too. cancel restart stops it, or Enter on the Estate tab's Restart row.");
        await EstateShownAsync();
    }

    /// <summary>A restart counting down stopped (not asked first: it only undoes one). Anyone's restart, not only hers.</summary>
    async Task CancelRestartAsync()
    {
        if (!ManagesEstate) { Answer(NotManager("restart its regions or stop a restart")); return; }
        var r = await _ops.DoAsync("SojournLand", "restart", new JsonObject { ["cancel"] = true });
        if (PersonOps.Error(r) is { } e) { Answer($"Not cancelled: {Theirs(e)}."); return; }
        _restart = null;
        Answer($"Asked {RegionHere} not to restart.");
        await EstateShownAsync();
    }

    /// <summary>A message to everyone in the region (or every region of the estate), its exact words in her confirmation.</summary>
    async Task EstateMessageAsync(bool estate, string? text)
    {
        if (!ManagesEstate) { Answer(NotManager(estate ? "message everyone in it" : "message everyone in the region")); return; }
        var to = estate ? "everyone in the estate" : $"everyone in {RegionHere}";
        text ??= Dialogs.Ask(this, estate ? "Message to the estate" : "Message to the region", $"To {to}");
        if (text == null) { Reader?.Answer("Nothing sent.", keep: false); return; }
        text = text.Trim();
        if (text.Length == 0) { Answer($"Message what? estate message WORDS, or message WORDS for the region."); return; }
        int bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        if (bytes > EstateOps.MessageBytes)
        {
            Answer($"That's too long: a message to {to} is at most {EstateOps.MessageBytes} bytes of text, about as many letters in English, and this one is {bytes}.");
            return;
        }
        var r = await ConfirmedAsync("SojournLand", "message", new JsonObject { ["text"] = text, ["estate"] = estate });
        if (r == null) return;
        Answer(PersonOps.Error(r) is { } e ? $"Not sent: {Theirs(e)}." : $"Sent to {to}: “{text}”");
    }

    // --- typed, in About Land's command box ---------------------------------------------------------------------------

    const string EstateHelp = "The estate, for its owner and managers: estate reads its tab. estate ban NAME, estate unban NAME, estate allow NAME, "
        + "estate unallow NAME, estate allow group NAME, estate unallow group NAME, estate manager NAME, estate remove manager NAME. "
        + "kick NAME: eject them from the estate. send home NAME, send everyone home. restart, or restart in 60 seconds (30 seconds to 4 minutes); "
        + "cancel restart. message WORDS: to everyone in the region; estate message WORDS: to everyone in the estate.";

    /// <summary>One of the estate's commands typed in About Land (true), or not one (false).</summary>
    async Task<bool> EstateWordsAsync(string verb, string rest)
    {
        var lower = rest.ToLowerInvariant().Trim();
        switch (verb)
        {
            case "estate" when lower is "help" or "?":
                Answer(EstateHelp);
                return true;
            case "estate":
                await EstateListWordsAsync(rest.Trim());
                return true;
            case "kick":
                await EjectFromEstateWordsAsync(rest.Trim());
                return true;
            case "eject" when Regex.IsMatch(lower, @"^from (the )?estate\b"):
                await EjectFromEstateWordsAsync(Regex.Replace(rest.Trim(), @"^from (the )?estate\s*", "", RegexOptions.IgnoreCase));
                return true;
            case "send" when Regex.IsMatch(lower, @"^(everyone|everybody|all) home$|^home (everyone|everybody|all)$"):
                await SendEveryoneHomeAsync();
                return true;
            case "send" when lower.StartsWith("home ") || lower == "home":
            {
                var name = rest.Trim()[4..].Trim();
                if (name.Length == 0) { Answer("send home NAME: someone in this region, sent to their home; send everyone home: all but you."); return true; }
                if (!ManagesEstate) { Answer(NotManager("send people home")); return true; }
                if (await _ops.FindPersonAsync(name) is not { } p) { Answer($"No one called {name} is in this region."); return true; }
                await SendHomeAsync(p.Id, p.Name);
                return true;
            }
            case "restart":
                await RestartWordsAsync(lower);
                return true;
            case "cancel" when lower is "restart" or "the restart":
                await CancelRestartAsync();
                return true;
            case "message":
                await EstateMessageAsync(false, rest.Trim().Length > 0 ? rest.Trim() : null);
                return true;
        }
        return false;
    }

    async Task RestartWordsAsync(string words)
    {
        if (words is "cancel" or "stop" or "off") { await CancelRestartAsync(); return; }
        if (words.Length == 0) { await RestartAsync(120); return; }
        var m = Regex.Match(words, @"^(?:in\s+)?(?<n>\d+(?:\.\d+)?)\s*(?<u>seconds?|secs?|s|minutes?|mins?|m)?$");
        if (!m.Success) { Answer("restart, or restart in 60 seconds (30 seconds to 4 minutes); cancel restart stops one."); return; }
        double n = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        await RestartAsync((int)Math.Round(m.Groups["u"].Value.StartsWith('m') ? n * 60 : n));
    }

    async Task EjectFromEstateWordsAsync(string name)
    {
        if (name.Length == 0) { Answer("kick NAME: someone in this region, ejected from the estate and sent home."); return; }
        if (!ManagesEstate) { Answer(NotManager("eject people from it")); return; }
        if (await _ops.FindPersonAsync(name) is not { } p) { Answer($"No one called {name} is in this region."); return; }
        await EstateEjectAsync(p.Id, p.Name);
    }

    /// <summary>estate ban / unban / allow / unallow NAME, allow / unallow group NAME, manager NAME, remove manager NAME,
    /// message WORDS.</summary>
    async Task EstateListWordsAsync(string words)
    {
        var m = Regex.Match(words, @"^(?<verb>ban|unban|allow|unallow|manager|add manager|remove manager|unmanage|message)(?<group>\s+group)?\s+(?<who>.+)$", RegexOptions.IgnoreCase);
        if (!m.Success) { Answer(EstateHelp); return; }
        var verb = m.Groups["verb"].Value.ToLowerInvariant();
        var who = m.Groups["who"].Value.Trim();
        if (verb == "message") { await EstateMessageAsync(true, who); return; }
        if (!ManagesEstate) { Answer(NotManager("change its lists")); return; }
        bool group = m.Groups["group"].Success;
        if (group && verb is not ("allow" or "unallow")) { Answer("Groups go on the estate's allowed groups: estate allow group NAME, estate unallow group NAME."); return; }
        var list = group ? "allowed_groups" : verb switch { "ban" or "unban" => "banned", "allow" or "unallow" => "allowed", _ => "managers" };
        bool add = verb is "ban" or "allow" or "manager" or "add manager";
        if (!add)
        {
            // taken off: the list's own names first (they needn't be anywhere near)
            if (await EstateListsAsync() is not { } lists) return;
            var listed = (lists[list] as JsonArray ?? new()).OfType<JsonObject>().Where(o => (o.Str("name") ?? "").Contains(who, StringComparison.OrdinalIgnoreCase)).ToList();
            if (listed.Count == 0) { Answer($"No one called {who} is on the estate's {EstateRows.ListWords(list)}."); return; }
            if (listed.Count > 1) { Answer($"Several on the estate's {EstateRows.ListWords(list)} are called something like {who}: {string.Join(", ", listed.Take(6).Select(o => o.Str("name")))}. Give the whole name."); return; }
            if (UUID.TryParse(listed[0].Str("id"), out var lid)) await EstateRemoveAsync(new EstatePerson(lid, listed[0].Str("name") ?? who, list));
            return;
        }
        if (group)
        {
            var mine = Host?.Session.State.Groups.Values.ToList() ?? new();
            var found = mine.Where(x => x.Name.Equals(who, StringComparison.OrdinalIgnoreCase)).Concat(mine.Where(x => x.Name.Contains(who, StringComparison.OrdinalIgnoreCase))).Take(1).ToList();
            if (found.Count == 0) { Answer($"You're not in a group called {who}: a group is allowed into the estate from among yours."); return; }
            await EstateListAddAsync(list, found[0].ID, found[0].Name);
            return;
        }
        if (list == "managers" && _land is { } l && l.Estate != null && !EstateRows.OwnsEstate(l)) { Answer("Only the estate's owner adds or removes its managers."); return; }
        if (await FindAnyoneAsync(who) is not { } p) return;
        await EstateListAddAsync(list, p.Id, p.Name);
    }
}
