using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// Places (Alt+6): landmarks by folder (the Landmarks folder and Favorites), the teleport history, a box to go to a
/// region by name (or an SLurl, or "Region 128 64 22"), and the regions around on the map with how many are there.
/// Enter teleports from any of them and the arrival is said ("Arrived in Bay City, at 128, 64, 22."). The lists are
/// read when the area opens and on F5, never changed under the person otherwise.
/// </summary>
public sealed partial class MainForm
{
    TabPage _placesPage = null!;
    TreeView _landmarks = null!;
    ListView _history = null!, _regions = null!;
    TextBox _goRegion = null!;

    sealed record HistoryPlace(string Region, double X, double Y, double Z);
    sealed record MapRegion(string Name);

    void BuildPlaces()
    {
        _placesPage = new TabPage("Places") { Name = "placesPage", AccessibleName = "Places" };
        _landmarks = new ReaderTree { Name = "landmarks", AccessibleName = "Landmarks", Dock = DockStyle.Fill, HideSelection = false, ShowRootLines = true };
        _history = List("history", "Teleport history");
        _goRegion = new LineBox { Name = "goRegion", AccessibleName = "Go to region", Dock = DockStyle.Fill };
        _regions = List("regions", "Regions around");

        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        t.Controls.Add(new Label { Text = "&Landmarks", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 0);
        t.Controls.Add(_landmarks, 1, 0);
        t.Controls.Add(new Label { Text = "Hi&story", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 1);
        t.Controls.Add(_history, 1, 1);
        t.Controls.Add(new Label { Text = "&Go to region", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        t.Controls.Add(_goRegion, 1, 2);
        t.Controls.Add(new Label { Text = "Regions &around", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 3);
        t.Controls.Add(_regions, 1, 3);
        _landmarks.TabIndex = 0; _history.TabIndex = 1; _goRegion.TabIndex = 2; _regions.TabIndex = 3;
        _placesPage.Controls.Add(t);

        _landmarks.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) { e.Handled = true; _ = RefreshPlacesAsync(said: true); }
            else if (e.KeyCode == Keys.Enter)
            {
                e.Handled = e.SuppressKeyPress = true;
                if (_landmarks.SelectedNode is { Tag: InventoryItem lm }) _ = _cmd.TeleportLandmarkAsync(lm.UUID, lm.Name);
                else if (_landmarks.SelectedNode is { } n) { if (n.IsExpanded) n.Collapse(); else n.Expand(); }
            }
        };
        _history.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) { e.Handled = true; _ = RefreshPlacesAsync(said: true); }
            else if (e.KeyCode == Keys.Enter && _history.FocusedItem?.Tag is HistoryPlace h)
            {
                e.Handled = e.SuppressKeyPress = true;
                _ = _cmd.GoToAsync(new Place(h.Region, h.X, h.Y, h.Z));
            }
        };
        _goRegion.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            if (RegionPlace(_goRegion.Text) is { } p) { _goRegion.Clear(); _ = _cmd.GoToAsync(p); }
            else Reader?.Answer("Type a region's name, then Enter. A place in it: Bay City 128 64 22.", keep: false);
        };
        _regions.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) { e.Handled = true; _ = RefreshPlacesAsync(said: true); }
            else if (e.KeyCode == Keys.Enter && _regions.FocusedItem?.Tag is MapRegion r)
            {
                e.Handled = e.SuppressKeyPress = true;
                _ = _cmd.GoToAsync(new Place(r.Name, 128, 128, 30));
            }
        };
    }

    /// <summary>"Bay City", "Bay City 128 64 22", "Bay City/128/64/22" or an SLurl, as a place (the middle of the region
    /// when no position is given).</summary>
    static Place? RegionPlace(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;
        if (Places.Parse(text) is { } p) return p;
        var m = System.Text.RegularExpressions.Regex.Match(text, @"^(.+?)[\s,]+(\d+(?:\.\d+)?)[\s,]+(\d+(?:\.\d+)?)(?:[\s,]+(\d+(?:\.\d+)?))?$");
        if (m.Success)
            return new Place(m.Groups[1].Value.Trim(), D(m.Groups[2].Value), D(m.Groups[3].Value), m.Groups[4].Success ? D(m.Groups[4].Value) : 30);
        return new Place(text, 128, 128, 30);
        static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
    }

    bool _placesBusy;

    async Task RefreshPlacesAsync(bool said)
    {
        if (_placesBusy || Host?.Session.Online != true) return;
        _placesBusy = true;
        // the landmarks' folders load from the grid the first time (slow on a busy one): a line to land on meanwhile
        if (_landmarks.Nodes.Count == 0) _landmarks.SelectedNode = _landmarks.Nodes.Add("Looking for landmarks.");
        try
        {
            int marks = await FillLandmarksAsync();
            var hist = await _ops.ReadAsync("SojournPeople", "teleportHistory", new JsonObject { ["count"] = 50 });
            var places = new List<(string, string, object)>();
            int i = 0;
            foreach (var h in (hist["places"] as JsonArray ?? new()).OfType<JsonObject>())
            {
                var pos = h["pos"] as JsonArray;
                double P(int k, double d) => Json.AsDouble(pos?.ElementAtOrDefault(k)) ?? d;
                var region = h.Str("region") ?? "";
                if (region.Length == 0) continue;
                var parcel = h.Str("parcel") is { Length: > 0 } pn ? $", {pn}" : "";
                var here = h["here"]?.GetValue<bool>() == true ? ", here now" : "";
                places.Add(($"{region}{parcel}, {When(h.Str("when"))}{here}", $"h{i++}", new HistoryPlace(region, P(0, 128), P(1, 128), P(2, 30))));
            }
            Fill(_history, places, "No teleports yet.");
            var around = await _ops.ReadAsync("TesselAgent", "regions", new JsonObject { ["range"] = 8 });
            var regions = new List<(string, string, object)>();
            var list = (around["regions"] as JsonArray ?? new()).OfType<JsonObject>().ToList();
            var me = list.FirstOrDefault(r => r["here"]?.GetValue<bool>() == true);
            double hx = Json.AsDouble(me?["x"]) ?? 0, hy = Json.AsDouble(me?["y"]) ?? 0;
            foreach (var r in list.OrderBy(r => Math.Pow((Json.AsDouble(r["x"]) ?? 0) - hx, 2) + Math.Pow((Json.AsDouble(r["y"]) ?? 0) - hy, 2)))
            {
                var name = r.Str("name") ?? "";
                if (name.Length == 0) continue;
                int agents = (int)(Json.AsDouble(r["agents"]) ?? 0);
                double dx = (Json.AsDouble(r["x"]) ?? 0) - hx, dy = (Json.AsDouble(r["y"]) ?? 0) - hy;
                var where = r["here"]?.GetValue<bool>() == true ? "here" : Away(dx, dy);
                regions.Add(($"{name}, {(agents == 1 ? "1 person" : $"{agents} people")}, {where}", name, new MapRegion(name)));
            }
            Fill(_regions, regions, "No regions on the map around.");
            if (said) Reader?.Answer($"{marks} landmarks, {places.Count} in the history, {regions.Count} regions around.", keep: false);
        }
        finally { _placesBusy = false; }
    }

    /// <summary>"2 regions north", "next door to the east", "3 regions northeast".</summary>
    static string Away(double dx, double dy)
    {
        var dir = Bearings.Point(Bearings.Compass(0, 0, dx, dy)).ToLowerInvariant();
        int n = (int)Math.Round(Math.Max(Math.Abs(dx), Math.Abs(dy)));
        return n <= 1 ? $"next door to the {dir}" : $"{n} regions {dir}";
    }

    /// <summary>"today 14:05", "yesterday 09:12", "12 October"; the history's "yyyy-MM-dd HH:mm".</summary>
    static string When(string? when)
    {
        if (!DateTime.TryParseExact(when, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return when ?? "";
        var days = (DateTime.Today - d.Date).Days;
        return days == 0 ? $"today {d:HH:mm}" : days == 1 ? $"yesterday {d:HH:mm}" : d.ToString("d MMMM", CultureInfo.InvariantCulture);
    }

    /// <summary>The landmarks tree: the Landmarks folder's (folders with landmarks in them) and Favorites; how many. The
    /// folders are fetched and walked off the window's thread (a big Landmarks folder is a long walk), the tree made on it.</summary>
    async Task<int> FillLandmarksAsync()
    {
        if (Store is not { } store) return 0;
        var inv = _ops.Session.Inventory;
        var keep = _landmarks.SelectedNode?.Name;
        var open = Descendants(_landmarks.Nodes).Where(n => n.IsExpanded).Select(n => n.Name).ToHashSet();
        int count = 0;
        async Task<Mark?> Folder(UUID id, string name)
        {
            await inv.EnsureLoadedAsync(id, CancellationToken.None);
            var kids = new List<Mark>();
            foreach (var n in Sorted(store.GetContents(id)))
            {
                if (n is InventoryFolder f) { if (await Folder(f.UUID, f.Name) is { } sub) kids.Add(sub); }
                else if (n is InventoryItem it && Target(store, it) is { AssetType: AssetType.Landmark } lm)
                {
                    kids.Add(new(it.UUID.ToString(), it.Name, lm, new()));
                    count++;
                }
            }
            return kids.Count > 0 ? new(id.ToString(), name, null, kids) : null;
        }
        var folders = new[] { (FolderType.Landmark, "Landmarks"), (FolderType.Favorites, "Favorites") }
            .Select(t => (Id: inv.FolderFor(t.Item1), Name: t.Item2)).Where(t => t.Id != UUID.Zero).ToList();
        var tops = await Task.Run(async () =>
        {
            var list = new List<Mark>();
            foreach (var (id, name) in folders)
                if (await Folder(id, name) is { } top) list.Add(top);
            return list;
        });
        static TreeNode Node(Mark m)
        {
            var node = new TreeNode(m.Name) { Name = m.Id, Tag = m.Landmark };
            foreach (var k in m.Kids) node.Nodes.Add(Node(k));
            return node;
        }
        _landmarks.BeginUpdate();
        try
        {
            _landmarks.Nodes.Clear();
            if (tops.Count == 0) _landmarks.Nodes.Add(new TreeNode("No landmarks yet."));
            foreach (var top in tops) _landmarks.Nodes.Add(Node(top));
            foreach (var n in Descendants(_landmarks.Nodes).Where(n => open.Contains(n.Name) || n.Parent == null).ToList()) n.Expand();
            // (a line's own node has no name, and Find refuses an empty one)
            _landmarks.SelectedNode = (!string.IsNullOrEmpty(keep) ? _landmarks.Nodes.Find(keep, true).FirstOrDefault() : null) ?? _landmarks.Nodes[0];
        }
        finally { _landmarks.EndUpdate(); }
        return count;
    }

    /// <summary>A landmarks folder (Landmark null) or a landmark, as found off the window's thread.</summary>
    sealed record Mark(string Id, string Name, InventoryItem? Landmark, List<Mark> Kids);
}
