using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// A group's settings and roles as rows (docs/access-app.md > Groups > Settings and roles), in the official viewer's
/// words where they're plain: its General tab's group settings (charter, Anyone can join, Cost to join, Show in search,
/// maturity), and a role's (name, title, description, whether its members are shown, its members) with its abilities
/// in the viewer's sets (GroupAbilities), a heading row each set. Rows are <see cref="BuildRow"/>s, so the list's keys
/// work as About Land's and the build editor's. Pure words over TesselGroups' profile and roles; a change comes back as
/// the op's arguments. Claude Opus 5.5, 2026-11-09.
/// </summary>
public static class GroupRows
{
    public static readonly string[] Maturities = { "general", "moderate" };

    // the viewer's limits (panel_group_general.xml, panel_group_roles.xml)
    public const int CharterChars = 511, RoleNameBytes = 20, RoleTitleBytes = 20, RoleDescChars = 295;

    public static ulong Bits(JsonObject role) =>
        ulong.TryParse(role.Str("power_bits"), out var b) ? b : (ulong)GroupOps.ParsePowers(role["powers"]);

    public static bool IsEveryone(JsonObject role) => role["everyone"]?.GetValue<bool>() == true;

    static string Flat(string s) => string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <param name="now">The value as it's typed back (the box Enter opens), when not as said ("none" is said for an empty title).</param>
    static BuildRow Row(string key, string label, string value, RowKind kind, string? now = null, string ask = "") =>
        new(key, $"{label}: {value}", kind) { Label = label, Now = now ?? value, Ask = ask.Length > 0 ? ask : label };

    /// <summary>The power a General setting takes, as the viewer's General tab enables it.</summary>
    public static GroupPowers PowerOf(string key) => key switch
    {
        "g.open" or "g.fee" => GroupPowers.ChangeOptions,
        "g.charter" or "g.search" or "g.maturity" => GroupPowers.ChangeIdentity,
        _ => GroupPowers.None,
    };

    static BuildRow Switch(string key, string label, bool on) => Row(key, label, on ? "on" : "off", RowKind.Switch) with { Value = on ? 1 : 0 };

    /// <summary>The General tab: the group's own settings, then a line on the roles.</summary>
    public static List<BuildRow> General(JsonObject profile, IReadOnlyList<JsonObject> roles)
    {
        var charter = profile.Str("charter") ?? "";
        int fee = profile.Int("fee", 0);
        bool mature = profile.Bool("mature", false);
        return new()
        {
            Row("g.charter", "Charter", charter.Trim().Length > 0 ? Flat(charter) : "none", RowKind.Text, charter, "Charter"),
            Switch("g.open", "Anyone can join", profile.Bool("open_enrollment", false)),
            Row("g.fee", "Cost to join", fee > 0 ? $"L${fee}" : "none", RowKind.Number, $"{fee}", "Cost to join, in L$ (0: none)") with { Value = fee, Step = 10 },
            Switch("g.search", "Show in search", profile.Bool("show_in_list", true)),
            Row("g.maturity", "Maturity", mature ? "moderate" : "general", RowKind.Choice, ask: "Maturity (general or moderate)") with { Choices = Maturities, Chosen = mature ? 1 : 0 },
            Row("g.roles", "Roles", $"{string.Join(", ", roles.Select(r => r.Str("name")))}. Each is a tab: Tab, then the arrows. New role: Alt+N", RowKind.Info),
        };
    }

