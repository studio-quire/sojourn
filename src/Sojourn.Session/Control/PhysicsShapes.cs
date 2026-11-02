using System.Collections.Concurrent;
using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Sojourn.Core.Agents;
using Sojourn.Render.World;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Session.Control;

/// <summary>
/// The physics shape types of the prims around the person (prim, none, convex hull), for the headset walk's local
/// collision model (VrPilot.Collide.cs, RegionScene.CollideRay). The region keeps them, not the object updates, so they
/// are asked for the way the viewer asks (LLViewerObjectList::fetchPhysicsFlags): a POST of up to 256 object ids to the
/// region's GetObjectPhysicsData capability, answered per id with its PhysicsShapeType (and density, friction...).
/// The viewer asks only when something wants one (the build floater); here everything around the avatar is asked for
/// ahead of the walk, nearest first, a second at a time and one request at a time, since a type learned on meeting a
/// wall (or a floor) would arrive a round trip after walking into it (the user's session of 2026-10-27: mesh plants
/// held the view up by their hulls until their "none" came): within <see cref="Reach"/> across and all the way down
/// (where a fall lands), and above all whatever a collision ray has just met, which goes first. A refused or failed
/// request is asked again after a growing pause. Answers are kept for the session and asked again after ten minutes
/// (an edit changes them; the region tells only the viewer that selected the object).
/// </summary>
public sealed class PhysicsShapes
{
    readonly AgentSession _session;
    readonly ConcurrentDictionary<UUID, (PhysicsShapeType Type, long At)> _known = new();
    readonly ConcurrentDictionary<UUID, long> _asked = new();
    readonly ConcurrentQueue<UUID> _urgent = new();   // met by a ray: first
    readonly ConcurrentQueue<UUID> _wanted = new();   // around the avatar, nearest first
    Vector3 _around;
    ulong _region;
    bool _running, _unsupported;
    public Action<string>? Say;

    /// <summary>How far around the avatar prims' types are asked for ahead of the walk (m), across; below, all the way down.</summary>
    public const float Reach = 24f;
    const int Batch = 256;   // the viewer's MAX_CONCURRENT_PHYSICS_REQUESTS
    const long Stale = 600_000;

    public PhysicsShapes(AgentSession session) => _session = session;

    /// <summary>The region doesn't offer the capability (or refuses it): nothing will be known.</summary>
    public bool Unsupported => _unsupported;
    public int Known => _known.Count;

    /// <summary>A prim's physics shape type if the region has said; asking for it first otherwise.</summary>
    public PhysicsShapeType? Of(Primitive p)
    {
        if (_known.TryGetValue(p.ID, out var k))
        {
            if (Environment.TickCount64 - k.At > Stale) Want(p.ID, urgent: false);
            return k.Type;
        }
        Want(p.ID, urgent: true);
        return null;
    }

    /// <summary>How a prim counts for the local collision models (RegionScene.CollideRay, CollisionWorld): flexible
    /// and phantom ones (a linkset's root phantom makes it all so) passable, else its physics shape type as the region
    /// gave it, or not known yet (asked for now).</summary>
    public CollideAs Collide(Primitive p)
    {
        if (((byte)p.PrimData.PathCurve & 0x80) != 0) return CollideAs.Passable;   // flexible prims are phantom
        var flags = _session.State.PrimFlagsOf(p);
        if (p.ParentID != 0 && _session.Region is { } w && w.Prims.TryGetValue(p.ParentID, out var root)) flags |= _session.State.PrimFlagsOf(root);
        if ((flags & PrimFlags.Phantom) != 0) return CollideAs.Passable;
        return Of(p) is { } t ? (CollideAs)(byte)t : CollideAs.Unknown;
    }

    /// <summary>Answers and asks long past let go: they were kept for the session, every prim of every region passed
    /// (75,000 on a three-hour train ride). An answer is asked again after <see cref="Stale"/> anyway.</summary>
    void Prune(long now)
    {
        foreach (var (id, k) in _known) if (now - k.At > 2 * Stale) _known.TryRemove(id, out _);
        foreach (var (id, at) in _asked) if (now - at > 2 * Stale) _asked.TryRemove(id, out _);
    }

    void Want(UUID id, bool urgent)
    {
        if (_unsupported) return;
        long now = Environment.TickCount64;
        if (_asked.TryGetValue(id, out long at) && now - at < 15_000) return;
        _asked[id] = now;
        (urgent ? _urgent : _wanted).Enqueue(id);
    }

    /// <summary>Where the avatar is (render thread, each frame): the background loop asks for what's around it.</summary>
    public void Around(ulong region, Vector3 at)
    {
        _around = at;
        if (region != _region) { _region = region; _unsupported = false; }
        if (_running) return;
        _running = true;
        _ = Task.Run(Loop);
    }

