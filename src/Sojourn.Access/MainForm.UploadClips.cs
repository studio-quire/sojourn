using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Media;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Sojourn.Access;

/// <summary>
/// A sound longer than Second Life's 30 seconds, offered as clips instead of refused (a song for a scripted instrument,
/// which plays the clips in order): a window says how many clips it makes, their names, lengths and fees, with the clip
/// length (29.9 seconds, so none is refused), their name, how the cuts fall (exactly, for players on a timer, or at the
/// quietest moment near each mark), the folder (a new one named after them) and a notecard listing them. One
/// confirmation in Notices for the whole set where uploads cost; each clip's upload said as it lands ("Clip 3 of 5
/// uploaded."), then where they are. Through TesselAgent uploadClips (Core's UploadOps, Media/SoundClips.cs). Sounds
/// Second Life doesn't take as they are (MP3, FLAC, M4A, WMA) are decoded to a WAV by Windows' own Media Foundation
/// first, so nothing is bundled for them. Claude Opus 5.5, 2026-11-01.
/// </summary>
public sealed partial class MainForm
{
    const string SoundKinds = "WAV, Ogg, MP3, FLAC, M4A or WMA";
    static readonly string[] WindowsSoundExt = { ".mp3", ".flac", ".m4a", ".aac", ".wma" };

    static bool WindowsDecodes(string path) => WindowsSoundExt.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>A sound Windows can play decoded to a 16-bit WAV at 44.1 kHz in a folder of its own under %TEMP% (the
    /// caller deletes it), its channels and rate as the file had them; or why it can't be.</summary>
    static async Task<(DecodedWav? Wav, string? Why)> DecodeToWavAsync(string path)
    {
        var file = Path.GetFileName(path);
        var dir = Path.Combine(Path.GetTempPath(), "SojournAccess-uploads", Guid.NewGuid().ToString("N")[..12]);
        try
        {
            Directory.CreateDirectory(dir);
            var src = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            var from = await MediaEncodingProfile.CreateFromFileAsync(src);
            if (from.Audio is not { } audio) { TryDelete(dir); return (null, $"{file} has no sound in it that Windows can read."); }
            int channels = (int)Math.Max(1, audio.ChannelCount), rate = (int)audio.SampleRate;
            var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.High);
            profile.Audio = AudioEncodingProperties.CreatePcm(SoundConvert.SampleRate, (uint)Math.Min(channels, 2), 16);
            var folder = await StorageFolder.GetFolderFromPathAsync(dir);
            var dst = await folder.CreateFileAsync(Path.GetFileNameWithoutExtension(path) + ".wav", CreationCollisionOption.ReplaceExisting);
            var prep = await new MediaTranscoder().PrepareFileTranscodeAsync(src, dst, profile);
            if (!prep.CanTranscode)
            {
                TryDelete(dir);
                return (null, $"Windows can't read {file} ({prep.FailureReason}). Save it as a WAV and upload that.");
            }
            await prep.TranscodeAsync();
            return (new DecodedWav(dst.Path, Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), channels, rate, new FileInfo(path).Length), null);
        }
        catch (Exception e)
        {
            TryDelete(dir);
            // Windows N comes without Media Foundation, and a damaged file fails here
            return (null, $"Windows couldn't read {file}: {e.Message.Trim().TrimEnd('.')}. Save it as a WAV and upload that.");
        }
    }

    sealed record DecodedWav(string Path, string Format, int Channels, int Rate, long Bytes);

    /// <summary>The look of a decoded file said as the file she chose: its format, channels, rate and size.</summary>
    static void Decoded(JsonObject look, string file, DecodedWav d)
    {
        look["file"] = file;
        look["format"] = d.Format;
        look["channels"] = d.Channels;
        look["rate"] = d.Rate;
        look["bytes"] = d.Bytes;
        look["decoded"] = true;
    }

    static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The clips window, then the upload of the set.</summary>
    async Task UploadClipsAsync(JsonObject look, string path, string file, InventoryFolder? into, List<(string Words, UUID Id)> folders, string? keepTree)
    {
        // a new folder named after them, in the first place offered (where she chose, else Sounds); or straight into one
        var places = new List<(string Words, UUID Id, bool New)>();
        if (folders.Count > 0)
        {
            var first = folders[0].Id;
            var firstName = _ops.Session.Inventory.Node(first)?.Name ?? "your inventory";
            places.Add(($"A new folder named after the clips, in {firstName}", first, true));
        }
        places.AddRange(folders.Select(f => (f.Words, f.Id, false)));
        using var form = new ClipsForm(look, path, file, places, Costs("sound") || UploadOps.TestFees?.ContainsKey("clip") == true,
            _ops.Session.Uploads.Balance, args => _ops.ReadAsync("TesselAgent", "uploadLook", args), Answer);
        if (form.ShowDialog(this) != DialogResult.OK || form.Args is not { } chosen) { Reader?.Answer("Nothing uploaded.", keep: false); return; }
        await SendClipsAsync(chosen, form.Count, file, keepTree);
    }

    async Task SendClipsAsync(JsonObject args, int count, string file, string? keepTree)
    {
        _uploading = true;
        var name = args.Str("name") ?? "it";
        void Progress(JsonObject p)
        {
            var line = $"Clip {p.Int("index", 0)} of {p.Int("count", 0)} uploaded.";
            if (IsDisposed) return;
            BeginInvoke(() => Reader?.Answer(line));
        }
        var uploads = _ops.Session.Uploads;
        uploads.ClipUploaded += Progress;
        try
        {
            Answer($"Uploading {name} as {count} clips.");
            if (await ConfirmedAsync("TesselAgent", "uploadClips", args) is not { } r) return;   // cancelled in Notices
            if (PersonOps.Error(r) is { } e) { Answer(Plain(e)); return; }
            var clips = r["clips"] as JsonArray ?? new JsonArray();
            int n = clips.Count;
            string first = (clips.FirstOrDefault() as JsonObject)?.Str("name") ?? "", last = (clips.LastOrDefault() as JsonObject)?.Str("name") ?? "";
            var folderId = UUID.TryParse(r.Str("folder_id"), out var fid) ? fid : UUID.Zero;
            var where = r.Bool("new_folder", false)
                ? $"into a new folder, {r.Str("folder_name")}, in {r.Str("parent_name")}"
                : $"into {r.Str("folder_name")}";
            var paid = r.Int("charged", 0) is > 0 and var fee ? $" L${fee} paid." : "";
            var note = r.Str("notecard_name") is { Length: > 0 } nc ? $" A notecard, {nc}, lists them in order."
                : r.Str("notecard_error") is { Length: > 0 } ne ? $" {ne}" : "";
            var said = $"Uploaded {name} as {n} clips, {first} to {last}, {UploadWords.Seconds(r.Num("seconds", 0))} in all, {where}.{paid}{note}";
            if ((clips.FirstOrDefault() as JsonObject)?.Str("item_id") is { } firstItem && UUID.TryParse(firstItem, out var item))
            {
                bool selected = await RevealAsync(item, folderId, keepTree);
                if (selected && !(InInventory && _pane == _pane1)) said += $" {first} is selected in your inventory, Alt+4.";
            }
            Answer(said);
        }
        finally
        {
            uploads.ClipUploaded -= Progress;
            _uploading = false;
        }
    }
}

