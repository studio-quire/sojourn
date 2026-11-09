using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Access;
using Microsoft.Extensions.Logging;

namespace Sojourn.Access;

/// <summary>
/// Back up to disk (Radegast's Backup, InventoryBackup.cs): a folder, or the whole inventory, saved into a folder on this
/// PC the person picks in Windows' own window: inventory.csv listing everything in it as Radegast's does (type, folder,
/// name, description, created, creator, last owner), and the text of every notecard (.txt) and script (.lsl) she made
/// herself and may copy, change and give, in folders as in the inventory. Only what she made: Second Life's rules for
/// viewers let an export the official viewer lacks take only content whose creator is the person (docs/tpv-policy.md,
/// 2.b); Radegast asks only for full rights. The rest is listed, not saved. One at a time, off the window's thread;
/// said as it starts and when it's done. Claude Opus 5.5, 2026-11-08.
/// </summary>
public sealed partial class MainForm
{
    bool _backingUp;
    (int Done, int Of) _backupAt;

    async Task BackupAsync(InventoryFolder? folder)
    {
        if (_ops?.Client is not { } c || Store is not { RootFolder: { } root } store) { Answer("Not in world yet: the inventory isn't loaded."); return; }
        if (_backingUp) { Answer($"A backup is under way: {_backupAt.Done} of {_backupAt.Of} notecards and scripts saved so far."); return; }
        var top = folder ?? root;
        var what = folder == null ? "your inventory" : FolderWords(folder);
        string? into;
        using (var pick = new FolderBrowserDialog
        {
            Description = $"Where to save the backup of {what}", UseDescriptionForTitle = true, ShowNewFolderButton = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        })
        {
            if (pick.ShowDialog(this) != DialogResult.OK) { Reader?.Answer("Not backed up.", keep: false); return; }
            into = pick.SelectedPath;
        }
        _backingUp = true;
        try
        {
            var dir = Unique(Path.Combine(into, SafeName($"{(folder == null ? "Inventory" : what)} backup {DateTime.Now.ToString("yyyy-MM-dd HH-mm", CultureInfo.InvariantCulture)}")));
            var me = c.Self.AgentID;
            var trash = c.Inventory.FindFolderForType(FolderType.Trash);
            var names = Host!.Session.Names;
            var (rows, save) = await Task.Run(() =>
            {
                var rows = new List<(InventoryItem Item, string Path)>();
                var save = new List<(InventoryItem Item, string Path)>();
                Walk(store, top.UUID, "", trash, CancellationToken.None, (n, path) =>
                {
                    if (n is not InventoryItem it) return true;
                    rows.Add((it, path));
                    // (a link's own creator and rights aren't what it links to, and a link's text is its original's: left to the original)
                    if (it.AssetType is AssetType.Notecard or AssetType.LSLText && it.CreatorID == me && FullRights(it)) save.Add((it, path));
                    return true;
                });
                return (rows, save);
            });
            static string Texts(int n) => n == 1 ? "1 notecard or script" : $"{n} notecards and scripts";
            var saving = save.Select(s => s.Item.UUID).ToHashSet();
            int others = rows.Count(r => r.Item.AssetType is AssetType.Notecard or AssetType.LSLText && !saving.Contains(r.Item.UUID));
            Answer($"Backing up {what}: a list of the {Count(rows.Count, "item")} in it, and the text of {Texts(save.Count)} you made, into {dir}. "
                   + "It's said when it's done.");
            Directory.CreateDirectory(dir);
            // the creators' and last owners' names, asked of the grid where not known (as a search by creator does)
            var people = rows.SelectMany(r => new[] { r.Item.CreatorID, r.Item.LastOwnerID }).Where(id => id != UUID.Zero && !names.Has(id)).Distinct().ToList();
            if (people.Count > 0) await ResolveCreatorsAsync(c, names, people, CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(dir, "inventory.csv"), Csv(rows, names), new UTF8Encoding(true));
            int saved = 0, failed = 0;
            _backupAt = (0, save.Count);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (it, path) in save)
            {
                bool script = it.AssetType == AssetType.LSLText;
                var r = await _ops.ReadAsync("TesselInventory", script ? "readScript" : "readNotecard", new JsonObject { ["item_id"] = it.UUID.ToString() });
                if (PersonOps.Error(r) is { } e || r.Str("text") is not { } text)
                {
                    failed++;
                    _ops.Session.Log.LogInformation("backup: {Name} not read: {Why}", it.Name, PersonOps.Error(r) ?? "no text");
                }
                else
                {
                    var folderDir = Path.Combine(new[] { dir }.Concat(path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafeName)).ToArray());
                    Directory.CreateDirectory(folderDir);
                    var file = Path.Combine(folderDir, SafeName(it.Name) + (script ? ".lsl" : ".txt"));
                    for (int i = 2; !used.Add(file); i++) file = Path.Combine(folderDir, $"{SafeName(it.Name)} ({i}){(script ? ".lsl" : ".txt")}");
                    await File.WriteAllTextAsync(file, text, new UTF8Encoding(false));
                    var when = Utc(it.CreationDate);
                    if (when.Year > 1970) { File.SetCreationTimeUtc(file, when); File.SetLastWriteTimeUtc(file, when); }
                    saved++;
                }
                _backupAt = (saved + failed, save.Count);
            }
            var parts = new List<string> { $"Backed up {what}: the list of {Count(rows.Count, "item")} in inventory.csv" };
            if (save.Count > 0) parts.Add($"{Texts(saved)} saved{(failed > 0 ? $", {failed} couldn't be read" : "")}");
            if (others > 0) parts.Add($"{Texts(others)} made by others listed only, as Second Life's rules for viewers ask");
            Answer(string.Join("; ", parts) + $". In {dir}.");
            _ops.Session.Log.LogInformation("backup of {What}: {Rows} listed, {Saved} saved, {Failed} not read, {Others} by others", what, rows.Count, saved, failed, others);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Answer($"The backup stopped: Windows couldn't write there ({e.Message}).");
        }
        finally { _backingUp = false; }
    }

    static bool FullRights(InventoryItem it)
    {
        const PermissionMask full = PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Transfer;
        return (it.Permissions.OwnerMask & full) == full;
    }

    /// <summary>Radegast's inventory.csv: a header, then a row per item, every field quoted.</summary>
    static string Csv(List<(InventoryItem Item, string Path)> rows, Sojourn.Core.World.NameCache names)
    {
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        string Who(UUID id) => id == UUID.Zero ? "" : names.Complete(id, id.ToString());
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", new[] { "Type", "Path", "Name", "Description", "Created", "Creator", "Last Owner" }.Select(Q)));
        foreach (var (it, path) in rows)
            sb.AppendLine(string.Join(",", new[]
            {
                TypeWord(it), "/" + path, it.Name, it.Description ?? "", Utc(it.CreationDate).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Who(it.CreatorID), Who(it.LastOwnerID),
            }.Select(Q)));
        return sb.ToString();
    }

    /// <summary>A name Windows takes for a file or folder: its forbidden characters as underscores, no trailing dots or
    /// spaces, no device names, at most 100 characters.</summary>
    static string SafeName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(name.Select(ch => bad.Contains(ch) ? '_' : ch).ToArray()).Trim().TrimEnd('.', ' ');
        if (s.Length > 100) s = s[..100].TrimEnd('.', ' ');
        if (s.Length == 0) s = "_";
        var stem = s.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])) s = "_" + s;
        return s;
    }

    static string Unique(string dir)
    {
        var path = dir;
        for (int i = 2; Directory.Exists(path) || File.Exists(path); i++) path = $"{dir} ({i})";
        return path;
    }
}
