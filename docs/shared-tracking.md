Written by Claude Opus 5.5, 2026-10-26. Agent-facing. Can other people in SL see the person's head and hand tracking? Each
finding is marked **verified** (with the source) or **inferred**. Viewer source was read at secondlife/viewer `main`
(release 26.5, published 2026-10-26) and the Puppetry project branch `DRTVWR-558`.

# Showing head and hand tracking to other people

## The answer

**Partly.** Full tracking (both arms, head, maybe fingers, streamed live) isn't possible on the main grid. It was Linden
Lab's Puppetry, and Puppetry never left three test regions on the beta grid. LL now calls it discontinued. The release
viewer contains none of its code, so even if a server relayed the data, nobody's viewer could show it. Some things do
fit inside the TPV policy, using what the official viewer already sends and plays:
- **Head direction, cheaply and well:** LookAt viewer effects. Every viewer turns the sender's head, eyes and part of
  the torso toward the look-at point. In mouselook the official viewer sends exactly this, with the camera's direction.
  Sojourn sends it since 2026-10-26 (below).
- **One arm pointing:** PointAt viewer effects. Every viewer runs the editing motion on the sender, an IK reach of the
  **left** arm toward the point, as when the official viewer selects or grabs something.
- **Rough arm and finger poses:** a palette of pre-made animations (arms only, hands only), chosen from the
  controllers' pose and played the normal way.
- **Floating hands:** a worn scripted attachment whose hand pieces Sojourn moves over HTTP. Possible, but it looks odd
  next to the avatar's own arms.

## The user's choice (2026-10-26)

- **Head direction, with a toggle: built.** Option A below, as the viewer sends it, with eye contact when your view
  rests on someone and a "direction" setting that never names who; others' look-at now turns their heads in Sojourn
  too. How it works, the setting and its default, and how it was checked: `docs/vr.md` > Where you look, seen by
  others. It's the only tracking others see.
- **Parked by the user** (judged within the TPV rules, not wanted now): a library of uploaded arm poses played like
  an AO (D), and scripted segmented arm and hand attachments moved by a script fed over a private chat channel (E's
  idea, with chat instead of HTTP-in).
- **Point-at (B) decided against for now**: it sounded finicky to the user.

## What Sojourn does now (verified, repo)

- `src/Sojourn.Render/SelfBody.cs` and `World/Avatars.cs` (`PoseSelf`, `Reach`) draw a local copy of the person's own
  avatar. The region's avatar is what others see; the copy is drawn only in this viewer, and the region knows nothing
  of it.
- `PoseSelf` puts the eyes under the camera, 8 cm back (`SOJOURN_BODY_BACK`). It scales the body about its feet to the
  person's eye height and turns its facing toward the head's yaw, smoothed. It shrinks `mNeck` and everything below it
  to a point, and hides what's worn there.
- `Reach` runs for `mShoulder*`/`mElbow*`/`mWrist*` on each side. It's analytic two-bone IK to the wrist target that
  `VrPilot.Hand` derives from the OpenXR grip pose (aim = grip turned 60 deg about X, the wrist 7.5 cm back and 3 cm up).
  The pole vector keeps the elbow down, out and back. Out of reach, the arm stretches up to 1.4x. The wrist takes the
  target rotation.
- Blending: the arm joints' rotations are **overwritten** after the region's animations are composed. Everything else,
  fingers included, keeps the animation.
- Network side (`Control/VrPilot.cs`): AgentUpdate carries `BodyRotation` (`_bodyYaw`, which turns once the
  head is 40 deg off), `HeadRotation` (the headset's orientation) and the camera, with `Mouselook = false`. Since
  2026-10-26 `Control/Gaze.cs` sends LookAt viewer effects (above); no other ViewerEffect.
- So others see a normal avatar playing its animations or AO. It faces the body yaw, its head turned where the person
  looks (unless an animation holds the head), its arms wherever the animations put them.
- Inferred: the sim doesn't relay AgentUpdate's `HeadRotation` to anyone. In the viewer, a remote avatar's head is
  driven only by animations and `LookAtPoint` (`indra/llcharacter/llheadrotmotion.cpp:186`), and avatar ObjectUpdates
  carry only the body rotation.
- Receiving side: others' LookAt turns their heads and eyes (`World/Avatars.LookAt.cs`, `Bodies/Animator.cs`, since
  2026-10-26). Others' PointAt is still ignored (the viewer runs its editing motion on their left arm).

## Puppetry

### The viewer side (verified, branch `DRTVWR-558`)

