# Sojourn and Sojourn Access

Two Second Life viewers from one codebase:

- **Sojourn** is a VR-first viewer for PC VR headsets (built and used with a Quest 3 over Link), with an optional AI
  assistant that operates the viewer for you by voice or typing: inventory, outfits, search, building, dialogs,
  settings. It renders with Vulkan and OpenXR through a port of the official viewer's deferred pipeline. It also runs
  on a desktop monitor, though the desktop isn't what it's designed for yet.
- **Sojourn Access** is for using Second Life without sight: a window of standard Windows controls for a screen reader
  (NVDA through its own API, others through UI Automation), everything by keyboard, Radegast's `//` commands, people
  and things placed around you by clock-face bearings, walk mode with sounds and warnings, a way to reach every part
  of a linkset and its faces, and, with the assistant, descriptions of places, people and pictures. It works fully
  without the assistant. Test builds and their issues live at
  [studio-quire/sojourn-access](https://github.com/studio-quire/sojourn-access).

Both share the session core (login, the world model, inventory, voice, world audio) and the renderer; Access keeps
the renderer running out of sight for descriptions and its collision model.

This software is not provided or supported by Linden Lab, the makers of Second Life.

## Status

Early. Both are under daily development and change quickly; expect missing features and bugs. Sojourn has no public
builds yet. Sojourn Access has occasional test builds for Windows (see its repository).

Sojourn is built by Claude models (Anthropic's AI; Claude Opus 5.5 so far) at Studio Quire, which also directs Sojourn
Access's design. Shoalstone, the person behind Studio Quire, had the idea and gives Sojourn its direction and key decisions. The
design notes in `docs/` and the index in `CLAUDE.md` are the ones the models work from, published as they are.

## Building

The easy way: ask your own Claude (Claude Code or a similar agent) to clone this repository and follow
[BUILDING.md](BUILDING.md). It's written for the agent: it installs what's missing (asking you first), runs
`tools/setup.py`, which fetches and builds everything, and helps you through the first start. Your Second Life password,
the grid's terms and your AI provider's key stay with you.

**With your Claude subscription**: Sojourn doesn't offer signing in with a Claude account. Instead your own Claude
Code session can be Sojourn's assistant: it starts Sojourn in **dev mode**, hears what you say or type to the
assistant, operates the viewer for you and answers on its panel, while Sojourn itself signs in to nothing. This works
from a build of this source and from an installed copy; ask your Claude to read [DEV-MODE.md](DEV-MODE.md) and start
it. Otherwise the assistant runs on an Anthropic or OpenRouter key of your own.

By hand: `python tools/setup.py --check` says what's missing, and `python tools/setup.py` builds everything (BUILDING.md
explains both). The rest of this section is the plain build without the optional parts.

Windows 10 or 11, x64. You need:

- the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0);
- a GPU with Vulkan 1.3 (both apps render; Access out of sight);
- for VR, an OpenXR runtime with Vulkan support (Meta's for Quest Link, or SteamVR);
- Python 3.11+ for the scripts in `tools/` (optional for a plain build).

```
dotnet build src/Sojourn.Client/Sojourn.Client.csproj -c Release
dotnet build src/Sojourn.Access/Sojourn.Access.csproj -c Release
```

NuGet restores the libraries (LibreMetaverse, Silk.NET, SkiaSharp and others; `THIRD_PARTY.md`). The OpenXR loader
and an OpenJPEG build come with the source as DLLs.

Running:

```
dotnet src/Sojourn.Client/bin/Release/net10.0/SojournVR.dll --login          # desktop, with the login screen
dotnet src/Sojourn.Client/bin/Release/net10.0/SojournVR.dll --login --vr     # in the headset
src/Sojourn.Access/bin/Release/net10.0-windows10.0.22621.0/SojournAccess.exe
```

Options, controls and how the client fits together: `docs/client.md`, `docs/vr.md`. Sojourn Access's keys, areas and
commands: `docs/access-app.md`.

### Optional parts with their own build steps

These are left out of a plain build; each app runs without them. `tools/setup.py` does all of them.

- **Voice chat and world audio** (`src/Sojourn.Voice.Native`): a C++ shim over Linden Lab's build of libwebrtc,
  fetched and built by `python tools/voice.py fetch` and `build` (Visual Studio 2022 Build Tools, CMake);
  `docs/voice.md` > Building. Without `sojourn_voice.dll` there is no voice and no world sound.
- **Web pages and video on surfaces** (VR client only; `src/Sojourn.Media.Native`): helpers over CEF and libVLC,
  fetched at pinned versions and built by `python tools/media.py fetch` and `build`; `docs/media.md`.
- **NVDA's controller client** for Sojourn Access: `python tools/screenreader.py setup` puts it in `local/nvda`;
  without it, Access speaks through UI Automation only.
- **The assistant** (`assistant/`): a Python sidecar on the Claude Agent SDK, with local speech recognition (Whisper or
  Parakeet) and optional spoken replies; `python tools/assistant.py setup`, and an Anthropic or OpenRouter key of your
  own. `docs/assistant.md`.

Many of the test scripts in `tools/` assume the studio's machine: a local OpenSim grid (`tools/grid.py`, ports
9700-9709) set up from another of the studio's projects that isn't published, and some the .NET SDK at
`%LOCALAPPDATA%\dotnet-sdk`. The build scripts (`setup.py`, `client.py`, `launcher.py`, `access.py`, `voice.py`,
`media.py`, `assistant.py`) find the tools wherever they're installed. The apps themselves log in to Second Life or any
OpenSim grid you give them.

## Documentation

- `CLAUDE.md`: the index, decisions and where things stand.
- `docs/access.md` and `docs/access-app.md`: Sojourn Access, the plan and as built; `docs/screen-reader.md`,
  `docs/radegast.md`, `docs/accessibility.md` for the research behind it.
- `docs/client.md`, `docs/vr.md`, `docs/rendering.md`, `docs/performance.md`: the viewer and its renderer.
- `docs/assistant.md`: the assistant, its confirmations and what it may and may not do; `DEV-MODE.md`, your own
  Claude as the assistant.
- `docs/tpv-policy.md`: how Sojourn keeps to Linden Lab's Third Party Viewer Policy.
- `docs/download.md`: the disclosures, support statement and privacy policy that go with any build we hand out.

## Licence

Sojourn is released under the GNU Lesser General Public License, version 2.1 only (`LICENSE`), the licence of the
Second Life viewer code it ports. The other code and assets it includes, and the libraries it builds against, keep
their own licences: `THIRD_PARTY.md`.

Second Life and Linden Lab are trademarks of Linden Research, Inc. Sojourn is not affiliated with or endorsed by
Linden Lab.

## Issues

GitHub issues are welcome, for Sojourn here and for Sojourn Access in its own repository. There's no formal support.
