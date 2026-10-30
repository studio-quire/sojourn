using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Leap;

/// <summary>Building, batch B6, beside TesselBuild (whose modify also took new keys: pbr, normal, specular, alpha_mode,
/// projector): materials read and uploaded, undo and redo, the Build menu's script chores, land impact and script
/// info, groups and deeding, default and bulk permissions, saving back to contents, pathfinding, and backups and
/// exports of what the person made themselves. Agents/MaterialOps.cs, BuildUndo.cs, ScriptChores.cs, ObjectInfo.cs,
/// Ownership*.cs, Pathfinding.cs, Backups.cs. What deletes, gives away, deeds, changes permissions or spends waits for
/// the person's press (Client/Building/BuildWiring.cs). Claude Opus 5.5, 2026-10-29.</summary>
public sealed class SojournBuild : SessionApi
{
    public override string Name => "SojournBuild";
    public override string Desc => "Building beyond TesselBuild: materials (PBR and legacy) read and uploaded, undo/redo of edits, scripts in bulk "
        + "(recompile for Mono/LSL/Lua, reset, run, remove), land impact, script info, object group and deeding, default and bulk permissions, "
        + "save back to contents, pathfinding, backups and Collada exports of one's own creations";

    const string Sel = "id | ids (objects; their whole linksets unless prims_only: true)";

