using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Microsoft.Extensions.Logging;

namespace Sojourn.Access;

/// <summary>
/// Inventory (Alt+4): a tree of folders and items, each item's name with its type and whether it's worn ("Red Dress,
/// skirt, worn"; "Hair, object, worn on Skull"), a search box and a Show choice (everything, worn, recent) that list
/// what matches flat, with where each lives. Space marks items (in the item's own words: "..., marked"), and what's
/// marked is what the actions take; nothing marked, the selected one. Enter does the obvious thing (wears, opens a
/// notecard or script in an edit control, teleports by a landmark); the Applications key lists every action: wear, add,
/// take off, replace or add to the outfit, rez on the ground or where the person says, unbox, give (confirmed), cut,
/// copy, paste, rename, new folder or notecard, details, delete (confirmed). Control+X, C, V, Delete and F2 work on the
/// tree too.
///
/// A second pane (a blind tester's second inventory window: for moving many items between folders, her fishing catch):
/// Alt+Shift+4 opens it beside the first (Alt+4 goes back to the first, the Applications key and the menu bar's View
/// too); each has its own tree, search, Show
/// and marks; Control+M moves what's marked (or selected) into the folder selected in the other pane, the Applications
/// key's "Copy to the other pane" copies, and cut and paste work across them; Control+W in the second pane closes it.
/// The trees don't change under the person: their actions refresh the folders they touched (in both panes), F5 everything.
///
/// A search or Show's list walks the whole inventory, thousands of items in a real one (a fishing game's catch): the
/// walk runs off the window's thread, the newest asked cancelling the one before, so the window and the screen reader
/// never wait on it (docs/access-app.md > How it fits > Big inventories). Searches leave the Trash out; Show: Trash lists
/// what's in it.
/// </summary>
public sealed partial class MainForm
{
    TabPage _inventoryPage = null!;
    InvPane _pane1 = null!, _pane2 = null!;
    /// <summary>The pane the person is in, or was in last.</summary>
    InvPane _pane = null!;
    TableLayoutPanel _invLayout = null!;
    ContextMenuStrip _invMenu = null!;
    readonly List<UUID> _invClip = new();
    bool _invClipCut;

    /// <summary>One pane of the inventory: its tree, search and Show, and what's marked, open and shown there.</summary>
    sealed class InvPane
    {
        public required string Title;   // "" for the first, "second pane" for the second
        public required TreeView Tree;
        public required TextBox Search;
        public required ComboBox Show;
        public required TableLayoutPanel Panel;
        public readonly System.Windows.Forms.Timer SearchTimer = new() { Interval = 450 };
        // what Space marked (by id): said in the item's own words ("..., marked"), not as a check box, which a screen
        // reader reads on every item ("check box, not checked") and the guided test took for worn or not
        public readonly HashSet<string> Marked = new();
        public readonly HashSet<string> Open = new();   // the folders the person has open, kept open when the tree is filled again
        public string Mode = "";   // "" the tree; else what the flat list shows: "search", "worn", "recent", "trash"
        public CancellationTokenSource? Finding;   // the flat list being found off the window's thread, if one is
        public readonly Dictionary<UUID, object> Fills = new();   // big folders still going in, by folder (FillRest)
        public string? Keep;   // during a fill: the node that stays selected
        public (HashSet<UUID> Ids, Dictionary<UUID, string> Points)? WornShown;
        public bool ShownOnce;
        public bool Contains(Control? c) => c != null && (c == Tree || c == Search || c == Show);
    }

    IEnumerable<InvPane> Panes => _secondOpen ? new[] { _pane1, _pane2 } : new[] { _pane1 };
    InvPane Other(InvPane p) => p == _pane1 ? _pane2 : _pane1;
    // (not the panel's Visible: that's false whenever the inventory's page isn't the one showing)
    bool _secondOpen;
    bool SecondOpen => _secondOpen;

    long _wornAt;

    LibreMetaverse.Inventory? Store => _ops?.Client?.Inventory.Store;

