using System.Collections.Concurrent;
using LibreMetaverse;
using Silk.NET.Vulkan;
using Sojourn.Render.Gpu;
using Sojourn.Render.World;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Sojourn.Render.Media;

/// <summary>
/// A web page or video drawn on faces: its section (written by a helper), the texture the faces show and how big
/// the renderer has seen it. Made and changed by the client's media code; drawn by the renderer.
///
/// The texture is laid out as the viewer's (LLViewerMediaTexture): power-of-two sides with the content in the corner
/// at texture coordinate (0,0) (the page's bottom row on v = 0, its left edge on u = 0), the rest white, so faces
/// whose repeats and offsets were set up for the viewer show the same part of the page. Content size is the media's
/// (the face's width_pixels/height_pixels, the parcel's media size, else 1024x1024); <see cref="Scale"/> (0.25-2, a
/// power of two) draws all of it at more or fewer texels for how big it is on screen. The helper's frames (a page
/// at that scale, a video at its own size) are blitted into the corner, scaled and flipped on the GPU.
/// </summary>
public sealed class MediaSurface
{
    static int _next;
    public int Id { get; } = Interlocked.Increment(ref _next);
    public MediaSection Section { get; }
    public string Name { get; }

    /// <summary>The media's size in its own pixels (a page's CSS pixels): the content's corner of the texture.</summary>
    public volatile int ContentWidth, ContentHeight;
    /// <summary>Texels per content pixel: 0.25, 0.5, 1 or 2.</summary>
    public volatile float Scale = 1f;
    /// <summary>Frames aren't copied while paused (out of view): the faces keep the last one.</summary>
    public volatile bool Paused;
    /// <summary>At most one frame copied in this many ms (0: every frame the helper sends): a video far off or small
    /// doesn't need its 30 fps uploaded.</summary>
    public volatile int CopyEveryMs;
    internal long LastCopyAt;

    /// <summary>Set while the surface is shown up close in the headset (the page board): the render thread keeps
    /// <see cref="View"/>, the newest frame as it came (top row first, unscaled) in R8G8B8A8, for an OpenXR quad layer to
    /// copy bit for bit. Render thread: null until the first frame is in it.</summary>
    public volatile bool WantView;
    public GpuImage? View { get; internal set; }

    /// <summary>Set while the surface is shown up close on the desktop: the upload worker keeps a copy of the newest frame
    /// in memory (BGRA, top row first, <see cref="PixelsWidth"/> x <see cref="PixelsHeight"/>), its version bumped at each
    /// change. Read it under <see cref="PixelsLock"/>.</summary>
    public volatile bool WantPixels;
    /// <summary>Others reading the copy for a while (the agent API's picture of a page), counted.</summary>
    public int PixelsReaders;
    internal bool WantsPixels => WantPixels || Volatile.Read(ref PixelsReaders) > 0;
    public readonly object PixelsLock = new();
    public byte[]? Pixels;
    public int PixelsWidth, PixelsHeight;
    public long PixelsVersion;
    internal bool PixelsWhole = true;

    /// <summary>What faces sample once the first frame is in (state Ready); Loading until then.</summary>
    public SceneTexture Texture { get; } = new(UUID.Random()) { State = TexState.Loading, Components = 4 };

    // interest, as the renderer saw it in its last frame: the largest screen area (pixels) of an object showing it,
    // and the nearest such object's distance (metres)
    public float Interest { get; internal set; }
    public float Distance { get; internal set; } = float.PositiveInfinity;
    /// <summary>Stopwatch time it was last drawn (0: never).</summary>
    public long SeenAt { get; internal set; }
    internal float AreaNow, DistNow = float.PositiveInfinity;

    // counters
    public long FramesShown, BytesCopied;
    internal volatile bool Disposed;

    // ---- worker <-> render thread ----
    internal sealed class Slot
    {
        public GpuBuffer? Buffer;
        public int W, H;                 // the frame size the buffer holds
        public volatile int State;       // 0 free, 1 filled (waiting for the render thread), 2 in flight on the GPU
        public int X, Y, RW, RH;         // the rows and columns that changed
        public long Order;
    }
    internal readonly Slot[] Slots = { new(), new() };
    internal volatile int WantW, WantH;   // a frame size the slots don't hold yet (the render thread makes them)
    internal volatile bool NeedWhole;     // fresh staging: the next copy takes the whole frame, whatever changed
    internal long FilledOrder;
    // render thread only
    internal GpuImage? Tex, Frame;
    internal int TexW, TexH;
    internal float TexScale;
    internal bool FrameValid, NeedBlit;

