# Building Sojourn: a guide for the agent doing it

This is for an AI agent (Claude Code or similar) that a person has asked to build Sojourn or Sojourn Access from this
source on their Windows PC and get it running. It takes you from a PC with nothing installed to Sojourn in their
headset, or Sojourn Access under their screen reader. People reading this themselves: README.md is for you, and the
short version is to ask your own Claude to clone this repository and follow this file.

There are no installers yet, so building from source is the way to get Sojourn for now. One script,
`tools/setup.py`, does the fetching and building; your part is the prerequisites, reading what fails, and helping the
person through the first start.

## What's yours and what's the person's

The person does these themselves, and you never do them for them:
- **Their Second Life login.** They type their username and password into Sojourn's launcher (or Access's login form).
  Don't ask for the password, don't put it in a command or file, and never read `local/secrets/` or any file named
  `mfa_hash` or holding a login. Linden Lab's Third Party Viewer Policy says passwords go nowhere but Linden Lab, and
  your context lives on your provider's servers (`docs/tpv-policy.md`).
- **The grid's questions.** Linden Lab's Terms of Service and authenticator (MFA) codes are answered by the person,
  in the launcher, the client or Access's login form. Nothing may answer them automatically, you included.
- **Their AI provider key.** They paste it into the launcher's key box themselves (Access: Alt+0, the key row). The box
  only keeps a key that came from their own keyboard or mouse, so you can't put one there, and a key shouldn't pass
  through your conversation either.
- **Installing system software.** Ask before running an installer; the Windows security (UAC) prompts are theirs to
  answer.
- **The headset.** Putting it on, connecting Link, and Windows Firewall's question on the first login.

You don't operate their Second Life account. Once Sojourn is running, its own assistant acts for them inside the
client, at their direction while they're there, with confirmations for spending, giving, deleting, permissions and new
scripts. Logging in for them or driving their avatar yourself would make the account a bot under Linden Lab's policies,
and is outside what Sojourn is for. Don't change the client's safety parts (the confirmations, the assistant's harness
and guards, the login handling) to get a build working: a build never needs it. If the person asks for such a change,
explain what the part protects; the copy is theirs.

## 1. Ask first

- **Which app.** Sojourn is the VR viewer, for sighted people with a PC VR headset. Sojourn Access is for using Second
  Life without sight, with a screen reader (section 7). Almost nobody wants both.
- **The headset** (Sojourn): which one, and how it connects. A Quest runs over Link or Air Link with Meta Horizon Link
  on the PC; other headsets through SteamVR. Sojourn runs on a monitor too, but the desktop isn't designed yet: it's
  a fallback.
- **The assistant**: whether they want it, and with which provider (section 6). Both apps work fully without it.
- **Where to put it**: a folder outside OneDrive (which would sync gigabytes of build output), such as
  `%USERPROFILE%\sojourn`. A full setup takes roughly 6 GB.

## 2. Prerequisites

Windows 10 or 11, x64, a GPU with Vulkan 1.3, and:

| What | Needed for | Get it |
|---|---|---|
| Git | cloning | `winget install Git.Git` |
| Python 3.11 or newer | running `tools/setup.py` | `winget install Python.Python.3.12` (3.12 also suits the assistant) |
| .NET 10 SDK | both apps | `winget install Microsoft.DotNet.SDK.10` |
| Visual Studio 2022 Build Tools, C++ workload (with its recommended CMake and Windows SDK) | voice chat and world audio, media | `winget install Microsoft.VisualStudio.2022.BuildTools --override "--wait --passive --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"` |
| uv, or Python 3.12 | the assistant | `winget install astral-sh.uv` (it fetches its own Python 3.12) |
| An OpenXR runtime | the headset | Meta Horizon Link for a Quest, or SteamVR, set as the active OpenXR runtime in its settings |

`python tools/setup.py --check` reports every one of these (and a newer Visual Studio that isn't 2022: the native
parts are built with 2022, which installs beside it). After installing anything, start a new terminal so PATH
updates.

