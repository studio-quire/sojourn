# The headset (phases 2 and 4)

Written by Claude Opus 5.5, 2026-10-20 onward. Agent-facing. Figures were measured in Meta's XR Simulator on the local
grid with main-grid-like lag (Testing without a headset) unless they say otherwise.

## Running it

```
python tools/grid.py start
python tools/client.py vr                 # = run --vr --shadow-cap 1536; add --logs to keep frames.csv and vr.csv
python tools/client.py vr-sim             # the same against Meta's XR Simulator, no headset needed
python tools/vrlog.py                     # summarise local/client/frames.csv and vr.csv after a --logs run
```

The Quest 3 runs as PCVR over Link or Air Link: Meta Horizon Link on the PC, the headset connected before launching.
The system's OpenXR runtime is Meta's (`HKLM\SOFTWARE\Khronos\OpenXR\1` `ActiveRuntime` =
`C:\Program Files\Oculus\Support\oculus-runtime\oculus_openxr_64.json`, checked 2026-10-20; read it from PowerShell,
Git Bash mangles `reg query /v`); SteamVR is installed but isn't the active runtime. Without a runtime, a running
service or a headset, `--vr` logs why and carries on on the desktop (no headset: `ErrorFormFactorUnavailable` at once;
a runtime that doesn't answer gets `--xr-wait` seconds, 20). The headset has to be there at launch: the Vulkan device
comes from the runtime, so switching into VR later would mean a new renderer. If the session ends (quit from the Meta
menu, Link drops), the client carries on on the desktop.

Options (`ClientOptions.cs`): `--vr`; `--xr-scale F|auto` (auto: dynamic resolution, below; a number fixes the eyes at
that scale of the runtime's recommended size; without it the settings' resolution holds, full size by default); `--hz N`
(through XR_FB_display_refresh_rate when offered; 72; Link offers only 90); `--xr-wait S`; `--validate` (also the
Khronos OpenXR core validation layer from `local/openxr-sdk`, and Vulkan's, below). `--quest-eyes` is desktop stereo
with a Quest 3's asymmetric frusta (approximate), to measure the headset's path with `--bench`.

Settings (`Control/VrSettings.cs`) live in `local/client/vr-settings.json` (per machine), changed in the headset's
settings menu and saved at once. Command-line options apply over them for a session and are kept if the menu then
saves: `--seated`/`--standing`, `--snap DEG`, `--smooth-turn`, `--turn-speed DEG/s`, `--hand-relative`, `--no-body`,
`--assistant-model quick|sonnet|opus`. Defaults: standing, your own eye height, smooth turning at 180 deg/s, moving
where you look, server movement, putting you back by teleport, the pointer off, pointing to teleport on, your body
shown, the interface shown, the assistant on quick, the eyes at full size and 128 m draw distance (the user,
2026-10-26: full resolution and 128 m rather than "auto"; a settings file saved before keeps its own). The assistant's
model is set on the desktop too (`docs/assistant.md` > Claude), so it lives here even without a headset. Tests that run
the client against `local/client` write that file too: delete it afterwards.

## Controls

Touch controllers (`Xr/XrInput.cs`, bound for `/interaction_profiles/oculus/touch_controller`). In the headset, the
settings menu's ? shows the controls nothing on screen shows.

| | left | right |
|---|---|---|
| stick | walk (where you look, or where the left hand points) | turn: smooth at 180 deg/s at full tilt, proportional past a 0.15 dead zone, or snap; up jumps; up/down climbs or sinks while flying; with the ray on something that scrolls, scrolls it; with the camera out, zooms |
| stick click | toggles running | open mic: mutes and unmutes voice chat (`docs/voice.md`) |
| X / A | X hides the whole interface and brings it back; held 0.6 s, every menu back to following in front, shown (`docs/assistant-panel.md` > Where it sits) | A: a quick press turns the pointer on and off; held, it shows only while held |
| Y / B | Y recentres (the avatar under you, facing the room's way; seated, also retakes the seated height); held 0.6 s, looks around (a free camera), and Y then brings you back | B toggles flying (looking around: flying or walking on floors) |
| menu (≡) / system | the settings menu (on release); held 0.8 s, the way out: stand up and go home (`Control/WayOut.cs`; Ctrl+Shift+H on the desktop) | the system's own |
| grip | hold to talk in voice chat (push-to-talk); with the assistant's mic tapped open, it sends what it heard first | hold to talk to the assistant; a quick tap (under 0.3 s) leaves the mic open hands-free until the next tap, which sends (`Assistant/AssistantHost.cs` > TalkPress) |
| both grips | held together: dictate words for people (a draft on the panel to send or cancel; `docs/assistant.md` > Incognito); the assistant's mic, if tapped open, sends first | |
| trigger | held on open ground, teleports you there (pointing to teleport) | with the pointer on: touches or sits on what it points at, presses keys on the panel, menu, feed and boards; held still on a thing, its card of what can be done with it (below); held on the panel's body, drags it (`docs/assistant-panel.md`) |

The headset's own recentre (hold the Meta button) is the same as Y. In the mirror window: WASD/arrows, Q/E turn, Space,
C, F, R recentres, Tab the panel, F8 the assistant's model; F3, F6, F12 as on the desktop.

- **Hiding the interface** (X; `InterfaceHidden`, saved; the user, 2026-10-28: to check the mic or anything else, then
  show it again). Hidden: the panel, the settings menu, the chat feed and its notices, the assistant's cards and labels,
  the forearm tokens, the pointer's beam and dots, name tags with their marks, voice's dots, the HUD board, page boards,
  the reader, the keyboard, the aiming crosshair. What was open stays open out of sight and comes back as it was (a
  panel put somewhere now out of view comes up in front, with what waited).
  - Still shown: a confirmation, with the ray to press it (the safety gate holds every other op); the world's own things
    (floating text, media on faces); the comfort vignette if on; pointing to teleport's arc and ring while the left
    trigger is held.
  - Still working: moving, turning, jumping, flying, Y, the way out, the right grip to the assistant (replies, choices,
    requests to point or type and dictated drafts wait on the hidden panel; nothing pops up), voice's push-to-talk and
    mute, dictation, A (the pointer's setting, used once shown), aiming's shots. Nothing hidden takes a ray or a press:
    the right trigger neither touches, sits nor presses, and the right stick turns.
  - The menu button brings the interface back with the menu open. On the desktop Tab does the same (the viewer's HUDs
    stay); Enter, Esc (with the menu open) or F2 bring it back (Enter doesn't type or send a draft out of sight), 1-9
    pick nothing. Also the `interface` setting.
- **The settings menu** (≡; `Control/SettingsMenu.cs`, drawn by `Assistant/PanelMenu.cs` on the panel's quad layer, in
  front of you as it opens): moving has posture (standing/seated), eye height (your own/the avatar's; standing only),
  turning (smooth/snap), turn speed (60-360 deg/s) or snap angle (15-90), moving toward (where you look/left hand),
  movement (server/predicted: The rig > Movement "server"), putting you back (teleport/jump/slide), point to teleport,
  look around, your body, tracking, your gaze; the other sections are in `docs/assistant-panel.md` > The settings
  menu. Every row is also `SojournSettings` on the agent API. Pointed at and pressed with the right trigger; × or the
  menu button closes it. While it's open the assistant's panel waits; a confirmation closes the menu and takes the
  layer.
- **The pointer** (A; `Control/HandPointer.cs`; off by default, saved, also the menu's moving section and the `pointer`
  setting). On: its beam shows, an amber dot marks what it's on ("that" for the assistant, also while talking), and its
  trigger touches and sits, presses the panel, the settings menu, the feed and the boards. Off: nothing is pointed at or
  pressable, except what comes up for the person and needs the ray, which brings it up by itself: a confirmation
  (always pressable; a trigger already held then isn't a press), the settings menu while open, and the assistant asking
  them to point (the trigger then picks the thing for it; `docs/assistant.md` > How it fits together). Why one pointer,
  off by default (Claude Opus 5.5, 2026-10-26, joining the user's two ideas: a toggled pointer, or none until the
  assistant asks): the assistant mostly goes by words and the middle of the view, and pointing still works the moment
  it's on. Why A: jumping is rare in SL, and the right stick's up jumps. The left hand points at nothing.
- **The pointer's trigger in the world** (`Control/DirectTouch.cs`, given the right hand by `HandPointer.World`; the
  user, 2026-10-25: the person's own hand, an exception to acting through the assistant, as the menu and confirmations
  are): what the viewer's left click does. An object whose click action is sit seats you (seated, another one moves you
  there); anything that takes touches is touched where the ray lands, held while the trigger is (a drag updates the
  touch); pay, buy, open and media click actions do nothing here (they're on the thing's card, below). Each pull is also the left mouse button for scripts,
  and only that when a script has taken it. Pointed at something it would act on, the beam ends in a pale dot (touch)
  or ring (sit) beside the amber one. Dots in the world keep one size to the eye up to 2 m, then ease to half by 30 m
  (`Control/PointerDot.cs`; the user's call after three tries, 2026-10-31). Nothing while a confirmation shows, the menu is open or the assistant asked them
  to point (a pull begun then, or on the panel, waits for a release); the ray stops at the panel and the feed. Not
  gated: the touch confirmation guards the assistant's touches, not the person's.
  - On a thing a pull waits to see what it is, as on a page (2026-10-31): let go before it moves 1.6 degrees or is
    held still 0.9 s, it's the click, sent where it went down (touch, sit, left button); moved, the press goes in where
    it began and follows the ray (drags, selections as before); held still, a ring fills round the dot from 0.45 s and
    the thing's card comes up as it closes, the press sending nothing. A script that has taken the left button gets
    the pull at once and nothing waits. The cost: a touch-and-hold script sees a still press only once it moves or is
    let go. Ground, sky and people press at once, as before.
- **A thing's card** (`Things/ThingCard.cs`, the actions in `Sojourn.Session/Objects/ThingActions.cs`; the user's ask,
  2026-10-31, after pressing a free gift and finding nothing happened: the viewer's right click). What can be done
  with that thing, only what applies: buy (with the price, "free" at L$0; a copy or its contents as it's sold), pay
  (its pay buttons, else the viewer's 1/5/10/20), touch (where a tap wouldn't already), sit here or stand up, take a
  copy, take (yours), open (contents, yours or modifiable: listed, copied into a folder named after it), about this
  (owner, creator, description, land impact, next owner's permissions, the sale; opens in place), ask the assistant.
  Its own click action leads. Buy and pay go through the dispatcher's gate, so the confirmation waits for the
  person's press as everywhere (the card steps aside while it's up); the rest is the person's own hand, as the
  trigger's touch and sit are.
  - Over the thing pressed, not where the menus sit (the user): its own quad layer on the line from the eyes to the
    point, 0.15 m in front of it but between 0.45 and 1.4 m away, at the panel's angular size, hanging from just above
    the point and facing you; placed once, then it stays put in the world as a menu does (the user, 2026-10-31: following
    the thing wasn't needed, since moving off closes it).
  - It goes when you forget it (the user's intended feel): its ×, a pull anywhere off it (which goes on to do that
    thing), a teleport or any view cut, moving 2 m from where it opened unless you've come at least a metre closer to
    the thing, the thing leaving view. Turning the head leaves it, and so does the pointer going off (letting go of A
    held for it: the user, 2026-10-31); its keys wait for the ray. Out of sight while a
    confirmation waits or X hides the interface.
  - While it's up the thing is "that" for the assistant whenever the pointer is on nothing else (`Pointer.Pinned`),
    so the grip asks about it too. "ask the assistant" is the talk key on the card: held, push-to-talk; tapped, the
    mic stays open and the key reads "send" (the grip's tap sends too); the card goes once the request is sent.
  - On the desktop a right click on a thing (a quick one, not a drag that steers) brings it up at the click.
- **Beams** take the look's light (`PanelLook.Light`: white in glass and silver, rose in pearl, pale blue in smoke) and
  are drawn fairly opaque (the ray 0.75, other cues 0.55; the user found near-white beams read grey beside the glowing
  panel, 2026-10-28): the pointer's ray, the teleport arc, the crosshair, drag outlines and the boards', keyboard's,
  reader's and world touch's cues. The pointer's dot, the crosshair's held shot (amber) and the hands check keep theirs.
- **On the left forearm**, each pressed with the right ray and trigger: voice's mic token (`docs/voice.md`); the HUD
  token while any HUDs are worn (brings up or puts away the HUD board: the HUD screen in the viewer's 16:9 layout, 60
  degrees wide, 1.25 m ahead; the pointer presses what's on it, and drags it by an empty part; `docs/hud.md`); the stand
  key while seated (below); the notices' bell while any wait, lit while some aren't on the feed (puts them all back on
  the feed, `docs/social.md` > Notices); incognito's token while it's on, with its mark (a hat and glasses; open lenses
  when the assistant may see), which ends it (`docs/assistant.md` > Incognito; the window on the PC shows a dark fill
  meanwhile).
- **The reader** (`docs/assistant-panel.md` > The reader): the right ray and trigger drag its text to scroll, its title strip
  to move it, × closes it; with the pointer on or off.
- **Scrolling with the stick** (`Control/SheetStick.cs`; the user's idea, 2026-10-27). While the right ray is on
  something that scrolls or pages now, the right stick is its: the reader, a long answer's pages on the panel, the
  settings menu's pages, the feed when it holds more than it shows, a page up close, on a HUD or on a face in the world
  (each as the ray already takes it: the reader with the pointer off too, the rest with it on); on either map it zooms
  (up in, 1.8 halvings of scale a second at full tilt; `docs/map.md`).
  - Scrolling: smooth, faster the further it's pushed (past 0.15, up to 1.8 views a second). Paging: a page per flick
    (past 0.6, again once back under 0.3), repeating while held (after 0.45 s, every 0.25 s). Up goes toward the top or
    the page before, as a mouse wheel rolled away does. Left and right page where there are ‹ › keys and scroll a web
    page sideways; on the reader and the feed they do nothing.
  - Meanwhile the client sees the stick centred (no turning, jump, climbing, or E/C while seated). A push belongs to
    where it began until the stick is back in the middle: begun on a sheet, it keeps scrolling if the ray slips off;
    begun turning, it keeps turning when the ray comes onto a sheet. Something that fits doesn't take the stick. No cue
    of its own: the bars and page dots already say a sheet goes on. The desktop's wheel scrolls the feed and the reader.
- **Media** (`docs/media.md`): the pointer's trigger on a page taps it, a drag scrolls it, a still hold that then moves
  presses and drags; on a video it pauses or plays; on media that waits it starts it. A hold kept still for 0.9 s (a
  ring fills round the dot) or a double tap brings it up close on a board (the pointer uses it, × puts it away, the
  trigger on the strip drags it); the HUD board stands aside meanwhile.
- **The keyboard** (`Control/HeadsetKeyboard.cs`; the user, 2026-10-26: optional, easy to put away, only when the person
  asks; it never comes up by itself on focusing a text field). Called up by a keyboard key: on the page board's strip
  while the page has a text field focused, or on the assistant's panel (idle, or beside × on its card) for a typed
  request. The one exception (the user, 2026-10-27): when the assistant asks the person to check a name it can't match,
  it comes up with the guess typed, so ↵ accepts it (`docs/assistant.md` > Checking a word by typing).
  - In front and low like a desk keyboard (0.55 m away, 36 degrees below the eyes, tilted to face you, 0.5 m wide),
    following the head as every menu does (carried past 35 degrees; its strip drags it).
  - Either hand's ray and trigger type (a key fires on the pull; ⌫ held repeats); shift once for a capital, twice for
    caps lock; 123 / abc for symbols; ↵ is Enter (a page's, or sends the request and puts the keyboard away); × or the
    same keyboard key puts it away. A ray on it is its own: nothing else sees that hand.
  - A page gets each key as the desktop's keyboard sends it, nothing kept or logged (a password goes only to the page;
    the assistant never types into one and reads fields without their values). Hidden and taking no ray while a
    confirmation waits or the menu is open, so typing can never reach a confirmation. Chat lines go through the
    assistant ("say ..."). `python tools/keyboard.py` checks both targets in the simulator.
- **The camera** (`Control/Shutter.cs`): menu > you > camera, or the assistant (`photo_camera`), takes it out and puts it
  away (no forearm token: the user found the forearm crowded, 2026-10-29). Out, the two hands held up apart hold the
  picture as a panel between them (its bottom left just off the left palm, top right just off the right), drawn with
  a live preview. While they frame (the camera out, both hands tracked at least 15 cm apart), the right trigger is the
  shutter unless the ray is on a menu, board, key or page, and touching waits; the right stick zooms (0.25x-8x, back to
  1 each time it comes out). `photo_ratio` locks a shape (free by default), `photo_pivot` sets where it's aimed from
  (0.06 m below the eyes, tuned by the user). The picture shows a few seconds above the panel once saved. X hides the
  outline and preview, not the camera. `docs/assistant.md` > Pictures by hand, `docs/rendering.md` > The
  camera's preview.
- **Pointing to teleport** (`Control/PointTeleport.cs`; the user, 2026-10-26). The left trigger pulled shows a beam from
  the left hand; on open ground (terrain, or the top of anything you could stand on that nobody wears) a ring fills on
  the spot over 0.6 s, and letting go once it's full teleports you there. A shorter pull, or one anywhere else, does
  nothing, so it never jumps you by accident. Not while a script has taken the left button, a confirmation waits or the
  menu is open. It's the viewer's double-click teleport (TeleportLocationRequest, into a neighbouring region too),
  landing rules included: a parcel's landing point still wins (TPV policy 2.k). Seated, it stands you first.
  `point_teleport` turns it off.
- **Looking around** (`Control/VrPilot.Spectate.cs`; the user, 2026-10-26: a spectator mode, the viewer's free camera:
  fly and noclip). Y held 0.6 s, the menu's "look around", or the assistant (`look_around`, not kept between sessions)
  starts it; Y, the same switch, or the avatar going elsewhere (a teleport, a sit or stand) ends it with a cut back onto
  the avatar, facing its way (the user, 2026-10-28).
  - You start flying, through anything, the ground included; the left stick goes where you look (or the left hand
    points), up and down too, sideways staying level, at 6 m/s (15 with the stick's click); the right stick up or A
    climbs, down sinks; B lands you to walk on whatever floor is under you (through walls still) and back. Turning as
    ever. Your avatar stands where it was, drawn whole; you have no body (the pointer still works). Nothing reconciles.
  - You stay within 256 m of the avatar (as the viewer's `DisableCameraConstraints` lifts its 50 m limit). The camera
    goes to the region in every AgentUpdate as the viewer's free camera's does, so what's around where you look loads;
    scripts read it only with PERMISSION_TRACK_CAMERA. Voice hears at the eyes within 50 m of the avatar
    (`docs/voice.md`); world sounds and media are heard where you are. TPV policy: the viewer's alt-cam and flycam do
    all this (2.k). Pointing to teleport takes the avatar there and ends it.
- **Seated, or while a script keeps movement controls** (`Control/VrPilot.Seated.cs`): the sticks are the viewer's keys
  for vehicles, horses, boats and games. Left stick forward/back is W/S (CONTROL_FWD/BACK), left/right A/D
  (CONTROL_ROT_LEFT/RIGHT), right stick up/down or A is E/C (CONTROL_UP/DOWN); the right stick still turns you, and the
  view rides what you sit on (The rig > Moving things).
- **Standing up**: while seated, a key with an up arrow sits on the left forearm (`Control/StandKey.cs`); the ray and
  trigger on it stand you up, on anything, as the viewer's Stand button. On something that hasn't taken the movement
  keys (a chair, a sill, a pose ball), pushing the left stick most of the way for a third of a second stands you up too
  (that push doesn't walk until let go). Vehicles and games keep the stick: the key, the way out or the assistant.
- **Aiming** (`Control/VrPilot.Aim.cs`, `Control/Aim.cs`; `docs/safety.md` > Aiming): while a script holds the
  mouselook button (guns, combat HUDs, shooting games; `aim` auto, always or off), the region is told the headset is in
  mouselook, the body and head turn to the aim (the head's forward, or the right hand's ray with `aim_with` hand), the
  right trigger is the mouselook button (not on a menu, board, key or the panel) and a crosshair sits where the aim
  lands; touching waits meanwhile. Menu > safety.
- **Comfort** (`docs/safety.md`; comfort aids are options, off by default: the user, 2026-10-27): the comfort vignette,
  once on, darkens the view's edges while something else moves you (`comfort_vignette` forced: a push, a seat or
  vehicle, a moving floor; all: the stick's walk and smooth turns too), a layer locked to the head under the menus.
  Keep me in place (`keep_in_place`) holds the room still when you're pushed and the follow walk brings the avatar
  back. The way out also releases controls a script took. Menu > safety.

## How it fits together

- `Xr/XrRuntime.cs`: instance and system (XR_KHR_vulkan_enable2, XR_KHR_win32_convert_performance_counter_time,
  XR_EXT_local_floor, XR_FB_display_refresh_rate when offered). It's the renderer's `IVulkanCreator`
  (`VulkanSetup.Creator`): the runtime makes the Vulkan instance and device from our create infos and picks the GPU;
  Silk.NET's `CurrentInstance`/`CurrentDevice` are set by hand after that. The loader is the Khronos `openxr_loader.dll`
  1.1.65, vendored in `src/Sojourn.Client/native/`.
- `Xr/XrSession.cs`: the session state machine (begin at Ready, end at Stopping, back to the desktop at Exiting or loss),
  the play space (local-floor, else stage, else local with an assumed 1.6 m eye height), one swapchain of two layers (an
  eye each), the frame loop. Eyes are rendered at the swapchain's size and copied bit for bit (`vkCmdCopyImage`) from
  the renderer's R8G8B8A8_UNORM output, already gamma-encoded, into an R8G8B8A8_SRGB swapchain, so the compositor
  decodes them right. Swapchain images are in COLOR_ATTACHMENT_OPTIMAL when acquired and handed back that way.
- The mirror window (`Window/MirrorTarget.cs`; both eyes side by side, mailbox present, never vsync: the headset paces
  the loop) shows the menus as the headset does, so someone at the PC can follow along (the user, 2026-10-27):
  `Render/Realtime/LayerMirror.cs` draws every quad layer the frame shows over each eye's part, its corners projected
  through that eye's pose and field of view, in EndFrame's order and parts (Menus over menus), then the hands' layer
  over them, from the layers' own mipped copies (`QuadLayer.MirrorPicture`, ~1.33x the memory of what's up). Its cost is
  negligible (0.015 ms a frame). Incognito blanks the window (`Presenter.Blank`) and nothing is drawn for it.
  `mirror_menus` (menu > view > menus on desktop, on by default) turns it off. A motion routine's `shot NAME` and F12
  also save the window as it shows (`NAME-mirror.png`).
- The loop (`Program.cs` `HeadsetFrame`, main thread): wait for the frame (`XrSession.WaitFrame`: xrWaitFrame, the eyes
  and controllers located at its predicted display time), dead-reckon to that time (`MotionSmoother.BeginFrame`), place
  the eyes (`Control/VrPilot.cs`, the rig), set the layers, then commit what the frame shows (`XrSession.Commit`: the
  quad layers' pictures and poses, the cards, blackout, depth, the panel on top) into its `XrFrame` and post it to the
  render thread, which begins it (xrBeginFrame), renders, and has it ended with the eye poses it was located with. The
  canonical pipelined loop: the main thread's xrWaitFrame for frame N+1 returns once frame N has begun, so simulating
  N+1 overlaps recording and submitting N; and a third thread ends frames (The rig > Overlap). Three `XrFrame`s rotate;
  quad layers keep a pending spec (main) and a frame's spec (render); the cards' layers are made and let go on the
  render thread. Before xrEndSession the main thread drains the render thread (`BeforeEnd`).
- Renderer side: `EyeSpec` carries a full eye pose relative to the camera and asymmetric tangents. Headset eyes cull
  with one frustum (the widest tangents, its apex pulled back to contain both eyes). Shadow fitting, LOD and probes use a
  symmetric centre view as wide as both eyes (detail levels by the viewer's default 60 degree camera angle, not the
  lenses'). The near plane is 2 cm (`docs/rendering.md` > The headset's near plane). Forward and alpha passes render
  both eyes at once (multiview), the rest an eye at a time (`docs/rendering.md` > Two eyes); `XrSession.RecordDepth`
  copies the eye's layer.

### The rig: room, avatar, camera

`Control/VrPilot*.cs`. The avatar's position is the region's; the camera mustn't wait on it or follow it jerkily. The
play space is placed in the region by an origin, a yaw and a floor height. The eyes are wherever the headset puts them
in it; head tracking is the runtime's, local and immediate. Only the rig moves the placement.

The user's decisions:
- **Smoothness over accuracy** (2026-10-25, after the view and walking were found jittery on the main grid): Sojourn
  is for talking and moving about, not combat. A big gap (a teleport, a sit, a push, a crossing) is one clean move, and
  the region's millimetres to centimetres of settling never reach the view.
- **Server movement by default** (2026-10-27, after trying both): the view rides the region's avatar. Predicted
  movement, where your own walk stays local and the region only slows it, is the option.
- **Cuts, not blinks** (2026-10-28): every move of the view to a new spot at once (teleports, putting you back,
  crossings' and seats' jumps, a teleport's height, the free camera's return, a drop of over a metre) is a straight
  cut, no black. Don't bring blinks back: a 90 ms blink of black at 30-45 fps was a few solid black frames each time,
  which the user saw as black frames. A fade may come later as a comfort option, off by default; not built.
  `VrPilot.TakeCut` records each: the console's `view cut: ...` (flying or on foot, the avatar's speed), the pacing
  line's `cut N`, vr.csv's state `cut` (`blink` in older logs).
- **Bring the server to you, else the person's choice** (2026-10-25, 2026-10-26): when you've moved away from your
  avatar in your room, the avatar is walked to you; when it can't be, teleported to you; failing that, you're put back
  by a jump or a slide, as the person chooses.

#### Moving through the world

- **The stick** (both modes) walks the avatar with AgentUpdate controls relative to its body, which faces where the
  head looks (the user, 2026-10-26: walking backward, a body that kept facing ahead swung round into people):
  forward, back, sideways or diagonally (`AtPos`/`AtNeg`, `LeftPos`/`LeftNeg`, both for a diagonal). The region moves an
  avatar only along those eight, so the body is turned up to 22.5 degrees off the head to make one of them exactly the
  stick's way (`VrPilot.Octant`, held until the head is 30 degrees past it); speeds are learned for each (OpenSim walks
  sideways at 1.9 m/s, the rest at 3.1; the main grid at 3.2). Standing still, the body turns to the head's heading once
  it's 40 degrees off. AgentUpdate carries the head's orientation and the camera pose (the interest list follows it).
- **Movement "server"** (`movement`: server, the default, or predicted; menu > moving; the user's call 2026-10-27, after
  predicted mode held views above the ground and walked off edges the model had as floor). The room rides the region's
  avatar as the renderer draws it (`MotionSmoother`, below) every frame, so the view goes exactly where the region takes
  the avatar, walls, steps, slopes and falls included, with no local model for walking. Kept from predicted mode: the
  floor's spring and never down through a floor (Height, below), the follow walk when you walk in your room (the room
  doesn't ride that walk or its coast, or it would chase you), putting you back, turning, seats.
  - A jump the avatar's velocity doesn't explain (over 0.3 m standing still, 2 m moving or just stopped: a teleport, a
    push) is drawn where the region has it at once (`MotionSmoother.Position`'s snap) and the room carried with it, a
    cut over a metre; one that brings the avatar to you (a teleport to you) isn't carried.
  - Along the way (`VrPilot.AlongTheWay`, both modes; `SOJOURN_ALONG_THE_WAY=0` to compare): an update that only came
    late or early along the avatar's own way (moving over 1 m/s; its step along the velocity between half a metre back
    and the prediction plus half a second's travel or 2 m, under a metre plus 15% across) isn't a jump. A slow region
    pauses its updates and comes back short; taken for a jump, that froze the view and then cut it on.
  - Letting go (`VrPilot.Glide`, not while flying, where it did worse; `SOJOURN_SERVER_GLIDE=0`): the view doesn't ride
    the drawing's run-on past the stop (pulled back afterwards, and a pull back taken for a jump left the view ahead for
    good). It slows evenly to where the avatar will rest (the drawn position at the release carried on for a stopping
    time learned per gait, 0.12-0.17 s on OpenSim with the brake), then eases onto the region's position. Walked off an
    edge mid-stop, it rejoins the drawing over a few tenths of a second.
  - The trade-off: the stick moves the view about a round trip late (~100-200 ms on the main grid), the view runs on
    0.3-0.6 m after letting go at a walk (the region hears the stop half a round trip late), and steadiness while
    walking is only as good as the drawn avatar's smoothing; but you never stand, walk or fall anywhere the region
    doesn't put you.
- **How the avatar is drawn between updates** (`World/MotionSmoother.cs`; Claude Opus 5.5, 2026-10-28, after the user
  found server movement rubber-banding while running). Dead-reckoned by its velocity; at each update, what was drawn
  (position and velocity) less the new trajectory, both taken at the display time of the first frame that shows the
  update, decays critically damped at 6/s (`Settle`). The own avatar's update is dated by its packet's arrival
  (`SelfUpdateGuard.LastArrival`), not by the first frame that sees it. The main grid sends the own avatar's update
  every ~45 ms while it runs, most within 5 mm of the last one's prediction, one in ten 6-10 cm off.
  - Don't blend each update in over a fixed time (the 0.18 s smoothstep this replaced, `SOJOURN_AVATAR_BLEND=1` to
    compare): it turned those centimetres into speed changes (a wobble) and pulled the drawing back when the region
    slowed the avatar. Replaying a real evening's 251 s of running through the decay: the view's median acceleration
    6.2 -> 0.9 m/s^2, backward travel 11.6 m -> 1.1 m. Don't start the decay at the update's arrival either: the first
    frame showing it comes ~50 ms later and jolted (p99 acceleration 143 against 40).
  - Island edges (`MotionSmoother.Edge`): where no region lies beyond an edge (none the session has a circuit to; the
    viewer's clipToVisibleRegions), the region lets an avatar run ~1 m past it, then puts it back exactly 1 m inside,
    still moving; so one moving outward is held at that metre and any other kept inside the edge (else holding the
    stick into an edge cut the view back every ~0.45 s). Flying out over such an edge, the view goes out a way and is
    put back when the region returns the avatar; the user wants no fix (going out can be useful, 2026-10-28), nor for
    the view sometimes falling faster than the avatar (SL's way).
  - Not done, on the numbers: scaling dead reckoning by time dilation (the updates matched their velocity, dilation
    ~1); dropping out-of-order own updates as Alchemy does (none in 6804); smoothing the walking heading against head
    sway (a deadband would make steering sluggish: the user's call); a speed-limited follower for the view (it drifts
    off the region's avatar where velocity and positions disagree, as at an island's edge).
- **Predicted movement's walk** (`VrPilot.Walk`): from the first frame the stick is held, the origin moves at the gait's
  speed (walking, running, flying; learned from the region's own velocity), steadily. The region's positions never move
  it; they only slow it (easing the speed, a quarter-second time constant) once the walk runs over 0.7 m ahead of where
  the region will have the avatar (its position a round trip old, carried on by its velocity), stopping it 1.5 m ahead;
  behind, it catches up at up to a third faster. Walls and steps stop it first (below). Across the walk it drifts back
  at most 0.5 m/s, only past a metre. Letting go stops it at once (and brakes, below). The main grid's round trip was
  ~100-190 ms and updates came every 60-110 ms (2026-10-25).
- **Braking** (`Control/VrPilot.Brake.cs`; the user's call 2026-10-27, after predicted mode threw them off an edge they
  had stopped at; both modes). Stopping is the region's, and the viewer's way to ask is AGENT_CONTROL_STOP (the official
  viewer's "stop moving" key and its autopilot's arrival). From the frame the stick, or the follow walk, lets go, STOP
  goes out with the movement keys clear, held until the region reports the avatar still (at least a round trip, at most
  a second; a crossing's arrival, briefly reporting it still, doesn't count for 0.5 s), and dropped at once for the
  stick, a jump, a seat, controls a script keeps, or the avatar in the air (OpenSim turns a STOP raised mid-fall into
  hovering). On OpenSim a released avatar walks on a quarter second before its physics brakes; STOP brakes it at once,
  and the animation is kept while it's held, so nobody sees a step. Walking let go in predicted mode, the avatar now
  rests 0.08-0.20 m short of the view's stop instead of ~0.95 m past it, and let go 0.2 m from a drop it stays on the
  edge. Don't use a reverse tap (`SOJOURN_BRAKE=reverse`): it stopped 0.6 m short, a step back others see.
  `SOJOURN_BRAKE=off` sends nothing; `SOJOURN_BRAKE_LOG=1` says how each brake ended. The follow walk doesn't start
  while the avatar is in the air (it once walked a falling avatar back over a region's border).
  - The climb (`VrPilot.ClimbStop`, both modes; `SOJOURN_CLIMB_STOP=0`): letting go of the climb while flying stops the
    view's height with it. From the release the floor goes evenly from the view's own height and speed to where the
    avatar is expected to stop (where the region last had it, carried on at its climb rate for a time learned per
    direction, 0.2-0.3 s on OpenSim), then eases onto the region's height once it says the avatar is still (at most
    1 m/s back). Without it the view overshot a 2 s climb by 3-4 m and came back down.
- **Following you in your room**: once you've stopped, a gap over 0.5 m between your head and the avatar (standing, not
  flying, not sitting; 0.8 m in seated mode, so a lean doesn't walk it) is closed by walking the avatar to you: the
  nudge walk (`NudgeAtPos`, the viewer's tapped key, ~1.9 m/s on OpenSim), stopping short by the coast it has learned.
  While following, and while the avatar coasts after a stop (Settling: until the region reports it still and the drawn
  position has caught up), the view doesn't move.
- **Putting you back** (`VrPilot.Teleport.cs`, `Correct`): when walking it there can't work (no progress for 1.5 s, the
  stick walk ended against something, or a flying glide left it over 4 m away), once the region has it still the avatar
  is teleported to where you see yourself (the user's fail-safe, 2026-10-26: the region agrees at once, leaving nothing
  to reconcile). Only within the region and where it's sure to land there: not seated, the region allows direct
  teleports, the parcel there takes teleports anywhere (or is yours; unknown parcels are asked about for next time), no
  wall between the avatar and you (walking into a wall in your room never gets you through it), at most every 5 s; one
  that lands over 3 m off or comes to nothing in 6 s turns it off for the region. Otherwise you're put back on the
  avatar: a jump (a cut) or a slide (critically damped, at most 0.6 m/s). `correction`: teleport (the default), jump or
  slide ("blink" still reads as jump); a teleport's fallback is the jump or slide chosen before. The console says
  `teleporting your avatar to you` or `not teleporting (there's a wall between)`.
- **Walls, steps and the region's word** (predicted movement; `VrPilot.Collide.cs`, `RegionScene.CollideRay`). A local
  walk needs the world's solid shapes locally. Rays along the walk at three heights over the surface walked on (just
  over the step height, the waist, the chest) meet the nearest solid surface: prims by their physics shapes (below; not
  phantom or flexible ones, trees, grass, attachments or what hasn't loaded), and other standing avatars as a box. The
  head stops 0.32 m short and the rest of the step slides along it, as the avatar's capsule does.
  - Steps up to the step height are climbed by the floor as the head goes over them, locally; taller ones are walls to
    the low ray. The step height is learned from the region (0.4 m to start; OpenSim climbs 0.3 and stops at 0.45;
    raised when the region's avatar climbs a taller one, lowered when it's stopped at a lower one).
  - Where the model and the region disagree, the region wins quickly: when it stops the avatar where the model saw
    nothing (a doorway it won't fit, a shape not known yet: its position barely moving along the walk for 0.2 s, its
    velocity low), the walk is pulled back onto the avatar over ~0.2 s and a wall remembered there for 4 s; when its
    avatar goes through something the model called solid (past its plane, not round its end), that prim stops counting
    for two minutes.
  - Thin posts between the three rays are caught only by the region's word. The rays cost 0.02-0.05 ms a frame among
    buildings (`SOJOURN_TIME_RAYS=1`). `tools/motion.py` checks walls and steps against the region (a wall: the view and
    the avatar stopped within 3 cm).
- **Physics shapes** (`Control/PhysicsShapes.cs`, `RegionScene.CollisionTriangles`, `World/PhysicsShape.cs`; asked for
  in both modes). The region collides with a prim's physics shape, not what's drawn, so the model asks for them as the
  viewer does (LLViewerObjectList::fetchPhysicsFlags: up to 256 ids a POST to GetObjectPhysicsData, answered with each
  one's PhysicsShapeType): first any prim a ray has just met, then every prim within 24 m across of the avatar and
  anywhere below it (where a fall lands), nearest first, a second at a time, one request at a time. A failed or refused
  request (a region over its rate answers with a line of text) goes again first after a pause growing from 0.5 to 8 s;
  kept for the session, asked again after 10 minutes. While any wait or failed, the console says every 30 s how many
  are known (`physics shapes: ...`).
  - As the viewer's LLPhysicsShapeBuilderUtil: a child with physics shape none doesn't count. A mesh with a convex hull
    shape is its hull (physics_convex's BoundingVerts); with a prim shape, its decomposition's hulls if it has them,
    else its physics model (physics_mesh), unless it's under 0.5 m in some dimension (the region's thin-triangle rule:
    the hull). Until its type comes, as uploaded (a physics model means prim, none means hull), so a mesh plant whose
    real type is none is a solid hull meanwhile. A prim or sculpt is the hull of its drawn shape (convex hull type) or
    its drawn triangles (prim type). Hulls are worked out once per asset or shape, in its unit space, front faces only.
  - `SOJOURN_PHYSICS_SHAPES=0` turns it off. Checked on the local grid (OpenSim answers GetObjectPhysicsData: a mesh
    arch stopped the view at its hull's face, a child prim with shape none was walked through); not against the main
    grid's answers or a decomposed mesh.
- **Moving things** (`World/MotionSmoother.Prims.cs`, `RegionScene.Motion.cs`; Claude Opus 5.5, 2026-10-27). Vehicles,
  keyframed rides, physical things and spinning signs move between the region's updates as the viewer moves them
  (LLViewerObject::idleUpdate): each update carried on by its velocity, acceleration (with the viewer's half-step
  correction) and angular velocity about the parent's axes, in the region's dilated time, while the region is heard
  from (fading only once the circuit's quiet for 2 s, as the viewer), a second at most past the region's edge.
  llTargetOmega's spin is the viewer's alone, kept across updates that don't turn the object (a colour change doesn't
  snap a wheel back), restarted by ones that do or by a physical object's new spin.
  - Where the viewer snaps to each update, the difference between what was drawn (position, velocity, rotation and its
    rate) and the new trajectory decays critically damped (6/s, ~85% gone in half a second), taken at the display time
    of the first frame that shows the update; jumps over 6 m snap. Don't blend instead (a 0.18 s smoothstep made a 1.4
    m/s keyframed ride's speed swing 0.4-2 m/s). Moving objects and their linksets are placed every frame at the
    frame's display time (only their placement and cull bounds); everything else when the region reports a change.
  - Seated, your avatar rides its seat where it's drawn (`MotionSmoother.Seated`, one answer a frame for the scene,
    your body and the view): the seat's motion carries the room, its turning turns it, the eyes are the sit animation's
    put on this frame's seat pose (`SelfSeatedEyesLocal`), and only a jump last frame's motion doesn't explain (over a
    metre) is a cut. The desktop's camera rides the same.
  - `SOJOURN_PRIM_MOTION=0` turns it off. An object's last update's motion shows as `vel`, `acc`, `omega` in
    TesselObjects' descriptions (zero while it visibly moves means it's moved by script in jumps: steps in every
    viewer). On a keyframed ride the view's acceleration p95 is 27 m/s^2 (snapping as the viewer does: 145); what's
    left is the ride's own corners and frame hitches.
- **What the region does to the avatar**: a jump its own velocity doesn't explain (over 0.3 m standing, 2 m otherwise)
  that takes it away from you (a teleport, a sit, a push) moves the room the same way at once (a cut over a metre); one
  that brings it to you (the region catching up after holding it back) isn't followed. Moving at over 0.3 m/s without
  being asked (a moving floor, a push, the agent API walking it) carries the room along. Sitting, the region moves you.
  Everything smaller is ignored.
- **Turning, crossing, recentring**: snap or smooth turns rotate the play space about the head. Region crossings shift
  the origin into the new region's coordinates. A position still in the old region's (for the frame or two
  LibreMetaverse takes to move it) is held over: one well outside the region, and for 3 s after a crossing one nearer
  where the avatar was in the old coordinates than in the new (walking across, the old coordinates are only centimetres
  past the edge, and that frame was once taken for a 256 m jump and back). Recentring puts the avatar under the head
  and turns the room so looking ahead faces the avatar's way.
- **Teleport arrivals** (`VrPilot.Arrival`, `Hold`, `Arrive`; Claude Opus 5.5, 2026-10-28). A teleport to another region,
  a neighbour too, is an arrival, not a crossing. From the grid's TeleportFinish until that region is current and its
  AgentMovementComplete has come (`TeleportWatch.Arriving`), the agent's position isn't to be trusted: LibreMetaverse
  makes the new region current only after sending CompleteAgentMovement, so for a round trip the position is the old
  region's read in the new region's coordinates, or the reply moves it first while the old region is still current.
  - Meanwhile the view stays exactly where it was, in the old region (its scene, sky and water; Program.cs keeps a
    region the view is in drawn even if the session lets it go first), head tracking and turning as ever, the body
    where it stood; nothing is walked, carried or put back and no AgentUpdates go out. Then one straight cut to the
    avatar where it landed, the room's heading kept, nothing of the old region's walking, seat or putting back carried
    over, the drawn avatar's track started afresh. No time limit: an arrival that never comes ends in a new login (30
    s). A region change that isn't a crossing, with no TeleportFinish heard, is held the same way.
  - A reply that comes after the region's own updates for the avatar carries their newer position
    (`TeleportWatch.WireReply`, `docs/client.md` > Gotchas); its own is where the region placed the agent on arrival.
  - Don't hold in the new region's coordinates: a reply arriving first then cut to a spot of the old region and then
    another, a late one to an unrelated spot, and the new region's sky lit the old place meanwhile.
  - The console says `arriving at X,Y: ...` and `arrived in REGION: the view held N s, ...`; vr.csv's follow column
    `Arriving`. On the local grid, where the reply comes at once, `SOJOURN_ARRIVAL_DELAY_MS` holds it back as a slow
    round trip does (only the reply, so the old body goes early). Checked in both modes, standing and walking: every
    teleport one cut, the head still to the millimetre through the hold.
- **Crossing at speed** (`VrPilot.Crossing.cs`; Claude Opus 5.5, 2026-10-28). The region's word on the avatar is
  unreliable for a moment at a border: OpenSim's first full update in the new region says it stands still at the
  handover point (`SOJOURN_SELF_TRACE=1`, `docs/client.md` > Gotchas), the handover point sits a metre or two off the old
  trajectory, and LibreMetaverse writes it again once connected, a round trip late. Taken as the truth, server movement
  cut back 2-2.4 m at crossings and predicted movement took the still report for a wall and put the view back 21-43 m.
  - So for 0.8 s after a crossing while moving (the stick or the climb held, over 1 m/s): a still report keeps the
    velocity from before (its position taken) until a moving one has come after it. Predicted movement judges no walls
    by the region's word, takes no step back for a teleport and doesn't let the region's lag slow the walk, giving that
    up afterwards at a steady 15% of the speed plus 0.3 m/s (2.2 m/s flying). Server movement rides a follower of the
    drawn avatar, at the region's velocity, easing onto the drawing at most a quarter of the speed plus 0.3 m/s faster
    or slower. The walk's trail and remembered walls move into the new coordinates with the room.
  - A lurch becomes a short change of speed, never a step back, a stop or a cut (flying across at 12.6 m/s, both modes:
    no cuts, no backward travel). Seated crossings (vehicles) go their own way (CarrySeated) and aren't covered.
  - The console says `crossing into X at N m/s` and, when a still report came, `crossing: the region reported you still
    at the handover`. `SOJOURN_CROSSING_FIX=0` turns it off.
- **Height** (`VrPilot.Lift`, `Floor`, `GroundUnder`, `Underfoot`): the room's floor is the ground plus a lift.
  - Wherever the local model (the terrain's heightmap and the prims' tops) has the avatar standing where the region has
    its feet, the ground is local: under the head while the stick walks (hills, kerbs and decks need nothing from the
    network; within a step up or down, a drop waiting for the avatar to fall), and under the avatar otherwise, so
    standing, a lean or a step in your room never moves the floor, even on a slope (the ground under the head glided
    the world a few cm as the head moved). The avatar's small height over the surface is learned slowly, and a moment's
    mismatch while walking (OpenSim's avatar hops on a step's edge) keeps to the model for up to 0.8 s; a jump or a fall
    is followed at once. Landing, the region's own position is tried when the drawn one doesn't match.
  - Where the model doesn't match (flying, jumping, falling, a floor still loading), it's the avatar's feet (agent Z
    minus half the avatar's height, which the region sends as its scale), kept as far from them as the view stood when
    it left the model (fading over seconds), led by the region's climb rate while walking (at most 1.5 m/s of it), and
    held while the avatar steps about away from the head. The floor follows its target through a critically damped
    spring (fed the climb rate off the model) and holds still within 4 cm when nothing moves; over 3 m it jumps (a
    cut), and a teleport's height goes with its cut.
  - Never down through a floor (`VrPilot.Underfoot`, both modes): whenever the view's feet go down, a ray sweeps from
    where they were last frame to where they'd be, under the head and, off the model, under the avatar, so no frame's
    step at any frame rate passes a thin floor; the first surface the model knows (terrain, a prim's top by its physics
    shape, not avatars) holds them. Where the region has its avatar 0.5-4 m under that surface at the same place
    (falling through it), the region wins: the view goes on down and the prim stops counting for two minutes. Floors
    the model doesn't have yet (a mesh not downloaded) land as the drawn avatar does. (Without it the drawing's run-on
    past a landing took the view 2 m through a thin plate after a 10 m fall; now it stops within the avatar's hover.)
  - Landed, the region's height wins (2026-10-27, after the user found the view resting above the ground after falls):
    once the region's avatar holds still (no velocity, its height unchanged for 0.3 s) within 0.8 m of the head, not
    walked by the stick or the follow, the floor is its feet, up or down, through the spring (over a metre down, a cut),
    and whatever held the view higher lets go. Walking, a hold more than a step over the region's avatar while it keeps
    level within 1.6 m of the head lets go after 0.4 s. A prim whose top the avatar stands 0.15-1.5 m under (a plant's
    hull, a mesh whose type hadn't come, a hull over a dip) stops counting as a floor, not a wall, for two minutes; the
    console says which (`the region has you standing ... under the top of object N (...)`). Walking, the view leads up
    a step only onto prims whose physics shape the region has told us; onto others it waits for the avatar to climb.
  - Eye heights: at your own eye height the lift is 0. At the avatar's, it's the avatar's eye height (measured by the
    renderer from its shape's rest pose, `FrameRenderer.SelfEyeHeight`) minus your standing eye height (the highest
    your head has been over the last 20 s). Seated mode: the avatar's eye height minus where your head was when seated
    mode began or you last recentred, so you sit and it stands. A space without a floor starts at the avatar's eyes.
  - Sitting on something in-world (either posture), the camera is at the avatar's eyes where its sit animation puts
    them (`FrameRenderer.SelfSeatedEyes`; 0.55 m over the agent's position until drawn, or with your body hidden),
    moved by how the real head moves from where it was then; as it settles (0.3 s), the head goes onto those eyes and
    the room turns to face the way it sits, a cut. Don't use a fixed offset over the agent's position: a seated agent's
    position isn't its pelvis, and 0.75 m put the view half a metre over the avatar's eyes on a sill.
- **Coordinates**: SL's region frame is Z-up (x east, y north); OpenXR's is Y-up with -z forward: (x, y, z)_xr =
  (-y, z, -x)_sl, quaternions by the same map on their vector part. OpenXR's view space is the renderer's eye space,
  so eye offsets and rotations relative to the head go across untouched.

#### Frames and the compositor

- **Late frames**: each eye's depth can go to the compositor beside its colour (XR_KHR_composition_layer_depth; Meta's
  runtime and the simulator offer it), so a frame that misses its slot is reprojected by where the head moved as well as
  how it turned. `reprojection_depth` (menu > view > graphics > depth reprojection) turns it on: **off by default since
  2026-10-29** (the user A/B'd it swaying by a railing at half rate and saw no difference; and given depth, Meta's
  compositor misorders overlapping menus, below). The depth range handed over is 10 km to 0.1 m (reverse-Z), not
  infinite: the spec allows an infinite nearZ, but a runtime deriving its projection the usual way gets NaN from it.
  See-through faces write no depth, so they move with what's behind them. The console says "depth layers on" when on.
- **Walls and the menus** (Claude Opus 5.5, 2026-10-28; `Render/Realtime/Overlay.Walls.cs`). Meta's compositor, given
  depth, hides a quad layer where the eyes' depth is nearer, but over Link only inside a roughly circular region around
  the view (rewriting the depth as radial distance changed nothing), and the PC runtime has no layer flag for it. So we
  hide menus ourselves:
  - Walls hide menus (the default, the user's choice: menus belong to the world; `menu_frost_test` exact, menu > view >
    wall test): each menu's footprint is carved to far in the depth the compositor gets, so it never hides a menu, and
    the hands' cut (Your own body > Over the menus) takes anything in the eye that's inside a menu's outline and nearer
    than it: walls, rocks and people in front of a menu go on the layer over the quads. The frost is tested at the
    menu's own depth. Not with `SOJOURN_HANDS_OVER_MENUS=0`.
  - `menus_over_walls` (menu > view, off by default): menus always over the world: the frost untested, the footprints
    carved, nothing over the menus but the hands. Carving writes each menu's footprint (4 px wider) as far, except where
    the cut's mask is; behind a menu a late frame's world moves as if far away.
  - For comparing, `menu_frost_test` radial (or head) leaves it to the compositor and tests the frost at the radial
    depth it seemed to use. `menu_frost_bias` scales the frost's distance by 1 + bias.
  - A confirmation is never covered: while one is up, nothing hides the menus that frame, the panel's frost is untested
    and its footprint carved (`OverlayScene.PanelOverWalls`).
  - Through cloth and hair (`handcut.frag`, walls mode): alpha-tested cloth and hair are full of pixel-sized holes, and
    blended faces write no depth, so the cut missed pixels there and a menu's rim showed through torn. The mask closes
    them: a pixel goes with its neighbours when 5 of its 8 show, and a gap up to two pixels across a row or column
    closes when what covers shows on both sides (a straight silhouette has one side only, so it doesn't move). The cut
    also takes what's in front of a menu's plane up to 0.05 rad past its outline (`CUT_REACH`), since a reprojected
    frame moves the eye layers while the quads stay put. Larger blended areas (a sheer veil) still let the menu
    through. `dev.py shot` doesn't show any of this (it leaves the hands' layer out); `tools/mirrorshot.py` does.
- **Menus over menus** (Claude Opus 5.5, 2026-10-28; `Xr/MenuOrder.cs`, `Render/Realtime/LayerCut.cs`). The compositor
  draws quad layers in the order given, with no depth between them, so we decide which menu is over which. Don't sort
  by each quad's nearest point to the head: that isn't what decides which of two sheets is in front where they overlap
  (the panel and a reader near it swapped as the head tilted).
  - Each frame (`XrSession.PlanMenus`), every pair of menus that overlap as seen from between the eyes is ordered by
    geometry: the part of one on the eyes' side of the other's plane is in front of it; coplanar ones by their stack (a
    board's screen over its glass). A topological sort of those constraints is the order, farthest-first deciding only
    where nothing constrains it. Exact from any pose for menus that don't meet (61412 rays over 3000 random scenes all
    matched the nearest hit).
  - A menu another passes through (the following panel pushed into a reader left in the room) goes in parts: cut along
    the other's plane, each part its own quad layer showing only its side, laid under and over the other; exact from
    any pose, both eyes. A part is the picture copied into a swapchain of its own with the rest cleared by a
    one-triangle pass, antialiased so the two parts' edges add up (`layercut.frag`); a CPU picture stays on the GPU
    while cut. Parts' swapchains go after ~3 s unused. A cycle of sheets that don't meet is broken by a cut too
    (Newell's painter's algorithm); at most three planes a menu and eight a frame, past which the old order decides.
  - Cost: the order ~2 us of the render thread a frame; parts, only while menus pass through each other, a copy and a
    clearing pass each and up to three swapchain images of the menu's size. The mirror window and `dev.py shot` lay the
    same parts. `SOJOURN_MENU_PARTS=0` orders whole layers only.
  - **The compositor's depth**: given depth, Meta's compositor seems to test quads against each other too (a part's
    cleared side counting as solid, and with whole menus merely overlapping, a menu's glass vanishing inside a circle
    around the view, with our carving and hands' layer ruled out). So no depth goes to the compositor while any two
    menus overlap (`MenuOrder.AnyOverlap`; late frames reproject by rotation alone meanwhile, and the pacing line counts
    them, "held back in N"), and the depth itself is off by default (Late frames).
  - Frost through menus: a menu behind another is seen through the front one's glass (tinted, sharp), never blurred by
    its frost; that would take a frost of its own between the two layers, per eye. Not built.
- **Late frames and the runtime**: each frame is rendered for the pose xrLocateViews gave at xrWaitFrame's predicted
  display time and submitted with that same pose and time; the rig only places the play space, a rigid move, so the
  compositor's reprojection is always consistent with the image. Swapchain images are acquired, waited, written in the
  frame's last submission and released once the queue has finished it (the jitter's fix, below), on the queue given to
  the runtime (nothing else submits to it). With the GPU over the period (90 Hz: 11.1 ms) Meta's runtime drops the app
  to half rate; later still (over 22 ms) an older frame stays up, reprojected, for another slot or two, which shows as
  an older frame flashing on head movement.
  - The console's `VR pacing:` line every 10 s: display steps (1 on time, 2 half rate, 3+ late) and how often the step
    changed, frames ended after their display time, how far ahead of the display time xrWaitFrame woke us
    (frames.csv's last column per frame), frames that showed no scene or a grey void, cuts (`cut N`), xrWaitFrame's hold
    and the time from waking to xrEndFrame, whether depth went to the compositor, GPU frames over the period, the
    costliest GPU passes, and the overlap's figures (below); with the head tracked, how far the head as rendered was
    from where the runtime later said it was (degrees and mm, mean/p95/max) and how much the runtime's prediction moved
    between waking and xrEndFrame.
  - The `VR ... fps` line: whether the desktop mirror was on, how often its window image wasn't free at submit (the
    frame's submission then waits on the 60 Hz window), and the render thread's part in handing images over (waiting
    for the last frame's end, acquiring). Meta's runtime adds a `VR runtime:` line (XR_META_performance_metrics:
    compositor dropped frames, its CPU and GPU time, motion-to-photon latency).
  - Ctrl+Numpad1 turns ASW off on the PC runtime (Ctrl+Numpad4 back to auto), for telling its synthesis from our frames.
- **The jitter, and its fix** (2026-10-27): under GPU load, a fast, fine shake of the whole view, like repeated previous
  frames or frames from a previous head position; worse at medium lag than heavy, unchanged by ASW or depth. (A lower
  or uneven frame rate, stutter, is a different thing.) Fix: `XrSession.WaitGpuBeforeRelease`, always on: after the
  frame's submission the queue is waited idle (`vkQueueWaitIdle`) before its swapchain images are released and the
  frame ended. The console says so at start; `SOJOURN_XR_WAIT_GPU=0` turns it off for diagnosis (the pacing line then
  says "NOT waiting"). Likely cause, not proven: Meta's compositor reading an eye image before our writes had finished,
  so it showed the image's older contents corrected to the new head pose (a 2021 report on Meta's forums describes the
  same for Vulkan apps under load); which side's sync is at fault is open. The studio's `docs/vr.md` has the general
  lesson. It was found with `tools/lagtest.py` (Testing without a headset) after the frame loop, the rig, depth, ASW and
  cadence had been ruled out. Cost: a CPU wait for the GPU each frame, taken off the render thread by Overlap (below).
  A cheaper form is to be checked in the headset against the same symptom before it replaces this. A standalone
  reproduction for Meta was dropped (the user, 2026-10-27); the workaround is to be published instead
  (`docs/public-note-draft.md`).
- **Overlap** (Claude Opus 5.5, 2026-10-28; `XrSession.Overlap`, on; `SOJOURN_XR_OVERLAP=0` back to frames in turn).
  With the wait on the render thread, CPU recording and GPU work added up (GPU 10.8 ms, yet a steady 45 fps, which
  shimmers while walking). So a third thread, frame end, does the wait, the releases, xrEndFrame and the mirror window's
  present, while the render thread builds and records the next frame. That frame is split (`IFrameTarget.TrySplit`,
  FrameRenderer): its scene goes into one command buffer with the eyes' depth, the menus' walls and the hands drawn into
  XrSession's own two-layer images (each eye finishing into an output of its own); xrBeginFrame for it comes from the
  frame-end thread right after the last frame's xrEndFrame (or from the render thread if it took the frame later); once
  the last frame has ended the scene is submitted, then the swapchain images are acquired and a second buffer copies
  everything in (`RecordOutput`), carrying the slot's fence.
  ```
  render:    [scene+record N+1] ...wait... [submit scene][acquire, output N+1, submit][scene+record N+2] ...
  frame end:  [GPU wait N ........][release, xrEndFrame N, xrBeginFrame N+1][window]  [GPU wait N+1 ......]...
  GPU:       [N scene][N output]   gap  [N+1 scene .........][N+1 output]   gap  [N+2 ...
  ```
  - The jitter fix is untouched: the next frame's work is only submitted after xrEndFrame, so the queue is idle at every
    release and end. Don't submit the next scene before the last frame is released: the spec has the runtime treat
    released images as written by queue work still pending, so whatever it queues at release would wait behind it.
  - A frame whose recording fails is begun and ended anyway (`XrSession.Abandon`), so the main thread's xrWaitFrame
    returns and the failure reaches it at the next hand-over instead of freezing the headset; the GPU wait's device
    loss is thrown as the renderer's (`VulkanException`, for the restart-on-lost-GPU path). A frame that can't split
    (no multiview, the loading frame) still ends on the frame-end thread but records after the last has ended.
  - One lock (`VulkanContext.QueueLock`) covers every use of the queue, OpenXR's calls that may touch it included. The
    HUD and page layers' visibility is taken when their image is copied (render thread).
  - Cost: the GPU idles between frames for the hand-over; copies of the eyes' depth (always) and hands (while menus are
    up), ~0.1 ms each per eye pair; ~90 MB of video memory at the Quest's size (`RenderTargets.OutputPerEye`). The
    pacing line's `overlap:` part: frames split, how many had their scene ready before the last had ended, the render
    thread's wait for those ends, the frame-end thread's GPU wait and release-to-xrEndFrame; `idle between frames` is
    the GPU's gap.
- **Frame synthesis off** (`Xr/MetaAsw.cs`; the user, 2026-10-26 and for good 2026-10-28: it brought the jitter back
  each time it was tried). Sojourn turns Meta's ASW off for its headset session through the runtime's Debug Tool CLI
  (`OculusDebugToolCLI.exe`, `server: asw.Off`) and back to auto when the session ends, so a slow frame is reprojected
  rather than the app locked at half rate with every other frame synthesised. No setting. Only on Meta's runtime with
  its CLI installed; the console says which it did. The runtime keeps the setting until it restarts, so it goes back
  to auto however Sojourn ends: a marker (`%LOCALAPPDATA%\Sojourn\asw-off.txt`, the process id) and a hidden PowerShell
  watcher that restores it if the process dies; a clean end, exit or crash restores it first; a start that finds a dead
  process's marker restores it too. Hitches over two periods (CPU spikes, GC) still show an older frame:
  `docs/performance.md`.

### Your own body

Others see the region's avatar; the headset draws its own copy (`SelfBody`, `Avatars.PoseSelf`), with the region's
animations:
- Standing, its eyes are put under the camera, 8 cm back along its facing (`SOJOURN_BODY_BACK`), so looking down shows
  the chest and feet; its feet stay on the ground. At your own eye height it's scaled about its feet so its eyes are at
  yours (others still see its real size). It faces the way the region's avatar does, smoothed. Sitting, it stays on its
  seat.
- The neck and everything above it shrink to a point, and nothing worn on them is drawn (hair, hats, glasses): no
  inside of a head in your eyes. Its shadow is headless too.
- Arms: two-bone IK from the shoulders to the controllers' grip poses, elbows hanging down, out and back. Fingers keep
  the animation. The hand (`VrPilot.Hand`): OpenXR's grip -Z runs along the tube the closed fingers make, not along the
  hand; the hand's bones run where Meta's runtime aims the controller, 60 deg from the grip's -Z toward its -Y (the aim
  is the grip turned 60 deg about its X); palm in (+X on the left, -X on the right), thumb up the tube; the wrist joint
  7.5 cm back along the bones from the grip's centre and 3 cm toward the back of the hand. When a controller is beyond
  reach (SL arms are short, and the body stands 8 cm behind the eyes), the upper arm and forearm stretch along their
  bones, up to 1.4x, so the hand still reaches it.
- Hands and animations (`tracking`: auto, on, off; menu > moving; the head too wherever it comes to be shown). Auto, the
  default, lets the arms follow the animations while something around you animates you: an animation started by an
  object you aren't wearing (a dance machine, a pose ball, a seat's own sit) or one the agent API played for you
  (`Core/Agents/SelfAnimations.cs`, checked four times a second); the hands come back when it stops. Not for the
  region's own standing, walking and sitting, nor your AO or other attachments (a dance HUD you wear counts as that: off
  lets its dances move your arms). Why automatic: a dance or a pose is something you chose that moves the arms, an AO's
  stands are background the tracked hands should win over, and the animation's source tells the two apart without a
  press. The head isn't posed from tracking (it's hidden in your view; others see it turn by the look-at, below).
- Whole-body one-shots (a bow, a gesture's) play on this body, but the eyes stay under your real head, so from them it
  mostly bends below you; a mirror shows it. One-shots on your own avatar are stopped once played, as the viewer stops
  them (else the region keeps them listed and the same one again does nothing).
- **Over the menus** (the hands over the menus; Claude Opus 5.5, 2026-10-27, the user's decision after a finger over the
  panel showed as a brown splotch): your own body covers every menu where it's in front of it, per pixel. A finger in
  front of a menu shows sharp over it, a hand reaching behind one stays hidden, a finger pushing through shows up to
  where it crosses the surface. The menus are the compositor's quad layers, over the eye images, so the hands go up
  again on a layer above them, cut by each menu's plane (and walls, in walls mode: The rig > Walls and the menus):
  - The own body (`SelfBody` and what it wears; `InstanceFlags.Self`) raises the G-buffer's flag a little, inside the
    lighting's tolerance (`GBUFFER_SELF_MARK`). Per eye, after the scene, a mask (`Overlay.Hands.cs`, `handcut.frag`,
    `handcut.glsl`): 1 where that flag is set and the pixel, from the eye's depth, is inside some menu's outline and
    nearer than every menu whose outline it's inside. The beams, dots and tokens drawn within 0.4 m of a controller join
    the mask by their own depth, so they show over the menus with the hand that holds them.
  - A menu held at a hand (`XrSession.HeldCards`: the location line at the left wrist and the mini-map, `docs/map.md`) is
    left out of the cut and the order and composited last, over the hands' layer, its frost untested: nothing covers
    it. While one shows the compositor gets no depth (it would hide the layer behind the hand).
  - The frost leaves the mask's pixels alone and its backdrop has them taken out before the blur, so the hand doesn't
    bleed into the glass around it. `SOJOURN_PANEL_IN_EYES=1`'s previews of the menus leave them out too.
  - The eye where the mask is, premultiplied, goes into a swapchain of its own, submitted as a second projection layer
    after the quads with the eyes' poses, display time and depth, so a late frame reprojects it as the eyes are.
  - The passes run only while a menu shows, only over the menus' footprint: ~0.12 ms a frame at Link's full eye size
    (`tools/hands.py cost`). `SOJOURN_HANDS_OVER_MENUS=0` turns it off; the console says "the hands go over the menus"
    when the layer first goes up.
- Checking without a headset: `SOJOURN_SELF_TEST=orbit` (a camera circling the avatar) or `down` (from its eyes) on the
  desktop, with the hands tracing circles; `--shot-every` saves the views. In the simulator, `SOJOURN_SELF_TEST=hands`
  (`Control/HandsCheck.cs`) films your body from in front, then from its left, every 6 s, each controller marked (a dot
  at the grip, a short line along its -Z, a long one along its aim, a dot where the wrist was asked to go): the hands
  should close around the dots. `watch` looks from the eyes at the left wrist. `tools/hands.py` checks the hands over
  the menus (Testing without a headset).
- Not done: the body doesn't lean or crouch with you (it slides under the head), legs don't step with room-scale
  walking (the region's walk plays while it follows), no finger poses from the controllers' touch sensors. In a dynamic
  mirror the body is drawn whole (`docs/rendering.md` > Mirrors).

### Where you look, seen by others (Claude Opus 5.5, 2026-10-26)

The user chose head direction, with a toggle, from `docs/shared-tracking.md`'s options. `Control/Gaze.cs` sends the
viewer's own look-at (a ViewerEffect of type LookAt, packed as `LLHUDEffectLookAt::packData`), so every viewer turns
your avatar's head, neck, a little of its torso and its eyes that way (`LLHeadRotMotion`, `LLEyeMotion`; no tilt, at
most 72 degrees off the body, under any animation that moves the head at priority 2 or more):
- The view's direction, smoothed over 0.08 s, as an offset from your own avatar (`LOOKAT_TARGET_MOUSELOOK`, no
  timeout): what the viewer sends in mouselook. The desktop's first person sends the same; its third person sends
  nothing.
- Eye contact: when someone's head stays within 10 degrees of the middle of your view for 0.3 s (within 20 m), that
  person (`LOOKAT_TARGET_FOCUS`), held until they're 18 degrees off for 0.25 s. Every viewer then aims your head at
  theirs, which reads right even where your avatar's head isn't quite where your eyes are.
- Looking around (spectating) holds the head where it was when it began (the user, 2026-10-26: the view isn't your real
  head then); eye contact becomes the direction it was in. Off still clears.
- The viewer's rate: at most four sends a second, only when the target changes or the direction moves more than 0.05
  (about 3 degrees); one effect id a login. Turned off it sends a None (the viewer's clear), then nothing.
- The setting (menu > moving > "your gaze": eye contact, direction, off; `gaze`), saved. Eye contact is the default:
  the viewer's defaults send cursor and focus targets naming people too, and eye contact is most of what makes a head
  readable. Direction is the viewer's privacy idea: the way you look, never who or what. `SOJOURN_GAZE_LOG=1` logs each
  send.
- Receiving (`Render/World/Avatars.LookAt.cs`, `Bodies/Animator.cs` `Look`): other people's look-at turns their heads
  in Sojourn as in the viewer (`calcTargetPosition` from last frame's pose, the head and eye motions ported, eased in
  and out over a second; eye jitter and blinks aren't drawn). Your own isn't shown on your own avatar.
- Checked on the local grid with a desktop client watching (`SOJOURN_VR_SCRIPT=motion`, `face`): the head met its eyes,
  turned each way and eased back when turned off. The simulator's head stays level, so pitch is checked only on paper.

## Testing without a headset

- **Meta XR Simulator** (an OpenXR runtime with a simulated Quest 3, in a window), unpacked in `local/xrsim/v207.1`
  (873 MB, not installed; it doesn't touch the registry). `tools/client.py vr-sim` points `XR_RUNTIME_JSON` at it for
  that process only. It keeps settings in `%APPDATA%\MetaXR\MetaXrSimulator`. It looks for its companion app for ~30 s
  first (`Cannot connect to a companion app`), then runs without it; hence `--xr-wait 90`. To get it again:
  `https://www.facebook.com/horizon_devcenter_download?app_id=28549923061320041&sdk_version=207` redirects to the MSI
  (the app id is in the Unity package `com.meta.xr.simulator` on npm.developer.oculus.com), then
  `msiexec /a <msi> /qn TARGETDIR=<dir>` extracts it without installing.
- **Main-grid-like lag**: the local grid answers at once; `SOJOURN_SELF_LAG_MS=150 SOJOURN_SELF_UPDATE_MS=90
  SOJOURN_SELF_NOISE_MM=10` make the own avatar's position arrive like the main grid's (a round trip late, in steps, a
  little off each time). The steps are made where the avatar is by then (OpenSim's last update carried on by its
  velocity) every SOJOURN_SELF_UPDATE_MS while it moves, as SL sends them (OpenSim sends nothing during a steady walk,
  so restamping the last position would look like the same old position arriving again and again); a teleport's
  arrival restarts it, and a crossing's positions come as late as the rest. `SOJOURN_LAG_CPU_MS=25` gives ~24 fps.
- **Lag on purpose** (`Xr/LagHook.cs`): `SOJOURN_LAG_CPU_MS=8` (or `4-20`, a random amount each frame) spins the loop
  that long in each headset frame, `SOJOURN_LAG_SPIKE=N:MS` adds MS to one frame in N, for seeing how late frames pace.
  `python tools/lagtest.py run [--lag 6-14] [--sim]` is the controlled test for the person in the headset: the local
  grid as Sojourn Lagger (base dir `local/lagtest`, agent API :9793), with a "test lag" row in menu > view (off, 4, 8,
  6-14, 12, 10-20, 20, 30 ms; `SOJOURN_LAG_TEST=1`) beside depth reprojection and "desktop mirror"
  (`SOJOURN_NO_MIRROR=1` off); on the agent API also `test_wait_gpu`, the jitter fix switched off and on. Each can be
  switched in seconds; `lagtest.py set lag|wait|mirror|asw|depth VALUE` does the same from outside, and `lagtest.py
  report` lists the run's pacing lines. At 90 Hz, 6-14 ms puts frames between one and two periods.
- **Motion routines**: `python tools/motion.py course` / `run NAME...` (wall, slant, backward, steps, step30/45/60,
  deck, fly, roomwall, sit, spectate, point, busy): a course on a floor prim high over Atelier and routines in the
  simulator (`SOJOURN_VR_SCRIPT=motion`, `SOJOURN_MOTION="face 0; wait 2; stick 0 1 4; ..."`, `Xr/VrScript.Motion.cs`:
  sticks, buttons, triggers, room-scale steps, either hand's aim, facing a heading, `shot NAME`), as Sojourn Mover in
  `local/motion`, each run's vr.csv and console kept there; a summary after each.
- `SOJOURN_VR_SCRIPT=walk`: 16 stick walks of 3 s (forward, right, back, left, every fourth turning) with 3 s pauses.
  `tools/perfrun.py LABEL --vr-sim` runs it on the forest with the logs.
- `python tools/jitter.py [vr.csv ...]` measures the view's steadiness from vr.csv (the camera minus the head, logged
  with each frame's dt and the stick). Standing: corrections a second (frames it moved over 0.5 mm) and how much.
  Walking: the speed's variation, jerk (m/s^2) and corrections (a frame's velocity off the last by over 0.15 m/s, or
  backward). After letting go: how far it still moves. Cuts and slides are counted. Older logs work too, reconstructed
  from the head and yaw (to their millimetre rounding).
- `SOJOURN_VR_SCRIPT=1`: once logged in, a fixed routine over the headset's input (`Xr/VrScript.cs`): a step forward in
  the room, the stick forward, a turn, the stick right, fly, climb, land, a step left, the menu button (open, close), X,
  the left grip, A, then the panel dragged with a scripted right controller and brought back by X (the console logs
  "assistant panel: put somewhere" / "following"), A again. With `--logs`, `tools/vrlog.py` then shows each follow and
  any camera motion the head, stick or turns don't explain (the frame each stick release lands on counts as ~4 cm
  unexplained; it's still the stick's).
- `SOJOURN_VR_SCRIPT=touch` with `SOJOURN_VR_AIMS="yaw,pitch;yaw,pitch"` (degrees off the room's forward, left and up
  positive): with the pointer on, the right hand taps the trigger on the first aim, holds it sweeping 8 degrees, taps
  on the second (to sit), then the sticks drive and the trigger is the left button (`Xr/VrScript.Touch.cs`).
- `SOJOURN_VR_SCRIPT=hud`: the HUD board's token, a press, a drag across a HUD, moving the board and × (`docs/hud.md` >
  Testing).
- `python tools/hands.py run [panel|menu|beam]`: scripted hands in front of, through and behind the panel and the
  settings menu, eye shots in `local/hands/shots`; `cost` times the hands' cut on and off at Link's size. Expected: in
  front the hand shows sharp over both, behind the panel it's hidden, pushed through the settings menu the fingertips
  end at its surface, and the pointer's beam shows over the panel from the hand.
- `--shot-every S` saves both eyes (`-eye1.png`) from the headset path too. Eye shots don't contain the quad layers:
  `SOJOURN_PANEL_IN_EYES=1` draws a preview of the panel into the eyes as well (not with the mirror's shots, which have
  the layers already). `dev.py shot` composites the layers itself; `tools/mirrorshot.py` captures Meta's own mirror.
- **Validation**: `--validate` turns on the OpenXR core validation layer (it reported nothing over a simulator session)
  and Vulkan's: Khronos' nightly Windows build (`gh release download nightly -R KhronosGroup/Vulkan-ValidationLayers -p
  Validation_Layers_Windows_64.zip`) unpacked in `local/vulkan-validation/layer` (`client.py` sets `VK_ADD_LAYER_PATH`
  for that process), its messages in the console as `[vk ...]`. It stops repeating a message after 10;
  `VK_LAYER_ENABLE_MESSAGE_LIMIT=false` with `VK_LAYER_DEBUG_ACTION=VK_DBG_LAYER_ACTION_LOG_MSG` and
  `VK_LAYER_LOG_FILENAME=...` logs every one with its VUID. Clean but for a warning about unused vertex attributes when
  pipelines are made (2026-10-29).

## Measured (2026-10-20, dense scene, shadow cap 1536)

- Desktop bench, `--quest-eyes` 2 x 2064x2208: GPU 7.4-9.7 ms per frame over the seven views, CPU 0.7-1.4 ms.
- Simulator, its Quest 3 profile (1680x1760 recommended), 72 Hz: 72/72 fps, GPU 4.3-5.8 ms. At `--xr-scale 1.23`
  (2064x2162): 72/72 once loaded, GPU ~6.2 ms at the arrival view, 0-0.4% frames missed (most when the simulator's
  xrEndFrame stalls). The simulator composites synchronously in xrEndFrame (5-9 ms), so CPU and GPU run in series
  there, unlike Meta's runtime over Link. Arriving costs more (`docs/performance.md`).

## Dynamic resolution

On auto (the menu's resolution, `--xr-scale auto`; full size is the default, the user, 2026-10-26), `Xr/EyeScale.cs`:
the swapchain is the runtime's recommended size; the eyes render into a corner of it (`XrSession.SetRenderScale`, the
projection views' imageRect) and the compositor scales them to the lenses. They start at about 4.2 million pixels an
eye (Quest 3 over Link at 90 Hz recommends 2448x2672, so x0.8, 1958x2138, the size that held 90/90 at 6.5-8.7 ms in the
dense scene; full size took 14 ms and fell to 45 fps). Once a second the GPU's mean frame time decides: two seconds
above 85% of the display's period shrink the eyes toward 75% of it, six seconds under 60% grow them a step (at most x1,
at least x0.6; changes 3 s apart and at least 2%, each size with its own render targets, trimmed when idle). The console
logs each change. `SOJOURN_EYE_START=F` starts elsewhere, for testing.

Foveated rendering was built and taken out again (the user, 2026-10-27: it looked too bad at the edges of view).

## Not done yet

- Flying up and down still comes from the region a round trip late (smoothed).
- The body's leaning, crouching and finger poses (Your own body).
