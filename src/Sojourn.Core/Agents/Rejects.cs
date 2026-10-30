using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// What the person has said they don't want offered, as the viewers' switches do it (read from their source,
/// secondlife/viewer develop and FirestormViewer/phoenix-firestorm master, 2026-10-28):
/// - "Only friends and groups can call or IM me" (the official viewer's VoiceCallsFriendsOnly, per account, off by
///   default; llimprocessing.cpp, llimview.cpp addMessage): one-to-one IMs from anyone not a friend (Lindens excepted)
///   aren't shown, the first of each conversation leaving a line saying why; their teleport offers and requests are
///   dropped silently; a conference a non-friend starts is left. Group chat isn't touched. (Voice calls: batch B10.)
/// - Firestorm's reject modes (FSRejectTeleportOffersMode with FSDontRejectTeleportOffersFromFriends,
///   FSRejectFriendshipRequestsMode, FSRejectAllGroupInvitesMode; all off by default): teleport offers and requests,
///   and friendship offers, are dropped and answered with a sentence (the person's own words once they set them;
///   confirmed, as Do Not Disturb's are); group invitations are dropped without a word.
/// - Accepting inventory by itself (AutoAcceptNewInventory: the official viewer's for notecards, textures and
///   landmarks; Firestorm's for anything; off by default): the offer is kept without asking, and a tip says what came.
/// Nothing here asks the person or acts beyond answering the offer in front of it. Claude Opus 5.5, 2026-10-28.
/// </summary>
public sealed class Rejects
{
    readonly AgentSession _s;
    public Rejects(AgentSession s) => _s = s;
    PeopleStore Store => _s.PeopleStore;

    public const string DefaultTeleportsResponse = "The Resident you messaged has turned on 'reject all teleport offers and requests', which means "
        + "they have asked not to be disturbed with teleport offers and requests. You may still send an IM message manually.";
    public const string DefaultFriendshipsResponse = "The Resident you messaged has turned on 'reject all friendship requests', which means "
        + "they have asked not to be disturbed with friendship requests. You may still send an IM message manually.";

    public static readonly string[] TeleportModes = { "off", "all", "not_friends" };
    public static readonly string[] InventoryModes = { "off", "viewer", "all" };

    public event Action? Changed;

    // ---------- the switches (per account, as both viewers keep them) ----------

    public bool FriendsOnly => Store.Flag("friends_only_ims", false);
    /// <summary>off, all, or not_friends (Firestorm's mode with "don't reject from friends").</summary>
    public string RejectTeleports => Store.Text("reject_teleports") is { } m && TeleportModes.Contains(m) ? m : "off";
    public bool RejectFriendships => Store.Flag("reject_friendships", false);
    public bool RejectGroupInvites => Store.Flag("reject_group_invites", false);
    /// <summary>off, viewer (notecards, textures, landmarks: the official viewer's), all (Firestorm's).</summary>
    public string AutoAcceptInventory => Store.Text("auto_accept_inventory") is { } m && InventoryModes.Contains(m) ? m : "off";

    public string TeleportsResponse => Store.Text("teleports_response") is { Length: > 0 } t ? t : DefaultTeleportsResponse;
    public string FriendshipsResponse => Store.Text("friendships_response") is { Length: > 0 } t ? t : DefaultFriendshipsResponse;

    public void SetFriendsOnly(bool on) { Store.Set("friends_only_ims", on); Changed?.Invoke(); }
    public void SetRejectTeleports(string mode)
    {
        if (!TeleportModes.Contains(mode)) throw new LeapError($"reject_teleports is one of {string.Join(", ", TeleportModes)}");
        Store.Set("reject_teleports", mode);
        Changed?.Invoke();
    }
    public void SetRejectFriendships(bool on) { Store.Set("reject_friendships", on); Changed?.Invoke(); }
    public void SetRejectGroupInvites(bool on) { Store.Set("reject_group_invites", on); Changed?.Invoke(); }
    public void SetAutoAcceptInventory(string mode)
    {
        if (!InventoryModes.Contains(mode)) throw new LeapError($"auto_accept_inventory is one of {string.Join(", ", InventoryModes)}");
        Store.Set("auto_accept_inventory", mode);
        Changed?.Invoke();
    }

    /// <summary>The words a reject mode answers with (kind teleports or friendships); empty: back to the standard sentence.
    /// Only through the confirmation gate (SojournPeople setResponse).</summary>
    public void SetResponse(string kind, string text) => Store.Set(kind + "_response", text.Length > 0 ? text : null);

    readonly ConcurrentDictionary<string, int> _turnedAway = new();

