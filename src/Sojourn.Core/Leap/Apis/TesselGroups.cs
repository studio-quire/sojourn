using System.Text.Json.Nodes;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Leap;

/// <summary>sojourn's groups API (the viewer's group floaters). Joining, leaving and the active group are
/// TesselAgent's ops, group chat is TesselIM's. Claude Opus 5.5, 2026-10-16.</summary>
public sealed class TesselGroups : SessionApi
{
    public override string Name => "TesselGroups";
    public override string Desc => "sojourn: groups: search, create, profile, members, roles and powers, invites, eject, bans, notices, titles";

    public TesselGroups(AgentSession s) : base(s)
    {
        var g = s.GroupOps;
        Op("search", "{text, start?} -> {groups: [{group_id, name, members}]}: groups listed in search, a page at a time", (a, ct) => J(g.SearchAsync(a, ct)));
        Op("create", "{name, charter?, open_enrollment=false, show_in_list=true, fee=0, mature=false, insignia_id?} -> {group_id}: I'm its owner",
            (a, ct) => J(g.CreateAsync(a, ct)));
        Op("profile", "{group_id} -> {name, charter, founder, open_enrollment, fee, members, roles, member, my_powers?}", (a, ct) => J(g.ProfileAsync(a, ct)));
        Op("update", "{group_id, charter?, open_enrollment?, fee?, show_in_list?, mature?, insignia_id?}", (a, ct) => J(g.UpdateAsync(a, ct)));
        Op("members", "{group_id} -> {members: [{id, name, title, owner, online, powers}]}", (a, ct) => J(g.MembersAsync(a, ct)));
        Op("roles", "{group_id} -> {roles: [{role_id, name, title, desc, powers, members, everyone}]}", (a, ct) => J(g.RolesAsync(a, ct)));
        Op("invite", "{group_id, to, role_id? | role_ids?}: they get a JoinGroup notification (default role: Everyone)", a => g.Invite(a));
        Op("eject", "{group_id, member} -> {ejected}: as the grid's reply says (owners can't be ejected)", (a, ct) => J(g.EjectAsync(a, ct)));
        Op("bans", "{group_id} -> {bans: [{id, name, since?}]}: who's banned from joining (Second Life only)", (a, ct) => J(g.BansAsync(a, ct)));
        Op("ban", "{group_id, member | members, eject=true} -> {banned, ejected}: banned from joining, and ejected if a member, as the viewer's Ban (Second Life only)",
            (a, ct) => J(g.BanAsync(a, true, ct)));
        Op("unban", "{group_id, member | members} -> {unbanned}: the ban lifted (Second Life only)", (a, ct) => J(g.BanAsync(a, false, ct)));
        Op("createRole", "{group_id, name, title?, desc?, powers?: [names] | \"all\"} -> role", (a, ct) => J(g.CreateRoleAsync(a, ct)));
        Op("updateRole", "{group_id, role_id, name?, title?, desc?, powers?}", (a, ct) => J(g.UpdateRoleAsync(a, ct)));
        Op("deleteRole", "{group_id, role_id}", a => g.DeleteRole(a));
        Op("assignRole", "{group_id, role_id, member}", a => g.AssignRole(a, true));
        Op("unassignRole", "{group_id, role_id, member}", a => g.AssignRole(a, false));
        Op("title", "{group_id, role_id}: wear that role's title (one of my roles; Everyone: the zero id)", (a, ct) => J(g.TitleAsync(a, ct)));
        Op("titles", "{group_id} -> {titles: [{role_id, title, selected}]}: the titles I can wear in that group", (a, ct) => J(g.TitlesAsync(a, ct)));
        Op("membership", "{group_id, notices?, list_in_profile?} -> {notices, list_in_profile}: whether I get its notices, whether my profile lists it", a => g.Membership(a));
        Op("notice", "{group_id, subject, message, item_id?} -> {sent, archived}: to every member (an attachment needs copy+transfer); archived: its archive shows it (the grid drops one it can't take without a word)", (a, ct) => J(g.NoticeAsync(a, ct)));
        Op("notices", "{group_id} -> {notices: [{notice_id, time, from, subject, attachment}]}", (a, ct) => J(g.NoticesAsync(a, ct)));
        Op("noticeRead", "{notice_id} -> {subject, message, from, attachment?: {name, type}}: one from the archive, whole", (a, ct) => J(g.NoticeReadAsync(a, ct)));
        Op("noticeTake", "{notice_id} -> {taken, type, folder}: its attachment, into the folder for its kind", (a, ct) => J(g.NoticeTakeAsync(a, ct)));
        Op("powers", "-> {powers}: the power names roles can have", _ => new JsonObject { ["powers"] = Json.Strings(GroupOps.PowerList) });
    }

    static async Task<JsonNode?> J(Task<JsonObject> t) => await t;
}
