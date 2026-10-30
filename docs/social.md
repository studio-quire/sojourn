# Other people on screen: names and chat

Written by Claude Opus 5.5, 2026-10-25. Agent-facing. Names over people, and other people's chat, IMs, group chat and
notices, in the headset and on the desktop (`src/Sojourn.Client/Social/`: the views; the feed's data, the social settings
and their wiring are `src/Sojourn.Session/Social/`); and objects' floating text, drawn the same way.

## The rule

Information only (user, 2026-10-25, after the second main-grid session): visible UI is fine, but the person operates
nothing except through the assistant (and the settings menu, confirmations, and since later that day their own touch
and sit: `docs/vr.md` > Controls). So nothing here answers anything: replying,
accepting a teleport or an item, answering a script menu all go through the assistant (`message`, `say`, `dialogs`,
`answer_dialog`), which can also read chat back (`recent_chat`). The words follow the panel's rule: no instructions,
no labels saying what things are. What the feed does take (user, 2026-10-26: notifications and pop-up menus need a way
to be opened and read in full) is the person's hand to read it: see The feed below. Since 2026-10-27 (the session's
script menu that never showed, and notices piling up unseen) a waiting notice also takes the person's own answer, as
the viewer's toast does: its keys, or putting it away (Notices below). The assistant still answers through
`answer_dialog`, under the confirmation gate.

## What shows

