using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// A group's chat as the viewers let the person quiet or moderate it (read from their source, 2026-10-28):
/// - Snooze (Firestorm's, from Catznip: llgroupactions.cpp snooze_group_im, llimview.cpp leaveSession): the chat is
///   closed here without leaving the session, its lines dropped until the time is up (GroupSnoozeTime, 15 minutes by
///   default, or a time the person gives), and the first line after that brings it back.
/// - Receive group chat off (Firestorm's exoGroupMuteList, the group profile's "Receive group chat"): kept on the
///   account's own block list as the name "Group:&lt;id&gt;", so Firestorm on another computer honours it too; the
///   session is left and every later invitation to it left at once.
/// - Moderation (the official viewer's LLIMSpeakerMgr::toggleAllowTextChat): a moderator (the group role power, as the
///   chat's member list says) stops or allows one member's text in the group's chat, for everyone, through the
///   ChatSessionRequest capability's "mute update". Voice moderation is batch B10's.
/// Claude Opus 5.5, 2026-10-28.
/// </summary>
public sealed class GroupChat
{
    readonly AgentSession _s;
    public GroupChat(AgentSession s) => _s = s;
    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    public static readonly TimeSpan DefaultSnooze = TimeSpan.FromMinutes(15);
    static string MuteName(UUID group) => "Group:" + group;

    readonly ConcurrentDictionary<UUID, DateTime> _snoozed = new();
    readonly ConcurrentDictionary<UUID, DateTime> _left = new();   // when its session was last left here

    public event Action? Changed;

