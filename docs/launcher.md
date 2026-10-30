# The launcher

How a person starts Sojourn: `src/Sojourn.Launcher`, built as **Sojourn.exe** (WPF); the VR client it starts is
`src/Sojourn.Client`, built as **SojournVR.exe** (the user's call, 2026-10-29: the launcher is what people start). Built by
Claude Opus 5.5, 2026-10-29. Agent-facing.

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
  - the provider (`assistantProvider`: claude, anthropic, openrouter, as `assistant/provider.py` has them);
  - the **key box**: paste an Anthropic or OpenRouter key (Ctrl+V, or Enter after typing): its shape says which, it's
    kept by `ProviderKeys` (DPAPI, linked here) and that provider chosen, the box cleared. Kept only from the person's
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
  a log key (the session's log, or the console's if it died with an unhandled exception).
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
- Windows' word first: what `GetCurrentInputMessageSource` calls hardware counts. Asked inside WPF's handling, it said
  unavailable for the person's real keys (the 2026-10-30 guided test: nothing real ever passed), so for anything else
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

## The client's side

- `Login/LoginStart.cs` takes a handed-over login (logs "login: the account the launcher handed over").
- `LaunchState.cs`: with `SOJOURN_STATE_FILE` set (the launcher sets `B/launcher-state.json`), the client writes
  `{pid, state, headset, detail, log}` when it changes (starting, form, connecting, terms, code, failed, world, ending;
  four times a second at most, no garbage per frame). Restarted runs inherit the variable and write their own pid.
- `Starts` (in `Sojourn.Session/Login/LoginStore.cs`) parses starts for both the launcher and the login sheet.
- `Login/LauncherAnswers.cs`: the launcher's answers (> The grid's questions at the desk).
- The launcher compiles `LoginStore.cs`, `GraphicsPresets.cs` and `ProviderKeys.cs` itself (linked source) rather
  than referencing Sojourn.Session, so it starts at once and builds in a second.

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
Checked 2026-10-29 on the local grid in desktop mode: a new account with remember on (logged in, kept with its
password), the saved account again without typing, stop from the launcher (5 s, logged out), a wrong password (the
reason at the foot, the run stopped), and a faked lost GPU (`SOJOURN_FAKE_DEVICE_LOST=6`: the restarted run followed
into the world). Checked 2026-10-30 by the user on Second Life (guided, desktop): a new account, Launch, "on the
desktop", stop (out in a third of a second); and with real hands, after the hooks: Ctrl+V of a key kept (the trace:
keys judged Hardware at the filter), and two clicks on × forgetting it. Before the hooks, every real press was refused
(GetCurrentInputMessageSource said unavailable inside WPF's handling). Decided then (Claude Opus 5.5): a program copying
the person's press can at most act twice on that same press, which every launcher action tolerates; no extra guard on
focus moves (it would refuse Alt+Tab then a quick paste). Not checked: the headset (`--vr`) from the launcher, MFA and
ToS on Second Life itself (the user's account asked for neither), a provider picked here reaching the assistant in a
full run. `shots` also draws OpenRouter with Claude's three (`settings-openrouter-claude`), a tag
(`settings-openrouter`) and the list on its own (`settings-openrouter-list`; a popup isn't part of the view). Open for release: a Claude plan works only if
Claude Code on the PC is signed in (`claude`, then `/login`); the launcher needs a sign-in for it (Claude Code's
browser sign-in, started from the launcher) and to say when it isn't signed in (the user, 2026-10-30).

## Look

Light blue on glass (the user, 2026-10-30, after the teal, violet and peach glows felt wrong for Sojourn): clear glass
for everything, a light blue accent only on what acts (toggles, Launch, a field's rim when typing), slate ink, and
behind the glass a very light blue gradient running straight down (symmetry, also theirs). The glass card holds the
form, Launch is a lens of light blue, the state takes its place while running. The headset's panel and menus keep
their clear glass (PanelLook): the worlds behind them vary too much for one colour, so colours there stay the
person's choice of palette in the viewer (the user, same day). Archivo Light
for the wordmark, Segoe UI Variable otherwise. Words only for names, fields and a word or two of state, but always enough to say what each thing is (the user,
2026-10-30: "remember password", "authenticator code", "grid" over its choices, never a bare word left to guesswork).

## A package with the VR client

`tools/access.py package` is the model (self-contained, versioned 0.1.0.N, data in `%LOCALAPPDATA%`). Sojourn's package
holds the launcher and the VR client only; Sojourn Access has its own package and releases (the user, 2026-10-29). It
would need:
- One folder, everything self-contained: the launcher (`Sojourn.exe`) looks for `SojournVR.exe` beside itself
  (`Places.cs`). Publishing both self-contained into one folder (not single-file) shares one runtime; WPF needs
  Microsoft.WindowsDesktop.App and the client ASP.NET Core's, so publish with the same SDK and RID.
- `-p:Version=0.1.0.N` on the launcher too, so its footer shows the build (the TPV policy's version on the login screen).
- The client's natives and data as Access ships them, plus `openxr_loader.dll`, the media helpers (CEF, libVLC) if
  media is in, and `linden/` and `Shaders/`.
- No assistant unless its Python side ships (`docs/access-app.md` > Packaging names what that takes); the provider
  setting is the place for it.
- The client's own default base dir when packaged is `data/` beside the exe; the launcher always passes
  `%LOCALAPPDATA%\Sojourn`, but the client run alone should default there too.
- A Start menu shortcut named Sojourn pointing at Sojourn.exe; unsigned, SmartScreen warns as for Access.
