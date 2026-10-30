using System.Numerics;
using Sojourn.Client.Xr;
using Sojourn.Render;
using Sojourn.Render.Realtime;

namespace Sojourn.Client.Login;

/// <summary>
/// Frames with no region: the login screen, and the headset before its room is placed in one. The room maps to the
/// renderer's coordinates as the rig's does before placement (OpenXR's axes turned to SL's, no offset), so rays and
/// sheets drawn by that mapping land where the person's hands and the headset's layers are.
/// </summary>
public static class VoidView
{
    public static Vector3 ToRegion(Vector3 xr) => new(-xr.Z, -xr.X, xr.Y);

    /// <summary>The headset's eyes this frame, in the void.</summary>
    public static FrameRequest Headset(XrFrame xf, OverlayScene? overlay = null)
    {
        var headXr = (xf.Eyes[0].Position + xf.Eyes[1].Position) * 0.5f;
        var headQ = xf.Eyes[0].Orientation;
        var inv = Quaternion.Conjugate(headQ);
        var eyes = new EyeSpec[2];
        for (int i = 0; i < 2; i++)
        {
            var e = xf.Eyes[i];
            eyes[i] = new EyeSpec(Vector3.Transform(e.Position - headXr, inv), Quaternion.Normalize(inv * e.Orientation),
                e.TanLeft, e.TanRight, e.TanUp, e.TanDown);
        }
        return new FrameRequest
        {
            RegionHandle = 0, Position = ToRegion(headXr), Width = xf.Width, Height = xf.Height, Eyes = eyes,
            Forward = ToRegion(Vector3.Transform(-Vector3.UnitZ, headQ)), Up = ToRegion(Vector3.Transform(Vector3.UnitY, headQ)),
            Overlay = overlay,
        };
    }

    /// <summary>The window's view in the void (only its overlay shows).</summary>
    public static FrameRequest Desktop(int width, int height, OverlayScene? overlay) => new()
    {
        RegionHandle = 0, Position = default, Forward = Vector3.UnitX, Width = width, Height = height, Overlay = overlay,
    };
}
