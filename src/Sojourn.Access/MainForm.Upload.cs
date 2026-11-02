using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// Uploading on her own (M6's first part; the blind tester's partner logs in as her to do it today): Control+U, Build >
/// Upload a file, a folder's "Upload a file here" in the inventory, or `//upload [path]`. Windows' own Open window picks
/// the file (screen-reader users know it); then a window of standard controls says what it is before anything is sent
/// (its kind and size, a picture's pixels and what Second Life will resize it to, a sound's length and channels, an
/// animation's length, a model's parts, triangles and size in metres) and asks its name, its folder, and for an
/// animation its priority and loop, for a model its scale, detail levels, physics shape and textures, each with the
/// official uploader's default. Where uploads cost L$ the fee is confirmed by the person's own key in Notices (the gate
/// works it out and holds the upload to it); the outcome is said and the new item selected in the inventory. Through
/// TesselAgent uploadLook, upload and uploadMesh (Core's UploadOps). A sound over Second Life's 30 seconds is offered
/// as clips instead (MainForm.UploadClips.cs); MP3, FLAC, M4A and WMA are decoded by Windows first. Claude Opus 5.5,
/// 2026-11-01.
/// </summary>
public sealed partial class MainForm
{
    const string OpenFilter =
        "Pictures, sounds, animations and models|*.png;*.jpg;*.jpeg;*.tga;*.bmp;*.wav;*.ogg;*.mp3;*.flac;*.m4a;*.wma;*.bvh;*.anim;*.dae;*.gltf;*.glb;*.obj"
        + "|Pictures (PNG, JPEG, TGA, BMP)|*.png;*.jpg;*.jpeg;*.tga;*.bmp|Sounds (WAV, Ogg, MP3, FLAC, M4A, WMA)|*.wav;*.ogg;*.mp3;*.flac;*.m4a;*.wma"
        + "|Animations (BVH, anim)|*.bvh;*.anim|Models (glTF, GLB, Collada, OBJ)|*.gltf;*.glb;*.dae;*.obj|Every file|*.*";

    bool _uploading;

    /// <summary>Control+U and the menu: into the folder selected in the inventory if the focus is on one there, else the
    /// kind's own folder (offered both ways in the window).</summary>
    void UploadKey()
    {
        InventoryFolder? into = InInventory && _pane.Tree.SelectedNode?.Tag is InventoryFolder f ? f : null;
        _ = UploadAsync(into, null);
    }

    /// <summary>`//upload` and `//upload PATH` (a path with spaces needs no quotes; quotes are taken off).</summary>
    Task UploadWordsAsync(string words)
    {
        var path = words.Trim().Trim('"').Trim();
        return UploadAsync(null, path.Length > 0 ? Environment.ExpandEnvironmentVariables(path) : null);
    }

