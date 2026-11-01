# The client

The desktop side and what both modes share. The headset: `docs/vr.md`.

Written by Claude Opus 5.5, 2026-10-20. Agent-facing.

## Running it

```
python tools/grid.py start                 # the local OpenSim grid (Atelier on :9700), see "The local grid"
python tools/client.py run                 # builds Release if needed, logs in Sojourn Pilot, opens the window
python tools/client.py run --stereo --eye 2064x2208 --fov 98 --shadow-cap 1536    # two Quest 3-sized eyes
python tools/client.py run --bench --stereo --eye 2064x2208 --fov 98 --bench-out local/client/bench.md
python tools/client.py vr                  # in the headset (docs/vr.md)
python tools/client.py vr --login --base-dir local/main   # the login screen: the person picks the account (below)
```

Started from a Claude Code session (any tool, CLAUDECODE=1 inherited), the client stays in the background: its window
shows without taking focus and never captures the mouse, so tests don't take over the PC the user is working at
(`Window/Background.cs`; the user, 2026-10-26). A run meant for the person sets `SOJOURN_FOREGROUND=1`, as
`dev.py start` and `client.py vr` do; in tests, use posted input (`tools/winmouse.ps1`, `winkey.ps1`), never real input
or activation.

The client builds as **SojournVR.exe** / `SojournVR.dll` (since 2026-10-29; `Sojourn.exe` is the launcher, what people
start: docs/launcher.md). The .NET 10 SDK is `%LOCALAPPDATA%\dotnet-sdk\dotnet.exe` (not on PATH); `SojournVR.exe`
won't start on its own (it finds only the machine's .NET 6), so run `dotnet SojournVR.dll`, which `tools/client.py` does. The window is
titled "Sojourn ..." (fps, CPU and GPU ms, size, draws, mode, where); it never contains "Second Life", because OBS
captures Tessel's viewer by that title. Vsync is on by default, which caps the frame rate at the display's; the
GPU is shared with Tessel's viewer, so keep uncapped runs (`--no-vsync`, `--bench`) to a few minutes.

Options (`src/Sojourn.Client/ClientOptions.cs`):

| | |
|---|---|
| `--grid URL`, `--first`, `--last`, `--password` / `--password-file`, `--start uri:Region&x&y&z` | login (defaults: the local grid, Sojourn Pilot, Atelier 128,118) |
| `--login`; `--login-shots DIR` | the login screen instead of the options above ("Logging in"); its sheet's stages drawn to PNGs, then quit |
| `--window WxH`, `--eye WxH` | window size; render size per eye (default: the window, or half its width in stereo) |
| `--stereo`, `--ipd M`, `--fov DEG` | two eyes side by side (F5 toggles); eye separation; vertical FOV (60 default, ~98 for a Quest 3 eye) |
| `--shadow-cap N` | longest side of the shadow maps (the viewer sizes them to the screen; a settings override) |
| `--no-vsync`, `--fps N` | uncapped (mailbox); a sleep-based cap |
| `--draw-distance M`, `--sky sunrise\|noon\|sunset\|midnight` | draw distance; a fixed sky instead of the region's day (both settings overrides: the menu's view section, saved) |
| `--bench`, `--bench-seconds S`, `--bench-out FILE` | the measurement tour, then quit (`docs/performance.md`) |
| `--shot-every S` | a PNG of every eye every S seconds into `local/client/shots/` (to check without looking); F12 takes one |
| `--minutes M` | quit after M minutes |
| `--bridge-port P` | the agent API (9720) |
| `--validate` | Vulkan's validation layer (`local/vulkan-validation`) and OpenXR's core validation (`local/openxr-sdk`), through `tools/client.py` |
| `--vr`, `--xr-scale`, `--hz`, `--xr-wait`, `--quest-eyes`; `--seated`, `--standing`, `--snap`, `--smooth-turn`, `--turn-speed`, `--hand-relative`, `--no-body` | the headset and its settings: `docs/vr.md` |
| `--no-assistant` | don't start the assistant's Python side (`docs/assistant.md`) |
| `--dev` | dev mode: no Claude of its own, the Claude Code session that started it is the assistant (`tools/dev.py`, `docs/dev-mode.md`) |
| `--no-voice`, `--voice-mode ptt\|open`, `--voice-unmuted` | voice chat off; push-to-talk or open mic (a settings override); open mic starting live (tests): `docs/voice.md` |
| `--no-sounds` | no world audio (objects' and gestures' sounds, footsteps, UI sounds, the parcel's music): `docs/audio.md` |
| `--assistant-model quick\|sonnet\|opus` | who takes the assistant's requests first (saved setting otherwise; `docs/assistant.md` > Claude) |

Environment: `SOJOURN_AVATAR_CLONES=N` draws every avatar N more times in a grid behind it (crowd load),
`SOJOURN_AVATAR_DEBUG=1` logs animation and attachment detail (both inherited from Multitude),
`SOJOURN_FRAME_LOG=FILE` writes a CSV line per frame (time, frame ms or in the headset the display-time step, CPU ms,
fence wait, captures, pending, then CPU ms of scene, avatars, probes, recording, and the rest: acquiring and presenting),
`SOJOURN_LMV_LOG=Information` (or Debug, Warning) shows LibreMetaverse's own log (Error otherwise),
`SOJOURN_MOTION_LOG=FILE` the own avatar's reported and drawn position per frame (desktop), `SOJOURN_VR_LOG=FILE`
the headset rig per frame, and `SOJOURN_VR_SCRIPT=1` runs the rig's test routine (`walk`: stick walks only;
`docs/vr.md`). `tools/client.py ... --logs` sets the frame and rig logs to `local/client/frames.csv` and `vr.csv`;
`tools/vrlog.py` summarises them. For performance work: `SOJOURN_FETCH_LATENCY_MS` / `SOJOURN_FETCH_KBPS` give texture
fetches a far grid's latency and bandwidth, `SOJOURN_MOTION_LEGACY=1` the old avatar smoothing and no predicted walk,
`SOJOURN_BUILD_ROOT=DIR` builds into a tree of its own (so a second session can build while another's client holds the
usual bin folder), and `SOJOURN_GRID=perf-grid` points `tools/grid.py` at the performance grid (`docs/performance.md`).

