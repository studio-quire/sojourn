using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sojourn.Core;
using Sojourn.Core.Leap;
using Sojourn.Render.Media;

namespace Sojourn.Session.Media;

/// <summary>Media sound's half of sojourn_voice.dll (world.cpp): a surface's sound ring mixed as one of 16 streams.</summary>
static unsafe partial class MediaNative
{
    const string Lib = "sojourn_voice";
    public const int Streams = 16;
    [LibraryImport(Lib)] public static partial int sv_media_attach(int stream, byte* section);
    [LibraryImport(Lib)] public static partial void sv_media_detach(int stream);
    [LibraryImport(Lib)] public static partial void sv_media_set(int stream, float gain, int spatial, double lx, double ly, double lz, double rx, double ry, double rz);
    [LibraryImport(Lib)] private static partial int sv_media_stats(byte* buf, int cap);

    public static string Stats()
    {
        var buf = new byte[4096];
        int n;
        fixed (byte* p = buf) n = sv_media_stats(p, buf.Length);
        return System.Text.Encoding.UTF8.GetString(buf, 0, Math.Clamp(n - 1, 0, buf.Length - 1));
    }
}

/// <summary>
/// One web page or video playing on a helper (MediaHelper) into one surface (MediaSurface: frames and sound): the
/// commands it takes and what the helper last said about it. Thread-safe: commands go straight to the pipe; events come
/// in from the manager (<see cref="Handle"/>).
/// Written by Claude Opus 5.5, 2026-10-26.
/// </summary>
public sealed class MediaPlayer : IDisposable
{
    static int _next;
    public int Id { get; } = Interlocked.Increment(ref _next);
    public MediaKind Kind { get; }
    public MediaSurface Surface { get; }
    readonly MediaHelper _helper;

