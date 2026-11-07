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

## By hand (B9's first part; Claude Opus 5.5, 2026-11-02)

The user's idea (2026-10-30): the assistant finds a thing and places it roughly; the person fine-tunes where it stands
and how it's turned by hand, which a headset is good at and voice is slow at. The first version (grab with the trigger,
the thing following the hand, the stick pushing, pulling and turning it, A or X for fine) confused the user at their
first try (2026-11-02: a readout that stayed put beside the thing, controls nothing showed, a stick that fought the
hand), so it was reworked the same day to their design: **select by tapping, then the viewer's handles**, everything
done through things you can see and drag, the controller's buttons and sticks keeping their usual jobs. Code:
`Session/Building/HandMove.cs` (the rules, the network, undo, what came of it, selection, straightening: any host),
`Session/Building/HandBuildApi.cs` (SojournHandBuild), `Client/Building/Gizmo.cs` (the handles' geometry and the drag
maths, no drawing), `Client/Building/HandBuild.cs` (selection, the gizmo drawn, the readout, the desktop;
`HandBuildPainter.cs`), the drawing's hold in `Render/World/MotionSmoother.Held.cs`. Controls: `docs/vr.md` > Controls
> Building by hand.

- **On and off**: menu > moving > build by hand (`build_by_hand`), a thing's card's "move by hand" (on, with that thing
  selected), the assistant's `hands` tool (SojournHandBuild `start {id?}`); off by the readout's ×, the same switch,
  the assistant (`stop`), Esc on the desktop, or leaving the region. Not kept between sessions. Off, everything is as
  it was.
- **Selecting**: while it's on, a tap of the pointer (the trigger pressed and let go, or a press that moves) on a thing
  the person may move selects it rather than touching it (as the viewer's edit tool selects); a still hold still
  brings up its card. A tap anywhere else (the sky, the ground, someone, a thing they may not move) lets the selection
  go and does what that tap always does: touches, sits. The pointer turned off (A) lets it go too (the user,
  2026-11-02); the pointer stays the person's own switch, which building doesn't hold up. Building coming on, or a
  thing selected for them (the card, the assistant), turns the pointer on, since everything is worked with it. Selection is the client's: the
  region hears ObjectSelect only while a handle is dragged. A thing on a media face: a tap on the face is the page's.
- **What may be moved**: the whole linked set (as the viewer's move with "edit linked parts" off; single prims of a set
  can come later), in the region the person is in, not worn (attachments later), with modify rights, or move rights on
  their own (a locked thing says so). Someone else's thing they may modify only once chosen on purpose (its card, the
  assistant), never by a tap. RLV's `@edit`, `@editobj`, `@editworld` hold (`RlvEngine.CanEdit`), with the quiet notice
  as for any refusal. Nothing spends. Why a tap didn't select shows on the readout. A selection that may no longer be
  moved (taken, returned, its rights changed) lets go by itself.
