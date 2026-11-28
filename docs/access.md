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
- **Works fully without a Claude account** (decided, the user, 2026-10-29). The aim is everything Radegast does, and the
  new spatial tools (bearings, routes, walk mode, sound), with no assistant; what it still lacks next to Radegast is in
  `docs/access-gaps.md`. The assistant (description, "do this for me", building from words) is the layer above. Where
  this doesn't reach: what only a picture says. A HUD that's one picture of buttons is read by the assistant, so
  without one only its numbered faces remain, and describing has only what the lists know; nothing short of a local
  model reading pictures would change that.
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
memory in a small place. **On the processor** since 2026-10-31: where no GPU can run it (the blind tester's Vulkan
1.1 driver), Mesa's lavapipe from the package draws instead, pictures only (none between them), a picture a few
tenths of a second once warm on four modern cores (access-app.md > How it fits > On the processor). Left: AvatarsApi (its limits belong to one renderer, so a restart needs them carried over),
the person's environment choices (the client's `EnvironmentWiring`), quality settings beyond rate and size (mirrors
are off, shadows capped at 1024), stills and 360s through `CaptureWiring` (it holds a renderer, so it too needs the
restart in mind). HUDs out of sight since 2026-11-01: placed and picked when asked, drawn only for a picture
(access-app.md > How it fits > HUDs out of sight).

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
assistant as a typed request; incognito (Ctrl+Shift+I, off only by a physical key) and voice chat's keys. Left: what it
still lacks of Radegast, ranked in `docs/access-gaps.md` (this milestone was never checked against it feature by feature
until 2026-11-07); a blind tester's verdict; voice is checked by the hidden-desktop test only without the voice server unless run with
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
Limits: faces need the renderer out of sight (with `--no-render`, or on something worn on the body, parts are touched
whole); part numbers are ours (root first, then the order the region made them), not promised to be scripts' link
numbers. HUDs (2026-11-01, after the blind tester called worn things and HUDs her major problem): a HUD part's faces as
they show on the screen, and a HUD that's one picture of buttons (a train's driver panel, games, 7Seas) has its buttons
read off its picture by the assistant once per look of it, then listed after its parts by name and pressed where they
are (access-app.md > Parts and faces); checked on the local grid with a painted panel, GPU and processor. Left:
rotating things by words, `//place` as a command, HUDs on Second Life itself (mesh HUDs, many-page HUDs, how well
the model reads busy ones), a blind tester's verdict.

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
said when the keys rest. Since 2026-11-01 routes are recorded by walking them (R in walk mode, `//record`): however she
moves, the walk becomes `//go xyz` lines in her and her partner's format, points kept only at turns, level changes,
doorways walked through, region borders, teleports, seats and touches, with her own notes as lines `//route` reads out;
saved as a notecard in Routes, playable at once (access-app.md > Walk mode > Recording a route); checked on the local
grid by walking a turn through a doorway and teleporting into another region, then playing it back. Stairs are
walked since 2026-10-31, and since 2026-11-11 floors over one another too (an upstairs over where she stands, a
balcony, a bridge: the path is planned per floor over each cell, `Walk/PathPlanner.cs`), checked on the local grid in
a two-storey house by `//go` up and down, out onto a balcony and down under it, following and the assistant's walk.
Left: floors on Second Life (what its autopilot makes of a stretch's height, sent as given there: her log says if it flies her; stairs more than 12 m outside a walk's
area aren't found); recording's defaults and words judged by a blind tester, Second Life's own
places (landing points, busy regions), a crossing on foot recorded and replayed; SL's own vehicles and crossings (only the local grid's tried); the sound design and
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
facts. People and things are lit as at midday with a light at the camera for true colours (in the dark only until
2026-11-11) and the model is told so; a place keeps its own light (the user's call). Without a Claude account it says describing needs one, then what the lists know.
Replies are read whole (the announcer cuts at 700 characters elsewhere). Judged by hand on the local grid in quick
mode (Haiku 5.5): answers in 4-7 s; a poster read word for word with its colours; looks garment by garment; a person
out of view said plainly, nothing made up. What that taught: names sit beside each picture (a named box was once "a
yellow box"), and once a reply leaked the tooling ("the pictures don't show..."). Since 2026-10-31 a description is one
lean call, economy (Haiku 5.5 high) by default or best (Opus 5.5 low) in Alt+0, chosen on 19 local-grid captures
replayed to Haiku, Sonnet and Opus and judged blind (`docs/assistant-cost.md`): Opus made no major errors, Haiku high
one, its slips mostly sides and shapes. Not seen yet: the midday relighting on Second Life, real SL places, crowds, and mesh
clothing with prints, the case that register is for (a capture switch is ready for a sitting there). On Second
Life (2026-11-01, on the processor, at a busy club) her partner beside her was described untextured, grey-white hair
and clothes named as his colours, in every picture: confident and uncheckable, the worst kind of error here. Since,
a picture waits for its subject's look (bakes, worn things' textures, shapes) up to 12 s on a GPU and 30 s on the
processor, says per worn thing what hadn't loaded, and the describer names no colour for an unloaded surface, says
which colours aren't known yet and suggests asking again (`docs/access-app.md` > Describing > A look waited for).
Rides (2026-11-01, her waterslide wish): C in walk mode or `//ride` records one, a moment every 3 s (speed, way, climb,
the named things and people near, objects' lines, the seat's floating text) and a picture from her eyes every few
seconds, adaptive and skipped rather than queued on a slow PC, at most 40 kept; it ends on her word, when she stands up,
or when the ride has been still 8 s; Shift+C has it told as a short story in order, in one lean call, nothing sent
before she asks (`docs/access-app.md` > Walk mode > Recording a ride). Checked on the local grid on a kart past named
markers; not yet on a real ride.

**M6. Making** (L, designed with her). Uploads (image, sound, mesh, with fees confirmed). Building with her own hands:
ScriptAble's 2009 editor design (the official editor's tab names as the vocabulary, one prim at a time, faces by
direction, shared values said once, metres and degrees, numbered choices, typed commands), plus what only a viewer
with a renderer has: rez onto surfaces, what touches what, the assistant describing what a change did, and the
assistant's `build` from a spec for "make me X". Land settings (About Land is already ops, `docs/land.md`). Built
(2026-11-01): uploads (`docs/access-app.md` > Uploading): pictures, sounds, animations and models by Control+U, Build >
Upload a file, a folder's Upload a file here or `//upload`; what the file is and what Second Life will make of it said
before anything is sent (a picture's pixels and resizing, a sound's length and channels, a model's triangles and size
in metres), its name and folder asked, a model's scale, detail levels and physics offered at the official uploader's
defaults; the fee worked out by the gate and confirmed by her own key in Notices; the result said and selected in the
inventory; errors in words. Checked on the local grid only. Building with her own hands (2026-11-02, a first version
for her and her partner to react to; `docs/access-app.md` > Building): the Build area on Control+B, one prim at a time:
a command box taking the editor's tab names and settings as words ("object", "hollow 50", "face top", "fullbright on",
"new box 2 metres ahead", "move by 20 cm up", "turn 90", "link Table", "undo"), a settings list per tab (Enter changes
a row, Right and Left step it) and a faces list; General, Object, Features and Texture's rows as the official editor
has them (permissions, sale, click action; physical, temporary, phantom, position, size, rotation in degrees,
material, type and each type's shape rows; flexible path, light, physics shape; texture, colour by name or numbers,
transparency, glow, full bright, shininess 0-3, bumpiness 0-17, mapping, repeats, offset, rotation), Content (listed,
add, remove, copy out, scripts run or stopped); faces by direction from the prim's own volume, so without the renderer
too; a value every face shares said once; each change said back once the region shows it; where it is with what it
rests on and touches, by the collision model's rays and the prims' boxes, no picture; new prims set down on what's
under the spot; linking and unlinking; all of a linked object as the official editor's Edit linked off, where a linked
object starts, or one part as Edit linked on, every answer saying which (2026-11-07); copying one prim's settings to another;
locked, repeats per metre, flip, a flexible path's force and a light's projector; undo in one history with the assistant's;
permissions, a sale, temporary and deleting through her confirmation; `look` (Control+D there) has the assistant describe it.
Used on Second Life by the blind tester since 2026-11-06 (single prims and linking); everything else checked on the local grid
by the hidden-desktop steps, with and without the renderer. Land settings (2026-11-03; `docs/access-app.md` > About
Land): the About Land area on Control+L, on the parcel she stands on, laid out as the build editor: a command box
taking About Land's tab names and settings as words ("options", "fly off", "ban NAME for 2 hours", "for sale 500",
"buy"), a settings list per tab (Enter changes, Right and Left step) and the tabs as a list: General, Covenant,
Objects, Options, Media, Sound, Access and the Region tab; where she is on the parcel by its cells; the allowed and
banned lists by name with Delete taking someone off, who owns the objects with Delete returning them; buying land or a
pass where she stands, selling, abandoning, deeding, dividing and joining by compass or her own directions, the land
brush; each change through SojournLand and said once the region shows it; who may enter, the land's owner and shape
behind her confirmation, the other switches at her press as the viewer's checkboxes. Checked on the local grid by the
hidden-desktop step on a parcel divided off and sold to her for it. Left: for land, Second Life itself (real prices, a
covenant, group land and its powers, mainland), the parcel's picture and media's other settings, the estate tools as
keys (they're ops the assistant reaches), and every word and what asks first judged by her; for building, scale on all parts,
locked and copying on Second Life itself, the rows left out (reflection probes, PBR materials), several separate objects at
once, a face as a copy's source, the assistant saying what a change did by
itself (now on asking), and every word and default judged by her and her partner; for uploads, Second Life itself
(real fees, an account's mesh upload rights, the large-texture price), a picture described by the assistant before it
goes up, 2048-pixel pictures, levels of detail and physics from files, a blind tester's verdict.

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
  passing through a session. Telling testers why it's easy to cover is fine (the user, 2026-11-02): it comes with the
  plan the studio uses for running Claude anyway, and those credits would go unused otherwise. She had been keeping to
  the economy describer to save the code (2026-11-02, 8 cents used that month); told her both.

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
  dance machine's dialog stayed in Notices after she answered, with no key to dismiss it. Done in 707
  (`docs/access-app.md`): arrivals and crossings are history lines; paths follow floors up steps and onto stages, a
  goal out of reach is walked as near as the way goes, and the teleport fallback skips landing-point parcels and never
  repeats a miss; the machine's menu was sent again after each pick, and Delete already put it away though nothing
  she heard said so, so it's now Ignore, named in every notice's keys. (She found Shift+F10's Put away herself.)
- Evening, with the assistant on (her code by IM) and then 707: describing knows only the object lists ("I wish it
  could see more"): her PC's renderer can't start, both chips reporting Vulkan 1.1 on a 2019 AMD driver (her first
  sent log); positions and distances read well. She couldn't copy a long answer out of Alt+9 whole. The failed 'go
  to' was her standing and him up a small flight of steps (fixed in 707). No way to give from the person (Radegast's
  copy, then Give in the profile): built after 707. The skirt again looked missing right after a login, then came:
  likely attachments still arriving. Unplugging her headset and mic left Access stuck until she logged in again (on
  589). Gestures don't play, by list or trigger (Access has none yet). At login 23 friends' "is online" lines were read
  one by one. She asked what "answers without a physical key: refused" means, whether her code carries over (it does)
  and what to say in a blog post she plans ("this viewer is just awesome"): answered through Tessel. Later she asked
  to switch between her headset and speakers, as in Radegast. Done after 707 (`docs/access-app.md`): gestures (her
  active ones, triggers in chat, Control+Shift+G, the inventory's Play and Activate), Control+Shift+L copying the
  assistant's last answer whole (and the histories' paragraphs on lines of their own), friends online at login as one
  line, the outfit's attachments said as still arriving after a login or teleport, audio following a device unplugged
  or the default changing (the shim had gone silent for good) and said in one line, the sound output and microphone in
  Alt+0 with Control+Shift+O for the next output, and the answers row in plain words.

### A train ride on 0.1.0.718 (the blind tester, 2026-11-01)

By offline IMs to Tessel, 06:54-10:00 SLT, driving her partner's multi-region train round her atoll (our words), and
her log (sent 12:57, `local/relay-logs/tester-20261101T175707-4c467c.txt`).
- **Liked**: the earcons ("relies on them all the time now"); crossings handled well for most of the ride.
- **Window order**: she now thinks her ask to swap history and input was wrong and sees why it was the other way; a
  toggle could work, back burner. Left as it is.
- **Speed lines**: "8 metres a second", "4 metres a second" over and over, not knowing why; she'd like a toggle. The
  reported velocity swung (19, 6, 13, 8 three seconds apart); now smoothed and said only when settled, with its own
  switch, `walk_speed` (`docs/access-app.md` > walk cues).
- **Worn things and HUDs couldn't be clicked** (her "MAJOR"); she didn't know whether their menus would be read. Show:
  your HUDs existed but she hadn't found it, and body attachments (a pet with a menu) weren't listed at all. Now
  Alt+Shift+7, every worn thing, HUDs first; their menus come to Notices as any script's do.
- **No way to delete what she'd rezzed**: Take into your inventory and Delete (the key too, confirmed) on her own things
  in Objects.
- **Crossings**: train sounds fading in and out at crossings; at the end, a crossing into the next region lost her seat
  (the train said "Driver missing! stopping..."), the next region's connection failed, and our reconnect's login was
  refused ("Login canceled"), so she relogged and reported a crash; when the train moved again copies of her skirt and
  her pet were left behind. What was ours went into 0.1.0.737 (`docs/client.md` > crossings): LibreMetaverse's
  minute-long wait for a UseCircuitCode acknowledgement the region never sent (the viewer sends none on a crossing),
  a sound staying with the region its object was first heard in, copies of her attachments and the seat left in old
  regions, and a silent relog; the region not acknowledging and dropping her seat within ~10 s was Second Life's.
- **Memory** (her log, not her words): 2.0-2.4 GB for the whole ride on her 8 GB PC, ~2 GB of it the managed heap and
  much of that fragmented, 107 s of collection pauses; mostly whole-region scans while seated, a change queue without
  bound and left regions kept alive between the view's pictures. Fixed after 737 (`docs/performance.md` > Sojourn
  Access on a small PC); her next long log says how much it moved.
- IMs not read was her own setting (solved).
- Evening, 0.1.0.737 (her log `tester-20261101T225505-44b38a`): the train round her atoll crossed 39 borders and the
  seat held every time; the first picture after login still took about three minutes on her processor (warm-up 199 s),
  sped up after (`docs/performance.md` > The first picture after login). A description from the picture worked while
  riding ("Wow!") but framed her own train from her eyes and named her as its owner (fixed: a seat is pictured from
  outside, she's "you"). Speed lines in m/s were ours; her partner's script says the throttle in percent. At a club the
  "Now playing" titles kept up with the music ("My God this is great!"): wanted, so they stay on. She asked for a sound
  on deleting (built: the `deleted` earcon) and `//touch OBJECT` (it already existed; told her). A typed gesture
  trigger went out as text while the list played it (fixed: triggers read on demand). Wishes, not asks: a HUD's map
  described (HUDs out of sight, next build), carnival bumper cars by arrow keys (planned with her partner), and a ride
  down a waterslide recorded and described afterwards (built since: M5 below, `docs/access-app.md` > Walk mode >
  Recording a ride).
- **Rail switches** (that evening): two wrong turns at switches on the train, backing up and resetting
  them, not knowing how to handle switches. Built after 737: a switch ahead said once with how it's set, W for the next
  one, Shift+W or `//switch` to throw it (`docs/access-app.md` > Rail switches), on the SLRR switch as the SL wiki
  documents it (her partner pointed to the standard); not yet checked against a real switch.
- **The track's end** (that night): her train (an SLRR-style freight train, the throttle 5% a tap of Up or Down) says
  in chat when its track runs out, then derails or strands her; she asked for it to just stop. Built: a line from the
  seat or her own objects holding "no further track", "no guide found" or "track guide", while moving, sounds an urgent
  cue, says "Track ends: stopping." and stops it by its controls (Down held, or tapped as many times as she opened the
  throttle), never backwards; Space in walk mode and `//stop` do the same any time (`docs/access-app.md` > Stopping a
  vehicle).
- Later that evening (0.1.0.737, logs `tester-20261102T004558-2d699f`, `...005104-216eca`, `...015846-eec99e`): her
  partner described in wrong colours twice, 25 minutes after arriving (others' looks never loaded on the processor
  path; fixed in 0.1.0.801, `docs/access-app.md` > Describing); switching sound to her LG TV's own speakers over HDMI
  played nothing though Windows opened it (cause unproven; 801 checks a switch reaches the device and names outputs by
  kind); a fast trivia game had NVDA reading lines again, 37% of what was read (the cut-line re-say; fixed after 801).
  Her NVDA is 2026.3 (so the "is it speaking" check applies), voice Microsoft Mark at rate 30 with rate boost. On
  trains: rail switches took her the wrong way (switches built after 801 from the SLRR standard), and when the track
  runs out her train says "No further track guide detected" / "No Guide found" and strands her (an automatic stop,
  after 801). Her train is the Infinity Freight Quarry Train v1.44; her partner modded its hover text so its speed can
  be read (V reads it since, after the speed; Shift+V or `//text` alone). "It seems like with every update, nothing breaks."
- That afternoon she told other blind residents about Sojourn Access, and at least two were downloading it (through
  Tessel, 13:58 SLT). They have no relay token, so no assistant and no Send a log (it points them to the issues page);
  what they say may come through her. The user said (2026-11-01) to give everyone codes and that they'd watch the
  relay's credit, handling it if it runs low: two friends' tokens (`tester-friend-1`, `-2`, $40 a month each) went
  to Tessel to pass on; more on asking (`python tools/relay.py token new NAME --cap 40`).

### Favourites and start places (the blind tester, 2026-11-02 evening)

On the builders' line (18:09 and 18:14): she'd kept objects and landmarks in her inventory's Favorites folder, learned
it's meant for landmarks, and heard from her partner of the newer way to favourite things; she asked for that and what
it does, and whether the Favorites folder's landmarks can be start places at login. Told the difference (the folder,
now the viewer's Favorites Bar, vs marks kept by Second Life's inventory service). Built that evening: the marks
(Ctrl+Shift+F, the Applications key, "favorite" in an item's words, Show: Favorites) and the Favorites bar's landmarks
in Start at (`docs/access-app.md` > Keys, Favorites; Login). The folder is now said as "Favorites bar" (the viewer's
name for it). The marks are checked only against a stand-in for the service, so her first use is their first on Second
Life.

### Her HUD, crossings and the rest of that freight run (0.1.0.811, the blind tester, 2026-11-02 evening)

Her log `tester-20261103T000721-ef2577` (17:19-19:07 SLT, about 180 region crossings seated, three GTFO jobs) and the
builders' line, where she said she was trying to take off another HUD and couldn't (our words). Rail switches are the
next entry.
- **The HUD**: her region-crossing script (an attachment named Region-Change that says each region in local chat),
  which she found she didn't need now that Access says crossings ("Oops there is no way to remove it"). What you wear
  (Alt+Shift+7) offered only Touch, Describe, Where and Parts, and Delete there said to use the inventory. Now Take off
  and Delete there take it off. Which list she was in isn't in the log: what each list's menu offers is logged now.
  Her Chat-Range script also repeats the chat-range lines Access says.
- **"The crossing left you standing"** seven times while she sat on ("Sometimes it says the crossing left me standing
  but then it looks as if I am sitting"), each a second or three before the region sent her avatar seated; the other
  170 said "still on Zephyr". Now judged once the avatar and its seat have come.
- **Warnings while driving**: 157 lines such as "Atoll Railroad Section, 20 metres." and "Guide, 13 metres.", the track's
  own pieces met straight ahead on curves, up to six in three seconds. Now only what the train is closing on.
- **The track's end** in Velox: the train said "No Guide found" at speed and Access stopped it ("Track ends: stopping.");
  she asked whether they'd run out of track, then drove on forward the same way. Likely a gap in the guide (a switch was
  just ahead), not the end. Kept as it is; a question for her partner.
- **The assistant never came up**: Claude Code's start took over the SDK's minute (12-38 s on her earlier days; the view
  was compiling on her processor meanwhile) and the whole sidecar ended, with nothing said; she didn't ask it anything
  that run. Now a slow start is waited for up to four minutes, a failed warm-up starts again at the first request, and
  the log says when the sidecar ends.
- **Her log** had left out every line of her GTFO! Operator HUD (its menus as read out, her touches of it) as repeating
  someone's words: someone had said its name. The filter now lets an object's name through.
- GTFO's menus came as script dialogs in Notices and she answered them all by key (Get Cargo, the pages, a hub, Load
  Job, Confirm, Deliver), levelling up twice. "Just still amazed how well it's working."
- Walk mode went on 47 times, mostly to change the throttle between IMs (it ends when the focus leaves it, with its
  sound). A question for her.
- Memory: 1.1-1.9 GB (2.0-2.4 on 718), the heap 1.2-1.7 GB with up to 0.7 fragmented, 77 s of collection pauses in 108
  minutes; allocation 25-31 MB/s while riding, 3-5 still. Smaller than before, but the allocation while moving is the
  next thing (`docs/performance.md` > Sojourn Access on a small PC).
- The first picture: a first frame 25 s after the view came up, the warm-up done 99 s after login (1.5-2 minutes was
  predicted); her processor 20-29% busy meanwhile, so the compiling is one thread at a time, not other programs.

### Rail switches on that freight run (0.1.0.811, the blind tester, 2026-11-02 evening)

Log `tester-20261103T000721-ef2577`, over the Linden railroad's atoll lines: the switch announcements' first meeting
with real switches.
- She was pleased the viewer sees switches now and asked whether she could work them; told W and Shift+W, `//switch
  flip`, `main` and `branch` on the builders' line mid-run; she didn't try a throw that run. Stands to the SLRR
  standard were read right (104 lines named the way set), but 178 lines said "unknown", often several in a few seconds
  (noise while driving), and five said 135-260 m at crossings. Fixed after 811: what doesn't say its setting is part of the switch
  that does within 60 m (most likely the switches' own guide rails, objects of their own on the Linden track), waits a
  moment for its stand's name, distances taken in the frame she's in when said, nothing first due under 5 m; the log
  now records each switch part found, so her next ride's log settles what the unknowns were (`docs/access-app.md` >
  Rail switches).

### A description at home (0.1.0.835, the blind tester, 2026-11-02 evening)

Log `tester-20261103T004420-253fad`, her first session on 835 (the freight run was on 811 from its old folder).
- The view on the processor was ready 53.5 s after login, against 99 s on 811 that afternoon.
- She asked the assistant to describe her, and shared what it said: the room by clock directions, her outfit garment
  by garment, the colours as in daylight because her house was dark. But her cat, which has wings, was "a
  black-and-white cat". Her look was fully loaded in both pictures (191 of 191 surfaces with their colours), so the miss
  wasn't loading. She switched to best (Opus 5.5) and asked again (log `tester-20261103T004927-5e9901`): a finer
  outfit, the room right, 40 s in all (shaders compiled by then), and still "a small black-and-white cat" on her
  shoulder. Both models missing them suggests the wings don't show from the front (folded behind, see-through?);
  asked her what they're like, and Tessel will describe a picture of the cat if she sends one.
- "Describe me" took 93 s: the quick model asked for the room (four pictures) and then her (two), and her full-length
  picture took 21 s to draw (fullbright and alpha compiling for her attachments). Control+Shift+D describes only
  herself (told her).
- She was in Alt+7 with Show on what she wears when she couldn't take her HUDs off: the list fixed after 811.
- Her parcel's stream answered 401 at every retry (about every 35 s): log noise only.

### A ride under the sea (0.1.0.835, the blind tester, 2026-11-02 evening)

Logs `tester-20261103T014229-b71db4` and `-015035-cbbb25`: her first rides recorded on Second Life, on her partner's
engine along a mainland railway and through a glass tunnel under the sea, there and back. Her note: "I thought there
might be more to the description, and I thought I had it set on best."
- Four recordings. 6 s (C twice). 29 s, ended as "the ride stopped moving" before the train set off: flipping the
  engine round at the platform moved it a few metres at once, which counted as the ride having moved. 5 minutes, ended
  by the five-minute cap while still rolling: 101 moments, 51 pictures drawn on lavapipe (one every 6 s or so, 0.4-3.6
  s each, the processor 37-79% busy), 20 thinned past 40, 16 of the 31 kept sent to the telling. The way back, at 11
  m/s, also cut at five minutes: 40 drawn, 38 kept.
- Best applied: told by Opus 5.5 (low effort), 37.7K and 39.3K tokens in. But in 134 and 146 words, the tunnel a
  sentence or a clause ("ran for over a minute through a glass underwater tunnel"); the 6-second ride's telling had
  121. The brief telling was about 100 words whatever the ride's
  length, and nothing said Shift+C again tells more. The 6-second ride's said she was standing on top of the engine:
  a moment taken mid-crossing, before the region had sent her seat.
- "The sounds went faint, I still hear them" in the tunnel: Second Life's underwater rolloff (5 against 1), which
  viewers apply by the camera's height against the region's water alone, so in a dry tunnel too (a sound 4 m away at a
  quarter of its level). Not the processor: faint rather than broken up, and the pictures never saturated it; but
  underruns after the first 2 s weren't logged, so that couldn't be ruled out from the log.
- Fixed after 835: a brief telling grows with the ride (about 50 words a minute past the first, some 300 for five
  minutes, up to about 500) and gives a long alike stretch what it looked like; "Describing your ride." adds how to
  have more; a seat held over a crossing, and a ride counted as moving only after 3 s of it; rides recorded for up to
  fifteen minutes, a long one's moments thinned for the telling to about a five-minute ride's size; under the water
  beneath a roof (a ray up from the ear meeting something solid below the surface) sounds keep their usual falloff in
  Access. Logged now: going under and above the water, roofed or not, with the heights, and the speakers running dry,
  every 30 s while it happens (`docs/access-app.md` > Recording a ride and Dry under the sea, `docs/audio.md`).

### A solo run home, an accident and a wrong turn (0.1.0.835, the blind tester, 2026-11-02 evening)

Log `tester-20261103T025050-3d7644` (19:18-21:50) and two notes on the builders' line: a freight run from Lunalis to Bhaga
on her train, then home toward Concinna with cargo, alone (someone she shares her home region with, most likely her
partner, joined after the wrong turn).
- **The accident** (21:34): leaving Bhaga at 35-50% throttle, the train crossed into Fishii four seconds out and she
  was left standing in Bhaga ("stood up from Zephyr" 21:34:17; no crossing of ours began, and the Fishii circuit was
  alive, as the teleport into it found). The train said "Driver missing! stopping..." and IMed a SLurl; she teleported
  to it 16 s later, sat, started the engine and was moving again 90 s after the stand-up, and called it easy. Second
  Life's crossing, as on 2026-11-01; but Access said nothing itself (the stand-up was only a log line). Now: "You're off Zephyr: it went on
  without you." when the seat is lost while it was moving, not at a crossing.
- **The wrong turn** (21:37): at 11 m/s through Crumbi and Tenera she went on toward Webworm instead of Velox, noticed
  only in Anilis and stopped in Pawpaw for help; they came, she flipped the train and drove back through Tenera
  onto the right line. She said she hasn't worked out the switches yet and hadn't touched them. The two switch lines
  before the turn were "Switch in 17 metres, set to the branch, Crumbi Station NCB." (21:37:29) and "Switch in 13
  metres, set to the main line, Crumbi Junction." (21:37:41), the first most likely the one that took her off her way. Both
  were cut off within a second, by "Guide, 15 metres." and by "Now in Tenera, still on Zephyr." (a walk line is an
  answer, and an answer cuts in); 25 of 146 switch lines that run were cut so. Both also came 1-2 s before the switch,
  too late to throw at that speed, and neither name says which way is home. A third, "set to: unknown" at 19 m
  (21:37:45), was most likely that switch's own rail (folded into its stand after 835). The 811 build's noise was still
  there: 91 of 146 switch lines "unknown" (fixed after 835), and 56 warnings ("Guide", "Atoll Railroad Section",
  "Something") in the 76 s before the turn (only what the train closes on after 835).
- Fixed after 835 for this: a switch's line is heard out before walk mode's other lines that come by themselves (the
  crossing, the speed, the next switch); warnings, her keys' answers and "Track ends" still cut in. Shift+W waits for a
  switch being read as it comes up instead of saying "Still reading a switch". Open (ours): saying switches sooner at
  speed, telling facing from trailing switches, and saying which way each way leads (a region or a direction), or that
  a switch is set against the way she came; the next log's `switch:` lines say where stands are and what they hold.
- Later, from Crenulate, the train went into Pavonia by a switch "set to the branch, Mountain Branch" (21:48:34) rather
  than on to Clearwing; with company riding along she stopped there, cancelled the job and teleported home. Whether
  that was meant isn't in the log.
- "Now in Nessus; the crossing left you standing" (21:38:43) while she sat on: the 811 run's false verdict, fixed after 835.

### Keys a vehicle wants, and a car after her partner's (0.1.0.877, the blind tester, 2026-11-03)

On the builders' line (16:42): some vehicles land on C, and W, A, S and D stand for the arrows for people without
them, so walk mode's letters (W for switches) looked like a clash. Told her a script sees only controls, so Page Down
is C and the arrows are WASD (`docs/access-app.md` > Walk mode); a layout where the letters drive waits until someone
needs it (she knows nobody yet). The mouse button, which some vehicles and weapons take, had no key: Enter now.

Log `tester-20261103T220106-714894` (16:38-17:01), her note: trying to follow her partner by vehicle "did not work as
well as I had hoped... it was worth a try".
- `//follow` while she sat in a car (16:53) only said he'd sat down and stood still: following walks. Now, seated, it
  says so and puts a beacon on the person instead.
- The beacon on him was lost ("isn't here now", 16:46:47) a few seconds after his car crossed into Astarte: it looked
  for its target only in her region. Now it looks in every region in view, then the map's positions.
- "It is too loud. I can't focus on the beacon and you": she could aim by the beacon (at its quiet default) but not
  hear it clearly over something loud; "Sooooo loud" when the cars came out (16:44:53). What was loud is asked; Alt+0's
  beacon and world volumes pointed out meanwhile. If it's the cars, the open question is a beacon that rises over the
  world's sounds (the mixer has no level meter yet) or the world ducking under it.
- `////unsit` (an extra pair of slashes) wasn't taken; now extra slashes are.
- She said the two cars' own sounds were too loud to hear the beacon even at its loudest: now the world's sounds go
  softer while a beacon sounds (`walk_beacon_duck`, much softer by default).
- Her next log (`tester-20261103T224954-3fb77f`, a freight run on Zephyr, 17:03-17:49) was the first with the switch
  lines: most switches said only 5-25 m out at 11 m/s, found late (`docs/access-app.md` > Rail switches has what it
  showed and what changed). She didn't try a throw ("things were going so well"); that's next, with her partner.

### About Land, auto-accept and a face out of sight (0.1.0.887-891, the blind tester, 2026-11-03 evening)

- About Land at a jazz club (pasted on the builders' line): right, its parcel's own description ending in a full stop
  said twice (fixed). Her log had only `//kland`: on land not hers About Land's lines are others' words, so logs leave
  them out, and checking it means asking her.
- She asked for Radegast's auto-accept of animations and inventory. Inventory was there (Alt+0, since 11-01); animation
  permission given without asking was built that evening (0.1.0.891, `docs/people.md` > `auto_grant_animation`), and on
  Second Life a dance ball's request was granted and said: "Woohoo!"
- That log carried two people's display names whole, from her Chat-Range HUD's "Display (username)" lines; every
  earlier log had some too. Fixed in 0.1.0.894 (`docs/access-app.md` > Sending a log), the copies scrubbed and the
  relay's deleted, and she was told.
- "Is there a way to make the camera turn?": a description said someone 7 m behind her had a face it couldn't make
  out. I first told her that was the place's description and to describe the person; her next log
  (`tester-20261104T015531-32cb62`) showed she had, the pictures from the front with the look all loaded, and another
  woman there "too dark to be sure of her clothes' colours": a dim club with the sun up, never relit. Corrected to her,
  and dim pictures were relit from then (people and things always, since 2026-11-11: `docs/access-app.md` > Describing).
- Building: she'd like a big place to play where she can't do damage; a sandbox was suggested, and we offered to find
  her one. Then, on a builders' grid with her partner (log `tester-20261104T025444-eb47e5`, 21:30-21:51): a box named,
  hollowed 50 and made red, taken into her inventory; a box and a sphere made; a script added to the sphere. What got in
  her way: help said whole as one line (asked six times; now a window to arrow through), `rename` (now `name`), wanting
  the list of shapes (now `shapes`), `//build:` copied with its colon (now taken), and help's text pasted into nearby
  chat by mistake. She asked whether she could start with anything but a box (`new cylinder`) and found it herself.
- The same log's ride (891, 21:12-21:26, her train past Achemon to Concinna) had switches said 40-95 m ahead, mostly
  50-70, against 5-25 m on 877. Missed: two Jodis stands whose names came only 7-8 m out, and two found 100 m out that
  left the straight band on a curve. Its `rails near` lines (29 stands, each rail's place, length, facing, phantom)
  show rails named Guide that are phantom too. Worked through offline, she rode over phantom Guides while Alternate
  Guides were almost always phantom, so the name says which rail is in use. Built after 900: the track followed by its
  rails, switches said by distance along it, facing or trailing, which side, set for or against her
  (`docs/access-app.md` > Rail switches > Along the track).

### "Set against you" and a line changed under her (the blind tester, 2026-11-04)

On the builders' line (08:46-08:49): she didn't know what "set against you" meant, so a trailing switch now says "set for
your line" or "set for the other line" (next build); told her, and the question of what her train does at such a join stands. She also said she was
put on another line the night before with nobody changing anything. Her log of that evening
(`tester-20261104T025444-eb47e5`) has a switch ahead changing to the branch by itself in Sinica (21:21:12) and no throw
of hers; asked whether that was the one. Old versions: told her to delete them, the data folder being shared.
That evening: her partner had been flipping switches, which would explain it. Her first throw on Second Life worked (18:06).
She couldn't copy switch lines to show him: all of them now go in the chat history too.
- Her waterslide (log `tester-20261105T022157-9f7eb3`, Pipe Dreams, 909): she started recording on a pose ball at the top,
  stood to walk to the tube, and that ended it; the slide itself, 38 m down and across a border into Pigra, went unrecorded,
  and the telling described her standing at the top ("It was ... weird"). Standing before anything moves no longer ends it.
- She'd like objects further off in the list, as in Radegast: Alt+0 now sets it, up to 125 m. Asked how far she wants;
  past the draw distance means raising that, which costs memory on her PC.
- "Weird repeats of lines" at trivia again, log `tester-20261105T020834-5121c6` (909): 67 of 552 lines read were cut lines
  said again, 47 of them within a second of another line being read, so heard out of order. Now none once someone's
  newer line has been read, and a switch for the whole thing in Alt+0. Whether the rest she'd really heard is a question
  for her: the logs leave the words out.
- Her log `tester-20261104T232958-ce996b` (903, her first with the track followed): the throw at Clearwing took 1.1 s,
  stand and rails together. Of 53 switches passed, 11 facing ones were on the line said, 15 unclear from the log, and
  one at Tenera (18:14) went the other way: its stand changed to the branch by itself 6 m short, said as it happened
  ("now set to the branch, Tenera Turnaround"), too late to do anything. Set against her at a
  join, the switch changed for her within seconds at Sinica and Euclidia, as the standard's merge sensors would, and
  she rolled through: so the line is mostly noise. Next: hold a trailing "other line" back until nearer, or say less.
- 903's "The switch ahead is now set for you" as she passed a stand with two places on it wasn't a change: no longer said.
- Log `tester-20261104T233532-b3e620`: steering a worn-or-sat chopper by hand after her partner by the beacon, which she
  called a little better but very jerky, and asked for vehicles following each other, closer than 10 m too: Driving
  after someone (`docs/access-app.md`). Horses she sits on are walking, by her note; that log is to come.

### Tip jars, ski balls, a motorbike and an igloo (0.1.0.918, the blind tester, 2026-11-05 and 06)

On the builders' line and five logs (our words). What's built from it is in `docs/access-app.md`.
- **Tip jars and vendors**: she couldn't pay a tip jar at a club and paid the person instead. Objects had no Pay or Buy at
  all, though Radegast has both. Now Pay..., Buy and Take a copy on objects and parts, the list saying which take money or
  are for sale, `//pay` and `//buy`.
- **Describing a club, person by person**: she found it much improved, and liked that it said why when it couldn't
  describe someone. Two of them came out as plain stand-ins, over the complexity limit. Someone asked about is now drawn
  whole for their own pictures. A small winged avatar was told as a ginger cat by its look; others' worn names aren't
  used for what they are (open).
- **Following by motorbike** (`//follow` seated, on her chopper, 20:04 onward): it worked and delighted her; she told a
  friend who finds driving hard. She found 3 metres the closest she could ask for; now 1.
- **A ski ball's dialogs**: sitting on one sent an attach question up to three times over its menu of runs, and her next
  number went to whatever was selected, which she hadn't heard (the answer's words cut NVDA off): Yes, Yes, then Block on
  the question when she likely meant a run, then 6, then No twice. So the same question asked again makes no second
  notice, the next notice is said after an answer with its keys, Block is on B and never a number, and there's a block
  list to undo it (Control+Shift+Y): nothing could unblock in Access before. That evening, on 926, she blocked an object by the
  notice's Block button by accident, and unblocked it; so the buttons are a way to slip too (the key on the ski run isn't known), and
  "Blocked" now says what undoes it. Her own things no longer offer Block in Objects (it shared K with Take).
- **A lift's top**: `//go` to her partner found no way to walk and no teleport that would land (a parcel with a landing
  point), and her `//go` without the word `person` only got the usage. He offered her a teleport, which worked. Now
  `//go NAME` takes a name alone and the line says asking for an offer may work.