Every session's console also goes to `logs/session-YYYYMMDD-HHMMSS.log` in its base dir (the newest ten kept;
`local/main/logs/` for main-grid sessions run with `--base-dir local/main`). Every 10 s it has a `view:` and `assets:`
line: objects drawn and culled, draws, texture fetches in flight and waiting, fetch rate, failures, decodes, VRAM,
mesh downloads, and the garbage collector's counts and pauses.

## Controls

W/S or up/down walk, A/D strafe, Q/E or left/right turn, Shift runs, Space jumps (up while flying), C or PgDn
down, F or Home toggles flying, V or M switches first/third person (first person hides your own avatar and
captures the mouse), hold the right mouse button to steer with the mouse in third person, wheel zooms. F3 stats in the title, F5 stereo, F6 prints the GPU pass report to the console, F7 vsync, F12 a shot.
A left click in the world (not on the panel, a key or a card's ×) touches or sits as the viewer's does, and the
headset's left trigger (`docs/vr.md` > Controls); in first person it's the mouselook button for scripts only, as in the
viewer. It needs a hand on the mouse: clicks posted by programs don't touch (`Assistant/RealInput.cs`).
A right click on a thing (a quick one: held or dragged, it steers) brings up its card of what can be done with it, at
the click, as the headset's still hold does (`docs/vr.md` > A thing's card); a click off it puts it away.
HUDs are drawn over the view as the viewer draws them; a click on one is the HUD's (touch, drag), the mouse on one is
"that", Alt+Shift+H hides them (`docs/hud.md`).
Media (`docs/media.md`): a click on a page in the world clicks it (the wheel scrolls it, typing goes to a field it has
focused, Esc gives the keys back), on a video pauses or plays it, on media that waits starts it; Z shows what's under
the pointer up close (Z, Esc or a click outside puts it away).
The assistant (`docs/assistant.md`): hold T to talk, Enter to type a request (Enter sends, Esc cancels), the mouse
points at "that", click keys on its panel (top right), 1-9 pick a choice, Tab hides or shows the panel, F8 cycles its model (quick, sonnet, opus). `/ask ...` in the console
types one too; `/model [quick|sonnet|opus]` shows or sets the model.
Esc is layered, as in games: each press frees the mouse and closes or cancels the nearest thing open or waiting, and
only when nothing is does it open the settings menu. In order: typing (to the assistant, a page's field), the screen
reader's confirmation or picked notice, a dictated draft, a confirmation, the assistant asking you to point, the
menu itself (its help, then out of a group back to its page, then closed), media up close, a face's media focus,
the reader, the world map, the feed's lines opened in full. With everything hidden (Tab), it brings the interface back
with the menu open. The assistant's cards stay (each has its ×). F2 still opens and closes the menu
(`Program.cs`, DeskEscape).
Voice chat (`docs/voice.md`): hold B or the middle mouse button to talk; in open mic, B mutes and unmutes.
With a screen reader running (`docs/screen-reader.md`), what arrives is read out through it, and Enter/Escape answer a
confirmation, Ctrl+N steps through waiting notices (their number keys answer, Delete puts one away), Ctrl+R says it
again, Ctrl+[ and Ctrl+] go back and forth through what was read, F1 says the keys; without one, no key changes.
Incognito (`docs/assistant.md` > Incognito): P turns it on and off (off by a hand on the keyboard only); while it's
on, a token with its mark (a hat and glasses) sits left of the mic token at the bottom middle, and a click on it ends
it. Hold Y and speak to dictate words for people: a draft on the panel, Enter sends it, Esc cancels.

The console beside the window prints what's said nearby and IMs; a typed line is said on channel 0 (`/shout ...`,
`/whisper ...`, `/quit`). In the world, chat shows on the feed and the assistant speaks for you (`docs/social.md`).

## The agent API

`http://127.0.0.1:9720/`, loopback only: Tessel's bridge HTTP API (`/status`, `/cmd {pump, data}`, `/listen`,
`/event`, `/events?since=&wait=`, `/rpc`) over Multitude's LEAP emulation, so `sl.py`-style tools and phase 3's
assistant drive this session (`src/Sojourn.Session/Bridge/BridgeServer.cs`; what each op does:
`projects/multitude/docs/api.md`): `TesselAgent move` walks and flies the avatar, `LLChatBar sendChat` speaks, and so
on. While an API move or autopilot runs and no key is held, the keys leave the controls
alone and the camera follows the body. Snapshot ops (`LLViewerWindow saveSnapshot`) save the next frame as the person
sees it (`Assistant/LiveSnapshots.cs`; never a moved camera). `LLAgent requestTeleport` with `skip_confirmation`
teleports at once, as the viewer's does, and waits for the arrival.

