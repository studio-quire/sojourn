# Sojourn Access: the plan

Written by Claude Opus 5.5, 2026-10-29. Agent-facing. The app for using Second Life without sight: a separate app in
Sojourn's codebase (the user's decision, CLAUDE.md > Usable without sight too). Its first slice of M1 is built
(`src/Sojourn.Access`; using it, how it fits and testing: `docs/access-app.md`), on A1, the screen-reader announcer
(`docs/screen-reader.md`), and M0's session host, `src/Sojourn.Session` (below).

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

Confirmations, notices' keys, the ToS and typed answers count only the person's own press (`docs/access-app.md` >
How it fits): a key on a physical keyboard, which Windows marks as hardware, because the assistant can run programs on
the PC and a program's keys are otherwise indistinguishable. That locks out people whose keys never come from a
physical keyboard: a braille display's keyboard (NVDA types its input with SendInput), Dragon, Voice Access, on-screen
keyboards and switch access all arrive as injected input, exactly as a program's would. So
`access_answers_without_key` (Settings, `refuse` by default) can be set to `hello`: such an answer is then asked again
by Windows Hello, in Windows' own window, with the action in its words ("Sojourn Access: Pay L$10 to Kestrel."),
answered by face, fingerprint or PIN; Cancel, Put away and the assistant's choices take any input then, since they only
ever say no or ask. The trade-offs:
- It needs Windows Hello set up (a PIN at least); without it the app says so and the physical key is still the way.
- Face and fingerprint can't be supplied by a program. A PIN can be, by a program that logged the person typing it
  earlier, if Windows' prompt takes injected keys (not checked; Windows doesn't promise otherwise), so with only a PIN
  it's weaker than a physical key, though a program would also have to raise the prompt at the right time and the
  person hears the action named in it.
- Every such answer costs a prompt: fine for paying or giving, heavy for paging through a script menu. And a program
  can raise prompts by sending keys (one at a time): a nuisance the person declines, not a way through.
- Not offered: the typed check (`confirm_text`), since a program that can type can read the word on screen and type it.
- How Windows' prompt reads with NVDA and JAWS, and whether it takes a braille keyboard's or an on-screen keyboard's
  PIN, needs a real run.

## Shape

- **A Windows app, `src/Sojourn.Access`**, WinForms with the standard Windows controls (edit boxes, RichEdit, list
  and tree views, tabs, buttons). Why: these are what NVDA and JAWS have read reliably for twenty years, they're
  what Radegast Legacy is and why it works, and Veles shows that a custom-drawn toolkit (Avalonia) stalls on
  accessibility. Our own drawn UI (the VR client's) is out. WPF has good UI Automation too; WinForms is chosen because it's
  the same ground Radegast proved with both NVDA and JAWS users. Windows only: blind SL residents are on Windows readers in every
  account found.
- **Shared session host.** `src/Sojourn.Session` (M0, built): the session's parts that the VR client's `Program.cs`
  used to wire around its window, and `SessionHost`, which starts them in steps. Access creates one and calls
  `StartAllAsync` (or the steps, to put its own views' hooks in); what's still the VR client's is listed under M0.
- **The renderer, out of sight.** Access needs a picture for `look` and description, picks on faces, and the local
  collision model for walking and warnings. It runs offscreen at a low rate and small size (Multitude renders on
  demand offscreen already), more when the assistant looks. Low-end PCs: a setting for how much it renders.
- **The agent API and ops** are the same: every action in Access goes through the op dispatcher, so the person's
  confirmations hold for keys and the assistant alike.
- **Testing without sight** (Radegast regresses its accessibility nearly every release, with no screen-reader testing):
  a check that walks the window's UI Automation tree (every control named, roles right, Tab order sane, nothing
  steals focus on incoming events) and replays the Radegast guide's workflows (chat, IM, give, unbox, teleport by
  SLurl, answer a script menu) on the local grid, listening to what's announced (`tools/uia_listen.ps1`). A sighted
  check catches the obvious only; a blind tester judges the rest.

## Milestones

Sizes as in viewer-gaps: S a day or less, M a few days, L a week or more, for one agent.

**M0. The split** (M). **Built** 2026-10-29 (Claude Opus 5.5, commits dbb7943..; `docs/client.md` > How it fits).
`src/Sojourn.Session` holds what isn't the VR window's, with the client's folder names: the agent API, session logs and
console chat, the assistant's host with its panel model and raster (one class with its painter, so the sheet kit came
along: Panel, PanelLook, GlassLight, AdaptiveInk), confirmations, sidecar, dev mode, snapshots, recorder, pictures
(Capture/, with a renderer), the reader's model; voice and world audio; the feed's data, social settings, keywords,
radar, translation and links; incognito's state, allowlist, API and wiring; media's manager, players and helpers;
people, land (outlines with a renderer), building and inventory; the map's data; the screen reader's wiring; VrSettings
and SojournSettings with the avatar, environment, safety and report APIs; the login store. `SessionHost` starts them
in steps; the VR client runs the same steps in the same order (its SojournSettings and API lists are identical to
before: 155 settings, 45 pumps, 452 ops, in order, on the desktop and in the XR Simulator, with the same frame times
and pictures); notices.py, screenreader.py and land.py pass, a typed request reaches Claude and changes a setting, and
people.py stops at its first step (the friendship offer never arrives) on the commit before the split too.
`src/Sojourn.SessionProbe` (`tools/probe.py`) is the proof: two console hosts on the library alone log in, answer
SojournSettings through their agent APIs and hear each other's local chat. The seams an app fills: `ISettingsMenu`
(each part's menu section), `IDictation`, `IShowing`, `ISeenView`, the feed's recall, the window's handle for the
screen reader, a renderer if any.

Still the VR client's, and what Access needs from them (the next splits, as it reaches them):
- The client's own SojournSettings rows (`Control/SettingsStandard.cs`, was SettingsApi.AddStandard): the assistant's
  model, efforts, spoken replies, hearing and look, voice on/mode/volume, names and chat switches, maturity and draw
  distance sit among the headset's and renderer's rows. Split the session's out before M1.
- Incognito's controls (`Private/PrivateControls.cs`): its three settings (incognito, incognito_sees,
  incognito_while_im), the triggers' wiring to the open conversation and dictation (`Private/Dictation.cs`, whose
  draft goes to the conversation open on the drawn feed). Access needs the settings and triggers (M1).