/// <summary>
/// The clips window: what they'll be, read line by line in a read-only text where the focus starts (the gist first:
/// how many clips, their names, the fees, "Upload them as clips?"), then the clips' name, their length, how the cuts
/// fall, the folder, the notecard, Upload and Cancel. A change to the name, length or cuts works the clips out again
/// and, for the length and cuts, says the new count. Enter anywhere but the text boxes' own uploads; Escape cancels.
/// </summary>
sealed class ClipsForm : Form
{
    readonly JsonObject _look;
    JsonObject _plan;
    readonly string _path, _file;
    readonly bool _costs;
    readonly int? _balance;
    readonly Func<JsonObject, Task<JsonObject>> _lookAgain;
    readonly Action<string> _say;
    readonly List<(string Words, UUID Id, bool New)> _places;
    readonly TextBox _about;
    readonly LineBox _name, _length;
    readonly ComboBox _cuts, _folder;
    readonly CheckBox _notecard;
    readonly Button _send;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 600 };
    bool _sayNext;

    /// <summary>The op's args once Upload is pressed, and how many clips were shown.</summary>
    public JsonObject? Args { get; private set; }
    public int Count => _plan.Int("count", 0);

    public ClipsForm(JsonObject look, string path, string file, List<(string Words, UUID Id, bool New)> places, bool costs, int? balance,
        Func<JsonObject, Task<JsonObject>> lookAgain, Action<string> say)
    {
        (_look, _path, _file, _places, _costs, _balance, _lookAgain, _say) = (look, path, file, places, costs, balance, lookAgain, say);
        _plan = look["clips"] as JsonObject ?? new JsonObject();
        Name = "uploadClips";
        Text = AccessibleName = $"Upload {file} as clips";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ShowInTaskbar = false;

        _about = new TextBox
        {
            Name = "clipsAbout", AccessibleName = "About the clips", Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Width = 460, Height = 170, BackColor = SystemColors.Window,
        };
        _name = new LineBox { Name = "clipsName", AccessibleName = "Name of the clips", Width = 460, MaxLength = UploadOps.NameLength - 3, Text = _plan.Str("base") ?? look.Str("name") ?? "" };
        _length = new LineBox { Name = "clipsLength", AccessibleName = "Clip length, in seconds", Width = 120, Text = N(_plan.Num("clip_seconds", SoundClips.DefaultSeconds)) };
        _cuts = new ComboBox { Name = "clipsCuts", AccessibleName = "Cuts", DropDownStyle = ComboBoxStyle.DropDownList, Width = 460 };
        _cuts.Items.AddRange(new object[]
        {
            "Exactly at each clip length: all the same length, for a player that starts each clip on a timer",
            "At the quietest moment just before each mark: lengths vary a little, for a player that waits for each clip to end",
        });
        _cuts.SelectedIndex = 0;
        _folder = new ComboBox { Name = "clipsFolder", AccessibleName = "Into the folder", DropDownStyle = ComboBoxStyle.DropDownList, Width = 460 };
        foreach (var p in places) _folder.Items.Add(p.Words);
        if (_folder.Items.Count > 0) _folder.SelectedIndex = 0;
        _notecard = new CheckBox
        {
            Name = "clipsNotecard", AccessibleName = "Make a notecard listing the clips in order", AutoSize = true,
            Text = "Make a n&otecard listing the clips in order (many players read one)",
        };

        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(10), Dock = DockStyle.Fill };
        void Row(string label, Control c, bool top = false)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | (top ? AnchorStyles.Top : 0) }, 0, t.RowCount);
            t.Controls.Add(c, 1, t.RowCount);
            t.RowCount++;
        }
        Row("&About the clips", _about, top: true);
        Row("&Name of the clips", _name);
        Row("Clip &length, in seconds", _length);
        Row("&Cuts", _cuts);
        Row("Into the &folder", _folder);
        Row("", _notecard);

        _send = new Button { Name = "clipsSend", AutoSize = true };
        var cancel = new Button { Name = "clipsCancel", Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        row.Controls.Add(_send);
        row.Controls.Add(cancel);
        t.Controls.Add(row, 1, t.RowCount);
        t.RowCount++;
        Controls.Add(t);
        AcceptButton = _send;
        CancelButton = cancel;
        _send.Click += async (_, _) => await SendAsync();

        _name.TextChanged += (_, _) => Changed(say: false);
        _length.TextChanged += (_, _) => Changed(say: true);
        _cuts.SelectedIndexChanged += (_, _) => Changed(say: true);
        _timer.Tick += async (_, _) => { _timer.Stop(); await ReplanAsync(); };

        ShowPlan(_plan);
        ActiveControl = _about;
        Shown += (_, _) => { _about.SelectionStart = 0; _about.SelectionLength = 0; };
        FormClosed += (_, _) => _timer.Dispose();
    }

    void Changed(bool say)
    {
        _sayNext |= say;
        _timer.Stop();
        _timer.Start();
    }

    void ShowPlan(JsonObject plan)
    {
        _plan = plan;
        int n = plan.Int("count", 0), total = plan.Int("fee", 0);
        _send.Text = _costs ? $"&Upload as {n} clips for L${total}" : $"&Upload as {n} clips";
        _about.Text = string.Join("\r\n", Lines());
    }

    /// <summary>What the clips will be, a line each, the gist first.</summary>
    List<string> Lines()
    {
        var l = _look;
        var p = _plan;
        int n = p.Int("count", 0), each = p.Int("fee_each", 0), total = p.Int("fee", 0);
        var names = p["names"] as JsonArray;
        string first = names?.FirstOrDefault()?.ToString() ?? "", last = names?.LastOrDefault()?.ToString() ?? "";
        var secs = N(p.Num("clip_seconds", SoundClips.DefaultSeconds));
        var fees = _costs ? $", L${each} each, L${total} in all" : "";
        var lines = new List<string>
        {
            $"{_file} is {UploadWords.Seconds(l.Num("seconds", 0))} long, and Second Life takes sounds up to {l.Num("max_seconds", 30):0} seconds: "
            + $"it can go up as {n} clips of up to {secs} seconds, named {first} to {last}{fees}. Upload them as clips?",
        };
        int ch = l.Int("channels", 1), rate = l.Int("rate", 44100);
        var chs = ch switch { 1 => "mono", 2 => "stereo", _ => $"{ch} channels" };
        lines.Add($"The file: a sound, {chs}, {l.Str("format")}, {rate} Hz, {Bytes(l.Num("bytes", 0))}{(l.Bool("decoded", false) ? ", read by Windows" : "")}.");
        var lengths = p["seconds"] as JsonArray;
        var lastLen = lengths?.LastOrDefault()?.GetValue<double>() ?? 0;
        lines.Add(p.Str("cuts") == "quiet"
            ? $"Each is cut at the quietest moment in the {SoundClips.QuietWindowSeconds:0} seconds before its mark, so they run {N(p.Num("shortest", 0))} to {N(p.Num("longest", 0))} seconds: for a player that waits for each clip to end."
            : $"Each is cut exactly {secs} seconds after the one before (the last is {N(lastLen)} seconds): all the same length, for a player that starts each on a timer.");
        lines.Add($"Each is made mono at 44,100 Hz, as Second Life plays sounds, with a few milliseconds' fade at each cut so it doesn't click.");
        if (n >= 10) lines.Add($"Numbered from 01, so a player that takes its clips in name order plays them in order.");
        var bal = _balance is { } b ? $" Your balance: L${b}." : "";
        lines.Add(_costs ? $"The fees are confirmed once, in Notices, by your own key.{bal}" : "Uploading is free on this grid.");
        return lines;
    }

    /// <summary>The clips worked out again for what's chosen; said (the gist) when the length or cuts changed.</summary>
    async Task<bool> ReplanAsync()
    {
        if (Choices(quiet: true) is not { } a) return false;
        a["clips"] = true;
        var r = await _lookAgain(a);
        if (IsDisposed) return false;
        if (PersonOps.Error(r) is { } e) { _say(e); return false; }
        if (r["clips"] is not JsonObject plan) return false;
        ShowPlan(plan.DeepClone().AsObject());
        if (_sayNext) _say(Lines()[0]);
        _sayNext = false;
        return true;
    }

    /// <summary>The op's args from what's chosen; null (said unless quiet) when something can't be used.</summary>
    JsonObject? Choices(bool quiet = false)
    {
        if (!double.TryParse(_length.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs)
            || secs < SoundClips.MinSeconds || secs > SoundConvert.MaxSeconds)
        {
            if (!quiet)
            {
                _say($"The clip length is a number of seconds, from 1 to 30: {N(SoundClips.DefaultSeconds)} keeps each just under Second Life's limit.");
                _length.Focus();
            }
            return null;
        }
        var name = _name.Text.Trim();
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(_file);
        var a = new JsonObject
        {
            ["path"] = _path, ["name"] = name, ["clip_seconds"] = secs, ["cuts"] = _cuts.SelectedIndex == 1 ? "quiet" : "exact",
        };
        if (_folder.SelectedIndex >= 0 && _folder.SelectedIndex < _places.Count)
        {
            var place = _places[_folder.SelectedIndex];
            a["folder_id"] = place.Id.ToString();
            a["new_folder"] = place.New;
        }
        if (_notecard.Checked) a["notecard"] = true;
        return a;
    }

    async Task SendAsync()
    {
        // what's sent is what was shown: a change still being worked out is finished first
        if (_timer.Enabled)
        {
            _timer.Stop();
            if (!await ReplanAsync()) { Choices(); return; }
        }
        if (Choices() is not { } a) return;
        Args = a;
        DialogResult = DialogResult.OK;
    }

    static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    static string Bytes(double b) => b >= 1 << 20 ? (b / 1048576).ToString("0.#", CultureInfo.InvariantCulture) + " MB" : b >= 1024 ? $"{(int)(b / 1024)} KB" : $"{b:0} bytes";
}
