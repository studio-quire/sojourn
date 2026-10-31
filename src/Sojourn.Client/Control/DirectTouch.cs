using System.Collections.Concurrent;
using LibreMetaverse;
using Sojourn.Client.Assistant;
using Sojourn.Client.Xr;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Control;

public enum DirectAct { None, Touch, Sit }

/// <summary>
/// The person's own hand on the world, and where touches land.
///
/// The hand (user, 2026-10-25: an exception to acting only through the assistant, as the settings menu and
/// confirmations are): pointing with the right controller while its pointer is on (HandPointer, which hands it over in
/// the left hand's fields this was written for) and pulling its trigger, or a left click in the world on the desktop,
/// does what the viewer's left click does. An object whose click action is sit seats you on it; anything that
/// takes touches is touched (ObjectGrab when the trigger goes down, ObjectGrabUpdate while it's held and the point
/// moves across it, ObjectDeGrab when it comes up), where the ray lands on its surface; other click actions (pay, buy,
/// open, media) do nothing here (in the headset a still hold brings up the thing's card, which has them: Headset's
/// gesture below, Things/ThingCard). Every press is also the left mouse button for scripts (LBUTTON control flags), and
/// when a script has taken that button the press is only that, as in the viewer. While a confirmation is on screen or
/// the settings menu is open nothing happens: this press can't answer, open or close anything. The person's own touch
/// doesn't go through the confirmation gate (that guards the assistant's). One departure from the viewer: seated, a
/// sit object other than your seat moves you to it (the viewer makes you stand first; standing here is the assistant's).
///
/// Where touches land: the renderer's pick hits triangles, and its hit carries the face, surface and texture
/// coordinates, normal and binormal (RegionScene.Pick). The agent API's requestTouch asks <see cref="FindAsync"/>:
/// a ray from the eye to the point it was given, else to where the pointer last landed on that object, else to its
/// middle, counting only that object's prims. Main thread, except FindAsync.
/// </summary>
public sealed class DirectTouch
{
    const float Reach = 64;
    readonly AgentSession _s;
    readonly FrameRenderer _r;
    readonly Pointer _pointer;
    readonly ConcurrentQueue<Action> _main = new();

    public DirectTouch(AgentSession session, FrameRenderer renderer, Pointer pointer)
    {
        _s = session;
        _r = renderer;
        _pointer = pointer;
    }

    /// <summary>Main thread, every frame.</summary>
    public void Drain()
    {
        while (_main.TryDequeue(out var a)) a();
    }

    // --- where the assistant's touches land (AgentSession.FindTouchSurface) ---------------------------------------

    public async Task<TouchPoint?> FindAsync(UUID prim, LibreMetaverse.Vector3? at, CancellationToken ct)
    {
        var done = new TaskCompletionSource<TouchPoint?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _main.Enqueue(() =>
        {
            try { done.TrySetResult(Find(prim, at is { } a ? V(a) : null)); }
            catch { done.TrySetResult(null); }
        });
        try { return await done.Task.WaitAsync(TimeSpan.FromSeconds(1), ct); }
        catch (TimeoutException) { return null; }
    }

    /// <summary>Where a touch on one of the person's HUD prims lands (Hud/HudView), or null if it isn't one.</summary>
    public Func<UUID, TouchPoint?>? FindHud { get; set; }

    TouchPoint? Find(UUID id, Vector3? at)
    {
        if (FindHud?.Invoke(id) is { } onHud) return onHud;
        if (_s.Client is not { Network.CurrentSim: { } sim } c || _s.Region is not { } w || w.PrimById(id) is not { } prim) return null;
        _s.Objects.IsAttachment(w, prim, out _, out var root);
        var set = new HashSet<UUID> { root.ID };
        foreach (var p in w.Prims.Values) if (p.ParentID == root.LocalID) set.Add(p.ID);
        var eye = _s.State.CameraOverride is { } cam ? new Vector3(cam.Pos.X, cam.Pos.Y, cam.Pos.Z) : V(c.Self.SimPosition) + new Vector3(0, 0, 0.7f);
        var middle = _r.DrawnPosition(sim.Handle, prim.LocalID) ?? V(w.RegionPosition(prim));
        var aims = new List<Vector3>();
        if (at is { } a) aims.Add(a);
        else if (_pointer.Recent(root.ID, 120) is { Region: var hr } h && hr == sim.Handle) aims.Add(h.Point);
        aims.Add(middle);
        foreach (var target in aims)
        {
            var dir = target - eye;
            if (dir.LengthSquared() < 1e-6f) continue;
            if (_r.PickPrims(sim.Handle, eye, dir, dir.Length() + 4, p => set.Contains(p.ID)) is { } hit) return Point(hit);
        }
        return null;
    }