- "You're off the ski ball: it went on without you" when the run ended and let her off: now says only that she's off it
  while it was moving.
- **Building an igloo and a tunnel** on a skybox with commands from Tessel: she did a lot, but couldn't find linking (one
  word in the help); told her `link NAME`, and it worked on Second Life, a sighted friend confirming. Now a line of its own
  in the help, `link` alone lists her things, and Objects' menu has Link to what I'm editing. Two of her commands went to
  chat as `build size 1.2` (no slashes, in Say): a question whether Say should take them.
- **Her log's filter**: objects named as a person (someone speaking through one, a relay or a tiny avatar's) kept their
  words in shared logs. Fixed in Send a log; 17 local copies scrubbed and the 10 still on the relay deleted.

### Notecards, settings and an old build (the blind tester, 2026-11-07)

On the builders' line and log `tester-20261107T150406-3fe8fa` (our words). She doesn't mind Alt+0's long list herself but wants it better organised, and chose groups over a box that narrows it. Editing and saving a notecard worked, on 0.1.0.929, an older build than she'd been told to use, without her knowing: a line at login now says when a newer build is out. She found links in notecards couldn't be opened: Control+K now. Play on a sound in the world, as the official viewer does, is fine. Paying an object worked, the first real payment (11:05; the build not checked, 947 or 955).

