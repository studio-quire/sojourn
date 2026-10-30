using System.Numerics;
using Silk.NET.OpenXR;
using Action = Silk.NET.OpenXR.Action;
using Quaternion = System.Numerics.Quaternion;

namespace Sojourn.Client.Xr;

// OpenXR's Session, not the Sojourn.Session namespace (which a bare name in Sojourn.* finds first)
using Session = Silk.NET.OpenXR.Session;

/// <summary>The controllers this frame. Sticks are -1..1 (x right, y forward); aims are in the play space.</summary>
public struct XrControllers
{
    public Vector2 LeftStick, RightStick;
    public bool A, B, X, Y, LeftStickClick, RightStickClick, Menu;
    public Quaternion? LeftAim, RightAim;
    public Vector3? LeftAimPos, RightAimPos;
    /// <summary>The grip poses (where the hands hold the controllers), for the own avatar's arms.</summary>
    public Quaternion? LeftHand, RightHand;
    public Vector3? LeftHandPos, RightHandPos;
    /// <summary>The right grip (talk to the assistant) and trigger (press what the right hand points at), the left
    /// grip (push-to-talk to residents) and trigger (touch or sit on what the left hand points at), 0..1.</summary>
    public float RightGrip, RightTrigger, LeftGrip, LeftTrigger;
}

/// <summary>
/// One action set for moving about, bound for Touch controllers (the Quest's; runtimes map other controllers onto
/// it): left stick moves, right stick turns (and climbs or sinks while flying), A jumps or climbs, B toggles flying,
/// left stick click toggles running, X shows or hides the assistant's panel, Y recentres, the left menu button opens the
/// settings; the right grip talks to the assistant, the right trigger presses; the left grip is voice chat's
/// push-to-talk and the right stick's click its mute (open mic); the left trigger touches or sits on what the left hand
/// points at (Control/DirectTouch).
/// </summary>
public sealed unsafe class XrInput : IDisposable
{
    readonly XR _xr;
    readonly Session _session;
    readonly ActionSet _set;
    readonly Action _move, _turn, _a, _b, _x, _y, _moveClick, _turnClick, _menu, _aim, _grip, _talk, _select, _voice, _touch;
    readonly ulong _left, _right;
    readonly Space _leftAim, _rightAim, _leftGrip, _rightGrip;