    static TouchPoint Point(PickHit h) => new(h.Region, h.LocalId, h.Id, Surface(h));

    static TouchSurface Surface(PickHit h) => h.Surface is { } s
        ? new TouchSurface(s.Face, L(s.ST), L(s.UV), L(h.Point), L(s.Normal), L(s.Binormal))
        : TouchSurface.None with { Position = L(h.Point) };

    static LibreMetaverse.Vector3 L(Vector3 v) => new(v.X, v.Y, v.Z);
    static LibreMetaverse.Vector2 L(Vector2 v) => new(v.X, v.Y);
    static Vector3 V(LibreMetaverse.Vector3 v) => new(v.X, v.Y, v.Z);

    // --- what a press does -------------------------------------------------------------------------------------------

    /// <summary>What pressing on this hit would do, as LLToolPie::handleLeftClickPick decides.</summary>
    public DirectAct Classify(PickHit? hit)
    {
        if (hit is not { Kind: PickKind.Object } h || _s.Client is not { } c) return DirectAct.None;
        if (_s.World.Region(_s.GridKey, h.Region) is not { } w || w.PrimById(h.Id) is not { } prim) return DirectAct.None;
        bool attached = _s.Objects.IsAttachment(w, prim, out _, out var root);
        var flags = _s.State.PrimFlagsOf(prim) | (root != prim ? _s.State.PrimFlagsOf(root) : PrimFlags.None);
        // click actions count on objects in the world, not on what people wear (useClickAction)
        int action = attached ? 0 : (int)(prim.ClickAction != 0 ? prim.ClickAction : root.ClickAction);
        bool mySeat = c.Self.SittingOn != 0 && h.Region == c.Network.CurrentSim?.Handle
            && (c.Self.SittingOn == prim.LocalID || c.Self.SittingOn == root.LocalID || (w.Prims.TryGetValue(c.Self.SittingOn, out var seat) && seat.ParentID == root.LocalID));
        switch (action)
        {
            case 1 when !mySeat: return DirectAct.Sit;
            case 2 or 4 or 5 or 6 or 7 or 8 or 9: return DirectAct.None;   // buy, open, play, media, zoom, disabled, ignore
            case 3 when (flags & PrimFlags.Money) != 0: return DirectAct.None;   // pay: the viewer's pay dialog
        }
        return (flags & PrimFlags.Touch) != 0 ? DirectAct.Touch : DirectAct.None;
    }

    (ulong Region, uint Local, UUID Id, float Distance, Vector3 Offset, TouchSurface Last, long At, long Sent)? _grab;
    bool _down, _mouselook;

    public bool Held => _down;

    public bool ButtonTaken(bool mouselook) =>
        (_s.ControlsTaken & (mouselook ? ScriptControlChange.MouseLookLeftButton : ScriptControlChange.LeftButton)) != 0;

    /// <summary>The button goes down with the ray on <paramref name="hit"/>.</summary>
    public void Press(PickHit? hit, Vector3 origin, Vector3 dir, bool mouselook)
    {
        if (_down) return;
        _down = true;
        _mouselook = mouselook;
        Button(true);
        if (ButtonTaken(mouselook) || hit is not { } h) return;
        switch (Classify(h))
        {
            case DirectAct.Sit:
                if (_s.World.Region(_s.GridKey, h.Region) is { } w && w.PrimById(h.Id) is { } prim)
                {
                    // the click's offset from the prim, in its frame, as the viewer's sit sends it (no sit target: you sit near it)
                    var rot = w.RegionRotation(prim);
                    var local = L(h.Point - V(w.RegionPosition(prim))) * LibreMetaverse.Quaternion.Inverse(rot);
                    _s.SendSit(h.Region, h.Id, local);
                }
                break;
            case DirectAct.Touch:
            {
                var s = Surface(h);
                var rw = _s.World.Region(_s.GridKey, h.Region);
                var offset = rw?.PrimById(h.Id) is { } p ? h.Point - V(rw.RegionPosition(p)) : Vector3.Zero;
                _s.SendGrab(h.Region, h.LocalId, s);
                long now = Environment.TickCount64;
                _grab = (h.Region, h.LocalId, h.Id, h.Distance, offset, s, now, now);
                break;
            }
        }
    }

