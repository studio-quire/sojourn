using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;

namespace Sojourn.Access;

/// <summary>
/// What can be done with a person, one list wherever their name is: People nearby, Friends, Search, an IM's Applications
/// key, the profile. Each list had written out its own subset, so someone sending unwanted IMs from afar couldn't be
/// blocked and friendship could only be offered from Search. The items are the same everywhere, with the same letters;
/// those that need the person near say so when they aren't, rather than the menu changing shape.
/// </summary>
public sealed partial class MainForm
{
    /// <param name="profile">Leave out Profile, in the profile's own window.</param>
    List<InvAction> PersonActions(UUID id, string name, bool profile = true)
    {
        bool friend = _ops.Client?.Friends.FriendList.ContainsKey(id) == true;
        bool blocked = Host?.Session.Mutes.IsMuted(id) == true;
        var list = new List<InvAction>
        {
            new("Send &IM", () => StartIm(id, name)),
            new("&Walk to", () => _ = NearAsync(id, name, p => _ = _cmd.WalkAsync(p.Pos, p.Name, 1.5, $"//go tp person {p.Name}"))),
            new("&Face", () => _ = NearAsync(id, name, p => _ = _cmd.FacePointAsync(p.Pos, p.Name))),
            new("W&here", () => _ = WhereAsync(id, name)),
            new("&Describe", () => _ = DescribeAsync(OfPerson(id, name)), "Control+D"),
            new("F&ollow", () => _ = NearAsync(id, name, p => _cmd.Follow(p))),
            new("&Beacon", () => _ = NearAsync(id, name, p => _walk?.BeaconOn(p.Name, p.Id))),
            // (no letters: theirs are all taken on this menu; MainForm.Pointing.cs, MainForm.TheirThings.cs)
            HearingPointing(id) ? new("Stop hearing what they point at", () => TogglePointing(id, name))
                : new("Hear what they point at", () => _ = NearAsync(id, name, p => TogglePointing(p.Id, p.Name))),
            new("What they wear", () => _ = NearAsync(id, name, p => ShowWornBy(p.Id, p.Name))),
            new("Their animations", () => _ = NearAsync(id, name, p => _ = AnimationsOfAsync(p.Id, p.Name))),
        };
        if (profile)
        {
            list.Add(new("&Profile", () => _ = ProfileAsync(id, name)));
            list.Add(new("Notes on the&m", () => _ = ProfileAsync(id, name, toNotes: true)));
        }
        list.Add(new("Chat &log", () => ChatLogOf(((_ops.Client?.Self.AgentID ?? UUID.Zero) ^ id).ToString(), name)));
        list.Add(new("P&ay...", () => _ = PayAsync(id, name)));
        list.Add(new("&Give what you copied", () => _ = GiveCopiedAsync(id, name)));
        list.Add(new("Offer &teleport", () => _ = PersonIdOpAsync("offerTeleport", id, $"Teleport offered to {name}.")));
        list.Add(new("&Request teleport", () => _ = PersonIdOpAsync("requestTeleport", id, $"Asked {name} for a teleport.")));
        if (!friend) list.Add(new("Offer frie&ndship", () => _ = PersonIdOpAsync("offerFriendship", id, $"Friendship offered to {name}.")));
        if (friend) list.Add(new("Start a co&nference...", () => _ = StartConferenceAsync(new Friend(id, name, true))));
        if (Host?.Session.State.Groups.IsEmpty == false) list.Add(new("In&vite to a group...", () => _ = InviteAsync(id, name)));
        bool silent = Voice?.PeerIsMuted(id) == true;
        list.Add(new(silent ? "Unm&ute in voice" : "M&ute in voice", () => _ = VoiceMuteAsync(id, name, !silent)));
        list.Add(new("How loud in voice...", () => _ = VoiceVolumeAsync(id, name)));
        list.Add(new("Always in&cognito with them", () => IncognitoWith(id, name)));
        list.Add(new(blocked ? "Unbloc&k" : "Bloc&k", () => _ = blocked ? UnblockAsync(new Blocked(id, name, "resident")) : BlockAsync(id, name, "resident")));
        list.Add(new(SoundItemText(name, group: false), () => ChooseImSound(id, name, group: false)));
        if (friend) list.Add(new("&End friendship", () => _ = EndFriendshipAsync(new Friend(id, name, true))));
        return list;
    }

    /// <summary>The same list as items, a friend's rights under it.</summary>
    /// <param name="first">Before any of them (the profile's window closing).</param>
    /// <param name="enterIms">In a list where Enter sends an IM (Friends, Search), which its item says.</param>
    void AddPersonItems(ToolStrip menu, UUID id, string name, bool profile = true, Action? first = null, bool enterIms = false)
    {
        foreach (var a in PersonActions(id, name, profile))
        {
            var act = a;
            var item = new ToolStripMenuItem(a.Text, null, (_, _) => { first?.Invoke(); act.Do(); })
            {
                ShortcutKeyDisplayString = enterIms && a.Text == "Send &IM" ? "Enter" : a.Key,
            };
            if (a.Text.Contains("sound")) item.Name = "imSoundItem";
            menu.Items.Add(item);
        }
        if (_ops.Client is not { } c || !c.Friends.FriendList.TryGetValue(id, out var f)) return;
        var rights = new ToolStripMenuItem("What they ma&y do") { Name = "friendRights" };
        void Right(string text, FriendRights bit, string arg, string can)
        {
            bool on = (f.TheirFriendRights & bit) != 0;
            rights.DropDownItems.Add(new ToolStripMenuItem(text, null, (_, _) => { first?.Invoke(); _ = SetRightAsync(id, name, arg, !on, can); }) { Checked = on });
        }
        Right("&See when you're online", FriendRights.CanSeeOnline, "online", "see when you're online");
        Right("&Find you on the map", FriendRights.CanSeeOnMap, "map", "find you on the map");
        Right("&Edit your objects", FriendRights.CanModifyObjects, "modify", "edit your objects");
        menu.Items.Add(rights);
    }

