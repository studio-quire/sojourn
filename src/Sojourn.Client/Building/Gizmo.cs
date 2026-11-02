using System.Numerics;

namespace Sojourn.Client.Building;

/// <summary>A handle of the build gizmo: the middle (free), an arrow along an axis, a plane square (Axis: the plane's
/// normal, so it moves across the other two), a ring about an axis.</summary>
public enum GizmoKind { Free, Arrow, Plane, Ring }

public readonly record struct GizmoHandle(GizmoKind Kind, int Axis = 0);

/// <summary>Snapping while dragging: the grid (metres), the turn step (radians), and whether the axes are the
/// region's (a world axis snaps the coordinate itself; a local one snaps the distance moved).</summary>
public readonly record struct GizmoSnap(float Grid, float Step, bool World);

/// <summary>
/// Where the gizmo is this frame: its middle (the linked set's box centre), its three axes (the region's, or the
/// thing's own), its ring radius R, and which way each arrow points (toward the eye, as LLManipTranslate turns its
/// arrows to the camera's side). All in the region's frame.
/// </summary>
public readonly record struct GizmoFrame(Vector3 Centre, Vector3 X, Vector3 Y, Vector3 Z, float R, Vector3 Signs)
{
    public Vector3 Axis(int i) => i == 0 ? X : i == 1 ? Y : Z;
    public float Sign(int i) => i == 0 ? Signs.X : i == 1 ? Signs.Y : Signs.Z;

    // proportions of R
    public const float OrbR = 0.15f, ArrowFrom = 0.3f, ArrowTo = 1.5f, HeadTo = 1.78f, HeadW = 0.14f, PlaneAt = 0.42f, PlaneHalf = 0.09f;

    /// <summary>The ring radius: the thing's own size where that's sensible, held between about 5 and 12 degrees of
    /// view from the eye (the viewer keeps its manipulators a fixed size on screen; here a band, so they hug a small
    /// thing nearby and stay graspable far off).</summary>
    public static float Radius(float objectRadius, float eyeDistance) =>
        Math.Clamp(objectRadius * 1.15f, 0.09f * eyeDistance, 0.22f * eyeDistance);

    /// <summary>The frame for this frame. signs: last frame's (an arrow flips only once the eye is clearly round the
    /// other side, so it doesn't flicker edge-on); null: from scratch.</summary>
    public static GizmoFrame Make(Vector3 centre, Quaternion axes, float objectRadius, Vector3 eye, Vector3? signs)
    {
        var x = Vector3.Transform(Vector3.UnitX, axes);
        var y = Vector3.Transform(Vector3.UnitY, axes);
        var z = Vector3.Transform(Vector3.UnitZ, axes);
        var to = eye - centre;
        float d = Math.Max(0.05f, to.Length());
        float S(Vector3 a, float before)
        {
            float k = Vector3.Dot(to, a) / d;
            if (before != 0 && MathF.Abs(k) < 0.1f) return before;
            return k >= 0 ? 1 : -1;
        }
        var s = signs ?? Vector3.Zero;
        return new GizmoFrame(centre, x, y, z, Radius(objectRadius, d), new Vector3(S(x, s.X), S(y, s.Y), S(z, s.Z)));
    }

    /// <summary>The arrow along axis i: from its start to its tip.</summary>
    public (Vector3 From, Vector3 To) Arrow(int i) => (Centre + Axis(i) * (Sign(i) * ArrowFrom * R), Centre + Axis(i) * (Sign(i) * HeadTo * R));

    /// <summary>The plane square with normal axis k: its middle and its two in-plane axes (signed toward the eye).</summary>
    public (Vector3 Mid, Vector3 U, Vector3 V) Plane(int k)
    {
        int i = (k + 1) % 3, j = (k + 2) % 3;
        var u = Axis(i) * Sign(i);
        var v = Axis(j) * Sign(j);
        return (Centre + (u + v) * (PlaneAt * R), u, v);
    }
}

/// <summary>
/// The build gizmo's geometry, with no drawing and no state: which handle a ray is on, and how a drag maps the ray's
/// motion onto the handle. The VR version of the viewer's LLManipTranslate (arrows, plane squares) and LLManipRotate
/// (rings), plus a middle handle that moves the thing freely. Claude Opus 5.5, 2026-11-02.
/// </summary>
public static class Gizmo
{
    public const int RingSegments = 48;

