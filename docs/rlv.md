# RLV (RLVa)

Written by Claude Opus 5.5, 2026-11-01. Agent-facing. RLV (Restrained Love) is the protocol roleplay collars, cuffs,
relays, furniture and HUDs use to restrict the person who wears them: scripts send `@behaviour[:option]=param` lines
by llOwnerSay and the viewer applies them. Sojourn does what Firestorm's RLVa does (its `rlv*.cpp`, `rlva_strings.xml`,
the chat path in `llviewermessage.cpp`, read 2026-11-01), so existing content works. First of the "Decided, to build"
items (the user, 2026-11-01; `docs/viewer-gaps.md`).

## Where

- Core `Rlv/`: the engine. `RlvCommand.cs` (RLVa's parse), `RlvDictionary.cs` (every RLVa behaviour, force and reply
  command, option types, `_sec` strictness, synonyms, modifiers), `RlvEngine.cs` (holders, counts, exceptions,
  modifiers, @clear, @notify, the garbage collector), `RlvEngine.Checks.cs` (RLVa's RlvActions: may the person do X,
  and if not, why, naming the restriction and the object), `RlvEngine.Commands.cs` (force and reply commands, @standtp,
  @notify's world news), `RlvEngine.Wear.cs` (attachment, point, layer and folder locks; the #RLV folder; force wear),
  `RlvOps.cs` (the gate over every op), `IRlvHost.cs`, `RlvNames.cs` (points, layers, groups).
- Core `Agents/AgentSession.Rlv.cs`: commands from owner-say, chat and IMs coming in, offers, script questions, the
  name and place filter, the watch (sitting, wearing, objects gone, flying), the host the engine acts through.
- Session `Rlv/RlvWiring.cs`: the `rlv` setting, SojournRlv, the safety section's rows.
- Client `Rlv/RlvView.cs` and Render `Frame/Renderer.Spheres.cs`, `Shaders/rlv_sphere.frag`: the vision spheres.
- Hooks in shared code, one or a few lines each: `LeapDispatcher.cs` (LeapApi wraps every op), `AgentSession.Events.cs`,
  `.Touch.cs` (SendSit, SendGrab), `.Actions.cs` (TeleportAsync), `Appearance/Wardrobe.cs`, `StockApis.cs` (sendChat,
  requestSit), `GestureOps.cs`, `WayOut.cs`, `SocialFeed.cs`, `ConsoleChat.cs`, `Radar.cs`, `SessionHost.cs`,
  `VrSettings.cs`, `Allowlist.cs`; the client's `VrPilot*.cs`, `Pilot.cs`, `StandKey.cs`, `NameTags.cs`,
  `FloatingText.cs`, `MapBoard.cs`, `WristLine.cs`, `Program.cs`, `SafetyWiring.cs`, `MenuGuide.cs`.

## On and off

`rlv` (SojournSettings, safety; the menu's safety section, advanced): off by default, as Firestorm's RLVa, and a change
counts from the next login (the menu's note says so). Off, `@` lines from objects show in chat as any owner-say, as in
Firestorm with RLVa off. `SojournRlv status`: on, what's held by which object (and where it's worn), what the person
can't do now and why, the vision effects. It issues nothing: restrictions only ever come from the person's own objects.

## Commands

Taken from llOwnerSay by any object the person owns (worn, a temporary attachment, or rezzed in the world), starting
with `@`, lowercased, comma-separated, and kept out of chat, the feed, the log and the assistant's reading. Each one
reaches @notify listeners as RLVa sends them (`/tploc=n`). Replies go out as RLVa sends them: a shout from the avatar on
the channel asked (positive, not the debug channel), whatever @sendchannel holds; a failed reply still answers, empty.

- **Versions**: `@version` "RestrainedLife viewer v3.4.3 (RLVa 2.4.2)", `@versionnew` "RestrainedLove viewer v3.4.3
  (RLVa 2.4.2)", `@versionnum` 3040300, `:impl` 2040213: what Firestorm reports, so scripts' RLVa checks pass.
- **Holding and clearing**: n/add and y/rem per object, counted; options as RLVa takes them (none, an exception's id, a
  modifier's value, or both); `_sec` (strict) forms, `@permissive`; @clear and @clear=filter (by text, as RLVa);
  `@getstatus[:filter[;sep]]`, `@getstatusall`, `@getcommand`; @setcam, @setdebug, @setenv held by one object at a
  time. An object's restrictions go when it does: detached, or not in the world for the garbage collector (every 30 s;
  seen and then missing for a minute, never seen after about ten minutes), as RLVa's onDetach and onGC. Across a
  teleport or crossing everything worn leaves the world model for a few seconds, so neither counts until the avatar has
  been in the region 15 s and the thing has been missing 3 s (RLVa drops a missing object at its next pass).
- **#RLV**: the folder at the inventory's top (several: the first with folders), fetched at login and every 20 s;
  paths by step, exact first, then the first non-hidden folder containing it. `@getinv`, `@getinvworn` (|XY codes),
  `@findfolder(s)` (`&&` terms), `@getpath(new)[:point|layer|id]`; `@attach`, `@attachover`, `@attachall*`,
  `@attachthis*`, `@addoutfit*`, `@...overorreplace`, `@detach:<folder>`, `@detachall`, `@detachthis*`,
  `@remattach[:point|group|id|folder]`, `@remoutfit[:layer|folder]`, `@detachme`. Attachment points from "(chest)" at
  the end of an item's name or a ".(chest)" folder (folded into its parent), no-modify items the folder first; items
  naming none go to their own point (RLVa's shared wear, on by default there); "+" folders add; "nostrip" never comes
  off by command; body parts never.
- **Locks**: `@detach=n` (the object itself), `@detach:<point>`, `@addattach`/`@remattach[:point]`,
  `@addoutfit`/`@remoutfit[:layer]`, `@attachthis`/`@detachthis` and their `all` and `_except` forms (by the object's
  folder, a point, a layer or a path), `@sharedwear`/`@sharedunwear`, `@unsharedwear`/`@unsharedunwear`. Held for every
  way of wearing (Wardrobe: the assistant, Access, outfits, RLV's own force wear); replacing an outfit leaves what's
  locked on; `@getattach`, `@getoutfit`, `@get(add|rem)(attach|outfit)names`.
- **Moving**: `@tploc`, `@tplm`, `@tplocal[:m]`, `@tplure[:id]` (an offer refused with RLVa's busy line, or turned away
  when answered), `@accepttp[:id]` (taken at once), `@tprequest`, `@accepttprequest`, `@sittp[:m]`, `@standtp`,
  `@sit`, `@unsit`, `@sit:<id>=force`, `@sit:<null>`/`@sitground=force`, `@unsit=force`, `@tpto:x/y/z[;angle]` and
  `region/x/y/z` (a forced teleport, as RLVa runs whatever @tploc says), `@getsitid`, `@fly` (and landing at once),
  `@fly:true|false=force`, `@jump`, `@temprun` (the desktop's Shift), `@alwaysrun` (the headset's run), `@setrot`, `@adjustheight` (its delta as the hover height). Local means within 256 m,
  as the viewer counts.
- **Chat**: `@sendchat` (chat becomes "...", emotes cut at 20 characters or the first full stop unless `@emote`, short
  /commands and (( OOC )) pass, RLVa's filterChat), `@redirchat:<ch>`/`@rediremote:<ch>` (sent there instead),
  `@chatnormal`, `@chatshout`, `@chatwhisper`, `@sendchannel[:ch]`, `@sendchannel_except:ch`, `@sendgesture` (and a
  gesture's words go through the same rules); `@recvchat[:id]`, `@recvchatfrom`, `@recvemote[:id]`, `@recvemotefrom`
  (what's heard from others and their objects, not the person's own attachments nor llOwnerSay or llRegionSayTo).
- **IMs**: `@sendim[:id|dist]` (the words kept back and "*** IM blocked by sender's viewer" sent instead, as RLVa),
  `@sendimto`, `@startim[:id|dist]`, `@startimto`, `@recvim[:id|dist]` (shown as blocked, the sender told RLVa's
  line), `@recvimfrom`.
- **Seeing**: `@shownames[:id]` (anonyms by RLVa's list and hash in chat, tags, the feed and what ops return; the
  radar silent), `@shownametags[:id|dist]`, `@shownearby` (people lists refused, the radar silent), `@showloc` (region
  and parcel names in what ops return, the location line hidden, offering teleports refused), `@showworldmap`,
  `@showminimap`, `@showhovertext:id`, `...all`, `...world`, `@showinv` (inventory ops refused), `@viewnote`,
  `@viewscript`.
- **Touching and building**: `@touchall`, `@touchworld[:id]`, `@touchthis:id`, `@touchattach[:id]`,
  `@touchattachself`, `@touchattachother[:id]`, `@touchhud[:id]`, `@touchme`, `@fartouch`/`@touchfar[:m]`, `@interact`,
  `@edit[:id]`, `@editobj:id`, `@editattach`, `@editworld`, `@rez`; for the person's trigger and clicks (SendGrab,
  SendSit) and the ops.
- **Other**: `@buy`, `@pay`, `@share[:id]`, `@setgroup`, `@setgroup:<name|id|none>=force` (once a minute per object),
  `@getgroup`, `@acceptpermission` (a request for only the controls, or attaching for an own object not worn, granted at once; the
  rest asked as usual, as RLVa), attach and teleport taken out of script
  requests while locked; `@notify:ch[;filter]` with RLVa's lines (`/sat object legally <id>`, `/unsat ...`,
  `/attached legally Chest`, `/detached illegally ...`, `/worn ...`, `/unworn ...`); `@getheightoffset`,
  `@getdebug_*` (RLVa's few: AvatarSex, RenderResolutionDivisor, its two RLV switches, WindLightUseAtmosShaders),
  `@getcam_*`.
- **Vision spheres**: `@setsphere` with every local modifier (`_mode` blend, blur, variable blur, chromatic
  aberration, pixelate; `_origin` avatar or camera; `_color`, `_param`, `_distmin`, `_distmax`, `_distextend`,
  `_valuemin`, `_valuemax`, `_tween`), up to six at once, as RLVa's RlvSphereEffect and rlvF.glsl.
- **Camera** (the desktop; below): `@setcam_avdistmin/max` (`@camdistmin/max`), `@setcam_fovmin/max`,
  `@camzoommin/max`, `@setcam_fov=force`, `@setcam_mouselook`, `@setcam_unlock`; the rest accepted.

## Every caller

Restrictions hold for the person's own controls, the menus, the assistant, dev mode and Access alike:
- **Every op**: LeapApi wraps each handler (`LeapDispatcher.cs`), so the dispatcher's callers and the paths that run a
  handler directly (Access's own presses, a thing's card, dictation, menus) meet the same check (`RlvOps.Check`), and
  what comes back has the hidden names and places replaced.
- **Lower down**, for what bypasses ops: teleports by name (`AgentSession.TeleportAsync`: the map board, places,
  SLURLs), sitting and touching by the person's hand (SendSit, SendGrab), wearing and taking off (Wardrobe), gestures,
  the headset's stand key, stand by stick, point teleport, fly toggle and jump, the desktop's fly and jump, answers to
  teleport offers and requests, chat and IMs coming in (`OnChat`, `OnIM`).
- A refusal is a quiet notice on the feed (`RlvBlocked`, the same words at most once in 3 s) and the op's error, both
  in the same words: "RLV: teleporting is restricted (@tploc, by 'Leather Collar')". The assistant's prompt says to
  explain and not work around it.

## The way out

The way out (`docs/safety.md`) overrides RLV and stays a sure exit (the user let that stand, 2026-11-01); and, their call
the same day, it lifts no restrictions: they were chosen, so it stands and goes home, and the rest waits for logging
out or `rlv` off. Camera restrictions on the desktop only and the switch in the advanced menu: also their call, as built. Pressed:
- it releases the controls scripts took, stands the person up whatever @unsit says, and asks for home whatever @tploc,
  @tplm, @tplocal, @sittp or @unsit say (it uses the protocol directly, as always, so no RLV check is in its way);
- @standtp doesn't take them back to where they sat from;
- for 30 s nothing moves them back: @tpto, @sit and @sitground force are refused, and @accepttp's offers aren't taken
  by themselves (logged), so a trap's relay or an owner's lure can't pull them straight back in;
- the log says what held them.

Honest, not hidden: nothing is cleared, detached or suspended. Restrictions stay held and enforced after it, and
scripts see what a real viewer would report: @notify's "unsat object illegally <id>" (RLVa's word for a stand the
restriction didn't allow), @getstatus as before. Seeing and hearing aren't the way out's: a vision sphere stays, but the
menus, panel and feed are layers above the world, and the way out needs no sight; chat filters stay. Logging out always
works, and `rlv` off counts from the next login: Firestorm's way out of RLV. Not done, deliberately: Firestorm's
RLVaLoginLastLocation (logging in where you were while restricted), which would turn relogging into a trap.

## In the headset (the adaptations)

- **Vision spheres in each eye**: a pass over each eye's tonemapped image before glow, from that eye's own depth and
  view (`rlv_sphere.frag`), so a sphere is one surface in the world, seen in stereo at its true depth; HUDs, name tags,
  hover text, menus, the panel and the feed draw over it, as the viewer's 2D interface does. The camera origin
  (`_origin:1`) is the middle of the head, not each eye, so both eyes see one sphere (centred on each eye, its edge would
  sit at two depths). Checked in stereo: `tools/rlv.py eyes`.
- **No camera locks or zooms in the headset**: the view is the person's head. @setcam_avdistmin/max, the FOV limits,
  @setcam_fov, @setcam_mode, @setcam_focus and the offsets apply on the desktop only; forcing a headset's view (moving,
  zooming or locking it against the head) is what makes people sick, and the studio's rule is no forced view moves.
  Scripts still get RLVa's replies (`@getcam_*` report the limits held; `@getcam_avdist` 0, as in first person).
- **@setrot** turns the avatar, not the headset's view: there the body faces where the person looks.
- Forced teleports and sits (@tpto, @sit force) are straight cuts of the view, as every teleport is.
- No comfort aid was added (CLAUDE.md > comfort aids).

## Left out

- `@setoverlay` (with `_touch`, `_tween` and its modifiers): a texture over the view; accepted, not drawn. In the
  headset it would be a head-locked layer.
- `@setcam_textures`/`@camtextures` (the world in one texture), `@setcam_avdist` (others as silhouettes),
  `@setcam_eyeoffset`/`_focusoffset`/`_focus`/`_mode` beyond being accepted, `@showself`/`@showselfhead`,
  `@viewtransparent`, `@viewwireframe` (no such views), `@showhovertexthud`, `@viewtexture`, `@allowidle`, `@setgroup`'s role, `@setenv`/`@getenv_*` and `@setdebug_*` (accepted, no effect),
  `@adjustheight`'s pelvis reference.
- RLVa's extras around the edges: IM queries (@list by IM), @stopim, give-to-#RLV (`#RLV/~` folders), commands kept
  from before login, reattaching a locked attachment something else took off, composite folders, legacy naming, the
  typing indicator under @redirchat, Marine's @camdraw* (RLVa doesn't have them either).
- Sojourn Access's own home key (`MainForm.HomeAsync`) asks the protocol directly, as the way out does, so @tploc
  doesn't hold it; whether it's Access's way out is its owners' call. Access's described pictures don't show spheres.

## Testing (tools/rlv.py)

`python tools/rlv.py fixture | test | eyes`. The RLV Rig and the RLV Cuff in Atelier at 60-64, 40, owned by Sojourn Rlv:
both relay their owner's "rig do @..." / "cuff do @..." lines on channel 7 as llOwnerSay, and the Rig tells its owner
what it hears on channel 0 from them and on the reply channels 1001-1006. `test` runs a console host (no GPU) through
versions, @getstatus, @clear, @notify, teleports, sitting, chat out and in, SojournRlv, and the #RLV folder (the Cuff
taken into `#RLV/cuffs` as "RLV Cuff (left hand)", worn by @attach, its hold kept across a teleport to Atelier East (its id stays the same on OpenSim), held by its own
@detach=n against the agent API and
@detach:folder, off when unlocked, its restrictions gone with it). `eyes` (the VR client in desktop stereo, a minute,
`local/gpu.lock`): a sphere clear within 2 m and black from 6 m darkens the far half of both eyes, @clear lifts it; the
way out stands the person up through @unsit, asks for home through @tploc, and @standtp doesn't take them back. Both
passed 2026-11-01. Atelier doesn't let the tester set a home, so the grid refusing home is the end of that path here.

## Checks only Second Life can do

In `docs/sittings.md` and the board: a real OpenCollar (its RLV, owner menu, outfits from #RLV), a worn RLV relay with
real furniture or a trap (and the way out from it), a blindfold HUD's spheres in the headset (comfort), @shownames in a
crowd, IM restrictions with someone, and a collar's hold across teleports (whether Second Life keeps an attachment's
id across regions as OpenSim does: if not, restrictions would last only until the collar sends them again).

## For the user (decisions, each a reversible default)

- The way out stands and goes home, with a 30 s guard against scripts moving the person back; restrictions stay held.
  Should it also lift RLV for the rest of the session (clear, or detach locked items)?
- Camera restrictions are desktop-only; in the headset the view is the head's. Any you'd want there?
- `rlv` is in the advanced menu, as Firestorm keeps it in preferences.
- The OpenCollar/LockMeister switches for the AO need a worn relay object of Sojourn's own; RLV wears nothing of its
  own (it uses the person's collars and relays), so that's a separate job: making, scripting and wearing an object as
  Firestorm's LSL bridge does.
