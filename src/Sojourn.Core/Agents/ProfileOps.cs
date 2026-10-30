using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Profiles and picks, as the viewer's profile floater shows and edits them (sojourn's TesselAgent profile ops).
/// Reading anyone's; editing my own. Written by Claude Opus 5.5, 2026-10-16. On OpenSim this needs the grid's
/// UserProfiles service (without it the region answers nothing, or an empty profile).
/// </summary>
public sealed class ProfileOps
{
    readonly AgentSession _s;
    public ProfileOps(AgentSession s) => _s = s;
    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);

    Task<Avatar.AvatarProperties> PropsAsync(GridClient c, UUID id, CancellationToken ct) =>
        GroupOps.AskAsync<AvatarPropertiesReplyEventArgs, Avatar.AvatarProperties>(
            h => c.Avatars.AvatarPropertiesReply += h, h => c.Avatars.AvatarPropertiesReply -= h,
            e => (e.AvatarID == id, e.Properties), () => c.Avatars.RequestAvatarProperties(id), "profile (the grid's profile service may be off)", ct, Wait);

    async Task<Dictionary<UUID, string>> PicksOfAsync(GridClient c, UUID id, CancellationToken ct)
    {
        try
        {
            return await GroupOps.AskAsync<AvatarPicksReplyEventArgs, Dictionary<UUID, string>>(
                h => c.Avatars.AvatarPicksReply += h, h => c.Avatars.AvatarPicksReply -= h,
                e => (e.AvatarID == id, e.Picks), () => c.Avatars.RequestAvatarPicks(id), "picks", ct, TimeSpan.FromSeconds(5));
        }
        catch (LeapError) { return new(); }  // nobody with picks may get no answer at all
    }

    public async Task<JsonObject> ProfileAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var id = a.Uuid("id") ?? c.Self.AgentID;
        // the interests come in their own reply to the same request: listen before asking
        var got = new TaskCompletionSource<Avatar.Interests>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnInterests(object? _, AvatarInterestsReplyEventArgs e) { if (e.AvatarID == id) got.TrySetResult(e.Interests); }
        c.Avatars.AvatarInterestsReply += OnInterests;
        Avatar.AvatarProperties p;
        Avatar.Interests? interests = null;
        try
        {
            p = await PropsAsync(c, id, ct);
            try { interests = await got.Task.WaitAsync(TimeSpan.FromSeconds(1.5), ct); } catch (TimeoutException) { }
        }
        finally { c.Avatars.AvatarInterestsReply -= OnInterests; }
        var picks = await PicksOfAsync(c, id, ct);
        await _s.Names.ResolveAsync(c, new[] { id, p.Partner }.Where(x => x != UUID.Zero), TimeSpan.FromSeconds(3), ct);
        var o = new JsonObject
        {
            ["id"] = id.ToString(), ["name"] = _s.Names.Complete(id, id.ToString()), ["about"] = p.AboutText ?? "",
            ["image_id"] = p.ProfileImage.ToString(), ["first_life"] = p.FirstLifeText ?? "", ["first_life_image_id"] = p.FirstLifeImage.ToString(),
            ["born"] = p.BornOn ?? "", ["url"] = p.ProfileURL ?? "",
            ["picks"] = new JsonArray(picks.Select(kv => (JsonNode)new JsonObject { ["pick_id"] = kv.Key.ToString(), ["name"] = kv.Value }).ToArray()),
        };
        if (p.Partner != UUID.Zero) o["partner"] = _s.Names.Complete(p.Partner, p.Partner.ToString());
        if (interests is { } i)
            o["interests"] = new JsonObject { ["want_to"] = i.WantToText ?? "", ["skills"] = i.SkillsText ?? "", ["languages"] = i.LanguagesText ?? "" };
        return o;
    }

    public async Task<JsonObject> SetProfileAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var p = await PropsAsync(c, c.Self.AgentID, ct);
        if (a.Str("about") is { } about) p.AboutText = about;
        if (a.Str("first_life") is { } fl) p.FirstLifeText = fl;
        if (a.Str("url") is { } url) p.ProfileURL = url;
        if (a.Has("image_id")) p.ProfileImage = await TextureAsync(a.Str("image_id"), ct);
        if (a.Has("first_life_image_id")) p.FirstLifeImage = await TextureAsync(a.Str("first_life_image_id"), ct);
        c.Self.UpdateProfile(p);
        if (a.Str("want_to") is not null || a.Str("skills") is not null || a.Str("languages") is not null)
        {
            c.Self.UpdateInterests(new Avatar.Interests
            {
                WantToText = a.Str("want_to") ?? "", SkillsText = a.Str("skills") ?? "", LanguagesText = a.Str("languages") ?? "",
            });
        }
        await Task.Delay(800, ct);
        return await ProfileAsync(new JsonObject(), ct);
    }

    /// <summary>A texture: an asset id, or one of my texture items (its asset), or none.</summary>
    async Task<UUID> TextureAsync(string? s, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(s) || s == "none") return UUID.Zero;
        if (!UUID.TryParse(s, out var id)) throw new LeapError($"{s} isn't a texture id");
        if (await _s.Inventory.ItemAsync(id, ct) is { } item)
        {
            if (item.AssetType != AssetType.Texture) throw new LeapError($"{item.Name} isn't a texture");
            return item.AssetUUID;
        }
        return id;
    }

    public async Task<JsonObject> PickAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var who = a.Uuid("id") ?? c.Self.AgentID;
        var pid = a.Uuid("pick_id") ?? throw new LeapError("need pick_id");
        var p = await GroupOps.AskAsync<PickInfoReplyEventArgs, ProfilePick>(
            h => c.Avatars.PickInfoReply += h, h => c.Avatars.PickInfoReply -= h,
            e => (e.PickID == pid, e.Pick), () => c.Avatars.RequestPickInfo(who, pid), "pick", ct, Wait);
        var (rx, ry) = ((int)(p.PosGlobal.X / 256), (int)(p.PosGlobal.Y / 256));
        return new JsonObject
        {
            ["pick_id"] = p.PickID.ToString(), ["name"] = p.Name, ["desc"] = p.Desc, ["region"] = p.SimName,
            ["parcel"] = p.OriginalName, ["pos"] = Json.Arr(p.PosGlobal.X - rx * 256, p.PosGlobal.Y - ry * 256, p.PosGlobal.Z),
            ["image_id"] = p.SnapshotID.ToString(), ["creator_id"] = p.CreatorID.ToString(),
        };
    }

    /// <summary>A pick of where I stand (as the profile's New Pick: this parcel, this spot).</summary>
    public async Task<JsonObject> CreatePickAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var sim = c.Network.CurrentSim ?? throw new LeapError("no region");
        var pos = c.Self.SimPosition;
        var name = a.Str("name");
        var local = c.Parcels.GetParcelLocalID(sim, pos);
        var parcelName = local != 0 && sim.Parcels.TryGetValue(local, out var parcel) ? parcel.Name : null;
        UUID parcelId = UUID.Zero;
        try { parcelId = await c.Parcels.RequestRemoteParcelIDAsync(pos, sim.Handle, sim.ID, ct); } catch { }
        var pickId = a.Uuid("pick_id") ?? UUID.Random();
        c.Self.PickInfoUpdate(pickId, false, parcelId, name ?? parcelName ?? sim.Name, c.Self.GlobalPosition,
            await TextureAsync(a.Str("image_id"), ct), a.Str("desc") ?? "");
        await Task.Delay(800, ct);
        return new JsonObject { ["pick_id"] = pickId.ToString(), ["name"] = name ?? parcelName ?? sim.Name, ["region"] = sim.Name, ["pos"] = Json.Arr(pos) };
    }

    public JsonObject DeletePick(JsonObject a)
    {
        C.Self.PickDelete(a.Uuid("pick_id") ?? throw new LeapError("need pick_id"));
        return new JsonObject();
    }
}
