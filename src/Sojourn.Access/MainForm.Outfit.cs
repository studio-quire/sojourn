using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// What am I wearing, from the worn list (docs/access.md > M2; no model needed: Control+Shift+D's description is the
/// picture side, with the assistant): Control+Shift+W or `//outfit` says it by name, grouped: clothing with each
/// layer's kind, attachments and where they're worn, HUDs, then the body parts. Names as the inventory has them (what
/// the person named or bought), sorted, so a "**Wearing: ..." item, a blind tester's way of labelling an outfit, comes first.
/// The outfit's attachments not on the avatar yet are said as still arriving for a while after logging in or reaching
/// another region (on Second Life they come in over a minute or so, for everyone: the blind tester's skirt "looked
/// missing, then re-attached itself", 2026-10-31), and as in the outfit but not attached after that.
/// </summary>
public sealed partial class MainForm
{
    long _outfitSettleUntil;
    bool _arrivingShown;

    /// <summary>Arrived somewhere (logged in: two minutes; another region: one): the outfit's attachments come in now.</summary>
    void OutfitMoving(bool login) =>
        _outfitSettleUntil = Math.Max(_outfitSettleUntil, Environment.TickCount64 + (login ? 120_000 : 60_000));

    bool OutfitArriving => Environment.TickCount64 < _outfitSettleUntil;

    /// <summary>An attachment in the outfit (its link in Current Outfit) that isn't on the avatar, in the item's words.</summary>
    string NotAttachedWords => OutfitArriving ? "worn, still arriving" : "in your outfit but not attached";

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
        var outfitObjects = new Dictionary<UUID, string>();
        var cof = c.Inventory.FindFolderForType(FolderType.CurrentOutfit);
        int links = 0;
        if (store != null && cof != UUID.Zero)
            foreach (var link in store.GetContents(cof).OfType<InventoryItem>())
            {
                links++;
                var it = Target(link);
                if (it.AssetType == AssetType.Object) { outfitObjects[it.UUID] = it.Name; continue; }
                if (it.AssetType is not (AssetType.Clothing or AssetType.Bodypart)) continue;
                var kind = ((WearableType)(it.Flags & 0xFF)).ToString().ToLowerInvariant();
                (it.AssetType == AssetType.Bodypart ? body : clothing).Add(it.Name.Equals(kind, StringComparison.OrdinalIgnoreCase) ? it.Name : $"{it.Name}, {kind}");
            }
        foreach (var p in w.Prims.Values.Where(p => p.ParentID == me.LocalID))
        {
            var item = AgentInventory.AttachItemId(p);
            outfitObjects.Remove(item);
            var name = item != UUID.Zero && store?.TryGetValue<InventoryItem>(item, out var ii) == true ? ii.Name : p.Properties?.Name;
            if (string.IsNullOrEmpty(name)) name = "something unnamed";
            int point = (int)p.PrimData.AttachmentPoint;
            (point is >= 31 and <= 38 ? huds : attached).Add((name, ObjectViews.AttachPointName(point)));
        }
        static string Sorted(IEnumerable<string> names) => string.Join("; ", names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        static string At(IEnumerable<(string Name, string Point)> list) =>
            Sorted(list.Select(a => a.Point.StartsWith("HUD ") ? $"{a.Name}, {a.Point[4..].ToLowerInvariant()}" : $"{a.Name} on {a.Point}"));
        int total = clothing.Count + attached.Count + huds.Count;
        int missing = outfitObjects.Count;
        var lines = new List<string>
        {
            total == 0 && missing > 0 && OutfitArriving ? $"Nothing attached yet; {missing} {(missing == 1 ? "thing" : "things")} still arriving."
            : total == 0 ? "Wearing no clothing or attachments."
            : $"Wearing {total} {(total == 1 ? "thing" : "things")}{(missing > 0 && OutfitArriving ? $", {missing} more still arriving" : "")}.",
        };
        // just logged in, the inventory (and so the outfit's own list) may not be in yet: only what's attached is known
        if (links == 0 && OutfitArriving && Host.Session.Inventory.Loading is { IsCompleted: false })
            lines.Add("Your outfit is still loading, so this may not be all of it: ask again in a moment.");
        if (clothing.Count > 0) lines.Add($"Clothing: {Sorted(clothing)}.");
        if (attached.Count > 0) lines.Add($"Attached: {At(attached)}.");
        if (huds.Count > 0) lines.Add($"HUDs: {At(huds)}.");
        if (missing > 0)
            lines.Add(OutfitArriving
                ? $"Still arriving, as attachments do for a minute or so after logging in or teleporting: {Sorted(outfitObjects.Values)}."
                : $"In your outfit but not attached: {Sorted(outfitObjects.Values)}.");
        if (body.Count > 0) lines.Add($"Body: {Sorted(body)}.");
        Answer(string.Join(" ", lines));
        return Task.CompletedTask;
    }
}
