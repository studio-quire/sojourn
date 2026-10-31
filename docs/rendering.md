# Rendering: Sojourn's own

Written by Claude Opus 5.5, 2026-10-26 to 10-31. Agent-facing. What Sojourn's renderer adds to or changes from the
pipeline it ports (Multitude's `docs/render.md` describes the port itself); frame time and the load governor are in
`docs/performance.md`. Features are checked on the local grid (desktop, often the XR Simulator) unless a section says
otherwise; what only Second Life can show is said where it's open. Code paths are under `src/Sojourn.Render/` unless
named otherwise.

## Distances from the head

The user's rule (2026-10-28, in the headset at Mirromere, where loading and the ocean's edge followed a flat plane in
front of them): wherever a distance decides whether or how something is drawn, loaded or sorted, it's the distance from
the head's centre (the point between the eyes, the centre view's `Position`), so turning the head never changes what's
drawn or at what detail. A general VR principle here, desktop included. The viewer measures several of these along the
camera's view axis or by its far clip plane; these are deliberate departures:
- **Draw distance**: a sphere about the head (`ViewSetup.Beyond`; the projection is infinite, so no far plane cuts
  anything), with a 2 m band at its edge (`ViewSetup.EdgeBand`, `SceneObject.InReach`): something in reach stays until
  it's 2 m past it, so leaning doesn't blink what sits on the edge. In view or not, as is the load governor's cap
  (`NewDistance`).
- **Water's far edge**: a sphere of 1024 m about the eye (`water.frag`, `waterhaze.frag`), where the viewer's far plane
  cut the water planes along a plane.
- **Blended faces and particles sort by distance**, not depth along the view axis (Blended faces: sort order).
- **Sun shadows**: the cascade splits are radii, the shader picks a cascade and fades by distance from the eye
  (`shadows.glsl`), and the splits come from the nearest and farthest receivers within the draw distance in every
  direction rather than the visible ones' depths (`AabbSceneExtents.TryGetRadialRange`). Each cascade is fitted to a
  depth slice holding its spherical shell (`SunShadowSetup`, `minDp`). The governor's shadow trim is a radius too, and
  projector shadows' fade follows the same radii. Its cost, measured on the CPU's shadow fit (five head yaws over a flat
  world): the splits no longer move with yaw; 2-13% fewer texels a metre on the ground; casters inside the cascades'
  cull volumes up 3-21%, so the shadow pass draws ~10-15% more in its far cascades. Where they end: Where shadows end.
- **Linden trees' detail** doesn't fall off the view's axis (`TreeBuilder.AppAngle`: the viewer's
  LLFace::calcImportanceToCamera, every tree taken as straight ahead).
- **Avatars**: which are whole and which impostors, and the avatar detail factor (the viewer's sNumVisibleAvatars), rank
  every avatar within the draw distance, in view or not, so turning can't swap them.
- Already radial: objects' detail levels (`VolumeLod.CalcLod`), the scene budget's on-screen size, texture and mesh
  priorities by size on screen, lights, particles' reach, media, impostor ranking.
- Left view-dependent on purpose, being about what's in sight rather than distance (the user, 2026-10-28: things out of
  view not loading until faced is fine): frustum culling; what's fetched for things out of view (nothing for an object
  neither seen nor casting into a shadow; textures at a tenth of their size for one only casting); which mirror is rendered, which two projectors cast shadows, which reflection probes fill the
  view's slots, flexible prims stepping only in view. The sun cascades are still fitted to what's in view (only their
  splits and reach are radial).

## Mirrors

A prim set as a **box** reflection probe with the **mirror** flag (`PRIM_REFLECTION_PROBE, TRUE, ambiance, clip,
PRIM_REFLECTION_PROBE_BOX | PRIM_REFLECTION_PROBE_MIRROR [| PRIM_REFLECTION_PROBE_DYNAMIC]`, or the build floater's
"Mirror" choices) is the viewer's hero probe (`llheroprobemanager.cpp`, `reflectionProbeF.glsl` `tapHeroProbe`). Code:
`Realtime/FrameRenderer.Mirror.cs` (choice, views), `Frame/Renderer.Mirror.cs` (the pass), `World/Avatars.Mirror.cs`
(avatars in it), `Shaders/probes.glsl` `tapHeroProbe`. Settings `mirrors` (on) and `mirror_resolution` (0.5), menu > view.

As the viewer:
- One mirror at a time (the "hero"), within the draw distance and in view. Its plane is the probe prim's +Z face through
  its centre; the reflection shows what's in front of that plane and nothing behind it.
- The reflection shows only on glossy surfaces inside the probe's box, in front of its plane or up to ~4 cm behind it,
  faded over the box's outer tenth: legacy shiny faces by their environment intensity, PBR ones by glossiness (from
  roughness 0.25 down to 0 it blends in fully). Exactly the viewer's weights.
- **So a mirror is two prims**: a glossy surface (blank texture; legacy glossiness and environment 255, or PBR roughness
  0, metallic 1) and a separate probe prim around it, its +Z pointing out of the surface, its centre plane ~1 cm in front
  (so the glass isn't in its own reflection), phantom and fully transparent. A single prim flagged as a mirror stays
  plain in the viewer too: its faces sit on the box's edge, where the weight is 0. The community tutorial:
  modemworld.me 2024-06-12 "creating a simple (prim) mirror". `assistant/build.py` builds them this way.
- Avatars and particles appear only in **dynamic** mirrors.
- Off in all the viewer's graphics presets (RenderMirrors); on here, since a mirror costs nothing until one's in view.

Different, for a headset:
- **Each eye has its own reflection.** The viewer renders a cube map from the camera mirrored in the plane and samples
  it by the reflected direction; one point can't serve two eyes (the reflection would look infinitely far away). Here
  each eye renders the frame's draw list from itself mirrored in the plane (its x axis turned round so it stays a
  rotation; the projection's left and right swapped), into an image a pixel at u reads at 1 - u. Exact planar
  reflection per eye.
- **Cheap where it can be**: only the mirror's pixels are rendered (its box projected), at `mirror_resolution` of the
  eye's size in 128-pixel steps, as a cube snapshot face is (no SSAO, no stars, the eyes' shadow maps), clipped at the
  plane by `gl_ClipDistance`, blended faces sorted again from the mirrored eye; 5 mips for rougher surfaces. A mirror
  under ~16x16 pixels isn't rendered. One draw list serves both: culling keeps what the eyes see or the mirror can show
  (`Frustum.Either`).
- **Which mirror**: the viewer takes the nearest in view, which in a ~100-degree headset view was often one at the edge
  of sight. Here distance counts more the further a mirror is off the line of sight (x3 at 90 degrees); one seen from
  behind isn't a candidate; the current one stays until another scores 10% better.
- **You in it**: the own body in the headset is headless for the eyes. In a dynamic mirror it's drawn a second time,
  whole, the head and what's worn on it put back. Instances carry a view mask (`InstanceFlags` MainOnly/MirrorOnly,
  checked in `scene.vert`); a static mirror marks every avatar's draws MainOnly. Not done: the mirrored head follows the
  animation, not the headset's rotation (SelfBody carries no head orientation).
- **Particles** in dynamic mirrors are the frame's quads turned to face the mirrored eye, sorted among the mirror's
  blended faces, clipped at the plane (`particle.vert`; `Renderer.Mirror.cs` MirrorParticles).
- **Pictures and the camera's preview** (2026-10-29) each pick their own mirror from their camera, as the eyes do, and
  render its reflection the same way at their own full size through their own targets (`Renderer.StillMirror`, let go
  after the still; `PreviewMirror`). A still's tiles and 360 faces each take theirs. Avatars in the preview's follow the
  eyes' mirror if they have one. A still with a dynamic mirror in view costs about twice (8.5 -> 18 ms at 1280x720).
