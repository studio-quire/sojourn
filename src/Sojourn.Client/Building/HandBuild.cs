using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using LibreMetaverse;
using Sojourn.Client.Assistant;
using Sojourn.Client.Control;
using Sojourn.Client.Xr;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using Sojourn.Session.Building;
using Sojourn.Session.Control;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Building;

/// <summary>The person's building-by-hand choices, kept in build-settings.json beside vr-settings.json. Snapping off
/// by default; when on, the viewer's grid (0.5 m) and 15 degree turns. The gizmo's axes the region's (world) by
/// default, as the viewer's ruler mode.</summary>
public sealed class HandBuildSettings
{
    public bool Snap { get; set; }
    public float Grid { get; set; } = 0.5f;
    public float Angle { get; set; } = 15;
    public bool Local { get; set; }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public static HandBuildSettings Load(string path, Action<string>? warn = null)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<HandBuildSettings>(File.ReadAllText(path), Json) ?? new();
        }
        catch (Exception e) { warn?.Invoke($"build settings: {e.Message} (defaults used)"); }
        return new();
    }

    public void Save(string path, Action<string>? warn = null)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, Json)); }
        catch (Exception e) { warn?.Invoke($"build settings: {e.Message}"); }
    }
}

/// <summary>
/// Building by hand in the headset and on the desktop (docs/vr.md > Controls > Building by hand, docs/building.md > By
/// hand). The second design (the user's, after their first try on 2026-11-02): everything through things you can see
/// and drag in the world, the controller's buttons and sticks keeping their usual jobs.
///
/// While it's on, a tap of the pointer on a thing the person may move selects it (a still hold still brings up its
/// card); a tap anywhere else lets it go and does what that tap always does. The pointer turned off lets it go too.
/// The selected thing carries the gizmo (Gizmo.cs): an arrow per axis to drag it along that axis only, a square per
/// pair to drag it across that plane, a ring per axis to turn it about that axis, and an orb in the middle to drag it
/// freely; the ray passes through the thing to reach them, and the one it's on lights up. Each drag is one move by
/// hand (Session/Building/HandMove: the network, undo, what came of it), drawn where it's put until the region agrees.
/// The readout, a small sheet that follows the head as the other menus do (resting right of the panel, draggable, brought
/// back by X held), says what's selected, where it is and how it's turned, what the handle under the ray does, and
/// carries the keys: snap, fine moves (a fifth as far), the axes (the region's or its own), undo, and ×; the pointer on
/// a key puts what it does in the readout's line.
/// Main thread. Claude Opus 5.5, 2026-11-02.
/// </summary>
public sealed class HandBuild
{
    readonly AgentSession _s;
    readonly FrameRenderer _r;
    readonly HandMove _m;
    readonly Func<Sojourn.Session.Assistant.PanelLook> _look;
    readonly Action<string> _log;
    readonly string _path;
    public readonly HandBuildSettings Settings;
    readonly ConcurrentQueue<Action> _main = new();

    /// <summary>Fine: moves and turns count this much.</summary>
    const float FineScale = 0.2f;

