using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Access;

/// <summary>
/// What a notecard holds: a landmark, a gift, another card (SL's welcome cards carry them). Listed under the text, a Tab
/// away; Enter copies one into the inventory, into the folder for its kind, and on a landmark or a notecard Enter again
/// teleports or opens it. Their places in the text read as markers, "[landmark: Harbour]": the session's
/// <see cref="Sojourn.Core.Agents.NotecardFile"/> does that, and keeps them on every save. A card in an object's contents
/// is the same (copied out with the object named, as the viewer does). What the card holds changes at its next save, as
/// in the viewer: Delete on one marks it to be removed; Control+Shift+V in the text puts in what Control+C copied in the
/// inventory, at the caret, marked as coming. Claude Opus 5.5 (the list 2026-11-07, changing it 2026-11-08).
/// </summary>
public sealed partial class MainForm
{
    sealed class Embedded(string id, string name, string kind, string marker)
    {
        /// <summary>Its item id inside the card; for one being put in, its own item in the inventory.</summary>
        public string Id => id;
        public string Name => name;
        public string Kind => kind;
        /// <summary>How its place reads in the text.</summary>
        public string Marker => marker;
        public InventoryItem? Copy;
        /// <summary>Marked to be removed at the next save.</summary>
        public bool Off;
        /// <summary>Being put in at the next save (not in the card yet).</summary>
        public bool Coming;
        public string Words => $"{Name}, {Kind}{(Off ? ", removed when you save" : Coming ? ", goes in when you save" : "")}";
    }

    /// <summary>
    /// One open card's things: as read, and what's to change at its next save. In the inventory (Object is zero) or in an
    /// object's contents (Card is its item there). The list exists from the start but joins the editor's window only once
    /// there's something in it.
    /// </summary>
    sealed class CardHeld
    {
        readonly MainForm _f;
        readonly InvPane _pane;
        readonly string _cardName;
        readonly UUID _card, _object;
        readonly List<Embedded> _things = new();
        public readonly ListView Rows;
        public EditorForm? Editor;

        public CardHeld(MainForm f, InvPane pane, string cardName, UUID card, UUID inObject, JsonObject read)
        {
            _f = f;
            _pane = pane;
            _cardName = cardName;
            _card = card;
            _object = inObject;
            Rows = List("editorEmbedded", "Embedded in this notecard");
            Read(read);
            Rows.Enter += (_, _) => { if (Rows.FocusedItem == null && Rows.Items.Count > 0) Rows.Items[0].Focused = true; };   // (no handle until it's shown, so not before)

            var menu = new ContextMenuStrip { Name = "embeddedMenu", AccessibleName = "Actions" };
            Embedded? On() => Rows.FocusedItem?.Tag as Embedded;
            var copy = new ToolStripMenuItem("&Copy into your inventory", null, (_, _) => { if (On() is { } e) _ = CopyAsync(e); });
            var teleport = new ToolStripMenuItem("&Teleport there", null, (_, _) => { if (On() is { } e) _ = UseAsync(e); });
            var open = new ToolStripMenuItem("&Open", null, (_, _) => { if (On() is { } e) _ = UseAsync(e); });
            var off = new ToolStripMenuItem("&Remove from the card", null, (_, _) => { if (On() is { } e) Toggle(e); }) { ShortcutKeyDisplayString = "Delete" };
            var putIn = new ToolStripMenuItem("&Put in what you copied", null, (_, _) => _ = PutInAsync()) { ShortcutKeyDisplayString = "Control+Shift+V" };
            menu.Items.AddRange(new ToolStripItem[] { copy, teleport, open, off, putIn });
            menu.Opening += (_, _) =>
            {
                var e = On();
                copy.Visible = e is { Coming: false };
                teleport.Visible = e is { Coming: false, Kind: "landmark" };
                open.Visible = e is { Coming: false, Kind: "notecard" };
                off.Visible = e != null;
                off.Text = e is { Off: true } ? "&Keep it in the card" : e is { Coming: true } ? "&Don't put it in" : "&Remove from the card";
                copy.ShortcutKeyDisplayString = e?.Copy == null ? "Enter" : "";
                teleport.ShortcutKeyDisplayString = open.ShortcutKeyDisplayString = e?.Copy != null ? "Enter" : "";
            };
            MenuOn(Rows, menu);
            Rows.KeyDown += (_, e) =>
            {
                if (On() is not { } it) return;
                if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None)
                {
                    e.Handled = e.SuppressKeyPress = true;
                    if (it.Coming) f.Answer($"{it.Name} goes in when you save; it's in your inventory already.");
                    else _ = it.Copy == null ? CopyAsync(it) : UseAsync(it);
                }
                else if (e.KeyCode == Keys.Delete && e.Modifiers == Keys.None) { e.Handled = e.SuppressKeyPress = true; Toggle(it); }
                else if (e.KeyData == (Keys.Control | Keys.Shift | Keys.V)) { e.Handled = e.SuppressKeyPress = true; _ = PutInAsync(); }
            };
        }