    /// <summary>How far off a handle the ray may pass and still be on it: an angle seen from where the ray starts (the
    /// hand, or the desktop's camera), at least a centimetre.</summary>
    public static float Tolerance(float t, float deg) => Math.Max(0.01f, t * MathF.Tan(deg * MathF.PI / 180));

    /// <summary>The handle the ray (from o, unit u) is on, if any, and how far along the ray. Of several, the one it
    /// passes closest to for its size; the middle wins from inside it (a hand reaching into the thing).</summary>
    public static (GizmoHandle Handle, float T)? Hit(in GizmoFrame f, Vector3 o, Vector3 u, float tolDeg = 1.3f)
    {
        (GizmoHandle, float T, float Score)? best = null;
        void Consider(GizmoHandle h, float t, float miss, float tol)
        {
            if (t <= 0 || miss > tol) return;
            float score = miss / tol;
            if (best is not { } b || score < b.Score) best = (h, t, score);
        }
        float R = f.R;
        // the middle: a sphere
        {
            var oc = f.Centre - o;
            float t = Vector3.Dot(oc, u);
            float r = GizmoFrame.OrbR * R;
            if (oc.Length() <= r) Consider(new(GizmoKind.Free), 0.001f, -1, 1);
            else if (t > 0)
            {
                float miss = (oc - u * t).Length() - r;
                Consider(new(GizmoKind.Free), t, Math.Max(0, miss) - 0.25f * Tolerance(t, tolDeg), Tolerance(t, tolDeg));
            }
        }
        for (int i = 0; i < 3; i++)
        {
            // (an arrow pointing nearly along the ray is a dot, over the middle: not one to aim at, as the viewer hides it)
            if (EndOn(f.Axis(i), u)) continue;
            var (a, b) = f.Arrow(i);
            var (miss, t) = RaySegment(o, u, a, b);
            Consider(new(GizmoKind.Arrow, i), t, Math.Max(0, miss - GizmoFrame.HeadW * R * 0.5f), Tolerance(t, tolDeg));
        }
        for (int k = 0; k < 3; k++)
        {
            var (mid, pu, pv) = f.Plane(k);
            var n = f.Axis(k);
            if (!Intersect(o, u, n, mid, out var x, out float t, 0.05f)) continue;
            var d = x - mid;
            float half = GizmoFrame.PlaneHalf * R;
            float out_ = Math.Max(Math.Max(0, MathF.Abs(Vector3.Dot(d, pu)) - half), Math.Max(0, MathF.Abs(Vector3.Dot(d, pv)) - half));
            Consider(new(GizmoKind.Plane, k), t, out_, Tolerance(t, tolDeg) * 0.6f);
        }
        for (int k = 0; k < 3; k++)
        {
            var (miss, t) = RayRing(o, u, f.Centre, f.Axis(k), R);
            Consider(new(GizmoKind.Ring, k), t, miss, Tolerance(t, tolDeg));
        }
        return best is { } h ? (h.Item1, h.T) : null;
    }

    /// <summary>An axis seen end-on (within about 12 degrees of the line of sight).</summary>
    public static bool EndOn(Vector3 axis, Vector3 sight) => MathF.Abs(Vector3.Dot(axis, Vector3.Normalize(sight))) > 0.978f;

    /// <summary>The closest the ray comes to a segment, and how far along the ray that is.</summary>
    public static (float Miss, float T) RaySegment(Vector3 o, Vector3 u, Vector3 a, Vector3 b)
    {
        var d = b - a;
        float len2 = d.LengthSquared();
        var w = o - a;
        float bb = Vector3.Dot(u, d), c = len2, dd = Vector3.Dot(u, w), e = Vector3.Dot(d, w);
        float denom = c - bb * bb;   // (u is unit)
        float s = denom > 1e-9f ? Math.Clamp((e - bb * dd) / denom, 0, 1) : 0;
        // the ray's parameter for that point of the segment, then the segment's again for the ray's (clamped both ways)
        var p = a + d * s;
        float t = Math.Max(0, Vector3.Dot(p - o, u));
        var q = o + u * t;
        s = len2 > 1e-12f ? Math.Clamp(Vector3.Dot(q - a, d) / len2, 0, 1) : 0;
        p = a + d * s;
        t = Math.Max(0, Vector3.Dot(p - o, u));
        return ((o + u * t - p).Length(), t);
    }

