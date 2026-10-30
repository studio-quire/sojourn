using LibreMetaverse;
using Sojourn.Render.Frame;
using Sojourn.Render.Gpu;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Sojourn.Render.Realtime;

/// <summary>An RGBA8 image (premultiplied, gamma-encoded) for the overlay; a new <see cref="Version"/> re-uploads it.
/// Frost (0-1): the world behind it blurred, where its alpha reaches Edge (glass panels).</summary>
public sealed record OverlayImage(byte[] Rgba, int Width, int Height, long Version, float Frost = 0, float Edge = 1);

/// <summary>A beam between two points of the frame's region (a pointer ray), its width in metres.</summary>
public readonly record struct OverlayBeam(Vector3 From, Vector3 To, float Width, Vector4 Color);

/// <summary>A disc facing the eye at a point of the frame's region, its radius in metres.</summary>
public readonly record struct OverlayDot(Vector3 At, float Radius, Vector4 Color);

public enum OverlayGlyphKind { Mic, MicMuted, Hud, Stand, Bell, Private, PrivateSees, Camera }

/// <summary>A small token facing the eye at a point of the frame's region: a disc (its radius in metres, a soft rim of
/// light at its edge) with a glyph drawn in Ink, and, while Level is above 0, a ring around it that grows and brightens
/// with it (in the disc's colour).</summary>
public readonly record struct OverlayGlyph(Vector3 At, float Radius, Vector4 Disc, Vector4 Ink, OverlayGlyphKind Kind, float Level = 0);

/// <summary>
/// A name tag over an avatar's head as the renderer draws it this frame (<see cref="NameTag.Head"/>): a picture from the
/// labels atlas (Uv: x, y, width, height in its pixels), upright and turned to the eye, its bottom Lift metres above
/// the head, MetresPerPx big. Marks the overlay draws with it, in the picture's pixels from its top left: a light
/// at its left while the avatar speaks (Speaking 0-1, its loudness; below 0: not speaking), centred on LineY; three
/// dots while they type (Typing: the animation's phase 0-1; below 0: not typing), from Dots.X a step of Dots.Y apart,
/// Dots.Z in radius. With At (and no avatar), it stands over that point instead: an object's floating text.
/// </summary>
public readonly record struct OverlayTag(UUID Avatar, Vector4 Uv, float MetresPerPx, float Alpha, float Lift, float LineY = 0,
    float Speaking = -1, Vector4 SpeakingColor = default, float Typing = -1, Vector4 TypingColor = default, Vector3 Dots = default,
    Vector3? At = null)
{
    /// <summary>Hidden by what's in front of it (the eye's depth test), or drawn over everything; null: floating text
    /// (At, no avatar) is tested and the rest aren't. Name tags are tested by default since 2026-10-28, as the
    /// viewer's are (Sojourn.Client's NameTags).</summary>
    public bool? DepthTested { get; init; }

    public bool Tested => DepthTested ?? (At != null && Avatar == UUID.Zero);
}

/// <summary>A flat picture over the view: at a pixel of the eye image (desktop), or placed in the world by its corners
/// (top left, top right, bottom right, bottom left in the frame's region; the headset, where it's its own layer and
/// the eyes get only its frost unless Content).</summary>
public sealed record OverlaySheet(OverlayImage Image, Vector2 At = default, Vector3[]? Corners = null, bool Content = true);

/// <summary>
/// One of the assistant's elements (Sojourn.Client.Showing), a picture in the shown atlas (Uv: x, y, width, height in
/// its pixels). It goes one of three ways: flat on the image with its top left at Screen (a pixel per atlas pixel); at
/// Corners in the world (top left, top right, bottom right, bottom left in the frame's region; the headset's cards,
/// whose own layer carries them, so the eyes get only their frost unless Content); or upright and turned to the eye,
/// its bottom centre Lift above At, MetresPerPx big (an atlas pixel), or Lift above an avatar's head as the renderer
/// drew it this frame (Avatar), as name tags are.
/// </summary>
public sealed record OverlayCard(Vector4 Uv)
{
    public float Alpha { get; init; } = 1;
    public Vector2? Screen { get; init; }
    public Vector3[]? Corners { get; init; }
    public Vector3 At { get; init; }
    public UUID Avatar { get; init; }
    public float Lift { get; init; }
    public float MetresPerPx { get; init; }
    public bool Content { get; init; } = true;
    public float Frost { get; init; }
    public float Edge { get; init; } = 1;
    /// <summary>Held at a hand, as the wrist's tokens are (the location line): its layer goes over the hands' layer and
    /// everything else in the headset (XrSession.HeldCards), so its frost is drawn untested.</summary>
    public bool Held { get; init; }
}

/// <summary>The handheld camera's preview (Sojourn.Client's Control/Shutter.cs; Realtime/FrameRenderer.Preview.cs): a
/// camera in the frame's region (at, its facing and up), its half-angles' tangents, and the panel it's shown on by its
/// corners (top left, top right, bottom right, bottom left). A standby (the camera out, nothing framed) only keeps the
/// preview's targets ready; nothing is drawn.</summary>
public sealed record CameraPreview(Vector3 At, Vector3 Forward, Vector3 Up, float TanH, float TanV, Vector3[] Corners)
{
    public bool Standby { get; init; }
    public static readonly CameraPreview Waiting = new(default, Vector3.UnitX, Vector3.UnitZ, 1, 1, []) { Standby = true };
}