        bool InObject => _object != UUID.Zero;
        public bool Any => _things.Count > 0;
        public bool Pending => _things.Any(t => t.Off || t.Coming);

        /// <summary>What to say on opening it (null when it holds nothing).</summary>
        public string? Said
        {
            get
            {
                if (_things.Count == 0) return null;
                var words = string.Join("; ", _things.Select(e => $"{e.Name}, {e.Kind}"));
                return $"{(_things.Count == 1 ? "It holds 1 thing" : $"It holds {_things.Count} things")}: {words}. Tab reaches {(_things.Count == 1 ? "it" : "them")}.";
            }
        }

        /// <summary>The window it's in: its list joined when it holds something, Control+Shift+V and what's pending wired.</summary>
        public void Opened(EditorForm editor)
        {
            Editor = editor;
            if (Any) editor.Held(Rows);
            editor.PutIn = PutInAsync;
            editor.PendingChanges = () => Pending;
        }

        /// <summary>The list as the card now is (readNotecard's or object.content.get's `embedded`).</summary>
        void Read(JsonObject read)
        {
            _things.Clear();
            foreach (var e in (read["embedded"] as JsonArray ?? new()).OfType<JsonObject>())
                _things.Add(new Embedded(e.Str("item_id") ?? "", e.Str("name") ?? "an item", e.Str("kind") ?? "item", e.Str("marker") ?? ""));
            Fill();
        }

