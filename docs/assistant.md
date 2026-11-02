# The assistant

Written by Claude Opus 5.5, 2026-10-21, condensed 2026-10-31. Agent-facing. A personal AI that operates the client for
the person in it: they hold a button and speak (or type), Claude acts through the agent API, and one panel shows what
it heard, what it's doing, the result, and choices or confirmations to press. The checks named here ran on the local
grid (desktop, sometimes the XR Simulator) unless they say otherwise; what waits for Second Life or the headset is in
`docs/sittings.md`.

Three parts have docs of their own:
- `docs/assistant-panel.md`: the panel, the settings menu, the look, where menus sit in the headset, size, showing
  things on screen, the reader.
- `docs/assistant-incognito.md`: how incognito is enforced (the gate, handles, what's kept, the controls, automatic
  spells, dictation, the sidecar's sessions, the leak test). Its rules are below (> Incognito).
- `docs/assistant-cost.md`: what each kind of use costs on a key or the relay, describing as one lean call with its
  economy and best models (how Haiku, Sonnet and Opus compared), and the measurements behind the defaults below.

## Using it

The client starts the assistant itself (`--no-assistant` to skip). First time on a machine:
`python tools/assistant.py setup` (the venv `local/assistant-venv`: claude-agent-sdk, faster-whisper, soundcard,
kokoro-onnx, sherpa-onnx, websockets; Kokoro's voice files into `local/assistant/kokoro` and Parakeet's model into
`local/assistant/parakeet`, ~480 MB), then `python tools/assistant.py check` (microphones, Claude Code, Whisper,
Parakeet, whether a Soniox key is there, the Claude sign-in and kept keys). Whose models answer is the person's
provider (next section).

| | Desktop | Headset |
|---|---|---|
| talk | hold T, speak, release; or tap T, speak, tap again to send | hold the right grip, speak, release; or tap it (under 0.3 s), speak hands-free, tap again to send (T in the mirror window too) |
| type | Enter, type, Enter (Esc cancels); `/ask ...` in the console | the PC keyboard into the mirror window, same keys |
| point ("that") | the mouse cursor (screen centre while steering) | the pointer: A turns it on (the right controller's ray); off, the assistant goes by your words and what's in the middle of your view, and asks you to point when it can't tell |
| press a key on the panel | click it (press and release on the same key) | the pointer on, point and pull the right trigger (a confirmation and the settings menu bring the ray up by themselves) |
| pick a choice | click, or 1-9 | point and trigger, or say it |
| read a long answer | click ‹ › | point at ‹ › and trigger |
| hide the whole interface, or bring it back | Tab | X |
| settings (the assistant's and, in the headset, moving) | Esc once nothing else is open (or F2), then click; Esc goes back, then closes | the left menu button, then point and trigger |

A tap leaves the mic open (a steady ring around the panel's orb) until the next press, whose release sends; voice's push-to-talk or dictation pressed meanwhile sends it first, as does the headset losing focus (`AssistantHost.TalkPress`).

"Stop", "cancel", "never mind" stop the current job. The panel hides itself 15-75 s after the last news (longer for
longer answers) unless something waits (a choice, a confirmation); its × hides it at once; talking brings it back. X
(Tab) hides the whole interface, the panel with it, until X again (saved with the headset's settings; `docs/vr.md` >
Controls): only a confirmation still comes up (it holds every other op); replies, choices and requests to point wait
unseen, and the assistant's context says the interface is hidden.

## Switching it on: the provider (provider.py)

Anyone can switch the assistant on with what they already have (built 2026-10-29 for the public release). For the VR
client the provider and its key are chosen **only in the launcher** (the user, 2026-10-29; `docs/launcher.md`); the
headset's settings menu shows the model choices for whatever was chosen there (Claude's model and effort rows with a
Claude account or key; with OpenRouter its model, plus Claude's rows for Claude through it) and a trouble note on the
first row when it doesn't work. Sojourn Access, which ships and starts on its own, keeps them in Alt+0's first rows.
The providers:
- **claude** (the default): the Claude subscription Claude Code is signed in to on this PC (`claude`, then `/login`).
  Offered only when running from the repo (`ProviderKeys.ClaudeOffered`): packaged and public builds offer no
  Claude-account sign-in (the user, 2026-10-30, because of Anthropic's Agent SDK terms; CLAUDE.md > Decisions), so
  neither Access's Alt+0 nor a packaged launcher lists it. There "claude", the old default every earlier build saved,
  means none chosen yet: the session host puts a key or token already kept in its place (`ProviderKeys.FallbackFor`,
  at settings load), else the sidecar's state is `none`, the person hears once how to choose (a token from Studio
  Quire, or their own Anthropic or OpenRouter key, pasted into the key row), and the provider row says "none chosen
  yet".
- **anthropic**: the person's Anthropic API key.
- **openrouter**: their OpenRouter key, and **openrouter model**: `claude`, shown as **Claude Haiku · Sonnet · Opus**
  with its tags (anthropic/claude-haiku-5.5 · sonnet-5.5 · opus-5.5: Claude Code's ids as `or_slug` maps them, so the
  model and effort rows work as usual), or any OpenRouter model id, which then takes every request itself (no
  handover; the model and effort rows go). The choices offered are real tags, never OpenRouter's rolling aliases
  (`Assistant/OpenRouterCatalog.cs`, the user's ask, 2026-10-30): Claude's three first, then each rolling family's
  current model (the alias's `alias_target` in OpenRouter's public `/api/v1/models`, no key needed) that has tool use,
  the `/api/v1/messages` endpoint the proxy speaks, and pictures; Anthropic's first, then by maker. Typing in the
  launcher's box also matches every model that can act (tools and messages; `:batch` and other variants left out).
  Fetched off the UI thread only once OpenRouter is chosen, cached in `local/cache/openrouter-models.json`
  (`%LOCALAPPDATA%\Sojourn\cache` packaged) and fetched again when the cache is 3 days old; offline, the cache or a
  list built in. A chosen tag stays as chosen when its family moves on. The launcher's box takes any id typed; the
  headset's row steps through the list (the name, or Claude's tags, under its label); Access's Enter steps it,
  Control+Enter takes any id.
- **relay** ("Studio relay"): a personal token (`sqr-...`) from Studio Quire (> The studio relay, below).

**Putting a key in**: the launcher's key box (paste it; its shape says which provider it's for and switches to it;
kept only from the person's own keys or mouse, `RealPress`; × twice forgets it). Access: Enter on the key row opens a
password box (Control+V or typed), Delete twice forgets it. `python tools/assistant.py key [FILE]` keeps one from a
file or typed unseen. Nothing else takes a key: no agent API op, setting or the assistant itself
(`assistant_provider`, `openrouter_model`, `openrouter_data_collection` and `auto_mode` are the person's own, read-only
on the API; the shell guard refuses `assistant.py key`).

**No data collection** (OpenRouter; on by default, the user, 2026-10-29): every request the proxy sends to
OpenRouter's `/v1/messages`, the classifier's and background calls' too, carries OpenRouter's provider preference
`"provider": {"data_collection": "deny"}` in the body (its API reference lists `provider` for the Anthropic-compatible
endpoint, checked 2026-10-29). OpenRouter then routes only to providers whose stated policy is not to store or train
on prompts: a policy, not a guarantee, and not zero data retention (`zdr: true` is stricter; not set, since whether
Claude's providers there meet it is unchecked and a model with none would stop working). If no provider for the model
keeps to it, OpenRouter refuses (404 "No endpoints found matching your data policy"): the state becomes `no_private`
and the person is told to choose another model or allow data collection. Set in the launcher and Access's Alt+0 (a
provider-level choice, not in the headset's menu).

**How keys are kept**: one file each in the keys folder (`local/secrets/assistant` from the repo,
`%LOCALAPPDATA%\Sojourn\secrets\assistant` otherwise), encrypted for the Windows account (DPAPI, entropy "Sojourn
assistant key"): the client only encrypts (`Assistant/ProviderKeys.cs`), the sidecar only decrypts (`provider.py`).
Never in settings JSON, a log or an environment. **Claude Code never has a key**: the sessions' `ANTHROPIC_BASE_URL`
is a proxy in the sidecar on loopback and their `ANTHROPIC_API_KEY` its per-run token; the proxy adds the key on the
way out and streams the answer back, so a shell that dumps its environment finds only the token. Claude Code's own
`CLAUDE_CODE_SUBPROCESS_ENV_SCRUB` can't be used instead: it forces the permission mode to default (checked, 2.1.314),
and the assistant needs auto mode. The folder is under the deny rules and the shell guard (DPAPI calls, the provider
module, the key variables), and auto mode's hard rules name it; `assistant_guard.py` has six cases for it.

**What works with which** (checked against the docs and the mock, 2026-10-29; no real keys on this PC yet, checked
2026-10-31):
- Claude account and Anthropic key: everything. With a key, auto mode's classifier runs as Claude Code's own requests
  through the proxy if Anthropic doesn't review in-band.
- OpenRouter with `claude`: everything, by OpenRouter's own Claude Code integration (its Anthropic-compatible
  endpoint, `https://openrouter.ai/api`, Bearer key): model ids mapped (`claude-haiku-5-5` ->
  `anthropic/claude-haiku-5.5`), effort and thinking passed through. WebSearch (an Anthropic server tool) unverified
  there.
- OpenRouter with another maker's model: Claude Code still sees a Claude id (so auto mode stays on: by its docs it
  starts in manual for other makers' models), the proxy swaps in the chosen id, and **the safety classifier stays on
  Claude** (`CLAUDE_CODE_AUTO_MODE_MODEL` pinned to Sonnet 5, sent as `anthropic/claude-sonnet-5`, a small cost on their
  OpenRouter); WebFetch's summaries and other background calls go to Claude Haiku there. Effort, thinking, context
  management and Anthropic's beta headers are left out for it; a model that can't see gets a line instead of each
  picture (look, describing and HUD pictures don't work, said in the menu's caveat and to the model); one without tool
  use isn't offered as working at all. Its context window comes from OpenRouter's list and the handoff line moves to
  80% of it. The prompt tells the model what it is (Claude Code's own text calls it Claude). How well another model
  follows Claude Code's tools and our prompts is untested.

**Without one that works** (none chosen, not signed in, no key, refused, out of credits or allowance, no tool use):
the sidecar runs (hearing, the panel), no sessions start, the panel says once how to switch it on, and each request
gets the same words back. A key refused mid-sitting (401/403/402 through the proxy) turns the state and the words to
say so. `tools/provider_check.py` checks all of it against `tools/provider_mock.py` (decoy keys, nothing spent; the
data preference on every OpenRouter request and its refusal, auto mode off); `provider_check.py real
anthropic|openrouter [MODEL]` with a kept key sends one short request and one picture.

### The studio relay

For people the studio covers, such as the blind tester: Anthropic's API through https://relay.studioquire.org, a
Cloudflare Worker holding the studio's key with a monthly cap per token (`tools/relay/README.md`; tokens made and
capped by `python tools/relay.py`). The proxy keeps and adds the token as it does a key, so it works exactly as an
Anthropic key does. Its check is the relay's `/relay/usage` (costs nothing); a month's allowance used up (402 from the
relay) is the state `allowance`, said plainly with the day it comes back, and the first request that goes through
after it clears it. The launcher shows the choice only once a token is kept or it's chosen; Access lists it always.

## Auto mode off (harness.py MANUAL_TOOLS)

The user's ask (2026-10-29), a first design they mean to revisit. A setting, on by default: in the launcher's settings
and Access's Alt+0 (not the headset's menu); `auto_mode` is read-only on the API. Off:
- **No classifier calls at all** (auto mode's reviewer is a Claude model, a Claude cost on the person's account even
  with another maker's model on OpenRouter): the sessions run in Claude Code's `dontAsk` permission mode (anything not
  allowed by a rule is refused, never asked), and provider.py leaves out `CLAUDE_CODE_AUTO_MODE_MODEL`.
- **Only what's safe without a reviewer**: the sojourn tools (the client's own confirmations hold for every caller as
  ever), Read/Glob/Grep (the deny rules hold; allowed in its own folders and Sojourn's docs), Edit/Write in its own
  folder and scratch folder. No shell or scripts (the shell can reach anything the deny rules can't see), no web search
  or fetching (a query or a URL could carry what was said in SL away, which only the classifier's hard rules hold
  back), no helpers. Jobs and building from a spec still work (they're sojourn tools); generated textures and meshes
  don't (they need scripts).
- **Said plainly**: the model's prompt says what's limited and that turning auto mode on gives it; the guard's refusal
  says the same; Access's row says "off, no shell, web or helpers".
- Switching it restarts the sessions as a provider change does (it rides in provider.py's settings).

## The person's choices about it

All in the settings menu's assistant section, saved with the rest (`local/client/vr-settings.json`). The assistant
changes replies, name, character and look only when asked (its `assistant_settings` and `panel_look` tools; the prompt
says so):
- **Replies**: on the panel (the default), or spoken too. Spoken replies are Kokoro (82M, Apache-2.0, int8 ONNX, ~0.7 s
  for two sentences on the CPU) in `assistant/voice.py`, played on the default output device; 11 English voices,
  af_heart by default; Windows' own voice if Kokoro's files are missing. Starting to talk or a new request cuts it off.
  While the person's screen reader reads them, spoken replies keep quiet: one voice, theirs (`docs/screen-reader.md`).
  It speaks only to the person, never into voice chat: with voice chat's engine up, `Speaker.sink` posts them to the
  client (`assistant/clientaudio.py`, POST /play), which plays them in its own mix, so they also feed its echo
  canceller (`docs/voice.md`). The microphone, too, is voice's when its engine is up (GET /mic: echo-cancelled, kept
  from residents while the button is held). `SOJOURN_TTS_OUT=dir` writes each reply to a WAV instead (tests).
- **Name** and **character**: none and plain by default. Set by asking ("I'd like to call you Wren", "be warmer, a bit
  cheeky"); once set, the menu has a switch for each ("called Wren", "its character" with the words quoted). The system
  prompt then says what the person would like ("They'd like you to use the name Wren with them", "They'd like your
  replies to have this character, in their words: ..."), never "you are Wren": prompts tell a model plainly what's going
  on (user, 2026-10-25). A change restarts the sessions after the request in progress, carrying the sitting's recent
  exchanges.
- **Look**: `docs/assistant-panel.md` > The look.
- **Touching others' things**: "ask before touching others' things", on by default (> Confirmations). The assistant
  has no way to change this one.
- **Memory**: on by default; a switch, and "forget memory" (clear, then "sure?") while anything is kept. Off: the index
  isn't loaded and the prompt says memory is off; the files stay until cleared. Both are the person's own: the agent
  API's `assistant_memory` setting is read-only. See Memory.

## How it fits together

```
client (C#)                                          sidecar: assistant/main.py (Python, the venv)
  input: talk/type/press ──► `sojourn` events ─────►   ears.py   mic while talk is held -> Parakeet (or Whisper, Soniox): Hearing
  Pointer: ray -> FrameRenderer.Pick -> id      ◄──────  brain.py  a Claude Code session per model (Agent SDK)
  Panel (Skia raster) -> overlay / quad layer   ◄──────  tools.py  ~50 tools over the agent API (HTTP :9720; moretools.py)
  SojournAssistant ops (panel, pointer, findInventory)          build.py, jobs.py (own tools), home.py (memory)
                                                                harness.py: Claude Code's tools, auto mode, deny rules
  Confirmations: LeapDispatcher.Gate (every caller)
  LiveSnapshots: the snapshot ops
```

- **Client** (`src/Sojourn.Session/Assistant/`, shared with Sojourn Access): `AssistantHost` (talk/type/press, raster
  pump), `Panel` (state and look), `Pointer`, `Confirmations`, `AssistantApi` (the `SojournAssistant` pump),
  `LiveSnapshots`, `Sidecar` (starts the Python side in a kill-on-close job object, so it and its Claude processes die
  with the client); `SessionHost` starts them. The VR client's own (`src/Sojourn.Client/Assistant/`): the panel's
  placement, the reader's view, pictures with the menus (`SeenShots`), UI size; `Program.cs` `DesktopAssistant` /
  `HeadsetAssistant` wire input, pointer and panel per frame.
- **Events** to the sidecar (kind `sojourn`): `talk_down`, `talk_up` (with the pointer's `target` at release and
  `first_target` at press), `typed {text, target}`, `pick {id, label}`, `pointed {target | cancelled}`, `typed_answer
  {text | cancelled}` (below), `mode {mode}` (the model setting changed), `prefs` (carries `memory`), `memory_cleared`
  (the menu's clear; restarts the sessions), and for tests `audio {path}`.
- **Sidecar** (`assistant/`): `main.py` (event loop, hello every 4 s so the panel knows it's up, exits 20 s after the
  client is gone), `ears.py`, `brain.py`, `tools.py`, `build.py` (a whole object from a spec), `jobs.py` (background
  jobs, and the assistant's own tools), `home.py` (its folder and memory), `harness.py` (what the sessions run under),
  and the system prompt in parts, all editable: `prompt.md` (plain, no persona), `prompt-powers.md` (its wider tools
  and their limits), `prompt-memory.md`, `prompt-job.md` (jobs only).
- **Seeing**: the snapshot ops (`LLViewerWindow saveSnapshot`) take the next frame's first eye as the person sees it,
  minus the panel and pointer, scaled to fit the size asked. The camera is never moved for a snapshot. With `showui`
  (dev.py shot, not the assistant's look) it's what the person sees: the eye as it went to the display with the
  headset's quad layers composited over it on the CPU, as the compositor blends them, and `close` adds one menu close
  up (`Assistant/SeenShots.cs`; the HUD screen, pages and the hands' layer aren't drawn).
- **Pointing**: `FrameRenderer.Pick` / `RegionScene.Raycast`: prims on the triangles of the LOD drawn last (unrigged
  prims, sculpts and meshes keep CPU positions and indices after upload for this), avatars on a 0.64 m box of their
  height, terrain by marching the heightmap; across loaded neighbours; HUDs and your own avatar and everything it wears
  skipped, known by its agent id in every region (a local id is per region and lags teleports and crossings). A hit on
  an attachment reports the wearer (`worn_by`), a child prim its linkset root (`root_id`). Names come from the world
  model or one `TesselObjects get` (cached). `Pointer.Current` is only ever this moment's: not cast for 0.75 s it's
  nothing, and the ray on the panel, a board or the feed clears it, so a stale target never stands in for "that". The
  one exception is chosen by the person: while a thing's card is up (`docs/vr.md` > A thing's card), or a request
  begun from its ask key is still being spoken, that thing is `Pointer.Pinned`, "that" whenever the pointer is on
  nothing else (`Pointer.Meant`, which the listening capsule names too).
- **The headset's pointer** (`Control/HandPointer.cs`; `docs/vr.md` > Controls): off by default, so usually nothing is
  pointed at. Then each request's target is what's in the middle of the view (`Pointer.Looking`: the head's forward
  ray, objects and people only, a few times a second), marked `looking: true`; the context line says "pointing at
  nothing (in the middle of their view: ...)", and the tools never take it as "that". When the assistant can't tell
  what's meant it calls `point {question}`: the panel puts its question up with the amber dot and what the ray is on,
  the ray comes up on the right hand whatever the toggle, and the trigger on something (a click on the desktop) sends a
  `pointed {target}` event, which answers the tool (`{pointed}`; words said instead answer it `{said}`, as ask_choice;
  its × `{cancelled}`) and makes it "that".
- **Checking a word by typing** (the `confirm_text` tool; the user's call, 2026-10-27): everyday words matter more than
  names in transcription, and a name only matters when the models have never heard it. So when a name or unusual word
  matters to the request and nothing known or found matches it (people around, friends, places, the inventory), the
  assistant asks the person to check it by typing, its best guess already typed in; usually they just send it.
  Otherwise it acts on the best match without asking (`prompt.md`). `SojournAssistant askTyped {question, text}`
  (`AssistantHost.AskTyped`) puts the question on the card, the request it's about dim above, and the guess in a field
  below with the caret at its end (`Panel.TypeAsk`); in the headset the keyboard comes up for it by itself
  (`HeadsetKeyboard.Show`, its strip showing the question: the one time it comes up without its key pressed). ↵ or
  Enter sends what's there as `typed_answer {text}` (the tool gets `{typed}`); the keyboard's ×, the card's ×, Esc, or
  sending nothing is `{cancelled}`; speaking instead answers it `{said}` as for ask_choice, and the tool then takes the
  question and the keyboard away (`askTyped {question: null}`; `panel {clear}` does too). A keyboard up for a request
  or a page gives way to it. On the desktop it's the typing line, so T types rather than talks there. 180 s without an
  answer is `{no_answer}`. It carries nothing but the assistant's question and the person's own words, so it's on
  incognito's allowlist and blind sessions have the tool. Dev mode: `dev.py confirm "question" "guess"`, the answer
  coming back on the watch as `typed answer: ...`.

## Hearing (ears.py, hearing.py, cloud.py)

What turns the person's speech into a request. The `hearing` setting (menu > assistant, `SojournSettings`, the
settings tool) picks the recogniser: local by default, a cloud service as an opt-in (the user, 2026-10-27). Never the
GPU: in the headset it costs frames, and the contention brought the view's jitter back.
- **parakeet** (the default; the user's pick, 2026-10-27: quicker, so a slip is quicker to say again): NVIDIA's
  Parakeet TDT 0.6B v2 (CC-BY-4.0), int8 in sherpa-onnx, then the name pass. A tenth of Whisper's time and CPU and
  better on everyday words. Whisper doesn't load beside it (the name pass reads Whisper's tokenizer.json from the
  downloaded model); it loads only when chosen, for Soniox, or when Parakeet is missing or fails to load (with a
  notice).
- **whisper**: faster-whisper small.en, int8, 4 threads, below-normal priority, with an `initial_prompt` of the names
  around the person, then the name pass. Better on names (> Measured); the one for other languages.
- **soniox**: Soniox's cloud service, streamed while the button is held (> Soniox). Offered in the menu only once the
  person's key is in place.

**Starting up**: the sidecar opens the mic, says its first hello within seconds and loads only the chosen model, in
the background (Parakeet about 2 s; Whisper up to about 30 s while the client loads the world). Until it's ready the
hello says `hearing_ready: false`; a press shows "hearing is starting…" in the listening capsule and, on release, that
what was said is heard once it's ready (the user, 2026-10-31: show that you can't speak yet). Nothing said meanwhile is
lost: the client's mic keeps each finished utterance for two minutes (eight at most), taken by its own press (`GET
/mic?down=&up=`, the talk events' times), so it's heard once the model is up, even a press made before the sidecar
read any events (the log line adds "kept until hearing was ready"). Without voice's engine, the sidecar's own mic
records a press only once its event loop runs.

**The words** (`hearing.Words`): `SojournAssistant hearing` (`Assistant/HearingWords.cs`: from what the client already
holds, never a fetch) gives people within 96 m nearest first (display and user names), the region, parcel and
neighbours in view, friends (online first), what they wear (the outfit folder's links), regions visited this session
and landmarks, and their own inventory folders breadth first (brands, outfits); plus names in the memory notes
(capitalised runs not at a sentence's start, a single word only if the notes never write it in lower case), and a
short general vocabulary (`VOCAB`: freecam, culling plane, rez, HUD, prim, Lelutka, Maitreya...; nothing about one
person: their friends and places come from their own context). Fetched every 15 s and again when talk starts, never
between the release and the transcript; held in memory only, never logged. Ranked: nearby people, where they are,
friends online, memory names, the vocabulary, worn, other friends, places, folders.
- Whisper's prompt: as many as fit in 200 tokens by its tokenizer (faster-whisper keeps the last 223), written least
  relevant first so the nearest the speech are the most relevant, as "A, B, C.". A transcript that's only a run of the
  prompt's own words (said back over noise) is dropped.
- The name pass (`fix_names`, after either local model): a span of 1-3 words close to a name becomes the name, compared
  by spelling roughly as heard and by consonant skeleton ("Quill on" -> Quillon, "freak him" -> freecam, "calling
  plane" -> culling plane). Guards against false names: everyday words (a single token in Whisper's vocabulary, or a
  short list) only by spelling at 0.88 or a whole skeleton across several words ("rock" stays, not Rook); names under 5
  letters only by exact letters; a span with an extra word must fit better than its parts.
- The context line carries up to 40 of the names (not the vocabulary), labelled as names they might say, not who's
  here, so Claude can match a near-miss ("TESL" -> Tessel).
- **Incognito**: blind, the client's allowlist hands back only `worn` (with `blind: true`), and the sidecar leaves out
  the memory names and whatever it fetched before the spell: the people and places around them are in no prompt, terms
  or context line, since a name put in where none was said would reach the blind session in the transcript. The client
  marks `sojourn` events `blind` while blind (`PrivateMode.BlindEvent`), so talk_down knows at once. Seeing: all of it,
  as the assistant sees everything then. Nothing of the audio or text is logged in or out of a spell (the "heard N s in
  X s (by)" line has neither, and pauses with the console).

### Soniox (cloud.py)

The user's rule (2026-10-27): an opt-in cloud service, our pick of the best; if it neither keeps nor trains on the
audio it may also run in incognito and get the names, otherwise local-only there. Checked against the vendors' own
pages, 2026-10-27:
- **Soniox** qualifies. Its privacy policy (soniox.com/policies/privacy-policy, updated 2026-06-29): real-time requests
  are processed transiently and not stored; customer content isn't used to train or improve its models; logs hold only
  operational metadata, no audio or transcripts. Its security docs (soniox.com/docs/security-and-privacy): nothing
  stored unless asked (only the async API stores, 30 days). A signed agreement would override the policy; we have none.
- **OpenAI**, for comparison (developers.openai.com/api/docs/guides/your-data): `/v1/audio/transcriptions` has no
  training and no abuse-monitoring retention but takes a whole file (no streaming input), so the wait starts at the
  release; `/v1/realtime`, which streams, keeps abuse-monitoring logs 30 days by default, and zero retention needs
  OpenAI's approval. So it would be local-only in incognito when streaming.
- Picked Soniox: streaming while held (final text moments after the release), context `terms` up to 8,000 tokens for
  the names, a verified no-keep, no-train default. $0.12 an hour of streaming (soniox.com/pricing, 2026-10-27). Its
  accuracy and real latency are unmeasured (no key on this PC, checked 2026-10-31).
- How: at talk_down a WebSocket to `wss://stt-rt.soniox.com/transcribe-websocket` (Bearer key on the connection),
  `stt-rt-v5`, pcm_s16le 16 kHz mono, English only, endpoint detection off, `context.terms` = the ranked words (300 at
  most); audio sent every 80 ms as it comes (the client's `GET /mic/live?from=`, which doesn't take the utterance, or
  the sidecar's own recording), an empty frame at the end of the audio (the client's 250 ms tail included), the final
  tokens joined. Its words are used if they come within 1.5 s of the release; Whisper starts on the utterance at 0.8 s
  if they haven't, and hears it instead past 1.5 s, or at once if the connection failed (no key, down, refused).
  Timings record `soniox` or `soniox->whisper`.
- **The key** is the person's, kept as the provider keys are: `soniox.key` in the keys folder, encrypted for the
  Windows account (DPAPI), put there by `python tools/assistant.py key --soniox [FILE]`; a key in the old plain
  `local/secrets/soniox.key` is moved in once by cloud.py (encrypted, read back, then the plain file deleted).
  Decrypted at each connection, never in the environment (the sessions inherit it) or a log; harness.py's deny rules
  on the secrets folders, the shell hook and auto mode's hard rules (which name it) keep the assistant's tools off it.
  Without it, choosing soniox hears with Whisper and says so on the panel's notice.
- `tools/soniox_mock.py` stands in for it (`SOJOURN_SONIOX_URL=ws://127.0.0.1:9739`, a decoy key in
  `SOJOURN_SONIOX_KEY_FILE`, a plain file only tests use; modes ok, slow, error, silent; `--log` records the config,
  the terms and the audio's length per session).

### Measured (2026-10-27)

TTS speech (Kokoro, several voices) through Sojourn's own capture chain or a headset-like simulation, plus real voices;
"names" is the share of names said that came out right, "false" names that came out where none was said.
- The benchmark that chose the approach (`local/asr-bench`): small.en unprompted 52% of names, prompted 91% (0 false),
  and 100% on rebuilt versions of the user's real failures ("Tessel" as "TESL", "freecam" as "freak him", "culling" as
  "calling"); +0.2 s and ~0.7 CPU-s a request. Parakeet: word errors on real voices 20.2% vs small.en's 24.8%, 0.1 s
  and a tenth of the CPU, names 49%; with its own hotwords it made names up, hence the name pass instead.
- Two sets with names the pass was never tuned on (34 and 32 sentences, 2 voices, clean and headset; the second written
  after the pass's last change, so it's the honest one):

  | | names | false (of 128) | word errors |
  |---|---|---|---|
  | small.en unprompted (first set) | 34-38% | 0 | 11-13% |
  | small.en prompted | 72-74% | 0 | 5.1-5.3% |
  | small.en prompted + name pass | 92-94% | 2 | 2.7-3.6% |
  | Parakeet | 46-50% | 0 | 8.9-9.2% |
  | Parakeet + name pass (the default) | 80-86% | 2-3 | 4.3-5.3% |

  The false names: "heart" -> Hart (someone called Hart nearby) and Parakeet's "Katia" (for Leocadia) -> Catwa.
  Near misses the context line's names are there for: "Amache" for Amacci, "beautiful harbor" for Moonfall Harbour.
- From the release to the text (desktop, a 2.7 s request, the sidecar's own recording): whisper 1.00-1.05 s, parakeet
  0.11-0.13 s, soniox (the mock, answering at once) 0.06-0.09 s, 0.37 s through the client's mic (its 330 ms tail).
  Whisper's prompt adds ~0.1-0.2 s and ~0.6 CPU-s to its 1.0 s and ~3.7 CPU-s (4 threads).
- Untested: real voices, accents, rooms and the Quest's mic; display names in decorative Unicode; crowded places where
  200 tokens hold only the nearest dozens; prompted Whisper on real voices' everyday words.

## Claude

- **The assistant model setting** picks who takes a request first (the person's setting, user 2026-10-21):
  - **quick** (the default): Haiku 5.5; multi-step jobs go on to Opus.
  - **sonnet**: Sonnet 5.5; multi-step jobs go on to Opus.
  - **opus**: Opus 5.5 takes everything itself, no handover: slower to start, surer.
  Set in the headset's settings menu (the "assistant model" row), F8 on the desktop (cycles; in the headset's mirror
  window too), `/model quick|sonnet|opus` in the console, `--assistant-model` on the command line (for the session,
  kept if the menu then saves), or the `SojournAssistant model` op (`tools/assistant.py model opus`). Saved in
  `vr-settings.json`. The client sends a `mode` event and the sidecar switches at once (the hello answer carries the
  mode too); the request in flight finishes where it started.
- **Effort** is a setting per model: haiku, sonnet and opus (opus's covers it as planner too), each low, medium, high,
  xhigh or max; **medium for all three by default** (the user, 2026-10-27). The headset's menu shows, under the model
  row, an effort row for each model the mode uses (quick: haiku and opus; sonnet: sonnet and opus; opus: opus). On
  the agent API (and the `settings` tool) all three: `SojournSettings` `haiku_effort` / `sonnet_effort` /
  `opus_effort`, saved as `assistantEffort`. The client sends an `effort` event `{effort: {haiku, sonnet, opus}}` (the
  hello answer carries them too); the sidecar (`brain.py` set_efforts) brings a changed model's warm session back up at
  the new effort now if it's idle, else after the request in progress, which finishes at the effort it started with;
  a session that starts later comes up at the new one. Restarting drops its conversation; the sitting's recent
  exchanges go in with its next request (> Context across models). Background jobs choose their own effort (> Jobs).
- The **planner** is Opus 5.5; the first model hands over with `escalate` (several steps or careful judgement, to be
  done now; builds and other long work go to a background job instead). It starts the moment the escalate call
  arrives (the first model's last turn finishes in the background), with the request and the same context line. The
  opus mode uses the same session, so it keeps one conversation.
- **One session per model** (`brain.py` MODELS): the mode's first model and Opus are kept warm (a session whose context
  is dropped reconnects in the background, not at the next request); another lingers until 15 idle minutes, then
  closes. A switch brings the new model up in ~0.6 s. `SOJOURN_HAIKU` / `SOJOURN_SONNET` / `SOJOURN_OPUS` = a model id
  run another model in its place (tests); the effort stays the setting's.
- **Context across models**: each exchange of the sitting is recorded with who answered it; a session's next request
  carries the ones it hasn't seen (another model answered: a handover, a mode switch), the last four within 15
  minutes, as "Earlier in this sitting". So "remember walnut" in one mode is known after switching to another.
- **The panel** shows the model at work, faintly, while a request runs ("haiku 5.5", turning to "opus 5.5" on a
  handover), so a slow answer explains itself. A switch on the desktop (F8) says so on the panel too.
- **Why Haiku for quick** (2026-10-21): the model's time to first token is most of a request's latency, and it's the
  server's (Claude Code sends the request within milliseconds). Through the SDK with a cached prompt, Haiku 5.5 was
  0.4-1.9 s every time, while Sonnet and Opus had spikes of 5-17 s on some turns (it moves with server load). On a
  22-request test Haiku picked the same tools as Sonnet and Opus, after two prompt fixes: never tell the person
  something through `say` (public chat), and escalate rather than build a stool as one cylinder (hence the prompt's
  one-prim rule). Haiku medium measured as low; Opus low and medium were about as fast as planner, so medium stays.
- **Session options** (`ClaudeAgentOptions`): Claude Code's own tools plus ours, `permission_mode="auto"` and
  harness.py's settings and hooks (> What it may do), `strict_mcp_config` (not the account's connectors),
  `setting_sources=[]` (no settings files, hooks or CLAUDE.md but ours), `CLAUDE_CODE_PLUGIN_DIRS` emptied (the user's
  mods load through it otherwise), `CLAUDE_CODE_DISABLE_ADVISOR_TOOL` (otherwise Claude Code gives the model a
  server-side advisor, a consult with a bigger model that costs seconds and never shows as a tool call),
  `ENABLE_TOOL_SEARCH=false` (our tools load up front; with tool search on, the first call to one costs a search turn),
  Claude Code's tools for other work disallowed (`harness.UNUSED_TOOLS`: design sync, worktrees, notebooks, code
  review, skills, agent teams; ~11K tokens a call), its background calls (a session's title) on Haiku
  (`ANTHROPIC_SMALL_FAST_MODEL`), an hour's prompt cache for Haiku and Sonnet request sessions
  (`CLAUDE_CODE_PROMPT_CACHE_TTL`, `Session.cache_env`; five minutes for Opus and jobs; `docs/assistant-cost.md`),
  `--no-session-persistence` (no transcripts), cwd `local/assistant/work` (its scratch folder). Context: the sitting's
  conversation, dropped after 15 idle minutes; up to the models' full 1M tokens, never compacted, handed off near the
  limit (> Long sessions).
- **The system prompt** is `prompt.md`, a line for the session's role, and the client's agent API as a compact op list
  (read from the client at start: every pump but SojournAssistant, LLViewerWindow and those a tool of its own covers,
  `brain.py` HIDDEN_PUMPS, and single ops a tool covers, HIDDEN_OPS, so each thing has one way to it). So client_api
  needs no lookups first; `api_help` is a tool only if the list couldn't be read. The op list is 40.7k characters with
  what the tools cover left out (60.2k with everything, 2026-10-29). It reaches claude as a file
  (`local/assistant/prompts/`, `--system-prompt-file`): on the command line it passed Windows' 32,767-character limit.
- **The context line** starts each request: time, region and position, parcel, facing (the camera's heading) and the
  point 1.5 m ahead, headset or desktop, the pointer target with its id, the jobs and what's on screen, and the names
  they might say (> Hearing), so a misheard one can be matched. Its three ops run at once, each capped at 1 s (a parcel
  the client hasn't heard about yet waits on the region).
- **A request cut off** before its end (`Brain.stop` interrupts, then cancels the handler after 10 s; a handed-over
  tail is cancelled after 30 s) leaves its turn's last messages queued in the session, ending in an
  `error_during_execution` result; a session reads such a turn out before its next request (or starts afresh, the
  recent exchanges carried in, if it won't end), so the next answer doesn't arrive one request late. Only busy
  sessions are interrupted; a request that errors before doing anything is asked again once; errors show as plain
  words ("That didn't go through. Try again?" / "That stopped partway..."), and timings and the log record them
  (`error`, `retried`).

### Tools

`tools.py`: look, describe (Sojourn Access's; the description written in one lean call, `describer.py`, and handed
back whole: `docs/assistant-cost.md`), capture (pictures and videos saved for the person), animations (what's
animating them; stop it), nearby, inspect (with `costs`: land impact and Script Info), find_items, wear, take_off, say, message, recent_chat,
go, sit, stand, touch, hud (the person's HUDs: pictures, presses by point, part or name, buttons read off a HUD's picture saved by name, the board; `docs/hud.md`),
media (pages and video on faces, the parcel's and offered pages: read, press, type, navigate, play, a picture, up
close, a face's settings, configure and remove_media; `docs/media.md`), search (> Search), pay, report (> Reporting
abuse), give, buy, remove_objects, dialogs, answer_dialog, ask_choice, confirm_text (> How it fits together), show
(`docs/assistant-panel.md` > Showing things), reader, assistant_settings, panel_look, draw_distance, settings (every
setting the person can change, through `SojournSettings`, `docs/client.md` > The agent API; its description lists the
names, so a new setting goes there too), client_api (any raw op but snapshots by path), escalate (Haiku and Sonnet
only), point (asks the person to point, above), api_help (only without the op list); `build` (build.py); its own
(jobs.py): forget, start_job, jobs, tell_job, stop_job, resume_job, and in a job progress; and Claude Code's (Read,
Write, Edit, Glob, Grep, Bash, PowerShell, WebSearch, WebFetch, Agent and the rest).
- wear/take_off/give/go take words as well as ids and hand back candidates when the words fit several things, so a
  clear request is one call. `go` teleports in the headset (no smooth glide) and walks under 40 m on the desktop.
  `ask_choice` puts up to six keys on the panel and waits for a press or for what's said next.
- `capture` takes stills' size and camera, 360s, Save to Inventory and postcards (SojournCapture is hidden from the op
  list, as SojournMedia is, so only the tools have them).
- **By area** (`moretools.py`, 2026-10-29): one tool per area people ask about often, each an `action` over that area's
  ops, taking words, "that" and ids as the tools above do; what's rare stays with client_api (the op list has it).
  prompt.md has one line naming them.
  - land (SojournLand: about, region, covenant into the reader, access, ban/unban/allow/unallow, eject, freeze, a
    pass, buy, land for sale by search or the map, About Land settings, lag: the lag meter's verdict with the top
    scripts and colliders, return, the region's emergency switches, terraform at the ground pointed at, holdings);
  - map (SojournMap where, stats, show, hide, friends; SojournBeacons track and untrack);
  - people (block and unblock, how someone's drawn, profile notes, reset skeleton, refresh attachments, resync);
  - me (display name and its change, money this session, away/DND status and autoresponses);
  - group_chat (each group's chat, snooze, on/off, moderating a member's text);
  - voice (status, mute and volume per person, the mic, calls, conference, the call switches and one group's calls
    turned away, moderation);
  - ao (the client AO and the pose stand; editing sets and picking a folder stay client_api's);
  - environment (the person's own sky: presets, their settings, shared, clouds);
  - undo (history, undo, redo: "undo that" is one call), scripts (the Build menu's chores and Script Info);
  - item (an inventory item's picture as an image, as hud's look; favourites; thumbnails);
  - keep_away (SojournSafety: hide/unhide things, sounds and blocking them, particles, release keys, bumps);
  - sojourn (SojournRelease: about, a problem report with the person's words that they confirm, the viewer
    statistics; 2026-11-01, not yet checked with typed requests).
- Checked with typed requests (quick mode, Haiku medium): each area's everyday asks ("who owns this land?", "is it me
  or the region that's lagging?", "turn my AO on", "make it sunset", "undo that", "mute X's voice", "what does the
  covenant here say?", "reset my skeleton"...) went to the tool meant in one call, 3.5-7 s. client_api's description
  says modify is for changing what's there: without it a recolour ("make that box blue") went to `build`.

## Long sessions

The user's direction (2026-10-26): a large context, no automatic compaction, and the assistant writing its own handoff
near the limit; the assistant is told how its harness handles this (`prompt-powers.md` > Long sessions; jobs:
`prompt-job.md`).
- **The window**: every session runs `claude-<model>-5-5[1m]`, the models' 1M-token context (Claude Code gives these
  three 1M already, checked 2026-10-26 with `claude -p --output-format json`'s `modelUsage.contextWindow`; the suffix
  keeps it so). The user's "about 800,000" is the handoff line below rather than a smaller window: Claude Code's window
  setting (`autoCompactWindow`) only steers compaction.
- **No compaction**: harness.py's settings say `autoCompactEnabled: false` and `idleCompaction: false`, and the env
  `DISABLE_AUTO_COMPACT=1`. The user keeps auto-compaction off in their own Claude Code settings, but these sessions
  load no settings files (`setting_sources=[]`), so it's said here.
- **Handoff** (`brain.py`): a session's context is read from each model call (input, cache and output tokens of the
  stream's `message_start`/`message_delta`; each request's timing line has it as `context`). A request session past
  `HANDOFF_AT` (800,000) gets one more turn after the request it's on (the person already has their answer): put what
  lasts into memory, and write `home/handoff-<role>.md` (what they're doing and after, what's pending, what recent
  requests may refer back to, what's on screen). Then it closes and comes up fresh; its next request carries the note
  (read once, then deleted) with the sitting's last exchanges and the memory index. Nothing interrupts a handoff turn
  ("stop" included), and a request that arrives meanwhile waits for it (a few seconds). Past `HARD_AT` (900,000)
  mid-request, a tool call's PostToolUse hook tells it to finish quickly and write the handoff before replying.
- **Jobs** hand off through their notes: past `HANDOFF_AT`, after a tool call, the hook tells the job to bring its
  notes up to date and end its turn; jobs.py then carries on in a fresh session from the notes, as after a restart.
- The 15-idle-minute reset has no handoff: it's there so other people's words don't linger, and memory is what
  carries over then.
- A request session starts at ~51,500 tokens (Claude Code's own prompt and tools, ours, the op list; 2026-10-31); a
  small request adds ~200-1,500. A Haiku session past `HAIKU_FRESH_AT` (90,000) starts fresh after the request it's on,
  carrying the recent exchanges as after any restart: Haiku 5.5's price steps up fivefold past 100K
  (`docs/assistant-cost.md`). `SOJOURN_HANDOFF_AT` / `SOJOURN_HANDOFF_HARD_AT` move the lines for tests; checked
  that way with a request session (a fresh session answered from its handoff note) and an Opus job that handed off
  twice mid-build and finished from its notes.

## What it may do (harness.py)

Decided with the user (2026-10-25): the assistant should be about as capable as Tessel at interaction, search and
making things, guarded by permissions and Claude Code's auto mode rather than by a stripped-down tool set. So the
sessions get Claude Code's own tools (files, shell, web, helpers) in auto mode, where a classifier model judges each
action that no rule settles, and these layers, hardest first:
1. **The client's confirmations**: they hold for every caller of the agent API, the assistant's curl and scripts
   included (> Confirmations). On the desktop a confirm press counts only from a hand on the mouse
   (`Assistant/RealInput.cs`): a click posted to the window (as `tools/winmouse.ps1` does) leaves the button's state
   untouched, and injected input (SendInput) is marked as such, so neither confirms. `SOJOURN_TEST_INPUT=1` at start
   lets tests confirm with posted clicks. Cancel takes any press.
2. **Deny rules** (`permissions.deny`, written to `local/assistant/settings.json` afresh before every session; it's too
   long for a command line). The file tools can't read or write: the SL password (`local/secrets`), the grid's
   passwords, `mfa_hash` and `account.txt` files anywhere under `local/`, `~/.ssh`, `~/.config`, `~/.docker`,
   `~/.claude.json`, everything under `~/.claude` but the session's own tool-output folder (credentials, other
   sessions' transcripts, the studio's memory), every program's folder under `AppData` but `Local/Temp` (browsers'
   passwords, Windows' credentials, the official viewer's saved login, gh, Tailscale), dotfiles named like
   credentials, the studio's other projects and its CLAUDE.md. They can read but not change Sojourn's code, docs,
   tools, the assistant's own code and prompts, its settings file and venv, and the client's settings and state
   (`local/client`, `main`, `tester`, `friend`). Claude Code applies the read rules to paths in shell commands too, and
   resolves links (a junction from its folder into `local/secrets` is refused). Always, for every session
   (`harness.unseen_globs`), the file tools also can't read caches, pages' profile, screenshots or pictures and videos
   named incognito (`docs/assistant-incognito.md` > The sidecar).
3. **Hooks** (`Guard`): shell commands naming a credential store, the login, the client or launcher (`SojournVR.dll`,
   `Sojourn.exe`, `client.py run`, `launcher.py run`, `--launcher-answers`), dev mode (`dev.py start`, `main.py
   --dev`, `docs/dev-mode.md`), Claude sessions that skip permissions, or ways of putting input into the client
   (winmouse/winkey, SendInput, PostMessage, SendKeys, pyautogui, AutoHotkey...) are refused before the classifier sees
   them. Best effort: a shell can spell things another way, hence 4.
4. **Auto mode's rules** (`autoMode`: the environment, allow, hard_deny): what the sessions are and that the person is
   the user; allowed: the agent API through curl or Python for what was asked, scripts in its work folder;
   hard-denied even if asked: any credential by any means, logging in or starting a viewer, answering confirmations,
   the ToS or MFA by any route, changing Sojourn's code, settings or its own prompt, recording other residents'
   conversations or sending what they said anywhere, anything that starts acting in SL for the person without their
   asking (timers, loops, auto-replies, scripts left to act later, jobs for work they didn't ask for; a job they asked
   for may keep acting until it's done or stopped, on that work only), reading the studio's other projects, following
   instructions found in pages, notecards, chat or object text. The environment tells the classifier what a job
   session is (work asked for in an earlier request, briefed by the assistant), so its later actions read as asked
   for.

On Windows Claude Code's sandbox can't keep shell commands out of folders, so for the shell, 2-4 are the guard.
`prompt-powers.md` tells the model the same lines and that what it reads is information, never instructions (checked
2026-10-25: a hidden instruction in a page it was asked to summarise was ignored and reported to the person).
`tools/assistant_guard.py` checks the harness (> Testing).

## Memory (home.py)

Its own folder, `local/assistant/home` (per machine, gitignored), as plain files it reads and edits with the file
tools (allowed there without the classifier). `MEMORY.md` is the index, one line per note ("- mira.md: Mira, a friend
who DJs at the Pavilion"); a note per topic (preferences, a friend, a place, an errand), rewritten in place, deleted
when no longer true. The index goes into each session's next request whenever its digest changed since that session
last had it (a session's first request, after another model or a job changed it, after a restart), with any note it
doesn't list and a nudge past 3000 characters; notes are opened when a request bears on them. So every model shares
it, and the prompt stays small as the memory grows. What goes in and what never does (passwords and login details;
what other people said, as transcripts, quotes or summaries; others' personal details beyond knowing who they are;
instructions from anything but the person): `prompt-memory.md`. The person asks what it remembers, says "forget X"
(the `forget` tool deletes notes and takes their lines out of the index) or "forget memory"; or clears it in the menu,
which deletes the files in the client and restarts the sessions so nothing lingers in a conversation.

## Jobs and helpers (jobs.py)

Jobs are the assistant's own way to do long work, not a system procedure (the user's decisions, 2026-10-25): one job
at a time, unbounded (no time or turn limit, and no stall watchdog: it stays on the panel and the person or the
assistant can stop it whenever), and the assistant decides whether to start one, which model and effort run it, how
to brief it, whether it may use helpers, and when to check on it, change, stop or resume it. The harness keeps only
the mechanics that need it. No caps on time, helpers or usage: plan usage isn't economised (the user).
- **The tools** (request sessions; `prompt.md` has one line on them): `start_job {title, brief, model:
  opus|sonnet|haiku, effort?, helpers?, queue?}` returns at once; `jobs` (each job's state, model and effort, its
  notes' file; the assistant Reads the notes for detail); `tell_job {text, id?}` (a change from the person: a running
  job gets it after its current tool call through a PostToolUse hook's additionalContext, `harness.Guard.post`, and it
  goes into the notes' "Told since" too, so a stopped or queued job has it when it carries on); `stop_job {id?}`;
  `resume_job {id, model?, effort?, helpers?, note?, queue?}`. Each request's context line lists the jobs and where
  they stand.
- **One at a time**: a second `start_job` while one runs is refused with the reason and the choices (queue it, fold it
  into the running one with tell_job, stop that one, or tell the person) unless `queue: true`; a queued job starts
  when the running one is done (not when it's stopped or fails: then the finish notice says what's waiting). The
  panel's line shows "(then the footbridge)".
- **A job** is a Claude Code session of its own with the model and effort chosen, the assistant's prompt plus
  `prompt-job.md`, the same tools (and `progress`), permission mode, deny rules, hooks and confirmations as a request;
  without helpers it doesn't get the Agent tool (`disallowed_tools`). It runs until its turn ends: messages told after
  its last tool call start another turn; a session that ends in an error after the notes changed carries on in a
  fresh one from them; otherwise it's failed.
- **Notes**: `home/jobs/<id>.md`: a header (state, asked, started, model, helpers), the brief, where the person was,
  plan, done with ids, left, to clean up, told since; kept by the job. A restart marks a running or queued job paused,
  never restarts either, and puts "the cabin stopped when the assistant restarted; ask to carry on whenever you like"
  on the panel.
- **The plan first** (2026-10-26): a job's first `build`, or TesselBuild create/rez/duplicate through client_api, is
  refused while its notes' Plan is empty (`harness.Guard` plan_first, `Job.plan_needed`), the refusal saying to write
  the plan (and Left) in and, if it has several parts, show it as a checklist; then it carries on. A mechanic, not a
  procedure: the notes are what a stopped or cut-off job resumes from, and Sonnet skipped them whatever the prompt
  said (Opus kept them). It costs Sonnet a turn (a bench 23 s -> 60 s). Shell calls to the agent API aren't covered
  (best effort).
- **Briefs**: what's wanted, not how (start_job's description and prompt.md: the person's words and what the job
  can't see, no tool names, ops or steps; the job has the same tools and its own instructions). Without this Haiku's
  briefs named ops and steps.
- **What shows**: the panel's dim job line (`jobs` on the panel op; it never brings the panel up by itself): the
  job's own `progress` words ("what it's doing now"), else what its tools are doing. The plan as a checklist through
  `show` (at the wrist, or over the build once it has a root), ticked off as parts finish (`prompt-job.md`). A job's
  elements are named `job-<id>-...` (`JobKit.show_id`; its "all" means its own), so request sessions see them as the
  job's in the context line, and they're taken down when the job ends however it ends, and at a restart.
- **When one ends**: its reply goes on the panel once no request is using it (spoken if replies are), the models get
  it as an exchange of the sitting, and if done its notes go (it writes a memory note first if worth keeping).
- **"Stop"** alone with no request in progress but a job running is a request like any other, so the assistant decides
  (it stops the job).
- **Helpers**: Claude Code's Agent tool, in jobs (when allowed) and requests alike, foreground only (several in one
  turn run in parallel; a background helper would outlive what started it, so it's refused). No cap on how many. They
  run under the same rules and can't answer confirmations either.

## Building (build.py)

- `build {spec}` makes a whole object in one call: an origin and yaw, parts in the object's own frame (shape, size,
  position, Euler degrees as llEuler2Rot or a quaternion, any TesselBuild modify key, per-face settings), made one by
  one (the client matches new prims to requests in order), then linked and named; ~0.3 s a prim locally. build's
  description and prompt-job.md name TesselBuild modify's materials (pbr over a material item, normal, specular,
  alpha_mode, projector) and undo.
- TesselBuild modify takes `tex_offset`, `tex_rotation`, `bump`, `texgen` and `physics_shape`; a linked child's new size
  is sent with its position (a scale alone once threw a child ~177 m on SL). `shiny` takes none, low, medium, high (or
  0-3), `texture` blank, plywood or transparent by name; a value of the wrong kind for any key is refused before
  anything is sent, never dropped. `animated: true|false` is the Features tab's Animated Mesh box on a linkset's root
  (reads back as `animated` in TesselObjects get).
- **Mirrors**: `reflection_probe {type: sphere|box, ambiance, clip_distance, dynamic, mirror}` or false sets the
  Features tab's probe (extra param 0x90; LibreMetaverse has no setter) and, as the build floater does, makes the prim
  phantom and fully transparent unless the call sets those. A mirror is two prims, as in the viewer
  (`docs/rendering.md` > Mirrors): the spec's `mirror: true` (or `{dynamic: true}` to show avatars) on a part makes it
  the glass, facing its own +x, dark, blank and shiny unless the part says otherwise, and adds the box probe after it
  (its +Z out of the glass, its middle plane 1 cm in front, physics shape none rather than phantom, which would take
  the linked object with it). TesselObjects get with `full` reads back each prim's face settings (`faces`: all, and each
  face that differs) and its probe as the renderer read it.
- **Flags** go as the viewer sends them (`LLViewerObject::updateFlags`: no ExtraPhysics block unless the physics shape
  changes). LibreMetaverse's SetFlags always adds that block, which OpenSim applies instead of the flags (phantom
  didn't take) and SL uses to reset a probe's physics shape to prim. Compressed updates' flags reach the agent's flag
  table too, so `phantom` sets, clears and reads back.
- **Contents**: `addToContents` selects the prim while the item goes in (as the Content tab has it), so the region sends
  the contents' serial number whenever they change, and the listing is read when it moves. An item that went in and
  left again before any listing showed it (a script that removes itself once it has run, an object's script taking a
  delivery) is reported as `gone_again`. A drop whose outcome wasn't seen is remembered for 5 minutes: the same item
  into the same prim again is refused with why unless `again: true` (on SL the assistant once ran a self-removing
  script three times by retrying).
- `TesselAgent ground {points}` gives terrain heights for placing things. Textures come from Python in the work folder
  and `TesselAgent upload` (confirmed where it costs L$); scripts through the inventory ops and `addToContents`
  (confirmed, one press each).

## The web

Claude Code's WebSearch and WebFetch, for questions about SL or the world. Searches are on the allow list; fetches
aren't, so auto mode decides on them. Replies stay plain text, no source lists. The Destination Guide (LL's picks,
https://secondlife.com/destinations, as the viewer's guide shows it) and Marketplace pages come this way; SL's own
search is the `search` tool (below).

## Search (SojournSearch, the search tool)

`Core/Agents/SearchOps.cs`, `Leap/Apis/SojournSearch.cs`. SL's search as the viewers' legacy search asks it
(Firestorm's search floater; the official viewer's search is a web page): places (DirPlacesQuery, busiest first),
events (DirFindQuery with DateEvents: on now and later today, today, tomorrow, or days ahead), classifieds
(DirClassifiedQuery). Each result is filled in by a second ask (ParcelInfoRequest, EventInfoRequest,
ClassifiedInfoRequest) with its region, a `go_to` the go tool takes ("Region x,y,z"), a description and, for events,
start time, length, cover charge and rating. Only the ratings the account goes to are asked for (its maturity
setting). Replies come in several packets, gathered until the grid has been quiet 1.2 s (8 s at most); words under 3
letters are refused, as SL ignores them. The local grid's OpenSim answers none of the three (its search module does
people and groups), so there they come back empty after 8 s; the results' parsing waits for Second Life.

## Reporting abuse (SojournReport, the report tool)

`Client/Control/ReportApi.cs`. The viewer's Report Abuse (`llfloaterreporter.cpp`), only at the person's own press:
`draft {about?, object?, category, summary, details?, picture=true}` takes the picture of their view (as the floater
does when it opens), resolves who and what (an object's owner is named with it and, as the viewer's picker does,
becomes the abuser when no one else is), checks the words fit the confirmation whole (summary 100 characters, details
280) and refuses copyright and IP complaints with where they go (the viewer never sends those); `send {draft}` is
gated for every caller (> Confirmations: reporting), its confirmation showing all of what goes to Linden Lab: who, the
category, the person's words, and whether a picture goes. A draft goes once. Sent as the viewer does:
SendUserReportWithScreenshot (the report, then the picture as J2C to the uploader it answers with) or SendUserReport,
else the UserReport message; the summary and details are laid out as gatherReport does ("Preview" off the main grid).
Categories from the region's AbuseCategories cap, else the viewer's own list. The `report` tool drafts and sends in one
call. `tools/report.py` checks it on the local grid (refusals, the confirmation, cancel sending nothing, a confirmed
send as the UserReport message; OpenSim has no report caps, so no picture there). The caps and the picture upload are
unchecked against SL, and a test there would reach Linden Lab for real.

## Pictures and videos for the person (SojournCapture)

`Assistant/CaptureApi.cs`, `Assistant/Recorder.cs`, `Capture/`, `Sojourn.Render/Realtime/FrameRenderer.Video.cs` and
`FrameRenderer.Still.cs`; the `capture` tool.
- **Where**: the person's own Pictures\Sojourn and Videos\Sojourn folders, named `Sojourn <date> <time> - <a few
  words>.png|mp4`. The client picks the folder and name; no caller gives a path (LLViewerWindow's snapshots by path
  stay closed to the assistant).
- **Pictures** (`snapshot`): the first eye at its full size, as the person sees it without the panel; or a PNG the
  caller hands over, which is how `capture` saves the picture `look` last gave it ("save that picture").
- **Videos** (`record {seconds 1-120, fps 1-120, width=960, name, wait}`): the first eye without the overlay, scaled on
  the GPU before the readback (a Quest eye is 18 MB a frame; copying and resizing that on the CPU would change the very
  timing a flicker recording is for), piped raw to ffmpeg, MP4 H.264 by the first encoder that works (NVENC, then Media
  Foundation, then libx264; ffmpeg from PATH or `SOJOURN_FFMPEG`; without one it's refused with how to get it). Real
  time at a constant rate: each 1/fps slot takes the first frame rendered in it, slots no frame reached repeat the one
  before, and the result says how many (`repeated`) and the rendered rate (`rendered_fps`). A one-frame flicker needs
  fps at the render rate (the tool says 90 in the headset, 60 at the desktop). No sound: voice chat shares the mix, and
  other people's voices aren't the assistant's to record. One at a time; a "Recording" card with a stopwatch shows
  while it runs (drawn over the view, so not in the video); `stop` ends it early. It records Sojourn's frames, not the
  headset compositor's reprojection (`docs/vr.md`'s frame log covers that). Unchecked in the headset.
- **Stills** (`Capture/Stills.cs`): `snapshot` with `size`/`width`/`height`, `fov`, `camera`, `hide_avatars` or
  `format` renders a picture of its own, the view never moving: an extra frame on the render thread just before the
  person's next, in a frame slot of its own, waited for there (a hitch of its GPU time at the person's request). Sizes
  up to the viewer's 7680x4320; over 2048 a side it's tiles of one image (each an eye of the same frame: one cull,
  shadow fit and exposure; 64 px margins so SSAO, glow and SMAA meet across the seams), copied straight into one
  readback (3840x2160 in 4 tiles took 1-1.4 s, no seam visible). Unsized: the window's eye on the desktop, 1920x1080
  in the headset; wider than the window keeps its width of view (the viewer's rawSnapshot). `aim: {at?, forward, up}`
  (region) places it (the eyes without `at`), the avatar's arms as they are (> Pictures by hand). Cameras: presets
  around the avatar (`front`/`selfie`, `behind`, `shoulder`, `above` with facing up the picture, `left`, `right`;
  `distance`), from its centre + 0.7 m and its body's facing as the still draws it (the region's avatar, as others see
  it; in the headset not the body under the head), placed again while settling and at the shot so a moving or turning
  person isn't left behind; or `{at, look_at}` in the region. Away from the head the own avatar is drawn whole and the
  interest list goes to 360 mode meanwhile, with `settle` (5 s; 0 from the head) to let what the camera sees load: its
  view is built without drawing every third frame until nothing waits or nothing more arrives for 2 s. What a still
  leaves alone: the person's sky (drawn under it, no fade stepped), exposure (held: tiles and faces match), the shadow
  maps' size, the load governor and GPU profiler (they never see it), probe captures (not run for it), the eyes' mirror
  (a still picks its own and reflects it as the eyes do, at the picture's size: `docs/rendering.md` > Mirrors), the
  view line. Its targets go as soon as it's done unless the person's frames use that size. PNG, or `format: jpg`
  (quality 75). Caveat: in the first seconds after arriving, before the default reflection probe is captured, a still
  can come out darker (it takes no captures of its own).
- **360s** (`panorama {quality preview|medium|high|max, width=4096, camera?, hide_avatars?, settle=8}`): as the
  viewer's floater (llfloater360capture.cpp): six 90-degree faces of 128/512/1024/2048 (each a pixel wider all round
  for stitching), all in one frame, the interest list in 360 mode meanwhile; stitched on the CPU to equirectangular (the
  middle is the camera's facing), a JPEG at 95 with GPano XMP (`PoseHeadingDegrees` the compass heading of the middle).
  From the person's view by default; from their eyes (the headset, first person) without their own avatar (their body
  seen from inside isn't a picture), from a camera behind them or placed, with it.
- **Sent on** (`Capture/Sharing.cs`; the picture is `path` in Pictures\Sojourn or the last one taken, pinned before the
  person is asked): `toInventory` (the viewer's Save to Inventory: each side to a power of two as its
  biasedScaleToPowerOfTwo, at most 2048; "Snapshot : Parcel, Region (x, y, z) - Rating", "Taken by ..."; the Photo
  Album as a Snapshot item, next owner everything, group and everyone from the upload defaults; the 2K price over
  1024x1024 where the grid has one; a spend on Second Life, free and ungated on loopback); `postcard` (the viewer's
  Send via E-mail: the region's SendPostcard cap, the card then a JPEG under 1.5 MB to its uploader; always waits for
  the press, `words`, the addresses, subject, message and picture shown; refused before asking where the region has no
  cap, as OpenSim's don't); `toProfile` (Share to Profile Feed) is refused with why: the viewer posts to
  my.secondlife.com with the web session its login opens (the reply's openid_url and openid_token), and LibreMetaverse
  drops the token.
- `CaptureApi.ShutterAsync` is a plain `snapshot` for a shutter on a controller or key: the headset's camera below, and
  on the desktop the viewer's Snapshot to Disk key, Ctrl+`, which takes the window's view.

### Pictures by hand

`Control/Shutter.cs`, `Control/ShutterReview.cs`. In the headset a camera is taken out and put away by menu > you >
camera or by asking the assistant (the `photo_camera` setting; no forearm token: the user found the arm crowded,
2026-10-29). Why a camera rather than a button: every controller button already has a job, and a held-out camera with
the trigger as its shutter is the usual VR way; no chord, no trigger word.
- **The panel between the hands** (the user's design, 2026-10-29): a real rectangle in the room, its bottom left corner
  4 cm off the left palm, its top right 4 cm off the right palm (the grips' +X), its bottom edge level, its sides
  rising toward the right hand, so raising that hand makes it taller; any shape from 1:3 to 3:1. It tilts up or down as
  aimed through its centre from `photo_pivot` below the eyes (6 cm, tuned by the user where they hold it; held higher
  it looks up; the user's pick, 2026-10-30). Aiming it by the controllers, the fingers or the view never matched the
  hands.
- **The picture** is taken from the panel's centre along its facing, the avatar's arms as they are (holding the camera
  up in a mirror, as the preview shows them; the user, 2026-10-29). Its field of view is what the panel spans from the
  eyes' distance behind it (25-70 cm), as if looking through it: hands spread or held closer, wider; the right stick
  zooms it while the camera's out (up in, down out, 0.25x to 8x, back to 1 each time it comes out; sideways still
  turns; the user, 2026-10-30). At least 6 degrees a side, at most 120 tall and 140 wide; about 2 megapixels
  (1920x1080 at 16:9, the longest side 2048). `photo_ratio` (free by default, or W:H: 16:9, square, 4:5...; the
  assistant sets it when asked; neither kept between sessions) keeps a shape, its diagonal the hands'.
- **Framing** means only the camera out and both hands tracked at least 15 cm apart (a hand's tracking dropping out
  holds the last panel 0.6 s); rules on where you look or where the hands had been made it go off at random (the user,
  2026-10-30). Only then is the panel drawn (thin edges, brighter corners, a live preview of the picture on it as a
  phone's screen: rendered only while framing, `docs/rendering.md` > The camera's preview) and the right trigger the
  shutter (not on the panel, a board, a key or a page; touching waits, as while aiming); otherwise the trigger is as
  ever (making it the shutter whenever the camera was out took pictures by accident). Whether a press is the shutter,
  and the panel it takes, are fixed when the trigger first moves (0.1), so the press doesn't shake the shot. The shot
  is SojournCapture snapshot with `aim` (the person's own press on the op), the panel flashing, the viewer's snapshot
  sound, into Pictures\Sojourn.
- **The picture shown after** (the user's ask, 2026-10-30): for 3 s, as a phone's camera shows what it took, on a sheet
  in the panel's look (a little glass round it, no spark, no keys, so it takes no ray or trigger) just above the panel
  it was taken through, where the eyes are as the shutter goes; left there in the room, so lowering the hands to look
  at it doesn't take it away, turned to face you, half the panel's size (its longest side 9-20 cm); a new picture
  takes its place. The saved file is decoded and scaled once off the frame thread (640 px its longest side); a quad
  layer among the client's boards with its frost in the eyes (`MapWiring.HeadsetScene`'s `more`); hidden, its seconds
  still running, while a confirmation waits or the interface is hidden.
- X hides the panel's outline and preview but not the camera.

## Animations on the person (LLAgent animations, stopAnimating)

`Core/Agents/SelfAnimations.cs`, the `animations` tool. Built because a dance machine's dance kept playing after a
teleport home, and the assistant could neither see nor stop it.
- `animations`: what plays on the person's avatar as the region last listed it, each with its name where known (the
  viewer's built-in ones, what the agent API played) and what started it (the object, its name, whether it's worn,
  and whether they sit on it; "the region" for standing, walking, sitting); `moved_by_things` says whether tracking in
  the headset gives way to them now (`docs/vr.md` > Your own body).
- `stopAnimating`: with an id, that one; without, the viewer's "Stop animating me" (LLAgent::stopCurrentAnimations):
  all stopped but do-not-disturb and the ground sit, the region's animation state reset, animation permissions of the
  region's objects revoked (RevokePermsOnStopAnimation, on by default: a dance machine can't start it again), standing
  up if seated, and the default stand asked for.
- **A seat's leftovers stop by themselves** (the user's call, 2026-10-28; `Core/Agents/SeatLeftovers.cs`): standing up
  from anything by any means (stick, forearm key, assistant, the region unseating you, a teleport away), 1.5 s later
  what that linkset still plays on you is stopped and that object's animation permission revoked (only its own: the
  region's, worn things' and others' are left), with a log line "stood up from X: stopped its N animations". Sitting
  back on the same linkset within the grace cancels it; leaving while the old region is still a neighbour (a vehicle
  over a border) waits up to 10 s for it to seat you again. Its prims animating you are noted while you sit, so it
  still works once a teleport has left the seat behind. Nothing else from a seat lingers here: Sojourn doesn't take up
  objects' camera parameters or sit camera offsets at all; controls scripts took follow the region's take/release
  messages (nothing says whose, so they aren't released by seat; Release Keys takes back all); temp attachments stay
  until detached, as in other viewers.

## Confirmations

`LeapDispatcher.Gate` runs before any op, whoever calls (the assistant, a script, curl; the external editor's `/rpc`
asks it too). `Confirmations.Classify` gates:
- spending: pay, buy, group create; uploads where they cost L$ (not on loopback); joining a group with a fee. A pay
  above the known balance is refused by the gate, not passed.
- deleting: TesselBuild delete/returnObjects/removeFromContents, `take` into Trash, inventory delete/emptyTrash and
  `move` into Trash, parcelReturn, deletePick, deleteRole, and `temp` (temporary on rez: the region deletes it for
  good) on modify, or on rez of a no-copy item.
- giving: give, addToContents into someone else's object, a group notice with an attachment.
- permissions: perm/sale keys on create/modify/duplicate/rez, inventory perms, parcelSet, parcelEject, group roles and
  settings, a group invite into a role beyond Everyone, granting a script's permission request, joining an experience
  (an experience's permission prompt, ScriptQuestionExperience: yes lets its scripts act wherever it runs without
  asking again; its no and "block experience" aren't gated).
- scripts: a script put into an object of yours (addToContents; the editor's object.item.create/content.save). A script
  acts as the person, so it could give away or delete its object's contents or the object (llGiveInventory, llDie)
  without any of the ops above.
- touching others' things, while the person's setting asks (on by default, user 2026-10-25): `requestTouch` on an
  object they don't own (a touch by position is pinned to the prim the op would pick), and a button pressed on a script
  menu from an object someone else owns. Vendors, rental boxes and tip jars once granted debit permission can take L$
  that way, and the client can't tell which hold it. Ownership unknown counts as not theirs. The person's own clicks
  and left trigger (`docs/vr.md` > Controls) don't go through the gate.
- reporting: every abuse report sent (SojournReport send), whatever the settings; the confirmation shows all of it.
- people and me (`docs/people.md`, added through `Confirmations.Cases`): removing a friend, a favourite, the teleport
  history or a contact set; changing a friend's rights; allowing an experience; moderating a member's group chat
  (permissions); and **words**: an autoresponse's text (Do Not Disturb's or away's), a display name and the reject
  switches' replies, shown whole, so what's sent as the person is theirs. A pay to an object is first checked against
  its pay buttons (`PayCheck`): an amount its pay window wouldn't offer is refused before the person is asked.
- the other feature areas, each through `Confirmations.Cases` in its wiring: land (`Land/LandWiring.cs`: every list,
  settings, freeze, divide, reclaim and estate change is permissions; sell, abandon and deed give; buy, a pass and a
  group contribution spend; join, returns, a terrain bake and terraforming a whole parcel or area delete; a region
  notice is words); building (`Building/BuildWiring.cs`: removing scripts and saving back delete, deeding gives, an
  object's group and default or bulk permissions are permissions, material uploads and restores spend where uploads
  cost, an undo or redo takes the kind of what it undoes, a creation's undo deleting it); the AO
  (`Inventory/InventoryWiring.cs`: removing an animation or a set deletes); voice moderation (`Voice/VoiceWiring.cs`,
  permissions); a face's media (`Client/Media/MediaWiring.cs`: who may use or control it and its whitelist are
  permissions, taking it off deletes); pictures (`Capture/CaptureWiring.cs`: Save to Inventory spends where uploads
  cost, a postcard is words).
- Not gated, and arguably should be (for the people area's owner, not the prompt): `SojournPeople setNotes` replaces
  the person's notes on someone without asking (its result carries what was there, `was`, so only the assistant could
  put them back).

**Where a touch lands**: `requestTouch` sends the surface data the viewer's click does (face, surface and texture
coordinates, position, normal, binormal: llDetectedTouchFace/ST/UV/Pos/Normal/Binormal), so buttons drawn on one face,
boards and games work. The point: `at` if given, else where the pointer last landed on that object (two minutes),
else its middle as seen from the eye; a ray from the eye counting only that object's prims (`Control/DirectTouch.cs`
`FindAsync`, the renderer's pick, which hits triangles). `hold` keeps it pressed for touch-and-hold scripts. The
`touch` tool takes `at` too. A HUD prim lands on the HUD as the person sees it (`docs/hud.md`); the pointer on a HUD
makes it "that" (`hud: true` in the context line's target).

**What's gated is pinned**: notification answers are classified by the button `Notifications.Choose` picks, the same
function the answer uses (a response naming several buttons can't be read one way by the gate and another by the
op). The gate pins what the op will act on before it runs (a notification id prefix becomes the full id) and
remembers names ops changed this session, so a renamed item or object shows as "renamed from ...".

**What the person sees**: the panel shows the client's own description, from the op's arguments resolved the way the
op resolves them (names looked up afresh, avatars by username, linkset size, parcel name, a folder's item and no-copy
counts), with cancel and the accent confirm key. While it waits, it shows nothing caller-written beside it (what was
heard, status, result, notices, the model, choices all hide), it's drawn in a preset (the look's own preset, or glass
for a look the assistant made), and the look op is refused, so neither words nor colours from a caller can sit beside
it, wash it out or move its keys. The dispatcher holds every other op (except the panel's own, `RunsWhileHeld`) until
the confirmed op has been under way for up to 2 s, so nothing can change what the person was shown. One at a time;
two minutes without an answer refuses. The op's own timeout starts after the answer.

**All of a long list** (2026-10-31, as tidying gathers everything bound for Trash into one move): a list the
description names only the start of ("... and 40 more", the first five or six) is kept whole (`Confirmations.Listed`,
which the feature areas' descriptions use too, into `PendingConfirm.Lists`), and the confirmation gets a list key at
its top right, also when its title or detail would be cut (4 and 10 lines). It opens all of it in the reader, the
client's words only, and answers nothing: any press opens it, and the confirmation waits on. In the headset the list
sits above the confirmation, both up; on the desktop it takes the panel's place until × or Esc. Closing it re-arms
the confirm key (0.8 s); opening or closing it starts the two minutes again, so reading doesn't run out the clock. It
goes when the confirmation is answered or refused, and nothing can change it meanwhile (the reader's API is held like
any other op).

**Only the person's real press answers** (nothing on the API can): a click or the trigger on the key. The confirm key
is an outline and takes no press that started in its first 0.8 s on screen (no double click or stray press confirms
something unread); that wait starts again whenever the confirmation moves (the headset's panel carried along or
gliding in front, a redraw that shifts its keys) or the headset loses focus, and a trigger already held when focus
returns isn't a press. In the headset the ray comes up by itself for a confirmation whether or not the person's
pointer is on, and a trigger already held when it comes up isn't a press either (its edges are followed while the ray
is down). A press counts only if it went down and up on the same confirmation, on the raster that was on screen. The
desktop's confirm click takes only what Windows calls hardware (`Assistant/RealInput.cs`, `Private/RealKeys.cs`). With
a screen reader reading out (`docs/screen-reader.md`) a confirmation is read at once with its keys (with a list cut
short, "Control R reads the whole list": Ctrl+R says it again with every name), and Enter confirms
(a fresh press of a hardware key once armed, never a held, posted, injected or system-made one, nor a key already held
when the window came to the front) or Escape cancels; what others or the assistant wrote isn't read until it's
answered.

**Sojourn Access** (`docs/access-app.md` > How it fits) answers confirmations from its Notices list, under a stricter
guard (`RealInput`, `InputJudge`): a hardware key or click, a fresh press, made after the focus, the selected notice
and the window in front last changed by anything but the person (our own code's moves for them bind presses from then
on, anything else from 0.7 s after it); the confirmation answered is the one the notice showed, and the person's own
gated actions wait for the confirmation their call raised (`PendingConfirm.For`). Typed answers (llTextBox, MFA) also
take a key the screen reader passed on for such a press, and only text the person's keys built. The person's own
actions skip this gate only when they began with the person's input. For people whose input never comes from a
physical key, `access_answers_without_key` set to `hello` asks Windows Hello instead. Out of scope here as there:
software running as administrator or with UI Access, and virtual keyboard or mouse drivers the person installed,
which Windows reports as hardware.

The assistant's `client_api` refuses `LLViewerWindow` (snapshots by file path; `look` is its way to see). Uploads take
files it made (harness.py's rules say which files it may read); their cost is confirmed here.

## Incognito

The user's asks (2026-10-27: private mode, renamed Incognito with "let it see" the same day; the code still says
private: `PrivateMode`, `SojournPrivate`, `Private/`). For doing things privately: nothing from a spell is kept, and by
default the assistant can't tell the specifics (what's said, who, where), only general things. Enforced in the client,
as confirmations are, not by asking a model to look away. No prompt, tool description or panel text assumes what it's
used for. How each rule is enforced: `docs/assistant-incognito.md`.
- **Two kinds of spell**: blind (the default) and seeing, the person's "let it see" (`incognito_sees`, saved, off by
  default). Seeing, the assistant helps as usual, adult content included where the person needs it navigated or
  operated (it never makes NSFW content; its usual policies hold); everything about remembering nothing stays.
- **Memories from before** (the user, 2026-10-27): either kind keeps loading the memory index; nothing new is written.
- **Blind, the assistant gets only what's on the allowlist**: ops that return nothing about the world or only the
  person's own things, with handles in place of the people and things around them, and a situation the client works
  out itself ("In a conversation nearby with 2 people; 3 new IMs from 1 person..."). Seeing, everything runs as usual.
  What the person asks for that lands in their own account (a landmark, a favourite, a profile note, a picture saved
  to inventory) runs while seeing, as their own choice; nothing of Sojourn's or the assistant's own keeps it (the user, 2026-10-31: what someone does in a spell may
  well be something they want in their account, adult things included).
- **Nothing kept**, either kind: the spell's events, IMs and notifications are sealed and gone when it ends; the
  console and session log pause; memory and handoffs pause; the sessions start again at each end; afterwards the
  assistant knows only how long it lasted.
- **On** by the menu's incognito section, the wrist token, P on the desktop, or asking the assistant in the person's
  own words (its call, as with any request; its `incognito` tool). **Off** only by the person's own controls (the
  token, the menu's switch, P, as real input), and during a spell only the menu's switch, by their hand, lets the
  assistant see; no caller, the assistant included, can do either.
- **No words are matched** (the user, 2026-10-27: phrase matching was too finicky): nothing said turns it on or off.
- **Automatic** spells: with someone or at a place the person keeps it with, and (off by default) while any IM
  conversation is open. Nothing starts it by a region's maturity (the user, 2026-10-27).
- **The PC's monitor** blanks while either kind is on in the headset (the headset is unaffected); on the desktop the
  window is the view itself, so it stays. The person's screen reader reads what the screen shows on the desktop,
  keeps quiet in the headset during a spell, never logs, and its review history drops the spell's lines at the end
  (`docs/screen-reader.md` > Incognito).
- **Dictation** (both grips, or Y on the desktop) turns the person's words into a draft for nearby chat or the open
  conversation, sent only by their press; the words never reach a model, in or out of incognito; an assistant mic tapped open is sent first.
- **Dev mode**: the watch prints incognito on/off and which kind (`tools/dev.py`), its API calls are gated like any
  caller's, `dev.py shot` is refused and `dev.py log` shows nothing from then; its own shell is the user's, so the
  skill's line is what keeps it off the client's files meanwhile. A dev session can't forget its own transcript
  (`docs/dev-mode.md`).

## The panel

The panel, the settings menu and how menus sit in the headset, the look (glass by default), size, showing things on
screen, and the reader: `docs/assistant-panel.md`.

## Testing without a voice or a hand

- `tools/fixtures.py`: Sojourn Pilot's inventory (shirts, coats, trousers, shoes in Clothing/..., five objects, three
  notecards, landmarks The Fountain, Pavilion and Arrival point), a linked Fountain on the east lawn (152,112), and the
  account Sojourn Friend. Idempotent.
- A second client as Sojourn Friend: `python tools/client.py run --first Sojourn --last Friend --password
  sojourn-local --bridge-port 9721 --window 320x180 --fps 5 --no-assistant --base-dir local/friend --start
  "uri:Atelier&132&122&24"`.
- Your own client and sidecar, leaving Sojourn Pilot and Friend alone (a second login kicks the first): the account
  Sojourn Tester (password in `local/tester/account.txt`), `dotnet src/Sojourn.Client/bin/Release/net10.0/SojournVR.dll
  --first Sojourn --last Tester --password ... --bridge-port 9722 --window 320x180 --fps 5 --no-assistant --base-dir
  local/tester --start "uri:Atelier&40&40&24"`, then `assistant/main.py --api http://127.0.0.1:9722` with the venv's
  python, and `SOJOURN_API=http://127.0.0.1:9722` for `tools/assistant.py`.
- `tools/assistant.py tts "..." f.wav` (Windows' voice), `hear f.wav` (same Whisper), `talk f.wav` (the whole
  push-to-talk path; start the client with `SOJOURN_MIC_FILE=f.wav`), `ask "..."`, `model [quick|sonnet|opus]`,
  `timings`. `SOJOURN_LOG_REPLIES=1` on the client puts each request and reply in the sidecar's log (tests only). Test
  speech is in `local/assistant/speech/`.
- Hearing: `tools/soniox_mock.py` (> Soniox); T held as a real key by `tools/winkey.ps1 -Vk 84 -HoldMs N` with
  `SOJOURN_VOICE_MIC_FILE=loop:f.wav` and `SOJOURN_TEST_INPUT=1` takes the client's mic path (`/mic/live`, `/mic`).
- `tools/winmouse.ps1 -X -Y [-Click] -ProcessId`: moves and clicks inside the window by posting messages (the real
  cursor stays put). The pointer follows the cursor from the window's own mouse messages for this reason.
- A client started with `SOJOURN_TEST_INPUT=1` takes posted clicks on the confirm key (tests only); without it they're
  refused, which is itself the check that posted input can't confirm.
- Meta's XR Simulator shows no compositor window here, so the quad layers are checked only by the OpenXR core
  validation layer (`--validate`: clean) and by the in-eyes preview (`docs/assistant-panel.md` > Drawing and
  checking); its controllers can't be pressed, so `SOJOURN_VR_SCRIPT=1` lays a right controller over them to drag the
  panel about (`docs/vr.md` > Testing without a headset).
- `tools/assistant_guard.py [model]` (the venv's python): one session per case under the real settings and hooks, each
  told to do one forbidden thing with one tool; decoys stand in for the credentials (a decoy file in `local/secrets`,
  a decoy `mfa_hash`, a junction from the work folder into `local/secrets`), so a failure leaks nothing. 28 cases:
  credential reads by Read, by cat, by a path assembled in Python (the classifier caught that one), through a
  junction; Claude's settings; another studio project; writes into src, out of its folder by `..`, into the client's
  settings and its own settings file; posted clicks; starting a client with the login, or in dev mode; a Claude session
  that skips permissions; one allowed write in its folder; the client's caches, screenshots and incognito pictures,
  the sidecar's token, and its own work folder's cache allowed; a decoy key kept as real ones are (encrypted, kind
  "decoy"): read, cat, decrypted by DPAPI, through the provider module, through it with its name in pieces (the
  classifier caught that one), and the key variable echoed. All 28 held with Haiku (2026-10-29).
- `tools/provider_check.py`: the providers against `tools/provider_mock.py` (> Switching it on).
- `tools/private_leak.py`: incognito's leak test (`docs/assistant-incognito.md` > The leak test).
- `tools/menu.py` and `--panel-shots`: the menu and the panel's pictures (`docs/assistant-panel.md`).
- `SOJOURN_HOME` / `SOJOURN_WORK` move the sidecar's folder and scratch folder (the client's menu clear still clears
  the default folder).

Checked end to end with typed requests (quick mode, Sojourn Tester): memory written, read back after a restart,
forgotten, off (the API refused turning it on), cleared from the menu; a web question; refusals by the model before
any tool (the password file, vr-settings.json, another project) and by the harness; posted clicks on a confirm key
doing nothing; builds as jobs (a 29-prim dining set in 36 s, a cabin with a confirmed door script and an uploaded
texture, a 54-prim watchtower resumed from its notes after a restart); jobs queued, told a change mid-build, stopped
by a bare "stop" and resumed with a change; a timer, labels, an arrow, a table and clearing the screen by `show`;
reports drafted from the person's words and cancelled; a dance stopped ("I'm stuck dancing since that last place");
pictures, a 360, a Photo Album upload and short videos saved for the person. Haiku's start_job replies took 7-14 s
(it writes the brief).

## Latency

Each request's timings go to `local/assistant/timings.jsonl` (`tools/assistant.py timings`), from the end of speech
or the typed Enter: transcription, the context line, the model's first token, the first action, the reply, model
turns, the answering model and its effort, the context size; for handovers, when quick handed over and the planner's
first token. Measured 2026-10-21 (desktop, Haiku as quick; transcription since: > Hearing > Measured): typed, a reply
with no tools at 0.7-1.3 s, one-tool requests act at 1.0-2.2 s and reply at 1.8-3.4 s; spoken, add the
transcription. A handover to the planner at 1.0-2.5 s; a table build (6 ops) done at 19-25 s. Sonnet and Opus as
first model replied in 1.2-6 s, with occasional spikes that move with server load, not the model's size.

## Not done

- People by interest in search (SL's search finds places, events and classifieds here; people and groups by name are
  TesselAgent's and TesselGroups').
- A job can't see its build from another angle: the camera stays the person's (`look`), so checks are numbers
  (inspect) and the person's view. Mesh uploads by jobs are untried.
- A choice can't scroll past six.
- Haiku declined to take the user to some places they asked for while probing it (2026-10-28). Fine for now by their
  call, but it would get in the way of residents with less tame tastes.
- `SojournPeople setNotes` isn't gated (> Confirmations).