    /// <summary>A role's rows. postLinks: whether the region has Post links in group chat (the viewer leaves it out
    /// where it doesn't).</summary>
    public static List<BuildRow> Role(JsonObject role, JsonObject profile, UUID me, bool postLinks)
    {
        bool everyone = IsEveryone(role), owners = role.Str("role_id") == profile.Str("owner_role");
        ulong bits = Bits(role);
        var rows = new List<BuildRow>
        {
            Row("r.name", "Name", role.Str("name") ?? "", RowKind.Text),
            Row("r.title", "Title", role.Str("title") is { Length: > 0 } t ? t : "none", RowKind.Text, role.Str("title") ?? ""),
            Row("r.desc", "Description", role.Str("desc") is { Length: > 0 } d ? Flat(d) : "none", RowKind.Text, role.Str("desc") ?? ""),
        };
        if (!owners) rows.Add(Switch("r.shown", "Members shown outside the group", (bits & GroupAbilities.MembersShown) != 0));
        var members = (role["members"] as JsonArray ?? new()).OfType<JsonObject>()
            .Select(m => m.Str("id") == me.ToString() ? "you" : m.Str("name") ?? "someone").OrderBy(n => n == "you" ? 1 : 0).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        rows.Add(Row("r.members", "Members", everyone ? $"everyone in the group, {profile.Int("members", 0)}"
            : members.Count == 0 ? "nobody yet" : $"{members.Count}: {Joined(members)}", RowKind.Info));
        if (!everyone && !owners) rows.Add(Row("r.delete", "Delete this role", "Enter deletes it, after your confirmation", RowKind.Info));
        if (owners)
        {
            rows.Add(Row("r.all", "Abilities", "every one, always: the Owners role has them all", RowKind.Info));
            return rows;
        }
        var shown = GroupAbilities.All.Where(a => a.Bit != GroupAbilities.PostLinksBit || postLinks).ToList();
        rows.Add(Row("h.all", "Abilities", $"{shown.Count(a => (bits & a.Mask) != 0)} of {shown.Count} on; Page Down goes set by set", RowKind.Info));
        foreach (var set in shown.GroupBy(a => a.Set))
        {
            rows.Add(Row("h." + set.Key, set.Key, $"{set.Count(a => (bits & a.Mask) != 0)} of {set.Count()} on", RowKind.Info));
            rows.AddRange(set.Select(a => Switch($"a.{a.Bit}", a.Words, (bits & a.Mask) != 0)));
        }
        return rows;
    }

    static string Joined(List<string> names) =>
        names.Count <= 1 ? string.Join("", names) : names.Count > 8 ? $"{string.Join(", ", names.Take(8))} and {names.Count - 8} more"
        : $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}";

    /// <summary>A General setting changed: TesselGroups update's arguments (the group's id added by the caller), whether
    /// it waits for her confirmation (who may join: as About Land's who may enter), the power it takes, or why not.</summary>
    public static (JsonObject? Args, bool Confirm, GroupPowers Power, string? Error) GeneralChange(string key, string value)
    {
        var v = value.Trim();
        bool? on = On(v);
        switch (key)
        {
            case "g.charter":
                var text = value.TrimEnd().ReplaceLineEndings("\n");
                if (text.Length > CharterChars) return (null, false, 0, $"A charter is at most {CharterChars} characters; this one is {text.Length}.");
                return (new JsonObject { ["charter"] = text.Trim().ToLowerInvariant() is "none" ? "" : text }, false, GroupPowers.ChangeIdentity, null);
            case "g.open":
                return on is { } o ? (new JsonObject { ["open_enrollment"] = o }, true, GroupPowers.ChangeOptions, null) : (null, false, 0, "Anyone can join: on or off.");
            case "g.fee":
                if (on == false || v.ToLowerInvariant() is "none" or "free") return (new JsonObject { ["fee"] = 0 }, true, GroupPowers.ChangeOptions, null);
                return int.TryParse(v.TrimStart('L', 'l', '$').Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fee) && fee >= 0
                    ? (new JsonObject { ["fee"] = fee }, true, GroupPowers.ChangeOptions, null) : (null, false, 0, "Cost to join: a number of L$, or 0 for none.");
            case "g.search":
                return on is { } s ? (new JsonObject { ["show_in_list"] = s }, false, GroupPowers.ChangeIdentity, null) : (null, false, 0, "Show in search: on or off.");
            case "g.maturity":
                var m = Array.FindIndex(Maturities, x => x.StartsWith(v.ToLowerInvariant(), StringComparison.Ordinal));
                return v.Length > 0 && m >= 0 ? (new JsonObject { ["mature"] = m == 1 }, false, GroupPowers.ChangeIdentity, null) : (null, false, 0, "Maturity: general or moderate.");
        }
        return (null, false, 0, "That's read from the grid.");
    }

    public static bool? On(string v)
    {
        var w = v.Trim().ToLowerInvariant().TrimEnd('.');
        return w is "on" or "yes" or "true" or "1" ? true : w is "off" or "no" or "false" or "0" ? false : null;
    }

    public static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);
}
