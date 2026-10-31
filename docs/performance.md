# Frame time and the VR budget

Written by Claude Opus 5.5, 2026-10-20 to 10-29; reorganised around how things are now on 2026-10-31. Agent-facing.
Measured on the RTX 5070 (12 GB) and Ryzen 5 9600X, Release builds, vsync off. Every number carries the date it was
measured; later work may have moved it. Where another session's work shared the GPU during a run, it says so.

## Where the frame goes

- **The budget.** The Quest 3 over Link at 90 Hz: 11.1 ms a frame, of which the GPU gets ~9 ms (Link's compositor and
  encoder take 1-2). Link recommends 2448x2672 an eye; the eyes are full size by default (`resolution` 1, the user's
  call 2026-10-26; auto, following the GPU, is the option: `docs/vr.md` > Dynamic resolution) and the user's settings
  have shadows at 3072. A Quest 3 eye is about 2064x2208 at 1.0x. Link's rate (72/80/90/120) is the person's to set;
  at 72 Hz there are 13.9 ms.
- **In busy places it's the GPU.** Calm regions ran 8-9 ms of GPU a frame (2026-10-27); busy SL places 18-40 ms
  (Mirromere, Calas Galadhon, Envision, Tempura; 2026-10-27/28), steadily, not only while arriving. In the headset at
  Tempura the render thread's CPU was 5-8 ms a frame (2026-10-28, once the frame-end thread took over the wait before
  releasing images: `docs/vr.md` > The rig > Overlap), so the GPU is what runs over. What costs there, roughly in
  order: blended faces (the alpha pass, and the pool of them drawn before the water), local lights and projectors in
  rooms full of them, shadows (4-16M triangles in the maps), the lighting chain (ssao blur ~0.85 ms an eye everywhere),
  probe captures while a place fills in. The GPU in busy places, below, has what was done about each and what's left.
- **Where a calm frame goes** (the local grid's dense scene, 2026-10-20: two eyes of 2064x2208, 98 degrees, shadow cap
  1536, the plaza view; before light tiles, the lenses' hidden area and the alpha paths):

  | pass | ms an eye | ms a frame |
  |---|---|---|
  | shadows (4 sun cascades, 2 projector maps; drawn once for both eyes) | | 1.14 |
  | G-buffer | 0.68 | 1.36 |
  | sky (after geometry, only uncovered pixels) | 0.01 | 0.03 |
  | sun shadow + SSAO into the light map (`sunlight`) | 0.49 | 0.97 |
  | the light map's two blur passes (`ssao blur`) | 0.55 | 1.10 |
  | sun, ambient and reflection probes (`light.frag`) | 0.59 | 1.19 |
  | local lights | 0.56 | 1.11 |
  | projectors | 0.25 | 0.50 |
  | fullbright, avatar alpha, water, haze, alpha | 0.49 | 0.97 |
  | post: tonemap, CAS, glow, combine, SMAA, present blit | 0.45 | 0.90 |
  | skinning | | 0.17 |
  | total | | 9.5 |

  The deferred lighting chain (six fullscreen passes, each reading the G-buffer again) was half the frame; then
  shadows, fitted to the centre view and shared by both eyes. The viewer sizes shadow maps to the screen, which at a
  headset eye's size made them the costliest pass (2-3.5 ms) until the cap: the dense scene at headset size took
  9.3-11.4 ms a frame uncapped, 7.8-10.0 at 1536.

## How to measure

- **The bench**: `python tools/client.py run --bench [--stereo] --eye WxH [--fov 98] [--quest-eyes] [--shadow-cap N]
  --sky noon --bench-out FILE` holds a set of views (Multitude's comparison views of Atelier by default, or
  `--bench-views FILE`: a view a line, name, eye, focus) and a moving orbit, each for `--bench-seconds` after a warm pass
  that waits until everything in view and its reflection probes have loaded. Per view: frame time (mean, p95), GPU ms
  (timestamps), render-thread CPU (scene and draw list, avatars, probe planning, recording), draws and instances, then
  GPU ms per pass group. Every pass reads as ms a frame, both eyes (tables from before 2026-10-27 give per-eye passes an
  eye). Screenshots of each view go to `local/client/shots/`. The bench always renders on the main thread.
- **Comparing two ways on a shared GPU**: `SOJOURN_AB=NAME` flips a switch every frame and tags each pass `|on`/`|off`
  (`docs/rendering.md` > Measuring on a shared GPU, which lists the switches and their traps); an A/A run differs by under
  0.05 ms. Run-to-run noise otherwise is ~5-10%.
- **Per frame**: `SOJOURN_FRAME_LOG=FILE` (frames.csv: time, frame ms or the headset's display-time step, CPU ms, fence
  wait, probe captures, assets pending, the scene phase's parts, GC collections and pause since the last frame, the
  loop's own CPU before and after the render, the main thread's wait for the world and the wake lead), summarised by
  `tools/vrlog.py` (late frames, those with a GC) and per 15 s window by `tools/perfrun.py`. `SOJOURN_GPU_LOG=FILE`:
  every frame's GPU ms and loading's parts of it (uploads and their bytes, probe captures and faces, impostor captures,
  a few big passes), summarised per window by `tools/gpuarrival.py`. `SOJOURN_PROFILE_MEDIAN=1` reports a window's
  median frame per pass; `SOJOURN_PROFILE_CASCADES=1` times each sun cascade.
- **A session's log** (every 10 s, `logs/session-*.log` in the base dir): `view:` (objects drawn and culled, draws, the
  scene phase's parts: sync, shadow fit, avatars, objects, particles; worn meshes and palettes; the scene budget's
  step), `content:` (lights, projectors, blended faces, particles, triangles an eye and in the shadow maps), the pacing
  line (the costliest passes; water in three parts: `water haze`, `pre-water alpha`, `water`), `assets:` (fetches,
  decodes, mesh queue, `N MB decoded waiting`, memory), `governor:` (below), `gpu:` from `perfrun.py` runs. These are
  what a main-grid session leaves to read afterwards.
