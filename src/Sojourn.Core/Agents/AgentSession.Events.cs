using System.Text;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.World;

namespace Sojourn.Core.Agents;

/// <summary>
/// Protocol events -> the events the viewer would post (LLChat, LLTeleport, ClaudeNotify, TesselEvents), with the
/// same shapes (see the per-pump notes in docs/api.md), plus the per-agent state the APIs read.
/// </summary>
public sealed partial class AgentSession
{
    public NameCache Names => World.Names(GridKey);
    public RegionWorld? Region => Client?.Network.CurrentSim is { } sim ? World.Region(GridKey, sim.Handle) : null;
    public Notifications Notify => State.Notify;

    /// <summary>The region worlds this session sees: its own region first, then neighbours it's connected to.</summary>
    public IEnumerable<RegionWorld> VisibleRegions()
    {
        var c = Client;
        if (c?.Network.CurrentSim is not { } cur) yield break;
        if (World.Region(GridKey, cur.Handle) is { } mine) yield return mine;
        foreach (var sim in c.Network.Simulators.ToArray())
            if (sim.Handle != cur.Handle && sim.Connected && World.Region(GridKey, sim.Handle) is { } w) yield return w;
    }

    /// <summary>Add to a position in <paramref name="w"/>'s frame to get it in the agent's region frame.</summary>
    public Vector3 FrameOffset(RegionWorld w)
    {
        var cur = Client?.Network.CurrentSim;
        if (cur is null || cur.Handle == w.Handle) return Vector3.Zero;
        Utils.LongToUInts(w.Handle, out var wx, out var wy);
        Utils.LongToUInts(cur.Handle, out var cx, out var cy);
        return new Vector3((float)((double)wx - cx), (float)((double)wy - cy), 0);
    }


    void WireEvents(GridClient c)
    {
        c.Self.ChatFromSimulator += (_, e) => Guard("chat", () => OnChat(c, e));
        c.Self.IM += (_, e) => Guard("im", () => OnIM(c, e.IM));
        c.Self.ScriptDialog += (_, e) => Guard("dialog", () => OnScriptDialog(c, e));
        // from the packet, not LibreMetaverse's ScriptQuestion event: that drops the experience a question comes from
        c.Network.RegisterCallback(global::LibreMetaverse.Packets.PacketType.ScriptQuestion, (_, e) => Guard("question", () => OnScriptQuestion(c, e)));
        c.Network.RegisterCallback(global::LibreMetaverse.Packets.PacketType.ScriptControlChange, (_, e) => Guard("controls", () => OnScriptControl(e)), false);   // in order: a release then a take
        c.Self.LoadURL += (_, e) => Guard("loadurl", () => OnLoadUrl(e));
        c.Self.AlertMessage += (_, e) => Guard("alert", () => OnAlert(c, e));
        c.Self.TeleportProgress += (_, e) => Guard("teleport", () => OnTeleport(e));
        c.Self.MoneyBalanceReply += (_, e) => Guard("money", () => OnMoney(c, e));
        c.Self.MoneyBalance += (_, e) => Guard("balance", () => _balanceKnown = true);
        c.Self.AvatarSitResponse += (_, e) => Guard("sit", () => c.Self.Sit());
        c.Groups.CurrentGroups += (_, e) => Guard("groups", () =>
        {
            State.Groups.Clear();
            foreach (var (id, g) in e.Groups) State.Groups[id] = g;
            foreach (var s in State.Ims.Values) if (s.Type == "p2p" && IsMyGroup(s.Id)) Retype(s);
        });
        c.Self.GroupChatJoined += (_, e) => Guard("groupchat", () => OnGroupChatJoined(e));
        c.Objects.ObjectDataBlockUpdate += (_, e) => Guard("block", () =>
        {
            State.Flags[e.Prim.LocalID] = (PrimFlags)e.Block.UpdateFlags;
        });
        // compressed updates carry the flags too (OpenSim and SL send most changes this way; phantom, scripted, touch
        // set after the first full update never reached State.Flags), but raise no block event: read them here
        c.Network.RegisterCallback(global::LibreMetaverse.Packets.PacketType.ObjectUpdateCompressed, (_, e) => Guard("compressed", () =>
        {
            foreach (var b in ((global::LibreMetaverse.Packets.ObjectUpdateCompressedPacket)e.Packet).ObjectData)
                if (b.Data is { Length: >= 20 })
                    State.Flags[Utils.BytesToUInt(b.Data, 16)] = (PrimFlags)b.UpdateFlags;
        }), false);
        c.Objects.ObjectUpdate += (_, e) => Guard("objupdate", () => OnObjectUpdate(c, e));
        c.Objects.KillObject += (_, e) => Guard("kill", () => State.Flags.TryRemove(e.ObjectLocalID, out PrimFlags _));
        c.Objects.ObjectProperties += (_, e) => Guard("props", () => OnProperties(e.Simulator, e.Properties, full: true));
        c.Objects.ObjectPropertiesFamily += (_, e) => Guard("propsfamily", () => OnProperties(e.Simulator, e.Properties, full: false));
        c.Friends.FriendOnline += (_, e) => Guard("friend", () => FriendNotice(e.Friend, true));
        c.Friends.FriendOffline += (_, e) => Guard("friend", () => FriendNotice(e.Friend, false));
        c.Network.SimChanged += (_, e) => Guard("simchanged", () => OnRegionChanged(c, e));
        c.Self.MuteListUpdated += (_, _) => Guard("mutelist", () => Mutes.Merge(c));
        Ears.Wire(c);
        Look.Wire(c);
        Anims.Wire(c);
        Safety.Wire(c);
        CrossingGuard.Attach(c, Log, msg => Notify.Add("CrossingFailed", "notify", msg, Notifications.None));
        SelfUpdateGuard.Attach(c, Log);
        ChildCapsGuard.Attach(c, Log);
        CircuitGuard.Attach(c, Log);
        SelfTrace.Attach(c, Log);
        Teleports.Wire(c);
        WirePeople(c);
    }

