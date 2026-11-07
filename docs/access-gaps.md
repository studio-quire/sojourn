# Sojourn Access: what it lacks next to Radegast

Written by Claude Opus 5.5, 2026-11-07. Agent-facing. The plan (`docs/access.md`, M1) says Access does everything Radegast
does, and the first tester's want is the same. On 2026-11-06 objects turned out to have no Pay or Buy and nothing could be
unblocked, so that claim had never been checked. This is the check: gaps and partial matches only, ranked. Take an entry
off when it's closed.

**How it was checked.** `docs/radegast.md` feature by feature, then the tasks blind residents hand to a sighted person, each
against what Access exposes in its code at `3d47ccf`: the menus in `MainForm.*.cs`, `ProcessCmdKey`, `Commands.cs`, the rows
in Alt+0's `Shown`. An op in Core or Session counted only for the size guess. Reading only: nothing built or run, no
screen reader, no grid, so items 1 and 2 (bugs from reading their paths) want reproducing on the local grid before the fix.
Radegast's side is radegast.md's word, itself read from source and never run. Not reviewed closely: walk mode's and rides'
internals, how each notice kind reads. Not counted as gaps: Radegast's pictures and 3D window, fonts and colours, tray,
tab highlights, plugins.

**The assistant doesn't close a gap.** It can reach many of these ops when asked, but the stance is that everything Radegast
does works with no assistant, and not everyone has one.

