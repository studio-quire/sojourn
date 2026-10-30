using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// The person's display name and their private notes on others, as the official viewer does them (2026-10-28, develop):
/// - Display name (llfloaterdisplayname.cpp, llviewerdisplayname.cpp): at most 31 characters; setting it to the
///   username is a reset (an empty name sent); the SetDisplayName capability takes [old, new] and the answer comes
///   later on the event queue (SetDisplayNameReply: 200 done, else the grid's reason). Second Life allows a change once
///   a week (display_name_next_update); the floater refuses until then, a reset excepted. Changing it waits for the
///   person's press on the exact name (the client's confirmation gate).
/// - Profile notes (llpanelprofile.cpp LLPanelProfileNotes): the AgentProfile capability's notes for that person (GET,
///   and a PUT of {notes}, up to 65530 characters), else the old AvatarNotesRequest/AvatarNotesUpdate messages
///   (OpenSim). Only the person sees them. Replacing notes that say something else waits for the person's press, as a
///   delete does (their words would be gone); notes where there were none, or a line appended, don't.
/// Claude Opus 5.5, 2026-10-28.
/// </summary>
public sealed class NamesAndNotes
{
    readonly AgentSession _s;
    public NamesAndNotes(AgentSession s) => _s = s;
    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    public const int MaxDisplayName = 31, MaxNotes = 65530;

    // ---------- display name ----------

    /// <summary>The person's names as the display-name service has them, with when the next change is allowed.</summary>
    public async Task<AgentDisplayName?> MineAsync(CancellationToken ct)
    {
        var c = C;
        if (!c.Avatars.DisplayNamesAvailable()) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        var (ok, names, _) = await c.Avatars.GetDisplayNamesAsync(new List<UUID> { c.Self.AgentID }, cts.Token);
        var me = ok ? names?.FirstOrDefault(n => n.ID == c.Self.AgentID) : null;
        if (me != null) { _s.Names.SetDisplay(me); _mine = me; }
        return me;
    }

    static string UserNameOf(AgentDisplayName d) =>
        d.UserName is { Length: > 0 } u ? u : World.NameCache.UserName($"{d.LegacyFirstName} {d.LegacyLastName}".Trim());

    static bool Locked(AgentDisplayName d) => d.NextUpdate > DateTime.UtcNow.AddSeconds(5);

    public async Task<JsonObject> ReadAsync(CancellationToken ct)
    {
        var d = await MineAsync(ct) ?? throw new LeapError("this grid has no display names (Second Life does)");
        var o = new JsonObject
        {
            ["display_name"] = d.DisplayName, ["username"] = UserNameOf(d), ["is_default"] = d.IsDefaultDisplayName,
            ["can_change_now"] = !Locked(d),
        };
        if (d.NextUpdate.Year > 2000) o["next_change"] = d.NextUpdate.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        return o;
    }

    AgentDisplayName? _mine;

    /// <summary>What a change would send: the new name ("" for a reset) and why not, if it can't be done. The
    /// confirmation describes from this, so what's confirmed is what goes.</summary>
    public async Task<(string Send, bool Reset, AgentDisplayName Mine, string? Refusal)> PlanAsync(JsonObject a, CancellationToken ct)
    {
        var d = await MineAsync(ct) ?? throw new LeapError("this grid has no display names (Second Life does)");
        var (send, reset, refusal) = Plan(d, a);
        return (send, reset, d, refusal);
    }

    /// <summary>Why a change can't be done, from what's known already (no asking the grid): the confirmation gate asks
    /// only for one that could go. Null: ask.</summary>
    public string? Refusal(JsonObject a)
    {
        if (_s.Client is not { } c) return "viewer not connected";
        if (!c.Avatars.DisplayNamesAvailable()) return "this grid has no display names (Second Life does)";
        try { return _mine is { } d ? Plan(d, a).Refusal : null; }
        catch (LeapError e) { return e.Message; }
    }

