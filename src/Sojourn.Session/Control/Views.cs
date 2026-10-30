using Sojourn.Render.Realtime;
using Sojourn.Render;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Session.Control;

// Where the person's eyes are this frame, as the VR client's pilots work it out (Control/Pilot.cs, VrPilot.cs): what
// world audio, voice and media listen from.

/// <summary>Where the eye is this frame, in the agent's region coordinates.</summary>
public readonly record struct CameraPose(ulong Region, Vector3 Position, Vector3 Forward, bool HideSelf);

/// <summary>What the headset shows this frame: the centre of the eyes in the agent's region and each eye relative to it.
/// Spectating: looking around away from the avatar (VrPilot.Spectate.cs), which is then drawn whole where it stands.</summary>
public readonly record struct VrView(ulong Region, Vector3 Position, Vector3 Forward, Vector3 Up, EyeSpec[] Eyes, SelfBody? Self, bool Spectating = false);