    bool _balanceKnown;
    public bool BalanceKnown => _balanceKnown;

    void OnLoggedIn(GridClient c)
    {
        State.ResetForLogin();
        Ears.Reset();
        _balanceKnown = false;
        ResetControlsTaken();
        ReadMaturity(c);
        Names.SetLegacy(c.Self.AgentID, $"{Config.First} {Config.Last}");
        c.Self.RequestBalance();
        c.Groups.RequestCurrentGroups();
        c.Self.RequestMuteList();
        StartBackgroundWork(c);
        Look.OnLoggedIn(c);
        PeopleLoggedIn(c);
    }

    void OnLoggedOut()
    {
        StopBackgroundWork();
    }

    static string AgentSlurl(UUID id) => $"secondlife:///app/agent/{id}/about";

    // ---------- chat ----------

    /// <summary>Firestorm's own worn LSL bridge talking to its viewer by llOwnerSay (its URL, its auth key...):
    /// Firestorm keeps that out of chat, and so does everything here that shows, reads or logs chat (it showed on the
    /// feed 2026-10-29, and in Sojourn Access's session log, the one testers may share, 2026-10-30).</summary>
    public static bool IsBridgeTalk(ChatEventArgs e) =>
        e.Type == ChatType.OwnerSay && (e.FromName ?? "").StartsWith("#Firestorm LSL Bridge", StringComparison.Ordinal);

    void OnChat(GridClient c, ChatEventArgs e)
    {
        if (e.Type is ChatType.StartTyping or ChatType.StopTyping) return;
        if (Ao.TakeOwnerSay(c, e)) return;   // a worn relay switching the AO (Agents/AoDriver.cs), kept out of chat
        if (IsBridgeTalk(e)) return;
        if (e.AudibleLevel != ChatAudibleLevel.Fully) return;
        if (e.SourceType == ChatSourceType.Agent) Private.WordsFrom?.Invoke(e.SourceID, "chat");
        var msg = (e.Message ?? "").Replace("\t", "    ");
        var style = 0;
        if (msg.StartsWith("/me ") || msg.StartsWith("/me'"))
        {
            style = 1;
            msg = msg[3..];
        }
        else if (e.Type == ChatType.Whisper) msg = "whispers: " + msg;
        else if (e.Type == ChatType.Shout) msg = "shouts: " + msg;
        if (e.SourceID != c.Self.AgentID && e.OwnerID != c.Self.AgentID && Mutes.IsMuted(e.SourceID, e.FromName, e.OwnerID)) return;
        // antispam (Agents/Safety.Antispam.cs): an object by its id, a resident by theirs, a long message by its owner
        if (e.Type != ChatType.Debug && (Safety.Spam(Safety.Queue.Chat, e.SourceID, e.FromName, mine: e.OwnerID == c.Self.AgentID)
            || Safety.NewlineFlood(e.SourceType == ChatSourceType.Object ? e.OwnerID : e.SourceID, e.FromName, msg, mine: e.OwnerID == c.Self.AgentID))) return;
        // script errors (DEBUG_CHANNEL): the server sends them to everyone in range, and viewers show only the ones
        // from objects you own. OpenSim's reach is wider still (both regions heard one anklet). Unknown owner: keep it.
        if (e.Type == ChatType.Debug && e.SourceType == ChatSourceType.Object
            && e.OwnerID != UUID.Zero && e.OwnerID != c.Self.AgentID) return;
        var from = e.SourceType == ChatSourceType.Agent ? Names.Complete(e.SourceID, e.FromName) : e.FromName;
        Events.Log("LLChat", new JsonObject
        {
            ["message"] = msg,
            ["from"] = from,
            ["from_id"] = e.SourceID.ToString(),
            ["time"] = Math.Round(State.Clock.Elapsed.TotalSeconds, 3),
            ["source"] = (int)e.SourceType,
            ["chat_type"] = (int)e.Type,
            ["chat_style"] = style,
            ["sender_slurl"] = e.SourceType == ChatSourceType.Agent ? AgentSlurl(e.SourceID) : "",
            ["owner_id"] = e.OwnerID.ToString(),
            ["position"] = Json.Arr(e.Position),
        });
        if (e.SourceID == c.Self.AgentID && e.Type is ChatType.Normal or ChatType.Whisper or ChatType.Shout)
            State.LastWords = e.Message;
        if (e.Type == ChatType.Debug && e.SourceType == ChatSourceType.Object)
            Scripts.OnDebugChat(e);
    }

    // ---------- IMs and the offers that travel as IMs ----------