    /// <summary>A menu of a person's actions, built as it opens for whoever is selected then.</summary>
    ContextMenuStrip PersonMenu(string menuName, Func<(UUID Id, string Name)?> who, bool enterIms = false)
    {
        var menu = new ContextMenuStrip { Name = menuName, AccessibleName = "Actions" };
        menu.Opening += (_, e) =>
        {
            menu.Items.Clear();
            if (who() is { } w) AddPersonItems(menu, w.Id, w.Name, enterIms: enterIms);
            e.Cancel = menu.Items.Count == 0;
        };
        return menu;
    }

    /// <summary>Described as they stand if they're here, else by their profile's pictures.</summary>
    Subject OfPerson(UUID id, string name) => Host?.Session.Region?.AvatarById(id) != null
        ? new(id.ToString(), "view", name, $"Describe {name}, the person I picked")
        : new(id.ToString(), "profile", $"{name}'s profile pictures", $"Describe {name}'s profile pictures (they aren't here)");

    /// <summary>What needs the person near, on them if they are within 512 metres; said if they aren't, with whether
    /// a friend is online at all.</summary>
    async Task NearAsync(UUID id, string name, Action<PersonOps.Person> act)
    {
        var p = (await _ops.PeopleAsync(512)).FirstOrDefault(x => x.Id == id);
        if (p != null) { act(p); return; }
        var online = _ops.Client is { } c && c.Friends.FriendList.TryGetValue(id, out var f) ? (f.IsOnline ? ", though online" : ", and offline") : "";
        Answer($"{name} isn't nearby{online}.");
    }

    /// <summary>Where someone is: from her, if they're near; a friend anywhere on the grid who lets her see them on the map, by
    /// region and spot.</summary>
    async Task WhereAsync(UUID id, string name)
    {
        var near = (await _ops.PeopleAsync(512)).FirstOrDefault(x => x.Id == id);
        if (near != null) { Answer($"{near.Name} is {_ops.Where(near.Pos, full: true)}."); return; }
        if (_ops.Client is not { } c || !c.Friends.FriendList.TryGetValue(id, out var f)) { Answer($"{name} isn't nearby."); return; }
        if (!f.IsOnline) { Answer($"{name} isn't nearby, and offline."); return; }
        var r = await _ops.ReadAsync("SojournPeople", "whereFriend", new JsonObject { ["to"] = id.ToString() });
        if (PersonOps.Error(r) is { } e) { Answer($"{name} is online, and not nearby: {e}."); return; }
        Answer($"{name} is in {r.Str("region")}, at {r.Int("x", 0)}, {r.Int("y", 0)}. Request teleport asks them for one.");
    }

    async Task SetRightAsync(UUID id, string name, string arg, bool on, string can)
    {
        if (await ConfirmedAsync("SojournPeople", "friendRights", new JsonObject { ["to"] = id.ToString(), [arg] = on }) is not { } r) return;
        Answer(PersonOps.Error(r) is { } e ? $"Not changed: {e}" : on ? $"{name} can {can} now." : $"{name} can't {can} now.");
    }

    /// <summary>Someone by name, wherever they are: open conversations, friends, people nearby, then the grid's avatar
    /// picker. For //block, //profile and //friend.</summary>
    async Task<(UUID Id, string Name)?> FindByNameAsync(string name)
    {
        if (await FindForSoundAsync(name) is { Group: false } known) return (known.Id, known.Name);
        return await FindSomeoneAsync(name) is { } grid ? (grid.Id, grid.Name) : null;
    }

    async Task ProfileWordsAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Equals("me", StringComparison.OrdinalIgnoreCase)) { await MyProfileAsync(); return; }
        if (await FindByNameAsync(name) is { } who) await ProfileAsync(who.Id, who.Name);
    }

    async Task NotesWordsAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) { Answer("//notes NAME: your own notes on someone, by name. Only you see them."); return; }
        if (await FindByNameAsync(name) is { } who) await ProfileAsync(who.Id, who.Name, toNotes: true);
    }

    async Task FriendWordsAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) { Answer("//friend NAME: offer anyone friendship, by name."); return; }
        if (await FindByNameAsync(name) is not { } who) return;
        if (_ops.Client?.Friends.FriendList.ContainsKey(who.Id) == true) { Answer($"{who.Name} is a friend already."); return; }
        await PersonIdOpAsync("offerFriendship", who.Id, $"Friendship offered to {who.Name}.");
    }
}
