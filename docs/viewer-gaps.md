# What Sojourn lacks next to the official viewer and Firestorm

Written by Claude Opus 5.5, 2026-10-25. Agent-facing. An audit against the official viewer (secondlife/viewer,
develop) and Firestorm (FirestormViewer/phoenix-firestorm, master), read from their source trees, and Sojourn's code
and docs as of e681f05.

How to read it. Sojourn's person doesn't operate viewer UI: the assistant acts through the agent API, and the screen
shows information (name tags, the feed, the panel). So a missing floater isn't a gap if an op covers it. Each gap says
where it's missing:
- **render**: the world looks or sounds wrong (or silent).
- **op**: the assistant has no way to do it.
- **info**: the person isn't shown something they'd need.

**Matters**: H / M / L, for a person in the headset with an assistant. **Size** to close, in model work: S (a day or
less), M (a few days), L (a week or more). **Basis**: *verified* means checked in Sojourn's code (file named);
*inferred* means from the docs, the absence of any code for it, or what LibreMetaverse 3.2.0 offers, without a run.

**Re-audited 2026-10-27** (Claude Opus 5.5), after the user's direction that Sojourn is to be released as a full VR
viewer with everything other viewers have, not only the high-value gaps. Checked against the code at a9b1449 plus the
working tree, and against the official viewer's and Firestorm's whole menu, context-menu, floater and preferences
surface (sparse checkouts of develop and master: `menu_*.xml`, `panel_preferences_*.xml`, `llviewerfloaterreg.cpp`)
and their release notes since 2023 (LL's from releasenotes.secondlife.com; Firestorm's through Inara Pey's release
overviews, as its wiki refused fetches, and its long-standing features from the source and memory). Rows added then say *added 2026-10-27*; corrected rows say what changed. A fourth
where-tag: **direct**: the assistant can do it, but a person in the headset would still want their own control
(pressing, dragging, glancing) rather than asking. Rows other agents were building on 2026-10-27 say *in progress*.
The plan for closing everything is the last section, "Closing the rest".

## The high ones, in brief

1. **No world audio at all**: object sounds, parcel music streams, media audio. Only voice and the assistant's
   replies play. SL is silent apart from people talking. **Done 2026-10-25**: object, gesture, collision, footstep,
   typing and UI sounds placed around you, and the parcel's music (docs/audio.md); media audio with media (2026-10-26).
2. **No media**: media on a prim and parcel media (web pages, video, YouTube/watch parties, shop boards) aren't drawn,
   and web page offers can't be opened. **Done 2026-10-26**: pages and video on faces and the parcel's, their sound
   placed, the person's hands on them, an up-close view, offered pages, the assistant's media tool (docs/media.md).
