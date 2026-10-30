using System.Net;
using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Session.Voice;

/// <summary>
/// Voice moderation, as the official viewer does it (read 2026-10-29):
/// - Nearby voice (LLNearbyVoiceModeration, the 2026 WebRTC moderation): the region's SpatialVoiceModerationRequest
///   capability, {operand: mute|unmute, agent_id} for one person (through the region they're in) or {operand:
///   mute_all|unmute_all} for everyone on the region's channel (the server keeps it for people who arrive later).
///   Who may (isNearbyChatModerator): on a parcel with its own voice channel, its owner (or, for group land, members
///   with the group's moderate chat power); on the estate's channel, the estate's owner and managers.
/// - Group and conference calls (LLIMSpeakerMgr::moderateVoiceParticipant, moderateVoiceSession): ChatSessionRequest's
///   "mute update" with mute_info.voice for one, "session update" with moderated_mode.voice for the whole call (and,
///   as forceVoiceModeratedMode, each member who doesn't match yet). Who may: the call's moderators (the chat's member
///   list, or a group role with the moderate chat power). One-to-one calls have none.
/// Everything here acts on other people, so the ops wait for the person's press (VoiceWiring's confirmation case);
/// what they couldn't do is refused before they're asked (WhyNot). Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class VoiceModeration
{
    readonly AgentSession _s;
    readonly VoiceClient _v;

    public VoiceModeration(AgentSession s, VoiceClient v)
    {
        _s = s;
        _v = v;
    }

    /// <summary>Why the person can't moderate the voice they're in, or null if they can.</summary>
    public string? WhyNot()
    {
        if (_s.Client is not { } c || c.Network.CurrentSim is not { } sim) return "not in world";
        if (_v.CallInfo is { } call)
        {
            if (call.P2P) return "a one-to-one call has no moderators";
            return CallModerator(c, call.Session) ? null : "only the call's moderators can do that";
        }
        var (spatial, parcel) = _v.Where;
        if (!spatial) return "not in nearby voice here";
        if (sim.Caps?.CapabilityURI("SpatialVoiceModerationRequest") is null) return "this region doesn't offer voice moderation";
        if (parcel is { } local)
        {
            if (!sim.Parcels.TryGetValue(local, out var p)) return "the parcel isn't known yet";
            if (p.OwnerID == c.Self.AgentID) return null;
            bool groupMod = p.IsGroupOwned && _s.State.Groups.TryGetValue(p.OwnerID, out var g) && (g.Powers & GroupPowers.ModerateChat) != 0;
            return groupMod ? null : "only the parcel's owner (or its group's moderators) can moderate its voice";
        }
        return _s.Estate.CanManage ? null : "only the estate's owner and managers can moderate nearby voice";
    }

    bool CallModerator(GridClient c, UUID session)
    {
        if (_s.State.Groups.TryGetValue(session, out var g) && (g.Powers & GroupPowers.ModerateChat) != 0) return true;
        if (c.Self.GroupChatSessions.TryGetValue(session, out var members))
            lock (members)
                return members.Any(m => m.AvatarKey == c.Self.AgentID && m.IsModerator);
        return false;
    }

    /// <summary>A person in the voice the person is in: by id, or a name they go by among its speakers.</summary>
    public UUID Speaker(string? who)
    {
        if (string.IsNullOrWhiteSpace(who)) throw new LeapError("need id (a resident's UUID or name)");
        if (UUID.TryParse(who, out var id) && id != UUID.Zero) return id;
        var want = who.Trim();
        bool Is(string? s) => s is { Length: > 0 } && string.Equals(s.Trim(), want, StringComparison.OrdinalIgnoreCase);
        var self = _s.Client?.Self.AgentID ?? UUID.Zero;
        var hits = _v.Peers.Where(p => p.Id != self && (Is(_s.Names.DisplayName(p.Id)) || Is(_s.Names.Legacy(p.Id)))).Select(p => p.Id).Distinct().ToList();
        return hits.Count == 1 ? hits[0] : throw new LeapError(hits.Count == 0 ? $"nobody called \"{want}\" is in voice here" : $"more than one \"{want}\" here; use their id");
    }

    public string NameOf(UUID id) => _s.Names.DisplayName(id) ?? _s.Names.Legacy(id) ?? $"someone ({id.ToString()[..8]})";

    /// <summary>Mutes or unmutes one person's voice for everyone in the voice the person is in.</summary>
    public async Task<JsonObject> MuteAsync(UUID who, bool mute, CancellationToken ct)
    {
        if (WhyNot() is { } no) throw new LeapError(no);
        var c = _s.Client ?? throw new NotConnected("viewer not connected");
        if (who == c.Self.AgentID) throw new LeapError("that's the person themselves");
        Uri cap;
        OSDMap body;
        if (_v.CallInfo is { } call)
        {
            cap = c.Network.CurrentSim?.Caps?.CapabilityURI("ChatSessionRequest") ?? throw new LeapError("the region has no ChatSessionRequest capability");
            body = new OSDMap
            {
                ["method"] = "mute update", ["session-id"] = OSD.FromUUID(call.Session),
                ["params"] = new OSDMap { ["agent_id"] = OSD.FromUUID(who), ["mute_info"] = new OSDMap { ["voice"] = OSD.FromBoolean(mute) } },
            };
        }
        else
        {
            // through the region the avatar is in (getVOAvatarFromId -> its region's capability)
            var sim = SimOf(c, who) ?? throw new LeapError($"{NameOf(who)} isn't in view (the viewer moderates people it can see)");
            cap = sim.Caps?.CapabilityURI("SpatialVoiceModerationRequest") ?? throw new LeapError($"{sim.Name} doesn't offer voice moderation");
            body = new OSDMap { ["operand"] = mute ? "mute" : "unmute", ["agent_id"] = OSD.FromUUID(who) };
        }
        await PostAsync(c, cap, body, ct);
        // the server's word comes back on the data channel ("m" for that person)
        bool seen = false;
        for (int i = 0; i < 12 && !seen; i++)
        {
            seen = _v.Peers.FirstOrDefault(p => p.Id == who) is { } p && p.ModeratorMuted == mute;
            if (!seen) await Task.Delay(250, ct);
        }
        return new JsonObject
        {
            ["who"] = NameOf(who), ["id"] = who.ToString(), ["muted"] = mute, ["where"] = _v.CallInfo is { } ci ? $"the call with {ci.With}" : "nearby voice",
            ["confirmed_by_voice_server"] = seen,
        };
    }

    /// <summary>Mutes or unmutes everyone else's voice in the voice the person is in.</summary>
    public async Task<JsonObject> MuteAllAsync(bool mute, CancellationToken ct)
    {
        if (WhyNot() is { } no) throw new LeapError(no);
        var c = _s.Client ?? throw new NotConnected("viewer not connected");
        if (_v.CallInfo is { } call)
        {
            var cap = c.Network.CurrentSim?.Caps?.CapabilityURI("ChatSessionRequest") ?? throw new LeapError("the region has no ChatSessionRequest capability");
            await PostAsync(c, cap, new OSDMap
            {
                ["method"] = "session update", ["session-id"] = OSD.FromUUID(call.Session),
                ["params"] = new OSDMap { ["update_info"] = new OSDMap { ["moderated_mode"] = new OSDMap { ["voice"] = OSD.FromBoolean(mute) } } },
            }, ct);
            // members who don't match yet (forceVoiceModeratedMode)
            var others = new List<UUID>();
            if (c.Self.GroupChatSessions.TryGetValue(call.Session, out var members))
                lock (members)
                    others = members.Where(m => m.AvatarKey != c.Self.AgentID && m.MuteVoice != mute).Select(m => m.AvatarKey).ToList();
            foreach (var id in others)
                await PostAsync(c, cap, new OSDMap
                {
                    ["method"] = "mute update", ["session-id"] = OSD.FromUUID(call.Session),
                    ["params"] = new OSDMap { ["agent_id"] = OSD.FromUUID(id), ["mute_info"] = new OSDMap { ["voice"] = OSD.FromBoolean(mute) } },
                }, ct, quiet: true);
            return new JsonObject { ["where"] = $"the call with {call.With}", ["everyone_muted"] = mute };
        }
        var here = c.Network.CurrentSim!.Caps?.CapabilityURI("SpatialVoiceModerationRequest") ?? throw new LeapError("this region doesn't offer voice moderation");
        await PostAsync(c, here, new OSDMap { ["operand"] = mute ? "mute_all" : "unmute_all" }, ct);
        return new JsonObject { ["where"] = $"nearby voice in {c.Network.CurrentSim.Name}", ["everyone_muted"] = mute, ["late_arrivals_too"] = mute };
    }

    static Simulator? SimOf(GridClient c, UUID id)
    {
        Simulator[] sims;
        lock (c.Network.Simulators) sims = c.Network.Simulators.ToArray();
        return sims.FirstOrDefault(s => s.ObjectsAvatars.Values.Any(a => a.ID == id));
    }

    static async Task PostAsync(GridClient c, Uri cap, OSDMap body, CancellationToken ct, bool quiet = false)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var (status, _) = await VoiceProtocol.PostAsync(c.HttpCapsClient, cap, body, cts.Token);
        if (quiet || (int)status is >= 200 and < 300) return;
        throw new LeapError(status == HttpStatusCode.Forbidden ? "the region says the person isn't a moderator here" : $"the region refused it ({(int)status})");
    }
}
