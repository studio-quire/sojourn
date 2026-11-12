# Sojourn Access: as built

Written by Claude Opus 5.5, 2026-10-29. Agent-facing. The plan, stance, milestones and what testers said are
`docs/access.md`; this is what's built (M0-M5) and how to run, use and change it. Testing (the scripted test on a hidden
desktop, the guided tests with the user or a tester) is `docs/access-testing.md`.

## Running it

- `python tools/access.py run` builds if needed and starts it on this desktop with the login form (the local grid
  filled in; log in as `Access Tester`, password `sojourn-local`, or any local account). It runs as
  `%LOCALAPPDATA%\dotnet-sdk\dotnet.exe src\Sojourn.Access\bin\Release\net10.0-windows10.0.22621.0\SojournAccess.dll`: the
  apphost (`SojournAccess.exe`) can't find .NET here (the SDK isn't a global install), and dotnet.exe already has the
  firewall's answer. `python tools/access.py build` builds it (below-normal priority, two nodes).
- Command line: `--grid URI`, `--user "First Last"`, `--password P`, `--start last|home|Region/x/y/z`, `--port 9725`
  (its agent API: on in the repo, off in a packaged build unless `--api`, since any program on the PC could read the
  session through it; `--no-api`), `--base DIR` (default local/access), `--no-assistant`, `--no-voice`, `--no-sounds`,
  `--reader auto|on|off`, `--autologin` (presses Log in itself: tests), `--no-render` (no renderer out of sight, below),
  `--local` (the local grid offered and chosen, which a packaged build otherwise leaves off the list).
- Second Life itself is first in the login's grid list (`agni`, `sl` or `second life` typed as a grid too); the TPV
  must-dos are the session host's, shared with the VR client (`docs/tpv-policy.md`).
- A grid on this PC (a loopback login URI) is reached over loopback alone: LibreMetaverse's UDP binds to 127.0.0.1
  (`Settings.BindAddress`, set per login in `MainForm.StartAsync`), so no port opens to the network and Windows
  Firewall never asks for local-grid runs of a new exe. Voice (WebRTC) still binds every interface, and so does
  Second Life's UDP.
- State, logs and settings live in its base dir: `state/<grid>/<first.last>/` per account, `logs/` the session's
  console (with records of whose words its lines carry: Sending a log, below), `vr-settings.json` the session's
  settings (the reader's switches among them; apart from the VR client's, which keeps its own in local/client),
  `access-settings.json` this app's own.
- Its icon is Shell, a pearl whelk with Sojourn's sky and sea in its opening (the user's choice, 2026-11-07;
  `art/icon/access.svg`, rendered into `access.ico` and PNGs by `tools/icon.py`, its 16, 24 and 32 px hand-drawn): the
  exe's ApplicationIcon, and every window's through WinForms' default icon, set from the same .ico embedded as a
  resource (`AppIcon.cs`), so it shows when run as the dll too. Fixed dialogs have none, as is Windows' way, except
  the login form, the first window in the taskbar.

## Packaging (a build for a tester's own PC)

`python tools/access.py package` (about 2 minutes) makes `local/dist/SojournAccess-0.1.0.N-win-x64.zip`, N the repo's
commit count, so every packaged build has its own number at the login (`AppInfo`; the TPV policy's unique version,
channel still "Sojourn"; a build in the repo stays 0.1.0.0). It builds HEAD exported by `git archive` into
`local/dist/access-tree` (fresh each time, removed after; the main tree's `local/` read by path, never a junction), so
other sessions' uncommitted work stays out (Claude Opus 5.5, 2026-11-01); `--dirty` builds the working tree instead,
marked so in the zip's name and `//about` when `src/` or `assistant/` differ. It ends by naming the public commit made
from that HEAD (`local/publish/map.txt`) for the release notes, or saying there's none yet. `--lean` leaves the assistant out
(`-lean` in the name); `--tag NAME` builds a try into `local/dist/NAME/`, apart from releases (`-NAME` in the name;
`package --check` takes its zip only when named). Built through `SOJOURN_BUILD_ROOT=local/dist/build`, so the repo's
own bin and obj are untouched; staged in `local/dist/stage/Sojourn Access 0.1.0.N/` (earlier builds' stages removed,
about 730 MB each; a released build's zip is on the front door).

- **What's in it** (0.1.0.583 with the assistant, 2026-10-30: 274 MB zipped, 641 MB unpacked, 3528 files; lean about
  87 MB zipped): `SojournAccess.exe`, self-contained single-file (the .NET 10 runtime, WinForms and ASP.NET's Kestrel
  inside; our pdbs embedded, so stack traces have lines); beside it the natives it loads by name: `sojourn_voice.dll`
  (voice and world audio, from `local/voice`), `openjp2.dll`, `shaderc_shared.dll` and `libSkiaSharp.dll` (the
  renderer), `nvdaControllerClient.dll` (from `local/nvda`), `mscordaccore.dll` (the runtime's reader of its own state,
  from the runtime pack the exe was published with, which a single file leaves out: the window watch reads a held
  thread's stack with it, How it fits > The window's thread held); the folders `Shaders`, `Character`, `Textures`, `Fonts`,
  `linden`; `lavapipe` (Mesa's software Vulkan driver for the view on a PC whose graphics can't run it, 56 MB, about
  21 MB of the zip: How it fits > On the processor; `python tools/access.py fetch-lavapipe` puts it in `local/lavapipe`
  first, from mesa-dist-win's release, checked by SHA-256); `README.txt` (for a screen-reader user: the template is `src/Sojourn.Access/Package/README.txt`);
  `Licences/` (notices for NVDA's client, the viewer's LGPL parts, libwebrtc, OpenJPEG, Archivo, Mesa's lavapipe with
  the LLVM inside it (`Package/licences/Mesa lavapipe.txt`), every NuGet package
  with files in the build read from the publish's `project.assets.json`, the .NET runtime packs; texts not found
  elsewhere in `Package/licences/`). Web and video media (CEF, libVLC) aren't in it: only the VR client uses them.
- **The assistant in it** (`bundle_assistant`): `python/`, python.org's embeddable CPython 3.12.10 (fetched once into
  `local/dist/python-embed`, checked by its SHA-256), with the venv's `claude-agent-sdk` and everything it needs in
  `python/Lib/site-packages` (31 distributions, copied by their RECORD files, the extras asked of each followed: mcp
  needs `pyjwt[crypto]`); the SDK's own Claude Code (`claude_agent_sdk/_bundled/claude.exe`, 322 MB of the 403) runs
  the sessions. `assistant/`, the sidecar's code and prompts. All compiled ahead (unchecked-hash .pyc), and
  `Sidecar.cs` starts it with `-B -E -s` and nothing from the PC's Python environment, so nothing is written beside the
  program. Packaging ends with a smoke test: the embedded Python imports every sidecar module (`voice` and `hearing`
  among them) and finds the package's own Claude Code. No hearing (Whisper, Parakeet) and no spoken replies: requests
  are typed, and the bundle has no numpy, so `voice.py` imports without it (`tools.py` reads its voice list; that
  import once crashed the bundled sidecar). Licences: Python's PSF licence and "The assistant's parts.txt" (Claude
  Code under Anthropic's Commercial Terms, each package's licence). The assistant's data (memory, scratch, Claude
  Code's own config by `CLAUDE_CONFIG_DIR`) goes under `<data folder>/assistant`; keys and tokens in
  `%LOCALAPPDATA%\Sojourn\secrets\assistant` (DPAPI, as in the repo); the bundled Claude Code runs with its telemetry,
  error reports and update checks off, and its own `LOCALAPPDATA` under `<data folder>/assistant/claude/local` (its
  cache, MCP logs holding the tools' calls among it, follows LOCALAPPDATA, not CLAUDE_CONFIG_DIR) (`provider.py`
  `env`).
- **Providers when packaged**: an Anthropic key, OpenRouter and the studio relay only, in Alt+0's Assistant group (no
  Claude-account sign-in: CLAUDE.md's decision of 2026-10-30; `ProviderKeys.ClaudeOffered`). A saved "claude" (older
  builds' default) gives way at settings load to a key or token already kept, else it reads "none chosen yet" and the
  assistant says once, in Alt+9's history and on the first Control+D, that it's off until a token or key is pasted
  into the key row. With the studio relay, the tester's use is paid by the studio up to a cap a month
  (`tools/relay/README.md`; the tester's token is `local/secrets/studio/relay-token-tester.key`, DPAPI'd, $50 a month);
  over it the tester hears the allowance is used up and the day it returns.
- **Packaged, it behaves as a person's app** (`AccessOptions.Packaged`: no `src/Sojourn.Access` above the program):
  data, settings, logs and caches in `%LOCALAPPDATA%\Sojourn Access`, never beside the program; Second Life chosen at
  the login, the local grid not listed unless `--local`. With the assistant bundled (`Sidecar.Bundled`,
  `AccessOptions.AssistantInBuild`) its sidecar starts at login and the agent API listens on loopback for it; a lean
  build has neither: Control+D says once a session that describing needs the assistant, which this build doesn't
  include, then what the lists know, and Alt+9's input is named "Ask the assistant (not in this build)".
  `//about` says the version, the commit, the disclaimer and the data folder; F1's list ends by pointing to it.
- **Checked** by `python tools/access.py package --check [ZIP]` (default the newest zip; about two minutes): the zip
  unpacked and run as a tester would, on a hidden desktop, with `--bare` for a PC unlike ours and `--relay TOKEN` for
  the assistant through the relay end to end. What it checks and what it can't reach: `docs/access-testing.md` >
  Checking a package.
- **Runs on a PC unlike ours**: with no Vulkan 1.3 GPU (a VM, Microsoft's basic display adapter, an old card or
  driver) the view draws on the processor instead (How it fits > On the processor), so faces and pictures work, only
  slower. Without that driver too it gives up after three tries (`NeverRan`, its log line naming the devices found and
  their Vulkan versions), and faces and pictures say so in a person's words (`ViewWords`); walk mode and its sonar need
  no GPU either way (the collision model, below). Without `sojourn_voice.dll` working (Windows' N editions lack Media Foundation's `MFPlat.DLL`, which
  it imports) arrival says once that sounds can't play and why (`SoundWords`; README.txt tells how to add the Media
  Feature Pack). The other natives need nothing beyond Windows itself (shaderc only the universal CRT, voice's CRT
  static; checked with objdump).
- **Not signed**: SmartScreen warns on first start; the README walks through it and the zip's Unblock. Signing is the
  user's call.

## Using it

Everything is standard Windows controls (edit boxes, list views, tabs, buttons), so NVDA and JAWS read them as they
read Radegast. What arrives is read through the person's own screen reader (A1's announcer, `docs/screen-reader.md`):
NVDA by its controller client when it runs, otherwise UI Automation notifications raised from the focused control.
Nothing moves the focus but the person's keys.

### Login

Grid (a plain list: the local grid, a grid given or used before, or "Another grid", which opens a Login URI box),
Username, Password, Show password (a check box), Start at (a plain list: my last location, home, the account's
Favorites bar landmarks ("Harbour, favorites bar"), a place given, or "Another place", which opens a Place box), Log
in. The focus starts where you'd type next. Progress and failures are
said ("That username and password don't match."); the focus goes back to Password. The grid's Terms of Service and an
MFA code come in their own small windows, answered only by the person's own key or click. On arrival the main window
opens with the focus in the Say box and "Logged in, in Atelier. F1 for keys." is said. Another account after a failed
one restarts the app with the form filled in (each account's state is its own).

Remember password (a check box, Alt+M, off by default; Radegast's guide ticks its own in step one): ticked, the
password is kept once a login with it works, as the VR client's saved logins keep it (`LoginStore`: "$1$" and the MD5,
the form every viewer sends, under Windows' data protection for this user, in `local/secrets/logins` from the repo or
`%LOCALAPPDATA%\Sojourn\secrets\logins` installed). For an account with a kept password the box is ticked and the
Password box, still empty, says "Remembered: Enter logs in with it, or type another."; a password typed there wins.
Unticking forgets it at once ("Password forgotten."), and a login with it unticked forgets it too. Before, the password
was never kept, typed at every start and every time the session gave up logging in again (gap 11, closed 2026-11-08).
What's kept is the account itself to anything that runs as the person on that PC, as with every viewer's saved login;
the assistant's file tools and shell can't reach either folder (`assistant/harness.py` > `secret_paths`). The scripted
test keeps its own (`SOJOURN_LOGINS`).

The Favorites bar's landmarks as start places (`StartPlaces.cs`; the blind tester's ask, 2026-11-02): the login screen
can't read the inventory, so, as the official viewer saves each account's favourites' SLurls while logged in
(`LLFavoritesOrderStorage`), Access keeps each landmark directly in the account's Favorites folder (or a link to one)
with where it points in `favorite-starts.json` in the account's state dir, checked every 10 s once the inventory has
loaded (only new landmarks are read; renames just renamed; sorted by name, as Places lists them). Start at reads that
file for the grid and username typed, following both as they change; one picked is remembered as the last start, unlike
a place typed once. The viewer shows them only with its "Show Favorites on login" preference; here always.

After that the connection is said too (`MainForm.Connection.cs`): a drop is the session's notice ("Lost the
connection: no region has answered for half a minute. Logging in again by itself; ...", or the grid's words for a
disconnect, or a crossing that didn't go through); each try that fails is said once ("Not back yet: Second Life still
counts you as logged in from before; ... Trying again by itself."); being back is "Back in Second Life, in X."; a
login that can't succeed by itself is said and the login window opens with the reason, Enter there logging in again
(it closes on arrival). The grid's terms or a code asked while logging in again come up as at the start. Meanwhile
what needs the world answers "not in world right now; logging in again". Before, nothing was said: on 2026-11-01 the
blind tester heard only "Not sent: not logged in" after a train's crossing failed, took it for a crash and closed the
app while it was logging in again.

### Areas

Alt+number; Radegast's numbers where Radegast has the same area.

| Key | Area | What's there |
|---|---|---|
| Alt+1 | Nearby chat | the nearby chat history (read-only, reviewable line by line) over Say (input), People nearby beside them; the history is one Shift+Tab back from Say, People nearby one Tab on (then the history), as Radegast goes round (People beside chat, below) |
| Alt+2 | Friends | the People area with the focus on Friends, its first friend read: Radegast's Alt+2 (a blind tester's habit, 2026-10-30) |
| Alt+3 | People | Nearby ("Kestrel Tester, 4 metres, two o'clock", nearest first), Friends ("Kestrel, online", online first), My groups ("Builders, tag worn") |
| Control+Tab | Conversations | a tab per IM or group chat: its history over its input ("Message to Kestrel"); also View > Conversations; and the builders' tab (Control+Shift+B, Messages to the builders, below) |
| Alt+4 | Inventory | the tree ("Red Dress, skirt, worn"; "Hair, object, worn on Skull"; "..., favorite"; "..., marked"), Search, Show (everything, worn, recent, trash, favorites, gestures) and Search by (name, creator, description) |
| Alt+5 | Search | Search for (words), Look for (people, places, events, groups, classifieds), Category and Day where they apply, Results |
| Alt+6 | Places | Landmarks (a tree: the Landmarks folder and the Favorites bar), Teleport history ("Bay City, Harbour, today 14:05"), Go to region (a box), Regions around ("Bay City, 3 people, next door to the north") |
| Alt+7 | Objects nearby | "Notices Harp, 3 metres, eleven o'clock, touchable" (or ", can sit"), nearest first, within 30 m, the nearest 60 and then a row for more; Control+F filters by words (Show: what you wear instead, Alt+Shift+7, or what people nearby wear); Tab: the selected one's parts, then a part's faces, then the filter and Show (Objects nearby, Parts and faces, below) |
| Alt+8 | Notices | what waits for an answer, newest first; the selected one's text, keys and reply box; Escape goes back where Control+N came from |
| Alt+9 | Assistant | a typed request ("Ask the assistant"), its replies, its choices as buttons |
| Alt+0 | Settings | the settings in named groups, one group at a time in a list, the groups a Tab away (Settings, below) |

Pressing the Alt+number of the area you're in reads it again ("Inventory: Red Dress, skirt, worn.").

Two more areas have no Alt+number: Build, the build editor, on Control+B (Building, below), and About Land, on
Control+L (About Land, below).

### Settings

`MainForm.Settings.cs`, over SojournSettings, the same settings the assistant changes. The rows grew past a hundred in one
list; a blind tester, offered groups or a box narrowing the list as you type, chose groups.
- **The groups**, in order: Reading out, Sounds, Walking, Driving and trains, People and offers, Chat, Voice, Assistant,
  Incognito, My avatar, General, Everything. Each name's first letter is its own. What's changed most in a group comes first
  (Sounds starts with the volumes). A row someone could look for in either of two groups is in both (Residents' voices,
  volume is in Voice and Sounds; arrivals and departures in People and offers and Reading out). Everything is every row,
  A to Z, for when you don't know where one is, or like the long list.
- **The list** shows one group, named for it ("Sounds settings"). Its first row is the group's heading: "Sounds, group 2 of
  12: volumes, the parcel's music, where sound comes out, and the sound for each kind of thing." The first group's also says
  the keys. Home reaches it from anywhere in the group.
- **Page Down and Page Up** go to the next group and the one before, round the ends, landing on the heading, which is said.
  It's said as well as being the row the focus lands on: NVDA drops a focus event for the position it's already on, and
  heading to heading is one. On a heading, nothing is there to change, so Right and Left do the same (Page Down takes Fn on
  many laptops), and Enter or Space go to the group's first setting. Plain Page Up and Page Down aren't anyone's: nothing
  else in Access uses them (their Control forms are the conversations' ring), NVDA's use NVDA, or Control+Alt in a table,
  and JAWS's use Insert.
- **Tab** reaches the groups, a list named Group, as the build editor's and About Land's tabs: "Sounds, 2 of 12". The arrows go
  through them, the settings list following behind; a letter goes straight to a group; Tab, Shift+Tab or Enter goes back to
  the settings, on the heading if the group changed.
- **Coming back**: Alt+0 lands in the settings list, in the group and on the row you left (for as long as the app runs; it
  starts at Reading out). Alt+0 pressed there says where you are: "Walking, group 3 of 12. Footsteps' volume: quiet."
- **A row** leads with what it's about, so first-letter navigation finds it within the group ("IM sound", "Footsteps (walk
  mode)", "Nearby chat read out"; no two rows start with the same three words); Enter, Space or Right moves one to its next
  value, Shift with them or Left to the one before (a sound's row plays it; Enter on the sound output or microphone opens the
  list of devices; on a row that has no steps, a number in a range or a word, Enter asks for it to be typed). Her keywords
  and what her auto responses say are rows too, typed, an auto response confirmed by her own key. The assistant's provider,
  key or token and OpenRouter's model are in Assistant (The assistant, below).
- **No setting falls off**: one registered and named in neither `SettingsGroups` nor `NotRows` still gets a row, by its bare
  name, at the end of General and in Everything, and the settings step fails until it's placed.

### The menu bar

`MainForm.Menu.cs`; Alt or F10, then the arrows, or Alt+W, V, R, B, H for a menu. It's there for finding out what exists,
as Radegast's menus are. World (teleport home, to the SLurl on the clipboard, set home here, landmark here, sit on the
ground or stand up, fly or stop flying, always run (a check, `//run`), walk round people (a check, `//walkround`), Auto-sit (On, a check, Control+Shift+S; sit back on what you're sitting on; sit on this region's seat now; forget each seat), walk mode, Recording a route (its keys and Discard), Gestures: the active ones to play, Away and Do not disturb as checked
items, incognito, region info, About Land (Control+L), the region's covenant and the estate (its tabs), parcels in this region, status, what you're wearing, save your outfit, describe where you are, describe
me, rebake, stop all animations, hover height, my profile, display name, highest rating of region (`MainForm.Self.cs`; each also a `//` command: `//rebake`, `//stopanimations`, `//hover`, `//displayname`, `//maturity`), log out, back to the login window (`//logout`: asked first, Enter goes and Escape stays; the app starts afresh there, the form filled in and the password typed again), another account, in a new window (`//newwindow`; The session itself, below), who's in voice, the people in voice to act on (Control+F4), open mic or push to talk, the sound out of the next device), View (the areas by their Alt+numbers,
Friends among them, the second inventory pane, the inventory's favorites, the newest notice, Conversations, the conversation ring, closing one),
Read-out (mute all, mute this conversation, times, arrivals, Recent messages: the last nine as items with their
Control+numbers, then back, forward, oldest, newest, mark, copy, copy the assistant's last answer, links seen, chat logs, blocked), Build (the
official viewer's Build menu: the build editor, Control+B; a new box; undo the last build change; the build
commands; upload a file, Control+U; export what you're editing to a file; import an object from a file) and Help (keys, the slash
commands, send a log, read a session's log, message the builders, Newer build?, clear the cache at the next start (checked while it's marked), about; a newer build is also said once after logging in).

- Each item shows its key beside it (`ShortcutKeyDisplayString`, which UI Automation gives screen readers as the item's
  accelerator key); the keys themselves stay in `ProcessCmdKey`, never the items' `ShortcutKeys`.
- Every item runs what its key, `//` command or op already does (Away and Do not disturb through SojournSettings'
  `away` and `do_not_disturb`, set home by `TesselAgent setHome`, a landmark by `TesselInventory createLandmark`), as
  the person's own press: items run as they're pressed, and those that move the focus do so once the menu has closed
  and given it back (`Later`, as our own move for the person).
- Away is her own Away: its check and what picking it does leave out away set by itself (five minutes without her,
  `away_after`). Picked while that's on, it says "Away." and the away stays as hers when she's back; it used to take
  the pick for turning away off and say "Not away." (Claude Fable 5.2, 2026-11-10). That needs her pick inside the
  quarter second between her first key after a while and the people tick clearing away by itself (`PeopleWiring.Away`),
  so Alt+W, A at a practised speed, not at a reader's pace. Alt+0's `away` row and the VR menu's switch still show
  and flip the plain state.
- No two items on an open menu share a letter: the menu bar's menus go through `Mnemonics.Fix` as they open, as the
  context menus do (World had two on N, Recording a route and Stop all animations, so N there did nothing).