    void OnIM(GridClient c, InstantMessage im)
    {
        var me = c.Self.AgentID;
        // blocked: their IMs, offers, teleport and friendship requests don't reach me (the viewer drops them too)
        if (im.FromAgentID != me && im.FromAgentID != UUID.Zero && Mutes.IsMuted(im.FromAgentID, im.FromAgentName)) return;
        if (im.Dialog is InstantMessageDialog.MessageFromObject or InstantMessageDialog.TaskInventoryOffered && Mutes.IsMuted(im.IMSessionID)) return;
        // antispam: everything that travels as an IM counts against the resident it's from (offline ones and typing don't)
        if (im.Dialog is not (InstantMessageDialog.StartTyping or InstantMessageDialog.StopTyping) && im.Offline != InstantMessageOnline.Offline
            && (Safety.Spam(Safety.Queue.Im, im.FromAgentID, im.FromAgentName) || Safety.NewlineFlood(im.FromAgentID, im.FromAgentName, im.Message))) return;
        if (Presence.Holds(c, im)) return;   // Do Not Disturb turns some away (Agents/Presence.cs)
        if (PeopleTake(c, im)) return;       // the person's switches: rejects, friends only, a group's chat snoozed or off
        switch (im.Dialog)
        {
            case InstantMessageDialog.MessageFromAgent:
            case InstantMessageDialog.BusyAutoResponse:
            case InstantMessageDialog.SessionSend:
                if (im.FromAgentID == me) return;     // our own group lines echoed back: the "out" event covered them
                if (string.IsNullOrEmpty(im.Message)) return;
                Private.WordsFrom?.Invoke(im.FromAgentID, im.Dialog == InstantMessageDialog.SessionSend ? "group" : "im");
                ImIn(c, im);
                return;
            case InstantMessageDialog.MessageFromObject:
                Events.Log("TesselEvents", new JsonObject
                {
                    ["event"] = "object_im",
                    ["object_id"] = im.IMSessionID.ToString(),
                    ["owner_id"] = im.FromAgentID.ToString(),
                    ["from"] = im.FromAgentName,
                    ["message"] = im.Message,
                    ["slurl"] = BucketString(im.BinaryBucket),
                    ["timestamp"] = Unix(im.Timestamp),
                });
                return;
            case InstantMessageDialog.MessageBox:
                Notify.Add("SystemMessage", "notify", im.Message, Notifications.Ok,
                    substitutions: new JsonObject { ["MESSAGE"] = im.Message });
                return;
            case InstantMessageDialog.InventoryOffered:
            case InstantMessageDialog.TaskInventoryOffered:
                InventoryOffer(c, im);
                return;
            case InstantMessageDialog.InventoryAccepted:
            case InstantMessageDialog.InventoryDeclined:
            {
                var name = im.Dialog == InstantMessageDialog.InventoryAccepted ? "InventoryAccepted" : "InventoryDeclined";
                var verb = im.Dialog == InstantMessageDialog.InventoryAccepted ? "received" : "declined";
                Notify.Add(name, "notifytip", $"{im.FromAgentName} {verb} your inventory offer.", Notifications.None,
                    substitutions: new JsonObject { ["NAME"] = im.FromAgentName });
                return;
            }
            case InstantMessageDialog.FriendshipOffered:
                FriendshipOffer(c, im);
                return;
            case InstantMessageDialog.FriendshipAccepted:
            case InstantMessageDialog.FriendshipDeclined:
            {
                var ok = im.Dialog == InstantMessageDialog.FriendshipAccepted;
                Notify.Add(ok ? "FriendshipAccepted" : "FriendshipDeclined", "notify",
                    $"{im.FromAgentName} {(ok ? "accepted" : "declined")} your friendship offer.", Notifications.Ok,
                    payload: new JsonObject { ["from_id"] = im.FromAgentID.ToString() },
                    substitutions: new JsonObject { ["NAME"] = im.FromAgentName });
                return;
            }
            case InstantMessageDialog.RequestTeleport:
            case InstantMessageDialog.GodLikeRequestTeleport:
                TeleportOffer(c, im);
                return;
            case InstantMessageDialog.RequestLure:
                TeleportRequest(c, im);
                return;
            case InstantMessageDialog.GroupInvitation:
                GroupInvite(c, im);
                return;
            case InstantMessageDialog.GroupNotice:
            {
                var parts = (im.Message ?? "").Split('|', 2);
                var subject = parts[0];
                var body = parts.Length > 1 ? parts[1] : "";
                Notify.Add("GroupNotice", "groupnotify", $"Topic: {subject}, Message: {body}", Notifications.Ok,
                    payload: new JsonObject { ["group_id"] = im.FromAgentID.ToString(), ["sender_name"] = im.FromAgentName },
                    substitutions: new JsonObject { ["SUBJECT"] = subject, ["MESSAGE"] = body, ["SENDER"] = im.FromAgentName });
                return;
            }
            case InstantMessageDialog.GotoUrl:
            {
                var url = BucketString(im.BinaryBucket);
                Notify.Add("GotoURL", "notify", $"{im.Message}\n\n{url}",
                    new[] { new NotifyButton("Later", "Later", 0), new NotifyButton("GoNow...", "Go now...", 1) },
                    payload: new JsonObject { ["url"] = url },
                    substitutions: new JsonObject { ["MESSAGE"] = im.Message, ["URL"] = url },
                    respond: (_, _) => Task.CompletedTask);
                return;
            }
            case InstantMessageDialog.FromTaskAsAlert:
                Notify.Add("ObjectMessage", "notifytip", $"{im.FromAgentName}: {im.Message}", Notifications.None,
                    substitutions: new JsonObject { ["NAME"] = im.FromAgentName, ["MESSAGE"] = im.Message });
                return;
            case InstantMessageDialog.SessionAdd:
            case InstantMessageDialog.SessionGroupStart:
            case InstantMessageDialog.SessionCardlessStart:
                if (im.FromAgentID != me && !string.IsNullOrEmpty(im.Message)) ImIn(c, im);
                return;
            default:
                return;
        }
    }

    static long Unix(DateTime t) =>
        // LibreMetaverse 3.2.0 makes offline messages' times from their Unix seconds as ticks: those are the seconds
        t.Year < 2000 && t.Ticks is > 1_000_000_000 and < 10_000_000_000 ? t.Ticks
        : t == default || t.Year < 2000 ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : new DateTimeOffset(t.ToUniversalTime()).ToUnixTimeSeconds();

    static string BucketString(byte[]? b) =>
        b is null || b.Length == 0 ? "" : Encoding.UTF8.GetString(b).TrimEnd('\0');

