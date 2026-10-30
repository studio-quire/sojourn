using System.Diagnostics;
using LibreMetaverse;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Session.Offscreen;

/// <summary>A picture from a camera of its own, for describing (docs/access.md > M5): where it stands and looks, in the
/// agent's region frame. ShowSelf: the person's own avatar drawn (pictures of them); Hide: avatars left out (others
/// standing between the camera and the person described).</summary>
public sealed record ShotSpec(string Label, Vector3 Position, Vector3 Forward, float VerticalFov, int Width, int Height)
{
    public Vector3 Up { get; init; } = Vector3.UnitZ;
    public bool ShowSelf { get; init; }
    public IReadOnlyCollection<UUID>? Hide { get; init; }
    /// <summary>Who or what the picture is of (an avatar's id or an object's root): how much of the picture it fills is
    /// counted.</summary>
    public UUID Subject { get; init; }
    /// <summary>Lit as at midday when the place is dark (its sun down or low), for true colours: a person, an object,
    /// what someone wears. Pictures of the place itself keep its own light.</summary>
    public bool Daylight { get; init; }
}

/// <summary>Something a shot's grid of rays met: a person (what they wear counts as them), an object by its linkset's
/// root, in how many of the grid's cells, the middle of those cells (0-1 across, 0-1 down) and the nearest distance.</summary>
public sealed record SeenThing(PickKind Kind, UUID Id, int Cells, float X, float Y, float Distance);

/// <param name="Relit">drawn under the midday sky instead of the place's own (it was dark)</param>
public sealed record Shot(ShotSpec Spec, byte[] Rgba, int Width, int Height, int Pending, IReadOnlyList<SeenThing> Seen,
    int Cells, int SkyCells, int GroundCells, int SubjectCells, bool Relit = false);

public readonly record struct ShotBatch(IReadOnlyList<Shot> Shots, ulong Region);

public sealed partial class OffscreenRenderer
{
    /// <summary>The grid of rays cast through each shot after it's drawn, to know what's visible where (16 x 9).</summary>
    public const int SeenCols = 16, SeenRows = 9;

    /// <summary>
    /// Pictures from cameras of their own: <paramref name="plan"/> places them on the render thread (where the world as
    /// drawn and the picks are), each is drawn until what's in it has arrived (at most <paramref name="settle"/> seconds,
    /// or a second without progress), read back, and a grid of picks cast through it says what's visible where (picks
    /// never count the person's own avatar). Null when no renderer is up, the plan gave nothing, or it took too long.
    /// </summary>
    public Task<ShotBatch?> ShootAsync(Func<FrameRenderer, Simulator, IReadOnlyList<ShotSpec>?> plan, double settle, CancellationToken ct = default) =>
        Queue<ShotBatch>((r, done) =>
        {
            if (_s.Client is not { Network.CurrentSim: { } sim } c) { done(null); return true; }
            var specs = plan(r, sim);
            if (specs is not { Count: > 0 }) { done(null); return true; }
            r.AvatarLimits.Self = c.Self.AgentID;
            // dark: the sun below about 10 degrees in the sky last drawn (the place's own: nothing relit yet this batch), and
            // no sky of the person's own (a shot's sky preset clears the renderer's personal one after it)
            bool dark = r.LastEnvironment?.Snapshot.Sky is { } seenSky && seenSky.SunDirection.Z < 0.17f
                        && r.Personal.Current is { Sky: null, Day: null };
            long t0 = Stopwatch.GetTimestamp();
            var shots = new List<Shot>();
            foreach (var spec in specs)
            {
                // later shots share what the first one waited for: each gets what's left of twice the budget, at least a second
                double left = Math.Max(1, 2 * settle - Stopwatch.GetElapsedTime(t0).TotalSeconds);
                shots.Add(Shoot(r, sim, spec, Math.Min(settle, left), spec.Daylight && dark));
            }
            _pictures += shots.Count;
            done(new ShotBatch(shots, sim.Handle));
            return true;
        }, TimeSpan.FromSeconds(settle * 2 + 20), ct);