- **CPU and memory**: `tools/cputrace.py` profiles the render thread; `SOJOURN_ALLOC_PROFILE=1` logs allocation by type
  every 10 s; `tools/alloctrace.py` says where allocations come from (the runtime's allocation ticks with stacks, read
  by `tools/alloctrace/`, a small TraceEvent program); `tools/heapwho.py` takes a gcdump of the running client and says
  what holds the heap.
- **The performance grid**: the local grid never had content like SL's, so `tools/forest.py` builds one (our own
  content): four regions of 27.8k objects (~420 mesh trees and ~2500 mesh ferns a region with alpha-masked leaf cards,
  grass cards, rocks, prim ruins, ~2000 clutter prims each with its own texture) and 8.9k textures encoded as SL's are
  (J2C, 5 levels, LRCP layers, the same bytes per size). It lives on a second grid copy so performance work never
  disturbs the usual one: `SOJOURN_GRID=perf-grid python tools/grid.py start` (ports 9740-9743, account Sojourn Perf).
  Scenes: `forest`; `places` (`tools/forest.py places --load`: four far regions with their own meshes and bump-mapped
  clutter, for `tools/teleports.py`, which teleports round them and times each stop); `crowd` and `forestcrowdfull`
  (`tools/crowd.py`, Crowds below). `python tools/perfrun.py LABEL [--cold] [--headset-size | --vr-sim] [--dll DIR]
  [--look X/Y/Z] [--set name=value] [--say "..."]` runs a timed session there. Close-ups of particular costs are
  `tools/render_scenes.py` scenes on the usual grid (flowers, club, shore, mist, particles, statue, flexi: `docs/rendering.md`
  > Test scenes).
- **Making the local grid behave like a busy SL place**: `SOJOURN_FETCH_LATENCY_MS` / `SOJOURN_FETCH_KBPS` give asset
  fetches a far grid's latency and bandwidth; `SOJOURN_TEX_FULL=1` makes every texture want full size (as if inside every
  object), which fills video memory the way busy places do; `SOJOURN_GOVERNOR_PERIOD_MS=N` judges frames against N ms
  (small to make the governor act on the desktop, 1000 to keep it out of a bench); `SOJOURN_VRAM_BUDGET_MB=N` caps the
  video memory budget.
- **How representative the forest is**: in the headset path it reproduced the first main-grid session's shape closely
  (2026-10-25). It lacks scripts and moving objects, PBR materials, SL's network (latency is simulated for fetches only)
  and SL's object counts in the biggest builds; the real busy places cost 3-4 times our rebuilds of them at the same
  size, for reasons not pinned down (The GPU in busy places). `--vr-sim` runs in Meta's XR Simulator, whose synchronous
  compositing adds ~8-11 ms of "rest" a frame that Link doesn't (Link: 0.3-0.5 ms). Crowds: the system avatar of the
  dense scene and `SOJOURN_AVATAR_CLONES=N` are far lighter than SL's mesh bodies; use `tools/crowd.py`.

## The load governor

`Realtime/LoadGovernor.cs`, `World/TextureStore.cs`. Built 2026-10-27 after a session died at a busy place: textures
had climbed to 5.3 GB with no ceiling, the GPU paged (the sunlight pass 54-79 ms) and Windows reset the driver. The
user's rules (2026-10-27): **nothing already loaded changes how it looks** (no lowering shadows, SSAO, reflections,
lights, LOD or resolution), and **loading may slow down but never stops**. Two exceptions the user approved, both far
away and gradual: the shadow and impostor trims below. Setting `load_governor` (menu > view, on) turns off the frame
side; video memory is governed regardless.

### The frame

Half-second windows of each frame's dearer of GPU time (which includes its uploads and their mip chains, pass
`uploads`; the draw list's own copies are `frame data`) and render-thread CPU, against the display's period (the
headset's, else 60 Hz). A level rises after a second over its threshold (at once only past 3 periods: the GPU paging)
and falls after 4 s under 0.8 of it. Levels change only how fast things load, never what's drawn:

| | level 0 | 1 (over 1.0 periods) | 2 (over 1.4) | 3 (over 1.8) |
|---|---|---|---|---|
| textures uploaded a frame | 12 MB / 1.5 ms | 6 MB / 0.75 ms | 3 MB / 0.4 ms | 1.5 MB / 0.25 ms |
| meshes uploaded a frame | 8 MB / 1 ms | 4 MB / 0.5 ms | 2 MB / 0.3 ms | 1 MB / 0.2 ms |
| face resolving a frame | 2 ms | 1.5 ms | 1 ms | 0.5 ms |
| decode threads, fetches | 4, 8 | 3, 8 | 2, 6 | 1, 4 |
| a texture or mesh over the budget goes | every frame | every frame | every 8th | every 90th |
| impostor captures a frame, their refresh | 4, full | 4, full | 2, half | 1, a quarter |
| stale reflection probes refreshed | as usual | as usual | a tenth as often | a tenth, a face a frame |
| new objects beyond 16 m | | | | held, let in at a rate |

- **Held objects come in at a rate** (the user's rule after MagnetiS, 2026-10-27, where busy places ran 20-30 ms a
  frame steadily and nothing far ever came): 32 a second when the hold starts, doubling every 4 s it lasts (a thousand
  a second after 20 s): a hold that doesn't bring the frame down means the place is heavy, not its loading. Each frame's
  allowance goes to the largest size classes on screen among what the last frame held (`RenderWorld.Admit`); once let
  in, an object is never held again. Only objects never drawn are held, so nothing drawn is taken away.
- **The draw distance doesn't come in for slow frames**: with far new objects held, a cut only took away what was drawn
  past it and kept anything beyond from ever loading (at MagnetiS it sat at 64 of 128 m for a whole session). It comes
  in only for video memory (below).
- **Trims: what's drawn far away, under lag** (the user's approval, 2026-10-27). After 1.5 s of windows over 1.05
  periods, a step every window while still over: full-detail shadows reach less far (the last sun cascade's far end,
  4% a step, down to half and never nearer than just past the third cascade, `ShadowSettings.FarReach`; map sizes and
  the person's settings don't change, and the coarse far cascade carries shadows on beyond, so the trim moves where
  shadows turn softer rather than where they end: `docs/rendering.md` > Where shadows end), and avatars beyond a
  distance become impostors (from 48 m in, 2 m a step, never nearer than 20 m; each avatar with the crowd's 15% band, so
  nobody flips at the boundary; not those set "always", nor with `full_avatars` 0). They come back after 5 s under 0.85
  periods at half that speed, and for a minute after the last cut no further than a step past where it stopped
  lagging. `SOJOURN_SHADOW_REACH=F` holds the shadow trim at F. What the shadow trim buys: nothing in the forest
  (cascade 3 0.54-0.81 ms a drawing at x1 and x0.5 alike, 2026-10-29); SL's busy places, with far casters by the
  million, are unmeasured, as is what the impostor trim buys with a real crowd.