- No mirror inside a mirror, as in the viewer.

Cost (RTX 5070): XR Simulator, Quest 3 eyes at full size, a 2.6 m mirror 3.5 m ahead: 0.27-0.5 ms a frame for both eyes.

## The camera's preview

2026-10-29. The handheld camera's panel (`docs/assistant.md` > Pictures by hand) shows the picture it's about to take,
live. `Realtime/FrameRenderer.Preview.cs` (the view, its targets), `Frame/Renderer.Preview.cs` (the pass),
`Realtime/Overlay.cs` (`OverlayScene.Preview`; `overlay.frag` styles 29-30, binding 9); the client's `Shutter.Decorate`
asks for it.
- Another view inside the person's frame, like a mirror's, not a still (a still builds a second draw list and waits for
  the GPU: a hitch every frame). Only on frames whose overlay frames a preview. The draw list is culled for its view as
  well (`Frustum.Either`).
- Drawn as the snapshot will be: a camera at the panel's centre along its facing, the shutter's tangents, a 0.1 m near
  plane, full shading and post, the eyes' shadow maps and probe list, recorded after the shadows, before the mirrors
  and eyes. The exposure is held (`HeldExposure`), so the eyes' adaptation never sees it. One profiler block, "preview".
- A fixed 768x768 target of its own, the panel's shape stretched over it, so moving hands remake nothing. Made when the
  camera comes out (a standby while nothing is framed) and let go 30 s after it's put away, through the frame's retire
  list.
- On the panel: drawn at its corners in the eye, opaque, tested against and writing the eye's depth, pushed 10 cm behind
  the plane so the hands holding its corners are drawn over it; its footprint goes into the hands' mask (style 30), so
  it shows over menus it's in front of. A surface less than 10 cm behind the panel hides it (`PreviewBehind`).
- Differs from the saved picture: shadows outside the eyes' view (the cascades are fitted to the eyes); no see-through
  layer cap.
- Cost (dense scene, XR Simulator): 1.1-1.2 ms of GPU while framing, ~0.15 ms of render-thread CPU, nothing on standby.
  Bringing the camera out makes a 55-70 ms display step (the targets' allocations).

## Blended faces: sort order

- **Prims sort first**, each as the viewer's spatial groups do (`LLSpatialPartition::calcDistance`: the box's centre
  pulled toward the camera by a quarter of its half size), measured from the head's centre (`RegionScene.GroupDepth`).
  Each prim stands for one of the viewer's octree groups (which group neighbours of a size together, whose faces then
  interleave).
- **Within a prim, each face** by its own centre pulled toward the camera by a quarter of its world box
  (`LLDrawable::updateDistance`), as a distance from the head's centre: `r - sum(halfsize * 0.5 * u^2)` for the unit
  direction u (`DrawCmd.SubDepth`; the face's box is `FaceGeometry.Center`, `HalfSize`). The viewer uses depth along
  the view axis; by distance the order stays put while the head turns.
- Particles by their distance, avatars' blended parts by theirs, so all share one key. Mirrors re-sort by the faces'
  centres' distance from the mirrored eye; HUDs keep the viewer's key along their axis (`ViewSetup.PlanarSort`).
- **Ties keep the order they were drawn in** (`List.Sort` isn't stable: overlapping leaf faces of one object flickered
  frame to frame). `RegionScene.AlphaDepth`, `Renderer.BuildBatches`.
- Interpenetrating blended surfaces have no right order: a thin blended plane through a birch's crown at Mirromere
  (2026-10-28) showed as streaks whose order changed as the head turned, in the viewer too; by distance it holds still.
- Rigged blended faces are drawn first with depth writes, unsorted, as `LLDrawPoolAlpha::renderPostDeferred` does.

## The scene budget's band

Something the scene budget leaves out (under `Budget.Pixels` on screen, or its shadow under `ShadowPixels`) comes back
only once it's a quarter over the limit (`RegionScene.Budgeted`, per object, the main view's state). Without it, objects
near the threshold popped in and out as the head moved (leaves in Mirromere's forest at the budget's top step).

## The alpha shader

- `alpha.frag` discards below the alpha pool's MINIMUM_ALPHA (0.004) on every blended path, as the viewer's alphaF,
  fullbrightF (IS_ALPHA) and pbralphaF do.
- The sun shadow is sampled only for a fragment that shows (after the discard) and not for fullbright faces: close-up
  layers of flower cards are mostly transparent texels that each paid for a shadow lookup. 12 flowered panes filling two
  Quest-sized eyes (`render_scenes.py flowers`): alpha 1.72 -> 1.30 ms an eye. Solid translucent glass gains nothing:
  every layer is lit in full, as in the viewer (the next section's simpler lighting is for that).

## See-through surfaces: simpler lighting and a layer cap

2026-10-27, both the user's approvals (simpler lighting a small visible difference; the cap their own idea).
`Frame/Renderer.Layers.cs`, `Shaders/alpha_simple.vert`, `alpha_count.frag`, `layers.glsl`, `alpha.frag` (SIMPLE_LIGHT,
LAYER_CUT). Settings `simple_see_through` and `alpha_layers`, menu > view "see-through light" (full, simple) and
"see-through layers" (all, 4, 8, 16). Main eyes only (the pre-water pool takes the lighting, not the cap); mirrors and
probe captures as before.
- **Simpler lighting, on by default** (the user's choice, 2026-10-27, after trying it at Luane's World: no difference
  they could see, and the blended pass there went 11-17 -> 4.4-5 ms): what varies slowly over a surface is computed per
  vertex and interpolated: the ambient with the reflection probes' (sampleAmbient), the atmospherics (sunlight, haze and
  its fading) and the six forward lights. Per pixel stay the texture, the sun's angle and shadow (normal-mapped where
  there's a map), and reflections (materialF's gloss and environment, PBR's radiance). Back faces of double-sided faces
  take their front's ambient. Fullbright faces unchanged. The probes' per-pixel ambient loop was most of the cost.
- **The layer cap, off by default.** Before the blended faces, `alpha_count.frag` draws them nearest first against the
  opaque depth without colour (fullbright ones not counted), keeping per pixel a budget spent by each fragment of alpha
  0.1 or more by what it hides (-ln(1 - alpha), 64 units for 1% let through, but at least 64/N), and the farthest
  counted depth. `alpha.frag` drops a fragment that lies behind every counted layer once the budget is spent: what's
  dropped lies behind layers letting through under 1% of it, or behind N layers of alpha 0.1 or more. The record is
  checked after the alpha test, so transparent texels don't pay for it (checked before, it made the pass slower than
  without it). `SOJOURN_LAYERS_DEBUG=1` draws what it drops magenta.
- **Measured** (`render_scenes.py flowers` and `mist`, two eyes of 2448x2672 with the Quest's frusta, shadows 3072,
  median ms a frame by `SOJOURN_AB=alphalayers|simplealpha`):

  | | panes | field | glass | mist |
  |---|---|---|---|---|
  | alpha, as the viewer | 5.95 | 3.11 | 2.45 | 1.60 |
  | simpler lighting | 1.75 | 0.84 | 0.66 | 1.59 (fullbright) |
  | cap N 16: count + alpha | 1.6-1.9 + 6.1 | 0.72 + 3.3 | 0.55 + 2.6 | 0.08 + 1.5 |
  | cap N 4: count + alpha | 1.8 + 6.0 | 0.71 + 3.6 | 0.54 + 2.5 | 0.08 + 1.6 |

  The cap drops almost nothing in these scenes (a pixel meets one to three lit petals) and adds a pass over every
  blended fragment; at N 4 it visibly removes what's seen through four panes of nested glass cases. Simpler lighting's
  pictures differ faintly at petal edges and on the glass (`local/levers/compare/`). Where the cap might pay: dense
  blended foliage whose texels are mostly opaque (canopies seen from inside); untested.

