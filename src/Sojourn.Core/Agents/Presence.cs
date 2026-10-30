using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// How the person shows to others, as the viewer shows it: Away (LLAgent::setAFK: the away animation and the AWAY
/// control flag; automatic after a while without the person, cleared when they're back), Do Not Disturb
/// (setDoNotDisturb: its animation; IMs kept but not shown, an autoresponse once per conversation, objects' IMs and
/// inventory from objects turned away, as llimprocessing.cpp does), Firestorm's away autoresponse (off by default),
/// typing in chat and IMs (startTyping/stopTyping, the typing animation, LLIMModel::sendTypingState), and the
/// person's own hover height (the AgentPreferences capability, kept per account as AvatarHoverOffsetZ).
///
/// The autoresponses carry the person's own words (the TPV policy: no auto-replying as the user beyond what they set):
/// the viewer's standard sentence until they set theirs, and setting theirs waits for their press on the exact text
/// (the client's confirmation gate). Nothing here answers anyone in any other way.
/// Claude Opus 5.5, 2026-10-27.
/// </summary>
public sealed class Presence
{
    readonly AgentSession _s;
    public Presence(AgentSession s) => _s = s;
    PeopleStore Store => _s.PeopleStore;

    /// <summary>The viewer's own sentences (settings_per_account.xml DoNotDisturbModeResponse; Firestorm's
    /// FSAwayAvatarResponse), sent until the person sets their own.</summary>
    public const string DefaultDndResponse = "This resident has turned on 'Do Not Disturb' and will see your message later.";
    public const string DefaultAwayResponse = "The Resident you messaged is currently away from keyboard. Your message will still be shown in their IM panel for later viewing.";
    public const int MaxResponse = 1000;
    public const double MinHover = -2, MaxHover = 2;
    /// <summary>The viewer's AFKTimeout choices, in minutes (0: never); 5 is its default.</summary>
    public static readonly int[] AwayAfterChoices = { 0, 2, 5, 10, 30, 60, 90, 120 };

    public event Action? Changed;

    // ---------- the state ----------

    bool _away, _awayAuto, _dnd;
    public bool Away => _away;
    /// <summary>Set by the client's idle timer, not the person: their being back clears it.</summary>
    public bool AwayIsAuto => _away && _awayAuto;
    public bool DoNotDisturb => _dnd;

    public string DndResponse => Store.Text("dnd_response") is { Length: > 0 } t ? t : DefaultDndResponse;
    public bool DndResponseIsOwn => Store.Text("dnd_response") is { Length: > 0 };
    public string AwayResponse => Store.Text("away_response") is { Length: > 0 } t ? t : DefaultAwayResponse;
    public bool AwayResponseOn => Store.Flag("away_response_on", false);
    public int AwayAfterMinutes => (int)Store.Number("away_after_minutes", 5);
    public bool TypingSent => Store.Flag("typing_sent", true);
    public bool TypingAnimation => Store.Flag("typing_animation", true);

    readonly ConcurrentDictionary<UUID, byte> _answered = new();   // conversations answered this spell of DND or away
    int _heldIms;
    readonly HashSet<UUID> _heldFrom = new();

    public void SetAway(bool on, bool auto = false)
    {
        if (_s.Client is not { } c) { _away = on; _awayAuto = auto && on; return; }
        if (on == _away) { if (on && !auto) _awayAuto = false; return; }
        _away = on;
        _awayAuto = auto && on;
        if (on) _answered.Clear();
        c.Self.Movement.Away = on;
        if (on) c.Self.AnimationStart(Animations.AWAY, true); else c.Self.AnimationStop(Animations.AWAY, true);
        c.Self.Movement.SendUpdate(true);
        Changed?.Invoke();
    }

    public void SetDoNotDisturb(bool on)
    {
        if (on == _dnd) return;
        _dnd = on;
        if (_s.Client is { } c)
        {
            if (on) c.Self.AnimationStart(Animations.BUSY, true); else c.Self.AnimationStop(Animations.BUSY, true);
        }
        if (on) { _answered.Clear(); lock (_heldFrom) { _heldIms = 0; _heldFrom.Clear(); } }
        else
        {
            int n; int people;
            lock (_heldFrom) { n = _heldIms; people = _heldFrom.Count; _heldIms = 0; _heldFrom.Clear(); }
            // what waited, said once it's over (the viewer shows the toasts it held back then)
            if (n > 0)
                _s.Notify.Add("DoNotDisturbHeld", "notifytip",
                    $"While you were in Do Not Disturb: {n} IM{(n == 1 ? "" : "s")} from {people} {(people == 1 ? "person" : "people")}.",
                    Notifications.None, substitutions: new JsonObject { ["COUNT"] = n, ["PEOPLE"] = people });
        }
        Changed?.Invoke();
    }

