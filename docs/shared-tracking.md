# Showing head and hand tracking to other people

Written by Claude Opus 5.5, 2026-10-26 and 2026-10-27. Agent-facing. Can other people in SL see the person's head and
hand tracking? Findings are marked **verified** (with the source) or **inferred**. Viewer source was read at
secondlife/viewer `main` (release 26.5, published 2026-10-26) and the Puppetry project branch `DRTVWR-558`.

## The answer

**Partly.** Full tracking (both arms, head, maybe fingers, streamed live) isn't possible on the main grid. It was Linden
Lab's Puppetry, which never left three test regions on the beta grid and which LL now calls discontinued; the release
viewer contains none of its code, so even if a server relayed the data, nobody's viewer could show it. What fits the
TPV policy uses what the official viewer already sends and plays: LookAt for head direction (A), PointAt for one arm
(B), uploaded animations as poses (D, F), or a worn scripted attachment (E).

## Decisions (user, 2026-10-26 and 2026-10-27)

- **Head direction, with a toggle: built.** LookAt as the viewer sends it (A), with eye contact when your view rests on
  someone and a "direction" setting that never names who; others' look-at turns their heads in Sojourn too. How it
  works, the setting and its default: `docs/vr.md` > Where you look, seen by others. It's the only tracking others see.