    static (string Send, bool Reset, string? Refusal) Plan(AgentDisplayName d, JsonObject a)
    {
        bool reset = a.Bool("reset", false);
        var want = (a.Str("name") ?? "").Trim();
        if (!reset && want.Length == 0) throw new LeapError("need name (the new display name) or reset: true");
        if (!reset && string.Equals(want, UserNameOf(d), StringComparison.OrdinalIgnoreCase)) reset = true;   // the floater's reset
        if (reset) return ("", true, d.IsDefaultDisplayName ? "the display name is already the username" : null);
        int chars = want.EnumerateRunes().Count();
        if (chars > MaxDisplayName)
            return (want, false, $"Display names can have a maximum of {MaxDisplayName} characters (this one has {chars})");
        if (want == d.DisplayName && !d.IsDefaultDisplayName) return (want, false, "that's already the display name");
        if (Locked(d))
            return (want, false, $"Second Life allows a new display name once a week: the next change can be after {When(d.NextUpdate)}");
        return (want, false, null);
    }

    public static string When(DateTime utc) =>
        utc.ToLocalTime().ToString("MMMM d, h:mm tt", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<JsonObject> SetAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var (send, reset, mine, refusal) = await PlanAsync(a, ct);
        if (refusal != null) throw new LeapError(refusal);
        if (c.Network.CurrentSim?.Caps?.CapabilityURI("SetDisplayName") is not { } cap) throw new LeapError("the region has no SetDisplayName capability");
        var reply = new TaskCompletionSource<SetDisplayNameReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(object? _, SetDisplayNameReplyEventArgs e) => reply.TrySetResult(e);
        c.Self.SetDisplayNameReply += On;
        try
        {
            var body = new OSDMap { ["display_name"] = new OSDArray { OSD.FromString(mine.DisplayName ?? ""), OSD.FromString(send) } };
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            var (resp, _) = await c.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, body, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new LeapError($"Sorry, we could not set your display name. Please try again later. ({(int)resp.StatusCode})");
            SetDisplayNameReplyEventArgs r;
            try { r = await reply.Task.WaitAsync(TimeSpan.FromSeconds(25), ct); }
            catch (TimeoutException) { throw new LeapError("sent, but the grid hasn't said whether it took it; ask displayName in a minute"); }
            if (r.Status != 200)
            {
                if (r.Status == 409) _ = MineAsync(CancellationToken.None);   // the viewer refetches its own name on a conflict
                throw new LeapError(r.Reason is { Length: > 0 } why ? $"the grid refused it: {why}" : "Sorry, we could not set your display name. Please try again later.");
            }
            if (r.DisplayName is { } now && now.ID == c.Self.AgentID) { _s.Names.SetDisplay(now); _mine = now; }
            var shown = r.DisplayName?.DisplayName ?? send;
            // SetDisplayNameSuccess: it takes a while for everyone to learn a new name
            _s.Notify.Add("SetDisplayNameSuccess", "notifytip",
                $"Hi {shown}! It takes a while for everyone to learn about a new name: several days in objects, scripts and search.",
                Notifications.None, substitutions: new JsonObject { ["DISPLAY_NAME"] = shown });
            var o = new JsonObject { ["display_name"] = shown, ["reset"] = reset };
            if (r.DisplayName is { } dn && dn.NextUpdate.Year > 2000) o["next_change"] = dn.NextUpdate.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
            return o;
        }
        finally { c.Self.SetDisplayNameReply -= On; }
    }

