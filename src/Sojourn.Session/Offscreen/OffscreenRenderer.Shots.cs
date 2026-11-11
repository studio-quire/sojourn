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
    /// <summary>Where the subject's middle is (the point the camera was aimed at): how far a picture lit as at midday
    /// carries its light at the camera.</summary>
    public Vector3? Target { get; init; }
    /// <summary>Lit as at midday with a light at the camera, whatever the place's light, for true colours: a person, an
    /// object, what someone wears. Pictures of the place itself keep its own light. (Until 2026-11-11 only when the place
    /// was dark or the picture came out dim, and without the camera's light; at the local grid's dusk, a low sun behind
    /// the subject and bright sky around it, neither held and a moss-green sweater came out dark teal. The place's own
    /// light moves colours whenever the sun isn't on the side the camera sees, at any hour, and relighting every picture
    /// costs about nothing, where measuring one and drawing it again cost a picture.)</summary>
    public bool Daylight { get; init; }
    /// <summary>A picture on the move (a ride being recorded): taken once the place's settling ends, without waiting for
    /// anyone's or anything's look (by then it's somewhere else).</summary>
    public bool Quick { get; init; }
}

/// <summary>Something a shot's grid of rays met: a person (what they wear counts as them), an object by its linkset's
/// root, in how many of the grid's cells, the middle of those cells (0-1 across, 0-1 down) and the nearest distance.</summary>
public sealed record SeenThing(PickKind Kind, UUID Id, int Cells, float X, float Y, float Distance);

/// <param name="Pending">everything in view still loading, out to the draw distance (for the log: on a busy place mostly
/// far things)</param>
/// <param name="Relit">drawn under the midday sky instead of the place's own (ShotSpec.Daylight)</param>
public sealed record Shot(ShotSpec Spec, byte[] Rgba, int Width, int Height, int Pending, IReadOnlyList<SeenThing> Seen,
    int Cells, int SkyCells, int GroundCells, int SubjectCells, bool Relit = false)
{
    /// <summary>The height of the place's own sun as the picture was taken (the sine of its elevation; null before the
    /// region's environment arrived): for saying what the place's light is when the picture's isn't it.</summary>
    public float? PlaceSun { get; init; }
    /// <summary>How far the look of the subject and of each thing seen in it had arrived as it was drawn
    /// (FrameRenderer.LoadOf), by id.</summary>
    public IReadOnlyDictionary<UUID, LookLoad> Loads { get; init; } = new Dictionary<UUID, LookLoad>();
    /// <summary>Seconds it watched its subject's look (from its start; around the person, the big things in it from when
    /// the place had settled): what it took of the batch's wait for looks.</summary>
    public double Waited { get; init; }
    /// <summary>Taken with some of what it waited for still on its way.</summary>
    public bool LookUnfinished { get; init; }
}

public readonly record struct ShotBatch(IReadOnlyList<Shot> Shots, ulong Region);

public sealed partial class OffscreenRenderer
{
    /// <summary>The grid of rays cast through each shot after it's drawn, to know what's visible where (16 x 9).</summary>
    public const int SeenCols = 16, SeenRows = 9;

    /// <summary>How long a batch of pictures may wait for its subjects' looks beyond the place's settling, and how long
    /// without any of it arriving: a person's bakes and the colour textures of what they wear, an object's, the shapes.
    /// On a busy place what's in view never all arrives, so the place's settling ends on time or a second or two without
    /// progress, which had left a person beside the blind tester untextured in every picture (grey-white hair and clothes
    /// described as his colours, 2026-11-01). On the processor longer: decoding is slower there, and with no frames
    /// between pictures nothing of anyone's look stays loaded from one picture to the next.</summary>
    static double LookWait(bool software) => software ? 30 : 12;
    static double LookStall(FrameRenderer r) => r.Software ? 8 : 4;

