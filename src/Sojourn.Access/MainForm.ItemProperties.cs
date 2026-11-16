using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;

namespace Sojourn.Access;

/// <summary>
/// An inventory item's properties, as the official viewer's Item Properties and Radegast's details pane have them: its
/// name and description, what the next owner may do, shared with the group, anyone can copy. Only with modify rights on
/// it (the viewer's rule); next-owner rights only up to her own, and a next owner who can't copy can always transfer (as
/// Second Life keeps it); anyone can copy only where she can copy and transfer. A change of permissions goes through her
/// confirmation (Confirmations' TesselInventory update), after the name and description, which change at once. The blind
/// tester asked whether objects can be edited in the inventory as in the viewer (2026-11-15): their build no (rezzed or
/// worn, as there), these yes.
/// </summary>
public sealed partial class MainForm
{
    async Task PropertiesAsync(InventoryItem picked)
    {
        var t = Target(picked);
        var p = t.Permissions;
        if ((p.OwnerMask & PermissionMask.Modify) == 0)
        {
            Answer($"{t.Name} is no-modify, so its name, description and permissions stay as they are. Details has them.");
            return;
        }
        var desc0 = (t.Description == "(No Description)" ? "" : t.Description ?? "").Trim();
        bool Has(PermissionMask m, PermissionMask bit) => (m & bit) != 0;
        var was = new Props(t.Name, desc0, Has(p.NextOwnerMask, PermissionMask.Modify), Has(p.NextOwnerMask, PermissionMask.Copy),
            Has(p.NextOwnerMask, PermissionMask.Transfer), Has(p.GroupMask, PermissionMask.Modify | PermissionMask.Copy), Has(p.EveryoneMask, PermissionMask.Copy));
        var mine = (Copy: Has(p.OwnerMask, PermissionMask.Copy), Transfer: Has(p.OwnerMask, PermissionMask.Transfer));
        if (ItemPropertiesForm.Ask(this, t.Name, was, mine) is not { } now || now == was) { Reader?.Answer("Nothing changed.", keep: false); return; }

        var said = new List<string>();
        var words = new JsonObject { ["id"] = t.UUID.ToString() };
        if (now.Name.Trim() is { Length: > 0 } name && name != was.Name) { words["name"] = name; said.Add($"named {name}"); }
        if (now.Desc != was.Desc) { words["desc"] = now.Desc; said.Add(now.Desc.Length > 0 ? "its description changed" : "its description cleared"); }
        var perms = new JsonObject { ["id"] = t.UUID.ToString() };
        if ((now.NextModify, now.NextCopy, now.NextTransfer) != (was.NextModify, was.NextCopy, was.NextTransfer))
            perms["next_perms"] = (now.NextModify ? "M" : "") + (now.NextCopy ? "C" : "") + (now.NextTransfer ? "T" : "");
        if (now.Shared != was.Shared) perms["group_perms"] = now.Shared ? "MCV" : "";
        if (now.AnyoneCopy != was.AnyoneCopy) perms["everyone_copy"] = now.AnyoneCopy;

        // the name and description first, at once: cancelling the permissions' confirmation leaves them changed
        if (words.Count > 1)
        {
            var w = await _ops.DoAsync("TesselInventory", "update", words);
            if (PersonOps.Error(w) is { } e) { Answer($"Not changed: {e}"); return; }
        }
        if (perms.Count > 1)
        {
            var r = await ConfirmedAsync("TesselInventory", "update", perms);
            if (r == null || PersonOps.Error(r) != null)
            {
                var why = r == null ? "not changed" : $"not changed: {PersonOps.Error(r)}";
                Answer(said.Count > 0 ? $"Saved {t.Name}: {string.Join("; ", said)}. Its permissions {why}." : $"Its permissions {why}.");
                await RefreshAfterAsync();
                return;
            }
            // what it has now, as the op left it (the rights she can give clip what was asked)
            var kept = r["perms"] as JsonObject;
            if (perms.ContainsKey("next_perms"))
            {
                var next = kept?.Str("next") ?? perms.Str("next_perms") ?? "";
                var may = new[] { ('M', "modify"), ('C', "copy"), ('T', "transfer") }.Where(x => next.Contains(x.Item1)).Select(x => x.Item2).ToList();
                said.Add(may.Count == 0 ? "the next owner may only move it" : $"the next owner may {string.Join(", ", may)}");
            }
            if (perms.ContainsKey("group_perms")) said.Add(now.Shared ? "shared with the group" : "not shared with the group");
            if (perms.ContainsKey("everyone_copy")) said.Add(now.AnyoneCopy ? "anyone can copy it" : "not anyone can copy it");
        }
        Answer($"Saved {t.Name}: {string.Join("; ", said)}.");
        await RefreshAfterAsync();

        async Task RefreshAfterAsync()
        {
            await Task.Delay(500);
            RefreshInventory(picked.ParentUUID);
        }
    }

