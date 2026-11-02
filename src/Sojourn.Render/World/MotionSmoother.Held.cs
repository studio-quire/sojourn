using System.Collections.Concurrent;
using LibreMetaverse;

namespace Sojourn.Render.World;

/// <summary>
/// Objects held in the person's hand (the client's building by hand): drawn where the hand puts them, whatever the
/// region last said, until let go. The viewer does the same for an object being dragged in its edit tools (its
/// overrideObjectUpdates puts the selection's own transform back over each update), since the region's echoes of the
/// moves sent while dragging come a round trip late and would pull it back. A root's pose is the region's frame; its
/// linkset follows it. Any thread.
/// </summary>
public sealed partial class MotionSmoother
{
    readonly ConcurrentDictionary<UUID, (ulong Region, uint Local, Vector3 Pos, Quaternion Rot)> _held = new();

    /// <summary>Draw this root at this pose (the region's frame) until <see cref="LetGo"/>.</summary>
    public void Hold(UUID id, ulong region, uint localId, Vector3 pos, Quaternion rot) =>
        _held[id] = (region, localId, pos, Quaternion.Normalize(rot));

    /// <summary>Back to the region's word (the next frame places it where the region last had it).</summary>
    public void LetGo(UUID id) => _held.TryRemove(id, out _);

    /// <summary>The held roots' local ids in a region (its scene places them every frame).</summary>
    public void HeldIn(ulong region, List<uint> into)
    {
        if (_held.IsEmpty) return;
        foreach (var h in _held.Values) if (h.Region == region) into.Add(h.Local);
    }

    bool IsHeld(Primitive p) => !_held.IsEmpty && p.ParentID == 0 && _held.ContainsKey(p.ID);

    (Vector3 Pos, Quaternion Rot)? HeldPose(Primitive p) =>
        !_held.IsEmpty && p.ParentID == 0 && _held.TryGetValue(p.ID, out var h) ? (h.Pos, h.Rot) : null;
}