    async Task UploadAsync(InventoryFolder? into, string? path)
    {
        if (_ops?.Client == null) { Answer("Not in world yet: uploads go into your inventory once you're logged in."); return; }
        if (_uploading) { Answer("An upload is under way; one at a time."); return; }
        var keepTree = _pane1.Tree.SelectedNode?.Name;
        if (path == null)
        {
            using var pick = new OpenFileDialog
            {
                Title = "Upload to Second Life: a picture, sound, animation or model", Filter = OpenFilter, CheckFileExists = true,
                Multiselect = false,
            };
            if (pick.ShowDialog(this) != DialogResult.OK) { Reader?.Answer("Nothing uploaded.", keep: false); return; }
            path = pick.FileName;
        }
        if (!File.Exists(path)) { Answer($"There's no file at {path}."); return; }
        if (UploadOps.KindOf(path) == "" && !WindowsDecodes(path)) { Answer(UploadOps.WrongKind(path, SoundKinds)); return; }
        var file = Path.GetFileName(path);
        DecodedWav? decoded = null;
        try
        {
            // a sound Second Life doesn't take as it is (MP3, FLAC ...): Windows decodes it to a WAV first
            if (WindowsDecodes(path))
            {
                var decoding = DecodeToWavAsync(path);
                if (await Task.WhenAny(decoding, Task.Delay(500)) != decoding) Reader?.Answer($"Reading {file}.", keep: false);
                var (wav, why0) = await decoding;
                if (wav == null) { Answer(why0!); return; }
                decoded = wav;
                path = wav.Path;
            }

            // what it is, read here (nothing sent); a slow one (a model with textures, a long sound) says so
            var ask = new JsonObject { ["path"] = path };
            if (UploadOps.KindOf(path) == "sound") ask["clips"] = true;
            var reading = Task.Run(() => _ops.ReadAsync("TesselAgent", "uploadLook", ask));
            if (decoded == null && await Task.WhenAny(reading, Task.Delay(500)) != reading) Reader?.Answer($"Reading {file}.", keep: false);
            var look = await reading;
            if (PersonOps.Error(look) is { } why) { Answer(Plain(why)); return; }
            if (decoded != null) Decoded(look, file, decoded);

            var kind = look.Str("type") ?? "";
            var folders = UploadFolders(kind, into);
            if (kind == "sound" && look.Bool("too_long", false))
            {
                await UploadClipsAsync(look, path, file, into, folders, keepTree);
                return;
            }
            using var form = new UploadForm(look, path, folders, Costs(kind), _ops.Session.Uploads.Balance,
                args => _ops.ReadAsync("TesselAgent", "uploadLook", args), args => _ops.ReadAsync("TesselAgent", "uploadMesh", args), Answer, file);
            if (form.ShowDialog(this) != DialogResult.OK || form.Args is not { } chosen) { Reader?.Answer("Nothing uploaded.", keep: false); return; }
            await SendUploadAsync(kind, chosen, keepTree);
        }
        finally
        {
            if (decoded != null) TryDelete(Path.GetDirectoryName(decoded.Path)!);
        }
    }

    /// <summary>Whether uploads of this kind cost L$ here (a test's pretend fee counts).</summary>
    bool Costs(string kind) => Host?.UploadsCost == true || UploadOps.TestFees?.ContainsKey(kind) == true;

    /// <summary>Where it can go: the folder it was asked for (or selected in the inventory), and the kind's own.</summary>
    List<(string Words, UUID Id)> UploadFolders(string kind, InventoryFolder? into)
    {
        var list = new List<(string, UUID)>();
        var ft = kind switch { "texture" => FolderType.Texture, "sound" => FolderType.Sound, "animation" => FolderType.Animation, _ => FolderType.Object };
        var own = _ops.Session.Inventory.FolderFor(ft);
        if (into != null) list.Add((into.UUID == own ? into.Name : $"{into.Name}, where you chose", into.UUID));
        if (own != UUID.Zero && own != into?.UUID)
        {
            var name = _ops.Session.Inventory.Node(own)?.Name ?? ft.ToString();
            list.Add(($"{name}, where {kind switch { "texture" => "pictures", "sound" => "sounds", "animation" => "animations", _ => "models" }} go", own));
        }
        if (into == null && FolderAt(_pane1) is { } sel && sel.UUID != own && sel.UUID != Store?.RootFolder?.UUID)
            list.Add(($"{sel.Name}, selected in your inventory", sel.UUID));
        if (list.Count == 0 && Store?.RootFolder is { } root) list.Add((root.Name, root.UUID));
        return list;
    }

