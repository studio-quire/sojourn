using System.Text;
using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using SkiaSharp;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Render.Realtime;
using Vec3 = System.Numerics.Vector3;

namespace Sojourn.Session.Capture;

/// <summary>
/// Pictures from cameras of their own (Render's FrameRenderer.Still.cs): bigger than the eye, up to the viewer's
/// 7680x4320; from a camera placed in the region or around the person (in front, behind, over the shoulder, above, to a
/// side); and 360s as the viewer's floater takes them (llfloater360capture.cpp: six 90-degree faces, stitched to an
/// equirectangular JPEG with the GPano metadata 360 viewers read, the interest list in 360 mode meanwhile). The view
/// itself never moves; the picture is an extra render at the person's request (a short hitch). Into Pictures\Sojourn,
/// named by the client. Claude Opus 5.5, 2026-10-29 (batch B8).
/// </summary>
public sealed class Stills
{
    readonly AgentSession _s;
    readonly FrameRenderer _renderer;
    readonly SemaphoreSlim _one = new(1, 1);

    public Stills(AgentSession s, FrameRenderer renderer) => (_s, _renderer) = (s, renderer);

    /// <summary>The viewer's 360 qualities: each face's size.</summary>
    public static readonly (string Name, int Face)[] Qualities = { ("preview", 128), ("medium", 512), ("high", 1024), ("max", 2048) };
    public static readonly string[] Presets = { "front", "selfie", "behind", "shoulder", "above", "left", "right" };

    /// <summary>Whether a snapshot's arguments want a still rather than the next frame's eye.</summary>
    public static bool Wants(JsonObject a) =>
        a.Has("width") || a.Has("height") || a.Has("size") || a.Has("fov") || a.Has("camera") || a.Has("hide_avatars") || a.Has("format") || a.Has("aim");

    // --- pictures -----------------------------------------------------------------------------------------------------

    public async Task<JsonObject> SnapshotAsync(JsonObject a, string name, CancellationToken ct)
    {
        var camera = Camera(a);
        bool away = camera is not null;
        var aim = camera is null ? Aim(a) : null;
        var (w, h) = Size(a);
        float? fov = a.Num("fov") is { } deg ? (float)(Math.Clamp(deg, 5, 175) * Math.PI / 180) : null;
        bool hide = a.Bool("hide_avatars", false);
        var format = (a.Str("format") ?? "png").ToLowerInvariant() switch { "jpg" or "jpeg" => "jpg", "png" => "png", var f => throw new LeapError($"format: png or jpg, not {f}") };
        int quality = Math.Clamp(a.Int("quality", 75), 1, 100);
        double settle = Math.Clamp(a.Num("settle", away ? 5 : 0), 0, 30);

        var job = new StillJob
        {
            SettleSeconds = settle,
            Views = req =>
            {
                bool headset = req.Eyes.Any(e => e.Asymmetric);
                var (pw, ph) = (w ?? 0, h ?? 0);
                // unsized: the window's eye on the desktop, 1920x1080 in the headset (its eye is nearly square)
                var (ew, eh) = headset ? (1920, 1080) : (req.Width, req.Height);
                if (pw == 0 && ph == 0) (pw, ph) = (ew, eh);
                else if (pw == 0) pw = (int)Math.Round(ph * (double)ew / eh);
                else if (ph == 0) ph = (int)Math.Round(pw * (double)ew / eh);
                (pw, ph) = (Math.Clamp(pw, 16, FrameRenderer.StillMax), Math.Clamp(ph, 16, FrameRenderer.StillMax));
                var vfov = fov ?? (headset ? _s.Camera.Fov : req.VerticalFov);
                // the viewer's rawSnapshot: wider than the window keeps the window's width of view, taller its height
                if (fov is null && !headset && (double)pw / ph > (double)req.Width / req.Height)
                    vfov = 2 * MathF.Atan(MathF.Tan(vfov / 2) * ((float)req.Width / req.Height) / ((float)pw / ph));
                var (pos, fwd, up) = camera is { } cam ? Place(cam, req) : aim is { } aimed ? (aimed.At ?? req.Position, aimed.Fwd, aimed.Up) : (req.Position, req.Forward, req.Up);
                return new[] { new StillView { Position = pos, Forward = fwd, Up = up, VerticalFov = vfov, Width = pw, Height = ph, AsSeen = !away, HideAvatars = hide } };
            },
        };
        var (r, settled) = await RunAsync(job, away, ct);
        var pic = r.Pictures[0];
        Directory.CreateDirectory(Assistant.CaptureApi.PicturesDir);
        var path = Assistant.Recorder.Unique(Path.Combine(Assistant.CaptureApi.PicturesDir, Assistant.Recorder.FileName(name, "." + format)));
        await Task.Run(() => File.WriteAllBytes(path, Encode(pic.Rgba, pic.Width, pic.Height, format, quality)), ct);
        var o = new JsonObject { ["path"] = path, ["width"] = pic.Width, ["height"] = pic.Height };
        if (r.Tiles > 1) o["tiles"] = r.Tiles;
        if (settled > 0 || r.Pending > 0) (o["waited_seconds"], o["still_loading"]) = (settled, r.Pending);
        return o;
    }