    /// <summary>Someone's display name changed while they're about (LLDisplayNameUpdate: "[OLD] ([SLID]) is now known as [NEW]").</summary>
    /// <summary>The person's own names fetched once logged in, so a change's refusal is known before anyone is asked.</summary>
    internal void OnLoggedIn(GridClient c)
    {
        _mine = null;
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 40 && !c.Avatars.DisplayNamesAvailable(); i++) await Task.Delay(500);
                if (c.Avatars.DisplayNamesAvailable()) await MineAsync(CancellationToken.None);
            }
            catch (Exception e) { _s.Log.LogDebug(e, "own display name"); }
        });
    }

    internal void Wire(GridClient c)
    {
        c.Avatars.DisplayNameUpdate += (_, e) =>
        {
            try
            {
                if (e.DisplayName is not { } d || d.ID == UUID.Zero) return;
                _s.Names.SetDisplay(d);
                if (string.IsNullOrEmpty(e.OldDisplayName) || e.OldDisplayName == d.DisplayName) return;
                var user = UserNameOf(d);
                _s.Notify.Add("DisplayNameUpdate", "notifytip", $"{e.OldDisplayName} ({user}) is now known as {d.DisplayName}.", Notifications.None,
                    payload: new JsonObject { ["from_id"] = d.ID.ToString() },
                    substitutions: new JsonObject { ["OLD_NAME"] = e.OldDisplayName, ["SLID"] = user, ["NEW_NAME"] = d.DisplayName });
            }
            catch (Exception x) { _s.Log.LogDebug(x, "display name update"); }
        };
    }

    // ---------- notes ----------

    UUID Person(JsonObject a)
    {
        var who = a.Str("to") ?? a.Str("id");
        if (UUID.TryParse(who ?? "", out var id) && id != UUID.Zero) return id;
        if (_s.People.Friend(who) is { } f) return f.UUID;
        throw new LeapError("to: the person's id (or a friend's name)");
    }

    readonly ConcurrentDictionary<UUID, string> _notes = new();   // as last read or written this session

    public async Task<string> NotesOfAsync(UUID id, CancellationToken ct) => _notes[id] = await FetchNotesAsync(id, ct);

    /// <summary>What a setNotes would destroy (the confirmation gate asks): the person and the notes it replaces, when
    /// those say something and the new text differs; null when nothing is lost (an append, none there, the same text).
    /// From what was last read or written, else asked of the grid now (synchronous: the gate's question is).</summary>
    public (UUID Id, string Was)? Replacing(JsonObject a)
    {
        if (a.Bool("append", false) || _s.Client is null) return null;
        UUID id;
        try { id = Person(a); } catch (LeapError) { return null; }   // the op says what's wrong
        if (!_notes.TryGetValue(id, out var was))
            try { was = Task.Run(() => NotesOfAsync(id, CancellationToken.None)).GetAwaiter().GetResult(); }
            catch (Exception e) { _s.Log.LogDebug(e, "notes before replacing them"); return (id, "(couldn't be read)"); }
        return was.Length > 0 && was != (a.Str("text") ?? "") ? (id, was) : null;
    }

    async Task<string> FetchNotesAsync(UUID id, CancellationToken ct)
    {
        var c = C;
        if (c.Network.CurrentSim?.Caps?.CapabilityURI("AgentProfile") != null)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var (ok, profile) = await c.Avatars.RequestAgentProfileAsync(id, cts.Token);
            if (ok && profile != null) return profile.Notes ?? "";
        }
        try
        {
            return await GroupOps.AskAsync<AvatarNotesReplyEventArgs, string>(
                h => c.Avatars.AvatarNotesReply += h, h => c.Avatars.AvatarNotesReply -= h,
                e => (e.AvatarID == id, e.Notes ?? ""), () => c.Avatars.RequestAvatarNotes(id), "notes", ct, TimeSpan.FromSeconds(6));
        }
        catch (LeapError) { return ""; }   // none kept: grids answer nothing at all
    }

    public async Task<JsonObject> NotesAsync(JsonObject a, CancellationToken ct)
    {
        var id = Person(a);
        var notes = await NotesOfAsync(id, ct);
        return new JsonObject { ["id"] = id.ToString(), ["name"] = _s.People.NameOf(id), ["notes"] = notes };
    }

    /// <summary>Replace (or with append, add a line to) the person's notes on someone; the old text comes back in "was".</summary>
    public async Task<JsonObject> SetNotesAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var id = Person(a);
        if (id == c.Self.AgentID) throw new LeapError("notes are on other people");
        var text = a.Str("text") ?? throw new LeapError("need text (empty clears them)");
        var was = await NotesOfAsync(id, ct);
        var now = a.Bool("append", false) && was.Length > 0 ? was.TrimEnd() + "\n" + text : text;
        if (now.Length > MaxNotes) throw new LeapError($"notes hold {MaxNotes} characters at most");
        if (c.Network.CurrentSim?.Caps?.CapabilityURI("AgentProfile") is { } cap)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var (resp, _) = await c.HttpCapsClient.PutAsync(new Uri($"{cap.AbsoluteUri.TrimEnd('/')}/{id}"), OSDFormat.Xml,
                new OSDMap { ["notes"] = OSD.FromString(now) }, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new LeapError($"the grid refused them ({(int)resp.StatusCode})");
        }
        else
        {
            if (Encoding.UTF8.GetByteCount(now) > 65000) throw new LeapError("too long for this grid's notes");
            c.Self.UpdateProfileNotes(id, now);
        }
        _notes[id] = now;
        return new JsonObject { ["id"] = id.ToString(), ["name"] = _s.People.NameOf(id), ["notes"] = now, ["was"] = was };
    }
}
