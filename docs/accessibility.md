# Without sight: an audio-first Sojourn

Written by Claude Opus 5.5, 2026-10-27. Agent-facing. The research behind Sojourn Access; the plan is
`docs/access.md`. Why: the user's decision "Usable without sight" (CLAUDE.md),
after Tessel met a blind resident who uses
Radegast with NVDA, and wished for a full viewer with Radegast's accessibility. Contact with her goes through Tessel,
and what she asks for goes in access.md > What a blind tester tells us.

**Decided (the user, 2026-10-27):** the accessible viewer is a separate app in the same codebase, Sojourn Access, not
a fork or a mode of the VR build (CLAUDE.md > Decisions). Shared pieces (screen-reader output, ops) go in shared code
so both apps get them.

**How sure this is.** Part 2 (what Sojourn had and lacked) was checked against the code on 2026-10-27, file named,
and where each gap went on 2026-10-31. Part 1 comes from two research agents' reports (2026-10-27): Radegast from its
source code, the rest from the sources linked. Each item is marked **[FH]** first-hand from a blind or low-vision
person, **[M]** measured in a study, **[O]** an organisation's or vendor's own report, **[S]** a proposal. Pages the
agents couldn't open (AppleVis and audiogames.net threads, SL forum threads behind Cloudflare, the old radegast.org
wiki) are worth reading in a browser.

## 1. What blind SL residents use and need

### Radegast (verified: its source at github.com/cinderblocks/radegast `4f5dd45`, 2026-10-26, and radegast.life)

Paths below are in that repo, read by a research agent on 2026-10-27.
- Two clients. **Legacy** (WinForms, .NET Framework 4.8, Windows, release 2.54 of 2026-07-14) is what blind residents
  use. **Veles** (an Avalonia 12 rewrite on .NET 10, no release yet) has much more UIA work in it. Founded by Latif
  Khalifa (2009-14); maintained by Cinder Roxley (Sjofn LLC), who says in the issues they can't test with a screen
  reader themselves. Its 2025-26 accessibility fixes come from a JAWS user's issues (#141, #158, #196).
- **Screen readers, Legacy: plain WinForms MSAA/UIA and nothing else.** That means accessible names on controls and the
  order Tab moves through them. A notification has `AccessibleRole.Alert`, and its first button takes focus, which is
  how dialogs get read out (`Radegast/GUI/Notifications/Notification.cs`). **There's no working way to announce
  anything.** The one attempt (`FriendsConsole.AnnounceForScreenReader`) calls `AccessibilityNotifyClients` by
  reflection with a string, which almost certainly throws and is swallowed. `nvdaControllerClient` and
  `UniversalSpeech` DLLs ship but nothing calls them. So new chat and IMs reach NVDA only when the person Tabs to the
  history and reads it, which is the way the help guide teaches.
- **Veles:** about 1,400 `AutomationProperties.Name` uses and live regions: polite on the chat log, the nearby list and
  IMs, assertive on script-dialog titles; radar entries say "speaking / typing / away / sitting". Whether Avalonia
  really raises the live-region events is unverified.
- **The Speech plugin** (`plugins/Radegast.Plugin.Speech`, "RadSpeech", Mojito Sorbet 2009-10, Latif's additions in
  2014; Legacy only). The best ideas in Radegast, and its own voice, separate from NVDA (so two voices):
  - SAPI through FMOD, rendered to WAV. Each avatar gets an installed voice matched to their apparent gender, with
    random rate and pitch; voices can be assigned per person.
  - Chat is spoken from the speaker's position (3D, off by default), objects in an object voice that can be muted.
    Each IM session is put in one of four places on a 3 m circle around the listener.
  - Notifications interrupt with an earcon and a phrase ("A script asks you,", "Teleport offer."), then the text and
    "Choose one of these: ...". Interruptions stack and resume.
  - Picking someone in the people list speaks "Name, N meters to your right / in front / behind you". Picking an object
    speaks its name, distance, direction and "scripted, sittable, touchable".
  - Notecards read in chunks (Space/Down next, Up back, Right repeat). Substitutions (lol, brb, URLs as "a URL").
    Ctrl+Shift+R cycles the speech rate; Escape flushes the queue.
  - Eight earcon WAVs. **No audio radar, no beacons, no footsteps.**
  - Its speech recognition used Windows Speech Recognition (deprecated in Windows 11). Anything not a command was sent
    as chat, and dialog labels with capitals can't be picked by voice (`BlueMenu.cs`).