    public SojournBuild(AgentSession s) : base(s)
    {
        // materials
        Op("materials", "{id, face?} -> {faces: [{face, pbr?: {material_id, overrides?}, legacy?: {normal, specular, ...}}], projector?}: "
            + "a prim's materials as the region has them (TesselBuild modify's pbr, normal, specular, projector set them)",
            (a, ct) => J(s.Materials.ReadAsync(a, ct)));
        Op("uploadMaterial", "{path (.gltf or .glb), material? (its index or name in the file; default the first), name?, quote?} -> "
            + "{item_id, asset_id, textures, cost}: Upload > Material: its textures uploaded first (each costs a texture upload on Second Life), "
            + "then the material item; quote: true says what it would cost; the person confirms where uploads cost",
            (a, ct) => J(s.Materials.UploadAsync(a, ct)));

        // undo
        Op("history", "{count=20} -> {undo: [{step, what, ago_s, objects, parts}], redo}: the edits undo would take back, newest first",
            a => s.Undo.History(Math.Clamp(a.Int("count", 20), 1, 200)));
        Op("undo", "{steps=1, id?} -> {undone, failed?}: takes back the last edits (TesselBuild modify, create, link, unlink; materials; "
            + "groups): only the parts they changed; a creation is undone by deleting it (to Trash, confirmed); id: the last edits of that object",
            (a, ct) => J(s.Undo.UndoAsync(a.Int("steps", 1), a.Uuid("id"), redo: false, ct)));
        Op("redo", "{steps=1, id?} -> {redone, failed?}: puts back what undo took back (not a deleted creation)",
            (a, ct) => J(s.Undo.UndoAsync(a.Int("steps", 1), a.Uuid("id"), redo: true, ct)));

        // scripts in bulk
        Op("scripts", $"{{{Sel}, name?}} -> {{scripts: [{{prim_id, prim, item_id, name, running, perms}}], count, running}}: every script in them",
            (a, ct) => J(s.ScriptChores.ListAsync(a, ct)));
        Op("recompile", $"{{{Sel}, target=mono (mono | lsl2 | luau | lsl-luau), running?, name?}} -> {{compiled, failed?: [{{..., diagnostics}}], "
            + "skipped?}}: Recompile Scripts in Selection for that target (Lua and LSL-on-Luau only where the region runs them); needs copy "
            + "and modify on each script", (a, ct) => J(s.ScriptChores.RecompileAsync(a, ct)));
        Op("resetScripts", $"{{{Sel}, name?}} -> {{reset, count}}: Reset Scripts in Selection", (a, ct) => J(s.ScriptChores.ResetAsync(a, ct)));
        Op("runScripts", $"{{{Sel}, running: true|false, name?}} -> {{running, count, changed}}: Set Scripts to Running / Not Running",
            (a, ct) => J(s.ScriptChores.SetRunningAsync(a, ct)));
        Op("removeScripts", $"{{{Sel}, name?}} -> {{removed, count}}: Remove Scripts from Selection (Firestorm); the person confirms",
            (a, ct) => J(s.ScriptChores.RemoveAsync(a, ct)));

        // what it costs and runs
        Op("landImpact", "{id | ids, prims?: true (each prim's own cost)} -> {objects: [{id, name, land_impact, land_impact_exact, physics, "
            + "prim_count}], land_impact, selected?}: the edit floater's land impact (the region's GetObjectCost)", (a, ct) => J(s.ObjectInfo.LandImpactAsync(a, ct)));
        Op("scriptInfo", "{id | ids} -> {objects: [{id, name, scripts, running}], scripts, running}: Firestorm's Script Info (counts; time "
            + "and memory are the region's Top Scripts, SojournLand topScripts)", (a, ct) => J(s.ObjectInfo.ScriptInfoAsync(a, ct)));

        // ownership and permissions
        Op("group", "{id | ids, group_id | group (a group's name, or none)} -> {group, count}: the objects' group (the edit floater's Group: "
            + "share with group and deeding go by it); the person confirms", (a, ct) => J(s.Ownership.SetGroupAsync(a, ct)));
        Op("deed", "{id | ids, group_id?} -> {deeded_to, count}: Deed to their group (it then belongs to the group, for good: needs "
            + "transfer rights and the group's deed power); the person confirms", (a, ct) => J(s.Ownership.DeedAsync(a, ct)));
        Op("defaultPerms", "-> {default_perms: {objects|uploads|scripts|notecards|gestures|wearables|materials|settings: {next_owner, "
            + "share_with_group, everyone_copy}}}: the viewer's Default Permissions for new things", _ => s.Ownership.DefaultsJson());
        Op("setDefaultPerms", "{kind, next_owner? (letters of M, C, T), share_with_group?, everyone_copy?} -> {kind, default_perms}: "
            + "changes one kind's defaults (objects are also told to the grid); the person confirms", (a, ct) => J(s.Ownership.SetDefaultsAsync(a, ct)));
        Op("bulkPerms", "{ids (objects: their contents) | folder_id (an inventory folder, and the folders in it), types? ([script, notecard, "
            + "texture, object, ...]; default all), next_owner?, share_with_group?, everyone_copy?} -> {changed, skipped}: Edit > Set "
            + "Permissions on everything of those types; the person confirms", (a, ct) => J(s.Ownership.BulkPermsAsync(a, ct)));

        // contents
        Op("saveBack", "{id | ids} -> {count, saved_into}: Save Object Back to Object Contents (for something rezzed from an object; it "
            + "replaces the item it came from)", (a, ct) => J(s.Build.DeRezAsync(a, DeRezDestination.TaskInventory, ct)));

        // pathfinding
        Op("pathfinding", "-> {region_enabled, navmesh: {status, version?}, characters?: count}: the region's pathfinding state",
            (a, ct) => J(s.Pathfinding.StatusAsync(a, ct)));
        Op("linksets", "-> {linksets: [{id, name, use, walkable?, a, b, c, d, phantom, "
            + "land_impact, ...}]}: the pathfinding linksets window", (a, ct) => J(s.Pathfinding.LinksetsAsync(a, ct)));
        Op("setLinksets", "{id | ids, use? (walkable | static_obstacle | dynamic_obstacle | material_volume | exclusion_volume | movable_obstacle "
            + "| movable_phantom), a?, b?, c?, d? (walkability 0-100 per character type)} -> {changed}: their pathfinding use",
            (a, ct) => J(s.Pathfinding.SetLinksetsAsync(a, ct)));
        Op("characters", "-> {characters: [{id, name, owner, cpu_us (the viewer's CPU time), shape, length, radius}]}: the region's pathfinding characters",
            (a, ct) => J(s.Pathfinding.CharactersAsync(a, ct)));
        Op("rebake", "-> {requested}: rebake the region's navmesh (after changing linksets)", (a, ct) => J(s.Pathfinding.RebakeAsync(a, ct)));

        // backups and exports
        Op("canExport", "{id} -> {exportable, why?, prims, textures: {mine, not_mine}}: whether an object can be backed up or exported "
            + "under Firestorm's rules (only what the person made themselves)", (a, ct) => J(s.Backups.CheckAsync(a, ct)));
        Op("backup", "{id, name?} -> {path, prims, assets, left_out}: Firestorm's Backup of a linkset the person made (shape, faces, "
            + "settings, its textures and sculpt maps they made) into Documents\\Sojourn\\Backups as .oxp", (a, ct) => J(s.Backups.BackupAsync(a, ct)));
        Op("backups", "-> {backups: [{name, path, size, made}]}: the backups made so far", _ => s.Backups.List());
        Op("restore", "{name (one of backups) | path, pos?, quote?} -> {root_id, prims, uploaded, cost}: rebuilds a backup in the world "
            + "(Firestorm's Import Linkset): its textures uploaded again where this grid hasn't got them (they cost uploads on Second Life; "
            + "quote: true says how many); the person confirms", (a, ct) => J(s.Backups.RestoreAsync(a, ct)));
    }

    static async Task<JsonNode?> J(Task<JsonObject> t) => await t;
}