    /// <summary>While the button is held: a touch follows the ray across the object (the viewer's drag of a
    /// non-physical object), at most 20 times a second and only when something changed.</summary>
    public void Hold(ulong region, Vector3 origin, Vector3 dir)
    {
        if (!_down) return;
        Button(true);
        if (_grab is not { } g) return;
        long now = Environment.TickCount64;
        if (now - g.Sent < 50) return;
        var s = SurfaceOn(g.Id, region, origin, dir);
        var grabAt = origin + Vector3.Normalize(dir) * g.Distance;
        if (s == g.Last) return;
        _s.SendGrabUpdate(g.Region, g.Id, L(g.Offset), L(grabAt), (uint)Math.Min(now - g.At, uint.MaxValue), s);
        _grab = g with { Last = s, At = now, Sent = now };
    }

    /// <summary>The button comes up: the touch ends where the ray is now on the object (nowhere if it's left it).</summary>
    public void Release(ulong region, Vector3 origin, Vector3 dir)
    {
        if (!_down) return;
        _down = false;
        if (_grab is { } g) _s.SendDeGrab(g.Region, g.Local, SurfaceOn(g.Id, region, origin, dir));
        _grab = null;
        Button(false);
    }

    TouchSurface SurfaceOn(UUID id, ulong region, Vector3 origin, Vector3 dir) =>
        region != 0 && _r.PickPrims(region, origin, dir, Reach, p => p.ID == id) is { } h ? Surface(h) : TouchSurface.None;

    /// <summary>The left button for scripts (AGENT_CONTROL_LBUTTON, or the mouselook one), as the viewer sends it on
    /// every click in the world: down while held, up once.</summary>
    public void Button(bool down)
    {
        if (_s.Client is not { Network.Connected: true } c) return;
        var mv = c.Self.Movement;
        if (down)
        {
            bool was = _mouselook ? mv.MLButtonDown : mv.LButtonDown;
            if (_mouselook) mv.MLButtonDown = true; else mv.LButtonDown = true;
            if (!was) mv.SendUpdate(true);
            return;
        }
        mv.LButtonDown = mv.MLButtonDown = false;
        if (_mouselook) mv.MLButtonUp = true; else mv.LButtonUp = true;
        mv.SendUpdate(true);
        mv.LButtonUp = mv.MLButtonUp = false;
    }

    // --- the headset ---------------------------------------------------------------------------------------------
    // A press on a thing waits to see what it is, as a press on a page does (Media/MediaInput.Gesture; the user's ask,
    // 2026-10-31: a still hold brings up what can be done with the thing, the viewer's right click). Let go before it
    // has moved or been held still to OpenMs, it's a click where it went down (the touch, sit and left button all sent
    // then); moved past MoveDeg, the press goes in where it began and follows the ray (a drag, as before); held still
    // past HoldMs a ring fills round the dot, and still at OpenMs the card comes up (LongPress) and the press sends
    // nothing. A script that has taken the left button gets the press at once, as ever, and nothing holds. What it
    // costs: a touch-and-hold script sees a still press only once it moves or is let go.

    enum Gesture { None, Pending, Hold, Live, Opened }
    Gesture _g;
    PickHit _downHit;
    Vector3 _downOrigin, _downDir;
    long _downAt;
    const float MoveDeg = 1.6f;
    const int HoldMs = 450, OpenMs = 900;
    bool _armed = true;

    /// <summary>A still hold on a thing lasted long enough: the card of what can be done with it (Things/ThingCard),
    /// with the hit, and the ray it came along, where the press went down.</summary>
    public Action<PickHit, Vector3, Vector3>? LongPress { get; set; }

    /// <summary>A still hold has begun on a thing (the ring starts): its card may start finding out about it.</summary>
    public Action<PickHit>? Holding { get; set; }

