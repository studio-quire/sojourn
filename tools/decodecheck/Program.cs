// decodecheck CACHE_DIR...: every mesh asset in the folders read for the collision model both ways, the fast one
// (MeshTriangles.Highest, kept as DrawnTriangles) and LibreMetaverse's decode as CollisionShapes.DecodeMesh used it
// alone, plain and mirrored, inverted, both; any difference in the triangles (bit for bit, in order) is printed and
// counted. The renderer's decode of each at its four LODs, the trimmed asset's (MeshParts) against the whole one's,
// field by field; and 3000 random prims' shapes, indexed against the flat list the collision model kept before. Then
// what the collision model holds and allocates for these meshes: how many have a hull of their own (no drawn triangles
// kept), the drawn triangles' sizes, the physics' decoded sizes, the bytes each decode allocates, and what reading the
// triangles as rays do costs flat and indexed. And every sound: the mixer's decode and the catalog's loudness and
// length against what they replaced, bit for bit, and what each allocates. Written by Claude Opus 5.5, 2026-11-14.
using System.Diagnostics;
using LibreMetaverse;
using LibreMetaverse.Rendering;
using LibreMetaverse.StructuredData;
using Sojourn.Render.Collision;
using Sojourn.Render.World;
using V3 = System.Numerics.Vector3;

var mesher = new MeshFoundry();
int meshes = 0, withHull = 0, noHull = 0, decided = 0, undecided = 0, differ = 0, noTriangles = 0, rigged = 0, riggedNoHull = 0, noHigh = 0;
long allocFastOnly = 0, allocSlowSame = 0, indexedBytes = 0, unriggedDrawn = 0, keptIndexed = 0, allocAllParts = 0, allocDraw = 0;
long allocWholeLods = 0, allocTrimmedLods = 0;
int lodDecodes = 0, partsDiffer = 0;
long drawnBytes = 0, physBytes = 0, allocSlow = 0, allocFast = 0, msSlow = 0, msFast = 0, largest = 0;
var drawnSizes = new List<long>();
var kept = new List<DrawnTriangles>();
var physSizes = new List<long>();

V3[]? Slow(Primitive prim, byte[] data)
{
    var fm = mesher.GenerateFacetedMeshMesh(prim, data, DetailLevel.Highest);
    if (fm?.Faces is not { Count: > 0 } faces) return null;
    var tris = new List<V3>();
    foreach (var f in faces)
    {
        if (f.Vertices == null || f.Indices == null) continue;
        for (int i = 0; i + 2 < f.Indices.Count; i += 3)
        {
            int a = f.Indices[i], b = f.Indices[i + 1], c = f.Indices[i + 2], n = f.Vertices.Count;
            if (a >= n || b >= n || c >= n) continue;
            tris.Add(V(f.Vertices[a].Position)); tris.Add(V(f.Vertices[b].Position)); tris.Add(V(f.Vertices[c].Position));
        }
    }
    return tris.ToArray();
}
static V3 V(LibreMetaverse.Vector3 v) => new(v.X, v.Y, v.Z);

// two decoded meshes alike, field by field (floats bit for bit; the prim left out, it's the caller's)
static bool Deep(object? a, object? b, int depth)
{
    if (a == null || b == null) return a == null && b == null;
    if (ReferenceEquals(a, b)) return true;
    var t = a.GetType();
    if (t != b.GetType() || depth > 12) return false;
    if (a is float fa) return BitConverter.SingleToInt32Bits(fa) == BitConverter.SingleToInt32Bits((float)b);
    if (t.IsPrimitive || a is string || t.IsEnum || a is decimal) return a.Equals(b);
    if (a is System.Collections.IList la)
    {
        var lb = (System.Collections.IList)b;
        if (la.Count != lb.Count) return false;
        for (int i = 0; i < la.Count; i++) if (!Deep(la[i], lb[i], depth + 1)) return false;
        return true;
    }
    if (a is Primitive) return true;
    foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
        if (!Deep(f.GetValue(a), f.GetValue(b), depth + 1)) return false;
    return true;
}

