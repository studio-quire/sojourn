using System.Numerics;
using Sojourn.Render.Realtime;

namespace Sojourn.Client.Xr;

/// <summary>
/// The order the headset's compositor is to lay the menus' quad layers in, so that each is over another exactly where
/// it's in front of it, from where the eyes are (Claude Opus 5.5, 2026-10-28). The compositor has no depth between
/// quads: it draws them in the order given. Sorting by distance (the nearest point to the head, as it was) isn't what
/// decides which of two sheets is in front where they overlap, so two tilted menus near each other swapped as the head
/// moved (the panel and the reader, the session of 2026-10-28).
/// Instead, per pair of menus that overlap as seen from the eyes: the part of one on the eyes' side of the other's plane
/// is in front of it, the part beyond is behind (a ray through the overlap meets the near part before the plane). So:
///   - one menu wholly on one side of the other's plane (as menus that don't pass through each other almost always
///     are): it goes over the other, or under it, wherever they overlap; menus that don't overlap have no say;
///   - one passing through the other (both parts overlap it): no order of the two whole layers is right, so it's cut
///     in two along the other's plane, each part its own layer showing only its side of the line (QuadLayer's pieces,
///     Render's LayerCut), and the parts go in on either side of the other; exact from any pose, as the line where
///     they meet is fixed on both sheets;
///   - coplanar sheets (a board's screen 2 mm over its glass seen edge-on): by their stack (the screen over the glass).
/// The constraints are sorted topologically, the old farthest-first order deciding wherever nothing constrains it
/// (menus that don't overlap: any order looks alike, and this one is stable). A cycle among sheets that don't meet
/// (three in a ring, rare) is broken by cutting one by another's plane, as Newell's painter's algorithm does; a menu
/// is cut by at most three planes (MenuClip), past that the farthest of the cycle goes first. Both eyes get one order
/// (the compositor's), worked out from between them: it differs from either eye's only when a menu's plane passes
/// between the eyes, seen edge-on, where it can't be seen anyway.
/// A confirmation's panel (Top) goes over everything, whole.
/// Allocation-free once warm; not thread-safe (an instance per thread that asks).
/// </summary>
public sealed class MenuOrder
{
    /// <summary>A menu: its centre and half its width along its right, half its height along its up (its corners are
    /// centre ± right ± up; the picture's top at +up); stack orders coplanar ones (higher over); Top over everything.</summary>
    public readonly record struct Menu(Vector3 Centre, Vector3 Right, Vector3 Up, int Stack = 0, bool Top = false);

    /// <summary>A layer to submit, in order: the menu's index and the part of it shown (whole when the clip is).</summary>
    public readonly record struct Part(int Menu, MenuClip Clip);

    const float Eps = 0.0005f;        // nearer a plane than this (0.5 mm) counts as on it (a board's screen is 2 mm out)
    const float AngleTol = 1e-5f;     // radians: outlines meeting closer than this don't overlap
    const int MaxRounds = 8;          // cuts at most
    const int MaxVerts = 4 + MenuClip.Max;

    sealed class Piece
    {
        public int Menu;
        public readonly Vector3[] V = new Vector3[MaxVerts + 1];
        public int N;
        public Vector3 Normal;     // the menu's plane (unit)
        public float D;            // dot(Normal, a point on it)
        public float Far;          // the old order's key: the menu's nearest point to the eye (farther first)
        public int Stack;
        public MenuClip Clip;      // in the menu's picture
        public readonly Vector4[] Planes = new Vector4[MenuClip.Max];   // the same, in the world: kept where dot(xyz, p) + w >= 0
        public int PlaneCount;
    }

    readonly List<Piece> _pool = new();
    readonly List<Piece> _pieces = new();
    readonly List<Part> _parts = new();
    bool[,] _before = new bool[16, 16];   // [i, j]: piece i goes before (under) piece j
    int[] _waiting = new int[16];
    bool[] _done = new bool[16];
    readonly Vector3[] _a = new Vector3[MaxVerts + 1], _b = new Vector3[MaxVerts + 1];
    IReadOnlyList<Menu> _menus = Array.Empty<Menu>();

    /// <summary>The last <see cref="Order"/> had two menus overlapping as seen from the eye (coplanar stacks aside,
    /// such as a board's screen on its glass), whether or not either passes through the other.</summary>
    public bool AnyOverlap { get; private set; }

