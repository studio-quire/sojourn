using Microsoft.Extensions.Logging;
using Sojourn.Client.Xr;
using Sojourn.Render.Realtime;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Assistant;

/// <summary>
/// Where the reader (<see cref="Reader"/>) is and what the hands do with it. Headset: its own quad layer, 1.2 m out,
/// its bottom edge just above the panel's top (the panel at 1.1 m below it stays in front where they meet), turned to
/// face the person, following the head lazily as every menu does, or put somewhere by dragging its title strip
/// (<see cref="PanelPlacement"/>). The right ray and trigger: on the text, a drag scrolls it (it runs on a little when
/// let go); on the title strip, a drag moves it; × closes it. The ray stops at it. Desktop: it takes the panel's place
/// at the top right while it's open (Program's DesktopAssistant), the mouse dragging or wheeling it. Hidden while a
/// confirmation waits.
/// </summary>
public sealed class ReaderView
{
    public readonly Reader Reader;
    readonly Func<PanelLook> _look;
    const float Distance = 1.2f;
    static float Mpp => UiSize.MetresPerPx(PanelPlacement.Distance);

    public ReaderView(Reader reader, Func<PanelLook> look)
    {
        Reader = reader;
        _look = look;
        reader.OpenChanged += open => { if (open) _place = true; };
    }

    /// <summary>Where its layer floats: following, or where the person put it.</summary>
    public readonly PanelPlacement Placement = new("reader", Rest);
    bool _place, _shownBefore;
    Vector3 _pos;
    Quaternion _rot = Quaternion.Identity;
    Vector3 _head;

    // --- drawing ---------------------------------------------------------------------------------------------------

    volatile Reader.Raster? _latest;
    bool _drawing;
    (long Version, float Scale, bool Lit) _drawnKey = (-1, 0, false);

    /// <summary>The latest picture of it, redrawn off the main thread when it changes (opened, scrolled, the look).</summary>
    /// <summary>The session's replies (their version, and as a document): kept current while the reader holds them.</summary>
    public (Func<long> Version, Func<ReaderDoc> Doc)? Replies { get; set; }
    long _repliesSeen = -1;

    public Reader.Raster? Frame(float scale)
    {
        Reader.Look = _look();
        if (Replies is { } rp && Reader.Doc?.Kind == ReaderKind.Replies && rp.Version() is var rv && rv != _repliesSeen)
        {
            if (_repliesSeen >= 0) Reader.Refresh(rp.Doc());
            _repliesSeen = rv;
        }
        if (!Reader.Open) { _latest = null; _drawnKey = (-1, 0, false); return null; }
        var key = (Reader.Version, scale, Reader.CloseLit);
        if (!_drawing && key != _drawnKey)
        {
            _drawing = true;
            _drawnKey = key;
            Task.Run(() =>
            {
                try { _latest = Reader.Draw(scale, key.Item3); }
                catch (Exception e) { Console.Error.WriteLine($"reader: couldn't draw it ({e.GetType().Name}: {e.Message})"); }
                finally { _drawing = false; }
            });
        }
        return _latest;
    }

    // --- the headset -------------------------------------------------------------------------------------------------

    /// <summary>This frame the right ray is on the reader (the panel, the feed and the world behind wait).</summary>
    public bool RightOn { get; private set; }

    bool _down, _armed = true, _moving;
    double _clock;

