using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Session.Voice;

/// <summary>
/// Which voice calls are turned away before they ring, as the viewers decide in LLIMMgr::inviteToSession (read
/// 2026-10-29, the official viewer and Firestorm's FIRE-6522):
/// - from someone whose voice the person muted (a voice mute or a block), one-to-one and ad-hoc calls (not a group's);
/// - group calls, when the person turns them away (VoiceCallsRejectGroup, both viewers);
/// - from anyone but friends while "only friends and groups can IM or call me" is on (B2's `friends_only_ims`, the
///   viewer's VoiceCallsFriendsOnly). Group calls are let through here: the setting's words say groups may call, and B2
///   keeps group chat too. The viewer's code turns away a group call a non-friend starts; that's the one difference;
/// - ad-hoc (conference) and one-to-one calls, each by its own switch (Firestorm's VoiceCallsRejectAdHoc, RejectP2P);
/// - every call while Do Not Disturb is on. Only then does the caller hear back (a one-to-one call gets the person's
///   Do Not Disturb sentence, as send_do_not_disturb_message) and the person a quiet line on the feed (the viewer's
///   "you_auto_rejected_call"); the rest are declined silently, as both viewers do.
/// The switches are kept per account with B2's (people.json); all off by default, as in both viewers.
/// Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class CallRejects
{
    readonly AgentSession _s;
    readonly ConcurrentDictionary<string, int> _counts = new();
    public CallRejects(AgentSession s) => _s = s;

    PeopleStore Store => _s.PeopleStore;
    public bool P2P => Store.Flag("reject_p2p_calls", false);
    public bool Group => Store.Flag("reject_group_calls", false);
    public bool Conference => Store.Flag("reject_conference_calls", false);
    public void SetP2P(bool on) => Store.Set("reject_p2p_calls", on);
    public void SetGroup(bool on) => Store.Set("reject_group_calls", on);
    public void SetConference(bool on) => Store.Set("reject_conference_calls", on);

    static bool IsLinden(string? name)
    {
        var parts = (name ?? "").Replace('.', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[1].Equals("linden", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Why this call is turned away (a key: voice_muted, group_calls, friends_only, conference_calls,
    /// p2p_calls, do_not_disturb), or null to let it ring. The viewer's order.</summary>
    public string? Why(IncomingCall call)
    {
        if (_s.Client is not { } c) return null;
        if (!IsLinden(call.Name) && call.Kind != "group" && _s.Mutes.VoiceMuted(call.From)) return "voice_muted";
        if (Group && call.Kind == "group") return "group_calls";
        if (_s.Rejects.FriendsOnly && call.Kind != "group" && !c.Friends.FriendList.ContainsKey(call.From)) return "friends_only";
        if (Conference && call.Kind == "conference") return "conference_calls";
        if (P2P && call.P2P) return "p2p_calls";
        if (_s.Presence.DoNotDisturb) return "do_not_disturb";
        return null;
    }

    /// <summary>After one is turned away: counted; in Do Not Disturb, the caller told and the person shown.</summary>
    public void TurnedAway(IncomingCall call, string why)
    {
        _counts.AddOrUpdate(why, 1, (_, n) => n + 1);
        if (why != "do_not_disturb" || _s.Client is not { } c) return;
        var name = _s.Names.DisplayName(call.From) ?? call.Name;
        if (call.P2P)
        {
            c.Self.InstantMessage(c.Self.Name, call.From, _s.Presence.DndResponse, call.Session, InstantMessageDialog.BusyAutoResponse,
                InstantMessageOnline.Online, c.Self.SimPosition, c.Network.CurrentSim?.ID ?? UUID.Zero, Utils.EmptyBytes);
            _s.Events.Log("TesselEvents", new JsonObject
            {
                ["event"] = "autoresponse", ["kind"] = "dnd", ["to_id"] = call.From.ToString(), ["to"] = call.Name,
                ["session_id"] = call.Session.ToString(),
            });
        }
        _s.Notify.Add("VoiceCallAutoRejected", "notifytip",
            $"You turned down {name}'s call: Do Not Disturb is on.", Notifications.None,
            payload: new JsonObject { ["from_id"] = call.From.ToString(), ["session_id"] = call.Session.ToString(), ["kind"] = call.Kind },
            substitutions: new JsonObject { ["NAME"] = name });
    }

    public JsonObject Status()
    {
        var turned = new JsonObject();
        foreach (var (k, n) in _counts.OrderBy(kv => kv.Key)) turned[k] = n;
        return new JsonObject
        {
            ["reject_p2p_calls"] = P2P, ["reject_group_calls"] = Group, ["reject_conference_calls"] = Conference,
            ["friends_only"] = _s.Rejects.FriendsOnly, ["do_not_disturb"] = _s.Presence.DoNotDisturb, ["turned_away"] = turned,
        };
    }
}
