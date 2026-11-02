# The launcher

How a person starts Sojourn: `src/Sojourn.Launcher`, built as **Sojourn.exe** (WPF); the VR client it starts is
`src/Sojourn.Client`, built as **SojournVR.exe** (the user's call, 2026-10-29: the launcher is what people start). Written
by Claude Opus 5.5, 2026-10-29 and 30. Agent-facing.

## What it does

- **The login**: saved accounts (the client's own store, `LoginStore`: the repo's `local/secrets/logins`, else
  `%LOCALAPPDATA%\Sojourn\secrets\logins`), or a new one (username, its grid: Second Life / Beta / another grid's login URI);
  the password hashed straight from the password box's protected buffer ("$1$" + MD5, never a string), remember off by
  default ("remember password"), turned off it forgets a kept password at once (the field greyed, saying "password
  remembered", while one is kept); forget an account by × twice; where to start (last, home, a
  place: a region or any SLurl, kept per account).
- **Before going in** (the gear): standing or seated; graphics (light, balanced, full: `GraphicsPresets.cs` in
  Sojourn.Session, linked here and offered the same in the headset's menu as view > quality; draw distance, shadows
  and their cap, reflections, mirrors and their sharpness, detail, far shadows, whole avatars, the eyes' resolution;
  settings that match none show "yours" and are left alone until a preset is picked; a first start with no settings
  file writes balanced, the 1536 shadow cap `client.py vr` uses); and the assistant, chosen **only here** for the VR
  client (the user, 2026-10-29; the headset's menu shows the model choices for whatever is chosen):
  - the provider (`assistantProvider`, as `assistant/provider.py` has them): claude (a Claude plan, offered only in runs
    from the repo, `ProviderKeys.ClaudeOffered`: released builds have no Claude-account sign-in, the user, 2026-10-30),
    anthropic, openrouter, and relay (Studio Quire's, shown only once chosen or its token is kept);
  - the **key box**: paste an Anthropic or OpenRouter key or a relay token (Ctrl+V, or Enter after typing): its shape
    says which, it's kept by `ProviderKeys` (DPAPI, linked here) and that provider chosen, the box cleared; a kept
    key greys the box out as a remembered password does. Kept only from the person's
    own press (> The person's own press), and only if everything in the box came from their own keys or mouse; never
    shown back (the box's hint says "key kept"), never logged; × twice forgets it;
  - with OpenRouter, the **model** box: any id typed, or one from the list behind the ▾ (Claude Haiku · Sonnet · Opus,
    then each OpenRouter family's current model, each row its tag and name; typing two letters or more lists the
    matching models instead), the other half under the box (a tag's name; Claude's three tags), all from
    `OpenRouterCatalog` (linked; read off the UI thread, cached, refreshed at most every 3 days: docs/assistant.md >
    Switching it on); and **no data collection** (on by default);
  - **auto mode** (on by default; docs/assistant.md > Auto mode off).
  Written into the client's own `vr-settings.json` in its base dir, only those keys, everything else kept.
- **Launch** starts the VR client (`--login --login-resume --base-dir B --vr`) with the login handed over as a lost GPU's
  restart hands it (`LoginStore.HandOver`: DPAPI, read and deleted by the client, ignored after two minutes; the
  remember choice goes with it). Its console goes to `B/logs/launcher-console.log`; no console window.
- **While it runs**, Launch's place shows its state: starting, logging in, terms or authenticator code, in the headset or on the
  desktop, closing; the grid's terms or MFA code are answered in the card (> The grid's questions at the desk); a
  stop key closes the client's window as the person would (ended after 20 s if it hasn't). A run that starts itself again (a lost GPU, back to its login screen) is followed. A failed login
  stops the run and leaves the grid's reason at the foot, the form as typed. A crash leaves "stopped unexpectedly" and
  a log key (the session's log, or the console's if it died with an unhandled exception), and offers a report (> After
  a crash).
- **The language** (the globe at the foot's right, findable in any language; Claude Opus 5.5, 2026-11-01): "as
  Windows" (what it resolves to under it) or a language by its own name, in a list above the globe; the launcher's and
  the client's one setting, `uiLanguage` in `vr-settings.json` (`auto` or a code), applied at once, not while the client
  runs. Not a row behind the gear: that page is full with OpenRouter's rows, and someone stuck in a language they can't
  read finds a globe sooner than a row (kept so, the user, 2026-11-01). Words: `Words.cs` (> Languages).
- Sojourn Access isn't started from here: the two ship separately (the user, 2026-10-29: nobody would use both).
- Base dir: `local/main` in the repo (the person's own headset settings and SL cache), `%LOCALAPPDATA%\Sojourn` packaged.

## Why the login happens in the client

The launcher only gathers the login; the client logs in. The login reply makes the session (the circuit, the seed
capability) and only the process that will live in it can use it: LibreMetaverse can't hand a live session across
processes, and logging in twice (once in the launcher, then again) is slower and trips SL's still-logged-in presence.
Credentials never reach the assistant: the hash exists in the launcher's memory, the DPAPI handover file and the
client's memory, all outside the assistant's reach (`assistant/harness.py` denies `local/secrets` and
`%LOCALAPPDATA%\Sojourn\secrets`).

## The grid's questions at the desk

The user's call (2026-10-29): when the grid asks for its Terms of Service (or a critical message) or an MFA code, the
person answers at the launcher rather than typing in the headset. The client still asks the grid and still shows its
own sheet (in the headset or the window); whichever answers first counts, and a client started without the launcher
works as before.
- **Client to launcher**: the state file (`LaunchState.cs`) carries the question: `{kind, text}` for terms (the text
  cleaned of HTML as the sheet shows it), `{asks}` for a code (2 and on: the last didn't work), and `answers`: whether
  the client takes answers from this launcher.
- **Launcher to client**: an anonymous pipe per run (`ClientRun`). Its read end is inherited by the client
  (`--launcher-answers H`), which makes it non-inheritable at once (`Login/LauncherAnswers.cs`), so nothing it starts
  (the assistant's sidecar, its Claude Code sessions, helpers) holds it; only a restart of the client (a lost GPU, back
  to the login screen) gets a copy, made the moment before it starts. The write end exists only in the launcher. An
  answer is one JSON line (`{terms, accept}` or `{code, remember}`), applied by `LoginSheet.FromLauncher` only to the
  question showing; codes are never logged.
- **Only the person's own press**: Continue and Decline (and Enter in the code box) act only on the person's own keys
  or mouse (> The person's own press); never the agent API or the assistant (docs/tpv-policy.md), and the shell guard
  refuses commands naming the launcher, `--launcher-answers` or the client's exe. `SOJOURN_TEST_INPUT=1` lets tests
  press as for the client.

## The person's own press

`RealPress.cs`, `HandHooks.cs`, `PressJudge.cs` (Claude Opus 5.5, 2026-10-30). The terms, the code and a kept key go
only from the person's hand: the assistant runs commands on this PC, so a posted message, injected input (SendInput)
or a UI Automation call must never count on its own.
- Every message the window's thread takes is judged as it's taken (`ComponentDispatcher.ThreadFilterMessage`, before
  WPF handles it); `RealPress.Now()` answers for the one being handled (under a second old, not inside a message
  another thread sent). Anything else through the queue (dispatcher callbacks, timers, UI Automation's work) isn't a
  press.
- Windows' word first: what `GetCurrentInputMessageSource` calls hardware counts. Asked inside WPF's handling, it says
  unavailable even for the person's real keys (with it alone, no real press ever passed, 2026-10-30), so for anything else
  low-level hooks (`HandHooks`, Access's KeyHook pattern) note the hand's presses: the keyboard's while the launcher is
  the active app, the mouse's also while the pointer is over its window (so the click that brings it to the front is
  seen letting go), on their own thread, put in front of other hooks every 2 s; only presses Windows didn't mark
  injected (LLKHF_/LLMHF_INJECTED and their lower-integrity flags), with a window of ours in front.
- `PressJudge`: a message not called hardware counts only as the message of such a press: the same key, down or up,
  into the window that was in front (a button: down or up at the same screen point, within 2 px), within a second,
  each press good for one message (a hardware message takes its press too). So a program's input never counts on its
  own; at most, raced in right behind the person's press, it stands for that same press once. An injected key on a
  press counts (a screen reader re-sending a key it took, as NVDA does). A character counts only as the character of
  the counted key-down just before it.
- The key box: text put in other than by the person's own keys or mouse (UI Automation setting it, posted characters)
  spoils it, and the person's Enter then clears the box and asks for the key again ("paste the key again").
- `SOJOURN_INPUT_LOG=path`: each key, character and click's judgement and the hooks' presses, never which character key
  (with Ctrl held it's named, for Ctrl+V).
- Not covered, as for Access: software running as administrator or with UI Access, or a virtual keyboard driver (Windows
  calls their input hardware). Touch and pen aren't counted.
- Not Access's files shared: Access's judge also settles focus changes, refuses posted keys and has no mouse hook; the
  launcher needs mouse buttons and posted-looking messages matched to presses (WPF's real input reads unavailable),
  and stays light, so it has its own small judge on the same hook pattern. `tools/inputcheck` checks both.

## After a crash

`CrashReportView.xaml(.cs)` (Claude Opus 5.5, 2026-11-01), by the person's `crash_reports` setting in
`vr-settings.json` (menu > about > crash reports; Firestorm's CrashSubmitBehavior): **ask** (the default) opens a window
over the launcher in its look, once per crashed run: "Sojourn stopped unexpectedly", the exit code in words, a note
(what happened, optional), what a report holds and where it goes, then send report (to the studio relay, with the
Studio Quire token if one is kept, else without), review (the filtered text, read-only), save instead (a file for a
GitHub issue) and not now. **always** sends it without asking, the foot saying whether it went; **never** leaves only
the foot's note. Send and save take only the person's own press (RealPress). The
report is `ProblemReport`'s (`docs/client.md` > About, problem and crash reports): the crashed session's log filtered as
Access's Send a log filters it, the exit code, and the runtime's last words from `launcher-console.log`. In runs from
the repo the client is started with the runtime's minidump on (`DOTNET_DbgEnableMiniDump`, into `dumps/`, never sent).
`launcher.py shots` draws it as `crash-report.png` and `crash-report-no-token.png`.

## The client's side

- `Login/LoginStart.cs` takes a handed-over login (logs "login: the account the launcher handed over").
- `LaunchState.cs`: with `SOJOURN_STATE_FILE` set (the launcher sets `B/launcher-state.json`), the client writes
  `{pid, state, headset, detail, log}` when it changes (starting, form, connecting, terms, code, failed, world, ending;
  four times a second at most, no garbage per frame). Restarted runs inherit the variable and write their own pid.
- `Starts` (in `Sojourn.Session/Login/LoginStore.cs`) parses starts for both the launcher and the login sheet.
- `Login/LauncherAnswers.cs`: the launcher's answers (> The grid's questions at the desk).
- The launcher compiles `LoginStore.cs`, `GraphicsPresets.cs`, `ProviderKeys.cs` and, for crash reports, `LogShare.cs`,
  `LogSend.cs` and `Release/ProblemReport.cs` itself (linked source) rather than referencing Sojourn.Session, so it
  starts at once and builds in a second.

## Languages

The launcher's words go through `Lang` (linked, its catalog `Sojourn.Session/Lang/launcher.json`). The XAML keeps
its English: `Words.Remember` walks a view's logical tree right after InitializeComponent, keeps each string's English
(TextBlock text, a control's string content, a TextBox's Tag hint, automation names) and shows it translated;
`Words.Translate`, after a change, re-translates those and lets go of any that code has set since. Code-behind sets its
own words with `Lang.T`/`F`, and the foot's note and the crash window's outcome are kept as a way to say them
(`Func<string>`), so on `Lang.Changed` each view re-runs what sets text (`LauncherView.LanguageChanged`). The windows'
and views' `Language` follows `Lang.Current.Tag`, so WPF picks Japanese and Chinese fonts. `ProblemReport`'s sentences
stay English (the client logs them, the assistant reads them) and are translated where the crash window shows them,
the relay's own words inside them left as they came. Longer languages: segments shrink their word to fit (a Viewbox,
down only), switches and labels wrap, hints trim, and the window grows past 660 only when a page needs it.
`launcher.py shots DIR --pseudo long` (40% longer, drawn as de) or `--pseudo cjk` (CJK filler, as ja) fills a made-up
translation in memory for checking the layout, and draws `changed-first`/`changed-settings` made in English then
changed, which should match `first`/`settings` pixel for pixel; `--lang CODE` draws a real one.

## Running and testing

```
python tools/launcher.py run                     # for the person: base dir local/main, the foreground
python tools/launcher.py run --base-dir local/launcher-test --desktop -- --no-assistant --minutes 1
python tools/launcher.py shots local/launcher-shots   # every state drawn offscreen at 2x over made-up accounts
python tools/launcher.py check                   # the grid's questions at the launcher, on a hidden desktop (below)
```

Started from a Claude Code session (CLAUDECODE=1) it shows without activating and never focuses a field; the client
it starts inherits that and stays in the background too. For tests set `SOJOURN_LOGINS` (and `SOJOURN_KEYS`) to a
scratch folder, and don't drive it by UI Automation on the visible desktop: Invoke on its WPF buttons brings the window
to the foreground (checked 2026-10-29). Use `--shots`, or a hidden desktop: `launcher.py check` runs it and the
desktop client on one ("sojourn-launcher", QuireUia's UI Automation there), faking the grid's ask with
`SOJOURN_FAKE_LOGIN_ASK`: first `tools/inputcheck -wpf` there (RealPress in a WPF window brought to the front: presses
given to its judge as the hooks would count while WPF handles them, once; posted keys and characters without them
don't); then, the launcher in front with its hooks on and its judgements traced, presses not from a person refused (a
key set into the key box by UI Automation or typed by posted characters and Enter, Enter posted in the code box, a
click posted on Continue, Continue invoked), then with `SOJOURN_TEST_INPUT=1` a code and the ToS answered at the
launcher and the client in world, the code in no log. SendInput can't be checked there (it reaches only the desktop
the person's input goes to). `tools/inputcheck` alone runs PressJudge's sequences (points, keys, characters).
`SOJOURN_FAKE_DEVICE_LOST=6` fakes a lost GPU, to see the restarted run followed. `shots` also draws OpenRouter with
Claude's three (`settings-openrouter-claude`), a tag (`settings-openrouter`) and the list on its own
(`settings-openrouter-list`; a popup isn't part of the view).

Checked: on the local grid (2026-10-29: new and saved accounts, stop, a wrong password, a lost GPU), and by the user on
Second Life on the desktop (2026-10-30: a new account, Launch, stop, and with real hands a pasted key kept and forgotten),
and Launch into the headset on Second Life (2026-10-31; `-- --dev --bridge-port 9720` passes dev mode through, so a
sitting's watch follows). Not yet: MFA and the ToS at the launcher on Second Life (the account has no MFA and the terms come only
when LL changes them; the user dropped it from the board, 2026-10-31: see it if it comes up), and a provider picked here
reaching the assistant in a full run.

Decided (Claude Opus 5.5, 2026-10-30): a program copying the person's press can at most act twice on that same press,
which every launcher action tolerates; no extra guard on focus moves (it would refuse Alt+Tab then a quick paste).

## Look

Light blue on glass (the user, 2026-10-30, after the teal, violet and peach glows felt wrong for Sojourn): clear glass
for everything, a light blue accent only on what acts (toggles, Launch, a field's rim when typing), slate ink, and
behind the glass a very light blue gradient running straight down (symmetry, also theirs). The glass card holds the
form, Launch is a lens of light blue, the state takes its place while running. The headset's panel and menus keep
their clear glass (PanelLook): the worlds behind them vary too much for one colour, so colours there stay the
person's choice of palette in the viewer (the user, same day). Archivo Light
for the wordmark, Segoe UI Variable otherwise. Words only for names, fields and a word or two of state, but always enough to say what each thing is (the user,
2026-10-30: "remember password", "authenticator code", "grid" over its choices, never a bare word left to guesswork).

## The package

`python tools/package.py` (Claude Opus 5.5, 2026-11-01): the launcher and the VR client with the assistant, installed,
updated and uninstalled by Velopack (the official viewer's installer too). Sojourn Access has its own package and
releases (the user, 2026-10-29; `docs/access-app.md` > Packaging, whose bundler this reuses).

- **What's in it**: both apps published self-contained into one folder (not single-file: they share one .NET runtime,
  WPF's and ASP.NET Core's beside it), stamped 0.1.0.N, N the repo's commit count (the footer, the login sheet and the
  login's version: `docs/tpv-policy.md` > Channel and version); the natives (`openxr_loader.dll`, `sojourn_voice.dll`,
  `openjp2.dll`, `shaderc_shared.dll`, Skia, GLFW), `Shaders/`, `Character/`, `Textures/`, `Fonts/`, `linden/`; media's
  helpers in `media/web` (CEF) and `media/video` (libVLC), where `MediaWiring` looks when there's no repo; the
  assistant as Access bundles it (embedded Python 3.12, compiled, `-B -E -s`, the SDK's own Claude Code, telemetry off)
  plus hearing and spoken replies (sherpa-onnx, faster-whisper, kokoro-onnx, soundcard, numpy); the models in
  `models/` (Parakeet, Kokoro, Whisper small.en's vocabulary for the name pass), checked by hash; `dev.py` and
  `DEV-MODE.md` for dev mode from an installed copy (`docs/dev-mode.md`); `Licences/`. Built from the commit's export
  (Releasing, below) through its own `SOJOURN_BUILD_ROOT` tree (`local/dist/sojourn/build`), so the repo's bin folders are untouched. Ends with a smoke test:
  the package's Python says a sentence with Kokoro and hears it back with Parakeet, from its own models.
- **Hearing and voice ship in it** (Claude Opus 5.5's call, 2026-11-01): Sojourn VR is voice-first, so Parakeet (the
  default, 650 MB) and Kokoro (120 MB) are bundled rather than fetched on first use: a person in the headset presses
  talk and it hears. Whisper (selectable) is fetched from Hugging Face (about 460 MB) only when the person picks it,
  into the data folder's cache (`assistant/paths.py` sets `HF_HOME`); the menu row's tip says so, and the setting's
  description asks the assistant to ask first. Without it a package never fetches Whisper as a stand-in: Soniox's
  stand-in is Parakeet then (`assistant/ears.py`).
- **Installing**: `SojournViewer-win-Setup.exe` (Velopack's; unsigned, so SmartScreen warns as for Access) installs
  for the person only, without admin, into `%LOCALAPPDATA%\SojournViewer` (`current\` the program, `Update.exe`, a
  stub `Sojourn.exe` the shortcut points at, which starts
  `current\Sojourn.exe`), a Start menu shortcut named Sojourn (no desktop one), an Add/Remove
  Programs entry (Sojourn, Studio Quire, 0.1.N), and starts the launcher. Nothing is installed until the person runs
  Setup. The program's folder is replaced by each update, so nothing of theirs lives there.
- **The icon** (Claude Opus 5.5, 2026-11-01): `art/icon/sojourn.ico`, rendered by `tools/icon.py` from
  `art/icon/sojourn.svg` (Threshold: a pearl doorway onto a way to the horizon; the other candidates in
  `art/icon/candidates/`, so another is one file copied over the SVG, then `python tools/icon.py`). Small sizes have
  their own shapes in the same SVG (`data-min`/`data-max`). Both exes carry it as their ApplicationIcon; the launcher's
  windows take it from the exe, the client's window from `Window/AppIcon.cs` (GLFW won't); `vpk pack --icon` gives it to
  Setup and the Add/Remove Programs entry, and Velopack's stub (the shortcut's target) copies the launcher's.
- **The data folder**: `%LOCALAPPDATA%\Sojourn` (`DataFolder.cs`, linked into the launcher: the launcher's and the
  client's base dir, saved accounts, keys, the OpenRouter list, the assistant's memory; Sojourn Access keeps its own
  in `Sojourn Access` but shares the keys in `Sojourn\secrets\assistant`); `SojournVR.exe` run on its
  own from an install defaults there too, with the login screen (`ClientOptions.Packaged`). `SOJOURN_DATA` puts it
  elsewhere (the package's check; the Python side follows it too).
- **Updates only at the person's press** (`Updates.cs`, `LauncherView.Updates.cs`): when an installed launcher opens it
  checks once (GitHub's API, studio-quire/sojourn's newest twenty releases); a newer release shows "update to 0.1.0.N"
  in the footer beside the version, hidden while the client runs (installing closes it). Only the person's own press
  (RealPress, as the key box) downloads it (a delta when there is one) and hands over to `Update.exe`, which installs it
  and starts the launcher again (a delta is small, about 7 MB for a launcher-only change, but rebuilding the full
  package from it takes a few minutes, "installing" meanwhile, and the restart waits for the old folder's removal).
  Nothing downloads or installs otherwise; Velopack's apply-on-start is off. A release
  counts only if its feed, `releases.win.json` (each package's SHA-256), carries our ECDSA P-256 signature
  (`releases.win.json.sig`), checked against the public key in `Updates.cs`; Velopack checks each download against the
  feed. So whoever can publish on GitHub still can't ship an update without the key. The key: `python tools/package.py
  key` made it once, kept DPAPI'd in `local/secrets/studio/update-signing.key`, backed up encrypted on the user's Google Drive
  with its passphrase beside it
  (the user's call, 2026-11-01; `package.py backup` and `restore`; the passphrase DPAPI'd here too); lose it and installed copies take no
  more updates (a new key needs a reinstall). Velopack's version is 0.1.N for the viewer's 0.1.0.N (it takes three
  parts).
- **Uninstalling** (Add/Remove Programs): Velopack removes the program folder, the shortcut and the entry; first our
  hook (`Updates.TidyForUninstall`, 30 s at most) removes what can be made again, `cache\` and `logs\` (renamed at once,
  deleted by a process of their own), and keeps what's the person's: saved accounts, keys (Sojourn Access shares them),
  settings, the chat log under `state\` and the assistant's memory, so a reinstall finds them (Claude Opus 5.5's
  default, 2026-11-01; Velopack's uninstall asks nothing, so there's no choice in the moment). Velopack's own log,
  `%LOCALAPPDATA%\velopack\velopack_SojournViewer.log` (installing, updates, uninstalling), stays.
- **Releasing**: `python tools/package.py` writes a release's files into `local/dist/sojourn/releases` (Setup, the full
  package, a delta from the previous build when its full package is there, the signed feed, `assets.win.json`); only
  the newest full package is kept there. Publishing is a GitHub release on studio-quire/sojourn with those files,
  tagged v0.1.0.N: not done until the release goes out. `--tag NAME` builds a
  try into `local/dist/sojourn/tries/NAME`, apart from the release line.
- **A release is exactly a commit** (Claude Opus 5.5, 2026-11-01): HEAD, or `--commit REV`, exported by `git archive`
  into `local/dist/sojourn/tree` and built from there, so the working tree can be as busy as it likes (several
  sessions work in it at once). What git doesn't hold (the voice dll, media's runtimes, the venv, the models, the
  signing key, NVDA's and libwebrtc's licence texts) is read from the main tree's `local/` by path; the export has no
  `local/` of its own and never a junction (CLAUDE.md > Gotchas). The version is the built commit's count; the commit
  the app shows (the about section, the assistant's `SojournRelease about`, problem reports) and the release notes
  name is the studio-quire/sojourn commit `tools/publish.py` made from it (`local/publish/map.txt`,
  `docs/publishing.md`), since a private hash means nothing outside the studio. Without one a release build stops and
  says so (publish first; pushing stays by hand); a `--tag` try names the private commit instead, and it notes when the
  public commit isn't on the public remote yet. The packaging code itself (`package.py`, `access.py`) runs from the working tree, and says so when it differs from the
  commit. `--dirty --tag NAME` builds a try from the working tree instead (its commit marked `-dirty` when `src/` or
  `assistant/` differ); `--dirty` without a tag is refused, so the release line holds only commits. The export and
  the build tree are fresh each time and removed after (a release is a clean build, a few minutes longer). The check
  builds its update's launcher from the commit the release's notes name, the same way.
- **Checked** by `python tools/package.py check` (on a hidden desktop, the data folder, `%TEMP%`, `%LOCALAPPDATA%` and
  `%APPDATA%` in a scratch folder; the install itself goes in the scratch folder by Setup's `--installto`, but the
  Start menu shortcut and the Add/Remove Programs entry are this PC's own for the minutes it runs, and removed after):
  Setup, the shortcut and entry; the launcher from the stub, its version, a login on the local grid, the client in
  world on the desktop with its natives, .NET and the assistant's Python from the install and Parakeet loaded; an
  update made from this release (the launcher stamped N+1, packed beside it with a delta) refused when its feed's
  signature doesn't hold, offered when it does, refused at a press not from the person, installed at the press and the
  launcher back as N+1; the quiet uninstall (program, shortcut and entry gone, caches and logs gone, accounts and
  settings kept); this PC's own `%LOCALAPPDATA%\Sojourn` untouched. Not covered: Setup's own window (the check runs it
  `--silent`), SmartScreen, the headset, and GitHub as the feed (a folder stands in; the GitHub path is the same code
  over the releases API).
