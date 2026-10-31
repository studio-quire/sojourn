using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Access;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// Describing (docs/access.md > M5): Control+D describes what's selected where the person is (a person or object in
/// its list, a friend nearby or else their profile pictures, an inventory item: something worn as it's worn, a picture
/// as it is), or else the place around them; Control+Shift+D describes them and what they're wearing; the same key
/// again for the same subject asks for more. `//describe` the same by words. The assistant does the describing
/// (SojournDescribe gives it pictures aimed at the subject and the names around), so the request names its subject by
/// id and the pictures start drawing as it goes. Without the assistant (no provider that works) it says so plainly, then
/// what the lists know: names, an object's own description and floating text, what someone wears by its items' names.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>What to describe: Key is here, me or an id (what SojournDescribe look takes; for item and profile, the
    /// item's or resident's id); Of is view, item or profile; Words name it for the person.</summary>
    sealed record Subject(string Key, string Of, string Words, string Asked);

    (Subject Subject, string Detail, long At)? _described;

    static Subject Here => new("here", "view", "where you are", "Describe where I am");
    static Subject Me => new("me", "view", "you", "Describe me: how I look and what I'm wearing");

    /// <summary>What Control+D means where the focus is.</summary>
    Subject SelectedSubject()
    {
        if (PeopleFocused?.FocusedItem?.Tag is PersonOps.Person p) return Of(p);
        if (_friends.Focused && _friends.FocusedItem?.Tag is Friend f) return Of(f);
        if (_objects.Focused && _objects.FocusedItem?.Tag is PersonOps.Thing t) return Of(t);
        // a part, or the part a face is on (a face alone is too small to describe apart from its part)
        if (_parts.Focused && _parts.FocusedItem?.Tag is Linkset.Part part) return Of(part);
        if (_faces.Focused && _faces.FocusedItem?.Tag is FaceItem face) return Of(face.Part);
        if (_pane.Tree.Focused && _pane.Tree.SelectedNode is InvNode { Item: InventoryItem item }) return ItemSubject(item);
        return Here;
    }

    static Subject Of(PersonOps.Person p) => new(p.Id.ToString(), "view", p.Name, $"Describe {p.Name}, the person I picked in my People list");
    static Subject Of(PersonOps.Thing t) => new(t.Id.ToString(), "view", t.Name, $"Describe {t.Name}, the object I picked in my Objects list");
    static Subject Of(Linkset.Part p) => new(p.Id.ToString(), "view", PartName(p), $"Describe {PartName(p)}, a part of an object I picked");

    Subject Of(Friend f) => Host?.Session.Region?.AvatarById(f.Id) != null
        ? new(f.Id.ToString(), "view", f.Name, $"Describe {f.Name}, my friend, who is here")
        : new(f.Id.ToString(), "profile", $"{f.Name}'s profile pictures", $"Describe {f.Name}'s profile pictures (they aren't here)");

    /// <summary>An inventory item: an attachment worn is described as it's worn, clothing worn as part of them, anything
    /// else by its picture (a texture or snapshot's own, else its thumbnail).</summary>
    Subject ItemSubject(InventoryItem item)
    {
        var it = Target(item);
        if (Host?.Session.Region is { } w && _ops.Client is { } c && w.AvatarById(c.Self.AgentID) is { } me)
        {
            foreach (var p in w.Prims.Values)
                if (p.ParentID == me.LocalID && AgentInventory.AttachItemId(p) == it.UUID)
                    return new(p.ID.ToString(), "view", $"your {it.Name}", $"Describe my {it.Name}, as I'm wearing it");
            if (it is InventoryWearable && Worn().Ids.Contains(it.UUID))
                return new("me", "view", $"your {it.Name}", $"Describe the {it.Name} I'm wearing (a look at me); mainly that, briefly the rest");
        }
        return new(it.UUID.ToString(), "item", $"the picture of {it.Name}", $"Describe the picture of my inventory item {it.Name}");
    }

    void DescribeKey(bool me) => _ = DescribeAsync(me ? Me : SelectedSubject());

    /// <summary>A description asked for: the same subject again within ten minutes asks for more.</summary>
    async Task DescribeAsync(Subject s, string? detail = null)
    {
        if (Host is not { } h) return;
        detail ??= _described is { } d && d.Subject == s && Environment.TickCount64 - d.At < 600_000 ? "rich" : "brief";
        _described = (s, detail, Environment.TickCount64);
        bool rich = detail == "rich";
        if (!_opt.Assistant || (!h.Assistant.SidecarAlive && h.Sidecar == null)) { await WithoutAssistantAsync(s); return; }
        // the assistant up but off (no provider that works, or the month's allowance used up): why, then the lists
        if (h.Assistant.ProviderStatus is { } ps && ps["state"]?.GetValue<string>() is { } state
            && state is not ("ready" or "offline" or "checking") && ps["words"]?.GetValue<string>() is { Length: > 0 } why)
        {
            await WithoutAssistantAsync(s, why);
            return;
        }
        if (s.Of == "view") h.Describe?.Prepare(s.Key, detail);
        var asked = rich ? $"{s.Asked}, in detail: you've described it briefly already, so add to that." : $"{s.Asked}, briefly.";
        Ask($"{asked} [describe key: subject={s.Key}, of={s.Of}, detail={detail}]",
            shown: rich ? $"More on {s.Words}." : $"Describe {s.Words}.",
            said: rich ? $"More on {s.Words}." : $"Describing {s.Words}.");
    }

    /// <summary>`//describe [here | me | more | person NAME | object NAME | NAME]`.</summary>
    async Task DescribeWordsAsync(string words)
    {
        var w = words.Trim();
        var lw = w.ToLowerInvariant();
        if (lw is "" or "here" or "around") { await DescribeAsync(Here); return; }
        if (lw is "me" or "myself" or "my outfit") { await DescribeAsync(Me); return; }
        if (lw is "more" or "again")
        {
            if (_described is not { } d) { Answer("Nothing described yet: //describe, or Control+D on something."); return; }
            await DescribeAsync(d.Subject, "rich");
            return;
        }
        bool person = lw.StartsWith("person "), thing = lw.StartsWith("object ");
        var name = person || thing ? w[7..].Trim() : w;
        if (!thing && await _ops.FindPersonAsync(name) is { } p)
        {
            await DescribeAsync(new(p.Id.ToString(), "view", p.Name, $"Describe {p.Name}"));
            return;
        }
        if (!person && (await _ops.ObjectsAsync(64, 1, name)).FirstOrDefault() is { } t)
        {
            await DescribeAsync(new(t.Id.ToString(), "view", t.Name, $"Describe {t.Name}"));
            return;
        }
        Answer(person ? $"No one called {name} nearby." : thing ? $"Nothing called {name} nearby." : $"No one and nothing called {name} nearby.");
    }

    bool _toldNoAssistant;

    /// <summary>No assistant: said plainly the first time this session, then what the lists know, which needs no model
    /// (after the first time, only that).</summary>
    /// <param name="why">the assistant's own words for why it's off, when it's up but has no provider that works</param>
    async Task WithoutAssistantAsync(Subject s, string? why = null)
    {
        var none = _toldNoAssistant ? "" : why != null ? $"Describing needs the assistant. {why.TrimEnd()} "
            : AccessOptions.AssistantInBuild ? "Describing needs the assistant, and it isn't running. "
            : "Describing needs the assistant, which this build doesn't include. ";
        _toldNoAssistant = true;
        if (s.Of != "view") { Answer(none.Length > 0 ? none.Trim() : "Describing needs the assistant."); return; }
        var r = await _ops.ReadAsync("SojournDescribe", "look", new JsonObject { ["subject"] = s.Key, ["pictures"] = false });
        if (PersonOps.Error(r) is { } e) { Answer(none + e); return; }
        var facts = r["subject"] as JsonObject ?? new();
        // a parcel's description is its owner's words (the session log leaves them out of a log sent to the studio)
        Answer(none + "What the lists know: " + Facts(facts), facts.Str("parcel_description") is { Length: > 0 } ? LineFrom.People : LineFrom.App);
    }

    static string Facts(JsonObject f)
    {
        var parts = new List<string>();
        void Add(string? s) { if (!string.IsNullOrWhiteSpace(s)) parts.Add(s.Trim().TrimEnd('.') + "."); }
        static string List(JsonNode? n) => n is JsonArray a ? string.Join(", ", a.Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x))) : "";
        if (f.Str("region") is { } region)
        {
            Add(f.Str("parcel") is { Length: > 0 } parcel ? $"{region}, {parcel}" : region);
            Add(f.Str("parcel_description"));
            if (List(f["people_within_30_metres"]) is { Length: > 0 } people) Add($"People: {people}");
            if (List(f["named_objects_within_20_metres"]) is { Length: > 0 } things) Add($"Named things: {things}");
            return string.Join(" ", parts);
        }
        Add(f.Str("name") is { } name ? f.Str("where") is { } where ? $"{name}, {where}" : name : null);
        if (f.Str("worn_by") is { } by) Add($"Worn by {by}, on {f.Str("worn_on")}");
        if (f.Str("sitting_on") is { } seat) Add($"Sitting on {seat}");
        if (f.Str("its_own_description") is { } desc) Add($"Its own description: {desc}");
        if (f.Str("floating_text") is { } text) Add($"Its floating text: {text}");
        if (f.Str("owner") is { } owner) Add($"Owned by {owner}");
        if (List(f["clothing_and_body_parts"]) is { Length: > 0 } layers) Add($"Clothing and body parts: {layers}");
        if (List(f["wearing_items_named"]) is { Length: > 0 } worn) Add($"Wearing items named: {worn}");
        return string.Join(" ", parts);
    }
}
