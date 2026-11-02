using System.Globalization;
using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Imaging.Skia;
using LibreMetaverse.ImportExport;
using Sojourn.Core.Leap;
using Sojourn.Core.Media;
using SkiaSharp;

namespace Sojourn.Core.Agents;

/// <summary>
/// TesselAgent upload (pictures, sounds, animations), uploadMesh, and uploadLook (what an upload would make of a file,
/// read here without sending anything), through the normal NewFileAgentInventory upload, so the grid's normal fee
/// applies. No spending caps here (operation, not code): the confirmation gate asks the person where uploads cost
/// (Session's Confirmations.Uploads.cs), pinning `max_cost` to the fee they were shown, which the ops then hold to.
/// `quote` asks the price without uploading. Errors are in words a person can act on ("The sound is 42 seconds long;
/// Second Life takes up to 30."), since Sojourn Access says them as they are. uploadClips cuts a longer sound into
/// clips a scripted player plays in order (Media/SoundClips.cs), confirmed once for the whole set.
/// </summary>
public sealed class UploadOps
{
    readonly AgentSession _s;
    public UploadOps(AgentSession s) => _s = s;

    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    public static readonly string[] TextureExt = { ".png", ".tga", ".bmp", ".jpg", ".jpeg" };
    public static readonly string[] SoundExt = { ".wav", ".ogg" };
    public static readonly string[] AnimationExt = { ".bvh", ".anim" };
    public static readonly string[] MeshExt = { ".dae", ".gltf", ".glb", ".obj" };
    /// <summary>What's read at all: a bigger picture would take gigabytes to decode before it's made smaller.</summary>
    public const int MaxPictureSide = 8192;
    public const long MaxFileBytes = 512L << 20;
    /// <summary>Second Life's limits on an inventory item's name and description (the viewer cuts them there).</summary>
    public const int NameLength = 63, DescLength = 127;
    /// <summary>A mesh object's largest side (the viewer's and the region's limit for a prim).</summary>
    public const double MaxMeshSide = 64;

    /// <summary>Fees to pretend on a free grid, for tests ("texture=0,sound=50"; SOJOURN_TEST_UPLOAD_FEE): uploads of
    /// those kinds are then confirmed as spending, at that fee. "clip=N" a sound clip's fee apart from a single sound's;
    /// "balance=N" the L$ balance uploads are checked against (the local grid's is 0). Null normally.</summary>
    public static IReadOnlyDictionary<string, int>? TestFees { get; } = ParseFees(Environment.GetEnvironmentVariable("SOJOURN_TEST_UPLOAD_FEE"));

    /// <summary>The balance uploads are checked against and said with: a test's pretend one, else the account's where
    /// it's known.</summary>
    public int? Balance => TestFees?.TryGetValue("balance", out var b) == true ? b
        : _s.BalanceKnown && _s.Client is { } c ? c.Self.Balance : null;