- The menus' letters avoid the areas' own label mnemonics (so inventory's and objects' Show is Alt+O and Places'
  History Alt+S, Alt+H being Help's).
- Alt alone and F10 open it only while the window is the foreground one (WinForms' `ToolStripManager.ProcessMenuKey`),
  so the hidden-desktop test can't press them; the guided test asks.
- The context menus show their keys the same way: Cut Control+X, Copy Control+C, Paste Control+V, Move to the other
  pane Control+M, Rename F2, Delete Delete, Describe Control+D, the first action Enter (an inventory item's); a
  friend's Send IM Enter, a group's chat Enter, a part's Touch Enter and Faces Tab, an object's Parts Tab.

### Keys

F1 shows them in a window, one a line under short headings, the focus in its read-only text; Escape closes it and the
focus goes back where it was. (A window because spoken they ran to some 2,000 characters, and the screen reader's stop
key lost the rest.) Walk mode's F1 does the same with its own keys.

- In any input: Enter says (or sends), Shift+Enter whispers, Ctrl+Enter shouts (nearby only); `:waves` is an emote as `/me waves` (two letters at least after the colon, so `:)` and `:D` stay); Ctrl+Up/Down bring back
  earlier lines; Ctrl+Backspace deletes the word before the caret (`LineBox`, `TextKeys`: a plain Windows edit box
  types U+007F for it). Tab and Shift+Tab go between a history and its input (in nearby chat, Tab from Say reaches
  the history, then People nearby): the tab strips aren't Tab stops (Alt+number and Ctrl+Tab switch them). `/me`, `/7 text` (a
  channel) as in any viewer; `//` commands below.
- Ctrl+Tab / Ctrl+Shift+Tab, or Ctrl+Page Down / Page Up: nearby chat and the conversations as one ring (focus to the
  input), from anywhere (a tab control's own Ctrl+Page keys would switch only the strip the focus is in). Ctrl+W closes
  the conversation you're in. With none open, Ctrl+Tab says "No conversations open." and View > Conversations lands on
  a read-only line saying so; an IM arriving while the focus is there leaves it there, the line now naming the
  conversation and Control+Tab, until the focus leaves it.
- Ctrl+N: the newest notice (the Notices area, it selected). On the notices list: 1-9, 0, minus and equals press its
  keys in order (a script menu's twelve: "... 0 Ten, minus Next, equals Back"), Enter goes to its keys (or the reply
  box for llTextBox), Delete ignores it (the official viewer's Ignore: it goes unanswered, a script hears nothing),
  said with what's left ("Ignored Dance Ball's menu. 1 waiting."), the focus on the notice that took its place; on a
  confirmation, Enter confirms and Escape cancels; one whose words name only the first few ("and 40 more") says
  "Tab: the whole list.", and Tab reaches the notice's text with every name, a line each. A notice's keys as read end with "Delete Ignore" ("Keys: 1 Waltz, 2
  Tango, B Block, Delete Ignore."), since the blind tester found no way to close a dance machine's menu (2026-10-31). Block is on B, never a
  number. After an answer the next notice is said with its keys ("Next: ..."): the answer's own words cut off NVDA reading it, and on a ski ball's
  two questions and a menu a number went to something unheard and blocked an object (2026-11-05). A script asking the same again while its question
  waits makes no second notice.
  An answered menu leaves the list; one its object sends again (a dance machine's, after each pick) is picked by
  itself, and replaces any earlier one from that object, as in the viewer. The Applications key (or Shift+F10) on a
  notice: its keys as a menu, each with the key that presses it ("Green, 2"), then Reply... and Ignore (Delete); a
  confirmation's Confirm and Cancel. Escape anywhere in the notices but on a confirmation (or in the reply box): back
  to the area and control Control+N came from, else to Say. Tab from the list reaches the notice's text, its reply box
  and its buttons ("1: Red", "Delete: Ignore").
- **A person's actions are one list** (`MainForm.PersonActions.cs`), the same items with the same letters wherever their name is: People nearby (Enter opens them), Friends and Search (Enter: an IM), an IM's message box or history, and their profile (Applications key or its Actions button; there the window closes first). Send IM, Walk to, Face, Where, Describe, Follow, Beacon, Hear what they point at (or Stop hearing; What a friend points at, below), What they wear, Their animations (One person's things, below), Profile, Notes on them, Chat log, Pay..., Give what you copied, Offer teleport, Request teleport, Offer friendship (a friend: Start a conference..., End friendship, confirmed), Invite to a group... (Groups, below), Mute in voice or Unmute in voice, How loud in voice... (0 to 2, 1 as they come; both for you alone and kept), Always incognito with them, Block or Unblock, then, only where she owns or manages the estate, Eject from estate and Ban from estate (About Land > The estate, below), IM sound for NAME.... What needs them near says so when they aren't ("Kestrel isn't nearby, though online."), rather than the menu changing shape; Where on a friend elsewhere on the grid who lets you see them on the map says the region and spot ("Kestrel is in Bay City, at 128, 64."; `SojournPeople whereFriend`, as the viewers' Show on map). A friend's What they may do has See when you're online, Find you on the map and Edit your objects, checked as they stand, each through her confirmation; edit rights are how a sighted helper helps without logging in as her. `//block NAME`, `//profile NAME` and `//friend NAME` find anyone by name: open conversations, friends, people nearby, then the grid. A new action goes in `PersonActions`, never a list's own menu. On a group (Enter: its chat, a conversation tab): Group chat, About the group, Wear its tag, No group
  tag, Choose your title..., Members..., Who's in its chat..., Notices..., New notice..., Invite someone..., Banned residents..., Settings and roles..., Chat log, Receive its notices, List it in your profile (Groups, below), Group sound for NAME..., Leave (the grid asks in Notices), and New group... (on "In no groups." too, alone). On a friend, Start a conference...: friends online in a list of check boxes, the one you were on marked, Space marking more, Enter starting it with two or more and going to its tab. In a group's message box or history: its sound and Who's in the chat...; in anyone's, Cut, Copy, Paste, Select all after what's above, in place of Windows' edit menu (`LineBox.AppsMenu`). A profile opens in
  its own window: a read-only text read line by line (name, born, partner, about, interests, picks, groups with their titles, first life, web), their picks and your notes on them a Tab on,
  Send IM, Pay, Give what you copied, Describe pictures, Actions..., Close (Escape) (Your profile and notes, below). Pay asks the amount, then waits for the
  person's confirmation in Notices (refused at once when it can't be afforded). Give what you copied (also on a person
  nearby and a friend) gives what Control+C or Control+X last took in the inventory, each thing confirmed: the blind
  tester's Radegast habit of copying an item and giving it from the person (2026-10-31), beside the inventory's Give
  to... On an object: Touch, Sit (on what she's sitting on, Stand up), Auto-sit on it (Stop auto-sitting on it on her seat), Walk to, Beacon, Face, Point at (Stop pointing on the one she points at; Pointing at something yourself, below), Describe, Details, Where (with its floating
  text), Contents, Parts, Move..., Export to a file... (on hers), and Take into your inventory, Delete and Return to its owner where she may (Objects nearby,
  below; Delete, also the key, is confirmed in Notices and goes to the Trash); on something worn only Touch, Describe, Details,
  Where, Parts and Take off (Delete too: back into the inventory, never deleted; the blind tester found no way to take a HUD off there,
  2026-11-02). Take off is only for what she wears now ("X isn't something you're wearing now." otherwise, and the
  agent API's `detach` by object refuses anything not worn by her), and says "Took off X." only once the region no
  longer has it on her (up to 8 s; else "X is still on you"). A menu's items act on the thing it was opened on,
  whatever the list holds by the time she picks: a refresh under the open menu once had Take off act on the row that
  came in its place, telling her she'd taken off a pose ball while her bag stayed on (2026-11-11). Each list menu's opening is a line in the session log with what it offered, on what (the inventory's
  too, with the item's words), so a log shows what she was given to choose from.
- Lists: F5 refreshes; a list you're in never changes under you otherwise (it refreshes every 3 s while you're
  elsewhere). Arriving in a list that hasn't filled yet (Second Life's objects wait up to 3 s for their names), its
  name says so ("Objects nearby, looking") and its first item is read as it fills; the landmarks tree has a "Looking
  for landmarks." line, the inventory "Inventory not loaded yet." Only the list you're in tells the screen reader its
  item has the focus (`ReaderList`: otherwise WinForms announced the lists filling beside it, and Alt+7's parts and
  faces had the last word).
- Objects' names follow renames: a script's llSetObjectName reaches only viewers that have the object selected (the
  official viewer asks for a name each time it shows one, on hover or a menu), so Objects nearby asks again for the
  names of scripted things (a script anywhere in the linkset) over 5 s old on each refresh, and F5 for all of them,
  waiting for the answers (the `nearby` op's `renew`). A self-naming sofa (the blind tester's, 2026-10-31: renamed as
  people sat and stood, and the list kept its first name for minutes) shows its new name on F5 in the list, or by
  itself while you're elsewhere, its parts list's name ("Parts of ...") following. Radegast asks only for names it
  lacks, so it keeps the first name too. The test's check is the visitor's `sofa`, renamed by a touch. Objects
  nearby's Applications menu has Refresh (F5) too, so the key can be found.
- In the inventory (Alt+4): arrows walk the tree (Right opens a folder); Enter wears an object, clothing or body part
  (takes it off if worn), opens a notecard or script, teleports by a landmark, opens or closes a folder; Space marks
  or unmarks ("3 marked"; the item's words end "marked": no check boxes, which NVDA read on every item and a sighted
  tester took for worn or not), and what's marked is what every action takes, else the selected one; an item's words
  follow a wear or take-off as it lands ("worn" comes and goes within a second). The Applications key (or Shift+F10)
  lists every action: Wear, Add to outfit, Take off, Rez on the ground, Rez where you say..., Unbox, Give to..., Cut,
  Copy, Paste, Paste as link, Rename, Sort by date (or by name), Details (rights, maker, date), Delete; on a folder Replace outfit, Add to outfit, Take off the
  folder's items, New folder, New notecard, New script, Upload a file here (Control+U; Uploading, below), Expand all in it (and
  Collapse all in it, once open); a notecard also Play as a route (`//route`); a landmark Teleport there and About the place.
  - **The order** (Radegast's Sort by date, which it starts on; Alt+0's "Inventory order in each folder", `access_inventory_sort`):
    by name, or newest first, folders first and by name either way. The Applications key's Sort by date, newest first (or Sort
    by name) turns it, said ("Sorted newest first: ..."), both panes following with the place kept; searches and Show's lists
    take the same order. By name unless changed.
  - **Expand all in it** (a folder's Applications key, or the number pad's star, as in any Windows tree; Radegast's Expand All):
    the folder and every folder under it opened, as opening each by hand does, the focus staying on it ("Opened Outfits and the
    12 folders in it: 340 items in all."); Collapse all in it closes them again, so they don't come back open by themselves.
  - A gesture's words say whether it's active, and its trigger ("Clap, gesture, active, /clap"); Enter plays it (any
    gesture, active or not), and its Applications key has Play, Activate and Deactivate (for all the marked ones).
  - Ctrl+X / Ctrl+C then Ctrl+V on a folder (or an item in it) move or copy; F2 renames. On Second Life, moves, deletes to the
    Trash and renames wait for the inventory service's answer before they're said done, and one it refuses is said
    ("Not deleted: Second Life's inventory service refused the move ..."), nothing changing (`docs/inventory.md` > Moving and tidying); the
    folder something left is shown again as it is now, wherever it was done from.
  - **Paste as link** (the official viewer's; the Applications key, L, wherever Paste is): a link to each item cut or
    copied, in the folder the pane points at, named as the item ("Linked Pip in Party outfit."); the item stays where it
    is, so one pet can be in every outfit as links rather than copies. Folders aren't linked, said so; a notecard
    with nothing ever saved in it can't be linked yet, as in the viewer. Play is on Y so it keeps a letter of its own (with the second pane open, Copy to X there is H, and Attach to O); L is also Unmark all's,
    so with marks the first L selects and the next moves on.
  - **Save your outfit** (World, or `//saveoutfit NAME`; the official viewer's Save Outfit): what's worn now as a new
    outfit folder in My Outfits, a link to each thing (named as worn, keeping the outfit link's description, where the
    viewer keeps clothing's layer order); Replace outfit on that folder wears it again. Anything the grid didn't link is
    named.
  - **About the place** on a landmark (the official viewer's landmark panel): the parcel it points to, asked of the
    grid (its name and region, rating, owner or owning group, size, traffic, a price if it's for sale, its description),
    where it lands, and the landmark's own notes and date (`TesselInventory landmarkAbout`).
  - **Favorites** (the official viewer's 2024 inventory favourites; `MainForm.Favorites.cs`): any item or folder can be
    a favourite, staying where it is, the mark kept on Second Life's inventory service, so the official viewer shows
    the same. Ctrl+Shift+F adds what's marked (else the selected one) to the favourites, or takes it off when all of it
    is one ("Added Red Dress to favorites."); the Applications key has Add to favorites and Remove from favorites
    (neither in the Trash, never Add in the Library, as the viewer). A favourite's words end "favorite" ("Red Dress,
    skirt, worn, favorite"), not in the Trash; a link's mark goes on what it links to and the link says its original's.
    Show: Favorites (also View > Inventory favorites) is the viewer's Favorites tab: the topmost favourites, folders
    first then by name, each with where it lives ("Red Dress, skirt, in Clothing"; "favorite" not said on each), a
    favourite folder opening with Right onto its contents (favourites inside it not listed again at the top), links,
    the outfit's links and the Trash left out; every action works there as in the tree; F5 reads them from the service
    again (marks made in another viewer meanwhile). With words in Search, Show: Favorites searches only them. The
    system folder the viewer now calls the Favorites Bar (landmarks for its bar and the login's start places) is named
    "Favorites bar" in the tree and in Places, so the two don't share a name. LibreMetaverse's inventory fetch drops the
    marks, so after logging in, once the inventory has loaded, Access reads every one from the service in the
    background (`SojournInventory favorites`; a big inventory in parts, `docs/inventory.md` > Thumbnails and
    favourites), and the words follow when it's done; Show: Favorites before then says "Reading your favorites from
    Second Life." and shows them when they're in. On a grid without the service (OpenSim) the key and Show say
    "Favorites aren't on this grid: ...". The viewer has no key for it; Ctrl+Shift+F is ours. Checked against a
    stand-in for the service (`docs/access-testing.md`); only Second Life checks the real one.
  - Delete and Give ask first (the confirmation comes up selected in Notices: Enter confirms, Escape cancels, then the
    focus is back in the tree). As in the official viewer and Firestorm, a worn body part isn't deleted, said at once
    without asking, and other worn items, the folder's too, are taken off first, the confirmation saying so.
  - Giving a folder gives everything under it, as the official viewer does: the confirmation counts the items in it and
    its folders, the folders inside it, the no-copy ones, and what stays (no transfer, or worn: left out, as the viewer
    leaves them). Second Life carries at most 42 things in one offer, its folders counted, so a bigger folder (or one
    with nothing that can be given) is refused before she's asked, saying why and that its folders can go one at a time.
    The answer says what went ("Gave Landmarks for Kestrel to Kestrel, 30 items in it and its 4 folders."): it's an
    offer they accept or decline, so it's what was offered, not what they kept (`InventoryOps.Gives.cs`).
  - Ctrl+F goes to Search (from the tree, Show or Search by), where typing lists what matches flat with its folder ("2 found"), Enter goes back to the
    tree, Escape clears it; a search leaves the Trash out and says what it has there ("2 found, and 3 in the Trash
    (Show: Trash lists them)"); a long one says "Searching." first. Tab from Search reaches Show (Up/Down: everything,
    worn, recent: every item newest first, each saying when it came ("Red Dress, skirt, yesterday 21:40, in Clothing"; the
    newest 500; it was the last day's only, too short for last week's purchase), trash: what's in the Trash, or with words what matches there, favorites, gestures: every one, active or not, each with its folder).
  - **Search by** (the official viewer's; Radegast's search fields), a Tab after Show: Name (as it always was), Creator, or
    Description; a change with words in Search searches again and says it ("Nothing made by Kestrel.", "1 found."). Creator
    matches any form of the name (as written, in plain letters, the username: `NameCache.Forms`); the names of makers not known yet are asked of the grid first (80 at a time,
    each batch waiting at most 5 s, so a name that doesn't come doesn't match), which "Searching." covers. A link is matched
    by what it links to. Folders match only by name.
  - F5 asks the grid for every folder of yours again, said as it starts and ends, then shows it in both panes (Radegast's Reload
    Inventory, how stale folders get mended; the tree doesn't change under you otherwise; your actions refresh what they
    touched). What the grid no longer has goes too: something deleted or moved in another viewer (or by the grid) leaves where it
    was, said ("1 thing wasn't there any more, and is gone from the list."). LibreMetaverse's fetch only ever added, so until
    2026-11-08 it stayed until the next login (`docs/inventory.md` > Loading).
  - On an object, Attach to... lists every point with what it holds; Enter wears it there beside what's on it ("Wearing Hat on Chest."), for something whose own point is wrong. On a sound, Play plays it in the world where she stands, as the viewer's does, so people near hear it too; on an animation, Play and Stop it.
  - **The Trash**: Delete on what's already in it goes for good, the confirmation saying so. Empty the Trash, first on the Trash folder's menu and on anything in it, or `//emptytrash`, behind her confirmation.
  - **Lost and Found** (where what's returned from the world lands): Empty Lost and Found the same way, first on its folder's
    menu and on anything in it, or `//emptylostandfound`, behind her confirmation ("Empty your Lost and Found: everything in it
    is deleted for good, not moved to the Trash."); Radegast's and the viewer's.
  - **Back up to disk...** (a folder's Applications key; "Back up all your inventory to disk..." on a folder at the top, the
    tree having no node for My Inventory; Radegast's Backup, `MainForm.Backup.cs`): Windows' own folder window asks where, and a
    folder named for it and the time ("Outfits backup 2026-11-08 15-20") gets inventory.csv, Radegast's list of everything in
    it (type, folder, name, description, created, creator, last owner; the Trash and the outfit's links left out), and the text
    of every notecard (.txt) and script (.lsl) she made herself with full rights, in folders as in the inventory, dated as the
    items are. Only what she made, as Second Life's rules for viewers ask of an export the official viewer lacks
    (`docs/tpv-policy.md`, 2.b; Radegast asks only for full rights); what others made is listed, and the end says how many.
    Said as it starts ("Backing up Outfits: a list of the 340 items in it, and the text of 12 notecards and scripts you made,
    into ...") and when done; one at a time, the item saying how far it's got if pressed again. Pictures aren't saved.
  - A notecard or script opens in its own window, an edit control: Ctrl+S saves ("Saved.", or a script's compile
    errors read out and listed under the text), Escape closes (asking first if changed). Find, go to a line, go to an error and
    the rest: Scripts, below.
  - **What a notecard holds** (`MainForm.Notecards.cs`, Core's `NotecardFile`; Radegast's Attachments, and SL's welcome
    cards carry landmarks and gifts this way). Opening one says so ("It holds 2 things: Harbour, landmark; Rules,
    notecard. Tab reaches them."), and a list a Tab from the text names each. Enter on one copies it into the inventory,
    in the folder for its kind ("Copied Harbour into Landmarks. Enter again teleports there."); on a landmark or a
    notecard Enter again teleports or opens it; the Applications key has the same. In the text each thing's place reads
    as a marker, "[landmark: Harbour]", which can be moved like any words. Saving keeps them ("Saved, with the 2 things
    it holds."), whoever saves: every writer (`writeNotecard`, an object's `object.content.save`) goes through
    `NotecardFile.Saved`, which keeps the asset's embedded block byte for byte and puts back the place of any item whose
    marker has gone from the text, at the end, saying so, since the official viewer drops an item with no place in the
    text at its next save. A card that can't be read just then isn't saved at all. `copyFromNotecard` posts the viewer's
    capability itself, LibreMetaverse's own call sending an empty body.
    - **Changing what it holds** (2026-11-08), at the next save, as the viewer's text editor does it: Delete on a thing in
      the list marks it to be removed ("Harbour is removed from the card when you save.", its line saying so; Delete again keeps it);
      Control+Shift+V in the text (or the list's Put in what you copied) puts in what Control+C or X last took in the
      inventory, its marker at the caret, numbered apart from any of the same words ("[landmark: Harbour, 2]"), its line "goes
      in when you save" (Delete takes it out again, marker and all). Only what the next owner may copy, modify and transfer
      goes in, as the viewer refuses the rest by the same rule; a folder, the card itself, or something never saved are
      refused, said; a thing already in the card gets another place. One save does it all, said ("Saved. Put in: Harbour.
      Removed: Rules."), and the card is read again into the window and the list. A change not saved is asked about on
      closing. `NotecardFile.Saved(text, remove, add, ...)`: what stays is numbered again from nought, in the text and the
      head, since the viewer finds an item by its place in the list and not by the "ext char index" it writes
      (`LLEmbeddedItems::getEmbeddedCharFromIndex`); each kept block is otherwise byte for byte; a new one is written as
      `LLInventoryItem::exportLegacyStream` writes it. `writeNotecard` and `object.content.save` take `remove` (embedded
      item ids) and `add` ([{item_id, marker}]).
    - **A card in an object's contents** (opened from the build editor's Content) has the same list, from
      `object.content.get`'s `embedded`, copied out with the object named (`copyFromNotecard`'s `object_id`, as the viewer
      copies from a card opened from an object), and changed the same way, its save confirmed as any change inside an object.
    - Only the local grid has shown changing what a card holds and copying out of one in an object; whether Second Life
      takes a card saved with a thing we put in is still to be seen (it takes the viewer's, written the same way).
  - Unbox rezzes the box 2 m ahead, copies what's inside into a new folder named after it at the top of the inventory,
    then puts the box away (deleted when the inventory still has it, taken back when it was no-copy). Rez on the ground
    sets an object down 2 m ahead with its bottom on the terrain (or the floor you stand on, when that's well above
    it); Rez where you say... (S) asks where in words (Placing by words, below).
- **The second pane** (the blind tester's second inventory window): Alt+Shift+4 opens a second pane beside the first
  ("Inventory, second pane", its own search and Show, `inventory2`) and goes to it; Alt+4 goes to the first. Each also
  from the menu bar's View and the tree's Applications key ("Open a second pane", "To the first pane"). Pressed in the
  pane the focus is already in, either reads where you are. Each pane has its own marks and open folders; Ctrl+M
  moves what's marked (else the selected item) into the folder selected in the other pane (or the folder its selected
  item is in); the Applications key has "Move to X, in the other pane" and "Copy to X, in the other pane"; Ctrl+X/C/V
  work across them. Ctrl+W in the second pane closes it. Actions refresh what they touched in both.
- In Search (Alt+5): type, Enter searches (Tab to Look for, Up/Down to change what: people, places, events, groups,
  classifieds; events need no words: Enter lists what's on now; Tab again: Category for places, events and classifieds, and for events Day, from on now to a week on); the focus goes to the results ("4 found."). A page of what the grid sends at a time: where it has more, a last row, "More results: Enter lists the next.", adds them under the rest with the focus on the first of them ("100 more, 200 in all."). People and classifieds come all at once from the grid. Enter on
  a person opens an IM, on a place, event or classified teleports there, on a group says its details; the
  Applications key has the rest (a person's Profile, teleports, friendship, Pay; a place's About; a group's Join, whose
  question comes to Notices).
- In Places (Alt+6): Enter teleports from any of its lists (a landmark, a place in the history, a region around), or
  from Go to region (a region's name, "Bay City 128 64 22", Region/x/y/z or an SLurl); the arrival is said ("Arrived
  in Bay City, at 128, 64, 22."). Tab moves Landmarks, History, Go to region, Regions around; F5 reads them again
  (they're read when the area opens, and on Alt+6 while it's open).
- Ctrl+G: teleport to the SLurl on the clipboard; "Teleporting to Bay City 128, 128, 22." then "Arrived in Bay City,
  at ...".
- Ctrl+Shift+1: region info (`//siminfo`), Radegast's key.
- Ctrl+Shift+H: teleport home, Radegast's key: "Teleporting home.", then the arrival; through the session's teleport
  watch as the VR client's way out, so a refusal is said ("Couldn't go home: ...").
- Ctrl+Shift+S: auto-sit on or off ("Auto-sit off.", "Auto-sit on: Comfy Chair in Bay City."; Auto-sit, below), the key
  Radegast issue #156 asks for.
- F6: mute all read-out of what arrives (answers to your keys are still said; a voice call ringing, who starts speaking
  and the music's titles are muted too); Shift+F6: mute or unmute the source you're in (nearby chat, or this
  conversation). Kept in access-settings.json.
- Answers longer than a line (`//help`, a long outfit or parcel list) are read whole, a few sentences at a time in
  turn, each its own line for review (`Announcer.WholeAnswers`; other announcers cut at 700 characters).
- Ctrl+[ / Ctrl+] (the bracket keys right of P): back and forward through what was read (the last 200 lines);
  Ctrl+Shift+[ the oldest, Ctrl+R or Ctrl+Shift+] the newest. Ctrl+1 to Ctrl+9: the last nine messages that arrived, 1
  the newest (NVDA's own VIP Mud module's keys; the brackets need AltGr on many layouts), answers to your keys left out
  (`Announcer.ArrivedAgo`, a Core addition); Ctrl+[ and ] go on from there. Ctrl+Shift+M marks the line you're on (or
  the newest), Ctrl+Shift+C copies the lines from the mark to the one you're on to the clipboard ("Copied 3 lines."):
  the old NVDA add-on's review, mark and copy.
- Ctrl+Shift+L: the assistant's last answer, whole, onto the clipboard ("Copied the assistant's last answer, 214
  words."; an answer given in several replies to one request is all of them; while it's still answering, what's there
  so far, said so): the blind tester's copy out of Alt+9's history took only part of a long one (2026-10-31). The
  histories also show a line's own line breaks now (Windows' edit control breaks lines only at CR LF, so an answer's
  paragraphs had run together as one line).
- Ctrl+Shift+G: the active gestures as a menu where the focus is ("Clap" with "/clap" beside it as its key), Enter
  plays one; also World > Gestures (Gestures, below).
- Ctrl+Shift+O: the sound out of the next device ("Sound on Headphones (USB Audio)."), then back to the default (Sound
  devices, below).
- Ctrl+U: upload a picture, sound, animation or model, into the folder the focus is on in the inventory or the kind's
  own (Uploading, below).
- Ctrl+B: the build editor, on what's selected in Objects, its parts or faces, else what was edited last (Building,
  below); Escape there goes back.
- Ctrl+L: About Land, on the parcel you're on (About Land, below); Escape there goes back.
- Ctrl+Shift+P: what a friend pointed at in their own viewer, selected in Objects (What a friend points at, below).
- Ctrl+Shift+T: times in the chat histories ("[14:05] Kestrel: hi", from the next line) or not.
- F3: walk mode (below); F3 or Escape again leaves it. R there, or `//record` anywhere, records a route by walking it
  (Walk mode > Recording a route); C there, or `//ride`, records a ride to hear it told after (Walk mode > Recording a
  ride).
- Ctrl+D: describe what's selected, or else where you are; Ctrl+Shift+D: you and what you're wearing; the same key
  again on the same thing: more (Describing, below).
- Ctrl+Shift+A: arrivals and departures within chat range read out, or none (Read-out, below).
- Alt+F4 (or the close button) while in world asks "Log out and quit?": Enter quits, Escape stays (Alt+F4 is next to
  Alt+4, and a slip closed the app at once in the guided test).
- Ctrl+Shift+W: what you're wearing, by name from the worn list (no model): "Wearing 6 things. Clothing: Red Blouse,
  shirt; ... Attached: Hair on Skull; ... HUDs: 7Seas HUD, top right. Body: ...". Sorted, so a "**Wearing: ..." item
  (a blind tester's outfit label) comes first. `//outfit` the same. The outfit's attachments not on the avatar yet (their links
  in Current Outfit, not attached) are said as arriving for two minutes after logging in and one after a teleport or
  reaching another region, since on Second Life they come in over a minute or so for everyone ("Wearing 5 things, 1
  more still arriving. ... Still arriving, as attachments do for a minute or so after logging in or teleporting:
  Skirt."), and after that as "In your outfit but not attached: ..."; the inventory's words for such an item say the
  same ("Skirt, object, worn, still arriving"), and Enter (Wear) attaches it again. The blind tester's skirt "looked
  missing, then re-attached itself" just after login (2026-10-31).
- Ctrl+Shift+I: incognito on; off again only by a physical key (Incognito, below).
- Ctrl+Shift+B: the builders' tab, to write to the Claude sessions building Sojourn Access (Messages to the builders,
  below).
- F4 (`access_talk_key`: f4, f12, scroll_lock, pause) held: talk in voice, with a click as the mic opens and closes;
  Shift+F4 push-to-talk or open mic; Ctrl+Shift+V who's in voice; Control+F4 the people in voice as a menu, to act on
  (Voice chat, below).

### Links

`MainForm.Links.cs`. Enter on a history's line opens the first link on it; Control+Shift+K (`//links`) lists the last ones seen, the newest first, with who said them. A web page opens in the browser, a place teleports, a person's link is their profile, a group's says About the group; nothing else opens. On the way to the screen reader, the histories, notices and profiles, a Second Life link reads as what it is: "Builders (a group)" (just "a group" the first time one she isn't in comes up, its name read for the next time), "Bay City (a place)" (with where, if two in a region have come up), "a parcel", "an event", "a classified", "Hat (an item)", "a link for viewers". Web pages read as they are. The words are remembered so Enter and Control+K still open them; a chat log's file keeps the link itself, and reads the same way in its window.

### Long lines

A blind tester's wish (2026-11-02), after a pasted description lost its end in an IM: Second Life cuts a chat line or
IM at 1023 bytes of UTF-8, so a longer one goes as several messages, in order. The session does the splitting
(`Sojourn.Core/Agents/ChatSplit.cs`, in `LLChatBar sendChat` on channel 0 and `TesselIM send`, for IMs, group chat and
conferences alike), so the assistant's messages and the VR client's dictation are split too; `MainForm.LongLines.cs` is
Access's side.
- Each part is at most 1000 bytes (room to spare under the grid's 1023), ending at a sentence's end that falls in its
  second half (. ! ? or ..., then a space and no lowercase letter, so "e.g. this" isn't one; full-width marks too),
  else at a clause's (comma, semicolon, colon, dash), else between words, else between characters, never inside one
  (whole grapheme clusters). An emote stays one: every part after the first starts "/me ". A line to a script's
  channel goes as it is.
- Parts go 0.6 s apart, so they arrive in order; a line sent while another's parts are going waits its turn. Once
  begun, every part goes.
- Said once all have gone: "Sent in 3 parts." Her own read-back skips the parts (each is matched as it comes back, by
  its words): she wrote them, and a pasted description read back again would run on for minutes.
- A line that would go as more than ten messages says "That's long enough for 13 messages. Enter again sends them
  all." and stays in the box; Enter again sends it (a whole notecard pasted by mistake would otherwise go to a group).
- The input boxes have no length limit now (Say's and a conversation's were 1023 characters, which cut a paste short
  without a word), and a paste into any one-line box keeps all its lines, joined by spaces (`LineBox`: Windows'
  one-line edit control pastes only up to the first line break, and Control+Shift+L's copy of an answer has
  paragraphs).
- `//` commands send nothing to chat (a route's lines that aren't commands are read as notes), so nothing there needs
  splitting.

### People beside chat

`chatPeople`, a blind tester's Radegast habit (2026-10-30): Radegast's nearby list sits beside chat, Tab from its input
reaching it. In Alt+1, People nearby is to the right of the history and Say. Tab goes Say, People nearby, the
history, round to Say, as Radegast's does (her order from 2026-10-31 had the history first; on 2026-11-07 she asked for
the people back between, having typed into the wrong one), so Tab from Say lands on it
("People nearby, list, Kestrel Tester, 4 metres, two o'clock, 1 of 3", the person last on, else the nearest). It's Alt+3's list made by the same code (`PeopleList`): the same words, the
Applications key's actions (Enter too), Control+D, F5; within 96 m, nearest first, "Nobody nearby." when empty. It's
kept current every 3 s while the chat area shows and the focus is elsewhere (in Say, typing), so arriving in it the
distances are at most a few seconds old; a refresh whose words haven't changed leaves the list as it is (only where
people are, for Walk to and Face, is renewed), so nothing beside the Say box churns while she types. A list saying
"Nobody nearby." is kept current with the focus in it too (Alt+3's as well): Tabbing in a moment after a teleport,
before anyone had come into view, found that line, and it stayed while she was there (2026-11-08). People arriving
come in under her with no item made the focused one, so nothing is read and the focus doesn't move; "2 people nearby
now." is said once, as a notice, and Down (or Up) reaches the first. Still nobody, the line is left as it is, not
read again every 3 s. So is one holding nothing yet (Tabbed into a second after logging in, before its first fill,
it stayed empty and silent): its first fill reads its first line, as arriving in it while it's looking does.

### People's names

Display names on Second Life are often decorative Unicode (mathematical script or bold letters, circled, squared or
fullwidth ones, small caps, upside-down text, lookalikes from Greek and Cyrillic, symbols and emoji), which a screen
reader reads as nothing, as a string of symbol names or as nonsense, so she might not know who's speaking. Alt+0's
"People's names" (`access_names`; in Reading out, and in People and offers) decides how a person's name is said
everywhere: chat and IM lines, conversations' names, People nearby, friends, notices (friends online, payments,
arrivals, a change of name), profiles, a group's members, voice, search results, links, whose a thing is.
- **display name in plain letters** (`plain`, the default): the letters under the decoration, "Marigold" for
  "𝓜𝓪𝓻𝓲𝓰𝓸𝓵𝓭 ✨"; the username when none are left ("★彡★"). An ordinary display name is said as before, so this keeps
  what Access has said from the start (display names alone), with decoration made sayable.
- **display name and username** (`both`): that, then the username in brackets when they differ ("Marigold (Juniper
  Tester)"): Radegast's default, "smart", with the display name made plain. Every line from someone with a display name
  of their own is longer this way.
- **display name as written** (`written`): exactly as other viewers show it.
- **username** (`username`): the username alone (Radegast's display names off).

Lists and profiles still add the username in brackets (the viewer's complete name), the display name worded the same
way. A profile whose display name is said otherwise than written has a line "Display name as written: ...", the one
place it's read whole (to copy for someone, or to know a name's decorated); with usernames alone, "Display name: ...".

Made plain (`Sojourn.Core/World/PlainName.cs`): Unicode's compatibility forms (NFKC) give the mathematical, fullwidth,
circled, super- and subscript letters back; tables take negative circled and squared letters, regional indicators
spelling letters (two together are a flag, left out), small caps, upside-down text (turned back and reversed when at
least two letters and a third of them are turned ones), Latin letters with decorative hooks and bars, Cherokee and
Chinese-character lookalikes, and a currency sign inside a word ("Ro$ie"). Greek and Cyrillic letters are read as the
Latin ones they look like only in a word that mixes alphabets, so "Rοѕємαrу" is "Rosemary" and "Анна Петрова" stays
itself. Marks over Latin letters that NFKC couldn't join (strikethrough, underline, zalgo), symbols, emoji, brackets and
other punctuation go (a word's own apostrophe, hyphen and full stop stay); three or more single letters in a row are
joined ("M a r i g o l d"); a word whose letters were stood in for and came out in mixed case is written as a name is
("Juniper" from "JυηιρҽR", not "Juni pe R" in a screen reader's voice). Names in a script of their own (Japanese,
Russian, Hindi, Arabic) and accented ones (Zoë, Łukasz, Nguyễn) are left as they are. `tools/namecheck` checks it on
made-up names of each style (`dotnet run --project tools/namecheck`; with names after `--`, prints each one's plain form).

The session words them: `NameCache.Style` (set from the setting before logging in and on each change; one app per
process, and the VR client keeps `Written`) governs its `DisplayName` and `Complete`, which the feed, the lists,
notices and ops use. An op's person carries `said` where the style words the name otherwise; Access reads it
(`PersonOps.SaidName`, which words a name itself where there's none, as the grid's people search) and the assistant is
told to name people by it (`assistant/prompt-access.md`). A log sent to the studio stands in for the plain form too
(the `~name` record's `plain`).

A name heard is a name she can type back, whatever the setting: `//go`, `//find`, `//follow`, `//face`, `//beacon`,
`//profile`, `//block`, `//imsound`, voice's and a group chat's members, the give window, each match any form of a name
(the one said, the username, the display name as written or in plain letters: `NameCache.Forms`,
`PersonOps.Person.Names`), and words pasted in decorative letters are matched in plain ones.

Only Second Life shows whether decorated names arrive as expected and how her screen reader says what's left. The
grid's own people search (Alt+5's people, and `//profile` or `//block` for someone not nearby, a friend or in a
conversation) matches the display name as written, so a name heard in plain letters may not find them there; the
username does. The local grid has no display names: the scripted test gives the visitor one through `SojournTestNames`
(`SOJOURN_TEST_NAMES=1`, `docs/access-testing.md`).

### The status bar

A StatusStrip at the very foot of the window, below the walk pad, holding one label named "Location: Atelier 64, 36,
15, Harbour, no fly, no build, L$0" (the parcel and its marks as `//status parcel` has them, asked again on moving a few metres, reaching a region or a minute on; where damage is on, "damage on, health 80%", as Radegast's and the viewer's show health, and health while it's below full anywhere) for the screen reader once in world (a label's name is all it reads of it): NVDA+End finds the status bar by
the window's bottom-left corner and reads its name and its children's, so "Status Location: ...".

### Read-out

- Nearby lines as "Kestrel: hello" (whispers, shouts, emotes worded so; names as People's names, above, says); a conversation's lines with its source
  ("Kestrel, IM: ...", "Kestrel, Builders: ...") unless it's the one you're in, where it's just "Kestrel: ..." (in walk
  mode, or with the window in the background, you're in none, so an IM never sounds like nearby chat); the first line
  of a new IM says "new IM".
- Notices in the feed's words ("Access Menu: pick one."; the first with keys in a session adds "Control N to
  answer."). A screen reader stops speaking at any key press or focus move, so a notice cut off that way before it
  could be heard out (within a second to three, by its length) is said again once, when the keys have rested a
  second, unless the focus has been in the notices since (she read it there, or answered it), it no longer waits, or
  the person silenced it with Control or Shift alone (the screen reader's own keys to stop or pause speech:
  `RealInput.SilencedAt`), or its words have been heard since anyway: read in another line handed to the reader after
  the cut with no key pressed or focus moved since (any key stops the reader mid-line, so a line an arrow cut wasn't
  heard out either), or said again already by the cut lines' way (below). Its words are compared as letters and
  digits, the notice's own "Control N to answer." aside (never a short line found inside a longer one: both start with
  the object's name), so a HUD's menu and the same words in its chat a second on are the same (the blind tester heard a freight delivery's words
  three and four times, 2026-11-11: the menu, the HUD's chat line, the menu said again, the chat line said again).
  The session log says what cut it, how far in, and whether it was said again or why not.
- Chat lines cut off by typing (`MainForm.CutLines.cs`): NVDA's "speech interrupt for typed characters" (and for
  Enter; both on by default) cancels all its speech at every key typed into a box, and nothing we can send survives it
  (`speech.cancelSpeech` clears every priority's queue; braille messages go at the next caret move too). A blind
  tester relies on hearing chat while she types, and turning the setting off slowed NVDA for her (2026-10-30). So an
  arrival (nearby, objects, IMs, group chat, the builders' tab) that a key typed into a box came into before it could
  have been heard out is said again, once, when the keys have rested a second, unless someone's newer line has been
  read since the last key (the talk has moved on; her own read back doesn't count), nor a line whose words a cut-off
  notice has just been said again with (`MainForm.CutLines.cs` > the same words twice), and never with Alt+0's
  `access_read_cut` off. Which lines: each is followed from
  the moment the announcer hands it to the reader (`Announcer.Spoke`), queued after the ones before, for its speaking
  time at a quick voice (30 ms a character, 0.5-10 s); the first key typed after it (`RealInput.FirstTypedAfter`)
  cuts it if it comes within that. A guess wrong that way loses a line to review rather than saying one she heard.
  On NVDA 2026.3 and later the reader is also asked every 100 ms whether it's speaking
  (`nvdaController_isSpeaking`) while arrivals are recent, and a line it was seen to fall quiet after, before the key,
  was heard out; older NVDAs say they can't, once, and the length alone decides. Never said again: her own lines (she
  wrote them), a line already said again (logged as `again`), anything after Control alone, anything while F6 mutes.
  Those cut in one stretch of typing are said together ("Kestrel: hi. Access Visitor: hello."); past three lines or
  500 characters, the newest that fit, after how many there were ("12 messages while you typed, the last 3: ...";
  Control+1 to 9 and Control+[ have them all). Before (2026-11-01), any key within a slower voice's time cut a line,
  her own included, and the lines came back at 0.7 s pauses, between words: in a busy trivia game a third of what
  was read was lines said again, many of them heard already, her own over and over.
- Her own lines (said, whispered, shouted, emotes, IMs) are read back in the same words as others' (Alt+0 "My own lines
  read back", `access_read_mine`, on by default, her wish): nearby once the region echoes them, so she hears they went
  out; IMs as they're sent (the grid echoes none). Each is read back once; typing on over it doesn't bring it back. A
  long line sent in parts isn't read back; "Sent in 3 parts." is said instead (Long lines, above).
- A conference is read and sounds as an IM does, being addressed to her though it's a session of many ("Kestrel, new conference: ..." first, then its name), with the IM's switches and sound; its tab reads "Kestrel's Conference" or "NAME (conference)", and a reply goes to the session, so to everyone in it. Second Life sends a conference's lines as dialog 0, like an IM, so Core tells one by its session not being the pair's (`AgentSession.ConferenceLine`), as the viewer's `computeSessionID` would; before, the tab was the first speaker's and replies went to them alone. Checked on the local grid only by the session's own record, since OpenSim passes a session's message on to nobody, and it starts no conference; Second Life alone shows a reply heard and one started. Inviting someone into a conference already going isn't here.
- One-off notices (friends online, alerts, a teleport that failed) are also status lines in the nearby chat history,
  as in Radegast. So is each new song's title on the parcel's stream ("Now playing: ..."; `MainForm.WatchMusicTitle`),
  with the read-out's switch (Alt+0, Music titles) and whatever F6 mutes, so a title missed can be found again (the
  blind tester, 2026-11-10: they were read, but not in chat). A title that names the song already playing in other words
  ("X by Y", then "Y - X", which one station sent two seconds apart) isn't news, read or written (`MusicStream.SameSong`).
- Friends online at login are one line (`MainForm.FriendsOnline.cs`): the grid tells of every friend online as the
  session starts, which the blind tester heard as 23 lines in the same second (2026-10-31). Those arriving while
  logging in and for 30 s after arriving (or after logging in again), with any that follow within 3 s of the last, are
  said together once they stop: "23 friends online: Ann, Bob, Cat and 20 more; the chat history names them all.", the
  history's line naming every one (two or three are all named), with one friends sound; one alone is said as before.
  After that each comes online on a line of its own, as always. Arrivals by teleports this app didn't start (an offer, home, the assistant) and region crossings are
  said ("Arrived in X.", "Now in X."). Every arrival and crossing is a line in the history too, a seated one (a train,
  a car) included ("Now in X, still on Y."), written there whether or not it's read out: the blind tester wore a
  script printing the region in local chat for this (2026-10-31).
- The reader's per-kind switches are A1's (`announce_nearby` ... in SojournSettings).
- Arrivals and departures are Firestorm's radar reports (`radar_chat_range`, `radar_draw_distance`, `radar_region`:
  enter, leave or both; off by default, as Firestorm), read as notices ("Kestrel came into chat range, 12 metres
  away.", "Kestrel left chat range."); Ctrl+Shift+A turns chat range's on (both ways) or all of them off, Alt+0 has
  each.

### Earcons

M4, `Sound/Earcons.cs`: a short sound per kind of thing arriving, so she can tell an IM from a menu from an offer before
NVDA gets to the words, or without them while read-out is muted (F6 mutes the words, never these: that way something
arriving can still be noticed without speech). Each kind is off or quiet, medium, loud, full (`earcon_KIND`, Alt+0 "IM
sound", "Offer sound" ...); a changed row plays its sound at the new level, which is how to learn them. Nothing sounds
for the conversation you're in or a source muted by Shift+F6. They stand in for the viewer's own new-IM and alert
sounds, which Access turns off, so a line never sounds twice. The words for each are the line itself.

| Kind (default) | Sound |
|---|---|
| `im` (medium) | two soft bells rising a sixth |
| `mention` (medium): a line naming you or with one of your keywords, any chat | the IM's two bells twice, quickly |
| `menu` (medium): a script menu, text box, permission or experience question | wood: two knocks and a higher one |
| `offer` (medium): teleport, friendship, an item, a place, a group, a web page | three bells falling |
| `group` (off) | wood: two notes falling |
| `nearby` (off), from where the speaker is | a low wooden pluck |
| `objects` (off), from the object | a short electronic blip |
| `friends` (off) | a breathy two-note flute, rising online, falling offline |
| `radar` (off): arrivals and departures being read out, from the person | a breath of noise swelling in (arriving) or fading out (leaving) |
| `notice` (off): group notices, payments, the region's messages | one bell |
| `sent` (off): your own line sent | a tiny tock |
| `mic` (medium): voice chat's mic opening and closing (the talk key held and let go, an open mic unmuted and muted, or left live when switching back to push to talk) | a short click, higher opening than closing |
| `deleted` (medium): something of yours deleted, from the inventory or the world, once the confirmed delete goes through (the blind tester's ask) | a brush of paper and two falling wood notes |

Placed ones sound a metre from the head in the speaker's direction, up to a third quieter at the edge of chat range.

**Someone's own IM sound** (`Sound/ImSounds.cs`, `MainForm.ImSounds.cs`; the blind tester's wish, 2026-11-01, "purely
cosmetic, for my way of thinking", after chasing down which IM a sound was for): a person's IMs, or a group's chat,
can have a sound of their own in place of the IM's (or group chat's) bells, so who wrote is known before the words.
"IM sound for NAME..." on the Applications key of a person nearby, a friend or their conversation ("Group sound for
NAME..." on a group or its chat) opens a list, the usual sound first, the one in use said so ("harp, in use"); each
plays as the arrows or a first letter reach it, Enter keeps it ("IMs from Kestrel: ocean wave."), Escape leaves it.
`//imsound NAME SOUND` does the same by words (`usual` takes it back), `//imsound NAME` says theirs, `//imsound` lists
the sounds and who has one; the name is looked for among open conversations, friends, people nearby and groups. Kept
by their key in access-settings.json (`ImSoundFor`), so a change of name keeps it; `access_im_sounds` on the agent API
(an object of key to sound, merged). Otherwise the IM's rules hold: its level (`earcon_im`; a group's, `earcon_group`,
off by default, which the choosing says), nothing for the conversation you're in or a muted one, one at a time (offline
IMs at login ring once). An IM with its own sound plays it even when it names you; a group chat line that names you
still sounds as a mention. The list plays each at the IM's level, or medium while that kind is off.

| Sound | What it is |
|---|---|
| bird call | two quick rising chirps and a falling call, high |
| cricket | three short pulsed trills, high |
| harp | a glissando up five plucked strings |
| heartbeat | two soft low thumps, lub-dub |
| horn | a soft brass fifth swelling and fading |
| music box | four tiny notes, up and back |
| ocean wave | surf swelling, brightening as it breaks, drawing back with a fizz of foam |
| sleigh bells | two shakes of small bells |
| steel drum | a steel pan's note with a grace note above it, then again, softer |
| wind chime | five small chime tubes, high and out of step |

Made in code (`Walk/CueSounds.Own.cs`), each from a family the other sounds don't use (bells are what's addressed to
you, wood is menus and groups, flute friends, a breath of noise people coming and going), none of them two notes
rising like the usual IM sound, each under half a second, and each as loud as the IM's bells (K-weighted and A-weighted
loudness of its loudest 400 ms, averaged, within about a decibel; the high ones a decibel under). No "woodblock" (wood
is menus and groups) and no "bell pair" (the usual IM sound is one).

**Volumes**: each cue and earcon its own; `access_sounds_volume` (25-200%) all of Access's own sounds together, apart
from the world's (`sounds_volume`) and the music (`music_volume`), so a loud club can be turned down without losing the
cues; `volume` everything. All in Alt+0 (the numeric ones step 0, 0.15, 0.25, 0.5 ... 2, up with Enter, down with
Shift+Enter). The screen reader is the person's main channel, so Access starts the parcel's music at 0.15 and the
world's sounds at 0.5 of the viewer's levels (the VR client keeps 1 and 1; at Firestorm Social Island the music drowned
NVDA until 0.15): put in the session's vr-settings.json once (`AccessSettings.SessionDefaults`), and only where the
value was still the VR client's default, so a person's own choice stays theirs.

### `//` commands

Radegast's syntax, so blind residents' notecard routes run as they are; typed in Say or any conversation.

| Command | Does |
|---|---|
| `//go [tp] N [dir]` | walk (tp: teleport) N metres ahead, or toward N, NNE ... NNW (16 points, or words), or left, right, back |
| `//go [tp] xyz X,Y[,Z]` | to region coordinates (Z defaults to your height) |
| `//go [tp] person NAME` / `object NAME` / `NAME` | to the nearest person whose name starts with NAME / object whose name has it; a name alone is a person, else an object; a teleport to a person lands beside them, facing them (below) |
| `//find person NAME` / `object NAME` (or just `//find NAME`) | "Kestrel is 4 m away, two o'clock, to the northeast, at your level.": everyone and everything that matches, the nearest first, the first eight and how many more |
| `//face DEG` / `DIR` / `left`/`right`/`around` / `person NAME` / `object NAME` | turn; degrees are SL's (0 east, counter-clockwise), as Radegast's |
| `//sit NAME` / `//sit ground`, `//stand` (`//unsit`) | sit on the nearest thing with that name, on the ground; stand |
| `//profile` / `//profile NAME`, `//notes NAME` | your own profile to change (Your profile and notes, below), or anyone's, by name; your notes on someone |
| `//block NAME` / `//unblock NAME` / `//blocks` | block someone or something nearby, take one off, the list (Control+Shift+Y, Enter or Delete unblocks; Block is also on a person's and an object's Applications key, and on B in a notice) |
| `//follow NAME` / `//follow NAME METRES` / `//follow stop` / `//follow` | follow as they move (2.5 m behind where they'll be in a moment, around what's in the way and up or down the stairs; at the wheel, Driving after someone, below), stop, or say who; said: when you're with them and they've stopped, when they sit, when they're getting away, when their floor is out of reach from where you can get to, when they're gone |
| `//tp list` / `N` / `NAME` / `Region/X/Y/Z` / an SLurl | landmarks in "Radegast Landmarks" (else Landmarks), by number or name; or a place |
| `//status [doing|region|parcel|location|health|money|time|music]` | any or all of them; music: what the parcel's stream is playing ("Playing: ...", in the whole status only when the parcel has music); health: "Health 80%." (in the whole status only while it's below full; the region sends it only once something hurts you where damage is on, so before that it says so); doing says "always running" while that's on |
| `//route NOTECARD` / `//route` / `//route stop` / `//route resume` | play a route: a notecard's `//` lines one at a time (with no name, the lines on the clipboard, as copied from a blog), each walk or teleport finished before the next ("Step 2 of 7."), other lines read as notes; a step that doesn't get there pauses it, `//route resume` tries that step again. In the inventory, a notecard's Applications key has Play as a route |
| `//record` / `note [WORDS]` / `undo` / `pause` (`resume`) / `save [NAME]` / `discard` | record a route by walking it: start, or while recording how it's going; a note here (no words: just a point); take back the last point or note; pause or go on; save it as a notecard in Routes (with no name, asked); throw it away (Walk mode > Recording a route) |
| `//pause [SECONDS]` | wait (in a route, before its next line; a second unless said) |
| `//thread COMMAND` | run a command without waiting for it: in a route, the next line goes on at once (typed, a command never holds the next anyway) |
| `//threads` / `//kill` | what's running (a route and its step, a walk, following, a pause, what `//thread` started); stop all of it. A teleport under way can't be stopped, and is said so |
| `//siminfo` | the region: type, maturity, people (of the limit), active scripts, objects, time dilation, frame rates; its server where the grid says (CPU class, regions per CPU, data centre) |
| `//parcelinfo` | every parcel in the region, the one you're on first: name, area, who may enter, traffic, the allowed and banned lists' sizes where the region sends them (to the parcel's owner or its group's managers), description |
| `//scriptinfo` / `//scriptinfo parcel` | the scripts in what you're wearing, per attachment and its point, and how many run; or the script memory and URLs the parcel's objects use, of what it has, the ten using most by name (the region's LandResources, as Radegast's and the viewers' Script Information) |
| `//beacon NAME` / `//beacon off` / `//beacon` | a sound on the nearest person or thing by name to walk to (walk mode's beacon, below), off, or where it is |
| `//touch NAME` / `OBJECT PART` / `NAME face DIR` | touch a thing or part by name within 10 m, on a face if given (Parts and faces, below) |
| `//details NAME` | whose a thing within 30 m is, who made it, its description, what she may do with it (else a named part within 10 m; Objects nearby, below) |
| `//point NAME` / `//point off` / `//point` | point at the nearest thing within 30 m with that name (else a named part within 10 m) for a minute, as Control+P in Objects; stop; what she's pointing at (Pointing at something yourself, below) |
| `//pay NAME` / `NAME 50`, `//buy NAME` | pay someone here or something within 30 m, buy something within 30 m (Paying and buying things, below) |
| `//switch` / `flip` / `main` / `branch` | the next rail switch and how it's set; throw it; set it to the main line or the branch, throwing it only if it isn't already (walk mode's W and Shift+W; Rail switches, below) |
| `//stop` | as Space in walk mode, from anywhere: seated on something moving, stop it by its controls (Stopping a vehicle, below); otherwise stop a walk, a follow or a route |
| `//ride` / `stop` / `describe` / `more` | record a ride: start (or, recording, how it's going), stop, have it told, tell more (walk mode's C and Shift+C; Walk mode > Recording a ride) |
| `//text` / `//text NAME` | floating text: what you're sitting on shows (walk mode's Shift+V), or the nearest thing within 30 m with that name in its name ("Ride Train shows: Throttle 30%, Speed 11 km/h.") |
| `//run` / `//run on` / `//run off` | Always run (Radegast's World > Always Run; also World's menu and Alt+0's Walking): on, walk mode's arrows, `//go` and following run rather than walk; `//run` alone turns it the other way, said ("Always running: ...", "Walking again: ..."). Kept (`walk_always_run`) |
| `//walkround` / `//walkround on` / `//walkround off` | whether `//go` and Walk to go round people standing in the way (People on the way, below; also World's menu and Alt+0's Walking): `//walkround` alone turns it the other way, said ("Walking round people: ...", "Walking straight: ..."). Kept (`walk_round_people`, on) |
| `//outfit` | what you're wearing, by name (Ctrl+Shift+W) |
| `//saveoutfit NAME` | what you're wearing saved as an outfit: a folder NAME in My Outfits with a link to each thing (with no name, it asks; also World > Save your outfit...) |
| `//upload` / `//upload PATH` | upload a file: the Open window, or the file at PATH (Ctrl+U; Uploading, below) |
| `//export` / `//export NAME` / `... as dae` (or `xml`, `oxp`) | save something she made to a file: what's edited, or the nearest of hers with that name; the Save window, its kind chosen there or by the words (Export and import, below) |
| `//import` / `//import PATH` | rebuild an object from a backup or Radegast's XML, 2 metres ahead: the Open window, or the file at PATH |
| `//autosit` / `on` / `off` / `here` / `NAME` / `now` / `forget [NAME\|here\|all]` | auto-sit: how it stands; on or off; what she's sitting on, or the nearest thing with that name, as this region's seat; sit on it now; forget one or all (Auto-sit, below) |
| `//build` / `//build COMMAND` | the build editor (Ctrl+B), or one of its commands on what's being edited, from any input (Building, below) |
| `//land` / `//land COMMAND` | About Land (Ctrl+L), or one of its commands on the parcel shown (or the one you're on), from any input (About Land, below) |
| `//describe [here|me|NAME|person NAME|object NAME|more]` | describe (Describing, below) |
| `//voice` / `people` / `mute all` / `unmute all` / `reconnect` / `answer` / `decline` / `mute NAME` / `unmute NAME` / `volume NAME N` | who's in voice, said with who's muted; the people in voice as a menu (Control+F4); everyone else muted for you, this session; everyone heard again; voice started afresh; answer or decline a call; one person muted, or how loud they are from 0 to 2 (1 as they come), for you alone and kept (Voice chat, below) |
| `//imsound NAME SOUND` / `NAME usual` / `NAME` / (nothing) | a sound of its own for someone's IMs or a group's chat; back to the usual; theirs; the sounds and who has one (Earcons, above) |
| `//chatlog` / `//chatlog NAME` | the chat logs on this PC; the one with that in its name (Chat logs, below) |
| `//emptytrash`, `//emptylostandfound` | everything in the Trash, or in Lost and Found, deleted for good, after you confirm (also on those folders' menus) |
| `//logout` | log out and go back to the login window, after you say so |
| `//about` | the version, the commit, the disclaimer and the data folder |
| `//sendlog` | send a session's log to the studio (Sending a log, below) |
| `//log` | read a session's log, as Send a log would send it (The session itself, below) |
| `//clearcache` / `//clearcache cancel` | clear the cache when the app next starts; take that back |
| `//newwindow` | another account, in a window of its own at the login |
| `//help [word]` | the commands |

On the local grid (2026-11-08) `//siminfo` says CPU class 9 and a CPU of its own (OpenSim gives no data centre),
`//parcelinfo` the traffic and, on the tester's own parcel, both lists' sizes, and `//scriptinfo parcel` gets an answer
without figures from OpenSim's LandResources ("The region sent nothing on this parcel's scripts."): Second Life's
figures are unseen. `//thread`, `//threads` and `//kill` are checked with a pause.

`//go xyz` takes coordinates past the region's edge (over 256, below 0: a recorded route's crossing, Recording a route,
below) and walks there straight by the autopilot, judged arrived by where the avatar is in the region it set out from,
and only once it's across, as the route's next line is in the next region's frame. The region's autopilot stops the
avatar at its edge without crossing (the local grid's did, 2026-11-02), so one standing there 1.5 s is walked across by
the forward control, facing the goal. A walk ended or stopped cancels the region's autopilot through
`Commands.CancelAutoPilot`, never seated: LibreMetaverse cancels by a press of forward, which a vehicle's script hears
as Up (a train opened a step by the first tap after a `//go`).
`//go` walks by the region's own autopilot, as Radegast's, around what's in the way (Paths, below): "Walking 9 m to
232, 43, around something in the way: 3 stretches." (to another floor, "..., up 3 metres: 5 stretches."), then each new
stretch ("2 of 3: 2 m to the west-northwest."). It
ends, once the avatar has stopped or is getting no nearer (below), with "Finished walking, under a metre from the
destination." (or how far, and "something was in the way" when it stalled), the cue such routes wait for. To a person or a thing it stops 1.5 m
short: the region's autopilot, heading for their middle, is let go once within that (it pushed on into the visitor's
body for 42 s, never still enough to finish, 2026-11-08).

**People on the way** are gone round, as things are (`Commands.WalkPeople.cs`, Paths below; Claude Fable 5.2,
2026-11-09). Until then a walk was planned round objects only and went straight into anyone standing on its line. Put
to the user as a choice between this and walking back after the overshoot (below), they left it to us (2026-11-09),
and this is ours: a sighted resident walks round people without thinking and a blind one can't unless the app does it;
being bumped is noticed in Second Life (viewers have bump notices), so the cost is social as well; and it removes the
case rather than mending it after. How it goes:
- **Hers to turn off** (2026-11-09, the evening): the setting `walk_round_people`, on unless she turns it off, by
  `//walkround` (alone the other way, or `on` / `off`), World > Walk round people (a check) or Alt+0's Walking, and
  on the agent API. Said either way: "Walking round people: //go and Walk to go round anyone standing in the way.
  //walkround again walks straight." and "Walking straight: //go and Walk to take no notice of people, and may walk
  into someone standing in the way. //walkround again goes round them." Off, walks are planned and walked as they
  were before that day (below, what walking into someone does). It's new and untried on Second Life, and she may
  simply prefer straight walks. A walk under way goes by it from its next look. `SOJOURN_WALK_PEOPLE=0` in the
  environment is off whatever the setting, for tests.
- **Who counts**: everyone on their feet in her region who is standing still, and anyone walking within 6 m of her
  (they'll be about there still as she passes). Not the person the walk is to, and not people sitting (on OpenSim a
  seated avatar has no body to walk into; Second Life untried). The way's line keeps 0.75 m from each one's middle,
  a metre where there's room.
- **What she hears**: a way round people that's under a metre longer than the straight line is the same walk to her,
  so the plain line, "Walking 16 metres to 240, 72.", and its stretches are walked unsaid (`Aside`; until the evening
  of 2026-11-09 it was "..., around Kestrel: 2 stretches." and "2 of 2: 10 metres to the east.", for a first stretch
  of 0.4 m when someone stood beside her). One person on open ground is always that: going round them adds 0.05 to
  0.85 m (the planner asked directly), the most for someone a metre dead ahead; so are two standing together, and
  three abreast from 6 m off. So she isn't told who she passes; what she hears named is a way round that's a walk
  of its own, with only people in the way (someone in a passage too narrow to pass them in, several across the line
  close by): "Walking 13 metres to 249, 77, around Kestrel: 3 stretches." ("Kestrel and Mira", "3 people"), each
  stretch said. With things in the way too, the line as it was.
- **Someone stepping in** after the way was planned: as she walks it looks ahead along its line (0.7 m either side),
  two seconds of her own going and 4 m at least, for anyone not allowed for, there for a quarter of a second (someone
  beside or behind her isn't in it, nor someone ahead walking the same way as fast as she is: the person she's going
  somewhere with), and plans afresh from where she is. What's said goes by how much longer the new way is than what
  was left of the old: under a metre, round people alone, it's a step aside, "Kestrel is in the way; going round."
  and no stretches said; otherwise "Kestrel is in the way; going round: 3 stretches."; and from 3 m longer, how far
  it is now, "Kestrel is in the way; going round, 14 metres from here: 3 stretches." (`Further`: she was told the
  walk's length as it set out, and it isn't that any more; the same on "Something's in the way; going round" and on
  "The way is clear now; going on", below). Five times
  a walk; after that it keeps to the way it has. Someone it went round who has since left costs only the detour. (It
  looked 4 m whatever her pace until the review of 2026-11-09: seeing someone and planning the way round took most of
  that, the new way coming 2.4 to 2.9 m short of the visitor at a walk and 1.7 m short at a run, a third of a second
  at Always run's 5.1 m/s.)
- **No way round, or none worth walking** (a doorway they stand in, people ringing the spot, or a way round that's
  10 to 12 m longer than the way through them): it goes as far as them and stops 1.5 m short, said as it sets out:
  "Walking 6 metres toward 244, 80. Kestrel is in the way and there's no short way round, so only as far as them."
  Until the evening of 2026-11-09 a way round was walked until it was some 45 m longer or left the area planned:
  far more than going round one person is worth, for someone who'll likely have moved on by the time she's there,
  and walking up to them and asking again costs her less. Going through someone now costs the search 38 cells
  once, however the way would cross them (`PathPlanner.ThroughPerson`; it was 30 for each cell too near them, so
  45 m through a person's middle and 8 m for a way that brushed one cell). Asked directly, a wall across the way:
  someone in the middle of its metre-wide doorway, the way round the wall's end is walked when that's 11.3 m
  further and not at 12.2; someone to one side of a doorway 1.5 m wide, whom the way would only brush, at 10.4 and
  not at 11.3. A row of nine people across the line 3 m ahead is gone round (7 m longer). Never the teleport
  fallback: that's for no way at all, and a person in a doorway is gone in a minute, so people never make a room a
  closed one. Arrived there it plans once more, as they may have moved on while she walked up: "The way is clear now;
  going on, 4 metres from here: 1 stretch.", or the end, "Finished walking, 4 metres from the destination: Kestrel is in the way, 2 metres,
  twelve o'clock." Someone on the walk's very spot: "Kestrel is standing there, so only as far as them." Too near
  them already to take a step: "Not walking to 244, 80: Kestrel is in the way, 2 metres, twelve o'clock, and there's no
  short way round." The region's autopilot is let go 1.3 m before the way's end (she goes on 1 to 1.5 m at a walk),
  sooner at a run (1.2 m more for each metre a second over a walk's 3.2, `LegAsync`'s lead), and once only: let go
  a metre before it at Always run's 5.1 m/s she ran on into the visitor in the doorway and moved it 0.6 m (the
  review's test, 2026-11-09).
- **Where a let-go leaves her** (found in that review, not changed): letting go is LibreMetaverse's press of forward
  (`CancelAutoPilot`), and on OpenSim each press is a step, 0.5 to 1.5 m at a walk and 2 to 3 m at a run, taken, by
  the look of it, the way she faced before the walk: the region's autopilot turns her as it walks, the app's own
  heading for her stays, and the press carries that heading. So where a walk that's let go ends depends on which way
  she happened to face. Ending short of the visitor in the doorway, she stands 1.9 m from it facing it, or 2.7 m
  from it having faced away ("... is in the way, 3 metres, six o'clock"), and at a run 2.5 m or 7 m. The same
  for a walk to a person or a thing, which is older: `//go person` let go at 1.5 m ended 2.0 to 2.7 m from the
  visitor in five runs (stepped back, and again when the walk then ended as stood still and was let go once more)
  and 0.4 m from it in a sixth, "Finished walking, under a metre from the destination."; with Always run on it passed within
  0.6 m of the visitor. The visitor wasn't moved in any of these, nor by her walking between two people 0.62 m
  either side of her line, so bodies on OpenSim meet nearer than the 0.6 to 0.75 m reckoned above (two avatars
  logged in on one spot settle 0.5 m apart). What Second Life does with the press isn't known; the blind tester's
  walk logs would show it (a `walk over` line whose `off` is more than its `nearest` is a step back). A let-go that
  doesn't move her, or one made facing the way she walked with the let-go points set for it, would make where these
  walks end the app's own choice.
- Standing right beside someone as she sets out, the way leaves away from them, never nearer than she is. Among
  several (a landing point's pile, a knot of people talking) there may be no such step: three people within 0.7 m
  round her, four within a metre or six within 1.5 m left none, and the walk was refused ("Not walking ...") for people
  she was touching already. So where no step can be taken and two or more stand within 1.1 m of her, those are left
  out of the way and it sets out through them as every walk did before, still going round everyone else
  (`Bystanders.Beside`; Claude Fable 5.2's review, 2026-11-09; on the local grid, three within 0.8 m: refused
  before, and after it she walked out between two of them 0.62 m either side, none moved). One person so near with
  no step to take is a doorway they stand in, and that stays "Not walking"; so does being ringed by people further
  off than 1.1 m (the six at 1.5 m), which waits on how near a way may pass someone.
- A stretch that sticks against someone (within 1.2 m, ahead of her to 60 degrees either side) ends the walk with
  that last line, not by marking the spot and pushing on, and a straight walk that ends short against someone names
  them where it said "something was in the way".
- **How much room it needs** (the planner asked directly, flat ground, 2026-11-09; a quarter-metre grid, so to about
  0.2 m): past someone standing in the middle of a corridor it goes at 2.6 m wide and is cut short of them at 2.4 m;
  past someone 0.5 m from one wall, at 1.8 m; between two people, when they stand 1.6 m apart or more. Scattered
  people, one to every 2 m square, never stopped a 12 m walk in 40 tries (2.3 m longer on average); at one to every
  1.6 m square 11 of 40 were cut short or not begun. (That was before the limit on how far round a way goes; in a
  scatter made afresh for it, uniform over 24 by 16 m, the limit changed one 12 m walk in 160: cut short where its
  way round had been 20 m longer.) Bodies on OpenSim meet at about 0.5 m (above), so 1.6 to 1.8 m
  of corridor is what passing someone without touching takes: the rest is the room it keeps and the grid. Whether
  that suits Second Life's shops and clubs is for the checklist there.
- **Not covered**: a walk of under 2 m or across a border (neither is planned), walk mode's own keys, following and
  the assistant's walks (the session's autopilot), which go as they did. And nothing here is tried on Second Life yet
  (`docs/access-testing.md` > Walking round people on Second Life).
- On the local grid (2026-11-09) the walk that slid, 22 m with the visitor 4 m along it, ends on the point in 11 s,
  four of four, never nearer the visitor than 1.4 m, the visitor not moved (said then as "Walking 23 metres to 84,
  46, around Walk Visitor: 2 stretches.", now as the plain line); the doorway, the stepping in and the rest are the
  test's "walking round people" step. With the setting off (or `SOJOURN_WALK_PEOPLE=0`) it walks as before, which
  is also how to measure what a grid does when one avatar walks into another.

What walking into someone does, where it still can: on OpenSim it shoves them along. An
avatar another avatar touches loses the motor that holds it in place and brakes it (ubODE's `OdeCharacter`,
`IsCollidingObj`), so the visitor, 4 m along a 22 m walk, slid ahead of her at walking speed all the way to the point, and when the
autopilot arrived there she had no brake either: both slid on 7 m, then crept (six walks of six, the visitor left 25 m
from where he stood, 2026-11-09; with nobody on the line the same walk ends on the point in 12 s). A walk then ended
only once she had stood still two seconds, and the creep kept that from passing: "Finished walking, 7 metres from the
destination (something was in the way)" came 40 s after she set out. So a walk also ends when it gets no nearer (the
user's choice of the remedies, 2026-11-09; `Commands.WatchWalkAsync`), the backstop still: three seconds of the region's updates of her
with none 0.1 m nearer the point than she has yet been, and no longer travelling (under 0.25 m/s from one update to
the next; ten seconds in all at most), so the distance said is where the slide left her, not a point on the way. The
same walk now says "Finished walking, 6 metres from the destination (something was in the way)" 18-19 s after she set
out, as the slide ends (nine slides in thirteen walks; twice of those she stood two seconds first, as before). It's judged
by the updates as they come, not by the clock, so a walk going on through a gap in them isn't given up on (the region
sends few at a steady walk, and the position between them is her last one carried on by its velocity); the walk is one
straight line by the region's autopilot, whose push along whatever it meets still closes on the point, so updates that
come and show her no nearer are a walk that isn't getting there. Up to a border not crossed yet it doesn't apply (the
push across is what a stop there gets). The stretches of a planned way already ended so (`LegAsync`, two seconds, then
planned again), and the session's own autopilot (the assistant's walks, in the VR client too) always has (`Motion`).
What Second Life does where OpenSim slides isn't known.

Seated (a chair, a pose ball), it stands
first ("Standing up to walk."). A goal out of reach where the floor meets it (up on a stage with no steps, inside a
bar) is walked as near as the way goes: "Walking 6 metres toward Kestrel; Kestrel is out of reach, so as near as the
way goes, 2 metres short.", ending "Finished walking, as near to Kestrel as the way goes: Kestrel is 2 metres, twelve
o'clock." With no way there at all (a room of its own behind a closed door, or where walking would leave her a storey
under them: Walk mode > Paths, below) it says so once, and teleports there
only where that can land beside it (`walk_teleport_fallback`, on): "No way to walk to 244, 46 found: teleporting
there." Not where the region has a telehub or the parcel a landing point (its owner excepted), which in Second Life
is most clubs and shops ("... and a teleport wouldn't land there: the parcel there sends teleports to its landing
point."), and not again for the same spot for five minutes after one that landed elsewhere, which is said ("The
teleport landed you away from Kestrel: Kestrel is 14 metres, two o'clock."), so asking again doesn't teleport again
(the blind tester's walks to her partner in a jazz club did, 2026-10-31). With the fallback
off, the line names the command that would try (`//go tp person Kestrel`). `//go tp` says where it really landed the
same way. Unlike Radegast (a bug there), `//face object` and `//find object` look for the name alone.

A teleport to someone lands beside them, not on them: `//go tp person`, a walk to them (`//go`, a person's Walk to)
that ends in the fallback's teleport, the assistant's walk to them that does, any `//go tp` to a spot someone stands at
(within a metre). Sent to their own position, the region stood her on their head, and the lists rightly said "under a
metre, below you" (2026-11-08). So it lands 2 m off on the side she comes from, on their floor, turns her to face them
and says where they are: "Teleported to Kestrel: Kestrel is 2 metres, twelve o'clock." With the collision model the
spot is planned out from where they stand (`Commands.BesideAsync`, the walk's `PathPlanner`): the nearest spot to that
side their floor reaches in a near-straight line, at that floor's height; if it's off what they stand on (beyond a
stage's edge) or behind something, the next side round, up to all eight; none better, that first side. Without the
model, or where it hasn't what they stand on, that side at their height, a little over it, so someone up on a stage
still has her land on the stage, not on the ground under them. The assistant's own go tool teleports 2 m short of a
person or thing on the side she comes from, at its height, and faces it on the desktop, but doesn't plan the spot.

### Walk mode

F3; M3, after a blind tester's request for arrow keys with a sound per step and cues for openings and doors, as in the
audio game Shades of Doom. F3 puts the focus on the walk pad, a plain pane at the foot of the window ("Walk mode", out
of the Tab order: not an edit or a list, so the screen reader passes the arrows and letters through); F3 or Escape goes
back where the focus was ("Walk mode off."), and any other way of leaving it (Alt+number, another window) lets go of
every key. The arrows move the avatar through the region's own movement (the agent's control flags, as Radegast's 3D
window and every viewer send them); the local collision model only tells you what's around.

| Key (in walk mode) | Does |
|---|---|
| Up / Down | walk forward / back while held (Shift: run; with Always run on, `//run`, they run anyway); let go and it stops where you are (the viewer's STOP flag, no coasting on) |
| Left / Right | turn: a tap one step (22.5 degrees by default, landing on the compass's sixteen points, said: "north-northwest."), held smoothly (90 a second; where you face is said when you let go) |
| Shift+Left / Right | a step sideways while held |
| Page Up / Page Down | up / down: flying, rise and sink; on foot, jump and crouch |
| Home | fly, or land ("Flying." / "Not flying: landing.") |
| Enter | the mouse button, for a script that takes clicks (a vehicle's horn or gun, a weapon): down while Enter is held, up when let go; with nothing taking them, "Nothing here takes clicks. T touches what's ahead." |
| Space | stop everything: the keys, a `//go`, a follow, a route ("Stopped."); seated on something moving, stop it by its controls ("Stopping.", then "Stopped.": Stopping a vehicle, below) |
| H | heading: "Facing northeast, 45 degrees." (Second Life's degrees, 0 east, counter-clockwise, as `//face` takes them) |
| V / Shift+V | speed: "Walking, 3 m a second, to the north." / "Standing still." / seated, the vehicle's, then what it shows as floating text when it shows any ("Ride Train: 3 metres a second, to the east. It shows: Throttle 30%, Speed 11 km/h."); Shift+V (or `//text`): only what it shows ("Ride Train shows: ...") |
| L | where: "Atelier East, 236, 52, 22, on stone." |
| A | what's ahead: the first thing straight ahead within 30 m, "Blue Door, 4 m ahead." or "Clear for 30 m ahead." |
| S | the sonar: walls on four sides, openings by clock face, doors by name; the words and a sweep of sound (below) |
| T | touch what's ahead within 3 m, where the ray met it (a door opens): "Touched Blue Door." |
| W / Shift+W | the next rail switch and how it's set: "Switch 35 metres ahead, set to the main line, North Station. Main line: North Station; branch: East Yard."; along a track whose rails say, which side it takes you or whether it's set for you ("Switch 60 metres ahead, set to the main line, Hill Line: it takes you left; the branch, East Yard, goes right. ..."); Shift+W throws it, saying the new setting once it has changed (Rail switches, below) |
| D | doors and gates by name within 15 m: "Garden Gate, 6 m, two o'clock." |
| O | the marks: doors, seats and touchable things within 12 m, said and sounded from where they are (below) |
| B / Shift+B | where the beacon is ("Beacon on Bench, 4 metres, twelve o'clock." / "Heading to 54, 41, ..."); Shift+B takes one you put on off |
| R / Shift+R | record a route (Recording a route, below): R starts, and while recording says how it's going ("Recording: 6 steps, 48 metres walked; the last point is 3 metres, seven o'clock."); Shift+R ends it, asks its name and saves it |
| N / Backspace / P | while recording: a note here (a box for its words; left empty, just a point); take back the last point or note; pause, or go on |
| C / Shift+C | record a ride (Recording a ride, below): C starts or stops it; Shift+C has the assistant tell it, again for more |
| M / Shift+M | how far: "Last walk: 0.9 metres. In all: 4.2 metres. Where the count began: 3.1 metres, six o'clock." (while walking, "... so far"); Shift+M: "Counting from here." (the count also starts again at a teleport) |
| F1 | these keys |

Seated on something, or with a script holding the movement keys, the same arrows drive it (CONTROL_FWD and BACK,
ROT_LEFT and RIGHT, UP and DOWN for Page Up and Down, a tap held long enough to reach the script): a car, a horse, a
boat, a ride's controls. A script sees only the controls, never the keys: to it Up and Down are W and S, Left and
Right A and D, Shift with them Shift+A and D, Page Up and Down E and C (a vehicle's "press C to land" is Page Down),
so walk mode's letters are free for its own keys (the blind tester asked, 2026-11-03, after learning some vehicles land
on C). A layout where W, A, S, D, E and C drive, for a keyboard without arrows, waits until someone needs it. Enter is
the left mouse button (CONTROL_LBUTTON; the mouselook one when that's all the script took). H and V then say the vehicle's heading and speed, A and the warnings look ahead from it,
leaving out its own parts. A rail switch coming up is said once with how it's set, W says the next one and Shift+W
throws it (Rail switches, below). Space (or `//stop`) stops it, and a vehicle saying its track ends is stopped by itself
(Stopping a vehicle, below).

**The cues**, each a sound and a line for the screen reader (sound is never the only channel), each with its own
setting for how it reaches you (both, text, sound or off; Alt+0 says both as "sound and text") and its sound's level
(quiet, medium, loud, full), in Alt+0 and on SojournSettings (`walk_NAME`, `walk_NAME_volume`). The sounds are our own,
made in code (`Walk/CueSounds.cs`), played through world audio's mixer, placed: a cue about something on your left is
in your left ear.

| Cue (setting, default) | When | Sound | Line |
|---|---|---|---|
| footsteps (`walk_steps`, sound) | each footfall while walking on the ground (also on `//go` and follows), by surface: ground, stone, wood, metal, glass, soft (flesh, plastic, rubber), water | a step for that surface, three takes a little apart so a walk doesn't sound mechanical | with text on, the surface when it changes: "On wood." |
| warning (`walk_walls`, both) | walking in walk mode toward something within about a second and a half (at least 1.8 m); once per thing until it's been clear a moment (again every 4 s while it's still ahead); its name asked for once it's within twice that, so the warning can name it (Second Life sends no names with objects) | two short falling tones; a door (named door or gate): two knocks | "Wall, 2 m." / "Blue Door, 4 m." / a person's name; "Something" for what isn't upright |
| blocked (`walk_walls`) | a walking key held, the region not moving you | a dull thud | "Blocked." |
| openings (`walk_openings`, both) | passing a gap in a wall beside you (it was within 3 m for a metre, now open beyond 4.5 m); a named door within 3 m beside you | a rising two-note chime, or knocks, from that side | "Opening on the left." / "Garden Door on the right." |
| edges (`walk_edges`, both) | a drop of more than 1.5 m a step and a half ahead; water ahead when you're dry | a falling glide; three bloops for water | "Drop ahead, 3 m down." / "Water ahead." |
| turns (`walk_turns`, text) | after a step turn, or a held turn let go | a tick | "north-northwest." / "east, 0 degrees." |
| sonar (`walk_sonar`, both) | S | twelve pings, one from each hour of the clock, starting ahead and going round to the right: higher the nearer (under 2.5 m, under 7, further), a breath where it's open (nothing within 15 m); then a three-note chime from each opening | "Walls: ahead 11 m, right 1 m, left 1 m. Open: behind. Opening at twelve o'clock, 6 m away, 1.4 m wide. Opening at six o'clock, under a metre away, 2.8 m wide." and doors by name |
| distance (`walk_distance`, text) | a stretch walked by the arrows has ended: no walking key for 0.7 s, no key at all for 0.7 s, the avatar stopped (or 3 s on), and walk mode's line before it heard out; taps in quick succession (a turn between them too) are one stretch; straight from where it began; under 0.3 m nothing; not a `//go`, a follow or a vehicle | none | "0.9 metres." / "4 metres." (tenths under 3 m: a tap is about 0.9 on Second Life) / "1.8 metres backward." / "1 metre to the left." (by the keys that walked it) |
| follow (`walk_follow`, text) | following someone | none | "With Kestrel, 3 m, twelve o'clock." / "Kestrel sat down, ..." / "Kestrel is getting away: 24 m, ..." |
| vehicle (`walk_vehicle`, both) | driving: what's ahead (as the warning, further at speed), only what the vehicle is closing on (met for 0.4 s or more and come nearer by at least half what it travelled meanwhile; each thing once in 4 s): on a train a ray from the seat meets the track's own sections and guides ahead on every curve, which stay about as far ahead as the track bends away (the blind tester's ride of 2026-11-02 had 157 such warnings); a seated border crossing, judged once the new region has sent the avatar and the seat it's on (up to 15 s, else said as not known yet: judged at 2.5 s whatever had come, it told her "the crossing left you standing" seven times while she sat on; said as not known, what the region then says, within 2 minutes, follows as "Bay City has you standing now, off Walk Kart." or "Bay City has you on Walk Kart now.", and the session log says what the region had of her avatar and where the seat was, at 15 s and if still unknown at 2 minutes: a tour car's crossing on 2026-11-11 left her with "it hasn't said yet" and nothing after); the speed has its own switch, `walk_speed` (on): the distance travelled over 2 s, said once it has held within 15% for 2 s and differs from the last said by 3 m/s and a third (a train's reported velocity swung 19, 6, 13, 8 three seconds apart, 2026-11-01); stood up off the seat within 5 s of its moving, not at a crossing (the region left her behind as her train crossed out, 2026-11-02, and only the train's own chat said so) | as the warning | "12 metres a second." / "Now in Bay City, still on Walk Kart." / "Now in Bay City; the crossing left you standing." / "Bay City has you standing now, off Walk Kart." / "You're off Walk Kart: it went on without you." |
| switch (`walk_switch`, both) | seated and moving: a rail switch coming into the way ahead (along the track where its rails are known) within about 7 s of travel (60-100 m), said once (one a line, the nearest first, 2.5 s apart; again only after a minute out of the way ahead; not once it's under 5 m; under 1.5 s away, "just ahead"); the next one ahead said again if its setting changes; a throw done (W, Shift+W, `//switch`). Whatever's said of a switch is written in the nearby chat history too, to copy (the blind tester, 2026-11-04, to show her partner). A switch's line is heard out (about 30 ms a character): the lines walk mode says by itself meanwhile (the seated crossing, the speed, the next switch, a route's point) wait for it and then follow in turn, as an answer cuts off what's being said (0.1.0.835 cut 25 of 146, both before her wrong turn); warnings, the answers to her keys and "Track ends" still cut in | a points' clack, then one note forking into two that part; a throw: a heavy clack and a lighter answer | "Switch in 40 metres, set to the main line, North Station." / along the track: "Switch in 40 metres, set to the main line, Hill Line: it takes you left; the branch, East Yard, goes right." / "Switch in 60 metres where a line joins from the right: set for the other line." / "The switch ahead is now set to the main line." / "Switch set to the branch, East Yard." |
| beacon (`walk_beacon`, both; volume quiet) | a walk under way (`//go`, walk to, a follow, the assistant's walk), else one put on someone or something | a soft pip from the target's direction, every 1.4 s far off down to 0.6 s near, with a fifth above it while it's within 20 degrees ahead (front and back are hard to tell by ear); a small bell flourish on reaching a hand beacon | "Reached Bench." / "Beacon lost: Kestrel isn't here now."; B says where it is |
| route (`walk_route`, sound; volume quiet) | recording a route: each point it keeps (Recording a route, below) | one small bright wooden tick | "Step 4: a doorway." / "Step 5: a turn." / "Step 7: a teleport to Bay City." |
| marks (`walk_marks`, both) | O | each thing in turn round from ahead to the right, as the sonar: a door knocks (as the warning's door), a seat sinks (two low notes falling), a touchable thing taps (a small high bell) | "Bench, a seat, 4 metres, twelve o'clock. Garden Gate, a door, 4 metres, three o'clock. Bell, touchable, 4 metres, nine o'clock." |

Other settings: `walk_turn_step` (smooth, 10, 15, 22.5, 30, 45, 90 degrees), `walk_turn_rate` (45-180 degrees a
second), `walk_teleport_fallback` (on), `walk_round_people` (on: `//go` > People on the way, above).

**The beacon** (M4): while the app walks somewhere (a `//go`, walk to, a follow: on the person as they move, the
assistant's walk: the session's autopilot's last point), a pip comes from where it's heading, placed a metre from the
head that way, so it's heard whatever the distance (louder only a little as it nears). With no walk under way it sounds
on what the person put it on, to walk to themselves in walk mode or by `//go`: `//beacon NAME` (the nearest person,
else thing, by name; `person`/`object` as `//find`), or Beacon on a person's or an object's Applications key. That one
stays until it's reached (within 1.6 m: "Reached ...", the flourish), taken off (Shift+B, `//beacon off`) or gone for
5 s; walking by hand doesn't stop it (it stops a `//go`), and a walk's own beacon takes its place while the walk lasts.
It finds its target in every region in view, so a car it's on crossing into the next region keeps it (0.1.0.877 lost her
partner's car 5 s after it crossed). For a person, the region whose map list (CoarseLocationUpdate) names them is where
they are: its copy of them, else their place on that list (to a metre); then a copy in a region with no list; then a copy
in a region whose list leaves them out, one that came in within 12 s first (a handover over a border can keep someone off
both lists for a few seconds), last an older one, most likely left behind where they crossed out, never killed by that
region (the own region used to be asked first and answered with it). A copy sitting on something not sent yet isn't
taken: its position is only its offset from the seat (`WalkMode.See`, 2026-11-08). `//follow`
while seated drives after them where the keys steer (Driving after someone, below), else puts one on them, saying why. While a beacon sounds the world's sounds go
softer (`walk_beacon_duck`: as usual, softer, much softer (the default, a quarter), very soft; over half a second each
way; not music or Access's own sounds): two cars' engines drowned it at its loudest (the blind tester, 2026-11-03).

**The marks** (O): from the objects list's flags and names within 12 m, at most eight: a door or gate by name, a seat
(sits on a click, or named chair, seat, bench, sofa, couch, stool, cushion, poseball, hammock, swing), anything a script
takes touches; never what you're sitting on.

**Rail switches** (`Walk/WalkMode.Switches.cs`; Claude Opus 5.5, 2026-11-01, after the blind tester took two wrong turns
at switches driving her partner's train and had to back up and reset them). Built on the Second Life Railroad's switch
as the SL wiki documents it ("Second Life Railroad/SLRR standards" and ".../SLRR Switch", 2010-2012, which the Virtual
Rail Consortium's free tools follow), and first met on real track on 2026-11-02 (below):
- From the standard: the switch is a linkset whose root is named "Switch Stand", with a large invisible part over the
  stand taking touches (for "click-able switches"), and dwarf signals. Its description is
  `Identifier~Status~Heading Name~Mainline Name~Branch Name~messaging channel`, Status "Main" or "Branch" ("for train
  builders/scripters to sense and react"). Trains steer by invisible guide rails: the one in use named "Guide" and
  solid, the other renamed "Alternate Guide" and phantom. The stand listens for "Main" or "Branch" on its channel and
  shouts the new setting there when done. A mainline switch goes back to the main line 2.5 minutes after being set to
  the branch, and sensors at the merge set it right for a train coming into the merge by themselves (spring switches).
- From an SL feedback report (Pawpaw Wye, 2025-12, with merged posts to 2026-07), so still so today: touching the
  stand, or Main and Branch on its channel, throw it; that stand had changed while its guide rails hadn't ("Guide on
  the main line and Alternate Guide on the branch" stayed put), and a large invisible "Switch Controller" meant to be
  linked to the stand for clicks had come unlinked. Two Tenera switches of the Crumbi Junction wye were reported the
  same way in 2026-07. So the guide rails are objects of their own, which the stand talks to.
- Not from the standard: an unsourced description passed on in Second Life had Guide always the main line's rail and
  Alternate Guide the branch's, one solid at a time; the standard renames them instead, so the names say only which rail
  is in use, never which way. Whether touching toggles or opens a menu isn't documented.
- **On real track** (the blind tester's freight run over the Linden railroad's atoll lines, 0.1.0.811, 2026-11-02, 108
  minutes and 181 crossings; log `tester-20261103T000721-ef2577`): stands named "Switch Stand" with the standard's
  description were read right: 104 lines named a way ("set to the main line, Aglia Station", "set to the branch,
  Lunalis Passing Track"; some stands carry the bare words, "the main line, Main", "the branch, Branch"). Twice a
  switch said as set to the main line changed to the branch some 10-20 s later as she neared it ("The switch ahead is
  now set to the branch, Clearwing passing track."): most likely the merge's sensors springing it for her train (or
  another driver). But 178 lines said "unknown", often several within seconds at growing distances around a station's
  named switches (17:50:31-40, four in 9 s). The log
  didn't say which kind each was. Most likely the switches' own guide rails: objects of their own (above), the
  branch's reaching tens of metres past the stand, taken for switches of their own whenever more than 25 m from a stand
  already named (and before its name had come). But W, pressed three times at 18:04:58-18:05:02, found stands (no "No
  switch stand found") that didn't say Main or Branch, so some unknowns are stands of some other description, or
  objects with "switch stand" in a longer name. Five lines said distances of 135-260 m, each at a crossing: the
  position was taken in the old region's frame and measured against the new one's (fixed). No throw was tried (she
  heard of W and Shift+W mid-run and said she'd try another time).
- **The second real log** (0.1.0.877, 2026-11-03, a freight run, `tester-20261103T224954-3fb77f`): stands and their
  Alternate Guides were placed right (guides 10 m long, single phantom prims, one every 7-10 m along the branch out to
  45 m from their stand), but most switches were said only 5-25 m out at 11 m/s, as found late: names were asked
  nearest first all round, and a busy track's objects beside and behind came before the way ahead. Some stands on the
  Linden track have an empty description (the same 32-prim stand), and some stands carry "Main" and "Branch" as the
  lines' names. Since: names asked for the way ahead first (nearest along it), thirty a look; said within about 7 s of
  travel (60-100 m) and names asked 30 m further; under a second and a half away it's "Switch just ahead, ..."; "Main"
  and "Branch" as names aren't repeated; a stand that says nothing is said as such ("its stand doesn't say how it's
  set"), a rail with no stand found as "how it's set isn't known: no switch stand found for it". The log adds where each
  was from her (along and aside the way, her speed) when found and when it came into the way ahead, which way each
  part faces, and the Guide and Alternate Guide rails near each stand with their places, lengths and facings, for
  working out which way each line goes.
- **The third real log** (0.1.0.891, 2026-11-03, `tester-20261104T025444-eb47e5`, her train from Neumoegen past
  Achemon to Concinna): switches were said 40-95 m ahead, mostly 50-70. Its `rails near` lines (29 stands) were
  reconstructed offline (Claude Opus 5.5): the name, not phantom, says which rail is in use. Alternate Guides were
  phantom 111 times in 114, but a quarter of the Guides were phantom too, and she rode over phantom Guides: on Lunalis's
  main line before the stand at 102, 157 (her place worked out from two rails found the same second, both dead ahead),
  and north out of Neumoegen station. At Lunalis the stand at 43, 100 changed from Branch to Main as she came up to it,
  and the rails there carry both names at one place, as the standard's renaming would. Positions in those lines were
  rounded to the metre, cut at 24 rails a stand and only rails already named, so yards (Achemon's, Obscure's ladder)
  can't be followed from them: the log now carries more (below).
- **Along the track** (`Walk/RailTrack.cs`; Claude Opus 5.5, 2026-11-03, after two switches found 100 m out left the
  straight band on a curve, and a parallel line's switch could fall inside it): seated, the rails named Guide and
  Alternate Guide within reach are followed from the seat. The start is the rail under the seat (within 2.5 m flat,
  running within 35 degrees of the way the seat goes, Guides first; if another line's rail is about as near, no track:
  which it's on isn't clear). From each rail's end the next is the one laid on from it (an end within 1 m, turning at
  most 30 degrees, within 2.5 m up or down). Every rail going on from a rail's span is followed 50 m to see where it goes:
  rails whose ways stay within 2.5 m are one line (switches' rails overlap for a while), ways further apart are two. The
  track goes on along the line with a Guide; where the other line's first rail is an alternate, that's a **facing**
  switch set for the line taken, and which side the other goes is where it's furthest from this one within 50 m. Two
  lines with Guides (or two with none) ahead: unclear, and the track stops there. A rail running in beside the track
  whose way back parts from the track's (followed back 50 m) is a line **joining** it: a **trailing** switch, placed
  where the joining line's rails reach the track's own, set for the person if their line's rails up to there are
  Guides and the other's an alternate, against them if theirs has an alternate and the other a Guide; else unclear.
  Only alternates going on: the track follows them (a switch set against it), and says so in the log if they end.
  The track reaches about as far as switches are said (90-130 m). Each stand is placed on it: at the place lines part
  or join nearest it within 12 m (one stand a place, those saying Main or Branch first), else on the track if within 3 m
  of its line (a switch whose other line's rails aren't named yet), else beyond the track's end by the straight band on
  from there, else not on the track (a line beside it: not said). A place where lines part or join with no stand is a
  switch of its own when its rails say something that matters: which side a facing one takes the person, or a
  trailing one set against them. With no rails known (or none under the seat), it's the straight band from the seat,
  as before.
- What's said, along the track: a facing switch with its rails clear, "Switch in 40 metres, set to the main line, Hill
  Line: it takes you left; the branch, East Yard, goes right." (the stand's names for the line taken and the other;
  without a stand that says, "Switch in 40 metres: it takes you left; the other line goes right."); a trailing one,
  "Switch in 40 metres where a line joins from the right: set for the other line." (or "set for your line"; "where another line
  joins" if its side isn't clear). Set for the other line, it's held until 2.5 s of travel or 20 m away: such a join
  usually springs for the train as it nears (Sinica and Euclidia, 0.1.0.903). Where the rails don't say (two Guides, no other line found, the side too close to
  call), it's the stand's words alone, as before: she can't check, and a wrong side is worse than none. W says the same,
  with both names. The next switch ahead is read again every 4 s, its rails too: if what they do changes, "The switch
  ahead now takes you right." / "The switch ahead is now set for your line."
- Names: the region sends a new name only to whoever has the object selected, so a rail renamed by a throw keeps its
  old name here until asked again. Rails with scripts near a switch are asked again each time it's said or read again,
  and any rail whose phantom flag flips (that does come by itself) is asked again at once. Rail-shaped roots (one prim,
  one axis 1.5-20 m, the others a metre or less) have their names asked first, twelve a look, so the track is known
  before the switches on it.
- So: the setting is read from the stand's description (asked again each time it's said: the region sends a changed
  description only to whoever has the object selected), with the main line's or branch's name from it when it has one.
  What doesn't say its setting (an "Alternate Guide" or "AlternateGuide" root, as one Marketplace kit names it, or a
  stand whose description doesn't give Main or Branch) is part of the switch whose stand does, within 60 m; with none,
  it waits 2 s for such a stand's name (the names around it asked for first) before it counts as a switch of its own,
  said with its setting unknown and not thrown (W doesn't wait). Names with "guide", "signal", "sensor" or "light" in
  them aren't stands (the standard's "Switch Stand Signal", a junction kit's "Switchstand Guide X2"). Switch stands are
  found among roots within 150 m in the regions in view and offered within 90 m, their names asked for twenty a look,
  nearest first ahead, never again those asked in the last 5 s (twenty that never answered held back every name behind
  them). "Ahead" is along the track where it's known (above); with no track, within 60 m along the way the seat moves
  (its facing, stopped), within 6 m either side and a third of the distance more. Where a switch is when it's said is
  taken again then (with a track, the track is followed again with what was just read), in the frame the person is in
  then; one first due closer than 5 m isn't said (too near to act on; 0.1.0.811 said several at 1-4 m).
- The log says what it finds (`switch: ...` lines, kept by Send a log): each stand or alternate guide the first time
  (name, local id, region and place, description as written, prim count, size, phantom, which parts take touches) and
  again when its name or description changes; where each part that doesn't say its setting went ("part of Switch
  Stand (...), 31 m from it", or "a switch of its own: no stand saying how it's set within 60 metres (the nearest 75
  metres away)"); each line said or not said and why, with what its rails said and where it was on the track; W's
  answers; and a throw's touched part, the setting before and after with the milliseconds it took, and each guide
  rail's name and phantom before and after. The rails near a stand are listed to the decimetre, nearest first, forty at
  most. Along the track: `track from REGION x, y, z heading H` with each rail taken (G or A, local id, region, place,
  length, facing, phantom, scripted) and why it ends, when what it found changes and every 15 s while moving; each place
  lines part or join the first time and when its verdict changes (`lines part 52 m along the track ...`, both lines'
  rails and points); `no track` with the nearest rail when there are rails but none under the seat; and, once past a
  switch it said, which line the seat went on (`passed the facing switch ... now on the line it said it would take`, or
  `on the OTHER line`).
- Throwing (Shift+W, `//switch flip`, `main`, `branch`) touches the stand's part that takes touches (the largest, if
  several; the stand itself if none), as a person's click, then reads the description until its Status changes (6 s at
  most) and says the new setting; a switch being read meanwhile (each is read as it comes up, and the next one
  again every 4 s) is waited for, up to 3 s, rather than refused. The guide rails within 60 m of the stand are watched
  too: if the stand changed and they didn't, it's said ("Its guide rails didn't change, though: the stand may be out of order"); a stand without a
  Status, if they changed, "Switch thrown: its guide rails changed, but its stand doesn't say which way." The channel
  commands aren't used: neighbouring stands may share a channel, and a shout would throw them all. A switch across a
  border is said but can't be thrown from here (touches go to the current region).
- Checked offline against the third log's rails (the same code, `RailTrack.Follow` with a slack for the rounding):
  Neumoegen's stand at 101, 135 came out consistent all three ways (its stand said Main, the main line's rails Guides,
  the branch's alternates; she only left northward there, so none of it was ridden through): southbound from the
  station, a facing switch 1 m on, the branch's alternates going off to the right (west); northbound on the main line,
  trailing, set for her, the other
  line joining from the left; northbound on the branch, trailing, set against her (its rails alternates), the main line
  joining from the right. Achemon's Switchstand Main pair at 235, 125 and 222, 172 is a crossover between the yard's
  two lines, its rails all Guides (phantom): from the east line northbound, facing onto the crossover, taking her left,
  then trailing onto the west line, set for her, joining from the left; from the west line northbound, the crossover
  joins from the right with her line's rails alternates: against. Achemon's Switch Stand at 14, 230 (said "set to the
  branch, Obscure SLRR Info Station") gave no verdict: its two lines' rails run on into Obscure before they part, past
  what the log held. At Lunalis 102, 157 the route she took (left) had no named rails in the log, so the track runs
  into the straight line's alternates and says nothing of it. The Jodis stands' place gave a trailing switch set for
  her; the Obscure ladder too little to tell.
- What her next ride's log should settle: whether the verdicts were right at each switch she passed (the `passed`
  lines), how far ahead the track reached at speed (the `track from` lines: are rails named in time?), whether rails
  named Guide but phantom are ridden as this log suggests, and on a throw, which part took the touch, whether a touch
  toggles, how long the description took to change and whether the rails' names followed.

**Menu letters** (`Mnemonics.cs`): once a menu is open, no two of its items share a letter: an item keeps its own if it's free, else takes the next free letter in its words, else has none. Windows moves to the first of two items on one letter and runs neither, so a press did nothing, unsaid (Play and Paste as link, then Play and Copy to, 2026-11-08); menus are built for what's selected, so any pairing can clash.

**A mistyped command**: the build box puts a word it doesn't know back in the box to correct, with what it most likely was (`Typos.cs`: a known word with the space left out, or a slip or two away: "Did you mean hollow?"); `//` commands say the same, and Control+Up brings the line back (the blind tester's building, 2026-11-06).

**A newer build** (`MainForm.Updates.cs`): once after logging in, a packaged build asks GitHub for its front door's newest release, and says so if that's newer, with the page in the history and Control+Shift+K's list; silent when it can't tell. Test builds are folders, and the blind tester went on running older ones without knowing, once while saving a notecard on a build that lost what's embedded.

**How far Objects lists**: Alt+0's "How far Objects nearby lists" (`access_objects_range`, 10 to 125 m, 30 by default,
the nearest 200 past 30 m). Beyond the draw distance (128 m by default) the region sends nothing.

**Links** (`MainForm.Links.cs`; Claude Opus 5.5, 2026-11-04, the blind tester's ask): Control+K in a notecard or script opens the first link on the caret's line (Enter makes a new line there). Enter on a line of a history opens
the first link on it; Control+Shift+K (`//links`, the Read-out menu) lists the last 50 seen in chat, IMs and notices, newest
first, with who said them, Enter opening one. A web page (http or https only) goes to her browser, an SLurl teleports, a
person's `secondlife:///app/agent` link opens their profile; any other `secondlife:` link is refused in words. A script's
llLoadURL opens in the browser on Go to page (Access had nothing wired to it before). Second Life's other app links,
`app/group` and the rest, aren't opened yet.

**Driving after someone** (`Walk/WalkMode.Convoy.cs`; Claude Opus 5.5, 2026-11-04, after the blind tester steered a
motorcycle by hand after her partner's by the beacon, jerkily, and asked for vehicles following each other): `//follow
NAME` at the wheel of something that has taken the arrows holds those keys for her, about 10 m behind, or `//follow NAME
METRES` (3 to 50). It follows the way they went (points every 2.5 m, in the grid's metres so a crossing moves nothing),
not the straight line to them, and comes down to their pace in time (their pace from their position over half a second,
2.5 m/s² of slowing assumed, allowing for Down pressed only part of the time). Its own speed is taken two ways, the
region's word on the seat's velocity and the seat's moves over the last half second (where the region put it, at the
times its news came): the room to slow is reckoned by the moves, Down goes only while both say forward, and Up is held only
while whichever is faster leaves room (2026-11-11: on her ride the word, read every 2 s, went 6.8, 9.8, 12.3, 0.4, 7.7 m/s
while the seat covered a steady 4-6 m every 2 s, and read backward 85 times in 20 minutes, most likely her scooter kicked
by the drive's own Down presses, below). It steers by the way the seat faces plus an offset learned on straights while Up drives it,
not by the way it's travelling: travel lags the wheel, and steering by it spun the test's kart. The keys are held for a
share of each 300 ms by how far off it is. What it learns of the vehicle (that offset, what a Down press does) is kept
for the next `//follow` on the same seat (by its name), and the stop uses the offset (Stopping a vehicle, below).
- **Never backward** (2026-11-09, after her ride on 0.1.0.1045, `docs/access.md` > Driving after her partner, backwards):
  a vehicle's Down is often reverse thrust, so speed is judged along the way it's driven, and Down goes only in presses
  of a tenth of a second while it's going forward faster than 2 m/s, let go by a thread of their own as the stop's are (a
  busy window can't stretch one), the next no sooner than 0.25 s after over 6 m/s, 0.35 s over 3, else 0.45 (the
  region's news shows a press about 0.15 s on). **What a press takes off** (2026-11-11, after the review of 8700ca8: 84 of
  the 85 moments her scooter's word read backward that day came within a couple of seconds of the drive's own Down
  presses, where about 70 would by chance, so a press most likely kicked it into reverse): each press's effect is measured,
  the speed just before against the lowest in the 0.45 s after, by the region's word where the seat's moves over a
  quarter second show at least a quarter of that drop, else by the moves (so a word read wrong teaches nothing; the
  moves lag the word, so slowing already they'd start high); a word backward while the moves go on forward doesn't
  count (the test's reversed word, landing in one, had a press of the kart take 11 m/s off), nor a measure a crossing
  came in. What it loses by itself meanwhile with Up let go counts
  too, which is on the safe side. A harder one is taken at once, a gentler one a third of the way; 3 m/s (the stop's
  30 m/s² for a tenth of a second) until one's been seen. A press goes only while that leaves 0.8 m/s (the stop's
  margin), and the next only once the last one's effect is in; held back, it coasts with Up let go (on a vehicle whose
  Down is that strong, Up let go is all the slowing it gets until it's faster). Shorter presses to leave the margin, as
  the stop's pulses are, were tried and dropped: the test's kart loses about 5 m/s² by itself with no key, which a 59 ms
  press's measure counted as 100 m/s² of braking, and none went where one was needed. Going backward faster than 0.8 m/s anyway by the region's
  word, unless the seat's moves over the last quarter second say it's going on forward faster than 0.5 m/s (over half a
  second, as 8700ca8 had it, a real reverse showed 0.3-0.6 s late while it went on steering; on her scooter an Up press
  can set it going full ahead, one at 10:00:17 most likely did, just before the crossing it ran on through, Over a border
  below, so a word alone isn't answered with Up), it presses Up the same way and doesn't steer (what steers forward
  circles in reverse), logged; while the word says backward and the moves forward ("not taken for backing", logged once)
  it doesn't steer either. For 1.5 s after a backing's stopped
  no Down (its Up presses could tip it forward into Down again, rocking); if that hasn't stopped it in 4 s,
  "The scooter is going backward and Up didn't stop it, so I've let go of the keys.". Before, Down was held while the
  speed alone (either way) read as closing on them: her scooter's Down took it to 10 m/s backward, which read as closing
  faster, so it held on and steered, and she circled her stopped partner backwards for half a minute. The test's kart
  backed at 4 m/s the same way. The offset it steers by is learned only while Up drives it (learned while backing, it
  took the back for the front), at most 100 degrees. A turn of over 90 degrees is taken at 3 m/s at most (Down pressed
  over 5), as a driver brakes for a U-turn; over 57, 6 m/s as before. The log's lines say how many presses of each went
  since the last line. An independent review (Claude Opus 5.5, 2026-11-09) found the first version held Down over a
  crossing and braked more weakly than the room to stop assumed; both changed (below and above). What it can't settle: a
  vehicle whose Down and Up step a throttle that stays where it's left (her scooter crawled at 1 m/s with Up held after
  some Down presses, and twice sat 7-8 s with Up held before moving), which the local grid's kart doesn't have. It aims at the first point of their way at least a second
ahead (5 m at least), skipping points it has come within that of or gone past to one side; close behind them, where their
way runs out within that, at a point that far on past them the way they're heading (2026-11-08: it used to aim at their
last point, which at her 3 m gap at 11 m/s was 0.5 to 3 m ahead, so every half-metre to one side read as a sharp turn and
swung the keys; she felt her steering weave, and a point passed to one side was aimed back at). Where they are is as the
beacon finds them, but an older copy in a region whose list leaves them out doesn't count, and a copy is carried on by the
velocity it last reported for up to 1.5 s since its place last changed, as a viewer draws it (a region sends a moving
avatar's place only now and then). Any arrow, Space or `//stop`, `//follow stop`, `//stand` or standing up ends it; so does
the vehicle not moving for 6 s with Up held (by where the seat goes over 2 s: against a table on 2026-11-11 the word read
0.5-0.8 m/s now and then, each starting the 6 s again, and Up was held into it 34 s), them seen over 100 m from where they
could have got to for 2 s on end (a tick without them, or her own crossing, starts the 2 s again; a teleport, most
likely; until then they count as unseen, Losing sight of them below:
her partner riding into Astarte on 2026-11-11 was put that far for a moment and taken for a jump, while 33 s later he was
33 m on, waiting), not finding them again (below; a stop follows those last two), or the vehicle's script letting go of
her keys (below).
Not for what runs on rails or shows a throttle, where Up held opens it all the way and the keys don't steer: she gets a
beacon, saying why; the same if the seat hasn't taken her keys. Checked on the local grid in a kart, on the flat and
over a border; real vehicles' turning and braking, roads, and how close a gap holds are for her and her partner.
- **Over a border** (2026-11-08, after her scooter trip on 0.1.0.996, `docs/access.md` > A scooter trip and a train that
  lost its guide): until the region she's entering has sent her avatar and its seat, where she is can come in the frame of
  the region she left, so nothing is decided or said, Up staying as it was (as a driver holds the throttle over a border),
  the wheel straight and no Down (a Down press or a turn held over the crossing's second or two would back or swerve her); 0.1.0.996 said "getting away, 289 metres off" there, and 918 276 and 226. The keys reading as untaken don't end
  it for 10 s after a crossing (the region left and the one entered each send their word on the script's keys, in either
  order), elsewhere after 2 s; then "The vehicle let go of your keys at the crossing into Berbera, so no longer driving
  after Kestrel. Sitting on it again usually gives them back." (on her trip they stayed untaken until she stood and sat
  again). A seat in doubt over a crossing counts as driving for the rest of walk mode too, so a walk's "Blocked." isn't
  said there. The region changing counts as doubt from the very tick it changes (2026-11-09: the convoy looks before the
  crossing is noted, and the new region's first word can have her standing a moment, which ended a test's convoy as
  "You're off your seat" mid-crossing). For longer than 1.5 s (2026-11-11: into Rivula the seat took 6 s, and with
  nothing decided the scooter ran on 70 m, ending 22 m past her partner, who had stopped 5 m over the border; her log's
  whole seconds put the other 19 crossings' seats 0-1 s on, which is up to 2 s, so a shorter wait would act in ordinary
  crossings), where she is comes from the map list of the region she's in, which names her in whole metres (about once
  a second on Second Life, seconds apart on the local grid; whether Second Life's list has a seated avatar's real place
  mid-handover is unknown, so a place further than 5 m and 15 m a second from where she'd be by now isn't taken, which
  also covers her own teleport), else where she was last known, carried on the way she was driving at her pace: by the
  lists once two places 0.7 s or more apart show it (a list sooner after the one before keeps the pace the lists last
  gave), else as it was before the crossing. Within the gap and the room to slow, or past them, Up is let go. A Down
  only against where they're seen now (lost before her crossing, they're where the search has them by now, and Up is
  let go near that, but no Down), at a pace sure to be over 2 m/s (the lists' pace less 1.5 m over their time apart:
  whole metres over a second could make a true 1-1.6 m/s read over 3), going faster than them by over 1 m/s within the
  room to slow, or past them, where what a press is learned to take off leaves 0.8 m/s at that pace (Never backward,
  above), once for each
  new pace by the lists (the one from before the crossing counting once), so each one's effect is in the next before
  another; the wheel stays straight. So the review of 2026-11-09's "no Down over a crossing" holds for the first 1.5 s.
  A line for each in the log, and "the seat sent after" how long. Why they read as untaken, likely (found 2026-11-08 when a test's kart read as not driven): the keys a
  script takes are counted as the viewer counts them, each take one up and each release one down, so the order matters,
  and LibreMetaverse ran every handler of that packet on the thread pool in any order (its own handler is registered
  async, which makes all of them so); a release then a take, as a script takes the keys again or two regions hand a
  vehicle over, could be counted the other way round and stay untaken until she sat again. Core now has them counted in
  the order they came (`AgentSession.WireScriptControls`).
- **Losing sight of them** (2026-11-08, the orchestrating session's call after her trip lost her partner 5 s after he
  crossed, and stopped): it drives on as a person following by eye would. From where they were last seen, the way they
  were heading (their last 6 m or so, else their copy's velocity) at their pace (the speed their copy last reported if
  higher, since a copy left frozen where they crossed out keeps it; 10 m/s at most), to 25 m past the border if their way
  meets one within 40 m, else 15 m past where they were seen; she follows that as she follows them, the gap behind it. A
  ray down every 4 m along it stops it short of open water (below the region's water, when they were above it) or a drop
  of over 3 m. Said once it's lasted 1.5 s: "Lost sight of Kestrel at the border; going where they went." ("at the
  border" when their way meets one within 40 m or they'd come under 30 m over one; standing when lost: "going to where
  they were."). Seen again: "Found Kestrel again, 25 metres away.", the guessed way dropped, on to them (further from
  where they were seen than 100 m and their pace for the time since, for 2 s on end: jumped away, as above). Not found: "Couldn't find
  Kestrel again, so I've let go of the keys." and a stop, 20 s after they were last seen, or after 4 s at rest at the end
  of the way on, but not within 4 s of her own crossing, since the region she's entered names who's in it within a second
  or two. The bounds are ours, chosen so that over a border she ends up inside the region they went into, where she sees
  what that region sees, and so it never goes on for long on a guess.
- **Why it couldn't see him** (her trip, 18:55): not the draw distance (Access sends 128 m, as the official viewer at its
  middle and high settings; 64 to 256 by level) nor a missing neighbour: Diloba was connected, sending sounds of his
  motorbike as it crossed. Her log can't say which of three it was, and the convoy's log lines (above) now say what each
  region in view had of him when it loses sight: (1) Second Life sending neither his seated avatar nor him on Diloba's
  map list for a while after the crossing, as it hands a seated avatar over; nothing of ours fixes that, and losing sight
  covers it; (2) the copy Berbera never killed answering first, frozen at the border, then dropped as a ghost 4 s after
  Berbera's list left him out, which is about when it lost him (now that copy doesn't count, so the loss starts at once
  from where he really was, and a copy just sent by Diloba counts even before Diloba's list names him); (3) our dropping of ghosts taking the copy Diloba had just sent, left out of Diloba's list or
  still on Berbera's during the handover: dropped, a copy doesn't come back (LibreMetaverse remakes it without its id from
  the next terse update, and a seated one gets none). Core's `RegionWorld` now drops no copy in its first 12 s in a region;
  the time a copy was first named by another region's list is forgotten once that region's list stops naming them (it
  used to stay, so a later list could drop a copy at once); and a list as long as OpenSim's cap (60) doesn't count as
  naming everyone.
- **The log**: its start (who, how far behind, and the seat on a line of its own, since a sent log leaves out any line
  repeating words someone wrote to her, which on her trip took every line naming her scooter), every 2 s where it is,
  how far from them (by which region's copy or list) and their pace, where it aims and how far on, the keys it holds;
  each crossing, the keys going and coming back; losing sight of them, with where they were last seen and how, the way on,
  and for each region in view its copy of them (and whether the seat it's on is there), its map list (naming them or not,
  how old) and any copy of them lately dropped as a ghost and why; them seen again (by what, how far from where they were
  last seen) or the search given up; the speed by the seat's moves beside the word's on each line, the word read backward
  while the moves say forward (once each time), them seen too far to be them (by what, where, and what each region had),
  the map's lines while the seat isn't sent (and a list place not taken); what a Down press took off, the first one and
  each that takes more than was thought (by the word and the moves, and what a full press is now taken to do), Down held
  back on the 2 s lines (for how long, and what one would take off), and the way the vehicle goes forward once learned;
  and its end with why. Until 0.1.0.996 these went only to `SOJOURN_WALK_LOG`, so her
  trip's log couldn't show why it went astray. The controls scripts take and let go of are logged by Core for everyone
  (`controls: REGION took ...; held now: ...`, at most 30 lines a minute).
- **Tested** (`tools/access.py`, "driving after someone lost at a border"): no region on the local grid hides someone, so
  `//blindconvoy AFTER SECONDS` (only with `SOJOURN_TEST_CONVOY_BLIND=1`) has the convoy not see them for a while. Under
  the same switch, for what her scooter and Second Life did that the local grid doesn't: `//glitchconvoy SECONDS` (the
  region's word on the seat's velocity reversed for one tick that often, only while its moves go on forward over 1 m/s:
  one landing as it starts off, or just after a crossing, is rightly taken for backing), `//farconvoy AFTER SECONDS` (them seen 150 m east
  of where they are) and `//doubtconvoy SECONDS` (the next crossing's seat counted as not sent that long), in "driving
  after someone over a border" and "driving after someone, the seat not sent over a border".

**Stopping a vehicle** (`Walk/WalkMode.Stop.cs`; Claude Opus 5.5, 2026-11-01, after the blind tester's freight train, an
SLRR-style train driven by the arrows, ran off the end of its track and derailed or stranded her: once she had to
teleport away and walk to where she could rez it again). Her train says so in nearby chat as the track runs out
("sensor : No further track guide detected", then "no_sensor : No Guide found"), and she asked for it to just stop.
- **What starts it**: Space in walk mode, or `//stop` anywhere (Say, a conversation, a route's line), while seated on
  something moving (over 0.3 m/s: a train at its lowest throttle creeps at 0.5); not moving, they stop walks, follows
  and routes as before. And by itself: a line holding "no further track", "no guide found" or "track guide" (ignoring case, underscores and spacing; more phrases in
  `walk_track_end_phrases`, a list on SojournSettings, so the assistant can add one asked; not in Alt+0) from the seat
  (any part of its linkset, or anything with its name) or from any of the person's own objects (a train's HUD, a
  locomotive apart from the car she sits in), aloud, to her alone (llOwnerSay, llRegionSayTo) or as an object's IM,
  while she's seated and moving: an urgent sound (three quick high-low pairs, unlike any other cue, played whatever the
  other cues are set to) and "Track ends: stopping.". Once only per stop (the train's later lines don't start another),
  and nothing while standing still. Alt+0's "Track's end: stop the vehicle when it says the track ends" (`walk_track_end`,
  on) turns this off. Each such line heard is in the session log with whether it was acted on, and why not (not moving,
  not from the seat, already stopping): her train's "No Guide found" on 0.1.0.996 brought no "Track ends", most likely as
  the train had already stopped itself, and only the debug walk log could say.
- **How it stops**, as she would: the back control (CONTROL_BACK, Down's), through the same control flags the arrows
  send, held apart from them, so leaving walk mode, the window losing the focus or a notice arriving doesn't let go of
  it; an arrow pressed meanwhile hands the controls back ("Stop cancelled: the keys are yours."). The first press is
  timed: without news from the region that it's slowing, it lasts only as long as a car with strong reverse thrust would
  take to lose the speed (the speed over 30 m/s², 0.15-0.4 s); news of hard slowing ends it while the speed it will have
  when the let-go acts (200 ms on) is over 0.8 m/s; news of gentle slowing (under 10 m/s², a gentle brake) holds it on,
  150 ms at a time. 1.2 s from the start, the speed over that stretch is compared with the one before, and the region's
  velocity with the one at the start: both 15% slower or more (or the first press already showed that), it's a car's
  brake: later presses are pulses (0.1-1 s), each sent once the last one's effect is in (0.8 s) and as long as the
  hardest slowing seen says leaves 0.8 m/s, with 40% to spare, or none, and it coasts the rest; not (a throttle train,
  whose Down press takes its throttle a step down and no further), Down is tapped instead (Taps, below). Taps spent
  while she drove by holding Up (a second or more: a car, not a throttle) mean a gentle brake the news was too slow to
  show: pulses. And taken for a car but coasting on without slowing (no pulse safe) for 1.2 s while Up wasn't held,
  it's a throttle after all (each press took it a step down): taps. Speeds for judging are measured from where the seat
  goes (a moving seat's position can come only once a second, and a train's reported velocity swung 19, 6, 13, 8 m/s),
  so the judging is over the whole stretch; the press's own timing goes by the region's velocity, which comes sooner.
- **Taps by what it says of its throttle** (`Walk/WalkMode.Throttle.cs`, 2026-11-02): a vehicle showing its throttle in
  the floating text of the seat's linkset ("Throttle 40%", as her partner modded her train to) or saying it from the seat
  ("throttle 30"; once a line has come, lines win: the text is set on the script's timer, so it lags) is tapped by
  that from the start, with no first press, Down while it reads over 0 and never below. How far one press moves it is
  learned from a change that followed a single press (hers or the stop's; the stop's own win). Knowing that, as many
  taps go out at once, 0.3 s apart, as leave a step to spare should one more be on the way than it seems (a press
  counts as shown in a line 30 ms after it, in the text 300 ms after), and the last only once every earlier one shows
  and a while has passed (0.4 s by lines; by the text, 1.5 times how often it changes, 1-3 s). A tap it doesn't show
  within 1 s (lines) or twice the text's gap (at least 2 s) was lost, and the next goes; three in a row and what it says
  isn't followed: taps by their effect instead. Below 0 all the same, Up puts it back (three taps at most). Not for a
  car driven by holding Up, whose throttle falls back by itself.
- **Taps by the count and their effect**, when it says nothing: as many as she tapped Up on this seat less the Down
  taps (walk mode counts them, 0-20), half at once, a tap every 0.3 s (Up taps that never reached the script leave the
  count over, and all at once would take it into reverse); then one at a time, each once the speed along the way has
  held steady over 0.7 s (a steady speed forward is a throttle still open, for a vehicle whose speed follows its
  throttle; a falling one may be coasting with the throttle closed, where a tap would be reverse), and only while each
  slows it (by 3% or 0.08 m/s; one that doesn't, one more try, then "Couldn't stop it: Down doesn't slow it any more;
  it's moving at 1 metre a second."). Past the count (a tap lost on the way, or a throttle opened some other way, leaves
  it short) up to 20 more. Each takes about 1.7 s to show on the visitor's train, so once two in a row took about the
  same off (within a quarter), half the steps the speed left implies go at once (by the larger of the two). With no
  count (she didn't tap Up on this seat), every tap is by its effect.
- **Never backwards**: a car's back control is often reverse thrust. The visitor's kart, held, lost 9.7 m/s in half a
  second and went on backing, faster than the region's news of it comes (a velocity every 60-125 ms, showing a press
  only ~150 ms after it, and a let-go takes as long again to act): held until the news said nearly still, it backed in
  34 of 40 runs, by up to 1.7 m (2.8 m with the window's thread stalling), and the scripted test failed 30 of them
  (2026-11-02). Hence the timed presses above, and a thread of the stop's own that lets go when a press's time is up, so
  a busy window can't hold it on. Timed, the kart never backed in 90 runs (plain, with the window's thread stalling,
  with the news 150 ms late, under full CPU load); the cost is a longer stop for a car that strong: it's let go at about
  3 m/s and coasts, stopping in about 4 s, 2-5 m further on than a hold would. It still lets go at once if the seat
  starts moving back the way it came ("Let go: it began moving backward, at 1 metre a second."), and as soon as it's
  nearly still (its velocity and its last 0.7 s both under 0.5 m/s). A throttle is never tapped below 0 by what it
  says, and by the count only half at once and the rest at a steady speed forward.
- **Already backing** (2026-11-11, after the review of 8700ca8; Claude Opus 5.5): the way the stop takes it is the way
  the seat went over the last second, so a vehicle already reversing would be braked as a car going that way, each
  press adding reverse thrust. So: (1) seated where driving after someone learned which way the vehicle goes forward
  (the same seat, by its name; never on rails or a throttle, where a seat may face either way), going backward over
  0.3 m/s by that over the last second, nothing is pressed: "It's going backward, at 3 metres a second, and Down would
  speed it up, so I haven't pressed it." in place of "Stopping." (2) The region's word along the way it's going under
  0.8 m/s at the start: no press yet, pressed if the word comes round within 0.6 s; going backward then, "It's moving
  backward, at 3 metres a second, so I haven't pressed Down." ("Let go" only when something was pressed: on 2026-11-11
  at 09:55:09 it said "Let go, but it's moving backward" with nothing sent.) (3) Any press or Down tap after which,
  between 0.3 and 1 s on, it goes faster along its way than when it went, by 0.5 m/s and a tenth more than it was
  gaining before (by the seat's moves over the 0.6 s before: a train just opened gains on after a tap that leaves it
  under its new speed), by the word and the seat's moves over a quarter second; not while tapping by a throttle it
  says, nor without the moves: "Let go: Down speeds it up. It's moving at 3 metres a second." and the stop ends.
- **"Stopped."** only once it's nearly still: under 0.2 m/s over the last 2 s by where the seat went, and by the
  region's velocity (the visitor's train at 5% creeps on at 0.5 m/s, which the earlier 0.5 m/s mark called stopped).
- **10 s at most**, longer while taps are taking effect (6 s more after each drop of the throttle it says, 4 s after
  each tap by effect that slowed it, 10 s from when those began; 25 s in all): then it lets go and says "Stopped.",
  "Couldn't stop it: it's still moving at 4 metres a second." or "Couldn't stop it: its throttle is at 0, but it's still
  moving at 2 metres a second." Standing up or being thrown off ends it ("You're no longer seated.").
- **Why** (2026-11-02): a full test run's ride step left the visitor's train at 10%, creeping on at 0.9 m/s ("Couldn't
  stop it"): it was at 40% while walk mode had counted three Up taps, and the stop tapped only the count. A tap lost on a busy region does the same, and an Up tap lost leaves the count over, which taps into
  reverse. The step's trials (`docs/access-testing.md`) take trains losing every third press, said by text, by lines or
  not at all, and one opened to 20% before the arrows, each stopped by Space and at the track's end: none failed,
  went below 0 or moved back in 80 (10 of each), and the kart never backed in 10. Stops took, by Space and at the
  track's end: by lines 5.7 and 6.4 s, by the text 9.8 and 8.6 s (its text changes once a second), with nothing said
  11.2 and 12.1 s, and from 20% before the arrows 13.0 and 15.0 s (from 60%: about 40 m past where the end was said).
- What it assumes of her train, asked: whether Down past 0 reverses it, whether a held Down does more than one step,
  whether its floating text changes at once or on a timer, and what else it says when the track runs out.

**What the vehicle shows** (2026-11-01, after the blind tester's partner modded her train's floating text to show its
speed and throttle): seated, V adds the floating text of the seat's linkset (llSetText on any of its parts, the root's
first, each part's lines joined by commas, parts by semicolons), and Shift+V or `//text` says it alone. Only the seat's
linkset: a locomotive apart from the car she sits in, or a HUD showing the speed, isn't read (`//text NAME` reads a
thing's by name, its root's only).

**The collision model** (2026-10-30, so walk mode works on a PC that can't run the renderer, as the blind tester's
couldn't): every ray here (warnings, openings, drops, the surface underfoot, the sonar, A and T, paths, placing by
words) comes from a model that needs no GPU, so walk mode answers the same on any PC, `--no-render` included. It's
built from the session's world: every prim in the regions around (not only what a renderer has drawn), prims by the
viewer's volume code (the viewer's LOD 2), sculpts from their maps, meshes by their assets' physics (hull,
decomposition, physics model), each counting by the physics shape type the region gives it, phantom and flexible ones
passable; standing avatars as boxes; trees, grass and attachments not at all (the renderer's model's rule, shared code,
so the two answer alike: the test compares them). Prims count at once; sculpts and meshes once downloaded (the grid's
disk cache, shared with the renderer), nearest first within the draw distance (64-256 m). The ground is the region's
heightmap. How it's built: How it fits, below.

**Paths** (`Walk/PathPlanner.cs`) for `//go`, walk to, follow and the assistant's walks (`LLAgent startAutoPilot`): the
collision model's solid triangles over the walk's area plus 12 m, on a grid of 0.25 m cells, what stands between knee
and head height (0.45-1.8 m over the floor) as obstacles; nearer than 0.3 m to them is blocked (a 0.8 m doorway still
passes), and A* pays more within 0.75 m, so the path keeps to the middle of gaps; then pulled straight where a line
keeps that room. A face lying on a cell's edge counts only in the cell behind it (2026-11-11: walls on round
coordinates, with a walk to a round number lining the grid up with them, took the free cells beside them too, and a
stair a metre wide between two walls had no cell a body fits in). The floor is followed out from where the avatar
stands (2026-10-31): from each cell to the next, the highest surface (or the ground) no more than 0.4 m up or 1.2 m
down, so stairs, a stage's steps, ramps and a floor over sloping ground are walked, and what's above that floor in the
body's band is in the way (a stage with no steps, a table); what the floor can't reach is blocked. With nothing known
under the avatar (in the air, or on something the model hasn't got) the floor goes on level at its feet.

**Floors over one another** (2026-11-11) are each walked: the plan is over nodes, a floor over a cell, so a cell holds
the ground floor and an upstairs over it, a balcony and the ground under it, a bridge and the road below, a spiral
stair's turns. The floor followed into a cell joins the node there within 0.45 m of its height, or starts another;
walls, the room kept, people, stuck spots (on the floor she stuck on) and the cells freed round the start are each
node's own. A step goes from a node only to the floor it reaches next door (until then any two neighbouring cells
were joined, whatever their floors), and a step more than 0.4 m down, off a stage's edge, may land beside what it
stepped off, which from the floor below is a wall. Until then a cell had one floor, the first to reach it, so from a
house's ground floor its upstairs over where she stood was taken for the ground floor, and the stairs led nowhere.
The goal's floor is the one under its height: the highest surface facing up (or the ground) from 0.2 m over it to
2.5 m under it, a ceiling's underside never one. The height is a person's middle, the point given, or for a thing a
metre over its lowest point (its linked set's boxes; its own middle where that's lower, `Commands.FootOf`): a thing's
position is its middle, upstairs for a two-storey house in one mesh and high over anything for a tower. With nothing
under the height within that (someone hovering, a light hung 2.9 m up in a ground floor room), the goal's floor is the
highest reached there at or under the height, else the lowest; with no height, the floor nearest hers. A goal that
can't be got to is walked to a spot round it that can, "as near as the way goes", unless it's in a space
of its own the floor never meets (a closed room, a room upstairs behind a shut door, a fenced platform, or she's shut in
herself: no way), which a thing's own inside, closed all round like a room with no door, never is. Where the floors
meet is found by following the goal's floor out as far as the planned area goes (from a goal inside something, a block
or a seat someone sits on, first out through it, and through a wall it touches too, so a sofa against a closed room's
wall meets the floor outside) to where hers is beside it and she fits (a crack behind a stage too narrow for her isn't
hers). The spot is looked for up to 6 m past there, or past a thing's own reach (`Commands.FootOf`: how far its linked
set's boxes go from its middle), else within 6 m of the goal, and for a person or a point twice as far; one nobody
stands at goes before one where someone does if it's no more than 6 m further than the nearest. Which spot
(2026-11-11, evening): for a thing, the bit of its foot nearest her to walk to (where the line on to its middle leaves
her floor for good within half a metre), as any side of a thing is at it, so `//go` to a block 40 m long from off its
end goes to that end. For a person or a point, of the spots nearest them round about (a stage's side level with them,
its corner nearest them, either side of a bar), the one where being nearer is worth the walk, a metre nearer for every
4 m further to walk (a spot more than 1.5 m over or under their floor counting that height as distance too), then of
spots right by it within half a metre as near, the one nearest her, back from the edge where there's room. So she's
walked to her side of a bar or a block; to her side of a stage 14 m across for someone anywhere in its middle, not round
it (some 28 m) to be a few metres nearer, and round it for someone within about 3 m of its far edge; and following
someone moving about on it, she stays on her side until then, not going from side to side as they cross its middle
(the nearest spot alone, as until then, had sent her round for a few centimetres). A spot a storey (2.5 m) or more
under the goal's floor, where that floor meets hers only 4 m or more from the goal (a room upstairs that a teleporter
or a balcony at its far end reaches, a deck 3 m up with no steps and the person in its middle), would leave her under
them, not beside them: `//go` and the assistant's walks take it for no way, so the teleport where it lands, as for a
room upstairs behind a shut door (`Plan.Beneath`), while following walks on to it and says they're above. A stage a
metre or two high is walked to whatever its size, and someone 3 m up within 4 m of an edge (a balcony, a pillar) is
walked to under it. Until 2026-11-11 where the floors meet was looked for only 4 m round the goal and the spot only
6 m from it, so someone deep in a stage was no way and a teleport. A plan with a spot to find costs 1.5 to 3 ms (the
synthetic scenes through both by reflection: someone 7 m into a stage, 15 m into one, a block 40 m long by name), up
to a millisecond more than the nearest spot alone; from 60 to 100 m off some 12 ms against 5, her walk to each spot
being reckoned out from where she stands. A line pulled straight follows the floor as she will, so the waypoints, X and Y alone (both
autopilots steer by them, and the floor carries her up the stairs), lead up the stairs in order; it keeps the room the
path had between its two ends, so a squeeze or a stage's edge stepped off beside its side brings only that stretch as
close. A walk to another floor says so in place of what's in the way ("Walking 15
metres to Kestrel, up 3 metres: 5 stretches."); a point 2 m or more over or under her is planned even within 2 m
across. Following plans to where they'll be, at their height, and goes by the stairs, the way's corners in turn without
stopping at each (looked at ten times a second, the autopilot never counting a corner as reached); she's "With" them
only on their floor, and where their floor is out of reach (hovering, up on a stage with no steps, a balcony a
teleporter goes to) she goes as near as the way does, and once there hears it, once until they're in reach again:
"Kestrel is 3 metres above you; no way up from here." (with how far and which way when they aren't overhead; "No way to
Kestrel from here: ..." when they're at her level). Walking off Second Life, the region's autopilot gets each
stretch's point no higher than 2 m over the ground under it (`Commands.AutoPilotTo`): OpenSim's flies the avatar to
any point higher than one and a half avatar heights over the ground there (its `ScenePresence.MoveToTarget`, read in
the grid's decompiled code), so a walk to someone upstairs would have been a flight; on the local grid the walks
upstairs are walked, no flying animation on her. Second Life's gets the height as given, as it always has and as the
official viewer's Go here sends it (a skybox's floor is thousands of metres over the ground): what it makes of a
point upstairs isn't known, and a flying animation on her during a walk is logged once, in the session's log ("go: a
flying animation (fly) on the walk to ...", with where she was and what the autopilot was last sent), so her logs say
whether it ever flies her. The way to a floor is only found within the walk's area plus 12 m: stairs further out than
that leave the goal out of reach. Planning costs a fifth to two thirds more than one floor a cell did (one core, the same
synthetic scenes through both by reflection, the PC otherwise quiet: a walk in the club 1.3-1.9 ms against 1.0-1.4, a
follow's 6 m 1.2 against 1.0, 30 m of open ground 3.8 against 2.3, 150 m through a town of blocks 53 against 39, 40 m
through 57,600 triangles of clutter 13 against 11).

A mesh or sculpt still downloading isn't known yet, so a walk that gets stuck
marks the spot for two minutes and plans again (twice), then counts as no way. People, where the caller gives them
(`//go` and Walk to: People on the way, above; follow and the assistant's walks give none), are discs of 0.45 m on the
floor they stand on, kept off like walls but never walls: stepping in among the cells within 0.3 m of one costs the
search 38 cells, once, instead of their being closed (so a way round is taken up to 10 to 12 m longer, no further;
landing off a stage's edge among people standing at its foot too), and what can be reached at all is reckoned without
them. So a way that has to cross someone
comes back cut 1.5 m short of them with who (`Plan.InWay`), not as no way; the cells around the start are free only
where they take her no nearer anyone than she is; and when people are all that keeps the way from being straight, the
plan names them (`Plan.Around`).

**Recording a route** (`Walk/RouteRecorder.cs`, `MainForm.Routes.cs`; the other half of M3's routes, `//route` playing
them): the blind tester and her partner write routes as `//go xyz` lines in notecards and on her blog, written once by
someone who can see; this lets her make one by walking it. R in walk mode (or `//record`, or World > Recording a route)
starts it where she stands ("Recording a route from here. Walk it however you go: R says how it's going, N adds a
note, Backspace takes back the last point, P pauses, Shift+R saves."). She then goes however she likes: the arrows,
`//go`, walk to, following someone, a seat that carries her, teleports; recording goes on in or out of walk mode. Shift+R
(`//record save NAME`) keeps where she stands as the last point, asks the name (offering "REGION route") and saves a
notecard in a Routes folder at the top of her inventory (made the first time; a name taken there gets a number), said
with how to play it ("Saved Lobby to bar in Routes: 7 steps. //route Lobby to bar plays it."), playable at once and
movable like any notecard. Escape in the name box keeps recording. `//record discard` throws it away.

What's kept, from where the avatar goes rather than the keys pressed, so any way of moving records the same:
- **Turns and level changes**: the track (a sample each 0.3 m) kept as straight stretches; a point where the stretch
  from the last point would stray over 0.8 m from where she walked, heights counting half again, so a corner, a bend's
  middle and the top of a stair are kept and a straight walk of any length isn't. No two points within a metre.
- **Doorways and narrow openings walked through**: each metre, rays along the stretch just walked, 1.1 m out on each
  side and down its middle: something on both sides with the middle clear keeps a point in the gap. (Openings passed
  beside her aren't points: they don't change the way.)
- **A region crossed on foot**: the last point before the border and the first after, so the stretch across is short
  (a played route walks across straight, by the region's autopilot, asked again in the new region if it stops).
- **Teleports** (any: `//tp`, a landmark, Control+G, an offer, home, a walk's own fallback): where she stood before, then
  `//go tp xyz X,Y,Z` within the region or `//tp` and an SLurl (`http://maps.secondlife.com/secondlife/Region%20Name/x/y/z`,
  the form SLurls are shared in, which `//tp` takes and Radegast's Control+G does) for another.
- **Sitting and standing**: `//sit NAME` by the seat's name (a seat with none is said and left out), `//stand`; a seat
  that carried her over 5 m (a ride) writes a note and `//pause` lines as long as the ride took before `//stand`, so a
  scripted ride replays (a vehicle she drove herself isn't driven on replay: the route only waits as long).
- **Touches**: something touched while recording (T in walk mode, Touch on an object, `//touch WORDS`) as `//touch` with
  its name or her words, then `//pause 2` for a door to swing. Not yet: a part touched from Alt+7's parts list.
- **Notes**: N (`//record note WORDS`) keeps where she stands as a point, then the words as a line of their own, which
  `//route` reads out on arriving there ("Note: the door to the ballroom"); empty, just the point.

The notecard starts with a line saying where the route starts ("Starts in Atelier East, at 236, 120, 22.", read as a
note when played), then the steps in order. `//go xyz X,Y` is written with a tenth of a metre where there is one, and
in the frame of the region the step before it ended in, as a played route will be in that region when it starts the
line (so past a border it runs over 256 or below 0); Z only where the level changed by more than a metre (otherwise
`//go xyz` keeps her height, as her routes do). Everything but `//sit`, `//touch` and `//pause` (ours) and the SLurl
teleport (Control+G) runs in Radegast line by line.

While recording: each kept point ticks (the `route` cue, sound only by default; with text, "Step 5: a turn."), counted
as `//route` will count them (its `//` lines); R says the count, the distance walked and where the last point is;
Backspace says what it took back ("Took back the point at a doorway. 4 steps; the last point is 2 metres, six o'clock.");
P pauses (where she stands kept) and goes on: from over 30 m away or another region by a teleport (the route can't know
the way), else a walk from the last point. The recording lives in the app only until saved: logging out loses it.

**Recording a ride** (`Walk/RideRecorder.cs`, `MainForm.Rides.cs`, `Walk/RideApi.cs`; Claude Opus 5.5, 2026-11-01, after
the blind tester wished to ride a carnival waterslide, "arrow keys make you go faster, sometimes so fast you fly off",
and hear it described afterwards). C in walk mode (or `//ride`, or World > Recording a ride) starts it: "Recording the
ride: a picture every few seconds and what's around, kept on this PC until you ask. C stops it, or it stops when you
stand up after the ride, or the ride stops; Shift+C then tells it." (With the view still starting or preparing: "pictures start once
it's up"; "without pictures" only with no view, one given up, or no assistant.) Any time: standing in the queue, or already seated and moving. Standing up before anything has moved doesn't end it (she was on a pose ball at the top of a waterslide, stood to go to the tube, and the slide went unrecorded, 2026-11-04); five minutes standing with no ride does.
- **What it keeps**, every 3 s a moment: the time from the start, the region, speed over the ground, which way it's
  going, climbing or falling, height and distance from the start, what she rides (when it changes; held over a region
  crossing while the new region hasn't said whether she's still seated, `WalkMode.SeatInDoubt`: a moment taken
  mid-crossing told her she was standing on top of the engine, 835), the named things
  and people within 20 m by name and where from her going that way (never what she rides), objects' lines (the seat's
  own, her own things', one said to her alone, or any object's within 20 m; never people's chat), and the seat's
  floating text when it changes. Every few seconds a picture from her eyes, looking the way she's going (tipped as it
  climbs or falls), 640x360, 90 degrees across, JPEG (15-30 KB on the local grid), with what's visible in it by name
  (`DescribeApi.MovingPictureAsync`: a `Quick` shot, taken once the place's short settling ends, waiting for no one's
  look, since by then it's somewhere else).
- **Not stalling her PC**: one picture at a time; the next at least 2.5 times as long after as the last took (3 s at
  the quickest, 15 s at most), and one due while another is still drawing is skipped, never queued. The view draws on
  its own thread below normal priority (the processor's driver threads too), so walk mode's keys and the screen reader
  come first. At most 40 pictures kept (a ride's 2 MB or so): past that every other one goes and they come half as
  often, so a long ride keeps its whole length. Pictures only while the view is drawing and the assistant is on to
  tell them; otherwise it records the moments alone and says so ("without pictures (the assistant isn't on to tell
  them)").
- **Ending**: C again or `//ride stop` ("Ride recorded: 1 minute 24 seconds, 28 pictures. Shift+C tells it."); by itself
  3 s after she stands up (a crossing's doubt about the seat isn't standing), once a ride she sat on has moved (3 s of
  it at 1 m/s or more, not a jolt: a train flipped round at its platform jumps a few metres at once, which on 835
  ended her recording before it set off) and then been still for 8 s, or at fifteen minutes (five until 835 cut both
  her undersea rides short), each said ("The ride stopped moving: ride recorded, ...").
- **Told**: Shift+C in walk mode (or `//ride describe`, World > Recording a ride > Tell the ride) stops a recording
  still going and sends the ride, through the describe key's path, to one lean call (`describer.py` RIDE_HOW) on her
  describing setting's model (best: Opus 5.5 at low effort): the moments in order as lines, each picture after its
  moment's line, and instructions to tell it as a story in the second person ("You set off down a steep blue tube
  ..."), the named things passed by name in the order passed, speeds and drops as felt, each stretch its share (a long
  one that looks alike, a tunnel or open water, still gets what it looked like and what changed), a moment without a
  picture from its facts alone. Brief by the ride's length: about 100 words for a minute or less, about 50 more a
  minute past that (some 300 for five minutes), up to about 500 at ten minutes or more (until 835 it was about 100
  whatever the length, and her five-minute rides got 134 and 146 words, the tunnel a clause); at most 16 pictures
  chosen evenly through the ride, 24 past five minutes. Any telling gets at most 100 moments (a five-minute ride's,
  about 30K tokens of them): past that the first and last, those with a picture sent, those where the region or the
  seat changed, and others evenly between, each one left out folded into the next sent (its named things near, up to
  8, objects' lines, up to 6, what the seat came to show), so a fifteen-minute ride's request stays near a
  five-minute one's. Said as it
  starts: "Describing your ride. Shift+C again tells more of it." (`//ride more` by words); the same key again within
  ten minutes, or `//ride more`, a fuller telling with every picture kept, adding to the first. Nothing leaves the PC before she asks; the ride stays until the next
  starts or she logs out, and one recorded in incognito goes when the spell ends. Without the assistant: "What the
  recording knows: 38 seconds on Ride Train, from Atelier East; 75 metres travelled, at most 3 metres a second; passing
  Access Visitor, Red Buoy, Old Lighthouse, Splash Pool; 6 lines said by objects." The assistant's describe tool takes `of: ride` too,
  so "what was that ride like?" in Alt+9 works.
- **Cost** (economy, Haiku 5.5 high, through the relay, the local grid's test ride): a 40-second ride with 14 pictures
  was 8.7K tokens in and 1.6K out (1.4K of it thinking): $0.0017, 8.8 s of the model, 17 s from Shift+C to the last
  sentence read. A picture is ~300 tokens at 640x360, so a brief telling of any ride (16 at most) stays near $0.002 and
  a rich one of a five-minute ride (40) about $0.004; on best (Opus 5.5 low) about $0.05 and $0.10, reckoned from the
  same tokens. Her five-minute rides on best (2026-11-02): 37.7K and 39.3K tokens in (16 pictures and 101 moments),
  about 600 out, 8-9 s of the model; the moments' lines, not the pictures, are most of the input. A longer ride, its
  moments thinned to 100 and 24 pictures, should be some 3K tokens more, with up to 500 words out (reckoned).
- SojournRide on the agent API: `status` (recording, the ride's length, moments, pictures taken, skipped and thinned,
  the last picture's time, the names passed in order, what the seat showed and objects said), `get` (what the describer
  gets; the sidecar asks only when she has it told).
- Checked on the local grid (`tools/access.py` step "recording a ride", `docs/access-testing.md`) on the GPU, on the
  processor (`ACCESS_NO_GPU=1`: the first pictures 1.1-1.4 s while its shaders compiled, then 40-150 ms on this PC's
  twelve threads, none skipped, H answered in 0.05 s throughout) and with neither view nor assistant.
- On Second Life (the blind tester, 0.1.0.835, 2026-11-02; `docs/access.md` > A ride under the sea): two five-minute
  rides on a train through a glass tunnel under the sea, on her 2400G's processor: 51 and 40 pictures drawn (one every
  6-7 s, 0.4-3.6 s each, the processor never past 80%), the telling's length too short for her. Not seen yet: a
  waterslide or coaster, the longer telling.

### Objects nearby

`MainForm.Lists.cs`, `MainForm.Objects.cs` (Claude Opus 5.5, 2026-11-07; what's Radegast's own source for learning what's
around you, and unnamed "Object" is the commonest thing a resident meets).

- **The list.** A line says the name, where, its sale or payment, and ", can sit" or ", touchable", the last when it or any
  part takes touches. Parts and faces are a Tab away (below).
- **Past the nearest.** A page is the nearest 60 (200 when `access_objects_range` is over 30 m). With more in range the last
  row says "More: 84 further on. Enter lists the next 60."; Enter there adds a page and reads the first thing it added. The
  longer list stays until she's in another region, and F5 says the count ("120 objects, 24 more further on.").
- **The filter** (`objectsFilter`; Control+F from the lists or Show, as in the inventory, Alt+L, or Tab after the faces). What's
  typed is looked for, in any case, in the name, description and floating text of everything in range, not only the page, and the
  list follows 0.4 s after a pause, said ("3 found."); Enter goes to the list, Escape clears it and goes to the list. The list's
  name says it's on ("Objects nearby, with lever") until it's cleared, so arriving by Alt+7 later says why it's short. In a
  busy place the names aren't all in: the filter asks for them nearest first, about 60 in its 3 s, and says what's left
  ("3 found; 240 in range have no name from Second Life yet, F5 looks again"), going on by itself while the focus is out of the
  list. On what she wears it goes by name.
- **Details** (Alt+Enter on an object or a part, Details on their menus, `//details NAME`): said whole, for the review keys.
  Name; "Yours", "Owned by Kestrel" or the group; "Made by Ann, 3 June 2024"; its description; what its floating text says; worn
  where; how many parts; land impact; a sale; what she may do ("Yours to copy, modify and transfer; the next owner may copy.", or
  "You may copy it.", "You may not change or copy it."); and scripted, takes touches, takes payment, temporary, physical, phantom.
  A part's are the part's own name and description, the object's owner. Radegast keeps these in a pane; here they're asked for.
- **What people nearby wear** (Show's third choice): "Pet, worn by Kestrel on Left Shoulder, 4 metres, two o'clock, touchable".
  Touch, Pay where it takes payment, Describe, Details, Where and its parts, as a thing of her own worn has; nothing that moves it.
  Never HUDs, which only their wearer has. What's listed is in view of the region only; Alt+7 goes back to things nearby.
- **Touch** lands where `//touch` does. On the object when it takes touches; on the one part that does when only a part does (a
  treasure chest's lid, the reason parts exist: "Touched part 2 of Treasure Chest."); with several, it says which and leaves it
  to her, a Tab away ("Sofa itself takes no touches. 2 of its parts do: Left Cushion and Right Cushion. Tab reaches its parts.");
  with none, it sends the touch as a click would and says "Nothing in it says it takes touches."
- **Contents** (the object's menu, for its root; a part's menu, for that part): the build editor's Content tab with the focus in
  its list, Escape coming back here; rows and keys in Building, below. Someone else's that shows nothing says so: "Nothing it
  shows you: what's inside a thing shows to those who may change it, or when it sells its contents."
- **Take, Delete, Return**, by the viewer's own rules: Take where it's hers, or she may change it and pass it on
  (`enable_take`); Delete where it's hers or she may change it (`getFirstDeleteableObject`); Return on someone else's where she
  may change it, or it stands on land that's hers or her group's by the region's own map (`Simulator.ParcelOverlay`), or she
  manages the estate (`LLObjectEnableReturn`). The region decides in the end, and what it refuses is said. "Hers" goes by the
  owner when it's known, not the region's flag alone: OpenSim sets the flag on a friend's she may edit too.
  - All that isn't hers goes through her confirmation, naming the owner ("Return Box to its owner. Owner: Kestrel. It
    leaves the world and goes to its owner's Lost and Found; only the owner can put it back."). A Take of her own, undone by
    rezzing it, doesn't ask, and a Delete always does.
  - Return sends away someone else's thing, so it's placed for that: it has no letter (only the arrows reach it, and no key
    does), it's the end of the group Take, Delete, Return, with Block before that group and only Refresh after it, so a slip
    from Refresh or from the menu's top by Up lands on something that still asks, and never on Block, which doesn't.
  - Checked on the local grid: a friend's box with edit rights (Take and Return offered, each cancelled at its confirmation; Delete
    confirmed, which OpenSim refuses, the refusal said) and the visitor's box on her own parcel (Return offered, cancelled, confirmed
    and gone). Only Second Life can show a friend's thing actually taken, deleted or returned; there it's what the viewer offers.
- **Looked for nearby, worn instead** (2026-11-11, the blind tester's HUD missing from the list until she found Show): a filter finding nothing among things nearby, where something she wears has those words in its name, says so after the usual words, "One thing you wear has it: Alt+Shift+7 shows what you wear." (`RefreshObjects`).

### Parts and faces

(M2, a blind tester's child prims: the sunken city's Treasure Chest 6 answers only on a child prim, which Radegast lists by name
alone.) `MainForm.Parts.cs`, `Linkset.cs`. In Objects (Alt+7) the list has two more under it, each following the
selection above it only when that moves (Tab passing through a list never changes what the next is about, a lesson
from guided testing of Radegast, and no list changes while you're in it):
- **Parts of X** (`parts`): every part, root first then in the order the region made them (their local ids, which
  follow the link order the linkset was made in; not promised to be the scripts' link numbers). Named ones by name,
  unnamed ones ("Object", "Primitive", or the root's own name on a child) by number, what and where: "Treasure Chest,
  part 1 of 4, the root, a box", "Part 2 of 4, a box above the root, touchable", "Latch, part 3 of 4, a tiny box in
  front of the root, nearer you, above it". Sizes: tiny under 12 cm, small under 40, large over 2.5 m, huge over 8.
  Where: up and down, left and right as you face the object, nearer or further from you; on a HUD, as on the screen.
  ", touchable" when the part's own script takes touches, ", can sit" for a sit click; a short description when it's
  unnamed ("described as Play"). Names come from selecting every part briefly, as the edit tool's linked parts are (on
  the local grid neither the root's selection nor a child's family properties brought a child's name). Enter touches
  that part (the part itself, never the root: the renderer's ray counts that part alone first); the Applications key:
  Touch, Sit, Point at (Control+P; Pointing at something yourself, below), Where, Describe, Details (Alt+Enter), Contents, Faces. F5 reads them again.
- **Faces of X** (`faces`): the part's faces as the renderer out of sight drew them (`RegionScene.FaceList`: each
  face's mean normal, flatness, whether it faces inward), by direction from where you are, the side facing you first:
  "The side facing you, face 2", "The top, face 0", "The left side" and "The right side" as you face it, "The far
  side", "The bottom", "The curved side" (a face that wraps round), "The inside, ..." (a hollow's). Always with the
  number, which scripts and builders use. Enter touches that face's middle (llDetectedTouchFace and ST as a click
  there). On a HUD part, its faces as they show on the screen (HUDs out of sight, below): "The front, face 4" when one
  face is all that shows (a flat HUD's usual), else each with where on the part and how much ("Face 2, at the top left,
  about a third of it"). Without the renderer (`--no-render`, or a PC that can't run it), or on something worn on the
  body (not drawn out of sight), the list says so and parts are touched whole.
- **Show** (`objectsShow`, after the lists and the filter; Alt+Shift+7 goes straight there): things nearby, what people nearby wear (Objects nearby, above), or what you wear, HUDs first
  ("7Seas HUD, on HUD Top Right, 14 parts, touchable"), then everything attached to the body (a pet or a skirt with a
  menu, "Pet, on Left Shoulder, 3 parts"; the blind tester couldn't reach her worn things, 2026-11-01). Their
  buttons are parts like any other: their parts listed (a body attachment's without where-words: its offsets are in
  its attachment point's frame), Enter presses one where it shows (a HUD's), or on the part without a surface (a body
  attachment's). Delete or Take off there takes it off.
- **A HUD's buttons read off its picture** (Claude Opus 5.5, 2026-11-01; `MainForm.Huds.cs`). Most HUDs she meets (a
  train's driver panel, games, 7Seas) are one textured prim with buttons painted on it, its script telling them apart
  by where it's touched (llDetectedTouchST), so they have no parts to name. A HUD's parts list ends with "Read its
  buttons from its picture, with the assistant": Enter asks the assistant ("Reading the buttons on Driver HUD."), which
  looks at the HUD as it shows and saves each control a click would act on, by its label (else what it shows) and
  place; it says what it found ("A train's driver panel. 9 buttons on Driver HUD: Brake, Horn, ... They're listed after
  its parts. It shows: Speed 0."), and the parts list then has them after the parts, in reading order: "Brake, button
  1 of 9, at the top left" (where among the buttons). Enter presses one in its middle, as a click there would (its
  script gets that face and ST); the line becomes "Read its buttons again". They're kept for that look of the HUD, not
  asked again: every time it's worn, and across sessions (`hud-buttons.json`); a HUD showing another page (its texture
  or the texture's offset changed) is another look, read when asked. Why words over pixels: she can't check a guessed
  position, so the model names what's there once, the app keeps the numbers, and each press after costs nothing and is
  the same press every time; a bad read is undone by reading again. Ctrl+D on a HUD (or one of its buttons) describes it
  from its picture. Without the assistant, Enter on the line says reading needs it; the parts and faces work anyway.
- `//touch NAME` touches the nearest thing or part within 10 m with that name; for an object whose root takes no
  touches and exactly one part does, that part (the chest's lid); `//touch OBJECT PART` ("//touch chest latch");
  `//touch NAME face top` (bottom, front, far, left, right, inside, or a number). When nothing nearby has the name, a
  part or read button of a HUD she wears ("//touch brake"). For routes and notecards.

### What a friend points at

`MainForm.Pointing.cs`, Core's `Agents/Pointing.cs` (Claude Opus 5.5, 2026-11-08; Radegast's Master Controls, which its guide
teaches as letting "a sighted avatar click items for you"). Among dozens of things called "Object", what a sighted friend
nearby picks out in their own viewer is how they help without logging in as her.
- **What their viewer sends.** The official viewer points its avatar's arm at whatever its person selects in the world, which a
  right click does (for its menu) as well as editing: a PointAt and a LookAt viewer effect, both of type select, at the very prim
  clicked, a part or the root, cleared when the selection ends (`LLSelectMgr::updatePointAt`; never at avatars, attachments or
  what they sit on; none at all with its EnableSelectionHints off, on by default). Radegast's Point at sends the PointAt;
  building sends beams at what's built on. Core keeps each person's last pointing after it clears, since their menu has closed
  long before she catches up: the PointAt and LookAt of one selection are one pointing, a beam while they have something
  selected isn't a new one, and an avatar or anything worn is never taken as pointed at (a friend texturing their own hair
  isn't pointing anything out). A block whose sender and source differ is dropped (one avatar claiming another's pointing);
  the local grid passes on a block that names someone else in both places, so that stops only a careless claim, and
  whether Second Life sets the sender itself isn't known.
- **Whose.** On a person's menu, Hear what they point at ("Hearing what Kestrel points at: when they right-click something in
  their viewer, you'll hear it, and Control+Shift+P goes to it."), and Stop hearing what they point at once it's on; or
  `//pointing NAME` (someone nearby), `//pointing off`, `//pointing` (whose, and the last thing pointed at). Several people at
  once; for the session, not kept. Turned on within a minute of their pointing ("I'm pointing at it now"), what they pointed
  at is said then too ("Kestrel pointed at ...").
- **Said, waiting.** "Kestrel points at Treasure Chest, 3 metres, two o'clock. Control+Shift+P goes to it.", in turn as what
  arrives does (F6 mutes it; the line goes into nearby chat's history either way); a part as "part 2 of Treasure Chest" or
  "Latch, part 3 of Treasure Chest", in Parts' numbering. A quick run of them (a few right clicks in a row) says only the last.
  Nothing moves the focus.
- **Control+Shift+P** (also View > What someone pointed at): Objects on things nearby, on it, the focus there; when it was a
  part, then on that part in Parts (the object's line, read first, names it), so Enter touches the very part. A filter that hides it is
  cleared on the way, and it's listed even past the nearest 60 or the range. Its line says which it is ("Treasure Chest, 3
  metres, two o'clock, pointed at by Kestrel (part 2)"), and the part's line in Parts ends ", pointed at by Kestrel", until one
  of hers points at something else.
- `SojournPeople pointedAt {to}` gives the assistant the last thing someone pointed at (the VR client's too).
- Checked on the local grid with the visitor selecting as the official viewer does (its `point` and `beam`); only Second Life
  shows the official viewer's and Firestorm's own selections arriving, and how far away they still reach her.

### Pointing at something yourself

`MainForm.PointAt.cs`, Core's `Agents/Pointing.cs` (Claude Opus 5.5, 2026-11-09; Radegast's Point At; the user's call,
2026-11-09: in Access, on her own press, and not in the VR client). The other half of What a friend points at: she picks out a
thing, and her avatar's arm points at it in everyone's viewer, with a beam from her hand, so a sighted friend nearby can say
"yes, that's the tip jar" without logging in as her.
- **Where.** Point at on Objects' menu (no letter: every letter of its words is taken there) and on Parts' (P), and Control+P in either
  list, on the thing or the very part; on the one she's pointing at, the item is Stop pointing and Control+P stops. `//point NAME`:
  the nearest thing with that name within 30 m, else a named part within 10 (as `//details`), and on the one she's pointing at,
  another minute; `//point off`; `//point` says what she's pointing at. Only things out in the world: not what's worn, hers or
  anyone's (the official viewer never points at attachments, and What a friend points at ignores them), and not people
  (Radegast's people list has Point At; the official viewer never points at avatars).
- **Said.** "Pointing at Tip Jar for a minute: people nearby see your arm and a beam on it.", a part as "Latch, part 3 of
  Treasure Chest" or "part 2 of Treasure Chest". Standing, with it over about 53 degrees off her facing (the official viewer's
  own threshold for turning its avatar toward what it selects, `lltoolselect.cpp`), she's turned to face it first, said so:
  "Turned to face Tip Jar, pointing at it for a minute: ...". The pointing arm, the left, reaches only ahead and to the left
  (`LLEditingMotion` folds a target behind it round to the front), so unturned a friend would see it point elsewhere; seated,
  the seat has the say. Its end, by itself, is said in turn as what arrives is (F6 mutes it, the line kept in nearby chat's
  history): "Stopped pointing at Tip Jar after a minute.", "...: you moved away." (3 m from where she stood, a teleport too),
  "...: it isn't here any more."; Stop pointing says "Stopped pointing at Tip Jar." A minute, not a setting: Radegast's guide
  calls a point left on rude, and pointing again is one key.
- **What's sent.** What the official viewer sends while its person has a thing selected: a PointAt and a LookAt of type select
  at the very prim (`LLSelectMgr::updatePointAt`), so it reads in every viewer, Radegast's Master Controls and What a friend
  points at included, as a sighted person's selection; the two in one packet, as the official viewer packs a frame's effects
  (`LLHUDManager::sendEffects`), and again every 5 s for anyone who arrived since or missed them. And the edit beam the official
  viewer sends about once a second while building (white, its default effect colour), re-sent each second under one id so
  viewers keep it as one beam: that's what makes it stand out. Ending, the PointAt and LookAt are cleared and the beam's life
  ended, in one packet, as when a selection ends. A region may hold back LookAts and beams sent alone from those looking from
  far off: OpenSim sends a packet of only those to an avatar only if its camera is within 10 m of the sender
  (`Scene.ShouldSendDiscardableEffect`), never holding back one with a PointAt in it; whether Second Life has a rule like it
  isn't known, so a friend whose camera is far away may see the arm without the beam. Radegast's Point At turns the avatar to
  the thing, sends the PointAt once and three beams of random colours and a sphere each second, and lasts until Unpoint.
- **Only her own press starts it**: a key on the keyboard or a click, or a key her screen reader passed on for one
  (`RealInput.Person` at the press, `RealInput.Acting` for `//point`); a program's keys, a UI Automation invoke, or a line a
  program typed and sent is refused with the usual words, and with answers without a key set to Windows Hello, Hello asks
  ("Sojourn Access: point at Tip Jar"). Stopping takes any input. No op of the agent API reaches it, so the assistant can't
  point her arm (the VR client's call against sending these, `docs/viewer-gaps.md`, was about the arm moving for what the
  assistant did).
- Checked on the local grid with the visitor receiving it (its `effects`, its camera by it: `camera on`); only Second Life shows
  how the arm and the beam look in the official viewer and Firestorm, whether a friend picks the thing out by them, and how far
  off they reach.

### One person's things

`MainForm.TheirThings.cs` (Claude Opus 5.5, 2026-11-08; Radegast's Attachments and Animations on a person).
- **What they wear** (a person's menu): Objects with Show's fourth choice, "What Kestrel wears" (added the first time, renamed
  for the next person), the list named so: only theirs (`nearby`'s `worn_by`), by name, "Tip Jar, on Right Hand, takes payment,
  touchable" (no distance, as they're all where the person is; Where says it), touched, paid, described and read as What people
  nearby wear's are, their parts a Tab on. Never their HUDs, which only their wearer has. Alt+7 goes back to things nearby.
- **Their animations** (a person's menu): said whole, "Kestrel is playing 3 animations: stand; one, from Dance Ball, which
  they're sitting on; one, from something not in view, most likely a HUD of theirs." Second Life's own are named as the
  viewers name them; another is named when one of hers has its asset (only items she may copy, modify and transfer show it),
  else counted under what started it: themselves (their viewer: a gesture, a viewer's own AO), a thing here, something worn
  (theirs or someone else's), or something not in view (others never see a HUD's prims). Radegast's tab also plays and saves
  someone else's animations; not here.

### Your profile and notes

`MainForm.MyProfile.cs`. `//profile` with no name (or `//profile me`, or a link to yourself), World > My profile: a window, not modal, the focus in About.
- **The words**: About, Wants to, Skills, Languages, First life, Web address, each a labelled box with its letter (Alt+A and so on). Control+S saves, as does Enter in a one-line box, as
  Sa&ve; it's said ("Saved."), and anything the grid didn't keep is said and the box shows what it kept. Escape closes, asking first if there are changes. Only what changed is sent. A box
  over the grid's 254 characters (Wants to, Skills, Languages, the address) says so rather than being cut. Wants to, Skills and Languages are the viewers' Interests; the check boxes other
  viewers keep with them aren't shown and aren't touched.
- **The picture**: a line naming it ("Portrait, a picture in your inventory.", "A picture that isn't in your inventory.", "No picture."), and it changes as soon as it's chosen, not on Control+S.
  Choose a picture...: her inventory's pictures, the newest first, narrowed as a name is typed, Control+D describing the selected one (the assistant); Enter takes it, "Your profile picture is now
  Portrait.". The last row takes it off, saying what it was: nothing else is next to it, and the first row, what a stray Enter takes, is always a picture. Upload a picture...: Windows' Open
  window, the upload's own window and any fee confirmed in Notices as usual (Uploading, below), and then it's her picture. Describe it: the pictures on her profile now. In the inventory, a
  texture's or snapshot's Applications key has Use as your profile picture, below the line that Enter's action comes from. On Second Life this goes through inventory, so an upload costs
  the usual fee; the official viewer's free upload for profiles isn't here. A picture she lacks full rights to may be refused, said. The first life picture isn't here.
- **Picks**: a list a Tab on. Enter reads one (its name, where, what's said about it); the Applications key has Teleport there, Change its words... (name and description; it stays where it is) and
  Delete (Delete too), confirmed in Notices, the focus coming back to this window. New pick where you stand... asks a name and what to say about it. Whether the grid kept one is said, since a limit's refusal
  comes as nothing.
- **Someone else's picks** are the same list in their profile's window: Enter reads, the Applications key has Teleport there, the window closing first.
- **Notes on someone** (only you see them): a box in their profile's window, a Tab or two on from its text, Alt+N; Notes on them on a person's Applications key and `//notes NAME` open it there. It's read-only until
  the grid has answered, so nothing typed is overwritten. It's saved as the window closes, whichever way it closes, and said, as Radegast's on leaving the box. Typing over them is her own edit, as a notecard's is;
  emptying a box that said something goes through her confirmation, as a delete.

### Groups

`MainForm.Groups.cs`, `MainForm.GroupPeople.cs` (members, who's in the chat, bans), `MainForm.GroupNotice.cs` (a new notice); on a
group in Alt+3's My groups, on its Applications key (Claude Opus 5.5, 2026-11-07; running one, 2026-11-08). What her roles there
don't let her do is said before anything is asked ("Your roles in Builders don't let you eject members."), from the powers the
session holds for the group; on the local grid (OpenSim) those are the powers of the role whose title she wears, not of all her
roles, so the test wears the Owners title first.

My groups starts with a No group tag row ("No group tag, as now" when none is worn), the official viewer's None: Enter on it
wears no tag. The blind tester looked for it (2026-11-08); until then No group tag was only on each group's menu.

- **Choose your title...**: the titles her roles give her there, the one worn said so ("Queen Bee, worn"); Enter wears one
  ("Your title in Builders is Queen Bee now."), saying when it won't show until she wears that group's tag, which is Wear its tag:
  the two halves of a group tag are separate here as in the viewer, and neither does the other unasked. With one title it says so
  and asks nothing. Core's `TesselGroups titles` is the viewer's own list (GroupTitlesRequest), and `title` checks against it.
- **About the group** also says who founded it and her title there.
- **Members...**: a window of its own, not modal ("Members of Builders"): Find, where a name typed narrows the list (its name
  saying how many: "Members, 3 of 40"; Enter there goes to the list), and the list, owners first, each with their title and whether
  they're online, hers ending "you". Enter or the Applications key on someone: what the group lets her do to them, Roles...,
  Eject from Builders, Ban from Builders, then their own actions (`PersonActions`, the window closing first as a profile's does;
  `Mnemonics` keeps the letters apart, so the group's keep theirs and the person's move). On herself only Roles... and Your
  profile. F5 reads the list again, Escape closes, F1 says the keys. Save the list... (Alt+S; Radegast's Save member list, 2026-11-09)
  writes the members as a CSV file by Windows' own Save window ("Builders members 2026-11-09.csv": name, title, whether they own
  it, when last on), said where it went. Every change below waits for her confirmation in Notices and
  the focus comes back to the window after (`ConfirmedFromAsync`).
  - **Roles...**: every role of the group, theirs ending "has it", Everyone "everyone has it"; Enter on one gives it or takes it
    away ("Give Kestrel the role "Greeters" in the group "Builders""; for Owners the confirmation adds that nobody but that owner
    can take them off it), then says what the grid shows ("Kestrel has the role Greeters now.", or that it doesn't show the change
    yet, since the grid ignores a change her roles don't allow without a word). Said without asking: giving a role takes the
    power to assign roles (or, limited, only roles she has); taking one away, the power to remove members from roles; nobody but
    an owner takes themselves off Owners.
  - **Eject from Builders**: said once the grid's reply says it went (`TesselGroups eject` waits for EjectGroupMemberReply); an
    owner can't be ejected, said without asking.
  - **Ban from Builders**: the viewer's Ban, banned from joining and ejected if a member (`TesselGroups ban`, over Second Life's
    GroupAPIv1 capability as the viewer's `LLGroupMgr::sendGroupBanRequest`; `bans` and `unban` too). OpenSim has no group bans,
    said at once ("Group bans aren't on this grid: ..."). Written from the viewer's protocol and not yet run on Second Life.
- **Who's in its chat...**, also on a group chat tab's Applications key as Who's in the chat... (Radegast's participant list):
  the same window over who's in the group's chat now, "a moderator" or "text stopped" said. Opening it joins the chat first, as
  the viewer's chat window does when it opens (`SojournPeople groupChat` with `join`); with the group's chat turned off it says
  so. Who's in it is the session's own list and anyone whose text has arrived, until the grid says they left, as the viewer's
  speaker list takes them in (`LLSpeakerMgr::speakerChatted`): OpenSim's session list names only her and whoever joins after
  her, Second Life's everyone there. A moderator also has Stop their text in the chat, or Let them text in the chat again (`moderateGroupChat`).
- **Notices...**: the archive, newest first, by subject, sender and date, and what's attached; Enter reads one whole in a
  window, Escape back to the list, Escape again out. One with something attached ends "Attached: Party, landmark." and has
  Take the attachment (Alt+T), which takes it into the folder for its kind, said ("Taking Party into your Landmarks folder.");
  `TesselGroups noticeTake` asks for the notice again and accepts the offer that comes with it, as the viewer's Open attachment
  does. Core's `noticeRead` asks for one and takes the reply IM (GroupNoticeRequested), which isn't otherwise a notice.
- **A notice arriving with something attached** (Core, so the VR client too): it waits in Notices instead of passing as a
  line, its words ending "(attached: Party, landmark)", its keys 1 Keep and 2 OK; Keep takes it into the folder for its kind.
  Left alone it stays in the group's archive. One with nothing attached is as before.
- **New notice...**: a window ("New notice to Builders"): Subject, Message (Enter a new line), Attachment (a read-only line),
  Attach what you copied (Alt+A, or Control+V on the Attachment line), Remove the attachment, Send (Alt+S, or Control+Enter
  anywhere in it), Cancel (Escape, asking first if anything's in it). The attachment is what Control+C last took in the
  inventory, as Radegast's notice pastes it: one thing, not a folder, a link's original, with copy and transfer rights (every
  member gets a copy), each said when it isn't so. A subject over Second Life's 63 bytes or a message over 511 is said, not cut.
  Without an attachment it goes at once; with one it waits for her confirmation, as a give does. "Sent the notice to Builders."
  once the group's archive shows it (`TesselGroups notice` looks, up to 4 s); otherwise it says the archive doesn't show it yet,
  since a grid drops a notice it can't take without a word. Core packs the notice's IM as the viewer does (`send_group_notice`:
  the attachment as LLSD under a `<? LLSD/XML ?>` line): LibreMetaverse's `SendGroupNotice` packs it otherwise, and OpenSim
  dropped every notice with an attachment ("failed to decode group notice bucket").
- **Invite someone...**: a person picked (nearby, friends, or anyone by typing their name, as Give to... picks), then, if her
  roles let her give roles, which one: Everyone first ("Everyone, the role every member has"), then those hers let her give.
  Everyone goes at once; another role can carry powers, so it waits for her confirmation. **Invite to a group...** on a person's
  actions asks the same role after the group, from the groups she may invite to, as the viewer's picker lists them.
- **Banned residents...**: Second Life only (OpenSim's said at once): who's banned and since when; Enter on one lifts the ban,
  through her confirmation; the first row, Ban someone..., picks anyone and bans them.
- **New group...**: a window with the name (4 to 35 letters), the charter, Anyone can join, Show in search (on), Create (Alt+R)
  and Cancel. On Second Life a group costs L$100, so Create waits for her confirmation, which says so with her balance
  (`TesselGroups create`, a spend for the gate already); the new group is said and in My groups.
- **Receive its notices** and **List it in your profile**: checked items, each said back (`TesselGroups membership`).
- **Settings and roles...** (`MainForm.GroupSettings.cs`, `GroupRows.cs`; Claude Opus 5.5, 2026-11-09): the official viewer's
  General tab's group settings and its Roles tab in a window of their own ("Settings and roles of Builders"), laid out as About
  Land is: the settings list (the focus starts there), then Tab, a list of tabs: General, then each role, Everyone and Owners
  first. Enter changes a row (a switch turns, a choice opens its list, anything else asks for it typed), Right and Left step it,
  F5 reads it all again, Control+Page Down and Page Up go to the next tab and the one before from anywhere in the window, F1 says
  the keys, Escape closes. Every change is sent at her press, as the viewer's boxes are, and said once the grid shows it ("Show in
  search: off."; a role's change is read back from the grid for up to 4 s, else "Asked; Builders' roles don't show the change
  yet."). What her roles there don't allow is said before anything's asked, by the viewer's rules for each row.
  - **General**: Charter (a box of several lines: Enter a new line, Control+Enter saves; 511 characters), Anyone can join, Cost to
    join (L$, typed or stepped by 10), Show in search, Maturity (general or moderate), then a line naming the roles. Who may join
    (Anyone can join, Cost to join) waits for her confirmation, as About Land's who may enter does; the rest doesn't. The charter,
    search and maturity take the viewer's identity power, who may join its options power (`TesselGroups update` checked the
    wrong one for search and maturity until now).
  - **A role**: Name (not Everyone's or Owners'; 20 bytes), Title (20 bytes), Description (not Everyone's; 295 characters),
    Members shown outside the group (the viewer's Reveal members; not Owners'), Members (who has it), Delete this role (Enter,
    through her confirmation; not Everyone or Owners), then its abilities: a line counting them ("Abilities: 3 of 48 on; Page
    Down goes set by set"), and under the viewer's sets (Membership, Roles, Group identity, Parcel management ... Experiences), a
    heading each ("Membership: 1 of 4 on") and a switch each ability, in plain words (Core's `GroupAbilities`, from the viewer's
    `role_actions.xml`: "Invite people to the group", "Eject members with no role but Everyone", "Give roles abilities and take them
    away"). Page Down and Page Up go heading to heading. Owners has one line instead: every ability, always. Post links in group
    chat is listed only where the region says it has it, as the viewer does.
  - **As the viewer has it**: Give members any role and Give roles abilities ask first, with the viewer's warning that members
    could raise themselves to nearly an owner's powers; Manage the ban list brings Eject members and Take members off roles with it
    (said), and those two can't be turned off while it's on (said; turning it off leaves them, said too).
  - **New role** (Alt+N, the button after the tabs): its name asked, made with no abilities and its title its name, its tab shown
    and the focus in its list ("Made the role Ushers ...").
  - Core: `TesselGroups roles` gives `power_bits`, every bit as a number, and `updateRole` keeps them when it isn't given powers
    (a rename that sent back only the named powers dropped the environment, Ignore the landing point and Post links, which
    LibreMetaverse has no names for); whether members are shown takes the role-properties power, not the abilities one, as the
    viewer has it. The confirmations name the role and list the abilities given and taken away in these words.
- Not here: the insignia (a picture), the viewer's Copy role.

### Scripts

`EditorForm.cs` (the keys, in notecards too), `EditorForm.Replace.cs`, `MainForm.Scripts.cs` (Claude Opus 5.5, 2026-11-07; replace,
files, Mono, Running and Reset 2026-11-08).

- **New script** on a folder's Applications key: asks a name, opens with the official viewer's starting text.
- **In the editor**, keys of its own window, so they don't meet the main window's (F1 there says them):
  - Control+L: "Line 12 of 40, column 5." (Radegast's key).
  - Control+F: asks what to find (any case), keeps it, and goes there saying "Line 12: the line's words"; F3 the next, Shift+F3 the
    one before, going round the ends said ("From the other end: ...").
  - Control+G: a line by number, saying it.
  - F8 and Shift+F8: the last save's errors in turn, "Error 1 of 2, line 3, column 5: Syntax error. The line: ...", the caret placed
    there. Unlike Radegast's, the caret doesn't move by itself when a save finds errors: she's told how many, and F8 goes.
    The errors box is still a Tab from the text.
  - Control+H: Replace, Windows' own window as Notepad has it: Find what (the selection, else the last words found), Replace with,
    Match case, Whole words only; Find next (Enter), Replace, Replace all, Close (Escape). Each press is said as find's keys say
    theirs: "Line 10: ...", "Replaced on line 10. Next: Line 12: ..." or "... No more."; Replace as Notepad's, a first press
    finding and the next replacing; Replace all "Replaced 2, on lines 5 and 10. Control+Z in the text undoes it." (one change, so
    one Control+Z). Nothing reaches Second Life until Control+S. F3 and Shift+F3 go on with the same words and choices after;
    Control+F's own find stays any case, anywhere. Not in a chat log's window.
  - Control+O: a file's text in place of the window's (Windows' Open window: `.lsl`, `.txt` or any), asked first over changes not
    saved; "Opened door.lsl, 40 lines. Control+S saves it into Second Life."; Control+Z gives back what was there. Over a megabyte
    is refused. Control+Shift+S: a copy of the text as it stands to a file (Windows' Save window, named after the item, `.lsl` for
    a script and `.txt` for a notecard or a chat log, UTF-8): "Saved a copy to door.lsl in Documents." A card's things are their
    markers in the file, and keep their places when it's opened into the same card again. Radegast's open from and save to disk.
    Tests set `SOJOURN_TEST_FILES`, a folder standing in for both windows (the file named as the Save window would name it).
- **Inside an object**: in the build editor's Content tab (or Objects' Contents), Enter on a script or notecard opens it from the object in
  the same window. Control+S there goes through her confirmation, as anything changing a script in an object does at the gate
  (`object.content.save`): this window comes forward with it selected in Notices, Enter or Escape, and the editor comes back. A
  notecard keeps and lists what it holds as one in the inventory does (Keys > What a notecard holds). A script needs copy and modify
  on it, as in the viewer, and says so. Space on a script in the Content list runs or stops it.
- **Mono, Running and Reset**, a script inside an object's (the official viewer's two boxes and button, and Radegast's): check boxes and
  a button in the row after Save, a Tab from the text, each saying what it does when reached (Mono's description: "Compiles the
  script for Mono, Second Life's usual engine: faster, with 64 KB of memory where the older LSL2 has 16. Most scripts want it.
  Changes when you save."). Their underlined letters work from the text with the focus left there: Alt+M ("Mono off: saving
  compiles it for the older LSL2 engine, with 16 KB of memory rather than Mono's 64.", "Mono on: ..."), Alt+R (Running, changed
  at once as the viewer's box does: "Stopped.", "Running.", or what the grid did instead, a script that doesn't compile not
  starting), Alt+E (Reset: "Reset Door: it starts over."). Mono starts as the grid says the script is (GetScriptRunning's reply,
  `object.content.get`'s `vm`), Mono when it doesn't say; a Lua script has no Mono box and stays Lua. A Mono change not saved is
  asked about on closing. A save sends Running as ticked: a save that doesn't compile stops a script, and the next that compiles
  runs it again. A script in the inventory has none of these, as in the official viewer: it runs only inside an object. Only the
  local grid has shown them, and it calls every script Mono; Second Life's own reply is still to be seen.
- **Her own objects' errors** (`access_script_errors`, on; Alt+0 "Script errors from my objects read out"): `SocialFeed` turns debug-channel
  lines from what she owns into notices, read out and in nearby chat's history: "Script error in Door: Math error". Other people's
  never (the grid sends them to everyone in range, and no viewer shows them). The same words from the same object at most once in half
  a minute, for a script failing in a loop. Muted by F6 as notices are; its sound is the notices' (off by default).

### Auto-sit

Radegast's AutoSit, with what Radegast issue #156 asks of it (a seat in each of several places, and a key to turn it on and
off); `MainForm.AutoSit.cs` (Claude Opus 5.5, 2026-11-09). Her seat for a region (a chair at home, a stool at the club) is
sat on again when she arrives there:
- **When**: logging in, or the session logging in again by itself after a drop (Back in Second Life); a teleport into the
  seat's region from another; a teleport home (Control+Shift+H, or one the grid marks as home). Not walking or driving in
  across a border, nor a teleport within the region, where she meant to go where it took her (one region from the next is
  told by where the teleport set out from, its first word from the grid, against where it ends).
- **Once each arrival**: it waits up to a minute and a half for the seat to appear (a big region's objects come slowly),
  asking every two seconds, and gives up if she moves 4 metres off or sits on something first. Each outcome is said, in turn
  like what arrives: "Auto-sit: sitting on Comfy Chair. Control+Shift+S turns auto-sit off." (the key once a session);
  "Auto-sit: you've moved off, so you're not sat on Comfy Chair."; "Auto-sit: Comfy Chair hasn't appeared here in a minute
  and a half, so you're not sat on it. //autosit now tries again."; the region's refusal in her words. Radegast sits her again
  every ten seconds while it's on, so there standing up means turning it off first; here she stands up when she likes.
- **Setting a seat**: Objects' Applications key on a thing, Auto-sit on it (on her seat, Stop auto-sitting on it); sitting on
  it, `//autosit here` or World > Auto-sit > Sit back on what you're sitting on; `//autosit NAME`, the nearest thing with that
  name. One seat a region: a new one takes the old one's place, said ("Auto-sit on Stool, in place of Comfy Chair: when you
  log in or teleport into Bay City, you're sat on it."). The seat is the prim she sits on (a part's own name unless it's
  only "Object", then its object's); one taken in and put out again is found by its name within 3 metres of where it was.
- **On and off**: off until she sets a first seat, which turns it on and says so; after that the switch stays as she left
  it: Control+Shift+S, World > Auto-sit > On (a check), Alt+0's My avatar (Auto-sit, a switch), `//autosit on` and `off`.
  Turning it on doesn't sit her (`//autosit now` does, as Radegast's Sit button); off ends a wait for a seat.
- **The seats**: Alt+0's My avatar has Auto-sit seats ("Comfy Chair in Bay City; Stool in Jazz Club"), Enter on it the same
  menu as World > Auto-sit, each seat to forget; `//autosit` says how it stands; `//autosit forget [NAME | here | all]`.
- **Kept per account**, in its state dir (`autosit.json`), since a seat is an object on one grid; on the agent API as
  `access_autosit` and `access_autosit_seats` ([{id, name, region, pos}]).
- **Run by itself as hers**: the sit goes through `PersonOps.SetUpAsync`, the op itself with no key of hers behind it just
  then (so incognito's limits on callers, which want things by handle, don't refuse it), allowed only for ops that never
  wait for a confirmation (`LLAgent requestSit`, the only one). RLV's sit restrictions still hold: they're in the op.
- **Radegast's other two automations aren't built** (judged 2026-11-09; a question for her in `docs/access.md`). PseudoHome
  teleports the account back to a saved spot every five seconds whenever it's further off than a tolerance (256 metres by
  default, so in practice whenever it's in another region): a way to keep an unattended account in place, which here would
  fight her own walks and teleports and is the recurring automation Second Life's rules for viewers watch for
  (`docs/tpv-policy.md` > Bots); its useful part, starting somewhere of her own, is the login's Start at a landmark or place.
  LSL Helper lets objects owned by listed keys drive the account by IM: invite people to her groups, send IMs, give her
  inventory away and say things, unasked; giving and inviting into a role wait for her own press here, for any caller, and an
  account scripts drive is a scripted agent.

### Placing by words

M2; `PlaceWords.cs`, `MainForm.Place.cs`. Rez where you say... (the inventory, S) and Move... (an object of yours in
Objects, M) ask where: "a metre to my left", "2 m ahead and half a metre up", "at two o'clock, 3 metres", "1.5 m
north", "here" (at your feet), "in front of me", "by 20 cm to the left" or anything with only up or down (from where it
is now: moving only). Distances in metres, centimetres or feet, digits or "a", "half a", "two"...; no distance is a
metre. It rests with its bottom (its whole linkset, as turned) on what's under the spot: the first solid surface down
from head height in the collision model (a table, a floor; said: ", on Table"), else the ground or the floor you stand
on. Answered in your terms ("Rezzed Chair, 1 metre to your left.").

### Uploading

M6's first part (Claude Opus 5.5, 2026-11-01; `MainForm.Upload.cs` over Core's `UploadOps`): pictures, sounds,
animations and models, uploaded on her own (the blind tester's partner logs in as her to do it). Control+U from anywhere
(on a folder in the inventory: into that folder), Build > Upload a file, a folder's Applications key > Upload a file
here, or `//upload` (`//upload PATH` skips the Open window; the path as typed, quotes optional).
- Windows' own Open window picks the file (screen-reader users know it), its types listed: pictures (PNG, JPEG, TGA,
  BMP), sounds (WAV, Ogg; MP3, FLAC, M4A and WMA decoded to a WAV first by Windows' own Media Foundation, so nothing is
  bundled for them, and they can't be read on Windows N without its Media Feature Pack), animations (BVH, anim),
  models (glTF, GLB, Collada, OBJ).
- The file is read on the PC, nothing sent (`TesselAgent uploadLook`), and a window says what it is in a read-only text
  where the focus starts, a line each, the gist first: "Sunset.png: a picture, PNG, 1000 by 750 pixels, 812 KB." /
  "Second Life needs each side a power of two, at most 1024 pixels, so it'll be resized to 1024 by 512." / "It has
  see-through parts (transparency)." / "Uploading costs L$10, confirmed in Notices by your own key. Your balance:
  L$250." A sound: its length, mono or stereo, format and rate, Second Life's 30 seconds, that it'll be made mono. An
  animation: its length and joints moving. A model: parts, faces, triangles, its size ("About 1.2 metres along X, 0.8
  along Y and 0.9 metres high, at a scale of 1."), rigged or not, its textures, the loader's notes.
- Tab: Name (the file's name; at most 63 characters, Second Life's), Description, Into the folder (the one chosen, the
  kind's own: Textures, Sounds, Animations, Objects, or the one selected in the inventory), then the kind's choices,
  each at the official uploader's default: an animation's Priority (0-4, 2) and Loop; a model's Scale (the new size
  said as it changes), Detail levels (made from the model, or full detail at every distance), Physics shape (the
  outline as a convex hull, or a box), Include its textures (off, as the viewer's), and Weigh it (Alt+W: the grid's fee
  and land impact, the viewer's Calculate weights and fee). Upload (Alt+U, or Enter in a one-line box; "Upload for
  L$10" where it costs), Cancel (Escape: "Nothing uploaded.").
- Refused in words before anything is sent or asked: a file of another kind ("Shopping list.txt isn't something Second
  Life takes. Uploads can be pictures (PNG, JPEG, TGA or BMP), ..."), a picture over 8192 pixels a side or a file
  over 512 MB, a model the grid would refuse (over eight faces a part, broken) or over 64 metres a side at its scale
  (with the scale that fits), too little L$ ("Not enough L$: the upload costs L$500, and your balance is L$0. Nothing
  was uploaded.").
- Where uploads cost (Second Life), the fee is confirmed in Notices by her own key, as paying and giving are. The gate
  works it out itself (`Session/Assistant/Confirmations.Uploads.cs`, for every caller, the assistant's uploads too): a
  picture's by its size as uploaded (the account's large-texture price over 1024 by 1024 where it has one), a sound's
  and an animation's from the account's benefits, a model's by asking the grid, its land impact in the confirmation.
  It pins the fee on the op as `max_cost`, which the op holds to, so what she confirms is what's charged: "Upload
  Sunset, a picture, for L$10. From Sunset.png, resized from 1000 by 750 pixels to 1024 by 512. Into Textures. Your
  balance: L$250." On a free grid (the local one) nothing asks.
- Then "Uploading Sunset." ("Still uploading" once after 20 s; for a model on Second Life "Asking Second Life the fee
  for ..." first) and the outcome: "Uploaded Sunset, a picture, 1024 by 512, into Textures. L$10 paid. It's selected in
  your inventory, Alt+4." The new item is selected in the first pane's tree, its folders opened, unless she's in that
  tree and has moved off where she was (it never changes under her) or it shows a search; a model's line adds how to
  rez it. A failure says the grid's words, the common ones plainly: not enough L$, a region not taking uploads, an
  account without Second Life's mesh upload steps done (payment information on file and the intellectual-property
  tutorial).
- As the official viewer: pictures resized by its biasedScaleToPowerOfTwo (the power of two below unless the side is
  over 1.75 times it; Sojourn's op had always rounded down, so 1000 became 512), at most 1024 (`max_size` 2048 is the
  op's, not offered: LibreMetaverse's upload states the 1024 price as the expected cost, unchecked on Second Life for a
  larger one); sounds by its converter (mono Ogg Vorbis at 44.1 kHz, up to 30 s; more WAV formats taken than the
  viewer's); animations through the BVH port with the floater's defaults; models through LibreMetaverse's two-step
  upload, levels of detail made by simplifying each face (a third, a ninth, a twenty-seventh of the triangles) and a
  convex hull for physics. Not offered: levels of detail or a physics mesh from files, the viewer's decomposition,
  skin weights and joint positions as choices, "This model represents". TGA is read by our own reader
  (`Core/Media/Tga.cs`: Skia has none). Permissions are the person's Default Permissions for uploads, as before.
- A sound over 30 seconds is offered as clips instead (`MainForm.UploadClips.cs` over Core's `uploadClips` and
  `Media/SoundClips.cs`, 2026-11-01): a song for a scripted instrument or player, which plays consecutive clips in
  order; cutting one by hand is hard without sight. Its window starts on the gist: "Harp song.wav is 2 minutes 14
  seconds long, and Second Life takes sounds up to 30 seconds: it can go up as 5 clips of up to 29.9 seconds, named
  Harp song 1 to Harp song 5, L$10 each, L$50 in all. Upload them as clips?", then the file, how it's cut, that each
  clip is made mono at 44,100 Hz with a 5 ms fade at each cut (so it doesn't click), and the fees. Tab: Name of the
  clips (the file's name), Clip length in seconds (29.9, so none is refused for a rounding; 1 to 30), Cuts (exactly at
  each clip length, all the same length, for a player that starts each clip on a timer, the default; or at the
  quietest moment in the 2 seconds before each mark, for a player that waits for each clip to end, so a cut falls
  between notes), Into the folder (a new folder named after the clips, in the folder chosen or Sounds, by default; or
  straight into one), Make a notecard listing the clips in order (off; a name a line, as many players read). A change
  to the length or cuts says the new gist. Names are numbered from 01 at ten clips or more, since a player script
  finds its clips in name order. Where uploads cost, one confirmation in Notices for the whole set: "Upload Harp song
  as 5 clips, for L$50. L$10 each. From ..., cut into clips of up to 29.9 seconds: Harp song 1 to Harp song 5. Into a
  new folder, Harp song, in Sounds." The gate works out the count from the file as the op will and pins the total as
  `max_cost`, which the op holds the set to. Then each clip goes up in order, said as it lands ("Clip 3 of 5
  uploaded."), and the outcome: "Uploaded Harp song as 5 clips, Harp song 1 to Harp song 5, 2 minutes 14 seconds in
  all, into a new folder, Harp song, in Sounds. L$50 paid. A notecard, Harp song clips, lists them in order. Harp
  song 1 is selected in your inventory, Alt+4." A clip that fails stops the rest: "Clip 3 of 5, Harp song 3, didn't
  go up: ... Clips 1 and 2 went up, into Harp song; L$20 paid. Nothing more was uploaded." (a new folder left empty by
  the first failing is removed). The assistant has the same op (`TesselAgent uploadClips`, WAV and Ogg only: the
  Windows decoding is Access's).
- Radegast uploads pictures (a console logging each step, the fee on its button) and meshes; Firestorm and the
  official viewer have Build > Upload, a floater per kind, the picture's shown, a sound's playable.

### Building

M6's second part (Claude Opus 5.5, 2026-11-02; `MainForm.Build.cs`, `BuildRows.cs`, `BuildShapes.cs`, over Core's
`BuildOps`, `BuildUndo` and `ContentsOps`): building with her own hands, prim by prim, from a 2009
design for a blind builder's editor (`docs/access.md` > M6): the official editor's tab names
and settings as the words, so blind and sighted builders share them; one prim at a time; faces by direction; a value
every face shares said once ("all sides"); metres and degrees, never quaternions; numbered choices; any setting
reachable by typing, with a list to browse. A first version for her and her partner to react to. On Second Life since
2026-11-06: she built an igloo and a tunnel and linked them, a sighted friend confirming; the rest of this section's
words are the local grid's until she's tried them.
- **The Build area**: Control+B (the official viewer's Build key), Build > Edit, Objects' or a part's Applications key
  > Edit, or `//build`; it has no Alt+number (all ten are taken). Control+B on an object in Objects edits its root; on a
  part, that part; on a face, that part with that face picked; elsewhere, what was edited last. Arriving says what's
  edited: "Editing Table top, part 2 of Table: a box 1.2 by 0.8 by 0.05 metres; 2 metres, one o'clock; resting on Leg."
  (the first time also how to start). Escape goes back where Control+B came from. Tab order: the command box, the
  settings list, Tab (the editor's five tabs, a list), the faces list. While the area shows, the prim is selected as the
  viewer's edit tool selects it (so the region sends its properties as they change) and let go of when she leaves.
- **Typed commands** (the command box; Enter runs, Control+Up and Down bring back earlier ones; `//build COMMAND` from
  Say or any input): a tab's name (`general`, `object`, `features`, `texture`, `content`) reads that tab's settings and
  shows it in the list; a setting's name and a value changes it (`name Table top`, `hollow 50`, `phantom on`, `material
  glass`, `colour red`, `transparency 50`, `shininess 2`, `bump bricks`, `fullbright on`, `light on`, `light radius 5`,
  `position z 25`, `rotation z 90`, `path cut 0 0.5`, `twist 0 90`, `next owner copy off`, `for sale 10 copy`); a
  setting's name alone says it. Besides: `face top` (or bottom, front, far, left, right, inside, north, `plusx` as that design
  wrote it, a number) and `all sides`; `new box` / `new cylinder 2 metres ahead` (any of box, cylinder, prism, sphere,
  torus, tube, ring, cone, pyramid; where in Placing by words' terms, 2 metres ahead unless said); `move a metre to my
  left`, `move by 20 cm up`, `move to 128 64 25`; `size 1 1 0.2` or `size 0.5`; `scale 2`; `turn 90` (left,
  counter-clockwise from above, Second Life's positive), `turn 45 right`, `turn 90 around x`; `where` (and `what`);
  `look` (the assistant describes it, as Control+D there does); `duplicate` (beside it to her right, clear of all of it, or
  `duplicate by 2 m left`), `delete`, `take` (always the whole object, said so; a part's delete says to unlink first);
  `link NAME` (another of hers joined to this one, this one's root staying the root), `link` alone her objects within 30 m
  to pick from, Objects' menu Link to what I'm editing on one of hers while a build is open, and a name several share taking
  the nearest and saying so, `unlink`; `edit NAME` (a part of this object by that very name first, then an object, then a
  part with it in its name), `part 2`, `part NAME`, `root`, `next`, `previous`, `parts`, `all parts`, `edit linked on` and
  `off` (A linked object, below); `lock`, `unlock`; `copy from NAME` (Copying settings, below); `export` (`export as dae`, `as xml`)
  and `import` (Export and import, below);
  `size`, `position`, `rotation` alone say them;
  in Content `add NAME` (from her inventory), `remove NAME`, `copy NAME`; `undo [N]`, `redo`; `help`; `done`. `help` (and
  Build > Build commands) opens the commands in a window to arrow through, a command a line, as F1's keys (said whole as
  one answer, the blind tester asked for it six times in her first quarter hour of building, 2026-11-03); `shapes` lists
  the shapes; `rename X` is `name X` (as she typed it).
- **A linked object** (2026-11-07): as the official editor's Edit linked, the same word, on or off. Off is where a linked
  object starts, as the viewer's default: after `link`, from Objects, by `edit NAME`, or by `all parts`
  (`whole` is taken but not offered, since it sounds as "hole"): "Editing all 2 parts of Igloo: 6 by 4 by 3 metres overall;
  ...". On is one part: `part 2`, `part tunnel`, `next`, `previous`, `root`, a part from Objects' parts list, `edit linked
  on`: "Editing tunnel, part 2 of Igloo, this part only: ..." ("the root of 2 parts, by itself (moving or turning it moves
  them all)" for the root, as the region has it). It's the one target, not a mode behind it: every arrival says which, and
  the Object tab has an Edit linked row. On all parts:
  - `move` and position, and `turn` and rotation: the root's, its parts going with it; turning is round the root, so the root
    stays where it is, as the official editor's spinners do (its mouse tool turns round the middle). "All 2 parts moved with it.",
    "All 2 parts turned round the root."
  - `scale 2`: every part's size and its distance from the root, sent as each part's own (one undo step), "Scaled all 2 parts
    of Igloo by 2: 12 by 8 by 6 metres overall", read back from the region; `scale` says how far it can go where a part
    would pass 1 cm or 64 m. Second Life's links reach about 54 m: noted, not refused. `size` alone is the overall size; `size
    1 1 1` says it's each part's own and points at `scale`. The official editor sends one message for this; ours is part by part
    because a scale with the linked flag is the region's own reckoning, which only OpenSim is here to check.
  - colours and the Texture tab, material, when left-clicked, next owner's permissions, share, anyone can: every part, one
    undo step and, for permissions, one confirmation ("Change permissions of Igloo, all 2 parts"). Read together, a value they
    share said once as "all sides" is: "Colour: red (255, 0, 0), all 2 parts"; else "red on 1 part; blue on 1 part" (three values,
    then "others").
  - name, description, sale, Content: the root's, as the object's. Physical, temporary and phantom: the region keeps them
    for the linked object as one, so they say "All 2 parts." from a part too. Locked goes to every part from a part too,
    and reads by the root's (the local grid's region keeps a part's own rights apart from it).
  - shape rows, a part's size and Features are each part's own (the official editor greys them): hidden from the list, and typed
    they say how to pick a part. Faces too: the faces list holds one line saying so.
  - where it is and what it rests on and touches is all of it's: what's under its lowest part, what's against any part, its own
    parts aside.
  On one part, a change ends "This part only.". `parts` lists them by number and name, in a window past 8. Worn things aren't
  edited at all yet.
- **Copying settings** (2026-11-07, the official editor's Copy and Paste buttons as a typed line; `TesselBuild copyFrom`):
  `copy from Wall` takes everything but the name, place and permissions: shape (with the flexible path), size, rotation,
  material, colour, texture, features. `copy colour from part 2`, `copy shape and size from Wall`: any of those. Colour is
  colour and transparency; texture is the Texture tab's other rows, on every side, or one side if she's picked a face
  (the same number on the source). The source is hers: a part of this object (`part 2`, `root`, its name), an object within 30 m,
  `part 3 of Wall`; or Objects' or a part's menu, Copy its settings to what I'm editing. Said back once the region shows it
  ("Copied colour from Wall to Dome. Colour: green (0, 255, 0), all sides."), one undo step. On all parts, only what goes to
  every part (material, colour, texture); shape, size, rotation or features asked for by name say they're each part's own. Held
  back and said: a mesh's or sculpt's shape; a texture from a prim she may not copy and transfer (the official viewer's rule for
  pasting one); PBR materials, which don't go by these settings. `copy NAME` with no `from` is Content's as before, and words that
  aren't settings ("copy cup from shelf") too.
- **The settings list**: the tab chosen, a row each, named "Object settings of Table" ("Texture settings of Table, the
  top, face 0"). Rows read "Hollow: 50 percent", "Phantom: off", "Bumpiness: 5 bricks", "Colour: red (255, 0, 0), all
  sides". Enter changes one: a switch turns, a choice opens its numbered list (the one it has checked), anything else
  opens a box with the value as it is. Right and Left (Space and Shift+Space too) step it: a number by its step
  (position 0.1 m, size 0.05 m, rotations 15 degrees, hollow 5%, transparency 10%, glow 0.05...), a choice to the next
  or previous, a switch turned. F5 reads them again. A row that's still the same row keeps its place as its words
  change, so a change never moves her. Content's rows are what's inside ("Door, script, running"): Enter on a script
  or notecard opens it (Scripts, above), Space runs or stops a script, Delete removes an item (confirmed).
- **What's on each tab**, the editor's own rows: General: name, description, creator, owner, group, share with group,
  anyone can move and copy, for sale (a price and original, copy or contents, or no), next owner can modify, copy and
  transfer, when left-clicked (0 touch ... 8 nothing). Object: edit linked and overall size on a linked object, where
  (below), locked, physical, temporary, phantom, position X
  (east), Y (north) and Z (height) in the region, size X, Y and Z, rotation X, Y and Z in degrees as the editor shows
  them, material (0 stone ... 6 rubber), building block type (0 box ... 6 ring; a mesh or sculpt says so), and the
  shape's rows as the editor shows them for that type: path cut, hollow (percent), twist (degrees), taper, top shear,
  slice (straight paths), dimple (a sphere), hole size, profile cut, radius, revolutions and skew (torus, tube, ring).
  On a straight path the editor's Path cut is the profile's begin and end and Slice the path's; the rows follow it, so
  "path cut 0 0.5" does what the sighted editor's spinners would. Features: flexible path with softness, gravity,
  drag, wind, tension and force; light with its colour, intensity, radius and falloff, and a projector (a texture, its field of
  view, focus and ambiance); physics shape type. Texture: the face
  (or all sides), texture (plywood, blank, transparent, or a texture in her inventory by name; one not in her
  inventory is said so), colour (a name or 0-255 numbers, said as the nearest of 29 names: "close to orange (240, 130,
  20)"), transparency, glow, full bright, shininess (0-3), bumpiness (0-17, the editor's order, as scripts number
  them), mapping, repeats (said without a sign), repeats per metre (reckoned along each face's own axes, as the official
  editor: every side's repeats go at once), flip horizontal and vertical (the official editor's Flip: a negative repeat, kept when
  the repeats change; a negative repeats typed flips too), texture rotation, offset.
- **Faces**: the faces list has "All sides (7 faces)" then each face by direction from where she is, with its number
  ("The top, face 0", "The side facing you, face 2", Parts and faces' words). They come from the prim's own volume,
  built on the processor as the viewer builds it (`VolumeBuilder`, the port of llvolume.cpp), so they're there with no
  picture, on her PC too; a sculpted prim is one face; a mesh's directions need the renderer to have drawn it, and until
  then its faces are listed by number. Picking one (Down, or Enter, which also opens Texture) makes Texture's rows that
  face's; with all sides, a value every face shares is said once ("Colour: red, all sides") and one that differs apart
  ("Full bright: off on 6 sides; on on the top, face 0"). A change on all sides goes to every face, each keeping its own
  other half of a pair it doesn't share (repeats, offset), one undo step and one texture entry.
- **Said back**: a change goes through `TesselBuild modify` and is said once the region shows it ("Hollow: 50
  percent."), or "Sent; Second Life hasn't shown the change yet." after 3 s. Moving, sizing, turning and changing the
  type also say where it is now. A refusal is the region's or the op's words in her terms ("You can't edit Table: you
  don't have modify rights on it"). Opening someone else's object reads it and says "It isn't yours to change."
- **Where it is, what it rests on, what it touches** (`where`, the Object tab's first row, and after each placement):
  "2 metres, twelve o'clock; resting on the ground; touching Wall". Under it: rays straight down from the middle of its
  underside and near its corners in the collision model (its own shape below counts: a cup on a table's top), the
  nearest within 3 cm "resting on", further "65 centimetres above"; below the terrain, "sunk 10 centimetres into the
  ground". Against it: other prims' boxes (not avatars or worn things) within a centimetre, by the separating-axis test,
  "touching" for plain boxes, else "close against ... (by the outer boxes)", since a sphere's or a hollow's box is
  bigger than it; an overlap says how deep ("5 centimetres into Wall"). No picture needed.
- **New prims** are half a metre each way, set down with their underside on what's under the spot (a floating
  rez in Radegast, the smallest first test), named Object until she names them, and become
  what's edited ("Made a box, half a metre each way, 2 metres ahead. Editing it."). Resizing keeps the middle where it
  was, as the viewer's editor does, so a resized box can end up floating or sunk; it's said.
- **Locked** (2026-11-07, the Object tab's first switch, `lock`, `unlock`, `locked on`): as the official editor's box, the
  owner's own move and modify rights off, on every part. A move or a change says "Igloo is locked, so that wasn't changed:
  locked off frees it"; `take` and `delete` ask for it to be unlocked first (the official viewer asks whether she's sure). The
  summary says so. Something she owns with no modify rights isn't called locked, which goes by the move right as the viewer's box does.
- **Her confirmation** (Notices, as everywhere): permissions (share, anyone can, next owner), a sale, temporary on (the
  region deletes it within a minute), delete, removing from Content, adding a new script, and an undo that deletes what
  was made. Undo and redo are `SojournBuild undo` and `redo`, one history with the assistant's edits and the headset's
  hand moves; each command or row change is one step, said by what it changed ("Undone: changed rotation on Table.").
- **Not yet**: the newer reflection probe and PBR materials (`TesselBuild modify` keys the assistant uses, so rows only, but a
  probe's state isn't read without the renderer), align media; moving the root alone with its parts left where they are (the
  official editor's Edit linked on does; here the root always takes them); stretching all of a linked object along one axis;
  several separate objects at once; a face as the source of a copy; editing something worn. On Second Life, only single prims and
  `link` have been tried (2026-11-06): scale on all parts, locked, copying, flip and repeats per metre are the local grid's, and
  every word is the blind tester's and her partner's to judge.

### Export and import

Radegast's Export (XML, and DAE) and Import object, and Firestorm's Backup and Import Linkset; `MainForm.Exports.cs` over
Core's `Backups` and Session's `Building/Collada.cs` (Claude Opus 5.5, 2026-11-09).
- **Export** saves one of her objects, always the whole of it, from Build > Export what you're editing to a file..., Objects'
  Applications key on hers (Export to a file...), the build editor's `export`, or `//export NAME` (the nearest of hers with that
  name; with none, what's edited). Windows' own Save window picks where (Documents\Sojourn\Backups first, then wherever she saved
  last) and the kind; `as dae`, `as xml` or `as oxp` in the words choose the kind it starts on:
  - a backup (.oxp): Firestorm's format 2, which Firestorm's Import Linkset and this app's import rebuild (`docs/building.md` >
    Backups);
  - Radegast's XML (.xml): LibreMetaverse's prim list as LLSD XML, as Radegast's PrimExporter writes it, its textures beside it
    as .jp2 files in a folder named as the file, which Radegast's Import object reads;
  - a Collada model (.dae): each prim's faces built as the viewer builds them (VolumeBuilder at its highest detail; a sculpt
    from its own map), placed and turned as in the world with the root at the middle, Z up, metres; faces of one colour and
    texture share a material, so a prim has a few at most and the model can go up again as a mesh by Control+U (Second Life
    takes eight a part), whose reader takes it back; her textures as PNGs beside it. Meshes are left out, said.
  Said as it starts and when done: "Exported Igloo, 2 parts, 1,412 triangles, as a Collada model: Igloo.dae in Backups.", with
  what was left out ("Left out: 1 texture made by others (plywood instead); the objects' contents (not kept in Sojourn's
  backups yet).").
- **Only what she made** (`docs/tpv-policy.md`, 2.b: an export the official viewer lacks takes only content whose creator is
  the person, every part of it): the rules are Core's Backups', Firestorm's (on Second Life a part only if she owns it and made
  it, never a mesh, a texture only if her inventory holds one she made; on OpenSim the grid's ExportSupported). Anything else is
  refused before the Save window, saying which part and why: "Igloo can't be exported: Door was made by someone else. Second
  Life's rules for viewers let you export only what you made yourself, every part and texture of it." Textures others made go
  as plywood, said. Radegast checks only the root's creator and takes any full-permission texture.
- **Import** rebuilds a backup (.oxp, Firestorm's or ours) or Radegast's XML: Build > Import an object from a file... (the Open
  window), the build editor's `import`, or `//import PATH`. 2 metres ahead, resting on what's there (as placing by words), the
  backups' restore doing the rest (each prim made, shaped, textured and linked; never temporary, never for sale; one undo
  step); textures this grid lacks are uploaded again, which on Second Life costs an upload each and is confirmed in Notices
  first with the total (the restore's own confirmation). Said as it starts ("Importing Igloo.oxp: 2 parts.") and when done
  ("Imported Igloo, 2 parts, 2 metres ahead, on Floor. Editing it (Control+B)."); it becomes what's edited. A Radegast
  export's textures come from its folder of .jp2 files where they're there.
- Only the local grid has run these (2026-11-09); Second Life's checks are `docs/building.md`'s (a backup refused for
  something bought, a texture made by someone else becoming plywood, a restore uploading on the Beta grid).
- In tests `SOJOURN_TEST_FILES` stands in for both windows (the export's file named as the object, in that folder).

### About Land

M6's land settings (Claude Opus 5.5, 2026-11-03; `MainForm.Land.cs`, `LandRows.cs`, over SojournLand's ops,
`docs/land.md`): land settings by her own keys, the task blind residents still hand to a sighted person logging in as
them. Laid out as the build editor is, with the official viewer's About Land tab names and settings as the words.
- **The area**: Control+L (ours: the official viewer has no key for About Land; Control+L is free in Access, NVDA and
  JAWS, and a RichEdit's left-align it would shadow isn't used here), World > About Land, or `//land`; no Alt+number.
  Arriving reads the parcel she stands on afresh and says it: "About Land: Your Parcel, owned by Estate Keeper, 576
  square metres, for sale: L$0 to you; you're on it; its nearest edge 12 metres, three o'clock." (the first time also
  how to start). Escape goes back where Control+L came from. Tab order: the command box, the settings list, Tab (the
  tabs, a list). World > The region's covenant opens it on the Covenant tab and reads that.
- **A change is said once the region shows it**, asked again each second for up to 10 (`ParcelOps`: a busy region is slow
  to answer). If it still hasn't on her own land, she hears "Not seen yet: the region hasn't shown ... changed", since it
  may well have; on someone else's, that only the owner may (2026-11-08: one look after a second told the local grid's own
  owner, twice in a night's runs, that only the owner could).
- **The parcel shown is pinned** by its id and region, so a change acts on what she was told about even after walking
  off it; F5 in the list, or `here`, reads the parcel she's on now (and the tab's lists again). Where she is comes
  from the parcel's 4 m cells as the region sent them (`LandRows.Nearest`): on it, and its nearest edge by the clock;
  or off it, and its nearest part.
- **The tabs** (About Land's, then World > Region/Estate's Region tab): General (name, description, owner, group,
  area with its size east to west and north to south, where, region and maturity, for sale, and the actions Buy this
  land, Abandon land, Deed to the group where they apply), Covenant (the estate's covenant, a line a row, after a line
  saying how many and when it changed), Objects (land impact used of the most, the owner's, set to the group, others',
  auto return of others' objects, then who owns what, a row each: "Access Visitor: 3 prims"; on Second Life with the date of their newest), Options (anyone can edit
  terrain, fly, have objects enter, run scripts, make landmarks; the group can build, its objects enter, its scripts
  run; damage allowed, pushing allowed, show in search, category, seen and heard from other parcels, teleport routing,
  landing point), Media (media URL), Sound (music URL, sounds kept to this parcel, voice, voice kept to this parcel),
  Access (open to, only the allowed list, the group may enter, passes sold with their price and hours, no one without
  payment info, no one under 18, Buy a pass where it sells them, then the allowed and banned lists, a row each:
  "Banned: Access Visitor, until Tue 3 Nov 23:27"), Region (its name and type, the estate and its owner, maturity,
  people at most, now: people, active scripts and time dilation, whether she manages it, water height, and the
  estate's switches: terraforming, flying and flying over blocked, damage, pushing, resale, joining and dividing,
  parcels hidden from search), Estate (The estate, below). The lists and owners are read only for her own land or her group's
  (the region tells no one else; the rows say so).
- **The estate** (`MainForm.Estate.cs`, `EstateRows.cs`; Claude Opus 5.5, 2026-11-09): the official viewer's World >
  Region/Estate for its owner and managers (its Estate and Access tabs, the Region tab's tools), and Radegast's Eject from
  estate and Restart. The Estate tab, for anyone, says whose estate it is and whether she manages it ("You manage it: no: only
  its owner and managers have the estate's lists and tools"), the region's word at its handshake (`EstateOps.CanManage`, so
  a manager made meanwhile shows after a teleport). For its owner and managers, the list then has:
  - its tools, Enter doing each, every one through her confirmation: Send someone home (a person picked, people in the region
    first), Send everyone home (all but her in the region), Eject someone from the estate (sent home), Restart the region (in 2
    minutes, the viewer's; everyone there is warned and counted down by the region; while hers counts down the row says how
    long is left and Enter cancels it, not asked), Message to the region and Message to the estate (typed, its exact words in
    the confirmation; at most 254 bytes, said before anything's asked, as one estate message's parameter carries no more).
  - the estate's switches, read only here (open to anyone or its lists, payment info, under 18, scripted agents, voice,
    direct teleport): changing them is `EstateChangeInfo`, which Core doesn't send yet (`docs/land.md` > Not done).
  - its four lists, each a heading saying how many and what Enter does ("Banned residents: 1 person. Enter bans someone"),
    then a row each ("Banned: Kestrel, since 2026-11-03 10:20, by Estate Keeper"); Enter on a heading picks someone (a group of
    hers for the allowed groups) and adds them, Delete on a row takes them off, both through her confirmation. Only the
    estate's owner changes its managers, said before anything's asked; nor can its owner or a manager be banned.
  - On a person's menu, where she owns or manages the estate, Eject from estate (someone in the region) and Ban from estate
    (also sent home if they're here: `estateAdd`'s `kick`, kicked once the ban shows, as a region needn't send a banned avatar
    away itself). Not there elsewhere, as Invite to a group isn't for someone in no group; typed, the estate's commands say
    only its owner and managers may.
  - Typed (About Land's box, or `//land ...` anywhere): `estate` reads the tab; `estate ban NAME`, `estate unban NAME`,
    `estate allow NAME`, `estate unallow NAME`, `estate allow group NAME`, `estate unallow group NAME`, `estate manager NAME`,
    `estate remove manager NAME` (taking off looks in that list's names first); `kick NAME` (or `eject from estate NAME`);
    `send home NAME`, `send everyone home`; `restart`, `restart in 60 seconds` (30 seconds to 4 minutes), `cancel restart`;
    `message WORDS` to the region, `estate message WORDS` to the estate; `estate help`.
  - On the local grid OpenSim's kick logs the avatar out rather than sending it home (its own behaviour), and the region
    keeps listing them until their connection times out, so the answer there is "Asked the estate to eject Kestrel; the
    region still shows them here for now." On Second Life the estate's kick teleports them home, by Second Life's own account,
    so they should leave the region's list at once; not seen here.
    Whether Second Life sends a banned avatar away by itself isn't known here, hence the kick. A real restart, the estate's
    switches' words on Second Life, and ban dates (Second Life's EstateAccess has them, OpenSim's not) are Second Life's to show.
- **The settings list**: named "Options of Our Garden"; rows read "Anyone can fly: on". Enter changes one (a switch
  turns, a choice opens its list, anything else a box with the value as it is; Enter on an action does it, on Landing
  point puts it where she stands); Right and Left (Space too) step it; F5 reads it all again. On someone listed,
  Delete takes them off the list; on an owner of objects, Delete returns their objects. A row that's still the same
  row keeps its place as its words change.
- **Typed commands** (Enter runs, Control+Up and Down bring back earlier ones; `//land COMMAND` from any input): a
  tab's name reads it; a setting and a value changes it (`name Our Garden`, `description ...`, `category
  residential`, `fly off`, `scripts on`, `damage off`, `show in search on`, `landing here`, `routing anywhere`, `auto
  return 10`, `music url ADDRESS`, `voice off`, `only the allowed list on`, `passes on`, `pass price 10`); a setting
  alone says it. Besides: `allow NAME`, `ban NAME for 2 hours`, `unallow NAME`, `unban NAME` (a name on the list
  first, else anyone in the region, else anyone on the grid by the avatar picker; several alike are listed back);
  `eject NAME`, `eject and ban NAME`, `freeze NAME`, `unfreeze NAME`; `return NAME`, `return others`; `for sale 500`,
  `for sale 0 to NAME`, `... with objects`, `not for sale`; `buy`, `buy pass`, `abandon`, `deed [GROUP]`; `divide
  north half` and `join north` (compass words, or ahead, behind, left and right from her, to the nearest of the four:
  parcels are laid out on the compass); `flatten`, `raise`, `lower`, `smooth`, `roughen`, `revert` (the viewer's land
  brush where she stands, 2 metres across; `parcel` for the whole parcel, `for 3 seconds` held longer; the heights
  before and after said); `my land` (holdings), `parcels` (`//parcelinfo`), `where`, `here`, `help`, `done`.
- **Said back**: every change goes through SojournLand's ops, which read the parcel back from the region (and refuse
  in words when it didn't change); the row's new words are then said ("Anyone can fly: off."), or "Already so" when it
  was. A refusal is the op's words in her terms ("Not changed: the region didn't change name: only the parcel's
  owner (or a group member with the powers) may").
- **Her confirmation** (Notices, as everywhere; the dispatcher's words for land, `docs/land.md` > What waits for the
  person, with a setting's change now listed in these words, not the op's): who may enter (only the allowed list, the
  group, passes and their price and hours, the payment-info and age switches, the group itself), anyone can edit
  terrain, the allowed and banned lists, eject, freeze, returning objects, a sale, buying, a pass, abandoning, deeding,
  dividing, joining, terraforming the whole parcel, and the region's settings. The other switches and words (name,
  description, fly, scripts, category, landing, music) change at her press, as the viewer's checkboxes do: they're
  hers to set and set back. Unfreezing and taking it off sale aren't asked, as in `docs/land.md`.
- **Not here**: the parcel's picture (snapshot), media's other settings (type, size, loop, the media's description),
  Experiences, the estate's switches changed (read only on the Estate tab), the region's top scripts and colliders and the
  debug switches (SojournLand has them, the assistant reaches them), claiming public land,
  and Second Life itself (only the local grid is checked, where land costs nothing and there's no covenant; there,
  who owns what comes without each owner's newest date). Her verdict on the words and on what asks first.

### Chat logs

`MainForm.ChatLogs.cs`, `ChatLogs.cs`; Alt+0's Chat group, Chat logs kept on this PC: all (the default, as in Radegast and the official viewer), conversations (IMs and group chat, no nearby chat), or off. Said once on arriving after the first build that has them.
- **The assistant's log** (2026-11-11, the blind tester kept forgetting her descriptions): what she asks the assistant (a description as "You: Describe Kestrel") and every reply, in `Assistant.txt` beside the conversations, under the same switch (kept with all or conversations) and incognito's rules; Alt+9's history starts each session with its last 60 lines, under which "(Those are earlier lines. New ones follow.)", as a conversation opened again does (`FillAssistantEarlier`). Read-out > Chat logs lists it with the rest.
- **The files**: plain text, one a conversation, in the data folder's `chat-logs/GRID/ACCOUNT` ("Nearby chat.txt", "Kestrel - 1a2b3c4d.txt", the end of its session id, as display names change): each run of lines under a line with the date, each line with its time. Written by a thread of their own (a write once held the window 2.1 s on the blind tester's PC, twice in an afternoon, 2026-11-11); reading, listing and deleting wait for what's queued first, at most 3 s. They're other people's words, so they stay on this PC: Send a log only takes `logs/`, and the assistant's file tools and shell are refused them (`assistant/harness.py`; its `tools/assistant_guard.py` tries).
- **Reading one**: Read-out > Chat logs (`//chatlog`), the logs with when each last changed, the most recent first, Enter reading one; `//chatlog NAME` the one with that in its name; Chat log on a person's Applications key, on a group's, and in a group's or conference's message box. It opens in a read-only window, as a notecard's, at the newest line: Control+F finds, Control+G goes to a line, Control+K opens a link, Escape closes. The list's Open the folder opens it in Windows, and its Delete (Delete too; last in the Tab order) asks in Notices: gone for good.
- **A conversation opened again**: closing one no longer loses it. A new tab starts with its last 60 lines, under which "(Those are earlier lines. New ones follow.)", from the log, or with logs off from this session's memory (200 lines each; gone when the app closes). Nearby chat's history isn't filled at login: it's a different place by then. The session's `TesselIM history` isn't used, as `leave` drops it.
- **Incognito**: nothing from a spell is written or remembered, seeing or blind, so a tab opened after one doesn't bring it back. What's on screen in an open tab stays as it is. Nor is anything kept with someone marked Always incognito with them, or any IM while IMs are incognito: their spell starts only once the conversation is in focus, so a first line would otherwise be kept.

### Incognito

M1; `MainForm.Incognito.cs` over Session's Private/, the VR client's rules (`docs/assistant.md` > Incognito).
Ctrl+Shift+I turns it on (any key of yours, the screen reader's pass-on too; the assistant may too, asked) and off,
which only a physical key does, fresh, as confirmations take it (Windows Hello instead with
`access_answers_without_key` hello); SojournSettings refuses incognito off to every caller. "Incognito on." / "off."
are said (A1's). Alt+0: Incognito; Incognito: the assistant sees (turned on mid-spell only by your own key there);
while you're in an IM (the conversation in focus is the VR feed's open IM here); always on this parcel. A person's
Applications key has Always incognito with them (Session's Triggers: within chat range or an IM with them, ending by
itself once neither holds). The reader keeps reading (on the desktop the window is the view); the review history drops
the spell's lines when it ends; the session log pauses; the title says "incognito" instead of the region (the status
bar adds ", incognito"). Access writes nothing of its own from a spell: its histories are on screen, and its chat logs leave the spell out (Chat logs, above).

### Voice chat

M1; `MainForm.Voice.cs` over Session's Voice/. On unless `--no-voice`, push to talk by default: hold the talk key (F4;
`access_talk_key`: f4, f12, scroll_lock, pause), only your own key opens the mic, a short click as it opens and another
as it closes (the `mic` earcon, at Access's own sounds' volume); a key held when the window loses the focus lets go.
Shift+F4 switches to an open mic (it starts muted; F4 then mutes and unmutes, unmuting by your own key only) and back.
Ctrl+Shift+V or `//voice`: where (nearby voice, a call, voice off here), who's speaking, who else is in voice, your
mic. `access_voice_speakers` (off): someone starting to speak said, each at most once in ten seconds. A call ringing is
said; `//voice answer` or `//voice decline`. Alt+0 has voice on or off, the mode and how loud residents are (not where
voice is heard from: Access hears from the avatar's head, having no camera). World audio (objects' sounds, the
parcel's music and its titles read out) is on unless `--no-sounds`.

**The people in voice** (Radegast's participant list; gap 14, 2026-11-08): Control with the talk key (Control+F4; with
Scroll Lock or Pause as the talk key, Control+Break, which is what Windows makes of them), World > People in voice, or
`//voice people` opens a menu where the focus is, the people in voice by name ("Kestrel, speaking, muted", "Pip, at
0.5", "muted by a moderator"), Right opening a person's actions, the same list as everywhere (Mute in voice or Unmute in
voice and How loud in voice among them, their letters as always); then Mute everyone else, Unmute everyone and Reconnect
voice, with no letters, so a letter only ever finds a name. A menu rather than a list that stays open: who's speaking
changes by the second, and Control+Shift+V says it as it is.
- **Mute everyone else** (`//voice mute all`; the op `SojournVoice muteOthers`): everyone else in voice muted for her,
  people who join later too, for this session only. Not put on her block list as one person's Mute in voice is (the
  official viewer's way, kept): a club's worth of strangers would fill it, and stay silent in every viewer for good.
  Radegast's Mute all is one by one for the session too. Unmute in voice on someone lets them through (and Mute in voice
  takes that back, still only for the session). Control+Shift+V says it's on.
- **Unmute everyone** (`//voice unmute all`): everyone in voice heard again, and voice-only mutes of the people in voice
  now taken off her block list (all of them heard, as the words say); someone she's blocked stays silent, said by name,
  since unblocking is its own action.
- **Reconnect voice** (`//voice reconnect`; `SojournVoice reconnect`): every connection started afresh at once, its
  backoff back to the first wait, a call keeping its channel; "Reconnecting voice.", then "Voice is back: nearby voice, 3
  others in it." or, after about 20 s, "Voice hasn't come back yet. It keeps trying by itself." Voice retries on its own
  every 10 s at most (Radegast's button is for its retries giving up, which ours don't), so this saves the wait or clears
  what the retries don't. With voice off here, or nowhere yet, it says there's nothing to reconnect.
- Checked by the scripted step "people in voice, to act on" with `ACCESS_VOICE=1` and without the mixer (nobody else in
  voice, the connection retrying): the menu, both everyone-actions in `SojournVoice status`, reconnect said and its
  outcome. Never yet with someone else in voice: muting someone in the menu, a newcomer muted by the spell, and the
  block list's entries taken off by Unmute everyone want the mixer (`tools/grid.py voice up`, which starts Docker
  Desktop) or Second Life.

**Sound devices** (`MainForm.Devices.cs`): Alt+0's "Sound output: speakers or headphones" and "Microphone for voice"
rows are the session's own `speakers` and `microphone` (the VR client's menu sets the same). Enter on one opens the
list of devices to choose from (a menu, the one in use checked); Space or Right steps from the default through each
device, Shift or Left back; Control+Shift+O moves the sound to the next device, round to the default; World > Sound
output has "Next device" and the same list. All of them offer the same devices in the same order: speakers, headphones
and headsets first, outputs to a TV or monitor and digital outputs last, each said with what Windows says it is and
where it stands ("Sound on Speakers (Realtek(R) Audio), speakers, 2 of 4."; "4 - 2D HD LG TV (2- AMD High Definition
Audio Device), a TV or monitor, over HDMI or DisplayPort"). Which devices there are follows Windows: a TV's HDMI output
is listed only while Windows has it (the TV on). A change takes effect at once and is said only once it's checked
(`docs/voice.md` > A change is checked): a soft two-note chime plays on the new output at Access's own sounds' volume,
so the person hears where sound comes out now; one that doesn't start, or that Windows passes no sound to, is put
back and said so with why ("Not changed: ... didn't start: another program is using it exclusively, error 0x8889000a.
Sound stays on Speakers (3- EPOS PC 8 USB)."); one that plays but that Windows has muted or near 0% is kept, with
"But Windows has it muted." A chosen device that's unplugged plays through the default until it's back. The blind
tester switches her headset and speakers in Radegast (2026-10-31); on 0.1.0.737 she chose her TV's HDMI output (its
own speakers) from the row, Access said so, the shim had it open, and she heard nothing (2026-11-01; why is open:
the log now has the output's format, Windows' volumes for it and what it took, see `docs/voice.md`). The shim follows Windows by itself (the default as
it moves, a device unplugged and plugged back, none there at all: `docs/voice.md`); after 0.1.0.707 went silent when
her headset was unplugged, what it did is said in one line once its events settle (1.5 s), while voice runs (its
thread reads them): "Sound now on Speakers (Realtek).", "No speakers or headphones: Sojourn Access is silent until one
is plugged in.", "Microphone now ...", "No microphone: voice chat hears nothing until one is plugged in."; the talk key
then says "No microphone: nobody hears you until one is plugged in."

### Gestures

`MainForm.Gestures.cs` over Core's `GestureOps`; the blind tester couldn't play hers at all (2026-10-31). The active
gestures are the inventory's gesture items activated (OpenSim marks the item; Second Life's login lists them, which
`Look` now makes active too), with their triggers once their assets are read (at login, in the background).
- Typed in nearby chat, the first word of a line that's an active gesture's trigger (split at spaces, case aside, as
  the official viewer's `triggerAndReviseString`) plays it ("Playing Clap.") and is replaced by the gesture's
  replacement text, or left out; what's left is said, nothing if nothing is. Channel lines (`/5 ...`) and IMs aren't
  looked at, as in the viewer.
- Control+Shift+G: the active gestures as a menu where the focus is, by name, each trigger shown as its key; Enter
  plays one. World > Gestures lists the same.
- The inventory: a gesture's words, Enter and its Applications key (Keys, above).
- Playing (`GestureOps.Start`, also the agent API's `LLGesture startGesture`, now any gesture of mine): the steps as
  the viewer's `LLGestureMgr` runs them, not LibreMetaverse's player: animations started and stopped, a sound triggered
  in the region from where you are, chat said as your own nearby chat (so read back as your lines are; "/5 text" on its
  channel), waits for a time or for its animations to end (30 s at most); at the end it waits for its animations as the
  viewer does, so a looping one keeps it playing until stopped (`stopGesture`), which stops what it started. One
  already playing isn't started again.

### The assistant

Alt+9. It works once a provider that works is chosen in Alt+0's Assistant group (from the repo: the Claude account signed
in here, an Anthropic key, OpenRouter or the studio relay, with the venv set up by `python tools/assistant.py setup`;
packaged: the last three only, Packaging above; `docs/assistant.md` > Switching it on); without one it says once why
and how to choose, and everything else works. A key or token goes in on the key row (Enter, a password box, Control+V
or typed; Delete twice forgets it); its shape picks the provider. OpenRouter's model is said as its name and tag (Enter
steps through Claude's three, then each OpenRouter family's current model; Control+Enter types any id). Replies are
read by the announcer (A1's "the assistant" kind) and kept in the history; its choices are buttons ("1: ..."); a
confirmation it needs comes to Notices as the first item. Its instructions know it's Sojourn Access
(`assistant/prompt-access.md`): no view, replies through the screen reader, plain text, directions by clock face as the
lists give them.

### Describing

M5; `MainForm.Describe.cs`. Ctrl+D describes what's selected where the focus is: a person in People nearby, a friend
(as they look here, or their profile pictures when they're elsewhere), an object in Objects nearby, a part in its Parts
list (or, on a face, the part it's on), an inventory item (an attachment you wear as it's worn, a worn clothing layer
as part of you, anything else by its picture: a texture's or snapshot's own, else its thumbnail); anywhere else, where
you are. Ctrl+Shift+D: you and what you're wearing. The same key again on the same thing (within ten minutes) asks for
more: brief by default (a few sentences, the gist first), rich on asking, added to what was said rather than repeated.
Also Describe on the Applications key of people, friends and objects, Describe pictures (or Ctrl+D) in a profile's
window, and `//describe` (here, me, NAME, person NAME, object NAME, more). A ride recorded in walk mode is told the
same way, by Shift+C there or `//ride describe` (Walk mode > Recording a ride).

It says "Describing Kestrel." at once; the description arrives as the assistant's reply, read out whole, a few
sentences at a time in turn (`AccessWiring.WholeReplies`; other announcements are cut at 700 characters), kept in
Alt+9's history and in review. The request carries its subject by id, so the model describes exactly that, and the
pictures start drawing as the request goes (`DescribeApi.Prepare`), so they're usually ready when they're asked for.
On a key, the relay or OpenRouter the key's request goes to no session: the sidecar fetches the pictures and facts and
one lean call writes the description (`assistant/describer.py`; `docs/assistant-cost.md`), recorded as an exchange
so a follow-up to the assistant knows what was said; asked in words, the describe tool does the same and hands the
session the finished text. Who writes it is Alt+0's "Describing model": economy (Claude Haiku 5.5 at high effort, the
default, about a tenth of a cent) or best (Claude Opus 5.5 at low effort, about two cents; four for more), on the
agent API as `access_describing`. On the Claude account the pictures go to the session as before.

Without the assistant (no provider that works, the month's allowance used up, or `--no-assistant`) it says so plainly,
once a session, with the sidecar's own words for why and how to switch it on, then what the lists know, which needs no
model: "Describing needs the assistant. The assistant is off until you choose how it runs: ... What the lists know:
Kestrel Tester, 4 metres, two o'clock. Wearing items named: ..."; for you, your clothing, body parts and attachments by
their names; for an object, its own description, floating text and owner; for here, the parcel, its description, the
people and named things around.

What the model gets (SojournDescribe, `Sojourn.Session/Offscreen/DescribeApi.cs`, and the `describe` tool,
`assistant/describe.py`, offered only in this app):
- Pictures aimed at the subject from cameras of their own, not the avatar's view. A person (or you): whole and from
  the waist up from their front, their back too when rich, other people left out of those pictures. An object from
  your side and a little above; one you're sitting on (a vehicle you ride or drive) from outside, ahead and to the
  right of the way you sit, a little above, with what its riders wear never in the camera's way (it had been framed
  from your own eyes, mostly out of the picture); a flat one (a sign, a poster, a board) face-on with the picture shaped to it, plus its
  face's image as uploaded for reading text (rich: that image at full size, no side view). Something worn, on its
  wearer from their front. One of your HUDs (or a part of it), the HUD as it shows on your screen, drawn bigger, its
  named parts placed on it, with its name, where on the screen, its parts' names and its floating text; the describer
  says what it's for, then its controls and readouts by where they sit, labels word for word. Here, four views from
  your eyes, ahead, right, behind and left, 90 degrees each.
- People, objects and what's worn are always lit as at midday (the viewer's own midday sky, for those pictures only)
  with a white light at the camera, so their colours are their own; here keeps its own light (the user's call,
  2026-10-29, was people and things relit when it's dark, the place in its own light). The model is told the light was
  changed and, from the place's own sun, whether it's night or the sun is low there, so it never gives that light as the
  place's (`lighting`; `describer.py` HOW > Light). The camera's light is a point light at 1.2 (a prim light's
  intensity) out to 2.5 times the distance to the subject's middle, so every subject gets it alike
  (`OffscreenRenderer.Shots.cs`, Render's `FrameRequest.CameraLight`). It costs nothing per picture but the midday
  sky's default reflection probe once in each 32 m square (0.12 s on the studio PC's processor, where these pictures
  take 0.06-0.2 s): captures under a preset are kept by the preset, not by each time it's put on
  (`FrameRenderer.Environment.cs`). Not handled: a place's own coloured lights still add to the subject's.
  Why (Claude Opus 5.5, 2026-11-11): relighting had followed the dark (the sun under about 10 degrees; from 2026-11-03
  also a picture whose middle came out dim, after a jazz club on Second Life), and at the local grid's dusk neither
  held, so the scripted test's moss-green sweater, (74, 103, 65) in its texture, came out dark teal, (11, 28, 32). The
  place's light moves colours at any hour wherever its sun isn't on the side the camera sees (in the grid's afternoon
  the sweater was (65, 93, 65) facing the sun, about (33, 53, 42) the other three ways), and the midday sky alone has
  its sun straight overhead, which leaves anything upright to the sky's blue ((13, 30, 33) facing any way). With the
  camera's light, (70, 100, 69) facing any way, on the GPU and on the processor. (The sweater, a worn box, was also lit
  wrongly by turned prims' normals, a renderer bug fixed that day: `docs/rendering.md`.)
- A camera that would be inside a wall comes closer (and wider) or goes round to another side.
- Each picture lists what's visible in it by name (a 16 x 9 grid of picks through it: who and what, where in the
  picture, how much of it, and where from you, "4 metres, two o'clock"), with how much the subject fills; the names go
  beside each picture as well as in the facts, so they stick to what's seen.
- **A person asked about is drawn whole** (Claude Opus 5.5, 2026-11-06; the blind tester's club night, where several were
  "too complex for your viewer to draw"). The complexity limit (350,000 here) makes a plain stand-in of anyone over it, but
  a person's own pictures draw them whole for that batch (`OffscreenRenderer.DrawnWhole`, logged with their complexity),
  unless the person chose never to draw them. In pictures around her a stand-in stays one, said so.
  `SOJOURN_TEST_AVATAR_LIMIT=1` for `tools/access.py`'s step "describing a look" checks both.
- **A look waited for and said as it had loaded** (Claude Opus 5.5, 2026-11-01). A surface whose texture hasn't loaded
  is drawn plain grey (or its material's own colour, often white), and the describer had named those as colours: on
  0.1.0.737 on the processor, the blind tester's partner beside her came out as "white hair, a light grey top with
  matching trousers" (and "deep brown skin, dark slate grey") when he wore a moss-green sweater, black trousers and
  brown hair. Her logs showed why: a picture settled until everything in view out to the draw distance had loaded, at
  most 3 s, or until that count made no progress for a second, and on a busy club it never reached nothing and seldom
  improved, so each picture was taken after about a second; with no frames between pictures on the processor nothing
  of anyone's look stays loaded from one to the next (textures unseen 3 minutes are let go), so every picture of him
  started cold and his worn things' textures (from the network) were still grey, while her own (on disk from earlier
  sessions) came in time. In a rich description his back view, taken seconds later, most likely showed him textured:
  the describer took it for someone else. Now:
  - Each picture waits for its subject's look besides the place's settling (`OffscreenRenderer.Shots.cs`): a person's
    bakes and how they're drawn (a loading cloud, a plain stand-in over the complexity limit), each thing they wear,
    or an object's linkset, by its faces' colour textures (a diffuse, or a GLTF material's base colour; a bakes-on-mesh
    face as the wearer's bake) and shapes still arriving, and what's two levels or more blurrier than the picture
    wants unless memory is holding sharper copies back (`FrameRenderer.LoadOf`, Render's `RegionScene.Load.cs`,
    `Avatars.Load.cs`); only faces a view asked for lately count, so a part never in view isn't waited for. At most
    12 s a batch on a GPU, 30 s on the processor, and 4 s (8 s) without any of it arriving (once a picture has given
    up on something, the batch's later pictures 1 s, or 2 s). A picture with no subject
    (here's four) waits the same way for the people and things filling it within 25 m, found once the place has settled.
    The log says how long it waited and what it still lacked.
  - Each picture says what of the subject's look had loaded (`subject_look`): fully loaded (colours real), blurry on
    named things, or which worn things' colours hadn't loaded, which are partly loaded or not drawn yet, the skin's bakes,
    a loading cloud, a stand-in, with "most of it hadn't loaded" when it's so (a person: more than half of what they wear
    in view, their body's bakes counting as one; a thing: more than half its surfaces); each thing seen in it whose look hadn't
    arrived says so (`look`). The count of everything in view still loading is the log's alone: said to the model on a
    busy place it was thousands of far things, so every picture looked unfinished, or none did.
  - The describer (`describer.py` HOW > Loading) never names a colour, pattern or material for a surface said not to
    have loaded (no grey or white hair, clothes or skin from it), names what had loaded, says plainly which parts'
    colours aren't known yet, and when most hadn't, gives what the shapes show (build, pose, a garment's cut), says the
    colours aren't known yet and suggests asking again in a moment; two pictures of one subject that differ are the same
    one, more loaded in one. Someone sitting on a pose ball or dance ball is animated by it, so standing or dancing in
    the pictures isn't a contradiction (the facts' `sitting_on` says so; a description had reported it as one).
  - Testing: `SOJOURN_TEST_HOLD_LOOK=NAME[:SECONDS]` holds back the bakes and worn textures of avatars whose name has
    NAME, `SOJOURN_TEST_HOLD_WORN` only what they wear, for good or SECONDS from each texture's first ask
    (`World/LookHold.cs`); `tools/access.py`'s step "describing a look as it loads" puts a moss-green box on the
    tester and describes them, with either switch set in the environment. On the local grid (2026-11-01, Haiku through
    the relay), on a GPU and on the processor: unheld, the box green in the picture and named green; held 5 s on the
    processor, waited out (5.1 s) and green (the old settling, by its rule, would have stopped after 2 s without progress); held for good, said by name in
    `subject_look`, grey in the picture, and the description said its colours hadn't loaded and named none, while
    giving the hair and trousers, which had. Not seen yet: Second Life's server bakes and a busy place's network.
- The facts: names (the viewer's complete names; "Object" counts as unnamed), an object's description, floating text,
  owner, parts, size, who sits on it, for sale, and that you're on it; what someone wears by its items' names and
  points; for you, the clothing and body parts too, and what you sit on, and each thing you wear also by the
  inventory item it was worn from where that name differs, and by its own description where it adds something
  ("Pip (on left shoulder; its inventory item: Flying Pet Gryphon)"; looked up by its AttachItemID in the inventory
  already loaded, as the worn list does; one of them described on its own gives its item too, `its_inventory_item`,
  said without the assistant as "Your inventory item: ..."). An object renamed in world keeps its product's name only in that item: the
  blind tester's renamed shoulder pet, a winged gryphon lying with its wings folded, was described by both models as
  a black-and-white cat (2026-11-02). Others' worn things stay by their object names. The describer is told what the
  item name and description are (the item as the person has it, often the product's name; the object's own words),
  to know what a thing is by, not as something seen. `tools/access.py`'s step "describing" wears a box renamed in
  world and checks the facts and Control+Shift+D give both names. You are "the person themselves (you)" wherever
  you'd be named (owner, wearer, who's seated), never your own name; for here the parcel, its description, people within 30 m and named things within 20 m.
  Textures and profile pictures by id (`texture`, `profile`). With `--no-render` there are no pictures (said so), only
  the facts and textures.
- The describer's instructions (`describer.py` HOW) say how to describe: brief or rich, the names given and never
  guessed, clothes in a designer's register (each garment's kind and cut, colours and material, what's printed or
  pictured on it and where on the garment, then hair, shoes, accessories; a careful describer's way with outfits), looks
  as presented without judging, the person as "you" (on something they ride: "You're riding ..." first), text read
  word for word or said to be unreadable, directions from the person (the
  clock's sides spelled out), uncertainty said in words and nothing invented, nothing about pictures or cameras, each
  detail checked before answering.

### Paying and buying things

`MainForm.Buying.cs` (Claude Opus 5.5, 2026-11-06; the blind tester couldn't pay a club's tip jars, as Objects had no Pay).
In Objects (Alt+7) and Parts, on the Applications key or Enter, as the viewer's pie menu offers them (`ThingActions.For`):
**Pay...** on what has a money event, it or a part (a pay goes to the part with the event, else the root), **Buy** on
what's for sale and not hers, **Take a copy** where she may copy it. A thing whose left click is pay or buy has that item
first. The list says it: "Tip Jar, 3 metres, eight o'clock, takes payment", "Vendor, 3 metres, ten o'clock, for sale,
L$50" (a part: ", takes payment"). Pay asks the amount, the box named with what its pay window offers ("Amount in L$. Its
pay buttons: L$10, L$50, or any amount"; "It takes only ..."; llSetPayPrice, the price suggested filled in, one that
takes nothing said so); Buy asks the price afresh. Both go to her confirmation in Notices with her own key, as paying a
person does. `//pay NAME` or `//pay NAME 50`: someone in the region by name, else a thing within 30 m, else a named part
within 10; `//buy NAME`: the nearest within 30 m. An object in the region beside hers is paid through that region.
`tools/access.py`'s step "paying and buying things" (the visitor's `tipjar` and `forsale`). The local grid has no money,
so a paid confirmation and a paid purchase haven't run there: only the amount, the refusal and a free purchase.
Her own payment (a person or a thing) is said once, by its answer ("Paid Kestrel L$50. You have L$1480."): Second Life's
"You paid ..." notice for it isn't read while it's under way or for 10 s after a confirmed answer, though it's still a line
in the history (`PayConfirmedAsync`, `OwnPayNotice`); in the blind tester's log of 2026-11-10 each tip was said three times,
the notice cut off by a key and said again. One the grid hasn't confirmed, or one the assistant made, is read as ever.

### Sending a log

Help > Send a log to Studio Quire, or `//sendlog` (`SendLog.cs`), for a tester to send us how a session went. With no
Studio Quire token kept it says so and gives the issues page instead; anonymous uploads don't exist. Otherwise a small
window, the focus in What happened (a note, Enter a new line), then Session (this one, then earlier ones by day and
time), What's sent (read-only, the plain line below), Send (Alt+N; Enter elsewhere), Review what's sent (the filtered
text in a read-only window, as F1's) and Cancel. "Sending.", then the outcome said; a failure keeps the window open.
The relay keeps it 30 days (`tools/relay/README.md`); `python tools/relay.py logs` and `logs fetch` read it here.

What's sent never holds other people's words (ToS 6.2(i), `docs/tpv-policy.md` > Data and privacy), so the log marks
whose words each line carries rather than leaving it to be guessed from text. Its file starts `~log {"v":2}`, and
words are records, `HH:mm:ss ~tag {json}`, never plain lines: `~words` each feed entry with `from` me, people, objects
or app (a notice by its kind, `LineSources.OfNotice`: a person's offer, group notice, invitation or an estate's message
box is people's, as is any kind not listed), `~heard` each line the screen reader was given (`Announcer.Heard`, with
`LineFrom`: an answer that quotes someone, such as a search result's About, a group's charter, `//parcelinfo` with
descriptions or a route's notes, says People), `~name` each name learned (legacy, display, pseudonym; the person's own
marked `me`). The console shows chat as before, but it's no longer in the file as plain lines. `LogShare.cs` filters (and `LogSend.cs` packs and posts, both shared with the VR client's problem and crash reports):
other people's lines and anything not placed become "(words left out)"; names and keys of everyone the log names
become "person N" (the order they come up) and the person's own "me", except that a name of one word that's a common
English word (a display name's first word "Grey", a display name "Amber"; `common-words.txt`, SCOWL's commonest) is
replaced only where it's capitalised as a name, so "a light grey top" stays (a log once read "light person 29 top");
an object's line relaying someone ("person 2:
...") and any line repeating five words in a row of someone's left-out words (or a whole short line) are left out,
except words that are an object's name (someone saying "Operator HUD" had every line naming her GTFO! Operator HUD
left out, its menus and her touches of it, 2026-11-02);
a display name the session never looked up (someone only the radar saw) but written before a known name in brackets,
the viewer's "Display (username)", is that person's name too, wherever it comes: a radar HUD's "Display (username) is
now in chat range" had sent display names whole in every log until 2026-11-03 (the usernames were replaced); those logs
were scrubbed in `local/relay-logs` and deleted from the relay;
an object named as a person (a chat relay, a small avatar's speaker) is that person talking, so its words are theirs,
left out and caught where repeated: they'd gone out until 2026-11-06, those logs scrubbed and deleted the same way; a
name of nothing but punctuation (".") is no name to replace, as it landed in every full stop ("ENE.person 23 or left");
keys, capability URLs, the Windows account's folder and public IP addresses are masked. The filter runs off the
window's thread (Send and Review wait for it, the window still answering): on the blind tester's PC it had held the
window 2-3 s each time (2026-11-11). The assistant's replies and
what it said about people stay, names replaced. A record behind another line's time stamp ("10:00:47 10:00:47 ~words
...") is read as the record: the console logger writes a line in pieces (stamp, coloured level, text) and a record
written from another thread between them took the stamp, about 1 in 100 records in a test run (2026-11-08), so it read
as a plain line and was kept, a one-word line of someone's whole (longer ones the repeat check caught); none were in her
sent logs. `SessionLog`'s Tee now hands the file whole lines. A log from before the records isn't offered. `tools/logcheck` checks
the filter on a synthetic log (`dotnet run --project tools/logcheck`; with a path, prints that log as it would go).

### The session itself

Radegast's New window, Log Viewer and Clean Cache (gap 21, 2026-11-08; `MainForm.Session.cs`).
- **Reading a session's log** (Help > Read a session's log..., `//log`): the sessions' logs, this one first, then earlier
  ones by day and time (the one before a crash among them; with only one, it opens at once); Enter reads one in a
  read-only window as a chat log's, at its newest line (Control+F, Control+G, Control+L, F1, Escape). It's the log as
  Send a log would send it (`LogShare`): the records in words, other people's words left out and their names as person 1,
  person 2, since the file itself is lines of JSON, and this way she reads exactly what she'd be sending. No token
  needed. Radegast's Log Viewer is its debug log, live; ours is a snapshot as it opens.
- **Clear the cache at the next start** (Help, checked while marked; `//clearcache`, `cancel` or the item again to take
  it back): marked by a file in the data folder, said with its size ("1.1 GB of what Second Life sent ... which comes
  again as it's needed"), and cleared as the app next starts, in the background before anything uses it (a login waits
  for it), then "The cache was cleared as Sojourn Access started: 1.1 GB." after arriving. World's Log out, back to the
  login window, is a next start. At a start rather than at once, as the official viewer and Firestorm do: while logged in
  the files are in use, and what's loaded stays in memory anyway (Radegast deletes at once). What goes: every grid's
  folder under `cache/` (pictures, sounds, models, animations and other assets, partial downloads, client-side bakes on
  grids without server baking). What stays: `cache/render` (the drawing code compiled for this PC, which takes minutes to
  build again on a PC drawing on the processor), the local grid's `starter.json`, settings, logins, chat logs, logs,
  the assistant's files; the inventory and names aren't kept on disk anyway. A file in use (another window running) is
  left and counted. `CacheClear`, started by `Program`.
- **Another account, in a new window** (World, `//newwindow`): the program again as its own process, at the login window
  with the grid filled in and the username empty, its agent API on a free port (this one's is taken; the assistant
  comes in through it). Each window is its own session, sounds and screen reader lines (both read out what arrives, so
  two accounts in one place are heard twice; F6 in one mutes it). They share the data folder: settings (the last saved
  wins, and the login form then offers the last account logged in), logs (each its own file), the cache and the
  assistant's home. Same account twice is the grid's to refuse.

### Messages to the builders

Help > Message the builders (Claude, at Studio Quire), or Control+Shift+B (`MainForm.Builders.cs`, `BuildersLine.cs`):
a tester's direct line to the Claude sessions building Sojourn Access, through Studio Quire's relay with their token
(`tools/relay/README.md`, `/relay/messages`), in place of passing everything through Tessel (agreed with Tessel,
2026-11-01; how a session handles it: `docs/access.md` > Who directs it). It's a conversation tab like an IM's, "The
builders (Claude)", in the Control+Tab ring: its history over its Message box, Enter sends, Control+W closes it,
Shift+F6 mutes it, `//` commands work as in any input. Whatever the person was doing, the key puts the focus in its
Message box ("Message to the builders", described as Claude sessions at Studio Quire that reply when a session reads
it).
- **Who she's talking to**, the first time (kept in `builders.json` in the base dir) said in full, and always the top of
  the history: Claude sessions, AI, building Sojourn Access at Studio Quire, not Tessel and not a person; replies come
  when a session reads the message, minutes or a day later; only what she types goes, kept with our replies 90 days
  (Tessel's ask, 2026-11-01).
- **Sending**: "Sent." once the relay has it, the line in the history as "You: ..."; a failure is said and the text
  goes back into the box.
- **Replies** are read as an IM's: "Claude, the builders: ..." elsewhere, "Claude: ..." in the tab (the signature is the
  session's, Claude unless it gives one), with the IM's sound (none in the tab), the tab opening and waiting as a new
  IM's does, the focus never moved; F6, Shift+F6 and "IMs read out" apply, and a line cut by typing is said again.
  Replies that came while the app was closed are said once after "Logged in" ("While Sojourn Access was closed, the
  builders replied. ...", the last three of many); `builders.json` keeps the newest said, per token.
- **Asking the relay**: once a minute while logged in, every 15 s for ten minutes after she writes, at once on opening
  the tab; backing off to five minutes while it can't be reached; not again for a token it refused (until the key row
  changes). `SOJOURN_BUILDERS_POLL` (seconds) for tests, `SOJOURN_RELAY` for another relay.
- **Without a token** the tab says how to reach us instead (Tessel, tesselwright, in Second Life, or the issues page),
  and Enter says it again, keeping what was typed.
- **Off**: `access_builders` (Alt+0 "Messages with the builders", on by default): no tab, the relay not asked, the key
  saying where to turn it on.
- Only what she types goes; nothing else of the session (no chat, no names). The history isn't kept on the PC; it's
  fetched from the relay at each start.

## How it fits

- `src/Sojourn.Access` (WinForms, net10.0-windows10.0.22621.0 for Windows Hello, x64; output `SojournAccess.dll`), on
  `Sojourn.Session` only, never the VR client. `Program.cs` (the login form first, then the session; the grid's
  questions; another account restarts the app), `LoginForm.cs` (with the ToS and MFA windows), `MainForm.cs` (the
  session made and started with `SessionHost.StartAllAsync`, keys, read-out shaping, the session's own events:
  arrivals), `MainForm.Chat.cs` (nearby, conversations, histories), `MainForm.Lists.cs` (people, Alt+3's and beside
  chat, objects), `MainForm.Notices.cs`, `MainForm.Assistant.cs`, `MainForm.Settings.cs` (a curated list over
  SojournSettings), `MainForm.Inventory.cs` (the tree, search and Show, the actions; Big inventories, below),
  `ReaderTree.cs` (its tree view and Places', read through Windows' own MSAA object: WinForms' numbered the items wrong
  for NVDA), `ReaderList.cs` (every list: its item's focus announced only while the list has the focus),
  `MainForm.Favorites.cs` (favourites in the inventory), `MainForm.Backup.cs` (Back up to disk), `StartPlaces.cs` (the Favorites bar's landmarks as start places),
  `MainForm.Places.cs`, `MainForm.Search.cs`, `MainForm.People.cs` (friends, groups, profiles, pay), `MainForm.Groups.cs`,
  `MainForm.GroupPeople.cs` and `MainForm.GroupNotice.cs` (a group's menu, its members and chat, a new notice),
  `MainForm.GroupSettings.cs` and `GroupRows.cs` (a group's settings and roles), `MainForm.Estate.cs` and `EstateRows.cs`
  (the estate's tools, About Land's Estate tab),
  `MainForm.Describe.cs` (describing), `MainForm.Parts.cs` and `Linkset.cs` (parts and faces), `MainForm.Pointing.cs` (what a friend points at), `MainForm.PointAt.cs` (pointing at something yourself), `MainForm.TheirThings.cs` (one person's worn things and animations), `MainForm.Huds.cs` (HUD faces and buttons), `MainForm.Place.cs` and
  `PlaceWords.cs` (placing by words), `MainForm.Outfit.cs`, `MainForm.Incognito.cs`, `MainForm.Voice.cs`,
  `MainForm.Menu.cs` (the menu bar), `MainForm.Session.cs` (a session's log, the cache, another window), `MainForm.Builders.cs` and `BuildersLine.cs` (the builders' tab and its relay
  calls), `MainForm.Upload.cs` (uploading and its window), `MainForm.Exports.cs` (export and import), `MainForm.AutoSit.cs` (auto-sit), `MainForm.Build.cs`, `BuildRows.cs` and
  `BuildShapes.cs` (the build editor; its settings' words; faces and contacts without a picture), `MainForm.Gestures.cs`, `MainForm.FriendsOnline.cs` (friends online at login as
  one line), `MainForm.Devices.cs` (sound devices), `MainForm.CutLines.cs` (lines cut by typing), `MainForm.Names.cs` (People's
  names: Core's `World/PlainName.cs` does the wording), `EditorForm.cs` (notecards and
  scripts), `EditorForm.Replace.cs` (its Replace window), `Dialogs.cs` (a line asked for; a person picked), `Commands.cs` (the `//` set), `PersonOps.cs` (the
  person's own actions through the ops; where people and things are), `Bearings.cs` (clock faces, compass, SLurls),
  `FormVoice.cs` (UIA notifications from WinForms' own accessibility objects), `RealInput.cs`, `AccessSettings.cs`,
  `AccessOptions.cs`; `Walk/` and `Sound/` (below).
- **The person's own actions run the ops directly** (`PersonOps.DoAsync`) when they began with the person's input
  (`RealInput.Acting`: the input message an action started from, a hardware key or click or a key the screen reader
  passed on for one, carried through its awaits for up to 5 minutes; the session started at Log in doesn't carry it):
  the assistant's confirmations (its touches on others' things) and incognito's limits on callers don't stand in the
  way. An action begun by anything else (a timer, a callback, a UI Automation invoke, a key a program sent) goes through
  the gate as the assistant's would (`GatedAsync`); lookups that change nothing go `ReadAsync`. Spending, giving and
  deleting always go through the gate (`MainForm.ConfirmedAsync` over `GatedAsync`) so the person confirms those too:
  the person asked for it, so its own confirmation (the one raised for that call's arguments, `PendingConfirm.For`,
  never another showing first) comes up selected in Notices with the focus on it, and the focus goes back where it was
  once it's answered. The person's own cancel of it says "Cancelled." and nothing else (`ConfirmedAsync` gives its
  caller nothing to say; the op's own refusal is for other callers). Answers to notices go straight to
  `Notifications.RespondAsync`, as A1's keys do; a confirmation answered from Notices answers the one that notice
  showed (`ReaderActions.Confirm(token, ...)`), never one that took its place.
- **Only the person's own press answers** (confirmations, notices' keys, the ToS's I agree and Continue, the
  llTextBox reply, MFA): the assistant can run programs on this PC, so `RealInput` (the window's message filter) judges
  every input message as it arrives and `InputJudge` (pure logic) says how far it counts:
  - Confirmations, notices and the ToS are answered on lists and buttons, where screen readers don't take keys, so they
    take only what Windows calls hardware (`IMO_HARDWARE`; injected, posted and system-made input never), a fresh press
    (Windows' repeat bit; a button's Space or a click answers as of its press, and a key or button already down when
    the app saw nothing of it doesn't), made after what it acts on last changed by anything but the person. The focus
    (every `WM_SETFOCUS` in the app, seen by a thread hook), the selected notice and the window in front
    (`WM_ACTIVATEAPP`) are noted with who changed them: the person (during one of their presses), our own code acting
    for them (`RealInput.Ours`: the focus put back after an answer, onto the confirmation they asked for, the next page
    of a menu they're paging, only ever another script menu of that object's, so a web page, an item or a question it
    sends then waits out the settle like any arrival) or anything else, including a message another thread sent, a UI
    Automation call, a notice arriving or the app coming to the front. After our own move a press counts if it went
    down after it; after anything else, from 0.7 s after it (`InputJudge.Settle`: a key meant for something else, or
    one pressed before the change, can't land on what's there now). Refused, the person hears why ("That takes your own
    key.", "That key was already down...", "That changed just as you pressed. Press again if you meant it.").
  - Whether another thread's message is being handled is what Windows says (`InSendMessageEx`, for the whole of its
    handling). Don't pair the sends a thread hook sees with their returns: UI Automation's connection messages
    (`UiaServerConnection_HookMessage`, sent at each focus or selection change a screen reader follows) go in and never
    come back, and that once refused every key for 10 s after Ctrl+N or a confirmation.
  - Typed answers are sent from edit fields, where NVDA takes Enter, the arrows, Home, End, Page Up and Down, Backspace
    and Delete before any window sees them and sends each on with keybd_event (`EditableTextBase` binds Enter to
    `caret_newLine`, which calls `gesture.send()`), so the window gets them as injected input, as from a program. So
    `KeyHook` (a low-level keyboard hook on its own thread, on only while the app is in front, put back ahead of other
    hooks every 2 s and at most once a second when a key came without its press, since NVDA's swallows the keys it
    takes and raw input never sees them either) notes the hand's presses and releases, and each press is good for one
    injected message of the same key into the window that was in front when it was pressed, within 1 s. That counts
    for the person's actions and typed answers, never for confirmations. The reply sends only text the person's own
    keys built (a character counts only as the character of their key just before it; anything else putting text
    there, a program setting it by UI Automation or posting characters, makes it refused until the box is cleared), and
    only to the notice it was typed for (another selected, the box empties). MFA's code the same.
  - Characters (WM_CHAR, WM_SYSCHAR) never answer, and the ToS's keys have no mnemonics: WinForms presses a mnemonic
    button on a character message, and a posted WM_SYSCHAR or WM_CHAR 'c' presses `&Continue` beside the focus
    (`tools/inputcheck` checks it). I agree is undone if anything but the person's press ticks it.
  - Negative answers (Cancel, Ignore) and the assistant's offered choices take the person's input, passed-on keys
    included.
  - **Without a physical key** (`access_answers_without_key`, Settings: `refuse` by default, or `hello`; Alt+0's row is
    "Answers from a braille display, dictation or on-screen keyboard", its values said with what they mean ("refused.
    Confirmations, menus and replies take only a physical keyboard's keys, since a program can send keys too; leave it
    if you use a keyboard", or "through Windows Hello ..."), the setting's own description the same, since the blind
    tester asked what "answers without a physical key: refused" meant (2026-10-31); lowering it to hello is the person's own, in Alt+0 by a physical key or Windows Hello, and
    SojournSettings refuses it to callers, since it guards against programs, the assistant's among them; back to refuse
    anyone may): braille keyboards, Dragon, Voice Access, on-screen keyboards and switch access type through injected
    input, which can't be told from a program's. With `hello`, such an answer is asked again by Windows Hello
    (`Hello.cs`, `UserConsentVerifier` for the window, the action in its words: "Sojourn Access: Pay L$10 to ...",
    "reply to ...: the text"), answered by face, fingerprint or PIN; negative answers and choices then take any input.
    Why not the typed check (`confirm_text`): a program that can type can read the word on screen and type it too. The
    trade-off is in `docs/access.md` > Answers without a physical key.
  - Out of scope: software running as administrator or with UI Access (it can drive the app as the person can), and
    a virtual keyboard or mouse driver the person installed (Windows calls its input hardware).
  - The VR client's `RealKeys` and `RealInput` take only hardware keys and clicks too, and its reader keys
    (`Access/ReaderKeys.cs`) a fresh press made while the window had the focus (a key held when it came to the front
    doesn't answer until pressed again).
  - For tests and debugging: `SOJOURN_TEST_INPUT=1` lets tests use posted keys (every rule above is off then); `=keys`
    judges posted keys and clicks as the hand's with every other rule on, and posted characters as typed.
    `SOJOURN_INPUT_LOG=path` logs each key's judgement and each focus or selection change with who made it, for a key
    refused in a real run. `tools/inputcheck` checks the judge (`docs/access-testing.md` > Input checks).
- **The window's thread held** (`WindowWatch`, started by `Program`): a frozen window is silent to someone who can't see
  it, and their keys go nowhere. A thread of its own posts a message to the window's thread four times a second (once
  the last was answered), which stamps the time when handled; the stamp stands still while the thread is held, or kept
  from input by a flood of posted messages (not a sent ping: a wait inside a COM call still answers those; not a
  WinForms timer: its ticks starve under posted messages, and a COM wait can dispatch them). Held 1 s or more is
  logged when it ends ("window: its thread was held 2.3 s"), for a tester's log; at 5 s it's said ("The window isn't
  answering. Waiting for it.") and "The window is back." when it is; from 30 s, each minute, it offers Control+Shift+Q,
  registered as a hotkey only while the thread is held, which logs out if it can (3 s at most) and starts the app again
  at the login window, the form filled in. NVDA hears it through its API; for another reader the announcer's
  notification would wait on the held thread, so Windows' own voice (SAPI) says it, only with a reader running. The
  test's `//holdwindow SECONDS` (with `SOJOURN_TEST_HOLD_WINDOW=1`) holds the thread; Control+Shift+Q and the SAPI
  voice aren't in the scripted test (a hotkey takes real keys). What the thread was doing (2026-11-11, after a tester's
  13 s with nothing around it in the log, and 16 holds of 1-5 s in her logs since 11-08, most of them while starting): at 2 s
  held, and again at 10 s, its stack goes to the log, innermost first ("window: held 2.1 s, its thread in ...: Thread.Sleep <
  Commands+<RunAsync>.MoveNext < ... < Program.Main"), read on a thread of its own from a snapshot of the process
  (ClrMD's `DataTarget.CreateSnapshotAndAttach`; the first read takes about 2 s here, loading the runtime's reader, later
  ones 0.1 s), at most 12 a session. Managed frames only: a wait inside a native call shows as the call into it. The
  runtime's reader is the package's `mscordaccore.dll` or, from source, the installed runtime's; never fetched from
  Microsoft's symbol server, ClrMD's last resort, so a package without it logs that instead.
- **An exception escaping on the window's thread** goes to the session log ("access: unhandled on the window's thread")
  and is said, at most once a minute, the app carrying on (`Program`: WinForms' own exception dialog would take the
  focus and sit on a Details button).
- **Objects' refreshes** (`RefreshObjects`): one asked for while another waits up to 3 s for names (Show changed to
  your HUDs just after Alt+7, F5) runs once that ends, and a list the person is in never refreshes by itself.
- **Big inventories** (for a tester with thousands of items, a fishing game's catch; `MainForm.Inventory.cs`):
  - A search or Show's list (worn, recent, trash) walks the inventory off the window's thread (`FindFlat`), reading the
    store as Sojourn.Core's walks do (LibreMetaverse's `GetContents` copies a folder's contents under that folder's
    lock; lookups are its concurrent dictionary); what's worn and the Trash's id are read on the window's thread first.
    A newer search cancels the older (each pane its own). One done within 0.1 s shows at once; a slower one says
    "Searching." at 0.5 s if the person asked, and when it lands the focus goes to the tree only if it's still in
    Search. Searches leave the Trash out and count its matches; Show: Trash walks only the Trash. Each list logs how
    many nodes it looked at and how long the walk and the list took ("inventory search: 59512 looked at, 500 listed
    ... walked in 8 ms, shown in 57 ms"). Landmarks (Places) and `//route`'s and `//tp`'s notecard and landmark
    searches collect off the window's thread too.
  - The walk is never the slow part (8-10 ms for 59,500 items); the tree view is: inserting costs ~45 us a node and
    emptying about as much. So a folder seen for the first time goes in 500 nodes at once (and as far as the one that
    stays selected), the rest 500 at a time from a WinForms timer, whose ticks come after waiting keys (`FillRest`); a
    folder shown already is brought up to date in place (`Reconcile`: only what went or came is removed or inserted,
    renamed ones moved, the words renewed, filled subfolders likewise), so a refresh after an action or F5 never
    empties and refills it. If the selected node went, its folder takes the selection (the top of the tree, at the
    top).
  - With UI Automation, emptying costs more: WinForms disconnects each item's UIA provider (made for an item once a
    client asks for it, and for each item selected once any client has asked for the tree) one by one. Each disconnect
    walks the UIA server's list of references its clients hold; a client that releases its elements (a screen reader)
    keeps that list short, but the test's QuireUia leaves references behind, and in a long run they made it seconds,
    then minutes (F5 with 4,500 open once took 12 s; `docs/access-testing.md` > The test client's references). NVDA
    reads the tree by MSAA (`ReaderTree`) anyway.
  - Measured by `python tools/invbench.py` (Inventory Big, 19,500 items, and Inventory Huge, 58,500, synthetic IARs
    loaded once; run on the hidden desktop with QuireUia's `stalls`: the window pinged every 20 ms, the longest it went
    without answering; a wait inside a COM call, as the UIA disconnects, still answers pings, so the app's own log says
    those). Huge, 2026-10-30: 500 found 31 ms, opening the 4,500-fish folder 54 ms (229 before the batching), F5 with
    it open 30 ms (344).
- **Enter in one-line inputs** (`LineBox`) is handled before Windows makes a character of it, so the edit control never
  gets the carriage return it answers with the default beep.
- **Read-out** is A1's `AccessWiring` in the session, with two seams added for this app: its own UIA voice and
  greeting (`SessionHost.StartAllAsync(args, hwnd, voice, greeting)`), and `AccessWiring.Shape`, the app's say over
  each line (mutes, the source named or not, "new IM").
- **Settings**: `access_mute_all`, `access_timestamps`, `access_muted_sources`, `access_answers_without_key` (above),
  `access_view_rate` and `access_view_size` (the renderer out of sight's idle frames a second and their size),
  `SessionDefaults` (which of Access's own defaults for the session's settings are in place: the quieter music and
  world sounds), walk mode's `walk_*` (each cue's channels and level, the turn step and rate, the teleport fallback)
  on SojournSettings (section "access", so the assistant can change them when asked), kept in access-settings.json
  (`Walk`, only what differs from the defaults); the reader's own (`screen_reader`, `announce_*`) are A1's. Nothing
  touches the VR client's menu.
- **The assistant's walking** (`LLAgent startAutoPilot`, the session's client-side autopilot) goes round what's in the
  way (Paths, above, through `Motion.Router`), and steers here by the avatar dead-reckoned between the region's updates
  (`Motion.DeadReckon`, set by this app only; the VR client's walks are as they were): steering by the position last
  heard, it ran up to 2.3 m past the target; dead-reckoned it ends 1.1-1.2 m short with its 1.5 m stop distance.
  `SOJOURN_DEAD_RECKON=0` turns it off, for comparing. `PersonOps.Me()` (bearings, `//status`) reads the same position.
- **Numbers into ops are doubles** (or ints): `Json.AsDouble` reads a `float` JsonValue as nothing, so a position
  built from `Vector3` floats became 0, 0, 0 (a rez landed in the region's corner, and `//go tp` went there too).
  `MainForm.Xyz` makes positions.
- **Local teleports reported failed**: LibreMetaverse answered some teleports within the region as failed
  ("Teleport started" or "Teleport finished") as they arrived, and raised no Finished for them, or a Start after it.
  The cause (found 2026-11-09): the region answers with TeleportStart and TeleportLocal together (Second Life too),
  and LibreMetaverse runs its handler for each on the thread pool, where the two interleave or swap (13 of 40 reported
  failed on the local grid, 35 Finished for the 40; with the cure in, the Start still reached the handler after the
  Local 17 times of 40). Whatever goes by
  those events was wrong until the next teleport, in the VR client as well: the history, LLTeleport and so
  "Arrived" for a teleport she didn't ask for, controls a script had taken, "until teleport" derenders, auto-sit's
  note of where a teleport set out, and after a late Start world sounds off, voice hidden and music ducked.
  `TeleportWatch.WireInTurn` (Core) now runs that handler one message at a time and leaves out a Start that comes
  after its end (40 of 40 arrived and Finished once). What was built around it before stays as a net should a
  LibreMetaverse upgrade drop the handler's name: `AgentSession.TeleportAsync` counts a teleport arrived when the
  agent is at the place asked within 3 s, `Commands.TeleportLandmarkAsync` counts being where the landmark points, and
  both tell the history themselves (`Places.TeleportArrived`; a place told twice is kept once). The teleport history
  (`Places.Arrived`, Core) keeps where a teleport within the region landed, even when it came before the parcel's name.
- **The renderer out of sight** (`docs/access.md` > M0b; `Sojourn.Session/Offscreen/`): started after the session
  (`SessionHost.StartOffscreen`; `--no-render` leaves it and the session's render hooks out). It draws from the
  avatar's eyes on a thread of its own, two small frames a second turning around the avatar, more while the assistant
  works, and a picture at its own size when asked: the assistant's `look` (`LLViewerWindow saveSnapshot`) gets a real
  one, and the person's view is the avatar's eyes, a little down. The assistant's touches land where the VR client's
  would (on the surface the eyes' ray meets; with `face`, that face's middle, the far side too), still behind the
  person's confirmation for others' things. `SojournView` reports its state and casts its own collision model's rays
  (`collide`, for comparing: walk mode's cues and paths come from the model without a GPU, below). A lost GPU restarts
  it within the app in 3 s. Its log lines are `render`'s in the session log. Its geometry arenas are a quarter of the
  client's (`GeometryArenaScale`, 72 MB each, more as needed).
  **Unseen**: while nothing can look at its pictures it draws only geometry, which is all the picks, faces, the
  collision model, rays, physics shapes and paths use. No textures but sculpt maps (`TextureStore.DataOnly` through
  `RenderQuality.Textures`: nothing else fetched, decoded or uploaded, what was resident let go within seconds); no
  avatars or what they wear (`FrameRequest.NoAvatars`; the collision model's standing avatars are boxes from the world
  model, and Objects lists no attachments); no shadows, reflection probes, particles, flexible prims' sway or avatar
  physics. The draw distance stays the person's (halving it saved only ~50 MB of memory and no video memory:
  `UnseenDrawDistance`, `SOJOURN_OFFSCREEN_UNSEEN_DISTANCE=M` for comparing). Nothing can look while no assistant is up
  or starting (`AssistantHost.SidecarAlive`, or the sidecar's process running: a `--lean` package, a repo run with
  `--no-assistant` or without the venv) and no picture was asked for in the last 10 minutes (`PicturesLinger`). A
  picture asked for (the snapshot ops, SojournDescribe's, the assistant's look) or the assistant coming up draws it
  whole again at once; what's in view loads from then on, and the picture waits its usual settling (at most 3 s, so on
  Second Life the first one after an unseen spell may still lack some textures). `SojournView status` has `unseen` and
  `draw_distance`; the log says when it changes. What it saves: at Firestorm Social Island, 1.7 GB of memory and 1.9
  GB of video memory drawn whole (1.2 GB of it textures, fetched at 2-3 MB/s for pictures nothing could look at),
  965 MB and 312 MB unseen (2026-10-29/30). On the perf grid (`forestcrowd`, Atelier 128,140 by the dancing crowd, no
  assistant, three minutes from a cold cache; whole / unseen): private bytes 1.50 / 0.80 GB, video memory 0.90 / 0.32
  GB, CPU 3% / 2% of a core, a frame 2.1-2.2 / 0.4 ms of GPU. A packaged build idle on the light local grid: 546 MB in
  memory and 328 MB of video memory.
  **On the processor** (Claude Opus 5.5, 2026-10-31, for the blind tester's PC: a Ryzen 5 2400G's Vega 11 and an RX 550
  on a 2019 OEM driver offering Vulkan 1.1, so no pictures and descriptions from the lists alone): when no GPU can run
  it (`NoUsableGpuException`: no driver, none with Vulkan 1.3, or a missing feature at vkCreateDevice), or one failed
  three times in ten minutes, it starts again at once on Mesa's lavapipe, a conformant Vulkan 1.4 driver on LLVM,
  from the package's `lavapipe` folder (Render's `Gpu/SoftwareVulkan.cs`). Nothing is installed: the Vulkan loader is
  pointed at its manifest for this process only (`VK_DRIVER_FILES`, and `VK_ICD_FILENAMES` for loaders before
  1.3.207), only while the instance is made, the loader's driver filters cleared for it, so no hardware driver loads
  beside it and nothing the app starts inherits it. `SOJOURN_SOFTWARE_VULKAN` names another copy (repo runs);
  `SOJOURN_OFFSCREEN_SOFTWARE=1` uses it from the start; `SojournView status` has `software`. On it:
  - no frames between pictures and none while the assistant works (`idle_hz` 0): an idle Access stays light (4-10% of
    a core, mostly the session itself). Picks, faces and touches glance at what isn't drawn yet, and a glance is only
    planned (`FrameRequest.PlanOnly`: what's in view asked for, counted and uploaded, nothing drawn; a prim's
    triangles are kept as it's built); the renderer's own collision rays know only what's been drawn (walk mode never
    uses them).
  - a picture settles on planned frames, then is the one frame drawn (the GPU draws every settling frame); settling
    twice the GPU's (6 s, or 2 s without progress), since the picture costs more than the wait.
  - **compiling** (Claude Opus 5.5, 2026-11-01): the driver compiles each pipeline's shaders as it first draws them, and
    each texture's sampling as code of its own for its size and mip count the first time a shader samples it (under one
    lock, so on one core), and keeps none of it between sessions. On the blind tester's 2400G that was nearly all of a
    session's first frame: 189 s, then ~1 s a frame (her log of 2026-11-01 evening; the fullbright and alpha passes
    dearest). Nothing can carry it over as built: lavapipe's VkPipelineCache stores only a 32-byte header
    (`lvp_pipeline_cache.c`), and Mesa's disk shader cache, which llvmpipe would use for exactly this code, isn't built
    on Windows (meson.build: "Shader Cache does not currently work on Windows"; the DLL has no `MESA_SHADER_CACHE_DIR`),
    both checked against Mesa 26.2.5's source; so the app keeps no pipeline cache of its own either (the GPU drivers
    keep their own). What makes it cheaper instead:
    - LLVM's optimiser off (`GALLIVM_PERF=nopt`, set by `SoftwareVulkan.Use` while the device is made;
      `SOJOURN_SOFTWARE_OPT=1` keeps it): compiling 2.3 times quicker, pictures once compiled no slower (the rasteriser
      and memory traffic are most of a frame).
    - textures go up square at 64, 256 or 512 (`TextureStore.FewSizes`, `MaxDimension` 512; the pixels kept for sculpt
      maps stay as they came; `SOJOURN_SOFTWARE_TEXTURES=as-is` for comparing), so Second Life's dozens of texture
      shapes compile three ways of sampling, not one each (reasoned: our grids' textures come in few sizes, so it
      measured as no change here; pictures look the same); no anisotropic filtering (several times the code and the
      work, for faces seen at a slant).
    - no shadows (`SOJOURN_SOFTWARE_SHADOWS=1` keeps them): half of each warm frame on a busy place, and their passes'
      compiling; a description wants colours, shapes, text and looks, which sunlight without shade shows as well.
    - a warm-up in two steps (`WarmFirst`, `WarmUp`), at below normal priority like the rest of its work: 2 s after
      it's up, once something can look at its pictures, one frame ahead drawn at once with whatever's there (the sky,
      ground, water, the default probe and the lighting passes, compiled whatever the place); 20 s after, what's around
      drawn once, settled first, the four ways round from the eyes and the person from their front at a picture's
      size, so a first "describe me" or "where am I" finds it compiled. A new place's new materials still cost a few
      seconds the first time.
  - **waiting**: while it's still preparing (`OffscreenRenderer.Preparing`: on the processor, not through its warm-up),
    a description's pictures are waited for up to five minutes, two after (a GPU's: twice the settling and 20 s); the
    assistant's look the same (`LiveSnapshots.Patience`; 8 s on a GPU), its HUD pictures too; the sidecar's picture
    calls wait 330 s (`api.PICTURE_WAIT`), leaving the bound to the app. Control+D while it's preparing says so after
    "Describing you.": "Still preparing the view: the first one after logging in takes longer on this PC." So a first
    description waits for its pictures rather than answering from the lists after 26 s ("I can't make out your look"),
    as it did twice for her.
  - reflection probes but the default one, no SMAA and no sharpening (without even the default probe, a shiny plaza
    took the sky's blue and lost its colours); the load governor's frame side off (it's off for every out-of-sight
    renderer: nobody watches its frame rate, and a processor's frames are seconds, which held loading back to a
    crawl); video memory is the PC's own, so it's budgeted from our allocations at 1 GB (`VulkanSetup.SoftwareBudget`):
    textures get 512 MB beyond everything else allocated before they're shed (`FrameRenderer`'s governor call). Judged
    against the whole 1 GB, the rest alone (geometry arenas, targets, probes: 0.4-0.5 GB at the blind tester's club)
    kept textures held and shrunk at the top pressure with almost none resident, so pictures were blurred and sharper
    copies dropped (her logs of 2026-11-01: pressure 5-10, textures 0.0 GB).
  - the driver's threads (one rasteriser per logical processor and its queue's) are put below normal priority, as the
    view's own thread is, found by their start address inside `vulkan_lvp.dll`, so the screen reader and the window
    come first.
  - each picture logs its size, settling and drawing time ("on the processor: ... drawn in N ms"), the warm-up its two
    steps, each with how busy the whole processor was meanwhile and this app's share of it (`ProcessorShare`), so a
    tester's log says what it costs on her PC and whether a slow one was compiling or waiting its turn.
  - between pictures (on a GPU too, between its idle frames), four times a second, a region drawn before is kept as a
    frame keeps it (`FrameRenderer.Tick`: its changes applied, its effects followed change by change; its land left
    for the next frame), and one never drawn isn't indexed at all, what it reports let go, since its first frame reads
    it whole (it had queued every update until the next picture, and its effects were rescanned five times a second);
    mesh bytes held to 64 MB (`MeshBytesBudget`; the client's 256); each region's sky asked again once a minute (the
    client's 10 s). Memory over a long ride: `docs/performance.md` > Sojourn Access on a small PC.
  Measured on the local grid at Atelier's plaza, the app held to 4 cores and 8 threads of this PC's Ryzen 5 9600X
  (`LP_NUM_THREADS=8`; 2026-10-31, before the compiling changes): a picture 0.1-0.25 s at 640-1280 px once warm;
  Control+D on the place 11.8 s to the spoken reply (Haiku 8.1 s of it; on the GPU 9.3 s, 5.5 s the model's), on
  herself 8.6 s (7.7); memory 0.8 GB idle, 1.1-1.3 GB after pictures (the GPU path: ~0.55 GB plus its video memory).
  The first picture after login, before and after the compiling changes: `docs/performance.md` > Sojourn Access on a
  small PC > The first picture.
  Pictures look as the GPU's do, the probes and antialiasing aside (the cost lab's captures side by side). It found a
  bug of the renderer's own: a tick (the world kept current without drawing) staged uploads and a shrunk texture's
  copy, then counted as a frame for freeing what was retired, so after a few ticks a waiting copy named a destroyed
  image; lavapipe crashed on it (an access violation in its copy commands, twice in a dozen sessions). `Tick` now sends
  what it staged at once. The VR client never uses the software driver.
- **HUDs out of sight** (Claude Opus 5.5, 2026-11-01; `Offscreen/OffscreenRenderer.Hud.cs`, Session's `Hud/`, Render's
  `FrameRenderer.RenderHud`): the person's HUDs on a 16:9 screen (the shape most are laid out on; the VR client's
  headset board is the same), never drawn between pictures. A pick, a face list or a press places them and builds
  their draw list with no view of the world, from the region's scene read first (`RenderHud` syncs it: a scene is
  otherwise read only by a frame drawing it, and on the processor there are none between pictures, so a region come
  since the last picture, every border crossed with no ride recording or description, had no HUDs in it, and her
  GTFO HUD was "Nothing called gtfo" for over a minute after borders, 2026-11-11; a press finding none logs what the
  region says she wears on HUD points; planned, nothing drawn: no GPU work, unseen too, on the processor a few
  milliseconds of one core), waiting up to 2.5 s for a mesh HUD's shape; a picture (SojournHud `look`, describing a HUD,
  reading its buttons) makes the view whole as any picture does, settles until its textures are in (at most the
  settling time), then draws the HUDs alone, as the VR client's HUD pass does (`hud.frag`, fullbright, the best LOD),
  read back over a dark grey. On the processor (her PC's path) a HUD picture at 1024x576 drew in 134 ms the first time
  (its shaders compiled) and 2-22 ms after, on top of the job's wait. Touches: `HudTouch` routes requestTouch and its
  faces on the person's HUD prims to the HUD screen (the part's middle as it shows, or a face's), everything else to
  the world's (`SessionHost.StartOffscreen`). A face's list comes from a grid of picks across the part (32 along its
  long side, `HudScan`): what a click there would hit, so a side that never shows isn't listed and a part covered by
  another is pressed where it shows. Buttons read are kept by `HudButtons` per look (a hash of the parts' shapes,
  places, rotation and face textures with offsets and repeats; colours and glow left out, since buttons light up), each
  as its middle and size across the HUD's box, and pressed there on the HUD's own prims; `save_buttons` drops a button
  whose middle isn't on the HUD. SojournHud (shared with the VR client: `docs/hud.md`) is registered here too, so the
  assistant's `hud` tool looks at and presses her HUDs ("press the brake": by position on a look, or by a name read).
- **The collision model** (Walk mode > The collision model, above): Render's `Collision/CollisionWorld.cs` (every
  region the session's world knows, each prim placed as the region reports changes, `RegionWorld.Changed`, in 16 m
  columns, 256 a query checked against the world as a safety net, a full rescan when the counts disagree; rays and solid
  triangles in a box, under one lock) and `CollisionShapes.cs` (shapes by key: prims built on the spot, the rest by a
  queue, eight downloads at once, nearest first, decoding on one or two threads; what's within reach is asked for each
  second, so the rest is what's been left behind, let go after three idle minutes), by
  `CollisionRule` and `CollideMath` (`World/PhysicsShape.cs`), which `RegionScene.CollideRay` (the renderer's, the VR
  client's walk) uses too. Session's `Collision/LocalCollision.cs` wraps it: the physics shape types (`PhysicsShapes`,
  shared with the renderer out of sight), the passable rule (`PhysicsShapes.Collide`), rays off the caller's thread,
  and a loop keeping the shapes within reach ready; started by `SessionHost.StartCollision` (Access only, before the
  renderer). `SojournCollision status` (prims, shapes ready, waiting, downloading, failed, MB) and `collide`. Its log
  lines are `collision`'s. Cost (2026-10-30, local grid, Atelier East with the walk course, 592 prims, 47 shapes): a
  sonar's 720 rays 3-3.5 ms on one core, a walking probe's 8 under 0.3 ms; the shapes 3.0 MB (`shapes_mb`; 3.5 at
  LOD 3). Every five minutes, if it changed, the log says what it holds (`collision: R regions, P prims, S shapes (M MB),
  T physics types known`), so a tester's log shows it on a busy Second Life region; physics types answered over 20
  minutes ago are let go (they were kept for the session: 75,000 on a three-hour train ride).
- **Walk mode** (`Walk/`): `WalkMode.cs` (the body as it is now, footsteps, the listener), `.Keys.cs` (keys to control
  flags, turning, braking, driving), `.Probe.cs` (ten batches of rays a second while moving: ahead, both sides,
  underfoot, down a step and a half ahead; the warnings, openings, edges, surface), `.Info.cs` (H, V, L, A, S, T, D,
  and a seated crossing said), `.Distance.cs` (the distance walked, M), `Sonar.cs` (the ring's words and openings,
  edges refined by sub-rays: pure geometry), `CueSounds.cs` (the sounds, made in code), `PathPlanner.cs`,
  `WalkPad.cs`, `WalkApi.cs` (SojournWalk: status, sweep). It runs on the window's 50 ms tick; rays go to the
  collision model in one batch (`LocalCollision.CollideManyAsync`, the seat's own linkset left out) and come back to
  the window. Positions are dead-reckoned (the region sends a walking avatar's position only now and then); when the
  region's word on the velocity says still while the position moves (a seat moved by llSetPos), the velocity comes
  from the positions. `SOJOURN_WALK_LOG=path` logs every probe and plan.
- **The listener**: Access has no camera, and world audio places nothing until something sets its listener
  (WorldSounds waits for one: objects' sounds, footsteps, even the new-IM sound). Walk mode's tick puts it at the
  avatar's head facing where it faces, for world audio and voice.
- **Dry under the sea** (`Walk/WalkMode.Roof.cs`, `AudioWiring.Roof`; 2026-11-02, after the blind tester's sounds went
  faint riding a glass tunnel under the sea): the viewer's underwater audio (sounds fading five times as fast with
  distance) goes by the ear's height against the region's water alone, so a dry tunnel or seabed room is "under water"
  too. In Access, under the water, one ray straight up from the ear to the surface by the collision model; anything
  solid it meets (the vehicle she rides too: a submarine's hull) makes it dry, at the usual falloff. Asked again once
  the ear has moved a metre or a second has passed, at most five times a second, one at a time; the last answer
  stands meanwhile (a first moment under the water, before the ray answers, is as the viewer's). A phantom roof
  doesn't count (the collision model passes through phantom prims). Logged with the heights ("audio: under the water
  (the ear at 4.2 m, the region's water at 20.0 m) beneath a roof 1.7 m up: dry, ..."); SojournAudio status's `water`
  says which. The VR client stays as the viewer.
- **Sound as a map** (M4): `Sound/Earcons.cs` (classifies feed entries by kind, the notice's notification name on
  `FeedEntry.NoticeName`; placed toward the speaker by `WalkMode.Toward`), `Walk/WalkMode.Sound.cs` (the beacon:
  `Commands.Heading` for its own walks and follows, else `Motion.Destination` for the session's autopilot, else the
  hand beacon; the marks), the sounds in `Walk/CueSounds.cs` (someone's own IM sounds in `CueSounds.Own.cs`, chosen
  through `Sound/ImSounds.cs`). Every Access sound plays as world audio's `App` kind,
  whose level is `WorldSounds.AppVolume` (`access_sounds_volume`) instead of the world's sounds volume. Access turns off
  the viewer's new-conversation sound (`AudioWiring.ConversationSound`) and alert sound (`SocialWiring.AppAlertSound`).
  `SojournWalk status` has `recent` (the last 60 cues: name, ms ago, level, bearing clockwise from facing and distance
  from the head when placed), `beacon` and `marks`.
- **What Access added to Session, Core and Render**, each doing nothing in the VR client, which doesn't use them:
  - For walking (M3): Core's `Motion.Router` (paths for the session's autopilot: the waypoints an app plans, each
    passed within 0.5 m; null in the VR client) and `RetargetAutoPilot` (a moving target), `StartAutoPilot(...,
    route: false)`; Session's `SoundBank.AddLocal` (a client's own sounds, never fetched or let go),
    `AudioWiring.Bank` and `OwnFootsteps` (the viewer's own footsteps for the own avatar off while walk mode plays its
    own by surface), a log line for a frame over half a second.
  - For sounds (M4): world audio's `App` kind, `FeedEntry.NoticeName`, `AudioWiring.ConversationSound`,
    `SocialWiring.AppAlertSound`.
  - For parts and faces (M2): Render's `RegionScene.FaceList` / `FrameRenderer.FaceList` (a prim's faces as drawn),
    `OffscreenRenderer.FacesAsync`, and `FindTouchAsync` counting a child prim given alone first (only the renderer
    out of sight's; the VR client's touches are DirectTouch's).
  - For gestures and the outfit (2026-10-31), which the VR client gets too: `GestureOps`' own player, `Trigger` and
    `WarmTriggers`, `LLGesture` playing any gesture of mine, `Look` making the login's active gestures active;
    `TesselInventory link` (Paste as link; the test's outfit link); `AgentInventory.IsAttached`, so `TesselAgent
    attach` wears again an object in the outfit that isn't on (it refused it as "already worn").
  - For uploading (M6, 2026-11-01), which the VR client's assistant gets too: `UploadOps.LookAsync` (`TesselAgent
    uploadLook`), plain-worded refusals, the viewer's biased picture sizes, TGA (`Core/Media/Tga.cs`), fees by kind and
    size from the account's benefits, `max_cost` held to, `uploadMesh`'s `lods` and `physics`, OBJ models;
    Session's `Confirmations.Uploads.cs` (the fee worked out, pinned and named before the person is asked; a model's
    by the grid's quote). `SOJOURN_TEST_UPLOAD_FEE` ("texture=0,mesh=0,animation=500,clip=10,balance=100") pretends
    fees on a free grid for the test: `clip` a sound clip's fee apart from a single sound's, `balance` the L$ balance
    uploads are checked against and said with (`UploadOps.Balance`; the local grid's is 0). `uploadClips` and
    `uploadLook`'s `clips` (a sound of any length cut as `Media/SoundClips.cs` plans; one `max_cost` for the set).
  - For describing (M5): `OffscreenRenderer.ShootAsync` (`OffscreenRenderer.Shots.cs`: shots planned on the render
    thread, each drawn until loaded, at most 3 s, 4 rich, its pixels waited for, then the 16 x 9 grid of picks),
    `DescribeApi` registered by `SessionHost.StartDescribe` (Access only), `DescribeApi.MovingPictureAsync` and
    `ShotSpec.Quick` (a ride's pictures: from the eyes along the way it goes, JPEG, waiting for no one's look),
    `SessionHost.App` / `AssistantHost.App`
    ("access", in the sidecar's hello, so its prompt and context line say Sojourn Access and the describe tool is
    offered; null in the VR client, whose prompt and tools are as before).
  - For what a friend points at and one person's things (2026-11-08), which the VR client gets too and doesn't use: Core's
    `Agents/Pointing.cs` (others' selection effects, each person's last; the person's own Point at, sent; `AgentSession.Pointing`), `SojournPeople
    pointedAt`, `TesselObjects nearby`'s `worn_by`.
  - For HUDs out of sight (2026-11-01): Render's `FrameRenderer.RenderHud` (the HUD pass with no world view;
    `PlanHud` can build without drawing); SojournHud moved from the VR client into Session (`Hud/HudApi.cs` over an
    `IHudScreen`, the client's `HudView` one of them) with `faces`, `buttons`, `save_buttons`, `forget_buttons` and
    presses by face or button, which the VR client's assistant gets too; the `hud` tool's `save_buttons`, and the
    sidecar's reading (`describer.read_buttons`, brain.py's describe key with `of=buttons`).
  - For export and import (2026-11-09): Core's `Backups.ExportAsync` (a backup in memory, the prims as copies with others'
    textures plywood, her textures' J2C, what was left out; `backup` writes the same), `Exported.RadegastXml`, a restore
    reading Radegast's XML export (`Backups.Read`); Session's `Building/Collada.cs` (an export as a Collada model). None
    takes a path from the agent API: Access writes where the person chose.
  - For auto-sit (2026-11-09): nothing outside Access (`PersonOps.SetUpAsync` is Access's own).

## Testing

`python tools/access.py test` (the scripted test on a hidden desktop), `tools/inputcheck`, `tools/namecheck`, `package --check`, a real
NVDA run, and the guided tests with the user or a tester: `docs/access-testing.md`.
