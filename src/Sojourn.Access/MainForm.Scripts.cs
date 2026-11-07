using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;
using Sojourn.Core;

namespace Sojourn.Access;

/// <summary>
/// Scripts past the editor's own keys (docs/access-app.md > Scripts): how a save's errors are worded, so the editor can go to
/// them (EditorForm's F8), and a script or notecard inside an object opened and saved through the same editor, the save waiting
/// for her confirmation as the gate has it. Claude Opus 5.5, 2026-11-07.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>What a new script starts as: the official viewer's own.</summary>
    const string NewScriptText = "default\n{\n    state_entry()\n    {\n        llSay(0, \"Hello, Avatar!\");\n    }\n\n    touch_start(integer total_number)\n    {\n        llSay(0, \"Touched.\");\n    }\n}\n";

    // "(3, 5) : ERROR : Syntax error" (Second Life's), "(0,35): Error: undefined name" (OpenSim's): both count from nought
    static readonly Regex CompilerLine = new(@"^\s*\((\d+),\s*(\d+)\)\s*:?\s*(?:error\s*:\s*)?(.+)$", RegexOptions.IgnoreCase);

    /// <summary>A compiler's lines as "Line 3, column 5: Syntax error" where they say where, as they came otherwise.</summary>
    static List<string> ScriptErrorLines(IEnumerable<string>? raw)
    {
        var lines = new List<string>();
        foreach (var e in raw ?? Array.Empty<string>())
        {
            var m = CompilerLine.Match(e);
            lines.Add(m.Success ? $"Line {int.Parse(m.Groups[1].Value) + 1}, column {int.Parse(m.Groups[2].Value) + 1}: {m.Groups[3].Value.Trim()}" : e.Trim());
        }
        return lines;
    }

    /// <summary>What a script's save says, and what goes in the editor's errors box.</summary>
    static (string Said, string? Errors, string? Now) ScriptSaved(List<string> errors) => errors.Count == 0
        ? ("Saved and compiled.", null, null)
        : ($"Saved; {errors.Count} {(errors.Count == 1 ? "error" : "errors")}, F8 goes to each: {string.Join(" ", errors.Take(3))}", string.Join("\r\n", errors), null);

    static string RpcError(JsonNode? r) => r?["rpc_error"]?["message"]?.ToString() ?? r?["error"]?.ToString() ?? "no answer";

    async Task<JsonObject> ContentRpcAsync(string method, JsonObject args)
    {
        var (_, r) = await Host!.Session.Scripts.RpcAsync(method, args, TimeSpan.FromSeconds(60), CancellationToken.None);
        return r as JsonObject ?? new JsonObject();
    }

    /// <summary>Enter on a script or notecard inside what's being edited: read from the object (a script needs copy and modify
    /// on it, as the viewer's editor asks) into the editor; a save goes back into the object.</summary>
    async Task OpenContentAsync(ContentItem c)
    {
        var prim = _buildId;
        bool script = c.Running != null;
        // a save that doesn't compile leaves a script stopped: one that was running goes on running once it compiles
        bool running = c.Running == true;
        var read = await ContentRpcAsync("object.content.get", new JsonObject { ["prim_id"] = prim.ToString(), ["item_id"] = c.Id.ToString() });
        if (read["result"] is not JsonObject got)
        {
            var why = RpcError(read);
            Answer($"Couldn't open {c.Name}: {(why.Contains("Insufficient permissions") ? "that takes copy and modify rights on it." : why)}");
            return;
        }
        EditorForm? editor = null;
        editor = new EditorForm(script ? "Script" : "Notecard", c.Name, got.Str("content") ?? "", async text =>
        {
            var args = new JsonObject { ["prim_id"] = prim.ToString(), ["item_id"] = c.Id.ToString(), ["content"] = text };
            if (script) args["running"] = running;
            if (await ConfirmedCallAsync(ContentRpcAsync("object.content.save", args), args, editor) is not { } saved) return ("Not saved.", null, null);
            if (saved["result"] is not JsonObject done) return ($"Not saved: {RpcError(saved)}", null, null);
            if (!script)
                return (done["moved_to_end"] is JsonArray { Count: > 0 } moved ? $"Saved. {string.Join(" and ", moved.Select(m => m?.ToString()))}: the place in the text was gone, so at the end now." : "Saved.", null, null);
            var diags = (done["diagnostics"] as JsonArray)?.OfType<JsonObject>().Select(d => d.Int("row", 0) is > 0 and var row
                ? $"Line {row}, column {d.Int("column", 0)}: {d.Str("message")}" : d.Str("message") ?? "").Where(x => x.Length > 0).ToList() ?? new();
            return ScriptSaved(ScriptErrorLines(diags));
        }, text => Reader?.Answer(text, keep: false));
        editor.OpenFirstLink = OpenFirstLink;
        editor.FormClosed += (_, _) =>
        {
            if (IsDisposed) return;
            Activate();
            _buildRows.Focus();
            _ = ShowBuildRowsAsync();
        };
        editor.Show(this);
    }
}
