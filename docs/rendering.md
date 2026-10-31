Written by Claude Opus 5.5, 2026-10-26. Agent-facing. What Sojourn's renderer adds to or changes from the pipeline it
ports (Multitude's `docs/render.md` describes the port itself): distances from the head, mirrors, how blended faces sort, the scene budget's
hysteresis, the alpha shader's discards, light tiles, the two eyes' passes (multiview), where sun shadows end, measuring on a shared GPU,
name tags and floating text in the eye's depth, the headset's near plane and detail levels, objects kept on screen through updates, flexible prims, avatar physics,
avatars' lip sync, others' beams and point-at, standing feet and the local fixes,
water (exclusion surfaces, under water), the environment over time (the person's own, cross-fades, clouds), parcel
lines, the interest list; and the test scenes for them.

# Rendering: Sojourn's own

## Distances from the head

The user's rule (2026-10-28, in the headset at Mirromere: loading and the ocean's edge followed a flat plane in front of
them): wherever a distance decides whether or how something is drawn, loaded or sorted, it's the distance from the
head's centre (the point between the eyes, the centre view's `Position`), so turning the head never changes what's drawn
or at what detail. A general VR principle here, desktop included. The viewer measures several of these along the
camera's view axis or by its far clip plane; these are deliberate departures (Claude Opus 5.5, 2026-10-28):
- **Draw distance**: a sphere about the head (`ViewSetup.Beyond`; the projection is infinite, so no far plane cuts
  anything), with a 2 m band at its edge (`ViewSetup.EdgeBand`, `SceneObject.InReach`): something in reach stays until
  it's 2 m past it, so leaning doesn't blink what sits on the edge. Tested in view or not, as is the load governor's
  cap (`NewDistance`: before, "not drawn in the last second" made far things behind you drop out under the cap once
  you'd looked away). The viewer culls by its far clip plane.
- **Water's far edge**: the viewer's projection far plane (1024 m) cut the water planes along a plane; here a sphere
  of 1024 m about the eye (`water.frag`, `waterhaze.frag`).
- **Blended faces and particles sort by distance**, not depth along the view axis (below).
- **Sun shadows**: the cascade splits are radii, the shader picks a cascade and fades the far end by distance from the
  eye (`shadows.glsl`), and the splits come from the nearest and farthest receivers within the draw distance in every
  direction rather than the visible ones' depths (`AabbSceneExtents.TryGetRadialRange`). Each cascade is fitted to a
  depth slice holding its spherical shell: the far end as before, the near end pulled in to the shell's radius along
  the most slanted corner ray (`SunShadowSetup`, `minDp`). The governor's shadow trim is a radius too. Measured on the
  CPU (a flat 16 m-patch world, the Quest's centre view, sun at 45 degrees, five head yaws; the shadow fit run before
  and after): the splits no longer move with yaw; texels a metre on the ground 2-4% fewer in the near cascades,
  5-13% fewer in the far two; casters inside the cascades' cull volumes +3-14% (cascade 1), +9-13% (2), +7-21% (3),
  so the shadow pass draws ~10-15% more in its far cascades. Projector shadows' fade follows the same radii. Where
  sun shadows end, and the coarse far cascade that carries them on: below, Where shadows end.
- **Linden trees' detail** no longer falls off the view's axis (`TreeBuilder.AppAngle`: the viewer's
  LLFace::calcImportanceToCamera, every tree taken as straight ahead).
- **Avatars**: which are whole and which impostors, and the avatar detail factor (the viewer's sNumVisibleAvatars,
  here the avatars within the draw distance not drawn as impostors), rank every avatar within the draw distance, in
  view or not (before, the nearest 16 among those in view or casting shadows, so turning could swap them).
- Already radial: objects' detail levels (`VolumeLod.CalcLod`), the scene budget's on-screen size, texture and mesh
  priorities by size on screen, lights, particles' reach, media, avatar detail and impostor ranking by distance.
- Left view-dependent on purpose, being about what's in sight rather than distance: frustum culling; what's fetched
  for things out of view (nothing for an object neither seen nor casting into a shadow, until it's seen; textures at a
  tenth of their size for one only casting); which mirror is rendered (one at a time), which two projectors cast
  shadows, which reflection probes fill the view's slots, flexible prims stepping only in view. The sun cascades are
  still fitted to what's in view (only their splits and reach are radial).

## Mirrors

A prim set as a **box** reflection probe with the **mirror** flag (`PRIM_REFLECTION_PROBE, TRUE, ambiance, clip,
PRIM_REFLECTION_PROBE_BOX | PRIM_REFLECTION_PROBE_MIRROR [| PRIM_REFLECTION_PROBE_DYNAMIC]`, or the build floater's
"Mirror" choices) is the viewer's hero probe (`llheroprobemanager.cpp`, `reflectionProbeF.glsl` `tapHeroProbe`).
Code: `Realtime/FrameRenderer.Mirror.cs` (choice, views), `Frame/Renderer.Mirror.cs` (the pass), `World/Avatars.Mirror.cs`
(avatars in it), `Shaders/probes.glsl` `tapHeroProbe`. Setting `mirrors` (on) and `mirror_resolution` (0.5), menu > view.

How the viewer does it, and what's kept:
- One mirror at a time (the "hero"): within the draw distance and in view. Its plane is the probe prim's +Z face
  through its centre; the reflection shows what's in front of that plane (+Z side) and nothing behind it.
- The reflection only shows on surfaces inside the probe's box, in front of its plane or up to ~4 cm behind it, faded
  over the box's outer tenth, and only glossy ones: legacy shiny/specular faces take it by their environment intensity
  (always full strength in the env term), PBR ones by glossiness (from roughness 0.25 down to 0 it blends in fully).
  Exactly the viewer's weights.
- **So a mirror is two prims**: a glossy surface (blank texture; legacy specular glossiness 255 and environment 255, or
  PBR roughness 0, metallic 1 for a true mirror) and a separate probe prim around it, rotated so its +Z points out of the
  surface, its centre plane ~1 cm in front of the surface (so the glass isn't in its own reflection), made phantom and
  fully transparent (the build floater does that when a prim becomes a probe). A single prim flagged as a mirror (the
  user's plywood cube, 2026-10-26) stays plain in the viewer too: its faces sit on the box's edge, where the weight is 0,
  and it's not glossy. The community tutorial: modemworld.me 2024-06-12 "creating a simple (prim) mirror".
- Avatars and particles appear only in **dynamic** mirrors (the viewer's cube snapshot leaves them out otherwise).
- Off in all the viewer's graphics presets (RenderMirrors); on here, since a mirror costs nothing until one's in view.

What's different, for a headset:
- **Each eye has its own reflection.** The viewer renders a cube map from the camera mirrored in the plane (all six faces
  a frame at Ultra) and samples it by the reflected direction. One point can't serve two eyes: every reflection would
  have the same image in both, so it would look infinitely far away. Here each eye renders the frame's draw list from
  itself mirrored in the plane (its x axis turned round so it stays a rotation; the projection's left and right
  swapped), into an image that a pixel at u reads at 1 - u. Exact planar reflection per eye.
- **Cheap where it can be**: only the mirror's pixels are rendered (the render area is its box projected), at
  `mirror_resolution` of the eye's size in 128-pixel steps, as a cube snapshot face is (no SSAO, no stars, the eyes'
  shadow maps, no particles), clipped at the plane by `gl_ClipDistance` (scene and water vertex shaders), blended faces
  sorted again from the mirrored eye; 5 mips for rougher surfaces. A mirror under ~16x16 pixels isn't rendered. One draw
  list serves both: culling keeps what the eyes see or what the mirror can show (a frustum from the mirrored camera
  through the mirror's outline, `Frustum.Either`).
- **Which mirror**: the viewer takes the nearest in view; a headset sees ~100 degrees, so the nearest was often one at
  the edge of sight while the one looked at stayed plain. Here distance counts more the further a mirror is off the line
  of sight (x3 at 90 degrees); one seen from behind isn't a candidate; the current one stays until another scores 10%
  better (no trading places as the head moves).
- **You in it**: the own body in the headset is headless for the eyes. In a dynamic mirror it's drawn a second time,
  whole: the same pose (arms on the controllers) with the head and what's worn on it put back. Instances carry a view mask
  (`InstanceFlags` MainOnly/MirrorOnly, checked in `scene.vert`), so the eyes never see the head and the mirror never
  sees the headless copy; a static mirror marks every avatar's draws MainOnly.
- **Pictures and the camera's preview** (Claude Opus 5.5, 2026-10-29; before, both showed a mirror as its glossy
  surface with the probes' blur): each picks its own mirror from its camera, as the eyes do (a still afresh, the preview
  with a hold of its own), and renders its reflection the same way, but at its own full size (no `mirror_resolution`)
  and through its own targets before it draws there (no second set). Images of their own (`Renderer.StillMirror`, let
  go after the still; `PreviewMirror`, with the preview's targets). A still's tiles and 360 faces each take theirs.
  The preview's mirror is a second hero in the person's frame, culled in and sorted from its own mirrored camera;
  avatars in it follow the eyes' mirror if they have one (a static and a dynamic one in view at once: the preview's
  shows what the eyes' does). Checked in the XR Simulator on the mirror platform: a still's render 8.5 -> 18 ms at
  1280x720 and 13 -> 24 ms for 3000x1688 in two tiles with the dynamic mirror in view (a second render of what the
  mirror shows, at the picture's size); framing the mirror in the preview, GPU 6.8 -> 7.2 ms ("mirror" block 0.63 ->
  1.0); the own avatar whole in the reflection (head put back), no validation errors.

Numbers (RTX 5070): desktop mono 1280x720 0.13-0.2 ms GPU for a mirror filling a quarter of the view; XR Simulator,
Quest 3 eyes at full size, a 2.6 m mirror 3.5 m ahead: 0.27-0.5 ms a frame for both eyes, 72/72 fps. CPU: one draw list
as before (plus what only the mirror sees), a re-sort of the blended faces.

Particles show in dynamic mirrors (2026-10-26), as cube snapshots of dynamic probes include them: the frame's quads
turned to face the mirrored eye (ribbons and quads turned to their velocity as they are), sorted from it among the
mirror's blended faces, clipped at the plane (`particle.vert`), in `Renderer.Mirror.cs` MirrorParticles.

Not done: the mirrored head follows the
animation, not the headset's rotation (SelfBody carries no head orientation); a mirror inside a mirror (none, as in the
viewer's mirror pass).

## The camera's preview

Claude Opus 5.5, 2026-10-29. The handheld camera's panel (`docs/assistant.md` > Pictures by hand) shows the picture
it's about to take, live. Code: `Realtime/FrameRenderer.Preview.cs` (the view, its targets), `Frame/Renderer.Preview.cs`
(the pass), `Realtime/Overlay.cs` (`OverlayScene.Preview`, the quad; `overlay.frag` styles 29-30, binding 9); the
client's `Shutter.Decorate` asks for it.
- Another view inside the person's frame, like a mirror's, not a still: a still builds a second draw list on the render
  thread and waits for the GPU, a hitch every frame. Only on frames whose overlay frames a preview; nothing renders
  otherwise. The draw list is culled for its view as well (`Frustum.Either`, which takes any number of volumes now).
- Drawn as the snapshot will be: a camera at the panel's centre along its facing, the shutter's tangents, a mono
  view's 0.1 m near plane, full shading and post (SSAO, glow, SMAA; half-size light if that option's on), the eyes'
  shadow maps (and kept far cascade) and probe list. Recorded after the shadows, before the mirrors and eyes.
- The exposure as it stands (`HeldExposure`: the last frame's, or this frame's once an eye made it), so the eyes'
  adaptation never sees it. Timed as one profiler block, "preview".
- A fixed 768x768 target (about the panel's size in a Quest eye), its own rather than the shared cache's, the panel's
  shape stretched over it: the hands moving remake nothing. Made when the camera comes out (the client sends a
  standby while it's out and nothing is framed) and let go 30 s after it's put away, through the frame's retire list
  (no wait on the GPU).
- On the panel: the output drawn at its corners in the eye, opaque, tested against the eye's depth and writing it
  (later tests and the compositor's reprojection see it), both pushed 10 cm behind the plane so the hands holding its
  corners are always drawn over it; its footprint also goes into the hands' mask (style 30), so it shows over the menus
  where it's in front of them, as the hands do.
- What differs from the saved picture: shadows outside the eyes' view (the cascades are fitted to the eyes); no see-through layer cap (none is on by
  default). Both show mirrors' reflections (Mirrors > Pictures and the camera's preview).
- Checked in the XR Simulator on the dense scene, 2026-10-29 (Claude Opus 5.5): framing costs 1.1-1.2 ms of GPU
  ("preview" block; 7.1 -> 8.2 ms) and ~0.15 ms of render-thread CPU; standby and put away cost nothing and show no
  block; the preview matched the saved picture in framing, orientation, exposure and colour (wide, tall, 3:1); the
  Vulkan validation layer found nothing of its own. Bringing the camera out made a 55-70 ms display step (the targets'
  allocations; twice of two), letting the targets go dropped one frame. A surface less than 10 cm behind the panel
  hides the preview (`PreviewBehind`), as does anything nearer than it.

## Blended faces: sort order

- Each blended face sorts by its own centre, pulled toward the camera by a quarter of its world box
  (`LLDrawable::updateDistance`; was the object's centre for every face), measured from the head's centre along the line
  to it: `r - sum(halfsize * 0.5 * u^2)` for the unit direction u (the viewer: depth along the view axis, the pull
  weighted by the axis). Particles by their distance, avatars' blended parts as before by theirs, so the three now share
  one key; mirrors re-sort by the faces' centres' distance from the mirrored eye; HUDs keep the viewer's key along
  their axis (`ViewSetup.PlanarSort`: their pieces stack by offset along it). Mirromere, 2026-10-28: a band of blue streaks over a birch's leaves
  was a thin blended plane (5 x 9.6 m, alpha 0.74, just above eye height) passing through the birch's crown, seen
  almost edge-on. Interpenetrating blended surfaces have no right order; along the view axis their keys lay within a
  few tenths of a metre, so which drew last changed as the head turned or tilted (in the viewer too); by distance it
  stays put while the head turns. Since 2026-10-28 (House of Legends, below) that's the order within a prim: prims sort
  first, each as the viewer's spatial groups do (`LLSpatialPartition::calcDistance`: the box's centre pulled toward the
  camera by a quarter of its half size, half the faces' pull), its whole box from the head's centre
  (`RegionScene.GroupDepth`, `DrawCmd.SubDepth` the face's key). Each prim stands for a group; the viewer's octree
  groups neighbours of a size together, whose faces then interleave. HUDs and mirrors keep their face keys alone.
  Ties keep the order they were drawn in (`List.Sort` isn't stable: an object's faces all tied, and their order changed whenever anything else in the list did:
  overlapping leaf faces flickering frame to frame). Particles likewise. `RegionScene.AlphaDepth`,
  `Renderer.BuildBatches`. The face's box is kept with its geometry (`FaceGeometry.Center`, `HalfSize`).
- Rigged blended faces are drawn first with depth writes, unsorted, as `LLDrawPoolAlpha::renderPostDeferred` does
  (rigged pass, then the rest); checked against the viewer, unchanged.

## The scene budget's band

Something the budget leaves out (under `Budget.Pixels` on screen, or its shadow under `ShadowPixels`) comes back only
once it's a quarter over the limit (`RegionScene.Budgeted`, per object, the main view's state). Without it an object
near the threshold popped in and out as the head moved: leaves in Mirromere's forest at the budget's top step (6 px).

## The alpha shader

- `alpha.frag` discards below the alpha pool's MINIMUM_ALPHA (0.004) on every blended path, as the viewer's alphaF,
  fullbrightF (IS_ALPHA) and pbralphaF do (ours had 0.002 on one path and none on the others).
- The sun shadow is sampled only for a fragment that shows (after the discard) and not for fullbright faces. Close-up
  layers of flower cards are mostly transparent texels that each paid for a shadow lookup. Measured on 12 flowered panes
  filling two Quest-sized eyes (`render_scenes.py flowers`, standing in the middle facing -x): alpha 1.72 -> 1.30 ms per
  eye, the frame 8.5 -> 7.1 ms. Solid translucent glass gains nothing from this: every layer is lit in full, as in the
  viewer (see performance.md for what's next).

## See-through surfaces: simpler lighting and a layer cap

Claude Opus 5.5, 2026-10-27, the user's approvals of both (simpler lighting "probably yes", a small visible difference;
the cap their own idea). `Frame/Renderer.Layers.cs`, `Shaders/alpha_simple.vert`, `alpha_count.frag`, `layers.glsl`,
`alpha.frag` (SIMPLE_LIGHT, LAYER_CUT). Settings `simple_see_through` and `alpha_layers`, menu > view "see-through
light" (full, simple) and "see-through layers" (all, 4, 8, 16). Main eyes only: mirrors and probe captures as before.
- **Simpler lighting** (off by default, for the person's eye): what varies slowly over a surface is computed per vertex
  and interpolated: the ambient with the reflection probes' (sampleAmbient), the atmospherics (sunlight, haze and its
  fading) and the six forward lights. Per pixel stay the texture, the sun's angle and shadow (normal-mapped where there's
  a map), and reflections (materialF's gloss and environment, PBR's radiance). Back faces of double-sided faces take
  their front's ambient. Fullbright faces unchanged. The probes' per-pixel ambient loop was most of the cost.
- **The layer cap** (off by default). Before the blended faces, `alpha_count.frag` draws them nearest first against the
  opaque depth without colour (fullbright ones not counted: cheap to draw), keeping per pixel a record: a budget spent by
  each fragment of alpha 0.1 or more by what it hides (-ln(1 - alpha), 64 units for 1% let through) but at least 64/N, and
  the farthest counted depth. `alpha.frag` drops a fragment that shows but lies behind every counted layer once the budget
  is spent. Whichever fragments get counted first, what's dropped lies behind layers letting through under 1% of it, or
  behind N layers of alpha 0.1 or more (at N 16, a stack of faint ones still passes up to 18%). Its alpha is read as
  alpha.frag reads it (explicit gradients, taken before any early return), the record checked after the alpha test so
  transparent texels don't pay for it. `SOJOURN_LAYERS_DEBUG=1` draws what it drops magenta.
- **Measured** (`render_scenes.py flowers` and `mist`, two eyes of 2448x2672 with the Quest's frusta, shadows 3072, median
  ms a frame by `SOJOURN_AB=alphalayers|simplealpha`; views: the 12 panes, the field, the glass cases at arm's length, the
  mist):

  | | panes | field | glass | mist |
  |---|---|---|---|---|
  | alpha, as the viewer | 5.95 | 3.11 | 2.45 | 1.60 |
  | simpler lighting | 1.75 | 0.84 | 0.66 | 1.59 (fullbright) |
  | cap N 16: count + alpha | 1.6-1.9 + 6.1 | 0.72 + 3.3 | 0.55 + 2.6 | 0.08 + 1.5 |
  | cap N 4: count + alpha | 1.8 + 6.0 | 0.71 + 3.6 | 0.54 + 2.5 | 0.08 + 1.6 |

  The cap drops almost nothing here: through the panes a pixel meets one to three lit petals (two thirds of the flower
  texture is fully transparent), so few pixels hold a stack deep enough, while the count is another pass over every
  blended fragment. At N 4 it visibly removes what's seen through four panes of the nested glass cases and stems seen
  through four flowered panes; at 16 the pictures match the viewer's within run-to-run noise. Simpler lighting's
  pictures differ faintly at petal edges and on the glass. Pictures: `local/levers/compare/` (each view before, simple,
  16, 4, and the differences x8). Where it might pay: dense blended foliage whose texels are mostly opaque (blended tree
  canopies seen from inside); untested.
- Tried and changed: the cut first checked before the alpha test, which made every transparent texel load its record
  (the alpha pass slower than without it).

## Light tiles

Claude Opus 5.5, 2026-10-27. `Frame/Renderer.Tiles.cs`, `Shaders/tilecull.comp`, `tiles.glsl`. The local-light and
projector passes are fullscreen loops over every light in view (the viewer draws a volume per light); busy places hold
100-160 lights and 40-60 projectors in view, and those two passes were the costliest of the frame there (Envision: 5 ms
an eye). After each main eye's G-buffer a compute pass lists, per 16x16-pixel tile, the lights whose sphere (the one the
passes test per pixel) reaches the tile's pixels: its four side planes and the box between its nearest and farthest
depth. Projectors must also have the tile in their frustum (five planes from their texture matrix), unless a pixel there
is a legacy face with an environment reflection, which takes a projector's light from outside its frustum. The passes
loop over their tile's list (up to 255 a tile; past that, everything). `projectors.frag` also skips a projector's work
at pixels outside its frustum where nothing can take it. Same picture (checked pixel for pixel against the old loop);
probe faces and mirrors still loop over everything. `SOJOURN_LIGHT_TILES=0` turns it off for comparing. The tiles
themselves cost ~0.35 ms an eye at Link's full size. Both passes read their pixel's tile header before the G-buffer and
leave where nothing of theirs reaches (2026-10-27: -0.4 to -0.6 ms a frame in most test views). The loops live in
`locallights.glsl` and `projectors.glsl`, bounded whatever a record says (at most 255 a tile, 1024 a view, indices
checked against the counts). Folding them into the sun and ambient pass, or merging the two, was tried and is slower
where rooms are full of light: the big shader runs everything at the loops' low occupancy (club 8.6 -> 10.7 ms).

## Two eyes: multiview and per-eye passes

Claude Opus 5.5, 2026-10-27. `Frame/Renderer.cs` RecordStereoScene, `Frame/RenderTargets.cs` (Layers, Eyes). Two eyes of
one size render through a stereo target set: the G-buffer, depth and HDR scene are two-layer images, and each eye has a
set of its own over its layer (2D views, their own bindless slots; the rest of the targets shared, as the eyes take
turns). The forward passes (fullbright, glow, avatar alpha) and the alpha pass (blended faces, particles, their glow)
draw both eyes at once (VK_KHR_multiview: pipeline variants with the view mask, made on first use from each pipeline's
description); `VIEW` and `SKY` are read by `gl_ViewIndex` (the eyes' views and skies side by side in the arena).
The G-buffer, the lighting chain, water, haze and post go an eye at a time: on the RTX 5070 the four-target G-buffer took
2-3 times as long drawn to both eyes at once as an eye at a time (club 0.6 -> 1.9 ms a frame, forest +0.4-0.9), and
the multiview forward and alpha passes measured neutral (within 0.1 ms; they're recorded once instead of twice).
Mirrors are rendered for both eyes first, each into its own image. `SOJOURN_MULTIVIEW=0` draws the eyes one after the
other; `SOJOURN_MULTIVIEW_GBUFFER=1` puts the G-buffer in multiview too. Pictures match the old path at its run-to-run
noise.

## The lenses' hidden area

Claude Opus 5.5, 2026-10-27. `Frame/Renderer.Lens.cs`, `Shaders/lensmask.*`, `lensHidden()` in `common.glsl`,
`Sojourn.Client/Xr/XrLensMask.cs`. Meta's PC runtime offers XR_KHR_visibility_mask: per eye, the triangles of its image
the lenses hide. Each eye gets a mask (R8) drawn from them, redrawn only when the mesh, the eye's size or its projection
changes, and the per-pixel passes skip what it marks: the sun-light map and its blur, sun and ambient, local lights,
projectors, haze, fullbright, glow, blended faces, particles, water, SMAA's edge detection. Nothing seen changes:
- the mesh is scaled out from the eye's axis until its edge is 96 px inside the hidden area (`LensMargin`): every
  visible pixel's neighbours (the light map's blur, SMAA's searches, CAS, glow's ~50 px) are still shaded;
- the G-buffer is drawn whole: SSAO samples depth up to the viewer's 500-px maximum radius;
- the window the luminance pass samples for dynamic exposure (the image's middle 60%, nudged down) and the margin
  around it are never marked;
- frames that keep the eye's image for someone (pictures, videos: `CaptureTo`, `VideoTo`) don't use it; developers'
  screenshots (`CapturePath`) show it.
Skipped pixels end black (the desktop's mirror window shows the corners black). `SOJOURN_LENS_MASK=0` turns it off;
`SOJOURN_TEST_LENS_MASK=R` puts a round test lens of radius R (tangents) on the desktop's `--quest-eyes`. With R 1.2
(19% of an eye hidden, 65% of that skipped after the margin and the exposure window): -0.33 to -1.15 ms a frame on
the test views, the visible area matching the old build at its run-to-run noise. Meta's real mesh is untested (the
XR Simulator doesn't offer the extension): the console logs each eye's triangles when it arrives.

## Small, invisible savings (2026-10-27)

- Shadow casters that cut nothing (no alpha, not water) are drawn depth alone, without a fragment shader (the same
  depths in any order): -0.03 to -0.09 ms a frame on the test views (the forest's casters are mostly masked leaves).
- The G-buffer isn't cleared: geometry or the sky writes every pixel of all four targets.
- Blended faces draw with `alpha.frag` compiled for their material's path alone (`ALPHA_PATH` 1-4: PBR, fullbright,
  alphaF, materialF; `Renderer.AlphaPaths`), their sorted batches split where the path changes: the same code a fragment
  runs, without the registers the other paths hold. Flower field alpha 7.5 -> 5.6 ms a frame, the field 8.1 -> 5.7.
- The far sun cascades take turns (`Frame/Renderer.Cascades.cs`): each frame one of cascades 2 and 3 keeps last frame's
  map when this frame's fitted box lies inside it (same region, sun, map size; both with receivers), its matrices
  turned to the eyes' and mirrors'. A still view saves 0.45-0.78 ms a frame at 3072 (cascade 2 is the costliest:
  0.45-2.9 ms in the forest); any movement of the view changes the fit, and then all four are drawn as before. Moving
  casters' far shadows are a frame late while it holds. The console says how often it held every 900 frames.
- A probe capture's sun shadow (Claude Opus 5.5, 2026-10-29): a capture spread over frames drew it into cascade 0 on
  each of them, since the frame's own shadows overwrite that map after the captures. Now it's drawn on the pass's
  first frame, copied into an image of its own (`Renderer.KeepCaptureShadow`) and copied back before the pass's faces
  on its later frames; the shaders read cascade 0 as before. Captures -25-35% a frame while a place fills in. A map
  the captures sample directly saved more (-46%) but moved probes by a level here and there: another constant index
  into `shadowMaps` compiles to slightly different arithmetic on this card, as did an index read from the sky.
- Precise barriers for the renderer's own transitions (stages and accesses from the two layouts) are behind
  `SOJOURN_PRECISE_BARRIERS=1`: not measurable under the shared GPU's load, so left off.

## GPU options to judge (2026-10-27)

Claude Opus 5.5, the user's decision to try them as settings (CLAUDE.md, Next). All off by default; each is a row in
menu > view and a `SojournSettings` name, and none changes the look unless the person turns it on. Measured on the perf
grid's forest (`places` scene, Atelier) by the bench: two eyes of 2448x2672 with the Quest's frusta, shadow cap 3072,
noon, five views (forest, ruins, far, close, and an orbit circling at 7 m/s and 18 degrees a second, so the view always
moves). Pictures against a run of the same build with the option off, which repeats itself to 0.00-0.01/255 on the
still views. Results and shots in `local/gpuopts/`. Other agents' test clients ran during several runs (each run's
`others.json` lists them): the half-size and small-caster numbers are per-frame A/B (both sides share the load); the
margin and narrow runs, compared run to run, had the GPU to themselves.

| option (setting) | GPU a frame | look |
|---|---|---|
| sun-light map at half size (`half_ssao`, "shadow & ssao size") | -1.3 to -1.6 ms (sunlight + its blur 2.2-2.7 -> 0.9-1.15 with the bring-up's 0.25-0.32) | leaf and shadow edges: mean 0.4-0.6/255, 99th percentile 6-14 levels, 1-1.5% of pixels off by more than 8; nothing I could see side by side |
| far cascades 10% larger (`far_shadow_margin` 0.1) | moving: shadows 2.17 -> 1.72 ms (a far cascade kept in all but 16 of 9000 frames, against ~1 in 3 refused while moving before); still views unchanged | far shadows 10% coarser: mean 0.1-0.2/255, 99th percentile 2-3 |
| far cascades 20% larger (0.2) | the same as 10% | slightly more change (close view mean 0.4) |
| small casters left out (`min_caster_texels` 1 or 2) | nothing here (shadows within 0.01-0.03 ms); even at 50 texels only 1-2% of the shadow pass: the forest's shadow cost is its trees | identical at 2 texels (mean 0.02-0.16) |
| narrow normals (`narrow_gbuffer`, on restart) | G-buffer -0.02 to -0.03 ms, the frame -0.02 to -0.05 | mean 0.04-0.05/255, 99th percentile 1 |

- **Half size** (`Frame/Renderer.HalfLight.cs`, `lightup.frag`, `HALF_LIGHT` in sunlight.frag and blurlight.frag): the
  sun shadow, SSAO and projector shadows are lit at the top-left pixel of each 2x2 block, the viewer's two blur passes
  run at that size with their taps spaced in the eye's pixels (the same reach), then a pass brings the map back to the
  eye's size: the four nearest texels by bilinear weight, each weighed down by how far its depth lies from the pixel's
  (4% of the distance counts as the same surface), the nearest in depth when none match. Main eyes only (mirrors and
  probe faces as before). `SOJOURN_HALF_LIGHT=1|0` overrides the setting, `SOJOURN_AB=halflight` compares.
- **Far cascades fitted larger** (`ShadowSettings.FarMargin`, `SunShadowSetup`, `Renderer.Cascades.cs`): cascades 2 and
  3 are fitted to their receivers grown about their middle; the receivers as fitted without the margin travel with the
  view, and a cascade is kept from the frame before when they all lie inside the old map, so it holds while the head
  turns and walks, not only when it's still. Moving casters' far shadows are a frame late. `SOJOURN_FAR_MARGIN=F`.
- **Small casters** (`ViewSetup.TinyCasters`, `RegionScene.DrawObject`): an object's bounding sphere measured across
  the light in each sun cascade's map (its clip-space size at the object); under the setting's texels it's left out
  of that cascade. Objects only (not avatars, trees, grass). Where it might pay: SL's busy places, full of small
  clutter and jewellery; unmeasured. `SOJOURN_MIN_CASTER_TEXELS=F`, `SOJOURN_AB=tinycasters` with
  `SOJOURN_CASTER_TEXELS`. Lower-detail casters weren't built (not in the user's list; a coarser caster shadows its own
  finer surface wrongly, and needs a second detail level of each mesh).
- **Narrow normals** (`RenderTargets.Narrow`, `gbuffer_util.glsl` gbNormOut/gbNormIn): the normal target in
  A2B10G10R10 (10-bit octahedral normal, env intensity in 9 bits with the self mark in b's top bit, the flag in the
  2-bit alpha), read back through gbNormIn wherever the G-buffer is read. Emissive in B10G11R11 was tried with it and
  dropped: the sky, written there, banded in rings. Read once at start (`SOJOURN_NARROW_GBUFFER=1`). On this card the
  G-buffer's bandwidth isn't what costs, so there's little to win.

## Where shadows end (2026-10-29)

Claude Opus 5.5. The user's finding in the headset on SL: sun shadows ended on a sphere around the head, a ring on the
ground, worse under lag. The viewer (`shadowUtil.glsl`) fades its last cascade out over that cascade's far half, from
(split 3 + end) / 2 to the end (here about 0.71 to 1.0 of the draw distance, by distance from the head; the viewer by
depth, so its edge is a line across the view that moves with it), and the end is the draw distance or twice the
farthest receiver. Terrain is drawn past the draw distance, so the edge lands on open ground. The load governor's trim
(performance.md > The load governor) brought the end in to half the draw distance, and with cascade 3's split near
there, its fade shrank to a few metres: a hard circle ~64 m out (at 128) past which trees and buildings stood on fully
lit ground, from a height nothing on the ground shadowed at all.

Now (`far_shadows`, menu > view > graphics > far shadows, on by default at 1024): a fifth sun cascade, coarse, in its own
map (shadow map 6, `SunShadowSetup.Far`, `FrameRenderer.FarShadow.cs`, `shadows.glsl`):
- It's fitted as the others (the viewer's fit, to the shell in view) from where cascade 3 ends (its full reach, or
  the trim's) out to 1.25 times the full reach (`ShadowSettings.FarEnd`), grown 25% about its middle
  (`FarCascadeMargin`). The shader hands cascade 3 over to it across a fifth of cascade 3's end, and fades it out from
  the full reach (the draw distance, where things stop being drawn) to its own end; cascade 3's own fade goes. Up to
  cascade 3's end the picture is as before.
- It's drawn only when it has to be: kept from an earlier frame while every receiver it must cover lies inside the
  old map (the margin holds it through turns and walks), the region, sun and size are the same, and it's under 8
  frames old (`SOJOURN_FAR_REFRESH`; moving casters' far shadows are that late). A still picture draws its own and the
  next frame draws the person's again. In the bench views, moving or not, it was kept 87% of frames (the age limit).
- Casters under a texel across in its map stay out (`FarCasterTexels`, `SOJOURN_FAR_CASTER_TEXELS`), and Linden grass;
  masked faces pick the mip the full-detail cascades would there (`shadow.frag` casterTex), so leaf cards don't thin.
- Under the trim, full-detail shadows end sooner and the coarse ones carry on: the trim moves where shadows turn
  softer, not where they end. With far shadows off, everything is as before (checked against the old build's shots:
  identical but for a mesh still loading).

Measured on the perf grid's forest (`local/shadowedge/`, the bench's five views: forest, ground, overlook, far and a
moving orbit; two Quest eyes of 2448x2672, shadow cap 3072, noon; per-frame A/B, `SOJOURN_AB=farshadows`, cascades
timed alone; another agent's client was on the GPU):

| | far map, a drawing | a frame (kept 7 of 8) | sunlight pass | cascades 0-3 |
|---|---|---|---|---|
| 1024, full reach | 0.11-0.20 ms | ~0.02 ms | +0.01-0.03 ms | unchanged |
| 1024, trim x0.5 | 0.12-0.19 ms | ~0.02 ms | +0.01-0.03 ms | unchanged |
| 2048, trim x0.5 | 0.22-0.42 ms | ~0.04 ms | +0.01-0.02 ms | unchanged |

Pictures (noon and sunset; `local/shadowedge/report-high.png`, `report-sunset.png`): at full reach, trees from ~90 m
to the draw distance keep their shadows instead of fading; at x0.5 the trimmed frame looks like the untrimmed one, a
little softer past the trim (2048 looked the same as 1024 there, so 1024 is the default; `sharper` in the menu is 2048).
`SOJOURN_FAR_SHADOWS=N` sets the map size over the setting (0 off), `SOJOURN_SHADOW_REACH=F` holds the trim at F, and
the console says every 2700 far frames how often the map was kept.

## Measuring on a shared GPU

`SOJOURN_AB=NAME` flips a switch every frame (`Renderer.AbSwitches`: multiview, mvgbuffer, solidshadows, gbclear,
lens, alphapaths, farhalf, barriers, halflight, tinycasters with `SOJOURN_CASTER_TEXELS`, captureshadow, farshadows;
none for an A/A) and tags each
pass in the profiler `|on` or `|off` (the far cascades' turns go with the same parity, so a shadows comparison wants
`SOJOURN_FAR_CASCADES_HALF=0`; `SOJOURN_GPU_LOG`'s `ab` column says each frame's side); `SOJOURN_PROFILE_MEDIAN=1` reports a window's median
frame for each pass instead of the mean. Together they compare two ways under whatever else runs on the GPU (an A/A run
differs by under 0.05 ms a frame). The bench takes views from a file (`--bench-views`, a view a line: name, eye, focus)
and holds the environment's time, so two builds' shots compare pixel for pixel; with `SOJOURN_GPU_PACING=0` too
when the builds' costs differ: the load governor gives probe captures the GPU time a frame leaves, so a cheaper build
captures probes at other moments and its shots differ by a level wherever probes light (seen 2026-10-29). The profiler sums a pass marked more
than once in a frame: every pass reads as ms a frame, both eyes. `SOJOURN_PROFILE_CASCADES=1` times each sun cascade.
Tried and dropped, slower on the RTX 5070: the lighting folded into one pass, local lights and projectors merged, the
light map's blur reading map and depth as one 64-bit texel.

## Name tags and floating text in the eye's depth

Claude Opus 5.5, 2026-10-27, after the user found floating text warping with head movement and painful up close.
They were already world-space quads per eye, but drawn over the finished image with no depth: the compositor's depth
reprojection (late frames, and Meta's positional timewarp) moved their pixels as whatever lay behind them, the sky or a
far wall, and floating text drew over things in front of it, so occlusion and stereo disagreed. Now they're drawn with
the eye's depth buffer (`Overlay.cs` `_worldTested`, `_worldOver`, before the rest of the overlay and before the depth
is handed to the compositor): they write their own depth where they show (`overlay.frag` WORLD_DEPTH discards below
alpha 0.1), floating text is depth-tested (the viewer's LLHUDText is), tags aren't (the viewer shows them through
walls), and both turn to the point between the eyes rather than to each eye, so both eyes see one plane.

Pictures placed at world corners are interpolated in perspective. The overlay's quads go in already divided by w
(normalised device coordinates), which across one big quad interpolates the picture affinely: text bends unevenly
across a sheet seen at an angle, the bend moving with the head ("like water", Luane's World, 2026-10-27). Labels
(`Overlay.Tag`: a quad each, with its corners' depth, `QuadZW`), sheets and the assistant's cards at corners
(`Overlay.Sheet`, `Overlay.Card`: the panel, feed, page and their frost in the eyes, cards in the world or room, upright
cards) carry each corner's clip w (tangent.z), which `overlay.vert` multiplies back, so the GPU interpolates them in
perspective exactly (Claude Opus 5.5, 2026-10-27/28).

Labels were first a grid of ~16-pixel cells, 600-1800 vertices a tag at the headset's size, against a fixed 4096 an eye:
in a crowd the overlay was cut off mid-list, so the last tags drawn lost their bottom rows in steps and the rest (more
tags, cards, the frost behind the feed and panel, beams, dots, wrist tokens) didn't show, changing frame to frame and
eye to eye (the user's "tags flickering on their bottom halves with rectangular cutouts", 2026-10-28). Each eye's vertex
buffer now grows to what the frame needs (`Overlay.VertexBuffer`); nothing is cut off.

## The headset's near plane and detail levels

- Near plane 2 cm in the headset (`ViewSetup.HeadsetNear`; the viewer's 0.1 m cut things off at the face when leaning
  in); the compositor's depth layers say the same (`XrSession` farZ). Reverse-Z in D32 float keeps the same relative
  precision at any distance whatever the near plane (~1e-5 m at 128 m). Shadow cascades still fit from 0.1 m.
- Objects' detail levels in the headset use the viewer's default camera angle (60 degrees), not the lenses' ~100:
  `VolumeLod.CalcLod`'s zoom compensation would otherwise pick coarser levels (rigged meshes a level down from ~19 m
  instead of ~30 at detail 2.5), though a Quest eye has more pixels a degree than a desktop. More triangles in view in
  busy places as a result (`ViewSetup.LodFov`).

## Blended or masked: as the viewer decides

Claude Opus 5.5, 2026-10-27 (a statue at Natoma showing its own far side through its front, alpha trouble at
MagnetiS). Checked against secondlife/viewer develop (`llimagegl.cpp` analyzeAlphaData, `llface.cpp`
canRenderAsMask, `lldrawpoolalpha.cpp`):
- A texture with an alpha channel that's 255 everywhere is a mask to the viewer (analyzeAlphaData's last test), so a
  face using it renders opaque with depth (auto alpha mask). We counted only textures with some alpha below 255, so such
  faces (older uploads kept an opaque alpha channel) were blended: no depth writes, sorted per face, a concave mesh's far
  side drawn over its near side. `TextureStore` IsAlphaMask.
- Rigged faces are never auto-masked (the viewer's rule; we masked them).
- The blended pass disables culling for double-sided GLTF materials (we culled their back faces); `_alpha` no longer
  culls and `alpha.frag` drops the back faces of everything else (avatars' own pipelines cull for themselves).
- The pre-water pool (Claude Opus 5.5, 2026-10-27; LLDrawPoolAlpha::renderAlpha and forwardRender, the pools'
  order in LLPipeline::renderGeomPostDeferred): with water in the frame, blended faces split by the water plane. One
  whose bounds reach the far side of the water from the camera (below it, from above) is drawn after the water haze
  and before the water, clipped to that side (the viewer's waterClip, `alpha.frag`), with depth writes, so the water's
  refraction copy holds it and its fog depth stops at it; one reaching the near side is drawn after the water as
  before, clipped to its side; one across it is drawn in both, a half each. Under water the sides swap. Mirror views
  and probe captures draw them all after the water, unsplit. `SOJOURN_PRE_WATER=0` turns it off for comparing.
  Its cost (Claude Opus 5.5, 2026-10-29; Mirromere's water pass at 3-13 ms a frame was mostly this pool): which side a
  face reaches is judged by its own box (`DrawCmd.HalfHeight`, from `FaceGeometry`'s box through the model, a
  centimetre spare), not its object's bounding sphere, which put every blended face of a big linkset near the water
  (grass, a pond's banks) in both pools to be discarded fragment by fragment on one side; the pool draws through
  alpha.frag per material path as the post-water pool does, lit in full, no layer cut (`_alphaPreWater[path]`); the
  water clip is alpha.frag's first test. Pictures: identical to the old build's above water, within run-to-run noise
  under it; the pool -55-65% on the shore views (performance.md > Arriving at a heavy place). The profiler splits the water pass:
  `water haze` (mask, depth copy, haze), `pre-water alpha`, `water` (copies, surface). Draws with no bounds at all
  (an avatar's loading cloud) go in both pools; before, they fell in the pre-water pool alone and vanished above water.
  Since 2026-10-29 the pool follows `simple_see_through` as every other blended face does (it had been lit in full
  whatever the setting; the user's default is simple): -65% of the pool again on the shore views.
  Before, a blended face's part under the water was drawn after the water and failed the depth test against its
  surface, so from above it wasn't there at all; checked on the local grid with a half-transparent box across the
  surface, which now shows faintly through the water below the line.
- `render_scenes.py statue`: a torus and a box textured with an all-opaque RGBA texture (blended faces in view 39 -> 30).

## Objects kept on screen through updates

A full object update (scripts setting hover text, a colour, a sound: LibreMetaverse makes the texture entry anew)
dropped the object's resolved faces, so with the frame's resolving spent (a busy place loading) an object over 16 m
away counted as new and wasn't drawn until resolved: a blink, on exactly the scripted things you point at. Now the
faces are only marked outdated (`FaceCache.Outdated`) unless the shape changed, and drawn as they were for up to two
frames like any out-of-date cache (Claude Opus 5.5, 2026-10-26; found looking for the pointer flicker). Media coming
or going on an object does the same.

## Mist and other flat blended planes

House of Legends' mist (2026-10-26: "affects the arms but not the hands", "a flat plane cutting through avatars") was
rebuilt on the local grid (`render_scenes.py mist`: three drifting fullbright mist planes at 0.35/0.8/1.25 m, particle
mist, a pane, a post) and looked at from the own body and a third-person camera. Our order and depth match the
viewer's: everything opaque or rigged is in the depth buffer before the planes, which are depth-tested, so a horizontal
plane covers what's below it as seen from above and nothing above it. A hand raised above a plane at elbow height is
clear while the forearm under it is misted; an avatar standing in it is cut by a straight line. The viewer draws the
same; in a headset, seeing your own body close up makes it plain. Not reproduced: any difference from the viewer.
What changed for alpha there is above (sort, discards). Our particles face the head's position with world up (not the
view plane), so they don't swing as the head turns.

2026-10-28, in the headset (Claude Opus 5.5): the mist showed only over stones and pavers as hard white shapes, the
ground hiding it everywhere else, and which mist was hidden changed as the person moved a few metres. The ground there
isn't terrain (the place is a skybox at 2360 m) but "Somber Designs: Dark Vale", a 62 x 63 m mesh whose ground faces'
textures have an alpha channel (219-255, not a mask by analyzeAlphaData), so without a material they're blended; the
mist planes (fullbright, alpha 0.7-0.9, a texture whose alpha is at most 50) are drawn as they should be. Two causes on
our side:
- The sort: by faces alone, a ground face under the person, pulled 15 m nearer by its own size, sorted after the mist
  lying on it, and being blended without depth it was drawn over the mist; the mist survived only over opaque things.
  Prims now sort first as the viewer's groups do (sort order, above). Even so, a blended ground this size sorts at
  about 14 m from the head when standing near its middle, so mist prims farther than that are still covered: the same
  key the viewer uses, so the viewer would draw that too if the ground were really blended there.
- Probably not blended in the viewer: a plain "alpha mode: none" in the build floater makes it opaque, and the mist was
  built to lie on it. Our legacy material fetch took an empty answer as "no material" for good, and an empty answer is
  what LibreMetaverse returns when the grid rate-limits the cap ("cap invocation rate", logged in MIST III minutes
  before) or the request fails; the grid de-duplicates materials, so a common one lost in one region stayed lost
  everywhere for the session. Empty answers are now asked again (2, 4, 8, 16, 30 s; none after six), failed materials
  count as waiting so their faces re-resolve, and the store's version moves when a retry is due (`MaterialStore`).
  Not confirmed: the client was down before the ground's material id could be read (`TesselObjects get full` doesn't
  show material ids).

## Animated objects (animesh)

Claude Opus 5.5, 2026-10-26. `World/Avatars.Animesh.cs`, `World/ObjectAnimations.cs`, the index's `Animesh` field
(`RegionScene.Index.cs`). An object whose root has the Animated Mesh flag (extra param 0x70, LibreMetaverse's
`IsAnimatedObject`) gets a skeleton of its own, as the viewer gives it a control avatar (`llcontrolavatar.cpp`): no
body drawn, the default shape, joint positions from its rigged meshes' overrides (as an avatar's from what it wears),
playing what its prims signal (ObjectAnimation, kept by object id as `LLObjectSignaledAnimationMap` keeps it, since the
message can arrive before the object; the linkset's lists merged, each animation once at its larger sequence).
Placed as `matchVolumeTransform`: in the world, mRoot at the root prim, turned by the prim's rotation and the root
mesh's bind shape rotation (MAINT-8671), kept within 3 m of the prim by last frame's extents (`getNewConstraintFixups`);
worn, at its attachment point on the wearer as posed that frame, the root's offset and rotation on it, and its meshes
no longer move the wearer's joints. Posed after the avatars, keyed by the root's local id, so its rigged faces skin
through the worn-mesh path (`AddRigged`) and are culled by its extents rather than the prim's sphere. A rig created
before anything is signalled takes what comes later as starting then (an avatar's first list counts as long playing).
Over 64 m across it's scaled down to 64 m about its pelvis (`getNewConstraintFixups`' scale, `ScaleFixup`, since
2026-10-29: by last frame's joint box padded a quarter metre a side, as the viewer's extents, or the rigged meshes'
largest span in bind space where that's larger, the viewer's box stretching by each joint's rigged vertices; every
joint's scale times the fixup, as `recursiveScaleJoint`; worn ones too). Not done: a worn one following its wearer's
impostor or jelly state beyond what attachments already do, name tags. `tools/animesh.py`: a figure of rigged mesh (crowd.py's body, head and dress) with
its own looping sway, in Atelier and worn at the pelvis: it moves in the world and worn, holds still when stopped,
and `TesselBuild animated: false` makes it a still mesh again (checked 2026-10-26, desktop). OpenSim's built-in dances
don't loop (their `.anim` says so), so they play once on an animated object there.

## Avatars' faces against the viewer

Claude Opus 5.5, 2026-10-27, after "everyone's face melts" at a club on the main grid (session-2026-10-27.md).
- **Checked equal to the viewer's source** (secondlife/viewer develop, read 2026-10-27): the appearance message's
  params (group 0 and 3 in id order, U8_to_F32), drivers (`LLDriverParamInfo` defaults, `getDrivenWeight`), skeletal
  distortion (`setInfo`/`apply`: scale deltas added, collision volumes inheriting, offsets), joint transforms
  (`LLXformMatrix::update`: a parent's scale moves only its children's offsets), attachment joint overrides
  (`addAttachmentOverridesForObject`, 0.1 mm threshold, the largest mesh id wins), the skin palette
  (`initSkinningMatrixPalette`), skin weights (`unpackVolumeFaces`: no terminator after a fourth influence; MeshFoundry
  3.2.0's decode compared on real heads, every vertex the same), animation decode and blending (`LLJointStateBlender`).
  SL's 253 params against OpenSim's 218 are only the physics drivers, the appearance version and hover; the face
  sliders are among the 218. An offline harness skinned real SL heads on our `BodyShape` (scratch, not kept): sliders
  one at a time do what they're named for; all of them random tear a head as the same sums would in the viewer.
- **Bakes on mesh in PBR materials**: a worn face whose GLTF material or override names a bake's magic id shows the
  wearer's bake (`getBakedTextureForMagicId` from `updateTEMaterialTextures`), as the legacy texture already did
  (`RegionScene.BakeOnMesh`). Before, the id was fetched as a texture: LL's red "BAKED_HEAD" placeholder.
- **Loading as the viewer**: an avatar stays a cloud until the attachments its appearance lists have arrived, for up
  to a minute after the last one came (`updateIsFullyLoaded`, `MAX_ATTACHMENT_WAIT_TIME_SEC`); before, its system
  body showed meanwhile, the head shaped by sliders meant for a mesh head (bakes on mesh hide it once the head's prim is
  in). Counted as the viewer counts. OpenSim doesn't send the list, so locally nothing waits.
- **The melting was altitude** (Claude Opus 5.5, 2026-10-28, Club PlanetAry at 2075 m): the skinning pre-pass blends
  region-space matrices with weights packed as unorm16, rounded one by one, so a vertex's weights summed to 1 ± ~3e-5
  and it was scaled toward the region's origin by that much: up to 3 cm at 2 km, nearly straight down, different for
  each blended vertex (faces and hems in vertical streaks, silhouettes intact; ~1 mm at ground level, so never seen on
  the local grid or offline). `SkinWeight.From` now makes the packed weights sum to exactly 65535 (the largest takes
  the rounding): her 358 worn meshes skinned on the CPU at 2074 m went from 141k vertices over 1 cm off to none, worst
  0.5 mm. Her textures, decoded as the client does at every level, were clean.
- **`SojournAvatars inspect`** (`World/Avatars.Inspect.cs`): one avatar as the renderer has it (params as sent, bakes
  and their state, hidden system meshes, attachments listed and here, each worn prim's mesh, skin, detail levels drawn,
  faces' textures and materials with bakes on mesh marked, joint overrides, animations), saved to `avatar-reports/` in
  the state folder: enough to rebuild that avatar locally.

## Flexible prims

Claude Opus 5.5, 2026-10-27, after the user's cape (34 old-style prims on the spine) stood still in the headset.
`World/FlexiSim.cs` (llflexibleobject.cpp's simulation), `World/RegionScene.Flexi.cs` (when, where, drawing),
`Geometry/VolumeBuilder.cs` BuildFlexi / BuildFlexiRest, `DrawList.AddFlexi`, `Renderer.UploadFlexi`. Setting `flexi`
(on), menu > view "flexible prims"; off draws them straight, as before.
- **As the viewer**: a prim whose path is flexible (0x80) and has flexible data is a chain of 2^softness sections from
  its anchor (the middle of its -Z face) along its Z, pulled by gravity, the region's wind and its force, held toward
  its parent's direction by tension, slowed by drag, each section's bend limited; the volume is swept along the chain's
  points in the prim's frame, in metres, and drawn at the prim's place and turn without its scale. Render sections
  (2^0-2^3 + 1 points) follow its length on screen (`updateRenderRes`), simulated ones no more than rendered. A step
  is due every 1-32 sixtieths of a second by how much of the screen it covers, only while it's in view and over 256
  pixels, a linkset's prims in the same sixtieth (`doIdleUpdate`). Worn ones follow the wearer: the chain stays where
  it was in the region while the anchor moves (a cape streams behind a run, up in a fall).
- **Where it differs**: steps run on the thread pool, one at a time per prim, and the prim is drawn with the latest
  finished one (a frame later at most); the viewer steps on its main thread. The chain starts straight again after a
  jump of 8 m (a teleport, a crossing). Only the main view simulates them: probe captures, HUDs and (as everything) the
  pick ray see the straight shape. The pixel area that sets the step rate is the bounding sphere's, a little larger
  than the viewer's per-face area, so steps come a little more often.
- **Drawing**: a step's faces line up vertex for vertex with the prim's rest shape at the same detail and point
  count (`ShapeKind.Flexi`, uploaded once, checked once per result), whose indices it's drawn with. The vertices are
  copied each frame into the frame arena and through the skinning pre-pass (one identity joint) into the frame's
  skinned vertex buffer: no allocation in the geometry pool per step, nothing written that a frame in flight reads.
  A prim's steps reuse their buffers (profile, mesh, faces, three sets of vertices in turn): ~0.1 KB of garbage a step.
- **Cost** (`render_scenes.py flexi`: a flag, 8 strands, a 48-strip curtain and a swinging rail with 3 strips, 60
  flexies, desktop 1280x720, the bench at 10-13 m): render thread 0.0-0.2 ms a frame more (their instances and draws
  each frame, the copy), GPU unchanged (skinning 0.02 ms), 1300-3100 steps a second of 5-25 us each on the pool (8-52
  ms of worker time a second). A step at the best detail with 9 points (277 vertices) takes ~70 us. The console says
  every 10 s what they cost (`flexible prims:`); `SOJOURN_FLEXI_DEBUG=1` logs each one's state.

## Avatar physics

Claude Opus 5.5, 2026-10-27. `World/Avatars.Physics.cs` (llphysicsmotion.cpp). Setting `avatar_physics` (on, the
viewer's AvatarPhysics), menu > view "avatar physics".
- Six motions (breast in-out, up-down, left-right on mChest; butt up-down, left-right and belly up-down on mPelvis),
  each a driven param in [0, 1]: a spring toward the shape's own, pushed by its joint's acceleration along its direction,
  gravity, damping and drag, with the physics wearable's mass, gain, spring, damping, drag and maximum effect, in steps
  of at most 0.05 s. Their driven params (1200-1207) move collision volumes (LEFT_PEC, RIGHT_PEC, BELLY, BUTT: what
  fitted mesh bodies are weighted to) and morph the system body (the viewer's clones of Breast_Gravity, the cleavage,
  belly and butt morphs).
- The volumes are moved every frame after the pose (they're leaves: only their own matrices change), so worn fitted
  meshes follow at no cost. The system body is morphed and uploaded again only while a section it shows moves, at most
  30 times a second an avatar and two avatars a frame (a mesh body hides it, so usually never).
- No physics wearable (OpenSim sends no physics params) leaves the maximum effects at 0: nothing moves, as in the
  viewer. `SOJOURN_TEST_AVATAR_PHYSICS=1` gives every avatar a lively wearable and logs the driven params' ranges.
- Differs: the viewer starts each motion from the region origin (a jolt as an avatar appears); here from where the
  joint is. The own body's whole copy in a mirror doesn't move with it.
- Checked on the local grid with the test wearable (the params swing through their range when the avatar runs and
  jumps, and settle); how it looks on SL's fitted mesh bodies is for the next main-grid session.

## Avatars: lip sync, others' effects, standing feet, fixes (batch B3b)

Claude Opus 5.5, 2026-10-29. `World/Avatars.LipSync.cs`, `Avatars.Effects.cs`, `Avatars.Feet.cs`, `Avatars.Reset.cs`,
`Bodies/Animator.Ik.cs`, `Bodies/JointSolverRP3.cs`; the client half `Control/AvatarsWiring.cs`, `AvatarsApi.Fix.cs`.
Read from the viewer's develop branch and Firestorm master (2026-10-29; copies in `local/ref/viewer`, `local/ref/firestorm`).
- **Lip sync** (`lip_sync`, on, the viewer's LipSyncEnabled): a speaker's `Lipsync_Ooh`/`Lipsync_Aah` morphs from voice's
  power as `LLVoiceVisualizer::lipSyncOohAah` (the LipSync* defaults: power transfer curves, babble loops at 24 a
  second); others' power is the server's (`VoicePeer.Power`, p/128, as getCurrentPower), your own from the mic while
  it goes to residents (the mic token's scale, speaking over 0.3; the viewer's updateOwnVolume), nobody blocked. Only
  the head is made again each frame: the shape's morphed head plus the two lip morphs (everything in a shape is linear
  in its weights, so it's the shape built with them), its vertices through the frame arena into the skinning pre-pass
  with the head's own weights (`DrawList.AddSkinnedCopy`, beside the flexible prims' copies; three buffers in turn).
  The body shape's own rebuilds (at most ten a second an avatar) aren't involved, so the mouth moves every frame for
  ~1100 vertices copied. Departures for smoothness: the curves and loops read between their entries and the power
  eased over 60 ms (the viewer steps 24 times a second and at each report: up to 0.99 of the morph in a frame), easing
  closed when speaking stops (the viewer snaps). The system head only, as the viewer: a mesh head hides it, and its
  mouth is its own scripts' and animations'. `SOJOURN_TEST_LIPSYNC=1` makes everyone else speak.
- **Beams** (`selection_beams`, on, the viewer's ShowSelectionBeam): the spiral family of viewer effects (beam 7, point
  9, sphere 11, spiral 12, edit 13; read from the packet, since LibreMetaverse drops their colour) drawn as their
  particle sources: with a target object or a global position, `LLViewerPartSourceBeam` (a particle every 25 ms living
  half a second, 10 cm, along the beam from the sender's left wrist to the target, the effect's colour fading to 40%
  alpha, turned along the beam; all gone when the effect ends); with neither, `LLViewerPartSourceSpiral` (a second each,
  25 cm, circling a metre round the sender and rising, fading out). Worked out from when each particle would have been
  emitted rather than simulated, and added to the frame's particles (`ParticleBuilder.Extra`), so they sort and blend
  with the rest and the particle switch hides them. Effects live for the duration they're sent with, renewed when sent
  again.
- **PointAt** (received only; Sojourn sends none and no beams, the user's call on the board): it starts the viewer's
  editing motion on the sender, `LLEditingMotion` with `LLJointSolverRP3` ported: the left arm from the collar reaching
  20 cm past the wrist toward the target (an avatar's head or an object plus the offset, or a global position), what's
  behind the arm folded by the edit plane and raised, high priority, the hand relaxed (priority 3), started at once and
  eased out over half a second at a None or Clear; not under 500 pixels of the avatar. The solver's base pose is the
  arm's when the motion first runs on that avatar, as onInitialize's; its elbow axis suits an arm bent as the stands
  hold it, so a near-straight one reaches only roughly, as in the viewer (the solver's cross-product fallback).
- **Standing feet**: the default stands (stand, stand 1-4, crouch) are `LLKeyframeStandMotion`: from its third update,
  each leg is solved from its key pose two updates in so the ankle reaches the ground under it (`LLVOAvatar::getGround`:
  the land a metre above to a metre below, or the avatar's foot plane from the simulator, 5 cm lower, whichever is met
  first; OpenSim's zero-normal "plane" is ignored), and the ankle laid on its slope. The viewer's lock once settled is
  never set, so it tracks every frame. Not for the own body in the headset (the rig puts its feet down) nor animated
  objects. AOs' stands aren't these, so most mesh avatars are untouched, as in the viewer.
- **Fixes** (`SojournAvatars resetSkeleton`, `refreshAttachments`, `resyncAnimations`): Reset Skeleton builds the shape,
  the joints worn meshes move and physics again (and with animations, everything listed starts again from now:
  `resetAnimations`); for your own it's also sent as ViewerEffect 19 (17 bytes: the target, the reset-animations bit),
  which every viewer obeys from the avatar's owner (and so do we), and with animations it's the viewer's Stop animating
  me. Refresh Attachments: yours each attached again where it is (Firestorm's `refreshAttachments`, the "phantom
  attach" fix); anyone's asked of the region again; their faces resolved afresh here. Resync Animations: every avatar's
  and animated object's animations restarted together, in their order (Firestorm's).
- **Jelly dolls' particles**: particle sources worn by an avatar drawn as a jelly doll (or one the person chose never to
  render) aren't drawn, as its attachments aren't. The viewer stops them only when the avatar is also over its render
  time; here the doll is the guard against a crasher, so its particles go too.
- Checked (2026-10-29): `tools/avatarfx check` (no grid: the solver, the editing motion, the stands' feet on raised and
  sloping ground, lip sync's amplitudes and smoothness, the 64 m fixup), and `tools/avatars.py` on the local grid (desktop,
  Sojourn Friend sending by LibreMetaverse): her left arm out toward a PointAt and back when cleared, a magenta edit beam
  from her left wrist to the observer and gone with `selection_beams` off, a spiral, her mouth opening and closing with
  `SOJOURN_TEST_LIPSYNC` (the head through the arena path, drawn whole), her ResetSkeleton obeyed, ours relayed to her by
  the region, the three ops answering, no exceptions. Not seen here: real voice levels driving it (SL's power range
  decides how far mouths open, as in the viewer), the editing arm against the official viewer's, foot IK on real slopes.

## Water: exclusion surfaces and under water

Claude Opus 5.5, 2026-10-28 (batch B3a), from secondlife/viewer develop (`lldrawpoolwaterexclusion.cpp`,
`pipeline.cpp` doWaterExclusionMask and renderGeomPostDeferred, `llvovolume.cpp` genDrawInfo, `lldrawpoolwater.cpp`,
`class3/environment/waterF.glsl` and `underWaterF.glsl`, `class3/deferred/waterHazeF.glsl`). `Frame/Renderer.Water.cs`
now holds the water pass.
- **Exclusion surfaces** (the viewer's since 7.1.12; the build floater's "Hide water"): a face whose texture is one of
  the two invisiprim alpha gradients, with its colour's alpha at least 0.999, no legacy or glTF material, and not on an
  attachment, HUD or rigged mesh, hides the water where it's in front of the water's surface (boat hulls, basements and
  pools below sea level). Such faces go in their own list (`DrawPass.WaterExclusion`, `World/RegionScene.WaterExclusion.cs`;
  other invisiprim faces are drawn nowhere, as before; never in shadows or impostors). At the start of the water pass, if
  any are in view, they're drawn into an R8 mask with a depth of its own (`RenderTargets.EnsureWaterMask`, shared by a
  stereo set's eyes): the water planes first, double-sided, for their depth, then the faces, depth-tested against them.
  Stored inverted (cleared to 0, faces write 1; `water_mask.glsl` turns it back). `waterhaze.frag` skips the fog
  where it's 0, `water.frag` shows the scene unrippled there (its fade), `water_under.frag` stops the ripples' distortion
  there, each as the viewer's shader does. Nothing in view excludes water: no mask and no pass (slot 0xFFFF reads as 1).
- **Under water** (the viewer's sUnderWaterRender, not ported before): the water's fog now covers the whole screen
  (doWaterHaze's full-screen blit, `waterhaze.frag` UNDER_WATER), the atmospherics' haze runs before the water rather than
  after (atmospherics_pass = POOL_WATER), and the surface seen from below is `water_under.frag` (underWaterF: the scene
  above, rippled and fogged by the water between), not the above-water shader. Before, nothing under water was fogged
  but blended faces, and the surface from below reflected the sky.
- **Sun shadows on the water** (HAS_SUN_SHADOW, which the viewer defines whenever sun shadows are on): `water.frag`
  samples the sun's shadow for its glints; before, water in the shade of a pier, boat or tree still glittered.
- Checked on the local grid (desktop, mono and stereo): the boat with the exclusion box is dry inside, the other holds
  water; under water the floor and far water fog to the water's colour and the surface shows the sky and the boats
  above, rippled.
- `python tools/render_scenes.py water`: two open boats in the sea off Atelier's west shore (64,128), one holding an
  exclusion box.
- Still differing from the viewer (Multitude `docs/render.md` > Still differs): finer ripple detail in pond reflections,
  shallow water ~15 levels brighter and with its ripple streaks washed out. The shaders and their uniforms read as
  faithful ports (checked again 2026-10-28: waterV, waterF, the water pool's uniforms, the normal maps' filtering), so
  the cause is in their inputs (the probes' reflections, the scene copy, the fog); finding it wants side-by-side shots
  against the reference viewer (`projects/multitude/local/refviewer`, on Multitude's phase 2 grid).

## The environment over time: the person's own, cross-fades, clouds

Claude Opus 5.5, 2026-10-28 (batch B3a), from `llenvironment.cpp` (setEnvironment, updateEnvironment, DayTransition,
switchTrack, updateCloudScroll) and `llviewermenu.cpp`.
- **The person's own environment** (`Environment/PersonalEnvironment.cs`; FrameRenderer.Personal; the viewer's ENV_LOCAL):
  over every region's and parcel's, seen only by them. A fixed sky or water replaces that part and keeps the other, or
  freezes it as it's shown at that moment when nothing local was there (the viewer copies the current environment's; the
  built-in default isn't frozen, so the place's keeps showing until it loads); a day cycle replaces both, at the default
  length and offset. Changes are instant (the viewer's TRANSITION_INSTANT for the menu and My Environments). The `sky`
  setting's presets come through the request's LocalSky and are applied when it changes; SojournEnvironment `use`
  applies presets (legacy noon too), items from the inventory or Library (`Agents/EnvironmentOps.cs` finds them and
  fetches the settings asset), or an asset by id; `shared` goes back. Not kept between sessions but for the `sky`
  preset (as before). Reflection probes are keyed by the personal environment's version while one is shown.
- **The time of day** (2026-10-29; `PersonalEnvironment.SetTime`, `Environment/TimeOfDay.cs`; SojournEnvironment
  `time`, the `time_of_day` setting, menu > view > time of day in advanced, ±30 min a step): over whatever sky is in
  use, held still. *By sun* is World > Environment > Personal Lighting: the sky as shown, frozen (a day's too, as
  LLFloaterEnvironmentAdjust captures it), only the sun and moon moved, colours kept; the default over a fixed sky.
  *By day* is the day cycle editor's scrubber: the day shown (the person's own, else the parcel's or region's; under a
  fixed sky, the place's) evaluated where its sun stands at that time, nearest where it stands now, sky and water frozen
  there (or `run`: the day goes on from there, by a new offset); the default while a day shows. Hours read the sun as
  WindLight's time of day and SL's default day have it: a great circle through the zenith, rising at 6 in the east,
  highest at 12, setting at 18, the moon opposite; a sky's path keeps its own east angle. Azimuth and elevation are
  Personal Lighting's fields (0 east, 90 south). One personal change a frame, so a time asked right after `shared`
  reads the place's day rather than the sky just dropped. Pressing the `sky` row's current choice again puts that sky
  back as it was. Checked on the local grid: presets nudged and set, the region's day scrubbed, run, back.
- **Cross-fades** (`Environment/EnvironmentFader.cs`): when what's shown changes to another layer (a parcel's own sky,
  the region's changing, the region's environment arriving) the sky and water blend from what was last shown to the new
  one's live values over 5 s (TRANSITION_DEFAULT), 1 s within 10 s of a teleport (TRANSITION_FAST), and over 5 s when an
  altitude band switches the day's sky track (TRANSITION_ALTITUDE). A refetch that answers the same keeps its layer
  (`EnvironmentState` compares the answer's LLSD), so the 10 s refreshes don't read as changes. A region whose
  environment hasn't arrived shows the last one meanwhile (the viewer keeps its region environment until the new one is
  recorded), so a teleport doesn't flash the built-in default.
- **Before any place's sky** (2026-10-30, the user's calls; Claude Opus 5.5): frames with no region at all (the login,
  the seconds before arrival: `FrameRenderer.Void.cs`) are a flat neutral grey, sRGB #949494. Between a region
  appearing and its environment loading, the viewer's own built-in default sky, as in SL (dim, a little teal), and the
  region's own fades in from it over 5 s. Tried and dropped the same day: a slate void (read teal), our own dawn and
  late-morning skies, and SL's Midday as the stand-in (its bright fade into the region's sky stood out).
- **Clouds**: ours scroll by the time of day (exact across sessions; the viewer's accumulate while it runs). Kept
  continuous across changes of what's shown, held while paused (World > Environment > Pause Clouds; the `clouds`
  setting, not kept, as the viewer's), carrying on from there, zero while the rate is exactly zero.
- Checked on the local grid: the presets, the OpenSim Library's day cycles, skies and water (by name and by asset id),
  a sky then a water over a day, back to shared, clouds paused, UI.call World.EnvSettings. Settings assets come from
  the ViewerAsset capability by `settings_id` (LibreMetaverse 3.2.0 asks for them as "invalid_id" and never finishes).

## Property lines and ban lines

Claude Opus 5.5, 2026-10-28 (batch B3a), from `llviewerparceloverlay.cpp`, `llviewerparcelmgr.cpp`,
`llglsandbox.cpp` (renderOneSegment, renderCollisionSegments), `llviewermessage.cpp` and colors.xml.
`World/ParcelLines.cs` builds them on the render thread for the main view; `Frame/Renderer.ParcelLines.cs` draws them
over each eye's finished image, as the viewer's render_hud_elements does after renderFinalize: colours as given,
alpha-blended, depth-tested against the eye's scene, from the frame's present step, so after pictures and videos take
the image (the viewer's snapshots leave the interface out by default) and under HUDs and menus.
- **Property lines** (`property_lines`, off by default as ShowPropertyLines): from each region's ParcelOverlay (4 m cells,
  owner type and west/south line bits), a 6.25 cm strip along each edge of land that isn't public, following the ground
  in 1 m steps and split at the water's height, in the owner's colour (yours green, your group's teal, others' red, for
  sale orange, auction purple, 40% opaque); kept above water while you are; within 256 m; lifted toward the head by
  1 cm a metre of its height over the ground, and 1 cm up. They write depth. Rebuilt when the overlay, the terrain or the
  water height changes. Departure: the viewer culls them behind a plane 8 m behind the camera along its axis and lifts
  them along the camera's axis; here distances are from the head and frustum culling does the rest.
- **Ban lines** (`ban_lines`: collision, the default, proximity or hide, as ShowBanLines): a region's ParcelProperties
  with a collision sequence id (banned, not on the access list, not in the group) carries a bitmap of the parcels you'd
  collide with; its outline becomes walls of the viewer's tape (NoEntryLines.png, yellow; NoEntryPassLines.png, green
  with L$, for a pass-list parcel you're not on; shipped as `Textures/*.j2c`, lossless), 50 m high, 5000 m where you're
  banned or the region blocks flying over, half a texture a metre, more opaque the nearer the head (the viewer's falloff
  from the avatar, its 5 m offset to the south and west kept). Shown for 10 s after the region refuses you (an alert
  with "Cannot enter parcel", or RegionEntry/LandClaim/LandBuyAccessBlocked), or in proximity mode for 1 s after each
  report. No depth writes (the viewer's). `SOJOURN_BAN_TEST=x0,y0,x1,y1` (region metres) reports a ban around that
  rectangle of the agent's region every half second, for tests (the local grid has no ban lines).
- Checked on the local grid: the region's edge as another's land (red) on the water; test ban walls' tape bands every
  2 m, hidden below the water. Real ban lines and the refusals that show them need Second Life.

## Beacons and highlights

Claude Opus 5.5, 2026-10-29 (batch B8), from pipeline.cpp (render*Beacons, postSort, renderHighlights),
llglsandbox.cpp (renderObjectBeacons), lldrawpoolalpha.cpp (renderDebugAlpha), llvovolume.cpp, lltracker.cpp,
llfloaterbeacons.cpp and Firestorm's changes. `World/Beacons.cs` gathers them on the render thread after the main
view's draws are built (`RegionScene.Beacons.cs` walks each region's objects); `Frame/Renderer.Beacons.cs` draws them
over each eye's finished image from the present step, right after the parcel lines: so in both eyes, in user
screenshots (F12), not in the assistant's pictures (as the viewer's snapshots leave the UI out), under HUDs and menus.
The client half is `Sojourn.Client/Beacons/` (settings in `beacons-settings.json`, `SojournBeacons`, the tracker).
- **Beacons** (`beacons` the floater open, off by default; `beacon_lines` off, `beacon_highlights` on; kinds
  `beacons_touch` and `beacons_physical` on, `_scripted`, `_sounds`, `_particles`, `_media` off: the viewer's
  defaults; Firestorm's are touch and physical off). Scripted and touch only at a linkset's root that isn't worn
  (exclusive, as the floater keeps them), physical, particle (a source with parts to emit) and media (a face's media
  flag) at any prim, worn ones too; sound beacons at world audio's sources, heard or not (`WorldSounds.Places`, the
  viewer's walk of its audio engine), sound highlights on prims with an attached sound. Only what's in view and within
  draw distance. Each beacon: a cross 2 m across and 50 m up and down over everything at a quarter of its alpha, and a
  0.5 m cross and a 10 cm wire cube tested against the scene; colours linearised as the viewer's (red scripts, green
  physical, white media, light blue particles, yellow sounds, alpha 0.5). Lines are strips facing the head,
  `beacon_width` pixels of the view wide (at least 1.5, below which a thin quad leaves gaps), split where they pass
  nearest the head and wherever their distance changes by a quarter, so the width holds along a 100 m line.
- **Highlights**: those objects' faces red at half, with the shape the frame drew, a fresh instance each, back faces
  culled, tested with no depth writes. Rigged, flexible and animated meshes aren't highlighted (the viewer skips rigged
  faces too).
- **Sun and moon** (`sun_beacon`, `moon_beacon`): 50 m from the avatar's middle toward each, crosses at both ends.
- **Highlight Transparent** (`highlight_transparent`, Ctrl+Alt+T on the desktop, for the session as the viewer's):
  every blended face the frame draws (`dl.Alpha`, `dl.RiggedAlpha`) red, masked ones among the opaque and fullbright
  lists blue (by their material's alpha mode; not foliage, avatars' own bodies, terrain, water or sky, which aren't the
  viewer's alpha pool), and, from the scan, faces the frame never draws: fully transparent ones red, invisiprims green
  (not on what's worn). Each times the viewer's smoke image (IMG_SMOKE, from the grid; the tint at 0.4 alpha until it
  arrives) in the face's own texture coordinates. The re-drawn faces use scene.vert, so they meet the G-buffer's depth
  exactly (GreaterOrEqual). Not done: the viewer's picking through transparent faces while it's on.
- **The tracking beacon** (`Beacons/Tracker.cs`, LLTracker): a column 2 m wide across the head's left axis, clear at its
  edges, red above the target up to 5020 m, blue below it to 0, alpha 0.2-0.5 fading in 3-6 m away, mixed toward a
  fixed haze grey with distance (the viewer mixes its sky's fog colour); tested, no depth writes. Its label ("name / 123
  m", white, over everything) is a floating text (`SocialView.Marks`, `FloatingText.Want.Over`), placed no further
  than 5 m along the line of sight, so it stays the size of a near tag at any distance as LLHUDText keeps its size on
  screen (in the headset it floats 5 m out toward the target); the old picture stays until the new one is in the
  labels' atlas (`NameTags.Holds`), so the distance doesn't blink. Distance from the avatar (Firestorm's). Ends within
  3 m of a place (height at half, the place raised to 1.5 m over the ground once its terrain is known) or of a person
  across the ground; a person is found in the regions in view (avatar or coarse position), a friend with map rights by
  FindAgent every 5 s; not found for 15 s, or a friend offline, ends it. `tracking_beacon` (on), `tracking_beep`
  (Firestorm's UISndTrackerBeacon, every distance/30 s; off). Driven by the map board (`Tracker.Shared`) and
  `SojournBeacons track`/`untrack`/`state`. Not done: the viewer's 2D arrow at the screen's edge toward it, and the
  "cheesy beacon" pulse and shockwave (an Advanced menu toy).
- Not done: the viewer window's legend ("Viewing particle beacons (blue)"), Firestorm's region-corner beacons and chat
  range spheres, beacon text labels.
- `tools/beacons.py scene` puts a test platform 60 m over Atelier 96,200; `run` shoots it with everything off, every
  kind's beacons, the default highlights, Highlight Transparent and a tracked place, checking each by its colours.

## The interest list's 360 mode

`interest_360` (off by default, the user's call after trying it at Firestorm Social Island, 2026-10-30: things behind arrive sooner, but off unless chosen; `Control/EnvironmentWiring.cs`): LibreMetaverse's InterestListManager posts mode
"360" to each region's InterestList capability (and to regions connected later), as the viewer does for its 360
snapshots: the region sends everything around the agent rather than what's in front first. Whether that helps in the
headset (things behind arriving sooner after turning) is for a main-grid session to measure; OpenSim has no such
capability. Claude Opus 5.5, 2026-10-28.

## Test scenes

`python tools/render_scenes.py mirror|mist|flowers|lights|particles|club|statue|flexi|water|remove` (local grid; `SOJOURN_GRID=perf-grid` for the other;
`club`: a room at 192,64 with 100 coloured point lights and 48 projectors; `statue` at 224,64; `flexi` 150 m up at 176,224):
platforms 60 m over Atelier at 64,64 (mirrors: static, dynamic, one facing west for the headset, which logs in facing
east; the plywood cube; things to reflect), 96,64 (mist) and 128,64 (a flower field of 800 blended cards, a table of
glass cases and domes, 12 flowered panes in a row; `lights` adds 48 point lights over it), 160,64 (`particles`: three
fountains of bouncing particles and one of homing ones, 20 s lives, near the viewer's 4096; their CPU cost). The mirror
platform has particle fountains in front of both wall mirrors and one behind where you stand. `SOJOURN_SELF_TEST=front` (new) puts the desktop camera at the own
body's eyes looking ahead, for mirrors without a headset.