- SojournFeed (`Social/FeedApi.cs`: notices brought back onto the drawn feed, the feed's key positions) and
  SocialView (it makes the SocialFeed; StartAllAsync makes its own).
- The map's SojournMap (`Map/MapWiring.cs`, `MapApi.cs`: where and stats live with the board); media's wiring, input,
  boards and SojournMedia (`Media/MediaWiring.cs` takes Silk input and headset frames); what the assistant shows
  (`Showing/`, drawn cards entangled with their painter); HUDs, beacons, safety (`SafetyWiring` takes the VR pilot),
  environment (renderer), AvatarsApi's limits (renderer), touch surfaces (`DirectTouch`), WayOut's keys.
- The login screen (`Login/`); a grid's ToS and MFA questions are answered there or in the console (ConsoleChat).

**M0b. The renderer out of sight** (M). **Built** 2026-10-29 (Claude Opus 5.5; `src/Sojourn.Session/Offscreen/`,
`docs/access-app.md` > How it fits). `OffscreenRenderer` is the VR client's `FrameRenderer` with no window: Vulkan
without a surface, frames read back nowhere (pictures by the snapshots' callbacks), one thread of its own at below
normal priority that both simulates and renders (no pipe: at a few frames a second overlap buys nothing, so
`FramePipe` stays the client's), the world queued to it between frames (`OnWorld`). The camera is the avatar's eyes
as drawn (the shared dead reckoning, seated on its seat); it never sends movement or a camera. Frames: idle ones at
`IdleHz` (2) and `IdleSize` (640x480), each a quarter turn around the avatar and a little down, so everything near gets
drawn now and then (its triangles kept for picks and the collision model, which count prims only once drawn); 5 a
second while the assistant works, every other one ahead at a picture's size; a picture (the snapshot ops, the
assistant's `look`) at its own size (16:9, at most 1280 wide), twenty frames a second until what's in view has arrived
(at most 3 s), then taken; a touch on something not drawn yet glances at it first. Each frame waits for its GPU work.
The region feed moved into Session (`RegionFeed.cs`, the VR client's too) and `PhysicsShapes` with it. Touches:
`AgentSession.FindTouchSurface` as `DirectTouch` finds it without a pointer, plus `FindFaceSurface` (a Core hook the VR
client doesn't set): requestTouch with `face` and no point lands in that face's middle from just outside it, the far
side of a part too (`RegionScene.FaceAim`), with the face's own surface coordinates. `SojournView` (status, `collide`:
the collision model's ray from the chest by heading or direction). A lost GPU drops the renderer and starts a fresh one
in the same process 3 s later, every region fed again (the app and session go on; three in ten minutes and it stops
for the session); `SOJOURN_OFFSCREEN_LOSE_AFTER=S` fakes one. `SessionHost.StartOffscreen` starts it (Access, and the
session probe with `--render`); Access's rate and size are settings. Since 2026-10-30 it's unseen while nothing can
look at its pictures (no assistant up or starting, no picture asked for in 10 minutes: a packaged build always): only
geometry drawn, no textures but sculpt maps, no avatars, shadows, reflections or particles; a picture asked for or the assistant coming up draws it whole again (`docs/access-app.md` > How it fits > The renderer
out of sight). The numbers below are for drawing it whole.
Numbers (local grid, Atelier, RTX 5070): an idle 640x480 frame ~1.1 ms of GPU, ~3-5 ms of CPU, so ~0.2% of the GPU at
2 a second; a picture 0.4 s once the place is loaded (the first after arriving waits up to its 3 s); the renderer adds
~400 MB of working set (~1 GB committed, mostly the driver's and the GPU allocator's) and ~0.5-0.6 GB of video memory
in a small place. Checked by `tools/access.py` (its renderer step: a picture by `saveSnapshot`, the collision model
meeting a box, touches on a box's far face and by the eyes' ray reported by its script, all after a faked GPU loss)
and by the VR client's desktop and XR Simulator runs before and after (the same APIs, ops and settings in order, frame
times and pictures alike: 43 APIs, 434 ops and 149 settings in the same order, only requestTouch's description changed; desktop CPU 1.59 / 1.55 ms a frame median, GPU 2.0-2.1 ms; XR Simulator 72/72 fps, CPU 2.30 / 2.14 ms, GPU 5.9 ms; the shots differing by no more than the sky drifts in a minute). Left: AvatarsApi (its limits belong to one renderer, so a restart needs them
carried over), the person's environment choices (the client's `EnvironmentWiring`), quality settings beyond rate and
size (mirrors are off, shadows capped at 1024), stills and 360s through `CaptureWiring` (it holds a renderer, so it too
needs the restart in mind), and HUD prims for touches. (What the collision model sees for walking is M3's walk mode.)

**M1. Radegast's daily life, better** (L). The first build worth showing her. Login (with the ToS and MFA answered by
her keys); areas by Alt+1-9: nearby chat, conversations (IMs and group chat as tabs), people, objects, inventory,
notices, search, places (landmarks, history, the map by name), the assistant. A1's announcer in the window, with the
add-on's keys (mute all, mute a source, review, mark, copy) and never-steal-focus. Script menus, offers, permission
requests and llTextBox as named controls answered by keys. The `//` command set with Radegast's syntax (`//go`,
`//find`, `//face`, `//sit`, `//follow`, `//tp`, `//status` and the rest, radegast.md has them all), its
two object-search bugs not copied, plus `//go` lists played from a notecard. People and objects lists with bearings
("Kestrel, 4 m, two o'clock") and an Applications-key menu of every action; arrivals and departures announced (a
switch). Teleports by SLurl on the clipboard (Ctrl+G), landmarks, search, offers; "arrived in X" said. Profiles,
friends, groups, pay and give behind confirmations. Works with no Claude account.

M1 progress (2026-10-29, Claude Opus 5.5; `docs/access-app.md`). Built: login by keyboard (ToS and MFA answered by
her own keys; the local grid only until the TPV must-dos are done for this app); the areas by Alt+1-9 and 0 (nearby
chat; conversations as tabs with Ctrl+Tab and Ctrl+W; people nearby with bearings, friends and my groups; inventory;
search; places; objects with bearings; notices; the assistant; settings), every list's actions on the Applications
key; the announcer in the window with mute all (F6), mute a source (Shift+F6), review (Ctrl+[ ], with Shift the
oldest and newest, Ctrl+R), mark and copy (Ctrl+Shift+M, C), times by key (Ctrl+Shift+T) and never-steal-focus; script
menus, offers, permission requests and llTextBox as named controls answered by her keys; Radegast's `//` set (go,
find, face, sit, stand, follow, tp, status, pause, siminfo, parcelinfo, scriptinfo for what's worn, help) with the
object-search bug not copied, and `//route` playing her `//go` routes from a notecard or the clipboard step by step;
Ctrl+G and landmark teleports with the arrival said; inventory (a tree with types and worn states, search, Show worn
and recent, Space marks many; wear, add, take off, outfits, rez on the ground, unbox, cut, copy, paste, rename, new
folders and notecards, give and delete behind her confirmation; notecards and scripts in a real edit control); places
(landmarks, teleport history, a region by name, the regions around); search (people, places, events, groups,
classifieds; IM, teleport or join from a result); profiles from any list of people; pay behind her confirmation;
arrivals and departures by key (Ctrl+Shift+A, Firestorm's radar reports); the assistant as a typed request, its walks
dead-reckoned (they overshot without a renderer). Since then (Claude Opus 5.5, the same day): **incognito** by
Ctrl+Shift+I (off only by a physical key, as confirmations; Session's Private/ state, allowlist and triggers: Alt+0's
let it see, while in an IM, always on this parcel, and "Always incognito with them" on a person; the title leaves out
the place) and **voice chat's keys** (voice on by default, push to talk on F4 or a key of her choosing, Shift+F4 an open
mic, Ctrl+Shift+V or `//voice` who's in voice and speaking, someone starting to speak said if she turns it on, a call
said and answered by `//voice answer`). Left in M1: a blind tester's verdict on all of it; voice checked only without
the voice server in the hidden-desktop test (its step with `ACCESS_VOICE=1` needs `tools/grid.py voice up`).

**M2. Her first new things** (M). The cheap wins from her list, where Sojourn already has the machinery:
- Every part reachable: a linkset's parts listed by name and number (unnamed ones as "part 7 of 12, a box above the
  root"), touched by part and face; HUD buttons the same way. The treasure chest test (the Linden sunken city's chests).
- Place by words ("a metre to my left"); rez on the ground is built (M1).
- Inventory: a second pane (multi-select, cut/copy/move, the Worn filter and Delete asking first are built, M1).
- Notecards without losing versions (what she meant is a question for her); scripts' compile errors are read out
  (M1); the cursor's line announced is left.
- What am I wearing: from the worn list, and with the assistant, from the picture (her outfit check without sighted help).

M2 progress (2026-10-29, Claude Opus 5.5; `docs/access-app.md` > Parts and faces, Placing by words, the inventory's
second pane). Built: **every part reachable**: in Objects the selected object's parts listed under it, root first,
named ones by name and unnamed ones by number, what and where ("Part 2 of 4, a box above the root, touchable"), each
part's faces under that by direction from her ("The top, face 0", "The side facing you", "The left side"), Enter
touching that part or face (never the root instead); Tab passing a list never changes what the next is about (the
Radegast guided test lost the part that way); Show: your HUDs makes a HUD's buttons parts like any other; `//touch
NAME`, `//touch OBJECT PART`, `//touch NAME face top`, and an object whose only touchable part is a child touched
there by its own name. The treasure chest test: the visitor's `treasure` fixture (an unnamed lid the only part with a
script, a named latch, an unnamed foot), found and its lid touched by keys and by `//touch treasure chest`.
**Placing by words**: Rez where you say... and Move... ("a metre to my left", "2 m ahead and half a metre up", "at two
o'clock, 3 metres", "here", "by 20 cm up"), resting on the surface under the spot (a table, by the collision model).
**The second pane**: Alt+Shift+4 (Alt+4 again until 2026-10-30); Ctrl+M moves what's marked into the folder selected in the other pane (copy on the
Applications key), each pane its own search, Show and marks. **What I'm wearing** without a model: Ctrl+Shift+W or
`//outfit`, by name, grouped (clothing, attachments and where, HUDs, body), her "**Wearing:" label first. Reshaped:
the faces need the renderer out of sight (with `--no-render`, or on a HUD, parts are touched whole), and a HUD drawn as
one prim with button regions by touch position can't be pressed by part: that's a picture job for the assistant (the
VR client's SojournHud presses by a picture's point; Access doesn't draw HUDs out of sight yet). Part numbers are
ours (root first, then the order the region made them), not promised to be scripts' link numbers. Left out on
purpose: notecard versions (a question for her). Left: faces of HUD parts, rotating things by words, `//place` as a
command, a blind tester's verdict.

**M3. Moving freely** (L). Walk mode: arrows walk and turn, a sound per step (by surface where known), cues for walls
ahead, openings and doors from the collision model (Shades of Doom, which she asked for by name in 2009), heading in
degrees on a key, a warning before a bump. Routes: record a walk, name it, replay with progress spoken, shared as a
notecard of `//go` lines. Follow a person (a moving target). Paths around obstacles (A* on the collision model) for
`//go` and the assistant's "walk me to the bar". Vehicles and seats with taken controls driven by arrows, with private
feedback on speed, heading and what's ahead; seated border crossings that hold (viewer-gaps' crossing work), and the
crossing said.

M3 progress (2026-10-29, Claude Opus 5.5; `docs/access-app.md` > Walk mode). Built: **walk mode** on F3 (a pane that
takes the keys, as Radegast's 3D window does): arrows walk and turn through the region's own movement (Shift runs, a
tap turns 22.5 degrees onto the compass's points, held turns smoothly, Shift+Left/Right steps sideways, Page Up/Down,
Home flies), letting go brakes with the viewer's STOP flag; footsteps by surface (ground, stone, wood, metal, glass,
soft, water, from the collision model's floor and the prim's material); a warning before a bump naming what's ahead
(a door by its name, with knocks); "Blocked."; openings and named doors passed on either side, heard from that side;
a drop or water ahead; H heading, V speed, L where, A what's ahead, T touch what's ahead (doors), D doors by name; S
the sonar (walls on four sides, openings by clock face with their width, a sweep of twelve pings round the clock);
every cue a sound and a line, each with its own setting for how it reaches her and how loud, the sounds made in code
and restrained. **Paths** around what's in the way (A* on the collision model with room kept from walls) for `//go`,
walk to, follow and the assistant's walks, each stretch said, a stuck stretch planned again, and a teleport when there's
no way. **Follow** as a moving target (dead-reckoned, 2.5 m behind where they'll be, around obstacles), saying when
she's with them, when they sit, get away or are gone. **Driving** seated or with taken controls by the same arrows,
with the vehicle's heading, speed and what's ahead (its own parts left out), and a **seated border crossing said**
with whether the seat held ("Now in Atelier East, still on Walk Kart."). Also fixed on the way: Access never set world
audio's listener, so nothing placed in the world played (objects' sounds, footsteps, the new-IM sound). Checked by
`tools/access.py test`'s walk-mode step on a course of its own in Atelier East. Left: routes recorded by walking and
saved as a notecard of `//go` lines (playing them is built, M1); paths across levels (stairs count as walls); the
sound design and every default judged by a blind tester; SL's own vehicles and crossings (only the local grid's).

