# What Sojourn lacks next to the official viewer and Firestorm

Written by Claude Opus 5.5, 2026-10-25; re-audited 2026-10-27, after the user's direction that Sojourn is released as
a full VR viewer, against both viewers' source (the official viewer's develop, Firestorm's master: every menu, context
menu, floater and preferences panel) and their release notes since 2023. Agent-facing. This is the release checklist
(the release waits for most gaps, not every last one: CLAUDE.md > Decisions), and it's consulted during headset
sittings. Status checked 2026-10-31.

## How to read it

Sojourn's person doesn't operate viewer UI: the assistant acts through the agent API, and the screen shows information
(name tags, the feed, the panel). So a missing floater isn't a gap if an op covers it. Each gap says where it's missing:
- **render**: the world looks or sounds wrong (or silent).
- **op**: the assistant has no way to do it.
- **info**: the person isn't shown something they'd need.
- **direct**: the assistant can do it, but a person in the headset would still want their own control (pressing,
  dragging, glancing) rather than asking.

**Matters**: H / M / L, for a person in the headset with an assistant. **Size** to close, in model work: S (a day or
less), M (a few days), L (a week or more).

Each area lists what's **open** first (a table; remainders of done rows included), then what's **done**, one line each
with the batch that did it and the doc that has the detail, then what existing ops already **cover**. What only Second
Life can show (the local grid is OpenSim) is in each feature doc's "Checks only Second Life can do" and on the board.
The batches are under Closing the rest; the user's open calls under Waiting on the user's decision.

## Open at a glance (2026-10-31)

- **For the release**: Sojourn's own package (launcher and VR client, uninstall, updates with consent), crash reports,
  the TPV policy's remaining distributor items (an About sheet, `ViewerStats`, the download page's disclosures, a
  privacy policy), UI language. Releasing it, below.
- **Waiting on the user** (Waiting on the user's decision): RLV, buying L$ and the Marketplace, outward-facing posts and
  cloud translation, direct controls (B9: building by hand, an inventory board, appearance sliders, the poser), the
  release formalities, and some smaller calls.
- **Not started**: the long tail (environment editing, look settings, local textures and
  meshes, the navmesh view, the remaining preferences).
- **Waiting on Linden Lab**: Game Control, IM history from LL's server, glTF scenes.
- **Small remainders** of built batches: the "open" tables below, and each feature doc's "Not done".

The first audit's nine high gaps (world audio, media, HUDs, seated controls for vehicles, crowds, animesh, hover text,
search, report abuse with a way out) were all closed 2026-10-25/26; they're among the done lines below.

## Rendering and simulation

| Open | Where | Matters | Size | Notes |
|---|---|---|---|---|
| Water: finer ripples; shallow water ~15 levels brighter than the viewer's | render | L | S-M | the shaders read as faithful ports; wants side-by-sides against the reference viewer (Multitude's `docs/render.md` "Still differs"; `docs/rendering.md` > Water) |
| Environment editing: making and saving sky, water and day-cycle settings (EEP items), applying one to my parcel or region; Personal Lighting's other sliders (an edit of the sky) | op | L-M: land owners and photographers | M: the settings asset's LLSD, `ExtEnvironment` PUT, inventory create (settings type) | long tail |
| Personal environment, the rest: Firestorm's transition time; keeping an applied item between sessions | render | L | S | the `sky` preset is already kept |
| PBR terrain: the estate's per-slot overrides (tiling, rotation, tint) | render | L | S | `World/Terrain.cs` PbrSlot takes each material's own transform only |
| Look settings the viewers offer: screen-space reflections, exposure, tone-mapping choice (Khronos Neutral / ACES, mix), glow strength, max lights, max particles, terrain detail, separate avatar/tree/terrain LOD factors; depth of field for desktop pictures only | render, op | L: SSR and DoF are usually off, and DoF is wrong in a headset; exposure and particle count matter in busy places | S each, behind settings | long tail. In the port: tone map, CAS sharpening, SMAA, SSAO; one `detail` factor; the launcher's graphics presets |
| glTF scenes as assets (`gltfscenemanager.cpp`) | render | L until LL ships it | M | still experimental in the viewer |
| Beacons, the rest: the 2D arrow at the screen's edge toward a tracked target, the cheesy beacon's pulse, the beacon legend, picking through transparent faces while highlighted, Firestorm's region-corner beacons and chat range spheres | render, info | L | S each | `docs/rendering.md` > Beacons and highlights |
| Flexi and avatar physics, the rest: flexies in probe captures and on HUDs stay straight, and touches hit the straight shape; the own body's copy in a mirror doesn't bounce | render | L | S each | |
| Sending point-at and edit beams while the assistant builds or selects (receiving is done) | info for others | L | S: ViewerEffect PointAt/Beam | the user's call: they move your arm in others' viewers (declined for now 2026-10-26); Firestorm makes sending optional |
| Crowds, unmeasured: the impostors' GPU cost (the first numbers were spoiled by another session's VRAM use), and whether near impostors read as solid in the headset | render | L | S | `docs/performance.md` > Crowds |

