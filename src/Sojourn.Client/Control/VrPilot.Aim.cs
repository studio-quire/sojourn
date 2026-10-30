using LibreMetaverse;
using Sojourn.Client.Xr;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;
using LQuaternion = LibreMetaverse.Quaternion;
using LVector3 = LibreMetaverse.Vector3;

namespace Sojourn.Client.Control;

/// <summary>
/// Weapons and mouselook games in the headset (batch B1; docs/vr.md > Controls > Aiming). The headset is always first
/// person, but the viewer only tells the region it's in mouselook in mouselook: AGENT_CONTROL_MOUSELOOK in every
/// AgentUpdate (LLAgentCamera::changeCameraToMouselook), the agent's frame pitched with the camera (LLAgent::pitch, so
/// llGetRot and llGetCameraRot point where you aim, up and down too), and a click is CONTROL_ML_LBUTTON instead of
/// CONTROL_LBUTTON, to a script that took it, with no touch (LLTool::handleMouseDown). While aiming here: the same, the
/// aim being the head (where you look, as mouselook's camera) or the right hand's ray; the body faces it; the right
/// trigger is the mouselook button (Control/Aim.cs, with a crosshair where the aim lands). Others see the head and body
/// turn to the aim, as they see anyone's in mouselook (TPV policy 2.k: nothing the viewer doesn't send).
/// Also "keep me in place" (Core's MoveLock): while it holds, the room isn't carried by what moves the avatar without
/// the person, so the view stays and the walk to the head brings the avatar back.
/// </summary>
public sealed partial class VrPilot
{
    /// <summary>Keep me in place holds now (Core's MoveLock.Holding): the room isn't carried along by a push.</summary>
    public Func<bool>? HoldRoom { get; set; }

    bool Held() => HoldRoom?.Invoke() == true;

    /// <summary>Where the head is this frame (the region it's in, and the region frame): where keep me in place brings the
    /// avatar back to from far (Core's MoveLock.ViewPlace).</summary>
    public (ulong Region, Vector3 Pos)? HeadInRegion { get; private set; }

    /// <summary>This frame the region is told the headset is in mouselook (aiming).</summary>
    public bool Aiming { get; private set; }

    /// <summary>Where the aim is from and toward this frame (the region's frame): the head or the right hand.</summary>
    public (Vector3 From, Vector3 Dir) AimRay { get; private set; }

    bool WantsAim() => Settings.Aim switch
    {
        AimMode.On => true,
        AimMode.Off => false,
        _ => (_session.ControlsTaken & ScriptControlChange.MouseLookLeftButton) != 0,
    };

    /// <summary>After the frame's movement and camera are set: mouselook's flag, the body and head turned to the aim and
    /// (aiming with the hand) the camera's axes along it, as the viewer's agent frame is in mouselook.</summary>
    void AimFrame(AgentManager.AgentMovement mv, XrControllers input, Vector3 head, Quaternion worldQ)
    {
        HeadInRegion = (_region, head);
        Aiming = WantsAim() && !Spectating;
        mv.Mouselook = Aiming;
        if (!Aiming || _session.Motion.Moving) return;   // (the agent API's walk steers the body by its facing)
        var dir = Vector3.Transform(Vector3.UnitX, worldQ);
        var from = head;
        if (Settings.AimWithHand && input.RightAim is { } hq && input.RightAimPos is { } hp)
        {
            dir = Vector3.Normalize(RoomDirToRegion(Vector3.Transform(-Vector3.UnitZ, hq)));
            from = RoomToRegion(hp);
        }
        AimRay = (from, dir);
        float yaw = MathF.Atan2(dir.Y, dir.X);
        // the at axis pitched down for a positive angle about Y (SL's frame): looking up is a negative one
        float pitch = -MathF.Asin(Math.Clamp(dir.Z, -0.999f, 0.999f));
        var q = Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, pitch));
        var lq = new LQuaternion(q.X, q.Y, q.Z, q.W);
        mv.BodyRotation = lq;
        mv.HeadRotation = lq;
        _bodyYaw = yaw;   // the body drawn, and the walk's keys, go by it
        if (Settings.AimWithHand)
        {
            var focus = head + dir;
            mv.Camera.LookAt(new LVector3(head.X, head.Y, head.Z), new LVector3(focus.X, focus.Y, focus.Z));
        }
    }
}
