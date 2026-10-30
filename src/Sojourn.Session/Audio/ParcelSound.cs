using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Session.Audio;

/// <summary>
/// The parcel you're on, as sound sees it: its music stream's URL, and the viewer's "sounds stay inside" rule
/// (LLViewerParcelMgr::canHearSound): a sound on your own parcel is heard; from another parcel it isn't if either
/// yours or that one keeps its sounds inside (the region's parcel overlay marks those, PARCEL_SOUND_LOCAL).
/// Refreshed twice a second; the parcel's properties are asked for when they aren't known (the region also sends
/// them as you walk onto a parcel).
/// </summary>
public sealed class ParcelSound
{
    readonly AgentSession _s;
    DateTime _askedAt;
    volatile Snapshot? _now;

    sealed record Snapshot(ulong Region, Parcel? Parcel, byte[]? Overlay);

    public ParcelSound(AgentSession s) => _s = s;

    /// <summary>The current parcel's music URL ("" none, null while it isn't known yet).</summary>
    public string? MusicUrl => _now is { Parcel: { } p } ? (p.MusicURL ?? "").Trim() : null;
    public string? ParcelName => _now?.Parcel?.Name;

    public void Refresh()
    {
        var c = _s.Client;
        if (c?.Network.CurrentSim is not { } sim || sim.Handle == 0) { _now = null; return; }
        var pos = c.Self.SimPosition;
        Parcel? parcel = null;
        int local = 0;
        try { local = c.Parcels.GetParcelLocalID(sim, pos); } catch { }
        if (local != 0) sim.Parcels.TryGetValue(local, out parcel);
        if (parcel != null && !Contains(parcel, pos)) parcel = null;   // the map's stale: we walked off it
        if (parcel == null && DateTime.UtcNow - _askedAt > TimeSpan.FromSeconds(3))
        {
            _askedAt = DateTime.UtcNow;
            try { c.Parcels.RequestParcelProperties(sim, pos.Y + 1, pos.X + 1, pos.Y - 1, pos.X - 1, 0, false); } catch { }
        }
        _now = new Snapshot(sim.Handle, parcel, sim.ParcelOverlay);
    }

    /// <summary>Whether a sound at this place (global metres) is heard from where you are.</summary>
    public bool CanHear(Vector3d pos)
    {
        var n = _now;
        if (n?.Parcel is not { } mine) return true;   // not known yet: heard, as everywhere without the flag
        Utils.LongToUInts(n.Region, out var rx, out var ry);
        double lx = pos.X - rx, ly = pos.Y - ry;
        bool sameRegion = lx >= 0 && ly >= 0 && lx < 256 && ly < 256;   // the agent's region (var regions: the first 256 m)
        if (sameRegion && Contains(mine, new Vector3((float)lx, (float)ly, 0))) return true;
        if ((mine.Flags & ParcelFlags.SoundLocal) != 0) return false;
        if (sameRegion && n.Overlay is { Length: >= 4096 } ov)
        {
            int x = Math.Clamp((int)(lx / 4), 0, 63), y = Math.Clamp((int)(ly / 4), 0, 63);
            if ((ov[y * 64 + x] & 0x20) != 0) return false;   // PARCEL_SOUND_LOCAL
        }
        return true;
    }

    static bool Contains(Parcel p, Vector3 pos)
    {
        var bm = p.Bitmap;
        if (bm is not { Length: >= 512 }) return pos.X >= p.AABBMin.X && pos.X <= p.AABBMax.X && pos.Y >= p.AABBMin.Y && pos.Y <= p.AABBMax.Y;
        int x = Math.Clamp((int)(pos.X / 4), 0, 63), y = Math.Clamp((int)(pos.Y / 4), 0, 63);
        int bit = y * 64 + x;
        return (bm[bit / 8] & (1 << (bit % 8))) != 0;
    }
}
