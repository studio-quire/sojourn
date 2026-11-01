# World audio

Written by Claude Opus 5.5, 2026-10-25. Agent-facing. The world's sounds (objects', gestures', collisions, footsteps,
typing, UI sounds) placed around you and following the head in the headset, and the parcel's music stream.
Voice chat is `docs/voice.md`; both play through the same engine.

## Where it stands

Built and tested on the local grid (`tools/audio.py test`). On Second Life in the headset, a live-music stream played,
showed its song in menu > sound and faded as the user left (2026-10-28). What's left to hear there is under Not done.

## What plays, and the viewer's rules it follows

From the viewer's source (`llaudioengine.cpp`, `llaudiosourcevo.cpp`, `LLViewerObject::setAttachedSound`,
`process_sound_trigger` and neighbours in `llviewermessage.cpp`, `llvieweraudio.cpp`, `llviewerparcelmgr.cpp`,
`llvoavatar.cpp`, `llimview.cpp`; read-only copies in `local/ref/viewer`, a sparse clone):

- **Attached sounds** (llPlaySound, llLoopSound, llLoopSoundMaster/Slave, llSetSoundQueueing, llAdjustSoundVolume,
  llStopSound, llSetSoundRadius): one source per object, following it as it moves (an attachment where it's worn, a
  HUD's centred), from AttachedSound, AttachedSoundGainChange and the object updates' sound fields; postponed 15 s for
  an object not seen yet; a loop already playing is left alone; without the queue flag a new sound stops the old one
  first. Slaves start when the loudest master loops, at its offset, sample-exact (in the mixer). A queued sound plays
  straight after the current one, also sample-exact; one still waiting for its asset is replaced, as in the viewer
  (scripts preload for that).
- **Triggered sounds** (llTriggerSound, gestures' sounds and the inventory's Play Inworld, which the region echoes
  back, collision sounds): placed where they were triggered.
- **Loudness**: gain x the type's level (SFX, UI, ambient: 0.5 each at 100%) x the distance: OpenAL's inverse distance
  clamped at 1 m, rolloff 1, 5 with the camera under water. **Cutoff**: llSetSoundRadius, measured from the ear to the
  object (to the wearer for an attachment). **Parcels**: a sound from another parcel isn't heard if yours or that one
  keeps its sounds inside (the overlay's PARCEL_SOUND_LOCAL), unless you're riding the object. Block list: residents'
  and objects' sounds dropped.
- **The cap**: 30 at once (LL_MAX_AUDIO_CHANNELS). Priority is gain / distance squared (UI 1); a new source takes the
  quietest channel only if it's louder. One-shots give up after 15 s without their asset, are cut after 60 s; loops
  never end on their own.
- **Footsteps** (the viewer makes them itself): each walking or running avatar within 20 m, every half stride (0.5 s
  walking, 0.33 s running, scaled by speed) and on landing, gain 0.1 at the ambient level at the feet. On the ground
  the land step; on a build the wood step (prims' default material), as there's no step raycast here.
- **Typing**: someone starting to type, UISndTyping at their body.
- **UI sounds**, centred: a new conversation (the first IM or group chat of a session; the viewer's default
  PlaySoundNewConversation), a voice call ringing (UISndStartIM), teleporting out, a region restart warning, L$ moving
  by more than L$50. Assets by the viewer's settings.xml ids; OpenSim ships them too.
- **Teleports**: the world's sounds stop until a second after arriving (the viewer mutes behind its progress screen);
  UI sounds still play; the music fades out and back.
- **Music**: the agent's parcel's music URL (the viewer plays one longer than 12 characters starting http:// or
  https://). A new URL fades the old out over 2 s, then the new in over 3 s once it plays; none fades out and stops.
  Shoutcast v1 ("ICY 200 OK", which .NET's HttpClient refuses, so it's our own HTTP/1.0 client), Shoutcast v2 and
  Icecast; MP3, AAC (ADTS, HE-AAC included) and Ogg Vorbis (chained streams: a new decoder per track); .pls and .m3u
  playlists and redirects; the ICY title (StreamTitle) as the song playing; reconnecting 2 s growing to 30 s while
  wanted. 1.5 s buffered before it plays, and again after running dry. Any TLS certificate is accepted, as the viewers'
  stream players do: SL streams often sit behind a shared host's certificate naming another host, and nothing is sent
  but the GET (`MusicStream.cs`). With a screen reader reading out, the title is read through it when the stream starts
  playing and whenever it changes ("Now playing: ..."; `docs/screen-reader.md`).

## Using it

- Settings menu > **sound**: volume (everything you hear: world sounds, music, voice, spoken replies), sounds, music
  (on/off; its note says the song or the state), music volume, and the speakers (and voice's microphone: docs/voice.md >
  Devices). Sounds and music are shares of the viewer's own levels (100% = its defaults, 0.5 and 0.3). Saved in
  `vr-settings.json` (`audioVolume`, `soundsVolume`, `musicVolume`, `music`).
- The agent API: SojournSettings `volume`, `sounds_volume`, `music`, `music_volume` (the assistant's settings tool).
  `SojournAudio status`: what's playing (sound, object, owner, distance), the music (parcel URL, state, title,
  format), the mixer's numbers; `SojournAudio sources`: every source tracked, muted or waiting too.
- `--no-sounds`: no world audio (voice's tests use it, so their recordings hold only voice).

## How it fits together

```
Sojourn.Session/Audio (C#)                                       sojourn_voice.dll: world.cpp
  AudioWiring: start-up, the listener each frame, settings, API     32 channels: 48 kHz buffers (resampled once on
  AudioEngine: the engine and devices, shared with voice              load, Kaiser sinc), loop, queue, sync slaves,
  WorldSounds: the viewer's sources and channels ("audio",            gain ramps, a 10 ms fade on stop; per channel
    50 Hz; LibreMetaverse's events only enqueue)          ───────►    the spherical-head model below; the music ring
  SoundBank: fetch (LibreMetaverse's cache), NVorbis decode           (20 s, sinc resampler, pre-roll, underruns);
    ("audio-decode", below normal), up to 160 MB held                 a bus gain and a limiter; added to the device
  MusicStream + OggVorbis: ICY client, frame sync, decoders           mix in RenderFrame, before the output volume
    ("audio-music", "audio-stream", below normal)                     and the echo canceller's reference.
  ParcelSound: your parcel, its URL, the sounds-stay-inside rule    Media Foundation's MP3 and AAC decoders (MFTs)
  Cues: UI sounds, typing, footsteps                                  for the stream thread.
```

- **One engine**: whoever starts first (voice, or world audio with `--no-voice`) opens the devices; world audio alone
  opens only the speaker, and voice reopens them with the mic. Everything world audio plays is in the device mix, so
  it's in the echo canceller's reference and never reaches the mic or residents. The output follows Windows' default
  and survives devices coming and going (`docs/voice.md` > Devices coming and going): with no output at all the mix
  runs on unheard, so sounds end on time and nothing stale plays when a device returns.
- **The listener** comes straight from the render loop (`sv_listener`: the camera on the desktop, the head in the
  headset, VrView), so a head turn is heard within the device's buffer (~30 ms), not at the audio thread's 50 Hz.
  Positions are global metres in doubles.
- **Spatialisation** (not the viewer's: its OpenAL pans stereo): Brown & Duda's spherical head (radius 8.75 cm). Per
  ear an interaural delay (Woodworth: up to 0.66 ms, read with a cubic) and a head-shadow shelf (a first-order shelf
  around 1.25 kHz, +8 dB toward the ear, about -18 dB on the far side), scaled so straight ahead is flat; a pinna
  shadow for sources behind (up to -5 dB in the highs) to tell behind from ahead; a mild broadband level difference
  (+-1.5 dB). Cues fade out inside half a metre. Parameters are interpolated across each 10 ms frame. A stereo sound
  placed in the world is mixed to mono; centred ones (UI, HUDs) play as they are.
- **CPU**: the mix runs on the WASAPI render thread (MMCSS); assets decode once, off every hot thread; the stream
  decodes on its own thread paced by the network. Nothing managed runs on the audio thread.
- Media audio (media on a prim, parcel media) is media's (docs/media.md), mixed here too (world.cpp sv_media_*).

## Testing

`python tools/audio.py test [NAMES]` (`fixtures`, `serve` separately): one client (Audio Pilot, API :9732, base dir
`local/audio/pilot`, `--no-voice`) records what it hears through voice's test switch (`SOJOURN_VOICE_OUT_FILE`) at
Atelier's north lawn (150, 240), while a phantom **Sound Box** (an OAR made by the tool and loaded once with
`load oar --merge`, owned by the parcel's owner so its script may set the parcel's music; silent until told, chat
channel 7) and a **test radio** (`serve`: an ICY server on 127.0.0.1:9751 with /mp3 answering "ICY 200 OK", /aac,
/ogg, /pls, /redirect) are driven. Checks, from the recording: a loop to the side flips L-R as the listener turns
(lookAt), centred ahead and behind, duller behind; 1/d over distance; llAdjustSoundVolume; the cutoff; stop; a
trigger's latency; queueing back to back; 40 triggers -> 30 playing, the limiter; the mixer's time; music in the three
formats with titles, its volume, off; Play Inworld; footsteps; the teleport sound. While another session's edit breaks
the main tree's build, `local/audio/wt-sync.sh` builds HEAD plus audio's files in `local/audio/wt`, then
`SOJOURN_CLIENT_ROOT=<it>`.

## Measured (2026-10-25, local grid)

- Balance: a source 90 degrees to the side, 9.9 dB louder in the near ear broadband, 0.48 ms interaural delay; ahead
  and behind 0.0 dB; behind 3-4 dB duller at 6 kHz against 1 kHz. Distance 8.5 dB for 7.3 -> 19.2 m (1/d: 8.5).
- Latency, command to sound: a scripted trigger 120-180 ms (chat to the script, the region, back to us); Play Inworld
  30-90 ms. The client's own share: up to 20 ms (the audio thread's tick) plus a 10 ms frame and the device buffer.
- Music: playing 0.5-4 s after the URL changes (the 1.5 s pre-roll and the server's burst); no underruns.
- CPU: the mix 4-13 us per 10 ms with a few sounds (~0.1% of a core), 170-200 us with 30 playing (under 2% of one
  core).

## Gotchas

- OpenSim sends a new llSetSoundRadius only with the object's next sound; the test asks for the loop again.
- OpenSim's footstep asset is quiet (-42 dBFS average): footsteps at the viewer's gain are around -80 dBFS. Faithful,
  but worth a listen on SL.
- Under water (the camera below the region's water height, 20 m at Atelier's south) the rolloff is 5: sounds drop off
  fast, so test spots must be on land.
- llSetParcelMusicURL works only for an object owned by the parcel's owner.

## Not done

- In the headset on Second Life (`docs/sittings.md`): how placing feels with real head motion, objects' sounds
  turning with the head, the balance against voice (the viewer's: voice at 1 is half the server's mix, as
  AudioLevelVoice; docs/voice.md), other club streams (Shoutcast v2, AAC+).
- Departures kept for now: no Doppler (OpenAL's factor 1 in the viewer; noisy object velocities would wobble the pitch),
  no wind noise, footsteps' material only land or wood, channels assigned all at once rather than one per frame.
- Opus and HLS streams; the viewer's "ask before playing" music mode; switches for gesture and collision sounds
  (always on, the viewer's defaults); sounds from regions above your maturity aren't filtered.
