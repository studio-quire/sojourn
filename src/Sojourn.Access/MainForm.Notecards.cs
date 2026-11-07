using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;

namespace Sojourn.Access;

/// <summary>
/// What a notecard holds: a landmark, a gift, another card (SL's welcome cards carry them). Listed under the text, a Tab
/// away; Enter copies one into the inventory, into the folder for its kind, and on a landmark or a notecard Enter again
/// teleports or opens it. Their places in the text read as markers, "[landmark: Harbour]": the session's
/// <see cref="Sojourn.Core.Agents.NotecardFile"/> does that, and keeps them on every save.
/// </summary>
public sealed partial class MainForm
{
    sealed class Embedded(string id, string name, string kind)
    {
        public string Id => id;
        public string Name => name;
        public string Kind => kind;
        public InventoryItem? Copy;
    }

    /// <summary>The list for a card that holds something (null when it holds nothing), and what to say on opening it.</summary>
    (ListView? List, string? Said) EmbeddedList(InvPane p, InventoryItem card, JsonObject read, Func<Form?> editor)
    {
        var held = (read["embedded"] as JsonArray ?? new()).OfType<JsonObject>()
            .Select(e => new Embedded(e.Str("item_id") ?? "", e.Str("name") ?? "an item", e.Str("kind") ?? "item")).ToList();
        if (held.Count == 0) return (null, null);
        var list = List("editorEmbedded", "Embedded in this notecard");
        list.Dock = DockStyle.Fill;
        foreach (var e in held) list.Items.Add(new ListViewItem($"{e.Name}, {e.Kind}") { Name = e.Id, Tag = e });
        list.Items[0].Selected = true;
        list.Enter += (_, _) => { if (list.FocusedItem == null) list.Items[0].Focused = true; };   // (no handle until it's shown, so not before)

        var menu = new ContextMenuStrip { Name = "embeddedMenu", AccessibleName = "Actions" };
        Embedded? On() => list.FocusedItem?.Tag as Embedded;
        var copy = new ToolStripMenuItem("&Copy into your inventory", null, (_, _) => { if (On() is { } e) _ = CopyEmbeddedAsync(card, e); });
        var teleport = new ToolStripMenuItem("&Teleport there", null, (_, _) => { if (On() is { } e) _ = UseEmbeddedAsync(p, card, e, editor()); });
        var open = new ToolStripMenuItem("&Open", null, (_, _) => { if (On() is { } e) _ = UseEmbeddedAsync(p, card, e, editor()); });
        menu.Items.AddRange(new ToolStripItem[] { copy, teleport, open });
        menu.Opening += (_, _) =>
        {
            teleport.Visible = On()?.Kind == "landmark";
            open.Visible = On()?.Kind == "notecard";
            copy.ShortcutKeyDisplayString = On()?.Copy == null ? "Enter" : "";
            teleport.ShortcutKeyDisplayString = open.ShortcutKeyDisplayString = On()?.Copy != null ? "Enter" : "";
        };
        MenuOn(list, menu);
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || On() is not { } it) return;
            e.Handled = e.SuppressKeyPress = true;
            _ = it.Copy == null ? CopyEmbeddedAsync(card, it) : UseEmbeddedAsync(p, card, it, editor());
        };
        var words = string.Join("; ", held.Select(e => $"{e.Name}, {e.Kind}"));
        return (list, $"{(held.Count == 1 ? "It holds 1 thing" : $"It holds {held.Count} things")}: {words}. Tab reaches {(held.Count == 1 ? "it" : "them")}.");
    }

    async Task<bool> CopyEmbeddedAsync(InventoryItem card, Embedded e)
    {
        var r = await _ops.DoAsync("TesselInventory", "copyFromNotecard", new JsonObject { ["item_id"] = card.UUID.ToString(), ["embedded_id"] = e.Id });
        if (PersonOps.Error(r) is { } err || !UUID.TryParse(r.Str("item_id"), out var id))
        {
            Answer($"Couldn't copy {e.Name}: {PersonOps.Error(r) ?? "the grid sent nothing back"}");
            return false;
        }
        e.Copy = await _ops.Session.Inventory.ItemAsync(id, CancellationToken.None);
        var folder = e.Copy is { } made && Store?.TryGetValue<InventoryBase>(made.ParentUUID, out var f) == true ? f.Name : "your inventory";
        var next = e.Kind switch { "landmark" => " Enter again teleports there.", "notecard" => " Enter again opens it.", _ => "" };
        Answer($"Copied {e.Name} into {folder}.{next}");
        if (e.Copy is { } it) RefreshInventory(it.ParentUUID);
        return e.Copy != null;
    }

    /// <summary>A landmark teleported by, a notecard opened; copied out first if it hasn't been (an embedded item can't be
    /// used from inside the card).</summary>
    async Task UseEmbeddedAsync(InvPane p, InventoryItem card, Embedded e, Form? from)
    {
        if (e.Copy == null && !await CopyEmbeddedAsync(card, e)) return;
        if (e.Kind == "landmark") await _cmd.TeleportLandmarkAsync(e.Copy!.UUID, e.Name);
        else if (e.Kind == "notecard") await OpenAsync(p, e.Copy!, from);
        else Answer($"{e.Name} is in your inventory already, copied.");
    }
}
