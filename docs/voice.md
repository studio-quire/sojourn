# Voice chat

Written by Claude Opus 5.5, 2026-10-25 (the research behind it 2026-10-21). Agent-facing. Talking with residents
through the Quest 3's mic (or the PC's), hearing them placed around you, push-to-talk or an open mic with a mute,
calls, and the assistant's push-to-talk kept out of all of it.

## Where it stands

Built through the plan's step 8 and tested on the local grid (2026-10-25). Not yet tried: the main grid (a sandbox,
a real P2P call) and the headset (echo from the Quest's speakers, latency, how panning feels). Both are on the board.

## Decisions

- **Our own stack** (user, 2026-10-25): a C++ shim of ours over Linden's prebuilt libwebrtc m144 (the build LL's
  viewer ships), our own C# for SL's protocol. Not LibreMetaverse's voice library (SIPSorcery: no echo cancelling,
  noise suppression or NetEq).
- **Push-to-talk is the default; open mic with a mute is the alternative**, a setting; open mic starts muted at login
  and whenever it's chosen (user, 2026-10-25).
- **Buttons** (user's suggestion, 2026-10-25): push-to-talk on the left grip in the headset; open mic's mute on the
  right stick's click; on the desktop B (or the middle mouse button, the SL viewer's push-to-talk) for both. The
  assistant keeps the right grip / T; while it's held the mic never reaches residents.
- **Policy** (user, 2026-10-25, from the plan): other residents' voices never go to the assistant, Claude, Whisper or
  disk, and there's no recording; the assistant may mute the mic, leave calls, decline a ringing call and set a
  person's volume or mute them in your own hearing, but never unmute the mic or answer a call (those are the person's
  own press); its spoken replies play to you only, never into a voice channel. Captions of others' voice: not built.
  Moderating others (2026-10-29) happens only after the person's own press on the confirmation.

## Using it

| | Desktop | Headset |
|---|---|---|
| talk (push-to-talk) | hold B or the middle mouse button | hold the left grip |
| mute / unmute (open mic) | B | the right stick's click |
| talk to the assistant (never heard by residents) | hold T | hold the right grip |
| answer or decline a call | the panel's keys (click, or 1 / 2) | the panel's keys (point, trigger) |
| settings | F2 > voice | the left menu button > voice |

- The settings menu's **voice** section: voice chat on or off (with the status line under it: "nearby voice:
  connected, 2 others", "voice is off on this parcel", "call with ..."), talking: hold to talk / open mic, mic on (open
  mic), volume (1 is the viewer's own level: its AudioLevelVoice, 0.5, scales the server's mix as AudioLevelSFX does
  world sounds, so voices and the world keep the viewer's balance; until 2026-10-26 voice played at the full mix, 6 dB
  louder against the world than in the viewer). Saved in `local/client/vr-settings.json` (`voiceEnabled`,
  `voiceMode`, `voiceVolume`). The devices are in the sound section (below).
- **Over the world**: a small light dot over each resident who's speaking (bigger when louder), and your mic: a
  microphone on a small disc in the assistant panel's look (`VoiceWiring.Decorate`, drawn by `Realtime/Overlay.cs` as an
  `OverlayGlyph`, the glyph a distance field in `overlay.frag`), like a watch face 4 cm off the back of your left wrist
  in the headset, at the bottom middle of the window on the desktop (about 38 px across at 1080 lines, where the
  viewer's speak button sits, clear of the chat feed and the panel). Live (your mic goes out to residents): the disc lit
  in the look's accent, the mic in its label colour, and a ring around it that follows your voice's level (your mic's,
  after the echo canceller, as the viewer's updateOwnVolume: `sv_mic_level`; the server's report for you came rarely
  or never, so the ring used to sit still). Muted (open mic's mute, or a moderator's): the body's glass with a slashed mic in full ink. Off
  (push-to-talk not held, the assistant's button held): the glass fainter, the mic dim. No voice here (the region or
  parcel has voice off, and no call is up: `VoiceClient.OffHere`): fainter still, a slashed mic that never lights, push-to-talk
  held or not (Claude Opus 5.5, 2026-10-27: at Calas Galadhon it lit for push-to-talk as if voice worked). Voice
  turned off in the settings: nothing.
- **Calls**: a ringing call puts "<name> is calling" (a group's: "<name> is calling <group>"; an ad-hoc conference's:
  "<name> is calling you into a conference") with "answer" and "decline" on the assistant's panel (it shows even when
  the panel is put away); answering is your press. In a call you're out of nearby voice; hanging up (or the other side
  leaving a one-to-one call) brings you back. An ad-hoc conference with several people is started on the API
  (`conference`, below), as the viewer's startAdhocCall: ChatSessionRequest's "start conference" with a temporary
  session id, then, once the region's ChatterBoxSessionStartReply gives the real one, its channel ("call"), which rings
  the others; open mic starts muted in it, as in the viewer's ad-hoc and group calls.
