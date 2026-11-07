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
            text = (text.Length == 0 || text.EndsWith('\n') ? text : text + "\n") + Place(i);
            moved.Add(Items.Count == _indices.Count ? Items[i].Name : $"embedded item {i + 1}");
        }
        var rest = Encoding.UTF8.GetBytes($"Text length {Encoding.UTF8.GetByteCount(text)}\n{text}}}\n");
        return _head.Concat(rest).ToArray();
    }
}
