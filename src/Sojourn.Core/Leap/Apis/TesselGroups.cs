using System.Text.Json.Nodes;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Leap;

/// <summary>sojourn's groups API (the viewer's group floaters). Joining, leaving and the active group are
/// TesselAgent's ops, group chat is TesselIM's. Claude Opus 5.5, 2026-10-16.</summary>
public sealed class TesselGroups : SessionApi
{
    public override string Name => "TesselGroups";
    public override string Desc => "sojourn: groups: search, create, profile, members, roles and powers, invites, eject, notices, titles";

    public TesselGroups(AgentSession s) : base(s)
    {
        var g = s.GroupOps;
        Op("search", "{text} -> {groups: [{group_id, name, members}]}: groups listed in search", (a, ct) => J(g.SearchAsync(a, ct)));
        Op("create", "{name, charter?, open_enrollment=false, show_in_list=true, fee=0, mature=false, insignia_id?} -> {group_id}: I'm its owner",
            (a, ct) => J(g.CreateAsync(a, ct)));
        Op("profile", "{group_id} -> {name, charter, founder, open_enrollment, fee, members, roles, member, my_powers?}", (a, ct) => J(g.ProfileAsync(a, ct)));
        Op("update", "{group_id, charter?, open_enrollment?, fee?, show_in_list?, mature?, insignia_id?}", (a, ct) => J(g.UpdateAsync(a, ct)));
        Op("members", "{group_id} -> {members: [{id, name, title, owner, online, powers}]}", (a, ct) => J(g.MembersAsync(a, ct)));
        Op("roles", "{group_id} -> {roles: [{role_id, name, title, desc, powers, members, everyone}]}", (a, ct) => J(g.RolesAsync(a, ct)));
        Op("invite", "{group_id, to, role_id? | role_ids?}: they get a JoinGroup notification (default role: Everyone)", a => g.Invite(a));
        Op("eject", "{group_id, member}", a => g.Eject(a));
        Op("createRole", "{group_id, name, title?, desc?, powers?: [names] | \"all\"} -> role", (a, ct) => J(g.CreateRoleAsync(a, ct)));
        Op("updateRole", "{group_id, role_id, name?, title?, desc?, powers?}", (a, ct) => J(g.UpdateRoleAsync(a, ct)));
        Op("deleteRole", "{group_id, role_id}", a => g.DeleteRole(a));
        Op("assignRole", "{group_id, role_id, member}", a => g.AssignRole(a, true));
        Op("unassignRole", "{group_id, role_id, member}", a => g.AssignRole(a, false));
        Op("title", "{group_id, role_id}: wear that role's title (one of my roles; Everyone: the zero id)", (a, ct) => J(g.TitleAsync(a, ct)));
        Op("notice", "{group_id, subject, message, item_id?}: to every member (an attachment needs copy+transfer)", (a, ct) => J(g.NoticeAsync(a, ct)));
        Op("notices", "{group_id} -> {notices: [{notice_id, time, from, subject, attachment}]}", (a, ct) => J(g.NoticesAsync(a, ct)));
        Op("powers", "-> {powers}: the power names roles can have", _ => new JsonObject { ["powers"] = Json.Strings(GroupOps.PowerList) });
    }

    static async Task<JsonNode?> J(Task<JsonObject> t) => await t;
}
