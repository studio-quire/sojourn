# HUD attachments

Written by Claude Opus 5.5, 2026-10-25. Agent-facing. The HUDs people wear (mesh body and head HUDs, AOs, dance and
couples HUDs, games and meters, vendors' HUDs): drawn, touched by the person, and seen and pressed by the assistant.
Code: `src/Sojourn.Render/Realtime/Hud.cs` and `FrameRenderer.Hud.cs`, `Frame/Renderer.Hud.cs`, `World/RegionScene.Hud.cs`,
`Shaders/hud*.{frag,vert}`; `src/Sojourn.Client/Hud/` (HudView, HudBoard, HudApi, HudShots).

## As the viewer has them

Read from secondlife/viewer (develop, 2026-10-25):
- **HUD space**: the attachment points 31-38 hang off `mScreen`, a joint the viewer makes in code (not in
  avatar_skeleton.xml), at the origin with scale (1, aspect, 1). x runs into the screen, y to the left, z up; the screen
  is one unit tall and the camera's aspect wide. A point's offset is scaled by that (Top Right sits at y = -aspect/2,
  z = 0.5, the corner); an object's offset from its point isn't. `Hud.PointFrame`.
- **Camera** (`get_hud_matrices`): orthographic, looking along +x, depth over the HUDs' box; nearer is smaller x. Zoom
  stays 1 except while editing a HUD (Ctrl+0/8/9 are the world camera's FOV, not HUD zoom).
- **Shading**: every non-PBR HUD face is fullbright (texture times tint, in sRGB, after gamma: no sun, sky, fog);
  legacy normal/specular maps, shine and glow don't apply; alpha always blends (never auto-masked), MASK materials cut
  out; PBR is base colour times its factor plus emissive, unlit. Rigged meshes on HUDs aren't drawn. Always the best
  LOD; textures at full resolution (BOOST_HUD).
- **Touch**: the mouse makes a ray along +x at its point of the screen; HUDs are tested before the world. Click actions
  never apply to attachments; a prim is touched if it or its root takes touches. ObjectGrab's surface info for a HUD is
  in HUD space (position, normal, binormal), with face, ST and UV as anywhere; held, ObjectGrabUpdate as for any
  non-physical object, face -1 once the point leaves the prim.
- Alt+Shift+H hides them. Snapshots leave them out. Hover text on HUDs is drawn (llhudtext.cpp, mOnHUDAttachment:
  always, over the HUDs, never faded, at the font's pixel size on the screen, the hover text switch not applying);
  HUD particles are off by default (RenderHUDParticles). A click on a HUD face with media goes to its page, as anywhere.

## What Sojourn does

- **Rendering**: `FrameRenderer.PlanHud` builds one draw list of the person's HUDs in HUD space (RegionScene swaps the
  HUD points' frames in for the call) and draws it with `hud.frag` (the IS_HUD shading above) into its own image: the
  screen at the window's size and aspect on the desktop, 1920x1080 at 16:9 for the headset's board, plus any picture
  the assistant asked for. Desktop: the image is laid over each eye after the assistant's capture and under the overlay
  (UNORM, blended in sRGB as the viewer does). Headset: an sRGB image (linear light, as the compositor blends) copied
  into its own quad layer. About as cheap as a few draws; it's drawn only while it shows.
- **The desktop**: as the viewer: over the whole view; a left click on a HUD is the HUD's (touch, drag while held,
  release), not the world's; the pointer's "that" is the HUD prim under the mouse; Alt+Shift+H. Hidden while a
  confirmation waits (nothing an object made sits beside it).
- **The headset: a board.** (Claude Opus 5.5, 2026-10-25.) A screen doesn't exist in a headset, and a head-locked HUD is
  uncomfortable and always in the way; most HUDs are used now and then (change an outfit layer, an animation, a
  setting) and some are glanced at (meters). So the HUDs live on a board the person calls up:
  - Called up from a **token at the left wrist** (a small disc with a screen glyph, 4.5 cm along the forearm from voice's
    mic token; shown while HUDs are worn): point the right ray at it and pull the trigger; again to put the board away.
    Also by the assistant ("show my HUDs"). × at the board's top right puts it away. Wrist tokens are the VR norm for
    a summoned panel, and every controller button is already taken.
  - **The layout is the viewer's**: a 16:9 screen (the shape most HUDs are laid out on), each HUD at its point's edge or
    corner, so a HUD is where its maker put it and its buttons where people expect them.
  - **Placed** in front of you when called, 1.25 m away, 60 degrees wide (most of a monitor's view, so HUD buttons keep
    about the angular size they have on a screen, ~0.7 degrees for a small one), its middle 2 degrees above the eyes so
    its bottom edge clears the panel resting 18 degrees below them. It follows the head as every menu does (the user,
    2026-10-28; `docs/assistant.md` > The panel > Where it sits): still while you look around it, carried along once
    you turn past 40 degrees (it's 60 wide: a glance at a HUD in its corner doesn't move it) or step away. The right
    trigger on an empty part of it drags it somewhere, facing you; dropped on the outline of its resting place it
    follows again. Where it was put is kept, between sessions too (`menuPlaces.hudBoard` in vr-settings.json): called
    up again, it comes up there when that spot is within 60 degrees of where you face and 3 m, else in front,
    following. X held (or the menu's reset, or the assistant) brings it back to following, as the headset's
    recentring does. Hidden a while (a confirmation, X, a page up close) it follows on out of sight.
  - **Touching**: either hand's ray and trigger, as the world's left trigger: the left button for scripts on every
    press, a touch where the ray lands on a prim that takes touches, held to drag across it. The ray stops at the
    board; a pale dot shows where a press would touch. The panel is in front of it (its rays win).
  - **Drawn**: the HUDs as their own quad layer (sharp, as the panel), over a glass board in the panel's look (a second
    layer behind, ~40% of the panel's body, with × in a top strip clear of any HUD). The HUDs are over the chat feed
    and the assistant's cards (called up to be used), under the panel. Hidden, and taking no ray, while a confirmation
    waits; a trigger held from answering it doesn't press the board.
  - Considered: on the wrist (too small for HUDs laid out across a screen, and the left hand walks); head-locked (the
    viewer's form, uncomfortable in VR and always in front of faces); world-locked only where you left it (answers and
    HUDs are wanted where you are).
- **Floating text on HUDs** (titlers, meters, status lines; `Hud/HudText.cs`, 2026-10-26): the viewer's rule (above),
  its bottom 0.6 of the prim's height above its middle, lines growing upward, centred, in the script's colour and
  opacity with the world's text's dark edge. Pictures are the world floating text's, cached by words, colour and size,
  handed to the renderer as `HudRequest.Labels` (a rectangle of HUD space each) and laid over the HUDs in every image of
  them: the desktop's screen, the board's, the assistant's pictures (`FrameRenderer.Hud.cs` PlanLabels, drawn by the HUD
  compose pipeline, a second one for the board's sRGB image). 14 px text on the desktop; on the board a third larger
  (the headset's pixels are coarser than a monitor's: a departure from the viewer's layout).
- **Media on HUD faces** (2026-10-26): drawn as on any face (media's lookup finds HUD prims; the person's own HUDs' media
  plays by itself). The desktop's click on a HUD face with media is its page's (click, hover, drag, wheel), not the
  HUD's touch (`MediaWiring.Desktop` takes the HUD hit before the world's). On the board either hand's press on such a
  face goes to the page as media's gestures do in the world (tap clicks, drag scrolls, a still hold presses and drags,
  a double tap brings it up close: `HudView.IMedia`, `MediaInput.Hud*`).
- **HUD particles**: not drawn, as in the viewer by default (its RenderHUDParticles is a debug setting, off); a HUD
  point has no frame in the world, so a HUD's particle source never emits there either.
- **The assistant** (`SojournHud`, the `hud` tool): `list` (what the region says is worn on HUD points, and whether it's
  drawn); `look` (a PNG of the whole HUD screen, 1280 wide, or of one HUD by name or id, up to 1024 a side and bigger than
  it shows, over a dark grey, with each HUD's and each named part's rectangle on it: parts' names come from selecting
  them, as the edit tool does); `press` (at [x, y] on a picture, the last by default; or a part's id; or words naming a
  part, optionally scoped to a HUD; `hold` seconds) with the same surface data as the person's press; `show {on}` (the
  board, or the desktop's HUDs). A press is the person's click on their own attachment, so there's no confirmation
  (the gate's touch rule is about others' things). `requestTouch` on a HUD prim lands on it (its middle as seen, or a
  few points across it), so the `touch` tool with "that" works on a HUD too.

## Testing

- `python tools/hud.py test [text media popup]` (2026-10-26): Media Pilot (media's test account and site,
  `tools/media_fixtures.py`) wears the HUD Test Screen at HUD Center: a thin box sized for HUD space with media's test
  page on its -x face and floating text above, told over channel 9 (`hud text a|b`, `hud notext`, `hud reset`); it
  stays in the world by media's stand and a copy is taken into inventory once. text: the text in `look`'s picture and
  the desktop's shot, above the HUD, changing and going; media: it plays, and a posted click on the page's button on
  the screen counts on the page; popup: the page's new-window link opens up close. `python tools/hud.py headset`: the
  same HUD on the board in the XR Simulator (`SOJOURN_VR_SCRIPT=hud` aiming at the page's button and dragging across
  the page): the tap counts on the page, the left hand's hold and sweep is the page's press-and-drag, never the
  HUD's touch.
- Test HUDs on the local grid: an account of its own (Hud Pilot, password in `local/hud/account.txt`), a linkset with
  named parts and a one-prim textured pad, each with a script reporting touches by llOwnerSay (link, name, face, ST, UV,
  position, normal, binormal, and how many `touch` events a hold made), attached at Top Right and Bottom Left and moved
  in from the corner with `TesselBuild modify pos` on the attached root. The scripts go in through `addToContents`,
  which the gate holds: a client started with `SOJOURN_TEST_INPUT=1` takes a posted click on the confirm key. Take one
  object at a time into inventory (several taken together come back as one coalesced item, which won't attach).
- `SOJOURN_VR_SCRIPT=hud` in the XR Simulator (`docs/vr.md`): the right hand points at the wrist token and the board
  comes up; the right taps a point (`SOJOURN_HUD_AIMS="u,v;u,v"`, the screen 0-1 across and down; the first defaults to
  the test HUD's red button), the left holds its trigger on the second while sweeping across it, the right drags the
  board by its top strip 12 degrees and taps ×. With `SOJOURN_PANEL_IN_EYES=1` the HUD screen is drawn into the eyes
  too, its edge as lines (the glass layer isn't), for shots.
- `--panel-shots DIR` writes `hud-board-*.png`: the board's glass and × in each look over the background, with
  `DIR/hud.png` (a `look {transparent: true}` picture) laid in its screen.

Checked 2026-10-25 (local grid): desktop clicks on each button and the pad reported the right link, face 4, ST
matching where the click was, HUD-space position and normal (-1, 0, 0); a drag across the pad sent 12 updates and
ended off it with face -1; `press` by picture point, part id and name, and `requestTouch` on a part, all landed;
Haiku answered "press the green button on my HUD" (two hud calls, acting at 2.2 s), "touch the top right corner of
the wooden pad" (ST 0.99, 0.99) and "press that" with the mouse on a HUD button. In the simulator the routine above
brought the board up from the token, pressed the red button, dragged across the pad (27 updates), moved the board and
put it away.

## Not done

- HUD particles (the viewer's are off by default too), editing HUDs (the viewer's zoom while a HUD is selected), the
  viewer's HUD hover highlight.
- The board's size isn't a setting (drag it nearer to make it bigger).
- Real HUDs at scale (a mesh body's many-button HUD, an AO, a combat meter) and the board and token in the headset:
  on the board.
