using Sojourn.Render.World;
using Vector2 = System.Numerics.Vector2;

namespace Sojourn.Session.Hud;

/// <summary>
/// A HUD part's faces as they show on the screen: a grid of picks across the part's box, as the mouse would click,
/// counting which of its faces each lands on (another part in front, or nothing, counts for none). A face is only
/// pressable where it shows, so this is what's listed, rather than every face the prim has (a flat HUD button's sides and
/// back never show); each with the pick nearest its middle as seen, where a press on it lands.
/// </summary>
public static class HudScan
{
    /// <summary>A face as it shows: its share of the part as seen, where on the part, and the pick at its middle.</summary>
    public sealed record FaceSeen(int Face, float Share, string Where, PickHit Hit);

    /// <summary>The part's faces that show, the largest first. On the thread where picks run.</summary>
    public static List<FaceSeen> Faces(HudPicker pick, HudPart part, int across = 32)
    {
        float y0 = part.Max.Y, y1 = part.Min.Y, z0 = part.Max.Z, z1 = part.Min.Z;
        float w = y0 - y1, h = z0 - z1;
        if (w <= 0 || h <= 0) return new();
        // samples in the middle of each cell, the long side `across` of them
        int nx = w >= h ? across : Math.Max(4, (int)MathF.Round(across * w / h));
        int ny = h >= w ? across : Math.Max(4, (int)MathF.Round(across * h / w));
        var faces = new Dictionary<int, List<(float U, float V, PickHit Hit)>>();
        int hits = 0;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                float u = (i + 0.5f) / nx, v = (j + 0.5f) / ny;
                if (pick(new Vector2(y0 - u * w, z0 - v * h)) is not { } hit || hit.Id != part.Id || hit.Surface is not { } s) continue;
                hits++;
                if (!faces.TryGetValue(s.Face, out var list)) faces[s.Face] = list = new();
                list.Add((u, v, hit));
            }
        var seen = new List<FaceSeen>();
        foreach (var (face, list) in faces)
        {
            float mu = list.Average(x => x.U), mv = list.Average(x => x.V);
            // its middle as seen, when that's on it (else the sample nearest it)
            var mid = pick(new Vector2(y0 - mu * w, z0 - mv * h)) is { Surface.Face: var f } exact && f == face && exact.Id == part.Id ? exact
                : list.MinBy(x => (x.U - mu) * (x.U - mu) + (x.V - mv) * (x.V - mv)).Hit;
            float share = list.Count / (float)hits;
            seen.Add(new FaceSeen(face, share, share > 0.8f ? "all of it" : Huds.Where(mu, mv), mid));
        }
        return seen.OrderByDescending(f => f.Share).ToList();
    }
}
