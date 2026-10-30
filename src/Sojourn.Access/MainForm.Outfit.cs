using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// What am I wearing, from the worn list (docs/access.md > M2; no model needed: Control+Shift+D's description is the
/// picture side, with the assistant): Control+Shift+W or `//outfit` says it by name, grouped: clothing with each
/// layer's kind, attachments and where they're worn, HUDs, then the body parts. Names as the inventory has them (what
/// the person named or bought), sorted, so a "**Wearing: ..." item, a blind tester's way of labelling an outfit, comes first.
/// </summary>
public sealed partial class MainForm
{
    Task OutfitListAsync()
    {
        if (_ops?.Client is not { } c || Host?.Session.Region is not { } w || w.AvatarById(c.Self.AgentID) is not { } me)
        {
            Answer("Not in a region yet.");
            return Task.CompletedTask;
        }
        var store = Store;
        var clothing = new List<string>();
        var body = new List<string>();
        var attached = new List<(string Name, string Point)>();
        var huds = new List<(string Name, string Point)>();
        var cof = c.Inventory.FindFolderForType(FolderType.CurrentOutfit);
        if (store != null && cof != UUID.Zero)
            foreach (var link in store.GetContents(cof).OfType<InventoryItem>())
            {
                var it = Target(link);
                if (it.AssetType is not (AssetType.Clothing or AssetType.Bodypart)) continue;
                var kind = ((WearableType)(it.Flags & 0xFF)).ToString().ToLowerInvariant();
                (it.AssetType == AssetType.Bodypart ? body : clothing).Add(it.Name.Equals(kind, StringComparison.OrdinalIgnoreCase) ? it.Name : $"{it.Name}, {kind}");
            }
        foreach (var p in w.Prims.Values.Where(p => p.ParentID == me.LocalID))
        {
            var item = AgentInventory.AttachItemId(p);
            var name = item != UUID.Zero && store?.TryGetValue<InventoryItem>(item, out var ii) == true ? ii.Name : p.Properties?.Name;
            if (string.IsNullOrEmpty(name)) name = "something unnamed";
            int point = (int)p.PrimData.AttachmentPoint;
            (point is >= 31 and <= 38 ? huds : attached).Add((name, ObjectViews.AttachPointName(point)));
        }
        static string Sorted(IEnumerable<string> names) => string.Join("; ", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        static string At(IEnumerable<(string Name, string Point)> list) =>
            Sorted(list.Select(a => a.Point.StartsWith("HUD ") ? $"{a.Name}, {a.Point[4..].ToLowerInvariant()}" : $"{a.Name} on {a.Point}"));
        int total = clothing.Count + attached.Count + huds.Count;
        var lines = new List<string> { total == 0 ? "Wearing no clothing or attachments." : $"Wearing {total} {(total == 1 ? "thing" : "things")}." };
        if (clothing.Count > 0) lines.Add($"Clothing: {Sorted(clothing)}.");
        if (attached.Count > 0) lines.Add($"Attached: {At(attached)}.");
        if (huds.Count > 0) lines.Add($"HUDs: {At(huds)}.");
        if (body.Count > 0) lines.Add($"Body: {Sorted(body)}.");
        Answer(string.Join(" ", lines));
        return Task.CompletedTask;
    }
}
