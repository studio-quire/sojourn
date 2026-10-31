using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// Placing things by words (docs/access.md > M2): an object from the inventory rezzed where the person says ("a metre to
/// my left", "here", "2 m ahead and half a metre up"), and one of theirs in the world moved the same way, or by an
/// amount from where it is ("by 20 cm to the left"). It rests with its bottom on what's under that spot: the surface the
/// renderer out of sight knows there (a table, a floor), else the ground or the floor the person stands on; up and down
/// raise or lower it from there. PlaceWords.cs reads the words.
/// </summary>
public sealed partial class MainForm
{
    const string PlaceExample = "For example: a metre to my left, 2 metres ahead, here, by 20 cm up";

    /// <summary>An inventory object rezzed where the person says.</summary>
    async Task RezAtAsync(InventoryItem item)
    {
        var words = Dialogs.Ask(this, $"Rez {item.Name} where?", "Where", "2 metres ahead");
        if (words == null) { Reader?.Answer("Not rezzed.", keep: false); return; }
        var (spot, error) = PlaceWords.Parse(words);
        if (spot == null) { Answer(error!); return; }
        if (spot.Relative) { Answer("By is for moving something already out; for rezzing, say where from you: 2 metres ahead."); return; }
        if (!spot.Horizontal) spot = spot with { Ahead = 2 };
        if (await SpotAsync(spot, null) is not { } at) { Answer("Not in a region yet."); return; }
        var r = await _ops.DoAsync("TesselBuild", "rez", new JsonObject { ["item_id"] = item.UUID.ToString(), ["pos"] = Xyz(at.X, at.Y, at.Z + 0.5) });
        if (PersonOps.Error(r) is { } e || !UUID.TryParse(r.Str("id"), out var id)) { Answer($"Couldn't rez {item.Name}: {PersonOps.Error(r) ?? "no object came"}"); return; }
        await Task.Delay(300);
        var bottom = await BottomAsync(id);
        await _ops.DoAsync("TesselBuild", "modify", new JsonObject { ["id"] = id.ToString(), ["pos"] = Xyz(at.X, at.Y, at.Z + spot.Up + bottom) });
        Answer($"Rezzed {item.Name}, {PlaceWords.Say(spot)}{OnWhat(at)}.");
        RefreshInventory(item.ParentUUID);
    }

    /// <summary>One of the person's things in the world moved where they say (the objects list's Move...).</summary>
    async Task MoveByWordsAsync(PersonOps.Thing t)
    {
        if (Host?.Session.Region is not { } w || w.PrimById(t.Id) is not { } prim) { Answer($"{t.Name} isn't here any more."); return; }
        var flags = Host.Session.State.PrimFlagsOf(prim);
        if ((flags & (PrimFlags.ObjectMove | PrimFlags.ObjectYouOwner)) == 0) { Answer($"{t.Name} isn't yours to move."); return; }
        var words = Dialogs.Ask(this, $"Move {t.Name} where?", "Where", "");
        if (words == null) { Reader?.Answer("Not moved.", keep: false); return; }
        var (spot, error) = PlaceWords.Parse(words);
        if (spot == null) { Answer(error!); return; }
        var now = w.RegionPosition(prim);
        double x, y, z;
        string said;
        if (spot.Relative || !spot.Horizontal)
        {
            // from where it is: by the person's directions (their left is still their left), up and down as said
            var heading = _ops.Me()?.Heading ?? 0;
            var (dx, dy) = spot.Offset(heading);
            (x, y, z) = (now.X + dx, now.Y + dy, now.Z + spot.Up);
            said = $"{t.Name} moved {PlaceWords.Say(spot with { Relative = false })}";
        }
        else
        {
            if (await SpotAsync(spot, prim) is not { } at) { Answer("Not in a region yet."); return; }
            var bottom = await BottomAsync(t.Id);
            (x, y, z) = (at.X, at.Y, at.Z + spot.Up + bottom);
            said = $"{t.Name} moved {PlaceWords.Say(spot)}{OnWhat(at)}";
        }
        var r = await _ops.DoAsync("TesselBuild", "modify", new JsonObject { ["id"] = t.Id.ToString(), ["pos"] = Xyz(x, y, z) });
        Answer(PersonOps.Error(r) is { } e ? $"Not moved: {e}" : said + ".");
    }

