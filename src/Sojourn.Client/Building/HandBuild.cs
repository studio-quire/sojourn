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
using Matrix4x4 = System.Numerics.Matrix4x4;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Building;

/// <summary>The person's building-by-hand choices, kept in build-settings.json beside vr-settings.json. Snapping off
/// by default (placing by hand is free); when on, the viewer's grid (0.5 m) and 15 degree turns.</summary>
public sealed class HandBuildSettings
{
    public bool Snap { get; set; }
    public float Grid { get; set; } = 0.5f;
    public float Angle { get; set; } = 15;

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
/// Building by hand in the headset and on the desktop (the user's idea, 2026-10-30: the assistant places a thing
/// roughly, the person fine-tunes it by hand; docs/vr.md > Controls > Building by hand, docs/building.md > By hand).
/// The rules, the network and undo are Session/Building/HandMove's; this is the hands, the drawing and the readout.
///
/// On (the thing's card's "move by hand", the assistant, menu > moving > build by hand): the pointer's ray is up as for
/// a confirmation, and the triggers grab instead of touching. A hand inside something the person may move (its prims'
/// boxes, 3 cm to spare) grabs it with that hand's trigger (the left too, before pointing to teleport); the right ray on
/// one grabs it from afar. Held, it keeps the pose it had to the hand (the grip's for a reach, the ray's from afar):
/// it moves and turns with the hand, walking carries it. The holding hand's stick pushes it away or pulls it in (up,
/// down; faster the further it is) and turns it about the vertical (left, right; with snap, a step a flick); its A or X
/// toggles fine, where the hand's moves and turns count a fifth from there. Let go of the trigger and it's placed.
/// While held it's drawn where the hand has it (MotionSmoother.Held) until the region confirms it or 4 s pass.
///
/// What's grabbable under the hand or ray gets a faint box, what's held or chosen a brighter one; the readout (a small
/// sheet beside the thing, placed when it changes, then staying put in the world as the card does) gives where it is,
/// how it's turned, what came of the move, and snap, fine, undo and × (done). Main thread. Claude Opus 5.5, 2026-11-02.
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

