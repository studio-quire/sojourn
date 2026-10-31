using System.Collections.Concurrent;
using Vector3 = System.Numerics.Vector3;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;

namespace Sojourn.Session.Assistant;

/// <summary>What the person points at: the first thing on the pointer's ray, by id.</summary>
public sealed record PointerTarget(PickKind Kind, ulong Region, string RegionName, UUID Id, UUID RootId, UUID WornBy, Vector3 Point, float Distance, string Name,
    bool Hud = false)
{
    /// <param name="looking">not pointed at: what's in the middle of the person's view (Pointer.Looking)</param>
    public JsonObject ToJson(bool looking = false)
    {
        var o = new JsonObject
        {
            ["kind"] = Kind.ToString().ToLowerInvariant(),
            ["name"] = Name,
            ["distance"] = MathF.Round(Distance, 1),
            ["region"] = RegionName,
            ["position"] = new JsonArray(MathF.Round(Point.X, 2), MathF.Round(Point.Y, 2), MathF.Round(Point.Z, 2)),
        };
        if (Hud)
        {
            // on the person's own HUD: its point is on the HUD screen, not in the region
            o.Remove("position");
            o.Remove("distance");
            o["hud"] = true;
        }
        if (Kind != PickKind.Ground) o["id"] = Id.ToString();
        if (Kind == PickKind.Object && RootId != Id) o["root_id"] = RootId.ToString();
        if (WornBy != UUID.Zero) o["worn_by"] = WornBy.ToString();
        if (looking) o["looking"] = true;
        return o;
    }

    public string Label => Kind == PickKind.Ground ? $"the ground at {Point.X:F0}, {Point.Y:F0}" : Hud ? Name : $"{Name} · {Distance:F0} m";
}

/// <summary>
/// Casts the pointer's ray every frame (the mouse on the desktop, the right controller in the headset while its
/// pointer is on) through the renderer's pick and keeps what it hits, with a name for the panel. What it holds is
/// only ever this moment's: a target not refreshed for a moment reads as nothing, so one from before a teleport, or
/// from before the ray moved onto the panel, can't stand in for "that". In the headset with the pointer off, what's in
/// the middle of the view (<see cref="Looking"/>) is kept instead, as a hint. Names of objects not yet known are asked
/// for once (TesselObjects get) and cached. Main thread, except <see cref="Current"/> and <see cref="Looking"/>.
/// </summary>
public sealed class Pointer
{
    readonly AgentSession _s;
    readonly ConcurrentDictionary<UUID, string> _names = new();
    readonly ConcurrentDictionary<UUID, bool> _asked = new();
    volatile PointerTarget? _current, _looking;
    long _currentAt, _lookingAt;
    const long FreshMs = 750;

    public Pointer(AgentSession session) => _s = session;

    /// <summary>What the pointer is on now (null: nothing, or it hasn't been cast for a moment).</summary>
    public PointerTarget? Current =>
        _current is { } c && Environment.TickCount64 - Interlocked.Read(ref _currentAt) < FreshMs ? c : null;

    /// <summary>The headset with its pointer off: the object or person in the middle of the view (null: nothing, the
    /// pointer's on, or not looked for lately).</summary>
    public PointerTarget? Looking =>
        _looking is { } l && Environment.TickCount64 - Interlocked.Read(ref _lookingAt) < 2 * FreshMs ? l : null;

    PointerTarget? Set(PointerTarget? t)
    {
        _current = t;
        Interlocked.Exchange(ref _currentAt, Environment.TickCount64);
        return t;
    }

    long _lookedAt;
    /// <summary>What's in the middle of the view (the head's forward ray), looked for a few times a second.</summary>
    public void Look(FrameRenderer renderer, ulong region, Vector3 origin, Vector3 dir)
    {
        long now = Environment.TickCount64;
        if (now - _lookedAt < 200) return;
        _lookedAt = now;
        if (_s.Client is not { } c || region == 0) { _looking = null; return; }
        var hit = renderer.Pick(region, origin, dir, 64, c.Self.LocalID, ground: false);
        string regionName = c.Network.Simulators.FirstOrDefault(s => s.Handle == region)?.Name ?? "";
        _looking = hit is { } h ? new PointerTarget(h.Kind, region, regionName, h.Id, h.RootId, h.WornBy, h.Point, h.Distance, NameOf(h)) : null;
        Interlocked.Exchange(ref _lookingAt, now);
    }

    /// <summary>A thing the person opened a card about (Things/ThingCard), or asked the assistant about from it: "that"
    /// while the pointer is on nothing else. Set and cleared by whoever pinned it.</summary>
    public PointerTarget? Pinned { get => _pinned; set => _pinned = value; }
    volatile PointerTarget? _pinned;

    /// <summary>What "that" means now: what's pointed at, else what's pinned.</summary>
    public PointerTarget? Meant => Current ?? _pinned;

    /// <summary>For the assistant: what's pointed at or pinned, else (looking: true) what's in the middle of the view.</summary>
    public JsonObject? TargetJson() => Meant?.ToJson() ?? Looking?.ToJson(looking: true);