## Light tiles

2026-10-27. `Frame/Renderer.Tiles.cs`, `Shaders/tilecull.comp`, `tiles.glsl`, `locallights.glsl`, `projectors.glsl`. The
local-light and projector passes are fullscreen loops over the lights in view (the viewer draws a volume per light);
busy places hold 100-160 lights and 40-60 projectors in view, and those passes were the costliest there (Envision: 5 ms
an eye).
- After each main eye's G-buffer a compute pass lists, per 16x16-pixel tile, the lights whose sphere reaches the tile's
  pixels (its four side planes and the box between its nearest and farthest depth). Projectors must also have the tile
  in their frustum, unless a pixel there is a legacy face with an environment reflection, which takes a projector's
  light from outside its frustum. The passes loop over their tile's list (up to 255; past that, everything), bounded
  whatever a record says (255 a tile, 1024 a view, indices checked). `projectors.frag` also skips a projector at pixels
  outside its frustum where nothing can take it. Both passes read their tile's header before the G-buffer and leave
  where nothing of theirs reaches (-0.4 to -0.6 ms a frame in most test views).
- The same picture, checked pixel for pixel against the old loop. Probe faces and mirrors still loop over everything.
  The tiles cost ~0.35 ms an eye at Link's size. `SOJOURN_LIGHT_TILES=0` for comparing; numbers in `docs/performance.md`
  > The GPU in busy places.
- Folding them into the sun and ambient pass, or merging the two, is slower where rooms are full of light: the big
  shader runs everything at the loops' low occupancy (club 8.6 -> 10.7 ms).

## Two eyes: multiview and per-eye passes

2026-10-27. `Frame/Renderer.cs` RecordStereoScene, `Frame/RenderTargets.cs` (Layers, Eyes). Two eyes of one size render
through a stereo target set: the G-buffer, depth and HDR scene are two-layer images, each eye with a set of its own over
its layer (2D views, their own bindless slots; the rest of the targets shared, the eyes taking turns). The forward
passes (fullbright, glow, avatar alpha) and the alpha pass (blended faces, particles, their glow) draw both eyes at once
(VK_KHR_multiview: pipeline variants with the view mask, made on first use); `VIEW` and `SKY` are read by
`gl_ViewIndex`. The G-buffer, the lighting chain, water, haze and post go an eye at a time: on the RTX 5070 the
four-target G-buffer took 2-3 times as long drawn to both eyes at once (club 0.6 -> 1.9 ms a frame), and the multiview
forward and alpha passes measured neutral (recorded once instead of twice). Mirrors are rendered for both eyes first.
`SOJOURN_MULTIVIEW=0` draws the eyes one after the other; `SOJOURN_MULTIVIEW_GBUFFER=1` puts the G-buffer in multiview too.

## The lenses' hidden area

2026-10-27. `Frame/Renderer.Lens.cs`, `Shaders/lensmask.*`, `lensHidden()` in `common.glsl`,
`Sojourn.Client/Xr/XrLensMask.cs`. Meta's PC runtime offers XR_KHR_visibility_mask: per eye, the triangles of its image
the lenses hide. Each eye gets an R8 mask drawn from them (redrawn only when the mesh, the eye's size or its projection
changes), and the per-pixel passes skip what it marks: the sun-light map and its blur, sun and ambient, local lights,
projectors, haze, fullbright, glow, blended faces, particles, water, SMAA's edge detection. Nothing seen changes:
- the mesh is scaled out from the eye's axis until its edge is 96 px inside the hidden area (`LensMargin`), so every
  visible pixel's neighbours (the blur, SMAA, CAS, glow's ~50 px) are still shaded;
- the G-buffer is drawn whole (SSAO samples depth up to 500 px away);
- the window the luminance pass samples for exposure, and a margin around it, is never marked;
- frames kept for someone (pictures, videos: `CaptureTo`, `VideoTo`) don't use it; developers' screenshots
  (`CapturePath`) show it.

Skipped pixels end black (the desktop's mirror window shows black corners). `SOJOURN_LENS_MASK=0` turns it off;
`SOJOURN_TEST_LENS_MASK=R` puts a round test lens of radius R (tangents) on the desktop's `--quest-eyes`: with R 1.2 (19%
of an eye hidden) -0.33 to -1.15 ms a frame. Meta's real mesh hasn't been looked at (the XR Simulator doesn't offer the
extension); the console logs each eye's triangles when they arrive.

## Small, invisible savings (2026-10-27)

- Shadow casters that cut nothing (no alpha, not water) are drawn depth alone, without a fragment shader: -0.03 to
  -0.09 ms a frame on the test views.
- The G-buffer isn't cleared: geometry or the sky writes every pixel of all four targets.
- Blended faces draw with `alpha.frag` compiled for their material's path alone (`ALPHA_PATH` 1-4: PBR, fullbright,
  alphaF, materialF; `Renderer.AlphaPaths`), their sorted batches split where the path changes: the same code without
  the registers the other paths hold. Flower field alpha 7.5 -> 5.6 ms a frame.
- The far sun cascades take turns (`Frame/Renderer.Cascades.cs`): each frame one of cascades 2 and 3 keeps last frame's
  map when this frame's fitted box lies inside it (same region, sun, map size), its matrices turned to the eyes' and
  mirrors'. A still view saves 0.45-0.78 ms a frame at 3072 (cascade 2 is the costliest, 0.45-2.9 ms in the forest); any
  movement changes the fit, and then all four are drawn. Moving casters' far shadows are a frame late while it holds.
  The console says how often it held every 900 frames.
- A probe capture's sun shadow (2026-10-29): a capture spread over frames had to draw it into cascade 0 on each of them,
  since the frame's own shadows overwrite that map. Now it's drawn on the pass's first frame, kept in an image of its
  own (`Renderer.KeepCaptureShadow`, 37 MB at 3072, once captures run) and copied back on later frames; the shaders read
  cascade 0 as before. Captures -25-35% a frame while a place fills in. A map the captures sample directly saved more
  (-46%) but moved probes by a level here and there: another constant index into `shadowMaps` compiles to slightly
  different arithmetic on this card.
- Precise barriers for the renderer's own transitions are behind `SOJOURN_PRECISE_BARRIERS=1`: not measurable under the
  shared GPU's load, so left off.

## GPU options to judge (2026-10-27)

Built as settings at the user's call to try them (2026-10-27); each is a row in menu > view and a `SojournSettings`
name. **The user's verdict (2026-10-28, in the headset): all four stay off by default, kept as options.** `half_ssao`:
they could hardly tell, but want full size as the default. `far_shadow_margin` (tried at 0.5, five times the suggested
0.1): a little noticeable once you know. `min_caster_texels` and `narrow_gbuffer` saved about nothing in our tests.

Measured on the perf grid's forest by the bench (two eyes of 2448x2672 with the Quest's frusta, shadows 3072, noon, five
views including an orbit moving at 7 m/s and 18 degrees a second); pictures against the same build with the option
off; `local/gpuopts/`:

| option (setting) | GPU a frame | look |
|---|---|---|
| sun-light map at half size (`half_ssao`, "shadow & ssao size") | -1.3 to -1.6 ms (sunlight + its blur 2.2-2.7 -> 0.9-1.15 with the bring-up's 0.25-0.32) | leaf and shadow edges: mean 0.4-0.6/255, 99th percentile 6-14 levels, 1-1.5% of pixels off by more than 8 |
| far cascades 10% larger (`far_shadow_margin` 0.1) | moving: shadows 2.17 -> 1.72 ms (a far cascade kept in nearly every frame); still views unchanged | far shadows 10% coarser: mean 0.1-0.2/255 |
| far cascades 20% larger (0.2) | the same as 10% | slightly more change |
| small casters left out (`min_caster_texels` 1 or 2) | nothing here; even at 50 texels only 1-2% of the shadow pass (the forest's shadow cost is its trees) | identical at 2 texels |
| narrow normals (`narrow_gbuffer`, on restart) | -0.02 to -0.05 ms | mean 0.04-0.05/255 |

- **Half size** (`Frame/Renderer.HalfLight.cs`, `lightup.frag`, `HALF_LIGHT` in sunlight.frag and blurlight.frag): the
  sun shadow, SSAO and projector shadows are lit at the top-left pixel of each 2x2 block, the viewer's two blur passes
  run at that size with their taps spaced in the eye's pixels, then a pass brings the map back to the eye's size: the
  four nearest texels by bilinear weight, each weighed down by how far its depth lies from the pixel's (4% of the
  distance counts as the same surface). Main eyes only. `SOJOURN_HALF_LIGHT=1|0` overrides, `SOJOURN_AB=halflight`.
- **Far cascades fitted larger** (`ShadowSettings.FarMargin`, `SunShadowSetup`, `Renderer.Cascades.cs`): cascades 2 and
  3 are fitted to their receivers grown about their middle, and kept from the frame before while the receivers as fitted
  without the margin all lie inside the old map, so they hold while the head turns and walks. `SOJOURN_FAR_MARGIN=F`.
- **Small casters** (`ViewSetup.TinyCasters`, `RegionScene.DrawObject`): an object whose bounding sphere spans fewer
  than the setting's texels in a sun cascade's map is left out of that cascade. Objects only. Where it might pay: SL's
  busy places, full of small clutter and jewellery; unmeasured. `SOJOURN_MIN_CASTER_TEXELS=F`, `SOJOURN_AB=tinycasters`
  with `SOJOURN_CASTER_TEXELS`. Lower-detail casters weren't built: a coarser caster shadows its own finer surface wrongly,
  and needs a second detail level of each mesh.
- **Narrow normals** (`RenderTargets.Narrow`, `gbuffer_util.glsl` gbNormOut/gbNormIn): the normal target in A2B10G10R10
  (10-bit octahedral normal, env intensity in 9 bits with the self mark in b's top bit, the flag in the 2-bit alpha).
  Emissive in B10G11R11 was dropped: the sky, written there, banded in rings. Read once at start
  (`SOJOURN_NARROW_GBUFFER=1`). On this card the G-buffer's bandwidth isn't what costs.

## Where shadows end (2026-10-29)

The user's finding in the headset on SL: sun shadows ended on a sphere around the head, a ring on the ground, worse under
lag. The viewer (`shadowUtil.glsl`) fades its last cascade over that cascade's far half, ending at the draw distance or
twice the farthest receiver, and terrain is drawn past the draw distance, so the edge lands on open ground. The load
governor's trim (`docs/performance.md` > The load governor) brought the end in to half the draw distance, where
cascade 3's fade shrank to a few metres: a hard circle past which trees stood on fully lit ground.

Now (`far_shadows`, menu > view > graphics > far shadows, on at 1024; `sharper` is 2048): a fifth sun cascade, coarse, in
its own map (shadow map 6, `SunShadowSetup.Far`, `FrameRenderer.FarShadow.cs`, `shadows.glsl`):
- Fitted as the others from where cascade 3 ends (its full reach, or the trim's) out to 1.25 times the full reach
  (`ShadowSettings.FarEnd`), grown 25% about its middle (`FarCascadeMargin`). The shader hands cascade 3 over to it across
  a fifth of cascade 3's end and fades it out from the full reach (the draw distance) to its own end; cascade 3's own
  fade goes. Up to cascade 3's end the picture is as before.
- Kept from an earlier frame while every receiver it must cover lies inside the old map, the region, sun and size are
  the same, and it's under 8 frames old (`SOJOURN_FAR_REFRESH`; moving casters' far shadows are that late): kept 87% of
  frames in the bench views, moving or not. A still picture draws its own.
- Casters under a texel across in its map stay out (`FarCasterTexels`, `SOJOURN_FAR_CASTER_TEXELS`), and Linden grass;
  masked faces pick the mip the full-detail cascades would there (`shadow.frag` casterTex), so leaf cards don't thin.
- Under the trim, full-detail shadows end sooner and the coarse ones carry on: the trim moves where shadows turn softer,
  not where they end. With far shadows off, everything is as the viewer has it.

Cost (perf grid forest, two Quest eyes of 2448x2672, shadows 3072, `SOJOURN_AB=farshadows`; `local/shadowedge/`): the far
map 0.11-0.20 ms a drawing at 1024 (0.22-0.42 at 2048), ~0.02 ms a frame kept 7 frames of 8, the sunlight pass +0.01-0.03
ms, cascades 0-3 unchanged. In pictures, trees from ~90 m to the draw distance keep their shadows instead of fading, and
a trimmed frame looks like the untrimmed one, a little softer past the trim (2048 looked the same as 1024).
`SOJOURN_FAR_SHADOWS=N` sets the map size over the setting (0 off), `SOJOURN_SHADOW_REACH=F` holds the trim at F, and the
console says every 2700 far frames how often the map was kept.

## Measuring on a shared GPU

`SOJOURN_AB=NAME` flips a switch every frame (`Renderer.AbSwitches`: multiview, mvgbuffer, solidshadows, gbclear, lens,
alphapaths, farhalf, barriers, halflight, tinycasters with `SOJOURN_CASTER_TEXELS`, captureshadow, farshadows,
simplealpha, alphalayers; none for an A/A) and tags each pass in the profiler `|on` or `|off`; `SOJOURN_GPU_LOG`'s `ab`
column says each frame's side. Both sides share whatever else runs on the GPU (an A/A run differs by under 0.05 ms a
frame). `SOJOURN_PROFILE_MEDIAN=1` reports a window's median frame for each pass. The profiler sums a pass marked more
than once in a frame: every pass reads as ms a frame, both eyes. `SOJOURN_PROFILE_CASCADES=1` times each sun cascade.
- The far cascades' turns go with the same parity as the A/B, so a shadows comparison wants `SOJOURN_FAR_CASCADES_HALF=0`.
- The bench takes views from a file (`--bench-views`, a view a line: name, eye, focus) and holds the environment's time,
  so two builds' shots compare pixel for pixel. Add `SOJOURN_GPU_PACING=0` when the builds' costs differ: the load
  governor gives probe captures the GPU time a frame leaves, so a cheaper build captures probes at other moments and its
  shots differ by a level wherever probes light.
- Tried and dropped, slower on the RTX 5070: the lighting folded into one pass, local lights and projectors merged, the
  light map's blur reading map and depth as one 64-bit texel.

## Name tags and floating text in the eye's depth

2026-10-27, after the user found floating text warping with head movement and painful up close: drawn over the
finished image with no depth, the compositor's depth reprojection moved their pixels as whatever lay behind them, and
floating text drew over things in front of it. Now they're drawn with the eye's depth buffer (`Overlay.cs`
`_worldTested`, `_worldOver`, before the rest of the overlay and before the depth goes to the compositor): they write
their own depth where they show (`overlay.frag` WORLD_DEPTH discards below alpha 0.1), floating text is depth-tested (as
the viewer's LLHUDText), tags aren't (the viewer shows them through walls), and both turn to the point between the eyes
rather than to each eye, so both eyes see one plane.
- **Perspective-correct**: the overlay's quads go in already divided by w, which across one big quad interpolates the
  picture affinely (text bending unevenly across a sheet seen at an angle, "like water"). Labels (`Overlay.Tag`, with
  their corners' depth, `QuadZW`), sheets and the assistant's cards at corners (`Overlay.Sheet`, `Overlay.Card`) carry
  each corner's clip w, which `overlay.vert` multiplies back.
- **Nothing cut off**: each eye's overlay vertex buffer grows to what the frame needs (`Overlay.VertexBuffer`). A fixed
  4096 vertices an eye had cut crowds' tags off mid-list (tags losing their bottom halves, the rest of the overlay
  missing, changing frame to frame).

## The headset's near plane and detail levels

- Near plane 2 cm in the headset (`ViewSetup.HeadsetNear`; the viewer's 0.1 m cut things off at the face when leaning
  in); the compositor's depth layers say the same (`XrSession` farZ). Reverse-Z in D32 float keeps the same relative
  precision at any distance whatever the near plane (~1e-5 m at 128 m). Shadow cascades still fit from 0.1 m.
- Objects' detail levels in the headset use the viewer's default camera angle (60 degrees), not the lenses' ~100:
  `VolumeLod.CalcLod`'s zoom compensation would otherwise pick coarser levels (rigged meshes a level down from ~19 m
  instead of ~30 at detail 2.5), though a Quest eye has more pixels a degree than a desktop. More triangles in view in
  busy places as a result (`ViewSetup.LodFov`).

## Blended or masked: as the viewer decides

2026-10-27, checked against secondlife/viewer develop (`llimagegl.cpp` analyzeAlphaData, `llface.cpp`
canRenderAsMask, `lldrawpoolalpha.cpp`), after a statue showing its own far side through its front:
- A texture with an alpha channel that's 255 everywhere is a mask to the viewer (analyzeAlphaData's last test), so a
  face using it renders opaque with depth (auto alpha mask; `TextureStore` IsAlphaMask). Counting only textures with
  some alpha below 255 had blended such faces (older uploads kept an opaque alpha channel): no depth writes, a concave
  mesh's far side drawn over its near side. `render_scenes.py statue` shows it.