Files: `indra/newview/llpuppetmodule.cpp`, `llpuppetmotion.cpp`, `llpuppetevent.cpp`. Its last merge was 2023-09-11,
and the side branches ended in 2023 (`rider/LSL-Puppet-Strings-NewIK`, 2023-05-24).

- **The LEAP API:** an `LLEventAPI("puppetry")` dispatching on `command`.
  - `get` returns the camera or the skeleton.
  - `set` carries `joint: {<name>: {inverse_kinematics|joint_state: {position|rotation|scale}}}`. Short keys are
    `i`/`j` and `p`/`r`/`s`; rotations are the xyz of a unit quaternion with positive w.
  - The viewer sends the plugin `stop`, `set_camera`, `enable_parts` (a mask: head 1, face 2, left hand 4, right hand 8,
    fingers 16; face and fingers were never functional) and `set_skeleton`.
  - The plugin is a side process the viewer launches. LL's sample was `webcam_puppetry.py`.
  - The viewer IK-solves the targets and blends the result as a motion (`ANIM_AGENT_PUPPET_MOTION`).
- **Transport:**
  - Sending: a **`Puppetry` region capability**. A GET returns `event_size` (the bytes per message window, ≤255;
    puppetry stays off without it) and optionally `update_period`. A POST sets options such as server echo.
  - The joint events are quantised and packed into **`AgentAnimation.PhysicalAvatarEventList.TypeData`**, split to fit
    the MTU and re-sent constantly (`packEvents`, llpuppetmotion.cpp:991-1080).
  - Receiving: the same block on **`AvatarAnimation`**, unpacked by `handle_puppetry_data` in llviewermessage.cpp.
- **A sister feature, LSL attachment tracking:** a new message, `AgentAnimationTracking` (Low 431). The sender's viewer
  reported every attachment point's position and rotation to the sim, gated by the region flag
  `REGION_FLAGS_ENABLE_ANIMATION_TRACKING`, so scripts could read joint positions (`llvoavatarself.cpp:1295-1350`).

### The release viewer (verified, `main`)

- No `llpuppet*` files, no `Puppetry` capability, no `AgentAnimationTracking` in `scripts/messages/message_template.msg`,
  and no animation-tracking region flag.
- The `PhysicalAvatarEventList` blocks are still in the AgentAnimation and AvatarAnimation templates. They're old,
  unused blocks that Puppetry borrowed.
- `GenericStreamingMessage` (`indra/llmessage/llgenericstreamingmessage.h`) defines only
  `METHOD_GLTF_MATERIAL_OVERRIDE`. (Inferred: this is the streaming path some recalled as Puppetry's; Puppetry itself
  used the blocks above.)

### Status

- Verified:
  - The last viewer was the project viewer 6.6.12.579958, released 2023-05-11, on Aditi only, in the Bunraku, Marionette
    and Castelet regions. Others needed the same viewer to see anything:
    https://releasenotes.secondlife.com/viewer/6.6.12.579958.html and https://wiki.secondlife.com/wiki/How_Puppetry_Works
    (last edited 2024-01-26; Preview grid only).
  - The Puppetry user group meetings were suspended on 2023-07-13. LL said that wasn't abandonment, and that animation
    streaming and IK work continued: https://modemworld.me/2023/07/15/2023-sl-puppetry-project-week-28-summary/
  - May 2026, Inara Pey's summary of the Content Creation user group: the "discontinued Puppetry project". VR with
    hands, body and face "streamed to everyone else in a region" is "not actually something LL is directly focused on"
    and not something "LL wishes to commit to in the near-term":
    https://modemworld.me/2026/05/23/2026-week-21-sl-ccug-meeting-summary/
- Inferred:
  - There's no Puppetry server support on Agni. Nothing ever announced it there, and the release viewer couldn't use it.
  - Whether the three Aditi regions still answer the cap is unknown.
  - A cheap real check: at the next main-grid login, add `Puppetry` to the seed capability request and log whether it
    comes back. Even a yes changes nothing for others, whose viewers lack the code.
- Policy: sending Puppetry blocks on Agni would be a protocol departure (1.a) and a shared-experience feature the release
  viewer lacks (2.k). Out, unless LL ships it.

## What fits the policy

The test, from `docs/tpv-policy.md` 2.k and Oz Linden's 2012 gloss (re-read 2026-10-27): no fragmentation, so someone on
LL's viewer must see the same world we do. Controls are explicitly outside the rule, and 1.a allows documented protocol
departures. So sending a message every viewer renders, with values the official viewer itself wouldn't send, is a
controls question, not a 2.k one. (Earlier notes here read 2.k as strict send-parity; that was more conservative than
the text needs.) Everything below uses messages the official viewer sends and effects it draws for everyone.