    /// <summary>Fine: the hand's moves and turns count this much.</summary>
    const float FineScale = 0.2f;
    const float ReachMargin = 0.03f;

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
        s.Api.Register(new HandBuildApi(s, _m));
    }

    public HandMove Move => _m;
    public bool On => _m.On;
    public bool Holding => _held != null || _desk != null;

    void Save() => Settings.Save(_path, _log);

    /// <summary>The thing's card's "move by hand": building by hand on, with it chosen.</summary>
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
        Focus(root.ID);
    }

    /// <summary>The menu's and the API's switch.</summary>
    public void Set(bool on)
    {
        if (on) _m.Start(UUID.Zero, "the switch"); else _m.Stop("the switch");
    }

    // --- what's going on --------------------------------------------------------------------------------------------

    UUID _focus;                 // what the readout is about: the last grabbed, else the chosen
    string? _note;               // what came of the last grab or move
    bool _grabbedOnce;           // the hint goes once something's been grabbed
    readonly Dictionary<UUID, int> _moves = new();   // hand moves this time on, for the readout's undo
    UUID _settling;              // let go, drawn where it was put until the region says
    long _settleUntil;
    ulong _region;
    long _version;
    bool _placeReadout = true;

    void Changed() => _version++;

    void Focus(UUID id)
    {
        if (_focus != id) _placeReadout = true;
        _focus = id;
        Changed();
    }

    void Note(string? note)
    {
        _note = note;
        Changed();
    }

    void OnChanged()
    {
        if (!_m.On)
        {
            // (HandMove let go of what was held as it stopped)
            if (_held is { } h) _r.Motion.LetGo(h.G.Root.ID);
            if (_desk is { } d) _r.Motion.LetGo(d.G.Root.ID);
            _held = null;
            _desk = null;
            if (_settling != UUID.Zero) { _r.Motion.LetGo(_settling); _settling = UUID.Zero; }
            (_focus, _note, _grabbedOnce, _placeReadout) = (UUID.Zero, null, false, true);
            _moves.Clear();
            _shown = null;
            Changed();
            return;
        }
        if (_focus == UUID.Zero && _m.Chosen != UUID.Zero) Focus(_m.Chosen);
        if (_m.Last is { } last && last.Id == _settling && last.Result != "moving")
        {
            _r.Motion.LetGo(_settling);
            _settling = UUID.Zero;
            Note(last.Result switch
            {
                "refused" => Lang.T("The region kept it where it was."),
                "moved elsewhere" => Lang.T("The region put it somewhere else."),
                "gone" => Lang.T("It's gone: returned or deleted."),
                _ => _note,
            });
        }
        Changed();
    }

    /// <summary>Why it can't be moved, in the person's language (HandMove's words; RLV's own pass as they are).</summary>
    static string? Why(string? why) => why switch
    {
        null => null,
        "not in a region" or "it's in the next region: go over to it first" or "it's gone" or "worn things aren't moved by hand yet"
            or "that's someone" or "it's locked" or "you may not modify it" or "it isn't yours: choose it on its card first" => Lang.T(why),
        _ => why,
    };

    /// <summary>Every frame (headset or desktop), first: what came back, a region left behind, a let-go's time up.</summary>
    void Tick()
    {
        while (_main.TryDequeue(out var a)) a();
        if (!_m.On) return;
        ulong here = _s.Client?.Network.CurrentSim?.Handle ?? 0;
        if (_region != 0 && here != _region) { _region = here; _m.Stop("left the region"); return; }
        _region = here;
        if (_settling != UUID.Zero && Environment.TickCount64 > _settleUntil)
        {
            _r.Motion.LetGo(_settling);
            _settling = UUID.Zero;
        }
    }

    // --- holding ------------------------------------------------------------------------------------------------------

    enum Hand { Left, Right }

    sealed class Held
    {
        public required HandMove.Grab G;
        public required Hand Hand;
        public required bool Ray;
        // the anchor: the hand's frame and the object's pose when it last changed mode (region frame)
        public Vector3 Pa, Oa;
        public Quaternion Ha, Ra;
        public bool Fine;
        public Vector3 Pos;
        public Quaternion Rot = Quaternion.Identity;
        public bool Flicked;
    }

    Held? _held;
    XrControllers _raw;
    bool _faceBefore, _leftArmed = true, _rightArmed = true, _blocked;
    Vector2 _heldStick;
    bool _faceDown;

    /// <summary>The start of a headset frame, before anything else reads the controllers: while a hand holds something
    /// its stick and its A or X are building's (nothing walks, turns, or toggles the pointer or the interface), and the
    /// left trigger that grabbed is never pointing to teleport's.</summary>
    public XrControllers Take(XrControllers c, VrPilot pilot)
    {
        Tick();
        _raw = c;
        if (!_m.On) { _held = null; return c; }
        if (c.LeftTrigger < 0.35f) _leftArmed = true;
        if (c.RightTrigger < 0.35f) _rightArmed = true;
        if (_held is { } h)
        {
            if (h.Hand == Hand.Right)
            {
                (_heldStick, _faceDown) = (c.RightStick, c.A);
                c.RightStick = default;
                c.A = false;
            }
            else
            {
                (_heldStick, _faceDown) = (c.LeftStick, c.X);
                c.LeftStick = default;
                c.X = false;
                c.LeftTrigger = 0;
            }
            return c;
        }
        // the left hand reaching into something and pulling its trigger grabs it (pointing to teleport never sees it)
        if (!_blocked && _leftArmed && c.LeftTrigger > 0.6f && c.LeftHandPos is { } lp && c.LeftHand is { } lq && pilot.Region != 0)
        {
            _leftArmed = false;
            if (Reach(pilot.RoomToRegion(lp)) is { } root && Grab(root, pilot.Region, Hand.Left, ray: false, pilot.RoomToRegion(lp), ToRegion(pilot, lq)))
                c.LeftTrigger = 0;
        }
        return c;
    }

    /// <summary>Takes hold of a prim's thing, if the person may move it. Returns whether it's held.</summary>
    bool Grab(Primitive prim, ulong region, Hand hand, bool ray, Vector3 handPos, Quaternion handRot)
    {
        var (root, why) = _m.May(region, prim.ID, chosen: false, tell: true);
        if (root == null)
        {
            Note(Why(why));
            return false;
        }
        if (_settling != UUID.Zero) { _r.Motion.LetGo(_settling); _settling = UUID.Zero; }
        var g = _m.Begin(root);
        _held = new Held
        {
            G = g, Hand = hand, Ray = ray, Pa = handPos, Ha = handRot, Oa = g.From, Ra = g.FromRot, Pos = g.From, Rot = g.FromRot, Fine = _fineNext,
        };
        _faceBefore = true;   // (a face button already down isn't a press)
        _grabbedOnce = true;
        _moves.TryAdd(root.ID, 0);
        Note(null);
        Focus(root.ID);
        _r.Motion.Hold(root.ID, region, root.LocalID, g.From, g.FromRot);
        return true;
    }

    /// <summary>The held thing this frame: from the hand, the stick and fine, snapped if snapping, to the region and
    /// drawn there. Let go once its trigger is.</summary>
    void Carry(Held h, VrPilot pilot, float dt)
    {
        var c = _raw;
        float trigger = h.Hand == Hand.Right ? c.RightTrigger : c.LeftTrigger;
        var frame = HandFrame(c, pilot, h.Hand, h.Ray);
        if (trigger < 0.35f || frame is null)
        {
            Release(h);
            return;
        }
        var (p, q) = frame.Value;
        var (pos, rot) = Follow(h, p, q);
        bool rebase = false;
        // fine: A (right) or X (left), on the press
        if (_faceDown && !_faceBefore)
        {
            // (from where the hand has it now, unsnapped: the anchor is the free pose)
            h.Fine = _fineNext = !h.Fine;
            rebase = true;
            Changed();
        }
        _faceBefore = _faceDown;
        // the stick: up pushes it away along the line from the hand (the hand's own way when it's in the hand), down
        // pulls it in; left and right turn it about the vertical
        var st = _heldStick;
        if (MathF.Abs(st.Y) > 0.2f)
        {
            var away = pos - p;
            float dist = away.Length();
            var dir = dist > 0.1f ? away / dist : Vector3.Normalize(AimForward(c, pilot, h.Hand));
            float rate = MathF.Sign(st.Y) * (MathF.Abs(st.Y) - 0.2f) / 0.8f * (0.3f + 1.0f * dist);
            float step = rate * dt;
            if (dist + step < 0.05f) step = 0.05f - dist;
            pos += dir * step;
            rebase = true;
        }
        float yaw = 0;
        if (Settings.Snap)
        {
            if (!h.Flicked && MathF.Abs(st.X) > 0.6f) { h.Flicked = true; yaw = -MathF.Sign(st.X) * Settings.Angle * MathF.PI / 180; }
            else if (MathF.Abs(st.X) < 0.3f) h.Flicked = false;
        }
        else if (MathF.Abs(st.X) > 0.2f) yaw = -MathF.Sign(st.X) * (MathF.Abs(st.X) - 0.2f) / 0.8f * MathF.PI / 2 * dt;
        if (yaw != 0)
        {
            rot = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw) * rot);
            rebase = true;
        }
        if (rebase) (h.Pa, h.Ha, h.Oa, h.Ra) = (p, q, pos, rot);
        var (sp, sr) = Settings.Snap ? Snap(pos, rot) : (pos, rot);
        bool edge = _m.AtEdge(sp);
        (h.Pos, h.Rot) = _m.Move(h.G, sp, sr);
        _r.Motion.Hold(h.G.Root.ID, h.G.Region, h.G.Root.LocalID, h.Pos, h.Rot);
        if (edge && _note == null) Note(Lang.T("At the region's edge."));
        else if (!edge && _note == Lang.T("At the region's edge.")) Note(null);
        Changed();
    }

    static Vector3 AimForward(XrControllers c, VrPilot pilot, Hand hand)
    {
        var aim = hand == Hand.Right ? c.RightAim : c.LeftAim;
        return aim is { } a ? pilot.RoomDirToRegion(Vector3.Transform(-Vector3.UnitZ, a)) : Vector3.UnitX;
    }

    /// <summary>The object's pose from the hand's: kept as it was to the hand since the anchor, or in fine, the hand's
    /// moves and turns since then at a fifth (turning about the object, not swinging it round the hand).</summary>
    static (Vector3 Pos, Quaternion Rot) Follow(Held h, Vector3 p, Quaternion q)
    {
        var delta = Quaternion.Normalize(q * Quaternion.Inverse(h.Ha));
        if (h.Fine)
        {
            var small = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, delta, FineScale));
            return (h.Oa + (p - h.Pa) * FineScale, Quaternion.Normalize(small * h.Ra));
        }
        return (p + Vector3.Transform(h.Oa - h.Pa, delta), Quaternion.Normalize(delta * h.Ra));
    }

    void Release(Held h)
    {
        _held = null;
        LetGo(h.G, h.Pos, h.Rot);
        _placeReadout = true;
    }

    /// <summary>Let go (a hand or the mouse): drawn where it was put until the region says (or 4 s); one that didn't
    /// move goes back to the region's word at once.</summary>
    void LetGo(HandMove.Grab g, Vector3 pos, Quaternion rot)
    {
        _m.End(g, pos, rot);
        if (_m.Last is { } l && l.Id == g.Root.ID && l.Result == "moving" && (DateTime.UtcNow - l.At).TotalSeconds < 1)
        {
            _settling = g.Root.ID;
            _settleUntil = Environment.TickCount64 + 4000;
            _moves[g.Root.ID] = _moves.GetValueOrDefault(g.Root.ID) + 1;
        }
        else _r.Motion.LetGo(g.Root.ID);
        Changed();
    }

    /// <summary>Snapped: the position to the grid in the region's axes, the turn to the step in each of the edit
    /// floater's angles.</summary>
    (Vector3, Quaternion) Snap(Vector3 pos, Quaternion rot)
    {
        float g = Math.Max(0.001f, Settings.Grid);
        var sp = new Vector3(MathF.Round(pos.X / g) * g, MathF.Round(pos.Y / g) * g, MathF.Round(pos.Z / g) * g);
        float a = Math.Max(0.1f, Settings.Angle) * MathF.PI / 180;
        new LibreMetaverse.Quaternion(rot.X, rot.Y, rot.Z, rot.W).GetEulerAngles(out float roll, out float pitch, out float yaw);
        float R(float x) => MathF.Round(x / a) * a;
        var q = LibreMetaverse.Quaternion.CreateFromEulers(R(roll), R(pitch), R(yaw));
        return (sp, Quaternion.Normalize(new Quaternion(q.X, q.Y, q.Z, q.W)));
    }

    /// <summary>A hand's frame in the region: the grip for a reach, the ray for a grab from afar.</summary>
    static (Vector3, Quaternion)? HandFrame(XrControllers c, VrPilot pilot, Hand hand, bool ray)
    {
        var (pos, rot) = hand == Hand.Right ? (ray ? c.RightAimPos : c.RightHandPos, ray ? c.RightAim : c.RightHand)
            : (ray ? c.LeftAimPos : c.LeftHandPos, ray ? c.LeftAim : c.LeftHand);
        if (pos is not { } p || rot is not { } q || pilot.Region == 0) return null;
        return (pilot.RoomToRegion(p), ToRegion(pilot, q));
    }

    /// <summary>A rotation of the play space as one of the region (the hand's axes carried across as the pilot carries
    /// directions; the map from room to region is itself a rotation, so this is one).</summary>
    public static Quaternion ToRegion(VrPilot pilot, Quaternion room)
    {
        var x = pilot.RoomDirToRegion(Vector3.Transform(Vector3.UnitX, room));
        var y = pilot.RoomDirToRegion(Vector3.Transform(Vector3.UnitY, room));
        var z = pilot.RoomDirToRegion(Vector3.Transform(Vector3.UnitZ, room));
        var m = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    // --- what's under the hand ------------------------------------------------------------------------------------------

    /// <summary>The smallest prim whose box (3 cm to spare) holds the point, in the region you're in, not worn and not
    /// someone: the prim, whose thing HandMove then judges.</summary>
    Primitive? Reach(Vector3 at)
    {
        if (_s.Region is not { } w) return null;
        Primitive? best = null;
        float bestVol = float.MaxValue;
        foreach (var p in w.Prims.Values)
        {
            var s = p.Scale;
            float r = 0.5f * MathF.Sqrt(s.X * s.X + s.Y * s.Y + s.Z * s.Z) + ReachMargin;
            if (p.ParentID != 0 && !w.Prims.TryGetValue(p.ParentID, out var parent)) continue;
            if (w.Avatars.ContainsKey(p.LocalID)) continue;
            var lp = w.RegionPosition(p);
            var c = new Vector3(lp.X, lp.Y, lp.Z);
            if (Vector3.DistanceSquared(c, at) > r * r) continue;
            var lr = w.RegionRotation(p);
            var local = Vector3.Transform(at - c, Quaternion.Inverse(new Quaternion(lr.X, lr.Y, lr.Z, lr.W)));
            if (MathF.Abs(local.X) > s.X / 2 + ReachMargin || MathF.Abs(local.Y) > s.Y / 2 + ReachMargin || MathF.Abs(local.Z) > s.Z / 2 + ReachMargin) continue;
            float vol = s.X * s.Y * s.Z;
            if (vol < bestVol) (best, bestVol) = (p, vol);
        }
        return best;
    }

    (UUID Root, bool Ok)? _hover;   // what the hand or ray is on, and whether it may be grabbed
    long _hoverAt;

    // --- the headset's world ------------------------------------------------------------------------------------------

    /// <summary>The headset's world each frame, after the menus have had the ray: the right hand's grab (a reach, else
    /// the ray on a thing), what's held carried, the boxes and the ray's cue. world: the controllers as the world's
    /// touch sees them (the right trigger only while the ray is out in the world freely). blocked: a confirmation or
    /// the menu is up (no new grabs; what's held stays held).</summary>
    public OverlayScene? Headset(OverlayScene? scene, XrControllers world, VrPilot pilot, bool blocked, bool hidden, bool focused, float dt)
    {
        _blocked = blocked || hidden || !focused;
        if (!_m.On) return scene;
        if (_held is { } h)
        {
            if (!focused) Release(h); else Carry(h, pilot, dt);
        }
        var c = _raw;
        // the right hand: reaching into a thing (looked for ten times a second, and on the pull), else its ray on one
        long now = Environment.TickCount64;
        _hover = null;
        if (_held == null && c.RightHandPos is { } rp && c.RightHand is { } rq && pilot.Region != 0)
        {
            bool pull = !_blocked && _rightArmed && world.LeftTrigger > 0.6f;
            if (pull || now - _hoverAt > 100)
            {
                _reach = Reach(pilot.RoomToRegion(rp));
                _hoverAt = now;
            }
            PickHit? hit = null;
            if (_reach == null && world.LeftAim is { } aq && world.LeftAimPos is { } ap)
                hit = _r.Pick(pilot.Region, pilot.RoomToRegion(ap), pilot.RoomDirToRegion(Vector3.Transform(-Vector3.UnitZ, aq)), 64, _s.Client?.Self.LocalID ?? 0);
            var prim = _reach ?? (hit is { Kind: PickKind.Object } oh && _s.Region?.PrimById(oh.Id) is { } hp ? hp : null);
            if (pull)
            {
                _rightArmed = false;
                if (prim == null) { }
                else if (_reach != null) Grab(prim, pilot.Region, Hand.Right, ray: false, pilot.RoomToRegion(rp), ToRegion(pilot, rq));
                else if (c.RightAimPos is { } a && c.RightAim is { } aq2) Grab(prim, pilot.Region, Hand.Right, ray: true, pilot.RoomToRegion(a), ToRegion(pilot, aq2));
            }
            else if (prim != null && _m.May(pilot.Region, prim.ID, chosen: false).Root is { } root) _hover = (root.ID, true);
        }
        if (hidden) return scene;
        return Outlines(scene, pilot.Region);
    }

    Primitive? _reach;

    // --- the boxes ----------------------------------------------------------------------------------------------------

    readonly Dictionary<UUID, (Vector3 Min, Vector3 Max, long At)> _bounds = new();

    /// <summary>The thing's box in its root's frame: every prim's box carried into it.</summary>
    (Vector3 Min, Vector3 Max)? Bounds(UUID rootId)
    {
        long now = Environment.TickCount64;
        if (_bounds.TryGetValue(rootId, out var b) && now - b.At < 2000) return (b.Min, b.Max);
        if (_s.Region is not { } w || w.PrimById(rootId) is not { } root) return null;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        void Add(Vector3 pos, Quaternion rot, LibreMetaverse.Vector3 s)
        {
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? -s.X : s.X, (i & 2) == 0 ? -s.Y : s.Y, (i & 4) == 0 ? -s.Z : s.Z) * 0.5f;
                var at = pos + Vector3.Transform(corner, rot);
                min = Vector3.Min(min, at);
                max = Vector3.Max(max, at);
            }
        }
        Add(Vector3.Zero, Quaternion.Identity, root.Scale);
        foreach (var p in w.Prims.Values)
            if (p.ParentID == root.LocalID && !w.Avatars.ContainsKey(p.LocalID))
                Add(new Vector3(p.Position.X, p.Position.Y, p.Position.Z), Quaternion.Normalize(new Quaternion(p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W)), p.Scale);
        _bounds[rootId] = (min, max, now);
        if (_bounds.Count > 64) _bounds.Clear();
        return (min, max);
    }

    /// <summary>Where a root is drawn: where the hand has it, else where the region does.</summary>
    (Vector3 Pos, Quaternion Rot)? Pose(UUID rootId)
    {
        if (_held is { } h && h.G.Root.ID == rootId) return (h.Pos, h.Rot);
        if (_desk is { } d && d.G.Root.ID == rootId) return (d.Pos, d.Rot);
        if (_s.Region is not { } w || w.PrimById(rootId) is not { } root) return null;
        var p = w.RegionPosition(root);
        var q = w.RegionRotation(root);
        return (new Vector3(p.X, p.Y, p.Z), Quaternion.Normalize(new Quaternion(q.X, q.Y, q.Z, q.W)));
    }

    OverlayScene? Outlines(OverlayScene? scene, ulong region)
    {
        if (_s.Client?.Network.CurrentSim?.Handle != region) return scene;
        var beams = new List<OverlayBeam>(scene?.Beams ?? []);
        var eye = _eye;
        void Box(UUID id, float alpha)
        {
            if (Pose(id) is not { } pose || Bounds(id) is not { } b) return;
            var light = Sojourn.Session.Assistant.PanelLook.InUse.Light(alpha);
            Span<Vector3> v = stackalloc Vector3[8];
            for (int i = 0; i < 8; i++)
                v[i] = pose.Pos + Vector3.Transform(new Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z), pose.Rot);
            float w = Math.Clamp(0.0015f * Vector3.Distance(eye, pose.Pos), 0.0015f, 0.03f);
            for (int i = 0; i < 8; i++)
                for (int bit = 1; bit < 8; bit <<= 1)
                    if ((i & bit) == 0) beams.Add(new OverlayBeam(v[i], v[i | bit], w, light));
        }
        var drawn = new HashSet<UUID>();
        if (_held is { } h) { Box(h.G.Root.ID, 0.9f); drawn.Add(h.G.Root.ID); }
        if (_desk is { } d) { Box(d.G.Root.ID, 0.9f); drawn.Add(d.G.Root.ID); }
        if (_hover is { Ok: true } hv && drawn.Add(hv.Root)) Box(hv.Root, 0.45f);
        if (_m.Chosen != UUID.Zero && drawn.Add(_m.Chosen)) Box(_m.Chosen, 0.7f);
        return (scene ?? new OverlayScene()) with { Beams = beams };
    }

    Vector3 _eye;

    // --- the readout --------------------------------------------------------------------------------------------------

    sealed record Drawn(OverlayImage Image, List<ReadoutHit> Keys, Vector2 Logical);
    volatile Drawn? _latest;
    volatile bool _drawing;
    (long Version, int Hover, float Scale, bool Layer, object Look)? _drawnKey;
    int _hoverKey = -1;
    long _imageVersion;

    ReadoutView View()
    {
        string title = Lang.T("building by hand");
        Vector3? pos = null, euler = null;
        if (_focus != UUID.Zero && Pose(_focus) is { } p)
        {
            title = _s.Region?.PrimById(_focus)?.Properties?.Name is { Length: > 0 } n ? n : _held?.G.Name ?? Lang.T("an object");
            (pos, euler) = (p.Pos, HandMove.Euler(p.Rot));
        }
        string? snap = Settings.Snap ? Lang.F("grid {0} m · turns {1}°", Settings.Grid.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
            Settings.Angle.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)) : null;
        bool fine = _held?.Fine ?? _desk?.Fine ?? _fineNext;
        string? hint = _grabbedOnce ? null : _desktop
            ? Lang.T("Drag a thing to move it; with Shift, up and down. The wheel turns it.")
            : Lang.T("Reach into a thing, or point at it, and hold the trigger. While you hold it, the stick pushes, pulls and turns it, and A or X slows it for fine moves.");
        return new ReadoutView(title, pos, euler, snap, fine, _focus != UUID.Zero && _moves.GetValueOrDefault(_focus) > 0, _note, hint, _hoverKey);
    }

    bool _fineNext;   // fine pressed with nothing held: the next grab starts fine
    bool _desktop;

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
                _fineNext = !_fineNext;   // (the keys take no press while something's held)
                break;
            case ReadoutKey.Undo:
                Undo();
                break;
        }
        Changed();
    }

    /// <summary>The readout's undo: the last step on the thing it's about, through the dispatcher as anyone's undo (so
    /// one that would delete a creation still waits for the person's press on its confirmation).</summary>
    void Undo()
    {
        var id = _focus;
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

    const float Near = 0.45f, Far = 1.2f;
    (ulong Region, Vector3 Pos, Vector3 Eyes)? _placed;
    Vector2 _size;
    Quaternion _rot = Quaternion.Identity, _headRot = Quaternion.Identity;
    (OverlayImage Image, Vector3 Pos, Quaternion Rot, float Width)? _shown;
    (Vector3 From, Vector3 To)? _beam;
    ReadoutKey? _down;
    bool _keysArmed;

    /// <summary>The right ray is on the readout this frame (the panel, boards and world behind wait).</summary>
    public bool RightOn { get; private set; }

    /// <summary>Each headset frame, with the boards' hands (the pointer's right, live while building): places the
    /// readout, reads the ray and trigger on its keys. hidden: a confirmation waits or the interface is hidden.</summary>
    public void Readout(XrControllers boards, VrPilot pilot, Vector3 head, Quaternion headRot, (Vector3 Pos, Quaternion Rot, Vector2 Size)? panel,
        bool hidden, bool focused)
    {
        _headRot = headRot;
        RightOn = false;
        _beam = null;
        _shown = null;
        _desktop = false;
        _eye = pilot.RoomToRegion(head);
        if (!_m.On || hidden || pilot.Region == 0) { _keysArmed = false; _hoverKey = -1; return; }
        if (Picture(UiSize.Raster, Sojourn.Session.Assistant.GlassLight.Layers) is not { } d) return;
        if (_placeReadout && _held == null) Place(d, pilot, head);
        if (_placed is not { } placed || placed.Region != pilot.Region) { Place(d, pilot, head); placed = _placed!.Value; }
        if (d.Logical.X > 0) _size = new Vector2(_size.X, _size.X * d.Logical.Y / d.Logical.X);
        // (placed by its top edge, which stays where it was put as it grows or shrinks)
        var topEdge = pilot.RegionToRoom(placed.Pos);
        _rot = PanelPlacement.Facing(pilot.RegionToRoom(placed.Eyes), topEdge);
        var centre = topEdge - Vector3.Transform(Vector3.UnitY, _rot) * (_size.Y / 2);
        _shown = (d.Image, centre, _rot, _size.X);
        ReadoutHit? key = null;
        bool on = false;
        if (focused && _held == null && boards.RightAim is { } q && boards.RightAimPos is { } from)
        {
            var dir = Vector3.Transform(-Vector3.UnitZ, q);
            float panelT = panel is { } pl && PanelPlacement.Hit(from, dir, pl.Pos, pl.Rot, pl.Size, out var pt, out _) ? pt : float.MaxValue;
            if (PanelPlacement.Hit(from, dir, centre, _rot, _size, out float t, out var local) && t < panelT)
            {
                on = true;
                var px = new Vector2((local.X / _size.X + 0.5f) * d.Logical.X, (0.5f - local.Y / _size.Y) * d.Logical.Y);
                foreach (var k in d.Keys) if (k.Rect.Contains(px.X, px.Y)) key = k;
                _beam = (from, from + dir * t);
            }
        }
        RightOn = on || _down != null;
        int hover = key is { } hk ? d.Keys.IndexOf(hk) : -1;
        if (hover != _hoverKey) { _hoverKey = hover; Changed(); }
        if (!focused) { _keysArmed = false; return; }
        float trigger = boards.RightTrigger;
        if (trigger < 0.35f)
        {
            if (_down is { } dk && key is { } k2 && k2.Key == dk) Press(dk);
            _down = null;
            _keysArmed = true;
        }
        else if (trigger > 0.6f && _keysArmed)
        {
            _keysArmed = false;
            if (on) { _down = key?.Key; _rightArmed = false; }
        }
    }

    /// <summary>Beside the thing it's about (on the line from the eyes to a point just right of it, a little in front),
    /// else in front and low; facing you; then it stays there in the world.</summary>
    void Place(Drawn d, VrPilot pilot, Vector3 head)
    {
        _placeReadout = false;
        var headRegion = pilot.RoomToRegion(head);
        Vector3 anchor;
        if (_focus != UUID.Zero && Pose(_focus) is { } p && Bounds(_focus) is { } b)
        {
            var to = p.Pos - headRegion;
            var flat = new Vector3(to.X, to.Y, 0);
            var right = flat.LengthSquared() > 1e-6f ? Vector3.Normalize(Vector3.Cross(flat, Vector3.UnitZ)) : Vector3.UnitY;
            float radius = Math.Min(2f, (b.Max - b.Min).Length() / 2);
            anchor = p.Pos + right * (radius + 0.12f) + Vector3.UnitZ * 0.1f;
        }
        else
        {
            var fwd = pilot.RoomDirToRegion(Vector3.Transform(-Vector3.UnitZ, _headRot));
            anchor = headRegion + new Vector3(fwd.X, fwd.Y, 0) * 0.7f - Vector3.UnitZ * 0.3f;
        }
        var anchorRoom = pilot.RegionToRoom(anchor);
        var dir = anchorRoom - head;
        float dist = dir.Length();
        float at = Math.Clamp(dist - 0.1f, Near, Far);
        var pos = head + (dist > 1e-3f ? dir / dist : -Vector3.UnitZ) * at;
        _size = d.Logical * UiSize.MetresPerPx(at);
        // (kept by its top edge: the middle given here is moved down by half its height when shown)
        _placed = (pilot.Region, pilot.RoomToRegion(pos + Vector3.Transform(Vector3.UnitY, PanelPlacement.Facing(head, pos)) * (_size.Y / 2)), headRegion);
    }

    /// <summary>Its layer this frame (Map/MapWiring.HeadsetScene puts it up with the boards').</summary>
    public (string Key, OverlayImage Image, Vector3 Pos, Quaternion Rot, float Width)? Layer() =>
        _shown is { } s ? ("building readout", s.Image, s.Pos, s.Rot, s.Width) : null;

    /// <summary>The ray's cue while it's on the readout.</summary>
    public OverlayScene? Decorate(OverlayScene? scene, Func<Vector3, Vector3> roomToRegion)
    {
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
    // In the world a left drag on something you may move moves it across the level it was grabbed at (Shift: up and
    // down, in the plane facing you), the wheel turns it while held; the readout sits at the window's right.

    sealed class DeskHeld
    {
        public required HandMove.Grab G;
        public Vector3 Point, Offset;   // where the drag began on it, and the root from there
        public float Yaw;
        public bool Fine;
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 Origin, Dir;     // the ray at the start (fine counts from there)
    }

    DeskHeld? _desk;
    SkiaSharp.SKRect _deskRect;
    float _deskScale = 1;

    /// <summary>A left press in the world while building: a grab if it's on something movable. True if taken.</summary>
    public bool DesktopDown(PickHit? hit, ulong region, Vector3 origin, Vector3 dir)
    {
        if (!_m.On) return false;
        if (hit is not { Kind: PickKind.Object } h || _s.Region?.PrimById(h.Id) is not { } prim) return true;   // (building: no touches)
        var (root, why) = _m.May(region, prim.ID, chosen: false, tell: true);
        if (root == null) { Note(Why(why)); return true; }
        if (_settling != UUID.Zero) { _r.Motion.LetGo(_settling); _settling = UUID.Zero; }
        var g = _m.Begin(root);
        _desk = new DeskHeld { G = g, Point = h.Point, Offset = g.From - h.Point, Pos = g.From, Rot = g.FromRot, Fine = _fineNext, Origin = origin, Dir = dir };
        _grabbedOnce = true;
        _moves.TryAdd(root.ID, 0);
        Note(null);
        Focus(root.ID);
        _r.Motion.Hold(root.ID, region, root.LocalID, g.From, g.FromRot);
        return true;
    }

    /// <summary>Each desktop frame: a drag carried along the mouse's ray.</summary>
    public void DesktopFrame(Vector3 origin, Vector3 dir, bool vertical)
    {
        Tick();
        _desktop = true;
        if (_desk is not { } d) return;
        var n = vertical ? Vector3.Normalize(new Vector3(-dir.X, -dir.Y, 0) is var f && f.LengthSquared() > 1e-6f ? f : Vector3.UnitX) : Vector3.UnitZ;
        float denom = Vector3.Dot(dir, n);
        if (MathF.Abs(denom) < 1e-4f) return;
        float t = Vector3.Dot(d.Point - origin, n) / denom;
        if (t <= 0 || t > 256) return;
        var at = origin + dir * t;
        if (d.Fine) at = d.Point + (at - d.Point) * FineScale;
        var pos = at + d.Offset;
        var rot = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, d.Yaw) * d.G.FromRot);
        var (sp, sr) = Settings.Snap ? Snap(pos, rot) : (pos, rot);
        (d.Pos, d.Rot) = _m.Move(d.G, sp, sr);
        _r.Motion.Hold(d.G.Root.ID, d.G.Region, d.G.Root.LocalID, d.Pos, d.Rot);
        Changed();
    }

    /// <summary>The wheel while dragging turns it (a snap step a notch, else 5 degrees). True if taken.</summary>
    public bool DesktopWheel(float notches)
    {
        if (_desk is not { } d || notches == 0) return false;
        float step = (Settings.Snap ? Settings.Angle : 5) * MathF.PI / 180;
        d.Yaw += MathF.Sign(notches) * step;
        return true;
    }

    public void DesktopUp()
    {
        if (_desk is not { } d) return;
        _desk = null;
        LetGo(d.G, d.Pos, d.Rot);
    }

    /// <summary>The desktop's readout at the window's right, and the boxes.</summary>
    public (OverlayImage Image, Vector2 At)? Desktop(float ui, int width, int height, bool hidden)
    {
        _deskRect = SkiaSharp.SKRect.Empty;
        if (!_m.On || hidden) return null;
        _deskScale = ui;
        if (Picture(ui, false) is not { } d) return null;
        float w = d.Image.Width, h = d.Image.Height;
        float x = Math.Max(8, width - w - 16 * ui), y = Math.Clamp(height * 0.35f, 8, Math.Max(8, height - h - 8));
        _deskRect = new SkiaSharp.SKRect(x, y, x + w, y + h);
        return (d.Image, new Vector2(x, y));
    }

    /// <summary>The boxes on the desktop (the mouse's ray is the pointer's).</summary>
    public OverlayScene? DesktopOutlines(OverlayScene? scene, ulong region, Vector3 eye, PickHit? hit)
    {
        if (!_m.On) return scene;
        _eye = eye;
        if (_desk == null && hit is { Kind: PickKind.Object } h && _s.Region?.PrimById(h.Id) is { } prim)
        {
            var (root, _) = _m.May(region, prim.ID, chosen: false);
            _hover = root != null ? (root.ID, true) : null;
        }
        else if (_desk == null) _hover = null;
        return Outlines(scene, region);
    }

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
        if (hover != _hoverKey) { _hoverKey = hover; Changed(); }
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
        api.Add("build_by_hand", new("building", "building by hand: the triggers grab the person's things to move and turn them (a reach or the "
            + "right ray), the stick pushes, pulls and turns what's held, and a readout shows where it is; not kept between sessions. "
            + "SojournHandBuild start puts a thing in their hands", "on, off", () => OnOff(_m.On), SettingsApi.Switch("build_by_hand", Set)));
        api.Add("build_snap", new("building", "building by hand snaps what's moved to the grid and its turns to steps (off by default)", "on, off",
            () => OnOff(Settings.Snap), SettingsApi.Switch("build_snap", on => { Settings.Snap = on; Save(); Changed(); })));
        api.Add("build_grid", new("building", "the grid building by hand snaps to, metres (the viewer's 0.5 by default)", "0.01-10",
            () => JsonValue.Create(Settings.Grid), SettingsApi.Number("build_grid", 0.01f, 10, " m", f => { Settings.Grid = f; Save(); Changed(); })));
        api.Add("build_snap_angle", new("building", "the step building by hand snaps turns to, degrees (15 by default)", "1-90",
            () => JsonValue.Create(Settings.Angle), SettingsApi.Number("build_snap_angle", 1, 90, " deg", f => { Settings.Angle = f; Save(); Changed(); })));
    }

    public static readonly float[] Grids = { 0.01f, 0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f };
    public static readonly float[] Angles = { 1, 5, 10, 15, 22.5f, 30, 45, 90 };
}
