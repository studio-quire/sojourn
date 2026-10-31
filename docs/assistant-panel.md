# The assistant's panel, menus and what it shows

Written by Claude Opus 5.5, 2026-10-25, moved out of `docs/assistant.md` 2026-10-31. Agent-facing. The panel, the
settings menu, their look, where every menu sits in the headset, size, the elements the assistant shows on screen,
and the reader. The assistant itself: `docs/assistant.md`.

The rule the interface follows (user, 2026-10-25, after a first glass pass they found rough and wordy): no text
explaining what elements do; lean on interface norms people already know; what's truly unintuitive is made intuitive
first, and only then explained, behind a small toggle.

## The panel

- **What it shows** (`Assistant/Panel.cs`): no state words, no instructions, no footer. Everything in full (user,
  2026-10-26): an answer up to 14 lines grows the card; a longer one goes a page at a time, pages of about equal length
  (at most 12 lines, ending at a sentence where one falls in the last few lines), with ‹ › keys either side of a dot a
  page (the card keeps the tallest page's height, so the keys stay put; turning a page keeps it up); in the headset a
  flick of the right stick while the ray is on the card turns a page too (`docs/vr.md` > Controls > Scrolling with the
  stick). Choices' labels and details wrap and the key grows; a confirmation's title takes 4 lines and its detail 10.
  - Listening: a small capsule with a breathing orb and, if the pointer is on something, the amber dot (the world's
    pointer dot) and its name: what "that" will mean. Tapped open hands-free (no button held), a steady ring around the
    orb until the tap that sends.
  - Working: three dots beside what was asked, what it's doing and, faintly, the model working on it (why it's quick
    or slow).
  - Answer: what was asked small and dim, the answer in full ink below.
  - Choices: numbered glass keys.
  - A confirmation: the client's title and detail, cancel and the accent confirm key, no × (cancel answers it). The
    confirm key is an accent outline that takes no press until armed, then solid (`docs/assistant.md` >
    Confirmations). Where × would be, a list key (a bulleted list) when it names only the start of a list ("and 40
    more") or its words are cut: all of it in the reader, the confirmation waiting on.
  - Nothing to show (X with no history): a capsule with a microphone and a ? key that reveals how to talk.
  Cards are 400 logical px wide; the listening capsule fits its content. Only news keeps it up: the pointer moving onto
  something doesn't. An answer stays 15 s plus 0.08 s a character, up to 75 s (shorter went before it was read in the
  headset, 2026-10-27).
- **The session's replies** (`Panel.Replies`, in memory only, the last 200): every answer is kept as it's shown (an
  answer growing during one request replaces its own). A stack key beside × on an answer, and on the idle capsule once
  there are replies, opens them all in the reader (> The reader), scrolled to the newest; they keep coming in while
  it's open.
- **Replying over an answer** (2026-10-27; the user lost the answer they were reading each time they spoke or typed
  again): the answer stays on the card, whole and on the page they were on, while the next request is listened to (the
  orb and what "that" is under a rule below it, not the small capsule), typed or worked on (the dots and their words
  below it), and the stack key stays up. When the new answer comes, the one before it shrinks to a dim two-line strip
  above it, under a rule; pressing the strip opens the replies in the reader. Only within a conversation: the answer
  before shows while it's under 10 minutes old (`Panel.Before`, `RecentSeconds`). Not while choices, a question to
  point, a draft or a confirmation is up.

## The settings menu

`Assistant/PanelMenu.cs`, rows from `Control/SettingsMenu.cs`.
- **Layout**: sections as a segmented control, ? and × at the top right; more sections than one strip holds with a
  little air round each name go on two strips, both as wide as the room. Rows say what they are by their control
  (`MenuRow`): a switch (a bead in a groove cut into the body: off, resting left in the keys' glass; on, at the right
  and lit in the accent; the whole row presses), a segmented choice (every option visible), swatches (looks, each a
  small sample of its material), a stepper, a value key that steps or acts (reset, refresh), or a **group**: a row with
  a chevron (and a faint value where one helps: "on", "some") that opens a page of its own rows, its header then a back
  key with the group's name, pressed to go back to the page it came from (`MenuRow.Group`; the section's tab index, so
  no new key kind). Groups keep a section to about one page at size 1 (the user wants completeness before release's
  simplification, but no section unusable, 2026-10-29). Notes only where the person set the words (a character). Rows
  that don't apply aren't shown (eye height when seated; name and character until asked for; beacons' kinds while
  beacons are off; the wall test with menus over walls).