    static Dictionary<string, int>? ParseFees(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var d = new Dictionary<string, int>();
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.Split('=') is [var k, var v] && int.TryParse(v, out var fee)) d[k.Trim()] = fee;
        return d;
    }

    /// <summary>texture, sound, animation or mesh by the file's extension; "" for none of them.</summary>
    public static string KindOf(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return TextureExt.Contains(ext) ? "texture" : SoundExt.Contains(ext) ? "sound" : AnimationExt.Contains(ext) ? "animation"
            : MeshExt.Contains(ext) ? "mesh" : "";
    }

    /// <param name="sounds">the sound formats the caller takes (Sojourn Access has Windows decode more)</param>
    public static string WrongKind(string path, string sounds = "WAV or Ogg") =>
        $"{Path.GetFileName(path)} isn't something Second Life takes. Uploads can be pictures (PNG, JPEG, TGA or BMP), "
        + $"sounds ({sounds}), animations (BVH or anim) or models (glTF, GLB, Collada DAE or OBJ).";

    /// <summary>The fee for an upload of this kind (a picture's by its size as uploaded), as the viewer's
    /// LLAgentBenefits works it out: the account's benefits, else the grid's one upload price; 0 where neither is known
    /// (OpenSim). A model's fee is the grid's quote, not this.</summary>
    public int Fee(string kind, int w = 0, int h = 0)
    {
        if (TestFees?.TryGetValue(kind, out var t) == true) return t;
        var c = C;
        var b = c.Self.Benefits;
        int cost = kind switch
        {
            "texture" when (long)w * h > 1024 * 1024 && b?.LargeTextureUploadCost is { Count: > 0 } large => large[0],
            "texture" => b?.TextureUploadCost ?? -1,
            "sound" => b?.SoundUploadCost ?? -1,
            "animation" => b?.AnimationUploadCost ?? -1,
            "mesh" => b?.MeshUploadCost ?? -1,
            _ => -1,
        };
        if (cost < 0) cost = c.Settings.UploadCost;
        return Math.Max(0, cost);
    }

    static string Existing(JsonObject a)
    {
        var path = a.Str("path") ?? throw new LeapError("need path");
        if (!File.Exists(path)) throw new LeapError($"There's no file at {path}.");
        var bytes = new FileInfo(path).Length;
        if (bytes == 0) throw new LeapError($"{Path.GetFileName(path)} is empty.");
        if (bytes > MaxFileBytes) throw new LeapError($"{Path.GetFileName(path)} is {UploadWords.Bytes(bytes)}, too big to upload.");
        return path;
    }

    static string Kind(JsonObject a, string path)
    {
        var kind = a.Str("type") ?? KindOf(path);
        if (kind is "image" or "picture") kind = "texture";
        if (kind is "model") kind = "mesh";
        return kind is "texture" or "sound" or "animation" or "mesh" ? kind : throw new LeapError(WrongKind(path));
    }

    // --- looking --------------------------------------------------------------------------------------------------------

    /// <summary>uploadLook: what an upload of the file would be, read here, nothing sent: its kind and size, and for a
    /// picture its size and what it'll be resized to and whether it has see-through parts, for a sound its length and
    /// channels, for an animation its length and joints, for a model its parts, faces, triangles and size; the fee
    /// where it's known before asking the grid (a model's isn't). A file that can't be uploaded is refused in words.
    /// A sound with `clips` (or `clip_seconds`) isn't refused for its length: `clips` says how uploadClips would cut it
    /// with the same args.</summary>
    public async Task<JsonNode?> LookAsync(JsonObject a, CancellationToken ct)
    {
        var path = Existing(a);
        var kind = Kind(a, path);
        var reply = new JsonObject
        {
            ["type"] = kind, ["file"] = Path.GetFileName(path), ["bytes"] = new FileInfo(path).Length,
            ["name"] = DefaultName(path),
        };
        switch (kind)
        {
            case "texture":
            {
                var p = await Task.Run(() => Picture(path, MaxSizeOf(a), keep: false), ct);
                reply["format"] = p.Format;
                reply["width"] = p.Width; reply["height"] = p.Height;
                reply["upload_width"] = p.UploadWidth; reply["upload_height"] = p.UploadHeight;
                reply["resized"] = p.Width != p.UploadWidth || p.Height != p.UploadHeight;
                reply["alpha"] = p.SeeThrough;
                reply["fee"] = Fee(kind, p.UploadWidth, p.UploadHeight);
                break;
            }
            case "sound":
            {
                bool clips = a.Bool("clips", false) || a.Has("clip_seconds");
                var s = await Task.Run(() => Sound(path, refuseLong: !clips), ct);
                reply["format"] = s.Format;
                reply["seconds"] = Math.Round(s.Seconds, 2);
                reply["channels"] = s.Channels;
                reply["rate"] = s.Rate;
                reply["max_seconds"] = SoundConvert.MaxSeconds;
                reply["too_long"] = s.Seconds > SoundConvert.MaxSeconds;
                reply["fee"] = Fee(kind);
                if (clips)
                {
                    var (_, mono) = await Task.Run(() => Decoded(path), ct);
                    reply["clips"] = ClipsPlan(a, path, mono).Json;
                }
                break;
            }
            case "animation":
            {
                var (_, info) = await Task.Run(() => Animation(path, a), ct);
                foreach (var (k, v) in info) reply[k] = v?.DeepClone();
                reply["fee"] = Fee(kind);
                break;
            }
            case "mesh":
            {
                var m = await Task.Run(() => Model(path, a, look: true), ct);
                foreach (var (k, v) in m.Facts) reply[k] = v?.DeepClone();
                break;
            }
        }
        return reply;
    }

    static string DefaultName(string path)
    {
        var n = Path.GetFileNameWithoutExtension(path).Trim();
        return n.Length > NameLength ? n[..NameLength] : n;
    }

    static int MaxSizeOf(JsonObject a) => Math.Clamp(a.Int("max_size", 1024), 64, 2048);

    // --- pictures -----------------------------------------------------------------------------------------------------

    sealed record PictureFacts(string Format, int Width, int Height, int UploadWidth, int UploadHeight, bool SeeThrough, SKBitmap? Scaled);

    /// <summary>A picture read, and its size as uploaded: each side a power of two at most maxSize, as the viewer's
    /// LLImageRaw::biasedScaleToPowerOfTwo (rounding down unless the side is over 1.75 times the power below). keep: the
    /// resized pixels kept for encoding (the caller disposes them).</summary>
    static PictureFacts Picture(string path, int maxSize, bool keep)
    {
        var data = File.ReadAllBytes(path);
        bool tga = Tga.Is(path);
        int w, h;
        string format;
        if (tga)
        {
            try { (w, h) = Tga.Size(data); }
            catch (MediaFormatException e) { throw new LeapError(e.Message); }
            format = "TGA";
        }
        else
        {
            using var codec = SKCodec.Create(new SKMemoryStream(data))
                ?? throw new LeapError($"{Path.GetFileName(path)} can't be read as a picture: the file may be damaged, or not the kind its name says.");
            (w, h) = (codec.Info.Width, codec.Info.Height);
            format = codec.EncodedFormat switch { SKEncodedImageFormat.Jpeg => "JPEG", SKEncodedImageFormat.Png => "PNG", SKEncodedImageFormat.Bmp => "BMP", var f => f.ToString().ToUpperInvariant() };
        }
        if (w > MaxPictureSide || h > MaxPictureSide)
            throw new LeapError($"{Path.GetFileName(path)} is {w} by {h} pixels, too big to read here (at most {MaxPictureSide} a side). Make it smaller first; Second Life keeps at most {maxSize} a side anyway.");
        SKBitmap src;
        try { src = (tga ? Tga.Decode(data) : SKBitmap.Decode(data)) ?? throw new LeapError($"{Path.GetFileName(path)} can't be read as a picture."); }
        catch (MediaFormatException e) { throw new LeapError(e.Message); }
        using (src)
        {
            int uw = Biased(w, maxSize), uh = Biased(h, maxSize);
            var scaled = w == uw && h == uh ? src.Copy() : src.Resize(new SKImageInfo(uw, uh), new SKSamplingOptions(SKCubicResampler.Mitchell));
            bool seeThrough = false;
            if (src.AlphaType != SKAlphaType.Opaque)
                foreach (var px in scaled.Pixels)
                    if (px.Alpha < 255) { seeThrough = true; break; }
            if (!keep) { scaled.Dispose(); scaled = null; }
            return new PictureFacts(format, w, h, uw, uh, seeThrough, scaled);
        }
    }

    /// <summary>The viewer's LLImageRaw::biasedDimToPowerOfTwo: the power of two below, unless the side is over 1.75 times
    /// it, at most max (and at least 16 here).</summary>
    public static int Biased(int dim, int max)
    {
        int larger = max, smaller = max;
        while (smaller > dim && smaller > 16) (larger, smaller) = (smaller, smaller >> 1);
        return (float)dim / smaller > 1.75f ? larger : smaller;
    }

    // --- sounds -------------------------------------------------------------------------------------------------------

    sealed record SoundFacts(string Format, int Channels, int Rate, double Seconds);

    static SoundFacts Sound(string path, bool refuseLong = true)
    {
        var data = File.ReadAllBytes(path);
        SoundFacts f;
        try
        {
            if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            {
                var w = Wav.Read(data);
                f = new SoundFacts($"WAV, {w.Format}", w.Channels, w.SampleRate, (double)w.Frames / w.SampleRate);
            }
            else
            {
                var d = SoundConvert.DecodeVorbis(data);
                f = new SoundFacts("Ogg Vorbis", d.SourceChannels, d.SampleRate, d.Duration);
            }
        }
        catch (MediaFormatException e) { throw new LeapError(e.Message); }
        if (refuseLong && f.Seconds > SoundConvert.MaxSeconds)
            throw new LeapError($"{Path.GetFileName(path)} is {UploadWords.Seconds(f.Seconds)} long; Second Life takes sounds up to {SoundConvert.MaxSeconds:0} seconds. Trim it to {SoundConvert.MaxSeconds:0} seconds or less, or upload it cut into clips (uploadClips).");
        if (f.Seconds <= 0) throw new LeapError($"{Path.GetFileName(path)} has no sound in it.");
        return f;
    }

    // --- animations ---------------------------------------------------------------------------------------------------

    /// <summary>An animation's asset bytes and what it is (from a .bvh with the upload floater's options as args, or a
    /// .anim as it is).</summary>
    static (byte[] Data, JsonObject Info) Animation(string path, JsonObject a)
    {
        var data = File.ReadAllBytes(path);
        var info = new JsonObject();
        try
        {
            if (Path.GetExtension(path).Equals(".bvh", StringComparison.OrdinalIgnoreCase))
            {
                var o = new AnimOptions();
                if (a.Num("priority") is { } pr) o = o with { Priority = (int)pr };
                if (a.Bool("loop") is { } lp) o = o with { Loop = lp };
                if (a.Num("loop_in") is { } li) o = o with { LoopInPercent = li };
                if (a.Num("loop_out") is { } lo) o = o with { LoopOutPercent = lo };
                if (a.Num("ease_in") is { } ei) o = o with { EaseIn = ei };
                if (a.Num("ease_out") is { } eo) o = o with { EaseOut = eo };
                if (a.Str("hand_pose") is { } hp) o = o with { HandPose = hp };
                if (a.Str("emote") is { } em) o = o with { Emote = em };
                var conv = AnimConvert.Convert(System.Text.Encoding.UTF8.GetString(data), o);
                data = conv.Data;
                info["format"] = "BVH";
                info["seconds"] = Math.Round(conv.Anim.Duration, 2);
                info["joints"] = conv.Anim.Joints.Count;
                info["priority"] = conv.Anim.Priority;
                info["loop"] = conv.Anim.Loop;
                if (conv.Warnings.Count > 0) info["warnings"] = Json.Strings(conv.Warnings);
            }
            else
            {
                var anim = AnimFile.Parse(data);
                info["format"] = "Second Life animation";
                info["seconds"] = Math.Round(anim.Duration, 2);
                info["joints"] = anim.Joints.Count;
                info["priority"] = anim.Priority;
                info["loop"] = anim.Loop;
            }
        }
        catch (MediaFormatException e) { throw new LeapError(e.Message); }
        return (data, info);
    }

    // --- models -------------------------------------------------------------------------------------------------------

    sealed record ModelFacts(List<ModelPrim> Prims, JsonObject Facts, string Log);

    /// <summary>A model loaded, scaled and checked: its parts, faces, triangles, size, whether it's rigged, its
    /// textures; what the grid would refuse is refused here in words. look: textures counted (loaded to count them).</summary>
    static ModelFacts Model(string path, JsonObject a, bool look)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        IDiagnosticModelLoader loader = ext switch
        {
            ".gltf" or ".glb" => new GltfLoader(new SkiaTextureCodec()),
            ".dae" => new ColladaLoader(new SkiaTextureCodec()),
            ".obj" => new ObjLoader(new SkiaTextureCodec()),
            _ => throw new LeapError(WrongKind(path)),
        };
        bool textures = look || a.Bool("textures", false);
        ModelLoadResult loaded;
        try { loaded = loader.LoadWithDiagnostics(path, textures); }
        catch (Exception e) { throw new LeapError($"{Path.GetFileName(path)} can't be read as a model: {e.Message}"); }
        var log = string.Join("\n", loaded.Diagnostics.Select(d => d.ToString()));
        if (loaded.HasErrors || loaded.Prims.Count == 0)
        {
            var why = loaded.Diagnostics.FirstOrDefault(d => d.Severity == ModelDiagnosticSeverity.Error)?.Message ?? "there's nothing in it to upload";
            throw new LeapError($"{Path.GetFileName(path)} can't be uploaded: {Sentence(why)}");
        }
        var prims = loaded.Prims.ToList();
        double scale = a.Num("scale") is { } sc && sc > 0 ? sc : 1;
        if (scale != 1)
            foreach (var p in prims) { p.Scale *= (float)scale; p.Position *= (float)scale; }
        var checks = ModelUploadChecks.Check(prims);
        if (checks.FirstOrDefault(d => d.Severity == ModelDiagnosticSeverity.Error) is { } bad)
            throw new LeapError($"{Path.GetFileName(path)} can't be uploaded: {Sentence(bad.Message)}");
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var p in prims)
        {
            min = Vector3.Min(min, p.Position - p.Scale / 2);
            max = Vector3.Max(max, p.Position + p.Scale / 2);
        }
        var size = max - min;
        double biggest = Math.Max(size.X, Math.Max(size.Y, size.Z));
        var faces = prims.SelectMany(p => p.Faces).ToList();
        var pictures = faces.Select(f => f.Material).Where(m => m.TextureData.Length > 0 || m.Texture.Length > 0)
            .Select(m => m.Texture.Length > 0 ? m.Texture : m.ID).Distinct().Count();
        var warnings = loaded.Diagnostics.Concat(checks).Where(d => d.Severity == ModelDiagnosticSeverity.Warning)
            .Select(d => Sentence(d.Message)).Distinct().Take(8).ToList();
        var facts = new JsonObject
        {
            ["format"] = ext switch { ".dae" => "Collada", ".obj" => "OBJ", _ => "glTF" },
            ["parts"] = prims.Count,
            ["faces"] = faces.Count,
            ["triangles"] = faces.Sum(f => f.Indices.Count / 3),
            ["vertices"] = faces.Sum(f => f.Vertices.Count),
            ["size"] = new JsonArray(Json.R(size.X, 3), Json.R(size.Y, 3), Json.R(size.Z, 3)),
            ["scale"] = scale,
            ["rigged"] = prims.Any(p => p.Skin != null),
            ["textures"] = pictures,
        };
        if (biggest > MaxMeshSide) facts["too_big"] = Math.Floor(MaxMeshSide / biggest * scale * 1000) / 1000;
        if (warnings.Count > 0) facts["warnings"] = Json.Strings(warnings);
        return new ModelFacts(prims, facts, log);
    }

    static string Sentence(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return s;
        s = char.ToUpperInvariant(s[0]) + s[1..];
        return ".!?".Contains(s[^1]) ? s : s + ".";
    }

    // --- uploading ----------------------------------------------------------------------------------------------------

    public async Task<JsonNode?> UploadAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var path = Existing(a);
        var type = Kind(a, path);
        if (type == "mesh") throw new LeapError("A model goes up by uploadMesh.");
        var name = Cut(a.Str("name") is { Length: > 0 } nm ? nm : DefaultName(path), NameLength);
        var desc = Cut(a.Str("desc") ?? "", DescLength);
        var timeout = TimeSpan.FromSeconds(a.Num("timeout", 120));
        byte[] data;
        AssetType at;
        InventoryType it;
        FolderType ft;
        var reply = new JsonObject { ["type"] = type, ["name"] = name };
        int cost;
        switch (type)
        {
            case "texture":
            {
                var p = await Task.Run(() => Picture(path, MaxSizeOf(a), keep: true), ct);
                using (var scaled = p.Scaled!)
                {
                    var tex = new AssetTexture(SkiaTextureCodec.ToManagedImage(scaled));
                    await Task.Run(tex.Encode, ct);
                    data = tex.AssetData;
                }
                (at, it, ft) = (AssetType.Texture, InventoryType.Texture, FolderType.Texture);
                reply["width"] = p.UploadWidth;
                reply["height"] = p.UploadHeight;
                reply["from_width"] = p.Width;
                reply["from_height"] = p.Height;
                cost = Fee(type, p.UploadWidth, p.UploadHeight);
                break;
            }
            case "sound":
            {
                var f = Sound(path);
                data = await File.ReadAllBytesAsync(path, ct);
                if (Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    // as the viewer's converter: mono Ogg Vorbis at 44.1 kHz, up to 30 s (more formats accepted)
                    try
                    {
                        var (ogg, info) = await Task.Run(() => SoundConvert.WavToVorbis(data), ct);
                        data = ogg;
                        reply["converted"] = $"{info.SourceChannels} ch {info.SourceSampleRate} Hz -> mono 44100 Hz Vorbis";
                    }
                    catch (MediaFormatException e) { throw new LeapError(e.Message); }
                }
                reply["duration"] = Math.Round(f.Seconds, 2);
                (at, it, ft) = (AssetType.Sound, InventoryType.Sound, FolderType.Sound);
                cost = Fee(type);
                break;
            }
            default:   // animation
            {
                var (anim, info) = Animation(path, a);
                data = anim;
                if (info["seconds"] is { } secs) reply["duration"] = secs.DeepClone();
                if (info["joints"] is { } joints) reply["joints"] = joints.DeepClone();
                if (info["warnings"] is { } warns) reply["warnings"] = warns.DeepClone();
                (at, it, ft) = (AssetType.Animation, InventoryType.Animation, FolderType.Animation);
                cost = Fee(type);
                break;
            }
        }
        reply["cost"] = cost;
        if (a.Bool("quote", false)) return reply;
        HoldTo(a, cost);
        var folder = a.Uuid("folder_id") ?? _s.Inventory.FolderFor(ft);
        // without next_perms, the person's Default Permissions for uploads (SojournBuild setDefaultPerms; the viewer's own)
        var d = _s.Ownership.DefaultsFor("uploads");
        var next = a.Str("next_perms") is { } np ? PermissionMask.Move | ObjectViews.PermMask(np) : d.NextMask;
        var perms = new Permissions((uint)PermissionMask.All, (uint)d.EveryoneMask, (uint)d.GroupMask, (uint)next, (uint)PermissionMask.All);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var r = await c.Inventory.CreateItemFromAssetAsync(data, name, desc, at, it, folder, perms, cts.Token);
        if (!r.Success) throw new LeapError(Failed(r.Status, r.Error, r.RawResult));
        reply["item_id"] = r.ItemID.ToString();
        reply["asset_id"] = r.AssetID.ToString();
        reply["folder_id"] = folder.ToString();
        if (at == AssetType.Sound) _s.Ears.Learn(r.AssetID, name);
        reply["charged"] = cost;
        return reply;
    }

    /// <summary>The fee the person confirmed (the gate pins `max_cost`): an upload that now costs more isn't made.</summary>
    static void HoldTo(JsonObject a, int cost)
    {
        if (a.Num("max_cost") is { } max && cost > max)
            throw new LeapError($"The upload now costs L${cost}, more than the L${max:0} you confirmed; nothing was uploaded.");
    }

    static string Cut(string s, int n) => s.Length > n ? s[..n] : s;

    public async Task<JsonNode?> UploadMeshAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var path = Existing(a);
        if (KindOf(path) != "mesh") throw new LeapError(WrongKind(path));
        var m = await Task.Run(() => Model(path, a, look: false), ct);
        if (m.Facts["too_big"] is { } fit)
        {
            var s = m.Facts["size"]!.AsArray().Select(v => v!.GetValue<double>()).Max();
            throw new LeapError($"At this scale the model is {UploadWords.Metres(s)} across; Second Life allows up to {MaxMeshSide:0} metres a side. A scale of {fit} fits; nothing was uploaded.");
        }
        var name = Cut(a.Str("name") is { Length: > 0 } nm ? nm : DefaultName(path), NameLength);
        var folder = a.Uuid("folder_id") ?? _s.Inventory.FolderFor(FolderType.Object);
        var d = _s.Ownership.DefaultsFor("uploads");
        var next = a.Str("next_perms") is { } np ? PermissionMask.Move | ObjectViews.PermMask(np) : d.NextMask;
        var perms = new Permissions((uint)PermissionMask.All, (uint)d.EveryoneMask, (uint)d.GroupMask, (uint)next, (uint)PermissionMask.All);
        // the official uploader's defaults: levels of detail made from the model, a physics shape from its outline
        var options = new ModelAssetOptions { GenerateLods = a.Bool("lods", true), GeneratePhysics = a.Str("physics") != "box" };
        var quote = a.Bool("quote", false);
        var quoted = -1;
        double? max = a.Num("max_cost");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(a.Num("timeout", 180)));
        var r = await c.Inventory.UploadModelAsync(m.Prims, name, Cut(a.Str("desc") ?? "", DescLength), folder, perms,
            confirmCost: price => { quoted = price; return !quote && (max is not { } mx || Charged(price) <= mx); },
            cancellationToken: cts.Token, options: options);
        var reply = new JsonObject { ["name"] = name, ["cost"] = Charged(Math.Max(0, quoted)) };
        foreach (var (k, v) in m.Facts) reply[k] = v?.DeepClone();
        if (r.RawResult is LibreMetaverse.StructuredData.OSDMap raw && raw.TryGetValue("data", out var dataOsd) && dataOsd is LibreMetaverse.StructuredData.OSDMap w)
        {
            double D(string k) => w.TryGetValue(k, out var v) ? v.AsReal() : 0;
            reply["weights"] = new JsonObject
            {
                ["land_impact"] = Json.R(Math.Max(D("resource_cost"), D("model_streaming_cost")), 3),
                ["streaming"] = Json.R(D("model_streaming_cost"), 3), ["physics"] = Json.R(D("physics_cost"), 3),
                ["server"] = Json.R(D("simulation_cost"), 3),
            };
        }
        if (m.Log.Length > 0) reply["log"] = m.Log;
        if (quote)
        {
            if (quoted < 0 && !r.Success) reply["refusal"] = Failed(r.Status, r.Error, r.RawResult);
            return reply;
        }
        if (r.Status == "cost_rejected" && max is { } held)
            throw new LeapError($"The upload now costs L${Charged(quoted)}, more than the L${held:0} you confirmed; nothing was uploaded.");
        if (!r.Success) throw new LeapError(Failed(r.Status, r.Error, r.RawResult));
        reply["item_id"] = r.ItemID.ToString();
        reply["asset_id"] = r.AssetID.ToString();
        reply["folder_id"] = folder.ToString();
        reply["charged"] = Charged(Math.Max(0, quoted));
        return reply;
    }

    // --- a long sound as clips -----------------------------------------------------------------------------------------

    /// <summary>Each clip uploaded (from the op's own thread): {index, count, name, item_id}. For the person's progress.</summary>
    public event Action<JsonObject>? ClipUploaded;

    /// <summary>A clip's fee: a sound's (or a test's pretend clip fee).</summary>
    public int ClipFee() => TestFees?.TryGetValue("clip", out var t) == true ? t : Fee("sound");

    public static string ClipsNotecardName(string baseName) => $"{Cut(baseName.Trim(), NameLength - 6)} clips";

    readonly object _decodedLock = new();
    (string Key, SoundFacts Facts, float[] Mono)? _decoded;

    /// <summary>A sound file read whole as mono at 44.1 kHz, the last one kept (the window's look, the gate's and the
    /// upload read the same file in turn).</summary>
    (SoundFacts Facts, float[] Mono) Decoded(string path)
    {
        var fi = new FileInfo(path);
        var key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        lock (_decodedLock)
            if (_decoded is { } d && d.Key == key) return (d.Facts, d.Mono);
        var facts = Sound(path, refuseLong: false);
        float[] mono;
        try { mono = SoundConvert.ReadMono(File.ReadAllBytes(path), Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase)).Mono; }
        catch (MediaFormatException e) { throw new LeapError(e.Message); }
        lock (_decodedLock) _decoded = (key, facts, mono);
        return (facts, mono);
    }

    sealed record ClipPlan(List<SoundClips.Clip> Clips, List<string> Names, string Base, int Each, JsonObject Json);

    /// <summary>How uploadClips cuts the file with these args: clip_seconds (29.9), cuts exact|quiet, name (the base).</summary>
    ClipPlan ClipsPlan(JsonObject a, string path, float[] mono)
    {
        var secs = Math.Clamp(a.Num("clip_seconds", SoundClips.DefaultSeconds), SoundClips.MinSeconds, SoundConvert.MaxSeconds);
        var quiet = a.Str("cuts") == "quiet";
        var baseName = Cut(a.Str("name") is { Length: > 0 } nm ? nm.Trim() : DefaultName(path), NameLength);
        var clips = SoundClips.Plan(mono, SoundConvert.SampleRate, secs, quiet);
        if (clips.Count == 0) throw new LeapError($"{Path.GetFileName(path)} has no sound in it.");
        var names = SoundClips.Names(baseName, clips.Count, NameLength);
        var lengths = clips.Select(c => Math.Round((double)c.Length / SoundConvert.SampleRate, 2)).ToList();
        int each = ClipFee();
        var json = new JsonObject
        {
            ["count"] = clips.Count, ["clip_seconds"] = secs, ["cuts"] = quiet ? "quiet" : "exact", ["base"] = baseName,
            ["names"] = Json.Strings(names), ["seconds"] = new JsonArray(lengths.Select(l => (JsonNode?)l).ToArray()),
            ["shortest"] = lengths.Min(), ["longest"] = lengths.Max(), ["fee_each"] = each, ["fee"] = each * clips.Count,
            ["notecard_name"] = ClipsNotecardName(baseName),
        };
        return new ClipPlan(clips, names, baseName, each, json);
    }

    /// <summary>uploadClips: a sound of any length cut into clips of at most clip_seconds (29.9 by default, so none is
    /// refused), named "name 1" to "name N", each made mono at 44.1 kHz with a few milliseconds' fade at each cut, and
    /// uploaded in order: into a new folder named after them (new_folder, on by default) in folder_id (the Sounds
    /// folder by default), or straight into folder_id. notecard: a notecard beside them listing the clips in order, a
    /// line each, as players read. The whole set's fee is confirmed once (`max_cost` the total). A clip that fails
    /// stops the rest, said with which went up.</summary>
    public async Task<JsonNode?> UploadClipsAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var path = Existing(a);
        if (Kind(a, path) != "sound") throw new LeapError($"{Path.GetFileName(path)} isn't a sound; only a sound is cut into clips.");
        var (facts, mono) = await Task.Run(() => Decoded(path), ct);
        var plan = ClipsPlan(a, path, mono);
        int n = plan.Clips.Count, total = plan.Each * n;
        var reply = new JsonObject
        {
            ["name"] = plan.Base, ["count"] = n, ["seconds"] = Math.Round(facts.Seconds, 2), ["cost_each"] = plan.Each, ["cost"] = total,
            ["plan"] = plan.Json,
        };
        if (a.Bool("quote", false)) return reply;
        HoldTo(a, total);
        var into = a.Uuid("folder_id") ?? _s.Inventory.FolderFor(FolderType.Sound);
        var folder = into;
        bool newFolder = a.Bool("new_folder", true);
        if (newFolder) folder = c.Inventory.CreateFolder(into, plan.Base);
        var desc = Cut(a.Str("desc") ?? "", DescLength);
        var d = _s.Ownership.DefaultsFor("uploads");
        var next = a.Str("next_perms") is { } np ? PermissionMask.Move | ObjectViews.PermMask(np) : d.NextMask;
        var perms = new Permissions((uint)PermissionMask.All, (uint)d.EveryoneMask, (uint)d.GroupMask, (uint)next, (uint)PermissionMask.All);
        var timeout = TimeSpan.FromSeconds(a.Num("timeout", 120));
        var made = new JsonArray();
        for (int i = 0; i < n; i++)
        {
            var clip = plan.Clips[i];
            var name = plan.Names[i];
            string? why = null;
            try
            {
                var samples = SoundClips.Cut(mono, clip, SoundConvert.SampleRate, fadeIn: i > 0, fadeOut: i < n - 1);
                var data = await Task.Run(() => SoundConvert.EncodeMono(samples), ct);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                var r = await c.Inventory.CreateItemFromAssetAsync(data, name, desc, AssetType.Sound, InventoryType.Sound, folder, perms, cts.Token);
                if (!r.Success) why = Failed(r.Status, r.Error, r.RawResult, tail: false);
                else
                {
                    _s.Ears.Learn(r.AssetID, name);
                    made.Add(new JsonObject
                    {
                        ["name"] = name, ["seconds"] = Math.Round((double)clip.Length / SoundConvert.SampleRate, 2),
                        ["item_id"] = r.ItemID.ToString(), ["asset_id"] = r.AssetID.ToString(),
                    });
                    ClipUploaded?.Invoke(new JsonObject { ["index"] = i + 1, ["count"] = n, ["name"] = name, ["item_id"] = r.ItemID.ToString() });
                }
            }
            catch (OperationCanceledException) { why = Failed("canceled", null, null, tail: false); }
            catch (Exception e) when (e is not NotConnected) { why = Failed(null, e, null, tail: false); }
            if (why == null) continue;
            var where = Node(folder, newFolder ? plan.Base : null);
            if (i == 0 && newFolder)
                try { await c.Inventory.RemoveFolderAsync(folder, CancellationToken.None); } catch (Exception) { }
            var paid = plan.Each > 0 ? $"; L${plan.Each * i} paid" : "";
            throw new LeapError(i == 0
                ? $"Clip 1 of {n}, {name}, didn't go up: {why} Nothing was uploaded."
                : $"Clip {i + 1} of {n}, {name}, didn't go up: {why} {UploadWords.Clips(i)} went up, into {where}{paid}. Nothing more was uploaded.");
        }
        reply["clips"] = made;
        reply["folder_id"] = folder.ToString();
        reply["folder_name"] = Node(folder, newFolder ? plan.Base : null);
        reply["new_folder"] = newFolder;
        reply["parent_id"] = into.ToString();
        reply["parent_name"] = Node(into, null);
        reply["charged"] = total;
        lock (_decodedLock) _decoded = null;   // (a long song's samples aren't kept once it's up)
        if (a.Bool("notecard", false))
        {
            var nc = ClipsNotecardName(plan.Base);
            try
            {
                var note = await _s.InventoryOps.CreateNotecardAsync(new JsonObject
                {
                    ["name"] = nc, ["folder_id"] = folder.ToString(), ["text"] = string.Join("\n", plan.Names) + "\n",
                    ["desc"] = Cut($"The clips of {plan.Base}, in the order they play", DescLength),
                }, ct);
                reply["notecard_id"] = note.Str("item_id");
                reply["notecard_name"] = nc;
            }
            catch (Exception e) when (e is not NotConnected)
            {
                reply["notecard_error"] = $"The notecard listing them wasn't made: {Sentence(e.Message)}";
            }
        }
        return reply;
    }

    string Node(UUID id, string? fallback) => _s.Inventory.Node(id)?.Name ?? fallback ?? "your inventory";

    /// <summary>A model's fee: the grid's quote, or the test's pretend fee.</summary>
    static int Charged(int quoted) => TestFees?.TryGetValue("mesh", out var t) == true ? t : quoted;

    /// <summary>Why an upload failed, in words: the grid's own message where it gave one, the common causes said plainly.</summary>
    /// <summary>Failed's words for one of several uploads: without its "nothing was uploaded", which the caller says.</summary>
    static string Failed(string? status, Exception? error, LibreMetaverse.StructuredData.OSD? raw, bool tail)
    {
        var s = Failed(status, error, raw);
        return tail ? s : s.Replace("; nothing was uploaded.", ".").Replace(" Nothing was uploaded.", "").Replace("Nothing was uploaded. ", "").Trim();
    }

    static string Failed(string? status, Exception? error, LibreMetaverse.StructuredData.OSD? raw)
    {
        string? said = null;
        if (raw is LibreMetaverse.StructuredData.OSDMap map)
        {
            if (map.TryGetValue("message", out var msg) && msg.AsString() is { Length: > 0 } ms) said = ms;
            else if (map.TryGetValue("error", out var err) && err is LibreMetaverse.StructuredData.OSDMap em && em.TryGetValue("message", out var m2)) said = m2.AsString();
        }
        var all = $"{status} {said} {error?.Message}".ToLowerInvariant();
        if (all.Contains("insufficient") || all.Contains("funds") || all.Contains("not enough"))
            return "There isn't enough L$ for the upload; nothing was uploaded.";
        if (all.Contains("capability_missing"))
            return "This region isn't taking uploads just now; nothing was uploaded. Try again in a moment, or in another region.";
        if (all.Contains("mesh") && (all.Contains("permission") || all.Contains("not allowed") || all.Contains("tutorial") || all.Contains("agreement")))
            return "Second Life lets an account upload models only once its owner has done the mesh upload steps on the Second Life website "
                   + "(payment information on file, and the tutorial on intellectual property). Nothing was uploaded."
                   + (said is { Length: > 0 } ? $" The grid said: {said}" : "");
        if (error is OperationCanceledException or TimeoutException || all.Contains("timed out") || all.Contains("canceled"))
            return "The upload took too long and was stopped; nothing was uploaded. Try again.";
        return $"The upload didn't go through{(said is { Length: > 0 } ? $": {Sentence(said)}" : $" ({status}{(error is null ? "" : ": " + error.Message)}).")} Nothing was uploaded.";
    }
}

/// <summary>Numbers as a person hears them in the upload ops' words.</summary>
public static class UploadWords
{
    public static string Bytes(long b) => b >= 1 << 20 ? $"{b / 1048576.0:0.#} MB".Replace(".0 ", " ") : b >= 1024 ? $"{b / 1024} KB" : $"{b} bytes";
    public static string Seconds(double s)
    {
        if (s < 60) return s == 1 ? "1 second" : $"{s.ToString("0.#", CultureInfo.InvariantCulture)} seconds";
        int m = (int)(s / 60), r = (int)(s % 60);
        var words = m == 1 ? "1 minute" : $"{m} minutes";
        return r == 0 ? words : $"{words} {r} second{(r == 1 ? "" : "s")}";
    }
    /// <summary>"Clip 1", "Clips 1 and 2", "Clips 1 to 4": the first n clips.</summary>
    public static string Clips(int n) => n switch { 1 => "Clip 1", 2 => "Clips 1 and 2", _ => $"Clips 1 to {n}" };
    public static string Metres(double m) => $"{m.ToString(m < 10 ? "0.##" : "0.#", CultureInfo.InvariantCulture)} metres";
}
