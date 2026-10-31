# People and me (SojournPeople)

Written by Claude Opus 5.5, 2026-10-27 and 2026-10-28 (batch B2 of `docs/viewer-gaps.md` > Closing the rest).
Agent-facing. Each feature as the official viewer or Firestorm does it (their source read for each, named below), an
op on `SojournPeople` for the assistant (`client_api`; its tool descriptions mention them), and a direct control where
a person needs one.

Code: `Core/Agents/PeopleOps.cs` (friends, contact sets, pay prices, favourites), `Presence.cs` (away, DND, autoresponse,
typing, hover height), `Places.cs` (SLurls, offers, map destinations, teleport history), `ExperienceOps.cs`,
`Rejects.cs` (friends only, the reject switches, inventory kept by itself), `GroupChat.cs`, `Money.cs`,
`NamesAndNotes.cs` (display name, notes), `PeopleStore.cs` (what's kept per account, `people.json` in the state dir),
`AgentSession.People.cs`;
`Leap/Apis/SojournPeople.cs`; the client half `Client/People/PeopleWiring.cs` (confirmations, the menu's "you" section
and its settings, a 4 Hz timer). Hooks in shared files: `AgentSession.Events.cs` (wiring, login, `Presence.Holds` and then
`PeopleTake` at the top of OnIM), `Confirmations.cs` (`Cases`, `PayCheck`, the `Words` kind), `SocialFeed.cs` (DND
quiet, the new notices' lines, no typing dots from those kept away), `Audio/Cues.cs` (no new-IM sound for them:
`AgentSession.PeopleHide`), `SettingsMenu.cs` (`PeopleRows`), `Program.cs`, `StockApis.cs`.

## What waits for the person (Confirmations)

Through `Confirmations.Cases` (a kind and a description per op, for any part of the client to add its own):
- **delete**: `removeFriend`, `removeFavorite` (to Trash), `clearTeleportHistory`, `contactSet {remove}`; `setNotes`
  replacing (or clearing) notes that say something else, the old text's start shown (the person's words would be gone;
  notes where there were none, an append or the same text don't ask).
- **permissions**: `friendRights` when it changes anything (reading them doesn't ask); the description shows each right
  as it is and as it'll be, and granting edit rights carries the viewer's GrantModifyRights warning. `setExperience
  allow`. `moderateGroupChat` (a member's text stopped or allowed for everyone; asked only when the person could:
  their group, its chat on, and not known to lack the power).
- **words** (a kind of its own): `setResponse` with text, or turning the away reply on; `setResponse` for the reject
  switches' sentences (kind teleports, friendships); `setDisplayName` (the exact name, or the username for a reset).
  The confirmation shows the exact words. This is how the autoresponse stays the person's own (the TPV policy: no
  auto-reply but what they set): the assistant writes down what they said it should say, and they press confirm on
  that text. Only a display name that could go is asked: one the grid's week wouldn't allow, too long, or on a grid
  without display names fails at once, from what's known since login.
- Not asked: the switches themselves, snoozing or turning off a group's chat (the person's own reception), new notes or
  a line appended (`setNotes` gives back what was there in `was`).
- **pay** to an object (`Confirmations.PayCheck`): its pay prices are asked first (RequestPayPrice, as the pay floater);
  an amount its pay window wouldn't offer (amount field hidden by PAY_HIDE and no button for it) is refused before the
  person is asked; otherwise the confirmation adds "Its pay buttons: L$50, L$100".

## Friends

- `friendRights {to, online?, map?, modify?}`: the viewer's profile Permissions floater (llpanelprofile.cpp): see me
  online, find me on the map, edit my objects (GrantUserRights with the whole new set). Map goes off with online, as the
  floater's checkbox does. It waits up to 6 s for the grid's ChangeUserRights echo; without one it updates
  LibreMetaverse's copy itself (`sent` instead of `changed`). `to` is an id, or a friend's display name, username or
  pseudonym. Returns `rights_given` (what they may do) and `rights_had` (what they let me).