    /// <summary>The closest the ray comes to a circle (centre c, axis n, radius r), by its segments.</summary>
    public static (float Miss, float T) RayRing(Vector3 o, Vector3 u, Vector3 c, Vector3 n, float r)
    {
        var (e1, e2) = Basis(n);
        float best = float.MaxValue, bestT = 0;
        var prev = c + e1 * r;
        for (int i = 1; i <= RingSegments; i++)
        {
            float a = i * MathF.Tau / RingSegments;
            var p = c + (e1 * MathF.Cos(a) + e2 * MathF.Sin(a)) * r;
            var (m, t) = RaySegment(o, u, prev, p);
            if (t > 0 && m < best) (best, bestT) = (m, t);
            prev = p;
        }
        return (best, bestT);
    }

    /// <summary>Two unit vectors at right angles to n and to each other.</summary>
    public static (Vector3, Vector3) Basis(Vector3 n)
    {
        var e1 = Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitX));
        return (e1, Vector3.Cross(n, e1));
    }

    /// <summary>Where the ray meets the plane through p with normal n (not when it runs nearly along it: |cos| under
    /// minCos, or behind, or absurdly far).</summary>
    public static bool Intersect(Vector3 o, Vector3 u, Vector3 n, Vector3 p, out Vector3 x, out float t, float minCos = 0.02f)
    {
        x = default;
        t = 0;
        float denom = Vector3.Dot(u, n);
        if (MathF.Abs(denom) < minCos) return false;
        t = Vector3.Dot(p - o, n) / denom;
        if (t <= 0 || t > 1000) return false;
        x = o + u * t;
        return true;
    }
}

/// <summary>
/// One drag of a gizmo handle, from the press to the release: where the ray was when it began and what the handle
/// means, so each frame's ray gives the root's pose. Arrows: the ray meets a plane holding the axis and facing the ray
/// (fixed at the press), and the point's distance along the axis is the move (LLManipTranslate's way). Plane squares:
/// the ray meets that plane. Rings: the ray meets the ring's plane and the turn is the angle swept round the middle,
/// added up frame by frame; seen nearly edge-on, the ray's travel along the ring's tangent at the grab, over R, is the
/// angle instead. The middle: the point grabbed rides the ray at the distance it was grabbed at (a lever from the
/// hand), so the hand's own moves carry it one to one up close. Turns go about the gizmo's middle (the viewer's
/// selection centre), so the root moves round it. Fine scales each move and turn; snapping comes after.
/// </summary>
public sealed class GizmoDrag
{
    public GizmoHandle Handle { get; }
    public GizmoFrame Frame { get; }
    readonly Vector3 _root0;
    readonly Quaternion _rot0;
    readonly Vector3 _o0, _u0, _grab;
    readonly float _t0;
    // arrows and edge-on rings: the plane the ray is read on, and the line the reading goes along
    readonly Vector3 _n, _line, _start;
    readonly bool _edge;
    Vector3 _vPrev;
    float _angle;
    (Vector3 Pos, Quaternion Rot) _last;

    public const float MaxMove = 256;

    /// <summary>The swept angle so far (radians, before fine and snapping), for a ring.</summary>
    public float Angle => _angle;

    public GizmoDrag(GizmoHandle h, GizmoFrame f, Vector3 root, Quaternion rot, Vector3 o, Vector3 u, float t)
    {
        (Handle, Frame, _root0, _rot0, _o0, _u0, _t0) = (h, f, root, Quaternion.Normalize(rot), o, Vector3.Normalize(u), t);
        _grab = _o0 + _u0 * _t0;
        _last = (root, _rot0);
        switch (h.Kind)
        {
            case GizmoKind.Arrow:
                _line = f.Axis(h.Axis);
                _n = Facing(_u0, _line);
                _start = _grab;
                break;
            case GizmoKind.Plane:
                _n = f.Axis(h.Axis);
                _start = Gizmo.Intersect(_o0, _u0, _n, f.Centre, out var x, out _, 0.01f) ? x : _grab;
                break;
            case GizmoKind.Ring:
            {
                var a = f.Axis(h.Axis);
                _edge = MathF.Abs(Vector3.Dot(_u0, a)) < 0.3f;
                // the grab's place on the ring
                var radial = _grab - f.Centre;
                radial -= a * Vector3.Dot(radial, a);
                if (radial.LengthSquared() < 1e-8f) radial = Gizmo.Basis(a).Item1;
                radial = Vector3.Normalize(radial);
                if (_edge)
                {
                    _line = Vector3.Normalize(Vector3.Cross(a, radial));   // the way the ring runs there (a turn's +)
                    _start = f.Centre + radial * f.R;
                    _n = Facing(_u0, _line);
                }
                else
                {
                    _n = a;
                    _vPrev = Gizmo.Intersect(_o0, _u0, a, f.Centre, out var p, out _, 0.01f) ? p - f.Centre : radial;
                }
                break;
            }
        }
    }