    /// <summary>A 360: six faces from one point, stitched; the image's middle is the way the camera faces.</summary>
    public async Task<JsonObject> PanoramaAsync(JsonObject a, string name, CancellationToken ct)
    {
        var q = a.Str("quality") ?? "high";
        int face = Qualities.FirstOrDefault(x => x.Name.Equals(q, StringComparison.OrdinalIgnoreCase)).Face;
        if (face == 0) throw new LeapError($"quality: one of {string.Join(", ", Qualities.Select(x => $"{x.Name} ({x.Face} a face)"))}");
        int outW = Math.Clamp(a.Int("width", 4096), 1024, 8192) & ~1, outH = outW / 2;
        bool hide = a.Bool("hide_avatars", false);
        var camera = Camera(a);
        double settle = Math.Clamp(a.Num("settle", 8), 0, 30);
        Vec3 f = default, at = default;
        // each face a pixel wider all round than 90 degrees, so stitching samples across its edges
        int n = face + 2;
        float tan = (float)n / face;
        var job = new StillJob
        {
            SettleSeconds = settle,
            Views = req =>
            {
                var (pos, look, _) = camera is { } cam ? Place(cam, req) : (req.Position, req.Forward, req.Up);
                at = pos;
                var hz = new Vec3(look.X, look.Y, 0);
                f = hz.LengthSquared() > 1e-6f ? Vec3.Normalize(hz) : Vec3.UnitX;
                return Faces(f).Select(x => new StillView
                {
                    // from the eyes (the headset, first person) without the person's own avatar: their body seen from
                    // inside isn't a picture; from a camera behind them or placed, the avatar whole
                    Position = pos, Forward = x.Forward, Up = x.Up, Width = n, Height = n, SquareTangent = tan, HideAvatars = hide,
                    HideSelf = camera is null && (req.Self != null || req.HideAvatars?.Contains(_renderer.AvatarLimits.Self) == true),
                }).ToArray();
            },
        };
        var (r, settled) = await RunAsync(job, interest: true, ct);
        double heading = (90 - Math.Atan2(f.Y, f.X) * 180 / Math.PI + 360) % 360;
        var region = _s.Client?.Network.CurrentSim?.Name ?? "";
        var jpeg = await Task.Run(() =>
        {
            var eqr = Stitch(r.Pictures, f, face, outW, outH);
            return WithPanoramaXmp(Encode(eqr, outW, outH, "jpg", 95), outW, outH, heading, $"{_s.Host.Channel} {_s.Host.Version}");
        }, ct);
        Directory.CreateDirectory(Assistant.CaptureApi.PicturesDir);
        var label = string.Join(" - ", new[] { "360", name }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var path = Assistant.Recorder.Unique(Path.Combine(Assistant.CaptureApi.PicturesDir, Assistant.Recorder.FileName(label, ".jpg")));
        await File.WriteAllBytesAsync(path, jpeg, ct);
        var o = new JsonObject
        {
            ["path"] = path, ["width"] = outW, ["height"] = outH, ["face"] = face, ["heading"] = Math.Round(heading, 1),
            ["at"] = Json.Arr(new Vector3(at.X, at.Y, at.Z)), ["region"] = region, ["waited_seconds"] = settled, ["still_loading"] = r.Pending,
        };
        return o;
    }

    async Task<(StillResult Result, double Settled)> RunAsync(StillJob job, bool interest, CancellationToken ct)
    {
        if (_s.Region is null) throw new LeapError("not in a region yet");
        await _one.WaitAsync(ct);
        InterestListMode? restore = null;
        try
        {
            // the viewer's 360 floater asks the regions for everything around while it's open; a camera away from the
            // person needs what's around it too
            if (interest && _s.Client is { } c && c.InterestList.CurrentMode != InterestListMode.Panoramic360)
            {
                restore = c.InterestList.CurrentMode;
                await c.InterestList.SetModeAsync(InterestListMode.Panoramic360, ct);
            }
            _renderer.Still(job);
            try
            {
                var r = await job.Result.WaitAsync(TimeSpan.FromSeconds(job.SettleSeconds + 30), ct);
                return (r, r.SettledSeconds);
            }
            catch (TimeoutException)
            {
                job.Cancel("timed out");
                throw new LeapError("no picture: no frame was drawn (minimised, or not in a region yet)");
            }
            catch (OperationCanceledException) { job.Cancel("cancelled"); throw; }
            catch (InvalidOperationException e) { throw new LeapError($"no picture: {e.Message}"); }
        }
        finally
        {
            if (restore is { } mode && _s.Client is { } c2 && c2.InterestList.CurrentMode == InterestListMode.Panoramic360)
                try { await c2.InterestList.SetModeAsync(mode, CancellationToken.None); } catch (Exception) { }
            _one.Release();
        }
    }

    // --- cameras ------------------------------------------------------------------------------------------------------

    abstract record Cam;
    sealed record Fixed(Vec3 At, Vec3 LookAt) : Cam;
    sealed record Around(string Preset, float Distance) : Cam;

    static Cam? Camera(JsonObject a)
    {
        switch (a["camera"])
        {
            case null: return null;
            case JsonValue v when v.TryGetValue<string>(out var p):
            {
                var preset = p.Trim().ToLowerInvariant();
                if (!Presets.Contains(preset)) throw new LeapError($"camera: one of {string.Join(", ", Presets)}, or {{at, look_at}} in the region");
                return new Around(preset, (float)Math.Clamp(a.Num("distance", preset == "above" ? 4 : preset == "shoulder" ? 1.2 : 1.6), 0.3, 64));
            }
            case JsonObject o:
            {
                var at = o.Vec3("at") ?? throw new LeapError("camera: at [x, y, z] (region metres) and look_at [x, y, z]");
                var look = o.Vec3("look_at") ?? throw new LeapError("camera: look_at [x, y, z] too");
                if (Vector3.Distance(at, look) < 0.01f) throw new LeapError("camera: at and look_at are the same point");
                return new Fixed(new Vec3(at.X, at.Y, at.Z), new Vec3(look.X, look.Y, look.Z));
            }
            default: throw new LeapError("camera: a preset name or {at, look_at}");
        }
    }

    /// <summary>A camera's place for this frame (render thread): around the person's avatar where this still draws it
    /// (as others see it: in the headset the region's avatar, not the body under the head), as it is now.</summary>
    (Vec3 Pos, Vec3 Fwd, Vec3 Up) Place(Cam cam, FrameRequest req)
    {
        if (cam is Fixed fx) return (fx.At, Vec3.Normalize(fx.LookAt - fx.At), Vec3.UnitZ);
        var around = (Around)cam;
        // the avatar's head (its centre and 0.7 m, as the touch picker takes the eye), facing its body's way
        Vec3 centre = req.Position - new Vec3(0, 0, 0.7f), facing = req.Forward;
        if (_s.Client is { } c)
        {
            System.Numerics.Quaternion body;
            if (_renderer.AvatarPlaced(req.RegionHandle, c.Self.AgentID) is { } drawn) (centre, body) = (drawn.Pos, drawn.Rot);
            else
            {
                var (p, q) = (c.Self.SimPosition, c.Self.SimRotation);
                (centre, body) = (new Vec3(p.X, p.Y, p.Z), new System.Numerics.Quaternion(q.X, q.Y, q.Z, q.W));
            }
            var x = Vec3.Transform(Vec3.UnitX, body);
            if (new Vec3(x.X, x.Y, 0).LengthSquared() > 1e-4f) facing = x;
        }
        return AroundHead(around.Preset, around.Distance, centre + new Vec3(0, 0, 0.7f), facing);
    }

    /// <summary>A preset's camera around a head facing a way (only its level part counts): where, looking which way,
    /// and the picture's up.</summary>
    public static (Vec3 Pos, Vec3 Fwd, Vec3 Up) AroundHead(string preset, float d, Vec3 head, Vec3 facing)
    {
        facing = new Vec3(facing.X, facing.Y, 0);
        facing = facing.LengthSquared() > 1e-6f ? Vec3.Normalize(facing) : Vec3.UnitX;
        var right = Vec3.Cross(facing, Vec3.UnitZ);
        Vec3 at, look, up = Vec3.UnitZ;
        switch (preset)
        {
            case "front" or "selfie": at = head + facing * d + new Vec3(0, 0, 0.1f); look = head; break;
            case "behind": at = head - facing * d + new Vec3(0, 0, 0.3f); look = head; break;
            case "shoulder": at = head - facing * d + right * 0.45f + new Vec3(0, 0, 0.15f); look = head + facing * 6; break;
            case "left": at = head - right * d; look = head; break;
            case "right": at = head + right * d; look = head; break;
            default: at = head + new Vec3(0, 0, d); look = head - new Vec3(0, 0, 0.7f); up = facing; break;   // above: facing up the picture
        }
        return (at, Vec3.Normalize(look - at), up);
    }

    /// <summary>The headset's hands' panel (Client's Control/Shutter.cs): from its centre (at, in the region; the eyes
    /// without), turned to {forward, up} in the region; the avatar's arms as they are (holding the camera up, as the preview shows them: the user, 2026-10-29).</summary>
    static (Vec3? At, Vec3 Fwd, Vec3 Up)? Aim(JsonObject a)
    {
        if (a["aim"] is not JsonObject o || o.Vec3("forward") is not { } f || o.Vec3("up") is not { } u) return null;
        Vec3 fwd = new(f.X, f.Y, f.Z), up = new(u.X, u.Y, u.Z);
        if (fwd.LengthSquared() < 1e-6f || Vec3.Cross(fwd, up).LengthSquared() < 1e-6f) return null;
        Vec3? at = o.Vec3("at") is { } p ? new Vec3(p.X, p.Y, p.Z) : null;
        return (at, Vec3.Normalize(fwd), Vec3.Normalize(up));
    }

    static (int? W, int? H) Size(JsonObject a)
    {
        int? w = a.Num("width") is { } x ? (int)x : null, h = a.Num("height") is { } y ? (int)y : null;
        if (a.Str("size") is { Length: > 0 } s && s.ToLowerInvariant() is not ("window" or "current"))
        {
            var parts = s.ToLowerInvariant().Split('x', '*', ' ', ',');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var sw) || !int.TryParse(parts[1], out var sh))
                throw new LeapError("size: WIDTHxHEIGHT (the viewer's: 1920x1080, 2560x1440, 3840x2160, 5120x2880, 7680x4320...) or window");
            (w, h) = (sw, sh);
        }
        if (w is < 16 or > FrameRenderer.StillMax || h is < 16 or > FrameRenderer.StillMax)
            throw new LeapError($"a picture is 16 to {FrameRenderer.StillMax} pixels a side (the viewer's largest is 7680x4320)");
        return (w, h);
    }

    // --- 360s ---------------------------------------------------------------------------------------------------------

    /// <summary>The six faces in the frame of the facing f: ahead, right, behind, left, up, down.</summary>
    static (Vec3 Forward, Vec3 Up)[] Faces(Vec3 f)
    {
        var r = Vec3.Cross(f, Vec3.UnitZ);
        return new[] { (f, Vec3.UnitZ), (r, Vec3.UnitZ), (-f, Vec3.UnitZ), (-r, Vec3.UnitZ), (Vec3.UnitZ, -f), (-Vec3.UnitZ, f) };
    }

    /// <summary>Equirectangular from the six faces (each n+2 pixels with a pixel's border): longitude 0 in the middle is
    /// the facing, east of it to the right; latitude +90 at the top.</summary>
    static byte[] Stitch(IReadOnlyList<StillPicture> faces, Vec3 f, int face, int w, int h)
    {
        var axes = Faces(f).Select(x =>
        {
            var right = Vec3.Normalize(Vec3.Cross(x.Forward, x.Up));
            return (F: x.Forward, R: right, U: Vec3.Cross(right, x.Forward));
        }).ToArray();
        var r = Vec3.Cross(f, Vec3.UnitZ);
        var outp = new byte[w * h * 4];
        int n = face + 2;
        Parallel.For(0, h, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, y =>
        {
            double lat = Math.PI / 2 - (y + 0.5) * Math.PI / h;
            float cl = (float)Math.Cos(lat), sl = (float)Math.Sin(lat);
            for (int x = 0; x < w; x++)
            {
                double lon = (x + 0.5) * 2 * Math.PI / w - Math.PI;
                var d = cl * ((float)Math.Cos(lon) * f + (float)Math.Sin(lon) * r) + sl * Vec3.UnitZ;
                int best = 0;
                float most = float.MinValue;
                for (int i = 0; i < 6; i++)
                {
                    float k = Vec3.Dot(d, axes[i].F);
                    if (k > most) (most, best) = (k, i);
                }
                var a = axes[best];
                float u = Vec3.Dot(d, a.R) / most, v = Vec3.Dot(d, a.U) / most;   // -1..1 across the face's 90 degrees
                // to pixels: the face's 90 degrees span pixels 1..n-1 of its n
                float px = (u + 1) * 0.5f * face + 1 - 0.5f, py = (1 - v) * 0.5f * face + 1 - 0.5f;
                Sample(faces[best].Rgba, n, px, py, outp, (y * w + x) * 4);
            }
        });
        return outp;
    }

    static void Sample(byte[] src, int n, float px, float py, byte[] dst, int o)
    {
        int x0 = Math.Clamp((int)MathF.Floor(px), 0, n - 2), y0 = Math.Clamp((int)MathF.Floor(py), 0, n - 2);
        float fx = Math.Clamp(px - x0, 0, 1), fy = Math.Clamp(py - y0, 0, 1);
        int i00 = (y0 * n + x0) * 4, i10 = i00 + 4, i01 = i00 + n * 4, i11 = i01 + 4;
        for (int c = 0; c < 3; c++)
        {
            float top = src[i00 + c] + (src[i10 + c] - src[i00 + c]) * fx, bottom = src[i01 + c] + (src[i11 + c] - src[i01 + c]) * fx;
            dst[o + c] = (byte)Math.Clamp(top + (bottom - top) * fy + 0.5f, 0, 255);
        }
        dst[o + 3] = 255;
    }

    /// <summary>The XMP 360 viewers read (Google's GPano, as the viewer's stitcher writes it), as an APP1 segment after
    /// the JPEG's first one.</summary>
    public static byte[] WithPanoramaXmp(byte[] jpeg, int w, int h, double heading, string software)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var xmp = "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:GPano=\"http://ns.google.com/photos/1.0/panorama/\">"
            + "<GPano:ProjectionType>equirectangular</GPano:ProjectionType><GPano:UsePanoramaViewer>True</GPano:UsePanoramaViewer>"
            + $"<GPano:CroppedAreaImageWidthPixels>{w}</GPano:CroppedAreaImageWidthPixels><GPano:CroppedAreaImageHeightPixels>{h}</GPano:CroppedAreaImageHeightPixels>"
            + $"<GPano:FullPanoWidthPixels>{w}</GPano:FullPanoWidthPixels><GPano:FullPanoHeightPixels>{h}</GPano:FullPanoHeightPixels>"
            + "<GPano:CroppedAreaLeftPixels>0</GPano:CroppedAreaLeftPixels><GPano:CroppedAreaTopPixels>0</GPano:CroppedAreaTopPixels>"
            + $"<GPano:PoseHeadingDegrees>{heading.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}</GPano:PoseHeadingDegrees>"
            + "<GPano:SourcePhotosCount>6</GPano:SourcePhotosCount>"
            + $"<GPano:CaptureSoftware>{System.Security.SecurityElement.Escape(software)}</GPano:CaptureSoftware>"
            + $"<GPano:StitchingSoftware>{System.Security.SecurityElement.Escape(software)}</GPano:StitchingSoftware>"
            + $"<GPano:FirstPhotoDate>{now}</GPano:FirstPhotoDate><GPano:LastPhotoDate>{now}</GPano:LastPhotoDate>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        var body = Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0" + xmp);
        if (body.Length + 2 > 0xFFFF || jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return jpeg;
        // after SOI, and after an APP0 (JFIF) when there is one
        int at = 2;
        if (jpeg[2] == 0xFF && jpeg[3] == 0xE0) at = 4 + (jpeg[4] << 8 | jpeg[5]);
        var seg = new byte[4 + body.Length];
        (seg[0], seg[1], seg[2], seg[3]) = (0xFF, 0xE1, (byte)((body.Length + 2) >> 8), (byte)(body.Length + 2));
        body.CopyTo(seg, 4);
        var o = new byte[jpeg.Length + seg.Length];
        Buffer.BlockCopy(jpeg, 0, o, 0, at);
        Buffer.BlockCopy(seg, 0, o, at, seg.Length);
        Buffer.BlockCopy(jpeg, at, o, at + seg.Length, jpeg.Length - at);
        return o;
    }

    public static byte[] Encode(byte[] rgba, int w, int h, string format, int quality)
    {
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bmp.GetPixels(), rgba.Length);
        using var data = bmp.Encode(format == "jpg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, format == "jpg" ? quality : 90);
        return data.ToArray();
    }
}
