using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Leap;

/// <summary>Land and region (batch B5): About Land with its access lists and sale settings, buying, selling, passes,
/// abandoning, deeding, dividing, joining, terraforming, land holdings and the map's items (Agents/ParcelOps*.cs,
/// LandMap.cs); the region's info and covenant, and the estate's tools for its owner and managers (Agents/EstateOps.cs).
/// Land for sale in search is SojournSearch land. What spends, gives land away, ejects or bans, or changes who may do
/// what waits for the person's own press (Client/Land/LandWiring.cs). Claude Opus 5.5, 2026-10-28.</summary>
public sealed class SojournLand : SessionApi
{
    public override string Name => "SojournLand";
    public override string Desc => "Land and region: About Land, access and ban lists, sell, buy, passes, abandon, deed, divide, join, terraform, "
        + "holdings, map items; the region's info and covenant; estate tools (lists, kick, send home, restart, top scripts and colliders, disable scripts)";

    const string At = "at? ([x, y] in this region: the parcel there; default where I stand)";

    public SojournLand(AgentSession s) : base(s)
    {
        var p = s.Parcels;
        var e = s.Estate;
        var map = s.LandMap;

        // About Land
        Op("parcel", $"{{{At}}} -> {{name, desc, owner, group, area, bounds [w,s,e,n], category, options, for_sale?, pass?, prims, "
            + "auto_return_minutes, see_avatars, landing, music_url, media_url}: About Land", (a, ct) => J(p.InfoAsync(a, ct)));
        Op("set", $"{{{At}, name?, desc?, music_url?, media_url?, snapshot_id?, group_id?, category?, landing? (here|anywhere|blocked), "
            + "pass_price?, pass_hours?, auto_return_minutes? (0: never), see_avatars? (others outside see and hear avatars here), options? "
            + "{fly, scripts, group_scripts, landmark, damage, push, show_in_search, object_entry, terraform, sound_local, group_build, "
            + "group_object_entry, access_list (only the allowed list may enter), access_group (the parcel's group may), pass_list (others may "
            + "buy a pass), deny_no_payment_info, deny_under_18, voice, voice_parcel_only}} -> About Land; the person confirms",
            (a, ct) => J(p.SetAsync(a, ct)));
        Op("objects", $"{{{At}}} -> {{owners: [{{owner_id, name, prims, newest}}]}}: who has objects on the parcel (its owner only)",
            (a, ct) => J(p.ObjectsAsync(a, ct)));
        Op("access", $"{{{At}}} -> {{open_to, allowed: [{{id, name, until?}}], banned: [...], pass?, deny_no_payment_info, deny_under_18}}: "
            + "About Land > Access", (a, ct) => J(p.AccessAsync(a, ct)));
        Op("allow", $"{{to (an id, or the name of someone here or a friend) | ids, hours? (0: for good), {At}}} -> access: onto the "
            + "parcel's allowed list (it keeps others out once options access_list is on); the person confirms", (a, ct) => J(p.ChangeListAsync(a, ban: false, add: true, ct)));
        Op("unallow", $"{{to | ids, {At}}} -> access: off the allowed list; the person confirms", (a, ct) => J(p.ChangeListAsync(a, ban: false, add: false, ct)));
        Op("ban", $"{{to | ids, hours? (0: for good), {At}}} -> access: onto the parcel's ban list (TesselAgent parcelEject with ban for "
            + "someone here now); the person confirms", (a, ct) => J(p.ChangeListAsync(a, ban: true, add: true, ct)));
        Op("unban", $"{{to | ids, {At}}} -> access: off the ban list; the person confirms", (a, ct) => J(p.ChangeListAsync(a, ban: true, add: false, ct)));
        Op("freeze", "{to, freeze=true} -> {frozen}: freeze someone on my parcel in place (or free them), as About Land's Freeze; the person confirms", a =>
        {
            var to = a.Uuid("to") ?? throw new LeapError("need to (their id)");
            bool on = a.Bool("freeze", true);
            C.Parcels.FreezeUser(to, on);
            return new JsonObject { ["frozen"] = on, ["id"] = to.ToString() };
        });

        // land changing hands and shape
        Op("sell", $"{{price (L$), to? (one person may buy) | to_group?, objects? (the person's objects on it go with it), {At}}} -> About Land: "
            + "put it up for sale (to anyone needs a price above 0); the person confirms", (a, ct) => J(p.SellAsync(a, ct)));
        Op("stopSelling", $"{{{At}}} -> About Land: not for sale any more", (a, ct) => J(p.StopSellingAsync(a, ct)));
        Op("buy", $"{{{At}, group? (buy for a group of mine with its land-deed power)}} -> About Land: buy a parcel for sale at its price "
            + "(buying means agreeing to the estate's covenant: show it with covenant); the person confirms", (a, ct) => J(p.BuyAsync(a, ct)));
        Op("buyPass", "{at? (default: the parcel here if it sells passes, else the one that just turned me away)} -> {parcel, price, hours, "
            + "until}: a pass onto a pass-restricted parcel; the person confirms", (a, ct) => J(p.BuyPassAsync(a, ct)));
        Op("abandon", $"{{{At}}} -> {{abandoned}}: give my parcel back to the estate (Governor Linden on the mainland); the person confirms",
            (a, ct) => J(p.AbandonAsync(a, ct)));
        Op("deed", $"{{group? (default: the parcel's group), {At}}} -> About Land: give my parcel to a group; the person confirms",
            (a, ct) => J(p.DeedAsync(a, ct)));
        Op("reclaim", $"{{{At}}} -> About Land: as the region's owner, take a parcel back from its owner; the person confirms",
            (a, ct) => J(p.ReclaimAsync(a, ct)));
        Op("divide", "{half: north|south|east|west (of the parcel I stand on) | west, south, east, north (metres, in 4 m steps), at?} -> "
            + "{rest, new_parcel}: split a parcel; the new part keeps its settings; the person confirms", (a, ct) => J(p.DivideAsync(a, ct)));
        Op("join", "{at: [[x, y], [x, y]] (a point in each parcel) | west, south, east, north} -> {joined, parcel}: every parcel the "
            + "rectangle touches becomes one (all the same owner's; the largest's settings stay); the person confirms", (a, ct) => J(p.JoinAsync(a, ct)));
        Op("terraform", $"{{action: {string.Join("|", ParcelOps.Actions)}, at? ([x, y]; default where I stand), size? (brush 1-4 m, 2), "
            + "seconds? (how long the brush is held, 1; 0.05-10), height? (flatten toward it), parcel? (the whole parcel: the person confirms), "
            + "west, south, east, north? (an area: the person confirms)}} -> {action, height_before, height_after}: reshape the land as the "
            + "viewer's land brush (revert: back to the region's last bake)", (a, ct) => J(p.TerraformAsync(a, ct)));
        Op("holdings", "{group?} -> {parcels: [{name, region, area, go_to}], total_area, billable_area, group_contributions}: My Land "
            + "Holdings (group: that group's land)", (a, ct) => J(map.HoldingsAsync(a, ct)));
        Op("contribute", "{group, area (m², 0: none)} -> {group, contribution}: how much of my land allowance a group may use for its land "
            + "(on Second Life it counts toward my land use fee); the person confirms", (a, ct) => J(map.ContributeAsync(a, ct)));
        Op("mapItems", $"{{type: {string.Join("|", LandMap.ItemTypes)}, region? (people: this region by default; the rest: the whole grid), "
            + "limit=20}} -> {{items: [{{name?, area?, price?, people?, region, at, go_to, distance_m}}], found}}: the world map's items, nearest first",
            (a, ct) => J(map.MapItemsAsync(a, ct)));

        // the region and estate
        Op("region", "-> {name, type, maturity, agent_limit, object_bonus, water_height, terrain limits, settings, debug, i_manage, "
            + "restart_schedule?}: World > Region/Estate, for anyone", (_, ct) => J(e.RegionAsync(ct)));
        Op("covenant", "-> {estate, estate_owner, changed?, covenant (the text)}: the estate's covenant (About Land > Covenant)",
            (_, ct) => J(e.CovenantAsync(ct)));
        Op("estate", "-> {estate, owner, managers, allowed, allowed_groups, banned: [{id, name, banned?, banned_by?}], public, ...}: the "
            + "estate's lists (its owner and managers)", (_, ct) => J(e.EstateAsync(ct)));
        Op("estateAdd", "{list: managers|allowed|allowed_groups|banned, to | ids | group, all_estates?} -> estate: onto an estate list "
            + "(banned: kept out of the whole estate); the person confirms", (a, ct) => J(e.ChangeListAsync(a, add: true, ct)));
        Op("estateRemove", "{list, to | ids | group, all_estates?} -> estate: off an estate list; the person confirms",
            (a, ct) => J(e.ChangeListAsync(a, add: false, ct)));
        Op("kick", "{to | ids} -> {kicked, still_here?}: out of the estate (sent home), as Region/Estate > Kick; the person confirms",
            (a, ct) => J(e.KickAsync(a, ct)));
        Op("sendHome", "{to | ids | everyone: true} -> {sent_home}: teleport them home (everyone: all but me); the person confirms",
            (a, ct) => J(e.SendHomeAsync(a, ct)));
        Op("message", "{text, estate? (the whole estate; default the region)} -> {sent_to}: a notice to everyone there, as the estate's; "
            + "the person confirms the words", a => e.Message(a));
        Op("setRegion", "{block_terraform?, block_fly?, block_fly_over?, allow_damage?, restrict_push?, allow_land_resell?, "
            + "allow_parcel_changes?, block_parcel_search?, agent_limit?, object_bonus?, maturity? (general|moderate|adult)} -> region: "
            + "the region's settings (the rest kept); the person confirms", (a, ct) => J(e.SetRegionAsync(a, ct)));
        Op("regionDebug", "{scripts_disabled?, collisions_disabled?, physics_disabled?} -> the three: the emergency switches for a "
            + "lag or griefing attack (each kept unless named); the person confirms", (a, ct) => J(e.DebugAsync(a, ct)));
        Op("restart", "{delay? (seconds, 30-240, 120) | cancel: true} -> {restart_in_seconds}: restart the region, everyone warned "
            + "(cancel: stop one counting down); the person confirms a restart", a => e.Restart(a));
        Op("topScripts", "{owner? | object? | parcel? (a name to filter by), limit=15} -> {objects: [{id, name, owner, ms, at}], total}: "
            + "the region's busiest scripts (estate managers)", (a, ct) => J(e.TopAsync(a, colliders: false, ct)));
        Op("topColliders", "{owner? | object? | parcel?, limit=15} -> {objects: [{id, name, owner, collisions, at}], total}: the "
            + "region's top colliders (estate managers)", (a, ct) => J(e.TopAsync(a, colliders: true, ct)));
        Op("returnObjects", "{ids (from topScripts or topColliders)} -> {returned}: back to their owners from anywhere in the region; "
            + "the person confirms", a => e.ReturnObjects(a));
        Op("bakeTerrain", "-> {baked}: the land as it is now becomes what terraform's revert returns to; the person confirms", _ => e.Bake());
    }

    static async Task<JsonNode?> J(Task<JsonObject> t) => await t;
}
