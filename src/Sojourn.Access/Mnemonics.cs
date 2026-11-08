using System.Runtime.CompilerServices;

namespace Sojourn.Access;

/// <summary>
/// No two items on an open menu share a letter. When two do, Windows only moves to the first on that letter and runs
/// nothing, so a press does nothing and nothing is said: Play and Paste as link both on L, and Play and Copy to on Y,
/// left the blind tester's key without effect in testing (2026-11-08), and menus are built item by item for what's
/// selected, so a clash can come from any pairing. Called once a menu is open (after its Opening handler has hidden
/// what doesn't apply): each item keeps its own letter if it's free, else takes the next free letter in its words,
/// else has none; each time from the words it was made with, so one context's change doesn't carry to the next.
/// Written by Claude Opus 5.5, 2026-11-08.
/// </summary>
static class Mnemonics
{
    static readonly ConditionalWeakTable<ToolStripItem, string> Made = new();

    public static void Fix(ToolStrip strip)
    {
        var used = new HashSet<char>();
        foreach (ToolStripItem item in strip.Items)
        {
            if (item is not ToolStripMenuItem) continue;
            if (!Made.TryGetValue(item, out var made)) { made = item.Text ?? ""; Made.Add(item, made); }
            if (item.Text != made) item.Text = made;
            if (!item.Visible) continue;
            int at = LetterAt(made);
            if (at < 0) continue;
            if (used.Add(char.ToLowerInvariant(made[at + 1]))) continue;
            var plain = made.Remove(at, 1);
            int free = -1;
            for (int i = 0; i < plain.Length; i++)
                if (char.IsLetterOrDigit(plain[i]) && !used.Contains(char.ToLowerInvariant(plain[i])) && !(i > 0 && plain[i - 1] == '&')) { free = i; break; }
            item.Text = free < 0 ? plain : plain.Insert(free, "&");
            if (free >= 0) used.Add(char.ToLowerInvariant(plain[free]));
        }
    }

    /// <summary>Where the mnemonic's ampersand is ("&&" is a literal ampersand), or -1.</summary>
    static int LetterAt(string s)
    {
        for (int i = 0; i < s.Length - 1; i++)
        {
            if (s[i] != '&') continue;
            if (s[i + 1] == '&') { i++; continue; }
            return i;
        }
        return -1;
    }
}
