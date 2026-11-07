using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;

namespace Sojourn.Access;

/// <summary>
/// The block list (docs/access-app.md > Blocking): a person or thing blocked from their menu or by //block, and the list
/// by //blocks or Control+Shift+Y, Enter or Delete taking one off. A notice's Block button could block something from
/// the start and nothing could undo it here: the tester's number key blocked a ski ball's object on 2026-11-05. Written
/// by Claude Opus 5.5, 2026-11-06.
/// </summary>
public sealed partial class MainForm
{
    async Task BlockAsync(UUID id, string name, string type)
    {
        var r = await _ops.DoAsync("TesselAgent", "mute", new JsonObject { ["id"] = id.ToString(), ["name"] = name, ["type"] = type });
        Answer(PersonOps.Error(r) is { } e ? $"Not blocked: {e}" : $"Blocked {name}. Control+Shift+Y lists what you've blocked.");
    }

    /// <summary>//block NAME: someone nearby, else someone in a conversation or a friend, else something nearby, else
    /// anyone on the grid by name (someone sending unwanted IMs is rarely nearby).</summary>
    async Task BlockWordsAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) { Answer("//block NAME: anyone by name, or something nearby. //blocks lists what you've blocked."); return; }
        if (await _ops.FindPersonAsync(name) is { } p) { await BlockAsync(p.Id, p.Name, "resident"); return; }
        if (await FindForSoundAsync(name) is { Group: false } known) { await BlockAsync(known.Id, known.Name, "resident"); return; }
        var thing = (await _ops.ObjectsAsync(96, 5, name)).FirstOrDefault();
        if (thing != null) await BlockAsync(thing.Id, thing.Name, "object");
        else if (await FindSomeoneAsync(name) is { } grid) await BlockAsync(grid.Id, grid.Name, "resident");
    }

    /// <summary>//unblock NAME: off the list by the name it's listed under (the whole name or part of it).</summary>
    async Task UnblockWordsAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) { await ShowBlocksAsync(); return; }
        var found = (await BlockedAsync()).FirstOrDefault(b => b.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (found == null) { Answer($"Nothing called {name} on your block list."); return; }
        await UnblockAsync(found);
    }

    sealed record Blocked(UUID Id, string Name, string Type);

    async Task<List<Blocked>> BlockedAsync()
    {
        var r = await _ops.ReadAsync("TesselAgent", "mutes", new JsonObject());
        return (r["mutes"] as JsonArray ?? new()).OfType<JsonObject>()
            .Select(o => new Blocked(UUID.TryParse(o.Str("id"), out var id) ? id : UUID.Zero, o.Str("name") ?? "", o.Str("type") ?? ""))
            .ToList();
    }

    async Task UnblockAsync(Blocked b)
    {
        var r = await _ops.DoAsync("TesselAgent", "unmute", new JsonObject { ["id"] = b.Id.ToString(), ["name"] = b.Name });
        Answer(PersonOps.Error(r) is { } e ? $"Still blocked: {e}" : $"Unblocked {BlockedName(b)}.");
    }

    static string BlockedName(Blocked b) => b.Name.Length > 0 ? b.Name : "something without a name";

    async Task ShowBlocksAsync()
    {
        var blocked = await BlockedAsync();
        if (blocked.Count == 0) { Answer("Nothing is blocked."); return; }
        using var form = new Form
        {
            Name = "blocks", Text = "Blocked", AccessibleName = "Blocked", Size = new Size(560, 400),
            StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false,
        };
        var list = new ListBox { Name = "blocksList", AccessibleName = "Blocked. Enter or Delete unblocks one.", Dock = DockStyle.Fill };
        list.Items.AddRange(blocked.Select(b => (object)$"{BlockedName(b)}, {(b.Type == "resident" ? "a person" : b.Type == "object" ? "an object" : "by name")}").ToArray());
        list.SelectedIndex = 0;
        var unblock = new Button { Name = "blocksUnblock", Text = "Unblock", AutoSize = true, DialogResult = DialogResult.OK };
        var close = new Button { Name = "blocksClose", Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        form.AcceptButton = unblock;
        form.CancelButton = close;
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) { e.Handled = true; form.DialogResult = DialogResult.OK; } };
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom };
        row.Controls.Add(unblock);
        row.Controls.Add(close);
        form.Controls.Add(list);
        form.Controls.Add(row);
        form.ActiveControl = list;
        if (form.ShowDialog(this) == DialogResult.OK && list.SelectedIndex >= 0) await UnblockAsync(blocked[list.SelectedIndex]);
    }
}
