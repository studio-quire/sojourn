using System.Text;
using System.Text.RegularExpressions;
using LibreMetaverse;
using LibreMetaverse.Assets;

namespace Sojourn.Core.Agents;

/// <summary>
/// A notecard's asset, as the viewer's LLNotecard writes it: a head holding the LLEmbeddedItems block, then the text.
/// An embedded item keeps its place in the text by a private-use character, U+100000 plus its "ext char index".
///
/// Saving keeps the head byte for byte rather than decoding and encoding it: LibreMetaverse's Encode rewrites what it
/// parsed (flags in decimal where the viewer's are hex, a no-copy item's shadow_id dropped), and these bytes are what
/// the grid last accepted. Every writer of a notecard goes through <see cref="Saved"/>, so no caller can lose an item.
///
/// To callers the place shows as a marker in words, "[landmark: Harbour]" (those characters read as nothing, or a box,
/// on a screen, and a model rewriting a text can't be trusted with them), and comes back from a marker or the raw
/// character alike. An item whose place has gone from the text goes to the end of it, rather than leaving the card:
/// the official viewer drops an item with no place in the text the next time it saves.
/// </summary>
public sealed class NotecardFile
{
    const int FirstEmbeddedChar = 0x100000;

    readonly byte[]? _head;   // everything before "Text length"; null with nothing embedded
    readonly List<int> _indices = new();

    /// <summary>The text with the grid's own place characters.</summary>
    public string RawText { get; }

    /// <summary>What's embedded, in the head's order; empty if the head couldn't be decoded (it's still kept).</summary>
    public List<InventoryItem> Items { get; } = new();

    public static NotecardFile Empty { get; } = Parse(null);

    NotecardFile(string text, byte[]? head) { RawText = text; _head = head; }

    public static byte[] Asset(string text)
    {
        var body = Encoding.UTF8.GetByteCount(text);
        return Encoding.UTF8.GetBytes($"Linden text version 2\n{{\nLLEmbeddedItems version 1\n{{\ncount 0\n}}\nText length {body}\n{text}}}\n");
    }