### A folder given, a folder deleted, and links (0.1.0.947, the blind tester, 2026-11-07 evening)

On two of her logs (our words; `tester-20261107T230900-bc507d` and `-231955-9e9c36`, on 0.1.0.947). What's built from it is in
`docs/access-app.md` > Keys. Three more that night on 0.1.0.955, named here only on 2026-11-13: `tester-20261108T011605-ff1e19`
(the deleted things had gone, shown once she logged in again: the folder shown again, above), `-034027-39129c` (renaming,
moving and making notecards, "very well") and `-020428-3496db`, below.
- **Sound on her TV** (`-020428-3496db`, 21:02-21:04: "Still unable to switch to the computer monitor speakers"): she
  chose "4 - 2D HD LG TV" (an AMD HDMI output) five times; each time it opened, took its sound without a gap, and the
  chime reached it at -44 dBFS against -24 sent, where her PC's speakers got -23 of -24. Windows had the TV's output at
  25% (her speakers at 42%), which accounts for the 20 dB, and the TV's own volume comes after that. So Windows passed
  the sound on, quietly; whether the TV played it the log can't say. Since 2026-11-13 a switch to an output Windows has
  under half says so ("But Windows has its volume at 25%."). Worth asking her whether other programs' sound comes out of
  the TV (NVDA with the TV as Windows' default, say), and whether the TV's own volume is up.
- **Giving a folder** of folders of landmarks was said given when nothing went. The library underneath gathered the
  folder item by item (nearly two minutes), then dropped the give without a word when it held more than it would send
  (66 things, folders counted, by the look of it); the confirmation had counted only the folder's own items, none. She
  gave its folders one at a time, which worked. Now everything under a folder is counted on the confirmation, a folder
  over Second Life's 42 things is refused before she's asked, saying why, and the answer says what went.
- **Deleting a folder** said it was in the Trash, but it stayed in her list until she logged in again (it had gone):
  the tree was shown again where the folder had gone to, not where it had been, and she deleted it twice more. Now the
  folder it left is shown again, and a move on Second Life waits for the inventory service's answer, a refusal said.
- She keeps copies of a pet in every outfit and wanted links instead: Paste as link, and Save your outfit, as links.
- A landmark's About, as the official viewer shows it: About the place on its Applications key.
- She wanted a way to delete for good, with the Trash so full: Delete on something in the Trash already does, and
  Empty the Trash (on the Trash's Applications key, or `//emptytrash`). Worth telling her.

### A scooter trip and a train that lost its guide (0.1.0.996, the blind tester, 2026-11-08)

Two logs with notes (our words). Her typed commands aren't in a log, so what she asked for is read from the answers.
- **A trip after her partner** (log `tester-20261109T000242-e7edba`, 18:38-19:02): each on a vehicle, she on a scooter, he
  on a motorbike, through nine regions from Concinna to Dicycla, she driving after him by `//follow` seated (with a gap of 3 metres, by
  her word afterwards). She found it
  hard to follow him and felt her steering weaved. What the log shows: by the look of it, twice it ran into something
  beside the road and, not moving, let go (once a thing 2 m ahead; later she told him the scooter wasn't moving), and
  once she was stuck against a wall at the water's edge for two minutes. At the crossing into Berbera (18:52:35) it said he was getting away 289 metres off
  (her position, mid-crossing, in the frame of the region she'd left: 918 said 276 and 226 the same way), said a walk's
  "Blocked.", and ended two seconds later because the scooter's keys read as let go; they stayed so, `//follow` giving
  only a beacon, until she stood and sat again two minutes later. At 18:55, just after he crossed into Diloba 20-30 m
  ahead, her session had him in no region in view nor on the map within 5 s, so it let go and stopped, and `//follow`
  with his name found nobody 20 s later (by then he may have gone on past the regions in view). Why it couldn't see him
  isn't in the log; why the keys read as let go (the scooter's script, Second Life's word at the crossing, or our count)
  isn't either. And the log couldn't show the steering at all: driving after someone wrote its lines only to a debug file, and Send a log left out every line
  naming her scooter (166 lines with words left out, the answers among them), its name most likely being in an IM she was
  sent. Done since (`docs/access-app.md` > Driving after someone): its lines in the session log, the seat's name on a line
  of its own; the controls scripts take and let go of logged, by region; over a crossing the keys held as they were until
  the seat is sent, nothing decided or said meanwhile, and no "Blocked."; keys reading as let go tolerated for 10 s after
  a crossing, then said with the crossing and that sitting again usually gives them back; `//stand` ending it in its own
  words (it said the vehicle had let go of her keys). Then (`docs/access-app.md` > Driving after someone > Losing sight
  of them, Why it couldn't see him): losing sight of him, it drives on where he went, a bounded way (over a border, 25 m into the next region), says
  so, and picks him up again or says it couldn't; a copy of him in a region whose list leaves him out (the one left where
  he crossed out) no longer counts, nor one on a seat not sent; Core no longer drops a copy as a ghost in its first 12 s in
  a region (it may have dropped the one Diloba had just sent); the draw distance and the neighbours ruled out (Diloba was
  connected); the aim close behind him no longer a point a metre or two ahead (a likely cause of the weave at her 3 m
  gap); the keys a script takes counted in the order they come (they were counted in any order, so a release and a take
  close together, as at a crossing, could leave them untaken: a likely cause of her keys reading as let go). Open: which of the
  three it was (her next log's "lost sight" line says what each region had of him), whether the aim fix settles the
  weave and the run-ins beside the road (the convoy lines say where it aimed), and whether the keys now stay over a
  crossing (the `controls:` lines show each region's word).
- **"No sensor"** (log `tester-20261108T152458-7ace75`, 10:01-10:24): a GTFO freight run on her train Zephyr from
  Neumoegen toward Crenulate. In Idia, at 13-16 m/s, the train itself said in chat that it found no guide and idled;
  Access said "Stopped." (its speed line: the train had stopped itself before the track-end stop could act). Her note
  means that message, not anything of Access's: nothing of ours went quiet. She re-railed, flipped it and set off again
  at the lowest throttle, and the train said the same within seconds at the same spot, so it wasn't speed. Its sensor
  looks for rails named Guide; the log's rails show the line she was on, west along Idia toward Jubata, with Guides up
  to about 12 m short of the border and nothing the app knew going on from there, the nearest rails past the border
  Alternate Guides (the name a switch gives the way it isn't set for). So the track's, most likely a switch at or past
  the border set for another line, or a gap. Whether that line was the way to Crenulate or a yard's isn't clear: a
  Ribeata switch said 65 m ahead was set to the branch toward a private railyard, and Idia's own stands (described only
  by numbers) were met as she went. A question for her partner.

### Deletes refused, and driving after her partner backwards (0.1.0.996 and 1045, the blind tester, 2026-11-08 and 09)

Three logs with notes (our words).
- **Nothing could be deleted** (logs `tester-20261109T024202-97c929`, a landmark, and `-20261109T223724-96ebc6`, an
  object, twice; both 0.1.0.996): each Delete was confirmed and then said "Not deleted: Second Life's inventory service
  refused the move ... (HTTP 400, Bad Request)". Second Life's service refuses a move sent as a PATCH of the folder
  (`parent_id`), which is how LibreMetaverse moves things wherever there's a service, and how ours did since 2026-11-08,
  when we began waiting for its answer; the official viewer moves by its UDP messages. So before that, on 0.1.0.955 and
  earlier, moves and deletes were said done and never happened on Second Life (her landmark folder that stayed, 2026-11-07;
  most likely also the notecards she moved into a folder that evening, which would be back where they were). Since
  2026-11-09 moves go as the viewer's, confirmed by reading the thing back (`docs/inventory.md` > Moving and tidying). The
  tests' stand-in for the service had taken the PATCH; it now refuses it as Second Life does.
- **Driving after her partner** (log `tester-20261110T011218-179b76`, 20:07-20:12, 1045, her police scooter after his
  vehicle, `//follow` at 10 m): she ended up going backwards and not following, and wasn't sure whether it was her doing.
  It wasn't: from 20:10:15, closing on him where he'd stopped, it held Down to keep the gap, the scooter's Down is reverse,
  and it went on holding it as the scooter backed at 6-10 m/s, since its speed alone read as closing faster; steering as
  it backed, she circled him at 1-12 m for 35 s. Fixed (`docs/access-app.md` > Driving after someone > Never backward);
  the test's kart turned out to back the same way at 4 m/s, unnoticed. Also in the log, not changed: twice Up was held 7-8 s
  with the scooter not moving before it went (maybe an engine that starts on the first press: a question for her); the
  first try crawled at about 1 m/s with Up held while he drove off, and she took the wheel; and over the crossing into
  Sponsa it lost sight of him, found him again by the map 44 m off, and drove on along a tunnel the other way (he was behind
  her, to the right) until a barricade stopped her 115 m from him. Turning round now slows to 3 m/s first, which a tunnel may
  still not allow. Then the parcel returned her scooter, and Delete on it in Objects, still listed, asked to "Delete
  someone (372d0a97)" and said "object not found": a thing gone from the region is now "something no longer here", and
  that error "it isn't here any more".

### Song titles at a jazz club (0.1.0.1074, the blind tester, 2026-11-10)

A log with a note and two messages (our words; log `tester-20261111T024341-8bc53c`, 19:21-21:43, at clubs, tipping DJs).
- **Titles not in chat**: she wasn't sure the stream's titles were being read; they were, 28 times, but nowhere she could
  look back for one she'd missed. Each is now a line in the nearby chat history too, and a song a station sends twice in
  other words within seconds is said once (`docs/access-app.md` > Read-out).
- **Each tip said three times**: the answer, Second Life's "You paid ..." and that again after a key cut it off. Her own
  payment is now said once, by its answer (`docs/access-app.md` > Paying and buying things).
- **The window held 13 s** at 19:25:39, with nothing in the log around it to say why; her logs since 11-08 have 16 holds of
  a second or more, most while starting (1-5.5 s, the 5.5 said aloud). The log now has the held thread's stack at 2 s
  and 10 s (`docs/access-app.md` > How it fits > The window's thread held), so the next one names its cause.
- Also seen, nothing changed: for a minute the parcel's music address was what reads like a line of chat (a club's stream
  changer taking it, most likely), refused as no address and logged, the music back when a real one was set; near the end she went through Reading out's rows and
  left "Lines cut off by my typing said again" off, maybe on purpose: a question for her if it stays off.

### Driving after her partner, smoother, and once past him (0.1.0.1074, the blind tester, 2026-11-11)

A log with a note (our words; log `tester-20261111T151640-1c4767`, 09:47-10:16, her police scooter after her partner's
motorbike across sixteen regions, `//follow` at 10 m). Much smoother than before, she said; once she zoomed past him and
neither could tell why.
- **Past him at a crossing** (10:00:18): she crossed into Rivula about 6 m behind him, where he'd stopped 5 m over the
  border, and the region took 6 s to send her seat (by the log's whole seconds the other 19 crossings' seats came 0-1 s
  on). Until the seat comes nothing is decided, by design, so nothing slowed her: the scooter ran on about 70 m and ended
  22 m past him. It was most likely going full ahead because of one Up press a moment before, against a backward reading
  (next item): the log has Up let go then, and over the 70 m (about 6.5 s) the scooter went faster than before the
  crossing, so her scooter seems to hold or keep its drive with no key held. Now, past 1.5 s without the seat, where she
  is comes from the region's map list, which names her, or where she was last known, near him or past him Up is let go,
  and Down pressed, once for each new pace by the lists, where that pace is sure (`docs/access-app.md` > Driving after
  someone > Over a border).
- **"Going backward" for a moment**: the seat's velocity as the region reports it, logged every 2 s, swung between 0.2
  and 13 m/s while by where it was the scooter covered a steady 7-9 m/s, and 85 times in the drive it read backward, for
  125-578 ms, each time pressing Up and stopping the steering. 84 of the 85 came in the same 2 s log line's span as the
  drive's own Down presses or the one after (it pressed Down in 61% of the spans, so about 15 would have come with no
  press near by chance, and 1 did), so they were
  most likely real kicks into reverse by its Down presses, her scooter's Down being strong (on 11-09, with a key held
  steady, the readings were steady), and an Up press on her scooter can send it full ahead. Now: what one Down press
  takes off is learned from each press, and a press goes only where that leaves a margin (else it coasts); backward
  counts when the seat's moves over the last quarter second don't say it's going on forward (so a reading alone isn't
  answered with Up, and real reverse is answered sooner than by the half second's), it isn't steered while they disagree, the room to slow
  is reckoned by the moves, and a Down goes only while both say forward (`docs/access-app.md` > Driving after someone >
  Never backward). At 09:55:08, as the drive let go (next item) with the scooter already going backward at 3 m/s by the
  reading and two of its Down presses in the 2 s before, the vehicle stop started and said "Let go, but it's moving
  backward, at 3 metres a second", and she told him she was going backwards again. The stop had pressed nothing: its
  first press, judged on the tick it began with the reading saying backward, was let go before any key went out. What
  was wrong was the line ("Let go" with nothing sent) and that it would have braked a vehicle already reversing for a
  second as a car going that way, each press adding reverse. Now it says so plainly, doesn't press Down into a vehicle
  going backward by the way driving after someone learned it goes forward, and stops at once if Down speeds it up
  (`docs/access-app.md` > Stopping a vehicle > Already backing).
- **A crossing taken for a teleport** (09:55:08): as he rode into Astarte something put him over 100 m from where he'd
  been, and the drive let go ("jumped away, a teleport most likely"); 33 s later he was 33 m ahead, waiting. The log
  doesn't say what it was (a copy of him or the map list's word, mid-crossing). Now that's a jump only after 2 s of it on
  end (a tick without him, or her own crossing, starts it again); meanwhile he counts as unseen, the drive going on where
  he went, and the log says by what and where he was seen.
- **An independent review of those changes** (an agent given the commit and docs, 2026-11-11) found, besides the
  backward readings and the stop above: the map list's whole metres could make a true 1-1.6 m/s read over the 3 m/s the
  first version braked from, and that code ran briefly in many ordinary crossings; two lists close together brought back
  the pace from before the crossing; with him lost before her crossing it braked against where he'd been; and "2 s on
  end" wasn't. All four fixed (`docs/access-app.md` > Driving after someone > Over a border), and its test of the
  backward readings made to test only what it says.
- **Pushed into things with Up held**: three times it drove her into something (a table, a wall, a sign, each named by the
  warning of what's ahead), and "isn't moving" came after 34 s and 16 s, and once not at all before he came back: the
  word read 0.3-0.8 m/s now and then against them, each restarting its 6 s. Now that's judged by where the seat goes over
  2 s. Why the way led into them (corners cut aiming a second ahead along his way, or his own way through) is open.

### A waterslide, a tour jet's pose, and the switch she tried (0.1.0.1074, the blind tester, 2026-11-11)

Two more logs and messages that morning (our words; logs `tester-20261111T152536-6b999a` and `-165652-0ce739`).
- **A ride recorded and told**: a waterslide recorded by `//ride` and told on asking; she thought it worked this time.
  Later a tour jet's ride was told in four passes of the same recording without trouble.
- **A pose that stayed**: after a tour jet put her off at its tour's end, her partner saw her still in its flying pose;
  a stop-all ball said it stopped two animations, and our own stop wasn't tried; logging in again fixed it. Most likely
  an animation override (`llSetAnimationOverride`) the jet set and then took with it when it deleted itself: only a relog
  clears one then (the wiki), and our revoke 1.5 s after standing reached nothing. Access now says when a seat may have
  left its pose and that logging in again clears it, and logs what still animates her after standing
  (`docs/assistant.md` > A seat's leftovers stop by themselves). Her next such log says what Second Life reports.
- **"Lines cut off by my typing said again" off**: she turned it off to hear whether chat reads differently, and will try
  both (her answer to our question).

### A tour car's border, her freight HUD after borders, a delivery said four times, a bag left on (0.1.0.1095, the blind tester, 2026-11-11 afternoon and evening)

Seven logs with her notes (our words): `tester-20261111T215734-f0bf01` (16:16-16:55), `-215615-ceb5d0`, `-230153-b16ce6`,
`-231709-90a91f` and `-232109-5647bd` (the session from 16:55), `tester-20261112T000556-68e47c` and `-005543-7b09dd` (from
18:21). The two in the middle weren't read until 2026-11-13.
- **A place her partner sent that wouldn't open** (`-230153-b16ce6`: her partner "tried to send me a link to teleport to ... the
  viewer said it couldn't open it"): an IM from him at 18:00:29, then "That link is for something Sojourn Access can't
  open." at 18:00:51. The log leaves his words out, so which link it was can't be known. What Access refused then: a map
  link with anything after the place (`?title=...`, which the map's own links carry), and every `secondlife:///app` link
  but a person's or a group's, among them a parcel's, an event's, a classified's and a landmark's. Now those open (a
  parcel, event or classified asked where it is and teleported to; her own landmark teleports, someone else's is said to
  be theirs to give), anything still refused says what kind it was, and the session log has a refused link's shape without
  its words (`docs/access-app.md` > Links). How he made the link (the map's Copy SLurl, a landmark dragged in, the location
  bar) is worth asking.
- **A train she couldn't stop at its station** (`-231709-90a91f`, read 2026-11-13: "there was a period when I was trying
  to stop the train at the end, but I could not do anything"; to her partner then, "Access stopped responding"): her
  Gazelle (an Atomic Infinity Free Train, throttle 20%) on a GTFO run into Sweetbay. As GTFO said she'd arrived
  (18:15:41), the window's thread was held 9.0 s, 2.1 s in waiting on Windows to write a line into her chat log; "The
  window isn't answering" at 5 s, back at 18:15:50. Then F3, F3 or Escape, F3 acted (walk mode on, off, on: pressed in
  the freeze and queued, or after; the log can't say) and Down took the throttle to idle by 18:15:56, the train stopping
  29 m on from where it was at 18:15:38. The crossing six seconds before had gone through (controls taken again). Ours:
  chat logs are written by a thread of their own since 1110 (from this hold, read then as two of 2.1 s: the stack line's
  time is when it was read, not how long); now a tab's earlier lines don't wait on a slow disk either, a hold's line says
  the thread's own processor time over it and the PC's load and memory, and keys handled late are logged by name. Why
  one line took her PC seconds the log can't say; its stack took 8.4 s to read, against 0.2-1.2 s in her other logs, so
  the PC as a whole was struggling then.
- **Off a tour car at a border** ("we got kicked off", sent with the start of the next session's log): a tour company's
  demo car, driving itself on its route with her and her partner aboard (not driving after someone), crossed into
  another region and stopped. The crossing went through as the day's others did (the region took her: its greeter
  welcomed her), but that region never had her seated on the car: 15 s on it was "it hasn't said yet whether you're
  still seated", the car's tour talk stopped mid-script (the same company's boat that afternoon went on to its closing
  lines), and its sit pose stayed on her, so she logged in again. Both riders lost it at once, so most likely the car
  didn't get across, or the land there refused or returned it: Second Life's or the land's doing, not the viewer's, as
  far as the log shows; it can't say which. Ours: nothing followed "it hasn't said yet", and the pose notice said "after
  you stood up". Now what the region says next is said ("X has you standing now, off Triumph TR3."), the notice says
  "now you're off it", and the log says at 15 s (and 2 minutes) what the region had of her avatar and where the car was.
- **Her GTFO HUD out of reach after borders** ("I suddenly could not touch my GTFO HUD"): `//touch gtfo` answered
  "Nothing called gtfo within 10 metres or on your HUDs" for over a minute after her train crossed a border, and in the
  next log after three more borders, until something drew a picture. The view out of sight places HUDs from the
  region's scene, which only a drawn frame read, and on her processor there are none between pictures: a region come
  after the last picture (her ride recording had ended ten minutes before) had an empty scene. Ours; the HUD's region
  is now read whenever a HUD is asked about. Reproduced on the local grid drawing on the processor, her words exactly,
  and passing after (`docs/access-testing.md` > your HUDs).
- **A delivery's words read again** ("the area where I delivered got read out again after it was completed"): GTFO
  says "task complete" in a menu and the same words in chat a second on (the HUD's doing); then a key or her typing cut
  the menu off in its first seconds, so it was said again, and while she typed the chat line was said again too: four
  times. The repeats were ours. Now a menu cut off isn't said again when its words were heard after the cut in another
  line, nor lines with words a menu has just been said again with (`docs/access-app.md` > Read-out). Whether a HUD's
  chat line repeating its menu should be left unread (kept in the history) is hers to say: asked with 1113.
- **Take off acting on a pose ball** ("It said I had removed the pose ball"): she opened the actions on her bag in what
  she wears, the list most likely refreshed under the open menu to things nearby (their count was said as it opened),
  and Take off acted on the row in its place, a pose ball she'd sat on, saying "Took off M Pose Ball." with nothing
  taken off. Now the menu acts on what it was opened on, Take off is only for what she wears and says so once it's off,
  and the agent API's detach refuses what isn't worn. What refreshed the list, read again with 1110 out (by two
  independent reviews): at 19:49:18 two refreshes landed under the Show after the one each was for, "No HUDs, 89 things
  worn." (others' attachments counted as hers) and "10 objects." (her own ten, under Things nearby), so the list held
  her worn things while Show said nearby; at 19:49:25 she opened her bag's actions in it and the refresh for nearby
  ("200 objects, 663 more further on.") filled the list with the nearest 200, the focus falling to the first, the pose
  ball she'd sat on that evening. The next build empties the list when Show changes and drops a refresh whose Show
  changed while it looked.
- **Send a log held the window** 2-3 s filtering her log on its thread; now off it. Her ten logs a day on the relay ran
  out that evening (thirty since).
- The 16:16 session otherwise went well, she said: a submarine, a sailboat and the car toured and told by `//ride`.
- She suggested merging the two viewers into one with a toggle, as likelier to be approved by Linden Lab. The user
  (2026-11-12): a good suggestion, but they stay distinct (CLAUDE.md > Decisions); told her with the reasons (#282).

### A grid drive: a tour HUD's keys, a boat's pose, a held start (0.1.0.1132, the blind tester, 2026-11-14 evening)

Three logs of one session (from 18:32) with her notes (our words): `tester-20261115T000245-efbf66` (trying to press
Page Down for a HUD and it wouldn't work), `-000548-8f76ea` three minutes on (Page Down had worked, she didn't know
how), then a "grid drive" with friends, riding in their vehicles and a boat over some fifteen regions, and
`tester-20261115T023115-be2a8c` ("the first part", 21:31: the HUD's Next Leg wanted a second's press she didn't know
how to give, and a sitting pose from the boat wouldn't stop, so she logged in again); after logging in again,
`tester-20261115T034838-71c24f` (the drive's end, 22:48: the prizes missed, "not the fault of the viewer"; a beamer for
them wasn't within reach of Objects). She flew for the first time ("would never have been able to fly in Radegast").
What's below went into 0.1.0.1136, sent that night (#287).
- **Page Down for a group drive's HUD**: given to her and worn at 18:55, it took the movement keys passed on (as many
  HUDs do), then Down alone; its own lines say to touch its circle for its commands. She pressed Page Down where she
  types (most likely Say), where it did nothing; touched the HUD from what she wears (nothing answered: the circle is
  likely a part of its own); and said in chat she couldn't get it to work. At 19:03:14 she went into walk mode and out
  four seconds later, and the HUD teleported her to the drive's start: in walk mode Page Down is CONTROL_DOWN, which the
  HUD had taken. So walk mode served a HUD as it serves vehicles, but nothing said so where she tried. Now Page Up and
  Page Down where she types say walk mode is where they go, and that a script gets the key there when one has taken it;
  the keys' F1 lists say so too (`docs/access-app.md` > Walk mode).
- **The window held 13.4 s** at 18:33:56, a minute and a quarter after login, as the view out of sight finished its
  first drawing (43.7 s): its own processor time 0.0 s, the PC 16-27% busy, 0.5 GB of its 6.3 GB memory free and this
  app at 2.1 GB, so most likely Windows paging. Eleven volume presses came through after it. The stack was read 6.2 s
  late and shows only those keys being handled. No other hold over a second in the half hour after, against several a
  session on 1095; the inventory's 698 nodes cleared in 101 ms. If the drive's logs show more, the lever is the open
  thread on starting the view only when needed (`docs/performance.md` > Sojourn Access on a small PC).
- **A button to hold for a second**: the HUD's buttons sit hidden until its circle is touched, so its picture showed only
  the compass and the assistant could name them only from the parts list ("nextleg", "reroute", "notecard", "sign0"
  to "sign3"), which it said. She touched Reroute by mistake (it sent her back to the start), then Next Leg twice, and
  each time the HUD said it "must be continuously pressed for at least one second": a touch here was a click, the
  press and release together. Now Enter on a part, a face or a read button holds the touch while Enter is held, up to
  10 s, and says how long once it's over half a second (`docs/access-app.md` > Parts and faces). A press of Sign 0 as
  the car crossed into Maryport found nothing ("could not find the target": a worn HUD's prims come again from each
  region crossed into); a touch by id now waits up to 3 s for it.
- **The boat's pose stayed on her** (21:28): she stood and took a friend's teleport the same second; the stop for the
  boat's animation went to the region she was leaving, and the next had her still in it (the 3 s look logged it, "not
  in this region", and said it might be the boat's). //stand didn't help (it's an animation, not a seat), and she logged
  in again. Now that look stops what's left again in the region she's in, and says so only if it's still there 3 s on,
  pointing at stopping all animations before logging in again (`docs/assistant.md`, A seat's leftovers).
- **Over three hours**: no other hold of the window over 1.3 s (two of 1.2-1.3 s: focus moving, and the screen reader
  taking a text change). The app grew to 3.1-3.4 GB on the drive, its managed heap to 1.3-1.8 GB with 0.7-1.5 GB of large
  objects left after full collections while 10-15 regions were around (the collision model's shapes 0-248 MB), and down
  to 0.7 GB of them at the end; her PC kept 0.6-0.8 GB free and nothing froze for it. What holds that much is the next
  question if it starts to hurt (no heap breakdown in a tester's log yet).
- Her sent logs turn every "Life" into a person's number ("Second person 29's viewers", "Relay For person 29"): a name
  in the log has Life in it, and the filter replaces names wherever they appear, our own lines included. Harmless
  for privacy (it leaves out more, not less), but it makes a log harder to read; a name that is also a common word could
  be left in our own lines. Not changed.

### Her teleporter's landmark, and deleting copies one by one (0.1.0.1132, the blind tester, 2026-11-15 morning)

Two logs of one session (from 08:56, still on 1132; she was getting 1139 installed), with her notes (our words):
`tester-20261115T141935-59acc8` (09:19: she could reach a landmark inside her teleport button by showing its contents,
but not delete it) and `tester-20261115T171145-b0bafe` (12:11: deleting many copies of one object, she's so quick that
her Enter sometimes comes before the confirmation will take it). On the builders' line that morning she also asked
whether objects can be edited in the inventory, as she'd heard the regular viewer allows.
- **The teleporter's old landmark**: she and her partner set out to replace "fuzzy" inside her TP Button. First she
  pressed Delete on the button itself in Objects (asked, cancelled: "I can't figure out how to delete the specific
  landmark, only the object"); she found Delete in its contents only by pressing Enter on the landmark ("Delete removes
  it"). Nothing in the log shows her own removal (no confirmation, no "Removed"), yet the button's script found it gone a
  minute later; her partner, there with her, may have done it from his viewer. She then got `add fuzzy` from the build
  editor's help (after `//help` there met "No build command"), and the button worked. Now what's inside a thing has its
  actions on the Applications key, as Objects and the inventory do, and a `//` command typed in the build or land box
  runs as from Say (`docs/access-app.md` > Building).
- **Deleting copies of Chat-Range** from outfit folders, some 25 between 09:54 and 10:31, by the item's menu and D, then
  Enter. Three times Enter came inside the confirmation's 0.8 s ("That came just as it appeared"); twice the next press
  took. The third time (10:27:50) she moved on with it unanswered, put the thing on and took it off, and pressed Delete on it
  three more times meanwhile; each queued another confirmation behind the first, so once she answered (10:29:35) she
  heard "Deleted Chat-Range" five times for two things. Now her own move to the Trash takes Enter from 0.3 s, and one
  asked while another waits takes her to the waiting one instead of queuing (`docs/access-app.md` > How it fits).
  Radegast moves to the Trash on Delete without asking (`docs/radegast.md`), which is likely where her speed comes from:
  a question for her whether she'd rather have that for her own Delete in the inventory, as the official viewer's
  "Confirm before deleting items" can be turned off.
- **Memory on 1132, riding the rails** from 09:58: the app at 3.0-4.0 GB with 0.3-0.8 GB of her 6.3 free, and seven holds
  of the window of 1.0-2.6 s with its own processor time near 0, by the look of it paging; 1139's memory work is for
  exactly this, so her next long ride's log is the check.
- Also: the assistant's job cleanup logged an error for SojournShow, which Access doesn't have; now quiet.
- **A script she couldn't read** (that evening, on 1139, `tester-20261115T233231-6464ed`, 18:32: she opened a script
  from her inventory but couldn't review the text or tell what she'd be changing, and closed it unsaved). She was looking
  for "a simple one to play with" with a friend, who gave her a chat-range script; she opened Peep!, Test Script and that
  one. Each opened with its text (no "Couldn't open"), and the editor says nothing on opening, so the log can't say what
  she met: NVDA not speaking a script's braces and semicolons at its usual symbol level, moving through the text, or
  the script's sense itself. Asked her which with 1142 (#292); her answer (#294): NVDA read nothing at all, "like I was
  in a blank document". Notecards read in the same window (she edited them on 929), so the scripts' text most likely
  came empty or unreadable; how isn't known. Both go by the same transfer from the region as the official viewer's
  (LibreMetaverse's and the viewer's source read 2026-11-15), and only Second Life has shown it. One weak spot found on
  the way: where Second Life keeps an item's asset id from her (a zero id), LibreMetaverse answers from and stores into
  its cache under the zero id whoever's item it is; the viewer skips its cache then. Since 1145 that entry is dropped
  around each read, every read of a notecard or script logs what came ("inventory: script Peep! read: asset id known,
  412 bytes"), bytes with a NUL inside are refused as not text, and a script or card that opens with nothing says so.
  **That was it**: her log on 1145 that evening (`tester-20261116T013840-4c1463`, 20:00) has "inventory: script
  Chat-Range 2 Script read: asset id kept from us, 2521 bytes" and her "I can read scripts" a few seconds on; she then
  edited and saved a script inside her rezzed HUD and put a sound in it (with `add`, before 1147's paste). So on Second
  Life a script's asset id is kept from her (unlike the local grid, which always sends it), and before 1145 the read came
  from whatever LibreMetaverse held under the zero id. Reported upstream with the fix and a test, with the user's yes:
  cinderblocks/libremetaverse#204 (2026-11-15; `projects/radegast` keeps it). Ibasho isn't affected: its viewer is
  the official one's code, which skips its cache for a null id.
  At 19:59, a minute after login, the window held 4.4 s while it built its tabs (its own processor time 0.2 s): her slow
  start, as before.
- **An edited object gone** (on 1147, `tester-20261116T024557-63e9b9`, 21:45: she couldn't take it back, and then it was
  gone, not in the Trash). At a club she rezzed her chat-range HUD at 21:26, put a sound in it and saved its edited script
  (21:27:45); Take into your inventory at 21:28:50 said "Not taken: 1 of 1 still in place: the region didn't take them".
  The op waited only 4 s for it to leave the region, as for any edit, with nothing from the region saying no; most likely
  Second Life took it a little later, into her Objects folder, and she looked in the Trash. (Or the club's auto-return
  sent it to Lost and Found.) She then found an older copy in the Trash, wore that, and deleted another for good through
  its confirmation. Told her where to look (#305). Now a take, delete or return is watched for 15 s, a region's refusal
  said as such, and one only slow says so and is watched two minutes more, saying when it goes and where (`BuildOps.GoneTries`,
  `MainForm.WatchGoing`); sent in 0.1.0.1150 (#306).
- **A person's ID from their profile** (#303-304, that night): in a profile, as she's heard, a button gives the person's
  agent ID; Access had none. Radegast's profile shows it in a read-only Key box, the viewer has Copy Agent ID: built
  the next morning as Copy their key on a person's actions (wherever they are) and a Key line at the end of the
  profile's text, before her answer; sent in 0.1.0.1153 (#307).

### A script inside her train, a failed crossing, and editing what she wears (0.1.0.1153, the blind tester, 2026-11-16 evening)

Two logs of one session (17:29 to 21:54) with her notes (our words): `tester-20261117T015720-0412dc` (20:57: a script inside
her rezzed train opened but she couldn't read it) and `tester-20261117T025732-f61561` (21:57: on another train she crossed,
or tried to, into another region and "crashed", she thinks not by the viewer's fault). Earlier on the builders' line (#309):
her partner edits the contents of what he wears, on his body or HUD, as of something rezzed; she can see a worn thing, its
details and parts, but not edit it, so she rezzes it (which went wrong the night before); she asked that the user be told.
- **Hours on the rails going well**: Zephyr round four regions from 20:17, crossings held every few minutes ("Now in Unttan,
  still on Zephyr."), switches said and thrown, by `//switch` from Say too ("I did it without having to go into walk
  mode"). Once (20:41) "I can't stop the train" and "it is refusing to let me stop", though by the track line it had been
  at 0 m/s since 20:40:40, after a throw the train answered with an error; she stood, deleted it and rezzed it again.
  It was still: no track line after 20:40:40 (one comes every 15 s while moving), no Space (that says "Stopped."), and
  after standing she walked from beside it into Saminekim within 5 s, so it hadn't gone on west. Between 20:39:42 and
  20:40:40 the seat went 9 m sideways onto the parallel branch at about the same x, as if the train put itself on the
  other line when the switch was thrown under it. What she heard as moving (its sounds, its text) is unknown. Not asked.
- **The script she couldn't read** (20:51-20:56): she found the speed notifier inside one of Zephyr's parts and opened it
  from the build editor's Content: "I can get in, but I can't read it". Nothing was logged of the read: a thing's contents
  were read by ScriptEditor's own transfer, with the asset id as the region listed it and without 1145's guard for a zero
  id, so most likely the same cause as her inventory scripts (Her teleporter's landmark, above). Now every notecard,
  script and landmark read goes through `TextAssets` (the guard, one such read at a time, and the log line, "contents:
  script X in Y read: asset id kept from us, 2521 bytes"), and a script or card opened from a thing's contents with no text
  says so.
- **The failed crossing** (21:52): VRC Hobo Train crossed from Dubia into Webworm at 21:52:11; she braked 12 s on; Webworm
  never answered, and at 21:52:41 the guard's 30 s ran out. LibreMetaverse went back to Dubia, where she was at -4, -1
  under the water with the train at 213, 3 ("Dubia hasn't sent my avatar"); going home had no answer after 15 s; she closed
  the app at 21:54:42. Second Life's failure, as she thought. Two parts were ours:
  - The guard's warning and its notice were left out of her sent log as someone's words: `LineSources.OfNotice` didn't
    list our own notices (CrossingFailed, Disconnected, RegionLost, the restart warnings, RLV's), so they counted as a
    person's, and every log line repeating them went too; nor the region's named alerts (AutopilotCanceled eleven times in
    her logs since 11-15, CantSitNoRoom, CantSitNoSuitableSurface, TeleportedByAttachment). Now listed; a region alert with
    no name stays a person's, since an operator can type one.
  - Closing held the hidden window 10 s and more while WinForms disconnected each control's UI Automation provider with
    NVDA listening (the cost 1132 took off clearing lists, here at the end). Once logged out the app now just exits.
  - The two minutes between: Dubia held her circuit but never sent her avatar, so the watchdog (wanting only a
    handshaken region) never logged in again, though the crossing's notice said it would if the region stopped
    answering. Since 2026-11-18 (Claude Opus 5.5) a region that hasn't sent her avatar 20 s after a failed crossing
    counts as lost: "You're lost between regions since the crossing failed: Dubia hasn't taken you back. Logging in
    again by itself" (`docs/client.md` > Region crossings; staged on the local grid with every reply held 40 s: the
    crossing failed, Atelier never sent the rider's avatar either, and 21 s on it logged in again in Atelier). Built ahead of her answer to #311's question (logging in by
    itself, or telling her and letting her choose), as the notice had already promised it; her answer may change it.
- **Editing what she wears** (#309): Objects' menu hid Contents and Edit (build) on her worn things and the editor skipped
  them. Built the next morning: the editor on her worn things, HUDs and body attachments, where they're worn, Content
  whole, position and rotation from where it's worn (`docs/access-app.md` > Building, Something she wears).

### Walks that found no way, to where her partner stood (0.1.0.1162, the blind tester, 2026-11-18 evening)

One log, `tester-20261118T224129-f36ae8` (17:31 to 17:41), with her note (our words): she tried to record a route there and
the app got confused, and `//go xyz` to the point he gave didn't work either. Read 2026-11-19 (Claude Opus 5.5).
- **Where**: a place she landed at by a landmark (the parcel there sends teleports to its landing point), her ear at 55.3 m
  over the water, so a floor some 34 m up; her partner about 13 m off and about 1.5 m higher, later at a point he gave her
  in words. She started a route three times, each time walking to him by `//go`, and threw two away.
- **The walks**: every walk to him or to that point but one went the same way: planned round something and his floor taken as
  out of reach ("as near as the way goes, 6 to 10 metres short"), a stretch that stopped getting anywhere after 2 to 4 s,
  planned again ("Something's in the way; going round: 8 stretches"), stopped again, and then "No way to walk to him
  found, and a teleport wouldn't land there". Once (17:37:04) the straight line to him was clear and she got there. Why
  the planner took his floor for out of reach, and what stopped her, isn't in her log: the planner's reasons went only
  to the walk log, a test switch. Unknown so far: something the collision model lacks (a physics shape not yet in, a
  parcel's ban line, which the model knows nothing of), or the planner misjudging a floor 1.5 m up.
  Her 17:36 walk stuck once and then had no way with nothing between: a stuck spot was marked over where she stood, so
  the plan from there shut her in (found by the scripted test the next day and fixed, 2e430ee); that may have been it,
  and may have ended the others' second tries too. Since 2026-11-19 the session log carries the planner's verdicts and
  reasons, and at a stretch that sticks what the model has ahead of her, shapes it lacks, the parcels and any parcel the
  region says she may not enter (`docs/access-app.md` > Walk mode > Why a walk went as it did, in her log), so the next one will
  say. She walked on the ground there (the land is high, about 54 m) and on wood; what leads up the 1.5 m to his floor
  is unknown.
- **`//go X,Y,Z`** (17:38:59), coordinates without the word xyz, was taken for an object's name ("No object called
  X,Y,Z nearby"). Two or three numbers alone now are coordinates.
- **"Autopilot canceled"** was read to her after nearly every walk (eleven times in her logs since 11-15, six here):
  Second Life's answer to our own walk letting go of the region's autopilot as it arrives or goes round. OpenSim's form
  was already kept quiet; Second Life's, a named alert in her language, wasn't. Now it isn't read, and the log says
  "the region's autopilot stopped".
- **Memory**: two minutes in, while the view out of sight first drew what's around (its textures 0.4 GB, on the
  processor so in the app's own memory), the app was at 2.1 GB private and her PC had 0.4 GB of its 6.3 free; the window
  watch's stack snapshot failed with Windows' commit limit (Error 1455). Nothing went wrong after. A snapshot copies the
  whole process's commit, so it's now skipped unless there's a gigabyte to spare beyond the app's size. Bears on the
  open thread about starting the renderer out of sight only when needed (Open threads, below).

### A group notice that wouldn't open (0.1.0.1162, the blind tester, 2026-11-19 evening)

One log, `tester-20261119T223104-61551a` (17:02 to 17:31), with her note (our words): reading group notices from the
Groups tab didn't work. Read the same evening (Claude Opus 5.5).
- **The notice**: a notice came at 17:25 and was read as it arrived; later, from the Groups tab, the archive's list
  came but opening one twice said "Couldn't read that notice: the notice: no answer from the grid", 10 s each. Ours
  took only an answer carrying the notice's id as its session, as OpenSim sends it; Radegast and its Veles take the next
  answer of that kind by the group it names, so Second Life most likely sends something else there. Now matched as they
  do, one asked at a time, and an answer under another session logged with what it carried (1c2176a); a grid that never
  answers is said as "the grid didn't answer". Only Second Life shows whether that's all of it.
- **Held at login**: her PC had 0.4 GB of 6.3 free from the start, and an IM she sent a minute in held the window 16 s,
  10 of them writing it to the IM history on the window's thread (the stack read 12 s late). Now written off it.

### Hit by the boat she rode in (0.1.0.1173, the blind tester, 2026-11-19 night)

One log, `tester-20261120T023042-437b99` (20:08 to 21:30), with her note (our words): sailing with her partner, along
for the ride, she kept getting hit with scripted objects, she guessed at region crossings. Read the same night (Claude
Opus 5.5).
- **The hits**: "hit you with a scripted object", her partner named each time, nineteen times from 20:47 to 21:28 while
  she sat in his boat as a passenger (once a half minute at most, as the notices are held to); the first ones 2 to 3 s
  after a crossing. The region names a vehicle's owner whenever its physics meets a rider's, so these were the boat
  itself. Now a hit (not a bump or a push) from whoever owns her seat, or sits on the same thing, is kept in the bumps
  list and logged, not said (`Safety.OwnRide`). OpenSim sends none, so only Second Life shows it.
- **The rest**: a train ride over twenty crossings before it, held throughout.

### Script menus in the order they're seen (the blind tester's partner, through Tessel, 2026-11-19)

His notecard and a demo script (our words; not a showstopper, he said): llDialog lays its list out three to a row from
the bottom up, so a menu's Back, Main and More, usually listed first, are the bottom row a sighted person reads last,
and Access read the list as given, as Radegast does: her train's Start, listed tenth, was key 0 for her and the top-left
button for him. He asked for the order sighted people see, so they speak of the same menu the same way. Built
2026-11-22 (Claude Opus 5.5; `docs/access-app.md` > Keys): the top row first, left to right, numbers, Tab and the
Applications menu alike, so that Start is 1 now. The layout checked in the official viewer's source: a short row (four
or five buttons) is the top one, from the left. Her old order stays a setting (Alt+0, General: Script menu buttons'
order, as listed), as her hands have known it in Radegast for years, and with it a paged menu's Back and Next keep
their keys from page to page, which as seen they don't when a page is short.

### Following her partner up his steps, and walking to him (0.1.0.1182, the blind tester, 2026-11-20 evening)

Two logs of one session at his place, `tester-20261120T222432-1d22fc` (17:22 to 17:24, "tried to walk to" him) and
`tester-20261120T222717-79c6c4` (on to 17:27, following him "up to where we dance"). Read the same evening (Claude Opus
5.5).
- **Following worked**: he came down off his floor to her (2 m below her by then, "no way down from here"), then walked
  back up; the follow took his trail ("Going the way ... went"), twice, and she was with him on his floor, where he sat
  and she took a dance ball. The first log on Second Life of 99fadaa's trail doing what it was for.
- **Walking to him still fails while he stays put**: four //go tries from the deck, each "no trail of theirs to take"
  (none of his steps seen since she arrived; later, 30 since he left a seat, none on her level), then the old way:
  his floor out of reach, stretches stuck with nothing of the model's ahead, "No way to walk to him found". Once the
  region lifted her 1.4 m and the walk still didn't trust it, as the straight way back had "a drop the model has": the
  ground under the steps the model lacks. Left as it is until the steps' cause is known.
- **What's at her feet** at each stick listed about 100 parts and said nothing useful: the six told in full were the
  region's surround sculpts (1024 m boxes, physics none) and a sky sphere, each "the nearest NaN m". A degenerate
  triangle (a sculpt's poles) gave NaN, which sorted first; fixed for 1187 (53559b7), nearest first by triangles, so
  the next log there should name what she stands on on those steps and why the model doesn't count it.
- **The likeliest causes, fixed and held** (ad4d0e8, reviewed): a mesh's flat decomposition hulls were left out and
  physics models were met from the front only (Open threads has what's left). In no build yet: her 1187 log at
  the steps (asked in #329; she said she'd test both ways, #331) shows which, a part under her "by its physics model"
  or with flat hulls; then build it at once. If it shows something else, that's the lead instead.
- **The window** held 1.9 s at login building the session log's first line (.NET loading the calendar's data through
  ICU on the window's thread): once a session, left.

### Walks sent above her, a route to her partner, and following that worked (0.1.0.1187, the blind tester, 2026-11-21)

Four logs, snapshots of one session (12:52 to 14:38), with her notes (our words): `tester-20261121T192016-8cb13a` (a
route recorded while following him at a roller rink, played from a forced landing point to a skating area: it worked),
`-193350-4126e9` (a walk to him at his place didn't work), `-193521-719186` (following him there worked) and
`-193826-457ac2` (a route recorded and saved while following him there, played, stopped at step 4 of 6). Read
2026-11-22 (Claude Opus 5.5 with the lead session's first look).
- **The walk to him** (14:33:23): from the deck where she lands, his trail taken this time (27 samples, joined 0.7 m
  west of her), but in 2 s she went a metre east, away from it, and each stretch stuck with nothing in the model
  ahead at any height. Each ended level with its point north to south and 1.6 to 1.7 m east of it. The cause was ours:
  every stretch went to the region's autopilot at his height, 2 m over her middle, and Second Life's autopilot leaves
  her east of a point above her by about that height. All 13 stretches in her logs there since 2026-11-19 that stuck
  or ended short had it (the point 1 to 5 m over her, 1.0 to 3.3 m east of it, 0.7 to 1.2 times the height, within
  0.07 m north to south, whichever way they went), the "drifting east" of her log on 1173 among them; the eight walks
  of this session sent a point at her height all arrived. Sure of the pattern; why Second Life does it, and whether
  it's east everywhere, isn't known. Fixed: on Second Life a walk sends its autopilot her own height and sends it
  again as she climbs (`docs/access-app.md` > Walk mode > Paths). So "nothing in the model ahead" was right, and
  this log says nothing about the steps' shape: she never stood on them in a walk.
- **Following worked** (14:34:31 and 14:35:52): a follow steers by our own autopilot, by X and Y as the official
  viewer's does, so the height never reached the region: by his trail up the steps and on his floor in 2 to 4 s.
- **The route**, "Tessa", recorded while following him up (six steps), played from the landing: step 2 to the
  steps' foot arrived; step 3, a point at their top 1.7 m over her, out of reach in the model with its way ending
  where she stood, was sent at that height and she drifted 1.5 m east in 4 s ("as near as the way goes ... 5
  metres"). Step 4, written without a height (a route keeps her height where the level changed by under a metre since
  the step before, as Radegast's routes do), so at the deck's, put the point in mid-air between the ground and his
  floor, and the planner found no spot near enough (no way, three times resumed). It stopped at step 4 because step 3
  hadn't taken her up. Since 2026-11-22 a route's step to a point that the walk calls done 2.5 m or more across from
  it, or a floor off it, pauses the route there, said with how far short (`docs/access-app.md` > `//route`): step 3
  would have stopped it.
- **A walk to someone who stays put**: with the height fixed, the walk by his trail most likely goes up, as following
  did. Without a trail (he went up before she came, her four walks on 1182), the walk still ended at the deck's edge.
  Weighed against her case and decided (Claude Opus 5.5, 2026-11-22): a walk to a person or a point on a floor the
  model can't reach goes on from as near as the way goes straight to them by the region, trusted, and on their floor
  is planned again; not where the model has a drop of more than 4 m on the way (`docs/access-app.md` > When the
  region has what the model lacks > On from as near as the way goes). At his place the straight way from the deck's
  edge to him runs up the steps, which is why walks aimed at him worked before 2026-11-11. Her route's step 3 goes the
  same way.
- **ad4d0e8** (a mesh's flat decomposition hulls as plates, physics models from both sides) goes into the next build:
  this log neither shows nor rules out its causes, and the at-her-feet line still names what she stands on.

### Sound on the monitor, and a box's sounds copied out one by one (0.1.0.1187, the blind tester, 2026-11-21 evening and 11-22 morning)

Two logs, read 2026-11-22 (Claude Opus 5.5). `tester-20261122T001423-03efe0` (19:10 to 19:14), her note (our words): she
finally got sounds to play on her monitor with the screen reader in her ears, which she loves. `tester-20261122T150657-90d27d`
(09:55 to 10:06 the next morning): copying a box's sounds into her inventory, she had to do it one by one and wasn't sure
it worked; is there a way to copy all of it?
- **Sound on the monitor**: chosen at 19:13:45 ("Sound on computer monitor ..., 3 of 3"), and the next session opened on
  it, the screen reader staying on her headset. Nothing to change.
- **The thirteen sounds**: she got a free box at 09:58 (L$0), had it out at home by 10:03 (it said "Playing..."), opened
  its Contents and copied thirteen sounds by the item's menu, 10:03:46 to 10:04:58, each said "Copied NAME into your
  inventory.". All thirteen came: the inventory's own count went from 14759 to 14772 by her next search, and reading it
  again from the grid dropped nothing. They went into her Sounds folder (a single copy goes to its type's folder), which
  nothing said; she searched (58 found) and had the inventory read again, still unsure. Now one copied says its folder
  ("into your Sounds folder"), a no-copy one says it moves, and Copy all into your inventory on the same menu (or `copy
  all` in the build box) takes everything at once into a new folder named for the thing at the top of her inventory,
  saying how many came, the folder, and what stayed inside and why (`docs/access-app.md` > Taking what's inside into her
  inventory: the viewer's semantics, where ours depart, OpenSim's narrower rule). Checked on the local grid only;
  Second Life's own answer to a whole box at once is hers to show.
- **At login** (the first log): the window held 5.4 s at Log in (its own processor time 0.4 s, 1.9 GB of her 6.3 free),
  her slow start as before (4.4 s building the tabs on 11-16, 1.9 s on the session log's first line on 11-20). Its stack
  was read, but before the session's log began, so only the console had it; those lines now go into the log, so the
  next one says what it waits on. And 1.0-1.3 s at 19:10:20 in starting incognito's two threads (Thread.StartCore from
  `Triggers`, by the stack): now started from the pool.
- Also: a sound at her home the grid hasn't got is asked for again every two minutes, a line each ("not playable (not
  found)"): a deliberate retry, left.

### Pauses in a recorded route that didn't wait (0.1.0.1187, the blind tester, 2026-11-22)

One log, `tester-20261122T163005-089a5c` (09:54 to 11:28), with her note (our words): she recorded a route and walked
it, then recorded another trying to add pauses, and the pauses didn't seem to take. She's writing a blog post about a
forest region with a recorded route its readers can walk. Read the same day (Claude Opus 5.5).
- **The pauses**: following her partner, she recorded a second route (76 steps), and six times paused the recording,
  stood 13 to 22 s listening to the place, and went on from the same spot, expecting the played route to wait there.
  The recorder's pause kept the spot as a point and wrote no wait, and its help's "pause or go on" read as a pause in
  the route. A route of Tessel's for her, with `//pause` lines, waited as she wanted. Built the same day (Claude Opus
  5.5; `docs/access-app.md` > Recording a route, > Waits), as told her first (#337): going on within 2 m of where it
  paused writes a wait as long as the pause, said with how to take it back; Shift+P (`//record wait SECONDS`) writes one
  without pausing; the help and the pause's own words tell pausing the recording from a wait in the route. Played,
  a wait says so as its step starts ("Step 17 of 78: waiting 19 seconds.", a long wait's lines once as their total),
  where it said only its number and went quiet, which a reader of her post could take for a stall.
- **The first route** (71 steps, recorded the same way): played, it stopped once at step 39 on a forest road, stuck
  three times with nothing of the model's ahead and the region's autopilot stopping, and went through on `//route
  resume` from a metre on. The second walked short at steps 67 to 69, each point she had walked to "out of reach ... on
  another floor" among snowdrift meshes. Both the walk's, not the recording's. Read for the walk (Claude Opus 5.5,
  2026-11-22): neither is the height sent to Second Life's autopilot (Walks sent above her, above), as each point was
  at hers. At step 39 her feet were in the box of a snowdrift mesh the model counts by its physics model (its
  triangles 0.5 m off) and the region held her with nothing of the model's ahead: a physics model met from the front
  only, which ad4d0e8 ends, is a likely cause, not a sure one. At steps 67 to 69 the points lay on meshes of convex
  hull physics, two "GR 90 corner"s among them whose own hull is flat, which the model filled with the hull of its
  drawn shape, 0.5 m over the road, where she had walked recording (at a height the route doesn't keep, so on that
  hull or not: Open threads); since the same day it counts that flat hull as a plate. Since 2026-11-22 such a step
  also goes on from as near as the way goes by the region ("Trying straight on to ...").
- **Two `//record` words it didn't take** before she saved the first one; commands aren't logged, so which is unknown.
  The answer now names her word ("//record doesn't take ...") and the log keeps it; `end`, `finish` and `done` save,
  as `stop` did.
- **A walk recorded as a ride** (`//ride` on foot after her partner, ten minutes, then told): she wants that telling
  matched to the route for the blog, which nothing does yet.

### A teleport partway down a ski route (0.1.0.1187, the blind tester, 2026-11-22)

One log, `tester-20261122T173905-941fc3` (the same session on to 12:39), with her note (our words): she recorded a
route and tested it herself, and was teleported at one point without knowing why. Read the same day (Claude Opus 5.5).
- **Another forest route** (11:20 to 11:28, a card she renamed "Enchanted Forest Slow 2", 78 steps, most likely the one
  Tessel made her from her description) played through; a teleport home 18 s into its last step, before "Route
  finished": a line of that card or her own key, which the log doesn't say.
- **The teleport**: her route "ski" (27 steps, recorded following her partner down a slope at a ski resort, 12:31),
  played from the resort's landmark at 12:34. Step 4 went 11 m on and 5 m down in 12 stretches; she ended 1.6 m east
  past the first stretch's point and lower, stuck twice heading back to it with nothing of the model's ahead, and the
  third stick was the walk's no-way end: "No way to walk to 158.9, 48.5 found: teleporting there." On 1187 the region's
  autopilot was sent each point's own height, here under her on the slope, and she went east past the point as at her
  partner's steps, where the points were over her (Walks sent above her, above): most likely the same fault with the
  point below (one walk, so not sure), which 60478e0's
  own height ends. The rest of the route walked, at times stuck once and going round.
- **Two loose ends** after the teleport, looked into (Claude Opus 5.5, 2026-11-22): "No route to resume." a second
  later was `//route resume` (or "again") arriving while the route still played; nothing of ours sends those words, so
  most likely hers, typed or recalled (she had used it at 10:54), the walk's going round and no-way end having sounded
  like a stop. A resume while a route plays now says it's still playing, at which step, with skip and stop, and never
  restarts anything (with an older pause point kept, it would have cancelled the playing route and gone back there).
  The stuck line after step 5 began was only late: a stuck line is written once its names come, up to 2 s on, and
  says so now ("stuck N s before this line"). With it `//route skip` (bba9b8d), so one step that keeps failing no
  longer blocks a route a reader of her blog plays.

### His steps in the model but cut off, and walks to him that went off the deck's far edge (0.1.0.1206, the blind tester, 2026-11-22 afternoon)

Four logs, snapshots of one session at her partner's place (13:15 to 13:26), with her notes (our words):
`tester-20261122T181639-12bc12` (her route up his steps, "Tessa", didn't work), `-181758-8d100d` (a walk to him),
`-181854-a8c378` (the route again, which worked: "not sure why the difference"), `-182029-de2bcf` and `-182625-f64e1e`
(the route again, which worked, then walks to him and to a point by his coordinates, which didn't). Read the same day (Claude Opus 5.5 with the lead session's first look).
- **The model has the steps.** Mid-steps the at-her-feet line names a sculpt of his house's linkset (its root named
  for a palm, sculpts and prims of physics prim, his floor among them), 3.5 by 3.9 m tilted 30 degrees, its box 0.17 m
  from her feet, and from there (feet 53.66 to 53.94, the model's floor 53.57 to 53.61) the model has a way to his
  floor (54.39): planned three times (twice straight, from half a metre further up) and walked twice. The earlier reading
  that the model lacked them came from sticks that weren't on them: on 1173 and 1182 she was stuck 5 to 9 m east of
  the steps (the autopilot's height fault, Walks sent above her, above), so nothing there spoke for the steps. What the
  model gets wrong is their foot: from the deck (52.41) the planner has the nodes up the steps but "cut off", the deck
  at their foot and the first step "too tight", and the why lines put the steps at 0.39 to 0.41 m each (the first, at
  her route's step 2, 0.41 m over the deck, against the planner's 0.4), the floors meeting 2.3 to 2.5 m from the
  route's step 3 and 8.7 to 9.5 m from him. Which cells close it and why (a step just over 0.4 m beside the way,
  counted as a wall at 0.3 m, or a face of a coarse sculpt over the steps) the log can't tell: the why line now says
  (below). The region walks her up them every time: following, and straight on by its autopilot from the deck's edge
  (all three plays of her route got her up to mid-steps in 1.2 to 1.4 s).
- **The route's first play** (13:15:48): step 3's straight on left her at 30.44,21.31 (feet 53.66), "there" a metre
  off with her middle half a metre under the point; the region then held her for 2 s on step 4's first stretch, 0.8 m
  up the model's way, without her moving at all (the model had a step's riser at her shin 0.85 m on and nothing
  higher; under her middle its surface was 0.43 m below her feet and 0.6 m on level with them, a riser's edge, where
  the region had her standing). Why the region held her isn't in the log. Ours was what came next: the stuck spot marked 0.7 m ahead, on the only way up the model
  has, shut her into 12 floor nodes, so the plan again was "no way", and so was every walk from there for the spot's
  two minutes (step 4 twice, step 5 twice after `//route skip`, a //go to him). The two later plays, from half a metre further up
  and 0.2 to 0.3 m higher, went straight up and on to the route's end. Now a stuck spot that leaves her no
  way, where the model had one through it, is taken back and the walk goes on straight to its end by the region,
  once a walk, with the guards of straight on (`docs/access-app.md` > When the region has what the model lacks).
- **The walks to him** (13:17:09 and 13:21:49) and to his coordinates (13:25:19), from the deck where she lands, no
  trail of his to take: the plan went north along the deck to the spot nearest him across, its far edge 5.5 to 7.8 m
  from him, rather than west to the foot of the steps, where the floors meet 8.7 to 9.5 m from him; then "Trying
  straight on", "On ground." 3 s later, and "Finished walking, as near to him as the way goes" a metre across from him
  and 2.6 to 4.4 m under him. The drop guard (over 4 m) let a fall off a deck through, as decided; calling where she
  landed "as near as the way goes" was wrong. Now (Claude Opus 5.5, 2026-11-22): a `//go` or walk to a person or a
  point out of reach, whose floor meets hers by steps of at most 0.6 m and a metre or more over or under hers, is
  planned to the nearest place they meet so (here the foot of the steps), not the spot nearest across, and goes
  straight on from there; a straight on toward someone above
  her that takes her down more than a step stops at once and says so ("Finished walking: you came down 2 metres off an
  edge on the way; ..."); and the why line says where her walk stops short of the goal's floor and why ("her walk
  stops there: beside it the goal's floor at ... too tight, beside a step up of ...", or what's in the way),
  so her next log there names the cause. The scripted test stages it with rails over the steps that only the model has
  (`docs/access-testing.md`, the steps the model hasn't got).
- **A //go from the route's end** to him (13:19:41), both on his floor, went straight and arrived.
- **Her partner's own reading** (a notecard and a snapshot, given through Tessel that evening; both kept on Lantern in
  `projects/second-life/local/for-helpers/`, dated 2026-11-22): the walk
  chooses the wrong path, the right-hand way down to the sand and round under his platform, where it used to come up
  the stairs. His snapshot from the platform shows the stairs splitting at the landing deck, the left flight up to
  him and the right a boardwalk down to the sand, with palms overhanging the stairs' foot: their fronds, if the
  model counts them solid where the region doesn't, are one candidate for what it calls too tight there (Claude Opus
  5.5's guess from the picture; the why line on 1209 and after names the part).

### Straight on from the corner of his steps' foot: off their side once, up them once (0.1.0.1212, the blind tester, 2026-11-22 evening)

Her log `tester-20261122T233010-f087b7` (a `//go` to her partner from the landing point at 18:29; her note, our words:
it failed badly) and, for its walks, `tester-20261122T234744-5e3247` (the same session on: another `//go` to him at
18:31, her route "Tessa" at 18:33; the 18:31 walk also on its own, `tester-20261122T233150-b21202`, her note: it
worked, he had moved a little). Read the same day (Claude Opus 5.5, with the lead session's comparison of the two).
- **Both walks went as 1209 meant**: planned to where the floors meet, 31.35,19.96 to 19.97, 6.2 and 7.1 m from him,
  then "Trying straight on" at him. At 18:29 she came down 2.3 m off an edge onto the sand within 0.5 s, half a metre
  south-west of there, and the new guard ended the walk and said so; at 18:31 she was up the steps on that line and on
  his floor 1.2 s on, planned on from there and arrived. The two lines were 1.5 degrees apart: where the region had
  her as she set out, or which way she slid off something at the corner, made the difference, which the log doesn't
  show.
- **Where the floors met was the corner of the steps' foot**, not its middle: her route, which worked again (its fourth
  good play), goes up from 32.35,20.46 west-north-west through the foot's middle, and the line from the meeting point
  runs about 0.75 m south-west of it, along the steps' south-west side by the deck's edge (she landed at 30.82,19.50).
- **What closed the foot**, from the why lines: the same "upright 52.12-54.32" of the steps' sculpt beside the foot in
  both walks and the route's step 3, as on 1206 over a cell 1.4 m up the steps. One part's upright faces have the same
  heights over cells 1.4 m apart along a slope only if the planner stood a face at its whole height over every cell it
  crossed: the steps' side, from under the deck to over his floor, a wall 2.2 m high at their foot. So most likely
  their side, not a palm or a rope rail (from the numbers alone; the sculpt isn't in our cache). On the foot's other
  side the route's step 2 names a post of the deck's, 1.3 m high.
- **Now** (Claude Opus 5.5, 2026-11-22; `docs/access-app.md` > Paths, A narrower body): a face too steep to stand on
  counts over a cell only as high as it is there; where the planner still finds no way onto the goal's floor, a `//go`
  or Walk to plans again for a narrower body and walks that way, up the steps' middle, a stretch of it that takes her
  more than a metre under the way ending the walk, said; and the search for where the floors meet follows each floor
  it comes to over a cell, where the ground under a deck could hide the deck ("no way: the goal is in a space of its
  own", the teleport). On a synthetic copy of the foot (steps 0.8 to 0.9 m wide between sides sloping with them),
  1212's planner had no way up and this one goes straight up; with rails a metre over the treads 0.64 m apart, its own
  way ends at the foot and the narrower one goes up their middle. Straight on from where the floors meet is left for
  where even that has no way. The scripted test stages it with the steps' east side open above the rails and the
  visitor east of their line (`docs/access-testing.md`).
- Also there: the route's step 3, from where step 2 had stopped her, a stretch of 0.9 m, held her 2 s without moving,
  was marked stuck and went on straight by the region, which worked (Open threads).

### A sound into her worn HUD, and a Delete after Take off (0.1.0.1212, the blind tester, 2026-11-22 evening)

`tester-20261122T234744-5e3247` (18:28 to 18:47), her note (our words): she tried to add a sound to a HUD she was
wearing, and it failed several times. Read the same evening (Claude Opus 5.5).
- **The sound into her Watch** (18:45 to 18:47): she copied munchkin_hee_hee (one of the free box's sounds she copied
  out that morning) and pasted it into the Contents of her Watch, worn on HUD Bottom Right. Second Life answered the
  first drop at once with its notice "Inventory creation on in-world object failed." (18:45:57), and 15 s later she
  heard "Not put in: ... it may still have gone in". The second paste, by the timings pressed while the first still
  waited, drew no notice and the same words 7 s after. The third met the op's guard against a second copy, whose words
  for the assistant came to her as "something went wrong inside Sojourn Access": they began with the sound's name, whose
  underscores `PersonOps.Words` took for an argument's. She looked afterwards: it hadn't gone in. Not a regression of
  ours: the drop, the wait and the listing are as on 1162 (`ContentsOps.AddAsync` unchanged since 10-26; that day's
  commits only took things out), when she pasted another sound, Ocean1, into her worn chat-range HUD. Most likely this
  sound isn't copy, modify and transfer as made: the official viewer drops nothing else into an attachment (the server
  doesn't support it, its source says), and the region's notice says it refused. Its rights aren't in the log (its
  Details say them). Now such a thing is refused before it's sent, saying why and the way round (rez it, put it in,
  take it back, wear it); the region's notice ends the wait as a refusal; a drop the serial shows never landed is said
  as not gone in; only one neither seen nor refused is said as not known, a second press while one goes in is said so,
  and the guard says it may already be inside with Control+V again for another copy; every op error starting with a
  name with an underscore in it now reaches her in words (`docs/access-app.md` > Putting things inside, An op's error
  in her words).
- **Delete after Take off** (18:37): on a link's row (by the log in Current Outfit: Take off removes only that link) she
  took Watch off; the row stayed, as only the Objects folder, Watch's own, was shown again, and Delete on it ten seconds
  later found no such item: the id was the link's, gone with the Take off. She then found Watch in Objects and wore it.
  Now Wear and Take off show again Current Outfit and the chosen row's folder, and Delete on a row whose thing has gone
  says so and reads its folder again.
- The region's notice went out of her sent log as someone's words (a named alert not in the app's list): kept now.

### "Standing still" on her partner's train, mid-crossing (0.1.0.1232, the blind tester, 2026-11-25 evening)

Her log `tester-20261125T234709-b62a3f` (18:16 to 18:47; her note, our words: driving her partner's train, at one point
it seemed she was standing and the train 5 m away while she sat; lag, she guessed, but she wanted us to see). Read the
same evening (Claude Opus 5.5).
- **What she heard** (18:44:48 to :53): walk mode on, then V: "Standing still."; V again 2 s on: "VRC Hobo 3.0
  Prototype C: 5 metres a second, ... It shows: Speed: 15 m/s"; 3 s on, 15 metres a second. She was crossing from
  Deltote into Athetis at 15 m/s (the app in Athetis from 18:44:39, the train, her seat, sent there only at 18:44:49):
  with no seat in the new region, walk mode's `Me()` has her on foot, and V said so. The 5 m/s, a second after the seat
  came, is the seat's first velocity there (the region's, or reckoned from its moves when it has none), most likely
  the crossing; not looked into further. Now V and Shift+V in a seated crossing's first seconds say "Crossing into
  Athetis: it hasn't said yet whether you're still on VRC Hobo 3.0 Prototype C." (`WalkMode.SpeedLine`, `SeatInDoubt`,
  as the crossing's own line already did). Her next log, two minutes on (`tester-20261125T234943-3a1117`, "the same
  thing, caught quicker"), has it longer: crossing into Obscure at 18:48:53, the region sent her avatar only 25 s on,
  and V said "Standing still." four times meanwhile (at 15 s the crossing's line said it hadn't said yet whether she
  was still seated; "Obscure has you on VRC Hobo 3.0 Prototype C now." at 25 s). The fix covers that too: after the
  crossing's line, while the region still hasn't sent her, the seat stays in doubt.
- She looked for it afterwards in the chat history and couldn't find it: walk mode's answers weren't kept anywhere to
  read back (only seated crossings and rail switches went in the history). Asked (#376), she said the chat history is
  fine, as something with a switch (#377): now what walk mode says goes there too, but for its warnings while walking,
  with Alt+0's "Walk mode's answers in the chat history" (on) (`docs/access-app.md` > Read-out). 1237 kept only bumps
  and turns out, though she was told its warnings would stay out: drops, water, openings and doors passed, and what a
  vehicle nears went in; the build after keeps them all out (checked the same evening, Claude Opus 5.5).
- The rest of the drive (26 minutes of crossings, switches said, End of track, Flip and Start) shows nothing else
  amiss in the log.

### A welcome cut off, and a train letting go of her keys (0.1.0.1240, the blind tester, 2026-11-26)

Three logs, read 2026-11-27 (Claude Opus 5.5). Her message that morning (#381): the build seems to be working well so far.
- **"Welcome aboard" skipped** (`tester-20261126T170126-a97cb9`, 09:00 to 12:01; her note, our words: sitting on the
  vehicle should say Welcome aboard with her name, and it's skipped; her settings?). Not her settings: each of her five
  sits on VRC Hobo 3.0 Prototype D handed "Welcome aboard, (her name)!" to NVDA, and in the same second "Sitting on VRC
  Hobo 3.0 Prototype D.", an answer, which cuts in and so cut the welcome off (she found it once by reading back, 12:00:37).
  Now an answer that comes with no key of hers since a line arrived says that line again after it
  (`MainForm.CutLines.cs`, `Announcer.CutIn`; docs/access-app.md > Read-out).
- **Arrows stopped working on her partner's train** (`tester-20261126T203934-dd267b`, 15:10 to 15:39, Gryphon Express
  from Fishii to Tuliptree and back; her note: lots of errors driving, the arrows stopped, only a relog fixed it, the
  other errors probably Second Life). At 15:37:50 the region said the train's script let go of her keys, in the same
  second as the train's "(her partner) has boarded!": a script holds one avatar's permissions at a time, so asking his
  let hers go, on any viewer. Her arrows then reached nothing ("Arrows broke again", 15:38:34) and nothing said why.
  After the relog (`tester-20261126T204055-849e2b`, "after a relog the arrows work again") sitting again had the train
  take them, and she drove it to 10 m/s; standing and sitting again would have done the same. Now the seat letting go
  of her keys is said, and an arrow pressed with nothing holding them says so (`WalkMode.SeatKeys.cs`). Twice the train
  stopped by itself as it crossed (15:15:26 into Crumbi, 15:29:01 into Lota), its script taking her keys afresh with its
  sit message, as if just sat on: most likely her "errors in different places", the train's own. Her earlier "Not
  working" (15:36:22) came with walk mode off since 15:32:19; what she tried then the log can't say.
- Both in 0.1.0.1244, sent with what her logs showed (#382, 2026-11-27 morning), asking what she heard as errors if it
  wasn't the train stopping at those crossings. Not raised by her: at 15:37:28 the train stood her up at the end of its
  track and nothing was said; now "You're off Gryphon Express." (0d47e1d, in 1248).
- **A switch she "failed to flip"** (her IM at 10:49 in the morning log, not raised with us): at Clearwing on Zephyr,
  Shift+W 45 m before a switch set to the passing track touched its stand at 10:48:01, the stand said Main at :03,
  and the train took the main line, as she wanted; but "Switch set to the main line, Clearwing Station." came only at
  :08 (the throw's reads in turn, each waiting out its ask on a slow region), and a second Shift+W, pressed while it
  waited, then threw the same switch, by then 53 m behind her, back to the branch: "Switch set to the branch,
  Clearwing passing track.", which read as her throw having failed. Now the answer comes as the stand's word does, a
  press during a throw says it's under way, and a switch behind a moving train isn't thrown (docs/access-app.md > Rail
  switches). Both in 0.1.0.1248, sent in 1244's place with what the log showed of her throw (#383, 2026-11-27); then
  0.1.0.1251 in 1248's place (#384), with a fresh agent's review's fixes: 1248 could say "You're off" on a teleport from
  a seat or a sit onto another, and refuse a throw of a switch ahead not placed on the track.

### A grid drive's tour, golf, and a link from a group's chat (0.1.0.1251, the blind tester, 2026-11-27 evening)

Her log `tester-20261128T020245-321dfc` (19:09 to 21:02; her note: she got an item she couldn't open in this viewer).
Read 2026-11-28 (Claude Opus 5.5).
- **What wouldn't open**: at 21:02:11 and :23 Enter on a line said "That link is for something Sojourn Access can't
  open.", a second after a line in her group's chat. The log's own line for it (`link not opened:` and the link's
  shape) was left out of what she sent as repeating someone's words: the shape kept the map's host and path whole,
  "https maps secondlife com secondlife", five words of anyone's map link. That leaves only one kind of link it can have
  been: a maps.secondlife.com (or slurl.com) link with more on its end than the place ("/22♥", "/22&...", a fifth
  part), which `Places.Parse` refused as not a place, while the viewer reads the number starting each part and goes.
  Now such a link is read as the viewer reads it (`LooseLink`), and the shape sets the host apart ("https://maps.secondlife.com,
  path /secondlife/[word]/[n]/[n]/[n+]", "[n+]" a number with more after it), which logcheck's filter keeps. Whether the
  link was what she meant by an item isn't known: asked.
- The rest went well, by the log and her words in it ("Not getting errors from Sojourn"): a grid drive's tour by its
  Nav HUD through some forty regions in a tour car and a boat, each crossing said and none lost; group notices'
  notecards and landmarks taken; a golf course's club, HUD and scorecard rented and worn. She told the group chat
  that in Radegast no one could take part in grid drives without being teleported all the time, there being no way to
  press Page Down; here she did, by F3 and Page Down, after the Say box's hint (19:33:28, 20:03:33). Following a
  person off the boat found their floor out of reach ("no way up from here", a climb steeper than stairs in their
  trail), and she teleported to them.
- The golf HUD's own instructions (20:58:25): its Putter button, the arrows to aim, then click the ground and hold for
  power. Walk mode's Enter held is the mouse button held, for a script that took it (told her, #385).
- **Takes forgotten at a teleport**: her tour's Nav HUD took the arrows, Page Up and Down (passed on) at 19:12, a
  teleport by it followed at 19:16, and at 19:33 the Say box's Page Down hint said it moves her down, not "A script has
  taken Page Down": Access cleared every take at a teleport's end (`AgentSession.ResetControlsTaken`, a precaution from
  2026-10-25 against an object left behind), while an attachment's take travels with her and no region sends it again
  (none after the teleport here, nor after her 2026-11-15 teleport with the same HUD), and the viewer zeroes its counts
  only in `LLAgent`'s constructor (read in secondlife/viewer's llagent.cpp, 2026-11-28). So after any teleport walk
  mode's Enter refused a worn weapon's or golf club's click ("Nothing here takes clicks."). Now a teleport keeps the
  counts and logs what's still held (`controls: still held after the teleport`); the step "a worn script's keys over
  a teleport" checks the click.

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

- Her partner's steps (her logs of 2026-11-22, His steps in the model but cut off, and Straight on from the corner of
  his steps' foot, above): their foot most likely closed by the steps' side stood at its whole height, now counted only
  as high as it is over each cell, and where the planner still finds no way a narrower body's way is walked. Neither is
  seen on Second Life yet: her next `//go` to him from the landing point says which (its plan line says "planned for
  a narrower body" where the first wasn't enough; its why line, where the planner's own way stops). If it's the step
  height (steps 0.39 to 0.41 m in the model against the planner's 0.4): Second Life's avatar climbs more than 0.4 m
  (how much isn't known here; the VR client learns it from the region, `docs/vr.md` > Walls, steps), and the
  planner's Step could be raised on Second Life, with Knee, the lowest of what's in the way, above it (a surface
  between 0.4 and 0.45 m is neither a step nor in the way today); a narrower body doesn't help there.
- Stretches under a metre from a standstill: the region held her 2 s without moving on two (1206's first play of her
  route, 0.8 m up the steps; 1212's route step 3, 0.9 m), each then marked stuck. Perhaps the region's autopilot
  doesn't start for a point that near. Her logs through 1212, read for it (Claude Opus 5.5, 2026-11-24; first stretches
  only, as later ones are walked on without stopping): a third that didn't move her at all, 1.05 m on 11-22 at 11:15:58
  (beside the "GR 90 corner"s); two that did, 1.14 m (10:54:15, after a stick) and 1.22 m (11:13:02, just after an
  arrival); the rest under 1.6 m were either already within reach or at her partner's steps, where the model's missing
  steps confound them. No region notice came for any that didn't move, where its own arrivals do send one
  (AutopilotCanceled with no cancel of ours, as before step 3 at 18:33:40). So a floor somewhere about 1.1 m fits, from
  three cases. Now (after 1226) a corner under 1.2 m from her is sent 1.4 m out along its stretch, the next stretch
  taking over once she's within reach of the corner, and a walk's last stretch to a stand-in short of a goal on another
  floor that starts under 1.2 m from her counts as reached, the walk going straight on from there (1212's step 3 did,
  after its stick); a last stretch otherwise goes to its own point, as before (`Commands.AimFor`; docs/access-app.md >
  `//` commands, after the table). Her next route with a short step, or a short corner from a standstill, says whether
  that was it (`go: a corner 0.88 m from her, ...: the region's autopilot sent 1.4 m out along the stretch`). A
  narrower body's way has short stretches through its squeezes, now sent the same way; a trusted walk on by the region
  still sends its point as it is.
- A narrower body's way passes gaps of 0.25 to 0.75 m where the planner's own needs 0.75 to 1.25 (Second Life's avatar
  is 0.6 m wide, 0.45 deep), so some of it she won't fit; how often on Second Life isn't known (stuck, the spot is
  marked and gone round). Following and the assistant's walks keep the planner's body: following has their trail, and
  the assistant's walk has no guard for coming off a narrow way.
- From where the floors meet (`Plan.AtMeeting`), straight on still aims at the person, which from a corner of steps'
  foot runs along their side (1212). Now only where a narrower body has no way either; if a log shows it again, aiming
  along the goal's floor's own way up from the meeting (the way it was followed out) is the next step, or picking the
  meeting with the most room rather than the first found.
- A person or a point whose nearest spot she can get to is 6 to 12 m off (a height between floors counting as
  distance) is no way unless someone stands at a spot (`PathPlanner.Solve`: the rings go to twice `GoalReach` since
  837b9ef, but `within` stayed capped at once): her route's step 4, where no way was right, as she wasn't up. Most
  likely an oversight; left, as a teleport beside a person can serve better than a walk that far short.
- The collision model's mesh physics (ad4d0e8, 2026-11-20: flat decomposition hulls as plates, physics models from
  both sides; 2026-11-22: a flat base hull as a plate too, and a mesh under 0.2 m in two sizes as its box, as the
  viewer's `determinePhysicsShape` has it; both in the build after 1187). Whether the region meets a flat hull as a
  thin plate or not at all hasn't been seen: the plate is the likelier (its shapes are convex sets with a small
  radius, which a flat set makes a thin plate, and the drawn shape's hull, which stood in before, comes from nothing
  the region reads). Either way the model has far less there than before: in a Second Life cache 1322 meshes have a
  flat base hull, 948 of them uploaded as convex hulls; as a convex hull shape 1236 had been filled whole by the drawn
  shape's hull (86 more, flat themselves, by their drawn triangles), and the 948's plates are mostly a fraction of the
  box's face (580 under a twentieth of it); the survey and the plate and box checks are kept in
  `local/collision-survey` (not in the repo). Her log of 2026-11-22 (Pauses in a recorded route,
  above) can't tell which, read again (Claude Opus 5.5, 2026-11-22): her route's points at the two "GR 90 corner"s
  carry no height of their own (a route writes one only for a change of over a metre, and step 64's, a metre up, was
  one), so she may have walked on the drawn hull's 0.5 m there; and her stick between them, with nothing of the
  model's ahead, was 0.8 m outside both their boxes (her body's axis 0.77 m from the nearer), so nothing of theirs,
  plate or hull, held her there. Their assets aren't in our cache, so the plates' places there
  aren't known. A log of hers with a stick beside such a plate (the at-her-feet line now says "its own hull flat, a
  plate") would be the first sight of it. Left of the viewer's rules, under 0.2 m either way: a big thin shape's thin
  side raised to 0.1 m, and a prim or sculpt under 0.2 m boxed. The VR client stops counting a floor the region lets
  the avatar through; Access has nothing like it, so a wrong floor there stays.

- A follow with neither a planned way nor a trail still steers straight at the one followed, as it always has: in a
  staged run with the visitor flying down, that walked her off a 2 m deck after it, and from a high floor it would be a
  high fall (seen by the agent fixing 66da1c0, 2026-11-19). Not changed: before the floors work it was the only way a
  follow went, and she hasn't met it; worth a guard on the drop if a log ever shows one. A guard like `//go`'s
  (OnFromDrop's 4 m) was built and taken back (Claude Opus 5.5, 2026-11-25): a follow has no way mostly where the model
  lacks what the region has, and a bridge it lacked would read as a drop and hold her at a crossing straight on walks
  today; with them below, the fall takes her to them, which Second Life doesn't hurt. Only a drop with them not below
  is plainly wrong, and the model can't tell that from a bridge it hasn't got.
- Cancel during Send a log's sending stops it since 2026-11-13 (before it's on its way; once it is, it says it can't be
  stopped), not run against a slow send.
- From the reviews of 2026-11-13: positions from parcel, event and classified links are taken modulo 256, wrong on
  OpenSim's larger regions (Second Life's are all 256 m). (A tab's earlier lines read on the window's thread and link
  words cleared at 1000 were closed the same day.)

- The renderer out of sight could start only when needed: on the processor, unseen, it's ~700 MB of committed memory
  (~0.25-0.3 GB of working set), against the first picture, face list or touch waiting for it to start and, on her
  2400G, for its shaders (minutes, by her log); `docs/performance.md` > Sojourn Access on a small PC. Kept starting
  at login (Claude Opus 5.5, 2026-11-18): her log of 2026-11-16 had 1.0 GB of her 6.3 GB free with the app at 1.4 GB,
  so 0.3 GB isn't what stands between her and trouble, and minutes before her first description would be. Her log of
  2026-11-18 had 0.4 GB free with the app at 2.1 GB two minutes in, as the view first drew what's around (Walks that
  found no way, above): weigh it again if a log shows trouble from memory.
- Branch `fix/own-voice` (arrivals in Access's own voice) is WIP, not wired in.
- Her PC's log (2026-10-31) named both chips at Vulkan 1.1.125 (driver 2.0.116, by its look a 2019 OEM driver): the view
  now draws on the processor there. Her logs of 2026-11-01 showed a session's first frame taking 1-3 minutes of
  compiling, then ~1 s a picture; the compiling was cut that day (`docs/performance.md` > The first picture after
  login: about 2.5 times less, a first frame at once, no shadows, and descriptions waiting for their pictures and
  saying so). Her next log's picture lines say how busy her processor was meanwhile. The lever left is a cache that
  lasts: Mesa's disk cache, which llvmpipe would use for exactly this code, isn't built on Windows (upstream's
  meson refuses it), so it would mean building lavapipe ourselves with it made to work there. AMD's legacy
  driver for Polaris and Vega (Adrenalin 26.5.2, Ryzen 2000G APUs included) would probably give her GPU Vulkan 1.3
  and faster pictures (reports mixed for Vega), an option to mention, never asked of her. Running the renderer itself
  on her driver isn't a 1.2 path: 1.1.125 predates dynamic rendering, synchronization2 and timeline semaphores, so
  every pass's begin-rendering (10 places), every barrier (14) and the SPIR-V target would need a second form: large,
  and testable only on her PC.
- Audio sides (her partner, through Tessel, 2026-10-30): Radegast had left and right reversed for over ten years; in
  hers that night a stream to her west sounded on her right while facing south, which is right. Ours, read through
  2026-11-09: world sounds are checked by `tools/audio.py`'s balance step (facing north, a sound to the east is louder in
  the right channel; facing south, the left), and Access's listener goes the same way (`WalkMode.Listen`, her facing).
  Voice is panned by the server from the heading we send (`VoiceClient.SetListener`: ahead, left and up in Second Life's
  frame, as the viewer sends the camera's), not heard on Second Life by us. Her ears with walk mode's sonar and in voice
  are the real check.

### Questions for a blind tester

- Sent with 0.1.0.1217 (#367, 2026-11-22 evening; 1220 in its place, #369, with the worn-object paste's words; then 1226, #374, walk mode's position at a stop; then 1232, #375, 2026-11-25, walks after a review of the walking code; then 1237, #378, that evening, walk mode's answers in the chat history and V in a crossing; then 1240, #379, an hour on, its warnings kept out of the history as she'd been told), after her log on 1212 at her partner's stairs: a //go to him from the
  landing point, a log either way, whose plan line says whether it was planned for a narrower body (the planner
  harness that checked it headlessly is kept in `local/planner-harness`, not in the repo).

- Sent with 0.1.0.1209 (#354, 2026-11-22 evening; 1212 in its place, #355, following down steps fixed), after her four logs on 1206 at her partner's steps (His steps in the model
  but cut off): a //go to him from the landing point while he stays on his floor, with a log, which now says where
  her walk stops short of his floor and why.

- Sent with 0.1.0.1206 (#344, 2026-11-22), after her logs on 1187 (Walks sent above her; A teleport partway down a ski
  route): a //go to her partner from where she lands while he stays on his floor, and her route up his steps
  ("Tessa") again, a log either way; whether the script menus' new order suits her (the old one is a setting).

- Sent with 0.1.0.1187 (#329, 2026-11-20 evening), after her first logs on 1182 (Following her partner up his steps,
  above): a //go to him from where she lands while he stays put, and a log when it sticks, which now names what's at
  her feet nearest first.

- Sent with 0.1.0.1182 (#328, 2026-11-19 night), after her logs at her partner's steps and on his boat: whether following him up
  the steps works now (his trail), and a log after any walk there that still fails, which now names what's under her.
- Sent with 0.1.0.1173 (#322, 2026-11-19 evening), after her logs of 11-18 and 11-19 (Walks that found no way; A group notice
  that wouldn't open): whether a notice opens from the Groups tab now (a log if not); if she goes back to where her
  partner was, whether walks there still fail (a log then says why); and what lies between where she lands there and where he
  stood: steps, a ramp, a doorway. She answered at once (#323): steps she had to go up confused it, which fits his
  floor being judged out of reach. Asked (#324) for a log after walking up them with 1173, working or not: its why and
  stuck lines say how those steps are built and why they were judged so (a riser past the planner's 0.4 m step, a
  physics shape that isn't the steps, or a slope).
  Her log on 1173 an hour later (`tester-20261120T010915-a6aa07`, "I failed to walk to" him) has the new lines: from a
  wooden deck (floor 52.41, over ground at 50.10) to him over a floor near 54.45, judged out of reach; each stretch then
  stuck with nothing in the model ahead at any height while she rose, feet 52.5, 53.7 and 55.9 with nothing under her in
  the model and every shape within 3 m said to be in it, drifting east from where she was sent. So it isn't the planner
  misjudging steps: Second Life has something solid there she walks up, most likely the steps, and our collision model
  has nothing there. Told her so (#325). Between them (`tester-20261120T011123-0ed95d`, read 2026-11-20, Claude Opus
  5.5): `//go 24,24,56`, the point he gave, went the same way from the deck, except that its first stick (nothing in the
  model ahead, not lifted) ended with her 2.3 m down on the ground and 3 m east ("On ground."), and from there no way at
  all; then a follow from the ground stuck at the foot of the steps, as below. On 1182 a lifted stick is trusted, but one
  on her own floor still goes round as before. Minutes later (`tester-20261120T011248-3f85cc`): following him from the
  landing point stuck at the bottom of the stairs, "no way up from here", which she says worked in an earlier build.
  Looked into the same evening (Claude Opus 5.5 with agents): the model has a floor near his height reaching 8 m out from
  him but not the steps up to it. Physics types, phantom flags, placing parts and decoding mesh physics all checked
  against the viewer and ruled out; most likely the steps never reached our world at their place (an update missed or a
  part whose root never came), or a mesh's flat physics hull left out (whether Second Life collides with flat hulls
  isn't known). 9e328a4 logs, at a stick with nothing under or ahead of her in the model, every part whose box comes
  within a metre of her feet, so her next log there names it. Why following worked before: until 2026-11-11 a follow
  steered at a spot short of him whatever his height, through steps the model lacked, and the region carried her up;
  since the floors work it finds his floor out of reach and stops. Decided: where the region plainly has a way the
  model lacks, the region's word wins (a follow takes his trail; a walk the region is lifting keeps on), as a fall off a
  deck in Second Life is harmless and said, while not reaching him is the failure she reported.
- On 1162 that evening (`tester-20261117T234133-003f45`, 18:41, "to show you how well this worked"): from what you wear she
  edited her worn chat-range HUD (Objects' Edit, "Editing Chat-Range 2, worn on HUD Bottom Right"), opened its script
  ("contents: script Chat-range alert in Chat-Range 2 read: asset id kept from us, 2651 bytes", so the guard's path on
  Second Life), saved twice with a syntax error said at line 10, fixed it ("Saved and compiled."), then pasted a sound in
  ("Put Ocean1 inside.") once she noticed the script named one that wasn't there. Each save asked its confirmation (a script
  replaced), answered in 2-3 s; not raised by her.
- Her ask that evening (#312): a ride recording that goes on when the vehicle stops, as on a loop with many stations.
  Built at once (a stop no longer ends one; an hour at most) and sent in 0.1.0.1162 (#313), to take instead of 1158.
- Sent with 0.1.0.1158 (#311, 2026-11-17 morning; #310 had told her what her two logs showed): does her HUD's script open, save and keep working from what you
  wear (Alt+Shift+7) > Contents, the HUD staying on? Are words like "Position Z, up, from HUD Bottom Left" clear? Would she
  want a body attachment's faces named rather than numbered? And when a crossing fails as on 2026-11-16 and the region then
  answers nothing, would she rather Sojourn Access logged her in again by itself after a while, or told her and let her
  choose?

- Sent with 0.1.0.1142 (#292, 2026-11-15 evening): what got in the way reading a script (NVDA not speaking its
  symbols, moving through the text, or the script's sense; told NVDA+P and Control+L); whether she'd rather her own
  Delete in the inventory moved things to the Trash without asking, as Radegast did. Her question about editing
  objects in the inventory answered there (Properties; the build only rezzed or worn). She answered the Trash one
  minutes later (#293): moving to the Trash the viewer's way, a confirmation for deleting for good so she has to think.
  Built that evening (`access_confirm_trash`, off). Her script answer (#294): nothing read at all (above, A script she
  couldn't read). Both went out in 0.1.0.1145 (#295), asking her to open one of those scripts again and send a log.
- Her ask after that (#296-298): putting objects, sounds and scripts into an object's contents by copying in the
  inventory and pasting. Built at once (`docs/access-app.md` > Building, the Content list's Control+V) and sent in
  0.1.0.1147 (#299).
- With 0.1.0.1136 (#287, 2026-11-14 night) and 0.1.0.1139 (#288, hours later, the memory work, to take instead): nothing
  new asked; 1125's three still stand.
- With 0.1.0.1132 (#284, 2026-11-13 evening): nothing new asked; 1125's three still stand.
- Sent with 0.1.0.1125 (#283, 2026-11-13): how her partner made the place link that wouldn't open (the map, the
  location, a landmark dragged in); whether she pressed anything but F3 and Down while the window froze at the
  station, and whether anything else was busy on her PC then; for her TV, whether turning up Windows' or the TV's
  volume brings the sound, and whether other programs' sound comes out of it.

- Sent with 0.1.0.1113 (#281 on the builders' line, 2026-11-11 late): should a HUD's chat line that repeats its own menu
  (GTFO's "task complete", said in its menu and in nearby chat a second on) go unread, kept in the history, so she hears
  it once?

- Auto-sit (built 2026-11-09 from Radegast's AutoSit and issue #156; nothing reported): did she use Radegast's, and for what
  (a seat at home, at a club, to stay seated while away)? Ours sits her once on each arrival by logging in or teleporting into
  the seat's region and then lets her stand; Radegast's sits her again every ten seconds while it's on. Which does she want?
  Is one seat a region enough, and is Control+Shift+S a good key? Did she use Radegast's PseudoHome (go back to a spot when
  moved) or LSL Helper (her objects sending her account IMs, gifts and group invites)? Neither is built; what would she use
  them for?
- Export and import (built 2026-11-09): has she or her partner exported builds from Radegast (XML) or Firestorm (Backup), and
  would she bring them here? Which kind would she keep her own builds in? A Collada model goes back up as a mesh by Control+U:
  would she or her partner do that?
- For the inventory's housekeeping (2026-11-08): Radegast starts sorted newest first, Access by name (Alt+0's "Inventory
  order", or Sort by date on the Applications key): which does she want to start with? Show: Recent now lists everything
  newest first, each saying when it came, rather than the last day's: is that how she'd find a purchase, or would she rather
  Radegast's "since I logged in"? Are Search by Creator and Description (a Tab after Show) worth the extra stop? F5 now drops
  what was deleted or moved in another viewer: worth telling her, after her deleted folder that stayed. Back up to disk saves
  the list of everything and the notecards and scripts she made herself (Second Life's rules for viewers don't let it take
  others'): would she want pictures too?
- Always run (`//run`, World's menu): on Second Life, does a `//go` run with it on? The local grid's autopilot turns running
  off as it starts and only runs because Access asks again. Health is on the status bar where damage is on, and on `//status
  health`: in a combat area, would she want a line said when she's hurt, or would that be too much?

- With 0.1.0.887 (2026-11-03, on the builders' line): a log after a ride (switches found sooner?) and after a throw
  with her partner; whatever is awkward in About Land. Still to ask about land when she's used it: is the split between
  switches that change at once and changes that ask first right; are the rows' words clear ("Damage allowed" rather
  than the viewer's "Safe (no damage)"); is Control+L a good key; divide and join by compass words or from her facing?
  And whether the world going much softer under a beacon is right.
- What a friend points at (built 2026-11-08 from Radegast's Master Controls; nothing reported): does her partner or a friend
  right-click things in their own viewer to show her, and do "... points at ..." and Control+Shift+P to go there do what Master
  Controls did for her? Should whose pointing she hears be remembered for next time (it lasts the session now), and should a
  pointing make a sound too? On a person's menu, are What they wear and Their animations any use?
- Point at (built 2026-11-09 from Radegast's Point At; nothing reported): Control+P on a thing or part in Objects, or `//point
  NAME`, points her arm at it for a minute with a beam from her hand. Did she use Radegast's, and does a sighted friend pick the
  thing out by it? Is a minute right? Turning her to face it when it's well to a side or behind (so the arm reaches it, as the
  official viewer does): welcome, or would she rather keep her facing? Would she point at people too (Radegast's people list
  can; the official viewer never does)? Is Control+P a good key?
- Display names in fancy letters or symbols (built 2026-11-08 from reasoning; nothing reported): has anyone's come
  through as nothing or nonsense, here or in Radegast? Access now says them in plain letters, the username when there
  are none; Alt+0's People's names also has the display name and username (Radegast's usual), as written, or the
  username alone. Which would she keep, and has a name come out wrong?
- Reading back (Control+[ and ]) stays where she left it however many lines arrive since; should new lines, or a
  while without the review keys, bring it back to the newest (Control+Shift+] does now)?
- Sent with 0.1.0.877 on the builders' line (2026-11-02, 22:45): was going into Pavonia at the end of her solo run on
  purpose; a log after trying a switch throw; whatever is awkward in the build editor. Her long train-loop ride
  recording was planned for 2026-11-03 (the first on 15-minute recordings and 877's switch lines). Still to come from
  her before that: harp clip length, how the IM sounds feel.
- Sent with 0.1.0.737 (2026-11-01): a log after her next ride, even a good one (it now records each crossing);
  where she found the skirt and pet copies (Alt+7?); for her partner, whether his train stops a few seconds after losing
  its driver, and whether riders in other viewers are dropped at that corner too.
- Sent with 0.1.0.718 (2026-10-31, 23:22): does the assistant now see on her PC (pictures on the processor), and
  how long does a description take? Are Control+Shift+G (gestures), L (copy the last answer) and O (sound output)
  good keys? Should a gesture say "Playing X." each time, or stay quiet since its chat line is read back? Answers
  arrive on the helper channel (`tools/tessel_watch.py`); a log she sends is `python tools/relay.py logs fetch`.
- First: while Sojourn Access is in the background, should she hear everything, only IMs and menus, or nothing (a
  setting either way)? TWBlue keeps reading off by default as too chatty and relies on sounds; Mudlet reads.
- F6 mutes the read-out here (the old add-on's key), but in Windows F6 moves between panes: keep it only if she used
  the add-on's key. Enter on a worn item takes it off: is one stray Enter too easy?
- What "edit notecards without going back two versions" was: asked 2026-11-07, she never found out why it happened, and is trying notecards in Access; a card going back a version would be worth a log.
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
- For rail switches (after the build following 811): is a switch said early enough ("Switch in 40 metres", up to 60 m
  ahead), and is the main line's or branch's name from the stand useful or noise? Are there still "unknown" switches,
  or switches said in bursts? Do W and Shift+W sit well once she's tried a throw (a log of that ride would show what the
  stand did), or would she rather a key that sets it to the main line and one to the branch? For her partner (the
  atoll's stands do follow the standard, by her 811 log): does touching a stand flip it, or open a menu; and are the
  switches' guide rails objects of their own, apart from the stand?
- For the track's end (after the next build): did it stop in time, before the derail? (Answered 2026-11-02: on her
  train, Down once it's stopped doesn't reverse it; other trains may.) For her partner: does holding Down take more
  than one step; what exactly does it say of its throttle, and
  anything else as the track runs out (a log's lines would do)? Is Space the right emergency key, or would she want one
  that works outside walk mode too (`//stop` works from Say)?
- For rides (after the next build): does C in walk mode sit well for recording a ride, and Shift+C for hearing it?
  Should a ride be told by itself when it ends rather than on asking (it isn't now: nothing is sent until she asks)?
  Is the telling the right length (brief about 100 words; again for more)? Does the recording end when she'd expect
  (standing up, or the ride still for 8 s), and is five minutes long enough for the rides she'd record? Does her
  partner's train show anything in its floating text besides throttle and speed, and is it right after V's speed or
  better only on Shift+V?
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
  and bodies? Pictures of people and things are lit as at midday for true colours, and the description says when it's
  night there: would she want that, or the place's own light (a sunset's on a face)? Would an overview on arriving
  somewhere new be welcome as an option, or only ever on her key? Without a Claude account, is hearing what the lists
  know (names, an object's own description, what's worn by name) any use?
- For parts and faces (M2): is "Part 2 of 4, a box above the root, touchable" the right amount (what, where, whether it
  takes touches), and is "the root" a word she uses? Are parts and faces as lists under the objects list (Tab, then
  Tab) easy to reach, or would she rather a key on an object? Faces by direction from where she stands ("the side
  facing you", "the left side") or by the object's own sides, or numbers only? Did the sunken city's chests have
  anything we haven't modelled (a part that only works when touched on one face, a hidden part)?
- For HUDs (2026-11-01): on the train's driver HUD, does "Read its buttons" find the controls she uses, under names she'd
  say, and is "Brake, button 2 of 9, at the top left" the right amount? Should the buttons come before the parts on a
  one-prim HUD (they're its only real choices), and should reading start by itself the first time she arrives at an
  unread HUD (a cent or less each time, through her code) rather than on Enter? Which HUDs does she use most, and do any
  change pages (each page is read once on its own)?
- For placing by words: what does she say when she places something ("a metre to my left", "in front of me", "on the
  table")? Is resting it on whatever's under the spot right, or should it stay at the height said?
- For the second pane: Alt+Shift+4 to open it and go there, Alt+4 back to the first, Control+M to move what's marked:
  does that sit well next to how she moves her fishing catch today, or would she rather a separate window
  as she asked?
- For what she's wearing (Ctrl+Shift+W): is the order right (clothing, attachments, HUDs, body), and does she still
  want her "**Wearing:" HUD labels once this exists, or would a line for the outfit folder worn be better?
- Incognito (Ctrl+Shift+I): would she use it at all, and is a key that turns it on easily but off only by a physical
  press the right balance for her?
- For uploads (2026-11-01): what does she upload most (pictures for a profile or a texture, sounds for a script,
  models), and from where (files her partner makes, downloads)? Is the window's first text the right amount, line by line
  (a picture's pixels and resizing, a sound's length and channels, a model's size in metres)? Would she want a picture
  described by the assistant before it goes up, or a sound played? Does Control+U sit well (the official viewer's old
  Upload Image key)? After an upload, should the focus go to the new item in the inventory, or stay where she was with
  it selected there (as now)?
- For building (after the next build), to her and her partner (his 2009 design is what it's built on):
  - Typing commands ("hollow 50", "face top") or browsing the settings list: which does she reach for, and is Control+B
    a good key (the official viewer's Build)?
  - Faces by direction from her ("the top, face 0", "the side facing you") change as she walks round; would she rather
    the prim's own sides (his "plusx") or the compass ("the north side")? All three are taken when typed.
  - Is "Full bright: off on 6 sides; on on the top, face 0" the right way to say what differs between faces?
  - Numbered choices: shininess and bumpiness are numbered from 0 as scripts number them (his post numbered shininess 1
    to 4): which?
  - Is a change said back right ("Hollow: 50 percent."), and after a move or resize is "resting on the ground;
    touching Wall" what she wants to hear, too much, or too little?
  - Resizing keeps the middle where it was (as the sighted editor does), so a box resting on the floor floats or sinks
    and is said so: should a resting prim keep its underside where it is instead?
  - For her partner: what would he add or change first (the rows left out are reflection probes and PBR materials)?
  - On a linked object (after the next build): is starting on all of it right (the official editor's default), or would she
    rather stay on the part she was on and ask for all of it? "This part only." at the end of each change on a part: useful, or
    too much once she knows? Should all of it turn round its root, as now, or its middle, as the official editor's mouse tool?
  - Should the assistant say what a change did to the look by itself, or only on "look" (as now: each costs a call)?
- For scripts and notecards (after the build following 0.1.0.996), to her and her partner: in a script opened from an object,
  is Mono ticked as she'd expect for her scripts, and do Alt+M, Alt+R and Alt+E (Mono, Running, Reset: the boxes' underlined
  letters, the focus staying in the text) sit well? Would they use Control+H's Replace window (Notepad's), and files on disk
  (Control+O to open one in place of the text, Control+Shift+S to save a copy)? In a notecard, is Control+Shift+V a good key
  for putting in what she copied in the inventory, and Delete in the list for removing a thing at the next save?
- Voice: does she use voice chat? Is F4 held a good talk key (Scroll Lock, Pause and F12 are choices), and would she
  want who starts speaking said by itself (off now; Ctrl+Shift+V says it on asking)? The people in voice (2026-11-08):
  Control+F4 opens them as a menu to act on; is a menu right, or would she rather a list that stays, as Radegast's tab?
  Mute everyone else lasts the session and stays off her block list (Radegast's mutes are for the session too; one
  person's Mute in voice is kept, as the official viewer does): is that what she'd expect? And Unmute everyone takes
  kept voice mutes of the people there off too: right, or should it leave the ones she made one by one?
- Radegast's Alt+number keys where ours differ (from the gaps list, 2026-11-07): Alt+3 is People and Alt+6 Places here,
  against Radegast's Groups and Map; Alt+8 and Alt+9 are Notices and the assistant, against Media and Voice; there's no
  key to her groups (People's third list, Alt+3 then Alt+G). Has any of that tripped her, and would she want Radegast's
  numbers back, or a key to Groups?
- Her routes: do any use Radegast's `//thread`, `//threads` or `//kill`? They work now (a route goes on past a
  `//thread` line at once; `//kill` stops a route, a walk, following and pauses), but only from Radegast's source.
- Read a session's log (Help, `//log`) shows it as Send a log would send it, other people's words left out: enough for
  her, or would she rather read her own session whole? Does she use a second account (World's Another account, in a
  new window, as Radegast's New window)?
- Groups (after the build with running one, 2026-11-08): does she, or anyone she knows, run a group or a club from Access, and
  what would they reach for next: making a role and its powers, or the group's settings (charter, who may join, fee)? A notice
  with something attached now waits in Notices with Keep as its first key, rather than passing as a line: right, or would she
  rather it were said and kept by itself? Are the members' and chat's windows easy to move around (Find, then the list, Enter
  on someone for what she can do), and is it right that a person's own actions there close the window first?
- A group's settings and roles (built 2026-11-09, the group menu's Settings and roles; nothing reported): are the abilities' words
  clear ("Eject members with no role but Everyone", "Give members roles, only ones they have themselves"), and is a switch each,
  under a heading a set with Page Down between them, quick enough to set up a role, or would she rather a role start from another's
  abilities (the viewer's Copy role)? Should Anyone can join and Cost to join ask first, as About Land's who may enter does, or
  change at her press like the rest?
- The estate (built 2026-11-09, About Land's Estate tab; for someone who owns or manages one, through her if she knows any): Eject
  from estate and Ban from estate are on a person's menu only where she manages the estate: right, or would she rather they were
  always there and said no elsewhere, as Radegast lists them? Is a ban that also sends them home if they're there what she'd
  expect? Are the estate's own switches (open to anyone, payment info, age) wanted changeable here?