- Rigged faces are never auto-masked (the viewer's rule).
- The blended pass doesn't cull double-sided GLTF materials; `alpha.frag` drops the back faces of everything else
  (avatars' own pipelines cull for themselves).
- **The pre-water pool** (LLDrawPoolAlpha::renderAlpha and forwardRender, the pools' order in
  LLPipeline::renderGeomPostDeferred): with water in the frame, blended faces split by the water plane. One whose box
  reaches the far side of the water from the camera is drawn after the water haze and before the water, clipped to that
  side (the viewer's waterClip, alpha.frag's first test), with depth writes, so the water's refraction copy holds it and
  its fog depth stops at it; one reaching the near side is drawn after the water, clipped to its side; one across it is
  drawn in both, a half each. Under water the sides swap. Draws with no bounds at all (an avatar's loading cloud) go in
  both pools. Mirror views and probe captures draw them all after the water, unsplit. `SOJOURN_PRE_WATER=0` turns it off.
  - Which side a face reaches is judged by its own box (`DrawCmd.HalfHeight`, from `FaceGeometry`'s box through the
    model, a centimetre spare), not its object's bounding sphere, which put every blended face of a big linkset near the
    water in both pools (2026-10-29). The pool draws through alpha.frag per material path (`_alphaPreWater[path]`), lit as
    `simple_see_through` says, no layer cut. Together -55% and then -65% of the pool on the shore views (`render_scenes.py
    shore`; `docs/performance.md` > The GPU in busy places): it had been most of Mirromere's 3-13 ms water pass.
  - The profiler splits the water pass: `water haze` (mask, depth copy, haze), `pre-water alpha`, `water` (copies,
    surface).

## Objects kept on screen through updates

A full object update (scripts setting hover text, a colour, a sound: LibreMetaverse makes the texture entry anew) only
marks the object's resolved faces outdated (`FaceCache.Outdated`) unless the shape changed, and they're drawn as they
were for up to two frames like any out-of-date cache (2026-10-26). Dropping them had made scripted things you point at
blink while a busy place loaded. Media coming or going on an object does the same.

## Mist and other flat blended planes

House of Legends' mist (2026-10-26/28: "a flat plane cutting through avatars", mist showing only over stones as hard
white shapes). `render_scenes.py mist` rebuilds it (three drifting fullbright mist planes, particle mist, a pane, a
post). What it showed:
- **Our order and depth match the viewer's**: everything opaque or rigged is in the depth buffer before the planes,
  which are depth-tested, so a horizontal plane covers what's below it as seen from above and nothing above it; an
  avatar standing in it is cut by a straight line. The viewer draws the same; seeing your own body close up in a
  headset makes it plain. Particles face the head's position with world up, so they don't swing as the head turns.
- **A blended ground**: the place's ground was a 62 x 63 m mesh whose textures have an alpha channel (219-255, not a
  mask), so without a material it's blended, and sorted by faces alone it came after the mist lying on it. Prims now
  sort first (Blended faces: sort order). A blended ground that size still sorts at ~14 m when standing near its
  middle, so farther mist is still covered: the viewer's key, so the viewer would do the same if the ground were blended.
- **Lost materials**: the ground was probably opaque by its material ("alpha mode: none") in the viewer, and our fetch
  had taken an empty answer as "no material" for good; LibreMetaverse returns empty when the grid rate-limits the cap
  or the request fails, and the grid de-duplicates materials, so one lost stayed lost everywhere for the session. Now
  (`MaterialStore`, `RegionSource.FetchMaterials`, our own ask since 2026-10-31): the rate limit is told apart from an
  answer, asks are paced per region as the viewer paces them (the simulator's MaxMaterialsPerTransaction ids an ask, 50
  if unsaid; RenderMaterialsCapability asks a second, 1 if unsaid), a rate-limited ask goes back in line with nothing
  counted against it and the region's pace halving each time (to 1/16). Unanswered asks (no cap yet, network) are
  retried at 2, 4, 8, 16, 30 s, none after six; failed materials count as waiting so their faces re-resolve. Not
  confirmed on that ground.

## Animated objects (animesh)

2026-10-26. `World/Avatars.Animesh.cs`, `World/ObjectAnimations.cs`, the index's `Animesh` field
(`RegionScene.Index.cs`). An object whose root has the Animated Mesh flag (extra param 0x70, LibreMetaverse's
`IsAnimatedObject`) gets a skeleton of its own, as the viewer gives it a control avatar (`llcontrolavatar.cpp`):
- No body drawn, the default shape, joint positions from its rigged meshes' overrides. It plays what its prims signal
  (ObjectAnimation, kept by object id as `LLObjectSignaledAnimationMap` keeps it, since the message can arrive before the
  object; the linkset's lists merged, each animation once at its larger sequence). A rig created before anything is
  signalled takes what comes later as starting then.
- Placed as `matchVolumeTransform`: in the world, its root at the root prim, turned by the prim's rotation and the root
  mesh's bind shape rotation (MAINT-8671), kept within 3 m of the prim by last frame's extents
  (`getNewConstraintFixups`); worn, at its attachment point on the wearer as posed that frame, and its meshes don't move
  the wearer's joints. Posed after the avatars, keyed by the root's local id, so its rigged faces skin through the
  worn-mesh path (`AddRigged`) and are culled by its extents.
- Over 64 m across it's scaled down to 64 m about its pelvis (`ScaleFixup`, 2026-10-29: by last frame's joint box padded
  a quarter metre a side, or the rigged meshes' largest span in bind space where that's larger; every joint's scale times
  the fixup, as `recursiveScaleJoint`; worn ones too).
- Not done: a worn one following its wearer's impostor or jelly state beyond what attachments already do; name tags.
- `tools/animesh.py`: a rigged figure with its own looping sway, in Atelier and worn. OpenSim's built-in dances don't
  loop (their `.anim` says so), so they play once on an animated object there.

## Avatars' faces against the viewer

2026-10-27, after "everyone's face melts" at a club on the main grid.
- **Checked equal to the viewer's source** (secondlife/viewer develop, read 2026-10-27): the appearance message's params,
  drivers, skeletal distortion, joint transforms, attachment joint overrides (0.1 mm threshold, the largest mesh id
  wins), the skin palette, skin weights (no terminator after a fourth influence; MeshFoundry 3.2.0's decode compared on
  real heads), animation decode and blending. SL's 253 params against OpenSim's 218 are only the physics drivers, the
  appearance version and hover. An offline harness skinned real SL heads on our `BodyShape`: sliders one at a time do
  what they're named for.
- **Bakes on mesh in PBR materials**: a worn face whose GLTF material or override names a bake's magic id shows the
  wearer's bake (`getBakedTextureForMagicId`; `RegionScene.BakeOnMesh`), as the legacy texture already did; before, LL's
  red "BAKED_HEAD" placeholder.
- **Loading as the viewer**: an avatar stays a cloud until the attachments its appearance lists have arrived, for up to
  a minute after the last one came (`updateIsFullyLoaded`, `MAX_ATTACHMENT_WAIT_TIME_SEC`), rather than showing its
  system body with a head shaped by sliders meant for a mesh head. OpenSim doesn't send the list, so locally nothing
  waits.
- **The melting was altitude** (2026-10-28, a club at 2075 m): the skinning pre-pass blends region-space matrices with
  weights packed as unorm16, rounded one by one, so a vertex's weights summed to 1 ± ~3e-5 and it was scaled toward the
  region's origin by that much: up to 3 cm at 2 km, different for each vertex (faces and hems in vertical streaks; ~1 mm
  at ground level, so never seen locally). `SkinWeight.From` makes the packed weights sum to exactly 65535 (the largest
  takes the rounding): an avatar's 358 worn meshes at 2074 m went from 141k vertices over 1 cm off to none.