    public JsonObject Status()
    {
        var away = new JsonObject();
        foreach (var (k, n) in _turnedAway.OrderBy(kv => kv.Key)) away[k] = n;
        return new JsonObject
        {
            ["friends_only_ims"] = FriendsOnly, ["reject_teleports"] = RejectTeleports, ["reject_friendships"] = RejectFriendships,
            ["reject_group_invites"] = RejectGroupInvites, ["auto_accept_inventory"] = AutoAcceptInventory,
            ["teleports_response"] = TeleportsResponse, ["friendships_response"] = FriendshipsResponse,
            ["turned_away_this_session"] = away,
        };
    }

    // ---------- what an IM meets (AgentSession.OnIM asks, after blocks, antispam and Do Not Disturb) ----------

    bool IsFriend(GridClient c, UUID id) => c.Friends.FriendList.ContainsKey(id);

    /// <summary>LLMuteList::isLinden: the second word of the name (dots as spaces) is "linden".</summary>
    static bool IsLinden(string? name)
    {
        var parts = (name ?? "").Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[1].Equals("linden", StringComparison.OrdinalIgnoreCase);
    }

    enum Verdict { Pass, Drop, Reply, NotFriend, Leave, AutoAccept }

    /// <summary>What would happen to an IM, without doing it (other listeners ask, such as the new-IM sound).</summary>
    Verdict Judge(GridClient c, InstantMessage im)
    {
        var me = c.Self.AgentID;
        if (im.FromAgentID == me || im.FromAgentID == UUID.Zero) return Verdict.Pass;
        bool friend = IsFriend(c, im.FromAgentID);
        bool friendsOnly = FriendsOnly && !friend && !IsLinden(im.FromAgentName);
        switch (im.Dialog)
        {
            case InstantMessageDialog.MessageFromAgent when !im.GroupIM && !_s.IsMyGroup(im.IMSessionID) && !string.IsNullOrEmpty(im.Message):
                if (!friendsOnly) return Verdict.Pass;
                if (OneToOne(me, im)) return Verdict.NotFriend;
                // a conference's line (SL's event queue sends them as dialog 0): ongoing ones carry on
                return _s.State.Ims.ContainsKey(im.IMSessionID) ? Verdict.Pass : Verdict.Leave;
            case InstantMessageDialog.SessionSend or InstantMessageDialog.SessionAdd or InstantMessageDialog.SessionCardlessStart
                when !im.GroupIM && !_s.IsMyGroup(im.IMSessionID):
                // a conference: one a non-friend starts is left (the viewer's addMessage); ongoing ones carry on
                return friendsOnly && !_s.State.Ims.ContainsKey(im.IMSessionID) ? Verdict.Leave : Verdict.Pass;
            case InstantMessageDialog.RequestTeleport:
            case InstantMessageDialog.RequestLure:
                if (friendsOnly) return Verdict.Drop;
                return RejectTeleports switch { "all" => Verdict.Reply, "not_friends" when !friend => Verdict.Reply, _ => Verdict.Pass };
            case InstantMessageDialog.FriendshipOffered:
                return RejectFriendships ? Verdict.Reply : Verdict.Pass;
            case InstantMessageDialog.GroupInvitation:
                return RejectGroupInvites ? Verdict.Drop : Verdict.Pass;
            case InstantMessageDialog.InventoryOffered:
            case InstantMessageDialog.TaskInventoryOffered:
            {
                var type = im.BinaryBucket is { Length: > 0 } b ? (AssetType)b[0] : AssetType.Unknown;
                return AutoAcceptInventory switch
                {
                    "all" => Verdict.AutoAccept,
                    "viewer" when type is AssetType.Notecard or AssetType.Landmark or AssetType.Texture => Verdict.AutoAccept,
                    _ => Verdict.Pass,
                };
            }
        }
        return Verdict.Pass;
    }

    /// <summary>One to one: no session, the viewers' id for the two of us (agent ^ other), or a session already kept as one.</summary>
    bool OneToOne(UUID me, InstantMessage im) =>
        im.IMSessionID == UUID.Zero || im.IMSessionID == (me ^ im.FromAgentID)
        || (_s.State.Ims.TryGetValue(im.IMSessionID, out var s) && s.Type == "p2p");

    /// <summary>Whether the person won't see this IM because of a switch (no side effects).</summary>
    public bool Hides(GridClient c, InstantMessage im) => Judge(c, im) is not Verdict.Pass;

    readonly ConcurrentDictionary<UUID, byte> _toldNotFriend = new(), _leftConference = new();

