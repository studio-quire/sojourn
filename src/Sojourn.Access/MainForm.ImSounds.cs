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
            line += group ? " Group chat sounds are off: Alt+0, Sounds, Group chat sound, turns them on." : " IM sounds are off: Alt+0, Sounds, IM sound, turns them on.";
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
            // someone's IM: the same as their name has in any list; a group's: its sound. The editing items after them
            // have no letters then, the person's own items having those
            bool person = !cv.Group && id != UUID.Zero;
            if (person) AddPersonItems(menu, id, name);
            else if (id != UUID.Zero) menu.Items.Add(new ToolStripMenuItem(SoundItemText(name, cv.Group), null, (_, _) => ChooseImSound(id, name, cv.Group)) { Name = "imSoundItem" });
            // (Radegast's group chat has its participant list beside it)
            if (cv.Group && id != UUID.Zero) menu.Items.Add(new ToolStripMenuItem("Who's in the c&hat...", null, (_, _) => _ = GroupPeopleAsync(new MyGroup(id, name), chat: true)));
            if (!person) menu.Items.Add(new ToolStripMenuItem("Chat lo&g", null, (_, _) => ChatLogOf(cv.SessionId, name)));
            if (id != UUID.Zero || !person) menu.Items.Add(new ToolStripSeparator());
            if (!box.ReadOnly) Add(person ? "Cut" : "Cu&t", "Control+X", box.Cut);
            Add(person ? "Copy" : "&Copy", "Control+C", box.Copy);
            if (!box.ReadOnly) Add(person ? "Paste" : "&Paste", "Control+V", box.Paste);
            Add(person ? "Select all" : "Select &all", "Control+A", box.SelectAll);
            menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
            menu.Show(box, new Point(10, Math.Min(box.Height, 40)));
            Mnemonics.Fix(menu);
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
    /// name before one starting so, before one containing the words. A person matches by any form of their name (the
    /// one said, the username, the display name as written or in plain letters) and is named as names are said.</summary>
    async Task<(UUID Id, string Name, bool Group)?> FindForSoundAsync(string name)
    {
        var all = new List<(UUID Id, string Name, bool Group, string Match)>();
        void Add(UUID id, string said, bool group, IEnumerable<string>? forms = null)
        {
            foreach (var f in (forms ?? Array.Empty<string>()).Prepend(said).Distinct()) all.Add((id, said, group, f));
        }
        var names = Host?.Session.Names;
        foreach (var cv in _convs.Values.Where(c => !c.Builders))
        {
            if (cv.Group && UUID.TryParse(cv.SessionId, out var g)) Add(g, cv.Title.EndsWith(" (group)") ? cv.Title[..^8] : cv.Title, true);
            else if (!cv.Group && cv.Other != UUID.Zero) Add(cv.Other, cv.Title, false, names?.Forms(cv.Other));
        }
        if (_ops.Client is { } c)
            foreach (var f in c.Friends.FriendList.Values) Add(f.UUID, names?.Complete(f.UUID, f.Name ?? "") ?? f.Name ?? "", false, names?.Forms(f.UUID));
        foreach (var p in await _ops.PeopleAsync(512)) Add(p.Id, p.Name, false, p.Names);
        if (Host != null) foreach (var g in Host.Session.State.Groups.Values) Add(g.ID, g.Name, true);
        var n = name.Trim();
        var plainWords = Sojourn.Core.World.PlainName.Of(n);
        bool Is(string s, string w) => s.Equals(w, StringComparison.OrdinalIgnoreCase);
        bool Starts(string s, string w) => s.StartsWith(w, StringComparison.OrdinalIgnoreCase)
            || s.Split(' ', '.').Any(part => part.StartsWith(w, StringComparison.OrdinalIgnoreCase));
        bool Has(string s, string w) => s.Contains(w, StringComparison.OrdinalIgnoreCase);
        (UUID Id, string Name, bool Group, string Match) hit = default;
        foreach (var test in new Func<string, string, bool>[] { Is, Starts, Has })
            foreach (var w in new[] { n, plainWords }.Where(w => w.Length > 0).Distinct())
                if (hit.Id == UUID.Zero) hit = all.FirstOrDefault(a => a.Match.Length > 0 && test(a.Match, w));
        return hit.Id == UUID.Zero ? null : (hit.Id, hit.Name, hit.Group);
    }

    /// <summary>A person's or group's name for //imsound's list (a group's said so).</summary>
    string NameOf(UUID id) => Host?.Session.State.Groups.TryGetValue(id, out var g) == true ? $"{g.Name} (group)"
        : Host?.Session.Names.Complete(id, "someone not known by name yet") ?? id.ToString();
}
