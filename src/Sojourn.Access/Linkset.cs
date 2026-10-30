using System.Globalization;
using LibreMetaverse;
using Sojourn.Core.Agents;
using Sojourn.Core.World;
using Sojourn.Render.World;

namespace Sojourn.Access;

/// <summary>
/// An object's parts and a part's faces in words (docs/access.md > M2, a blind tester's child prims: the Linden sunken city's
/// Treasure Chest 6 answered only on a child prim, which Radegast lists by name alone). Every part numbered, root first,
/// then the rest in the order the region made them (their local ids, which follow the link order a linkset is made in):
/// a named one by its name ("Latch, part 3 of 5, a tiny box in front of the root, nearer you, touchable"), an unnamed one by
/// what and where it is ("Part 2 of 5, a box above the root, touchable"). Faces by direction from where the person is:
/// the top, the bottom, the side facing you, the far side, the left and right sides as you face it, a curved side, an
/// inside; always with the face's number, which scripts and builders use. Pure words over the world model.
/// </summary>
public static class Linkset
{
    /// <summary>One part. Number from 1 (the root); Pos its middle (region frame; for a HUD, the HUD's own frame:
    /// x toward the screen, y left, z up); Touch: its own script takes touches; Sit: a click sits.</summary>
    public sealed record Part(UUID Id, UUID Root, uint LocalId, int Number, int Count, string Name, bool Named, string Shape,
        System.Numerics.Vector3 Pos, System.Numerics.Vector3 Scale, bool Touch, bool Sit, string? Desc, bool Hud, string RootName)
    {
        public string Words { get; init; } = "";
    }

    /// <summary>Names that say nothing: what a new prim is called on Second Life and OpenSim, and the root's own name on a
    /// child (builders often leave every part named as the whole).</summary>
    public static bool Unnamed(string? name, string rootName) =>
        string.IsNullOrWhiteSpace(name) || name is "Object" or "Primitive" or "Prim" || (rootName.Length > 0 && name == rootName);