    /// <summary>True when OnIM should go no further: the IM was dropped, answered or accepted here.</summary>
    internal bool Takes(GridClient c, InstantMessage im)
    {
        var verdict = Judge(c, im);
        if (verdict == Verdict.Pass) return false;
        var me = c.Self.AgentID;
        switch (verdict)
        {
            case Verdict.NotFriend:
            {
                Count("ims");
                // the viewer leaves one line in the conversation saying why; here one quiet notice a conversation
                var sid = im.IMSessionID != UUID.Zero ? im.IMSessionID : me ^ im.FromAgentID;
                if (_toldNotFriend.TryAdd(sid, 0))
                {
                    var name = _s.Names.DisplayName(im.FromAgentID) ?? im.FromAgentName;
                    _s.Notify.Add("IMNotShownNotFriend", "notifytip",
                        $"{name} sent you an IM. Only friends and groups can IM you now, so it isn't shown.", Notifications.None,
                        payload: new JsonObject { ["from_id"] = im.FromAgentID.ToString(), ["session_id"] = sid.ToString() },
                        substitutions: new JsonObject { ["NAME"] = name });
                }
                return true;
            }
            case Verdict.Leave:
                Count("conferences");
                if (_leftConference.TryAdd(im.IMSessionID, 0))
                {
                    try { c.Self.RequestLeaveGroupChat(im.IMSessionID); } catch (Exception e) { _s.Log.LogDebug(e, "leaving a conference"); }
                    _s.Log.LogInformation("left a conference a non-friend started (only friends and groups can IM)");
                }
                return true;
            case Verdict.Drop:
                Count(im.Dialog == InstantMessageDialog.GroupInvitation ? "group_invites" : "teleports");
                _s.Log.LogInformation("{What} turned away (the person's switch)", im.Dialog);
                return true;
            case Verdict.Reply:
            {
                bool tp = im.Dialog != InstantMessageDialog.FriendshipOffered;
                Count(tp ? "teleports" : "friendships");
                var words = tp ? TeleportsResponse : FriendshipsResponse;
                // the one-to-one conversation's id: the offer's own id is its lure or transaction (Firestorm's
                // send_rejecting_tp_offers_message / send_rejecting_friendship_requests_message send no session, which
                // a viewer files under the conversation of the two)
                var sid = me ^ im.FromAgentID;
                c.Self.InstantMessage(c.Self.Name, im.FromAgentID, words, sid, InstantMessageDialog.BusyAutoResponse, InstantMessageOnline.Online,
                    c.Self.SimPosition, c.Network.CurrentSim?.ID ?? UUID.Zero, Utils.EmptyBytes);
                // a friendship offer is declined too (beyond Firestorm, which lets it lapse): OpenSim keeps an unanswered
                // one as a half friendship, sent again at every login and blocking an offer the other way
                if (!tp)
                    try { c.Friends.DeclineFriendship(im.FromAgentID, im.IMSessionID); }
                    catch (Exception e) { _s.Log.LogDebug(e, "declining a turned-away friendship offer"); }
                _s.Events.Log("TesselEvents", new JsonObject
                {
                    ["event"] = "autoresponse", ["kind"] = tp ? "reject_teleports" : "reject_friendships", ["to_id"] = im.FromAgentID.ToString(),
                    ["to"] = im.FromAgentName, ["session_id"] = sid.ToString(),
                });
                return true;
            }
            case Verdict.AutoAccept:
            {
                var fromTask = im.Dialog == InstantMessageDialog.TaskInventoryOffered;
                var type = im.BinaryBucket is { Length: > 0 } b ? (AssetType)b[0] : AssetType.Unknown;
                var objectId = !fromTask && im.BinaryBucket is { Length: >= 17 } bb ? new UUID(bb, 1) : UUID.Zero;
                AgentInventory.AnswerOffer(c, im, type, objectId, keep: true);
                var item = ItemName(im.Message ?? "", fromTask);
                var from = fromTask ? im.FromAgentName : _s.Names.DisplayName(im.FromAgentID) ?? im.FromAgentName;
                var kind = AgentInventory.AssetTypeLabel(type);
                _s.Notify.Add(fromTask ? "InvOfferAutoAcceptObject" : "InvOfferAutoAcceptUser", "notifytip",
                    $"{from} gave you {kind} {item} (accepted by itself).", Notifications.None,
                    payload: new JsonObject
                    {
                        ["from_id"] = im.FromAgentID.ToString(), ["from_name"] = from, ["item_name"] = item, ["type"] = (int)type,
                        ["from_task"] = fromTask,
                    },
                    substitutions: new JsonObject { ["NAME"] = from, ["OBJECT_TYPE"] = kind, ["DESC"] = item });
                return true;
            }
        }
        return false;
    }

    /// <summary>An object's offer says "'name'. (object is located at ...)": the quoted part (as InventoryOffer reads it).</summary>
    static string ItemName(string msg, bool fromTask)
    {
        if (!fromTask || !msg.StartsWith('\'')) return msg;
        int close = msg.LastIndexOf("'.", StringComparison.Ordinal);
        if (close < 1) close = msg.LastIndexOf("' ", StringComparison.Ordinal);
        if (close < 1 && msg.EndsWith('\'')) close = msg.Length - 1;
        return close > 1 ? msg[1..close] : msg;
    }

    void Count(string what) => _turnedAway.AddOrUpdate(what, 1, (_, n) => n + 1);

    internal void OnLoggedIn()
    {
        _toldNotFriend.Clear();
        _leftConference.Clear();
    }
}
