using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Render.World;

namespace Sojourn.Access;

/// <summary>
/// Every part reachable (docs/access.md > M2): in Objects (Alt+7), the object selected above has its parts listed below
/// it ("Parts of Treasure Chest"), each by name and number or, unnamed, by what and where it is (Linkset.cs), and the part
/// selected there has its faces listed below that ("Faces of part 2"), by direction from where the person is. Enter on a
/// part touches that part (not the root), Enter on a face touches that face; the Applications key has the rest. Show
/// switches the objects list between what's nearby and the person's own HUDs, whose buttons are parts like any other.
/// The lists follow the selection above them only when it moves: Tab passing through a list never changes what the next
/// one is about (Radegast's lesson: tabbing past its object list made the root the target again, the guided test of
/// 2026-10-29), and none changes while the person is in it.
/// </summary>
public sealed partial class MainForm
{
    ListView _parts = null!, _faces = null!;
    ComboBox _objectsShow = null!;
    ContextMenuStrip _partMenu = null!, _faceMenu = null!;
    UUID _partsOf, _facesOf;
    int _partsVersion, _facesVersion;
    Sojourn.Session.Offscreen.OffscreenRenderer? _view;
    Sojourn.Session.Collision.LocalCollision? _rays;

    bool ShowingHuds => _objectsShow.SelectedIndex == 1;

    void BuildParts()
    {
        _parts = List("parts", "Parts");
        _faces = List("faces", "Faces");
        _objectsShow = new ComboBox { Name = "objectsShow", AccessibleName = "Show", DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        _objectsShow.Items.AddRange(new object[] { "Things nearby", "Your HUDs" });
        _objectsShow.SelectedIndex = 0;
        _objectsShow.SelectedIndexChanged += (_, _) =>
        {
            _objects.AccessibleName = ShowingHuds ? "Your HUDs" : "Objects nearby";
            _ = RefreshObjects(_objectsShow.Focused);
        };

        _partMenu = new ContextMenuStrip { Name = "partMenu", AccessibleName = "Actions" };
        void PartItem(string text, Action<Linkset.Part> act) =>
            _partMenu.Items.Add(new ToolStripMenuItem(text, null, (_, _) => { if (_parts.FocusedItem?.Tag is Linkset.Part p) act(p); }));
        PartItem("&Touch", p => _ = TouchPartAsync(p, null));
        PartItem("&Sit", p => _ = SitPartAsync(p));
        PartItem("W&here", p => Answer(p.Hud ? $"{PartName(p)} is on your HUD." : $"{PartName(p)} is {_ops.Where(L(p.Pos), full: true)}."));
        PartItem("&Describe", p => _ = DescribeAsync(Of(p)));
        PartItem("&Faces", p => { _faces.Focus(); });
        KeysOn(_partMenu, ("&Touch", "Enter"), ("&Describe", "Control+D"), ("&Faces", "Tab"));
        MenuOn(_parts, _partMenu);
        _parts.KeyDown += (_, e) => PartKey(e);
        _parts.SelectedIndexChanged += (_, _) => { if (_parts.FocusedItem?.Tag is Linkset.Part p && p.Id != _facesOf) _ = RefreshFacesAsync(p); };

        _faceMenu = new ContextMenuStrip { Name = "faceMenu", AccessibleName = "Actions" };
        _faceMenu.Items.Add(new ToolStripMenuItem("&Touch", null, (_, _) => { if (_faces.FocusedItem?.Tag is FaceItem f) _ = TouchPartAsync(f.Part, f.Face); }));
        _faceMenu.Items.Add(new ToolStripMenuItem("W&here", null, (_, _) => { if (_faces.FocusedItem?.Tag is FaceItem f) Answer($"{_faces.FocusedItem.Text}: {_ops.Where(L(f.Face.Point), full: true)}."); }));
        KeysOn(_faceMenu, ("&Touch", "Enter"));
        MenuOn(_faces, _faceMenu);
        _faces.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && _faces.FocusedItem?.Tag is FaceItem f) { e.Handled = e.SuppressKeyPress = true; _ = TouchPartAsync(f.Part, f.Face); }
            else if (e.KeyCode == Keys.F5 && _parts.FocusedItem?.Tag is Linkset.Part p) { e.Handled = true; _ = RefreshFacesAsync(p, said: true); }
        };