- **`SojournAvatars inspect`** (`World/Avatars.Inspect.cs`): one avatar as the renderer has it (params, bakes and their
  state, hidden system meshes, attachments, each worn prim's mesh, skin, detail levels, faces' textures and materials,
  joint overrides, animations), saved to `avatar-reports/` in the state folder: enough to rebuild that avatar locally.

## Flexible prims

2026-10-27, after the user's cape of 34 old-style prims stood still in the headset. `World/FlexiSim.cs`
(llflexibleobject.cpp's simulation), `World/RegionScene.Flexi.cs` (when, where, drawing), `Geometry/VolumeBuilder.cs`
BuildFlexi / BuildFlexiRest, `DrawList.AddFlexi`, `Renderer.UploadFlexi`. Setting `flexi` (on), menu > view "flexible
prims"; off draws them straight.
- **As the viewer**: a prim whose path is flexible (0x80) and has flexible data is a chain of 2^softness sections from its
  anchor (the middle of its -Z face) along its Z, pulled by gravity, the region's wind and its force, held toward its
  parent's direction by tension, slowed by drag, each section's bend limited; the volume is swept along the chain in the
  prim's frame and drawn at the prim's place and turn without its scale. Render sections follow its length on screen
  (`updateRenderRes`). A step is due every 1-32 sixtieths of a second by how much of the screen it covers, only while
  it's in view and over 256 pixels, a linkset's prims in the same sixtieth (`doIdleUpdate`). Worn ones follow the
  wearer: the chain stays where it was while the anchor moves (a cape streams behind a run).
- **Where it differs**: steps run on the thread pool, one at a time per prim, and the prim is drawn with the latest
  finished one (a frame later at most). The chain starts straight again after a jump of 8 m. Only the main view
  simulates them: probe captures, HUDs and the pick ray see the straight shape. The step rate's pixel area is the
  bounding sphere's, so steps come a little more often than the viewer's.
- **Drawing**: a step's faces line up vertex for vertex with the prim's rest shape (`ShapeKind.Flexi`, uploaded once),
  whose indices it's drawn with; the vertices are copied each frame into the frame arena and through the skinning
  pre-pass into the frame's skinned vertex buffer: nothing allocated in the geometry pool per step, nothing written
  that a frame in flight reads. ~0.1 KB of garbage a step.
- **Cost** (`render_scenes.py flexi`, 60 flexies): render thread 0.0-0.2 ms a frame more, GPU unchanged, 1300-3100
  steps a second of 5-25 us each on the pool. The console says every 10 s what they cost (`flexible prims:`);
  `SOJOURN_FLEXI_DEBUG=1` logs each one's state.

## Avatar physics

2026-10-27. `World/Avatars.Physics.cs` (llphysicsmotion.cpp). Setting `avatar_physics` (on, the viewer's AvatarPhysics),
menu > view "avatar physics".
- Six motions (breast in-out, up-down, left-right on mChest; butt up-down, left-right and belly up-down on mPelvis),
  each a driven param in [0, 1]: a spring toward the shape's own, pushed by its joint's acceleration, gravity, damping
  and drag, with the physics wearable's parameters, in steps of at most 0.05 s. Their driven params (1200-1207) move
  collision volumes (LEFT_PEC, RIGHT_PEC, BELLY, BUTT: what fitted mesh bodies are weighted to) and morph the system body.
- The volumes are moved every frame after the pose (they're leaves), so worn fitted meshes follow at no cost. The system
  body is morphed and uploaded again only while a section it shows moves, at most 30 times a second an avatar and two
  avatars a frame.
- No physics wearable (OpenSim sends no physics params) leaves the maximum effects at 0: nothing moves, as in the
  viewer. `SOJOURN_TEST_AVATAR_PHYSICS=1` gives every avatar a lively wearable and logs the driven params' ranges.
- Differs: the viewer starts each motion from the region origin (a jolt as an avatar appears); here from where the joint
  is. The own body's whole copy in a mirror doesn't move with it.
- Open: how it looks on SL's fitted mesh bodies.

## Avatars: lip sync, others' effects, standing feet, fixes (batch B3b)

2026-10-29. `World/Avatars.LipSync.cs`, `Avatars.Effects.cs`, `Avatars.Feet.cs`, `Avatars.Reset.cs`,
`Bodies/Animator.Ik.cs`, `Bodies/JointSolverRP3.cs`; the client half `Control/AvatarsWiring.cs`, `AvatarsApi.Fix.cs`.
Read from the viewer's develop branch and Firestorm master (copies in `local/ref/viewer`, `local/ref/firestorm`).
- **Lip sync** (`lip_sync`, on, the viewer's LipSyncEnabled): a speaker's `Lipsync_Ooh`/`Lipsync_Aah` morphs from voice's
  power as `LLVoiceVisualizer::lipSyncOohAah` (its power transfer curves and babble loops); others' power is the
  server's (`VoicePeer.Power`), your own from the mic while it goes to residents; nobody blocked. Only the head is made
  again each frame: the shape's morphed head plus the two lip morphs (linear in their weights), its vertices through the
  frame arena into the skinning pre-pass (`DrawList.AddSkinnedCopy`), ~1100 vertices copied; the body shape's own
  rebuilds aren't involved. Departures for smoothness: the curves and loops read between their entries and the power
  eased over 60 ms (the viewer steps 24 times a second), easing closed when speaking stops. The system head only, as the
  viewer: a mesh head's mouth is its own scripts'. `SOJOURN_TEST_LIPSYNC=1` makes everyone else speak.
- **Beams** (`selection_beams`, on, the viewer's ShowSelectionBeam): the spiral family of viewer effects (beam 7, point
  9, sphere 11, spiral 12, edit 13; read from the packet, since LibreMetaverse drops their colour) drawn as their
  particle sources: with a target, `LLViewerPartSourceBeam` (particles along the beam from the sender's left wrist to
  the target in the effect's colour); with none, `LLViewerPartSourceSpiral` (circling round the sender and rising).
  Worked out from when each particle would have been emitted rather than simulated, and added to the frame's particles
  (`ParticleBuilder.Extra`), so they sort and blend with the rest and the particle switch hides them.
- **PointAt** (received only; Sojourn sends none and no beams, the user's call): starts the viewer's editing motion on
  the sender (`LLEditingMotion` with `LLJointSolverRP3` ported): the left arm reaching toward the target, eased out over
  half a second at a None or Clear; not under 500 pixels of the avatar. A near-straight arm reaches only roughly, as in
  the viewer.
- **Standing feet**: the default stands (stand, stand 1-4, crouch) are `LLKeyframeStandMotion`: each leg is solved so the
  ankle reaches the ground under it (`LLVOAvatar::getGround`: the land within a metre, or the avatar's foot plane from
  the simulator, whichever is met first; OpenSim's zero-normal plane ignored) and the ankle laid on its slope, every
  frame. Not for the own body in the headset (the rig puts its feet down) nor animated objects. AOs' stands aren't these.
- **Fixes** (`SojournAvatars resetSkeleton`, `refreshAttachments`, `resyncAnimations`): Reset Skeleton rebuilds the
  shape, the joints worn meshes move and physics (with animations, everything restarts: `resetAnimations`); for your
  own it's also sent as ViewerEffect 19, which every viewer obeys from the avatar's owner (and so do we). Refresh
  Attachments: yours each attached again where it is (Firestorm's "phantom attach" fix); anyone's asked of the region
  again. Resync Animations: every avatar's and animated object's animations restarted together (Firestorm's).
- **Jelly dolls' particles**: particle sources worn by an avatar drawn as a jelly doll (or set never to render) aren't
  drawn. The viewer stops them only when the avatar is also over its render time; here the doll is the guard against a
  crasher, so its particles go too.
- Tests: `tools/avatarfx check` (no grid: the solver, the editing motion, the stands' feet, lip sync's amplitudes, the
  64 m fixup) and `tools/avatars.py` on the local grid (a second account sending PointAt, beams, lip sync, resets).
  Open on SL: real voice levels driving mouths, the editing arm against the official viewer's, foot IK on real slopes.

## Water: exclusion surfaces and under water

2026-10-28 (batch B3a), from secondlife/viewer develop (`lldrawpoolwaterexclusion.cpp`, `pipeline.cpp`
doWaterExclusionMask and renderGeomPostDeferred, `llvovolume.cpp` genDrawInfo, `lldrawpoolwater.cpp`,
`class3/environment/waterF.glsl` and `underWaterF.glsl`, `class3/deferred/waterHazeF.glsl`). `Frame/Renderer.Water.cs`
holds the water pass.
- **Exclusion surfaces** (the viewer's since 7.1.12; the build floater's "Hide water"): a face whose texture is one of
  the two invisiprim alpha gradients, with its colour's alpha at least 0.999, no legacy or glTF material, and not on an
  attachment, HUD or rigged mesh, hides the water where it's in front of the water's surface (boat hulls, basements).
  Such faces go in their own list (`DrawPass.WaterExclusion`, `World/RegionScene.WaterExclusion.cs`; never in shadows
  or impostors). If any are in view, the water pass draws them into an R8 mask with a depth of its own
  (`RenderTargets.EnsureWaterMask`): the water planes first for their depth, then the faces. Stored inverted (cleared to
  0, faces write 1; `water_mask.glsl`). `waterhaze.frag` skips the fog there, `water.frag` shows the scene unrippled,
  `water_under.frag` stops the ripples' distortion, each as the viewer's shader does. Nothing in view: no mask, no pass.
  Seen on SL (2026-10-31): a hollow tub in 2.7 m of water with an invisiprim box at the surface stayed dry inside.
- **Under water** (the viewer's sUnderWaterRender): the water's fog covers the whole screen (`waterhaze.frag`
  UNDER_WATER), the atmospherics' haze runs before the water, and the surface seen from below is `water_under.frag`
  (the scene above, rippled and fogged by the water between).
- **Sun shadows on the water** (HAS_SUN_SHADOW): `water.frag` samples the sun's shadow for its glints.
- `python tools/render_scenes.py water`: two open boats in the sea off Atelier's west shore (64,128), one holding an
  exclusion box.
- Still differing from the viewer (Multitude `docs/render.md` > Still differs): finer ripple detail in pond
  reflections, shallow water ~15 levels brighter with its ripple streaks washed out. The shaders read as faithful ports
  (checked again 2026-10-28), so the cause is in their inputs (the probes' reflections, the scene copy, the fog);
  finding it wants side-by-side shots against the reference viewer (`projects/multitude/local/refviewer`).

## The environment over time: the person's own, cross-fades, clouds

2026-10-28 (batch B3a), from `llenvironment.cpp` (setEnvironment, updateEnvironment, DayTransition, switchTrack,
updateCloudScroll) and `llviewermenu.cpp`.
- **The person's own environment** (`Environment/PersonalEnvironment.cs`; FrameRenderer.Personal; the viewer's
  ENV_LOCAL): over every region's and parcel's, seen only by them. A fixed sky or water replaces that part and keeps the
  other, or freezes it as shown when nothing local was there; a day cycle replaces both, at the default length and
  offset. Changes are instant. The `sky` setting's presets come through the request's LocalSky; SojournEnvironment `use`
  applies presets, inventory or Library items (`Agents/EnvironmentOps.cs` fetches the settings asset), or an asset by
  id; `shared` goes back. Not kept between sessions but for the `sky` preset. Reflection probes are keyed by the
  personal environment's version while one is shown.
- **The time of day** (2026-10-29; `PersonalEnvironment.SetTime`, `Environment/TimeOfDay.cs`; SojournEnvironment `time`,
  the `time_of_day` setting, menu > view > time of day in advanced, ±30 min a step), over whatever sky is in use, held
  still. *By sun* is Personal Lighting: the sky as shown, frozen, only the sun and moon moved; the default over a fixed
  sky. *By day* is the day cycle editor's scrubber: the day shown evaluated where its sun stands at that time, sky and
  water frozen there (or `run`: the day goes on from there); the default while a day shows. Hours read the sun as
  WindLight's time of day has it (rising at 6 in the east, highest at 12, setting at 18, the moon opposite); azimuth
  and elevation are Personal Lighting's fields (0 east, 90 south). One personal change a frame, so a time asked right
  after `shared` reads the place's day. Pressing the `sky` row's current choice again puts that sky back.
- **Cross-fades** (`Environment/EnvironmentFader.cs`): when what's shown changes layer (a parcel's own sky, the region's
  changing or arriving) the sky and water blend from what was last shown over 5 s (TRANSITION_DEFAULT), 1 s within 10 s
  of a teleport (TRANSITION_FAST), and 5 s when an altitude band switches the day's sky track. A refetch that answers
  the same keeps its layer (`EnvironmentState` compares the answer's LLSD). A region whose environment hasn't arrived
  shows the last one meanwhile, so a teleport doesn't flash the built-in default.
- **Before any place's sky** (the user's calls, 2026-10-30): frames with no region at all (the login, the seconds before
  arrival: `FrameRenderer.Void.cs`) are a flat neutral grey, sRGB #949494. Between a region appearing and its
  environment loading, the viewer's own built-in default sky, as in SL, and the region's fades in from it over 5 s.
  Tried and dropped: a slate void (read teal), our own dawn and late-morning skies, SL's Midday as the stand-in.
- **Clouds** scroll by the time of day (exact across sessions; the viewer's accumulate while it runs), continuous across
  changes of what's shown, held while paused (the `clouds` setting, not kept, as the viewer's).
- **No dynamic exposure or tone map in the live view** (`ViewerRenderSettings.SnapshotNoPost`, Multitude's snapshot
  look): right for legacy skies, but the viewer applies both to every non-legacy sky, so ours are brighter and flatter
  on most modern skies (seen at a skybox whose owner's sky washes it white, 2026-10-28). The user: not worth matching
  for now (2026-10-28).
- Settings assets come from the ViewerAsset capability by `settings_id` (LibreMetaverse 3.2.0 asks for them as
  "invalid_id" and never finishes).

## Property lines and ban lines

2026-10-28 (batch B3a), from `llviewerparceloverlay.cpp`, `llviewerparcelmgr.cpp`, `llglsandbox.cpp`
(renderOneSegment, renderCollisionSegments), `llviewermessage.cpp` and colors.xml. `World/ParcelLines.cs` builds them on
the render thread for the main view; `Frame/Renderer.ParcelLines.cs` draws them over each eye's finished image, as the
viewer's render_hud_elements does: colours as given, alpha-blended, depth-tested against the eye's scene, after pictures
and videos take the image, under HUDs and menus.
- **Property lines** (`property_lines`, off by default as ShowPropertyLines): from each region's ParcelOverlay, a
  6.25 cm strip along each edge of land that isn't public, following the ground in 1 m steps and split at the water's
  height, in the owner's colour (yours green, your group's teal, others' red, for sale orange, auction purple, 40%
  opaque); within 256 m; lifted toward the head by 1 cm a metre of its height over the ground. They write depth.
  Rebuilt when the overlay, the terrain or the water height changes. Departure: the viewer culls them behind a plane
  along the camera's axis; here distances are from the head.
- **Ban lines** (`ban_lines`: collision, the default, proximity or hide, as ShowBanLines): a ParcelProperties with a
  collision sequence id carries a bitmap of the parcels you'd collide with; its outline becomes walls of the viewer's
  tape (NoEntryLines.png; NoEntryPassLines.png for a pass-list parcel; shipped as `Textures/*.j2c`), 50 m high, 5000 m
  where you're banned or the region blocks flying over, more opaque the nearer the head (the viewer's falloff). Shown
  for 10 s after the region refuses you, or in proximity mode for 1 s after each report. No depth writes.
  `SOJOURN_BAN_TEST=x0,y0,x1,y1` (region metres) reports a ban around that rectangle every half second (the local grid
  has no ban lines). Seen on SL (2026-10-31): they come and go in sections, as the viewer's do (one opacity per 4 m
  segment by its distance, none past 13 m); kept.

## Beacons and highlights

2026-10-29 (batch B8), from pipeline.cpp (render*Beacons, postSort, renderHighlights), llglsandbox.cpp
(renderObjectBeacons), lldrawpoolalpha.cpp (renderDebugAlpha), llvovolume.cpp, lltracker.cpp, llfloaterbeacons.cpp and
Firestorm's changes. `World/Beacons.cs` gathers them on the render thread after the main view's draws are built
(`RegionScene.Beacons.cs`); `Frame/Renderer.Beacons.cs` draws them over each eye's finished image right after the
parcel lines: in both eyes and user screenshots (F12), not in the assistant's pictures, under HUDs and menus. The client
half is `Sojourn.Client/Beacons/` (settings in `beacons-settings.json`, `SojournBeacons`, the tracker).
- **Beacons** (`beacons` the floater open, off; `beacon_lines` off, `beacon_highlights` on; kinds `beacons_touch` and
  `beacons_physical` on, `_scripted`, `_sounds`, `_particles`, `_media` off: the viewer's defaults). Scripted and touch
  only at an unworn linkset's root (exclusive, as the floater keeps them), physical, particle and media at any prim;
  sound beacons at world audio's sources (`WorldSounds.Places`), sound highlights on prims with an attached sound. Only
  what's in view and within draw distance. Each: a cross 2 m across and 50 m up and down over everything at a quarter
  of its alpha, a 0.5 m cross and a 10 cm wire cube tested against the scene; the viewer's colours (red scripts, green
  physical, white media, light blue particles, yellow sounds). Lines are strips facing the head, `beacon_width` pixels
  wide (at least 1.5), split so the width holds along a 100 m line.
- **Highlights**: those objects' faces red at half, with the shape the frame drew, back faces culled, no depth writes.
  Rigged, flexible and animated meshes aren't highlighted.
- **Sun and moon** (`sun_beacon`, `moon_beacon`): 50 m from the avatar's middle toward each.
- **Highlight Transparent** (`highlight_transparent`, Ctrl+Alt+T on the desktop, for the session): every blended face the
  frame draws red, masked ones blue (not foliage, avatars' own bodies, terrain, water or sky, which aren't the viewer's
  alpha pool), and faces the frame never draws: fully transparent ones red, invisiprims green (not on what's worn). Each
  tinted with the viewer's smoke image (IMG_SMOKE) in the face's own texture coordinates, through scene.vert so they meet
  the G-buffer's depth exactly. Not done: picking through transparent faces while it's on.
- **The tracking beacon** (`Beacons/Tracker.cs`, LLTracker): a column 2 m wide, red above the target up to 5020 m, blue
  below it, fading in 3-6 m away, mixed toward a haze grey with distance; tested, no depth writes. Its label ("name /
  123 m") is a floating text placed no further than 5 m along the line of sight, so it keeps a near tag's size (the old
  picture stays until the new one is in the labels' atlas, so the distance doesn't blink). Ends within 3 m of a place or
  of a person; a person is found in the regions in view, a friend with map rights by FindAgent every 5 s; not found for
  15 s, or a friend offline, ends it. `tracking_beacon` (on), `tracking_beep` (off). Driven by the map board
  (`Tracker.Shared`) and `SojournBeacons track`/`untrack`/`state`. Not done: the 2D arrow at the screen's edge, the
  "cheesy beacon" pulse.
- Not done: the legend ("Viewing particle beacons (blue)"), Firestorm's region-corner beacons and chat range spheres,
  beacon text labels.
- `tools/beacons.py scene` puts a test platform 60 m over Atelier 96,200; `run` shoots it with each kind on and checks
  each by its colours.

## The interest list's 360 mode

`interest_360` (off by default; `Control/EnvironmentWiring.cs`): LibreMetaverse's InterestListManager posts mode "360" to
each region's InterestList capability (and to regions connected later), as the viewer does for its 360 snapshots: the
region sends everything around the agent rather than what's in front first. The user tried it at Firestorm Social Island
(2026-10-30): things behind arrive sooner, but off unless chosen. OpenSim has no such capability.

## Test scenes

`python tools/render_scenes.py mirror|mist|flowers|lights|particles|club|statue|flexi|water|shore|remove` (local grid;
`SOJOURN_GRID=perf-grid` for the other). Platforms 60 m over Atelier: 64,64 mirrors (static, dynamic, one facing west for
the headset, which logs in facing east; a single flagged cube; particle fountains in front of both wall mirrors and one
behind where you stand), 96,64 mist, 128,64 a flower field of 800 blended cards, glass cases and domes, 12 flowered panes
in a row (`lights` adds 48 point lights over it), 160,64 `particles` (fountains of bouncing and homing particles near the
viewer's 4096). `club`: a room at 192,64 with 100 coloured point lights and 48 projectors; `statue` at 224,64; `flexi`
150 m up at 176,224; `shore` a bank of blended grass sloping into the sea (views in `tools/shore-views.txt`).
`SOJOURN_SELF_TEST=front` puts the desktop camera at the own body's eyes looking ahead, for mirrors without a headset.
