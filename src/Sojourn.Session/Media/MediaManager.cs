using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Render.Media;
using NVector3 = System.Numerics.Vector3;
using LMediaEntry = LibreMetaverse.MediaEntry;

namespace Sojourn.Session.Media;

/// <summary>What plays by itself (docs/media.md > Defaults): the person's own (their objects, HUDs and land), everything
/// that asks to (the viewer's), or nothing until started.</summary>
public enum MediaAutoPlay { Mine, On, Off }

public enum MediaItemKind { Prim, Parcel, Page }

/// <summary>Where a media face is in its region (Sojourn.Render's MediaFaceFrame, kept here for the manager's thread).</summary>
public readonly record struct FaceFrame(NVector3 Centre, NVector3 Normal, NVector3 Right, NVector3 Up, float Width, float Height);

/// <summary>
/// One piece of media the client knows: a face's (prim media), the parcel's, or a page an object offered (llLoadURL)
/// that the person opened. What it is (from the region), what the person did with it, and its player while loaded.
/// Guarded by the manager's lock.
/// </summary>
public sealed class MediaItem
{
    // (Page: the standalone page up close, an offered one or a window a page opened; Key "page", one at a time)
    public required string Key { get; init; }
    public required MediaItemKind Kind { get; init; }
    public ulong Region { get; init; }
    public uint LocalId { get; init; }
    public UUID PrimId { get; init; }
    public int Face { get; init; } = -1;
    /// <summary>The parcel media's texture (faces showing it show the media).</summary>
    public UUID Texture;

    // from the region
    public string Url = "";        // the entry's current URL (prim media: its current_url; parcel: its media URL)
    public string Home = "";
    public bool AutoPlayFlag, Loop, AutoScale, AutoZoom, FirstClickInteract;
    public int Width, Height;      // the media's size (0: the default)
    public MediaPermission Interact = MediaPermission.Anyone, Control = MediaPermission.Anyone;
    public string[]? Whitelist;
    public string? Mime;           // parcel media's type; prim media's after the probe
    public string? Desc;

    // the object's place and owner, refreshed by the scan
    public UUID Owner;
    public UUID Group;
    public bool Mine, OnHud, OnOthers;
    public NVector3 RegionPos;
    public float Size = 1;          // the prim's largest side, for the interest estimate
    public FaceFrame? Frame;
    public DateTime FrameAt;
    public string? ObjectName;

    // what the person did
    public bool Started;            // started by the person or by the assistant for them
    public bool Stopped;            // stopped by them: stays off until started again
    public float Volume = 1;        // theirs for this one
    public long FocusUntil;         // being used (pressed, zoomed): ranks first and stays sharp until then (ms ticks)
    public bool Zoomed;

    // playing
    public MediaPlayer? Player;
    public MediaKind PlayerKind;
    public string? PlayingUrl;      // what the player was opened or last sent to
    public DateTime NotRankedSince;
    public Task<(MediaKind Kind, string? Mime)>? Probe;
    public string? ProbedUrl;
    public MediaKind ProbedKind;
    public string? Refusal;         // why it can't play (a URL the rules refuse, the probe failed)
    public string Tier = "unloaded";
    public float Interest, Distance = float.PositiveInfinity;
    public float WantScale = 1;
    public DateTime WantScaleSince;
    public bool UserNav;            // the person navigated: the next address change is broadcast (prim media)
    public DateTime UserNavAt;
    public string? ServerNavUrl;    // a navigation the region asked for: not broadcast
    public WebPageOffer? Offer;
    /// <summary>A page's new window (a popup): the item whose page opened it, and whether that one was the person's own.</summary>
    public string? OpenedFrom;
    public bool OpenerMine;

    public bool Playing => Player is { Closed: false };
}

/// <summary>
/// The client's media (docs/media.md): prim media from the regions' ObjectMedia capability, the agent parcel's media,
/// and pages objects offered; which of them load and play (the person's auto-play choice, the viewer's rules for
/// whose media shows), up to eight loaded at once ranked as the viewer ranks them, each one's frame rate, texture size
/// and sound by how much of it is seen and how near it is. Runs on its own thread ("media", ten times a second); input
/// and the agent API call in under the same lock. Players live on the helpers (MediaHelper: CEF pages in Chromium's
/// sandbox, libVLC video at low integrity); frames reach faces through the renderer's MediaSurfaces, sound goes into
/// world audio's mix (so voice's echo canceller hears it).
/// Written by Claude Opus 5.5, 2026-10-26.
/// </summary>
public sealed partial class MediaManager : IDisposable
{
    public const int MaxLoaded = 8;           // PluginInstancesTotal
    const int MaxNormal = 2, MaxLow = 4;      // PluginInstancesNormal, PluginInstancesLow
    const float RollOffMin = 40, RollOffMax = 80, RollOffRate = 0.02f, MediaLevel = 0.3f;   // MediaRollOff*, AudioLevelMedia

