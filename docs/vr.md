# The headset (phases 2 and 4)

Written by Claude Opus 5.5, 2026-10-20 and 21. Agent-facing.

## Running it

```
python tools/grid.py start
python tools/client.py vr                 # = run --vr --shadow-cap 1536; add --logs to keep frames.csv and vr.csv
python tools/client.py vr-sim             # the same against Meta's XR Simulator, no headset needed
python tools/vrlog.py                     # summarise local/client/frames.csv and vr.csv after a --logs run
```

The Quest 3 runs as PCVR over Link or Air Link: Meta Horizon Link on the PC, the headset connected before
launching. The system's OpenXR runtime is Meta's (`HKLM\SOFTWARE\Khronos\OpenXR\1` `ActiveRuntime` =
`C:\Program Files\Oculus\Support\oculus-runtime\oculus_openxr_64.json`, checked 2026-10-20; read it from PowerShell,
Git Bash mangles `reg query /v`). SteamVR is installed too but isn't the active runtime. Without a runtime, a running
service or a headset, `--vr` logs why and carries on on the desktop (no headset: `ErrorFormFactorUnavailable`,
immediate; a runtime that doesn't answer is given `--xr-wait` seconds, 20). The headset has to be there at launch:
the Vulkan device comes from the runtime, so switching into VR later would mean a new renderer. If the session ends
(quit from the Meta menu, Link drops), the client carries on on the desktop.

Options (`ClientOptions.cs`): `--vr`, `--xr-scale F|auto` (auto: dynamic resolution, below; a number fixes the eyes
at that scale of the runtime's recommended size; without it the settings' resolution holds, full size by default), `--hz N` (asked for through
XR_FB_display_refresh_rate when offered; 72; Link offered only 90), `--xr-wait S`, `--validate` (also offers the
Khronos OpenXR core validation layer from `local/openxr-sdk`). `--quest-eyes` is desktop stereo with a Quest 3's
asymmetric frusta (approximate), to measure the headset's path with `--bench`.

Settings (`Control/VrSettings.cs`) live in `local/client/vr-settings.json` (per machine), changed in the headset's
settings menu and saved at once. Command-line options apply over them for a session and are kept if the menu then
saves: `--seated`/`--standing`, `--snap DEG` (snap turning at that angle), `--smooth-turn`, `--turn-speed DEG/s`,
`--hand-relative`, `--no-body`, `--assistant-model quick|sonnet|opus`. Defaults: standing, your own eye height, smooth
turning at 180 deg/s, moving where you look, your body shown, the interface shown, the assistant on quick,
putting you back by teleport, pointing to teleport on, the eyes at full size and 128 m draw distance (the user's
request, 2026-10-26: full resolution and 128 m rather than "auto"; a settings file saved before keeps its own). The
assistant's model is set on the desktop too (`docs/assistant.md` > Claude), so it lives here even without a headset. Tests that run the client against `local/client` write that file
too: delete it afterwards.

## Controls

Touch controllers (`Xr/XrInput.cs`, bound for `/interaction_profiles/oculus/touch_controller`):

| | left | right |
|---|---|---|
| stick | walk (where you look, or where the left hand points) | turn: smooth at 180 deg/s at full tilt, proportional past a 0.15 dead zone; or snap; up jumps; up/down climbs or sinks while flying; with the right ray on something that scrolls or pages, scrolls it instead (below) |
| stick click | toggles running | open mic: mutes and unmutes voice chat (`docs/voice.md`) |
| X / A | X hides the whole interface, and brings it back; held 0.6 s, every menu back to following in front, shown (`docs/assistant.md` > The panel > Where it sits) | A: a quick press turns the pointer on and off, held it shows only while held (below) |
| Y / B | Y recentres (the avatar under you, facing the room's way; seated, also retakes the seated height); held 0.6 s, looks around as a free camera (below), and Y then brings you back | B toggles flying (looking around: flying or walking on floors) |
| menu (≡) / system | the settings menu (on release); held 0.8 s, the way out: stand up and go home (`Control/WayOut.cs`; Ctrl+Shift+H on the desktop) | the system's own |
| grip | hold to talk in voice chat (push-to-talk) | hold to talk to the assistant |
| both grips | held together: dictate words for people (a draft on the panel to send or cancel; `docs/assistant.md` > Incognito) | |
| trigger | held on open ground, teleports you there (below; touching and sitting are the pointer's) | with the pointer: touches or sits on what it points at, presses keys on the panel, menu, feed and boards; held on the panel's body, drags it somewhere (`docs/assistant.md` > The panel) |

- X hides everything of ours and brings it back as it was (`InterfaceHidden`, saved; the user's ask, 2026-10-28: to
  check the mic or anything else, show it again). Hidden: the panel, the settings menu, the chat feed and its notices,
  the assistant's cards and labels (in view, at the wrist, in the room, in the world), the forearm tokens (mic, HUD,
  bell, incognito, stand), the pointer's beam and dots, name tags with their speaking and typing marks, voice's dots,
  the HUD board, page boards, the reader, the keyboard, the aiming crosshair. What was open stays open out of sight;
  shown again, the panel comes up with what waited (a panel put somewhere now out of view comes up in front).
  - Still shown: a confirmation, with the ray to press it (the safety gate: it holds every other op); the world's own
    things (objects' floating text, media on faces); the comfort vignette if it's on; pointing to teleport's arc and
    ring, which show only while the left trigger is held for it (a teleport needs its spot seen).
  - Still working: moving, turning, jumping, flying, Y, the way out (menu held), the right grip to the assistant (its
    replies, choices, requests to point or type and dictated drafts wait on the hidden panel; nothing pops up),
    voice's push-to-talk and mute, both grips' dictation, A (the pointer's setting, used once shown), aiming's shots.
  - Not taken: nothing hidden takes a ray or a press (the pointer isn't live, so the right trigger neither touches
    nor sits nor presses; the right stick turns). The menu button brings the interface back with the menu open.
  - Tab does the same on the desktop (the HUDs there are the viewer's and stay); Enter, F2 bring it back (Enter
    doesn't type or send a draft out of sight), 1-9 pick nothing. Also the `interface` setting.
- The settings menu (`Control/SettingsMenu.cs`, drawn by `Assistant/PanelMenu.cs` in the panel's look on the panel's
  quad layer, placed in front of you when it opens, `docs/assistant.md` > The panel): moving has posture
  (standing/seated), eye height (your own/the avatar's; only standing), turning (smooth/snap), turn speed (60-360
  deg/s) or snap angle (15-90), moving toward (where you look/left hand), movement (predicted/server: The rig > Movement "server"), putting
  you back (teleport/jump/slide: The rig, below), point to teleport, look around, your body; the other sections, and
  the groups that keep long ones to a page, are listed in `docs/assistant.md` > The panel > The settings menu. Every
  row is also `SojournSettings` on the agent API (`docs/client.md`). Segments, switches and swatches, pointed at and
  pressed with the right trigger; × or the menu button again closes it; ? shows the controls nothing on screen shows.
  While it's open the assistant's panel waits; a confirmation closes the menu and takes the layer.
- The camera (`Control/Shutter.cs`, batch M 2026-10-29): menu > you > camera, or the assistant (`photo_camera`), takes
  it out and puts it away (no forearm token since 2026-10-29). Out, the two hands held up apart in front of you hold
  the picture as a panel between them (a rectangle in the room, its bottom left just off the left
  palm, its top right just off the right), drawn there with a live preview of the picture on it, and only
  while they frame is the right trigger the shutter (unless the ray is on a menu, board, key or page; touching waits
  meanwhile); otherwise the trigger is as ever. `photo_ratio` locks a shape (free by default). X hides the panel's
  outline and preview, not the camera. The preview renders only while the hands frame (`docs/rendering.md` > The
  camera's preview).
  `docs/assistant.md` > The panel > Pictures by hand.
- The headset's own recentre (hold the Meta button) is the same as Y.
- The pointer (`Control/HandPointer.cs`; Claude Opus 5.5, 2026-10-26, from the user's two ideas after their main-grid
  session: a pointer with an on/off toggle that decides whether menus can be clicked, since most of the time what's
  pointed at doesn't matter; or no pointer by default, the assistant asking them to point when it can't tell what they
  mean, with the interaction pointer on the right hand and its own toggle). Built as one: a single pointer on the right
  hand, off by default. A, pressed quickly, turns it on and off (saved; held, it shows only while held, the toggle untouched; the menu's moving section and the `pointer` setting too). On: its
  beam shows, the amber dot marks what it's on ("that" for the assistant, also while talking), its trigger touches and
  sits (below), presses the panel, the settings menu, the chat feed and the boards. Off: nothing is pointed at and none
  of those can be pressed, except what comes up for the person and needs the ray, which brings it up by itself: a
  confirmation (always pressable; the arming rules are unchanged and a trigger already held then isn't a press), the
  settings menu while it's open, and the assistant asking them to point (`docs/assistant.md` > How it fits together;
  the trigger on something then picks it for the assistant). Why one pointer rather than either idea alone: a toggle
  alone keeps the pointer as the way "that" reaches the assistant, so it would be on most of the time; asking alone
  needs a second pointer for touching and menus anyway, and loses pointing while you speak. Together, the pointer stays
  off until it's wanted, the assistant goes by words and what's in the middle of the view (most of the time enough, as
  the user expected), and pointing still works the moment it's on. A, not a new button: jumping is rare in SL and the
  right stick's up was free while walking (flying, it still climbs). The left hand points at nothing now, so its
  trigger is free.
- Beams in the look's light (2026-10-28, the user found the near-white beams read grey next to the glowing panel): the
  pointer's ray, the teleport arc, the crosshair, the panel's drag outline and the boards', keyboard's, reader's and
  world touch's cues take `PanelLook.Light` (the look's first sheen colour, lifted toward white: white in glass and
  silver, rose in pearl, pale blue in smoke) and are drawn more opaque (the ray 0.5 -> 0.75, the others 0.3-0.35 ->
  0.55). The dot on what the pointer is on, the crosshair's held shot (amber) and the hands check keep their colours.
- The pointer's trigger (user, 2026-10-25: the person's own hand, an exception to acting through the assistant, as the
  menu and confirmations are; `Control/DirectTouch.cs`, given the right hand by `HandPointer.World`, written for the
  left): what the viewer's left click does, while the pointer's on and its ray is out in the world. An object whose click action is
  sit seats you on it (seated, another one moves you there; the viewer would have you stand first); anything that takes
  touches is touched where the ray lands on it, held while the trigger is (a drag across it updates the touch);
  pay, buy, open and media click actions do nothing here. Each pull is also the left mouse button for scripts, and only
  that when a script has taken it. Pointed at something it would act on, the beam ends in a pale dot (touch) or ring
  (sit) beside the amber one. Nothing while a confirmation shows, the menu is open or the assistant asked them to point
  (a pull that began then, or on the panel, waits for a release), and the ray stops at the assistant's panel and the
  feed. Not gated: the touch confirmation guards the assistant's touches, not the person's.
- HUDs (`docs/hud.md`): while any are worn, a small token with a screen glyph sits on the left forearm beside voice's
  mic token. The right ray and trigger on it bring up the HUD board (the HUD screen in the viewer's 16:9 layout, 60
  degrees wide, 1.25 m ahead) or put it away; so does × on the board, or the assistant. The pointer's ray and trigger
  press what's on it, as in the world; on an empty part they drag the board.
- Incognito (`docs/assistant.md` > Incognito): while it's on, a token lit in the accent with its mark (a hat and
  glasses; open lenses when the assistant may see) sits on the left forearm past the other tokens; the right ray and
  trigger on it end incognito. The menu's incognito section turns it on and off, holds "let the assistant see" and its automatic
  triggers. The window on the PC shows a dark fill instead of the eyes while it lasts.
- Notices (`docs/social.md` > Notices): while any wait for an answer, a bell on the left forearm past the HUD token and
  the stand key, lit while some aren't on the chat feed; the right ray and trigger on it put them all back on the feed,
  where their keys answer them and × puts one away (the pointer's on for that).
- The reader (`docs/assistant.md` > The reader): the right ray and trigger drag its text to scroll, drag its title
  strip to move it, × closes it; with the pointer on or off.
- Scrolling with the stick (`Control/SheetStick.cs`; the user's idea from the headset, 2026-10-27: pointing at a menu
  that can scroll, the right stick scrolls it). While the right ray is on something that scrolls or pages now, the
  right stick is its: the reader, a long answer's pages on the panel, the settings menu's pages, the chat feed when it
  holds more than it shows, a page up close, a page on a HUD on the HUD board, a page on a face out in the world (the
  ray as each already takes it: the reader with the pointer off too, the rest with it on); on either map it zooms
  (up in, smoothly, 1.8 halvings of scale a second at full tilt; `docs/map.md`). Where it scrolls, smoothly,
  faster the further it's pushed (past a 0.15 dead zone, to 1.8 of its views a second at full tilt); where it pages, a
  page per flick (past 0.6, again once back under 0.3), repeating while held (after 0.45 s, every 0.25 s). Up goes
  toward the top or the page before, as a mouse wheel rolled away does; down further on or the next page. Left and
  right page too where there are ‹ › keys, and scroll a web page sideways; on the reader and the feed they do nothing.
  Meanwhile the client sees the stick centred: no turning, no jump, no climbing, no E/C while seated. A push belongs
  to where it began until the stick is back in the middle: one begun on a sheet keeps scrolling it if the ray slips
  off and never turns you; one begun elsewhere (turning) keeps turning when the ray comes onto a sheet. Something that
  fits (a short answer, a menu section on one page, a short notecard) doesn't take the stick, so it turns as ever. No
  cue of its own: the reader's and the feed's bars and the page dots already say a sheet goes on. The desktop's wheel
  scrolls the feed as it does the reader.
- Media (`docs/media.md`): the pointer's trigger on a page taps it, a drag scrolls it, a still hold presses and drags;
  on a video it pauses or plays; on media that waits it starts it. A double tap brings it up close on a board (the
  pointer uses it, × puts it away, the trigger on the strip drags it); the HUD board stands aside meanwhile.
- The keyboard (`Control/HeadsetKeyboard.cs`; the user's decision, 2026-10-26: optional, easy to put away, and only
  when the person asks for it: it never comes up by itself, not even on focusing a text field). Called up by a keyboard
  key: on the page board's strip while the page has a text field focused (the key appears, never the keyboard), or on
  the assistant's panel (idle, or beside × on its card) for a typed request. The one exception (the user, 2026-10-27):
  when the assistant asks the person to check a name it can't match by typing, the keyboard comes up by itself with its
  guess already typed, so ↵ accepts it (`docs/assistant.md` > Checking a word by typing). It comes up in front and low, like a desk
  keyboard (0.55 m away, 36 degrees below the eyes, tilted to face you, 0.5 m wide) and follows the head as every menu does
  (carried past 35 degrees; its strip drags it somewhere: `docs/assistant.md` > The panel > Where it sits). Either hand's
  ray and trigger type (a key fires on the pull; ⌫ held repeats); shift once for a capital, twice for caps lock; 123 /
  abc switch to symbols and back; ↵ is Enter (a page's, or sends the request, which also puts the keyboard away); × or
  the same keyboard key puts it away. A page gets each key as the desktop's keyboard sends it, nothing kept or logged
  (so a password the person types goes only to the page; the assistant never types into one, and reads fields without
  their values); a request shows on the panel as it's typed, as on the desktop. While a confirmation waits or the menu
  is open it's hidden and takes no ray (a trigger held across that waits for a release), so typing can never reach a
  confirmation. A ray on it is its own: the boards, the panel and the world don't see that hand. Chat lines go through
  the assistant ("say ..."), as before. `python tools/keyboard.py` checks both targets in the simulator.
- Seated, or while a script keeps movement controls (`Control/VrPilot.Seated.cs`): the sticks are the viewer's keys
  for vehicles, horses, boats and games. The left stick forward/back is W/S (CONTROL_FWD/BACK), left/right A/D
  (CONTROL_ROT_LEFT/RIGHT), the right stick up/down or A is E/C (CONTROL_UP/DOWN); the right stick still turns you, and
  the view turns with what you sit on and rides it where it's drawn, carried on between the region's updates
  (Moving things, below).
- Standing up (the session of 2026-10-26: seated in a chair, the person found no way to stand but asking the
  assistant): while seated, a small key with an up arrow sits on the left forearm past the HUD token
  (`Control/StandKey.cs`); the right ray and trigger on it stand you up, on anything, as the viewer's Stand button.
  On something that hasn't taken the movement keys (a chair, a windowsill, a pose ball), pushing the left stick most
  of the way for a third of a second stands you up too, and that push doesn't walk until it's let go. Vehicles and
  games keep the stick (they read it as keys): the key, the way out (menu held) or the assistant stand you up there.
- Pointing to teleport (`Control/PointTeleport.cs`; the user's wish, 2026-10-26: the left trigger, free since the
  pointer moved right, could also teleport you to where you point). The left trigger pulled shows a beam from the left
  hand; on open ground (terrain, or the top of anything you could stand on, scripted or not, that
  nobody wears) a ring fills on the spot over 0.6 s; letting go once it's full teleports you there. A shorter pull, or
  one anywhere else, does nothing, so it never jumps you by accident and never competes with touching and sitting.
  Not while a script has taken the left button, a confirmation waits or the menu is open. It's the viewer's
  double-click teleport (TeleportLocationRequest, into a neighbouring region too), landing rules included: a parcel's
  landing point still wins (TPV policy 2.k: nothing the official viewer can't do). Seated, it stands you first.
  `point_teleport` (menu: point to teleport) turns it off.
- Looking around (`Control/VrPilot.Spectate.cs`; the user's request, 2026-10-26: a spectator mode where the real
  avatar stays put and you move a local "ghost" to look around a place, as the viewer's free camera does). Y held
  0.6 s, the menu's "look around", or the assistant (`look_around`, not kept between sessions) starts it; Y, the same
  switch, or the avatar going elsewhere (a teleport, a sit or stand) ends it, putting you back on the avatar (a
  straight cut), facing the way the avatar faces (the user, 2026-10-28). It's a free camera (the user, 2026-10-26: fly and noclip): you start flying, through anything, the ground
  included; the left stick goes where you look (or where the left hand points, with that setting), up and down too,
  sideways staying level, at 6 m/s (15 with the stick's click); the right stick up or A climbs and down sinks. B lands
  you to walk on whatever floor is under you (terrain or a prim's top; through walls still) and back. Turning is as
  ever. Your avatar stands where it is, drawn whole as others see it,
  and you have no body of your own (no hands; the pointer still works). Nothing reconciles: no follow walk, no put
  back, no riding along. You stay within 256 m of the avatar (the viewer's `DisableCameraConstraints` lifts its 50 m
  limit the same way). The camera goes to the region as the viewer's free camera's
  does, in every AgentUpdate: the region's interest list follows it, so what's around where you look loads, which is
  the point of looking around; scripts read the camera only with permission (PERMISSION_TRACK_CAMERA), as with the
  viewer. Voice already hears at the eyes and speaks from the avatar, the listener tethered within the same 50 m
  (`docs/voice.md`): the viewer's "hear from camera position". World sounds and media are heard where you are. TPV
  policy: the viewer's alt-cam and flycam do all this, and others see nothing change (2.k). Pointing to teleport while
  looking around takes the avatar there and ends it. Checked in the simulator: walking 3.8 m off, turning back and
  flying up showed the avatar standing whole where it was; Y put the view back on it.
- Aiming (`Control/VrPilot.Aim.cs`, `Control/Aim.cs`; batch B1, `docs/safety.md` > Aiming): while a script holds the
  mouselook button (guns, combat HUDs, shooting games; `aim` auto, or on always, or off), the region is told the
  headset is in mouselook, the body and head turn to the aim (the head's forward, or the right hand's ray with
  `aim_with` hand), the right trigger is the mouselook button (not on a menu, board, key or the panel) and a crosshair
  sits where the aim lands; touching waits meanwhile, as in the viewer's mouselook. Menu > safety.
- Comfort (`docs/safety.md`; comfort aids are options, off by default, the user, 2026-10-27): the comfort vignette,
  once turned on, darkens the view's edges while something else moves you (`comfort_vignette` forced: a push, a seat
  or vehicle, a moving floor; all: the stick's walk and smooth turns too), a layer locked to the head under the menus; keep me in place (`keep_in_place`) holds the room still when you're pushed and the
  walk to the head brings the avatar back; the way out (menu held) also releases controls a script took. Menu > safety.
- In the mirror window: WASD/arrows, Q/E turn, Space, C, F, R recentres, Tab the panel, F8 the assistant's model; F3,
  F6, F12 as on the desktop.

## How it fits together

- `Xr/XrRuntime.cs`: instance and system (XR_KHR_vulkan_enable2, XR_KHR_win32_convert_performance_counter_time,
  XR_EXT_local_floor, XR_FB_display_refresh_rate when offered). It's the renderer's `IVulkanCreator`
  (`VulkanSetup.Creator`): the runtime makes the Vulkan instance and device from our create infos and picks the GPU.
  Silk.NET's `CurrentInstance`/`CurrentDevice` are set by hand after that. The loader is the Khronos
  `openxr_loader.dll` 1.1.65, vendored in `src/Sojourn.Client/native/`.
- `Xr/XrSession.cs`: session state machine (begin at Ready, end at Stopping, back to the desktop at Exiting or loss),
  the play space (local-floor, else stage, else local with an assumed 1.6 m eye height), one swapchain of two layers
  (an eye each), the frame loop. Eyes are rendered at the swapchain's size and copied bit for bit
  (`vkCmdCopyImage`) from the renderer's R8G8B8A8_UNORM output, already gamma-encoded, into an R8G8B8A8_SRGB
  swapchain, so the compositor decodes them right. Swapchain images are in COLOR_ATTACHMENT_OPTIMAL when acquired and
  are handed back that way. It's an `IFrameTarget`; `Window/MirrorTarget.cs` adds the window (both eyes side by side,
  mailbox present, never vsync: the headset paces the loop with xrWaitFrame), the menus drawn over them (below).
- The mirror window shows the menus as the headset does (Claude Opus 5.5, 2026-10-27, the user's ask, so someone at
  the PC can follow along). The menus are quad layers the compositor lays over the lenses, so the window used to show
  the world without them. Now, once both eyes and their hands are recorded (`IFrameTarget.RecordFinish`),
  `Render/Realtime/LayerMirror.cs` draws every quad layer the frame shows (the panel or settings menu, the chat feed,
  cards, the HUD and page boards, the keyboard, the reader) over each eye's part of the window, its corners projected
  through that eye's pose and field of view as rendered, so it's mapped in perspective where the headset puts it; in
  EndFrame's order and parts (`XrSession.PlanMenus`, > Menus over menus; a part's clip applied in the draw), premultiplied. Then the hands'
  layer over them, read from its own swapchain image (sampled, sRGB-decoded and encoded again): a hand in front of a
  menu and the pointer's beam and dot over it show as in the headset; the pointer elsewhere is in the eyes already.
  The pictures are the layers' own copies with mips (`QuadLayer`'s `MirrorPicture`, RGBA8): made from the same staging
  only when a picture changes, a board's screen (the renderer's image) copied each frame it's up; about 1.33x the
  memory of what's up (the panel at most ~9 MB). The draw is a quad per layer per eye over the window's pixels.
  Incognito blanks the window as before (`Presenter.Blank`, read once per frame), and then nothing is copied or drawn
  for it. `mirror_menus` (menu > view > menus on desktop, on by default, saved) turns it off. A motion routine's
  `shot NAME` and F12 also save the window as it shows (`NAME-mirror.png`, `Presenter.CaptureNext`).
  Cost (2026-10-27, the XR Simulator, eyes at x0.8 of its Quest 3 size, a 1600x800 window, the settings menu up; F6's
  pass totals over 15 s with `mirror_menus` on, then 15 s off): the `mirror` pass 0.015 ms a frame, `present` (where
  the pictures' copies fall) 0.256 against 0.257 off, the whole frame 4.90 ms either way. Nothing worth a default
  change. (Another agent's test client shared the GPU then; these are the passes' own timestamps.)
- `Control/VrPilot.cs`: the rig (below). `Program.cs` `HeadsetFrame`, on the main thread: wait for the frame
  (`XrSession.WaitFrame`: xrWaitFrame, the eyes and controllers located at its predicted display time), dead-reckon to
  that time (`MotionSmoother.BeginFrame(displayAt)`), place the eyes, set the layers, then commit what the frame shows
  (`XrSession.Commit`: the quad layers' pictures and poses, the cards, blackout, depth, the panel on top) into its
  `XrFrame` and post it to the render thread, which begins it (xrBeginFrame), renders and ends it with the eye poses it
  was located with (the projection views are the frame's own). The canonical pipelined loop: the main thread's
  xrWaitFrame for the next frame returns once the render thread has begun this one, so simulating frame N+1 overlaps
  recording and submitting N. Three `XrFrame`s rotate (one rendered, one waiting, one simulated); quad layers keep a
  pending spec (main) and a frame's spec (render); the cards' layers are made and let go on the render thread. Before
  xrEndSession (Stopping) the main thread drains the render thread (`BeforeEnd`).
- Renderer side: `EyeSpec` carries a full eye pose relative to the camera (offset and rotation in its eye space) and
  asymmetric tangents. Headset eyes cull with one frustum, the centre camera with the widest tangents and its apex
  pulled back to contain both eyes. Shadow fitting, LOD and probes use a symmetric centre view as wide as both eyes
  (detail levels by the viewer's default 60 degree camera angle, not the lenses'). The near plane is 2 cm, so leaning
  in close doesn't cut things off (`docs/rendering.md` > The headset's near plane). The eyes render through a stereo
  target set: forward and alpha passes for both at once (multiview), the rest an eye at a time (`docs/rendering.md` >
  Two eyes); `XrSession.RecordDepth` copies the eye's layer.

### The rig: room, avatar, camera

Decided by the user (2026-10-25, after the second main-grid session found the view and walking jittery): the head and
the view never take a correction from the server (2026-10-26: walls the region stops the avatar at stop the view too,
below, and the fail-safe is a teleport). Sojourn is for talking and moving about, not combat, so accuracy
hardly matters and smoothness does: your own motion stays local and may drift from the region's as far as it needs
while you move; once you've stopped it's put right slowly and gently, and a big gap (a teleport, a sit, a push, a
crossing) is one clean move, a straight cut of the view, rather than a visible slide. Two more of the user's ideas,
both built: bring the server to you (walk the avatar to where you see yourself) where that works, and when it can't,
the person chooses how they're put back, a jump to the spot or a slide.

Cuts, not blinks (the user, 2026-10-28): every move of the view to a new spot at once (teleports, putting you back,
crossings' and seats' jumps, a teleport's height, the free camera's return, a drop of over a metre) is a straight cut
to the new spot, no black. Before, each was hidden by a 90 ms blink of black, which at 30-45 fps was a few solid black
frames each time: what the user had been seeing as black frames. A fade may come later as a comfort option, off by
default (comfort aids are options, Decisions in CLAUDE.md); not built. `VrPilot.TakeCut` says when one happened: the
console's `view cut: ...` line (flying or on foot, the avatar's speed), the pacing line's `cut N` (frames), vr.csv's
state `cut` (`blink` in older logs).

The avatar's position is the region's. The camera mustn't wait on it or follow it, so (`Control/VrPilot.cs`):
- The play space is placed in the region by an origin, a yaw and a floor height. The eyes are wherever the headset
  puts them in it; head tracking is the runtime's, local and immediate. Only what the person does moves the placement:
  the stick, turns, and the few clear things the region does (below).
- Stick locomotion walks the avatar with AgentUpdate controls relative to its body, which faces where the head looks
  (the user, 2026-10-26: walking backward it swung round and bumped into people): forward, back, sideways or
  diagonally (`AtPos`/`AtNeg`, `LeftPos`/`LeftNeg`, both for a diagonal; the follow walk the same with the nudge
  keys). The region moves an avatar only along those eight, so the body is turned up to 22.5 degrees off the head to
  make one of them exactly the stick's way (`VrPilot.Octant`, held until the head is 30 degrees past it); speeds are
  learned for each (OpenSim walks sideways at 1.9 m/s, the rest at 3.1). The camera's walk is the client's own (`VrPilot.Walk`): from the first frame the stick
  is held, the origin moves at the gait's speed (walking, running, flying; learned from the region's own velocity,
  3.2 m/s walking on the main grid), steadily. The region's positions never move it; they only slow it, by easing the
  speed (a quarter-second time constant), once the walk runs over 0.7 m ahead of where the region will have the avatar
  (its position a round trip old, carried on by its velocity since its last update: OpenSim sends one only when the
  velocity changes, SL every 60-110 ms), stopping it 1.5 m ahead; behind, it catches up at up to a third faster.
  Walls and steps stop it first (below). Across the walk it drifts back at most 0.5 m/s, only past a metre. Letting go stops it at
  once, and the region is told to stop the avatar there too (Braking, below). The main-grid session of 2026-10-25
  measured the round trip at ~100-190 ms and updates every 60-110 ms.
- Braking (`Control/VrPilot.Brake.cs`; the user's decision, 2026-10-27, after predicted mode threw them off an edge
  they had stopped at; Claude Opus 5.5): stopping is the region's, and the viewer's way to ask for it is
  AGENT_CONTROL_STOP, the flag the official viewer holds for its "stop moving" key and sets as its autopilot arrives
  (`stop_moving` in llviewerinput.cpp, LLAgent::autoPilot). From the frame the stick, or the follow walk to the head,
  lets go, STOP goes out with the movement keys clear, held until the region reports the avatar still (at least a
  round trip, at most a second; a crossing's arrival, which reports it still for a moment, doesn't count for 0.5 s),
  and dropped at once for the stick, a jump, a seat, controls a script keeps, or the avatar in the air (OpenSim turns
  a STOP raised mid-fall into hovering: `breaking` in ScenePresence.AddNewMovement). Both movement modes. Why it
  works on OpenSim: a released avatar there walks on for a quarter second before its physics brakes (ScenePresence's
  `m_delayedStop`); a STOP brakes it at once, and its animator keeps the current animation while STOP is held, so
  nobody sees a step. SL's simulator is closed, so its answer to STOP is for the headset to tell; LL's own autopilot
  relies on it to stop at its target. A reverse tap (the opposite keys for 0.1 s, then STOP) was tried for comparison
  (`SOJOURN_BRAKE=reverse`): it stopped the avatar 0.6 m short of the view and took longer (0.5 s), a step back
  others would see. `SOJOURN_BRAKE=off` sends nothing, as before; `SOJOURN_BRAKE_LOG=1` says how each brake ended.
  The follow walk doesn't start while the avatar is in the air (after walking off an edge it walked the avatar back
  over a region's border mid-fall). Measured in the simulator on the local grid (main-grid-like lag, ~24 fps,
  braketest in the session doc's checks), a 2.6-2.8 m/s walk let go on open floor, predicted mode: the avatar came to
  rest 0.93-0.97 m past the view's stop without braking (still after 0.64-0.67 s), 0.08-0.20 m short of it with STOP
  (0.21-0.28 s). Let go with the view 0.2 m from a 100 m drop: without braking the avatar walked off and fell; with
  it, it stood 0.18 m from the edge. Server mode: the avatar rested 1.55 m past where the view was at the release
  without braking, 0.4-0.6 m with it (the view a round trip behind it, above), and at the same edge fell without and
  stood with it. Flying at OpenSim's 10-13 m/s: it glided on 13.9 m without, 9.3 m with.
  Letting go of the climb while flying (up or down) does the same, and the view's height stops with it
  (`VrPilot.ClimbStop`, both modes): before, the drawing carried the climb on until the region's word arrived and the
  floor's spring, fed the climb rate, ran on past that, so after a 2 s climb at OpenSim's ~16 m/s the view rose 3-4 m
  over where the avatar stopped and blinked back down. Now from the release the floor is set straight to a height that
  slows evenly from the view's own height and speed to where the avatar is expected to stop (where the region last had
  it, carried on at its climb rate for a time learned per direction from where it stopped, 0.2-0.3 s here), then eases
  onto the region's height once it says the avatar is still (at most 1 m/s if it has to come back).
  `SOJOURN_CLIMB_STOP=0` turns it off. Measured (1.5 s climbs, then 1 s down; how far the view's floor went past where it
  settled): predicted up 1.73 -> 0.05 m, down 1.56 -> 0.40; server up 1.62 -> 0.23, down 1.03 -> 0.77 (going down,
  OpenSim's own avatar dips 1.2-1.4 m past and comes back, which the view follows); after a 2 s climb 0.03-0.04 m, no
  blinks.
- Once you've stopped, the gap between your head and the avatar is closed by walking the avatar to you: when it's
  over 0.5 m (standing, not flying, not sitting), the nudge walk (`NudgeAtPos`, the viewer's tapped key: ~1.9 m/s on
  OpenSim) takes it there, stopping short by the coast it has learned (avatars run on after a stop: ~0.6 m for a
  nudge, ~1.3 m for a walk on OpenSim before braking, below; with it about a round trip's worth). The same covers
  walking in your room. While following and while the avatar
  coasts after a stop (Settling: until the region reports it still and the drawn position has caught up), the view
  doesn't move.
- When walking it there can't work (it made no progress for 1.5 s, the stick walk ended against something, or a
  flying glide left it over 4 m away), once the region has it still the avatar is teleported to where you see yourself
  (`VrPilot.Teleport.cs`; the user's fail-safe, 2026-10-26: a teleport makes the region agree at once, leaving nothing
  to reconcile), within the region, where that's sure to land there: not seated, the region allows direct teleports,
  the parcel there takes teleports anywhere (or is yours; unknown parcels are asked about for next time), no wall
  between the avatar and you (walking into a wall in your room never gets you through it), at most every 5 s, and
  teleports here have landed where asked so far (one that lands over 3 m off, or comes to nothing in 6 s, turns it off
  for the region). Otherwise you're put back on the avatar (`Correct`): a jump (a straight cut) or a slide (critically
  damped, at most 0.6 m/s). The "putting you back" setting (`correction`) is teleport (the default), jump or slide
  ("blink", the jump's name until 2026-10-28, still reads as it); a teleport's fallback is the jump or slide chosen
  before. Measured on the local grid: a 6.6 m flying glide and a 1.8 m sideways walk ended
  with the avatar teleported to the view (`teleporting your avatar to you` in the console); a step into the wall in
  the room was refused (`not teleporting (there's a wall between)`) and put back.
- Walls, steps and the region's word (`VrPilot.Collide.cs`, `RegionScene.CollideRay`; after the session of
  2026-10-26, where the view walked through log walls the avatar was stopped at and onto a boat it couldn't step up
  on): the walk is local, so it needs the world's solid shapes locally. Rays along the walk at three heights over the
  surface walked on (just over the step height, the waist, the chest) meet the nearest solid surface from outside:
  prims by their physics shapes (Physics shapes, below; not phantom or flexible ones, not trees, grass, attachments
  or what hasn't loaded), and other standing avatars as a box. The head stops 0.32 m short of it and the rest of the step
  slides along it, as the avatar's capsule does. Steps up to the step height (learned from the region: 0.4 m to start,
  OpenSim's climbs 0.3 and stops at 0.45; raised when the region's avatar climbs a taller one, lowered when it's
  stopped at a lower one) are climbed by the floor as the head goes over them, locally; taller ones are walls to the
  low ray, so every step is one or the other. Where the model and the region disagree the region wins quickly: when
  it stops the avatar where the model saw nothing (a doorway it won't fit, a physics shape not known yet) and
  says so (its position barely moving along the walk for 0.2 s and its velocity low), the walk is pulled back onto the
  avatar over about 0.2 s and a wall is remembered there for 4 s; when its avatar goes through something the model
  called solid (past its plane, straight through it, not round its end), that prim stops counting for two minutes.
  Measured on the local grid (`tools/motion.py`, main-grid-like lag): into a wall, the view stopped at 43.58 and the
  avatar at 43.61; at a 0.6 m step at 205.18 and 205.20; a 0.3 m step climbed by both, the view rising 0.12-0.25 m
  without a bump; at a 0.45 m step the model climbed it and the region didn't, the walk was pulled back and the step
  height lowered to 0.42, after which both stopped at it. The rays cost 0.02-0.05 ms a frame walking among Atelier's
  buildings (`SOJOURN_TIME_RAYS=1` logs it). Thin posts between the three rays are caught only by the region's word.
- Physics shapes (`Control/PhysicsShapes.cs`, `RegionScene.CollisionTriangles`, `World/PhysicsShape.cs`; Claude
  Opus 5.5, 2026-10-27): the region collides with a prim's physics shape, which isn't what's drawn, so the model asks
  for them as the viewer does (LLViewerObjectList::fetchPhysicsFlags: up to 256 ids a POST to the region's
  GetObjectPhysicsData, answered with each one's PhysicsShapeType): first any prim a collision ray has just met, then
  every prim within 24 m across of the avatar and anywhere below it (where a fall lands), nearest first, scanned a
  second at a time, one request at a time; a failed or refused request (a region over its rate answers with a line of
  text) goes again first after a pause growing from 0.5 to 8 s; kept for the session, asked again after 10 minutes.
  While anything waits or failed, the console says every 30 s how many are known, answered and waiting
  (`physics shapes: ...`). Until a prim's type comes it counts as uploaded (below), so a mesh plant whose real type is
  none is a solid hull meanwhile. Then, after
  the viewer's LLPhysicsShapeBuilderUtil and its physics-shape view: a child with physics shape none doesn't count; a
  mesh is its convex hull (the asset's physics_convex BoundingVerts) for a convex hull shape, and for a prim shape its
  decomposition's hulls if it has them, else its physics model (physics_mesh), unless it's under 0.5 m in some
  dimension (the region's thin-triangle rule: the hull), else its hull; until its type is known, as uploaded (a
  physics model means prim, none means hull). A prim or sculpt with a convex hull shape is the hull of its drawn
  shape; with a prim shape, its drawn triangles. Hulls are worked out once per asset or shape (incremental, from the
  points), all in the shape's unit space, tested front faces only like the rest. `SOJOURN_PHYSICS_SHAPES=0` turns it
  off for comparing. Checked in the simulator on the local grid (OpenSim answers GetObjectPhysicsData): walking into a
  mesh doorway arch (two posts and a lintel, uploaded with its hull), the view stopped at the hull's face, 66.53, and the
  region's avatar at 66.56, with no pull back; walking into a wall's child prim with physics shape none, the view and
  the avatar both walked through. Not checked against the main grid's answers or a decomposed mesh.
- Movement "server" (`movement`: server, the default since the user tried both on 2026-10-27, or predicted; menu > moving, SojournSettings; the user's ask,
  2026-10-27, after the prediction's misses: views held above the ground, falls off edges the model had as floor;
  Claude Opus 5.5): everything above about the local walk, walls, steps and floors is predicted mode. Server mode drops
  all of it: the room rides the region's avatar as the renderer draws it (`MotionSmoother`: dead-reckoned by its
  velocity, each update's difference decaying, below), every frame, so the view goes exactly where the region takes the
  avatar, walls, steps, slopes and falls included, with no local model for walking, sweep or clamp. Head tracking
  stays the runtime's, local and immediate; only where the room sits in the world follows the avatar. Kept from
  predicted mode: the floor's spring (steps and each update's error glide; the region's own height once it reports the
  avatar still, since the drawing runs on past a landing), never down through a floor (`Underfoot`, above: the drawing
  runs on down past a landing until the region's word arrives, which took the view 2 m through a 5 cm plate after a
  10 m fall and 5 m into the ground after 100 m; now 0.06-0.1 m, the avatar's own hover; the region's still avatar wins
  over anything holding the view higher, and physics shapes are asked for in this mode too), the follow walk to the
  head when you walk in your room (the room doesn't ride that walk or its coast, or it would chase you) and putting you
  back when it can't, turning, seats; a jump the avatar's velocity doesn't explain by over 0.3 m standing still, 2 m
  moving or just stopped (a teleport, a push) is drawn where the region has it at once (`MotionSmoother.Position`'s
  snap) and the room carried with it, one that brings the avatar to you (a teleport to you) isn't carried, and over a
  metre is a cut (until 2026-10-28 the room went by the region's own positions for a quarter second instead, which
  stepped the view at each update). An update that only came late or early along the avatar's own way (moving over 1 m/s; its step along
  the velocity between half a metre back and the prediction plus half a second's travel or 2 m, under a metre plus 15%
  across) isn't a jump at all, in either mode (`VrPilot.AlongTheWay`; `SOJOURN_ALONG_THE_WAY=0` for comparing): a slow
  region pauses its updates and comes back short of where the velocity would have had the avatar (the session of
  2026-10-28, 00:05:34: flying at 16 m/s, a 0.25 s pause, 2.5 m short), and taking that for a jump froze the view for
  0.15 s, then cut it 1.8 m on to the next update; the drawn avatar glides through it instead. Letting go of the stick, the view doesn't ride the drawing's run-on past the stop (it ran
  0.4 m past where the avatar stood and was pulled back, and with the old 0.3 m jump rule moving too, the pull back was
  taken for a jump and left out, so the view stayed 0.43 m ahead of the avatar for good): it slows evenly to where the
  avatar will rest (where it was drawn at the release carried on by its speed for a stopping time learned per gait
  from where the region stood it, 0.12-0.17 s on OpenSim with the brake), then eases onto where the region has it
  (`VrPilot.Glide`, Braking below; not while flying, where it did worse; off with `SOJOURN_SERVER_GLIDE=0`); off the
  ground mid-stop (walked off an edge) it rejoins the drawing over a few tenths of a second. The trade-off: the stick
  moves the view about a round trip late (~100-200 ms on the main grid), and so the view still runs on 0.3-0.6 m after
  you let go at a walk (the avatar's own run-on: the region hears the stop half a round trip late), and how steady the
  view is while walking is only as good as the drawn avatar's smoothing of the region's updates (the 2026-10-25 jitter
  work made the predicted walk the default for that reason), but you never stand, walk or fall anywhere the region
  doesn't put you.
  How the avatar is drawn between updates (`World/MotionSmoother.cs`; Claude Opus 5.5, 2026-10-28, after the user
  found server movement rubber-banding while running at Eden Valley: pulled back, jerks, a small back-and-forth
  wobble). The main grid sends the own avatar's update every ~45 ms while it runs, most within 5 mm of where the last
  one's velocity predicted, one in ten 6-10 cm off. Each update used to be blended in over 0.18 s (smoothstep),
  which turned those few centimetres into speed changes (the wobble) and, when the region slowed the avatar, pulled the
  drawing back over what it had run on. Now, as moving prims do (below), what was drawn (position and velocity) less
  the new trajectory, both taken at the display time of the first frame that shows the update, decays critically
  damped at 6/s (`Settle`; taken at the update's arrival instead, the first frame shown ~50 ms later jolted by the
  decay's curve over those 50 ms: p99 acceleration 40 against 143; the prims' decay had the same flaw and is now dated
  the same way). The own avatar's update is dated by when its packet came in (`SelfUpdateGuard.LastArrival`), not by
  the first frame that sees it, up to a frame later. And where no region lies beyond an edge (an island's: none the
  session has a circuit to, LLViewerObject::interpolateLinearMotion's clipToVisibleRegions) no avatar is drawn running
  out over it: the region lets one walk on ~1 m past the edge, then puts it back exactly 1 m inside, still moving, so
  one moving outward is held at that metre and any other kept inside the edge (`MotionSmoother.Edge`). Holding the
  stick into such an edge, the drawing used to run 1.4 m out and come back 2-2.4 m every ~0.45 s, and the view cut
  back with it (12 cuts in 8 s that evening). Measured by replaying that evening's own updates (`local/main/vr.csv`,
  server movement, 251 s of running on the ground) through the real `MotionSmoother` with a recorded clock
  (`Clock`): the view's acceleration median 6.2 -> 0.9 m/s^2 along the way, p95 40 -> 10, across it 6.1 -> 1.5, p95
  34 -> 16; backward travel 11.6 m in 54 spells -> 1.1 m in 13; near the island's edges with the stick held, 25.7 m
  back -> 0.4 m. The drawing runs further off the region's own path on turns (p95 52 cm against 42; 4/s would have
  been smoother still at 75), and on stopping it overshoots less (median 45 cm past where it came to rest, against 58).
  Not done, on the numbers: scaling avatars' dead reckoning by time dilation (that evening's updates matched their
  velocity, median 1 mm off, so dilation was ~1); dropping out-of-order own updates as Alchemy does (none in 6804);
  smoothing the walking heading against head sway (the region's heading follows the body's 1:1, sd 5 degrees against
  its own 1 s mean, much of it looking about while walking; a deadband would make steering sluggish, so it's the
  user's call). Nor a speed-limited follower for the view (the crossing's `Ride` for all walking): with the decay,
  backward motion is nearly gone, and a follower drifts off the region's avatar where its velocity says one thing and
  its positions another (at an island's edge the region reports the avatar still running outward while it holds it).
  `SOJOURN_AVATAR_BLEND=1` brings the 0.18 s blend back for comparing.
- Moving things (`World/MotionSmoother.Prims.cs`, `RegionScene.Motion.cs`; Claude Opus 5.5, 2026-10-27): vehicles,
  keyframed rides, physical things and spinning signs move between the region's updates as the viewer moves them
  (LLViewerObject::idleUpdate): each update carried on by its velocity, acceleration (with the viewer's half-step
  correction) and angular velocity about the parent's axes, in the region's dilated time, for as long as the region is
  heard from (a keyframe can run seconds without an update; the viewer fades out only once the circuit's quiet for 2 s,
  and so does this), a second at most past the region's edge. llTargetOmega's spin is the viewer's alone and is kept
  across updates that don't turn the object (a colour change doesn't snap a wheel back), restarted by ones that do or by
  a physical object's new spin. Where the viewer snaps to each update, the difference between what was drawn
  (position, velocity, rotation and its rate) and the new trajectory decays critically damped (6/s: ~85% gone in half
  a second; taken at the display time of the first frame that shows the update, since 2026-10-28); jumps over 6 m
  snap. Avatars' smoothstep blend over 0.18 s was tried first (and avatars kept it until 2026-10-28): it turned each update's few
  centimetres of disagreement into a speed change, and keyframed motion on OpenSim (updates every ~0.1 s, each 2-5 cm
  off its own velocity, turns sent as steps with no angular velocity) made the view's speed swing 0.4-2 m/s on a
  1.4 m/s ride. The scene places
  moving objects and their linksets every frame at the frame's display time (only their placement and cull bounds; the
  probe scan's volume follows the updates); everything else is placed when the region reports a change, as before.
  Seated, the person's avatar rides its seat where it's drawn (`MotionSmoother.Seated`, one answer a frame for the
  scene, your own body and the view), and the headset's view follows it frame by frame: the seat's motion carries the
  room, its turning turns it, the eyes are the sit animation's put on this frame's seat pose (the renderer gives them in
  the avatar's own frame, `SelfSeatedEyesLocal`), and only a jump that last frame's motion doesn't explain (over a
  metre) is a cut, so a fast vehicle doesn't cut every frame. The desktop's camera rides the same.
  `SOJOURN_PRIM_MOTION=0` turns it off for comparing; `SOJOURN_PRIM_LOG=1` prints each update a moving prim gets
  and where it was drawn then. Measured in the simulator (a keyframed ride on the local grid, a square loop with
  diagonal turning legs, ~60 s seated): the view's acceleration median 5.8, p95 27 m/s^2, and its turning rate's change
  frame to frame p95 15 deg/s; with the 0.18 s blend it was p95 71 m/s^2 and 86 deg/s. Replaying the logged updates
  offline, snapping as the viewer does gives p95 145 m/s^2. What's left is mostly the ride's own corners (keyframes
  change velocity at once) and frame hitches; a hitch over a metre of travel is a cut.
- What the region does to the avatar: a jump its own velocity doesn't explain (over 0.3 m standing, over 2 m
  otherwise) that takes it away from you (a teleport, a sit, a push) moves the room the same way at once (a cut
  when it's over a metre); a jump that brings it to you (the region catching up after holding it back) isn't
  followed. Moving at over 0.3 m/s without being asked (a moving floor, being pushed along, the agent API walking it)
  carries the room along. Sitting, the region moves you (vehicles, seats). Everything smaller is ignored: the
  region's millimetres to centimetres of settling never reach the view.
- Snap or smooth turns rotate the play space about the head. Region crossings shift the origin into the new region's
  coordinates (a position still in the old region's, for the frame or two LibreMetaverse takes to move it, is held
  over: one well outside the region, and since 2026-10-27, for 3 s after a crossing, one nearer where the avatar was in
  the old coordinates than in the new; walking across, the old coordinates are only centimetres past the edge, and in
  the simulator that frame was taken for a 256 m jump and back). Crossing at speed, below. A teleport to another region, a neighbour too, is no
  crossing: it's an arrival (`VrPilot.Arrival`, `Hold`, `Arrive`; Claude Opus 5.5, 2026-10-28). From the grid's
  TeleportFinish until that region is current and its arrival reply (AgentMovementComplete) has come
  (`TeleportWatch.Arriving`), the agent's position isn't to be trusted: LibreMetaverse makes the new region current
  only after it has sent CompleteAgentMovement, so for a round trip or more the position is the old region's read in
  the new region's coordinates (an unrelated spot of it), or the reply comes first and moves it into the new
  coordinates while the old region is still current. Meanwhile the view stays exactly where it was, in the old region
  (its scene, sky and water; Program.cs keeps a region the view is in drawn as it was even if the session lets it go
  first, so the eyes never show nothing), with head tracking and turning as ever and the body where it stood; nothing
  is walked, carried or put back and no AgentUpdates go out. Then one straight cut to the avatar where it landed, the
  room's heading kept, nothing of the old region's walking, following, seat or putting back carried over, the drawn
  avatar's track started afresh. No time limit: an arrival that never comes ends in a new login (30 s). A region change
  that isn't a crossing with no TeleportFinish heard is held the same way until that region has the agent. The console
  says `arriving at X,Y: ...` and `arrived in REGION: the view held N s, ...`; vr.csv's follow column says `Arriving`.
  A reply that comes after the region's own updates for the avatar (lost and resent) carries their newer position
  instead (`TeleportWatch.WireReply`, `docs/client.md` > Gotchas): its own is where the region placed the agent on
  arrival, and with a fall from a high landing point it put the view 4 m up and back down, two more cuts.
  On the local grid, where the reply comes at once, `SOJOURN_ARRIVAL_DELAY_MS` holds it back as a slow round trip does
  (only the reply: the region's own updates aren't held, so with it the body drawn in the old place goes as soon as
  the new region has the avatar, a fraction of a second into the hold; in the real order the reply comes first).
  Held in the new region's coordinates instead (until this, 2026-10-28), a reply arriving first was a cut to a spot
  of the old region and then another, a late one a cut to an unrelated spot of the new region, and the new region's
  sky lit the old place meanwhile. Measured in the simulator on the local grid (a temporary far region, main-grid-like
  lag, both movement modes, standing and walking with the stick, neighbours too): every teleport one cut, the head
  still to the millimetre through the hold, no other cut or slide in the 5 s after; the hold 0.07-0.6 s with no delay,
  0.93-0.99 s with 400 ms, 2.0-2.1 s with 1500 ms; eye shots every 0.1 s showed the old place throughout the hold.
  Recentring puts the avatar under the head and turns the room so looking ahead faces the avatar's way.
- Crossing at speed (`VrPilot.Crossing.cs`; Claude Opus 5.5, 2026-10-28): the region's word on the avatar is
  unreliable for a moment at a border. OpenSim makes the arriving avatar physical only when CompleteAgentMovement
  comes and gives its momentum back on the next physics step, so the new region's first full update says it stands
  still at the handover point (`SOJOURN_SELF_TRACE=1`, `docs/client.md` > Gotchas: velocity 0 there, 47 ms later 0.9 m
  on at full speed); the handover point sits a metre or two off the old trajectory either way; and LibreMetaverse
  writes it again once it has connected, on the main grid a round trip late. Taken as the truth, flying at 12.6 m/s
  server movement cut 2-2.4 m back at some crossings and predicted movement took the still report for a wall, held
  the view at the border while the avatar flew on and put it back 21-43 m. So for 0.8 s after a crossing while moving
  (the stick or the climb held, over 1 m/s): a still report keeps the velocity from before (its position taken) until
  a moving one has come after it; predicted movement judges no walls by the region's word, takes no step back for a
  teleport, and doesn't let the servo slow the walk for how far the region fell behind, giving that up afterwards at a
  steady 15% of the speed plus 0.3 m/s (2.2 m/s flying); server movement rides a follower of the drawn avatar,
  moving at the region's velocity and easing onto the drawing at most a quarter of the speed plus 0.3 m/s faster or
  slower, until it's on it; the walk's trail and remembered walls move into the new coordinates with the room. A lurch
  becomes a short change of speed, never a step back, a stop or a cut. Seated crossings (vehicles) go their own way
  (CarrySeated) and aren't covered. The console says `crossing into X at N m/s` and, when a still report came,
  `crossing: the region reported you still at the handover`. `SOJOURN_CROSSING_FIX=0` turns it off for comparing.
  Measured in the simulator (main-grid-like lag, 4 crossings a run each way at 12.6 m/s; the scratch scripts' measure:
  the view's speed along the flight from 0.5 s before to 1.5 s after): server movement before, 5 cuts and 0.5-4.6 m of
  backward travel at every crossing; after, none, the speed 8.7-15.5 m/s. Predicted before, 2 cuts and a 25 m
  put-back; after, none, the lowest speed 6.6-12.2 m/s for about a second. Running across at 5.1 m/s, predicted,
  carried straight on.
- Height (`VrPilot.Lift`, `Floor`, `GroundUnder`): the room's floor is the ground plus a lift. Wherever the local
  model (the terrain's heightmap and the prims' tops) has the avatar standing where the region has its feet, the
  ground is local: under the head while the stick walks (hills, kerbs and decks need nothing from the network; within
  a step up or down, a drop waiting for the avatar to fall), and under the avatar otherwise, so standing, a lean or a
  step in your room never moves the floor, even on a slope (the head-jitter findings of 2026-10-26: following the
  ground under the head glided the world a few cm as the head moved). The avatar's small height over the surface is
  learned slowly, and a moment's mismatch while walking (OpenSim's avatar hops on a step's edge: within a step, no
  jump pressed in the last second) keeps to the model for up to 0.8 s; a jump or a fall is followed at once. Landing,
  the region's own position is tried when the drawn one (dead-reckoned on past the floor) doesn't match. Where the
  model doesn't match (flying, jumping, falling, a floor still loading), it's the avatar's feet (agent Z minus half
  the avatar's height, which the region sends as its scale), kept as far from them as the view stood when it left the
  model (that fading over seconds), led by the region's climb rate while walking (at most 1.5 m/s of it), and held
  while the avatar steps about away from the head (within a step). The floor follows that target through a critically
  damped spring (fed the climb rate off the model) and holds still within 4 cm when nothing moves; over 3 m it jumps
  (a cut), and a teleport's height goes with its cut. Before (the session of 2026-10-27, its vr.csv) a jump
  was held on the ground 0.8 s, then the view jumped 3-5 m up to the avatar unblinked, and landed 0.1-0.7 m into the
  ground. Never down through a floor (`VrPilot.Underfoot`): whenever the view's feet go down, a ray sweeps from where
  they were last frame to where they'd be, under the head and, off the model, under the avatar, so no frame's step at
  any frame rate passes a thin floor; the first surface the model knows (terrain, a prim's top by its physics shape,
  not other avatars) holds them and stops the spring's fall. Where the region has its avatar 0.5-4 m under that
  surface at the same place (falling through it; further down it may have fallen off beside it), the region wins: the
  view goes on down and the prim stops counting for two minutes, as for walls. Floors the model doesn't have yet (a
  mesh not downloaded) still land as the drawn avatar does.
  Landed, the region's height wins (Claude Opus 5.5, 2026-10-27, after the user found the view resting above the
  ground after falls at Inarishima: before, a model surface up to 0.5 m over where the region stood the avatar held the
  view there for good, and even where the model matched it the view stood up to 0.25 m over the avatar's feet): once the region's
  avatar holds still (no velocity, its height unchanged for 0.3 s) within 0.8 m of the head, not walked by the stick or
  the follow, the floor is its feet, up or down, through the spring (over a metre down, a cut), and whatever
  held the view higher lets go; walking, a hold more than a step over the region's avatar while it keeps level
  within 1.6 m of the head lets go after 0.4 s (after a jump the view had walked 1.1 m over it for 1.6 s). A prim whose top the avatar stands 0.15-1.5 m under (inside it, so the model has it
  wrong there: a plant's hull, a mesh whose type hadn't come yet, a hull over a dip) stops counting as a floor, not as
  a wall, for two minutes, and the console says which and what the model took it for (`the region has you standing
  ... under the top of object N (mesh, physics shape not known yet)`). Walking, the view leads up a step only onto
  prims whose physics shape the region has told us; onto others it waits for the avatar to climb (a step's round trip
  late), since until then a mesh plant counts by its hull.
  Measured in the simulator on the local grid at ~24 fps (`SOJOURN_LAG_CPU_MS=25`) with main-grid-like lag, old build
  against new: 10 m down onto a 5 cm plate, the view went 1.23 m through it (and jumped 4.9 m unblinked first) / stopped
  on it to the millimetre, the drawn avatar 1.3 m below meanwhile; 108 m down off the course's edge onto terrain, 6 m
  into the ground / none; standing and walking jumps, 0.3-0.9 m into the floor on landing and a 4.4 m unblinked jump up
  / none, the view rising with the avatar.
  At your own eye height the lift is 0. At the avatar's, it's the avatar's eye height (measured by the
  renderer from its shape's rest pose, `FrameRenderer.SelfEyeHeight`) minus your standing eye height (the highest your
  head has been over the last 20 s). Seated mode: the avatar's eye height minus where your head was when seated mode
  began or you last recentred, so you sit and it stands; following in the room then waits for a 0.8 m gap instead of
  0.5 (a lean doesn't walk it). A space without a floor starts at the avatar's eyes. Sitting on something in-world
  (either posture), the camera is at the avatar's eyes where its sit animation puts them (the renderer's,
  `FrameRenderer.SelfSeatedEyes`; 0.55 m over the agent's position until it has drawn them, or with your body hidden),
  moved by how the real head moves from where it was then; as it settles (0.3 s), the head goes onto those eyes and
  the room turns to face the way it sits, a cut. Before (the session of 2026-10-26, a windowsill at Burn2)
  it was a fixed 0.75 m over the agent's position, which on the local grid's 0.9 m sill put the view 0.53 m above the
  avatar's own eyes (they were 0.86 m over the seat); a seated agent's position isn't its pelvis.
- Late frames: each eye's depth goes to the compositor beside its colour (XR_KHR_composition_layer_depth; Meta's
  runtime and the simulator offer it), so a frame that misses its slot is reprojected by where the head moved as well
  as how it turned. Without it a missed frame showed the old view turned but not moved, which reads as the world
  shifting with the head. The console says "depth layers on" at start. The depth range handed over is 10 km to 0.1 m
  (reverse-Z), not infinite: the spec allows an infinite nearZ, but a runtime deriving its projection the usual way
  gets NaN from it. `reprojection_depth` (menu > view > graphics > depth reprojection, saved) turns it on: **off by default since
  2026-10-29** (the user A/B'd it swaying by a railing at half rate and at 512 m draw distance and saw no difference;
  and given depth, Meta's compositor misorders overlapping menus inside a patch around the view, below). See-through
  faces (flowers, glass, leaves) write no depth, so they're moved by the depth of what's behind them. Once a suspect
  for the jitter; switching it made no difference (the jitter's fix is below).
- Walls and the menus (Claude Opus 5.5, 2026-10-28; `Render/Realtime/Overlay.Walls.cs`): with the depth submitted,
  Meta's compositor hides a menu's quad layer where the eyes' depth is nearer, but over Link not reliably: a wall just
  behind the panel hid it depending on head pitch, rewriting the depth under the menus as distance from the eye (for
  an apparent radial-vs-planar test) changed nothing, and the user then saw walls hide menus only inside a roughly
  circular region around the view, not at all outside it. The runtime's extension list has no layer flag for it (no
  XR_FB_composition_layer_depth_test or _settings on PC). So we hide them ourselves:
  - Walls hide menus (the default, the user's choice: menus belong to the world; `menu_frost_test` exact, menu > view >
    wall test): each menu's footprint is carved to far in the depth the compositor gets (it never hides a menu), and
    the hands' cut (Your own body > Hands over the menus) takes anything in the eye, not only the own body, that's
    inside a menu's outline and nearer than it: walls, rocks and people in front of a menu go on the layer over the
    quads, exactly. The frost is tested at the menu's own depth. Not with `SOJOURN_HANDS_OVER_MENUS=0`.
  - For comparing, `menu_frost_test` radial (or head) leaves it to the compositor and tests the frost at the radial
    depth it seemed to use (placed sheets drawn as an 8x8 grid at that depth). `menu_frost_bias` scales the frost's
    distance by 1 + bias.
  - `menus_over_walls` (menu > view, off by default): menus always over the world: the frost untested, the footprints
    carved, nothing over the menus but the hands. Carving writes each menu's footprint (4 px wider) as far after the
    eye's depth is copied, except where the cut's mask is (what's in front keeps its depth, so its layer reprojects
    right); behind a menu a late frame's world moves as if far away.
  - A confirmation is never covered, in either mode: while one is up (the panel on top), nothing hides the menus that
    frame, the panel's frost is untested and its footprint carved (`OverlayScene.PanelOverWalls`).
  - Through cloth and hair (2026-10-28, seen in Meta's own mirror, `tools/mirrorshot.py`): a menu behind a dancer's
    dark cloak showed its bright rim and text as torn, aliased pixels along its edges. Alpha-tested cloth is full of
    pixel-sized holes at the eye's resolution, and blended faces (`Renderer.cs`'s alpha pipeline writes no depth)
    leave the depth of what's behind them, so the cut missed single pixels there. The mask now closes them: a pixel
    the cut misses goes with its neighbours when 5 of its 8 show (`handcut.frag`, walls mode only); holes a pixel or
    two wide close, a straight silhouette doesn't move. Larger blended areas (a sheer veil) still let the menu through,
    as they write no depth; making the cut see them would mean the blended faces marking where they're mostly opaque.
    After that the top and bottom edges were clean through the user's own hair, the left and right still tore a
    little (Claude Opus 5.5, 2026-10-28 late night; two likely causes, both fixed): gaps
    between hair strands are often two pixels wide, which 5 of 8 doesn't close, so a gap along a side edge showed a
    sliver of rim; a gap up to two pixels across a row or column now closes when what covers shows on both sides
    (a straight silhouette has one side only; `handcut.frag`). And a reprojected frame moves both projection layers
    by their depth while the quads stay put, so hair near the eye slid over the menu's edge from outside the outline,
    where the cut hadn't taken it (sideways, as heads mostly move): the cut now takes what's in front of a menu's
    plane up to 0.05 rad past its outline (`CUT_REACH`; outside the menus the over-layer shows the eye over itself,
    which changes nothing), its box grown to match.
    `dev.py shot` doesn't show any of this: its compositing leaves the hands' layer out (it names it).
- Menus over menus (Claude Opus 5.5, 2026-10-28; `Xr/MenuOrder.cs`, `Render/Realtime/LayerCut.cs`): the compositor
  draws quad layers in the order given, with no depth between them, so we decide which menu is over which, as we do
  for walls. Sorting by each quad's nearest point to the head (as before) isn't what decides which of two sheets is in
  front where they overlap: the panel and the reader near it swapped as the head tilted (the session of 2026-10-28; in
  a sweep of head poses around them, the old order was wrong in 378 of 2632 poses where they overlapped).
  - Each frame, as the layers are recorded (`XrSession.PlanMenus`), every pair of menus that overlap as seen from
    between the eyes is ordered by geometry: the part of one on the eyes' side of the other's plane is in front of it.
    One wholly on one side of the other's plane goes over or under it; coplanar ones by their stack (a board's screen
    over its glass). A topological sort of those constraints is the order, the old farthest-first order deciding only
    where nothing constrains it (menus apart on screen, where any order looks the same). Exact from any pose for menus
    that don't meet: 61412 rays over 3000 random scenes all matched the nearest hit (the old order missed 1082).
  - A menu another passes through (the following panel pushed into a reader left in the room: 308 times in that
    sweep) has no right whole-layer order, so it goes in parts: cut along the other's plane, each part its own quad
    layer showing only its side of the line, laid under and over the other. The line is fixed on both sheets, so this
    is exact from any pose and for both eyes. A part is the picture copied into a swapchain of the part's own and the
    rest cleared by a one-triangle pass, antialiased over a pixel so the two parts' edges add up (`LayerCut`,
    `layercut.frag`); a CPU picture is kept on the GPU while it's cut (QuadLayer's source), so parts cost no upload.
    The parts' swapchains and the source go after ~3 s unused. A cycle of sheets that don't meet (three in a ring) is
    broken by a cut too (Newell's painter's algorithm); a menu is cut by at most three planes and a frame by at most
    eight, past which the old order decides (on random scenes of 2-5 menus passing through each other, 0.04% of
    overlapping rays wrong, all in scenes needing more).
  - Cost: the order is ~2 us of the render thread a frame, nothing allocated; parts, only while menus pass through
    each other, a copy and a clearing pass each (GPU, small) and up to three swapchain images of the menu's size each.
  - The mirror window and `dev.py shot` lay the same parts (the mirror's draw and `SeenShots` apply each part's clip).
    `SOJOURN_MENU_PARTS=0` orders whole layers only, for comparing. Checked on the CPU (2026-10-28, the user in the
    headset): the order against ray casts, the shaders compiled.
  - The compositor's depth (Claude Opus 5.5, 2026-10-28 late night): in the headset, with menus through each other,
    a bubble of the overlap flipped with head tilt between the back menu and only our frost. Nothing of ours depends
    on the pose there (both menus' frosts blur the same backdrop, tested only against the world; the cut's mask and
    the order are exact), and "the layer gone, only our frost left" inside a patch around the view is what the
    compositor's own depth test did with walls. Carving makes it never hide a menu behind the world, but it seems to
    test quads against each other too, a part's cleared side counting as solid. So while any menu goes in parts no
    depth goes to the compositor (late frames reproject by rotation alone meanwhile; the pacing line counts those
    frames, "held back in N"). Confirmed 2026-10-29: `reprojection_depth` off cures it. The same flip came back with
    whole menus merely overlapping (the reader just behind the panel: inside a circle around the view the panel's
    glass went missing over the reader, back on tilting the head down), and stayed with our carving off, with the
    hands' layer sent without depth and with no hands' layer: so it's the compositor testing quads against each other
    by the depth we send, nothing we write into it. So no depth while any two menus overlap at all
    (`MenuOrder.AnyOverlap`), and the depth itself is off by default (above).
  - Frost through menus: quads lie over the eye images, so a menu behind another is never blurred by the front one's
    frost, only seen through its glass (tinted, sharp); the frost under both is the world's. Blurring the back one
    would take a frost of its own between the two layers, per eye (a quad per eye at the front menu's pose, made from
    the eye's backdrop with the back menu drawn in): not built.
- Late frames and the runtime (from the main-grid session of 2026-10-26, Claude Opus 5.5): each frame is rendered for
  the pose xrLocateViews gave at xrWaitFrame's predicted display time and submitted with that same pose and time; the
  rig only places the play space, a rigid move, so the compositor's reprojection is always consistent with the image.
  Swapchain images are acquired, waited, written in the frame's last submission and released once the queue has
  finished it (below; split frames under Overlap), on the queue given to the runtime (nothing else submits to it). What the person sees when frames are late is the runtime's: with
  the GPU over the period (90 Hz: 11.1 ms) Meta's runtime drops the app to half rate and synthesises every other frame
  (ASW); a frame later still (GPU over 22 ms) leaves an older one up, reprojected, for another slot or two. That
  session's flower field (Mirromere) and café (Papercut), where the person saw an older frame flash on head movement,
  ran at a steady half rate with the GPU at 12-37 ms even at the smallest eyes (x0.6). The console's `VR pacing:` line every 10 s shows it: display steps (1
  on time, 2 half rate, 3+ late) and how often the step changed, frames ended after their display time, how far ahead
  of the display time xrWaitFrame woke us (mean and range; frames.csv's last column has it per frame), frames that
  showed no scene or a grey void (either a flash), frames where the rig cut the view to a new spot (`cut N`; each also
  logs `view cut: ...`, flying or on foot and the avatar's speed), how long xrWaitFrame held the loop and its own time from waking to
  xrEndFrame, whether depth went to the compositor, the GPU frames over the period, and the costliest GPU passes; the
  `VR ... fps` line says whether the desktop mirror was on and in how many frames its window image wasn't free yet at
  submit (the frame's one submission then waits on the GPU for the 60 Hz window, the eyes' work with it); with the head
  tracked, how far the head as rendered was from where the runtime later says it was at that display time (degrees and
  mm, mean/p95/max: the error the compositor corrects) and how much the runtime's prediction for that same time moved
  between waking and xrEndFrame (what locating the views later would gain). Meta's runtime adds a `VR runtime:` line,
  its own counters (XR_META_performance_metrics: compositor dropped frames, its CPU and GPU time, motion-to-photon
  latency). Ctrl+Numpad1 turns ASW off on the PC runtime (Ctrl+Numpad4 back to auto), for telling its synthesis from
  our frames.
- The jitter, and its fix (2026-10-27; the user's test in the headset, local grid and main grid): under GPU load, a
  fast, fine shake of the whole view, like repeated previous frames or frames from a previous head position. It came
  with lag, was worse at medium lag than heavy, and didn't change with ASW or depth on or off; a lower or uneven frame
  rate (stutter) was a different thing and fine. Fix: `XrSession.WaitGpuBeforeRelease`, always on: after the frame's
  submission the queue is waited idle (`vkQueueWaitIdle`) before its swapchain images are released and the frame
  ended; the console says so at start, and `SOJOURN_XR_WAIT_GPU=0` turns it off for diagnosis (the pacing line then
  says "NOT waiting"). Likely cause, not proven: Meta's compositor reading an eye image before our writes to it had
  finished, so it showed the image's older contents corrected to the new head pose; a 2021 report on Meta's forums
  describes the same for Vulkan apps under load, and the wait also changes timing, so which side's sync is at fault is
  open. Found with `tools/lagtest.py` (below), after three sessions in which the frame loop, the rig, depth, ASW and
  cadence were ruled out. Cost: the CPU now waits for the GPU each frame (~9 ms a frame at Itame, 38 fps there against
  the mid-40s before). A cheaper form is to be checked in the headset against the same symptom before it replaces
  this. The studio's `docs/vr.md` has the general lesson. Whose bug it is wasn't settled: a standalone reproduction
  for a report to Meta was dropped (the user, 2026-10-27; in git history before this line); the workaround is to be
  published instead (`docs/public-note-draft.md`).
- Overlap (Claude Opus 5.5, 2026-10-28; `XrSession.Overlap`, on; `SOJOURN_XR_OVERLAP=0` back to frames in turn). The
  wait above ran on the render thread before it took the next frame, so CPU recording and GPU work added up (Tempura
  Island: GPU 10.8 ms, yet a steady 45 fps, which shimmers while walking). Now a third thread, frame end, does the
  wait, the releases, xrEndFrame and the mirror window's present, while the render thread builds and records the next
  frame. That frame is split (`IFrameTarget.TrySplit`, FrameRenderer): its scene goes into one command buffer with the
  eyes' depth, the menus' walls and the hands drawn into XrSession's own two-layer images (each eye now finishes into
  an output of its own in a stereo set, so both survive to the end); xrBeginFrame for it comes from the frame-end
  thread right after the last frame's xrEndFrame (the main thread's xrWaitFrame for the frame after waits on that
  begin, so it isn't held behind the recording), or from the render thread if it took the frame later; once the last
  frame has ended the scene is submitted (on the frame-end thread straight after xrEndFrame when the scene was ready
  first, so no thread hop in the GPU's gap); then the swapchain images are acquired and a second buffer copies
  everything in, the quad layers' pictures and the window's copies too (`RecordOutput`), carrying the slot's fence.
  Per frame:
  ```
  render:    [scene+record N+1] ...wait... [submit scene][acquire, output N+1, submit][scene+record N+2] ...
  frame end:  [GPU wait N ........][release, xrEndFrame N, xrBeginFrame N+1][window]  [GPU wait N+1 ......]...
  GPU:       [N scene][N output]   gap  [N+1 scene .........][N+1 output]   gap  [N+2 ...
  ```
  A frame whose recording fails is begun and ended anyway (`XrSession.Abandon`, from the render loop's catch), so the
  main thread's xrWaitFrame returns and the failure reaches it at the next hand-over instead of freezing the headset;
  the GPU wait's device loss is thrown as the renderer's (`VulkanException`, so the restart-on-lost-GPU path sees it).
  The jitter fix is untouched: nothing of a frame goes back and it doesn't end before `vkQueueWaitIdle`, and since the
  next frame's work is only submitted after xrEndFrame, the queue is idle at every release and end, as before. Not
  done, on purpose: submitting the next scene before the last frame is released; the spec has the runtime treat
  released images as written by queue work still pending, so whatever it queues at release (Meta's likely sync) would
  wait behind the next frame. What it costs: the GPU idles between frames for the hand-over (wake, releases,
  xrEndFrame, xrBeginFrame, submit), and copies of the eyes' depth (always) and hands (while menus are up) into
  ours first, ~0.1 ms each per eye pair; and video memory, ~90 MB at the Quest's size (the second eye's output and
  the two-layer depth and hands images; the per-eye output only while overlap is on, `RenderTargets.OutputPerEye`).
  A frame that can't split (no multiview, the loading frame) still ends on the frame-end thread but records after the
  last has ended. One lock (`VulkanContext.QueueLock`) covers every use of the queue, OpenXR's calls that may touch it
  (xrBeginFrame, xrEndFrame, swapchain acquires and releases) included. The HUD and page layers' visibility is taken
  when their image is copied (render thread), not asked again where the frame ends. The pacing line's `overlap:` part: frames split, how many had
  their scene ready before the last had ended (overlapping its GPU work), the render thread's wait for those ends, and
  the frame-end thread's GPU wait and release-to-xrEndFrame; `idle between frames` (the GPU, last second) shows the
  gap. In the `VR ... fps` line, "handing images over: headset" is now the render thread's part (waiting for the last
  frame's end, acquiring).
- Frame synthesis (`Xr/MetaAsw.cs`; the user, 2026-10-26, after a session that ran 61% at half rate and still had the
  jitter: suppress the headset feature if it's the cause): Sojourn turns Meta's ASW off for its headset session
  through the runtime's Debug Tool CLI (`OculusDebugToolCLI.exe`, `server: asw.Off`) and back to auto when the session
  ends, so a slow frame is reprojected (with depth) rather than the app locked at half rate with every other frame
  synthesised. No setting since 2026-10-28 (the user: it brought the jitter back each time it was tried; off for
  good). Only on Meta's runtime with its CLI
  installed; the console says which it did. The runtime keeps the setting until it restarts, so it goes back to auto
  however Sojourn ends: a marker (`%LOCALAPPDATA%\Sojourn\asw-off.txt`, the process id) and a hidden PowerShell
  watcher that waits for the process and restores it if the marker is still that process's (a killed client, as at
  13:18 on 2026-10-27); a clean end, process exit and a crash restore it first; a start that finds a dead process's
  marker restores it too. Hitches over two periods (CPU spikes, GC) still show an older frame either
  way: `docs/performance.md`.
- Standing still, the body turns to the head's heading once it's 40 degrees off; moving, it faces the head (above). AgentUpdate carries the head's
  orientation and the camera pose (the interest list follows it).
- SL's region frame is Z-up (x east, y north); OpenXR's is Y-up with -z forward: (x, y, z)_xr = (-y, z, -x)_sl,
  quaternions by the same map on their vector part. OpenXR's view space is the renderer's eye space, so eye offsets
  and rotations relative to the head go across untouched.

### Your own body

Others see the region's avatar; the headset draws its own copy (`SelfBody`, `Avatars.PoseSelf`), with the region's
animations:
- Standing, its eyes are put under the camera, 8 cm back along its facing (`SOJOURN_BODY_BACK`), so looking down shows
  the chest and feet; its feet stay on the ground. At your own eye height it's scaled about its feet so its eyes are
  at yours (it keeps its proportions; others still see its real size). It faces the way the region's avatar does,
  smoothed. Sitting on something, it stays on its seat.
- The neck and everything above it shrink to a point, and nothing worn on them is drawn (hair, hats, glasses): no
  inside of a head in your eyes. Its shadow is headless too.
- Arms: two-bone IK from the shoulders to the controllers' grip poses, elbows hanging down, out and back. Fingers keep
  the animation. The hand (`VrPilot.Hand`): OpenXR's grip -Z runs along the tube the closed fingers make, not along the
  hand; the hand's bones run where Meta's runtime aims the controller, 60 deg from the grip's -Z toward its -Y (measured
  in the simulator: the aim is the grip turned 60 deg about its X); palm in (+X on the left, -X on the right), thumb up
  the tube; the wrist joint 7.5 cm back along the bones from the grip's centre and 3 cm toward the back of the hand.
  When a controller is beyond the arm's reach (SL arms are short for their avatars, and the body stands 8 cm behind
  the eyes), the upper arm and forearm stretch along their bones, up to 1.4x, so the hand still reaches it. (Taking
  the grip's -Z as the hand's direction tilts the fingers up 60 deg and puts the wrist under the controller; without
  the stretch, the hands stop near the chest whenever the arms reach out.)
- Hands and animations (Claude Opus 5.5, 2026-10-26, after the user's arms didn't dance on a dance machine): the
  `tracking` setting (menu > moving > "tracking": auto, on, off; the assistant's settings tool too; the head too
  wherever it comes to be shown).
  Auto, the default, lets the arms follow the animations while something around you animates you: an animation
  started by an object you aren't wearing (a dance machine, pose ball, a seat's own sit, old dance balls' built-in
  dances) or one the agent API played for you (`Core/Agents/SelfAnimations.cs`, checked four times a second); the
  hands come back when it stops. Not for the region's own standing, walking and sitting, nor your AO or other
  attachments, which animate you all the time (a dance HUD you wear counts as that: off lets its dances move your
  arms). Why automatic: a dance or a pose is something you chose that moves the arms, while an AO's stands are
  background the tracked hands should win over, and the animation's source tells the two apart without a press. The
  head isn't posed from tracking at all (it's hidden in your own view; others see it turn where you look by the
  viewer's look-at, below). Untried in the headset.
- Whole-body one-shots (a bow, a gesture's) play on this body, but the eyes stay under your real head, so from them
  it mostly bends below you; a mirror shows it. One-shots on your own avatar are stopped once played, as the viewer
  stops them (`SelfAnimations`; else the region keeps them listed and the same one again does nothing).
- Checking without a headset: `SOJOURN_SELF_TEST=orbit` (a camera circling the avatar) or `down` (from its eyes, looking
  down) on the desktop, with the hands tracing circles; `--shot-every` saves the views. In the simulator (`vr-sim`),
  `SOJOURN_SELF_TEST=hands` (`Control/HandsCheck.cs`) films your body from in front, then from its left, every 6 s, with
  each controller marked (a dot at the grip, a short line along its -Z, a long one along its aim, a dot where the wrist
  was asked to go): the hands should close around the dots. `watch` looks from the eyes at the left wrist (voice's mic
  token).
- Over the menus (Claude Opus 5.5, 2026-10-27; the user's decision after a finger over the panel showed as a brown
  splotch): the hands and the rest of your own body cover every menu where they're in front of it, per pixel. A finger
  in front of a menu shows sharp over it, a hand reaching behind one stays hidden by it, a finger pushing through shows
  up to where it crosses the surface; world objects never cover menus. The menus are the compositor's quad layers,
  over the eye images, so the hands go up again on a layer above them, cut by each menu's plane:
  - The own body (`SelfBody` and what it wears; `InstanceFlags.Self`) raises the G-buffer's flag a little, inside the
    lighting's tolerance (`GBUFFER_SELF_MARK`). Per eye, after the scene, a mask (`Overlay.Hands.cs`, `handcut.frag`):
    1 where that flag is set and the pixel, from the eye's depth, is inside some menu's outline and nearer than every
    menu whose outline it's inside (`handcut.glsl`). The beams, dots and tokens the overlay draws within 0.4 m of a
    controller (the pointer's beam and its dot, the wrist's tokens) join the mask by their own depth, so they show over
    the menus with the hand that holds them; outlines and dots out in the world don't. A menu held at a hand
    (`XrSession.HeldCards`: the location line at the left wrist and the mini-map over it, docs/map.md) is left out of the cut and the menus'
    order and composited last, over the hands' layer, its frost untested: nothing covers it, and what's behind it
    (the hand over the panel) shows through its glass as it would without it. The beam and tokens pass under it. While
    one shows the compositor gets no depth (it would hide the layer behind the hand).
  - The frost leaves the mask's pixels alone (the hand stays sharp under the glass's place) and its backdrop has them
    taken out before the blur (colour and alpha scaled by what's left, the blur divided by it), so the hand doesn't
    bleed into the glass around it. `SOJOURN_PANEL_IN_EYES=1`'s previews of the menus leave them out too, so the eye
    shots show what the headset composites.
  - The eye where the mask is, premultiplied (clear elsewhere), goes into a swapchain of its own (`XrSession`, made on
    first use), submitted as a second projection layer after the quads, alpha-blended as the quads are, with the eyes'
    poses, display time, image rectangle and depth: a late frame is reprojected exactly as the eyes are, so the hands
    don't swim against the menus or the world.
  - The passes run only while a menu shows, and only over the menus' footprint on screen: ~0.12 ms a frame at Link's
    full eye size (2452x2570) with the settings menu up; the frame totals with and without were the same within the
    noise (`tools/hands.py cost`). The second layer is a full-view blend in the compositor too.
  - `SOJOURN_HANDS_OVER_MENUS=0` turns it off (for comparing). The console says "the hands go over the menus" when the
    layer first goes up.
- Not done: the body doesn't lean or crouch with you (it slides under the head), legs don't step with room-scale
  walking (the region's walk plays while it follows), finger poses from the controllers' touch sensors. In a dynamic mirror the body is drawn whole (`docs/rendering.md` > Mirrors).

### Where you look, seen by others (Claude Opus 5.5, 2026-10-26)

The user chose head direction, with a toggle, from `docs/shared-tracking.md`'s options. `Control/Gaze.cs` sends the
viewer's own look-at (a ViewerEffect of type LookAt, packed as `LLHUDEffectLookAt::packData`), so every viewer turns
your avatar's head, neck, a little of its torso and its eyes that way (`LLHeadRotMotion`, `LLEyeMotion`; no tilt,
at most 72 degrees off the body, under any animation that moves the head at priority 2 or more):
- The view's direction, smoothed over 0.08 s, as an offset from your own avatar (`LOOKAT_TARGET_MOUSELOOK`, no
  timeout): what the viewer sends in mouselook. The desktop's first person sends the same; its third person sends
  nothing (the viewer clears its look-at leaving mouselook).
- Eye contact: when someone's head stays within 10 degrees of the middle of your view for 0.3 s (within chat range,
  20 m), that person (`LOOKAT_TARGET_FOCUS`, as the viewer's camera focused on someone), held until they're 18 degrees
  off for 0.25 s. Every viewer then aims your head at theirs, which reads right even where your avatar's head isn't
  quite where your eyes are (the rig lets them drift apart, and your eye height may not be the avatar's).
- Looking around (spectating) holds the head where it was when it began (the user's call, 2026-10-26: the view isn't
  your real head then): nothing more is sent, and eye contact becomes the direction it was in. Off still clears.
- The viewer's rate: at most four sends a second, and only when the target changes or the direction moves more
  than 0.05 (about 3 degrees); one effect id a login. Turned off it sends a None (the viewer's clear), then nothing.
  A turn of the head is typically two sends.
- The setting (menu > moving > "your gaze": eye contact, direction, off; `gaze` in SojournSettings and the
  assistant's settings tool), saved. Eye contact is the default: the viewer's defaults (Enable LookAt on, Limit LookAt
  Distance off) send cursor and focus targets naming people too, and eye contact is most of what makes a head
  readable. Direction is the viewer's privacy idea (its limit turns named targets into a point near the head): the
  way you look, never who or what. `SOJOURN_GAZE_LOG=1` logs each send.
- Receiving (`Render/World/Avatars.LookAt.cs`, `Bodies/Animator.cs` `Look`): other people's look-at turns their heads
  in Sojourn as in the viewer (`calcTargetPosition` from last frame's pose, the head and eye motions ported, eased in
  and out over a second when an effect starts and ends; eye jitter and blinks aren't drawn). Your own isn't shown on
  your own avatar (a mirror or the desktop's third person show it facing ahead).
- Checked (2026-10-26, local grid): Sojourn Tester in the XR Simulator turning its view (`SOJOURN_VR_SCRIPT=motion`,
  `face`) toward, 35 degrees either side of and 60 degrees from Sojourn Friend on the desktop, whose first-person shots
  showed the head meeting its eyes, turning each way, the body following past 40 degrees, easing back to ahead when
  turned off and turning again on direction. The simulator's head stays level, so pitch was checked only on paper (the
  same basis as yaw).

## Testing without a headset

- **Meta XR Simulator** (an OpenXR runtime with a simulated Quest 3, in a window), unpacked in `local/xrsim/v207.1`
  (873 MB, not installed; it doesn't touch the registry). `tools/client.py vr-sim` points `XR_RUNTIME_JSON` at it for
  that process only. It keeps settings in `%APPDATA%\MetaXR\MetaXrSimulator`. It looks for its companion app for
  ~30 s first (`Cannot connect to a companion app`), then runs without it; hence `--xr-wait 90`. To get it again:
  `https://www.facebook.com/horizon_devcenter_download?app_id=28549923061320041&sdk_version=207` redirects to the MSI
  (the app id is in the Unity package `com.meta.xr.simulator` on npm.developer.oculus.com), then
  `msiexec /a <msi> /qn TARGETDIR=<dir>` extracts it without installing.
- Lag on purpose (`Xr/LagHook.cs`): `SOJOURN_LAG_CPU_MS=8` (or `4-20`, a random amount each frame) spins the loop
  that long in each headset frame, `SOJOURN_LAG_SPIKE=N:MS` adds MS to one frame in N. For seeing how late frames pace
  (the `VR pacing:` line, frames.csv's display steps).
  `python tools/lagtest.py run [--lag 6-14] [--sim]` is the controlled test for the person in the headset: the local
  grid as Sojourn Lagger (base dir `local/lagtest`, agent API :9793), with a "test lag" row in menu > view (off, 4, 8,
  6-14, 12, 10-20, 20, 30 ms; `SOJOURN_LAG_TEST=1`) beside depth reprojection and "desktop mirror"
  (`SOJOURN_NO_MIRROR=1` off: the frame's submission no longer waits on the window's image); on the agent API also
  `test_wait_gpu`, the jitter fix (above) switched off and on, which is how it was found. Each can be switched in
  seconds; `lagtest.py set lag|wait|mirror|asw|depth VALUE` does the same from outside, and
  `lagtest.py report` lists the run's pacing lines (display steps and how often they changed, the switches in force,
  the head's prediction error, Meta's counters). At 90 Hz, 6-14 ms puts frames between one and two periods.
- `SOJOURN_VR_SCRIPT=walk`: 16 stick walks of 3 s (forward, right, back, left, every fourth turning) with 3 s pauses,
  for measuring how walking feels. `tools/perfrun.py LABEL --vr-sim` runs it on the forest with the logs. The local
  grid answers at once; `SOJOURN_SELF_LAG_MS=150 SOJOURN_SELF_UPDATE_MS=90 SOJOURN_SELF_NOISE_MM=10` make the own
  avatar's position arrive like the main grid's (a round trip late, in steps, a little off each time). Since
  2026-10-27 the steps are made where the avatar is by then (OpenSim's last update carried on by its velocity) every
  SOJOURN_SELF_UPDATE_MS while it moves, as SL sends them; before, the last position was stamped afresh instead, and
  as OpenSim sends nothing during a steady walk, it looked like the same old position arriving again and again (in
  server movement the view stood still while walking and blinked forward at the stop; predicted mode's dead
  reckoning was misled the same way). Figures taken with it before then carry that. A teleport's arrival restarts it
  (2026-10-28): the reply is already the region's word, and a landing under 20 m from the old spot in the old
  region's coordinates was delayed as if it were motion, the arrival's cut going to the old spot and a second cut
  following. A crossing's positions come as late as the rest, read in the new region's coordinates (2026-10-28):
  before, the first was shown at once, a lag's travel ahead of the last (1.9 m at 12.6 m/s), which the main grid,
  where everything comes equally late, doesn't do.
- `python tools/jitter.py [vr.csv ...]` measures the view's steadiness from vr.csv: the room's placement frame by
  frame (the camera minus the head, logged exactly with each frame's dt and the stick). Standing: corrections a second
  (frames it moved over 0.5 mm) and how much, horizontal and vertical. Walking: the speed's variation, jerk (m/s^2) and
  corrections (a frame's velocity off the last by over 0.15 m/s, or backward). After letting go: how far it still
  moves. Cuts and slides putting you back are counted. Older logs work too, reconstructed from the head and yaw
  (to their millimetre rounding).
- Numbers (walk routine, forest, simulator, main-grid-like lag and noise, 2026-10-25), before the predicted walk /
  with it / now: standing, corrections 15.9 / 23.7 / 0.0 a second horizontally (vertical 13-15 / 3.9, smooth glides
  under 3 mm a frame); walking, speed variation 0.78 / 0.25 / 0.05, jerk median 52 / 8.3 / 0.1 m/s^2 (p95 286 / 32 /
  3.2), corrections 34 / 23 / 0.2 a second, backward frames 253 / 0 / 0; after letting go 38-45 cm of motion / 0.
  The second main-grid session's log (the predicted walk, the real network; reconstructed, so its millimetre rounding
  inflates every figure) had centimetre carries of the region's small moves and height in ~7% of standing frames.
- `SOJOURN_VR_SCRIPT=1`: once logged in, a fixed routine laid over the headset's input (`Xr/VrScript.cs`): a 1.2 m
  step forward in the room, the stick forward, a turn, the stick right, fly, climb, land, a 0.8 m step left, the menu
  button (open, then close), X, the left grip (voice), A (the pointer on), then the panel moved with a scripted right controller: dragged
  35 deg right, shown there by X, brought in front by X with the head turned away, dragged out and back onto its
  outline (the console logs "assistant panel: put somewhere" / "following"), A (off). With `SOJOURN_PANEL_IN_EYES=1` and
  `--shot-every 1` the shots show it.
  With `--logs`, `tools/vrlog.py` then shows each follow and any camera motion the head, stick or turns don't explain.
  On the local grid, 2026-10-20: every follow ended 0.1-0.2 m from the head, with no oscillation. There was no
  unexplained camera motion beyond millimetres, apart from the frame each stick release lands on, which the summary
  counts as unexplained (~4 cm, still the stick's motion).
- `SOJOURN_VR_SCRIPT=touch` with `SOJOURN_VR_AIMS="yaw,pitch;yaw,pitch"` (degrees off the room's forward, left and up
  positive): with the pointer on, the right hand taps the trigger on the first aim, holds it there sweeping 8 degrees, taps on the second
  (to sit), then the sticks drive and the trigger is the left button (`Xr/VrScript.Touch.cs`).
- `SOJOURN_VR_SCRIPT=hud`: the HUD board's token, a press, a drag across a HUD, moving the board and × (`docs/hud.md` >
  Testing).
- `python tools/hands.py run [panel|menu|beam]`: scripted hands in front of, through and behind the panel and the
  settings menu (the motion script's `rhand`/`lhand` grips, `panel` putting the panel within reach, `shot NAME`), eye
  shots in `local/hands/shots`; `cost` times the hands' cut on and off at Link's size. Checked 2026-10-27: in front
  the hand shows sharp over both, behind the panel it's hidden (the fingertips above its edge show), pushed through
  the settings menu the fingertips end at its surface, and the pointer's beam shows over the panel from the hand.
- `python tools/motion.py course` / `run NAME...` (wall, slant, backward, steps, step30/45/60, deck, fly, roomwall, sit,
  spectate, point, busy): a course on a floor prim high over Atelier and routines in the simulator
  (`SOJOURN_VR_SCRIPT=motion`, `SOJOURN_MOTION="face 0; wait 2; stick 0 1 4; ..."`, `Xr/VrScript.Motion.cs`: sticks,
  buttons, triggers, room-scale steps, either hand's aim, facing a heading), as Sojourn Mover in `local/motion`, each
  run's vr.csv and console kept there; a summary after each.
- `--shot-every S` saves both eyes (`-eye1.png`) from the headset path too. The assistant's panel is its own quad
  layer, which eye shots don't contain: `SOJOURN_PANEL_IN_EYES=1` draws a preview of it into the eyes as well (don't
  use it with the mirror's shots, which have the layers already: it would show twice there). The OpenXR core validation layer (with
  `--validate`) reported nothing over a simulator session.
- Vulkan validation: Khronos' nightly Windows build of the layer (`gh release download nightly -R
  KhronosGroup/Vulkan-ValidationLayers -p Validation_Layers_Windows_64.zip`) unpacked in `local/vulkan-validation/layer`
  (no SDK, nothing installed); `--validate` turns it on (`client.py` sets `VK_ADD_LAYER_PATH` for that process) and
  its messages go to the console as `[vk ...]`. It stops repeating a message after 10; `VK_LAYER_ENABLE_MESSAGE_LIMIT=false`
  with `VK_LAYER_DEBUG_ACTION=VK_DBG_LAYER_ACTION_LOG_MSG` and `VK_LAYER_LOG_FILENAME=...` logs every one with its VUID.
  First run 2026-10-29 (Claude Opus 5.5): three standing errors fixed (the bindless set disturbed for the second eye
  by the first eye's overlay, the overlay's never-written pictures sampled undefined, the eyes' output cleared without
  transfer-dst use); what's left is a warning about unused vertex attributes when pipelines are made.

## Measured (2026-10-20, dense scene, shadow cap 1536)

- Desktop bench, `--quest-eyes` 2 x 2064x2208: GPU 7.4-9.7 ms per frame over the seven views, CPU 0.7-1.4 ms.
- Simulator, its Quest 3 profile (1680x1760 recommended), 72 Hz: 72/72 fps, GPU 4.3-5.8 ms. At `--xr-scale 1.23`
  (2064x2162): 72/72 once loaded, GPU ~6.2 ms at the arrival view, 0-0.4% frames missed (most when the simulator's
  xrEndFrame stalls for 13-23 ms). The first ~15 s of arriving missed ~10%.
- The simulator's xrEndFrame blocks 5-9 ms (it composites synchronously), so CPU and GPU run in series there. Meta's
  runtime over Link should return quickly; the console's `VR ... fps` line every 10 s shows the time spent handing
  images over. If it's large on Link too, rendering on its own thread (xrWaitFrame for frame N+1 while N finishes) is
  the fix.
- What drops frames on arrival: the simulator's own synchronous compositing (GPU and CPU in series), probe captures
  and CPU spikes in the scene phase. Captures now go two faces a frame (`ProbeFacesPerFrame`), one capture at a time,
  so a capture's 1-4 ms is spread over six frames; terrain surfaces and meshes (10-28 ms each, also on every LOD
  change while walking) now build on the thread pool. Smaller scene spikes remain (`docs/performance.md`).

## Dynamic resolution

On auto (the menu's resolution, `--xr-scale auto`; full size is the default since 2026-10-26, the user's request),
`Xr/EyeScale.cs`: the swapchain is the runtime's recommended size; the eyes render into a corner of it
(`XrSession.SetRenderScale`, the projection views' imageRect) and the compositor scales them to the lenses. They
start at about 4.2 million pixels an eye (Quest 3 over Link at 90 Hz recommends 2448x2672, so x0.8, 1958x2138, the size
that held 90/90 at 6.5-8.7 ms in the dense scene; full size took 14 ms and fell to 45 fps). Once a second the GPU's
mean frame time decides: two seconds above 85% of the display's period shrink the eyes toward 75% of it, six seconds
under 60% grow them a step (at most x1, at least x0.6; changes 3 s apart and at least 2%, each size gets its own
render targets, trimmed when idle). The console logs each change. `SOJOURN_EYE_START=F` starts elsewhere, for testing;
in the simulator, starting at 0.6 grew to 1.0 in steps over 50 s at 71-72/72 fps.

Foveated rendering was built and taken out again (the user, 2026-10-27: it looked too bad at the edges of view).

## Not done yet

- Flying up and down still comes from the region a round trip late (smoothed).
