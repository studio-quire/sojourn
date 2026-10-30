using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using Sojourn.Session.Control;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;

namespace Sojourn.Session.Assistant;

/// <summary>
/// The client's half of the assistant: the panel, the pointer, push-to-talk and typed requests, choices and
/// confirmations. Requests reach the sidecar (assistant/main.py) as `sojourn` events on the agent API:
/// {what: talk_down | talk_up | typed | pick, target?, text?, id?}; it answers through SojournAssistant ops.
/// Input methods and <see cref="Frame"/> run on the main thread.
/// </summary>
public sealed class AssistantHost
{
    readonly AgentSession _s;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastHello = -1;
    string? _mic;

    public Panel Panel { get; } = new();
    public Pointer Pointer { get; }
    public Confirmations Confirmations { get; }
    public LiveSnapshots Snapshots { get; } = new();
    public bool InHeadset
    {
        get => _inHeadset;
        set { _inHeadset = value; Panel.Update(p => { p.Hint = Hint; p.PointHow = PointHow; }, show: false, news: false); }
    }
    bool _inHeadset;
    /// <summary>Which app the person uses, for the assistant's instructions: null the VR client, "access" Sojourn Access
    /// (no view shown, replies through the person's screen reader).</summary>
    public string? App { get; set; }
    public bool Talking { get; private set; }
    public bool Typing => Panel.Read(s => s.Typing != null);

    public string PointHow => InHeadset ? "Point, then pull the trigger" : "Click on it";
    public string Hint => InHeadset ? "hold the right grip and speak" : "hold T and speak, or press Enter to type";

    public AssistantHost(AgentSession session, bool uploadsCost)
    {
        _s = session;
        Pointer = new Pointer(session);
        Confirmations = new Confirmations(session, Panel, uploadsCost);
        Panel.Update(p => p.Hint = Hint, show: false);
    }

    public bool SidecarAlive => _lastHello >= 0 && _clock.Elapsed.TotalSeconds - _lastHello < 15;

    Func<bool>? _sidecarRunning;
    double _sidecarStartedAt;

    /// <summary>The sidecar was launched: until its first hello (it loads hearing first, ~20 s) it's starting, not
    /// missing, while its process lives and for up to two minutes.</summary>
    public void SidecarLaunched(Func<bool> running)
    {
        _sidecarStartedAt = _clock.Elapsed.TotalSeconds;
        _sidecarRunning = running;
    }

    bool SidecarStarting => _lastHello < 0 && _sidecarRunning?.Invoke() == true && _clock.Elapsed.TotalSeconds - _sidecarStartedAt < 120;

    // a request made while it starts is kept in the event log and reaches it once it's ready
    const string Starting = "The assistant is starting… it will answer once it's ready.";

    /// <summary>Dev mode (docs/dev-mode.md): the sidecar's secret, handed to it at start, which it shows to pass on what
    /// the person said (SojournAssistant dev); null outside dev mode.</summary>
    public string? DevToken { get; init; }
    public bool Dev => DevToken != null;

    /// <summary>A `dev` event: what the person said or picked, for the session driving the client in dev mode.</summary>
    public void DevEvent(JsonObject data) => _s.Events.Log("dev", data);

    /// <summary>The sidecar reads the driving session's reply out loud (when the person has spoken replies on).</summary>
    public void DevSpeak(string text) => Send(new JsonObject { ["what"] = "say", ["text"] = text });

    // --- which model takes requests (the setting lives in VrSettings; SettingsMenu saves it and calls SetModel) ------

    volatile int _model;
    public AssistantModel Model => (AssistantModel)_model;
    /// <summary>A change asked for from outside the menu (the agent API, the console): the settings take it, save it
    /// and call <see cref="SetModel"/>.</summary>
    public Action<AssistantModel>? ChangeModel;
    /// <summary>Brings the headset's panel (or the menu showing in its place) in front of the person.</summary>
    public Action? BringPanel;
    /// <summary>Brings every headset menu back to following in its own place, forgetting where the person put them
    /// (Assistant/MenuPlaces.cs; any thread).</summary>
    public Action? ResetMenus;

    public static string ModelName(AssistantModel m) => m.ToString().ToLowerInvariant();

    public static string Describe(AssistantModel m) => m switch
    {
        AssistantModel.Sonnet => "Sonnet first, Opus for multi-step jobs",
        AssistantModel.Opus => "Opus for everything: slower, surer",
        _ => "quick: Haiku first, Opus for multi-step jobs",
    };