    /// <summary>A plane holding the line and facing the ray as nearly as it can: its normal.</summary>
    static Vector3 Facing(Vector3 u, Vector3 line)
    {
        var n = u - line * Vector3.Dot(u, line);
        if (n.LengthSquared() < 1e-6f) n = Gizmo.Basis(line).Item1;
        return Vector3.Normalize(n);
    }

    /// <summary>The root's pose for the ray now. fine: the share of the move that counts (1, or a fifth).</summary>
    public (Vector3 Pos, Quaternion Rot) Update(Vector3 o, Vector3 u, float fine, GizmoSnap? snap)
    {
        u = Vector3.Normalize(u);
        var f = Frame;
        switch (Handle.Kind)
        {
            case GizmoKind.Arrow:
            {
                if (!Gizmo.Intersect(o, u, _n, _start, out var x, out _)) return _last;
                float s = Math.Clamp(Vector3.Dot(x - _start, _line) * fine, -MaxMove, MaxMove);
                if (snap is { } sn) s = SnapAlong(s, _line, sn);
                return _last = (_root0 + _line * s, _rot0);
            }
            case GizmoKind.Plane:
            {
                if (!Gizmo.Intersect(o, u, _n, f.Centre, out var x, out _, 0.05f)) return _last;
                var d = (x - _start) * fine;
                d -= _n * Vector3.Dot(d, _n);
                if (d.Length() > MaxMove) d = Vector3.Normalize(d) * MaxMove;
                if (snap is { } sn)
                {
                    int i = (Handle.Axis + 1) % 3, j = (Handle.Axis + 2) % 3;
                    var (ai, aj) = (f.Axis(i), f.Axis(j));
                    d = ai * SnapAlong(Vector3.Dot(d, ai), ai, sn) + aj * SnapAlong(Vector3.Dot(d, aj), aj, sn);
                }
                return _last = (_root0 + d, _rot0);
            }
            case GizmoKind.Ring:
            {
                var a = f.Axis(Handle.Axis);
                if (_edge)
                {
                    if (!Gizmo.Intersect(o, u, _n, _start, out var x, out _)) return _last;
                    _angle = Vector3.Dot(x - _start, _line) / Math.Max(0.01f, f.R);
                }
                else
                {
                    if (!Gizmo.Intersect(o, u, a, f.Centre, out var x, out _, 0.05f)) return _last;
                    var v = x - f.Centre;
                    v -= a * Vector3.Dot(v, a);
                    if (v.Length() < 0.05f * f.R) return _last;   // through the middle: no sense of angle there
                    _angle += MathF.Atan2(Vector3.Dot(Vector3.Cross(_vPrev, v), a), Vector3.Dot(_vPrev, v));
                    _vPrev = v;
                }
                float angle = _angle * fine;
                if (snap is { Step: > 0 } sn) angle = MathF.Round(angle / sn.Step) * sn.Step;
                var dq = Quaternion.CreateFromAxisAngle(a, angle);
                var rot = Quaternion.Normalize(dq * _rot0);
                return _last = (f.Centre + Vector3.Transform(_root0 - f.Centre, dq), rot);
            }
            default:
            {
                // the point grabbed rides the ray at its distance; the root keeps its offset from it
                var at = o + u * _t0;
                var d = (at - _grab) * fine;
                if (d.Length() > MaxMove) d = Vector3.Normalize(d) * MaxMove;
                var pos = _root0 + d;
                if (snap is { Grid: > 0 } sn) pos = new Vector3(MathF.Round(pos.X / sn.Grid) * sn.Grid, MathF.Round(pos.Y / sn.Grid) * sn.Grid, MathF.Round(pos.Z / sn.Grid) * sn.Grid);
                return _last = (pos, _rot0);
            }
        }
    }

    /// <summary>A move of s along a unit axis, snapped: on the region's axes the root's coordinate lands on the grid
    /// (as the viewer's grid, from the region's corner); on the thing's own, the distance goes in grid steps.</summary>
    float SnapAlong(float s, Vector3 axis, GizmoSnap sn)
    {
        if (sn.Grid <= 0) return s;
        if (!sn.World) return MathF.Round(s / sn.Grid) * sn.Grid;
        float at = Vector3.Dot(_root0, axis);
        return MathF.Round((at + s) / sn.Grid) * sn.Grid - at;
    }
}
