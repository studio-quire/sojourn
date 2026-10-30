# The launcher

How a person starts Sojourn: `src/Sojourn.Launcher` (SojournLauncher.exe, WPF). Built by Claude Opus 5.5, 2026-10-29.
Agent-facing.

## What it does

- **The login**: saved accounts (the client's own store, `LoginStore`: the repo's `local/secrets/logins`, else
  `%LOCALAPPDATA%\Sojourn\secrets\logins`), or a new one (username, Second Life / Beta / another grid's login URI);
  the password hashed straight from the password box's protected buffer ("$1$" + MD5, never a string), remember off by
  default, turned off it forgets a kept password at once; forget an account by × twice; where to start (last, home, a
  place: a region or any SLurl, kept per account).
- **Before going in** (the gear): standing or seated, graphics (light, balanced, full), the assistant's provider
  (`assistantProvider`: claude, anthropic, openrouter, as `assistant/provider.py` has them; the keys themselves are pasted
  in the client's menu or Access, never here).
  Written into the client's own `vr-settings.json` in its base dir, only those keys, everything else kept. Graphics
  presets exist only here (`SettingsFile.cs`: draw distance, shadows and their cap, reflections, mirrors and their
  sharpness, detail, far shadows, whole avatars, the eyes' resolution); a file that matches none shows "yours" and is
  left alone until a preset is picked. A first start with no settings file writes balanced (the 1536 shadow cap
  `client.py vr` uses).
- **Launch** starts the VR client (`--login --login-resume --base-dir B --vr`) with the login handed over as a lost GPU's
  restart hands it (`LoginStore.HandOver`: DPAPI, read and deleted by the client, ignored after two minutes; the
  remember choice goes with it). Its console goes to `B/logs/launcher-console.log`; no console window.
- **While it runs**, Launch's place shows its state: starting, logging in, terms or code (in the headset or the window),
  in the headset or on the desktop, closing; a stop key closes the client's window as the person would (ended after
  20 s if it hasn't). A run that starts itself again (a lost GPU, back to its login screen) is followed. A failed login
  stops the run and leaves the grid's reason at the foot, the form as typed. A crash leaves "stopped unexpectedly" and
  a log key (the session's log, or the console's if it died with an unhandled exception).
- **Access** at the foot starts Sojourn Access (its own data, its own login).
- Base dir: `local/main` in the repo (the person's own headset settings and SL cache), `%LOCALAPPDATA%\Sojourn` packaged.

## Why the login happens in the client

The launcher only gathers the login; the client logs in, and the grid's Terms of Service and MFA code are answered on the
client's login sheet (in the headset, or the window on the desktop), as before. The login reply makes the session (the
circuit, the seed capability) and only the process that will live in it can use it: LibreMetaverse can't hand a live
session across processes, and logging in twice (once in the launcher to get the questions answered, then again) is
slower and trips SL's still-logged-in presence. The questions also stay where answers are only ever real presses
(`RealInput`, controllers; never the agent API). The launcher shows that the client is waiting and where. Credentials
never reach the assistant: the hash exists in the launcher's memory, the DPAPI handover file and the client's memory,
all outside the assistant's reach (`assistant/harness.py` denies `local/secrets` and `%LOCALAPPDATA%\Sojourn\secrets`).

## The client's side

- `Login/LoginStart.cs` takes a handed-over login (logs "login: the account the launcher handed over").
- `LaunchState.cs`: with `SOJOURN_STATE_FILE` set (the launcher sets `B/launcher-state.json`), the client writes
  `{pid, state, headset, detail, log}` when it changes (starting, form, connecting, terms, code, failed, world, ending;
  four times a second at most, no garbage per frame). Restarted runs inherit the variable and write their own pid.
- `Starts` (in `Sojourn.Session/Login/LoginStore.cs`) parses starts for both the launcher and the login sheet.
- The launcher compiles `LoginStore.cs` itself (linked source) rather than referencing Sojourn.Session, so it starts at
  once and builds in a second.

## Running and testing

```
python tools/launcher.py run                     # for the person: base dir local/main, the foreground
python tools/launcher.py run --base-dir local/launcher-test --desktop -- --no-assistant --minutes 1
python tools/launcher.py shots local/launcher-shots   # every state drawn offscreen at 2x over made-up accounts
```

Started from a Claude Code session (CLAUDECODE=1) it shows without activating and never focuses a field; the client
it starts inherits that and stays in the background too. For tests set `SOJOURN_LOGINS` to a scratch folder, and don't
drive it by UI Automation on the visible desktop: Invoke on its WPF buttons brings the window to the foreground
(checked 2026-10-29; the other patterns weren't checked one by one). Use `--shots`, or a hidden desktop (`tools/quireuia`).
Checked 2026-10-29 on the local grid in desktop mode: a new account with remember on (logged in, kept with its
password), the saved account again without typing, stop from the launcher (5 s, logged out), a wrong password (the
reason at the foot, the run stopped), and a faked lost GPU (`SOJOURN_FAKE_DEVICE_LOST=6`: the restarted run followed
into the world). Not checked: the headset (`--vr`) from the launcher, MFA and ToS on Second Life itself, a provider
picked here reaching the assistant, Access from the launcher.

## Look

Sojourn's own glass (the panel's default look, `PanelLook`: clear white glass, deep teal ink, one teal accent), over a
pale sky going to pearl with soft glows (Lookbook Aurora Glass's "the colour lives in the light behind the glass"):
the glass card holds the form, Launch is a lens of teal light, the state takes its place while running. Archivo Light
for the wordmark, Segoe UI Variable otherwise. Words only for names, fields and a word or two of state.

## A package with the VR client

`tools/access.py package` is the model (self-contained, versioned 0.1.0.N, data in `%LOCALAPPDATA%`). A package of the
VR client with the launcher would need:
- One folder, everything self-contained: the launcher looks for `Sojourn.exe` and `SojournAccess.exe` beside itself
  (`Places.cs`). Publishing all three self-contained into one folder (not single-file) shares one runtime; WPF needs
  Microsoft.WindowsDesktop.App and the client ASP.NET Core's, so publish with the same SDK and RID. Access is
  single-file today; it would join the folder.
- `-p:Version=0.1.0.N` on the launcher too, so its footer shows the build (the TPV policy's version on the login screen).
- The client's natives and data as Access ships them, plus `openxr_loader.dll`, the media helpers (CEF, libVLC) if
  media is in, and `linden/` and `Shaders/`.
- No assistant unless its Python side ships (`docs/access-app.md` > Packaging names what that takes); the provider
  setting is the place for it.
- The client's own default base dir when packaged is `data/` beside the exe; the launcher always passes
  `%LOCALAPPDATA%\Sojourn`, but the client run alone should default there too.
- A Start menu shortcut named Sojourn pointing at the launcher (or the launcher becomes Sojourn.exe and the client is
  renamed); unsigned, SmartScreen warns as for Access.
