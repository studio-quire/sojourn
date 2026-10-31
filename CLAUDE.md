# Sojourn

*Published as it is: this is the agent-facing index the Claude models working on Sojourn read first. Some things it
points to stay private and aren't in this repository: what the headset sittings left open (`docs/sittings.md`), the
board, `local/`, and the other Studio Quire projects under `projects/`. For people, start with README.md.*

Working name. Our own Second Life client, built for VR first, with a personal AI that operates the client for you by voice: whatever is awkward in a headset (inventory, outfits, search, typing, building, dialogs, settings). An accessibility tool for a person who is present and in charge, not an autonomous resident like Tessel; it runs on the desktop too. Its sibling Sojourn Access, in the same codebase, makes Second Life usable without sight. Started 2026-10-20 (Claude Opus 5.5, with the user).

## Where it stands (2026-10-31)

- **In use on Second Life** in the Quest 3 over Link, in dev-mode sittings: the user in the headset, a Claude Code
  session as its assistant (`docs/driving.md`). What's left for the user to see is on the board; what sittings left
  open, and older checks, in `docs/sittings.md` (kept current; no per-sitting notes).
- **Built**: the desktop client and the headset (OpenXR, the rig, controls, your own body and where you look), the
  assistant (voice, Claude through the agent API, confirmations, the panel, memory, jobs, incognito), voice chat, world
  audio, HUDs, media, name tags and the feed, and most of the viewer-gap batches (safety, people, rendering, avatars,
  social, land, building, inventory and the AO, the map, voice and media extras, the menus, the login screen, the
  launcher). The old phases 1-3 are done; phase 4 (embodiment) has its first pass.
- **Sojourn Access**: milestones M0-M5 built (2026-10-29); test builds (the assistant bundled) published as releases
  on its front door, studio-quire/sojourn-access, since 2026-10-30; a blind tester's first session on Second Life was
  2026-10-30. `docs/access.md` (plan, milestones, what testers said), `docs/access-app.md` (as built, packaging).
- **Public source** since 2026-10-29 (`docs/publishing.md`). The VR viewer itself has no release yet.
- **Next**: the board's checks; the gaps left in `docs/viewer-gaps.md` (> Closing the rest) and the calls waiting on
  the user there; Access's next milestones and testers; phase 4's leaning, crouching and finger poses; an in-world
  desktop (the user wants it, 2026-10-28; not started): our own mirror of the PC's desktop or single windows (Windows'
  capture APIs) as sharp layers or surfaces in the world, the pointer as the mouse and the real keyboard, built as a
  library other apps of ours can use, rather than Meta's Dash desktop.

## Decisions