An op either does what it says or fails with a plain reason (`LeapError`): a success nobody acts on misleads every
caller, and the assistant tells the person it's done. Since the fork from headless residents, ops that can't work here
refuse: the camera (`LLAgent setCameraParams`; the view is the person's), the viewer's floaters, tools and UI calls,
Tessel's stream controls, `LLViewerControl` settings other than the draw distance and field of view, and in the headset
`lookAt` and `move`'s turning. Where the grid accepts or ignores a request without a word, the op watches for the outcome (sitting,
linking, taking or deleting objects, ejecting, a payment's reply) or checks the rights first (editing, group powers),
and says `requested` where it can do neither. `TesselAgent hideAvatars` hides people in the person's view;
`LLAppViewer requestQuit` closes Sojourn. `SojournSettings` (`Control/SettingsApi.cs`) is every setting the person can
change, for any caller (the user, 2026-10-25: the assistant can do pretty much anything the person can): `get` lists
them with values, choices and what each does, `set {name: value}` changes them on the main thread, saved as the menu's
would be, or refuses with the reason. The settings menu's rows (moving, assistant, voice, view), running, the view's
quality (draw distance, shadows, shadow resolution, reflections, detail, sky, the headset's resolution, the desktop's
field of view) and the desktop's keys (vsync, stereo, stats, first or third person). Refused as the person's own, being
safeguards on the assistant itself: whether its touches on others' things wait for confirmation, and the mic going live
(answering calls and confirmations aren't settings). A new setting goes in the menu and here together. On top: the
`SojournAssistant` pump (panel, pointer, inventory search), and
the confirmation gate: spending, deleting, giving, permission and new-script ops from any caller (the editor's `/rpc`
too) wait for the person's press on the panel, and every other op waits meanwhile (`docs/assistant.md`).

Loopback isn't only our processes: web pages in the person's browser can reach it. So the API refuses requests with an
`Origin` header (browsers), a `Host` other than 127.0.0.1 or localhost (DNS rebinding), and POSTs that aren't
`Content-Type: application/json` (403 / 415). Our tools send JSON and nothing else needs changing. Local programs can
still call it, and post clicks to the window (`tools/winmouse.ps1` does), so anything already running as the user can
drive the client; that's the trust boundary. The assistant itself now runs commands as the user, so a confirm press on
the desktop counts only from a hand on the mouse, not from posted or injected clicks (`docs/assistant.md` > What it
may do).

## How it fits together

- `Sojourn.Core`: Multitude's session core with one agent (`AgentRegistry`, `AgentSession`, the shared world model
  `WorldHub`/`RegionWorld`, LEAP APIs, appearance with client-side baking on OpenSim, inventory, building...).
  Dropped from the fork: the island's World-module senses, the Whisper sidecar, the spotlight API, render/world
  config. `HostConfig` is still the config type; the client builds one in code (state and caches in
  `local/client/`).
- `Sojourn.Render`: Multitude's Vulkan port of the viewer's deferred pipeline (`projects/multitude/docs/render.md`
  covers the pipeline, fidelity and the viewer ports). New here: `Realtime/FrameRenderer.cs` replaces the on-demand
  `RenderService` and the render wall; `VulkanContext` takes a `VulkanSetup` (window surface, swapchain, multiview
  feature, or an `IVulkanCreator` that makes the instance and device: the OpenXR runtime); `Renderer.Record` takes a
  list of eyes.
- `Sojourn.Session` (2026-10-29, for Sojourn Access: `docs/access.md`): one person's session for any app of ours, with
  no window, headset or drawn menus; it references Core and Render, never the client, OpenXR or Silk's windowing and
  input. `SessionHost.cs` starts it in steps (logs, the registry and agent, saved settings, the agent API, the
  assistant's host and sidecar, voice, world audio, incognito, people, land, the social wiring, capture and reports, the
  screen reader, SojournSettings); `StartAllAsync` runs them all for an app with no views of its own. What an app
  supplies comes in at the steps: whether it's in a headset, its settings menu (`Control/ISettingsMenu.cs`: each part's
  `Settle(menu?, api)` fills a section), its views' hooks (`Private/IDictation`, `Assistant/IShowing`,
  `Assistant/ISeenView`, notices back onto its feed), its window's handle for the screen reader, and a renderer if it
  has one. `RegionFeed.cs` gives a renderer the world's regions as they come and go; `Offscreen/` is the renderer for
  an app that shows no view (Access: pictures for the assistant, touches on faces, the collision model's rays;
  `SessionHost.StartOffscreen`). Folders keep the client's names; namespaces are `Sojourn.Session.*` (the client
  imports them all in `GlobalUsings.cs`), so a bare `Session` in `Sojourn.*` code names that namespace and OpenXR's is
  aliased in `Xr/`. `tools/probe.py` runs `src/Sojourn.SessionProbe`, a console host on the library alone: two log in,
  answer SojournSettings through their agent APIs and hear each other's chat.
