using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Session.Voice;

/// <summary>
/// SojournVoice: voice chat on the agent API, for the assistant and tools. What it leaves out is deliberate: nothing
/// here unmutes the mic (opening a live mic is the person's own button) or answers a ringing call (the person's own
/// press on the panel); nothing reads or records what anyone says.
/// </summary>
public sealed class VoiceApi : LeapApi
{
    readonly VoiceClient _v;
    readonly VoiceModeration _mod;
    readonly CallRejects _rejects;

    public override string Name => "SojournVoice";
    public override string Desc => "Voice chat with residents: status, muting, people's volume, calls, conferences, moderation";
    public override bool NeedsSession => false;

    public VoiceApi(AgentSession s, VoiceClient voice, VoiceModeration mod, CallRejects rejects, bool localGrid) : base(s)
    {
        _v = voice;
        _mod = mod;
        _rejects = rejects;
        if (localGrid)
            Op("testInvite", "{id, name, kind: p2p|group|conference, session?} -> {}: tests on a local grid: a voice invitation as the region "
                + "would bring it, through the call switches before it rings", a =>
            {
                var kind = a.Str("kind") ?? "p2p";
                if (kind is not ("p2p" or "group" or "conference")) throw new LeapError("kind: p2p, group or conference");
                _v.TestInvite(Id(a), a.Str("name") ?? "test", kind, a.Uuid("session") ?? UUID.Random());
                return new JsonObject();
            });
        Op("conference", "{ids: [resident UUIDs], name?} -> {calling}: starts an ad-hoc voice conference with them (they answer on "
            + "their side); leaves nearby voice for it; the mic starts muted in open mic, as the viewer's", a =>
        {
            Running();
            var ids = a.UuidList("ids").Where(id => id != UUID.Zero && id != S.Client?.Self.AgentID).Distinct().ToList();
            if (ids.Count == 0) throw new LeapError("ids: the residents to call (UUIDs)");
            var name = a.Str("name") ?? string.Join(", ", ids.Take(4).Select(id => NameOf(id) ?? _mod.NameOf(id))) + (ids.Count > 4 ? $" and {ids.Count - 4} more" : "");
            _v.Conference(ids, name, byPerson: false);
            return new JsonObject { ["calling"] = name, ["people"] = ids.Count };
        });
        Op("moderate", "{id: a resident's UUID or name, mute: bool} -> {who, muted, where, confirmed_by_voice_server}: as a moderator, mutes "
            + "or unmutes one person's voice for everyone (nearby voice, as estate managers and parcel owners may; or a group or conference "
            + "call's moderators). Waits for the person's confirmation", async (a, ct) =>
        {
            Running();
            return await _mod.MuteAsync(_mod.Speaker(a.Str("id") ?? a.Str("name")), a.Bool("mute", true), ct);
        });
        Op("moderateAll", "{mute: bool} -> {where, everyone_muted}: as a moderator, mutes or unmutes everyone else's voice (nearby: "
            + "people who arrive later too, until unmuted). Waits for the person's confirmation", async (a, ct) =>
        {
            Running();
            return await _mod.MuteAllAsync(a.Bool("mute", true), ct);
        });
        Op("calls", "{} -> {reject_p2p_calls, reject_group_calls, reject_conference_calls, friends_only, do_not_disturb, turned_away}: "
            + "the switches that turn calls away before they ring (settings), and how many each turned away this session", _ => _rejects.Status());
        if (localGrid)
            Op("testCall", "{channel, name} -> {}: tests on a local grid: join a non-spatial channel as if a call had been set up", a =>
            {
                _v.TestCall(a.Str("channel") ?? "test", a.Str("name") ?? "test");
                return new JsonObject();
            });
        if (localGrid)
            Op("testRing", "{id, name} -> {}: tests on a local grid: a call ringing, as an invitation would bring it", a =>
            {
                _v.TestRing(Id(a), a.Str("name") ?? "test");
                return new JsonObject();
            });
        Op("status", "{} -> {status, channel, mode, mic_live, muted, moderator_muted, i_moderate, hear_from, call?, ringing?, people:[{id, name, "
            + "speaking, power, moderator_muted, volume?, muted_by_me?}]}: who's in voice nearby (or in the call) and who is speaking", _ => Status());
        Op("mute", "{} -> {muted}: mutes the person's mic toward residents (open mic). Unmuting is the person's own button, "
            + "not an op", _ =>
        {
            _v.SetMuted(true);
            return new JsonObject { ["muted"] = true };
        });
        Op("peerMute", "{id, mute: bool} -> {}: mutes or unmutes one resident's voice in the person's own hearing (nobody else's); kept "
            + "on the account's block list as a voice mute (the viewer's Mute Voice: their chat still shows; a block mutes voice too)", a =>
        {
            _v.SetPeerMute(Id(a), a.Bool("mute", true));
            return new JsonObject();
        });
        Op("peerVolume", "{id, volume: 0-2} -> {}: one resident's loudness for the person (1 as they come); kept between sessions", a =>
        {
            _v.SetPeerVolume(Id(a), (float)Math.Clamp(a.Num("volume", 1), 0, 2));
            return new JsonObject();
        });
        Op("call", "{id, name?} -> {calling}: calls a resident (voice, one to one); they answer on their side. Leaves nearby "
            + "voice for the call; the mic stays as it is", a =>
        {
            Running();
            var id = Id(a);
            _v.Call(id, a.Str("name") ?? NameOf(id) ?? id.ToString(), byPerson: false);
            return new JsonObject { ["calling"] = id.ToString() };
        });
        Op("callGroup", "{id, name?} -> {calling}: starts or joins a group's voice call (the mic starts muted in open mic)", a =>
        {
            Running();
            var id = Id(a);
            if (!S.State.Groups.TryGetValue(id, out var g)) throw new LeapError("I'm not in that group");
            if (!a.Has("name")) a["name"] = g.Name;
            _v.CallGroup(id, a.Str("name") ?? id.ToString());
            return new JsonObject { ["calling"] = id.ToString() };
        });
        Op("hangup", "{} -> {}: leaves the call, back to nearby voice", _ =>
        {
            if (_v.CallWith is null) throw new LeapError("not in a call");
            _v.Hangup();
            return new JsonObject();
        });
        Op("decline", "{} -> {}: declines the call that's ringing (answering is the person's own press on the panel)", _ =>
        {
            if (_v.Incoming is null) throw new LeapError("no call is ringing");
            _v.Decline();
            return new JsonObject();
        });
    }

