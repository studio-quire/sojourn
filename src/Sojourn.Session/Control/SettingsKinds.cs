namespace Sojourn.Session.Control;

// Kinds of setting kept in VrSettings whose machinery is the VR client's (Control/Gaze.cs, Comfort.cs, VrPilot.Aim.cs).

/// <summary>How much of where you look other people see (the `gaze` setting): EyeContact, the default, turns your
/// avatar's head the way you look and, while your view rests on someone, toward them; Direction only the way you look,
/// never naming who or what (the viewer's "Limit LookAt Distance" idea); Off sends nothing (its "Enable LookAt" off).</summary>
public enum GazeSharing { EyeContact, Direction, Off }

/// <summary>When the comfort vignette narrows the view: never (the default: comfort aids are options, the user,
/// 2026-10-27), when something else moves you, or for any motion that isn't your own body's (the stick's walking and
/// smooth turning too).</summary>
public enum Vignette { Off, Forced, All }

/// <summary>When the headset is "in mouselook": auto while a script holds the mouselook button (CONTROL_ML_LBUTTON:
/// weapons, shooting games), on always (for scripts that only check AGENT_MOUSELOOK; touching waits meanwhile, as in
/// the viewer's mouselook), or never.</summary>
public enum AimMode { Auto, On, Off }