    /// <summary>A group of mine by id or by name (exact, then a unique start).</summary>
    public UUID Group(JsonObject a)
    {
        if ((a.Uuid("group_id") ?? a.Uuid("group") ?? a.Uuid("session_id")) is { } id)
            return _s.IsMyGroup(id) ? id : throw new LeapError("not one of my groups");
        var name = (a.Str("group") ?? a.Str("name") ?? "").Trim();
        if (name.Length == 0) throw new LeapError("need group_id or group (its name)");
        var groups = _s.State.Groups.Values.ToList();
        var hit = groups.Where(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hit.Count == 0) hit = groups.Where(g => g.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
        return hit.Count switch
        {
            1 => hit[0].ID,
            0 => throw new LeapError($"no group of mine called \"{name}\""),
            _ => throw new LeapError($"\"{name}\" could be {string.Join(", ", hit.Select(g => g.Name))}"),
        };
    }

    string NameOf(UUID group) => _s.State.Groups.TryGetValue(group, out var g) ? g.Name : group.ToString();

    // ---------- what reaches the person (AgentSession.OnIM asks) ----------

    public bool ChatOff(UUID group) => _s.Mutes.IsMuted(UUID.Zero, MuteName(group));

    public DateTime? SnoozedUntil(UUID group) => _snoozed.TryGetValue(group, out var t) && t > DateTime.UtcNow ? t : null;

    /// <summary>The group a line of chat belongs to, if it's a group's (SL sends some as dialog 0 with the group's id).</summary>
    UUID? GroupOf(InstantMessage im)
    {
        if (im.Dialog is not (InstantMessageDialog.SessionSend or InstantMessageDialog.MessageFromAgent or InstantMessageDialog.SessionAdd
            or InstantMessageDialog.SessionGroupStart)) return null;
        return im.IMSessionID != UUID.Zero && _s.IsMyGroup(im.IMSessionID) ? im.IMSessionID : null;
    }

    /// <summary>Whether the person won't see this line (snoozed, or the group's chat off). No side effects.</summary>
    public bool Hides(InstantMessage im) => GroupOf(im) is { } g && (ChatOff(g) || SnoozedUntil(g) != null);

    /// <summary>True when OnIM should drop the line. A snooze that ran out ends here, on the first line after it.</summary>
    internal bool Takes(GridClient c, InstantMessage im)
    {
        if (GroupOf(im) is not { } g) return false;
        if (ChatOff(g))
        {
            // each new invitation to it is left (someone speaking brings one), but not every line of it
            var now = DateTime.UtcNow;
            if (!_left.TryGetValue(g, out var at) || now - at > TimeSpan.FromSeconds(60))
            {
                _left[g] = now;
                try { c.Self.RequestLeaveGroupChat(g); } catch (Exception e) { _s.Log.LogDebug(e, "leaving a group chat"); }
                _s.Log.LogInformation("left {Group}'s chat (receive group chat is off)", NameOf(g));
            }
            return true;
        }
        if (_snoozed.TryGetValue(g, out var until))
        {
            if (until > DateTime.UtcNow) return true;
            if (_snoozed.TryRemove(g, out _)) Changed?.Invoke();
        }
        return false;
    }

    // ---------- the ops ----------

    public JsonObject Info(UUID g)
    {
        var o = new JsonObject
        {
            ["group_id"] = g.ToString(), ["name"] = NameOf(g), ["chat_on"] = !ChatOff(g),
        };
        if (SnoozedUntil(g) is { } t)
        {
            o["snoozed_until"] = t.ToString("yyyy-MM-ddTHH:mm:ssZ");
            o["snoozed_minutes_left"] = Math.Round((t - DateTime.UtcNow).TotalMinutes, 1);
        }
        if (_s.Client is { } c && c.Self.GroupChatSessions.TryGetValue(g, out var members))
        {
            List<ChatSessionMember> list;
            lock (members) list = members.ToList();
            o["in_chat"] = list.Any(m => m.AvatarKey == c.Self.AgentID);
            o["i_moderate"] = IModerate(c, g);
            o["members"] = new JsonArray(list.Select(m => (JsonNode)new JsonObject
            {
                ["id"] = m.AvatarKey.ToString(), ["name"] = _s.Names.DisplayName(m.AvatarKey) ?? m.AvatarKey.ToString()[..8],
                ["moderator"] = m.IsModerator, ["text_stopped"] = m.MuteText,
            }).ToArray());
        }
        else o["in_chat"] = false;
        return o;
    }

    public JsonObject List() => new()
    {
        ["groups"] = new JsonArray(_s.State.Groups.Keys.Select(g =>
        {
            var o = new JsonObject { ["group_id"] = g.ToString(), ["name"] = NameOf(g), ["chat_on"] = !ChatOff(g) };
            if (SnoozedUntil(g) is { } t) o["snoozed_minutes_left"] = Math.Round((t - DateTime.UtcNow).TotalMinutes, 1);
            return (JsonNode)o;
        }).ToArray()),
    };

    /// <summary>Snooze for minutes (null: the default 15), or 0 to end a snooze.</summary>
    public JsonObject Snooze(UUID g, double? minutes)
    {
        if (minutes is <= 0)
        {
            _snoozed.TryRemove(g, out _);
            Changed?.Invoke();
            return Info(g);
        }
        var span = minutes is { } m ? TimeSpan.FromMinutes(Math.Min(m, 60 * 24 * 7)) : DefaultSnooze;
        _snoozed[g] = DateTime.UtcNow + span;
        // the viewer's conversation closes, its unread count with it
        if (_s.State.Ims.TryGetValue(g, out var s)) s.Unread = 0;
        _s.Log.LogInformation("{Group}'s chat snoozed for {Minutes} min", NameOf(g), Math.Round(span.TotalMinutes));
        Changed?.Invoke();
        return Info(g);
    }

    /// <summary>Receive group chat on or off (Firestorm's per-group switch, on the account's block list).</summary>
    public JsonObject SetChat(UUID g, bool on)
    {
        var c = C;
        if (on)
        {
            _s.Mutes.Remove(UUID.Zero, MuteName(g), c);
            _left.TryRemove(g, out _);
        }
        else
        {
            _s.Mutes.Add(UUID.Zero, MuteName(g), "name", c);
            _snoozed.TryRemove(g, out _);
            _left[g] = DateTime.UtcNow;
            c.Self.RequestLeaveGroupChat(g);
            if (_s.State.Ims.TryGetValue(g, out var s)) { s.Ready = false; s.Unread = 0; }
        }
        Changed?.Invoke();
        return Info(g);
    }

    /// <summary>The person moderates this group's chat: their roles carry the power (how the viewer first marks a group's
    /// speakers, llspeakers.cpp), or the chat's member list says so (its later updates; OpenSim's never does).</summary>
    public bool IModerate(GridClient c, UUID g)
    {
        if (_s.State.Groups.TryGetValue(g, out var grp) && (grp.Powers & GroupPowers.ModerateChat) != 0) return true;
        if (c.Self.GroupChatSessions.TryGetValue(g, out var members))
            lock (members)
                return members.Any(m => m.AvatarKey == c.Self.AgentID && m.IsModerator);
        return false;
    }

    /// <summary>A member of the chat by id, or a name they go by among its members.</summary>
    public UUID Member(GridClient c, UUID g, string? who)
    {
        if (string.IsNullOrWhiteSpace(who)) throw new LeapError("need to (a member's id or name)");
        if (UUID.TryParse(who, out var id)) return id;
        var want = who.Trim();
        List<ChatSessionMember> list = new();
        if (c.Self.GroupChatSessions.TryGetValue(g, out var members)) lock (members) list = members.ToList();
        bool Is(string? s) => s is { Length: > 0 } && string.Equals(s.Trim(), want, StringComparison.OrdinalIgnoreCase);
        var hits = list.Where(m => Is(_s.Names.DisplayName(m.AvatarKey)) || Is(_s.Names.Legacy(m.AvatarKey))
                                   || Is(_s.Names.Legacy(m.AvatarKey) is { } l ? World.NameCache.UserName(l) : null)).ToList();
        return hits.Count == 1 ? hits[0].AvatarKey : throw new LeapError(hits.Count == 0
            ? $"nobody called \"{want}\" is in {NameOf(g)}'s chat now" : $"more than one \"{want}\" in the chat; use their id");
    }

    /// <summary>Joins the group's chat if not in it (moderating needs the session), waiting for the region's word.</summary>
    async Task JoinAsync(GridClient c, UUID g, CancellationToken ct)
    {
        bool In() => c.Self.GroupChatSessions.TryGetValue(g, out var m) && m.Any(x => x.AvatarKey == c.Self.AgentID);
        if (In()) return;
        var s = _s.State.Ims.GetOrAdd(g, _ => new ImSession { Id = g, Type = "group", Name = NameOf(g), Ready = false });
        s.Started ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = s.Started;
        c.Self.RequestJoinGroupChat(g);
        try { await started.Task.WaitAsync(TimeSpan.FromSeconds(15), ct); }
        catch (Exception e) when (e is not OperationCanceledException) { throw new LeapError("the group's chat didn't start"); }
        finally { if (s.Started == started) s.Started = null; }
        for (int i = 0; i < 20 && !In(); i++) await Task.Delay(250, ct);
    }

    /// <summary>Stop or allow one member's text in the group's chat, for everyone (a moderator's power).</summary>
    public async Task<JsonObject> ModerateAsync(UUID g, JsonObject a, CancellationToken ct)
    {
        var c = C;
        if (ChatOff(g)) throw new LeapError($"receive group chat is off for {NameOf(g)}; turn it on first");
        await JoinAsync(c, g, ct);
        if (!IModerate(c, g)) throw new LeapError($"only {NameOf(g)}'s moderators can do that (a role with the moderate group chat power)");
        var who = Member(c, g, a.Str("to") ?? a.Str("id"));
        if (who == c.Self.AgentID) throw new LeapError("that's the person themselves");
        bool allow = a.Bool("text") ?? a.Bool("allow") ?? throw new LeapError("text: false stops their text, true allows it again");
        if (c.Network.CurrentSim?.Caps?.CapabilityURI("ChatSessionRequest") is not { } cap) throw new LeapError("the region has no ChatSessionRequest capability");
        var body = new OSDMap
        {
            ["method"] = "mute update",
            ["session-id"] = OSD.FromUUID(g),
            ["params"] = new OSDMap
            {
                ["agent_id"] = OSD.FromUUID(who),
                ["mute_info"] = new OSDMap { ["text"] = OSD.FromBoolean(!allow) },
            },
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var (resp, _) = await c.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, body, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new LeapError($"the region refused it ({(int)resp.StatusCode})");
        // the member list's update follows; wait briefly so the answer can say what it shows
        bool? stopped = null;
        for (int i = 0; i < 16; i++)
        {
            if (c.Self.GroupChatSessions.TryGetValue(g, out var members))
                lock (members)
                    if (members.FirstOrDefault(m => m.AvatarKey == who) is { AvatarKey: var k } m && k != UUID.Zero && m.MuteText == !allow)
                        stopped = m.MuteText;
            if (stopped != null) break;
            await Task.Delay(250, ct);
        }
        return new JsonObject
        {
            ["group"] = NameOf(g), ["member"] = _s.Names.DisplayName(who) ?? who.ToString(), ["text_allowed"] = allow,
            ["confirmed_by_region"] = stopped != null,
        };
    }

    internal void OnLoggedIn()
    {
        _left.Clear();
        _snoozed.Clear();
    }
}
