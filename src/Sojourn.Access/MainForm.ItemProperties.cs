using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;

namespace Sojourn.Access;

/// <summary>
/// An inventory item's properties, as the official viewer's Item Properties and Radegast's details pane have them: its
/// name and description, what the next owner may do, shared with the group, anyone can copy. What may change follows the
/// viewer's own rules (LLSidepanelItemInfo, read in its source 2026-11-15; the op holds them too): the name and description
/// with modify rights and not in the Trash; the next owner's rights with transfer rights, each up to the item's own
/// (its base), and transfer held on while the next owner can't copy; anyone can copy with copy and transfer; a landmark's
/// or calling card's rights never change. A change of permissions goes through her confirmation (Confirmations'
/// TesselInventory update), after the name and description, which change at once. The blind tester asked whether objects
/// can be edited in the inventory as in the viewer (2026-11-15): their build no (rezzed or worn, as there), these yes.
/// </summary>
public sealed partial class MainForm
{
    async Task PropertiesAsync(InventoryItem picked)
    {
        var t = Target(picked);
        var p = t.Permissions;
        bool Has(PermissionMask m, PermissionMask bit) => (m & bit) != 0;
        bool restrictable = t.InventoryType is not (InventoryType.Landmark or InventoryType.CallingCard);
        var may = new May(
            Words: Has(p.OwnerMask, PermissionMask.Modify) && !InTrash(t),
            NextModify: restrictable && Has(p.OwnerMask, PermissionMask.Transfer) && Has(p.BaseMask, PermissionMask.Modify),
            NextCopy: restrictable && Has(p.OwnerMask, PermissionMask.Transfer) && Has(p.BaseMask, PermissionMask.Copy),
            NextTransfer: restrictable && Has(p.OwnerMask, PermissionMask.Transfer),
            Share: restrictable,
            AnyoneCopy: restrictable && Has(p.OwnerMask, PermissionMask.Copy) && Has(p.OwnerMask, PermissionMask.Transfer));
        if (may is { Words: false, NextModify: false, NextCopy: false, NextTransfer: false, Share: false, AnyoneCopy: false })
        {
            Answer($"Nothing about {t.Name} can be changed: {(restrictable ? "it's no-modify and no-transfer" : "a landmark or calling card keeps its name only with modify rights, and always goes with every right")}. Details has what it is.");
            return;
        }
        var desc0 = (t.Description == "(No Description)" ? "" : t.Description ?? "").Trim();
        var was = new Props(t.Name, desc0, Has(p.NextOwnerMask, PermissionMask.Modify), Has(p.NextOwnerMask, PermissionMask.Copy),
            Has(p.NextOwnerMask, PermissionMask.Transfer), Has(p.GroupMask, PermissionMask.Modify | PermissionMask.Copy), Has(p.EveryoneMask, PermissionMask.Copy));
        if (ItemPropertiesForm.Ask(this, t.Name, was, may) is not { } now || now == was) { Reader?.Answer("Nothing changed.", keep: false); return; }

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
                var rights = new[] { ('M', "modify"), ('C', "copy"), ('T', "transfer") }.Where(x => next.Contains(x.Item1)).Select(x => x.Item2).ToList();
                said.Add(rights.Count == 0 ? "the next owner may only move it" : $"the next owner may {string.Join(", ", rights)}");
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

    /// <summary>What she may change on it (the viewer's rules, above).</summary>
    sealed record May(bool Words, bool NextModify, bool NextCopy, bool NextTransfer, bool Share, bool AnyoneCopy);

    static class ItemPropertiesForm
    {
        /// <summary>The window: Name and Description boxes, then the switches, OK (Enter) and Cancel (Escape); what she may
        /// not change read-only or unavailable, saying why; transfer kept on while the next owner can't copy. Null when
        /// cancelled.</summary>
        public static Props? Ask(IWin32Window owner, string itemName, Props was, May may)
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
            var name = new LineBox { Name = "propName", AccessibleName = "Name", Text = was.Name, Width = 360, MaxLength = 63, ReadOnly = !may.Words };
            var desc = new LineBox { Name = "propDesc", AccessibleName = "Description", Text = was.Desc, Width = 360, MaxLength = 127, ReadOnly = !may.Words };
            if (!may.Words) name.AccessibleDescription = desc.AccessibleDescription = "Only with modify rights, and not in the Trash.";
            t.Controls.Add(new Label { Text = "&Name", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            t.Controls.Add(name, 1, 0);
            t.Controls.Add(new Label { Text = "&Description", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            t.Controls.Add(desc, 1, 1);
            CheckBox Box(string id, string text, bool on, bool may, string? why, int row)
            {
                var c = new CheckBox { Name = id, Text = text, Checked = on, Enabled = may, AutoSize = true };
                if (!may && why != null) c.AccessibleDescription = why;
                t.Controls.Add(c, 1, row);
                return c;
            }
            const string NextWhy = "Only for what you can transfer, and only rights the item itself has.";
            var mod = Box("propNextModify", "Next owner can &modify", was.NextModify, may.NextModify, NextWhy, 2);
            var copy = Box("propNextCopy", "Next owner can &copy", was.NextCopy, may.NextCopy, NextWhy, 3);
            var transfer = Box("propNextTransfer", "Next owner can &transfer", was.NextTransfer, may.NextTransfer, NextWhy, 4);
            var shared = Box("propShared", "Share with the &group", was.Shared, may.Share, "A landmark or calling card keeps its rights.", 5);
            var anyone = Box("propAnyoneCopy", "&Anyone can copy", was.AnyoneCopy, may.AnyoneCopy,
                "Only for what you can copy and transfer yourself.", 6);
            // Second Life never takes transfer from a next owner who can't copy (the viewer holds the box on)
            void CopyRule()
            {
                if (!may.NextTransfer) return;
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
            return new Props(may.Words && name.Text.Trim().Length > 0 ? name.Text.Trim() : was.Name, may.Words ? desc.Text.Trim() : was.Desc,
                mod.Checked, copy.Checked, transfer.Checked, shared.Checked, anyone.Checked);
        }
    }
}
