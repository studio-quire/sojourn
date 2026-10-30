# Sojourn Access: using it, how it fits, testing

Written by Claude Opus 5.5, 2026-10-29. Agent-facing (the guided test at the end is for walking the user or a tester
through it). The plan, stance and milestones are `docs/access.md`; this is what's built of M1 and how to use, change
and check it.

## Running it

- `python tools/access.py run` builds if needed and starts it on this desktop with the login form (the local grid
  filled in; log in as `Access Tester`, password `sojourn-local`, or any local account). It runs as
  `%LOCALAPPDATA%\dotnet-sdk\dotnet.exe src\Sojourn.Access\bin\Release\net10.0-windows10.0.22621.0\SojournAccess.dll`: the
  apphost (`SojournAccess.exe`) can't find .NET here (the SDK isn't a global install), and dotnet.exe already has the
  firewall's answer. `python tools/access.py build` builds it (below-normal priority, two nodes).
- Command line: `--grid URI --user "First Last" --password P --start last|home|Region/x/y/z --port 9725 (its agent
  API; on in the repo, off in a packaged build unless `--api`, since any program on the PC could read the session
  through it; `--no-api`) --base DIR (default local/access) --no-assistant --no-voice --no-sounds --reader auto|on|off --autologin
  --no-render --local` (`--autologin` presses Log in itself: tests; `--no-render`: no renderer out of sight, below;
  `--local`: the local grid offered and chosen, which a packaged build otherwise leaves off the list).
- Second Life itself is first in the login's grid list (`agni`, `sl` or `second life` typed as a grid too); the TPV
  must-dos are the session host's, shared with the VR client (`docs/tpv-policy.md`).
- A grid on this PC (a loopback login URI) is reached over loopback alone: LibreMetaverse's UDP binds to 127.0.0.1
  (`Settings.BindAddress`, set per login in `MainForm.StartAsync`), so no port opens to the network and Windows
  Firewall never asks for local-grid runs of a new exe. Voice (WebRTC) still binds every interface, and so does
  Second Life's UDP.
- State, logs and settings live in its base dir: `state/<grid>/<first.last>/` per account, `logs/` the session's
  console, `vr-settings.json` the session's settings (the reader's switches among them; apart from the VR client's,
  which keeps its own in local/client), `access-settings.json` this app's own.

## Packaging (a build for a tester's own PC)