    /// <summary>Each headset frame, before the panel's hand: places it, reads the right ray and trigger, puts it on its
    /// layer. panel: the panel's layer last frame (nearer: a ray reaching it first isn't on the reader). hidden: a
    /// confirmation waits (nothing is shown beside it, and a trigger held over from it waits for a release).</summary>
    public void Headset(XrControllers input, Vector3 head, Quaternion headRot, (Vector3 Pos, Quaternion Rot, Vector2 Size)? panel, bool hidden,
        bool focused, XrSession hs, float dt)
    {
        _clock += dt;
        _head = head;
        RightOn = false;
        Reader.Tick(dt);
        Reader.ViewHeight = Reader.BaseViewHeight * UiSize.Fit;   // the same space at any size: more lines when smaller
        Reader.Wide = UiSize.Wide;
        var r = hidden ? null : Frame(UiSize.Raster);
        // placed afresh each time it comes up (opened, or back after a confirmation or X): where it was put if that's
        // in view, else following in front
        if (r != null && (!_shownBefore || _place)) { Placement.Place(); _place = false; }
        _shownBefore = r != null;
        if (r != null)
            (_pos, _rot) = Placement.Update(head, headRot, new Vector2(r.LogicalWidth, r.LogicalHeight) * Mpp, dt, frozen: _onSheet || _down);   // (last frame's: the ray on it, or a press held)
        _sheet = r == null ? null : (r.Image, PanelPlacement.Corners(_pos, _rot, new Vector2(r.LogicalWidth, r.LogicalHeight) * Mpp));
        if (r == null)
        {
            Placement.CancelGrab();
            hs.SetReader(null, default, Quaternion.Identity, 0);
            // closed (its × pressed) or hidden: no beam left behind. It stayed, still and beside the real pointer, after
            // the × closed it mid-press (2026-10-27); a press that closed it is over.
            _beam = null;
            if (hidden) _armed = false;
            End();
            if (input.RightTrigger > 0.35f) _armed = false;   // the closing pull waits for its release
            return;
        }
        var size = new Vector2(r.LogicalWidth, r.LogicalHeight) * Mpp;
        hs.SetReader(r.Image, _pos, _rot, size.X);

        Vector2? at = null;
        float t = 0;
        if (input.RightAim is { } q && input.RightAimPos is { } from && focused)
        {
            var dir = Vector3.Transform(-Vector3.UnitZ, q);
            float panelT = panel is { } pl && PanelPlacement.Hit(from, dir, pl.Pos, pl.Rot, pl.Size, out var pt, out _) ? pt : float.MaxValue;
            // the title strip held: it follows the ray at the distance it was taken, facing you (the hit is tested
            // where it was this frame, before the move: the next frame's test sees it where it went)
            if (_moving && Placement.Drag(from, dir)) (_pos, _rot) = Placement.Pose;
            if (PanelPlacement.Hit(from, dir, _pos, _rot, size, out t, out var local) && t < panelT)
                at = new Vector2((local.X / size.X + 0.5f) * r.LogicalWidth, (0.5f - local.Y / size.Y) * r.LogicalHeight);
        }
        RightOn = at != null || _down;
        _onSheet = at != null;
        _beam = RightOn && input.RightAimPos is { } bf && input.RightAim is { } bq ? (bf, bf + Vector3.Transform(-Vector3.UnitZ, bq) * (at != null ? t : 0.4f)) : null;
        if (!_moving) Reader.Point(at, _clock);
        if (input.RightTrigger < 0.35f)
        {
            if (_down)
            {
                if (_moving) Placement.Drop(); else Reader.PressUp(at);
                _down = false;
                _moving = false;
            }
            _armed = true;
        }
        else if (input.RightTrigger > 0.6f && !_down && _armed && at is { } a && input.RightAimPos is { } o && input.RightAim is { } rq)
        {
            _down = true;
            _armed = false;
            if (a.Y < Reader.Header && !Reader.CloseKey.Contains(a.X, a.Y))
            {
                _moving = true;
                Placement.Grab(o, Vector3.Transform(-Vector3.UnitZ, rq), t);
            }
            else Reader.PressDown(a, _clock);
        }
    }

    (Vector3 From, Vector3 To)? _beam;
    bool _onSheet;   // the beam ends on the reader (not the short stub while a press is held off it)

    (OverlayImage Image, Vector3[] Corners)? _sheet;   // this frame's picture on its layer and its corners in the room