    void BuildInventory()
    {
        _inventoryPage = new TabPage("Inventory") { Name = "inventoryPage", AccessibleName = "Inventory" };
        _invMenu = new ContextMenuStrip { Name = "inventoryMenu", AccessibleName = "Actions" };
        _pane1 = MakePane("", "inventory", "Inventory");
        _pane2 = MakePane("second pane", "inventory2", "Inventory, second pane");
        _pane2.Panel.Visible = false;
        _pane = _pane1;

        _invLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        _invLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _invLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 0));
        _invLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _invLayout.Controls.Add(_pane1.Panel, 0, 0);
        _invLayout.Controls.Add(_pane2.Panel, 1, 0);
        _pane1.Panel.TabIndex = 0; _pane2.Panel.TabIndex = 1;
        _inventoryPage.Controls.Add(_invLayout);
    }

    InvPane MakePane(string title, string name, string accessibleName)
    {
        var tree = new ReaderTree
        {
            Name = name, AccessibleName = accessibleName, Dock = DockStyle.Fill, HideSelection = false,
            ShowRootLines = true, PathSeparator = "/",
        };
        var search = new LineBox { Name = name == "inventory" ? "inventorySearch" : name + "Search", AccessibleName = title == "" ? "Search inventory" : "Search the second pane", Dock = DockStyle.Fill };
        var show = new ComboBox { Name = name == "inventory" ? "inventoryShow" : name + "Show", AccessibleName = title == "" ? "Show" : "Show, second pane", DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
        show.Items.AddRange(new object[] { "Everything", "Worn", "Recent", "Trash" });
        show.SelectedIndex = 0;
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Margin = Padding.Empty };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        // the mnemonics only on the first pane's labels (Alt+S and Alt+O there, Alt+H being Help's; the second's are reached by Tab)
        t.Controls.Add(new Label { Text = title == "" ? "&Search" : "Search", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        t.Controls.Add(search, 1, 0);
        t.Controls.Add(new Label { Text = title == "" ? "Sh&ow" : "Show", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        t.Controls.Add(show, 3, 0);
        t.Controls.Add(tree, 0, 1);
        t.SetColumnSpan(tree, 4);
        tree.TabIndex = 0; search.TabIndex = 1; show.TabIndex = 2;
        var p = new InvPane { Title = title, Tree = tree, Search = search, Show = show, Panel = t };

        // the menu by the Applications key, Shift+F10 or a right click, built for what's chosen then (not the control's
        // ContextMenuStrip: a menu shown for its own key would open again on the key's release)
        tree.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            if (tree.GetNodeAt(e.Location) is { } at) tree.SelectedNode = at;
            ShowInventoryMenu(p);
        };
        tree.BeforeExpand += (_, e) => { if (e.Node != null && e.Node.Tag is InventoryFolder f && !Filled(e.Node)) FillFolder(p, e.Node, f.UUID); };
        tree.AfterExpand += (_, e) => { if (p.Mode == "" && e.Node?.Tag is InventoryFolder) p.Open.Add(e.Node.Name); };
        tree.AfterCollapse += (_, e) => { if (p.Mode == "" && e.Node?.Tag is InventoryFolder) p.Open.Remove(e.Node.Name); };
        tree.KeyDown += (_, e) => InventoryKey(p, e);
        foreach (Control c in new Control[] { tree, search, show }) c.Enter += (_, _) => _pane = p;
        search.TextChanged += (_, _) => { p.SearchTimer.Stop(); p.SearchTimer.Start(); };
        p.SearchTimer.Tick += (_, _) => { p.SearchTimer.Stop(); ShowInventory(p, said: true); };
        search.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; p.SearchTimer.Stop(); ShowInventory(p, said: true, focus: true); }
            else if (e.KeyCode == Keys.Escape) { e.Handled = e.SuppressKeyPress = true; search.Clear(); p.SearchTimer.Stop(); ShowInventory(p, said: false, focus: true); }
        };
        show.SelectedIndexChanged += (_, _) => ShowInventory(p, said: show.Focused);
        return p;
    }

    void InventoryShown()
    {
        if (_pane1.ShownOnce) return;
        // not loaded yet (just arrived on a busy grid): a line saying so is there to land on, never an empty tree
        if (Store?.RootFolder == null) { if (_pane1.Tree.Nodes.Count == 0) ShowInventory(_pane1, said: false); return; }
        _pane1.ShownOnce = true;
        ShowInventory(_pane1, said: false);
    }

    /// <summary>Alt+Shift+4 (the inventory's page showing): to the second pane, opening it the first time (at the top,
    /// or where it was left).</summary>
    void OpenSecondPane()
    {
        _pane = _pane2;
        if (SecondOpen) { _pane2.Tree.Focus(); return; }
        _secondOpen = true;
        _pane2.Panel.Visible = true;
        _invLayout.ColumnStyles[0].Width = 50;
        _invLayout.ColumnStyles[1].Width = 50;
        if (!_pane2.ShownOnce) { _pane2.ShownOnce = true; ShowInventory(_pane2, said: false); }
        _pane2.Tree.Focus();
        Reader?.Answer("Second pane. Alt+4 goes to the first, Alt+Shift+4 back here, Control+M moves what's marked into the folder selected in the other, Control+W closes this one.", keep: false);
    }

    void CloseSecondPane()
    {
        _secondOpen = false;
        _pane2.Panel.Visible = false;
        _invLayout.ColumnStyles[0].Width = 100;
        _invLayout.ColumnStyles[1].Width = 0;
        _pane = _pane1;
        _pane1.Tree.Focus();
        Reader?.Answer("Second pane closed.", keep: false);
    }

    bool InInventory => _areas.SelectedTab == _inventoryPage && (_pane1.Contains(ActiveControl) || _pane2.Contains(ActiveControl));

    // --- what's shown ---------------------------------------------------------------------------------------------------

    static bool Filled(TreeNode n) => !(n.Nodes.Count == 1 && n.Nodes[0].Tag == null);

    static TreeNode Placeholder() => new("Loading") { Name = "" };

    /// <summary>The tree, or the flat list for a search or the Show choice; said: how many, when the person asked;
    /// focus: the tree takes the focus once it shows them (Enter and Escape in Search). A flat list is found off the
    /// window's thread (FindFlat), cancelling the one asked before; one found within a tenth of a second shows at once,
    /// as it always did, and a slower one says "Searching." at half a second (when the person asked) and shows when
    /// it's found, the focus going to the tree then if it's still in Search.</summary>
    void ShowInventory(InvPane p, bool said, bool focus = false)
    {
        p.Finding?.Cancel();
        p.Finding = null;
        var tree = p.Tree;
        if (Store is not { RootFolder: { } root } store || _ops.Client is not { } client)
        {
            p.Fills.Clear();
            tree.Nodes.Clear();
            tree.SelectedNode = tree.Nodes.Add("Inventory not loaded yet. F5 to look again.");
            if (focus) tree.Focus();
            return;
        }
        var words = p.Search.Text.Trim();
        var show = p.Show.SelectedIndex;
        var mode = words.Length > 0 ? "search" : show == 1 ? "worn" : show == 2 ? "recent" : show == 3 ? "trash" : "";
        if (mode == "")
        {
            p.Mode = "";
            FillFolder(p, null, root.UUID);
            if (focus) tree.Focus();
            return;
        }
        var ask = new FlatAsk(store, root.UUID, client.Inventory.FindFolderForType(FolderType.Trash), mode, words, show, Worn());
        var cts = p.Finding = new CancellationTokenSource();
        var find = Task.Run(() => FindFlat(ask, cts.Token), cts.Token);
        try { find.Wait(100); }
        catch (AggregateException) { }   // (cancelled or failed: ShowFlat and ShowFlatLater see which)
        if (find.IsCompleted) ShowFlat(p, cts, find, said, focus);
        else ShowFlatLater(p, cts, find, said, focus);
    }

    /// <summary>A flat list still being found: "Searching." at half a second if the person asked, then the list.
    /// (async void: a failure reaches the window's handler, logged and said, as one on the window's thread does.)</summary>
    async void ShowFlatLater(InvPane p, CancellationTokenSource cts, Task<Flat> find, bool said, bool focus)
    {
        if (said && await Task.WhenAny(find, Task.Delay(400)) != find && p.Finding == cts) Reader?.Answer("Searching.", keep: false);
        try { await find; }
        catch (OperationCanceledException) { return; }
        // the focus follows only if it's still where Enter was pressed (the person may have gone on elsewhere)
        ShowFlat(p, cts, find, said, focus && ActiveControl == p.Search);
    }

    /// <summary>A flat list found, shown, unless a newer one was asked for meanwhile or it's another login's.</summary>
    void ShowFlat(InvPane p, CancellationTokenSource cts, Task<Flat> find, bool said, bool focus)
    {
        if (p.Finding != cts || find.IsCanceled) return;
        p.Finding = null;
        var (ask, ordered, inTrash, seen, walkMs) = find.Result;   // (a failure throws here, on the window's thread)
        if (Store != ask.Store) return;
        var tree = p.Tree;
        var worn = ask.Worn;
        var words = ask.Words;
        bool trashOnly = ask.Show == 3;
        p.Mode = ask.Mode;
        var keep = tree.SelectedNode?.Name;
        p.WornShown = worn;
        // what a search left out in the Trash is counted, and Show: Trash lists it
        var trashed = inTrash > 0 ? $"{inTrash} in the Trash (Show: Trash lists {(inTrash == 1 ? "it" : "them")})" : "";
        var shown = System.Diagnostics.Stopwatch.StartNew();
        p.Fills.Clear();   // (a big folder still going in has left the tree)
        tree.BeginUpdate();
        try
        {
            tree.Nodes.Clear();
            foreach (var (n, path) in ordered)
                tree.Nodes.Add(MakeNode(p, n, worn, path.Length > 0 ? path : "My Inventory"));
            if (ordered.Count == 0)
                tree.Nodes.Add(new TreeNode(p.Mode switch
                {
                    "search" when trashOnly => $"Nothing called {words} in the Trash.",
                    "search" => trashed.Length > 0 ? $"Nothing called {words}, but {trashed}." : $"Nothing called {words}.",
                    "worn" => "Nothing worn.",
                    "trash" => "The Trash is empty.",
                    _ => "Nothing new in the last day.",
                }));
            tree.SelectedNode = (keep != null ? tree.Nodes[keep] : null) ?? tree.Nodes[0];
        }
        finally { tree.EndUpdate(); }
        if (said)
        {
            var many = $"{ordered.Count}{(ordered.Count >= FlatLimit ? " or more" : "")}";
            Reader?.Answer(ordered.Count == 0 ? tree.Nodes[0].Text
                : p.Mode == "worn" ? $"{many} worn."
                : trashOnly ? $"{many} in the Trash."
                : trashed.Length > 0 ? $"{many} found, and {trashed}." : $"{many} found.", keep: false);
        }
        if (focus) tree.Focus();
        // (how long each part took, for a tester's log: the walk off the window's thread, the list on it)
        _ops.Session.Log.LogInformation("inventory {Mode}: {Seen} looked at, {Found} listed, {InTrash} in the Trash; walked in {Walk} ms, shown in {Shown} ms",
            p.Mode, seen, ordered.Count, inTrash, walkMs, shown.ElapsedMilliseconds);
    }

    const int FlatLimit = 500;

    /// <summary>What a flat list asks for, with what's read on the window's thread for it (the Trash's id, what's worn).</summary>
    sealed record FlatAsk(LibreMetaverse.Inventory Store, UUID Root, UUID Trash, string Mode, string Words, int Show,
        (HashSet<UUID> Ids, Dictionary<UUID, string> Points) Worn);

    /// <summary>A flat list found: its nodes in order, with the folder each is in, how many matches the Trash has, how
    /// many nodes were looked at and how long it took.</summary>
    sealed record Flat(FlatAsk Ask, List<(InventoryBase Node, string Path)> Found, int InTrash, int Seen, long Ms);

    /// <summary>A flat list's walk, off the window's thread. It reads the store as Sojourn.Core's own walks do: GetContents
    /// hands out a copy of a folder's contents taken under that folder's lock, and lookups are LibreMetaverse's concurrent
    /// dictionary; nothing of the window's is touched. A search leaves the Trash out, counting its matches; Show: Trash
    /// walks only the Trash. At most 500, as before.</summary>
    static Flat FindFlat(FlatAsk a, CancellationToken ct)
    {
        var took = System.Diagnostics.Stopwatch.StartNew();
        var store = a.Store;
        var since = DateTime.UtcNow.AddHours(-24);
        int seen = 0;
        bool Hit(InventoryBase n) => a.Mode switch
        {
            "search" => n.Name.Contains(a.Words, StringComparison.OrdinalIgnoreCase)
                        && (a.Show != 1 || n is InventoryItem wi && a.Worn.Ids.Contains(Target(store, wi).UUID))
                        && (a.Show != 2 || n is InventoryItem ri && Utc(ri.CreationDate) >= since),
            "worn" => n is InventoryItem it && it.AssetType is not (AssetType.Link or AssetType.LinkFolder) && a.Worn.Ids.Contains(it.UUID),
            "trash" => true,
            _ => n is InventoryItem rt && Utc(rt.CreationDate) >= since,
        };
        bool trashOnly = a.Show == 3;
        var found = new List<(InventoryBase Node, string Path)>();
        if (trashOnly && a.Trash == UUID.Zero) return new(a, found, 0, 0, 0);
        var trashName = store.TryGetValue<InventoryFolder>(a.Trash, out var tf) ? tf.Name : "Trash";
        Walk(store, trashOnly ? a.Trash : a.Root, trashOnly ? trashName : "", trashOnly ? UUID.Zero : a.Trash, ct, (n, path) =>
        {
            seen++;
            if (Hit(n)) found.Add((n, path));
            return found.Count < FlatLimit;
        });
        int inTrash = 0;
        if (a.Mode == "search" && !trashOnly && a.Trash != UUID.Zero)
            Walk(store, a.Trash, trashName, UUID.Zero, ct, (n, _) => { seen++; if (Hit(n)) inTrash++; return true; });
        var ordered = a.Mode == "recent"
            ? found.OrderByDescending(f => ((InventoryItem)f.Node).CreationDate).ToList()
            : found.OrderBy(f => f.Node is InventoryFolder ? 0 : 1).ThenBy(f => f.Node.Name, StringComparer.OrdinalIgnoreCase).ToList();
        return new(a, ordered, inTrash, seen, took.ElapsedMilliseconds);
    }

    static DateTime Utc(DateTime d) => d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    /// <summary>Every node under a folder, depth first, with the path of the folder it's in (the outfit's links and
    /// <paramref name="skip"/> not entered); false once told to stop. Any thread; throws when cancelled.</summary>
    static bool Walk(LibreMetaverse.Inventory store, UUID folder, string path, UUID skip, CancellationToken ct, Func<InventoryBase, string, bool> each)
    {
        ct.ThrowIfCancellationRequested();
        List<InventoryBase> contents;
        try { contents = store.GetContents(folder); }
        catch (InventoryException) { return true; }   // (gone meanwhile)
        foreach (var n in contents)
        {
            if (!each(n, path)) return false;
            if (n is InventoryFolder f && f.PreferredType != FolderType.CurrentOutfit && f.UUID != skip
                && !Walk(store, f.UUID, path.Length > 0 ? $"{path}/{f.Name}" : f.Name, skip, ct, each))
                return false;
        }
        return true;
    }

    /// <summary>A folder's contents as nodes (folder null: the top of the tree), folders first then items, by name; the
    /// nodes that were open, marked and selected stay so. Shown already (a refresh after an action, F5), they're brought
    /// up to date in place (Reconcile): emptying a tree view costs as much as filling it, 200 ms for a folder of 4,500
    /// (a fishing game's catch), so only what changed goes or comes. Shown the first time, a folder of thousands goes in
    /// a part at a time: the first 500 (and as far as the one that stays selected) at once, the rest in parts of 500
    /// between the person's keys (FillRest); the whole folder at once held the window's thread 200 ms, nearly all of it
    /// the tree view's own inserting.</summary>
    void FillFolder(InvPane p, TreeNode? node, UUID folder) => FillFolder(p, node, folder, null);

    void FillFolder(InvPane p, TreeNode? node, UUID folder, (HashSet<UUID> Ids, Dictionary<UUID, string> Points)? wornNow)
    {
        if (Store is not { } store) return;
        var tree = p.Tree;
        var nodes = node?.Nodes ?? tree.Nodes;
        List<InventoryBase> kids;
        try { kids = Sorted(store.GetContents(folder)); }
        catch (InventoryException) { kids = new(); }   // (gone meanwhile)
        if (kids.Count == 0 && node != null)
            _ = _ops.Session.Inventory.EnsureLoadedAsync(folder, CancellationToken.None).ContinueWith(_ =>
            {
                if (IsDisposed) return;
                BeginInvoke(() =>
                {
                    if (!node.IsExpanded || store.GetContents(folder).Count == 0) { if (!Filled(node)) node.Nodes.Clear(); return; }
                    FillFolder(p, node, folder);
                });
            });
        var worn = wornNow ?? Worn();
        p.WornShown = worn;
        if (ShowsTree(nodes))
        {
            Reconcile(p, node, folder, nodes, kids, worn);
            return;
        }
        var selected = tree.SelectedNode?.Name;
        // (the outermost fill's selection: a folder opened inside it, as open folders are, keeps that one among its first part)
        bool outer = p.Keep == null;
        if (outer) p.Keep = selected ?? "";
        try
        {
            var made = kids.Select(k => MakeNode(p, k, worn, null)).ToArray();
            var keep = p.Keep;
            int now = made.Length <= FillPart ? made.Length : Math.Max(FillPart, Array.FindIndex(made, n => n.Name == keep) + 1);
            var fill = p.Fills[folder] = new object();
            tree.BeginUpdate();
            try
            {
                nodes.Clear();
                nodes.AddRange(made[..now]);
                // (opening one fills it, and it opens its own open folders in turn)
                OpenAgain(p, made[..now]);
                if (node == null && nodes.Count == 0) nodes.Add(new TreeNode("Inventory is empty, or not loaded yet. F5 to look again."));
                var again = selected != null ? tree.Nodes.Find(selected, true).FirstOrDefault() : null;
                if (again != null) tree.SelectedNode = again;
                else if (tree.SelectedNode == null && tree.Nodes.Count > 0) tree.SelectedNode = tree.Nodes[0];
            }
            finally { tree.EndUpdate(); }
            if (now < made.Length) AfterInput(() => FillRest(p, node, folder, made, now, fill));
            else p.Fills.Remove(folder);
        }
        finally { if (outer) p.Keep = null; }
    }

    /// <summary>Whether these nodes are a folder's contents as the tree shows them (not a flat list's, a line saying
    /// there's nothing, or a folder's placeholder).</summary>
    static bool ShowsTree(TreeNodeCollection nodes)
    {
        if (nodes.Count == 0) return false;
        foreach (TreeNode n in nodes)
            if (n is not InvNode { Path: null }) return false;
        return true;
    }

    /// <summary>A folder shown already, brought up to date: nodes no longer in it removed, new ones put in their place,
    /// moved ones (renamed) moved, every node's words and item renewed, and its folders that were filled brought up to
    /// date the same way. What's open stays open; what's selected stays selected, or if it went, the folder itself
    /// (the top of the tree, for the top) takes the selection.</summary>
    void Reconcile(InvPane p, TreeNode? node, UUID folder, TreeNodeCollection nodes, List<InventoryBase> kids,
        (HashSet<UUID> Ids, Dictionary<UUID, string> Points) worn)
    {
        var tree = p.Tree;
        p.Fills.Remove(folder);   // (a big folder still going in: the rest goes in now, below)
        var want = kids.Select(k => k.UUID.ToString()).ToHashSet();
        var selected = tree.SelectedNode;
        bool lost = false;
        tree.BeginUpdate();
        try
        {
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                if (want.Contains(nodes[i].Name)) continue;
                for (var up = selected; up != null && !lost; up = up.Parent) lost = up == nodes[i];
                nodes.RemoveAt(i);
            }
            var have = new Dictionary<string, InvNode>();
            foreach (InvNode n in nodes) have[n.Name] = n;
            for (int i = 0; i < kids.Count; i++)
            {
                var k = kids[i];
                if (have.TryGetValue(k.UUID.ToString(), out var tn))
                {
                    if (!ReferenceEquals(nodes[i], tn))
                    {
                        bool wasSelected = selected == tn;
                        nodes.Remove(tn);
                        nodes.Insert(i, tn);
                        if (wasSelected) tree.SelectedNode = tn;
                    }
                    tn.Item = k;
                    tn.Tag = k;
                    if (Label(p, tn, worn) is var text && tn.Text != text) tn.Text = text;
                    if (k is InventoryFolder && Filled(tn)) FillFolder(p, tn, k.UUID, worn);
                }
                else
                {
                    var made = MakeNode(p, k, worn, null);
                    nodes.Insert(i, made);
                    if (p.Open.Contains(made.Name)) made.Expand();
                }
            }
            if (node == null && nodes.Count == 0) nodes.Add(new TreeNode("Inventory is empty, or not loaded yet. F5 to look again."));
            if (lost || tree.SelectedNode == null) tree.SelectedNode = node ?? (tree.Nodes.Count > 0 ? tree.Nodes[0] : null);
        }
        finally { tree.EndUpdate(); }
    }

    const int FillPart = 500;

    /// <summary>The rest of a big folder's nodes, a part at a time, each after whatever input came meanwhile; stops when
    /// the folder was filled or brought up to date again since, or its node left the tree.</summary>
    void FillRest(InvPane p, TreeNode? node, UUID folder, TreeNode[] made, int from, object fill)
    {
        if (IsDisposed || !p.Fills.TryGetValue(folder, out var f) || f != fill || node is { TreeView: null }) return;
        var nodes = node?.Nodes ?? p.Tree.Nodes;
        int to = Math.Min(made.Length, from + FillPart);
        p.Tree.BeginUpdate();
        try
        {
            nodes.AddRange(made[from..to]);
            OpenAgain(p, made[from..to]);
        }
        finally { p.Tree.EndUpdate(); }
        if (to < made.Length) AfterInput(() => FillRest(p, node, folder, made, to, fill));
        else p.Fills.Remove(folder);
    }

    static void OpenAgain(InvPane p, TreeNode[] added)
    {
        foreach (var tn in added)
            if (p.Open.Contains(tn.Name)) tn.Expand();
    }

    /// <summary>Runs on the window's thread once the input waiting has been handled: a WinForms timer's tick comes after
    /// keys in Windows' order, where BeginInvoke's message would come before them.</summary>
    static void AfterInput(Action a)
    {
        var t = new System.Windows.Forms.Timer { Interval = 1 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            a();
        };
        t.Start();
    }

    static List<InventoryBase> Sorted(IEnumerable<InventoryBase> nodes) => nodes
        .OrderBy(n => n is InventoryFolder ? 0 : 1).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();

    static IEnumerable<TreeNode> Descendants(TreeNodeCollection nodes)
    {
        foreach (TreeNode n in nodes)
        {
            yield return n;
            foreach (var d in Descendants(n.Nodes)) yield return d;
        }
    }

    /// <summary>An inventory node, with the folder it's in when it's in a flat list (for its words again later).</summary>
    sealed class InvNode(InventoryBase item, string? path) : TreeNode
    {
        public InventoryBase Item = item;   // (renewed when a refresh finds it again: LibreMetaverse may have replaced it)
        public readonly string? Path = path;
    }

    TreeNode MakeNode(InvPane p, InventoryBase n, (HashSet<UUID> Ids, Dictionary<UUID, string> Points) worn, string? path)
    {
        var tn = new InvNode(n, path) { Name = n.UUID.ToString(), Tag = n };
        tn.Text = Label(p, tn, worn);
        if (n is InventoryFolder && path == null) tn.Nodes.Add(Placeholder());
        return tn;
    }

    /// <summary>"Red Dress, skirt, worn, in Clothing, marked".</summary>
    string Label(InvPane p, InvNode tn, (HashSet<UUID> Ids, Dictionary<UUID, string> Points) worn) =>
        Words(p, tn.Item, worn) + (tn.Path != null ? $", in {tn.Path}" : "") + (p.Marked.Contains(tn.Name) ? ", marked" : "");

    /// <summary>Every node's words again where they changed (worn, marked), without rebuilding the tree: the place
    /// and what's open stay, and the screen reader hears the item it's on change.</summary>
    void Relabel(InvPane p, (HashSet<UUID> Ids, Dictionary<UUID, string> Points)? worn = null)
    {
        var w = worn ?? Worn();
        p.WornShown = w;
        foreach (var tn in Descendants(p.Tree.Nodes).OfType<InvNode>())
            if (Label(p, tn, w) is var text && tn.Text != text) tn.Text = text;
    }

    /// <summary>The slow tick, while the inventory shows: when what's worn changes (a wear or take-off landing, or the
    /// outfit changed some other way), the items' words follow.</summary>
    void WatchWorn()
    {
        if (_areas.SelectedTab != _inventoryPage || Environment.TickCount64 - _wornAt < 1000) return;
        _wornAt = Environment.TickCount64;
        var now = Worn();
        // the outfit's things not attached stop being "still arriving" once its first minute or two is over
        bool arrivingFlipped = _arrivingShown != OutfitArriving;
        _arrivingShown = OutfitArriving;
        foreach (var p in Panes)
        {
            if (p.WornShown is not { } shown) continue;
            if (!arrivingFlipped && now.Ids.SetEquals(shown.Ids) && now.Points.Count == shown.Points.Count && now.Points.All(x => shown.Points.TryGetValue(x.Key, out var v) && v == x.Value))
                continue;
            Relabel(p, now);
        }
    }

    /// <summary>"Red Dress, skirt, worn", "Hair, object, worn on Skull", "Dresses" (a folder), "Home, landmark, link".</summary>
    string Words(InvPane p, InventoryBase n, (HashSet<UUID> Ids, Dictionary<UUID, string> Points) worn)
    {
        if (n is InventoryFolder f) return p.Mode == "" ? f.Name : $"{f.Name}, folder";
        var it = (InventoryItem)n;
        var target = Target(it);
        var words = $"{it.Name}, {TypeWord(target)}";
        if (worn.Ids.Contains(target.UUID))
            words += worn.Points.TryGetValue(target.UUID, out var at) ? $", worn on {at}"
                // in the outfit (its link in Current Outfit) and not on the avatar yet: arriving, or not attached
                : target.AssetType == AssetType.Object ? $", {NotAttachedWords}" : ", worn";
        else if (target.AssetType == AssetType.Gesture && GestureActive(target))
            words += Sojourn.Core.Agents.GestureOps.KnownTrigger(target.AssetUUID) is { Length: > 0 } t ? $", active, {t}" : ", active";
        if (it.AssetType == AssetType.Link) words += ", link";
        else if (it.AssetType == AssetType.LinkFolder) words += ", folder link";
        return words;
    }

    /// <summary>What a link points to (else the item itself).</summary>
    InventoryItem Target(InventoryItem it) => Store is { } store ? Target(store, it) : it;

    static InventoryItem Target(LibreMetaverse.Inventory store, InventoryItem it) =>
        it.AssetType == AssetType.Link && store.TryGetValue<InventoryItem>(it.AssetUUID, out var t) ? t : it;

    static string TypeWord(InventoryItem it) => it.AssetType switch
    {
        AssetType.Clothing or AssetType.Bodypart => ((WearableType)(it.Flags & 0xFF)).ToString().ToLowerInvariant(),
        AssetType.Object => "object",
        AssetType.Notecard => "notecard",
        AssetType.LSLText => "script",
        AssetType.Landmark => "landmark",
        AssetType.Texture => it.InventoryType == InventoryType.Snapshot ? "snapshot" : "texture",
        AssetType.Sound => "sound",
        AssetType.Animation => "animation",
        AssetType.Gesture => "gesture",
        AssetType.CallingCard => "calling card",
        AssetType.Settings => "environment settings",
        AssetType.Material => "material",
        AssetType.Link => "link",
        AssetType.LinkFolder => "folder link",
        _ => "item",
    };

    /// <summary>What's worn: items attached now (by the region's word, with their points) and the outfit's links.</summary>
    (HashSet<UUID> Ids, Dictionary<UUID, string> Points) Worn()
    {
        var ids = new HashSet<UUID>();
        var points = new Dictionary<UUID, string>();
        if (_ops?.Client is not { } c) return (ids, points);
        // (the region's prims walked in place, once a second on this page: Values would copy them all first)
        if (Host!.Session.Region is { } w && w.AvatarById(c.Self.AgentID) is { } me)
            foreach (var (_, p) in w.Prims)
                if (p.ParentID == me.LocalID && AgentInventory.AttachItemId(p) is var id && id != UUID.Zero)
                {
                    ids.Add(id);
                    points[id] = ObjectViews.AttachPointName((int)p.PrimData.AttachmentPoint);
                }
        var cof = c.Inventory.FindFolderForType(FolderType.CurrentOutfit);
        if (Store is { } store && cof != UUID.Zero)
            foreach (var l in store.GetContents(cof).OfType<InventoryItem>()) ids.Add(l.AssetUUID);
        return (ids, points);
    }

    /// <summary>The folders whose contents changed: shown again in each pane (the person's place kept), or a flat list
    /// redone.</summary>
    void RefreshInventory(params UUID[] folders)
    {
        var root = Store?.RootFolder?.UUID ?? UUID.Zero;
        foreach (var p in Panes)
        {
            if (!p.ShownOnce) continue;
            if (p.Mode != "") { ShowInventory(p, said: false); continue; }
            foreach (var f in folders.Distinct())
            {
                if (f == root || f == UUID.Zero) { FillFolder(p, null, root); continue; }
                foreach (var tn in p.Tree.Nodes.Find(f.ToString(), true))
                    if (Filled(tn) && tn.Tag is InventoryFolder) FillFolder(p, tn, f);
            }
        }
    }

    // --- what the person picked -------------------------------------------------------------------------------------

    List<TreeNode> Marked(InvPane p) => Descendants(p.Tree.Nodes).Where(n => n.Tag is InventoryBase && p.Marked.Contains(n.Name)).ToList();

    /// <summary>The marked nodes, else the selected one.</summary>
    List<InventoryBase> Chosen(InvPane p)
    {
        var marked = Marked(p);
        if (marked.Count > 0) return marked.Select(n => (InventoryBase)n.Tag).ToList();
        return p.Tree.SelectedNode?.Tag is InventoryBase b ? new() { b } : new();
    }

    void Unmark(InvPane p)
    {
        if (p.Marked.Count == 0) return;
        p.Marked.Clear();
        Relabel(p, p.WornShown);
    }

    /// <summary>Space: the selected item marked, or unmarked; how many are marked said.</summary>
    void ToggleMark(InvPane p)
    {
        if (p.Tree.SelectedNode is not InvNode tn) return;
        if (!p.Marked.Remove(tn.Name)) p.Marked.Add(tn.Name);
        tn.Text = Label(p, tn, p.WornShown ?? Worn());
        int n = Marked(p).Count;
        Reader?.Answer(n == 0 ? "Nothing marked." : $"{n} marked.", keep: false);
    }

    static string Named(List<InventoryBase> nodes) => nodes.Count == 1 ? nodes[0].Name : $"{nodes.Count} items";

    /// <summary>The folder a pane points at: the folder selected, or the one the selected item is in (else the top).</summary>
    InventoryFolder? FolderAt(InvPane p)
    {
        var at = p.Tree.SelectedNode?.Tag as InventoryBase;
        return at is InventoryFolder f ? f : at != null && Store?.TryGetValue<InventoryFolder>(at.ParentUUID, out var pf) == true ? pf : Store?.RootFolder;
    }

    // --- keys and the menu ------------------------------------------------------------------------------------------

    void InventoryKey(InvPane p, KeyEventArgs e)
    {
        bool handled = true;
        var tree = p.Tree;
        switch (e.KeyData)
        {
            case Keys.Enter:
                if (tree.SelectedNode is { Tag: InventoryFolder } fn) { if (fn.IsExpanded) fn.Collapse(); else fn.Expand(); }
                else if (tree.SelectedNode?.Tag is InventoryItem it)
                {
                    if (DefaultAction(p, it) is { } act) act.Do();
                    else Reader?.Answer("Nothing to open; the Applications key has its actions.", keep: false);
                }
                break;
            case Keys.Space: ToggleMark(p); break;
            case Keys.Delete: _ = DeleteAsync(p); break;
            case Keys.F2: Rename(p); break;
            case Keys.Control | Keys.X: Clip(p, cut: true); break;
            case Keys.Control | Keys.C: Clip(p, cut: false); break;
            case Keys.Control | Keys.V: _ = PasteAsync(p); break;
            case Keys.Control | Keys.M: _ = ToOtherPaneAsync(p, copy: false); break;
            case Keys.Control | Keys.F: p.Search.Focus(); p.Search.SelectAll(); break;
            case Keys.F5: ShowInventory(p, said: false); Reader?.Answer("Inventory shown again.", keep: false); break;
            case Keys.Apps or (Keys.Shift | Keys.F10): ShowInventoryMenu(p); break;
            default: handled = false; break;
        }
        if (handled) e.Handled = e.SuppressKeyPress = true;
    }

    /// <param name="Key">the key that does it too, shown on the menu</param>
    sealed record InvAction(string Text, Action Do, string? Key = null);

    /// <summary>Enter on an item: wear it, open it, or teleport by it.</summary>
    InvAction? DefaultAction(InvPane p, InventoryItem item) => ItemActions(p, new() { item }).TakeWhile(a => a.Text != "-").FirstOrDefault();

    void ShowInventoryMenu(InvPane p)
    {
        if (!FillInventoryMenu(p)) return;
        var r = p.Tree.SelectedNode?.Bounds ?? Rectangle.Empty;
        _invMenu.Show(p.Tree, new Point(r.Left + 10, r.Bottom));
    }

    bool FillInventoryMenu(InvPane p)
    {
        _invMenu.Items.Clear();
        var chosen = Chosen(p);
        if (chosen.Count == 0) return false;
        var actions = chosen.Count == 1 && chosen[0] is InventoryFolder f ? FolderActions(p, f) : ItemActions(p, chosen);
        foreach (var a in actions)
        {
            if (a.Text == "-") { _invMenu.Items.Add(new ToolStripSeparator()); continue; }
            var act = a;
            _invMenu.Items.Add(new ToolStripMenuItem(a.Text, null, (_, _) => act.Do()) { ShortcutKeyDisplayString = a.Key });
        }
        // the other pane, as Alt+4 and Alt+Shift+4 go there (after the menu has closed and given the focus back)
        _invMenu.Items.Add(new ToolStripSeparator());
        bool inSecond = p == _pane2;
        _invMenu.Items.Add(new ToolStripMenuItem(inSecond ? "To the first pane" : SecondOpen ? "To the second pane" : "Open a second pane", null,
            (_, _) => BeginInvoke(() => RealInput.Ours(() => GoInventory(second: !inSecond)))) { ShortcutKeyDisplayString = inSecond ? "Alt+4" : "Alt+Shift+4" });
        return _invMenu.Items.Count > 0;
    }

    List<InvAction> ItemActions(InvPane p, List<InventoryBase> chosen)
    {
        var list = new List<InvAction>();
        var items = chosen.OfType<InventoryItem>().ToList();
        var worn = Worn();
        var wearable = items.Select(Target).Where(i => i.AssetType is AssetType.Object or AssetType.Clothing or AssetType.Bodypart).ToList();
        bool one = chosen.Count == 1 && items.Count == 1;
        var target = one ? Target(items[0]) : null;
        if (one && target!.AssetType is AssetType.Notecard or AssetType.LSLText) list.Add(new("&Open", () => _ = OpenAsync(p, target)));
        if (one && target!.AssetType == AssetType.Notecard) list.Add(new("Play as a ro&ute", () => _ = PlayRouteAsync(target)));
        // (each menu's letters its own: Cu&t below has T)
        if (one && target!.AssetType == AssetType.Landmark) list.Add(new("Teleport the&re", () => _ = _cmd.TeleportLandmarkAsync(target.UUID, target.Name)));
        // gestures: Enter plays one; the chosen ones activated or deactivated together
        var gestures = items.Select(Target).Where(i => i.AssetType == AssetType.Gesture).ToList();
        if (one && gestures.Count == 1) list.Add(new("P&lay", () => _ = PlayGestureAsync(gestures[0].UUID)));
        if (gestures.Any(g => !GestureActive(g))) list.Add(new("&Activate", () => _ = ActivateGesturesAsync(gestures.Where(g => !GestureActive(g)).ToList(), on: true)));
        if (gestures.Any(GestureActive)) list.Add(new("Deacti&vate", () => _ = ActivateGesturesAsync(gestures.Where(GestureActive).ToList(), on: false)));
        // (an object in the outfit but not on the avatar can be worn again: Wear attaches it)
        var notWorn = wearable.Where(i => !worn.Ids.Contains(i.UUID) || i.AssetType == AssetType.Object && !worn.Points.ContainsKey(i.UUID)).ToList();
        var isWorn = wearable.Where(i => worn.Ids.Contains(i.UUID)).ToList();
        if (notWorn.Count > 0) list.Add(new("&Wear", () => _ = WearAsync(p, notWorn, replace: true)));
        if (notWorn.Any(i => i.AssetType is AssetType.Object or AssetType.Clothing)) list.Add(new("&Add to outfit", () => _ = WearAsync(p, notWorn, replace: false)));
        if (isWorn.Count > 0) list.Add(new("Ta&ke off", () => _ = TakeOffAsync(p, isWorn)));
        if (one && target!.AssetType == AssetType.Object && !worn.Ids.Contains(target.UUID))
        {
            list.Add(new("&Rez on the ground", () => _ = RezAsync(target)));
            list.Add(new("Rez where you &say...", () => _ = RezAtAsync(target)));
            list.Add(new("&Unbox", () => _ = UnboxAsync(target)));
        }
        list.Add(new("-", () => { }));
        list.Add(new("&Give to...", () => _ = GiveAsync(p, chosen)));
        list.Add(new("Cu&t", () => Clip(p, cut: true), "Control+X"));
        list.Add(new("&Copy", () => Clip(p, cut: false), "Control+C"));
        if (_invClip.Count > 0) list.Add(new("&Paste", () => _ = PasteAsync(p), "Control+V"));
        if (SecondOpen && FolderAt(Other(p)) is { } there)
        {
            list.Add(new($"&Move to {there.Name}, in the other pane", () => _ = ToOtherPaneAsync(p, copy: false), "Control+M"));
            list.Add(new($"Cop&y to {there.Name}, in the other pane", () => _ = ToOtherPaneAsync(p, copy: true)));
        }
        if (one) list.Add(new("Re&name", () => Rename(p), "F2"));
        if (one) list.Add(new("D&etails", () => _ = DetailsAsync(items[0])));
        if (one) list.Add(new("Descri&be", () => DescribeKey(me: false), "Control+D"));
        list.Add(new("&Delete", () => _ = DeleteAsync(p), "Delete"));
        if (Marked(p).Count > 0) list.Add(new("Unmark a&ll", () => { Unmark(p); Reader?.Answer("Nothing marked.", keep: false); }));
        // what Enter does, the first action, says so (one item chosen: Enter acts on the selected one)
        if (one && list.Count > 0 && list[0].Text != "-") list[0] = list[0] with { Key = "Enter" };
        return list;
    }

    List<InvAction> FolderActions(InvPane p, InventoryFolder f)
    {
        var list = new List<InvAction>
        {
            new("&Replace outfit", () => _ = OutfitAsync(f, append: false)),
            new("&Add to outfit", () => _ = OutfitAsync(f, append: true)),
            new("Ta&ke off the folder's items", () => _ = TakeOffFolderAsync(f)),
            new("-", () => { }),
            new("New &folder", () => _ = NewFolderAsync(f)),
            new("New n&otecard", () => _ = NewNotecardAsync(p, f)),   // (Re&name has N)
            new("&Upload a file here...", () => _ = UploadAsync(f, null), "Control+U"),
            new("&Give to...", () => _ = GiveAsync(p, new() { f })),
            new("Cu&t", () => Clip(p, cut: true), "Control+X"),
            new("&Copy", () => Clip(p, cut: false), "Control+C"),
        };
        if (_invClip.Count > 0) list.Add(new("&Paste", () => _ = PasteAsync(p), "Control+V"));
        if (SecondOpen && FolderAt(Other(p)) is { } there && there.UUID != f.UUID)
        {
            list.Add(new($"&Move to {there.Name}, in the other pane", () => _ = ToOtherPaneAsync(p, copy: false), "Control+M"));
            list.Add(new($"Cop&y to {there.Name}, in the other pane", () => _ = ToOtherPaneAsync(p, copy: true)));
        }
        list.Add(new("Re&name", () => Rename(p), "F2"));
        list.Add(new("&Delete", () => _ = DeleteAsync(p), "Delete"));
        return list;
    }

    // --- actions --------------------------------------------------------------------------------------------------------

    async Task WearAsync(InvPane p, List<InventoryItem> items, bool replace)
    {
        var done = new List<string>();
        foreach (var it in items)
        {
            var r = await _ops.DoAsync("TesselAgent", "attach", new JsonObject { ["item_id"] = it.UUID.ToString(), ["replace"] = replace });
            if (PersonOps.Error(r) is { } e) Answer($"Didn't wear {it.Name}: {e}");
            else done.Add(it.Name);
        }
        if (done.Count > 0) Answer($"{(replace ? "Wearing" : "Added")} {Join(done)}.");
        Unmark(p);
        await Task.Delay(1500);
        RefreshInventory(items.Select(i => i.ParentUUID).ToArray());
    }

    async Task TakeOffAsync(InvPane p, List<InventoryItem> items)
    {
        var done = new List<string>();
        foreach (var it in items)
        {
            var r = await _ops.DoAsync("TesselAgent", "detach", new JsonObject { ["item_id"] = it.UUID.ToString() });
            if (PersonOps.Error(r) is { } e) Answer($"Didn't take off {it.Name}: {e}");
            else done.Add(it.Name);
        }
        if (done.Count > 0) Answer($"Took off {Join(done)}.");
        Unmark(p);
        await Task.Delay(1500);
        RefreshInventory(items.Select(i => i.ParentUUID).ToArray());
    }

    async Task OutfitAsync(InventoryFolder f, bool append)
    {
        Answer(append ? $"Adding {f.Name} to the outfit." : $"Changing into {f.Name}.");
        var r = await _ops.DoAsync("LLAppearance", "wearOutfit", new JsonObject { ["folder_id"] = f.UUID.ToString(), ["append"] = append });
        Answer(PersonOps.Error(r) is { } e ? $"Didn't change: {e}" : append ? $"Added {f.Name}." : $"Wearing {f.Name}.");
        await Task.Delay(1500);
        RefreshInventory(f.ParentUUID, f.UUID);
    }

    async Task TakeOffFolderAsync(InventoryFolder f)
    {
        var worn = Worn();
        var items = Store?.GetContents(f.UUID).OfType<InventoryItem>().Select(Target).Where(i => worn.Ids.Contains(i.UUID) && i.AssetType != AssetType.Bodypart).ToList() ?? new();
        if (items.Count == 0) { Answer($"Nothing in {f.Name} is worn (body parts stay on)."); return; }
        var r = await _ops.DoAsync("LLAppearance", "detachItems", new JsonObject { ["items_id"] = new JsonArray(items.Select(i => (JsonNode)i.UUID.ToString()).ToArray()) });
        Answer(PersonOps.Error(r) is { } e ? $"Didn't take them off: {e}" : $"Took off {Join(items.Select(i => i.Name).ToList())}.");
        await Task.Delay(1500);
        RefreshInventory(f.UUID);
    }

    static string Join(List<string> names) => names.Count switch
    {
        0 => "nothing", 1 => names[0], 2 => $"{names[0]} and {names[1]}",
        <= 5 => string.Join(", ", names.Take(names.Count - 1)) + $" and {names[^1]}",
        _ => $"{names.Count} items",
    };

    /// <summary>Where an object rezzed in front of the avatar rests: 2 m ahead, on the ground (the terrain, or the floor
    /// the avatar stands on when that's well above it).</summary>
    async Task<(Vector3 Pos, float Ground)?> InFrontAsync()
    {
        if (_ops.Me() is not { } me) return null;
        var rad = (90 - me.Heading) * Math.PI / 180;
        var x = me.Pos.X + (float)(Math.Cos(rad) * 2);
        var y = me.Pos.Y + (float)(Math.Sin(rad) * 2);
        float feet = me.Pos.Z - 0.9f, ground = feet;
        var r = await _ops.ReadAsync("TesselAgent", "ground", new JsonObject { ["points"] = new JsonArray(new JsonArray((double)x, (double)y)) });
        if ((r["heights"] as JsonArray)?.FirstOrDefault() is JsonValue hv && hv.TryGetValue<double>(out var terrain) && feet - terrain < 1.5)
            ground = (float)terrain;
        return (new Vector3(x, y, ground + 0.5f), ground);
    }

    /// <summary>Rezzes an object 2 m ahead and sets it down with its bottom on the ground; its id, or null (said).</summary>
    async Task<UUID?> RezOnGroundAsync(InventoryItem item)
    {
        if (await InFrontAsync() is not { } at) { Answer("Not in a region yet."); return null; }
        var r = await _ops.DoAsync("TesselBuild", "rez", new JsonObject { ["item_id"] = item.UUID.ToString(), ["pos"] = Xyz(at.Pos.X, at.Pos.Y, at.Pos.Z) });
        if (PersonOps.Error(r) is { } e || !UUID.TryParse(r.Str("id"), out var id)) { Answer($"Couldn't rez {item.Name}: {PersonOps.Error(r) ?? "no object came"}"); return null; }
        var got = await _ops.ReadAsync("TesselObjects", "get", new JsonObject { ["ids"] = new JsonArray(id.ToString()), ["timeout"] = 3 });
        if ((got["objects"] as JsonArray)?.FirstOrDefault() is JsonObject o && o["scale"] is JsonArray s && s.Count >= 3)
        {
            var half = (float)s[2]!.GetValue<double>() / 2;
            await _ops.DoAsync("TesselBuild", "modify", new JsonObject { ["id"] = id.ToString(), ["pos"] = Xyz(at.Pos.X, at.Pos.Y, at.Ground + half) });
        }
        return id;
    }

    /// <summary>A position for an op (as doubles: the ops read a float JsonValue as nothing, which put a rez at 0, 0, 0).</summary>
    static JsonArray Xyz(double x, double y, double z) => new(x, y, z);

    async Task RezAsync(InventoryItem item)
    {
        if (await RezOnGroundAsync(item) is null) return;
        Answer($"Rezzed {item.Name}, 2 metres ahead, on the ground.");
        RefreshInventory(item.ParentUUID);
    }

    /// <summary>Unboxing: the box rezzed in front, its contents copied into a new folder named after it at the top of the
    /// inventory, then the box put away (deleted when the inventory still has it, taken back when it was no-copy).</summary>
    async Task UnboxAsync(InventoryItem box)
    {
        Answer($"Unboxing {box.Name}.");
        if (await RezOnGroundAsync(box) is not { } id) return;
        JsonArray items = new();
        for (int i = 0; i < 6 && items.Count == 0; i++)
        {
            await Task.Delay(i == 0 ? 500 : 1000);
            var r = await _ops.DoAsync("TesselBuild", "contents", new JsonObject { ["id"] = id.ToString(), ["running"] = false });
            items = r["items"] as JsonArray ?? new();
        }
        int copied = 0;
        string? folderName = null;
        if (items.Count > 0 && Store?.RootFolder is { } root)
        {
            folderName = box.Name;
            var made = await _ops.DoAsync("TesselInventory", "createFolder", new JsonObject { ["name"] = folderName, ["parent_id"] = root.UUID.ToString() });
            if (PersonOps.Error(made) is { } fe) { Answer($"Couldn't make a folder for it: {fe}"); folderName = null; }
            else
                foreach (var it in items.OfType<JsonObject>())
                {
                    var c = await _ops.DoAsync("TesselBuild", "copyFromContents", new JsonObject
                    {
                        ["id"] = id.ToString(), ["item_id"] = it.Str("item_id"), ["folder_id"] = made.Str("folder_id"),
                    });
                    if (PersonOps.Error(c) is { } ce) Answer($"Didn't copy {it.Str("name")}: {ce}");
                    else copied++;
                }
        }
        // the box away again: the rezzed copy deleted (the inventory keeps the box), or a no-copy box taken back
        bool copyable = (box.Permissions.OwnerMask & PermissionMask.Copy) != 0;
        var away = copyable
            ? await _ops.DoAsync("TesselBuild", "delete", new JsonObject { ["ids"] = new JsonArray(id.ToString()) })
            : await _ops.DoAsync("TesselBuild", "take", new JsonObject { ["ids"] = new JsonArray(id.ToString()), ["folder_id"] = box.ParentUUID.ToString() });
        var left = PersonOps.Error(away) is { } ae ? $" The box is still out, 2 metres ahead ({ae})." : "";
        Answer(items.Count == 0 ? $"Nothing inside {box.Name}.{left}"
            : $"Unboxed {box.Name}: {copied} of {items.Count} {(items.Count == 1 ? "item" : "items")} into the folder {folderName}, at the top of your inventory.{left}");
        await Task.Delay(1000);
        RefreshInventory(Store?.RootFolder?.UUID ?? UUID.Zero, box.ParentUUID);
    }

    async Task OpenAsync(InvPane p, InventoryItem item)
    {
        bool script = item.AssetType == AssetType.LSLText;
        var r = await _ops.ReadAsync("TesselInventory", script ? "readScript" : "readNotecard", new JsonObject { ["item_id"] = item.UUID.ToString() });
        if (PersonOps.Error(r) is { } e) { Answer($"Couldn't open {item.Name}: {e}"); return; }
        var editor = new EditorForm(script ? "Script" : "Notecard", item.Name, r.Str("text") ?? "", async text =>
        {
            var w = await _ops.DoAsync("TesselInventory", script ? "writeScript" : "writeNotecard", new JsonObject { ["item_id"] = item.UUID.ToString(), ["text"] = text });
            if (PersonOps.Error(w) is { } we) return ($"Not saved: {we}", null);
            if (!script) return ("Saved.", null);
            var errors = (w["errors"] as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? new();
            if (w["compiled"]?.GetValue<bool>() != false && errors.Count == 0) return ("Saved and compiled.", null);
            var list = string.Join("\r\n", errors);
            return ($"Saved; {errors.Count} {(errors.Count == 1 ? "error" : "errors")}: {string.Join(" ", errors.Take(3))}", list);
        }, text => Reader?.Answer(text, keep: false));
        editor.FormClosed += (_, _) => { if (!IsDisposed) { Activate(); p.Tree.Focus(); } };
        editor.Show(this);
    }

    /// <summary>A notecard of `//go` lines (a blind tester's routes) played step by step (Commands.PlayRouteAsync).</summary>
    async Task PlayRouteAsync(InventoryItem card)
    {
        var r = await _ops.ReadAsync("TesselInventory", "readNotecard", new JsonObject { ["item_id"] = card.UUID.ToString() });
        if (PersonOps.Error(r) is { } e) { Answer($"Couldn't read {card.Name}: {e}"); return; }
        await _cmd.PlayRouteAsync(card.Name, r.Str("text") ?? "");
    }

    async Task GiveAsync(InvPane p, List<InventoryBase> chosen)
    {
        var people = new List<Dialogs.Someone>();
        foreach (var x in await _ops.PeopleAsync(96)) people.Add(new(x.Id, x.Name, _ops.Where(x.Pos) ?? "nearby"));
        var friends = await _ops.ReadAsync("TesselAgent", "friends");
        foreach (var f in (friends["friends"] as JsonArray ?? new()).OfType<JsonObject>()
                     .OrderByDescending(f => f["online"]?.GetValue<bool>() == true))
            if (UUID.TryParse(f.Str("id"), out var fid) && people.All(x => x.Id != fid))
                people.Add(new(fid, f.Str("display_name") is { Length: > 0 } dn ? dn : f.Str("username") ?? "a friend",
                    f["online"]?.GetValue<bool>() == true ? "friend, online" : "friend"));
        var who = Dialogs.Pick(this, $"Give {Named(chosen)} to", people, FindSomeoneAsync);
        if (who == null) { Reader?.Answer("Not given.", keep: false); return; }
        foreach (var n in chosen)
        {
            var args = new JsonObject { ["to"] = who.Id.ToString() };
            args[n is InventoryFolder ? "folder_id" : "item_id"] = n.UUID.ToString();
            if (await ConfirmedAsync("TesselAgent", "give", args) is not { } r) continue;
            Answer(PersonOps.Error(r) is { } e ? $"{n.Name} not given: {e}" : $"Gave {n.Name} to {who.Name}.");
        }
        Unmark(p);
        RefreshInventory(chosen.Select(c => c.ParentUUID).ToArray());
    }

    /// <summary>What's cut or copied in the inventory, given to someone from their side (the profile's Give, the people
    /// and friends menus): the blind tester's Radegast habit, copying an item and giving it from the person
    /// (2026-10-31). Each give waits for her confirmation, as from the inventory.</summary>
    async Task GiveCopiedAsync(UUID to, string name)
    {
        var chosen = _invClip.Select(id => Store is { } s && s.Contains(id) ? s[id] : null).OfType<InventoryBase>().ToList();
        if (chosen.Count == 0)
        {
            Answer($"Nothing copied to give. In your inventory, Alt+4, Control+C on what you'd give {name}, then Give here.");
            return;
        }
        foreach (var n in chosen)
        {
            var args = new JsonObject { ["to"] = to.ToString() };
            args[n is InventoryFolder ? "folder_id" : "item_id"] = n.UUID.ToString();
            if (await ConfirmedAsync("TesselAgent", "give", args) is not { } r) continue;
            Answer(PersonOps.Error(r) is { } e ? $"{n.Name} not given: {e}" : $"Gave {n.Name} to {name}.");
        }
        RefreshInventory(chosen.Select(c => c.ParentUUID).ToArray());
    }

    /// <summary>Anyone on the grid by name (the avatar picker): an exact match, or the only one.</summary>
    async Task<Dialogs.Someone?> FindSomeoneAsync(string name)
    {
        var r = await _ops.ReadAsync("TesselAgent", "findAvatar", new JsonObject { ["name"] = name, ["limit"] = 5 });
        var hits = (r["people"] as JsonArray ?? new()).OfType<JsonObject>().ToList();
        var hit = hits.FirstOrDefault(h => string.Equals(h.Str("username"), name, StringComparison.OrdinalIgnoreCase)
                                           || string.Equals(h.Str("display_name"), name, StringComparison.OrdinalIgnoreCase))
                  ?? (hits.Count == 1 ? hits[0] : null);
        if (hit == null || !UUID.TryParse(hit.Str("id"), out var id))
        {
            Reader?.Answer(hits.Count == 0 ? $"Nobody called {name}." : $"{hits.Count} people match {name}; type more of the name.", keep: false);
            return null;
        }
        return new(id, hit.Str("display_name") is { Length: > 0 } dn ? dn : hit.Str("username") ?? name, "");
    }

    void Clip(InvPane p, bool cut)
    {
        var chosen = Chosen(p);
        if (chosen.Count == 0) return;
        _invClip.Clear();
        _invClip.AddRange(chosen.Select(c => c.UUID));
        _invClipCut = cut;
        Unmark(p);
        Reader?.Answer($"{(cut ? "Cut" : "Copied")} {Named(chosen)}. Control+V on a folder puts {(chosen.Count == 1 ? "it" : "them")} there.", keep: false);
    }

    async Task PasteAsync(InvPane p)
    {
        if (_invClip.Count == 0) { Reader?.Answer("Nothing cut or copied.", keep: false); return; }
        if (FolderAt(p) is not { } folder) return;
        var ids = _invClip.ToList();
        if (await MoveOrCopyAsync(ids, folder, copy: !_invClipCut) && _invClipCut) _invClip.Clear();
    }

    /// <summary>Control+M (or the menu's Move to and Copy to): what's marked in this pane, else what's selected, into the
    /// folder the other pane points at (the folder selected there, or the one its selected item is in).</summary>
    async Task ToOtherPaneAsync(InvPane p, bool copy)
    {
        if (!SecondOpen) { Reader?.Answer("There's one pane: Alt+4 again opens a second, to move things between folders.", keep: false); return; }
        var chosen = Chosen(p);
        if (chosen.Count == 0 || FolderAt(Other(p)) is not { } folder) return;
        if (chosen.Any(c => c.UUID == folder.UUID)) { Reader?.Answer($"{folder.Name} can't go into itself: select another folder in the other pane.", keep: false); return; }
        if (await MoveOrCopyAsync(chosen.Select(c => c.UUID).ToList(), folder, copy)) Unmark(p);
    }

    /// <summary>Items and folders moved (or copied) into a folder, said, and every pane showing the folders involved
    /// refreshed; true when it went.</summary>
    async Task<bool> MoveOrCopyAsync(List<UUID> ids, InventoryFolder folder, bool copy)
    {
        var from = ids.Select(i => Store?.TryGetValue<InventoryBase>(i, out var n) == true ? n.ParentUUID : UUID.Zero).ToList();
        bool ok;
        if (!copy)
        {
            if (await ConfirmedAsync("TesselInventory", "move", new JsonObject
                {
                    ["ids"] = new JsonArray(ids.Select(i => (JsonNode)i.ToString()).ToArray()), ["folder_id"] = folder.UUID.ToString(),
                }) is not { } r) return false;
            if (PersonOps.Error(r) is { } e) { Answer($"Not moved: {e}"); return false; }
            Answer($"Moved {(ids.Count == 1 ? Store?.TryGetValue<InventoryBase>(ids[0], out var one) == true ? one.Name : "it" : $"{ids.Count} items")} into {folder.Name}.");
            ok = true;
        }
        else
        {
            int done = 0;
            foreach (var id in ids)
            {
                var r = await _ops.DoAsync("TesselInventory", "copy", new JsonObject { ["id"] = id.ToString(), ["dest_id"] = folder.UUID.ToString() });
                if (PersonOps.Error(r) is { } e) Answer($"Not copied: {e}");
                else done++;
            }
            if (done > 0) Answer($"Copied {(done == 1 ? "it" : $"{done} items")} into {folder.Name}.");
            ok = done > 0;
        }
        await Task.Delay(800);
        RefreshInventory(from.Append(folder.UUID).ToArray());
        return ok;
    }

    void Rename(InvPane p)
    {
        if (p.Tree.SelectedNode?.Tag is not InventoryBase n) return;
        var name = Dialogs.Ask(this, $"Rename {n.Name}", "New name", n.Name);
        if (name == null || name == n.Name) { Reader?.Answer("Not renamed.", keep: false); return; }
        _ = RenameAsync(n, name);
    }

    async Task RenameAsync(InventoryBase n, string name)
    {
        var r = await _ops.DoAsync("TesselInventory", "update", new JsonObject { ["id"] = n.UUID.ToString(), ["name"] = name });
        Answer(PersonOps.Error(r) is { } e ? $"Not renamed: {e}" : $"Renamed to {name}.");
        await Task.Delay(500);
        RefreshInventory(n.ParentUUID);
    }

    async Task NewFolderAsync(InventoryFolder parent)
    {
        var name = Dialogs.Ask(this, $"New folder in {parent.Name}", "Folder name");
        if (name == null) { Reader?.Answer("No folder made.", keep: false); return; }
        var r = await _ops.DoAsync("TesselInventory", "createFolder", new JsonObject { ["name"] = name, ["parent_id"] = parent.UUID.ToString() });
        Answer(PersonOps.Error(r) is { } e ? $"No folder made: {e}" : $"Made the folder {name} in {parent.Name}.");
        await Task.Delay(500);
        RefreshInventory(parent.UUID);
    }

    async Task NewNotecardAsync(InvPane p, InventoryFolder parent)
    {
        var name = Dialogs.Ask(this, $"New notecard in {parent.Name}", "Notecard name");
        if (name == null) { Reader?.Answer("No notecard made.", keep: false); return; }
        var r = await _ops.DoAsync("TesselInventory", "createNotecard", new JsonObject { ["name"] = name, ["folder_id"] = parent.UUID.ToString(), ["text"] = "" });
        if (PersonOps.Error(r) is { } e || !UUID.TryParse(r.Str("item_id"), out var id)) { Answer($"No notecard made: {PersonOps.Error(r)}"); return; }
        RefreshInventory(parent.UUID);
        if (await _ops.Session.Inventory.ItemAsync(id, CancellationToken.None) is { } item) await OpenAsync(p, item);
    }

    async Task DetailsAsync(InventoryItem item)
    {
        var t = Target(item);
        var p = t.Permissions.OwnerMask;
        string Rights(PermissionMask m)
        {
            var r = new List<string>();
            if ((m & PermissionMask.Copy) != 0) r.Add("copy");
            if ((m & PermissionMask.Modify) != 0) r.Add("modify");
            if ((m & PermissionMask.Transfer) != 0) r.Add("transfer");
            return r.Count == 0 ? "none" : string.Join(", ", r);
        }
        string creator = "someone";
        var names = await _ops.ReadAsync("TesselAgent", "names", new JsonObject { ["ids"] = new JsonArray(t.CreatorID.ToString()), ["timeout"] = 3 });
        if (names["names"] is JsonObject nm && nm[t.CreatorID.ToString()] is { } cn)
            creator = cn is JsonObject co ? co.Str("display_name") ?? co.Str("username") ?? creator : cn.ToString();
        var desc = string.IsNullOrWhiteSpace(t.Description) || t.Description == "(No Description)" ? "" : $" Description: {t.Description}.";
        Answer($"{t.Name}, {TypeWord(t)}. Yours to {Rights(p)}; the next owner may {Rights(t.Permissions.NextOwnerMask)}. "
               + $"Made by {creator}; in your inventory since {Utc(item.CreationDate).ToLocalTime().ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}.{desc}");
    }

    async Task DeleteAsync(InvPane p)
    {
        var chosen = Chosen(p);
        if (chosen.Count == 0) return;
        if (await ConfirmedAsync("TesselInventory", "delete", new JsonObject
            {
                ["ids"] = new JsonArray(chosen.Select(c => (JsonNode)c.UUID.ToString()).ToArray()),
            }) is not { } r) return;
        if (PersonOps.Error(r) is { } e) { Answer($"Not deleted: {e}"); return; }
        Answer($"Deleted {Named(chosen)} (in the Trash).");
        Unmark(p);
        await Task.Delay(500);
        RefreshInventory(chosen.Select(c => c.ParentUUID).Append(_ops.Session.Inventory.FolderFor(FolderType.Trash)).ToArray());
    }
}
