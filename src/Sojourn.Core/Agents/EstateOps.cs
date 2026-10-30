using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Region and estate, as the viewer's World > Region/Estate floater (llfloaterregioninfo.cpp) and Top Objects
/// (llfloatertopobjects.cpp) do it. For anyone: the region's info (RequestRegionInfo, which any agent may ask), the
/// estate's covenant (EstateCovenantRequest, its notecard as an estate asset), the restart schedule where the grid has
/// it. For the estate's owner and managers: the estate's lists (the EstateAccess capability, else EstateOwnerMessage
/// getinfo's setaccess replies), managers, allowed residents and groups and bans changed (estateaccessdelta), someone
/// kicked from the estate or sent home, a message to everyone in the region or estate, the region's settings, a
/// restart (or its cancel), the top scripts and colliders (LandStatRequest) and returning them, disabling scripts,
/// collisions and physics in an emergency (setregiondebug), baking the terrain. The region refuses what my powers don't
/// allow without a word, so changes are read back where they can be. Every change is gated (Client/Land/LandWiring.cs).
/// Claude Opus 5.5, 2026-10-28 (batch B5).
/// </summary>
public sealed class EstateOps
{
    readonly AgentSession _s;
    public EstateOps(AgentSession s) => _s = s;
    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");
    Simulator Sim => C.Network.CurrentSim ?? throw new LeapError("no region");

