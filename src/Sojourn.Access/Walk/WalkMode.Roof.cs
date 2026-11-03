using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Access.Walk;

/// <summary>Under the water with a roof overhead (a glass tunnel under the sea, a room on the seabed): dry inside, so
/// world audio keeps its usual falloff there, where Second Life's viewers make sounds fade five times as fast by the
/// ear's height alone (the blind tester's sounds "went faint" riding such a tunnel, 0.1.0.835). One ray straight up from
/// the ear to the water's surface by the collision model; anything solid it meets counts, the vehicle she rides too
/// (a submarine's hull is a roof). Asked again once the ear has moved a metre or a second has passed, at most five
/// times a second and one at a time; the last answer stands meanwhile. Access only: the VR client stays as the viewer.
/// Claude Opus 5.5, 2026-11-02.</summary>
public sealed partial class WalkMode
{
    sealed record RoofSeen(ulong Region, Vector3 Ear, long At, float? Above);
    volatile RoofSeen? _roofSeen;
    volatile bool _roofAsking;

    /// <summary>AudioWiring.Roof: how far above the ear the roof is, or null (none, or not known yet).</summary>
    float? RoofOver(ulong region, Vector3 ear, float water)
    {
        long now = Environment.TickCount64;
        var seen = _roofSeen;
        bool near = seen != null && seen.Region == region && Vector3.Distance(seen.Ear, ear) < 3;
        bool due = !near || now - seen!.At >= 200 && (Vector3.Distance(seen.Ear, ear) >= 1 || now - seen.At >= 1000);
        if (due && !_roofAsking && _rays.Ready && water > ear.Z)
        {
            _roofAsking = true;
            _ = AskRoof(region, ear, water - ear.Z, now);
        }
        return near ? seen!.Above : null;
    }

    async Task AskRoof(ulong region, Vector3 ear, float up, long asked)
    {
        try
        {
            if (await _rays.CollideManyAsync([(ear, Vector3.UnitZ, up)]) is { } got && got.Region == region)
                _roofSeen = new RoofSeen(region, ear, asked, got.Hits[0]?.Distance);
        }
        catch (Exception) { /* a region left meanwhile: asked again on the next move */ }
        finally { _roofAsking = false; }
    }
}