        _objects.SelectedIndexChanged += (_, _) =>
        {
            if (_objects.FocusedItem?.Tag is PersonOps.Thing t && t.Id != _partsOf && !_parts.Focused && !_faces.Focused) _ = RefreshPartsAsync(t);
        };

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 25));
        t.Controls.Add(new Label { Text = "Sh&ow", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        t.Controls.Add(_objectsShow, 1, 0);
        t.Controls.Add(_objects, 0, 1);
        t.SetColumnSpan(_objects, 2);
        t.Controls.Add(_parts, 0, 2);
        t.SetColumnSpan(_parts, 2);
        t.Controls.Add(_faces, 0, 3);
        t.SetColumnSpan(_faces, 2);
        _objects.TabIndex = 0; _parts.TabIndex = 1; _faces.TabIndex = 2; _objectsShow.TabIndex = 3;
        _objectsPage.Controls.Add(t);
        Fill(_parts, new(), "No object selected.");
        Fill(_faces, new(), "No part selected.");
    }

    sealed record FaceItem(Linkset.Part Part, FaceShape Face);

    static LibreMetaverse.Vector3 L(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>"Latch", "Treasure Chest" (the root or a lone prim), "part 2 of Treasure Chest".</summary>
    static string PartName(Linkset.Part p) => p.Named ? p.Name : p.Number == 1 ? p.RootName : $"part {p.Number} of {p.RootName}";

    void PartKey(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && _parts.FocusedItem?.Tag is Linkset.Part p) { e.Handled = e.SuppressKeyPress = true; _ = TouchPartAsync(p, null); }
        else if (e.KeyCode == Keys.F5 && _objects.FocusedItem?.Tag is PersonOps.Thing t) { e.Handled = true; _ = RefreshPartsAsync(t, said: true); }
    }

    /// <summary>The selected object's parts (their names asked of the region first, a HUD's by selecting it as the edit
    /// tool does); the part the person was on kept when it's the same object.</summary>
    async Task RefreshPartsAsync(PersonOps.Thing t, bool said = false)
    {
        int version = ++_partsVersion;
        bool same = t.Id == _partsOf;
        _partsOf = t.Id;
        _parts.AccessibleName = $"Parts of {t.Name}";
        if (!same) Fill(_parts, new(), "Finding its parts.");
        var parts = await PartsOfAsync(t.Id, t.Hud);
        if (version != _partsVersion || IsDisposed) return;
        if (parts == null) { Fill(_parts, new(), $"{t.Name} isn't here any more."); return; }
        Fill(_parts, parts.Select(p => (p.Words, p.Id.ToString(), (object)p)).ToList(), "No parts.");
        if (said) Reader?.Answer(parts.Count == 1 ? "1 part." : $"{parts.Count} parts.", keep: false);
        if (_parts.FocusedItem?.Tag is Linkset.Part first && (first.Id != _facesOf || said)) _ = RefreshFacesAsync(first);
    }

    async Task<List<Linkset.Part>?> PartsOfAsync(UUID root, bool hud)
    {
        if (Host?.Session.Region is not { } w || w.PrimById(root) is not { } prim) return null;
        // every part selected briefly, as the edit tool's linked parts are (Radegast's parts list does the same): the
        // region sends each its own properties then. On the local grid neither selecting the root nor asking a child's
        // family properties brought a child's name.
        var views = Host.Session.Objects;
        try { await views.FetchFullAsync(w, views.Linkset(w, prim), TimeSpan.FromSeconds(3), CancellationToken.None); }
        catch (Exception) { /* names are a nicety: the parts are listed either way */ }
        for (int i = 0; i < 15 && views.Linkset(w, prim).Any(p => p.Properties == null); i++) await Task.Delay(100);
        var missing = views.Linkset(w, prim).Where(p => p.Properties == null).ToList();
        if (missing.Count > 0 && !hud)
            try { await views.FetchAsync(w, missing, TimeSpan.FromSeconds(2), CancellationToken.None); } catch (Exception) { }
        if (Host.Session.Region is not { } w2 || w2.PrimById(root) is not { } p2) return null;
        var me = _ops.Me() is { } m ? new System.Numerics.Vector3(m.Pos.X, m.Pos.Y, m.Pos.Z) : (System.Numerics.Vector3?)null;
        return Linkset.Parts(w2, Host.Session.Objects, p2, Host.Session, me, hud);
    }

    /// <summary>The selected part's faces by direction from the person (the renderer out of sight knows them once it has
    /// drawn the part; it glances at it first if not).</summary>
    async Task RefreshFacesAsync(Linkset.Part p, bool said = false)
    {
        int version = ++_facesVersion;
        _facesOf = p.Id;
        _faces.AccessibleName = $"Faces of {PartName(p)}";
        if (p.Hud) { Fill(_faces, new(), "A HUD's faces aren't drawn here: Enter on the part touches it."); return; }
        if (ViewWords.Gone(_view)) { Fill(_faces, new(), $"Faces need the 3D view. {ViewWords.Down(_view)} Enter on the part touches it."); return; }
        Fill(_faces, new(), "Finding its faces.");
        var set = await _view!.FacesAsync(p.Id);
        if (version != _facesVersion || IsDisposed) return;
        if (set is not { } faces) { Fill(_faces, new(), "Its faces aren't known yet (not drawn): F5 here looks again."); return; }
        var eye = Eye();
        var items = faces.Faces.Select(f => (Text: Linkset.FaceWords(f, eye), Key: f.Face.ToString(), Tag: (object)new FaceItem(p, f)))
            .OrderBy(x => Order(x.Text)).ThenBy(x => ((FaceItem)x.Tag).Face.Face).ToList();
        Fill(_faces, items, "No faces.");
        if (said) Reader?.Answer(items.Count == 1 ? "1 face." : $"{items.Count} faces.", keep: false);
    }

    /// <summary>The faces in the order a person would look: the side facing you first, then top, sides, far, bottom.</summary>
    static int Order(string words) => words.StartsWith("The side facing") ? 0 : words.StartsWith("The top") ? 1
        : words.StartsWith("The left") || words.StartsWith("The right") ? 2 : words.StartsWith("The far") ? 3
        : words.StartsWith("The bottom") ? 4 : 5;

    System.Numerics.Vector3 Eye() => _ops.Me() is { } m ? new(m.Pos.X, m.Pos.Y, m.Pos.Z + 0.7f) : default;

    /// <summary>A part touched as the person's own click on it (the part itself, never its root), on a face when one is
    /// given (where on it the face's middle is, as a script's llDetectedTouchFace and ST read it).</summary>
    async Task TouchPartAsync(Linkset.Part p, FaceShape? face)
    {
        var args = new JsonObject { ["obj_uuid"] = p.Id.ToString() };
        if (face is { } f) args["face"] = f.Face;
        var r = await _ops.DoAsync("LLAgent", "requestTouch", args);
        var what = face is { } ff ? $"{PartName(p)}, {Linkset.FaceWords(ff, Eye()).Split(", face")[0].ToLowerInvariant()}" : PartName(p);
        Answer(PersonOps.Error(r) is { } e ? $"Didn't touch {what}: {e}" : $"Touched {what}.");
    }

    async Task SitPartAsync(Linkset.Part p)
    {
        var r = await _ops.DoAsync("LLAgent", "requestSit", new JsonObject { ["obj_uuid"] = p.Id.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"Didn't sit: {e}" : $"Sitting on {PartName(p)}.");
    }

    /// <summary>The person's own HUDs (worn on the HUD points), for the objects list's Show.</summary>
    async Task<List<PersonOps.Thing>> HudsAsync()
    {
        if (Host?.Session.Region is not { } w || _ops.Client is not { } c) return new();
        var roots = w.Prims.Values.Where(p => p.ParentID == c.Self.LocalID && c.Self.LocalID != 0 && (int)p.PrimData.AttachmentPoint is >= 31 and <= 38).ToList();
        // a worn object's own name comes only once it's been selected (the edit tool's)
        if (roots.Any(p => p.Properties == null))
            try { await Host.Session.Objects.FetchFullAsync(w, roots, TimeSpan.FromSeconds(3), CancellationToken.None); } catch (Exception) { }
        return roots.Select(p =>
        {
            var name = p.Properties?.Name is { Length: > 0 } n ? n : "a HUD";
            var point = Sojourn.Core.Agents.ObjectViews.AttachPointName((int)p.PrimData.AttachmentPoint);
            int count = Host.Session.Objects.Linkset(w, p).Count;
            return new PersonOps.Thing(p.ID, name, default, 0, false, false, $"on {point}, {(count == 1 ? "1 part" : $"{count} parts")}") { Hud = true };
        }).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>`//touch NAME [face WORDS]`: the nearest part (or object) within 10 m whose name has the words, or the
    /// part named so of the object named first ("//touch chest lid"); on a face by direction or number.</summary>
    async Task TouchWordsAsync(string words)
    {
        var w = words.Trim();
        string? faceWords = null;
        int at = w.LastIndexOf(" face ", StringComparison.OrdinalIgnoreCase);
        if (at >= 0) { faceWords = w[(at + 6)..].Trim(); w = w[..at].Trim(); }
        else if (w.StartsWith("face ", StringComparison.OrdinalIgnoreCase)) { Answer("//touch NAME face top: the name first."); return; }
        if (w.Length == 0) { Answer("//touch NAME, or //touch NAME face top (or front, left, a number)."); return; }
        var found = await FindPartAsync(w);
        if (found is not { } p) { Answer($"Nothing called {w} within 10 metres."); return; }
        FaceShape? face = null;
        if (faceWords != null)
        {
            if (p.Hud || _view == null || await _view.FacesAsync(p.Id) is not { } set) { Answer($"{PartName(p)}'s faces aren't known: touching it without a face."); }
            else if (Linkset.FindFace(set.Faces, faceWords, Eye()) is { } f) face = f;
            else { Answer($"{PartName(p)} has no face {faceWords}: top, bottom, front, far, left, right, inside, or a number."); return; }
        }
        await TouchPartAsync(p, face);
    }

    /// <summary>A part by words: an object's name then a part's ("chest lid"), else any part or object within 10 m whose
    /// name has them, nearest first; a part with its own script before the object itself.</summary>
    async Task<Linkset.Part?> FindPartAsync(string words)
    {
        var things = await _ops.ObjectsAsync(10, 30);
        var lw = words.ToLowerInvariant();
        foreach (var t in things)
        {
            var parts = await PartsOfAsync(t.Id, false);
            if (parts == null) continue;
            var tn = t.Name.ToLowerInvariant();
            // "chest lid": the object's name, then a part's
            if (lw.StartsWith(tn + " ") && parts.FirstOrDefault(p => p.Named && p.Name.Contains(words[(tn.Length + 1)..], StringComparison.OrdinalIgnoreCase)) is { } named)
                return named;
            if (parts.Skip(1).FirstOrDefault(p => p.Named && p.Name.Contains(words, StringComparison.OrdinalIgnoreCase)) is { } child) return child;
            if (tn.Contains(lw))
                // the object by name: the one part that takes touches when only a child does (the sunken city's chests)
                return parts[0].Touch || parts.Count(p => p.Touch) != 1 ? parts[0] : parts.First(p => p.Touch);
        }
        return null;
    }
}