    /// <summary>This frame's hit as the renderer gave it (with where on the surface), and the ray it came from.</summary>
    public PickHit? CurrentHit { get; private set; }
    public (ulong Region, Vector3 Origin, Vector3 Dir) Ray { get; private set; }

    // where the pointer last landed on each object (by linkset root): a touch the assistant makes later lands there
    readonly Dictionary<UUID, (PickHit Hit, long At)> _recent = new();

    /// <summary>Where the pointer last landed on this object (any prim of its linkset), if within maxSeconds. Main thread.</summary>
    public PickHit? Recent(UUID root, double maxSeconds) =>
        _recent.TryGetValue(root, out var r) && Environment.TickCount64 - r.At < maxSeconds * 1000 ? r.Hit : null;

    /// <summary>The person's HUD prim under the pointer this frame (Hud/HudView), which comes before anything in the
    /// world, as in the viewer.</summary>
    public Func<PickHit?>? HudUnder { get; set; }

    /// <summary>The pointer is on one of the person's HUDs (the headset's board): that prim is what "that" means.</summary>
    public PointerTarget? OnHud(PickHit h)
    {
        CurrentHit = null;
        if (_s.Client is not { } c) return Set(null);
        return Set(new PointerTarget(h.Kind, h.Region, c.Network.CurrentSim?.Name ?? "", h.Id, h.RootId, h.WornBy, h.Point, 0, HudName(h), Hud: true));
    }

    string HudName(PickHit h)
    {
        var region = _s.Region;
        string? part = region?.PrimById(h.Id)?.Properties?.Name, hud = region?.PrimById(h.RootId)?.Properties?.Name;
        if (hud == null && !_names.TryGetValue(h.RootId, out hud)) Ask(h.RootId);
        hud ??= "a HUD";
        return part is { Length: > 0 } && part != hud && part != "Object" ? $"{part} on {hud}" : hud;
    }

    /// <summary>The ray in the camera region's frame; null clears it (nothing to point with).</summary>
    public PointerTarget? Update(FrameRenderer renderer, ulong region, Vector3 origin, Vector3 dir, float reach = 96)
    {
        var c = _s.Client;
        CurrentHit = null;
        _looking = null;
        if (c is null || region == 0) return Set(null);
        if (HudUnder?.Invoke() is { } onHud) { Ray = (region, origin, dir); return OnHud(onHud); }
        uint self = c.Self.LocalID;
        Ray = (region, origin, dir);
        var hit = renderer.Pick(region, origin, dir, reach, self);
        CurrentHit = hit;
        if (hit is not { } h) return Set(null);
        if (h.Kind == PickKind.Object)
        {
            if (_recent.Count > 64) _recent.Clear();
            _recent[h.RootId] = (h, Environment.TickCount64);
        }
        string regionName = c.Network.Simulators.FirstOrDefault(s => s.Handle == region)?.Name ?? "";
        return Set(new PointerTarget(h.Kind, region, regionName, h.Id, h.RootId, h.WornBy, h.Point, h.Distance, NameOf(h)));
    }

    /// <summary>Nothing pointed at this frame (no ray, or it's on the panel, a board or the feed).</summary>
    public void Clear() { Set(null); CurrentHit = null; }

    string NameOf(PickHit h)
    {
        switch (h.Kind)
        {
            case PickKind.Ground: return "ground";
            case PickKind.Avatar: return AvatarName(h.Id);
        }
        var region = _s.Region;
        string? name = null;
        if (region?.PrimById(h.RootId) is { Properties.Name: { Length: > 0 } rn }) name = rn;
        else if (_names.TryGetValue(h.RootId, out var cached)) name = cached;
        else Ask(h.RootId);
        name ??= "an object";
        if (h.WornBy != UUID.Zero) name += $" (worn by {AvatarName(h.WornBy)})";
        return name;
    }

    string AvatarName(UUID id)
    {
        var region = _s.Region;
        if (region?.Avatars.Values.FirstOrDefault(a => a.ID == id) is { } av && av.Name is { Length: > 0 } n) return n;
        return _s.Names.Legacy(id) ?? "someone";
    }

    void Ask(UUID id)
    {
        if (!_asked.TryAdd(id, true)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var got = await _s.Api.CallAsync("TesselObjects",
                    new JsonObject { ["op"] = "get", ["ids"] = new JsonArray(id.ToString()) }, true, TimeSpan.FromSeconds(5), default);
                if (got is JsonObject o && FirstName(o) is { } n) _names[id] = n;
            }
            catch { _asked.TryRemove(id, out _); }
        });
    }

    static string? FirstName(JsonObject o) => o["objects"] switch
    {
        JsonArray a => a.OfType<JsonObject>().Select(x => x.Str("name")).FirstOrDefault(n => n is { Length: > 0 }),
        JsonObject m => m.Select(kv => (kv.Value as JsonObject)?.Str("name")).FirstOrDefault(n => n is { Length: > 0 }),
        _ => null,
    };
}
