using System.Runtime.CompilerServices;

namespace Sojourn.Access;

/// <summary>
/// No two items on an open menu share a letter. When two do, Windows only moves to the first on that letter and runs
/// nothing, so a press does nothing and nothing is said: Play and Paste as link both on L, and Play and Copy to on Y,
/// left the blind tester's key without effect in testing (2026-11-08), and menus are built item by item for what's
/// selected, so a clash can come from any pairing. Called once a menu is open (after its Opening handler has hidden
/// what doesn't apply, or named an item for what's selected): each item keeps its own letter if it's free, else takes
/// the next free letter in its words, else has none. The words come back as they were when the menu closes, so the
/// next Opening handler sees and renames the words it made (a first version kept each item's first words for good,
/// which left "Sort A to Z" and a group's sound item stale and Objects' own checks of its items' words failing).
/// Written by Claude Opus 5.5, 2026-11-08.
/// </summary>
static class Mnemonics
{
    /// <summary>The items given other words while a menu is open, with the words they had.</summary>
    static readonly ConditionalWeakTable<ToolStrip, List<(ToolStripItem Item, string Made, string Set)>> Changed = new();

    public static void Fix(ToolStrip strip)
    {
        Restore(strip);
        var changed = new List<(ToolStripItem, string, string)>();
        var used = new HashSet<char>();
        foreach (ToolStripItem item in strip.Items)
        {
            if (item is not ToolStripMenuItem || !item.Visible) continue;
            var made = item.Text ?? "";
            int at = LetterAt(made);
            if (at < 0) continue;
            if (used.Add(char.ToLowerInvariant(made[at + 1]))) continue;
            var plain = made.Remove(at, 1);
            int free = -1;
            for (int i = 0; i < plain.Length; i++)
                if (char.IsLetterOrDigit(plain[i]) && !used.Contains(char.ToLowerInvariant(plain[i])) && !(i > 0 && plain[i - 1] == '&')) { free = i; break; }
            item.Text = free < 0 ? plain : plain.Insert(free, "&");
            if (free >= 0) used.Add(char.ToLowerInvariant(plain[free]));
            changed.Add((item, made, item.Text));
        }
        if (changed.Count == 0) return;
        Changed.AddOrUpdate(strip, changed);
        if (strip is ToolStripDropDown dd)
        {
            void Closed(object? s, ToolStripDropDownClosedEventArgs e) { dd.Closed -= Closed; Restore(dd); }
            dd.Closed += Closed;
        }
    }

    /// <summary>The words changed by Fix put back, where nothing else has changed them since.</summary>
    static void Restore(ToolStrip strip)
    {
        if (!Changed.TryGetValue(strip, out var list)) return;
        Changed.Remove(strip);
        foreach (var (item, made, set) in list)
            if (item.Text == set) item.Text = made;
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
