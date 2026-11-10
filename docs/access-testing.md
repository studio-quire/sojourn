# Sojourn Access: testing

Written by Claude Opus 5.5, 2026-10-29 (split out of `docs/access-app.md` on 2026-10-31). Agent-facing; the guided
tests at the end are for walking the user or a tester through it. How the app works is `docs/access-app.md`; what
testers said is `docs/access.md` > What a blind tester tells us.

## The scripted test

`python tools/access.py test`: on the local grid, nothing on the visible desktop; it builds the app and the visitor
first when their sources are newer than their builds (QuireUia only by `python tools/access.py tools`).

**Which steps to run** (Claude Fable 5.2, 2026-11-09). The steps below are a menu: run the ones a change could break
(the studio's `docs/agent-briefs.md` > Checking in proportion). `ACCESS_STEPS=WORDS` picks them by words in their names,
`=NAME` one by its whole name; `python tools/access.py steps [WORDS]` lists the steps with how long each took when it
last ran on this PC, and what a choice of words adds up to. Which steps a change touches is the session's own reading
of the names and the steps' docstrings: nothing works that out. A run of some steps costs about half a minute around
them (some 20 s to the first step, 10 s to close; it was 100 s). Each step's checks follow a `(step: NAME)` line, and
the run ends with the steps that had failures, which are the words for running those again (a name with commas in it
alone, as `=NAME`: as words its pieces take other steps too). A full run is about 72 minutes
(2026-11-09: stopping a vehicle 7, walk mode 4, the building steps 1.5 to 3 each; 74 before that day's faster start,
72 worked out from a run cut short at an hour, which had spent 56.5 minutes in the steps the one before spent 57.4 in,
with a minute less around them). It earns that when a change sits
under most steps (the main window's plumbing, the reader, confirmations and Notices, the session host and Sojourn.Core
under it, whichever app the change was made for; QuireUia's server; the test's helpers for keys, finds and selections)
or a lot has gone in since the last one; otherwise a build for a tester needs the steps of what changed since the last
full run, and `package --check` for the packed files. The test's start, its reading of the log and its end run the same
under any step, so a change there is checked by a few steps (one that starts the app again, and `ACCESS_FRESH_SECS=20`
for QuireUia started afresh between them). What a run of some steps doesn't show: the app an hour into one session
(2026-11-07's stall came only late in full runs), and what one feature leaves for another (a tab open, a mode on, the
focus elsewhere), so a new step hasn't met the others until a full run has had it. And what only a step run alone
meets: a list's first fill of the session, held back 3 s (auto-sit's walk down Objects started under it and failed 2 of
2 alone, having passed in every full run; `select_in` now waits for the fill).

Last full run 2026-11-09, 19:57, on main at `7144fc3` (teleports within a region handled in turn, a `//go` that ends
when it gets no nearer, the test's faster start), in two sessions: 2234 of 2235 through 85 steps and most of stopping a
vehicle, an hour in one session, when the grid's process ended under it (nothing of a shutdown in `OpenSim.log`, no
crash on Windows' record, and the same trial passed after: not explained); then that step and the twelve after it in a
session of their own, with the one step fixed, 300 of 300. The miss was the test's: World > Away read "on" after "Not
away.", away by itself having come on 2 s before (How it runs, below). The run before, 03:45 that day, on main with
the estate, a group's settings, auto-sit, export and import, and Point at: 2438 of 2440, both misses timing (Tab to the
lid's faces before they'd come, the very next check finding them; a picture's colour before its texture). The run
before 0.1.0.1045: 2192 of 2193 (a group's powers coming after a title's change, since asked again). The night before,
2026-11-08, on main after that day's five merges (0.1.0.1026): 2027 of 2039, no stall, and the IM sound window that took 2026-11-07's keys
not seen again. Its twelve failures were all the test's own, fixed that night: "Logged in" unheard as `--autologin`
raced QuireUia's first contact with the process (now a login by keys, below); the long line's accented words
re-encoded by a scripted edit, so posted typing lost a character in each sentence; earlier runs' leftovers (now swept,
below) in front of two tree walks and under the builders' box; a confirmation pressed at a fixed 1.5 s that a slow
first upload hadn't asked yet; `near_visitor` teleporting her onto the visitor, where the lists rightly said "under a
metre, below you" to checks wanting metres, and Tabbing into People nearby before it had filled after the teleport;
the group step's chat left open, so the sound map's Control+Tab landed there. Those steps and their neighbours then
passed, 648 of 648 on the default accounts; no full run since.

