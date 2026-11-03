using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Microsoft.Extensions.Logging;

namespace Sojourn.Access;

/// <summary>
/// Favourites, as the official viewer's 2024 inventory has them (llinventorybridge.cpp, llinventorypanel.cpp, read
/// 2026-11-02): any item or folder marked, staying where it is, the mark kept on Second Life's inventory service
/// (Core's InventoryMeta). Control+Shift+F or the Applications key's "Add to favorites" / "Remove from favorites" marks
/// what's marked with Space, else the selected one; a link's mark goes on what it links to, and a link says its
/// original's. An item's words end "favorite" (not in the Trash, as the viewer draws no star there). Show: Favorites
/// is the viewer's Favorites tab: the topmost favourites, each with where it lives, a favourite folder opening onto its
/// contents (favourites inside it aren't listed again at the top), links and the Trash left out.
///
/// LibreMetaverse's inventory fetch drops the flag, so the whole inventory's flags are read from the service once a
/// session, off the window's thread, after the inventory has loaded (the official viewer gets them with its own fetch);
/// words follow when that's done. OpenSim has no such service: the list and the keys say favourites aren't on this grid.
/// The blind tester's ask, 2026-11-02. Claude Opus 5.5.
/// </summary>
public sealed partial class MainForm
{
    const int ShowFavorites = 4;
    const string FavoritesNotHere = "Favorites aren't on this grid: Second Life keeps them in its inventory service, which this grid doesn't have.";

    InventoryMeta Meta => _ops.Session.InventoryMeta;
    Task? _favReading;
    string? _favError;

    /// <summary>Whether the words should say "favorite": a link by its original, nothing in the Trash, and not on a
    /// favourites list's own top entries (all of them are).</summary>
    bool SaysFavorite(InvPane p, InvNode tn)
    {
        if (p.Mode == "trash" || p.Mode == "favorites" && tn.Path != null || p.Show.SelectedIndex == ShowFavorites && tn.Path != null) return false;
        if (!IsFavorite(tn.Item)) return false;
        for (TreeNode? up = tn.Parent; up != null; up = up.Parent)
            if (up.Tag is InventoryFolder { PreferredType: FolderType.Trash }) return false;
        return true;
    }

    bool IsFavorite(InventoryBase n) => Meta.IsFavorite(n is InventoryItem { AssetType: AssetType.Link or AssetType.LinkFolder } l ? l.AssetUUID : n.UUID);

    /// <summary>After arriving: once the inventory has loaded, every favourite read from the service (Second Life only).</summary>
    async Task ReadFavoritesAfterLoadingAsync()
    {
        try
        {
            if (_ops.Session.Inventory.Loading is { } loading) await loading;
        }
        catch (Exception e) when (e is not OperationCanceledException) { }   // (a partial load: the sweep reads the service itself)
        if (IsDisposed || !Meta.Available || Meta.Swept) return;
        await ReadFavorites(refresh: false);
    }

    /// <summary>The service's favourites read (one reading at a time, shared), off the window's thread: a big inventory's
    /// answers are tens of megabytes. When it's done, the panes' words follow and a favourites list is shown again.</summary>
    Task ReadFavorites(bool refresh)
    {
        if (_favReading is { IsCompleted: false } running) return running;
        return _favReading = ReadAsync();

        async Task ReadAsync()
        {
            var took = System.Diagnostics.Stopwatch.StartNew();
            var r = await Task.Run(() => _ops.ReadAsync("SojournInventory", "favorites", new JsonObject { ["refresh"] = refresh }));
            _favError = PersonOps.Error(r);
            _ops.Session.Log.LogInformation("favorites read from the inventory service: {Count} in {Ms} ms{Error}",
                r["count"]?.GetValue<int>() ?? 0, took.ElapsedMilliseconds, _favError != null ? $" ({_favError})" : "");
            if (IsDisposed) return;
            foreach (var p in Panes)
            {
                if (!p.ShownOnce) continue;
                if (p.Show.SelectedIndex == ShowFavorites) ShowInventory(p, said: false);
                else Relabel(p, p.WornShown);
            }
        }
    }

    /// <summary>Show: Favorites (or a search within them) before the service has been read, or on a grid without one:
    /// a line saying so in place of the list; true when the list can't be shown yet.</summary>
    bool FavoritesNotReady(InvPane p, bool said, bool focus)
    {
        string? line = null;
        if (!Meta.Available) line = FavoritesNotHere;
        else if (!Meta.Swept)
        {
            bool failed = _favReading is { IsCompleted: true } && _favError != null;
            line = failed ? $"Couldn't read your favorites from Second Life: {_favError}. F5 tries again." : "Reading your favorites from Second Life.";
            if (!failed)
                ReadFavorites(refresh: false).ContinueWith(_ => BeginInvoke(() =>
                {
                    if (!IsDisposed && p.Show.SelectedIndex == ShowFavorites && p.Mode == "favorites")
                        ShowInventory(p, said: said, focus: focus && ActiveControl == p.Search);
                }), TaskScheduler.Default);
        }
        if (line == null) return false;
        p.Finding?.Cancel();
        p.Finding = null;
        p.Fills.Clear();
        p.Mode = "favorites";
        p.Tree.Nodes.Clear();
        p.Tree.SelectedNode = p.Tree.Nodes.Add(line);
        if (said) Reader?.Answer(line, keep: false);
        if (focus) p.Tree.Focus();
        return true;
    }