    /// <summary>The reader's sheet in the eyes (its frost, as the panel's and the feed's; with inEyes the reader itself,
    /// for checking without a headset), and the ray's cue while it's on the reader (a beam to where it lands): it works
    /// with the pointer off too, as the reader is the person's own to scroll.</summary>
    public OverlayScene? Decorate(OverlayScene? scene, Func<Vector3, Vector3> roomToRegion, bool inEyes)
    {
        if (_sheet != null && Placement.Dragging)
            scene = (scene ?? new OverlayScene()) with { Beams = (scene?.Beams ?? []).Concat(Placement.Outline(roomToRegion)).ToArray() };
        if (_sheet is { } sh && (sh.Image.Frost > 0 || inEyes))
            scene = (scene ?? new OverlayScene()) with
            {
                Reader = new OverlaySheet(sh.Image, Corners: sh.Corners.Select(roomToRegion).ToArray(), Content: inEyes),
            };
        if (_beam is not { } b) return scene;
        var a = roomToRegion(b.From);
        var c = roomToRegion(b.To);
        if (Vector3.DistanceSquared(a, c) < 1e-6f) return scene;
        var cue = PanelLook.InUse.Light(0.55f);
        var beams = new List<OverlayBeam>(scene?.Beams ?? []) { new(a + Vector3.Normalize(c - a) * 0.04f, c, 0.003f, cue) };
        if (!_onSheet) return (scene ?? new OverlayScene()) with { Beams = beams };
        var dots = new List<OverlayDot>(scene?.Dots ?? []) { new(c, Math.Clamp(0.006f * Vector3.Distance(a, c), 0.006f, 0.02f), cue with { W = 0.9f }) };
        return (scene ?? new OverlayScene()) with { Beams = beams, Dots = dots };
    }

    void End()
    {
        if (_down && !_moving) Reader.PressUp(null);
        if (_moving) Placement.CancelGrab();
        _down = _moving = false;
    }

    /// <summary>Where it rests while it follows: 1.2 m out where the head faces, its bottom edge 2 degrees above the
    /// panel's top (18 below the eyes), its top no higher than 12 above: the panel and the reader are separate layers,
    /// and a reader reaching down behind the panel was drawn over it (2026-10-27).</summary>
    static (Vector3, Quaternion) Rest(Vector3 anchor, float yaw, PanelPlacement.Mode mode, Vector2 size)
    {
        float half = MathF.Atan(size.Y / 2 / Distance);
        float pitch = MathF.Min(-16 * MathF.PI / 180 + half, 12 * MathF.PI / 180 - half);
        return PanelPlacement.Toward(anchor, yaw, pitch, Distance);
    }

    /// <summary>The headset's own recentring moved the room: an open reader comes up in front again.</summary>
    public void Recentred() { if (Reader.Open) _place = true; }

    /// <summary>For checking without a headset: its corners in the room, and a point on it by logical pixels.</summary>
    public Vector3[]? Corners()
    {
        if (_latest is not { } r || !Reader.Open) return null;
        return PanelPlacement.Corners(_pos, _rot, new Vector2(r.LogicalWidth, r.LogicalHeight) * Mpp);
    }

    public Vector3? PointAt(float x, float y)
    {
        if (_latest is not { } r || !Reader.Open) return null;
        return _pos + Vector3.Transform(new Vector3((x - r.LogicalWidth / 2) * Mpp, (r.LogicalHeight / 2 - y) * Mpp, 0), _rot);
    }

    // --- the desktop ---------------------------------------------------------------------------------------------------

    readonly System.Diagnostics.Stopwatch _deskWatch = System.Diagnostics.Stopwatch.StartNew();
    double _deskClock;
    Vector2? _deskAt;

    /// <summary>The reader in the panel's place (open, and nothing more pressing there): its picture as a panel raster
    /// whose one key is ×.</summary>
    public (OverlayImage Image, Panel.Raster Raster)? DeskFrame(float scale)
    {
        Reader.ViewHeight = Reader.BaseViewHeight;
        Reader.Wide = 1;
        if (Frame(scale) is not { } r) return null;
        var keys = new[] { new PanelKey(KeyKind.Hide, 0, r.Close) };
        return (r.Image, new Panel.Raster(r.Image.Rgba, r.Image.Width, r.Image.Height, r.Scale, keys, r.Version, null, Array.Empty<PanelChoice>(), false));
    }

