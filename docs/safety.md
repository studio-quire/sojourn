# Safety and comfort (batch B1)

Written by Claude Opus 5.5, 2026-10-27. Agent-facing. What Sojourn does against griefing, clutter and forced motion,
after what the official viewer and Firestorm do (read from their sources: secondlife/viewer develop and
FirestormViewer/phoenix-firestorm master, 2026-10-27; files named below), how the person and the assistant use each,
and what's left. The way out (stand up and go home: `Control/WayOut.cs`) and abuse reports are older
(`docs/vr.md` > Controls, `docs/assistant.md` > Reporting abuse).

Where: Core's `Agents/Safety.cs` (derender, blacklist, release keys), `Safety.Antispam.cs`, `Safety.Bumps.cs` (bumps
and the sounds heard lately), `Agents/MoveLock.cs`; the client's `Control/SafetyWiring.cs` (settings, the menu's safety
section, the hooks), `Control/SafetyApi.cs` (SojournSafety), `Control/Comfort.cs` (the vignette) with its layer in
`Xr/XrSession.cs` (SetVignette), `Control/VrPilot.Aim.cs` and `Control/Aim.cs` (aiming). Hooks in shared code:
`AgentSession.Events.cs` (antispam's checks), `Audio/WorldSounds.cs` (Refuse, Silence), Render's
`Particles/ParticleBuilder.cs` (Off, Hidden, Drawn), `VrPilot.cs` (HoldRoom at the carries, AimFrame after the camera).

## Derendering and the asset blacklist

What Firestorm does (`fsassetblacklist.cpp`, `llviewermenu.cpp` derenderObject, `llviewerobjectlist.cpp`
processObjectUpdate, `llviewermessage.cpp` is_sound_blacklisted and process_avatar_animation): Derender on an object
kills it locally and blocks every later update for its id, until the next teleport (`FSTempDerenderUntilTeleport`, on
by default) or for good ("Derender Permanently", kept per account in `asset_blacklist.xml`); you stand up first if you
sit on it. The same list holds sounds (by asset, or an owner's worn, rezzed or gesture sounds: its WORN, REZZED and
GESTURE flags) and animations (not played on others, stopped on yourself). The official viewer has only "Block
particle owner" (a mute; muted owners' particles aren't simulated, `llviewerpartsim.cpp`) and the Particles rendering
type switch.

Here: `SojournSafety derender {id, permanent?}` takes the object (a linkset by any prim, or something someone wears) out
of the shared world model as a kill would, and every later update for it or a prim linked to it is taken out again
(`Safety.OnWorld`, on the world model's change events). So nothing sees it: the renderer, the pointer and touches,
world sounds, floating text, media, the physics shapes, and the agent API's object lists. Until the next teleport, or
for good in `asset-blacklist.json` in the account's state folder. `rerender {id | all}` asks the region for every prim
of it again (RequestMultipleObjects), so it's back at once, not at its next change (Firestorm waits for that). People
aren't derendered here: `hideAvatars` and `SojournAvatars render never` are the tools for people (the op says so).
`blockSound {sound}` or `{owner, from: worn|rezzed|gestures|all}`, `blockAnimation {id}`, `unblock {id}`, `hidden` (the
list). A sound newly blocked stops at once if it's playing (`WorldSounds.Silence`). `sounds` lists what was heard
lately with its object, owner and how (Firestorm's Sound Explorer), so "block that noise" has something to go on.

Particles: `particles` (setting) off draws none (the viewer's Rendering Types > Particles); a blocked owner's or object's
never draw (the viewer's rule: Sojourn didn't keep it before). `SojournSafety particles` lists who's emitting nearby and
how many particles the last view drew; `blockParticleOwner {id}` blocks the owner of a source, as the viewer's Block
particle owner does, which blocks them entirely (chat, IMs, offers, menus, sounds); to hide one emitter, derender it.

## Antispam

Firestorm's `NACLantispam.cpp` (with its callers in `llviewermessage.cpp`, `llimprocessing.cpp`; settings
`UseAntiSpam`, `_NACL_AntiSpam*`): per kind, each source may send 10 in 4 seconds (sounds and preloads 6 times as
many); past that it's blocked on that kind, silently, until the person changes region (purgeAllQueues), and a notice
says so once. A message of more than 70 lines blocks its sender's messages at once. Kinds and keys: nearby chat (an
object by its id, a resident by theirs), everything that travels as an IM (IMs, offers, teleport offers and requests,
friendship offers, objects' IMs) by the sending resident, script menus and questions by the object, sounds and
preloads by object and by owner. The person's own objects don't count. Off by default in Firestorm.

Here the same rules (`Safety.Antispam.cs`, checks at the mute checks in `AgentSession.Events.cs` and in world audio),
off by default as there (the user, 2026-10-27), with one difference: menus count by their owner too, so a linkset of prims sending one each counts together (Firestorm's per-object key misses that). llLoadURL
offers count as menus and are now dropped for blocked objects and owners, as the viewer's process_load_url does (they
weren't). The notice is AntiSpamBlocked on the feed. `SojournSafety spam` lists the blocks, `unspam {id | all}` lifts
them; the `antispam` setting switches it.

## Keep me in place

Firestorm's movelock (`fslslbridge.cpp`, the Avatar menu's Movelock, `RelockMoveLockAfterMovement`,
`RelockMoveLockAfterRegionChange`): its LSL bridge, a scripted object it makes and wears, holds the avatar with
llMoveToTarget; let go while you move, taken up where you stop; on OpenSim it's phantom instead. Sojourn wears nothing
of its own, so the viewer's own means stand in (`Agents/MoveLock.cs`, the `keep_in_place` setting, off by default):
where the avatar comes to rest by the person's doing (their stick or keys, the walk to them, a sit, a teleport, the
assistant walking them) is the place, and when something else moves it off by over half a metre it's brought back
once the shove ends. In the headset the room isn't carried by motion the person didn't make while it holds
(`VrPilot.HoldRoom` at the rig's carries, `docs/vr.md` > The rig): the view stays where it was and is the place; the
rig's own walk to the head brings the avatar back from near, and from over 8 m, once the shove is over, it's
teleported to the view (`MoveLock.HeadsetTick`, `ViewPlace`; the rig's walk looks like the person's own moving, so the
desktop's resting-place anchor can't serve here). On the desktop it's walked back by
the viewer's autopilot, or teleported back within the region (twice failing, or from over 30 m; at most every 5 s; where the region refuses direct teleports, where it ended up becomes the place).
It undoes a push rather than resisting it as a script would; a moving floor counts as being moved too.

## The comfort vignette

Neither viewer has one. `Control/Comfort.cs` (`comfort_vignette`: off by default, forced or all; `vignette_strength`; comfort aids are options and the defaults
suit experienced VR players, the user, 2026-10-27):
while the view moves without the person's body moving, its edges darken toward the middle. Forced: the room carried by
what they didn't do (a push, a moving floor, a seat or vehicle carrying and turning them, the agent API walking them);
all: the stick's walk and smooth turns too. Snap turns, cuts of the view (teleports, putting you back) and recentring are jumps, not motion. Stronger with speed
(0.25-2.5 m/s) and turning (8-60 deg/s), in over 0.12 s and out over 0.35; at strength 1 and full motion the clear
middle closes to 30 degrees. It's an OpenXR quad layer locked to the head (in the view space), under every other layer
so the menus stay clear, one picture made once and resized with the strength; the PC's mirror window shows it too. The
console says when it comes and goes.

## Aiming: weapons and mouselook games

The viewer in mouselook (`llagentcamera.cpp` changeCameraToMouselook, `llagent.cpp` pitch and leftButtonGrabbed,
`llviewerinput.cpp` script_trigger_lbutton): AGENT_CONTROL_MOUSELOOK in every AgentUpdate, the agent's frame pitched
with the camera (so llGetRot and llGetCameraRot point where you aim), a click sent as CONTROL_ML_LBUTTON to a script
that took it, and no touching.
The headset is always first person, but until now never told the region so. `Control/VrPilot.Aim.cs` (`aim`: auto, on,
off; `aim_with`: head or hand): auto aims while a script holds the mouselook button (CONTROL_ML_LBUTTON: guns, combat
HUDs, shooting games); on aims always (for scripts that only look at AGENT_MOUSELOOK; touching waits meanwhile, as in
mouselook). Aiming: the mouselook flag goes to the region, the body and head turn to the aim with its pitch (the head's
forward, or the right hand's ray), aiming with the hand puts the camera's axes along it too; the right trigger is the
mouselook button, down while held (`Control/Aim.cs`), except on a menu, board, key or the panel; a crosshair (a broken
ring and a dot, warm while pressed) sits where the aim lands, at that depth. Others see the avatar face the aim, as
they see anyone in mouselook (TPV policy 2.k: nothing the viewer doesn't send). Firestorm's extras (target markers,
mouselook zoom) aren't done.

## The way out

The left menu button held 0.8 s, or Ctrl+Shift+H (`Control/WayOut.cs`, older than B1): releases controls a script
took, stands you up, and sends the viewer's Teleport Home (TeleportLandmarkRequest with no landmark). What came of it
is read from the protocol (`Core/Agents/TeleportWatch.cs`, Claude Opus 5.5, 2026-10-28), not LibreMetaverse's
GoHomeAsync, which never heard a refusal (one comes without a TeleportStart, and it drops those) and refused to start
while an earlier teleport still looked under way: on the main grid that night it said "the region didn't send us
home" twice. Now: home reached, done; the grid refuses (its reason in plain words, Second Life's keys such as no_host
put into words: `TeleportWatch.Plain`) or doesn't answer in 15 s (45 once started), then the person hears why and goes
to the latest places in their teleport history in other regions instead, up to three ("Couldn't go home: ... Going to
X instead"); the grid sent them home and the region there never answered (the night's case: no ACK to the
UseCircuitCode for 60 s), then after 30 s the session logs in again, where the grid has put them, and says so
(`TeleportStalled`). A second press while one is on its way says "Still on the way home". `CircuitGuard` makes that
last case rare: LibreMetaverse sent the new region's UseCircuitCode once, so one lost datagram stranded a teleport;
it's now resent every 1.5 s until acknowledged (`docs/client.md` > Gotchas). Checked on the local grid (an account
with no home, a home in a far region, that region taken down, its circuit's datagrams lost for 1.5 s and for 45 s:
`SOJOURN_CIRCUIT_LOSS`): each as above.

## Release keys

The viewer's Advanced > Release Keys (`llagent.cpp` forceReleaseControls: ForceScriptControlRelease) takes back every
control scripts took. `SojournSafety releaseKeys`, the menu's safety section ("controls a script took: release", shown
while some are taken), and the way out now does it too before standing you up (a stuck vehicle or weapon keeping the
sticks). `SojournSafety status` lists the controls taken.

## Bumps, pushes and hits

The viewer's Bumps, Pushes & Hits floater (`llfloaterbump.cpp`) lists the region's MeanCollisionAlerts for the session;
Firestorm can also put them in nearby chat (`FSCollisionMessagesInChat`, off by default; its strings are these).
`Safety.Bumps.cs`: kept for the session (`SojournSafety bumps`), and on the feed as a quiet notice (CollisionAlert) for
pushes and hits by default (`bump_notices`: pushes, all, off; bumps are anyone walking into you), at most one per person
a half minute, with how many came since. OpenSim never sends these (only Second Life does), so it's unchecked here.

## The person's controls

Settings menu > safety: comfort vignette and its strength (headset), keep me in place, aim and aim with (headset),
particles, antispam; release (while a script holds controls) and "N hidden: show again" (while anything is
derendered). Every one is also SojournSettings (section safety) for the assistant. Hiding a particular thing is by
asking ("hide that", "block that noise", "who's making these particles?"): the pointer gives the assistant its id.

## Incognito

None of SojournSafety's ops are on incognito's allowlist yet (batch T classifies new ops), so while it's blind they're
refused: the safe default. What's added during a spell is never written down (a permanent derender lasts until the
next teleport), and the lists (hidden, sounds, spam, bumps) show a spell's entries only while it lasts and sees, and
drop its names afterwards.

## Testing (tools/safety.py)

`python tools/safety.py fixture | test | headset`: the Safety Rig in Atelier at 74-80, 36-44 (owned by Sojourn Friend; the
tester is Sojourn Safety on :9765, :9766 in the simulator): the Spammer (twelve linked prims that flood on command on
channel 7), the Lamp (a looped sound and particles while told "lamp on") and the Seat (takes the forward key and the
mouselook button, dances you, says what it hears; "ride" carries it round a 4 m square). Sounds play at volume 0.

Results (2026-10-27, local grid, both runs whole and passing): desktop: derender (gone from the world and the
particle sources; back on rerender; back after a teleport; kept in the file for good and off it again), antispam (chat
10 of 15, the 80-line message held, IMs from twelve prims 10 of 12, menus 10 of 12, sounds 60 of 70, one notice each),
particles off and a blocked owner's (to 0, back on unblock), the lamp's loop listed as playing, blocked, stopped,
unblocked, keep me in place (a hard push teleported back, a small one walked back), the seat's controls listed, the
dance blacklisted and stopped, release keys letting go of both. Simulator: keep me in place (pushed 31 m, the view held,
no vignette, teleported back to the view), a push without it bringing the vignette up (0.87), aiming on the seat
(mouselook reached the region, the right trigger's down and up reached the script), the seat's ride carrying the avatar
round its square with the vignette at 0.97 (none with it off), not aiming once standing. Held at full
(`SOJOURN_VIGNETTE=1`), the mirror window's shot shows the edges darkened.

Fixed while checking: the seat's ride (llSetKeyframedMotion without KFM_DATA reads its list as vector, rotation, time;
OpenSim stopped the script on it, which is what failed the seat's steps after); keep me in place in the headset
measured from the rig's resting place, which the rig's own walk to the head kept moving (now the view is the place,
`ViewPlace`), and a far throw (over 4 m between updates) was carried like a teleport (now only a jump without speed is).
The test's own teleports were refused as "teleport started" right after pushes in the headset; the headset run no
longer teleports, and the desktop's retry.

## Not done

- People derendered (Firestorm's AT_PERSON): hideAvatars covers it, but isn't kept between sessions.
- Firestorm's per-setting antispam limits and its global queue; bumps on the local grid (OpenSim sends none).
- Aiming: Firestorm's target markers and mouselook zoom; a gun worn as an attachment checked (the Seat stands in).
- The menu's release row appears when the menu next redraws after a script takes controls.
- Local teleports right after a hard push sometimes report "teleport started" and fail on OpenSim (seen from the test,
  cause not traced); keep me in place's teleport back retries after 5 s.
- incognito's allowlist entries and the assistant's own tools for these (batch T).