    /// <summary>The favourites list's walk (off the window's thread, as FindFlat's): the topmost favourites under My
    /// Inventory, a favourite folder holding its own (the viewer's hasPredecessorsInRootContent), links, the Trash and
    /// the outfit's links left out (the viewer's Favorites tab).</summary>
    static void WalkFavorites(LibreMetaverse.Inventory store, UUID folder, string path, UUID trash, Func<UUID, bool> fav,
        List<(InventoryBase Node, string Path)> found, ref int seen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        List<InventoryBase> contents;
        try { contents = store.GetContents(folder); }
        catch (InventoryException) { return; }
        foreach (var n in contents)
        {
            if (found.Count >= FlatLimit) return;
            seen++;
            if (n is InventoryItem { AssetType: AssetType.Link or AssetType.LinkFolder }) continue;
            if (fav(n.UUID)) { found.Add((n, path)); continue; }
            if (n is InventoryFolder f && f.UUID != trash && f.PreferredType != FolderType.CurrentOutfit)
                WalkFavorites(store, f.UUID, path.Length > 0 ? $"{path}/{f.Name}" : f.Name, trash, fav, found, ref seen, ct);
        }
    }

    /// <summary>Control+Shift+F, or the menu: what's chosen added to the favourites, or (all of it a favourite already)
    /// taken off them; <paramref name="add"/> from the menu says which.</summary>
    async Task FavoriteAsync(InvPane p, bool? add = null)
    {
        var chosen = Chosen(p);
        if (chosen.Count == 0) return;
        if (!Meta.Available) { Answer(FavoritesNotHere); return; }
        bool on = add ?? chosen.Any(c => !IsFavorite(c));
        var change = chosen.Where(c => IsFavorite(c) != on).ToList();
        if (change.Count == 0) { Answer(on ? $"{Named(chosen)} {(chosen.Count == 1 ? "is" : "are")} in your favorites already." : $"{Named(chosen)} isn't a favorite."); return; }
        var done = new List<string>();
        foreach (var n in change)
        {
            var r = await _ops.DoAsync("SojournInventory", "favorite", new JsonObject { ["id"] = n.UUID.ToString(), ["on"] = on });
            if (PersonOps.Error(r) is { } e) Answer($"{n.Name}: {(on ? "not added" : "not removed")}, {e}");
            else done.Add(n.Name);
        }
        if (done.Count > 0) Answer(on ? $"Added {Join(done)} to favorites." : $"Removed {Join(done)} from favorites.");
        Unmark(p);
        foreach (var pane in Panes)
        {
            if (!pane.ShownOnce) continue;
            if (pane.Show.SelectedIndex == ShowFavorites) ShowInventory(pane, said: false);
            else Relabel(pane, pane.WornShown);
        }
    }

    /// <summary>The menu's favourites entries for what's chosen: Add where any isn't one (never in the Library or the
    /// Trash, as the viewer), Remove where any is.</summary>
    IEnumerable<InvAction> FavoriteActions(InvPane p, List<InventoryBase> chosen, bool folder)
    {
        bool inTrash = p.Mode == "trash" || chosen.Any(c => InTrash(c));
        if (inTrash) yield break;
        if (chosen.Any(c => !IsFavorite(c))) yield return new(folder ? "Add to fa&vorites" : "Add to &favorites", () => _ = FavoriteAsync(p, add: true), "Control+Shift+F");
        if (chosen.Any(IsFavorite)) yield return new("Remove from favor&ites", () => _ = FavoriteAsync(p, add: false), chosen.All(IsFavorite) ? "Control+Shift+F" : null);
    }

    bool InTrash(InventoryBase n)
    {
        var trash = _ops.Session.Inventory.FolderFor(FolderType.Trash);
        if (Store is not { } store || trash == UUID.Zero) return false;
        var id = n.ParentUUID;
        for (int i = 0; i < 64 && id != UUID.Zero; i++)
        {
            if (id == trash) return true;
            if (!store.TryGetValue<InventoryBase>(id, out var up)) return false;
            id = up.ParentUUID;
        }
        return false;
    }

    /// <summary>View > Inventory favorites: the first pane showing its favourites, the focus on the list.</summary>
    void GoFavorites()
    {
        GoInventory(second: false);
        if (_pane1.Search.Text.Length > 0) _pane1.Search.Clear();
        if (_pane1.Show.SelectedIndex != ShowFavorites) _pane1.Show.SelectedIndex = ShowFavorites;
        ShowInventory(_pane1, said: true, focus: true);
    }

    /// <summary>The system folder the official viewer now calls the Favorites Bar (its landmarks, the login's start
    /// places), named so beside the new favourites.</summary>
    static string FolderWords(InventoryFolder f) => f.PreferredType == FolderType.Favorites ? "Favorites bar" : f.Name;
}
