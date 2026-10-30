using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Imaging.Skia;
using LibreMetaverse.StructuredData;
using SkiaSharp;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Session.Capture;

/// <summary>
/// A picture the person took, sent on: into their inventory (the viewer's Save to Inventory, llpanelsnapshotinventory /
/// LLSnapshotLivePreview::saveTexture: a texture upload into the Photo Album, L$ on Second Life), by email (the viewer's
/// postcard, the region's SendPostcard), or to the web profile's feed (llwebprofile.cpp, which isn't possible yet: see
/// <see cref="ProfileRefusal"/>). Only pictures in Pictures\Sojourn go, named or the last one taken; what each will do
/// is pinned before the person is asked (Capture/CaptureWiring.cs) and shown whole. Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class Sharing
{
    readonly AgentSession _s;
    readonly Func<string?> _last;
    public const int MaxTexture = 2048;
    const int MaxPostcardBytes = 1572864;   // the viewer's MAX_POSTCARD_DATASIZE

    public Sharing(AgentSession s, Func<string?> lastPicture) => (_s, _last) = (s, lastPicture);

    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    /// <summary>The picture an op means: `path` inside Pictures\Sojourn, else the last one taken this session.</summary>
    public string Picture(JsonObject a)
    {
        var dir = Path.GetFullPath(Assistant.CaptureApi.PicturesDir) + Path.DirectorySeparatorChar;
        if (a.Str("path") is { Length: > 0 } p)
        {
            var full = Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(dir, p));
            if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) throw new LeapError("path: only pictures in Pictures\\Sojourn go (the ones snapshot and panorama save)");
            if (!File.Exists(full)) throw new LeapError($"no such picture: {Path.GetFileName(full)}");
            return full;
        }
        return _last() is { } last && File.Exists(last) ? last : throw new LeapError("no picture taken yet this session: take one, or give its path");
    }

    // --- inventory ----------------------------------------------------------------------------------------------------

    public sealed record InventoryPlan(string Path, string Name, string Desc, int Width, int Height, int Cost);

    /// <summary>What a save to inventory will do: the texture's size (each side a power of two, as the viewer's
    /// biasedScaleToPowerOfTwo, at most 2048), its name and description as the viewer writes them, and its cost.</summary>
    public InventoryPlan PlanInventory(JsonObject a)
    {
        var path = Picture(a);
        var (pw, ph) = ImageSize(path);
        int w = pw, h = ph;
        if (a.Str("size") is { Length: > 0 } s)
        {
            var parts = s.ToLowerInvariant().Split('x');
            if (parts.Length != 2 || !int.TryParse(parts[0], out w) || !int.TryParse(parts[1], out h))
                throw new LeapError("size: WIDTHxHEIGHT (the viewer's: 128x128, 256x256, 512x512, 1024x512, 1024x1024, 2048x1024, 2048x2048)");
        }
        (w, h) = (Biased(Math.Clamp(a.Int("width", w), 1, 8192)), Biased(Math.Clamp(a.Int("height", h), 1, 8192)));
        var where = Location();
        var name = a.Str("name") is { Length: > 0 } n ? n : $"Snapshot : {where}";
        var desc = a.Str("desc") ?? $"Taken by {Me()} at {where}";
        if (name.Length > 63) name = name[..63];
        if (desc.Length > 127) desc = desc[..127];
        return new InventoryPlan(path, name, desc, w, h, Cost(w, h));
    }

    public async Task<JsonObject> ToInventoryAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var plan = PlanInventory(a);
        byte[] data = await Task.Run(() =>
        {
            using var src = SKBitmap.Decode(plan.Path) ?? throw new LeapError($"can't read the picture {Path.GetFileName(plan.Path)}");
            using var scaled = src.Width == plan.Width && src.Height == plan.Height ? src.Copy()
                : src.Resize(new SKImageInfo(plan.Width, plan.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
            var tex = new AssetTexture(SkiaTextureCodec.ToManagedImage(scaled));
            tex.Encode();
            return tex.AssetData;
        }, ct);
        var folder = _s.Inventory.FolderFor(FolderType.Snapshot);
        // the viewer's: everything for the next owner, the group's and everyone's from the person's upload defaults
        var d = _s.Ownership.DefaultsFor("uploads");
        var perms = new Permissions((uint)PermissionMask.All, (uint)d.EveryoneMask, (uint)d.GroupMask, (uint)PermissionMask.All, (uint)PermissionMask.All);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(a.Num("timeout", 120)));
        var r = await c.Inventory.CreateItemFromAssetAsync(data, plan.Name, plan.Desc, AssetType.Texture, InventoryType.Snapshot, folder, perms, cts.Token);
        if (!r.Success) throw new LeapError($"upload failed: {r.Status}{(r.Error is null ? "" : ": " + r.Error.Message)}");
        return new JsonObject
        {
            ["item_id"] = r.ItemID.ToString(), ["asset_id"] = r.AssetID.ToString(), ["name"] = plan.Name, ["folder"] = "Photo Album",
            ["width"] = plan.Width, ["height"] = plan.Height, ["charged"] = plan.Cost, ["from"] = Path.GetFileName(plan.Path),
        };
    }

    /// <summary>The viewer's LLImageRaw::biasedDimToPowerOfTwo: the power of two below, unless the size is over 1.75
    /// times it (a strong bias toward rounding down), at most 2048.</summary>
    static int Biased(int dim)
    {
        int larger = MaxTexture, smaller = MaxTexture;
        while (smaller > dim && smaller > 4) (larger, smaller) = (smaller, smaller >> 1);
        return (float)dim / smaller > 1.75f ? larger : smaller;
    }

    /// <summary>LLAgentBenefits::getTextureUploadCost(w, h): the large texture price over 1024x1024, where the grid has one.</summary>
    int Cost(int w, int h)
    {
        var c = C;
        if ((long)w * h > 1024 * 1024 && c.Self.Benefits?.LargeTextureUploadCost is { Count: > 0 } large) return large[0];
        return Math.Max(0, c.Settings.UploadCost);   // (OpenSim leaves it unknown, -1)
    }

    static (int W, int H) ImageSize(string path)
    {
        using var codec = SKCodec.Create(path) ?? throw new LeapError($"can't read the picture {Path.GetFileName(path)}");
        return (codec.Info.Width, codec.Info.Height);
    }

    /// <summary>LLAgentUI::buildLocationString, LOCATION_FORMAT_FULL: "Parcel, Region (x, y, z) - Rating".</summary>
    string Location()
    {
        var c = C;
        if (c.Network.CurrentSim is not { } sim) return "";
        var p = c.Self.SimPosition;
        string? parcel = null;
        try
        {
            int local = c.Parcels.GetParcelLocalID(sim, p);
            if (local != 0 && sim.Parcels.TryGetValue(local, out var pa) && pa.Name is { Length: > 0 } pn) parcel = pn;
        }
        catch (Exception) { }
        var rating = sim.Access switch { SimAccess.PG => "General", SimAccess.Mature => "Moderate", SimAccess.Adult => "Adult", _ => "" };
        var at = $"{sim.Name} ({(int)p.X}, {(int)p.Y}, {(int)p.Z})";
        return (parcel != null ? $"{parcel}, {at}" : at) + (rating.Length > 0 ? $" - {rating}" : "");
    }

    string Me() => C.Self.Name is { Length: > 0 } n ? n : $"{_s.Config.First} {_s.Config.Last}";

    // --- email --------------------------------------------------------------------------------------------------------

    public sealed record PostcardPlan(string Path, string[] To, string Subject, string Message, int Width, int Height);

    public PostcardPlan PlanPostcard(JsonObject a)
    {
        var path = Picture(a);
        var to = (a.Str("to") ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (to.Length == 0) throw new LeapError("to: the email address (or several, separated by commas)");
        foreach (var t in to)
            if (!System.Text.RegularExpressions.Regex.IsMatch(t, @"^[^@\s]+@[^@\s]+\.[^@\s]+$")) throw new LeapError($"to: {t} isn't an email address");
        var subject = a.Str("subject") is { Length: > 0 } s ? s : "Postcard from Second Life";
        var message = a.Str("message") ?? "Check this out!";
        var (w, h) = ImageSize(path);
        float k = Math.Min(1f, 1024f / Math.Max(w, h));
        return new PostcardPlan(path, to, subject, message, Math.Max(1, (int)(w * k)), Math.Max(1, (int)(h * k)));
    }

    /// <summary>Why a postcard can't go from here, or null: the region offers no SendPostcard (OpenSim grids don't).</summary>
    public string? PostcardRefusal() =>
        _s.Client?.Network.CurrentSim?.Caps?.CapabilityURI("SendPostcard") is null
            ? "this region doesn't send postcards (no SendPostcard capability: Second Life's regions have it, OpenSim grids don't); nothing was sent" : null;

    public async Task<JsonObject> PostcardAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        if (PostcardRefusal() is { } no) throw new LeapError(no);
        var plan = PlanPostcard(a);
        var cap = c.Network.CurrentSim!.Caps!.CapabilityURI("SendPostcard")!;
        byte[] jpeg = await Task.Run(() =>
        {
            using var src = SKBitmap.Decode(plan.Path) ?? throw new LeapError($"can't read the picture {Path.GetFileName(plan.Path)}");
            using var scaled = src.Resize(new SKImageInfo(plan.Width, plan.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
            for (int q = 75; ; q -= 10)
            {
                using var data = scaled.Encode(SKEncodedImageFormat.Jpeg, q);
                if (data.Size <= MaxPostcardBytes || q <= 25) return data.ToArray();
            }
        }, ct);
        var g = c.Self.GlobalPosition;
        var body = new OSDMap
        {
            ["pos-global"] = OSD.FromVector3d(g), ["to"] = OSD.FromString(string.Join(",", plan.To)), ["name"] = OSD.FromString(Me()),
            ["subject"] = OSD.FromString(plan.Subject), ["msg"] = OSD.FromString(plan.Message),
        };
        // LLPostcardUploadInfo: the card, then the picture to the uploader it answers with
        var (_, first) = await c.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, body, ct);
        if (Map(first) is not { } st || st["state"].AsString() != "upload" || st["uploader"].AsString() is not { Length: > 0 } up)
            throw new LeapError($"the region refused the postcard{(Map(first)?["message"].AsString() is { Length: > 0 } m ? ": " + m : "")}; nothing was sent");
        var (_, second) = await c.HttpCapsClient.PostAsync(new Uri(up), "application/octet-stream", jpeg, ct);
        if (Map(second) is not { } done || done["state"].AsString() != "complete")
            throw new LeapError("the picture didn't go up; the postcard may not have been sent");
        return new JsonObject { ["sent"] = true, ["to"] = string.Join(", ", plan.To), ["subject"] = plan.Subject, ["bytes"] = jpeg.Length };
    }

    static OSDMap? Map(byte[]? data)
    {
        if (data is not { Length: > 0 }) return null;
        try { return OSDParser.Deserialize(data) as OSDMap; } catch (Exception) { return null; }
    }

    // --- the profile feed ---------------------------------------------------------------------------------------------

    /// <summary>Why the profile feed can't take a picture. The viewer posts to my.secondlife.com's snapshots upload with
    /// the web session it opens at login (the login reply's openid_url and openid_token, posted for a cookie:
    /// LLViewerMedia::openIDSetup). LibreMetaverse keeps the URL but drops the token, so Sojourn has no web session to
    /// post with; nothing else would be honest (the person's password isn't ours to use on the web).</summary>
    public string ProfileRefusal() =>
        _s.Client?.Network.LoginResponseData?.OpenIDUrl is not { Length: > 0 }
            ? "this grid has no web profile feed; nothing was sent"
            : "the profile feed isn't available yet: it needs the web sign-in Second Life's login gives the viewer, which Sojourn doesn't keep; nothing was sent";
}