    public static NotecardFile Parse(byte[]? raw)
    {
        if (raw is null || raw.Length == 0) return new NotecardFile("", null);
        var bytes = Encoding.Latin1.GetString(raw);   // one char a byte: offsets here are offsets in raw
        var at = bytes.IndexOf("\nText length ", StringComparison.Ordinal);
        if (!bytes.StartsWith("Linden text version", StringComparison.Ordinal) || at < 0)
            return new NotecardFile(Encoding.UTF8.GetString(raw).TrimEnd('\0'), null);
        var head = bytes[..(at + 1)];
        var lineEnd = bytes.IndexOf('\n', at + 1);
        if (lineEnd < 0) return new NotecardFile("", null);
        var start = lineEnd + 1;
        // up to the closing brace, a byte no multi-byte character holds (some writers' stated length is a byte out), or
        // the length it states if the card doesn't end with one
        var end = bytes.LastIndexOf('}');
        if (end < start || bytes[(end + 1)..].Trim(' ', '\n', '\r', '\t', '\0').Length > 0)
            end = int.TryParse(bytes[(at + 13)..lineEnd].Trim(), out var stated) && stated >= 0 && start + stated <= raw.Length ? start + stated : raw.Length;
        var text = Encoding.UTF8.GetString(raw, start, end - start);
        var indices = Regex.Matches(head, @"(?m)^\s*ext char index\s+(\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToList();
        var file = new NotecardFile(text, indices.Count > 0 ? raw[..head.Length] : null);
        file._indices.AddRange(indices);
        if (indices.Count > 0)
        {
            var card = new AssetNotecard(UUID.Zero, raw);
            if (card.Decode() && card.EmbeddedItems.Count == indices.Count) file.Items.AddRange(card.EmbeddedItems);
        }
        return file;
    }

    /// <summary>How the item at this place in <see cref="Items"/> reads in the text: "[landmark: Harbour]", numbered
    /// when two would read the same.</summary>
    public string Marker(int i)
    {
        string Plain(int n) => Items.Count == _indices.Count
            ? $"{AgentInventory.AssetTypeLabel(Items[n].AssetType)}: {Items[n].Name}"
            : $"embedded item {n + 1}";
        var word = Plain(i);
        var nth = Enumerable.Range(0, i + 1).Count(n => Plain(n) == word);
        return nth == 1 ? $"[{word}]" : $"[{word}, {nth}]";
    }

    /// <summary>The text with each item's place as its marker, for reading and for handing back to <see cref="Saved"/>.</summary>
    public string Readable => Enumerable.Range(0, _indices.Count).Aggregate(RawText, (t, i) => t.Replace(Place(i), Marker(i), StringComparison.Ordinal));

    string Place(int i) => char.ConvertFromUtf32(FirstEmbeddedChar + _indices[i]);

    /// <summary>This text in an asset that keeps what's embedded here. <paramref name="moved"/> names the items whose
    /// place was no longer in the text, now at its end.</summary>
    public byte[] Saved(string text, out List<string> moved)
    {
        moved = new List<string>();
        if (_head is null) return Asset(text);
        for (int i = 0; i < _indices.Count; i++) text = text.Replace(Marker(i), Place(i), StringComparison.Ordinal);
        for (int i = 0; i < _indices.Count; i++)
        {
            if (text.Contains(Place(i), StringComparison.Ordinal)) continue;
            text = AtEnd(text, Place(i));
            moved.Add(Items.Count == _indices.Count ? Items[i].Name : $"embedded item {i + 1}");
        }
        var rest = Encoding.UTF8.GetBytes($"Text length {Encoding.UTF8.GetByteCount(text)}\n{text}}}\n");
        return _head.Concat(rest).ToArray();
    }

    static string AtEnd(string text, string place) => (text.Length == 0 || text.EndsWith('\n') ? text : text + "\n") + place;

    /// <summary>
    /// This text in an asset with some of what's embedded taken off and things put in, as the viewer does when an item's
    /// character is deleted or an inventory item dropped into the text. The viewer finds an item by its place in the
    /// list, not by the "ext char index" it writes (LLEmbeddedItems::getEmbeddedCharFromIndex), so what stays is numbered
    /// again from nought, in the text and the head alike; each kept block is otherwise byte for byte as it came. An added
    /// item takes the places of its <c>Marker</c> in the text (whatever words the caller put there, unique in it), or goes
    /// to the end, named in <paramref name="moved"/>, as a kept one whose place is gone does.
    /// </summary>
    public byte[] Saved(string text, IReadOnlyCollection<UUID> remove, IReadOnlyList<(InventoryItem Item, string Marker)> add, out List<string> moved, out List<string> removed)
    {
        removed = new List<string>();
        if (remove.Count == 0 && add.Count == 0) return Saved(text, out moved);
        moved = new List<string>();
        if (_indices.Count > 0 && (Items.Count != _indices.Count || Blocks() is null))
            throw new InvalidOperationException("what this card holds couldn't be read, so nothing can be taken off it or put in");
        var blocks = _indices.Count > 0 ? Blocks()!.Value : (Prefix: "Linden text version 2\n{\nLLEmbeddedItems version 1\n{\ncount 0\n", Items: new List<string>(), Suffix: "}\n");
        for (int i = 0; i < _indices.Count; i++) text = text.Replace(Marker(i), Place(i), StringComparison.Ordinal);
        // old place -> new place in the list (-1: taken off)
        var newAt = new int[_indices.Count];
        var kept = new List<string>();
        for (int i = 0, n = 0; i < _indices.Count; i++)
        {
            if (remove.Contains(Items[i].UUID)) { newAt[i] = -1; removed.Add(Items[i].Name); continue; }
            newAt[i] = n++;
            kept.Add(Regex.Replace(blocks.Items[i], @"(?m)^(\s*ext char index\s+)\d+", m => m.Groups[1].Value + newAt[i], RegexOptions.None, TimeSpan.FromSeconds(1)));
        }
        var sb = new StringBuilder(text.Length);
        foreach (var r in text.EnumerateRunes())
        {
            int k = r.Value - FirstEmbeddedChar, old = k is >= 0 and < 0x10000 ? _indices.IndexOf(k) : -1;
            if (old < 0) sb.Append(r.ToString());
            else if (newAt[old] >= 0) sb.Append(char.ConvertFromUtf32(FirstEmbeddedChar + newAt[old]));
        }
        text = sb.ToString();
        for (int i = 0; i < _indices.Count; i++)
        {
            if (newAt[i] < 0) continue;
            var place = char.ConvertFromUtf32(FirstEmbeddedChar + newAt[i]);
            if (text.Contains(place, StringComparison.Ordinal)) continue;
            text = AtEnd(text, place);
            moved.Add(Items[i].Name);
        }
        foreach (var (item, marker) in add)
        {
            var place = char.ConvertFromUtf32(FirstEmbeddedChar + kept.Count);
            kept.Add(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(Block(kept.Count, item))));
            if (marker.Length > 0 && text.Contains(marker, StringComparison.Ordinal)) text = text.Replace(marker, place, StringComparison.Ordinal);
            else { text = AtEnd(text, place); moved.Add(item.Name); }
        }
        var prefix = Regex.Replace(blocks.Prefix, @"(?m)^(\s*count\s+)\d+", m => m.Groups[1].Value + kept.Count, RegexOptions.None, TimeSpan.FromSeconds(1));
        var head = Encoding.Latin1.GetBytes(prefix + string.Concat(kept) + blocks.Suffix);
        return head.Concat(Encoding.UTF8.GetBytes($"Text length {Encoding.UTF8.GetByteCount(text)}\n{text}}}\n")).ToArray();
    }