- **Name tags** (`NameTags.cs`): a small capsule in the panel's look over each person's head (where the renderer drew
  the head this frame: `NameTag.Head`, so tags ride the blended motion), upright, turned to the eye. The display name;
  under it the username (`first.last`, SL's form) when they've set a display name of their own, and only within 10 m (gone past
  11.5; distance alone, since looking made it feel inconsistent: the user, 2026-10-29). Shown for everyone within chat range (20 m: who hears you and whom you hear), fading out by 24 m; beyond
  that only whoever you look at (5 degrees of the middle of the view, or the pointer on them), is speaking, typing, or
  spoke in the last 20 s. At most 16; of two that overlap, the farther fades to a third. Sizes are angular, shrinking
  slowly with distance (headset: 0.08 deg per logical px at 3 m, a name ~1.2 degrees, by distance^-0.7, so a far tag grows only a little in the world (the user, 2026-10-28); desktop: about the panel's text,
  ^-0.1), fixed in metres nearer than 1.5 m. Not you. Hidden behind walls and things in front, as the viewer's
  (`LLHUDNameTag::render` depth-tests): drawn with the eye's depth test (`OverlayTag.DepthTested`); `tags_through_walls`
  shows them through.
  - Speaking in voice: a light in the look's accent just left of the name, bigger when louder. It replaces voice's dot
    over a tagged speaker (`VoiceWiring.Tagged`); untagged speakers keep the dot.
  - Typing in nearby chat: three dots inside the capsule, breathing.
  - (Batch B4, 2026-10-28, as `LLVOAvatar::idleUpdateNameTagText`.) Their group title small and dim over the name (the
    "Title" name-value; `tag_group_titles`, on), only within 10 m, as the username: the viewer shows it always,
    but in a crowd it doubled the text. Marks after the name, as glyphs rather than the viewer's "(Away)", "(Do Not
    Disturb)", "(Editing Appearance)": a crescent moon (the AWAY animation), a soft red no-entry sign (BUSY), a hanger
    (CUSTOMIZE). The name the person calls them (B2's pseudonyms) in place of their display name, the username under
    it near. A ring round the capsule in their contact set's colour (Firestorm colours by set; `PeopleOps.SetColor`),
    or with `tag_friend_colour` (off, as the viewer's NameTagShowFriends) friends' in a pale yellow. The ring is our
    choice for the glass: coloured ink or a tinted body lost contrast on light glass; how it looks is the user's call.
  - Chat bubbles (`chat_bubbles`, off by default as the viewer's UseChatBubbles): over a tag, what they said nearby in
    the last 20 s (a glass bubble, 230 px wide, at most 12 lines, newest at the bottom, whispers dim), fading over its
    last 4 s. They ride the tags, so they need names on.
- **The feed** (`SocialFeed.cs`, `FeedPainter.cs`): one glass sheet, newest at the bottom, each entry for a while
  (nearby 24-60 s by length, IMs 60-120 s, notices 10-120 s), up to 380 logical px tall (newest that fit).
  - Nearby: the name in semibold and the words after it. Emotes read "Kestrel waves". Whispers dim, shouts semibold,
    objects dim, your own lines (the assistant's `say` too) with your name dim. People's links in a line read as their
    names, a mention (the official viewer's 2025 chat mentions: an agent link ending `/mention`) as "@name"; names are
    the person's pseudonyms where they gave one.
  - Alerts (B4): a line from someone else that mentions the person (`mention_alerts`, on) or holds one of their
    keywords (Firestorm's keyword alerts: `SojournSocial keywords`, per account in the state dir's keywords.json;
    nearby, IMs and group chat each a switch, whole words, case; `keyword_alerts`) is washed in the accent (in a
    conversation, a bar beside the line), stays at least 90 s, and plays the viewer's UISndAlert (`alert_sound`). The
    screen reader reads it as any line.
  - SLurls (B4) are links, as the viewer shows them: "Region (x, y, z)" in the accent, underlined (heavier under the
    pointer), `Social/Links.cs`. A press on one offers its place (`Places.Offer`: a notice with a Teleport key; the
    teleport is the person's press on that). In the reader too (a notecard's links). Opening a block or a notecard
    offers nothing by itself.
  - Translations (B4, the viewer's TranslateChat; `translate_chat` off, `translate_to` the system's language,
    `translator` none): other people's lines (not objects') translated, drawn dim under the line (`Translation.cs`:
    one at a time, newest first, cached; never a remote service while incognito). No service is wired: which one may
    see what people say is the user's call (the board). `SOJOURN_TEST_TRANSLATOR=1` adds a "test" translator that
    turns words around, for checking the plumbing; a local one (NLLB through CTranslate2, which the assistant's venv
    has for Whisper) or a cloud one registers through `Translation.Register`. Lines already read out aren't read again
    translated.
  - IMs and group chat: a pane per conversation, headed by an envelope and who it's with, or two heads and the group;
    in a group each line has its speaker; your lines sit at the right, dim. Someone typing an IM: three dots under
    their conversation (a pane of its own if there isn't one).
  - Notices, a pane with a bell: teleport offers and requests, friendship, items given, script menus, group notices and
    invitations, web page offers, friends coming online, region restarts and other alerts. One line in our words
    (`SocialFeed.NoticeLine`). One still waiting for an answer has keys (below).
  - IMs and group chat are told apart by the session: SL's group chat arrives as an ordinary IM (dialog 0) whose session
    is the group's id, which the viewer recognises as one of your groups; so do we (`AgentSession.SessionFor`), and a
    session kept as p2p from before the group list came is retyped (TesselIM sessions, recent_chat's "<name> in <group>").
  - Hidden while a confirmation waits: nothing other people wrote sits beside it.
  - Read in full (the headset's pointer, or the mouse): a block (a line, a conversation's pane, a notice or script
    menu) is cut at six lines, ending in "..."; the one under the pointer is lit, a press opens it whole (up to 40
    lines, the sheet to 640 px) and keeps it up at least two minutes, a second press closes it. While the pointer is on
    the sheet nothing on it runs out, and a close key at its top right puts everything on it away (new words bring it
    back). `FeedPainter.Use`, `SocialView.Point`/`PressDown`/`PressUp`.
  - What objects say (greeters, DJ boards' "now playing", object IMs) leaves after 18-40 s, sooner than people's words:
    in a busy place it kept the feed up without end (one of the guesses at the user's "notification on the left that
    stayed open", 2026-10-26). A feed picture that fails to draw takes the sheet down rather than leaving the old one up.
  - The pointer counts as on the sheet (nothing runs out, nothing new joins, so keys stay put under it) only while it
    arrived or moved there in the last 12 s: a hand at rest whose ray lies across the feed kept everything on it up
    without end (the other guess at the notice that stayed open, 2026-10-27). For 4 s after a press on a notice's key,
    new entries join anyway (a script's next page comes up under the pointer at once).
  - Scrolling back (Claude Opus 5.5, 2026-10-27, from the user's idea of the right stick scrolling what it points at):
    when the feed holds more than it shows (older blocks still in their time but left off for room), the right stick
    with the ray on it (`docs/vr.md` > Controls > Scrolling with the stick), or the wheel on the desktop, scrolls it
    smoothly. At rest it's as before (the newest whole blocks that fit); scrolled, the sheet is as tall as it
    gets (380, 520 with notices waiting, 640 with a block open), a window onto every block cut at its edges, a thin
    bar at the right saying where, and only keys wholly in the window take a press (`FeedPainter.Use.Scroll`,
    `Drawn.MaxScroll`). Scrolling counts as using it (nothing runs out or joins meanwhile); off the sheet for 3 s it
    goes back to the newest. It goes back only over what's still on the feed: older chat is the assistant's
    (`recent_chat`).
- **Notices** (`SocialFeed` notices, `FeedPainter.NoticeKeys`, `Social/NoticeBell.cs`; Claude Opus 5.5, 2026-10-27,
  after the Tidewater Harp's menu never showed as one and old notices piled up): a notice whose notification still
  waits (`Notifications`) carries its keys under its words and a × at its top right.
  - Script menus: the buttons three to a row as the viewer lays them out, the first three along the bottom (so a
    script's "More >" or "< Back  OK  Next >" row sits where it expects); a label too long for its key takes a second
    line. Paging is the script's: More > is an answer, and the script's next menu replaces this one in place (same
    object, `dialog:<id>`). The text box isn't a key (the assistant types for them). Offers, invitations, questions,
    page offers: their form's buttons (not the viewer's Info). A notecard given has Read first: it keeps the item, finds
    it in the inventory and opens it in the reader (`docs/assistant.md` > The reader). An alert with only OK has only ×.
  - The person's press answers it (`Notifications.RespondAsync`, as their press on the viewer's toast; the
    confirmation gate guards the assistant's answers, not theirs). Keys take no press in the notice's first 0.6 s on
    the feed, and on the desktop only a hand on the mouse (`RealInput`, as a confirmation). × puts it away unanswered
    (`Notifications.Dismiss`, logged as NotifyDismissed: a script menu's Ignore, an offer left unanswered as a closed
    toast leaves it). With the pointer on the sheet, a clear key beside the close key puts every waiting notice away.
  - Answered anywhere (here, by the assistant, replaced, expired) it leaves the feed at once. It shows its two minutes;
    after that, while any wait, a bell sits on the left forearm past the HUD token and the stand key (headset), lit in
    the accent while some of them aren't on the feed; the right ray and trigger on it (the pointer on or not) put them
    all back on the feed for two minutes. The assistant can do the same (`SojournFeed notices {show: true}`).
  - Alerts with nothing to answer (a teleport that failed) leave the pending list after 3 minutes; anything after its
    hour. With the chat switch off, waiting notices still show (alone on the sheet).
- Source: the session's event log (`LLChat`, `TesselEvents` im/object_im, `ClaudeNotify`), the stream the assistant
  reads, so mutes and the viewer's filtering (script errors, blocked people) apply. Typing comes from the protocol
  (not logged).
- **Floating text** (`FloatingText.cs`; llSetText: prices, signs, titlers, game status), as the viewer's LLHUDText: the
  script's words in its colour and opacity, centred, growing up from 0.6 of the object's height above its middle, the
  last 10 lines, a thin dark edge for a bright sky (the viewer's drop shadow). Fades from 8 to 12 m (measured from the
  object's near side, as the viewer does); what the pointer is on or you look straight at reads to 64 m. Hidden behind
  things (the viewer depth-tests it; here a ray to it, four a frame, each again after half a second). At most 20,
  nearest first, the farther of two that overlap faded to a third. None on HUDs or from blocked people or objects, and
  in the headset not your own attachments'. Sized as tags are; their pictures share the tags' atlas.
- **Hover tips** (B4, `HoverTips.cs`; the viewer's tooltips after ToolTipDelay): the pointer (the mouse, or the right
  ray while its pointer is on) resting 0.7 s (`hover_tip_delay`) on a thing shows a small capsule over the point: its
  name, and its price if it's for sale. Nothing else (the viewer's tip also has owner lines and an inspect key), nothing
  for things named only "Object", nor for people while their tags show, nor for the ground (the viewer's land tips are
  off by default). On by default (`hover_tips`): it only answers a pointer the person rests on purpose, and says what
  the thing is, which the little-text rule allows; turned off, the assistant still says what "that" is when asked.
- **Radar reports** (B4, `Radar.cs`; Firestorm's, all off by default as there): someone coming into or leaving chat
  range (20 m), the draw distance or the region (`radar_chat_range`, `radar_draw_distance`, `radar_region`: off,
  enter, leave or both), and the age alert (`radar_age_alert`, days; someone that new within draw distance, from their
  profile's born date), each a notice on the feed ("RadarReport") the screen reader reads. Measured avatar to avatar,
  once a second; who's there on arrival (login, teleport, crossing) is taken as it is for 8 s, not reported.
  `SojournSocial radar` lists who it sees.
- **Read out** (`docs/screen-reader.md`): with a screen reader running, every line and notice that joins the feed is
  read through it, in order, who first, and waiting notices are answered by keys (Ctrl+N, then their number keys).
  The chat switch is what's drawn; what's read has switches of its own.
- **Do Not Disturb** (`docs/people.md`): IMs and group chat stay off the feed (kept in their conversations) and people's
  offers go to the bell; when it ends the bell's notices come back. **Places offered** (a script's llMapDestination, a
  SLurl's link pressed on the feed or in the reader) are notices with a Teleport key.
- Settings: the view section's **names**, **chat** and **hover text** switches, `names` / `chat` / `hover_text` on
  `SojournSettings` (the assistant's `settings` tool). B4's are in section social (social-settings.json beside
  vr-settings.json, `SocialSettings.cs`): `chat_bubbles`, `tag_group_titles`, `tag_friend_colour`,
  `tags_through_walls`, `hover_tips`, `hover_tip_delay`, `mention_alerts`, `keyword_alerts`, `alert_sound`, the radar's
  four, `translate_chat`, `translate_to`, `translator`. In the menu (batch M, 2026-10-29) the social section has
  names, chat and hover text (moved from view) and all of B4's but translation (`docs/assistant.md` > The panel). `SojournSocial
  status` reads them with the keywords and counts.

## Where

- Desktop: tags in the world; the feed at the bottom left (where the viewer's chat toasts come up), at the panel's
  scale.
- Headset: tags drawn into the eyes (the overlay's labels atlas). The feed is its own quad layer (`Xr/QuadLayer.cs`,
  as the panel's), with its frost in the eyes: low on the left, its right edge 3 degrees left of the panel's widest sheet (at any size: `UiSize.BesideYaw`) where
  the panel rests, its bottom 30 degrees below the eyes, growing upward; 1.1 m away at the panel's angular size (300
  logical px, ~21 degrees wide; 15.5 px text). It keeps its place beside the panel while the panel shows and follows
  (`PanelPlacement.AsLeader`), and is carried along on its own past 30 degrees (`PanelPlacement.Leash`) when the panel's away.
  A press on it that moves 2.5 cm drags it somewhere instead, as every menu (`docs/assistant.md` > The panel > Where it
  sits); kept there between sessions, dropped on its outline it follows again. Why there: the
  middle is for faces and the panel, chat logs read bottom-up, and the left is where people expect chat; reading it is
  a glance down and left.
- `SOJOURN_PANEL_IN_EYES=1` draws the feed into the eyes too (shots without a headset).

## How it's drawn

`Realtime/Overlay.cs` binds three pictures: the panel, the labels atlas (`OverlayScene.Labels`, `Tags`) and the feed
(`OverlayScene.Feed`, an `OverlaySheet`); styles 9-11 in `overlay.frag`. Tags are placed at record time from the
frame's `NameTag`s (`FrameRequest.ShowNames`), so they never lag the drawn avatar; the client decides which from the
last frame's (`FrameRenderer.Heads`). Tag pictures are drawn once per (name, username, typing, look) and packed into an
atlas off the main thread; the feed redraws off the main thread when what it shows changes. Tags and floating text
are drawn first, with the eye's depth: they write theirs where they show (so the compositor's depth reprojection
moves them as themselves), floating text and tags are depth-tested as the viewer's are (tags unless
`tags_through_walls`; the hover tip never), and they turn to the point between the eyes, one plane for both eyes
(`docs/rendering.md` > Name tags and floating text in the eye's depth). Chat bubbles and the hover tip are pictures in
the same atlas.

`--panel-shots DIR` also writes `social-feed*.png` (each look; `-menu-cut`, `-menu-pointed`, `-menu-open`: a long
script menu cut, lit with the close key, opened; `-notices`: the harp's eleven-key menu and a notecard given, waiting),
`social-tags*.png` and, with `view.png`, `view-social.png` (the feed beside an answer in a Quest eye).
`python tools/notices.py test` presses a menu's keys on the desktop (More >, a piece, ×, clear, Read) against the
Notices Harp on the local grid.

`python tools/social.py` (two clients on the local grid) checks keywords, mentions, a feed link pressed, the reader's
link, the test translator and the radar's reports, and takes shots of tags (title, ring, bubble, away, Do Not
Disturb, behind a wall and through it) and a hover tip into `local/social/`.

## Not done

- Scrolling back past what's still on the feed (ask the assistant).
- A translation service (the user's call); a translation arriving after its line was read out isn't read.
- Sojourn doesn't send mentions itself (the assistant's `say` writes names as words).
