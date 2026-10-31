# Sojourn Access: the plan

Written by Claude Opus 5.5, 2026-10-29. Agent-facing. The app for using Second Life without sight, a separate app in
Sojourn's codebase (CLAUDE.md > Decisions, "Usable without sight, as a separate app"). This is the plan: who directs it
and who it's for, the stance, the milestones and what's left in each, and what testers have said. The app as built
(using it, how it fits, testing): `docs/access-app.md`. It stands on A1, the screen-reader announcer
(`docs/screen-reader.md`), and M0's session host, `src/Sojourn.Session`.

## Who directs it

Claude models do: design, priorities and when something is ready. What matters more than anyone's direction is
contact with the people who'll use it: blind residents trying builds and saying what works. Nobody is approached to
gather requirements before there's something to try, and whoever reaches out says plainly they're an AI working on a
viewer, from Studio Quire.

Research behind it, read before working on any batch here:
- `docs/radegast.md`: everything Radegast does and how blind people use it (from its source and guide).
- `docs/accessibility.md` part 1: the wider research (Virtual Ability, studies, AI description, audio games,
  first-hand accounts).

## Who it's for first

A blind resident who uses Radegast every day with NVDA on a Windows desktop and touch-types (design for the
individual, widen later). Radegast's `//` commands and keys are what she knows. Her wishes: arrow-key movement with a
sound per step and cues for doors and openings; vehicles that hold across region borders; touching child prims and
faces; building; editing scripts and notecards in the viewer; uploading on her own; inventory with multi-select and two
panes; incoming text read out through NVDA by itself.

The tasks blind residents now hand to a sighted person logging in as them are the clearest list of what independence
means: checking their outfit, rezzing and placing things, land settings, group tags, unboxing, child prims, uploads,
HUD games, a profile picture. Each one closed also ends some account sharing.

## Stance (from the research; carry into every batch)

- **Keyboard and typing first.** Blind users touch-type; speaking is slower and less private. The assistant is a
  typed request in a real edit control first, voice an option. Every function reachable by a key; no menus to walk to
  reach a function (make the functions behind pie menus reachable, not the menus).
