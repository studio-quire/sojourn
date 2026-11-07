using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Render.World;

namespace Sojourn.Access;

/// <summary>
/// Building with her own hands (docs/access.md > M6; a 2009 editor design for blind builders):
/// the Build area, Control+B (the official viewer's Build key), on one prim at a time. Its command box takes the official
/// editor's tab names and settings as typed words ("object", "hollow 50", "face top", "fullbright on", "new box 2 metres
/// ahead", "undo"), so any setting is reachable without walking a menu; its settings list browses a tab, each setting a
/// row (Enter changes it, Right and Left step it); its faces list names faces by direction and picks the one Texture's
/// rows are for, or all sides. What only a viewer that knows where surfaces are can add: a new prim set down on what's
/// under the spot, and where a prim is said with what it rests on and touches, from the collision model and the prims'
/// boxes, no picture needed; faces come from the prim's own volume (BuildShapes), so they're named on a PC without the
/// renderer too. Every change goes through TesselBuild's ops (so undo covers it and the person's confirmation holds for
/// permissions, a sale, temporary and deleting), and is said back once the region shows it.
/// </summary>
public sealed partial class MainForm
{
    TabPage _buildPage = null!;
    LineBox _buildCommand = null!;
    ListView _buildRows = null!, _buildFaces = null!;
    ComboBox _buildTab = null!;
    UUID _buildId;
    int? _buildFace;
    Control? _beforeBuild;
    TabPage? _beforeBuildPage;
    List<FaceShape>? _buildShapes;
    UUID _buildShapesOf;
    string _buildShapesKey = "";
    string? _buildWhere;
    UUID _buildSelected;
    bool _buildIntroduced;
    readonly List<string> _buildHistory = new();
    int _buildHistoryAt;
    Dictionary<UUID, string>? _textureNames;
    long _textureNamesAt;

    sealed record ContentItem(UUID Id, string Name, string Type, bool? Running);

    string BuildTabName => BuildRows.Tabs[Math.Max(0, _buildTab.SelectedIndex)];