static bool Same(V3[]? a, V3[]? b)
{
    // (the collision model keeps an empty list as none)
    if (a is not { Length: > 0 }) return b is not { Length: > 0 };
    if (b == null || a.Length != b.Length) return false;
    for (int i = 0; i < a.Length; i++)
        if (BitConverter.SingleToInt32Bits(a[i].X) != BitConverter.SingleToInt32Bits(b[i].X) ||
            BitConverter.SingleToInt32Bits(a[i].Y) != BitConverter.SingleToInt32Bits(b[i].Y) ||
            BitConverter.SingleToInt32Bits(a[i].Z) != BitConverter.SingleToInt32Bits(b[i].Z)) return false;
    return true;
}

foreach (var dir in args)
foreach (var file in Directory.EnumerateFiles(dir))
{
    byte[] data;
    bool isRigged = false;
    try { data = File.ReadAllBytes(file); } catch { continue; }
    if (data.Length < 16 || data[0] != (byte)'{') continue;
    try
    {
        using var ms = new MemoryStream(data);
        if (OSDParser.DeserializeLLSDBinary(ms) is not OSDMap h || !(h.ContainsKey("high_lod") || h.ContainsKey("medium_lod") || h.ContainsKey("physics_convex"))) continue;
        isRigged = h.ContainsKey("skin");
        if (!h.ContainsKey("high_lod")) noHigh++;
    }
    catch { continue; }
    meshes++;
    if (isRigged) rigged++;
    MeshPhysics? phys = null;
    try { phys = MeshPhysics.FromAsset(data); } catch { }
    if (phys != null) physSizes.Add(((phys.Hull?.Length ?? 0) + (phys.Decomposition?.Length ?? 0) + (phys.Triangles?.Length ?? 0)) * 12L);
    bool hull = phys?.Hull != null;
    {
        // what the renderer's decode costs (every mesh it draws, at the highest LOD here), and the share of it that is
        // inflating every part of the asset (AssetMesh.Decode) whatever LOD is wanted; then the trimmed asset's decode
        // (MeshParts) at each LOD against the whole one's, field by field
        var prim0 = new Primitive { Sculpt = new Primitive.SculptData { Type = SculptType.Mesh } };
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        new LibreMetaverse.Assets.AssetMesh(UUID.Zero, data).Decode();
        long b1 = GC.GetAllocatedBytesForCurrentThread();
        mesher.GenerateFacetedMeshMesh(prim0, data, DetailLevel.Highest);
        long b2 = GC.GetAllocatedBytesForCurrentThread();
        allocAllParts += b1 - b0;
        allocDraw += b2 - b1;
        foreach (var lod in new[] { DetailLevel.Highest, DetailLevel.High, DetailLevel.Medium, DetailLevel.Low })
        {
            long c0 = GC.GetAllocatedBytesForCurrentThread();
            var whole = mesher.GenerateFacetedMeshMesh(prim0, data, lod);
            long c1 = GC.GetAllocatedBytesForCurrentThread();
            var trimmed = MeshParts.Decode(mesher, prim0, data, lod);
            long c2 = GC.GetAllocatedBytesForCurrentThread();
            lodDecodes++;
            allocWholeLods += c1 - c0;
            allocTrimmedLods += c2 - c1;
            if (!Deep(whole, trimmed, 0))
            {
                partsDiffer++;
                Console.WriteLine($"DIFFERS (trimmed) {System.IO.Path.GetFileName(file)} {lod}");
            }
        }
    }
    if (hull) withHull++; else noHull++;
    if (!hull && isRigged) riggedNoHull++;
    var id = UUID.TryParse(System.IO.Path.GetFileName(file), out var u) ? u : UUID.Random();
    foreach (byte flags in new byte[] { 0, 0x80, 0x40, 0xC0 })
    {
        var prim = new Primitive { Sculpt = new Primitive.SculptData { SculptTexture = id, Type = (SculptType)(5 | flags) } };
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        var t0 = Stopwatch.GetTimestamp();
        var slow = Slow(prim, data);
        var t1 = Stopwatch.GetTimestamp();
        long a1 = GC.GetAllocatedBytesForCurrentThread();
        var result = MeshTriangles.Highest(prim, data);
        var fast = result == null ? null : result.Triangles?.Flat() ?? Array.Empty<V3>();
        if (result?.Triangles is { } dt && flags == 0 && !hull && !isRigged) { keptIndexed += dt.Bytes; kept.Add(dt); }
        var t2 = Stopwatch.GetTimestamp();
        long a2 = GC.GetAllocatedBytesForCurrentThread() - (result?.Triangles is { } ft ? ft.Count * 12L + 24 : 0);   // (Flat(), the comparison's own)
        if (fast == null) undecided++;
        else
        {
            decided++;
            if (!Same(fast, slow))
            {
                differ++;
                int at = 0;
                while (slow != null && at < Math.Min(fast.Length, slow.Length) && Same([fast[at]], [slow[at]])) at++;
                Console.WriteLine($"DIFFERS {System.IO.Path.GetFileName(file)} flags {flags:x2}: fast {fast.Length} corners, LibreMetaverse {slow?.Length.ToString() ?? "none"}" +
                                  (slow != null && at < Math.Min(fast.Length, slow.Length) ? $", first at {at}: {fast[at].X:R},{fast[at].Y:R},{fast[at].Z:R} against {slow[at].X:R},{slow[at].Y:R},{slow[at].Z:R}" : ""));
            }
        }
        if (flags != 0 || hull) continue;   // (what the collision model does: no-hull meshes, plain)
        allocSlow += a1 - a0;
        allocFast += fast != null ? a2 - a1 : a1 - a0;
        if (fast != null) { allocFastOnly += a2 - a1; allocSlowSame += a1 - a0; }
        msSlow += (long)Stopwatch.GetElapsedTime(t0, t1).TotalMicroseconds;
        msFast += (long)Stopwatch.GetElapsedTime(t1, t2).TotalMicroseconds;
        if (slow is { Length: > 0 } s0 && !isRigged)
        {
            // kept as unique corners and indices instead (what that storage would hold)
            int unique = new HashSet<V3>(s0).Count;
            indexedBytes += unique * 12L + s0.Length * (unique <= 65536 ? 2L : 4L);
            unriggedDrawn += s0.Length * 12L;
        }
        if (slow is { Length: > 0 } s) { drawnBytes += s.Length * 12L; drawnSizes.Add(s.Length * 12L); largest = Math.Max(largest, s.Length * 12L); }
        else noTriangles++;
    }
}

