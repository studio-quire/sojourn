using System.Text.Json;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Session.Login;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Sojourn.Access;

/// <summary>
/// The landmarks in an account's Favorites bar (the Favorites system folder) as places to start at login, as the
/// official viewer offers them (LLFavoritesOrderStorage saves each account's favourites' SLurls while logged in, since
/// the login screen can't read the inventory, and the start location list shows them): kept in the account's state dir
/// (favorite-starts.json) by MainForm while she's in world, read by the login form for the account typed. The blind
/// tester's ask, 2026-11-02. Claude Opus 5.5.
/// </summary>
public static class StartPlaces
{
    public sealed record Place(string ItemId, string Name, string Region, int X, int Y, int Z)
    {
        public string Start => $"uri:{Region}&{X}&{Y}&{Z}";
    }

    const string FileName = "favorite-starts.json";

    /// <summary>An account's state dir: where its session keeps its own files (MainForm gives the session this one).</summary>
    public static string StateDir(string baseDir, string grid, string first, string last) =>
        Path.Combine(baseDir, "state", Grids.KeyOf(grid), $"{first}.{last}".ToLowerInvariant());

    public static List<Place> Load(string stateDir)
    {
        try
        {
            var file = Path.Combine(stateDir, FileName);
            if (File.Exists(file) && JsonSerializer.Deserialize<List<Place>>(File.ReadAllText(file)) is { } list) return list;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public static void Save(string stateDir, List<Place> places)
    {
        Directory.CreateDirectory(stateDir);
        var file = Path.Combine(stateDir, FileName);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(places, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, overwrite: true);
    }
}

public sealed partial class MainForm
{
    long _startsAt;
    bool _startsBusy;
    string? _startsSeen;

    /// <summary>The slow tick, in world: when the Favorites bar's landmarks have changed (checked every 10 s, once the
    /// inventory has loaded), where each points is read (new ones only) and the account's start places saved.</summary>
    void KeepStartPlaces()
    {
        if (!_arrived || _startsBusy || Environment.TickCount64 - _startsAt < 10_000 || Store is not { } store) return;
        var inv = _ops.Session.Inventory;
        if (inv.Loading is { IsCompleted: false }) return;
        _startsAt = Environment.TickCount64;
        var folder = inv.FolderFor(FolderType.Favorites);
        if (folder == UUID.Zero) return;
        List<InventoryItem> marks;
        try
        {
            marks = store.GetContents(folder).OfType<InventoryItem>()
                .Where(i => Target(store, i).AssetType == AssetType.Landmark)
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (InventoryException) { return; }
        var seen = string.Join("|", marks.Select(m => $"{m.UUID}={m.Name}"));
        if (seen == _startsSeen) return;
        _startsBusy = true;
        _ = SaveStartPlacesAsync(marks, seen);
    }

    async Task SaveStartPlacesAsync(List<InventoryItem> marks, string seen)
    {
        try
        {
            var dir = _ops.Session.StateDir;
            var before = StartPlaces.Load(dir);
            var known = before.ToDictionary(p => p.ItemId);
            var now = new List<StartPlaces.Place>();
            foreach (var m in marks)
            {
                if (known.TryGetValue(m.UUID.ToString(), out var had)) { now.Add(had with { Name = m.Name }); continue; }
                var lm = Store is { } store ? Target(store, m) : m;   // (a link: where its landmark points)
                var r = await _ops.ReadAsync("TesselInventory", "landmark", new JsonObject { ["item_id"] = lm.UUID.ToString() });
                if (r.Str("region") is { Length: > 0 } region && r["pos"] is JsonArray pos && pos.Count >= 3)
                    now.Add(new(m.UUID.ToString(), m.Name, region, (int)(Json.AsDouble(pos[0]) ?? 128), (int)(Json.AsDouble(pos[1]) ?? 128), (int)(Json.AsDouble(pos[2]) ?? 30)));
                else _ops.Session.Log.LogInformation("start places: where {Name} points didn't come ({Error})", m.Name, PersonOps.Error(r) ?? "no region");
            }
            if (!now.SequenceEqual(before)) StartPlaces.Save(dir, now);
            _startsSeen = seen;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _ops.Session.Log.LogWarning(e, "start places"); }
        finally { _startsBusy = false; }
    }
}