    sealed record Props(string Name, string Desc, bool NextModify, bool NextCopy, bool NextTransfer, bool Shared, bool AnyoneCopy);

    static class ItemPropertiesForm
    {
        /// <summary>The window: Name and Description boxes, then the switches, OK (Enter) and Cancel (Escape). Next owner's
        /// copy and transfer only where she has them herself; transfer kept on while the next owner can't copy; anyone can
        /// copy only where she can copy and transfer. Null when cancelled.</summary>
        public static Props? Ask(IWin32Window owner, string itemName, Props was, (bool Copy, bool Transfer) mine)
        {
            var title = $"Properties of {itemName}";
            using var f = new Form
            {
                Name = "itemProperties", Text = title, AccessibleName = title, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false,
                MaximizeBox = false, StartPosition = FormStartPosition.CenterParent, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ShowInTaskbar = false, KeyPreview = true,
            };
            var t = new TableLayoutPanel { ColumnCount = 2, RowCount = 9, AutoSize = true, Padding = new Padding(10), Dock = DockStyle.Fill };
            f.Controls.Add(t);
            var name = new LineBox { Name = "propName", AccessibleName = "Name", Text = was.Name, Width = 360, MaxLength = 63 };
            var desc = new LineBox { Name = "propDesc", AccessibleName = "Description", Text = was.Desc, Width = 360, MaxLength = 127 };
            t.Controls.Add(new Label { Text = "&Name", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            t.Controls.Add(name, 1, 0);
            t.Controls.Add(new Label { Text = "&Description", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            t.Controls.Add(desc, 1, 1);
            CheckBox Box(string id, string text, bool on, bool may, string? why, int row)
            {
                var c = new CheckBox { Name = id, Text = text, Checked = on && may, Enabled = may, AutoSize = true };
                if (!may && why != null) c.AccessibleDescription = why;
                t.Controls.Add(c, 1, row);
                return c;
            }
            const string NotYours = "You don't have this right yourself, so you can't give it.";
            var mod = Box("propNextModify", "Next owner can &modify", was.NextModify, true, null, 2);
            var copy = Box("propNextCopy", "Next owner can &copy", was.NextCopy, mine.Copy, NotYours, 3);
            var transfer = Box("propNextTransfer", "Next owner can &transfer", was.NextTransfer, mine.Transfer, NotYours, 4);
            var shared = Box("propShared", "Share with the &group", was.Shared, true, null, 5);
            var anyone = Box("propAnyoneCopy", "&Anyone can copy", was.AnyoneCopy, mine.Copy && mine.Transfer,
                "Only for what you can copy and transfer yourself.", 6);
            // Second Life never takes transfer from a next owner who can't copy (the viewer holds the box on)
            void CopyRule()
            {
                if (!mine.Transfer) return;
                bool hold = !copy.Checked;
                if (hold) transfer.Checked = true;
                transfer.Enabled = !hold;
                transfer.AccessibleDescription = hold ? "On while the next owner can't copy: one of the two always stays." : null;
            }
            copy.CheckedChanged += (_, _) => CopyRule();
            CopyRule();
            var ok = new Button { Name = "ok", Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Name = "cancel", Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            row.Controls.Add(ok);
            row.Controls.Add(cancel);
            t.Controls.Add(row, 1, 7);
            name.SelectAll();
            f.ActiveControl = name;
            if (f.ShowDialog(owner) != DialogResult.OK) return null;
            return new Props(name.Text.Trim().Length > 0 ? name.Text.Trim() : was.Name, desc.Text.Trim(), mod.Checked, copy.Checked, transfer.Checked, shared.Checked, anyone.Checked);
        }
    }
}
