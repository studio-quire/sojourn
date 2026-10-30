using System.Diagnostics;
using System.Numerics;

namespace Sojourn.Render.World;

/// <summary>
/// The land a land op is about, and the terraform brush, drawn with the property lines (depth-tested and writing
/// depth). Ported from the Second Life viewer (LGPL 2.1, Linden Research, Inc.):
/// - The selection (LLViewerParcelMgr::renderHighlightSegments, llglsandbox.cpp): the outline of the selected land's
///   4 m cells as 0.666 m posts of 20% yellow (PARCEL_POST_HEIGHT), each edge from the ground at its two ends
///   (renderOneSegment below 1 m). Here it's a parcel (its bitmap) or a rectangle a land op names, shown while the
///   person is asked and while the op runs, since the assistant, not a hand, picked the land.
/// - The brush (LLToolBrushLand::renderOverlay): a white stick from the ground at each metre of the brush's square,
///   taller toward the middle (0.2 m plus up to 0.4), while a terraform stroke goes.
/// Claude Opus 5.5, 2026-10-28 (batch B5).
/// </summary>
public sealed partial class ParcelLines
{
    const float PostHeight = 0.666f;
    static readonly uint SelectionColor = 0x00FFFFu | (uint)MathF.Round(0.2f * 255) << 24;   // (1, 1, 0, 0.2) as RGBA8
    const uint BrushColor = 0xFFFFFFFFu;

    sealed record Selection(ulong Region, byte[]? Bitmap, (float W, float S, float E, float N)? Rect, long Until);
    sealed record BrushMark(ulong Region, Vector2 Centre, float Half, (float W, float S, float E, float N)? Area, long Until);
    volatile Selection? _selection;
    volatile BrushMark? _brush;

