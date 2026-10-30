using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Client.Assistant;
using Sojourn.Client.Social;
using Sojourn.Client.Xr;
using Sojourn.Core.Agents;

namespace Sojourn.Client.Private;

/// <summary>
/// Dictation (docs/assistant.md > Incognito), by its key only: the person's words for other people, heard and written on this PC by
/// the sidecar's Whisper and never given to the assistant, put up on the panel as a draft for the person to send or
/// cancel. Held: Y on the desktop; in the headset both grips at once (the left grip talks to people, the right to the
/// assistant: both together talk to people in writing). The sidecar gets `sojourn {what: dictate_down}` and
/// `{what: dictate_up}` and answers with SojournPrivate draft; the draft goes to the IM conversation open on the chat
/// feed, else nearby chat, straight through the session (not the agent API's gate). The sidecar hears only that it was
/// sent or cancelled (`{what: draft, done}`), never where to.
/// </summary>
public sealed class Dictation : IDictation
{
    readonly AgentSession _s;
    readonly AssistantHost _a;
    readonly SocialView _social;
    readonly object _lock = new();
    sealed record Draft(string Text, DraftTo To, UUID? Session, string? Name, long Seq);
    Draft? _draft;
    long _seq;
    long _hearingUntil;
    bool _l, _r, _swallowL, _swallowR;

    /// <summary>The dictation button is held: the mic is the sidecar's alone (voice chat's gate is shut to residents).</summary>
    public bool Held { get; private set; }

    /// <summary>A draft waits for the person.</summary>
    public bool Pending { get { lock (_lock) return _draft != null; } }

    public Dictation(AgentSession s, AssistantHost assistant, SocialView social, PrivateApi api)
    {
        _s = s;
        _a = assistant;
        _social = social;
        api.Dictated = Arrived;
        assistant.DraftPressed = Pressed;
    }

    void Tell(JsonObject d) => _s.Events.Log("sojourn", d);

    public void Down()
    {
        if (Held) return;
        Held = true;
        _a.Panel.Update(p => p.Dictation = PanelDictation.Held);
        Tell(new JsonObject { ["what"] = "dictate_down" });
    }

    public void Up()
    {
        if (!Held) return;
        Held = false;
        _hearingUntil = Environment.TickCount64 + 20_000;
        _a.Panel.Update(p => p.Dictation = PanelDictation.Hearing);
        Tell(new JsonObject { ["what"] = "dictate_up" });
    }

    /// <summary>The headset's grips: both at once dictate. Whichever went first already started its own thing (talking
    /// to the assistant, or voice's push-to-talk): the assistant's talk is dropped without a talk_up, so nothing heard
    /// reaches it, and voice's grip is let go. Until each grip is let go, neither does its own thing again.</summary>
    public XrControllers Headset(XrControllers c)
    {
        bool l = c.LeftGrip > 0.6f || (_l && c.LeftGrip >= 0.35f);
        bool r = c.RightGrip > 0.6f || (_r && c.RightGrip >= 0.35f);
        (_l, _r) = (l, r);
        if (!Held && l && r)
        {
            _a.DropTalk();
            Down();
            _swallowL = _swallowR = true;
        }
        else if (Held && (!l || !r)) Up();
        if (!l) _swallowL = false;
        if (!r) _swallowR = false;
        if (_swallowL) c.LeftGrip = 0;
        if (_swallowR) c.RightGrip = 0;
        return c;
    }

    /// <summary>Main thread, every frame: the hearing state runs out if no words come; a waiting draft follows the
    /// conversation the person opens on the feed (a few times a second).</summary>
    public void Frame()
    {
        long now = Environment.TickCount64;
        if (_hearingUntil != 0 && now > _hearingUntil)
        {
            _hearingUntil = 0;
            _a.Panel.Update(p => { if (p.Dictation == PanelDictation.Hearing) p.Dictation = PanelDictation.None; }, show: false, news: false);
        }
        if (now < _retargetAt) return;
        _retargetAt = now + 250;
        Draft? d;
        lock (_lock) d = _draft;
        if (d == null) return;
        var (to, session, name) = Destination();
        if (to == d.To && session == d.Session && name == d.Name) return;
        Draft next;
        lock (_lock)
        {
            if (_draft != d) return;
            _draft = next = d with { To = to, Session = session, Name = name, Seq = ++_seq };
        }
        Show(next, news: false);
    }

    long _retargetAt;

