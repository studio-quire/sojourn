using Sojourn.Render.Frame;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Realtime;

/// <summary>Which depth a headset menu's frost is tested at against the eye's, to hide it where the compositor hides
/// the menu's quad layer behind a wall: Planar, the menu's own (exact); Radial, its distance from the eye taken as a
/// depth along the view (what Meta's compositor appears to do, Overlay.Walls.cs); RadialHead, the same from the point
/// between the eyes.</summary>
public enum MenuDepth { Planar, Radial, RadialHead }

/// <summary>Where a display wants the menus' footprints carved out of the depth it hands its compositor: this eye's
/// layer of its depth swapchain (D32, in DEPTH_STENCIL_ATTACHMENT_OPTIMAL, the eye's depth already copied in).</summary>
public readonly record struct DepthTarget(ImageView View, uint Width, uint Height);

// Menus and walls in the headset (Claude Opus 5.5, 2026-10-28). The menus are quad layers; with the eyes' depth
// submitted (XR_KHR_composition_layer_depth), Meta's compositor hides a quad where the depth is nearer. Over Link that
// test can't be relied on: the panel was hidden early by walls just behind it depending on head pitch, and correcting
// the depth under the menus to distance from the eye (its test compares a quad's radial distance) changed nothing; in
// the headset the user then saw that walls hid menus only inside a roughly circular region around the view, not at all
// outside it. So we don't use it:
//   - Walls hide menus (the default, the user's choice; OverlayScene.MenuDepthFix, "exact"): the compositor never hides
//     a menu (each footprint carved to far in its depth, as below), and we hide them ourselves on the layer over the
//     menus the hands use (Overlay.Hands.cs): its mask takes any point of the eye, not only the own body, that's inside
//     a menu's outline and nearer than it, so walls, rocks and people in front of a menu are drawn over it, exactly.
//     The frost is tested at the menu's own depth, so it hides with it. With a confirmation up the panel stays over
//     everything, so for that frame nothing hides the menus.
//   - The compositor's own test, for comparing (radial, head): the frost is tested at the depth the compositor seems
//     to use (MenuDepth.Radial): the plane's points each pushed out along their ray until their planar depth is their
//     radial distance, times 1 + bias. Per vertex that's exact; across a quad it isn't affine on screen, so the quad is
//     drawn as an 8x8 grid (under 0.1% off within a cell).
//   - Menus over walls (an option): the frost untested, the footprints carved, nothing drawn over the menus but the hands.
// Carving: each menu's footprint (a few pixels wider) written as far (0, reverse-Z) into the depth the compositor gets,
// after the eye's depth is copied there, except where the cut's mask is (what's in front of a menu keeps its depth, so
// its layer still reprojects right). The runtime offers no layer flag for this (XR_FB_composition_layer_depth_test and
// _settings aren't in Meta's PC list), and leaving the depth out while a menu is up would lose positional reprojection
// for the whole view.
public sealed unsafe partial class Overlay
{
    const int MenuGrid = 8;            // a placed sheet's grid, a side, when its depth isn't its plane's
    const float CarveMargin = 4;       // pixels around each menu's footprint carved too
    Pipeline _carve;
    MenuDepth _menuDepth;
    float _menuScale = 1;
    Vector3 _menuFrom;
    int _carveFrom, _carveTo, _carveEye = -1;
    GpuBuffer? _carveVertices;
    readonly Vector4[] _grid = new Vector4[(MenuGrid + 1) * (MenuGrid + 1)];
    int _gridN;

    /// <summary>Whether we hide the menus behind what's in front of them this frame, the compositor never doing it
    /// (walls hide menus, the exact test, no confirmation up).</summary>
    static bool WorldOverMenus(OverlayScene scene) => !scene.MenusOverWalls && scene.MenuDepthFix && !scene.PanelOverWalls;

    void CreateWalls(ShaderCompiler shaders)
    {
        _carve = PipelineBuilder.Create(_vk, new GraphicsPipelineDesc
        {
            Vertex = shaders.Get("overlay.vert", ShaderStage.Vertex), Fragment = shaders.Get("overlay.frag", ShaderStage.Fragment, "CARVE"),
            DepthFormat = RenderTargets.DepthFormat, DepthTest = true, DepthWrite = true, DepthCompare = CompareOp.Always,
            Cull = CullModeFlags.None, Layout = _layout,
        });
    }

    /// <summary>This eye's menu depth, from the scene (Record, after BeginCut and before the sheets are built).</summary>
    void BeginWalls(OverlayScene scene, ViewSetup view, Vector3 head)
    {
        // hidden by us, the frost's test is exact (its bias still applies, for comparing)
        _menuDepth = scene.MenusOverWalls || scene.MenuDepthFix ? MenuDepth.Planar : scene.MenuDepth;
        _menuScale = scene.MenusOverWalls ? 1 : Math.Max(0.1f, 1 + scene.MenuDepthBias);
        _menuFrom = _menuDepth == MenuDepth.RadialHead ? head : view.Position;
        _carveFrom = _carveTo = 0;
        _carveEye = -1;
    }