    public MediaSurface(string name, uint maxWidth, uint maxHeight, int contentWidth, int contentHeight)
    {
        Name = name;
        Section = new MediaSection(name, maxWidth, maxHeight);
        ContentWidth = contentWidth;
        ContentHeight = contentHeight;
    }

    public static int NextPow2(int v)
    {
        int p = 1;
        while (p < v && p < 8192) p <<= 1;
        return p;
    }

    /// <summary>Where a point on the face's texture (the viewer's u, v) falls on the content, in content pixels from
    /// the top left (LLViewerMediaImpl::scaleTextureCoords); null outside the content.</summary>
    public (float X, float Y)? ContentAt(float u, float v)
    {
        int cw = ContentWidth, ch = ContentHeight;
        int tw = NextPow2(cw), th = NextPow2(ch);
        u -= MathF.Floor(u);
        v -= MathF.Floor(v);
        float x = u * tw, y = ch - v * th;
        if (x < 0 || y < 0 || x > cw || y > ch) return null;
        return (x, y);
    }
}

/// <summary>
/// Every media surface the renderer knows: which faces show which (prim media by object and face, parcel media by the
/// texture it replaces), the upload worker that copies frames out of the sections, and the render thread's part:
/// textures made and updated in place, a frame's interest gathered. Faces resolve through <see cref="Lookup"/>.
/// </summary>
public sealed class MediaSurfaces : IDisposable
{
    readonly ConcurrentDictionary<(ulong Region, uint LocalId), MediaSurface?[]> _faces = new();
    readonly ConcurrentDictionary<UUID, MediaSurface> _textures = new();
    readonly ConcurrentDictionary<int, MediaSurface> _all = new();
    readonly ConcurrentQueue<(ulong Region, uint LocalId)> _invalidObjects = new();
    readonly ConcurrentQueue<UUID> _invalidTextures = new();
    readonly ConcurrentQueue<MediaSurface> _dead = new();
    Thread? _worker;
    volatile bool _stop;
    readonly AutoResetEvent _wake = new(false);
    public Action<string>? Log { get; set; }

    /// <summary>Anything to look up at all (a quick test for every face resolved).</summary>
    public bool Any => !_faces.IsEmpty || !_textures.IsEmpty;
    public int Count => _all.Count;
    public IEnumerable<MediaSurface> All => _all.Values;

    // ---- the client's side ----

    public void Add(MediaSurface s)
    {
        _all[s.Id] = s;
        StartWorker();
    }

    /// <summary>The surface drawn on that face of that prim (null: none). Prim media wins over parcel media.</summary>
    public void SetFace(ulong region, uint localId, int face, MediaSurface? s)
    {
        if (face < 0 || face >= 64) return;
        var arr = _faces.GetOrAdd((region, localId), _ => new MediaSurface?[Math.Max(8, face + 1)]);
        if (face >= arr.Length)
        {
            var bigger = new MediaSurface?[face + 1];
            Array.Copy(arr, bigger, arr.Length);
            _faces[(region, localId)] = arr = bigger;
        }
        if (ReferenceEquals(arr[face], s)) return;
        arr[face] = s;
        if (s == null && arr.All(x => x == null)) _faces.TryRemove((region, localId), out _);
        _invalidObjects.Enqueue((region, localId));
    }

    /// <summary>Every face showing this texture shows the surface instead (parcel media); null puts the texture back.</summary>
    public void SetTexture(UUID texture, MediaSurface? s)
    {
        if (texture == UUID.Zero) return;
        if (s == null) { if (!_textures.TryRemove(texture, out _)) return; }
        else if (_textures.TryGetValue(texture, out var had) && ReferenceEquals(had, s)) return;
        else _textures[texture] = s;
        _invalidTextures.Enqueue(texture);
    }

