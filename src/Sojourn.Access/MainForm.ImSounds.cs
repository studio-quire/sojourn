using LibreMetaverse;
using Sojourn.Access.Sound;
using Sojourn.Access.Walk;

namespace Sojourn.Access;

/// <summary>
/// A sound of its own for someone's IMs, or a group's chat (Sound/ImSounds.cs): "IM sound for NAME..." on the
/// Applications key of a person nearby, a friend and an IM's tab, "Group sound for NAME..." on a group and a group
/// chat's tab, each opening the list of sounds, played as she moves through it; //imsound NAME SOUND too.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>The list of sounds for someone (or a group), each heard as she reaches it; the one chosen is kept.</summary>
    void ChooseImSound(UUID id, string name, bool group)
    {
        if (_earcons is not { } earcons) return;
        var names = new List<string> { group ? "the usual group chat sound" : "the usual IM sound" };
        names.AddRange(ImSounds.All.Select(s => s.Name));
        var now = _set.ImSoundOf(id);
        int current = Array.FindIndex(ImSounds.All, s => s.Name == now) + 1;   // (none: 0, the usual one)
        var usual = group ? CueSounds.Cue.EarGroup : CueSounds.Cue.EarIm;
        var pick = Dialogs.ChooseSound(this, $"{(group ? "Group" : "IM")} sound for {name}", names, current,
            i => earcons.Try(i == 0 ? usual : ImSounds.All[i - 1].Cue, group));
        if (pick is not { } p) { Reader?.Answer("Not changed.", keep: false); return; }
        SetImSound(id, name, group, p == 0 ? null : ImSounds.All[p - 1].Name);
    }

    void SetImSound(UUID id, string name, bool group, string? sound)
    {
        _set.SetImSound(id, sound);
        var line = $"{(group ? $"{name} group chat" : $"IMs from {name}")}: {sound ?? "the usual sound"}.";
        if (sound != null && _set.EarconLevel(group ? "group" : "im") <= 0)
            line += group ? " Group chat sounds are off: Alt+0, Group chat sound, turns them on." : " IM sounds are off: Alt+0, IM sound, turns them on.";
        Answer(line);
    }

    static string SoundItemText(string? name, bool group) =>
        $"{(group ? "Group" : "IM")} &sound{(name != null ? $" for {name.Replace("&", "&&")}" : "")}...";

    /// <summary>"IM sound for NAME..." (a group's "Group sound for NAME...") added to a list's menu, named for whoever
    /// is selected as it opens.</summary>
    void SoundItem(ContextMenuStrip menu, Func<(UUID Id, string Name)?> who, bool group)
    {
        var item = new ToolStripMenuItem(SoundItemText(null, group)) { Name = "imSoundItem" };
        item.Click += (_, _) => { if (who() is { } w) ChooseImSound(w.Id, w.Name, group); };
        menu.Opening += (_, _) => item.Text = SoundItemText(who()?.Name, group);
        menu.Items.Add(item);
    }

    /// <summary>A conversation's Applications key (in its message box or history): its sound, then what Windows' edit
    /// menu had, which this menu takes the place of.</summary>
    void ConversationMenu(Conversation cv)
    {
        void Show(TextBox box)
        {
            var menu = new ContextMenuStrip { Name = "conversationMenu", AccessibleName = "Actions" };
            void Add(string text, string key, Action act) => menu.Items.Add(new ToolStripMenuItem(text, null, (_, _) => act()) { ShortcutKeyDisplayString = key });
            var name = cv.Group && cv.Title.EndsWith(" (group)") ? cv.Title[..^8] : cv.Title;
            var id = cv.Group ? (UUID.TryParse(cv.SessionId, out var g) ? g : UUID.Zero) : cv.Other;
            if (id != UUID.Zero)
            {
                menu.Items.Add(new ToolStripMenuItem(SoundItemText(name, cv.Group), null, (_, _) => ChooseImSound(id, name, cv.Group)) { Name = "imSoundItem" });
                menu.Items.Add(new ToolStripSeparator());
            }
            if (!box.ReadOnly) Add("Cu&t", "Control+X", box.Cut);
            Add("&Copy", "Control+C", box.Copy);
            if (!box.ReadOnly) Add("&Paste", "Control+V", box.Paste);
            Add("Select &all", "Control+A", box.SelectAll);
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            menu.Show(box, new Point(10, Math.Min(box.Height, 40)));
        }
        if (cv.Input is LineBox input) input.AppsMenu = () => Show(cv.Input);
        if (cv.History is LineBox history) history.AppsMenu = () => Show(cv.History);
    }

    /// <summary>//imsound: NAME SOUND gives someone (or a group) a sound; NAME alone says theirs; nothing lists the sounds
    /// and who has one. The name is looked for among open conversations, friends, people nearby and groups.</summary>
    async Task ImSoundWordsAsync(string words)
    {
        var w = words.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length == 0)
        {
            var given = _set.ImSoundFor.Select(kv => UUID.TryParse(kv.Key, out var id) ? $"{NameOf(id)}: {kv.Value}" : null).OfType<string>().ToList();
            Answer($"IM sounds: {ImSounds.Names}. {(given.Count > 0 ? $"Given: {string.Join("; ", given)}." : "Nobody has one yet.")} "
                + "//imsound NAME SOUND gives one (usual: the usual sound); a person's Applications key has IM sound for them, where each can be heard.");
            return;
        }
        // the sound is the last words (one to four: "the usual IM sound"), the name what comes before
        string? sound = null;
        int nameWords = w.Length;
        for (int k = Math.Min(4, w.Length - 1); k >= 1 && sound == null; k--)
            if (ImSounds.Find(string.Join(' ', w[^k..])) is { } s) { sound = s; nameWords = w.Length - k; }
        var name = string.Join(' ', w[..nameWords]);
        if (await FindForSoundAsync(name) is not { } who)
        {
            Answer(sound == null && w.Length > 1
                ? $"No sound or person called that: the sounds are {ImSounds.Names}, or usual."
                : $"No one called {name} in your conversations, friends, groups or nearby.");
            return;
        }
        if (sound == null)
        {
            Answer($"{(who.Group ? $"{who.Name} group chat" : $"IMs from {who.Name}")}: {_set.ImSoundOf(who.Id) ?? "the usual sound"}.");
            return;
        }
        SetImSound(who.Id, who.Name, who.Group, sound.Length == 0 ? null : sound);
        if (sound.Length > 0 && ImSounds.CueOf(sound) is { } cue) _earcons?.Try(cue, who.Group);
    }

    /// <summary>Someone (or a group) by name: open conversations first, then friends, people nearby, groups; a whole
    /// name before one starting so, before one containing the words.</summary>
    async Task<(UUID Id, string Name, bool Group)?> FindForSoundAsync(string name)
    {
        var all = new List<(UUID Id, string Name, bool Group)>();
        foreach (var cv in _convs.Values.Where(c => !c.Builders))
        {
            if (cv.Group && UUID.TryParse(cv.SessionId, out var g)) all.Add((g, cv.Title.EndsWith(" (group)") ? cv.Title[..^8] : cv.Title, true));
            else if (!cv.Group && cv.Other != UUID.Zero) all.Add((cv.Other, cv.Title, false));
        }
        if (_ops.Client is { } c)
            all.AddRange(c.Friends.FriendList.Values.Select(f => (f.UUID, Host!.Session.Names.Complete(f.UUID, f.Name ?? ""), false)));
        foreach (var p in await _ops.PeopleAsync(512)) { all.Add((p.Id, p.Name, false)); if (p.Username.Length > 0) all.Add((p.Id, p.Username, false)); }
        if (Host != null) all.AddRange(Host.Session.State.Groups.Values.Select(g => (g.ID, g.Name, true)));
        var n = name.Trim();
        bool Starts(string s) => s.StartsWith(n, StringComparison.OrdinalIgnoreCase)
            || s.Split(' ', '.').Any(part => part.StartsWith(n, StringComparison.OrdinalIgnoreCase));
        var hit = all.FirstOrDefault(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        if (hit.Id == UUID.Zero) hit = all.FirstOrDefault(a => a.Name.Length > 0 && Starts(a.Name));
        if (hit.Id == UUID.Zero) hit = all.FirstOrDefault(a => a.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
        return hit.Id == UUID.Zero ? null : hit;
    }

    /// <summary>A person's or group's name for //imsound's list (a group's said so).</summary>
    string NameOf(UUID id) => Host?.Session.State.Groups.TryGetValue(id, out var g) == true ? $"{g.Name} (group)"
        : Host?.Session.Names.Complete(id, "someone not known by name yet") ?? id.ToString();
}