/// <summary>What's drawn over each finished eye image: name tags over avatars, the chat sheet, the panel flat on the
/// image (top left at a pixel), and beams and dots placed in the world. Snapshots are taken before it, so they never
/// show it.</summary>
public sealed record OverlayScene
{
    public OverlayImage? Panel { get; init; }
    public Vector2 PanelAt { get; init; }
    public IReadOnlyList<OverlayBeam> Beams { get; init; } = Array.Empty<OverlayBeam>();
    public IReadOnlyList<OverlayDot> Dots { get; init; } = Array.Empty<OverlayDot>();
    public IReadOnlyList<OverlayGlyph> Glyphs { get; init; } = Array.Empty<OverlayGlyph>();
    /// <summary>The panel placed in the world instead (top left, top right, bottom right, bottom left in the frame's
    /// region): where the headset's panel layer floats, for its frost, and a preview of the layer for checking without
    /// a headset (<see cref="PanelContent"/>).</summary>
    public Vector3[]? PanelCorners { get; init; }
    /// <summary>Draw the panel itself, not just its frost (in the headset the panel is its own layer over the eyes).</summary>
    public bool PanelContent { get; init; } = true;
    /// <summary>The atlas the tags' pictures come from.</summary>
    public OverlayImage? Labels { get; init; }
    public IReadOnlyList<OverlayTag> Tags { get; init; } = Array.Empty<OverlayTag>();
    /// <summary>Chat and messages (drawn under the panel).</summary>
    public OverlaySheet? Feed { get; init; }
    /// <summary>The reader (Sojourn.Client's Reader): in the headset its own layer, so the eyes get its frost (unless
    /// Content); on the desktop it takes the panel's place instead.</summary>
    public OverlaySheet? Reader { get; init; }
    /// <summary>The assistant's elements (over the tags, under the feed and the panel), from the shown atlas.</summary>
    public OverlayImage? ShownAtlas { get; init; }
    public IReadOnlyList<OverlayCard> Cards { get; init; } = Array.Empty<OverlayCard>();
    /// <summary>The client's own boards and lines (Sojourn.Client's Map/: the map board, the location line), from their
    /// atlas, placed as the assistant's cards are (flat at a pixel, or at corners with only their frost in the
    /// headset), over the assistant's cards and under the feed.</summary>
    public OverlayImage? BoardAtlas { get; init; }
    public IReadOnlyList<OverlayCard> Boards { get; init; } = Array.Empty<OverlayCard>();
    /// <summary>A web page or video shown up close on the desktop (Media/PageView), BGRA, over the feed and under the panel.</summary>
    public OverlaySheet? Page { get; init; }
    /// <summary>The headset's menus and hands this frame: the person's hands are drawn over the menus where they're in
    /// front of them (Overlay.Hands.cs); null for none.</summary>
    public HandCut? Cut { get; init; }
    /// <summary>The headset's menus always over the world (Overlay.Walls.cs): their frost untested, and their footprints
    /// (Cut's menus) carved out of the depth the compositor gets (<see cref="Overlay.RecordWalls"/>). Off, walls hide
    /// them: with <see cref="MenuDepthFix"/> we do (carved too, and what's in front drawn over them on the hands'
    /// layer), their frost tested exactly; without, the compositor's own test does, their frost tested at
    /// <see cref="MenuDepth"/> (its apparent error). Either way its distance times 1 + <see cref="MenuDepthBias"/>.</summary>
    public bool MenusOverWalls { get; init; }
    public bool MenuDepthFix { get; init; }
    /// <summary>The panel always over the world, in either mode (a confirmation: nothing may cover it): its frost
    /// untested and its footprint (<see cref="PanelCorners"/>) carved out of the compositor's depth.</summary>
    public bool PanelOverWalls { get; init; }
    public MenuDepth MenuDepth { get; init; } = MenuDepth.Planar;
    public float MenuDepthBias { get; init; }
    /// <summary>Take a small sample of the world behind every few frames (<see cref="Overlay.Sampled"/>, Overlay.Backdrop.cs).</summary>
    public bool SampleBackdrop { get; init; }
    /// <summary>The handheld camera's live preview, drawn at its panel's corners in the world (under everything else,
    /// tested against the eye's depth and writing its own), and with the held things over the menus.</summary>
    public CameraPreview? Preview { get; init; }
}

/// <summary>
/// Draws <see cref="OverlayScene"/>s into the renderer's output images after post-processing (and after any capture):
/// one small pipeline, premultiplied alpha, no depth, vertices computed on the CPU per eye. A glass sheet (frost) first
/// gets the eye image blitted down into a mip chain (the backdrop) and the world behind it drawn blurred from that.
/// Four pictures can be bound at once: the panel, the labels atlas (tags), the feed and the assistant's elements' atlas.
/// </summary>
public sealed unsafe partial class Overlay : IDisposable
{
    const int StartVertices = 4096;   // an eye's vertex buffer to start with; it grows to whatever a frame needs
    const int Bindings = 10;   // panel, backdrop, labels, feed, shown, page, the hands' mask (Overlay.Hands.cs), reader, boards, the camera's preview
    readonly VulkanContext _vk;
    readonly DescriptorSetLayout _setLayout;
    readonly DescriptorPool _pool;
    readonly DescriptorSet[] _sets;
    readonly GpuImage?[][] _setImages;
    readonly PipelineLayout _layout;
    readonly Pipeline _pipeline;
    // world labels (name tags, floating text) with the eye's depth: they write their own where they show, so the
    // compositor's depth reprojection moves them as what they are (not as the wall or sky behind), and floating text is
    // hidden by what's in front of it as the viewer's depth test hides it; name tags too unless the person shows them
    // through walls (OverlayTag.DepthTested)
    readonly Pipeline _worldTested, _worldOver;
    // the cards and sheets (panel, feed, page) after them, tested against the eye's depth without writing it: one
    // placed in the world is hidden by what's in front of it, its frost too. The headset's compositor hides a menu's
    // quad layer behind a nearer wall by the eyes' depth, and the frost drawn without a test stayed on the wall, a
    // panel-sized blur with nothing on it (the user, 2026-10-28). Flat ones and upright labels sit at the near plane;
    // placed ones at the depth the compositor seems to test them at, or untested with menus over walls (Overlay.Walls.cs).
    readonly Pipeline _sheetTested;
    readonly Sampler _sampler;
    // a vertex buffer per frame slot and eye (VertexBuffer), grown when a frame's overlay needs more; it used to be a
    // fixed 4096 vertices an eye, and what didn't fit was cut off mid-tag (tags drawn as grids of cells then, the bottom
    // rows of the last ones in a crowd missing, flickering as the count changed: the session of 2026-10-28)
    readonly GpuBuffer[] _vertices;
    readonly Sampler _blurSampler;
    GpuImage _backdrop;
    const int BackdropMips = 6;
    bool _backdropThisEye;

    /// <summary>A picture the overlay keeps on the GPU, re-uploaded when its version changes.</summary>
    sealed class Tex(GpuImage image, int frames, Format format = Format.R8G8B8A8Unorm)
    {
        public GpuImage Image = image;
        public readonly Format Format = format;
        public long Version = -1;
        public readonly GpuBuffer?[] Staging = new GpuBuffer?[frames];
    }
    readonly Tex _panel, _labels, _feed, _shown, _page, _reader, _boards;
    // the camera's preview this frame (the renderer's preview output, shader-readable), or a pixel standing in
    GpuImage? _preview;
    readonly GpuImage _noPreview;

