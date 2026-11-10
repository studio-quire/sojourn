using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Access;

/// <summary>
/// Export and import (Radegast's Export to XML and DAE and its Import object; Firestorm's Backup and Import Linkset).
/// Export saves one of her objects, always the whole of it, where she chooses in Windows' own Save window, in one of
/// three kinds: a backup (.oxp, Firestorm's format, which Firestorm and this app rebuild), Radegast's XML (its textures
/// beside it as .jp2 files in a folder named as the file), or a Collada model (.dae, its textures as PNGs beside it;
/// Session's Collada.cs), which this app's own upload takes back as a mesh. Only what she made: Second Life's rules for
/// viewers (docs/tpv-policy.md, 2.b) let an export the official viewer lacks take only content whose creator is the
/// person, every part and texture of it, so anything else is refused before the Save window, saying which part and why,
/// and textures others made go as plywood, said (Core's Backups, Firestorm's rules). Import rebuilds a backup or a
/// Radegast export 2 metres ahead, resting on what's there, as the backups' restore does (its textures uploaded again
/// only where this grid lacks them, which costs on Second Life and is confirmed in Notices first), and it becomes what's
/// edited. Build menu, Objects' Applications key on her own things, the build editor's export and import, //export and
/// //import. SOJOURN_TEST_FILES stands in for both windows in tests. Claude Opus 5.5, 2026-11-09.
/// </summary>
public sealed partial class MainForm
{
    static readonly string? ExportTestFiles = Environment.GetEnvironmentVariable("SOJOURN_TEST_FILES");

    static readonly (string Kind, string Ext, string Filter, string Said)[] ExportKinds =
    {
        ("oxp", "oxp", "Backup for Firestorm and Sojourn Access (*.oxp)|*.oxp", "a backup Firestorm and Sojourn Access can rebuild"),
        ("xml", "xml", "Radegast's export (*.xml)|*.xml", "Radegast's XML"),
        ("dae", "dae", "Collada model (*.dae)|*.dae", "a Collada model"),
    };

    bool _exporting;
    string? _exportDir;