    ImSession SessionFor(GridClient c, InstantMessage im)
    {
        var me = c.Self.AgentID;
        // SL's group chat comes through the event queue as dialog 0 with the group's id as the session: the viewer tells
        // it apart by that id being one of your groups (LLViewerChatterBoxInvitation: gAgent.isInGroup)
        var isGroup = im.Dialog == InstantMessageDialog.SessionSend || im.GroupIM
            || im.Dialog is InstantMessageDialog.SessionGroupStart or InstantMessageDialog.SessionAdd
            || (im.IMSessionID != UUID.Zero && IsMyGroup(im.IMSessionID));
        if (!isGroup)
        {
            var sid = im.IMSessionID != UUID.Zero ? im.IMSessionID : me ^ im.FromAgentID;
            return State.Ims.GetOrAdd(sid, _ => new ImSession
            {
                Id = sid, Type = "p2p", OtherId = im.FromAgentID, Name = Names.DisplayName(im.FromAgentID) ?? im.FromAgentName,
            });
        }
        var groupSid = im.IMSessionID;
        var isMyGroup = IsMyGroup(groupSid);
        var s = State.Ims.GetOrAdd(groupSid, _ => new ImSession
        {
            Id = groupSid, Type = isMyGroup ? "group" : "conference",
            Name = isMyGroup ? GroupName(groupSid) : BucketString(im.BinaryBucket) is { Length: > 0 } n ? n : "Conference",
        });
        if (isMyGroup && s.Type == "p2p") { Retype(s); s.Ready = true; }   // its lines are reaching us: we're in it
        return s;
    }

    /// <summary>A session kept as p2p (from history, or before the group list came) whose id is one of your groups: the
    /// group's chat. Sending to it joins the session first, as for any group.</summary>
    void Retype(ImSession s)
    {
        s.Type = "group";
        s.Name = GroupName(s.Id);
        s.OtherId = UUID.Zero;
        s.Ready = false;
    }

    void ImIn(GridClient c, InstantMessage im)
    {
        var s = SessionFor(c, im);
        var ts = Unix(im.Timestamp);
        State.RecordIm(s, new ImLine(im.FromAgentName, im.FromAgentID, im.Message, ts));
        s.Unread++;
        s.Offline |= im.Offline == InstantMessageOnline.Offline;
        Events.Log("TesselEvents", new JsonObject
        {
            ["event"] = "im",
            ["dir"] = "in",
            ["session_id"] = s.Id.ToString(),
            ["session_type"] = s.Type,
            ["session_name"] = s.Name,
            ["from_id"] = im.FromAgentID.ToString(),
            ["from"] = im.FromAgentName,
            ["message"] = im.Message,
            ["offline"] = im.Offline == InstantMessageOnline.Offline,
            ["dialog"] = s.Type == "p2p" ? 0 : 13,
            ["timestamp"] = ts,
        });
    }

