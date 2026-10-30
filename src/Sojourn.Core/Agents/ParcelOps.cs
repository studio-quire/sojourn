using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// About Land (sojourn's TesselAgent parcel ops, and SojournLand's): a parcel's name, description, music and media
/// URLs, landing point, options, access (the allowed and banned lists, group access, passes), sale settings and object
/// return; who has objects there and returning them; ejecting and banning. Every op works on the parcel I stand on, or
/// the one at `at` ([x, y] in this region's metres), or one already confirmed by `local_id`. Written by Claude Opus 5.5,
/// 2026-10-16; access lists, more settings and the parcel by place 2026-10-28 (batch B5). Build rights stay with the
/// grid's land office on our grids (its `allow` narrows "everyone can build"), so the build flag isn't offered here.
/// The region checks I'm allowed (owner, or group powers) and ignores what I'm not, without a word: so each change is
/// read back and a refusal said.
/// </summary>
public sealed partial class ParcelOps
{
    readonly AgentSession _s;
    public ParcelOps(AgentSession s) => _s = s;
    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    static readonly (string Name, ParcelFlags Flag, bool Inverted)[] Options =
    {
        ("fly", ParcelFlags.AllowFly, false), ("scripts", ParcelFlags.AllowOtherScripts, false),
        ("group_scripts", ParcelFlags.AllowGroupScripts, false), ("landmark", ParcelFlags.AllowLandmark, false),
        ("damage", ParcelFlags.AllowDamage, false), ("push", ParcelFlags.RestrictPushObject, true),
        ("show_in_search", ParcelFlags.ShowDirectory, false), ("object_entry", ParcelFlags.AllowAPrimitiveEntry, false),
        ("terraform", ParcelFlags.AllowTerraform, false), ("sound_local", ParcelFlags.SoundLocal, false),
        // with the parcel's group set: its members may build (OpenSim checks the group), and their objects may enter
        ("group_build", ParcelFlags.CreateGroupObjects, false), ("group_object_entry", ParcelFlags.AllowGroupObjectEntry, false),
        // About Land > Access: only the allowed list, the parcel's group, passes for others; who's turned away
        ("access_list", ParcelFlags.UseAccessList, false), ("access_group", ParcelFlags.UseAccessGroup, false),
        ("pass_list", ParcelFlags.UsePassList, false), ("deny_no_payment_info", ParcelFlags.DenyAnonymous, false),
        ("deny_under_18", ParcelFlags.DenyAgeUnverified, false),
        // About Land > Sound: voice here, and voice kept to the parcel (the estate's channel when off)
        ("voice", ParcelFlags.AllowVoiceChat, false), ("voice_parcel_only", ParcelFlags.UseEstateVoiceChan, true),
    };

    public static readonly string[] Categories =
        { "none", "linden", "adult", "arts", "business", "educational", "gaming", "hangout", "newcomer", "park", "residential", "shopping", "stage", "other", "rental" };

    /// <summary>A point in this region from `at` ([x, y] or [x, y, z]), or null for where I stand.</summary>
    public static Vector3? Point(JsonObject a)
    {
        if (a["at"] is not JsonArray xy) return null;
        if (xy.Count < 2) throw new LeapError("at: [x, y] in this region's metres");
        float x = (float)(Json.AsDouble(xy[0]) ?? double.NaN), y = (float)(Json.AsDouble(xy[1]) ?? double.NaN);
        if (!float.IsFinite(x) || !float.IsFinite(y) || x < 0 || y < 0 || x >= 256 * 16 || y >= 256 * 16) throw new LeapError("at: [x, y] in this region's metres");
        return new Vector3(x, y, 0);
    }

