using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Access;

namespace Sojourn.Access;

/// <summary>
/// Her own profile (`//profile`, World > My profile): a window of labelled boxes for the words, a line and buttons for the
/// picture, and her picks. Control+S saves the words, said back, with anything the grid didn't keep. A picture takes
/// effect as it's chosen, from the pictures in her inventory or a file uploaded there and then, each describable first,
/// since a picture on her own was a task blind residents hand to a sighted person (docs/access-app.md > Your profile).
/// Not modal, as a notecard's window isn't, so a fee or a deleted pick can be confirmed in Notices and come back here.
/// </summary>
public sealed partial class MainForm
{
    Form? _myProfile;
    bool _myProfileOpening;
    TextBox? _myPicture;

    const string PicturesFilter = "Pictures (PNG, JPEG, TGA, BMP)|*.png;*.jpg;*.jpeg;*.tga;*.bmp|Every file|*.*";

    async Task MyProfileAsync()
    {
        if (_myProfile is { IsDisposed: false } open) { open.Activate(); return; }
        if (_myProfileOpening) return;
        _myProfileOpening = true;
        JsonObject r;
        try { r = await _ops.ReadAsync("TesselAgent", "profile", new JsonObject()); }
        finally { _myProfileOpening = false; }
        if (PersonOps.Error(r) is { } e) { Answer($"Couldn't read your profile: {e}"); return; }

        var form = new Form
        {
            Name = "myProfile", Text = "My profile", AccessibleName = "My profile", Size = new Size(720, 700),
            StartPosition = FormStartPosition.CenterParent, KeyPreview = true, ShowInTaskbar = false,
        };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoScroll = true, Padding = new Padding(6) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        form.Controls.Add(t);
        int tab = 0, row = 0;
        void Row(string label, Control c, int height = 0)
        {
            t.RowStyles.Add(height > 0 ? new RowStyle(SizeType.Absolute, height) : new RowStyle(SizeType.AutoSize));
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, TabIndex = tab++ };
            c.TabIndex = tab++;
            t.Controls.Add(l, 0, row);
            t.Controls.Add(c, 1, row);
            row++;
        }
        LineBox Box(string name, string label, string? text, int lines)
        {
            var box = new LineBox { Name = name, AccessibleName = label.Replace("&", ""), Dock = DockStyle.Fill, MaxLength = 0 };
            if (lines > 1) { box.Multiline = true; box.AcceptsReturn = true; box.ScrollBars = ScrollBars.Vertical; }
            box.Text = (text ?? "").Trim().ReplaceLineEndings("\r\n");
            Row(label, box, lines > 1 ? lines * 22 : 0);
            return box;
        }

        var interests = r["interests"] as JsonObject;
        var fields = new (string Op, TextBox Box, string Words)[]
        {
            ("about", Box("myAbout", "&About", r.Str("about"), 6), "About"),
            ("want_to", Box("myWantTo", "&Wants to", interests?.Str("want_to"), 1), "Wants to"),
            ("skills", Box("mySkills", "&Skills", interests?.Str("skills"), 1), "Skills"),
            ("languages", Box("myLanguages", "&Languages", interests?.Str("languages"), 1), "Languages"),
            ("first_life", Box("myFirstLife", "&First life", r.Str("first_life"), 4), "First life"),
            ("url", Box("myWeb", "We&b address", r.Str("url"), 1), "Web address"),
        };
        var saved = fields.ToDictionary(f => f.Op, f => f.Box.Text);

        var picture = new TextBox { Name = "myPicture", AccessibleName = "Your picture", ReadOnly = true, Dock = DockStyle.Fill, BackColor = SystemColors.Window, Text = PictureWords(r.Str("image_id")) };
        _myPicture = picture;
        Row("&Picture", picture);
        var pictureRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        var choose = new Button { Name = "myPictureChoose", Text = "C&hoose a picture...", AutoSize = true };
        var upload = new Button { Name = "myPictureUpload", Text = "&Upload a picture...", AutoSize = true };
        var describe = new Button { Name = "myPictureDescribe", Text = "&Describe it", AutoSize = true };
        pictureRow.Controls.AddRange(new Control[] { choose, upload, describe });
        Row("", pictureRow);