- **Calls turned away before they ring** (`Voice/CallRejects.cs`, LLIMMgr::inviteToSession in both viewers): from
  someone whose voice you muted (a voice mute or a block; one-to-one and conference calls, not a group's); group
  calls (`reject_group_calls`, both viewers); a non-friend's one-to-one and conference calls while B2's
  `friends_only_ims` ("only friends and groups can IM or call me") is on; conference calls
  (`reject_conference_calls`) and one-to-one calls (`reject_p2p_calls`), Firestorm's; every call in Do Not Disturb.
  All declined silently ("decline p2p voice" / "decline invitation"), as the viewers do, except Do Not Disturb: a
  one-to-one caller gets the person's DND sentence (as send_do_not_disturb_message) and the feed a quiet "You turned
  down X's call" (the viewer's you_auto_rejected_call). The switches are per account, beside B2's in people.json, all
  off by default; `SojournVoice calls` reads them with how many each turned away. One difference from the official
  viewer, on purpose: with friends only on, a group call a non-friend starts still rings (the setting's words say
  groups may call, and B2 lets group chat through); the viewer's code turns it away.
- **Voice mutes** live on the account's block list since 2026-10-29: SL's mute list with the viewer's flags
  (`Core/Agents/MuteList.cs` now carries them: a voice mute is the viewer's Mute Voice, flags 0xD, which leaves their
  chat alone; a full block mutes their voice too, as LLMuteList::isMuted(id, flagVoiceChat)). So they hold across
  machines and viewers, and a voice mute set in Firestorm arrives here. The ones kept on this machine before
  (`peerVoiceMutes` in vr-settings.json) move to the list at the first login; per-person volumes stay per machine, as
  the viewer keeps them. The list's changes reach the voice server at once (`m` for those in voice).