    public Overlay(VulkanContext vk, ShaderCompiler shaders, int framesInFlight)
    {
        _vk = vk;
        var api = vk.Api;
        var bindings = stackalloc DescriptorSetLayoutBinding[Bindings];
        for (int i = 0; i < Bindings; i++)
            bindings[i] = new DescriptorSetLayoutBinding((uint)i, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit);
        var lci = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = Bindings, PBindings = bindings };
        VulkanContext.Check(api.CreateDescriptorSetLayout(vk.Device, in lci, null, out _setLayout), "overlay set layout");
        var size = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, (uint)(framesInFlight * Bindings));
        var pci = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = (uint)framesInFlight, PoolSizeCount = 1, PPoolSizes = &size };
        VulkanContext.Check(api.CreateDescriptorPool(vk.Device, in pci, null, out _pool), "overlay pool");
        var setLayout = _setLayout;
        // a set per frame slot: a slot's set is rewritten only once that slot's last frame is done with it
        _sets = new DescriptorSet[framesInFlight];
        _setImages = Enumerable.Range(0, framesInFlight).Select(_ => new GpuImage?[Bindings]).ToArray();
        for (int i = 0; i < framesInFlight; i++)
        {
            var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _pool, DescriptorSetCount = 1, PSetLayouts = &setLayout };
            VulkanContext.Check(api.AllocateDescriptorSets(vk.Device, in ai, out _sets[i]), "overlay set");
        }
        var plci = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout };
        VulkanContext.Check(api.CreatePipelineLayout(vk.Device, in plci, null, out _layout), "overlay layout");
        var sci = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo, MagFilter = Filter.Linear, MinFilter = Filter.Linear, MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge, AddressModeV = SamplerAddressMode.ClampToEdge, AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 0,
        };
        VulkanContext.Check(api.CreateSampler(vk.Device, in sci, null, out _sampler), "overlay sampler");
        sci.MaxLod = BackdropMips;
        VulkanContext.Check(api.CreateSampler(vk.Device, in sci, null, out _blurSampler), "overlay blur sampler");
        _pipeline = PipelineBuilder.Create(vk, new GraphicsPipelineDesc
        {
            Vertex = shaders.Get("overlay.vert", ShaderStage.Vertex),
            Fragment = shaders.Get("overlay.frag", ShaderStage.Fragment),
            ColorFormats = new[] { RenderTargets.OutputFormat },
            Blend = new[] { BlendMode.PremultipliedAlpha },
            DepthTest = false, DepthWrite = false,
            Cull = CullModeFlags.None,
            Layout = _layout,
        });
        var worldFs = shaders.Get("overlay.frag", ShaderStage.Fragment, "WORLD_DEPTH");
        GraphicsPipelineDesc World(bool tested) => new()
        {
            Vertex = shaders.Get("overlay.vert", ShaderStage.Vertex), Fragment = worldFs,
            ColorFormats = new[] { RenderTargets.OutputFormat }, Blend = new[] { BlendMode.PremultipliedAlpha },
            DepthFormat = RenderTargets.DepthFormat, DepthTest = true, DepthWrite = true,
            DepthCompare = tested ? CompareOp.GreaterOrEqual : CompareOp.Always, Cull = CullModeFlags.None, Layout = _layout,
        };
        _worldTested = PipelineBuilder.Create(vk, World(true));
        _worldOver = PipelineBuilder.Create(vk, World(false));
        var sheets = World(true);
        sheets.Fragment = shaders.Get("overlay.frag", ShaderStage.Fragment);
        sheets.DepthWrite = false;
        _sheetTested = PipelineBuilder.Create(vk, sheets);
        _vertices = Enumerable.Range(0, framesInFlight * 2).Select(_ => NewVertices(StartVertices)).ToArray();
        _panel = new Tex(NewPicture(1, 1), framesInFlight);
        _labels = new Tex(NewPicture(1, 1), framesInFlight);
        _feed = new Tex(NewPicture(1, 1), framesInFlight);
        _reader = new Tex(NewPicture(1, 1), framesInFlight);
        _shown = new Tex(NewPicture(1, 1), framesInFlight);
        _boards = new Tex(NewPicture(1, 1), framesInFlight);
        _page = new Tex(NewPicture(1, 1, Format.B8G8R8A8Unorm), framesInFlight, Format.B8G8R8A8Unorm);
        _noPreview = NewPicture(1, 1);
        _backdrop = NewBackdrop(1, 1);
        CreateCut(shaders, framesInFlight);
        CreateWalls(shaders);
    }

    GpuBuffer NewVertices(int count) =>
        new(_vk, (ulong)count * PipelineBuilder.VertexStride, BufferUsageFlags.VertexBufferBit, MemoryUse.Upload);

    /// <summary>This slot and eye's vertex buffer, at least this many vertices big. A bigger one replaces it: the old one
    /// was last read by this slot's previous frame (done), and goes once this frame is.</summary>
    GpuBuffer VertexBuffer(FrameResources fr, int slot, int eye, int count)
    {
        int i = slot * 2 + (eye & 1);
        var vb = _vertices[i];
        if (vb.Size >= (ulong)count * PipelineBuilder.VertexStride) return vb;
        fr.Retire.Add(vb.Dispose);
        return _vertices[i] = NewVertices(Math.Max(count, (int)(vb.Size / PipelineBuilder.VertexStride) * 2));
    }

    GpuImage NewPicture(uint w, uint h, Format format = Format.R8G8B8A8Unorm) => new(_vk, w, h, format, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit);

    GpuImage NewBackdrop(uint w, uint h)
    {
        uint mips = 1;
        while (mips < BackdropMips && (w >> (int)mips) > 0 && (h >> (int)mips) > 0) mips++;
        return new GpuImage(_vk, w, h, RenderTargets.OutputFormat,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.ColorAttachmentBit, mips);
    }

    void WriteDescriptor(int slot)
    {
        var want = new[] { _panel.Image, _backdrop, _labels.Image, _feed.Image, _shown.Image, _page.Image, MaskBound, _reader.Image, _boards.Image, _preview ?? _noPreview };
        var have = _setImages[slot];
        if (want.Select((img, i) => img == have[i]).All(same => same)) return;
        var infos = stackalloc DescriptorImageInfo[Bindings];
        var w = stackalloc WriteDescriptorSet[Bindings];
        for (int i = 0; i < Bindings; i++)
        {
            have[i] = want[i];
            infos[i] = new DescriptorImageInfo(i == 1 ? _blurSampler : _sampler, want[i].View, ImageLayout.ShaderReadOnlyOptimal);
            w[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = _sets[slot], DstBinding = (uint)i, DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &infos[i],
            };
        }
        _vk.Api.UpdateDescriptorSets(_vk.Device, Bindings, w, 0, null);
    }

    /// <summary>A glass sheet wants the backdrop this eye: made the target's half size now (its mips set the frost's
    /// depth), recorded once the vertices are built (<see cref="RecordBackdrop"/>, after the hands' mask).</summary>
    void NeedBackdrop(FrameResources fr, GpuImage target)
    {
        _backdropThisEye = true;
        uint w = Math.Max(1, target.Width / 2), h = Math.Max(1, target.Height / 2);
        if (_backdrop.Width != w || _backdrop.Height != h)
        {
            var (old, oldView) = (_backdrop, _backdropMip0);
            fr.Retire.Add(() => { if (oldView.Handle != 0) _vk.Api.DestroyImageView(_vk.Device, oldView, null); old.Dispose(); });
            _backdropMip0 = default;
            _backdrop = NewBackdrop(w, h);
        }
    }

    /// <summary>The eye image (TransferSrcOptimal, left so) blitted at half size into the backdrop (the hands taken out
    /// while they're over the menus) and down its mips, then left shader-readable.</summary>
    void RecordBackdrop(CommandBuffer cmd, GpuImage target, int slot, int eye)
    {
        var api = _vk.Api;
        uint w = _backdrop.Width, h = _backdrop.Height;
        var b = _backdrop;
        GpuImage.Barrier(_vk, cmd, b.Handle, b.Aspect, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, b.Mips, 0, 1);
        var blit = new ImageBlit
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
        };
        blit.SrcOffsets[1] = new Offset3D((int)target.Width, (int)target.Height, 1);
        blit.DstOffsets[1] = new Offset3D((int)w, (int)h, 1);
        api.CmdBlitImage(cmd, target.Handle, ImageLayout.TransferSrcOptimal, b.Handle, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);
        if (_cutThisEye) MaskBackdrop(cmd, slot, eye);
        int mw = (int)w, mh = (int)h;
        for (uint m = 1; m < b.Mips; m++)
        {
            GpuImage.Barrier(_vk, cmd, b.Handle, b.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, m - 1, 1, 0, 1);
            int nw = Math.Max(1, mw / 2), nh = Math.Max(1, mh / 2);
            var down = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m - 1, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m, 0, 1),
            };
            down.SrcOffsets[1] = new Offset3D(mw, mh, 1);
            down.DstOffsets[1] = new Offset3D(nw, nh, 1);
            api.CmdBlitImage(cmd, b.Handle, ImageLayout.TransferSrcOptimal, b.Handle, ImageLayout.TransferDstOptimal, 1, in down, Filter.Linear);
            mw = nw; mh = nh;
        }
        if (b.Mips > 1)
            GpuImage.Barrier(_vk, cmd, b.Handle, b.Aspect, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal, 0, b.Mips - 1, 0, 1);
        GpuImage.Barrier(_vk, cmd, b.Handle, b.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, b.Mips - 1, 1, 0, 1);
        b.Layout = ImageLayout.ShaderReadOnlyOptimal;
    }

    Dictionary<UUID, Vector3>? _heads;

    /// <summary>Record the overlay for one eye into its output image (TransferSrcOptimal before and after). heads: the
    /// name tags the renderer placed this frame (where the tags go). depth: the eye's depth, which tags and floating text
    /// are tested against and write; head: the point between the eyes they turn to (one plane for both eyes). preview:
    /// the camera's preview image this frame (shader-readable), for <see cref="OverlayScene.Preview"/>.</summary>
    public void Record(FrameResources fr, int slot, CommandBuffer cmd, GpuImage target, int eye, ViewSetup view, OverlayScene scene,
        IReadOnlyList<NameTag>? heads = null, GpuImage? depth = null, Vector3? head = null, GpuImage? normal = null, GpuImage? preview = null)
    {
        var api = _vk.Api;
        _preview = scene.Preview is { Standby: false } && preview?.Layout == ImageLayout.ShaderReadOnlyOptimal ? preview : null;
        if (eye == 0)
        {
            Upload(fr, slot, cmd, _panel, scene.Panel);
            Upload(fr, slot, cmd, _labels, scene.Tags.Count > 0 ? scene.Labels : null);
            Upload(fr, slot, cmd, _feed, scene.Feed?.Image);
            Upload(fr, slot, cmd, _reader, scene.Reader?.Image);
            Upload(fr, slot, cmd, _shown, scene.Cards.Count > 0 ? scene.ShownAtlas : null);
            Upload(fr, slot, cmd, _boards, scene.Boards.Count > 0 ? scene.BoardAtlas : null);
            Upload(fr, slot, cmd, _page, scene.Page?.Image);
            _heads = null;
            if ((scene.Tags.Count > 0 || scene.Cards.Any(c => c.Avatar != UUID.Zero)) && heads is { Count: > 0 })
            {
                _heads = new Dictionary<UUID, Vector3>(heads.Count);
                foreach (var t in heads) if (t.Id != UUID.Zero) _heads[t.Id] = t.Head;
            }
            ClearUnwritten(cmd);
        }
        _backdropThisEye = false;
        BeginCut(fr, cmd, slot, eye, scene, view, target, depth, normal);
        BeginWalls(scene, view, head ?? view.Position);
        WantSample(fr, target, eye, scene);
        var verts = _verts;   // the overlay's own, refilled per eye (it grew afresh twice a frame)
        verts.Clear();
        float w = target.Width, h = target.Height;
        var viewProj = view.View * view.Proj;

        // the camera's preview first, held between the hands: hidden by what's in front of it (the hands, a little past
        // its plane too), its depth written (later tests see it; the compositor reprojects it about where it is)
        var previewCorners = _preview != null ? PreviewQuad(verts, scene.Preview!, viewProj) : null;
        // name tags and floating text next: they belong to the world, under everything else; floating text, then tags
        var facing = head ?? view.Position;
        int tested = verts.Count, world = tested;
        if (scene.Labels is { } atlas && _labels.Version == atlas.Version)
        {
            var size = new Vector2(atlas.Width, atlas.Height);
            // the depth-tested ones first (floating text, and name tags unless they show through walls), then the rest
            void Place(OverlayTag t)
            {
                if (t.At is { } at) Tag(verts, t, at, size, view, viewProj, facing);
                else if (_heads != null && _heads.TryGetValue(t.Avatar, out var top)) Tag(verts, t, top, size, view, viewProj, facing);
            }
            foreach (var t in scene.Tags) if (t.Tested) Place(t);
            tested = verts.Count;
            foreach (var t in scene.Tags) if (!t.Tested) Place(t);
            world = verts.Count;
        }

        // the assistant's elements over them
        if (scene.ShownAtlas is { } sa && _shown.Version == sa.Version)
            foreach (var card in scene.Cards) Card(fr, cmd, target, verts, card, new Vector2(sa.Width, sa.Height), view, viewProj);
        // the client's own boards over them
        if (scene.BoardAtlas is { } ba && _boards.Version == ba.Version)
            foreach (var card in scene.Boards) if (!card.Held) Card(fr, cmd, target, verts, card, new Vector2(ba.Width, ba.Height), view, viewProj, style: 26, frostStyle: 27);

        // the feed, the reader, then the panel over them: each a picture flat on the image or placed in the world, with
        // its frost
        if (scene.Feed is { } feed && _feed.Version == feed.Image.Version)
            Sheet(fr, cmd, target, verts, feed.Image, feed.At, feed.Corners, feed.Content, viewProj, frostStyle: 11, style: 10);
        if (scene.Reader is { } reading && _reader.Version == reading.Image.Version)
            Sheet(fr, cmd, target, verts, reading.Image, reading.At, reading.Corners, reading.Content, viewProj, frostStyle: 25, style: 24);
        if (scene.Page is { } page && _page.Version == page.Image.Version)
            Sheet(fr, cmd, target, verts, page.Image, page.At, page.Corners, page.Content, viewProj, frostStyle: 16, style: 15);
        int panelFrom = verts.Count;   // the panel last of them: untested while it's a confirmation (PanelOverWalls)
        if (scene.Panel is { } p && _panel.Version == p.Version)
            Sheet(fr, cmd, target, verts, p, scene.PanelAt, scene.PanelCorners, scene.PanelContent, viewProj, frostStyle: 4, style: 1);
        int sheets = verts.Count;   // the cards and sheets end here, tested against the eye's depth (_sheetTested)
        // boards held at a hand (the location line): untested, as nothing in front covers them; before the beams' held
        // pass, so they stay out of the hands' mask
        if (scene.BoardAtlas is { } hb && _boards.Version == hb.Version)
            foreach (var card in scene.Boards) if (card.Held) Card(fr, cmd, target, verts, card, new Vector2(hb.Width, hb.Height), view, viewProj, style: 26, frostStyle: 27);

        // beams, dots and tokens; the held ones (near the hands) last, where the hands' mask takes them too
        var cut = _cutThisEye ? scene.Cut : null;
        int heldFrom = int.MaxValue;
        for (int pass = 0; pass < (cut != null ? 2 : 1); pass++)
        {
            bool held = pass == 1;
            foreach (var beam in scene.Beams) if (cut == null || Held(cut, beam.From) == held) Beam(verts, beam, view, viewProj);
            foreach (var dot in scene.Dots) if (cut == null || HeldDot(cut, scene, dot) == held) Dot(verts, dot, view, viewProj);
            foreach (var g in scene.Glyphs) if (cut == null || Held(cut, g.At) == held) Glyph(verts, g, view, viewProj);
            if (pass == 0)
            {
                heldFrom = verts.Count;
                // the preview's footprint among the held things, so it shows over the menus where it's in front of them
                // as the hands do (drawn as nothing into the eye: style 30)
                if (cut != null && previewCorners is { } pc) QuadZW(verts, pc[0], pc[1], pc[2], pc[3], Vector4.One, 30, new(0, 0), new(1, 0), new(1, 1), new(0, 1));
            }
        }
        int drawn = verts.Count;   // after them, only the menus' footprints for the compositor's depth (RecordWalls)
        CarveVertices(verts, scene, viewProj, w, h, eye);
        var vb = VertexBuffer(fr, slot, eye, verts.Count);
        _carveVertices = vb;
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(verts).CopyTo(vb.MappedAs<MeshVertex>());
        ulong offset = 0;
        WriteDescriptor(slot);
        if (_cutThisEye) RecordMask(cmd, slot, eye, depth!, normal!, Math.Min(heldFrom, drawn), drawn, vb);
        if (_backdropThisEye) RecordBackdrop(cmd, target, slot, eye);
        if (_sampleThisEye) RecordSample(fr, cmd, slot, scene, viewProj, target);
        if (_backdrop.Layout != ImageLayout.ShaderReadOnlyOptimal)
            _backdrop.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);   // bound, if never drawn from
        if (drawn == 0) return;

        target.Transition(cmd, ImageLayout.ColorAttachmentOptimal);
        var att = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = target.View, ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Load, StoreOp = AttachmentStoreOp.Store,
        };
        var ri = new RenderingInfo
        {
            SType = StructureType.RenderingInfo, RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(target.Width, target.Height)),
            LayerCount = 1, ColorAttachmentCount = 1, PColorAttachments = &att,
        };
        var vp = new Viewport(0, 0, w, h, 0, 1);
        var sc = new Rect2D(new Offset2D(0, 0), new Extent2D(target.Width, target.Height));
        var set = _sets[slot];
        var buf = vb.Handle;
        void Begin(Pipeline pipeline, RenderingInfo info)
        {
            api.CmdBeginRendering(cmd, in info);
            api.CmdSetViewport(cmd, 0, 1, in vp);
            api.CmdSetScissor(cmd, 0, 1, in sc);
            api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
            api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, 1, in set, 0, null);
            api.CmdBindVertexBuffers(cmd, 0, 1, in buf, in offset);
        }
        if (scene.MenusOverWalls) sheets = world;   // the frost untested: nothing hides the menus over it
        else if (scene.PanelOverWalls) sheets = panelFrom;   // nor a confirmation's panel
        sheets = Math.Min(sheets, drawn);
        world = Math.Min(world, sheets);
        tested = Math.Min(tested, world);
        int first = 0;
        if (sheets > 0 && depth != null && depth.Width == target.Width && depth.Height == target.Height)
        {
            depth.Transition(cmd, ImageLayout.DepthAttachmentOptimal);
            var datt = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo, ImageView = depth.View, ImageLayout = ImageLayout.DepthAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Load, StoreOp = AttachmentStoreOp.Store,
            };
            var wri = ri with { PDepthAttachment = &datt };
            Begin(_worldTested, wri);
            if (tested > 0) api.CmdDraw(cmd, (uint)tested, 1, 0, 0);
            if (world > tested)
            {
                api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _worldOver);
                api.CmdDraw(cmd, (uint)(world - tested), 1, (uint)tested, 0);
            }
            if (sheets > world)
            {
                api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _sheetTested);
                api.CmdDraw(cmd, (uint)(sheets - world), 1, (uint)world, 0);
            }
            api.CmdEndRendering(cmd);
            first = sheets;
        }
        if (drawn > first)
        {
            Begin(_pipeline, ri);
            api.CmdDraw(cmd, (uint)(drawn - first), 1, (uint)first, 0);
            api.CmdEndRendering(cmd);
        }
        target.Transition(cmd, ImageLayout.TransferSrcOptimal);
    }

    /// <summary>Pictures the set binds that nothing has written yet (the preview's stand-in, a sheet never shown) cleared
    /// to nothing and made shader-readable: every binding counts as used by the overlay's draws, and one left undefined
    /// is an error (the validation layer: VUID-vkCmdDraw-None-09600, 2026-10-29).</summary>
    void ClearUnwritten(CommandBuffer cmd)
    {
        foreach (var img in (ReadOnlySpan<GpuImage>)[_noPreview, _panel.Image, _labels.Image, _feed.Image, _shown.Image, _page.Image, _reader.Image, _boards.Image])
        {
            if (img.Layout != ImageLayout.Undefined) continue;
            img.Transition(cmd, ImageLayout.TransferDstOptimal);
            var none = new ClearColorValue(0f, 0f, 0f, 0f);
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            _vk.Api.CmdClearColorImage(cmd, img.Handle, ImageLayout.TransferDstOptimal, in none, 1, in range);
            img.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        }
    }

    /// <summary>How far behind its panel the preview is tested and written (metres along the view): the hands hold its
    /// corners and sit a little in front of or behind the plane, and are drawn over it either way, as fingers over a
    /// phone's screen.</summary>
    const float PreviewBehind = 0.1f;

    /// <summary>The preview's quad at the panel's corners (style 29), tested and written <see cref="PreviewBehind"/>
    /// back. Its corners as projected at the plane (for the hands' mask), or null when one is behind the eye.</summary>
    Vector4[]? PreviewQuad(List<MeshVertex> verts, CameraPreview p, Matrix4x4 viewProj)
    {
        if (p.Corners is not { Length: 4 } k) return null;
        var q = new Vector4[4];
        Span<Vector4> back = stackalloc Vector4[4];
        for (int i = 0; i < 4; i++)
        {
            if (!ProjectZW(k[i], viewProj, out q[i])) return null;
            // reverse-Z with an infinite far plane: depth = near / distance along the view (clip w)
            back[i] = q[i] with { Z = q[i].Z * q[i].W / (q[i].W + PreviewBehind) };
        }
        QuadZW(verts, back[0], back[1], back[2], back[3], Vector4.One, 29, new(0, 0), new(1, 0), new(1, 1), new(0, 1));
        return q;
    }

    /// <summary>A picture's quad (and its frost first, if it has any): flat at a pixel, or at corners in the world.</summary>
    readonly List<MeshVertex> _verts = new();

    void Sheet(FrameResources fr, CommandBuffer cmd, GpuImage target, List<MeshVertex> verts, OverlayImage img, Vector2 at, Vector3[]? corners,
        bool content, Matrix4x4 viewProj, int frostStyle, int style)
    {
        float w = target.Width, h = target.Height;
        (Vector4, Vector4, Vector4, Vector4) q = default;   // NDC xyz (reverse-Z depth; the near plane when flat), clip w
        // placed in the world: a grid at the depth the compositor tests it at (Overlay.Walls.cs)
        bool placed = corners is { Length: 4 };
        if (placed)
        {
            if (!ProjectPlaced(corners!, viewProj)) return;
        }
        else
        {
            var a = new Vector2(at.X / w * 2 - 1, at.Y / h * 2 - 1);
            var b = new Vector2((at.X + img.Width) / w * 2 - 1, (at.Y + img.Height) / h * 2 - 1);
            q = (new(a.X, a.Y, 1, 1), new(b.X, a.Y, 1, 1), new(b.X, b.Y, 1, 1), new(a.X, b.Y, 1, 1));
        }
        if (img.Frost > 0)
        {
            NeedBackdrop(fr, target);
            // the blur reaches about as far at any resolution: deeper mips on bigger images
            float lod = Math.Clamp(img.Frost * (2.5f + MathF.Max(0, MathF.Log2(h / 1080f))), 0, Math.Max(0, _backdrop.Mips - 3));   // the shader also reads two mips deeper
            var frost = new Vector4(_cutThisEye ? -w : w, h, lod, img.Edge);
            if (placed) PlacedQuads(verts, frost, frostStyle, new(0, 0), new(1, 1));
            else QuadZW(verts, q.Item1, q.Item2, q.Item3, q.Item4, frost, frostStyle, new(0, 0), new(1, 0), new(1, 1), new(0, 1));
        }
        if (content && placed) PlacedQuads(verts, Vector4.One, style, new(0, 0), new(1, 1));
        else if (content) QuadZW(verts, q.Item1, q.Item2, q.Item3, q.Item4, Vector4.One, style, new(0, 0), new(1, 0), new(1, 1), new(0, 1));
    }

    /// <summary>An element's quad from the shown atlas (and its frost first): flat, at corners, or upright like a tag.</summary>
    void Card(FrameResources fr, CommandBuffer cmd, GpuImage target, List<MeshVertex> verts, OverlayCard card, Vector2 atlas, ViewSetup view, Matrix4x4 viewProj,
        int style = 12, int frostStyle = 13)
    {
        float w = target.Width, h = target.Height;
        Vector4 a = default, b = default, c = default, d = default;   // NDC xyz (reverse-Z depth; the near plane unless placed at corners in the world), clip w
        static Vector4 Near(Vector3 xyw) => new(xyw.X, xyw.Y, 1, xyw.Z);
        bool placed = false;   // at corners in the world: a grid at the depth the compositor tests it at (Overlay.Walls.cs)
        if (card.Screen is { } at)
        {
            a = new Vector4(at.X / w * 2 - 1, at.Y / h * 2 - 1, 1, 1);
            c = new Vector4((at.X + card.Uv.Z) / w * 2 - 1, (at.Y + card.Uv.W) / h * 2 - 1, 1, 1);
            (b, d) = (new(c.X, a.Y, 1, 1), new(a.X, c.Y, 1, 1));
        }
        else if (card.Corners is { Length: 4 } k)
        {
            if (!ProjectPlaced(k, viewProj)) return;
            placed = true;
        }
        else
        {
            var bottom = card.At;
            if (card.Avatar != UUID.Zero)
            {
                if (_heads == null || !_heads.TryGetValue(card.Avatar, out var head)) return;
                bottom = head;
            }
            bottom += new Vector3(0, 0, card.Lift);
            float mpp = card.MetresPerPx, qw = card.Uv.Z * mpp, qh = card.Uv.W * mpp;
            var toCard = bottom - view.Position;
            var right = Vector3.Cross(toCard, Vector3.UnitZ);
            if (right.LengthSquared() < 1e-8f || Vector3.Dot(toCard, -new Vector3(view.View.M13, view.View.M23, view.View.M33)) <= 0.05f) return;
            right = Vector3.Normalize(right);
            Vector3 P(float px, float py) => bottom + right * (px * mpp - qw / 2) + Vector3.UnitZ * (qh - py * mpp);
            // upright over a thing or a person: shown through what's in front, as name tags are
            if (!ProjectW(P(0, 0), viewProj, out var pa) || !ProjectW(P(card.Uv.Z, 0), viewProj, out var pb) ||
                !ProjectW(P(card.Uv.Z, card.Uv.W), viewProj, out var pc) || !ProjectW(P(0, card.Uv.W), viewProj, out var pd)) return;
            (a, b, c, d) = (Near(pa), Near(pb), Near(pc), Near(pd));
        }
        var uv0 = new Vector2(card.Uv.X / atlas.X, card.Uv.Y / atlas.Y);
        var uv1 = new Vector2((card.Uv.X + card.Uv.Z) / atlas.X, (card.Uv.Y + card.Uv.W) / atlas.Y);
        Vector2 ub = new(uv1.X, uv0.Y), ud = new(uv0.X, uv1.Y);
        if (card.Frost > 0)
        {
            NeedBackdrop(fr, target);
            float lod = Math.Clamp(card.Frost * (2.5f + MathF.Max(0, MathF.Log2(h / 1080f))), 0, Math.Max(0, _backdrop.Mips - 3));
            var frost = new Vector4(_cutThisEye ? -w : w, h, lod, card.Edge);
            if (placed) PlacedQuads(verts, frost, frostStyle, uv0, uv1);
            else QuadZW(verts, a, b, c, d, frost, frostStyle, uv0, ub, uv1, ud);
        }
        if (card.Content && placed) PlacedQuads(verts, new Vector4(1, 1, 1, card.Alpha), style, uv0, uv1);
        else if (card.Content) QuadZW(verts, a, b, c, d, new Vector4(1, 1, 1, card.Alpha), style, uv0, ub, uv1, ud);
    }

    void Upload(FrameResources fr, int slot, CommandBuffer cmd, Tex tex, OverlayImage? img)
    {
        if (img == null || img.Version == tex.Version || img.Width <= 0 || img.Height <= 0) return;
        var api = _vk.Api;
        if (tex.Image.Width != img.Width || tex.Image.Height != img.Height)
        {
            fr.Retire.Add(tex.Image.Dispose);   // the frame before may still sample it: one queue, so gone once this frame is done
            tex.Image = NewPicture((uint)img.Width, (uint)img.Height, tex.Format);
        }
        ulong bytes = (ulong)img.Rgba.Length;
        if (tex.Staging[slot] is not { } st || st.Size < bytes)
        {
            if (tex.Staging[slot] is { } oldSt) fr.Retire.Add(oldSt.Dispose);
            tex.Staging[slot] = st = new GpuBuffer(_vk, Math.Max(bytes, 1UL << 20), BufferUsageFlags.TransferSrcBit, MemoryUse.Upload);
        }
        img.Rgba.AsSpan().CopyTo(st.Mapped);
        tex.Image.Transition(cmd, ImageLayout.TransferDstOptimal);
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D((uint)img.Width, (uint)img.Height, 1),
        };
        api.CmdCopyBufferToImage(cmd, st.Handle, tex.Image.Handle, ImageLayout.TransferDstOptimal, 1, in copy);
        tex.Image.Transition(cmd, ImageLayout.ShaderReadOnlyOptimal);
        tex.Version = img.Version;
    }

    static void Quad(List<MeshVertex> v, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector4 color, int style,
        Vector2? uvA = null, Vector2? uvB = null, Vector2? uvC = null, Vector2? uvD = null)
    {
        MeshVertex V(Vector2 p, Vector2 uv) => new()
        {
            Position = new Vector3(p, 0), Normal = new Vector3(color.X, color.Y, color.Z), Tangent = new Vector4(color.W, style, 0, 0), Uv = uv,
        };
        var (ua, ub, uc, ud) = (uvA ?? new(0, 0), uvB ?? new(1, 0), uvC ?? new(1, 1), uvD ?? new(0, 1));
        v.Add(V(a, ua)); v.Add(V(b, ub)); v.Add(V(c, uc));
        v.Add(V(a, ua)); v.Add(V(c, uc)); v.Add(V(d, ud));
    }

    /// <summary>A world point to the eye's normalised device coordinates with its (reverse-Z) depth.</summary>
    static bool ProjectZ(Vector3 p, Matrix4x4 viewProj, out Vector3 ndc)
    {
        var c = Vector4.Transform(new Vector4(p, 1), viewProj);
        ndc = default;
        if (c.W < 0.02f) return false;
        ndc = new Vector3(c.X / c.W, c.Y / c.W, c.Z / c.W);
        return true;
    }

    /// <summary>A quad with each corner's depth (world labels).</summary>
    static void Quad(List<MeshVertex> v, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector4 color, int style,
        Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD)
    {
        int at = v.Count;
        Quad(v, new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), new Vector2(c.X, c.Y), new Vector2(d.X, d.Y), color, style, uvA, uvB, uvC, uvD);
        ReadOnlySpan<float> z = [a.Z, b.Z, c.Z, a.Z, c.Z, d.Z];
        for (int i = 0; i < 6; i++)
        {
            var m = v[at + i];
            m.Position.Z = z[i];
            v[at + i] = m;
        }
    }

    /// <summary>A world point to the eye's normalised device coordinates (x, y) and its clip w (z), for QuadW.</summary>
    static bool ProjectW(Vector3 p, Matrix4x4 viewProj, out Vector3 ndcW)
    {
        var c = Vector4.Transform(new Vector4(p, 1), viewProj);
        ndcW = default;
        if (c.W < 0.02f) return false;
        ndcW = new Vector3(c.X / c.W, c.Y / c.W, c.W);
        return true;
    }

    /// <summary>A quad whose corners carry their clip w (z of each point; overlay.vert multiplies it back), so the
    /// picture is interpolated in perspective: one quad between corners divided by w is affine, and bends text across
    /// a sheet seen at an angle unevenly, the bend moving with the head (as labels did, Luane's World, 2026-10-27).</summary>
    static void QuadW(List<MeshVertex> v, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector4 color, int style,
        Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD)
    {
        int at = v.Count;
        Quad(v, new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), new Vector2(c.X, c.Y), new Vector2(d.X, d.Y), color, style, uvA, uvB, uvC, uvD);
        ReadOnlySpan<float> ws = [a.Z, b.Z, c.Z, a.Z, c.Z, d.Z];
        for (int i = 0; i < 6; i++)
        {
            var m = v[at + i];
            m.Tangent.Z = ws[i];
            v[at + i] = m;
        }
    }

    /// <summary>A world point to the eye's normalised device coordinates with its (reverse-Z) depth, and its clip w.</summary>
    static bool ProjectZW(Vector3 p, Matrix4x4 viewProj, out Vector4 ndcZW)
    {
        var c = Vector4.Transform(new Vector4(p, 1), viewProj);
        ndcZW = default;
        if (c.W < 0.02f) return false;
        ndcZW = new Vector4(c.X / c.W, c.Y / c.W, c.Z / c.W, c.W);
        return true;
    }

    /// <summary>A world label's quad: each corner's depth and its clip w (as <see cref="QuadW"/>), so both the depth and
    /// the picture are interpolated as the plane's.</summary>
    static void QuadZW(List<MeshVertex> v, Vector4 a, Vector4 b, Vector4 c, Vector4 d, Vector4 color, int style,
        Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD)
    {
        int at = v.Count;
        Quad(v, new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z), new Vector3(c.X, c.Y, c.Z), new Vector3(d.X, d.Y, d.Z), color, style, uvA, uvB, uvC, uvD);
        ReadOnlySpan<float> ws = [a.W, b.W, c.W, a.W, c.W, d.W];
        for (int i = 0; i < 6; i++)
        {
            var m = v[at + i];
            m.Tangent.Z = ws[i];
            v[at + i] = m;
        }
    }

    /// <summary>A tag: upright (the region's up), turned to the head (facing: both eyes see one plane, so it stands at
    /// one depth), its bottom centre above the head; its marks. Corners carry their depth.</summary>
    static void Tag(List<MeshVertex> v, OverlayTag t, Vector3 head, Vector2 atlas, ViewSetup view, Matrix4x4 viewProj, Vector3 facing)
    {
        float mpp = t.MetresPerPx, w = t.Uv.Z * mpp, h = t.Uv.W * mpp;
        var bottom = head + new Vector3(0, 0, t.Lift);
        var toTag = bottom - view.Position;
        var right = Vector3.Cross(bottom - facing, Vector3.UnitZ);
        if (right.LengthSquared() < 1e-8f || Vector3.Dot(toTag, -new Vector3(view.View.M13, view.View.M23, view.View.M33)) <= 0.05f) return;
        right = Vector3.Normalize(right);
        var up = Vector3.UnitZ;
        Vector3 At(float px, float py) => bottom + right * (px * mpp - w / 2) + up * (h - py * mpp);   // a pixel of the picture
        // One quad whose corners carry their depth and clip w, so the picture is interpolated in perspective (an affine
        // quad bent the text unevenly, the bend moving with the head: "like water", Luane's World, 2026-10-27). A grid of
        // ~16-pixel cells did it first, at 600-1800 vertices a tag: a few tags filled the overlay's vertex buffer and the
        // rest were cut off mid-tag, their bottom rows missing in steps (the session of 2026-10-28).
        if (!ProjectZW(At(0, 0), viewProj, out var a) || !ProjectZW(At(t.Uv.Z, 0), viewProj, out var b) ||
            !ProjectZW(At(t.Uv.Z, t.Uv.W), viewProj, out var c) || !ProjectZW(At(0, t.Uv.W), viewProj, out var d)) return;
        var uv0 = new Vector2(t.Uv.X / atlas.X, t.Uv.Y / atlas.Y);
        var uv1 = new Vector2((t.Uv.X + t.Uv.Z) / atlas.X, (t.Uv.Y + t.Uv.W) / atlas.Y);
        QuadZW(v, a, b, c, d, new Vector4(1, 1, 1, t.Alpha), 9, uv0, new(uv1.X, uv0.Y), uv1, new(uv0.X, uv1.Y));
        float lineH = Math.Max(1, t.LineY) * mpp;
        if (t.Speaking >= 0)
        {
            // a light just left of the name, bigger when louder
            float lv = Math.Clamp(t.Speaking, 0, 1);
            float r = lineH * (0.42f + 0.3f * lv);
            var at = At(-r * 1.25f / mpp, t.LineY);
            Facing(v, at, r * 1.7f, t.SpeakingColor with { W = t.SpeakingColor.W * t.Alpha * (0.3f + 0.3f * lv) }, 3, view, viewProj, true);
            Facing(v, at, r, t.SpeakingColor with { W = t.SpeakingColor.W * t.Alpha }, 5, view, viewProj, true);
        }
        if (t.Typing >= 0)
            for (int i = 0; i < 3; i++)
            {
                float k = 0.5f + 0.5f * MathF.Cos((t.Typing - i / 6f) * MathF.Tau);
                Facing(v, At(t.Dots.X + i * t.Dots.Y, t.LineY), t.Dots.Z * mpp / 0.92f,
                    t.TypingColor with { W = t.TypingColor.W * t.Alpha * (0.3f + 0.7f * k) }, 5, view, viewProj, true);
            }
    }

    static void Beam(List<MeshVertex> v, OverlayBeam b, ViewSetup view, Matrix4x4 viewProj)
    {
        // keep the part in front of the eye: walk the near end forward if it's behind
        var from = b.From;
        var dir = b.To - b.From;
        if (dir.LengthSquared() < 1e-8f) return;
        var side = Vector3.Cross(dir, view.Position - from);
        if (side.LengthSquared() < 1e-10f) return;
        side = Vector3.Normalize(side) * (b.Width / 2);
        // with their depth: the hands' mask tests held beams against the menus (Overlay.Hands.cs)
        Vector3 a0, a1, c0, c1;
        if (!ProjectZ(b.To + side, viewProj, out c0) || !ProjectZ(b.To - side, viewProj, out c1)) return;
        for (int i = 0; i < 8 && !(ProjectZ(from + side, viewProj, out a0) & ProjectZ(from - side, viewProj, out a1)); i++)
            from += dir * 0.125f;
        if (!ProjectZ(from + side, viewProj, out a0) || !ProjectZ(from - side, viewProj, out a1)) return;
        Quad(v, a0, c0, c1, a1, b.Color, 2, new(0, 0), new(1, 0), new(1, 1), new(0, 1));
    }

    static void Dot(List<MeshVertex> v, OverlayDot d, ViewSetup view, Matrix4x4 viewProj) => Facing(v, d.At, d.Radius, d.Color, 3, view, viewProj, true);

    /// <summary>A quad facing the eye, centred on a point (overlay.frag draws the shape by style).</summary>
    static void Facing(List<MeshVertex> v, Vector3 at, float radius, Vector4 color, int style, ViewSetup view, Matrix4x4 viewProj, bool depth = false)
    {
        var right = new Vector3(view.View.M11, view.View.M21, view.View.M31) * radius;
        var up = new Vector3(view.View.M12, view.View.M22, view.View.M32) * radius;
        if (!ProjectZ(at - right + up, viewProj, out Vector3 a) || !ProjectZ(at + right + up, viewProj, out Vector3 b) ||
            !ProjectZ(at + right - up, viewProj, out Vector3 c) || !ProjectZ(at - right - up, viewProj, out Vector3 e)) return;
        if (depth) Quad(v, a, b, c, e, color, style, new(0, 0), new(1, 0), new(1, 1), new(0, 1));
        else Quad(v, new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), new Vector2(c.X, c.Y), new Vector2(e.X, e.Y), color, style);
    }

    /// <summary>The level ring behind, the disc (its edge at 92% of the quad, for the antialiasing), the glyph on it.</summary>
    static void Glyph(List<MeshVertex> v, OverlayGlyph g, ViewSetup view, Matrix4x4 viewProj)
    {
        float level = Math.Clamp(g.Level, 0, 1);
        if (level > 0.02f)
            Facing(v, g.At, g.Radius * (1.18f + 0.3f * level), g.Disc with { W = g.Disc.W * (0.25f + 0.45f * level) }, 8, view, viewProj, true);
        Facing(v, g.At, g.Radius / 0.92f, g.Disc, 5, view, viewProj, true);
        Facing(v, g.At, g.Radius * 0.74f, g.Ink, g.Kind switch { OverlayGlyphKind.MicMuted => 7, OverlayGlyphKind.Hud => 14, OverlayGlyphKind.Stand => 20, OverlayGlyphKind.Bell => 21, OverlayGlyphKind.Private => 22, OverlayGlyphKind.PrivateSees => 23, OverlayGlyphKind.Camera => 28, _ => 6 }, view, viewProj, true);
    }

    public void Dispose()
    {
        var api = _vk.Api;
        foreach (var t in new[] { _panel, _labels, _feed, _shown, _page, _reader, _boards })
        {
            t.Image.Dispose();
            foreach (var s in t.Staging) s?.Dispose();
        }
        _noPreview.Dispose();
        _backdrop.Dispose();
        DisposeSamples();
        DisposeCut();
        DisposeWalls();
        foreach (var b in _vertices) b.Dispose();
        api.DestroyPipeline(_vk.Device, _pipeline, null);
        api.DestroyPipeline(_vk.Device, _worldTested, null);
        api.DestroyPipeline(_vk.Device, _worldOver, null);
        api.DestroyPipeline(_vk.Device, _sheetTested, null);
        api.DestroyPipelineLayout(_vk.Device, _layout, null);
        api.DestroySampler(_vk.Device, _sampler, null);
        api.DestroySampler(_vk.Device, _blurSampler, null);
        api.DestroyDescriptorPool(_vk.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_vk.Device, _setLayout, null);
    }
}