`python tools/access.py package` (about 2 minutes) makes `local/dist/SojournAccess-0.1.0.N-win-x64.zip`, N the repo's
commit count, so every packaged build has its own number at the login (`AppInfo`; the TPV policy's unique version,
channel still "Sojourn"; a build in the repo stays 0.1.0.0). It refuses uncommitted changes under `src/` (`--dirty` for
a local try, marked so in the zip's name and `//about`). Built through `SOJOURN_BUILD_ROOT=local/dist/build`, so the
repo's own bin and obj are untouched; staged in `local/dist/stage/Sojourn Access 0.1.0.N/`.
- **What's in it** (87 MB zipped, 225 MB unpacked, checked 2026-10-29): `SojournAccess.exe`, self-contained single-file
  (the .NET 10 runtime, WinForms and ASP.NET's Kestrel inside; our pdbs embedded, so stack traces have lines);
  beside it the natives it loads by name: `sojourn_voice.dll` (voice and world audio, from `local/voice`),
  `openjp2.dll`, `shaderc_shared.dll` and `libSkiaSharp.dll` (the renderer), `nvdaControllerClient.dll` (from
  `local/nvda`); the folders `Shaders`, `Character`, `Textures`, `Fonts`, `linden`; `README.txt` (for a screen-reader
  user: the template is `src/Sojourn.Access/Package/README.txt`); `Licences/` (notices for NVDA's client, the viewer's
  LGPL parts, libwebrtc, OpenJPEG, Archivo, every NuGet package with files in the build read from the publish's
  `project.assets.json`, the .NET runtime packs; texts not found elsewhere in `Package/licences/`). Web and video media
  (CEF, libVLC) aren't in it: only the VR client uses them.
- **Packaged, it behaves as a person's app** (`AccessOptions.Packaged`: no `src/Sojourn.Access` above the program):
  data, settings, logs and caches in `%LOCALAPPDATA%\Sojourn Access`, never beside the program; Second Life chosen at
  the login, the local grid not listed unless `--local`; no assistant (its Python side only exists in the repo,
  `AccessOptions.AssistantInBuild`): Control+D says once a session that describing needs the assistant, which this
  build doesn't include, then what the lists know, and Alt+9's input is named "Ask the assistant (not in this build)".
  `//about` says the version, the commit, the disclaimer and the data folder; F1 ends by pointing to it.
- **Checked** by `python tools/access.py package --check [ZIP]` (default the newest zip; about a minute; passed 38 of
  38 on 0.1.0.504, 2026-10-30, as --bare too; released as v0.1.0.504 on studio-quire/sojourn-access): the zip unpacked in a fresh folder under %TEMP%, run on a hidden desktop of its own
  through QuireUia as Package Tester (`--local --autologin --reader on --no-voice`, world audio to a file, no mic, the
  data folder `--base` in the check's folder: .NET's GetFolderPath ignores %LOCALAPPDATA%, so the default folder itself
  isn't exercised), with no dotnet on PATH and %TEMP%, %LOCALAPPDATA% and %APPDATA% pointed into the check's folder.
  It checks: no .NET loaded from outside the exe, logged in to the local grid, the main window and the focus in Say,
  arriving, F1 and `//about` read out as UIA notifications (`//about` naming github.com/studio-quire/sojourn and the
  data folder), Control+D's absence said once in two presses, chat sent (the grid's echo), the renderer out of sight
  up and the GPU working for it, `nvdaControllerClient.dll`, `sojourn_voice.dll`, `openjp2.dll`, `shaderc_shared.dll`
  loaded from the package's folder and nothing from the repo, no TCP listener at all (9725 included) and every socket
  on loopback (Windows' "Bound" rows of outgoing sockets aside), no failures in the log, a clean exit and logout, the
  package's folder byte-for-byte unchanged, nothing written through the redirected folders nor anything named as ours
  new in the profile's real ones (the GPU driver's own cache aside); then it removes everything it made. It refuses
  to run beside the VR client or dev mode. `--bare` runs it as on a PC unlike ours (2026-10-30): Vulkan's drivers
  hidden (`VK_LOADER_DRIVERS_DISABLE=*`: a VM, Microsoft's basic display adapter, an old card) and `sojourn_voice.dll`
  removed, which fails as it would on Windows' N editions (it imports Media Foundation's `MFPlat.DLL`). It still logs
  in, reads out, chats and quits clean; the renderer gives up after three tries (`NeverRan`), and walk mode, the sonar
  and faces say so in a person's words (`ViewWords`); arrival says once that sounds can't play and why (`SoundWords`;
  README.txt tells how to add the Media Feature Pack). The other natives need nothing beyond Windows itself
  (shaderc only the universal CRT, voice's CRT static; checked with objdump). Not checked: Second Life itself from a
  packaged build, voice, Windows Hello, SmartScreen and the firewall's question (the first SL login asks for a new
  exe: UDP binds every interface): the guided test of a release, below.
- **Not signed**: SmartScreen warns on first start; the README walks through it and the zip's Unblock. Signing is the
  user's call.
- **An assistant-enabled package** would need: the sidecar (`assistant/`) and a Python runtime shipped (an embeddable
  CPython with the venv's packages, a few hundred MB, or the sidecar ported into the app), `Sidecar` finding them beside
  the program instead of above it, the person's own Claude sign-in or API key and somewhere to keep it (DPAPI, as
  `LoginStore`), the local hearing models if voice requests are wanted, and the privacy and TPV disclosures that come
  with sending chat and pictures to Anthropic (`docs/tpv-policy.md` > Data and privacy).

## Using it

Everything is standard Windows controls (edit boxes, list views, tabs, buttons), so NVDA and JAWS read them as they
read Radegast. What arrives is read through the person's own screen reader (A1's announcer, `docs/screen-reader.md`):
NVDA by its controller client when it runs, otherwise UI Automation notifications raised from the focused control.
Nothing moves the focus but the person's keys.

**Login.** Grid (a plain list: the local grid, a grid given or used before, or "Another grid", which opens a Login
URI box), Username, Password, Show password (a check box), Start at (a plain list: my last location, home, a place
given, or "Another place", which opens a Place box), Log in. The focus starts where you'd type next. Progress and
failures are said ("That username and password don't match."); the focus goes back to Password. The grid's Terms of
Service and an MFA code come in their own small windows, answered only by the person's own key or click. On arrival
the main window opens with the focus in the Say box and "Logged in, in Atelier. F1 for keys." is said. Another
account after a failed one restarts the app with the form filled in (each account's state is its own).

**Areas** (Alt+number; Radegast's numbers where Radegast has the same area):

| Key | Area | What's there |
|---|---|---|
| Alt+1 | Nearby chat | the nearby chat history (read-only, reviewable line by line) over Say (input); the history is one Shift+Tab back from Say, as in Radegast |
| Alt+2 | Conversations | a tab per IM or group chat: its history over its input ("Message to Kestrel") |
| Alt+3 | People | Nearby ("Kestrel Tester, 4 metres, two o'clock", nearest first), Friends ("Kestrel, online", online first), My groups ("Builders, tag worn") |
| Alt+4 | Inventory | the tree ("Red Dress, skirt, worn"; "Hair, object, worn on Skull"; "..., marked"), Search, and Show (everything, worn, recent) |
| Alt+5 | Search | Search for (words), Look for (people, places, events, groups, classifieds), Results |
| Alt+6 | Places | Landmarks (a tree: the Landmarks folder and Favorites), Teleport history ("Bay City, Harbour, today 14:05"), Go to region (a box), Regions around ("Bay City, 3 people, next door to the north") |
| Alt+7 | Objects nearby | "Notices Harp, 3 metres, eleven o'clock, touch", nearest first, within 30 m (Show: your HUDs instead); Tab: the selected one's parts, then a part's faces (Parts and faces, below) |
| Alt+8 | Notices | what waits for an answer, newest first; the selected one's text, keys and reply box |
| Alt+9 | Assistant | a typed request ("Ask the assistant"), its replies, its choices as buttons |
| Alt+0 | Settings | the ones that matter here (read-out and its kinds, mutes, timestamps, arrivals, earcons and volumes, walk mode's cues); Enter or Space moves one to its next value, Shift+Enter or Shift+Space to the one before (a sound's row plays it). First, the assistant's provider (Claude account, Anthropic key, OpenRouter key) and whether it works; its key (Enter: a password box, Control+V or typed; Delete twice forgets it); OpenRouter's model (Control+Enter types any id). `docs/assistant.md` > Switching it on |

**Keys** (F1 says them):
- In any input: Enter says (or sends), Shift+Enter whispers, Ctrl+Enter shouts (nearby only); Ctrl+Up/Down bring back
  earlier lines. `/me`, `/7 text` (a channel) as in any viewer; `//` commands below.
- Ctrl+Tab / Ctrl+Shift+Tab: nearby chat and the conversations as one ring (focus to the input). Ctrl+W closes the
  conversation you're in.
- Ctrl+N: the newest notice (the Notices area, it selected). On the notices list: 1-9 and 0 press its keys in order,
  Enter goes to its keys (or the reply box for llTextBox), Delete puts it away; on a confirmation, Enter confirms and
  Escape cancels. Tab from the list reaches the notice's text, its reply box and its buttons ("1: Red").
- Applications key (or Shift+F10, or Enter) on a person nearby: Send IM, Walk to, Face, Where, Follow, Beacon, Profile, Pay...,
  Offer teleport, Request teleport. On a friend (Enter: an IM): Send IM, Profile, Offer or Request teleport, Pay...,
  End friendship (confirmed). On a group (Enter: its chat, a conversation tab): Group chat, About the group, Wear its
  tag, No group tag, Leave (the grid asks in Notices). A profile opens in its own window: a read-only text read line
  by line (name, born, partner, about, interests, picks, first life, web), Send IM, Pay, Close (Escape). Pay asks the
  amount, then waits for the person's confirmation in Notices (refused at once when it can't be afforded). On an object: Touch, Sit, Walk to, Beacon, Face, Describe, Where (with its floating text), Parts, Move... (yours: Placing by words, below); on a HUD only Touch, Describe, Where, Parts. A person also has Always incognito with them. F5 refreshes the list; a
  list you're in never changes under you otherwise (it refreshes every 3 s while you're elsewhere). Arriving in a list
  that hasn't filled yet (Second Life's objects wait up to 3 s for their names), its name says so ("Objects nearby,
  looking") and its first item is read as it fills; the landmarks tree has a "Looking for landmarks." line, the
  inventory "Inventory not loaded yet." Only the list you're in tells the screen reader its item has the focus
  (`ReaderList`: WinForms announced the lists filling beside it, and Alt+7's parts and faces had the last word).
- In the inventory (Alt+4): arrows walk the tree (Right opens a folder); Enter wears an object, clothing or body part
  (takes it off if worn), opens a notecard or script, teleports by a landmark, opens or closes a folder; Space marks
  or unmarks ("3 marked"; the item's words end "marked": no check boxes, which NVDA read on every item and the guided
  test took for worn or not), and what's marked is what every action takes, else the selected one; an item's words
  follow a wear or take-off as it lands ("worn" comes and goes within a second); the
  Applications key (or Shift+F10) lists every action: Wear, Add, Take off, Rez on the ground, Unbox, Give to...,
  Cut, Copy, Paste, Rename, Details (rights, maker, date), Delete; on a folder Replace outfit, Add to outfit, Take
  off these, New folder, New notecard. Ctrl+X / Ctrl+C then Ctrl+V on a folder (or an item in it) move or copy;
  Delete and Give ask first (the confirmation comes up selected in Notices: Enter confirms, Escape cancels, then the
  focus is back in the tree; as in the official viewer and Firestorm, a worn body part isn't deleted, said at once
  without asking, and other worn items, the folder's too, are taken off first, the confirmation saying so); F2 renames; Ctrl+F goes to Search, where typing lists what matches flat with its folder
  ("2 found"), Enter goes back to the tree, Escape clears it; Tab from Search reaches Show (Up/Down: everything,
  worn, recent: the last day's). F5 shows it again (the tree doesn't change under you otherwise; your actions
  refresh what they touched). A notecard or script opens in its own window, an edit control: Ctrl+S saves ("Saved.",
  or a script's compile errors read out and listed under the text), Escape closes (asking first if changed).
  Unbox rezzes the box 2 m ahead, copies what's inside into a new folder named after it at the top of the inventory,
  then puts the box away (deleted when the inventory still has it, taken back when it was no-copy). Rez on the
  ground sets an object down 2 m ahead with its bottom on the terrain (or the floor you stand on, when that's well
  above it); Rez where you say... (S) asks where in words (Placing by words, below).
- **The second pane** (a blind tester's second inventory window): Alt+4 again, in the inventory's tree, opens a second pane
  beside the first ("Inventory, second pane", its own search and Show, `inventory2`), and from then on moves between the
  two. Each pane has its own marks and open folders; Ctrl+M moves what's marked (else the selected item) into the folder
  selected in the other pane (or the folder its selected item is in); the Applications key has "Move to X, in the other
  pane" and "Copy to X, in the other pane"; Ctrl+X/C/V work across them. Ctrl+W in the second pane closes it. Actions
  refresh what they touched in both.
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
- F6: mute all read-out of what arrives (answers to your keys are still said; a voice call ringing, who starts speaking
  and the music's titles are muted too); Shift+F6: mute or unmute the source you're in (nearby chat, or this
  conversation). Kept in access-settings.json.
- Answers longer than a line (F1's keys, `//help`, a long outfit or parcel list) are read whole, a few sentences at a
  time in turn, each its own line for review (`Announcer.WholeAnswers`; other announcers cut at 700 characters).
- Ctrl+[ / Ctrl+] (the bracket keys right of P): back and forward through what was read (the last 200 lines); Ctrl+Shift+[ the oldest, Ctrl+R or
  Ctrl+Shift+] the newest. Ctrl+Shift+M marks the line you're on (or the newest), Ctrl+Shift+C copies the lines from
  the mark to the one you're on to the clipboard ("Copied 3 lines."): the old NVDA add-on's review, mark and copy.
- Ctrl+Shift+T: times in the chat histories ("[14:05] Kestrel: hi", from the next line) or not.
- F3: walk mode (below); F3 or Escape again leaves it.
- Ctrl+D: describe what's selected (a person or object in its list, a part or the part a face is on, a friend, an
  inventory item, a profile's pictures), or else where you are; Ctrl+Shift+D: you and what you're wearing; the same key again on the same thing: more
  (Describing, below).
- Ctrl+Shift+A: arrivals and departures within chat range read out ("Kestrel came into chat range (12 m).", "Kestrel
  left chat range."), or none; Firestorm's radar reports, the finer choices in Alt+0.
- Alt+F4 (or the close button) while in world asks "Log out and quit?": Enter quits, Escape stays (Alt+F4 is next to
  Alt+4, and a slip closed the app at once in the guided test).
- Ctrl+Shift+W: what you're wearing, by name from the worn list (no model): "Wearing 6 things. Clothing: Red Blouse,
  shirt; ... Attached: Hair on Skull; ... HUDs: 7Seas HUD, top right. Body: ...". Sorted, so a "**Wearing: ..." item
  (a blind tester's outfit label) comes first. `//outfit` the same.
- Ctrl+Shift+I: incognito on; off again only by a physical key (Incognito, below).
- F4 (`access_talk_key`: f4, f12, scroll_lock, pause) held: talk in voice; Shift+F4 push-to-talk or open mic; Ctrl+Shift+V
  who's in voice (Voice chat, below).

**Read-out.** Nearby lines as "Kestrel: hello" (whispers, shouts, emotes worded so); a conversation's lines with its
source ("Kestrel, IM: ...", "Kestrel, Builders: ...") unless it's the one you're in, where it's just "Kestrel: ..." (in walk mode, or with the window in the background, you're in
none, so an IM never sounds like nearby chat);
the first line of a new IM says "new IM". Notices in the feed's words ("Access Menu: pick one."; the first with keys
in a session adds "Control N to answer."). A screen reader stops speaking at any key press or focus move, so a
notice cut off that way before it could be heard out (within a second to three, by its length) is said again once,
when the keys have rested 0.7 s, unless the focus went to the notices or the person silenced it with Control or Shift
alone (the screen reader's own keys to stop or pause speech: `RealInput.SilencedAt`). One-off notices (friends online, alerts, a teleport that failed) are also
status lines in the nearby chat history, as in Radegast. Arrivals by teleports this app didn't start (an offer, home,
the assistant) and region crossings are said ("Arrived in X.", "Now in X."). The reader's per-kind switches are A1's
(`announce_nearby` ... in SojournSettings). Arrivals and departures are Firestorm's radar reports
(`radar_chat_range`, `radar_draw_distance`, `radar_region`: enter, leave or both; off by default, as Firestorm), read
as notices; Ctrl+Shift+A turns chat range's on (both ways) or all of them off, Alt+0 has each.

**Earcons** (M4, `Sound/Earcons.cs`): a short sound per kind of thing arriving, so she can tell an IM from a menu from
an offer before NVDA gets to the words, or without them while read-out is muted (F6 mutes the words, never these:
that way something arriving can still be noticed without speech). Each kind is off or quiet, medium, loud, full
(`earcon_KIND`, Alt+0 "Sound for ..."); a changed row plays its sound at the new level, which is how to learn them.
Nothing sounds for the conversation you're in or a source muted by Shift+F6. They stand in for the viewer's own
new-IM and alert sounds, which Access turns off, so a line never sounds twice. The words for each are the line itself.

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

Placed ones sound a metre from the head in the speaker's direction, up to a third quieter at the edge of chat range.
**Volumes**: each cue and earcon its own; `access_sounds_volume` (25-200%) all of Access's own sounds together, apart
from the world's (`sounds_volume`) and the music (`music_volume`), so a loud club can be turned down without losing the
cues; `volume` everything. All in Alt+0 (the numeric ones step 0, 0.15, 0.25, 0.5 ... 2, up with Enter, down with
Shift+Enter). The screen reader is the person's main channel, so Access starts the parcel's music at 0.15 and the
world's sounds at 0.5 of the viewer's levels (the VR client keeps 1 and 1): put in the session's vr-settings.json once
(`AccessSettings.SessionDefaults`), and only where the value was still the VR client's default, so a person's own
choice stays theirs (the 2026-10-29 run on Second Life: Firestorm Social Island's music drowned NVDA until 0.15).

**`//` commands** (Radegast's syntax, so blind residents' notecard routes run as they are; typed in Say or any conversation):

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
| `//pause [SECONDS]` | wait (in a route, before its next line; a second unless said) |
| `//siminfo` | the region: type, maturity, people (of the limit), active scripts, objects, time dilation, frame rates |
| `//parcelinfo` | every parcel in the region, the one you're on first: name, area, who may enter, description |
| `//scriptinfo` | the scripts in what you're wearing, per attachment and its point, and how many run |
| `//beacon NAME` / `//beacon off` / `//beacon` | a sound on the nearest person or thing by name to walk to (walk mode's beacon, below), off, or where it is |
| `//touch NAME` / `OBJECT PART` / `NAME face DIR` | touch a thing or part by name within 10 m, on a face if given (Parts and faces, below) |
| `//outfit` | what you're wearing, by name (Ctrl+Shift+W) |
| `//voice` / `answer` / `decline` | who's in voice; answer or decline a call |
| `//help [word]` | the commands |

`//go` walks by the region's own autopilot, as Radegast's, around what's in the way when the renderer out of sight
knows it (Paths, below): "Walking 9 m to 232, 43, around something in the way: 3 stretches.", then each new stretch
("2 of 3: 2 m to the west-northwest."). It ends, once the avatar has stopped, with "Finished walking, under a metre from
the destination." (or how far, and "something was in the way" when it stalled), the cue such routes wait for. With
no way there at all it says so and teleports there where the region allows ("No way around found to 244, 46:
teleporting there."; `walk_teleport_fallback`). Not Radegast's bug: `//face object` and `//find object` look for the
name alone.

**Walk mode** (F3; M3, after a blind tester's request for arrow keys with a sound per step and cues for openings and doors,
as in the audio game Shades of Doom). F3 puts the focus on the walk pad, a plain pane at the foot of the window ("Walk
mode", out of the Tab order: not an edit or a list, so the screen reader passes the arrows and letters through); F3 or
Escape goes back where the focus was ("Walk mode off."), and any other way of leaving it (Alt+number, another window)
lets go of every key. The arrows move the avatar through the region's own movement (the agent's control flags, as
Radegast's 3D window and every viewer send them); the local collision model only tells you what's around.

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
| D | doors and gates by name within 15 m: "Garden Gate, 6 m, two o'clock." |
| O | the marks: doors, seats and touchable things within 12 m, said and sounded from where they are (below) |
| B / Shift+B | where the beacon is ("Beacon on Bench, 4 metres, twelve o'clock." / "Heading to 54, 41, ..."); Shift+B takes one you put on off |
| F1 | these keys |

Seated on something, or with a script holding the movement keys, the same arrows drive it (CONTROL_FWD and BACK,
ROT_LEFT and RIGHT, UP and DOWN for Page Up and Down, a tap held long enough to reach the script): a car, a horse, a
boat, a ride's controls. H and V then say the vehicle's heading and speed, A and the warnings look ahead from it,
leaving out its own parts.

**The cues**, each a sound and a line for the screen reader (sound is never the only channel), each with its own
setting for how it reaches you (both, text, sound or off) and its sound's level (quiet, medium, loud, full), in Alt+0
and on SojournSettings (`walk_NAME`, `walk_NAME_volume`). The sounds are our own, made in code (`Walk/CueSounds.cs`),
played through world audio's mixer, placed: a cue about something on your left is in your left ear.

| Cue (setting, default) | When | Sound | Line |
|---|---|---|---|
| footsteps (`walk_steps`, sound) | each footfall while walking on the ground (also on `//go` and follows), by surface: ground, stone, wood, metal, glass, soft (flesh, plastic, rubber), water | a step for that surface, three takes a little apart so a walk doesn't sound mechanical | with text on, the surface when it changes: "On wood." |
| warning (`walk_walls`, both) | walking in walk mode toward something within about a second and a half (at least 1.8 m); once per thing until it's been clear a moment | two short falling tones; a door (named door or gate): two knocks | "Wall, 2 m." / "Blue Door, 4 m." / a person's name; "Something" for what isn't upright |
| blocked (`walk_walls`) | a walking key held, the region not moving you | a dull thud | "Blocked." |
| openings (`walk_openings`, both) | passing a gap in a wall beside you (it was within 3 m for a metre, now open beyond 4.5 m); a named door within 3 m beside you | a rising two-note chime, or knocks, from that side | "Opening on the left." / "Garden Door on the right." |
| edges (`walk_edges`, both) | a drop of more than 1.5 m a step and a half ahead; water ahead when you're dry | a falling glide; three bloops for water | "Drop ahead, 3 m down." / "Water ahead." |
| turns (`walk_turns`, text) | after a step turn, or a held turn let go | a tick | "north-northwest." / "east, 0 degrees." |
| sonar (`walk_sonar`, both) | S | twelve pings, one from each hour of the clock, starting ahead and going round to the right: higher the nearer (under 2.5 m, under 7, further), a breath where it's open (nothing within 15 m); then a three-note chime from each opening | "Walls: ahead 11 m, right 1 m, left 1 m. Open: behind. Opening at twelve o'clock, 6 m away, 1.4 m wide. Opening at six o'clock, under a metre away, 2.8 m wide." and doors by name |
| follow (`walk_follow`, text) | following someone | none | "With Kestrel, 3 m, twelve o'clock." / "Kestrel sat down, ..." / "Kestrel is getting away: 24 m, ..." |
| vehicle (`walk_vehicle`, both) | driving: what's ahead (as the warning, further at speed), the speed when it changes by a third, a seated border crossing | as the warning | "12 m a second." / "Still on Walk Kart after the crossing." / "The crossing left you standing: you're no longer seated." |
| beacon (`walk_beacon`, both; volume quiet) | a walk under way (`//go`, walk to, a follow, the assistant's walk), else one put on someone or something | a soft pip from the target's direction, every 1.4 s far off down to 0.6 s near, with a fifth above it while it's within 20 degrees ahead (front and back are hard to tell by ear); a small bell flourish on reaching a hand beacon | "Reached Bench." / "Beacon lost: Kestrel isn't here now."; B says where it is |
| marks (`walk_marks`, both) | O | each thing in turn round from ahead to the right, as the sonar: a door knocks (as the warning's door), a seat sinks (two low notes falling), a touchable thing taps (a small high bell) | "Bench, a seat, 4 metres, twelve o'clock. Garden Gate, a door, 4 metres, three o'clock. Bell, touch, 4 metres, nine o'clock." |

**The beacon** (M4): while the app walks somewhere (a `//go`, walk to, a follow: on the person as they move, the
assistant's walk: the session's autopilot's last point), a pip comes from where it's heading, placed a metre from the
head that way, so it's heard whatever the distance (louder only a little as it nears). With no walk under way it sounds
on what the person put it on, to walk to themselves in walk mode or by `//go`: `//beacon NAME` (the nearest person,
else thing, by name; `person`/`object` as `//find`), or Beacon on a person's or an object's Applications key. That one
stays until it's reached (within 1.6 m: "Reached ...", the flourish), taken off (Shift+B, `//beacon off`) or gone for
5 s; walking by hand doesn't stop it (it stops a `//go`), and a walk's own beacon takes its place while the walk lasts.
**The marks** (O): from the objects list's flags and names within 12 m, at most eight: a door or gate by name, a seat (sits
on a click, or named chair, seat, bench, sofa, couch, stool, cushion, poseball, hammock, swing), anything a script takes
touches; never what you're sitting on.

Other settings: `walk_turn_step` (smooth, 10, 15, 22.5, 30, 45, 90 degrees), `walk_turn_rate` (45-180 degrees a second),
`walk_teleport_fallback` (on). If the renderer out of sight stops for a moment (a lost GPU starting again) while
walk mode has the keys, that's said ("Walk cues paused: ..." and "Walk cues back.") so silence isn't taken for open
ground. With `--no-render` there are no cues from the model (footsteps still play, on "ground").

**Paths** (`Walk/PathPlanner.cs`) for `//go`, walk to, follow and the assistant's walks (`LLAgent startAutoPilot`): the
collision model's solid triangles over the walk's area plus 12 m, on a grid of 0.25 m cells, what stands between knee
and head height (0.45-1.8 m over the floor) as obstacles; nearer than 0.3 m to them is blocked (a 0.8 m doorway still
passes), and A* pays more within 0.75 m, so the path keeps to the middle of gaps; then pulled straight where a line keeps
that room. The floor is the ground under each cell when the walk starts on the ground, else the floor stood on: a path
stays on one level (stairs up count as walls). People aren't obstacles. Only what the renderer has drawn is known, so
a walk that gets stuck marks the spot for two minutes and plans again (twice), then falls back to the teleport.

**Parts and faces** (M2, a blind tester's child prims: the sunken city's Treasure Chest 6 answers only on a child prim, which
Radegast lists by name alone; `MainForm.Parts.cs`, `Linkset.cs`). In Objects (Alt+7) the list has two more under it,
each following the selection above it only when that moves (Tab passing through a list never changes what the next is
about, the Radegast guided test's lesson, and no list changes while you're in it):
- **Parts of X** (`parts`): every part, root first then in the order the region made them (their local ids, which
  follow the link order the linkset was made in; not promised to be the scripts' link numbers). Named ones by name,
  unnamed ones ("Object", "Primitive", or the root's own name on a child) by number, what and where: "Treasure Chest,
  part 1 of 4, the root, a box", "Part 2 of 4, a box above the root, touch", "Latch, part 3 of 4, a tiny box in front of
  the root, nearer you, above it". Sizes: tiny under 12 cm, small under 40, large over 2.5 m, huge over 8. Where: up and
  down, left and right as you face the object, nearer or further from you; on a HUD, as on the screen. ", touch" when
  the part's own script takes touches, ", sit" for a sit click; a short description when it's unnamed ("described as
  Play"). Names come from selecting every part briefly, as the edit tool's linked parts are (on the local grid neither the
  root's selection nor a child's family properties brought a child's name). Enter touches that part (the part
  itself, never the root: the renderer's ray counts that part alone first); the Applications key: Touch, Sit, Where,
  Describe, Faces. F5 reads them again.
- **Faces of X** (`faces`): the part's faces as the renderer out of sight drew them (`RegionScene.FaceList`: each face's
  mean normal, flatness, whether it faces inward), by direction from where you are, the side facing you first: "The
  side facing you, face 2", "The top, face 0", "The left side" and "The right side" as you face it, "The far side",
  "The bottom", "The curved side" (a face that wraps round), "The inside, ..." (a hollow's). Always with the number,
  which scripts and builders use. Enter touches that face's middle (llDetectedTouchFace and ST as a click there).
  Without the renderer (`--no-render`), or on a HUD (not drawn out of sight), the list says so and parts are touched
  whole.
- **Show** (`objectsShow`, after the lists): things nearby, or your HUDs ("7Seas HUD, on HUD Top Right, 14 parts"), whose
  buttons are parts like any other: their parts listed, Enter presses one (on the part, without a surface: a HUD drawn
  as one textured prim with regions by touch position can't be pressed by part; that's the assistant's picture job).
- `//touch NAME` touches the nearest thing or part within 10 m with that name; for an object whose root takes no
  touches and exactly one part does, that part (the chest's lid); `//touch OBJECT PART` ("//touch chest latch");
  `//touch NAME face top` (bottom, front, far, left, right, inside, or a number). For routes and notecards.

**Placing by words** (M2; `PlaceWords.cs`, `MainForm.Place.cs`). Rez where you say... (the inventory, S) and Move... (an
object of yours in Objects, M) ask where: "a metre to my left", "2 m ahead and half a metre up", "at two o'clock, 3
metres", "1.5 m north", "here" (at your feet), "in front of me", "by 20 cm to the left" or anything with only up or down
(from where it is now: moving only). Distances in metres, centimetres or feet, digits or "a", "half a", "two"...; no
distance is a metre. It rests with its bottom (its whole linkset, as turned) on what's under the spot: the first solid
surface down from head height in the collision model (a table, a floor; said: ", on Table"), else the ground or the
floor you stand on. Answered in your terms ("Rezzed Chair, 1 metre to your left.").

**Incognito** (M1; `MainForm.Incognito.cs` over Session's Private/, the VR client's rules: docs/assistant.md >
Incognito). Ctrl+Shift+I turns it on (any key of yours, the screen reader's pass-on too; the assistant may too, asked)
and off, which only a physical key does, fresh, as confirmations take it (Windows Hello instead with
`access_answers_without_key` hello); SojournSettings refuses incognito off to every caller. "Incognito on." / "off."
are said (A1's). Alt+0: Incognito, Incognito: the assistant sees (turned on mid-spell only by your own key there),
while you're in an IM (the conversation in focus is the VR feed's open IM here), always on this parcel; a person's
Applications key has Always incognito with them (Session's Triggers: within chat range or an IM with them, ending by
itself once neither holds). The reader keeps reading (on the desktop the window is the view); the review history
drops the spell's lines when it ends; the session log pauses; the title says "incognito" instead of the region (the
status bar adds ", incognito"). Access writes nothing of its own from a spell (its histories are on screen only).

**Voice chat** (M1; `MainForm.Voice.cs` over Session's Voice/). On unless `--no-voice`, push to talk by default: hold the
talk key (F4; `access_talk_key`: f4, f12, scroll_lock, pause), only your own key opens the mic; a key held when the
window loses the focus lets go. Shift+F4 switches to an open mic (it starts muted; F4 then mutes and unmutes,
unmuting by your own key only) and back. Ctrl+Shift+V or `//voice`: where (nearby voice, a call, voice off here), who's
speaking, who else is in voice, your mic. `access_voice_speakers` (off): someone starting to speak said, each at most
once in ten seconds. A call ringing is said; `//voice answer` or `//voice decline`. Alt+0 has voice on or off, the mode,
how loud residents are and where they're heard from.

**The assistant** (Alt+9) works only with a Claude account and the assistant's Python side set up (`python
tools/assistant.py setup`); without them it says so and everything else works. Replies are read by the announcer (A1's
"the assistant" kind) and kept in the history; its choices are buttons ("1: ..."); a confirmation it needs comes to
Notices as the first item. Its instructions know it's Sojourn Access (`assistant/prompt-access.md`): no view, replies
through the screen reader, plain text, directions by clock face as the lists give them.

**Describing** (M5; `MainForm.Describe.cs`). Ctrl+D describes what's selected where the focus is: a person in People
nearby, a friend (as they look here, or their profile pictures when they're elsewhere), an object in Objects nearby,
a part in its Parts list (or, on a face, the part it's on), an inventory item (an attachment you wear as it's worn, a worn clothing layer as part of you, anything else by its
picture: a texture's or snapshot's own, else its thumbnail); anywhere else, where you are. Ctrl+Shift+D: you and what
you're wearing. The same key again on the same thing (within ten minutes) asks for more: brief by default (a few
sentences, the gist first), rich on asking, added to what was said rather than repeated. Also Describe on the
Applications key of people, friends and objects, Describe pictures (or Ctrl+D) in a profile's window, and `//describe`
(here, me, NAME, person NAME, object NAME, more). It says "Describing Kestrel." at once; the description arrives as the
assistant's reply (read out whole, a few sentences at a time in turn, where other announcements are cut at 700
characters: `AccessWiring.WholeReplies`; kept in Alt+9's history and in review). The request carries its subject by id, so the
model describes exactly that, and the pictures start drawing as the request goes (`DescribeApi.Prepare`), so they're
usually ready when the model asks. Without the assistant (no Claude account, or `--no-assistant`) it says so plainly,
then what the lists know, which needs no model: "Describing needs the assistant, and it isn't running: it needs a
Claude account. What the lists know: Kestrel Tester, 4 metres, two o'clock. Wearing items named: ..."; for you, your
clothing, body parts and attachments by their names; for an object, its own description, floating text and owner; for
here, the parcel, its description, the people and named things around.

What the model gets (SojournDescribe, `Sojourn.Session/Offscreen/DescribeApi.cs`, and the `describe` tool,
`assistant/describe.py`, offered only in this app): pictures aimed at the subject from cameras of their own, not the
avatar's view. A person (or you): whole and from the waist up from their front, their back too when rich, other people
left out of those pictures; an object from your side and a little above, a flat one (a sign, a poster, a board) face-on
with the picture shaped to it, plus its face's image as uploaded for reading text (rich: that image at full size, no
side view); something worn, on its wearer from
their front; here, four views from your eyes, ahead, right, behind and left, 90 degrees each. When the place is dark
(its sun under about 10 degrees, and no sky of your own), people, objects and what's worn are lit as at midday (the
viewer's own midday sky, for that picture only) so their colours show, and the model is told the light was changed and
the place is dark; here keeps its own light (the user's call, 2026-10-29). A camera that would be
inside a wall comes closer (and wider) or goes round to another side. Each picture lists what's visible in it by name
(a 16 x 9 grid of picks through it: who and what, where in the picture, how much of it, and where from you, "4 metres,
two o'clock"), with how much the subject fills and what was still loading; the names go beside each picture as well as
in the facts, so they stick to what's seen. The facts: names (the viewer's complete
names; "Object" counts as unnamed), an object's description, floating text, owner, parts, size, who sits on it, for
sale; what someone wears by its items' names and points; for you, the clothing and body parts too; for here the
parcel, its description, people within 30 m and named things within 20 m. Textures and profile pictures by id
(`texture`, `profile`). The tool's description carries how to describe: brief or rich, the names given and never
guessed, clothes in a designer's register (each garment's kind and cut, colours and material, what's printed or
pictured on it and where on the garment, then hair, shoes, accessories; a careful describer's way with outfits), looks
as presented without judging, text read word for word or said to be unreadable, directions from the person,
uncertainty said in words and nothing invented, nothing about pictures or cameras. With `--no-render` there are no
pictures (said so), only the facts and textures.

## How it fits

- `src/Sojourn.Access` (WinForms, net10.0-windows10.0.22621.0 for Windows Hello, x64; output `SojournAccess.dll`), on `Sojourn.Session` only, never
  the VR client. `Program.cs` (the login form first, then the session; the grid's questions; another account restarts
  the app), `LoginForm.cs` (with the ToS and MFA windows), `MainForm.cs` (the session made and started with
  `SessionHost.StartAllAsync`, keys, read-out shaping, the session's own events: arrivals),
  `MainForm.Chat.cs` (nearby, conversations, histories), `MainForm.Lists.cs` (people, objects),
  `MainForm.Notices.cs`, `MainForm.Assistant.cs`, `MainForm.Settings.cs` (a curated list over SojournSettings),
  `MainForm.Inventory.cs` (the tree, search and Show, the actions), `ReaderTree.cs` (its tree view and Places', read
  through Windows' own MSAA object: WinForms' numbered the items wrong for NVDA), `ReaderList.cs` (every list: its
  item's focus announced only while the list has the focus), `MainForm.Places.cs`, `MainForm.Search.cs`,
  `MainForm.People.cs` (friends, groups, profiles, pay), `MainForm.Describe.cs` (describing), `MainForm.Parts.cs` and
  `Linkset.cs` (parts and faces), `MainForm.Place.cs` and `PlaceWords.cs` (placing by words), `MainForm.Outfit.cs`,
  `MainForm.Incognito.cs`, `MainForm.Voice.cs`, `EditorForm.cs`
  (notecards and scripts),
  `Dialogs.cs` (a line asked for; a person picked), `Commands.cs` (the `//` set), `PersonOps.cs` (the person's own
  actions through the ops; where people and things are), `Bearings.cs` (clock faces, compass, SLurls),
  `FormVoice.cs` (UIA notifications from WinForms' own accessibility objects), `RealInput.cs`,
  `AccessSettings.cs`, `AccessOptions.cs`.
- **The person's own actions run the ops directly** (`PersonOps.DoAsync`) when they began with the person's input
  (`RealInput.Acting`: the input message an action started from, a hardware key or click or a key the screen reader
  passed on for one, carried through its awaits for up to 5 minutes; the session started at Log in doesn't carry it):
  the assistant's confirmations (its touches on others' things) and incognito's limits on callers don't stand in the
  way. An action begun by anything else (a timer, a callback, a UI Automation invoke, a key a program sent) goes through
  the gate as the assistant's would (`GatedAsync`); lookups that change nothing go `ReadAsync`. Spending, giving and
  deleting always go through the gate (`MainForm.ConfirmedAsync` over `GatedAsync`) so the person confirms those too:
  the person asked for it, so its own confirmation (the one raised for that call's arguments, `PendingConfirm.For`,
  never another showing first) comes up selected in Notices with the focus on it, and the focus goes back where it was
  once it's answered. Answers to notices go straight to `Notifications.RespondAsync`, as A1's keys do; a confirmation
  answered from Notices answers the one that notice showed (`ReaderActions.Confirm(token, ...)`), never one that took
  its place.
- **Only the person's own press answers** (confirmations, notices' keys, the ToS's I agree and Continue, the
  llTextBox reply, MFA): the assistant can run programs on this PC, so `RealInput` (the window's message filter) judges
  every input message as it arrives and `InputJudge` (pure logic) says how far it counts:
  - Confirmations, notices and the ToS are answered on lists and buttons, where screen readers don't take keys, so they
    take only what Windows calls hardware (`IMO_HARDWARE`; injected, posted and system-made input never), a fresh press
    (Windows' repeat bit; a button's Space or a click answers as of its press, and a key or button already down when
    the app saw nothing of it doesn't), made after what it acts on last changed by anything but the person. The focus
    (every `WM_SETFOCUS` in the app, seen by a thread hook), the selected notice and the window in front (`WM_ACTIVATEAPP`)
    are noted with who changed them: the person (during one of their presses), our own code acting for them
    (`RealInput.Ours`: the focus put back after an answer, onto the confirmation they asked for, the next page of a
    menu they're paging, only ever another script menu of that object's, so a web page, an item or a question it sends
    then waits out the settle like any arrival) or anything else, including a message another thread sent, a UI Automation call, a notice
    arriving or the app coming to the front. Whether another thread's message is being handled is what Windows says
    (`InSendMessageEx`, for the whole of its handling); pairing the sends a thread hook sees with their returns
    failed, since UI Automation's connection messages (`UiaServerConnection_HookMessage`, sent at each focus or
    selection change a screen reader follows) go in and never come back, and for 10 s after one every key was refused
    (the 2026-10-29 NVDA run: the first key after Ctrl+N or a confirmation). After our own move a press counts if it
    went down after it; after
    anything else, from 0.7 s after it (`InputJudge.Settle`: a key meant for something else, or one pressed before the
    change, can't land on what's there now). Refused, the person hears why ("That takes your own key.", "That key was
    already down...", "That changed just as you pressed. Press again if you meant it.").
  - Typed answers are sent from edit fields, where NVDA takes Enter, the arrows, Home, End, Page Up and Down, Backspace
    and Delete before any window sees them and sends each on with keybd_event (`EditableTextBase` binds Enter to
    `caret_newLine`, which calls `gesture.send()`), so the window gets them as injected input, as from a program (that
    refused the guided test's reply). So `KeyHook` (a low-level keyboard hook on its own thread, on only while the app
    is in front, put back ahead of other hooks every 2 s and at most once a second when a key came without its press,
    since NVDA's swallows the keys it takes and raw input never sees them either) notes the hand's presses and
    releases, and each press is good for one injected message of the same key into the window that was in front when
    it was pressed, within 1 s. That counts for the person's actions and typed answers, never for confirmations. The
    reply sends only text the person's own keys built (a character counts only as the character of their key just
    before it; anything else putting text there, a program setting it by UI Automation or posting characters, makes it
    refused until the box is cleared), and only to the notice it was typed for (another selected, the box empties).
    MFA's code the same.
  - Characters (WM_CHAR, WM_SYSCHAR) never answer, and the ToS's keys have no mnemonics: WinForms presses a mnemonic
    button on a character message, and a posted WM_SYSCHAR or WM_CHAR 'c' presses `&Continue` beside the focus
    (`tools/inputcheck` checks it). I agree is undone if anything but the person's press ticks it.
  - Negative answers (Cancel, Put away) and the assistant's offered choices take the person's input, passed-on keys
    included.
  - **Without a physical key** (`access_answers_without_key`, Settings: `refuse` by default, or `hello`; lowering it to
    hello is the person's own, in Alt+0 by a physical key or Windows Hello, and SojournSettings refuses it to callers,
    since it guards against programs, the assistant's among them; back to refuse anyone may): braille
    keyboards, Dragon, Voice Access, on-screen keyboards and switch access type through injected input, which can't be
    told from a program's. With `hello`, such an answer is asked again by Windows Hello (`Hello.cs`,
    `UserConsentVerifier` for the window, the action in its words: "Sojourn Access: Pay L$10 to ...", "reply to ...:
    the text"), answered by face, fingerprint or PIN; negative answers and choices then take any input. Why not the
    typed check (`confirm_text`): a program that can type can read the word on screen and type it too. The trade-off is
    in `docs/access.md` > Answers without a physical key.
  - Out of scope: software running as administrator or with UI Access (it can drive the app as the person can), and
    a virtual keyboard or mouse driver the person installed (Windows calls its input hardware).
  `tools/inputcheck` checks the judge with simulated sequences, and `RealInput` itself in a window of its own (posted
  keys judged as the hand's, a UI Automation client on another thread reading it as a screen reader does: Ctrl+N then
  a number, a confirmation our code focused then Escape and Enter, and moves by a callback or another thread's
  message still refusing) (`dotnet run --project tools/inputcheck`; `-raw` after the separator shows a swallowed key
  never reaching raw input). The VR client's `RealKeys` and `RealInput` take only hardware keys and clicks too, and its
  reader keys (`Access/ReaderKeys.cs`) a fresh press made while the window had the focus (a key held when it came to
  the front doesn't answer until pressed again). `SOJOURN_TEST_INPUT=1` lets tests use posted keys (every rule above
  is off then); `=keys` judges posted keys and clicks as the hand's with every other rule on, and posted characters
  as typed. `SOJOURN_INPUT_LOG=path` logs each key's judgement and each focus or selection change with who made it,
  for a key refused in a real run.
- **The person's own cancel** of a confirmation their action raised says "Cancelled." and nothing else
  (`ConfirmedAsync` gives its caller nothing to say; the op's own refusal is for other callers).
- **Enter in one-line inputs** (`LineBox`) is handled before Windows makes a character of it, so the edit control never
  gets the carriage return it answers with the default beep (heard in the guided test).
- **Read-out** is A1's `AccessWiring` in the session, with two seams added for this app (2026-10-29): its own UIA
  voice and greeting (`SessionHost.StartAllAsync(args, hwnd, voice, greeting)`), and `AccessWiring.Shape`, the app's
  say over each line (mutes, the source named or not, "new IM").
- **Settings**: `access_mute_all`, `access_timestamps`, `access_muted_sources`, `access_answers_without_key` (above), `access_view_rate` and
  `access_view_size` (the renderer out of sight's idle frames a second and their size), `SessionDefaults` (which of
  Access's own defaults for the session's settings are in place: the quieter music and world sounds), walk mode's `walk_*` (each
  cue's channels and level, the turn step and rate, the teleport fallback) on SojournSettings (section "access", so the
  assistant can change them when asked), kept in access-settings.json (`Walk`, only what differs from the defaults); the reader's own (`screen_reader`,
  `announce_*`) are A1's. Nothing touches the VR client's menu.
- **The assistant's walking** (`LLAgent startAutoPilot`, the session's client-side autopilot) goes round what's in the
  way (Paths, above, through `Motion.Router`), and steers here by the
  avatar dead-reckoned between the region's updates (`Motion.DeadReckon`, set by this app only; the VR client's walks
  are as they were): steering by the position last heard, it ran up to 2.3 m past the target; now it ends 1.1-1.2 m
  short with its 1.5 m stop distance. `SOJOURN_DEAD_RECKON=0` turns it off, for comparing. `PersonOps.Me()` (bearings,
  `//status`) reads the same position.
- **Numbers into ops are doubles** (or ints): `Json.AsDouble` reads a `float` JsonValue as nothing, so a position
  built from `Vector3` floats became 0, 0, 0 (a rez landed in the region's corner, and `//go tp` went there too, until
  2026-10-29). `MainForm.Xyz` makes positions.
- **Local teleports reported failed**: LibreMetaverse sometimes answers a teleport within the region as failed
  ("Teleport started" or "Teleport finished") as it arrives; `AgentSession.TeleportAsync` now counts it arrived when
  the agent is at the place asked within 3 s (a Core change, 2026-10-29; the VR client had the same false failures).
  A landmark teleport within the region may never report finishing either, so `Commands.TeleportLandmarkAsync` also
  counts being where the landmark points. And the teleport history (`Places.Arrived`, Core) recorded a stop at the
  next place when a teleport within the region came before the parcel's name: it now keeps where it landed.
- **The renderer out of sight** (`docs/access.md` > M0b; `Sojourn.Session/Offscreen/`): started after the session
  (`SessionHost.StartOffscreen`; `--no-render` leaves it and the session's render hooks out). It draws from the
  avatar's eyes on a thread of its own, two small frames a second turning around the avatar, more while the assistant
  works, and a picture at its own size when asked: the assistant's `look` (`LLViewerWindow saveSnapshot`) gets a real
  one, and the person's view is the avatar's eyes, a little down. The assistant's touches land where the VR client's
  would (on the surface the eyes' ray meets; with `face`, that face's middle, the far side too), still behind the
  person's confirmation for others' things. `SojournView` reports its state and casts the collision model's rays
  (`collide`), and walk mode's cues and paths come from its collision model. A lost GPU restarts it within the app in 3 s. Its log lines are
  `render`'s in the session log.
  **Unseen** (2026-10-30, after the first run on Second Life: at Firestorm Social Island it held ~1.7 GB of memory and
  ~1.9 GB of video memory, 1.2 GB of it textures fetched at 2-3 MB/s, for pictures nothing could look at): while nothing
  can look at its pictures it draws only geometry, which is all the picks, faces, the collision model, rays, physics
  shapes and paths use. No textures but sculpt maps (`TextureStore.DataOnly` through `RenderQuality.Textures`: nothing
  else fetched, decoded or uploaded, what was resident let go within seconds); no avatars or what they wear
  (`FrameRequest.NoAvatars`; the collision model's standing avatars are boxes from the world model, and Objects lists
  no attachments); no shadows, reflection probes, particles, flexible prims' sway or avatar physics. The draw distance
  stays the person's: at 64 m instead of 128 it saved only ~50 MB of memory in the perf grid's forest and no video
  memory, while paths for `//go` (their area plus 12 m, up to 200 m) know only what's been drawn
  (`UnseenDrawDistance`). Its geometry arenas are a quarter of the client's (`GeometryArenaScale`, 72 MB each, more as needed), unseen or not.
  Nothing can look while no assistant is up or starting (`AssistantHost.SidecarAlive`, or the sidecar's process
  running: a packaged build always, a repo run with `--no-assistant` or without the venv) and no picture was asked for
  in the last 10 minutes (`PicturesLinger`). A picture asked for (the snapshot ops, SojournDescribe's, the assistant's
  look) or the assistant coming up draws it whole again at once; what's in view loads from then on, and the picture
  waits its usual settling (at most 3 s, so on Second Life the first one after an unseen spell may still lack some
  textures). `SojournView status` has `unseen` and `draw_distance`; the log says when it changes;
  `SOJOURN_OFFSCREEN_UNSEEN_DISTANCE=M` for comparing. Numbers (2026-10-30, perf grid `forestcrowd`, Atelier 128,140
  by the dancing crowd, no assistant, three minutes from a cold cache; before / unseen): working set 630-740 / 520-600
  MB, private bytes 1.50 / 0.80 GB, video memory 0.90 / 0.32 GB, textures 96 MB from 106 fetches / none, CPU 3% / 2%
  of a core (the first minute 6% / 4%), GPU 4-5 / 1 ms a second (a frame 2.1-2.2 / 0.4 ms of GPU, 2.6-3.6 / 0.7-0.9 ms
  of CPU). That scene understates it: ~90 distinct textures and its 40 avatars jelly dolls (over the complexity
  limit), where Social Island's cost was mostly ~2,000 textures and whole avatars, none of which load unseen. A
  packaged build idle on the light local grid (`package --check`): 546 MB in memory and 328 MB of video memory, from
  531 and 574.
- **Walk mode** (`Walk/`, 2026-10-29): `WalkMode.cs` (the body as it is now, footsteps, the listener), `.Keys.cs`
  (keys to control flags, turning, braking, driving), `.Probe.cs` (ten batches of rays a second while moving: ahead,
  both sides, underfoot, down a step and a half ahead; the warnings, openings, edges, surface), `.Info.cs` (H, V, L, A,
  S, T, D, and a seated crossing said), `Sonar.cs` (the ring's words and openings, edges refined by sub-rays: pure
  geometry), `CueSounds.cs` (the sounds, made in code), `PathPlanner.cs`, `WalkPad.cs`, `WalkApi.cs` (SojournWalk:
  status, sweep). It runs on the window's 50 ms tick; rays go to the renderer's thread in one batch
  (`OffscreenRenderer.CollideManyAsync`, the seat's own linkset left out) and come back to the window. In walk mode the
  renderer draws at least four frames a second (`MovingHz`, two otherwise), so what comes near is in the model sooner.
  Positions are dead-reckoned (the region sends a walking avatar's position only now and then); when the region's word
  on the velocity says still while the position moves (a seat moved by llSetPos), the velocity comes from the positions.
  `SOJOURN_WALK_LOG=path` logs every probe and plan.
- **Sound as a map** (M4): `Sound/Earcons.cs` (classifies feed entries by kind, the notice's notification name now on
  `FeedEntry.NoticeName`; placed toward the speaker by `WalkMode.Toward`), `Walk/WalkMode.Sound.cs` (the beacon:
  `Commands.Heading` for its own walks and follows, else `Motion.Destination` for the session's autopilot, else the hand
  beacon; the marks), the sounds in `Walk/CueSounds.cs`. Every Access sound plays as world audio's new `App` kind, whose
  level is `WorldSounds.AppVolume` (`access_sounds_volume`) instead of the world's sounds volume. Access turns off the
  viewer's new-conversation sound (`AudioWiring.ConversationSound`) and alert sound (`SocialWiring.AppAlertSound`); all
  four Session/Core additions do nothing in the VR client. `SojournWalk status` has `recent` (the last 60 cues: name,
  ms ago, level, bearing clockwise from facing and distance from the head when placed), `beacon` and `marks`.
- **The listener**: Access has no camera, and until walk mode nothing set world audio's listener, so nothing placed in
  the world played at all (objects' sounds, footsteps, even the new-IM sound: WorldSounds waits for a listener). Walk
  mode's tick now puts it at the avatar's head facing where it faces, for world audio and voice.
- **The session's parts M3 touched** (behaviour-neutral for the VR client, which doesn't use them): Core's `Motion.Router`
  (paths for the session's autopilot: the waypoints an app plans, each passed within 0.5 m; null in the VR client, so
  its walks are as they were) and `RetargetAutoPilot` (a moving target), `StartAutoPilot(..., route: false)`;
  Render's `RegionScene.SolidTriangles` / `FrameRenderer.SolidTriangles` (the collision model's triangles in a box);
  Session's `SoundBank.AddLocal` (a client's own sounds, never fetched or let go), `AudioWiring.Bank` and
  `OwnFootsteps` (the viewer's own footsteps for the own avatar off while walk mode plays its own by surface),
  `OffscreenRenderer.CollideManyAsync`, `SolidsAsync`, `Moving`/`MovingHz`, a log line for a frame over half a second.
- **The session's parts M5 touched** (also neutral for the VR client): `OffscreenRenderer.ShootAsync`
  (`OffscreenRenderer.Shots.cs`: shots planned on the render thread, each drawn until loaded, at most 3 s, 4 rich, its
  pixels waited for, then the 16 x 9 grid of picks), `DescribeApi` registered by `SessionHost.StartDescribe` (Access
  only), `SessionHost.App` / `AssistantHost.App` ("access", in the sidecar's hello, so its prompt and context line say
  Sojourn Access and the describe tool is offered; null in the VR client, whose prompt and tools are as before).
- Voice chat is on unless `--no-voice` (push to talk on F4; the test runs with `--no-voice` unless `ACCESS_VOICE=1`);
  world audio (objects' sounds, the parcel's music and its titles read out) is on unless `--no-sounds`.
- **The session's parts M2 touched** (neutral for the VR client, which doesn't use them): Render's
  `RegionScene.FaceList` / `FrameRenderer.FaceList` (a prim's faces as drawn), `OffscreenRenderer.FacesAsync`, and
  `FindTouchAsync` counting a child prim given alone first (only the renderer out of sight's; the VR client's touches
  are DirectTouch's).

## Testing

`python tools/access.py test` (local grid; about 4 minutes; nothing on the visible desktop; `build` and `tools`
first; `ACCESS_STEPS=inventory` runs only the steps named so, after the login's checks; `ACCESS_NO_RENDER=1` starts the
app with `--no-render`, nothing on the GPU, for the steps that don't need the view, e.g. while someone's in the
headset). Last full run 2026-10-29
15:50 (keys judged by the real-input guard, about 9 minutes with the walk-mode step): 299 of 300, the known tab-item
one below. After M2, 2026-10-29 19:00, with `ACCESS_NO_RENDER=1`: 367 of 397, the 25 that need the renderer, the tab
item, and four (Alt+3 once, the permission question's answer, an announcement cut by an arriving notice) that passed
when run again; M2's steps with the renderer: 64 of 64. After the review of 2026-10-29 evening, with
`ACCESS_NO_RENDER=1`: 357 of 385, the 25 that need the renderer, the tab item, and two that passed run again ("every
part" stopping on a list refreshed under UI Automation, the second pane's line not heard). After the first run on Second
Life's fixes (2026-10-29 night, `ACCESS_NO_RENDER=1`): 399 of 422, the 21 that need the renderer, the tab item, and
the second pane's line (passed run again). It runs the app on a hidden
Win32 desktop (`tools/quireuia`, from the Radegast contributions' tools: `launch` starts a program there, `serve`
records the focus, alert and notification events a screen reader gets and drives the app by UI Automation and posted
keys), with `SOJOURN_TEST_INPUT=keys` (posted keys judged as the person's hardware presses, every other rule of the
real-input guard applying, so Ctrl+N then a number and a confirmation then Enter are checked against it;
`ACCESS_TEST_INPUT=1` lets every key through instead) and `SOJOURN_TEST_NO_NVDA=1` (on a hidden desktop NVDA's
controller client claims NVDA runs, so speech would go nowhere), and a bot (`tools/visitor`, Access Visitor) that
talks, IMs, and owns a scripted fixture box sending menus, llTextBox, permission requests and offers. It covers
login and focus, every area's controls named with their roles and the Tab order (the history one Shift+Tab back from Say), chat in and out (say, whisper,
shout), an IM's tab arriving without taking the focus, Ctrl+Tab and Ctrl+W, people and objects with their bearings
measured against positions (their first fills held back 3 s, `SOJOURN_TEST_LIST_DELAY_MS`, as Second Life's objects
wait for names: arriving, the list says "looking"; once it and the lists under it have filled, its own item is the only
one with the keyboard focus and the last focus event, by UI Automation and MSAA), F1 (all of it, none cut short), F6,
Ctrl+R, SojournSettings refusing to lower `access_answers_without_key`, the music and world sounds starting at 0.15
and 0.5 (kept in the session's file, applied once), the music's volume stepped by Enter and Shift+Enter in Alt+0,
`//status music`, a notice silenced by Control alone not said again, Alt+0, Alt+F4's question (Escape stays), a menu (and its announcement
said again when a key cuts it off), a text box and a permission question answered by keys, a
`//go` and a `//tp` checked against the region's positions, the assistant's walk (the session's autopilot) stopping
about its stop distance short. The inventory step makes its own fixtures through the agent API (a folder, notecards,
a crate with a notecard inside) and then, by keys only: Alt+4 and the arrows (what an MSAA reader is
told, as NVDA reads WinForms' trees, is the highlighted item: as first shown, after F5, inside a folder), a search, a notecard opened, edited, saved (checked by
reading it back) and closed, two items marked with Space, cut and pasted into a folder, one copied, the crate worn by
Enter (checked attached), listed by Show: Worn (Delete on a worn body part listed there refused at once, no confirmation), taken off, rezzed on the ground (its bottom within 0.15 m of the
terrain), unboxed (its notecard in a new folder, the box gone), a notecard given to the visitor through the picker
and the confirmation (the visitor gets the offer; the focus comes back to the tree), and the run's folders deleted
through the confirmation (the first cancelled once by Escape: only "Cancelled." said, the focus back in the tree; then, with the
crate in it worn, its confirmation says it's taken off first, and it is). The more-commands step: `//siminfo`, `//parcelinfo`, `//scriptinfo` answer. The describing step (the test has no
assistant, so it checks the path without one): Ctrl+D in Say says it needs the assistant and names the visitor among
what the lists know, Ctrl+Shift+D names the clothing, Ctrl+D on the visitor in People says who and where, `//describe
more` asks again; with the renderer, SojournDescribe's two pictures of the visitor aren't flat and name it as filling
them, the fixture box's is framed on it, and here gives four views with the visitor among the people around. What the
model makes of them isn't in the test: real descriptions were judged by hand (`docs/access.md` > M5 progress), with a
scratch driver like the test's that builds a poster sign, presses the keys with the assistant running and reads back
what was announced. The review step: three lines from the
visitor, Ctrl+[ twice to the middle one, Ctrl+Shift+M, Ctrl+], Ctrl+Shift+C ("Copied 2 lines", the clipboard left
alone by `SOJOURN_TEST_NO_CLIPBOARD=1`), Ctrl+Shift+[; Ctrl+Shift+T and a line with its time in the history;
Ctrl+Shift+A and the visitor walking out of chat range and back, both said (after the radar's 8 s settling). The
places step: a region by name typed into Go to region (arrival said, position
checked), a landmark made where the tester stood found in the tree and teleported to by Enter, the history newest
first with "here now" and the one before teleported to, the regions around listed, and a route notecard (a note
line and two `//go xyz` lines) played by `//route` to its last point. The search step: people searched, a result's
profile opened and closed, Enter on it opening an IM; a places search said either way; the people area's three lists
named; Pay from a person's Applications key asking the amount (the local grid has no money, so its refusal is what's
said). Menus open on the Applications key's press (`MenuOn`), not by WinForms' ContextMenuStrip, which opens on
WM_CONTEXTMENU and never for posted keys. Ctrl+G isn't (the clipboard is the person's desktop's).
The renderer step (`ACCESS_STEPS=renderer`; the app runs with `SOJOURN_OFFSCREEN_LOSE_AFTER=15`, so its GPU is "lost"
once first): the view runs again with one loss counted and is unseen (the test has no assistant), the visitor's
`facebox` (a box whose script says each touch's face and surface coordinates) is met by `SojournView collide` at its
distance, and two touches through the person's confirmation (Ctrl+N, Enter): one on face 4, the box's side away from
the tester, and a plain one, landing on the face toward the eyes; the box's script reports both faces with their
coordinates. All that unseen; then `saveSnapshot` gives a 1024x576 picture that isn't flat, and the view is drawn
whole from then on (for the next 10 minutes of the run). The app's renderer is light (about 0.2% of the GPU); a run that wants clean GPU timings elsewhere should
still not overlap it.
The walk-mode step (`ACCESS_STEPS="walk mode"`, about 4 minutes; after the faked GPU loss so it doesn't fall mid-walk):
first a seated crossing (the visitor's `kart`, a physical LSL car, near Atelier's east edge where the ground is about
as high as Atelier East's, sat on and driven east by a held Up: the crossing said with whether the seat held; on the
local grid it held, "Now in Atelier East, still on Walk Kart."); then a
course the tester builds in Atelier East (a 3 m corridor north with a stone floor, a 1.4 m opening in its west wall,
its end a wall named Blue Door; a platform 3 m up; a closed pen), cleared through the grid console after: F3 and the
focus on the walk pad, F1, H, S's words (walls both sides at a metre, the end 11 m ahead, the opening at twelve
o'clock about 1.4 m wide, the corridor's mouth behind), a held Up (posted key down and up: QuireUia's `keydown` and
`keyup`) walking to the end with the opening passed on the left said between 5.5 and 9.5 m along, the warning naming
Blue Door before 11.2 m, Blocked at it, stone underfoot and cue sounds sent; a step turn each way, A, T (touched),
Space, L; the drop said walking off the platform while still on it; Escape back to Say. Driving: the visitor's `cart`
(its script takes the sitter's keys and moves itself), sat on by //sit, H says east, a held Up reaches the script and
drives it east, V says its speed, a tap of Left reaches it, A doesn't take the cart for what's ahead. Paths: `//go`
from inside the corridor to beyond its west wall goes round (and ends within 1.5 m), the assistant's walk back in
goes round too, `//go` into the pen teleports there. Following: the visitor walks off 15 m and stops, "With Walk
Visitor" is said and the tester is within 4.5 m of it; it teleports away and "gone ... stopped following" is said.
The app runs with world audio on, to a file (`SOJOURN_VOICE_OUT_FILE`: local grid only, never the speakers), and
`SOJOURN_WALK_LOG` (`local/access-test/walk.log`).
The sound-as-a-map step (`ACCESS_STEPS=sound`, about 2 minutes, needs no renderer): by `SojournWalk status`' record of
the cues sent, the defaults; an IM's bell, centred, and none for one in the conversation you're in; a script menu's
and an offer's; nearby chat silent by default, then (turned on) placed within 25 degrees of the visitor's bearing; an
Alt+0 change playing the IM's bell at the new level and cycling back; then at a spot of its own with a bench, a gate
and a scripted bell 4 m ahead, right and left: O's words by clock face and each sound placed within 20 degrees; a
`//beacon` on the visitor pipping from its bearing, quietly; one on the bench brighter ahead, walked to by a held Up
until "Reached"; a `//go`'s and the assistant's walk's destinations on the beacon while they last and silent after.
It can't say whether they sound right.

The M2 steps. Every part (`ACCESS_STEPS="every part"`): the visitor's `treasure` (a chest whose unnamed lid alone has a
script), its four parts listed under it (the root by name, the lid by number and where, the latch by name and nearer
you, the foot as a tiny box below, only the lid marked touch), Tab to them named for the chest, Enter on the lid heard
by its script (not the root's), Shift+Tab and Tab leaving the lid chosen, Tab to its faces (with the renderer: "The
top, face 0" and the side facing you, Enter there heard as face 0; without, the list says it needs the view), `//touch
treasure chest` reaching the lid, `//touch treasure chest latch`, and with the renderer `//touch ... face top`. Your HUDs:
a two-prim HUD made by the agent API (its button's script put in through the confirmation by keys), worn on HUD Center;
Ctrl+Shift+W and `//outfit` name it among the HUDs; Show: Your HUDs lists it with its parts; Enter on Play heard by its
script on link 2. Placing by words: a block rezzed "a metre to my left" by the Applications key's S (within 0.3 m,
resting on the ground), then moved "by 50 cm up" by Objects' M. Two inventory panes: Alt+4 twice, a folder searched in
the second, two notecards marked in the first and moved there by Ctrl+M, Ctrl+W closing it. Incognito: Ctrl+Shift+I on
(said, the title without the region), SojournSettings refusing it off, a visitor's line read out but not in the
session log, Alt+0 showing it, Ctrl+Shift+I off. Voice keys: without voice (`--no-voice`, the test's default) F4 and
Ctrl+Shift+V say it isn't running; `ACCESS_VOICE=1` (with `tools/grid.py voice up`) checks F4 held opening the mic and
letting go closing it, Shift+F4's modes and Ctrl+Shift+V.

`ACCESS_ACCOUNTS=Walk` runs the test as Walk Tester and Walk Visitor on ports 10 up, so it never throws out a person's
guided session as Access Tester; it still uses Atelier's spots, so run it while nobody's testing there.
`access.py test` refuses to start while its tester or visitor is on the grid (two runs kick each other out).
The one failure: UI Automation doesn't list the conversations' tab items, and of the areas' it lists all but the first
and last (WinForms' TabControl provider, as our client sees it). The focus never needs the strip (Ctrl+Tab and Alt+2
land in the input, named for the conversation), and NVDA reads Win32 tab controls through MSAA, so it may not matter:
the guided test asks. Lessons from the Radegast tools hold: no console windows (Windows 11 puts them in the visible
Terminal), one stable binary path (dotnet.exe), UIA doesn't cross desktops, Git Bash turns a leading `//` into `/`
(`MSYS_NO_PATHCONV=1`).

A real NVDA run needs the visible desktop: the portable, silent NVDA in
`projects/radegast/local/nvda-portable` (speaks nothing, logs speech at IO level), only while nobody's at the PC, quit
after (`nvda -q`).

## Guided test (with Companion, sighted, NVDA running)

First run 2026-10-29 by the user (sighted, new to screen readers): what was found and done is in `docs/access.md` >
What a blind tester tells us. A second run checks those (the reply box above all).

For a session walking the user through it with Companion (`projects/companion/docs/guiding.md`). Before: the grid up
(`python tools/grid.py status`), `python tools/access.py build`, the visitor bot started (`python tools/access.py
visitor`, Access Visitor at Atelier 68,38), NVDA running with speech on (the user's choice; this is about what NVDA
says). Tell the user Companion's push-to-talk is Right Ctrl; Sojourn Access's own keys use the left Ctrl.

1. Start it: `python tools/access.py run`. The login form: Tab through it. Ask: did NVDA say each field's name once
   (Grid, Username, Password, Show password, Start at, Log in), each list as one combo box with its choice?
2. Log in as Access Tester (`sojourn-local`). Ask: did NVDA say "Logging in as Access Tester.", then "Logged in, in
   Atelier. F1 for keys." (F1 said once)? Is the cursor in the Say box?
3. Type `hello` and Enter. Have the visitor answer (`python tools/access.py visitor say hi back`). Ask: did NVDA read
   "Access Visitor: hi back" by itself, while the focus stayed in Say?
4. Shift+Tab once to the history and arrow up and down. Ask: does it read line by line, and does a new line
   (visitor says something) leave your place alone?
5. The visitor IMs you (`visitor im hello`). Ask: did NVDA say "Access Visitor, new IM: hello" and did the focus stay
   put? Then Ctrl+Tab: "Message to Access Visitor"? Type a reply and Enter. Shift+Tab to the tab strip: does NVDA
   say the conversation's tab name? (UI Automation doesn't list it.) Ctrl+Tab back to Say.
6. Alt+3. Ask: does the list say "Access Visitor, 4 metres, two o'clock" or similar? Is the clock direction right
   (the visitor stands where the bot was put)? Applications key: the menu reads? Choose Face; then Alt+3, F5: is
   the visitor at twelve o'clock now? Alt+0: settings; Enter on one changes it (put it back).
7. Have the visitor's box send a menu (`visitor fmenu`). Ask: did NVDA say "Access Fixture: Access Menu: pick one.
   Control N to answer."? (Pressing a key while it speaks cuts it off; it's said again when you pause.) Ctrl+N (the
   item reads its keys: 1 Red, 2 Green, 3 Blue, 4 Block), then 2. "Green, done." and "No notices waiting." once? Did
   the box say "picked Green"?
8. `visitor ftext`: Ctrl+N, Enter (to the reply box), type a word, Enter. Heard "Reply, done." and the box's "typed
   ..."? No Windows ding? (This was refused the first time: NVDA passes Enter on from edit fields itself.)
9. Type `//go xyz 66,40` and Enter. Ask: "Walking 4 m to 66, 40." then "Finished walking, under a metre from the destination."?
10. Type `//find access` and `//status`. Ask: are the words clear? Too long?
11. F6, have the visitor talk: silence? F6 again. Ctrl+[ a few times (Ctrl and the left bracket key, right of P; not
    the equals key by Backspace): does it go back through what was read?
12. Ctrl+G with `secondlife://Atelier/80/40/24` copied (Companion can't copy for them; type it in Notepad and copy):
    "Teleporting to ..." and "Arrived in Atelier, at 80, 40, ..."?
13. Alt+4, the inventory: arrow through it, Right opens a folder. Ctrl+F, type `shirt`, Enter. Ask: did NVDA say how
    many were found, then read the item as "..., shirt, worn, in Clothing" or similar? Enter takes it off ("Took off
    ..."), Enter again puts it back on: arrow off and back, does the item say "worn" again? The Applications key:
    does the menu read? Escape. Space on two items: "2 marked", and each item's words end "marked" (no "check box")?
    Space again to unmark.
Walk mode (its sounds play on the PC's speakers: say so first; a quiet room helps):

14. F3. Ask: did NVDA say "Walk mode. Arrows walk, F1 for its keys."? F1: the keys read? H: "Facing ..."?
15. S, the sonar. Ask: did the words come at once and the twelve pings go round from ahead to the right, higher
    where something's near? Could you tell from the sound alone where it's open? Too long, too loud?
16. Hold Up a few seconds toward a building (Atelier's are around 70-100, 40-70; A says what's ahead first). Ask: are
    the footsteps there and steady, and do they stop when you let go? Did the warning ("Wall, 2 m.") come early enough,
    and "Blocked." when you reached it? Any "Opening on the left/right." passing a gap? Too chatty?
17. Taps of Left and Right: a step each, "north-northwest." and so on? Hold Left: a smooth turn, where you face said
    when you let go? Space: "Stopped."? Escape: "Walk mode off." and the focus back where it was?
18. Type `//go xyz` to a spot on the far side of a building (A tells you one is ahead). Ask: "around something in the
    way", each stretch said, and "Finished walking ..." at the end?
Parts and the rest of M2 (before step 19; `python tools/access.py visitor treasure 66 40 23` puts the chest out):

- Alt+7, arrow to Treasure Chest, Tab. Ask: are the parts clear ("Part 2 of 4, a box above the root, touch")? Enter on
  part 2: did the box say "chest lid touched"? Tab: the faces read by direction? Enter on "The top": face 0?
- Alt+4, Alt+4 again: "Second pane"? Alt+4 back and forth, Ctrl+W closes it. Ctrl+Shift+W: what you're wearing, clear?
- Ctrl+Shift+I: "Incognito on."; Ctrl+Shift+I again: "Incognito off."? F4: what does it say (voice on or off)?

19. Alt+F4: "Log out and quit?"; Escape stays ("Still here."), Alt+F4 and Enter quit. `python tools/access.py visitor
    stop`. Ask what felt wrong, slow or chatty; record it in `docs/access.md` > What a blind tester tells us (sighted,
    with NVDA: say so), and take the check off the board.

## Guided test of a release (Second Life, as a tester gets it)

What `package --check` can't reach: the release as downloaded, Windows' own warnings, and the packaged build on Second
Life. Use the newest release on studio-quire/sojourn-access; the user's own SL account; NVDA speaking. Their password
and any MFA code are theirs to type: before that step, NVDA's log at its default level (no input logging) and our
own input logs off.
1. From github.com/studio-quire/sojourn-access, follow the README as written to Releases and the win-x64 zip. Ask:
   did the README's directions match the page (Assets heading, the file's name)?
2. Alt+Enter on the zip, Unblock, unzip (Downloads is fine). Enter on SojournAccess.exe. Ask: any "Windows protected
   your PC"? (Unblocked, there shouldn't be.) Did the login window's focus land in Username with Second Life chosen?
3. Log in (their typing). Ask: did Windows Firewall ask, and what was chosen? An MFA code or the Terms, if asked, in
   their small window? "Logged in, in ..." and the cursor in Say?
4. `//about`: the release's version and github.com/studio-quire/sojourn? F1 read out?
5. A short round where people are: Alt+3 (a person read with distance and clock direction), Alt+7 (the first object
   read without arrowing), Ctrl+D (says once that this build has no assistant, then what the lists know), F3 and S (the
   sonar), Escape. Music, if the parcel has any: under NVDA at its start level?
6. Alt+F4, Enter. Windows+R `%LOCALAPPDATA%\Sojourn Access`: the logs there, nothing new beside the program.
7. Removing it as the README says (the folder and the data folder). Record findings in `docs/access.md`, take the item
   off the board.