- `removeFriend {to}`: as LLAvatarActions::handleRemove: edit rights revoked first, then TerminateFriendship.
- **Contact sets** (Firestorm's lggcontactsets): named sets with a colour, any avatar a member (friends or not),
  pseudonyms (names the person calls someone, shown to them only); kept on this computer per account. `contactSets`,
  `contactSet {name, color, rename, remove}`, `contactSetAdd/Remove {name, to | ids}`, `pseudonym {to, name}`.
  `PeopleOps.SetColor(id)` and `Pseudonym(id)` feed the name tags' ring and names, the hover tips and the radar
  (docs/social.md).

## Away and Do Not Disturb

As the viewer (LLAgent::setAFK/clearAFK/setDoNotDisturb, llimprocessing.cpp) and Firestorm's away reply:
- **Away**: the away animation and the AWAY control flag (re-asserted 4 times a second, since the movement keys write the
  same field from another thread). Automatic after `away_after` minutes (the viewer's AFKTimeout choices; 5 by default,
  as LL's; Firestorm's is never) without the person: no head or desktop-view movement over 2 cm or ~1.6 degrees, no talk
  or typing to the assistant, no dictation, and on the desktop no input while Sojourn's window is in front
  (GetLastInputInfo). In the headset that's the headset put down. Their being back clears an automatic away; one they
  set stays until they turn it off (in the viewer any key clears it, but a head never keeps still).
- **Do Not Disturb**: the busy animation; IMs and group chat kept in the conversations but not put on the feed, offers
  (type "offer": teleports, friendship, items, invitations) held back to the bell; object IMs and alerts from objects not
  theirs dropped and inventory offered by objects declined (`Presence.Holds`); nearby chat, script menus and alerts still
  show. An autoresponse (`BusyAutoResponse`) once per conversation to a live IM, and to each teleport offer, teleport
  request and friendship offer, as the viewer does. When it ends, held notices come back on the feed and a tip says how
  many IMs came from how many people.
- The words: the viewer's own sentence ("This resident has turned on 'Do Not Disturb' and will see your message later.")
  until the person sets theirs with `setResponse {kind: dnd, text}` (confirmed). The away reply (Firestorm's
  FSSendAwayAvatarResponse) is off by default; `setResponse {kind: away, on: true, text?}`. Each one sent is logged as a
  TesselEvents `autoresponse` event without its words.
- "Live" IMs: the viewer replies only to IM_ONLINE; LibreMetaverse's clients mark every IM offline, so a live one is also
  told by having no stored time (`Presence.Live`). Sojourn's own TesselIM send marks IMs offline too (LibreMetaverse's
  default; the viewer sends IM_ONLINE), so a viewer in DND that goes by the flag may not answer Sojourn's IMs. Left as is:
  whether OpenSim stores offline IMs sent with IM_ONLINE is unchecked.
- A new login starts present and disturbable, as the viewer does.

## Typing

`Presence.Typing(on, to?)`: nearby, ChatFromViewer StartTyping/StopTyping (built by hand: LibreMetaverse's Chat drops
an empty message) and the typing animation (PlayTypingAnim); one person's IM, IM_TYPING_START/STOP (the viewer sends IM
typing only one to one). Shown while the person dictates to people (Private/Dictation.cs: held, or its draft waiting),
toward the IM open on the feed or nearby; not while they talk or type to the assistant (that isn't writing to anyone).
Re-sent every 10 s while it lasts; an op's start runs out after 30 s. Settings `typing_indicator` (Firestorm's
FSSendTypingState) and `typing_animation`.

## Hover height