    /// <summary>The head cut into what comes before the items (up to "count N"), each item's block ("{" to its "}"), and
    /// what follows them, in Latin-1 (a char a byte); null if it isn't laid out as the viewer writes it.</summary>
    (string Prefix, List<string> Items, string Suffix)? Blocks()
    {
        if (_head is null) return null;
        var head = Encoding.Latin1.GetString(_head);
        var count = Regex.Match(head, @"(?m)^\s*count\s+\d+[^\n]*\n");
        if (!count.Success) return null;
        int at = count.Index + count.Length;
        var items = new List<string>();
        while (at < head.Length)
        {
            int lineEnd = head.IndexOf('\n', at);
            if (lineEnd < 0) return null;
            if (head[at..lineEnd].Trim() != "{") break;
            int depth = 0, end = at;
            while (end < head.Length)
            {
                int next = head.IndexOf('\n', end);
                if (next < 0) return null;
                var line = head[end..next].Trim();
                if (line == "{") depth++;
                else if (line == "}") depth--;
                end = next + 1;
                if (depth == 0) break;
            }
            if (depth != 0) return null;
            items.Add(head[at..end]);
            at = end;
        }
        return items.Count == _indices.Count ? (head[..(count.Index + count.Length)], items, head[at..]) : null;
    }

    /// <summary>An inventory item as the viewer embeds it (LLNotecard::exportEmbeddedItemsStream, each item by
    /// LLInventoryItem::exportLegacyStream with its asset key).</summary>
    static string Block(int index, InventoryItem it)
    {
        var p = it.Permissions;
        static string Hex(PermissionMask m) => ((uint)m).ToString("x8");
        var sale = it.SaleType switch { SaleType.Original => "orig", SaleType.Copy => "copy", SaleType.Contents => "cntn", _ => "not" };
        var created = it.CreationDate == default ? 0 : (long)Utils.DateTimeToUnixTime(it.CreationDate);
        return "{\next char index " + index + "\n\tinv_item\t0\n\t{\n\t\titem_id\t" + it.UUID + "\n\t\tparent_id\t" + it.ParentUUID + "\n"
               + "\tpermissions 0\n\t{\n"
               + $"\t\tbase_mask\t{Hex(p.BaseMask)}\n\t\towner_mask\t{Hex(p.OwnerMask)}\n\t\tgroup_mask\t{Hex(p.GroupMask)}\n\t\teveryone_mask\t{Hex(p.EveryoneMask)}\n\t\tnext_owner_mask\t{Hex(p.NextOwnerMask)}\n"
               + $"\t\tcreator_id\t{it.CreatorID}\n\t\towner_id\t{it.OwnerID}\n\t\tlast_owner_id\t{it.LastOwnerID}\n\t\tgroup_id\t{it.GroupID}\n\t}}\n"
               + $"\t\tasset_id\t{it.AssetUUID}\n\t\ttype\t{Utils.AssetTypeToString(it.AssetType)}\n\t\tinv_type\t{Utils.InventoryTypeToString(it.InventoryType)}\n\t\tflags\t{it.Flags:x8}\n"
               + $"\tsale_info\t0\n\t{{\n\t\tsale_type\t{sale}\n\t\tsale_price\t{it.SalePrice}\n\t}}\n"
               + $"\t\tname\t{it.Name}|\n\t\tdesc\t{it.Description}|\n\t\tcreation_date\t{created}\n\t}}\n}}\n";
    }

    /// <summary>The viewer's rule for what can go in a notecard (LLViewerTextEditor::handleDragAndDrop): the next owner may
    /// copy, modify and transfer it, since whoever gets the card can copy it out.</summary>
    public static bool CanEmbed(InventoryItem it) =>
        (it.Permissions.NextOwnerMask & (PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Transfer)) == (PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Transfer);

    /// <summary>The words a marker of this kind and name has, "landmark: Harbour" (inside the brackets).</summary>
    public static string MarkerWords(AssetType type, string name) => $"{AgentInventory.AssetTypeLabel(type)}: {name}";
}