    Shot Shoot(FrameRenderer r, Simulator sim, ShotSpec spec, double settle, bool relight)
    {
        var hide = new HashSet<UUID>(_s.HiddenInView(hideSelf: !spec.ShowSelf) ?? []);
        if (spec.Hide != null) hide.UnionWith(spec.Hide);
        var req = new FrameRequest
        {
            RegionHandle = sim.Handle, Position = spec.Position, Forward = Vector3.Normalize(spec.Forward), Up = spec.Up,
            Width = Math.Clamp(spec.Width, 64, LookMax), Height = Math.Clamp(spec.Height, 64, LookMax),
            VerticalFov = spec.VerticalFov, DrawDistance = _s.Camera.FarClip, HideAvatars = hide.Count > 0 ? hide : null,
            // the viewer's own midday (LLSettingsSky's default), put on at once and taken off by the next frame without it
            LocalSky = relight ? Sojourn.Render.Environment.DefaultSettingsAssets.DefaultSkyMiddayRPId : null,
        };
        long t0 = Stopwatch.GetTimestamp();
        int best = int.MaxValue;
        long improved = t0;
        FrameStats st;
        while (true)
        {
            r.Motion.BeginFrame();
            st = Render(r, req);
            _lookFrames++;
            if (st.Pending < best) (best, improved) = (st.Pending, Stopwatch.GetTimestamp());
            if (_stop || st.Pending == 0 || Stopwatch.GetElapsedTime(t0).TotalSeconds >= settle || Stopwatch.GetElapsedTime(improved).TotalSeconds > 1) break;
            _wake.WaitOne(Math.Max(0, 50 - (int)st.TotalCpuMs));
        }
        // the picture's pixels come back on a worker once the frame is retired (FrameRenderer.RecordCapture): waited for,
        // with a plain frame drawn now and then in case retiring waits on the next one
        (byte[] Px, int W, int H)? got = null;
        using var arrived = new ManualResetEventSlim();
        r.Motion.BeginFrame();
        st = Render(r, req with { CaptureTo = (px, pw, ph) => { got = (px, pw, ph); arrived.Set(); } });
        for (int i = 0; i < 4 && !arrived.Wait(250); i++)
        {
            r.Motion.BeginFrame();
            Render(r, req);
        }
        var (rgba, w, h) = got ?? (new byte[req.Width * req.Height * 4], req.Width, req.Height);
        return Seen(r, sim, spec, req, rgba, w, h, got == null ? -1 : st.Pending) with { Relit = relight };
    }

    Shot Seen(FrameRenderer r, Simulator sim, ShotSpec spec, FrameRequest req, byte[] rgba, int w, int h, int pending)
    {
        var fwd = req.Forward;
        var right = Vector3.Cross(fwd, req.Up);
        right = right.LengthSquared() > 1e-8f ? Vector3.Normalize(right) : Vector3.UnitY;
        var up = Vector3.Normalize(Vector3.Cross(right, fwd));
        float tanV = MathF.Tan(req.VerticalFov / 2), tanH = tanV * w / h;
        var tally = new Dictionary<(PickKind, UUID), (int N, float X, float Y, float D)>();
        int sky = 0, ground = 0, subject = 0;
        for (int j = 0; j < SeenRows; j++)
        for (int i = 0; i < SeenCols; i++)
        {
            float u = (i + 0.5f) / SeenCols, v = (j + 0.5f) / SeenRows;
            var dir = fwd + right * ((u * 2 - 1) * tanH) + up * ((1 - v * 2) * tanV);
            if (r.Pick(sim.Handle, req.Position, dir, req.DrawDistance, 0, ground: true) is not { } hit) { sky++; continue; }
            if (hit.Kind == PickKind.Ground) { ground++; continue; }
            var key = hit.WornBy != UUID.Zero ? (PickKind.Avatar, hit.WornBy) : hit.Kind == PickKind.Avatar ? (PickKind.Avatar, hit.Id) : (PickKind.Object, hit.RootId);
            // the subject itself, and what it wears or what it's part of
            if (spec.Subject != UUID.Zero && (key.Item2 == spec.Subject || hit.RootId == spec.Subject || hit.Id == spec.Subject)) subject++;
            var t = tally.TryGetValue(key, out var x) ? x : (N: 0, X: 0f, Y: 0f, D: float.MaxValue);
            tally[key] = (t.N + 1, t.X + u, t.Y + v, MathF.Min(t.D, hit.Distance));
        }
        var seen = tally.Select(kv => new SeenThing(kv.Key.Item1, kv.Key.Item2, kv.Value.N, kv.Value.X / kv.Value.N, kv.Value.Y / kv.Value.N, kv.Value.D))
            .OrderByDescending(s => s.Cells).ToList();
        return new Shot(spec, rgba, w, h, pending, seen, SeenCols * SeenRows, sky, ground, subject);
    }
}
