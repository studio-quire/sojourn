using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Session.Social;

namespace Sojourn.Access;

/// <summary>
/// Long lines (docs/access-app.md > Long lines): the session's sendChat and IM send put a line over the grid's limit out
/// as several messages, in order (Sojourn.Core's ChatSplit). Here: her own read-back of those parts is left out, since
/// she wrote them and a pasted description read back again would run on for minutes, and "Sent in 3 parts." is said once
/// they've all gone; a line that would go as more than ten asks for Enter again first.
/// </summary>
public sealed partial class MainForm
{
    const int ManyParts = 10;
    string? _manyAsked;

    /// <summary>The line can go now; false the first time Enter is pressed on one that would go as more than
    /// <see cref="ManyParts"/> messages (a whole notecard pasted by mistake): said so, and left in the box for Enter
    /// again.</summary>
    bool LongLineReady(string text)
    {
        int n = ChatSplit.Split(text).Count;
        if (n <= ManyParts || _manyAsked == text) { _manyAsked = null; return true; }
        _manyAsked = text;
        Reader?.Answer($"That's long enough for {n} messages. Enter again sends them all.");
        return false;
    }

    readonly List<(string Text, long Until)> _partsGoing = new();

    /// <summary>Sends a line by the op, its parts kept from being read back if it goes as several, then says how many.</summary>
    async Task<JsonObject> SendInPartsAsync(string text, string pump, string op, JsonObject args)
    {
        var parts = ChatSplit.Split(text);
        if (parts.Count > 1)
        {
            long until = Environment.TickCount64 + 30_000 + parts.Count * (long)ChatSplit.Gap.TotalMilliseconds;
            lock (_partsGoing) foreach (var p in parts) _partsGoing.Add((PartWords(p), until));
        }
        var r = await _ops.DoAsync(pump, op, args);
        if (PersonOps.Error(r) == null && r.Int("parts", 1) is > 1 and var n) Reader?.Answer($"Sent in {n} parts.");
        return r;
    }

    /// <summary>Her own line that's one part of a long one (the announcer's thread, from Shape): not read back.</summary>
    bool IsPartGoing(FeedEntry e)
    {
        if (!e.Mine) return false;
        var text = PartWords(e.Text);
        long now = Environment.TickCount64;
        lock (_partsGoing)
        {
            _partsGoing.RemoveAll(p => p.Until < now);
            int i = _partsGoing.FindIndex(p => Same(p.Text, text));
            if (i < 0) return false;
            _partsGoing.RemoveAt(i);
            return true;
        }
    }

    // as the feed has it: an emote without its /me (SocialFeed), spaces as the region may leave them
    static string PartWords(string s) => Regex.Replace((ChatSplit.IsEmote(s) ? s[3..] : s).Trim(), @"\s+", " ");

    // the feed may reword links in a line, so a long one is matched by its start
    static bool Same(string a, string b) => a == b || a.Length >= 40 && b.Length >= 40 && string.CompareOrdinal(a, 0, b, 0, 40) == 0;
}