    static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);

    /// <summary>Whether I may manage this region's estate (llviewerregion canManageEstate: its owner or a manager).</summary>
    public bool CanManage => _s.Client is { } c && c.Network.CurrentSim is { } sim && (sim.IsEstateManager || sim.SimOwner == c.Self.AgentID);

    void NeedManager(string what)
    {
        if (!CanManage) throw new LeapError($"{what} is for this estate's owner and managers");
    }

    // --- the region, for anyone ----------------------------------------------------------------------------------

    /// <summary>The RegionInfo the region answers RequestRegionInfo with (the floater's General and Debug tabs).</summary>
    public async Task<RegionInfoPacket> RegionInfoAsync(CancellationToken ct)
    {
        var c = C;
        var sim = Sim;
        var tcs = new TaskCompletionSource<RegionInfoPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(object? _, PacketReceivedEventArgs e)
        {
            if (e.Simulator == sim && e.Packet is RegionInfoPacket p) tcs.TrySetResult(p);
        }
        c.Network.RegisterCallback(PacketType.RegionInfo, On);
        try
        {
            c.Network.SendPacket(new RequestRegionInfoPacket { AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID } }, sim);
            return await tcs.Task.WaitAsync(Wait, ct);
        }
        catch (TimeoutException) { throw new LeapError("the region didn't describe itself"); }
        finally { c.Network.UnregisterCallback(PacketType.RegionInfo, On); }
    }

    static string Maturity(byte access) => access switch { 13 => "general", 21 => "moderate", 42 => "adult", _ => access.ToString() };

    static ulong FlagsOf(RegionInfoPacket p) =>
        p.RegionInfo3 is { Length: > 0 } ext ? ext[0].RegionFlagsExtended : p.RegionInfo.RegionFlags;

    static bool Has(ulong flags, RegionFlags f) => (flags & (ulong)f) != 0;
    const ulong BlockFlyOver = 1UL << 27;   // REGION_FLAGS_BLOCK_FLYOVER (LibreMetaverse calls it AbuseEmailToEstateOwner)

    public async Task<JsonObject> RegionAsync(CancellationToken ct)
    {
        var sim = Sim;
        var p = await RegionInfoAsync(ct);
        var ri = p.RegionInfo;
        ulong f = FlagsOf(p);
        var o = new JsonObject
        {
            ["name"] = Utils.BytesToString(ri.SimName),
            ["type"] = p.RegionInfo2.ProductName is { Length: > 0 } pn ? Utils.BytesToString(pn) : sim.ProductName,
            ["maturity"] = Maturity(ri.SimAccess),
            ["estate_id"] = ri.EstateID,
            ["agent_limit"] = p.RegionInfo2.MaxAgents32 > 0 ? (int)p.RegionInfo2.MaxAgents32 : ri.MaxAgents,
            ["agent_limit_max"] = p.RegionInfo2.HardMaxAgents,
            ["object_bonus"] = Json.R(ri.ObjectBonusFactor, 2),
            ["water_height"] = Json.R(ri.WaterHeight, 2),
            ["terrain_raise_limit"] = Json.R(ri.TerrainRaiseLimit, 2), ["terrain_lower_limit"] = Json.R(ri.TerrainLowerLimit, 2),
            ["grid_position"] = new JsonArray((int)(sim.Handle >> 32) / 256, (int)(sim.Handle & 0xFFFFFFFF) / 256),
            ["settings"] = new JsonObject
            {
                ["block_terraform"] = Has(f, RegionFlags.BlockTerraform), ["block_fly"] = Has(f, RegionFlags.NoFly),
                ["block_fly_over"] = (f & BlockFlyOver) != 0, ["allow_damage"] = Has(f, RegionFlags.AllowDamage),
                ["restrict_push"] = Has(f, RegionFlags.RestrictPushObject), ["allow_land_resell"] = !Has(f, RegionFlags.BlockLandResell),
                ["allow_parcel_changes"] = Has(f, RegionFlags.AllowParcelChanges), ["block_parcel_search"] = Has(f, RegionFlags.BlockParcelSearch),
                ["sandbox"] = Has(f, RegionFlags.Sandbox),
            },
            ["debug"] = new JsonObject
            {
                ["scripts_disabled"] = Has(f, RegionFlags.SkipScripts), ["collisions_disabled"] = Has(f, RegionFlags.SkipCollisions),
                ["physics_disabled"] = Has(f, RegionFlags.SkipPhysics),
            },
            ["i_manage"] = CanManage,
        };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            if (Sim.Caps?.CapabilityURI("RegionSchedule") != null && await C.Estate.GetRegionRestartScheduleAsync(cts.Token) is { } rs)
                o["restart_schedule"] = Schedule(rs);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        return o;
    }

    static JsonNode? Schedule(object rs)
    {
        // RegionRestartSchedule: whatever it carries, as the grid gave it
        try { return JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(rs, rs.GetType(), new System.Text.Json.JsonSerializerOptions { IncludeFields = true })); }
        catch { return rs.ToString(); }
    }

    /// <summary>The estate's covenant (About Land > Covenant): its notecard's text, who owns the estate, when it changed.</summary>
    public async Task<JsonObject> CovenantAsync(CancellationToken ct)
    {
        var c = C;
        var e = await GroupOps.AskAsync<EstateCovenantReplyEventArgs, EstateCovenantReplyEventArgs>(
            h => c.Estate.EstateCovenantReply += h, h => c.Estate.EstateCovenantReply -= h,
            x => (true, x), () => c.Estate.RequestCovenant(), "the covenant", ct, Wait);
        await _s.Names.ResolveAsync(c, new[] { e.EstateOwnerID }, TimeSpan.FromSeconds(3), ct);
        var o = new JsonObject
        {
            ["estate"] = e.EstateName, ["estate_owner"] = _s.Names.Complete(e.EstateOwnerID, e.EstateOwnerID.ToString()),
            ["estate_owner_id"] = e.EstateOwnerID.ToString(),
        };
        if (e.CovenantID == UUID.Zero)
        {
            o["covenant"] = "none: no covenant for this estate";
            return o;
        }
        if (e.Timestamp > 0) o["changed"] = DateTimeOffset.FromUnixTimeSeconds(e.Timestamp).ToLocalTime().ToString("d MMM yyyy HH:mm");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        Asset? asset = null;
        try { asset = await c.Estate.RequestCovenantNotecardAsync(e.CovenantID, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        if (asset is AssetNotecard note)
        {
            note.Decode();
            var text = note.BodyText ?? "";
            o["covenant"] = text.Length > 12000 ? text[..12000] + "..." : text;
        }
        else o["covenant"] = "its notecard didn't come";
        return o;
    }

    // --- the estate's lists --------------------------------------------------------------------------------------

    static readonly string[] ListNames = { "managers", "allowed", "allowed_groups", "banned" };

    /// <summary>The estate's name, owner and lists: the EstateAccess capability (Second Life), else getinfo.</summary>
    public async Task<JsonObject> EstateAsync(CancellationToken ct)
    {
        NeedManager("the estate's lists");
        var c = C;
        var lists = new Dictionary<string, List<UUID>>();
        JsonArray? bannedDetail = null;
        var info = GroupOps.AskAsync<EstateUpdateInfoReplyEventArgs, EstateUpdateInfoReplyEventArgs>(
            h => c.Estate.EstateUpdateInfoReply += h, h => c.Estate.EstateUpdateInfoReply -= h, x => (true, x),
            () => { }, "the estate's info", ct, Wait);
        if (Sim.Caps?.CapabilityURI("EstateAccess") is { } cap)
        {
            c.Estate.RequestInfo();
            var (_, data) = await c.HttpCapsClient.GetAsync(cap, ct);
            if (data is { } bytes && OSDParser.Deserialize(bytes) is OSDMap m)
            {
                // each entry's id: "id" as the viewer reads it; OpenSim names a manager's "agent_id"
                static UUID IdOf(OSDMap x) => x["id"].AsUUID() is var id && id != UUID.Zero ? id
                    : x.Values.Select(v => v.AsUUID()).FirstOrDefault(v => v != UUID.Zero);
                List<UUID> Ids(string key) => m[key] is OSDArray arr ? arr.OfType<OSDMap>().Select(IdOf).Where(x => x != UUID.Zero).ToList() : new();
                lists["managers"] = Ids("Managers");
                lists["allowed"] = Ids("AllowedAgents");
                lists["allowed_groups"] = Ids("AllowedGroups");
                lists["banned"] = Ids("BannedAgents");
                if (m["BannedAgents"] is OSDArray bans)
                {
                    bannedDetail = new JsonArray();
                    foreach (var b in bans.OfType<OSDMap>())
                    {
                        var d = new JsonObject { ["id"] = IdOf(b).ToString() };
                        if (b["ban_date"].AsString() is { Length: > 0 } bd && bd[0] != '0') d["banned"] = bd.Length > 16 ? bd[..16] : bd;
                        if (b["banning_id"].AsUUID() is var by && by != UUID.Zero) d["banned_by_id"] = by.ToString();
                        bannedDetail.Add(d);
                    }
                }
            }
        }
        else
        {
            // OpenSim: getinfo answers with estateupdateinfo and a setaccess per list
            var got = new System.Collections.Concurrent.ConcurrentDictionary<string, List<UUID>>();
            void Users(object? _, EstateUsersReplyEventArgs e) => got["allowed"] = e.AllowedUsers;
            void Groups(object? _, EstateGroupsReplyEventArgs e) => got["allowed_groups"] = e.AllowedGroups;
            void Bans(object? _, EstateBansReplyEventArgs e) => got["banned"] = e.Banned;
            void Managers(object? _, EstateManagersReplyEventArgs e) => got["managers"] = e.Managers;
            c.Estate.EstateUsersReply += Users; c.Estate.EstateGroupsReply += Groups;
            c.Estate.EstateBansReply += Bans; c.Estate.EstateManagersReply += Managers;
            try
            {
                c.Estate.RequestInfo();
                var end = DateTime.UtcNow + Wait;
                var quietSince = DateTime.UtcNow;
                int seen = 0;
                while (DateTime.UtcNow < end && got.Count < 4)
                {
                    await Task.Delay(150, ct);
                    if (got.Count != seen) { seen = got.Count; quietSince = DateTime.UtcNow; }
                    else if (seen > 0 && DateTime.UtcNow - quietSince > TimeSpan.FromSeconds(1.5)) break;
                }
            }
            finally
            {
                c.Estate.EstateUsersReply -= Users; c.Estate.EstateGroupsReply -= Groups;
                c.Estate.EstateBansReply -= Bans; c.Estate.EstateManagersReply -= Managers;
            }
            foreach (var (k, v) in got) lists[k] = v.Where(x => x != UUID.Zero).ToList();
        }
        EstateUpdateInfoReplyEventArgs? est = null;
        try { est = await info; } catch (LeapError) { }
        var people = lists.Where(kv => kv.Key != "allowed_groups").SelectMany(kv => kv.Value).Append(est?.EstateOwner ?? UUID.Zero).Where(x => x != UUID.Zero);
        await _s.Names.ResolveAsync(c, people, TimeSpan.FromSeconds(3), ct);
        JsonArray Named(List<UUID> ids, bool groups) => new(ids.Select(id => (JsonNode)new JsonObject
        {
            ["id"] = id.ToString(), ["name"] = groups ? GroupName(id) : _s.Names.Complete(id, id.ToString()),
        }).ToArray());
        var o = new JsonObject();
        if (est != null)
        {
            o["estate"] = est.EstateName;
            o["owner"] = _s.Names.Complete(est.EstateOwner, est.EstateOwner.ToString());
            o["owner_id"] = est.EstateOwner.ToString();
            o["public"] = Has((ulong)est.Flags, RegionFlags.PublicAllowed);
            o["deny_no_payment_info"] = Has((ulong)est.Flags, RegionFlags.DenyAnonymous);
            o["deny_under_18"] = Has((ulong)est.Flags, RegionFlags.DenyAgeUnverified);
            o["deny_scripted_agents"] = Has((ulong)est.Flags, RegionFlags.DenyBots);
            o["voice"] = Has((ulong)est.Flags, RegionFlags.AllowVoice);
            o["direct_teleport"] = Has((ulong)est.Flags, RegionFlags.AllowDirectTeleport);
        }
        foreach (var k in ListNames)
            o[k] = lists.TryGetValue(k, out var ids) ? Named(ids, k == "allowed_groups") : null;
        if (bannedDetail != null && o["banned"] is JsonArray named)
            foreach (var (n, d) in named.OfType<JsonObject>().Zip(bannedDetail.OfType<JsonObject>()))
            {
                if (d["banned"] is { } when) n["banned"] = when.DeepClone();
                if (d.Str("banned_by_id") is { } by && UUID.TryParse(by, out var byId)) n["banned_by"] = _s.Names.Complete(byId, by);
            }
        return o;
    }

    string GroupName(UUID id) => _s.State.Groups.TryGetValue(id, out var g) ? g.Name : id.ToString();

    /// <summary>estateaccessdelta: one person (or group, for allowed_groups) onto or off an estate list, in this
    /// estate or all the estates I manage with the same owner.</summary>
    public async Task<JsonObject> ChangeListAsync(JsonObject a, bool add, CancellationToken ct)
    {
        NeedManager("changing the estate's lists");
        var c = C;
        var list = (a.Str("list") ?? "").Trim().ToLowerInvariant();
        if (Array.IndexOf(ListNames, list) < 0) throw new LeapError($"list: {string.Join(" | ", ListNames)}");
        bool all = a.Bool("all_estates", false);
        var ids = list == "allowed_groups"
            ? new List<UUID> { _s.Parcels.GroupArg(a) ?? _s.Parcels.GroupArg(a, "to") ?? throw new LeapError("need group (an id or a group of mine)") }
            : await _s.Parcels.PeopleAsync(a, ct);
        foreach (var id in ids)
        {
            if (list == "banned" && add && id == c.Self.AgentID) throw new LeapError("that's you");
            switch (list, add)
            {
                case ("managers", true): c.Estate.AddEstateManager(id, all); break;
                case ("managers", false): c.Estate.RemoveEstateManager(id, all); break;
                case ("allowed", true): c.Estate.AddAllowedUser(id, all); break;
                case ("allowed", false): c.Estate.RemoveAllowedUser(id, all); break;
                case ("allowed_groups", true): c.Estate.AddAllowedGroup(id, all); break;
                case ("allowed_groups", false): c.Estate.RemoveAllowedGroup(id, all); break;
                case ("banned", true): c.Estate.BanUser(id, all); break;
                case ("banned", false): c.Estate.UnbanUser(id, all); break;
            }
        }
        // the region may take a moment (OpenSim queues estate changes): read the list again for a while
        JsonObject now = new();
        for (int i = 0; i < 5; i++)
        {
            await Task.Delay(i == 0 ? 1500 : 2500, ct);
            now = await EstateAsync(ct);
            if (now[list] is not JsonArray arr) return now;
            var listed = arr.OfType<JsonObject>().Select(x => x.Str("id")).ToHashSet();
            if (ids.All(id => listed.Contains(id.ToString()) == add)) return now;
        }
        throw new LeapError($"the estate's {list} list didn't change (the estate's owner decides who manages it; managers can change the rest)");
    }

    // --- people in the region --------------------------------------------------------------------------------------

    /// <summary>kickestate: out of the estate altogether (sent home), as the estate's Kick.</summary>
    public async Task<JsonObject> KickAsync(JsonObject a, CancellationToken ct)
    {
        NeedManager("kicking from the estate");
        var c = C;
        var ids = await _s.Parcels.PeopleAsync(a, ct);
        foreach (var id in ids)
        {
            if (id == c.Self.AgentID) throw new LeapError("that's you");
            c.Estate.KickUser(id);
        }
        return await GoneAsync(ids, "kicked", ct);
    }

    /// <summary>teleporthomeuser (or everyone but me: teleporthomeallusers).</summary>
    public async Task<JsonObject> SendHomeAsync(JsonObject a, CancellationToken ct)
    {
        NeedManager("sending people home");
        var c = C;
        if (a.Bool("everyone", false))
        {
            var here = _s.Region?.Avatars.Values.Select(av => av.ID).Where(id => id != c.Self.AgentID).ToList() ?? new();
            c.Estate.TeleportHomeAllUsers();
            return await GoneAsync(here, "sent_home", ct);
        }
        var ids = await _s.Parcels.PeopleAsync(a, ct);
        foreach (var id in ids)
        {
            if (id == c.Self.AgentID) throw new LeapError("that's you (TesselAgent goHome)");
            c.Estate.TeleportHomeUser(id);
        }
        return await GoneAsync(ids, "sent_home", ct);
    }

    async Task<JsonObject> GoneAsync(List<UUID> ids, string what, CancellationToken ct)
    {
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(300, ct);
            var here = _s.Region?.Avatars.Values.Select(av => av.ID).ToHashSet() ?? new();
            if (!ids.Any(here.Contains)) break;
        }
        var still = (_s.Region?.Avatars.Values.Select(av => av.ID).ToHashSet() ?? new()).Intersect(ids).ToList();
        var o = new JsonObject { [what] = new JsonArray(ids.Except(still).Select(id => (JsonNode)_s.Names.Complete(id, id.ToString())).ToArray()) };
        if (still.Count > 0) o["still_here"] = new JsonArray(still.Select(id => (JsonNode)_s.Names.Complete(id, id.ToString())).ToArray());
        return o;
    }

    /// <summary>A message to everyone in the region (simulatormessage) or the whole estate (instantmessage).</summary>
    public JsonObject Message(JsonObject a)
    {
        NeedManager("messaging the region");
        var text = (a.Str("text") ?? "").Trim();
        if (text.Length == 0) throw new LeapError("need text");
        if (text.Length > 1000) throw new LeapError("1000 characters at most");
        if (a.Bool("estate", false)) C.Estate.EstateMessage(text); else C.Estate.SimulatorMessage(text);
        return new JsonObject { ["sent_to"] = a.Bool("estate", false) ? "the estate" : "the region", ["text"] = text };
    }

    // --- the region's settings -------------------------------------------------------------------------------------

    /// <summary>setregioninfo (DispatchRegionInfo where the grid has it): the General tab, the rest kept as they are.</summary>
    public async Task<JsonObject> SetRegionAsync(JsonObject a, CancellationToken ct)
    {
        NeedManager("the region's settings");
        var p = await RegionInfoAsync(ct);
        ulong f = FlagsOf(p);
        bool B(string key, bool now) => a.Bool(key) ?? now;
        var maturity = (a.Str("maturity") ?? Maturity(p.RegionInfo.SimAccess)).Trim().ToLowerInvariant() switch
        {
            "general" or "pg" => EstateTools.RegionMaturity.PG,
            "moderate" or "mature" => EstateTools.RegionMaturity.Mature,
            "adult" => EstateTools.RegionMaturity.Adult,
            _ => throw new LeapError("maturity: general | moderate | adult"),
        };
        int limit = a.Int("agent_limit", p.RegionInfo2.MaxAgents32 > 0 ? (int)p.RegionInfo2.MaxAgents32 : p.RegionInfo.MaxAgents);
        if (p.RegionInfo2.HardMaxAgents > 0 && limit > p.RegionInfo2.HardMaxAgents) throw new LeapError($"agent_limit: at most {p.RegionInfo2.HardMaxAgents} here");
        await C.Estate.SetRegionInfoAsync(
            B("block_terraform", Has(f, RegionFlags.BlockTerraform)), B("block_fly", Has(f, RegionFlags.NoFly)),
            B("block_fly_over", (f & BlockFlyOver) != 0), B("allow_damage", Has(f, RegionFlags.AllowDamage)),
            B("allow_land_resell", !Has(f, RegionFlags.BlockLandResell)), B("restrict_push", Has(f, RegionFlags.RestrictPushObject)),
            B("allow_parcel_changes", Has(f, RegionFlags.AllowParcelChanges)), limit,
            (float)a.Num("object_bonus", p.RegionInfo.ObjectBonusFactor), B("block_parcel_search", Has(f, RegionFlags.BlockParcelSearch)), maturity);
        await Task.Delay(1500, ct);
        return await RegionAsync(ct);
    }

    /// <summary>setregiondebug: the Debug tab's emergency switches (each kept unless named).</summary>
    public async Task<JsonObject> DebugAsync(JsonObject a, CancellationToken ct)
    {
        NeedManager("disabling scripts, collisions or physics");
        var p = await RegionInfoAsync(ct);
        ulong f = FlagsOf(p);
        bool scripts = a.Bool("scripts_disabled") ?? Has(f, RegionFlags.SkipScripts);
        bool collisions = a.Bool("collisions_disabled") ?? Has(f, RegionFlags.SkipCollisions);
        bool physics = a.Bool("physics_disabled") ?? Has(f, RegionFlags.SkipPhysics);
        C.Estate.SetRegionDebug(scripts, collisions, physics);
        await Task.Delay(1500, ct);
        var after = FlagsOf(await RegionInfoAsync(ct));
        var o = new JsonObject
        {
            ["scripts_disabled"] = Has(after, RegionFlags.SkipScripts), ["collisions_disabled"] = Has(after, RegionFlags.SkipCollisions),
            ["physics_disabled"] = Has(after, RegionFlags.SkipPhysics),
        };
        if (o["scripts_disabled"]!.GetValue<bool>() != scripts || o["collisions_disabled"]!.GetValue<bool>() != collisions || o["physics_disabled"]!.GetValue<bool>() != physics)
            o["note"] = "the region didn't take all of it";
        return o;
    }

    /// <summary>restart: in delay seconds (the viewer's 120; 30-240), or cancel one counting down.</summary>
    public JsonObject Restart(JsonObject a)
    {
        NeedManager("restarting the region");
        if (a.Bool("cancel", false))
        {
            C.Estate.CancelRestart();
            return new JsonObject { ["restart"] = "cancelled" };
        }
        int delay = Math.Clamp(a.Int("delay", 120), 30, 240);
        C.Estate.RestartRegion(delay);
        return new JsonObject { ["restart_in_seconds"] = delay, ["note"] = "everyone here is told and counted down; anyone still here when it goes is logged out" };
    }

    /// <summary>terrain bake: the region's current land becomes what revert returns to.</summary>
    public JsonObject Bake()
    {
        NeedManager("baking the terrain");
        C.Estate.EstateOwnerMessage("terrain", "bake");
        return new JsonObject { ["baked"] = true };
    }

    // --- top scripts and colliders ---------------------------------------------------------------------------------

    // llfloatertopobjects.h STAT_FILTER_*
    const uint ByParcel = 0x1, ByOwner = 0x2, ByObject = 0x4, ByParcelName = 0x8;

    /// <summary>LandStatRequest: the region's busiest scripts (ms a frame) or colliders, filtered by owner, object or
    /// parcel name as the floater's fields do.</summary>
    public async Task<JsonObject> TopAsync(JsonObject a, bool colliders, CancellationToken ct)
    {
        NeedManager(colliders ? "the top colliders" : "the top scripts");
        var c = C;
        uint flags = 0;
        string filter = "";
        if (a.Str("owner") is { Length: > 0 } owner) { flags = ByOwner; filter = owner; }
        else if (a.Str("object") is { Length: > 0 } obj) { flags = ByObject; filter = obj; }
        else if (a.Str("parcel") is { Length: > 0 } parcel) { flags = ByParcelName; filter = parcel; }
        int limit = Math.Clamp(a.Int("limit", 15), 1, 100);
        var type = colliders ? EstateTools.LandStatReportType.TopColliders : EstateTools.LandStatReportType.TopScripts;
        (int Count, Dictionary<UUID, EstateTask> Tasks) got;
        if (colliders)
            got = await GroupOps.AskAsync<TopCollidersReplyEventArgs, (int, Dictionary<UUID, EstateTask>)>(
                h => c.Estate.TopCollidersReply += h, h => c.Estate.TopCollidersReply -= h, e => (true, (e.ObjectCount, e.Tasks)),
                () => c.Estate.LandStatRequest(0, type, flags, filter), "the top colliders", ct, TimeSpan.FromSeconds(10));
        else
            got = await GroupOps.AskAsync<TopScriptsReplyEventArgs, (int, Dictionary<UUID, EstateTask>)>(
                h => c.Estate.TopScriptsReply += h, h => c.Estate.TopScriptsReply -= h, e => (true, (e.ObjectCount, e.Tasks)),
                () => c.Estate.LandStatRequest(0, type, flags, filter), "the top scripts", ct, TimeSpan.FromSeconds(10));
        var arr = new JsonArray();
        foreach (var t in got.Tasks.Values) LastTop[t.TaskID] = (t.TaskName, t.OwnerName);
        foreach (var t in got.Tasks.Values.OrderByDescending(t => t.Score).Take(limit))
        {
            var o = new JsonObject
            {
                ["id"] = t.TaskID.ToString(), ["name"] = t.TaskName, ["owner"] = t.OwnerName,
                [colliders ? "collisions" : "ms"] = Json.R(t.Score, 3), ["at"] = Json.Arr(t.Position),
            };
            arr.Add(o);
        }
        return new JsonObject { ["objects"] = arr, ["total"] = got.Count };
    }

    /// <summary>What the top lists named, by object id (a return's confirmation says what goes).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<UUID, (string Name, string Owner)> LastTop { get; } = new();

    /// <summary>Top Objects' Return: those objects back to their owners, wherever in the region they are.</summary>
    public JsonObject ReturnObjects(JsonObject a)
    {
        NeedManager("returning objects region-wide");
        var c = C;
        var ids = a.UuidList("ids");
        if (a.Uuid("id") is { } one) ids.Add(one);
        if (ids.Count == 0) throw new LeapError("need ids (from topScripts or topColliders)");
        foreach (var chunk in ids.Distinct().Chunk(100))
            c.Network.SendPacket(new ParcelReturnObjectsPacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
                ParcelData = { LocalID = -1, ReturnType = 0 },   // the whole region, RT_NONE: by task id
                TaskIDs = chunk.Select(id => new ParcelReturnObjectsPacket.TaskIDsBlock { TaskID = id }).ToArray(),
                OwnerIDs = Array.Empty<ParcelReturnObjectsPacket.OwnerIDsBlock>(),
            }, Sim);
        return new JsonObject { ["returned"] = ids.Count };
    }
}
