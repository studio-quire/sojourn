using System.Text.Json.Nodes;
using LibreMetaverse;
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
            e => (e.QueryID == q, e.MatchedGroups), () => q = c.Directory.StartGroupSearch(text, 0), "group search (does the grid have groups enabled?)", ct);
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

    public JsonObject Eject(JsonObject a)
    {
        var c = C;
        var gid = Gid(a);
        var who = a.Uuid("member") ?? a.Uuid("to") ?? throw new LeapError("need member");
        Need(gid, GroupPowers.Eject, "ejecting members");
        c.Groups.EjectUser(gid, who);
        return new JsonObject { ["requested"] = true, ["member"] = who.ToString() };
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
        if (rid != UUID.Zero)
        {
            // a title is one of my own roles'; the grid ignores any other
            var roles = await RolesAsync(new JsonObject { ["group_id"] = gid.ToString() }, ct);
            var role = (roles["roles"] as JsonArray)!.OfType<JsonObject>().FirstOrDefault(r => r["role_id"]?.GetValue<string>() == rid.ToString())
                       ?? throw new LeapError("no such role in that group");
            var me = c.Self.AgentID.ToString();
            if (!(role["members"] as JsonArray)!.OfType<JsonObject>().Any(m => m["id"]?.GetValue<string>() == me))
                throw new LeapError($"I don't have the role {role["name"]}, so its title isn't mine to wear");
        }
        c.Groups.ActivateTitle(gid, rid);
        return new JsonObject { ["title_role"] = rid.ToString() };
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
        c.Groups.SendGroupNotice(gid, notice);
        return new JsonObject { ["sent"] = true };
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
                ["notice_id"] = n.NoticeID.ToString(), ["time"] = n.Timestamp, ["from"] = n.FromName, ["subject"] = n.Subject,
                ["attachment"] = n.HasAttachment ? AgentInventory.AssetTypeLabel(n.AssetType) : null,
            });
        return new JsonObject { ["group_id"] = gid.ToString(), ["notices"] = arr };
    }
}