    async Task Loop()
    {
        long nextScan = 0, nextReport = 0, pauseUntil = 0, nextPrune = 0;
        int pause = 0, failures = 0, answered = 0;
        string? lastFailure = null;
        var near = new List<(float D, UUID Id)>();
        while (true)
        {
            try
            {
                await Task.Delay(100).ConfigureAwait(false);
                var c = _session.Client;
                if (c is null || !c.Network.Connected || _session.Region is not { } w) continue;
                long now = Environment.TickCount64;
                if (now >= nextScan)
                {
                    nextScan = now + 1000;
                    var at = _around;
                    near.Clear();
                    foreach (var (_, p) in w.Prims)   // (in place: Values would copy the region's prims each second)
                    {
                        if (p.PrimData.PCode != PCode.Prim) continue;
                        var pos = p.Position;
                        if (p.ParentID != 0)
                        {
                            if (!w.Prims.TryGetValue(p.ParentID, out var r)) continue;   // (worn: its parent's an avatar)
                            pos = r.Position + pos * r.Rotation;
                        }
                        float size = p.Scale.Length();
                        float dx = pos.X - at.X, dy = pos.Y - at.Y, across = MathF.Sqrt(dx * dx + dy * dy);
                        if (across > Reach + size || pos.Z > at.Z + Reach + size) continue;
                        if (_known.TryGetValue(p.ID, out var k) && now - k.At <= Stale) continue;
                        near.Add((across + MathF.Max(0, at.Z - pos.Z - size) * 0.1f, p.ID));
                    }
                    near.Sort((a, b) => a.D.CompareTo(b.D));
                    foreach (var (_, id) in near) Want(id, urgent: false);
                    near.Clear();
                    if (now >= nextPrune) { nextPrune = now + 60_000; Prune(now); }
                }
                if (now >= nextReport)
                {
                    nextReport = now + 30_000;
                    if (failures > 0 || !_wanted.IsEmpty || !_urgent.IsEmpty)
                        Say?.Invoke($"physics shapes: {_known.Count} known, {answered} answered in the last 30 s, {_urgent.Count + _wanted.Count} waiting"
                                    + (failures > 0 ? $", {failures} requests failed ({lastFailure})" : ""));
                    failures = answered = 0;
                }
                if ((_wanted.IsEmpty && _urgent.IsEmpty) || _unsupported || now < pauseUntil) continue;
                var sim = c.Network.CurrentSim;
                if (sim?.Caps?.CapabilityURI("GetObjectPhysicsData") is not { } cap) continue;
                var ids = new List<UUID>();
                while (ids.Count < Batch && _urgent.TryDequeue(out var id)) if (!ids.Contains(id)) ids.Add(id);
                while (ids.Count < Batch && _wanted.TryDequeue(out var id)) if (!ids.Contains(id)) ids.Add(id);
                if (ids.Count == 0) continue;
                var arr = new OSDArray();
                foreach (var id in ids) arr.Add(OSD.FromUUID(id));
                string? failed = null;
                OSDMap? m = null;
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var (resp, data) = await c.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, new OSDMap { ["object_ids"] = arr }, cts.Token).ConfigureAwait(false);
                    if (resp is { StatusCode: System.Net.HttpStatusCode.NotFound }) { _unsupported = true; continue; }
                    if (resp is { IsSuccessStatusCode: false }) failed = $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
                    else if (data is null) failed = "no answer";
                    else
                    {
                        try { m = OSDParser.Deserialize(data) as OSDMap; }
                        catch (Exception) { }
                        // (a region over its rate for a capability answers with a line of text)
                        if (m == null) failed = "not an answer: " + System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 40)).Trim();
                    }
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
                {
                    failed = e.GetType().Name;
                }
                if (failed != null || m == null)
                {
                    // the same ids again first, after a pause that grows while it keeps failing (0.5 s to 8 s)
                    failures++;
                    lastFailure = failed;
                    pause = Math.Min(8000, pause == 0 ? 500 : pause * 2);
                    pauseUntil = Environment.TickCount64 + pause;
                    foreach (var id in ids) _urgent.Enqueue(id);
                    continue;
                }
                pause = 0;
                long got = Environment.TickCount64;
                foreach (var id in ids)
                    if (m.TryGetValue(id.ToString(), out var entry) && entry is OSDMap e && e.TryGetValue("PhysicsShapeType", out var t))
                    {
                        _known[id] = ((PhysicsShapeType)(byte)t.AsInteger(), got);
                        answered++;
                    }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Say?.Invoke($"physics shapes: {e.GetType().Name}: {e.Message}");
                await Task.Delay(2000).ConfigureAwait(false);
            }
        }
    }
}