    /// <summary>A point on the surface under the spot the words give (region frame), and what it's on.</summary>
    sealed record SpotPoint(double X, double Y, double Z, string? On);

    async Task<SpotPoint?> SpotAsync(PlaceWords.Spot spot, Primitive? moving)
    {
        if (_ops.Me() is not { } me) return null;
        var (dx, dy) = spot.Offset(me.Heading);
        double x = me.Pos.X + dx, y = me.Pos.Y + dy;
        float feet = _walk?.Me()?.Feet ?? me.Pos.Z - 0.9f;
        // the collision model: the first solid surface down from head height (a table, a floor), the thing being moved
        // left out
        if (_rays != null && Host?.Session.Region is { } w)
        {
            var passable = moving == null ? null : Host.Session.Objects.Linkset(w, Host.Session.Objects.RootOf(w, moving)).Select(p => p.LocalID).ToHashSet();
            var from = new System.Numerics.Vector3((float)x, (float)y, feet + 2.2f);
            var rays = new[] { (from, -System.Numerics.Vector3.UnitZ, 30f) };
            if (await _rays.CollideManyAsync(rays, passable) is { } batch && batch.Hits[0] is { } hit)
            {
                var name = hit.LocalId != 0 && w.Prims.TryGetValue(hit.LocalId, out var on) ? Host.Session.Objects.RootOf(w, on).Properties?.Name : null;
                return new SpotPoint(x, y, from.Z - hit.Distance, hit.LocalId == 0 ? null : name);
            }
        }
        // else the ground there, or the floor the person stands on when that's well above it
        double ground = feet;
        var r = await _ops.ReadAsync("TesselAgent", "ground", new JsonObject { ["points"] = new JsonArray(new JsonArray(x, y)) });
        if ((r["heights"] as JsonArray)?.FirstOrDefault() is JsonValue hv && hv.TryGetValue<double>(out var terrain) && feet - terrain < 1.5)
            ground = terrain;
        return new SpotPoint(x, y, ground, null);
    }

    static string OnWhat(SpotPoint at) => at.On is { Length: > 0 } on && on is not ("Object" or "Primitive") ? $", on {on}" : "";

    /// <summary>How far an object's lowest point is below its root's middle (its whole linkset, turned as it is), so it
    /// can rest on a surface.</summary>
    async Task<double> BottomAsync(UUID root)
    {
        await _ops.ReadAsync("TesselObjects", "get", new JsonObject { ["ids"] = new JsonArray(root.ToString()), ["children"] = true, ["timeout"] = 3 });
        if (Host?.Session.Region is not { } w || w.PrimById(root) is not { } prim) return 0.25;
        var rootZ = w.RegionPosition(prim).Z;
        double lowest = 0;
        foreach (var p in Host.Session.Objects.Linkset(w, prim))
        {
            var q = w.RegionRotation(p);
            var rot = new System.Numerics.Quaternion(q.X, q.Y, q.Z, q.W);
            // the box's half height as turned: each axis's half length times how much of it points up
            double half = 0;
            foreach (var (axis, len) in new[] { (System.Numerics.Vector3.UnitX, p.Scale.X), (System.Numerics.Vector3.UnitY, p.Scale.Y), (System.Numerics.Vector3.UnitZ, p.Scale.Z) })
                half += Math.Abs(System.Numerics.Vector3.Transform(axis, rot).Z) * len / 2;
            lowest = Math.Max(lowest, rootZ - (w.RegionPosition(p).Z - half));
        }
        return lowest;
    }
}