    public JsonObject Status() => new()
    {
        ["away"] = _away, ["away_automatic"] = AwayIsAuto, ["do_not_disturb"] = _dnd,
        ["away_after_minutes"] = AwayAfterMinutes,
        ["dnd_response"] = DndResponse, ["dnd_response_is_theirs"] = DndResponseIsOwn,
        ["away_response_on"] = AwayResponseOn, ["away_response"] = AwayResponse,
        ["typing_sent"] = TypingSent, ["typing_animation"] = TypingAnimation,
    };

    /// <summary>The words of an autoresponse (kind dnd or away), set only through the confirmation gate; empty: back
    /// to the viewer's sentence. away_response_on may come with it.</summary>
    public JsonObject SetResponse(JsonObject a)
    {
        var kind = (a.Str("kind") ?? "dnd").Trim().ToLowerInvariant();
        if (kind is not ("dnd" or "away")) throw new LeapError("kind: dnd or away");
        if (a.Has("text"))
        {
            var text = (a.Str("text") ?? "").Trim();
            if (text.Length > MaxResponse) throw new LeapError($"{MaxResponse} characters at most");
            Store.Set(kind + "_response", text.Length > 0 ? text : null);
        }
        if (kind == "away" && a.Bool("on") is { } on) Store.Set("away_response_on", on);
        Changed?.Invoke();
        return Status();
    }

    public void SetAwayAfter(int minutes) { Store.Set("away_after_minutes", minutes); Changed?.Invoke(); }
    public void SetTypingSent(bool on) { Store.Set("typing_sent", on); if (!on) Typing(false); Changed?.Invoke(); }
    public void SetTypingAnimation(bool on) { Store.Set("typing_animation", on); Changed?.Invoke(); }

    // ---------- what IMs meet while DND (AgentSession.OnIM asks first) ----------

    /// <summary>True when the IM is turned away and OnIM should drop it: objects' IMs and alerts from objects that
    /// aren't mine, and inventory objects offer, declined, while Do Not Disturb (as the viewer does).</summary>
    internal bool Holds(GridClient c, InstantMessage im)
    {
        if (!_dnd) return false;
        var me = c.Self.AgentID;
        switch (im.Dialog)
        {
            case InstantMessageDialog.MessageFromObject:
            case InstantMessageDialog.FromTaskAsAlert:
                return im.FromAgentID != me;
            case InstantMessageDialog.TaskInventoryOffered:
            {
                var type = im.BinaryBucket is { Length: > 0 } b ? (AssetType)b[0] : AssetType.Unknown;
                AgentInventory.AnswerOffer(c, im, type, UUID.Zero, keep: false);
                _s.Log.LogInformation("an object's offer turned away (Do Not Disturb)");
                return true;
            }
            case InstantMessageDialog.MessageFromAgent when im.FromAgentID != me && Live(im) && !string.IsNullOrEmpty(im.Message)
                                                            && !_s.PeopleHide(c, im):   // not ones the switches keep away
                lock (_heldFrom) { _heldIms++; _heldFrom.Add(im.FromAgentID); }
                return false;
        }
        return false;
    }

    /// <summary>Said now, not kept for them while they were away. The viewer sends IM_ONLINE; LibreMetaverse's clients mark
    /// every IM offline, so a live one is also known by having no stored time (LibreMetaverse 3.2.0 turns stored ones'
    /// Unix seconds into ticks; a live one's zero time is 1970).</summary>
    static bool Live(InstantMessage im) =>
        im.Offline == InstantMessageOnline.Online || (im.Timestamp.Year < 2000 && im.Timestamp.Ticks is not (> 1_000_000_000 and < 10_000_000_000));

    /// <summary>Whether the feed should show what others say to the person now (not while Do Not Disturb).</summary>
    public bool Quiet => _dnd;

