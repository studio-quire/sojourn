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
own bin and obj are untouched; staged in `local/dist/stage/Sojourn Access 0.1.0.N/`.

- **What's in it** (0.1.0.583 with the assistant, 2026-10-30: 274 MB zipped, 641 MB unpacked, 3528 files; lean about
  87 MB zipped): `SojournAccess.exe`, self-contained single-file (the .NET 10 runtime, WinForms and ASP.NET's Kestrel
  inside; our pdbs embedded, so stack traces have lines); beside it the natives it loads by name: `sojourn_voice.dll`
  (voice and world audio, from `local/voice`), `openjp2.dll`, `shaderc_shared.dll` and `libSkiaSharp.dll` (the
  renderer), `nvdaControllerClient.dll` (from `local/nvda`); the folders `Shaders`, `Character`, `Textures`, `Fonts`,
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
- **Providers when packaged**: an Anthropic key, OpenRouter and the studio relay only, in Alt+0's first rows (no
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
Username, Password, Show password (a check box), Start at (a plain list: my last location, home, a place given, or
"Another place", which opens a Place box), Log in. The focus starts where you'd type next. Progress and failures are
said ("That username and password don't match."); the focus goes back to Password. The grid's Terms of Service and an
MFA code come in their own small windows, answered only by the person's own key or click. On arrival the main window
opens with the focus in the Say box and "Logged in, in Atelier. F1 for keys." is said. Another account after a failed
one restarts the app with the form filled in (each account's state is its own).

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
| Alt+4 | Inventory | the tree ("Red Dress, skirt, worn"; "Hair, object, worn on Skull"; "..., marked"), Search, and Show (everything, worn, recent, trash) |
| Alt+5 | Search | Search for (words), Look for (people, places, events, groups, classifieds), Results |
| Alt+6 | Places | Landmarks (a tree: the Landmarks folder and Favorites), Teleport history ("Bay City, Harbour, today 14:05"), Go to region (a box), Regions around ("Bay City, 3 people, next door to the north") |
| Alt+7 | Objects nearby | "Notices Harp, 3 metres, eleven o'clock, touchable" (or ", can sit"), nearest first, within 30 m (Show: what you wear instead, Alt+Shift+7); Tab: the selected one's parts, then a part's faces (Parts and faces, below) |
| Alt+8 | Notices | what waits for an answer, newest first; the selected one's text, keys and reply box; Escape goes back where Control+N came from |
| Alt+9 | Assistant | a typed request ("Ask the assistant"), its replies, its choices as buttons |
| Alt+0 | Settings | first the assistant's provider and whether it works, its key or token row and OpenRouter's model (The assistant, below); then the ones that matter here (read-out and its kinds, mutes, timestamps, arrivals, earcons and volumes, the sound output and microphone, walk mode's cues). Each row leads with what it's about, so first-letter navigation finds it ("IM sound", "Footsteps (walk mode)", "Nearby chat read out"; no two rows start with the same three words); Enter, Space or Right moves one to its next value, Shift with them or Left to the one before (a sound's row plays it; Enter on the sound output or microphone opens the list of devices) |

Pressing the Alt+number of the area you're in reads it again ("Inventory: Red Dress, skirt, worn.").

### The menu bar

`MainForm.Menu.cs`; Alt or F10, then the arrows, or Alt+W, V, R, B, H for a menu. It's there for finding out what exists,
as Radegast's menus are. World (teleport home, to the SLurl on the clipboard, set home here, landmark here, sit on the
ground or stand up, fly or stop flying, walk mode, Recording a route (its keys and Discard), Gestures: the active ones to play, Away and Do not disturb as checked
items, incognito, region info, parcels in this region, status, what you're wearing, describe where you are, describe
me, who's in voice, open mic or push to talk, the sound out of the next device), View (the areas by their Alt+numbers,
Friends among them, the second inventory pane, the newest notice, Conversations, the conversation ring, closing one),
Read-out (mute all, mute this conversation, times, arrivals, Recent messages: the last nine as items with their
Control+numbers, then back, forward, oldest, newest, mark, copy, copy the assistant's last answer), Build (upload a
file, Control+U: the official viewer's Build menu, where building by hand is to come) and Help (keys, the slash
commands, send a log, message the builders, about).

- Each item shows its key beside it (`ShortcutKeyDisplayString`, which UI Automation gives screen readers as the item's
  accelerator key); the keys themselves stay in `ProcessCmdKey`, never the items' `ShortcutKeys`.
- Every item runs what its key, `//` command or op already does (Away and Do not disturb through SojournSettings'
  `away` and `do_not_disturb`, set home by `TesselAgent setHome`, a landmark by `TesselInventory createLandmark`), as
  the person's own press: items run as they're pressed, and those that move the focus do so once the menu has closed
  and given it back (`Later`, as our own move for the person).
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

- In any input: Enter says (or sends), Shift+Enter whispers, Ctrl+Enter shouts (nearby only); Ctrl+Up/Down bring back
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
  Tango, 3 Block, Delete Ignore."), since the blind tester found no way to close a dance machine's menu (2026-10-31).
  An answered menu leaves the list; one its object sends again (a dance machine's, after each pick) is picked by
  itself, and replaces any earlier one from that object, as in the viewer. The Applications key (or Shift+F10) on a
  notice: its keys as a menu, each with the key that presses it ("Green, 2"), then Reply... and Ignore (Delete); a
  confirmation's Confirm and Cancel. Escape anywhere in the notices but on a confirmation (or in the reply box): back
  to the area and control Control+N came from, else to Say. Tab from the list reaches the notice's text, its reply box
  and its buttons ("1: Red", "Delete: Ignore").
- Applications key (or Shift+F10, or Enter) on a person nearby: Send IM, Walk to, Face, Where, Follow, Beacon,
  Profile, Pay..., Offer teleport, Request teleport, Always incognito with them. On a friend (Enter: an IM): Send IM,
  Profile, Offer or Request teleport, Pay..., End friendship (confirmed). On a group (Enter: its chat, a conversation
  tab): Group chat, About the group, Wear its tag, No group tag, Leave (the grid asks in Notices). A profile opens in
  its own window: a read-only text read line by line (name, born, partner, about, interests, picks, first life, web),
  Send IM, Pay, Give what you copied, Describe pictures, Close (Escape). Pay asks the amount, then waits for the
  person's confirmation in Notices (refused at once when it can't be afforded). Give what you copied (also on a person
  nearby and a friend) gives what Control+C or Control+X last took in the inventory, each thing confirmed: the blind
  tester's Radegast habit of copying an item and giving it from the person (2026-10-31), beside the inventory's Give
  to... On an object: Touch, Sit, Walk to, Beacon, Face, Describe, Where (with its floating
  text), Parts, Move..., Take into your inventory, Delete (the last three on your own: Placing by words, below; Delete,
  also the key, is confirmed in Notices and goes to the Trash); on something worn only Touch, Describe, Where, Parts.
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
  Copy, Paste, Rename, Details (rights, maker, date), Delete; on a folder Replace outfit, Add to outfit, Take off the
  folder's items, New folder, New notecard, Upload a file here (Control+U; Uploading, below); a notecard also Play as
  a route (`//route`).
  - A gesture's words say whether it's active, and its trigger ("Clap, gesture, active, /clap"); Enter plays it (any
    gesture, active or not), and its Applications key has Play, Activate and Deactivate (for all the marked ones).
  - Ctrl+X / Ctrl+C then Ctrl+V on a folder (or an item in it) move or copy; F2 renames.
  - Delete and Give ask first (the confirmation comes up selected in Notices: Enter confirms, Escape cancels, then the
    focus is back in the tree). As in the official viewer and Firestorm, a worn body part isn't deleted, said at once
    without asking, and other worn items, the folder's too, are taken off first, the confirmation saying so.
  - Ctrl+F goes to Search, where typing lists what matches flat with its folder ("2 found"), Enter goes back to the
    tree, Escape clears it; a search leaves the Trash out and says what it has there ("2 found, and 3 in the Trash
    (Show: Trash lists them)"); a long one says "Searching." first. Tab from Search reaches Show (Up/Down: everything,
    worn, recent: the last day's, trash: what's in the Trash, or with words what matches there).
  - F5 shows it again (the tree doesn't change under you otherwise; your actions refresh what they touched).
  - A notecard or script opens in its own window, an edit control: Ctrl+S saves ("Saved.", or a script's compile
    errors read out and listed under the text), Escape closes (asking first if changed).
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
  classifieds; events need no words: Enter lists what's on now); the focus goes to the results ("4 found."). Enter on
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
- Ctrl+Shift+T: times in the chat histories ("[14:05] Kestrel: hi", from the next line) or not.
- F3: walk mode (below); F3 or Escape again leaves it. R there, or `//record` anywhere, records a route by walking it
  (Walk mode > Recording a route).
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
  Shift+F4 push-to-talk or open mic; Ctrl+Shift+V who's in voice (Voice chat, below).

### People beside chat

`chatPeople`, a blind tester's Radegast habit (2026-10-30): Radegast's nearby list sits beside chat, Tab from its input
reaching it. In Alt+1, People nearby is to the right of the history and Say. Tab goes Say, the history, People
nearby, round to Say (her order, 2026-10-31; Radegast's goes Say, people, history), so Shift+Tab from Say lands on it
("People nearby, list, Kestrel Tester, 4 metres, two o'clock, 1 of 3", the person last on, else the nearest). It's Alt+3's list made by the same code (`PeopleList`): the same words, the
Applications key's actions (Enter too), Control+D, F5; within 96 m, nearest first, "Nobody nearby." when empty. It's
kept current every 3 s while the chat area shows and the focus is elsewhere (in Say, typing), so arriving in it the
distances are at most a few seconds old; a refresh whose words haven't changed leaves the list as it is (only where
people are, for Walk to and Face, is renewed), so nothing beside the Say box churns while she types.

### The status bar

A StatusStrip at the very foot of the window, below the walk pad, holding one label named "Location: Atelier 64, 36,
15, L$0" for the screen reader once in world (a label's name is all it reads of it): NVDA+End finds the status bar by
the window's bottom-left corner and reads its name and its children's, so "Status Location: ...".

### Read-out

- Nearby lines as "Kestrel: hello" (whispers, shouts, emotes worded so); a conversation's lines with its source
  ("Kestrel, IM: ...", "Kestrel, Builders: ...") unless it's the one you're in, where it's just "Kestrel: ..." (in walk
  mode, or with the window in the background, you're in none, so an IM never sounds like nearby chat); the first line
  of a new IM says "new IM".
- Notices in the feed's words ("Access Menu: pick one."; the first with keys in a session adds "Control N to
  answer."). A screen reader stops speaking at any key press or focus move, so a notice cut off that way before it
  could be heard out (within a second to three, by its length) is said again once, when the keys have rested 0.7 s,
  unless the focus went to the notices or the person silenced it with Control or Shift alone (the screen reader's own
  keys to stop or pause speech: `RealInput.SilencedAt`).
- Chat lines cut off by typing (`MainForm.CutLines.cs`): NVDA's "speech interrupt for typed characters" (on by
  default) cancels all its speech at every key typed into a box, and nothing we can send survives it
  (`speech.cancelSpeech` clears every priority's queue; braille messages go at the next caret move too). A blind
  tester relies on hearing chat while she types, and turning the setting off slowed NVDA for her (2026-10-30). So a
  line a key typed into an editable box fell within (by its estimated speaking time: 40 ms a character, 0.8-8 s,
  queued after the one before) is said again once the keys have rested 0.7 s, lines cut together as one ("Kestrel:
  hi. Access Visitor: hello."), each once, and not after Control alone.
- Her own lines (said, whispered, shouted, emotes, IMs) are read back in the same words as others' (Alt+0 "My own lines
  read back", `access_read_mine`, on by default, her wish): nearby once the region echoes them, so she hears they went
  out; IMs as they're sent (the grid echoes none).
- One-off notices (friends online, alerts, a teleport that failed) are also status lines in the nearby chat history,
  as in Radegast.
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
| `//go [tp] person NAME` / `object NAME` | to the nearest person whose name starts with NAME / object whose name has it |
| `//find person NAME` / `object NAME` (or just `//find NAME`) | "Kestrel is 4 m away, two o'clock, to the northeast, at your level." |
| `//face DEG` / `DIR` / `left`/`right`/`around` / `person NAME` / `object NAME` | turn; degrees are SL's (0 east, counter-clockwise), as Radegast's |
| `//sit NAME` / `//sit ground`, `//stand` (`//unsit`) | sit on the nearest thing with that name, on the ground; stand |
| `//follow NAME` / `//follow stop` / `//follow` | follow as they move (2.5 m behind where they'll be in a moment, around what's in the way), stop, or say who; said: when you're with them and they've stopped, when they sit, when they're getting away, when they're gone |
| `//tp list` / `N` / `NAME` / `Region/X/Y/Z` / an SLurl | landmarks in "Radegast Landmarks" (else Landmarks), by number or name; or a place |
| `//status [doing|region|parcel|location|money|time|music]` | any or all of them; music: what the parcel's stream is playing ("Playing: ...", in the whole status only when the parcel has music) |
| `//route NOTECARD` / `//route` / `//route stop` / `//route resume` | play a route: a notecard's `//` lines one at a time (with no name, the lines on the clipboard, as copied from a blog), each walk or teleport finished before the next ("Step 2 of 7."), other lines read as notes; a step that doesn't get there pauses it, `//route resume` tries that step again. In the inventory, a notecard's Applications key has Play as a route |
| `//record` / `note [WORDS]` / `undo` / `pause` (`resume`) / `save [NAME]` / `discard` | record a route by walking it: start, or while recording how it's going; a note here (no words: just a point); take back the last point or note; pause or go on; save it as a notecard in Routes (with no name, asked); throw it away (Walk mode > Recording a route) |
| `//pause [SECONDS]` | wait (in a route, before its next line; a second unless said) |
| `//siminfo` | the region: type, maturity, people (of the limit), active scripts, objects, time dilation, frame rates |
| `//parcelinfo` | every parcel in the region, the one you're on first: name, area, who may enter, description |
| `//scriptinfo` | the scripts in what you're wearing, per attachment and its point, and how many run |
| `//beacon NAME` / `//beacon off` / `//beacon` | a sound on the nearest person or thing by name to walk to (walk mode's beacon, below), off, or where it is |
| `//touch NAME` / `OBJECT PART` / `NAME face DIR` | touch a thing or part by name within 10 m, on a face if given (Parts and faces, below) |
| `//switch` / `flip` / `main` / `branch` | the next rail switch and how it's set; throw it; set it to the main line or the branch, throwing it only if it isn't already (walk mode's W and Shift+W; Rail switches, below) |
| `//outfit` | what you're wearing, by name (Ctrl+Shift+W) |
| `//upload` / `//upload PATH` | upload a file: the Open window, or the file at PATH (Ctrl+U; Uploading, below) |
| `//describe [here|me|NAME|person NAME|object NAME|more]` | describe (Describing, below) |
| `//voice` / `answer` / `decline` | who's in voice; answer or decline a call |
| `//about` | the version, the commit, the disclaimer and the data folder |
| `//sendlog` | send a session's log to the studio (Sending a log, below) |
| `//help [word]` | the commands |

`//go xyz` takes coordinates past the region's edge (over 256, below 0: a recorded route's crossing, Recording a route,
below) and walks there straight by the autopilot, judged arrived by where the avatar is in the region it set out from.
`//go` walks by the region's own autopilot, as Radegast's, around what's in the way (Paths, below): "Walking 9 m to
232, 43, around something in the way: 3 stretches.", then each new stretch ("2 of 3: 2 m to the west-northwest."). It
ends, once the avatar has stopped, with "Finished walking, under a metre from the destination." (or how far, and
"something was in the way" when it stalled), the cue such routes wait for. Seated (a chair, a pose ball), it stands
first ("Standing up to walk."). A goal out of reach where the floor meets it (up on a stage with no steps, inside a
bar) is walked as near as the way goes: "Walking 6 metres toward Kestrel; Kestrel is out of reach, so as near as the
way goes, 2 metres short.", ending "Finished walking, as near to Kestrel as the way goes: Kestrel is 2 metres, twelve
o'clock." With no way there at all (a room of its own behind a closed door) it says so once, and teleports there
only where that can land beside it (`walk_teleport_fallback`, on): "No way to walk to 244, 46 found: teleporting
there." Not where the region has a telehub or the parcel a landing point (its owner excepted), which in Second Life
is most clubs and shops ("... and a teleport wouldn't land there: the parcel there sends teleports to its landing
point."), and not again for the same spot for five minutes after one that landed elsewhere, which is said ("The
teleport landed you away from Kestrel: Kestrel is 14 metres, two o'clock."), so asking again doesn't teleport again
(the blind tester's walks to her partner in a jazz club did, 2026-10-31). With the fallback
off, the line names the command that would try (`//go tp person Kestrel`). `//go tp` says where it really landed the
same way. Unlike Radegast (a bug there), `//face object` and `//find object` look for the name alone.

### Walk mode

F3; M3, after a blind tester's request for arrow keys with a sound per step and cues for openings and doors, as in the
audio game Shades of Doom. F3 puts the focus on the walk pad, a plain pane at the foot of the window ("Walk mode", out
of the Tab order: not an edit or a list, so the screen reader passes the arrows and letters through); F3 or Escape goes
back where the focus was ("Walk mode off."), and any other way of leaving it (Alt+number, another window) lets go of
every key. The arrows move the avatar through the region's own movement (the agent's control flags, as Radegast's 3D
window and every viewer send them); the local collision model only tells you what's around.

| Key (in walk mode) | Does |
|---|---|
| Up / Down | walk forward / back while held (Shift: run); let go and it stops where you are (the viewer's STOP flag, no coasting on) |
| Left / Right | turn: a tap one step (22.5 degrees by default, landing on the compass's sixteen points, said: "north-northwest."), held smoothly (90 a second; where you face is said when you let go) |
| Shift+Left / Right | a step sideways while held |
| Page Up / Page Down | up / down: flying, rise and sink; on foot, jump and crouch |
| Home | fly, or land ("Flying." / "Not flying: landing.") |
| Space | stop everything: the keys, a `//go`, a follow, a route ("Stopped.") |
| H | heading: "Facing northeast, 45 degrees." (Second Life's degrees, 0 east, counter-clockwise, as `//face` takes them) |
| V | speed: "Walking, 3 m a second, to the north." / "Standing still." / seated, the vehicle's |
| L | where: "Atelier East, 236, 52, 22, on stone." |
| A | what's ahead: the first thing straight ahead within 30 m, "Blue Door, 4 m ahead." or "Clear for 30 m ahead." |
| S | the sonar: walls on four sides, openings by clock face, doors by name; the words and a sweep of sound (below) |
| T | touch what's ahead within 3 m, where the ray met it (a door opens): "Touched Blue Door." |
| W / Shift+W | the next rail switch and how it's set: "Switch 35 metres ahead, set to the main line, North Station. Main line: North Station; branch: East Yard."; Shift+W throws it, saying the new setting once it has changed (Rail switches, below) |
| D | doors and gates by name within 15 m: "Garden Gate, 6 m, two o'clock." |
| O | the marks: doors, seats and touchable things within 12 m, said and sounded from where they are (below) |
| B / Shift+B | where the beacon is ("Beacon on Bench, 4 metres, twelve o'clock." / "Heading to 54, 41, ..."); Shift+B takes one you put on off |
| R / Shift+R | record a route (Recording a route, below): R starts, and while recording says how it's going ("Recording: 6 steps, 48 metres walked; the last point is 3 metres, seven o'clock."); Shift+R ends it, asks its name and saves it |
| N / Backspace / P | while recording: a note here (a box for its words; left empty, just a point); take back the last point or note; pause, or go on |
| M / Shift+M | how far: "Last walk: 0.9 metres. In all: 4.2 metres. Where the count began: 3.1 metres, six o'clock." (while walking, "... so far"); Shift+M: "Counting from here." (the count also starts again at a teleport) |
| F1 | these keys |

Seated on something, or with a script holding the movement keys, the same arrows drive it (CONTROL_FWD and BACK,
ROT_LEFT and RIGHT, UP and DOWN for Page Up and Down, a tap held long enough to reach the script): a car, a horse, a
boat, a ride's controls. H and V then say the vehicle's heading and speed, A and the warnings look ahead from it,
leaving out its own parts. A rail switch coming up is said once with how it's set, W says the next one and Shift+W
throws it (Rail switches, below).

**The cues**, each a sound and a line for the screen reader (sound is never the only channel), each with its own
setting for how it reaches you (both, text, sound or off; Alt+0 says both as "sound and text") and its sound's level
(quiet, medium, loud, full), in Alt+0 and on SojournSettings (`walk_NAME`, `walk_NAME_volume`). The sounds are our own,
made in code (`Walk/CueSounds.cs`), played through world audio's mixer, placed: a cue about something on your left is
in your left ear.

| Cue (setting, default) | When | Sound | Line |
|---|---|---|---|
| footsteps (`walk_steps`, sound) | each footfall while walking on the ground (also on `//go` and follows), by surface: ground, stone, wood, metal, glass, soft (flesh, plastic, rubber), water | a step for that surface, three takes a little apart so a walk doesn't sound mechanical | with text on, the surface when it changes: "On wood." |
| warning (`walk_walls`, both) | walking in walk mode toward something within about a second and a half (at least 1.8 m); once per thing until it's been clear a moment | two short falling tones; a door (named door or gate): two knocks | "Wall, 2 m." / "Blue Door, 4 m." / a person's name; "Something" for what isn't upright |
| blocked (`walk_walls`) | a walking key held, the region not moving you | a dull thud | "Blocked." |
| openings (`walk_openings`, both) | passing a gap in a wall beside you (it was within 3 m for a metre, now open beyond 4.5 m); a named door within 3 m beside you | a rising two-note chime, or knocks, from that side | "Opening on the left." / "Garden Door on the right." |
| edges (`walk_edges`, both) | a drop of more than 1.5 m a step and a half ahead; water ahead when you're dry | a falling glide; three bloops for water | "Drop ahead, 3 m down." / "Water ahead." |
| turns (`walk_turns`, text) | after a step turn, or a held turn let go | a tick | "north-northwest." / "east, 0 degrees." |
| sonar (`walk_sonar`, both) | S | twelve pings, one from each hour of the clock, starting ahead and going round to the right: higher the nearer (under 2.5 m, under 7, further), a breath where it's open (nothing within 15 m); then a three-note chime from each opening | "Walls: ahead 11 m, right 1 m, left 1 m. Open: behind. Opening at twelve o'clock, 6 m away, 1.4 m wide. Opening at six o'clock, under a metre away, 2.8 m wide." and doors by name |
| distance (`walk_distance`, text) | a stretch walked by the arrows has ended: no walking key for 0.7 s, no key at all for 0.7 s, the avatar stopped (or 3 s on), and walk mode's line before it heard out; taps in quick succession (a turn between them too) are one stretch; straight from where it began; under 0.3 m nothing; not a `//go`, a follow or a vehicle | none | "0.9 metres." / "4 metres." (tenths under 3 m: a tap is about 0.9 on Second Life) / "1.8 metres backward." / "1 metre to the left." (by the keys that walked it) |
| follow (`walk_follow`, text) | following someone | none | "With Kestrel, 3 m, twelve o'clock." / "Kestrel sat down, ..." / "Kestrel is getting away: 24 m, ..." |
| vehicle (`walk_vehicle`, both) | driving: what's ahead (as the warning, further at speed), a seated border crossing; the speed has its own switch, `walk_speed` (on): the distance travelled over 2 s, said once it has held within 15% for 2 s and differs from the last said by 3 m/s and a third (a train's reported velocity swung 19, 6, 13, 8 three seconds apart, 2026-11-01) | as the warning | "12 m a second." / "Still on Walk Kart after the crossing." / "The crossing left you standing: you're no longer seated." |
| switch (`walk_switch`, both) | seated and moving: a rail switch coming into the way ahead within 60 m, said once (one a line, the nearest first, 2.5 s apart; again only after a minute out of the way ahead); the next one ahead said again if its setting changes; a throw done (W, Shift+W, `//switch`) | a points' clack, then one note forking into two that part; a throw: a heavy clack and a lighter answer | "Switch in 40 metres, set to the main line, North Station." / "Switch in 25 metres, set to: unknown." / "The switch ahead is now set to the main line." / "Switch set to the branch, East Yard." |
| beacon (`walk_beacon`, both; volume quiet) | a walk under way (`//go`, walk to, a follow, the assistant's walk), else one put on someone or something | a soft pip from the target's direction, every 1.4 s far off down to 0.6 s near, with a fifth above it while it's within 20 degrees ahead (front and back are hard to tell by ear); a small bell flourish on reaching a hand beacon | "Reached Bench." / "Beacon lost: Kestrel isn't here now."; B says where it is |
| route (`walk_route`, sound; volume quiet) | recording a route: each point it keeps (Recording a route, below) | one small bright wooden tick | "Step 4: a doorway." / "Step 5: a turn." / "Step 7: a teleport to Bay City." |
| marks (`walk_marks`, both) | O | each thing in turn round from ahead to the right, as the sonar: a door knocks (as the warning's door), a seat sinks (two low notes falling), a touchable thing taps (a small high bell) | "Bench, a seat, 4 metres, twelve o'clock. Garden Gate, a door, 4 metres, three o'clock. Bell, touchable, 4 metres, nine o'clock." |

Other settings: `walk_turn_step` (smooth, 10, 15, 22.5, 30, 45, 90 degrees), `walk_turn_rate` (45-180 degrees a
second), `walk_teleport_fallback` (on).

**The beacon** (M4): while the app walks somewhere (a `//go`, walk to, a follow: on the person as they move, the
assistant's walk: the session's autopilot's last point), a pip comes from where it's heading, placed a metre from the
head that way, so it's heard whatever the distance (louder only a little as it nears). With no walk under way it sounds
on what the person put it on, to walk to themselves in walk mode or by `//go`: `//beacon NAME` (the nearest person,
else thing, by name; `person`/`object` as `//find`), or Beacon on a person's or an object's Applications key. That one
stays until it's reached (within 1.6 m: "Reached ...", the flourish), taken off (Shift+B, `//beacon off`) or gone for
5 s; walking by hand doesn't stop it (it stops a `//go`), and a walk's own beacon takes its place while the walk lasts.

**The marks** (O): from the objects list's flags and names within 12 m, at most eight: a door or gate by name, a seat
(sits on a click, or named chair, seat, bench, sofa, couch, stool, cushion, poseball, hammock, swing), anything a script
takes touches; never what you're sitting on.

**Rail switches** (`Walk/WalkMode.Switches.cs`; Claude Opus 5.5, 2026-11-01, after the blind tester took two wrong turns
at switches driving her partner's train and had to back up and reset them). Built on the Second Life Railroad's switch
as the SL wiki documents it ("Second Life Railroad/SLRR standards" and ".../SLRR Switch", 2010-2012, which the Virtual
Rail Consortium's free tools follow), **not yet checked against a real switch in Second Life**:
- From the standard: the switch is a linkset whose root is named "Switch Stand", with a large invisible part over the
  stand taking touches (for "click-able switches"), and dwarf signals. Its description is
  `Identifier~Status~Heading Name~Mainline Name~Branch Name~messaging channel`, Status "Main" or "Branch" ("for train
  builders/scripters to sense and react"). Trains steer by invisible guide rails: the one in use named "Guide" and
  solid, the other renamed "Alternate Guide" and phantom. The stand listens for "Main" or "Branch" on its channel and
  shouts the new setting there when done. A mainline switch goes back to the main line 2.5 minutes after being set to
  the branch, and sensors at the merge set it right for a train coming into the merge by themselves (spring switches).
- From an SL feedback report (Pawpaw Wye, 2025-12), so still so today: touching the stand, or Main and Branch on its
  channel, throw it; that stand had changed while its guide rails hadn't.
- Not from the standard: an unsourced description passed on in Second Life had Guide always the main line's rail and
  Alternate Guide the branch's, one solid at a time; the standard renames them instead, so the names say only which rail
  is in use, never which way. Where the guide rails are (in the stand's linkset, the track's, or objects of their own)
  isn't documented; whether touching toggles or opens a menu isn't either.
- So: the setting is read from the stand's description (asked again each time it's said: the region sends a changed
  description only to whoever has the object selected), with the main line's or branch's name from it when it has one.
  A root named "Alternate Guide" (or "AlternateGuide", as one Marketplace kit names it) with no stand within 25 m is a
  switch of another make, said with its setting unknown and not thrown. Switch stands are found among roots within
  90 m in the regions in view, their names asked for twenty a look, nearest first ahead. "Ahead" is within 60 m along
  the way the seat moves (its facing, stopped), within 6 m either side and a third of the distance more (curves).
- Throwing (Shift+W, `//switch flip`, `main`, `branch`) touches the stand's part that takes touches (the largest, if
  several; the stand itself if none), as a person's click, then reads the description until its Status changes (6 s at
  most) and says the new setting. The guide rails within 15 m of the stand are watched too: if the stand changed and
  they didn't, it's said ("Its guide rails didn't change, though: the stand may be out of order"); a stand without a
  Status, if they changed, "Switch thrown: its guide rails changed, but its stand doesn't say which way." The channel
  commands aren't used: neighbouring stands may share a channel, and a shout would throw them all. A switch across a
  border is said but can't be thrown from here (touches go to the current region).
- What the first real switch should check (Tessel, or her next ride's log): the stand's name and description as
  written, which part takes the touch and whether a touch toggles, how long the description takes to change, and where
  the guide rails are.

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
keeps that room. The floor is followed out from where the avatar stands (2026-10-31): from each cell to the next, the
highest surface (or the ground) no more than 0.4 m up or 1.2 m down, so stairs, a stage's steps, ramps and a floor
over sloping ground are walked, and what's above that floor in the body's band is in the way (a stage with no steps,
a table); what the floor can't reach is blocked. With nothing known under the avatar (in the air, or on something the
model hasn't got) the floor goes on level at its feet. A goal that can't be got to
moves to the nearest spot that can, within 6 m, unless it's in a space of its own the floor never meets (a closed
room: no way). People aren't obstacles. A mesh or sculpt still downloading isn't known yet, so a walk that gets stuck
marks the spot for two minutes and plans again (twice), then counts as no way.

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
  Touch, Sit, Where, Describe, Faces. F5 reads them again.
- **Faces of X** (`faces`): the part's faces as the renderer out of sight drew them (`RegionScene.FaceList`: each
  face's mean normal, flatness, whether it faces inward), by direction from where you are, the side facing you first:
  "The side facing you, face 2", "The top, face 0", "The left side" and "The right side" as you face it, "The far
  side", "The bottom", "The curved side" (a face that wraps round), "The inside, ..." (a hollow's). Always with the
  number, which scripts and builders use. Enter touches that face's middle (llDetectedTouchFace and ST as a click
  there). On a HUD part, its faces as they show on the screen (HUDs out of sight, below): "The front, face 4" when one
  face is all that shows (a flat HUD's usual), else each with where on the part and how much ("Face 2, at the top left,
  about a third of it"). Without the renderer (`--no-render`, or a PC that can't run it), or on something worn on the
  body (not drawn out of sight), the list says so and parts are touched whole.
- **Show** (`objectsShow`, after the lists; Alt+Shift+7 goes straight there): things nearby, or what you wear, HUDs first
  ("7Seas HUD, on HUD Top Right, 14 parts, touchable"), then everything attached to the body (a pet or a skirt with a
  menu, "Pet, on Left Shoulder, 3 parts"; the blind tester couldn't reach her worn things, 2026-11-01). Their
  buttons are parts like any other: their parts listed (a body attachment's without where-words: its offsets are in
  its attachment point's frame), Enter presses one where it shows (a HUD's), or on the part without a surface (a body
  attachment's). Delete there says to take it off in the inventory.
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
  BMP), sounds (WAV, Ogg), animations (BVH, anim), models (glTF, GLB, Collada, OBJ).
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
  Life takes. Uploads can be pictures (PNG, JPEG, TGA or BMP), ..."), a sound over 30 seconds ("Too long.wav is 31
  seconds long; Second Life takes sounds up to 30 seconds. Trim it ..."), a picture over 8192 pixels a side or a file
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
- Radegast uploads pictures (a console logging each step, the fee on its button) and meshes; Firestorm and the
  official viewer have Build > Upload, a floater per kind, the picture's shown, a sound's playable.

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
bar adds ", incognito"). Access writes nothing of its own from a spell (its histories are on screen only).

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

Alt+9. It works once a provider that works is chosen in Alt+0's first rows (from the repo: the Claude account signed
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
window, and `//describe` (here, me, NAME, person NAME, object NAME, more).

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
- When the place is dark (its sun under about 10 degrees, and no sky of your own), people, objects and what's worn are
  lit as at midday (the viewer's own midday sky, for that picture only) so their colours show, and the model is told
  the light was changed and the place is dark; here keeps its own light (the user's call, 2026-10-29).
- A camera that would be inside a wall comes closer (and wider) or goes round to another side.
- Each picture lists what's visible in it by name (a 16 x 9 grid of picks through it: who and what, where in the
  picture, how much of it, and where from you, "4 metres, two o'clock"), with how much the subject fills; the names go
  beside each picture as well as in the facts, so they stick to what's seen.
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
  points; for you, the clothing and body parts too, and what you sit on; you are "the person themselves (you)" wherever
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
become "person N" (the order they come up) and the person's own "me"; an object's line relaying someone ("person 2:
...") and any line repeating five words in a row of someone's left-out words (or a whole short line) are left out;
keys, capability URLs, the Windows account's folder and public IP addresses are masked. The assistant's replies and
what it said about people stay, names replaced. A log from before the records isn't offered. `tools/logcheck` checks
the filter on a synthetic log (`dotnet run --project tools/logcheck`; with a path, prints that log as it would go).

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
  `MainForm.Places.cs`, `MainForm.Search.cs`, `MainForm.People.cs` (friends, groups, profiles, pay),
  `MainForm.Describe.cs` (describing), `MainForm.Parts.cs` and `Linkset.cs` (parts and faces), `MainForm.Huds.cs` (HUD faces and buttons), `MainForm.Place.cs` and
  `PlaceWords.cs` (placing by words), `MainForm.Outfit.cs`, `MainForm.Incognito.cs`, `MainForm.Voice.cs`,
  `MainForm.Menu.cs` (the menu bar), `MainForm.Builders.cs` and `BuildersLine.cs` (the builders' tab and its relay
  calls), `MainForm.Upload.cs` (uploading and its window), `MainForm.Gestures.cs`, `MainForm.FriendsOnline.cs` (friends online at login as
  one line), `MainForm.Devices.cs` (sound devices), `MainForm.CutLines.cs` (lines cut by typing), `EditorForm.cs` (notecards and
  scripts), `Dialogs.cs` (a line asked for; a person picked), `Commands.cs` (the `//` set), `PersonOps.cs` (the
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
  - With UI Automation, emptying is far worse: WinForms disconnects each item's UIA provider (made for every item once
    a client walks the tree, as a test's `find id=...` does) one by one, 2-3 ms each (F5 with 4,500 open once took
    12 s). NVDA reads the tree by MSAA (`ReaderTree`) and doesn't make them.
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
- **Local teleports reported failed**: LibreMetaverse sometimes answers a teleport within the region as failed
  ("Teleport started" or "Teleport finished") as it arrives; `AgentSession.TeleportAsync` counts it arrived when the
  agent is at the place asked within 3 s (a Core change; the VR client had the same false failures). A landmark
  teleport within the region may never report finishing either, so `Commands.TeleportLandmarkAsync` also counts being
  where the landmark points. The teleport history (`Places.Arrived`, Core) keeps where a teleport within the region
  landed, even when it came before the parcel's name.
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
  their draw list with no view of the world (planned, nothing drawn: no GPU work, unseen too, on the processor a few
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
- **Sound as a map** (M4): `Sound/Earcons.cs` (classifies feed entries by kind, the notice's notification name on
  `FeedEntry.NoticeName`; placed toward the speaker by `WalkMode.Toward`), `Walk/WalkMode.Sound.cs` (the beacon:
  `Commands.Heading` for its own walks and follows, else `Motion.Destination` for the session's autopilot, else the
  hand beacon; the marks), the sounds in `Walk/CueSounds.cs`. Every Access sound plays as world audio's `App` kind,
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
    by the grid's quote). `SOJOURN_TEST_UPLOAD_FEE` ("texture=0,mesh=0,animation=500") pretends fees on a free grid
    for the test.
  - For describing (M5): `OffscreenRenderer.ShootAsync` (`OffscreenRenderer.Shots.cs`: shots planned on the render
    thread, each drawn until loaded, at most 3 s, 4 rich, its pixels waited for, then the 16 x 9 grid of picks),
    `DescribeApi` registered by `SessionHost.StartDescribe` (Access only), `SessionHost.App` / `AssistantHost.App`
    ("access", in the sidecar's hello, so its prompt and context line say Sojourn Access and the describe tool is
    offered; null in the VR client, whose prompt and tools are as before).
  - For HUDs out of sight (2026-11-01): Render's `FrameRenderer.RenderHud` (the HUD pass with no world view;
    `PlanHud` can build without drawing); SojournHud moved from the VR client into Session (`Hud/HudApi.cs` over an
    `IHudScreen`, the client's `HudView` one of them) with `faces`, `buttons`, `save_buttons`, `forget_buttons` and
    presses by face or button, which the VR client's assistant gets too; the `hud` tool's `save_buttons`, and the
    sidecar's reading (`describer.read_buttons`, brain.py's describe key with `of=buttons`).

## Testing

`python tools/access.py test` (the scripted test on a hidden desktop), `tools/inputcheck`, `package --check`, a real
NVDA run, and the guided tests with the user or a tester: `docs/access-testing.md`.