    /// <summary>Tell the sidecar (it switches at once; the next request goes to the new model). announce: say so on the
    /// panel (not from the menu, which has the layer and shows the value itself).</summary>
    public void SetModel(AssistantModel m, bool announce)
    {
        _model = (int)m;
        Send(new JsonObject { ["what"] = "mode", ["mode"] = ModelName(m) });
        if (announce)
            Panel.Update(p =>
            {
                if (!p.Working) p.Heard = p.Result = null;   // the change on its own, not beside the last request's answer
                p.Status = "assistant model: " + Describe(m);
            });
    }

    /// <summary>Each model's effort (the setting in VrSettings, which SettingsMenu saves): the sidecar brings a changed
    /// model's session back up at it for its next turn. The hello answer carries them too.</summary>
    public void EffortsChanged() => Send(new JsonObject { ["what"] = "effort", ["effort"] = Settings.AssistantEffort.ToJson() });

    // --- the person's choices about the assistant itself: a name, a character, spoken replies --------------------
    // The settings live in VrSettings (SettingsMenu owns, saves and wires them here). The sidecar gets them with every
    // hello and at once when they change; the assistant can change the name, character and speech only when asked.

    public VrSettings Settings { get; set; } = new();
    /// <summary>Save the settings and redraw the menu (set by SettingsMenu).</summary>
    public Action? SettingsChanged;

    /// <summary>A new look for the panel (the agent API's look op): the settings take it, save it and apply it.</summary>
    public Action<PanelLook>? ChangeLook;

    /// <summary>The person's screen reader reads the replies (Access/AccessWiring.cs): the assistant's own voice keeps
    /// quiet meanwhile, so there's one voice.</summary>
    public Func<bool>? ReaderSpeaksReplies { get; set; }

    public JsonObject PrefsJson() => new()
    {
        ["name"] = Settings.UseAssistantName ? Settings.AssistantName : null,
        ["character"] = Settings.UseAssistantCharacter ? Settings.AssistantCharacter : null,
        ["speak"] = Settings.SpokenReplies && ReaderSpeaksReplies?.Invoke() != true,
        ["voice"] = Settings.AssistantVoice,
        ["memory"] = Settings.AssistantMemory,
        ["hearing"] = Settings.Hearing,
        // whose models answer (never a key: the sidecar reads that itself; the stamp says when it last changed)
        ["provider"] = Settings.AssistantProvider,
        ["openrouter_model"] = Settings.OpenRouterModel,
        ["key_stamp"] = ProviderKeys.Stamp(Settings.AssistantProvider),
    };

    /// <summary>The provider as the sidecar last found it: {kind, state (ready, offline, no_login, no_key, refused,
    /// credits, no_tools, checking), short, words, caveat, model}; null before its first word.</summary>
    public JsonObject? ProviderStatus { get; private set; }
    /// <summary>The provider's state changed (the settings menu redraws its row).</summary>
    public Action? ProviderStatusChanged;

    /// <summary>The person pressed "paste key" (the settings menu) or kept one in Sojourn Access: the key on the
    /// clipboard is kept, the provider switched to its kind, the sidecar told. Returns what to say (never the key).</summary>
    public string PasteKey()
    {
        var (kind, said) = ProviderKeys.FromClipboard();
        if (kind != null) UseKey(kind);
        return said;
    }

    /// <summary>A key was just kept for kind: that provider is used from now on (saved, the sidecar told).</summary>
    public void UseKey(string kind)
    {
        Settings.AssistantProvider = kind;
        ProviderStatus = null;
        SettingsChanged?.Invoke();
        PrefsChanged();
    }

    /// <summary>The assistant's memory: notes it keeps in its own folder (assistant/home.py).</summary>
    public static string MemoryDir => Path.Combine(Sidecar.Root ?? AppContext.BaseDirectory, "local", "assistant", "home");