    /// <summary>A sheet or card placed in the world by its corners (top left, top right, bottom right, bottom left):
    /// its grid projected, each point at the depth it's tested at; false if any is behind the eye.</summary>
    bool ProjectPlaced(Vector3[] k, Matrix4x4 viewProj)
    {
        int n = _gridN = _menuDepth == MenuDepth.Planar ? 1 : MenuGrid;
        for (int j = 0; j <= n; j++)
            for (int i = 0; i <= n; i++)
            {
                float s = i / (float)n, t = j / (float)n;
                var p = Vector3.Lerp(Vector3.Lerp(k[0], k[1], s), Vector3.Lerp(k[3], k[2], s), t);
                if (!ProjectZW(p, viewProj, out var q)) return false;
                if (_menuDepth == MenuDepth.Planar) q.Z /= _menuScale;
                else
                {
                    // reverse-Z with an infinite far plane: depth = near / planar distance, and clip z (ndc z * w) = near
                    float r = Vector3.Distance(p, _menuFrom);
                    if (r > 1e-4f) q.Z = q.Z * q.W / (r * _menuScale);
                }
                _grid[j * (n + 1) + i] = q;
            }
        return true;
    }

    /// <summary>The last projected grid as quads, the picture's rectangle (uv0 top left, uv1 bottom right) over it.</summary>
    void PlacedQuads(List<MeshVertex> v, Vector4 color, int style, Vector2 uv0, Vector2 uv1)
    {
        int n = _gridN;
        Vector2 Uv(int i, int j) => uv0 + (uv1 - uv0) * new Vector2(i / (float)n, j / (float)n);
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
                QuadZW(v, _grid[j * (n + 1) + i], _grid[j * (n + 1) + i + 1], _grid[(j + 1) * (n + 1) + i + 1], _grid[(j + 1) * (n + 1) + i],
                    color, style, Uv(i, j), Uv(i + 1, j), Uv(i + 1, j + 1), Uv(i, j + 1));
    }

    readonly List<Vector3[]> _carved = new();

    /// <summary>With menus over walls, each menu's footprint in this eye (a little wider), at the far plane; with a
    /// confirmation up, the panel's in either mode. Appended after everything drawn (<see cref="RecordWalls"/> draws
    /// them into the depth the compositor gets).</summary>
    void CarveVertices(List<MeshVertex> v, OverlayScene scene, Matrix4x4 viewProj, float w, float h, int eye)
    {
        _carved.Clear();
        if ((scene.MenusOverWalls || WorldOverMenus(scene)) && scene.Cut is { Menus.Count: > 0 } cut) _carved.AddRange(cut.Menus);   // the panel among them
        else if ((scene.MenusOverWalls || scene.PanelOverWalls) && scene.PanelCorners is { Length: 4 } pc) _carved.Add(pc);
        if (_carved.Count == 0) return;
        _carveFrom = v.Count;
        _carveEye = eye;
        const float far = 1e-9f;   // overlay.vert reads 0 as "no depth"; this is as far as 0 to the compositor
        Span<Vector2> c = stackalloc Vector2[4];
        foreach (var m in _carved)
        {
            if (m is not { Length: 4 }) continue;
            bool behind = false;
            for (int i = 0; i < 4 && !behind; i++)
            {
                if (ProjectW(m[i], viewProj, out var p)) c[i] = new Vector2(p.X, p.Y);
                else behind = true;
            }
            if (behind)
            {
                // reaching behind the eye: all of it
                Quad(v, new Vector3(-1, -1, far), new Vector3(1, -1, far), new Vector3(1, 1, far), new Vector3(-1, 1, far), Vector4.Zero, 0,
                    new(0, 0), new(1, 0), new(1, 1), new(0, 1));
                continue;
            }
            var mid = (c[0] + c[1] + c[2] + c[3]) * 0.25f;
            var px = new Vector2(2 / w, 2 / h);   // a pixel in NDC
            for (int i = 0; i < 4; i++)
            {
                var d = (c[i] - mid) / px;
                float len = d.Length();
                if (len > 1e-3f) c[i] += d / len * CarveMargin * px;
            }
            Quad(v, new Vector3(c[0], far), new Vector3(c[1], far), new Vector3(c[2], far), new Vector3(c[3], far), Vector4.Zero, 0,
                new(0, 0), new(1, 0), new(1, 1), new(0, 1));
        }
        _carveTo = v.Count;
    }

    /// <summary>Into the display's depth for this eye (after <see cref="Record"/> for the same eye and the display's copy
    /// of the eye's depth): the menus' footprints carved to far, so the compositor never hides them (we do, or nothing
    /// does).</summary>
    public void RecordWalls(CommandBuffer cmd, int slot, int eye, DepthTarget t)
    {
        bool carve = _carveEye == eye && _carveTo > _carveFrom && _carveVertices != null;
        if (!carve) return;
        var api = _vk.Api;
        Mark?.Invoke("present");
        var datt = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = t.View, ImageLayout = ImageLayout.DepthStencilAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load, StoreOp = AttachmentStoreOp.Store,
        };
        var area = new Rect2D(new Offset2D(0, 0), new Extent2D(t.Width, t.Height));
        var ri = new RenderingInfo { SType = StructureType.RenderingInfo, RenderArea = area, LayerCount = 1, PDepthAttachment = &datt };
        var vp = new Viewport(0, 0, t.Width, t.Height, 0, 1);
        api.CmdBeginRendering(cmd, in ri);
        api.CmdSetViewport(cmd, 0, 1, in vp);
        var set = _sets[slot];
        var buf = _carveVertices!.Handle;
        ulong offset = 0;
        api.CmdSetScissor(cmd, 0, 1, in area);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _carve);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);
        api.CmdBindVertexBuffers(cmd, 0, 1, in buf, in offset);
        api.CmdDraw(cmd, (uint)(_carveTo - _carveFrom), 1, (uint)_carveFrom, 0);
        api.CmdEndRendering(cmd);
        Mark?.Invoke("walls");
    }

    void DisposeWalls()
    {
        _vk.Api.DestroyPipeline(_vk.Device, _carve, null);
    }
}