    /// <summary>`//export [NAME] [as KIND]` and the build editor's `export [as KIND]`: what's edited, else the nearest of
    /// hers by that name.</summary>
    async Task ExportWordsAsync(string words)
    {
        var w = words.Trim().Trim('"');
        string? kind = null;
        var m = System.Text.RegularExpressions.Regex.Match(w, @"^(?<name>.*?)\s*\bas\s+(a\s+|an\s+)?(?<kind>oxp|backup|xml|radegast|dae|collada|model)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
        {
            w = m.Groups["name"].Value.Trim();
            kind = m.Groups["kind"].Value.ToLowerInvariant() switch { "backup" => "oxp", "radegast" => "xml", "collada" or "model" => "dae", var k => k };
        }
        if (w.Length == 0)
        {
            if (BuildPrim() is not { } p || Host?.Session is not { } s || s.Region is not { } region) { Answer("Nothing to export: edit it first (Control+B), or //export and its name."); return; }
            var root = s.Objects.RootOf(region, p);
            await ExportThingAsync(root.ID, root.Properties?.Name is { Length: > 0 } rn ? rn : "Object", kind);
            return;
        }
        var t = (await _ops.ObjectsAsync(96, 20, w)).FirstOrDefault(x => x.Mine);
        if (t == null) { Answer($"Nothing of yours called {w} nearby."); return; }
        await ExportThingAsync(t.Id, t.Name, kind);
    }

    /// <summary>One of her objects to a file: refused, with why, if any of it isn't hers to export; else the Save window
    /// (the kind chosen there, or by the words), then written and said.</summary>
    async Task ExportThingAsync(UUID id, string name, string? kind = null)
    {
        if (_exporting) { Answer("An export is under way; it's said when done."); return; }
        if (Host?.Session is not { } s) { Answer("Not in world yet."); return; }
        var check = await _ops.ReadAsync("SojournBuild", "canExport", new JsonObject { ["id"] = id.ToString() });
        if (PersonOps.Error(check) is { } ce) { Answer($"{Cap(name)} can't be exported: {ce}."); return; }
        if (check["exportable"]?.GetValue<bool>() != true)
        {
            var why = (check["why"] as JsonArray ?? new()).Select(x => WhyWords(x?.ToString() ?? "")).Where(x => x.Length > 0).ToList();
            var rule = (check.Str("rules") ?? "").StartsWith("Second Life")
                ? "Second Life's rules for viewers let you export only what you made yourself, every part and texture of it."
                : "This grid doesn't let those be exported.";
            Answer($"{Cap(name)} can't be exported: {string.Join("; ", why.Take(4))}{(why.Count > 4 ? $"; and {why.Count - 4} more parts" : "")}. {rule}");
            return;
        }
        string path;
        int k = Math.Max(0, Array.FindIndex(ExportKinds, x => x.Kind == kind));
        if (ExportTestFiles != null) path = Path.Combine(ExportTestFiles, SafeFileName(name) + "." + ExportKinds[k].Ext);
        else
        {
            using var d = new SaveFileDialog
            {
                Title = $"Export {name}", FileName = SafeFileName(name), Filter = string.Join("|", ExportKinds.Select(x => x.Filter)), FilterIndex = k + 1,
                DefaultExt = ExportKinds[k].Ext, AddExtension = true, OverwritePrompt = true,
                InitialDirectory = _exportDir ?? Backups.Dir,
            };
            Directory.CreateDirectory(Backups.Dir);
            if (d.ShowDialog(this) != DialogResult.OK) { Reader?.Answer("Not exported.", keep: false); return; }
            path = d.FileName;
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            k = Array.FindIndex(ExportKinds, x => x.Ext == ext) is >= 0 and var byExt ? byExt : Math.Clamp(d.FilterIndex - 1, 0, ExportKinds.Length - 1);
            _exportDir = Path.GetDirectoryName(path);
        }
        var kindNow = ExportKinds[k];
        _exporting = true;
        try
        {
            Answer($"Exporting {name} as {kindNow.Said}.");
            var e = await Task.Run(() => s.Backups.ExportAsync(id, CancellationToken.None));
            var said = await Task.Run(() => WriteExport(e, path, kindNow.Kind));
            var left = e.LeftOut.Count > 0 ? $" Left out: {string.Join("; ", e.LeftOut)}." : "";
            Answer($"Exported {name}, {said}: {FileWhere(path)}.{left}");
        }
        catch (LeapError le) { Answer($"{Cap(name)} can't be exported: {PersonOps.Words(le.Message)}."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Answer($"Couldn't write {FileWhere(path)}: {ex.Message}");
        }
        finally { _exporting = false; }
    }

    /// <summary>The export written in its kind; what went, in words.</summary>
    static string WriteExport(Backups.Exported e, string path, string kind)
    {
        static string Parts(int n) => n == 1 ? "1 part" : $"{n} parts";
        static string Textures(int n) => n == 1 ? "1 texture" : $"{n} textures";
        switch (kind)
        {
            case "xml":
            {
                File.WriteAllText(path, e.RadegastXml());
                if (e.Textures.Count == 0) return $"{Parts(e.Prims.Count)}, as Radegast's XML";
                var dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path));
                Directory.CreateDirectory(dir);
                foreach (var (tex, (_, j2c)) in e.Textures) File.WriteAllBytes(Path.Combine(dir, tex + ".jp2"), j2c);
                return $"{Parts(e.Prims.Count)}, as Radegast's XML, its {Textures(e.Textures.Count)} in the folder {Path.GetFileName(dir)} beside it";
            }
            case "dae":
            {
                var w = Sojourn.Session.Building.Collada.Write(e, path);
                e.LeftOut.AddRange(w.LeftOut);
                return $"{Parts(w.Prims)}, {w.Triangles:N0} triangles, as a Collada model"
                       + (w.Textures > 0 ? $", its {Textures(w.Textures)} beside it as pictures" : "");
            }
            default:
                File.WriteAllBytes(path, e.Oxp());
                return $"{Parts(e.Prims.Count)}{(e.Textures.Count > 0 ? $" and {Textures(e.Textures.Count)}" : "")}, as a backup Firestorm and Sojourn Access can rebuild";
        }
    }

    /// <summary>canExport's reasons as she hears them ("Seat was made by someone else").</summary>
    static string WhyWords(string why)
    {
        var colon = why.LastIndexOf(": ", StringComparison.Ordinal);
        if (colon < 0) return why;
        var (part, reason) = (why[..colon], why[(colon + 2)..]);
        return reason switch
        {
            var r when r.StartsWith("made by someone else") => $"{part} was made by someone else",
            var r when r.StartsWith("not yours to export") => $"{part} isn't yours to export on this grid",
            var r when r.StartsWith("a mesh") => $"{part} is a mesh, which viewers don't export from Second Life",
            var r when r.StartsWith("its sculpt map") => $"{part}'s sculpt map was made by someone else",
            var r when r.StartsWith("its creator didn't arrive") => $"who made {part} hasn't come from the region yet (try again)",
            _ => why,
        };
    }