    /// <summary>The parts to submit, bottom first. eye: between the eyes, in the menus' space. cut: false to keep
    /// every menu whole (no parts made: the order alone). The list is this instance's, until it's asked again.</summary>
    public List<Part> Order(IReadOnlyList<Menu> menus, Vector3 eye, bool cut = true)
    {
        _menus = menus;
        AnyOverlap = false;
        _parts.Clear();
        _pieces.Clear();
        int top = -1, used = 0;
        for (int i = 0; i < menus.Count; i++)
        {
            var m = menus[i];
            if (m.Top && top < 0) { top = i; continue; }
            var n = Vector3.Cross(m.Right, m.Up);
            if (n.LengthSquared() < 1e-20f) continue;   // no area
            var p = used < _pool.Count ? _pool[used] : Add();
            used++;
            p.Menu = i;
            p.N = 4;
            p.V[0] = m.Centre - m.Right + m.Up;
            p.V[1] = m.Centre + m.Right + m.Up;
            p.V[2] = m.Centre + m.Right - m.Up;
            p.V[3] = m.Centre - m.Right - m.Up;
            p.Normal = Vector3.Normalize(n);
            p.D = Vector3.Dot(p.Normal, m.Centre);
            p.Far = NearestDistance(m, eye);
            p.Stack = m.Stack;
            p.Clip = default;
            p.PlaneCount = 0;
            _pieces.Add(p);
        }

        for (int round = 0; ; round++)
        {
            bool allowCut = cut && round < MaxRounds;
            int n = _pieces.Count;
            Grow(n);
            // the pairs' constraints; the first pair that passes through each other is cut, and everything asked again
            int cutAt = -1, cutBy = -1;
            for (int i = 0; i < n && cutAt < 0; i++)
                for (int j = i + 1; j < n; j++)
                {
                    _before[i, j] = _before[j, i] = false;
                    int r = Relation(_pieces[i], _pieces[j], eye);
                    if (r != 0 && _pieces[i].Menu != _pieces[j].Menu && !Coplanar(_pieces[i], _pieces[j])) AnyOverlap = true;
                    // both of i's sides overlapping j: unless j lies wholly on one side of i's plane (one resting on
                    // the other, within Eps), they pass through each other
                    if (r == 2 && Relation(_pieces[j], _pieces[i], eye) is var back && back != 2) r = -back;
                    if (r == 2)
                    {
                        // through each other: cut the one with room for another plane (the one cut less), or else
                        // order them the old way
                        var (pi, pj) = (_pieces[i], _pieces[j]);
                        if (allowCut && (pi.PlaneCount < MenuClip.Max || pj.PlaneCount < MenuClip.Max))
                        {
                            (cutAt, cutBy) = pi.PlaneCount <= pj.PlaneCount ? (i, j) : (j, i);
                            break;
                        }
                        r = pi.Far >= pj.Far ? -1 : 1;
                    }
                    if (r < 0) _before[i, j] = true;
                    else if (r > 0) _before[j, i] = true;
                }
            if (cutAt >= 0)
            {
                used = CutPiece(cutAt, cutBy, used);
                continue;
            }
            if (Sort(n, eye, allowCut, ref used)) break;
        }
        if (top >= 0) _parts.Add(new Part(top, default));
        return _parts;
    }

    static bool Coplanar(Piece a, Piece b) => MathF.Abs(Vector3.Dot(a.Normal, b.Normal)) > 0.999f && MathF.Abs(MathF.Abs(a.D) - MathF.Abs(b.D)) < 0.01f;

    Piece Add()
    {
        var p = new Piece();
        _pool.Add(p);
        return p;
    }

    void Grow(int n)
    {
        if (_before.GetLength(0) >= n) return;
        int size = Math.Max(n, _before.GetLength(0) * 2);
        _before = new bool[size, size];
        _waiting = new int[size];
        _done = new bool[size];
    }