`hoverHeight {height}` and the `hover_height` setting (-2 to 2 m): kept per account (the viewer's AvatarHoverOffsetZ),
POSTed to the AgentPreferences capability on change and at login (only once set here; until then the account's own
value from the capability is shown). The menu's row steps 2 cm, sending once a run of presses stops.

## Experiences

`experiences {mine?}` (allowed, blocked; mine and admin), `experience {id}` (profile and my permission),
`findExperiences {text}`, `setExperience {id, allow|block|forget}`, `regionExperiences`. Second Life only: OpenSim has
no experience capabilities, and each op says so.

## Places

- **Teleport history** (LLTeleportHistory and its storage): each arrival by teleport and at login, once the parcel's name
  is known, kept per account (`teleport-history.json`, 200 places), the same place within 5 m kept once at its first
  position with its latest time, in order of arrival (a stop left before its parcel's name came is still kept, at its
  own region). Nothing during incognito. `teleportHistory {count}` (newest first, `here` on the first,
  `go_to` for the go tool), `clearTeleportHistory`.
- **Favourites** (the favourites bar: landmarks in the Favorites folder): `favorites`, `addFavorite {item_id?}` (here, as
  a landmark made there, or a copy of a landmark; a no-copy one is moved), `removeFavorite` (to Trash).
- **Places offered** (`Places.Offer`): a `PlaceOffered` notice with a Teleport key on the feed (the person's press, or
  the assistant's answer when asked, teleports; not gated, as any teleport they ask for), the same place replacing
  itself, four at most, each gone after three minutes unanswered. From:
  - a script's llMapDestination (ScriptTeleportRequest; the viewer opens the map with a beacon and a Teleport button),
    unless the object is blocked;
  - SLurls (secondlife:///app/teleport|region|worldmap, secondlife://Region/x/y/z, maps.secondlife.com and slurl.com) on
    the feed and in the reader, drawn as links (docs/social.md): a press on one offers its place, as the viewer's
    click; opening a block or a notecard offers nothing by itself. Not on arrival: greeters and boards post SLurls all
    day;
  - the assistant: `offerPlace {slurl | region, pos}`; `parseSlurls {text}`.
- Not done: landmarks embedded in notecards aren't offered (the reader shows the text).

## What's turned away (Rejects)

All per account in `people.json`, all off by default as in both viewers; settings in section you, and `SojournPeople
rejects` reads them with how many were turned away this session. Checked in OnIM after blocks, antispam and Do Not
Disturb (`PeopleTake`); other listeners ask `PeopleHide`, which has no side effects.
- `friends_only_ims` (the official viewer's VoiceCallsFriendsOnly, Preferences > Privacy; llimprocessing.cpp, llimview.cpp
  addMessage): one-to-one IMs from anyone but friends (and Lindens: second name "linden") aren't shown, with one quiet
  `IMNotShownNotFriend` notice a conversation (the viewer writes a line into the conversation saying how to see it);
  their teleport offers and requests are dropped silently; a conference a non-friend starts is left (ongoing ones carry
  on). Group chat and offers of friendship or inventory aren't touched. In Do Not Disturb their IMs still get the DND
  sentence, as in the viewer (its DND branch comes first). Its voice-call half: a non-friend's one-to-one and
  conference calls are turned away before they ring, groups' still ring (docs/voice.md > Calls turned away).
- `reject_teleports` off | all | not_friends (Firestorm's FSRejectTeleportOffersMode with
  FSDontRejectTeleportOffersFromFriends): teleport offers and requests dropped and answered with a sentence;
  `reject_friendships` (FSRejectFriendshipRequestsMode): friendship offers the same. The sentences are Firestorm's with
  its name taken out until the person sets theirs (`setResponse {kind: teleports|friendships, text}`, confirmed); sent
  as BusyAutoResponse IMs, logged as `autoresponse` events without the words. In DND these get only the reject sentence
  (Firestorm's order). A turned-away friendship offer is also declined, a deliberate departure from Firestorm (which
  lets it lapse; 2026-10-29): the sender gets the sentence and the usual "declined", and nothing is left pending (OpenSim
  keeps an unanswered offer as a half friendship, sent again at each login and blocking an offer the other way).
- `reject_group_invites` (FSRejectAllGroupInvitesMode): invitations dropped without a word.
- `auto_accept_inventory` off | viewer | all (AutoAcceptNewInventory: the official viewer's for notecards, textures and
  landmarks, Firestorm's for anything): kept without the offer notice, and a notice (`InvOfferAutoAcceptUser`,
  `InvOfferAutoAcceptObject`) says what came, as Firestorm's FSShowAutoAcceptInventoryInNotifications does by default.
  Do Not Disturb still turns objects' offers away first.

## A group's chat (GroupChat)

- **Snooze** (Firestorm's, from Catznip: llgroupactions.cpp, llimview.cpp): `snoozeGroupChat {group, minutes?}` (15 by
  default, GroupSnoozeTime; 0 ends it): the session isn't left, its lines are dropped until then, and the first line
  after brings it back. In memory: a new login starts unsnoozed.
- **Off** (Firestorm's "Receive group chat", exoGroupMuteList): `setGroupChat {group, on}`. Kept as the by-name block
  "Group:<id>" on the account's list (here and on the grid), as Firestorm keeps it on SL, so it holds there too; the
  session is left, and every later invitation to it (someone speaking brings one) left again, at most once a minute.
  Turned on, it comes back when the session next reaches the person.
- **Moderation** (LLIMSpeakerMgr::toggleAllowTextChat): `moderateGroupChat {group, to, text: false|true}` POSTs the
  ChatSessionRequest capability's "mute update" with mute_info.text; the person must moderate the chat (its member
  list's is_moderator, else their roles' moderate-chat power); the chat is joined first if needed. `groupChat {group}`
  lists the members with moderator and text_stopped, `groupChat` alone every group's chat_on and snooze. Voice
  moderation: docs/voice.md.

## Money (Money)

The official viewer's process_money_balance_reply(_extended): each transaction once (the last 30 ids), in its sentences
("X paid you L$N for item.", "You paid X L$N: gift words", "You failed to pay ..."; a blocked payer's words left out),
after the other side's name is known; on the feed as `PaymentReceived` (who, "paid you L$N ..."), `PaymentSent`,
`PaymentFailure`. `money_notices` all (both viewers' default) | received | spent | off. `SojournPeople money` is
Firestorm's money tracker, both ways, for the session: received, spent, totals, payers summed. In memory only; what
came during incognito shows only while that spell sees, and is gone after. World audio's money sounds are separate
(`Audio/Cues.cs`).

## Display name and notes (NamesAndNotes)

- `displayName` (names, `can_change_now`, `next_change`) and `setDisplayName {name | reset}` as the viewer's floater: 31
  characters at most, the username (or reset) sends an empty name, Second Life's once a week refused before asking (a
  reset is allowed anyway, as the floater allows it); the SetDisplayName capability with [old, new], then the event
  queue's SetDisplayNameReply (200 done; else the grid's reason; on 409 the name is fetched again); the viewer's "Hi X!
  ... several days" tip after. Others' changes while about: `DisplayNameUpdate` ("X (user) is now known as Y"). A grid
  without the display-name service: both ops say so. The local OpenSim serves names with a week's lockout of its own
  and has no SetDisplayName, so only the read and the refusal before asking are checked there.
- `notes {to}`, `setNotes {to, text, append?}`: the AgentProfile capability (GET's notes, PUT {notes}, 65530 characters),
  else AvatarNotesRequest/Update (OpenSim's profiles service).

## Pay price buttons

`payPrices {id}` -> `{any_amount, suggested, buttons}` from PayPriceReply (PAY_HIDE -1, PAY_DEFAULT -2 as the floater
reads them), cached 30 s; and the pay check above. No direct pay board: the person asks the assistant, which can offer
the buttons with ask_choice.

## The menu

Section "you": do not disturb, away (noted "by itself" when automatic), away after, hover height, show typing, only
friends IM me, turn away teleports (off, all, not friends'), turn away friendships, turn away group invites; voice's
call switches beside them, and *turn away calls from*, a switch per group (docs/voice.md). Settings on
`SojournSettings` (section you): `do_not_disturb`, `away`, `away_after`, `hover_height`, `typing_indicator`,
`typing_animation`, `friends_only_ims`, `reject_teleports`, `reject_friendships`, `reject_group_invites`,
`auto_accept_inventory`, `money_notices` (the last two on the API only). The autoresponse words aren't settings
(they'd skip the confirmation): `SojournPeople status` reads them.

## Testing

`python tools/people.py [--dll DIR]` on the local grid: Sojourn People (:9861, SOJOURN_TEST_INPUT=1) and Sojourn Peer
(:9862), base dirs in `local/people/`. `python tools/people2.py [--dll DIR]`, the same clients: friends only, the
reject switches and their words, inventory kept by itself, a group ("Sojourn People Test", People's own) joined,
snoozed, off and moderated, display names read and a change refused before asking, notes, the money tracker's shape.
It needs groups and profiles on the local grid: Multitude's SQLite groups store for OpenSim's Groups V2 (`python
projects/multitude/grid/groups.py deploy local/grid/bin`), and the standalone profile service on SQLite
(`local/grid/bin/config/Sojourn.Profiles.ini`). ChatSessionRequest there is the WebRTC voice module's, so moderation's
"mute update" answers 400: the confirmation and the moderator check before it are tested, the send isn't. Because
OpenSim keeps an unanswered friendship offer (above), people.py accepts a waiting offer, or has Peer offer, so
leftovers from any run don't stop it.

## Checks only Second Life can do

The local grid has no display names, no L$, and its group chat server isn't SL's:
1. Display name: `displayName` shows the right next_change; a change asks with the exact name, lands, and the "Hi X!"
   notice comes; a second change the same week is refused at once, without asking; a reset (the username) is allowed.
2. Money: pay someone (or a tip jar) and get paid: "You paid X L$N." and "X paid you L$N." on the feed, a gift's words
   after a colon, a vendor's item named ("for <item>"); each once; `SojournPeople money` lists them with payers summed.
3. Group chat: snooze a busy group (its lines stop for 15 minutes and come back with the next line after); turn its
   chat off (it stays off after a relog, and Firestorm on the same account shows "Receive group chat" unticked); on
   again brings it back.
4. Moderation, in a group where the person is a moderator: stopping one member's text asks first, the region takes
   it (no "refused" error), that member's lines stop reaching everyone and `groupChat {group}` shows them
   text_stopped; allowing it again restores them. Never checked anywhere yet: OpenSim can't do it.
5. Notes: `setNotes` on someone, then the official viewer's or Firestorm's profile > Notes shows the same text.
6. Only friends and groups: a non-friend's IM isn't shown (one quiet notice), a friend's is; their teleport offers
   don't arrive. Turning away teleports or friendships: the sender (an alt or a friend's viewer) gets the sentence.