    /// <summary>The parcel I stand on, at `at`, or by `local_id` (one a confirmation pinned), with full properties
    /// asked for afresh. A `region` pinned with it must still be where I am.</summary>
    public async Task<(Simulator Sim, Parcel Parcel)> ParcelAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var sim = c.Network.CurrentSim ?? throw new LeapError("no region");
        if (a.Str("region") is { Length: > 0 } pinned && !string.Equals(pinned, sim.Name, StringComparison.OrdinalIgnoreCase))
            throw new LeapError($"that was in {pinned}; I'm in {sim.Name} now, so nothing was done");
        int? local = a.Has("local_id") ? a.Int("local_id", 0) : null;
        // by id, asked by a point inside it: OpenSim leaves ParcelPropertiesRequestByID unanswered
        var pos = Point(a) ?? (local is { } known ? PointIn(sim, known) : null) ?? c.Self.SimPosition;
        if (local is null && (pos.X >= sim.SizeX || pos.Y >= sim.SizeY)) throw new LeapError($"at: inside this region (0-{sim.SizeX} m)");
        var tcs = new TaskCompletionSource<Parcel>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, ParcelPropertiesEventArgs e)
        {
            if (e.Simulator != sim || e.Parcel is not { } p) return;
            if (local is { } id ? p.LocalID == id : p.AABBMin.X <= pos.X && p.AABBMin.Y <= pos.Y && p.AABBMax.X >= pos.X && p.AABBMax.Y >= pos.Y)
                tcs.TrySetResult(p);
        }
        c.Parcels.ParcelProperties += Handler;
        try
        {
            if (local is { } id && Point(a) is null && PointIn(sim, id) is null) c.Parcels.RequestParcelProperties(sim, id, 0);
            else c.Parcels.RequestParcelProperties(sim, pos.Y + 1, pos.X + 1, pos.Y - 1, pos.X - 1, 0, false);
            return (sim, await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8), ct));
        }
        catch (TimeoutException) { throw new LeapError("the region didn't describe that parcel"); }
        finally { c.Parcels.ParcelProperties -= Handler; }
    }

    /// <summary>The middle of a cell of the parcel, from what the region last said of it (its bitmap: a bit per 4 m
    /// cell, row by row from the south-west).</summary>
    static Vector3? PointIn(Simulator sim, int localId)
    {
        if (!sim.Parcels.TryGetValue(localId, out var p) || p.Bitmap is not { Length: > 0 } bits) return null;
        int per = (int)(sim.SizeX / 4);
        for (int i = 0; i < per * per && i / 8 < bits.Length; i++)
            if ((bits[i / 8] & (1 << (i % 8))) != 0) return new Vector3(i % per * 4 + 2, i / per * 4 + 2, 0);
        return null;
    }

    /// <summary>The parcel I stand on (SojournLand's and TesselAgent's parcel ops before `at`).</summary>
    Task<(Simulator Sim, Parcel Parcel)> HereAsync(CancellationToken ct) => ParcelAsync(new JsonObject(), ct);

    /// <summary>What a confirmation pins so the op acts on the parcel the person was shown.</summary>
    public static void Pin(JsonObject a, Simulator sim, Parcel p)
    {
        a["local_id"] = p.LocalID;
        a["region"] = sim.Name;
    }

    /// <summary>The client's: show a parcel (its bitmap) or a rectangle on the ground for this long (World/ParcelLines.cs),
    /// as the viewer shows the land About Land is about.</summary>
    public Action<ulong, byte[]?, (float W, float S, float E, float N)?, double>? Showing { get; set; }

    internal void Show(Simulator sim, Parcel p, double seconds) => Showing?.Invoke(sim.Handle, p.Bitmap, null, seconds);

    public string NameOf(UUID id) => id == UUID.Zero ? "nobody" : _s.Names.Complete(id, id.ToString());

    public string GroupName(UUID id) =>
        id == UUID.Zero ? "none" : _s.State.Groups.TryGetValue(id, out var g) ? g.Name : id.ToString();

    public bool Mine(Parcel p) => p.OwnerID == C.Self.AgentID && !p.IsGroupOwned;

    JsonObject Describe(Parcel p)
    {
        var opts = new JsonObject();
        foreach (var (name, flag, inv) in Options) opts[name] = ((p.Flags & flag) != 0) ^ inv;
        var cat = (int)p.Category;
        var o = new JsonObject
        {
            ["local_id"] = p.LocalID, ["name"] = p.Name, ["desc"] = p.Desc, ["music_url"] = p.MusicURL ?? "",
            ["media_url"] = p.Media.MediaURL ?? "", ["landing"] = p.Landing switch { LandingType.LandingPoint => "point", LandingType.Direct => "anywhere", _ => "blocked" },
            ["landing_point"] = Json.Arr(p.UserLocation), ["options"] = opts, ["owner_id"] = p.OwnerID.ToString(),
            ["owner"] = p.IsGroupOwned ? $"the group {GroupName(p.OwnerID)}" : NameOf(p.OwnerID),
            ["group_id"] = p.GroupID.ToString(), ["group"] = GroupName(p.GroupID), ["group_owned"] = p.IsGroupOwned,
            ["area"] = p.Area, ["snapshot_id"] = p.SnapshotID.ToString(),
            ["bounds"] = Json.Arr(p.AABBMin.X, p.AABBMin.Y, p.AABBMax.X, p.AABBMax.Y),
            ["category"] = cat >= 0 && cat < Categories.Length ? Categories[cat] : cat.ToString(),
            ["see_avatars"] = p.SeeAVs, ["auto_return_minutes"] = p.OtherCleanTime,
            ["prims"] = new JsonObject
            {
                ["used"] = p.TotalPrims, ["max"] = p.MaxPrims, ["owner"] = p.OwnerPrims, ["group"] = p.GroupPrims, ["others"] = p.OtherPrims,
            },
        };
        if ((p.Flags & ParcelFlags.ForSale) != 0)
        {
            var sale = new JsonObject { ["price"] = p.SalePrice, ["objects_included"] = (p.Flags & ParcelFlags.SellParcelObjects) != 0 };
            if (p.AuthBuyerID != UUID.Zero) { sale["buyer_id"] = p.AuthBuyerID.ToString(); sale["buyer"] = NameOf(p.AuthBuyerID); }
            o["for_sale"] = sale;
        }
        if ((p.Flags & ParcelFlags.UsePassList) != 0) o["pass"] = new JsonObject { ["price"] = p.PassPrice, ["hours"] = Json.R(p.PassHours, 2) };
        return o;
    }

    public async Task<JsonObject> SetAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var (sim, p) = await ParcelAsync(a, ct);
        if (a.Str("name") is { } name) p.Name = name;
        if (a.Str("desc") is { } desc) p.Desc = desc;
        if (a.Str("music_url") is { } mu) p.MusicURL = mu;
        if (a.Str("media_url") is { } me) { var m = p.Media; m.MediaURL = me; p.Media = m; }
        if (a.Uuid("group_id") is { } group) p.GroupID = group;   // About Land's "Group: Set"; UUID.Zero = none
        if (a.Uuid("snapshot_id") is { } snap) p.SnapshotID = _s.Inventory.Node(snap) is InventoryItem { AssetType: AssetType.Texture } t ? t.AssetUUID : snap;
        if (a.Num("pass_price") is { } pp) p.PassPrice = Math.Max(0, (int)Math.Round(pp));
        if (a.Num("pass_hours") is { } ph) p.PassHours = (float)Math.Clamp(ph, 0.01, 2400);
        if (a.Num("auto_return_minutes") is { } ar) p.OtherCleanTime = Math.Max(0, (int)Math.Round(ar));
        if (a.Bool("see_avatars") is { } see) p.SeeAVs = see;
        if (a.Str("category") is { } catName)
        {
            int ci = Array.IndexOf(Categories, catName.Trim().ToLowerInvariant());
            if (ci < 0) throw new LeapError($"category: {string.Join(", ", Categories)}");
            p.Category = (ParcelCategory)(sbyte)ci;
        }
        switch (a.Str("landing"))
        {
            case null: break;
            case "here" or "point":
                p.Landing = LandingType.LandingPoint;
                p.UserLocation = c.Self.SimPosition;
                p.UserLookAt = Vector3.UnitX * c.Self.SimRotation;
                break;
            case "anywhere": p.Landing = LandingType.Direct; break;
            case "blocked": p.Landing = LandingType.None; break;
            default: throw new LeapError("landing: here | anywhere | blocked");
        }
        if (a["options"] is JsonObject opts)
            foreach (var (k, v) in opts)
            {
                var o = Options.FirstOrDefault(x => x.Name == k);
                if (o.Name is null)
                    throw new LeapError(k == "build"
                        ? "building rights go through the land office here (`allow First Last` on channel 5)"
                        : $"no option {k}; options: {string.Join(", ", Options.Select(x => x.Name))}");
                var on = Json.AsDouble(v) is { } d ? d != 0 : v?.GetValue<bool>() == true;
                if (on ^ o.Inverted) p.Flags |= o.Flag; else p.Flags &= ~o.Flag;
            }
        var want = Describe(p);
        p.Update(c, sim, false);
        await Task.Delay(1000, ct);
        var (_, after) = await ParcelAsync(new JsonObject { ["local_id"] = p.LocalID }, ct);
        var got = Describe(after);
        // the region ignores changes I'm not allowed to make, without a word: say so
        var keys = new (string Arg, string Field)[]
        {
            ("name", "name"), ("desc", "desc"), ("music_url", "music_url"), ("media_url", "media_url"), ("landing", "landing"),
            ("options", "options"), ("group_id", "group_id"), ("pass_price", "pass"), ("pass_hours", "pass"),
            ("auto_return_minutes", "auto_return_minutes"), ("see_avatars", "see_avatars"), ("category", "category"),
        };
        var refused = keys.Where(k => a.Has(k.Arg) && !JsonNode.DeepEquals(want[k.Field], got[k.Field])).Select(k => k.Arg).Distinct().ToList();
        if (refused.Count > 0)
            throw new LeapError($"the region didn't change {string.Join(", ", refused)}: only the parcel's owner (or a group member with the powers) may (owner {NameOf(after.OwnerID)})");
        return got;
    }

    public async Task<JsonObject> InfoAsync(CancellationToken ct) => await InfoAsync(new JsonObject(), ct);

    public async Task<JsonObject> InfoAsync(JsonObject a, CancellationToken ct)
    {
        var (sim, p) = await ParcelAsync(a, ct);
        Show(sim, p, 10);
        var ids = new[] { p.OwnerID, p.AuthBuyerID }.Where(x => x != UUID.Zero && !p.IsGroupOwned);
        try { await _s.Names.ResolveAsync(C, ids, TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return Describe(p);
    }

    /// <summary>Who has objects on this parcel, and how many prims.</summary>
    public async Task<JsonObject> ObjectsAsync(CancellationToken ct) => await ObjectsAsync(new JsonObject(), ct);

    public async Task<JsonObject> ObjectsAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var (sim, p) = await ParcelAsync(a, ct);
        var owners = await GroupOps.AskAsync<ParcelObjectOwnersReplyEventArgs, List<ParcelPrimOwners>>(
            h => c.Parcels.ParcelObjectOwnersReply += h, h => c.Parcels.ParcelObjectOwnersReply -= h,
            e => (e.Simulator == sim, e.PrimOwners), () => c.Parcels.RequestObjectOwners(sim, p.LocalID),
            "object owners (only the parcel's owner may ask)", ct, TimeSpan.FromSeconds(8));
        await _s.Names.ResolveAsync(c, owners.Select(o => o.OwnerID), TimeSpan.FromSeconds(3), ct);
        var arr = new JsonArray();
        foreach (var o in owners.OrderByDescending(o => o.Count))
            arr.Add(new JsonObject
            {
                ["owner_id"] = o.OwnerID.ToString(), ["name"] = o.IsGroupOwned ? "(group)" : _s.Names.Complete(o.OwnerID, o.OwnerID.ToString()),
                ["prims"] = o.Count, ["group_owned"] = o.IsGroupOwned, ["newest"] = o.NewestPrim.ToString("yyyy-MM-dd HH:mm"),
            });
        return new JsonObject { ["parcel"] = p.Name, ["owners"] = arr };
    }

    /// <summary>Return objects on this parcel: by owner ids, or every object of others.</summary>
    public async Task<JsonObject> ReturnAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var (sim, p) = await ParcelAsync(a, ct);
        var owners = a.UuidList("owner_ids");
        if (a.Uuid("owner_id") is { } one) owners.Add(one);
        if (a.Bool("others", false)) c.Parcels.ReturnObjects(sim, p.LocalID, ObjectReturnType.Other, new List<UUID>());
        else if (owners.Count > 0) c.Parcels.ReturnObjects(sim, p.LocalID, ObjectReturnType.List, owners);
        else throw new LeapError("need owner_id(s) or others: true");
        await Task.Delay(500, ct);
        return new JsonObject { ["parcel"] = p.Name, ["requested"] = true };
    }

    /// <summary>Eject (and ban). The region does it without a word, or ignores it without one (not my land, no
    /// powers): so watch them leave where they were.</summary>
    public async Task<JsonObject> EjectAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var to = a.Uuid("to") ?? throw new LeapError("need to");
        var w = _s.Region ?? throw new LeapError("no region");
        Vector3? Where() => w.Avatars.Values.FirstOrDefault(av => av.ID == to) is { } av ? TesselAgent.AvatarPos(w, av) : null;
        var before = Where() ?? throw new LeapError("they aren't in this region");
        c.Parcels.EjectUser(to, a.Bool("ban", false));
        for (var i = 0; i < 25; i++)
        {
            await Task.Delay(200, ct);
            if (Where() is not { } now || Vector3.Distance(now, before) > 2) return new JsonObject { ["ejected"] = to.ToString(), ["banned"] = a.Bool("ban", false) };
        }
        throw new LeapError("they're still where they were: ejecting needs the parcel they're on to be mine (or a group's I have the powers in)");
    }

    // --- the allowed and banned lists (About Land > Access) ------------------------------------------------------

    /// <summary>The region keeps at most this many on each list (llparcel.h PARCEL_MAX_ACCESS_LIST).</summary>
    public const int MaxListed = 300;
    const int PerPacket = 48;   // PARCEL_MAX_ENTRIES_PER_PACKET

    async Task<List<ParcelAccessEntry>> ListAsync(Simulator sim, Parcel p, AccessList which, CancellationToken ct)
    {
        var c = C;
        // LibreMetaverse raises its reply event only with Settings.Parcel.AlwaysRequestAcl (which asks every parcel's
        // lists unbidden): so the packet itself
        var tcs = new TaskCompletionSource<List<ParcelAccessEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(object? _, PacketReceivedEventArgs e)
        {
            if (e.Simulator != sim || e.Packet is not ParcelAccessListReplyPacket r || r.Data.LocalID != p.LocalID || (r.Data.Flags & (uint)which) == 0) return;
            tcs.TrySetResult(r.List.Select(t => new ParcelAccessEntry
            {
                AgentID = t.ID, Time = Utils.UnixTimeToDateTime((uint)t.Time), Flags = (AccessList)t.Flags,
            }).ToList());
        }
        c.Network.RegisterCallback(PacketType.ParcelAccessListReply, On);
        List<ParcelAccessEntry> got;
        try
        {
            c.Parcels.RequestParcelAccessList(sim, p.LocalID, which, 0);
            got = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8), ct);
        }
        catch (TimeoutException)
        {
            throw new LeapError($"the parcel's {(which == AccessList.Ban ? "banned" : "allowed")} list didn't come (only its owner, or a group member with the powers, may see it)");
        }
        finally { c.Network.UnregisterCallback(PacketType.ParcelAccessListReply, On); }
        // an empty list comes as one blank entry
        return got.Where(e => e.AgentID != UUID.Zero).ToList();
    }

    static long? Expires(ParcelAccessEntry e)
    {
        long unix = (long)(e.Time.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds;
        return unix > 86400 ? unix : null;
    }

    JsonArray Entries(IEnumerable<ParcelAccessEntry> list)
    {
        var arr = new JsonArray();
        foreach (var e in list.OrderBy(e => NameOf(e.AgentID), StringComparer.OrdinalIgnoreCase))
        {
            var o = new JsonObject { ["id"] = e.AgentID.ToString(), ["name"] = NameOf(e.AgentID) };
            if (Expires(e) is { } until)
                o["until"] = DateTimeOffset.FromUnixTimeSeconds(until).ToLocalTime().ToString("ddd d MMM HH:mm");
            arr.Add(o);
        }
        return arr;
    }

    /// <summary>About Land > Access: who's allowed and who's banned, and how the parcel is closed.</summary>
    public async Task<JsonObject> AccessAsync(JsonObject a, CancellationToken ct)
    {
        var (sim, p) = await ParcelAsync(a, ct);
        Show(sim, p, 10);
        var allowed = await ListAsync(sim, p, AccessList.Access, ct);
        var banned = await ListAsync(sim, p, AccessList.Ban, ct);
        await _s.Names.ResolveAsync(C, allowed.Concat(banned).Select(e => e.AgentID), TimeSpan.FromSeconds(3), ct);
        bool list = (p.Flags & ParcelFlags.UseAccessList) != 0, group = (p.Flags & ParcelFlags.UseAccessGroup) != 0;
        var o = new JsonObject
        {
            ["parcel"] = p.Name,
            ["open_to"] = !list && !group ? "everyone" : list && group ? "the allowed list and the parcel's group" : list ? "the allowed list" : "the parcel's group",
            ["allowed"] = Entries(allowed), ["banned"] = Entries(banned),
            ["deny_no_payment_info"] = (p.Flags & ParcelFlags.DenyAnonymous) != 0, ["deny_under_18"] = (p.Flags & ParcelFlags.DenyAgeUnverified) != 0,
        };
        if ((p.Flags & ParcelFlags.UsePassList) != 0) o["pass"] = new JsonObject { ["price"] = p.PassPrice, ["hours"] = Json.R(p.PassHours, 2) };
        return o;
    }

    /// <summary>Someone onto or off the allowed or banned list (hours: for that long). The whole list goes back to the
    /// region, as the viewer sends it, then it's read again to see the change took.</summary>
    public async Task<JsonObject> ChangeListAsync(JsonObject a, bool ban, bool add, CancellationToken ct)
    {
        var c = C;
        var (sim, p) = await ParcelAsync(a, ct);
        var who = await PeopleAsync(a, ct);
        var which = ban ? AccessList.Ban : AccessList.Access;
        var current = await ListAsync(sim, p, which, ct);
        var map = current.GroupBy(e => e.AgentID).ToDictionary(g => g.Key, g => g.First());
        double hours = a.Num("hours", 0);
        var until = hours > 0 ? DateTime.UtcNow.AddHours(hours) : DateTime.UnixEpoch;
        foreach (var id in who)
        {
            if (add)
            {
                if (id == p.OwnerID) throw new LeapError("the parcel's owner can't be on its lists");
                if (id == c.Self.AgentID && ban) throw new LeapError("that's you");
                map[id] = new ParcelAccessEntry { AgentID = id, Time = until, Flags = 0 };
            }
            else map.Remove(id);
        }
        if (map.Count > MaxListed) throw new LeapError($"a parcel's list holds at most {MaxListed}");
        SendList(sim, p.LocalID, which, map.Values.ToList());
        await Task.Delay(800, ct);
        var after = (await ListAsync(sim, p, which, ct)).Select(e => e.AgentID).ToHashSet();
        var missed = who.Where(id => after.Contains(id) != add).ToList();
        if (missed.Count > 0)
            throw new LeapError($"the region didn't change the {(ban ? "banned" : "allowed")} list: only the parcel's owner (or a group member with the powers) may (owner {NameOf(p.OwnerID)})");
        var res = await AccessAsync(new JsonObject { ["local_id"] = p.LocalID }, ct);
        if (!ban && add && (p.Flags & ParcelFlags.UseAccessList) == 0)
            res["note"] = "the allowed list only keeps others out once the parcel is closed to it (set options access_list: true)";
        return res;
    }

    /// <summary>LLViewerParcelMgr::sendParcelAccessListUpdate: the whole list, 48 to a message, numbered.</summary>
    void SendList(Simulator sim, int localId, AccessList which, List<ParcelAccessEntry> entries)
    {
        var c = C;
        var transaction = UUID.Random();
        int sections = (int)Math.Ceiling(entries.Count / (double)PerPacket);
        int seq = 1;
        for (int at = 0; at == 0 || at < entries.Count; at += PerPacket)
        {
            var chunk = entries.Skip(at).Take(PerPacket).ToList();
            var pkt = new ParcelAccessListUpdatePacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
                Data = { Flags = (uint)which, LocalID = localId, TransactionID = transaction, SequenceID = seq++, Sections = sections },
                List = chunk.Count == 0
                    // an empty list still sends one blank block
                    ? new[] { new ParcelAccessListUpdatePacket.ListBlock { ID = UUID.Zero, Time = 0, Flags = 0 } }
                    : chunk.Select(e => new ParcelAccessListUpdatePacket.ListBlock
                    {
                        ID = e.AgentID, Time = (int)(Expires(e) ?? 0), Flags = 0,
                    }).ToArray(),
            };
            c.Network.SendPacket(pkt, sim);
            if (chunk.Count == 0) break;
        }
    }

    /// <summary>People named by `to` (an id, or the name of someone here or a friend) or `ids`.</summary>
    public async Task<List<UUID>> PeopleAsync(JsonObject a, CancellationToken ct)
    {
        var ids = a.UuidList("ids");
        foreach (var who in a.StrList("to"))
        {
            if (UUID.TryParse(who, out var id) && id != UUID.Zero) { ids.Add(id); continue; }
            ids.Add(await PersonAsync(who, ct));
        }
        if (ids.Count == 0) throw new LeapError("need to (an id or a name) or ids");
        return ids.Distinct().ToList();
    }

    async Task<UUID> PersonAsync(string name, CancellationToken ct)
    {
        await Task.CompletedTask;
        var want = name.Trim();
        static string Norm(string s) => s.Replace(".", " ").Replace(" Resident", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (_s.Region is { } w && w.Avatars.Values.FirstOrDefault(av => string.Equals(Norm(av.Name), Norm(want), StringComparison.OrdinalIgnoreCase)) is { } here)
            return here.ID;
        if (_s.People.Friend(want) is { } f) return f.UUID;
        throw new LeapError($"no one called {want} is here or a friend: find their id with TesselAgent findAvatar");
    }
}
