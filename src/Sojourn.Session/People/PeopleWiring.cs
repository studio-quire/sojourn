using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Session.Assistant;
using Sojourn.Session.Control;
using Sojourn.Session.Social;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Session.People;

/// <summary>
/// Batch B2's client half (people and me), kept out of Program.cs: the confirmations for SojournPeople's ops and a
/// pay's check against the object's pay buttons; the "you" section of the settings menu and its settings (Do Not
/// Disturb, away, hover height, typing); and a timer, four times a second, that shows typing while the person dictates
/// to people, sets them away after a while without them and back when they return, and brings held notices back when
/// Do Not Disturb ends. Claude Opus 5.5, 2026-10-27. (Places in chat and notecards are offered by a press on their
/// link since batch B4: Social/Links.cs.)
/// </summary>
public sealed class PeopleWiring : IDisposable
{
    readonly AgentSession _s;
    readonly AssistantHost _a;
    readonly Private.IDictation? _dictation;
    readonly ILogger _log;
    readonly Timer _timer;
    ISettingsMenu? _menu;

    /// <param name="recall">Brings the waiting notices back onto the app's feed (the VR client's SocialView.Recall).</param>
    /// <param name="dictation">The app's dictation, if it has one (typing shown while the person dictates).</param>
    public static PeopleWiring Start(AgentSession s, AssistantHost assistant, SocialFeed feed, Action recall, Private.IDictation? dictation, ILogger log) =>
        new(s, assistant, feed, recall, dictation, log);

    PeopleWiring(AgentSession s, AssistantHost assistant, SocialFeed feed, Action recall, Private.IDictation? dictation, ILogger log)
    {
        _s = s;
        _a = assistant;
        _dictation = dictation;
        _log = log;
        assistant.Confirmations.Cases.Add(new Confirmations.Case(Kind, DescribeAsync));
        assistant.Confirmations.PayCheck = PayCheckAsync;
        bool wasDnd = false;
        s.Presence.Changed += () =>
        {
            _menu?.Changed();
            bool dnd = s.Presence.DoNotDisturb;
            // what Do Not Disturb held back comes back on the feed once it's over
            if (wasDnd && !dnd && feed.Folded > 0) recall();
            wasDnd = dnd;
        };
        s.Rejects.Changed += () => _menu?.Changed();
        _timer = new Timer(_ => Tick(), null, 1000, 250);
    }

    public void Dispose() => _timer.Dispose();

    // --- confirmations ---------------------------------------------------------------------------------------------

    string? Kind(string pump, string op, JsonObject a)
    {
        if (pump != "SojournPeople") return null;
        switch (op)
        {
            case "removeFriend" or "removeFavorite" or "clearTeleportHistory":
                return "delete";
            case "contactSet" when a.Bool("remove", false):
                return "delete";
            case "friendRights":
                // only a change waits; asking what they are doesn't
                return _s.People.Friend(a.Str("to") ?? a.Str("id")) is { } f && _s.People.WantedRights(f, a) != f.TheirFriendRights ? "permissions" : null;
            case "setExperience" when (a.Str("permission") ?? "").Trim().Equals("allow", StringComparison.OrdinalIgnoreCase):
                return "permissions";
            case "setResponse" when (a.Str("text") ?? "").Trim().Length > 0 || a.Bool("on") == true:
                return "words";
            // a name everyone sees, and on Second Life the week's one change: only one that could go is asked
            case "setDisplayName":
                return _s.NamesAndNotes.Refusal(a) is null ? "words" : null;
            // a member's text stopped (or allowed) for everyone in the group's chat; one that can't go fails without asking
            case "moderateGroupChat":
                return ModerationCould(a) ? "permissions" : null;
            // notes that say something else, replaced: the person's words gone, as a delete
            case "setNotes":
                return _s.NamesAndNotes.Replacing(a) is not null ? "delete" : null;
        }
        return null;
    }

    static string YesNo(bool b) => b ? "yes" : "no";

    /// <summary>Whether a moderation could go (a group of theirs, its chat on, and not known to be outside their powers).</summary>
    bool ModerationCould(JsonObject a)
    {
        try
        {
            var g = _s.GroupChat.Group(a);
            if (_s.GroupChat.ChatOff(g) || _s.Client is not { } c) return false;
            return _s.GroupChat.IModerate(c, g) || !c.Self.GroupChatSessions.ContainsKey(g);
        }
        catch (Sojourn.Core.Leap.LeapError) { return false; }
    }

