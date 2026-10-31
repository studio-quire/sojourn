# The map, where you are, and how things are running (SojournMap)

Written by Claude Opus 5.5, 2026-10-29 (batch B8 of `docs/viewer-gaps.md` > Closing the rest). Agent-facing. The world
map and the mini-map (two boards, the user's call on 2026-10-29: one board zooming between them lost the world map's
place whenever the line reopened it), the location line at the left wrist (the viewer's location bar, with its lag
meter), and statistics at a glance; each as the official viewer does it (its source read: `llworldmap.cpp`,
`llworldmapview.cpp`, `llworldmipmap.cpp`, `llnetmap.cpp`, `lllocationinputctrl.cpp`, `llviewerparcelmgr.cpp`,
`llfloaterlagmeter.cpp`), with `SojournMap` for the assistant. The rest of B8 (beacons and Highlight Transparent; the rest of
snapshots) is in `docs/rendering.md` and `docs/assistant.md` > Pictures and videos.

Code: `Client/Map/` (`MapWiring.cs` the glue, settings and where the mini-map is, `MapBoard.cs` either map,
`WristLine.cs`, `MapPainter.cs` the drawing and both layouts (`MapLayout`), `MapApi.cs`, `MapShots.cs`);
`Session/Map/` (`MapTiles.cs`, `WorldMap.cs` the map's data, `Where.cs` location and statistics);
`Xr/VrScript.Map.cs`; `tools/map.py`. Hunks in shared files: `Program.cs` (the wiring's calls, both maps' places in
`MenuPlaces`), the renderer's overlay (`Overlay.cs`, `overlay.frag`: a ninth picture, the client's boards' atlas,
`OverlayScene.BoardAtlas`/`Boards`, styles 26 and 27, `OverlayCard.Held`), `XrSession.SetCards` (a group, so the
map's layers sit beside the assistant's cards) and `HeldCards`, `ShownView.WristFrom` (the assistant's wrist cards
start above the line, and above the mini-map while it's on the hand), `PanelShots.cs` (one line).

## The location line

`Where.Now`: the region and its rating, the parcel (the region's parcel map and properties, asked for as voice asks
when unknown), and the icons the viewer's location bar shows, by its rules (`allowAgent*`): no voice (region or parcel),
no flying (region `NoFly` or the parcel's), no pushing (either restricts), no building (the parcel lets nobody build:
your own rights don't clear it, as the viewer's EXT-4610), no scripts (region skip-scripts or the parcel's other-scripts
switch), avatars hidden from outside (`SeeAVs` off), damage (region or parcel) with your health beside a heart, and a
tag when the parcel is for sale to you. Then the lag meter's three bars (below). Glyphs, struck through for "no"; the
rating a letter in a recess (G, M, A).
- **Headset**: just above the left wrist (3 cm up, facing the eyes; a fixed size in the room, the panel's angular size
  at 0.45 m, so it doesn't shrink as the hand comes closer) while it's looked at as a watch: its place within 25 degrees
  of the middle of the view (gone past 29), the left palm turned to the eyes (within 65 degrees, gone past 75), nearer
  than 0.85 m; gone the frame the look leaves it (the user, 2026-10-29). Held like the forearm's tokens: its layer goes
  last, over the hands' layer (`XrSession.HeldCards`, docs/vr.md > Over the menus), so nothing covers it, and it's left
  out of the hands' cut, so the hand behind it still covers the menus behind that; the pointer's beam and the tokens
  pass under it. The assistant's wrist cards stack above it (and above the mini-map while that's on the hand). The
  right ray on it lights it; the trigger on it opens the mini-map over it, closes it, or brings it back to the hand
  from out on its own. With the pointer off (A), boards take no ray, so it shows but isn't pressed, as the HUD token.
- **Desktop**: top left; a click on it (a hand on the mouse, `RealInput`) opens or closes the mini-map under it.
- Settings (`SojournSettings`, saved in `map-settings.json` beside the VR settings): `location_line` (on), `lag_meter`
  (on). Both on by default: in the headset the line only shows when you look at your wrist. In the menu, one row:
  location line off, on, + lag. The maps need no row (the line opens the mini-map, its globe the world map).

## Statistics at a glance

`Where.StatsNow`: the region's SimStats (LibreMetaverse's `Simulator.Stats`, about once a second: time dilation, frame
rates, the frame's parts in ms, people, objects, scripts), the ping (`LastLag`, missed pings; LibreMetaverse keeps no
packet loss) and this client's frame rate (the frame loop's, last second). The lag meter's three parts, each fine, slow
or very slow, by the viewer's thresholds (`floater_lagmeter.xml`): the client 15/10 fps on the desktop, in the headset
90%/60% of the display's rate instead (the desktop's numbers mean nothing at 90 Hz); the network ping 300/600 ms; the
region's frame time 33/50 ms (30/20 fps), and time dilation under 0.95/0.75 as well. The slowest part of the region's
frame over 20 ms is named, as the lag meter does. On the line as three bars (full when fine); under the world map
with nothing picked as one line (region fps, time dilation, ping, people, your fps); `stats` for the assistant with a
verdict ("is it me or the region?").