**The test client's references.** Every element QuireUia's managed UI Automation client (System.Windows.Automation)
hands out stays a reference in the app's UIA server for as long as QuireUia is connected: GC and finalizers don't let it
go. The server keeps them in a list and walks the whole list for each reference whenever a provider is disconnected,
which WinForms does for every tree item as a tree is emptied (`TreeNode.Remove`), for list items, and for every control
as a window closes. So a long run makes those slower and slower. When QuireUia's `find` walked the whole window, each
find left a reference per element, some 290,000 late in a full run, and emptying the inventory's tree for a search (at
`delete_by_search` after recording a route, three full runs of three on 2026-11-07) held the window for over three
minutes: "the client is busy", every later step failing. The window's thread sat in `UiaDisconnectProvider`, waiting
in a COM call on one of UIA's own threads, which was walking the list at a full core (so it still answered pings, and
looked idle in `dotnet-stack`). Killing QuireUia freed the window at once. Since then (QuireUia, 2026-11-07) a find's
exact parts (`id`, `name`, `type`, `class`) go to UIA as a condition checked in the app, so only matches come back
(`name~` still walks the window, so use it sparingly); `focus` leaves out the ancestors unless asked (`focus up`); and
event senders are let go of at once. A few references per command still stay (measured on a WinForms tree of 300 rows
in a window of its own, reading the server's list size with cdb: 40 whole-window finds left 12,000 before and about 120
after), and with those changes alone the same search still held the window 65 s late in a run. So the run starts
QuireUia's server afresh between steps every five minutes (`fresh_server`; `ACCESS_FRESH_SECS` changes it, 0 never): a
new connection starts with none, and the one closing frees its references at once without holding the window (checked
in the lab with 18,000 held). The app isn't activated again after it, and QuireUia's own small window never takes the
activation (it once did, by the look of it, when a dialog of the app closed: the app was left with no focus and every
later step failed); a run of the profile, IM sound and building steps with the server started afresh before every step
passed but for one check that fails without it too. A COM client that releases its elements, as screen readers do, left none from 40 whole-window walks, so this is
our test client's, not a reader's. Long runs of a stall's kind elsewhere ("Wearing" slow to come back, a notecard's
window slow to close) are worth reading the same way first: `cdb -pv -p PID -c "~*k; q"` for every thread's stack
(WinDbg's cdb; `UIAutomationCore!ObjectReferenceManager` busy on a thread is this).

- `ACCESS_STEPS=inventory,walk` runs only the steps whose names have one of those words (after the login's checks);
  `=links` the step named exactly that (`links` alone takes three; a whole name with commas in it works when it's
  all that's asked). A run of some steps waits for the login's own
  arrivals first (the friends' line, a couple of seconds on) and starts the app as already told about chat logs (said
  once, 12 s after a first login), so neither lands in whichever step is first: the links step run alone found the
  friends' line under the one it goes back to. A full run's first steps take both as before.
- `ACCESS_NO_RENDER=1` starts the app with `--no-render`, nothing on the GPU, for the steps that don't need the view
  (e.g. while someone's in the headset). `ACCESS_NO_GPU=1` hides Vulkan's drivers from it
  (`VK_LOADER_DRIVERS_DISABLE=*`), as on a PC whose graphics can't run the view: it then draws on the processor with
  `local/lavapipe` (`python tools/access.py fetch-lavapipe`; the test hands it over as `SOJOURN_SOFTWARE_VULKAN`), and
  the renderer step checks it's on llvmpipe, draws nothing and stays light while idle (CPU over 10 s; with
  `ACCESS_RELAY`, after the warm-up's first frame), lands touches on
  faces and gets the look picture (timed); the describing step prints each look's seconds. With
  `ACCESS_NO_SOFTWARE=1` as well it gives up instead: the renderer step checks it gave up without drawing and the
  collision model still meets the visitor's box, and the walk-mode step runs on the collision model alone.
  `ACCESS_SOFTWARE=1` draws on the processor with the GPU there (`SOJOURN_OFFSCREEN_SOFTWARE=1`).
- `ACCESS_VOICE=1` (with `tools/grid.py voice up`) runs it with voice; otherwise `--no-voice`.
- `ACCESS_RELAY=NAME` runs the app's assistant on Studio Quire's relay with that token (`access-check` is the tests';
  kept in the run's own folder as `SOJOURN_KEYS`), not `--no-assistant`: the HUD step then has the assistant read a
  painted panel's buttons and describe it (a few tenths of a cent); the describing step's no-assistant checks are
  skipped; the builders' step runs on the relay (below). `ACCESS_STEPS=HUD` runs both HUD steps.
- `SOJOURN_BUILD_ROOT=DIR` (absolute) builds and runs the app from a tree of its own, and `tools` builds the visitor
  there too (`DIR/bin/Visitor`), so a second session can test while another run holds the usual binaries open.
- From a git worktree (an agent's branch, say `local/wt/NAME`, which has no `local/` and must never get a junction to
  it): run the worktree's own `tools/access.py` with `SOJOURN_LOCAL` set to the main tree's `local` (absolute), plus
  `SOJOURN_BUILD_ROOT` and `ACCESS_ACCOUNTS=Walk` so it doesn't collide with a run from the main tree. `grid.py` and
  `relay.py` read `SOJOURN_LOCAL` too, and so does the build (`src/Directory.Build.props`' `SojournLocal`, for voice's
  `sojourn_voice.dll`, without which world audio is silent and its checks fail).
- A run with `SOJOURN_BUILD_ROOT` keeps its logs in a folder named for it too (`local/access-test-walk-NAME`), so the
  next agent's run doesn't empty them.
- One scripted run is on the grid at a time, whatever its accounts (they share Atelier's spots and fixtures): a run
  holds `local/access-test.lock` and another refuses to start, or waits its turn with `ACCESS_WAIT=1`.
- `ACCESS_ACCOUNTS=Walk` runs it as Walk Tester and Walk Visitor on ports 30 up (10 up are the Radegast tools' own
  QuireUia and visitor: a run beside a Radegast session once took its server for ours), so it never throws out a person's
  guided session as Access Tester; it still uses Atelier's spots, so run it while nobody's testing there. The test
  refuses to start while its tester or visitor is on the grid (two runs kick each other out).
- The app's renderer is light (about 0.2% of the GPU), but a run that wants clean GPU timings elsewhere shouldn't
  overlap it.

**How it runs.** The app runs on a hidden Win32 desktop (`tools/quireuia`, from the Radegast contributions' tools:
`launch` starts a program there, `serve` records the focus, alert and notification events a screen reader gets and
drives the app by UI Automation and posted keys; a key's modifiers go into the app's key state, only those asked for:
attaching to its input resets that state, seemingly from the real keyboard's, and a run of 2026-11-08 got Alt+Shift+4
for Alt+4, most likely a Shift held on the visible desktop just then). That is only half closed: the state is reset
again when QuireUia lets go after a key with a modifier, and a key posted with none (Tab, F5, arrows) is handled under
whatever that left, until the next key with one. Suspected, not shown, in two runs of 22 of the places step
(2026-11-09) where the Tab after Alt+6 and F5 went backwards to Regions around once and nowhere once, as Shift+Tab and
Alt+Tab would, with someone typing at the PC; nothing else differed in the recorded events up to that key. In test
runs the app now logs each such key as it arrived with what was held (`key Tab, Shift` in its session log), so the
next one says which it was. If it is this, the cure is for the key's modifiers to travel with it (a message the app
sets its own key state by in test runs), not to be lent through the shared input state. Nor are posted keys the PC's
input, which is what tells the app someone is there: from five minutes into a session the tester is away by itself, for
the rest of the run, and set away again within a second of being taken off it. A step that sets Away itself turns
`away_after` off meanwhile (the menu bar's, which reaches it at about the five-minute mark of a full run and failed
there, 2026-11-09). With:
- `SOJOURN_TEST_INPUT=keys`: posted keys judged as the person's hardware presses, every other rule of the real-input
  guard applying, so Ctrl+N then a number and a confirmation then Enter are checked against it (`ACCESS_TEST_INPUT=1`
  lets every key through instead);
- `SOJOURN_TEST_NO_NVDA=1` (on a hidden desktop NVDA's controller client claims NVDA runs, so speech would go nowhere);
- `SOJOURN_TEST_NO_CLIPBOARD=1` (the clipboard left alone);
- `SOJOURN_TEST_LIST_DELAY_MS`: the lists' first fills held back 3 s, as Second Life's objects wait for names.

A bot (`tools/visitor`, Access Visitor) talks, IMs, and owns scripted fixtures: a box sending menus, llTextBox,
permission requests and offers; `facebox` (says each touch's face and surface coordinates); `treasure` (a chest whose
unnamed lid alone has a script); `sofa` (renames itself while sat on or on a touch); `kart` (a physical LSL car; with `throttle`, a throttle train that says its track ends past `endx`, and with `hover 1` shows its throttle and speed as floating text; `lose N`, `start P` and `quiet 1` for the stop's trials) and `cart` (its script takes the sitter's keys);
`switch` (a rail switch as the SLRR standard has one: a "Switch Stand" whose description says Main or Branch, a touch
part, two guide rails that swap their names and phantom; a touch or Main and Branch on its channel set it); `rails`
(guide rails laid as given, "Guide" solid and "Alternate Guide" phantom, no scripts) and `stand` (a scriptless "Switch
Stand" with a description), for a track to follow. Menus
open on the Applications key's press (`MenuOn`), not by WinForms' ContextMenuStrip, which opens on WM_CONTEXTMENU and
never for posted keys. Before it logs in, whatever it owns in Atelier and Atelier East goes (an interrupted run's
leftovers: a second fixture box obeys its words too, and every notice step then gets two of each).

The tester is swept the same way before she logs in (`sweep_tester`): her objects in Atelier, Atelier East and Atelier
Northeast, and in the first two the other account set's tester's and visitor's while they're offline (Access or Walk:
both use Atelier's spots), and in her inventory (the grid's `inventory.db`, she being offline) everything but a new account's own (the
system folders, the Default body parts and clothes and Current Outfit's links to them), her calling cards and the
Routes folder; the Trash and Lost and Found are emptied. Steps still leave some of what they make (about 25 things a
full run, more when one is interrupted), and by 2026-11-08 some 1,000 had piled up in Access Tester's inventory, so a
walk down the tree's top level never reached the run's folder. So nothing a step meets comes from an earlier run, and
a person's guided session as the tester keeps nothing past the next scripted run. Walks down the tree's top level go
by `select_top`, which closes any folder an earlier step left open on the way.

She logs in from the login form by keys (the form filled in from the command line, then Enter), not by `--autologin`:
that logs in the moment the form shows, the session's start then holds the window's thread for seconds, and a UI
Automation client that hadn't met the process yet missed "Logged in" said as the main window opened (2026-11-08). A
person's reader has read the form first.

Two more bots, Access Palone and Access Paltwo (`PALS`; Walk's with ACCESS_ACCOUNTS), log in in Atelier Northeast
before the app does, out of every list's way: the tester's friends (made by the first run's friends step, which they
accept, and kept on the grid), so the grid tells of them at login.

**The steps**, by the names `ACCESS_STEPS` matches (in run order; each step's function in `tools/access.py` has a
docstring with its checks in full):
- **the outfit while it arrives** (first, within two minutes of the login): an object linked in Current Outfit but not
  attached (the link written into the grid's inventory database, as another session's Wear leaves one, and read by F5:
  the link op refuses Current Outfit) said as still arriving by Ctrl+Shift+W and in the inventory's words, worn again by Enter, then attached.
- **friends online at login**: the pals (and the visitor, if a friend) said as one "N friends online: ..." line, none
  alone, the history naming all; after the burst a pal logging out and in is said alone. The first run makes the
  friendships and checks this from the next.
- **the window's controls** (again after use), **Tab order**: every area's controls named with their roles; Say,
  People nearby, the history, Say; no tab strip a stop.
- **nearby chat in / out**, **her own lines read back**, **lines cut by typing**, **an IM**: say, whisper, shout; her
  own lines read back (and not, with the row off); lines cut by posted typing said again together after a second's
  pause, once, not after Control alone, not one heard out before the typing, not her own; busy chat (the visitor
  talking every second or so for 20 s while a thread posts keys every 0.15 s, a line of her own sent halfway): each
  line said at most twice, the cut ones said again in one go after the pause, the newest three after how many there
  were, her own read back once; an IM's tab arriving without taking the focus, Ctrl+Tab and Ctrl+W.
- **a long line in parts**: about 2,600 bytes typed in Say and put in an IM's box whole (UI Automation's SetValue: the
  posted characters pass through the ANSI code page, which loses three-byte ones), every sentence with non-ASCII
  characters: the visitor gets every part, in order, all the text, none over 1000 bytes, each ending at a sentence's
  end; "Sent in N parts." said and the parts not read back; a line of 13 parts waits for Enter again, then all go.
- **the builders' tab** (`builders`): without ACCESS_RELAY, Control+Shift+B's tab saying how to reach us without a
  token, Enter saying it again and keeping the text. With it, on the live relay with that token (its messages deleted
  after): a reply posted before the app started said once after logging in, the introduction the first time, Enter
  sending ("Sent.", her words on the relay), replies posted by the admin path read in the tab without a sound and in
  Say as an IM's with its sound, the focus staying, the reply marked delivered, Control+W and Help's item, the setting
  off. The app asks the relay every 3 s in the test (`SOJOURN_BUILDERS_POLL`).
- **people and objects**: bearings measured against positions; arriving before the first fill (held back 3 s), the
  list says "looking"; once filled, its own item the only one focused and the last focus event, by UI Automation and
  MSAA; the `sofa` renamed by Touch from the list: unchanged while she's in it, its new name on F5 (the item she's on,
  read), its parts named for it, and renamed back by Enter on its part, followed by the list while she's in the parts.
- **people beside chat, friends on Alt+2**: Tab from Say into People nearby, its menu and Control+D, the list left
  alone while the visitor walks off and current again from Say; Alt+2 to Friends; View's Friends and Conversations;
  Control+Tab with none open.
- **names a screen reader can't say** (the app runs with `SOJOURN_TEST_NAMES=1`: the local grid has no display names,
  so `SojournTestNames display` gives the visitor one in mathematical bold script with a sparkle): its line read as
  "Marigold: ..."; `//find marigold` and People nearby by the plain name; Alt+0's People's names row read and stepped
  by Enter, said, and the next line "Marigold (Walk Visitor): ..."; the username alone; as written; a display name of
  symbols alone said as the username; `//profile marigold` naming them so, with the display name as written. Put back
  after. The folding itself: `tools/namecheck` (`docs/access-app.md` > People's names).
- **the renderer out of sight** (the app runs with `SOJOURN_OFFSCREEN_LOSE_AFTER=15`, so its GPU is "lost" once first):
  back with one loss counted, unseen; `facebox` met by `SojournCollision collide` and `SojournView collide` at the
  same distance; two touches through the person's confirmation (one on face 4, the far side; a plain one on the face
  toward the eyes), the box reporting both; then `saveSnapshot` gives a picture that isn't flat, and the view is drawn
  whole for the next 10 minutes of the run.
- **keys**: F1's window (every key, Escape back to Say), Ctrl+Backspace, Ctrl+R, F6 muting.
- **the assistant's last answer**: a two-paragraph answer put on the panel by the agent API, its paragraphs on lines
  of their own in Alt+9's history, Ctrl+Shift+L saying it copied every word (the count is the check: the clipboard is
  left alone), Read-out's item with its key.
- **the menu bar and the review's new keys**: the World, View and Help menus with each item's key shown (QuireUia's
  `get` gives UI Automation's access and accelerator keys), items doing what their keys do, the context menus' keys,
  Control+1 to 9, a notice's keys on its Applications key, Escape from Notices back where Control+N came from,
  the no-conversations line, Control+Page Up and Down, the StatusBar with the location. Alt alone and F10 aren't
  pressed (WinForms opens the menu bar for them only in the foreground window, which a hidden desktop never has).
- **settings**: Alt+0's groups: Page Down through all of them, each heading said and selected and the list named for it, round
  both ends; the list of groups a Tab away by arrows and a letter, back by Tab and Enter; a heading's Enter, Left and Right;
  back on the same row after leaving, and Alt+0 pressed there; every registered setting with a row in a group or left out on
  purpose, Everything being all the rows and only those, A to Z. Then the rows (no two starting with the same three words,
  Right and Left stepping), SojournSettings
  refusing to lower `access_answers_without_key`, the music and world sounds starting at 0.15 and 0.5 (applied once),
  the music's volume stepped, `//status music`; the answers-without-a-key row and its description in plain words; the
  sound output row stepping the default and the first device, Ctrl+Shift+O (world audio goes to a file whatever is
  chosen, so the PC's devices are never played to).
- **my profile, notes**: `//profile`, its boxes changed and saved by Control+S and read back from the grid; Escape with changes asking; a picture uploaded from the window, its fee confirmed and the
  focus back there; chosen by name, the first row a picture and the last taking it off; a texture's Use as your profile picture; a pick made, read, its words changed in place, deleted through Notices
  and back in the window; notes saved as the window closes, there on opening again, emptied through Notices; an interest set alone leaving the others; one too long refused in words.
- **chat logs**: nearby chat and an IM in their files, with the date's line; a conversation closed and opened again starting with its earlier lines; a spell of incognito in neither the files nor a reopened tab; conversations only, and off, written to no file but still remembered this session; Read-out > Chat logs reading one at its newest line, read-only; Delete through Notices; the assistant's rules naming them.
- **search's further pages, people further off, links in words**: Category and Day only where they apply and a Tab apart; a page of one (`SOJOURN_TEST_GROUPS_PAGE`) with its last row, Enter on it; a friend next door listed at 512 metres and not at 96; a parcel's link and a place's said and written as their kinds and names, and Enter on the line still teleporting.
- **small things from the gaps**: `//voice mute` with voice off said so; `:waves` as an emote, `:)` as itself, in nearby chat and an IM; `//find` saying every match; Objects' Sort A to Z and back; Stand up on what she's on; Show: Gestures; the status bar's parcel; Help's Newer build? both ways; World's Log out asked and Escape staying, then (the last step) going, a fresh process at the login window with the form filled in, the cache marked before it cleared as that process starts (`cache/render` kept, where the run has a renderer).
- **the session itself: its log, the cache, another window**: `//log` reading this session's log in a read-only window, as
  Send a log would send it (no records as JSON), Escape back; `//clearcache` marking the cache, said with its size, Help's
  item there, `//clearcache` again taking it back; `//newwindow` starting another process of the app at the login window
  (killed again).
- **the window held** (a few seconds): `//holdwindow 2` (the test's own command) logged when it ends and not said;
  `//holdwindow 7` said as the window not answering, then back, and logged (`WindowWatch`).
- **settings typed and in words**: a switch stepped (Group invitations turned away), a number typed (Hover height, its range in
  the question), the keywords typed and in the op, an auto response typed and confirmed by her own key. The settings step also
  checks no row says only the assistant can change it.
- **the quit question**: Alt+F4 asks, Escape stays.
- **a script menu**, **a text box**, **a permission question**: answered by keys; a menu's announcement said again
  when a key cuts it off (not when silenced by Control alone, nor once answered in Notices); a menu of twelve, minus and equals pressing Next and
  Back.
- **a dance machine's menu, ignored**: the fixture's `fdance` menu, sent again after each pick: answered by 1, the
  new one picked by itself; Delete Ignore in its keys, Ignore (Delete) on its Applications menu; Delete ignores it
  (the object hears nothing), said with what's left, the focus on the offer under it, then none.
- **inventory**: fixtures made through the agent API, then by keys only: the tree as an MSAA reader is told it (as
  first shown, after F5, inside a folder), search, a notecard edited and saved, marks, cut, copy and paste, wear and
  take off, Show: Worn (a worn body part's Delete refused at once), rez on the ground, unbox, give through the picker
  and the confirmation, and the run's folders deleted through the confirmation (cancelled once first: only
  "Cancelled.", the focus back in the tree; a worn item in the folder taken off first, as its confirmation says).
- **notecards with things in them** (`ACCESS_STEPS=notecards`; about a minute, no renderer needed): the visitor's
  `embedcard` offers a card holding a landmark and a notecard, written as the official viewer writes them, each at its
  place in the text. By keys: markers in the text, what it holds said on opening, Tab to the list, Enter copying the
  landmark out and Enter again teleporting by it, the notecard copied and opened from its copy, Escape back; Control+S
  keeping both; a marker taken out coming back at the end, said. By the ops: a rewrite with no places in it keeping both
  (the new asset's embedded block byte for byte the old one's, read from the grid's `Asset.db`), `append`, a copy out
  after all that, and the same card in a box through `object.content.save`, confirmed by keys. The card in the box by keys
  (Objects, Contents, Enter): what it holds said and listed, Enter copying the landmark out of it there. Then by keys on the
  card in her inventory: a landmark copied (Control+C) and put in at the caret by Control+Shift+V, its marker numbered apart
  from the one of the same name, its line saying it goes in; the inner card marked by Delete, its line saying so; one save
  doing both, said; the grid's card holding the two landmarks, the kept one's block byte for byte as it came, the window and
  list read again, the new one copying out. Only the local grid: Second Life still has to show a save and a copy out on a
  real card, no-copy items with a shadow_id among them, and a card saved with something put in (OpenSim's copy out goes by
  the item's id in its owner's inventory, not by the card's block).
- **every part**: the `treasure` chest's four parts listed and named, Enter on the lid heard by its script (not the
  root's), its faces (with the renderer "The top, face 0", Enter there heard as face 0; without, the list says it needs
  the view), the object's line saying touchable and the menu's Touch landing on the lid, `//touch` by name, by part and by face.
- **objects: details, the filter, past the nearest, what people wear** (`ACCESS_STEPS=objects:`, with the next; about 2 minutes, no
  renderer needed): the visitor's boxes (`boxes NAME N X Y Z`) until over 60 are in range and one further (`descr`, with a word
  only its description has). The More row, Enter on it reading the first thing added; Control+F's filter finding that one past the
  page, its count said, the list named for it; Alt+Enter's details; a thing not hers offering no Take, Delete or Return, and the
  Delete key saying so; no match said; Escape back in the whole list; the visitor's `pet` worn, listed by Show's third choice, whose
  and where, Touch heard by its script, Pay on its menu and nothing that moves it, its details and parts.
- **objects: a friend's thing, taken, deleted, returned**: a pal (`tp`, `rights ID on`, `rez`) leaves a box she may edit. Take,
  Delete and Return offered; Return with no letter, second from the end, reached by Up twice; its confirmation naming the owner
  and cancelled; Take's the same; Delete confirmed, OpenSim's refusal said. The About Land step returns the visitor's box from her parcel for real.
- **what a friend points at** (`ACCESS_STEPS=points at`; about 2 minutes, no renderer needed): the visitor's box and `treasure`
  chest; Hear what they point at on its menu, said with what to ask of them; the visitor's `point` (a PointAt and a LookAt of
  type select, as the official viewer's right click) said once, with where and the key, the focus staying in Say;
  Control+Shift+P on it in Objects, marked; the chest's lid pointed at said as part 2, a `beam` meanwhile not said,
  Control+Shift+P on the lid in Parts with the chest's line naming the part, Enter heard by the lid's script; a beam with
  nothing selected said; a filter hiding it cleared by Control+Shift+P; a pal's pointing claimed as the visitor's (printed:
  the local grid passes it on); Stop hearing on the menu, then nothing said; `//pointing NAME` saying the last minute's,
  `//pointing`, `//pointing off`.
- **pointing at something herself** (`ACCESS_STEPS=pointing at something`; about 2 minutes, no renderer needed): the visitor's box behind her
  and a `treasure` chest; Point at on Objects' menu, a UI Automation invoke of it refused and nothing sent; Control+P turning her
  to face the box, said, the visitor's `effects` (its `camera on`, or the region holds back beams) getting a PointAt and a
  LookAt of type select at it from her and a beam each second; Stop pointing on the menu then, Control+P stopping it, the
  visitor getting both cleared and the beam ended, no beam after; the lid from Parts (its menu's Point at, the PointAt at the lid itself), then the box from Objects moving it there;
  `//point`, `//point NAME` again (another minute), `//go 4 left` ending it as she moved away, the box derezzed ending it,
  `//point off` with nothing pointed at.
- **one person's worn things and animations** (`ACCESS_STEPS=worn things and animations`; under a minute): the visitor's `pet`
  and `animate dance1`; What they wear on its menu going to Objects named "What Walk Visitor wears", the pet's line with
  where and touchable and no distance, Show saying whose, Touch heard by its script; Their animations naming dance1; Alt+7
  back to things nearby.
- **groups: a title, its notices** (`ACCESS_STEPS=groups:` runs both groups steps; under a minute, no renderer needed): a
  group of hers ("Access Titles", or the account set's first name: groups' names are the grid's), made once and kept on the
  grid; two titles listed by Choose your title, the worn one said so, another worn by Enter and the session's `titles`
  following; About saying it; Receive off and on; the visitor's group (also kept, as the local grid's power to send notices
  goes by the title worn), a notice sent to it read from the archive and Escape back to the list; the visitor invited from
  People nearby (Everyone, when a role is asked). OpenSim's own behaviour on titles and notices; only Second Life shows a tag
  changing over someone's head.
- **groups: running one**: on her group, her Owners title worn and a Greeters role kept there, the visitor invited and joined
  (the visitor's `group accept`) and speaking in its chat: Members... a window, her row "you", Find narrowing it; Enter on the
  visitor, Roles, Eject and Ban first; Greeters given and taken by Roles, each behind her confirmation, the grid following, the
  focus back in the window; Ban saying this grid has none; Escape closing it; Who's in its chat listing them both (the visitor as one who's spoken there: OpenSim's own list names only her); Eject behind
  her confirmation, the grid and the list following; a notecard copied and pasted into New notice, sent behind her
  confirmation, arriving with Keep as its first key and kept, then taken again from the archive (copies counted in
  Notecards); Invite someone into Greeters, its confirmation cancelled; New group up to its confirmation (L$100 said),
  cancelled, nothing made. Bans aren't on OpenSim, so only Second Life runs them.
- **groups: settings and roles** (`ACCESS_STEPS=groups: settings`; about 2 minutes, no renderer needed): her Owners title worn;
  the group menu's Settings and roles a window, its controls named, General's six rows; Show in search stepped by Right and
  Left, Anyone can join and Cost to join (typed) each behind her confirmation, the charter written in its box of several lines
  (Control+Enter), each said back and checked in the grid's `groups.db` (the profile op answers from what was last sent for 10 s
  on OpenSim); a role made by Alt+N, its tab shown and its rows; its title typed; Page Down to Membership; Invite people turned
  on by Right; Give members any role asking first with the viewer's warning, cancelled; Manage the ban list bringing Eject and
  Take members off roles, Eject then refused; the role renamed, its abilities kept; Delete this role behind her confirmation;
  Escape; the members' Save the list (Alt+S) writing its CSV into the run's files folder. Its role and settings put back after
  (and an interrupted run's role, Ushers or Hosts and a nonce, deleted first).
- **scripts: a new one, moving around in it, inside an object, its errors** (`ACCESS_STEPS=scripts:`; about 2 minutes, no renderer
  needed): New script on the Scripts folder and its starting text; Control+L, Control+F, F3 and Shift+F3 round the ends, Control+G,
  F8 with nothing; Control+H's Replace window (Find next by Enter, Replace, Match case finding nothing, Replace all saying how many and
  where, Escape, Control+Z undoing it); Control+Shift+S a copy to a file and Control+O a file's text in place of the changes, asked
  first, then saved (the run's `files` folder as `SOJOURN_TEST_FILES`, for Windows' Save and Open windows); put in a box of hers (the
  script is deleted from nothing: it stays in the test account's Scripts until the next run's sweep), Objects' Contents
  landing in the Content tab's list, Enter opening it from the object, a syntax error saved through the confirmation, back in the
  editor, the errors said and F8 on one; fixed, compiled; Mono ticked with its description, Alt+M turning it off (said, the focus left in
  the text) and a save, the `vm` the grid then reports printed (the local grid says Mono whatever was asked); Alt+R stopping and running it, the grid asked; Alt+E resetting it, its
  `state_entry` heard again by the visitor; Mono back on and Escape asking first; Space stopping and starting it; `//touch` making it say a line on the debug channel,
  read out and in the history, not twice in half a minute, nothing with the switch off, again with it on; the visitor's `whoops`
  never read out. Second Life's compiler has its own wording and numbers its lines from nought, which the code allows for without
  the local grid showing it; a real run-time error there is still to be seen.
- **your HUDs**: a two-prim HUD made and worn (its script put in through the confirmation by keys); named by
  Ctrl+Shift+W and `//outfit`; Show: Your HUDs lists its parts; Enter on its button heard by its script.
- **a HUD that's one picture of buttons**: a painted driver panel (a title and four labelled buttons, uploaded) on one
  thin box worn at HUD Center, its script saying the face and llDetectedTouchST of each touch. SojournHud look draws it
  out of sight (red where the brake is painted); its parts end with the reading line; its faces are only "The front,
  face 4", Enter there heard in its middle; buttons saved as the assistant saves them (one off the panel dropped)
  listed by name, number and place; Enter on two of them and `//touch reverse` heard where they were meant (all four
  presses fit one way the face lies within 0.03 of ST: 0.001 measured); describing it gets its picture. With
  `ACCESS_RELAY`: Enter on the reading line, the assistant's reply, the four labels listed, its Brake landing inside
  the painted brake, and Control+D's description naming the buttons. Earlier runs' test HUDs still worn are taken off
  first (they'd cover its middle). 51 of 51 with the relay on the GPU, 49 of 49 on the processor (`ACCESS_NO_GPU=1`),
  2026-11-01.
- **gestures**: two made and activated through the agent API; a trigger typed in Say playing its gesture (the visitor
  hears its line from her, the clap on her avatar, the trigger never said, her line read back), a replacement said in
  its place, Ctrl+Shift+G's menu with triggers as keys playing one, World > Gestures, the inventory's words, Play and
  Deactivate (its trigger then said as typed) and Activate again.
- **placing by words**: a block rezzed "a metre to my left" (within 0.3 m, on the ground), then moved "by 50 cm up".
- **auto-sit** (about 2 minutes, no renderer needed): a box of hers made Atelier's seat by `//autosit NAME`, the first seat
  turning it on, said, and on the agent API with its region; a teleport to Atelier East neither sits her nor says anything;
  back into Atelier she's sat on it by itself, said; standing up stays stood; a teleport within Atelier doesn't sit her;
  Control+Shift+S off, said, and a teleport there and back leaves her standing; on again, said with its seat; Objects' menu
  on the box has Stop auto-sitting on it. Forgotten and off again after, the box deleted (later steps teleport about Atelier).
  Not run: a login (it would need the app started again) or a teleport home.
- **exporting and importing** (about 2 minutes, no renderer needed): a two-part build of hers exported by `//export NAME` as
  a backup, then `as xml` and `as dae`, each said and written to the run's `files` folder (`SOJOURN_TEST_FILES`); the XML
  LLSD with both prims; the model read back by the app's own upload (`uploadLook`: two parts, triangles); the backup and
  the XML imported by `//import PATH`, each a new two-part object named as hers, said and edited; the visitor's fixture,
  opened in the build editor, refused by `export`, saying why. What it made it deletes. Textures she made aren't in it
  (the build is plywood): a texture's way out is `docs/building.md`'s backup check.
- **building a linked object**, **building: copying settings**, **building: locked, repeats per metre, flip and the Features
  rows** (`ACCESS_STEPS=building` runs them with the first; each checks the region as well as what's said) and **going by name,
  and blocking by name** (`//go` by a number, xyz, person, object and a name alone; `//block`, `//blocks`, `//unblock`).
- **building with her own hands** (about 2 minutes; the faces need no renderer, so it runs the same with
  `ACCESS_NO_RENDER=1`): Control+B's editor and its controls named; by typed commands, a box made 2 metres ahead with
  its underside on the ground, named, sized, hollowed and coloured, each said back once the region shows it; `face top`
  by direction and full bright on it, then all sides saying that face apart; `object` reading its tab; Right and Left on
  Phantom in the settings list; moved by 50 cm up (half a metre higher, said floating above the ground), turned 90 and
  undone; temporary cancelled at its confirmation (still not temporary); a cylinder made a metre to her left, linked to
  the box and unlinked; the faces list by direction; both deleted through the confirmation. Leftovers of an
  interrupted run (named Access Build or Access Leg) are taken into the inventory first and last.
- **uploads** (no renderer needed; the run sets `SOJOURN_TEST_UPLOAD_FEE` so pictures and models are confirmed at L$0,
  a sound's clips at L$10 each against a pretend balance of L$100, and animations cost L$500 on the free grid; its
  files made by `upload_fixtures`): a text file refused in words; a 40-second FLAC (made by ffmpeg where the PC has it)
  read by Windows and offered as 2 clips, then Escape; Build's Upload a file showing Control+U, Escape in Windows' Open window uploading nothing; a PNG
  by Control+U and the Open window (QuireUia sets its File name box and clicks Open), its size, resizing,
  see-through parts and fee said, renamed, confirmed in Notices, said and selected in the inventory; a stereo WAV by a
  folder's Upload a file here, said as stereo made mono, free, landing in that folder and selected in the tree where
  the focus is (from the inventory's tree: over a search left showing nothing new is selected, by design, so a step
  that searches clears it after, `clear_search`); a BVH refused for L$; a glTF cube by `//upload PATH`, its triangles and size, a scale of 2 said, Weigh
  it's land impact, its confirmation cancelled (nothing in Objects) and then confirmed; a 75-second WAV (a tone of its
  own in each 29.9-second part) by `//upload PATH`, offered as 3 clips at L$10 each, L$30 in all, a length of 20 saying
  4, renamed, with the notecard, one confirmation for the set, each clip said as it lands, the result with the new
  folder in Sounds, the three clips and the notecard (listing them in order) in it, and each clip's asset (read from the
  grid's `Asset.db`, decoded by ffmpeg where the PC has it) its part's tone and length. It deletes what it uploaded.
  Not reached: Second Life's real fees, its mesh upload rights, a 2048 picture, the quiet cuts (checked on synthetic
  notes outside the test: each fell just before an attack), a clip failing partway.
- **About Land, her own parcel** (`ACCESS_STEPS=About Land`; about 2 minutes, no renderer needed): Estate Keeper, the
  estate's owner, logs in as a second visitor bot (`land` commands in `tools/visitor`), divides `LAND_PLOT` (24 by 24
  m at Atelier's south-west, away from the other steps' spots) off Atelier and puts it up for sale to the tester for
  L$0; she teleports onto it. By keys: Control+L saying whose it is, that it's for sale to her and where she is on it,
  its controls named; `buy` confirmed in Notices, then hers in the region; its name said back once the region has it;
  `general` read; `where`; Options' Anyone can fly stepped by Right and Left in the settings list (the region checked
  each way); a category, a landing point and routing; the visitor banned for an hour by name, confirmed, the until
  said and in the region's list, the Access tab listing him, Delete on his row lifting it after the confirmation, the
  focus back in the list; an allow cancelled at the confirmation; a box of hers counted on the Objects tab, with who
  owns what; the visitor's box on her parcel, Return offered on Objects' menu, cancelled, confirmed and gone from the region; the covenant and the region read; for sale for L$100 confirmed and then not for sale; a divide cancelled;
  abandoned through the confirmation (the Keeper's again); passes sold by the Keeper and one bought; Escape back to
  Say; `//land where` from Say. After, and before it starts (an interrupted run's leftovers), the Keeper reclaims what
  isn't his and joins Atelier into one parcel again; the box is taken and she's back where she was. Skipped, said so,
  while Estate Keeper is on the grid (`tools/land.py` running).
- **the estate from the keys** (`ACCESS_STEPS=the estate`; about 3 minutes, no renderer needed): Estate Keeper as a second
  visitor bot (`estate manager add|remove`, `estate unban` in `tools/visitor`). Not a manager: the Estate tab says so, `kick
  NAME` says only its owner and managers may, the visitor's menu has no estate items. Made a manager (the region says so at its
  handshake: a teleport to Atelier East and back): the tab says it, its tools listed; the second pal brought beside her, Eject
  from estate and then Ban from estate from its menu, each behind her confirmation, the pal out of the region (logged in again
  after when the kick logged it out); Delete on its row lifting the ban; `estate ban NAME` and `estate unban NAME` on the other
  account set's visitor, offline; `restart` and `send everyone home` cancelled at their confirmations; `message WORDS` heard by
  the visitor; a message over 254 bytes refused in words. After: her manager's place and the bans taken back by the Keeper, the
  pal back at its spot, a teleport out and back so later steps meet her as no manager. Skipped while Estate Keeper is on the
  grid.
- **favorites in the inventory** (needs nothing of the view): against `AisStandIn`, a stand-in for Second Life's
  inventory service that the test serves on a free port and hands the app as `SOJOURN_TEST_AIS` (Core's InventoryMeta
  uses it only where the region offers no InventoryAPIv3, so never on Second Life). Checked: the app reads every
  favourite by itself after logging in; an item's words without and with "favorite"; Ctrl+Shift+F adding one, sent as
  the viewer sends it; a folder's Applications key adding it; Show: Favorites by keys, its count said, the item with its
  folder and no "favorite" on each, the folder as a folder; one marked "in another viewer" (put in the stand-in) absent
  until F5 reads them again; Right opening the favourite folder onto its contents; Enter opening a notecard from the
  list; Ctrl+Shift+F there removing it (sent as an undefined "favorite", as the viewer unmarks) and it leaving the list.
  `ACCESS_NO_AIS=1` runs without the stand-in, as on OpenSim: Ctrl+Shift+F and Show: Favorites say favourites aren't on
  this grid. The stand-in answers in the service's shape but flat (every flag under the top folder): the real
  service's nesting, its 403 on a big subtree, its timing and whether Second Life copies a mark with an item are only
  checked on Second Life (`docs/inventory.md` > Checks only Second Life can do).
- **favorite landmarks at login** (last, as it logs out and in): a landmark of where the tester stands made in the
  Favorites bar (`SojournPeople addFavorite`), `favorite-starts.json` written for the account with its region, Places
  naming the folder "Favorites bar"; then a `//tp` away, the app closed, started again without `--autologin`, Alt+S to
  Start at, Down past My last location and Home to "Access Start ..., favorites bar", Enter, and the tester arriving
  within 5 m of the landmark.
- **two inventory panes**: Alt+4 again reading the first, Alt+Shift+4 opening the second, a search there, Ctrl+M moving
  marked notecards across, Ctrl+W closing it.
- **inventory housekeeping and putting herself right**: F5 asking the grid; a card deleted and a folder (with a card in it) moved
  to the Trash in the grid's own inventory database behind the app (`inventory_db_change`, as another viewer leaves them): both
  still there before F5, then F5 saying something wasn't there any more, the card gone from the store and the tree's search, the
  folder in the Trash with its card; Search by: a card's description word found by nothing by name, "Nothing made by" by Creator,
  "1 found" by Description, and what she made by Creator with her own name, then back on Name; Sort by date on an item's menu
  (the setting following, the newest card first in its folder) and Sort by name back; Expand all by the number pad's star on the
  run's folder, said, and Collapse all from its menu; an object attached to Chest by the point list; Play on
  a sound and an animation from the Library where it has them (the animation seen on her, then `//stopanimations`); `//emptytrash`
  cancelled, then confirmed, a card sent to the Trash by Delete gone; a card in Lost and Found, Empty Lost and Found on its menu,
  `//emptylostandfound` cancelled then confirmed, the folder empty and still Lost and Found; `//rebake`, `//hover`, `//maturity`,
  `//status health` and `//displayname`'s words. It empties the test account's Trash and Lost and Found. Second Life alone shows a
  display name changed, a rating changed, real hover, health sent, and F5 against the real inventory service's replies.
- **folders given, deleted from the tree, links, outfits and landmarks**: a folder holding only two folders with a card
  each given to the visitor (the confirmation counting 2 items and 2 folders, the answer saying what went, the visitor
  offered it); a folder of 43 folders refused before any confirmation, saying 42; a folder deleted from the tree itself
  (not a search) gone from the tree at once, its move sent to `AisStandIn` and waited for; one the stand-in refuses
  (409) said plainly and left where it was, in the inventory and the tree; Paste as link making a link in another
  folder, the card staying; `//saveoutfit` making a folder in My Outfits with a link per thing worn; a landmark's About
  the place (its parcel's rating, owner and size, where it lands, her notes). With the stand-in serving, every move,
  rename and item change in the run goes to it first and then to OpenSim by UDP; the real service's answers to them are
  only seen on Second Life.
- **incognito**: on and said, the title without the region, SojournSettings refusing it off, a line read out but not
  logged, off.
- **voice keys**: without voice F4, Ctrl+Shift+V and Ctrl+F4 say so; with `ACCESS_VOICE=1`, F4 opening and closing the mic with
  its clicks, Shift+F4's modes, Ctrl+Shift+V.
- **people in voice, to act on**: without voice, `//voice people` says so; with `ACCESS_VOICE=1` (the mixer needn't be up:
  then nobody else is in voice and the connection retries; `SOJOURN_VOICE_MIC_FILE=none` keeps the PC's mic closed),
  Ctrl+F4's menu (its people or "Nobody else in voice", then Mute everyone else, Unmute everyone, Reconnect voice),
  Escape, `//voice mute all` and `unmute all` said and in `SojournVoice status`, `//voice reconnect` said with its
  outcome.
- **walking**, **a teleport**, **the assistant's walk**: a `//go`, Ctrl+Shift+H and a `//tp` checked against the
  region's positions; the session's autopilot stopping about its stop distance short.
- **always running** (about a minute, no renderer needed): between TP_TO and PILOT_TO, a `//go` walked, then `//run` (said, the
  setting on) and a `//go` back seen running (the region's run animation on her) and faster than the walk; walk mode's held Up
  running without Shift; `//run` again walking, said, and held Up walking. OpenSim's autopilot turns running off as it starts,
  so the app sends it again after and half a second on (sent only at once, the walk kept its walk animation); 2026-11-08:
  3.2 m a second walking, 5.2 running. Second Life's autopilot is only seen there.
- **places and a route**: Go to region, a landmark made and teleported to from the tree, the history, regions around,
  and a route notecard played by `//route` to its last point. The history's second place has to be
  Go to region's: it missed that teleport one run in five until 2026-11-09 (`docs/access-app.md` > Local teleports
  reported failed), and the step then went on from her start, where the route's first walk goes into the visitor
  standing on the line and ended 7 m past the point (what that walk did: `docs/access-app.md`, at `//go`). The route
  now starts beside its first point whatever came before (teleported there if not, with a line saying so), so a miss
  fails its own check and not the route's.
- **a conference**: the visitor's `conferenceline` (a hand-made packet: dialog 0, a session that isn't the pair's, as Second
  Life sends one): said as a new conference, the focus staying, a tab not called a group, a reply recorded on that session
  with no "Not sent", Control+W; Friends' Start a conference asking who, Escape starting nothing, one alone refused, two
  marked saying why the local grid can't start one.
- **a person's actions, the same everywhere**: People nearby, Friends, an IM's Applications key, Search and the profile each
  offering the same items; a friend's edit right given and taken back by keys through the confirmation; Block and Unblock from
  Friends; `//block` on someone not nearby and `//unblock`, `//profile`, `//friend` by name.
- **search, profiles, pay**: people and places searched, a profile opened, Enter opening an IM, Pay asking the amount
  (the local grid has no money, so its refusal is what's said). Ctrl+G isn't tested (the clipboard is the person's
  desktop's).
- **more commands**: `//siminfo`, `//parcelinfo`, `//scriptinfo`, `//scriptinfo parcel` (the memory, or why not);
  `//thread //pause 60` named by `//threads`, stopped by `//kill`, then nothing running.
- **describing** (the test has no assistant, so the path without one): Ctrl+D and Ctrl+Shift+D saying it needs the
  assistant and what the lists know, `//describe more`; with the renderer, SojournDescribe's pictures of the visitor,
  the fixture box and here framed and named. What the model makes of them is judged by hand (`docs/access.md` >
  M5), with a scratch driver like the test's that presses the keys with the assistant running and reads back
  what was announced.
- **review keys and arrivals**: Ctrl+[, Ctrl+], mark and copy, times in the history; Ctrl+Shift+A and the visitor
  walking out of chat range and back, both said.
- **sound as a map** (about 2 minutes, no renderer needed), by `SojournWalk status`' record of the cues sent: which
  earcon starts for what arrives and where it's placed, an Alt+0 change playing it, the mic's click, O's marks by clock
  face with each one's sound matched to its own item in the line (everything in range, not only the step's three),
  the beacon on the visitor and on a bench walked to until "Reached", a walk's destination on the beacon. It can't
  say whether they sound right.
- **someone's own IM sound** (under a minute, no renderer needed): People nearby's Applications key, IM sound for the
  visitor, the list on the usual sound with each sound played as Down reaches it, the ocean wave chosen and kept by
  the visitor's key; the visitor's IM then plays it at the IM's level and not the usual bells; `//imsound` says and
  changes it; the conversation's Applications key takes it back to the usual (which plays as Home reaches it), and the
  visitor's next IM rings the usual bells.
- **walk mode** (about 4 minutes; world audio to a file, `SOJOURN_VOICE_OUT_FILE`, never the speakers; walk log in
  `local/access-test/walk.log`): a seated border crossing on the visitor's `kart` (said, and a line in the history);
  a course the tester builds in
  Atelier East (a corridor with a stone floor, an opening in its west wall, its end a wall named Blue Door; a platform;
  a closed pen), where both collision models must agree within 2 cm on 33 rays, and then by keys: F3, F1, H, the
  sonar's words, a held Up past the opening and to Blocked at Blue Door, turns, A, T, Space, L, the drop off the
  platform; driving the visitor's `cart`; `//go` and the assistant's walk going round the wall, `//go` into the pen
  teleporting; a club on one floor slab over the ground there (walls, a door, tables, a bar, a stage up three steps, a
  plinth with none): `//go person` from a sofa stands up and walks up the steps to the visitor on the stage, no
  teleport, and to the visitor on the plinth as near as the way goes; following the visitor and losing it.
- **walk mode's warning, its name not in** (under a minute): Second Life sends no names with objects, and the tester's
  own new prims bring theirs, so the visitor puts a Green Gate (`rez NAME X Y Z size SX SY SZ`) on open ground in
  Atelier East and a held Up toward it must be warned of by name ("Green Gate, 4 metres."), not "Wall".
- **distance walked**: three taps of Up said once as one stretch, close to how far the avatar went (the run prints the
  metres a tap), Down said "backward", M and Shift+M, nothing with `walk_distance` off.
- **rail switches** (about 2 minutes, no renderer needed): on a flat stretch of Atelier East found first (and a ray along
  it clear), two of the visitor's `switch` fixtures 30 m apart and its `kart` heading east toward them; seated, in walk
  mode, a held Up: the first said once ("Switch in N metres, set to the main line, North Station.", N 25-60) with the
  switch cue; W saying it ahead with both ways' names; Shift+W touching its stand, "Switch set to the branch, East
  Yard." and the visitor hearing the stand set, the throw's cue; `//switch`, `//switch main` (set back, heard),
  `//switch main` again ("already"); then on past both, the second said once and the first not again. Then along a
  track (`rail_track`, after clearing those off when Atelier East has no second flat stretch): the visitor's rails
  under the ground, straight east under a new kart, then curving left to north; a facing switch at the curve's first
  joint (its branch going on east as alternates, a stand set to Main), a trailing one where a line from the south
  meets the curve (the curve's last rail an alternate: against the kart), and a decoy stand with its rails on a line
  12 m south, in the old straight band and nearer. Stopped, W says the facing switch by its distance along the track
  with "it takes you left; the branch, East Yard, goes right", not the decoy; the status op has the track; a held Up
  holds back the trailing one from far off (set for the other line) and never says the decoy; the log has the
  `track from` and `lines part`/`lines join` lines. `RailTrack.Follow` itself can be run offline on a log's rails
  (with a slack for their rounding).
- `package --check`'s three provider checks fail on a PC with a key in `%LOCALAPPDATA%\Sojourn\secretsssistant` (the
  main PC's since 2026-11-04, from the installed VR app): the saved "claude" gives way to it, while the check's sidecar has
  a LOCALAPPDATA of its own and finds none. Not the build's fault.
- **links** (under a minute, no renderer needed): a web link and a place in the visitor's chat; Control+Shift+K lists
  them, Enter on the web one says it's opening (`SOJOURN_NO_BROWSER`: no browser comes up), Enter on the history's line
  with the place teleports.
- **driving after someone** (about 2 minutes, no renderer needed): on the gentlest patch of Atelier East, the visitor's
  kart, `//follow` with a gap, the visitor ahead to the left: it steers there and stops short without touching them; the
  visitor walks round behind and it turns and goes after them; an arrow takes the wheel back.
- **driving after someone over a border** (about a minute, no renderer needed): the visitor just inside Atelier East, the
  tester's kart 35 m west in Atelier on the strip at its east edge as high as Atelier East's flat; `//follow` drives it
  over: the crossing said with the seat held, then "With" them; nothing said of the keys let go, them getting away or
  lost, or "Blocked."; the session log has the convoy's lines (its crossing too) and the `controls:` line of the kart's
  take; `//stand` ends it, said. OpenSim's word on a script's keys at a crossing needn't be Second Life's.
- **driving after someone lost at a border** (about 2 minutes, no renderer needed): the same kart and strip a row over,
  the visitor walking east from 8 m inside Atelier East as the kart sets off, and `//blindconvoy 0.5 6` (the test's own
  command: the convoy doesn't see them from half a second on, for 6 s, since nothing on the local grid hides someone):
  "Lost sight of ... at the border; going where they went.", the kart crossing into Atelier East while they're unseen
  (by the convoy's lines) and on to about where they were last seen, "Found ... again", "With" them; nothing of jumping
  away, the keys, giving up or "Blocked."; the session log has the loss, with what each region had of them, and the
  finding. Then unseen for good while it stands with them: "...; going to where they were." and "Couldn't find ... again"
  within about 10 s.
- **stopping a vehicle** (about 10 minutes, no renderer needed): on a flat stretch of Atelier East, the visitor's `kart`
  as a car, a held Up, then Space with Up still held: "Stopping.", "Stopped.", nothing said of moving backward, and it
  never moves back (`ACCESS_STOP_CARS=N` runs the car N times, a flake check, and `ACCESS_STOP_TRAIN=0` skips the rest;
  the app's `SOJOURN_TEST_STOP_STALL=MS` holds the window's thread up to that long a tick in three while stopping, and
  `SOJOURN_TEST_STOP_LAG=MS` shows the stop the seat's velocity that much late); then its kart as
  a throttle train (`throttle 5`, on past 0 into reverse, each change said), opened 8 taps of Up (walk mode counting
  8) and stopped by Space, again by `//stop` from Say, then opened 12 and run past the end of its track (`endx`): "Track
  ends: stopping." with the TrackEnd cue, "Stopped.", the throttle's lines ending at 0 and never below, still, never
  moving back, and its later "No Guide found" lines not starting another stop. Then the trials, each a fresh train
  stopped once by Space and once at the track's end, its throttle as the train tells the visitor (`llOwnerSay`): "text"
  (`hover 1 quiet 1 lose 3`: every third press lost on the way, the throttle shown only as floating text, as the blind
  tester's train), "lines" (`lose 3`, said aloud), "none" (`quiet 1 lose 3`, said nowhere the app hears: the count and
  the taps' effect) and "start" (`quiet 1 start 20`: opened to 20% before the arrows, the count short). Each must say
  "Stopped.", end at 0, never go below it and never move back; `ACCESS_STOP_TRIALS=N` (1) runs each kind N times and
  `ACCESS_STOP_KINDS=text,none` picks kinds, and the line after them tallies failures and reversals.
- **walking round people** (about 3 minutes, no renderer needed; `docs/access-app.md` at `//go` > People on the way): in
  Atelier East between the walk course and the route's wall, a closed pen 4 m square with one doorway a metre wide.
  The visitor standing on the line of a 16 m `//go`: gone round, arriving on the point, never nearer it than 0.7 m,
  the visitor where it stood, and said as the plain walk it is to her (under a metre longer: no "around", no
  stretches); `//go person` to the visitor is a plain walk to it, nobody said to
  be in the way, ending beside it (how near isn't checked closely: letting go is a step whichever way she last faced);
  the visitor in the middle of a passage 2.25 m wide between the pen and a block south of it, too narrow to pass it
  in: the way round the block, a couple of metres longer, said with its name and stretches, each stretch said; a wall
  across the south lane with a doorway, the visitor landing in the doorway once she has set out: round the wall's
  end, said with how far it is now ("... going round, 14 metres from here: 3 stretches.");
  a 20 m walk with nobody near says what it always did, and the visitor landing on its line once she has set out,
  15 m along, is gone round, said in one short line with no stretches, at a walk and with Always run on; the visitor
  in the pen's doorway, the point
  inside: told at the start it's only as far as them, the end saying who's in the way and where, she outside
  and it unmoved, nothing said of a teleport (the fallback on), the same again when asked again, and short of it and
  it unmoved again when she runs up; walking round people turned off by World's menu (said, the setting off): the same
  walk says nothing of the visitor, and `//walkround` turns it on again; from further off, the visitor leaving the
  doorway as she walks up: "The way is
  clear now" and she goes on in; and the visitor and her two friends within 0.8 m round her on open ground (the
  friends come over from Atelier Northeast and go back): she sets out, not "Not walking". The walks into the visitor
  that the planning replaced are what the setting off brings back. What the planner does with a way round of a given
  length (the 10 to 12 m it'll go, the under a metre it doesn't say) was asked of it directly, not on the grid:
  `PathPlanner.Solve` called by reflection with slabs for walls, from a scratch program; the figures are in
  `docs/access-app.md` at `//go` > People on the way.
- **recording a route** (about 2 minutes, no renderer needed): in Atelier East, north of the walk course, a wall with a
  doorway; in walk mode R starts recording, held Up walks through the doorway, taps of Right turn east, held Up walks on,
  then a `//tp` into Atelier Northeast; R's state, N's note (Backspace taking it back, N again), Shift+R's name and the
  save said; the ticks counted (with `walk_route` both, their lines name the doorway and the turn); the notecard read:
  its start line, a few `//go xyz` lines, one in the doorway and one at the turn, the teleport's SLurl, the note last;
  then from the start `//route` plays it, reading its notes, and ends within 3 m of where the recording did. The
  notecard is deleted after; the Routes folder stays.
- **recording a ride** (about 2 minutes; pictures and the telling need `ACCESS_RELAY` and the renderer: the app takes
  pictures only for an assistant to tell, so without the relay the step checks it says so and takes none): on a flat stretch of
  Atelier East, three named markers 4 m north of the way east and the visitor's throttle train (`kart ... throttle 10
  hover 1`, its floating text "Throttle N%" and "Speed N km/h"); seated, V and Shift+V say what it shows, `//text` too;
  C starts recording, three taps of Up open it to 3 m/s, V adds "It shows: Throttle 30%", H answers within a second
  four times while pictures are taken; Space stops it and the recording ends by itself 8 s later, said; SojournRide's
  moments every ~3 s, pictures taken (none skipped), the markers among the names passed in order, the floating text
  and the train's lines kept, the pictures JPEG at 640x360 with what's seen in each (`ride-0.jpg` in the run's folder).
  With the relay Shift+C tells it, the markers named in order, and Shift+C again tells more (the telling's tokens
  printed); without, Shift+C says what the recording knows, the markers in order.
- **a roof under the water** (about a minute): on Atelier's seabed (the first ground under 14 m, the water at 20),
  SojournAudio status's `water` is "under" (rolloff 5); a 6 by 6 m slab the visitor rezzes 3.5 m above the seabed
  makes it "under a roof" (rolloff 1, the roof 0.5-3 m up); taken away, "under" again.

**Tab items.** QuireUia's `find` uses the managed UI Automation client (System.Windows.Automation), which leaves out a
WinForms tab control's first and last tab items; the COM client (UIAutomationCore, what screen readers use) lists them
all, as MSAA does. So the test reads tab items by QuireUia's `tabs`, which walks them with the COM client in a process
of its own: in QuireUia's server the managed client has registered its client-side proxies with UIAutomationCore for
the whole process (its WindowsTab proxy takes WinForms' tab controls by class name), and the COM client there saw one
pane per tab control and nothing else. A screen reader's process has no such proxies. (Checked 2026-10-30.)

**Lessons from the Radegast tools** hold: no console windows (Windows 11 puts them in the visible Terminal), one stable
binary path (dotnet.exe), UIA doesn't cross desktops, Git Bash turns a leading `//` into `/` (`MSYS_NO_PATHCONV=1`).

## Input checks

`dotnet run --project tools/inputcheck` checks the real-input guard (`docs/access-app.md` > How it fits): `InputJudge`
with simulated sequences, and `RealInput` itself in a window of its own (posted keys judged as the hand's, a UI
Automation client on another thread reading it as a screen reader does: Ctrl+N then a number, a confirmation our code
focused then Escape and Enter, and moves by a callback or another thread's message still refusing), and that a posted
WM_SYSCHAR or WM_CHAR can't press a mnemonic button. `-raw` after the separator shows a swallowed key never reaching
raw input.

## Checking a package

`python tools/access.py package --check [ZIP]` (default the newest zip; about two minutes). Last results, 2026-10-30:
0.1.0.589 with the assistant, 41 of 41, `--bare` 41 of 41, `--relay` 43 of 43.

The zip is unpacked in a fresh folder under %TEMP% and run on a hidden desktop of its own through QuireUia as Package
Tester (`--local --autologin --reader on --no-voice`, world audio to a file, no mic, the data folder `--base` in the
check's folder: .NET's GetFolderPath ignores %LOCALAPPDATA%, so the default folder itself isn't exercised), with no
dotnet on PATH and %TEMP%, %LOCALAPPDATA% and %APPDATA% pointed into the check's folder. It refuses to run beside the
VR client or dev mode. It checks:
- no .NET loaded from outside the exe; logged in to the local grid; the main window and the focus in Say; arriving;
  F1's keys window; `//about` read out as UIA notifications (naming github.com/studio-quire/sojourn and the data
  folder);
- with the assistant: its sidecar on the package's own Python, its provider checked (a saved Claude account read as
  none chosen), Alt+9's box named for it and Alt+0's provider row offering no Claude account; Control+D's absence said
  once in two presses (with the assistant: why it's off and how to choose);
- chat sent (the grid's echo); the renderer out of sight up and the GPU working for it; `nvdaControllerClient.dll`,
  `sojourn_voice.dll`, `openjp2.dll`, `shaderc_shared.dll` loaded from the package's folder and nothing from the repo;
- no TCP listener at all (a lean build; with the assistant, only the agent API on loopback, at port 9735 there) and
  every socket on loopback (Windows' "Bound" rows of outgoing sockets aside);
- no failures in the log, a clean exit and logout, the package's folder byte-for-byte unchanged, nothing written
  through the redirected folders nor anything named as ours new in the profile's real ones (the GPU driver's own cache
  aside); then it removes everything it made.

`--bare` runs it as on a PC unlike ours: Vulkan's drivers hidden (`VK_LOADER_DRIVERS_DISABLE=*`) and
`sojourn_voice.dll` removed (as on Windows' N editions). It still logs in, reads out, chats and quits clean; the
view draws on the processor from the package's `lavapipe` folder (the log's switch and "up on llvmpipe", the driver's
threads below normal priority, with the assistant what's around drawn once, no renderer failures, `vulkan_lvp.dll`
loaded from there, and the idle CPU and memory printed), and nothing is written outside the data folder (this Mesa
build keeps no shader cache). A try build with it, 2026-10-31: 42 of 42, idle 4% of a core and 937 MB.
`--bare --no-software` removes the `lavapipe` folder too, as packages before it were: the renderer gives up
after three tries, and the words for faces, pictures and sounds are as `docs/access-app.md` > Packaging says.

**Through the relay, end to end**: `python tools/relay.py token new e2e --cap 2`, then `package --check ZIP --relay
e2e`: the token kept as the app keeps it, the provider set to the relay, then a typed Alt+9 request answered (the
region named) and counted by the relay, a Control+D description, and the cap set to a millionth of a dollar for a
moment so the next request hears the allowance is used up in a plain sentence (the cap put back after). Revoke the
token after (`relay.py token revoke e2e`).

Not reached by it: Second Life itself from a packaged build, voice, spoken requests, Windows Hello, SmartScreen and the
firewall's question (the first SL login asks for a new exe: UDP binds every interface), and the privacy and TPV
disclosures beyond the README (`docs/tpv-policy.md` > Data and privacy). Those are the guided test of a release, below.

## A real NVDA run

Needs the visible desktop: the portable, silent NVDA in `projects/radegast/local/nvda-portable` (speaks nothing, logs
speech at IO level), only while nobody's at the PC, quit after (`nvda -q`).

## Guided test (with Companion, sighted, NVDA running)

The user (sighted, new to screen readers) ran it first on 2026-10-29; what was found is in `docs/access.md` > What a
blind tester tells us, and a second run checks those (the reply box above all).

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
   put? Then Ctrl+Tab: "Message to Access Visitor"? Type a reply and Enter. Shift+Tab: straight to the conversation's
   history (the tab strips aren't Tab stops)? Ctrl+Tab back to Say.
6. Alt+3. Ask: does the list say "Access Visitor, 4 metres, two o'clock" or similar? Is the clock direction right
   (the visitor stands where the bot was put)? Applications key: the menu reads? Choose Face; then Alt+3, F5: is
   the visitor at twelve o'clock now? Alt+0: settings; Enter on one changes it (put it back).
7. Have the visitor's box send a menu (`visitor fmenu`). Ask: did NVDA say "Access Fixture: Access Menu: pick one.
   Control N to answer."? (Pressing a key while it speaks cuts it off; it's said again when you pause.) Ctrl+N (the
   item reads its keys: 1 Red, 2 Green, 3 Blue, 4 Block), then 2. "Green, done." and "No notices waiting." once? Did
   the box say "picked Green"?
8. `visitor ftext`: Ctrl+N, Enter (to the reply box), type a word, Enter. Heard "Reply, done." and the box's "typed
   ..."? No Windows ding?
9. Type `//go xyz 66,40` and Enter. Ask: "Walking 4 m to 66, 40." then "Finished walking, under a metre from the
   destination."?
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

14. F3. Ask: did NVDA say "Walk mode. Arrows walk, F1 for its keys."? F1: the keys in a window, line by line, Escape
    back to walk mode? H: "Facing ..."?
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

- Alt+7, arrow to Treasure Chest, Tab. Ask: are the parts clear ("Part 2 of 4, a box above the root, touchable")? Enter
  on part 2: did the box say "chest lid touched"? Tab: the faces read by direction? Enter on "The top": face 0?
- Alt+4, then Alt+Shift+4: "Second pane"? Alt+4 and Alt+Shift+4 between them, Ctrl+W closes it. Ctrl+Shift+W: what
  you're wearing, clear?
- Ctrl+Shift+I: "Incognito on."; Ctrl+Shift+I again: "Incognito off."? F4: what does it say (voice on or off)?

The menu bar (what the hidden desktop can't press):

- Alt alone, then Right and Down: did NVDA say "World" and each item with its key ("Region info, Control+Shift+1")?
  Escape twice back to Say. F10 the same. On an inventory item, the Applications key: "Cut, Control+X"?
- NVDA+End: does it read the location?

19. Alt+F4: "Log out and quit?"; Escape stays ("Still here."), Alt+F4 and Enter quit. `python tools/access.py visitor
    stop`. Ask what felt wrong, slow or chatty; record it in `docs/access.md` > What a blind tester tells us (sighted,
    with NVDA: say so), and take the check off the board.

## Guided test of a release (Second Life, as a tester gets it)

What `package --check` can't reach: the release as downloaded, Windows' own warnings, and the packaged build on Second
Life. First run 2026-10-30 on a lean 0.1.0.510 (`docs/access.md` > The release as a tester gets it). Use the newest
release on studio-quire/sojourn-access; the user's own SL account; NVDA speaking. Their password and any MFA code are
theirs to type: before that step, NVDA's log at its default level (no input logging) and our own input logs off.

1. From github.com/studio-quire/sojourn-access, follow the README as written to Releases and the win-x64 zip. Ask:
   did the README's directions match the page (Assets heading, the file's name)?
2. Alt+Enter on the zip, Unblock, unzip (Downloads is fine). Enter on SojournAccess.exe. Ask: any "Windows protected
   your PC"? (Unblocked, there shouldn't be; NVDA reads its Run anyway button as just "R".) Did the login window's focus
   land in Username with Second Life chosen?
3. Log in (their typing). Ask: did Windows Firewall ask, and what was chosen? An MFA code or the Terms, if asked, in
   their small window? "Logged in, in ..." and the cursor in Say?
4. `//about`: the release's version and github.com/studio-quire/sojourn? F1: the keys in a window, Escape back to Say?
5. A short round where people are: Alt+3 (a person read with distance and clock direction), Alt+7 (the first object
   read without arrowing), Ctrl+D (a lean build: says once that it has no assistant, then what the lists know; with
   the assistant and no provider chosen: says once that it's off and how to choose; with one, a description), F3 and
   S (the sonar), Escape. Music, if the parcel has any: under NVDA at its start level? Somewhere busy (a few minutes
   in), read its cost yourself, nothing for the user to do: working set and private bytes (psutil on its pid) and
   video memory (`gpu_of` in `tools/access.py`), against 965 MB and 312 MB unseen at Firestorm Social Island
   (2026-10-30); and the session log's `render[0] view:` lines (textures 0 resident, unseen).
6. Alt+F4, Enter. Windows+R `%LOCALAPPDATA%\Sojourn Access`: the logs there, nothing new beside the program.
7. Removing it as the README says (the folder and the data folder). Record findings in `docs/access.md`, take the item
   off the board.

## Walking round people on Second Life

Not run yet (written 2026-11-09 by Claude Fable 5.2, with the change). Everything in `docs/access-app.md` at `//go` >
People on the way was built and checked on OpenSim, whose avatars slide when touched; nobody here knows what Second
Life's do, so this is the first real evidence either way. It needs the user's account in Sojourn Access from the repo
(its agent API on, and `SOJOURN_WALK_LOG=path` set: each plan is a `go A -> B, N standing: ...` line there), a session
driving it, and a second avatar whose person has agreed to be stood in the way and, for step 1, walked into (one of
ours if they're willing, or a friend's), on open ground where building is allowed or a doorway is at hand. Where the
other avatar is before and after comes from the agent API (`TesselAgent people`, their `pos`), the tester's own from
`LLAgent getPosition`; ask the other person what they saw too. Eight things, the first three the ones that matter
(the review of 2026-11-09, also Claude Fable 5.2, added to 1, 3, 5, 6 and 7 and wrote 8):

1. **What does Second Life do when she walks into someone?** Once, with walking round people off (`//walkround off`,
   or `SOJOURN_WALK_PEOPLE=0`: walks take no notice of people, as before): the other avatar standing 4 m along a 20 m `//go xyz`. Do they get moved, and how far? Does
   she slide past the point, stop at them, or go round by the region's own doing? Does the other person's viewer show
   a bump notice? How long until "Finished walking", and what does it say? This decides how much the rest matters.
   And how near two avatars come before they touch: walked at the other slowly by hand (walk mode's Up, tapped) until
   one of them is moved or she gets no nearer, how far apart are their positions? On OpenSim that's 0.6 to 0.75 m,
   and the 0.75 m a way keeps (`PathPlanner.PersonR` plus the body's 0.3) is set from it.
2. **Round someone standing** (`//walkround on`, so walks heed people again): the same walk. Wanted: the plain
   "Walking 20 metres to ..." (a step aside for one person isn't said; the walk log's `(round 1)` shows it was
   planned), an arrival under a metre from the point, the other avatar where they stood to within
   0.1 m, no bump notice for them. From positions sampled a few times a second, the nearest she came to them: the
   way's line keeps 0.75 m, a metre where there's room. If Second Life's avatars touch further out than that,
   `PathPlanner.PersonR` grows; if nearer, it can come down, which is what narrow places want (8).
3. **Does it count the right people?** The walk log's "N standing" against who is really on their feet nearby,
   somewhere with a dozen people: are people behind the camera or far off missing (Second Life sends avatars by its
   own rules), and do people sitting on things get left out as meant? A person sitting on the ground: counted?
   And someone who has just left: the other avatar teleporting away from the line, then the same walk at once and
   again ten seconds on. Is "around NAME" still said for someone who isn't there (a copy an avatar leaves behind is
   dropped some seconds after the region's list stops naming them), and for how long?
4. **Sitting people**: someone seated on a pose ball or a chair on her line. We don't go round them, on the reading
   that a seated avatar has no body to walk into: is that true on Second Life (no bump, nobody moved)? If not, they
   count too.
5. **A doorway**: the other avatar standing in one, the walk's point inside. Wanted: "... is in the way and there's no
   short way round, so only as far as them.", then "Finished walking, N metres from the destination: NAME is in the
   way, ...", her standing about 1.5 m from them, no teleport. Then the same walk from 15 m off, the other stepping
   out of the doorway once she has set out: "The way is clear now; going on" and she arrives inside. And the first
   with Always run on: how far from them does she stop? On OpenSim, let go a metre before the way's end, she ran
   into the visitor; it's now let go sooner at a run, by a guess at how far Second Life carries her on.
6. **Stepping in**: a 20 m walk over open ground, the other avatar walking onto its line 8 to 10 m ahead of her and
   stopping there. Wanted: "NAME is in the way; going round." and an arrival, nobody touched. Then the
   other avatar walking across the line without stopping: nothing said, or one such line and a small detour. The
   first again with Always run on (it looks two seconds of her going ahead, so further at a run). And the other
   avatar walking to the same point a few metres ahead of her, as someone she's going somewhere with would: nothing
   should be said of them while they keep ahead.
7. **Somewhere busy** (a club's landing point, with people standing about and dancing): `//go` from among them to a
   spot across the room and back, a few times. Does it set out at all, or say "Not walking ..." where a sighted person
   would simply walk off (that would mean the room it keeps is too wide for crowds)? How many "going round" lines in
   one walk, and is that too much to listen to? Does any walk end in a teleport or in silence? Her own account of
   it, when a blind tester has had it for an evening, counts for more than ours here. Two things to listen for there.
   Standing with two or more people within a metre of her, a walk with no step that keeps off them sets out through
   them, as walks did before: does that get her out, or leave her pushing against someone until "Finished walking
   ...: NAME is in the way"? And what she isn't told: a way round people that's under a metre longer is said as the
   plain walk, so she never hears who she passed (it was "around NAME: 2 stretches." and "2 of 2: ...", for a first
   stretch a step long). Does she miss knowing, or is it the noise it looked like?
8. **A narrow place**: the other avatar standing in the middle of a shop's aisle or a corridor 2 to 2.5 m wide, the
   walk's point beyond them. The way needs 2.6 m to pass someone standing mid-corridor (`docs/access-app.md` > How
   much room it needs), so here she's told "only as far as them", unless there's a way round no more than 10 to 12 m
   longer, which it takes: where it does, was that worth the walk, or would she rather have been stopped (or sent
   further)? Would a sighted person simply have walked past? With
   walking round people off, does the same walk get past, and is the other avatar moved or their person bothered? If
   passing is fine there, a way should be let squeeze by someone (as near as bodies allow, from 1) before it's cut
   short of them.