    /// <summary>How far a still hold is toward the card (0-1, for the ring), while one is.</summary>
    public float? Filling => _g == Gesture.Hold ? Math.Clamp((Environment.TickCount64 - _downAt - HoldMs) / (float)(OpenMs - HoldMs), 0, 1) : null;

    void Cancel()
    {
        if (_down) Release(0, default, default);
        _g = Gesture.None;
    }

    /// <summary>The pointing hand's ray and trigger this frame (in the left hand's fields: HandPointer.World); adds the
    /// cue to the overlay (the hand's beam is the pointer's own). panel: the assistant's layer where it floats (the ray
    /// stops there, and nothing behind it is touched). blocked: a confirmation or the menu is up.</summary>
    public OverlayScene? Headset(OverlayScene? scene, XrControllers input, VrPilot pilot, (Vector3 Pos, Quaternion Rot, Vector2 Size)? panel,
        bool blocked, bool focused)
    {
        if (!focused) { _armed = false; Cancel(); return scene; }
        if (input.LeftAim is not { } q || input.LeftAimPos is not { } at || pilot.Region == 0 || _s.Client is not { } c)
        {
            Cancel();
            return scene;
        }
        var dirXr = Vector3.Transform(-Vector3.UnitZ, q);
        bool onPanel = panel is { } pl && OnPlane(at, dirXr, pl);
        var origin = pilot.RoomToRegion(at);
        var dir = pilot.RoomDirToRegion(dirXr);
        ulong region = pilot.Region;
        float trigger = input.LeftTrigger;
        // a trigger held when the press couldn't count (a confirmation, the menu, focus coming back) waits for a release
        if (trigger < 0.35f) _armed = true;
        PickHit? hit = onPanel ? null : _r.Pick(region, origin, dir, Reach, c.Self.LocalID);
        var act = blocked ? DirectAct.None : Classify(hit);
        // something came up mid-press (a confirmation, the menu, aiming): a press still deciding sends nothing
        if (blocked && _g is Gesture.Pending or Gesture.Hold) _g = Gesture.None;
        if (_g == Gesture.None && trigger > 0.6f && _armed)
        {
            _armed = false;
            if (!blocked && !onPanel) Begin(hit, origin, dir);
        }
        else if (_g != Gesture.None && trigger < 0.35f) End(region, origin, dir);
        else if (_g != Gesture.None) Continue(region, origin, dir);
        return Cue(scene, origin, dir, hit, act, trigger, blocked || onPanel);
    }

    void Begin(PickHit? hit, Vector3 origin, Vector3 dir)
    {
        if (LongPress == null || ButtonTaken(false) || hit is not { Kind: PickKind.Object } h)
        {
            Press(hit, origin, dir, mouselook: false);
            _g = Gesture.Live;
            return;
        }
        (_g, _downHit, _downOrigin, _downDir, _downAt) = (Gesture.Pending, h, origin, Vector3.Normalize(dir), Environment.TickCount64);
    }

