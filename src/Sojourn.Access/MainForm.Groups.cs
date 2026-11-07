using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Access;

namespace Sojourn.Access;

/// <summary>
/// What's on a group's menu past its chat and tag (docs/access-app.md > Groups): which title she wears there, half of the
/// "group tag" task blind residents hand to a sighted person; who's in it; its notices, read from the archive; whether she
/// gets them. Inviting someone is on a person's menu. Claude Opus 5.5, 2026-11-07.
/// </summary>
public sealed partial class MainForm
{
    static string TitleWords(JsonObject t) => t.Str("title") is { Length: > 0 } w ? w : "no title";

    /// <summary>The titles she can wear there, the one worn saying so; Enter wears one.</summary>
    async Task ChooseTitleAsync(MyGroup g)
    {
        var r = await _ops.ReadAsync("TesselGroups", "titles", new JsonObject { ["group_id"] = g.Id.ToString() });
        if (PersonOps.Error(r) is { } e) { Answer($"Couldn't read your titles in {g.Name}: {e}"); return; }
        var titles = (r["titles"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        if (titles.Count == 0) { Answer($"{g.Name} gave no titles for you."); return; }
        if (titles.Count == 1) { Answer($"You have one title in {g.Name}: {TitleWords(titles[0])}."); return; }
        int worn = titles.FindIndex(t => t["selected"]?.GetValue<bool>() == true);
        var words = titles.Select((t, i) => i == worn ? $"{TitleWords(t)}, worn" : TitleWords(t)).ToList();
        if (Dialogs.Choose(this, $"Your title in {g.Name}", "Titles", words, Math.Max(worn, 0)) is not { } pick) { Reader?.Answer("Title left as it was.", keep: false); return; }
        if (pick == worn) { Answer($"You're wearing {TitleWords(titles[pick])} already."); return; }
        var set = await _ops.DoAsync("TesselGroups", "title", new JsonObject { ["group_id"] = g.Id.ToString(), ["role_id"] = titles[pick].Str("role_id") });
        if (PersonOps.Error(set) is { } se) { Answer($"Title not changed: {se}"); return; }
        var showing = _ops.Client?.Self.ActiveGroup == g.Id ? "" : " It shows once you wear this group's tag.";
        Answer($"Your title in {g.Name} is {TitleWords(titles[pick])} now.{showing}");
    }

    /// <summary>The title worn in a group, or null when that can't be told.</summary>
    async Task<string?> WornTitleAsync(UUID group)
    {
        var r = await _ops.ReadAsync("TesselGroups", "titles", new JsonObject { ["group_id"] = group.ToString() });
        return (r["titles"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(t => t["selected"]?.GetValue<bool>() == true) is { } worn ? TitleWords(worn) : null;
    }

    /// <summary>Who's in it, in a window to read line by line: owners first, each with their title and whether they're on.</summary>
    async Task GroupMembersAsync(MyGroup g)
    {
        Reader?.Answer($"Asking for {g.Name}'s members.", keep: false);
        var r = await _ops.ReadAsync("TesselGroups", "members", new JsonObject { ["group_id"] = g.Id.ToString() });
        if (PersonOps.Error(r) is { } e) { Answer($"Couldn't read {g.Name}'s members: {e}"); return; }
        var lines = (r["members"] as JsonArray)?.OfType<JsonObject>().Select(m =>
        {
            var words = new List<string> { m.Str("name") ?? "someone" };
            if (m["owner"]?.GetValue<bool>() == true) words.Add("an owner");
            if (m.Str("title") is { Length: > 0 } title) words.Add(title);
            if (m.Str("online") is { Length: > 0 } on) words.Add(on);
            return string.Join(", ", words);
        }).ToList() ?? new();
        ShowText($"{g.Name}: {(lines.Count == 1 ? "1 member" : $"{lines.Count} members")}", string.Join("\n", lines));
    }

    /// <summary>The notices its archive holds, newest first; Enter reads one in a window of its own, Escape there comes back
    /// to the list. What's attached to one is said, not offered.</summary>
    async Task GroupNoticesAsync(MyGroup g)
    {
        var r = await _ops.ReadAsync("TesselGroups", "notices", new JsonObject { ["group_id"] = g.Id.ToString() });
        if (PersonOps.Error(r) is { } e) { Answer($"Couldn't read {g.Name}'s notices: {e}"); return; }
        var notices = (r["notices"] as JsonArray)?.OfType<JsonObject>().ToList() ?? new();
        if (notices.Count == 0) { Answer($"{g.Name} has no notices in its archive."); return; }
        static string Date(JsonObject n) => Json.AsDouble(n["time"]) is { } t and > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)t).LocalDateTime.ToString("d MMMM", CultureInfo.InvariantCulture) : "";
        var words = notices.Select(n => string.Join(", ", new[] { n.Str("subject") ?? "no subject", n.Str("from") is { Length: > 0 } who ? $"from {who}" : "", Date(n),
            n.Str("attachment") is { Length: > 0 } a ? $"with a {a} attached" : "" }.Where(w => w.Length > 0))).ToList();
        int at = 0;
        while (Dialogs.Choose(this, $"{g.Name}'s notices", "Notices", words, at) is { } pick)
        {
            at = pick;
            var read = await _ops.ReadAsync("TesselGroups", "noticeRead", new JsonObject { ["notice_id"] = notices[pick].Str("notice_id") });
            if (PersonOps.Error(read) is { } re) { Answer($"Couldn't read that notice: {re}"); return; }
            ShowText(read.Str("subject") is { Length: > 0 } subject ? subject : "A notice", $"{words[pick]}\n\n{read.Str("message")}");
        }
    }

    /// <summary>Whether she gets its notices, whether her profile lists it: said back as they now are.</summary>
    async Task GroupSwitchAsync(MyGroup g, string which)
    {
        if (Host?.Session.State.Groups.TryGetValue(g.Id, out var now) != true) return;
        bool on = !(which == "notices" ? now.AcceptNotices : now.ListInProfile);
        var r = await _ops.DoAsync("TesselGroups", "membership", new JsonObject { ["group_id"] = g.Id.ToString(), [which] = on });
        Answer(PersonOps.Error(r) is { } e ? $"Not changed: {e}"
            : which == "notices" ? (on ? $"You'll get {g.Name}'s notices." : $"You won't get {g.Name}'s notices now; they stay in its archive.")
            : on ? $"Your profile lists {g.Name}." : $"Your profile doesn't list {g.Name} now.");
    }

    /// <summary>Someone invited: the groups she may invite to, as the viewer's picker lists them; they're asked in their own
    /// viewer.</summary>
    async Task InviteAsync(UUID id, string name)
    {
        var groups = Host?.Session.State.Groups.Values.Where(g => (g.Powers & GroupPowers.Invite) != 0).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (groups is not { Count: > 0 }) { Answer("None of your groups lets you invite people."); return; }
        await Task.Yield();
        if (Dialogs.Choose(this, $"Invite {name} to", "Groups", groups.Select(g => g.Name).ToList(), 0) is not { } pick) { Reader?.Answer("Nobody invited.", keep: false); return; }
        var r = await _ops.DoAsync("TesselGroups", "invite", new JsonObject { ["group_id"] = groups[pick].ID.ToString(), ["to"] = id.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"Not invited: {e}" : $"Invited {name} to {groups[pick].Name}.");
    }
}