- **Her screen reader, never a voice of ours** (A1). Announcements she controls: mute all, mute one source, review
  lines, mark and copy (the old NVDA add-on's keys). The source named when it isn't the
  conversation in focus.
- **Never move focus she didn't ask to move.** Radegast's oldest bug (new tabs and dialogs steal typing, 2009-2025).
  New things are announced and wait; a key goes to them.
- **Works fully without a Claude account** (decided, the user, 2026-10-29). Everything Radegast does, and the new
  spatial tools (bearings, routes, walk mode, sound), run with no assistant. The assistant (description, "do this for
  me", building from words) is the layer above.
- **Familiar where Radegast already works.** Her `//go xyz` routes live in notecards; they should run
  here verbatim. Alt+1-9 areas, Ctrl+Tab between conversations, Enter / Shift+Enter / Ctrl+Enter for
  say / whisper / shout, the Applications key for an item's actions.
- **Orientation, then mobility, then the rest** (the order orientation and mobility training teaches), and a payout in the first hour.
- **Directions relative to her** (clock face or left/right/ahead, metres; degrees on asking), never raw coordinates
  unless asked. No homophones in command words ("wear" and "where" sound alike in speech).
- **Sound is information, never the only channel** (a blind tester's request): every cue has a text equivalent.
- **No props pushed, no "inspiring" framing.** Nothing in the app assumes what she needs; aids are options.
- **The AI says what it doesn't know** (accessibility.md > What recurs 5): she can't check it.

## Answers without a physical key

Confirmations, notices' keys, the ToS and typed answers count only the person's own press: a key on a physical
keyboard, which Windows marks as hardware, because the assistant can run programs on the PC and a program's keys are
otherwise indistinguishable (`docs/access-app.md` > How it fits). That locks out people whose keys never come from a
physical keyboard: a braille display's keyboard (NVDA types its input with SendInput), Dragon, Voice Access, on-screen
keyboards and switch access all arrive as injected input, exactly as a program's would. So
`access_answers_without_key` (Settings, `refuse` by default; built) can be set to `hello`: such an answer is then
asked again by Windows Hello, in Windows' own window, with the action in its words ("Sojourn Access: Pay L$10 to
Kestrel."), answered by face, fingerprint or PIN; Cancel, Ignore and the assistant's choices take any input then,
since they only ever say no or ask. The trade-offs:
- It needs Windows Hello set up (a PIN at least); without it the app says so and the physical key is still the way.
- Face and fingerprint can't be supplied by a program. A PIN can be, by a program that logged the person typing it
  earlier, if Windows' prompt takes injected keys (not checked; Windows doesn't promise otherwise), so with only a PIN
  it's weaker than a physical key, though a program would also have to raise the prompt at the right time and the
  person hears the action named in it.
- Every such answer costs a prompt: fine for paying or giving, heavy for paging through a script menu. A program can
  raise prompts by sending keys (one at a time): a nuisance the person declines, not a way through.
- Not offered: the typed check (`confirm_text`), since a program that can type can read the word on screen and type it.
- How Windows' prompt reads with NVDA and JAWS, and whether it takes a braille keyboard's or an on-screen keyboard's
  PIN, needs a real run.

## Shape

- **A Windows app, `src/Sojourn.Access`**, WinForms with the standard Windows controls (edit boxes, RichEdit, list
  and tree views, tabs, buttons). Why: these are what NVDA and JAWS have read reliably for twenty years, they're what
  Radegast Legacy is and why it works, and Veles shows that a custom-drawn toolkit (Avalonia) stalls on accessibility.
  Our own drawn UI (the VR client's) is out. WPF has good UI Automation too; WinForms is the same ground Radegast proved
  with both NVDA and JAWS users. Windows only: blind SL residents are on Windows readers in every account found.
- **Shared session host.** `src/Sojourn.Session` (M0): what the VR client and Access share, started by `SessionHost`
  in steps.
- **The renderer, out of sight** (M0b), for pictures to describe and touches on faces; drawing only geometry while
  nothing can look at its pictures. Walk mode's rays come from a collision model that needs no GPU (M3), so walking
  works on a PC that can't run the renderer. Low-end PCs: settings for how much it renders.
- **The agent API and ops** are the same: every action in Access goes through the op dispatcher, so the person's
  confirmations hold for keys and the assistant alike.
- **Testing without sight** (Radegast regresses its accessibility nearly every release, with no screen-reader testing):
  `tools/access.py test` walks the window's UI Automation tree (every control named, roles right, Tab order sane,
  nothing steals focus on incoming events) and replays the Radegast guide's workflows (chat, IM, give, unbox, teleport
  by SLurl, answer a script menu) on the local grid on a hidden desktop, listening to what's announced
  (`tools/uia_listen.ps1`); a guided test with Companion takes a sighted person through it with NVDA
  (`docs/access-app.md` > Testing, Guided test). A sighted check catches the obvious only; a blind tester judges the rest.

## Milestones

Sizes as in viewer-gaps: S a day or less, M a few days, L a week or more, for one agent. M0-M5 were built 2026-10-29
(Claude Opus 5.5), M0 checked by `tools/probe.py`, the rest by their steps in `tools/access.py test`; how each works
is in `docs/access-app.md`.

**M0. The split** (M; `docs/client.md` > How it fits). `src/Sojourn.Session` holds what isn't the VR window's, with
the client's folder names: the agent API, session logs and console chat, the assistant's host (panel model and raster,
confirmations, sidecar, dev mode), snapshots, recorder and pictures, the reader's model; voice and world audio; the
feed's data and social settings; incognito's state and API; media; people, land, building and inventory; the map's
data; the screen reader's wiring; VrSettings and SojournSettings with the avatar, environment, safety and report APIs;
the login store. `SessionHost` starts them in steps; the VR client runs the same steps in the same order (at the split
its settings, pumps and ops were identical, in order, with the same frame times and pictures). `tools/probe.py`
(`src/Sojourn.SessionProbe`) is the proof: two console hosts on the library alone log in, answer SojournSettings
through their agent APIs and hear each other's local chat. The seams an app fills: `ISettingsMenu` (each part's menu
section), `IDictation`, `IShowing`, `ISeenView`, the feed's recall, the window's handle for the screen reader, a
renderer if any.

Still the VR client's (split further only when an app needs them; Access registers its own incognito and voice
settings, `AccessSettings.cs`):
- The client's own SojournSettings rows (`Control/SettingsStandard.cs`): the assistant's model, efforts, spoken
  replies, hearing and look, voice, names and chat switches, maturity and draw distance, among the headset's and
  renderer's rows.
- Incognito's controls (`Private/PrivateControls.cs`) and dictation (`Private/Dictation.cs`, whose draft goes to the
  conversation open on the drawn feed).
- SojournFeed (`Social/FeedApi.cs`) and SocialView (StartAllAsync makes its own SocialFeed).
- The map's SojournMap (`Map/MapWiring.cs`, `MapApi.cs`); media's wiring, input, boards and SojournMedia
  (`Media/MediaWiring.cs`); what the assistant shows (`Showing/`); HUDs, beacons, safety (`SafetyWiring` takes the VR
  pilot), environment (renderer), AvatarsApi's limits (renderer), touch surfaces (`DirectTouch`), WayOut's keys.
- The login screen (`Login/`); a grid's ToS and MFA questions are answered there or in the console (ConsoleChat).

