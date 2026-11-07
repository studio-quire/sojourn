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

    /// <summary>The profile as the grid sends it: its properties, then in their own replies to the same request its
    /// interests and groups, listened for before asking. Grids that send either do it within a moment of the rest.</summary>
    async Task<(Avatar.AvatarProperties Props, Avatar.Interests? Interests, List<AvatarGroup> Groups)> ReadAsync(GridClient c, UUID id, CancellationToken ct)
    {
        var interests = new TaskCompletionSource<Avatar.Interests>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstGroups = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var groups = new List<AvatarGroup>();
        void OnInterests(object? _, AvatarInterestsReplyEventArgs e) { if (e.AvatarID == id) interests.TrySetResult(e.Interests); }
        void OnGroups(object? _, AvatarGroupsReplyEventArgs e)
        {
            if (e.AvatarID != id) return;
            lock (groups) groups.AddRange(e.Groups);
            firstGroups.TrySetResult();
        }
        c.Avatars.AvatarInterestsReply += OnInterests;
        c.Avatars.AvatarGroupsReply += OnGroups;
        try
        {
            var p = await PropsAsync(c, id, ct);
            try { await Task.WhenAll(interests.Task, firstGroups.Task).WaitAsync(TimeSpan.FromSeconds(1.5), ct); } catch (TimeoutException) { }
            // (a long list of groups comes in several packets)
            if (firstGroups.Task.IsCompleted) await Task.Delay(250, ct);
            lock (groups) return (p, interests.Task.IsCompleted ? interests.Task.Result : null, groups.ToList());
        }
        finally
        {
            c.Avatars.AvatarInterestsReply -= OnInterests;
            c.Avatars.AvatarGroupsReply -= OnGroups;
        }
    }

    public async Task<JsonObject> ProfileAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var id = a.Uuid("id") ?? c.Self.AgentID;
        var (p, interests, groups) = await ReadAsync(c, id, ct);
        var picks = await PicksOfAsync(c, id, ct);
        await _s.Names.ResolveAsync(c, new[] { id, p.Partner }.Where(x => x != UUID.Zero), TimeSpan.FromSeconds(3), ct);
        var o = new JsonObject
        {
            ["id"] = id.ToString(), ["name"] = _s.Names.Complete(id, id.ToString()), ["about"] = p.AboutText ?? "",
            ["image_id"] = p.ProfileImage.ToString(), ["first_life"] = p.FirstLifeText ?? "", ["first_life_image_id"] = p.FirstLifeImage.ToString(),
            ["born"] = p.BornOn ?? "", ["url"] = p.ProfileURL ?? "",
            ["picks"] = new JsonArray(picks.Select(kv => (JsonNode)new JsonObject { ["pick_id"] = kv.Key.ToString(), ["name"] = kv.Value }).ToArray()),
            ["groups"] = new JsonArray(groups.Select(g => (JsonNode)new JsonObject { ["group_id"] = g.GroupID.ToString(), ["name"] = g.GroupName ?? "", ["title"] = g.GroupTitle ?? "" }).ToArray()),
        };
        if (p.Partner != UUID.Zero) o["partner"] = _s.Names.Complete(p.Partner, p.Partner.ToString());
        if (interests is { } i)
            o["interests"] = new JsonObject { ["want_to"] = i.WantToText ?? "", ["skills"] = i.SkillsText ?? "", ["languages"] = i.LanguagesText ?? "" };
        return o;
    }

    /// <summary>Bytes the grid's one-byte length fields hold, with their ending zero: over it the message can't be built.</summary>
    const int ShortField = 254;

    static void Fits(string? text, string what)
    {
        if (text != null && System.Text.Encoding.UTF8.GetByteCount(text) > ShortField)
            throw new LeapError($"{what} holds {ShortField} characters at most");
    }

    public async Task<JsonObject> SetProfileAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        Fits(a.Str("url"), "the web address");
        Fits(a.Str("want_to"), "wants to");
        Fits(a.Str("skills"), "skills");
        Fits(a.Str("languages"), "languages");
        var (p, had, _) = await ReadAsync(c, c.Self.AgentID, ct);
        if (a.Str("about") is { } about) p.AboutText = about;
        if (a.Str("first_life") is { } fl) p.FirstLifeText = fl;
        if (a.Str("url") is { } url) p.ProfileURL = url;
        if (a.Has("image_id")) p.ProfileImage = await TextureAsync(a.Str("image_id"), ct);
        if (a.Has("first_life_image_id")) p.FirstLifeImage = await TextureAsync(a.Str("first_life_image_id"), ct);
        c.Self.UpdateProfile(p);
        if (a.Str("want_to") is not null || a.Str("skills") is not null || a.Str("languages") is not null)
        {
            // what wasn't given stays, and the check boxes other viewers keep with these words
            var was = had ?? new Avatar.Interests();
            was.WantToText = a.Str("want_to") ?? was.WantToText ?? "";
            was.SkillsText = a.Str("skills") ?? was.SkillsText ?? "";
            was.LanguagesText = a.Str("languages") ?? was.LanguagesText ?? "";
            c.Self.UpdateInterests(was);
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
            // (the grid keeps a picture's asset back from someone without full rights to it: zero would clear the picture)
            if (item.AssetUUID == UUID.Zero) throw new LeapError($"the grid doesn't let {item.Name} be used: it needs full rights to the picture");
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

    /// <summary>A pick of where I stand (as the profile's New Pick: this parcel, this spot). With pick_id, that pick edited,
    /// what isn't given staying as it is, its place too unless here.</summary>
    public async Task<JsonObject> CreatePickAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var sim = c.Network.CurrentSim ?? throw new LeapError("no region");
        if (a.Uuid("pick_id") is { } editing && !a.Bool("here", false))
        {
            var p = await GroupOps.AskAsync<PickInfoReplyEventArgs, ProfilePick>(
                h => c.Avatars.PickInfoReply += h, h => c.Avatars.PickInfoReply -= h,
                e => (e.PickID == editing, e.Pick), () => c.Avatars.RequestPickInfo(c.Self.AgentID, editing), "pick", ct, Wait);
            var image = a.Has("image_id") ? await TextureAsync(a.Str("image_id"), ct) : p.SnapshotID;
            c.Self.PickInfoUpdate(editing, p.TopPick, p.ParcelID, a.Str("name") ?? p.Name, p.PosGlobal, image, a.Str("desc") ?? p.Desc);
            await Task.Delay(800, ct);
            return new JsonObject { ["pick_id"] = editing.ToString(), ["name"] = a.Str("name") ?? p.Name, ["region"] = p.SimName };
        }
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
