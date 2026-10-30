using LibreMetaverse;
using Sojourn.Render.Frame;
using Sojourn.Render.Geometry;
using Sojourn.Render.Media;
using Sojourn.Render.Shadows;

namespace Sojourn.Render.World;

/// <summary>
/// What the renderer tells a scene about avatars for one view: where their attachment points are, their palettes and
/// extents, what their worn objects do, and the mirror's and the own avatar's part. A build carries its own (DrawCtx),
/// so the main view's and a probe capture's can't overwrite each other's; the scene keeps the main view's as
/// <see cref="RegionScene.Hooks"/> for what places attachments between builds (picks, particle sources, look-at).
/// </summary>
public sealed record SceneHooks
{
    public static readonly SceneHooks None = new();

    /// <summary>Where an avatar's attachment point is this frame (avatar local id, attachment point id), in this
    /// region's frame; null when the avatar isn't posed (out of range, still loading). Null: attachments aren't drawn.</summary>
    public Func<uint, int, (Vector3 Pos, Quaternion Rot)?>? AttachmentFrame { get; init; }

    /// <summary>Rigged meshes: the joint palette for a skin on an avatar posed this frame (its start in the draw
    /// list), or -1.</summary>
    public Func<uint, ShapeGeometry, DrawList, int>? RiggedPalette { get; init; }

    /// <summary>What an avatar's attachments do this frame (Avatars.WornOf); null: drawn.</summary>
    public Func<uint, (Avatars.Worn Mode, ImpostorCapture? Cap)>? WornOf { get; init; }

    /// <summary>An avatar's animated extents this frame (centre in this region's frame, full diagonal), for rigged
    /// LOD and culling; null when it isn't posed.</summary>
    public Func<uint, (Vector3 Center, float Diagonal)?>? AvatarExtents { get; init; }

    /// <summary>A mirror in view (Realtime/FrameRenderer.Mirror.cs): what avatars' worn objects draw in (InstanceFlags:
    /// MainOnly while the mirror leaves avatars out), and the own avatar (its local id, 0 for none) when the mirror shows
    /// it whole: its rigged meshes are skinned once more for the mirror on the pose <see cref="MirrorPalette"/> gives,
    /// and what's worn on its hidden head is drawn there at <see cref="MirrorAttachmentFrame"/>.</summary>
    public uint AvatarFlags { get; init; }
    public uint MirrorSelf { get; init; }
    public Func<uint, ShapeGeometry, DrawList, int>? MirrorPalette { get; init; }
    public Func<uint, int, (Vector3 Pos, Quaternion Rot)?>? MirrorAttachmentFrame { get; init; }

    /// <summary>The person's own avatar in the headset (its local id, 0 for none): what it wears is marked
    /// InstanceFlags.Self for the eyes, as its body is.</summary>
    public uint SelfOwner { get; init; }
}

/// <summary>
/// One draw list build of a region's scene (RegionScene.BuildDrawList, the HUD's): what it was given, the column and
/// object it's drawing, and what it counts for the world, added to it when the build finishes (<see cref="Finish"/>).
/// Everything a build writes and reads back lives here, so a scene can be built by several at once, for different
/// objects; per-object state stays on the SceneObject.
/// </summary>
internal sealed class DrawCtx
{
    public readonly SceneHooks Hooks;
    public readonly ViewSetup V;
    public readonly DrawList Dl;
    public readonly Vector3 Offset;
    public readonly ViewOptions Opt;
    public readonly Frustum Frustum;
    public readonly ShadowFrustum?[]? Casters;
    public readonly Vector3 Axis;
    public readonly IAssetSource Assets;
    /// <summary>The region's simulator for this build.</summary>
    public readonly Simulator Sim;
    public RegionDrawStats Stats;

    // the column being drawn: the shadow maps that reach it (-1: unknown, try all) and whether it's wholly in view
    public int CellCasters = -1;
    public bool CellInView;

    // the impostor capture the attachments being drawn go into, if any, and the instance flags of what's being drawn
    // (InstanceFlags)
    public ImpostorCapture? Capture;
    public uint InstFlags;

    /// <summary>While drawing HUDs: pixels per HUD unit on the image they're drawn for (their textures' size; every LOD
    /// at its best). 0 otherwise.</summary>
    public float HudPx;

    // the screen area of the object being drawn, and each cached texture's share of it
    public float ObjArea = float.PositiveInfinity;
    public float TexFactor = 1f;

    // textures the face being resolved uses, while its object's faces are being cached (and each one's share of the
    // area); whether a material it uses is still on its way
    public List<SceneTexture>? Deps;
    List<float>? _depFactors;
    public List<float> DepFactors => _depFactors ??= new List<float>();
    public bool MaterialsWaiting;

    // where the blended faces of the prim being drawn sort as a group (RegionScene.GroupDepth; NaN: each by its own key)
    public float AlphaGroup = float.NaN;

    // the texture animation of the prim whose faces are being added, if any
    public Particles.TextureAnimState? Anim;
    public int AnimFaces;
    public double AnimElapsed;

    // media (RegionScene.Media.cs): the prim whose faces are being resolved (0: none, as for rigged meshes), its
    // distance, and the surfaces its faces use, kept with its FaceCache so cached draws report them
    public uint MediaPrim;
    public float MediaDist;
    public List<MediaSurface>? MediaUsed;

    // the wearers' bakes as RegionScene.BakeKey has them: once a build per wearer
    Dictionary<uint, int>? _bakeKeys;
    public Dictionary<uint, int> BakeKeys => _bakeKeys ??= new Dictionary<uint, int>();

    // what it counts for the world (RenderWorld.HeldObjects), added when it finishes
    public int HeldObjects;

    public DrawCtx(SceneHooks hooks, ViewSetup v, DrawList dl, Vector3 offset, ViewOptions opt, Frustum frustum,
        ShadowFrustum?[]? casters, Vector3 axis, IAssetSource assets, Simulator sim)
    {
        Hooks = hooks;
        V = v;
        Dl = dl;
        Offset = offset;
        Opt = opt;
        Frustum = frustum;
        Casters = casters;
        Axis = axis;
        Assets = assets;
        Sim = sim;
    }

    /// <summary>For resolving an object's faces alone, off the frame (FaceResolver): no view, nothing drawn.</summary>
    public DrawCtx(IAssetSource assets, Simulator sim)
    {
        Hooks = SceneHooks.None;
        V = null!;
        Dl = null!;
        Opt = null!;
        Frustum = Frustum.All;
        Assets = assets;
        Sim = sim;
    }

    /// <summary>The frame's resolving is spent: every build's this frame, those running at once included
    /// (RenderWorld.AddResolve).</summary>
    public bool ResolveSpent(RenderWorld world) => world.ResolveSpent;

    /// <summary>Adds what this build counted to the world's totals.</summary>
    public void Finish(RenderWorld world)
    {
        world.HeldObjects += HeldObjects;
        HeldObjects = 0;
    }
}
