using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Session.Assistant;

/// <summary>
/// The words around the person that help the assistant's ears hear names (SojournAssistant hearing; assistant/hearing.py
/// turns them into Whisper's prompt and Parakeet's name pass, and into the context line's names). Everything here is
/// already in memory: nothing is fetched from the grid or loaded from the inventory, so it's cheap enough to ask for every
/// few seconds. In a blind incognito spell the allowlist hands back only <c>worn</c> (Private/Allowlist.cs).
/// </summary>
public sealed class HearingWords
{
    readonly AgentSession _s;
    readonly List<string> _visited = new();      // regions this session has been in, newest first
    JsonArray? _kept;                             // inventory names, rebuilt at most once a minute
    JsonArray? _landmarks;
    double _keptAt = double.NegativeInfinity;
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public HearingWords(AgentSession s) => _s = s;

    bool _visitedLoaded;
    string VisitedFile => Path.Combine(_s.StateDir, "recent-regions.json");

    void LoadVisited()
    {
        if (_visitedLoaded) return;
        _visitedLoaded = true;
        try
        {
            if (File.Exists(VisitedFile) && JsonNode.Parse(File.ReadAllText(VisitedFile)) is JsonArray a)
                foreach (var n in a) if (n?.GetValue<string>() is { Length: > 0 } v && !_visited.Contains(v)) _visited.Add(v);
        }
        catch (Exception) { }   // a damaged file just starts the list afresh
    }

    void SaveVisited()
    {
        try { File.WriteAllText(VisitedFile, new JsonArray(_visited.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()).ToJsonString()); }
        catch (IOException) { }
    }

    public JsonObject Build()
    {
        var c = _s.Client ?? throw new NotConnected("viewer not connected");
        var names = _s.Names;
        var me = c.Self.SimPosition;

        // people in view and on the map, nearest first (the region's and its neighbours')
        var people = new List<(double D, JsonObject O)>();
        var seen = new HashSet<UUID> { c.Self.AgentID };
        void Person(UUID id, Vector3 pos)
        {
            if (!seen.Add(id) || names.Json(id) is not { } n) return;
            var d = Vector3.Distance(pos, me);
            if (d <= 96) people.Add((d, n));
        }
        foreach (var rw in _s.VisibleRegions())
            foreach (var av in rw.Avatars.Values)
            {
                var pos = av.ParentID != 0 && rw.Prims.TryGetValue(av.ParentID, out var seat) ? rw.RegionPosition(seat) + av.Position * seat.Rotation : av.Position;
                Person(av.ID, pos + _s.FrameOffset(rw));
            }
        foreach (var rw in _s.VisibleRegions())
            foreach (var (id, pos) in rw.Coarse) Person(id, pos + _s.FrameOffset(rw));

        // the person's own names first (they say them: a username came back misspelt, 2026-10-27), then where they
        // are: the region, the parcel if known, the neighbours in view
        var here = new JsonArray();
        foreach (var own in new[] { _s.Names.DisplayName(c.Self.AgentID), c.Self.FirstName,
                     c.Self.LastName is { Length: > 0 } ln && ln != "Resident" ? $"{c.Self.FirstName} {ln}" : null })
            if (own is { Length: > 0 } o && !here.Any(h => h?.GetValue<string>() == o)) here.Add(o);
        var sim = c.Network.CurrentSim;
        if (sim is { Name.Length: > 0 })
        {
            here.Add(sim.Name);
            lock (_visited)
            {
                LoadVisited();
                // kept across logins (a fresh login forgot Calas Galadhon, 2026-10-27), never during incognito
                if (!_s.Private.On && (_visited.Count == 0 || _visited[0] != sim.Name))
                {
                    _visited.Remove(sim.Name);
                    _visited.Insert(0, sim.Name);
                    if (_visited.Count > 24) _visited.RemoveAt(_visited.Count - 1);
                    SaveVisited();
                }
            }
            var local = c.Parcels.GetParcelLocalID(sim, me);
            if (local != 0 && sim.Parcels.TryGetValue(local, out var parcel) && parcel.Name is { Length: > 0 } pn) here.Add(pn);
        }
        foreach (var rw in _s.VisibleRegions())
            if (rw.Name is { Length: > 0 } rn && rn != sim?.Name) here.Add(rn);

        // friends, online first
        var friends = new JsonArray();
        foreach (var f in c.Friends.FriendList.Values.OrderByDescending(f => f.IsOnline))
        {
            var n = names.Json(f.UUID) ?? (string.IsNullOrEmpty(f.Name) ? null : new JsonObject { ["display_name"] = f.Name, ["username"] = Sojourn.Core.World.NameCache.UserName(f.Name) });
            if (n == null) continue;
            n["online"] = f.IsOnline;
            friends.Add(n);
        }

        // what they wear: the current outfit folder's links carry the worn items' names
        var worn = new JsonArray();
        if (c.Inventory.Store is { } store)
        {
            var cof = c.Inventory.FindFolderForType(FolderType.CurrentOutfit);
            foreach (var it in store.GetContents(cof).OfType<InventoryItem>())
                if (it.Name is { Length: > 0 } wn) worn.Add(wn);
        }

        if (_clock.Elapsed.TotalSeconds - _keptAt > 60) Inventory(c);
        var places = new JsonArray();
        lock (_visited) foreach (var v in _visited) if (v != sim?.Name) places.Add(v);
        foreach (var l in _landmarks ?? new JsonArray()) places.Add(l?.DeepClone());

        return new JsonObject
        {
            ["people"] = new JsonArray(people.OrderBy(p => p.D).Take(24).Select(p => (JsonNode)p.O).ToArray()),
            ["here"] = here,
            ["friends"] = friends,
            ["worn"] = worn,
            ["places"] = places,
            ["kept"] = _kept?.DeepClone() ?? new JsonArray(),
        };
    }

    /// <summary>Landmarks and the inventory's top folders (brands and outfits, as people file them), from what's
    /// loaded: never a fetch.</summary>
    void Inventory(GridClient c)
    {
        _keptAt = _clock.Elapsed.TotalSeconds;
        if (c.Inventory.Store is not { RootFolder: { } root } store) return;
        var trash = c.Inventory.FindFolderForType(FolderType.Trash);
        var kept = new List<string>();
        var landmarks = new List<string>();
        var queue = new Queue<UUID>();   // breadth first: the folders nearest the top first
        queue.Enqueue(root.UUID);
        int folders = 0;
        while (queue.Count > 0 && folders++ < 20000)
        {
            var fid = queue.Dequeue();
            foreach (var n in store.GetContents(fid))
            {
                if (n is InventoryFolder f)
                {
                    if (f.UUID == trash || f.PreferredType is FolderType.CurrentOutfit or FolderType.LostAndFound or FolderType.Inbox) continue;
                    // the system folders' own names aren't worth hearing; the person's own (brands, outfits) are
                    if (f.PreferredType == FolderType.None && kept.Count < 120 && f.Name is { Length: > 0 }) kept.Add(f.Name);
                    queue.Enqueue(f.UUID);
                }
                // a landmark made this session is a plain item to LibreMetaverse until the inventory reloads
                else if (n is InventoryItem { AssetType: AssetType.Landmark } lm && landmarks.Count < 60 && lm.Name is { Length: > 0 })
                    landmarks.Add(lm.Name);
            }
        }
        _kept = new JsonArray(kept.Select(k => (JsonNode)JsonValue.Create(k)).ToArray());
        _landmarks = new JsonArray(landmarks.Select(k => (JsonNode)JsonValue.Create(k)).ToArray());
    }
}