- **Point-at (B): decided against for now**; it sounded finicky to the user. (Separate and still open on the board:
  pointing the avatar's arm at what the assistant builds or selects, as the official viewer always does.)
- **Parked**, judged within the TPV rules but not wanted now: an arm-pose palette played like an AO (D), and scripted
  segmented arm and hand attachments moved by a script fed over a private chat channel (E's idea, with chat instead of
  HTTP-in).
- **The joint alphabet (F)**, which would supersede D: saved for a possible future attempt (2026-10-27; the studio's
  `ideas/sojourn-joint-alphabet.md`). Its cheap checks come first.
- Watch for LL reviving Puppetry or animation streaming; the seed-cap check under Puppetry would tell us.

## What others see of Sojourn now (verified, repo)

- `src/Sojourn.Render/SelfBody.cs` and `World/Avatars.cs` (`PoseSelf`, `Reach`) draw a local copy of the person's own
  avatar, posed to the headset and hands (eyes under the camera, two-bone IK for the arms from the OpenXR grip pose,
  overwriting only the arm joints after the region's animations; `docs/vr.md`). The region knows nothing of it.
- `Control/VrPilot.cs`: AgentUpdate carries `BodyRotation` (`_bodyYaw`, which turns once the head is 40 deg off),
  `HeadRotation` (the headset's orientation) and the camera, with `Mouselook = false` unless aiming
  (`docs/safety.md` > Aiming). `Control/Gaze.cs` sends LookAt viewer effects; no other ViewerEffect.
- So others see a normal avatar playing its animations or AO, facing the body yaw, its head turned where the person
  looks (unless an animation holds the head), its arms wherever the animations put them.
- Inferred: the sim doesn't relay AgentUpdate's `HeadRotation` to anyone. In the viewer, a remote avatar's head is
  driven only by animations and `LookAtPoint` (`indra/llcharacter/llheadrotmotion.cpp:186`), and avatar ObjectUpdates
  carry only the body rotation.
- Receiving: others' LookAt turns their heads and eyes (`World/Avatars.LookAt.cs`, `Bodies/Animator.cs`). Others'
  PointAt is ignored (the viewer runs its editing motion on their left arm).

## Puppetry

- **Viewer side** (verified, `DRTVWR-558`: `llpuppetmodule.cpp`, `llpuppetmotion.cpp`, `llpuppetevent.cpp`; last merge
  2023-09-11): a LEAP API (`LLEventAPI("puppetry")`: `get` the camera or skeleton, `set` joint IK targets or states)
  fed by a side process the viewer launches (LL's sample was `webcam_puppetry.py`), with a mask of parts (head, face,
  hands, fingers; face and fingers never worked). The viewer IK-solved the targets and blended them as a motion.
  Transport: a `Puppetry` region capability (GET returns `event_size`, ≤255 bytes a window; without it puppetry stays
  off), and the joint events quantised into `AgentAnimation.PhysicalAvatarEventList.TypeData`, received on
  `AvatarAnimation`. A sister feature, `AgentAnimationTracking` (Low 431) behind `REGION_FLAGS_ENABLE_ANIMATION_TRACKING`,
  reported attachment points to the sim for scripts.
- **The release viewer** (verified, `main`): no `llpuppet*` files, no `Puppetry` capability, no
  `AgentAnimationTracking` in `message_template.msg`, no animation-tracking region flag. The `PhysicalAvatarEventList`
  blocks remain, old unused blocks Puppetry borrowed. `GenericStreamingMessage` defines only
  `METHOD_GLTF_MATERIAL_OVERRIDE`.
- **Status** (verified): the last viewer was the project viewer 6.6.12.579958 (2023-05-11), on Aditi only, in the
  Bunraku, Marionette and Castelet regions, and others needed the same viewer to see anything
  (https://releasenotes.secondlife.com/viewer/6.6.12.579958.html, https://wiki.secondlife.com/wiki/How_Puppetry_Works).
  The user group meetings were suspended 2023-07-13
  (https://modemworld.me/2023/07/15/2023-sl-puppetry-project-week-28-summary/). In May 2026 Inara Pey's summary of the
  Content Creation user group called it the "discontinued Puppetry project", and VR tracking "streamed to everyone else
  in a region" "not actually something LL is directly focused on"
  (https://modemworld.me/2026/05/23/2026-week-21-sl-ccug-meeting-summary/).
- Inferred: no Puppetry server support on Agni; whether the Aditi regions still answer the cap is unknown. A cheap
  check: at a main-grid login, add `Puppetry` to the seed capability request and log whether it comes back. Even a yes
  changes nothing for others, whose viewers lack the code.
- Policy: sending Puppetry blocks on Agni would be a protocol departure (1.a) and a shared-experience feature the release
  viewer lacks (2.k). Out, unless LL ships it.

## What fits the policy

The test, from `docs/tpv-policy.md` 2.k and Oz Linden's 2012 gloss (re-read 2026-10-27): no fragmentation, so someone on
LL's viewer must see the same world we do. Controls are explicitly outside the rule, and 1.a allows documented protocol
departures. So sending a message every viewer renders, with values the official viewer itself wouldn't send, is a
controls question, not a 2.k one; 2.k isn't strict send-parity. Everything below uses messages the official viewer
sends and effects it draws for everyone.

### A. Head direction by LookAt (built)

- Verified, viewer `main`: `llhudeffectlookat.cpp` sets every avatar's `LookAtPoint` from its LookAt effect and starts
  `ANIM_AGENT_HEAD_ROT`. `llheadrotmotion.cpp`: the head turns toward the point within 72 deg (0.8 x 90), neck and head
  splitting it and the torso taking 35%; half-lives 0.15 s (head) and 0.27 s (torso); no roll; the eyes follow
  (`LLEyeMotion`); priority MEDIUM (1). `DisableLookAtAnimation` defaults off. In mouselook the official viewer sends
  `LOOKAT_TARGET_MOUSELOOK` with the camera's at-axis (`llagentcamera.cpp:1179`; priority 7, no timeout). At most 4
  sends a second, and only when the target moves over 5 cm.
- Others see the head turning and nodding with the person's, the eyes with it, the torso a little; no tilt. A target on
  another avatar puts the point at their head, which reads as eye contact. Latency inferred ~0.3-0.6 s.
- Policy: plain parity, what a mouselook user sends. People with "show look-at" on see the targets, as with any viewer.
- Limits: head animations at priority ≥2 win (many AO stands animate the head at 3-4), so a heavy AO mutes it
  (inferred).

### B. Pointing with the left arm by PointAt (decided against)

- Verified: `llhudeffectpointat.cpp:397` starts `ANIM_AGENT_EDITING` on the sender in every viewer;
  `lleditingmotion.cpp` is an IK reach of the **left** shoulder, elbow and wrist toward `PointAtPoint` (half-life 0.1 s,
  priority HIGH (2), a relaxed hand), reaching well forward and left and badly right or behind (targets behind a 45 deg
  plane are reflected forward). Same 4 Hz / 5 cm rate. The official viewer points only at objects (selecting, grabbing),
  never at avatars or attachments (`LLAgentCamera::setPointAt`).
- Policy: objects are parity; free space is within 2.k as every viewer draws it the same; pointing at people is what
  the official viewer deliberately refuses, so it's a courtesy question and would be left out.
- Limits: arm animations at priority ≥3 (most AOs) override it. The right hand is Sojourn's pointer, so it would share
  the left hand.

### C. Aim animations (minor)

Verified (`llvoavatar.cpp:6049`): while a built-in gun-aim animation plays (`aim_r_handgun`, rifle, bazooka,
`aim_l_bow`), every viewer runs `LLTargetingMotion`, turning the torso so the right hand points along `LookAtPoint`. A
right-arm "aim where I look" for free, but tied to the head's direction, not the hand's.

### D. A pose palette (parked)

Animations made and uploaded once: arm-only poses for bins of wrist position relative to the shoulder (say 3 heights x
5 directions x 2 reaches, ~30 per arm), and Bento finger poses from the Touch controllers' sensors (open, point,
thumbs-up, fist, pinch); `Avatars.Reach` can generate the keyframes, the .anim writer would be new. Played at high
priority over the AO, eased ~0.2-0.3 s, switched only when the bin changes. Others would see arms in soft steps and
hand shapes, ~0.3-0.6 s late (inferred). Cost: ~70 uploads at the account's `animation_upload_cost` (L$10 per the wiki;
read from the account's benefits at login, `llagentbenefits.cpp`), about L$700. Parity: AOs switch animations
constantly; keep the rate modest (2.f). Generating animations live isn't practical: each is a paid, permanent upload
taking seconds, and SL has no temporary upload others can see (inferred).

### E. Scripted floating hands (parked)

A worn attachment with unrigged hand pieces, its script taking poses over HTTP-in (`llRequestURL`) at ~10 Hz and setting
them with `llSetLinkPrimitiveParamsFast`. Inferred: others see hands moving on their own, steppy at the sim's object
update rate, beside the avatar's own animated arms (or those alpha'd out): VRChat-style floating hands, odd on an SL
body. Ordinary scripted content in policy terms; Sojourn would hide the pieces in its own view.

### F. A joint alphabet (saved for later)

Binning each joint separately instead of whole arms gets close to streaming.
- Verified (`llpose.cpp` `LLJointStateBlender`, `llmotioncontroller.cpp`, `llkeyframemotion.cpp`): every viewer blends
  animations per joint and channel, up to 6 states per joint by priority, and an animation moves only the joints it
  has keys for. Ease weights are `cubic_step` and stay continuous across a stop or restart (`mResidualWeight`), so
  easing one pose out while the next eases in blends the same on every viewer. Uploaded animations can't be additive
  and sit at weight 1 once eased in.
- The scheme: one static asset per pose of a single joint or group (shoulder orientations; elbow bend and twist; wrist;
  hand shapes from the Touch sensors; head and neck with roll; chest lean; pelvis height). Each joint plays its nearest
  pose, a change is a ~0.15 s crossfade, and an arm's poses are the product of its joints' sets while the uploads are
  only their sum. A fine collar set (±10° in small steps) over a coarse shoulder set refines the upper arm, for a 1-2 cm
  shoulder shift within real clavicle range.
- Budget (inferred): ~300-450 poses per arm and ~50-100 for head, torso and pelvis, ~700-1000 in all, ~L$7-10k once.
  Tracked joints at priority 6 over the AO, stopping (back to the AO) when tracking gives way or the hands rest.
- Limits: 30 animations per avatar, the AO and built-ins included (~10-12 held, double briefly during crossfades: tight
  but fits); each change makes the sim send everyone nearby the full AvatarAnimation list, so cap changes at ~5-10/s
  with hysteresis and send nothing while still (2.f); latency ~0.3-0.5 s (inferred); a one-time fetch hitch per pose
  per viewer; at rest the error is the bin size.
- Policy: parity, playing your own animations as AOs do, honest poses with nothing encoded in them (2.g).
- An analog refinement, fragile, only if F's resolution isn't enough: a stopped animation with a huge ease-out holds a
  nearly frozen weight, so timing a start then a stop could hold any weight between two poses; but it depends on
  message timing through the sim, can only grow by restarting, late arrivals see only the bins, and stopped motions
  linger on observers.
- Cheap checks first, in a main-grid sandbox with a second account on the official viewer: how promptly the sim relays
  a burst of AgentAnimation changes; 20+ held animations with an AO under the cap; the upload price; how 10° and 15°
  bins read to someone watching.
- Set aside: arms rigged to spare Bento bones (fewer uploads, but the person would wear our body instead of their own);
  scripted arm segments (E: continuous, but not their own arms).

### Ruled out

- Experiences add no new channel: they only pre-grant permissions.
- A Sojourn-to-Sojourn side channel (our own relay, or packed chat) showing full tracking only to other Sojourn users
  goes against 2.k: LL-viewer users would see the AO while Sojourn users saw tracked arms, which is the Emerald
  extra-attachment case the clause was written for. A hybrid (everyone sees the animation or attachment version,
  Sojourn users see it exact) is still fragmentation, and it isn't "rendering" in Oz's sense, because the pose comes from
  outside SL rather than from world state. Announcing "I'm on Sojourn" to other viewers would also need the person's
  opt-in under 2.j.

### Other viewers (inferred, not read in their source)

Firestorm, Alchemy and Kokua send LookAt/PointAt like LL's and add privacy toggles (don't send, hide targets); none
picked up Puppetry. P373R's Firestorm VR Mod (last build 2024, per https://github.com/humbletim/firestorm-gha) is a
stereo camera and shares no tracking beyond what Firestorm sends.

## Not verified

- Whether Agni or Aditi regions still answer the `Puppetry` cap.
- The current animation upload price on the user's account.
- How often the sim relays ViewerEffect and AvatarAnimation to others; the latencies above.
- Whether heavy AOs mute A in practice: worth one look with a second viewer beside the person.