### A. Head direction by LookAt (strong)

- Verified, viewer `main`:
  - `llhudeffectlookat.cpp`: every viewer, for every avatar's LookAt effect, sets `LookAtPoint` relative to that
    avatar's head and starts `ANIM_AGENT_HEAD_ROT`.
  - `llheadrotmotion.cpp`:
    - The head turns toward the point, constrained to 0.8 x 90 = 72 deg. Neck and head split it; the torso takes 35%.
    - Smoothing half-lives: 0.15 s for the head, 0.27 s for the torso. No roll (the frame is built from the world's up).
    - The eyes follow too (`LLEyeMotion` reads the same point, line 535).
    - Priority MEDIUM (1).
  - `DisableLookAtAnimation` defaults to off and doesn't persist.
  - In mouselook the official viewer sends `LOOKAT_TARGET_MOUSELOOK` with the camera's at-axis (`llagentcamera.cpp:1179`;
    priority 7, no timeout). Freelook and others are lower and time out.
  - Rate: at most 4 sends a second, and only when the target moves more than 5 cm (`MAX_SENDS_PER_SEC`,
    `MIN_DELTAPOS_FOR_UPDATE_SQUARED`).
- What others see: the head turning and nodding with the person's, the eyes with it, the torso following a little; no
  head tilt. A target on another avatar puts the point at their head, which reads as eye contact.
- Cost: small. LibreMetaverse 3.2.0 has `AgentManager.LookAtEffect(source, target, globalOffset, LookAtType, effectId)`
  (verified, its XML docs). Send `Mouselook` with head + forward x some metres, at the viewer's 4 Hz / 5 cm. Or, when the
  gaze ray hits a person or object, target it.
- Latency: inferred ~0.3-0.6 s (up to 250 ms of sampling, the trip through the sim, then the receiver's smoothing).
- Policy: plain parity. It's what a mouselook user sends. Some people have "show look-at" on (Firestorm draws names at
  the targets), the same exposure as any viewer's.
- Limits: animations at priority ≥2 that move the head win over it. Many AO stands animate the head at 3-4. Inferred:
  with a heavy AO the effect is muted.

### B. Pointing with the left arm by PointAt (useful, narrow)

- Verified, viewer `main`:
  - `llhudeffectpointat.cpp:397`: every viewer receiving a PointAt starts `ANIM_AGENT_EDITING` on the sender.
  - `lleditingmotion.cpp`: IK of `mShoulderLeft`/`mElbowLeft`/`mWristLeft` toward `PointAtPoint`, half-life 0.1 s,
    priority HIGH (2), a relaxed hand pose.
    - Targets behind a plane at 45 deg to the body's forward-left are reflected forward and raised. So it reaches well
      forward and to the left, and badly to the right or behind.
  - Rate: 4 Hz / 5 cm, as with LookAt.
  - Parity: the official viewer refuses to point at avatars or attachments (`LLAgentCamera::setPointAt`). It points while
    selecting (`POINTAT_TARGET_SELECT`) or grabbing (`GRAB`), always at an object plus an offset.
- What others see: the left arm reaching where the person points.
- Cost: small. `AgentManager.PointAtEffect` exists. The right hand is Sojourn's pointer, so either share the left hand,
  or use it for the person's touches and grabs, which is what the official viewer does anyway.
- Policy: objects are plain parity. Free space is still within 2.k on the fragmentation reading, since every viewer draws it
  the same. Pointing at people is the one the official viewer deliberately refuses, so treat it as a courtesy question
  and leave it out.
- Limits: arm animations at priority ≥3 (most AOs) override it.

### C. Aim animations (minor)

- Verified (`llvoavatar.cpp:6049`): while any of the built-in gun-aim animations plays (`aim_r_handgun`, rifle,
  bazooka, `aim_l_bow`), every viewer runs `LLTargetingMotion`. It turns the torso so the right hand points along
  `LookAtPoint` (additive, HIGH).
- Playing a built-in animation is standard, so a right-arm "aim where I look" exists for free.
- It's tied to the head's direction, not the hand's. Only for a deliberate pointing gesture, if at all.

### D. A pose palette (the most that's realistic for arms and fingers)

