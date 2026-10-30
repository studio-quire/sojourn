# Frame time and the VR budget

Written by Claude Opus 5.5, 2026-10-20 (busy places: 2026-10-25). Agent-facing. Measured on the RTX 5070 (12 GB)
and Ryzen 5 9600X, Release build, vsync off.

## Arriving at a heavy place: loading in the GPU's spare time (2026-10-28)

Written by Claude Opus 5.5, after two GPU resets soon after arriving at Mirromere (`docs/session-2026-10-28e.md`
items 7, 14, 17; the resets are a card/driver fault under long heavy load, the root board).
- **What the GPU did at Mirromere** (the evening's logs, `local/main/logs/session-20261028-*.log`, 10 s pacing lines):
  over its period in every frame for as long as the user stayed, not only while arriving: 18-40 ms a frame from
  21:19 to 21:31, loading level 3 throughout, the shadow and impostor trims at their floors from the first minute. The
  costliest passes were the scene's own: alpha 4-12 ms and water 3-13 ms a frame (the two rising together: water
  includes the pre-water pool of blended faces, drawn with the general alpha shader, not the per-path ones), then
  shadows, ssao blur, gbuffer, sun+ambient 1-2.5 each. Uploads never reached the top eight passes (under ~0.7 ms);
  probe captures did now and then at 0.7-1.0 ms. The second reset (21:31:23) came with nothing loading at all (no
  fetches, nothing waiting, 73 KB/s). Before the first (20:20) Windows' video memory budget for the process had been
  dipping 10.9 -> 8.7-9.0 GB for minutes whenever its use passed ~6.5 GB (others' use of the card), and the last window
  shrank 1.5 GB of textures with the G-buffer at 14 ms a frame (frames 60 ms), which looks like paging. So loading's
  GPU work is a few percent of a heavy place's frame, and holding it back can't bring such a place near its budget;
  what would is the scene (blended faces and water at Mirromere), which is rendering's and the user's call.
- **Where loading's GPU time goes while a place fills in** (the forest on the perf grid, cold, full Link size
  2448x2672 two eyes, shadow cap 3072, judged at 90 Hz with `SOJOURN_GOVERNOR_PERIOD_MS=11.1`): reflection probe
  captures, 0.5-0.8 ms a frame for the 30-40 s they take (two faces a frame, each frame re-rendering the capture's sun
  shadow, about a third of it: the capture shares the main cascade 0 map, which the frame's own shadows overwrite;
  since 2026-10-29 kept and copied back instead, below).
  Uploads: ~15 MB per GPU ms with their mip chains, 0.01 ms a frame on average there (150 MB in all), at most 0.4-0.8 ms
  in a frame at the level 0-1 budgets. Impostor captures nil (no crowd). The forest's scene alone is ~10.5 ms of the
  11.1, so the probes' 0.7 ms put 60% of arrival frames over.