    /// <summary>Takes it off every face and frees it once no frame in flight uses it.</summary>
    public void Remove(MediaSurface s)
    {
        foreach (var kv in _faces)
            for (int i = 0; i < kv.Value.Length; i++)
                if (ReferenceEquals(kv.Value[i], s)) SetFace(kv.Key.Region, kv.Key.LocalId, i, null);
        foreach (var kv in _textures)
            if (ReferenceEquals(kv.Value, s)) SetTexture(kv.Key, null);
        if (_all.TryRemove(s.Id, out _))
        {
            s.Disposed = true;
            _dead.Enqueue(s);
        }
    }

    // ---- the renderer's side ----

    internal MediaSurface? Lookup(ulong region, uint localId, int face, UUID texture)
    {
        if (localId != 0 && _faces.TryGetValue((region, localId), out var arr) && face >= 0 && face < arr.Length && arr[face] is { } s)
            return s;
        return texture != UUID.Zero && _textures.TryGetValue(texture, out var t) ? t : null;
    }

    internal static void Seen(MediaSurface s, float area, float distance)
    {
        if (area > s.AreaNow) s.AreaNow = area;
        if (distance < s.DistNow) s.DistNow = distance;
    }

    /// <summary>Objects and textures whose faces must be resolved again (a surface came or went).</summary>
    internal bool TakeInvalid(out (ulong Region, uint LocalId) obj) => _invalidObjects.TryDequeue(out obj);
    internal bool TakeInvalid(out UUID texture) => _invalidTextures.TryDequeue(out texture);

