using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;

namespace Sojourn.Access;

/// <summary>
/// Gestures, as other viewers have them (the blind tester's couldn't be played at all, 2026-10-31): her active
/// gestures (the inventory's gesture items she activated, the login's list) with their triggers, played from
/// Control+Shift+G's menu or World > Gestures, or by typing a trigger in nearby chat, which plays it instead of saying
/// it (LLGestureMgr's rule, Core's GestureOps.Trigger: the first word of the line that's a trigger, replaced by the
/// gesture's replacement text or left out). In the inventory a gesture's words say "active", Enter plays it and its
/// Applications key has Play, Activate and Deactivate. A gesture's chat goes out as her own nearby chat, so it's read
/// back as her own lines are. Core plays the steps (GestureOps.Start).
/// </summary>
public sealed partial class MainForm
{
    readonly ContextMenuStrip _gestureMenu = new() { Name = "gestureMenu", AccessibleName = "Gestures" };
    ToolStripMenuItem _gesturesItem = null!;

    /// <summary>The active gestures by name: item id, name, trigger (null until its asset has been read).</summary>
    List<(UUID Item, string Name, string? Trigger)> ActiveGestures()
    {
        var list = new List<(UUID, string, string?)>();
        if (_ops?.Client is not { } c) return list;
        foreach (var (item, asset) in c.Self.ActiveGestures)
        {
            var name = Store?.TryGetValue<InventoryItem>(item, out var it) == true ? it.Name : null;
            if (string.IsNullOrEmpty(name)) continue;   // (not in the inventory as loaded: nothing to call it)
            list.Add((item, name, Sojourn.Core.Agents.GestureOps.KnownTrigger(asset) is { Length: > 0 } t ? t : null));
        }
        return list.OrderBy(g => g.Item2, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A menu's items for the active gestures: each its name, its trigger shown as the item's key (typing it in
    /// nearby chat plays it too); clicked or Enter, played.</summary>
    void FillGestures(ToolStripItemCollection items)
    {
        items.Clear();
        Host?.Session.Gestures.WarmTriggers();
        var all = ActiveGestures();
        if (all.Count == 0)
        {
            items.Add(new ToolStripMenuItem("No active gestures (a gesture's Applications key in the inventory activates it)") { Enabled = false });
            return;
        }
        int n = 0;
        foreach (var (item, name, trigger) in all)
        {
            var id = item;
            items.Add(new ToolStripMenuItem(name, null, (_, _) => _ = PlayGestureAsync(id)) { ShortcutKeyDisplayString = trigger, Name = $"gesture{++n}" });
        }
    }

    /// <summary>Control+Shift+G: the active gestures as a menu where the focus is; Escape goes back.</summary>
    void ShowGestures()
    {
        if (_ops?.Client is null) { Answer("Not in a region yet."); return; }
        FillGestures(_gestureMenu.Items);
        if (_gestureMenu.Items.Count == 1 && !_gestureMenu.Items[0].Enabled)
        {
            Reader?.Answer("No active gestures. In the inventory (Alt+4), a gesture's Applications key has Activate.", keep: false);
            return;
        }
        var at = ActiveControl ?? (Control)this;
        _gestureMenu.Show(at, new Point(10, Math.Min(at.Height, 30)));
    }

    /// <summary>Plays a gesture by its item (the person's own: their key or menu).</summary>
    async Task PlayGestureAsync(UUID item)
    {
        var r = await _ops.DoAsync("LLGesture", "startGesture", new JsonObject { ["id"] = item.ToString() });
        Reader?.Answer(PersonOps.Error(r) is { } e ? $"Didn't play: {e}" : $"Playing {r.Str("name") ?? "the gesture"}.", keep: false);
    }

    /// <summary>A nearby line as it's sent: a trigger in it plays its gesture, and what's left (the replacement text, or
    /// the rest of the line) is said; null to say the line as it is. Channel lines ("/5 ...") aren't looked at, as in
    /// the viewer.</summary>
    string? Triggered(string text)
    {
        if (Host?.Session is not { } s || System.Text.RegularExpressions.Regex.IsMatch(text, @"^/-?\d+(\s|$)")) return null;
        if (s.Gestures.Trigger(text) is not { } hit) return null;
        Reader?.Answer($"Playing {hit.Name}.", keep: false);
        return hit.Revised;
    }

    /// <summary>Activate or deactivate gesture items from the inventory: said, and every pane's words follow.</summary>
    async Task ActivateGesturesAsync(List<InventoryItem> gestures, bool on)
    {
        var ids = new JsonArray(gestures.Select(g => (JsonNode)g.UUID.ToString()).ToArray());
        var r = await _ops.DoAsync("TesselInventory", on ? "activateGesture" : "deactivateGesture", new JsonObject { ["item_ids"] = ids });
        if (PersonOps.Error(r) is { } e) { Answer($"{(on ? "Not activated" : "Not deactivated")}: {e}"); return; }
        if (on && gestures.Count == 1 && Host?.Session.Gestures is { } gs)
            try { await Task.Run(() => gs.DecodeAssetAsync(gestures[0].AssetUUID, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* said without its trigger */ }
        var names = Join(gestures.Select(g => g.Name).ToList());
        Answer(on ? $"{names} active{TriggerWords(gestures)}." : $"{names} {(gestures.Count == 1 ? "is" : "are")} no longer active.");
        foreach (var p in Panes) Relabel(p, p.WornShown);
    }

    /// <summary>", typing /clap plays it" for one gesture whose trigger is known.</summary>
    string TriggerWords(List<InventoryItem> gestures)
    {
        if (gestures.Count != 1) return "";
        var t = Sojourn.Core.Agents.GestureOps.KnownTrigger(gestures[0].AssetUUID);
        return t is { Length: > 0 } ? $": typing {t} in nearby chat plays it" : "";
    }

    bool GestureActive(InventoryItem g) => _ops?.Client?.Self.ActiveGestures.ContainsKey(g.UUID) == true;
}