    /// <summary>Kahn's sort over the constraints, farthest first among the free; true when done (the parts written),
    /// false when a cycle was broken by a cut (ask again).</summary>
    bool Sort(int n, Vector3 eye, bool allowCut, ref int used)
    {
        for (int i = 0; i < n; i++)
        {
            _done[i] = false;
            int w = 0;
            for (int j = 0; j < n; j++) if (_before[j, i]) w++;
            _waiting[i] = w;
        }
        int startParts = _parts.Count;
        for (int emitted = 0; emitted < n; emitted++)
        {
            int pick = -1;
            for (int i = 0; i < n; i++)
                if (!_done[i] && _waiting[i] == 0 && (pick < 0 || Farther(_pieces[i], _pieces[pick]))) pick = i;
            if (pick < 0)
            {
                // a cycle among sheets that don't meet: cut one of it by another's plane it straddles, else let the
                // farthest of what's left go first
                if (allowCut)
                    for (int i = 0; i < n; i++)
                    {
                        if (_done[i] || _pieces[i].PlaneCount >= MenuClip.Max) continue;
                        for (int j = 0; j < n; j++)
                            if (!_done[j] && _before[i, j] && Straddles(_pieces[i], _pieces[j]))
                            {
                                _parts.RemoveRange(startParts, _parts.Count - startParts);
                                used = CutPiece(i, j, used);
                                return false;
                            }
                    }
                for (int i = 0; i < n; i++)
                    if (!_done[i] && (pick < 0 || Farther(_pieces[i], _pieces[pick]))) pick = i;
            }
            _done[pick] = true;
            for (int j = 0; j < n; j++) if (_before[pick, j]) _waiting[j]--;
            var p = _pieces[pick];
            _parts.Add(new Part(p.Menu, p.Clip));
        }
        return true;
    }

    static bool Farther(Piece a, Piece b) => a.Far > b.Far || (a.Far == b.Far && (a.Menu < b.Menu || (a.Menu == b.Menu && a.Clip.Count < b.Clip.Count)));

    /// <summary>Piece i cut in two along piece j's plane: it keeps one side, a new piece the other. Returns the pool's
    /// use.</summary>
    int CutPiece(int i, int j, int used)
    {
        var a = _pieces[i];
        var by = _pieces[j];
        var plane = new Vector4(by.Normal, -by.D);   // dot(xyz, p) + w: the signed distance from j's plane
        var b = used < _pool.Count ? _pool[used] : Add();
        used++;
        b.Menu = a.Menu;
        b.Far = a.Far;
        b.Stack = a.Stack;
        b.Normal = a.Normal;
        b.D = a.D;
        b.Clip = a.Clip;
        b.PlaneCount = a.PlaneCount;
        for (int k = 0; k < a.PlaneCount; k++) b.Planes[k] = a.Planes[k];
        int na = ClipPoly(a.V, a.N, plane, _a);
        int nb = ClipPoly(a.V, a.N, -plane, _b);
        Array.Copy(_a, a.V, na);
        a.N = na;
        Array.Copy(_b, b.V, nb);
        b.N = nb;
        var m = _menus[a.Menu];
        AddPlane(a, m, plane);
        AddPlane(b, m, -plane);
        _pieces.Add(b);
        return used;
    }

    static void AddPlane(Piece p, in Menu m, Vector4 plane)
    {
        p.Planes[p.PlaneCount++] = plane;
        // over the picture: x(u, v) = centre + (2u - 1) right + (1 - 2v) up
        var n = new Vector3(plane.X, plane.Y, plane.Z);
        float nr = Vector3.Dot(n, m.Right), nu = Vector3.Dot(n, m.Up);
        p.Clip.Add(new Vector4(2 * nr, -2 * nu, Vector3.Dot(n, m.Centre) + plane.W - nr + nu, 0));
    }

    /// <summary>-1: a goes under b where they overlap; 1: over it; 0: no say (apart on screen, or coplanar and level);
    /// 2: they pass through each other (no order of the two is right).</summary>
    int Relation(Piece a, Piece b, Vector3 eye)
    {
        // a's parts on the eye's side of b's plane and beyond it
        float sb = Vector3.Dot(b.Normal, eye) - b.D;
        float sa = Vector3.Dot(a.Normal, eye) - a.D;
        if (MathF.Abs(sb) < Eps || MathF.Abs(sa) < Eps) return 0;   // one edge-on: it can't be seen
        var plane = new Vector4(b.Normal * MathF.Sign(sb), -b.D * MathF.Sign(sb));   // > 0 on the eye's side
        float lo = float.MaxValue, hi = float.MinValue;
        for (int k = 0; k < a.N; k++)
        {
            float d = Vector3.Dot(new Vector3(plane.X, plane.Y, plane.Z), a.V[k]) + plane.W;
            lo = MathF.Min(lo, d);
            hi = MathF.Max(hi, d);
        }
        if (hi <= Eps && lo >= -Eps)
        {
            // coplanar: by their stack, where they overlap
            if (a.Stack == b.Stack || !Overlap(a.V, a.N, b.V, b.N, eye)) return 0;
            return a.Stack > b.Stack ? 1 : -1;
        }
        if (lo >= -Eps) return Overlap(a.V, a.N, b.V, b.N, eye) ? 1 : 0;   // wholly in front of b's plane
        if (hi <= Eps) return Overlap(a.V, a.N, b.V, b.N, eye) ? -1 : 0;   // wholly beyond it
        int nf = ClipPoly(a.V, a.N, plane, _a);
        int nk = ClipPoly(a.V, a.N, -plane, _b);
        bool front = nf >= 3 && Overlap(_a, nf, b.V, b.N, eye);
        bool back = nk >= 3 && Overlap(_b, nk, b.V, b.N, eye);
        if (front && back) return 2;
        return front ? 1 : back ? -1 : 0;
    }