    /// <summary>Records and reports an IM we sent (TesselIM send calls this).</summary>
    public void ImOut(ImSession s, UUID toId, string message)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        State.RecordIm(s, new ImLine(Config.LoginName, AgentId, message, ts));
        State.LastWords = message;
        Events.Log("TesselEvents", new JsonObject
        {
            ["event"] = "im",
            ["dir"] = "out",
            ["session_id"] = s.Id.ToString(),
            ["session_type"] = s.Type,
            ["session_name"] = s.Name,
            ["to_id"] = toId.ToString(),
            ["message"] = message,
            ["dialog"] = s.Type == "p2p" ? 0 : 17,
            ["timestamp"] = ts,
        });
    }

    internal bool IsMyGroup(UUID id) => State.Groups.ContainsKey(id);

    string GroupName(UUID id) => State.Groups.TryGetValue(id, out var g) ? g.Name : id.ToString();

    void OnGroupChatJoined(GroupChatJoinedEventArgs e)
    {
        if (State.Ims.TryGetValue(e.SessionID, out var s) || State.Ims.TryGetValue(e.TmpSessionID, out s))
        {
            s.Ready = e.Success;
            if (!string.IsNullOrEmpty(e.SessionName)) s.Name = e.SessionName;
            if (e.Success) s.Started?.TrySetResult(); else s.Started?.TrySetException(new Exception("session didn't start"));
        }
    }

    void InventoryOffer(GridClient c, InstantMessage im)
    {
        var fromTask = im.Dialog == InstantMessageDialog.TaskInventoryOffered;
        var type = im.BinaryBucket is { Length: > 0 } b ? (AssetType)b[0] : AssetType.Unknown;
        var objectId = !fromTask && im.BinaryBucket is { Length: >= 17 } bb ? new UUID(bb, 1) : UUID.Zero;
        var typeName = AgentInventory.AssetTypeLabel(type);
        var itemName = im.Message ?? "";
        // an object's offer says "'<name>'. (<object> is located at ...)" (SL and OpenSim): the item's name is the quoted part
        if (fromTask && itemName.StartsWith('\''))
        {
            int close = itemName.LastIndexOf("'.", StringComparison.Ordinal);
            if (close < 1) close = itemName.LastIndexOf("' ", StringComparison.Ordinal);
            if (close < 1 && itemName.EndsWith('\'')) close = itemName.Length - 1;
            if (close > 1) itemName = itemName[1..close];
        }
        var mine = fromTask && Region?.PrimById(im.FromAgentID) is { } src && (State.PrimFlagsOf(src) & PrimFlags.ObjectYouOwner) != 0;
        string name, msg;
        IEnumerable<NotifyButton> buttons;
        var subs = new JsonObject
        {
            ["OBJECTFROMNAME"] = im.FromAgentName, ["OBJECTTYPE"] = typeName,
            ["NAME_SLURL"] = AgentSlurl(im.FromAgentID), ["ITEM_SLURL"] = itemName,
        };
        if (fromTask)
        {
            name = mine ? "OwnObjectGiveItem" : "ObjectGiveItem";
            msg = mine
                ? $"Your object named <nolink>{im.FromAgentName}</nolink> has given you this {typeName}: <nolink>{itemName}</nolink>"
                : $"An object named <nolink>{im.FromAgentName}</nolink> has given you this {typeName}: <nolink>{itemName}</nolink>";
            buttons = mine
                ? new[] { new NotifyButton("Keep", "Keep", 0), new NotifyButton("Discard", "Discard", 1) }
                : new[] { new NotifyButton("Keep", "Keep", 0), new NotifyButton("Discard", "Discard", 1), new NotifyButton("Mute", "Block", 2) };
        }
        else
        {
            name = "UserGiveItem";
            msg = $"{AgentSlurl(im.FromAgentID)} has given you this {typeName}: {itemName}";
            buttons = new[] { new NotifyButton("Show", "Keep", 3), new NotifyButton("Discard", "Discard", 1), new NotifyButton("Mute", "Block", 2) };
        }
        Notify.Add(name, "offer", msg, buttons,
            payload: new JsonObject
            {
                ["from_id"] = im.FromAgentID.ToString(), ["from_name"] = im.FromAgentName, ["object_id"] = objectId.ToString(),
                ["session_id"] = im.IMSessionID.ToString(), ["type"] = (int)type, ["item_name"] = itemName, ["from_task"] = fromTask,
            },
            substitutions: subs,
            respond: (button, _) =>
            {
                var keep = button is "Keep" or "Show";
                if (button == "Mute") Mutes.Add(fromTask ? im.IMSessionID : im.FromAgentID, im.FromAgentName, fromTask ? "object" : "resident", c);
                AgentInventory.AnswerOffer(c, im, type, objectId, keep);
                return Task.CompletedTask;
            });
    }

    void FriendshipOffer(GridClient c, InstantMessage im)
    {
        var hasMsg = !string.IsNullOrWhiteSpace(im.Message);
        Notify.Add(hasMsg ? "OfferFriendship" : "OfferFriendshipNoMessage", hasMsg ? "offer" : "notify",
            $"{AgentSlurl(im.FromAgentID)} is offering friendship.\n\n{im.Message}\n\n(By default, you will be able to see each other's online status.)",
            new[] { new NotifyButton("Accept", "Accept", 0), new NotifyButton("Decline", "Decline", 1) },
            payload: new JsonObject
            {
                ["from_id"] = im.FromAgentID.ToString(), ["session_id"] = im.IMSessionID.ToString(),
                ["online"] = im.Offline == InstantMessageOnline.Online, ["sender"] = im.FromAgentName,
            },
            substitutions: new JsonObject { ["NAME_SLURL"] = AgentSlurl(im.FromAgentID), ["MESSAGE"] = im.Message, ["NAME"] = im.FromAgentName },
            respond: (button, _) =>
            {
                if (button == "Accept") c.Friends.AcceptFriendship(im.FromAgentID, im.IMSessionID);
                else if (button == "Decline") c.Friends.DeclineFriendship(im.FromAgentID, im.IMSessionID);
                return Task.CompletedTask;
            });
    }

    void TeleportOffer(GridClient c, InstantMessage im)
    {
        Notify.Add("TeleportOffered", "offer",
            $"{AgentSlurl(im.FromAgentID)} has offered to teleport you to their location:\n\n“{im.Message}”",
            new[] { new NotifyButton("Teleport", "Teleport", 0), new NotifyButton("Cancel", "Cancel", 1) },
            payload: new JsonObject
            {
                ["from_id"] = im.FromAgentID.ToString(), ["lure_id"] = im.IMSessionID.ToString(),
                ["godlike"] = im.Dialog == InstantMessageDialog.GodLikeRequestTeleport, ["region_maturity"] = 0,
            },
            substitutions: new JsonObject { ["NAME_SLURL"] = AgentSlurl(im.FromAgentID), ["MESSAGE"] = im.Message, ["NAME"] = im.FromAgentName },
            respond: (button, _) =>
            {
                c.Self.TeleportLureRespond(im.FromAgentID, im.IMSessionID, button == "Teleport");
                return Task.CompletedTask;
            });
    }

    void TeleportRequest(GridClient c, InstantMessage im)
    {
        Notify.Add("TeleportRequest", "offer",
            $"{AgentSlurl(im.FromAgentID)} is requesting to be teleported to your location.\n{im.Message}\n\nOffer a teleport?",
            new[] { new NotifyButton("Yes", "Yes", 0), new NotifyButton("No", "No", 1) },
            payload: new JsonObject { ["from_id"] = im.FromAgentID.ToString(), ["lure_id"] = im.IMSessionID.ToString() },
            substitutions: new JsonObject { ["NAME_SLURL"] = AgentSlurl(im.FromAgentID), ["MESSAGE"] = im.Message, ["NAME"] = im.FromAgentName },
            respond: (button, resp) =>
            {
                if (button == "Yes") c.Self.SendTeleportLure(im.FromAgentID, resp.Str("message") ?? "Join me");
                return Task.CompletedTask;
            });
    }

    void GroupInvite(GridClient c, InstantMessage im)
    {
        var groupId = im.FromAgentID;
        var fee = im.BinaryBucket is { Length: >= 4 } b ? (b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]) : 0;
        Notify.Add("JoinGroup", "offer", im.Message ?? "",
            new[] { new NotifyButton("Join", "Join", 0), new NotifyButton("Decline", "Decline", 1), new NotifyButton("Info", "Info", 2) },
            payload: new JsonObject
            {
                ["transaction_id"] = im.IMSessionID.ToString(), ["group_id"] = groupId.ToString(), ["name"] = im.FromAgentName,
                ["message"] = im.Message, ["fee"] = fee, ["use_offline_cap"] = false,
            },
            substitutions: new JsonObject { ["MESSAGE"] = im.Message },
            respond: (button, _) =>
            {
                if (button == "Join" && fee > 0)
                    ConfirmInviteFee(c, groupId, im.IMSessionID, fee);   // the viewer asks again before paying
                else if (button is "Join" or "Decline")
                    c.Self.GroupInviteRespond(groupId, im.IMSessionID, button == "Join");
                return Task.CompletedTask;
            });
    }

    /// <summary>The viewer's send_join_group_response for an invitation with a fee: JoinGroupCanAfford, then accept
    /// (Join) or decline (Cancel).</summary>
    void ConfirmInviteFee(GridClient c, UUID groupId, UUID sessionId, int fee)
    {
        Notify.Add("JoinGroupCanAfford", "alertmodal", $"Joining this group costs L${fee}.\nDo you wish to proceed?",
            new[] { new NotifyButton("OK_okcancelbuttons", "Join", 0, true), new NotifyButton("Cancel_okcancelbuttons", "Cancel", 1) },
            payload: new JsonObject { ["group_id"] = groupId.ToString(), ["transaction_id"] = sessionId.ToString(), ["fee"] = 0 },
            substitutions: new JsonObject { ["COST"] = fee.ToString() },
            respond: (button, _) =>
            {
                c.Self.GroupInviteRespond(groupId, sessionId, button == "OK_okcancelbuttons");
                return Task.CompletedTask;
            });
    }

    void FriendNotice(FriendInfo f, bool online)
    {
        // at login the friends list's names can still be empty: "is online" with no one before it (2026-10-27). Ask for
        // the name first, briefly; a notice a moment late beats one that doesn't say who.
        if (string.IsNullOrWhiteSpace(f.Name) && Names.DisplayName(f.UUID) is null && Client is { } c)
        {
            _ = Task.Run(async () =>
            {
                try { await Names.ResolveAsync(c, new[] { f.UUID }, TimeSpan.FromSeconds(4)); }
                catch (Exception e) { Log.LogDebug(e, "friend's name"); }
                PostFriendNotice(f, online);
            });
            return;
        }
        PostFriendNotice(f, online);
    }

    void PostFriendNotice(FriendInfo f, bool online)
    {
        var name = Names.DisplayName(f.UUID) is { Length: > 0 } dn ? dn
            : !string.IsNullOrWhiteSpace(f.Name) ? f.Name : "A friend";
        Notify.Add("FriendOnlineOffline", "notifytip", $"<nolink>{name}</nolink> is {(online ? "Online" : "Offline")}", Notifications.None,
            payload: new JsonObject { ["FROM_ID"] = f.UUID.ToString() },
            substitutions: new JsonObject { ["NAME"] = name, ["STATUS"] = online ? "Online" : "Offline" });
    }

    // ---------- dialogs, questions, urls, alerts ----------

    void OnScriptDialog(GridClient c, ScriptDialogEventArgs e)
    {
        if (Mutes.IsMuted(e.ObjectID, e.ObjectName, e.OwnerID)) return;
        var group = string.IsNullOrEmpty(e.FirstName);
        var owner = group ? e.LastName : $"{e.FirstName} {e.LastName}".Trim();
        // antispam: by the object, and by its owner (a linkset of prims each sending one counts together)
        bool mineDialog = e.OwnerID == c.Self.AgentID;
        if (Safety.Spam(Safety.Queue.Dialog, e.ObjectID, e.ObjectName, mineDialog) || Safety.Spam(Safety.Queue.Dialog, e.OwnerID, owner, mineDialog)) return;
        var buttons = new List<NotifyButton>
        {
            new("Client_Side_Mute", "Block", -2), new("Client_Side_Ignore", "Ignore", -1),
        };
        var labels = e.ButtonLabels.Count == 0 ? new List<string> { "OK" } : e.ButtonLabels;
        for (var i = 0; i < labels.Count; i++) buttons.Add(new NotifyButton(labels[i], labels[i], i, Dynamic: true));
        var message = group
            ? $"<nolink>{owner}</nolink>'s '<nolink>{e.ObjectName}</nolink>'\n{e.Message}"
            : $"{owner}'s '<nolink>{e.ObjectName}</nolink>'\n{e.Message}";
        Notify.Add(group ? "ScriptDialogGroup" : "ScriptDialog", "notify", message, buttons,
            payload: new JsonObject
            {
                ["sender"] = e.ObjectID.ToString(), ["object_id"] = e.ObjectID.ToString(),
                ["chat_channel"] = e.Channel, ["object_name"] = e.ObjectName, ["owner_id"] = e.OwnerID.ToString(),
            },
            substitutions: new JsonObject
            {
                ["TITLE"] = e.ObjectName, ["MESSAGE"] = e.Message, [group ? "GROUPNAME" : "NAME"] = owner,
            },
            key: "dialog:" + e.ObjectID,
            respond: (button, resp) =>
            {
                if (button is null or "Client_Side_Ignore") return Task.CompletedTask;
                if (button == "Client_Side_Mute")
                {
                    Mutes.Add(e.ObjectID, e.ObjectName, "object", c);
                    return Task.CompletedTask;
                }
                var index = labels.IndexOf(button);
                var text = button;
                if (button == "!!llTextBox!!")
                    text = resp[button] is JsonValue v && v.TryGetValue<string>(out var typed) ? typed : "";
                c.Self.ReplyToScriptDialog(e.Channel, Math.Max(0, index), text, e.ObjectID);
                return Task.CompletedTask;
            });
    }

    /// <summary>A script asks for permissions (process_script_question). One from an experience (the packet's
    /// Experience block) is the viewer's ScriptQuestionExperience: yes joins the experience (the region records it, and
    /// its scripts ask no more where it runs), and it can be blocked outright.</summary>
    void OnScriptQuestion(GridClient c, PacketReceivedEventArgs pe)
    {
        if (pe.Packet is not global::LibreMetaverse.Packets.ScriptQuestionPacket p) return;
        var sim = pe.Simulator;
        var taskId = p.Data.TaskID;
        var itemId = p.Data.ItemID;
        var objectName = Utils.BytesToString(p.Data.ObjectName);
        var ownerName = Utils.BytesToString(p.Data.ObjectOwner);
        var asked = (ScriptPermission)p.Data.Questions;
        var experience = p.Experience?.ExperienceID ?? UUID.Zero;
        // OpenSim has no experiences: SOJOURN_TEST_EXPERIENCE=<id> makes its questions come from one (tests only)
        if (experience == UUID.Zero && UUID.TryParse(Environment.GetEnvironmentVariable("SOJOURN_TEST_EXPERIENCE") ?? "", out var testExperience))
            experience = testExperience;
        if (Mutes.IsMuted(taskId, objectName)) return;
        if (Safety.Spam(Safety.Queue.Dialog, taskId, objectName)) return;   // antispam (the question names no owner id)
        var questions = string.Join("\n", PermissionLines(asked));
        var payload = new JsonObject
        {
            ["task_id"] = taskId.ToString(), ["item_id"] = itemId.ToString(), ["sender"] = taskId.ToString(),
            ["questions"] = (int)asked, ["object_name"] = objectName, ["owner_name"] = ownerName,
        };
        var subs = new JsonObject { ["OBJECTNAME"] = objectName, ["NAME"] = ownerName, ["QUESTIONS"] = questions };
        Task Respond(string? button, JsonObject response)
        {
            var grant = button == "Yes" ? asked : ScriptPermission.None;
            c.Self.ScriptQuestionReply(sim, itemId, taskId, grant);
            if (button == "Mute") c.Self.UpdateMuteListEntry(MuteType.Object, taskId, objectName);
            if (button == "BlockExperience" && experience != UUID.Zero)
                _ = c.Self.SetExperiencePermissionAsync(experience, "Block").ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return Task.CompletedTask;
        }
        if (experience == UUID.Zero)
        {
            Notify.Add("ScriptQuestion", "notify",
                $"'<nolink>{objectName}</nolink>', an object owned by '{ownerName}', would like to:\n\n{questions}\nIs this OK?",
                new[] { new NotifyButton("Yes", "Yes", 0), new NotifyButton("No", "No", 1), new NotifyButton("Mute", "Block", 2) },
                payload: payload, substitutions: subs, respond: Respond);
            return;
        }
        // the experience's name first, as the viewer does (LLExperienceCache), briefly
        _ = Task.Run(async () =>
        {
            string? name = null;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var info = await c.Self.GetExperienceInfoAsync(new[] { experience }, cts.Token);
                if (info?.Experiences?.FirstOrDefault(x => x.PublicID == experience || x.ExperienceID == experience) is { Name: { Length: > 0 } n }) name = n;
            }
            catch { }
            payload["experience"] = experience.ToString();
            payload["experience_name"] = name ?? "";
            subs["EXPERIENCE"] = name ?? "an experience";
            Guard("question", () => Notify.Add("ScriptQuestionExperience", "notify",
                $"'<nolink>{objectName}</nolink>', an object owned by '{ownerName}', requests your participation in the experience:\n\n{name ?? "(its name didn't come)"}\n\n" +
                "Once permission is granted you will not see this message again for this experience unless it is revoked from the experience profile.\n\n" +
                $"Scripts associated with this experience will be able to do the following on regions where the experience is active:\n\n{questions}\nIs this OK?",
                new[]
                {
                    new NotifyButton("Yes", "Yes", 0), new NotifyButton("No", "No", 1),
                    new NotifyButton("BlockExperience", "Block experience", 3), new NotifyButton("Mute", "Block object", 2),
                },
                payload: payload, substitutions: subs, respond: Respond));
        });
    }

    static IEnumerable<string> PermissionLines(ScriptPermission p)
    {
        var lines = new (ScriptPermission Flag, string Text)[]
        {
            (ScriptPermission.Debit, "Take Linden dollars (L$) from you"),
            (ScriptPermission.TakeControls, "Act on your control inputs"),
            (ScriptPermission.RemapControls, "Remap your control inputs"),
            (ScriptPermission.TriggerAnimation, "Animate your avatar"),
            (ScriptPermission.Attach, "Attach to your avatar"),
            (ScriptPermission.ReleaseOwnership, "Release ownership and become public"),
            (ScriptPermission.ChangeLinks, "Link and delink from other objects"),
            (ScriptPermission.ChangeJoints, "Add and remove joints with other objects"),
            (ScriptPermission.ChangePermissions, "Change its permissions"),
            (ScriptPermission.TrackCamera, "Track your camera"),
            (ScriptPermission.ControlCamera, "Control your camera"),
            (ScriptPermission.Teleport, "Teleport you"),
        };
        foreach (var (flag, text) in lines) if ((p & flag) != 0) yield return text;
    }

    void OnLoadUrl(LoadUrlEventArgs e)
    {
        // blocked objects and their owners offer no pages (process_load_url), and antispam counts them as menus
        if (Mutes.IsMuted(e.ObjectID, e.ObjectName, e.OwnerIsGroup ? UUID.Zero : e.OwnerID)) return;
        bool mine = e.OwnerID == AgentId;
        if (Safety.Spam(Safety.Queue.Dialog, e.ObjectID, e.ObjectName, mine) || Safety.Spam(Safety.Queue.Dialog, e.OwnerID, e.ObjectName, mine)) return;
        Notify.Add("LoadWebPage", "notify",
            $"Load web page {e.URL} ?\n\n{e.Message}\n\nFrom object: <nolink>{e.ObjectName}</nolink>, owner: {AgentSlurl(e.OwnerID)}",
            new[] { new NotifyButton("Gotopage", "Go to page", 0), new NotifyButton("Cancel", "Cancel", 1) },
            payload: new JsonObject
            {
                ["object_id"] = e.ObjectID.ToString(), ["owner_id"] = e.OwnerID.ToString(), ["owner_is_group"] = e.OwnerIsGroup,
                ["object_name"] = e.ObjectName, ["message"] = e.Message, ["url"] = e.URL,
            },
            substitutions: new JsonObject { ["URL"] = e.URL, ["MESSAGE"] = e.Message, ["OBJECTNAME"] = e.ObjectName },
            // "Go to page" opens it in the client's own page view when it has one (OpenWebPage); a headless resident has
            // no browser, and the URL is in the payload
            respond: (button, _) =>
            {
                if (button == "Gotopage") OpenWebPage?.Invoke(new WebPageOffer(e.URL, e.ObjectID, e.ObjectName, e.OwnerID, e.OwnerIsGroup, e.Message));
                return Task.CompletedTask;
            });
    }

    /// <summary>A web page an object offered (llLoadURL) that the person chose to open: the client shows it.</summary>
    public Action<WebPageOffer>? OpenWebPage { get; set; }

    /// <summary>Set by an op that waits for the region's answer as an alert (setHome).</summary>
    internal TaskCompletionSource<string>? LastAlert;

    void OnAlert(GridClient c, AlertMessageEventArgs e)
    {
        var msg = e.Message ?? "";
        State.LastAlert = (DateTime.UtcNow, msg);
        if (msg.StartsWith("RESTART_X_MINUTES") || msg.StartsWith("RESTART_X_SECONDS"))
        {
            var minutes = msg.StartsWith("RESTART_X_MINUTES");
            var n = e.ExtraParams is { } x && x.ContainsKey(minutes ? "MINUTES" : "SECONDS") ? x[minutes ? "MINUTES" : "SECONDS"].AsString() : "?";
            var region = c.Network.CurrentSim?.Name ?? "";
            Notify.Add(minutes ? "RegionRestartMinutes" : "RegionRestartSeconds", "notify",
                $"The region \"{region}\" will restart in {n} {(minutes ? "minutes" : "seconds")}. If you stay in this region you will be logged out.",
                Notifications.Ok, substitutions: new JsonObject { ["NAME"] = region, [minutes ? "MINUTES" : "SECONDS"] = n });
            return;
        }
        if (msg == "AUTOPILOT_CANCELED") return;
        if (msg.StartsWith('/')) msg = msg[1..];  // the viewer shows these without the slash
        LastAlert?.TrySetResult(msg);
        var name = string.IsNullOrEmpty(e.NotificationId) ? "SystemMessageTip" : e.NotificationId!;
        Notify.Add(name, "notifytip", msg, Notifications.None, substitutions: new JsonObject { ["MESSAGE"] = msg });
    }

    // ---------- teleports, region changes ----------

    void OnTeleport(TeleportEventArgs e)
    {
        switch (e.Status)
        {
            case TeleportStatus.Finished:
                // what scripts there held stays behind: a stale take would keep the stick from walking (VrPilot)
                ResetControlsTaken();
                Events.Log("LLTeleport", new JsonObject { ["success"] = true });
                break;
            case TeleportStatus.Failed:
            case TeleportStatus.Cancelled:
                // a refusal from the grid was noticed already, from the message itself (TeleportWatch); this event
                // still covers LibreMetaverse's own failures (a region name it couldn't look up, a lost connection)
                if (DateTime.UtcNow - _teleportFailNoticed > TimeSpan.FromSeconds(5)) TeleportFailed(TeleportWatch.Plain(e.Message));
                break;
        }
    }

    void OnRegionChanged(GridClient c, SimChangedEventArgs e)
    {
        Ears.Arrived();
        Log.LogInformation("now in {Region}", c.Network.CurrentSim?.Name);
        if (c.Network.CurrentSim is { } sim) Camera.ResetFor(sim);
    }

    // ---------- money ----------

    void OnMoney(GridClient c, MoneyBalanceReplyEventArgs e)
    {
        _balanceKnown = true;
        var ti = e.TransactionInfo;
        var amount = ti?.Amount ?? 0;
        if (amount == 0 && (e.TransactionID == UUID.Zero || string.IsNullOrEmpty(e.Description))) return;
        var ev = new JsonObject
        {
            ["event"] = "money",
            ["balance"] = e.Balance,
            ["transaction_id"] = e.TransactionID.ToString(),
            ["desc"] = e.Description,
        };
        if (ti is not null)
        {
            var me = c.Self.AgentID;
            ev["type"] = ti.TransactionType;
            ev["amount"] = ti.Amount;
            ev["source_id"] = ti.SourceID.ToString();
            ev["dest_id"] = ti.DestID.ToString();
            ev["source_group"] = ti.IsSourceGroup;
            ev["dest_group"] = ti.IsDestGroup;
            ev["item"] = ti.ItemDescription;
            ev["dir"] = ti.DestID == me ? "in" : ti.SourceID == me ? "out" : "";
        }
        Events.Log("TesselEvents", ev);
    }

    // ---------- objects: properties, created objects ----------

    void OnProperties(Simulator sim, Primitive.ObjectProperties props, bool full)
    {
        var world = World.Region(GridKey, sim.Handle);
        if (world is not null)
        {
            var meta = world.PropsMeta.GetOrAdd(props.ObjectID, _ => new PropsMeta());
            meta.FetchedAt = DateTime.UtcNow;
            if (full) meta.Full = true;
        }
        if (State.PropsRequested.TryRemove(props.ObjectID, out _))
        {
            PropsMeta? m = null;
            var known = world?.PropsMeta.TryGetValue(props.ObjectID, out m) ?? false;
            var ev = ObjectViews.PropsJson(props, known && m!.Full, m?.Created ?? 0);
            ev["event"] = "object_properties";
            ev["id"] = props.ObjectID.ToString();
            Events.Log("TesselEvents", ev);
        }
    }

    void OnObjectUpdate(GridClient c, PrimEventArgs e)
    {
        var p = e.Prim;
        // Full updates set this agent's flags in the block event (synchronously, before the shared prim can be
        // rewritten by another session); this event can run later on another thread, so only fill in a gap here.
        State.Flags.TryAdd(p.LocalID, p.Flags);
        var flags = State.PrimFlagsOf(p);
        // (ObjectUpdate's OwnerID is only filled for objects with a sound; ownership is in this agent's flags)
        if ((flags & PrimFlags.CreateSelected) != 0 && (flags & PrimFlags.ObjectYouOwner) != 0 && p.ParentID == 0 && !e.IsAttachment)
            Build.OnCreateSelected(c, p);
    }
}

/// <summary>A web page offered by an object (llLoadURL): what it is and who offered it.</summary>
public sealed record WebPageOffer(string Url, UUID ObjectId, string ObjectName, UUID OwnerId, bool OwnerIsGroup, string Message);