    async Task SendUploadAsync(string kind, JsonObject args, string? keepTree)
    {
        _uploading = true;
        var name = args.Str("name") ?? "it";
        try
        {
            bool mesh = kind == "mesh";
            Answer(mesh && Costs(kind) ? $"Asking Second Life the fee for {name}." : $"Uploading {name}.");
            var call = ConfirmedAsync("TesselAgent", mesh ? "uploadMesh" : "upload", args);
            // a long one (a big model, a slow grid) says it's still going, once
            if (await Task.WhenAny(call, Task.Delay(20000)) != call && Host?.Assistant.Panel.Read(p => p.Confirm) == null)
                Reader?.Answer($"Still uploading {name}.", keep: false);
            if (await call is not { } r) return;   // cancelled by her own key in Notices ("Cancelled." was said)
            if (PersonOps.Error(r) is { } e) { Answer(Plain(e)); return; }
            var folderId = UUID.TryParse(r.Str("folder_id"), out var fid) ? fid : UUID.Zero;
            var folderName = _ops.Session.Inventory.Node(folderId)?.Name ?? "your inventory";
            var what = kind switch
            {
                "texture" => $"a picture, {r.Int("width", 0)} by {r.Int("height", 0)}",
                "sound" => $"a sound, {Secs(r.Num("duration", 0))}",
                "animation" => $"an animation, {Secs(r.Num("duration", 0))}",
                _ => "a model",
            };
            var paid = r.Int("charged", 0) is > 0 and var fee ? $" L${fee} paid." : "";
            if (!UUID.TryParse(r.Str("item_id"), out var item)) { Answer($"Uploaded {name}, {what}, into {folderName}.{paid}"); return; }
            bool selected = await RevealAsync(item, folderId, keepTree);
            var where = selected && !(InInventory && _pane == _pane1) ? " It's selected in your inventory, Alt+4." : "";
            var next = mesh ? " Rez it from there: the Applications key, Rez on the ground." : "";
            Answer($"Uploaded {name}, {what}, into {folderName}.{paid}{where}{next}");
        }
        finally { _uploading = false; }
    }