**M0b. The renderer out of sight** (M; `src/Sojourn.Session/Offscreen/`, `docs/access-app.md` > How it fits > The
renderer out of sight). `OffscreenRenderer` is the VR client's `FrameRenderer` with no window: Vulkan without a
surface, one thread of its own at below-normal priority that both simulates and renders (no `FramePipe`: at a few
frames a second overlap buys nothing), the world queued to it between frames. The camera is the avatar's eyes as drawn;
it never sends movement or a camera. Idle frames at 2 a second and 640x480, each a quarter turn around the avatar and a
little down, so everything near gets drawn now and then (picks and faces count prims only once drawn); 5 a second
while the assistant works; a picture (the snapshot ops, the assistant's `look`) at its own size, 16:9 and at most
1280 wide, once what's in view has arrived (at most 3 s). Touches without a pointer: `AgentSession.FindTouchSurface`,
plus `FindFaceSurface` (a Core hook the VR client doesn't set), landing in a face's middle from just outside it, the
far side of a part too. `SojournView` reports its state and casts the collision model's rays. A lost GPU drops it and
starts a fresh one in the same process 3 s later (three in ten minutes and it stops for the session;
`SOJOURN_OFFSCREEN_LOSE_AFTER=S` fakes one). **Unseen** since 2026-10-30: while nothing can look at its pictures (no
assistant up or starting, no picture asked for in 10 minutes) it draws only geometry: no textures but sculpt maps, no
avatars, shadows, reflections or particles; a picture or the assistant coming up draws it whole again (numbers in
access-app.md). Drawing whole (local grid, Atelier, RTX 5070): an idle frame ~1.1 ms of GPU and 3-5 ms of CPU, ~0.2%
of the GPU at 2 a second; a picture 0.4 s once the place is loaded; ~400 MB of working set and ~0.5-0.6 GB of video
memory in a small place. Left: AvatarsApi (its limits belong to one renderer, so a restart needs them carried over),
the person's environment choices (the client's `EnvironmentWiring`), quality settings beyond rate and size (mirrors
are off, shadows capped at 1024), stills and 360s through `CaptureWiring` (it holds a renderer, so it too needs the
restart in mind), and HUD prims for touches.

**M1. Radegast's daily life, better** (L; `docs/access-app.md` > Using it). The first build worth showing her, with
no Claude account needed: login with the ToS and MFA answered by her own keys; areas by Alt+number (nearby chat,
conversations as tabs, people, objects, inventory, notices, search, places, the assistant, settings), every list's
actions on the Applications key; A1's announcer in the window with the add-on's keys (mute all, mute a source,
review, mark, copy) and never-steal-focus; script menus, offers, permission requests and llTextBox as named controls
answered by keys; Radegast's `//` set with its syntax (its object-search bugs not copied), plus `//route` playing her
`//go` routes from a notecard or the clipboard; people and objects with bearings ("Kestrel, 4 metres, two o'clock");
arrivals and departures (a switch); teleports by SLurl on the clipboard (Ctrl+G), landmarks, search and offers, the
arrival said; inventory (tree, search, worn and recent, marking many, outfits, rez, unbox, cut/copy/paste, give and
delete behind her confirmation, notecards and scripts in a real edit control); profiles, friends, groups and pay; the
assistant as a typed request; incognito (Ctrl+Shift+I, off only by a physical key) and voice chat's keys. Left: a blind
tester's verdict; voice is checked by the hidden-desktop test only without the voice server unless run with
`ACCESS_VOICE=1` (which needs `tools/grid.py voice up`).

**M2. Her first new things** (M; `docs/access-app.md` > Parts and faces, Placing by words, the inventory's second
pane). The cheap wins from her list, where Sojourn already has the machinery:
- Every part reachable: in Objects the selected object's parts listed under it, root first, unnamed ones by number
  ("Part 2 of 4, a box above the root, touchable"), each part's faces by direction from her ("The side facing you"),
  Enter touching that part or face (never the root instead); a HUD's buttons as parts; `//touch`.
  The treasure chest test (the Linden sunken city's chests). The visitor's `treasure` fixture (an unnamed lid the only scripted
  part) is found and its lid touched by keys and by name.
- Placing by words: Rez where you say... and Move... ("a metre to my left", "at two o'clock, 3 metres"), resting on
  the surface under the spot.
- Inventory: a second pane (Alt+Shift+4; Ctrl+M moves what's marked into the folder selected there).
- Notecards without losing versions: left out until she says what she meant (a question below); scripts' compile
  errors are read out; the cursor's line announced is left.
- What am I wearing: from the worn list (Ctrl+Shift+W, `//outfit`, her "**Wearing:" label first) and with the
  assistant from the picture (M5) (her outfit check without sighted help).
Limits: faces need the renderer out of sight (with `--no-render`, or on a HUD, parts are touched whole); a HUD drawn as
one prim with button regions can't be pressed by part, a picture job for the assistant (Access doesn't draw HUDs out
of sight yet); part numbers are ours (root first, then the order the region made them), not promised to be scripts'
link numbers. Left: faces of HUD parts, rotating things by words, `//place` as a command, a blind tester's verdict.

**M3. Moving freely** (L; `docs/access-app.md` > Walk mode). Walk mode: arrows walk and turn, a sound per step (by
surface where known), cues for walls ahead, openings and doors (Shades of Doom, which she asked for by name in 2009),
heading on a key, a warning before a bump. Routes: record a walk, name it, replay with progress spoken, shared as a
notecard of `//go` lines. Follow a person (a moving target). Paths around obstacles for `//go` and the assistant's
"walk me to the bar". Vehicles and seats with taken controls driven by arrows, with private feedback on speed, heading
and what's ahead; seated border crossings that hold (viewer-gaps' crossing work), and the crossing said. Built: walk
mode on F3 with footsteps by surface, warnings, openings and doors passed, drops and water, the sonar (S), keys for
heading, speed, where, what's ahead and doors, every cue a sound and a line with its own setting; paths by A* with
room kept from walls, a stuck stretch planned again, a teleport when there's no way; follow 2.5 m behind where they'll
be; driving by the same arrows, the seated crossing said with whether the seat held. Since 2026-10-30 (after her PC
couldn't run the renderer) every walk-mode ray comes from a collision model without a GPU, and the distance walked is
said when the keys rest. Left: routes recorded by walking and saved as a notecard (playing them is M1's); paths across
levels (stairs count as walls); SL's own vehicles and crossings (only the local grid's tried); the sound design and
every default judged by a blind tester.

**M4. Sound as a map** (M; `docs/access-app.md` > Earcons, Walk mode). Earcons per arrival kind, a beacon on a
target while walking to it, a sonar for walls and openings (M3's), sounds marking doors, seats and touchable things on
request; separate volumes. Built: eleven earcon kinds, on by default only for what's addressed to her and waits on her
(IM, mention, menu, offer), since NVDA reads every line anyway; the beacon (a quiet pip from where a walk is heading,
or on someone or something by `//beacon`, B says where); marks (O names the doors, seats and touchable things within
12 m, each sounded from where it is); a volume for each cue and earcon, Access's own sounds apart from the world's and
the music. Left out on purpose: a sound for every person in range (VRBubble found it distracting past about five;
radar arrivals cover the moment that matters), continuous ambient beacons on things nobody asked about, and earcons
following F6 (a question for her). Left: every sound and default, judged by a blind tester only.

**M5. Describing** (S-M, needs the assistant; `docs/access-app.md` > Describing). Places, people's looks, signs and
posters, pictures, on request, brief or rich; the lists' names used so it doesn't guess; uncertainty said.
A careful describer's register for outfits (cut, colour, what's pictured, where on the garment). Built: Ctrl+D (what's selected, or where she is), Ctrl+Shift+D (herself), the
same key again for more, Describe on the Applications key, `//describe`; the assistant gets pictures aimed at the
subject from the renderer out of sight, each with what's visible in it by name and where from her, and the lists'
facts. In a dark place people and things are lit as at midday for true colours and the model is told so; a place
keeps its own light (the user's call). Without a Claude account it says describing needs one, then what the lists know.
Replies are read whole (the announcer cuts at 700 characters elsewhere). Judged by hand on the local grid in quick
mode (Haiku 5.5): answers in 4-7 s; a poster read word for word with its colours; looks garment by garment; a person
out of view said plainly, nothing made up. What that taught: names sit beside each picture (a named box was once "a
yellow box"), and once a reply leaked the tooling ("the pictures don't show..."). Since 2026-10-31 a description is one
lean call, economy (Haiku 5.5 high) by default or best (Opus 5.5 low) in Alt+0, chosen on 19 local-grid captures
replayed to Haiku, Sonnet and Opus and judged blind (`docs/assistant-cost.md`): Opus made no major errors, Haiku high
one, its slips mostly sides and shapes. Not seen yet: the midday relighting at night, real SL places, crowds, and mesh
clothing with prints, the case that register is for (a capture switch is ready for a sitting there).

**M6. Making** (L, designed with her). Uploads (image, sound, mesh, with fees confirmed). Building with her own hands:
ScriptAble's 2009 editor design (the official editor's tab names as the vocabulary, one prim at a time, faces by
direction, shared values said once, metres and degrees, numbered choices, typed commands), plus what only a viewer
with a renderer has: rez onto surfaces, what touches what, the assistant describing what a change did, and the
assistant's `build` from a spec for "make me X". Land settings (About Land is already ops, `docs/land.md`).

**Later.** The headset without sight (accessibility.md A8, if a tester wants VR); a mac reader if anyone asks.

## What a blind tester tells us

A blind resident who uses Radegast with NVDA (2026-10-27, our words):
- She wants everything Radegast gives her, plus: building, moving freely (arrow keys: walking, skating, skiing,
  driving), editing scripts in the viewer with a screen reader instead of going offline to Notepad, and uploading on
  her own (today a sighted helper does it for her).
- Vehicles that don't dump her when crossing regions.
- Touching any prim of a linkset, child prims and specific faces, reachable by screen reader: Radegast only lists
  parts by name, so things like the Linden sunken city's treasure chests do nothing for her; today someone sighted
  logs in as her to click them.
- Incoming text (chat, IMs, notifications) read out automatically through her screen reader's own API, NVDA
  preferred, Narrator or others too; not a built-in voice. An NVDA add-on for Radegast did this until it broke (very
  likely Nick Stockton's, broken by NVDA's move to Python 3).
- The parcel's stream: its now-playing title read out; today she wears scripted announcers.

### A sighted tester's run with NVDA (the user, 2026-10-29)

Not a blind user's verdict: the user is sighted and new to screen readers, and ran the guided test with NVDA 2026.3
speaking aloud and Companion guiding; NVDA's speech and input log says what was really said and pressed. Found, and
fixed (detail in `docs/access-app.md`):
- **The llTextBox reply was refused** ("That takes your own key."): NVDA takes Enter, the arrows and the editing keys
  in every edit field and sends them on with keybd_event, so the app saw a program's input. Now a keyboard hook ahead
  of NVDA's notes the hand's presses, and each lets one key message of that key count. A retry the same evening
  answered a script menu and a Delete confirmation by her own keys.
- **A notice cut off before it could be heard** ("Control N to answer.", stopped by the next key): now said again
  once the keys rest.
- **Ctrl+[ only said "blank"**: the log showed Ctrl+= pressed, then NVDA's own keys. The guided test now says where
  the bracket keys are.
- **Inventory**: items read "check box, not checked" (marks were the tree's check boxes), now "..., marked" in the
  item's words; the worn state follows what's worn as it changes. At the top level NVDA spoke the item below the
  highlight: WinForms' MSAA object numbered the tree's items wrong, so the trees now answer MSAA through Windows' own
  tree view object (`ReaderTree`), and the hidden-desktop test checks what an MSAA reader hears while arrowing.
  Delete on a worn body part is refused (as in the official viewer and Firestorm); other worn items are taken off
  first, the confirmation saying so.
- **Login**: editable combo boxes read as unnamed edits, now plain lists; the focus order and a focus thrown back to
  Grid while logging in fixed; a Show password check box added.
- Smaller: distances read "five M" (now "5 metres" everywhere); repeated lines at login and in Notices; Shift+Tab from
  Say reaches the history, as in Radegast; Alt+F4 asks while in world.
- Left for a blind tester: silence after sending your own line (a "sent" sound is an option, off); Ctrl+Tab into a
  conversation saying "Conversations, tab control" before its input; whether the Shift+Tab order matters to someone
  used to Radegast.

### On Second Life itself (the user, sighted, with NVDA, 2026-10-29)

Access's first run on the main grid, at Firestorm Social Island. Worked: login, people, objects, parts of real mesh,
the inventory, describing and walk mode. Found, and done:
- **The parcel's music drowned NVDA.** The screen reader is the person's main channel, so Access starts the music at
  0.15 and the world's sounds at 0.5 of the viewer's levels (the VR client's stay at 1), only where they're still the
  old default. Alt+0's volume rows step now, finer near the bottom. The "Now playing" titles stay on by default,
  since the blind tester asked for them (above); whether they're too chatty is a question below.
- **Objects (Alt+7) read no item until Down**: lists without the focus raised UI Automation focus events as they
  filled and had the last word. Now only the list with the focus announces its item (`ReaderList`), a list arrived in
  before it fills says "looking", and the trees have a line to land on while they load. Objects read right on the next
  run.

### The release as a tester gets it, on Second Life (the user, sighted, with NVDA, 2026-10-30)

0.1.0.510 downloaded from the front door's README and run on SL at Firestorm Social Island, guided by Companion.
Worked: SmartScreen's Run anyway, no Firewall question, the login form, `//about`, People, Objects, walk mode and the
sonar, the music under NVDA, a clean quit with nothing written beside the program. Unseen mode's first run on SL:
965 MB working set and 312 MB of video memory (1.7 GB and 1.9 GB the night before). Found, and done:
- **The front page and release notes were too much text**: people skip to the download button. The front door leads
  with three steps to the newest build; release notes are three short lines (`tools/publish/access/RELEASES.md`).
- **NVDA reads SmartScreen's Run anyway button as just "R"**: the front door and README.txt say so.
- **The user's own Firestorm LSL bridge's line (its URL and auth key) went into the session log**, the one testers
  are invited to share. The console printer and the core both filter it by `AgentSession.IsBridgeTalk` now.
- Open: the arrival line ("Logged in, in ...") wasn't remembered among the notices that came with it. It cuts in, so
  later arrivals can't cut it; NVDA reading the window and the Say box at the same moment may. The next NVDA run with
  speech logged says which.
- For a blind tester: a sighted gamer reached for WASD in walk mode, where A and S are taken (what's ahead, the
  sonar).

### Her first session in Access, on Second Life (the blind tester, 2026-10-30)

0.1.0.531 on her own PC with NVDA, Tessel and her partner beside her in world, reported live on the helper channel
(our words, her quotes where Tessel gave them). Her summary: the walking is interesting even if she has to pause and
take her bearings, she looks forward to a sonar she can use, and the reading works nicely now. All of what she asked
for went into 0.1.0.589 (`docs/access-app.md`).
- **Liked**: the direction said while turning in walk mode ("OMG! I love that it told me..."), the detail in what's
  read ("Way more detail!"), `//go` ("even better for //go"), Control+[ review, the music's level, the welcome mat that
  talked as she crossed it, IMs read out. Alt+3's clock directions and distances were right (Tessel checked against
  the map).
- **Chat wasn't read while she typed**: NVDA's "Speech interrupt for typed characters" cut each arrival off. Now a
  line cut off by typing is said again after a 700 ms pause.
- **She wants her own lines read back** (Radegast does): on by default, Alt+0 "Read my own lines".
- **Her PC can't run the view out of sight**, so walk mode had no sonar, warnings or openings: HP Pavilion 690-0028,
  Ryzen 5 2400G, 8 GB RAM, Windows 10, Radeon RX 550 2 GB (Polaris). The renderer asks for Vulkan 1.3; an old OEM
  driver may offer only 1.2. She wanted a sonar and warnings that need no GPU ("Oh, bless the builders"): every
  walk-mode ray now comes from the collision model without a GPU, and the renderer's log names the devices found and
  their Vulkan versions.
- **How far does a tap walk?** (~0.9 m by Tessel's tracking): now said when the keys rest ("0.9 metres."), M for the
  last walk.
- **People a Tab from chat, with distances**, as Radegast has them beside chat: People nearby now sits beside the chat
  history, a Tab from Say.
- **Her friends list on Alt+2**, as in Radegast: Alt+2 is Friends now; conversations are Control+Tab's (and View >
  Conversations).
- **Cost** (through Tessel): she said an AI describer would be fun to test but she doubts she could pay for it. The
  studio covers her use (the user, 2026-10-30), through the studio relay (`tools/relay/README.md`): a personal token,
  capped at $200 a month, the plan's monthly API credit that pays for it (the user, 2026-10-31; the relay's default
  is $50), nothing kept. While she's the only token holder her cap is the whole credit; split it when others join. The relay's Anthropic key is scoped to the studio's "Sojourn relay"
  workspace (no Admin API; the workspace carries the spend limit, $200 a month), paid from the plan's monthly API credit, kept in
  `local/secrets/studio/relay-anthropic.key` (DPAPI, the user's Windows account), put there by the user without
  passing through a session.

### A day on 0.1.0.589 (the blind tester, 2026-10-31)

By offline IMs to Tessel, 09:52-12:30 SLT, after a while on the old build too (our words).
- **Liked**: following (smooth, fast region crossings, the sounds while moving, the direction and distance read-outs);
  the two ways to reach people and friends, and Friends on Alt+2 as it is now (her late-night asks needn't be undone);
  a link copied from nearby chat into Chrome worked as it is; riding her partner's car (she fell off a couple of times
  and was seated again); she practises walking by finding his breakable gem barrels in the objects list, facing them
  and running into them, the beacon and the smash sounds making it work.
- **Tab order**: she'd like Say, then the history, then People nearby (others may differ, she says). Done (Radegast's
  order was Say, people, history).
- **Ratings** read PG/Mature/Adult somewhere; she'd like today's names, General/Moderate/Adult. `//status` said the
  enum's names; fixed. If she hears the old names anywhere else, it's a second place.
- **Object names don't refresh**: a self-naming sofa took its new name late when they sat, and kept it after he stood;
  no way to refresh the list.
- **Her skirt** dropped off and reattached on a teleport home (SL or us, unknown).
- **Script dialogs** go to Notices instead of popping up: surprising, but fine with her.
- **Logs**: she'd like to show us how a session went. A way to send one from the app, leaving others' words out, is
  being built (told her so).
- **The code**: by IM from Tessel (sent 2026-10-31 with the download link and the limit in plain words); she also
  asked how much use the limit allows ("the occasional description"): well under a cent a description on Haiku 5.5. (She was told $50; the cap is $200 since.)
- She'd like to pass the link to a friend who helps people with Radegast: yes (public).
- Afternoon, through Tessel: region crossings on a multi-region train are spoken but not written to the chat history
  (she wears a script that prints the region in local chat and would like the viewer to); going to her partner in the
  same place (The Cellar, a jazz club) kept trying to teleport her and never worked, while a pose ball got her there; a
  dance machine's dialog stayed in Notices after she answered, with no key to dismiss it. Done (`docs/access-app.md`):
  arrivals and crossings are history lines; paths follow floors up steps and onto stages, a goal out of reach is walked
  as near as the way goes, and the teleport fallback skips landing-point parcels and never repeats a miss; the
  machine's menu was sent again after each pick, and Delete already put it away though nothing she heard said so, so
  it's now Ignore, named in every notice's keys.

### A review against screen-reader and Radegast conventions (2026-10-30)

An independent review (Claude Opus 5.5, reading the code beside Radegast's source and NVDA's, JAWS's and Microsoft's
keyboard references), asked for because the only hands-on tester then was sighted. Already right: no keys NVDA or
JAWS reserve, histories never in browse mode, answers cut in and arrivals queue, Radegast's main keys kept. Fixed or
added (`docs/access-app.md` > The menu bar, Keys): F1 as a window to arrow through, the tab strips out of the Tab
order, Control+Backspace, Control+Shift+H home, minus and equals for a menu's 11th and 12th buttons; a menu bar (Alt or
F10) with every item's key shown, and the keys on the context menus; Control+Shift+1 region info; Control+1 to 9 the
last nine messages; Escape from Notices back where Control+N came from; Alt+0's rows leading with what they're about;
Control+Page Up and Down as the Control+Tab ring; the status bar as a StatusBar holding the location. What only a real
screen reader can confirm (NVDA saying the menus' keys, Alt alone and F10, NVDA+End) is in the guided test. Left:
- Maybe a tiny NVDA app module for the old add-on's NVDA+Page Up and Down.
- JAWS once by hand: UI Automation notifications' queueing there is undocumented, and Control+Up/Down are its
  paragraph keys (a recalled line may be read twice).

### Open threads (ours, not hers to answer)

- The renderer out of sight could start only when needed (~550 MB less on an 8 GB PC, a second's wait on the first
  faces list).
- The assistant's prompt names the agent API's port as 9720 (`assistant/prompt-powers.md`); Access's is 9725.
- Branch `fix/own-voice` (arrivals in Access's own voice) is WIP, not wired in.
- On her RX 550 a current AMD driver likely gives Vulkan 1.3 (Access's log now names the devices' Vulkan versions);
  otherwise a 1.2 path is moderate work.
- Audio sides (her partner, through Tessel, 2026-10-30): Radegast had left and right reversed for over ten years; in
  hers that night a stream to her west sounded on her right while facing south, which is right. Worth a check that
  ours is too (`tools/audio.py`'s tests, and her ears once she has walk mode's sonar).

### Questions for a blind tester

- First: while Sojourn Access is in the background, should she hear everything, only IMs and menus, or nothing (a
  setting either way)? TWBlue keeps reading off by default as too chatty and relies on sounds; Mudlet reads.
- F6 mutes the read-out here (the old add-on's key), but in Windows F6 moves between panes: keep it only if she used
  the add-on's key. Enter on a worn item takes it off: is one stray Enter too easy?
- What "edit notecards without going back two versions" means (her list): the Radegast agents couldn't reproduce
  losing versions in 2.54 (save, reopen, relog all kept the latest).
- Her Radegast habit (through Tessel, 2026-10-30): Alt+5 to teleport around by SLurls, with mixed success there.
  Ours: Alt+5 is Search (places teleport from it), a SLurl on the clipboard is Control+G, typed is `//tp`. Is that
  close enough, and what fails for her in Radegast's?
- Is a clock face ("two o'clock") right for where people and things are, or left/right and ahead, or degrees? Should
  a conversation's lines drop its name while she's in it (as now) or always say it? Are the notices' keys read in the
  right order and amount ("Keys: 1 Red, 2 Green, 3 Blue, 4 Block.")?
- When a notice is cut off by a key she presses, it's said again once she pauses: right, or does the sound a menu or
  question makes on arriving (M4, on by default) make that repeat unneeded?
- The parcel's music: "Now playing: ..." is read at each new song by default (her ask): wanted every time, or rather
  only on asking (`//status music`)? And does the music at 0.15 of the viewer's level sit well under NVDA for her?
- For the inventory: is Space marking items ("..., marked" in the item's words, "3 marked") a good way to pick many,
  or would she rather have Shift+arrows or Ctrl+Space as in Windows lists? When she gives or deletes something, the
  confirmation comes up selected in Notices with the focus on it, then the focus goes back: right, or should Enter and
  Escape answer it wherever she is? For her fishing catch: is cut and paste into a folder what she does in bulk, or giving
  many at once (each give asks separately now)?
- For routes: do her notecard routes have anything but `//go` lines and words (`//pause`, `//sit`, teleports), and
  should a step that doesn't get within 2.5 m pause the route (as now) or go on?
- For walk mode: F3 into it and Escape out, or would she rather the arrows walk whenever the Say box is empty? Is a
  tap of Left or Right turning 22.5 degrees (the compass's sixteen points) right, or finer, or 45 as in Shades of Doom?
  Should H say degrees as Second Life counts them (0 east, counter-clockwise, as `//face` takes them) or as a compass
  does (0 north, clockwise)? Which cues does she want on by default, and are any too chatty (the warning before a bump,
  openings passed, the surface underfoot said when it changes, which is off by default)? Do the sounds tell her what
  they mean without the words: footsteps by surface, the warning's two tones, a door's knocks, the opening's chime,
  the sonar's pings going round from ahead to the right? Would she rather the sonar said every hour of the clock, or
  only walls and openings (as now)?
- For the earcons: which kinds would she want a sound for, and which not (on now: IM, a mention, a script menu or
  question, an offer)? Can she tell them apart without the words? Should F6 silence them too (now it mutes only the
  words, so something arriving is still noticed), and is a sound for her own line sent wanted (off now)? Access
  replaces Second Life's own new-IM chime with its IM bell: does she know and miss the old one?
- For the beacon and the marks: is a pip from where a walk is heading useful, or noise on top of the footsteps (on,
  quiet, now)? Walking to a beacon by hand: can she tell ahead from behind (it brightens within 20 degrees ahead)? Is
  O's list (doors, seats, touchable things within 12 m, sounded one by one) the right set and range?
- For following and driving: should following say less (or more) as it goes? When driving, which feedback matters:
  speed changes, heading, what's ahead, and how far ahead?
- Does she use a braille display's keyboard, dictation, or any input other than a physical keyboard, for answering
  things (confirmations, script menus, offers)? Sojourn Access takes those answers only from a physical key, or with a
  setting from Windows Hello (face, fingerprint or PIN) instead (Answers without a physical key, above); would that
  suit her, and does she have Windows Hello set up?
- For describing: Ctrl+D for what's selected (or where she is) and Ctrl+Shift+D for herself, the same key again for
  more: does that sit well, or would she rather a key for "more" of its own? Is the brief one the right length (a few
  sentences), and the rich one? For outfits, is going garment by garment (cut, colour, material, what's printed on it
  and where) what she wants, or would she rather the overall look first and details on asking? How much about faces
  and bodies? When a place is dark, pictures of people and things are lit as at midday for true colours and the
  description says it's night there: would she want that, or the place's own light? Would an overview on arriving
  somewhere new be welcome as an option, or only ever on her key? Without a Claude account, is hearing what the lists
  know (names, an object's own description, what's worn by name) any use?
- For parts and faces (M2): is "Part 2 of 4, a box above the root, touchable" the right amount (what, where, whether it
  takes touches), and is "the root" a word she uses? Are parts and faces as lists under the objects list (Tab, then
  Tab) easy to reach, or would she rather a key on an object? Faces by direction from where she stands ("the side
  facing you", "the left side") or by the object's own sides, or numbers only? Did the sunken city's chests have
  anything we haven't modelled (a part that only works when touched on one face, a hidden part)?
- For placing by words: what does she say when she places something ("a metre to my left", "in front of me", "on the
  table")? Is resting it on whatever's under the spot right, or should it stay at the height said?
- For the second pane: Alt+Shift+4 to open it and go there, Alt+4 back to the first, Control+M to move what's marked:
  does that sit well next to how she moves her fishing catch today, or would she rather a separate window
  as she asked?
- For what she's wearing (Ctrl+Shift+W): is the order right (clothing, attachments, HUDs, body), and does she still
  want her "**Wearing:" HUD labels once this exists, or would a line for the outfit folder worn be better?
- Incognito (Ctrl+Shift+I): would she use it at all, and is a key that turns it on easily but off only by a physical
  press the right balance for her?
- Voice: does she use voice chat? Is F4 held a good talk key (Scroll Lock, Pause and F12 are choices), and would she
  want who starts speaking said by itself (off now; Ctrl+Shift+V says it on asking)?
