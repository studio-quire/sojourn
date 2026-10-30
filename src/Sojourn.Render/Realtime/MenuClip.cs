namespace Sojourn.Render.Realtime;

/// <summary>
/// The part of a headset menu's picture a quad layer shows when the menu is cut in parts (Sojourn.Client's MenuOrder:
/// where another menu passes through it): up to three half-planes over the picture's shown rectangle, u across (0 at
/// the left, 1 at the right) and v down (0 at the top, 1 at the bottom); a point is kept where x u + y v + z >= 0 for
/// each. Count 0: the whole picture.
/// </summary>
public struct MenuClip
{
    public const int Max = 3;
    public Vector4 P0, P1, P2;
    public int Count;

    public readonly bool Whole => Count == 0;

    public readonly Vector4 this[int i] => i switch { 0 => P0, 1 => P1, _ => P2 };

    /// <summary>This with one more half-plane (false when it has <see cref="Max"/> already).</summary>
    public bool Add(Vector4 plane)
    {
        switch (Count)
        {
            case 0: P0 = plane; break;
            case 1: P1 = plane; break;
            case 2: P2 = plane; break;
            default: return false;
        }
        Count++;
        return true;
    }

    /// <summary>Whether a point of the picture (u, v as above) is kept.</summary>
    public readonly bool Keeps(float u, float v)
    {
        for (int i = 0; i < Count; i++)
        {
            var p = this[i];
            if (p.X * u + p.Y * v + p.Z < 0) return false;
        }
        return true;
    }

    /// <summary>The half-planes over a w x h pixel image (pixel coordinates, centres at +0.5), each scaled so that it
    /// gives the signed distance in pixels: what the cut pass and the mirror's draw antialias by.</summary>
    public readonly MenuClip InPixels(float w, float h)
    {
        var o = new MenuClip();
        for (int i = 0; i < Count; i++)
        {
            var p = this[i];
            float a = p.X / w, b = p.Y / h;
            float len = MathF.Sqrt(a * a + b * b);
            o.Add(len > 1e-12f ? new Vector4(a / len, b / len, p.Z / len, 0) : new Vector4(0, 0, p.Z >= 0 ? 1e6f : -1e6f, 0));
        }
        return o;
    }
}