    void Continue(ulong region, Vector3 origin, Vector3 dir)
    {
        switch (_g)
        {
            case Gesture.Live:
                Hold(region, origin, dir);
                return;
            case Gesture.Pending or Gesture.Hold:
            {
                float deg = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(dir), _downDir), -1, 1)) * 180 / MathF.PI;
                long held = Environment.TickCount64 - _downAt;
                if (deg > MoveDeg)
                {
                    // moved: the press goes in where it began, then follows the ray
                    Press(_downHit, _downOrigin, _downDir, mouselook: false);
                    _g = Gesture.Live;
                    Hold(region, origin, dir);
                }
                else if (held >= OpenMs)
                {
                    _g = Gesture.Opened;
                    LongPress?.Invoke(_downHit, _downOrigin, _downDir);
                }
                else if (_g == Gesture.Pending && held >= HoldMs)
                {
                    _g = Gesture.Hold;
                    Holding?.Invoke(_downHit);
                }
                return;
            }
        }
    }

    void End(ulong region, Vector3 origin, Vector3 dir)
    {
        // let go before it moved or opened: a click where it went down
        if (_g is Gesture.Pending or Gesture.Hold) Press(_downHit, _downOrigin, _downDir, mouselook: false);
        if (_down) Release(region, origin, dir);
        _g = Gesture.None;
    }

    static bool OnPlane(Vector3 from, Vector3 dir, (Vector3 Pos, Quaternion Rot, Vector2 Size) pl)
    {
        var n = Vector3.Transform(Vector3.UnitZ, pl.Rot);
        float denom = Vector3.Dot(dir, n);
        if (denom > -1e-4f) return false;
        float t = Vector3.Dot(pl.Pos - from, n) / denom;
        if (t <= 0) return false;
        var p = from + dir * t - pl.Pos;
        return MathF.Abs(Vector3.Dot(p, Vector3.Transform(Vector3.UnitX, pl.Rot))) <= pl.Size.X / 2
            && MathF.Abs(Vector3.Dot(p, Vector3.Transform(Vector3.UnitY, pl.Rot))) <= pl.Size.Y / 2;
    }

    // the cue: pale, so it reads apart from the assistant's amber dot; a dot for a touch, a ring for a seat, at the end
    // of the pointer's beam
    static Vector4 Ink => PanelLook.InUse.Light(0.9f);

    OverlayScene? Cue(OverlayScene? scene, Vector3 origin, Vector3 dir, PickHit? hit, DirectAct act, float trigger, bool quiet)
    {
        bool holding = _grab != null;
        var fill = Filling;
        if (quiet || (act == DirectAct.None && !holding && fill == null)) return scene;
        float d = hit?.Distance ?? 3;
        var end = origin + dir * d;
        var beams = new List<OverlayBeam>(scene?.Beams ?? []);
        var dots = new List<OverlayDot>(scene?.Dots ?? []);
        float r = PointerDot.Radius(d, 0.006f);
        if (act == DirectAct.Touch || holding) dots.Add(new OverlayDot(end, holding ? r * 1.35f : r, Ink));
        else if (act == DirectAct.Sit) Ring(beams, end, origin, r * 1.7f, r * 0.35f);
        // a still hold on its way to the card: the page's ring (it opens when the ring closes)
        if (fill is { } f) Sojourn.Client.Media.MediaInput.Fill(beams, end, origin, r * 2.6f, f);
        return (scene ?? new OverlayScene()) with { Beams = beams, Dots = dots };
    }

    static void Ring(List<OverlayBeam> beams, Vector3 at, Vector3 eye, float radius, float width)
    {
        var toEye = Vector3.Normalize(eye - at);
        var a = Vector3.Normalize(Vector3.Cross(toEye, MathF.Abs(toEye.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
        var b = Vector3.Cross(toEye, a);
        const int n = 20;
        for (int i = 0; i < n; i++)
        {
            float t0 = i * MathF.Tau / n, t1 = (i + 1) * MathF.Tau / n;
            beams.Add(new OverlayBeam(at + (a * MathF.Cos(t0) + b * MathF.Sin(t0)) * radius, at + (a * MathF.Cos(t1) + b * MathF.Sin(t1)) * radius, width, Ink));
        }
    }

    // --- the desktop -------------------------------------------------------------------------------------------------

    /// <summary>A left click in the world (not on the panel, the menu or a key): the assistant's pointer ray is the mouse's.</summary>
    public void DesktopDown(bool mouselook)
    {
        var (region, origin, dir) = _pointer.Ray;
        Press(mouselook ? null : _pointer.CurrentHit, origin, dir, mouselook);
    }

    public void DesktopUp()
    {
        var (region, origin, dir) = _pointer.Ray;
        Release(region, origin, dir);
    }

    /// <summary>Each desktop frame: a held touch follows the mouse; the cue at the pointer when a click would act.</summary>
    public OverlayScene? Desktop(OverlayScene? scene, bool blocked)
    {
        var (region, origin, dir) = _pointer.Ray;
        if (_down) Hold(region, origin, dir);
        var hit = _pointer.CurrentHit;
        var act = blocked ? DirectAct.None : Classify(hit);
        if (hit is not { } h || (act == DirectAct.None && _grab == null)) return scene;
        var dots = new List<OverlayDot>(scene?.Dots ?? []);
        var beams = new List<OverlayBeam>(scene?.Beams ?? []);
        float r = PointerDot.Radius(h.Distance, 0.009f);
        if (act == DirectAct.Sit) Ring(beams, h.Point, origin, r * 1.6f, r * 0.3f);
        else dots.Add(new OverlayDot(h.Point, _grab != null ? r * 1.35f : r, Ink));
        return (scene ?? new OverlayScene()) with { Beams = beams, Dots = dots };
    }
}
