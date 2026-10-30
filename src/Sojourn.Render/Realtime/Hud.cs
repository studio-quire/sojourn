using Sojourn.Render.Bodies;
using Sojourn.Render.Frame;

namespace Sojourn.Render.Realtime;

/// <summary>
/// What a frame draws of the person's HUD attachments. HUD space is the viewer's (llvoavatarself.cpp mScreen,
/// llviewerdisplay.cpp get_hud_matrices): x into the screen, y to the left, z up; the screen is one unit tall and
/// <see cref="Aspect"/> wide, centred on the origin, and each HUD point sits on its edge or corner (its offset's y times
/// the aspect). The screen image is the whole of it; a <see cref="Shot"/> is any window of it, for the assistant.
/// </summary>
public sealed record HudRequest
{
    /// <summary>The region the person's avatar is in (its HUDs are among that region's objects) and its local id there.</summary>
    public required ulong Region { get; init; }
    public required uint Self { get; init; }
    /// <summary>The HUD screen's width over its height: the window's on the desktop, the board's in the headset.</summary>
    public float Aspect { get; init; } = 16f / 9f;
    /// <summary>The screen image's size in pixels (0: not drawn).</summary>
    public int Width { get; init; }
    public int Height { get; init; }
    /// <summary>Linear light in an sRGB image (the headset's layer) rather than sRGB bytes blended as the viewer does.</summary>
    public bool Linear { get; init; }
    /// <summary>Lay the screen image over every eye, filling it (the desktop).</summary>
    public bool OverEyes { get; init; }
    /// <summary>Or at these corners in the frame's region (top left, top right, bottom right, bottom left): the headset's
    /// layer drawn into the eyes too, for checking without a headset.</summary>
    public Vector3[]? Corners { get; init; }
    public HudShot? Shot { get; init; }
    /// <summary>Text drawn over the HUDs (their llSetText), in every image of them.</summary>
    public IReadOnlyList<HudLabel>? Labels { get; init; }
}

/// <summary>A picture laid over the HUDs (premultiplied sRGB bytes; a new Version when it changes), covering a
/// rectangle of HUD space: its middle's y and z, and its width and height in HUD units.</summary>
public sealed record HudLabel(OverlayImage Picture, Vector2 Center, Vector2 Size);

/// <summary>A picture of a window of HUD space (its middle's y and z, its half height, the screen's aspect being
/// irrelevant: the window is Width/Height wide), handed over as straight RGBA in sRGB once the GPU is done.</summary>
public sealed record HudShot(Vector2 Center, float HalfHeight, int Width, int Height, Action<byte[], int, int> Done);

/// <summary>The HUDs a frame placed: the region, the HUD screen's aspect, each HUD with its parts in HUD space.</summary>
public sealed record HudsPlaced(ulong Region, float Aspect, IReadOnlyList<Sojourn.Render.World.HudObject> Objects)
{
    public static readonly HudsPlaced None = new(0, 1, Array.Empty<Sojourn.Render.World.HudObject>());
}

public static class Hud
{
    /// <summary>An attachment point's frame in HUD space, or null for points that aren't on the HUD (31-38).</summary>
    public static (Vector3 Pos, Quaternion Rot)? PointFrame(int point, float aspect)
    {
        if (!CharacterData.Shared.AttachmentPoints.TryGetValue(point, out var ap) || !ap.Hud) return null;
        return (new Vector3(ap.Position.X, ap.Position.Y * aspect, ap.Position.Z), ap.Rotation);
    }

    public static bool IsHudPoint(int point) => point is >= 31 and <= 38;

    /// <summary>A point of the screen image (0-1 across from the left, 0-1 down from the top) in HUD space (x = 0).</summary>
    public static Vector3 FromScreen(Vector2 uv, float aspect) => new(0, (0.5f - uv.X) * aspect, 0.5f - uv.Y);

    /// <summary>A HUD-space point on the screen image (0-1 across, 0-1 down).</summary>
    public static Vector2 ToScreen(Vector3 p, float aspect) => new(0.5f - p.Y / aspect, 0.5f - p.Z);

    /// <summary>The orthographic camera for a window of HUD space: looking along +x from in front of everything
    /// (nearer is smaller x, as in the viewer), reverse-Z over the HUDs' depth.</summary>
    internal static ViewSetup View(Vector2 center, float halfHeight, int width, int height, float minX, float maxX)
    {
        float camX = minX - 0.5f, far = Math.Max(1, maxX - camX + 0.5f);
        float hh = halfHeight, hw = halfHeight * width / height;
        // eye space: x = -y (right), y = z (up), forward -z = +x
        float ex = -center.X, ey = center.Y;
        var proj = new Matrix4x4(
            1 / hw, 0, 0, 0,
            0, -1 / hh, 0, 0,
            0, 0, 1 / far, 0,
            -ex / hw, ey / hh, 1, 1);
        return new ViewSetup
        {
            Width = (uint)width, Height = (uint)height,
            Position = new Vector3(camX, 0, 0),
            View = Matrix4x4.CreateLookAt(new Vector3(camX, 0, 0), new Vector3(camX + 1, 0, 0), Vector3.UnitZ),
            Proj = proj,
            VFov = 1, Near = 0, DrawDistance = 1e6f, HasWater = false,
        };
    }
}
