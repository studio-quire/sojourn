# Building (SojournBuild, TesselBuild's new keys)

Written by Claude Opus 5.5, 2026-10-29 (batch B6 of `docs/viewer-gaps.md` > Closing the rest). Agent-facing. What the
viewer's build tools do beyond what TesselBuild already did: materials (PBR applied, overridden, taken off and
uploaded; the legacy normal and specular maps), projectors, undo and redo, the Build menu's script chores, land impact
and script info, an object's group and deeding, default and bulk permissions, saving back to contents, taking
separately, pathfinding, and backups of what the person made themselves. Each from the official viewer's source
(develop) or Firestorm's (master), named below; an op on `SojournBuild` or a key on `TesselBuild modify` (so `build`
specs take it per part); and, for what deletes, gives away, deeds, changes permissions or spends, a confirmation the
person presses. No menu rows or settings: the person asks the assistant. A face's media settings are media's
(`SojournMedia settings`, `configure`, `remove_media`: docs/media.md), not repeated here.

Code: `Core/Agents/MaterialOps.cs`, `BuildUndo.cs`, `ScriptChores.cs`, `ObjectInfo.cs`, `Ownership.cs`,
`Pathfinding.cs`, `Backups.cs`, `AgentSession.Building.cs`; `BuildOps.cs` (the new modify keys, the projector,
undo's records, take separately, save back), `UploadOps.cs` (default permissions), `Scripts/ScriptEditor.cs` (compiling
for a target, an experience kept, Lua's targets for inventory scripts); `Leap/Apis/SojournBuild.cs`; the client half
`Client/Building/BuildWiring.cs`. Hunks in shared files: `TesselApis.cs` (modify awaits materials and is one undo step;
take's `separate`), `InventoryOps.cs` and `GestureOps.cs` (default permissions; Lua targets), `Program.cs` (one line).

## Materials (MaterialOps; TesselBuild modify keys, on `face` or every face)

- `pbr`: a material (inventory item or asset id) applied, `false` to take it off, or `{material?, base_color?,
  metallic?, roughness?, emissive?, alpha_mode? (opaque|blend|mask), alpha_cutoff?, double_sided?, base_color_texture?,
  normal_texture?, metallic_roughness_texture?, emissive_texture? (false: none), repeats?, offset?, rotation? (degrees,
  all maps), reset?}`. Through ModifyMaterialParams (`llgltfmateriallist.cpp`): an LLSD array of `{object_id, side,
  asset_id?, gltf_json}`, always with `gltf_json` (`""` clears). A new material starts its override afresh but keeps
  its texture transforms (queueApply's setBaseMaterial); overrides on their own go onto the face's current override, as
  the live material editor does, and are refused on a face with no PBR material. Taking one off is the null asset with
  `""`. Override JSON is LibreMetaverse's `AssetMaterial` (it mirrors LLGLTFMaterial: defaults nudged by an epsilon
  when set explicitly, `ffffffff-...` for "no texture"). Each face's material id comes from the renderer's
  `RenderMaterialTracker` (LibreMetaverse skips extra param 0x80), overrides from LibreMetaverse's
  `GLTFMaterialOverrides`.
- `normal {texture | false, repeats?, offset?, rotation?}`, `specular {texture | false, color?, glossiness? (0-255, the
  exponent), environment? (0-255), repeats?, offset?, rotation?}`, `alpha_mode` (none, blend, mask, emissive) and
  `alpha_cutoff` (0-255): the legacy material, PUT to RenderMaterials (`llmaterialmgr.cpp`: zlib over binary LLSD
  `{FullMaterialsPerFace: [{Face, ID: local id, Material}]}`, LLMaterial's keys with offsets, repeats and rotation x10000).
  The face's current material is fetched first and changed, not replaced. A fresh one takes alpha mode none on an
  opaque face and blend on a see-through one (LLMaterial's own default is blend, which would send an opaque face to the
  alpha pass; the viewer's panel goes by the texture's alpha, which we'd have to decode); one left with nothing but its
  default is taken off.
- `projector {texture, fov? (degrees, 0-172), focus? (-20-20), ambiance? (0-1)}` or `false`: extra param 0x40 (texture
  and fov/focus/ambiance), new ones at LLLightImageParams' defaults (90 degrees, 0, 0); turning one on also lights the
  prim if it wasn't (a projector only casts from a light).
- "Every face" is the viewer's getNumTEs: a prim's volume faces from the renderer's `VolumeBuilder`, one for a sculpt,
  a mesh's high-LOD submeshes read from its asset (8 until then; filtered before sending).
- `SojournBuild materials {id, face?}` reads it all back: each face's PBR id and override JSON, its legacy material,
  the projector.
- `SojournBuild uploadMaterial {path, material?, name?, quote?}`: Upload > Material (`llmaterialeditor.cpp`): the glTF
  file read by LibreMetaverse's `GltfLoader`, its textures uploaded first (each a texture upload's fee; the material item
  itself is free, CreateInventoryItem then UpdateMaterialAgentInventory), permissions from the person's Uploads
  defaults. Confirmed as a spend where uploads cost.
- Faces edited by modify now carry the prim's media URL in ObjectImage, as `sendTEUpdate` does (LibreMetaverse sent an
  empty one, which could take a face's media off on SL).

## Lights and projectors taken off (World/ExtraParamsSync.cs)

LibreMetaverse fills a prim's extra parameters from each full update but never clears one the update leaves out, so a
light a script turned off, or a projector taken off, stayed on the prim (drawn, read back, kept by undo). Each full and
compressed update now clears the light and projector it didn't carry, as the viewer's in-use flags do. Flexible and
sculpt data are left alone (the renderer reads them on workers after checking, and both go with a path or shape change).

## Undo and redo (BuildUndo)

The viewer's undo covers a selection's moves, turns and sizes; this covers every modify key, materials, groups, links
and creations made through the agent API, whoever called. Before an edit, what it will change is captured (placement,
scale, shape, faces with their legacy material ids, name, description, material, flags, physics shape, light,
projector, flexi, sculpt, probe, animesh, click action, permissions, sale, group, PBR ids and overrides), and undo puts
back only those parts, so a script's own changes meanwhile stay. A modify over several prims is one step. A creation is
undone by deleting it (to Trash; the undo is confirmed as a delete); prims made one by one and then linked fold into one
creation, so undoing a `build` takes the whole object away in one press. Link and unlink undo each other. Redo puts
back what undo took (not a deleted creation). `history {count}`, `undo {steps, id?}`, `redo {steps, id?}`; 200 steps
kept, this session only. Putting permissions or a sale back is confirmed as a permissions change. Names, descriptions
and permissions live in the object's properties, which the region sends only when asked: undo asks afresh before
capturing what redo needs; the first capture before an edit uses what's known (a rename right after another rename
may go back to the older name).

## Scripts in bulk (ScriptChores; the Build > Scripts menu, `llcompilequeue.cpp`)

`scripts`, `recompile {target}`, `resetScripts`, `runScripts {running}`, `removeScripts` on the objects named (their
whole linksets, or `prims_only`), one script at a time as the queue goes. Targets are the viewer's four: `mono`,
`lsl2`, `luau` (Lua) and `lsl-luau` (LSL on the Luau VM); the Lua two only where the region's `LuaScriptsEnabled`
feature is on. A recompile reads the script's text and saves it back through UpdateScriptTask, skipping scripts
without copy and modify and those of the other language (Lua by the item's subtype, SST_LUA), as the queue does; a
script's experience is kept (GetMetadata, then `experience` in the save: without it a save drops the association, as
the external editor's saves did before). `TesselInventory createScript`/`writeScript` take `luau` and `lsl-luau` too
(UpdateScriptAgent with the target; LibreMetaverse's own takes only Mono or not). Removing is confirmed as a delete.

## Cost and scripts (ObjectInfo)

`landImpact {ids, prims?}`: GetObjectCost (`linked_set_resource_cost` rounded up is the land impact, as the edit
floater shows), ResourceCostSelected's physics, streaming and simulation for the selection. `scriptInfo`: Firestorm's
Script Info counts (scripts, running); its time and memory come from Firestorm's LSL bridge, which Sojourn doesn't
wear, so they're the region's Top Scripts (`SojournLand topScripts`, estate managers).

## Group, deeding, permissions (Ownership)

- `group {ids, group_id | group}`: ObjectGroup (roots), confirmed as a permissions change (what's shared goes to that
  group's members).
- `deed {ids}`: ObjectOwner with owner null and the object's group (`llselectmgr.cpp` sendOwner), confirmed as a
  give. Refused before the person is asked when there's no group or their roles lack the deed power; the rest of the
  edit floater's rules (theirs, transferable, set to that group, shared with it) are read from properties asked afresh,
  so the confirmation says "this will be refused" and the deed refuses (properties lag a change just made).
- `defaultPerms` / `setDefaultPerms {kind, next_owner, share_with_group, everyone_copy}`: the Default Permissions
  floater's eight kinds (`llfloaterperms.cpp`), kept per account in `building.json`. Until set, the viewer's own:
  next owner may only transfer objects, uploads, scripts and wearables; notecards and gestures go on fully; materials and
  settings modify and transfer. Objects' are told to the grid through AgentPreferences (`default_object_perm_masks`)
  on the change and at each region's caps, but only once the person has set them here (we don't overwrite what
  another viewer set). Uploads (`TesselAgent upload`, `uploadMesh`, materials, restored textures), scripts, notecards
  and gestures made by the inventory ops use theirs. The next owner must keep copy or transfer. Confirmed as a
  permissions change.
- `bulkPerms {ids | folder_id, types?, next_owner?, share_with_group?, everyone_copy?}`: Edit > Set Permissions
  (`llfloaterbulkpermission.cpp`) on the objects' contents (UpdateTaskInventory per item, prims we may modify), or, as
  an addition, on an inventory folder and its subfolders. No copy for the next owner forces transfer on (fair use);
  settings items keep copy. Confirmed.

## Contents and taking

`TesselBuild take {..., separate: true}`: an item each (Firestorm's take separately: one DeRezObject per root) rather
than one combined. `SojournBuild saveBack {id}`: Save Object Back to Object Contents (DeRez destination 2 to the
object's `FromTaskID`, refused for worn objects and things not rezzed from contents); confirmed as a delete, since it
replaces the item it came from. DeRez now carries the active group, as the viewer's.

## Pathfinding (Pathfinding; Second Life only)

`pathfinding` (RetrieveNavMeshSrc's presence, NavMeshGenerationStatus, AgentState), `linksets` (RegionObjects),
`setLinksets {ids, use, a-d}` (ObjectNavMeshProperties: only what changes; volumes only where `can_be_volume`),
`characters` (CharacterProperties), `rebake` (`{command: rebuild}`). Uses: walkable, static obstacle, movable obstacle,
material volume, exclusion volume, movable phantom. The navmesh's view is the long tail.

## Backups (Backups; Firestorm's rules)

`canExport {id}`, `backup {id, name?}`, `backups`, `restore {name | path, pos?, quote?}`. Firestorm's rules
(`fsexportperms.cpp`): on Second Life a prim only if the person owns and created it, meshes never, a texture or sculpt
map only if an item of it in their inventory (Trash included) was created by them; others' textures become plywood, a
sculpt map not theirs leaves the build out. On OpenSim the grid's `ExportSupported` decides (creator or the export
permission; unset, the owner with full permissions). The file is Firestorm's `.oxp` format 2 (zlib over binary LLSD:
format_version, client, creation_date, author, grid, linkset, prim by Firestorm's keys and the viewer's asLLSD forms,
asset with J2C textures), written only to `Documents\Sojourn\Backups`. Contents aren't kept yet (said in `left_out`).
A restore makes each prim, sets its shape, faces and settings and links them; textures are uploaded again only where
the grid hasn't got them (the same grid: none, free), then confirmed as a spend where uploads cost. Restored builds are
never temporary and never for sale.

## What waits for the person

Delete: `removeScripts`, `saveBack`, an undo that deletes a creation. Give: `deed`. Permissions: `group`,
`setDefaultPerms`, `bulkPerms`, an undo or redo that puts permissions or a sale back. Spend (where uploads cost):
`uploadMaterial`, `restore`. Through `Confirmations.Cases` from `BuildWiring.cs`, no hunk in `Confirmations.cs`;
`deed` (its stable rules), `group` and `bulkPerms` with what would fail refused first (`Confirmations.Refusals`). Materials, projectors,
recompiles, resets, running, take separately and pathfinding aren't gated (the viewer doesn't ask either).

## The assistant's side

The assistant is the main builder. Its `undo` and `scripts` tools (`assistant/moretools.py`) cover undo, redo and
history, and the script chores; `inspect` adds land impact and script info; the materials, projector and the rest
reach it through `TesselBuild modify` and `client_api` (its description and `prompt-job.md` name them). None of
SojournBuild is on incognito's blind allowlist: every op names objects by id and reads or changes the world. With "let
it see" they run as usual, except that a `backup` is named as an incognito one, which the assistant's file tools never
read (`Session/Private/Allowlist.cs`, `harness.py`). If the person builds by hand later, a Default Permissions sheet,
undo/redo keys and a backups list are the rows that would want direct controls.

## Testing

`python tools/building.py [--dll DIR] [--only materials,undo,scripts,perms,group,take,backup,pathfinding]` (the local
grid; Sojourn Builder on :9891 at Atelier, base `local/building/a`): a projector read back, legacy normal and specular
on every face and off one, a PBR material (the viewer's blank one) applied, overridden, added to and taken off, an
override with no material refused; undo and redo of a modify, an undo of a creation pressed, a made-then-linked build
as one step; two scripts listed, stopped, reset, recompiled, Lua refused, counted, costed and removed (pressed);
default permissions set and sent, bulk permissions on a notecard inside; a group set, a deed refused unshared and then
pressed; two objects taken separately; a backup of a two-prim build written and restored with nothing uploaded;
pathfinding said absent. What it makes it deletes.

## Checks only Second Life can do

- PBR: applying a real material from inventory (and a no-modify one, refused or not), overrides on a mesh face,
  `uploadMaterial` of a .gltf with textures (the fee is the textures'), what the region answers ModifyMaterialParams
  when a face index is past the object's faces.
- Lua: `recompile` with `luau` and `lsl-luau` on a Lua-enabled region, `createScript {target: luau}`.
- An experience script recompiled keeps its experience.
- Default object permissions taking on a new prim after `setDefaultPerms` (AgentPreferences).
- Deeding to a real group with the power; `saveBack` on something rezzed from a box's contents.
- Land impact on a mesh (GetObjectCost), Script Info counts on a busy object.
- Pathfinding's five on a pathfinding region (a sandbox like the Pathfinding test regions); what `linksets` lists for
  someone who isn't an estate manager.
- A backup refused for something bought, a mesh refused, a texture made by someone else becoming plywood; a restore
  on the Beta grid (textures uploaded, L$ on Aditi).