- `Sojourn.Client`: `Program.cs` (window loop on the main thread; `HeadsetFrame` in the headset; the session host's
  steps in their order between the window's, headset's and menus' own), `Window/Presenter.cs` (swapchain as a frame
  target), `Window/MirrorTarget.cs` (headset plus window), `Control/Pilot.cs` (keys and mouse to AgentUpdate, the
  camera), `Control/VrPilot.cs` and `Xr/` (the headset: `docs/vr.md`), `Assistant/` (the panel's placement, the
  reader's view, pictures with the menus: `docs/assistant.md`), `Control/SettingsMenu.cs` and `SettingsStandard.cs`
  (the menu, and the client's own SojournSettings rows: moving, the assistant, voice, the renderer's quality),
  `Bench.cs`, and every drawn sheet (feed, name tags, cards, HUD board, map board, page boards, keyboard, login).

Threads (Claude Opus 5.5, 2026-10-27; `docs/performance.md` > Threads): the **main thread** runs the window, input,
the rig, panels, name tags, picks, voice/audio/media ticks and the headset's xrWaitFrame, and builds each frame's
`FrameRequest`; it posts the frame to the **render thread** (`Sojourn.Client/FramePipe.cs`), which renders it (and in
the headset begins and ends it) while the main thread goes on to the next. The world (scenes, avatars, everything a
pick reads) has one owner at a time, handed over in order: a posted frame owns it until its scene, probe plans and
capture lists are built (`FrameRenderer.WorldReleased`, before recording), then the main thread does until its next
post. So every frame sees the world as one thread would have left it; recording, submitting and presenting overlap the
next frame's simulation. What reads the world from outside (`Pick`, `CollideRay`, `DrawnPosition`, `Heads`,
`MediaFace`, `HudPick`...) checks it's its owner's turn and logs the first offender of each with its stack ("the world
read off its owner's turn"). `SOJOURN_RENDER_THREAD=0` renders on the main thread (the bench always does);
`SOJOURN_RENDER_STRESS=1` adds random delays at the hand-overs, for shaking out ordering assumptions. Within the frame,
the draw lists are built in parts by three **scene workers** with the render thread (`World/DrawPlan.cs`,
`SOJOURN_SCENE_THREADS=N`, 0 for none), and two **face resolvers** work out new and changed objects' faces off the
frame (`World/FaceResolver.cs`, `SOJOURN_FACE_THREADS`).

The frame (`FrameRenderer.RenderFrame`), on the render thread, two frames in flight:
1. Wait for the slot's fence (its arena, draw list and staging are then free), apply region changes and finished
   texture/mesh uploads.
2. The centre view (camera, environment, sky and water uniforms), then each eye: the centre moved and turned by the
   eye's pose in its eye space, with its own projection (`EyeSpec`; asymmetric tangents for headsets, and then a
   centre view wide enough for both).
3. One draw list for every eye: culled against one frustum holding every eye's, LODs and sort depth from the centre,
   shadow cascades fitted to the centre view and shared (each eye gets its own eye-to-shadow matrices). Avatars are
   posed first; then each region's lights, runs of its 16 m columns (about 384 objects a part) and its wearers'
   attachments are drawn as parts on the scene workers, each into a draw list segment with its own scratch
   (`World/DrawCtx.cs`), terrain, water and sky on the render thread, and the segments appended in order with their
   instance, material, palette and skinned-vertex indices moved along (`DrawList.Append`): the same draws in the same
   order as one thread made. Probe captures' lists are built the same way.
4. Reflection probes planned for the centre view. While probes in view are missing (and for refreshes, one per
   100 ms) one capture at a time is recorded inline before the scene, two of its twelve face renders a frame
   (`ProbeFacesPerFrame`; 0 takes captures whole, `ProbeCapturesPerFrame` a frame). The default probe is taken whole.