- A set of animations Sojourn makes once and uploads once:
  - **Arms:** each an arm-only pose (collar, shoulder, elbow, wrist) for one bin of wrist position relative to the
    shoulder. Say 3 heights x 5 directions x 2 reaches, about 30 per arm.
  - **Hands:** Bento finger poses mapped to the Touch controllers' capacitive sensors: open, point (index lifted),
    thumbs-up, fist, pinch.
  - Our own IK in `Avatars.Reach` can generate the arm keyframes. The .anim writer would be new.
- Played as any animation is (AgentAnimation start/stop). High priority so they win over the AO's arms, an ease-in of
  ~0.2-0.3 s so a change blends from the last pose, and a pose switched only when the bin changes, rate-limited to a few
  a second.
- What others see: arms moving to the right region in soft steps, hands making the right shapes, about 0.3-0.6 s
  behind (inferred).
- Cost: an animation upload is L$10 per the wiki (undated, inferred current). The viewer takes the real price from the
  account's benefits at login (`animation_upload_cost`, verified `llagentbenefits.cpp`). About 70 animations would be
  ~L$700 once, plus the work of writing .anim files and choosing bins. Sojourn already uploads (`UploadOps`).
  Free/paid Bento hand-pose sets exist on the Marketplace as a shortcut (inferred).
- Policy: parity. The official viewer plays the animations you own, and AO HUDs switch animations constantly. Keep the
  switch rate modest (2.f, load).
- Generating animations live instead isn't practical: each pose is a paid upload taking seconds, and a permanent asset.
  Inferred: SL has no temporary or local-only upload others can see. The official viewer's upload preview is local.

### E. Scripted floating hands (possible, awkward)

- A worn attachment with unrigged hand pieces. Its script takes an HTTP-in URL (`llRequestURL`). Sojourn POSTs
  shoulder-relative hand poses at ~10 Hz, and the script sets them with `llSetLinkPrimitiveParamsFast`
  (`PRIM_POS_LOCAL`/`PRIM_ROT_LOCAL`).
- Inferred:
  - Others see hands moving on their own, steppy at whatever object update rate the sim manages. Rigged mesh can't be
    moved this way, so the avatar's own arms and hands still play their animations, or are alpha'd out. That's
    VRChat-style floating hands, odd on an SL body.
  - HTTP-in avoids chat throttles. Chat on a private channel would work but is spammy.
- Policy: in-world it's ordinary scripted content any resident can wear and drive from outside. Fine, but a judgement
  call. Sojourn would hide the pieces in its own view, where it draws its own arms.

### F. A joint alphabet (proposed 2026-10-27, not built)

D binned whole-arm poses. Binning each joint separately instead gets close to streaming.
- Verified (viewer `main`: `llpose.cpp` `LLJointStateBlender`, `llmotioncontroller.cpp`, `llkeyframemotion.cpp`):
  - Every viewer blends animations per joint and per channel (rotation, position, scale), with up to 6 states per joint
    ordered by priority. An animation moves only the joints it has keys for.
  - Ease weights are `cubic_step` and stay continuous across a stop or a restart (`mResidualWeight`). So easing one pose
    out while the next eases in gives the same smooth blend on every viewer.
  - Uploaded animations can't be additive (priority capped below `ADDITIVE_PRIORITY`), and they sit at weight 1 once
    eased in.
- The scheme: one static asset per pose of a single joint or joint group:
  - shoulder orientations;
  - elbow (bend x forearm direction and twist);
  - wrist;
  - hand shapes per hand, from the Touch sensors;
  - head and neck with roll;
  - chest lean;
  - pelvis height (crouch).

  `Avatars.Reach` already computes the rotations. Each joint plays its nearest pose, and a change is a crossfade (start
  the new pose, stop the old, ~0.15 s eases). An arm's poses are the product of its joints' sets, while the uploads are
  only their sum.
- Precision through the hierarchy: the collar is the shoulder's parent, ~10-15 cm away. A fine collar set (about ±10° in
  small steps) over a coarse shoulder set refines the upper arm's direction. The cost is a shoulder shift of 1-2 cm,
  within real clavicle range.