    /// <summary>The conversation open on the feed (the newest, if more than one): who it's with or the group, and its
    /// session; else nearby chat.</summary>
    public (DraftTo To, UUID? Session, string? Name) Destination()
    {
        var opened = _social.Opened;
        if (opened.Length == 0) return (DraftTo.Nearby, null, null);
        var entries = _social.Feed.Snapshot().Entries;
        var open = entries.Where(e => opened.Contains(e.Seq) && e.Kind is FeedKind.Im or FeedKind.Group && UUID.TryParse(e.Session, out _))
            .OrderByDescending(e => entries.Where(x => x.Session == e.Session).Max(x => x.At)).FirstOrDefault();
        if (open == null) return (DraftTo.Nearby, null, null);
        return (open.Kind == FeedKind.Group ? DraftTo.Group : DraftTo.Im, UUID.Parse(open.Session!), open.SessionName ?? open.Name);
    }

    /// <summary>The other person of the one-to-one IM open on the feed (private mode's per-person trigger).</summary>
    public UUID? OpenImWith()
    {
        var opened = _social.Opened;
        if (opened.Length == 0) return null;
        var entries = _social.Feed.Snapshot().Entries;
        var open = entries.FirstOrDefault(e => opened.Contains(e.Seq) && e.Kind == FeedKind.Im && UUID.TryParse(e.Session, out _));
        if (open == null) return null;
        return entries.Where(e => e.Session == open.Session && !e.Mine && e.From != UUID.Zero).Select(e => (UUID?)e.From).FirstOrDefault();
    }

    void Arrived(string text)
    {
        var (to, session, name) = Destination();
        Draft d;
        lock (_lock) _draft = d = new Draft(text, to, session, name, ++_seq);
        _hearingUntil = 0;
        Show(d, news: true);
    }

    void Show(Draft d, bool news) => _a.Panel.Update(p =>
    {
        p.Draft = new PanelDraft(d.Text, d.To, d.Name, d.Seq);
        if (p.Dictation == PanelDictation.Hearing) p.Dictation = PanelDictation.None;
    }, show: true, news: news);

    /// <summary>A draft's key on the panel; only a hand's press (a posted click does nothing).</summary>
    void Pressed(int index, bool real)
    {
        if (!real) return;
        var (seq, send) = Panel.DraftKey(index);
        Draft? d;
        lock (_lock) d = _draft?.Seq == seq ? _draft : null;
        if (d == null) return;
        if (send) Send(d); else Cancel(d);
    }

    /// <summary>The desktop's keys while a draft waits: Enter sends, Esc cancels (real keys only). True if taken.</summary>
    public bool Key(Silk.NET.Input.Key key)
    {
        Draft? d;
        lock (_lock) d = _draft;
        if (d == null) return false;
        switch (key)
        {
            case Silk.NET.Input.Key.Enter or Silk.NET.Input.Key.KeypadEnter:
                if (RealKeys.Down(RealKeys.Return)) Send(d);
                return true;
            case Silk.NET.Input.Key.Escape:
                if (RealKeys.Down(RealKeys.Escape)) Cancel(d);
                return true;
        }
        return false;
    }

    bool Take(Draft d)
    {
        lock (_lock)
        {
            if (_draft != d) return false;
            _draft = null;
        }
        _a.Panel.Update(p => { p.Draft = null; if (p.Notice == "not sent") p.Notice = null; }, show: false);
        return true;
    }

    void Cancel(Draft d)
    {
        if (Take(d)) Tell(new JsonObject { ["what"] = "draft", ["done"] = "cancelled" });
    }

    void Send(Draft d)
    {
        if (!Take(d)) return;
        _ = Task.Run(async () =>
        {
            string? failed = null;
            try
            {
                var r = d.Session is { } sid
                    ? await Run("TesselIM", "send", new JsonObject { ["session_id"] = sid.ToString(), ["message"] = d.Text })
                    : await Run("LLChatBar", "sendChat", new JsonObject { ["message"] = d.Text });
                if (r is JsonObject o && o.ContainsKey("error")) failed = o["error"]?.ToString();
            }
            catch (Exception e) { failed = e.Message; }
            if (failed == null)
            {
                Tell(new JsonObject { ["what"] = "draft", ["done"] = "sent" });
                return;
            }
            // not sent: the draft comes back (and says so) rather than the words being lost
            Draft back;
            lock (_lock) _draft = back = d with { Seq = ++_seq };
            _a.Panel.Update(p => { p.Draft = new PanelDraft(back.Text, back.To, back.Name, back.Seq); p.Notice = "not sent"; });
        });
    }

    /// <summary>The session's own op, as the client (not a caller through the gate).</summary>
    async Task<JsonNode?> Run(string pump, string op, JsonObject args)
    {
        if (!_s.Online) throw new InvalidOperationException("not connected");
        if (!_s.Api.ApiMap.TryGetValue(pump, out var api) || !api.Ops.TryGetValue(op, out var h)) throw new InvalidOperationException($"no {pump} {op}");
        return await h.Run(args, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(25));
    }
}