    readonly AgentSession _s;
    readonly MediaSurfaces _surfaces;
    readonly Action<string> _log;
    readonly bool _privateOk;
    readonly string _runtime, _profile;
    readonly object _mu = new();
    readonly Dictionary<string, MediaItem> _items = new();
    readonly Dictionary<int, MediaItem> _byPlayer = new();
    readonly ConcurrentQueue<Action> _queue = new();
    readonly Thread _thread;
    volatile bool _stop;
    readonly AutoResetEvent _wake = new(false);
    MediaHelper? _web, _video;
    readonly bool[] _streams = new bool[MediaNative.Streams];

    /// <summary>The person's choices (VrSettings): what plays by itself, the media volume (1: the viewer's level), and
    /// whether a face's sound comes from where it is.</summary>
    public volatile MediaAutoPlay Mode = MediaAutoPlay.Mine;
    public volatile float VolumeSetting = 1;
    public volatile bool Placed = true;
    /// <summary>Media on: off unloads everything and nothing loads (the viewer's AudioStreamingMedia).</summary>
    public volatile bool Enabled = true;
    /// <summary>Sound can be mixed (world audio or voice opened the engine).</summary>
    public bool SoundOn { get; set; }

    // where the person's ears are (global metres) and which way they face, from the render loop
    (double X, double Y, double Z) _ear;
    NVector3 _earForward = NVector3.UnitX;
    volatile bool _haveEar;
    public Func<float> DrawDistance { get; set; } = () => 128;

    public event Action? Changed;