    public XrInput(XrRuntime rt, Session session)
    {
        _xr = rt.Api;
        _session = session;
        _left = rt.Path("/user/hand/left");
        _right = rt.Path("/user/hand/right");

        var sci = new ActionSetCreateInfo { Type = StructureType.ActionSetCreateInfo, Priority = 0 };
        XrRuntime.Put(sci.ActionSetName, 64, "moving");
        XrRuntime.Put(sci.LocalizedActionSetName, 128, "Moving");
        ActionSet set;
        XrRuntime.Check(_xr.CreateActionSet(rt.Instance, &sci, &set), "xrCreateActionSet");
        _set = set;

        _move = Create("move", "Move", ActionType.Vector2fInput);
        _turn = Create("turn", "Turn", ActionType.Vector2fInput);
        _a = Create("jump", "Jump or climb", ActionType.BooleanInput);
        _b = Create("fly", "Fly", ActionType.BooleanInput);
        _x = Create("panel", "Show or hide the assistant", ActionType.BooleanInput);
        _y = Create("recentre", "Recentre", ActionType.BooleanInput);
        _moveClick = Create("run", "Run", ActionType.BooleanInput);
        _turnClick = Create("voice_mute", "Mute voice chat", ActionType.BooleanInput);
        _menu = Create("settings", "Settings", ActionType.BooleanInput);
        _aim = Create("aim", "Hand pointing", ActionType.PoseInput, both: true);
        _grip = Create("hand", "Hand", ActionType.PoseInput, both: true);
        _talk = Create("talk", "Talk to the assistant", ActionType.FloatInput);
        _select = Create("select", "Press what you point at", ActionType.FloatInput);
        _voice = Create("voice_talk", "Talk in voice chat", ActionType.FloatInput);
        _touch = Create("touch", "Touch or sit on what you point at", ActionType.FloatInput);

        var bindings = new (Action Action, string Path)[]
        {
            (_move, "/user/hand/left/input/thumbstick"),
            (_turn, "/user/hand/right/input/thumbstick"),
            (_a, "/user/hand/right/input/a/click"),
            (_b, "/user/hand/right/input/b/click"),
            (_x, "/user/hand/left/input/x/click"),
            (_y, "/user/hand/left/input/y/click"),
            (_moveClick, "/user/hand/left/input/thumbstick/click"),
            (_turnClick, "/user/hand/right/input/thumbstick/click"),
            (_menu, "/user/hand/left/input/menu/click"),
            (_grip, "/user/hand/left/input/grip/pose"),
            (_grip, "/user/hand/right/input/grip/pose"),
            (_aim, "/user/hand/left/input/aim/pose"),
            (_aim, "/user/hand/right/input/aim/pose"),
            (_talk, "/user/hand/right/input/squeeze/value"),
            (_select, "/user/hand/right/input/trigger/value"),
            (_voice, "/user/hand/left/input/squeeze/value"),
            (_touch, "/user/hand/left/input/trigger/value"),
        };
        var sb = bindings.Select(b => new ActionSuggestedBinding { Action = b.Action, Binding = rt.Path(b.Path) }).ToArray();
        fixed (ActionSuggestedBinding* p = sb)
        {
            var ps = new InteractionProfileSuggestedBinding
            {
                Type = StructureType.InteractionProfileSuggestedBinding,
                InteractionProfile = rt.Path("/interaction_profiles/oculus/touch_controller"),
                CountSuggestedBindings = (uint)sb.Length,
                SuggestedBindings = p,
            };
            XrRuntime.Check(_xr.SuggestInteractionProfileBinding(rt.Instance, &ps), "xrSuggestInteractionProfileBindings");
        }

        var attach = new SessionActionSetsAttachInfo { Type = StructureType.SessionActionSetsAttachInfo, CountActionSets = 1, ActionSets = &set };
        XrRuntime.Check(_xr.AttachSessionActionSets(session, &attach), "xrAttachSessionActionSets");
        _leftAim = PoseSpace(_aim, _left);
        _rightAim = PoseSpace(_aim, _right);
        _leftGrip = PoseSpace(_grip, _left);
        _rightGrip = PoseSpace(_grip, _right);
    }

    Action Create(string name, string label, ActionType type, bool both = false)
    {
        var ci = new ActionCreateInfo { Type = StructureType.ActionCreateInfo, ActionType = type };
        XrRuntime.Put(ci.ActionName, 64, name);
        XrRuntime.Put(ci.LocalizedActionName, 128, label);
        var hands = stackalloc ulong[2] { _left, _right };
        if (both) { ci.CountSubactionPaths = 2; ci.SubactionPaths = hands; }
        Action a;
        XrRuntime.Check(_xr.CreateAction(_set, &ci, &a), $"xrCreateAction {name}");
        return a;
    }

    Space PoseSpace(Action action, ulong hand)
    {
        var ci = new ActionSpaceCreateInfo
        {
            Type = StructureType.ActionSpaceCreateInfo,
            Action = action,
            SubactionPath = hand,
            PoseInActionSpace = new Posef { Orientation = new Quaternionf(0, 0, 0, 1) },
        };
        Space s;
        XrRuntime.Check(_xr.CreateActionSpace(_session, &ci, &s), "xrCreateActionSpace");
        return s;
    }