- **Moderation** (`Voice/VoiceModeration.cs`, the official viewer's): nearby, through the region's
  SpatialVoiceModerationRequest (the 2026 WebRTC moderation, LLNearbyVoiceModeration): `{operand: mute|unmute,
  agent_id}` for one person, through the region they're in, or `mute_all|unmute_all` for everyone on the region's
  channel, which the server keeps for people arriving later; allowed (isNearbyChatModerator) on a parcel with its own
  channel to its owner (or, for group land, members with the group's moderate chat power), on the estate's channel to
  the estate's owner and managers. In group and conference calls, ChatSessionRequest's "mute update" (mute_info.voice)
  and "session update" (moderated_mode.voice, then each member who doesn't match yet, as forceVoiceModeratedMode), for
  the call's moderators. Each acts on other people, so it waits for the person's press (a "permissions" case through
  `Confirmations.Cases` from VoiceWiring, naming who and where; the name is pinned to an id), and what they couldn't do
  is refused before they're asked (`Confirmations.Refusals`). Being muted by a moderator was already shown (the mic
  token's slashed mic) and holds push-to-talk off.
- **Where you hear from** (`voice_hear_from`, the viewer's VoiceEarLocation): camera (the default: on the desktop the
  camera, in the headset the head), avatar (the avatar's head and its rotation) or mixed (the avatar's place, the
  camera's or head's direction). Kept per machine in `voice-media-settings.json` beside vr-settings.json
  (`LocalPrefs.cs`).
- **The assistant** sees `SojournVoice` on the agent API: `status` (who's here, who's speaking, the call, a ringing
  call and its kind, `i_moderate`, `hear_from`), `mute`, `peerMute {id, mute}` (on the block list, above),
  `peerVolume {id, volume}`, `call {id}`, `callGroup {id}`, `conference {ids, name?}`, `hangup`, `decline`,
  `moderate {id or name, mute}` and `moderateAll {mute}` (both confirmed), `calls` (the switches and counts). No
  unmute, no answer. Per-person volumes are kept between sessions (`peerVolumes` in vr-settings.json, as the viewer
  keeps its per-speaker volumes on the machine) and reach the server as each person joins; `status` shows a person's
  `volume` when it isn't 1 and `muted_by_me`. Settings (SojournSettings, section voice): `reject_p2p_calls`,
  `reject_group_calls`, `reject_conference_calls`, `voice_hear_from`.
- **Menu rows** (batch M, 2026-10-29): voice > hear from (head, the viewer's camera in the headset; avatar; mixed);
  you > turn away > one-to-one, group and conference calls, three switches beside B2's. Moderation needs no row: the assistant does it on request, confirmed.
- Options: `--no-voice`, `--voice-mode ptt|open` (a settings override, like the headset's), `--voice-unmuted` (tests).
- **Devices** (2026-10-26): Settings menu > sound > speakers and microphone (automatic, or any device Windows has,
  cycling with − / +; shown by the part of the name that tells it apart), chosen apart for the desktop and the
  headset (`speakers`, `microphone`, `headsetSpeakers`, `headsetMicrophone` in vr-settings.json); `SojournSettings`
  speakers / microphone (auto, or a device by part of its name; an unclear name is refused with the list),
  `SojournAudio devices` lists them. Automatic is the Oculus pair ("Oculus Virtual Audio Device") in the headset and
  the system's defaults on the desktop (on this PC the default output is the Realtek's, checked 2026-10-26). A change
  reopens the devices at once (the echo canceller carries on); a chosen device that isn't connected (unplugged, Link
  off) falls back to automatic, logged. `SOJOURN_VOICE_MIC` / `SOJOURN_VOICE_SPEAKER` still override (tests). The
  engine and devices are shared with world audio (`Audio/AudioEngine.cs`): whichever starts first opens them.

## How it fits together

```
Sojourn.Session (C#, Voice/)                                    sojourn_voice.dll (C++, src/Sojourn.Voice.Native)
  VoiceWiring: buttons, frame hand-off, dots, settings, calls UI    engine.cpp: libwebrtc's threads and factory; our
  VoiceClient (own thread, 100 ms tick, woken by events/buttons):     audio device module (it never touches a device:
    channel choice (estate + neighbours | parcel | call | none),       we push capture and pull playout); our one APM
    VoiceConnection state machines, peers, poses, the mic             (AEC3, NS very high, AGC2, HPF): capture side the
  VoiceProtocol: caps bodies, SLData JSON (one file, to diff          mic, reverse side the whole device mix; mute
    against llvoicewebrtc.cpp)                                        ramp, the assistant's gate and tap, local playback
  VoiceNative: the C ABI (sojourn_voice.h)                ─────►   audio_io.cpp: WASAPI shared mode, event-driven,
  AssistantMic: the tap -> GET /mic, POST /play -> playback         MMCSS, 48 kHz float, 10 ms; WAV files and paced
  VoiceApi: SojournVoice ops                                        silence for tests; device loss and retry
  Bridge/BridgeServer: /mic, /play for the sidecar                peer.cpp: peer connections set up as the viewer's
Sojourn.Core/Agents/ChildCapsGuard: neighbours' caps (below)
assistant/clientaudio.py: ears.py takes the client's mic, voice.py's Speaker.sink plays through the client
```

The VR client binds the buttons to its keys, mouse and controllers in `src/Sojourn.Client/Voice/VoiceInput.cs`.

- **The audio path**. Capture thread: mic frame -> APM (echo-cancelled against everything we played) -> the tap ring
  (16 kHz, 300 ms pre-roll) -> gain (0 when muted or while the assistant's gate is open, with a ramp) -> libwebrtc's
  transport -> Opus to the server. Render thread: libwebrtc's mixed decoded voice (stereo, as the server panned it) +
  local sounds (`sv_play`: the assistant's replies) + world audio (`world.cpp`: the world's sounds and the music,
  `docs/audio.md`) -> output volume -> APM's reverse stream and the device. libwebrtc's
  voice engine has no APM of its own (the factory is built without one), so nothing processes twice. No managed code
  runs on the audio threads: native events are queued, the tap is pulled.
- **Mute**: the shim ramps the gain to zero (120 ms push-to-talk release, 300 ms open-mic mute), then the C# side
  disables the send track on the primary connection, as the viewer does. Capture keeps running all session, so the
  echo canceller never cold-starts.
- **The assistant's mic**: while its button is held (`AssistantMic.Gate`), the tap collects; residents get zeros. On
  release the gate stays open 250 ms (the tail), then the utterance (with the 300 ms before the press) waits for the
  sidecar's GET /mic (taken once, within 10 s). The sidecar's own `soundcard` capture remains only without voice, and
  for `SOJOURN_MIC_FILE` tests. The tail adds ~0.33 s before Whisper starts.
- **The assistant's spoken replies**: `voice.py`'s Speaker.sink posts half-second pieces to POST /play (about 1 s
  queued, cut off at once on stop), so they're in the device mix and the echo canceller's reference. The Windows-voice
  fallback (when Kokoro's files are missing) still plays by itself.
- **Neighbours** (`Sojourn.Core/Agents/ChildCapsGuard`): the guard connects neighbouring regions itself (docs/client.md >
  Gotchas: LibreMetaverse's two handlers raced and lost a neighbour about one login in ten), one circuit each, its seed
  set once the circuit is up, and checks for half a minute after.

## SL's protocol (what VoiceProtocol and VoiceConnection follow)

Sources (checked 2026-10-21, the code again 2026-10-25): the viewer's `indra/newview/llvoicewebrtc.cpp`,
`indra/llwebrtc/llwebrtc.cpp`, `llvoicechannel.cpp`, `llimview.cpp` (github.com/secondlife/viewer, develop; copies of
the files used are in `local/ref/`); LL's "WebRTC on Second Life, a Developer's View" (~2024-03, linked from
https://wiki.secondlife.com/wiki/WebRTC_Voice). Vivox is gone from SL since 2026-05.

- **Caps**. `ProvisionVoiceAccountRequest` on the region the connection is for (a neighbour's too):
  spatial `{jsep:{type:offer, sdp}, channel_type:"local", voice_server_type:"webrtc", parcel_local_id?}`, calls
  `{jsep, channel_type:"multiagent", channel, credentials, voice_server_type}`; the reply `{viewer_session,
  jsep:{type:answer, sdp}}`; 409 channel full, 401 locked; leaving `{logout:true, viewer_session}`.
  `VoiceSignalingRequest`: our candidates after the answer, a batch per tick `{viewer_session, candidates:[{sdpMid,
  sdpMLineIndex, candidate}]}` (SL's trickle form, no "candidate:" prefix), then `{candidate:{completed:true}}`.
  `ChatSessionRequest` for calls: "start p2p voice" `{session-id: me XOR them, params: them, alt_params:
  {voice_server_type}}`, "call" `{session-id, alt_params:{preferred_voice_server_type}}` -> `voice_credentials
  {channel_uri, channel_credentials}`, "accept invitation", "decline p2p voice", "decline invitation". A ringing call
  is a ChatterBoxInvitation with voice. Group calls: the group's chat session, then "call" with the group's id.
- **Channels**. Estate: "Estate", a connection to the region you're in plus each region within 100 m of you (eight
  compass points at 100 m) with voice; every connection receives, only the primary one (your region) sends; crossing
  a border flips it and re-sends the join. Parcel (voice allowed, not the estate channel): "<region id>-<local id>",
  one connection. Voice off on the region (estate AllowVoice), the parcel (AllowVoiceChat) or SimulatorFeatures'
  VoiceServerType not "webrtc": no connection. A new channel takes over once its connection is up. A call replaces
  spatial voice until it ends. Teleports mute everything and restore 200 ms after arrival.
- **Peer connections** (llwebrtc.cpp): Unified Plan, UDP 60000-60100, STUN `stun1..3.agni.secondlife.io:3478` on the
  main grid (two on aditi; none on OpenSim), the client offers; one sendrecv Opus track "SLAudio" in "SLStream",
  disabled at first; an ordered data channel "SLData" made before the offer; the offer's Opus lines edited to
  `opus/48000/2` and `minptime=10;useinbandfec=1;stereo=1;sprop-stereo=1;maxplaybackrate=48000;
  sprop-maxplaybackrate=48000;sprop-maxcapturerate=48000` (the viewer appends a second fmtp line; we replace the
  first, which says the same once). Failed: renegotiate at once; disconnected: after 10 s. The viewer's sender
  `SetParameters` calls fail in m144 too (no getParameters first): left out.
- **SLData JSON**. To the server: `{"j":{"p":true}}` (join, primary) or `{"j":{}}`; poses `{"sp","sh","lp","lh"}` —
  avatar (raised 1 m to head height) and listener positions in global metres x100 as ints, quaternions x100 as ints,
  SL's frame (X forward, Z up); the listener (camera on the desktop, head in the headset) tethered to 50 m; sent at
  most every 100 ms and only when something moved 0.1 m or turned 4 degrees. `{"m":{id:bool}}`, `{"ug":{id:gain}}`
  (volume x220) for my mix, re-sent to people as they join. From the server: an object keyed by agent id: `j:{p}`
  joined (counted only from their primary region's server), `l` left, `p` power x128, `v` speaking, `m`
  moderator-muted (only from the primary region), `V` the server's version on my own id.
- **The server mixes and pans** (one stereo track per connection, built for my pose). Head turns are heard after a
  round trip (the 100 ms send limit, the network, the server's mix tick, the jitter buffer), as for everyone on SL.
- **TPV policy** (`docs/tpv-policy.md`): our wire is the viewer's (same caps, SDP edits, rates, STUN); 10 Hz poses,
  randomised backoff (0.5-1.5 s growing to 10 s), neighbours only within 100 m, a connection not up in 20 s retries.

## Building

- LL's libwebrtc: `gh release download m144.7559.06.20 -R secondlife/3p-webrtc-build -p '*windows64*'` and unpack the
  .tar.zst into `local/webrtc/` (include/, lib/release/webrtc.lib; ~800 MB unpacked). Don't link Tessel's copy.
- `python tools/voice.py build` (CMake from the VS 2022 Build Tools, /MT release, C++20) builds `sojourn_voice.dll`
  and the test program `sv_loopback.exe` into `local/voice/build/Release/` when the sources changed;
  `tools/client.py build` runs it first, and the client's csproj copies the DLL next to `Sojourn.dll`. Without the DLL
  the client runs without voice.

## The local voice server

`python tools/grid.py voice up` starts legion-voice-mixer 1.2.0 (a Janus plugin speaking SL's protocol: per-listener
spatial mixes, mute and gain, GPL-3; `ghcr.io/johnlegionh/legion-voice-mixer`) in Docker Desktop (compose project
`sojourn-voice`, files in `local/voice-server/`), advertising media on the PC's LAN address, and points OpenSim at it
(`local/grid/bin/config/os-webrtc-janus.ini`, restarting the grid the first time). `voice down`, `voice status`,
`voice parcels estate|parcel|off` (the four regions' parcels on the estate channel, each its own, or no voice;
restarts the grid). OpenSim 0.9.3.1's Janus service names the stock AudioBridge plugin, so
`tools/janus_name_proxy.py` (a second container) renames it on the HTTP API. Ports: 14223 (HTTP, loopback), 14225
(admin, loopback), UDP 10000-10100.

## Testing

`python tools/voice.py ...` (each prints ok/FAIL lines):
- `loopback`: the shim alone, two in-process peer connections: connects, the data channel both ways, a signal through
  raw (envelope correlation ~0.97, 50-70 ms) and processed (Whisper hears the same words), mute gives silence, the
  assistant's gate silences residents and taps 1000 + 300 + 250 ms.
- `devices`, `chirp [--desktop|--synthetic]`: the echo path of real devices for the headset session (a sweep to the
  Oculus headphones, the Oculus mic raw and echo-cancelled; prints the round trip and echo level); `--synthetic` checks
  the tool on a made-up echo (120 ms, -6 dB).
- `roundtrip`: Voice Friend plays speech as its mic, Voice Pilot records what it hears: the words (Whisper),
  file-to-file latency (110-120 ms through the local mixer), the L-R balance flipping as Friend moves to Pilot's
  other side.
- `scenarios [neighbours crossing parcel off retry gate echo call]`: hearing across a border through a neighbour,
  the mic following you into the next region through one connection only, a parcel's own channel, no voice on a
  voice-off parcel, the mixer restarting mid-call, the assistant's gate (residents hear -134 dBFS while it's held,
  the assistant gets the words), a synthetic echo path cancelled (Friend hears its own echo at -43 dBFS without AEC,
  -99 with it), calls (the ringing panel's answer key; both in one non-spatial channel 120 m apart; hangup), and
  switches (2026-10-29: invitations from Friend through each call switch, B2's friends only and Do Not Disturb by
  `testInvite`, which runs the same screening as a real invitation; Friend's voice muted as a voice-only block list
  entry and unmuted; hearing from the avatar seen in the poses sent; moderation refused before asking, since OpenSim
  has no SpatialVoiceModerationRequest; what OpenSim makes of "start conference" printed).
- They use their own accounts, Voice Pilot and Voice Friend (`tools/grid.py account Voice Pilot|Friend
  sojourn-local`, API ports 9730 / 9731, state in `local/voice/pilot|friend`), so another session's Sojourn Pilot
  isn't kicked. While another session's edits break the main tree's build, test from a worktree:
  `local/voice/wt-sync.sh` (HEAD plus voice's files, built there), then `SOJOURN_CLIENT_ROOT=<that folder>`.
- `tools/headset_friend.py` puts Voice Friend near the start for a headset voice session: a looped WAV on an open
  mic, and what it hears recorded to `local/voice/headset/`.
- Test-only switches: `SOJOURN_VOICE_MIC_FILE=path|loop:path|none` (a WAV as the mic), `SOJOURN_VOICE_OUT_FILE=path`
  (what's heard to a WAV, honoured on a local grid only), `SOJOURN_VOICE_TEST_ECHO=delay_ms,gain` (the device mix fed
  back into the mic), `SOJOURN_VOICE_APM=off`, `SOJOURN_VOICE_LOG=1` (poses, data, which connection sends),
  `SOJOURN_NEIGHBOUR_LOG=1`; `SojournVoice testCall` / `testRing` on local grids only; `tools/winkey.ps1` presses keys
  in the window.

## Measured (2026-10-25, local grid)

- Connecting: an in-process pair 50-80 ms; the client to the local mixer within a second of login, neighbours a second
  or two later.
- File mic to file speaker through the mixer: 110-120 ms. Add the devices (WASAPI ~20-40 ms each way here; Link's
  unknown) and, on SL, the network.
- Echo cancelling: 56 dB of a synthetic 150 ms echo removed (speech), noise bursts to -93 dBFS. AEC3 doesn't cancel a
  pure sine sweep (it learns from broadband sound), so the chirp tool measures the echo with a sweep and the
  cancelling with noise.
- CPU: about 4% of one core for voice (live mic, connected), beside the renderer.
- The Oculus mic takes 1-2 s to open with Link off, then delivers silence ("silent" device event after 1 s without
  audio).

## Gotchas

- OpenSim 0.9.3.1 starts a P2P call ("start p2p voice": 200 and a session start reply to the caller only) but answers
  "call" with 400 and never rings the callee; its multiagent provisioning reads `channel_id` where the viewer sends
  `channel`. Real calls are for the main grid.
- (Fixed 2026-10-26) About one login in five or ten, a neighbour was lost: LibreMetaverse's EnableSimulator and
  EstablishAgentCommunication handlers raced (docs/client.md > Gotchas). `scenarios neighbours` still says "warn"
  rather than failing if it ever recurs.
- libwebrtc needs WSAStartup (the shim calls it); without it every socket fails and nothing connects.
- Git Bash's `timeout` wrapper can hang on a native test program that has exited; run `sv_loopback.exe` from Python
  (tools/voice.py does).

## Not done

- Tried on the main grid: estate voice in a sandbox, a P2P call both ways, a group call (board).
- The headset: echo with the Quest's open speakers (`voice.py chirp`), latency, how the server's panning feels with
  head turns (board).
- Built 2026-10-29 (batch B10) but only Second Life can show them working: a conference ringing others and connecting,
  nearby moderation as an estate manager or parcel owner (a person muted for everyone, mute all holding for someone
  who arrives), a group call's moderation, a voice mute made here showing in Firestorm's block list (and one made
  there arriving here), a declined call reaching the caller (and the DND sentence). On the board.
- Names for people not in view (`status` names only avatars the client has). Firestorm's "hear media and sounds from"
  (world sounds follow the head or camera, as before).
- (World sounds and the parcel's music now play through the same mix: `docs/audio.md`.)