    void Running()
    {
        if (!_v.Available) throw new LeapError("voice chat isn't running in this session");
    }

    static UUID Id(JsonObject a) =>
        UUID.TryParse(a.Str("id") ?? "", out var id) && id != UUID.Zero ? id : throw new LeapError("id: a resident's UUID");

    string? NameOf(UUID id)
    {
        if (S.Client is not { } c) return null;
        Simulator[] sims;
        lock (c.Network.Simulators) sims = c.Network.Simulators.ToArray();
        foreach (var sim in sims)
            if (sim.ObjectsAvatars.Values.FirstOrDefault(av => av.ID == id) is { Name: { Length: > 0 } n }) return n;
        return null;
    }

    JsonObject Status()
    {
        var people = new JsonArray();
        var self = S.Client?.Self.AgentID ?? UUID.Zero;
        foreach (var p in _v.Peers)
        {
            if (p.Id == self) continue;
            people.Add(new JsonObject
            {
                ["id"] = p.Id.ToString(), ["name"] = NameOf(p.Id), ["speaking"] = p.Speaking, ["power"] = Math.Round(p.Power, 2),
                ["moderator_muted"] = p.ModeratorMuted,
            });
            if (_v.PeerIsMuted(p.Id)) people[^1]!["muted_by_me"] = true;
            if (_v.PeerVolume(p.Id) is var vol && Math.Abs(vol - 1) > 0.001f) people[^1]!["volume"] = Math.Round(vol, 2);
        }
        var r = new JsonObject
        {
            ["status"] = _v.Status, ["channel"] = _v.Channel, ["mode"] = _v.Mode == VoiceMode.PushToTalk ? "push-to-talk" : "open mic",
            ["mic_live"] = _v.MicOpen, ["muted"] = _v.Muted, ["moderator_muted"] = _v.ModeratorMuted, ["people"] = people,
            ["hear_from"] = _v.Ear.ToString().ToLowerInvariant(),
        };
        if (_mod.WhyNot() is null) r["i_moderate"] = true;
        if (_v.CallWith is { } with) r["call"] = with;
        if (_v.Incoming is { } inc)
        {
            r["ringing"] = new JsonObject { ["from"] = inc.Name, ["id"] = inc.From.ToString(), ["p2p"] = inc.P2P, ["kind"] = inc.Kind };
            if (inc.SessionName is { } sn) r["ringing"]!["group"] = sn;
        }
        return r;
    }
}
