using LibreMetaverse;
using Sojourn.Core.Access;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// One person's worn things and animations, from their menu (Radegast's Attachments and Animations on a person). What they
/// wear is Objects' Show with a fourth choice, "What Kestrel wears", touched, paid and read as what people nearby wear is
/// (the nearby op's worn_by); their animations are said in one answer, named where Second Life's own are or one of hers
/// shares the asset, else by what started them. Claude Opus 5.5, 2026-11-08.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>Whose things Show's fourth choice lists (it's added the first time).</summary>
    (UUID Id, string Name)? _wornBy;

    const int ShowOne = 3;
    bool ShowingOne => _objectsShow.SelectedIndex == ShowOne;

    static string WornByChoice(string name) => $"What {name} wears";

    /// <summary>A person's menu: Objects listing what they wear, the focus in it.</summary>
    void ShowWornBy(UUID id, string name)
    {
        _wornBy = (id, name);
        var choice = WornByChoice(name);
        if (_objectsShow.Items.Count <= ShowOne) _objectsShow.Items.Add(choice);
        else if ((string)_objectsShow.Items[ShowOne] != choice)
        {
            // (replacing the selected item would drop the selection, and its refresh with it)
            if (ShowingOne) _objectsShow.SelectedIndex = 0;
            _objectsShow.Items[ShowOne] = choice;
        }
        if (_objectsShow.SelectedIndex != ShowOne) _objectsShow.SelectedIndex = ShowOne;   // (refreshes)
        else
        {
            ObjectsShowChanged();
            _ = RefreshObjects(false);
        }
        GoArea(_objectsPage);
    }

    bool WornByHere => _wornBy is { } w && Host?.Session.VisibleRegions().Any(r => r.AvatarById(w.Id) != null) == true;

    string WornByEmpty => _wornBy is not { } w ? "Choose What they wear on a person's menu."
        : WornByHere ? $"Nothing {w.Name} wears is in view." : $"{w.Name} isn't nearby.";

    string WornByCount(int n) => _wornBy is { } w ? (n == 1 ? $"1 thing {w.Name} wears." : $"{n} things {w.Name} wears.") : $"{n} things.";

    /// <summary>A person's menu: what animations they're playing, as the region last listed them.</summary>
    async Task AnimationsOfAsync(UUID id, string name)
    {
        if (Host?.Session is not { } s) return;
        var found = s.VisibleRegions().Select(w => (W: w, A: w.AvatarById(id))).FirstOrDefault(x => x.A != null);
        if (found.A is not { } av) { Answer($"{name} isn't nearby."); return; }
        List<Animation> list;
        lock (av) list = av.Animations?.ToList() ?? new();
        if (list.Count == 0) { Answer($"Second Life hasn't said what {name} is playing yet."); return; }
        var assets = list.Select(a => a.AnimationID).ToHashSet();
        var mine = await Task.Run(() => AnimationNames(assets));
        // the names of the things that started them (a thing's name comes once asked for)
        var unnamed = list.Select(a => found.W.PrimById(a.AnimationSourceObjectID)).OfType<Primitive>()
            .Select(p => s.Objects.RootOf(found.W, p)).Where(r => r.Properties == null).DistinctBy(r => r.LocalID).ToList();
        if (unnamed.Count > 0)
            try { await s.Objects.FetchAsync(found.W, unnamed, TimeSpan.FromSeconds(2), CancellationToken.None); } catch (Exception) { }
        // what started each: themselves (their viewer: Second Life's own moves, a gesture, a viewer's AO), a thing here,
        // or something not in view (others never see a HUD's prims)
        var groups = new List<(string From, List<string> Named, int Unnamed)>();
        foreach (var a in list)
        {
            var named = GestureOps.BuiltInName(a.AnimationID) is { } b ? b.Replace('_', ' ') : mine.GetValueOrDefault(a.AnimationID);
            var from = SourceWords(s, found.W, av, a.AnimationSourceObjectID);
            int at = groups.FindIndex(g => g.From == from);
            if (at < 0) { groups.Add((from, new(), 0)); at = groups.Count - 1; }
            var g = groups[at];
            if (named != null) g.Named.Add(named);
            groups[at] = named != null ? g : g with { Unnamed = g.Unnamed + 1 };
        }
        var parts = groups.OrderBy(g => g.From.Length == 0 ? 0 : 1).Select(g =>
        {
            var what = g.Named.ToList();
            if (g.Unnamed > 0) what.Add(g.Unnamed == 1 ? (what.Count == 0 ? "one" : "one more") : $"{g.Unnamed}{(what.Count == 0 ? "" : " more")}");
            return g.From.Length == 0 ? AndList(what) : $"{AndList(what)}, {g.From}";
        });
        Answer($"{name} is playing {(list.Count == 1 ? "1 animation" : $"{list.Count} animations")}: {string.Join("; ", parts)}.", LineFrom.People);
    }

    /// <summary>"" for their own (Second Life's moves, their gestures), "from Dance Ball", "from Vista AO, which they wear",
    /// "from something not in view, most likely a HUD of theirs".</summary>
    string SourceWords(Sojourn.Core.Agents.AgentSession s, Sojourn.Core.World.RegionWorld w, Avatar av, UUID source)
    {
        if (source == UUID.Zero || source == av.ID) return "";
        if (w.PrimById(source) is not { } prim) return "from something not in view, most likely a HUD of theirs";
        s.Objects.IsAttachment(w, prim, out var wearer, out var root);
        var name = root.Properties?.Name is { Length: > 0 } n ? n : "an object";
        if (wearer?.ID == av.ID) return $"from {name}, which they wear";
        if (wearer != null) return $"from {name}, which {s.Names.DisplayName(wearer.ID) ?? wearer.Name} wears";
        return av.ParentID != 0 && (av.ParentID == root.LocalID || av.ParentID == prim.LocalID) ? $"from {name}, which they're sitting on" : $"from {name}";
    }

    /// <summary>Her own animations' names by their assets (only those whose asset Second Life shows her: ones she may
    /// copy, change and pass on). Any thread.</summary>
    Dictionary<UUID, string> AnimationNames(HashSet<UUID> assets)
    {
        var names = new Dictionary<UUID, string>();
        if (_ops.Client?.Inventory.Store is not { } store || store.RootFolder is not { } root) return names;
        Walk(store, root.UUID, "", UUID.Zero, CancellationToken.None, (n, _) =>
        {
            if (n is InventoryItem { AssetType: AssetType.Animation } it && assets.Contains(it.AssetUUID)) names.TryAdd(it.AssetUUID, it.Name);
            return names.Count < assets.Count;
        });
        return names;
    }
}
