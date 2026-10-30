using System.Text.Json.Nodes;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Session.Offscreen;

/// <summary>
/// SojournView: the renderer out of sight (OffscreenRenderer) for an app that shows no view: how it's doing, and the
/// local collision model's rays (what's solid ahead, for walking and warnings without sight; docs/access.md > M3).
/// Pictures are the snapshot ops' (LLViewerWindow saveSnapshot, the assistant's look); touches land by its picks
/// (LLAgent requestTouch, with face).
/// </summary>
public sealed class ViewApi : LeapApi
{
    readonly OffscreenRenderer _view;

    public override string Name => "SojournView";
    public override string Desc => "the view this app renders out of sight (for pictures, touches on faces and the collision model): its state, and what's solid along a ray";

    public ViewApi(AgentSession s, OffscreenRenderer view) : base(s)
    {
        _view = view;
        Op("status", "-> {state: starting|running|restarting|stopped, device, frames, idle, looks, glances, pictures, gpu_ms (a frame, "
            + "lately), cpu_ms, video_mb, textures_mb, losses (in ten minutes), idle_hz, idle_size}", _ => Status());
        Op("collide", "{heading?: degrees from the way I face (0 ahead, 90 left), dir?: [x,y,z] in the region instead, pitch?: degrees, "
            + "from?: [x,y,z] (default my chest), max?: metres (20)} -> {hit: false} or {hit, distance, normal, prim?, avatar}: "
            + "the first solid surface along the ray in the local collision model (prims by their physics shapes, phantom ones "
            + "passable, things not drawn yet not counted)", CollideAsync);
    }

    JsonNode Status()
    {
        var st = _view.Status();
        return new JsonObject
        {
            ["state"] = st.State, ["device"] = st.Device, ["frames"] = st.Frames, ["idle"] = st.Idle, ["looks"] = st.Looks,
            ["glances"] = st.Glances, ["pictures"] = st.Pictures,
            ["gpu_ms"] = double.IsFinite(st.GpuMs) ? Math.Round(st.GpuMs, 2) : null, ["gpu_frames"] = st.GpuFrames,
            ["cpu_ms"] = double.IsFinite(st.CpuMs) ? Math.Round(st.CpuMs, 2) : null,
            ["video_mb"] = st.VramUsed >> 20, ["textures_mb"] = st.TextureBytes >> 20, ["losses"] = st.Losses,
            ["idle_hz"] = _view.IdleHz, ["idle_size"] = _view.IdleSize,
        };
    }

    async Task<JsonNode?> CollideAsync(JsonObject a, CancellationToken ct)
    {
        var c = S.Client ?? throw new NotConnected("not logged in");
        var me = c.Self.SimPosition;
        var from = a.Vec3("from") is { } f ? new Vector3(f.X, f.Y, f.Z) : new Vector3(me.X, me.Y, me.Z + 0.3f);
        Vector3 dir;
        if (a.Vec3("dir") is { } d) dir = new Vector3(d.X, d.Y, d.Z);
        else
        {
            var body = c.Self.Movement.BodyRotation;
            var fwd = LibreMetaverse.Vector3.UnitX * body;
            double yaw = Math.Atan2(fwd.Y, fwd.X) + a.Num("heading", 0) * Math.PI / 180, pitch = a.Num("pitch", 0) * Math.PI / 180;
            dir = new Vector3((float)(Math.Cos(yaw) * Math.Cos(pitch)), (float)(Math.Sin(yaw) * Math.Cos(pitch)), (float)Math.Sin(pitch));
        }
        if (dir.LengthSquared() < 1e-8f) throw new LeapError("the ray needs a direction");
        if (!_view.Running) throw new LeapError($"the view isn't running ({_view.State})");
        var hit = await _view.CollideAsync(from, System.Numerics.Vector3.Normalize(dir), (float)Math.Clamp(a.Num("max", 20), 0.1, 256), ct);
        if (hit is not { } asked) throw new LeapError($"the view couldn't answer ({_view.State})");
        if (asked.Hit is not { } h) return new JsonObject { ["hit"] = false };
        var o = new JsonObject
        {
            ["hit"] = true, ["distance"] = Math.Round(h.Distance, 2),
            ["normal"] = new JsonArray(Math.Round(h.Normal.X, 2), Math.Round(h.Normal.Y, 2), Math.Round(h.Normal.Z, 2)), ["avatar"] = h.Avatar,
        };
        if (!h.Avatar && S.Region is { } w && w.Prims.TryGetValue(h.LocalId, out var p)) o["prim"] = p.ID.ToString();
        return o;
    }
}
