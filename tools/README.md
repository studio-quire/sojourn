# tools/

Agent-facing. Each script's docstring has its commands; this is the map. Tests run on the local grid (never the main
grid unless the script says so).

## Building and running
- `setup.py`: a complete build from a fresh clone on any Windows PC (prerequisites, voice, media, NVDA's client, the apps, the assistant; `--check` reports only); the public BUILDING.md (`publish/sojourn/`) guides a person's own agent through it. `buildenv.py`: where dotnet, VS 2022's C++ tools and CMake are, for it and the build scripts.
- `client.py`: build and run the VR client (`run`, `vr`, `vr-sim`, `--logs`, `--login`, `--base-dir`).
- `launcher.py`: the launcher (Sojourn.exe): build, run, its states drawn offscreen, `check` its terms and MFA path on a hidden desktop.
- `access.py`: Sojourn Access: build, run, `package` (docs/access-app.md > Packaging; `fetch-lavapipe` first, the view's software driver), the visitor bot's commands, its test on a hidden desktop.
- `probe.py`: Sojourn.Session without the VR client: two console hosts log in, answer SojournSettings and hear each other; no GPU.
- `crossing.py`: a seated region crossing in the visitor's kart through one console host (in the new region, still seated, connected; `--sound` its loop heard from the kart; `--noack MS` the old 60 s wait emulated); no GPU.
- `dev.py`: dev mode: start, watch, say, shot, log, call, restart (docs/driving.md).
- `grid.py`: the local grid: start/stop, accounts, console, `scene light|dense`, `voice up`.
- `fixtures.py`: the test inventory, fountain and second account.
- `openjpeg.py`: builds the vendored J2C decoder. `voice.py`, `media.py`: voice's shim and media's helpers (fetch, build, tests).
- `publish.py`, `publish/`: the public snapshot (docs/publishing.md).
- `relay.py`, `relay/`: Studio Quire's relay for the assistant (deploy, tokens, spending, the logs testers send; `relay/README.md`). `logcheck/`: the filter for those logs on a synthetic log (`dotnet run`).

## Headset and performance
- `vrlog.py`: a headset session's logs, summarised. `jitter.py`: how steady the view is, from a vr.csv.
- `lagtest.py`: lag on purpose in the headset, the pacing switches on the menu, its report.
- `mirrorshot.py`: what the headset really shows (Meta's compositor mirror, captured without focus, `--burst N`).
- `motion.py`: walking, walls, steps, seats and looking around in the XR Simulator. `hands.py`: hands over the menus there. `keyboard.py`: the headset's keyboard there.
- `forest.py`: the synthetic SL-like forest on the performance grid; `crowd.py`: its NPC crowd of rigged-mesh avatars; `teleports.py`: a long session's teleports there.
- `perfrun.py`: timed runs on the performance grid, summarised (`--as`, `--port`, `--base` for a second session).
- `gpuarrival.py`: arrival's GPU cost from a `SOJOURN_GPU_LOG` run.
- `cputrace.py`: where a running client's CPU goes. `alloctrace.py`: where its allocations come from. `heapwho.py`: what holds its managed heap.
- `render_scenes.py`: rendering test scenes (mirrors, mist, a flower field, lights, particles). `animesh.py`: an animated object.

## Features on the local grid
- The assistant: `assistant.py` (setup, check, test requests typed, from WAVs or synthesised speech, timings); `assistant_guard.py` (the harness refuses what it should); `provider_check.py` and `provider_mock.py` (providers without a real key); `soniox_mock.py`; `model_tap.py` (a proxy keeping everything Claude Code sends to the model, and each answer's usage); `private_leak.py` (incognito's leak test); `costlab.py` (Access with its assistant on the relay through the tap: what each kind of use costs) and `describe_lab.py` (describing's captures replayed to each model and setting, judged blind; `docs/assistant-cost.md`).
- People and social: `people.py`, `people2.py` (batch B2), `social.py` (B4), `notices.py` (notices and the reader), `offline_im.py`, `avatars.py` and `avatarfx/` (others' beams, lip sync, the avatar fixes).
- World: `audio.py` (world audio), `media_fixtures.py`, `hud.py`, `beacons.py`, `map.py` (`--headset` in the simulator), `land.py`, `building.py`, `ao.py` and `aocheck/` (inventory and the AO), `safety.py`, `report.py` (abuse reports: never on the main grid, they reach Linden Lab).
- Voice: `voice.py` tests, `janus_name_proxy.py` (the local voice server's plugin-name shim), `headset_friend.py` (a talking companion that records what it hears).
- The menu: `menu.py` (every section and page drawn, rows pressed and read back, Esc's layers; `--headset`).
- Without sight: `screenreader.py` (NVDA's controller client into `local/nvda`; the read-out test, `uia_listen.ps1` standing in for Narrator); `invbench.py` (Access with 20,000 or 60,000 items: how long the window freezes); `inputcheck/` (judging the person's own keys, a screen reader's pass-through included); `quireuia/` (a program on a hidden desktop, what a screen reader is told, driven by UI Automation); `visitor/` (Access Visitor, a bot with a scripted fixture box: chat, IMs, menus, llTextBox, permissions, offers).
- `winmouse.ps1`, `winkey.ps1`: mouse and keys into the window without the real cursor.
