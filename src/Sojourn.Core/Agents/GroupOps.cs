using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Packets;
using LibreMetaverse.StructuredData;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Groups as the viewer's group floaters run them (sojourn's TesselGroups API): search, create, profile, members,
/// roles and powers, invites, ejecting, notices, titles. Joining, leaving and the active group stay in TesselAgent
/// (the Tessel viewer's ops); group chat is TesselIM send {group_id}. Written by Claude Opus 5.5, 2026-10-16.
/// The grid needs a groups service (OpenSim: Groups V2 with a storage provider; docs/groups.md).
/// </summary>
public sealed class GroupOps
{
    readonly AgentSession _s;
    public GroupOps(AgentSession s) => _s = s;

    // OpenSim ignores a GroupProfileRequest for the same group within 10 s of the last one (LLClientView), so a
    // second ask in that window would wait for nothing: answer it from the last reply, or the one still on its way.
    static readonly TimeSpan ProfileWindow = TimeSpan.FromSeconds(10);
    readonly System.Collections.Concurrent.ConcurrentDictionary<UUID, (DateTime Asked, Task<Group> Reply)> _profiles = new();

    Task<Group> GroupProfileAsync(UUID gid, CancellationToken ct)
    {
        var c = C;
        var now = DateTime.UtcNow;
        if (_profiles.TryGetValue(gid, out var last) && now - last.Asked < ProfileWindow && !last.Reply.IsFaulted && !last.Reply.IsCanceled)
            return last.Reply;
        var reply = AskAsync<GroupProfileEventArgs, Group>(h => c.Groups.GroupProfile += h, h => c.Groups.GroupProfile -= h,
            e => (e.Group.ID == gid, e.Group), () => c.Groups.RequestGroupProfile(gid), "group profile (does the grid have groups enabled?)",
            CancellationToken.None);
        _profiles[gid] = (now, reply);
        return reply.WaitAsync(ct);
    }
    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>Sends a request and waits for the reply event that matches.</summary>
    internal static async Task<T> AskAsync<TArgs, T>(Action<EventHandler<TArgs>> add, Action<EventHandler<TArgs>> remove,
        Func<TArgs, (bool Hit, T Value)> match, Action send, string what, CancellationToken ct, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(object? _, TArgs e)
        {
            try { var (hit, v) = match(e); if (hit) tcs.TrySetResult(v); }
            catch (Exception x) { tcs.TrySetException(x); }
        }
        add(On);
        try
        {
            send();
            try { return await tcs.Task.WaitAsync(timeout ?? Wait, ct); }
            catch (TimeoutException) { throw new LeapError($"{what}: no answer from the grid"); }
        }
        finally { remove(On); }
    }

    static UUID Gid(JsonObject a) => a.Uuid("group_id") ?? a.Uuid("id") ?? throw new LeapError("need group_id");

    /// <summary>The grid ignores a group change my roles don't allow, without a word: so ask my powers first.</summary>
    void Need(UUID gid, GroupPowers any, string what)
    {
        if (!_s.State.Groups.TryGetValue(gid, out var g)) throw new LeapError("I'm not in that group");
        if ((g.Powers & any) == 0)
            throw new LeapError($"{what} in {g.Name} needs the {string.Join(" or ", PowerJson(any).Select(x => x!.GetValue<string>()))} power, which my roles there don't give");
    }

    // ---------- powers ----------

    static readonly Dictionary<string, GroupPowers> PowerNames = Enum.GetValues<GroupPowers>()
        .Where(p => p != GroupPowers.None)
        .GroupBy(p => p.ToString().ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());

    public static IEnumerable<string> PowerList => PowerNames.Keys.OrderBy(k => k);

    public static GroupPowers ParsePowers(JsonNode? n)
    {
        if (n is null) return GroupPowers.None;
        if (n is JsonValue v && v.TryGetValue<string>(out var s))
        {
            if (s.Equals("all", StringComparison.OrdinalIgnoreCase)) return PowerNames.Values.Aggregate(GroupPowers.None, (x, y) => x | y);
            if (ulong.TryParse(s, out var raw)) return (GroupPowers)raw;
            return s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Aggregate(GroupPowers.None, (x, w) => x | One(w));
        }
        if (n is JsonArray arr) return arr.Aggregate(GroupPowers.None, (x, w) => x | One(w?.GetValue<string>() ?? ""));
        throw new LeapError("powers: a list of names, \"all\", or a number");
    }

