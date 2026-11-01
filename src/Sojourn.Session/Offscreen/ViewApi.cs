using System.Text.Json.Nodes;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Session.Collision;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Session.Offscreen;

/// <summary>
/// SojournView: the renderer out of sight (OffscreenRenderer) for an app that shows no view: how it's doing, and its
/// collision model's rays (for comparing with SojournCollision's, the model without a GPU walk mode uses).
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
        Op("status", "-> {state: starting|running|restarting|stopped, device, software (drawn on the processor: no GPU here could), frames, idle, looks, glances, pictures, gpu_ms (a frame, "
            + "lately), cpu_ms, video_mb, textures_mb, losses (in ten minutes), idle_hz, idle_size, unseen (nothing can look at its "
            + "pictures: only geometry drawn, no textures, avatars, shadows or reflections), draw_distance}", _ => Status());
        Op("collide", CollisionApi.RayArgs + ": the first solid surface along the ray in the renderer's collision model (prims by "
            + "their physics shapes, phantom ones passable, things not drawn yet not counted); SojournCollision's collide is the one "
            + "without a GPU that Sojourn Access walks by", CollideAsync);
    }

    JsonNode Status()
    {
        var st = _view.Status();
        return new JsonObject
        {
            ["state"] = st.State, ["device"] = st.Device, ["software"] = _view.Software, ["frames"] = st.Frames, ["idle"] = st.Idle, ["looks"] = st.Looks,
            ["glances"] = st.Glances, ["pictures"] = st.Pictures,
            ["gpu_ms"] = double.IsFinite(st.GpuMs) ? Math.Round(st.GpuMs, 2) : null, ["gpu_frames"] = st.GpuFrames,
            ["cpu_ms"] = double.IsFinite(st.CpuMs) ? Math.Round(st.CpuMs, 2) : null,
            ["video_mb"] = st.VramUsed >> 20, ["textures_mb"] = st.TextureBytes >> 20, ["losses"] = st.Losses,
            ["idle_hz"] = _view.Software ? 0 : _view.IdleHz, ["idle_size"] = _view.IdleSize, ["unseen"] = _view.Unseen, ["draw_distance"] = Math.Round(_view.Reach),
        };
    }

    async Task<JsonNode?> CollideAsync(JsonObject a, CancellationToken ct)
    {
        var (from, dir, max) = CollisionApi.Ray(S, a);
        if (!_view.Running) throw new LeapError($"the view isn't running ({_view.State})");
        var hit = await _view.CollideAsync(from, dir, max, ct);
        if (hit is not { } asked) throw new LeapError($"the view couldn't answer ({_view.State})");
        return CollisionApi.Answer(S, asked.Hit);
    }
}