Done:
- **Object sounds** (2026-10-25): placed around you by the viewer's rules (cap 30, priorities, cutoff, parcel
  sound-local, queue, sync), our own spherical-head spatialiser in the voice shim's mix; `docs/audio.md`.
- **Parcel audio stream** (2026-10-25): `Audio/MusicStream.cs` (ICY and HTTP, MP3 and AAC, Ogg, playlists, the title);
  `docs/audio.md`.
- **Media on a prim and parcel media** (2026-10-26): CEF and libVLC helpers, the viewer's rules for what plays and
  whose, mine-only auto-play by default, sound in the mix, `SojournMedia`; `docs/media.md`.
- **Avatar impostors and a complexity limit** (2026-10-26): the nearest `full_avatars` (16) whole, the rest impostors
  that keep their depth in each eye; the viewer's ARC from mesh headers, over `avatar_complexity` (350k) a jelly doll
  (its worn particles not drawn since B3b); `SojournAvatars` render always/never per person; `docs/performance.md` >
  Crowds. Under lag, more impostors come only from the load governor's distance trim, not a second trigger from the
  scene budget (the user's call, 2026-10-27).
- **Animesh** (2026-10-26; the 64 m scale fixup in B3b): `docs/rendering.md` > Animated objects.
- **HUD attachments drawn** (2026-10-25): see Avatars > HUDs.
- **Hover text** (2026-10-25): `Social/FloatingText.cs`; `docs/social.md`.
- **Parcel environments** (2026-10-25): `RegionEnvironment` asks `?parcelid=` on a parcel change and each refresh, as
  the viewer does.
- **llTargetOmega spin and prim motion extrapolation** (2026-10-27): velocity, acceleration and spin as the viewer's
  idleUpdate, the seated view riding the drawn seat; `docs/vr.md` > The rig (Moving things).
- **Flexi prims** (2026-10-27, `flexi` on): `docs/rendering.md` > Flexible prims.
- **Avatar physics** (2026-10-27, `avatar_physics` on): `docs/rendering.md` > Avatar physics.
- **Look-at both ways** (2026-10-26): ours sent (`gaze`, eye contact by default), others' heads turning;
  `docs/vr.md` > Where you look, seen by others.
- **Point-at and edit beams received** (B3b): beams, edit beams and spirals (`selection_beams`, on), a PointAt's
  left-arm IK; `docs/rendering.md` > Avatars: lip sync, others' effects.
- **Lip sync** (B3b, `lip_sync` on): system heads only, as the viewer.
- **Reset skeleton, refresh attachments, resync animations** (B3b): `SojournAvatars resetSkeleton` (yours sent as
  ViewerEffect 19, others' resets obeyed), `refreshAttachments`, `resyncAnimations`. Not done: Firestorm's Undeform
  (L, S).
- **Others' hover height** (applied) and **standing foot IK** (B3b).
- **Mirrors** (2026-10-26): a reflection per eye, you whole in dynamic ones; `docs/rendering.md` > Mirrors.
- **Water**: the pre-water alpha pool (2026-10-27); exclusion surfaces, the camera under water, the sun's shadow on the
  glints (B3a); `docs/rendering.md` > Water.
- **Personal environment** (B3a; the time of day 2026-10-29): `SojournEnvironment use/mine/shared/clouds/time`, the
  presets and inventory or Library EEP items, cross-fades as LLEnvironment; `docs/rendering.md` > The environment
  over time.
- **Ban lines and property lines** (B3a): `ban_lines`, `property_lines`; `docs/rendering.md` > Property lines and ban
  lines. The owners' colours on the mini-map: Social > the map.
- **Beacons and highlights** (B8): every kind, Highlight Transparent, the tracking beacon; `docs/rendering.md` >
  Beacons and highlights.
- **The interest list's 360 mode** (B3a): `interest_360`, off by default (the user's call after trying it on SL,
  2026-10-30); `docs/rendering.md` > The interest list's 360 mode.
- **Region and viewer statistics** (B8): `SojournMap stats`, the lag meter on the location line; `docs/map.md`. Not
  done: the full Statistics floater; packet loss (LibreMetaverse keeps none).

At parity or close (against Multitude's side-by-sides and Sojourn's code): PBR materials with glTF overrides, legacy
materials and alpha modes, bake-on-mesh, rigged mesh, sculpts, mesh and volume LODs by the viewer's rules (LOD-range
fetches still to do), projectors with shadows, reflection probes, region EEP with altitude tracks and legacy
WindLight, PBR terrain, particles, texture animation, glow, Linden trees and grass, water. LL's content changes since
2023 are all in (checked 2026-10-27): glTF PBR materials, mirrors, PBR terrain, 2K textures, glTF meshes, WebRTC voice
(the only voice since 2026-05-05), SMAA, CAS sharpening, the Khronos Neutral tone map; water exclusion too.