    /// <summary>The mouse on it this frame (logical pixels; null: not on it, or it isn't up).</summary>
    public void DeskPoint(Vector2? at)
    {
        double now = _deskWatch.Elapsed.TotalSeconds;
        float dt = (float)Math.Min(0.1, now - _deskClock);
        _deskClock = now;
        _deskAt = at;
        Reader.Tick(dt);
        Reader.Point(at, _deskClock);
    }

    /// <summary>The mouse is on it (a click or the wheel is its).</summary>
    public bool DeskOn => _deskAt != null && Reader.Open;

    public void DeskDown() { if (_deskAt is { } a) Reader.PressDown(a, _deskClock); }
    public void DeskUp() => Reader.PressUp(_deskAt);
}

/// <summary>The reader's wiring, kept out of Program.cs: its API for the assistant, the panel's stack key (the session's
/// replies), a notice's read key (a notecard given), and the replies growing while they're open.</summary>
public static class ReaderWiring
{
    public static ReaderView Start(Sojourn.Core.Agents.AgentSession session, AssistantHost assistant, Social.SocialView social,
        Microsoft.Extensions.Logging.ILogger log)
    {
        var view = new ReaderView(new Reader(), () => assistant.Panel.LookFor(InkSheet.Reader));
        session.Api.Register(new ReaderApi(session, view.Reader, assistant.RepliesDoc));
        assistant.RepliesRequested += () => view.Reader.Show(assistant.RepliesDoc(), atEnd: true);
        view.Replies = (() => assistant.Panel.RepliesVersion, assistant.RepliesDoc);
        social.ReadNotecard = n => _ = Task.Run(async () =>
        {
            // a notecard given: kept (the notice's Keep), then found in the inventory by its name and read
            var pay = n["payload"] as System.Text.Json.Nodes.JsonObject;
            string name = (string?)pay?["item_name"] ?? "";
            void Note(string what) => session.Events.Log("reader", new System.Text.Json.Nodes.JsonObject { ["what"] = what, ["name"] = name });
            if (Guid.TryParse((string?)pay?["object_id"], out var direct) && direct != Guid.Empty && await TryOpen(direct.ToString(), session, view)) return;
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(1000);
                try
                {
                    // the newest notecard of that name in the Notecards folder (where a kept one lands), its contents
                    // asked for afresh
                    var c = session.Client;
                    var folder = session.Inventory.FolderFor(LibreMetaverse.FolderType.Notecard);
                    if (c?.Inventory.Store is not { } store) continue;
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    try { await c.Inventory.FolderContentsAsync(folder, c.Self.AgentID, true, true, LibreMetaverse.InventorySortOrder.ByDate, cts.Token); }
                    catch (Exception e) { Note("folder: " + e.Message); }
                    var item = store.GetContents(folder).OfType<LibreMetaverse.InventoryItem>()
                        .Where(it => it.AssetType == LibreMetaverse.AssetType.Notecard && it.Name == name)
                        .OrderByDescending(it => it.CreationDate).FirstOrDefault();
                    if (item != null && await TryOpen(item.UUID.ToString(), session, view)) return;
                }
                catch (Exception e) { Note("looking: " + e.Message); }
            }
            Note("not found");
            log.LogInformation("reader: the notecard {Name} wasn't found in the inventory", name);
        });
        return view;
    }

    static async Task<bool> TryOpen(string item, Sojourn.Core.Agents.AgentSession session, ReaderView view)
    {
        try
        {
            var r = await session.InventoryOps.ReadNotecardAsync(new System.Text.Json.Nodes.JsonObject { ["item_id"] = item }, CancellationToken.None);
            view.Reader.Show(new ReaderDoc(ReaderKind.Notecard, (string?)r["name"] ?? "", ReaderApi.Paragraphs((string?)r["text"] ?? "")), atEnd: false);
            return true;
        }
        catch (Exception e) { Console.WriteLine($"reader: couldn't read the notecard {item}: {e.Message}"); return false; }
    }
}
