# Sojourn Access: testing

Written by Claude Opus 5.5, 2026-10-29 (split out of `docs/access-app.md` on 2026-10-31). Agent-facing; the guided
tests at the end are for walking the user or a tester through it. How the app works is `docs/access-app.md`; what
testers said is `docs/access.md` > What a blind tester tells us.

## The scripted test

`python tools/access.py test`: on the local grid, nothing on the visible desktop; it runs `build` and `tools` first. A
full run takes a while (the walk-mode step alone about 4 minutes). Last full run 2026-10-30 evening: 510 of 511, the one failure a tab item
the test couldn't see (fixed since in the test's client: Tab items, below).

- `ACCESS_STEPS=inventory` runs only the steps whose names have those words (after the login's checks).
- `ACCESS_NO_RENDER=1` starts the app with `--no-render`, nothing on the GPU, for the steps that don't need the view
  (e.g. while someone's in the headset). `ACCESS_NO_GPU=1` hides Vulkan's drivers from it
  (`VK_LOADER_DRIVERS_DISABLE=*`), as on a PC whose graphics can't run the view: the renderer step then checks it gave
  up without drawing and the collision model still meets the visitor's box, and the walk-mode step runs on the
  collision model alone.
- `ACCESS_VOICE=1` (with `tools/grid.py voice up`) runs it with voice; otherwise `--no-voice`.
- `ACCESS_ACCOUNTS=Walk` runs it as Walk Tester and Walk Visitor on ports 10 up, so it never throws out a person's
  guided session as Access Tester; it still uses Atelier's spots, so run it while nobody's testing there. The test
  refuses to start while its tester or visitor is on the grid (two runs kick each other out).
- The app's renderer is light (about 0.2% of the GPU), but a run that wants clean GPU timings elsewhere shouldn't
  overlap it.

**How it runs.** The app runs on a hidden Win32 desktop (`tools/quireuia`, from the Radegast contributions' tools:
`launch` starts a program there, `serve` records the focus, alert and notification events a screen reader gets and
drives the app by UI Automation and posted keys), with:
- `SOJOURN_TEST_INPUT=keys`: posted keys judged as the person's hardware presses, every other rule of the real-input
  guard applying, so Ctrl+N then a number and a confirmation then Enter are checked against it (`ACCESS_TEST_INPUT=1`
  lets every key through instead);
- `SOJOURN_TEST_NO_NVDA=1` (on a hidden desktop NVDA's controller client claims NVDA runs, so speech would go nowhere);
- `SOJOURN_TEST_NO_CLIPBOARD=1` (the clipboard left alone);
- `SOJOURN_TEST_LIST_DELAY_MS`: the lists' first fills held back 3 s, as Second Life's objects wait for names.

A bot (`tools/visitor`, Access Visitor) talks, IMs, and owns scripted fixtures: a box sending menus, llTextBox,
permission requests and offers; `facebox` (says each touch's face and surface coordinates); `treasure` (a chest whose
unnamed lid alone has a script); `sofa` (renames itself while sat on or on a touch); `kart` (a physical LSL car) and `cart` (its script takes the sitter's keys). Menus
open on the Applications key's press (`MenuOn`), not by WinForms' ContextMenuStrip, which opens on WM_CONTEXTMENU and
never for posted keys.

**The steps**, by the names `ACCESS_STEPS` matches (in run order; each step's function in `tools/access.py` has a
docstring with its checks in full):
- **the window's controls** (again after use), **Tab order**: every area's controls named with their roles; Say,
  People nearby, the history, Say; no tab strip a stop.
- **nearby chat in / out**, **her own lines read back**, **lines cut by typing**, **an IM**: say, whisper, shout; her
  own lines read back (and not, with the row off); lines cut by posted typing said again together after the pause,
  once, not after Control alone; an IM's tab arriving without taking the focus, Ctrl+Tab and Ctrl+W.
- **people and objects**: bearings measured against positions; arriving before the first fill (held back 3 s), the
  list says "looking"; once filled, its own item the only one focused and the last focus event, by UI Automation and
  MSAA; the `sofa` renamed by Touch from the list: unchanged while she's in it, its new name on F5 (the item she's on,
  read), its parts named for it, and renamed back by Enter on its part, followed by the list while she's in the parts.
- **people beside chat, friends on Alt+2**: Tab from Say into People nearby, its menu and Control+D, the list left
  alone while the visitor walks off and current again from Say; Alt+2 to Friends; View's Friends and Conversations;
  Control+Tab with none open.
- **the renderer out of sight** (the app runs with `SOJOURN_OFFSCREEN_LOSE_AFTER=15`, so its GPU is "lost" once first):
  back with one loss counted, unseen; `facebox` met by `SojournCollision collide` and `SojournView collide` at the
  same distance; two touches through the person's confirmation (one on face 4, the far side; a plain one on the face
  toward the eyes), the box reporting both; then `saveSnapshot` gives a picture that isn't flat, and the view is drawn
  whole for the next 10 minutes of the run.
- **keys**: F1's window (every key, Escape back to Say), Ctrl+Backspace, Ctrl+R, F6 muting.
- **the menu bar and the review's new keys**: the World, View and Help menus with each item's key shown (QuireUia's
  `get` gives UI Automation's access and accelerator keys), items doing what their keys do, the context menus' keys,
  Control+1 to 9, a notice's keys on its Applications key, Escape from Notices back where Control+N came from,
  the no-conversations line, Control+Page Up and Down, the StatusBar with the location. Alt alone and F10 aren't
  pressed (WinForms opens the menu bar for them only in the foreground window, which a hidden desktop never has).
- **settings**: Alt+0's rows (no two starting with the same three words, Right and Left stepping), SojournSettings
  refusing to lower `access_answers_without_key`, the music and world sounds starting at 0.15 and 0.5 (applied once),
  the music's volume stepped, `//status music`.
- **the quit question**: Alt+F4 asks, Escape stays.
- **a script menu**, **a text box**, **a permission question**: answered by keys; a menu's announcement said again
  when a key cuts it off (not when silenced by Control alone); a menu of twelve, minus and equals pressing Next and
  Back.
- **a dance machine's menu, ignored**: the fixture's `fdance` menu, sent again after each pick: answered by 1, the
  new one picked by itself; Delete Ignore in its keys, Ignore (Delete) on its Applications menu; Delete ignores it
  (the object hears nothing), said with what's left, the focus on the offer under it, then none.
- **inventory**: fixtures made through the agent API, then by keys only: the tree as an MSAA reader is told it (as
  first shown, after F5, inside a folder), search, a notecard edited and saved, marks, cut, copy and paste, wear and
  take off, Show: Worn (a worn body part's Delete refused at once), rez on the ground, unbox, give through the picker
  and the confirmation, and the run's folders deleted through the confirmation (cancelled once first: only
  "Cancelled.", the focus back in the tree; a worn item in the folder taken off first, as its confirmation says).
- **every part**: the `treasure` chest's four parts listed and named, Enter on the lid heard by its script (not the
  root's), its faces (with the renderer "The top, face 0", Enter there heard as face 0; without, the list says it needs
  the view), `//touch` by name, by part and by face.
- **your HUDs**: a two-prim HUD made and worn (its script put in through the confirmation by keys); named by
  Ctrl+Shift+W and `//outfit`; Show: Your HUDs lists its parts; Enter on its button heard by its script.
- **placing by words**: a block rezzed "a metre to my left" (within 0.3 m, on the ground), then moved "by 50 cm up".
- **two inventory panes**: Alt+4 again reading the first, Alt+Shift+4 opening the second, a search there, Ctrl+M moving
  marked notecards across, Ctrl+W closing it.
- **incognito**: on and said, the title without the region, SojournSettings refusing it off, a line read out but not
  logged, off.
- **voice keys**: without voice F4 and Ctrl+Shift+V say so; with `ACCESS_VOICE=1`, F4 opening and closing the mic with
  its clicks, Shift+F4's modes, Ctrl+Shift+V.
- **walking**, **a teleport**, **the assistant's walk**: a `//go`, Ctrl+Shift+H and a `//tp` checked against the
  region's positions; the session's autopilot stopping about its stop distance short.
- **places and a route**: Go to region, a landmark made and teleported to from the tree, the history, regions around,
  and a route notecard played by `//route` to its last point.
- **search, profiles, pay**: people and places searched, a profile opened, Enter opening an IM, Pay asking the amount
  (the local grid has no money, so its refusal is what's said). Ctrl+G isn't tested (the clipboard is the person's
  desktop's).
- **more commands**: `//siminfo`, `//parcelinfo`, `//scriptinfo`.
- **describing** (the test has no assistant, so the path without one): Ctrl+D and Ctrl+Shift+D saying it needs the
  assistant and what the lists know, `//describe more`; with the renderer, SojournDescribe's pictures of the visitor,
  the fixture box and here framed and named. What the model makes of them is judged by hand (`docs/access.md` >
  M5), with a scratch driver like the test's that presses the keys with the assistant running and reads back
  what was announced.
- **review keys and arrivals**: Ctrl+[, Ctrl+], mark and copy, times in the history; Ctrl+Shift+A and the visitor
  walking out of chat range and back, both said.
- **sound as a map** (about 2 minutes, no renderer needed), by `SojournWalk status`' record of the cues sent: which
  earcon starts for what arrives and where it's placed, an Alt+0 change playing it, the mic's click, O's marks by clock
  face, the beacon on the visitor and on a bench walked to until "Reached", a walk's destination on the beacon. It can't
  say whether they sound right.
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
- **distance walked**: three taps of Up said once as one stretch, close to how far the avatar went (the run prints the
  metres a tap), Down said "backward", M and Shift+M, nothing with `walk_distance` off.

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
renderer gives up after three tries, and the words for faces, pictures and sounds are as `docs/access-app.md` >
Packaging says.

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