    void OnIM(GridClient c, InstantMessage im)
    {
        if (!_dnd && !(_away && AwayResponseOn)) return;
        var me = c.Self.AgentID;
        if (im.FromAgentID == me || im.FromAgentID == UUID.Zero || im.ToAgentID != me) return;
        if (_s.Mutes.IsMuted(im.FromAgentID, im.FromAgentName)) return;
        string? words = null;
        switch (im.Dialog)
        {
            // a person's IM, online (not other autoresponses, not stored ones): once a conversation
            case InstantMessageDialog.MessageFromAgent when Live(im) && !string.IsNullOrEmpty(im.Message):
                if (im.GroupIM || _s.IsMyGroup(im.IMSessionID)) return;
                if (!_answered.TryAdd(im.IMSessionID != UUID.Zero ? im.IMSessionID : im.FromAgentID, 0)) return;
                words = _dnd ? DndResponse : AwayResponse;
                break;
            // offers that would bother them: the viewer answers each with the DND sentence too
            case InstantMessageDialog.RequestTeleport:
            case InstantMessageDialog.RequestLure:
            case InstantMessageDialog.FriendshipOffered:
                // one a reject switch turns away gets that sentence instead (Firestorm's order; Agents/Rejects.cs)
                if (_dnd && !_s.Rejects.Hides(c, im)) words = DndResponse;
                break;
        }
        if (words is null) return;
        // an IM's own conversation; an offer's id is its lure or transaction, so the reply goes to the conversation of the
        // two (the viewer's send_do_not_disturb_message sends offers' replies with no session)
        var sid = im.Dialog == InstantMessageDialog.MessageFromAgent && im.IMSessionID != UUID.Zero ? im.IMSessionID : me ^ im.FromAgentID;
        c.Self.InstantMessage(c.Self.Name, im.FromAgentID, words, sid, InstantMessageDialog.BusyAutoResponse, InstantMessageOnline.Online,
            c.Self.SimPosition, c.Network.CurrentSim?.ID ?? UUID.Zero, Utils.EmptyBytes);
        // that one went, not its words (they're the person's own, and the log is the viewer-like chat log)
        _s.Events.Log("TesselEvents", new JsonObject
        {
            ["event"] = "autoresponse", ["kind"] = _dnd ? "dnd" : "away", ["to_id"] = im.FromAgentID.ToString(),
            ["to"] = im.FromAgentName, ["session_id"] = sid.ToString(),
        });
    }

    // ---------- typing ----------

    UUID? _typingTo;     // null: not typing; zero: nearby; else the IM's other person
    DateTime _typingSince, _typingRefreshed;
    bool _typingAnim;

    /// <summary>Starts or stops showing others that the person is writing to them: nearby (to null) or one person's
    /// IM. Starting toward someone else stops the last. A start not refreshed runs out after 30 s (the viewer's own
    /// runs out after 5 s without a key; dictation refreshes it while it lasts).</summary>
    public void Typing(bool on, UUID? to = null)
    {
        if (_s.Client is not { } c || !c.Network.Connected) { _typingTo = null; return; }
        var target = to ?? UUID.Zero;
        lock (this)
        {
            if (on && !TypingSent) on = false;
            if (_typingTo is { } was && (!on || was != target)) Send(c, was, false);
            if (!on) { _typingTo = null; return; }
            var now = DateTime.UtcNow;
            // started, or re-sent now and then while it lasts (a lost packet, someone arriving later)
            if (_typingTo != target || now - _typingSince > TimeSpan.FromSeconds(10)) { Send(c, target, true); _typingSince = now; }
            _typingTo = target;
            _typingRefreshed = now;
        }
    }

    public bool IsTyping => _typingTo != null;