        var me = _ops.Client?.Self.AgentID ?? UUID.Zero;
        var pictures = new Subject(me.ToString(), "profile", "your profile pictures", "Describe my own profile pictures");
        choose.Click += (_, _) => ChoosePicture(form);
        upload.Click += (_, _) => _ = UploadPictureAsync(form);
        describe.Click += (_, _) => _ = DescribeAsync(pictures);

        var picks = PicksList(form, me, r["picks"] as JsonArray, mine: true);
        Row("Pic&ks", picks, 120);
        var newPick = new Button { Name = "myPickNew", Text = "&New pick where you stand...", AutoSize = true };
        newPick.Click += (_, _) => _ = NewPickAsync(form, picks);
        var save = new Button { Name = "mySave", Text = "Sa&ve", AutoSize = true };
        var close = new Button { Name = "myClose", Text = "Close", AutoSize = true };
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { newPick, save, close });
        Row("", buttons);
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        bool Changed() => fields.Any(f => f.Box.Text.TrimEnd() != saved[f.Op].TrimEnd());
        bool saving = false;
        async Task<bool> SaveAsync()
        {
            if (saving) return false;
            var changed = fields.Where(f => f.Box.Text.TrimEnd() != saved[f.Op].TrimEnd()).ToList();
            if (changed.Count == 0) { Reader?.Answer("Nothing has changed.", keep: false); return true; }
            saving = true;
            try
            {
                Reader?.Answer("Saving.", keep: false);
                var args = new JsonObject();
                foreach (var f in changed) args[f.Op] = f.Box.Text.TrimEnd().ReplaceLineEndings("\n");
                var w = await _ops.DoAsync("TesselAgent", "setProfile", args);
                if (PersonOps.Error(w) is { } why) { Reader?.Answer($"Not saved: {why}.", keep: false); return false; }
                var lost = new List<string>();
                foreach (var f in changed)
                {
                    var kept = f.Op is "want_to" or "skills" or "languages" ? (w["interests"] as JsonObject)?.Str(f.Op) : w.Str(f.Op);
                    var sent = f.Box.Text.TrimEnd().ReplaceLineEndings("\n");
                    if (kept != null && kept.TrimEnd().ReplaceLineEndings("\n") != sent)
                    {
                        lost.Add(f.Words);
                        // what's there now, so the box says what the profile says
                        f.Box.Text = kept.TrimEnd().ReplaceLineEndings("\r\n");
                    }
                    saved[f.Op] = f.Box.Text;
                }
                Reader?.Answer(lost.Count > 0
                    ? $"Saved, but the grid didn't keep all of {Join(lost)}: the box shows what it kept."
                    : "Saved.", keep: false);
                return true;
            }
            finally { saving = false; }
        }

        foreach (var f in fields)
        {
            var box = f.Box;
            if (!box.Multiline) box.KeyDown += (_, e) => { if (e.KeyData == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; _ = SaveAsync(); } };
        }
        save.Click += (_, _) => _ = SaveAsync();
        close.Click += (_, _) => form.Close();
        form.CancelButton = close;
        form.KeyDown += (_, e) =>
        {
            if (e.KeyData == (Keys.Control | Keys.S)) { e.Handled = e.SuppressKeyPress = true; _ = SaveAsync(); }
            else if (e.KeyData == Keys.F1)
            {
                e.Handled = e.SuppressKeyPress = true;
                Reader?.Answer("Control+S saves the words, as does Enter in a one-line box; Escape closes. Tab goes through About, Wants to, Skills, Languages, First life, "
                    + "the web address, your picture and its buttons, then your picks, where Enter reads one and the Applications key has the rest. A picture is changed as soon as it's chosen.", keep: false);
            }
        };
        form.FormClosing += (_, e) =>
        {
            if (e.Cancel || e.CloseReason != CloseReason.UserClosing || !Changed()) return;
            var q = MessageBox.Show(form, "Save the changes to your profile?", "My profile", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (q == DialogResult.No) return;
            e.Cancel = true;
            if (q == DialogResult.Yes) _ = SaveThenCloseAsync();
        };
        async Task SaveThenCloseAsync()
        {
            if (await SaveAsync()) form.Close();   // (a failed save keeps it open)
        }
        form.FormClosed += (_, _) =>
        {
            _myProfile = _confirmFrom = null;
            _myPicture = null;
            if (!IsDisposed) Activate();
        };
        form.ActiveControl = fields[0].Box;
        form.Shown += (_, _) =>
        {
            fields[0].Box.SelectionStart = 0;
            fields[0].Box.SelectionLength = 0;
            Reader?.Answer("Your profile. Control+S saves, Escape closes, F1 says the rest.", keep: false);
        };
        _myProfile = _confirmFrom = form;
        form.Show(this);
    }

    /// <summary>The pictures in her inventory as far as it's loaded, the newest first (one just uploaded is at the top).</summary>
    List<InventoryItem> MyPictures() => Store?.RootFolder is not { } root ? new()
        : _ops.Session.Inventory.ItemsInStore(root.UUID).Where(i => i.AssetType == AssetType.Texture)
            .OrderByDescending(i => i.CreationDate).ToList();

    /// <summary>What her profile's picture is, in words: its name if it's in her inventory.</summary>
    string PictureWords(string? imageId)
    {
        if (!UUID.TryParse(imageId, out var id) || id == UUID.Zero) return "No picture.";
        return MyPictures().FirstOrDefault(i => i.AssetUUID == id) is { } item
            ? $"{item.Name}, a picture in your inventory."
            : "A picture that isn't in your inventory.";
    }

    /// <summary>The pictures to choose from, narrowed as a name is typed, Control+D describing the selected one. Taking it
    /// off is the last row: nothing is one press from it, and the first row is what a stray Enter would take.</summary>
    void ChoosePicture(Form owner)
    {
        var items = MyPictures();
        if (items.Count == 0) { Reader?.Answer("There are no pictures in your inventory yet. Upload a picture puts one there and uses it.", keep: false); return; }
        var rows = items.Select(i => new Dialogs.Someone(i.UUID, i.Name, $"in {_ops.Session.Inventory.Node(i.ParentUUID)?.Name ?? "your inventory"}")).ToList();
        rows.Add(new Dialogs.Someone(UUID.Zero, "No picture, taking yours off", ""));
        Reader?.Answer($"{items.Count} {(items.Count == 1 ? "picture" : "pictures")}, the newest first. Control+D describes the selected one.", keep: false);
        var chosen = Dialogs.Pick(owner, "Choose your profile picture", rows, _ => Task.FromResult<Dialogs.Someone?>(null), "Pictures",
            s => { if (s.Id != UUID.Zero) _ = DescribeAsync(new(s.Id.ToString(), "item", $"the picture of {s.Name}", $"Describe the picture of my inventory item {s.Name}")); });
        if (chosen == null) { Reader?.Answer("Your picture hasn't changed.", keep: false); return; }
        _ = SetPictureAsync(chosen.Id, chosen.Name);
    }

    /// <summary>The picture changed, said back from what the grid now has. itemId zero: none.</summary>
    async Task SetPictureAsync(UUID itemId, string name)
    {
        var was = _myPicture?.Text;
        var r = await _ops.DoAsync("TesselAgent", "setProfile", new JsonObject { ["image_id"] = itemId == UUID.Zero ? "none" : itemId.ToString() });
        if (PersonOps.Error(r) is { } e) { Reader?.Answer($"Your picture hasn't changed: {e}.", keep: false); return; }
        if (_myPicture is { IsDisposed: false } box) box.Text = PictureWords(r.Str("image_id"));
        bool none = !UUID.TryParse(r.Str("image_id"), out var now) || now == UUID.Zero;
        if (itemId != UUID.Zero && none) { Reader?.Answer("The grid didn't keep that picture: your profile has none now.", keep: false); return; }
        if (itemId == UUID.Zero && !none) { Reader?.Answer("The grid kept your picture as it was.", keep: false); return; }
        Reader?.Answer(itemId == UUID.Zero
            ? (was is { Length: > 0 } ? $"No profile picture now. It was: {was}" : "No profile picture now.")
            : $"Your profile picture is now {name}.", keep: false);
    }

    /// <summary>A picture from a file: Windows' Open window, the upload's own window and any fee confirmed as usual, then
    /// it's her picture. Nothing changes if she stops along the way.</summary>
    async Task UploadPictureAsync(Form form)
    {
        _uploadedItem = UUID.Zero;
        await UploadAsync(null, null, PicturesFilter);
        var item = _uploadedItem;
        if (form.IsDisposed) return;
        form.Activate();
        if (item == UUID.Zero) return;
        if (MyPictures().FirstOrDefault(i => i.UUID == item) is not { } picture)
        {
            Reader?.Answer("That went into your inventory, but it isn't a picture, so your profile hasn't changed.", keep: false);
            return;
        }
        await SetPictureAsync(item, picture.Name);
    }

    // --- picks: hers to change, anyone's to read and go to ------------------------------------------------------------

    sealed record PickRow(UUID Id, string Name);

    static List<(string Text, string Key, object Tag)> PickRows(JsonArray? picks) =>
        (picks ?? new()).OfType<JsonObject>()
            .Select(p => (UUID.TryParse(p.Str("pick_id"), out var id) ? id : UUID.Zero, p.Str("name") ?? "a pick"))
            .Where(p => p.Item1 != UUID.Zero).OrderBy(p => p.Item2, StringComparer.OrdinalIgnoreCase)
            .Select(p => (p.Item2, p.Item1.ToString(), (object)new PickRow(p.Item1, p.Item2))).ToList();

    /// <summary>A profile's picks as a list: Enter reads one, the Applications key goes there; hers also edits, and Delete
    /// asks in Notices. Delete is last on the menu, its own letter, after what's pressed more.</summary>
    ListView PicksList(Form form, UUID owner, JsonArray? picks, bool mine)
    {
        var list = List(mine ? "myPicks" : "profilePicks", "Picks");
        Fill(list, PickRows(picks), mine ? "No picks yet." : "No picks.");
        PickRow? On() => list.FocusedItem?.Tag as PickRow;
        var menu = new ContextMenuStrip { Name = mine ? "myPickMenu" : "profilePickMenu", AccessibleName = "Actions" };
        menu.Items.Add(new ToolStripMenuItem("&Read it", null, (_, _) => { if (On() is { } p) _ = ReadPickAsync(owner, p); }) { ShortcutKeyDisplayString = "Enter" });
        menu.Items.Add(new ToolStripMenuItem("&Teleport there", null, (_, _) =>
        {
            if (On() is not { } p) return;
            if (!mine) form.Close();
            _ = GoToPickAsync(owner, p);
        }));
        if (mine)
        {
            menu.Items.Add(new ToolStripMenuItem("&Change its words...", null, (_, _) => { if (On() is { } p) _ = EditPickAsync(form, list, p); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("&Delete", null, (_, _) => { if (On() is { } p) _ = DeletePickAsync(form, list, p); }) { ShortcutKeyDisplayString = "Delete" });
        }
        MenuOn(list, menu);
        list.KeyDown += (_, e) =>
        {
            if (On() is not { } p) return;
            if (e.KeyData == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; _ = ReadPickAsync(owner, p); }
            else if (mine && e.KeyData == Keys.Delete) { e.Handled = e.SuppressKeyPress = true; _ = DeletePickAsync(form, list, p); }
        };
        list.Enter += (_, _) => { if (list.FocusedItem == null && list.Items.Count > 0) list.Items[0].Focused = true; };
        form.FormClosed += (_, _) => menu.Dispose();
        return list;
    }

    Task<JsonObject> PickOf(UUID owner, PickRow p) =>
        _ops.ReadAsync("TesselAgent", "pick", new JsonObject { ["pick_id"] = p.Id.ToString(), ["id"] = owner.ToString() });

    async Task ReadPickAsync(UUID owner, PickRow row)
    {
        var r = await PickOf(owner, row);
        if (PersonOps.Error(r) is { } e) { Reader?.Answer($"Couldn't read {row.Name}: {e}", keep: false); return; }
        var where = new[] { r.Str("parcel"), r.Str("region") }.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct().ToList();
        var about = r.Str("desc") is { Length: > 0 } d ? $" {d.Trim()}" : "";
        Reader?.Answer($"{r.Str("name") ?? row.Name}{(where.Count > 0 ? $", at {string.Join(", in ", where)}" : "")}.{about}", keep: false, from: LineFrom.People);
    }

    async Task GoToPickAsync(UUID owner, PickRow row)
    {
        var r = await PickOf(owner, row);
        if (PersonOps.Error(r) is { } e) { Answer($"Couldn't read {row.Name}: {e}"); return; }
        if (r.Str("region") is not { Length: > 0 } region || r["pos"] is not JsonArray { Count: >= 3 } pos) { Answer($"{row.Name} doesn't say where it is."); return; }
        await _cmd.GoToAsync(new Place(region, Json.AsDouble(pos[0]) ?? 128, Json.AsDouble(pos[1]) ?? 128, Json.AsDouble(pos[2]) ?? 30));
    }

    /// <summary>A profile's picks read again into its list; those now there.</summary>
    async Task<List<(string Text, string Key, object Tag)>> RefillPicksAsync(ListView list)
    {
        var r = await _ops.ReadAsync("TesselAgent", "profile", new JsonObject());
        var rows = PickRows(r["picks"] as JsonArray);
        if (PersonOps.Error(r) == null && !list.IsDisposed) Fill(list, rows, "No picks yet.");
        return rows;
    }

    async Task NewPickAsync(Form form, ListView list)
    {
        var name = Dialogs.Ask(form, "New pick where you stand", "Its name");
        if (name == null) { Reader?.Answer("No pick made.", keep: false); return; }
        var about = Dialogs.Ask(form, $"New pick: {name}", "What to say about it (or nothing)", allowEmpty: true);
        if (about == null) { Reader?.Answer("No pick made.", keep: false); return; }
        var r = await _ops.DoAsync("TesselAgent", "createPick", new JsonObject { ["name"] = name, ["desc"] = about });
        if (PersonOps.Error(r) is { } e) { Reader?.Answer($"No pick made: {e}.", keep: false); return; }
        var rows = await RefillPicksAsync(list);
        Reader?.Answer(rows.Any(p => p.Key == r.Str("pick_id"))
            ? $"Pick {name} made, here where you stand."
            : $"The grid didn't keep {name}: you may have as many picks as your account allows already.", keep: false);
    }

    async Task EditPickAsync(Form form, ListView list, PickRow row)
    {
        var r = await PickOf(_ops.Client?.Self.AgentID ?? UUID.Zero, row);
        if (PersonOps.Error(r) is { } e) { Reader?.Answer($"Couldn't read {row.Name}: {e}", keep: false); return; }
        var name = Dialogs.Ask(form, "Change a pick", "Its name", r.Str("name") ?? row.Name);
        if (name == null) { Reader?.Answer("Not changed.", keep: false); return; }
        var about = Dialogs.Ask(form, $"Change a pick: {name}", "What to say about it (or nothing)", r.Str("desc") ?? "", allowEmpty: true);
        if (about == null) { Reader?.Answer("Not changed.", keep: false); return; }
        var w = await _ops.DoAsync("TesselAgent", "createPick", new JsonObject { ["pick_id"] = row.Id.ToString(), ["name"] = name, ["desc"] = about });
        if (PersonOps.Error(w) is { } we) { Reader?.Answer($"Not changed: {we}.", keep: false); return; }
        await RefillPicksAsync(list);
        Reader?.Answer($"Changed {name}; it's still where it was.", keep: false);
    }

    async Task DeletePickAsync(Form form, ListView list, PickRow row)
    {
        var args = new JsonObject { ["pick_id"] = row.Id.ToString(), ["name"] = row.Name };
        if (await ConfirmedCallAsync(_ops.GatedAsync("TesselAgent", "deletePick", args), args, form) is not { } r) return;
        if (PersonOps.Error(r) is { } e) { Reader?.Answer($"Still there: {e}.", keep: false); return; }
        var rows = await RefillPicksAsync(list);
        Reader?.Answer(rows.Any(p => p.Key == row.Id.ToString()) ? $"The grid still has {row.Name}." : $"Pick {row.Name} deleted.", keep: false);
    }
}