- **Simple and advanced** (the user's call, 2026-10-29: Sojourn is assistant first, so the menu holds only what a
  person would change by hand rather than ask for; graphics are for asking, draw distance excepted): by default the
  menu shows assistant (model, memory, forget memory, ask before touching, advanced menu), incognito (all of it: by
  hand on purpose, some of it better not said aloud), voice (mic on, in open mic), safety (a script's controls back,
  hidden things shown again, when there are any) and view (draw distance); a section with none of those isn't shown.
  The **advanced menu** switch (last in assistant, `advanced_menu` in SojournSettings, saved) shows every row. Only the
  menu changes: the settings and the assistant's reach are the same either way. Each row's level is one letter in
  `Control/MenuGuide.cs`; the tests' presses and pictures use the whole menu.
- **Names that say what they are** (the user, 2026-10-30: minimal must stay readable): a row's few words name the
  thing, not just its value or a bare noun (name tags, chat feed, media autoplay, media first click, selection beams,
  menu size, menu positions, advanced menu, let the assistant see, screen reader); like keys in a group are all words
  or all icons, never mixed (the map's Track beside Go). A segmented row's options give up their air (to 12 px) before
  its name would be cut short (haiku effort).
- **Tips** (the user's ask, 2026-10-30: names stay minimal): the pointer (right ray, or the mouse on the desktop)
  resting 0.5 s on a row or tab shows what it does in a sentence or two, on a small raised pane of the menu's glass
  just under it (above it at the foot), over the rows below; it goes as soon as the pointer leaves the row or a key is
  pressed, and crossing rows never shows one. The words are the person's, not the settings' `desc` (which is for the
  assistant): `MenuGuide`, beside each row's level, keyed by section, group and label. Drawn into the menu's own
  raster, so it follows the menu and costs a redraw only when it comes or goes.
- **Opening it on the desktop** (the user, 2026-10-30: Escape, as games do): Esc is layered. Each press frees the mouse
  and takes the nearest thing open or waiting (typing, the screen reader's keys, a dictated draft, a confirmation
  cancelled, a point question, then media up close or focused, the reader, the world map, the feed's opened lines);
  only when nothing takes it does the menu open. In the menu, Esc puts its help away, then goes back out of a group to
  the page it was opened from, then closes it. F2 still opens and closes it. The order: `docs/client.md` > Controls,
  `DeskEscape` in `Program.cs`. The headset keeps the left menu button.
- **Sections** (advanced, headset; the desktop has no moving, menu size, menu positions or menus):
  - **moving**;
  - **assistant**: model, each of the mode's models' effort, look, adaptive ink, spoken replies, memory, hearing,
    forget memory, called X, its character, ask before touching others' things, advanced menu;
  - **incognito**: incognito, let the assistant see, its triggers;
  - **you**: do not disturb, away, away after, hover height, show typing, *turn away* › only friends IM me, teleports
    none/not friends'/all, friendships, group invites, one-to-one, group and conference calls; *turn away calls
    from* › a switch per group (with voice and groups); *AO* › the AO, its set,
    stands, OpenCollar, LockMeister, lock its folder; then skeleton · reset, attachments · refresh, animations ·
    resync (the person's own avatar but the last, which is everyone's), run as the person's own press on
    SojournAvatars' ops, the key showing … then done or failed;
  - **social**: name tags, chat feed, hover text, chat bubbles, tags through walls, group titles, friend colour, hover
    tips off/0.3/0.7/1.5 s, *alerts* › mentions, keywords, sound; *radar* › chat range, draw distance, region each
    off/enter/leave/both, new accounts;
  - **voice** (+ hear from: head (the viewer's camera)/avatar/mixed); **sound** (media autoplay and media volume, +
    media first click, a stepper through none to all, and zoom to media); **screen reader** (what's read out through
    one); **safety**;
  - **view**: size (four A's growing left to right, first in the headset), menu positions · reset, draw distance,
    *graphics* › the renderer's quality, the GPU options to judge, resolution, depth reprojection, load all around;
    *avatars* › full avatars, complexity limit, avatar physics, lip sync, selection beams; sky; location line
    off/on/+ lag; *beacons* › beacons and, while on, its kinds, lines, line width, highlights, sun, moon, then
    highlight transparent, tracking beacon, tracking beep; *parcel lines* › property lines, ban lines; maturity;
    *menus* › menus over walls, wall test, frost bias, menus on desktop.
- **Where the rows come from**: the client's own (`SettingsMenu`, `SettingsStandard`), a part's own section through
  `ISettingsMenu` (people, sound, incognito, reader, safety; media appends to sound), and the rest made by name from
  `SojournSettings` (`SettingsApi.SwitchRow`, `ChoiceRow`, `CycleRow`, `NumberRow`, `Change`: a press sets it as a
  caller's `set` would, a refusal logged), so a row does what the API does and a part needs no menu of its own (the
  social layer, the AO, avatars, the map's line, beacons, parcel lines, voice's hearing place and call switches). A
  setting this app hasn't registered has no row.
- **Not placed** (2026-10-29): translation (`translate_chat`, `translate_to`, `translator`: no service yet, the user's
  call), `clouds`, `mirror_resolution`, `shadow_resolution`, `money_notices`, `auto_accept_inventory`,
  `typing_animation`, `media_placed`; all on SojournSettings for the assistant.
- **Behind ?**: the controls nothing on screen shows (grip to talk, X, Y, the menu button; the seated height; the
  camera).
- **Paging**: a section taller than the room it has (44 deg in the headset, from 2 above the eyes to the 42-below
  floor; the window's height on the desktop) goes a page of rows at a time, with ‹ › and a dot a page at its foot,
  every page as tall as the tallest, turned by those keys or a flick of the right stick with the ray on the menu; at
  size 1 a section holds about ten rows under two strips of tabs, at 0.7 about fifteen.
- **Checking it**: `--panel-shots` draws mock sections (`7e`-`7h`: every tab, size 0.7, a group's page, a lit group
  row; `7i`-`7l` the tips); with `SOJOURN_TEST_MENU=1`, `SojournSettings menuShots {dir}` draws the running client's
  real menu, every section, group and page at the current size (`menu-<section>[-<group>]-<page>.png`);
  `tools/menu.py` runs both and presses 22 rows by label (`menuPress`), each read back, then puts them back;
  `--headset` takes the camera out, a picture and away in the simulator.

## The look

**Glass** by default, light (user, 2026-10-25: glass, pearl and silver are the user's preferred defaults across
projects; a light theme with refreshing colours by default, less contrast, no gold accent, and nothing taken wholesale
from iOS or any design language).
- `Assistant/PanelLook.cs` holds it as data: preset, material (glass, pearl, metal: how light sits on it), tint and
  opacity, glow (below), sheen (up to three colours: corner glints on glass, pearl's orient in diagonal bands under a
  satin highlight, metal's reflection bands with bevelled keys), frost, rim, ink/dim/faint, accent and its label, key
  colour and opacity, radius, font.
- **Presets**: glass (clear white glass at 0.22, lightly frosted, white light at its corners, a bright rim, glow 0.45,
  deep teal ink and accent; the user's tuning in the headset, 2026-10-28, toward Liquid Glass), pearl (milky nacre,
  plum-grey ink, deep rose accent), silver (polished metal, opaque, deep blue accent), smoke (dark smoked glass, soft
  light ink, pale sea-glass accent). The accent is the light held in the look: the confirm key, an on switch's bead,
  the listening orb; nothing else is coloured (the amber dot is the world's pointer, not the look's). Only the default
  (glass) has to read well everywhere, by the legibility measure with a margin (`--panel-shots` prints each preset's
  least contrasts); the others are looks for their own surroundings and stay as designed (the user, 2026-10-28):
  smoke is faint over a bright world (ink 2.6, dim 1.6), and that's fine.
- The menu picks presets by swatch; the assistant can set any value (`SojournAssistant.look`, its `panel_look` tool),
  and a changed look is "yours" (its own swatch). A saved preset loads as the preset is now, so a redesign reaches it.
- **How see-through** comes from frost as much as tint_opacity: frost blurs the world behind wherever the body is (the
  renderer's frost covers all of it at any opacity), so a lower tint_opacity alone only shows more of the blur, which
  barely reads as see-through (0.8 -> 0.65 looked the same in the headset, 2026-10-27). The panel_look tool says to
  lower frost too. tint_opacity goes down to 0.05.
- **Glow** (`Assistant/GlassLight.cs`; the user's ask, 2026-10-28: as it turns see-through it should keep its own
  light): dark ink needs light behind it, and over a dark world a light tint at low opacity is only a dark grey. So
  the body never shows dimmer over black than glow x its tint when solid: below tint_opacity = glow it gives off light
  of its own, (glow - tint_opacity) x the tint, in linear light; at or above glow, none. It's laid as a light behind the
  glass: each pixel gets it by (its coverage of the body - its alpha), coverage = alpha / tint_opacity, so opaque ink
  and keys keep their colour and the body's antialiased edge its share. Premultiplied, that's colour above alpha, light
  added over the world (the layers' blend and the overlay's both allow it). 0.5 by default (smoke 0): with the glass
  ink that's ink 4.6, dim 2.5 over black at any tint_opacity, dim being what binds. Clearer than that with dark ink
  isn't possible without less light behind the text: the world still comes through at (1 - tint_opacity), but over a
  pedestal of the glow's light.
- **Adaptive ink** (`Assistant/AdaptiveInk.cs`, `adaptive_ink`, a switch under look; off by default; the user's idea,
  2026-10-28): for clear glass without the glow's milkiness, the ink turns light over dark places and dark over bright
  ones. The compositor does the final blend, so it's per sheet, not per pixel: the overlay copies the frost's deepest
  backdrop mip of the first eye (32x34 for a Quest eye) to a readback buffer every third frame and publishes it when
  that frame is done (`Realtime/Overlay.Backdrop.cs`, nothing waits; with no frosted sheet it builds the mip chain for
  it). Each sheet (the panel and menu, the feed, the cards with the person, the reader) is found in it by its corners
  or pixels; its patch's darker and brighter quarter, smoothed over 0.1 s, decide by `PanelLook.InkContrast` (the
  legibility measure on the bare body): it turns only when its ink reads under 4.5 and the other would read 1.25x
  better, at most every 0.35 s, fading over 0.15 s in four redraws; a sheet back after a second away takes its ink at
  once. So a solid body never turns. The other ink is the look's own turned over (each colour's lightness mirrored
  around the ink, landing at L* 90 or 24, hue kept; keys, sheen and rim fainter under light ink), and there's no glow
  while it's on. Confirmations keep their preset; name tags, the HUD board and the keyboard keep the look.
- **The headset's layers are blended in linear light**: their swapchains are sRGB, so the compositor decodes each
  channel, then blends. Skia premultiplies in gamma, and decode(a x c) is far darker than a x decode(c) at low alpha (a
  light tint at 0.3 showed a quarter as bright as meant, so lowering tint_opacity darkened the panel rather than
  clearing it). So while a headset session runs (`GlassLight.Layers`, set by XrSession), the look's sheets (panel,
  menu, feed, reader, the cards with the person) are re-premultiplied in linear light with the glow, on their draw
  tasks (`GlassLight.Finish`, table lookups). The desktop overlay blends gamma bytes as Skia does, so it gets only the
  glow (the same step in encoded light). Not converted yet: the HUD board's glass, the keyboard and the page board's
  chrome (fixed opacity 0.88+, so less affected); the PC's mirror window blends the converted pictures in gamma, so
  see-through parts look a little brighter there than in the headset.
- **Legibility measure** (`PanelLook.Readability`), as the headset shows it (the sheet as Skia paints it, finished by
  GlassLight, laid in linear light) over a black, a mid-grey and a white world: ink against the body with the light
  that lies where text can sit (sheen at full strength, pearl's satin) 3.2:1, dim 2.3:1, ink on a lit key 3.2:1, the
  accent against the body 1.8:1, the accent's label 4.5:1. Advice, never a refusal (user, 2026-10-28: a look that's
  hard to read gets changed by asking, or a preset picked): any valid change applies, and `look` returns a
  `readability` note with what would be faint, over which world and what would help, for the assistant to mention.
  Confirmations don't rely on it: they're drawn in a preset.
- **Fonts**: Segoe UI by default, regular and semibold only (thin strokes shimmer in a headset); Archivo bundled as the
  fallback.
- **Characters the face lacks** (`Assistant/FallbackText.cs`, everything Skia draws: the panel, feed, name tags,
  bubbles, floating text, hover tips, the reader, cards, the map): emoji, symbols, "fancy" Unicode letters and other
  scripts fall back per grapheme cluster: Segoe UI Emoji for emoji (in colour, Skia draws its COLR glyphs), else the
  look's face, else Segoe UI Symbol (symbols first) or the system's fallback for the look's face (letters first).
  Neighbouring clusters in one face make a run; a fallback run holding a multi-code-point cluster (ZWJ families, skin
  tones, keycaps, tag-sequence flags) is shaped with HarfBuzz (SkiaSharp.HarfBuzz). `Panel.Measure` and `Painter.Text` go
  through it, so wrapping measures what's drawn, and wraps and ellipses cut only between clusters
  (`FallbackText.Before`). ASCII takes the face directly; other text is split once per (face, string) and cached. Not
  shaped: text in the look's own face (as before), and right-to-left order across runs. Flags show as letter pairs
  (Windows' emoji font has none).

## Frost

The overlay (`Realtime/Overlay.cs`) blits the finished eye image at half size into a mip chain and draws the world
behind the panel from a mip deep enough to blur about the same at any resolution, mixed 60% with one two mips deeper,
masked by the panel's alpha (`overlay.frag` style 4). The wide half is what keeps light across the body continuous: a
bright sky over dark ground behind the panel becomes a gentle change of light, not a band edge. Nothing the body draws
starts or stops partway down either. In the headset it's drawn into the eye images where the panel's layer floats,
and the compositor lays the panel over it; quick head motion can show the frost a few mm off the layer's edge. Where a
wall hides the layer, the frost is hidden too (the compositor's depth corrected under the menus); with
`menus_over_walls` the frost is untested and walls never hide the layer (`docs/vr.md` > The rig, Walls and the menus).

## Where it sits

In the headset (`Assistant/PanelPlacement.cs`): its own swapchain as an OpenXR quad layer, sampled straight onto the
lenses. Two ways it sits, switched by hand with no words:
- **Following** (the default): body-anchored with a lazy follow: placed where you face when it appears, low in view,
  turned to face the eyes; it holds still while you look around it, read it, lean in or glance at someone, and once you
  turn past its range (or step 0.2 m off, 0.15 m up or down; a hand reaching to it holds its place) it's carried along
  at the range's edge, continuously, eased over ~60 ms (`PanelPlacement.Leash`; user, 2026-10-26: it sits wherever
  the head leaves it, never gliding back to the middle). The chat feed and the view cards beside it move with it, and
  on their own the same way. Stick walking and turning carry it along (it lives in the room).
- **Put somewhere**: point at the panel's body (not a key), hold the trigger and move: it follows the ray, turned to
  face you, and stays where you let go, in the room (so the stick still carries it, and a seated person can keep it
  low at their side). It shows there whenever it comes up while that place is within 60 deg of where you face;
  otherwise it comes up in front, following, for that showing (X hiding the interface and showing it again does that
  too); the assistant can bring it in front (`SojournAssistant bringPanel`; the place it was put is kept for next
  time). While dragging, a faint outline marks its resting place; dropping it there (within 9 deg; the outline
  brightens) makes it follow again. Kept between sessions (`menuPlaces` in the settings, by name: the tracking space's
  coordinates and which space, local-floor here), so it's there next time when that spot is in view; forgotten when
  the headset's own recentring moves the room (a runtime announcing its space without moving it, as the XR Simulator
  does at the start, isn't one).
- **Every menu the same way** (the user's ask, 2026-10-28: every menu follows your head until you move it): each sheet
  has its own `PanelPlacement` (the one machinery: follow, leash, drag, outline, kept place) and its own resting
  place: the settings menu (its own placement, apart from the panel's though they share a layer; dragged by its body
  between rows), the chat feed (left of the panel; a press on a block that moves 2.5 cm becomes a drag), each card
  with the person in view (its place in the column right of the panel; a card put somewhere leaves the column, dropped
  on the column's next place it goes back; the ray stops at view cards, and their bodies take a drag, not a touch
  through to the world), the reader (above the panel; by its title strip), the HUD board and a page up close (above
  it; by their strips and margins), the keyboard (low and near; by its strip). The feed and the cards keep their places
  beside the panel while it shows and follows (a `Leader`: its heading and anchor), and follow on their own otherwise;
  the reader, the boards and the keyboard follow on their own (they sit above or below the panel, so they never cover
  it whatever the heading). The boards are carried past 40 deg, the keyboard past 35 (wide sheets: looking at a HUD in
  a corner shouldn't move the board). Hidden by a confirmation or X, a sheet comes back placed afresh (the HUD board
  follows on out of sight). Room cards stay where the assistant put them (`room` is its choice); wrist cards stay at
  the wrist.
- **Bringing them all back** (`Assistant/MenuPlaces.cs`): every menu back to following in its own place, from in
  front, every kept place forgotten, and the interface shown if X had hidden it. X held 0.6 s (the user's call,
  2026-10-28; a tap still hides and shows everything, on its release, so a hold never toggles), the view section's
  **menu positions · reset** key (shown only while one is put somewhere), or the assistant (`SojournAssistant
  resetMenus`; its `bring_panel` tool with `all`).

Why these two: in SL the panel is mostly news (answers, choices, the listening capsule) that should stay out of the
way of faces and the scene, so the default rests low and comes to you; a person who settles somewhere (seated at a
club, building, a long chat with the assistant) wants it at one spot they chose. A "raised to read" mode wasn't needed:
a glance down reads it, as with a phone, and the beam points at it there. Considered and not chosen: head-locked
(uncomfortable, always in the way); world-fixed only (answers arrive seconds after you've turned to something else);
wrist or hand (the left hand walks with the stick, answers arrive while you're busy, and 0.3-0.5 m is far nearer than
the lenses' focus for reading paragraphs). A hand-held menu like VRChat's is a possible later option.

Details:
- While the beam is on it or the trigger is held, its range is 15 deg wider (it holds still for the hand, but a head
  turned well away still carries it, so a resting hand's ray across it can't leave it behind).
- Confirmations always come up in front, following (never at a place the panel was put), and can't be dragged; a
  confirmation re-arms (0.8 s) after every glide and whenever the layer is put somewhere new (from a panel's place to
  in front), so nothing slides under a press.
- Sizes are angular: 0.07 deg per logical px at size 1 (body text ~1.25 deg, ~30 of the Quest 3's display pixels; a
  card ~28 deg wide), at 1.1 m (near the lenses' ~1.3 m focus rather than arm's length, past the controllers); a panel
  put somewhere keeps its size in metres.
- Following, by state:
  - status, answers, choices: top edge 18 deg below the eyes, growing downward; carried past 30 deg. Starting to talk
    leaves it where it is.
  - a confirmation: top edge 10 deg below the eyes, nearer the centre; glides in front when it arrives; carried past
    20 deg.
  - the settings menu: top edge 4 deg below the eyes when opened; carried past 35 deg; left alone 90 s (no ray on it,
    no press), it closes by itself, as it can sit forgotten to one side while the panel waits for it.
  - never lower than 42 deg below the eyes at its bottom edge.
- Desktop: top right of the image at 0.8-1.6x (by window height), drawn by `Realtime/Overlay.cs` after
  post-processing.

## Size

`Assistant/UiSize.cs`, `ui_size` in the settings (VrSettings.UiSize); added when the user found the headset's UI very
large (2026-10-27). One factor, 0.7-1.2 (the menu's A's: 0.7, 0.85, 1, 1.2), on the angular size of the panel, the
settings menu, the chat feed (and its place beside the panel), the assistant's cards (view column, wrist, room), the
reader and the HUD board. Not the keyboard (a thing to hit at arm's length, not to read), name tags and world labels,
or the desktop. It applies at once, to a panel put somewhere too.
- The space freed is spent on more content rather than left empty (the user, 2026-10-27, after choosing 0.7).
  Heights: where long text lives the angular budget stays, so smaller type fits more into it: an answer shows whole up
  to 14 lines and pages 12 at size 1, 20 and 17 at 0.7 (`Panel.Fit`; a page past the last after a size change is
  clamped as drawn); the menu's 44 deg holds more rows; the reader's view is 560 logical px at 1, 800 at 0.7; the
  feed's height caps and the lines a message shows before it's cut grow the same way.
- Widths: the card, menu, feed, reader and the assistant's cards with the person are wider in logical px by Fit^0.7
  (`UiSize.Wide`: 1.28x at 0.7), so they keep ~90% of their angular width and carry ~60 characters a line where size 1
  carried ~48 (not the full 1/size, which would run lines past a comfortable measure); the HUD board's angle goes by
  the same rule (54 deg at 0.7, 60 at 1). The feed (left) and the view cards (right) sit edge to edge with the panel at
  every size, 3 deg apart from its widest sheet (the menu), the widths taken as the yaw they span 30 deg below the eyes
  where they all meet lowest, and they share the panel's anchor (`UiSize.BesideYaw`). Short things simply get smaller.
- Sharpness: the raster follows the size (2.4 x the size per logical px), so every size has ~34 raster px a degree,
  ~1.4x the Quest 3's ~25 display px a degree. Drawing smaller sheets at a fixed 2.4 would put more texels a degree on
  a layer with no mips than the display can show, and thin strokes would shimmer. At 0.7 body text is ~0.9 deg, ~22
  display px to the em; the faintest small labels (12.5 px) ~15. Below 0.7 those go unreadable, hence the floor.

## Drawing and checking

- Rasterised with Skia off the main thread when it changes (8 times a second while the orb or dots move), at 2.4 x
  the size for the headset, less for a sheet that would be taller (1536 px) or wider (1152) than the headset's
  swapchains, so it's never cropped (the feed's layer is the panel's size). Keys and presses go by the raster on
  screen that frame.
- `SOJOURN_PANEL_IN_EYES=1` also draws the headset panel into the eye images (affine, slightly skewed), so
  `--shot-every` shows it without a headset; the headset's own layer covers it.
- `--panel-shots DIR` draws the panel's states, the settings menu (with and without help) and each preset over
  `local/client/latest.png` (frost approximated in Skia) and quits: for judging the look without a session. It prints
  any preset's readability advice and the adaptive ink's turned colours. `glow-sheet.png` is the glass at tint_opacity
  0.8 down to 0.05 over black, grey, white and the world, composited as the headset's compositor does
  (`glow-sheet-before.png` without the glow or the linear premultiply); `adaptive-sheet.png` the looks over black to
  white in the ink each would come up in. With `DIR/view.png` (a Quest-eye shot: `--stereo --quest-eyes --eye
  1032x1104`), also `view-*.png`: the panel, menu, reader, feed and shown cards where the headset places them, at
  their angular size in the left eye, at each size.

## Showing things (Showing/)

At the user's request (2026-10-25): the assistant can put things on screen to show the person information in whatever
form helps, as the panel is for its words. Information only (the user's direction for Sojourn: the person operates
nothing but the assistant): the one press an element takes is its own close key (and in the headset a card in view
can be dragged somewhere, as every menu can).
- **The API**: `SojournShow` `show` (create, or change the element with that id: the fields given replace its own),
  `remove` ({id} or {all}), `list`; the assistant's `show` tool wraps all three. Each request's context line lists
  what's on screen ("on screen: tea (view: timer)"), so it can change or clear things. Events back to the sidecar:
  `shown_done` (a timer ran out: the sidecar puts "Tea is done." on the panel, spoken if replies are) and
  `shown_dismissed` (the person's close key).
- **The vocabulary** (`ElementSpec.cs`): an element is an anchor, an optional title and up to six blocks, or a label
  (one short line, drawn as a capsule). Anchors: `view`, `wrist`, `room`, an object's or avatar's id, `[x,y,z]` in this
  region. Blocks: text (body, title, big for a figure, dim), list (numbered, or a checklist when items carry `done`,
  with a dim detail at the right), table (a header row, numbers right-aligned), timer (counts down by itself; sent
  again it keeps running) and stopwatch, progress, chart (bars or a line, one series, values written only at the bar
  tips, a line's end and its highest point), image (`view`: what they see, captured before the overlay; a texture by
  id, OpenJPEG at a quarter size; or a picture Sojourn saved, below), toward (an arrow turned by where something lies
  against the way they face, and how far), map (track-up: ahead is up, them in the middle, N at the rim, places as dots
  with labels, the radius a round number). Why these: ten kinds that compose cover the user's examples (a card near them or on a thing, a list, a
  timer, labels, a map or pointer, an errand's checklist, a picture, a chart) without the model choosing sizes, colours
  or fonts, which are fixed so every element reads; `for: seconds` gives an element a lifetime.
- **The look** (`ElementPainter.cs`): the panel's current look (`PanelLook`, glow included), its body and fonts; only
  ink, dim and faint, never the accent (the confirm key's light: "this needs you"), and nothing raised or key-like but
  the close key. Each element carries the assistant's spark (a small four-pointed star) at the top left, which nothing
  a person, an object or the client says carries. Cards are as wide as their content wants, 200-300 logical px (360
  for an image, a map, a line chart or a wide table; 560 for a saved picture; 260 out in the world).
- **Pictures** (`Showing/Pictures.cs`; the user, 2026-10-31: a picture just taken, seen at once in the headset):
  `{image: "last"}` (the newest in Pictures\Sojourn) or a file name or path there, nothing outside it. Its size comes
  from the header when it's shown, so the card has its final shape at once (dots in a recess of that shape until it's
  read); decoded off the main thread to at most 1280 px on its long side. The card is up to 560 logical px wide (39
  deg in view, 31 at 1.4 m in the room) and the picture up to 440 tall, so an upright one makes a narrower card. Best at
  `room`: in front of them; in the view column it reaches far to the right. The capture tool's `show: true` (picture,
  360) saves and shows in one step, on the card `picture` in the room, the next picture replacing it; `--panel-shots`
  writes `shown-picture*.png` (wide, upright, still being read).
  Incognito (the assistant never sees a picture taken in a spell, and a shown card can be seen by a look with the
  menus): blind, only `last` (any picture: it can't look at all, and naming files would let it probe for them); seeing,
  pictures named incognito only if written since it began to see; out of a spell, none named incognito (refused with a
  note that the person can open it from the folder). A card holding such a picture, or a `view` taken during a spell,
  goes when the spell ends or its seeing changes (`Shown.SpellChanged`).
- **Where** (`ShownView.cs`):
  - Desktop: with the person, stacked at the bottom right (the first shown lowest; a second column to the left when one
    fills to 38% of the height); in the world, upright billboards sized like name tags.
  - Headset: view cards each on their own quad layer (`XrSession.SetCards`, swapchains sized to fit), a column right
    of the panel's card mirroring the chat feed (bottom 30 deg below the eyes, growing up, following lazily with the
    panel); a card dragged by its body is put somewhere, as the panel is (> Where it sits). Wrist cards above the left
    wrist at the panel's angular size, shown while it's within 30 deg of where you look. Room cards placed 1.4 m ahead
    when they first show, at about eye level with the bottom edge no lower than 10 deg below the eyes (clear of the
    panel's top at 18 deg below), beside the room cards already there if straight ahead is taken, and left in the room.
    World elements drawn into the eyes from the elements' atlas (`Overlay.cs`, styles 12-13 with their frost), placed
    at record time over an avatar's head like tags (above the tag when names show), over an object's linkset top, or
    on a stem over a point.
  - The headset's quad layers are composited each over another exactly where it's in front of it, a menu another
    passes through going in parts on either side of it (`Xr/MenuOrder.cs`, `docs/vr.md` > Menus over menus; the
    compositor has no depth between quads); a board's picture stays over its glass, and a confirmation's panel goes
    over all.
- **Safety**: while a confirmation waits nothing shown is drawn (desktop and headset), so nothing of the assistant's
  sits beside the client's words; the op itself is held like any other while a confirmed op starts (not
  `RunsWhileHeld`). No caps on how much, how many or how often (the user, 2026-10-28: the person judges that and can
  ask for less; the prompt still says to keep it small); images, maps and tables only with the person.
- **Drawing** is off the main thread, one build at a time (changes arriving meanwhile coalesce), only when an
  element's picture would change (live parts quantised: a timer's second, 5 deg of bearing, a metre), at most 8 times
  a second; the atlas grows toward square past 2048 wide, up to the GPU's 16384 side.
- The `show` tool takes names where ids go (`at`, `toward`, map places: "the fountain", "Mira", "that"; several fits
  come back as candidates), reads blocks sent as JSON text (a typed schema turns that away before the tool runs), and
  names an element sent without an id. Before these two changes Haiku twice claimed a list and a map it never showed.
  Map places and arrows given only an id are labelled with the thing's name. Several elements on one thing stack
  upward.
- `--panel-shots` writes `shown-*.png` (each kind; timer, checklist, bars and a label in every look) and, with
  `view.png`, `view-shown.png` (a timer and a checklist in the view column beside an answer, in perspective). The
  wrist cards and the trigger on a close key are unchecked in the headset.

## The reader (Reader.cs, ReaderView.cs)

Built 2026-10-27 after a notecard couldn't be read in the headset (a show card holds 480 px and 300 characters a
block) and replies went before they were read. One sheet in the panel's look for long text the person reads at their
own pace: the session's replies (the panel's stack key), a notecard (a notice's Read key, or the assistant), text
the assistant writes there with a title (it carries the spark; a notecard is someone else's words and doesn't), or all
of what a waiting confirmation names (its list key; the client's words, one name a line under "46 items"). 440
logical px wide, up to 560 tall (more at a smaller size: > Size); a title strip with ×, the text below, a thin bar at
the right that says where you are. Drawn in the panel's own glass, frost behind it as the panel and the feed have
(every menu alike, the user's call, 2026-10-28): in the headset its sheet goes into the eyes' overlay for the frost
(`OverlayScene.Reader`, styles 24-25), tested against the world's depth and hidden by walls as the other menus are.
- **Headset**: its own quad layer, 1.2 m out, its bottom edge 2 deg above the panel's top, facing you, following the
  head as every menu does (> Where it sits; the panel at 1.1 m stays in front where they meet). The right ray and
  trigger, with the pointer on or off (it's the person's own to read; a faint beam shows while the ray is on it): a
  drag on the text scrolls it, the text following the hand, running on a little when let go; a drag on the title strip
  puts it somewhere (dropped on its outline, it follows again); × closes it. The right stick with the ray on it
  scrolls it smoothly, faster the further it's pushed (`docs/vr.md` > Controls > Scrolling with the stick). The ray
  stops at it. Hidden while a confirmation waits, but for that confirmation's list, which rests 4 deg above the
  confirmation's top (higher than the panel's) with both up.
- **Desktop**: in the panel's place at the top right while it's open (a confirmation takes the place back, but for its
  own list, which × or Esc closes); the mouse drags the text, the wheel scrolls three lines a notch, × closes. A
  confirmation's list opened over something else gives it back when it closes.
- **The API**: `SojournReader` `open` ({notecard: item id}, {replies: true}, or {text, title}; up to 200,000
  characters, paragraphs at blank lines), `close`, `state`; the assistant's `reader` tool, which it's told to use
  rather than a show card for anything longer than a few lines.
- `--panel-shots` writes `reader-replies`, `reader-notecard` (scrolled partway), `reader-notecard-smoke` and
  `reader-confirm-list` (and `6c-confirm-list`, the confirmation with its list key).