        void Fill(Embedded? select = null)
        {
            var at = Rows.FocusedItem?.Index ?? 0;
            Rows.BeginUpdate();
            UiaRelease.Clear(Rows);
            foreach (var e in _things) Rows.Items.Add(new ListViewItem(e.Words) { Name = e.Id, Tag = e });
            Rows.EndUpdate();
            if (Rows.Items.Count == 0) return;
            var item = select != null ? Rows.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag == select) : Rows.Items[Math.Min(at, Rows.Items.Count - 1)];
            if (item == null) return;
            item.Selected = true;
            if (Rows.IsHandleCreated) item.Focused = true;
        }

        /// <summary>Delete: one marked to be removed at the save, or kept after all; one being put in, not put in (its marker
        /// out of the text again).</summary>
        void Toggle(Embedded e)
        {
            if (e.Coming)
            {
                _things.Remove(e);
                Editor?.TakeOut(e.Marker);
                Fill();
                _f.Answer($"{e.Name} won't go in.");
                return;
            }
            e.Off = !e.Off;
            Fill(e);
            _f.Answer(e.Off ? $"{e.Name} is removed from the card when you save." : $"{e.Name} stays in the card.");
        }

        /// <summary>Control+Shift+V: what Control+C (or X) took in the inventory put in at the caret, each as its marker,
        /// going in at the save. Only what the viewer would take: the next owner may copy, modify and transfer it.</summary>
        async Task PutInAsync()
        {
            await Task.Yield();
            var store = _f.Store;
            var copied = _f._invClip.Select(id => store is { } s && s.Contains(id) ? s[id] : null).OfType<InventoryBase>().ToList();
            if (copied.Count == 0)
            {
                _f.Answer("Nothing copied to put in. In your inventory, Alt+4, Control+C on what should go in the card, then Control+Shift+V here.");
                return;
            }
            var said = new List<string>();
            var put = new List<string>();
            Embedded? last = null;
            foreach (var n in copied)
            {
                if (n is not InventoryItem it0) { said.Add($"{n.Name} is a folder: only single things go in a notecard."); continue; }
                var it = it0.AssetType == AssetType.Link && store is { } st && st.Contains(it0.AssetUUID) && st[it0.AssetUUID] is InventoryItem target ? target : it0;
                if (!InObject && it.UUID == _card) { said.Add("A notecard can't hold itself."); continue; }
                if (!NotecardFile.CanEmbed(it)) { said.Add($"{it.Name} can't go in: only things the next owner may copy, modify and transfer can, as in the viewer."); continue; }
                if (it.AssetUUID == UUID.Zero) { said.Add($"{it.Name} can't go in yet: nothing's ever been saved in it."); continue; }
                // already in it (or coming): another place for the same thing, as the viewer reuses an item dropped twice
                if (_things.FirstOrDefault(t => t.Id == it.UUID.ToString()) is { } had)
                {
                    Editor?.InsertAtCaret(had.Marker);
                    had.Off = false;
                    last = had;
                    said.Add($"{it.Name} is in the card already: another place for it, at the caret.");
                    continue;
                }
                var words = NotecardFile.MarkerWords(it.AssetType, it.Name);
                var text = Editor?.Current ?? "";
                string marker = $"[{words}]";
                for (int k = 2; _things.Any(t => t.Marker == marker) || text.Contains(marker, StringComparison.Ordinal); k++) marker = $"[{words}, {k}]";
                Editor?.InsertAtCaret(marker);
                last = new Embedded(it.UUID.ToString(), it.Name, AgentInventory.AssetTypeLabel(it.AssetType), marker) { Coming = true };
                _things.Add(last);
                put.Add(it.Name);
            }
            if (last != null)
            {
                Fill(last);
                if (Editor != null) Editor.Held(Rows);
            }
            var lead = put.Count switch { 0 => "", 1 => $"{put[0]} goes in at the caret when you save.", _ => $"{string.Join(" and ", put)} go in at the caret when you save." };
            _f.Answer(string.Join(" ", said.Prepend(lead).Where(s => s.Length > 0)));
        }

        /// <summary>What a save sends besides the text: the things removed, and those going in with their markers.</summary>
        public void AddTo(JsonObject args)
        {
            var off = _things.Where(t => t.Off).Select(t => (JsonNode)t.Id).ToArray();
            var coming = _things.Where(t => t.Coming).Select(t => (JsonNode)new JsonObject { ["item_id"] = t.Id, ["marker"] = t.Marker }).ToArray();
            if (off.Length > 0) args["remove"] = new JsonArray(off);
            if (coming.Length > 0) args["add"] = new JsonArray(coming);
        }

        /// <summary>A save in the inventory: the text, what's removed and what goes in; what to say, and the text as it now
        /// is when the card changed beyond the words (read again, the list with it).</summary>
        public async Task<(string Said, string? Errors, string? Now)> SaveAsync(string text)
        {
            var args = new JsonObject { ["item_id"] = _card.ToString(), ["text"] = text };
            AddTo(args);
            var w = await _f._ops.DoAsync("TesselInventory", "writeNotecard", args);
            if (PersonOps.Error(w) is { } we) return ($"Not saved: {we}", null, null);
            return await SavedAsync(w);
        }

        /// <summary>What a save said, the card read again when what it holds changed or moved.</summary>
        public async Task<(string Said, string? Errors, string? Now)> SavedAsync(JsonObject w)
        {
            static List<string> Names(JsonNode? n) => (n as JsonArray)?.Select(x => x?.ToString() ?? "").Where(x => x.Length > 0).ToList() ?? new();
            var moved = Names(w["moved_to_end"]);
            var removed = Names(w["removed"]);
            var added = Names(w["added"]);
            var words = new List<string>();
            if (added.Count > 0) words.Add($"Put in: {string.Join(", ", added)}.");
            if (removed.Count > 0) words.Add($"Removed: {string.Join(", ", removed)}.");
            if (moved.Count > 0)
                words.Add(moved.Count == 1 ? $"{moved[0]}: its place was gone from the text, so it's at the end now." : $"{string.Join(" and ", moved)}: their places were gone from the text, so they're at the end now.");
            string? now = null;
            if (moved.Count + removed.Count + added.Count > 0)
            {
                var again = await ReadAgainAsync();
                if (again != null)
                {
                    now = again.Str(InObject ? "content" : "text");
                    Read(again);
                    if (Editor != null && Any) Editor.Held(Rows);
                }
            }
            else
                foreach (var t in _things) t.Off = false;
            if (words.Count > 0) return ($"Saved. {string.Join(" ", words)}", null, now);
            int kept = w["embedded_kept"]?.GetValue<int>() ?? _things.Count;
            return (kept > 0 && _things.Count > 0 ? $"Saved, with the {(kept == 1 ? "thing" : $"{kept} things")} it holds." : "Saved.", null, null);
        }

        async Task<JsonObject?> ReadAgainAsync()
        {
            if (InObject)
            {
                var got = await _f.ContentRpcAsync("object.content.get", new JsonObject { ["prim_id"] = _object.ToString(), ["item_id"] = _card.ToString() });
                return got["result"] as JsonObject;
            }
            var r = await _f._ops.ReadAsync("TesselInventory", "readNotecard", new JsonObject { ["item_id"] = _card.ToString() });
            return PersonOps.Error(r) == null ? r : null;
        }

        async Task<bool> CopyAsync(Embedded e)
        {
            var args = new JsonObject { ["item_id"] = _card.ToString(), ["embedded_id"] = e.Id };
            if (InObject) args["object_id"] = _object.ToString();
            var r = await _f._ops.DoAsync("TesselInventory", "copyFromNotecard", args);
            if (PersonOps.Error(r) is { } err || !UUID.TryParse(r.Str("item_id"), out var id))
            {
                _f.Answer($"Couldn't copy {e.Name}: {PersonOps.Error(r) ?? "the grid sent nothing back"}");
                return false;
            }
            e.Copy = await _f._ops.Session.Inventory.ItemAsync(id, CancellationToken.None);
            var folder = e.Copy is { } made && _f.Store?.TryGetValue<InventoryBase>(made.ParentUUID, out var fo) == true ? fo.Name : "your inventory";
            var next = e.Kind switch { "landmark" => " Enter again teleports there.", "notecard" => " Enter again opens it.", _ => "" };
            _f.Answer($"Copied {e.Name} into {folder}.{next}");
            if (e.Copy is { } it) _f.RefreshInventory(it.ParentUUID);
            return e.Copy != null;
        }

        /// <summary>A landmark teleported by, a notecard opened; copied out first if it hasn't been (an embedded item can't be
        /// used from inside the card).</summary>
        async Task UseAsync(Embedded e)
        {
            if (e.Copy == null && !await CopyAsync(e)) return;
            if (e.Kind == "landmark") await _f._cmd.TeleportLandmarkAsync(e.Copy!.UUID, e.Name);
            else if (e.Kind == "notecard") await _f.OpenAsync(_pane, e.Copy!, Editor);
            else _f.Answer($"{e.Name} is in your inventory already, copied.");
        }
    }
}
