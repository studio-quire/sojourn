using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;

namespace Sojourn.Access;

/// <summary>Something the estate's tools do, as a row of About Land's Estate tab: Enter does it.</summary>
public sealed record EstateAction(string Key, string Label);

/// <summary>An estate list's heading row: Enter adds someone (or a group) to it.</summary>
public sealed record EstateListHead(string List, string Label);

/// <summary>Someone (or a group) on one of the estate's lists: Delete takes them off.</summary>
public sealed record EstatePerson(UUID Id, string Name, string List);

/// <summary>
/// About Land's Estate tab (docs/access-app.md > About Land): the official viewer's World > Region/Estate, its Estate and
/// Access tabs and the Region tab's tools, for the estate's owner and managers. Who owns the estate and whether she
/// manages it, for anyone; then the estate's switches (read here, not changed), its tools as rows Enter does (send
/// someone or everyone home, eject someone from the estate, restart the region, a message to the region or the estate),
/// and its four lists, each a heading Enter adds to, then a row a name, Delete taking them off. Claude Opus 5.5, 2026-11-09.
/// </summary>
public static class EstateRows
{
    /// <summary>The estate's lists as SojournLand estate names them, and their words.</summary>
    public static readonly (string List, string Heading, string One)[] Lists =
    {
        ("managers", "Managers", "Manager"),
        ("allowed", "Allowed residents", "Allowed"),
        ("allowed_groups", "Allowed groups", "Allowed group"),
        ("banned", "Banned residents", "Banned"),
    };

    public static string ListWords(string list) => Lists.FirstOrDefault(x => x.List == list).Heading?.ToLowerInvariant() ?? list;

    static string Count(int n, bool groups) => n == 0 ? (groups ? "none" : "nobody") : n == 1 ? (groups ? "1 group" : "1 person") : $"{n} {(groups ? "groups" : "people")}";

    public static string EstateName(LandLook l) => l.Estate?.Str("estate") ?? l.Covenant?.Str("estate") ?? "this estate";

    public static bool OwnsEstate(LandLook l) => (l.Estate?.Str("owner_id") ?? l.Covenant?.Str("estate_owner_id")) == l.Me.ToString();

    public static IEnumerable<(string Words, string Key, object Tag)> For(LandLook l)
    {
        (string, string, object) Info(string key, string label, string value) =>
            ($"{label}: {value}", key, new BuildRow(key, $"{label}: {value}", RowKind.Info) { Label = label, Now = value });
        (string, string, object) Act(string key, string label, string words) => ($"{label}: {words}", "a." + key, new EstateAction(key, label));

        var owner = l.Estate?.Str("owner") ?? l.Covenant?.Str("estate_owner");
        yield return Info("e.name", "Estate", (l.Estate?.Str("estate") ?? l.Covenant?.Str("estate")) is { } en
            ? $"{en}{(owner != null ? $", owned by {(OwnsEstate(l) ? "you" : owner)}" : "")}" : "not read yet; F5 reads it");
        yield return Info("e.manage", "You manage it", !l.ManagesEstate ? "no: only its owner and managers have the estate's lists and tools"
            : OwnsEstate(l) ? "yes, as its owner" : "yes, as one of its managers");
        if (!l.ManagesEstate) yield break;

        // its tools first, then its switches and lists
        yield return Act("home_one", "Send someone home", "Enter picks them");
        yield return Act("home_all", "Send everyone home", $"Enter sends everyone in {l.Region} but you home, after your confirmation");
        yield return Act("kick", "Eject someone from the estate", "Enter picks them; they're sent home, after your confirmation");
        yield return l.RestartAt is { } at && at > DateTime.UtcNow
            ? Act("cancel_restart", "Restart", $"{l.Region} restarts in about {Math.Max(1, (int)Math.Round((at - DateTime.UtcNow).TotalSeconds))} seconds; Enter cancels it")
            : Act("restart", "Restart the region", $"Enter restarts {l.Region} in 2 minutes, everyone in it warned, after your confirmation");
        yield return Act("message_region", "Message to the region", $"Enter writes one to everyone in {l.Region}");
        yield return Act("message_estate", "Message to the estate", "Enter writes one to everyone in all its regions");

        if (l.Estate is not { } e)
        {
            yield return Info("e.lists", "Its lists", l.EstateWhy ?? "not read yet; F5 reads them");
            yield break;
        }
        static string OnOff(JsonObject e, string key) => e.Bool(key, false) ? "on" : "off";
        yield return Info("e.public", "Open to", e.Bool("public", true) ? "anyone" : "only its allowed residents and groups");
        yield return Info("e.payment", "No one without payment info", OnOff(e, "deny_no_payment_info"));
        yield return Info("e.age", "No one under 18", OnOff(e, "deny_under_18"));
        yield return Info("e.bots", "No scripted agents", OnOff(e, "deny_scripted_agents"));
        yield return Info("e.voice", "Voice", OnOff(e, "voice"));
        yield return Info("e.direct", "Direct teleport, to any spot rather than a telehub", OnOff(e, "direct_teleport"));

        bool owns = OwnsEstate(l);
        foreach (var (list, heading, one) in Lists)
        {
            var people = (e[list] as JsonArray ?? new()).OfType<JsonObject>().ToList();
            var add = list switch
            {
                "managers" when !owns => "only the estate's owner adds or removes them",
                "managers" => "Enter adds one",
                "allowed_groups" => "Enter adds one of your groups",
                "banned" => "Enter bans someone",
                _ => "Enter adds someone",
            };
            yield return ($"{heading}: {Count(people.Count, list == "allowed_groups")}. {char.ToUpperInvariant(add[0])}{add[1..]}", "l." + list, new EstateListHead(list, heading));
            foreach (var p in people)
            {
                if (!UUID.TryParse(p.Str("id"), out var id)) continue;
                var name = p.Str("name") is { Length: > 0 } n && n != id.ToString() ? n : list == "allowed_groups" ? "a group you're not in" : "someone whose name didn't come";
                var since = p.Str("banned") is { Length: > 0 } b ? $", since {b}" : "";
                var by = p.Str("banned_by") is { Length: > 0 } who ? $", by {who}" : "";
                yield return ($"{one}: {name}{since}{by}", $"{list}.{id}", new EstatePerson(id, name, list));
            }
        }
    }
}