- **No oscillation** (the user's headset test, 2026-10-27: jitter was worse at medium lag than heavy, frames on time and
  late by turns): hence the levels' hysteresis above, the held objects' smoothly rising rate, the trims' slow return,
  and the shadow cascades fitting the person's draw distance whatever the memory cap does.
- **Approved but not built**: slowing far particles' and far texture animations' updates (particles are generated from
  their history each frame, so a slower rate would show as stepping and save little), far objects' scene updates.

### Loading in the GPU's spare time

Since 2026-10-28 (`BeginLoading`, `UploadBudget`, `MayForce`, `ProbeFaces`): each frame's timestamps say what the scene
costs without loading (passes `uploads`, `probes`, `impostor captures`; `PassProfiler` hands each frame's parts to the
governor as a `GpuFrame`), the estimate rising at once and falling slowly. Loading may spend what 90% of the period
leaves after it, never less than 2% of the period (0.22 ms at 90 Hz: loading slows but never stops), at most 2 ms, with
up to 1 ms saved up. Uploads take it first (their byte budgets the lesser of the level's and that); a texture or mesh
over the budget goes on the level's turn only once 0.75 ms is saved up; probe faces get what's left (a capture in
progress waits a frame when no face fits, never more than seven in a row). The costs (bytes per GPU ms, a probe face's
ms) are learnt from the timestamps. The levels still cap everything, and streaming's first-look sizes still plan by the
level's budget. `SOJOURN_GPU_PACING=0` for a before. The 10 s line says `loading on the GPU X ms a frame (scene S, spare
P; uploads N MB a ms, a probe face F ms)`.

Why: at a heavy place it can't help, near the budget it's the difference. At Mirromere (2026-10-28: 18-40 ms a frame
for 12 minutes) loading's GPU work was a few percent of the frame (uploads under ~0.7 ms, probe captures 0.7-1.0 ms now
and then), so holding it back can't bring such a place near its budget: the scene is what costs. In the forest arriving
cold (scene ~10.5 of the 11.1 ms, full Link size, shadows 3072, `SOJOURN_GOVERNOR_PERIOD_MS=11.1`), probe captures took
0.5-0.8 ms a frame for 30-40 s and put 60% of arrival frames over (uploads: ~15 MB per GPU ms with their mips, 0.01 ms a
frame on average there). Same build, pacing off / on (`local/perf/f3-*`, `h1-*`):

| | off | on |
|---|---|---|
| frames over 11.1 ms while probes were captured | 1641 of 2684 (61%) | 512 of 5611 (9%) |
| GPU frame median / p95 then | 11.0-11.2 / 11.5-11.7 ms | 10.4-10.8 / 11.1-11.3 ms |
| textures and meshes in view loaded (pending under 20) | 9.7 s | 9.9 s |
| reflection probes all captured | 36 s | 71 s |
| as if twice over budget (period 5.5 ms): loading's GPU a frame, frame median, probes done | 0.35-0.54 ms, 10.6-11.1 ms, 49 s | 0.22 ms, 10.6-10.9 ms, 73 s |

Floors of 1.4% and 4% of the period bracketed the choice (probes not done after 90 s; 866 frames over).

**Single submissions stay bounded**: every frame's loading work is in its own command buffer under these budgets; the
biggest single item is one texture or mesh over the budget (a 2048 bake, 16 MB with mips, ~1 ms of GPU); texture
shrinks are 48 GPU-side copies a frame; probe captures a slice of faces (whole captures only for the default probe);
the 64 MB staging ring submits and waits when full rather than growing a frame. Nothing comes near Windows' 2 s
timeout.

### Video memory

Always on: it's what keeps the driver from paging. Four times a second, VK_EXT_memory_budget: the process's
device-local use against what Windows budgets it, less the free space inside our own allocator's blocks. The budget
moves with other processes' use of the card (before a reset on 2026-10-28 it dipped 10.9 -> 8.7-9.0 GB for minutes),
so the target follows it: the budget less 1 GB or 10% (the headset's compositor and Link's encoder are other
processes; a new set of render targets lands between two looks).
- **Within 512 MB of the target** textures stop growing: no sharper copies, first looks a level down, and what's been
  out of view 10 s comes down three levels (2 s once over the target).
- **Over the target** a pressure 1-13 rises a step every 250 ms (two past the budget itself), and textures on screen
  come down by ceil((p - s) / 2) levels, at most 3, where s = log4(screen area / 256 px): the smallest on screen first,
  the largest (nearer than their radius) last; what's been unseen 5 s is evicted, 512 MB a pass. Shrinking is a GPU copy
  of the image's own smaller mips into a smaller image (no decode, memory back in three frames, 48 a frame). Geometry idle
  10-30 s and idle render targets go sooner while memory is short.
- **Easing**: a step only 20 s after the pressure last rose and once a GB under the target, then every 4 s; sharper
  copies come back through the scheduler from disk. So textures don't go sharp, soft and sharp.
- **The draw distance, as a last resort**: if the pressure is at its top and still over for 1.5 s, the draw distance
  comes in from its far end, 4 m every half second, to no less than half the person's (48 m at least), and goes back out
  after 5 s with textures free to grow (768 MB under the target), 2 m a half second, for a minute after its last cut no
  further than 4 m past where it stopped. Objects already in reach stay out to 6 m past the cap, in view or not; new
  ones come in only within it (`ViewSetup.NewDistance`, `SceneObject.InReach`).
- Measured (2026-10-27, `places` and `tools/teleports.py`, 6 stops of 40 s, two eyes of 2448x2672, shadows 3072,
  `SOJOURN_TEX_FULL=1`; process memory from Windows' GPU Process Memory counter): ungoverned (`SOJOURN_VRAM_GOVERNOR=0`),
  1.2 GB at login and 5.06 GB 100 s in, still climbing; governed against a 3 GB test budget (target 2.6), at most 2.86 GB
  over the whole route, pressure at most 3; against 2 GB, at most 1.95.

### Texture slots

Since 2026-10-27, after a headset session crashed four teleports in on `bindless texture table full` (17k textures
known, nothing ever evicted, since memory was within budget). The bindless table is as big as the device allows up to
65535 (slots reach shaders as 16 bits of a push constant, 0xFFFF meaning none), said at start (`video memory: ...; 65535
texture slots`). World textures ask with `Bindless.TryAllocTexture`, leaving 1024 for the renderer's own images; with
none free an upload or shrink goes back in its queue and waits, never throws. The cube table can't throw either: a
probe that can't get two cube slots reuses the longest-unused resident probe's maps, or waits.

Every texture has two times: last drawn (`LastUsed`) and last near (`NearAt`: part of something within a view's draw
distance that wasn't drawn: culled by direction only, avatars behind you with their bakes and attachments, impostors
out of view, worn things between captures, your own HUDs and head attachments, projectors and Linden plants out of
view; touched every 10 s at most). `TextureStore.Reclaim`, under the face resolvers' write lock: every 2 s, textures
neither drawn nor near for 3 minutes are let go (left behind by a teleport, beyond draw distance), 256 a pass, so a
place left behind drains over a minute; every frame, if fewer than a sixteenth of the world textures' share is free,
the longest unseen not drawn in the last 2 s go until an eighth is, far and left behind first, what's only behind you
last. Eviction for memory goes in the same order. A let-go texture's image and slot are retired like a replaced one
(freed three frames on) and it comes back through the scheduler from disk (bakes from their source) when next wanted.
For tests: `SOJOURN_TEXTURE_SLOTS=N` (at least 2048), `SOJOURN_TEXTURE_IDLE_MS=N` (at least 10 s).

### The 10 s line

`governor:` the frame's cost against the period (and the worst window), the level and what it does, the draw distance
against the person's (and the least), new objects held and let in with the rate, the trims (`full-detail shadows reach
x0.72 (90 m of 125; least x0.60), coarse beyond`, `avatars past 30 m as impostors (nearest 24 m)`), video memory in use /
budget / target with textures, geometry, render targets, probes and our allocator's use, whether textures are held,
the pressure, textures shrunk (MB), evicted and sharper copies dropped, `texture slots U of M (most N), K unseen 180 s
let go (MB), J let go for slots, uploads waited for a slot W times`, and loading on the GPU (above). A low slot table
also logs `texture slots low: ...` (at most every 5 s). At start: `video memory: ... MB, this process budgeted ... MB`.

## Memory over a long session

A sitting on 2026-10-28 grew until the PC had 2 GB free (the managed heap 2.9 -> 10.4 GB, doubling in 22 minutes at one
place, 131k texture decodes for 17k textures, the governor at level 2-3 throughout). Causes and what's there now, mostly
`World/TextureStore.cs`:
- **Decoded pixels waiting for upload are capped.** Under lag every texture of a busy place sat in memory as RGBA behind
  the throttled uploads, fragmenting the large object heap. Streamed decodes now stop at 256 MB waiting
  (`SOJOURN_DECODED_CAP_MB`) for anything bigger than a frame's upload budget, and at twice that for everything; at most
  16 fetched textures wait for a decode thread. Small ones a frame's budget takes still go past the cap (a cap full of
  big ones had left every small one waiting). A texture with nothing on screen whose wanted size doesn't fit a frame's
  budget comes in first at a level a quarter of it takes (256x256 at level 3), from its own queue after the bakes and
  ahead of sharper copies, without the forced turn, then sharpens as usual (`TextureStore.StreamLevel`): otherwise a big
  texture new to a place stayed grey for minutes (rain walls at Missing Melody, 2026-10-28).
- **No second decode while one waits for its upload**, unless video memory wants it smaller than what's waiting (the
  scheduler had decoded such textures again every pass: 5699 decodes for 1949 textures in the reproduction, 1736 for
  1985 after).
- **Let go with the image**: an evicted texture's CPU-side data (a bake's morph mask, a sculpt map's pixels, a waiting
  decode) and its dictionary entry. Avatars' morph-mask cache lets go a minute after the wearer was last drawn; a bake's
  morph-mask channel is sampled once for the seven masked morphs and dropped.
- **Pools don't hold left regions**: DrawPlan's pooled parts and Avatars' pooled entries let go of their scenes after
  each frame (a gcdump had 9 RegionScenes for 5 regions: left regions, 27k objects, held only by pools).
- Also let go now: geometry built under lag but not yet uploaded, when idle (~250 MB uploaded for nobody after a
  teleport); the particle tracker's entries with their region; animations after 10 minutes unplayed, or past a minute
  while over 64 MB. Deliberate caches: mesh bytes (256 MB, oldest out), decoded pixels waiting (the caps above).
- The console's memory part tells live from fragmented: `memory WS MB (heap H MB, F fragmented, C committed; last full
  collection left gen2 G, large L)`; the assets part says `N MB decoded waiting`.

The reproduction: `places` with `SOJOURN_TEX_FULL=1 SOJOURN_GOVERNOR_PERIOD_MS=2 SOJOURN_VRAM_BUDGET_MB=4000
SOJOURN_FETCH_LATENCY_MS=120`, two Quest-sized eyes, `tools/teleports.py` (before: the user's build of 2026-10-28):

| | before | now |
|---|---|---|
| managed heap, by minute 4 | 0.3 -> 3.0 GB, still climbing (stopped for the PC's memory) | 0.3 -> 0.8 GB, then 0.8-0.9 to minute 22 |
| live in gcdumps | 0.95-1.67 GB at 2-4 min, 1.36 of it decoded pixels | 0.45-0.46 GB at 4-22 min, 0.27 decoded pixels |
| large object heap, fragmented | 2.9 GB, 1.2 fragmented, at 4 min | 0.6-0.7 GB, 0.1-0.3 fragmented |
| process private bytes | 1.7 -> 6.6 GB at 4 min | 4.6-5.0 GB from minute 4 to 22 |

At ordinary settings the caps never bind (0 MB decoded waiting at every line) and teleport loading times matched the
old build's within noise. On the main grid: the memory line should stay level at a busy place under lag; if it
doesn't, `tools/heapwho.py` some minutes into the growth says what holds it.

## Threads

Since 2026-10-27 (the user: no good reason for work to be on the render thread). How the frame is laid out:
`docs/client.md` > How it fits together, `docs/vr.md` > How it fits together.

### A render thread of its own

`Sojourn.Client/FramePipe.cs`: the main thread simulates frame N+1 (input, the rig, panels, picks, xrWaitFrame) while
the render thread records, submits and presents N (in the headset xrBeginFrame/xrEndFrame with the poses N was located
with). The world is handed over in order (a frame owns it until its scene is built), so nothing sees it half-updated;
outside readers check it's their owner's turn and log offenders. `SOJOURN_RENDER_THREAD=0` for comparing,
`SOJOURN_RENDER_STRESS=1` jitters the hand-overs. The render thread runs above normal priority.

### Persistent instances and materials

`Frame/PersistentTable.cs`: world objects drawn in their own region's frame keep their faces' instances and materials
on the GPU, written when first drawn, moved or changed; a frame adds only their draws. `SOJOURN_PERSISTENT=0` for
comparing.

### Parallel draw lists

`World/DrawPlan.cs`, `RegionScene.Parallel.cs`, `Realtime/SceneWorkers.cs`: three scene workers and the render thread
draw each region's lights, runs of 16 m columns and wearers' attachments into segments, appended in order with indices
moved along (the same draws in the same order); probe captures' lists too. Per-build scratch is a context
(`World/DrawCtx.cs`); Avatars' palettes are per draw list. `SOJOURN_SCENE_THREADS=N`.

### Streaming off the render thread

Objects' faces are resolved by two face resolvers (`World/FaceResolver.cs`, nearest first; texture slot changes
write-locked against them) while the old faces are drawn for at most two frames after they were last current (retired
textures live two frames longer to match); region changes are applied in a 1.5 ms batch a frame. Texture and mesh
uploads stay on the render thread under their budgets: the frame's "rest" (uploads, acquiring) measured p99 0.3-0.5 ms,
so a transfer queue wasn't worth its complexity, and GPU-driven culling isn't needed at these numbers either.

Numbers (2026-10-27, perf grid, two Quest-sized eyes on the desktop, render-thread CPU a frame; the GPU was shared, so
frame times aren't comparable; `local/perf/cpu-*`):

| | before | after |
|---|---|---|
| crowd (`forestcrowdfull`, 40 dancing NPCs, all whole), median (p95, p99) | 4.1 / 3.6 (5.6 / 5.2, 6.9 / 6.7) | 3.3 / 2.8 (4.3 / 3.8, 5.4 / 5.1) |
| its scene / objects | 2.9 / 2.5, 1.9 / 1.7 | 2.0 / 1.7, 0.9 / 1.4 |
| forest arriving cold (120 ms, 30 Mbit/s), first minute: p95, p99 / scene p99 | 2.8, 5.3 / 3.0 | 2.0, 3.2 / 1.4 |
| the same once loaded: p95, p99 | 1.9, 2.5 | 1.3, 1.6 |

The main thread's own ~0.5 ms now overlaps the render thread's. Pictures match the old build's (0.7/255 on average,
leaves and water moving). Checked for races with the world-owner checks, stress mode and a fresh review of the
threading (its findings fixed). On SL in the headset at Tempura (2026-10-28) the render thread's CPU was 5-8 ms a frame
(Where the frame goes).

## Busy places: the CPU

The first main-grid sessions (2026-10-25) were CPU-bound: the render thread's scene phase took 12-58 ms and probe
planning 4-14, growing with the objects in range and with assets still loading, and the render thread allocated
~400 MB/s. What's there now because of it:
- **An object index** (`RegionScene.Index.cs`): placements follow the region's change reports (`RegionSource.Changes`),
  objects sit in 16 m columns culled whole (draw distance, frustum, which shadow maps reach them), lights and
  attachments are lists of their own, and probe scans and particle tracking follow the same reports; a few objects a
  frame are rechecked as a safety net. Before, sync, attachment indexing, shadow extents, projectors, the draw list,
  probe scans and particle tracking each walked every object every frame. Moving prims are placed every frame only
  while they move (`docs/vr.md` > The rig > Moving things).
- **Face caches that hold waiting faces**, valid until a texture changes state or a material arrives; materials tell
  "on its way" from "failed" (retried after 30 s). Worn objects cache too, keyed by the wearer's bakes, and
  `Avatars.WornState` runs when what's worn changes rather than every frame.
- **The scene budget** (`Realtime/SceneBudget.cs`, `Budget` on the renderer): twice a second the scene phase's mean
  decides; over 5 ms, objects under a few pixels of radius on screen stop casting shadows (2.5x the draw threshold),
  then stop being drawn, a step at a time (0.6 to 6 px); under three quarters of the budget for 2 s it steps back. Worn
  meshes go by their wearer's size, their shadows first (at 4x). A band stops objects at the threshold popping
  (`docs/rendering.md` > The scene budget's band). The view line says when it's stepped in. Its avatars side is left
  to the governor's impostor trim (2026-10-29: a second trigger would make more avatars impostors than the user agreed to).
- **Time budgets for resolving and uploads** (the governor's table), with an out-of-date object drawn as it was for up
  to two frames and new objects over 16 m away waiting a frame, so near ones resolve first.
- **Probe captures**: pooled draw lists, rebuilt at a capture's start and halfway rather than every frame; objects under
  a pixel of a 256-pixel face left out; one capture at a time, two of its twelve face renders a frame (a whole capture
  had added ~1-4 ms to one frame).
- **Terrain** surfaces (10-17 ms) and patch meshes (8-28 ms) build on the thread pool, the last one drawn meanwhile
  (`World/Terrain.cs`): they had dropped frames while walking.
- **Particles** carry on from the last frame; bouncing and homing ones were integrated from birth every frame
  (`render_scenes.py particles`: 7.4-10.6 -> 1.1-2.1 ms a frame, 2026-10-26).
- **Decoding** by OpenJPEG (native, `tools/openjpeg.py`): 1.7x faster than CoreJ2K on full decodes, a tenth of the
  garbage. Downloads stream by byte range, largest on screen first (`docs/client.md` > Assets).
- Smaller: the draws' sort by geometry arena is a counting pass; the region's simulator (a lock shared with the network
  threads) and the LOD radius aren't looked up per object per frame.

The forest arriving cold with 120 ms latency at 30 Mbit/s (2026-10-25, two Quest-sized eyes on the desktop): frames over
10 ms of CPU 1032-1043 -> 57, CPU p95 10-19 -> 6.7-7.2 ms, 120-127 GB allocated -> 7.8 GB in 150 s, GC pauses 2.0 -> 0.7 s;
everything loaded at the same time (~105-120 s: the network decides). Profiled on the forest, objects cost ~0.3 us
each against ~1.2 in Mirromere (2026-10-26), so the synthetic scene doesn't show what makes SL's objects dear (faces per
object, PBR overrides?); `tools/cputrace.py` in a busy main-grid place would.

### Garbage

A session on 2026-10-26 had a quarter of its late frames with every phase inflated together, which is what collections
look like: the process allocated ~500 MB/s. The cause was **worn meshes' joint palettes**: every rigged mesh got a
fresh `Matrix4x4[]` each frame, and many SL creators' skins list ~110 joints whatever the mesh uses (~7 KB and 110
matrix products a worn mesh a frame; an SL avatar wears 100+). Now a palette is per avatar and skin content
(`SkinShare`: joint map and inverse binds, shared by a body's pieces, its clothes and every LOD), written straight into
the draw list; the view line says `worn meshes N a frame, M palettes, K shapes built`. `tools/crowd.py build
--full-skins` makes SL-like skins (scene `forestcrowdfull`); our first crowd listed only the joints used and never
showed it. Measured 2026-10-27 (40 dancing NPCs in full skins, all whole, two Quest-sized eyes, the machine idle):

| | before | after |
|---|---|---|
| allocated | 448 MB/s (Matrix4x4[] 78%) | 15 MB/s |
| gen0 collections, GC pause | ~23/s, ~14 ms a second | 0.9/s, 0.4 ms a second |
| CPU a frame, median (p95) | 4.8 (5.6) ms | 3.5 (4.2) ms |
| worn meshes, palettes a frame | 520, 520 | 520, 40 |

Also off the render thread's garbage since: a `FaceGeometry` per rigged face per frame, the animator's per-joint arrays,
the probe list's grid, the overlay's vertex list, ray candidates' copies, the fixed sky's LLSD parsed every frame;
texture decodes take their buffers from the shared array pool. Body shapes rebuild for morph-only changes (an
expression fading in) at most 10 times a second an avatar and once a frame overall. Geometry builds make faces' boxes
and pick copies on their own threads. The collector runs in SustainedLowLatency.

## The GPU in busy places

Measured in the headset on 2026-10-27 (GPU ms a frame, then the costliest passes, ms an eye unless "a frame"; full Link
size, shadow cap 1536; before light tiles, the alpha paths and simpler see-through lighting):

| place | GPU | lights / projectors in view | costliest |
|---|---|---|---|
| Mirromere | 22.2 | 100-160 / 35-64 | alpha 2.2, projectors 1.4, probes 1.4 a frame, shadows 1.3 a frame, local lights 1.1, ssao blur 1.0 |
| Calas Galadhon | 22.6 | ~117 / ~52 | projectors 2.1, alpha 1.7, local lights 1.7, shadows 1.2 a frame, probes 0.9 a frame |
| Envision (club) | 23.4 | ~116 / ~55 | projectors 3.3, local lights 1.8, shadows 1.2 a frame, gbuffer 1.0 |
| calm regions | 8.4-9.0 | 10-30 / 0 | ssao blur 0.8-0.9, sunlight 0.6 |

On 2026-10-28 at Mirromere, with the user's heavier settings (full size, shadows 3072), alpha took 4-12 ms and water
3-13 ms a frame, rising together: the water pass included the pool of blended faces drawn before it.

What was done, with what each measured (details in `docs/rendering.md`, by section):
- **Light tiles** (2026-10-27, > Light tiles): local lights and projectors loop over their 16x16 tile's lights, and
  leave tiles none of theirs reaches. `render_scenes.py club` (100 point lights, 48 projectors): local lights 3.99 ->
  2.54 ms an eye, projectors 3.66 -> 0.64, tiles 0.35; frame 24 -> 15 ms. A club is the hard case; spread-out places
  gain more. Folding them into the sun and ambient pass was slower where rooms are full of light.
- **Blended faces** (> The alpha shader, > See-through surfaces, > Small, invisible savings): the sun shadow only for
  fragments that show, the viewer's discards on every path (12 flowered panes: 1.72 -> 1.30 ms an eye); `alpha.frag`
  compiled per material path (flower field 7.5 -> 5.6 ms a frame); **simpler lighting** for blended faces, on by default
  since the user chose it (2026-10-27, Luane's World: no difference they could see; the blended pass 11-17 -> 4.4-5 ms,
  the GPU frame ~29-38 -> ~21-23; in the test scenes -70% of the pass). The layer cap (`alpha_layers`) saved nothing in
  the tested scenes and costs its counting pass, so it's off.
- **The pre-water pool** (2026-10-29, > Blended or masked): drawn per material path, judged by each face's own box rather
  than its object's sphere, and lit as `simple_see_through` says. `render_scenes.py shore` (a bank of blended grass
  sloping into the sea; views in `tools/shore-views.txt`; two eyes 2448x2672, shadows 3072), GPU ms a frame for the bank,
  low, above and under views: the water pass 3.9 / 8.6 / 4.2 / 8.9 -> about 1.3 / 2.6 / 1.8 / 2.3, the frame 17.0 / 20.0 /
  17.0 / 24.1 -> 15.2 / 16.0 / 15.3 / 19.7 before the lighting change. Pictures identical above water.
- **Probe captures' sun shadow** drawn once a capture pass and copied back on its later frames (2026-10-29): captures
  0.55-0.61 -> 0.30-0.46 ms a frame while the forest fills in. Captures also take only the GPU's spare time (the
  governor).
- **Shadows**: the cap (the user's 3072); casters that cut nothing drawn depth alone; the far cascades kept from the frame
  before while the view holds still (-0.45-0.78 ms at 3072); a coarse far cascade so shadows don't end in a ring
  (~0.02 ms a frame); the governor's trim under lag.
- **The lenses' hidden area** not shaded (-0.3 to -1.2 ms a frame with a test lens hiding 19%).
- **Foveated rendering** took a third off the fullscreen passes (flower field 11.1 -> 8.0 ms) and was removed: too
  visible at the edges of view (the user's call, 2026-10-27). Not to be offered again in that form.
- **Detail levels** in the headset follow the viewer's default camera angle rather than the lenses' (`docs/rendering.md`
  > The headset's near plane and detail levels), which puts more triangles in view in busy places: deliberate.

What's left: Mirromere at the 2026-10-29 build is estimated, not measured (the water pass at a third to a half; still
over 11.1 ms: the post-water alpha, shadows and the lighting chain remain; a check is on the board). The real places
cost 3-4 times our rebuilds of them at the same size (flower field 4.5 ms at x0.6 against 11-18 ms in Mirromere and the
café, 2026-10-26), and which part (more blended layers, lights, mesh triangles in four cascades, probes) isn't known;
the `content:` line next to the pacing line in those places would say. The remaining levers change the look and are
the person's: resolution auto, Link at 72 Hz, the half-size light map (`half_ssao`, -1.3 to -1.6 ms; the user kept full
size as the default, 2026-10-28).

Not looked into: half rate with the GPU to spare. In a garden near Mirromere (2026-10-31) the display stepped at half
rate in 20-97% of frames while the GPU passes summed to ~7-10 ms and the GPU sat idle 2.4-7.6 ms between frames, so
something other than the GPU's work held those frames; the overlap's hand-over (`docs/vr.md` > Overlap; the pacing
line's `idle between frames`) is the first place to look.

## A long session's loading

A 67-minute session with 13 teleports (2026-10-27) loaded places more and more slowly: meshes didn't keep up (all 8
fetches busy for the last 20 minutes with thousands waiting). What's there now (`docs/client.md` > Assets):
- Meshes have connections and a queue of their own; LibreMetaverse's single FIFO of 8, never cancelled, had every legacy
  bump-mapped face's diffuse texture, bakes and sounds ahead of them, including places left behind. Bump maps are on the
  texture scheduler.
- Mesh bytes are held only up to 256 MB, and wants nobody asked about for a minute are dropped.
- LibreMetaverse's cache holds 4 GB (it was 1 GB: after an hour only the last ~10 minutes were on disk).
- The texture scheduler and eviction no longer copy the whole texture dictionary (35k entries) 50 times a second.

The reproduction (`places`, `tools/teleports.py`, 13 teleports at 70 s a stop, cold cache, 120 ms and 30 Mbit/s): seconds
until pending was under 20 on each first visit went from 52-70+ and rising place by place to 32-36 and level. The
local grid's single OpenSim serves ~0.2-1.7 MB/s, so it says more about order and queueing than about SL's speed.

## Crowds

`World/Avatars.Crowd.cs`, `Frame/Renderer.Impostors.cs`, `Frame/ImpostorAtlas.cs`, `Shaders/impostor.*`. Since
2026-10-26.
- **Who's drawn how**: the nearest `full_avatars` (16, the viewer's High-Ultra) whole, and anyone within 2.5 m; the
  rest impostors; out of view beyond the count, nothing (impostors cast no shadow). Over `avatar_complexity` (350,000)
  or 1000 m² of attachments, a jelly doll (the system body in the viewer's colours, no attachments, no worn particles);
  "never" a grey one. The person's own avatar is never limited. Beyond the governor's distance trim, impostors whatever
  the count. `SojournAvatars list|render|inspect`, settings menu > view. On first sight an avatar is a cloud
  ("loading") until the attachments its appearance lists are in (`docs/rendering.md` > Avatars' faces against the viewer).
- **Complexity** is the viewer's ARC (getRenderCost, texture costs, 200 a bake), with meshes judged from their headers
  (`GeometryCache.MeshCost` fetches a mesh's data without building it). Until every worn mesh's header is known an
  avatar is a cloud ("checking"), so a crasher attachment is never decoded or drawn once over the limit. A checked
  avatar's mesh fetches count 4x its screen area. Your own is judged too (shown, never limited).
- **Worn rigged meshes** take the wearer's LOD (calcLOD: the avatar's distance and animated extents) as soon as the mesh
  header says rigged.
- **Impostors**: a capture draws the avatar once, with the G-buffer pipelines, into a tile of an atlas in the G-buffer's
  formats (16 tiles 256x512, 32 of 128x256, 128 of 64x128; 117 MB, made on first use), from an orthographic box along the
  line from the camera whose view is a translation only (world normals). In the scene its box is drawn back faces only
  and `impostor.frag` marches each eye's ray through the tile's depth to the surface, writing that texel's G-buffer and
  depth: each eye sees the surfaces at their own depth (no card), and lights, shadows received and SSAO treat it as
  geometry. Up to 4 captures a frame by staleness (fewer under lag: the governor's table): 4-30 times a second by size on
  screen, at once past 2 degrees of view change or 15% of distance. Between captures only the root moves.
- **The test crowd**: `tools/crowd.py` (scene `crowd`): OpenSim NPCs in three outfits of our own rigged mesh (~230k
  triangles at high LOD, ~90 prims each with jewellery, bakes-on-mesh skin, blended hair; ARC 400-470k, so jelly dolls at
  the default limit), dancing; `--say "spawn 40 7 1.5"` etc. through `tools/perfrun.py`.
- **Numbers** (2026-10-26, two Quest-sized eyes on the desktop, complexity limit off): 80 avatars all whole: CPU median
  10 ms (p95 14), scene 7, GPU 14.6 ms (alpha 2.3, shadows 2.3, skinning 1.45); with 16 whole and the rest impostors: CPU
  median 4.9-5.6 (p95 7.6-9.2), scene 3.6-4.0. The GPU after wasn't measured cleanly (another session filled the VRAM).
  A real SL crowd with impostors hasn't been measured.

## Toward ~11 ms for two eyes (90 Hz)

The plan from 2026-10-21, when the dense scene took 14 ms at full Link size and real SL places were known to be heavier.
Where each item stands (2026-10-31):

1. **Hitches on arrival and while moving: done.** The scene budget, resolve and upload time budgets, pooled capture
   lists, sliced probe captures, terrain off the thread, loading in the GPU's spare time, and the threads (Busy places:
   the CPU; Threads).
2. **Fold the lighting chain: done as far as it pays.** Light tiles for local lights and projectors (with early outs:
   -0.4 to -0.6 ms a frame in most views). One pass for everything was slower on this card (club 8.6 -> 10.7 ms).
   Half-resolution SSAO is built as `half_ssao`, off: the user could hardly tell but kept full size as the default
   (2026-10-28).
3. **Shadows under 1 ms: partly.** Done: the cap, depth-only casters, far cascades kept while still, the coarse far
   cascade, the governor's trim. Built as options and left off by the user's verdict (2026-10-28): far cascades fitted
   larger so they hold while moving (`far_shadow_margin`; 0.1 took a moving view's shadows 2.17 -> 1.72 ms), small casters
   left out (`min_caster_texels`, nothing gained in the forest). Not built: lower-detail casters (a coarser caster
   shadows its own finer surface wrongly, and needs a second detail level of each mesh). Open: what shadows cost in SL's
   busy places, with far casters by the million.
4. **Multiview: done, saves nothing.** The G-buffer drawn to both eyes at once was 2-3 times slower on the RTX 5070, so
   only the forward and alpha passes are multiview (`docs/rendering.md` > Two eyes).

   4b. **The lenses' hidden area: done**; Meta's real mesh in the headset hasn't been looked at (the console logs each
   eye's triangles when they arrive; the XR Simulator doesn't offer it).

   4c. **See-through surfaces: done** (per path, simpler lighting on by default, the pre-water pool; the layer cap off).
5. **Fixed foveated rendering: removed** (the user, 2026-10-27: too visible at the edges of view).
6. **Avatars: done** (Crowds). Open: measuring a real SL crowd.
7. **CPU at SL scale: done** (the index, persistent instances and materials, parallel draw lists, particles);
   GPU-driven culling not needed at these numbers. Open: `tools/cputrace.py` in a busy main-grid place, since our forest
   doesn't show what makes SL's objects dear.
8. **Small ones**: the G-buffer's clears are gone; precise barriers are behind `SOJOURN_PRECISE_BARRIERS=1`, off (not
   measurable under the shared GPU's load); narrow G-buffer normals built as `narrow_gbuffer`, off (-0.02 to -0.05 ms;
   a narrow emissive target banded the sky).

What would still buy frames at busy places is in The GPU in busy places > What's left.
