# Sojourn

*Published as it is: this is the agent-facing index the Claude models working on Sojourn read first. Some things it
points to stay private and aren't in this repository: the sittings' notes (`docs/session-*.md`), the board, `local/`,
and the other Studio Quire projects under `projects/`. For people, start with README.md.*

Working name. Our own Second Life client, built for VR first, with a personal AI that operates the client for you by voice: whatever is awkward in a headset (inventory, outfits, search, typing, building, dialogs, settings). It's an accessibility tool for a person who is present and in charge, not an autonomous resident like Tessel. It works on the desktop too. Started 2026-10-20 (Claude Opus 5.5, with the user).

## Decisions

- **Build our own** (user, 2026-10-20): no VR mods (P373R's Firestorm VR Mod), no injectors (VorpX), no official viewer + patches. The reason given: we can probably do it better.
- **Accessibility tool** (user, 2026-10-20): the assistant acts for a human who is logged in and directing it. That makes it different from Tessel, so this account doesn't carry Tessel's scripted-agent setup.
- **Both halves** (user, 2026-10-20): a VR-native client and the assistant, as one project.
- **Base: a fork of Multitude's client core and renderer** (Claude Opus 5.5, 2026-10-20). That's .NET 10 on the LibreMetaverse protocol library, plus a Vulkan port of the viewer's deferred pipeline (`projects/multitude/src/Multitude.Core`, `Multitude.Render`; ~37k lines). Why:
  - Vulkan + OpenXR with multiview is the natural VR stack. The official viewer is OpenGL and mostly single-threaded, and frame rate is the hardest problem in SL VR.
  - With the assistant as the main interface, the official viewer's 2D floaters (most of its code) aren't needed.
  - Multitude already serves the viewer's LEAP APIs and Tessel's bridge HTTP API per agent (`projects/multitude/docs/api.md`), so the assistant's control surface mostly exists already.
  - We don't rewrite the wire protocol: LibreMetaverse is plumbing.
- **The assistant's shape** (Claude Opus 5.5, 2026-10-21): the Agent SDK in a Python sidecar on the person's own provider (their Claude account by default, an Anthropic API key, or an OpenRouter key and model, since 2026-10-29: `docs/assistant.md`; keys DPAPI-encrypted, added by a local proxy so Claude Code never holds them) (Whisper is Python too); who takes a request first is the person's setting (user, 2026-10-21: sometimes they'd rather wait for Opus and be sure): quick (Haiku 5.5, the default), sonnet (Sonnet 5.5), each handing multi-step jobs to Opus 5.5, or opus (Opus for everything); each model's effort is a setting too (low to max, medium for all by default, user 2026-10-27; switches live); no settings files or transcripts. Confirmations live in the op dispatcher so they hold for any caller. The panel is an OpenXR quad layer in the headset.
- **The assistant's powers** (user, 2026-10-25): about as capable as Tessel at interaction, search and making things, guarded by permissions and Claude Code's auto mode rather than a stripped-down tool set. So its sessions have Claude Code's own tools (files, shell, web, helpers) in auto mode, with deny rules and hard rules for what must never happen (credentials, the person's confirmations, Sojourn's code and settings, recording others' conversations, acting in SL on its own); a memory kept as files in its own folder (an index loaded each request, a note per topic); long work such as builds as background jobs the person can watch, stop and resume. What's allowed and denied, and why: `docs/assistant.md` > What it may do.
- **Jobs are the assistant's** (user, 2026-10-25): one at a time and unbounded (no time limit, no stall watchdog; it stays on the panel and the person or the assistant can stop it). The assistant decides whether to start one, its model, its brief, whether it uses helpers, and when to check on, change, stop or resume it; the harness keeps only the mechanics (outliving the request, surviving restarts from its notes, the panel's line, one at a time, the same permissions and deny rules). Plan usage isn't to be economised, so no caps for that. A job the person asked for may keep acting until it's done or stopped; nothing starts acting on its own. `docs/assistant.md` > Jobs and helpers.
- **The person's choices about the assistant** (user, 2026-10-25): replies on the panel by default, spoken (a local voice) as an option; no name and a plain character by default, both optional and written into the prompt as what the person would like ("they'd like you to use the name X"), never "you are X"; the panel and settings menu in a glassy look by default, which the assistant can change when asked; the assistant's touches and menu answers on other people's objects wait for confirmation by default, with a toggle; voice chat built, push-to-talk by default, open mic with a mute toggle as the option.
- **The desktop gets its own UI, later** (user, 2026-10-27): if Sojourn keeps a desktop version, its interface should be quite different from the headset's (today it reuses the headset's panel, feed and menus). To be workshopped with the user once VR is good; until then the desktop is mainly for testing. The first release is VR-first: the desktop a fallback, recommended against, its look still iffy; released as soon as VR is ready rather than waiting on the desktop (user, 2026-10-29).
- **Under lag, far things get cheaper; see-through surfaces optional** (user, 2026-10-27): the load governor may end
  shadows sooner far away and make avatars beyond 20-48 m impostors, gradually (`docs/performance.md` > The load
  governor); simpler lighting for blended faces and a cap on their layers are settings, both off by default (the first
  for the user's eye, the second because it costs more than it saves in our scenes; `docs/rendering.md`).
- **A full viewer for release** (user, 2026-10-27): Sojourn is to be released as a full VR viewer, so every feature
  other viewers have that we lack gets closed, not only the valuable ones (`docs/viewer-gaps.md`). The release doesn't
  wait for every last gap, only most of them, and is planned relatively soon (user, 2026-10-29). Free by default and open source as
  licences permit; paid extras are possible later, such as credits so the assistant works without the person's own
  API key (user, 2026-10-29). Sojourn itself is a VR viewer without an accessibility lean; accessibility is Sojourn
  Access's purpose. The menu is simple by
  default (only what a person would change by hand rather than ask; draw distance stays), an `advanced` switch
  shows everything for experienced SL players, and every row has a tip on hover (the user, 2026-10-29;
  `Control/MenuGuide.cs`).
- **Usable without sight too** (user, 2026-10-27, after Tessel met a blind resident who
  uses Radegast with NVDA and wished for a full viewer with its accessibility): things like that are to be part of the
  project. An audio-first way to use Sojourn, the assistant at its centre (it already hears, speaks and acts).
  **A separate app in the same codebase** (user, 2026-10-27): its own app project beside the VR client (working name
  Sojourn Access), with its own accessible window and keyboard model, sharing Core, the assistant, hearing, audio and
  the renderer (kept running out of sight for descriptions and the collision model); not a fork, not a mode of the VR
  build, possibly shipped under its own name. Separate too from the sighted desktop UI still to be workshopped. Directed by
  Claude models, and fully usable without a Claude account
  (user, 2026-10-29). Plan: `docs/access.md`.
- **Comfort aids are options; defaults suit experienced VR players** (user, 2026-10-27; 2026-10-28: don't add standard
  comfort aids without asking, most serious VR players don't use them; so view moves are straight cuts, no black blinks): no vignette unless turned on;
  antispam off by default as in Firestorm. (Movelock and bump notices: no preference; kept as built, docs/safety.md.)
- **Hearing** (user, 2026-10-27): local by default (Whisper with the names around the person as a prompt; Parakeet
  selectable); a cloud transcription service as an opt-in, our pick of the best. If it neither keeps nor trains on the
  audio, it may also run in incognito and receive the names hints; otherwise local-only there.
- **Menus belong to the world** (user, 2026-10-28): in the headset, walls and things in front hide the menus (the
  panel, feed, cards) by default, as Meta's compositor does with our depth; menus always over the world is an option.
- **Open source as one repo plus Access's front door** (user agreed, 2026-10-29): the source goes public as
  `studio-quire/sojourn` (LGPL-2.1-only, fresh history from a scrubbed snapshot; this repo's history stays private),
  because the two apps share most of their code; Sojourn Access gets a code-free front door, `studio-quire/sojourn-access`
  (a README for screen-reader users, downloads as releases, its issues). How: Publishing, below.
- **Sojourn and Sojourn Access ship separately** (user, 2026-10-29): no one uses both, so each has its own package and
  releases; the code stays in one repo.
- **The assistant's provider is the launcher's** (user, 2026-10-29): the provider, keys, OpenRouter's data collection and
  auto mode are chosen in the launcher (Sojourn Access: its Alt+0); the viewer's menu shows only that provider's model
  choices. `docs/launcher.md`, `docs/assistant.md` > Switching it on.
- **Fork, not shared** (same): Multitude's renderer renders on demand and offscreen for many agents, whereas this one renders one person at 72-90 Hz in stereo. Their needs diverge. If both stay alive and the shared parts stay alike, extracting a common library is an option later.
- **Incognito** (user, 2026-10-27; private mode renamed): nothing from a spell is kept; blind to specifics by default, "let it see" (off by default) lets the assistant help as usual, adult content included when needed, never making NSFW content, and no text assumes that use; on by the menu, wrist, P or the assistant asked, off only by the person's own controls; no trigger words; the PC's mirror blanks; no automatic start by region maturity. `docs/assistant.md` > Incognito.

## Plan

Phases are a guide; reorder when something teaches us otherwise.
1. **Desktop real-time client.** Fork Core + Render. Log one human in. Render continuously to a window with camera and avatar controls and local chat. Measure frame time on real-looking scenes, then optimise toward a VR budget: persistent scene state, not per-snapshot setup.
2. **OpenXR.** Vulkan OpenXR, head tracking, controllers for locomotion and turning, comfort options; then multiview. The user runs a Quest 3 as PCVR over Link. Target 72 Hz first, 90 Hz later. Content limits FPS in busy places, so plan for avatar impostors, LOD and draw-distance budgets.
3. **The assistant** (built: `docs/assistant.md`). Push-to-talk and Whisper, Claude sessions with tools over the agent API, a pointer for "that", one panel, confirmations enforced in the client. Since then: the person's choices (spoken replies, a name and character, the look); Claude Code's tools under auto mode, memory, web search, background jobs and builds (2026-10-25). SL's own search (places, events, classifieds) and abuse reports (2026-10-26).
4. **Embodiment** (first pass built: `docs/vr.md` > Your own body). Your own body under your head, its arms on the controllers, drawn only for you. Others see the avatar's animations and, since 2026-10-26, its head turning where you look (the viewer's look-at, eye contact by default, a `gaze` setting; `docs/vr.md` > Where you look): the user's choice from `docs/shared-tracking.md` (Puppetry is discontinued; pose animations and scripted arms are parked, point-at decided against for now). Mirrors show you whole (`docs/rendering.md`). Next: leaning and crouching, finger poses.

## Where it stands

- **Phase 1 runs** (2026-10-20): the desktop client logs one person in to the local grid and renders continuously in a window (mono, or two eyes side by side), with walk/run/fly, mouse look, first/third person, console chat and the agent API on :9720. How to run it, controls and how it's built: `docs/client.md`.
- **Phase 2 runs in the Quest 3** (first session 2026-10-21): `python tools/client.py vr` runs it over Link (OpenXR through XR_KHR_vulkan_enable2, Meta's runtime is the active one), the eyes mirrored to the window. Head tracking, Touch controls (stick walking head- or hand-relative, smooth turning at 180 deg/s or snap, jump, fly, run), the room placed in the region so head tracking never waits on the network, the avatar stepping after you when you walk in your room. A settings menu on the left menu button (standing or seated, your eye height or the avatar's, turning, your body, the assistant's panel and model), saved in `local/client/vr-settings.json`. The eyes' size follows the GPU to hold the frame rate. Region crossings hold (a LibreMetaverse bug undid them; `docs/client.md` > Gotchas). Without a headset it carries on on the desktop. Tested against Meta's XR Simulator (`vr-sim`) with a scripted routine: `docs/vr.md`. What's new to try is on the board.
- **Measured** (`docs/performance.md`, `docs/vr.md`): two Quest 3 eyes (2064x2208, the Quest's asymmetric frusta) of a dense test scene take 7.4-9.7 ms of GPU with the shadow cap; CPU 1-2 ms. In the simulator the dense scene holds 72/72 fps once loaded; the first ~15 s after arriving drop ~10% of frames there (its compositor runs in series with us). Probe captures now run two faces a frame and terrain builds off the render thread. The deferred lighting chain is half the GPU frame; the ordered plan to ~11 ms at 90 Hz is in performance.md.
- **On Second Life itself** (first main-grid session 2026-10-25, Bay City and Calas Galadhon in the headset; `local/main/` holds its state and cache): it ran at 45 fps or less, CPU-bound (scene 12-33 ms a frame), downloads crawled and the forest's vegetation never loaded. Since (`docs/performance.md` > Busy places): an object index updated from the region's change reports (no more walking every object several times a frame), textures streamed by byte range for their size on screen, largest first, meshes in a prioritised queue with retries, OpenJPEG decoding, a predicted stick walk and blended avatar motion in the headset. On a synthetic SL-like forest (`tools/forest.py`, a second grid copy) in the XR Simulator: CPU 39-65 ms down to 17-24, everything loaded in 30 s instead of thousands of assets still waiting after 2.5 minutes, the camera starting a walk after one frame instead of 139 ms. After the second session (2026-10-25: the view and walking jittery, 12-15 fps in a crowd): the headset's view takes no server corrections, the avatar is walked to you and you're put back on it by a jump (a straight cut, no black: the user, 2026-10-28) or a slide only when it can't be (the user's direction, `docs/vr.md` > The rig, measured by `tools/jitter.py`), late frames carry depth for positional reprojection, and a scene budget, time budgets for resolving and uploads and cached worn objects hold the frame rate while a place fills in (`docs/performance.md` > Busy places). Every setting the person can change is on the agent API (`SojournSettings`) and the assistant's `settings` tool, the view's graphics settings in the menu; every session's console goes to `logs/` in its base dir.
- **The person's choices** (2026-10-25): the settings menu has sections (moving, assistant, voice), F2 on the desktop. Spoken replies (Kokoro, `assistant/voice.py`), name and character, the touch confirmation: `docs/assistant.md`.
- **The panel and menu, redesigned** (2026-10-25): words only where the person needs to read them (no state names, instructions or footers; help behind a ? key), switches, segments and swatches in the menu; a look held as data (`Assistant/PanelLook.cs`: light glass by default, pearl, silver, smoke; materials glass, pearl, metal; assistant-editable under a legibility rule; frost from the renderer). In the headset it rests low in view (top edge 18 deg below the eyes), 1.1 m away, following lazily, or stays wherever the person drags it with the trigger (dropped back on its outline, it follows again); angular sizes (`Assistant/PanelPlacement.cs`). `--panel-shots` renders it all. `docs/assistant.md` > The panel.
- **Other people on screen** (2026-10-25, after the user's second main-grid session; information only, everything they do still goes through the assistant): name tags over people (display name, the username near or looked at; everyone within chat range, beyond that whoever you look at or who's speaking, typing or just spoke; a speaking light and typing dots), and a feed of nearby chat, IMs, group chat and notices (offers, script menus, friends online), bottom left on the desktop and on its own quad layer low on the left in the headset. Names and chat switches in the view section. `docs/social.md`.
- **Closing the viewer gaps** (2026-10-25, the user's approved list from `docs/viewer-gaps.md`; tested on the local grid
  and in the XR Simulator): touches land where you point (face, surface and texture coordinates, normal, binormal, as
  the viewer's click; the assistant's at the point the person last pointed at); the person's own touch and sit with the
  pointer's trigger (the right hand, since 2026-10-26) or a left click in the world (sit objects seat you, touchable ones are touched, the left button for
  scripts; never while a confirmation or the menu is up); seated or with controls taken, the sticks are the movement
  keys and the view turns with the seat; objects' floating text; the maturity preference (`maturity` setting, the
  account's own); experience prompts through the notifications and the confirmation gate; parcels' own skies.
  `docs/vr.md` > Controls, `docs/social.md`, `docs/assistant.md` > Confirmations.
- **The assistant shows things** (2026-10-25): cards and labels it puts up to show the person information (timers,
  lists and checklists, tables, charts, pictures, arrows toward places, small maps, labels over people and things), with
  them (in view beside the panel, at the wrist, in the room) or out in the world; drawn in the panel's look with its
  spark, never pressable but for a close key, hidden while a confirmation waits; no caps on how much (the user, 2026-10-28).
  `docs/assistant.md` > Showing things.
- **Phase 3 built: the assistant** (2026-10-21): hold T (desktop) or the right grip (headset) and speak, or press Enter and type; Claude acts through the agent API and one panel shows what it heard, what it's doing, the result, and choices or confirmations to press. Local Whisper on the CPU; an assistant-model setting (settings menu, F8, `/model`, `--assistant-model`; switches live): quick (Haiku first, Opus for multi-step jobs), sonnet, or opus for everything; the panel shows which model is working. The pointer (mouse, or right-controller ray) gives "that" as an id. Spending, deleting, giving, permission changes and new scripts wait for the person's own press, enforced in the client for every caller. Spoken one-tool requests act ~2.5-4 s after the end of speech. X in the headset (Tab on the desktop) keeps the panel out of sight; confirmations still show. Used in the headset over Link on 2026-10-21. `docs/assistant.md`.
- **The assistant's powers** (2026-10-25, checked on the local grid): Claude Code's tools in auto mode under `assistant/harness.py`'s deny rules and hooks (`tools/assistant_guard.py`: 16 of 16 forbidden things refused, decoys for credentials); a desktop confirm takes only a hand on the mouse, not posted or injected clicks; memory as files in `local/assistant/home` (index loaded each request, notes per topic, forget, a menu switch and clear); web search; background jobs the assistant starts, briefs (model, effort, helpers) and steers (tell, stop, resume from their notes; one at a time, a queue after it, no time limit), with a progress line on the panel and the plan as a checklist (2026-10-26); `build` makes a linked object from a spec. Built a 29-prim dining set (36 s), a cabin with a scripted door and a generated floor texture (139 s), and a 54-prim watchtower resumed after a restart.
- **Voice chat built** (2026-10-25, tested on the local grid): SL's WebRTC voice through our own shim over LL's libwebrtc m144 (`src/Sojourn.Voice.Native`) and C# (`Sojourn.Session/Voice/`): nearby voice with neighbouring regions, parcel channels, calls, push-to-talk by default (left grip / B) or open mic with a mute (right stick click / B), the head as the listener in the headset, dots over speakers and a mic token at your left wrist (live, muted, off), a voice section in the settings menu. One capture and one echo canceller serve voice and the assistant: holding the assistant's button keeps the mic from residents, and its spoken replies play in voice's mix. The local grid's voice server is legion-voice-mixer in Docker (`tools/grid.py voice up`). `docs/voice.md`.
- **World audio built** (2026-10-25, tested on the local grid): objects' sounds (play, loop, trigger, stop, queue, sync, gain, cutoff), gestures', collisions', footsteps, typing and UI sounds placed around you by a spherical-head model, following the head in the headset, the viewer's rules (30 at once by priority, parcels' sounds kept inside, the block list), and the parcel's music stream (Shoutcast and Icecast; MP3, AAC, Ogg; the song's title). Through voice's engine, so it never reaches the mic. Settings menu > sound (volume, sounds, music). `docs/audio.md`.
- **HUD attachments** (2026-10-25, tested on the local grid and in the XR Simulator): drawn as the viewer draws them
  on the desktop (its HUD space, camera and fullbright shading; a click on one is the HUD's; Alt+Shift+H); in the headset
  on a board called up from a token at the left wrist (16:9 in the viewer's layout, 60 degrees wide, its own quad layer,
  staying in the room, dragged by the right trigger, × to put away), pressed by either hand's trigger with the viewer's
  surface data; the assistant sees them as pictures and presses by point, part or name (`SojournHud`, the hud tool).
  `docs/hud.md`.
- **Media** (2026-10-26, tested on the local grid and in the XR Simulator): web pages (CEF in Chromium's sandbox) and
  video (libVLC at low integrity) on faces and the parcel's, in helper processes, their sound placed in world audio's
  mix; the person's own media plays by itself, others' waits for a touch (the default); the mouse and the left
  trigger click, scroll and press on pages; up close on a sheet or a board; offered pages (llLoadURL) open after a
  confirmed "Go to page"; the assistant reads, presses, types and navigates pages (`SojournMedia`, the media tool),
  asking first on others'. `docs/media.md`.
- **Loose ends closed** (2026-10-26, tested on the local grid and in the XR Simulator): floating text and media on HUDs;
  pages' new windows open up close (from the person's press, under the URL rules); a keyboard in the headset, only when
  the person asks for it (`docs/vr.md` > Controls); typing into pages on the desktop reaches them (field focus);
  speakers and microphone as settings, voice at the viewer's balance against world sounds, the mic token's ring from
  your own mic; the neighbour region lost about one login in ten (LibreMetaverse's handlers raced) fixed; jobs write
  their plan before building, and briefs say what's wanted. H.264 in pages needs our own CEF build to stay
  licence-clean (`docs/media.md`, the user's call on the board).
- **Crowds, offline messages, a way out** (2026-10-26, tested on the perf grid's NPC crowd, `tools/crowd.py`): the
  nearest 16 avatars whole, the rest impostors that keep their depth in each eye; the viewer's complexity limit with
  jelly dolls, judged from mesh headers before anything is built; per-person always/never (`SojournAvatars`); offline
  IMs asked for at login; the left menu button held (Ctrl+Shift+H) stands you up and sends you home. What's left from
  that list: `docs/viewer-gaps.md` (end). `docs/performance.md` > Crowds.
- **The assistant, after the third main-grid session** (2026-10-26, checked on the local grid): pictures and short
  videos of the view saved for the person (Pictures/Videos\Sojourn; `SojournCapture`, the capture tool); what's
  animating them and "Stop animating me"; the `tracking` setting, by default giving way to animations from things
  around you; mirrors and reflection probes buildable, wrong values refused, addToContents sure of what went in; long
  sittings in the full 1M context with no compaction, each session writing its own handoff near 800,000.
  `docs/assistant.md` > Long sessions, Pictures and videos, Animations on the person, Building.
- **The pointer and the panel, after the third main-grid session** (2026-10-26, the user's findings in
  `docs/session-2026-10-26.md`): one pointer, on the right hand, off by default (A toggles it; it touches, sits, presses
  menus and is "that"); with it off the assistant goes by words and the middle of the view and asks the person to point
  when it can't tell, the pointer coming up by itself. Long answers show whole or a page at a time; the chat feed opens
  a line, notice or script menu in full and has a close key; the panel, menu and feed are carried along past their
  range instead of gliding back to the middle. `docs/vr.md` > Controls, `docs/assistant.md` > The panel.
- **Notices, the reader, replies kept** (2026-10-27, Claude Opus 5.5, the fifth session's panel findings; tested on the
  local grid, `tools/notices.py`): waiting notices carry their keys on the feed (a script menu's buttons in the
  viewer's layout, More > paging by the script; Read for a notecard given), answered by the person's own press, put
  away by ×, all cleared, and brought back by a bell on the forearm; a reader for notecards, the session's replies (the
  panel's stack key) and the assistant's long text, scrolled by hand; the headset's quad layers composited by which is
  in front (since 2026-10-28, `Xr/MenuOrder.cs`); group chat known by its group. `docs/social.md` > Notices, `docs/assistant.md` > The reader.
- **Walking, walls and seats in the headset** (2026-10-26, after the third main-grid session; tested on the local grid in the XR Simulator, `tools/motion.py`): the walk meets walls and steps locally (a collision model on the drawn prims, a step height learned from the region) and gives way quickly where the region disagrees; when the avatar can't follow you it's teleported to you; the body faces where you look, walking backward and sideways too; seated, the view is at the avatar's eyes, and the stick or a forearm key stands you up; the left trigger held on open ground teleports you there; Y held looks around away from the avatar (the user's request). `docs/vr.md` > The rig, Controls.
- **The world model, 2026-10-27** (Claude Opus 5.5, checked on the local grid in the XR Simulator): moving prims
  (vehicles, keyframed rides, physical things, llTargetOmega spin) carried on between the region's updates as the
  viewer does, corrections decaying smoothly, and the seated view riding the seat where it's drawn (`docs/vr.md` > The
  rig > Moving things); the local collision model by the region's physics shapes (GetObjectPhysicsData, meshes' hulls
  and physics models, children with none; > Physics shapes); the viewer's pre-water alpha pool with depth writes
  (`docs/rendering.md` > Blended or masked).
- **Frame rate up close, after the third main-grid session** (2026-10-26, Claude Opus 5.5; the flashes were the GPU at 12-37 ms in Mirromere and the café, and the CPU was at 9-14 ms there too): cheaper local lights, particles in dynamic mirrors, bouncing and homing particles no longer re-integrated from birth each frame (5x less particle CPU in a stress scene), objects no longer blinking out after a script updates them, the view line's `content:` part and `tools/cputrace.py` to tell what costs in the real places. The real places cost 3-4x our rebuild at the same size and the user's settings now fix full resolution and 3072 shadows: the next session's pacing and content lines decide what's next (`docs/performance.md` > Close-up alpha; checks on the board).
- **Late frames and garbage, after the fourth main-grid session** (2026-10-26 evening, Claude Opus 5.5; Firestorm Social Island allocated ~500 MB/s, ~10 MB a frame, and most of its ~240 late frames had every phase inflated, as collections do): worn meshes' joint palettes were made afresh per mesh per frame (SL skins list ~110 joints); now one per avatar and skin, written into the draw list, with the rest of the render thread's per-frame garbage, pooled texture decode buffers, rate-limited expression-driven body rebuilds and a higher-priority render thread. On an SL-like crowd (40 whole avatars): 448 -> 15 MB/s, gen0 ~23/s -> 0.9/s, CPU a frame 4.8 -> 3.5 ms; pictures unchanged. frames.csv now carries GCs and the loop's own CPU; `tools/alloctrace.py`. Not the view-jitter fix: the next headset session's `vrlog.py` says what's left of the late frames (`docs/performance.md` > Late frames and garbage).
- **GPU work that leaves the picture as it was** (2026-10-27, Claude Opus 5.5; each checked against the old build's shots
  at its own run-to-run noise, timed by per-frame A/B under the shared GPU, `docs/rendering.md` > Two eyes, The lenses'
  hidden area, Small savings, Measuring): multiview for the forward and alpha passes (neutral here; the G-buffer stays
  an eye at a time, 2-3x slower in multiview on this card); light passes leave unlit tiles before reading the G-buffer
  (-0.4-0.6 ms a frame); the lenses' hidden area not shaded (XR_KHR_visibility_mask; a 19% test lens saved 0.3-1.2 ms,
  Meta's real mesh untested); blended faces by material path (alpha -25-30%); solid shadow casters depth-only; far
  cascades kept a frame while the view is still. What would only pay with a visible change is the user's call
  (`docs/performance.md`, plan items 2-4c).
- **The rest of the 2026-10-26 list** (Claude Opus 5.5, tested on the local grid; what only SL or the headset can check is on the board): animesh, objects with their own skeleton, in the world and worn (`docs/rendering.md` > Animated objects; TesselBuild `animated`); SL's search for the assistant, places, events and classifieds with where each is, the Destination Guide by the web (`docs/assistant.md` > Search); abuse reports in the person's words with a picture of their view, sent only at their press (`docs/assistant.md` > Reporting abuse); where the panel and the HUD board were put and per-person voice volumes and mutes kept between sessions; `phantom` takes and reads back (LibreMetaverse's flag message carried an ExtraPhysics block OpenSim applies instead); the assistant's "(stopped: error_during_execution)" was a cut-off turn's leftovers answering the next request, now read out first (`docs/assistant.md` > Checked end to end).
- **Dev mode** (2026-10-26, the user's ask; tested on the local grid and in the XR Simulator): a Claude Code session the
  user started is Sojourn's assistant. `python tools/dev.py start [--vr|--sim|--main]` starts the client with `--dev`
  (no Claude of its own); what the person says or types reaches the session through `dev.py watch` under the Monitor
  tool, and `dev.py say` answers on the panel. The how-to is `docs/driving.md`; how it works and why
  only the person's words get through: `docs/dev-mode.md`.
- **Incognito** (2026-10-27, the user's asks; private mode renamed; tested on the local grid by `tools/private_leak.py`,
  which searches everything sent to the model for planted markers): nothing from a spell is kept (sealed events, IM
  history and logs kept clear, the sitting's conversation dropped, no memory notes); blind by default, the assistant
  getting only a general picture and handles through an allowlist on the op dispatcher and sessions without Claude
  Code's tools; with "let it see", the usual tools and ops but nothing that writes (Read, Glob, Grep, the web; a working
  folder deleted at the end). On by the menu, P, the assistant asked or automatically (a person, a place, an open IM),
  off only by the person (the token at the wrist, the menu, P); the headset's mirror window blanks; dictation (Y, or
  both grips) drafts words the person sends without the assistant. `docs/assistant.md` > Incognito.
- **Load governor, video memory, a lost GPU** (2026-10-27, Claude Opus 5.5, after the sixth main-grid session paged
  5.3 GB of textures into a driver reset): nothing loaded changes how it looks, and loading slows but never stops (the
  user's rules); while frames run long, loading slows and new far objects come in at a rate, largest first, faster the
  longer it lasts, with wide hysteresis (`load_governor`, menu > view); video memory is held under Windows' budget for
  the process by stopping texture growth, shrinking what's out of view, then on-screen textures smallest first, the
  draw distance coming in only when that's not enough; a lost GPU restarts the client where
  the person was. Foveation was removed (too visible at the edges). `docs/performance.md` > The load governor,
  `docs/client.md` > When the GPU is lost.
- **The jitter fixed** (2026-10-27, Claude Opus 5.5; the user confirmed it in the headset): the fast, fine whole-view
  shake under load was eye images reaching the compositor before the GPU had finished them, likely on Meta's side; each
  frame's GPU work now finishes before its images go back (~9 ms of CPU wait a frame in busy places). Found with
  `tools/lagtest.py`. `docs/vr.md` > The rig > The jitter, and its fix.
- **Threads** (2026-10-27, Claude Opus 5.5; the user: do the postponed CPU work): a render thread of its own (the
  main thread simulates the next frame while the last is recorded, submitted and, in the headset, ended; the world
  handed over in order, so nothing sees it half-built), persistent GPU instances and materials for world objects, draw
  lists built in parts on three scene workers, faces resolved off the frame and region changes in timed batches. On
  the perf grid's crowd the render thread's CPU a frame went 3.6-4.1 -> 2.8-3.3 ms median (p99 6.7-6.9 -> 5.1-5.4),
  arriving cold p99 5.3 -> 3.2; the same pictures. `docs/performance.md` > Threads, `docs/client.md` > How it fits.
- **The evening of 2026-10-27** (two dev-mode sittings on SL, Claude Opus 5.5 with background agents;
  `docs/session-2026-10-27b.md`, items 1-67). Confirmed by the user in the headset: border crossings keep the own avatar
  (a stale copy was left in the old region), falls no longer sink, the own avatar's bakes load (an appearance with a
  placeholder param wiped the real ones), floating text no longer goes watery (labels were affine-mapped; now in perspective),
  far impostors don't hop, the cloak sways (flexible prims), teleporting onto scripted floors, the friend-online name,
  right-stick scrolling, the small UI size, server movement, Parakeet hearing. Also fixed: a frame-loop freeze hulling
  big meshes, the texture table filling (65535 slots, long-unseen textures let go), far objects held for good under
  steady lag, long requests losing their first 30 s, a notecard "worn", the reader's stuck beam. New: menus on the PC's
  mirror window, a UI size setting with kept answers, avatar physics, `movement` (server, the default, or predicted),
  `hearing` (parakeet default, whisper, soniox opt-in with the user's key), `confirm_text` (a prefilled typed check for
  unknown names), recent regions kept as hearing hints. Defaults the user chose: server movement, Parakeet, simple
  see-through light; frame synthesis off for good, no setting (it brought the jitter back, 2026-10-28).
- **After midnight, 2026-10-28** (two sittings on SL in quick mode, Haiku at medium; Claude Opus 5.5 with background
  agents; `docs/session-2026-10-28.md`): avatars stuck as clouds in a crowd (bakes asked for by a bakes-on-mesh face
  never loaded whole; big uploads starved under lag), name tags cut off in crowds (a fixed vertex buffer), the black
  flashes (the rig's 90 ms blinks: removed, every view move is a straight cut, the user's call), the way out (a lost
  UseCircuitCode, now resent; refusals heard), far teleports (the view holds in the old place until arrival, one cut),
  other regions' updates moving the agent (SelfUpdateGuard), fast border crossings, and a memory leak (decoded
  textures piling up behind throttled uploads; `tools/heapwho.py`). Reasoning effort per model is a setting. All
  checked off the main grid; the headset checks are the board's first item.
- **The afternoon of 2026-10-28** (a dev-mode sitting on SL, Claude Opus 5.5 with background agents;
  `docs/session-2026-10-28b.md`): the board's checks passed; a memory leak fixed (pooled draw parts kept left regions'
  scenes alive, and caches grew per place); the headset's layers are sRGB, so sheets are premultiplied in linear light
  (`GlassLight.cs`) and the panel has a glow of its own as it clears; the default look is now clear white glass;
  readability is advice, never a refusal, and the assistant's cards have no caps; Meta's compositor hides quad layers
  by our depth (menus over walls an option); that test proved patchy over Link, so since the evening we hide
  menus ourselves (docs/vr.md > Walls and the menus); X hides the whole interface; big textures load small first under lag (the
  rain's grey sheets); sleeping controllers hold their pose 20 s; `dev.py shot` composites the headset's menus.
- **The evening of 2026-10-28** (dev-mode sittings on SL, Claude Opus 5.5 with background agents;
  `docs/session-2026-10-28c.md`): the fine shimmer when walking by stick is half rate (gone at a steady 90; left as
  known, quality first, the user's call); frames now overlap the GPU (a frame-end thread does the jitter fix's wait,
  CPU 17 -> 5-8 ms a frame); frame synthesis removed for good; walls hide menus by our own drawing over them, the
  compositor kept from it (its depth test only worked in a patch around the view).
- **The night of 2026-10-28** (a dev-mode sitting on SL, Claude Opus 5.5 with background agents;
  `docs/session-2026-10-28d.md`; all checked on the CPU only, the headset checks on the board): melting avatars were
  skin weights not summing to 1 at a skybox's height (fixed in `SkinWeight.From`); menus over menus ordered by which
  is really in front and cut where they cross (`Xr/MenuOrder.cs`); avatars smoothed as prims are, island edges held
  (the rubber banding); the reader in the panel's glass; the menus' edges no longer tear through cloth; music streams
  take any certificate; a headset runtime failure counts as a lost GPU (a driver reset arriving at Mirromere);
  `tools/mirrorshot.py` captures what the headset really shows.
- **Late on 2026-10-28** (a dev-mode sitting on SL, Claude Opus 5.5 with background agents;
  `docs/session-2026-10-28e.md`; all confirmed in the headset): the night's six checks passed; PBR terrain drawn (Eden
  Valley's grey ground) and its flicker gone; far regions' ground fills in as it arrives; no depth to the compositor
  while menus pass through each other (it depth-tested them against each other); menus' edges clean through hair;
  every distance that decides drawing, sorting or shadows from the head's centre; blended faces sorted in two levels
  as the viewer (House of Legends' mist); lost materials asked again; a seat's leftover pose stopped on standing; a
  restart after a lost GPU no longer dies on a held log file. The GPU resets are a card/driver fault under heavy load
  (the root board). Next: menus follow your head until moved, and a reset.
- **Inventory and the avatar's own** (2026-10-29, batch B7, Claude Opus 5.5; `docs/inventory.md`): a client AO playing
  Firestorm's `#Firestorm/#AO` (or a picked folder, a ZHAO-II notecard's in place; nothing made unless asked), the pose
  stand, thumbnails and favourites (SL's inventory service), protected folders.
- **Building** (2026-10-29, batch B6, Claude Opus 5.5; `docs/building.md`, checked on the local grid by
  `tools/building.py`): PBR materials applied, overridden and taken off, legacy normal and specular maps and projectors
  (TesselBuild modify keys, so `build` takes them); `SojournBuild`: materials read back and uploaded, undo and redo of
  every edit (a build undone in one step), the Build menu's script chores with SLua's four targets, land impact and
  script info, group and deeding, default and bulk permissions, save back to contents, take separately, pathfinding,
  backups and restores under Firestorm's own-creation rules. Lights and projectors taken off prims now go
  (LibreMetaverse kept them).
- **Information for the person** (2026-10-29, batch B8, Claude Opus 5.5 with two helpers; `docs/map.md`,
  `docs/rendering.md` > Beacons and highlights, `docs/assistant.md` > Pictures and videos): the world map board (pick a
  spot, Go or track it; it keeps what it showed) and a mini-map that opens from the location line above the left
  wrist, on the hand or popped out (split 2026-10-29, the user's call), the line with the lag meter, statistics at a glance (`SojournMap stats`), beacons and Highlight Transparent, the tracking beacon,
  bigger and placed-camera pictures, 360s, to inventory and postcards; the profile feed refused (the user's call).
- **The menus after the waves** (2026-10-29, batch M, Claude Opus 5.5; `docs/assistant.md` > The panel > The settings
  menu): every row the batches asked for placed (social a section of its own; the AO, turn-away and avatar fixes in
  you; hear from, first click, zoom to media; the view's graphics, avatars, beacons, parcel lines and menus as groups,
  a row opening a page of its own); tabs on two strips when they don't fit; rows for other parts' settings made by name
  from SojournSettings. A camera at the left forearm whose shutter is the right trigger, Ctrl+` on the desktop.
- **Before the main grid**: the ToS and MFA are answered by the person in the console, never automatically or by the assistant (done). The rest of the must-dos are in `docs/tpv-policy.md`. Main grid uses the user's old account; ask them for the login.
- **Next** (checked 2026-10-27; the user's checks and calls are on the board):
  - Simulator and local-grid checks the evening's agents left for when the GPU is free: falls, jumps, edges, a
    teleport, a crossing and jitter.py in both movement modes ran (`docs/session-2026-10-27b.md` > Checks after the
    session: server mode's falls sank again and crossings blinked there, both fixed, as was flying up overshooting); the
    texture slots churn test, the mirror window's menus (0.015 ms a frame) and the pointer over the feed and reader
    passed (same section).
  - Closing every viewer gap: the batches at the end of `docs/viewer-gaps.md` (waves 1-3). B1, safety and comfort, is
    built (`docs/safety.md`; its headset checks on the board).
  - Usable without sight: Sojourn Access, its plan `docs/access.md` (milestones M0-M6, from the research in
    `accessibility.md`, `radegast.md`; A1 built: what arrives read out through the person's own
    screen reader, NVDA by its API, others by UI Automation, with keys for confirmations and notices,
    `docs/screen-reader.md`). M0 built (2026-10-29): the session host split out as `src/Sojourn.Session`, the VR
    client on it unchanged, a console probe logging in without a window. Most of M1 built the same night
    (`src/Sojourn.Access`, `docs/access-app.md`): login, Alt+1-9 areas (chat, conversations, people with clock-face
    bearings, friends and groups, inventory, search, places, objects, notices answered by keys, the assistant),
    read-out with mutes, review, mark and copy, Radegast's `//` commands and `//route` for her notecard routes,
    profiles, give, delete and pay behind her confirmation, Ctrl+G; local grid only. Since: incognito's and voice
    chat's keys (`docs/access.md` > M1 progress); next a blind tester's verdict. M0b built (2026-10-29): the renderer out of
    sight (`src/Sojourn.Session/Offscreen/`): the assistant's look gets a real picture in Access, its touches land on
    faces, the collision model answers rays, a lost GPU restarts it in the app.
    M3 built (2026-10-29): walk mode (F3: arrows walk through the region's movement; footsteps by surface, a warning
    before a bump, openings and drops, a sonar key, every cue a sound and a line), paths around obstacles for `//go`,
    follow and the assistant's walks, driving seated by arrows, seated crossings said (`docs/access-app.md` > Walk mode).
    M4 built (2026-10-29): earcons per kind of arrival (IM, mention, menu, offer on by default; the rest optional), a
    beacon on where a walk heads or on what she picks to walk to, O for doors, seats and touchable things sounded from
    where they are, Access's own volume apart from the world's (`docs/access-app.md` > Earcons).
    M5 built (2026-10-29): describing by Ctrl+D (what's selected, or here), Ctrl+Shift+D (her and what she wears),
    again for more, and `//describe`: the assistant gets pictures aimed at the subject with what's visible in each by
    name, and the lists' facts; without a Claude account it says so, then what the lists know (`docs/access-app.md` >
    Describing).
    M2 built (2026-10-29): every part of a linkset and its faces listed and touched by keys (the treasure chest test
    passes), HUD buttons as parts, placing by words, a second inventory pane, what she's wearing by name; M1's incognito
    and voice keys too (`docs/access-app.md` > Parts and faces, Incognito, Voice chat).
    A build for a tester's own PC: `python tools/access.py package` (a self-contained zip in local/dist, versioned
    0.1.0.N, data in %LOCALAPPDATA%, Second Life by default, `--local` for the local grid, no assistant; unsigned;
    `docs/access-app.md` > Packaging). Handing it to anyone is distribution under the TPV policy and the LGPL
    (`docs/tpv-policy.md` > If it's ever distributed).
  - Braking when the stick is let go: built (the viewer's STOP flag, both modes; `docs/vr.md` > The rig > Braking),
    held in the simulator; the main grid's answer is on the board. The GPU options the user decided to try are built
    as settings, all off, for the headset (half-size shadow & SSAO -1.3 to -1.6 ms a frame, far cascades 10% larger
    -0.45 ms while moving, small shadow casters and narrow normals ~nothing here; `docs/rendering.md` > GPU options to
    judge; on the board). Sheets drawn in the eyes are now perspective-correct (rendering.md > Name tags).
  - Voice chat on the main grid; Phase 4's next steps (leaning, crouching, fingers).
  - An in-world desktop (the user wants it, 2026-10-28; planned, not started): our own mirror of the PC's desktop or
    single windows (Windows' capture APIs), as sharp layers or surfaces in the world, the pointer as the mouse and the
    real keyboard; built as a library other apps of ours can use, rather than Meta's Dash desktop.
- Deliberate departures from the viewer, for the budget: shadow maps can be capped (`--shadow-cap`; the viewer sizes them to the screen), the sky is drawn after the geometry. For the headset: mirrors render a reflection per eye instead of one cube map, the scene budget has a band against popping, and every distance that decides what's drawn, loaded or sorted (draw distance, the water's edge, blended faces' order, sun shadows' cascades and reach, tree detail, which avatars are whole) is from the head's centre, never the view's axis or a far plane (the user, 2026-10-28; `docs/rendering.md` > Distances from the head). Everything else renders as Multitude's port of the viewer does.

## Layout

- `src/` .NET 10 solution: `Sojourn.Core` (Multitude's session core: login, world model, LEAP APIs, appearance), `Sojourn.Render` (Multitude's Vulkan port of the viewer's pipeline; `Realtime/FrameRenderer.cs` is the per-frame renderer, `Realtime/Overlay.cs` the panel and pointer drawn over it), `Sojourn.Session` (the session host the VR client and Sojourn Access share, never touching a window, OpenXR or drawn menus: `SessionHost.cs` starts it in steps; `Bridge/` the agent API; `Assistant/` the assistant's host, panel model and raster, confirmations, sidecar, snapshots; `Voice/` voice chat; `Audio/` world audio; `Social/` the feed's data, keywords, radar, translation; `Private/` incognito's state and allowlist; `Media/` media's manager and helpers; `Capture/` pictures; `People/`, `Land/`, `Building/`, `Inventory/`, the map's data (`Map/`); `Access/` the screen reader's wiring, over the shared `Sojourn.Core/Access/`; `Control/` VrSettings, SojournSettings and the session's APIs; `RegionFeed.cs` regions to a renderer; `Offscreen/` the renderer out of sight for an app that shows no view; the folders as in the client), `Sojourn.Client` (SojournVR.exe, the VR app on it: window, controls, the frame loop, bench; `Xr/` and `Control/VrPilot.cs` the headset; `Login/` the login screen; the drawn menus and what's drawn over the world: `Assistant/` the panel's placement, the reader's view, pictures with the menus, `Control/SettingsMenu.cs` and the client's own settings rows (`SettingsStandard.cs`), `Social/` name tags and the feed's sheet, `Hud/` HUD attachments, `Map/` the map board and the location line, `Beacons/` beacons and the tracking beacon, `Showing/` what the assistant shows on screen, `Media/` pages' input, sheet and boards, `Private/` incognito's controls and dictation, `Access/` the reader's keys; the Khronos OpenXR loader in `native/`), `Sojourn.SessionProbe` (a console host over Sojourn.Session alone: `tools/probe.py`), `Sojourn.Access` (Sojourn Access, the app without sight: WinForms on Sojourn.Session; `docs/access-app.md`), `Sojourn.Launcher` (Sojourn.exe, how a person starts Sojourn: login, start, settings before going in (the assistant's provider and key are chosen only there), Launch and the run's state, the grid's terms and MFA code at the desk; WPF; `docs/launcher.md`), `Sojourn.Voice.Native` (voice's C++ shim over libwebrtc, built by `tools/voice.py`; world audio's mixer and decoders in `world.cpp`), `Sojourn.Media.Native` (media's helpers: `sojourn_web` over CEF, `sojourn_video` over libVLC, built by `tools/media.py`).
- `assistant/`: the assistant's Python side (Whisper, the Claude sessions, the tools, spoken replies in `voice.py`; `harness.py` what its sessions may do, `home.py` its memory, `jobs.py` background jobs, `build.py` building from a spec; `prompt*.md` its system prompt), started by the client. Its own folder `local/assistant/home` (memory) and scratch `local/assistant/work`.
- `tools/`: `client.py` (build and run: `run`, `vr`, `vr-sim`, `--logs`), `probe.py` (Sojourn.Session without the VR client: two console hosts log in, answer SojournSettings and hear each other in local chat; no GPU), `grid.py` (the local grid: start/stop, accounts, console, `scene light|dense`), `vrlog.py` (a headset session's logs, summarised), `forest.py` (the synthetic SL-like forest for the performance grid), `perfrun.py` (timed runs on it, summarised; `--as`, `--port`, `--base` for a second session's account, as `teleports.py` takes), `openjpeg.py` (builds the vendored J2C decoder), `assistant.py` (setup, check, and test requests: typed, from WAVs, synthesised speech, timings), `assistant_guard.py` (checks the assistant's harness refuses what it should), `fixtures.py` (the test inventory, fountain and second account), `winmouse.ps1` / `winkey.ps1` (mouse and keys in the window for tests, without the real cursor), `voice.py` (voice's shim: build, devices, the echo test, loopback, round trip and scenario tests), `janus_name_proxy.py` (the local voice server's plugin-name shim), `audio.py` (world audio's fixtures, test radio and recorded tests), `media.py` / `media_fixtures.py` (media's helpers: fetch and build; its fixtures, test site, tests and frame cost), `hud.py` (HUD text, HUD media and new windows, on the desktop and the board), `keyboard.py` (the headset's keyboard in the simulator), `render_scenes.py` (rendering test scenes: mirrors, mist, a flower field, lights, particles), `cputrace.py` (where a running client's CPU goes: a sampled trace, summarised), `heapwho.py` (what holds a running client's managed heap: a gcdump, read by `tools/heapwho/`), `dev.py` (dev mode: start, watch, say, shot, log, call, restart), `hands.py` (the hands over the menus in the simulator: shots and cost), `mirrorshot.py` (what the headset really shows: Meta's compositor mirror captured without focus, `--burst N`), `lagtest.py` (lag on purpose in the headset, with the pacing switches on the menu, and its report), `screenreader.py` (NVDA's controller client into `local/nvda`; the read-out test, with `uia_listen.ps1` standing in for Narrator), `land.py` (land and region on the local grid: two clients, every gated op pressed or cancelled, what it changes changed back), `ao.py` (inventory and the AO on the local grid: the AO from a fresh account to a set, sits, stands, locks, a worn collar relay, the pose stand, a ZHAO-II folder, confirmations pressed), `aocheck/` (the AO's logic checked without a grid), `avatars.py` (others' beams and PointAt, lip sync and the avatar fixes on the local grid: an observer client and `avatarfx send` as Sojourn Friend), `avatarfx/` (the avatars' IK, lip sync and animesh fixup checked without a grid; the effects sender), `map.py` (the map board and the location line on the local grid, `--headset` in the simulator), `beacons.py` (beacons and highlights: a test platform and its checks), `building.py` (building on the local grid: materials, undo, the script chores, permissions, group and deed, take separately, a backup restored, gated ops pressed), `menu.py` (the settings menu on the local grid: every section, group and page drawn as the client has them, rows pressed and read back; `--headset` the camera in the simulator), `launcher.py` (the launcher: build, run, its states drawn offscreen, `check` its terms and MFA path on a hidden desktop), `access.py` (Sojourn Access: build, run, the visitor bot's commands, and the test on a hidden desktop), `quireuia/` (a program on a hidden Win32 desktop, what a screen reader is told there, and driving it by UI Automation and posted keys; from the Radegast contributions), `visitor/` (Access Visitor, a LibreMetaverse bot with a scripted fixture box: chat, IMs, menus, llTextBox, permission requests, offers), `inputcheck/` (Sojourn Access's judge of the person's own keys, a screen reader's pass-through included, checked without a person; RealInput in a window of its own under a UI Automation reader).
- `docs/`: `client.md` (running, controls, architecture, the local grid, gotchas), `vr.md` (the headset: running, controls and settings, the rig, your own body, dynamic resolution, testing without a headset, numbers), `assistant.md` (the assistant: using it, how it fits, Claude, confirmations, the panel, testing, numbers), `performance.md` (measurements, what dominates, the plan to the VR budget), `tpv-policy.md` (Linden Lab's TPV policy for us), `audio.md` (world audio: what plays by the viewer's rules, using it, how it fits, the spatialiser, testing, numbers), `voice.md` (voice chat: using it, how it fits, SL's protocol, building, the local voice server, testing, numbers), `social.md` (names over people, objects' floating text, the chat feed: what shows, where, how it's drawn), `hud.md` (HUD attachments: the viewer's HUDs, the desktop, the headset's board, the assistant's pictures and presses, testing), `media.md` (pages and video: what plays and whose, using it, how it fits, safety, the API, numbers, testing), `rendering.md` (what the renderer adds to the port: mirrors, blended faces' sort, the budget's band, the alpha shader, test scenes), `shared-tracking.md` (whether others can see your tracking: Puppetry, the TPV policy, the options), `dev-mode.md` (a Claude Code session as the assistant: the relay, whose words get through, testing), `driving.md` (the how-to for that session: start, listen, act, answer, record), `screen-reader.md` (what's read out through the person's screen reader, the keys, incognito, testing), `access.md` (Sojourn Access, the app without sight: who it's for, stance, shape, milestones, a blind tester's requests), `launcher.md` (the launcher: what it does, why the login happens in the client, the client's side, testing, look, packaging with the VR client), `access-app.md` (Sojourn Access as built: running, keys, areas, `//` commands, how it fits, testing, the guided test), `radegast.md` (all of Radegast and how blind people use it), `building.md` (building: PBR and legacy materials, projectors, undo, scripts in bulk and SLua's targets, land impact, group and deeding, default and bulk permissions, pathfinding, backups under Firestorm's rules; for batch T and M; SL-only checks), `inventory.md` (inventory and the avatar's own: thumbnails, favourites, protected folders, the AO from Firestorm's folder format, the pose stand; for batch T and M; SL-only checks), `map.md` (the map board, the location line, statistics at a glance; SojournMap; for batch T and M; SL and headset checks), `land.md` (land and region: About Land and its lists, buying, selling, passes, dividing, joining, terraforming, holdings, the map's items, land search, the region and covenant, the estate's tools; what's drawn; what waits for the person), `people.md` (people and me: friends' rights, contact sets, away and Do Not Disturb with the person's own reply, typing, hover height, experiences, teleport history, favourites, pay buttons, places offered), `safety.md` (derendering and the blacklist, antispam, keep me in place, the comfort vignette, aiming for weapons, release keys, bumps), `viewer-gaps.md` (what Sojourn lacks next to the official viewer and Firestorm, by how much it matters and what it takes; closed ones marked). Multitude's `docs/render.md` still describes the pipeline itself.
- `local/` gitignored: the grid (`local/grid`, ports 9700-9703 and 9709), the client's state, caches, screenshots and logs (`local/client`), the assistant's venv, test speech and timings (`local/assistant-venv`, `local/assistant`), Meta's XR Simulator (`local/xrsim`), the OpenXR SDK's validation layers (`local/openxr-sdk`), LL's libwebrtc and voice's build, tests and voice server (`local/webrtc`, `local/voice`, `local/voice-server`), reference copies of the viewer's voice code (`local/ref`), NVDA's controller client (`local/nvda`, LGPL-2.1, from `tools/screenreader.py setup`).

## Publishing

`python tools/publish.py` (`--check` to only build and check) makes or refreshes two local repos beside this one,
each run that changes one a new commit there (authored by Claude, the user's GitHub noreply as committer, UTC dates):
`../sojourn-public` (studio-quire/sojourn: this repo's HEAD, committed files only, scrubbed, plus README, LICENSE,
THIRD_PARTY.md and docs/download.md from `tools/publish/sojourn/`) and `../sojourn-access-public`
(studio-quire/sojourn-access: `tools/publish/access/` only). Left out (`tools/publish/rules.py`): board.json,
`docs/session-*.md`, headset-checks.md, public-note-draft.md, the research doc centred on two private individuals, and
the publish machinery itself.
Private individuals (the blind tester and her partner, residents met in sessions, issue filers) become roles: in
docs, code comments, tests and fixtures write roles, not residents' names, so the rules stay few. The run refuses to
commit on any banned name, handle, email or path, or a gitleaks finding (portable copy in `local/gitleaks`).
`local/publish/map.txt` records which private commit each public one came from: a packaged Access build's release
names the public commit made from the same private HEAD. Pushing is by hand, to a remote named `public` (never
`origin`, so the nightly backup can't publish).

## Constraints

- GPU: Tessel's live viewer shares the RTX 5070 until the always-on box takes Tessel over (~2026-10-24). Until then, keep real-time render tests short, and run one GPU-heavy agent at a time.
- Test on a local OpenSim grid first (Multitude's dev grid setup, `projects/multitude/docs/running.md`). On the main grid, Sojourn uses the user's old SL account (user, 2026-10-20). Ask them for the login when we get there; credentials stay out of the repo (`local/` only).
- Third-party viewers on the main grid follow Linden Lab's TPV policy: `docs/tpv-policy.md` (what applies to us, the must-dos before the first main-grid login, where the assistant meets the bot/scripted-agent rules).
- Background research on SL VR (official OpenXR branch stalled 2024, P373R mod, Puppetry, VorpX): summarised in the session that started the project. Nothing here depends on it.

## Credit

Built by Claude (Opus 5.5 so far). The user wants credit for whatever their role turns out to be (2026-10-20). So far that's the idea, the direction and the key decisions; when something goes public, describe their role as it actually stands then.