- **Now** (`Realtime/LoadGovernor.cs`, `BeginLoading`, `UploadBudget`, `MayForce`, `ProbeFaces`): each frame's
  timestamps say what the scene costs without loading (pass `uploads`, `probes`, `impostor captures`), the scene's
  estimate rising at once and falling slowly; loading may spend what 90% of the period leaves after it, never less
  than 2% of the period (0.22 ms at 90 Hz: loading slows but never stops), at most 2 ms, with up to 1 ms saved up.
  Uploads take it first (their byte budgets the lesser of the level's and that), a texture or mesh over the budget
  goes on the level's turn only once 0.75 ms is saved up, and probe faces get what's left (a capture in progress waits
  a frame when no face fits, never more than seven in a row). Costs are learnt from the timestamps (`PassProfiler`
  hands each frame's parts to the governor as a `GpuFrame`). The levels still cap everything; streaming's first-look
  sizes still plan by the level's budget, not the frame's share. Off with the frame side (`load_governor`);
  `SOJOURN_GPU_PACING=0` for a before. The 10 s governor line says `loading on the GPU X ms a frame (scene S, spare
  P; uploads N MB a ms, a probe face F ms)`.
- **Numbers** (same build, pacing off / on, the forest arrival above; `tools/gpuarrival.py` on `SOJOURN_GPU_LOG`
  files, `local/perf/f3-*`, `h1-*`):

  | | off | on |
  |---|---|---|
  | frames over 11.1 ms while probes were captured | 1641 of 2684 (61%), 10-40 s | 512 of 5611 (9%), 10-70 s |
  | GPU frame median / p95 then | 11.0-11.2 / 11.5-11.7 ms | 10.4-10.8 / 11.1-11.3 ms |
  | textures and meshes in view loaded (pending under 20) | 9.7 s | 9.9 s |
  | reflection probes all captured (loading's GPU work ends) | 36 s | 71 s |
  | as if twice the budget (period 5.5 ms, as Mirromere; the same 0.22 ms floor): loading's GPU a frame | 0.35-0.54 ms | 0.22 ms |
  | the same: frame median, probes done | 10.6-11.1 ms, 49 s | 10.6-10.9 ms, 73 s |

  Two other off runs and two settings tried on the way (floors 1.4% and 4% of the period: 101 frames over in 10-40 s and
  probes not done after 90 s; 866 over and done at 50 s) bracket it. At a place already over budget the gain is small (~0.3 ms
  of 11) and reflections take ~1.5x as long; near the budget it's the difference between frames alternating on time
  and late for half a minute and holding.
- **Single submissions stay bounded**: every frame's loading work is in its own command buffer under these budgets;
  the biggest single item is one texture or mesh over the budget (a 2048 bake, 16 MB with mips, ~1 ms of GPU);
  texture shrinks are 48 a frame of GPU-side copies; probe captures a slice of faces (whole captures only for the
  default probe); the staging ring (64 MB) submits and waits when full rather than growing a frame. Nothing comes near
  Windows' 2 s timeout.
- **The scene's own cost, taken down** (Claude Opus 5.5, 2026-10-29; both the same picture as before, `docs/rendering.md`
  > Blended or masked, Small savings):
  - *The pre-water alpha pool* (Mirromere's "water"): drawn per material path, and only faces whose own box reaches
    the water's far side. Reproduced by `render_scenes.py shore` (a bank of blended grass linksets sloping into the sea,
    weed below, pads on top; views in `tools/shore-views.txt`), two eyes 2448x2672, shadows 3072, GPU ms a frame:

    | view (bank, low, above, under) | before | after |
    |---|---|---|
    | the frame | 17.0, 20.0, 17.0, 24.1 | 15.2, 16.0, 15.3, 19.7 |
    | the water pass (haze, pre-water pool, copies, surface) | 3.9, 8.6, 4.2, 8.9 | 2.0, 4.6, 2.5, 4.5 |
    | of which the pool | 2.9, 7.1, 3.0, 7.7 | 1.05, 3.0, 1.04, 3.25 |

    Per path did most of it (`SOJOURN_AB=alphapaths`: the pool 2.3/6.9/2.3/7.7 -> 1.05/3.0/1.04/3.25, -55%, more than
    the post-water pool's 30-40% here: the general shader with depth writes); the box cut the bank's pool by a fifth
    (grass linksets whose spheres reached the water) and nothing in the shallows. Pictures: identical to the old build
    above water (mean 0.000), under water within its run-to-run noise.
  - *Probe captures' sun shadow*: drawn on a capture pass's first frame, kept in an image of its own and copied back
    into cascade 0 on the pass's later frames instead of drawn again (`Renderer.KeepCaptureShadow`,
    `FrameRenderer.CaptureShadow`). The forest's cold arrival (`SOJOURN_AB=captureshadow`, frames that captured
    probes, two runs): probe captures 0.55-0.61 -> 0.30-0.46 ms a frame (median 0.51-0.56 -> 0.25-0.34). The first
    run was of a version whose captures sampled a map of their own (-46%, no copy); its probes came out a level
    different here and there (another index into the shadow array compiles to slightly different arithmetic), so the
    copy, which leaves the shaders as they were, went in. Costs one cascade-sized image (37 MB at 3072) once captures run.
- **What Mirromere would show now** (estimated, not measured there): the water pass's pool at a third to a half of
  what it was, so a frame that read alpha 8 + water 8 might read alpha 8 + water 3-4; the pass now reads in three parts
  (`water haze`, `pre-water alpha`, `water`) in the pacing line. Still over 11.1 ms: the post-water alpha, shadows
  and the lighting chain remain.
- *The pool lit as the setting says*: it had been lit in full whatever `simple_see_through` said (the user's
  default is simple), so a face crossing the water changed lighting at the waterline. Now it follows the setting as
  every blended face does (`Renderer.PreWaterPipeline`; `SOJOURN_AB=simplealpha` now flips both pools). The pool on
  the shore views, measured by an A/B of the same pipelines before the switch went: 1.05/3.0/1.04/3.25 ->
  0.37/1.10/0.36/1.08 ms (-65%), so the water pass at about 1.3/2.6/1.8/2.3 against 3.9/8.6/4.2/8.9 at the start.

## Memory over a long session (2026-10-28)

Written by Claude Opus 5.5. The second sitting of 2026-10-28 on SL (01:30-02:12) grew until the PC had 2 GB free:
the managed heap 2.9 -> 10.4 GB, doubling in 22 minutes at one place (Semang), with 131k texture decodes for 17k
textures known; the governor sat at level 2-3 (uploads at a quarter or less) the whole time, as in every main-grid
session. It was older than that night (10-27's 20:17 session reached 4.6 GB). The causes, all in
`World/TextureStore.cs`:
- **Decoding ran ahead of uploads.** Nothing bounded the decoded pixels waiting for upload (`SceneTexture.Pending`,
  RGBA at the decoded level, megabytes each), so under lag every texture of a busy place sat in memory as pixels behind
  the throttled uploads, and their varied sizes left the large object heap fragmented as they went. Now streamed
  decodes stop at 256 MB waiting (`SOJOURN_DECODED_CAP_MB`) for anything bigger than a frame's upload budget, and at
  twice that for everything; at most 16 fetched textures wait for a decode thread (500 were seen waiting with their
  bytes when the governor cut decoding to one thread). The small ones a frame's budget takes still go past the cap: a
  cap full of big ones (one forced upload every 90 frames at level 3) had left every small one waiting behind them.
  That left a big texture new to the place grey for minutes (not decoded past the cap, then a forced turn among dozens):
  Missing Melody's rain walls, 20x40 m faces at alpha 0.5 with a near-transparent streak texture, drawn as grey sheets
  (Claude Opus 5.5, 2026-10-28). A texture with nothing on screen whose wanted size doesn't fit a frame's budget now
  comes in first at a level that takes a quarter of it (256x256 at level 3), uploaded from its own queue after the
  bakes and ahead of sharper copies, without using the forced turn; it's sharpened afterwards as before
  (`TextureStore.StreamLevel`).
- **Decoding again while a decode waited for its upload**: the scheduler saw the texture still unloaded and decoded it
  again every pass (each a file read and a decode), likely much of those 131k (in the reproduction 5699 decodes for 1949
  textures before, 1736 for 1985 now). Now only if video memory wants it
  smaller than what's waiting.
- **Evicted textures kept what was on the CPU** (a bake's morph mask, `Aux`, a megabyte or more; a sculpt map's `Rgba`;
  a waiting decode) for good, the entries never leaving the dictionary: every avatar and sculpt of the session. Now
  let go with the image. Avatars' morph-mask cache (512 kept, for avatars long gone) lets go a minute after the wearer
  was last drawn.
- **Left regions held by pools** (the third sitting, a tour of teleports: 1 -> 5 GB, 4.3 GB live after a full
  collection). A gcdump had 9 RegionScenes, but only 5 were in RenderWorld's regions: the other 4 (27k objects, their
  geometry, face caches and Core's world model) were left regions held only by DrawPlan's pooled parts (a part kept its
  scene until a later frame used as many parts, so after a frame of many, at 256 m or a big place, never) and by
  Avatars' pooled entries. Both now let go after each frame. Scene counts matched Core's RegionWorlds, so Core does drop
  regions on leaving.
- **Also growing with places and people seen**: geometry built under lag waited in the upload queue with all its
  vertices (~250 MB, mostly worn rigged meshes) and was uploaded for nobody after a teleport, as idle eviction skipped
  shapes not yet uploaded (now they go too); every bake's morph-mask channel was kept while resident (~240 MB at a
  busy place, 4 MB a 2048 bake; now sampled for the seven masked morphs once and dropped); the particle tracker kept an
  entry for every object of every region seen (46 MB, 150k; now let go with the region); every animation seen was
  kept (44 MB for ~630; now let go after 10 minutes unplayed, or past a minute while over 64 MB). Deliberate caches
  left as they are: mesh bytes (256 MB, oldest out), decoded pixels waiting (256 / 512 MB caps above).
- The console's memory part now tells live from fragmented: `memory WS MB (heap H MB, F fragmented, C committed; last
  full collection left gen2 G, large L)`, and the assets part `N MB decoded waiting`. `tools/heapwho.py` takes a gcdump
  of the running client and says what holds it (retained by type, big arrays by their holders' types).

The reproduction: the perf grid's `places` scene (`tools/teleports.py`, setup in its docstring) with every texture
wanted at full size and lag forced (`SOJOURN_TEX_FULL=1 SOJOURN_GOVERNOR_PERIOD_MS=2 SOJOURN_VRAM_BUDGET_MB=4000
SOJOURN_FETCH_LATENCY_MS=120`, two Quest-sized eyes). An NPC crowd coming and going, and teleports at ordinary
settings, stayed flat.

| that reproduction | before (the user's build, c480ab0) | now |
|---|---|---|
| managed heap (the console's), by minute 4 | 0.3 -> 3.0 GB, still climbing (stopped for the PC's memory) | 0.3 -> 0.8 GB, then 0.8-0.9 to minute 22 |
| live in gcdumps (heapwho) | 0.95 / 1.67 / 1.61 GB at 2 / 3 / 4 min; 1.36 GB of it decoded pixels in the upload queue | 0.45 / 0.45 / 0.46 GB at 4 / 12 / 22 min; 0.27 GB decoded pixels |
| large object heap, fragmented (dotnet-counters) | 2.9 GB, 1.2 fragmented, at 4 min | 0.6-0.7 GB, 0.1-0.3 fragmented |
| process private bytes | 1.7 -> 6.6 GB at 4 min | 4.6-5.0 GB from minute 4 to 22 (the rest is native: the driver's and ours) |
| at 3.3 min: textures uploaded, decodes | 1568, 5699 | 1723, 1736 |

At ordinary settings the caps never bind (decoded pixels waiting read 0 MB at every 10 s line), and loading is as
before within the runs' noise: `tools/teleports.py --cold --stops 7 --stay 60` with 120 ms and 30 Mbit/s on fetches,
two runs each, seconds until what's in view had loaded (pending under 20) at Place One / Two / Three / Four / One
again: before 27.0 26.6 29.2 28.6 9.0 and 26.0 25.8 27.4 27.3 8.7; now 28.9 28.5 31.4 31.5 8.8
and 26.1 25.8 27.6 27.8 8.7. What a main-grid session can confirm: the memory line staying level at a busy place under
lag; if it doesn't, `tools/heapwho.py` some minutes into the growth says what holds it.

## Threads (2026-10-27)

Written by Claude Opus 5.5 (the user: do what was postponed as big but would help; no good reason for work to be on
the render thread). How it fits: `docs/client.md` > How it fits together, `docs/vr.md` > How it fits together.
- **A render thread of its own** (`Sojourn.Client/FramePipe.cs`): the main thread simulates frame N+1 (input, the rig,
  panels, picks, xrWaitFrame) while the render thread records, submits and presents N (xrBeginFrame/xrEndFrame in the
  headset, with the poses N was located with). The world is handed over in order (a frame owns it until its scene is
  built), so nothing can see it half-updated; outside readers check it's their owner's turn and log offenders.
  `SOJOURN_RENDER_THREAD=0` for comparing, `SOJOURN_RENDER_STRESS=1` jitters the hand-overs.
- **Persistent instances and materials** (`Frame/PersistentTable.cs`): world objects drawn in their own region's frame
  keep their faces' instances and materials on the GPU, written when first drawn, moved or changed; a frame adds only
  their draws. `SOJOURN_PERSISTENT=0` for comparing.
- **Draw lists in parts** (`World/DrawPlan.cs`, `RegionScene.Parallel.cs`, `Realtime/SceneWorkers.cs`): three scene
  workers and the render thread draw each region's lights, runs of 16 m columns and wearers' attachments into segments,
  appended in order with indices moved along (the same draws in the same order); probe captures' lists too. Per-build
  scratch is a context (`World/DrawCtx.cs`); Avatars' palettes are per draw list. `SOJOURN_SCENE_THREADS=N`.
- **Streaming off the frame**: objects' faces are resolved by two face resolvers (`World/FaceResolver.cs`, nearest
  first; texture slot changes write-locked against them) while the old faces are drawn for at most two frames after
  they were last current (retired textures now live two frames longer to match); region changes are applied in a
  1.5 ms batch a frame instead of up to 6000 changes. Texture and mesh uploads stay on the render thread under their
  budgets: the frame's "rest" (uploads, acquiring) measured p99 0.3-0.5 ms, so a transfer queue wasn't worth its
  complexity yet. GPU-driven culling (plan item 7's last step) isn't needed at these numbers either.

Numbers (perf grid, two Quest-sized eyes on the desktop; render-thread CPU a frame; the GPU was shared with another
session's runs at times, so frame times aren't comparable; `local/perf/cpu-*`):

| | before (main, 5bdf6bb) | now |
|---|---|---|
| crowd (`forestcrowdfull`, 40 dancing NPCs in full skins, all whole), median (p95, p99), two runs each | 4.1 / 3.6 (5.6 / 5.2, 6.9 / 6.7) | 3.3 / 2.8 (4.3 / 3.8, 5.4 / 5.1) |
| its scene / objects | 2.9 / 2.5, 1.9 / 1.7 | 2.0 / 1.7, 0.9 / 1.4 |
| forest arriving cold, 120 ms and 30 Mbit/s on fetches, first minute: p95, p99 / scene p99 | 2.8, 5.3 / 3.0 | 2.0, 3.2 / 1.4 |
| the same once loaded: p95, p99 | 1.9, 2.5 | 1.3, 1.6 |

The main thread's own ~0.5 ms a frame now overlaps the render thread's. In the XR Simulator both hold 72/72 once
loaded when the GPU is theirs (the simulator composites inside xrEndFrame, 8-10 ms of the render thread's "rest"). One
cluster of slow frames (every phase inflated together, recording 26 ms: the thread losing its core, not the scene) came
once in the new arrival run, as a 25 ms one did in the old. Pictures: the same view rendered by both builds differs by
0.7/255 on average (leaves and water moving); crowds with impostors look as before.

Checked for races: the world-owner checks logged nothing in any run; stress mode in the simulator and on the desktop
with a walking crowd, impostors captured and cold loading; a fresh review of the threading (its findings fixed: stale
face caches naming retired textures, an in-flight resolve outliving a texture-entry change, the materials' version read
late, blended ties ordered by persistent slot, a missing barrier when the persistent table grows). Not yet seen: a
main-grid session, which should show what's left (the next session's frames.csv has the main thread's wait for the
world and the wake lead as its last columns).

## The load governor and video memory (2026-10-27)

Written by Claude Opus 5.5. The sixth main-grid session died at a Halloween place (Cravone): textures resident had
climbed to 5.3 GB, the GPU went from ~23 ms a frame to 85, then 233-270 (the sunlight pass alone 54-79 ms, ssao blur
14-15: passes that read many large images, as paging makes them), and 13:18:25 ended in `vkWaitForFences failed:
ErrorDeviceLost`, Windows resetting the driver. Textures were RGBA8 with full mips, held at whatever level their
screen area wanted with no ceiling: the 1.5 GB "budget" only evicted what nobody had looked at for 5 s. Now
(`Realtime/LoadGovernor.cs`, `World/TextureStore.cs`; the user's rule, 2026-10-27: nothing already loaded changes how it
looks, so no lowering shadows, SSAO, reflections, lights, LOD or resolution):
- **Video memory, always on.** Four times a second, VK_EXT_memory_budget: the process's device-local use against what
  Windows budgets it, less the free space inside our own allocator's blocks. The target is the budget less 1 GB or 10%
  (the headset's compositor and Link's encoder are other processes; a new set of render targets lands between two
  looks). Within 512 MB of the target, textures stop growing: no sharper copies, first looks a level down, and what's
  been out of view 10 s comes down three levels (2 s once over the target). Over the target, a pressure 1-13 rises a step every 250 ms (two past
  the budget itself) and textures on screen come down by ceil((p - s) / 2) levels, at most 3, where s = log4(screen
  area / 256 px): the smallest on screen first, the largest (nearer than their radius) last; what's been unseen 5 s is
  evicted, 512 MB a pass. Shrinking is a GPU copy of the image's own smaller mips into a smaller image (no decode,
  memory back in three frames, 48 a frame). Pressure eases a step only 20 s after it last rose, once a GB under the
  target, then every 4 s; sharper copies come back through the scheduler from disk. Geometry idle 10-30 s and idle
  render targets go sooner while memory is short. If the pressure is at its top and still over for 1.5 s, the draw
  distance comes in from its far end, 4 m every half second, to no less than half the person's (48 m at least), and
  goes back out after 5 s with textures free to grow (768 MB under the target), 2 m a half second, for a minute after
  its last cut no further than 4 m past where it stopped.
- **Texture slots, and what's long unseen** (2026-10-27, after the evening's headset session on SL crashed 13 minutes
  and four teleports in on `bindless texture table full`: 17447 textures known, 5.2 GB resident, 7.5 GB of a 9.6 GB budget,
  so nothing was ever evicted and every texture of every place left behind kept its slot). The bindless table is now as
  big as the device allows up to 65535 (slots go to shaders 16 bits to a push constant, 0xFFFF meaning none; the RTX
  5070's update-after-bind limits are far above that; it was 16384), said at start (`video memory: ...; 65535 texture
  slots`). World textures ask with `Bindless.TryAllocTexture`, leaving 1024 for the renderer's own images (targets,
  mirrors, HUD, media); with none to be had an upload or shrink goes back in its queue and waits, never throws. Every
  texture has two times: last drawn (`LastUsed`) and last near (`NearAt`: part of something within a view's draw
  distance that wasn't drawn: columns and objects culled only by direction, avatars behind you with their bakes and
  attachments, impostors out of view, worn things between captures, the own HUDs and head attachments, projectors and
  Linden plants out of view; touched every 10 s at most). `TextureStore.Reclaim`, under the face resolvers' write lock:
  every 2 s, textures neither drawn nor near for 3 minutes are let go (left behind by a teleport, out of draw distance),
  256 a pass so a place left behind drains over a minute rather than freeing thousands of images in one frame; every
  frame, if fewer than a sixteenth of the world textures' share is free (counting slots on their way back), the longest
  unseen not drawn in the last 2 s go until an eighth is, far and left behind first, what's only behind you last. A let-go
  texture's image and slot are retired like a replaced one (freed three frames after the two a stale face cache may
  still name it); it comes back through the scheduler from disk (bakes from their source) when next wanted. Eviction for
  memory goes in the same order now. The cube table can't throw either: a probe that can't get two cube slots reuses the
  longest-unused resident probe's maps, or waits. The governor's 10 s line says `texture slots U of M (most N), K unseen
  180 s let go (MB), J let go for slots, uploads waited for a slot W times`; a low table also logs `texture slots low:
  ...` (at most every 5 s). For tests: `SOJOURN_TEXTURE_SLOTS=N` (at least 2048) makes the table small,
  `SOJOURN_TEXTURE_IDLE_MS=N` (at least 10 s) the idle time short.
- **The frame (the load governor; setting `load_governor`, menu > view, on by default).** Half-second windows of each
  frame's dearer of GPU time (which includes the frame's uploads and mip chains: pass `uploads`; the draw list's own
  copies are `frame data` since 2026-10-28) and render-thread
  CPU, against the display period (the headset's, else 60 Hz). Over the period: uploads (textures 6 MB / 0.75 ms a
  frame, meshes 4 MB / 0.5 ms), face resolving (1.5 ms) and decoding (3 threads) at half; over 1.4: a quarter, 2
  decode threads, 6 fetches, and far avatars' impostors and stale reflection probes refreshed half as often (probes a
  tenth); over 1.8, lagging too much: objects never drawn and more than 16 m away are held (nothing drawn is taken
  away) and let in at a rate, largest on screen first, one decode thread, 4 fetches, impostors at a quarter, probe
  captures a face a frame. A texture or mesh bigger than the frame's upload budget still goes, every frame normally,
  every 8th at 1.4, every 90th at 1.8: loading never stops, and never beats.
- **Held objects come in at a rate** (2026-10-27, after MagnetiS: on SL in the headset busy places run 20-30 ms a
  frame steadily, so the top level lasted 33 of 50 windows and, with nothing let through, far things never came; the
  user's rule: loading may slow down but never stop). 32 a second when the hold starts, doubling every 4 s it lasts
  (a thousand a second after 20 s): a hold that doesn't bring the frame down means the place is heavy, not its
  loading, so it gives way, gradually rather than on and off. Each frame's allowance (the rate over the frame's time,
  a quarter second's worth at most) goes to the largest size classes on screen (log2 of the screen area) among what
  the last frame held (`RenderWorld.Admit`); once let in, an object is never held again, its mesh and textures queued
  by size on screen as usual. The draw distance no longer comes in for slow frames: with far new objects held, a cut
  only took away what was drawn past it and kept anything beyond from ever loading (at MagnetiS it sat at 64 of 128 m
  from 20 s in to the end); it comes in only for video memory, above.
- **Trims: what's drawn far away, under lag** (the user's approval, 2026-10-27: an exception to the rule above, both
  far away and gradual). After 1.5 s of half-second windows over 1.05 periods, a step every window while still over:
  full-detail shadows reach less far (the last sun cascade's far end, 4% of it a step, down to half and never nearer
  than just past the third cascade, `ShadowSettings.FarReach`: the near cascades, the maps' size and the person's
  settings don't change; beyond it the coarse far cascade carries shadows on as before, so the trim moves where they
  turn softer rather than where they end, `docs/rendering.md` > Where shadows end; with far shadows off, cascade 3's
  own fade ends them there, which showed as a ring on the ground around the person), and avatars beyond a distance become impostors
  (from 48 m in, 2 m a step, never nearer than 20 m; each avatar with the crowd's 15% band, so nobody flips at the
  boundary; not those set "always", not with full avatars "all"). They come back after 5 s under 0.85 periods at half
  that speed, for a minute after the last cut no further than a step past where it stopped lagging. The 10 s line says
  `full-detail shadows reach x0.72 (90 m of 125; least x0.60), coarse beyond` and `avatars past 30 m as impostors
  (nearest 24 m)`. What the shadow trim buys, measured in the perf grid's forest (2026-10-29, cascade 3 timed alone,
  two Quest-sized eyes, five views): nothing there, cascade 3 0.54-0.81 ms a drawing at x1 and at x0.5 alike; SL's busy
  places (casters far away by the million) are still unmeasured. `SOJOURN_SHADOW_REACH=F` holds the trim at F.
  `SOJOURN_GOVERNOR_PERIOD_MS=N` judges frames against N ms (tests: small to make it act on the desktop, 1000 to keep it
  out of a bench). Checked on the desktop (two Quest-sized eyes at 60 Hz are over budget there): shadows went to x0.50
  (66 m of 125) and the impostor distance to 20 m. Not measured yet: what each buys in a real busy place (the next
  main-grid session's governor lines), and the impostors with a crowd present (the crowd plan's band for count-based
  impostors had never applied, fixed with this).
- **No oscillation** (the user's headset test, 2026-10-27: jitter worse at medium lag than heavy, frames on time and
  late by turns): a level rises after a second over its threshold (at once only past 3 periods, the GPU paging), falls
  after 4 s under 0.8 of it; levels change only how fast things load; the held objects' rate rises smoothly; when
  memory brings the draw distance in, objects already in reach (in view or not) stay out to 8 m past its edge, new
  ones come in only within it (`ViewSetup.NewDistance`, `SceneObject.InReach`); the shadow cascades keep fitting the person's draw distance, so the draw
  distance's cut doesn't change them (the shadow trim above does, at their far end).
- **Not slowed** (approved but not built): far particles' and far texture animations' updates (particles are
  generated from their history each frame, so a slower rate would show as stepping, not save much), far objects'
  scene updates.
- **The console's 10 s line** `governor:`: the frame's cost against the period (and the worst window), the level
  reached and what it does, the draw distance against the person's (and the least), new objects held (object-frames)
  and let in, with the rate now, video memory
  in use / budget / target with textures, geometry, render targets, probes and our allocator's use, whether textures
  are held, the pressure, and textures shrunk (MB), evicted and sharper copies dropped. At start: `video memory: ... MB,
  this process budgeted ... MB`. `SOJOURN_VRAM_BUDGET_MB=N` caps the budget for testing (headroom 10%).

Measured (perf grid, scene `places` and `tools/teleports.py`, 6 stops of 40 s, two eyes of 2448x2672 with the Quest's
frusta, shadow cap 3072; `SOJOURN_TEX_FULL=1` makes every texture want full size, as if inside every object, so the
local scenes fill video memory the way the busy SL place did; process memory from Windows' GPU Process Memory counter):
- Not governed (`SOJOURN_VRAM_GOVERNOR=0`): 1.2 GB at login, 5.06 GB 100 s in (textures 3.05 GB and climbing), where
  the test stopped it; a 3 GB budget would have been passed at 60 s.
- Governed against a 3 GB test budget (`SOJOURN_VRAM_BUDGET_MB=3000`, target 2.6 GB): at most 2.86 GB over the whole
  route (textures 0.3-1.4 GB), 0.1-1.4 GB of textures shrunk a 10 s window around the teleports, pressure at most 3.
  Against 2 GB (target 1.8): at most 1.95 GB.
- The frame side as first built (the draw distance then came in for slow frames too), checked with another session's
  GPU work holding the card at 99% (so no clean timing): on Place One, frames over 1.8 periods for 1.5 s brought the draw distance in from 256 to 128 m in ~16 s and from 128 to 84-100
  after teleports, held new objects beyond 16 m (hundreds a second at the top level), and gave it back 2 m a half
  second once frames ran under 1.3 periods for 5 s (to the person's 128 m within ~40 s). On these local scenes the cut
  bought nothing measurable (their cost is per pixel, not in far geometry); where far objects cost (SL's busy places:
  shadow casters, the scene phase's objects) it does. Clean before/after frame numbers wait for a quiet GPU.
- A real device loss came during one of these runs (14:36, nvlddmkm event 153 twice, with another session's GPU work
  at 99%; not traced to this code): the client logged out, started again where it was and said so.

## The GPU in busy places (2026-10-27)

Written by Claude Opus 5.5. The fifth main-grid session's late frames were the GPU, steadily, in three places (frame
GPU ms mean, then the costliest passes, ms an eye unless "a frame"; full Link size 2448x2672, shadow cap 1536):

| place | GPU | lights / projectors in view | costliest |
|---|---|---|---|
| Mirromere | 22.2 | 100-160 / 35-64 | alpha 2.2, projectors 1.4, probes 1.4 a frame, shadows 1.3 a frame, local lights 1.1, ssao blur 1.0 |
| Calas Galadhon | 22.6 | ~117 / ~52 | projectors 2.1, alpha 1.7, local lights 1.7, shadows 1.2 a frame, probes 0.9 a frame |
| Envision (club) | 23.4 | ~116 / ~55 | projectors 3.3, local lights 1.8, shadows 1.2 a frame, gbuffer 1.0 |
| calm regions (Arcanum, Wych) | 8.4-9.0 | 10-30 / 0 | ssao blur 0.8-0.9, sunlight 0.6 |

- Foveated rendering (since removed, the user's call 2026-10-27: too visible at the edges of view) was off there; it
  had taken a third off the fullscreen passes.
- **Light tiles** (`docs/rendering.md` > Light tiles): `render_scenes.py club` (a 20 m room, 100 point lights of 3-8 m,
  48 projectors; two Quest-sized eyes on the desktop): local lights 3.99 -> 2.54 ms an eye, projectors
  3.66 -> 0.64, tiles 0.35; frame 24 -> 15 ms; the same picture. A club is the hard case (most lights reach most of a
  small room); spread-out places like Mirromere gain more on local lights.
- Left there, roughly an eye: alpha 1.5-3 (Mirromere 2.2, MagnetiS 3.1: layered blended faces lit in full as the
  viewer's alphaF), ssao blur ~0.85 everywhere, local lights in rooms full of light; a frame: probe captures 0.9-1.8,
  shadows 1.2-1.6 (4-16M triangles in the shadow maps). Cheaper alpha lighting or half-resolution SSAO would change
  the look (the user's call); probes and shadows could update less often (plan items 1 and 3). The headset's detail
  levels now follow the viewer's default camera angle (`docs/rendering.md`), which puts more triangles in view there.

## Late frames and garbage (2026-10-26, evening)

Written by Claude Opus 5.5. The fourth main-grid session (Firestorm Social Island, ~8.5 min, most of it in the headset)
had ~240 frames over two display periods in ~400 s (an older frame left up), 61% of frames at half rate, and CPU p50
10 ms, p99 21, the scene phase ~20 ms in the late frames against 7 overall. Most late frames had every phase inflated
together rather than one phase spiking (125 of 214; 41 were the scene alone): what garbage collections and a thread
losing its core look like. The process allocated ~500 MB/s by the end (178 GB, 7681/658/108 collections, 10.2 s
paused), rising through the session as people arrived, with only ~10 texture decodes a second: ~10 MB a frame.
- **The cause: worn meshes' joint palettes.** Every rigged mesh an avatar wears got a fresh `Matrix4x4[]` palette each
  frame (inverse binds times the posed joints), and many SL creators' skins list ~110 joints whatever the mesh uses:
  ~7 KB and 110 matrix products a worn mesh a frame, copied again into the draw list and uploaded. An SL avatar
  wears 100+ rigged meshes. Our crowd had skins listing only the joints used, so it never showed (`tools/crowd.py
  build --full-skins` now makes SL-like ones, scene `forestcrowdfull` on the perf grid: forest plus that crowd).
  Now a palette is per avatar and skin content (`SkinShare`: joint map and inverse binds, shared by a body's pieces,
  its clothes and every LOD), written straight into the draw list. The view line says `worn meshes N a frame, M
  palettes, K shapes built`.
- Also gone from the render thread's garbage: a `FaceGeometry` per rigged face per frame, the animator's per-joint
  arrays, the probe list's grid (a list per cell), the overlay's vertex list, ray candidates' copies, the fixed sky's
  LLSD parsed every frame, hover texts' keys. Texture decodes take their planes and RGBA from the shared array pool
  (megabytes each, on the large object heap; tens a second while a place loads), handed to the upload under the
  texture's lock with their own size (a second decode finishing before the upload could have mixed sizes before).
- Body shapes: an expression or hand pose fading in changes the animated morphs nearly every frame, and each change
  rebuilt and uploaded the whole body. Morph-only changes now rebuild at most 10 times a second an avatar and once a
  frame overall; a new appearance at once.
- Geometry builds now make faces' boxes and pick copies on their own threads; the render thread only copies to the pool.
- The render thread runs above normal priority, and the collector in SustainedLowLatency (background gen2 rather than
  blocking while it can).
- Measuring: frames.csv has four more columns (gen0/gen1/gen2 collections since the last frame and GC pause ms) and
  two more (the loop's own CPU before and after the render: panels, name tags, input; xrEndFrame, voice, audio,
  media), which `frames.csv`'s CPU column never covered. `tools/vrlog.py` counts late frames and those with a GC;
  `tools/perfrun.py` shows them per window. `tools/alloctrace.py` says where allocations come from (the runtime's
  allocation ticks with stacks, read by `tools/alloctrace/`, a small TraceEvent program).

Numbers (perf grid `forestcrowdfull`: 40 dancing NPCs wearing full skins, all whole (`full_avatars` 48, complexity
limit off), two Quest-sized eyes on the desktop, 2026-10-27 with the machine otherwise idle; `local/perf/ab2-*`):

| | before (135c94d) | now |
|---|---|---|
| allocated | 448 MB/s (Matrix4x4[] 78%) | 15 MB/s |
| gen0 collections, GC pause | ~23/s, ~14 ms a second | 0.9/s, 0.4 ms a second |
| CPU a frame, median (p95) | 4.8 (5.6) ms | 3.5 (4.2) ms |
| scene / objects | 3.5 / 2.8 ms | 2.5 / 1.9 ms |
| worn meshes, palettes a frame | 520, 520 | 520, 40 |

When every piece's skin lists different joints (nothing to share), palettes stay
520 a frame and the gain is the garbage: 473 -> 15 MB/s, CPU median 5.3 -> 4.3 ms. The pictures (crowd, forest,
textures) match the old build's.

What was left on the render thread then, the draw list itself (~1 us a drawn object, ~0.2 a face), is now built in
parts on several threads with persistent instances (Threads, above); particles (1-1.5 ms at Social Island) are still
the render thread's alone.

## A long session's loading (2026-10-27)

Written by Claude Opus 5.5. In the fifth main-grid session (67 min, 13 teleports) places loaded more and more slowly.
The session's `assets:` lines: textures kept up (waiting near 0 almost throughout), meshes didn't: all 8 mesh fetches
busy for the last 20 minutes with 2-7k waiting, draining ~9 a second. Causes, all fixed (`docs/client.md` > Assets):
- **Meshes went through LibreMetaverse's download queue**: one FIFO of 8 at once for everything it fetches, never
  cancelled. Every legacy bump-mapped face put its diffuse texture in it whole, at once, wanted or not, so a place's
  bump maps (and bakes, sounds) queued ahead of its meshes, and what was left behind at the last place still did.
  Meshes now have connections of their own, and bump maps are on the texture scheduler.
- **Mesh bytes were kept all session** (gigabytes over an hour), and wants for meshes never wanted again were never
  dropped (the waiting count included every one: the "7000 waiting" was mostly places left behind).
- **LibreMetaverse's cache was 1 GB**, pruned every 5 minutes: by the end only the last ~10 minutes' whole files were
  on disk (`local/main/cache/grid` held nothing older than 01:32), so going back anywhere fetched it again.
- The texture scheduler and eviction copied the whole texture dictionary (35k entries by the end) 50 times a second and
  every 2 s.
Not a cause: the render thread's scene phase didn't grow over the session, nor did GC pauses per collection.

The reproduction: `tools/forest.py places --load` (four far regions, each with its own 360 meshes and 500 bump-mapped
clutter prims, beside the forest) and `tools/teleports.py` (13 teleports round them, 70 s a stop, cold cache, 120 ms
latency and 30 Mbit/s on fetches; load time per stop from frames.csv's pending count). Seconds until pending was under
20 on each first visit to a place, before (75cad00) / now: Place One 54 / 33, Two 52 / 32, Three over 70 / 35, Four
68 / 36; before, each new place took longer than the last, now they hold. Revisits within ~10 minutes are a few seconds
slower now (10-22 s against 5-11: bump maps and textures decode again from disk). The local grid's single OpenSim is
the limit there (~0.2-1.7 MB/s served), so the numbers say more about order and queueing than about SL's speed.

## Busy places (2026-10-25)

The first main-grid session (Bay City, then Calas Galadhon, a dense forest estate, in the Quest 3 over Link) ran at
45 fps or less: median frame 22 ms, p95 67, 76% of frames over 14 ms. It was the **CPU**: the GPU never held a frame
up (fence waits ~0), while the render thread's scene phase took 12-33 ms and probe planning 4-14, both growing with
the objects in range and with the assets still loading (5-20k "pending"). The vegetation in the forest never loaded.

What dominated, and what changed:
- **Every object, several times a frame.** Sync, attachment indexing, shadow extents, projectors, the draw list,
  probe scans (every 500 ms per region, with a matrix inversion per object) and particle/texture-animation tracking
  (a quarter of the render thread alone) each walked every object of every region. Now `RegionScene.Index.cs` keeps
  each object's placement from the region's change reports (`RegionSource.Changes`, from `RegionWorld.Changed`), files
  objects in 16 m columns that are culled whole (draw distance, frustum, which shadow maps reach them), keeps lights
  and attachments in lists of their own, and probe scans and particle tracking follow the same reports. A few objects
  a frame are rechecked against the region's dictionary as a safety net.
- **Objects waiting for assets re-resolved every face every frame.** The face cache now holds faces still waiting,
  valid until one of their textures changes state or a material arrives; materials distinguish "on its way" from
  "failed" (retried after 30 s) so a missing material no longer counts as loading forever.
- **Downloads** (see `docs/client.md` > Assets): the old path fetched whole textures FIFO, eight at a time shared with
  meshes, never cancelled, so a teleport left the old region's backlog in front of the new one; a mesh whose download
  failed once was never asked for again. Textures now stream by byte range for the screen area they cover, largest
  first; meshes have their own prioritised queue with retries.
- **Decoding**: OpenJPEG (native, `tools/openjpeg.py`) replaces CoreJ2K: 1.7x faster on full decodes, a tenth of the
  garbage (CoreJ2K allocated ~27 MB a 1024 texture, and the garbage collector's pauses landed on the render thread).
- Per-frame upload budgets down (textures 12 MB, geometry 8 MB a frame); per object, the LOD radius and the region's
  simulator are no longer recomputed or looked up (the latter took a lock the network threads share).

### Holding the frame rate while a place fills in (after the second session, 2026-10-25)

The second main-grid session (Calas Galadhon, Burn2's temple burn with a crowd, a sandbox; Link at 90 Hz) still ran
37-48 fps in the forest (scene 10-12 ms, probe planning 7) and 12-15 at the burn (scene 40-58 ms, avatars 14), only
the empty sandbox holding 90. 19% of the time went to frames held 4 display periods, 17% to 6. The render thread
allocated ~400 MB/s (500 GB in 21 minutes; a gen2 collection a second; 19 s of GC pauses). What changed:
- **A scene budget** (`Realtime/SceneBudget.cs`, on the renderer as `Budget`): twice a second the scene phase's mean
  decides; over 5 ms, objects under a few pixels of radius on screen stop casting shadows (2.5x the draw threshold),
  then stop being drawn, a step at a time (0.6 to 6 px); under three quarters of the budget for 2 s it steps back.
  Worn meshes go by their wearer's size, their shadows first (at 4x). The view line says when it's stepped in.
- **Resolving faces gets 2 ms a frame** (`RenderWorld.ResolveTicks`): first draws and objects whose textures just
  changed state are where loading costs the render thread. Past the budget, an object out of date draws as it was for
  up to two more frames (a retired texture slot is only reused three frames on), and new objects over 16 m away wait a
  frame, so the near ones resolve first. Texture uploads stop after 1.5 ms a frame and geometry after 1 ms, as well as
  at their byte budgets (at least one each, so progress never stops).
- **Worn objects are cached**: attachments and rigged meshes kept no face cache (bakes on mesh follow the wearer), so a
  crowd re-resolved every face of every attachment every frame, cloning texture entries. Their faces now cache like
  others', keyed by the wearer's bakes. `Avatars.WornState` (joint overrides, hidden body parts) ran every frame per
  avatar, sorting the rigged meshes by their ids' strings; it now runs when what's worn changes.
- **Probe captures**: each capture grew a fresh draw list (thousands of entries: most of what was allocated while a
  place loaded); they're pooled now. A capture spread over six frames rebuilt its draw list (everything around the
  probe) every frame, the main source of probe spikes; now at its start and halfway. Objects under a pixel of a
  256-pixel face are left out of captures. Shadow extents and terrain stride patterns are no longer rebuilt per frame.
- **Seeing where it goes**: the view line (every 10 s) breaks the scene phase down: sync, shadow fit, avatars, objects,
  particles, ms a frame. `SOJOURN_ALLOC_PROFILE=1` logs allocation by type every 10 s (the runtime's own samples).

Forest, cold, two Quest-sized eyes on the desktop (`perfrun.py --cold --headset-size`), 120 ms fetch latency at
30 Mbit/s, the same three regions, the loading part (15-120 s), before / now: frames over 10 ms of CPU 1032-1043 / 57,
CPU p95 10-19 / 6.7-7.2 ms, allocated 120-127 GB / 7.8 GB in 150 s (steady state 66 / 37 MB/s, loading 400 / 96),
gen2 collections 1144-1216 / 58, GC pauses 2.0 / 0.7 s; everything loaded at the same time (~105-120 s: the network
decides). The CPU spikes left are scene (new objects arriving) and probe planning, 5 ms each at most on average. The
scene budget didn't engage there in steady state (scene ~2 ms); a run that connected all four regions (34k objects,
25k draws) held scene ~3.8 ms with it at step 2-4. Not measured locally: crowds (no rigged-mesh content on the local
grids), so the worn-object caching and the budget on avatars wait for the next main-grid session's view lines.

### The reproduction: a synthetic forest

The local grid never had content like SL's, so `tools/forest.py` builds one (our own content, nothing from SL): four
regions with 27.8k objects (~420 mesh trees and ~2500 mesh ferns a region with alpha-masked leaf cards, grass cards,
rocks, prim ruins of 80 prims, ~2000 clutter prims each with its own texture) and 8.9k textures encoded the way SL's
are (J2C, 5 levels, LRCP layers, the same bytes per size). It lives on a second grid copy so performance work never
disturbs the usual grid: `SOJOURN_GRID=perf-grid python tools/grid.py start` (ports 9740-9743, account Sojourn Perf,
scene `forest` in `local/perf-grid/scenes`). `python tools/perfrun.py LABEL [--cold] [--headset-size | --vr-sim]
[--dll DIR]` runs a timed session there and summarises frames.csv per 15 s; `SOJOURN_FETCH_LATENCY_MS` and
`SOJOURN_FETCH_KBPS` give the asset fetches a far grid's latency and bandwidth.

How representative: in the headset path it reproduces the main-grid session's shape closely (the old build: scene
22-27 ms, probes 8-10, frames 41-83 ms, thousands of assets still pending after 2.5 minutes). What it lacks: avatars
(rigged-mesh crowds: the session had few avatars and they cost under 0.1 ms there, but a club would), scripts and
moving objects, PBR materials, SL's network (latency is simulated only for the texture fetches) and SL's sheer
object counts in the biggest builds. `--vr-sim` runs in Meta's XR Simulator, whose synchronous compositing adds
~8-11 ms of "rest" a frame that Link doesn't (Link measured 0.3-0.5 ms).

### Numbers (forest, 2026-10-25)

XR Simulator (Quest 3 profile, 72 Hz), cold cache, 150 s from login, standing at Atelier 200,200:

| | before (6548095) | now |
|---|---|---|
| CPU ms per frame, median (p95) | 39-65 (60-125) | 17-24 (21-39), of which ~10 the simulator's compositor |
| scene / probes ms | 22-27 / 8-10 | 5.4-8 / 0.1-4.9 |
| frame ms, median | 42-83 (12-24 fps) | 27.8 (36 fps: half rate, the simulator's compositing in series) |
| assets still loading | 9,478 at peak, 1,270 after 150 s | 459 at peak, 0 after 30 s |
| downloaded | 196 MB, 1,447 assets in 150 s (not done) | 110 MB, everything (3.1k textures, partly) |
| with 120 ms latency and 30 Mbit/s | | 4,533 at peak, 0 after 60-75 s |

Desktop, two Quest 3 eyes at the headset's starting size (1958x2138, Quest frusta, shadow cap 1536), steady state
once loaded: old CPU 8.7-9.1 ms median (p95 14.5-15.7), scene 5.5-5.8, probes 2.6-2.7, frame 10.4-10.9 ms; now
CPU 3.6-6.8 (p95 4.5-8.4), scene 2.4-5.0, probes 0-0.1, frame 6.1-7.4 ms (GPU-bound). The view holds 3.6-6.8k drawn
objects of ~34k and 25-54k draws (faces x passes x shadow cascades).

What's left on the CPU, in order: (1) building the draw list costs ~1 us per drawn object (a material, an instance and
a draw per face per pass and cascade, copied into the frame arena and sorted): persistent GPU instance and material
buffers updated only where objects change, then GPU culling (plan item 7 below); (2) ~40-60 MB/s of allocations in
steady state (iterators, closures, per-frame lists), ~10x that while textures decode; (3) skipping small far shadow
casters (item 3). On the main grid, the `assets:` and `view:` lines in the session log (every 10 s) give objects drawn
and culled, draws, fetches, and GC counts and pauses.

## Crowds (2026-10-26)

Written by Claude Opus 5.5. `World/Avatars.Crowd.cs`, `Frame/Renderer.Impostors.cs`, `Frame/ImpostorAtlas.cs`,
`Shaders/impostor.*`.
- **Who's drawn how**: the nearest `full_avatars` (16, the viewer's High-Ultra) whole, and anyone within 2.5 m; the rest
  impostors; out of view beyond the count, nothing (impostors cast no shadow). Over `avatar_complexity` (350,000) or
  1000 m² of attachments, a jelly doll (the system body in the viewer's colours, no attachments); "never" a grey one.
  The person's own avatar is never limited. `SojournAvatars list|render|inspect`, settings menu > view. On first sight an
  avatar is a cloud ("loading") until the attachments its appearance lists are in (`docs/rendering.md` > Avatars' faces).
- **Complexity** is the viewer's ARC (getRenderCost, texture costs, 200 a bake), with meshes judged from their headers:
  `GeometryCache.MeshCost` fetches a mesh's data without building it. Until every worn mesh's header is known an avatar
  is a cloud ("checking"), so a crasher attachment is never decoded or drawn once over the limit. A checked avatar's
  mesh fetches count 4x its screen area (it shows nothing until they're in). Your own is judged too (shown, never
  limited).
- **Worn rigged meshes** take the wearer's LOD (calcLOD: the avatar's distance and animated extents) as soon as the
  mesh header says rigged; only before that is the prim's own LOD asked for first.
- **Impostors**: a capture draws the avatar once, with the G-buffer pipelines, into a tile of an atlas in the G-buffer's
  formats (16 tiles 256x512, 32 of 128x256, 128 of 64x128; 117 MB, made on first use), from an orthographic box along the
  line from the camera whose view is a translation only (world normals). In the scene its box is drawn back faces only
  and `impostor.frag` marches each eye's ray through the tile's depth to the surface, writing that texel's G-buffer and
  depth: each eye sees the surfaces at their own depth (no card), and the deferred lights, shadows received and SSAO
  treat it as geometry. Colours are filtered among the avatar's texels. Up to 4 captures a frame, by staleness: 4-30
  times a second by size on screen, at once past 2 degrees of view change or 15% of distance. Between captures only the
  root moves (lights worn follow; nothing else of the avatar is drawn).
- **The test crowd**: `tools/crowd.py` (perf grid scene `crowd`): OpenSim NPCs in three outfits of our own rigged mesh
  (~230k triangles at high LOD, ~90 prims each with jewellery, bakes-on-mesh skin, blended hair; ARC 400-470k, so
  jelly dolls at the default limit), dancing; `--say "spawn 40 7 1.5"` etc. through `tools/perfrun.py`, which also
  takes `--look X/Y/Z` and `--set name=value`, and logs a `gpu:` line every 10 s.
- **Numbers** (two Quest-sized eyes on the desktop, complexity limit off): 80 avatars around the viewer, all whole
  before: CPU median 10 ms (p95 14), scene 7 (avatars 1.7, objects 4.2-4.8), record 3, GPU 14.6 ms (alpha 2.4, shadows
  2.3, skinning 1.45); with 16 whole and 43 impostors: CPU median 4.9-5.6 (p95 7.6-9.2), scene 3.6-4.0 (avatars 1.2-1.5,
  objects 1.7-1.8), record 1.3-1.5. 40 on the lawn before: CPU median 7.8, scene 5.6, GPU 9.5. GPU after: not
  measured cleanly (another session's training filled the VRAM from 13:45 on; frames paged at 100+ ms both ways).
- Fixed on the way: avatars whose object update came after their appearance stayed clouds for good (6 in 40 NPCs);
  `WorldHub` now keeps each avatar's last appearance and restores it.

## Close-up alpha (2026-10-26)

Written by Claude Opus 5.5. The third main-grid session's flashes were the GPU at 12-37 ms a frame in Mirromere's
flower field and the Papercut cafe: close-up layered blended faces (flowers, glass cases), every layer lit in full by
`alpha.frag` (atmospherics, probe ambient over up to 32 probes, six local lights, the sun shadow), as the viewer's
alphaF does, over two eyes of ~4 Mpx each. `tools/render_scenes.py flowers` rebuilds the shape of it (800 blended flower
cards, glass cases, 12 flowered panes in a row). The alpha shader now samples the shadow only for fragments that show
and discards below the viewer's minimum alpha on every path: 12 panes filling the view, alpha 1.72 -> 1.30 ms per eye
(`docs/rendering.md`). Solid translucent glass doesn't gain from that.

After (Claude Opus 5.5, the renderer's owner that evening; RTX 5070 to itself, desktop stereo with the Quest's frusta,
first person in the flower field facing the panes, shadow cap 3072 as the user's settings now have):
- **What the log says and doesn't.** In Mirromere and the café the eyes were already at their floor (x0.6, 1472x1600)
  and the GPU still took 11-18 ms (café 15-37). The rebuilt flower field takes 4.5 ms at x0.6 and 11.1 at full Link size
  (2448x2672). So the real places cost 3-4 times what our rebuild does at the same size, and we don't know which part
  (more blended layers, lights, mesh triangles in four shadow cascades, probes). The draw count tracked the GPU time
  there loosely (café 30k draws ~25 ms, 9k ~17), but the synthetic forest draws 25-54k at 6-7 ms. The view line now says
  what's drawn (`content:` lights, projectors, blended faces, particles, triangles an eye and in the shadow maps) next to
  the pacing line's costliest passes: the next session in those places decides between per-pixel and geometry work.
- **The CPU was over too**: the headset's `cpu` there was 9-14 ms a frame (objects 3.5-5, particles 2-2.5, shadow fit
  0.8), itself near the 11.1 ms period. Fixing only the GPU wouldn't hold 90 there.
- **The user's settings are heavier now**: full resolution fixed (no dynamic resolution to fall back on) and shadows
  3072. Those places at x1 would be ~2.5x the x0.6 cost, past the 22 ms where the runtime shows an older frame. Asked
  on the board: resolution auto (full when it fits) or 72 Hz in the Link app (13.9 ms before halving).
- **Done**: the local-light pass tests each light's reach before its lighting (48 lights over the flower field: 1.40 ->
  1.06 ms an eye, the same picture); since 2026-10-27 it loops over its tile's lights only (The GPU in busy places,
  above). Foveated rendering took 11.1 -> 8.0 ms there and was removed (too visible at the edges, the user's call).
- **Since (2026-10-27, the user's approval)**: simpler lighting for blended faces and a cap on their layers, both
  settings (`docs/rendering.md` > See-through surfaces). Simpler lighting takes the alpha pass from 5.95 to 1.75 ms a
  frame (panes), 3.11 to 0.84 (field), 2.45 to 0.66 (glass); off by default for the person's eye. The cap saves nothing
  in these scenes and costs its count, so it's off.

## How to measure

`python tools/client.py run --bench [--stereo] --eye WxH [--fov 98] [--shadow-cap N] --sky noon --bench-out FILE`:
holds Multitude's comparison views of the Atelier scene (overview, plaza, pavilion, PBR row, waterside, pond) and a
moving orbit, each for `--bench-seconds` after a warm pass that waits until everything in view (and its reflection
probes) has loaded. Per view: frame time (mean / p95), GPU ms per frame (timestamps), render-thread CPU per frame
(scene and draw list, avatars, probe planning, recording), draws and instances; then GPU ms per pass group, one
column per view. Every pass reads as ms a frame, both eyes (since 2026-10-27; the tables below from before then give per-eye passes an eye).
`SOJOURN_AVATAR_CLONES=20` adds a crowd (each avatar drawn 20 more times). For hitches rather than means,
`SOJOURN_FRAME_LOG=FILE` writes every frame (time, frame ms, CPU ms, fence wait, probe captures, assets pending);
`SOJOURN_GPU_LOG=FILE` every frame's GPU ms and loading's parts of it (uploads and their bytes, probe captures and
faces, impostor captures, a few big passes), summarised per window by `tools/gpuarrival.py`. Screenshots of each view go to
`local/client/shots/`. Scenes: `python tools/grid.py scene light|dense` (`docs/client.md`).

Caveats: the GPU is shared with Tessel's viewer; run-to-run noise is ~5-10%. The crowd is the system avatar
(~7k triangles, two alpha layers); Second Life crowds wear rigged mesh bodies of 100k+ triangles each, so real
crowds cost far more than this one. A Quest 3 eye is about 2064x2208 at 1.0x; with Link the runtime's recommended
size depends on its settings.

## Numbers (2026-10-20)

Ranges are over the seven views (GPU ms per frame, mean).

| scene | eyes x size, vertical FOV | shadow cap | GPU ms | frame p95 ms | CPU ms |
|---|---|---|---|---|---|
| light (116 prims, 1 avatar) | 1 x 1920x1080, 60 | none | 1.6-2.2 | 2.6-4.1 | 0.3-0.5 |
| light | 2 x 2064x2208, 98 | 1536 | 5.4-6.8 | 7.4-9.3 | 0.4-0.9 |
| dense (~2400 prims, 21 avatars) | 1 x 1920x1080, 60 | none | 2.6-3.5 | 4.1-5.5 | 0.6-1.6 |
| dense | 1 x 2064x2208, 60 | none | 4.9-7.0 | 6.9-8.9 | 0.6-1.3 |
| dense | 2 x 2064x2208, 60 | none | 8.5-11.5 | 10.5-14.3 | 0.8-1.7 |
| dense | 2 x 2064x2208, 60 | 1536 | 7.7-10.2 | 9.6-13.5 | 0.9-1.7 |
| dense | 2 x 2064x2208, 98 | none | 9.3-11.4 | 11.1-13.7 | 0.8-1.8 |
| dense | 2 x 2064x2208, 98 | 1536 | 7.8-10.0 | 9.9-12.2 | 0.8-1.9 |
| dense | 2 x 2064x2208, Quest 3 frusta (`--quest-eyes`) | 1536 | 7.4-9.7 | 9.4-11.9 | 0.7-1.4 |

Dense views hold 1400-2000 instances and 3.4-5.8k draws at the headset FOV. In the headset path (Meta XR Simulator,
`docs/vr.md`) the dense scene holds 72/72 fps once loaded at 2064x2162 eyes (0-0.4% missed, mostly the simulator's
compositor), and the first ~15 s after arriving miss ~10% of frames there. CPU is the render thread's own work
per frame (it overlaps the GPU's previous frame); its p95 reaches 4-5 ms on the moving orbit.

Where a two-eye frame goes (dense, 2 x 2064x2208, 98 degrees, cap 1536, plaza view, GPU ms):

| | per eye | per frame |
|---|---|---|
| shadows (4 sun cascades, 2 projector maps; once) | | 1.14 |
| G-buffer | 0.68 | 1.36 |
| sky (after geometry, only uncovered pixels) | 0.01 | 0.03 |
| sun shadow + SSAO into the light map (`sunlight`) | 0.49 | 0.97 |
| the light map's two blur passes (`ssao blur`) | 0.55 | 1.10 |
| sun, ambient and reflection probes (`light.frag`) | 0.59 | 1.19 |
| local lights (fullscreen, up to 256 lights per pixel) | 0.56 | 1.11 |
| projectors | 0.25 | 0.50 |
| fullbright, avatar alpha, water, haze, alpha | 0.49 | 0.97 |
| post: tonemap, CAS, glow, combine, SMAA, present blit | 0.45 | 0.90 |
| skinning | | 0.17 |
| total | | 9.5 |

**What dominates:** the deferred lighting chain, ~2.4 ms per eye (sunlight + blur + sun/ambient + local lights +
projectors: six full-screen passes over 4.6 Mpx, each reading the G-buffer again), half the frame. Then shadows
(fitted to the centre view and shared by both eyes; the viewer's rule sizes them to the screen, which at a headset
eye's size made them 2-3.5 ms, the costliest pass, until the cap), then the G-buffer. CPU is small at this density
(about 0.15-0.3 us per draw after the face cache) but grows linearly with draws: a CPU stress (256 m draw distance,
61 avatars, 5-7k draws, small eyes) measured 1.5-3.5 ms before the face cache and 0.6-1.5 ms after.

## Toward ~11 ms for two eyes (90 Hz)

At 72 Hz (13.9 ms) the dense scene fits now with the shadow cap. 90 Hz needs ~9 ms of GPU (the Link compositor and
encoder take ~1-2 ms of the 11.1), and real SL places are heavier than the dense scene, mostly through avatars.
In the Quest over Link (2026-10-21, Link offered only 90 Hz, recommending 2448x2672 an eye): at full size the dense
scene took 14 ms and ran at 45 fps; at x0.8 (1958x2138) 6.5-8.7 ms at 90/90, with drops to 52-65 fps at ~10-12 ms on
heavier views. The eyes' size now follows the GPU (dynamic resolution, `docs/vr.md`), which trades sharpness for frame
rate there; the items below would buy the sharpness back. In rough order of value:

1. **Hitches on arrival and while moving.** (Since: the scene budget, resolve and upload time budgets, pooled and
   less often rebuilt capture lists: see Busy places.) Probe captures are now sliced: one capture at a time, two of its 12 face
   renders a frame (its draw list rebuilt halfway, its sun shadow drawn once a pass and copied back on later frames,
   each pass filtered after its sixth face), where a whole capture used to add ~1-4 ms to one frame. Terrain was the worst CPU hitch: a region's surface
   (10-17 ms) on every terrain change and a new patch mesh (8-28 ms) whenever the camera's stride pattern changed,
   so walking dropped frames too. Both now build on the thread pool and the last one is drawn meanwhile
   (`World/Terrain.cs`). Since 2026-10-27 (Threads, above): faces resolve off the frame, region changes in a timed
   batch, and the render thread is its own, so the main thread's simulation overlaps recording and xrEndFrame.
2. **Fold the lighting chain** (~2.4 ms per eye): one tiled compute pass for sun, ambient, probes, local lights
   and projectors (lights culled per tile instead of looped per pixel: done 2026-10-27 for local lights and projectors,
   the passes kept), SSAO and its blur at half resolution.
   Expect -1.5 to -2 ms per frame. Diverges from the viewer's pass structure, not its look; compare against the
   reference sheets as Multitude did. **2026-10-27 (Claude Opus 5.5):** the local-light and projector passes leave a
   tile none of theirs reaches before reading the G-buffer (-0.4 to -0.6 ms a frame in most test views); folding them
   into the sun and ambient pass, and merging the two, measured slower in light-filled rooms (`docs/rendering.md` >
   Light tiles). Half-resolution SSAO: built as a setting, off (`half_ssao`; -1.3 to -1.6 ms a frame at Link's size,
   a faint change at edges; `docs/rendering.md` > GPU options to judge), for the user to judge in the headset.
3. **Shadows under 1 ms:** cap 1024-1536 for VR, update the far cascades every other frame, cheaper casters (lower
   LODs, skip small far casters). The cascade fit already uses the centre view for both eyes. 2026-10-27: casters
   that cut nothing are drawn depth alone (-0.03 to -0.09 ms a frame; `docs/rendering.md` > Small, invisible savings);
   the far cascades take turns while the view holds still (-0.45-0.78 ms at 3072 in the forest), but any head movement
   changes their fit: doing it while moving needs the far cascades fitted a little larger (slightly softer far
   shadows). Built as settings, off (`far_shadow_margin`: at 0.1 a moving view's shadows 2.17 -> 1.72 ms;
   `min_caster_texels`: nothing in the forest is small enough to matter, SL's clutter unmeasured; `docs/rendering.md`
   > GPU options to judge), for the user to judge. Lower LODs for casters weren't built.
4. **Multiview**: done 2026-10-27 (`docs/rendering.md` > Two eyes). Measured on the RTX 5070 it saves nothing: the
   G-buffer drawn to both eyes at once was 2-3 times slower than an eye at a time, so only the forward and alpha
   passes are multiview (neutral, recorded once); the fullscreen passes go an eye at a time through each layer's views.
4b. **The lenses' hidden area**: done 2026-10-27, not shaded (`docs/rendering.md` > The lenses' hidden area); a test lens
   hiding 19% saved 0.3-1.2 ms a frame. Meta's real mesh is for the next headset session.
4c. **See-through surfaces**: done 2026-10-27, alpha.frag per material path (-25-30% of the alpha pass in the flower
   field); simpler lighting (-70% of the alpha pass, on by default since the user chose it) and a layer cap (off) are
   settings (`docs/rendering.md` > See-through surfaces). 2026-10-29: the pre-water pool per path too (Arriving at a
   heavy place, above).
5. **Fixed foveated rendering**: built (-28% of the frame in the flower field) and removed on 2026-10-27 (the user: too
   visible at the edges of view). Not to be offered again in that form.
6. **Avatars**, the real Second Life cost in crowds: rigged-mesh LOD, impostors past a distance or count (the viewer
   billboards beyond 16), a complexity budget like the viewer's jelly dolls. Needs mesh-body test content first (the
   forest has no avatars); measure at a club with the person's account before building it.
7. **CPU at SL scale** (25-55k draws in the forest): cached world bounds and a spatial index are done (the object
   index, above). Persistent instance and material buffers and parallel draw lists done 2026-10-27 (Threads, above:
   objects 1.7-1.9 -> 0.9-1.4 ms in the crowd); GPU-driven culling not needed at those numbers. Before that
   (2026-10-26, Claude Opus 5.5): profiled on the forest (`tools/cputrace.py`), objects cost ~0.3 us each there against
   ~1.2 in Mirromere (3-4.6k drawn, 4-5 ms), so the synthetic scene doesn't show what makes SL's objects dear (faces per
   object, PBR overrides, textures to validate?). What the profile did show is fixed: the draws' sort by geometry arena
   (now a counting pass) and a lock on the region's simulator shared with the network threads. Run `tools/cputrace.py`
   in a busy main-grid place before building the buffers. Particles were the other CPU item there (2-2.5 ms in
   Mirromere, 16 ms once in Bay City): bouncing and homing particles were integrated from birth every frame (O(age)
   each); they now carry on from the last frame (render_scenes.py particles: 7.4-10.6 -> 1.1-2.1 ms a frame).
8. Small: precise barriers instead of ALL_COMMANDS everywhere, packing the G-buffer formats (narrow normals built as
   `narrow_gbuffer`, off: -0.02 to -0.05 ms a frame here; a narrow emissive banded the sky). (The G-buffer's colour
   clears are gone: the sky writes every pixel geometry doesn't.)

Prims are dead-reckoned too (2026-10-27): moving ones are placed every frame (their placement and cull sphere, a
spin only rotating), everything else only when the region reports a change (`docs/vr.md` > The rig > Moving things).