- **Keys** (`MainForm.cs`): Alt+1-9 for tabs (chat, friends, groups, inventory, search, map, objects, media, voice),
  Ctrl+Tab between chat and IM tabs, Enter says / Shift+Enter whispers / Ctrl+Enter shouts. Walking by arrows only when
  the 3D window has focus. **Text commands are how blind users move:** `//go` (distance and direction, xyz, an object
  or a person), `//face`, `//find` ("X is 12 meters away to the NE, 3 meters above our level"), `//follow`, `//sit`,
  `//tp` to landmarks, `//status`.
- **Lists:** nearby people as "Name (12m)" by distance, with Profile, IM, Pay, Follow, Walk To, Face, Teleport To,
  Mute and others. **No arrival or departure announcements.** Objects as "Name (N m) owned by X", filtered by words,
  radius and rezzed/attached, with Touch, Sit, Walk to, Turn to, Pay, Buy, Contents; hover text only in the details
  pane, never read out.
- **Dialogs and the rest:** script menu buttons and llTextBox are named controls. Permission requests can be set to
  ask, accept or decline. Avatar name links were turned off because they troubled blind users, and SLurls are shown as
  plain text since JAWS read raw `secondlife:///app/agent` strings (#133). The guide's own list of gaps: unlabelled
  volume sliders, some unlabelled buttons.
- **Who uses it** (first-hand accounts, not verified): Virtual Ability told Latif blind people were using Radegast (it
  wasn't made for them); an article's "almost 1,000" is Virtual Ability's membership across all disabilities, not blind
  residents (applevis.com/forum/text-based-viewer-opens-virtual-worlds-blind). Volunteers at Virtual
  Ability wrote its guide (radegast.life/documentation/help). A blind Virtual Ability trainer (2021): blind users shop, dance, fish
  and go to events, but can't build or drive (blog.virtualability.org/2021/06/the-most-common-questions-asked-by.html).
- What it can't do: render, so no pictures, media or HUDs, and nothing describes what a place looks like. Not found:
  braille support, hover text read aloud, ambient or footstep cues, whether the speech plugin is still used.

### Blind residents in their own words

- **A blind Virtual Ability trainer** (Virtual Ability's blog, 2021). [FH] Blind users shop, manage their wardrobes,
  go to events, dance, fish and volunteer, but can't drive or build; teleporting is how they get around; bumping into
  people goes on for years; learning is by trial and error; some stores and clubs accommodate blind visitors. Radegast,
  kept going by a lone developer, is their only way in.
- **A blind newcomer** (2019). [FH] Radegast is "brilliant" but steep to learn; the hardest things are which way he
  faces, how far people are, not bumping into them, and setting a profile picture.
- **A resident who lost her sight** (a 2020 account). [FH] In 2013 the official viewer gave her screen reader nothing:
  no menus, notifications, IMs or local chat, and no feedback on whether keys did anything; someone sighted drove her
  avatar. She wanted descriptions of her surroundings and her old photos.
- **A blind resident in 2010.** [FH] Radegast plus the "Sight Unseen" group's navigation scripts in a cane, bracelet or
  pet (`/2 where`, `/3 go name=...`).
- Screen readers named first-hand: JAWS, NVDA, a braille display.
- **Donna Davis** (researcher, Voices of VR #934, 2017): most blind residents use Radegast; text gestures don't
  translate; SL getting more visual makes it harder; a blind member designed a ballroom navigable by audio cues.

### Virtual Ability, Max, Linden Lab, Firestorm

- **Virtual Ability Inc.** [O]: blind people are asked to contact them first; VAI registers new users itself because
  SL's sign-up site isn't screen-reader accessible (2022); Radegast logins land on its orientation island, with volunteer
  mentors (virtualability.org/essential-second-life-skills). Venue practice from Alice Krueger's 2022 talk: **numbered
  seats** so a greeter can say "seat 12" and the visitor finds it in the objects list; descriptive object names (the
  default is "Object"); poster text as notecards with image descriptions; a teleport-reachable entrance; "no single
  accessibility guide" (equalentry.com/accessibility-virtual-reality-second-life).
- **Max, the Virtual Guide Dog** (Virtual Helping Hands, 2009 beta) [O]: started from a legally blind resident wearing
  a dog as a "please help" sign; read chat to the screen reader, a radar of people and objects with direction and
  distance, following, moving and teleporting; "Marco/Polo": objects with a Polo script describe themselves on
  request. No user study found (engadget.com/2009-04-03-virtually-leading-the-blind.html).
- **Linden Lab** [O]: no screen-reader support. The wiki's Disability page (edited 2025-02) points to OS screen readers
  and third-party clients; the viewer has font and UI sizes, rebindable keys, sound alerts, and the page asks builders
  for descriptive names, linked stairs, no forced landing points, scripts allowed for assistive devices
  (wiki.secondlife.com/wiki/Disability). At a 2010 UX meeting LL said Viewer 2 shipped before any accessibility work and
  screen readers "don't handle custom controls well"; a "keyboard pass" was promised; Latif asked for OS accessibility
  hooks so blind users could use their own screen readers. No sign it was followed through
  (wiki.secondlife.com/wiki/User_Experience_Interest_Group/Transcripts/2010-03-18). No jira ticket was found.
- **Firestorm** [O]: a dyslexia font, font sizes, a text-only mode; no screen-reader feature. Chat-to-speech add-ons
  exist outside it.

### Research on SL and virtual worlds

- **TextSL** (Folmer, Yuan, Carr, Sapre, ASSETS 2009; Oktay & Folmer, W4A 2010): typed commands (`move north 20`,
  `describe objects`, `where is the chair?`), A* paths with collision avoidance, a summariser of what's within 20 m
  ranked by distance. [M] Four screen-reader users matched four sighted ones on success; slower at exploring and
  interacting (a tutorial 1137 s against 528 s), as fast at communicating. [M] Across 433 regions **31% of objects are
  named "Object"** (up to 85% in some), ~40% without a descriptive name.
- **Perspective viewer** (Kruger & van Zijl, Stellenbosch, ICEAPVI 2015; access.uoa.gr/ICEAPVI-2015/proceedingsFiles/107-112.pdf):
  "/" commands plus keys to repeat output by line, word or character; an audio compass; a virtual sonar (3D beeps and
  names); footsteps inferred from movement; a directional noise for open ways (from Shades of Doom); positional speech
  of names, staggered by distance; occluded and duplicate names filtered out. Users said volume control on continuous
  cues is essential. It proposes roles and states for objects (a door: closed, approach from the left).
- **IBM** (Carter & Corona, AccessWorld 2008, afb.org/aw/9/2/14262) [S]: "Where am I?", what's within 3 m, an object's
  operations, move to it, filters hiding decoration, following a guide, trusted annotations; immersion alone isn't
  access. Gareth White (Sussex, 2007) [S]: cues should be rendered by the client, since in-world scripted sounds are
  heard by everyone.

### Social VR and AI description (2022-2026)

- **A sighted guide in social VR** (Collins, Jung et al., ASSETS 2023, 16 BLV; arxiv.org/pdf/2410.21659) [M/FH]: most
  wanted warnings before hazards and otherwise a guide "in my back pocket"; social help (describe people, find a named
  person, lead them right next to a conversation, join groups naturally rather than bumping in); context beyond alt
  text; sound beacons; ongoing help with controls.
- **An LLM guide** (Collins et al., CHI 2026, 16 BLV; arxiv.org/html/2603.09964) [M]: 63% of 476 queries answered
  correctly, ~6 s for guidance and ~11 s for visual questions; requests 60% navigation, 31% description, an overview
  first; problems were inconsistent directions and no memory of places; they asked for a private mode and a one-button
  return to a safe place. 12 of 16 personified it in company.
- **VRSight** (Killough … Zhao, UIST 2025, Rec Room, 9 blind; arxiv.org/html/2508.02958v1) [M]: an overview rated most
  helpful, a left-to-right sweep next, aim assist least; one participant trusted a chair the model made up.
- **VRBubble** (Ji, Cochran, Zhao, ASSETS 2022, 12 blind; arxiv.org/pdf/2208.11071) [M]: earcons or speech for avatars
  in proxemic bubbles halved errors counting them, but distracted in crowds (about 5 avatars a practical limit).
- **Nonverbal cues** (Jung, Collins et al., 2024; arxiv.org/abs/2410.21652) [M]: audio and haptic cues for eye contact,
  nods and shakes improved detecting attention.
- **EnVisionVR** (Cambridge preprint 2025, 12 BLV; arxiv.org/html/2502.03564) [M]: "where am I / what's near me / where
  is X" (tones, beeping) raised finding things from 50% to 92%; they wanted speech rate and verbosity settings and
  consistent names.
- **RAVEN** (CHI 2026, 8 BLV; arxiv.org/pdf/2510.06573) [M]: keyboard shortcuts dropped (hard to remember); raw xyz
  incomprehensible, so egocentric directions; 7 of 8 raised trust: wrong information can't be noticed.
- **AI description in daily life** (Gonzalez … Azenkot, CHI 2024, 16 BLV; arxiv.org/html/2403.15604v2) [M]: trust
  2.4/4; leaving people out misled (a chapel thought empty); quick private answers wanted, human checking for high
  stakes. Preferences for detail vary widely between people (An et al., 2025).
- Games and guidelines [O]: audio games' compass snaps, wall-or-opening radar, beacons toward points of interest
  (afb.org/aw/summer2026/gaming-for-navigation); Owlchemy's Cosmonious High found automatic narration overlapped, so
  descriptions went to a button, short, cancellable, ducking other audio
  (unity.com/blog/games/developing-for-blindness-low-vision-in-vr-with-cosmonious-high); Microsoft Soundscape's beacons
  and "around me"; W3C XAUR (w3.org/TR/xaur: descriptions to assistive tech, filtering and querying objects, braille
  or a second screen, a safe-harbour place); the Game Accessibility Guidelines' vision items (screen-reader menus,
  sonar, compass, a sound per kind of object, separate volumes).

Not found: how many blind SL users there are (proxies: the support group's 100+, 2019; Virtual Ability's ~1,000
members of every disability, 2015); first-hand accounts from blind VRChat or Rec Room users; a user study of Max.

### What recurs

1. **Their own screen reader**, and speech under their control: triggered rather than automatic, brief,
   interruptible, rate and verbosity theirs, other audio ducked, egocentric directions, braille too. (Radegast's
   plugin speaks in a second voice beside NVDA, and its announcements to NVDA don't work: the opening.)
2. **Knowing what's here**: an overview first, then details, ranked and filtered (nearest, de-duplicated, not behind
   walls). Names can't be relied on (a third are "Object"), so description has to make up for them, signs and posters
   included.
3. **Getting there without stumbling**: go to or follow a person or thing, which way they face, warnings before a
   collision, confirmation that a move happened, a safe place one press away. The most cited daily difficulty.
4. **Social awareness**: who's near and coming (a handful at a time), looks on request, joining a conversation
   gracefully, gestures and nonverbal cues as speech or sound.
5. **Trustworthy AI**: 63-75% accuracy in the studies, and the person can't check; say what's uncertain, ask, never
   claim what didn't happen, remember places.
6. **Getting in and learning**: SL's sign-up isn't accessible; controls must be taught; mentors matter. Building and
   driving stay out of reach today.

Points 2, 4 and 5 are where Sojourn can go past Radegast: it renders, its assistant can look. For point 3's safe place
it already has a way out (the held menu button, Ctrl+Shift+H: stand and go home).

## 2. What Sojourn has, and what's missing (checked against the code, 2026-10-27)

### Already there

- **The assistant by voice and text** (`assistant/`): hold T and speak, or Enter and type; it acts through the agent API
  (go, sit, stand, touch, wear, take off, find items, say, message, recent chat, dialogs and answer_dialog, search,
  pay/give behind confirmations, the reader for notecards). For a blind person this is already most of Radegast's
  action surface, by asking rather than by finding a button.
- **Spoken replies** (`assistant/voice.py`): Kokoro locally, Windows' voice as fallback; a setting, off by default.
- **Looking** (`look` in `assistant/tools.py`): a picture of the view for the model, so "what's here?", "what does she
  look like?", "what does this sign say?" can be answered. Text-only clients have nothing like it.
- **Lists**: `nearby` (people and objects nearest first, names, floating text, for-sale), `inspect` (owner, creator,
  profile), object flags touch/pay/scripted known in `Core/Agents/ObjectViews.cs`.
- **Spatial sound** (`docs/audio.md`): object sounds, footsteps, typing, UI sounds and the parcel's music placed by a
  spherical-head model; voice chat placed too (`docs/voice.md`). The world already sounds where things are.
- **Moving by keys on the desktop** (`Control/Pilot.cs`): arrows/WASD walk and turn, F flies, Space jumps; autopilot
  (`LLAgent startAutoPilot`, `Core/Agents/Motion.cs`) walks to a point; teleports by name, landmark or region.
- **Notices with their keys** (`Social/SocialFeed.cs`, `docs/social.md`): script menus, offers, invitations as
  answerable items; the event log they come from is the one the assistant reads.
- **Hearing tuned to SL names** (`docs/assistant.md` > Hearing): the names around the person feed the name pass after
  Parakeet (now the default) or Whisper.

### Gaps found then, and where they went

Written about the VR client on 2026-10-27; Sojourn Access now covers most of them for a blind person.
- **Screen-reader output**: none then. A1 reads what arrives through the person's own reader
  (`docs/screen-reader.md`), and confirmations and notices are answered by keys (`Access/ReaderKeys.cs`). The VR
  client's panel, feed and menus are still pixels with no UI Automation tree: browsing them is Access's job.
- **The console beside the window** prints nearby chat and IMs and takes typed chat (`ConsoleChat.cs`): probably
  readable by NVDA as a stopgap; unchecked.
- **Spoken replies** (`assistant/voice.py`): a new reply cuts off the one being spoken, at a fixed speed (1.05, no
  rate setting), where screen-reader users listen at 2-3x. Still so; Access reads replies through the person's reader.
- **The typed request line is drawn**, not an edit control: no echo, no review. Still so in the VR client; Access's is
  a real edit control.
- **`nearby` gives distance only, no bearing** (`assistant/tools.py`), where "the bar is 4 m at your 2 o'clock" is
  the form people need. Still so for the assistant's tool; Access's lists give bearings.
- **No follow, no routes**: autopilot walks a straight line and gives up when stuck, and nothing plans a path on the
  collision model. Access has paths, follow and walk mode (access.md > M3).
- **No earcons, beacons or sonar**, no arrival or departure announcements: Access's M4 and M1.
- **The 3D view must render** for picks, `look` and the collision model, with no low-cost mode for someone who
  doesn't need the picture: Access's renderer out of sight (M0b), unseen when nothing looks, and a collision model
  that needs no GPU.
- **Incognito is "blind" by default** (the assistant sees no specifics), which for a blind person removes their eyes;
  "let it see" exists. The default, and the word "blind" in an accessibility setting, are still open questions.
- **The assistant needed a Claude account.** Now it takes an Anthropic key, OpenRouter or the studio relay too
  (`docs/assistant.md`), and Access works fully without any.
- **HUDs and media**: the assistant can picture and press them (`docs/hud.md`, `docs/media.md`), which Radegast
  can't: a strength to keep.

## 3. The plan

Superseded by `docs/access.md` (2026-10-29): Sojourn Access's own plan, milestones M0-M6, built on this research
plus `docs/radegast.md` (all of Radegast). A1 is built (`docs/screen-reader.md`); the old batches A2-A8 are folded into access.md's milestones
(A2 the window, M1; A3 bearings and lists, M1; A4 routes and follow, M3; A5 sound, M4; A6 describing, M5; A7 no
account, access.md's stance; A8 the headset, later). A blind tester's requests moved there too.