## 3. Clone and set up

```
git clone https://github.com/studio-quire/sojourn
cd sojourn
python tools/setup.py --check            # what's there, what's missing, what a run would do; changes nothing
python tools/setup.py --for sojourn      # or --for access; no --for builds both
```

It builds, in order:
- **voice**: Linden Lab's build of libwebrtc (~80 MB download, ~800 MB unpacked) and Sojourn's native voice shim.
  Without it the apps run with no voice chat and no world sound.
- **media** (Sojourn only): CEF and libVLC (~300 MB) and the helpers that show web pages and video on surfaces.
- **nvda** (Access only): NVDA's controller client, a small download.
- **sojourn** / **access**: the apps themselves (`dotnet build`, Release; NuGet restores the libraries).
- **assistant**: a Python venv with the Claude Agent SDK, local speech recognition and spoken replies, and their models
  (~1.5 GB).

Downloads are pinned and checked against their hashes. Everything fetched or built goes under `local/` (ignored by
git), apart from the apps' `bin/` and `obj/` folders. Most of the time goes on downloads and the C++ builds: a first
run takes from about five minutes to half an hour, longer than most agents' command timeouts, so run it in the
background (or with the longest timeout you have) and tell the person it's going; a rerun takes seconds.
`--skip voice,media,nvda,assistant` leaves optional parts out. It ends with a summary of each part (built, already
there, skipped, blocked by a missing prerequisite, or failed) and how to start what was built; the exit code is 0 only
when everything asked for is there. Running it again is always safe: finished parts are kept, so after a failure, fix
the cause and rerun.

## 4. When a step fails

The output above the summary has the failing tool's own error; the summary names the part and the doc to read.
- **A download failed or a hash didn't match**: the bad file is deleted, so rerun. If a hash keeps failing, the file
  changed upstream: don't edit the pinned hash to force it through; tell the person, and it's worth an issue here.
- **CMake can't find Visual Studio**, or the C++ build fails at once: the C++ workload or its Windows SDK is missing
  (`--check` says which). A configure that failed leaves its folder behind, so after fixing the tools delete
  `local/voice/build` (or `local/media/build`) and rerun.
- **dotnet restore fails**: NuGet can't reach nuget.org (a proxy, offline). The .NET SDK's own errors say which.
- **The assistant's packages won't install**: usually a Python too new for the speech packages' wheels. Install uv or
  Python 3.12, delete `local/assistant-venv`, and rerun.
- **Anything else**: the part's tool can be run alone for a closer look: `python tools/voice.py fetch|build`,
  `python tools/media.py fetch|build`, `python tools/assistant.py setup|check`, `python tools/screenreader.py setup`,
  `dotnet build src/Sojourn.Client/Sojourn.Client.csproj -c Release`. Their docs are `docs/voice.md`, `docs/media.md`,
  `docs/assistant.md`.

An optional part that won't build can be skipped for now: the person can start using Sojourn and you can come back to
it. If you change the source or the tools to make something build on their PC, tell them what you changed. An issue
here helps everyone, but opening one is public, from their GitHub account: ask first, and leave out their names,
account, paths, keys and any log holding other people's chat.

## 5. Running Sojourn

Before starting it for the headset, have the person connect Link (or start SteamVR), with that runtime active.

The launcher, `Sojourn.exe`, is how a person starts Sojourn:
```
python tools/launcher.py run              # in the headset
python tools/launcher.py run --desktop    # on the monitor instead
```
or `src\Sojourn.Launcher\bin\Release\net10.0-windows\Sojourn.exe` directly (with .NET installed machine-wide, as
winget does; otherwise `dotnet` with `Sojourn.dll` beside it). Started from a Claude Code session any other way, the
launcher and viewer open without taking focus (so test runs never disturb anyone); `launcher.py run` starts them for
the person. `python tools/setup.py --shortcuts`
adds Start menu entries (ask first).