    /// <summary>The parts of a root's linkset from the world model, root first, numbered, with their words; me: where the
    /// person is (region frame) for "nearer you", null for a HUD (its own frame: up, down, left and right on the screen).</summary>
    public static List<Part> Parts(RegionWorld w, ObjectViews views, Primitive root, AgentSession s, System.Numerics.Vector3? me, bool hud)
    {
        var prims = views.Linkset(w, root);
        var ordered = new List<Primitive> { root };
        ordered.AddRange(prims.Where(p => p.LocalID != root.LocalID).OrderBy(p => p.LocalID));
        string rootName = root.Properties?.Name ?? "";
        var rootPos = hud ? System.Numerics.Vector3.Zero : V(w.RegionPosition(root));
        var rootRot = Q(root.Rotation);
        var list = new List<Part>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            // a HUD's parts in the HUD's own frame: their offsets from the root turned by the root's rotation
            var pos = hud ? (i == 0 ? System.Numerics.Vector3.Zero : System.Numerics.Vector3.Transform(V(p.Position), rootRot)) : V(w.RegionPosition(p));
            var flags = s.State.PrimFlagsOf(p);
            string name = p.Properties?.Name ?? "";
            var desc = p.Properties?.Description is { Length: > 0 and < 80 } d && d != "(No Description)" ? d : null;
            var part = new Part(p.ID, root.ID, p.LocalID, i + 1, ordered.Count, name, i == 0 ? name.Length > 0 && name is not ("Object" or "Primitive")
                : !Unnamed(name, rootName), p.PrimData.PCode == PCode.Prim ? ObjectViews.ShapeName(p) : "part", pos, V(p.Scale),
                (flags & PrimFlags.Touch) != 0, p.ClickAction == ClickAction.Sit, desc, hud, rootName.Length > 0 ? rootName : "the object");
            list.Add(part with { Words = Words(part, rootName, rootPos, me) });
        }
        return list;
    }

    /// <summary>"Treasure Chest, part 1 of 5, the root, a box", "Part 2 of 5, a box above the root, touchable",
    /// "Latch, part 3 of 5, a tiny box in front of the root, nearer you, touchable"; a lone prim is "the whole object".</summary>
    public static string Words(Part p, string rootName, System.Numerics.Vector3 rootPos, System.Numerics.Vector3? me)
    {
        string number = p.Count == 1 ? "the whole object" : $"part {p.Number} of {p.Count}";
        string what = $"a {Article(Size(p.Scale))}{Kind(p.Shape)}";
        string words;
        if (p.Number == 1)
            words = (p.Named ? $"{p.Name}, {number}" : char.ToUpperInvariant(number[0]) + number[1..]) + (p.Count > 1 ? ", the root, " : ", ") + what;
        else
        {
            var where = Where(p.Pos - rootPos, rootPos, me, p.Hud);
            words = (p.Named ? $"{p.Name}, {number}, " : $"Part {p.Number} of {p.Count}, ") + what + (where.Length > 0 ? " " + where : "");
        }
        if (p.Desc != null && !p.Named) words += $", described as {p.Desc}";
        if (p.Touch) words += ", touchable";
        if (p.Sit) words += ", can sit";
        return words;
    }

    static string Size(System.Numerics.Vector3 s)
    {
        float m = Math.Max(s.X, Math.Max(s.Y, s.Z));
        return m < 0.12f ? "tiny" : m < 0.4f ? "small" : m < 2.5f ? "" : m < 8 ? "large" : "huge";
    }

    static string Article(string size) => size.Length == 0 ? "" : size + " ";

    static string Kind(string shape) => shape switch
    {
        "mesh" => "mesh part",
        "sculpt" => "sculpted part",
        "other" or "part" => "part",
        "half-circle" => "half cylinder",
        _ => shape,
    };

    /// <summary>"above the root, to its left", from the person's side of it ("nearer you", "further from you"); on a HUD,
    /// as it shows on the screen.</summary>
    static string Where(System.Numerics.Vector3 d, System.Numerics.Vector3 rootPos, System.Numerics.Vector3? me, bool hud)
    {
        float up = d.Z, side, ahead;
        if (hud) { side = -d.Y; ahead = 0; }   // the HUD's +y is the screen's left
        else
        {
            var f = me is { } m ? new System.Numerics.Vector2(rootPos.X - m.X, rootPos.Y - m.Y) : new System.Numerics.Vector2(0, 1);
            f = f.LengthSquared() < 1e-4f ? new System.Numerics.Vector2(0, 1) : System.Numerics.Vector2.Normalize(f);
            var r = new System.Numerics.Vector2(f.Y, -f.X);
            var flat = new System.Numerics.Vector2(d.X, d.Y);
            side = System.Numerics.Vector2.Dot(flat, r);
            ahead = System.Numerics.Vector2.Dot(flat, f);
        }
        float big = Math.Max(Math.Abs(up), Math.Max(Math.Abs(side), Math.Abs(ahead)));
        if (big < 0.04f) return "at the root";
        float t = Math.Max(0.03f, big * 0.3f);
        var parts = new List<(float Size, string First, string Then)>();
        if (Math.Abs(up) > t) parts.Add((Math.Abs(up), up > 0 ? "above the root" : "below the root", up > 0 ? "above it" : "below it"));
        if (Math.Abs(side) > t) parts.Add((Math.Abs(side), side > 0 ? "to the right of the root" : "to the left of the root", side > 0 ? "to its right" : "to its left"));
        if (Math.Abs(ahead) > t) parts.Add((Math.Abs(ahead), ahead > 0 ? "beyond the root, further from you" : "in front of the root, nearer you",
            ahead > 0 ? "further from you" : "nearer you"));
        parts.Sort((a, b) => b.Size.CompareTo(a.Size));
        return string.Join(", ", parts.Select((x, i) => i == 0 ? x.First : x.Then));
    }

    /// <summary>A face in words from where the person is (eye: their eyes, region frame): "The top", "The side facing
    /// you", "The far side", "The left side", "The right side" (as you face it), "The bottom", "A curved side", "The inside
    /// ..."; then its number ("face 0").</summary>
    public static string FaceWords(FaceShape f, System.Numerics.Vector3 eye)
    {
        string dir;
        var n = f.Flat >= 0.6f ? f.Mean : f.Normal;
        if (f.Flat < 0.6f) dir = n.Z > 0.85f ? "the curved top" : n.Z < -0.85f ? "the curved bottom" : "the curved side";
        else if (n.Z > 0.7f) dir = "the top";
        else if (n.Z < -0.7f) dir = "the bottom";
        else
        {
            var toEye = new System.Numerics.Vector2(eye.X - f.Point.X, eye.Y - f.Point.Y);
            var nh = new System.Numerics.Vector2(n.X, n.Y);
            if (toEye.LengthSquared() < 1e-4f || nh.LengthSquared() < 1e-6f) dir = "a side";
            else
            {
                toEye = System.Numerics.Vector2.Normalize(toEye);
                nh = System.Numerics.Vector2.Normalize(nh);
                float facing = System.Numerics.Vector2.Dot(nh, toEye);
                // the person's right as they look at it: looking along -toEye
                var right = new System.Numerics.Vector2(-toEye.Y, toEye.X);
                dir = facing > 0.7f ? "the side facing you" : facing < -0.7f ? "the far side"
                    : System.Numerics.Vector2.Dot(nh, right) > 0 ? "the right side" : "the left side";
            }
        }
        if (f.Inner) dir = "the inside, " + dir.Replace("the ", "");
        return char.ToUpperInvariant(dir[0]) + dir[1..] + string.Create(CultureInfo.InvariantCulture, $", face {f.Face}");
    }

    /// <summary>A face named in words or by number ("top", "facing me", "front", "far", "back", "left", "right", "bottom",
    /// "3"), among the faces as named from the person; null when none fits.</summary>
    public static FaceShape? FindFace(IReadOnlyList<FaceShape> faces, string words, System.Numerics.Vector3 eye)
    {
        var w = words.Trim().ToLowerInvariant();
        if (w.StartsWith("face ")) w = w[5..].Trim();
        if (int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return faces.FirstOrDefault(f => f.Face == n) is { Area: > 0 } hit ? hit : null;
        string? want = w switch
        {
            "top" or "the top" or "up" => "The top",
            "bottom" or "the bottom" or "underneath" or "under" => "The bottom",
            "front" or "facing me" or "facing you" or "near" or "near side" or "this side" => "The side facing you",
            "back" or "far" or "far side" or "behind" or "the back" => "The far side",
            "left" or "left side" => "The left side",
            "right" or "right side" => "The right side",
            "side" or "curved" or "round" or "curved side" => "The curved side",
            "inside" or "inner" => "The inside",
            _ => null,
        };
        if (want == null) return null;
        // the outside first, the largest of those that fit (a hollow box's inner face shares the outer's direction)
        return faces.Select(f => (f, words: FaceWords(f, eye))).Where(x => x.words.StartsWith(want, StringComparison.Ordinal))
            .OrderBy(x => x.f.Inner).ThenByDescending(x => x.f.Area).Select(x => (FaceShape?)x.f).FirstOrDefault();
    }

    static System.Numerics.Vector3 V(Vector3 v) => new(v.X, v.Y, v.Z);
    static System.Numerics.Quaternion Q(Quaternion q) => new(q.X, q.Y, q.Z, q.W);
}
