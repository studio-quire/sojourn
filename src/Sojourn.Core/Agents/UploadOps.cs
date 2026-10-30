using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Imaging.Skia;
using LibreMetaverse.ImportExport;
using Sojourn.Core.Leap;
using SkiaSharp;

namespace Sojourn.Core.Agents;

/// <summary>
/// TesselAgent upload (textures, sounds, animations) and uploadMesh, through the normal NewFileAgentInventory
/// upload, so the grid's normal fee applies. No spending caps here (operation, not code); `quote` asks the price
/// without uploading.
/// </summary>
public sealed class UploadOps
{
    readonly AgentSession _s;
    public UploadOps(AgentSession s) => _s = s;

    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    static readonly string[] TextureExt = { ".png", ".tga", ".bmp", ".jpg", ".jpeg" };

    public async Task<JsonNode?> UploadAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var path = a.Str("path") ?? throw new LeapError("need path");
        if (!File.Exists(path)) throw new LeapError($"no such file: {path}");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var type = a.Str("type") ?? (TextureExt.Contains(ext) ? "texture" : ext is ".wav" or ".ogg" ? "sound" : ext is ".anim" or ".bvh" ? "animation" : "");
        var name = a.Str("name") ?? Path.GetFileNameWithoutExtension(path);
        var desc = a.Str("desc") ?? "";
        var timeout = TimeSpan.FromSeconds(a.Num("timeout", 120));
        byte[] data;
        AssetType at;
        InventoryType it;
        FolderType ft;
        var reply = new JsonObject { ["type"] = type, ["name"] = name };
        switch (type)
        {
            case "texture":
            {
                var maxSize = Math.Clamp(a.Int("max_size", 1024), 64, 2048);
                using var src = SKBitmap.Decode(path) ?? throw new LeapError($"can't read image {path}");
                int Pow2(int v) { var p = 1; while (p * 2 <= Math.Min(v, maxSize)) p *= 2; return Math.Max(p, 16); }
                var (w, h) = (Pow2(src.Width), Pow2(src.Height));
                using var scaled = src.Width == w && src.Height == h ? src.Copy() : src.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
                var tex = new AssetTexture(SkiaTextureCodec.ToManagedImage(scaled));
                tex.Encode();
                data = tex.AssetData;
                (at, it, ft) = (AssetType.Texture, InventoryType.Texture, FolderType.Texture);
                reply["width"] = w;
                reply["height"] = h;
                break;
            }
            case "sound":
                data = await File.ReadAllBytesAsync(path, ct);
                if (ext == ".wav")
                {
                    // as the viewer's converter: mono Ogg Vorbis at 44.1 kHz, up to 30 s (more formats accepted)
                    try
                    {
                        var (ogg, info) = Media.SoundConvert.WavToVorbis(data);
                        data = ogg;
                        reply["duration"] = Math.Round(info.Duration, 2);
                        reply["converted"] = $"{info.SourceChannels} ch {info.SourceSampleRate} Hz -> mono 44100 Hz Vorbis";
                    }
                    catch (Media.MediaFormatException e) { throw new LeapError($"sound: {e.Message}"); }
                }
                else if (ext != ".ogg") throw new LeapError("sound: give a .wav (converted here) or an Ogg Vorbis .ogg");
                (at, it, ft) = (AssetType.Sound, InventoryType.Sound, FolderType.Sound);
                break;
            case "animation":
                data = await File.ReadAllBytesAsync(path, ct);
                if (ext == ".bvh")
                {
                    // the viewer's animation upload floater, with its options as args
                    var o = new Media.AnimOptions();
                    if (a.Num("priority") is { } pr) o = o with { Priority = (int)pr };
                    if (a.Bool("loop") is { } lp) o = o with { Loop = lp };
                    if (a.Num("loop_in") is { } li) o = o with { LoopInPercent = li };
                    if (a.Num("loop_out") is { } lo) o = o with { LoopOutPercent = lo };
                    if (a.Num("ease_in") is { } ei) o = o with { EaseIn = ei };
                    if (a.Num("ease_out") is { } eo) o = o with { EaseOut = eo };
                    if (a.Str("hand_pose") is { } hp) o = o with { HandPose = hp };
                    if (a.Str("emote") is { } em) o = o with { Emote = em };
                    try
                    {
                        var conv = Media.AnimConvert.Convert(System.Text.Encoding.UTF8.GetString(data), o);
                        data = conv.Data;
                        reply["duration"] = Math.Round(conv.Anim.Duration, 2);
                        reply["joints"] = conv.Anim.Joints.Count;
                        if (conv.Warnings.Count > 0) reply["warnings"] = Json.Strings(conv.Warnings);
                    }
                    catch (Media.MediaFormatException e) { throw new LeapError($"animation: {e.Message}"); }
                }
                else if (ext != ".anim") throw new LeapError("animation: give a .bvh (converted here) or a .anim");
                (at, it, ft) = (AssetType.Animation, InventoryType.Animation, FolderType.Animation);
                break;
            default:
                throw new LeapError($"can't tell what to upload {path} as; give type texture|sound|animation");
        }
        var cost = c.Settings.UploadCost;
        reply["cost"] = cost;
        if (a.Bool("quote", false)) return reply;
        var folder = a.Uuid("folder_id") ?? _s.Inventory.FolderFor(ft);
        // without next_perms, the person's Default Permissions for uploads (SojournBuild setDefaultPerms; the viewer's own)
        var d = _s.Ownership.DefaultsFor("uploads");
        var next = a.Str("next_perms") is { } np ? PermissionMask.Move | ObjectViews.PermMask(np) : d.NextMask;
        var perms = new Permissions((uint)PermissionMask.All, (uint)d.EveryoneMask, (uint)d.GroupMask, (uint)next, (uint)PermissionMask.All);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var r = await c.Inventory.CreateItemFromAssetAsync(data, name, desc, at, it, folder, perms, cts.Token);
        if (!r.Success) throw new LeapError($"upload failed: {r.Status}{(r.Error is null ? "" : ": " + r.Error.Message)}");
        reply["item_id"] = r.ItemID.ToString();
        reply["asset_id"] = r.AssetID.ToString();
        if (at == AssetType.Sound) _s.Ears.Learn(r.AssetID, name);
        reply["charged"] = cost;
        return reply;
    }

    public async Task<JsonNode?> UploadMeshAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var path = a.Str("path") ?? throw new LeapError("need path");
        if (!File.Exists(path)) throw new LeapError($"no such file: {path}");
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var textures = a.Bool("textures", false);
        IDiagnosticModelLoader loader = ext switch
        {
            ".gltf" or ".glb" => new GltfLoader(new SkiaTextureCodec()),
            ".dae" => new ColladaLoader(),
            _ => throw new LeapError("uploadMesh: give a .dae, .gltf or .glb"),
        };
        var loaded = loader.LoadWithDiagnostics(path, textures);
        var log = string.Join("\n", loaded.Diagnostics.Select(d => d.ToString()));
        if (loaded.HasErrors || loaded.Prims.Count == 0)
            return new JsonObject { ["error"] = "the model didn't load", ["log"] = log };
        var prims = loaded.Prims.ToList();
        if (a.Num("scale") is { } sc && sc > 0)
            foreach (var p in prims) { p.Scale *= (float)sc; p.Position *= (float)sc; }
        var name = a.Str("name") ?? Path.GetFileNameWithoutExtension(path);
        var folder = a.Uuid("folder_id") ?? _s.Inventory.FolderFor(FolderType.Object);
        var d = _s.Ownership.DefaultsFor("uploads");
        var next = a.Str("next_perms") is { } np ? PermissionMask.Move | ObjectViews.PermMask(np) : d.NextMask;
        var perms = new Permissions((uint)PermissionMask.All, (uint)d.EveryoneMask, (uint)d.GroupMask, (uint)next, (uint)PermissionMask.All);
        var quote = a.Bool("quote", false);
        var quoted = -1;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(a.Num("timeout", 180)));
        var r = await c.Inventory.UploadModelAsync(prims, name, a.Str("desc") ?? "", folder, perms,
            confirmCost: price => { quoted = price; return !quote; }, cancellationToken: cts.Token);
        var reply = new JsonObject { ["name"] = name, ["cost"] = Math.Max(0, quoted) };
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
        if (log.Length > 0) reply["log"] = log;
        if (quote)
        {
            if (quoted < 0 && !r.Success) reply["refusal"] = r.Status;
            return reply;
        }
        if (!r.Success) return new JsonObject { ["error"] = $"upload failed: {r.Status}{(r.Error is null ? "" : ": " + r.Error.Message)}", ["log"] = log };
        reply["item_id"] = r.ItemID.ToString();
        reply["asset_id"] = r.AssetID.ToString();
        reply["charged"] = Math.Max(0, quoted);
        return reply;
    }
}