## The maps

Two boards in one look and one drawing (`MapBoard`, `MapPainter` with a layout each), north up (the viewer's mini-map
turns with you by default; not here). Each keeps its own view; the layers (land for sale, events) are shared.
- **The mini-map** (llnetmap.cpp's place): out on its own and on the desktop 272 logical px square, on the hand
  wider (below); about 125 m across when it first opens (0.5 m a pixel), following you; reopened from the line it follows you again, what was picked on it let go (its zoom kept).
  Keys along the top: zoom out, in, find me; on the right the globe (the world map up or away; lit while it's up),
  then, on the hand, out (a frame with an arrow leaving it), or, out on its own, back to the hand (the arrow going in)
  and ×. What's picked shows on a glass band over the map's foot with Track and Go (words, alike); Go keeps the mini-map up,
  following you again. Where it is (`MiniHome`: off, hand, free) is kept in `map-settings.json` (`MiniMap`).
  - **On the hand**: stacked over the location line in its plane (its bottom 6 mm above the line's top), at the line's
    scale (the panel's angular size at 0.45 m, ~0.55 mm a logical px at UI size 1), a fixed 380 x 306 px (a 250 px map;
    ~21 x 17 cm), about a typical line's width; while it's on the hand the line is drawn at least 380 wide, its meter
    then at the right end (`MapLayout.Hand`; the user, 2026-10-29: a width following the line from parcel to parcel
    felt wrong). While it's open it stays over the hand whatever the look or the palm, until closed (the user,
    2026-10-29: even a very loose rule felt like a bug when it went). Both face the eyes whatever the hand's turn
    (placed above the wrist in the room's up, never the hand's). With the line switched off it shows where the line
    would be. Held like the line (`XrSession.HeldCards`, key "map mini held"): its layer over the hands' layer, out of
    the menus' order and the hands' cut. The right ray presses, drags the map and picks on it, and the right stick
    zooms it (below); its margins don't drag it (it goes where the hand goes). Its last picture is kept while it's out
    of sight, so it's there the moment the wrist comes up, redrawn then.
  - **Out on its own**: a board like the world map ("map mini", in the menus' order and the hands' cut), 1 m away, 32
    degrees right of where you face with its middle 2 below the eyes (clear of the world map, ~44 degrees wide and
    centred, and of the panel and cards low in view), at the panel's angular size, following lazily (carried past 40)
    until dragged somewhere (`PanelPlacement` "mini map", kept by `MenuPlaces`). Pressing the line brings it back to
    the hand, as its own key does; × closes it.
- **The world map** (llworldmapview.cpp's): 624 px wide, a continuous zoom from the world down to the mini-map's scale
  (tiles, region names, crowds counted, and close in people as dots). Opened by the mini-map's globe or by the
  assistant (`show` at a place), and kept as it was between openings: the place, the zoom, following or not, what's
  picked and its Go (the user, 2026-10-29). The first opening follows you a few regions across (2 m a pixel, 1.2 km).
  Keys: zoom out, in, find me (follow again), land for sale, events, `?` (the legend in the map's colours), ×. What's
  picked shows in the strip under the map with Track and Go; with nothing picked, the region in the middle and
  the statistics line. Go puts it away, the pick kept with it.
- **Tiles** (`MapTiles`): the viewer's world mipmap, `{map server}map-{level}-{x}-{y}-objects.jpg` from the login's
  `map-server-url` (Second Life's `https://map.secondlife.com/` if none), the level by `scaleToLevel` on the board's
  raster, a coarser tile's part while a finer one comes, four fetches at a time, nearest the middle first, 192 kept in
  memory; a missing tile (no region) is water for 10 minutes. OpenSim serves every level.
- **What's on them** (`WorldMap`): regions' names and edges from MapBlocks (16-region squares around what's in view,
  each at most every 5 minutes; names drawn from 96 px a region, as the viewer); land for sale, events and telehubs
  grid-wide (`LandMap.ItemsAsync`, docs/land.md; on opening and every 5 minutes while up); people counted per region in view at
  most once a minute each (the viewer's `AGENTS_UPDATE_TIMER`, while 8 m a pixel or closer); close up (2 m a pixel or
  closer) everyone around by their coarse positions instead; friends who let you see them on the map (FindAgent every
  30 s, ringed and named); you as an arrow the way you face, the 20 m and 100 m rings. Land for sale (a tag) and events
  (a star) by the world map's keys (sale off and events on by default, kept). Asked for only while a map shows.
- **Zooming by the stick** (both, wherever they are; the user, 2026-10-29): with the right ray on a map, the right
  stick up zooms in and down out, smoothly (1.8 halvings or doublings of scale a second at full tilt), as the reader
  scrolls (`Control/SheetStick.cs`, `MapWiring.Sheet`): meanwhile the stick doesn't turn, jump or climb, and a push
  begun on the map stays the map's until the stick is back in the middle. Left and right do nothing there.
- **Pressing** (both): a press on the map that doesn't move picks the spot (the nearest mark within 12 px, else the
  place); Track drives the tracking beacon (`Beacons/Tracker.cs`, docs/rendering.md: someone picked is tracked
  as them, anywhere else as the place; pressed while tracking, it stops); pressing the spot again lets it go. Go
  teleports there (`AgentSession.TeleportAsync` by region name, the point at ground level): the person's own press,
  nothing waits for a confirmation, as the viewer's map.
- **Headset**: the world map's own quad layer (`XrSession.SetCards` group "map", so menu order, walls, the mirror and
  "what the person sees" treat it as the cards), its frost drawn into the eyes from the boards' atlas. 1.25 m away, its
  middle 3 degrees above the eyes (its bottom clears the panel resting 18 below), at the panel's angular size, following
  lazily (carried past 40 deg) until put somewhere: the right trigger on its edge or top strip drags it
  (`PanelPlacement` "map", kept with the other menus); on the map the trigger held drags the map. The ray goes to the
  nearest of the line, the mini-map and the world map. Hidden while a confirmation waits or the interface is hidden.
  Each drawn on a worker whenever what it shows changes (and every 2 s while up: people move), one drawing at a time.
- **Desktop**: the mini-map under the line at the top left (no out or back keys), the world map beside it (under the
  line while the mini-map is closed), at the desktop's UI scale; the mouse presses, drags, the wheel zooms.

## SojournMap

- `where` -> the line's facts (`no_fly` ... `health` in damage areas, `for_sale`).
- `stats` -> `{client, network, region, verdict}` (above).
- `show {region?, at?, across?, pick?, sale?, events?, free?}`: with `region` (its middle, or `at` in it) or `at`
  alone (in this region), the world map up there; without, the mini-map, following the person: on the hand unless it's
  out on its own, or as `free` says (true: out on its own, in view; false: on the hand). `across` the metres the map
  spans, `pick` the place at its middle picked so Go is one press (on the world map without it, what was picked stays).
  Answers `{shown, map: world | mini, where?, region, across}`. `hide {map?: world | mini}` (default both). `state` ->
  `{world {...}, mini {..., where: off | hand | free}}` (what each shows, what's picked; on the desktop where the line and
  each map are drawn, and their scales).
- `friends {wait_s=3}`: online friends, and where those who let you see them on the map are.

The map's items are `SojournLand mapItems`; teleports are the go tool's. The assistant reaches these through its `map`
tool (`assistant/moretools.py`: where, stats, show, hide, friends, track); in blind incognito only `stats` (it names
nothing), `show` around them (no region or place) and `hide` (`Session/Private/Allowlist.cs`).

## Testing

`python tools/map.py [--dll DIR]` (the GPU lock first): two desktop clients in Atelier; where, stats, the line clicked
(the mini-map under it), its globe (the world map), + zooming, a spot picked in Atelier East by a click, Go taking you
there with the pick kept, show at a named region with the place picked, the world map hidden and opened again from the
globe as it was, show without a place (the mini-map), hide, friends; shots into `local/map/a/shots`.
`SOJOURN_VR_SCRIPT=map` (`Xr/VrScript.Map.cs`) in the XR Simulator: the wrist raised, the line pressed (the mini-map on
the hand), its globe, + pressed, the map dragged, a spot picked, the board dragged 10 degrees, ×; then the mini-map
popped out and put back; `SOJOURN_MAP_SHOTS=1` takes shots. `--panel-shots` writes `map-*.png` (close, near, far,
help, picked; mini-hand, mini-free, mini-desktop) and `line-*.png` (plain, everything blocked with damage, lag,
general without a parcel) in each look, tiles from the local grid (`SOJOURN_MAP_SERVER`).

## Not done

- The mini-map's property lines and owners' colours (Firestorm's); avatars above and below you marked (the viewer's
  up/down carets); the mini-map turning with you.
- The world map's search box (`SojournSearch` and `show {region}` cover it), landmarks and teleport history on the map.
- Statistics beyond the lag meter's (the viewer's full Statistics floater: texture and mesh counts, bandwidth); the
  view line and `tools/vrlog.py` have our side.

## Checks only Second Life or the headset can do

The line, the mini-map (on the hand and out on its own) and the world map were tried in the headset on 2026-10-29,
and the rules above came from that. Still to see on Second Life:
1. A no-fly or damage region (a combat sim) shows its icons and your health; a sandbox shows none.
2. Zoomed out, crowds counted in regions; a friend who lets you see them on the map ringed and named; land for sale
   (the tag key) and events (the star) where they are.
3. Pick a spot in another region and press Go: you arrive there.
4. Busy place: the bars drop when frames do, the region's when it lags; "is it me or the region?" to the assistant.