    /// <summary>
    /// Pictures from cameras of their own: <paramref name="plan"/> places them on the render thread (where the world as
    /// drawn and the picks are), each is drawn until what's in it has arrived (at most <paramref name="settle"/> seconds,
    /// or a second without progress) and its subject's look has (<see cref="LookWait"/>; with no subject, the big things
    /// in it), read back, and a grid of picks cast through it says what's visible where (picks never count the person's
    /// own avatar), with how far the look of the subject and of each thing seen had arrived. Null when no renderer is
    /// up, the plan gave nothing, or it took too long.
    /// </summary>
    public Task<ShotBatch?> ShootAsync(Func<FrameRenderer, Simulator, IReadOnlyList<ShotSpec>?> plan, double settle, CancellationToken ct = default) =>
        Queue<ShotBatch>((r, done) =>
        {
            if (_s.Client is not { Network.CurrentSim: { } sim } c) { done(null); return true; }
            var specs = plan(r, sim);
            if (specs is not { Count: > 0 }) { done(null); return true; }
            Seen(r);   // (unseen until now: what's in them loads while they settle)
            r.AvatarLimits.Self = c.Self.AgentID;
            // people and things lit as at midday (ShotSpec.Daylight), unless the person had a sky of their own on this
            // renderer (a picture's preset would replace it and clear it after). The last batch's preset stays on until a
            // frame without it: a batch straight after another (no frames between on the processor) is still relit.
            bool relight = r.Personal.Current is { Day: null } and ({ Sky: null } or { From: "preset" });
            var placeSun = r.PlaceEnvironment(sim.Handle, specs[0].Position.Z)?.Sky.SunDirection.Z;
            long t0 = Stopwatch.GetTimestamp();
            var shots = new List<Shot>();
            double lookLeft = LookWait(r.Software);
            bool stalled = false;
            var whole = DrawnWhole(r, c.Self.AgentID, sim, specs);
            try
            {
                foreach (var spec in specs)
                {
                    // later shots share what the first one waited for: each gets what's left of twice the budget, at least a
                    // second, and what's left of the batch's wait for looks, at least two
                    double left = Math.Max(1, 2 * settle - Stopwatch.GetElapsedTime(t0).TotalSeconds);
                    var shot = Shoot(r, sim, spec, Math.Min(settle, left), spec.Daylight && relight, Math.Max(2, lookLeft), stalled)
                        with { PlaceSun = placeSun };
                    lookLeft -= shot.Waited;
                    // a look that stopped arriving for one picture isn't waited for as long again in the next
                    stalled |= shot.LookUnfinished;
                    shots.Add(shot);
                }
            }
            finally
            {
                foreach (var id in whole) r.AvatarLimits.Overrides.TryRemove(id, out _);
            }
            _pictures += shots.Count;
            done(new ShotBatch(shots, sim.Handle));
            return true;
        }, PictureWait(settle), ct);

    /// <summary>A person asked about is drawn whole for their pictures, however complex (a stand-in over the limit says
    /// nothing of how they look: the blind tester's club night, 2026-11-05, "too complex for your viewer to draw"), since
    /// this view is there for describing and it's one avatar for a moment. Not someone the person chose never to draw.
    /// What it puts on, for the batch to take off again.</summary>
    List<UUID> DrawnWhole(FrameRenderer r, UUID me, Simulator sim, IReadOnlyList<ShotSpec> specs)
    {
        var whole = new List<UUID>();
        if (_s.World.Region(_s.GridKey, sim.Handle) is not { } w) return whole;
        foreach (var id in specs.Select(s => s.Subject).Distinct())
        {
            if (id == UUID.Zero || id == me || w.AvatarById(id) is null || !r.AvatarLimits.Overrides.TryAdd(id, AvatarRender.Always)) continue;
            whole.Add(id);
            if (r.AvatarLimits.Status.FirstOrDefault(st => st.Id == id) is { Drawn: "jelly" } now)
                _log($"the view out of sight: {id} drawn whole for their pictures, over the limit at complexity {now.Complexity}");
        }
        return whole;
    }

    /// <summary>The light at the camera in a picture lit as at midday (FrameRequest.CameraLight): a prim light's
    /// intensity, and its radius as a multiple of the distance to the subject's middle (so it reaches every subject alike:
    /// at that, a point light without falloff gives 0.72 of its light there). The midday sky's sun is straight overhead,
    /// which leaves a standing person's front, and anything upright, to the sky's blue: on the local grid (2026-11-11)
    /// the scripted test's moss green, (74, 103, 65) in its texture, came out (13, 30, 33) under it whichever way the
    /// tester faced; with this light (70, 100, 69) every way, on the GPU and on the processor alike (0.6: (51, 75, 54);
    /// 2: (88, 125, 83)). In the place's own afternoon light it had been (65, 93, 65) facing the sun and (30-37, 48-58,
    /// 40-44) the other three ways.</summary>
    const float FlashIntensity = 1.2f, FlashReach = 2.5f;