**M4. Sound as a map** (M). Earcons per arrival kind, a beacon on a target while walking to it, a sonar key for walls
and openings, sounds marking doors, seats and touchable things on request; separate volumes. Sound design judged by a
blind tester only.

M4 progress (2026-10-29, Claude Opus 5.5; `docs/access-app.md` > Earcons, Walk mode). The sonar came with M3. Built:
**earcons**, eleven kinds (IM, mention, menu, offer, group, nearby, objects, friends, radar, notice, sent), each off or at
a level of its own and played when its Alt+0 row changes so it can be learned; on by default only for what's
addressed to her and waits on her (IM, mention, menu, offer), as the viewer sounds a new IM and Radegast a notice, since
NVDA reads every line anyway; nearby chat, objects and arrivals sound from where the speaker is; F6 mutes the words,
not these; they replace the viewer's new-IM and alert sounds in Access. **The beacon**: a quiet pip from where a walk
is heading (`//go`, walk to, a follow on the person as they move, the assistant's walk), brighter while straight ahead,
and one she can put on someone or something (`//beacon NAME`, the lists' Applications key) to walk to by hand in walk
mode until "Reached"; B says where it is. **Marks**: O in walk mode names the doors, seats and touchable things within
12 m and sounds each from where it is, going round as the sonar does. **Volumes**: each cue and earcon its own, Access's
own sounds together apart from the world's and the music (a new App kind in world audio), all in Alt+0. Checked by
`tools/access.py test`'s sound-as-a-map step (which sound starts, where it's placed). Left out on purpose: a sound for
every person in range (VRBubble found it distracting past about five; radar arrivals cover the moment that matters),
continuous ambient beacons on things nobody asked about, and earcons following F6 (a question for her). Left for a
blind tester: every sound and default.

**M5. Describing** (S-M, needs the assistant). Places, people's looks, signs and posters, pictures, on request, brief
or rich; the lists' names used so it doesn't guess; uncertainty said. A careful describer's register for outfits (cut, colour,
what's pictured, where on the garment).

M5 progress (2026-10-29, Claude Opus 5.5; `docs/access-app.md` > Describing). Built: Ctrl+D describes what's selected
(a person, friend, object, inventory item or profile's pictures) or else where she is, Ctrl+Shift+D her and what she's
wearing, the same key again for more; Describe on the lists' Applications keys and in profiles; `//describe`. The
assistant gets pictures aimed at the subject from the renderer out of sight (a person whole and from the waist up from
their front; an object from her side, a sign or poster face-on with its image as uploaded; here, four views round her),
each with what's visible in it by name and where from her, and the lists' facts (names, descriptions, floating text,
what someone wears by its items' names), so it names rather than guesses; how to describe (brief by default, a careful describer's
register for clothes, text word for word, uncertainty in words, nothing invented) is in its tool. The request names its
subject by id and the pictures start drawing as it goes. Without a Claude account it says describing needs one, then
what the lists know. When the place is dark, people and things are lit as at midday for true colours and the model is
told so; here keeps its own light (the user's call). Replies are read whole in Access (A1's announcer cuts at 700
characters elsewhere). Checked by `tools/access.py test`'s describing step (19 of 19 with the renderer: the path without
an account, and the pictures framed and named). Judged by hand on the local grid in quick mode (Haiku 5.5), two runs:
the request answered in 4-7 s (one tool call; the pictures were ready when the model asked, 0.2-1.9 s to draw); the
poster read word for word with its colours ("MOONLIGHT CAFE in gold capitals with a gold crescent moon to the right,
then Open 6 pm to midnight in white and Live jazz on Fridays in orange"); people's looks garment by garment (a fitted
long-sleeved grey top, bright red capri-length trousers, barefoot) and noticed the visitor looks the same as the tester;
directions by clock face as the lists give them; a person not in view said plainly, with nothing made up. Misses: in
the first run a named box was called "a yellow box" (the names now sit beside each picture too, and the second run named
everything), a rich description was cut short by the announcer (now read whole), and once "the pictures don't show..."
leaked the tooling. Not seen yet: the midday relighting at night (the grid's day had come round), Opus or Sonnet's
descriptions, real SL places, crowds, and mesh clothing with prints, the case that register is for. A blind
tester's verdict on all of it is what counts (questions below).

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

Not a blind user's verdict: the user is sighted and new to screen readers, and ran the guided test
(`docs/access-app.md`) with NVDA 2026.3 speaking aloud and Companion guiding. NVDA's speech and input log says what
was really said and pressed. What was found, and what was done (Claude Opus 5.5):
- **The llTextBox reply was refused** ("That takes your own key.", twice, with a Windows ding). NVDA takes Enter, the
  arrows, Home, End, Page Up and Down, Backspace and Delete in every edit field and sends them on with keybd_event, so
  the app saw injected input and refused it as a program's; number keys on a list passed because NVDA leaves those
  alone. The MFA code's Enter would have been refused the same way. Now a keyboard hook ahead of NVDA's notes the
  hand's presses, and each press lets one key message of that key count (`docs/access-app.md` > How it fits); Enter
  in one-line inputs no longer reaches the edit as a carriage return (the likely ding). Checked by `tools/inputcheck`
  and the hidden-desktop test; with NVDA and real keys only the next run can check it.
- **Ctrl+[ only said "blank".** NVDA's log has Ctrl+= (the key by Backspace), then Ctrl+Down twice, NVDA's own next
  paragraph in the Say box, which said "blank". Ctrl+[ never arrived; NVDA doesn't take it, and the window's keys come
  before the edit's. The guided test now says where the bracket keys are.
- **The menu's "Control N to answer." was lost**: spoken, then cut off 0.7 s later by Alt+1 (NVDA stops speaking at
  any key or focus move). A notice cut off before it could be heard out is now said again once the keys rest; no
  earcon for now.
- **Inventory**: every item read "check box, not checked" (the multi-select used the tree's check boxes), taken for
  worn or not; now Space marks into the item's words ("..., marked"). The item's words didn't follow a wear: they were
  redone 1.5 s after it, before the outfit's links had landed; now they follow what's worn as it changes.
- **Login**: Grid and Start at were editable combo boxes, read as a combo box and then an unnamed "edit, selected
  last"; now plain lists, with a box for another grid or place. Password said "Alt+U" (the labels' tab order), the
  first focus landed on Grid before moving, and Log in, disabled while logging in, threw the focus to Grid, read over
  the progress: all fixed, and a Show password check box added (the user wondered whether people want to hear what
  they type; NVDA says "star" per character in a password field, as it should). Password's focus was announced.
- Smaller, done: "5 m" was read "five M" (now "5 metres" everywhere Access speaks or lists a distance); "F1 for keys"
  twice at login (once now); "No more notices." beside the list's own "No notices waiting." (one now); Shift+Tab from
  Say went to the area tabs (the history is one Shift+Tab back now, as in Radegast); Alt+F4, pressed for Alt+4, quit at
  once (it asks while in world now: Enter quits, Escape stays).
- Seen, left for a blind tester: silence after sending your own line (a quiet "sent" sound, or nothing? M4 made it an option, off); Ctrl+Tab
  into a conversation says "Conversations, tab control" before its input (the inner tab strip as context; the input's
  name says who); whether the Shift+Tab order matters at all to someone used to Radegast.
- **Retry, the same evening** (with NVDA speaking, logs in `local/access-retry/`): a script menu's Ctrl+N then 2 at
  once ("Green, done.") and Escape at once on a Delete confirmation ("Cancelled.") both count as her own keys now.
  Found: at the inventory's top level NVDA spoke the item below the highlight ("Calling Cards, not selected" on Body
  Parts); inside folders it was right.
- **That top-level misreading**: the tree view numbers its items for MSAA in the order they went in (children and
  placeholders too) and says the focus by that number; WinForms' own MSAA object (.NET 7 on) took the number for a
  position among the top-level nodes, so NVDA heard the folder at twice the highlight's place (each folder has one
  placeholder child); numbers past the top level's count fell through to Windows' object, right (inside folders, and
  the whole top level once the tree was refilled, so it came and went). Now the trees answer MSAA
  by Windows' own tree view object, as native ones do (`ReaderTree`; UI Automation keeps WinForms'), which should also
  give top-level items NVDA's "x of y". The hidden-desktop test checks what an MSAA reader is told while arrowing:
  the tree as first shown, after F5, and inside a folder. And Delete on a worn body part asked to confirm (then the op would
  have refused): now refused at once, as the official viewer and Firestorm do (replace it first); other worn items are
  taken off first, the confirmation saying so.

### On Second Life itself (the user, sighted, with NVDA, 2026-10-29)

Access's first run on the main grid, at Firestorm Social Island. Worked: login, people, objects, parts of real mesh, the
inventory, describing and walk mode. Found, and done (Claude Opus 5.5):
- **The parcel's music drowned NVDA** until turned down to 0.15. The screen reader is the person's main channel, so
  Access now starts the music at 0.15 and the world's sounds at 0.5 of the viewer's levels (the VR client's stay at 1),
  set once in the session's settings and only where they were still the old default, so a choice already made (the
  user's 0.15) is kept. Alt+0's volume rows had never stepped at all (the values come as floats, which the row didn't
  read as numbers); they do now, finer near the bottom (0, 0.15, 0.25 ...), and Shift+Enter steps down instead of
  round by way of 2. The "Now playing" titles stay on by default, since the blind tester asked for them (above); `//status music`
  says the song on asking, and Alt+0 turns the titles off. Whether they're too chatty is a question for her (below).
- **Objects (Alt+7) said "Objects nearby, table" and no item until Down**, where People (Alt+3) read its first person.
  On the hidden desktop, WinForms raised UI Automation's focus event for items in lists without the focus whenever
  they filled: arriving in Objects, the parts and faces lists under it filled just after it and had the last word
  ("Faces need the view ..."), and Friends and My groups did the same every 3 s while in People. NVDA calls the list a
  table, UI Automation's word for it, so these are the events it follows; that they're what silenced it is our reading,
  for the next NVDA run to confirm. Now only the list with the focus announces its item (`ReaderList`), a list arrived
  in before it fills says "looking" (Second Life's objects wait up to 3 s for names), and the landmarks tree and the
  inventory have a line to land on while they load. The test checks the last focus event after Alt+3 and Alt+7 by
  both UI Automation and MSAA, with the first fills held back as on Second Life.

### The release as a tester gets it, on Second Life (the user, sighted, with NVDA, 2026-10-30)

0.1.0.510 downloaded from the front door's README, unzipped without Unblock, run on SL at Firestorm Social Island,
guided by Companion. Worked: SmartScreen's Run anyway, no Firewall question at all, the login form's focus in Username
with Second Life chosen, `//about`, People, Objects, Control+D's absence, walk mode and the sonar, the music under NVDA,
a clean quit with nothing written beside the program. Unseen mode's first run on SL there: 965 MB working set and 312 MB
of video memory (1.7 GB and 1.9 GB at the same place the night before), 11 MB of textures resident (sculpt maps).
Found, and done (Claude Opus 5.5):
- **The front page and release notes were too much text**: people skip to the download button. The front door now
  leads with three steps to the newest build (`releases/latest`), the rest below; release notes are three short lines
  (`tools/publish/access/RELEASES.md` says so).
- **NVDA read SmartScreen's Run anyway button as just "R"**: the front door and README.txt say so.
- **The user's own Firestorm LSL bridge's line (its URL and auth key) went into the session log**, the one testers are
  invited to share: the console printer listened to chat apart from the core's filter. Both use
  `AgentSession.IsBridgeTalk` now. It was never read out.
- Open: the arrival line ("Logged in, in ...") wasn't remembered among the notices that came with it. It's an
  answer that cuts in, so the later arrivals can't cut it; NVDA's own reading of the window and the Say box at the
  same moment may. The next NVDA run with speech logged says which.
- Seen, for a blind tester: a sighted gamer reached for WASD in walk mode, where A and S are taken (what's ahead, the
  sonar); walk mode itself didn't make sense to a sighted person, as expected.

### Her first session in Access, on Second Life (the blind tester, 2026-10-30)

0.1.0.531 on her own PC with NVDA, from ~21:20 EDT, Tessel and her partner beside her in world, reported live on the helper
channel (our words, her quotes where Tessel gave them). Her summary: the walking is interesting even if she has to
pause and take her bearings, she looks forward to a sonar she can use, and the reading works nicely now.
- **Liked**: the direction said while turning in walk mode ("OMG! I love that it told me..."), the detail in what's
  read ("Way more detail!"), `//go` ("even better for //go"), Control+[ review, the music's level, the welcome mat that
  talked as she crossed it, IMs read out. Alt+3's clock directions and distances were right (Tessel checked against
  the map).
- **Chat wasn't read while she typed**: NVDA's "Speech interrupt for typed characters" cut each arrival off; with it
  off NVDA reads arrivals while she types. Since (branch fix/arrivals-while-typing, merged): a line cut off by typing
  is said again after a 700 ms pause.
- **She wants her own lines read back** (Radegast does): now on by default, Alt+0 "Read my own lines".
- **Her PC can't run the view out of sight**, so walk mode had no sonar, warnings or openings. HP Pavilion 690-0028:
  Ryzen 5 2400G, 8 GB RAM, Windows 10, Radeon RX 550 2 GB (Polaris). The renderer asks for Vulkan 1.3; an old OEM
  driver may offer only 1.2. Wanted: a sonar and warnings that need no GPU at all ("Oh, bless the builders"). Since
  (branch feat/sonar): every walk-mode ray comes from a collision model without a GPU (`docs/access-app.md` > Walk
  mode > The collision model); the renderer's log line now names the devices found and their Vulkan versions.
- **How far does a tap walk?** (~0.9 m by Tessel's tracking): without the sonar she can't tell how far she went.
  Wanted: the distance walked, spoken when she pauses. Since (branch feat/people): said when the keys rest ("0.9
  metres."), M for the last walk, the count and where it began (`docs/access-app.md` > Walk mode).
- **People a Tab from chat, with distances**, as Radegast has it beside chat; Alt+3 works but isn't where her habit
  reaches. Since (feat/people): People nearby beside the chat history, a Tab from Say (`docs/access-app.md` > People
  beside chat).
- **Her friends list on Alt+2**, as in Radegast (said after the session, having gone back to Radegast to check): Alt+2
  was Conversations. Since (feat/people): Alt+2 goes to Friends; conversations are Control+Tab's (and View >
  Conversations).

### A review against screen-reader and Radegast conventions (2026-10-30)

An independent review (Claude Opus 5.5, reading the code beside Radegast's source and NVDA's, JAWS's and Microsoft's
keyboard references), asked for because the only hands-on tester so far is sighted. Fixed before 0.1.0.529: F1 as a
window to arrow through, the tab strips out of the Tab order, Control+Backspace deleting a word, Control+Shift+H home,
minus and equals for a script menu's 11th and 12th buttons, a last "12 m". Already right: no keys NVDA or JAWS
reserve, histories never in browse mode, answers cut in and arrivals queue, Radegast's main keys kept. Done after
0.1.0.531 (Claude Opus 5.5; `docs/access-app.md` > The menu bar, Keys): a menu bar (Alt or F10; World, View, Read-out,
Help) with every item's key shown, set home, landmark here, sit or stand, fly, Away and Do not disturb among them, and
the keys on the context menus too; Control+Shift+1 region info; Control+1 to 9 the last nine messages; Escape from
Notices back where Control+N came from, and a notice's keys on its Applications key; Alt+0's rows leading with what
they're about, Left and Right stepping them; Alt+Shift+4 the second pane, Alt+4 again reading the inventory again; a
click as push-to-talk opens and closes; the no-conversations line keeping the focus when an IM arrives; Control+Page Up
and Down as the Control+Tab ring; the status bar checked as a StatusBar holding the location. What only a real screen
reader can confirm (NVDA saying the menus' keys, Alt alone and F10, NVDA+End) is in the guided test. Left:
- Maybe a tiny NVDA app module for the old add-on's NVDA+Page Up and Down.
- JAWS once by hand: UI Automation notifications' queueing there is undocumented, and Control+Up/Down are its
  paragraph keys (a recalled line may be read twice).
Added to the questions below: what's read out while Access is in the background (asked first).

### Questions for a blind tester

- First: while Sojourn Access is in the background, should she hear everything, only IMs and menus, or nothing (a
  setting either way)? TWBlue keeps reading off by default as too chatty and relies on sounds; Mudlet reads.
- F6 mutes the read-out here (the old add-on's key), but in Windows F6 moves between panes: keep it only if she used
  the add-on's key. Enter on a worn item takes it off: is one stray Enter too easy?
- What "edit notecards without going back two versions" means (her list): the Radegast agents couldn't reproduce
  losing versions in 2.54 (save, reopen, relog all kept the latest).
- Cost (through Tessel, 2026-10-30): reading that an AI describer comes later, she said it would be fun to test but she doubts she could pay for it. Decided the same evening: the studio covers her use (the user, 2026-10-30); how is ours to build (Access's assistant in a packaged build, then a way in that doesn't need her own key): a studio relay (a Cloudflare Worker holding the studio's Anthropic key as a secret, a personal token for her, usage capped, no content kept). The key is scoped to the studio's "Sojourn relay" workspace in the Anthropic console (so it can't use the Admin API; the workspace carries the spend limit; paid from the plan's monthly API credit, otherwise unused), kept in local/secrets/studio/relay-anthropic.key, DPAPI for the user's Windows account, put there by the user without passing through a session (checked against the API 2026-10-30). The relay caps each person too ($50 a month to start).
- Audio sides (her partner, through Tessel, 2026-10-30): Radegast had left and right reversed for over ten years; in hers tonight a stream to her west sounded on her right while facing south, which is right. Worth a check that ours is too (tools/audio.py's tests, and her ears once she tries walk mode's sonar).
- Her Radegast habit (through Tessel, 2026-10-30): Alt+5 to teleport around by SLurls, with mixed success there. Ours: Alt+5 is Search (places teleport from it), a SLurl on the clipboard is Control+G, typed is //tp. Is that close enough, and what fails for her in Radegast's?
- Naming her in Radegast issues and PRs: yes, by her SL name, as someone who asked for chat read-outs (her answer through Tessel, 2026-10-30). Sojourn's own public repo still names no private individuals.
- For Sojourn Access's first slice (`docs/access-app.md`), once she's tried it or heard it described: is a clock face
  ("two o'clock") right for where people and things are, or left/right and ahead, or degrees? Should a conversation's
  lines drop its name while she's in it (as now) or always say it? Are the notices' keys read in the right order and
  amount ("Keys: 1 Red, 2 Green, 3 Blue, 4 Block.")?
- When a notice is cut off by a key she presses, it's said again once she pauses: right, or does the
  sound a menu or question now makes on arriving (M4, on by default) make that repeat unneeded?
- The parcel's music: "Now playing: ..." is read at each new song by default (her ask): wanted every time, or rather
  only on asking (`//status music`)? And does the music at 0.15 of the viewer's level sit well under NVDA for her?
- For the inventory: is Space marking items ("..., marked" in the item's words, "3 marked") a good way to pick many, or would
  she rather have Shift+arrows or Ctrl+Space as in Windows lists? When she gives or deletes something, the
  confirmation comes up selected in Notices with the focus on it, then the focus goes back: right, or should Enter and
  Escape answer it wherever she is? For her fishing catch: is cut and paste into a folder what she does in bulk, or giving
  many at once (each give asks separately now)?
- For routes: do her notecard routes have anything but `//go` lines and words (`//pause`, `//sit`, teleports), and
  should a step that doesn't get within 2.5 m pause the route (as now) or go on?
- For walk mode (once she's tried it or heard it described; `docs/access-app.md` > Walk mode): F3 into it and Escape
  out, or would she rather the arrows walk whenever the Say box is empty? Is a tap of Left or Right turning 22.5
  degrees (the compass's sixteen points) right, or finer, or 45 as in Shades of Doom? Should H say degrees as Second
  Life counts them (0 east, counter-clockwise, as `//face` takes them) or as a compass does (0 north, clockwise)?
  Which cues does she want on by default, and are any too chatty (the warning before a bump, openings passed, the
  surface underfoot said when it changes, which is off by default)? Do the sounds tell her what they mean without the
  words: footsteps by surface, the warning's two tones, a door's knocks, the opening's chime, the sonar's pings going
  round from ahead to the right? Would she rather the sonar said every hour of the clock, or only walls and openings
  (as now)?
- For the earcons (M4; `docs/access-app.md` > Earcons): which kinds would she want a sound for, and which not (on now:
  IM, a mention, a script menu or question, an offer)? Can she tell them apart without the words? Should F6 silence
  them too (now it mutes only the words, so something arriving is still noticed), and is a sound for her own line sent
  wanted (off now; the guided run asked)? Access replaces Second Life's own new-IM chime with its IM bell: does she know
  and miss the old one?
- For the beacon and the marks: is a pip from where a walk is heading useful, or noise on top of the footsteps (on,
  quiet, now)? Walking to a beacon by hand: can she tell ahead from behind (it brightens within 20 degrees ahead)? Is
  O's list (doors, seats, touchable things within 12 m, sounded one by one) the right set and range?
- For following and driving: should following say less (or more) as it goes? When driving, which feedback matters:
  speed changes, heading, what's ahead, and how far ahead?
- Does she use a braille display's keyboard, dictation, or any input other than a physical keyboard, for answering
  things (confirmations, script menus, offers)? Sojourn Access takes those answers only from a physical key, or with a
  setting from Windows Hello (face, fingerprint or PIN) instead (Answers without a physical key, above); would that
  suit her, and does she have Windows Hello set up?
- For describing (once she's tried it; `docs/access-app.md` > Describing): Ctrl+D for what's selected (or where she is)
  and Ctrl+Shift+D for herself, the same key again for more: does that sit well, or would she rather a key for "more"
  of its own? Is the brief one the right length (a few sentences), and the rich one? For outfits, is going garment by
  garment (cut, colour, material, what's printed on it and where) what she wants, or would she rather the overall look
  first and details on asking? How much about faces and bodies? When a place is dark, pictures of people and things are
  lit as at midday for true colours and the description says it's night there: would she want that, or the place's own
  light? Would an overview on arriving somewhere new be welcome as an option, or only ever on her key? Without a Claude
  account, is hearing what the lists know (names, an object's own description, what's worn by name) any use?
- For parts and faces (M2): is "Part 2 of 4, a box above the root, touchable" the right amount (what, where, whether it
  takes touches), and is "the root" a word she uses? Are parts and faces as lists under the objects list (Tab, then
  Tab) easy to reach, or would she rather a key on an object? Faces by direction from where she stands ("the side
  facing you", "the left side") or by the object's own sides, or numbers only? Did the sunken city's chests have
  anything we haven't modelled (a part that only works when touched on one face, a hidden part)?
- For placing by words: what does she say when she places something ("a metre to my left", "in front of me", "on the
  table")? Is resting it on whatever's under the spot right, or should it stay at the height said?
- For the second pane: Alt+Shift+4 to open it and go there, Alt+4 back to the first, Control+M to move what's marked: does that sit
  well next to how she moves her fishing catch today, or would she rather a separate window as she asked?
- For what she's wearing (Ctrl+Shift+W): is the order right (clothing, attachments, HUDs, body), and does she still
  want her "**Wearing:" HUD labels once this exists, or would a line for the outfit folder worn be better?
- Incognito (Ctrl+Shift+I): would she use it at all, and is a key that turns it on easily but off only by a physical
  press the right balance for her?
- Voice: does she use voice chat? Is F4 held a good talk key (Scroll Lock, Pause and F12 are choices), and would she
  want who starts speaking said by itself (off now; Ctrl+Shift+V says it on asking)?