## Avatars

| Open | Where | Matters | Size | Notes |
|---|---|---|---|---|
| Appearance editing by the person: the shape and outfit editors' sliders, with a mirror | direct | M: editing a shape is visual; by voice ("a bit taller") it works but converges slowly | M: a slider board like the HUD board | B9, the user's call. `editWearable` / `wearableParams` cover every param by op |
| The poser (Firestorm's `fsfloaterposer`: bones posed by hand, saved as animations) | op, direct | L-M: photographers | M: a joint editor over our animator | B9 |
| The AO floater's hand reordering and group/track editing | op, direct | L | S | `docs/inventory.md` > The AO |
| Firestorm's target markers and zoom for combat | info | L | S | |

Done:
- **HUDs** (2026-10-25): drawn as the viewer draws them on the desktop; in the headset a board called up from the
  wrist, touched by either hand, pictured and pressed by the assistant (`SojournHud`, the `hud` tool); hover text and
  media on HUDs since 2026-10-26; `docs/hud.md`.
- **Touch with surface coordinates, and touch-and-hold** (2026-10-25): face, ST, UV, position, normal, binormal
  (`RegionScene.Pick`, `Control/DirectTouch.cs`); `requestTouch hold`, and the person's held trigger drags.
- **Seated controls in the headset** (2026-10-25): the sticks are the movement keys while seated or while a script
  holds them, a left-trigger press is the left button (`Control/VrPilot.Seated.cs`); `docs/vr.md` > Controls.
- **Combat and mouselook-only scripts** (B1): `aim`, `aim_with` head or hand, a crosshair; `docs/safety.md` > Aiming.
- **Release keys** (B1): `docs/safety.md` > Release keys.
- **My own hover height** (B2): `hover_height`, kept per account; `docs/people.md` > Hover height.
- **A client AO** (B7): Firestorm's AO ported (`SojournAo`), its folder or a picked one, ZHAO-II notecards read in
  place; OpenCollar/LockMeister switches need a worn relay (the user's call); `docs/inventory.md` > The AO.
- **Pose stand** (B7): `SojournAo poseStand`; `docs/inventory.md` > The pose stand.

Covered by ops: gestures, animations, attachments, outfits, bakes (`LLGesture`, `TesselInventory`, `playAnimation`,
`LLAppearance`, `attach`); sit on the ground, fly, always run, stop animations, the Library's starter avatars and
outfits (`requestSit` with no target, `move`, `stopAnimating`, `library`, `wear`).

## Interaction

| Open | Where | Matters | Size | Notes |
|---|---|---|---|---|
| RLV / RLVa (Firestorm's `rlv*.cpp`: consent-based restrictions scripts apply, shared #RLV folders) | op | L-M: a minority, mostly roleplay | M-L | the user's call (Waiting); the dispatcher's gate is the natural place to enforce it for every caller |
| Game Control: the viewer's gamepad input sent to the region (GameControlData) for scripts' `game_control` events | op (the person's controls) | L until LL ships it, then M-H: VR controllers are gamepads | S-M once the message is final | an alpha viewer since 2026-09-29 (26.4), held for simulator work |
| Media, the rest: the parcel's prevent-camera-zoom (LibreMetaverse drops the block); H.264 in pages | render | L; M for video sites | S; H.264 needs our own CEF build, L | H.264 is the user's call; `docs/media.md` > Not done |
| Landmarks embedded in notecards offered as places | op | L | S | `docs/people.md` > Places |

Done:
- **Experiences** (prompts 2026-10-25; lists, profiles, search, the region's for estate owners in B2): prompts through
  the notifications and the confirmation gate; `SojournPeople experiences` and the rest; `docs/people.md` >
  Experiences. Unchecked on SL (OpenSim has none).
- **llMapDestination and SLurls** (B2; drawn as links on the feed since B4): a press offers the place with a Teleport
  key; `docs/people.md` > Places.
- **Hover tips** (B4): names and prices at the pointer after 0.7 s, on by default; `docs/social.md`.
- **Bumps, pushes and hits** (B1): `SojournSafety bumps`, `bump_notices`; `docs/safety.md` > Bumps, pushes and hits.
- **Object inspection and script info** (B6): `inspect` and `TesselObjects get full` (creator, owner, perms);
  `SojournBuild landImpact`, `scriptInfo` (counts; time and memory from the region's Top Scripts, as Firestorm's need
  its worn bridge); `docs/building.md` > Cost and scripts.
- **Web page offers** (2026-10-26): "Go to page", always confirmed, opens it up close; `docs/media.md`.
- **Media, first click and the rest** (B10): focus-first clicks, auto-zoom as up close, auto-scale, a face's media
  settings by op; `docs/media.md` > What plays, and whose.
- **Pay price buttons** (B2): `docs/people.md` > Pay price buttons.
- **Direct touch and sit** (decided by the user 2026-10-25): the left trigger and a left click in the world touch or
  sit directly, an exception to "the assistant is the interface"; `docs/vr.md` > Controls.

Covered by ops: touch, sit, menus (llDialog), text boxes, permission prompts (`requestTouch`, `requestSit`,
`LLNotifications respond`, ScriptQuestion through the gate).

## Social

| Open | Where | Matters | Size | Notes |
|---|---|---|---|---|
| Profile feed, Flickr, Primfeed, Discord presence | op | L | S-M each | outward-facing: the user's call, each the person's own press. The profile feed is refused for now: it needs the login's openid_token, which LibreMetaverse drops |
| A chat translation service | info | M: SL is international | S once chosen | plumbing built (B4); which service, if any, is the user's call |
| IM history from LL's server (the 26.5 Chat Modernization beta syncs one-to-one IMs with SL Mobile over a new HTTPS service) | info, op | L-M now, M once it's the default viewer | S-M once the service is documented | Sojourn keeps its own transcripts (`AgentState.cs`) |
| The map, the rest: the mini-map's property lines and owners' colours, marks for avatars above and below, a turning mini-map, the world map's search box and landmarks | info | L | S each | `docs/map.md` > Not done |
| Classifieds in profiles; Firestorm's "leave all conference chats" | op | L | S | |

Done:
- **Search for places, events, classifieds** (2026-10-26) and **land for sale** (B5): `SojournSearch`, the search tool;
  the Destination Guide by the web tools; `docs/assistant.md` > Search. The results' parsing is unchecked on SL.
- **Offline IMs at login** (2026-10-26): asked once logged in (ReadOfflineMsgs, else the old packet),
  `AgentSession.Work.cs`. SL's cap path is a check.
- **Friends' rights and removal, contact sets and pseudonyms** (B2; set colours on tags since B4): `docs/people.md` >
  Friends.
- **The map** (B8): world map and mini-map as one board, people, friends, land for sale, events, Go or track;
  `docs/map.md`.
- **Teleport history and favourites** (B2): `docs/people.md` > Places.
- **Display name and profile notes** (B2): `docs/people.md` > Display name and notes. Display names unchecked on SL.
- **Away and Do Not Disturb, typing sent** (B2; DND declines calls since B10): `docs/people.md` > Away and Do Not
  Disturb.
- **Keyword alerts and @mentions, group titles, away marks, colours on tags, chat bubbles, tags hidden behind walls**
  (B4): `docs/social.md`.
- **Reject switches and "only friends can IM me"** (B2; calls in B10): `docs/people.md` > What's turned away.
- **Radar alerts** (B4): Firestorm's chat range, draw distance and region reports and its age alert, all off by
  default; `SojournSocial radar`.
- **Voice, the rest** (B10): conferences, WebRTC moderation, voice mutes on the block list, call rejects, hearing from
  camera or avatar; `docs/voice.md` > Using it.
- **Group chat moderation and snoozing** (B2; voice moderation in B10): `docs/people.md` > A group's chat.

Covered by ops: IMs, conferences, group chat and notices, profiles and picks, teleport offers, requests and lures,
group management (`TesselIM`, `TesselGroups`, `TesselAgent`).

## Inventory

| Open | Where | Matters | Size | Notes |
|---|---|---|---|---|
| Browsing inventory by the person: a visual inventory in the headset (folders, thumbnails, wear or rez by pressing or dragging into the world) | direct | M: looking through what you own is browsing, not asking | M-L: a board like the HUD board | B9, the user's call (a 2D floater by another name) |
| The Marketplace listings folder; Firestorm's favourite wearables | op | L | S-M | |

Done:
- **Thumbnails and protected folders** (B7): read, shown, set and uploaded over AIS3; protected folders refused before
  any confirmation; `docs/inventory.md`. Thumbnails need SL (OpenSim has no AIS3).
- **Favourites** (B7): `SojournInventory favorites`, `favorite`.

Covered by ops: offers, folders, outfits, wearing, giving, the Library, notecards, landmarks, gestures
(`TesselInventory`, `LLInventory`, `LLAppearance`, `SojournAssistant findInventory`).

## Building and scripting

| Open | Where | Matters | Size | Notes |
|---|---|---|---|---|
| Building by the person: select, grab, move, rotate and scale with handles, snap to grid, a tool board | direct | M: fine placement by hand is what a headset is good at | L | B9, the user's call. Their idea (2026-10-30): the assistant finds the thing in inventory and places it roughly, the person fine-tunes position and rotation by hand |
| Script errors for the person (the viewer's script warnings window) | info | L-M | S | errors from the person's objects reach the assistant as LLChat debug events; the feed leaves them out |
| Local textures and meshes (preview before upload) | op | L | M | long tail |
| Backups, the rest: contents in backups, Collada export | op | L | S-M | `docs/building.md` |
| The navmesh's view | info | L | S-M | long tail |
| The glTF scene editor | op | L | M | waits on LL shipping it |

Done (B6, `docs/building.md`): **PBR materials** applied, overridden and taken off, read back; **normal and specular
maps, alpha mode, projector parameters, object group and deeding** (media faces in B10); **SLua's compile targets**
(`mono`, `lsl2`, `luau`, `lsl-luau` where the region allows Lua) and **bulk script chores** on linksets (recompile,
reset, run, remove, save back, take separate, bulk and default permissions); **undo and redo** for every edit through
the API; **pathfinding** (unchecked: OpenSim has none); **material upload**; **export and backup** under Firestorm's
own-creation rules.

Covered by ops: create, modify, link, take, rez, contents, script create/read/write, uploads of textures, sounds,
animations and meshes (`TesselBuild`, `TesselInventory`, `TesselAgent upload*`).

## Land and region

| Open | Where | Matters | Size | Notes |
|---|---|---|---|---|
| Telehubs, region terrain textures and heights, terrain upload and download, the estate's own settings (LibreMetaverse misreads the sun hour it would send back), claiming public land, the BuyPass notice | op | L | S each | `docs/land.md` > Not done |

Done (B5 unless said, `docs/land.md`): **access and ban lists**; **where you are** (B8: the location line at the wrist,
the viewer's icons, the rating, health; `docs/map.md`); **region and estate info, covenant, restart schedule**; **buy,
sell, abandon, deed, divide, join, terraform**, each gated; **estate tools** (lists, kick, send home, restart, top
scripts and colliders, return, bake terrain); **land holdings, contributions, buying a pass**.

Covered by ops: About Land and its settings (`parcelInfo`, `parcelSet`, `parcelObjects`, `parcelReturn`,
`parcelEject`); restart warnings on the feed, set home, teleport home.

## Money and marketplace

| Open | Where | Matters | Size | Notes |
|---|---|---|---|---|
| Marketplace browsing and buying, buying L$, transaction history (all web in the viewers too) | op | L-M: shopping by voice in VR is appealing, but it's web and money | M: a web tool with spending confirmations | the user's decision (Waiting) |

Done: **money notices and the money tracker** (B2): `money_notices`, `SojournPeople money`; every pay already waits
for the person's press; `docs/people.md` > Money. Unchecked here (the local grid moves no L$).

Covered: balance, pay, buy objects, behind the confirmation gate.

## Snapshots and preferences

| Open | Where | Matters | Size | Notes |
|---|---|---|---|---|
| Preferences the viewers have and Sojourn doesn't: UI language (Releasing it), time format, name display (display names, usernames, legacy names), chat font size, cache size and clearing it, media auto-play finer than `media_autoplay` (others' attachments, HUD media, Firestorm's media filter of allowed and blocked sites), parcel music fading, sounds from collisions and gestures | op | L-M each; together what a release's settings are expected to have | S each | long tail; on the API for the assistant rather than menu rows (the simple menus). Since done: the away timeout (`away_after`), the typing animation, group titles on tags |
| Snapshots, the rest: the viewer's image filters and frozen-frame preview; the profile feed (Social) | op | L | S each | `docs/assistant.md` > Pictures and videos for the person |

Done:
- **Pictures for the person** (2026-10-26): `SojournCapture` snapshot and record, the capture tool.
- **Snapshots, the rest** (B8; the controller's shutter in M): sizes up to 7680x4320, placed cameras, 360 panoramas,
  to inventory, postcards; a camera at the left forearm with the right trigger as its shutter (Ctrl+` on the
  desktop); `docs/assistant.md` > Pictures and videos for the person, and > The panel > Pictures by hand.
- **Maturity preference** (2026-10-25): `maturity`, the account's own.
- **Volumes and per-object mutes** (2026-10-25; media volume 2026-10-26).

Covered: every setting the person has is on `SojournSettings`.

## Safety

| Open | Where | Matters | Size | Notes |
|---|---|---|---|---|
| Derendering, the rest: hidden people kept (`hideAvatars` covers them, not kept), Firestorm's auto-hide of objects with huge texture area | op | L-M | S | `docs/safety.md` > Not done |
| A push resisted, as llMoveToTarget would | op | L | S once decided | needs a worn script: the user's call |

Done:
- **Report abuse** (2026-10-26): drafted in the person's words with a picture of their view, sent only at their press
  (`SojournReport`); `docs/assistant.md` > Reporting abuse. SL's caps and the picture upload unchecked (a test
  reaches LL).
- **A way out that doesn't wait on the assistant** (2026-10-26, first used in the headset 2026-10-28): the left menu button
  held or Ctrl+Shift+H stands you up and goes home; `docs/safety.md` > The way out.
- **Derender objects and particles, the asset blacklist, block particle owner** (B1): `SojournSafety derender`;
  `docs/safety.md` > Derendering and the asset blacklist.
- **Antispam** (B1): Firestorm's rules, off by default as in Firestorm (the user, 2026-10-27); `docs/safety.md` >
  Antispam.
- **Comfort when something else moves you** (B1): `comfort_vignette` and `keep_in_place`, both off by default;
  `docs/safety.md`.

Covered by ops: block (people, objects, by name), voice mutes, hiding people, ejecting and banning from your parcel.

## Releasing it

Not viewer features as such, but what every released viewer has. Policy detail in `docs/tpv-policy.md` > If it's ever
distributed.

| Open | Where | Matters | Size | Notes |
|---|---|---|---|---|
| Sojourn's package: the launcher and the VR client in one self-contained folder, a Start menu shortcut, a clean uninstall; updates only with consent | | **H** for a release | M | `docs/launcher.md` > A package with the VR client; Access's package (`tools/access.py package`) is the model. Batch R's notes below |
| The TPV policy's distributor items, the rest: an About sheet in the VR client, the viewer statistics report (2.h, `ViewerStats`), the download page's disclosures (the disclaimer, support, the AI operating the client), a privacy policy | info | **H** for a release (policy) | S-M | the wording and the privacy policy are the user's (Waiting) |
| Crash reports and logs a person can send (opt-in), "report a problem" | | M | S-M | today the launcher says "stopped unexpectedly" and offers the log |
| UI language: the panel, menus, feed and the assistant's replies in the person's language (the viewers ship 12) | info | M | S-M: the assistant already answers in the language it's spoken to; the client's own words are few | |
| The desktop's own interface, quite different from the headset's | direct | M | M | to be workshopped once VR is good; the first release is VR-first and doesn't wait on it (CLAUDE.md > Decisions) |

Done:
- **A login the person does** (R, 2026-10-28): the login screen in the window and the headset, saved accounts, grids,
  start location, the ToS, MFA and failures on a sheet answered only by real input; `docs/client.md` > Logging in.
  Passwords as the viewers keep them, under DPAPI in our own folder rather than the OS's credential store; MFA kept
  only with "remember this computer". Since 2026-10-29 the launcher (`Sojourn.exe`) gathers the login and answers the
  grid's terms and codes at the desk: `docs/launcher.md`.
- **`SojournRelease about`** (R): the name, the channel and version sent at login, the disclaimer, the licence note.
- **LGPL source published**: the public snapshot since 2026-10-29 (`docs/publishing.md`).
- **Sojourn Access's package** (2026-10-29): `docs/access-app.md` > Packaging.
- **Simpler menus** (M, 2026-10-29): simple by default, the `advanced_menu` switch shows everything, a tip on every
  row (`Control/MenuGuide.cs`).
- **The assistant for people who aren't us**, mostly settled: the person's own provider (a Claude account, an
  Anthropic key or OpenRouter, 2026-10-29), no Claude-account sign-in in released builds (the user, 2026-10-30), and
  the studio relay for people the studio covers; `docs/assistant.md` > Switching it on. What's still open: Waiting.

## Closing the rest

Written by Claude Opus 5.5, 2026-10-27: the open rows grouped into batches separate agents could build at once without
editing the same files, ordered by what a release needs first (wave 1: what a release can't go without; wave 2: what
people meet every session; wave 3: builders, land owners, the long tail). Most were built 2026-10-27..29.

### How the batches share the code

For any further batch:
- **New ops go in new files**: a new `LeapApi` class per area (e.g. `SojournPeople`, `SojournLand`) over its own
  `Agents/*Ops.cs`, registered with one line beside the others. `TesselAgent.cs` and `TesselApis.cs` get fixes, not
  new ops. Wiring that draws nothing goes in Sojourn.Session, so Sojourn Access gets it too.
- **Settings** are registered from the batch's own wiring file with `api.Add(...)` (as `MediaWiring.cs`,
  `LandWiring.cs`). `SettingsApi.cs`, `SettingsMenu.cs` and `VrSettings.cs` belong to the menus pass (M).
- **Confirmations**: an op that spends, gives, deletes or changes permissions adds its case from its own wiring
  through `Confirmations.Cases`, and refusals that come before asking through `Confirmations.Refusals`, so
  `Confirmations.cs` itself rarely needs a hunk.
- **The assistant**: a new op reaches it at once through `client_api` and `api_help`. Dedicated tools
  (`assistant/tools.py`, `moretools.py`), the prompt's lines and incognito's allowlist (`Private/Allowlist.cs`: an op
  not on it is refused while incognito is blind, the safe default) belong to the assistant pass (T).
- **The handoff**: each batch writes its feature doc (what's built, settings, testing, "Not done", "Checks only Second
  Life can do", and "For batch T and M": the menu rows, bindings, tools and incognito classes it needs), and marks its
  rows here.
- Each batch works on its own branch or worktree and merges main in before handing back (never junction `local/` into
  a worktree: CLAUDE.md). The local grid and the GPU are shared: one GPU-heavy agent at a time, clients on the grid
  coordinated by hand.

### The batches (status 2026-10-31)

- **R. Release** (wave 1, L): **partly built**. Done: the login screen (2026-10-28), `SojournRelease about`, the
  launcher (2026-10-29), Access's package. Left: Sojourn's package and updater, crash reports, `ViewerStats`, the
  About sheet, the privacy policy and disclosures (the last two the user's). Notes for the rest, below.
- **B1. Safety and comfort** (wave 1): built 2026-10-27. `docs/safety.md`.
- **B2. People and me** (wave 1): built 2026-10-27/28. `docs/people.md`.
- **B3a. World rendering** (wave 1): built 2026-10-28; left: water's ripples and shallows. `docs/rendering.md`.
- **B3b. Avatars** (wave 2): built 2026-10-29; receiving only (sending PointAt and beams is the user's call).
  `docs/rendering.md` > Avatars: lip sync, others' effects, standing feet, fixes.
- **B4. The social layer** (wave 2): built 2026-10-28, translation without a service. `docs/social.md`.
- **B5. Land and region** (wave 2): built 2026-10-28. `docs/land.md`.
- **B7. Inventory and the avatar's own** (wave 2): built 2026-10-29. `docs/inventory.md`.
- **B10. Voice and media, the rest** (wave 2): built 2026-10-29. `docs/voice.md`, `docs/media.md`.
- **B6. Building** (wave 3): built 2026-10-29; left: Collada export, contents in backups, the navmesh's view.
  `docs/building.md`.
- **B8. Information for the person** (wave 3): built 2026-10-29 (the map board, the location line, statistics,
  beacons, the rest of snapshots). `docs/map.md`, `docs/rendering.md` > Beacons and highlights, `docs/assistant.md` >
  Pictures and videos for the person.
- **The long tail** (wave 3, each S-M, any order): **not started**. Environment editing and saving EEP items; the
  remaining look settings (exposure, tone-mapping choice, particle cap, separate LOD factors, SSR; depth of field for
  desktop pictures only); local textures and meshes; the navmesh view; terrain paint maps; the remaining preferences;
  UI language; and, once LL ships them, glTF scenes, Game Control from the controllers, IM history from LL's service.
- **A. Usable without sight**: became Sojourn Access, a separate app (the user, 2026-10-27); milestones M0-M5 built
  2026-10-29. `docs/access.md`, `docs/access-app.md`.
- **T. The assistant** (after each wave, S-M): done 2026-10-29 for waves 1-3 (tools by area, the prompt's lines,
  incognito's classification of every new op, end-to-end checks with `tools/assistant.py`). `docs/assistant.md`. A
  further batch needs its own T pass.
- **M. Menus and settings** (after each wave, M; the look is the user's call): done 2026-10-29 (every row the batches
  asked for, groups with pages of their own, then the simple menus). `docs/assistant-panel.md`. A further batch
  needs its own M pass; the missing preferences go on the API for the assistant rather than as rows.
- **B9. Direct controls** (L each): **not started, the user's call**. Building by hand (handles, snapping, a tool
  board), an inventory board, an appearance board of sliders beside the mirror, the poser: each a viewer floater's job
  done the headset's way. Whether, and which, is a direction question, since Sojourn's premise is that the assistant is
  the interface.

### R: notes for the rest, from the viewers' source (2026-10-27)

- **ViewerStats**: an LLSD POST to the region's `ViewerStats` cap once a region is up, then every 300 s and at quit
  (`llviewerstats.cpp` `send_viewer_stats`): `agent`, `system` (OS, CPU, RAM, GPU, and also `mac_address` and
  `serial_number`), `downloads`, `stats` (net, failures, misc); no other residents' data. Firestorm drops `ui`.
  Leaving out the MAC address and serial is the user's call.
- **Updates**: LL's updater (Velopack, asking `update.secondlife.com/update/v1.2/{channel}/{version}/win64/...`) follows
  `UpdaterServiceSetting` (3 automatic, 1 ask, 0 mandatory only); Firestorm has none. The plan: updates from a signed
  manifest (ECDSA, the key kept in `local/`), installed only at the person's press.
- **Crash reports**: the official viewer sends BugSplat crashes without asking; Firestorm's `CrashSubmitBehavior` is
  ask (default), always or never.
- The disclaimer's wording is the policy's, in neither tree. Both viewers quit on logout and neither relogs (Sojourn
  restarts into the login screen).

### Waiting on the user's decision

The board's "Calls for the viewer gaps" carries these; none blocks other work.
- **RLV/RLVa**: restrictions a script imposes on the person's own client, by their consent. The dispatcher's gate is
  the natural place to enforce them for every caller, the assistant included; but some restrictions (no teleport, no
  standing, no IMs) collide with the way out, which has to stay a sure exit. Whether to build it, and whether the way
  out overrides it.
- **Buying L$ and the Marketplace**: real money and the person's payment details (web pages in the viewers too).
  Whether Sojourn only opens them for the person's own hands, or the assistant may browse and fill a cart up to the
  gate; never card data either way. The land buy floater's website preflight goes with it (`docs/land.md`).
- **The assistant for other people**: the provider is settled (above, Releasing it); what's left is what Sojourn
  offers a person with no provider at all, and what it costs them.
- **Outward-facing posts** (profile feed, Flickr, Primfeed, Discord presence) and **cloud translation**: built only if
  wanted, each the person's own press or setting.
- **Direct controls** (B9): how much of the viewers' hands-on UI belongs in Sojourn; for building, the user's idea of
  rough placement by the assistant and fine-tuning by hand (2026-10-30).
- **The release itself**: its name, the disclosures, a privacy policy, the TPV directory listing. (The source is
  public; for Access's test build the user settled on GitHub releases, unsigned, 2026-10-29.)
- **Smaller calls**: sending PointAt and beams (your arm points at what the assistant builds or selects, as the official
  viewer always does); an AO folder of Sojourn's own in the person's inventory or only one they pick, and the
  OpenCollar/LockMeister relay; leaving the MAC address and serial out of `ViewerStats`; hover tips on by default and
  contact-set colours on the glass tags; H.264 in pages (our own CEF build); push resistance by a worn script.
