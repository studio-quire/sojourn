# Inventory and the avatar's own (SojournInventory, SojournAo)

Written by Claude Opus 5.5, 2026-10-29 (batch B7 of `docs/viewer-gaps.md` > Closing the rest). Agent-facing.
Thumbnails (an item's picture seen, and set), favourites, protected folders, a client-side AO from an inventory folder
in Firestorm's format, and Firestorm's pose stand. Each as the viewers do it (their source read for each, named
below), ops on `SojournInventory` and `SojournAo` (`client_api`, `api_help`), the AO's switches also on
`SojournSettings` (section you). What deletes waits for the person's press.

Code: `Core/Agents/InventoryMeta.cs` (thumbnails, favourites), `InventoryOps.cs` (protected folders, `Locked`,
`Refusal`), `AoEngine.cs` (the overrider, pure logic), `AoFolders.cs` (the folder format, ZHAO-II notecards),
`AoDriver.cs` (the AO's session half), `PoseStand.cs`, `AccountStore.cs` (`inventory.json` per account),
`AgentSession.Inventory.cs`; `Leap/Apis/SojournInventory.cs`, `SojournAo.cs`; the client half
`Client/Inventory/InventoryWiring.cs` (the APIs registered, confirmations, the refusal before asking, OpenJPEG for
pictures, the settings). Hunks in shared files: `SelfAnimations.cs` (the AO hooked to the region's list, "my AO" in
`animations`, a stop that keeps permissions), `AgentSession.Events.cs` (one line: the relay's owner-say taken out of
chat), `Confirmations.cs` (`Refusals`, a list beside `Cases`), `Program.cs` (one line).

## Loading (AgentInventory)

At login the person's inventory is fetched in the background (`Loading`, which the outfit, the AO and gestures wait
for), then the Library, as the viewer's background fetch does. `LoadSubtreeAsync` fetches every folder under a root
over FetchInventoryDescendents2 / FetchLibDescendents2, 10 folders a request and 4 requests at once, a folder that
doesn't answer retried once; one walk per root at a time, shared by callers, running on when a caller stops waiting.
`CollectAsync` (LLInventory `collectDescendantsIf`) walks the store after it and says `partial` when folders didn't
answer; `SojournEnvironment mine` waits at most 5 s and says `partial` while the Library is still arriving. The
batching matters: the Library is ~490 folders and ~2,100 items on SL, and fetched a folder at a time it took far longer
than the API's 15 s.

On Second Life the outfit's objects are attached once it has loaded (`Look.RezOutfitAttachmentsAsync`): only those the
avatar doesn't wear, as the current region has it, and only once that region has been current 10 s. Right after a
teleport the avatar arrives before its attachments, and a worn object attached again drops off and comes back; before
the wait, a teleport home in the first minute or so after logging in (while a big inventory loads) could do that (a
blind tester's skirt dropped off and came back on a teleport home, 2026-10-31: the only way of ours we found).
Nothing of ours touches attachments on a teleport or crossing otherwise: LibreMetaverse 3.2.0's own outfit resend
after each region change (RezMultipleAttachmentsFromInv with FirstDetachAll) is off with its `SendAppearance`.

## Thumbnails and favourites (InventoryMeta)

The official viewer's 2023-2025 inventory keeps both on the inventory service beside each item and folder:
`"thumbnail": {"asset_id"}` and `"favorite": {"toggled"}` (`llinventory.cpp`; the old fetch caps carry them under
`"metadata"`). LibreMetaverse 3.2.0 parses neither, so they're read from AIS3's own answers (`GET
category/<id>/children?depth=50`, `item/<id>`, `category/<id>`) into a cache, and changed by AIS PATCH as the
viewer's `update_inventory_item`/`update_inventory_category` do.
- `meta {id}`: thumbnail id and favourite flag.
- `picture {id, size=256}`: what an item looks like, as the viewer's gallery shows it: its thumbnail, else a texture's
  (or snapshot's) own image; a PNG (base64) decoded by the renderer's OpenJPEG. The assistant's `item` tool turns it
  into an image (through `client_api` it would come back as text).
- `thumbnail {id, texture_id | item_id | path | view: true | remove: true}`: the viewer's Change Thumbnail. A square
  texture of 64-256 px is set as it is (an item's needs copy and transfer, as the viewer asks); anything else is
  cropped to its middle square, scaled to at most 256, J2C-encoded and uploaded through `InventoryThumbnailUpload`
  (LibreMetaverse's `UploadThumbnailAsync`), which sets it. `view` takes a picture of the view (the snapshot provider)
  for it, as the viewer's floater does. The viewer refuses a texture that isn't square; this crops it and says so.
- `favorites {refresh?}` (the viewer's Favorites tab): the whole tree read from the service once a session (a
  subtree at a time; one the service finds too big, HTTP 403, split by its folders, four at a time), then kept by our
  own changes; with each favourite's path and thumbnail. `favorite {id, on}`.
- OpenSim (our grid) has no InventoryAPIv3 or InventoryThumbnailUpload: those ops say so; textures' own pictures work.

## Moving and tidying (the user, 2026-10-31)

Plain moves between folders don't ask: a move is easy to undo, and gating each one would mean a press per item while
the assistant tidies. Moves into Trash (and deleting) wait for the person's press, as deleting (`docs/assistant.md` >
Confirmations). While tidying, the assistant gathers everything bound for Trash into one `TesselInventory move {ids,
folder_id}`, so the person answers one question for the lot (the user preferred batching to a switch that lets Trash
moves through unasked). The confirmation names six and "N more".

## Protected folders (InventoryOps)

Firestorm's "Protect folder" (`fsfloaterprotectedfolders.cpp`, per-account `FSProtectedFolders`; checked in
`get_is_category_removable`, `..._renameable`, `isItemMovable`): a protected folder can't be deleted, moved or renamed
(its contents can), until unprotected. Kept per account in `inventory.json`. `protected`, `protect {folder_id}`,
`unprotect {folder_id}`. And Firestorm's `LockAOFolders` (on by default): the AO's folder and everything in it can't
be deleted, moved or renamed by the inventory ops; the AO's own ops change it (`ao_lock_folders` turns the lock off).
`InventoryOps.Locked` answers for system folders too (they were already refused). The confirmation gate asks
`Refusal` first, so the person is never asked to confirm a deletion that would then be refused.

## The AO (AoEngine, AoFolders, AoDriver)

Firestorm's AO (`aoengine.cpp`, `aoset.cpp`, master of 2026-10, ~4.5k lines read) ported, not reinvented: the same
folder format, so a person's existing `#Firestorm/#AO` plays here as it does in Firestorm, and anything changed here
reads back in Firestorm.

The format (`AoFolders`): the AO folder holds a folder per set, `name[:SO][:SM][:DM][:**]` (sit override, smart sit,
no stands in mouselook, the default set); a set holds a folder per state by Firestorm's or ZHAO's name (Standing or
Stand.1, Walking or Walk.N, Sitting On Ground or Sit.G...: 26 states, swimming and Always included),
`State[:CT<seconds>][:CY][:RN]` (cycle time, cycle, random); a state holds animations (links, usually), each one's
order as its description; a subfolder `name:OR<n>` is a group played together as one step, `name:TR[:CT..][:CY][:RN]`
a track of animations played alongside the state's own. Unknown options and states are warned about (`status`'s
`warnings`), as Firestorm logs them.

How it plays (`AoEngine`, Firestorm's `override()` and its friends, line for line where it matters): Firestorm hooks
`LLVOAvatar::startMotion/stopMotion` for self; here `SelfAnimations.Wire` hands every AvatarAnimation for my avatar to
`AoDriver.OnSelfAnimations`, which diffs it (a new id or a new sequence number is a start, as
`processAnimationStateChanges` restarts) and asks the engine for each default motion; what the engine starts and stops
goes out as one ordered AgentAnimation. So everyone sees the AO, as with Firestorm's. In detail: the state's step
starts in place of the motion (its other members and its tracks beside it); a cycling state moves to its next (or a
random) step each time it starts and every cycle time; transient motions (ground sit, pre-jump, stand up, landings)
play beside the Linden motion, which the viewer needs; typing plays beside whatever plays; stray animations of other
states are cleaned up on every change; turning on stops the Linden motion at the region (its echo ignored once) and
turning off starts it again; swimming replaces flying under the region's water; Always plays throughout, started again
when the region loses it (at most every 3 s per animation); a region change asks for the motion again.

What gives way, as the viewers do:
- Seats keep their own sits unless the set overrides sits (`:SO`, off by default); with smart sit (`:SM`), for a
  second after sitting the AO checks whether the seat plays its own animation on you (an unworn prim of the seat's
  linkset) and gives way if it does. A ground sit while on a prim (scripted seats that use it) is the seat's.
- Objects that animate you otherwise (dance machines) play over the AO by their priority, as in Firestorm; the AO
  isn't stopped for them. (A possible departure, not built: pausing stands while `SelfAnimations.AnimatedByThings`.)
- The headset's tracked hands: the AO's animations are mine (their source is my avatar), so under `tracking` auto
  they never count as things animating me and the tracked hands win over them; `off` lets them move the arms.
- The pose stand pauses it.
- Seat leftovers (`SeatLeftovers.cs`) never touch the AO's animations (they aren't the seat's); "Stop animating me"
  stops them with everything else, and the AO overrides again on the region's next stand.

Which folder (`folder`; per account): `firestorm`, the default, is `#Firestorm/#AO` when the person has one (read
from the root's `#Firestorm`); else nothing plays until the person picks one or asks for a set. Sojourn makes no
folder by itself: `newSet` makes `#Firestorm/#AO` only when there's no AO folder and the person asks for a set (the
user's open decision, below, can change that). A picked folder may be a folder of sets, one set (its subfolders named
for states), or a scripted AO's folder with a ZHAO-II notecard and its animations (`[ Standing ]a|b,c`: `|` between
steps, `,` within a group, `#` comments), read in place with nothing copied (Firestorm only imports those into its own
folders); a notecard set's flags are kept by Sojourn in `inventory.json`.

Ops: `status`, `on {on}` (Firestorm's UseAO, off by default), `stands {on}` (UseAOStands, on), `folder {folder}`,
`reload`, `set {name?}` (states and steps), `select {name}`, `next`, `previous`, `play {step | index}`, `setFlags
{name?, default?, sit_override?, smart_sit?, no_mouselook_stands?}` and `stateFlags {set?, state, cycle?, random?,
cycle_time?}` (both written into the folders' names, as Firestorm saves them; renamed at once rather than on
Firestorm's 5 s settings timer), `add {set?, state, item_id | item_ids}` (links; the state's folder made if missing),
`remove {set?, state, step | index}` and `removeSet {name}` (confirmed: links deleted, a set's folder to Trash; the
animations themselves stay), `newSet {name}`, `renameSet {name, to}`, `collar {opencollar?, lockmeister?,
lock_folders?}`, `relayScript`. `TesselAgent animations` names the AO's as started by "my AO (SojournAo)".

OpenCollar and LockMeister: in Firestorm they reach the AO through its worn LSL bridge (the bridge listens on the
collar's channels and owner-says `<clientAO state=on|off|standon|standoff>`; `fslslbridge.cpp`,
`EBEDD1D2-...lsltxt`). Sojourn obeys the same line from any of the person's attachments (not from rezzed objects),
with `source=oc|lm` from its own relay checked against the `ao_opencollar`/`ao_lockmeister` switches (on by default,
as Firestorm's BridgeIntegrationOC/LM), and keeps it out of chat: on unpauses (and turns stands on), off pauses,
standon/standoff the stands; nothing while the AO is off, as in Firestorm. The relay (`relayScript`, the bridge's
OC/LM half, pairing with the collar as it does) must be worn: whether Sojourn offers or makes one is the user's call,
like movelock's push resistance. Without it, collars can't reach the AO (non-zero channels never reach a viewer).

Not ported: Firestorm's AO floater as such (the assistant is the interface; the menu's you > AO has the AO, its set
by a stepper, stands, the collars' switches and the folder's lock), reordering steps by hand (`add` appends; the order
is the links' descriptions, editable with `TesselInventory update desc`), editing groups and tracks (read and played, not made), the ZHAO-II import that copies
a notecard set into `#Firestorm/#AO` (played in place instead), Firestorm's moving of foreign items out of `#AO` to
Lost and Found (left where they are, ignored), remembering each state's position across logins
(`FSCurrentAOState`; the current set is remembered), and mouselook: `AoDriver.Mouselook` is the hook for the desktop's
first person, unwired (the headset's view never follows the avatar's animations, so `:DM` has nothing to do there).

## The pose stand (PoseStand)

Firestorm's (`fsfloaterposestand.cpp`, `fspose.cpp`, `app_settings/posestand.xml`): `poseStand {pose? | item_id?,
lock?}` pauses the AO, stops what plays (`LLAgent::stopCurrentAnimations(true)`: objects keep their permissions, a
seat isn't left), sits on the ground first so movement can't break the pose (Firestorm's lock, on by default; not
when already seated) and plays the pose; called again it swaps the pose; `poseStandOff` stops it, stands up if it sat,
and brings the AO back. `poses` lists Firestorm's eleven (T pose, arms down legs together, ...: Second Life asset
ids; other grids lack them) and the last one used; any animation of the person's by name or item works too. The pose
counts as the agent API animating me, so in the headset the arms follow it under `tracking` auto (seen in a mirror).

## Defaults the user may want to change

- The AO off until turned on (Firestorm's default), though a person with a filled `#Firestorm/#AO` probably used it.
  On by default would fight a worn scripted AO HUD.
- No folder of Sojourn's own; `#Firestorm/#AO` read when present, made only by `newSet` (board: the user's decision).
- Collar switches on; the AO folder locked; the pose stand locks by sitting on the ground.
- Thumbnails that aren't square are cropped rather than refused.
- Sits not overridden unless the set says so; objects other than seats aren't yielded to beyond their priority.

## The assistant's side

Its `ao` tool (`assistant/moretools.py`) covers status, on and off, stands, sets, next and previous, play, and the pose
stand; editing sets and picking another folder go through `client_api`. Its `item` tool covers pictures, favourites
and thumbnails (`from: view` for "use what I'm looking at"); protected folders are `client_api`. In blind incognito the
AO's and the pose stand's ops are allowed (the person's own avatar, as outfits are; the folder is picked only outside a
spell); pictures of inventory aren't (`Session/Private/Allowlist.cs`).

## Testing

- `dotnet run --project tools/aocheck -p:SojournOut=<build dir>`: 52 checks of the format, ZHAO-II and the engine
  (start, cycle timers, next/previous, walking, typing, sits with and without override, smart sit giving way, ground
  sits on prims, stands off, swimming, landings, Always reasserted, region change, off, selecting a set). No grid.
- `python tools/ao.py [--dll DIR]` (the local grid; Sojourn Dancer on :9881 in Atelier, base `local/ao/a`): the whole
  path from no folder through `newSet`, links, standing, cycling, walking, sitting with and without override, stands
  off, the locks and protection, the pose stand, a worn relay's off and on, a ZHAO-II folder in place, a removal
  pressed and one cancelled, a texture's picture, and what OpenSim's lack of AIS3 answers.

## Checks only Second Life can do

- Thumbnails and favourites against the real inventory service: `meta`, `favorites` on a large inventory (the 403
  split), `favorite`, `thumbnail` by texture, by file and by view (and that the viewer shows them).
- A real `#Firestorm/#AO` made by Firestorm (its sets, groups, tracks) playing here as there, on SL's server-side
  default animations; a seat with its own animation under smart sit; swimming in Linden water.
- The pose stand's Firestorm poses (SL assets) and the lock.
- A worn relay with a real OpenCollar (its `OpenCollar?` pairing and ZHAO_AOOFF) and a LockMeister item.
