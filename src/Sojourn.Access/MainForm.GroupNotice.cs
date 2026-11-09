using System.Text;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// A new notice to a group (the viewer's Create Notice): a window with its subject, message and one thing attached, the
/// thing pasted from what Control+C took in the inventory, as Radegast's notice takes it. Every member who gets the
/// group's notices gets it; with something attached it waits for her confirmation, as a give does. Not modal, so the
/// confirmation in Notices comes back here. Claude Opus 5.5, 2026-11-08.
/// </summary>
public sealed partial class MainForm
{
    readonly Dictionary<UUID, Form> _noticeWindows = new();

    // the viewer's limits (panel_group_notices.xml), in bytes as the grid counts them
    const int NoticeSubjectMax = 63, NoticeMessageMax = 511;

    void NewNotice(MyGroup g)
    {
        if (_noticeWindows.TryGetValue(g.Id, out var open) && !open.IsDisposed) { open.Activate(); return; }
        if (Lacks(g, GroupPowers.SendNotices, "send notices") is { } no) { Answer(no); return; }
        var title = $"New notice to {g.Name}";
        var form = new Form
        {
            Name = "groupNotice", Text = title, AccessibleName = title, Size = new Size(620, 460),
            StartPosition = FormStartPosition.CenterParent, KeyPreview = true, ShowInTaskbar = false,
        };
        var subject = new LineBox { Name = "noticeSubject", AccessibleName = "Subject", Dock = DockStyle.Fill, MaxLength = 0 };
        var message = new LineBox
        {
            Name = "noticeMessage", AccessibleName = "Message", Dock = DockStyle.Fill, MaxLength = 0, Multiline = true, AcceptsReturn = true,
            ScrollBars = ScrollBars.Vertical,
        };
        var attachment = new TextBox { Name = "noticeAttachment", AccessibleName = "Attachment", ReadOnly = true, Dock = DockStyle.Fill, BackColor = SystemColors.Window };
        var attach = new Button { Name = "noticeAttach", Text = "&Attach what you copied", AutoSize = true };
        var remove = new Button { Name = "noticeRemove", Text = "Remo&ve the attachment", AutoSize = true, Enabled = false };
        var send = new Button { Name = "noticeSend", Text = "&Send", AutoSize = true };
        var cancel = new Button { Name = "noticeCancel", Text = "Cancel", AutoSize = true };
        var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(6) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        int row = 0, tab = 0;
        void Row(string label, Control c, RowStyle style)
        {
            t.RowStyles.Add(style);
            if (label.Length > 0) t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, TabIndex = tab++ }, 0, row);
            c.TabIndex = tab++;
            t.Controls.Add(c, 1, row++);
        }
        Row("S&ubject", subject, new RowStyle(SizeType.AutoSize));
        Row("&Message", message, new RowStyle(SizeType.Percent, 100));
        Row("A&ttachment", attachment, new RowStyle(SizeType.AutoSize));
        var attachRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        attachRow.Controls.AddRange(new Control[] { attach, remove });
        Row("", attachRow, new RowStyle(SizeType.AutoSize));
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { send, cancel });
        Row("", buttons, new RowStyle(SizeType.AutoSize));
        form.Controls.Add(t);

        InventoryItem? attached = null;
        const string Nothing = "Nothing attached. In your inventory, Control+C on one thing, then Attach what you copied here.";
        attachment.Text = Nothing;
        void Attach()
        {
            var copied = _invClip.Select(id => Store is { } s && s.Contains(id) ? s[id] : null).OfType<InventoryBase>().ToList();
            if (copied.Count == 0) { Reader?.Answer("Nothing copied. In your inventory, Alt+4, Control+C on the thing to attach, then come back here.", keep: false); return; }
            if (copied.Count > 1) { Reader?.Answer($"A notice carries one thing, and you copied {copied.Count}. Copy just the one.", keep: false); return; }
            if (copied[0] is not InventoryItem item) { Reader?.Answer($"{copied[0].Name} is a folder: a notice carries one thing, not a folder.", keep: false); return; }
            // a link attaches what it links to
            if (item.AssetType is AssetType.Link && Store is { } st && st.Contains(item.AssetUUID) && st[item.AssetUUID] is InventoryItem target) item = target;
            var mask = item.Permissions.OwnerMask;
            if ((mask & (PermissionMask.Copy | PermissionMask.Transfer)) != (PermissionMask.Copy | PermissionMask.Transfer))
            {
                Reader?.Answer($"{item.Name} can't go with a notice: every member gets a copy, so it takes copy and transfer rights, and it's {((mask & PermissionMask.Copy) == 0 ? "no-copy" : "no-transfer")}.", keep: false);
                return;
            }
            attached = item;
            attachment.Text = $"{item.Name}, {AgentInventory.AssetTypeLabel(item.AssetType)}";
            remove.Enabled = true;
            Reader?.Answer($"Attached {item.Name}.", keep: false);
        }
        attach.Click += (_, _) => Attach();
        remove.Click += (_, _) =>
        {
            attached = null;
            attachment.Text = Nothing;
            remove.Enabled = false;
            attach.Focus();
            Reader?.Answer("Nothing attached now.", keep: false);
        };

        bool sending = false;
        async Task SendAsync()
        {
            if (sending) return;
            var words = subject.Text.Trim();
            var body = message.Text.TrimEnd().ReplaceLineEndings("\n");
            if (words.Length == 0) { subject.Focus(); Reader?.Answer("A notice needs a subject.", keep: false); return; }
            if (Encoding.UTF8.GetByteCount(words) > NoticeSubjectMax) { subject.Focus(); Reader?.Answer($"The subject is longer than Second Life takes, {NoticeSubjectMax} letters; shorten it.", keep: false); return; }
            if (Encoding.UTF8.GetByteCount(body) > NoticeMessageMax) { message.Focus(); Reader?.Answer($"The message is longer than Second Life takes, {NoticeMessageMax} letters; shorten it.", keep: false); return; }
            var args = new JsonObject { ["group_id"] = g.Id.ToString(), ["subject"] = words, ["message"] = body };
            if (attached != null) args["item_id"] = attached.UUID.ToString();
            sending = true;
            try
            {
                if (attached == null) Reader?.Answer("Sending.", keep: false);
                var r = attached != null ? await ConfirmedFromAsync(form, "TesselGroups", "notice", args) : await _ops.DoAsync("TesselGroups", "notice", args);
                if (r == null || form.IsDisposed) return;
                if (PersonOps.Error(r) is { } e) { Reader?.Answer($"Not sent: {e}", keep: false); return; }
                form.Close();
                // (the grid drops a notice it can't take without a word: its archive says whether it went)
                Answer(r["archived"]?.GetValue<bool>() == false
                    ? $"Sent the notice to {g.Name}, but its archive doesn't show it yet, so it may not have gone through; the group's Notices lists what's there."
                    : $"Sent the notice to {g.Name}.");
            }
            finally { sending = false; }
        }
        send.Click += (_, _) => _ = SendAsync();
        cancel.Click += (_, _) => form.Close();
        form.CancelButton = cancel;
        form.KeyDown += (_, e) =>
        {
            if (e.KeyData == (Keys.Control | Keys.V) && form.ActiveControl == attachment) { e.Handled = e.SuppressKeyPress = true; Attach(); }
            else if (e.KeyData == (Keys.Control | Keys.Enter)) { e.Handled = e.SuppressKeyPress = true; _ = SendAsync(); }
            else if (e.KeyData == Keys.F1)
            {
                e.Handled = e.SuppressKeyPress = true;
                Reader?.Answer("Subject, then Message, where Enter starts a new line. To attach something, copy it in your inventory with Control+C, then Attach what you copied, "
                    + "or Control+V on the Attachment line. Send, Alt+S or Control+Enter, sends it to every member who gets the group's notices; Escape cancels.", keep: false);
            }
        };
        form.FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing || sending || form.DialogResult == DialogResult.OK) return;
            if (subject.Text.Trim().Length == 0 && message.Text.Trim().Length == 0 && attached == null) return;
            if (!QuitDialog.Ask(form, "Discard this notice?", title, "&Discard")) e.Cancel = true;
        };
        form.FormClosed += (_, _) =>
        {
            _noticeWindows.Remove(g.Id);
            if (!IsDisposed) Activate();
        };
        form.ActiveControl = subject;
        form.Shown += (_, _) => Reader?.Answer($"New notice to {g.Name}. Subject first; F1 for keys.", keep: false);
        _noticeWindows[g.Id] = form;
        form.Show(this);
    }
}
