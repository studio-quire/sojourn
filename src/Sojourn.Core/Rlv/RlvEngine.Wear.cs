using LibreMetaverse;

namespace Sojourn.Core.Rlv;

[Flags]
public enum Lock { None = 0, Add = 1, Remove = 2 }

public enum FolderSourceKind { Attachment, SharedPath, Point, Layer, Root }

/// <summary>A folder lock (RLVa's RlvFolderLocks entry): what it locks, deny or allow, the node or its subtree, who holds it.</summary>
public sealed record FolderLockEntry(FolderSourceKind Kind, object Value, bool Allow, bool Subtree, UUID Obj, Lock Type);

/// <summary>Locks on attachments, points, layers and folders, the #RLV shared folder, and wearing by command
/// (rlvlocks.cpp, rlvinventory.cpp, RlvForceWear in rlvhelper.cpp).</summary>
public sealed partial class RlvEngine
{
    // @detach=n: the holder's own attachment can't come off
    readonly HashSet<UUID> _attachLocks = new();
    readonly List<(int Point, UUID Obj, Lock Type)> _pointLocks = new();
    readonly List<(int Layer, UUID Obj, Lock Type)> _wearLocks = new();
    readonly List<FolderLockEntry> _folderLocks = new();
    // force-wear commands gathered across one line, done together at its end (RLVa's RlvForceWear::done)
    readonly List<(RlvItem Item, int Point, bool Add)> _toWear = new();
    readonly List<UUID> _toTakeOff = new();

    void LockObject(UUID obj, bool add)
    {
        if (add) _attachLocks.Add(obj); else _attachLocks.Remove(obj);
    }

    void LockPoints(int point, UUID obj, Lock type, bool add)
    {
        if (add) _pointLocks.Add((point, obj, type));
        else { int i = _pointLocks.FindIndex(p => p.Point == point && p.Obj == obj && p.Type == type); if (i >= 0) _pointLocks.RemoveAt(i); }
    }

    void LockWearables(int layer, UUID obj, Lock type, bool add)
    {
        if (add) _wearLocks.Add((layer, obj, type));
        else { int i = _wearLocks.FindIndex(p => p.Layer == layer && p.Obj == obj && p.Type == type); if (i >= 0) _wearLocks.RemoveAt(i); }
    }

    void FolderLock(FolderLockEntry e, bool add)
    {
        if (add) _folderLocks.Add(e);
        else { int i = _folderLocks.IndexOf(e); if (i >= 0) _folderLocks.RemoveAt(i); }
    }

    (FolderSourceKind Kind, object Value)? FolderSource(RlvCommand cmd)
    {
        if (!cmd.HasOption) return (FolderSourceKind.Attachment, cmd.Object);
        if (RlvNames.WearableType(cmd.Option) is var wt and >= 0) return (FolderSourceKind.Layer, wt);
        if (RlvNames.AttachPoint(cmd.Option) is var pt and > 0) return (FolderSourceKind.Point, pt);
        return SharedFolder(cmd.Option) != null ? (FolderSourceKind.SharedPath, cmd.Option) : null;
    }

    // ---------- the shared folder ----------

    /// <summary>A shared folder by path ("Outfits/Red dress"): each step an exact match (any case), else the first
    /// folder containing it that isn't hidden (RlvInventory::getSharedFolder).</summary>
    RlvFolder? SharedFolder(string path, bool partial = true)
    {
        var f = _host.SharedRoot();
        if (f == null) return null;
        foreach (var step in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            RlvFolder? exact = null, part = null;
            foreach (var c in f.Folders)
            {
                if (c.Name.Equals(step, StringComparison.OrdinalIgnoreCase)) { exact = c; break; }
                if (partial && part == null && !c.Name.StartsWith('.') && c.Name.Contains(step, StringComparison.OrdinalIgnoreCase)) part = c;
            }
            f = exact ?? part;
            if (f == null) return null;
        }
        return f;
    }

