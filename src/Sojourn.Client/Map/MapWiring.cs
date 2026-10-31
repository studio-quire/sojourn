using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sojourn.Client.Assistant;
using Sojourn.Client.Control;
using Sojourn.Client.Xr;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Map;

/// <summary>Where the mini-map is: put away, on the left hand over the location line, or out as a board of its own.</summary>
public enum MiniHome { Off, Hand, Free }

/// <summary>
/// Batch B8's map and location (docs/map.md), kept out of Program.cs: the two maps (the world map, and the mini-map on
/// the hand or out on its own), the location line, their tiles and data, SojournMap (where am I, statistics, a map shown
/// at a place), the settings, and their pictures on screen: in the headset each its own quad layer
/// (XrSession.SetCards, group "map") with its frost drawn into the eyes, those held at the hand (the line, the mini-map
/// on it) over the hands' layer; on the desktop flat at the top left; all from one atlas the overlay binds
/// (OverlayScene.BoardAtlas). Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class MapWiring : IDisposable
{
    readonly AgentSession _s;
    readonly Func<PanelLook> _look;
    readonly ILogger _log;
    readonly string _path;
    public readonly MapTiles Tiles;
    public readonly WorldMap Map;
    /// <summary>The world map: opened from the mini-map's globe or by the assistant, kept as it was between openings.</summary>
    public readonly MapBoard World;
    /// <summary>The mini-map: opened from the location line, following you.</summary>
    public readonly MapBoard Mini;
    public readonly WristLine Line;

    /// <summary>This client's frame rate over the last second and what it should be (the headset's refresh; 0 on the
    /// desktop, where the lag meter's own thresholds apply). Set by the frame loop.</summary>
    public float Fps { get; set; }
    public float TargetFps { get; set; }

    public static MapWiring Start(AgentSession s, Func<PanelLook> look, string vrSettingsPath, ILogger log) => new(s, look, vrSettingsPath, log);

    MapWiring(AgentSession s, Func<PanelLook> look, string vrSettingsPath, ILogger log)
    {
        _s = s;
        _look = look;
        _log = log;
        _path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(vrSettingsPath)) ?? ".", "map-settings.json");
        Tiles = new MapTiles(() => s.Client?.Network.LoginResponseData?.MapServerUrl);
        Map = new WorldMap(s);
        Action<string> say = m => log.LogInformation("{Msg}", m);
        // the world map opens a few regions across (names readable, crowds counted), the mini-map about 125 m across
        World = new MapBoard(s, Map, Tiles, look, say, MapDock.World, "world map", MapBoard.WorldRest, MapBoard.WorldDistance, openMpp: 2);
        Mini = new MapBoard(s, Map, Tiles, look, say, MapDock.Hand, "mini-map", MapBoard.MiniRest, MapBoard.MiniDistance, openMpp: 0.5f);
        Line = new WristLine(s);
        if (Environment.GetEnvironmentVariable("SOJOURN_MAP_DEBUG") == "1") Line.Debug = m => log.LogInformation("{Msg}", m);
        World.Line = () => StatsNow() is { } st ? StatsLine(st) : null;
        Mini.OpenWorld = () => World.Toggle();
        Mini.WorldUp = () => World.Shown;
        Mini.PopOut = () => SetMini(MiniHome.Free);
        Mini.PutBack = () => SetMini(MiniHome.Hand);
        Mini.Closed = () => SetMini(MiniHome.Off);
        Load();
        s.Api.Register(new MapApi(s, this));
    }

    public Stats? StatsNow() => Where.StatsNow(_s, Fps, TargetFps);

    readonly System.Collections.Concurrent.ConcurrentQueue<Action> _main = new();

    /// <summary>Run on the main thread at the next frame (the boards are the frame's); the task ends when it has.</summary>
    public Task OnMain(Action a)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _main.Enqueue(() => { try { a(); done.TrySetResult(); } catch (Exception e) { done.TrySetException(e); } });
        return done.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    void Drain()
    {
        while (_main.TryDequeue(out var a)) a();
    }

    /// <summary>Statistics at a glance, under the world map: the region's frame rate and time dilation, the ping, people.</summary>
    static string StatsLine(Stats st) =>
        $"region {st.SimFps:0} fps · time {st.Dilation:0.00} · ping {st.PingMs:0} ms · {st.Agents} {(st.Agents == 1 ? "person" : "people")} · you {st.Fps:0} fps";

    // --- the mini-map's place -----------------------------------------------------------------------------------------

    public MiniHome MiniAt { get; private set; } = MiniHome.Off;

    /// <summary>Put the mini-map away, on the hand, or out as a board of its own (kept for next time). Main thread.</summary>
    public void SetMini(MiniHome h)
    {
        if (h == MiniAt) return;
        MiniAt = h;
        if (h != MiniHome.Off) Mini.Dock = h == MiniHome.Free ? MapDock.Free : MapDock.Hand;
        Mini.Show(h != MiniHome.Off);
        _log.LogInformation("map: mini-map {Where}", h switch { MiniHome.Hand => "on the hand", MiniHome.Free => "out on its own", _ => "away" });
        ShownWrist();
        if (!_loading) Save();
    }

    // --- settings -------------------------------------------------------------------------------------------------------

    sealed class Saved
    {
        public bool LocationLine { get; set; } = true;
        public bool LagMeter { get; set; } = true;
        public bool Sale { get; set; }
        public bool Events { get; set; } = true;
        /// <summary>Where the mini-map was: off, hand, free.</summary>
        public string MiniMap { get; set; } = "off";
    }

    bool _loading;

    void Load()
    {
        try
        {
            if (File.Exists(_path) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(_path)) is { } v)
            {
                Line.On = v.LocationLine;
                Line.Meter = v.LagMeter;
                World.SetLayers(v.Sale, v.Events);
                Mini.SetLayers(v.Sale, v.Events);
                (_saleBefore, _eventsBefore) = (v.Sale, v.Events);
                _loading = true;
                if (Enum.TryParse<MiniHome>(v.MiniMap, ignoreCase: true, out var h)) SetMini(h);
            }
        }
        catch (Exception e) { _log.LogWarning("map settings: {Msg}", e.Message); }
        finally { _loading = false; }
        ShownWrist();
    }

    void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(new Saved
            {
                LocationLine = Line.On, LagMeter = Line.Meter, Sale = World.View.Sale, Events = World.View.Events,
                MiniMap = MiniAt.ToString().ToLowerInvariant(),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { _log.LogWarning("map settings: {Msg}", e.Message); }
    }

    /// <summary>Where the assistant's wrist cards start: above the line, and above the mini-map while it's on the hand.</summary>
    void ShownWrist() => Sojourn.Client.Showing.ShownView.WristFrom = (Line.On ? WristLine.CardsFrom : 0.05f)
        + (MiniAt == MiniHome.Hand ? MapLayout.HandH * WristLine.Mpp + WristLine.Gap : 0);

    /// <summary>The settings, for any caller of the agent API (menu rows are batch M's).</summary>
    public void Settle(SettingsApi api)
    {
        // the tracking beacon (Beacons/Tracker.cs): either map's track key drives it, and they redraw when it ends
        if (Sojourn.Client.Beacons.Tracker.Shared is { } tracker)
            foreach (var b in new[] { World, Mini })
            {
                b.Track = tracker.TrackPlace;
                b.TrackAvatar = tracker.TrackAvatar;
                b.Untrack = tracker.Clear;
                b.Tracking = () => tracker.Active;
                tracker.Changed += b.Redraw;
            }
        api.Add("location_line", new("view", "where you are, as the viewer's location bar: the parcel, region, rating and what's blocked there (and your health "
            + "where damage is on); in the headset just above the left wrist while you look at it, on the desktop at the top left; pressed, it opens the mini-map",
            "on, off", () => Line.On, SettingsApi.Switch("location_line", on => { Line.On = on; ShownWrist(); Save(); })));
        api.Add("lag_meter", new("view", "the location line ends in the viewer's lag meter, three small bars full when all is well: this computer's frame rate, "
            + "the connection (ping), the region's frame time", "on, off", () => Line.Meter, SettingsApi.Switch("lag_meter", on => { Line.Meter = on; Save(); })));
    }

    /// <summary>The map's layers changed by hand or by the assistant: on both maps, and kept for next time.</summary>
    public void SetLayers(bool? sale, bool? events)
    {
        World.SetLayers(sale, events);
        KeepLayers();
    }

    bool _saleBefore, _eventsBefore = true;

    /// <summary>The world map's layer keys (land for sale, events) hold for the mini-map too, and are kept.</summary>
    void KeepLayers()
    {
        if (World.View.Sale == _saleBefore && World.View.Events == _eventsBefore) return;
        (_saleBefore, _eventsBefore) = (World.View.Sale, World.View.Events);
        Mini.SetLayers(_saleBefore, _eventsBefore);
        Save();
    }

    // --- the headset ----------------------------------------------------------------------------------------------------

    /// <summary>Each headset frame: the line at the wrist and the mini-map over it while the wrist is looked at (input:
    /// the controllers as read, for the left hand's pose), or the mini-map out on its own, and the world map (boards: the
    /// hands as the boards see them).</summary>
    public void Headset(XrControllers input, XrControllers boards, Vector3 head, Quaternion headRot, (Vector3 Pos, Quaternion Rot, Vector2 Size)? panel,
        bool blocked, bool hidden, bool focused, float dt)
    {
        Drain();
        var line = input;
        (line.RightAim, line.RightAimPos, line.RightTrigger) = (boards.RightAim, boards.RightAimPos, boards.RightTrigger);
        Line.LookNow = _look();
        bool onHand = MiniAt == MiniHome.Hand;
        if (MiniAt != MiniHome.Off) Mini.Dock = onHand ? MapDock.Hand : MapDock.Free;
        float miniH = MapLayout.HandH * WristLine.Mpp;
        bool asked = Line.Headset(line, head, headRot, Line.Meter ? StatsNow() : null, hidden, blocked, focused,
            above: onHand ? miniH : 0, hold: onHand && Mini.Pressing, minW: onHand ? MapLayout.HandMin : 0);
        ShownWrist();
        // on the hand: stacked over the line, in its plane, while the wrist is looked at
        (Vector3, Quaternion, float)? held = null;
        if (onHand && Line.StackFrom is { } sf)
            held = (sf.Bottom + Vector3.Transform(Vector3.UnitY, sf.Rot) * (miniH / 2), sf.Rot, WristLine.Mpp);
        // the ray goes to the nearest of them (the line and the mini-map on the hand in front of any board)
        float worldT = World.RayT(boards) ?? float.MaxValue, miniT = Mini.RayT(boards) ?? float.MaxValue;
        Mini.Headset(boards, head, headRot, panel, blocked, hidden || onHand && held == null, focused, dt, held, MathF.Min(Line.HitT, worldT));
        World.Headset(boards, head, headRot, panel, blocked, hidden, focused, dt, nearer: MathF.Min(Line.HitT, Mini.RightOn ? Mini.HitT : miniT));
        // the line pressed: the mini-map opens on the hand, or closes; out on its own, it comes back to the hand
        if (asked) SetMini(onHand ? MiniHome.Off : MiniHome.Hand);
        KeepLayers();
    }

    public bool RightOn => World.RightOn || Mini.RightOn || Line.RightOn;

    /// <summary>The right stick zooms the map under the right ray, either map wherever it is (the user, 2026-10-29), as
    /// the reader and feed scroll (Control/SheetStick.cs): up zooms in, as a wheel rolled away does.</summary>
    public StickSheet? Sheet()
    {
        var b = Mini.RightOn ? Mini : World.RightOn ? World : null;
        return b == null ? null : new StickSheet(b, Scroll: (down, _) => b.Zoom(-down));
    }

    /// <summary>The boards' and the line's layers onto the headset (every frame, so they go when they're down), their
    /// frost and the rays' beams into the eyes' overlay. more: other sheets of the client's placed as the boards are, in
    /// the same atlas (the camera's picture just taken, Control/ShutterReview).</summary>
    public OverlayScene? HeadsetScene(OverlayScene? scene, XrSession hs, Func<Vector3, Vector3> roomToRegion, bool inEyes,
        (string Key, OverlayImage Image, Vector3 Pos, Quaternion Rot, float Width)? more = null)
    {
        var layers = new List<(string, OverlayImage, Vector3, Quaternion, float)>(3);
        var placed = new List<(OverlayImage Image, Vector3[] Corners, bool Held)>(3);
        void Add(string key, (OverlayImage Image, Vector3 Pos, Quaternion Rot, float Width)? l, bool held)
        {
            if (l is not { } x) return;
            layers.Add((key, x.Image, x.Pos, x.Rot, x.Width));
            var size = new Vector2(x.Width, x.Width * x.Image.Height / x.Image.Width);
            placed.Add((x.Image, PanelPlacement.Corners(x.Pos, x.Rot, size).Select(roomToRegion).ToArray(), held));
        }
        Add("map board", World.Layer(), false);
        // the line and the mini-map on the hand are held there as the forearm's tokens are: their layers over the hands'
        // and everything else; out on its own, the mini-map is a board like the world map (another key, another layer)
        bool miniHeld = MiniAt == MiniHome.Hand;
        Add(miniHeld ? MiniHeldKey : MiniKey, Mini.Layer(), miniHeld);
        Add(LineKey, Line.Layer(), true);
        if (more is { } m) Add(m.Key, (m.Image, m.Pos, m.Rot, m.Width), false);
        if (hs.HeldCards.Length == 0) hs.HeldCards = [LineKey, MiniHeldKey];
        hs.SetCards(layers, "map");
        scene = World.Decorate(scene, roomToRegion);
        scene = Mini.Decorate(scene, roomToRegion);
        scene = Line.Decorate(scene, roomToRegion);
        if (placed.Count == 0) return scene;
        var (atlas, uvs) = Atlas(placed.Select(p => p.Image).ToList());
        var cards = placed.Select((p, i) => new OverlayCard(uvs[i]) { Corners = p.Corners, Content = inEyes, Frost = p.Image.Frost, Edge = p.Image.Edge, Held = p.Held }).ToList();
        return (scene ?? new OverlayScene()) with { BoardAtlas = atlas, Boards = cards };
    }

    const string LineKey = "map line", MiniHeldKey = "map mini held", MiniKey = "map mini";

    public void Recentred()
    {
        World.Recentred();
        Mini.Recentred();
    }

    // --- the desktop ----------------------------------------------------------------------------------------------------

    /// <summary>The desktop's line, mini-map (under the line) and world map (beside the mini-map, or under the line) over
    /// the view, top left. ui: the desktop's UI scale.</summary>
    public OverlayScene? Desktop(OverlayScene? scene, float ui, bool hidden)
    {
        Drain();
        var look = _look();
        var line = Line.Desktop(Line.Meter ? StatsNow() : null, look, ui, hidden);
        if (MiniAt != MiniHome.Off) Mini.Dock = MapDock.Desktop;
        float top = Line.DesktopBottom(ui), left = 16 * ui;
        var mini = Mini.Desktop(ui, left, top, hidden);
        if (Mini.Shown) left = mini is { } m ? m.At.X + m.Image.Width + 8 * ui : left + MapLayout.Of(Mini.View).W * ui + 8 * ui;
        var world = World.Desktop(ui, left, top, hidden);
        KeepLayers();
        var flat = new List<(OverlayImage Image, Vector2 At)>(3);
        if (line is { } l) flat.Add(l);
        if (mini is { } mm) flat.Add(mm);
        if (world is { } w) flat.Add(w);
        if (flat.Count == 0) return scene;
        var (atlas, uvs) = Atlas(flat.Select(f => f.Image).ToList());
        var cards = flat.Select((f, i) => new OverlayCard(uvs[i]) { Screen = f.At, Frost = f.Image.Frost, Edge = f.Image.Edge }).ToList();
        return (scene ?? new OverlayScene()) with { BoardAtlas = atlas, Boards = cards };
    }

    public bool DesktopOver(Vector2 px) => Line.DesktopOver(px) || Mini.DesktopOver(px) || World.DesktopOver(px);

    public void DesktopMove(Vector2 px)
    {
        Line.DesktopMove(px);
        Mini.DesktopMove(px);
        World.DesktopMove(px);
    }

    /// <summary>A left click: true if it was the line's or a map's. The line opens and closes the mini-map.</summary>
    public bool DesktopDown(Vector2 px)
    {
        if (Line.DesktopOver(px)) { SetMini(Mini.Shown ? MiniHome.Off : MiniHome.Hand); return true; }
        return Mini.DesktopDown(px) || World.DesktopDown(px);
    }

    public void DesktopUp(Vector2 px)
    {
        Mini.DesktopUp(px);
        World.DesktopUp(px);
    }

    public bool DesktopWheel(Vector2 px, float y) => Mini.DesktopWheel(px, y) || World.DesktopWheel(px, y);

    // --- the atlas --------------------------------------------------------------------------------------------------------

    long _atlasOf = -1;
    OverlayImage? _atlas;
    Vector4[] _uvs = [];
    long _atlasVersion;

    /// <summary>The pictures stacked into one (the overlay binds one picture for them), and each one's place in it; made
    /// afresh only when one of them changed.</summary>
    (OverlayImage Atlas, Vector4[] Uvs) Atlas(List<OverlayImage> images)
    {
        long key = images.Count;
        foreach (var i in images) key = key * 1_000_003 + i.Version * 31 + i.Width;
        if (_atlas != null && key == _atlasOf && _uvs.Length == images.Count) return (_atlas, _uvs);
        int w = images.Max(i => i.Width), h = images.Sum(i => i.Height + 2);
        var px = new byte[w * h * 4];
        var uvs = new Vector4[images.Count];
        int y = 0;
        for (int n = 0; n < images.Count; n++)
        {
            var img = images[n];
            for (int row = 0; row < img.Height; row++)
                Buffer.BlockCopy(img.Rgba, row * img.Width * 4, px, ((y + row) * w) * 4, img.Width * 4);
            uvs[n] = new Vector4(0, y, img.Width, img.Height);
            y += img.Height + 2;
        }
        _atlas = new OverlayImage(px, w, h, Interlocked.Increment(ref _atlasVersion));
        _uvs = uvs;
        _atlasOf = key;
        return (_atlas, _uvs);
    }

    public void Dispose() => Tiles.Dispose();
}