    static GroupPowers One(string w) => PowerNames.TryGetValue(w.Trim().ToLowerInvariant(), out var p) ? p
        : throw new LeapError($"no group power {w} (TesselGroups powers lists them)");

    public static JsonArray PowerJson(GroupPowers p)
    {
        var arr = new JsonArray();
        foreach (var (name, bit) in PowerNames.OrderBy(kv => kv.Key)) if ((p & bit) == bit) arr.Add(name);
        return arr;
    }

    // ---------- finding and making ----------

    public async Task<JsonObject> SearchAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var text = a.Str("text") ?? a.Str("name") ?? "";
        UUID q = UUID.Zero;
        var groups = await AskAsync<DirGroupsReplyEventArgs, List<DirectoryManager.GroupSearchData>>(
            h => c.Directory.DirGroupsReply += h, h => c.Directory.DirGroupsReply -= h,
            e => (e.QueryID == q, e.MatchedGroups), () => q = c.Directory.StartGroupSearch(text, a.Int("start", 0)), "group search (does the grid have groups enabled?)", ct);
        var arr = new JsonArray();
        foreach (var g in groups.Where(g => g.GroupID != UUID.Zero))
            arr.Add(new JsonObject { ["group_id"] = g.GroupID.ToString(), ["name"] = g.GroupName, ["members"] = g.Members });
        return new JsonObject { ["groups"] = arr };
    }

    public async Task<JsonObject> CreateAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var name = a.Str("name")?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length < 4 || name.Length > 35) throw new LeapError("a group name is 4 to 35 characters");
        var g = new Group
        {
            Name = name, Charter = a.Str("charter") ?? "", OpenEnrollment = a.Bool("open_enrollment", false),
            ShowInList = a.Bool("show_in_list", true), MembershipFee = a.Int("fee", 0), MaturePublish = a.Bool("mature", false),
            AllowPublish = a.Bool("show_in_list", true), InsigniaID = a.Uuid("insignia_id") ?? UUID.Zero, AcceptNotices = true,
            ListInProfile = true,
        };
        var (ok, id, msg) = await AskAsync<GroupCreatedReplyEventArgs, (bool, UUID, string)>(
            h => c.Groups.GroupCreatedReply += h, h => c.Groups.GroupCreatedReply -= h,
            e => (true, (e.Success, e.GroupID, e.Message)), () => c.Groups.RequestCreateGroup(g), "creating the group (does the grid have groups enabled?)", ct, TimeSpan.FromSeconds(20));
        if (!ok) throw new LeapError($"the group wasn't created: {msg}");
        c.Groups.RequestCurrentGroups();
        return new JsonObject { ["group_id"] = id.ToString(), ["name"] = name, ["message"] = msg };
    }

    public async Task<JsonObject> ProfileAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var gid = Gid(a);
        var g = await GroupProfileAsync(gid, ct);
        var o = new JsonObject
        {
            ["group_id"] = g.ID.ToString(), ["name"] = g.Name, ["charter"] = g.Charter, ["founder_id"] = g.FounderID.ToString(),
            ["open_enrollment"] = g.OpenEnrollment, ["fee"] = g.MembershipFee, ["show_in_list"] = g.ShowInList,
            ["mature"] = g.MaturePublish, ["members"] = g.GroupMembershipCount, ["roles"] = g.GroupRolesCount,
            ["insignia_id"] = g.InsigniaID.ToString(), ["owner_role"] = g.OwnerRole.ToString(), ["member_title"] = g.MemberTitle,
        };
        o["member"] = _s.State.Groups.ContainsKey(gid);
        if (g.Powers != GroupPowers.None) o["my_powers"] = PowerJson(g.Powers);
        if (g.FounderID != UUID.Zero)
        {
            await _s.Names.ResolveAsync(c, new[] { g.FounderID }, TimeSpan.FromSeconds(3), ct);
            o["founder"] = _s.Names.Complete(g.FounderID, g.FounderID.ToString());
        }
        return o;
    }

    public async Task<JsonObject> UpdateAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var gid = Gid(a);
        if (a.Has("charter") || a.Has("insignia_id")) Need(gid, GroupPowers.ChangeIdentity, "changing the charter or insignia");
        if (a.Has("open_enrollment") || a.Has("fee") || a.Has("show_in_list") || a.Has("mature")) Need(gid, GroupPowers.ChangeOptions, "changing the group's options");
        var g = await GroupProfileAsync(gid, ct);   // a struct: the copy below is ours to change
        if (a.Str("charter") is { } ch) g.Charter = ch;
        if (a.Bool("open_enrollment") is { } oe) g.OpenEnrollment = oe;
        if (a.Num("fee") is { } fee) g.MembershipFee = (int)fee;
        if (a.Bool("show_in_list") is { } sl) { g.ShowInList = sl; g.AllowPublish = sl; }
        if (a.Bool("mature") is { } m) g.MaturePublish = m;
        if (a.Uuid("insignia_id") is { } ins) g.InsigniaID = ins;
        c.Groups.UpdateGroup(gid, g);
        // asking again now would be ignored (the 10 s window); what we sent is the new profile
        _profiles[gid] = (DateTime.UtcNow, Task.FromResult(g));
        return await ProfileAsync(new JsonObject { ["group_id"] = gid.ToString() }, ct);
    }

    // ---------- members and roles ----------

    public async Task<JsonObject> MembersAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var gid = Gid(a);
        UUID req = UUID.Zero;
        var members = await AskAsync<GroupMembersReplyEventArgs, Dictionary<UUID, GroupMember>>(
            h => c.Groups.GroupMembersReply += h, h => c.Groups.GroupMembersReply -= h,
            e => (e.GroupID == gid && (req == UUID.Zero || e.RequestID == req), e.Members), () => req = c.Groups.RequestGroupMembers(gid),
            "group members", ct, TimeSpan.FromSeconds(15));
        await _s.Names.ResolveAsync(c, members.Keys, TimeSpan.FromSeconds(3), ct);
        var arr = new JsonArray();
        foreach (var m in members.Values.OrderByDescending(m => m.IsOwner).ThenBy(m => _s.Names.Complete(m.ID, "")))
            arr.Add(new JsonObject
            {
                ["id"] = m.ID.ToString(), ["name"] = _s.Names.Complete(m.ID, m.ID.ToString()), ["title"] = m.Title,
                ["owner"] = m.IsOwner, ["online"] = m.OnlineStatus, ["powers"] = PowerJson(m.Powers),
            });
        return new JsonObject { ["group_id"] = gid.ToString(), ["members"] = arr };
    }

    public async Task<JsonObject> RolesAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var gid = Gid(a);
        UUID r1 = UUID.Zero, r2 = UUID.Zero;
        var roles = await AskAsync<GroupRolesDataReplyEventArgs, Dictionary<UUID, GroupRole>>(
            h => c.Groups.GroupRoleDataReply += h, h => c.Groups.GroupRoleDataReply -= h,
            e => (e.GroupID == gid && (r1 == UUID.Zero || e.RequestID == r1), e.Roles), () => r1 = c.Groups.RequestGroupRoles(gid), "group roles", ct);
        List<KeyValuePair<UUID, UUID>> rm;
        try
        {
            rm = await AskAsync<GroupRolesMembersReplyEventArgs, List<KeyValuePair<UUID, UUID>>>(
                h => c.Groups.GroupRoleMembersReply += h, h => c.Groups.GroupRoleMembersReply -= h,
                e => (e.GroupID == gid && (r2 == UUID.Zero || e.RequestID == r2), e.RolesMembers), () => r2 = c.Groups.RequestGroupRolesMembers(gid),
                "group role members", ct, TimeSpan.FromSeconds(6));
        }
        catch (LeapError) { rm = new(); }  // a group whose roles have no members can answer with nothing
        await _s.Names.ResolveAsync(c, rm.Select(kv => kv.Value).Distinct(), TimeSpan.FromSeconds(3), ct);
        var arr = new JsonArray();
        foreach (var r in roles.Values.OrderBy(r => r.ID == UUID.Zero ? 0 : 1).ThenBy(r => r.Name))
        {
            var mem = new JsonArray();
            foreach (var kv in rm.Where(kv => kv.Key == r.ID))
                mem.Add(new JsonObject { ["id"] = kv.Value.ToString(), ["name"] = _s.Names.Complete(kv.Value, kv.Value.ToString()) });
            arr.Add(new JsonObject
            {
                ["role_id"] = r.ID.ToString(), ["name"] = r.Name, ["title"] = r.Title, ["desc"] = r.Description,
                ["powers"] = PowerJson(r.Powers), ["members"] = mem, ["everyone"] = r.ID == UUID.Zero,
            });
        }
        return new JsonObject { ["group_id"] = gid.ToString(), ["roles"] = arr };
    }

    public JsonObject Invite(JsonObject a)
    {
        var c = C;
        var gid = Gid(a);
        var to = a.Uuid("to") ?? throw new LeapError("need to (an avatar id)");
        var roles = a.UuidList("role_ids");
        if (a.Uuid("role_id") is { } r) roles.Add(r);
        if (roles.Count == 0) roles.Add(UUID.Zero);  // Everyone
        Need(gid, GroupPowers.Invite, "inviting");
        c.Groups.Invite(gid, roles, to);
        return new JsonObject { ["invited"] = to.ToString(), ["group_id"] = gid.ToString() };
    }

    /// <summary>Ejected, as the grid's reply says (EjectGroupMemberReply): an owner, or someone not in it, isn't.</summary>
    public async Task<JsonObject> EjectAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var gid = Gid(a);
        var who = a.Uuid("member") ?? a.Uuid("to") ?? throw new LeapError("need member");
        if (who == c.Self.AgentID) throw new LeapError("that's me: leaving a group is TesselAgent leaveGroup");
        Need(gid, GroupPowers.Eject, "ejecting members");
        if (!await EjectOneAsync(c, gid, who, ct)) throw new LeapError("the grid didn't eject them (an owner can't be ejected, and only a member can)");
        return new JsonObject { ["ejected"] = true, ["member"] = who.ToString() };
    }

    static Task<bool> EjectOneAsync(GridClient c, UUID gid, UUID who, CancellationToken ct) =>
        AskAsync<GroupOperationEventArgs, bool>(h => c.Groups.GroupMemberEjected += h, h => c.Groups.GroupMemberEjected -= h,
            e => (e.GroupID == gid, e.Success), () => c.Groups.EjectUser(gid, who), "ejecting", ct);

    // ---------- bans (Second Life's GroupAPIv1 capability, as the viewer's LLGroupMgr::sendGroupBanRequest; OpenSim has none) ----------

    public static bool BansHere(GridClient? c) => c?.Network.CurrentSim?.Caps?.CapabilityURI("GroupAPIv1") != null;

    static Uri BanCap(GridClient c, UUID gid) =>
        c.Network.CurrentSim?.Caps?.CapabilityURI("GroupAPIv1") is { } cap ? new Uri($"{cap}?group_id={gid}")
            : throw new LeapError("group bans aren't on this grid: Second Life keeps them in a service this grid doesn't have");

    /// <summary>Who's banned from joining, and since when (the viewer's Banned Residents tab).</summary>
    public async Task<JsonObject> BansAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var gid = Gid(a);
        var uri = BanCap(c, gid);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var (resp, data) = await c.HttpCapsClient.GetAsync(uri, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new LeapError((int)resp.StatusCode is 401 or 403 ? "the grid won't show this group's bans to me (it takes the ban power)" : $"the grid didn't give the ban list (HTTP {(int)resp.StatusCode})");
        var list = (data is { Length: > 0 } ? OSDParser.Deserialize(data) as OSDMap : null)?["ban_list"] as OSDMap;
        var bans = new List<(UUID Id, DateTime Since)>();
        if (list != null)
            foreach (var key in list.Keys)
                if (UUID.TryParse(key, out var id))
                    bans.Add((id, list[key] is OSDMap entry && entry.ContainsKey("ban_date") ? entry["ban_date"].AsDate() : DateTime.MinValue));
        await _s.Names.ResolveAsync(c, bans.Select(b => b.Id), TimeSpan.FromSeconds(3), ct);
        var arr = new JsonArray();
        foreach (var (id, since) in bans.OrderBy(b => _s.Names.Complete(b.Id, ""), StringComparer.OrdinalIgnoreCase))
        {
            var o = new JsonObject { ["id"] = id.ToString(), ["name"] = _s.Names.Complete(id, id.ToString()) };
            if (since > new DateTime(2000, 1, 1)) o["since"] = since.ToUniversalTime().ToString("yyyy-MM-dd");
            arr.Add(o);
        }
        return new JsonObject { ["group_id"] = gid.ToString(), ["bans"] = arr };
    }

    /// <summary>Banned (and, as the viewer's Ban does, ejected if they're a member), or the ban lifted.</summary>
    public async Task<JsonObject> BanAsync(JsonObject a, bool ban, CancellationToken ct)
    {
        var c = C;
        var gid = Gid(a);
        var who = a.UuidList("members");
        if ((a.Uuid("member") ?? a.Uuid("to")) is { } one && !who.Contains(one)) who.Add(one);
        if (who.Count == 0) throw new LeapError("need member");
        if (ban && who.Contains(c.Self.AgentID)) throw new LeapError("that's me");
        var uri = BanCap(c, gid);
        Need(gid, GroupPowers.GroupBanAccess, ban ? "banning" : "lifting a ban");
        var ids = new OSDArray();
        foreach (var id in who) ids.Add(OSD.FromUUID(id));
        // (the viewer's EBanRequestAction: BAN_CREATE 1, BAN_DELETE 2)
        var body = new OSDMap { ["ban_action"] = OSD.FromInteger(ban ? 1 : 2), ["ban_ids"] = ids };
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var (resp, _) = await c.HttpCapsClient.PostAsync(uri, OSDFormat.Xml, body, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new LeapError((int)resp.StatusCode is 401 or 403 ? "the grid refused it (banning takes the ban power)" : $"the grid refused it (HTTP {(int)resp.StatusCode})");
        }
        var o = new JsonObject { ["group_id"] = gid.ToString(), [ban ? "banned" : "unbanned"] = Json.Strings(who.Select(w => w.ToString())) };
        if (!ban || !a.Bool("eject", true)) return o;
        if (!_s.State.Groups.TryGetValue(gid, out var g) || (g.Powers & GroupPowers.Eject) == 0) { o["ejected"] = new JsonArray(); o["not_ejected"] = "ejecting takes the eject power"; return o; }
        var ejected = new JsonArray();
        foreach (var id in who)
            try { if (await EjectOneAsync(c, gid, id, ct)) ejected.Add(id.ToString()); }
            catch (LeapError) { }   // (not a member: nothing to eject)
        o["ejected"] = ejected;
        return o;
    }

    public async Task<JsonObject> CreateRoleAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var gid = Gid(a);
        var name = a.Str("name") ?? throw new LeapError("need name");
        var role = new GroupRole
        {
            GroupID = gid, ID = UUID.Random(), Name = name, Title = a.Str("title") ?? name, Description = a.Str("desc") ?? "",
            Powers = ParsePowers(a["powers"]),
        };
        c.Groups.CreateRole(gid, role);
        // the grid picks the id: find the role by name
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(700, ct);
            var roles = await RolesAsync(new JsonObject { ["group_id"] = gid.ToString() }, ct);
            if ((roles["roles"] as JsonArray)!.OfType<JsonObject>().FirstOrDefault(r => r["name"]?.GetValue<string>() == name) is { } hit)
                return hit;
        }
        throw new LeapError("the role didn't appear (creating roles needs the createrole power)");
    }

    public async Task<JsonObject> UpdateRoleAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var gid = Gid(a);
        var rid = a.Uuid("role_id") ?? throw new LeapError("need role_id");
        if (a.Has("name") || a.Has("title") || a.Has("desc")) Need(gid, GroupPowers.RoleProperties, "changing a role's name, title or description");
        if (a.Has("powers")) Need(gid, GroupPowers.ChangeActions, "changing a role's powers");
        var roles = await RolesAsync(new JsonObject { ["group_id"] = gid.ToString() }, ct);
        var cur = (roles["roles"] as JsonArray)!.OfType<JsonObject>().FirstOrDefault(r => r["role_id"]?.GetValue<string>() == rid.ToString())
                  ?? throw new LeapError("no such role in that group");
        var role = new GroupRole
        {
            GroupID = gid, ID = rid, Name = a.Str("name") ?? cur["name"]!.GetValue<string>(),
            Title = a.Str("title") ?? cur["title"]!.GetValue<string>(), Description = a.Str("desc") ?? cur["desc"]!.GetValue<string>(),
            Powers = a.Has("powers") ? ParsePowers(a["powers"]) : ParsePowers(cur["powers"]),
        };
        c.Groups.UpdateRole(role);
        await Task.Delay(500, ct);
        return new JsonObject { ["role_id"] = rid.ToString(), ["name"] = role.Name, ["powers"] = PowerJson(role.Powers) };
    }

    public JsonObject DeleteRole(JsonObject a)
    {
        var rid = a.Uuid("role_id") ?? throw new LeapError("need role_id");
        if (rid == UUID.Zero) throw new LeapError("Everyone can't be deleted");
        var gid = Gid(a);
        Need(gid, GroupPowers.DeleteRole, "deleting roles");
        C.Groups.DeleteRole(gid, rid);
        return new JsonObject { ["requested"] = true, ["role_id"] = rid.ToString() };
    }

    public JsonObject AssignRole(JsonObject a, bool add)
    {
        var c = C;
        var gid = Gid(a);
        var rid = a.Uuid("role_id") ?? throw new LeapError("need role_id");
        var who = a.Uuid("member") ?? throw new LeapError("need member");
        if (add) Need(gid, GroupPowers.AssignMember | GroupPowers.AssignMemberLimited, "assigning roles");
        else Need(gid, GroupPowers.RemoveMember, "removing members from roles");
        if (add) c.Groups.AddToRole(gid, rid, who); else c.Groups.RemoveFromRole(gid, rid, who);
        return new JsonObject { ["requested"] = true };
    }

    public async Task<JsonObject> TitleAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var gid = Gid(a);
        var rid = a.Uuid("role_id") ?? UUID.Zero;
        if (!_s.State.Groups.ContainsKey(gid)) throw new LeapError("I'm not in that group");
        // a title is one of my own roles'; the grid ignores any other
        var mine = await TitlesOfAsync(gid, ct);
        if (!mine.ContainsKey(rid)) throw new LeapError("that role isn't one of mine in that group, so its title isn't mine to wear");
        c.Groups.ActivateTitle(gid, rid);
        return new JsonObject { ["title_role"] = rid.ToString(), ["title"] = mine[rid].Title };
    }

    public async Task<JsonObject> TitlesAsync(JsonObject a, CancellationToken ct)
    {
        var titles = new JsonArray();
        foreach (var (rid, t) in (await TitlesOfAsync(Gid(a), ct)).OrderBy(x => x.Value.Title, StringComparer.OrdinalIgnoreCase))
            titles.Add(new JsonObject { ["role_id"] = rid.ToString(), ["title"] = t.Title, ["selected"] = t.Selected });
        return new JsonObject { ["titles"] = titles };
    }

    Task<Dictionary<UUID, GroupTitle>> TitlesOfAsync(UUID gid, CancellationToken ct)
    {
        var c = C;
        UUID req = UUID.Zero;
        return AskAsync<GroupTitlesReplyEventArgs, Dictionary<UUID, GroupTitle>>(h => c.Groups.GroupTitlesReply += h, h => c.Groups.GroupTitlesReply -= h,
            e => (e.GroupID == gid && (req == UUID.Zero || e.RequestID == req), e.Titles), () => req = c.Groups.RequestGroupTitles(gid), "group titles", ct);
    }

    /// <summary>Whether I get the group's notices and my profile lists it (the viewer's group General tab); each kept as it is
    /// unless given.</summary>
    public JsonObject Membership(JsonObject a)
    {
        var c = C;
        var gid = Gid(a);
        if (!_s.State.Groups.TryGetValue(gid, out var g)) throw new LeapError("I'm not in that group");
        bool notices = a.Bool("notices") ?? g.AcceptNotices, listed = a.Bool("list_in_profile") ?? g.ListInProfile;
        if (a.Has("notices") || a.Has("list_in_profile"))
        {
            c.Groups.SetGroupAcceptNotices(gid, notices, listed);
            g.AcceptNotices = notices;   // (a struct: the copy goes back; the grid's next list of groups will say the same)
            g.ListInProfile = listed;
            _s.State.Groups[gid] = g;
        }
        return new JsonObject { ["notices"] = notices, ["list_in_profile"] = listed };
    }

    // ---------- notices ----------

    public async Task<JsonObject> NoticeAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var gid = Gid(a);
        var notice = new GroupNotice { Subject = a.Str("subject") ?? "", Message = a.Str("message") ?? "", OwnerID = c.Self.AgentID };
        if (a.Uuid("item_id") is { } iid)
        {
            var item = await _s.Inventory.ItemAsync(iid, ct) ?? throw new LeapError($"no inventory item {iid}");
            if ((item.Permissions.OwnerMask & (PermissionMask.Copy | PermissionMask.Transfer)) != (PermissionMask.Copy | PermissionMask.Transfer))
                throw new LeapError($"{item.Name}: a notice attachment needs copy and transfer rights");
            notice.AttachmentID = item.UUID;
        }
        if (notice.Subject.Length == 0) throw new LeapError("need subject");
        Need(gid, GroupPowers.SendNotices, "sending notices");
        var before = await NoticeIdsAsync(c, gid, ct);
        SendNotice(c, gid, notice);
        // the grid drops a notice it can't take without a word (OpenSim's log: "failed to decode group notice bucket"), so
        // what's sent is said by its archive
        for (int i = 0; i < 4; i++)
        {
            await Task.Delay(1000, ct);
            var now = await NoticeIdsAsync(c, gid, ct);
            if (now != null && now.Except(before ?? new()).Any()) return new JsonObject { ["sent"] = true, ["archived"] = true };
        }
        return new JsonObject { ["sent"] = true, ["archived"] = false };
    }

    async Task<HashSet<UUID>?> NoticeIdsAsync(GridClient c, UUID gid, CancellationToken ct)
    {
        try
        {
            var list = await AskAsync<GroupNoticesListReplyEventArgs, List<GroupNoticesListEntry>>(
                h => c.Groups.GroupNoticesListReply += h, h => c.Groups.GroupNoticesListReply -= h,
                e => (e.GroupID == gid, e.Notices), () => c.Groups.RequestGroupNoticesList(gid), "group notices", ct, TimeSpan.FromSeconds(5));
            return list.Select(n => n.NoticeID).Where(n => n != UUID.Zero).ToHashSet();
        }
        catch (LeapError) { return null; }
    }

    /// <summary>A notice as the viewer packs it (send_group_notice): an IM to the group, subject and body joined by a bar,
    /// the attachment as LLSD under the "&lt;? LLSD/XML ?&gt;" line, which OpenSim reads by skipping exactly that line;
    /// LibreMetaverse's SendGroupNotice packs it otherwise, and OpenSim drops it.</summary>
    static void SendNotice(GridClient c, UUID gid, GroupNotice notice)
    {
        var bucket = notice.AttachmentID == UUID.Zero ? new byte[] { 0 }
            : System.Text.Encoding.UTF8.GetBytes($"<? LLSD/XML ?>\n<llsd><map><key>item_id</key><uuid>{notice.AttachmentID}</uuid><key>owner_id</key><uuid>{notice.OwnerID}</uuid></map></llsd>\n\0");
        var pkt = new ImprovedInstantMessagePacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
            MessageBlock =
            {
                FromGroup = false, ToAgentID = gid, Offline = 0, ID = UUID.Zero, Timestamp = 0, ParentEstateID = 0, RegionID = UUID.Zero,
                Position = c.Self.SimPosition, Dialog = (byte)InstantMessageDialog.GroupNotice, FromAgentName = Utils.StringToBytes(c.Self.Name),
                Message = Utils.StringToBytes($"{notice.Subject}|{notice.Message}"), BinaryBucket = bucket,
            },
        };
        c.Network.SendPacket(pkt, c.Network.CurrentSim);
    }

    public async Task<JsonObject> NoticesAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var gid = Gid(a);
        var list = await AskAsync<GroupNoticesListReplyEventArgs, List<GroupNoticesListEntry>>(
            h => c.Groups.GroupNoticesListReply += h, h => c.Groups.GroupNoticesListReply -= h,
            e => (e.GroupID == gid, e.Notices), () => c.Groups.RequestGroupNoticesList(gid), "group notices", ct);
        var arr = new JsonArray();
        foreach (var n in list.Where(n => n.NoticeID != UUID.Zero).OrderByDescending(n => n.Timestamp))
            arr.Add(new JsonObject
            {
                ["notice_id"] = n.NoticeID.ToString(), ["time"] = (long)n.Timestamp, ["from"] = n.FromName, ["subject"] = n.Subject,
                ["attachment"] = n.HasAttachment ? AgentInventory.AssetTypeLabel(n.AssetType) : null,
            });
        return new JsonObject { ["group_id"] = gid.ToString(), ["notices"] = arr };
    }

    /// <summary>A notice from the archive, whole: asked for by id, it comes as an IM of its own kind, subject and body joined by
    /// a bar, as a new notice's are, with what's attached named in its bucket.</summary>
    public async Task<JsonObject> NoticeReadAsync(JsonObject a, CancellationToken ct)
    {
        var im = await RequestNoticeAsync(C, a.Uuid("notice_id") ?? throw new LeapError("need notice_id"), ct);
        var parts = (im.Message ?? "").Split('|', 2);
        var o = new JsonObject { ["subject"] = parts[0], ["message"] = parts.Length > 1 ? parts[1] : "", ["from"] = im.FromAgentName };
        if (NoticeAttachment(im.BinaryBucket) is { } att)
            o["attachment"] = new JsonObject { ["name"] = att.Name, ["type"] = AgentInventory.AssetTypeLabel(att.Type) };
        return o;
    }

    /// <summary>A notice's attachment taken from the archive into the folder for its kind, as the viewer's Open attachment
    /// accepts the offer that comes with the notice.</summary>
    public async Task<JsonObject> NoticeTakeAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var im = await RequestNoticeAsync(c, a.Uuid("notice_id") ?? throw new LeapError("need notice_id"), ct);
        if (NoticeAttachment(im.BinaryBucket) is not { } att) throw new LeapError("that notice has nothing attached");
        AgentInventory.AnswerOffer(c, im, att.Type, UUID.Zero, keep: true);
        var folder = c.Inventory.FindFolderForType(att.Type);
        return new JsonObject
        {
            ["taken"] = att.Name, ["type"] = AgentInventory.AssetTypeLabel(att.Type),
            ["folder"] = _s.Inventory.Node(folder)?.Name ?? "", ["folder_id"] = folder.ToString(),
        };
    }

    static Task<InstantMessage> RequestNoticeAsync(GridClient c, UUID id, CancellationToken ct) =>
        AskAsync<InstantMessageEventArgs, InstantMessage>(h => c.Self.IM += h, h => c.Self.IM -= h,
            e => (e.IM.Dialog == InstantMessageDialog.GroupNoticeRequested && (e.IM.IMSessionID == id || e.IM.IMSessionID == UUID.Zero), e.IM),
            () => c.Groups.RequestGroupNotice(id), "the notice", ct);

    /// <summary>What a notice's IM says is attached (its bucket: whether, the asset type, the group, the item's name), or null.</summary>
    public static (AssetType Type, string Name)? NoticeAttachment(byte[]? b)
    {
        if (b is not { Length: >= 18 } || b[0] == 0) return null;
        var name = b.Length > 18 ? System.Text.Encoding.UTF8.GetString(b, 18, b.Length - 18).TrimEnd('\0') : "";
        return ((AssetType)(sbyte)b[1], name);
    }
}