// sounds: the mixer's decode (SoundBank.Decode) against the one it replaced, sample for sample, and the catalog's
// loudness and length (SoundConvert.VorbisLevels) against the sums over DecodeVorbis's samples it replaced
int sounds = 0, soundsDiffer = 0;
long aBankOld = 0, aBankNew = 0, aCatOld = 0, aCatNew = 0, soundBytes = 0;
foreach (var dir in args)
foreach (var file in Directory.EnumerateFiles(dir))
{
    byte[] ogg;
    try { ogg = File.ReadAllBytes(file); } catch { continue; }
    if (ogg.Length < 4 || ogg[0] != 'O' || ogg[1] != 'g' || ogg[2] != 'g' || ogg[3] != 'S') continue;
    try
    {
        long t0 = GC.GetAllocatedBytesForCurrentThread();
        var (oldData, oldCh, oldRate) = OldBank(ogg);
        long t1 = GC.GetAllocatedBytesForCurrentThread();
        var newData = Sojourn.Session.Audio.SoundBank.Decode(ogg, out int ch, out int rate, out int count);
        long t2 = GC.GetAllocatedBytesForCurrentThread();
        var d = Sojourn.Core.Media.SoundConvert.DecodeVorbis(ogg);
        double sum = 0, peak = 0;
        foreach (var x in d.Samples) { sum += x * x; peak = Math.Max(peak, Math.Abs(x)); }
        long t3 = GC.GetAllocatedBytesForCurrentThread();
        var lv = Sojourn.Core.Media.SoundConvert.VorbisLevels(ogg);
        long t4 = GC.GetAllocatedBytesForCurrentThread();
        sounds++;
        soundBytes += oldData.Length * 4L;
        (aBankOld, aBankNew, aCatOld, aCatNew) = (aBankOld + t1 - t0, aBankNew + t2 - t1, aCatOld + t3 - t2, aCatNew + t4 - t3);
        bool same = ch == oldCh && rate == oldRate && count == oldData.Length
                    && System.Runtime.InteropServices.MemoryMarshal.Cast<float, int>(newData.AsSpan(0, count)).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.Cast<float, int>(oldData.AsSpan()))
                    && lv.Count == d.Samples.Length && lv.SampleRate == d.SampleRate
                    && BitConverter.DoubleToInt64Bits(lv.SumSquares) == BitConverter.DoubleToInt64Bits(sum)
                    && BitConverter.DoubleToInt64Bits(lv.Peak) == BitConverter.DoubleToInt64Bits(peak);
        if (!same) { soundsDiffer++; Console.WriteLine($"DIFFERS (sound) {System.IO.Path.GetFileName(file)}"); }
    }
    catch (Exception e) { Console.WriteLine($"sound {System.IO.Path.GetFileName(file)}: {e.GetType().Name}"); }
}
if (sounds > 0)
    Console.WriteLine($"{sounds} sounds ({MB(soundBytes)} of samples): {soundsDiffer} differ; the mixer's decode {MB(aBankOld)} allocated before, {MB(aBankNew)} now; " +
                      $"the catalog's {MB(aCatOld)} before, {MB(aCatNew)} now");
