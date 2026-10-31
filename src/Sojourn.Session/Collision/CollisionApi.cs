using System.Text.Json.Nodes;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Render.World;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Session.Collision;

/// <summary>
/// SojournCollision: the local collision model without a GPU (<see cref="LocalCollision"/>): how much of the world it
/// holds, and what's solid along a ray. SojournView's collide asks the renderer's model the same way, for comparing.
/// </summary>
public sealed class CollisionApi : LeapApi
{
    readonly LocalCollision _model;

    public override string Name => "SojournCollision";
    public override string Desc => "the local collision model (no GPU needed): what it holds, and what's solid along a ray";

    public const string RayArgs = "{heading?: degrees from the way I face (0 ahead, 90 left), dir?: [x,y,z] in the region instead, pitch?: degrees, "
        + "from?: [x,y,z] (default my chest), max?: metres (20)} -> {hit: false} or {hit, distance, normal, prim?, avatar}";

    public CollisionApi(AgentSession s, LocalCollision model) : base(s)
    {
        _model = model;
        Op("status", "-> {regions, prims (indexed), placed, shapes, ready, waiting (for a download), fetching, failed, shapes_mb, "
            + "physics_known (prims whose physics shape type the region has said), reach (m around me kept ready), last_rays, last_ms "
            + "(the last batch of rays and how long it took), slowest_ms (in the last minute or so)}", _ => Status());
        Op("collide", RayArgs + ": the first solid surface along the ray (prims by their physics shapes, phantom ones passable, "
            + "sculpts and meshes once downloaded)", CollideAsync);
    }

    JsonNode Status()
    {
        var st = _model.Status();
        return new JsonObject
        {
            ["regions"] = st.Regions, ["prims"] = st.Prims, ["placed"] = st.Placed, ["shapes"] = st.Shapes, ["ready"] = st.Ready,
            ["waiting"] = st.Waiting, ["fetching"] = st.Fetching, ["failed"] = st.Failed, ["shapes_mb"] = Math.Round(st.Bytes / 1048576.0, 1),
            ["physics_known"] = _model.Physics.Known, ["reach"] = Math.Round(_model.Reach),
            ["last_rays"] = _model.LastBatch.Rays, ["last_ms"] = Math.Round(_model.LastBatch.Ms, 2), ["slowest_ms"] = Math.Round(_model.LastBatch.SlowestMs, 2),
        };
    }

    async Task<JsonNode?> CollideAsync(JsonObject a, CancellationToken ct)
    {
        var (from, dir, max) = Ray(S, a);
        var (hit, asked) = await _model.CollideAsync(from, dir, max, ct);
        if (!asked) throw new NotConnected("not in a region");
        return Answer(S, hit);
    }

    /// <summary>A ray as the collide ops take it: from the chest (or given), by heading from the way I face (or a
    /// direction given), up to max metres.</summary>
    public static (Vector3 From, Vector3 Dir, float Max) Ray(AgentSession s, JsonObject a)
    {
        var c = s.Client ?? throw new NotConnected("not logged in");
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
        return (from, Vector3.Normalize(dir), (float)Math.Clamp(a.Num("max", 20), 0.1, 256));
    }

    public static JsonNode Answer(AgentSession s, CollideHit? hit)
    {
        if (hit is not { } h) return new JsonObject { ["hit"] = false };
        var o = new JsonObject
        {
            ["hit"] = true, ["distance"] = Math.Round(h.Distance, 2),
            ["normal"] = new JsonArray(Math.Round(h.Normal.X, 2), Math.Round(h.Normal.Y, 2), Math.Round(h.Normal.Z, 2)), ["avatar"] = h.Avatar,
        };
        if (!h.Avatar && s.Region is { } w && w.Prims.TryGetValue(h.LocalId, out var p)) o["prim"] = p.ID.ToString();
        return o;
    }
}