    void Send(GridClient c, UUID to, bool on)
    {
        if (to == UUID.Zero)
        {
            // ChatFromViewer with no words (LibreMetaverse's Chat skips empty messages)
            c.Network.SendPacket(new ChatFromViewerPacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
                ChatData = { Channel = 0, Message = Utils.StringToBytes(""), Type = (byte)(on ? ChatType.StartTyping : ChatType.StopTyping) },
            });
            if (on && TypingAnimation && !_typingAnim) { c.Self.AnimationStart(Animations.TYPE, true); _typingAnim = true; }
            if (!on && _typingAnim) { c.Self.AnimationStop(Animations.TYPE, true); _typingAnim = false; }
        }
        else
            c.Self.InstantMessage(c.Self.Name, to, "typing", c.Self.AgentID ^ to,
                on ? InstantMessageDialog.StartTyping : InstantMessageDialog.StopTyping, InstantMessageOnline.Online,
                c.Self.SimPosition, c.Network.CurrentSim?.ID ?? UUID.Zero, Utils.EmptyBytes);
    }

    // ---------- hover height ----------

    double? _accountHover;

    /// <summary>The person's hover height (metres, -2 to 2): kept per account and sent to the region as the viewer's
    /// sendHoverHeight does (at login, and on each change). Until it's set here, what the account had at login.</summary>
    public double HoverHeight => Store.Read(d => d.Num("hover_height")) ?? _accountHover ?? 0;

    public async Task<JsonObject> SetHoverHeightAsync(double metres, CancellationToken ct)
    {
        if (double.IsNaN(metres) || metres < MinHover || metres > MaxHover) throw new LeapError($"hover height goes from {MinHover} to {MaxHover} m");
        metres = Math.Round(metres, 3);
        Store.Set("hover_height", metres);
        Changed?.Invoke();
        var (sent, why) = await SendHoverAsync(ct);
        var o = new JsonObject { ["hover_height"] = metres, ["sent"] = sent };
        if (why != null) o["note"] = why;
        return o;
    }

    /// <summary>The AgentPreferences capability's POST {hover_height}, its answer read (LibreMetaverse's doesn't say).</summary>
    async Task<(bool, string?)> SendHoverAsync(CancellationToken ct)
    {
        if (_s.Client is not { } c || c.Network.CurrentSim?.Caps?.CapabilityURI("AgentPreferences") is not { } cap)
            return (false, "kept; the region has no AgentPreferences capability, so it goes at the next login or region that has one");
        var h = HoverHeight;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var (resp, data) = await c.HttpCapsClient.PostAsync(cap, LibreMetaverse.StructuredData.OSDFormat.Xml,
                new LibreMetaverse.StructuredData.OSDMap { ["hover_height"] = h }, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (false, $"the region refused it ({(int)resp.StatusCode})");
            if (data != null && LibreMetaverse.StructuredData.OSDParser.Deserialize(data) is LibreMetaverse.StructuredData.OSDMap m && m.ContainsKey("hover_height"))
                _accountHover = m["hover_height"].AsReal();
            return (true, null);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _s.Log.LogWarning(e, "hover height");
            return (false, "the region didn't answer");
        }
    }

    // ---------- wiring ----------

    internal void Wire(GridClient c)
    {
        c.Self.IM += (_, e) => { try { OnIM(c, e.IM); } catch (Exception x) { _s.Log.LogWarning(x, "autoresponse"); } };
        c.Network.SimChanged += (_, _) => Restate(c);
    }

    internal void OnLoggedIn(GridClient c)
    {
        _typingTo = null;
        _typingAnim = false;
        _accountHover = null;
        // a new login starts present and disturbable, as the viewer does
        _away = _awayAuto = _dnd = false;
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 40 && c.Network.CurrentSim?.Caps?.CapabilityURI("AgentPreferences") == null; i++) await Task.Delay(500);
                // one set here goes to the region, as the viewer sends its per-account AvatarHoverOffsetZ; else the
                // account's own (another viewer may have set it) is what's shown
                if (Store.Read(d => d.Has("hover_height"))) await SendHoverAsync(CancellationToken.None);
                else if (await c.Self.GetAgentPreferencesAsync() is { } prefs) _accountHover = prefs.HoverHeight;
            }
            catch (Exception e) { _s.Log.LogDebug(e, "hover height at login"); }
        });
    }

    /// <summary>After a teleport or crossing: the region learns the state anew (the away flag rides every update).</summary>
    void Restate(GridClient c)
    {
        if (_dnd) c.Self.AnimationStart(Animations.BUSY, true);
        if (_away) c.Self.AnimationStart(Animations.AWAY, true);
    }

    /// <summary>Twice a second (the client's timer): typing nobody refreshed runs out.</summary>
    public void Tick()
    {
        if (_typingTo != null && DateTime.UtcNow - _typingRefreshed > TimeSpan.FromSeconds(30)) Typing(false);
    }
}