differ += soundsDiffer;

// SoundBank.Load's decode as it was before SoundBank.Decode
static (float[] Data, int Channels, int Rate) OldBank(byte[] ogg)
{
    using var reader = new NVorbis.VorbisReader(new MemoryStream(ogg), closeOnDispose: true);
    int ch = reader.Channels, rate = reader.SampleRate;
    if (ch < 1 || rate <= 0) throw new InvalidDataException("no audio");
    var pcm = new List<float>(reader.TotalSamples is > 0 and < 48000 * 60 ? (int)reader.TotalSamples * ch : 1 << 16);
    var buf = new float[4096 * ch];
    int read;
    while ((read = reader.ReadSamples(buf, 0, buf.Length)) > 0)
        for (int i = 0; i < read; i++) pcm.Add(buf[i]);
    int keep = Math.Min(ch, 2);
    if (keep == ch) return (pcm.ToArray(), keep, rate);
    int frames0 = pcm.Count / ch;
    var data = new float[frames0 * keep];
    for (int f = 0; f < frames0; f++) for (int c = 0; c < keep; c++) data[f * keep + c] = pcm[f * ch + c];
    return (data, keep, rate);
}

int primShapes = 0, primDiffer = 0;
long primFlat = 0, primIndexed = 0;
var rnd = new Random(1);
for (int k = 0; k < 3000; k++)
{
    var cd = new Primitive.ConstructionData
    {
        PCode = PCode.Prim, Material = Material.Wood,
        ProfileCurve = (ProfileCurve)rnd.Next(0, 6), PathCurve = rnd.Next(2) == 0 ? PathCurve.Line : PathCurve.Circle,
        ProfileBegin = rnd.Next(3) == 0 ? (float)rnd.NextDouble() * 0.4f : 0, ProfileEnd = rnd.Next(3) == 0 ? 1 - (float)rnd.NextDouble() * 0.4f : 1,
        ProfileHollow = rnd.Next(3) == 0 ? (float)rnd.NextDouble() * 0.9f : 0,
        PathBegin = 0, PathEnd = 1, PathScaleX = 1, PathScaleY = rnd.Next(4) == 0 ? 0.25f + (float)rnd.NextDouble() * 0.75f : 1,
        PathTwist = rnd.Next(4) == 0 ? (float)rnd.NextDouble() : 0, PathRevolutions = 1,
    };
    foreach (int lod in new[] { 2 })
    {
        var faces = Sojourn.Render.Geometry.VolumeBuilder.BuildPrim(cd, Sojourn.Render.Geometry.VolumeBuilder.DetailForLod(lod));
        int n = 0;
        foreach (var f in faces) n += f.Indices.Length / 3 * 3;
        var flat = new V3[n];
        int at = 0;
        foreach (var f in faces)
            for (int i = 0; i + 2 < f.Indices.Length; i += 3)
            {
                flat[at++] = f.Positions[f.Indices[i]];
                flat[at++] = f.Positions[f.Indices[i + 1]];
                flat[at++] = f.Positions[f.Indices[i + 2]];
            }
        var ix = DrawnTriangles.Of(faces);
        primShapes++;
        if (!Same(flat, ix?.Flat())) primDiffer++;
        primFlat += flat.Length * 12L;
        primIndexed += ix?.Bytes ?? 0;
    }
}
Console.WriteLine($"{primShapes} prim shapes at LOD 2 (random parameters): {primDiffer} differ from the old flat list; {MB(primFlat)} flat, {MB(primIndexed)} indexed");
differ += primDiffer;

