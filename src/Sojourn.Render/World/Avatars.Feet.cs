// The ground under a standing avatar's feet, as the viewer finds it for LLKeyframeStandMotion (newview/llvoavatar.cpp
// getGround, newview/llworld.cpp resolveStepHeightGlobal): the land, or the avatar's foot plane from the simulator
// when that's met first. LGPL 2.1, Linden Research, Inc.
using LibreMetaverse;
using Sojourn.Render.Bodies;

namespace Sojourn.Render.World;

public sealed partial class Avatars
{
    /// <summary>What a body's foot IK asks the ground of this frame (the delegate is made once per body).</summary>
    sealed class FootSource
    {
        public RegionScene Scene = null!;
        public Vector3 Offset;
        public Avatar Av = null!;
        public FootGround? Fn;
    }

    readonly System.Runtime.CompilerServices.ConditionalWeakTable<Body, FootSource> _feet = new();

    /// <summary>Before the pose: where this frame's root is (as PlaceRoot will put it) and the ground under it, for the
    /// default stands' foot IK. None while seated, nor for the own body in the headset (the rig puts its feet down).</summary>
    void SetFootGround(RegionScene scene, Vector3 offset, Avatar av, Body body, BodyShape shape, Vector3 pos, Quaternion rot, bool seated, bool selfInHeadset)
    {
        var anim = body.Anim;
        if (seated || selfInHeadset) { anim.Ground = null; return; }
        var root = pos;
        PlaceRoot(body, shape, av, ref root, rot, seated, out _);
        anim.Root = root;
        var fs = _feet.GetOrCreateValue(body);
        (fs.Scene, fs.Offset, fs.Av) = (scene, offset, av);
        anim.Ground = fs.Fn ??= p => Ground(fs.Scene, fs.Offset, fs.Av, p);
    }

    /// <summary>resolveStepHeightGlobal from a metre above the point to a metre below: the land's height there, unless
    /// the avatar's foot plane (the simulator's collision plane for it, where it stands on a prim) is nearer, taken
    /// 5 cm lower ("to compensate for error in foot plane reported by Havok"). In the view's frame.</summary>
    static (Vector3 Pos, Vector3 Normal) Ground(RegionScene scene, Vector3 offset, Avatar av, Vector3 point)
    {
        const float segment = 2f;
        var a = point - offset + new Vector3(0, 0, 1);
        float land = scene.TerrainHeight(a.X, a.Y);
        float landDist = (a.Z - land) / segment;
        var hit = new Vector3(a.X, a.Y, land);
        var normal = LandNormal(scene, a.X, a.Y);
        // (no plane: SL sends zeros, OpenSim a zero normal with a height, which isn't a plane either)
        var fp = av.CollisionPlane;
        if (fp.X * fp.X + fp.Y * fp.Y + fp.Z * fp.Z > 0.25f)
        {
            var n = new Vector3(fp.X, fp.Y, fp.Z);
            float d = Math.Clamp((Vector3.Dot(a, n) - fp.W + 0.05f) / segment, 0, 1);
            if (d < landDist)
            {
                hit = a with { Z = a.Z - d * segment };
                normal = n;
            }
        }
        return (hit + offset, normal);
    }

    /// <summary>The land's normal from its slope half a metre either side (the viewer reads its patch's normals).</summary>
    static Vector3 LandNormal(RegionScene scene, float x, float y)
    {
        float dx = scene.TerrainHeight(x + 0.5f, y) - scene.TerrainHeight(x - 0.5f, y);
        float dy = scene.TerrainHeight(x, y + 0.5f) - scene.TerrainHeight(x, y - 0.5f);
        var n = new Vector3(-dx, -dy, 1);
        return Vector3.Normalize(n);
    }
}