    /// <summary>Render thread, at the start of a frame: the last frame's interest published, new frames recorded into
    /// the textures (copies, the blit into the content's corner, mips), the dead freed later.</summary>
    internal void BeginFrame(RenderWorld world)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        while (_dead.TryDequeue(out var d)) Free(world, d);
        foreach (var s in _all.Values)
        {
            s.Interest = s.AreaNow;
            s.Distance = s.DistNow;
            if (s.AreaNow > 0) s.SeenAt = now;
            s.AreaNow = 0;
            s.DistNow = float.PositiveInfinity;
            try { Update(world, s); }
            catch (Exception e) { Log?.Invoke($"media {s.Name}: {e.Message}"); }
        }
        _wake.Set();
    }

    unsafe void Update(RenderWorld world, MediaSurface s)
    {
        var vk = world.Gpu.Vk;
        var bindless = world.Gpu.Bindless;
        var up = world.Gpu.Uploader;

        // the frame image and its staging buffers, at the size the helper sends
        int ww = s.WantW, wh = s.WantH;
        if (ww > 0 && wh > 0 && (s.Frame == null || s.Frame.Width != ww || s.Frame.Height != wh))
        {
            if (s.Slots.Any(x => x.State == 2)) return;   // wait for the GPU to finish with the old ones
            RetireFrame(world, s);
            s.Frame = new GpuImage(vk, (uint)ww, (uint)wh, Format.B8G8R8A8Unorm, ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit);
            foreach (var slot in s.Slots)
            {
                slot.Buffer = new GpuBuffer(vk, (ulong)ww * (ulong)wh * 4, BufferUsageFlags.TransferSrcBit, MemoryUse.Upload);
                slot.W = ww;
                slot.H = wh;
                slot.State = 0;
            }
            s.FrameValid = false;
            s.NeedWhole = true;
        }

        // the texture, at the content's size and scale
        int cw = Math.Max(1, s.ContentWidth), ch = Math.Max(1, s.ContentHeight);
        float scale = s.Scale;
        int tw = Math.Clamp((int)(MediaSurface.NextPow2(cw) * scale), 16, 4096);
        int th = Math.Clamp((int)(MediaSurface.NextPow2(ch) * scale), 16, 4096);
        if (s.Tex == null || s.TexW != tw || s.TexH != th)
        {
            if (s.Tex != null)
            {
                var old = s.Tex;
                int oldSlot = s.Texture.Slot;
                world.RetireLater(() => { old.Dispose(); if (oldSlot >= 0) bindless.FreeTexture(oldSlot); });
            }
            uint mips = (uint)Math.Floor(Math.Log2(Math.Max(tw, th))) + 1;
            var tex = new GpuImage(vk, (uint)tw, (uint)th, Format.B8G8R8A8Unorm,
                ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit, mips);
            up.Record(cmd =>
            {
                GpuImage.Barrier(vk, cmd, tex.Handle, tex.Aspect, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, tex.Mips, 0, 1);
                var white = new ClearColorValue(1f, 1f, 1f, 1f);
                var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, tex.Mips, 0, 1);
                vk.Api.CmdClearColorImage(cmd, tex.Handle, ImageLayout.TransferDstOptimal, &white, 1, &range);
                GpuImage.Barrier(vk, cmd, tex.Handle, tex.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, 0, tex.Mips, 0, 1);
            });
            tex.Layout = ImageLayout.ShaderReadOnlyOptimal;
            int slot = bindless.AllocTexture();
            bindless.Write(slot, tex.View, bindless.Repeat);
            s.Tex = tex;
            s.TexW = tw;
            s.TexH = th;
            s.TexScale = scale;
            SetTexture(s.Texture, slot, tw, th, ready: s.FrameValid);
            s.NeedBlit = s.FrameValid;
        }

        // new frames, oldest first: their changed rows into the frame image
        var frame = s.Frame;
        bool copied = false;
        if (frame != null)
        {
            var (a, b) = (s.Slots[0], s.Slots[1]);
            foreach (var slot in a.Order <= b.Order ? new[] { a, b } : new[] { b, a })
            {
                if (slot.State != 1 || slot.Buffer == null || slot.W != frame.Width || slot.H != frame.Height) continue;
                var buf = slot.Buffer;
                int x = slot.X, y = slot.Y, rw = slot.RW, rh = slot.RH, fw = slot.W;
                bool first = !s.FrameValid;
                up.Record(cmd =>
                {
                    GpuImage.Barrier(vk, cmd, frame.Handle, frame.Aspect, first ? ImageLayout.Undefined : ImageLayout.TransferSrcOptimal,
                        ImageLayout.TransferDstOptimal, 0, 1, 0, 1);
                    var region = new BufferImageCopy
                    {
                        BufferOffset = ((ulong)y * (ulong)fw + (ulong)x) * 4,
                        BufferRowLength = (uint)fw,
                        BufferImageHeight = 0,
                        ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                        ImageOffset = new Offset3D(x, y, 0),
                        ImageExtent = new Extent3D((uint)rw, (uint)rh, 1),
                    };
                    vk.Api.CmdCopyBufferToImage(cmd, buf.Handle, frame.Handle, ImageLayout.TransferDstOptimal, 1, in region);
                    GpuImage.Barrier(vk, cmd, frame.Handle, frame.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, 0, 1, 0, 1);
                });
                slot.State = 2;
                var done = slot;
                world.RetireLater(() => done.State = 0);
                s.FrameValid = true;
                s.NeedBlit = true;
                s.FramesShown++;
                copied = true;
            }
        }
        UpdateView(world, s, frame, copied);

        // the frame into the content's corner of the texture (flipped: SL's v runs up the page), then its mips
        if (s.NeedBlit && frame != null && s.FrameValid && s.Tex is { } t)
        {
            int dw = Math.Clamp((int)MathF.Round(cw * s.TexScale), 1, (int)t.Width), dh = Math.Clamp((int)MathF.Round(ch * s.TexScale), 1, (int)t.Height);
            int fw = (int)frame.Width, fh = (int)frame.Height;
            up.Record(cmd =>
            {
                GpuImage.Barrier(vk, cmd, t.Handle, t.Aspect, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferDstOptimal, 0, t.Mips, 0, 1);
                var blit = new ImageBlit
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                };
                blit.SrcOffsets[0] = new Offset3D(0, 0, 0);
                blit.SrcOffsets[1] = new Offset3D(fw, fh, 1);
                blit.DstOffsets[0] = new Offset3D(0, dh, 0);
                blit.DstOffsets[1] = new Offset3D(dw, 0, 1);
                vk.Api.CmdBlitImage(cmd, frame.Handle, ImageLayout.TransferSrcOptimal, t.Handle, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Linear);
                int mw = (int)t.Width, mh = (int)t.Height;
                for (uint m = 1; m < t.Mips; m++)
                {
                    GpuImage.Barrier(vk, cmd, t.Handle, t.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, m - 1, 1, 0, 1);
                    int nw = Math.Max(1, mw / 2), nh = Math.Max(1, mh / 2);
                    var mb = new ImageBlit
                    {
                        SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m - 1, 0, 1),
                        DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, m, 0, 1),
                    };
                    mb.SrcOffsets[1] = new Offset3D(mw, mh, 1);
                    mb.DstOffsets[1] = new Offset3D(nw, nh, 1);
                    vk.Api.CmdBlitImage(cmd, t.Handle, ImageLayout.TransferSrcOptimal, t.Handle, ImageLayout.TransferDstOptimal, 1, in mb, Filter.Linear);
                    GpuImage.Barrier(vk, cmd, t.Handle, t.Aspect, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal, m - 1, 1, 0, 1);
                    mw = nw;
                    mh = nh;
                }
                GpuImage.Barrier(vk, cmd, t.Handle, t.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, t.Mips - 1, 1, 0, 1);
            });
            s.NeedBlit = false;
            if (s.Texture.State != TexState.Ready) SetTexture(s.Texture, s.Texture.Slot, s.TexW, s.TexH, ready: true);
        }
    }

    /// <summary>The headset's up-close view: the frame image copied (BGRA to RGBA, no scaling) into <see cref="MediaSurface.View"/>
    /// whenever a new frame came in, left in TransferSrcOptimal for the quad layer's copy.</summary>
    unsafe void UpdateView(RenderWorld world, MediaSurface s, GpuImage? frame, bool copied)
    {
        var vk = world.Gpu.Vk;
        if (!s.WantView || frame == null || !s.FrameValid)
        {
            if (!s.WantView && s.View is { } gone)
            {
                s.View = null;
                world.RetireLater(gone.Dispose);
            }
            return;
        }
        var view = s.View;
        bool fresh = false;
        if (view == null || view.Width != frame.Width || view.Height != frame.Height)
        {
            if (view != null) { var old = view; world.RetireLater(old.Dispose); }
            view = new GpuImage(vk, frame.Width, frame.Height, Format.R8G8B8A8Unorm, ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit);
            fresh = true;
        }
        if (!fresh && !copied) return;
        int fw = (int)frame.Width, fh = (int)frame.Height;
        var target = view;
        var from = fresh ? ImageLayout.Undefined : ImageLayout.TransferSrcOptimal;
        world.Gpu.Uploader.Record(cmd =>
        {
            GpuImage.Barrier(vk, cmd, target.Handle, target.Aspect, from, ImageLayout.TransferDstOptimal, 0, 1, 0, 1);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            };
            blit.SrcOffsets[1] = new Offset3D(fw, fh, 1);
            blit.DstOffsets[1] = new Offset3D(fw, fh, 1);
            vk.Api.CmdBlitImage(cmd, frame.Handle, ImageLayout.TransferSrcOptimal, target.Handle, ImageLayout.TransferDstOptimal, 1, in blit, Filter.Nearest);
            GpuImage.Barrier(vk, cmd, target.Handle, target.Aspect, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal, 0, 1, 0, 1);
        });
        target.Layout = ImageLayout.TransferSrcOptimal;
        s.View = target;
    }

    static void SetTexture(SceneTexture t, int slot, int w, int h, bool ready)
    {
        t.Slot = slot;
        t.Width = w;
        t.Height = h;
        t.Discard = 0;
        t.State = ready ? TexState.Ready : TexState.Loading;
    }

    static void RetireFrame(RenderWorld world, MediaSurface s)
    {
        var f = s.Frame;
        var bufs = s.Slots.Select(x => x.Buffer).ToArray();
        s.Frame = null;
        foreach (var x in s.Slots) { x.Buffer = null; x.State = 0; x.W = x.H = 0; }
        world.RetireLater(() =>
        {
            f?.Dispose();
            foreach (var b in bufs) b?.Dispose();
        });
    }

    void Free(RenderWorld world, MediaSurface s)
    {
        RetireFrame(world, s);
        var tex = s.Tex;
        var view = s.View;
        int slot = s.Texture.Slot;
        s.Tex = null;
        s.View = null;
        if (view != null) world.RetireLater(view.Dispose);
        s.Texture.State = TexState.Missing;
        s.Texture.Slot = -1;
        var bindless = world.Gpu.Bindless;
        world.RetireLater(() =>
        {
            tex?.Dispose();
            if (slot >= 0) bindless.FreeTexture(slot);
            s.Section.Dispose();
        });
    }

    // ---- the upload worker: frames out of the sections into staging buffers, off the render thread ----

    void StartWorker()
    {
        if (_worker != null) return;
        lock (this)
        {
            if (_worker != null) return;
            _worker = new Thread(Work) { Name = "media-upload", IsBackground = true, Priority = ThreadPriority.AboveNormal };
            _worker.Start();
        }
    }

    unsafe void Work()
    {
        while (!_stop)
        {
            _wake.WaitOne(4);
            foreach (var s in _all.Values)
            {
                if (s.Disposed || (s.Paused && !s.WantsPixels)) continue;
                try { Copy(s); }
                catch (Exception e) { Log?.Invoke($"media {s.Name}: copy: {e.Message}"); }
            }
        }
    }

    unsafe void Copy(MediaSurface s)
    {
        long now = System.Environment.TickCount64;
        if (s.WantsPixels && s.Pixels == null && s.Section.TryPeek(out var shown))
        {
            // a reader that starts while nothing new is coming (a still page): what's showing now, whole
            try { Mirror(s, shown, 0, 0, shown.Width, shown.Height); }
            finally { s.Section.EndPeek(); }
        }
        if (s.CopyEveryMs > 0 && now - s.LastCopyAt < s.CopyEveryMs) return;
        var free = s.Slots.FirstOrDefault(x => x.State == 0);
        if (free == null) return;
        if (!s.Section.TryTake(out var f)) return;
        s.LastCopyAt = now;
        if (free.Buffer == null || free.W != f.Width || free.H != f.Height)
        {
            // a size the staging buffers don't have: the render thread makes them, and the frame is taken again whole
            s.WantW = f.Width;
            s.WantH = f.Height;
            s.Section.Release(0);
            s.Section.Rewind();
            return;
        }
        int x = f.DirtyX, y = f.DirtyY, w = f.DirtyW, h = f.DirtyH;
        if (s.NeedWhole) { x = 0; y = 0; w = f.Width; h = f.Height; s.NeedWhole = false; }
        Mirror(s, f, x, y, w, h);
        if (w <= 0 || h <= 0) { s.Section.Release(f.Seq); return; }
        byte* dst = (byte*)free.Buffer.Alloc.Mapped;
        int row = w * 4, dstStride = f.Width * 4;
        for (int r = 0; r < h; r++)
            System.Buffer.MemoryCopy(f.Pixels + (long)(y + r) * f.Stride + x * 4, dst + (long)(y + r) * dstStride + x * 4, row, row);
        s.Section.Release(f.Seq);
        s.BytesCopied += (long)row * h;
        free.X = x;
        free.Y = y;
        free.RW = w;
        free.RH = h;
        free.Order = Interlocked.Increment(ref s.FilledOrder);
        free.State = 1;
    }

    /// <summary>The desktop's up-close copy: the rows that changed (every row when it starts or the size changes). Each
    /// buffer the helper publishes holds a whole frame, so a whole copy can come from any of them.</summary>
    static unsafe void Mirror(MediaSurface s, in MediaFrame f, int x, int y, int w, int h)
    {
        if (!s.WantsPixels)
        {
            if (s.Pixels != null) lock (s.PixelsLock) { s.Pixels = null; s.PixelsWhole = true; }
            return;
        }
        lock (s.PixelsLock)
        {
            if (s.Pixels == null || s.PixelsWidth != f.Width || s.PixelsHeight != f.Height || s.PixelsWhole)
            {
                if (s.Pixels == null || s.Pixels.Length != f.Width * f.Height * 4) s.Pixels = new byte[f.Width * f.Height * 4];
                (s.PixelsWidth, s.PixelsHeight, s.PixelsWhole) = (f.Width, f.Height, false);
                (x, y, w, h) = (0, 0, f.Width, f.Height);
            }
            if (w <= 0 || h <= 0) return;
            int row = w * 4, stride = f.Width * 4;
            fixed (byte* dst = s.Pixels)
                for (int r = 0; r < h; r++)
                    System.Buffer.MemoryCopy(f.Pixels + (long)(y + r) * f.Stride + x * 4, dst + (long)(y + r) * stride + x * 4, row, row);
            s.PixelsVersion++;
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _worker?.Join(500);
    }
}