    static string SharedPath(RlvFolder f)
    {
        var parts = new List<string>();
        for (var c = f; c?.Parent != null; c = c.Parent) parts.Insert(0, c.Name);
        return string.Join("/", parts);
    }

    /// <summary>A folder whose contents fold into its parent: ".(point)" names, and "+"-less hidden ones with an
    /// attachment point (RlvInventory::isFoldedFolder, without legacy naming).</summary>
    static bool IsFolded(RlvFolder f)
    {
        var (text, at) = RlvNames.FirstParenthesised(f.Name);
        return f.Name.StartsWith('.') && at == 1 && RlvNames.AttachPoint(text) > 0;
    }

    static IEnumerable<RlvFolder> Descendants(RlvFolder f)
    {
        foreach (var c in f.Folders)
        {
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    /// <summary>The attachment point an item names: "(chest)" at the end of its name, or a ".(chest)" folder it's in
    /// (no-modify items: the folder first), 0 when neither does (RlvAttachPtLookup::getAttachPointIndex).</summary>
    static int PointOf(RlvItem item)
    {
        int ByFolder() => item.Folder is { } f && f.Name.StartsWith('.') && RlvNames.FirstParenthesised(f.Name) is var (t, at) && at == 1
            ? RlvNames.AttachPoint(t) : 0;
        int ByName() => item.IsLink ? 0 : RlvNames.AttachPoint(RlvNames.LastParenthesised(item.Name));
        return item.Modify ? (ByName() is var a and > 0 ? a : ByFolder()) : (ByFolder() is var b and > 0 ? b : ByName());
    }

    static bool NoStrip(RlvItem item)
    {
        if (!item.IsLink && item.Name.Contains("nostrip", StringComparison.Ordinal)) return true;
        for (var f = item.Folder; f != null; f = IsFolded(f) ? f.Parent : null)
            if (f.Name.Contains("nostrip", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>What @attach/@detach on a folder would wear or take off (RlvWearableItemCollector): the folder's own items,
    /// folded ".(point)" folders' items, and with subtree (attachall) every non-hidden descendant's; "+"-prefixed folders
    /// add rather than replace. Each with its point and whether it adds.</summary>
    List<(RlvItem Item, bool Add)> Collect(RlvFolder root, bool wear, bool subtree, bool add)
    {
        var outList = new List<(RlvItem, bool)>();
        void Visit(RlvFolder f, bool adding, bool top)
        {
            if (!top && f.Name.StartsWith('+')) adding = true;
            foreach (var item in f.Items)
            {
                if (!wear && NoStrip(item)) continue;
                switch (item.Kind)
                {
                    case RlvItemKind.BodyPart when !wear:
                        break;
                    case RlvItemKind.BodyPart:
                    case RlvItemKind.Clothing:
                    case RlvItemKind.Object:
                    case RlvItemKind.Gesture:
                        outList.Add((item, adding));
                        break;
                    case RlvItemKind.FolderLink:
                        if (_host.SharedRoot() is { } r && Find(r, item.LinkedFolder) is { } linked) Visit(linked, adding, false);
                        break;
                }
            }
            foreach (var c in f.Folders)
            {
                if (IsFolded(c)) { if (!wear || c.Items.Count(i => i.Kind == RlvItemKind.Object) == 1) Visit(c, adding, false); }
                else if (subtree && !c.Name.StartsWith('.')) Visit(c, adding, false);
            }
        }
        Visit(root, add || root.Name.StartsWith('+'), true);
        return outList;
    }

    static RlvFolder? Find(RlvFolder f, UUID id) =>
        f.Id == id ? f : f.Folders.Select(c => Find(c, id)).FirstOrDefault(x => x != null);

    // ---------- may it be worn or taken off ----------

    /// <summary>Whether a folder is locked for adding or removing (RlvFolderLocks::isLockedFolder): walking up from it, a
    /// deny lock on it (or on an ancestor, for a subtree lock) locks it unless the same holder allowed it nearer.</summary>
    bool FolderLocked(UUID folder, Lock type)
    {
        if (_folderLocks.Count == 0) return false;
        var map = new List<(UUID Folder, FolderLockEntry E)>();
        bool rootLocked = false;
        foreach (var e in _folderLocks)
        {
            if ((e.Type & type) == 0) continue;
            foreach (var f in LockedFolders(e))
            {
                if (f == _host.InventoryRoot) { if (e.Subtree && !e.Allow) rootLocked = true; }
                else map.Add((f, e));
            }
        }
        var allowedBy = new HashSet<UUID>();
        var cur = folder;
        int guard = 0;
        while (cur != UUID.Zero && cur != _host.InventoryRoot && guard++ < 64)
        {
            foreach (var (f, e) in map)
            {
                if (f != cur) continue;
                if (!e.Subtree && cur != folder) continue;
                if (allowedBy.Contains(e.Obj)) continue;
                if (e.Allow) allowedBy.Add(e.Obj);
                else return true;
            }
            cur = _host.ParentOf(cur);
        }
        return rootLocked && allowedBy.Count == 0;
    }

    IEnumerable<UUID> LockedFolders(FolderLockEntry e)
    {
        switch (e.Kind)
        {
            case FolderSourceKind.Root:
                yield return _host.InventoryRoot;
                break;
            case FolderSourceKind.SharedPath:
                if (((string)e.Value).Length == 0) { if (_host.SharedRoot() is { } r) yield return r.Id; }
                else if (SharedFolder((string)e.Value) is { } f) yield return f.Id;
                break;
            case FolderSourceKind.Attachment:
            {
                var obj = (UUID)e.Value;
                var root = _holders.TryGetValue(obj, out var h) && h.Root != UUID.Zero ? h.Root : obj;
                foreach (var w in _host.Worn())
                    if (w.Object == root && w.Point != 0) yield return _host.FolderOf(w.Item);
                break;
            }
            case FolderSourceKind.Point:
                foreach (var w in _host.Worn())
                    if (w.Point == (int)e.Value) yield return _host.FolderOf(w.Item);
                break;
            case FolderSourceKind.Layer:
                foreach (var w in _host.Worn())
                    if (w.Point == 0 && w.WearableType == (int)e.Value) yield return _host.FolderOf(w.Item);
                break;
        }
    }

    bool PointLocked(int point, Lock type, UUID except = default) =>
        _pointLocks.Any(p => (p.Point == 0 || p.Point == point) && (p.Type & type) != 0 && p.Obj != except);

    bool LayerLocked(int layer, Lock type, UUID except = default) =>
        _wearLocks.Any(p => (p.Layer < 0 || p.Layer == layer) && (p.Type & type) != 0 && p.Obj != except);

    /// <summary>Whether a worn attachment may come off, and if not, which holder's restriction keeps it on.</summary>
    string? DetachBlock(RlvWorn w, UUID except = default)
    {
        foreach (var h in _holders.Values)
            if (_attachLocks.Contains(h.Id) && h.Id != except && (h.Root == w.Object || h.Id == w.Object))
                return $"@detach, by {Named(h.Id)}, which locks itself on";
        if (_pointLocks.FirstOrDefault(p => (p.Point == 0 || p.Point == w.Point) && (p.Type & Lock.Remove) != 0 && p.Obj != except) is { Obj: var o } pl && o != UUID.Zero)
            return $"@{(pl.Point == 0 ? "remattach" : "detach:" + RlvNames.PointName(pl.Point).ToLowerInvariant())}, by {Named(o)}";
        if (FolderLocked(_host.FolderOf(w.Item), Lock.Remove)) return "@detachthis: its folder is locked";
        return null;
    }

    string? TakeOffBlock(RlvWorn w, UUID except = default)
    {
        if (w.Point != 0) return DetachBlock(w, except);
        if (RlvNames.IsBodyPart(w.WearableType)) return null;   // (not RLV's: the wardrobe says so itself)
        if (_wearLocks.FirstOrDefault(p => (p.Layer < 0 || p.Layer == w.WearableType) && (p.Type & Lock.Remove) != 0 && p.Obj != except) is { Obj: var o } wl && o != UUID.Zero)
            return $"@remoutfit{(wl.Layer < 0 ? "" : ":" + RlvNames.Layers[wl.Layer])}, by {Named(o)}";
        if (FolderLocked(_host.FolderOf(w.Item), Lock.Remove)) return "@detachthis: its folder is locked";
        return null;
    }

    string? AttachBlock(int point, UUID folder, bool add, UUID except = default)
    {
        if (point != 0 && _pointLocks.FirstOrDefault(p => (p.Point == 0 || p.Point == point) && (p.Type & Lock.Add) != 0 && p.Obj != except) is { Obj: var o } pl && o != UUID.Zero)
            return $"@{(pl.Point == 0 ? "addattach" : "detach:" + RlvNames.PointName(pl.Point).ToLowerInvariant())}, by {Named(o)}";
        if (point == 0 && _pointLocks.FirstOrDefault(p => p.Point == 0 && (p.Type & Lock.Add) != 0 && p.Obj != except) is { Obj: var o2 } && o2 != UUID.Zero)
            return $"@addattach, by {Named(o2)}";
        if (folder != UUID.Zero && FolderLocked(folder, Lock.Add)) return "@attachthis: its folder is locked";
        if (!add && point != 0)
            foreach (var w in _host.Worn())
                if (w.Point == point && DetachBlock(w, except) is { } why) return "it would replace something held on: " + why;
        return null;
    }

    string? WearBlock(int layer, UUID folder, bool add, UUID except = default)
    {
        if (_wearLocks.FirstOrDefault(p => (p.Layer < 0 || p.Layer == layer) && (p.Type & Lock.Add) != 0 && p.Obj != except) is { Obj: var o } wl && o != UUID.Zero)
            return $"@addoutfit{(wl.Layer < 0 ? "" : ":" + RlvNames.Layers[wl.Layer])}, by {Named(o)}";
        if (folder != UUID.Zero && FolderLocked(folder, Lock.Add)) return "@attachthis: its folder is locked";
        if (!add && !RlvNames.IsBodyPart(layer))
            foreach (var w in _host.Worn())
                if (w.Point == 0 && w.WearableType == layer && TakeOffBlock(w, except) is { } why) return "it would replace something held on: " + why;
        return null;
    }

    string Named(UUID obj) => $"'{NameOf(obj)}'";

    static string? Said(string what, string? why) => why == null ? null : $"RLV: {what} is restricted ({why})";

    // ---------- the person's own wearing, for every caller ----------

    /// <summary>Why an attachment can't be detached now (null: it can).</summary>
    public string? CanDetach(UUID objectOrItem)
    {
        lock (_gate)
        {
            var w = _host.Worn().FirstOrDefault(x => x.Point != 0 && (x.Object == objectOrItem || x.Item == objectOrItem));
            return w == null ? null : Said("taking that off", DetachBlock(w));
        }
    }

    /// <summary>Why a worn item (attachment or clothing) can't be taken off now.</summary>
    public string? CanTakeOff(UUID item)
    {
        lock (_gate)
        {
            var w = _host.Worn().FirstOrDefault(x => x.Item == item || x.Object == item);
            return w == null ? null : Said("taking that off", TakeOffBlock(w));
        }
    }

    /// <summary>Why an item can't be put on (point 0: its own; add: beside what's there rather than replacing).</summary>
    public string? CanAttach(UUID item, int point, bool add)
    {
        lock (_gate) return Said("putting that on", AttachBlock(point, _host.FolderOf(item), add));
    }

    public string? CanWear(UUID item, int layer, bool add)
    {
        lock (_gate) return Said("putting that on", WearBlock(layer, _host.FolderOf(item), add));
    }

    // ---------- force wear ----------

    void FlushWear()
    {
        if (_toWear.Count > 0) { _host.Wear(_toWear.ToList()); _toWear.Clear(); }
        if (_toTakeOff.Count > 0) { _host.TakeOff(_toTakeOff.Distinct().ToList()); _toTakeOff.Clear(); }
    }

    RlvRet ForceFolder(RlvFolder folder, RlvWear flags, UUID by)
    {
        bool remove = (flags & RlvWear.Remove) != 0, subtree = (flags & RlvWear.Subtree) != 0, add = (flags & RlvWear.Add) != 0;
        var items = Collect(folder, !remove, subtree, add);
        if (remove)
        {
            var worn = _host.Worn();
            foreach (var (item, _) in items)
            foreach (var w in worn)
                if (w.Item == item.Target && TakeOffBlock(w) == null) _toTakeOff.Add(w.Item);
            return RlvRet.Success;
        }
        var seenLayers = new HashSet<int>();
        foreach (var (item, adding) in items)
        {
            switch (item.Kind)
            {
                case RlvItemKind.Object:
                {
                    int pt = PointOf(item);
                    if (AttachBlock(pt, item.Folder?.Id ?? UUID.Zero, adding) != null) continue;
                    if (!adding && pt != 0) _toWear.RemoveAll(x => x.Point == pt && !x.Add);
                    _toWear.Add((item, pt, adding));
                    break;
                }
                case RlvItemKind.Clothing:
                case RlvItemKind.BodyPart:
                {
                    // the second item of a layer in one go always adds (RlvForceWear::forceFolder)
                    bool a = adding || !seenLayers.Add(item.WearableType);
                    if (WearBlock(item.WearableType, item.Folder?.Id ?? UUID.Zero, a) != null) continue;
                    _toWear.Add((item, 0, a));
                    break;
                }
                case RlvItemKind.Gesture:
                    _toWear.Add((item, 0, true));
                    break;
            }
        }
        return RlvRet.Success;
    }

    /// <summary>The folders of the items an object's force command means: the folder its own item is in (option empty),
    /// what's worn on a point or layer, or a shared path (RlvCommandOptionGetPath).</summary>
    List<RlvFolder> ContextFolders(RlvCommand cmd)
    {
        var root = _host.SharedRoot();
        var outList = new List<RlvFolder>();
        if (root == null) return outList;
        IEnumerable<UUID> items;
        var worn = _host.Worn();
        if (!cmd.HasOption)
        {
            var obj = _holders.TryGetValue(cmd.Object, out var h) && h.Root != UUID.Zero ? h.Root : (_host.Object(cmd.Object)?.Root ?? cmd.Object);
            items = worn.Where(w => w.Object == obj && w.Point != 0).Select(w => w.Item);
        }
        else if (RlvNames.WearableType(cmd.Option) is var wt and >= 0) items = worn.Where(w => w.Point == 0 && w.WearableType == wt).Select(w => w.Item);
        else if (RlvNames.AttachPoint(cmd.Option) is var pt and > 0) items = worn.Where(w => w.Point == pt).Select(w => w.Item);
        else
        {
            if (SharedFolder(cmd.Option) is { } f) outList.Add(f);
            return outList;
        }
        foreach (var id in items)
            if (ItemIn(root, id) is { Folder: { } f })
            {
                var g = IsFolded(f) && f.Parent != null ? f.Parent : f;
                if (!outList.Contains(g)) outList.Add(g);
            }
        return outList;
    }

    static RlvItem? ItemIn(RlvFolder f, UUID target)
    {
        foreach (var i in f.Items) if (i.Target == target || i.Id == target) return i;
        foreach (var c in f.Folders) if (ItemIn(c, target) is { } x) return x;
        return null;
    }

    RlvRet ForceWear(RlvCommand cmd, RlvWear flags)
    {
        if ((flags & RlvWear.ContextObject) != 0)
        {
            var folders = ContextFolders(cmd);
            if (cmd.HasOption && folders.Count == 0 && RlvNames.AttachPoint(cmd.Option) == 0 && RlvNames.WearableType(cmd.Option) < 0) return RlvRet.Option;
            foreach (var f in folders) ForceFolder(f, flags, cmd.Object);
            return RlvRet.Success;
        }
        if (_host.SharedRoot() == null) return RlvRet.NoSharedRoot;
        if (!cmd.HasOption || SharedFolder(cmd.Option) is not { } folder) return RlvRet.Option;
        return ForceFolder(folder, flags, cmd.Object);
    }

    /// <summary>@detach/@remattach=force: a folder, a point, a group, an attachment by id, or everything.</summary>
    RlvRet ForceDetach(RlvCommand cmd)
    {
        var worn = _host.Worn().Where(w => w.Point != 0).ToList();
        if (cmd.HasOption && RlvNames.AttachPoint(cmd.Option) is var pt and > 0)
            worn = worn.Where(w => w.Point == pt).ToList();
        else if (cmd.HasOption && RlvNames.Groups.Contains(cmd.Option))
            worn = worn.Where(w => RlvNames.Group(w.Point) == cmd.Option).ToList();
        else if (cmd.HasOption && UUID.TryParse(cmd.Option, out var id))
            worn = worn.Where(w => w.Object == id).ToList();
        else if (cmd.HasOption)
        {
            if (SharedFolder(cmd.Option) is not { } f) return RlvRet.Option;
            return ForceFolder(f, RlvWear.Remove | RlvWear.Node, cmd.Object);
        }
        var root = _host.SharedRoot();
        foreach (var w in worn)
            if (DetachBlock(w) == null && !(root != null && ItemIn(root, w.Item) is { } item && NoStrip(item)))
                _toTakeOff.Add(w.Item);
        return RlvRet.Success;
    }

    /// <summary>@remoutfit[:layer|folder]=force.</summary>
    RlvRet ForceRemOutfit(RlvCommand cmd)
    {
        int layer = -1;
        if (cmd.HasOption && (layer = RlvNames.WearableType(cmd.Option)) < 0)
        {
            if (SharedFolder(cmd.Option) is not { } f) return RlvRet.Option;
            return ForceFolder(f, RlvWear.Remove | RlvWear.Node, cmd.Object);
        }
        var root = _host.SharedRoot();
        foreach (var w in _host.Worn())
            if (w.Point == 0 && !RlvNames.IsBodyPart(w.WearableType) && (layer < 0 || w.WearableType == layer) && TakeOffBlock(w) == null
                && !(root != null && ItemIn(root, w.Item) is { } item && NoStrip(item)))
                _toTakeOff.Add(w.Item);
        return RlvRet.Success;
    }

    // ---------- replies about the shared folder ----------

    string GetInv(RlvCommand cmd, out RlvRet ret)
    {
        ret = RlvRet.Success;
        if (_host.SharedRoot() == null) { ret = RlvRet.NoSharedRoot; return ""; }
        if (SharedFolder(cmd.Option) is not { } f) { ret = RlvRet.Option; return ""; }
        return string.Join(",", f.Folders.Where(c => c.Name.Length > 0 && !c.Name.StartsWith('.') && !IsFolded(c) && !c.Name.Contains('/')).Select(c => c.Name));
    }

    /// <summary>@getinvworn: "|XY,child|XY..." where X is the folder's own items and Y its descendants': 0 none, 1 none
    /// worn, 2 some, 3 all (onGetInvWorn).</summary>
    string GetInvWorn(RlvCommand cmd, out RlvRet ret)
    {
        ret = RlvRet.Success;
        if (_host.SharedRoot() == null) { ret = RlvRet.NoSharedRoot; return ""; }
        if (SharedFolder(cmd.Option) is not { } f) { ret = RlvRet.Option; return ""; }
        var worn = _host.Worn().Select(w => w.Item).ToHashSet();
        (int Worn, int Total) Own(RlvFolder x)
        {
            int w = 0, t = 0;
            void Count(RlvFolder y)
            {
                foreach (var i in y.Items)
                    if (i.Kind is RlvItemKind.Object or RlvItemKind.Clothing or RlvItemKind.BodyPart)
                    { t++; if (worn.Contains(i.Target)) w++; }
                foreach (var c in y.Folders) if (IsFolded(c)) Count(c);
            }
            Count(x);
            return (w, t);
        }
        (int Worn, int Total) Below(RlvFolder x)
        {
            int w = 0, t = 0;
            foreach (var c in x.Folders)
            {
                if (IsFolded(c) || c.Name.StartsWith('.')) continue;
                var (a, b) = Own(c); var (d, e) = Below(c);
                w += a + d; t += b + e;
            }
            return (w, t);
        }
        static int Code((int Worn, int Total) c) => c.Total == 0 ? 0 : c.Worn == 0 ? 1 : c.Worn != c.Total ? 2 : 3;
        var sb = new System.Text.StringBuilder($"|{Code(Own(f))}{Code(Below(f))}");
        foreach (var c in f.Folders)
            if (!IsFolded(c) && !c.Name.StartsWith('.')) sb.Append($",{c.Name}|{Code(Own(c))}{Code(Below(c))}");
        return sb.ToString();
    }

    /// <summary>@findfolder(s):a&&b — folders under #RLV whose names contain every term (not hidden, not
    /// starting with '~'); findfolder gives the deepest.</summary>
    string FindFolder(RlvCommand cmd, bool all, out RlvRet ret)
    {
        ret = RlvRet.Success;
        if (!cmd.HasOption) { ret = RlvRet.Option; return ""; }
        if (_host.SharedRoot() is not { } root) return "";
        var terms = cmd.Option.Split("&&", StringSplitOptions.RemoveEmptyEntries);
        var hits = Descendants(root).Where(f => !f.Name.StartsWith('.') && !f.Name.StartsWith('~') && !HiddenAncestor(f)
            && terms.All(t => f.Name.Contains(t, StringComparison.OrdinalIgnoreCase))).Select(SharedPath).ToList();
        if (all) return string.Join(",", hits);
        return hits.OrderByDescending(p => p.Count(ch => ch == '/')).ThenBy(p => hits.IndexOf(p)).FirstOrDefault() ?? "";
    }

    static bool HiddenAncestor(RlvFolder f)
    {
        for (var p = f.Parent; p?.Parent != null; p = p.Parent) if (p.Name.StartsWith('.')) return true;
        return false;
    }

    /// <summary>@getpath(new)[:point|layer|uuid]: the shared path of what's worn there (or of the issuing object).</summary>
    string GetPath(RlvCommand cmd, bool all)
    {
        if (_host.SharedRoot() is not { } root) return "";
        var worn = _host.Worn();
        IEnumerable<UUID> items;
        if (!cmd.HasOption)
        {
            var obj = _holders.TryGetValue(cmd.Object, out var h) && h.Root != UUID.Zero ? h.Root : (_host.Object(cmd.Object)?.Root ?? cmd.Object);
            items = worn.Where(w => w.Object == obj).Select(w => w.Item);
        }
        else if (RlvNames.AttachPoint(cmd.Option) is var pt and > 0) items = worn.Where(w => w.Point == pt).Select(w => w.Item);
        else if (RlvNames.WearableType(cmd.Option) is var wt and >= 0) items = worn.Where(w => w.Point == 0 && w.WearableType == wt).Select(w => w.Item);
        else if (UUID.TryParse(cmd.Option, out var id)) items = worn.Where(w => w.Object == id).Select(w => w.Item);
        else return "";
        var paths = new List<string>();
        foreach (var it in items)
            if (ItemIn(root, it) is { Folder: { } f })
            {
                var g = IsFolded(f) && f.Parent != null ? f.Parent : f;
                var p = SharedPath(g);
                if (!paths.Contains(p)) paths.Add(p);
            }
        return all ? string.Join(",", paths) : paths.FirstOrDefault() ?? "";
    }
}