    public MediaManager(AgentSession s, MediaSurfaces surfaces, string runtime, string profile, bool privateOk, Action<string> log)
    {
        _s = s;
        _surfaces = surfaces;
        _runtime = runtime;
        _profile = profile;
        _privateOk = privateOk;
        _log = log;
        _thread = new Thread(Run) { Name = "media", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public bool PrivateOk => _privateOk;

    /// <summary>The ears this frame (the render loop: the camera on the desktop, the head in the headset).</summary>
    public void Listen(ulong region, NVector3 pos, NVector3 forward)
    {
        Utils.LongToUInts(region, out uint rx, out uint ry);
        _ear = (rx + (double)pos.X, ry + (double)pos.Y, pos.Z);
        _earForward = forward;
        _haveEar = true;
    }

    /// <summary>Runs on the manager's thread, in order.</summary>
    public void Post(Action a)
    {
        _queue.Enqueue(a);
        _wake.Set();
    }

    /// <summary>Under the manager's lock (input, the agent API).</summary>
    public T Locked<T>(Func<T> f)
    {
        lock (_mu) return f();
    }

    public void Locked(Action f)
    {
        lock (_mu) f();
    }

    public IReadOnlyCollection<MediaItem> ItemsUnlocked => _items.Values;

    public MediaItem? Item(string key) => _items.TryGetValue(key, out var i) ? i : null;

    public static string PrimKey(ulong region, uint localId, int face) => $"prim:{region}:{localId}:{face}";

    // ---- the thread --------------------------------------------------------------------------------------------------

    long _lastSchedule, _lastScan, _lastParcel, _lastSound;

    void Run()
    {
        while (!_stop)
        {
            _wake.WaitOne(50);
            if (_stop) break;
            List<Found>? found = null;
            if (Environment.TickCount64 - _lastScan >= 1000)
            {
                _lastScan = Environment.TickCount64;
                try { found = Gather(); }
                catch (Exception e) { _log($"media: scan: {e.GetType().Name}: {e.Message}"); }
            }
            lock (_mu)
            {
                try
                {
                    while (_queue.TryDequeue(out var a)) a();
                    long now = Environment.TickCount64;
                    if (found != null) Scan(found);
                    if (now - _lastParcel >= 500) { _lastParcel = now; ParcelTick(); }
                    PumpFetches(now);
                    if (now - _lastSchedule >= 250) { _lastSchedule = now; Schedule(now); }
                    if (now - _lastSound >= 100) { _lastSound = now; Sound(); }
                }
                catch (Exception e) { _log($"media: {e.GetType().Name}: {e.Message}"); }
            }
        }
    }

    void Notify()
    {
        try { Changed?.Invoke(); } catch { }
    }

    // ---- what plays ----------------------------------------------------------------------------------------------------

    /// <summary>Why this item may not show at all (the viewer's rules), or null.</summary>
    string? NotShown(MediaItem i)
    {
        if (!Enabled) return "media is off";
        if (i.Kind == MediaItemKind.Page) return null;
        if (i.Kind == MediaItemKind.Prim)
        {
            // media on other people's attachments isn't shown (MediaShowOnOthers, off by default)
            if (i.OnOthers) return "on someone else's attachment";
            if (_s.Mutes.IsMuted(i.PrimId) || (i.Owner != UUID.Zero && _s.Mutes.IsMuted(i.Owner))) return "blocked";
            // a parcel that hides media from outside it (ObscureMedia): only its own shows while you're on it
            if (!i.OnHud && _agentParcel is { ObscureMedia: true } ap && !InAgentParcel(i)) return "this parcel hides media from outside it";
            if (!i.OnHud && i.Distance > DrawDistance() + 16) return "too far";
        }
        if (i.Url.Length == 0) return "no URL";
        return null;
    }

    /// <summary>Plays by itself under the person's auto-play choice.</summary>
    bool AutoPlays(MediaItem i) => Mode switch
    {
        MediaAutoPlay.Off => false,
        MediaAutoPlay.On => i.Kind == MediaItemKind.Parcel || i.AutoPlayFlag || i.OnHud,
        _ => i.Kind == MediaItemKind.Parcel ? _agentParcelMine : i.Mine && (i.AutoPlayFlag || i.OnHud),
    };

    bool Wanted(MediaItem i)
    {
        if (i.Kind == MediaItemKind.Page) return true;
        if (NotShown(i) != null || i.Refusal != null) return false;
        if (i.Stopped) return false;
        return i.Started || AutoPlays(i);
    }

    /// <summary>The person may click into it, type and scroll (interact) or navigate it (control): the face's permissions,
    /// any of owner, group or anyone (the viewer checks them one at a time, so a face set for owner and group refuses its
    /// owner when the owner isn't in the group). Parcel media and offered pages are the person's own copy.</summary>
    public bool May(MediaItem i, bool control)
    {
        if (i.Kind != MediaItemKind.Prim) return true;
        var perm = control ? i.Control : i.Interact;
        if ((perm & MediaPermission.Anyone) != 0) return true;
        if ((perm & MediaPermission.Owner) != 0 && i.Mine) return true;
        if ((perm & MediaPermission.Group) != 0 && i.Group != UUID.Zero && _s.State.Groups.ContainsKey(i.Group)) return true;
        return false;
    }

    float Interest(MediaItem i)
    {
        // measured: the screen area (pixels) of the largest object showing it in the last frame it was drawn
        if (i.Player is { } p && p.Surface.SeenAt != 0 && Stopwatch.GetElapsedTime(p.Surface.SeenAt).TotalSeconds < 1.5)
            return Math.Max(1, p.Surface.Interest);
        if (i.Kind != MediaItemKind.Prim) return 1e6f;
        // not drawn yet: how big it would be, from its size and distance (and a tenth of that behind the view)
        float d = Math.Max(0.5f, i.Distance);
        float est = i.Size * i.Size / (d * d) * 880_000f;
        var to = Global(i) is { } g && _haveEar ? new NVector3((float)(g.X - _ear.X), (float)(g.Y - _ear.Y), (float)(g.Z - _ear.Z)) : NVector3.Zero;
        if (to.LengthSquared() > 1e-4f && NVector3.Dot(NVector3.Normalize(to), _earForward) < 0.2f) est *= 0.1f;
        return est;
    }

    (double X, double Y, double Z)? Global(MediaItem i)
    {
        if (i.Kind != MediaItemKind.Prim) return null;
        Utils.LongToUInts(i.Region, out uint rx, out uint ry);
        var p = i.Frame?.Centre ?? i.RegionPos;
        return (rx + (double)p.X, ry + (double)p.Y, p.Z);
    }

    void Schedule(long now)
    {
        foreach (var i in _items.Values)
        {
            if (i.Kind == MediaItemKind.Prim && _haveEar && Global(i) is { } g)
                i.Distance = (float)Math.Sqrt((g.X - _ear.X) * (g.X - _ear.X) + (g.Y - _ear.Y) * (g.Y - _ear.Y) + (g.Z - _ear.Z) * (g.Z - _ear.Z));
            if (i.OnHud) i.Distance = 0;
            i.Interest = Interest(i);
        }
        var wanted = _items.Values.Where(Wanted)
            .OrderByDescending(i => i.FocusUntil > now || i.Zoomed)
            .ThenByDescending(i => i.Kind == MediaItemKind.Page)
            .ThenByDescending(i => i.Kind == MediaItemKind.Parcel)
            .ThenByDescending(i => i.Interest)
            .ThenBy(i => i.Distance)
            .ToList();
        var ranked = new HashSet<MediaItem>(wanted.Take(MaxLoaded));
        foreach (var i in _items.Values.ToList())
        {
            if (ranked.Contains(i)) { i.NotRankedSince = default; continue; }
            if (i.Player == null) { i.Tier = Wanted(i) ? "waiting for a slot" : "unloaded"; continue; }
            // out of the first eight: unloaded after 3 s (it may come back), at once if it's no longer wanted at all
            if (Wanted(i))
            {
                if (i.NotRankedSince == default) i.NotRankedSince = DateTime.UtcNow;
                if (DateTime.UtcNow - i.NotRankedSince < TimeSpan.FromSeconds(3)) continue;
            }
            Unload(i);
            i.Tier = "unloaded";
        }
        int normal = 0, low = 0;
        foreach (var i in wanted.Take(MaxLoaded))
        {
            if (!EnsurePlaying(i)) continue;
            var p = i.Player!;
            bool focus = i.FocusUntil > now || i.Zoomed || i.Kind == MediaItemKind.Page;
            bool seen = p.Surface.SeenAt != 0 && Stopwatch.GetElapsedTime(p.Surface.SeenAt).TotalSeconds < 1.0;
            bool young = (DateTime.UtcNow - p.OpenedAt).TotalSeconds < 2;
            string tier;
            if (focus) tier = "focus";
            else if (!seen && !young && i.Kind != MediaItemKind.Page) tier = "hidden";
            else
            {
                float content = Math.Max(1, p.Surface.ContentWidth * (float)p.Surface.ContentHeight);
                bool small = i.Interest < content / 4;
                if (normal < MaxNormal && !small) { tier = "normal"; normal++; }
                else if (normal + low < MaxNormal + MaxLow) { tier = "low"; low++; }
                else tier = "slideshow";
            }
            Apply(i, p, tier);
        }
    }

    /// <summary>A tier's frame rate and texture size: focused and normal at full rate, low at half, slideshow a few
    /// frames a second; hidden (not drawn for a second) not painted or uploaded at all, its sound still playing.</summary>
    void Apply(MediaItem i, MediaPlayer p, string tier)
    {
        i.Tier = tier;
        var s = p.Surface;
        bool hidden = tier == "hidden";
        s.Paused = hidden;
        if (p.Kind == MediaKind.Web)
        {
            p.SetHidden(hidden);
            p.SetFps(tier switch { "focus" or "normal" => 30, "low" => 15, "slideshow" => 4, _ => 1 });
        }
        s.CopyEveryMs = tier switch { "low" => 66, "slideshow" => 250, _ => 0 };

        // texels per page pixel by how big it is on screen (powers of two, changed only once the want has held 1.5 s);
        // used up close, a page is drawn at the size the person reads it
        float content = Math.Max(1, s.ContentWidth * (float)s.ContentHeight);
        float want = tier == "focus" ? Math.Max(1, ZoomScale(i, s)) : i.Interest >= content * 2.5f ? 2 : i.Interest >= content / 3 ? 1 : i.Interest >= content / 16 ? 0.5f : 0.25f;
        float max = MaxScale(s);
        want = Math.Min(want, max);
        if (want != i.WantScale) { i.WantScale = want; i.WantScaleSince = DateTime.UtcNow; }
        bool settled = DateTime.UtcNow - i.WantScaleSince > TimeSpan.FromSeconds(tier == "focus" ? 0.2 : 1.5);
        if (settled && MathF.Abs(s.Scale - want) > 0.01f && !hidden)
        {
            s.Scale = want;
            if (p.Kind == MediaKind.Web) p.SetSize(s.ContentWidth, s.ContentHeight, want);
        }
    }

    /// <summary>Set by the page view: the scale a page is shown at up close (screen pixels per page pixel).</summary>
    public float DesktopZoomScale { get; set; } = 1.5f;

    float ZoomScale(MediaItem i, MediaSurface s) => i.Zoomed ? DesktopZoomScale : 1;

    static float MaxScale(MediaSurface s) =>
        Math.Max(0.25f, Math.Min((float)s.Section.MaxWidth / Math.Max(1, s.ContentWidth), (float)s.Section.MaxHeight / Math.Max(1, s.ContentHeight)));

    // ---- players -----------------------------------------------------------------------------------------------------

    MediaHelper? Helper(MediaKind kind)
    {
        if (kind == MediaKind.Web)
        {
            _web ??= NewHelper("web", Path.Combine(_runtime, "web", "sojourn_web.exe"),
                [$"--sojourn-profile={_profile}", "--sojourn-ua=Sojourn/0.1", .. PrivateArg], lowIntegrity: false);
            return _web;
        }
        _video ??= NewHelper("video", Path.Combine(_runtime, "video", "sojourn_video.exe"),
            [$"--sojourn-vlc={Path.Combine(_runtime, "video", "vlc")}", "--sojourn-ua=Sojourn/0.1", .. PrivateArg], lowIntegrity: true);
        return _video;
    }

    string[] PrivateArg => _privateOk ? ["--sojourn-private-ok"] : [];

    MediaHelper NewHelper(string kind, string exe, string[] args, bool lowIntegrity)
    {
        var h = new MediaHelper(kind, exe, args, lowIntegrity, _log);
        h.Event += ev => Post(() => HelperEvent(ev));
        h.Exited += () => Post(() => HelperLost(h));
        return h;
    }

    readonly Dictionary<string, DateTime> _helperFailedAt = new();

    /// <summary>Opens the item's player if it hasn't one (probing a prim's URL for its type first). False while it
    /// can't play yet.</summary>
    bool EnsurePlaying(MediaItem i)
    {
        if (i.Player is { Closed: false } p && i.PlayingUrl != null) return true;
        if (i.Player is { Closed: true }) Unload(i);
        string url = i.Url;
        var refusal = MediaUrls.WebRefusal(url, _privateOk);
        MediaKind kind;
        if (i.Kind == MediaItemKind.Parcel && i.Mime is { Length: > 0 } pm && pm != "none/none")
            kind = MediaUrls.KindOf(url, pm);
        else
        {
            // what kind of media the URL is: the viewer asks the server (a HEAD) before it picks a plugin
            if (i.ProbedUrl != url)
            {
                if (i.Probe == null)
                {
                    if (MediaUrls.Parse(url).Scheme is "rtsp" or "rtmp" or "rtmps" or "mms" or "mmsh" or "rtp" or "udp" or "data" or "about")
                    {
                        (i.ProbedUrl, i.ProbedKind) = (url, MediaUrls.KindOf(url, null));
                    }
                    else if (refusal == null)
                    {
                        i.Probe = MimeProbe.RunAsync(url, _privateOk);
                        i.Probe.ContinueWith(_ => _wake.Set());
                        return false;
                    }
                    else (i.ProbedUrl, i.ProbedKind) = (url, MediaKind.Web);
                }
                else if (!i.Probe.IsCompleted) return false;
                else
                {
                    var (k, mime) = i.Probe.IsCompletedSuccessfully ? i.Probe.Result : (MediaUrls.KindOf(url, null), null);
                    (i.ProbedUrl, i.ProbedKind, i.Probe) = (url, k, null);
                    i.Mime = mime;
                }
            }
            kind = i.ProbedKind;
        }
        refusal = kind == MediaKind.Video ? MediaUrls.VideoRefusal(url, _privateOk) : refusal;
        if (refusal == "slurl") refusal = "a Second Life link, not a page";
        if (refusal != null)
        {
            if (i.Refusal != refusal) _log($"media: {i.Key} not played: {refusal} ({Short(url)})");
            i.Refusal = refusal;
            return false;
        }
        var helper = Helper(kind);
        if (helper == null || !helper.Ensure(() => _wake.Set()))
        {
            if (helper?.Failure is { } why && (!_helperFailedAt.TryGetValue(why, out var at) || DateTime.UtcNow - at > TimeSpan.FromMinutes(1)))
            {
                _helperFailedAt[why] = DateTime.UtcNow;
                _log($"media: {why}");
            }
            return false;
        }

        // the surface: a page at the face's size (and room for twice its pixels up close), a video at up to 1080p
        int w = i.Width > 0 ? Math.Clamp(i.Width, 64, 2048) : 1024, h = i.Height > 0 ? Math.Clamp(i.Height, 64, 2048) : 1024;
        if (i.Kind == MediaItemKind.Page) (w, h) = (1280, 800);
        // auto-scale (the face's, or the parcel's): the page laid out at the power-of-two size it's drawn at, so it fills
        // the face instead of its corner (LLPluginClassMedia::setSizeInternal rounds the media's size up)
        else if (kind == MediaKind.Web && i.AutoScale)
            (w, h) = (Math.Min(2048, Sojourn.Render.Media.MediaSurface.NextPow2(w)), Math.Min(2048, Sojourn.Render.Media.MediaSurface.NextPow2(h)));
        MediaSurface surface;
        if (kind == MediaKind.Web)
            surface = new MediaSurface($"sojourn-media-{Environment.ProcessId}-{Interlocked.Increment(ref _sectionSeq)}", (uint)Math.Min(2048, w * 2), (uint)Math.Min(2048, h * 2), w, h);
        else
        {
            // a video's own size once it's known; stretched over the whole texture when the media scales to the face
            surface = new MediaSurface($"sojourn-media-{Environment.ProcessId}-{Interlocked.Increment(ref _sectionSeq)}", 1920, 1080,
                i.AutoScale || i.Width == 0 ? 1024 : w, i.AutoScale || i.Height == 0 ? 1024 : h);
        }
        _surfaces.Add(surface);
        var player = new MediaPlayer(kind, helper, surface, url);
        i.Player = player;
        i.PlayerKind = kind;
        i.PlayingUrl = url;
        i.Refusal = null;
        i.WantScale = surface.Scale = 1;
        _byPlayer[player.Id] = i;
        if (kind == MediaKind.Web) player.OpenWeb(url, w, h, 1, 30, i.Kind == MediaItemKind.Prim ? Allow(i) : null, audio: true);
        else player.OpenVideo(url, i.Loop, audio: true);
        if (i.Kind == MediaItemKind.Prim) _surfaces.SetFace(i.Region, i.LocalId, i.Face, surface);
        else if (i.Kind == MediaItemKind.Parcel && i.Texture != UUID.Zero) _surfaces.SetTexture(i.Texture, surface);
        AttachSound(player);
        _log($"media: playing {i.Key} ({kind.ToString().ToLowerInvariant()}{(i.Started ? ", started" : ", by itself")}): {Short(url)}");
        Notify();
        return true;
    }

    static int _sectionSeq;

    /// <summary>The whitelist a face's page navigates within, if it has one switched on.</summary>
    static IReadOnlyList<string>? Allow(MediaItem i) => i.Whitelist is { Length: > 0 } wl ? wl : null;

    void Unload(MediaItem i)
    {
        if (i.Player is not { } p) return;
        DetachSound(p);
        p.Close();
        _byPlayer.Remove(p.Id);
        _surfaces.Remove(p.Surface);
        i.Player = null;
        i.PlayingUrl = null;
        i.Zoomed = false;
        Notify();
    }

    void HelperEvent(JsonObject ev)
    {
        int id = ev.Int("id", 0);
        if (id == 0 || !_byPlayer.TryGetValue(id, out var i) || i.Player is not { } p || p.Id != id) return;
        string? kind = (string?)ev["ev"];
        string before = p.Url;
        p.Handle(ev);
        switch (kind)
        {
            case "state" when p.Kind == MediaKind.Video && i.AutoScale == false && i.Width == 0 && p.VideoWidth > 0:
                // a video without a size of its own: the texture's corner fits the video, as the viewer's does
                if (p.Surface.ContentWidth != p.VideoWidth || p.Surface.ContentHeight != p.VideoHeight)
                    (p.Surface.ContentWidth, p.Surface.ContentHeight) = (p.VideoWidth, p.VideoHeight);
                break;
            case "url" when i.Kind == MediaItemKind.Prim:
                Navigated(i, p.Url);
                break;
            case "blocked":
                _log($"media: {i.Key}: refused {Short(ev.Str("url") ?? "")} ({ev.Str("why")})");
                break;
            case "popup":
                Popup(i, ev.Str("url") ?? "", ev.Str("why") == "gesture");
                break;
            case "fullscreen" when ev.Bool("on", false):
                ZoomRequested?.Invoke(i.Key);
                break;
        }
        if (kind is "title" or "loaded" or "state" or "error") Notify();
    }

    DateTime _lastPopup;

    /// <summary>A new window a page asked for (a link with target=_blank, window.open). The viewer opens these in its own
    /// browser; here it opens up close as the standalone page (in place of an offered page or an earlier window), only
    /// when the person's own press on the page made it (a script's window on its own is refused, as browsers block
    /// popups without a click), http(s) only under the URL rules and the opener face's whitelist, one a second at most.
    /// The window is the person's: they use it, navigate it and put it away; the assistant's confirmations on it follow
    /// the page that opened it.</summary>
    void Popup(MediaItem from, string url, bool gesture)
    {
        var u = MediaUrls.Parse(url);
        string? why = !gesture ? "no press made it"
            : u.Scheme is not ("http" or "https") ? "not a web address"
            : MediaUrls.WebRefusal(url, _privateOk) is { } r ? r
            : !MediaUrls.WhitelistAllows(from.Whitelist, url) ? "not on the face's whitelist"
            : (DateTime.UtcNow - _lastPopup).TotalSeconds < 1 ? "another window a moment ago" : null;
        if (why != null)
        {
            _log($"media: {from.Key}: a new window wasn't opened ({Short(url)}: {why})");
            return;
        }
        _lastPopup = DateTime.UtcNow;
        _log($"media: {from.Key}: opening its new window {Short(url)}");
        var i = OpenPage(new MediaItem
        {
            Key = "page", Kind = MediaItemKind.Page, Url = url, Owner = from.Owner, OpenedFrom = from.Key, OpenerMine = IsMine(from),
            ObjectName = from.Kind == MediaItemKind.Parcel ? "the parcel's media" : from.ObjectName,
        });
        ZoomRequested?.Invoke(i.Key);
    }

    /// <summary>The page asked to be fullscreen (a video's button): shown up close.</summary>
    public event Action<string>? ZoomRequested;

    /// <summary>A face's page went somewhere: the person's own navigation is sent to the region so everyone sees it
    /// (ObjectMediaNavigate, as LLVOVolume::mediaNavigated), if they may interact; redirects and the region's own
    /// navigations aren't.</summary>
    void Navigated(MediaItem i, string url)
    {
        if (url == i.ServerNavUrl) { i.ServerNavUrl = null; return; }
        if (!i.UserNav || DateTime.UtcNow - i.UserNavAt > TimeSpan.FromSeconds(8)) return;
        i.UserNav = false;
        if (url == i.Url || url.StartsWith("about:") || url.StartsWith("data:")) return;
        if (!May(i, control: false) || !MediaUrls.WhitelistAllows(i.Whitelist, url)) return;
        i.Url = url;
        i.PlayingUrl = url;
        SendNavigate(i, url);
    }

    void HelperLost(MediaHelper h)
    {
        foreach (var i in _items.Values.Where(x => x.Player != null).ToList())
        {
            if ((i.PlayerKind == MediaKind.Web ? _web : _video) != h) continue;
            i.Player!.Lost();
            Unload(i);
        }
    }

    // ---- sound -------------------------------------------------------------------------------------------------------

    void AttachSound(MediaPlayer p)
    {
        if (!SoundOn) return;
        int s = Array.IndexOf(_streams, false);
        if (s < 0) return;   // 16 at once: the rest play silent
        unsafe
        {
            if (MediaNative.sv_media_attach(s, p.Surface.Section.Base) != 0) return;
        }
        _streams[s] = true;
        p.Stream = s;
    }

    void DetachSound(MediaPlayer p)
    {
        if (p.Stream < 0) return;
        MediaNative.sv_media_detach(p.Stream);
        _streams[p.Stream] = false;
        p.Stream = -1;
    }

    /// <summary>Each 100 ms: every playing item's loudness (the media volume at the viewer's level, the item's own, and
    /// for faces the viewer's rolloff: full to 40 m, falling to nothing at 80 m) and where it comes from (a face's sound
    /// from its two sides, when placed; parcel media and pages centred).</summary>
    void Sound()
    {
        if (!SoundOn) return;
        foreach (var i in _items.Values)
        {
            if (i.Player is not { Stream: >= 0 } p) continue;
            float gain = MediaLevel * VolumeSetting * i.Volume;
            bool spatial = false;
            NVector3 l = default, r = default;
            if (i.Kind == MediaItemKind.Prim && !i.OnHud)
            {
                float d = i.Distance;
                if (d > RollOffMax) gain = 0;
                else if (d > RollOffMin)
                {
                    float att = 1 + RollOffRate * (d - RollOffMin);
                    gain *= MathF.Min(1, 1 / (att * att));
                }
                if (Placed && !i.Zoomed && i.Frame is { } f)
                {
                    float half = MathF.Min(f.Width / 2, 1.5f) * 0.8f;
                    (l, r, spatial) = (f.Centre - f.Right * half, f.Centre + f.Right * half, true);
                }
                else if (Placed && !i.Zoomed) (l, r, spatial) = (i.RegionPos, i.RegionPos, true);
            }
            if (spatial)
            {
                Utils.LongToUInts(i.Region, out uint rx, out uint ry);
                MediaNative.sv_media_set(p.Stream, gain, 1, rx + (double)l.X, ry + (double)l.Y, l.Z, rx + (double)r.X, ry + (double)r.Y, r.Z);
            }
            else MediaNative.sv_media_set(p.Stream, gain, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    // ---- the person's and the assistant's hands ---------------------------------------------------------------------

    /// <summary>Starts it (and keeps it on until stopped): local, always allowed.</summary>
    public void Start(MediaItem i)
    {
        i.Started = true;
        i.Stopped = false;
        i.Refusal = null;
        i.ProbedUrl = null;
        Focus(i, 4000);
        _wake.Set();
        _lastSchedule = 0;
    }

    /// <summary>Stops and unloads it until started again: local, always allowed.</summary>
    public void Stop(MediaItem i)
    {
        i.Stopped = true;
        i.Started = false;
        if (i.Kind == MediaItemKind.Page) { _items.Remove(i.Key); }
        Unload(i);
        Notify();
    }

    public void Focus(MediaItem i, int ms) => i.FocusUntil = Math.Max(i.FocusUntil, Environment.TickCount64 + ms);

    /// <summary>The person (or the assistant for them) navigates a face's page: within its whitelist, and sent to the
    /// region for everyone if they may control it (the viewer's URL bar is a control).</summary>
    public string? NavigateTo(MediaItem i, string url, bool user)
    {
        url = url.Trim();
        if (!url.Contains(':')) url = "https://" + url;
        if (MediaUrls.WebRefusal(url, _privateOk) is { } why) return $"can't go there ({why})";
        if (i.Kind == MediaItemKind.Prim && !MediaUrls.WhitelistAllows(i.Whitelist, url)) return "that address isn't on this face's whitelist";
        if (i.Kind == MediaItemKind.Prim && !May(i, control: true)) return "its owner doesn't let others change what it shows";
        if (i.Player is { Kind: MediaKind.Web, Closed: false } p && MediaUrls.KindOf(url, null) == MediaKind.Web)
        {
            i.ServerNavUrl = url;   // broadcast here once, not again when the address changes
            p.Navigate(url);
            i.PlayingUrl = url;
        }
        else Unload(i);   // reopened at the new URL (and its kind found again)
        i.Url = url;
        i.ProbedUrl = null;
        Start(i);
        if (i.Kind == MediaItemKind.Prim && user) SendNavigate(i, url);
        return null;
    }

    /// <summary>The person's own: media on their objects and HUDs, their land's, a page their own object offered. The
    /// confirmation rule for the assistant's presses is about everything else.</summary>
    public bool IsMine(MediaItem i) => i.Kind switch
    {
        MediaItemKind.Prim => i.Mine,
        MediaItemKind.Parcel => _agentParcelMine,
        _ => i.OpenedFrom != null ? i.OpenerMine : i.Offer is { } o && !o.OwnerIsGroup && o.OwnerId == _s.Client?.Self.AgentID,
    };

    /// <summary>Changes what one of the person's own faces shows, for everyone (ObjectMedia's update, with the other
    /// faces' entries as fetched): its current URL, and its home too if asked. Shown here at once.</summary>
    public async Task<string?> SetUrlAsync(MediaItem i, string url, bool home, CancellationToken ct)
    {
        url = url.Trim();
        if (!url.Contains(':')) url = "https://" + url;
        LMediaEntry?[] entries;
        UUID id;
        Simulator sim;
        lock (_mu)
        {
            if (i.Kind != MediaItemKind.Prim) return "only media on an object's face can be set for everyone";
            if (!i.Mine) return "that face isn't the person's own";
            if (MediaUrls.WebRefusal(url, _privateOk) is { } why && MediaUrls.VideoRefusal(url, _privateOk) != null) return $"can't show that ({why})";
            if (!MediaUrls.WhitelistAllows(i.Whitelist, url)) return "that address isn't on this face's whitelist";
            if (!_prims.TryGetValue((i.Region, i.LocalId), out var pm) || pm.Entries is not { } fetched || pm.Sim is not { } s
                || i.Face >= fetched.Length || fetched[i.Face] is not { } entry)
                return "its media settings haven't come from the region yet";
            entry.CurrentURL = url;
            if (home) entry.HomeURL = url;
            (entries, id, sim) = (fetched, pm.Id, s);
        }
        if (_s.Client is not { } c) return "not logged in";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        await c.Objects.UpdateObjectMediaAsync(id, entries!, sim, cts.Token);
        lock (_mu) return NavigateTo(i, url, user: false);
    }

    /// <summary>A face's place this second (the main thread works them out: FrameRenderer.MediaFace).</summary>
    public void SetFrame(MediaItem i, FaceFrame? f)
    {
        i.FrameAt = DateTime.UtcNow;
        if (f != null) i.Frame = f;
    }

    /// <summary>The person clicked into a face's page: an address change soon after is theirs.</summary>
    public void UserActed(MediaItem i)
    {
        i.UserNav = true;
        i.UserNavAt = DateTime.UtcNow;
        Focus(i, 6000);
    }

    // ---- the page an object offered (llLoadURL), opened by the person ----------------------------------------------

    public MediaItem OpenOffer(WebPageOffer offer) =>
        OpenPage(new MediaItem { Key = "page", Kind = MediaItemKind.Page, Url = offer.Url, Offer = offer, ObjectName = offer.ObjectName, Owner = offer.OwnerId });

    /// <summary>The standalone page (an offered page or a page's new window), in place of any before it, up close.</summary>
    MediaItem OpenPage(MediaItem i)
    {
        foreach (var old in _items.Values.Where(x => x.Kind == MediaItemKind.Page).ToList())
        {
            Unload(old);
            _items.Remove(old.Key);
        }
        i.Started = true;
        i.Zoomed = true;
        _items[i.Key] = i;
        _lastSchedule = 0;
        _wake.Set();
        Notify();
        return i;
    }

    // ---- status ------------------------------------------------------------------------------------------------------

    public JsonObject Describe(MediaItem i, bool full)
    {
        var o = new JsonObject { ["key"] = i.Key, ["kind"] = i.Kind.ToString().ToLowerInvariant() };
        if (i.Kind == MediaItemKind.Prim)
        {
            o["object_id"] = i.PrimId.ToString();
            o["face"] = i.Face;
            if (i.ObjectName is { } n) o["name"] = n;
            o["dist"] = MathF.Round(i.Distance, 1);
            o["mine"] = i.Mine;
            if (i.OnHud) o["hud"] = true;
        }
        if (i.Kind == MediaItemKind.Page && i.Offer is { } off) { o["offered_by"] = off.ObjectName; }
        if (i.Kind == MediaItemKind.Page && i.OpenedFrom is { } from) { o["opened_from"] = from; }
        o["url"] = i.Player?.Url ?? i.Url;
        if (i.Player is { } p)
        {
            o["state"] = p.State;
            o["type"] = p.Kind == MediaKind.Web ? "page" : "video";
            if (p.Title is { Length: > 0 } t) o["title"] = t;
            if (p.Kind == MediaKind.Video) { o["time"] = Math.Round(p.Ms / 1000, 1); if (p.LengthMs > 0) o["length"] = Math.Round(p.LengthMs / 1000, 1); }
            if (p.Kind == MediaKind.Web) { o["can_back"] = p.CanBack; o["can_forward"] = p.CanForward; if (p.WantsKeys) o["field_focused"] = true; }
            if (p.Error is { } e) o["error"] = e;
            if (p.Refused is { } rf && DateTime.UtcNow - rf.At < TimeSpan.FromMinutes(2)) o["refused"] = $"{Short(rf.Url)} ({rf.Why})";
        }
        else
        {
            o["state"] = NotShown(i) is { } ns ? $"not shown: {ns}" : i.Refusal is { } r ? $"can't play: {r}" : i.Stopped ? "stopped" : Wanted(i) ? "starting" : "waiting to be started";
        }
        o["interact"] = May(i, false);
        o["control"] = May(i, true);
        if (full)
        {
            o["tier"] = i.Tier;
            if (i.Home.Length > 0) o["home"] = i.Home;
            if (i.Whitelist is { Length: > 0 } wl) o["whitelist"] = new JsonArray(wl.Select(x => (JsonNode)x).ToArray());
            o["autoplay"] = i.AutoPlayFlag;
            o["started"] = i.Started;
            o["volume"] = i.Volume;
            if (i.Player is { } pp) { o["scale"] = pp.Surface.Scale; o["interest"] = MathF.Round(i.Interest); o["frames"] = pp.Surface.FramesShown; }
            if (i.Frame is { } f)
                o["face_frame"] = new JsonObject
                {
                    ["centre"] = new JsonArray(MathF.Round(f.Centre.X, 2), MathF.Round(f.Centre.Y, 2), MathF.Round(f.Centre.Z, 2)),
                    ["normal"] = new JsonArray(MathF.Round(f.Normal.X, 2), MathF.Round(f.Normal.Y, 2), MathF.Round(f.Normal.Z, 2)),
                    ["size"] = new JsonArray(MathF.Round(f.Width, 2), MathF.Round(f.Height, 2)),
                };
        }
        return o;
    }

    internal static string Short(string url) => url.Length > 100 ? url[..100] + "..." : url;

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(1000);
        lock (_mu)
        {
            foreach (var i in _items.Values.ToList()) Unload(i);
            _items.Clear();
        }
        _web?.Dispose();
        _video?.Dispose();
    }
}