    static string Secs(double s) => s.ToString(s < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + (s == 1 ? " second" : " seconds");

    /// <summary>An op's refusal as a sentence for the person (the upload ops already speak in words; the gate's and the
    /// dispatcher's may not).</summary>
    static string Plain(string e)
    {
        e = e.Trim();
        if (e.Length == 0) return "Not uploaded.";
        e = char.ToUpperInvariant(e[0]) + e[1..];
        if (!".!?".Contains(e[^1])) e += ".";
        return e.Contains("uploaded", StringComparison.OrdinalIgnoreCase) ? e : $"Not uploaded: {e}";
    }

    /// <summary>The new item put in the first pane's tree and selected there, its folders opened, unless the person is in
    /// that tree and has moved off where they were when the upload began (a list never changes under them). True when
    /// it's selected.</summary>
    async Task<bool> RevealAsync(UUID item, UUID folder, string? keepTree)
    {
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
        {
            try
            {
                while (Store?.Contains(item) != true)
                {
                    await _ops.Session.Inventory.ItemAsync(item, cts.Token);
                    if (Store?.Contains(item) == true) break;
                    await Task.Delay(300, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        }
        var p = _pane1;
        if (Store is not { } store || !store.Contains(item) || !p.ShownOnce || p.Mode != "")
        {
            RefreshInventory(folder);
            return false;
        }
        var tree = p.Tree;
        if (tree.Focused && tree.SelectedNode?.Name != keepTree) { RefreshInventory(folder); return false; }
        // the folders from the top down to the item's
        var chain = new List<UUID>();
        var root = store.RootFolder?.UUID ?? UUID.Zero;
        for (var at = folder; at != UUID.Zero && at != root && chain.Count < 32; at = store.TryGetValue<InventoryBase>(at, out var n) ? n.ParentUUID : UUID.Zero)
            chain.Insert(0, at);
        RefreshInventory(folder);
        var nodes = tree.Nodes;
        TreeNode? node = null;
        foreach (var f in chain)
        {
            node = nodes.Find(f.ToString(), false).FirstOrDefault();
            if (node == null) return false;
            if (!node.IsExpanded) node.Expand();
            nodes = node.Nodes;
        }
        var made = nodes.Find(item.ToString(), false).FirstOrDefault();
        if (made == null && node != null) { FillFolder(p, node, folder); made = node.Nodes.Find(item.ToString(), false).FirstOrDefault(); }
        if (made == null) return false;
        RealInput.Ours(() => tree.SelectedNode = made);
        return true;
    }
}

/// <summary>
/// The upload window: what the file is, read line by line in a read-only text where the focus starts (its first line the
/// gist), then Name, Description, the folder, the kind's choices with the official uploader's defaults, Upload and
/// Cancel. A model's scale says its new size; Weigh it asks the grid its fee and land impact (the official uploader's
/// "Calculate weights and fee"). Enter anywhere but the text boxes' own uploads; Escape cancels.
/// </summary>
sealed class UploadForm : Form
{
    readonly JsonObject _look;
    readonly string _path, _kind;
    readonly bool _costs;
    readonly int? _balance;
    readonly Func<JsonObject, Task<JsonObject>> _lookAgain, _weigh;
    readonly Action<string> _say;
    readonly TextBox _about;
    readonly LineBox _name, _desc;
    readonly ComboBox _folder;
    readonly List<(string Words, UUID Id)> _folders;
    readonly ComboBox? _priority, _detail, _physics;
    readonly CheckBox? _loop, _textures;
    readonly LineBox? _scale;
    readonly Button _send;
    string? _weighed;
    double _scaleNow = 1;
    readonly System.Windows.Forms.Timer _scaleTimer = new() { Interval = 700 };
    readonly string _file;

    /// <summary>The op's args once Upload is pressed.</summary>
    public JsonObject? Args { get; private set; }

    /// <param name="file">the file's name as the person chose it (a sound Windows decoded goes up from a WAV of it)</param>
    public UploadForm(JsonObject look, string path, List<(string Words, UUID Id)> folders, bool costs, int? balance,
        Func<JsonObject, Task<JsonObject>> lookAgain, Func<JsonObject, Task<JsonObject>> weigh, Action<string> say, string? file = null)
    {
        (_look, _path, _folders, _costs, _balance, _lookAgain, _weigh, _say) = (look, path, folders, costs, balance, lookAgain, weigh, say);
        _kind = look.Str("type") ?? "";
        file ??= Path.GetFileName(path);
        _file = file;
        Name = "upload";
        Text = AccessibleName = $"Upload {file}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ShowInTaskbar = false;

        _about = new TextBox
        {
            Name = "uploadAbout", AccessibleName = "About the file", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Width = 460, Height = 150, BackColor = SystemColors.Window,
        };
        _name = new LineBox { Name = "uploadName", AccessibleName = "Name", Width = 460, MaxLength = UploadOps.NameLength, Text = look.Str("name") ?? "" };
        _desc = new LineBox { Name = "uploadDesc", AccessibleName = "Description", Width = 460, MaxLength = UploadOps.DescLength };
        _folder = new ComboBox { Name = "uploadFolder", AccessibleName = "Into the folder", DropDownStyle = ComboBoxStyle.DropDownList, Width = 460 };
        foreach (var f in folders) _folder.Items.Add(f.Words);
        if (_folder.Items.Count > 0) _folder.SelectedIndex = 0;

        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(10), Dock = DockStyle.Fill };
        void Row(string label, Control c, bool top = false)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | (top ? AnchorStyles.Top : 0) }, 0, t.RowCount);
            t.Controls.Add(c, 1, t.RowCount);
            t.RowCount++;
        }
        Row("&About the file", _about, top: true);
        Row("&Name", _name);
        Row("&Description", _desc);
        Row("Into the &folder", _folder);

        if (_kind == "animation")
        {
            // the viewer's animation floater's two that matter most; the rest keep its defaults
            _priority = new ComboBox { Name = "uploadPriority", AccessibleName = "Priority", DropDownStyle = ComboBoxStyle.DropDownList, Width = 460 };
            _priority.Items.AddRange(new object[] { "0, the lowest", "1", "2, the usual (the official uploader's default)", "3", "4, over most others" });
            _priority.SelectedIndex = Math.Clamp(look.Int("priority", 2), 0, 4);
            _loop = new CheckBox { Name = "uploadLoop", Text = "&Loop: play it over and over until stopped", AccessibleName = "Loop", AutoSize = true, Checked = look.Bool("loop", false) };
            Row("P&riority", _priority);
            Row("", _loop);
        }
        else if (_kind == "mesh")
        {
            _scale = new LineBox { Name = "uploadScale", AccessibleName = "Scale", Width = 120, Text = "1" };
            _detail = new ComboBox { Name = "uploadDetail", AccessibleName = "Detail levels", DropDownStyle = ComboBoxStyle.DropDownList, Width = 460 };
            _detail.Items.AddRange(new object[]
            {
                "Made from the model: fewer triangles seen from far off (the official uploader's default)",
                "Full detail at every distance: costs more land impact",
            });
            _detail.SelectedIndex = 0;
            _physics = new ComboBox { Name = "uploadPhysics", AccessibleName = "Physics shape", DropDownStyle = ComboBoxStyle.DropDownList, Width = 460 };
            _physics.Items.AddRange(new object[]
            {
                "The model's outline, wrapped tight (a convex hull, the official uploader's default)",
                "A box around it: the simplest to walk into and bump",
            });
            _physics.SelectedIndex = 0;
            Row("&Scale", _scale);
            Row("Detail &levels", _detail);
            Row("&Physics shape", _physics);
            if (look.Int("textures", 0) > 0)
            {
                _textures = new CheckBox { Name = "uploadTextures", Text = "Include its &textures (each one a picture's upload)", AccessibleName = "Include its textures", AutoSize = true };
                Row("", _textures);
            }
            _scale.TextChanged += (_, _) => { _scaleTimer.Stop(); _scaleTimer.Start(); };
            _scaleTimer.Tick += async (_, _) => { _scaleTimer.Stop(); await RescaleAsync(); };
            // what was weighed no longer holds once a choice changes
            void Unweigh() { if (_weighed == null) return; _weighed = null; _about.Text = string.Join("\r\n", Lines()); }
            _detail.SelectedIndexChanged += (_, _) => Unweigh();
            _physics.SelectedIndexChanged += (_, _) => Unweigh();
            if (_textures != null) _textures.CheckedChanged += (_, _) => Unweigh();
        }

        var fee = look.Int("fee", -1);
        _send = new Button
        {
            Name = "uploadSend", AutoSize = true,
            Text = _kind != "mesh" && _costs && fee >= 0 ? $"&Upload for L${fee}" : "&Upload",
        };
        var cancel = new Button { Name = "uploadCancel", Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        row.Controls.Add(_send);
        if (_kind == "mesh")
        {
            var weighBtn = new Button { Name = "uploadWeigh", Text = "&Weigh it: fee and land impact", AutoSize = true };
            weighBtn.Click += async (_, _) => await WeighAsync();
            row.Controls.Add(weighBtn);
        }
        row.Controls.Add(cancel);
        t.Controls.Add(row, 1, t.RowCount);
        t.RowCount++;
        Controls.Add(t);
        AcceptButton = _send;
        CancelButton = cancel;
        _send.Click += (_, _) => Send();

        _about.Text = string.Join("\r\n", Lines());
        ActiveControl = _about;
        Shown += (_, _) => { _about.SelectionStart = 0; _about.SelectionLength = 0; };
        FormClosed += (_, _) => _scaleTimer.Dispose();
    }

    /// <summary>What the file is, a line each, the gist first.</summary>
    List<string> Lines()
    {
        var l = _look;
        var file = _file;
        var size = Bytes(l.Num("bytes", 0));
        var lines = new List<string>();
        switch (_kind)
        {
            case "texture":
            {
                int w = l.Int("width", 0), h = l.Int("height", 0), uw = l.Int("upload_width", 0), uh = l.Int("upload_height", 0);
                lines.Add($"{file}: a picture, {l.Str("format")}, {w} by {h} pixels, {size}.");
                lines.Add(l.Bool("resized", false)
                    ? $"Second Life needs each side a power of two, at most 1024 pixels, so it'll be resized to {uw} by {uh}."
                    : "Its size suits Second Life as it is: it won't be resized.");
                if (l.Bool("alpha", false)) lines.Add("It has see-through parts (transparency).");
                break;
            }
            case "sound":
            {
                int ch = l.Int("channels", 1), rate = l.Int("rate", 44100);
                var chs = ch switch { 1 => "mono", 2 => "stereo", _ => $"{ch} channels" };
                lines.Add($"{file}: a sound, {Secs(l.Num("seconds", 0))}, {chs}, {l.Str("format")}, {rate} Hz, {size}.");
                lines.Add($"Second Life takes sounds up to {l.Num("max_seconds", 30):0} seconds.");
                if (ch > 1 || rate != 44100 || file.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    lines.Add(ch > 1 ? "Second Life plays sounds in mono, so it'll be made mono, at 44,100 Hz." : "It'll be converted to Second Life's sound format, at 44,100 Hz.");
                break;
            }
            case "animation":
            {
                lines.Add($"{file}: an animation, {l.Str("format")}, {Secs(l.Num("seconds", 0))}, {Count(l.Int("joints", 0), "joint")} moving, {size}.");
                if (l["warnings"] is JsonArray warns && warns.Count > 0)
                    lines.Add("Notes from reading it: " + string.Join(" ", warns.Select(w => w?.ToString()).Take(4)));
                break;
            }
            case "mesh":
            {
                int parts = l.Int("parts", 1), faces = l.Int("faces", 0);
                lines.Add($"{file}: a model, {l.Str("format")}, {Count(parts, "part")}, {Count(faces, "face")}, {Count(l.Int("triangles", 0), "triangle")}, {size}.");
                lines.Add(SizeLine(l));
                if (l.Bool("rigged", false)) lines.Add("It's rigged to an avatar's skeleton: it's worn, and moves with the body.");
                if (l.Int("textures", 0) is > 0 and var tex)
                    lines.Add($"It has {Count(tex, "texture")}: Include its textures, below, uploads {(tex == 1 ? "it" : "them")} with it{(_costs ? ", each a picture's fee" : "")}; without, it's plain white.");
                if (l["warnings"] is JsonArray warns && warns.Count > 0)
                    lines.Add("Notes from reading it: " + string.Join(" ", warns.Select(w => w?.ToString()).Take(4)));
                break;
            }
        }
        lines.Add(FeeLine());
        if (_weighed != null) lines.Add(_weighed);
        return lines.Where(s => s.Length > 0).ToList();
    }

    string SizeLine(JsonObject l)
    {
        var s = l["size"] as JsonArray;
        double X(int i) => s != null && s.Count > i ? s[i]!.GetValue<double>() : 0;
        var line = $"About {M(X(0))} along X, {M(X(1))} along Y and {M(X(2))} high, at a scale of {N(l.Num("scale", 1))}.";
        if (l.Num("too_big") is { } fit)
            line += $" That's too big: Second Life allows up to 64 metres a side. A scale of {N(fit)} fits.";
        return line;
    }

    string FeeLine()
    {
        if (_kind == "mesh")
            return _costs
                ? "Second Life works out a model's fee and land impact: Weigh it, below, asks now, and uploading asks again before anything is paid."
                : "Uploading is free on this grid. Weigh it, below, asks the grid its land impact.";
        var fee = _look.Int("fee", 0);
        if (!_costs) return "Uploading is free on this grid.";
        var bal = _balance is { } b ? $" Your balance: L${b}." : "";
        return $"Uploading costs L${fee}, confirmed in Notices by your own key.{bal}";
    }

    async Task RescaleAsync()
    {
        if (_scale == null || !double.TryParse(_scale.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sc) || sc <= 0 || sc == _scaleNow) return;
        var r = await _lookAgain(new JsonObject { ["path"] = _path, ["scale"] = sc });
        if (IsDisposed || PersonOps.Error(r) is not null) return;
        _scaleNow = sc;
        _weighed = null;
        foreach (var k in new[] { "size", "scale", "too_big" })
        {
            _look.Remove(k);
            if (r[k] is { } v) _look[k] = v.DeepClone();
        }
        _about.Text = string.Join("\r\n", Lines());
        _say(SizeLine(_look));
    }

    async Task WeighAsync()
    {
        if (Choices() is not { } args) return;
        args["quote"] = true;
        _say("Weighing.");
        var r = await _weigh(args);
        if (IsDisposed) return;
        if (PersonOps.Error(r) is { } e) { _say(e); return; }
        if (r.Str("refusal") is { Length: > 0 } no) { _say(no); return; }
        var words = r["weights"] is JsonObject w
            ? string.Create(CultureInfo.InvariantCulture, $"Land impact {w.Num("land_impact", 0):0.#} when rezzed (download {w.Num("streaming", 0):0.#}, physics {w.Num("physics", 0):0.#}, server {w.Num("server", 0):0.#}).")
            : "The grid gave no land impact.";
        words += _costs ? $" The fee: L${r.Int("cost", 0)}." : "";
        _weighed = words;
        _about.Text = string.Join("\r\n", Lines());
        _say(words);
    }

    /// <summary>The op's args from what's chosen; null (said) when something can't be used.</summary>
    JsonObject? Choices()
    {
        var name = _name.Text.Trim();
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(_path);
        var a = new JsonObject { ["path"] = _path, ["name"] = name, ["type"] = _kind == "mesh" ? null : _kind };
        if (_desc.Text.Trim() is { Length: > 0 } d) a["desc"] = d;
        if (_folder.SelectedIndex >= 0 && _folder.SelectedIndex < _folders.Count) a["folder_id"] = _folders[_folder.SelectedIndex].Id.ToString();
        if (_priority != null) a["priority"] = _priority.SelectedIndex;
        if (_loop != null) a["loop"] = _loop.Checked;
        if (_scale != null)
        {
            if (!double.TryParse(_scale.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sc) || sc <= 0 || sc > 1000)
            {
                _say("The scale is a number: 1 as the model is, 0.5 half the size, 2 twice.");
                _scale.Focus();
                return null;
            }
            a["scale"] = sc;
            a["lods"] = _detail!.SelectedIndex == 0;
            a["physics"] = _physics!.SelectedIndex == 0 ? "hull" : "box";
            a["textures"] = _textures?.Checked == true;
            a["timeout"] = 140;
        }
        if (a["type"] is null) a.Remove("type");
        return a;
    }

    void Send()
    {
        if (Choices() is not { } a) return;
        Args = a;
        DialogResult = DialogResult.OK;
    }

    static string Count(int n, string what) => $"{n.ToString("N0", CultureInfo.InvariantCulture)} {what}{(n == 1 ? "" : "s")}";
    static string M(double m) => m.ToString(m < 10 ? "0.##" : "0.#", CultureInfo.InvariantCulture) + (m == 1 ? " metre" : " metres");
    static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static string Secs(double s) => s.ToString(s < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + (s == 1 ? " second" : " seconds");
    static string Bytes(double b) => b >= 1 << 20 ? (b / 1048576).ToString("0.#", CultureInfo.InvariantCulture) + " MB" : b >= 1024 ? $"{(int)(b / 1024)} KB" : $"{b:0} bytes";
}