    /// <summary>Whether a has points beyond Eps on both sides of b's plane.</summary>
    static bool Straddles(Piece a, Piece b)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        for (int k = 0; k < a.N; k++)
        {
            float d = Vector3.Dot(b.Normal, a.V[k]) - b.D;
            lo = MathF.Min(lo, d);
            hi = MathF.Max(hi, d);
        }
        return lo < -Eps && hi > Eps;
    }

    /// <summary>The convex polygon's part where dot(xyz, p) + w >= 0, into o; its vertex count.</summary>
    static int ClipPoly(Vector3[] v, int n, Vector4 plane, Vector3[] o)
    {
        var pn = new Vector3(plane.X, plane.Y, plane.Z);
        int m = 0;
        for (int k = 0; k < n; k++)
        {
            var p = v[k];
            var q = v[(k + 1) % n];
            float dp = Vector3.Dot(pn, p) + plane.W, dq = Vector3.Dot(pn, q) + plane.W;
            if (dp >= 0 && m < o.Length) o[m++] = p;
            if ((dp >= 0) != (dq >= 0) && m < o.Length) o[m++] = p + (q - p) * (dp / (dp - dq));
        }
        return m;
    }

    /// <summary>Whether two convex polygons (not containing the eye) overlap as seen from it: their cones from the eye
    /// share more than a sliver. Separating planes through the eye are looked for along every edge of either, and
    /// parallel to either's plane (one in front of the eye, the other behind).</summary>
    static bool Overlap(Vector3[] p, int np, Vector3[] q, int nq, Vector3 eye) =>
        !Separates(p, np, q, nq, eye) && !Separates(q, nq, p, np, eye);

    static bool Separates(Vector3[] p, int np, Vector3[] q, int nq, Vector3 eye)
    {
        if (np < 3 || nq < 3) return true;
        var c = Vector3.Zero;
        for (int k = 0; k < np; k++) c += p[k];
        c = c / np - eye;
        for (int k = 0; k < np; k++)
        {
            var m = Vector3.Cross(p[k] - eye, p[(k + 1) % np] - eye);
            float len = m.Length();
            if (len < 1e-12f) continue;
            m /= len;
            if (Vector3.Dot(m, c) < 0) m = -m;   // the polygon's side
            if (AllOutside(q, nq, eye, m)) return true;
        }
        var nrm = Vector3.Cross(p[1] - p[0], p[2] - p[0]);
        if (nrm.LengthSquared() > 1e-20f)
        {
            nrm = Vector3.Normalize(nrm);
            if (Vector3.Dot(nrm, c) < 0) nrm = -nrm;
            if (AllOutside(q, nq, eye, nrm)) return true;
        }
        return false;
    }

    static bool AllOutside(Vector3[] q, int nq, Vector3 eye, Vector3 m)
    {
        for (int k = 0; k < nq; k++)
        {
            var d = q[k] - eye;
            float len = d.Length();
            if (len < 1e-9f || Vector3.Dot(m, d) / len > AngleTol) return false;
        }
        return true;
    }

    /// <summary>The old order's key: the menu's nearest point to the eye.</summary>
    static float NearestDistance(in Menu m, Vector3 eye)
    {
        float hw = m.Right.Length(), hh = m.Up.Length();
        if (hw < 1e-9f || hh < 1e-9f) return Vector3.Distance(m.Centre, eye);
        var r = m.Right / hw;
        var u = m.Up / hh;
        var d = eye - m.Centre;
        float x = Math.Clamp(Vector3.Dot(d, r), -hw, hw), y = Math.Clamp(Vector3.Dot(d, u), -hh, hh);
        return Vector3.Distance(eye, m.Centre + r * x + u * y);
    }
}