3. **HUDs**: never drawn, so the HUDs people wear (mesh body and head HUDs, AOs, games, RP meters) can be neither
   seen nor worked by the person (touches carry surface coordinates since 2026-10-25, so drawing them is what's left).
   **Done 2026-10-25**: drawn as the viewer draws them on the desktop, on a board called up from the wrist in the
   headset, touched by either hand, pictured and pressed by the assistant (docs/hud.md).
4. **Vehicles and scripts that take controls don't work in the headset**: seated, the sticks send nothing. **Done 2026-10-25**: the sticks are the movement keys while seated or while a script keeps them (docs/vr.md > Controls).
5. **Crowds**: no avatar impostors and no complexity limit (jelly dolls); it's the frame-rate risk in clubs and events,
   and the defence against crasher attachments. **Done 2026-10-26**: impostors that keep their depth in each eye, the
   viewer's complexity limit with jelly dolls, per-person always/never (docs/performance.md > Crowds).
6. **Animesh** (animated objects: pets, NPCs, animated decor, animesh worn as attachments) isn't animated. **Done
   2026-10-26**: a skeleton of its own per animated object, in the world and worn (docs/rendering.md > Animated
   objects).
7. **Hover text** (llSetText: vendor prices, signs, titlers, game status) isn't drawn; only the assistant can read it. **Done 2026-10-25**: drawn as the viewer draws it (docs/social.md).
8. **Search beyond people and groups**: places, events, classifieds and the Destination Guide ("somewhere with live
   music tonight"). **Done 2026-10-26**: places, events and classifieds as the viewers' legacy search asks them, each
   with where it is, the search tool; the Destination Guide through the web (docs/assistant.md > Search).
9. **Report abuse** has no op, and there's no way to escape fast that doesn't wait on the assistant. **Way out built
   2026-10-26** (held left menu button / Ctrl+Shift+H: stand, go home). **Report abuse done 2026-10-26**: drafted in
   the person's words with a picture of their view, sent only at their press (docs/assistant.md > Reporting abuse).

## Rendering and simulation

| Gap | Where | Matters | Size | Basis |
|---|---|---|---|---|
| Object sounds: llPlaySound/llLoopSound/llTriggerSound, attached sounds, gestures' sounds, collision sounds, UI sounds (IM chime, alerts). `Sound/Ears.cs` turns sounds into events for a headless resident; nothing plays them. voice.md lists "world sounds through the same mix" as not done | render | **H**: a silent world; much of SL's mood, feedback (doors, vendors, games) and presence is sound | M: Ogg Vorbis decode, asset fetch, positional mix through voice's engine (it already has the output mix and the echo reference; Steam Audio HRTF suggested), loops, queueing, gain changes, volume settings | **done 2026-10-25**: `Audio/` and the shim's `world.cpp`, the viewer's rules (cap 30, priorities, cutoff, parcel sound-local, queue, sync), our own spherical-head spatialiser rather than Steam Audio; docs/audio.md |
| Parcel audio stream (music_url: Icecast/Shoutcast MP3/AAC/Ogg) | render | **H**: clubs, live music, DJs, events are a large part of SL social life | S-M: an HTTP stream decoder into the same mix; stream title as info | **done 2026-10-25**: `Audio/MusicStream.cs` (ICY and HTTP, MP3 and AAC through Media Foundation, Ogg Vorbis, playlists, the title on SojournAudio and the menu), docs/audio.md |
| Media on a prim (MOAP) and parcel media: web pages and video drawn on faces, autoplay, shared URL per face, media audio | render, op | **H** where it's used (watch parties, TV/web boards, shops, games), absent elsewhere | L: an offscreen browser (CEF, as the viewer's `media_plugins/cef`) and video (the viewer uses libVLC), frames uploaded into face textures, media audio into the mix; then the assistant's ops: navigate, play/pause, a "click there" on the page | **done 2026-10-26**: `Media/` (CEF and libVLC helpers, the viewer's rules for what plays and whose, mine-only auto-play by default, sound in the mix, the SojournMedia ops), docs/media.md |
| Avatar impostors and a complexity limit (the viewer's billboards past ~16 avatars, jelly dolls by ARC, "always/never render fully" per person) | render, op | **H** for crowds: rigged mesh bodies of 100k+ triangles each; also the defence against crasher attachments | M-L (performance.md plan item 6) | verified (no impostor/complexity code; performance.md) **done 2026-10-26**: `World/Avatars.Crowd.cs`, `Frame/Renderer.Impostors.cs`, `impostor.frag`: the nearest `full_avatars` (16) whole, the rest impostors (G-buffer captures in an atlas, relief-marched per eye, a few refreshed a frame, no shadows); the viewer's ARC from mesh headers before anything is built, over `avatar_complexity` (350k) a jelly doll; `SojournAvatars` list/render always|never. Measured on tools/crowd.py's NPCs (docs/performance.md > Crowds). Not done: the impostor count following the scene budget; jelly dolls' attachment particles still play. **2026-10-29** (B3b): jelly dolls' worn particles aren't drawn; the impostor count under lag is the load governor's distance trim (2026-10-27, the user's call), and the scene budget gets no second trigger of its own: it would make more avatars impostors than the user agreed to (performance changes leave the picture as it was) |
| Animesh: control avatars for animated objects, their animations (ObjectAnimation), animesh attachments posed on their own skeleton | render | **H-M**: pets, NPCs, animated decor are common; unanimated they show in the mesh's bind pose or wrong | M: the avatar animator and skinning exist; needs a control avatar per object, the viewer's placement rule (`llcontrolavatar.cpp`), LibreMetaverse's ObjectAnimation event | verified (`RegionScene.cs`: only meshes worn by an avatar are skinned; render.md: "no animesh yet") **done 2026-10-26**: `World/Avatars.Animesh.cs` (placement as `matchVolumeTransform`, ObjectAnimation by object id, joint overrides, worn animesh on its own skeleton), TesselBuild `animated`; checked with `tools/animesh.py`. The 64 m scale fixup **done 2026-10-29** (B3b, docs/rendering.md > Animated objects) |
| HUD attachments drawn (points 31-38, the viewer's HUD camera) | render, info | **H**: see Avatars > HUDs | M (with the HUD design below) | **done 2026-10-25**: the viewer's HUD space, camera and IS_HUD shading (`Realtime/Hud.cs`, `hud.frag`); docs/hud.md |
| Hover text (llSetText) over prims | info | **H-M**: vendor prices, "touch here", titlers, RP and game status; the assistant reads it (`TesselObjects` `text`), the person never sees it | S-M: the name tags' labels atlas and placement fit it (colour, alpha, newlines, distance fade) | **done 2026-10-25**: `Social/FloatingText.cs`, docs/social.md |
| Parcel environments (a parcel's own EEP sky/water, very common at clubs and homes) | render | M-H: the place looks as its owner didn't intend (wrong time of day, wrong fog) | S: `EnvironmentState` already holds a parcel layer; ask `ExtEnvironment?parcelid=` on entering a parcel and when its version changes | **done 2026-10-25**: confirmed (the viewer asks `?parcelid=` for the agent's parcel on entering it and when its version changes); `RegionEnvironment` now does, on parcel change and each refresh |
| llTargetOmega spin (client-side rotation of signs, fans, wheels, displays) | render | M: common, and visibly dead when still | S | verified (angular velocity skipped in `RenderMaterialTracker.cs`; nothing applies it) **done 2026-10-27**: `World/MotionSmoother.Prims.cs` (the viewer's applyAngularVelocity, the spin kept across updates as it keeps it), docs/vr.md > The rig > Moving things |
| Prim motion extrapolation (vehicles, doors, moving builds step at the region's update rate) | render | M: stepping is uncomfortable in a headset, worst when riding | S: the avatar's dead reckoning (`MotionSmoother`) applies to prims | verified (client.md, performance.md) **done 2026-10-27**: velocity, acceleration and spin as the viewer's idleUpdate, corrections blended, the seated view riding the drawn seat; docs/vr.md > The rig > Moving things |
| Flexi prims simulated (flexible paths built straight) | render | L-M: old hair, skirts, tails, flags, plants; most modern content is rigged mesh | S-M: port `llflexibleobject.cpp` (a spring chain with gravity and wind) | verified (`VolumeBuilder.cs` comment). **done 2026-10-27**: `World/FlexiSim.cs` (the spring chain, the viewer's sections and their LOD by size on screen), `World/RegionScene.Flexi.cs` (its update throttling, steps on the thread pool, vertices copied through the skinning pre-pass), worn ones following the wearer; setting `flexi` (on); docs/rendering.md > Flexible prims. Not done: flexies in probe captures and on HUDs stay straight; touches hit the straight shape |
| Avatar physics (breast, belly, butt bounce from physics wearables) | render | L-M | M: port `llphysicsmotion.cpp`; the driven morphs exist (`LlmMesh.cs`) | inferred (morphs present, no motion code). **done 2026-10-27**: `World/Avatars.Physics.cs` (the six motions, collision volumes moved each frame, the system body's morphs uploaded when shown); setting `avatar_physics` (on); docs/rendering.md > Avatar physics. Not done: the own body's whole copy in a mirror stays still |
| Look-at and point-at, both ways: others' heads turning to what they look at (render.md: "no head look-at targets"); and sending ours, so others see your avatar's head follow where you look in the headset | render, info for others | M: the one piece of head motion others can see within the TPV policy (the official viewer sends it); Firestorm also lets you keep it private | S-M: ViewerEffect send and receive (LibreMetaverse has both), a head look-at in the animator; a privacy setting | **look-at done 2026-10-26** (corrected 2026-10-27): sent by `Control/Gaze.cs` (the `gaze` setting, eye contact by default), others' received and turning their heads in `World/Avatars.LookAt.cs`. Point-at isn't: see "Point-at and edit beams" below |
| Point-at and edit beams (*added 2026-10-27*): the particle beam from someone's hand to what they're editing or selecting (LLHUDEffectBeam, PointAt), drawn by every viewer; ours sent while the assistant builds | render, info for others | L: shows who's building what; Firestorm makes sending optional ("Don't send my selection target hints") | S: ViewerEffect PointAt/Beam both ways, a particle line | verified absent (no PointAt in `Gaze.cs` or the renderer). **Receiving done 2026-10-29** (B3b, docs/rendering.md > Avatars: lip sync, others' effects): beams, edit beams and spirals drawn as the viewer's particle sources (`selection_beams`, on), a PointAt's left-arm IK (LLEditingMotion). Sending: not built (the user's call on the board) |
| Mirrors (the viewer's hero probes, `llheroprobemanager.cpp`) | render | L-M: rare content; but a mirror is how people see themselves in VR (phase 4 lists a mirror) | M | **closed 2026-10-26**: a reflection per eye, you whole in dynamic ones (`docs/rendering.md`) |
| Water below the surface ordering (pre-water alpha pool), finer ripples, shallow water brightness | render | L | S-M | **pre-water alpha pool done 2026-10-27** (`docs/rendering.md` > Blended or masked; corrected 2026-10-27). Open: finer ripples, shallow water ~15 levels brighter than the viewer's (Multitude `docs/render.md` "Still differs") **Under water and water in shade done 2026-10-28** (B3a): the camera under water gets the viewer's full-screen water fog, the atmospherics before the surface and the surface from below as underWaterF; the sun's shadow on the water's glints (HAS_SUN_SHADOW). Still open: finer ripples and the shallows, whose shaders read as faithful ports; it wants side-by-sides against the reference viewer (docs/rendering.md > Water) |
| Water exclusion surfaces (*added 2026-10-27*): the viewer's feature (7.1.12, 2025-03; "Hide Water" in the texture tab since 2025.03) where a face marked so hides water inside it (boat hulls, basements, pools below sea level) | render | M: boats and houseboats filled with water look broken, and vehicles are a VR draw | S: the viewer's water mask pass (a depth/stencil of exclusion faces before water) | verified absent (`water.frag`: "no water exclusion surfaces yet") **done 2026-10-28** (B3a): the viewer's mask (invisiprim faces of full alpha, no materials, not worn or rigged, in front of the water planes) read by the three water shaders as theirs do; `render_scenes.py water` (docs/rendering.md > Water) |
| Personal environment (*added 2026-10-27*): World > Environment's fixed times (sunrise, midday, sunset, midnight), "use shared environment", My Environments (EEP items from inventory applied locally), Personal Lighting, pause clouds; Firestorm's transition time and persistence | render, op | M: dark clubs and night regions are hard to see in a headset; photographers and builders live on it; local only, so within the TPV policy | S-M: `EnvironmentState` already has the Local layer (ENV_LOCAL); an op and a menu row to set it from presets or an inventory settings asset, and to drop back to shared | verified absent (`FrameRenderer`: "Sojourn can't change the sky while running yet") **done 2026-10-28** (B3a): `SojournEnvironment use` (the presets with legacy noon, skies, waters and day cycles from the inventory and Library, `mine` lists them, or by asset id), `shared`, `clouds` (and the `clouds` setting), UI.call World.EnvSettings; applied as LLEnvironment does over every place, cross-fades for the place's own changes (5 s, 1 s after a teleport, altitude tracks 5 s), clouds continuous (docs/rendering.md > The environment over time). Personal Lighting's sun and moon and the day cycle's time (SojournEnvironment `time`, 2026-10-29). Not done: Personal Lighting's other sliders (an edit of the sky: with environment editing, below), Firestorm's transition time and keeping an item between sessions (the `sky` preset is kept) |
| Environment editing (*added 2026-10-27*): making and saving sky, water and day-cycle settings (EEP items), applying one to my parcel or region | op | L-M: land owners and photographers | M: the settings asset's LLSD, `ExtEnvironment` PUT for parcel/region, inventory create (settings type) | verified absent |
| Lip sync (*added 2026-10-27*): speakers' mouths move with their voice (the viewer's "Move avatar lips when speaking", local per viewer from each speaker's voice power) | render | M: in VR, faces up close; without it voice comes from still faces | S-M: voice already has each peer's level (the speaking dots); drive the viewer's mouth morphs/visemes (`LLVoiceVisualizer` -> lip-sync params) in the animator | verified absent (no lip code in `Voice/` or `Bodies/`). **done 2026-10-29** (B3b): `lip_sync` (on), the viewer's curves smoothed, the head alone remade each frame; the system head only, as the viewer's |
| Ban lines and property lines (*added 2026-10-27*): parcel boundaries, the red ban walls (on collision or proximity), land owners' colours | render, info | M: in the headset an invisible ban wall bounces you without warning; property lines matter to land owners | S-M: ParcelOverlay (LibreMetaverse's `ParcelOverlay` event) to boundary geometry; the viewer's ban-line rule (shown within ~20 m when banned or no access) | verified absent (only `Audio/ParcelSound.cs` reads parcel boundaries, for sound) **done 2026-10-28** (B3a): `World/ParcelLines.cs`, over the finished image as the viewer's; `ban_lines` (collision, proximity, hide) and `property_lines` (off) settings; checked on the local grid with a test ban (docs/rendering.md > Property lines and ban lines). Real ban lines on SL are the user's check. Not done: the land owners' colour overlay on the mini-map (the mini-map is B8's map board since 2026-10-29; its parcel layer isn't drawn yet) |
| Beacons and highlights (*added 2026-10-27*): scripted, touch, physical, sound, particle and media sources; "Highlight Transparent"; the cheesy beacon toward a tracked place or person | render, info | L-M: finding the invisible touch point of a sign, who's spamming sound or particles; the tracking beacon is a VR way to find a friend in a crowd | S each: markers through the renderer's overlay; the assistant's "toward" cards already point the way | verified absent (no beacon code); the assistant's `show` toward/arrows cover pointing (`Showing/`). **done 2026-10-29** (B8, docs/rendering.md > Beacons and highlights): every kind's beacons and highlights as the viewer draws them, both eyes, with the floater's rules and defaults (`beacons` off; the switches on SojournSettings), sun and moon beacons, Highlight Transparent (Ctrl+Alt+T, `highlight_transparent`), the tracking beacon over a place or person with its label, ending on arrival (`SojournBeacons track`, the map board), Firestorm's tracker beep. Not done: the 2D arrow at the screen's edge toward a tracked target, the cheesy beacon's pulse, the window's beacon legend, picking through transparent faces while highlighted, Firestorm's region-corner beacons and chat range spheres |
| Water, glow and sky look settings the viewer offers (*added 2026-10-27*): screen-space reflections, depth of field, exposure/brightness, tone-mapping choice (Khronos Neutral / ACES, mix), sharpening, glow strength, max lights, max particles, terrain detail, avatar/tree/terrain LOD factors separately | render, op | L: SSR and DoF are usually off and DoF is wrong for a headset (a decision, not a gap there); exposure and particle count matter in busy places | S each, behind settings | verified: `tonemap.frag`, `cas.frag`, SMAA and SSAO are in the port; no SSR or DoF; the menu has one `detail` factor (`SettingsMenu.cs`) |
| glTF scenes as assets (`gltfscenemanager.cpp`, still experimental in the viewer) | render | L until LL ships it | M | inferred |
| Chat bubbles over heads (viewer option) | info | L: the tag's light and the feed carry who spoke (social.md) | S | social.md |
| The region's interest list in 360 mode (*added 2026-10-27*): the viewer can ask the region to send everything around the camera, not what's in front first (`InterestList` cap, mode 360, made for 360 snapshots) | render | M, if it's what it looks like: in a headset the head turns faster than a desktop camera, so things behind you may arrive late | S: one cap POST at login and region change, measured on SL before keeping it | inferred: Sojourn has no interest-list code; LibreMetaverse 3.2.0 has an `InterestListManager` (`SetModeAsync`, and a handler on SimConnected) whose default mode is unchecked; whether SL's ordering hurts us in the headset is unmeasured **built 2026-10-28** (B3a) as `interest_360`, off by default (the user's call after trying it on SL, 2026-10-30: things behind arrive sooner; docs/rendering.md > The interest list's 360 mode) |
| Reset skeleton, refresh attachments, resync animations (*added 2026-10-27*): local fixes for an avatar left deformed by a bad animation or joint overrides, attachments that didn't show, animations out of step (the viewer's Reset Skeleton (and Animations), Firestorm's Refresh Attachments, Resync Animations, Undeform) | render, op | M: deformed and half-drawn avatars are common on SL; today only a relog fixes them | S: rebuild the avatar's skeleton and overrides from its current attachments, re-request attachment objects, restart animation clocks | verified absent (no reset code in `World/Avatars*.cs`). **done 2026-10-29** (B3b): `SojournAvatars resetSkeleton` (yours sent as ViewerEffect 19; others' resets of their own obeyed), `refreshAttachments` (yours attached again, Firestorm's), `resyncAnimations`. Not done: Firestorm's Undeform (a pose of its own after the reset) |
| Crowd and animesh remainders (moved here 2026-10-27 from the end): the impostor count following the scene budget, jelly dolls' attachment particles still playing, animesh's 64 m scale fixup | render | L-M | S each | `docs/performance.md` > Crowds, `docs/rendering.md` > Animated objects. **2026-10-29** (B3b): jelly dolls' particles and the 64 m fixup done; the scene budget's impostors left to the load governor (the Avatar impostors row) |
| Region and viewer statistics for the person (*added 2026-10-27*): frame rate, ping, packet loss, the region's frame rate and time dilation, script time (the viewer's Statistics bar and Lag Meter; Firestorm's traffic indicator) | info, op | L-M: "is it me or the region?" is a common question in lag; the view line and logs have our side, nothing has the region's | S: SimStats (LibreMetaverse's `SimStats` event) on an op, an assistant answer, a wrist glance | verified absent (no SimStats or time dilation code). **done 2026-10-29** (B8, docs/map.md): `SojournMap stats` (the region's SimStats, ping, our fps, the lag meter's three states by its thresholds, the slowest part named, a verdict), the lag meter's bars on the location line, a statistics line under the map. Not done: the full Statistics floater (bandwidth, texture and mesh counts); packet loss (LibreMetaverse keeps none) |

At parity or close (verified against Multitude's side-by-sides, render.md, and Sojourn's code): PBR materials with
glTF overrides, legacy materials and alpha modes (blend, mask, emissive), bake-on-mesh, rigged mesh attachments,
sculpts, mesh and volume LODs by the viewer's rules (LOD-range fetches still to do), projectors with shadows,
reflection probes (automatic and manual), region EEP with altitude tracks and legacy WindLight, PBR terrain inputs,
particles (every pattern and flag), texture animation, glow, Linden trees and grass, water. Checked against LL's
content changes since 2023 (2026-10-27): glTF PBR materials, mirrors, PBR terrain, 2K textures (`upload` takes up to
2048), glTF meshes (they arrive as ordinary mesh assets), WebRTC voice (the only voice since Vivox's retirement on
2026-05-05), SMAA, CAS sharpening and the Khronos Neutral tone map are all in; water exclusion too since 2026-10-28.

## Avatars

| Gap | Where | Matters | Size | Basis |
|---|---|---|---|---|
| **HUDs**: drawn nowhere; the assistant can find a HUD prim (`TesselObjects nearby attachments:true` marks `hud`) and touch it (with surface coordinates since 2026-10-25), but neither it nor the person can see it or point at it | render, op, info | **H**: mesh body and head HUDs (alpha cuts, skins, nails), AO HUDs, dance and couples HUDs, RP meters, combat and games, vendor HUDs | M. A VR shape: draw HUD attachments into their own quad layer (as the panel's), a tablet the person summons and puts away, or on the wrist; `look` can take a picture of it for the assistant; touches by the ray (or by the assistant from that picture) carry the hit's UV, ST, normal and position | **done 2026-10-25**: desktop as the viewer; headset a board called up from a wrist token, 16:9 in the viewer's layout, its own quad layer, either trigger presses; `SojournHud` / the `hud` tool (pictures, press by point, part or name). Hover text on HUDs and media on HUD faces since 2026-10-26. docs/hud.md |
| Touch with surface coordinates in the world too (llDetectedTouchST/UV/Pos: piano keys, game boards, single-face vendors with arrows, web-like boards), and touch-and-hold (drag) | op | **H-M** | S: `RegionScene.Pick` already hits triangles; interpolate the UVs and send them in Grab/DeGrab | **done 2026-10-25**: touches carry face, ST, UV, position, normal, binormal (`RegionScene.Pick`, `Control/DirectTouch.cs`); touch-and-hold: `requestTouch hold`, the person's held trigger drags (ObjectGrabUpdate) |
| A client AO (Firestorm's `aoengine.cpp`: stand/walk/sit overrides from an inventory folder, no HUD) | op | M: scripted AO HUDs run server-side but are configured by touching their HUD; a client AO the assistant drives would sidestep HUDs ("switch to my other stands", "sit cross-legged") | M | verified absent; Firestorm source **done 2026-10-29** (B7, docs/inventory.md): `SojournAo`, Firestorm's AO ported (its folder format, sets, states, groups, tracks, Always, default set, sit override, smart sit, cycle and random, swimming), playing `#Firestorm/#AO` when there or a picked folder (a ZHAO-II notecard folder read in place), with OpenCollar/LockMeister switches through a worn relay (the user's call whether to wear one). Left: the AO floater's hand reordering and group/track editing |
| Seated controls in the headset: nothing is sent while sitting (`VrPilot.cs`: `stickWalk = ... && !sitting`), so vehicles, horses, boats, planes and llTakeControls games don't move; no mouselook or left-button controls for weapons and click-in-mouselook games | op (the person's controls) | **H** for vehicles (driving and flying in VR is a draw), M for combat | S: when seated (or when a script has taken controls, ScriptControlChange), the sticks send AtPos/AtNeg/YawPos/YawNeg/UpPos as the viewer's keys do; a trigger as the left button | **done 2026-10-25**: `Control/VrPilot.Seated.cs` (W/S, A/D as yaw, E/C; the view turns with the seat); every left-trigger press or world click is the left button, and only that when a script took it. Not done: mouselook-only weapons in the headset |
| Others' hover height, standing foot IK on slopes | render | L | S | hover height **verified applied** 2026-10-27 (`World/Avatars.cs` adds `HoverHeight`, seated and standing); standing foot IK **done 2026-10-29** (B3b): the default stands' LLKeyframeStandMotion, on the land or the foot plane |
| My own hover height (*added 2026-10-27*): Avatar > Hover Height, raising or lowering the avatar so mesh feet meet the floor (kept per account, AgentPreferences cap `hover_height`) | op | M: sunk or floating feet are a daily fix on SL, and in the headset your eye height follows the avatar's | S: the cap POST, an op and a setting | verified absent (no hover-height code outside the renderer's reading of others') **done 2026-10-27** (B2): `hoverHeight` and the `hover_height` setting, kept per account and POSTed to AgentPreferences on change and at login, a 2 cm stepper in the menu's "you" section (docs/people.md) |
| Combat and mouselook-only scripts in the headset (*added 2026-10-27*; was the seated row's "Not done"): weapons and games that take the mouselook button (CONTROL_ML_LBUTTON) or need the agent in mouselook (AGENT_CONTROL_MOUSELOOK); aim by head or hand; Firestorm's crosshair and target markers | op (the person's controls) | M: combat sims and shooting games are a large SL niche; in VR the aim is natural | S-M: send the mouselook flag while first person in the headset (or while a script wants ML controls), the trigger as the ML button, the body turned to the aim | verified (`DirectTouch.cs` knows ML controls; nothing sends AGENT_CONTROL_MOUSELOOK). **done 2026-10-27** (B1, docs/safety.md > Aiming): `aim` auto (while a script holds the mouselook button), on, off; `aim_with` head or hand; the mouselook flag, the body and head turned to the aim with its pitch, the right trigger as the mouselook button, a crosshair. Not done: Firestorm's target markers and zoom |
| Release keys (*added 2026-10-27*): take back controls a script has taken (the viewer's Advanced > Release Keys) | op, direct | L-M: a stuck vehicle or a broken weapon HUD keeps the sticks; the way out stands you up but doesn't release them | S: ScriptControlChange release (the viewer sends a ForceScriptControlRelease) on the menu and an op | verified absent. **done 2026-10-27** (B1): `SojournSafety releaseKeys`, menu > safety while controls are taken, and the way out releases them too |
| Appearance editing by the person (*added 2026-10-27*): the shape and outfit editors' sliders, with a mirror, alpha cuts and skins on the mesh body's HUD | direct | M: editing a shape is visual; by voice ("a bit taller", "softer jaw") it works but is slow to converge | M: a slider board like the HUD board, the mirror already shows you whole | verified: `editWearable` / `wearableParams` cover every param by op; no direct control |
| Poser and pose stand (*added 2026-10-27*; was a Building row): Firestorm's poser (bones posed by hand, saved as animations) and pose stand (a still T-pose to fit attachments) | op, direct | L-M: photographers; fitting attachments needs a still body | M: the poser is a joint editor over our animator; a pose stand is one built-in animation | Firestorm source (`fsfloaterposer`, `fsfloaterposestand`) **pose stand done 2026-10-29** (B7): `SojournAo poseStand` (Firestorm's poses on SL, any animation of the person's; the AO paused, the lock by ground sit). Left: the poser |
| Gestures, animations, attachments, outfits, bakes | | covered by ops (`LLGesture`, `TesselInventory` gestures, `playAnimation`, `LLAppearance`, `attach`) | | verified |
| Sit on the ground, fly, always run, stop animations (with revoking their permissions), the Library's starter avatars (the viewer's 2025 Avatar Welcome Pack), Library outfits | | covered (`requestSit` with no target, `move` run/fly, `stopAnimating`, `library` and `wear`) | | verified (`StockApis.cs`, `SelfAnimations.cs`) |

## Interaction

| Gap | Where | Matters | Size | Basis |
|---|---|---|---|---|
| Experiences: the experience permission prompt (teleporters, region games, auto-attach HUDs, Linden Realms-like content), "remember", the person's allow/block list | op, info | M-H: experience-run places silently fail without it | S: LibreMetaverse has `GetExperiencePermissionAsync`, `SetExperiencePermissionAsync`; route the prompt through `Notifications` and the confirmation gate as ScriptQuestion is | **done 2026-10-25**: prompts are ScriptQuestionExperience through the notifications and the confirmation gate (yes, no, block experience, block object); no allow/block list op yet |
| Experiences, the rest (*added 2026-10-27*): my allowed and blocked experiences (list, forget, block), an experience's profile, experience search, the region's experience settings for estate owners | op | L-M: a wrongly blocked experience breaks a region for good, with no way back today | S: LibreMetaverse 3.2.0 wraps the caps (`GetAgentExperiencesAsync`, `Get/SetExperiencePreferencesAsync`, `ForgetExperiencePermissionAsync`, `FindExperienceByNameAsync`, `GetExperienceInfoAsync`, `GetRegionExperiencesAsync`) | verified (only the prompt in `AgentSession.Events.cs`) **done 2026-10-27** (B2): `SojournPeople experiences/experience/findExperiences/setExperience/regionExperiences`, allow through the gate (docs/people.md). Untested against SL: OpenSim has no experiences |
| llMapDestination (a script offering a map location) and SLurls in chat or notecards | op, info | M-L: the assistant can teleport by name, but the offer itself never reaches the feed | S | verified 2026-10-27 (no ScriptTeleportRequest handler; the feed only resolves `app/agent` links, `SocialFeed.cs` AgentLink). A press on a SLurl in the feed or reader could offer "Go" through the confirmation gate **done 2026-10-27** (B2): ScriptTeleportRequest and SLurls (in a chat block, IM conversation or notice the person opens on the feed, a notecard opened in the reader, or `offerPlace`) become a PlaceOffered notice with a Teleport key (docs/people.md > Places). Left: SLurls aren't drawn as links on the feed; notecards' embedded landmarks |
| Hover tips (*added 2026-10-27*): pointing at an object or a person shows its name, owner, price for sale, "touch"/"sit" and the land's name (the viewer's Show Tips, land tooltips; Firestorm's extended tips with group titles) | info, direct | M: in the headset the pointer is the person's mouse; knowing what a thing is before touching it saves asking | S: a small label at the pointer's hit (the floating text's atlas), on dwell | verified absent (no tooltip code; the assistant's `inspect` answers when asked) |
| Bumps, pushes and hits (*added 2026-10-27*): who collided with, pushed or hit you (MeanCollisionAlert; the viewer's floater, Firestorm's collision reports to chat) | info | L-M: the first question when someone's pushing you around | S: the event on the feed (folded) and an op | verified absent (no MeanCollision handler in `Agents/`). **done 2026-10-27** (B1): kept for the session (`SojournSafety bumps`), pushes and hits on the feed by default (`bump_notices`), one per person a half minute; unchecked (OpenSim sends none: on the board) |
| Object inspection and script info (*added 2026-10-27*): an object's creator, owner, dates, permissions and land impact (the viewer's Inspect and Object profile), Firestorm's Script Info (a selection's or avatar's script count and memory), the viewer's attachment scripts list (2024) | op | L-M: "is this laggy?", "who made this?" | S: ObjectProperties and the script-info cap (`AttachmentResources`, `GetObjectCost`) | partly verified: `TesselObjects get full` and the assistant's `inspect` give owner, creator and perms; no script counts, memory or land impact |
| Web page offers (llLoadURL) can't be opened: shown as a notice, URL kept in the payload | op | M: without a browser; with one (the media row) it's a panel | with media | **done 2026-10-26**: "Go to page" (always confirmed) opens it up close (docs/media.md) |
| Game Control (*added 2026-10-27*): the viewer's gamepad input sent to the region (GameControlData) for scripts' `game_control` events; an alpha viewer since 2026-09-29, held for simulator work | op (the person's controls) | L until LL ships it; then M-H for us: VR controllers are gamepads, and it would let SL games read the sticks directly | S-M once the message is final | release notes (the alpha 26.4.0.35904126261); verified absent |
| Media, the rest (*added 2026-10-27*; from media.md "Not done"): first-click-interact (PRIM_MEDIA_FIRST_CLICK_INTERACT, 7.2.0, and Firestorm's per-setting choice), auto-zoom and auto-scale, editing a face's media settings, H.264 in pages (needs our own CEF build; the user's call on the board) | render, op | M: video sites and shop boards | S-M; the CEF build L | `docs/media.md` > Not done. **done 2026-10-29** (B10, docs/media.md > What plays): the viewer's focus-first click (a face's first click focuses it, a faint ring; first click interact by `media_first_click`, land owner's by default as both viewers), auto-zoom as up close on the focusing click (the view never moves; `media_auto_zoom`), auto-scale pages at their power-of-two size, `SojournMedia settings`/`configure`/`remove_media` (who may use or control it and removal confirmed). Not done: the parcel's prevent-camera-zoom (LibreMetaverse drops the block); H.264 stays the user's call |
| Pay price buttons (llSetPayPrice: the amounts an object accepts) | op, info | L-M: the assistant pays any amount; a vendor that wants exactly L$250 refuses others | S: RequestPayPrice | inferred **done 2026-10-27** (B2): `payPrices` (RequestPayPrice); a pay to an object its pay window wouldn't offer is refused before the person is asked, and the confirmation names its buttons (docs/people.md) |
| Touch, sit, menus (llDialog), text boxes (llTextBox), permission prompts | | covered: `requestTouch`, `requestSit`, `LLNotifications respond` (text boxes included), ScriptQuestion through the gate | | verified (`AgentSession.Events.cs`) |
| **A design question, not a gap**: every touch and sit goes through the assistant (2.5-4 s spoken). A door, a dance ball or a doorbell is the most common thing people do in SL, and the trigger is the VR norm for it. The left trigger is free (vr.md). Worth asking the user whether a direct point-and-trigger touch/sit belongs in the "information only" rule's exceptions, as the settings menu and confirmations are | | M | S | **decided 2026-10-25** (user): yes; the left trigger and a left click in the world touch or sit directly (docs/vr.md > Controls) |
| RLV / RLVa (Firestorm's `rlv*.cpp`: consent-based restrictions scripts apply, shared #RLV folders for outfit changes by script) | op | L-M: a minority use it, mostly roleplay; the dispatcher's gate is a natural place to enforce @restrictions for every caller | M-L | verified absent |

## Social

| Gap | Where | Matters | Size | Basis |
|---|---|---|---|---|
| Search for places, events, classifieds, land for sale, the Destination Guide (assistant.md "Not done") | op | **H**: finding somewhere to go is the first thing someone in VR would ask for | S-M: LibreMetaverse's `StartPlacesSearch`, `StartEventsSearch`, `StartClassifiedSearch` (Firestorm still offers this legacy search); the Destination Guide and web search are web APIs | **done 2026-10-26**: `SojournSearch` places/events/classifieds (DirPlacesQuery, DirFindQuery DateEvents, DirClassifiedQuery, each result filled in with where it is), the search tool; the Destination Guide by the web tools. Not done: land for sale. The local grid answers none of these, so the parsing waits on SL (board) |
| Offline IMs at login (the viewer asks the ReadOfflineMsgs cap) | info | M: messages sent while the person was away may never show | S | unverified: check whether LibreMetaverse calls `RetrieveInstantMessagesAsync` itself; Sojourn doesn't **done 2026-10-26**: LibreMetaverse never asks by itself; the session now does once logged in (ReadOfflineMsgs, else the RetrieveInstantMessages packet) and keeps their sent times (`AgentSession.Work.cs`); tested on the perf grid with `tools/offline_im.py` (OpenSim's store stand-in). SL's cap path: on the board |
| Friends: remove a friend, friend rights (see on map, modify objects, see online), contact sets (Firestorm) | op | L-M | S: LibreMetaverse's `FriendsManager.TerminateFriendship`, `GrantRights` | verified absent 2026-10-27 (`friends` reports the rights both ways, `TesselAgent.cs`; nothing changes them or removes anyone) **done 2026-10-27** (B2): `friendRights` (the map with online, the edit-rights warning) and `removeFriend`, both through the gate; Firestorm's contact sets and pseudonyms kept per account (docs/people.md). Since 2026-10-28 (B4) tags are ringed in a set's colour and show pseudonyms, as the feed's names do |
| Map: people on the map, events, land for sale (MapItems); a map the person can see | op, info, direct | L-M: `regions` covers names and counts; an info element showing the region's map tile fits the assistant-made info elements being added | S-M | verified (`regions` only, by MapBlocks). Corrected 2026-10-27: the assistant can show a small map of up to 8 points (`Showing/ElementSpec.cs` MapBlock) but with no map tiles; no MapItems (people, events, land for sale, telehubs); no world map or mini-map the person opens themselves (both viewers have both; Firestorm's mini-map with chat-range rings). **done 2026-10-29** (B8, docs/map.md): the map board, world map and mini-map in one zoom (the grid's tiles, regions, crowds, people close up with the chat and shout rings, friends with map rights, land for sale and events), pick a spot and Go or track it; opened from the location line or `SojournMap show`. Not done: the mini-map's owners' colours and property lines, above/below marks, a turning mini-map |
| Teleport history (viewer and Firestorm), favourites bar | op | L-M: "take me back to the place before" | S | verified absent **done 2026-10-27** (B2): `teleportHistory` (each teleport and login, kept per account, nothing in incognito), `favorites`, `addFavorite`, `removeFavorite` (docs/people.md) |
| Display name change, profile notes (private notes on others), classifieds in profiles | op | L | S | verified absent (LibreMetaverse has `SetDisplayNameAsync`, `RequestAvatarNotes`) **done 2026-10-28** (B2): `displayName`/`setDisplayName` as the viewer's floater (31 characters, the week's lockout refused before asking, the exact name confirmed, SetDisplayNameReply read), others' changes as DisplayNameUpdate notices; `notes`/`setNotes` by AgentProfile, else the old notes messages (docs/people.md). Left: classifieds in profiles. Display names untested here (OpenSim has none) |
| Away / Do Not Disturb with an autoresponse, typing indicator sent | op, info for others | L-M: DND is how people say "in VR, busy"; the TPV policy note means an autoresponse is the person's words, set by them | S | verified absent 2026-10-27 (only the BUSY animation id in `SelfAnimations.cs`; nothing sends typing or the typing animation, nor sets away after idle) **done 2026-10-27** (B2): Away (automatic after `away_after`, 5 min, from the head, desktop input and the assistant) and Do Not Disturb as the viewer's llimprocessing (IMs and offers held from the feed, objects' IMs and offers turned away, a reply once a conversation), the reply in the person's words confirmed on the exact text (the viewer's sentence until then), Firestorm's away reply (off by default), typing sent while dictating to people with the typing animation; the menu's "you" section (docs/people.md). DND declines voice calls since 2026-10-29 (B10: a one-to-one caller gets the DND sentence, docs/voice.md). Tags carry the away, Do Not Disturb and appearance marks since 2026-10-28 (B4) |
| Keyword alerts (Firestorm), group titles, away marks, friend colours on tags | info | L | S | social.md "Not done". Since: the viewer's chat mentions (2025: @name in chat, highlighted with a sound), the same alert path **done 2026-10-28** (B4, docs/social.md): keywords per account (`SojournSocial keywords`) and @mentions lit on the feed with the viewer's alert sound; group titles, away / Do Not Disturb / appearance marks (glyphs), contact sets' colours as a ring and friends' as an option on tags; tags hidden behind walls; chat bubbles (off); hover tips |
| Firestorm's "reject" switches (*added 2026-10-27*): reject teleport offers, group invites, friendship requests; leave all conference chats; auto-accept inventory offers; only friends and groups can IM or call me (the viewer's privacy setting) | op, info | L-M: in the headset an offer flood is noise; "only friends can IM me" is the official viewer's | S: filters in `Notifications` before the feed, settings, and the account's own IM privacy (UpdateAgentInformation) | verified absent **done 2026-10-28** (B2): `friends_only_ims` as the official viewer's (a client-side setting there too, not UpdateAgentInformation: one-to-one IMs, lures and requests from non-friends, conferences they start left), `reject_teleports` (all, not friends'), `reject_friendships` with Firestorm's sentences in the person's own words, `reject_group_invites`, `auto_accept_inventory` (the official viewer's three types or Firestorm's all); `Agents/Rejects.cs`, the menu's "you" section (docs/people.md). The call half of friends-only and Firestorm's reject group, conference and one-to-one calls since 2026-10-29 (B10, `Voice/CallRejects.cs`, docs/voice.md). Left: Firestorm's "leave all conference chats" |
| Radar alerts (*added 2026-10-27*): someone entering or leaving chat range, draw distance or the region (Firestorm's radar reports), age shown for very new accounts | info | L-M: the classic safety cue at clubs and homes; in the headset a soft note on the feed | S: `people` already tracks who's near; a diff each second to the feed, behind a setting | verified (people's list exists; no alerts) **done 2026-10-28** (B4): Firestorm's chat range, draw distance and region reports (enter, leave or both, each off by default) and its age alert as feed notices; `SojournSocial radar` |
| Chat translation (*added 2026-10-27*): the viewer translates incoming chat (Azure, Google, DeepL, LibreTranslate keys) | info, op | M: SL is international; the assistant translates when asked, but nothing shows translated chat on the feed | S-M: through the assistant's model or a local one, on the feed's lines, behind a setting; a cloud translator is a privacy choice for the person | verified absent (the assistant can translate on request) **plumbing built 2026-10-28** (B4): `translate_chat`, `translate_to`, `translator` (none), translations drawn under the lines, never a remote service while incognito; which service is the user's call (the board) |
| Profile feed, Flickr, Primfeed, Discord presence (*added 2026-10-27*): posting a snapshot to the profile feed or a social site, Discord rich presence (both viewers, opt-in) | op | L | S-M each | verified absent; outward-facing, so each is the person's own press |
| Voice, the rest (*added 2026-10-27*; from voice.md "Not done"): starting an ad-hoc conference call, the moderator API (the viewer's 2026 WebRTC voice moderation, 26.2: land, estate and group moderators mute everyone, sticky for late joiners, or one person), voice mutes on SL's own mute list, auto-reject group/conference/P2P calls (Firestorm), hearing from the camera or the avatar | op | L-M | S each | `docs/voice.md` > Not done. **done 2026-10-29** (B10, docs/voice.md > Using it): `SojournVoice conference`; `moderate`/`moderateAll` (nearby through SpatialVoiceModerationRequest for estate managers and parcel owners, calls through ChatSessionRequest for their moderators; confirmed, refused before asking where the person can't); voice mutes as voice-only entries on the block list (the mute list now carries the viewer's flags); `reject_p2p_calls`, `reject_group_calls`, `reject_conference_calls`, B2's friends only and DND for calls, voice-muted callers; `voice_hear_from` camera/avatar/mixed. Only SL can show conferences, moderation and the mute list across viewers working (the board) |
| IM history from LL's server (*added 2026-10-27*): the 26.5 Chat Modernization beta syncs one-to-one IM history with SL Mobile over a new HTTPS service (full history for MFA accounts) | info, op | L-M now (beta), M once it's the default viewer: conversations would follow the person between Sojourn and the phone | S-M once the service is documented | release notes (26.5 beta, 2026-10); verified: Sojourn keeps its own transcripts (`AgentState.cs` > IM transcripts) |
| Group chat moderation and snoozing (*added 2026-10-27*): mute a member in group chat (moderators), snooze or leave a group's chat for a while, disable a group's chat (Firestorm) | op | L | S: ChatSessionRequest | verified absent (`TesselGroups` has eject, roles, titles, notices) **done 2026-10-28** (B2): `snoozeGroupChat` (Firestorm's, 15 min by default), `setGroupChat` (Firestorm's receive group chat, kept as its "Group:<id>" block so it holds on SL across viewers), `moderateGroupChat` (the viewer's text moderation, confirmed), `groupChat` (members, moderators, who's stopped) (docs/people.md). Voice moderation (calls and nearby) since 2026-10-29 (B10, docs/voice.md) |
| IMs, conferences, group chat and notices, profiles and picks, teleport offers, requests and lures, group management | | covered (`TesselIM`, `TesselGroups`, `TesselAgent`) | | verified |

## Inventory

| Gap | Where | Matters | Size | Basis |
|---|---|---|---|---|
| Thumbnails (the viewer's inventory gallery), Marketplace listings folder, Firestorm's protected folders | op, info | L: words describe items well enough to the assistant | S-M | verified absent **done 2026-10-29** (B7, docs/inventory.md): thumbnails read from AIS3 and shown as pictures (`SojournInventory picture`; a texture's own image without one), set from a texture, a file or the view (cropped, scaled, uploaded as the viewer does); protected folders and the AO folder's lock, refused before any confirmation. Left: the Marketplace listings folder; thumbnails need SL (OpenSim has no AIS3) |
| Inventory favourites, Firestorm's favourite wearables (*added 2026-10-27*): the viewer's 2024-2025 favourite flag on items and folders, the Favorites tab | op | L | S: AIS3's item field | verified absent (the Favorites *folder* type is known, `InventoryOps.cs`; not the flag) **done 2026-10-29** (B7): `SojournInventory favorites` (the whole tree read from AIS3 once a session) and `favorite`. Left: Firestorm's favourite wearables (its own folder of links); SL only |
| Browsing inventory by the person (*added 2026-10-27*): a visual inventory in the headset (folders, thumbnails, wear/rez by pressing or dragging into the world) | direct | M: "wear my red dress" works by voice, but looking through what you own is browsing, not asking | M-L: a board like the HUD board, thumbnails first | verified: everything is by op (`TesselInventory`, `LLInventory`, `find_items`); no direct view. The user's call whether Sojourn wants one (it's a 2D floater by another name) |
| Offers, folders, outfits, wearing, giving, the Library, notecards, landmarks, gestures | | covered (`TesselInventory`, `LLInventory`, `LLAppearance`, `SojournAssistant findInventory`) | | verified |

## Building and scripting

| Gap | Where | Matters | Size | Basis |
|---|---|---|---|---|
| PBR on faces: applying a glTF material item, editing overrides, uploading materials | op | M: building is a promised strength, and new content is PBR | S-M: LibreMetaverse has `CreateMaterialAsync`; the render-material extra param is already read | verified (`BuildOps` ModifyKeys has no PBR key). **done 2026-10-29** (B6, docs/building.md): TesselBuild modify `pbr` (a material applied, overrides added to or reset, taken off; ModifyMaterialParams as the viewer sends it), `SojournBuild materials` reads each face's id and override back; uploads in the row below |
| Normal and specular maps; media faces; projector and reflection-probe params; object group and deeding (texture offset, rotation, planar mapping, bump and physics shape were added 2026-10-25) | op | M-L | S each | verified (`BuildOps` ModifyKeys). Corrected 2026-10-27: reflection probes (`reflection_probe`, mirrors included) and animesh (`animated`) are done; a light takes colour, intensity, radius and falloff but no projector texture, FOV, focus or ambiance. **done 2026-10-29** (B6, docs/building.md): `normal` and `specular` (legacy materials through RenderMaterials, `alpha_mode`, `alpha_cutoff`), `projector`; object group and deeding (`SojournBuild group`, `deed`, gated); media faces are B10's `SojournMedia settings`/`configure`/`remove_media` |
| SLua (Luau) scripts; script errors on the debug channel for the person and the assistant | op, info | L-M: LSL compiles with errors returned; whether script errors reach the assistant was not checked (social.md: the feed filters them) | S-M | Corrected 2026-10-27, partly verified: errors from the person's own objects reach the assistant as LLChat debug events (`AgentSession.Events.cs`) and the feed leaves them out (`SocialFeed.cs`), so the person sees none (the viewer's script warnings window); the external-editor RPC creates `luau` scripts (`Scripts/ScriptEditor.cs`) and `createScript`/`writeScript` pass `target` to the cap untouched (`luau` untried); no compile-target choice for the viewer's four (Mono, LSL, Lua, LSL on Luau). The viewer made SLua part of its default release on 2026-10-13 (26.4, a compiler choice in the script editor, Lua compiling only on Lua-enabled regions, the VS Code bridge over the same external-editor protocol Sojourn serves); how the target reaches the server is unchecked. **done 2026-10-29** (B6, docs/building.md): the targets are `mono`, `lsl2`, `luau`, `lsl-luau` in UpdateScriptTask/UpdateScriptAgent's `target`, offered where the region's `LuaScriptsEnabled` feature is on; `SojournBuild recompile`, and inventory `createScript`/`writeScript` take all four; saves keep a script's experience. Script errors for the person: still open |
| Bulk script and object work on a selection (*added 2026-10-27*): recompile (Mono, LSL, Lua, LSL on Luau), reset, set running or not, remove scripts (Firestorm); save back to object contents; take as combined or separate items; bulk permissions on inventory | op | L-M: builders' chores, easy by the assistant once they exist | S each: the viewer's compile queue is a loop over contents with the UpdateScriptTask cap; DeRez to the object's task inventory | partly verified: `script` sets running or resets one script (`ContentsOps.cs`); no recompile, no save-back, no bulk perms. **done 2026-10-29** (B6, docs/building.md): `SojournBuild scripts`, `recompile`, `resetScripts`, `runScripts`, `removeScripts` (gated) on whole linksets; `saveBack` (gated); `TesselBuild take {separate}`; `bulkPerms` on contents or an inventory folder and `setDefaultPerms` (both gated); `landImpact`, `scriptInfo` |
| Building by the person (*added 2026-10-27*): select, grab, move, rotate and scale with handles, snap to grid, undo/redo, the edit floater's tabs (the viewer's whole Build menu and its Options) | direct, op | M: builders are a large part of SL; the assistant builds from words and specs well (the watchtower, the cabin) but fine placement by hand is what a headset is good at; undo is missing for the assistant too | L: handles in the world from the pointer, a small tool board; undo as the last few modify/create states kept per object | verified: every edit is an op (`BuildOps.cs`); no handles, no undo. The user's call on how much hands-on building Sojourn wants. **Undo done 2026-10-29** (B6, docs/building.md): `SojournBuild history`/`undo`/`redo` for every edit through the API, a build's prims made and linked as one step. Handles and the rest: B9 |
| Pathfinding (*added 2026-10-27*): linksets' pathfinding types, characters, rebake the navmesh, view the navmesh | op | L: region builders of NPC games | S-M: the pathfinding caps | verified absent. **done 2026-10-29** (B6, docs/building.md): `SojournBuild pathfinding`, `linksets`, `setLinksets`, `characters`, `rebake` (SL only, unchecked here: OpenSim has none). Not done: the navmesh's view (long tail) |
| Material upload and the glTF uploader (*added 2026-10-27*): Upload > Material (a glTF material as an inventory item), the 2025 glTF mesh import, the Develop menu's glTF scene editor | op | L-M (materials go with the PBR row above) | S-M | partly verified: `uploadMesh` reads .gltf/.glb into a mesh (`UploadOps.cs`); no material items. **done 2026-10-29** (B6): `SojournBuild uploadMaterial` (textures first, then the material item; gated where uploads cost). The glTF scene editor waits on LL shipping it |
| Local textures/meshes (preview before upload), the poser (Firestorm's `fsfloaterposer`) | op | L | M | Firestorm source. The poser moved to Avatars 2026-10-27 |
| Export and backup of one's own creations (*added 2026-10-27*): Firestorm's Backup (linkset to XML with assets the person created), Collada export, linkset import | op | L-M: builders' safety net; checked against permissions and creator as Firestorm does (full perms and own creation only) | M | Firestorm source (`fsexport.cpp`); verified absent. **done 2026-10-29** (B6, docs/building.md): `SojournBuild canExport`, `backup` (Firestorm's .oxp, its own-creation rules), `backups`, `restore` (re-uploading only what the grid lacks, gated). Not done: contents in backups, Collada export |
| Create, modify, link, take, rez, contents, script create/read/write, uploads of textures, sounds, animations (BVH) and meshes | | covered (`TesselBuild`, `TesselInventory`, `TesselAgent upload*`) | | verified |

## Land and region

| Gap | Where | Matters | Size | Basis |
|---|---|---|---|---|
| Parcel access and ban lists (view, add, remove) | op | M for land owners: the lasting answer to a griefer (`parcelEject` with ban covers the moment) | S: ParcelAccessList | verified absent. **done 2026-10-28** (B5, docs/land.md): `SojournLand access`, `allow`/`unallow`/`ban`/`unban` (for hours or for good), the parcel's access switches on `set`, freeze |
| Where you are, for the person: parcel name, rating, no-fly/no-build/no-push/no-voice/damage marks and health in damage areas (the viewer's location bar) | info | M-L: in the headset the person only learns where they are by asking | S: an info element | inferred. **done 2026-10-29** (B8, docs/map.md): the location line, above the left wrist while looked at (the desktop's top left), the viewer's icons by its rules (voice, fly, push, build, scripts, avatars hidden, damage with health, for sale), the rating, the lag meter; pressed, the map board; `SojournMap where`; `location_line`, `lag_meter` settings |
| Region and estate info, covenant, region restart schedule | op, info | L-M | S: `RequestCovenant`, RegionInfo | verified absent. **done 2026-10-28** (B5): `SojournLand region` (for anyone; the schedule where the grid has the RegionSchedule cap), `covenant` (its notecard's text) |
| Buy, sell, abandon, deed, divide, join land; terraform | op | L | S-M | verified absent. **done 2026-10-28** (B5): `buy`, `sell`/`stopSelling`, `abandon`, `deed`, `reclaim`, `divide`, `join`, `terraform` (the brush and the land's change drawn), each gated; land outlined on the ground while asked. Deed unchecked here (no group on the test accounts) |
| Estate and region tools (*added 2026-10-27*): estate managers, allowed and banned residents and groups for the estate, region restart and its delay, top scripts and colliders (the viewer's Top Objects), telehubs, region textures and terrain heights, disabling scripts or collisions in an emergency | op | L-M: for estate owners and managers, rare among visitors but urgent when needed (a griefer's lag bomb) | M: EstateOwnerMessage and LandStatRequest, each behind the confirmation gate | verified absent (no estate code; `ParcelOps.cs` is parcel-level). **done 2026-10-28** (B5): estate lists (`estate`, `estateAdd`/`estateRemove`: managers, allowed residents and groups, bans), `kick`, `sendHome`, `message`, `setRegion`, `regionDebug`, `restart` and its cancel, `topScripts`/`topColliders` and `returnObjects`, `bakeTerrain`. Not done: telehubs, region textures and terrain heights, terrain upload/download, the estate's own settings (EstateChangeInfo: LibreMetaverse misreads the sun hour it would send back) |
| Land holdings, group land contributions, buying a pass (*added 2026-10-27*): My land holdings, Buy Pass on a pass-restricted parcel, the parcel's sale settings | op | L-M: passes gate clubs and venues; a pass is a spend, through the gate | S: ParcelBuyPass, the land holdings (via the group and agent data) | verified absent. **done 2026-10-28** (B5): `buyPass` (the parcel that just turned you away by default), `holdings`, `contribute`, the sale settings on `sell`; land for sale in search (`SojournSearch land`) and the map's items (`mapItems`) |
| About Land and its settings | | covered (`parcelInfo`, `parcelSet`, `parcelObjects`, `parcelReturn`, `parcelEject`) | | verified |
| Region restart warnings, set home, teleport home | | covered (`AgentSession.Events.cs` RESTART alerts on the feed; `setHome`, `goHome`) | | verified |

## Money and marketplace

| Gap | Where | Matters | Size | Basis |
|---|---|---|---|---|
| Marketplace browsing and buying, buying L$, transaction history (all web in the viewer too) | op | L-M: shopping in VR by voice is appealing, but it's web and money; the gate would need to cover it | M: a web tool the assistant can use, with spending confirmations | verified absent. **The user's decision** (money and the person's payment details): see Closing the rest |
| Pay confirmations and money notices (*added 2026-10-27*): Firestorm's "confirm before paying over L$N", L$ received and spent notices (both viewers), Firestorm's money tracker (who paid me, this session) | op, info | L-M | S: the gate already confirms every pay; the notices are MoneyBalanceReply's transaction info on the feed | partly verified: every pay waits for the person's press (`Confirmations.cs`); received money isn't shown **done 2026-10-28** (B2): PaymentReceived, PaymentSent, PaymentFailure on the feed in the viewer's sentences (`money_notices`), `SojournPeople money` as the money tracker (docs/people.md). Untested here: the local grid moves no L$ |
| Balance, pay, buy objects | | covered, behind the confirmation gate | | verified |

## Snapshots and preferences

| Gap | Where | Matters | Size | Basis |
|---|---|---|---|---|
| "Take a picture" for the person: the assistant's `look` is for its own eyes, and file-writing ops are refused to it; F12 needs the keyboard; no saving to inventory or the profile feed, no 360 capture | op | M: photos are a big part of SL, and the headset view is worth keeping | S: an op that saves the current eye image to a fixed pictures folder (no path from the caller) | **done 2026-10-26** (corrected 2026-10-27): `SojournCapture` snapshot and record (Pictures/Videos\Sojourn), the capture tool. Open: next row |
| Snapshots, the rest (*added 2026-10-27*): to inventory (an upload, L$ on some grids), to the profile feed or email, 360 snapshots (equirectangular, the interest list in 360 mode), high resolution beyond the eye's size, a picture from a camera placed away from the head; a direct shutter on the controller | op, direct | L-M | S-M each | verified (`CaptureApi.cs`: the view's image or a video, saved to disk only) **done 2026-10-29** (B8, `docs/assistant.md` > Pictures and videos): `snapshot` sizes up to 7680x4320 (tiles of one frame), placed cameras (presets around the person, or a point and a look-at), `panorama` (the viewer's 360: six faces, GPano JPEG, 360 interest list meanwhile), `toInventory` (Photo Album, a spend on SL), `postcard` (email, the region's SendPostcard, always confirmed); checked on the local grid. Left: the profile feed (refused: LibreMetaverse drops the login's openid_token, which the viewer's my.secondlife.com upload needs), the shutter on the controller (built by batch M, 2026-10-29: a camera at the left forearm, the right trigger its shutter; Ctrl+` on the desktop; `docs/assistant.md` > The panel > Pictures by hand), the viewer's image filters and frozen-frame preview; SL-only: the postcard and the inventory upload's price |
| Preferences the viewers have and Sojourn doesn't (*added 2026-10-27*): UI language (the viewer's 12), time format, away timeout, name display (display names, usernames, legacy names), group tag display, chat font size, cache size and clearing it, media auto-play finer than `media_autoplay`'s mine/all/off (others' attachments' media, HUD media; Firestorm's media filter of allowed and blocked sites), parcel music fading, sounds from collisions and gestures, "play typing animation" | op | L-M each; together they're what a release's settings are expected to have | S each | verified against the settings registered in `SettingsApi.cs`, `MediaWiring.cs` and `AudioWiring.cs` (2026-10-27). The menu is already too complex for release (the user, `docs/session-2026-10-27b.md` finding 1), so these mostly belong on the API and the assistant, not as rows |
| Maturity preference (General / Moderate / Adult): teleports into regions above it fail, and the viewer offers to change it | op | M: a blocker the person can't get past | S | **done 2026-10-25**: `maturity` on SojournSettings and the menu (view), the account's own until changed (UpdateAgentInformation) |
| Volumes (master, sounds, music, media, UI) and per-object mutes | op | with audio | S | **done 2026-10-25**: volume, sounds, music on SojournSettings and the menu (media volume 2026-10-26); blocked residents' and objects' sounds are dropped |
| Every setting the person has | | covered (`SojournSettings`) | | verified |

## Safety

| Gap | Where | Matters | Size | Basis |
|---|---|---|---|---|
| Report abuse (the viewer's `llfloaterreporter.cpp`, with a snapshot) | op | **H**: LL's own channel under the Community Standards; the person can't file one any other way while in-world | S: LibreMetaverse `UserReport.SendUserReport`, categories from the cap; the person's words, sent at their press | **done 2026-10-26**: `SojournReport` draft/send (`Control/ReportApi.cs`; LibreMetaverse's UserReport is internal, so our own after the viewer's), a picture of the view, the confirmation gate for every send, the report tool. The SL caps and picture upload untested (a test reaches LL) |
| A way out that doesn't wait on the assistant: go home, stand, or leave from the settings menu or a controller chord | op | **H-M**: being caged, orbited, flooded with particles or sound, or harassed in a headset needs an exit in under a second, not 3 s and a model's reading | S | verified (`goHome` is an op only) **built 2026-10-26**: `Control/WayOut.cs`: the left menu button held 0.8 s (a short press opens the menu on release) or Ctrl+Shift+H stands you up and teleports you home, a notice in the feed. Not pressed for real yet: posted key messages don't reach GLFW's modifier state, so the chord and the hold need the person (board) |
| Derender objects and particles, and an asset blacklist (Firestorm `fsassetblacklist.cpp`) | op | M: griefers' particle and sound spam, eyesores; `hideAvatars` covers people only | S-M | verified absent (2026-10-27: still none; blocking an object drops its sounds, chat and dialogs but it's still drawn). Includes the viewer's "Block particle owner" and Firestorm's auto-hide of objects with huge texture area. **done 2026-10-27** (B1, docs/safety.md): `SojournSafety derender` (taken out of the world model and kept out, until a teleport or for good, rerender asks the region again), sounds blacklisted by asset or by owner (worn, rezzed, gestures), animations blacklisted, the sounds heard lately, particle sources and Block particle owner (a blocked owner's particles no longer draw), the `particles` switch; menu > safety. Not done: people (hideAvatars covers them, not kept), Firestorm's auto-hide by texture area |
| Antispam: rate limits on object IMs, dialogs, offers and sounds (Firestorm `NACLantispam.cpp`) | info | M: a spam flood fills the feed and the assistant's context | S | verified absent. **done 2026-10-27** (B1, docs/safety.md): Firestorm's rules (10 in 4 s per source and kind, sounds 60, 70 lines), menus also by owner, off by default as in Firestorm (the user, 2026-10-27); `antispam` setting, `SojournSafety spam`/`unspam`; llLoadURL from blocked objects now dropped too |
| Comfort when something else moves you: pushes, seats and vehicles carry the room (vr.md), with no vignette | render | M: forced motion is the classic VR sickness case | S-M: the vignette in vr.md "Not done"; a "keep me in place" (Firestorm's movelock) | vr.md; verified 2026-10-27 (no vignette or movelock code). **done 2026-10-27** (B1, docs/safety.md): the comfort vignette (`comfort_vignette` off by default, forced or all; `vignette_strength`; a head-locked layer under the menus); keep me in place (`keep_in_place`, off by default: the view held and the avatar walked back in the headset, walked or teleported back on the desktop; Firestorm's is a worn LSL bridge). Not done: a push resisted as llMoveToTarget would (that needs a worn script: the user's call) |
| Block (people, objects, by name), voice mutes, hiding people, ejecting and banning from your parcel | | covered (`mute`, `SojournVoice peerMute` (on the block list's voice flag since 2026-10-29), `hideAvatars`, `parcelEject`) | | verified |

## Releasing it (*added 2026-10-27*)

Not viewer features as such, but what every released viewer has and Sojourn, run from `tools/client.py` with command-line
logins, doesn't. Policy detail in `docs/tpv-policy.md` > If it's ever distributed.

| Gap | Where | Matters | Size | Basis |
|---|---|---|---|---|
| A login the person does: a login screen in the window and in the headset (saved accounts, the password kept by the OS's credential store, grid choice: Second Life, the Beta grid, OpenSim grids as Firestorm's grid manager), start location (last, home, a region or SLurl), the ToS and critical messages read and accepted by the person's press in the headset, MFA codes typed there, logout and relog | direct | **H** for a release | M | verified (`docs/client.md`: `--first/--last/--password(-file)/--start`; ToS and MFA answered in the console) **built 2026-10-28** (R): `--login`, `docs/client.md` > Logging in; passwords under DPAPI in our own folder rather than the OS's credential store |
| What the TPV policy asks of a distributed viewer: the About window with version and source, an honest channel and version at login, the viewer statistics report (2.h, the `ViewerStats` cap), the disclaimer, a privacy policy, LGPL source published, the AI operating the client disclosed | info | **H** for a release (policy) | S-M | `docs/tpv-policy.md` **started 2026-10-27** (R): `SojournRelease about` (`Client/Release/ReleaseApi.cs`) gives the name, the channel and version sent at login, the disclaimer and the licence note, checked on the local grid. Left: an About sheet on the menu and login screen, a per-build version, `ViewerStats`, the privacy policy and the LGPL source (the user's), the AI disclosed on the download page |
| An installer: CEF, libVLC, libwebrtc, OpenJPEG, the Python sidecar with Whisper and Kokoro models, the OpenXR loader, in one package; updates only with consent; a clean uninstall | | **H** for a release | L | verified (everything is built and fetched by `tools/*.py` into `local/`) |
| Crash reports and logs a person can send (opt-in, both viewers have them), the viewer's "report a problem" | | M | S-M | verified (logs go to `logs/` in the base dir; nothing sends them) |
| The assistant for people who aren't us: signing in to their own Claude account, what works without one (today nearly everything the person can't do with their own hands goes through it), and the costs a person meets | | **H** for a release; **the user's decision** | M | `docs/assistant.md` (the Agent SDK on the user's subscription) |
| Simpler menus and a release-sized settings set (the user, 2026-10-27, `docs/session-2026-10-27b.md` finding 1); the desktop's own interface, to be workshopped once VR is good (CLAUDE.md decision) | direct | **H** for a release | M | the user's findings |
| UI language: the panel, menus, feed and the assistant's replies in the person's language (the viewers ship 12) | info | M | S-M: the assistant already answers in the language it's spoken to; the client's own words are few | verified (English only) |

## Left from the 2026-10-26 list

Written by Claude Opus 5.5, 2026-10-26. Done from the list: crowds (impostors, complexity limit, jelly dolls, per-person
choices), offline IMs, the way out; then animesh, search (places, events, classifieds; the Destination Guide by the
web), report abuse, and the small items from the user's sessions (where the panel and HUD board were put kept between
sessions, per-person voice volumes kept, phantom, the assistant's error_during_execution). The draw list's CPU cost
is the renderer's work. Open (the crowd and animesh items are also a row under Rendering since 2026-10-27):
- Crowds: GPU numbers (the Roomsense detector training held 11 of 12 GB of VRAM through this session, so every GPU
  timing after 13:45 was paging); a stereo check that near impostors read as solid in the headset; the impostor count
  stepping down with the scene budget.
- Search and reports on SL itself: the local grid answers no places, events or classifieds and has no report caps, so
  the search results' parsing and the report's screenshot upload wait on a main-grid session (search on the board; a
  report only when there's something real to report). Land for sale is searched since 2026-10-28 (`SojournSearch land`, B5).
- Animesh: the 64 m size limit (the viewer's scale fixup). Done 2026-10-29 (B3b), with jelly dolls' particles; the
  impostor count is the load governor's (the Avatar impostors row).

## Closing the rest (2026-10-27)

Written by Claude Opus 5.5, 2026-10-27, after the user's direction in the headset: Sojourn is released as a full VR
viewer, so every open row above gets closed, not only the high ones. This groups them into batches that separate
agents can build at once without editing the same files, and orders them by what a release needs first. Sizes are
the batch's whole model work (S a day or less, M a few days, L a week or more).

### How the batches share the code

The files every batch would want are owned by one batch each, so the rest only add new files:
- **New ops go in new files**: a new `LeapApi` class per batch (e.g. `SojournPeople`, `SojournLand`) with its work in
  a new `Agents/*Ops.cs`, registered with one line beside the others. `TesselAgent.cs` and `TesselApis.cs` get fixes,
  not new ops.
- **Settings** are registered from the batch's own wiring file with `api.Add(...)`, as `MediaWiring.cs` and
  `AudioWiring.cs` do; `SettingsApi.cs`, `SettingsMenu.cs` and `VrSettings.cs` belong to batch M (menus). A batch whose
  feature needs a menu row or a controller binding says so in its handoff, and M places it.
- **The assistant**: a new op reaches it at once through `client_api` and `api_help`. `assistant/tools.py`,
  `prompt*.md` and incognito's allowlist (`Private/Allowlist.cs`; an op not on it is refused while incognito is blind,
  the safe default) belong to batch T, which runs after each wave.
- **Confirmations**: an op that spends, gives, deletes or changes permissions adds its case to `Confirmations.cs` as an
  appended hunk. It's the one shared file all batches touch; small hunks keep merges trivial.
- Each batch works on its own branch or worktree and merges main in before handing back. The local grid and the GPU are
  shared: render tests one GPU-heavy agent at a time, and clients on the grid coordinated by hand (test slots were
  retired on 2026-10-27).
- Not in any batch, because other agents were on them on 2026-10-27: flexi prims, avatar physics, falling through
  floors, the own avatar lost at region crossings, menus in the desktop mirror. Batches touching their files
  (`Bodies/`, `Geometry/VolumeBuilder.cs`, `VrPilot*.cs`, `Agents/CrossingGuard.cs`, `Realtime/LayerMirror.cs`,
  `Program.cs`) start after those merge.

### Wave 1: what a release can't go without

1. **R. Release** (L). The login screen in the window and the headset (saved accounts, grids, start location, ToS and
   MFA by the person's press), the TPV policy's distributed-viewer list (About, channel and version, `ViewerStats`,
   the disclaimer, a privacy policy, LGPL source), an installer and updater with consent, opt-in crash reports. Files:
   a new `Client/Login/`, the start of `Program.cs` (after the mirror agent merges), a new `tools/package.py`. Its
   decisions (below) are the user's; the login can be built now, it's needed either way. **Begun 2026-10-27**: only
   `SojournRelease about` (the Releasing it rows); the batch was cut short for a headset session. The shape worked out
   for the rest: the account is chosen before the session exists (each account and grid has its own state dir, so
   switching accounts restarts the client into the login screen, as `DeviceLoss.Restart` does), with the command line
   and `dev.py` logins left as they are; the ToS, MFA and failures on a sheet in the panel's place, pressed only by real
   input; saved passwords as the viewers keep them (`$1$` + MD5, never plain) under DPAPI in a folder the assistant's
   deny rules and shell hook (`assistant/harness.py`) name; updates from a signed manifest (ECDSA, the key kept in
   `local/`), installed only at the person's press. From the viewers' source (develop, master; 2026-10-27): ViewerStats
   is an LLSD POST to the region's `ViewerStats` cap once a region is up, then every 300 s and at quit
   (`llviewerstats.cpp` `send_viewer_stats`: `agent`, `system` with OS, CPU, RAM, GPU and also `mac_address` and
   `serial_number`, `downloads`, `stats` net/failures/misc; no other residents' data; Firestorm drops `ui`); the ToS
   floater's Continue waits for a checkbox, the MFA prompt saves `mfa_hash` only with "Remember this computer for 30
   days" (Sojourn always saves it today); both viewers quit on logout, neither relogs; LL's updater (Velopack, query
   `update.secondlife.com/update/v1.2/{channel}/{version}/win64/...`) asks per `UpdaterServiceSetting` (3 automatic, 1
   ask, 0 mandatory only), Firestorm has none; the official viewer sends BugSplat crashes without asking, Firestorm's
   `CrashSubmitBehavior` is ask (default), always or never; the disclaimer's wording is the policy's, in neither tree.
   **The login built 2026-10-28** (Claude Opus 5.5; `docs/client.md` > Logging in, `Client/Login/`): `--login` shows
   the login screen in the window and the headset (saved accounts, a new one by username, password, grid: Second Life,
   Beta or a login URI; start last, home or a region or SLurl; remember password, forget), the void with the sheet over
   it (`FrameRenderer.Void.cs`); the ToS, critical messages, MFA and failures in plain words on the sheet in the panel's
   place until the first arrival, command-line logins too; one state dir per account and grid; log out (menu > you),
   Back and Cancel restart into the login screen; a lost GPU logs the same account back in. Saved passwords as the
   viewers keep them under DPAPI in `local/secrets/logins` (a release's under AppData), named in `harness.py`'s deny
   rules and shell hook. Departures from the plan: no OS credential store (DPAPI in our own folder, as the brief
   settled); no relog without a restart. Left for R: the installer and updater, crash reports, `ViewerStats`, the
   About sheet and privacy policy (the user's release decisions, on the board), and the login screen as a packaged
   build's default (today `--login`).
2. **B1. Safety and comfort in the headset** (M-L). Derender objects and particles, the asset blacklist and "block
   particle owner" (a filter where objects enter the scene, `World/RegionSource.cs`, and an op); antispam (rate limits
   in `Agents/Notifications.cs` before the feed); the comfort vignette and movelock (`Xr/`, a new
   `Control/Comfort.cs`); release keys; combat and mouselook-only scripts in the headset (`Control/DirectTouch.cs`,
   `VrPilot.Seated.cs`); bumps and pushes reported. Starts after the floors agent merges `VrPilot*`. **Built 2026-10-27** (docs/safety.md;
   the rows above say what's left).
3. **B2. People and me** (M). Friends removed and their rights, display name, profile notes, Away and Do Not Disturb
   with the person's own autoresponse, the typing indicator sent, my hover height, experiences' lists, teleport history
   and favourite places, the reject switches and "only friends can IM me", pay price buttons, llMapDestination and
   SLurls as offers through the gate, money received and spent on the feed, group chat moderation and snoozing. Files: new `Agents/PeopleOps.cs` and
   `Leap/Apis/SojournPeople.cs`; its offer filters go into `Agents/Notifications.cs` after B1's antispam merges (B1 owns
   that file in wave 1). **First part built 2026-10-27** (docs/people.md): friends' rights and removal, contact sets,
   Away and Do Not Disturb with the person's own reply, typing, hover height, experiences, teleport history and
   favourites, pay price buttons, llMapDestination and SLurls as offers (not gated: a teleport the person presses).
   **The rest built 2026-10-28**: display name, profile notes, the reject switches and "only friends and groups can
   IM me", inventory kept by itself, money received and spent on the feed, group chat snooze, off and moderation (the
   filters went beside DND's in OnIM, `Agents/Rejects.cs` and `GroupChat.cs`, rather than into `Notifications.cs`,
   which stays the generic notification store). What only SL can show: docs/people.md > Checks only Second Life can do.
4. **B3a. World rendering** (M-L; this wave's GPU agent). Water exclusion surfaces, the personal environment (presets,
   inventory EEP items, back to shared; an op), ban lines and property lines (ParcelOverlay), the interest list's 360
   mode measured on SL, water's ripples and shallows. Files: `Shaders/water.*`, `Environment/`,
   `World/RegionEnvironment.cs`, a new `World/ParcelLines.cs`.
   **Built 2026-10-28** (Claude Opus 5.5; checked on the local grid, desktop mono and stereo): water exclusion surfaces,
   the camera under water (the viewer's fog, haze order and underWaterF, none of which were ported), sun shadows on the
   water's glints, the personal environment (SojournEnvironment `use`, `mine`, `shared`, `clouds`; UI.call
   World.EnvSettings; cross-fades and clouds over time), property and ban lines, the interest list's 360 mode as a
   setting. Files beyond the list: `Frame/Renderer.Water.cs` (the water pass, moved), `Frame/Renderer.ParcelLines.cs`,
   `Agents/EnvironmentOps.cs`, `Control/EnvironmentWiring.cs`, and hunks in `RegionScene.cs`, `DrawList.cs`,
   `Renderer.cs`, `RenderTargets.cs`, `FrameRenderer.cs`, `VrSettings.cs`, `StockApis.cs`, `Program.cs`. Left: ripples and
   shallows (side-by-sides with the reference viewer), Personal Lighting's sliders, and what only SL shows (real ban
   lines, the 360 mode's effect; the board). The first agent's notes from the viewer's source, kept:
   - *Water exclusion*: a face is one when its texture is the invisiprim alpha gradient (IMG_ALPHA_GRAD or
     IMG_ALPHA_GRAD_2D, the two invisiprim ids `RegionScene.cs` knows; which id is which is unchecked, and the "Hide
     water" box sets IMG_ALPHA_GRAD; both load as GL_ALPHA8, so never in the alpha pool), its colour alpha >= 0.999, no legacy material (the panel's "Hide water" removes it) and no glTF material,
     and it isn't on an attachment, rigged or a HUD (`llvovolume.cpp` rebuildGeom, PASS_INVISIBLE; also with shiny).
     Sojourn today just skips those faces (`RegionScene.cs` InvisiprimA/B). The viewer's `doWaterExclusionMask` runs
     just before the water pool: an R8 target with its own depth, cleared to 1, the water planes drawn double-sided in
     white (`pushWaterPlanes(0)` and the edge tiles `(1)`), then the PASS_INVISIBLE faces in black with depth testing;
     `waterF.glsl`, `underWaterF.glsl` and the deferred `waterHazeF.glsl` read it (WATER_EXCLUSIONTEX) to drop water
     and its haze where it's 0. Port those three shaders' use of it as they are.
   - *Personal environment*: half exists. The `sky` setting and menu row already give the viewer's four fixed skies
     (`Program.cs` SkyFor, `FrameRenderer.BuildView` puts it in a Local layer over the region's and parcel's), and
     `EnvironmentState.Local` takes any layer. Missing: water and day cycles in the local layer, inventory settings
     items (fetch the asset, `DayCycle`/`SkySettings`/`WaterSettings.FromLlsd`), the op (`UIApi`'s `World.EnvSettings`
     still refuses in `StockApis.cs`), the viewer's cross-fade, pause clouds.
   - *Ban lines*: ParcelProperties with sequence id COLLISION_BANNED / NOT_ON_LIST / NOT_IN_GROUP carries a bitmap of
     the parcels you'd collide with (`llviewerparcelmgr.cpp` processParcelProperties -> writeSegmentsFromBitmap); drawn
     by renderCollisionSegments for a few seconds after, per the `ShowBanLines` setting (hide, on collision (default),
     on proximity). Property lines come from ParcelOverlay (`llviewerparceloverlay.cpp`, not yet read).

### Wave 2: what people meet every session

5. **B3b. Avatars** (M; after the avatar-physics agent merges `Bodies/`). Lip sync from voice levels; reset skeleton,
   refresh attachments, resync animations; point-at and edit beams both ways; standing foot IK; the crowd and animesh
   remainders (impostors with the scene budget, jelly dolls' particles, the 64 m fixup). Files: `Bodies/Animator.cs`,
   `World/Avatars*.cs`, a level hook from `Voice/`.
   *Read, not built (2026-10-27, from the viewer's develop branch):* lip sync is `LLVOAvatar::idleUpdateLipSync` setting
   `Lipsync_Ooh`/`Lipsync_Aah` from voice power (`LLVoiceVisualizer::lipSyncOohAah`, setting `LipSyncEnabled`); here the
   morph weights `AvatarAnimator.Evaluate` returns, levels through a shared tracker like `LookAtTracker.Shared`, but
   `Avatars.cs` rebuilds shapes at most 10 a second, one avatar a frame (choppy mouths: maybe morph the head only).
   Reset skeleton: drop the body cache, restart animation clocks, and for yourself send ViewerEffect 19 (17 bytes: the
   target id and a reset-animations byte). ViewerEffect types: Beam 7, Point 9, Spiral 12, Edit 13, LookAt 14, PointAt
   15, ResetSkeleton 19; a received PointAt starts left-arm IK (`lleditingmotion.cpp`). Foot IK:
   `llkeyframestandmotion.cpp`. 64 m fixup: `llcontrolavatar.cpp` (`MAX_LEGAL_SIZE` 64, scale 64/size). The user's
   call: whether Sojourn sends PointAt and beams at all (they move your arm in others' viewers; point-at was declined
   for now on 2026-10-26).
   **Built 2026-10-29** (Claude Opus 5.5; docs/rendering.md > Avatars: lip sync, others' effects, standing feet, fixes;
   checked by `tools/avatarfx check` and on the local grid by `tools/avatars.py`): lip sync (`lip_sync`), others' beams,
   edit beams and spirals (`selection_beams`) and a PointAt's arm, the stands' foot IK, reset skeleton (and animations;
   yours sent), refresh attachments, resync animations, jelly dolls' particles, the 64 m fixup. Receiving only: Sojourn
   sends no PointAt and no beams (nothing built for it). The scene budget doesn't add impostors (the load governor
   already does, as the user agreed). Files beyond the list: `Bodies/Animator.Ik.cs`, `JointSolverRP3.cs`,
   `World/RegionScene.Worn.cs`, `Control/AvatarsWiring.cs`, `AvatarsApi.Fix.cs`, and hunks in `BodyShape.cs`
   (the head's normals before normalising), `DrawList.cs`/`Renderer.cs` (vertices made this frame skinned with their
   own weights), `ParticleBuilder.cs`, `FrameRenderer.cs`, `Program.cs`. For batch T: `SojournAvatars resetSkeleton`,
   `refreshAttachments`, `resyncAnimations` (incognito: all three harmless, the first two name a person); for batch M:
   rows for `lip_sync` and `selection_beams` (view), and Reset Skeleton / Refresh Attachments on a person's card or the
   "you" section.
6. **B4. The social layer** (M). Chat bubbles; away/busy marks, group titles and friend colours on tags; keyword alerts
   and @mentions; radar alerts; translated lines on the feed (behind a setting); a SLurl pressed in the feed or reader
   offering "Go"; hover tips at the pointer; tags hidden behind walls. Files: `Client/Social/`.
   *Read, not built (2026-10-27):* the viewer's tags depth-test (hidden behind walls is its default); marks "(Away)",
   "(Do Not Disturb)", "(Appearance)" from ANIM_AGENT_AWAY/DO_NOT_DISTURB/CUSTOMIZE; group titles from the "Title"
   name-value (shown by default); NameTagShowFriends off by default (Firestorm colours by contact set: B2's
   `PeopleOps.SetColor`, `Pseudonym`); chat bubbles off by default, 12 lines; hover tips on after 0.7 s (land tips off);
   TranslateChat off; Firestorm's radar alerts all off; FSKeywords per account; 2025 @mentions are agent links ending
   `/mention` shown "@name" (`llviewerchat.cpp`). Plan: `Social/SocialWiring.cs` and a `SojournSocial` API; alerts as
   feed entries so the screen reader gets them. The user's calls: cloud translation, hover tips on by default given
   little text, how set colours look on the glass tags.
   **Built 2026-10-28** (Claude Opus 5.5; docs/social.md, checked on the local grid by `tools/social.py`): all of the
   above, with translation's plumbing but no service. Defaults chosen: marks as glyphs (a moon, a no-entry sign, a
   hanger) rather than the viewer's words; group titles and usernames only near or looked at; a set's colour as a
   ring round the tag; hover tips on, names and prices only. Opening a block or a notecard no longer offers every
   place in it (B2's way): a press on the link does. For batch M: rows for chat bubbles, tags through walls, hover
   tips, keyword alerts and the radar's three reports.
7. **B5. Land and region** (M-L). Access and ban lists; region and estate info and the covenant; estate tools behind the
   gate (managers, estate bans, restart, top scripts and colliders, disable scripts); buy, sell, divide, join, abandon,
   deed; terraform; buy pass; land holdings; land for sale in search; MapItems. Files: `Agents/ParcelOps.cs` (B5 owns
   it), new `Agents/EstateOps.cs` and `Leap/Apis/SojournLand.cs`, `Agents/SearchOps.cs`.
   **Built 2026-10-28** (Claude Opus 5.5; `docs/land.md`, checked on the local grid by `tools/land.py`): every item;
   files beyond the list `Agents/ParcelOps.Deals.cs`, `ParcelOps.Terraform.cs`, `LandMap.cs`, `AgentSession.Land.cs`,
   `Client/Land/LandWiring.cs` (the confirmations, through `Confirmations.Cases`, so no hunk in `Confirmations.cs`),
   `World/ParcelLines.Selection.cs` (the land outlined and the brush), and hunks in `ParcelLines.cs`, `Terrain.cs`,
   `StockApis.cs`, `TesselAgent.cs` (parcelSet stays the parcel you stand on), `SojournSearch.cs`, `Program.cs`. Left:
   the rows' "Not done" and what only Second Life shows (docs/land.md > Checks only Second Life can do).
8. **B7. Inventory and the avatar's own** (M). Thumbnails (read for the assistant's pictures, and uploaded:
   LibreMetaverse has `UploadThumbnailAsync`), favourites, protected folders, a client AO from an inventory folder
   (Firestorm's, with its OpenCollar/LockMeister switches), a pose stand. Files: `Agents/InventoryOps.cs`,
   `Agents/AgentInventory.cs`, `Agents/SelfAnimations.cs`, a new `Agents/AoEngine.cs`.
   **Built 2026-10-29** (Claude Opus 5.5; `docs/inventory.md`, checked by `tools/aocheck` and on the local grid by
   `tools/ao.py`): thumbnails (read, shown, set and uploaded) and favourites over AIS3, protected folders (refused
   before the confirmation is asked, through a new `Confirmations.Refusals`), the AO (`AoEngine.cs` pure, `AoFolders.cs`,
   `AoDriver.cs`; `SojournAo`), the pose stand. Files beyond the list: `InventoryMeta.cs`, `AoFolders.cs`, `AoDriver.cs`,
   `PoseStand.cs`, `AccountStore.cs`, `AgentSession.Inventory.cs`, `Leap/Apis/SojournInventory.cs`, `SojournAo.cs`,
   `Client/Inventory/InventoryWiring.cs`; hunks in `AgentSession.Events.cs`, `Confirmations.cs`, `Program.cs`.
   Sojourn makes no inventory folder by itself: the AO plays `#Firestorm/#AO` when there, else a folder the person
   picks; `newSet` makes `#Firestorm/#AO` only when asked (the board's open decision can change that). For batch T and M,
   and what only SL shows: docs/inventory.md. What the first agent found, from Firestorm master and LibreMetaverse 3.2.0:
   - LibreMetaverse 3.2.0 has `InventoryManager.UploadThumbnailAsync(item_or_folder, task, j2c bytes)` (the
     InventoryThumbnailUpload cap, 64-256 px, J2K encoded by the caller), but no thumbnail id or favourite flag on its
     inventory nodes: reading either means parsing AIS3's `thumbnail.asset_id` and `favorite` fields ourselves (and
     setting them by AIS PATCH). OpenSim's support for both is unchecked, so the local grid may not test them.
   - Firestorm's AO (`aoengine.cpp`, `aoset.cpp`, ~4.4k lines): sets are subfolders of `#Firestorm/#AO`, states
     subfolders of a set holding links to animations, flags in the folder names (`:**` default set, `:SO` sit override,
     `:SM` smart sit, `:DM` no mouselook stands; states `:CY` cycle, `:RN` random, `:CT<s>` cycle time; newer `:TR`
     tracks and an "Always" state). 26 states keyed to the default motions (stand, walk, run, sit, ground sit, crouch,
     land, fly, hover, jump, turn, typing, and swimming mapped from flying under water). It overrides in
     `LLVOAvatar::startMotion/stopMotion` for self: when the region lists a default motion, start the state's
     animation by AgentAnimation (stand variants STAND_1-4 and sit variants stopped; SIT_GENERIC kept under an
     overridden sit to cover loop points; transient states played once). For Sojourn that hook is the region's
     AvatarAnimation list for the own avatar (`SelfAnimations.Wire`). It creates `#Firestorm/#AO` itself, so a build
     here must not create or move folders in the person's inventory without their say. ZHAO-II notecard import too.
   - The OpenCollar/LockMeister switches (`BridgeIntegrationOC/LM`) arrive through Firestorm's worn LSL bridge
     (`<clientAO state=on|off|standon|standoff>`), so they need a worn script: the user's call, like movelock's push
     resistance.
   - Pose stand (`fsfloaterposestand.cpp`, `app_settings/posestand.xml`): a list of pose animations by SL asset id
     (PS_T_Pose 3481ccd5-..., arms down, sitting, ...), played on self with movement locked; those assets exist on SL
     only, so the local grid needs its own stand-in pose.
   - Protected folders (`fsfloaterprotectedfolders.cpp`, per-account `FSProtectedFolders`): folder ids the viewer
     refuses to move, rename or delete (`LLInventoryModel::getProtectedCategories`, checked in
     `llinventoryfunctions.cpp` beside the system folder types); `InventoryOps.IsSystem` is the natural check.
9. **B10. Voice and media, the rest** (S-M). Voice: ad-hoc conference calls, WebRTC moderation, voice mutes on SL's
   mute list, auto-reject switches, hearing from the camera or the avatar (`Client/Voice/`). Media: first-click-interact,
   auto-zoom and auto-scale, a face's media settings by op (`Client/Media/`); the H.264 CEF build stays the user's call.
   **Built 2026-10-29** (Claude Opus 5.5; the rows above; docs/voice.md, docs/media.md). Menu rows and assistant
   lines for batches M and T are listed in those docs; what only SL shows is on the board.

### Wave 3: builders, land owners, the long tail

10. **B6. Building** (L). PBR materials applied, overridden and uploaded; normal and specular maps; projector
    parameters; media face settings; object group and deeding; SLua's compile targets and the bulk script chores
    (recompile, reset, running, remove); save back to contents, take combined or separate; default and bulk inventory
    permissions; undo for the assistant's edits; script info and land impact; pathfinding; export and backup under
    Firestorm's own-creation rules; glTF material uploads. Files: `Agents/BuildOps.cs`, `UploadOps.cs`,
    `ContentsOps.cs`, `Scripts/`, new ops files.
    **Built 2026-10-29** (Claude Opus 5.5; `docs/building.md`, checked on the local grid by `tools/building.py`): every
    item but media faces (B10's) and Collada export; new `SojournBuild` (`Leap/Apis/SojournBuild.cs`) over
    `Agents/MaterialOps.cs`, `BuildUndo.cs`, `ScriptChores.cs`, `ObjectInfo.cs`, `Ownership.cs`, `Pathfinding.cs`,
    `Backups.cs`, `AgentSession.Building.cs`, and `Client/Building/BuildWiring.cs` (confirmations through
    `Confirmations.Cases`); TesselBuild modify's keys `pbr`, `normal`, `specular`, `alpha_mode`, `alpha_cutoff`,
    `projector`; hunks in `TesselApis.cs` (modify awaits materials, one undo step; take's `separate`), `InventoryOps.cs`
    and `GestureOps.cs` (default permissions, Lua targets), `Program.cs`. Left: contents in backups, Collada export, the
    navmesh's view, and what only Second Life shows (docs/building.md > Checks only Second Life can do).
11. **B8. Information for the person** (L; GPU for its shots, after B3a). A world map and mini-map board (map tiles,
    MapItems from B5, friends by their rights), the location line at the wrist (parcel, rating, no-fly, no-build,
    no-push, no-voice, health in damage areas), statistics at a glance (SimStats), beacons and Highlight Transparent,
    the rest of snapshots (360, to inventory or the profile feed, higher resolution, a camera away from the head).
    Files: a new `Client/Map/`, `Showing/`, `Assistant/CaptureApi.cs`, the renderer's overlay.
    **Built 2026-10-29** (Claude Opus 5.5 with two helpers; checked on the local grid and in the XR Simulator): the map
    board and the location line (`docs/map.md`, `Client/Map/`, `tools/map.py`, `SojournMap`), statistics at a glance (the
    same), beacons, highlights, Highlight Transparent and the tracking beacon (`docs/rendering.md` > Beacons and
    highlights, `Client/Beacons/`, `World/Beacons.cs`, `tools/beacons.py`, `SojournBeacons`), the rest of snapshots
    (`docs/assistant.md` > Pictures and videos, `Client/Capture/`, `FrameRenderer.Still.cs`). Shapes chosen: the world
    map and mini-map are one board with a continuous zoom, north up; the location line is the wrist's watch face (shown
    while you look at it; pressed, the map); the client's own boards are a ninth overlay picture
    (`OverlayScene.BoardAtlas`) and a card group on the headset's layers. Left: the profile feed (refused: it needs the
    login's openid_token, which LibreMetaverse drops; outward-facing, the user's call), the rows' "Not done", and what
    only Second Life or the headset shows (the docs' check lists).
12. **The long tail** (each S-M, in any order once its area's batch is done): environment editing and saving EEP items;
    the remaining look settings (exposure, tone-mapping choice, particle cap, separate LOD factors, SSR; depth of field
    for desktop pictures only); local textures and meshes; the navmesh view; terrain paint maps; glTF scenes once LL
    ships them; Game Control from the controllers once its message is final; IM history from LL's new service once
    26.5 is the default; UI language.

### Usable without sight (*added 2026-10-27*)

13. **A. Accessibility** (L over its batches A0-A8). Neither viewer has it: screen-reader output (NVDA, UIA
    notifications) of chat, IMs, notices and the assistant, keys for confirmations and notices (today mouse-only),
    bearings and summaries of what's around, follow and routes around walls, earcons and beacons, description on
    request, and what works without a Claude account. The plan, ordered by what would help a blind Radegast user first
    (A1, the screen reader and keys, first): `docs/accessibility.md`. A2 (an accessible desktop window) waits on the
    user's call about the desktop's own UI.

### After each wave

- **T. The assistant** (S-M a wave). Dedicated tools for what people ask often (land, people, the AO, the map), the
  prompt's lines, incognito's classification of every new op, end-to-end checks with `tools/assistant.py`.
- **M. Menus and settings** (M; the look is the user's call). The menus made much simpler for release (the user's
  finding), the new settings that need a direct control placed (the personal environment's presets, Do Not Disturb,
  the vignette, movelock, the AO), and the missing preferences (the Snapshots and preferences row) built on the API
  for the assistant rather than as rows. Starts after the mirror-menus agent merges.

### Direct controls, after the user's call

- **B9** (L each): building by hand (handles, snapping, a tool board, undo), an inventory board, an appearance board of
  sliders beside the mirror, the poser. Each is a viewer floater's job done the headset's way; whether, and which, is a
  direction question for the user, since Sojourn's premise is that the assistant is the interface.

### Waiting on the user's decision

- **RLV/RLVa**: restrictions a script imposes on the person's own client, by their consent. The dispatcher's gate is
  the natural place to enforce them for every caller, the assistant included; but some restrictions (no teleport, no
  standing, no IMs) collide with the way out, which has to stay a sure exit. Whether to build it, and whether the way
  out overrides it.
- **Buying L$ and the Marketplace**: real money and the person's payment details (web pages in the viewers too).
  Whether Sojourn only opens them for the person's own hands, or the assistant may browse and fill a cart up to the
  gate; never card data either way.
- **The assistant for other people**: their own Claude account, its costs, and what Sojourn offers without one.
- **Outward-facing posts** (profile feed, Flickr, Primfeed, Discord presence) and **cloud translation**: built only if
  wanted, each the person's own press or setting.
- **Direct controls** (B9) and **the simpler menus** (M): how much of the viewers' hands-on UI belongs in Sojourn.
- **The release itself**: its name, the disclosures, a privacy policy, the TPV directory listing, the LGPL source
  published.