// reading every triangle's corners as a ray does (Hit's loop), flat against indexed: what the indexing costs a ray
if (kept.Count > 0)
{
    var flats = kept.Select(k => k.Flat()).ToList();
    float sink = 0;
    double Time(Action a) { a(); var t = Stopwatch.GetTimestamp(); for (int r = 0; r < 20; r++) a(); return Stopwatch.GetElapsedTime(t).TotalMilliseconds / 20; }
    double tf = Time(() => { foreach (var f in flats) for (int i = 0; i + 2 < f.Length; i += 3) { var a = f[i]; var b = f[i + 1]; var c = f[i + 2]; sink += V3.Cross(b - a, c - a).X; } });
    double ti = Time(() => { foreach (var d in kept) for (int i = 0; i + 2 < d.Count; i += 3) { var a = d[i]; var b = d[i + 1]; var c = d[i + 2]; sink += V3.Cross(b - a, c - a).X; } });
    Console.WriteLine($"  every triangle of those read once (as rays read them): flat {tf:0.0} ms, indexed {ti:0.0} ms ({sink > 0})");
}

static string MB(long b) => $"{b / 1048576.0:0.0} MB";
static long Pct(List<long> v, double p) { if (v.Count == 0) return 0; v.Sort(); return v[(int)Math.Min(v.Count - 1, p * v.Count)]; }
Console.WriteLine($"{meshes} meshes: {withHull} with a hull of their own, {noHull} without (drawn triangles kept for those, {noTriangles} of them none)");
Console.WriteLine($"  rigged (a skin, left to LibreMetaverse): {rigged}, {riggedNoHull} of them without a hull; no high LOD: {noHigh}");
Console.WriteLine($"  compared both ways (x4 flags): {decided} read fast and the same unless listed above ({differ} differ), {undecided} left to LibreMetaverse");
if (drawnSizes.Count > 0)
    Console.WriteLine($"  drawn triangles kept: {MB(drawnBytes)} in all, {drawnBytes / drawnSizes.Count / 1024} KB each on average, median {Pct(drawnSizes, 0.5) / 1024} KB, " +
                      $"90% {Pct(drawnSizes, 0.9) / 1024} KB, largest {MB(largest)}; {drawnSizes.Count(x => x >= 85_000)} of them on the large object heap ({MB(drawnSizes.Where(x => x >= 85_000).Sum())})");
if (physSizes.Count > 0)
    Console.WriteLine($"  physics decoded: {MB(physSizes.Sum())} for {physSizes.Count} meshes, {physSizes.Sum() / physSizes.Count / 1024} KB each on average (the status line counts 4 KB), median {Pct(physSizes, 0.5) / 1024} KB, 90% {Pct(physSizes, 0.9) / 1024} KB");
Console.WriteLine($"  the unrigged ones' drawn triangles: {MB(unriggedDrawn)} as three corners each, {MB(keptIndexed)} as the collision model now keeps them (each face's corners and indices), {MB(indexedBytes)} if every corner were kept once");
Console.WriteLine($"  the renderer's decode of every mesh once at its highest LOD: {MB(allocDraw)} allocated, {MB(allocAllParts)} of it inflating every part");
Console.WriteLine($"  at each of the four LODs ({lodDecodes} decodes): the whole asset {MB(allocWholeLods)} allocated, the trimmed one {MB(allocTrimmedLods)}; {partsDiffer} differ");
differ += partsDiffer;
Console.WriteLine($"  decoding the no-hull meshes once each: LibreMetaverse {MB(allocSlow)} allocated, {msSlow / 1000} ms; fast where it could {MB(allocFast)}, {msFast / 1000} ms");
Console.WriteLine($"  those it read fast: {MB(allocSlowSame)} allocated by LibreMetaverse's decode, {MB(allocFastOnly)} by the fast one");
return differ;