    static string SafeFileName(string n)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(n.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return s.Length == 0 ? "Object" : s.Length > 60 ? s[..60] : s;
    }

    static string FileWhere(string path) => $"{Path.GetFileName(path)} in {(Path.GetFileName(Path.GetDirectoryName(path)) is { Length: > 0 } dir ? dir : Path.GetPathRoot(path))}";

    // --- import --------------------------------------------------------------------------------------------------------

    bool _importing;

    /// <summary>`//import [PATH]`, the build editor's `import`, Build > Import: the Open window when no path is given.</summary>
    async Task ImportWordsAsync(string words)
    {
        var path = words.Trim().Trim('"');
        if (path.Length == 0)
        {
            using var d = new OpenFileDialog
            {
                Title = "Import an object from a file",
                Filter = "Objects saved to a file (*.oxp, *.xml)|*.oxp;*.xml|Backups (*.oxp)|*.oxp|Radegast's exports (*.xml)|*.xml|All files (*.*)|*.*",
                CheckFileExists = true, Multiselect = false, InitialDirectory = _exportDir ?? Backups.Dir,
            };
            if (d.ShowDialog(this) != DialogResult.OK) { Reader?.Answer("Nothing imported.", keep: false); return; }
            path = d.FileName;
            _exportDir = Path.GetDirectoryName(path);
        }
        await ImportAsync(path);
    }

    async Task ImportAsync(string path)
    {
        if (_importing) { Answer("An import is under way; it's said when done."); return; }
        if (!File.Exists(path)) { Answer($"There's no file {path}."); return; }
        var quote = await _ops.ReadAsync("SojournBuild", "restore", new JsonObject { ["path"] = path, ["quote"] = true });
        if (PersonOps.Error(quote) is { } qe) { Answer($"Couldn't import {Path.GetFileName(path)}: {qe}."); return; }
        int prims = quote.Int("prims", 0), uploads = quote.Int("uploads", 0);
        var spot = new PlaceWords.Spot(Ahead: 2, Right: 0, North: 0, East: 0, Up: 0, Here: false, Relative: false);
        if (await SpotAsync(spot, null) is not { } at) { Answer("Not in a region yet."); return; }
        _importing = true;
        try
        {
            Answer($"Importing {Path.GetFileName(path)}: {(prims == 1 ? "1 part" : $"{prims} parts")}"
                   + (uploads > 0 ? $", {(uploads == 1 ? "1 texture" : $"{uploads} textures")} to upload again" : "") + ". It's said when it's done.");
            var args = new JsonObject { ["path"] = path, ["pos"] = Xyz(at.X, at.Y, at.Z + 1) };
            // what costs waits for her confirmation; the rest runs as hers, with no call limit (a big build takes minutes)
            var r = quote.Int("cost", 0) > 0 ? await ConfirmedAsync("SojournBuild", "restore", args) : await _ops.DoAsync("SojournBuild", "restore", args);
            if (r == null) return;
            if (PersonOps.Error(r) is { } e) { Answer($"Couldn't import {Path.GetFileName(path)}: {e}."); return; }
            var roots = (r["root_ids"] as JsonArray ?? new()).Select(x => UUID.TryParse(x?.ToString(), out var u) ? u : UUID.Zero).Where(u => u != UUID.Zero).ToList();
            if (roots.Count == 0) { Answer($"Nothing was rebuilt from {Path.GetFileName(path)}."); return; }
            await Task.Delay(500);
            var bottom = await BottomAsync(roots[0]);
            await _ops.DoAsync("TesselBuild", "modify", new JsonObject { ["id"] = roots[0].ToString(), ["pos"] = Xyz(at.X, at.Y, at.Z + bottom) });
            var name = (await _ops.ThingAsync(roots[0]))?.Name ?? Path.GetFileNameWithoutExtension(path);
            SetBuildTarget(roots[0]);
            _buildWhere = null;
            var more = roots.Count > 1 ? $", and {(roots.Count == 2 ? "1 more object" : $"{roots.Count - 1} more objects")} beside it" : "";
            var paid = r.Int("charged", 0) is > 0 and var l ? $" L${l} paid for {r.Int("uploaded", 0)} textures uploaded again." : "";
            Answer($"Imported {name}, {(prims == 1 ? "1 part" : $"{prims} parts")}, {PlaceWords.Say(spot)}{OnWhat(at)}{more}. Editing it (Control+B).{paid}");
        }
        finally { _importing = false; }
    }
}