    async Task<(string Title, string Detail)> DescribeAsync(string pump, string op, JsonObject a)
    {
        var people = _s.People;
        switch (op)
        {
            case "removeFriend" when people.Friend(a.Str("to") ?? a.Str("id")) is { } f:
                return ($"Remove {people.NameOf(f.UUID)} from your friends",
                    "You stop seeing each other online and on the map" + ((f.TheirFriendRights & FriendRights.CanModifyObjects) != 0
                        ? ", and their rights to edit your objects end." : "."));
            case "friendRights" when people.Friend(a.Str("to") ?? a.Str("id")) is { } f:
            {
                var now = f.TheirFriendRights;
                var want = people.WantedRights(f, a);
                var parts = new List<string>();
                void Line(string what, FriendRights bit)
                {
                    bool was = (now & bit) != 0, will = (want & bit) != 0;
                    parts.Add(was == will ? $"{what}: {YesNo(will)}" : $"{what}: {YesNo(was)} → {(will ? "YES" : "NO")}");
                }
                Line("see you online", FriendRights.CanSeeOnline);
                Line("find you on the map", FriendRights.CanSeeOnMap);
                Line("edit, delete or take your objects", FriendRights.CanModifyObjects);
                var warn = (want & FriendRights.CanModifyObjects) != 0 && (now & FriendRights.CanModifyObjects) == 0
                    ? " They could then change, delete or take ANY of your objects in-world." : "";
                return ($"Change what {people.NameOf(f.UUID)} may do", string.Join("; ", parts) + "." + warn);
            }
            case "contactSet":
            {
                var name = a.Str("name") ?? "";
                int n = 0;
                try { n = (people.ContactSets(new JsonObject { ["name"] = name })["sets"] as JsonArray)?.FirstOrDefault()?["members"]?.AsArray().Count ?? 0; }
                catch { }
                return ($"Delete the contact set \"{name}\"", $"{n} {(n == 1 ? "person" : "people")} in it; friends stay friends.");
            }
            case "removeFavorite":
            {
                var name = UUID.TryParse(a.Str("item_id") ?? "", out var id) && _s.Inventory.Node(id) is { } node ? $"\"{node.Name}\"" : "a favourite";
                return ($"Remove the favourite {name}", "It goes to your Trash.");
            }
            case "clearTeleportHistory":
            {
                var kept = _s.Places.HistoryJson(new JsonObject { ["count"] = 1 }).Int("kept", 0);
                return ("Clear your teleport history", $"{kept} place{(kept == 1 ? "" : "s")}, kept only on this computer.");
            }
            case "setExperience":
            {
                var id = a.Uuid("id") ?? UUID.Zero;
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var name = await _s.Experiences.NameAsync(id, cts.Token);
                return (name is { Length: > 0 } ? $"Allow the experience \"{name}\"" : "Allow an experience",
                    "Its scripts can then act for you wherever it runs without asking each time: take your controls, animate you, "
                    + "attach things to you, teleport you, and track or control your camera.");
            }
            case "setResponse" when (a.Str("kind") ?? "").Trim().ToLowerInvariant() is "teleports" or "friendships":
            {
                bool tp = a.Str("kind")!.Trim().ToLowerInvariant() == "teleports";
                var text = (a.Str("text") ?? "").Trim();
                var words = text.Length > 0 ? text : tp ? Rejects.DefaultTeleportsResponse : Rejects.DefaultFriendshipsResponse;
                return ($"Turn away {(tp ? "teleport offers and requests" : "friendship offers")} with:",
                    $"“{words}” Sent to each person whose {(tp ? "teleport" : "friendship")} offer is turned away, as your words.");
            }
            case "setDisplayName":
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var (send, reset, mine, _) = await _s.NamesAndNotes.PlanAsync(a, cts.Token);
                var user = mine.UserName is { Length: > 0 } u ? u : _s.Names.Json(_s.AgentId)?.Str("username") ?? "";
                var shown = reset ? user : send;
                return (reset ? $"Set your display name back to your username, “{shown}”" : $"Change your display name to “{shown}”",
                    $"Everyone sees it over you, in chat and in your profile (now “{mine.DisplayName}”). Second Life lets you change it once a week.");
            }
            case "moderateGroupChat":
            {
                var g = _s.GroupChat.Group(a);
                var gname = _s.State.Groups.TryGetValue(g, out var grp) ? grp.Name : "the group";
                var who = a.Str("to") ?? a.Str("id") ?? "";
                string name = who;
                if (_s.Client is { } c)
                    try { name = _s.People.NameOf(_s.GroupChat.Member(c, g, who)); } catch (Sojourn.Core.Leap.LeapError) { }
                bool allow = a.Bool("text") ?? a.Bool("allow") ?? false;
                return allow
                    ? ($"Let {name} type in {gname}'s chat again", "As a moderator, for everyone in the chat.")
                    : ($"Stop {name}'s text in {gname}'s chat", "As a moderator: nobody in the chat sees what they type until you allow it again.");
            }
            case "setNotes" when _s.NamesAndNotes.Replacing(a) is { } r:
            {
                static string Start(string t) => (t = t.ReplaceLineEndings(" ").Trim()).Length > 120 ? t[..120].TrimEnd() + "…" : t;
                var text = a.Str("text") ?? "";
                return ($"Replace your notes on {people.NameOf(r.Id)}",
                    $"They say now: “{Start(r.Was)}”" + (text.Trim().Length == 0 ? " They'd be cleared." : $" New: “{Start(text)}”")
                    + " The old text is gone after.");
            }
            case "setResponse":
            {
                var kind = (a.Str("kind") ?? "dnd").Trim().ToLowerInvariant();
                var text = (a.Str("text") ?? "").Trim();
                var words = text.Length > 0 ? text : kind == "away" ? _s.Presence.AwayResponse : _s.Presence.DndResponse;
                var when = kind == "away" ? "while you're away" : "while you're in Do Not Disturb";
                if (kind == "away" && a.Bool("on") == false && text.Length > 0)
                    return ("Set your away reply (not sent until you turn it on)", $"“{words}”");
                return ($"Reply to IMs {when} with:", $"“{words}” Sent once to each person who IMs you then, as your words.");
            }
        }
        return ($"{pump}.{op}", a.ToJsonString().Length > 80 ? a.ToJsonString()[..80] + "..." : a.ToJsonString());
    }

    /// <summary>A pay to an object: refused before it's asked when its pay window wouldn't offer the amount (the
    /// viewer's pay floater shows only the buttons then); else its buttons said on the confirmation.</summary>
    async Task<(string? Refusal, string Note)> PayCheckAsync(JsonObject a)
    {
        if (a.Uuid("to") is not { } id || !_s.VisibleRegions().Any(r => r.PrimById(id) != null)) return (null, "");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (await _s.People.PayPricesAsync(id, cts.Token) is not { } p) return (null, "");
            int amount = a.Int("amount", 0);
            if (PeopleOps.PriceRefusal(p.Default, p.Buttons, amount) is { } no) return (no + "; nothing was paid", "");
            var buttons = p.Buttons.Where(b => b > 0).ToList();
            return (null, buttons.Count > 0 ? $"Its pay buttons: {string.Join(", ", buttons.Select(b => $"L${b}"))}." : "");
        }
        catch (Exception e) { _log.LogDebug(e, "pay prices"); return (null, ""); }
    }

    // --- the menu and settings --------------------------------------------------------------------------------------

    static readonly int[] AwayChoices = Presence.AwayAfterChoices;
    static string Minutes(int m) => m == 0 ? "never" : m < 60 ? $"{m} min" : $"{m / 60.0:0.#} h";
    static string TeleportLabel(string mode) => mode switch { "all" => "all", "not_friends" => "not friends'", _ => "none" };   // under "turn away": none of them
    static readonly string[] TeleportOrder = { "off", "not_friends", "all" };   // as they grow
    /// <summary>Whether anything of the person's is turned away (the turn-away group's faint "some").</summary>
    static bool Busy(Rejects rj) => rj.FriendsOnly || rj.RejectTeleports != "off" || rj.RejectFriendships || rj.RejectGroupInvites;
    static string Metres(double h) => Math.Abs(h) < 0.0005 ? "0 m" : $"{h:+0.00;-0.00} m";

    double? _hoverWant;
    DateTime _hoverDue;
    double Hover => _hoverWant ?? _s.Presence.HoverHeight;

    public void Settle(ISettingsMenu? menu, SettingsApi api)
    {
        _menu = menu;
        var me = _s.Presence;
        var rj = _s.Rejects;
        if (menu != null) menu.PeopleRows = () =>
        {
            var rows = new List<(MenuRow Row, Action<int> Act)>
            {
                (MenuRow.Switch("do not disturb", me.DoNotDisturb), _ => me.SetDoNotDisturb(!me.DoNotDisturb)),
                (MenuRow.Switch("away", me.Away, note: me.AwayIsAuto ? "by itself" : null), _ => me.SetAway(!me.Away)),
                (new MenuRow("away after", Minutes(me.AwayAfterMinutes), true), d =>
                {
                    int i = Array.IndexOf(AwayChoices, me.AwayAfterMinutes);
                    me.SetAwayAfter(AwayChoices[Math.Clamp((i < 0 ? 2 : i) + (d == 0 ? 1 : d), 0, AwayChoices.Length - 1)]);
                }),
                (new MenuRow("hover height", Metres(Hover), true), d => StepHover(d == 0 ? 1 : d)),
                (MenuRow.Switch("show typing", me.TypingSent), _ => me.SetTypingSent(!me.TypingSent)),
                // who reaches the person, with voice's call switches (Voice/VoiceWiring.cs) beside them
                MenuRow.Group("turn away", () =>
                {
                    var away = new List<(MenuRow Row, Action<int> Act)>
                    {
                        (MenuRow.Switch("only friends IM me", rj.FriendsOnly), _ => rj.SetFriendsOnly(!rj.FriendsOnly)),
                        (MenuRow.Choice("teleports", TeleportOrder.Select(TeleportLabel).ToArray(), Array.IndexOf(TeleportOrder, rj.RejectTeleports)),
                            i => rj.SetRejectTeleports(TeleportOrder[i])),
                        (MenuRow.Switch("friendships", rj.RejectFriendships), _ => rj.SetRejectFriendships(!rj.RejectFriendships)),
                        (MenuRow.Switch("group invites", rj.RejectGroupInvites), _ => rj.SetRejectGroupInvites(!rj.RejectGroupInvites)),
                    };
                    foreach (var call in new[] { api.SwitchRow("reject_p2p_calls", "one-to-one calls"), api.SwitchRow("reject_group_calls", "group calls"),
                                 api.SwitchRow("reject_conference_calls", "conference calls") })
                        if (call is { } c) away.Add(c);
                    return away;
                }, Busy(rj) || api.IsOn("reject_p2p_calls") || api.IsOn("reject_group_calls") || api.IsOn("reject_conference_calls") ? "some" : ""),
            };
            // one group's calls turned away (Voice/CallRejects.cs asks), when there's voice and groups to list
            if (api.Has("reject_group_calls") && !_s.State.Groups.IsEmpty)
            {
                int quieted = _s.State.Groups.Keys.Count(rj.CallsTurnedAway);
                rows.Add(MenuRow.Group("turn away calls from", () => _s.State.Groups.Values
                    .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(g => (MenuRow.Switch(g.Name, rj.CallsTurnedAway(g.ID)), (Action<int>)(_ => rj.SetCallsTurnedAway(g.ID, !rj.CallsTurnedAway(g.ID)))))
                    .ToList(), api.IsOn("reject_group_calls") ? "all" : quieted > 0 ? $"{quieted}" : ""));
            }
            return rows;
        };
        api.Add("do_not_disturb", new("you", "Do Not Disturb: IMs and offers are kept but not shown, each person who IMs gets the "
            + "autoresponse once (the person's own words, set with SojournPeople setResponse, which they confirm), objects' IMs and "
            + "offers are turned away; others see them busy", "on, off",
            () => JsonValue.Create(me.DoNotDisturb ? "on" : "off"), SettingsApi.Switch("do_not_disturb", me.SetDoNotDisturb)));
        api.Add("away", new("you", "shown to others as away (set by itself after away_after without them, and cleared when they're back)", "on, off",
            () => JsonValue.Create(me.Away ? "on" : "off"), SettingsApi.Switch("away", on => me.SetAway(on))));
        api.Add("away_after", new("you", "minutes without the person (no head, hands, keys or talk) before they show as away; 0: never",
            string.Join(", ", AwayChoices), () => JsonValue.Create(me.AwayAfterMinutes), v =>
            {
                if (Json.AsDouble(v) is not { } d || !AwayChoices.Contains((int)d)) return $"away_after is one of {string.Join(", ", AwayChoices)} (minutes)";
                me.SetAwayAfter((int)d);
                return null;
            }));
        api.Add("hover_height", new("you", "raises or lowers the person's avatar so its feet meet the floor, kept with the account (metres)",
            "-2 to 2", () => JsonValue.Create(Math.Round(Hover, 3)), _ => null)
        {
            SetAsync = async (v, ct) =>
            {
                if (Json.AsDouble(v) is not { } d) return "hover_height takes metres, -2 to 2";
                try { _hoverWant = null; await me.SetHoverHeightAsync(d, ct); return null; }
                catch (Sojourn.Core.Leap.LeapError e) { return e.Message; }
            },
        });
        api.Add("typing_indicator", new("you", "others see when the person is writing to them (nearby or in an IM)", "on, off",
            () => JsonValue.Create(me.TypingSent ? "on" : "off"), SettingsApi.Switch("typing_indicator", me.SetTypingSent)));
        api.Add("friends_only_ims", new("you", "only friends and groups can IM or call the person (the official viewer's privacy setting): "
            + "others' IMs aren't shown (a quiet notice says so once a conversation), their teleport offers and requests are dropped, "
            + "and conferences they start are left", "on, off",
            () => JsonValue.Create(rj.FriendsOnly ? "on" : "off"), SettingsApi.Switch("friends_only_ims", rj.SetFriendsOnly)));
        api.Add("reject_teleports", new("you", "turn away teleport offers and requests (Firestorm's): all, or not_friends (friends' still come); "
            + "each is answered with a sentence (the person's own words through SojournPeople setResponse kind teleports)",
            string.Join(", ", Rejects.TeleportModes), () => JsonValue.Create(rj.RejectTeleports),
            SettingsApi.Choice("reject_teleports", Rejects.TeleportModes, i => rj.SetRejectTeleports(Rejects.TeleportModes[i]))));
        api.Add("reject_friendships", new("you", "turn away friendship offers (Firestorm's), each answered with a sentence (setResponse kind friendships)",
            "on, off", () => JsonValue.Create(rj.RejectFriendships ? "on" : "off"), SettingsApi.Switch("reject_friendships", rj.SetRejectFriendships)));
        api.Add("reject_group_invites", new("you", "turn away group invitations without a word (Firestorm's)", "on, off",
            () => JsonValue.Create(rj.RejectGroupInvites ? "on" : "off"), SettingsApi.Switch("reject_group_invites", rj.SetRejectGroupInvites)));
        api.Add("auto_accept_inventory", new("you", "keep what's offered without asking: viewer (notecards, textures and landmarks, as the "
            + "official viewer), all (anything, as Firestorm), or off; a notice says what came", string.Join(", ", Rejects.InventoryModes),
            () => JsonValue.Create(rj.AutoAcceptInventory),
            SettingsApi.Choice("auto_accept_inventory", Rejects.InventoryModes, i => rj.SetAutoAcceptInventory(Rejects.InventoryModes[i]))));
        api.Add("money_notices", new("you", "L$ received and spent shown on the feed (both viewers show both)", string.Join(", ", Money.NoticeModes),
            () => JsonValue.Create(_s.Money.Notices), SettingsApi.Choice("money_notices", Money.NoticeModes, i => _s.Money.SetNotices(Money.NoticeModes[i]))));
        api.Add("typing_animation", new("you", "the avatar plays the typing animation while the person writes in nearby chat", "on, off",
            () => JsonValue.Create(me.TypingAnimation ? "on" : "off"), SettingsApi.Switch("typing_animation", me.SetTypingAnimation)));
    }

    void StepHover(int dir)
    {
        _hoverWant = Math.Clamp(Math.Round(Hover + dir * 0.02, 3), Presence.MinHover, Presence.MaxHover);
        _hoverDue = DateTime.UtcNow + TimeSpan.FromSeconds(0.4);   // a run of presses sends once
        _menu?.Changed();
    }

    // --- four times a second ----------------------------------------------------------------------------------------

    int _ticks;
    bool _dictTyping;

    void Tick()
    {
        try
        {
            if (_s.Client is not { Network.Connected: true } c) return;
            _ticks++;
            var me = _s.Presence;
            me.Tick();
            if (_ticks % 2 == 0) _s.Places.Tick();
            // the away flag rides every update; the client's movement keys write the same field from another thread
            if (c.Self.Movement.Away != me.Away) c.Self.Movement.Away = me.Away;
            if (_hoverWant is { } h && DateTime.UtcNow >= _hoverDue)
            {
                _hoverWant = null;
                _ = me.SetHoverHeightAsync(h, CancellationToken.None).ContinueWith(t => _log.LogInformation("hover height: {R}",
                    t.IsCompletedSuccessfully ? t.Result.ToJsonString() : t.Exception?.GetBaseException().Message));
            }
            Typing();
            Away(c);
        }
        catch (Exception e) { if (_ticks % 240 == 1) _log.LogWarning(e, "people"); }
    }

    /// <summary>Dictating to people (Private/Dictation.cs: both grips, or Y) shows them typing: nearby, or the open
    /// one-to-one IM's other person (the viewer sends IM typing only one to one). Only while the words are being
    /// spoken: a draft left waiting on the panel isn't writing (it showed the person typing for minutes, 2026-10-29).</summary>
    void Typing()
    {
        bool writing = _dictation is { } dict && dict.Held;
        if (writing)
        {
            var (to, _, _) = _dictation!.Destination();
            UUID? target = to switch { DraftTo.Nearby => UUID.Zero, DraftTo.Im => _dictation.OpenImWith(), _ => null };
            if (target is { } t) { _s.Presence.Typing(true, t == UUID.Zero ? null : t); _dictTyping = true; return; }
        }
        if (_dictTyping) { _s.Presence.Typing(false); _dictTyping = false; }
    }

    // (SLurls on the feed and in the reader are links since batch B4: a press on one offers its place, Social/Links.cs;
    // opening a block or a notecard no longer offers every place in it)

    // --- away by itself ---------------------------------------------------------------------------------------------

    DateTime _activeAt = DateTime.UtcNow;
    Vector3 _refPos, _refDir;
    uint _lastInput;

    /// <summary>The person is there if their head (or the desktop's view) moved, they talked to the assistant or typed,
    /// or, on the desktop, the PC had input while Sojourn's window was in front. Gone for away_after: away; back: not.</summary>
    void Away(GridClient c)
    {
        bool active = (_a.Talking && !_a.Latched) || _a.Typing || _dictation?.Held == true;   // (a mic tapped open and forgotten isn't presence)
        if (_s.State.CameraOverride is { } cam)
        {
            var dir = cam.Focus - cam.Pos;
            if (dir.Length() > 1e-4f) dir = Vector3.Normalize(dir);
            if (Vector3.Distance(cam.Pos, _refPos) > 0.02f || Vector3.Dot(dir, _refDir) < 0.9996f)   // 2 cm or ~1.6 degrees
            {
                active |= _refDir != Vector3.Zero;
                _refPos = cam.Pos;
                _refDir = dir;
            }
        }
        if (!_a.InHeadset && OurWindowInFront() && LastInputTick() is var tick && tick != _lastInput)
        {
            active |= _lastInput != 0;
            _lastInput = tick;
        }
        var me = _s.Presence;
        if (active)
        {
            _activeAt = DateTime.UtcNow;
            if (me.AwayIsAuto) me.SetAway(false);
            return;
        }
        int after = me.AwayAfterMinutes;
        if (after > 0 && !me.Away && DateTime.UtcNow - _activeAt > TimeSpan.FromMinutes(after))
        {
            me.SetAway(true, auto: true);
            _log.LogInformation("away: nobody for {Minutes} min", after);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    static uint LastInputTick()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? info.dwTime : 0;
    }

    static bool OurWindowInFront()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var w = GetForegroundWindow();
        return w != IntPtr.Zero && GetWindowThreadProcessId(w, out var pid) != 0 && pid == (uint)Environment.ProcessId;
    }
}