The product:
- **Build our own** (user, 2026-10-20): no VR mods (P373R's Firestorm VR Mod), no injectors (VorpX), no official viewer
  plus patches; we can probably do it better. A VR-native client and the assistant, as one project.
- **An accessibility tool** (user, 2026-10-20): the assistant acts for a human who is logged in and directing it, so
  this account doesn't carry Tessel's scripted-agent setup.
- **A full viewer for release** (user, 2026-10-27, 2026-10-29): every feature other viewers have that we lack gets
  closed, not only the valuable ones (`docs/viewer-gaps.md`); the release waits for most gaps, not every last one, and
  is planned relatively soon. Free by default and open source as licences permit; paid extras possible later (such as
  credits so the assistant works without the person's own key). Sojourn itself is a VR viewer without an
  accessibility lean; accessibility is Sojourn Access's purpose.
- **VR-first; the desktop gets its own UI later** (user, 2026-10-27, 2026-10-29): today the desktop reuses the
  headset's panel, feed and menus and is mainly for testing. A desktop interface quite different from the headset's is
  to be workshopped once VR is good. The first release is VR-first, the desktop a fallback recommended against, not
  waited on.
- **Simple menus** (user, 2026-10-29): by default only what a person would change by hand rather than ask (draw
  distance stays); an `advanced` switch shows everything; every row has a tip on hover (`Control/MenuGuide.cs`).
- **Usable without sight, as a separate app** (user, 2026-10-27, after Tessel met a blind resident who uses Radegast
  with NVDA and wished for a full viewer with its accessibility): Sojourn Access, its own app project beside the VR
  client with its own accessible window and keyboard model, sharing Core, the assistant, hearing, audio and the
  renderer (running out of sight for descriptions and the collision model); not a fork or a mode of the VR build,
  possibly shipped under its own name, and separate from the sighted desktop UI to come. Directed by Claude models and
  fully usable without a Claude account (user, 2026-10-29). `docs/access.md`.
- **Two apps, shipped separately, one repo** (user, 2026-10-29): no one uses both, so each has its own package and
  releases. The source is public as one repo plus Access's code-free front door (`docs/publishing.md`).

How it's built:
- **A fork of Multitude's client core and renderer** (Claude Opus 5.5, 2026-10-20): .NET 10 on LibreMetaverse, plus
  Multitude's Vulkan port of the viewer's deferred pipeline (`projects/multitude/src/Multitude.Core`,
  `Multitude.Render`). Vulkan + OpenXR with multiview is the natural VR stack (the official viewer is OpenGL and mostly
  single-threaded, and frame rate is SL VR's hardest problem); with the assistant as the main interface the viewer's 2D
  floaters aren't needed; Multitude already served the LEAP APIs and Tessel's bridge API, so the assistant's control
  surface mostly existed; LibreMetaverse is plumbing we don't rewrite.
- **Forked, not shared**: Multitude renders on demand and offscreen for many agents, Sojourn one person at 72-90 Hz in
  stereo. A common library is an option later if both stay alive and alike.
- **Renders as the viewer does**, with deliberate departures for the budget: shadow maps can be capped
  (`--shadow-cap`), the sky drawn after the geometry, mirrors a reflection per eye instead of one cube map, a band
  against popping in the scene budget, and every distance that decides what's drawn, loaded or sorted measured from
  the head's centre (the user, 2026-10-28; `docs/rendering.md` > Distances from the head). Where a fix has
  trade-offs, do what other viewers do; go beyond them where it's easy (the user, 2026-10-28).
- **Under lag, far things get cheaper; see-through surfaces optional** (user, 2026-10-27): the load governor may end
  shadows sooner far away and make avatars beyond 20-48 m impostors, gradually; nothing loaded changes how it looks,
  and loading slows but never stops (`docs/performance.md` > The load governor). Simpler lighting for blended faces is a
  setting, on by the user's choice (2026-10-27); a cap on their layers is one too, off (it costs more than it saves in
  our scenes; `docs/rendering.md`). The fine shimmer when walking by stick at half rate (gone at a steady 90) is left
  as known: quality first (user, 2026-10-28).
- **Comfort aids are options; defaults suit experienced VR players** (user, 2026-10-27, 2026-10-28): don't add standard
  comfort aids without asking. View moves are straight cuts, never black blinks; no vignette unless turned on;
  antispam off by default as in Firestorm; frame synthesis removed for good (it brought the jitter back). Movelock and
  bump notices: no preference, kept as built (`docs/safety.md`).
- **Menus belong to the world** (user, 2026-10-28): in the headset, walls and things in front hide the panel, feed and
  cards by default; menus always on top is an option.

The assistant (detail in `docs/assistant.md`):
- **Its shape** (Claude Opus 5.5, 2026-10-21): the Agent SDK in a Python sidecar (Whisper too), on the person's own
  provider: their Claude account, an Anthropic key, or an OpenRouter key and model (2026-10-29), keys DPAPI-encrypted
  and added by a local proxy so Claude Code never holds them. Who takes a request first is the person's setting (user,
  2026-10-21): quick (Haiku 5.5, the default) or sonnet, each handing multi-step jobs to Opus 5.5, or opus for
  everything; each model's effort a setting too (medium by default, user 2026-10-27). Spending, deleting, giving,
  permission changes and new scripts wait for the person's own press (real input only), enforced in the op dispatcher
  so they hold for any caller. The panel is an OpenXR quad layer.
- **The provider is the launcher's** (user, 2026-10-29): provider, keys, OpenRouter's data collection and auto mode
  are chosen in the launcher (Access: Alt+0); the viewer's menu shows only that provider's models. `docs/launcher.md`.
- **No Claude-account sign-in in released builds** (user, 2026-10-30): Anthropic's Agent SDK terms don't let
  third-party products offer claude.ai login or plan usage without approval, so packaged and public builds offer an
  Anthropic key, OpenRouter and the studio relay only; people who want their subscription have their own Claude add it
  to their copy. Our own runs from the repo keep the Claude account. Bundling the SDK's Claude Code is allowed; the
  product must not present itself as Claude Code.
- **Its powers** (user, 2026-10-25): about as capable as Tessel at interaction, search and making things, guarded by
  permissions and Claude Code's auto mode rather than a stripped-down tool set: Claude Code's own tools, deny rules
  and hard rules for what must never happen (credentials, the person's confirmations, Sojourn's code and settings,
  recording others' conversations, acting in SL on its own), memory as files in its own folder.
  `docs/assistant.md` > What it may do.
- **Jobs are the assistant's** (user, 2026-10-25): one at a time, unbounded (no time limit or stall watchdog); the
  assistant decides whether to start one, its model, brief and helpers, and when to check, change, stop or resume it.
  The harness keeps only the mechanics. Plan usage isn't economised, so no caps. A job the person asked for may act
  until done or stopped; nothing starts acting on its own.
- **The person's choices** (user, 2026-10-25): replies on the panel by default, spoken by a local voice as an option; no
  name and a plain character by default, both optional and written as what the person would like ("they'd like you to
  use the name X"), never "you are X"; a glassy look by default, which the assistant can change when asked; its
  touches and menu answers on others' objects wait for confirmation by default (a toggle); voice chat push-to-talk by
  default, open mic with a mute as the option.
- **Hearing** (user, 2026-10-27): local by default (Parakeet, with Whisper selectable, the names around the person as
  hints); a cloud service as an opt-in, our pick of the best. Only if it neither keeps nor trains on audio may it run
  in incognito and get the name hints.
- **Incognito** (user, 2026-10-27): nothing from a spell is kept; blind to specifics by default, with "let it see" (off
  by default) letting the assistant help as usual, adult content included when needed, never making NSFW content, and
  no text assumes that use. On by the menu, the wrist, P or asking the assistant; off only by the person's own
  controls; no trigger words; the PC's mirror blanks; no automatic start by region maturity. `docs/assistant.md` >
  Incognito.

## Gotchas and rules

- **Local grid first** (Multitude's dev grid, `projects/multitude/docs/running.md`; `tools/grid.py`). On the main grid
  Sojourn uses the user's old SL account; credentials stay in `local/` and are typed by the user. The grid's terms and
  MFA are answered by the person, never automatically or by the assistant. Main-grid rules for third-party viewers:
  `docs/tpv-policy.md`.
- **The GPU is shared** with other sessions: one GPU-heavy agent at a time, real-time render tests short. Its driver
  resets under long heavy load (the studio's `docs/gpu-resets.md`); Sojourn restarts itself where the person was
  (`docs/client.md` > When the GPU is lost).
- **Every setting is on the agent API** (`SojournSettings`) and so reachable by the assistant's `settings` tool; a
  batch registers its own (`docs/viewer-gaps.md` > How the batches share the code).
- **Never junction `local/` into a git worktree**: `git worktree remove` follows the junction and deletes the main
  tree's `local/` (it wiped half of it on 2026-10-30). If one exists, `cmd /c rmdir <worktree>\local` first.
- **This CLAUDE.md and most docs are published** in the public snapshot (all but the board, `docs/sittings.md` and a
  few docs `tools/publish/rules.py` lists): write roles, not private individuals' names, anywhere in docs, comments, tests
  and fixtures. Never push this repo to the public remote. `docs/publishing.md`.

## Layout

- `src/` .NET 10 solution:
  - `Sojourn.Core` Multitude's session core (login, world model, LEAP APIs, appearance); `Sojourn.Render` its Vulkan
    port of the viewer's pipeline (`Realtime/FrameRenderer.cs` the per-frame renderer).
  - `Sojourn.Session` the session host the VR client and Access share, never touching a window, OpenXR or drawn menus
    (`SessionHost.cs` starts it): `Bridge/` the agent API, `Assistant/` host, panel model, confirmations, sidecar,
    `Voice/`, `Audio/`, `Social/`, `Private/` (incognito), `Media/`, `Capture/`, `People/`, `Land/`, `Building/`,
    `Inventory/`, `Map/`, `Objects/` (the person's own actions on a thing), `Access/` (screen reader wiring), `Control/`
    (VrSettings, SojournSettings, the session's APIs), `Offscreen/` (the renderer out of sight), `Collision/` (the
    collision model without a GPU).
  - `Sojourn.Client` SojournVR.exe: window, controls, frame loop, bench; `Xr/` and `Control/VrPilot*.cs` the headset;
    `Login/`; the drawn menus and what's drawn over the world (`Assistant/`, `Control/SettingsMenu.cs`, `Social/`,
    `Hud/`, `Map/`, `Beacons/`, `Showing/`, `Media/`, `Private/`, `Access/`, `Things/` a thing's card); the OpenXR
    loader in `native/`.
  - `Sojourn.Access` Sojourn Access (WinForms on Sojourn.Session); `Sojourn.Launcher` Sojourn.exe, how a person starts
    Sojourn (WPF); `Sojourn.SessionProbe` a console host (`tools/probe.py`); `Sojourn.Voice.Native` voice's C++ shim
    over LL's libwebrtc and world audio's mixer (`tools/voice.py`); `Sojourn.Media.Native` media's helpers over CEF
    and libVLC (`tools/media.py`).
- `assistant/` the assistant's Python side, started by the client: hearing, the Claude sessions and tools, `voice.py`
  (spoken replies), `harness.py` (what its sessions may do), `home.py` (memory), `jobs.py`, `build.py`, `provider.py`,
  `prompt*.md`. Its home is `local/assistant/home`, scratch `local/assistant/work`.
- `tools/` build, run and test scripts: `tools/README.md` is the map.
- `docs/` (Multitude's `docs/render.md` still describes the pipeline itself):
  - Running and driving: `client.md` (running, controls, agent API, architecture, local grid, gotchas), `vr.md` (the
    headset: controls, the rig, your body, testing in the XR Simulator), `launcher.md`, `dev-mode.md` (a Claude Code
    session as the assistant) and `driving.md` (its how-to for sittings), `sittings.md` (what they left open, checks
    for when there's time).
  - The assistant: `assistant.md` (the entry point: providers, hearing, Claude, permissions, memory, jobs,
    confirmations, incognito's rules), `assistant-panel.md` (the panel, the settings menu, showing things, the reader),
    `assistant-incognito.md` (how incognito is enforced).
  - Seeing and speed: `rendering.md` (what the renderer adds to the port), `performance.md` (measurements, busy places,
    the load governor, threads, the plan to 90 Hz).
  - Features: `social.md`, `people.md`, `hud.md`, `media.md`, `audio.md`, `voice.md`, `map.md`, `building.md`,
    `inventory.md`, `land.md`, `safety.md`, `shared-tracking.md` (whether others see your tracking).
  - The viewer and its rules: `viewer-gaps.md` (what we lack next to the official viewer and Firestorm, the batches
    closing it), `tpv-policy.md`, `publishing.md`.
  - Without sight: `access.md` (Sojourn Access's plan), `access-app.md` (as built), `access-testing.md` (its scripted
    and guided tests), `screen-reader.md` (the VR client's screen reader output); research in
    `accessibility.md`, `radegast.md`.
- `local/` gitignored: the local grid (`local/grid`, ports 9700-9703 and 9709), the client's state, caches and logs
  (`local/client`; `local/main` for the main grid), the assistant's venv and files, Meta's XR Simulator
  (`local/xrsim`), the OpenXR SDK's validation layers, libwebrtc and the voice server, NVDA's controller client
  (`local/nvda`), gitleaks. Every session's console goes to `logs/` in its base dir.

## Credit

Built by Claude (Opus 5.5 so far). The user wants credit for whatever their role turns out to be (2026-10-20): so far
the idea, the direction and the key decisions; when something goes public, describe their role as it stands then.