- Budget (inferred): ~300-450 poses per arm and ~50-100 for head, torso and pelvis, so ~700-1000 in all. At L$10 each
  (the wiki; check the account's `animation_upload_cost`) that's ~L$7-10k once. The tracked joints play at priority 6 so
  they beat the AO, and stop (back to the AO) when tracking gives way (the `tracking` setting) or the hands rest.
- Limits:
  - 30 animations per avatar (the LSL docs, since server 1.25.4), the AO and built-ins included. ~10-12 are held,
    briefly double that during crossfades: it fits, but tightly.
  - Each change makes the sim send AvatarAnimation (the full list) to everyone nearby. Cap changes at ~5-10/s with
    hysteresis, and send nothing while still (2.f).
  - Latency is inferred at ~0.3-0.5 s.
  - A viewer's first sight of each pose fetches its asset, a one-time hitch per pose.
  - At rest the error is the bin size; in motion it's smooth.
- Policy: parity. It's playing your own animations, as AOs do, and everyone sees the same thing. The assets are honest
  poses with nothing encoded in them (2.g).
- Analog refinement (experimental): a stopped animation holds weight residual x `cubic_step(1 - t/easeOut)`, which is
  nearly frozen if its ease-out is huge. The viewer doesn't clamp ease-out on load; upload-side limits are unknown.
  Timing a start then a stop would then hold any weight between two neighbouring poses, giving continuous poses from a
  coarse set. It's fragile, though:
  - the weight depends on message timing through the sim;
  - it can only grow, by restarting;
  - late arrivals see only the bins;
  - stopped motions linger on observers.

  Only if F's resolution isn't enough.
- Cheap checks first, on the main grid in a sandbox, with a second account on the official viewer:
  - how promptly the sim relays a burst of AgentAnimation changes;
  - 20+ held animations with an AO under the cap;
  - the upload price;
  - how 10° and 15° bins read to someone watching.
- Considered and set aside:
  - Arms rigged to spare Bento bones (wing chain) placed at the shoulder, so three 1-DOF bones compose a 3-DOF joint.
    That means far fewer uploads, but the person would have to wear our body instead of their own.
  - Scripted unrigged arm segments (E): continuous, but not the person's own arms.

### Ruled out

- Experiences add no new channel: they only pre-grant permissions.
- A Sojourn-to-Sojourn side channel (our own relay, or packed chat) showing full tracking only to other Sojourn users
  goes against 2.k: LL-viewer users would see the AO while Sojourn users saw tracked arms, which is the Emerald
  extra-attachment case the clause was written for. A hybrid (everyone sees the animation or attachment version,
  Sojourn users see it exact) is still fragmentation, and it isn't "rendering" in Oz's sense, because the pose comes from
  outside SL rather than from world state. It would only need to carry each person's own tracking. Announcing "I'm on
  Sojourn" to other viewers would also need the person's opt-in under 2.j.

### Other viewers

- Inferred, not checked in their source:
  - Firestorm, Alchemy and Kokua send LookAt/PointAt like LL's and add privacy toggles (don't send, hide targets). None
    picked up Puppetry; searches found nothing.
  - P373R's Firestorm VR Mod (last build 2024, Firestorm 7.1.11 with mod code 6.3.3, per
    https://github.com/humbletim/firestorm-gha) is a stereo camera. It shares no tracking beyond what Firestorm sends.

## Ranked options

1. **LookAt from the headset**, in mouselook semantics (A). Built (2026-10-26), receiving side too. Hours of work. Others see the head and eyes follow the
   person's, at the official viewer's rate. Plus the receiving side: run `LLHeadRotMotion`/`LLEyeMotion` from others'
   LookAt in `Animator`, so other people's heads move in Sojourn as they do in the official viewer.
2. **PointAt from the left hand at objects** (B). Decided against for now (user, 2026-10-26). Small. Others see the left arm reach toward what's pointed at, unless
   an AO holds the arm. Also render `LLEditingMotion` for others.
3. **A finger-pose palette from the Touch sensors** (D, hands). A day or two plus ~L$60. Others see point, fist,
   thumbs-up, open hand.
4. **An arm-pose palette** (D, arms). Parked (user, 2026-10-26). Several days plus ~L$600. Others see arms roughly where the person's are, stepped
   and late, over the AO.
5. Floating hands by script (E). Parked (user, 2026-10-26). Only if the person wants that look.
5a. **A joint alphabet** (F), proposed 2026-10-27: supersedes 3 and 4 if built. Both arms, wrists, hand shapes, head
   roll, lean and crouch, on the person's own body, for everyone. ~L$7-10k of uploads once; the cheap checks in F come
   first. Saved by the user for a possible future attempt (2026-10-27; studio shelf: ideas/sojourn-joint-alphabet.md).
6. Watch for LL reviving Puppetry or animation streaming. The seed-cap check above tells us if a region ever offers it.

## Not verified

- Whether Agni or Aditi regions still answer the `Puppetry` cap.
- The current animation upload price on the user's account.
- How often the sim relays ViewerEffect and AvatarAnimation to others; the latencies above.
- Other TPVs' handling, from their source.
- Whether heavy AOs mute A and B in practice: worth one look with a second viewer beside the person.