In the launcher the person adds their account (username, the grid: Second Life; their password; "remember password"
is off unless they turn it on) and where to start, and in its settings (the gear): standing or seated, a graphics
preset, and the assistant's provider and key. Launch starts the viewer (`SojournVR`) in the headset; the Terms of
Service or an authenticator code, if the grid asks, come up in the launcher for them to answer. Their settings, saved login and caches stay
under `local/` in this folder.

Controls are in `docs/vr.md` (> Controls) and the assistant's in `docs/assistant.md` (> Using it): in the headset, hold
the right grip and speak. The viewer can also be started without the launcher
(`dotnet src/Sojourn.Client/bin/Release/net10.0/SojournVR.dll --login --vr --base-dir local/main`; options in
`docs/client.md`), but the launcher is what's maintained for people.

To update later: `git pull`, then `python tools/setup.py` again.

## 6. The assistant's provider

The assistant is optional. Its provider is chosen in the launcher's settings (Access: Alt+0), and the person pastes
the key there. Public builds offer:
- **An Anthropic API key** (console.anthropic.com), billed by Anthropic for what's used.
- **An OpenRouter key** (openrouter.ai) and a model. Claude models through it can do everything; other makers' models
  work with caveats (`docs/assistant.md` > What works with which). "No data collection", on by default, routes only to
  providers whose stated policy is not to keep or train on prompts.
- **The studio relay**: a personal token from Studio Quire for people it covers, such as testers. Not something to
  sign up for here.

Public builds don't offer signing in with a Claude subscription: Anthropic's terms for the Agent SDK don't let
third-party products offer claude.ai login or use plan limits without Anthropic's approval. A person who wants to use
their own subscription can ask you to add it to their own copy. That's their decision, under their own account's
terms, and the change stays in their copy. It's two switches, both false here: `ClaudeOffered` in
`src/Sojourn.Session/Assistant/ProviderKeys.cs` (the launcher and Access then list it) and `CLAUDE_OFFERED` in
`assistant/provider.py` (the assistant then uses the account Claude Code is signed in to on this PC).

Before they turn it on, point them to the privacy section of `docs/download.md`: what each request sends to their
provider (their request, what the assistant looks up to answer, pictures of their view when it needs to see).
Speech recognition is local by default: nothing they say leaves the PC for it unless they choose cloud hearing.

## 7. Sojourn Access

```
python tools/setup.py --for access
```
builds voice, NVDA's controller client, Sojourn Access and the assistant (no media). Test builds that need no building
are also published at https://github.com/studio-quire/sojourn-access/releases, if the person would rather.

The person is likely using a screen reader, so keep your messages short and in plain text, give keys rather than
places on the screen, and say when a long step starts and when it ends. UAC prompts appear on the secure desktop,
which NVDA reads when it's installed rather than portable.

Start it as `src\Sojourn.Access\bin\Release\net10.0-windows10.0.22621.0\SojournAccess.exe` (with .NET installed
machine-wide; otherwise `dotnet SojournAccess.dll` in that folder). A Start menu entry (`setup.py --shortcuts`)
lets them start it from the Windows key by name, so it opens in front with the focus. In the login form Second Life
is first in the grid list; they type their username and password, and answer the Terms of Service or an authenticator
code there. Once logged in, F1 lists the keys; `docs/access-app.md` has them all. The assistant's provider is in
Alt+0's first rows, where Enter on the key row opens a box for the key. Without a GPU that can run the view, Access
still works with `--no-render` (no descriptions of the view; walking uses its collision model, which needs no GPU).

## Where to read more

`README.md` (the overview), `CLAUDE.md` (the index the models working on Sojourn read first), `docs/client.md`,
`docs/vr.md`, `docs/launcher.md`, `docs/assistant.md`, `docs/access-app.md`, `docs/tpv-policy.md`, `docs/download.md`.
Issues: https://github.com/studio-quire/sojourn/issues, and Sojourn Access's at
https://github.com/studio-quire/sojourn-access/issues.

Written by Claude Opus 5.5 at Studio Quire, 2026-10-31.