    void BuildBuild()
    {
        _buildPage = new TabPage("Build") { Name = "buildPage", AccessibleName = "Build" };
        _buildCommand = new LineBox { Name = "buildCommand", AccessibleName = "Build command", Dock = DockStyle.Fill };
        _buildRows = List("buildRows", "Settings");
        _buildFaces = List("buildFaces", "Faces");
        _buildTab = new ComboBox { Name = "buildTab", AccessibleName = "Tab", DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        _buildTab.Items.AddRange(BuildRows.Tabs);
        _buildTab.SelectedIndex = 1;   // Object: where, size, shape
        _buildTab.SelectedIndexChanged += (_, _) => _ = ShowBuildRowsAsync();

        _buildCommand.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Control && !e.Alt)
            {
                e.Handled = e.SuppressKeyPress = true;
                var text = _buildCommand.Text.Trim();
                if (text.Length == 0) { Answer("Type a command: help lists them."); return; }
                _buildCommand.Clear();
                _buildHistory.Remove(text);
                _buildHistory.Add(text);
                _buildHistoryAt = _buildHistory.Count;
                _ = BuildCommandAsync(text);
            }
            else if (e.Control && e.KeyCode is Keys.Up or Keys.Down && _buildHistory.Count > 0)
            {
                e.Handled = e.SuppressKeyPress = true;
                _buildHistoryAt = Math.Clamp(_buildHistoryAt + (e.KeyCode == Keys.Up ? -1 : 1), 0, _buildHistory.Count);
                _buildCommand.Text = _buildHistoryAt < _buildHistory.Count ? _buildHistory[_buildHistoryAt] : "";
                _buildCommand.SelectionStart = _buildCommand.Text.Length;
            }
        };
        _buildRows.KeyDown += (_, e) => BuildRowKey(e);
        _buildFaces.SelectedIndexChanged += (_, _) =>
        {
            if (_buildFaces.FocusedItem is not { } it || !_buildFaces.Focused) return;
            var face = it.Tag as int?;
            if (face == _buildFace) return;
            _buildFace = face;
            if (BuildTabName == "Texture") _ = ShowBuildRowsAsync();
        };
        _buildFaces.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = e.SuppressKeyPress = true;
                _buildFace = _buildFaces.FocusedItem?.Tag as int?;
                if (_buildTab.SelectedIndex != 3) _buildTab.SelectedIndex = 3; else _ = ShowBuildRowsAsync();
                _buildRows.Focus();
            }
            else if (e.KeyCode == Keys.F5) { e.Handled = true; _ = ShowBuildFacesAsync(said: true); }
        };

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        t.Controls.Add(new Label { Text = "Command", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        t.Controls.Add(_buildCommand, 1, 0);
        t.Controls.Add(_buildRows, 0, 1);
        t.SetColumnSpan(_buildRows, 2);
        t.Controls.Add(new Label { Text = "Tab", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        t.Controls.Add(_buildTab, 1, 2);
        t.Controls.Add(_buildFaces, 0, 3);
        t.SetColumnSpan(_buildFaces, 2);
        _buildCommand.TabIndex = 0; _buildRows.TabIndex = 1; _buildTab.TabIndex = 2; _buildFaces.TabIndex = 3;
        _buildPage.Controls.Add(t);
        Fill(_buildRows, new(), "Nothing to edit yet: type new box, or choose something in Objects (Alt+7) and press Control+B.");
        Fill(_buildFaces, new(), "No faces: nothing to edit yet.");
        _areas.SelectedIndexChanged += (_, _) => _ = BuildSelectAsync();
    }

    // --- coming and going -------------------------------------------------------------------------------------------

    /// <summary>Control+B: the editor, on what's chosen where the focus is (an object, a part, a face), else what was
    /// edited last; the focus in its command box.</summary>
    void GoBuild()
    {
        if (!_buildPage.ContainsFocus)
        {
            _beforeBuild = ActiveControl;
            _beforeBuildPage = _areas.SelectedTab;
        }
        (UUID Id, int? Face)? picked = null;
        if (_objects.Focused && _objects.FocusedItem?.Tag is PersonOps.Thing { Hud: false } t) picked = (t.Id, null);
        else if (_parts.Focused && _parts.FocusedItem?.Tag is Linkset.Part { Hud: false } p) picked = (p.Id, null);
        else if (_faces.Focused && _faces.FocusedItem?.Tag is FaceItem { Part.Hud: false } f) picked = (f.Part.Id, f.Face.Face);
        bool fresh = picked is { } pk && pk.Id != _buildId;
        if (picked is { } pick) SetBuildTarget(pick.Id, pick.Face);
        GoArea(_buildPage);
        _ = GuardedAsync(() => BuildArrivedAsync(fresh), "the build editor");
    }

    void EditThing(UUID id, int? face = null)
    {
        if (!_buildPage.ContainsFocus) { _beforeBuild = ActiveControl; _beforeBuildPage = _areas.SelectedTab; }
        bool fresh = id != _buildId;
        SetBuildTarget(id, face);
        GoArea(_buildPage);
        _ = GuardedAsync(() => BuildArrivedAsync(fresh), "the build editor");
    }

    async Task BuildArrivedAsync(bool fresh)
    {
        var intro = _buildIntroduced ? "" : " Type a tab's name (general, object, features, texture, content), or a setting and its value; help lists the commands; Tab for the settings.";
        _buildIntroduced = true;
        if (BuildPrim() == null)
        {
            Answer("Build. Nothing to edit yet: new box makes one, or choose something in Objects (Alt+7) and press Control+B." + intro);
            return;
        }
        if (fresh || _buildWhere == null) await BuildWhereAsync();
        await ShowBuildFacesAsync();
        await ShowBuildRowsAsync();
        Answer(BuildSummary() + intro);
    }

    /// <summary>Escape in the editor: back where Control+B came from.</summary>
    void BuildBack()
    {
        var page = _beforeBuildPage is { } p && p != _buildPage ? p : _chatPage;
        _areas.SelectedTab = page;
        var back = _beforeBuild is { IsDisposed: false, CanFocus: true } b && page.Contains(b) ? b : FirstControl(page);
        back?.Focus();
        FocusChanged();
    }

    /// <summary>The prim being edited is selected while the editor shows (as the viewer's edit tool selects it: the
    /// region then sends its properties as they change), and let go of when the person leaves the editor.</summary>
    async Task BuildSelectAsync()
    {
        if (Host?.Session.Online != true) return;
        var want = _areas.SelectedTab == _buildPage && BuildPrim() != null ? _buildId : UUID.Zero;
        if (want == _buildSelected) return;
        var was = _buildSelected;
        _buildSelected = want;
        if (was != UUID.Zero) await _ops.ReadAsync("TesselObjects", "deselect", new JsonObject { ["ids"] = new JsonArray(was.ToString()) });
        if (want != UUID.Zero) await _ops.ReadAsync("TesselObjects", "select", new JsonObject { ["ids"] = new JsonArray(want.ToString()), ["add"] = true });
    }

    void SetBuildTarget(UUID id, int? face = null)
    {
        if (id != _buildId) { _buildWhere = null; _buildShapes = null; _buildShapesOf = UUID.Zero; }
        _buildId = id;
        _buildFace = face;
        _ = BuildSelectAsync();
    }

    // --- what's being edited ----------------------------------------------------------------------------------------

    Primitive? BuildPrim() => _buildId != UUID.Zero && Host?.Session.Region is { } w ? w.PrimById(_buildId) : null;

    /// <summary>The prim as the editor reads it, or null when it's gone (or nothing is chosen).</summary>
    PrimLook? BuildLook()
    {
        if (Host?.Session is not { } s || s.Region is not { } w || BuildPrim() is not { } p) return null;
        var root = s.Objects.RootOf(w, p);
        var set = s.Objects.Linkset(w, root);
        var ordered = new List<Primitive> { root };
        ordered.AddRange(set.Where(x => x.LocalID != root.LocalID).OrderBy(x => x.LocalID));
        int number = Math.Max(1, ordered.FindIndex(x => x.LocalID == p.LocalID) + 1);
        var pos = w.RegionPosition(p);
        var rot = w.RegionRotation(p);
        var shapes = BuildFaceShapes(p, pos, rot);
        int count = shapes?.Count ?? Math.Max(1, s.Materials.FaceCount(p));
        var eye = Eye();
        string FaceName(int i) => shapes?.FirstOrDefault(f => f.Face == i) is { Area: > 0 } fs
            ? LowerFirst(Linkset.FaceWords(fs, eye)) : $"face {i}";
        string rootName = root.Properties?.Name is { Length: > 0 } rn ? rn : "the object";
        string own = p.Properties?.Name ?? "";
        string name = number == 1 ? (own.Length > 0 ? own : "the object")
            : Linkset.Unnamed(own, rootName) ? $"part {number} of {rootName}" : $"{own}, part {number} of {rootName}";
        return new PrimLook(p, s.State.PrimFlagsOf(p), number == 1, number, ordered.Count, name, rootName, pos, rot, count, FaceName,
            id => s.Names.Complete(id, "someone not known yet"),
            id => s.State.Groups.TryGetValue(id, out var g) ? g.Name : "a group you're not in",
            TextureName) { Where = _buildWhere };
    }

    static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>The prim's faces by direction, from its own volume (no picture needed), or the renderer's for a mesh once
    /// it has drawn it; kept while the prim's shape, size and turn stay the same.</summary>
    List<FaceShape>? BuildFaceShapes(Primitive p, LibreMetaverse.Vector3 pos, LibreMetaverse.Quaternion rot)
    {
        var key = $"{pos}{rot}{p.Scale}{p.PrimData.profileCurve}{p.PrimData.PathCurve}{p.PrimData.ProfileBegin}{p.PrimData.ProfileEnd}{p.PrimData.PathBegin}"
                  + $"{p.PrimData.PathEnd}{p.PrimData.ProfileHollow}{p.PrimData.PathTwist}{p.PrimData.PathScaleX}{p.PrimData.PathScaleY}";
        if (_buildShapesOf == p.ID && _buildShapesKey == key && _buildShapes != null) return _buildShapes;
        var shapes = BuildShapes.Faces(p, new(pos.X, pos.Y, pos.Z), new(rot.X, rot.Y, rot.Z, rot.W));
        if (shapes == null && _view != null && !ViewWords.Gone(_view))
        {
            // a mesh: the renderer's faces, if it has drawn it (asked in the background; known next time)
            var id = p.ID;
            _ = _view.FacesAsync(id).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result is { } set && id == _buildId) BeginInvoke(() =>
                {
                    _buildShapes = set.Faces.ToList();
                    _buildShapesOf = id;
                });
            });
        }
        if (shapes != null) { _buildShapes = shapes; _buildShapesOf = p.ID; _buildShapesKey = key; }
        return shapes ?? (_buildShapesOf == p.ID ? _buildShapes : null);
    }

    /// <summary>A texture's name from the person's inventory, by its picture's id (looked for once a minute at most).</summary>
    string? TextureName(UUID asset)
    {
        if (_textureNames == null || Environment.TickCount64 - _textureNamesAt > 60_000)
        {
            _textureNamesAt = Environment.TickCount64;
            var names = new Dictionary<UUID, string>();
            if (Store is { RootFolder: { } root } store)
                try
                {
                    Walk(store, root.UUID, "", UUID.Zero, CancellationToken.None, (n, _) =>
                    {
                        if (n is InventoryItem { AssetType: AssetType.Texture } it && it.AssetUUID != UUID.Zero) names.TryAdd(it.AssetUUID, it.Name);
                        return true;
                    });
                }
                catch (Exception) { /* a name is a nicety */ }
            _textureNames = names;
        }
        return _textureNames.TryGetValue(asset, out var name) ? name : null;
    }

    /// <summary>A texture typed by name: plywood, blank, transparent, an id, or a texture in the inventory (its own name
    /// first, then one with the words in it).</summary>
    (string? Id, string? Error) TextureFor(string words)
    {
        var w = words.Trim();
        if (w.ToLowerInvariant() is "plywood" or "blank" or "transparent" or "default") return (w.ToLowerInvariant(), null);
        if (UUID.TryParse(w, out _)) return (w, null);
        if (Store is not { RootFolder: { } root } store) return (null, "The inventory isn't loaded yet.");
        var found = new List<InventoryItem>();
        Walk(store, root.UUID, "", Host?.Session.Inventory.FolderFor(FolderType.Trash) ?? UUID.Zero, CancellationToken.None, (n, _) =>
        {
            if (n is InventoryItem { AssetType: AssetType.Texture } it && it.Name.Contains(w, StringComparison.OrdinalIgnoreCase)) found.Add(it);
            return found.Count < 50;
        });
        var hit = found.FirstOrDefault(i => i.Name.Equals(w, StringComparison.OrdinalIgnoreCase)) ?? found.FirstOrDefault();
        return hit == null ? (null, $"No texture called {w} in your inventory. Plywood, blank and transparent are always there.") : (hit.UUID.ToString(), null);
    }

    /// <summary>"Editing Table top, part 2 of Table: a box 1.2 by 0.8 by 0.05 metres; 2 metres, one o'clock; resting on
    /// Leg."</summary>
    string BuildSummary()
    {
        if (BuildLook() is not { } l) return "Nothing to edit: it isn't here any more.";
        var size = $"{BuildRows.N(l.P.Scale.X)} by {BuildRows.N(l.P.Scale.Y)} by {BuildRows.N(l.P.Scale.Z)} metres";
        var whole = l.Count == 1 ? "" : l.IsRoot ? $", the root of {l.Count} parts (moving it moves them all)" : "";
        var rights = (l.Flags & PrimFlags.ObjectModify) != 0 ? "" : (l.Flags & PrimFlags.ObjectMove) != 0 ? " You may move it, not change it." : " It isn't yours to change.";
        return $"Editing {l.Name}{whole}: {ShapeWords(l)} {size}; {l.Where ?? _ops.Where(l.Pos) ?? "here"}.{rights}";
    }

    static string ShapeWords(PrimLook l) => l.Shape switch
    {
        "mesh" => "a mesh part", "sculpt" => "a sculpted prim", "half-circle" => "a half sphere", "other" => "a prim",
        var s => (s[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "an " : "a ") + s,
    };

    // --- where it is, what it rests on and touches ------------------------------------------------------------------

    /// <summary>Where the prim is from the person, and what's under it and against it: "2 metres, one o'clock; resting
    /// on Floor; touching Wall". Under it from the collision model's rays down from its underside (its own shape below
    /// counts, a table's top under a cup); against it by the prims' boxes (exact for plain boxes, close for round or cut
    /// ones, said so). Nothing here needs a picture.</summary>
    async Task BuildWhereAsync()
    {
        _buildWhere = await WhereWordsAsync();
    }

    async Task<string?> WhereWordsAsync()
    {
        if (Host?.Session is not { } s || s.Region is not { } w || BuildPrim() is not { } p) return null;
        var pos = w.RegionPosition(p);
        var q = w.RegionRotation(p);
        var box = new BuildShapes.Box(new(pos.X, pos.Y, pos.Z), new(q.X, q.Y, q.Z, q.W), new(p.Scale.X / 2, p.Scale.Y / 2, p.Scale.Z / 2));
        var parts = new List<string>();
        if (_ops.Where(pos) is { } from) parts.Add(from);
        float bottom = box.Bottom;
        // under it: rays straight down from the middle of its underside and near its corners
        (float Gap, string What)? under = null;
        if (_rays != null)
        {
            var origins = new List<System.Numerics.Vector3> { new(box.Centre.X, box.Centre.Y, bottom + 0.03f) };
            foreach (var sx in new[] { -1, 1 })
                foreach (var sy in new[] { -1, 1 })
                {
                    var corner = box.Centre + box.Axis(0) * (sx * box.Half.X * 0.8f) + box.Axis(1) * (sy * box.Half.Y * 0.8f);
                    origins.Add(new(corner.X, corner.Y, bottom + 0.03f));
                }
            var rays = origins.Select(o => (o, -System.Numerics.Vector3.UnitZ, 64f)).ToList();
            if (await _rays.CollideManyAsync(rays, new HashSet<uint> { p.LocalID }) is { } batch)
            {
                var hits = batch.Hits.Where(h => h != null).Select(h => h!.Value).ToList();
                if (hits.Count > 0)
                {
                    var near = hits.MinBy(h => h.Distance);
                    under = (near.Distance - 0.03f, near.LocalId == 0 ? "the ground" : await PrimNameAsync(near.LocalId, p));
                }
            }
        }
        // the ground there (the collision model may not have the terrain, or nothing was under it)
        var g = await _ops.ReadAsync("TesselAgent", "ground", new JsonObject { ["points"] = new JsonArray(new JsonArray((double)pos.X, (double)pos.Y)) });
        double? ground = (g["heights"] as JsonArray)?.FirstOrDefault() is JsonValue hv && hv.TryGetValue<double>(out var gz) ? gz : null;
        if (under == null && ground is { } gd) under = ((float)(bottom - gd), "the ground");
        if (under is { } u)
        {
            if (u.Gap <= 0.03f && u.Gap >= -0.03f) parts.Add($"resting on {u.What}");
            else if (u.Gap > 0) parts.Add($"{Len(u.Gap)} above {u.What}");
        }
        if (ground is { } gr && bottom < gr - 0.03) parts.Add($"sunk {Len(gr - bottom)} into the ground");
        // against it: other prims' boxes (not avatars or anything worn)
        var me = box;
        var near2 = w.Prims.Values.Where(x => x.LocalID != p.LocalID && x.PrimData.PCode == PCode.Prim && !s.Objects.IsAttachment(w, x, out _, out _))
            .Select(x =>
            {
                var xp = w.RegionPosition(x);
                var xq = w.RegionRotation(x);
                return (Prim: x, Box: new BuildShapes.Box(new(xp.X, xp.Y, xp.Z), new(xq.X, xq.Y, xq.Z, xq.W), new(x.Scale.X / 2, x.Scale.Y / 2, x.Scale.Z / 2)));
            })
            .Where(x => System.Numerics.Vector3.Distance(x.Box.Centre, me.Centre) < x.Box.Radius + me.Radius + 0.05f)
            .Select(x => (x.Prim, Gap: BuildShapes.Gap(me, x.Box)))
            .Where(x => x.Gap <= 0.01f).OrderBy(x => x.Gap).Take(6).ToList();
        if (near2.Count > 0)
        {
            var named = new List<string>();
            var into = new List<string>();
            bool rough = !BuildShapes.IsPlainBox(p);
            foreach (var (x, gap) in near2)
            {
                var n = await PrimNameAsync(x.LocalID, p);
                if (under is { } uu && uu.What == n && gap > -0.03f) continue;   // (said as what it rests on)
                rough |= !BuildShapes.IsPlainBox(x);
                if (!named.Contains(n)) named.Add(n);
                if (gap < -0.01f) into.Add($"{Len(-gap)} into {n}");
            }
            if (named.Count > 0) parts.Add((rough ? "close against " : "touching ") + Join(named) + (rough ? " (by the outer boxes)" : ""));
            parts.AddRange(into.Distinct());
        }
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>"Leg, part 2 of Table" (named parts by name), "Table", or "another part of this object".</summary>
    async Task<string> PrimNameAsync(uint local, Primitive editing)
    {
        if (Host?.Session is not { } s || s.Region is not { } w || !w.Prims.TryGetValue(local, out var x)) return "something";
        var root = s.Objects.RootOf(w, x);
        if (x.Properties == null || root.Properties == null)
            try { await s.Objects.FetchAsync(w, new[] { x, root }.Distinct().ToList(), TimeSpan.FromSeconds(1.5), CancellationToken.None); } catch (Exception) { }
        string rootName = root.Properties?.Name is { Length: > 0 } rn ? rn : "an object";
        string own = x.Properties?.Name ?? "";
        bool same = s.Objects.RootOf(w, editing).LocalID == root.LocalID;
        if (x.LocalID == root.LocalID) return same ? "the root of this object" : rootName;
        if (!Linkset.Unnamed(own, rootName)) return same ? own : $"{own}, part of {rootName}";
        return same ? "another part of this object" : rootName;
    }

    static string Len(double m) => m < 1
        ? string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, Math.Round(m * 100))} centimetres")
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Round(m, 1)} metres");

    // --- the lists ---------------------------------------------------------------------------------------------------

    /// <summary>The settings list for the tab chosen; rows that are still the same rows keep their place (only their words
    /// change), so a change while the person is in it doesn't move them.</summary>
    async Task ShowBuildRowsAsync(bool said = false)
    {
        var tab = BuildTabName;
        if (BuildLook() is not { } l)
        {
            _buildRows.AccessibleName = $"{tab} settings";
            Fill(_buildRows, new(), "Nothing to edit yet: type new box, or choose something in Objects (Alt+7) and press Control+B.");
            return;
        }
        List<(string Text, string Key, object Tag)> items;
        if (tab == "Content")
        {
            var contents = await ContentsAsync();
            items = contents == null ? new() : contents.Select(c => (ContentWords(c), "item:" + c.Id, (object)c)).ToList();
            _buildRows.AccessibleName = $"Contents of {l.Name}";
            RowsInto(_buildRows, items, contents == null ? "Its contents couldn't be read." : "Nothing inside. add NAME puts an item from your inventory in.");
        }
        else
        {
            var face = tab == "Texture" ? _buildFace : null;
            items = BuildRows.For(l, tab, face).Select(r => (r.Words, r.Key, (object)r)).ToList();
            _buildRows.AccessibleName = tab == "Texture" ? $"Texture settings of {l.Name}, {(face is { } f ? l.FaceName(f) : "all sides")}" : $"{tab} settings of {l.Name}";
            RowsInto(_buildRows, items, "None.");
        }
        if (said) Answer(items.Count == 0 ? "None." : $"{items.Count} settings.");
    }

    static void RowsInto(ListView list, List<(string Text, string Key, object Tag)> items, string empty)
    {
        if (items.Count > 0 && list.Items.Count == items.Count && items.Select((it, i) => list.Items[i].Name == it.Key).All(x => x))
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (list.Items[i].Text != items[i].Text) list.Items[i].Text = items[i].Text;
                list.Items[i].Tag = items[i].Tag;
            }
            return;
        }
        Fill(list, items, empty);
    }

    async Task ShowBuildFacesAsync(bool said = false)
    {
        if (BuildLook() is not { } l) { Fill(_buildFaces, new(), "No faces: nothing to edit yet."); return; }
        await Task.Yield();
        var items = new List<(string Text, string Key, object Tag)> { ($"All sides ({(l.Faces == 1 ? "1 face" : $"{l.Faces} faces")})", "all", null!) };
        var shapes = _buildShapesOf == l.P.ID ? _buildShapes : null;
        for (int i = 0; i < l.Faces; i++)
        {
            var words = shapes?.FirstOrDefault(f => f.Face == i) is { Area: > 0 } fs ? Linkset.FaceWords(fs, Eye()) : $"Face {i}";
            items.Add((words, i.ToString(CultureInfo.InvariantCulture), i));
        }
        if (shapes == null && l.Faces > 1) items[0] = (items[0].Text + "; their directions aren't known without the 3D view (a mesh), so by number", "all", null!);
        _buildFaces.AccessibleName = $"Faces of {l.Name}";
        Fill(_buildFaces, items, "No faces.");
        var want = _buildFace is { } bf ? bf.ToString(CultureInfo.InvariantCulture) : "all";
        if (_buildFaces.Items[want] is { } sel && !_buildFaces.Focused) { sel.Selected = true; ReaderList.MakeCurrent(sel); }
        if (said) Answer(l.Faces == 1 ? "1 face." : $"{l.Faces} faces.");
    }

    // --- the settings list's keys ------------------------------------------------------------------------------------

    void BuildRowKey(KeyEventArgs e)
    {
        var item = _buildRows.FocusedItem;
        if (e.KeyCode == Keys.F5) { e.Handled = true; _ = RefreshBuildAsync(said: true); return; }
        if (item?.Tag is ContentItem c)
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; _ = GuardedAsync(() => ContentEnterAsync(c), c.Name); }
            else if (e.KeyCode == Keys.Delete) { e.Handled = e.SuppressKeyPress = true; _ = GuardedAsync(() => RemoveContentAsync(c), c.Name); }
            return;
        }
        if (item?.Tag is not BuildRow r || e.Control || e.Alt) return;
        switch (e.KeyCode)
        {
            case Keys.Enter: e.Handled = e.SuppressKeyPress = true; _ = GuardedAsync(() => EnterRowAsync(r), r.Label); break;
            case Keys.Space or Keys.Right: e.Handled = e.SuppressKeyPress = true; _ = GuardedAsync(() => StepRowAsync(r, e.Shift ? -1 : 1), r.Label); break;
            case Keys.Left: e.Handled = e.SuppressKeyPress = true; _ = GuardedAsync(() => StepRowAsync(r, -1), r.Label); break;
        }
    }

    async Task RefreshBuildAsync(bool said)
    {
        await BuildWhereAsync();
        await ShowBuildFacesAsync();
        await ShowBuildRowsAsync(said);
    }

    /// <summary>Enter on a setting: a switch turned, a choice picked from its numbered list, anything else typed (the box
    /// opening with the value as it is).</summary>
    async Task EnterRowAsync(BuildRow r)
    {
        switch (r.Kind)
        {
            case RowKind.Info:
                if (r.Key == "where") { await BuildWhereAsync(); await ShowBuildRowsAsync(); Answer($"Where: {_buildWhere ?? "not known"}."); }
                else if (r.Key == "face") { _buildFaces.Focus(); }
                else Answer(r.Words + ".");
                return;
            case RowKind.Switch:
                await ApplyBuildAsync(r.Key, r.Value > 0 ? "off" : "on");
                return;
            case RowKind.Choice:
                ChoiceMenu(r);
                return;
        }
        var typed = Dialogs.Ask(this, r.Label, r.Ask, r.Now);
        if (typed == null) { Reader?.Answer("Not changed.", keep: false); return; }
        await ApplyBuildAsync(r.Key, typed);
    }

    /// <summary>A choice's numbered list (the 2009 design's numbered choices: shininess 0-3, bumpiness 0-17), the one it has
    /// checked; Enter takes one.</summary>
    void ChoiceMenu(BuildRow r)
    {
        var menu = new ContextMenuStrip { Name = "choiceMenu", AccessibleName = r.Label };
        for (int i = 0; i < r.Choices!.Length; i++)
        {
            var name = r.Choices[i];
            menu.Items.Add(new ToolStripMenuItem($"{i + r.First} {name}", null, (_, _) => _ = GuardedAsync(() => ApplyBuildAsync(r.Key, name), r.Label)) { Checked = i == r.Chosen });
        }
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        var at = _buildRows.FocusedItem?.Bounds ?? default;
        menu.Show(_buildRows, new Point(at.Left + 10, at.Bottom));
        if (r.Chosen >= 0 && r.Chosen < menu.Items.Count) menu.Items[r.Chosen].Select();
    }

    /// <summary>Right and Left: a number up or down by its step, a choice to the next or the one before, a switch turned.</summary>
    async Task StepRowAsync(BuildRow r, int dir)
    {
        switch (r.Kind)
        {
            case RowKind.Switch: await ApplyBuildAsync(r.Key, r.Value > 0 ? "off" : "on"); return;
            case RowKind.Choice when r.Choices is { Length: > 0 } ch:
                int i = r.Chosen < 0 ? 0 : ((r.Chosen + dir) % ch.Length + ch.Length) % ch.Length;
                await ApplyBuildAsync(r.Key, ch[i]);
                return;
            case RowKind.Number when r.Step > 0:
                var v = Math.Round((r.Value + dir * r.Step) / r.Step) * r.Step;
                await ApplyBuildAsync(r.Key, BuildRows.N(v, 4));
                return;
            default:
                Answer($"{r.Label}: Enter to type it.");
                return;
        }
    }

    // --- a change ----------------------------------------------------------------------------------------------------

    static readonly string[] Gated = { "next_perms", "everyone_perms", "group_perms", "sale" };

    /// <summary>A setting changed (a row or a typed command): through TesselBuild modify, the person's confirmation first
    /// for permissions, a sale or temporary; then said back as the region shows it.</summary>
    async Task ApplyBuildAsync(string key, string value)
    {
        if (BuildLook() is not { } l) { Answer(NothingToEdit); return; }
        var face = BuildRows.TabOf(key) == "Texture" ? _buildFace : null;
        var (args, error) = BuildRows.Change(l, key, value, face, TextureFor);
        if (args == null) { Answer(error ?? "Not changed."); return; }
        var before = BuildRows.Row(l, key, face)?.Words;
        if (!await ModifyAsync(args)) return;
        var after = await EchoAsync(key, face, before);
        bool placed = key.StartsWith("pos.") || key.StartsWith("size.") || key.StartsWith("rot.") || key is "type" or "cut" or "slice" or "hollow" or "taper";
        if (placed) await BuildWhereAsync();
        await ShowBuildRowsAsync();
        if (key.StartsWith("size.") || key.StartsWith("rot.") || key == "type") await ShowBuildFacesAsync();
        Answer(after == null ? $"Sent; Second Life hasn't shown the change yet. F5 reads the settings again."
            : $"{after}.{(placed && _buildWhere is { } wh ? $" {Cap(wh)}." : "")}");
    }

    const string NothingToEdit = "Nothing to edit: new box makes one, or choose something in Objects (Alt+7) and press Control+B.";

    /// <summary>TesselBuild modify on the prim being edited, each set of keys in turn; a refusal said in the person's
    /// terms. False when it didn't go (refused, or cancelled at the confirmation).</summary>
    async Task<bool> ModifyAsync(List<JsonObject> sets)
    {
        foreach (var a in sets)
        {
            a["id"] = _buildId.ToString();
            bool gate = a.Select(kv => kv.Key).Any(Gated.Contains) || a["temp"] is JsonValue tv && tv.TryGetValue<bool>(out var temp) && temp;
            var r = gate ? await ConfirmedAsync("TesselBuild", "modify", a) : await _ops.DoAsync("TesselBuild", "modify", a);
            if (r == null) return false;   // ("Cancelled." was said)
            if (PersonOps.Error(r) is { } e) { Answer($"Not changed: {Theirs(e)}"); return false; }
        }
        return true;
    }

    /// <summary>The op's words are the assistant's ("I don't have modify rights"): said as the person's.</summary>
    static string Theirs(string e) => e.Replace("I can't", "You can't").Replace("I don't", "you don't").Replace("I'm", "you're").Replace(" my ", " your ");

    /// <summary>The setting's words once the region shows them changed (up to 3 s), or null.</summary>
    async Task<string?> EchoAsync(string key, int? face, string? before)
    {
        if (BuildRows.TabOf(key) == "General" && Host?.Session is { } s && s.Region is { } w && BuildPrim() is { } p)
            try { await s.Objects.RenewAsync(w, new[] { p }, TimeSpan.FromSeconds(3), CancellationToken.None); } catch (Exception) { }
        for (int i = 0; i < 20; i++)
        {
            if (BuildLook() is { } l && BuildRows.Row(l, key, face) is { } r && r.Words != before) return r.Words;
            await Task.Delay(150);
        }
        return null;
    }

    // --- typed commands ----------------------------------------------------------------------------------------------

    // one command a line, to arrow through in a window (help, Build > Build commands): said whole as one answer, the
    // blind tester asked for it six times in her first quarter hour of building (2026-11-03)
    const string BuildHelp = """
        Build commands
        Tabs: general, object, features, texture, content. A tab's name lists its settings
        A setting and a value changes it: name Table top (or rename Table top), hollow 50, phantom on, material glass
        More settings: colour red, transparency 50, shininess 2, bump bricks, fullbright on, light on
        Size 1 1 0.2, position z 25, rotation z 90. A setting's name alone says how it is
        Making one: new box, or new cylinder 2 metres ahead. It's set down on what's there, and you edit it
        Shapes: box, cylinder, prism, sphere, torus, tube, ring, cone, pyramid. type sphere changes the one you're editing; shapes lists them
        Faces: face top, face 2, face north. all sides: every face
        Moving: move a metre to my left, move by 20 cm up, move to 128 64 25
        Turning: turn 90 left, turn 45 around x. Sizing all of it: scale 2
        where: where it is, what it rests on and touches. what: what it is. look: the assistant describes it
        The whole object: duplicate, delete, take
        Joining: link tunnel joins the object called tunnel to this one, which stays the root. link alone lists your objects to pick from. unlink takes a part out again
        Parts: edit NAME, part 2, root, next, previous
        In content: add NAME, remove NAME, copy NAME
        undo, redo. done, or Escape, goes back
        """;

    static readonly string ShapesWords = "Shapes: box, cylinder, prism, sphere, torus, tube, ring, cone and pyramid. "
        + "new and a shape makes one; type and a shape changes the one you're editing.";

    /// <summary>The build commands in a window to arrow through, said as such.</summary>
    void ShowBuildHelp()
    {
        Answer("Build commands, in a window to arrow through. Escape closes it.");
        BeginInvoke(() => ShowText("Build commands", BuildHelp));
    }

    /// <summary>`//build WORDS` from any input: the editor's command, without going there.</summary>
    Task BuildWordsAsync(string words)
    {
        if (words.Trim().Length > 0) return BuildCommandAsync(words);
        GoBuild();
        return Task.CompletedTask;
    }

    /// <summary>A build command, its failure said and logged rather than lost with the task nobody awaits.</summary>
    Task BuildCommandAsync(string text) => GuardedAsync(() => BuildCommandCoreAsync(text), text);

    async Task GuardedAsync(Func<Task> act, string what)
    {
        try { await act(); }
        catch (Exception e)
        {
            Console.Error.WriteLine($"access: build: {what}: {e}");
            Answer($"That didn't work ({e.GetType().Name}: {e.Message}); it's in the log.");
        }
    }

    /// <summary>The words the box takes besides the settings (BuildRows.Words), for a mistyped one.</summary>
    static readonly string[] BuildVerbs =
    {
        "help", "shapes", "new", "edit", "done", "what", "where", "face", "move", "turn", "scale", "duplicate", "delete", "take",
        "link", "unlink", "part", "root", "next", "previous", "add", "remove", "copy", "undo", "redo", "look", "general", "object",
        "features", "texture", "content", "type",
    };

    async Task BuildCommandCoreAsync(string text)
    {
        var t = text.Trim();
        // "rename Table top": the inventory's word for it, as she typed it (2026-11-03)
        if (t.StartsWith("rename ", StringComparison.OrdinalIgnoreCase)) t = "name " + t[7..].Trim();
        var lower = t.ToLowerInvariant();
        var sp = lower.IndexOf(' ');
        var verb = sp < 0 ? lower : lower[..sp];
        var rest = sp < 0 ? "" : t[(sp + 1)..].Trim();
        var tabIndex = Array.FindIndex(BuildRows.Tabs, x => x.Equals(verb, StringComparison.OrdinalIgnoreCase) || x.Equals(verb.TrimEnd('s'), StringComparison.OrdinalIgnoreCase));
        if (tabIndex >= 0 && rest.Length == 0) { await SayTabAsync(tabIndex); return; }
        switch (verb)
        {
            case "help" or "?" or "commands": ShowBuildHelp(); return;
            case "shapes" or "types" or "kinds": Answer(ShapesWords); return;
            case "new" or "make" or "create": await NewPrimAsync(rest); return;
            case "edit" when rest.Length > 0: await EditNamedAsync(rest); return;
            case "edit": GoBuild(); return;
            case "done" or "close" or "exit": BuildBack(); return;
        }
        if (BuildLook() is not { } l) { Answer(NothingToEdit); return; }
        switch (verb)
        {
            case "what" or "status" or "summary": await BuildWhereAsync(); Answer(BuildSummary()); return;
            case "where" or "touching" when rest.Length == 0:
                await BuildWhereAsync();
                await ShowBuildRowsAsync();
                Answer($"{Cap(l.Name)}: {_buildWhere ?? "not known"}.");
                return;
            case "face" or "side": PickFace(rest); return;
            case "all" when rest is "sides" or "faces": PickFace("all"); return;
            case "move" or "put" or "place": await MoveBuildAsync(rest); return;
            case "turn" or "rotate" or "spin": await TurnAsync(rest); return;
            case "scale": await ScaleAsync(rest); return;
            case "size" when BuildRows.Numbers(rest, true) is { Count: 1 or 3 } sz:
                await SetVectorAsync("scale", sz.Count == 1 ? new[] { sz[0], sz[0], sz[0] } : sz.ToArray(), "Size");
                return;
            case "position" or "pos" when BuildRows.Numbers(rest, true) is { Count: 3 } at:
                await SetVectorAsync("pos", at.ToArray(), "Position");
                return;
            case "rotation" when BuildRows.Numbers(rest, false) is { Count: 3 } rot:
                await SetVectorAsync("rot_euler", rot.ToArray(), "Rotation");
                return;
            case "undo" or "redo": await UndoAsync(verb == "redo", rest); return;
            case "delete": await DeleteBuildAsync(l); return;
            case "take": await TakeBuildAsync(l); return;
            case "duplicate": await DuplicateAsync(l, rest); return;
            case "link": await LinkAsync(l, rest); return;
            case "unlink": await UnlinkAsync(l); return;
            case "part" or "root" or "next" or "previous" or "prev": await PartStepAsync(l, verb, rest); return;
            case "add": await AddContentAsync(rest); return;
            case "remove" when rest.Length > 0: await RemoveContentNamedAsync(rest); return;
            case "copy" when rest.Length > 0: await CopyContentAsync(rest); return;
            case "look" or "describe": DescribeBuild(l); return;
        }
        if (BuildRows.Match(t) is not { } m)
        {
            // what she typed back in the box to fix, rather than typed again
            if (_buildCommand.Text.Length == 0) { _buildCommand.Text = text.Trim(); _buildCommand.SelectionStart = _buildCommand.Text.Length; }
            var guess = Typos.Closest(verb, BuildVerbs.Concat(BuildRows.Words));
            Answer($"No build command or setting {verb}.{(guess != null ? $" Did you mean {guess}?" : " help lists them.")} It's back in the box to correct.");
            return;
        }
        if (m.Value.Length == 0)
        {
            var face = BuildRows.TabOf(m.Key) == "Texture" ? _buildFace : null;
            if (m.Key == "where") await BuildWhereAsync();
            Answer((BuildRows.Row(BuildLook() ?? l, m.Key, face)?.Words ?? m.Key) + ".");
            return;
        }
        await ApplyBuildAsync(m.Key, m.Value);
    }

    /// <summary>A tab's name typed: its settings read out, and the list showing it.</summary>
    async Task SayTabAsync(int tab)
    {
        if (_buildTab.SelectedIndex != tab) _buildTab.SelectedIndex = tab;
        if (BuildLook() is not { } l) { Answer(NothingToEdit); return; }
        if (BuildRows.Tabs[tab] == "Object") await BuildWhereAsync();
        if (BuildRows.Tabs[tab] == "Content")
        {
            var c = await ContentsAsync();
            await ShowBuildRowsAsync();
            Answer(c == null ? "Its contents couldn't be read." : c.Count == 0 ? $"Content: nothing inside {l.Name}."
                : $"Content of {l.Name}, {c.Count} items: {string.Join("; ", c.Select(ContentWords))}.");
            return;
        }
        await ShowBuildRowsAsync();
        var face = BuildRows.Tabs[tab] == "Texture" ? _buildFace : null;
        var rows = BuildRows.For(BuildLook() ?? l, BuildRows.Tabs[tab], face);
        Answer($"{BuildRows.Tabs[tab]}: {string.Join("; ", rows.Select(r => r.Words))}.");
    }

    void PickFace(string words)
    {
        if (BuildLook() is not { } l) return;
        var w = words.Trim().ToLowerInvariant();
        if (w is "" ) { Answer($"Face: {(_buildFace is { } f0 ? l.FaceName(f0) : "all sides")}. face top, face 2, or all sides."); return; }
        if (w is "all" or "all sides" or "all faces" or "every face" or "every side")
        {
            _buildFace = null;
            Answer($"All sides ({(l.Faces == 1 ? "1 face" : $"{l.Faces} faces")}).");
        }
        else
        {
            var shapes = _buildShapesOf == l.P.ID ? _buildShapes : null;
            int? hit = null;
            if (int.TryParse(w.Replace("face ", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                hit = n >= 0 && n < l.Faces ? n : null;
            else if (shapes != null)
                hit = (BuildShapes.ByAxis(shapes, w, new(l.Rot.X, l.Rot.Y, l.Rot.Z, l.Rot.W)) ?? Linkset.FindFace(shapes, w, Eye()))?.Face;
            if (hit is not { } face)
            {
                Answer(shapes == null && !int.TryParse(w, out _) ? $"Its faces' directions aren't known (a mesh, without the 3D view): a number from 0 to {l.Faces - 1}."
                    : $"No face {words.Trim()}: top, bottom, front, far, left, right, inside, north, plusx, or a number from 0 to {l.Faces - 1}.");
                return;
            }
            _buildFace = face;
            Answer($"Face: {l.FaceName(face)}.");
        }
        _ = ShowBuildFacesAsync();
        if (BuildTabName == "Texture") _ = ShowBuildRowsAsync();
    }

    async Task SetVectorAsync(string key, double[] v, string label)
    {
        if (key == "scale" && v.Any(x => x < 0.01 || x > 64)) { Answer("Size: each from 0.01 to 64 metres."); return; }
        if (key == "pos" && (v[0] is < 0 or > 256 || v[1] is < 0 or > 256 || v[2] is < 0 or > 4096)) { Answer("Position: inside the region, 0 to 256 east and north."); return; }
        var args = new JsonObject { [key] = new JsonArray(v.Select(x => (JsonNode)x).ToArray()) };
        await ChangedAsync(args, label);
    }

    /// <summary>A placement change (move, turn, size by command) sent, then said back with where it ended up.</summary>
    async Task ChangedAsync(JsonObject args, string label)
    {
        if (BuildLook() is not { } before) return;
        var key = args.ContainsKey("scale") ? "size" : args.ContainsKey("pos") ? "pos" : "rot";
        string Words(PrimLook l) => key switch
        {
            "size" => $"Size: {BuildRows.N(l.P.Scale.X)} by {BuildRows.N(l.P.Scale.Y)} by {BuildRows.N(l.P.Scale.Z)} metres",
            "pos" => $"Position: {BuildRows.N(l.Pos.X, 2)}, {BuildRows.N(l.Pos.Y, 2)}, {BuildRows.N(l.Pos.Z, 2)}",
            _ => $"Rotation: {BuildRows.N(BuildRows.Euler(l.Rot).X, 1)}, {BuildRows.N(BuildRows.Euler(l.Rot).Y, 1)}, {BuildRows.N(BuildRows.Euler(l.Rot).Z, 1)} degrees",
        };
        var was = Words(before);
        if (!await ModifyAsync(new List<JsonObject> { args })) return;
        string? now = null;
        for (int i = 0; i < 20 && now == null; i++)
        {
            await Task.Delay(150);
            if (BuildLook() is { } l && Words(l) != was) now = Words(l);
        }
        await BuildWhereAsync();
        await ShowBuildRowsAsync();
        await ShowBuildFacesAsync();
        Answer(now == null ? $"{label} sent; Second Life hasn't shown the change yet." : $"{now}. {Cap(_buildWhere ?? "")}.");
    }

    /// <summary>move WORDS: where from the person ("a metre to my left", "2 metres ahead"), set down on what's under the
    /// spot; "by 20 cm up" from where it is; "to 128 64 25" in the region.</summary>
    async Task MoveBuildAsync(string words)
    {
        if (Host?.Session is not { } s || s.Region is not { } w || BuildPrim() is not { } p || BuildLook() is not { } l) { Answer(NothingToEdit); return; }
        if (words.StartsWith("to ", StringComparison.OrdinalIgnoreCase) && BuildRows.Numbers(words, true) is { Count: 2 or 3 } at)
        {
            await SetVectorAsync("pos", new[] { at[0], at[1], at.Count == 3 ? at[2] : l.Pos.Z }, "Position");
            return;
        }
        var (spot, error) = PlaceWords.Parse(words);
        if (spot == null) { Answer(error!); return; }
        double x, y, z;
        if (spot.Relative || !spot.Horizontal)
        {
            var (dx, dy) = spot.Offset(_ops.Me()?.Heading ?? 0);
            (x, y, z) = (l.Pos.X + dx, l.Pos.Y + dy, l.Pos.Z + spot.Up);
        }
        else
        {
            if (await SpotAsync(spot, p) is not { } a) { Answer("Not in a region yet."); return; }
            // its own underside on the surface: the whole object's for its root, else this part's box
            double below = l.IsRoot ? await BottomAsync(s.Objects.RootOf(w, p).ID)
                : l.Pos.Z - new BuildShapes.Box(default, new(l.Rot.X, l.Rot.Y, l.Rot.Z, l.Rot.W), new(p.Scale.X / 2, p.Scale.Y / 2, p.Scale.Z / 2)).Bottom;
            (x, y, z) = (a.X, a.Y, a.Z + spot.Up + below);
        }
        await SetVectorAsync("pos", new[] { x, y, z }, "Position");
    }

    /// <summary>turn DEGREES [left | right | around x | y | z]: about the region's vertical by default, left being
    /// counter-clockwise seen from above (Second Life's own sense of positive).</summary>
    async Task TurnAsync(string words)
    {
        if (BuildLook() is not { } l) return;
        var w = words.ToLowerInvariant();
        if (BuildRows.Numbers(w, false) is not { Count: 1 } nums) { Answer("turn 90, turn 45 right, turn 90 around x."); return; }
        double deg = nums[0] * (w.Contains("right") || w.Contains("clockwise") && !w.Contains("counter") && !w.Contains("anti") ? -1 : 1);
        var axis = w.Contains("around x") || w.Contains("about x") ? LibreMetaverse.Vector3.UnitX
            : w.Contains("around y") || w.Contains("about y") ? LibreMetaverse.Vector3.UnitY : LibreMetaverse.Vector3.UnitZ;
        var q = l.Rot * LibreMetaverse.Quaternion.CreateFromAxisAngle(axis, (float)(deg * Math.PI / 180));
        q = LibreMetaverse.Quaternion.Normalize(q);
        await ChangedAsync(new JsonObject { ["rot"] = new JsonArray((double)q.X, (double)q.Y, (double)q.Z, (double)q.W) }, "Turn");
    }

    async Task ScaleAsync(string words)
    {
        if (BuildLook() is not { } l) return;
        if (BuildRows.Numbers(words.Replace("%", " percent"), false) is not { Count: 1 } n || n[0] <= 0) { Answer("scale 2 doubles it; scale 0.5 halves it."); return; }
        double f = words.Contains('%') || words.Contains("percent") ? n[0] / 100 : n[0];
        await SetVectorAsync("scale", new[] { l.P.Scale.X * f, l.P.Scale.Y * f, l.P.Scale.Z * f }, "Size");
    }

    // --- making, copying, linking, deleting -------------------------------------------------------------------------

    static readonly string[] NewShapes = { "box", "cylinder", "prism", "sphere", "torus", "tube", "ring", "cone", "pyramid" };

    /// <summary>new [SHAPE] [WHERE]: a prim half a metre each way, where the words say (2 metres ahead unless said), with
    /// its underside on what's there (a floating rez in Radegast, the smallest first test); then
    /// it's the one being edited.</summary>
    async Task NewPrimAsync(string words)
    {
        var w = words.Trim();
        var shape = "box";
        var first = w.Split(' ', 2)[0].ToLowerInvariant().TrimEnd('s');
        if (first is "a" or "an" && w.Split(' ', 2) is { Length: 2 } wa) { w = wa[1]; first = w.Split(' ', 2)[0].ToLowerInvariant(); }
        if (first == "prim" || NewShapes.Contains(first)) { shape = first == "prim" ? "box" : first; w = w.Split(' ', 2) is { Length: 2 } r ? r[1] : ""; }
        var (spot, error) = PlaceWords.Parse(w.Length == 0 ? "2 metres ahead" : w);
        if (spot == null) { Answer(error!); return; }
        if (spot.Relative) { Answer("Say where from you: new box 2 metres ahead."); return; }
        if (!spot.Horizontal) spot = spot with { Ahead = 2 };
        if (await SpotAsync(spot, null) is not { } at) { Answer("Not in a region yet."); return; }
        var r0 = await _ops.DoAsync("TesselBuild", "create", new JsonObject { ["shape"] = shape, ["pos"] = Xyz(at.X, at.Y, at.Z + spot.Up + 0.5), ["scale"] = Xyz(0.5, 0.5, 0.5) });
        if (PersonOps.Error(r0) is { } e || !UUID.TryParse(r0.Str("id"), out var id)) { Answer($"Couldn't make a {shape}: {Theirs(PersonOps.Error(r0) ?? "no prim came")}"); return; }
        var bottom = await BottomAsync(id);
        await _ops.DoAsync("TesselBuild", "modify", new JsonObject { ["id"] = id.ToString(), ["pos"] = Xyz(at.X, at.Y, at.Z + spot.Up + bottom) });
        await Task.Delay(400);
        SetBuildTarget(id);
        if (_areas.SelectedTab != _buildPage) { _ = BuildSelectAsync(); }
        await BuildWhereAsync();
        await ShowBuildFacesAsync();
        await ShowBuildRowsAsync();
        Answer($"Made a {shape}, half a metre each way, {PlaceWords.Say(spot)}{OnWhat(at)}. Editing it.");
    }

    async Task DuplicateAsync(PrimLook l, string words)
    {
        if (Host?.Session is not { } s || s.Region is not { } w || BuildPrim() is not { } p) return;
        var root = s.Objects.RootOf(w, p);
        double ox, oy, oz = 0;
        if (words.Trim().Length > 0)
        {
            var (spot, error) = PlaceWords.Parse(words.Trim().StartsWith("by ") ? words : "by " + words);
            if (spot == null) { Answer(error!); return; }
            (ox, oy) = spot.Offset(_ops.Me()?.Heading ?? 0);
            oz = spot.Up;
        }
        else
        {
            // beside it to the person's right, a little more than its width
            var reach = new BuildShapes.Box(default, new(l.Rot.X, l.Rot.Y, l.Rot.Z, l.Rot.W), new(p.Scale.X / 2, p.Scale.Y / 2, p.Scale.Z / 2));
            var h = (_ops.Me()?.Heading ?? 0) * Math.PI / 180;
            var right = new System.Numerics.Vector3((float)Math.Cos(h), (float)-Math.Sin(h), 0);
            double step = 2 * reach.Reach(right) + 0.05;
            (ox, oy) = (right.X * step, right.Y * step);
        }
        var r = await _ops.DoAsync("TesselBuild", "duplicate", new JsonObject { ["id"] = root.ID.ToString(), ["offset"] = Xyz(ox, oy, oz) });
        if (PersonOps.Error(r) is { } e || !UUID.TryParse(r.Str("id"), out var id)) { Answer($"Not copied: {Theirs(PersonOps.Error(r) ?? "no copy came")}"); return; }
        SetBuildTarget(id);
        await RefreshBuildAsync(said: false);
        Answer($"Copied {l.RootName}{(l.Count > 1 ? $" (all {l.Count} parts)" : "")}; editing the copy. {Cap(_buildWhere ?? "")}.");
    }

    async Task DeleteBuildAsync(PrimLook l)
    {
        if (!l.IsRoot) { Answer($"That's {l.Name}: deleting takes the whole object. unlink first to delete only this part, or root then delete for the whole object."); return; }
        if (await ConfirmedAsync("TesselBuild", "delete", new JsonObject { ["ids"] = new JsonArray(_buildId.ToString()) }) is not { } r) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not deleted: {Theirs(e)}"); return; }
        Answer($"Deleted {l.RootName} (in the Trash). undo brings it back.");
        SetBuildTarget(UUID.Zero);
        await RefreshBuildAsync(said: false);
    }

    async Task TakeBuildAsync(PrimLook l)
    {
        if (Host?.Session is not { } s || s.Region is not { } w || BuildPrim() is not { } p) return;
        var root = s.Objects.RootOf(w, p);
        var r = await _ops.DoAsync("TesselBuild", "take", new JsonObject { ["ids"] = new JsonArray(root.ID.ToString()) });
        if (PersonOps.Error(r) is { } e) { Answer($"Not taken: {Theirs(e)}"); return; }
        Answer($"Took {l.RootName} into your inventory (Objects).");
        SetBuildTarget(UUID.Zero);
        await RefreshBuildAsync(said: false);
    }

    /// <summary>link NAME: another object of the person's joined to this one, this one's root staying the root.</summary>
    async Task LinkAsync(PrimLook l, string name)
    {
        if (Host?.Session is not { } s || s.Region is not { } w || BuildPrim() is not { } p) return;
        var root = s.Objects.RootOf(w, p);
        var near = (await _ops.ObjectsAsync(30, 60)).Where(t => t.Id != root.ID).ToList();
        name = name.Trim();
        string also = "";
        PersonOps.Thing? other;
        if (name.Length == 0)
        {
            var mine = near.Where(t => t.Mine).ToList();
            if (mine.Count == 0) { Answer("None of your objects are within 30 metres to link."); return; }
            var picked = Dialogs.Pick(this, $"Link to {l.RootName}", mine.Select(t => new Dialogs.Someone(t.Id, t.Name, _ops.Where(t.Pos) ?? "")).ToList(),
                _ => Task.FromResult<Dialogs.Someone?>(null), "Your objects within 30 metres");
            if (picked == null) return;
            other = mine.First(t => t.Id == picked.Id);
        }
        else
        {
            var called = near.Where(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
            var exact = called.Where(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0) called = exact;
            if (called.Count == 0) { Answer($"Nothing called {name} within 30 metres to link."); return; }
            var mine = called.Where(t => t.Mine).ToList();
            if (mine.Count == 0) { Answer($"{called[0].Name} isn't yours, so it can't be linked."); return; }
            other = mine[0];
            if (mine.Count > 1) also = $" That's the nearest of {mine.Count} called {name}, {_ops.Where(other.Pos)}.";
        }
        await LinkToAsync(l, root, other, also);
    }

    /// <summary>Whether a build is open that this thing could be linked to.</summary>
    bool LinkableNow(PersonOps.Thing t) => BuildPrim() is { } p && Host?.Session is { Region: { } w } s && s.Objects.RootOf(w, p).ID != t.Id;

    /// <summary>Objects' "Link to what I'm editing".</summary>
    async Task LinkThingAsync(PersonOps.Thing t)
    {
        if (Host?.Session is not { } s || s.Region is not { } w || BuildPrim() is not { } p || BuildLook() is not { } l) { Answer("Nothing is being edited: Control+B on something first."); return; }
        await LinkToAsync(l, s.Objects.RootOf(w, p), t, "");
    }

    async Task LinkToAsync(PrimLook l, Primitive root, PersonOps.Thing other, string also)
    {
        if (Host?.Session is not { } s || s.Region is not { } w) return;
        var r = await _ops.DoAsync("TesselBuild", "link", new JsonObject { ["ids"] = new JsonArray(root.ID.ToString(), other.Id.ToString()) });
        if (PersonOps.Error(r) is { } e) { Answer($"Not linked: {Theirs(e)}"); return; }
        var newRoot = UUID.TryParse(r.Str("root_id"), out var nr) ? nr : root.ID;
        await RefreshBuildAsync(said: false);
        var count = s.Objects.Linkset(w, w.PrimById(newRoot) ?? root).Count;
        Answer($"Linked {other.Name} to {l.RootName}: {count} parts, {(newRoot == root.ID ? $"{l.RootName}'s root still the root" : $"{other.Name}'s root the root now")}. unlink takes a part out again; undo too.{also}");
    }

    async Task UnlinkAsync(PrimLook l)
    {
        if (l.Count == 1) { Answer($"{Cap(l.Name)} is one prim; nothing to unlink."); return; }
        var r = await _ops.DoAsync("TesselBuild", "unlink", new JsonObject { ["id"] = _buildId.ToString() });
        if (PersonOps.Error(r) is { } e) { Answer($"Not unlinked: {Theirs(e)}"); return; }
        await RefreshBuildAsync(said: false);
        Answer(l.IsRoot ? $"Unlinked all {l.Count} parts of {l.RootName}: each is an object of its own now; still editing the one that was the root."
            : $"Unlinked {l.Name}: it's an object of its own now, still the one being edited.");
    }

    async Task UndoAsync(bool redo, string words)
    {
        int steps = int.TryParse(words.Trim(), out var n) && n > 0 ? Math.Min(n, 20) : 1;
        if (await ConfirmedAsync("SojournBuild", redo ? "redo" : "undo", new JsonObject { ["steps"] = steps }) is not { } r) return;
        if (PersonOps.Error(r) is { } e) { Answer($"{(redo ? "Nothing redone" : "Nothing undone")}: {Theirs(e)}."); return; }
        var done = (r[redo ? "redone" : "undone"] as JsonArray)?.Select(x => x?.ToString()).OfType<string>().Select(UndoWords).ToList() ?? new();
        var failed = (r["failed"] as JsonArray)?.OfType<JsonObject>().Select(f => $"{f.Str("what")}: {f.Str("error")}").ToList() ?? new();
        await Task.Delay(500);
        await RefreshBuildAsync(said: false);
        Answer($"{(redo ? "Redone" : "Undone")}: {(done.Count == 0 ? "nothing" : string.Join("; ", done))}.{(failed.Count > 0 ? " Couldn't: " + string.Join("; ", failed) + "." : "")}"
               + (BuildPrim() == null && _buildId != UUID.Zero ? " What was being edited is gone." : ""));
    }

    /// <summary>An undo step's words with modify's keys as the editor's names ("changed rot_euler on Table": "changed
    /// rotation on Table").</summary>
    static string UndoWords(string what) => System.Text.RegularExpressions.Regex.Replace(what, @"\b(rot_euler|local_rot_euler|local_rot|rot|local_pos|pos|scale|desc|tex_offset|tex_rotation|texgen|color|shiny|bump|phantom|physics|temp|flexi|click_action|next_perms|everyone_perms|group_perms|radius_offset|profile_cut|path_cut|top_shear|hole_size)\b",
        m => m.Value switch
        {
            "rot_euler" or "rot" or "local_rot" or "local_rot_euler" => "rotation", "pos" or "local_pos" => "position", "scale" => "size",
            "desc" => "description", "tex_offset" => "texture offset", "tex_rotation" => "texture rotation", "texgen" => "mapping",
            "color" => "colour", "shiny" => "shininess", "bump" => "bumpiness", "physics" => "physical", "temp" => "temporary",
            "flexi" => "flexible path", "click_action" => "when left-clicked", "next_perms" => "next owner's permissions",
            "everyone_perms" => "anyone's permissions", "group_perms" => "sharing with the group", "radius_offset" => "radius",
            var k => k.Replace('_', ' '),
        });

    async Task EditNamedAsync(string name)
    {
        var hit = (await _ops.ObjectsAsync(30, 60, name)).FirstOrDefault(t => t.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (hit == null) { Answer($"Nothing called {name} within 30 metres."); return; }
        EditThing(hit.Id);
    }

    /// <summary>part N, root, next, previous: another prim of the same object.</summary>
    async Task PartStepAsync(PrimLook l, string verb, string rest)
    {
        if (Host?.Session is not { } s || s.Region is not { } w || BuildPrim() is not { } p) return;
        var root = s.Objects.RootOf(w, p);
        var ordered = new List<Primitive> { root };
        ordered.AddRange(s.Objects.Linkset(w, root).Where(x => x.LocalID != root.LocalID).OrderBy(x => x.LocalID));
        int at = verb switch
        {
            "root" => 1,
            "next" => l.Number % ordered.Count + 1,
            "previous" or "prev" => (l.Number + ordered.Count - 2) % ordered.Count + 1,
            _ => int.TryParse(rest, out var n) ? n : -1,
        };
        if (at < 1 || at > ordered.Count) { Answer($"part 1 to {ordered.Count} ({l.RootName} has {ordered.Count} part{(ordered.Count == 1 ? "" : "s")})."); return; }
        SetBuildTarget(ordered[at - 1].ID);
        if (ordered[at - 1].Properties == null)
            try { await s.Objects.FetchAsync(w, new[] { ordered[at - 1] }, TimeSpan.FromSeconds(2), CancellationToken.None); } catch (Exception) { }
        await RefreshBuildAsync(said: false);
        Answer(BuildSummary());
    }

    void DescribeBuild(PrimLook l) => _ = DescribeAsync(BuildSubject(l));

    Subject BuildSubject(PrimLook l) => new(_buildId.ToString(), "view", l.Name,
        $"Describe {l.Name}, the prim I'm building, as it is now: its shape, size and colours, and how it sits with what's around it");

    /// <summary>Control+D in the editor: the prim being edited.</summary>
    Subject? BuildDescribeSubject() => _buildPage.ContainsFocus && BuildLook() is { } l ? BuildSubject(l) : null;

    // --- content: what's inside the prim ------------------------------------------------------------------------------

    async Task<List<ContentItem>?> ContentsAsync()
    {
        if (_buildId == UUID.Zero) return null;
        var r = await _ops.ReadAsync("TesselBuild", "contents", new JsonObject { ["id"] = _buildId.ToString(), ["running"] = true });
        if (PersonOps.Error(r) != null) return null;
        return (r["items"] as JsonArray ?? new()).OfType<JsonObject>()
            .Select(o => new ContentItem(UUID.TryParse(o.Str("item_id"), out var id) ? id : UUID.Zero, o.Str("name") ?? "item", o.Str("type") ?? "item",
                o["running"] is JsonValue rv && rv.TryGetValue<bool>(out var run) ? run : null))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string ContentWords(ContentItem c) => $"{c.Name}, {c.Type}{(c.Running is { } r ? r ? ", running" : ", not running" : "")}";

    async Task ContentEnterAsync(ContentItem c)
    {
        if (c.Running is not { } running) { Answer($"{ContentWords(c)}. Delete removes it; copy {c.Name} puts a copy in your inventory."); return; }
        var r = await _ops.DoAsync("TesselBuild", "script", new JsonObject { ["id"] = _buildId.ToString(), ["item_id"] = c.Id.ToString(), ["running"] = !running });
        Answer(PersonOps.Error(r) is { } e ? $"Not changed: {Theirs(e)}" : $"{c.Name}: {(!running ? "running" : "not running")}.");
        await ShowBuildRowsAsync();
    }

    async Task RemoveContentAsync(ContentItem c)
    {
        if (await ConfirmedAsync("TesselBuild", "removeFromContents", new JsonObject { ["id"] = _buildId.ToString(), ["item_id"] = c.Id.ToString() }) is not { } r) return;
        Answer(PersonOps.Error(r) is { } e ? $"Not removed: {Theirs(e)}" : $"Removed {c.Name} from it.");
        await ShowBuildRowsAsync();
    }

    async Task RemoveContentNamedAsync(string name)
    {
        var c = (await ContentsAsync())?.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? (await ContentsAsync())?.FirstOrDefault(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (c == null) { Answer($"Nothing called {name} inside it."); return; }
        await RemoveContentAsync(c);
    }

    async Task CopyContentAsync(string name)
    {
        var c = (await ContentsAsync())?.FirstOrDefault(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (c == null) { Answer($"Nothing called {name} inside it."); return; }
        var r = await _ops.DoAsync("TesselBuild", "copyFromContents", new JsonObject { ["id"] = _buildId.ToString(), ["item_id"] = c.Id.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"Not copied: {Theirs(e)}" : $"Copied {c.Name} into your inventory.");
    }

    /// <summary>add NAME: an item from the inventory put inside (a script starts running; a new script waits for the
    /// person's confirmation, as everywhere).</summary>
    async Task AddContentAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) { Answer("add NAME: an item from your inventory, by its name."); return; }
        if (Store is not { RootFolder: { } root } store) { Answer("The inventory isn't loaded yet."); return; }
        var found = new List<InventoryItem>();
        var trash = Host?.Session.Inventory.FolderFor(FolderType.Trash) ?? UUID.Zero;
        await Task.Run(() => Walk(store, root.UUID, "", trash, CancellationToken.None, (n, _) =>
        {
            if (n is InventoryItem it && it.AssetType is not (AssetType.Link or AssetType.LinkFolder) && it.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) found.Add(it);
            return found.Count < 50;
        }));
        var item = found.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? found.FirstOrDefault();
        if (item == null) { Answer($"No item called {name} in your inventory."); return; }
        if (await ConfirmedAsync("TesselBuild", "addToContents", new JsonObject { ["id"] = _buildId.ToString(), ["item_id"] = item.UUID.ToString() }) is not { } r) return;
        Answer(PersonOps.Error(r) is { } e ? $"Not added: {Theirs(e)}" : $"Put {item.Name} inside.");
        if (BuildTabName == "Content") await ShowBuildRowsAsync();
    }
}
