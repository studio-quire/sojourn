# Media: web pages and video on faces, parcel media, web page offers

Written by Claude Opus 5.5, 2026-10-26. Agent-facing.

## Where it stands (2026-10-26)

Built and tested on the local grid and in Meta's XR Simulator: pages and video on objects' faces (media on a prim) and
the parcel's media, drawn as the viewer draws them, with their sound placed in world audio's mix; the person's hands
on them (the mouse, the headset's left trigger), an up-close view (a sheet on the desktop, a board in the headset);
pages objects offer (llLoadURL) opened after the person's press; the settings; the assistant's `media` tool (Haiku
read a board, pressed its button and put a TV up close on request). `python tools/media.py test` (19 cases) and
`test headset` pass; `perf` measures the frame cost (below). Not seen yet: the headset itself (the simulator draws no
quad layers here, so the board's look is unchecked) and Second Life's real media (YouTube, club screens): on the board.

## Using it

| | Desktop | Headset |
|---|---|---|
| start media that waits (a play triangle shows under the pointer) | click it | left trigger on it |
| click on a page | click (hover, drag, double click work) | tap the left trigger |
| scroll a page | the wheel over it | drag across it with the trigger held (it follows the ray), or the right stick with the ray on it (up close, on a HUD, on a face; up/down and sideways: `docs/vr.md` > Controls > Scrolling with the stick) |
| press and drag (select, sliders) | drag | hold the trigger still for half a second, then move |
| type into a page | click a field, type; Esc gives the keys back | up close, the board's keyboard key (there while a field is focused) brings up the keyboard; or ask the assistant |
| pause or play a video | click it | tap it |
| up close | Z on what's under the pointer (Z or Esc puts it away); a click outside the sheet puts it away | double tap it; either trigger uses the page there, × puts it away, the right trigger drags the board by its strip |

A page's own fullscreen button (a video player's) also brings it up close, and so does a new window the person's
press on a page opens (a link with target=_blank, window.open). Up close the strip has ×, back, forward and
reload (only where the face lets the person control it) or play/pause, and the title. Settings menu > sound: **media**
(yours, all, off: what plays by itself) and **media volume**; SojournSettings has `media_autoplay` (mine, all, off),
`media_volume` (0-2, 1 the viewer's level) and `media_placed` (a face's sound from where it is; off: centred). The
assistant's `media` tool (see SojournMedia below) reads, presses, types, navigates, pictures and zooms.

## What plays, and whose

- **Auto-play** (decided 2026-10-26): `mine` by default: media on the person's own objects and HUDs, and their own
  land's, plays by itself; everything else shows its own texture until started (a touch, a click, the assistant).
  `all` is the viewer's behaviour (whatever has auto-play set, and parcel media), `off` nothing. Why: loading a URL
  tells its server your address and that you're there, every page costs frame time, and a loud surprise is worse in a
  headset than at a desk. A script's parcel "play" doesn't override the choice.
- **Shown at all** (the viewer's rules): not on other people's attachments (the viewer's MediaShowOnOthers, off),
  not from blocked residents or objects, not beyond the draw distance (+16 m), and while the person is on a parcel set
  to hide media from outside it (ObscureMedia), only its own.
- **Permissions**: interact (clicks, keys, scroll, following links) and control (back, forward, home, the address)
  by the face's perms_interact / perms_control, any of owner, group or anyone (the viewer checks them one at a time,
  which refuses an owner who isn't in the face's group). Starting and stopping are always the person's (local).
- **Whitelist** (when the face has one switched on): every main-frame navigation, redirect and request is checked in
  the helper, the region's too, with the viewer's matching (scheme, host:port and path, `*` wildcards; an entry
  without a port matches only the default port, so the test's whitelist is `127.0.0.1:9752`).
- **Shared address**: the person's navigation on a face they may interact with is sent to the region
  (ObjectMediaNavigate, as the viewer) so everyone sees it; the region's own changes are followed, not sent back.
  `set_url` changes one of the person's own faces (ObjectMedia update with the other faces' entries as fetched).
- **New windows** (popups, 2026-10-26): the viewer opens them in its own browser; here a new window opens up close as
  the standalone page (in place of an offered page or an earlier window), only when the person's press on the page
  made it (Chromium reports whether a user gesture did: a script's window on its own is refused, as browsers block
  popups without a click), http(s) only, under the URL rules and the opener face's whitelist, one a second at most. It's
  the person's window: they use and navigate it and put it away (× ends it); the assistant's confirmations on it follow
  the page that opened it (`list` shows `opened_from`). `MediaManager.Popup`.
- **HUD faces**: media on the person's HUDs plays as on any face of theirs, and a click or the board's press on it goes
  to its page, not the HUD's touch (docs/hud.md).
- **The first click focuses** (2026-10-29, batch B10; LLToolPie::handleMediaClick, LLViewerMediaFocus, 7.2.0's
  first-click-interact, read in `lltoolpie.cpp`): a click (a tap in the headset) on a playing page or video in the
  world that isn't focused focuses it and goes no further, unless its first click is let through: the face asks for
  it (PRIM_MEDIA_FIRST_CLICK_INTERACT) and the person's `media_first_click` allows it for whose object it is (none,
  huds, own, friends, group, land, any: each including the ones before; all: every face, asked or not). The default
  is land, both viewers' (31): HUDs, the person's own, friends', their groups' and the land owner's objects on the
  person's parcel, when they ask. Hover and press-and-hold go only into a face that takes clicks; drag-scrolling in
  the headset works either way (it isn't a click). Media that waits to be started is started and focused by the same
  click. One face is focused at a time, outlined faintly (the viewer's focus ring; not while up close); a click in the
  world off media, a left-trigger press off media in the headset, or Esc on the desktop takes the focus away. The
  assistant's presses aren't affected (they're deliberate, and confirmed on others' media). `MediaManager.Focus.cs`.
- **Auto-zoom**: a face with PRIM_MEDIA_AUTO_ZOOM comes up close (the sheet, the board) on the click that focuses
  it. The viewer moves its camera onto the face; here the view never moves (the headset's above all), the page comes
  to you. `media_auto_zoom` (on by default, as the viewer always follows the flag). The parcel's "prevent camera
  zoom" (ParcelProperties' MediaLinkSharing block) isn't followed: LibreMetaverse drops that block.
- **Auto-scale** (the face's PRIM_MEDIA_AUTO_SCALE, or the parcel's "auto scale content"): a page is laid out at the
  power-of-two size it's drawn at (1000x500 becomes 1024x512), so it fills the face rather than its corner, as
  LLPluginClassMedia::setSizeInternal rounds the size up; videos already filled the texture. A face whose size or
  auto-scale changes reopens at its new size.
- **Loaded at once**: eight, ranked as the viewer ranks them (in use or up close first, then offered pages, parcel
  media, how big on screen, nearest); tiers: focus and two "normal" at full rate, four "low" at half, the rest a
  slideshow; not seen for a second, hidden (no painting or copies; its sound plays on). Texture scale 0.25-2 by size on
  screen, changed only after 1.5 s; up close at the size it's read.

## How it fits

```
client (Sojourn.Client/Media)                                   helpers (src/Sojourn.Media.Native, local/media/runtime)
  MediaWiring   start-up, hooks, settings, per-frame calls          sojourn_web.exe   CEF 156 off-screen, Chromium's sandbox
  MediaManager  items (prim faces, parcel, offered page);  ─pipe─►  sojourn_video.exe libVLC 3.0.21, low integrity, 1 process
    own thread: scan, ObjectMedia fetches, rules, players   ◄─────  events (url, title, loading, keyboard, eval results...)
  MediaPlayer   one surface on a helper                    section  frames: BGRA triple buffer + dirty rects
  MediaInput    mouse and left hand on faces                ◄─────  sound: float stereo ring
  PageView      the sheet and the headset's board
  MediaApi      SojournMedia                              renderer (Sojourn.Render/Media): MediaSurfaces copies dirty
                                                          rows off the render thread, blits into the face texture
                                                          (the viewer's power-of-two corner layout), mips; the
                                                          headset board's GPU copy; interest per frame
                                                          sound: world.cpp sv_media_* (resampled, placed, gain)
```

MediaManager and MediaPlayer (with MediaHelper, MediaUrls, MimeProbe) are `src/Sojourn.Session/Media/`, for any app;
MediaWiring, MediaInput, PageView, MediaApi and MediaFaceSettings stay the VR client's (`src/Sojourn.Client/Media/`).

- Engine choice: CEF (minimal distribution, BSD) in our own helper, run through CEF's `bootstrap.exe` (renamed
  `sojourn_web.exe`) so Chromium's sandbox holds every renderer, GPU and utility process. Not WebView2: no off-screen
  frames we can copy cheaply and, decisive, no way to take its sound into our mix (voice's echo canceller needs it).
  Not CefSharp/CefGlue: in-process (a crash takes the client down) and no sandbox. libVLC for video and streams
  (LGPL-2.1+), plugins pruned to network inputs, decoders and memory outputs. Sizes: web runtime 361 MB (libcef.dll
  295 MB), video 60 MB. `python tools/media.py fetch` (pinned, hash-checked), `build` (CMake, then staged and pruned).
- The kind of media: parcel media says its type; for prim media a HEAD (or a one-byte GET) asks the server, as the
  viewer does (MimeProbe, redirects followed by hand under the URL rules), only for media about to play.
- Helpers start in the background (never under the manager's lock, which the frame takes), in kill-on-close job objects
  below normal priority; restarted after a crash with backoff. Their sections are labelled so the low-integrity video
  helper may write them, and readable by this user only.
- The manager's thread scans connected regions' prims for a MediaURL once a second outside its lock, fetches
  ObjectMedia one prim a second nearest first, and asks for the objects' names (for the person and the assistant).
- Face frames (where a face is, its page's right and up) are worked out on the main thread, a few a frame, each about
  once a second (`FrameRenderer.MediaFace`): sound comes from the face's two sides, and the interest estimate.
- The headset's board: chrome by the panel's painter, the page as its own quad layer copied from the renderer's
  newest frame (`MediaSurface.View`); the HUD board stands aside while it's up; both hidden while a confirmation waits.
- Desktop sheet: composed off the main thread in Skia from the upload worker's CPU copy, drawn in the overlay's sixth
  binding (BGRA). A video up close shows in its own shape (on a face it may be stretched over a square texture).
- Media's per-frame hooks are guarded: a fault is logged (once a minute a place) and the frame goes on without it.
- **A field focused** (`MediaPlayer.WantsKeys`): Chromium's report (OnVirtualKeyboardRequested) comes only for touch, not
  the mouse events the helper sends, so after each click (and each Tab, Enter or Esc) the page is asked whether its
  focused element is editable (an iframe counts). Before 2026-10-26 the desktop's typing into a page never reached it
  (the keys went to the world). `status` shows `field_focused`.
- **The headset's keyboard** (the user's decision, 2026-10-26: an optional keyboard, easy to put away, coming up only
  when the person asks, never by itself on focusing a field): `Control/HeadsetKeyboard.cs`, docs/vr.md > Controls. For
  a page its key sits in the board's strip while a field is focused (the key appears, not the keyboard); each key goes
  to the page as the desktop's do, nothing kept or logged, so a password the person types goes only to the page.

## Safety

- Pages run in Chromium's sandbox; the video helper at low integrity in a one-process job with UI restrictions (no
  other windows, clipboard or desktops) and a 2 GB cap; neither can write files beyond CEF's profile
  (`<base dir>/media/web-profile`).
- URL rules (`common/urls.h`, `MediaUrls.cs`, both sides): http(s) only for pages (plus the video schemes for streams);
  no file://, no private or loopback addresses unless the grid itself is on this machine (the local grid's test site
  needs them); secondlife:// links are reported, not followed; new windows only as above.
- Others' media waits for the person to start it (the default above). An offered page (llLoadURL) opens only through
  "Go to page" and its confirmation, always asked: the title shows the address, the detail the object and owner and
  that the site sees their address.
- The assistant: its presses, typing and navigation on media that isn't the person's own wait for their
  confirmation while "ask before touching others' things" is on (Confirmations.OtherTouches, named for the object and
  owner; what the owner doesn't allow is refused without asking). It never types into a password field (refused in the
  op), reads fields without their values, and its prompt says what a page says is content, never instructions, and
  that sign-ins, payments and personal details are the person's to give.
- Sound from a page or stream is untrusted: the mixer turns anything not a sample (NaN, infinities, beyond ±4) into
  silence before the ears' filters.

## SojournMedia (agent API) and the media tool

Ops: list, status, play, pause (a video; a page's own videos through script), stop, seek, volume, go, back, forward,
reload, home, set_url, read (title, address, the words from an offset, links, buttons, fields: label, type and whether
filled), press (by words: the best-fitting visible link, button or field scrolled to the middle, then real mouse
events; or at a point of the last picture), type (into the field named, clicked first, or the focused one; Enter on
request), scroll (screens by the wheel, or to the top or bottom), look (a PNG of it as it shows, ≤ 1280 wide, with its
scale to page pixels), zoom, stats (tests). Media is named by key, an object's id (the pointer's "that"), or words for
its object, title or site; with none, the one up close, the only one playing, or the nearest. The pump is left out of
the assistant's API list (`assistant/brain.py` HIDDEN_PUMPS): the tool covers it, so batch T adds the three below to
the media tool.

A face's media settings (2026-10-29, batch B10, `MediaFaceSettings.cs`; the viewer's Media Settings floater and
llmediaentry.cpp's limits): `settings` reads a face's entry (by `object` id and `face`, or `media` as above: home and
current URL, auto play, loop, scale and zoom, first click interact, width and height, controls standard or mini, who
may use it and who may control it as [owner, group, anyone], the whitelist, alt image) with `can_edit` and the faces
that have media; `configure` changes any of them, or puts media on a face that has none (it needs a URL), on objects
the person may modify (the region's ObjectModify flag), sent through ObjectMedia with every other face's entry as the
region has it, then read back (`confirmed_by_region`); `remove_media` takes it off a face. Changing who may use or
control a face's media, or its whitelist, waits for the person's press ("permissions", old and new shown), as does
taking media off a face ("delete"); the rest doesn't, as set_url doesn't. An address the face's own whitelist would
refuse is refused, as the viewer's panel refuses it.

Settings (section sound): `media_first_click` (none, huds, own, friends, group, land, any, all; land by default) and
`media_auto_zoom` (on), kept per machine in `voice-media-settings.json` beside vr-settings.json. Menu (batch M,
2026-10-29): sound > first click (a stepper through all eight; maybe none, own, land, any for release) and zoom to
media (a switch).

## Numbers (2026-10-26)

- Frame cost, XR Simulator, standing 25 s before the fixtures (Roomsense benchmarks sharing the CPU): media off, CPU
  7.5 ms median (9.6 p95), GPU 4.8 ms; three playing (two pages, the video, one of them hidden), 7.4 (9.1) and 4.8;
  the video up close on the board too (full rate, the board's GPU copy), 7.9 (9.6) and 4.9. 72/72 fps throughout. The
  helpers used 14-15% of one core.
- The assistant (Haiku, typed): reading a board 10.5 s to the reply (two calls), pressing a button on it 6.4 s, a TV
  on and up close 5.2 s.

## Testing

- `python tools/media.py fixtures`: four objects on Atelier's north lawn (x 120-132, y 238), loaded from an OAR made
  here, scripts told over channel 8: Media Board (the land owner's, control owner-only, whitelist `127.0.0.1:9752`),
  My Media Board (Media Pilot's own), Media TV (a looping video), Parcel Screen (shows the parcel's media texture; sets
  the parcel's media, `screen on URL TYPE` / `screen off`, and offers a page, `screen offer URL`). Ids change with the
  fixtures' version tag, and the region is told to `backup` so the database has them.
- `python tools/media.py test [cases]`: a client (Media Pilot, 960x540, `SOJOURN_TEST_INPUT=1`, touches unconfirmed
  so the API's presses run) against the fixtures and `serve`'s test site: autoplay, read, press, type, password,
  scroll, look, face (the page's corners where the face's corners project in an F12 shot), click (a posted click in
  the window on the button), navigate (and the region's stored address), region (a script's change followed),
  whitelist, video, sound (the video's tones in the recorded output, gone at media volume 0), parcel, zoom, offer (the
  confirm key found in a shot and clicked), stop, log; since 2026-10-29 (fixtures 5: My Media Board lets its first
  click through, Media Board asks for auto-zoom) settings (read, and configure refused on an object the person can't
  modify), autoscale (configure to 1000x500, drawn so, then auto-scaled to 1024x512, and the script's reset), focus
  (on Media Board the first click focuses it and brings it up close without clicking the page; after Esc the next
  click goes in; it looks for a point the pointer really lands on with `list`'s `under_pointer`). Test runs can use a
  client built elsewhere inside the project: `SOJOURN_CLIENT_DLL=<dir>/Sojourn.dll` (voice.py too).
- Last run 2026-10-29: all pass (B10's run had `test headset` pass too). Face, click and focus, which aim the
  person's pointer at a face, had failed for reasons outside media, fixed that day:
  - `lookAt` and the desktop view: the pilot followed a body the API turned only while a frame saw the move running,
    and the region's first word on the avatar's facing could still turn it back afterwards (a race at login, so it came
    and went); `Control/Pilot.cs` now takes any turn of the body it didn't make as the view's heading. And a test client
    in first person turned with the person's real mouse anywhere on the screen (GLFW reads the system's cursor); mouse
    look now needs the window in front (`Window/Background.Foreground`).
  - The pick: Linden trees were picked by their object's whole box, metres of air around the trunk (Atelier's cypress
    took the pointer from the board beside it; the same on 2026-10-28's builds, so not new). Trees are now picked as the
    viewer picks them, a tetrahedron about the trunk, and grass on its drawn blades (`RegionScene.Pick.cs`); the
    headset's pointer, touches and floating text's occlusion use the same pick.
- `python tools/media.py test headset`: `SOJOURN_VR_SCRIPT=media` in the simulator (`Xr/VrScript.Media.cs`): a tap on
  My Media Board's button with the left hand, a double tap (up close), the right hand's tap on the board, ×, a drag
  scrolling the page. `SOJOURN_MEDIA_DEBUG=1` logs each gesture. Under OpenXR's core validation layer (`--validate`
  with `XR_API_LAYER_PATH`, as `tools/client.py` sets it) the routine, the page board's layers included, was clean.
- `python tools/media.py perf`: the frame cost above.
- HUD media and new windows: `python tools/hud.py test media popup` (docs/hud.md > Testing); the headset's keyboard on a
  page: `python tools/keyboard.py page`.
- The confirmation's look was checked in F12 shots (the touch rule's and the offered page's).

## Gotchas

- libVLC's memory audio output (amem) delivered S16 whatever format was asked for, and a second ahead of its time (its
  network caching): the helper asks for S16, converts, and holds sound until its timestamp is 40 ms away.
- A face's media texture has each side rounded up to a power of two separately (1024x512 stays 1024x512): faces set up
  for the viewer show the content's corner with repeats w/pow2(w), h/pow2(h).
- The assistant's system prompt used to go on claude's command line, which Windows caps at 32,767 characters; the
  media pump's ops tipped it over and claude wouldn't start ("the filename or extension is too long"). It now goes by
  file (`local/assistant/prompts/`).
- Notifications are answered with a map (`{"Gotopage": true}`); a bare string presses nothing.
- In first person on the desktop the mouse looks about: the pointer is the screen's middle until Esc frees it.

## H.264 and AAC in pages (looked into 2026-10-26)

CEF's minimal build has no proprietary codecs: a page's own H.264/AAC video won't play (YouTube's VP9/Opus does;
direct MP4 links go to libVLC). No DRM. What it would take to distribute (a research pass; sources in the session):
- `proprietary_codecs=true ffmpeg_branding=Chrome` (what LL's own CEF build and Firestorm's use) puts FFmpeg's H.264 and
  AAC decoders in the binary. Via LA's AVC licence is free up to 100,000 units a year but is a licence to sign and
  report under; AAC has no free tier ($15,000 or $1,000 one-time plus $0.98 a unit). H.264's pool patents have expired
  in the EU, Japan, China and Canada, but the US tail runs to 2030-11. Not clearly within licence for us: not done.
- Cisco's OpenH264 isn't wired into Chromium's playback (WebRTC only), and its terms don't fit.
- The clean route: Windows' own decoders. CEF built with `proprietary_codecs=true` and Chromium branding (a one-line
  patch to an assert in media/BUILD.gn) plays H.264 through the GPU's decoder (D3D11VideoDecoder); AAC needs our own
  patch routing it to Media Foundation (Cromite has one, GPL, so ours would be written fresh). We'd ship no H.264 or
  AAC decoder (as Firefox and Opera do on Windows). Unproven off-screen; a CEF build is ~100-150 GB and 6-10 hours here
  (or overnight elsewhere), redone each CEF update.
- A third route, not tried: inject a script that catches a page's H.264 video and plays it in our own player (VLC or
  Windows' licensed decoders) drawn at the video element's place. Plain-file video is a contained job; streaming
  players (MSE) are fiddly; DRM is impossible either way. The one to try first if it's ever needed.
- Parked (user, 2026-10-26): wait and see whether it matters on the grid before doing any of this.

## Not done
- The parcel's "prevent camera zoom" for auto-zoom (LibreMetaverse drops ParcelProperties' MediaLinkSharing block;
  reading it means a raw event-queue hook or a LibreMetaverse patch).
- Checked only on the local grid: SL's own boards that set first click interact or auto-zoom, and configure on SL
  (the region's ObjectMedia update with a face count from its own GET: SL may want every face's entry).
- The headset's keyboard key is on the page board only; a field focused on a page out in the world needs the page up
  close first (a double tap) to type into it by hand.