**Spot checks that held** (the tester's asks marked built): Pay, Buy and Take a copy on objects and parts; the block list and
Block on B; Alt+2 Friends; Say, history, people Tab order; `//go NAME`; `//follow NAME 1`; objects out to 125 m; Take off on
worn things; the two auto-accepts; Control+Shift+L; favourites and start places; gestures; switch lines in the chat history;
the delete sound. All are in the code as described. The `//` set's syntax, Alt+0's volumes and the Land and Build areas match
or pass Radegast except as listed.

**Why so much is missing.** Three causes, worth fixing as causes:
- Actions on a person are written out per list, so each list has a different subset (item 3).
- Alt+0 is a hand-kept list over SojournSettings; a setting not on it can only be changed by asking the assistant (item 8).
- Core was built for the assistant, so most of this is ops with no key. Most sizes below are small for that reason.

Sizes as in viewer-gaps: S a day or less, M a few days, L a week or more, for one agent. Numbers stay as first ranked, so references hold when an entry goes.

## Ranked

### 2. Conference chat: a reply fails, and she can't start one (bug and gap)
- **Radegast:** several friends selected, IM: a conference. Replies go to the session. The tester asked for this in Radegast's early years.
- **Access:** `SocialFeed.Im` makes every non-p2p session `FeedKind.Group`; `Joined` opens it with `group: true`; `SendImAsync`
  then sends `group_id`, and `TesselIM send` refuses with "not a member of that group". So she's invited in, hears them, and
  each line says "Not sent". The tab is titled "(group)" too. Starting one: lists are `MultiSelect = false`, and
  `TesselIM conference` is never called.
- **Where:** `MainForm.Chat.cs` `SendImAsync` (`session_id` unless it's really her group), `Conversation.Group`'s title; a
  Start a conference item on Friends, with several marked or picked (`Dialogs.Pick`).
- **Size:** S each.
- **Matters: high.** People start these on anyone; she looks as if she's ignoring them.

### 3. Actions on a person differ by list; an IM has almost none
- **Radegast:** IM toolbar: Profile, Pay, Offer Teleport. Profile: Start IM, Add Friend, Offer and Request Teleport, Pay, Mute,
  Give. Friends: rights (see online, see on map, modify objects). Near list: all of it plus Follow, Walk To and the rest.
- **Access:** an IM's Applications key has its sound and the edit items. Offer friendship exists only on Search results. Block
  exists only on People nearby and `//block`, which finds only someone nearby, so someone sending unwanted IMs from afar can't be
  blocked. The profile window has IM, Pay, Give and Describe only. Friends has no Block, Walk to or Where, Search no Give or
  Block. Friend rights aren't anywhere. No `//profile NAME` or `//friend NAME`.
- **Where:** one `PersonActions(id, name)` in `MainForm.People.cs`, used by `PeopleList`, friends, `ResultActions`,
  `ConversationMenu` and the profile's buttons; the ops all exist (`offerFriendship`, `mute`, `friendRights`, `offerTeleport`).
  Rights behind `ConfirmedAsync`, as the op says.
- **Size:** S-M.
- **Matters: high.** Daily. Blocking from an IM is safety; giving a partner edit rights is how a sighted helper helps without
  logging in as her, which is what the plan wants to end.

### 4. Objects: no details, no filter, thin menu
- **Radegast:** "your main source for learning what is around you", in its guide. Each line has its owner; the details pane has
  description, hover text, owner, creator, prim count, permissions. A word filter, rezzed/attached/both, sort by name. Contents,
  Return, Point at, Stand up; Take and Delete wherever the region allows.
- **Access:** a line has name, where, sale or payment, can sit or touchable. No details item: owner, creator and description
  are only behind Edit (build) > General, a route nobody would guess. No filter: `//find` gives the nearest one only (Radegast
  gives every match), and the list stops at the nearest 60 within 30 m (200 beyond). Nobody else's worn things. No Contents item
  (Edit, then type `content`). Take, Delete and Return: `Mine` only, so nothing group-owned or a friend's with edit rights,
  and no Return at all here. As read, the menu's Touch goes to the root, where `//touch` picks an object's only touchable
  child, so the treasure chest case can say Touched and do nothing from the menu (Tab to its parts works).
- **Where:** `MainForm.Lists.cs` `BuildObjects`, `MainForm.Parts.cs` (a filter box in its layout, passed as `nearby`'s `query`),
  `nearby` `attachments=true`, `TesselBuild returnObjects`, `TesselObjects get` for details.
- **Size:** S each, M together.
- **Matters: high.** Unnamed "Object" is the commonest thing she meets; owner and description are what say what it is, and a
  filter is the way through a busy place's scenery.

### 5. Groups: only chat, the tag and one spoken paragraph
- **Radegast:** General (charter, founder, fee, show in search, receive notices, list in profile, my active title), Members &
  Roles (list, last login, assign, invite, eject, ban), Notices (archive, read, attachment, new notice with an attachment), New
  Group, and a participant list in group chat.
- **Access:** Group chat, Wear its tag, No group tag, Leave, a sound, and `GroupInfoAsync` saying members, joining and charter once.
  No choice of title, so the group-tag task on the sighted-helper list (#141) is half done: which group, not which title.
- **Where:** a group window as the profile's, from `MainForm.People.cs`. `TesselGroups` has `title`, `roles`, `members`, `notice`,
  `notices`, `invite`, `eject`, `assignRole`, `create`, `update`; `SojournPeople groupChat` has who's in chat. `notices` gives subjects
  only: reading one's body needs a small new op.
- **Size:** L in all. Title S (do it alone first), notices M, members and invite M.
- **Matters: high** for anyone who runs a club or a group, and for reading a notice missed; the guide teaches notices. Title is
  medium-high by itself, on the list of what sends people to a sighted helper.

### 6. Scripts and contents: only inventory scripts, and no way to move around in them
- **Radegast:** New Script; open and save scripts and notecards inside an object; compile errors with line and column and the
  cursor moved to the first; Mono and Running; find and replace; open from and save to disk; a key saying the line and column;
  script errors in chat.
- **Access:** `EditorForm` on inventory items, errors in a box as text. No New script in the folder menu (`createScript` exists).
  Enter on a script in a prim's contents only toggles running; on a notecard it says to copy it out. No find, no go to the error,
  no line key, no Mono or Running, no disk. Runtime errors from her own objects never arrive: `SocialFeed` drops debug-channel
  chat, and `Scripts.OnDebugChat` reports only to the editor's published objects.
- **Where:** `FolderActions`; `MainForm.Build.cs` `ContentEnterAsync` with Core's `Scripts/ScriptEditor.cs` (it already saves task
  notecards, on the bridge's RPC); `EditorForm`; an Access switch in the feed for her own objects' errors.
- **Notecards:** what a card holds can be listed and copied out, but nothing can be added to one or taken off it (an item's marker deleted comes back at the end), and a card in an object's contents can't be copied out of directly.
- **Size:** New script and runtime errors S; find, go to error and the line key S; in-object editing M; adding and removing a card's items S.
- **Matters: high for the tester and her partner** (scripting in the viewer is on her stated list, and she welcomed Radegast's
  in-object editing when it came), low for most others. Without errors said, a script that breaks is silence.

### 7. Her own profile, and a profile picture
- **Radegast:** own profile from her name in the near list; About saves on leaving the field; Interests; Picks (detail, teleport,
  new, delete); Notes on anyone; groups listed with titles. A picture is hard there too (on the sighted-helper list).
- **Access:** no way even to read her own: the `people` op skips herself, and no menu or command opens it. Others' are read-only,
  picks by name only, no groups, no Notes.
- **Where:** `MainForm.People.cs` `ProfileAsync`, a World item and `//profile`. Ops: `profile` (`id` defaults to me), `setProfile`
  (with `image_id`), `pick`, `createPick`, `deletePick`, `notes`, `setNotes`. `profile` doesn't return groups: small Core addition.
  The picture: choose an inventory texture, Describe says what it is, the upload path is there.
- **Size:** M.
- **Matters: medium-high.** Not daily, but there's no way at all, and the picture is a named task. Notes are cheap and useful to
  someone who can't recognise a face or a look.

### 8. Settings only the assistant can change
- **Radegast:** Preferences has them all as controls.
- **Access:** registered in the session but not on Alt+0's `Shown`: `reject_teleports`, `reject_friendships`, `reject_group_invites`,
  `friends_only_ims`, `money_notices`, `typing_animation`, `typing_indicator`, `away_after`, `hover_height`, `mention_alerts`,
  `keyword_alerts`, `rlv`, `reject_group_calls`, `reject_conference_calls`, translation. Radegast's auto response (`setResponse`) and
  her keywords (`SojournSocial keywords`) have no way in, though the Mention sound row promises keywords. `maturity` is registered
  only by the VR client, so isn't there at all (item 10).
- **Where:** `MainForm.Settings.cs` `Shown`, a typed row for words (as `@key`); `AccessSettings.Register` for what's the client's.
- **Size:** S.
- **Matters: medium-high.** A flat breach of the stance, and very cheap. RLV in particular can't be turned on without a token.

### 9. Inventory housekeeping
- **Radegast:** Empty Trash and Lost and Found; Attach to (point list) and Attach to HUD; Play and Stop for sounds and animations;
  Paste as Link; New Script; search by creator and description; sort by date; Reload Inventory; Expand All; Backup to disk.
- **Access:** none of these. Worth noting: nothing ever leaves the Trash (`DeleteAsync` sends no `purge`, so Delete in Show: Trash
  does nothing new), and each Unbox of a copyable box adds its rezzed copy to it. F5 re-shows the local store, never asking the grid
  again, where Radegast's reload is how stale folders get mended. Recent is the last 24 hours only.
- **Where:** `ItemActions` and `FolderActions` in `MainForm.Inventory.cs`. Ops: `emptyTrash`, `delete` with `purge`, `attach` with
  `point`, `attachPoints`, `playSound`, `playAnimation`, `stopAnimation`, `link`.
- **Size:** S each; search fields S; refetch S-M; Backup M.
- **Matters: medium-high.** Attach to a point is how you wear something whose default is wrong, and Play is the only way she can
  tell what a sound is. Trash grows without end.

### 10. Putting herself right: rebake, stop animations and the rest
- **Radegast:** World and Tools: Rebake Textures, Set Appearance, Stop All Animations, Set Hover Height, Change Display Name, Set
  Access Level, Always Run; Health in the status bar.
- **Access:** none. Shift runs in walk mode only, so `//go` always walks.
- **Where:** `MainForm.Menu.cs` World. Ops: `rebake`, `stopAnimating`, `hoverHeight`, `setDisplayName`, `AgentSession.Access`'s
  `SetAgentAccessAsync`, `Movement.AlwaysRun`, `Self.Health` in `//status`.
- **Size:** S each, S-M together.
- **Matters: medium-high.** When someone tells her she's grey or stuck in a pose, these are what you do, and she can't see whether it's
  so; a maturity preference set low stops her entering regions with no way to change it.

### 11. The password is typed at every login
- **Radegast:** Remember; its guide's first step.
- **Access:** never kept (`AccessSettings` says so on purpose), so typed each start and whenever the session gives up logging in again.
  `--autologin` exists only as an option. Sojourn's own login has `LoginStore`.
- **Where:** `LoginForm`, `Session/Login/LoginStore.cs`. The reasoning behind never keeping it isn't written down; a session should
  decide this with confirmations' threat model in mind (a program on the PC reading what's stored).
- **Size:** S.
- **Matters: medium.** Every day, though nobody has complained on record.

### 12. Names a screen reader can't say
- **Radegast:** `display_name_mode`: display name only, both, smart, or off.
- **Access:** display name everywhere, the username only when it's empty (`PersonOps.PeopleAsync`, the feed). A display name in decorative
  Unicode (common on SL) likely reads as nothing or nonsense, with no way to get the username. That's reasoning, not something a tester
  has reported: worth asking her.
- **Where:** a mode in `AccessSettings`, applied in `PersonOps` and `Shape`; a fallback when a name has no plain letters.
- **Size:** S-M.
- **Matters: medium**, more if confirmed: not knowing who's speaking.

### 13. Chat logs, and history after closing a conversation
- **Radegast:** logs chat and IMs to files by default (`chat_log_dir`).
- **Access:** histories live in the window. Control+W loses one; reopening starts empty although `TesselIM history` may hold it (check it
  outlasts `leave`); nothing lasts past the session. The session log isn't for this.
- **Where:** `MainForm.Chat.cs` `Open` (fill from history), `Joined`; per-conversation files with a switch, keeping nothing in incognito.
- **Size:** fill S, files M.
- **Matters: medium.** Rereading what someone said yesterday; a screen reader user reviews text more than most.

### 14. Voice: who's muted
- **Radegast:** participant list with mute each, mute and unmute all, levels, reconnect.
- **Access:** Control+Shift+V says who's there. No muting one person short of blocking them, no one person's level, no reconnect.
- **Where:** `MainForm.Voice.cs`, `//voice mute NAME` and a list. `VoiceApi` has `peerMute`, `peerVolume`.
- **Size:** S-M.
- **Matters: medium.** Voice is a main channel for her, and one loud neighbour matters more without other cues.

### 15. Master Controls and Point at
- **Radegast:** a sighted friend points at something in their viewer and it becomes the selected thing, to touch, sit on or pay; the
  guide teaches it. Point at and Unpoint on people and objects.
- **Access:** neither. Parts and faces reduce the need, but "what my partner is pointing at" would name a thing among dozens called
  Object.
- **Where:** People nearby's and `MainForm.Lists.cs`'s menus; viewer effects in Core (not checked).
- **Size:** M.
- **Matters: medium.** Lets a helper help without the account.

### 16. Search stops at 30
- **Radegast:** 100 a page, next and previous, type and maturity filters, dates.
- **Access:** the first 30, nothing more; no filters. `SojournSearch` takes `start`, `category`, `when`.
- **Where:** `MainForm.Search.cs`.
- **Size:** S.
- **Matters: medium-low.**

### 17. People beyond 96 m
- **Radegast:** near list to 362 m, the region and its neighbours. Find online friends on the grid.
- **Access:** `RefreshPeople` asks for 96 m, though `//go person` and `//find` reach 512 if she knows the name. Nothing says where a
  friend elsewhere on the grid is.
- **Where:** `MainForm.Lists.cs`, a setting as for objects; Friends' Where.
- **Size:** S; friends' places S-M.
- **Matters: medium-low.**

### 18. Someone else's attachments and animations
- **Radegast:** Attachments on a person (listed, touchable) and Animations (what they're playing).
- **Access:** Describe's fallback names what they wear, once. Nothing to touch.
- **Where:** People menu, `nearby` `attachments=true`, parts as for her own.
- **Size:** S-M.
- **Matters: medium-low.** A pet or a tip jar someone wears is out of reach.

### 19. Links other than a person's read raw
- **Radegast:** `resolve_uris_as_plaintext` (#133, raised by a JAWS user).
- **Access:** `SocialFeed.Plain` resolves `app/agent` only. `secondlife:///app/group/<uuid>/about` and others are read out as they are.
- **Where:** `SocialFeed` or Access's `Shape`.
- **Size:** S.
- **Matters: medium-low.** Common in group invitations, and thirty-six characters of noise each.

### 20. Estate from the keys
- **Radegast:** Eject from estate, Eject and Ban on a person; Restart for estate managers.
- **Access:** About Land passes Radegast for parcels (`eject`, `ban` by typed name), but nothing on the person's menu, and the estate is
  ops only. `SojournLand` has `estateAdd`, `kick`, `sendHome`, `restart`, `message`.
- **Where:** `MainForm.Land.cs`, `LandRows.cs`, the People menu.
- **Size:** M.
- **Matters: low** daily, high for the rare blind estate owner.

### 21. The session itself
- **Radegast:** Disconnect, Reconnect, New window for a second account, Clean Cache, Check For Updates, Log Viewer.
- **Access:** Alt+F4 only. Another account means restarting (the app does that from the login form, which isn't reachable once in).
  No update check inside the app: a tester learns of a build by being told.
- **Where:** a File menu in `MainForm.Menu.cs`; `Program.cs`.
- **Size:** S-M.
- **Matters: low to medium.** Relogging is what people try when things go wrong.

### 22. Automation
- **Radegast:** AutoSit (#156 asks for more of it), PseudoHome, LSL Helper.
- **Access:** none.
- **Where:** a small part of its own beside `MainForm.Routes.cs`.
- **Size:** M.
- **Matters: low.** AutoSit is the one someone asked for.

### 23. Import and export
- **Radegast:** Import object from XML; Export to XML and DAE.
- **Access:** none. `SojournBuild` has `canExport`, `backup`, `restore`.
- **Where:** Build menu.
- **Size:** M.
- **Matters: low.**

### 24. Small things
All S, all low:
- `//scriptinfo` takes no `parcel`. `//thread`, `//threads` and `//kill` are absent, so a route using them pauses there.
- `//siminfo` lacks CPU class and data centre; `//parcelinfo` lacks ban and access list sizes and traffic.
- The status bar lacks the parcel and its no-fly and no-build marks (`//status parcel` has them).
- MU* `:` emotes; Show: Gestures in the inventory; Stand up on Objects' menu.
- Alt+3 is People and Alt+6 Places, against Radegast's Groups and Map; Alt+8 and Alt+9 are Notices and the assistant, against Media and
  Voice. Chosen, but "familiar where Radegast works" is only partly so, and there's no key to Groups.
- F1 says Enter touches under Objects, meaning a part; on an object Enter opens the menu.

## The sighted-helper list, what isn't closed

- **Checking an outfit:** by names without the assistant, by picture with it. A box worn by mistake shows only by its name.
- **Rezzing and placing:** done, without `//place`, so not in a route.
- **Land settings:** parcels done; estate at item 20.
- **Group tags:** which group, not which title: item 5.
- **Unboxing:** only from the inventory, and only where she can rez; the failure doesn't suggest a sandbox. Item 4 for a box already out, item
  9 for the copies left in the Trash.
- **HUD games:** a HUD that's one picture of buttons is read by the assistant, so with no token only numbered faces remain. Nothing to do
  short of a local reader, but the plan's "without an account" doesn't reach here and should say so.
- **A profile picture:** nothing: item 7.