- **The handles** (`Gizmo.cs`; the viewer's `LLManipTranslate` and `LLManipRotate`, done for a ray): at the selected
  thing's middle (its linked set's box centre, the viewer's selection centre), in the axis colours every build tool
  uses (x red, y green, z blue), drawn over everything:
  - **arrows**, one per axis, each pointing to the side the eye is on (the viewer turns its arrows to the camera's
    side): dragging one moves the thing along that axis only. The ray meets a plane holding the axis and facing the ray
    as it was at the press, and the point's distance along the axis is the move, as the viewer reads the mouse. An
    arrow seen end-on (within about 12 degrees) is faded and can't be taken, as the viewer hides it.
  - **plane squares**, one per pair of axes, between the arrows: dragging one moves the thing across that plane (the
    xy square: along the floor, its height kept).
  - **rings**, one per axis, round the thing: dragging one turns it about that axis, about its middle (so a linked set
    turns in place). The turn is the angle the ray's point on the ring's plane sweeps round the middle, added up frame
    by frame; seen nearly edge-on, the ray's travel along the ring's tangent at the grab, over the ring's radius. Far
    halves are drawn dimmer.
  - **the orb** in the middle: dragging it moves the thing freely, the point grabbed riding the ray at the distance it
    was grabbed at (a lever from the hand: a hand reaching into the orb carries it one to one). It only moves, never
    turns it (the user, 2026-11-02: keep it that way).
  - The ray passes through the thing to reach them. Under the ray a handle lights and thickens; during a drag the
    others fade and the dragged one shows its line (an arrow's axis right across, a ring's start and now). Their size
    follows the thing's (rings 15% outside its box) within 5 to 12 degrees of view, so they hug a small thing nearby and
    stay graspable far off; the viewer keeps its own a fixed size on screen. Picking allows 1.3 degrees from the ray's
    start (at least a centimetre) around each handle; of several, the one the ray passes closest to for its size.
  - Axes: the region's by default (`build_axes` world), or the thing's own (local), as the viewer's ruler mode.
- **Fine** (the readout's key, `build_fine`; not kept): drags move and turn a fifth as far.
- **Snapping** (`build_snap`, off; `build_grid`, 0.5 m, the viewer's; `build_snap_angle`, 15 degrees; kept in
  `build-settings.json`): an arrow or square on the region's axes puts the root's coordinate on the grid (from the
  region's corner, as the viewer's); on local axes the distance moved goes in grid steps; rings turn in steps; the orb
  puts the root's position on the grid. The viewer snaps a translate only while the mouse is over its snap guide; here
  it's the switch.
- **The network**, as the viewer's: while a handle is dragged the thing is selected (ObjectSelect; the region holds a
  selected physical thing still) and let go of after; MultipleObjectUpdate with position, rotation and the linked flag
  on the root (as LLSelectMgr packs it with UPD_LINKED_SETS), at most 10 a second while dragged and only when it moved
  (0.5 mm or 0.05 degrees), and once more on release. The official viewer today moves the selection locally while
  dragging and sends on mouse up (its `llmaniptranslate.cpp` still says "throttle updates to 10 per second" from when it
  sent while dragging); sending while dragged lets the region and anyone watching see the move as it happens. Clamped
  inside the region (no carrying across a border; the viewer clips to the regions it can see) and under 4096 m
  (MAX_OBJECT_Z).
- **Drawn where it's put**: while dragged and after, the renderer draws the root where it was put, whatever the region's
  echoes say (the viewer's overrideObjectUpdates does the same for a selection being dragged), until the region's own
  word matches (2 cm, a degree) or 4 s pass, then the region's word again: a refused move snaps back.
- **What came of it** (`HandMove.Classify`, `HandMove.Last`, SojournHandBuild `state`), read from the region's updates
  within 3 s of letting go, and from any refusal notice that answered the drag: placed (2 cm, a degree); refused (it
  stayed where it was: within 2 cm, or with a notice within 10 cm and nearer where it was than where it was put, since
  the region takes the first few centimetres before the edge); partly refused (a notice, and it went part of the way:
  the region took the updates up to where the land stops it); nudged (no notice, within 10 cm and 5 degrees: the region
  adjusting it); moved elsewhere (no notice: physics, a script); or gone (returned or deleted). The state carries where
  the hand put it (`put`, `off_m`) and the region's words (`region_said`) for the assistant. The readout says each but
  placed in words that don't suggest something else moved it ("The land there doesn't allow it, so it stayed put.",
  "The region stopped it at the edge of land that doesn't allow it.", "It ended up 1.5 m from where you let go of it."),
  and a drag already refused isn't drawn where it was let go.
- **The region's refusals** (`HandMove.ClaimAlert`, through `AgentSession.ClaimAlerts`): Second Life answers a move
  onto land that doesn't allow the person's objects with a notice like "Can't rotate -- permission denied" (rotate,
  though it was a move: each update carries position and rotation together; seen on 2026-11-02 at
  a sandbox's edge, once per drag, early in it). A notice refusing to move, turn, position or scale that comes while a
  drag is held or within 3.5 s of letting go is taken as its answer: the readout says "The land there doesn't allow
  it." at once and the outcome after, and the feed doesn't show it. Any other notice, or one with no drag of ours
  about, goes to the feed as before. Checked by `--panel-shots` (`building's outcomes` and `building's refusals`, with
  the user's two refused drags from that log among the cases; `hb-readout-10-*.png`).
- **Undo**: each drag that moved something is one undo step ("moved NAME by hand", the placement captured at the press:
  `BuildUndo.BeforeHandMove`), in the same history as the assistant's edits, so "undo that" takes back a hand move, and
  the readout's undo takes back the last step on the selected thing (through the dispatcher, so undoing a creation
  still asks).
- **The readout** (`HandBuildPainter.cs`, the panel's look like the thing's card): the selected thing's name and ×
  (done), where it is (the region's x, y, z, each axis letter in its colour) and how it's turned (the edit floater's
  three angles), a line, and four keys, each saying how it stands and lit while on: "snap: on/off" (its grid and steps
  under the keys while on), "fine moves: on/off", "world axes" or "its own axes", undo. The line (three lines' room
  always, so the keys never shift under the pointer) says what the key under the pointer does as things stand ("Snap
  is off. Press to move in 0.5 m steps and turn in 15° steps.": the user's ask, 2026-11-02, the bare words having
  explained nothing), else what came of the last move or why a tap didn't select, else what the handle under the ray
  does ("move along x", "turn around z"), else "Point at a key to see what it does." ("Tap one of your things to move
  it." with nothing selected). The handles need no line of their own: the user found them self-explanatory. A menu
  like the others (where: `docs/vr.md` > Building by hand).
- **The user's verdicts** (2026-11-02, in the headset): the handles feel good (arrows, squares, rings and orb: hitting
  them and the directions they drag); the orb stays move-only; straightening a tilted box puts its rotation back to 0
  and keeps where it is.
- **Straightening is the assistant's** (the user, 2026-11-02: no reset button): SojournHandBuild `straighten {id,
  level?}` sets the rotation back to none (the edit floater's 0, 0, 0), or with level only takes out the tilt, keeping
  the heading; the whole linked set turns about its middle so it stays where it is; one undo step ("straightened NAME",
  "levelled NAME"). The assistant's `hands` tool has it as `straighten` ("straighten it", "reset its rotation", "level
  it").
- **The assistant**: the `hands` tool (start with a thing, stop, state, straighten); its context line says, while
  building by hand is on, what's selected (with its id, so "it" is that) and what's being dragged, and for ten minutes
  what the person last moved by hand, how far, where to and whether the region kept it. Not on incognito's blind
  allowlist (it names objects), so it runs with "let it see" as SojournBuild does.

Checked in the XR Simulator on the local grid by `python tools/handbuild.py`: a tap selecting, each kind of handle
dragged and measured against what it means (an arrow 0.3 m along its axis and nowhere else, a ring by the angle the aim
swept, the square across its plane, the orb on the ray at its distance), another thing selected by a tap, a tap on the
sky letting go, someone else's thing not selected by a tap (and sat on, when its click action reaches the client:
two runs in six on 2026-11-02, the last included), undo back to the start, straighten and level. What waits for the headset and Second Life: the readout and the card as
menus (their places, dragging, X held), a parcel that refuses a move, a no-mod or locked thing, a physical
one; the desktop's handles under the mouse are built but not yet run. On the local grid a click action set by one
client often never reaches another's, and a colour change clears it even in its maker's (TesselBuild with colour and
click action together, or a colour after it; seen 2026-11-02, not yet looked into: whether it's OpenSim's updates or
our reading of them, and whether Second Life does the same).

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

`TesselBuild modifyEach {edits: [{id, ...keys}]}`: several prims or faces with keys of their own, one undo step and one
confirmation, rights checked first, a prim's face sets as one texture entry (a modify a face would undo the one before). `TesselBuild
copyFrom {from, ids, what?, face?}`: the official editor's Copy and Paste, one undo step, saying what couldn't go. `modify`'s
`locked`: the Object tab's Locked box, the owner's own move and modify rights (the owner can always unlock).

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
read (`Session/Private/Allowlist.cs`, `harness.py`). Building by hand (above) has undo on its readout; a
Default Permissions sheet, redo and a backups list are rows that might want direct controls later.

## Testing

`python tools/building.py [--dll DIR] [--only materials,undo,scripts,perms,group,take,backup,pathfinding]` (the local
grid; Sojourn Builder on :9891 at Atelier, base `local/building/a`): a projector read back, legacy normal and specular
on every face and off one, a PBR material (the viewer's blank one) applied, overridden, added to and taken off, an
override with no material refused; undo and redo of a modify, an undo of a creation pressed, a made-then-linked build
as one step; two scripts listed, stopped, reset, recompiled, Lua refused, counted, costed and removed (pressed);
default permissions set and sent, bulk permissions on a notecard inside; a group set, a deed refused unshared and then
pressed; two objects taken separately; a backup of a two-prim build written and restored with nothing uploaded;
pathfinding said absent. What it makes it deletes.

`python tools/handbuild.py [--dll DIR] [--keep]` (the XR Simulator and the local grid; Sojourn Shaper on :9783, base
`local/handbuild`; refuses to start while another VR client runs): a platform high over Atelier and a tall box on it,
building by hand started with the box chosen, then a scripted right hand grabbing it with the ray, raising it 0.2 m
and turning it a quarter with the stick; reaching into it and moving it 0.3 m sideways; the left hand reaching in and
lifting it 0.1 m (and no teleport from that pull). After each, where the region has it and what `state` says; the undo
step in the history; three undos put it back. Shots of it held and after in `local/handbuild/shots`. What it makes it
takes again.

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