    public static bool MemoryKept()
    {
        try { return Directory.Exists(MemoryDir) && Directory.EnumerateFiles(MemoryDir, "*", SearchOption.AllDirectories).Any(); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>The person's clear in the menu: every note goes (links inside aren't followed), and the sidecar starts
    /// its sessions again so nothing of it lingers in a conversation.</summary>
    public void ClearMemory()
    {
        try
        {
            if (Directory.Exists(MemoryDir))
                foreach (var e in new DirectoryInfo(MemoryDir).EnumerateFileSystemInfos())
                {
                    if (e is DirectoryInfo d && !d.Attributes.HasFlag(FileAttributes.ReparsePoint)) d.Delete(recursive: true);
                    else e.Delete();
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Send(new JsonObject { ["what"] = "memory_cleared" });
    }

    /// <summary>The settings changed (menu, console or the agent API): tell the sidecar now.</summary>
    public void PrefsChanged()
    {
        var d = PrefsJson();
        d["what"] = "prefs";
        Send(d);
    }

    string? _shownNotice;

    /// <summary>The sidecar checked in (its mic, or why there's none; its provider's state). A notice brings the panel
    /// up once, when it's new (no mic, no provider that works, a key refused).</summary>
    public void SidecarHello(string? mic, string? notice, JsonObject? provider = null)
    {
        _lastHello = _clock.Elapsed.TotalSeconds;
        _mic = mic;
        bool fresh = notice != null && notice != _shownNotice;
        _shownNotice = notice;
        Panel.Update(p =>
        {
            p.Notice = notice;
            if (p.Status == Starting) p.Status = null;
        }, show: fresh);
        if (provider != null && provider.ToJsonString() != ProviderStatus?.ToJsonString())
        {
            ProviderStatus = provider;
            ProviderStatusChanged?.Invoke();
        }
    }

    // private, what the pointer is on goes as a blind handle (Agents/PrivateMode.cs)
    void Send(JsonObject data) => _s.Events.Log("sojourn", _s.Private.BlindEvent(data));

    JsonNode? TargetJson() => Pointer.TargetJson();

    // --- asked to point (the assistant's point tool): the person points and pulls the trigger, or clicks -------------

    /// <summary>The assistant asked the person to point at what they mean, and waits.</summary>
    public bool PointAsked => Panel.Read(p => p.PointAsk != null);

    /// <summary>The person pointed (the trigger, or a click in the world) while asked: the pick goes back to the
    /// assistant; nothing (null) answers that there was nothing there.</summary>
    public void Pointed(PointerTarget? target)
    {
        if (!PointAsked) return;
        Panel.Update(p => { p.PointAsk = null; p.Status = target is { } t ? t.Label : null; });
        Send(new JsonObject { ["what"] = "pointed", ["target"] = target?.ToJson() });
    }

    /// <summary>The person put the question away (its close key).</summary>
    void PointCancelled()
    {
        Panel.Update(p => p.PointAsk = null);
        Send(new JsonObject { ["what"] = "pointed", ["cancelled"] = true });
    }

    // --- asked to type (the assistant's confirm_text tool): a word to check, the keyboard up with its guess -----------
    // The user's call (2026-10-27): a name only matters when the models have never heard it; then the assistant asks
    // the person to check it by typing, its best guess already in, so usually they just send it. What they send goes
    // back as a `typed_answer` event; speaking instead answers it in the sidecar, which then takes the question away.

    sealed class TypedAsk(string question) { public readonly string Question = question; }
    TypedAsk? _typedAsk;
    readonly System.Collections.Concurrent.ConcurrentQueue<Action> _onMain = new();

    /// <summary>The headset: bring its keyboard up for this target (not a toggle). Main thread.</summary>
    public event Action<Sojourn.Session.Control.IKeyTarget>? KeyboardWanted;

    /// <summary>The agent API's askTyped: the question on the panel, the typing line holding the guess (the caret at its
    /// end), and in the headset the keyboard up for it. Any thread.</summary>
    public void AskTyped(string question, string guess)
    {
        var ask = new TypedAsk(question);
        Volatile.Write(ref _typedAsk, ask);
        Panel.Update(p => { p.TypeAsk = question; p.Typing = guess; p.Status = null; p.PointAsk = null; p.Prompt = null; p.Choices.Clear(); });
        _onMain.Enqueue(() => { if (Volatile.Read(ref _typedAsk) == ask) KeyboardWanted?.Invoke(new AskKeys(this, ask)); });
    }

    /// <summary>The question taken away (answered by voice, timed out, the request stopped): the keyboard goes with it
    /// (its target is no longer alive). Any thread.</summary>
    public void ClearTypedAsk()
    {
        if (Interlocked.Exchange(ref _typedAsk, null) == null) return;
        Panel.Update(p => { p.TypeAsk = null; p.Typing = null; }, show: false, news: false);
    }

    /// <summary>Sent (Enter, ↵) or dismissed (Esc, the keyboard's or panel's ×; sending nothing counts as that).</summary>
    void AnswerTyped(string? text)
    {
        if (Interlocked.Exchange(ref _typedAsk, null) == null) return;
        bool sent = !string.IsNullOrEmpty(text);
        Panel.Update(p => p.TypeAsk = p.Typing = null);   // back to the request it was asked for, worked on
        Send(sent ? new JsonObject { ["what"] = "typed_answer", ["text"] = text } : new JsonObject { ["what"] = "typed_answer", ["cancelled"] = true });
    }

    bool TypedAsked => Volatile.Read(ref _typedAsk) != null;

    sealed class AskKeys(AssistantHost a, TypedAsk ask) : Sojourn.Session.Control.IKeyTarget
    {
        public string Label => ask.Question;
        public bool Alive => Volatile.Read(ref a._typedAsk) == ask;
        public object Owner => ask;
        public void Opened() { }
        public void Char(char ch) { if (Alive) a.Type(ch); }
        public void Backspace() { if (Alive) a.Backspace(); }
        public bool Enter() { if (Alive) a.SubmitTyping(); return true; }
        public void Closed() { if (Alive) a.AnswerTyped(null); }
    }

    // --- talking -------------------------------------------------------------------------------------------------

    JsonNode? _talkTarget;

    public void TalkDown()
    {
        if (Talking) return;
        Talking = true;
        _talkTarget = TargetJson();
        Panel.Update(p => { p.Listening = true; p.Hint = Hint; });
        Send(new JsonObject { ["what"] = "talk_down", ["target"] = _talkTarget?.DeepClone() });
    }

    /// <summary>The talk became dictation (both grips, Private/Dictation.cs): no talk_up, so nothing heard reaches the
    /// assistant.</summary>
    public void DropTalk()
    {
        if (!Talking) return;
        Talking = false;
        Panel.Update(p => p.Listening = false, show: false, news: false);
    }

    /// <summary>A dictated draft's key (Panel.DraftKey), and whether a hand pressed it (not a posted click).</summary>
    public Action<int, bool>? DraftPressed { get; set; }

    public void TalkUp()
    {
        if (!Talking) return;
        Talking = false;
        var target = TargetJson() ?? _talkTarget?.DeepClone();
        bool alive = SidecarAlive, starting = !alive && SidecarStarting;
        Panel.Update(p =>
        {
            p.Listening = false;
            p.Hearing = alive && _mic != null;
            if (starting) p.Status = Starting;
            else if (!alive) p.Notice = "The assistant isn't running (python tools/assistant.py check).";
        });
        Send(new JsonObject { ["what"] = "talk_up", ["target"] = target, ["first_target"] = _talkTarget?.DeepClone() });
    }

    // --- typing (desktop, or the mirror window's keyboard; in the headset, its keyboard) --------------------------------

    /// <summary>The panel's keyboard key was pressed (the headset): bring up its keyboard for a typed request.</summary>
    public event Action? KeyboardRequested;

    /// <summary>The panel's stack key: the session's replies, in the reader.</summary>
    public event Action? RepliesRequested;

    /// <summary>The session's replies as the reader holds them.</summary>
    public ReaderDoc RepliesDoc() =>
        new(ReaderKind.Replies, "", Panel.Replies.Select(r => new ReaderPart(r.Asked, r.Text)).ToList());

    /// <summary>What the headset's keyboard types into for a request: the panel's typing line, sent by Enter.</summary>
    public Sojourn.Session.Control.IKeyTarget KeyTarget() => new RequestKeys(this);

    sealed class RequestKeys(AssistantHost a) : Sojourn.Session.Control.IKeyTarget
    {
        bool _sent;
        public string Label => "a request to the assistant";
        public bool Alive => true;
        public object Owner => a;
        public void Opened() => a.StartTyping();
        public void Char(char ch) => a.Type(ch);
        public void Backspace() => a.Backspace();
        public bool Enter() { _sent = true; a.SubmitTyping(); return true; }
        public void Closed() { if (!_sent && !a.TypedAsked) a.CancelTyping(); }   // not when a question to type took its place
    }

    public void StartTyping() => Panel.Update(p => p.Typing ??= "");
    public void CancelTyping()
    {
        if (TypedAsked) { AnswerTyped(null); return; }
        Panel.Update(p => p.Typing = null);
    }
    public void Type(char ch)
    {
        if (char.IsControl(ch)) return;
        Panel.Update(p => { if (p.Typing != null && p.Typing.Length < 400) p.Typing += ch; });
    }
    public void Backspace() => Panel.Update(p => { if (p.Typing is { Length: > 0 } t) p.Typing = t[..^1]; });

    public void SubmitTyping()
    {
        var text = Panel.Read(p => p.Typing)?.Trim();
        if (TypedAsked) { AnswerTyped(text); return; }
        Panel.Update(p => p.Typing = null);
        if (string.IsNullOrEmpty(text)) return;
        Request(text);
    }

    /// <summary>A request in words (typed, or from the console's /ask).</summary>
    public void Request(string text)
    {
        Panel.Update(p =>
        {
            p.Heard = text;
            p.Status = SidecarAlive ? null : SidecarStarting ? Starting : "the assistant isn't running";
            p.Result = null;
        });
        Send(new JsonObject { ["what"] = "typed", ["text"] = text, ["target"] = TargetJson() });
    }

    // --- pressing keys on the panel ------------------------------------------------------------------------------

    // what the press went down on, as drawn then: a release acts only on that same thing
    (PanelKey Key, PendingConfirm? Confirm, PanelChoice? Choice, long At, bool Real)? _down;

    /// <summary>The panel key at a point (logical panel coordinates) of the raster on screen this frame, or -1.</summary>
    public int KeyAt(Vector2 logical)
    {
        if (_shown?.Raster is not { } r) return -1;
        for (int i = 0; i < r.Keys.Count; i++)
            if (r.Keys[i].Rect.Contains(logical.X, logical.Y)) return i;
        return -1;
    }

    public void Hover(int key) => Panel.SetHover(key);

    Panel.Raster? _turnedOn;

    /// <summary>The card on screen pages (a long answer's ‹ › keys are on it).</summary>
    public bool Paged => _shown?.Raster is { } r && r.Keys.Any(k => k.Kind == KeyKind.Page);

    /// <summary>The right stick's flick (Control/SheetStick): the page its ‹ or › key would turn to, where that key is on
    /// the card now; once per drawing, so a quick second flick can't run past the last page before the card is redrawn.</summary>
    public bool Turn(int dir)
    {
        if (_shown?.Raster is not { } r || ReferenceEquals(r, _turnedOn)) return false;
        if (!r.Keys.Any(k => k.Kind == KeyKind.Page && k.Index == Math.Sign(dir))) return false;
        _turnedOn = r;
        Panel.Turn(dir);
        return true;
    }

    /// <summary>For scripted tests: where a key of the raster on screen is, as fractions of the panel (0-1 across and down).</summary>
    public Vector2? KeyFraction(KeyKind kind)
    {
        if (_shown?.Raster is not { } r) return null;
        foreach (var k in r.Keys)
            if (k.Kind == kind) return new Vector2(k.Rect.MidX / r.LogicalWidth, k.Rect.MidY / r.LogicalHeight);
        return null;
    }

    /// <summary>Press starts on a key; release on the same key acts (a slip off it, or the panel changing what's
    /// under it, cancels). Confirm takes only a press that started once it had been on screen for a moment.</summary>
    public void PressDown(int key)
    {
        _down = null;
        if (_shown?.Raster is not { } r || key < 0 || key >= r.Keys.Count) return;
        var k = r.Keys[key];
        _down = (k, r.Confirm, k.Kind == KeyKind.Choice && k.Index < r.Choices.Count ? r.Choices[k.Index] : null, Environment.TickCount64,
            InHeadset || RealInput.LeftButton());
    }

    public void PressUp(int key)
    {
        var down = _down;
        _down = null;
        if (down is not { } d || _shown?.Raster is not { } r || key < 0 || key >= r.Keys.Count) return;
        var k = r.Keys[key];
        if (k.Kind != d.Key.Kind || k.Index != d.Key.Index) return;
        switch (k.Kind)
        {
            case KeyKind.Hide:
                if (r.PointAsk) PointCancelled(); else if (TypedAsked) AnswerTyped(null); else Panel.Hide();
                break;
            case KeyKind.Page:
                Panel.Turn(k.Index);
                break;
            case KeyKind.Help:
                Panel.ToggleHelp();
                break;
            case KeyKind.Keyboard:
                KeyboardRequested?.Invoke();
                break;
            case KeyKind.Replies:
                RepliesRequested?.Invoke();
                break;
            case KeyKind.Draft when r.Confirm is null:
                DraftPressed?.Invoke(k.Index, d.Real);
                break;
            case KeyKind.Confirm or KeyKind.Cancel:
                if (r.Confirm is not { } pc || pc != d.Confirm) return;   // another confirmation came up under the press
                if (k.Kind == KeyKind.Confirm && !pc.ArmedAt(d.At)) return;
                // only a hand on the mouse confirms: not clicks posted or injected by a program (the assistant has a shell)
                if (k.Kind == KeyKind.Confirm && !d.Real) return;
                Confirmations.Answer(pc, k.Kind == KeyKind.Confirm);
                break;
            case KeyKind.Choice:
                if (r.Confirm is null && k.Index < r.Choices.Count && r.Choices[k.Index] == d.Choice && Panel.Read(p => p.Choices.Contains(d.Choice!)))
                    Pick(d.Choice!);
                break;
        }
    }

    /// <summary>Number keys on the desktop pick a choice (not while a confirmation is up: the choices are hidden).</summary>
    public void PickNumber(int n)
    {
        var (choices, confirming) = Panel.Read(p => (p.Choices.ToList(), p.Confirm != null));
        if (!confirming && n >= 1 && n <= choices.Count) Pick(choices[n - 1]);
    }

    /// <summary>Choices the client answers itself (voice chat's ringing call): true if it took the press.</summary>
    public Func<PanelChoice, bool>? LocalPick { get; set; }

    void Pick(PanelChoice c)
    {
        if (LocalPick?.Invoke(c) == true) { Panel.Update(p => { p.Choices.Clear(); p.Prompt = null; }); return; }
        Panel.Update(p => { p.Status = $"picked {c.Label}"; p.Choices.Clear(); p.Prompt = null; });
        Send(new JsonObject { ["what"] = "pick", ["id"] = c.Id, ["label"] = c.Label });
    }

    // --- every frame -----------------------------------------------------------------------------------------------

    float _scale = 1;
    bool _drawing;
    (long Version, bool Armed, int Phase, bool Visible, int Ink) _drawnKey = (-1, false, 0, false, -1);
    sealed record Drawn(OverlayImage Image, Panel.Raster Raster);
    volatile Drawn? _latest;   // written by the draw task, whole
    Drawn? _shown;             // what Frame handed out this frame (main thread): keys and presses go by it
    long _imageVersion;
    string? _pointing;

    /// <summary>The latest raster of the panel, redrawn in the background when it changes; null while hidden. Keys
    /// (KeyAt, presses) refer to the raster returned here until the next call.</summary>
    public (OverlayImage Image, Panel.Raster Raster)? Frame(float scale)
    {
        while (_onMain.TryDequeue(out var a)) a();
        var pointing = Pointer.Current?.Label;
        if (pointing != _pointing)
        {
            _pointing = pointing;
            Panel.Update(p => p.Pointing = pointing, show: false, news: false);
        }
        bool visible = Panel.Visible;
        bool armed = Panel.Read(p => p.Confirm)?.ArmedAt(Environment.TickCount64) ?? false;   // the confirm key lights up
        var key = (Panel.Version, armed, Panel.Phase, visible, Panel.Adaptive?.StepOf(InkSheet.Panel) ?? -1);
        if (visible && !_drawing && (key != _drawnKey || scale != _scale))
        {
            _drawing = true;
            _drawnKey = key;
            _scale = scale;
            Task.Run(() =>
            {
                try
                {
                    var r = Panel.Draw(scale);
                    var before = _latest?.Raster;
                    _latest = new Drawn(new OverlayImage(r.Rgba, r.Width, r.Height, Interlocked.Increment(ref _imageVersion), r.Frost, r.Edge), r);
                    if (r.Confirm is { } pc)
                    {
                        pc.MarkShown();   // on screen from the next frame
                        // the same confirmation laid out differently: its keys moved, so it arms afresh
                        if (before?.Confirm == pc && (before.Width != r.Width || before.Height != r.Height || ConfirmKey(before) != ConfirmKey(r)))
                            pc.MarkMoved();
                    }
                }
                finally { _drawing = false; }
            });
        }
        _shown = visible ? _latest : null;
        return _shown is { } d ? (d.Image, d.Raster) : null;
    }

    static SkiaSharp.SKRect ConfirmKey(Panel.Raster r) => r.Keys.FirstOrDefault(k => k.Kind == KeyKind.Confirm).Rect;
}