    Shot Shoot(FrameRenderer r, Simulator sim, ShotSpec spec, double settle, bool relight, double lookWait, bool stalledBefore)
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
            CameraLight = relight ? (FlashIntensity, FlashReach * Vector3.Distance(spec.Position, spec.Target ?? spec.Position + spec.Forward * 3)) : null,
        };
        long t0 = Stopwatch.GetTimestamp();
        int best = int.MaxValue;
        long improved = t0;
        FrameStats st;
        int planned = 0;
        // on the processor the settling frames are only planned and the picture itself costs a second or more, so what's
        // in it gets longer to arrive: twice the settling, two seconds without progress (a slow PC's decoding is slower too)
        double stall = r.Software ? 2 : 1;
        if (r.Software) settle *= 2;
        // what it waits for besides: the subject's look, or with none (around the person) the people and things filling
        // the picture, found once the place has settled
        var watch = new List<UUID>();
        if (spec.Subject != UUID.Zero && !spec.Quick) watch.Add(spec.Subject);
        bool lookedAround = spec.Subject != UUID.Zero || spec.Quick;
        // (judged by what this picture's frames asked for: on the processor its drawn frame alone takes seconds)
        long since = Environment.TickCount64;
        int Waiting() => watch.Sum(id => r.LoadOf(sim.Handle, id, since)?.Waiting(blurryToo: !r.TexturesHeld) ?? 0);
        long watchFrom = t0, lookImproved = t0;
        int lookBest = int.MaxValue, lookLeft = 0;
        double lookStall = stalledBefore ? stall : LookStall(r);
        while (true)
        {
            r.Motion.BeginFrame();
            st = Render(r, Settling(r, req));
            _lookFrames++;
            planned++;
            if (st.Pending < best) (best, improved) = (st.Pending, Stopwatch.GetTimestamp());
            lookLeft = watch.Count > 0 ? Waiting() : 0;
            if (lookLeft < lookBest) (lookBest, lookImproved) = (lookLeft, Stopwatch.GetTimestamp());
            if (_stop) break;
            bool placeDone = st.Pending == 0 || Stopwatch.GetElapsedTime(t0).TotalSeconds >= settle || Stopwatch.GetElapsedTime(improved).TotalSeconds > stall;
            bool lookDone = lookLeft == 0 || Stopwatch.GetElapsedTime(watchFrom).TotalSeconds >= lookWait
                            || Stopwatch.GetElapsedTime(lookImproved).TotalSeconds > lookStall;
            if (placeDone && lookDone)
            {
                if (lookedAround) break;
                // around the person: the people and things filling the picture within 25 m, waited for as a subject is
                lookedAround = true;
                watch.AddRange(Tally(r, sim, req, out _, out _, out _).Where(s => s.Cells >= 5 && s.Distance <= 25).Take(6).Select(s => s.Id));
                if (watch.Count == 0) break;
                (watchFrom, lookImproved, lookBest) = (Stopwatch.GetTimestamp(), Stopwatch.GetTimestamp(), int.MaxValue);
                continue;
            }
            _wake.WaitOne(Math.Max(0, 50 - (int)st.TotalCpuMs));
        }
        double waited = watch.Count > 0 ? Stopwatch.GetElapsedTime(watchFrom).TotalSeconds : 0;
        if (watch.Count > 0 && (waited > 0.5 || lookLeft > 0))   // (in a tester's log: how long, and what it still lacked)
            _log($"the view out of sight: \"{spec.Label}\" waited {waited:0.0} s for " +
                 (spec.Subject != UUID.Zero ? "its subject's look" : $"the look of {watch.Count} things in it") +
                 (lookLeft > 0 ? $", {lookLeft} parts of it still loading" : ", all there") +
                 (spec.Subject != UUID.Zero && r.LoadOf(sim.Handle, spec.Subject, since) is { } sl
                     ? $" ({(sl.Person ? $"drawn {sl.Drawn ?? "not"}, bakes {sl.Bakes - sl.BakesLoading - sl.BakesMissing} of {sl.Bakes}, " : "")}" +
                       $"{sl.Parts.Count} {(sl.Person ? "things worn" : "linkset")}: surfaces {sl.Faces - sl.Loading} of {sl.Faces} with their colours, " +
                       $"{sl.Parts.Sum(p => p.Blurry)} blurry, {sl.Parts.Sum(p => p.ShapesWaiting)} shapes on their way)" : ""));
        // the picture's pixels come back on a worker once the frame is retired (FrameRenderer.RecordCapture): waited for,
        // with a plain frame drawn now and then in case retiring waits on the next one
        (byte[] Px, int W, int H)? got = null;
        using var arrived = new ManualResetEventSlim();
        r.Motion.BeginFrame();
        long drawn = Stopwatch.GetTimestamp();
        var share = r.Software ? ProcessorShare.Now() : default;
        double settled = Stopwatch.GetElapsedTime(t0, drawn).TotalSeconds;
        st = Render(r, req with { CaptureTo = (px, pw, ph) => { got = (px, pw, ph); arrived.Set(); } });
        if (r.Software)   // (how long pictures take on the PCs that draw on the processor: in a tester's log)
            _log($"the view out of sight on the processor: \"{spec.Label}\" at {req.Width}x{req.Height}, {settled:0.0} s settling " +
                 $"({planned} planned frames, {st.Pending} still loading), drawn in {Stopwatch.GetElapsedTime(drawn).TotalMilliseconds:0} ms ({share.Since()})");
        for (int i = 0; i < 4 && !arrived.Wait(250); i++)
        {
            r.Motion.BeginFrame();
            Render(r, Settling(r, req));
        }
        var (rgba, w, h) = got ?? (new byte[req.Width * req.Height * 4], req.Width, req.Height);
        return Seen(r, sim, spec, req, rgba, w, h, got == null ? -1 : st.Pending, since) with { Relit = relight, Waited = waited, LookUnfinished = lookLeft > 0 };
    }

    Shot Seen(FrameRenderer r, Simulator sim, ShotSpec spec, FrameRequest req, byte[] rgba, int w, int h, int pending, long since)
    {
        var seen = Tally(r, sim, req, out int sky, out int ground, out int subject, spec.Subject);
        // how far each one's look had arrived as drawn: the subject's, and each thing's in it
        var loads = new Dictionary<UUID, LookLoad>();
        foreach (var id in seen.Take(14).Select(s => s.Id).Prepend(spec.Subject).Where(id => id != UUID.Zero).Distinct())
            if (r.LoadOf(sim.Handle, id, since) is { } load) loads[id] = load;
        return new Shot(spec, rgba, w, h, pending, seen, SeenCols * SeenRows, sky, ground, subject) { Loads = loads };
    }

    /// <summary>What the shot's grid of picks meets, most cells first (render thread).</summary>
    List<SeenThing> Tally(FrameRenderer r, Simulator sim, FrameRequest req, out int sky, out int ground, out int subject, UUID subjectId = default)
    {
        var fwd = req.Forward;
        var right = Vector3.Cross(fwd, req.Up);
        right = right.LengthSquared() > 1e-8f ? Vector3.Normalize(right) : Vector3.UnitY;
        var up = Vector3.Normalize(Vector3.Cross(right, fwd));
        float tanV = MathF.Tan(req.VerticalFov / 2), tanH = tanV * req.Width / req.Height;
        var tally = new Dictionary<(PickKind, UUID), (int N, float X, float Y, float D)>();
        (sky, ground, subject) = (0, 0, 0);
        for (int j = 0; j < SeenRows; j++)
        for (int i = 0; i < SeenCols; i++)
        {
            float u = (i + 0.5f) / SeenCols, v = (j + 0.5f) / SeenRows;
            var dir = fwd + right * ((u * 2 - 1) * tanH) + up * ((1 - v * 2) * tanV);
            if (r.Pick(sim.Handle, req.Position, dir, req.DrawDistance, 0, ground: true) is not { } hit) { sky++; continue; }
            if (hit.Kind == PickKind.Ground) { ground++; continue; }
            var key = hit.WornBy != UUID.Zero ? (PickKind.Avatar, hit.WornBy) : hit.Kind == PickKind.Avatar ? (PickKind.Avatar, hit.Id) : (PickKind.Object, hit.RootId);
            // the subject itself, and what it wears or what it's part of
            if (subjectId != UUID.Zero && (key.Item2 == subjectId || hit.RootId == subjectId || hit.Id == subjectId)) subject++;
            var t = tally.TryGetValue(key, out var x) ? x : (N: 0, X: 0f, Y: 0f, D: float.MaxValue);
            tally[key] = (t.N + 1, t.X + u, t.Y + v, MathF.Min(t.D, hit.Distance));
        }
        return tally.Select(kv => new SeenThing(kv.Key.Item1, kv.Key.Item2, kv.Value.N, kv.Value.X / kv.Value.N, kv.Value.Y / kv.Value.N, kv.Value.D))
            .OrderByDescending(s => s.Cells).ToList();
    }
}