    public HandBuild(AgentSession s, FrameRenderer renderer, Func<Sojourn.Session.Assistant.PanelLook> look, string vrSettingsPath, Action<string> log)
    {
        _s = s;
        _r = renderer;
        _look = look;
        _log = log;
        _path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(vrSettingsPath)) ?? ".", "build-settings.json");
        Settings = HandBuildSettings.Load(_path, log);
        _m = new HandMove(s);
        _m.Changed += () => _main.Enqueue(OnChanged);
        s.ClaimAlerts(_m.ClaimAlert);
        s.Api.Register(new HandBuildApi(s, _m));
    }

    public HandMove Move => _m;
    public bool On => _m.On;
    public bool Dragging => _drag != null;

    /// <summary>The headset's pointer: whether it's on, and turning it on (a thing handed over by the assistant comes
    /// with the pointer to work it). Unset on the desktop.</summary>
    public Func<bool>? PointerOn { get; set; }
    public Action? PointerUp { get; set; }

    void Save() => Settings.Save(_path, _log);

    /// <summary>The thing's card's "move by hand": building by hand on, with it selected.</summary>
    public void Choose(Sojourn.Session.Objects.ThingFacts f)
    {
        var (root, why) = _m.May(f.Region, f.Id, chosen: true, tell: true);
        if (root == null)
        {
            _log($"building by hand: {f.Name} can't be moved by hand ({why})");
            if (!_m.On) _m.Start(UUID.Zero, "the thing's card");
            Note(Why(why));
            return;
        }
        _m.Start(root.ID, "the thing's card");
    }

    /// <summary>The menu's and the API's switch.</summary>
    public void Set(bool on)
    {
        if (on) _m.Start(UUID.Zero, "the switch"); else _m.Stop("the switch");
    }

    // --- what's going on --------------------------------------------------------------------------------------------

    string? _note;               // what came of the last move, or why a tap didn't select
    readonly Dictionary<UUID, int> _moves = new();   // hand moves this time on, for the readout's undo
    UUID _awaiting;              // let go, its outcome to be said when the region has had its say
    UUID _settling;              // let go, drawn where it was put until the region says (not after a refusal)
    (Vector3 Pos, Quaternion Rot) _settlePose;
    long _settleUntil;
    ulong _region;
    long _version;
    bool _wasOn, _pointerBefore;
    long _checkedAt;

    void Changed() => _version++;

    void Note(string? note)
    {
        _note = note;
        Changed();
    }

    bool _onBefore;
    UUID _selBefore;

    void OnChanged()
    {
        if (!_m.On)
        {
            (_onBefore, _selBefore) = (false, UUID.Zero);
            EndDrag();
            if (_settling != UUID.Zero) { _r.Motion.LetGo(_settling); _settling = UUID.Zero; }
            _awaiting = UUID.Zero;
            (_note, _signs) = (null, null);
            _moves.Clear();
            _shown = null;
            Changed();
            return;
        }
        // building just turned on, or something selected for them (the card, the assistant): the pointer comes on to
        // work it (it stays the person's switch after)
        if ((!_onBefore || _m.Selected != _selBefore && _m.Selected != UUID.Zero) && PointerOn is { } p && !p()) PointerUp?.Invoke();
        (_onBefore, _selBefore) = (true, _m.Selected);
        // the region refused the drag as it went: said at once (the feed doesn't show the region's own words)
        if (_drag is { G.Refusal: not null } && _note != Refusing) Note(Refusing);
        if (_m.Last is { } last && last.Id == _awaiting && last.Result != "moving")
        {
            if (_settling == _awaiting) { _r.Motion.LetGo(_settling); _settling = UUID.Zero; }
            _awaiting = UUID.Zero;
            Note(Said(last) ?? (_note == Refusing ? null : _note));
        }
        Changed();
    }

    static string Refusing => Lang.T("The land there doesn't allow it.");

    /// <summary>What came of a move, for the readout (null: placed, nothing to say).</summary>
    public static string? Said(HandMove.Outcome o) => o.Result switch
    {
        "refused" when o.Said != null => Lang.T("The land there doesn't allow it, so it stayed put."),
        "refused" => Lang.T("The region kept it where it was."),
        "partly refused" => Lang.T("The region stopped it at the edge of land that doesn't allow it."),
        "nudged" => Lang.F("The region nudged it {0} cm.", Math.Max(1, (int)MathF.Round(o.Off * 100))),
        "moved elsewhere" when o.Off >= 0.1f => Lang.F("It ended up {0} m from where you let go of it.", Math.Round(o.Off, o.Off < 10 ? 1 : 0)),
        "moved elsewhere" => Lang.F("It ended up turned {0}° from how you left it.", (int)MathF.Round(o.OffTurn)),
        "gone" => Lang.T("It's gone: returned or deleted."),
        _ => null,
    };

    /// <summary>Why it can't be moved, in the person's language (HandMove's words; RLV's own pass as they are).</summary>
    static string? Why(string? why) => why switch
    {
        null => null,
        "not in a region" or "it's in the next region: go over to it first" or "it's gone" or "worn things aren't moved by hand yet"
            or "that's someone" or "it's locked" or "you may not modify it" or "it isn't yours: choose it on its card first" => Lang.T(why),
        _ => why,
    };

    void Select(UUID root, string by)
    {
        if (root != _m.Selected) { _note = null; _signs = null; }
        _m.Select(root, by);
        Changed();
    }

    void Deselect(string why)
    {
        EndDrag();
        if (_m.Selected != UUID.Zero) Select(UUID.Zero, why);
    }

    /// <summary>Every frame (headset or desktop), first: what came back, a region left behind, a let-go's time up, a
    /// selection that's gone or may no longer be moved, the pointer turned off.</summary>
    void Tick(bool? pointer)
    {
        while (_main.TryDequeue(out var a)) a();
        if (!_m.On) { _wasOn = false; return; }
        if (!_wasOn) { _wasOn = true; _pointerBefore = pointer ?? true; }
        ulong here = _s.Client?.Network.CurrentSim?.Handle ?? 0;
        if (_region != 0 && here != _region) { _region = here; _m.Stop("left the region"); return; }
        _region = here;
        if (_settling != UUID.Zero && Environment.TickCount64 > _settleUntil)
        {
            _r.Motion.LetGo(_settling);
            _settling = UUID.Zero;
        }
        // the pointer turned off lets the selection go (the user, 2026-11-02)
        if (pointer is { } pn)
        {
            if (_pointerBefore && !pn) Deselect("the pointer off");
            _pointerBefore = pn;
        }
        long now = Environment.TickCount64;
        if (_m.Selected != UUID.Zero && _drag == null && now - _checkedAt > 250)
        {
            _checkedAt = now;
            var (root, why) = _m.May(here, _m.Selected, chosen: false);
            if (root == null)
            {
                Deselect("it can't be moved now");
                Note(Why(why));
            }
        }
    }

    // --- the gizmo ----------------------------------------------------------------------------------------------------

    sealed class Drag
    {
        public required GizmoDrag D;
        public required HandMove.Grab G;
        public Vector3 Pos;
        public Quaternion Rot;
        public float T;   // how far along the ray the handle is (the beam ends there)
        public Vector3 From, Last;   // where the ray began, at the press and now
    }

    static string F(Vector3 v) => $"{v.X:0.00}, {v.Y:0.00}, {v.Z:0.00}";

    Drag? _drag;
    GizmoFrame? _frame;
    Vector3? _signs;
    GizmoHandle? _hot;      // the handle under the ray
    float _hotT;
    UUID _hoverThing;       // a thing the ray is on that a tap would select
    Vector3 _eye;

    /// <summary>Where a root is drawn: where a drag has it, where it was let go (until the region agrees), else where
    /// the region has it.</summary>
    (Vector3 Pos, Quaternion Rot)? Pose(UUID rootId)
    {
        if (_drag is { } d && d.G.Root.ID == rootId) return (d.Pos, d.Rot);
        if (_settling == rootId) return _settlePose;
        if (_s.Region is not { } w || w.PrimById(rootId) is not { } root) return null;
        var p = w.RegionPosition(root);
        var q = w.RegionRotation(root);
        return (new Vector3(p.X, p.Y, p.Z), Quaternion.Normalize(new Quaternion(q.X, q.Y, q.Z, q.W)));
    }

    /// <summary>The selected thing's gizmo this frame, from the eye (held as it was while a drag goes on).</summary>
    GizmoFrame? FrameNow(Vector3 eye)
    {
        if (_drag is { } d) return d.D.Frame with { Centre = _m.Centre(d.G.Root.ID, d.Pos, d.Rot) };
        if (_m.Selected == UUID.Zero || Pose(_m.Selected) is not { } pose || _m.Bounds(_m.Selected) is not { } b) return null;
        var centre = _m.Centre(_m.Selected, pose.Pos, pose.Rot);
        var f = GizmoFrame.Make(centre, Settings.Local ? pose.Rot : Quaternion.Identity, (b.Max - b.Min).Length() / 2, eye, _signs);
        _signs = f.Signs;
        return f;
    }

    GizmoSnap? SnapNow => Settings.Snap ? new GizmoSnap(Math.Max(0.001f, Settings.Grid), Math.Max(0.1f, Settings.Angle) * MathF.PI / 180, !Settings.Local) : null;

    bool _fine;

    /// <summary>A drag of the handle under the ray begins (the ray from o along u, region frame).</summary>
    bool BeginDrag(Vector3 o, Vector3 u)
    {
        if (_frame is not { } f || _hot is not { } h || _m.Selected == UUID.Zero) return false;
        var (root, why) = _m.May(_s.Client?.Network.CurrentSim?.Handle ?? 0, _m.Selected, chosen: false, tell: true);
        if (root == null) { Note(Why(why)); return false; }
        if (_settling != UUID.Zero) { _r.Motion.LetGo(_settling); _settling = UUID.Zero; }
        var g = _m.Begin(root);
        _drag = new Drag { D = new GizmoDrag(h, f, g.From, g.FromRot, o, u, _hotT), G = g, Pos = g.From, Rot = g.FromRot, T = _hotT, From = o };
        _log($"building by hand: dragging its {h.Kind.ToString().ToLowerInvariant()}{(h.Kind == GizmoKind.Free ? "" : " " + AxisName[h.Axis])} "
            + $"({(Settings.Local ? "local" : "world")}{(_fine ? ", fine" : "")}{(Settings.Snap ? ", snap" : "")}), the ray from {F(o)} {_hotT:0.00} m");
        _moves.TryAdd(root.ID, 0);
        Note(null);
        _r.Motion.Hold(root.ID, g.Region, root.LocalID, g.From, g.FromRot);
        return true;
    }

    void UpdateDrag(Vector3 o, Vector3 u)
    {
        if (_drag is not { } d) return;
        d.Last = o;
        var (pos, rot) = d.D.Update(o, u, _fine ? FineScale : 1, SnapNow);
        bool edge = _m.AtEdge(pos);
        (d.Pos, d.Rot) = _m.Move(d.G, pos, rot);
        _r.Motion.Hold(d.G.Root.ID, d.G.Region, d.G.Root.LocalID, d.Pos, d.Rot);
        // (the beam stays on the handle as it goes)
        var c = _m.Centre(d.G.Root.ID, d.Pos, d.Rot);
        d.T = Math.Max(0.05f, Vector3.Dot(c - o, Vector3.Normalize(u)));
        if (edge && _note == null) Note(Lang.T("At the region's edge."));
        else if (!edge && _note == Lang.T("At the region's edge.")) Note(null);
        Changed();
    }

    /// <summary>Let go: drawn where it was put until the region says (or 4 s); one that didn't move goes back to the
    /// region's word at once.</summary>
    void EndDrag()
    {
        if (_drag is not { } d) return;
        _drag = null;
        var g = d.G;
        if (Vector3.Distance(d.Last, d.From) > 0.05f) _log($"building by hand: the ray's start moved {F(d.Last - d.From)} during the drag");
        _m.End(g, d.Pos, d.Rot);
        if (_m.Last is { } l && l.Id == g.Root.ID && l.Result == "moving" && (DateTime.UtcNow - l.At).TotalSeconds < 1)
        {
            _awaiting = g.Root.ID;
            _moves[g.Root.ID] = _moves.GetValueOrDefault(g.Root.ID) + 1;
            // a drag the region has already refused isn't held where it was let go: it shows where the region has it
            if (g.Refusal == null)
            {
                _settling = g.Root.ID;
                _settlePose = (d.Pos, d.Rot);
                _settleUntil = Environment.TickCount64 + 4000;
            }
            else _r.Motion.LetGo(g.Root.ID);
        }
        else _r.Motion.LetGo(g.Root.ID);
        Changed();
    }

    /// <summary>The handle the ray is on (none while blocked), the frame worked out from the eye.</summary>
    void Hover(Vector3 eye, Vector3? o, Vector3? u, bool live)
    {
        _eye = eye;
        _frame = FrameNow(eye);
        var before = _hot;
        if (_drag is { } d) (_hot, _hotT) = (d.D.Handle, d.T);
        else if (live && _frame is { } f && o is { } ro && u is { } ru && Gizmo.Hit(f, ro, ru) is { } h) (_hot, _hotT) = (h.Handle, h.T);
        else _hot = null;
        if (before != _hot) Changed();
    }

    /// <summary>A point on a handle of the gizmo now, in the region (kind 0 the middle, 1 an arrow, 2 a plane square, 3 a
    /// ring; axis 0-2): for the simulator's test to aim at (VrScript's rhandle). A ring's point is off the axes, on the
    /// side nearer the eye.</summary>
    public Vector3? HandlePoint(int kind, int axis)
    {
        if (_frame is not { } f) return null;
        axis = Math.Clamp(axis, 0, 2);
        switch (kind)
        {
            case 1:
            {
                var (a, b) = f.Arrow(axis);
                return Vector3.Lerp(a, b, 0.6f);
            }
            case 2:
                return f.Plane(axis).Mid;
            case 3:
            {
                var (e1, e2) = Sojourn.Client.Building.Gizmo.Basis(f.Axis(axis));
                Vector3 best = f.Centre;
                float bestD = float.MaxValue;
                for (int i = 0; i < 4; i++)
                {
                    float a = (45 + 90 * i) * MathF.PI / 180;
                    var p = f.Centre + (e1 * MathF.Cos(a) + e2 * MathF.Sin(a)) * f.R;
                    float d = Vector3.Distance(p, _eye);
                    if (d < bestD) (best, bestD) = (p, d);
                }
                return best;
            }
            default:
                return f.Centre;
        }
    }

    // --- taps (DirectTouch's hooks) ------------------------------------------------------------------------------------

    /// <summary>A press on this hit is building's: a thing the person may move (it selects it rather than touching).</summary>
    public bool Claims(PickHit? hit) => _m.On && Movable(hit) != UUID.Zero;

    UUID Movable(PickHit? hit) =>
        hit is { Kind: PickKind.Object } h && _m.May(h.Region, h.Id, chosen: false).Root is { } root ? root.ID : UUID.Zero;

    /// <summary>A tap (or a drag begun) on a thing building claims: it's selected.</summary>
    public void Tapped(PickHit hit)
    {
        var (root, why) = _m.May(hit.Region, hit.Id, chosen: false, tell: true);
        if (root == null) { Note(Why(why)); return; }
        Select(root.ID, "a tap");
    }

    /// <summary>A press anywhere else in the world: the selection goes (and the press does what it always does).</summary>
    public void Elsewhere()
    {
        if (_m.On && _drag == null) Deselect("a tap elsewhere");
    }

    // --- the headset ----------------------------------------------------------------------------------------------------

    bool _armed = true, _rayOn;

    /// <summary>The start of a headset frame, before the assistant's ray is worked out: the gizmo from the eyes and the
    /// handle the right ray is on (raw: the controllers as read). The ray goes to a handle only when that's nearer than
    /// any menu (HeadsetAssistant compares <see cref="HandleT"/>, then says through <see cref="RayOn"/>).</summary>
    public void Aim(XrControllers raw, VrPilot pilot, Vector3 head, bool pointerOn, bool hidden)
    {
        Tick(pointerOn);
        if (!_m.On || pilot.Region == 0) { _hot = null; _frame = null; return; }
        Vector3? o = null, u = null;
        if (raw.RightAim is { } q && raw.RightAimPos is { } at)
            (o, u) = (pilot.RoomToRegion(at), pilot.RoomDirToRegion(Vector3.Transform(-Vector3.UnitZ, q)));
        Hover(pilot.RoomToRegion(head), o, u, pointerOn && !hidden);
    }

    /// <summary>How far along the right ray the handle under it is (metres; null: none), or the dragged one.</summary>
    public float? HandleT => _m.On && _hot != null ? _hotT : null;

    /// <summary>Said by HeadsetAssistant each frame: the ray is the gizmo's (a handle is nearest, or a drag goes on).</summary>
    public bool RayOn { set => _rayOn = value; }

    /// <summary>The headset's world each frame, after the menus have had the ray: a press on a handle begins a drag, the
    /// drag follows the ray, the release ends it; then the gizmo and the boxes. hit: what the pointer's on out in the
    /// world (a thing a tap would select gets a faint box). blocked: a confirmation or the menu is up.</summary>
    public OverlayScene? Headset(OverlayScene? scene, XrControllers raw, VrPilot pilot, PickHit? hit, bool blocked, bool hidden, bool focused)
    {
        if (!_m.On) return scene;
        float trigger = raw.RightTrigger;
        Vector3? o = null, u = null;
        if (raw.RightAim is { } q && raw.RightAimPos is { } at)
            (o, u) = (pilot.RoomToRegion(at), pilot.RoomDirToRegion(Vector3.Transform(-Vector3.UnitZ, q)));
        if (_drag != null)
        {
            if (!focused || trigger < 0.35f || o == null || hidden || blocked) EndDrag();
            else UpdateDrag(o.Value, u!.Value);
        }
        else if (trigger > 0.6f && _armed)
        {
            _armed = false;
            if (_rayOn && !blocked && !hidden && focused && o is { } ro) BeginDrag(ro, u!.Value);
        }
        if (trigger < 0.35f) _armed = true;
        _hoverThing = _drag == null && _hot == null && !hidden ? Movable(hit) : UUID.Zero;
        if (hidden) return scene;
        return Draw(scene);
    }

    // --- drawing ------------------------------------------------------------------------------------------------------

    static readonly Vector4[] AxisInk =
    {
        new(0.95f, 0.30f, 0.28f, 0.95f),   // x red
        new(0.38f, 0.85f, 0.32f, 0.95f),   // y green
        new(0.32f, 0.55f, 1.00f, 0.95f),   // z blue (as every viewer's build tools)
    };

    static Vector4 Lit(Vector4 c) => new(c.X + (1 - c.X) * 0.55f, c.Y + (1 - c.Y) * 0.55f, c.Z + (1 - c.Z) * 0.45f, 1);

    OverlayScene? Draw(OverlayScene? scene)
    {
        var beams = new List<OverlayBeam>(scene?.Beams ?? []);
        var dots = new List<OverlayDot>(scene?.Dots ?? []);
        if (_hoverThing != UUID.Zero && _hoverThing != _m.Selected) Box(beams, _hoverThing, 0.45f);
        if (_m.Selected != UUID.Zero) Box(beams, _m.Selected, 0.28f);
        if (_frame is { } f) DrawGizmo(beams, dots, f);
        return (scene ?? new OverlayScene()) with { Beams = beams, Dots = dots };
    }

    void Box(List<OverlayBeam> beams, UUID id, float alpha)
    {
        if (Pose(id) is not { } pose || _m.Bounds(id) is not { } b) return;
        var light = Sojourn.Session.Assistant.PanelLook.InUse.Light(alpha);
        Span<Vector3> v = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
            v[i] = pose.Pos + Vector3.Transform(new Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z), pose.Rot);
        float w = Math.Clamp(0.0015f * Vector3.Distance(_eye, pose.Pos), 0.0015f, 0.03f);
        for (int i = 0; i < 8; i++)
            for (int bit = 1; bit < 8; bit <<= 1)
                if ((i & bit) == 0) beams.Add(new OverlayBeam(v[i], v[i | bit], w, light));
    }

    /// <summary>The gizmo, over everything (beams are drawn over the eye's image): rings with their far halves dimmer,
    /// arrows with stepped heads, plane squares, the orb. The handle under the ray lights and thickens; during a drag
    /// the others dim and the dragged one shows its line (an arrow's axis right across, a ring's start and now).</summary>
    void DrawGizmo(List<OverlayBeam> beams, List<OverlayDot> dots, GizmoFrame f)
    {
        float R = f.R;
        float w = Math.Clamp(0.0045f * Vector3.Distance(_eye, f.Centre), 0.0015f, 0.06f);
        var drag = _drag?.D.Handle;
        Vector4 Ink(GizmoHandle h, int axis)
        {
            var c = AxisInk[axis];
            if (_hot == h) return Lit(c);
            return drag != null ? c with { W = c.W * 0.3f } : c;
        }
        float Width(GizmoHandle h) => _hot == h ? w * 1.8f : w;
        // rings
        for (int k = 0; k < 3; k++)
        {
            var h = new GizmoHandle(GizmoKind.Ring, k);
            var ink = Ink(h, k);
            var (e1, e2) = Sojourn.Client.Building.Gizmo.Basis(f.Axis(k));
            float centreDist = Vector3.Distance(_eye, f.Centre);
            var prev = f.Centre + e1 * R;
            for (int i = 1; i <= Sojourn.Client.Building.Gizmo.RingSegments; i++)
            {
                float a = i * MathF.Tau / Sojourn.Client.Building.Gizmo.RingSegments;
                var p = f.Centre + (e1 * MathF.Cos(a) + e2 * MathF.Sin(a)) * R;
                bool far = Vector3.Distance(_eye, (prev + p) * 0.5f) > centreDist;
                beams.Add(new OverlayBeam(prev, p, Width(h), far ? ink with { W = ink.W * 0.55f } : ink));
                prev = p;
            }
        }
        // arrows: the shaft, then the head in four steps narrowing to the tip
        for (int i = 0; i < 3; i++)
        {
            var h = new GizmoHandle(GizmoKind.Arrow, i);
            var ink = Ink(h, i);
            if (Sojourn.Client.Building.Gizmo.EndOn(f.Axis(i), f.Centre - _eye)) ink = ink with { W = ink.W * 0.25f };
            var a = f.Axis(i) * f.Sign(i);
            float ww = Width(h);
            beams.Add(new OverlayBeam(f.Centre + a * (GizmoFrame.ArrowFrom * R), f.Centre + a * (GizmoFrame.ArrowTo * R), ww, ink));
            float head = Math.Max(GizmoFrame.HeadW * R, 3.5f * ww), len = (GizmoFrame.HeadTo - GizmoFrame.ArrowTo) * R / 4;
            for (int s = 0; s < 4; s++)
            {
                var from = f.Centre + a * (GizmoFrame.ArrowTo * R + s * len);
                beams.Add(new OverlayBeam(from, from + a * len, head * (1 - s * 0.24f), ink));
            }
        }
        // plane squares
        for (int k = 0; k < 3; k++)
        {
            var h = new GizmoHandle(GizmoKind.Plane, k);
            var ink = Ink(h, k);
            var (mid, pu, pv) = f.Plane(k);
            float half = GizmoFrame.PlaneHalf * R;
            var c0 = mid + (-pu - pv) * half;
            var c1 = mid + (pu - pv) * half;
            var c2 = mid + (pu + pv) * half;
            var c3 = mid + (-pu + pv) * half;
            float ww = Width(h) * 0.8f;
            beams.Add(new OverlayBeam(c0, c1, ww, ink));
            beams.Add(new OverlayBeam(c1, c2, ww, ink));
            beams.Add(new OverlayBeam(c2, c3, ww, ink));
            beams.Add(new OverlayBeam(c3, c0, ww, ink));
            if (_hot == h)
                for (int s = 1; s < 4; s++)
                    beams.Add(new OverlayBeam(c0 + (c3 - c0) * (s / 4f), c1 + (c2 - c1) * (s / 4f), ww, ink with { W = 0.6f }));
        }
        // the orb in the middle, with a rim
        {
            var h = new GizmoHandle(GizmoKind.Free);
            bool hot = _hot == h;
            float r = GizmoFrame.OrbR * R * (hot ? 1.15f : 1);
            var ink = hot ? new Vector4(1, 1, 1, 0.95f) : new Vector4(0.92f, 0.94f, 0.96f, drag != null ? 0.3f : 0.75f);
            dots.Add(new OverlayDot(f.Centre, r, ink));
            var toEye = Vector3.Normalize(_eye - f.Centre);
            var (e1, e2) = Sojourn.Client.Building.Gizmo.Basis(toEye);
            var prev = f.Centre + e1 * r;
            for (int i = 1; i <= 24; i++)
            {
                float a = i * MathF.Tau / 24;
                var p = f.Centre + (e1 * MathF.Cos(a) + e2 * MathF.Sin(a)) * r;
                beams.Add(new OverlayBeam(prev, p, w * 0.8f, new Vector4(1, 1, 1, hot ? 1 : 0.6f)));
                prev = p;
            }
        }
        // a drag's own line
        if (_drag is { } d)
        {
            var dh = d.D.Handle;
            if (dh.Kind == GizmoKind.Arrow)
            {
                var a = f.Axis(dh.Axis);
                float span = Math.Max(4 * R, 3);
                beams.Add(new OverlayBeam(f.Centre - a * span, f.Centre + a * span, w * 0.6f, AxisInk[dh.Axis] with { W = 0.4f }));
            }
            else if (dh.Kind == GizmoKind.Ring)
            {
                var a = f.Axis(dh.Axis);
                var (e1, _) = Sojourn.Client.Building.Gizmo.Basis(a);
                var now = Vector3.Transform(e1, Quaternion.CreateFromAxisAngle(a, d.D.Angle * (_fine ? FineScale : 1)));
                beams.Add(new OverlayBeam(f.Centre, f.Centre + e1 * R, w * 0.7f, AxisInk[dh.Axis] with { W = 0.45f }));
                beams.Add(new OverlayBeam(f.Centre, f.Centre + now * R, w * 0.9f, Lit(AxisInk[dh.Axis])));
            }
        }
    }

    // --- the readout --------------------------------------------------------------------------------------------------

    sealed record Drawn(OverlayImage Image, List<ReadoutHit> Keys, Vector2 Logical);
    volatile Drawn? _latest;
    volatile bool _drawing;
    (long Version, int Hover, float Scale, bool Layer, object Look)? _drawnKey;
    int _hoverKey = -1;
    long _imageVersion;

    static readonly string[] AxisName = { "x", "y", "z" };

    /// <summary>What the handle under the ray does, in a few words.</summary>
    static string Does(GizmoHandle h) => h.Kind switch
    {
        GizmoKind.Arrow => Lang.F("move along {0}", AxisName[h.Axis]),
        GizmoKind.Plane => Lang.F("move across {0} and {1}", AxisName[(h.Axis + 1) % 3], AxisName[(h.Axis + 2) % 3]),
        GizmoKind.Ring => Lang.F("turn around {0}", AxisName[h.Axis]),
        _ => Lang.T("move it freely"),
    };

    ReadoutView View()
    {
        string title = Lang.T("building by hand");
        Vector3? pos = null, euler = null;
        var sel = _drag?.G.Root.ID ?? _m.Selected;
        if (sel != UUID.Zero && Pose(sel) is { } p)
        {
            title = _s.Region?.PrimById(sel)?.Properties?.Name is { Length: > 0 } n ? n : _drag?.G.Name ?? Lang.T("an object");
            (pos, euler) = (p.Pos, HandMove.Euler(p.Rot));
        }
        string grid = Settings.Grid.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string angle = Settings.Angle.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        string? snap = Settings.Snap ? Lang.F("grid {0} m · turns {1}°", grid, angle) : null;
        var undoOn = sel != UUID.Zero && _moves.GetValueOrDefault(sel) > 0;
        // the line (the user, 2026-11-02: the keys explained there): what the key under the pointer does, unless it was
        // just pressed and has something to say; else what came of things, what the handle under the ray does, or a
        // short hint
        var keys = _latest?.Keys;
        ReadoutKey? hovered = _hoverKey >= 0 && keys != null && _hoverKey < keys.Count ? keys[_hoverKey].Key : null;
        string? about = hovered is { } hk && !(_pressedHere && _note != null) ? About(hk, Settings.Snap, _fine, Settings.Local, grid, angle) : null;
        string line = about ?? _note ?? (_hot is { } h ? Does(h)
            : sel == UUID.Zero ? Lang.T("Tap one of your things to move it.")
            : Lang.T("Point at a key to see what it does."));
        bool strong = about != null || _note != null || _hot != null;
        return new ReadoutView(title, pos, euler, snap, _fine, Settings.Local, undoOn, line, strong, _hoverKey);
    }

    /// <summary>What a key of the readout does, as things stand (its line while the pointer is on it).</summary>
    internal static string About(ReadoutKey key, bool snap, bool fine, bool local, string grid, string angle) => key switch
    {
        ReadoutKey.Done => Lang.T("Stop building by hand."),
        ReadoutKey.Snap => snap
            ? Lang.F("Snap is on: moves go in {0} m steps, turns in {1}° steps. Press to move freely.", grid, angle)
            : Lang.F("Snap is off. Press to move in {0} m steps and turn in {1}° steps.", grid, angle),
        ReadoutKey.Fine => fine
            ? Lang.T("Fine moves are on: a drag goes a fifth as far. Press for full moves.")
            : Lang.T("Press for fine moves: a drag goes a fifth as far, for small adjustments."),
        ReadoutKey.Local => local
            ? Lang.T("The handles turn with the thing, along its own axes. Press for the region's axes.")
            : Lang.T("The handles follow the region's axes: x east, y north, z up. Press for the thing's own axes."),
        ReadoutKey.Undo => Lang.T("Undo the last move or turn of this thing."),
        _ => "",
    };

    bool _pressedHere;   // the key under the pointer was pressed (its note, if any, shows in place of what it does)

    Drawn? Picture(float scale, bool layer)
    {
        if (!_m.On) { _latest = null; _drawnKey = null; return null; }
        var look = _look();
        var key = (_version, _hoverKey, scale, layer, (object)look);
        if (!_drawing && key != _drawnKey)
        {
            _drawing = true;
            _drawnKey = key;
            var view = View();
            Task.Run(() =>
            {
                try
                {
                    var (px, w, h, used, keys) = HandBuildPainter.Draw(view, look, scale, layer);
                    var img = new OverlayImage(px, w, h, Interlocked.Increment(ref _imageVersion), look.Frost, look.TintOpacity);
                    _latest = new Drawn(img, keys, new Vector2(w / used, h / used));
                }
                catch (Exception e) { Console.Error.WriteLine($"building by hand: couldn't draw the readout ({e.GetType().Name}: {e.Message})"); }
                finally { _drawing = false; }
            });
        }
        return _latest;
    }

    void Press(ReadoutKey key)
    {
        _pressedHere = true;
        if (key != ReadoutKey.Undo) _note = null;
        switch (key)
        {
            case ReadoutKey.Done:
                _m.Stop("its done key");
                return;
            case ReadoutKey.Snap:
                Settings.Snap = !Settings.Snap;
                Save();
                _log($"building by hand: snap {(Settings.Snap ? "on" : "off")}");
                break;
            case ReadoutKey.Fine:
                _fine = !_fine;
                break;
            case ReadoutKey.Local:
                Settings.Local = !Settings.Local;
                _signs = null;
                Save();
                break;
            case ReadoutKey.Undo:
                Undo();
                break;
        }
        Changed();
    }

    /// <summary>The readout's undo: the last step on the selected thing, through the dispatcher as anyone's undo (so
    /// one that would delete a creation still waits for the person's press on its confirmation).</summary>
    void Undo()
    {
        var id = _m.Selected;
        if (id == UUID.Zero) return;
        var args = new JsonObject { ["op"] = "undo", ["steps"] = 1, ["id"] = id.ToString() };
        _ = Task.Run(async () =>
        {
            string? note;
            try
            {
                JsonNode? r;
                if (!_s.Private.On) r = await _s.Api.CallAsync("SojournBuild", args, reply: true, TimeSpan.FromSeconds(150), CancellationToken.None);
                else if (_s.Api.Gate?.Invoke("SojournBuild", "undo", args) is { } ask && await ask() is { } refusal) r = new JsonObject { ["error"] = refusal };
                else r = await _s.Api.ApiMap["SojournBuild"].Ops["undo"].Run(args, CancellationToken.None);
                note = r?["error"] is JsonValue e && e.TryGetValue<string>(out var err) ? Lang.F("Didn't work: {0}", err) : Lang.T("Undone.");
                if (r?["error"] == null) _main.Enqueue(() => _moves[id] = Math.Max(0, _moves.GetValueOrDefault(id) - 1));
            }
            catch (Exception e) { note = Lang.F("Didn't work: {0}", e.Message); }
            _main.Enqueue(() => Note(note));
        });
    }

    // Where the readout sits (the user, 2026-11-02, after two tries at placing it by the thing and then near the person:
    // like every other menu): it follows the head lazily with the panel (PanelPlacement), resting in the column right of
    // the panel and the settings menu where the view cards go, its bottom 30 degrees below the eyes; the trigger held on
    // it (off its keys) and moved puts it somewhere, kept between sessions as "building readout"; X held brings it back
    // with the other menus. It comes up afresh each time building comes on.
    public readonly PanelPlacement Placement = new("building readout", (anchor, yaw, _, size) => PanelPlacement.RightOfPanel(anchor, yaw, size));
    Vector2 _size;
    (OverlayImage Image, Vector3 Pos, Quaternion Rot, float Width)? _shown;
    (Vector3 From, Vector3 To)? _beam;
    ReadoutKey? _down;
    bool _keysArmed, _visible, _onSheet, _dragging;

    /// <summary>The right ray is on the readout this frame (the panel, boards and world behind wait).</summary>
    public bool RightOn { get; private set; }

    /// <summary>Its size (metres) while it's up and resting in the column right of the panel: a thing's card rests above
    /// it there.</summary>
    public Vector2? Resting => _shown != null && Placement.FollowingYaw != null ? _size : null;

    /// <summary>Each headset frame, with the boards' hands (the pointer's right while it's on): places the readout,
    /// reads the ray and trigger on its keys, and drags it by its body. besidePanel: the panel's resting place while it
    /// shows and follows. hidden: a confirmation waits or the interface is hidden.</summary>
    public void Readout(XrControllers boards, VrPilot pilot, Vector3 head, Quaternion headRot, (Vector3 Pos, Quaternion Rot, Vector2 Size)? panel,
        PanelPlacement.Leader? besidePanel, bool hidden, bool focused, float dt)
    {
        RightOn = false;
        _beam = null;
        _shown = null;
        _desktop = false;
        if (!_m.On) _visible = false;
        if (!_m.On || hidden || pilot.Region == 0)
        {
            _keysArmed = false;
            _hoverKey = -1;
            _onSheet = false;
            EndSheetDrag(cancel: true);
            return;
        }
        if (Picture(UiSize.Raster, Sojourn.Session.Assistant.GlassLight.Layers) is not { } d) return;
        _roomToRegion ??= pilot.RoomToRegion;
        if (!_visible) { Placement.Place(); _visible = true; }
        if (d.Logical.X > 0) _size = d.Logical * UiSize.MetresPerPx(PanelPlacement.Distance);
        var (pos, rot) = Placement.Update(head, headRot, _size, dt, frozen: _onSheet || _down != null || _dragging, leader: besidePanel);
        Vector3? from = boards.RightAimPos;
        Vector3? dir = boards.RightAim is { } q ? Vector3.Transform(-Vector3.UnitZ, q) : null;
        if (_dragging && from is { } df && dir is { } dd && Placement.Drag(df, dd)) (pos, rot) = Placement.Pose;
        _shown = (d.Image, pos, rot, _size.X);
        ReadoutHit? key = null;
        float hitT = 0;
        _onSheet = false;
        if (focused && _drag == null && from is { } o && dir is { } u)
        {
            float panelT = panel is { } pl && PanelPlacement.Hit(o, u, pl.Pos, pl.Rot, pl.Size, out var pt, out _) ? pt : float.MaxValue;
            if (PanelPlacement.Hit(o, u, pos, rot, _size, out float t, out var local) && t < panelT || _dragging && PanelPlacement.Hit(o, u, pos, rot, _size, out t, out local))
            {
                _onSheet = true;
                hitT = t;
                var px = new Vector2((local.X / _size.X + 0.5f) * d.Logical.X, (0.5f - local.Y / _size.Y) * d.Logical.Y);
                if (!_dragging) foreach (var k in d.Keys) if (k.Rect.Contains(px.X, px.Y)) key = k;
                _beam = (o, o + u * t);
            }
        }
        RightOn = _onSheet || _down != null || _dragging;
        int hover = key is { } hk ? d.Keys.IndexOf(hk) : -1;
        if (hover != _hoverKey) { _hoverKey = hover; _pressedHere = false; Changed(); }
        if (!focused) { _keysArmed = false; EndSheetDrag(cancel: true); return; }
        float trigger = boards.RightTrigger;
        if (trigger < 0.35f)
        {
            if (_dragging) EndSheetDrag(cancel: false);
            else if (_down is { } dk && key is { } k2 && k2.Key == dk) Press(dk);
            _down = null;
            _keysArmed = true;
        }
        else if (trigger > 0.6f && _keysArmed)
        {
            _keysArmed = false;
            if (_onSheet)
            {
                _armed = false;
                // on a key it's that key's press; on the rest of the sheet, a drag to put it somewhere
                if (key is { } pk) _down = pk.Key;
                else if (from is { } go && dir is { } gu) { Placement.Grab(go, gu, hitT); _dragging = true; }
            }
        }
    }

    Func<Vector3, Vector3>? _roomToRegion;

    /// <summary>A point on the readout as it was last shown, in the region: across and down from its top left (0-1).
    /// For the simulator's test to aim at (VrScript's rreadout).</summary>
    public Vector3? ReadoutPoint(float u, float v)
    {
        if (_shown is not { } s || _roomToRegion is not { } toRegion) return null;
        var p = s.Pos + Vector3.Transform(new Vector3((u - 0.5f) * _size.X, (0.5f - v) * _size.Y, 0), s.Rot);
        return toRegion(p);
    }

    void EndSheetDrag(bool cancel)
    {
        if (!_dragging) return;
        _dragging = false;
        if (cancel) Placement.CancelGrab(); else Placement.Drop();
    }

    /// <summary>Its layer this frame (Map/MapWiring.HeadsetScene puts it up with the boards').</summary>
    public (string Key, OverlayImage Image, Vector3 Pos, Quaternion Rot, float Width)? Layer() =>
        _shown is { } s ? ("building readout", s.Image, s.Pos, s.Rot, s.Width) : null;

    /// <summary>The ray's cue while it's on the readout, and its resting place's outline while it's dragged.</summary>
    public OverlayScene? Decorate(OverlayScene? scene, Func<Vector3, Vector3> roomToRegion)
    {
        if (Placement.Dragging) scene = (scene ?? new OverlayScene()) with { Beams = (scene?.Beams ?? []).Concat(Placement.Outline(roomToRegion)).ToArray() };
        if (_beam is not { } b || _shown == null) return scene;
        var a = roomToRegion(b.From);
        var c = roomToRegion(b.To);
        if (Vector3.DistanceSquared(a, c) < 1e-6f) return scene;
        var cue = Sojourn.Session.Assistant.PanelLook.InUse.Light(0.75f);
        var beams = new List<OverlayBeam>(scene?.Beams ?? []) { new(a + Vector3.Normalize(c - a) * 0.04f, c, 0.004f, cue) };
        var dots = new List<OverlayDot>(scene?.Dots ?? []) { new(c, Math.Clamp(0.006f * Vector3.Distance(a, c), 0.006f, 0.02f), cue with { W = 0.9f }) };
        return (scene ?? new OverlayScene()) with { Beams = beams, Dots = dots };
    }

    // --- the desktop --------------------------------------------------------------------------------------------------
    // The same gizmo under the mouse: a press on a handle drags it, a press on a thing you may move selects it, a press
    // anywhere else lets the selection go and does what it always does. The readout sits at the window's right.

    bool _desktop;
    SkiaSharp.SKRect _deskRect;

    /// <summary>Each desktop frame: the gizmo from the camera, the handle under the mouse, a drag carried along.</summary>
    public void DesktopFrame(Vector3 eye, Vector3 origin, Vector3 dir, PickHit? hit, bool hidden)
    {
        Tick(null);
        _desktop = true;
        if (!_m.On) return;
        Hover(eye, origin, dir, !hidden);
        if (_drag != null) UpdateDrag(origin, dir);
        _hoverThing = _drag == null && _hot == null && !hidden ? Movable(hit) : UUID.Zero;
    }

    /// <summary>A left press in the world while building: true if building took it (a handle, or a thing it selects);
    /// otherwise the selection goes and the press is the world's.</summary>
    public bool DesktopDown(PickHit? hit, Vector3 origin, Vector3 dir)
    {
        if (!_m.On) return false;
        if (_hot != null) return BeginDrag(origin, dir) || true;
        if (Claims(hit)) { Tapped(hit!.Value); return true; }
        Elsewhere();
        return false;
    }

    public void DesktopUp() => EndDrag();

    /// <summary>Whether a press here would be building's (the world's cue for a touch stays away).</summary>
    public bool DesktopTakes(PickHit? hit) => _m.On && (_hot != null || _drag != null || Claims(hit));

    /// <summary>The desktop's readout at the window's right.</summary>
    public (OverlayImage Image, Vector2 At)? Desktop(float ui, int width, int height, bool hidden)
    {
        _deskRect = SkiaSharp.SKRect.Empty;
        if (!_m.On || hidden) return null;
        if (Picture(ui, false) is not { } d) return null;
        float w = d.Image.Width, h = d.Image.Height;
        float x = Math.Max(8, width - w - 16 * ui), y = Math.Clamp(height * 0.35f, 8, Math.Max(8, height - h - 8));
        _deskRect = new SkiaSharp.SKRect(x, y, x + w, y + h);
        return (d.Image, new Vector2(x, y));
    }

    /// <summary>The gizmo and the boxes on the desktop.</summary>
    public OverlayScene? DesktopOutlines(OverlayScene? scene) => _m.On ? Draw(scene) : scene;

    public bool DesktopOver(Vector2 px) => _m.On && _deskRect.Contains(px.X, px.Y);

    ReadoutHit? DeskKey(Vector2 px)
    {
        if (!DesktopOver(px) || _latest is not { } d) return null;
        var scale = d.Image.Width / d.Logical.X;
        var at = new Vector2((px.X - _deskRect.Left) / scale, (px.Y - _deskRect.Top) / scale);
        foreach (var k in d.Keys) if (k.Rect.Contains(at.X, at.Y)) return k;
        return null;
    }

    public void DesktopMove(Vector2 px)
    {
        if (!_m.On || _latest is not { } d) return;
        int hover = DeskKey(px) is { } k ? d.Keys.IndexOf(k) : -1;
        if (hover != _hoverKey) { _hoverKey = hover; _pressedHere = false; Changed(); }
    }

    /// <summary>A left press on the readout: true if it was on it.</summary>
    public bool DesktopPress(Vector2 px)
    {
        if (!DesktopOver(px)) return false;
        _down = DeskKey(px)?.Key;
        return true;
    }

    public void DesktopRelease(Vector2 px)
    {
        if (_down is { } dk && DeskKey(px) is { } k && k.Key == dk) Press(dk);
        _down = null;
    }

    // --- settings -----------------------------------------------------------------------------------------------------

    public void Settle(SettingsApi api)
    {
        static string OnOff(bool b) => b ? "on" : "off";
        api.Add("build_by_hand", new("building", "building by hand: a tap of the pointer selects one of the person's things, and its arrows, rings and "
            + "middle handle move and turn it, with a readout of where it is; not kept between sessions. SojournHandBuild start selects a thing for them",
            "on, off", () => OnOff(_m.On), SettingsApi.Switch("build_by_hand", Set)));
        api.Add("build_snap", new("building", "building by hand snaps moves to the grid and turns to steps (off by default)", "on, off",
            () => OnOff(Settings.Snap), SettingsApi.Switch("build_snap", on => { Settings.Snap = on; Save(); Changed(); })));
        api.Add("build_grid", new("building", "the grid building by hand snaps to, metres (the viewer's 0.5 by default)", "0.01-10",
            () => JsonValue.Create(Settings.Grid), SettingsApi.Number("build_grid", 0.01f, 10, " m", f => { Settings.Grid = f; Save(); Changed(); })));
        api.Add("build_snap_angle", new("building", "the step building by hand snaps turns to, degrees (15 by default)", "1-90",
            () => JsonValue.Create(Settings.Angle), SettingsApi.Number("build_snap_angle", 1, 90, " deg", f => { Settings.Angle = f; Save(); Changed(); })));
        api.Add("build_axes", new("building", "the axes building by hand's arrows and rings follow: the region's (world, the default) or the thing's own (local)",
            "world, local", () => Settings.Local ? "local" : "world",
            SettingsApi.Choice("build_axes", new[] { "world", "local" }, i => { Settings.Local = i == 1; _signs = null; Save(); Changed(); })));
        api.Add("build_fine", new("building", "building by hand's fine moves: drags move and turn a fifth as far (off; not kept between sessions)", "on, off",
            () => OnOff(_fine), SettingsApi.Switch("build_fine", on => { _fine = on; Changed(); })));
    }

    public static readonly float[] Grids = { 0.01f, 0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f };
    public static readonly float[] Angles = { 1, 5, 10, 15, 22.5f, 30, 45, 90 };
}