    /// <summary>Where it is now (the page's address, or the stream's URL).</summary>
    public string Url { get; private set; }
    public string? Title { get; private set; }
    /// <summary>web: loading, loaded, error; video: opening, buffering, playing, paused, stopped, ended, error.</summary>
    public string State { get; private set; } = "opening";
    public string? Error { get; private set; }
    public bool Loading { get; private set; } = true;
    public bool CanBack { get; private set; }
    public bool CanForward { get; private set; }
    public double Ms { get; private set; }
    public double LengthMs { get; private set; }
    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }
    /// <summary>The page has a text field focused (CEF's keyboard request): typing goes to it.</summary>
    /// <summary>A text field has the page's focus: typing goes to it. Chromium's own report (OnVirtualKeyboardRequested)
    /// comes only for touch, not the mouse events we send, so the page is also asked (document.activeElement) after
    /// each click and each key that moves focus.</summary>
    public bool WantsKeys => _imeWanted || _fieldFocused;
    bool _imeWanted, _fieldFocused;
    int _focusSeq;

    // an editable element focused (an iframe counts: what's inside another site's frame can't be seen from here)
    const string FocusJs = "(() => { const e = document.activeElement; if (!e || e === document.body) return false; " +
        "if (e.isContentEditable || e.tagName === 'TEXTAREA' || e.tagName === 'IFRAME') return true; " +
        "if (e.tagName !== 'INPUT') return false; " +
        "return !['button','submit','reset','checkbox','radio','range','color','file','image','hidden'].includes((e.type || 'text').toLowerCase()); })()";

    /// <summary>Asks the page whether a text field has its focus now (a moment after a click lands).</summary>
    public async void RefreshFocus(int delayMs = 60)
    {
        if (Kind != MediaKind.Web || Closed) return;
        int seq = Interlocked.Increment(ref _focusSeq);
        try
        {
            await Task.Delay(delayMs);
            var ev = await EvalAsync(FocusJs, TimeSpan.FromSeconds(2));
            var body = ev.Str("result") is { } r ? JsonNode.Parse(r) as JsonObject : null;
            if (seq == _focusSeq && ev.Bool("ok", false) && body?["result"]?["value"] is JsonValue v && v.TryGetValue<bool>(out var on))
                _fieldFocused = on;
        }
        catch { }
    }
    public bool Fullscreen { get; private set; }
    public bool Closed { get; private set; }
    /// <summary>The last navigation the helper refused, and why (scheme, private address, whitelist, popup, slurl...).</summary>
    public (string Url, string Why, DateTime At)? Refused { get; private set; }
    public DateTime OpenedAt { get; } = DateTime.UtcNow;

    // what was last asked of it, so a change is sent once
    public int Fps { get; private set; } = 30;
    public bool Hidden { get; private set; }
    public float Scale { get; private set; } = 1;
    public int Stream = -1;

    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _evals = new();
    static int _evalSeq;

    public MediaPlayer(MediaKind kind, MediaHelper helper, MediaSurface surface, string url)
    {
        Kind = kind;
        _helper = helper;
        Surface = surface;
        Url = url;
    }

    void Send(string op, JsonObject? args = null)
    {
        if (Closed) return;
        var c = args ?? new JsonObject();
        c["op"] = op;
        c["id"] = Id;
        _helper.Send(c);
    }

    public void OpenWeb(string url, int w, int h, float scale, int fps, IReadOnlyList<string>? allow, bool audio)
    {
        (Scale, Fps) = (scale, fps);
        var a = new JsonObject { ["shm"] = Surface.Name, ["url"] = url, ["w"] = w, ["h"] = h, ["scale"] = scale, ["fps"] = fps, ["audio"] = audio };
        if (allow is { Count: > 0 }) a["allow"] = new JsonArray(allow.Select(x => (JsonNode)x).ToArray());
        Send("open", a);
    }

    public void OpenVideo(string url, bool loop, bool audio) =>
        Send("open", new JsonObject { ["shm"] = Surface.Name, ["url"] = url, ["maxw"] = (int)Surface.Section.MaxWidth, ["maxh"] = (int)Surface.Section.MaxHeight,
            ["loop"] = loop, ["audio"] = audio });

    // ---- pages ----
    public void Navigate(string url) => Send("navigate", new JsonObject { ["url"] = url });
    public void Back() => Send("back");
    public void Forward() => Send("forward");
    public void Reload() => Send("reload");
    public void StopLoading() => Send("stop");
    public void SetAllow(IReadOnlyList<string>? allow) => Send("allow", new JsonObject { ["allow"] = new JsonArray((allow ?? []).Select(x => (JsonNode)x).ToArray()) });

    /// <summary>kind: down, up, move, leave; x, y in the page's own pixels.</summary>
    public void Mouse(string kind, float x, float y, int button = 0, int clicks = 1, bool held = false)
    {
        Send("mouse", new JsonObject { ["kind"] = kind, ["x"] = (int)MathF.Round(x), ["y"] = (int)MathF.Round(y), ["button"] = button, ["clicks"] = clicks, ["held"] = held });
        if (kind == "up") RefreshFocus();
    }

    public void Wheel(float x, float y, int dx, int dy) =>
        Send("mouse", new JsonObject { ["kind"] = "wheel", ["x"] = (int)x, ["y"] = (int)y, ["dx"] = dx, ["dy"] = dy });

    /// <summary>kind: down, up, char; code a Windows virtual key; mods 1 shift, 2 control, 4 alt.</summary>
    public void Key(string kind, int code, int ch = 0, int mods = 0)
    {
        Send("key", new JsonObject { ["kind"] = kind, ["code"] = code, ["char"] = ch, ["mods"] = mods });
        if (kind == "up" && code is 9 or 13 or 27) RefreshFocus();   // Tab, Enter (a form sent) and Esc move focus
    }

    public void Text(string text) => Send("text", new JsonObject { ["text"] = text });
    public void Focus(bool on) => Send("focus", new JsonObject { ["on"] = on });

    /// <summary>Runs JavaScript in the page (DevTools Runtime.evaluate, by value, awaiting promises): its result object.</summary>
    public async Task<JsonObject> EvalAsync(string js, TimeSpan timeout, CancellationToken ct = default)
    {
        if (Kind != MediaKind.Web) throw new LeapError("not a web page");
        int req = Interlocked.Increment(ref _evalSeq);
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _evals[req] = tcs;
        Send("eval", new JsonObject { ["req"] = req, ["js"] = js });
        try { return await tcs.Task.WaitAsync(timeout, ct); }
        catch (TimeoutException) { throw new LeapError("the page didn't answer in time"); }
        finally { _evals.TryRemove(req, out _); }
    }

    // ---- video ----
    public void Play() => Send("play");
    public void Pause() => Send("pause");
    public void StopVideo() => Send("stop");
    public void Seek(double ms) => Send("seek", new JsonObject { ["ms"] = Math.Max(0, ms) });

    // ---- both ----
    public void SetFps(int fps)
    {
        if (fps == Fps || Kind != MediaKind.Web) return;
        Fps = fps;
        Send("fps", new JsonObject { ["fps"] = fps });
    }

    public void SetHidden(bool hidden)
    {
        if (hidden == Hidden) return;
        Hidden = hidden;
        if (Kind == MediaKind.Web) Send("hidden", new JsonObject { ["hidden"] = hidden });
    }

    public void SetSize(int w, int h, float scale)
    {
        Scale = scale;
        if (Kind == MediaKind.Web) Send("size", new JsonObject { ["w"] = w, ["h"] = h, ["scale"] = scale });
    }

    public void SetAudio(bool on) => Send("audio", new JsonObject { ["on"] = on });

    /// <summary>An event from the helper about this player.</summary>
    public void Handle(JsonObject ev)
    {
        switch ((string?)ev["ev"])
        {
            case "loading":
                Loading = ev.Bool("loading", false);
                CanBack = ev.Bool("back", false);
                CanForward = ev.Bool("fwd", false);
                if (Loading) { State = "loading"; _imeWanted = _fieldFocused = false; }   // a new document has no field focused
                else if (State == "loading") State = "loaded";
                break;
            case "loaded":
                if (ev.Str("url") is { Length: > 0 } lu) Url = lu;
                State = "loaded";
                Error = null;
                break;
            case "url":
                if (ev.Str("url") is { Length: > 0 } u) Url = u;
                break;
            case "title":
                Title = ev.Str("title");
                break;
            case "error":
                Error = ev.Str("text") ?? "error";
                State = "error";
                break;
            case "blocked" or "popup" or "slurl":
                Refused = (ev.Str("url") ?? "", (string)ev["ev"]! == "blocked" ? ev.Str("why") ?? "refused" : (string)ev["ev"]!, DateTime.UtcNow);
                break;
            case "keyboard":
                _imeWanted = ev.Int("mode", 0) != 0;
                break;
            case "fullscreen":
                Fullscreen = ev.Bool("on", false);
                break;
            case "crashed":
                Error = $"the page's process ended ({ev.Str("text")})";
                State = "error";
                break;
            case "state":
                State = ev.Str("state") ?? State;
                Ms = ev.Num("ms") ?? Ms;
                LengthMs = ev.Num("length") ?? LengthMs;
                VideoWidth = ev.Int("w", VideoWidth);
                VideoHeight = ev.Int("h", VideoHeight);
                if (ev.Str("title") is { Length: > 0 } t) Title = t;
                Loading = State is "opening" or "buffering";
                break;
            case "eval":
                if (_evals.TryRemove(ev.Int("req", 0), out var tcs)) tcs.TrySetResult(ev);
                break;
            case "closed":
                Closed = true;
                break;
        }
    }

    /// <summary>Tells the helper to close it. The surface is the manager's to remove.</summary>
    public void Close()
    {
        if (Closed) return;
        Send("close");
        Closed = true;
        foreach (var e in _evals.Values) e.TrySetException(new LeapError("the media closed"));
    }

    /// <summary>The helper went away: nothing more will come.</summary>
    public void Lost()
    {
        Closed = true;
        State = "error";
        Error ??= "the media helper ended";
        foreach (var e in _evals.Values) e.TrySetException(new LeapError("the media helper ended"));
    }

    public void Dispose() => Close();
}