5. Record: skinning, captures, shadows once, then per eye the scene (G-buffer, sky, lighting, forward) and post
   (tonemap, CAS, glow, SMAA), each eye's output blitted into the swapchain image (`IFrameTarget`).
   After post, per eye: an assistant snapshot if one is waiting, then the overlay (`Realtime/Overlay.cs`: the
   desktop panel, the headset's pointer beam), then F12/`--shot-every` captures, so screenshots show the overlay and
   the assistant's pictures don't.
6. Submit (waiting on the acquire semaphore, signalling the present one), present.
In the headset a frame is split instead (`docs/vr.md` > The rig > Overlap): recorded before the target is acquired,
its scene submitted once the last frame has ended, then a second command buffer writes the swapchain images; its
present is the frame-end thread's.

Two eyes of one size render through a stereo target set: the forward and alpha passes draw both at once
(VK_KHR_multiview), the G-buffer, lighting and post an eye at a time (`docs/rendering.md` > Two eyes).

Persistent scene state: textures, meshes, prim geometry, probes and region scenes are kept across frames (as in
Multitude), and each object keeps its resolved faces and instance matrices (`RegionScene.FaceCache`, also while some of
its assets are on their way), so a frame's CPU work is culling, LOD and appending draws. A world object drawn in its own
region's frame keeps its faces' instances and materials on the GPU too (`Frame/PersistentTable.cs`: written when first
drawn, moved or changed, copied in at the start of the frame's commands; draws and instances name a slot with bit 30
set, `INSTANCE()`/`MATERIAL()` in `common.glsl`); attachments, rigged and animated-texture faces, neighbours' objects
(drawn moved by the regions' offset) and impostor captures still write theirs into the frame. An object whose faces need
resolving again is handed to the face resolvers and drawn as it was meanwhile (for at most two frames: a retired
texture slot is reused three on); a new one once they're done, near new ones at once. Objects are placed when the
region reports a change, not every frame (`RegionScene.Index.cs`): the region source queues the local ids of objects
added, updated, moved or removed, each frame applies them (re-placing a linkset's children with their root), world
objects sit in 16 m columns culled whole, lights, projectors and attachments (by wearer) are lists of their own, and the
probe scan and particle tracking follow the same reports. A few objects a frame are rechecked against the region's
dictionary in case a report was missed.

Movement: avatars are drawn at a smoothed copy of their position, dead-reckoned from the last update with its
velocity to the moment the frame is shown (`World/MotionSmoother.cs`, Stopwatch time; in the headset the predicted
display time); at a new update, what was drawn less the new trajectory decays critically damped (since 2026-10-28;
before, a 0.18 s blend), so position and speed stay continuous, and nothing is drawn running out over an edge with no
region beyond (`docs/vr.md` > The rig > Movement "server"). The desktop camera follows the own avatar's; in the headset a stick walk is
predicted locally (`docs/vr.md` > The rig). Moving prims (vehicles, keyframed motion, physical things, llTargetOmega
spin) are carried on between updates the same way, with their acceleration and angular velocity as the viewer's
idleUpdate does, and placed every frame while they move (`World/MotionSmoother.Prims.cs`, `RegionScene.Motion.cs`;
`docs/vr.md` > The rig > Moving things).

## Assets

Textures stream the viewer's way (`World/TextureStore.cs`, `World/AssetFetch.cs`, `Integration/HubAssetFetch.cs`):
every face asks for its texture with the screen area its object covers (scaled by the face's repeats; a tenth when it
only casts a shadow), and a scheduler every 20 ms gives the 8 fetch slots (the viewer's 8 connections to a host) to the
textures covering the most screen, those with nothing yet first. A fetch is an HTTP range of the region's ViewerAsset
capability: as many bytes as the wanted discard level needs (LLImageJ2C::calcDataSizeJ2C; the whole file for full
size; before the header is known, as if 1024 square), decoded at that level by OpenJPEG (`World/OpenJpeg.cs`, the
native `openjp2.dll` from `tools/openjpeg.py`; CoreJ2K if it's missing), refined when a view wants more. Textures nobody
asked for in 3 s aren't fetched, so leaving a region leaves its queue. What's fetched stays in the grid's cache folder:
whole files as LibreMetaverse keeps them (shared with its own downloads), prefixes under `partial/`. Failures back off
and are retried; a 404 is missing. Sculpt maps load whole through the same scheduler. Legacy bump maps go through it
too, made from their source's file at the level their face wants (as the viewer makes them from the diffuse as
loaded), after faces' first looks. Bakes load whole through LibreMetaverse.

Meshes download whole through a queue of their own (`World/Geometry.cs`): 8 at once, the asset whose objects cover the
most screen first, only assets wanted in the last 3 s, a failed download retried with backoff, over connections of
their own (`HubAssetSource.MeshAsync`: the region's mesh capability, from the cache folder first). Wants nobody asked
about for a minute are dropped with the shapes waiting on them (holders ask again). Mesh bytes are held only up to
256 MB, oldest out first, to build each LOD from; after that they're read from disk again. Not done: fetching only the
LODs in use (the viewer reads the header, then each LOD's byte range).

LibreMetaverse's own download queue (bakes, sounds, animations, anything through `WorldHub` asset calls) is one FIFO of
8 at once, never cancelled: nothing that a place asks for by the thousand goes through it any more (meshes and bump maps
did, 2026-10-27). The cache folder: LibreMetaverse prunes the whole files to 0.9 of 4 GB every 5 minutes (`WorldHub`
raises it from 1 GB, which an hour in SL went through several times over), and `HubAssetSource` keeps `partial/` under
2 GB. The view line's asset part also says the mesh queue's size and held bytes, meshes downloaded (KB/s, ms each),
LibreMetaverse's queue, and the process's memory.

Materials (`World/MaterialStore.cs`): a face knows whether its material is on its way or isn't coming (none, or the
fetch failed: retried after 30 s), so a missing one no longer counts as loading forever.

Video memory and loading speed are held by the load governor (`docs/performance.md` > The load governor): textures
shrink and evict to stay under the process's budget, and loading slows while frames run long.

## Logging in

Claude Opus 5.5, 2026-10-28 (batch R, `src/Sojourn.Client/Login/`). Two ways in:
- **The command line** (`--grid`, `--first`, `--last`, `--password(-file)`, `--start`; what `tools/client.py`,
  `dev.py` and every test use) logs straight in, as before.
- **The launcher** (`src/Sojourn.Launcher`, `docs/launcher.md`): the person's way in; it gathers the account and
  start and hands them over as a lost GPU's restart does (`--login --login-resume`, the remember choice with it), and
  reads the run's state from `SOJOURN_STATE_FILE` (`LaunchState.cs`).
- **The login screen** (`--login`; the options above are ignored): before any session, assistant, voice or agent API
  exists, the window and the headset show the void (`Render/Realtime/FrameRenderer.Void.cs`: a flat neutral grey with
  the overlay drawn over it, so sheets keep their frost and the rays show) and the login sheet, in the panel's look,
  centred on the desktop, in the settings menu's place in the headset (`PanelPlacement`, following the head, dragged
  by its body). Saved accounts to pick (the last used selected), + for a new one (username as on the website, first
  last or the one name; password; grid: Second Life, Beta, or other by login URI, its name asked from
  `get_grid_info` after the first login), start (last, home, or a region by name or SLurl, kept per account),
  remember password (off by default), forget (twice). Help behind ?, the version at the foot (TPV 1.g). Fields take
  the PC's keyboard (Tab, Enter, Esc quits, Ctrl+V) or, in the headset, a press on a field brings the headset
  keyboard for it. Either hand's ray and trigger press.

The account is chosen before the session exists because everything kept per account lives in its state dir:
`<base>/state/<grid>/<first.last>/` (grid: `agni`, `aditi` or `host_port`), the cache per grid under
`<base>/cache/<grid>`; the settings (`vr-settings.json`) stay the base dir's, the person's for every account. So
another account is another run: Back, Cancel or the menu's log out ("you" section, at the foot, twice) shut the
client down as quitting does and start it again at the login screen (`LoginRestart`), the account shown selected.
For the person's own use, `--base-dir local/main` keeps their headset settings; the first login there asks for MFA
once (the old `state/pilot` isn't carried over).

After Log in, the sheet stays in the panel's place until the first arrival, for command-line logins too: progress;
the grid's Terms of Service or critical message (paged; Continue only once "I agree" is on, as the viewer's floater);
the MFA code with "remember this computer" (off unless switched on: the mfa_hash is kept only then, a stale one
deleted; `AgentSession.KeepMfaHash`, true for the console's `/mfa`); failures in plain words (`LoginWords`: the
grid's reason key as one sentence, its own message beneath when it says more), a password field again after a wrong
one, Back and Try again. A grid asking the ToS or a code on a later reconnect brings the sheet back. The console's
`/accept`, `/decline` and `/mfa` still work.

Credentials: the password never becomes a string. It's hashed from the field's buffer as the viewers send it ("$1$"
+ MD5, as LibreMetaverse does for a plain one) and the buffer cleared; the hash is the session's config, in memory.
It's kept only with remember on and only after a login that worked, under DPAPI for this Windows user, in
`local/secrets/logins/accounts.json` when run from the repo (else `%LOCALAPPDATA%\Sojourn\secrets\logins`); the
usernames are there too (TPV 2.e). Both folders are named in the assistant's deny rules and its shell hook
(`assistant/harness.py`). The grid's questions are answered only by real presses: a hand on the mouse
(`RealInput`), Enter from a hand on the keyboard, or a controller; their answer keys arm 0.8 s after the sheet
appears. The agent API has nothing that touches any of it.

Testing: `SOJOURN_LOGINS=DIR` keeps saved logins elsewhere; `SOJOURN_FAKE_LOGIN_ASK=tos|critical|mfa` makes the
first login answer as a grid asking that would (once a process, without asking the grid; OpenSim never asks), and
`SOJOURN_TEST_INPUT=1` lets posted clicks answer; `--shot-every S` also pictures the login screen;
`--login-shots DIR` draws every stage in every preset look without a GPU.

## When the GPU is lost

2026-10-27, `DeviceLoss.cs`. The GPU's driver resets under long heavy load (the studio's `docs/gpu-resets.md`), which
the client sees as `ErrorDeviceLost`. A device loss anywhere in the frame loop ends it:
- The console says so; the client logs out (6 s at most), stops the assistant, voice, world sound, media helpers and the
  agent API, and starts the same program again with the same arguments, working directory, console and environment,
  `--start` replaced by where the person was (`uri:Region&x&y&z`; kept out of the log while incognito), then exits with
  code 75. The new run reads `restarted.json` in the base dir and, once in world, says on the assistant's panel that it
  restarted after the graphics card was reset. Three losses within ten minutes (`SOJOURN_RESTARTS`) and it stops
  restarting and says so.
- The loss can't always reach the frame loop: a render-thread failure is thrown on the main thread only at its next
  hand-over (`FramePipe`), and after a reset Meta's runtime has held the main thread in `xrWaitFrame` for 28 s before
  failing it (twice on 2026-10-31, the headset frozen meanwhile). So a watch thread (`DeviceLoss.Guard`, 2026-10-31)
  runs the same restart from outside the loop, on definite signals only:
  1. the render thread failed with a lost device (`VK_ERROR_DEVICE_LOST`, or the runtime's `ErrorRuntimeFailure`,
     `ErrorSessionLost`, `ErrorInstanceLost`) and the loop hasn't come for it within 2 s;
  2. it failed with anything else, the loop hasn't come for it within 2 s, and the System log has a display driver
     reset (nvlddmkm 153 or Display 4101) from the last two minutes;
  3. the loop hasn't turned for 10 s and the System log has a driver reset from 30 s before it stopped on.

  A slow frame, a load hitch, a debugger pause or the headset taken off logs no driver reset, so a timer alone never
  restarts. The watch logs the render thread's failure when the loop doesn't come for it (the reason was lost before).
  Only the main frame loop is watched, not the login screen's.
- A run from the login screen logs the same account in again without the form: the password hash goes to the next run
  in a DPAPI-protected `resume.dat` in the saved logins' folder, read and deleted at its start, ignored after two
  minutes (`--login-resume`). The saved MFA hash normally spares a second MFA question.
- In the headset, once the next run is started, the old one destroys its OpenXR session and instance (`XrSession.LetGo`,
  5 s at most) and the new one waits for it to exit (2026-10-31): left to the exit, Meta's runtime noticed the dead
  session ~11 s later and moved the new run to Synchronized for good (the headset black). As a fallback, a restarted run
  put in Synchronized within two minutes of first showing and left there 10 s starts again once more (`DeviceLoss.Watch`;
  it counts toward the three).
- Why a new process rather than a new device: the GPU state is spread through the world's caches (textures, geometry,
  face caches holding texture slots, avatars, impostors, probes, media surfaces, the overlay) and the OpenXR session is
  bound to the device; a new process rebuilds all of it by construction and the disk cache brings the place back fast.
  The renderer out of sight (Session's `Offscreen/`, Sojourn Access) restarts within its process instead: nothing
  outside it holds its GPU state.
- `SOJOURN_FAKE_DEVICE_LOST=S` throws the same exception S seconds after the first region is drawn (not passed on to the
  restarted run), for testing without resetting the driver; with `SOJOURN_FAKE_HELD=1` too, the failure reaches the
  frame loop a minute late, as when `xrWaitFrame` held it, so the watch's restart (rule 1) can be seen.

## The local grid

`local/grid/` (gitignored) is a copy of Multitude's phase 2 render grid (`projects/multitude/local/grid-p2`) moved
to Sojourn's ports: region Atelier on 9700 (UDP 9700), Atelier North/East/Northeast on 9701-9703, private port 9709;
REST console admin/localdev. Account: Sojourn Pilot / sojourn-local. `tools/grid.py` starts it (below-normal
priority, .NET 8 runtime), makes accounts, sends console commands and swaps scenes:

- `light`: Multitude's Atelier test scene as built (`projects/multitude/docs/render.md`): 116 prims in 50 objects:
  a plaza and pavilion, sculpts and meshes, PBR and legacy materials, alpha and alpha masks, local and projector
  lights, Linden trees and grass, a pond, terrain textures, water, the default day.
- `dense`: the same plus 20 copies of it (`save oar` / `load oar --merge --displacement`), about 2400 prims over the
  four regions, so a view holds 1400-2000 instances and 3-6k draws.
- A second copy, `local/perf-grid` (ports 9740-9743 and 9749, `SOJOURN_GRID=perf-grid`), holds the synthetic forest
  (`tools/forest.py`, 27.8k objects and 8.9k textures; `docs/performance.md` > How to measure), so performance work can
  restart and swap scenes without disturbing the usual grid.

Ports in use: grid 9700-9703 and 9709, agent API 9720, voice tests' clients 9730-9731, audio's test client 9732 and
test radio 9751, the screen reader's test client 9877, the session probes 9760-9761, the local voice server 14223, 14225 and UDP 10000-10100, voice's peer connections UDP 60000-60100. Multitude has 9360-9380, 9450, 8380, 10350-10375 and the
lab 8300-8899; Tessel 9063, 9070, 9020, 9080.

## Gotchas

- The Bash tool turns backslash escapes into control characters, and a Python heredoc containing `'''` broke the
  shell's parsing once; for code patches, write the script to the scratchpad with the Write tool and run it.
- OpenSim's `load oar --merge` skips objects whose UUIDs already exist in the region; to copy a region's objects
  into itself, load an OAR saved from another region.
- What OpenSim sends depends on the avatar's position and draw distance (the interest list), so object counts
  differ a little between runs, and objects in the neighbours arrive later than the region's own.
- Neighbouring regions: LibreMetaverse 3.2.0 raises each event-queue event on a task of its own, so a neighbour's
  EnableSimulator and EstablishAgentCommunication (sent together) were handled at once; when each missed the other's
  Simulator, both connected: two circuits for one region, OpenSim sent DisableSimulator to both, and the neighbour was
  gone for the session (2 logins in 20). `Agents/ChildCapsGuard.cs` unregisters both handlers (by reflection on their
  method names: recheck on a LibreMetaverse upgrade; it logs a warning if they're gone) and connects each neighbour
  once under a lock, setting its seed when the circuit is up.
- New circuits: LibreMetaverse 3.2.0 sends a region's UseCircuitCode once and resends nothing until the region's
  handshake, so one lost datagram left a teleport's arrival waiting 60 s and never arriving (the main grid's way out,
  2026-10-28). `Agents/CircuitGuard.cs` sends it again every 1.5 s until acknowledged (by reflection on its private
  acknowledgement flag: recheck on an upgrade). `SOJOURN_CIRCUIT_LOSS=PORT:MS` loses a new circuit's datagrams to that
  port for MS ms, for testing; `SOJOURN_CIRCUIT_GUARD=0` for comparing.
- Teleport outcomes: LibreMetaverse drops a TeleportFailed that comes before any TeleportStart (how refusals come) and
  reports arriving only once the new circuit is up. `Agents/TeleportWatch.cs` reads the raw messages (UDP, and the event
  queue through its catch-all handler, which runs apart from LibreMetaverse's blocking TeleportFinish handler): every
  step in the console (`teleport: ...`), every refusal noticed in plain words, `GoAsync` for awaiting a request, and an
  arrival that hasn't come 30 s after the grid's TeleportFinish makes the session log in again. `Arriving` says where
  a teleport is taking the agent until that region is current and its AgentMovementComplete has come (the headset holds
  the view meanwhile: `docs/vr.md` > The rig). LibreMetaverse's handler for that reply moves Client.Self to the
  position it carries, where the region placed the agent as it took it in; the avatar's own updates only start after
  it, so when one has come first (the reply lost and resent) it's the newer word: `WireReply` replaces the handler
  (by reflection, as above) and puts that position into the reply before LibreMetaverse applies it (`teleport: the
  arrival reply came after the region's own updates; kept theirs`). Setting it back afterwards left a moment in which
  the old one was read. `SOJOURN_ARRIVAL_DELAY_MS` holds every reply back that long, for testing.
- The agent's own position: LibreMetaverse 3.2.0 moves Client.Self by any terse update carrying the agent's local id,
  from any region, though local ids are only unique within one (SL's simulators number their own; OpenSim starts each
  region's at a random base, so on the local grid they practically never meet), so a moving object in a neighbour, or
  in the old region after a teleport or crossing, could move the agent there (and the headset's view with it, in a cut,
  and back); after a teleport the id stays the old region's until the new region's full update for the avatar, so an
  object in the new region with that id did the same. `Agents/SelfUpdateGuard.cs` replaces its handler (by reflection
  on its private handler, event field and helpers: recheck on an upgrade; it warns and leaves LibreMetaverse's in place
  if they're gone): an update is the agent's only from the current region where that region has the agent's avatar
  under that id; anything else updates its own object as LibreMetaverse would, never the agent, and the console says
  so once per region and id (`self update guard: ...`). It also notes the last update that moved the agent (the arrival
  reply above). `SOJOURN_SELF_GUARD=0` for comparing; `SOJOURN_SELF_ID_COLLIDE=FIRST-LAST` gives terse updates for
  those ids from any region but the current one the agent's id, so the case can be made on the local grid (a falling
  prim in a neighbour: without the guard the agent and the view followed it down).
- Region crossings: LibreMetaverse 3.2.0 undoes a crossing whose arrival reply comes before it listens (30 s later it
  moves the agent back into the old region) and leaves the new circuit without its handshake flag, which stops every
  AgentUpdate. Before either, its Connect resends UseCircuitCode on the circuit it already has and blocks up to 60 s
  for the acknowledgement before CompleteAgentMovement goes out (the viewer sends only CompleteAgentMovement): on the
  main grid a region didn't acknowledge it while the blind tester rode a train over its border (2026-11-01), so the
  region never took her in, the train's script stopped for want of a driver and the session lost its region. And it
  ignores a crossing that comes while the last is unfinished (a vehicle over two borders in seconds).
  `Agents/CrossingGuard.cs` finishes the crossing, restores the flag, lets the acknowledgement wait go at once for a
  circuit already up (crossings, and teleports into a connected neighbour), ends a stale crossing for a newer one, and
  says a crossing that fails (notice `CrossingFailed`). All by reflection on private members: recheck them on a
  LibreMetaverse upgrade; the guard logs "crossing into ..." when it acts. `SOJOURN_CROSSING_GUARD=0` turns the last
  three off; with it, `SOJOURN_CROSSING_NOACK=MS` holds each crossing MS ms as the wait did. OpenSim takes a late
  CompleteAgentMovement 40 s on and keeps the rider seated; on Second Life the train found itself without a driver
  about 10 s in.
  `python tools/crossing.py [--sound] [--noack MS]`: a seated crossing in the visitor's kart through a console session
  (no GPU, no window): in Atelier East, still seated, connected, and with `--sound` the kart's loop heard from the kart.
- What a crossing leaves behind: old regions often never kill what crossed out (OpenSim crosses a ridden vehicle
  silently), and each region's store keeps its copy. Once the current region has the avatar and no crossing is under
  way, `WorldHub.SelfIn` drops the avatar's other copies with what they wear, other regions' copies of its attachments
  (also ones that came again without their avatar as parent) and of the linkset it sits on (`own avatar in X: dropped
  what was left in Y, ...`). Nothing else is judged by region: an object that left the current region for a neighbour
  may itself be the stale copy. An attachment without a wearer is never listed (`ObjectViews.Listable`).
- Losing the region: the session's watchdog logs in again after 30 s out of every region; first it sends a logout
  through whatever circuit still answers (up to 4 s), so the grid doesn't refuse the new login as still logged in.
  The drop is a notice (`RegionLost`, or `Disconnected` with the grid's words), so a person hears that the session is
  mending itself.
- Crossing at speed: the new region's first full update for the agent says it stands still at the handover point
  (OpenSim gives the avatar its momentum back a physics step after CompleteAgentMovement), and LibreMetaverse writes
  the CrossedRegion position into Client.Self after its Network.Connect returns (at once now that CrossingGuard lets
  its acknowledgement wait go; it lands before anything newer). The headset's
  rig takes both as the region's lurches, not motion (`docs/vr.md` > The rig > Crossing at speed).
  `SOJOURN_SELF_TRACE=1` (`Agents/SelfTrace.cs`) logs every word on the agent's own position as it arrives
  (CrossedRegion, AgentMovementComplete, full and terse updates from any region, the current region changing, and a
  2 ms poll of Client.Self), `self trace +MS: ...` in the console: how this was found.