    /// <summary>Read the controllers for a frame. Unfocused (a system menu is up), everything reads as released.</summary>
    public XrControllers Sync(Space space, long time, bool focused)
    {
        var c = new XrControllers();
        if (!focused) return c;
        var active = new ActiveActionSet { ActionSet = _set };
        var si = new ActionsSyncInfo { Type = StructureType.ActionsSyncInfo, CountActiveActionSets = 1, ActiveActionSets = &active };
        var r = _xr.SyncAction(_session, &si);
        if (r != Result.Success) return c;   // SessionNotFocused and the like
        c.LeftStick = Stick(_move);
        c.RightStick = Stick(_turn);
        c.A = Button(_a); c.B = Button(_b); c.X = Button(_x); c.Y = Button(_y);
        c.LeftStickClick = Button(_moveClick);
        c.RightStickClick = Button(_turnClick);
        c.Menu = Button(_menu);
        (c.LeftAim, c.LeftAimPos) = Aim(_leftAim, space, time);
        (c.RightAim, c.RightAimPos) = Aim(_rightAim, space, time);
        (c.LeftHand, c.LeftHandPos) = Aim(_leftGrip, space, time);
        (c.RightHand, c.RightHandPos) = Aim(_rightGrip, space, time);
        c.RightGrip = Value(_talk);
        c.RightTrigger = Value(_select);
        c.LeftGrip = Value(_voice);
        c.LeftTrigger = Value(_touch);
        return c;
    }

    Vector2 Stick(Action a)
    {
        var gi = new ActionStateGetInfo { Type = StructureType.ActionStateGetInfo, Action = a };
        var st = new ActionStateVector2f { Type = StructureType.ActionStateVector2f };
        if (_xr.GetActionStateVector2(_session, &gi, &st) != Result.Success || st.IsActive == 0) return default;
        return new Vector2(st.CurrentState.X, st.CurrentState.Y);
    }

    bool Button(Action a)
    {
        var gi = new ActionStateGetInfo { Type = StructureType.ActionStateGetInfo, Action = a };
        var st = new ActionStateBoolean { Type = StructureType.ActionStateBoolean };
        return _xr.GetActionStateBoolean(_session, &gi, &st) == Result.Success && st.IsActive != 0 && st.CurrentState != 0;
    }

    float Value(Action a)
    {
        var gi = new ActionStateGetInfo { Type = StructureType.ActionStateGetInfo, Action = a };
        var st = new ActionStateFloat { Type = StructureType.ActionStateFloat };
        return _xr.GetActionStateFloat(_session, &gi, &st) == Result.Success && st.IsActive != 0 ? st.CurrentState : 0;
    }

    // A controller left still goes to sleep (or the runtime switches to hands) after a few seconds, and its pose turns
    // invalid: the hand vanished and the pointer went, sooner than in other games (the user, 2026-10-28). Like them, a
    // hand holds its last pose for a while (the room doesn't move, so it stays where it was put down) before it's gone.
    const double HoldLost = 20;
    readonly Dictionary<ulong, (Quaternion Q, Vector3? P, double At)> _last = new();
    readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    (Quaternion?, Vector3?) Aim(Space hand, Space space, long time)
    {
        double now = _clock.Elapsed.TotalSeconds;
        _last.TryGetValue(hand.Handle, out var last);
        bool held = last.At > 0 && now - last.At < HoldLost;
        var loc = new SpaceLocation { Type = StructureType.SpaceLocation };
        if (_xr.LocateSpace(hand, space, time, &loc) != Result.Success || (loc.LocationFlags & SpaceLocationFlags.OrientationValidBit) == 0)
            return held ? (last.Q, last.P) : (null, null);
        var q = loc.Pose.Orientation;
        var p = loc.Pose.Position;
        var quat = new Quaternion(q.X, q.Y, q.Z, q.W);
        if ((loc.LocationFlags & SpaceLocationFlags.PositionValidBit) == 0) return (quat, held ? last.P : null);
        var pos = new Vector3(p.X, p.Y, p.Z);
        _last[hand.Handle] = (quat, pos, now);
        return (quat, pos);
    }

    public void Dispose()
    {
        _xr.DestroySpace(_leftAim);
        _xr.DestroySpace(_rightAim);
        _xr.DestroySpace(_leftGrip);
        _xr.DestroySpace(_rightGrip);
        _xr.DestroyActionSet(_set);
    }
}