    static long Ticks(double seconds) => Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);

    /// <summary>Show a parcel (its ParcelProperties bitmap: a bit per 4 m cell, row by row from the south-west) for this long.</summary>
    public void Select(ulong region, byte[] bitmap, double seconds) => _selection = new Selection(region, bitmap, null, Ticks(seconds));

    /// <summary>Show a rectangle of the region (metres) for this long.</summary>
    public void Select(ulong region, float west, float south, float east, float north, double seconds) =>
        _selection = new Selection(region, null, (west, south, east, north), Ticks(seconds));

    /// <summary>Keep what's shown this much longer from now (an op still running), or let it go.</summary>
    public void KeepSelection(double seconds)
    {
        if (_selection is { } s) _selection = s with { Until = Ticks(seconds) };
    }

    public void Unselect() => _selection = null;

    /// <summary>The terraform brush at a point (half its side, metres), or over an area, for this long.</summary>
    public void Brush(ulong region, Vector2 centre, float half, (float W, float S, float E, float N)? area, double seconds) =>
        _brush = new BrushMark(region, centre, half, area, Ticks(seconds));

    /// <summary>What's shown now (for tests and the API).</summary>
    public (bool Selection, bool Brush) Shown => (_selection is { } s && Stopwatch.GetTimestamp() < s.Until, _brush is { } b && Stopwatch.GetTimestamp() < b.Until);

    void EmitSelection(RenderWorld world, ulong viewRegion, List<ParcelLineVertex> into, Func<ulong, ulong, Vector3> offset)
    {
        long now = Stopwatch.GetTimestamp();
        if (_selection is { } sel && now < sel.Until && world.Region(sel.Region) is { } scene && scene.Source.HasSimulator)
        {
            var off = offset(viewRegion, sel.Region);
            int per = Math.Max(1, (int)(scene.Source.Simulator.SizeX / GridStep));
            int stride = per + 1;
            var segments = new byte[stride * stride];
            const byte South = 1, West = 2;
            void Cell(int x, int y)
            {
                int o = x + y * stride;
                segments[o] ^= South; segments[o + stride] ^= South;
                segments[o] ^= West; segments[o + 1] ^= West;
            }
            if (sel.Bitmap is { } bits && bits.Length * 8 >= per * per)
            {
                for (int y = 0; y < per; y++)
                    for (int x = 0; x < per; x++)
                        if ((bits[(x + y * per) / 8] & (1 << ((x + y * per) % 8))) != 0) Cell(x, y);
            }
            else if (sel.Rect is { } r)
            {
                for (int y = Math.Max(0, (int)(r.S / GridStep)); y < Math.Min(per, (int)MathF.Ceiling(r.N / GridStep)); y++)
                    for (int x = Math.Max(0, (int)(r.W / GridStep)); x < Math.Min(per, (int)MathF.Ceiling(r.E / GridStep)); x++)
                        Cell(x, y);
            }
            for (int y = 0; y < stride; y++)
                for (int x = 0; x < stride; x++)
                {
                    byte m = segments[x + y * stride];
                    float x1 = x * GridStep, y1 = y * GridStep;
                    if ((m & South) != 0) Post(scene, into, off, x1, y1, x1 + GridStep, y1);
                    if ((m & West) != 0) Post(scene, into, off, x1, y1, x1, y1 + GridStep);
                }
        }
        if (_brush is { } b && now < b.Until && world.Region(b.Region) is { } bs && bs.Source.HasSimulator)
        {
            var off = offset(viewRegion, b.Region);
            if (b.Area is { } a)
            {
                // over an area the viewer shows its selection; the brush's sticks would be thousands
                foreach (var (x1, y1, x2, y2) in new[] { (a.W, a.S, a.E, a.S), (a.E, a.S, a.E, a.N), (a.E, a.N, a.W, a.N), (a.W, a.N, a.W, a.S) })
                {
                    int steps = Math.Max(1, (int)(MathF.Max(MathF.Abs(x2 - x1), MathF.Abs(y2 - y1)) / GridStep));
                    for (int i = 0; i < steps; i++)
                        Post(bs, into, off, x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps,
                            x1 + (x2 - x1) * (i + 1) / steps, y1 + (y2 - y1) * (i + 1) / steps, BrushColor);
                }
                return;
            }
            int half = Math.Max(1, (int)MathF.Floor(b.Half));
            float size = bs.Source.Simulator.SizeX;
            for (int di = -half; di <= half; di++)
                for (int dj = -half; dj <= half; dj++)
                {
                    float x = b.Centre.X + di, y = b.Centre.Y + dj;
                    if (x < 0 || y < 0 || x >= size || y >= size) continue;
                    float z = bs.TerrainHeight(x, y);
                    float scale = MathF.Sqrt(2f) - MathF.Sqrt(di * di + dj * dj) / half;   // 1 in the middle, 0 at the corners
                    float top = z + 0.2f + 0.2f * MathF.Max(0, scale) * 2f;
                    Stick(into, new Vector3(x, y, z) + off, top - z);
                }
        }
    }

    /// <summary>renderOneSegment below a metre: a strip of post height from the ground at each end.</summary>
    static void Post(RegionScene scene, List<ParcelLineVertex> into, Vector3 off, float x1, float y1, float x2, float y2, uint color = 0)
    {
        if (color == 0) color = SelectionColor;
        float border = scene.Source.Simulator.SizeX - 0.1f;
        float z1 = scene.TerrainHeight(MathF.Min(x1, border), MathF.Min(y1, border));
        float z2 = scene.TerrainHeight(MathF.Min(x2, border), MathF.Min(y2, border));
        void V(float x, float y, float z) => into.Add(new ParcelLineVertex { Pos = new Vector3(x, y, z) + off, Color = color, Tex = -1 });
        V(x1, y1, z1 + PostHeight); V(x1, y1, z1); V(x2, y2, z2);
        V(x1, y1, z1 + PostHeight); V(x2, y2, z2); V(x2, y2, z2 + PostHeight);
    }

    /// <summary>A vertical stick as two crossed quads 3 cm wide (the viewer's are GL lines, a pixel wide).</summary>
    static void Stick(List<ParcelLineVertex> into, Vector3 foot, float height)
    {
        const float w = 0.015f;
        foreach (var d in new[] { new Vector3(w, 0, 0), new Vector3(0, w, 0) })
        {
            var a = foot - d; var b = foot + d; var up = new Vector3(0, 0, height);
            void V(Vector3 p) => into.Add(new ParcelLineVertex { Pos = p, Color = BrushColor, Tex = -1 });
            V(a); V(b); V(b + up);
            V(a); V(b + up); V(a + up);
        }
    }
}
