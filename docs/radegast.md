# Radegast, all of it: an inventory for Sojourn Access

Written by Claude Opus 5.5, 2026-10-28. Agent-facing. What Radegast does as a viewer and how blind people use it, so
Sojourn Access (`docs/accessibility.md`) can cover all of it and go past it. Part 1 of accessibility.md already has the
short version (screen readers, the Speech plugin, keys, `//` commands, lists); this goes wider and deeper and doesn't
repeat that part's Speech-plugin detail.

**Sources and marks.** Code: github.com/cinderblocks/radegast at `4f5dd45` (2026-10-26, "Stop generating release notes
with the Copilot CLI"), 3,187 commits; paths below are in that repo, read but never built or run. **[code]** = what the
source says (runtime behaviour not verified). **[FH]** = first-hand from a blind or low-vision user. **[doc]** = the
project's own documentation. **[dev]** = the maintainer or a developer speaking. The help guide, blogs and issue
findings came from two research agents reading the web and GitHub on 2026-10-28; not reached: the old radegast.org
wiki (Wayback blocked; it had an Accessibility_Guide page, likely the same guide), scriptable.wikispaces.com,
forum.audiogames.net and SL forums (Cloudflare), ~270 JIRA issues never archived.

**Much of it came from blind users themselves:** in 2009-10 Latif Khalifa worked with blind residents on the
features blind people still use: the `//` commands came from ScriptAble's Guide scripts (April 2010), and keyboard tab
closing, whisper/shout keys, notecard editing, `//status` and others were blind users' JIRA requests. One blind
resident's wishlist is below ("A blind resident's account").

## What blind users rely on most

1. **Chat by keyboard, read by reviewing history.** Alt+1 to the input, Enter to send, Tab to the history to hear what
   came in (NVDA/JAWS don't announce it; the Speech plugin can, and early blind users loved its spatial voices).
   Turning off timestamps and prefixes because they're tiring to hear.
2. **`//` commands for being somewhere:** `//go` (distance and compass direction, xyz, a person, an object, with
   `tp`), `//find`, `//face`, `//sit`, `//follow`, `//tp` from a landmark folder, `//status`. Blind residents navigate venues
   by **chains of `//go xyz` waypoints kept in notecards**.
3. **The nearby people list** (nearest first, live distances) and **the Objects tab** ("your main source for learning
   what is around you"), each with an Applications-key menu holding every action (touch, sit, walk to, face, pay, buy,
   contents, teleport offers...).
4. **Blue dialogs that take focus** so script menus, offers and permission requests get read, answered by Tab/Enter.
5. **Inventory by keyboard:** the tree, search with the Worn and Recent filters ("one way to make sure you are fully
   dressed"), Replace Outfit, Wear/Attach/Detach, unboxing by rezzing and opening contents, giving by Copy then the
   profile's Give Inventory, notecards written with Ctrl+S.
6. **Teleporting as the main way to travel:** SLurl on the clipboard + Ctrl+G, landmarks, search results, offers.
7. **Alt+1-9, Ctrl+Tab, Ctrl+W** to move between areas; IMs as tabs; "Ding on every IM".
8. **Sighted help as the fallback** for anything visual: outfit checks, rezzing and placing, parcel settings, survey
   objects, child prims. Master Controls exists for a sighted friend to point at things for you.

## What it lacks (as a viewer, for a blind user)

- **No announcements to the screen reader.** New chat, IMs, arrivals, departures, teleport completion: all must be
  found by Tabbing to a history. The only voice that speaks on its own is the Speech plugin's (off by default, second
  voice, Legacy only), and several statuses (compile errors, Ctrl+L in the script editor, new-notecard prompts) are
  sent *only* to it. A 2020 request for "a toggle button to make a screen reader read chat automatically" is still
  unmet.
- **Focus theft:** new tabs and notifications grab focus mid-typing (#82, 17 years running).
- **No free movement by keyboard** outside the 3D Scene window; the chat tab's walk/turn buttons respond to the mouse
  only. Walking is the server's straight-line autopilot: no pathfinding, no warning before a wall, "Finished walking N
  meters from destination" after.
- **No spatial sense:** no audio radar, beacons, footsteps, surfaces, doors, walls or room shape; no descriptions of
  places, avatars, outfits or pictures; hover text sits silently in a details pane. Object names ("Object") are often
  all there is.
- **Touch is coarse:** whole prims by name, including child prims and HUD buttons in a list most can't make sense of
  (unnamed, and labelled "Objects" like the main list); never a face, a point, or a drag. A blind user still can't open a
  treasure chest's lid.
- **No real building:** SimpleBuilder rezzes single basic prims with numeric placement; no linking, shapes, textures,
  or editing others' objects; no "where is the ground" (#168). Uploads exist (image, mesh, import from XML) but aren't
  taught or accessibility-checked.
- **Vehicles:** no handling of controls taken by scripts, no driving except from the 3D window, nothing for riding
  across region borders (a blind user's wish).
- **Land and estate:** no About Land (#136), no access/ban lists, buying, media settings.
- **No web/video media, no HUD understanding** beyond prim names; no profile picture setting; no multi-select in
  inventory (a blind user's wish) and Delete trashes without confirmation.
- **Bugs that matter:** `//face object` and `//find object` search for the wrong string (so rarely match); first use of
  any object command only opens the Objects tab ("try again in a minute"); every release has regressed something a JAWS
  user relies on, with no screen-reader testing.
- **Veles** fixes some of the announcement problem (live regions) but drops the `//` commands, Alt+1-9 and the Speech
  plugin, and is pre-alpha.

## Legacy, area by area

Legacy is the WinForms client (`Radegast/`, .NET Framework 4.8, Windows only): one main window holding **tabs**, a
menu bar and a status bar. Most of its UI is designer-built WinForms, so a screen reader gets MSAA/UIA names and Tab
order, nothing more (see accessibility.md part 1 on announcements).

### The main window, menus and global keys (`Radegast/GUI/Dialogs/MainForm.cs`, `MainForm.Designer.cs`)

- **Menus** (Alt then arrows; File, World, Tools, Plugins, Help have mnemonics):
  - File: New window (a second account in the same app), Upload image, Upload mesh, Import object, Script editor,
    Login, Disconnect, Reconnect, Preferences, Exit.
  - World: Fly, Always Run, Sit On Ground, Stand, Set Hover Height, Stop All Animations, Change My Display Name, Mute
    List, Create Landmark Here, Set Home To Here, Teleport Home (Ctrl+Shift+H), Region/Parcel (Ctrl+Shift+1), Set
    Access Level (PG / Mature / Adult), Away, Busy.
  - Tools: Autopilot (a test stub with hardcoded waypoints, not a user feature), Clean Cache, Reload Inventory, Set
    Appearance, Rebake Textures, My Attachments, Mini Map.
  - Plugins: Manage... plus each plugin's own entry (Speech, Build Prims...).
  - Help: Keyboard Shortcuts, Bugs/Feature Requests (radegast.life/bugs/issue-entry), **Accessibility Guide** (opens
    radegast.life/documentation/help, added 2014), Check For Updates, About, Log Viewer, Debug Console (Ctrl+Alt+D).
  - Toolbar buttons (not keyboard-reachable except via the Alt+number keys): Friends, Groups, Inventory, Search, Map,
    Objects, Media, Voice, 3D Scene, a clock in SL time.
- **Status bar** (labels, read by a screen reader's status-bar command): login name, L$ balance, region, parcel, health,
  and the parcel's no-fly / no-build / no-script / no-push / no-voice icons (images with text labels; one is mislabelled
  "Push" for voice).
- **Global keys** (`frmMain_KeyDown`; the Help > Keyboard Shortcuts text is in `KeyboardShortcuts.resx`):
  - Alt+1 Chat, Alt+2 Friends, Alt+3 Groups, Alt+4 Inventory, Alt+5 Search, Alt+6 Map, Alt+7 Objects, Alt+8 Media,
    Alt+9 Voice (the 2-9 ones toggle the tab open/closed, as the toolbar buttons do).
  - Ctrl+Tab / Ctrl+Shift+Tab next/previous tab (Firefox-style, 2009, RAD-47); Ctrl+W closes or hides the tab.
  - Ctrl+G: teleport to the SLurl on the clipboard, or open the map at your position.
  - Ctrl+Shift+H home; Ctrl+Shift+1 region/parcel info; Ctrl+Alt+D debug console; Ctrl+Alt+Shift+H says "Hippos!"
    in chat (an easter egg).
  - In any chat or IM input: Enter says, Shift+Enter whispers, Ctrl+Enter shouts; Ctrl+Up/Down recall earlier lines.
  - Context menus: the Applications key, or Ctrl+Enter in some lists (`RadegastContextMenu.ContexMenuKeyCode`; friends
    list, inventory search results). Context menus are `RadegastContextMenuStrip`s that plugins can add to (Douglas R.
    Miles, 2009, "accessible context menus").
- **Tabs** can be detached into their own windows, hidden or closed (`RadegastTab`, `DetachedTab.cs`). IM sessions
  open as new tabs; a tab "highlights" (and the taskbar flashes) on new messages per the Preferences switches; a
  partner typing partially highlights their tab. None of this is spoken (highlight is visual).
- **Notifications** ("blue dialogs", `MainForm.AddNotification`, `GUI/Notifications/*`): a panel at the top right
  shows one notice at a time; more queue behind a ">>" button (a `TransparentButton` with no accessible name). Each
  notice is `AccessibleRole.Alert`, plays the window-open UI sound, flashes the taskbar, and **moves focus to its first
  button** (buttons get "Press Enter to activate X" descriptions). That's how a dialog gets read, and also why focus
  jumps out of the chat input when one arrives. A hidden `notification_toast_enabled` setting shows them instead as
  toasts that time out (5 s) and drop the oldest past five, which would lose dialogs for a screen-reader user.
  Kinds: script dialogs (buttons, llTextBox input, Ignore), permission requests (Yes / No / Mute), inventory offers
  (Accept / Discard / Ignore), teleport offers and requests, friendship and group invitations, group notices (with
  attachment), llLoadURL, region restarts (with countdown), generic alerts and system messages. Muted residents and
  objects are filtered first (`TabsConsole.cs` ~250-560).

### Login (`GUI/Consoles/LoginConsole.cs`, `GUI/Dialogs/MfaPrompt.cs`)

Username, Password, Grid (list or custom Login URL), Start location (home / last / region), Remember, "I agree to the
Terms of Service", Login. All fields have accessible names. MFA codes come in their own dialog. Login progress and
errors print in the chat tab ("Logged in as...", "Login error: ..."). Auto-reconnect on disconnect is a preference.

### Chat tab (`GUI/Consoles/ChatConsole.cs`, `Core/ChatTextManager.cs`, `Core/RichTextBoxPrinter.cs`)

- **Layout and Tab order:** chat input (0) → chat type combo (Say/Whisper/Shout, 11) → Say button → nearby people
  list "Nearby people" (0 in its own panel) → movement buttons → chat history "Chat history" (a RichTextBox, 4).
- **History lines** (`ChatTextManager.cs`): `[time] Name: text`, `Name whispers: ...`, `Name shouts: ...`, emotes as
  `Name text`, typing as `Name ...`; objects in a different style; owner-say, script errors (`show_script_errors`),
  Linden chat, your own channel chat as `(channel N) You: ...`, alerts as `Alert message: ...`, and the client's own
  status lines (login, teleports, "Finished walking 2 meters from destination", L$ transactions if enabled, friends
  online/offline if enabled, display-name changes, inventory accepted/declined). Muted residents, muted objects,
  objects whose owner is muted and objects muted by name are dropped. Avatar names can be links (off by default since
  2012 because they troubled blind users); URLs and SLurls can be resolved to plain text (`resolve_uris_as_plaintext`,
  for JAWS, #133). Timestamps optional. **Nothing is announced:** the person reviews the history with the screen
  reader (accessibility.md part 1).
- **Sending** (`ProcessChatInput`): `/N text` on channel N, `/me` emotes, MU*-style `:` emotes (`mu_emotes`), `//`
  commands (below), gesture triggers (chat text replaced by a gesture's trigger, `GestureManager`), RLV chat
  redirections/limits. 1,000-character limit; pasted newlines become ¶ and are sent as line breaks. The typing
  animation is sent while typing unless `no_typing_anim`; typing a leading `/` doesn't trigger it.
- **Nearby people list** ("radar", `UpdateRadar`): everyone in the region and neighbours within 362 m, as `Name (12m)`,
  `Name (?m)` when height is unknown, a trailing `*` when the avatar is within draw distance; you in bold; sorted by
  distance (`SorterClass`). Enter/double-click opens the profile. Context menu: Profile, Pay, Start IM, Follow /
  Unfollow, Textures, Attachments, Master controls, Animations, Point at / Unpoint, Offer Teleport, Teleport To,
  Request Teleport, Walk To, Face, Eject, Ban, Eject from estate, Mute/Unmute, plus plugin actions. Dropping an
  inventory item on a name gives it (mouse only; the keyboard route is Copy in inventory, then the profile's Give
  Inventory button). Selecting someone fires the Speech plugin's "N meters to your right".
- **Movement buttons** (named "Walk forward", "Walk backwards", "Turn left", "Turn right", "Jump/Up", "Crouch/Down",
  "Fly", "Run"): **the walk and turn buttons act on MouseDown/MouseUp only**, so pressing them with Space or Enter does
  nothing; Fly and Run are clicks and work. A keyboard user can't walk from the chat tab. [code]

### Chat commands: the full `//` set (`Radegast/Core/Commands/*.cs`, `Radegast.Core/CommandsManager.cs`)

Typed into any chat input with the `//` prefix; parsed with quotes and backslash escapes; queued on one worker thread;
output goes to the chat history as notification lines (so the screen reader finds it by reviewing history). The
navigation set (go, face, find, follow, sit, unsit, tp, status) arrived in one burst, 2010-04-23..26 (RAD-147..157,
Latif Khalifa); scriptinfo/siminfo/parcelinfo earlier in 2010. Legacy only: **Veles has none of them** (its
CommandsManager carries only `help` and plugin commands).

| Command | Arguments and what it does |
|---|---|
| `//help [word]` | Lists commands whose name contains the word: `name: description Usage: //usage`. |
| `//go [tp] N [dir]` | Walk (or with `tp` teleport, where the parcel allows) N metres forward, or toward a compass direction (E, ENE, NE ... 16 points). Turns, then walks by the server's autopilot. |
| `//go [tp] xyz X,Y[,Z]` | Walk/teleport to region coordinates (Z defaults to current height). |
| `//go [tp] object <name>` | To the first object whose name contains the text (needs the Objects tab open; the first use opens it and says "try again in a minute"). |
| `//go [tp] person <name>` | To the nearest person whose name starts with the text. |
| `//go help` | Full usage. |
| `//face <degrees>` | Face a heading (0 = east, 90 = north, SL convention). |
| `//face <dir>` | Face a compass point (N, SW...). (The built-in help text has a typo: "face n -- face east".) |
| `//face person <name>` / `//face object <name>` | Turn toward them. **Bug:** `face object` and `find object` search for the whole argument string including the word "object", so they rarely match. [code] |
| `//find person <name>` | For each match: "Name is 12 meters away to the North by Northeast at our level / 3 meters above our level". |
| `//find object <name>` | Same for objects (see the bug above). |
| `//follow <name>` / `//follow stop` / `//follow` | Follow someone at ~3 m by autopilot, stop, or report who you follow. |
| `//sit <object name>` / `//sit ground` | Sit on the first object whose name contains the text, or on the ground. |
| `//unsit` | Stand. |
| `//tp list` / `//tp <n>` / `//tp <name>` | Landmarks in the inventory folder **"Radegast Landmarks"** (created if missing), numbered by creation date; teleport by number or name prefix. |
| `//status [doing\|region\|parcel\|location\|money\|time]` | Any or all: sitting on X / flying / standing, always run, following; region and maturity; parcel name and no-fly/no-build/no-scripts/no-push/damage/no-voice; position, heading in degrees and compass; L$ balance; SL time. |
| `//scriptinfo [avatar\|parcel]` | Script memory and URLs used, per attachment point or per parcel object. |
| `//siminfo` | Region product, CPU class, data centre, agents, active scripts, time dilation. |
| `//parcelinfo` | Every parcel in the region: local ID, name, description, ban/access list sizes, traffic. |
| `//thread <command>` / `//threads` / `//kill` / `//pause [s]` | Run a command in the background, list or kill them, pause a command script. |
| `//loadplugin <path>` | Load a plugin DLL or C# source file at runtime. |

Walking ends with "Finished walking N meters from destination" when within 2 m, when the autopilot is cancelled, or
after 10 s without progress (`StateManager.WalkTo`). There's no pathfinding: the server's autopilot walks straight, so
walls stop it. Following re-aims the autopilot as the person moves (`FollowUpdate`).

### Instant messages and conferences (`IMTabWindow.cs`, `GroupIMTabWindow.cs`, `ConferenceIMTabWindow.cs`, `Core/IMTextManager.cs`)

One tab per conversation, opened automatically on an incoming IM. Input (Enter, Ctrl+Up/Down history), Send, history.
Toolbar: Profile, Pay, Offer Teleport, "Ding on every IM" (a sound per message, `im_ding_all`). Group chat adds a
participant list with Profile, Pay, Start IM, Offer/Request Teleport, Eject, Ban, Mute, and a Show toggle. Friends can
be multi-selected into a conference. Settings: ignore conference chats, allow them from friends, log ignored ones to
local chat, sound on new group IM, sound on being mentioned (`mention_me_sound`), auto-response (always / non-friends /
busy, with a text). IM, group and conference tab controls got accessible names on 2026-10-22. Logs to files unless
disabled (`chat_log_dir`). Offline IMs arrive at login.

### Friends (`FriendsConsole.cs`)

List of friends with online state; IM (several selected → conference), Profile, Remove, Pay, Offer Teleport, Request
Teleport; the rights you grant (see online status, see on map, modify objects) as checkboxes. Online/offline notices go
to chat if enabled, plus an attempted screen-reader announcement that doesn't work (accessibility.md part 1). Friend
requests became "much more verbose ... screen reader friendly" in 2025-11 (PR #140, nooperation).

### Groups (`GroupsConsole.cs`, `GroupDetails.cs`, `Dialogs/GroupInfo.cs`)

List of groups ("List of groups"; the active group's tag now in its accessible name, 2026-10-22): Activate, Leave, IM,
Info, Refresh, New Group (name, charter, Create), Mute. Group details: General (charter, insignia, founder, enrollment
fee, open enrollment, show in search, receive notices, list in profile, my active title), Members & Roles (members with
last login, roles, abilities, assign, invite, eject, ban, save member list), Notices (archive, read, new notice with
subject, text and an attachment pasted from inventory), Banned residents.

### Inventory (`GUI/Consoles/Inventory/InventoryConsole.cs`, 3,227 lines)

- A tree (arrow keys expand/collapse natively), with search (by name, creator, description; filters: all, recent, worn,
  gestures), sort (by name / date, folders by name, system folders first) and a details pane (name, description,
  creator, permissions, dates, UUIDs).
- Keys on the tree: **F2** rename, **F5** refresh folder, **Delete** moves to Trash *without confirmation*, Apps key
  context menu. Search box: Enter searches; results list: Apps or Ctrl+Enter.
- Context menu by item type: Open (notecard, script, image, landmark, gesture, animation, sound...), Wear / Add to Worn
  / Take off, Attach to (point list), Attach to HUD, Detach, Replace Outfit / Add to Outfit / Take off Items (folders),
  Rez inworld, Touch (an attachment), Teleport (landmark), Info, Play / Stop (animation, gesture), Activate / Deactivate
  (gesture), Rename, Cut, Copy, Paste, Paste as Link, Delete, New Folder, New Note, New Script, Empty Trash, Empty Lost
  and Found, Expand / Expand All / Collapse, Fix type, Refresh, Backup...
- New notecards and scripts ask for a name; their prompt is an "invisible" notice (spoken only by the Speech plugin).
- Backup (`InventoryBackup.cs`) saves notecards, scripts and other text assets to disk.
- Inventory offers can be auto-accepted (`inv_auto_accept_mode`: ask, accept, discard).
- Outfits are folders (My Outfits) worn by Replace Outfit; the current outfit folder is kept per the viewer's COF rules
  with RLV policy (`OutfitManager.cs`, `RlvCOFPolicy.cs`).

### Objects tab (`ObjectsConsole.cs`)

- A list ("Objects") of root objects within a radius (default set in a numeric box "Search Radius"), filtered by words
  ("Search filter") and by rezzed / attached / both, sorted by distance or name; items `Name (12m) owned by Owner` or
  `Name attached to Chest`. Status: "Tracking N objects".
- **Linked parts:** selecting an object shows a second list of its child prims by name (also named "Objects", which a
  screen reader can't tell apart), and choosing one makes it the target of Touch, Pay, Buy and so on. So child prims
  (and HUD buttons, via attachments) *can* be touched, but only by prim name (often "Object" or "Primitive"), never at a
  face or position. [code]
- Buttons and context menu: Touch/Click, Sit On / Stand Up, Walk to / Stop, Turn to, Point at, Pay, Buy (shows price),
  Contents (list; Enter opens an item; "Copy to inventory"), Take, Delete, Return, Mute, 3D View, Export (if
  permitted), Detach (attachments), plus plugin actions. The details pane shows name, description, **hover text**,
  owner, creator, prim count and permissions (editable checkboxes if you may modify), not read out on its own.
- Several `//` commands depend on this tab's object list.

### Profiles (`GUI/Dialogs/Profile.cs`)

Tabs: Profile (name, born on, partner, info, groups list with titles, web URL), Interests (want-to / skills checkboxes
and languages), Picks (title, detail, parcel; teleport, show on map, new, delete; pick buttons named 2026-10-22),
First Life, Notes (private notes). Buttons: Start IM, Add Friend, Offer Teleport, Request Teleport, Pay, Mute/Unmute,
**Give Inventory** (gives what's on the inventory clipboard; or drop items on "Drop items from inventory here"). Your
own About text saves when you leave the field. Profile pictures are images with no description; setting one is hard
(a blind newcomer, accessibility.md).

### Search (`SearchConsole.cs`)

Tabs People, Places, Groups, Events (100 per page, Next/Previous, type and maturity filters; dates in PT). Results lists
with Profile, New IM, Teleport, Show on Map. Fields have accessible names ("Search event text containing", "Perform
Events Search"...). No classifieds or Destination Guide in Legacy (Veles has classifieds).

### Map (`MapConsole.cs`, `Controls/MapControl.cs`, `MiniMap.cs`)

A visual world map, plus the parts a blind user uses: a region-name search, X/Y/Z fields, Teleport, Go home, My
position, Refresh, teleport status, and "Find online friends on the grid" (RAD-165). The map itself and the mini map
are pictures.

### Region and parcel (`RegionInfo.cs`, Ctrl+Shift+1)

Region name, type, owner, CPU class, data centre, stats (time dilation, FPS, agents, prims, scripts, timings), current
parcel (name, description, area, owner, group, traffic, prim counts, auto-return), Restart for estate managers. No
About Land with access/ban lists, buy/sell, or media settings; no estate tools beyond eject/ban from the people list.

### Media and sound (`MediaConsole.cs`, `Radegast.Core/Media/*`)

FMOD audio: master, in-world sounds, object sounds, UI sounds and parcel music volumes (named sliders since 2010-14;
the guide still lists unlabelled ones), Mute All, audio device and profiles, parcel stream URL, station, song title,
play/stop, auto-start, keep current stream. Object sounds play placed in 3D (FMOD, `BufferSound.cs`), which is
the only spatial audio Legacy has besides the Speech plugin's. **No web or video media**:
media on prims and parcel video aren't played.

### Voice (`VoiceConsole.cs`)

LibreMetaverse's WebRTC voice: enable, auto-connect, push-to-talk with a chosen key (held
anywhere except while typing) or open mic, talk/listen devices, mute, levels, participant mute and mute/unmute all,
reconnect, status. Named controls since 2020. No positional cues about who's speaking beyond voice itself.

### Appearance and the self

Set Appearance, Rebake Textures (Tools); My Attachments (`AttachmentTab.cs`: lists attachments of you or anyone, with
touch); Animations (`AnimTab.cs`, `AnimDetail.cs`: what an avatar is playing; save or give an animation if permitted);
Textures (`OutfitTextures.cs`: an avatar's baked textures as pictures); Stop All Animations; hover height; display name.
No shape/skin/clothing editing.

### Notecards, scripts, gestures, landmarks, images (`GUI/Consoles/Assets/*`)

- **Notecards** (`Notecard.cs`): open, edit and save (inventory or an object's contents), save to disk, embedded
  items listed (Attachments), Ctrl+S. Status like "Notecard status: saved" goes out as an invisible notice.
- **Scripts** (`ScriptEditor.cs`): a real LSL editor with syntax highlighting (optional), Mono and Running checkboxes,
  save-and-compile in inventory or in an object, compile errors listed with line/column (and the cursor moved to the
  first), find/replace, save to and open from disk, undo/redo, Ctrl+S save, Ctrl+F find, **Ctrl+L speaks "Cursor at
  line N, column M"** and compile errors are also sent as invisible notices (both spoken only by the Speech plugin).
  Tab inserts four spaces, so Tab can't leave the editor (use the menu or Ctrl+Tab). "Accessibility improvements for
  script editor" date to 2010-03. So Legacy can edit scripts in-viewer; whether it's comfortable with NVDA is unknown.
- **Gestures**: play, status, reupload. **Landmarks**: teleport, show on map. **Images**: a picture viewer (no
  description).

### Building, upload, import/export

- **Upload image** (`ImageUploadConsole.cs`, L$ cost), **Upload mesh** (`MeshUploadConsole.cs`), **Import object**
  (`ImportConsole.cs`: rez an object from an exported XML file at chosen X/Y/Z, uploading its textures), **Export**
  (`ExportConsole.cs` XML, `ExportCollada.cs` DAE) of objects you may export.
- **SimpleBuilder plugin** ("Build Prims", `plugins/Radegast.Plugin.SimpleBuilder`): rez a box, sphere, torus, tube,
  prism or cylinder at an initial size and distance, then set position, rotation and scale numerically, rename, save.
  That's the whole of building: no linking, no textures, no prim parameters (cut, hollow, twist), no edit of existing
  objects beyond what you rezzed with it.
- **3D Scene window** (`GUI/Rendering/SceneWindow*.cs`, the "3D Scene" toolbar button): an OpenGL view with keyboard
  movement while it has focus: arrows walk/turn, Shift+Left/Right strafe, PageUp/PageDown up/down (jump, crouch,
  fly), Home toggles fly, Escape resets the camera, Alt+arrows orbit/zoom, Ctrl+arrows pan (2011). This is the only
  place arrow keys walk. `PrimWorkshop.cs` shows a single object in 3D.

### Preferences (`GUI/Dialogs/Settings.cs`) and every setting that matters to a blind user

General: confirm exit, minimize to tray, hide login splash, theme compatibility mode (plain colours), display L$
transactions in chat / as a dialog, friends online notifications (show, highlight), auto-reconnect and delay, log to
file, RLV support (and output RLV commands), disable the look-at beacon, show script errors, resolve URIs (and as plain
text, with a timeout), LSL syntax highlighting, highlight tabs on chat / IM / group IM, taskbar flash. Chat: timestamps
in chat and IM, avatar name links, display name mode (display name only / both / smart / off), MU* emotes, no typing
animation, chat log location or none, conference chat handling, sounds on mention and on group IM, fonts and colours
per message type. Auto response (text; always / non-friends / busy). **On script permission questions: ask, Auto
Accept or Auto Decline** (`on_script_question`). Inventory offers auto-accept mode. Automation: AutoSit (sit on a
saved object after login and teleports, retrying), PseudoHome (go back to a saved spot if moved more than a
tolerance), LSL Helper (let objects owned by listed UUIDs drive the client by IM: group invite, send IM, give
inventory, say). Graphics settings (only for the 3D window). Speech plugin settings live in its Plugins menu entry (on
or off, per-area switches for inventory/objects/friends, rate slow/medium/fast, voice assignment). Full key list from
the code: `AutoSit AvatarHoverOffsetZ LSLHelper PseudoHome agent_language_public allow_conference_chats_from_friends
audio_notify_device_changes auto_reconnect auto_response_text auto_response_type av_name_link chat_fonts chat_log_dir
chat_timestamps confirm_exit disable_chat_im_log disable_look_at display_name_mode friends_notification_highlight
group_im_sound hide_login_graphics highlight_on_chat highlight_on_group_chat highlight_on_im ignore_conference_chats
im_ding_all inv_auto_accept_mode inv_sort_bydate inv_sort_sysfirst log_ignored_conferences_to_chat log_to_file
mention_me_sound minimize_to_tray mu_emotes no_typing_anim object_console_filter on_script_question reconnect_time
remember_login resolve_uris resolve_uris_as_plaintext rlv_enabled script_syntax_highlight show_friends_online_notifications
show_script_errors taskbar_highlight theme_compatibility_mode transaction_notification_chat
transaction_notification_dialog` (plus rendering ones).

### Other pieces

Mute list (residents and objects by name), Pay dialog (amount, fast-pay L$1/5/10/20, via object), avatar picker,
**master controls** (`MasterTab.cs`, from the people list: follows another avatar's point-at effect, so whatever a
friend points at in their own viewer becomes "Master Selected" and you can Touch, Sit, Pay, Point at or inspect it; a
sighted helper pointing for a blind user, in effect, though nothing says it was meant that way),
RLV (`Radegast.Core/RLV/*`: restrictions, redirections, auto-accept TP/permissions from RLV), log viewer, debug
console, tray icon, multiple accounts (New window), command-line options, C# script plugins.

### Plugins shipped with Legacy (built into the app folder, so in the installer)

| Plugin | What it does |
|---|---|
| **Speech** ("RadSpeech", off by default, Plugins > Speech) | Its own TTS voice for chat, IMs, notices, lists, menus; earcons; 3D placement; speech recognition. Detail in accessibility.md part 1. Also: spoken **modes** that follow the selected tab (chat, inventory "closet", friends, voice, surroundings, each IM), voice commands ("talk", "friends", "who is online", "open the closet", "read it", "go there", "describe it", "what can I sit on", "walk", "turn left/right", "stop", "skip"), a **"talk to Max" mode that relays speech to channel 2** for Max the Virtual Guide Dog (Virtual Helping Hands' scripted companion); "thank you max" ends it. Spoken context menus ("Profile menu", "Doing Pay", "Menu closed"). |
| **SimpleBuilder** ("Build Prims") | Rez and place basic prims numerically (above). |
| **ALICE Chatbot** | An AIML bot that answers chat/IMs for you. |
| **IRC Relay** | Relays group chat to IRC. |

Not shipped: Demo, DemoTab, EVOVend (a vendor delivery bot).

## How a blind person actually uses it

### The official guide [doc]

"Getting Started With Radegast: A User Guide for the Blind and Visually Impaired", by volunteers at
Virtual Ability, written from May 2013 and adopted as Radegast's help; https://radegast.life/help-guide/ (the Help
menu's /documentation/help redirects there). One page; unchanged since at least 2021. Nothing else on radegast.life
is about blind use. What it teaches, as workflows:

- **Getting in:** register via Virtual Ability (land on Virtual Ability Island; mentors by email). Tick remember,
  Space/Enter on Log In; "You will know when you are logged in because the cursor will automatically be placed in the
  edit field of the chat window." It claims "By default Radegast is completely accessible on its own ... no issues
  have been reported regarding lack of accessibility using NVDA or JAWS." Add-ons unsupported.
- **Moving around the UI:** Alt+1-9 for windows (Alt+1 always returns to the chat edit field), Alt+F / Alt+W menus,
  Tab/Shift+Tab within a window, Ctrl+Tab between windows, arrows in lists, Ctrl+W closes (IMs), Alt+F4 closes a
  profile window. Tab order from chat input: once to history, twice to the nearby list.
- **Chat:** type, Enter. Whisper/shout by Tabbing twice to the combo, arrowing, Shift+Tab to Say (the guide doesn't
  teach Shift+Enter / Ctrl+Enter, though the code has them). Read new chat by Tabbing to the history. Turn off
  timestamps and prefixes in Preferences: tiring to hear.
- **Finding and moving:** `//status`; `//go 2`, `//go 2 N`; `//go xyz 128,128,22` (it explains x west→east 0-256, y
  south→north, z height, water ~20); `//go tp person/object <name>`; `//find object <words>` (any part of the name);
  `//sit <name>` / `//sit ground` / `//unsit`; `//follow <name>` / `//follow stop` (ask first); `//face ne|w|<deg>|person
  <name>`, 0° = east. The nearby list (your name first, then nearest-first, live) and its Applications-key menu: Walk
  To ("nose to nose"), Face, Offer/Request Teleport, Point At (undo with Unpoint, or it's rude), **Master Control**,
  which "lets a sighted avatar click items for you" (you get the object's name and buttons; "many screen readers will
  present these buttons as blank").
- **Objects (Alt+7), "your main source for learning what is around you":** Shift+Tab from the search box to the list
  ("Pink Sofa (2 m)"; unnamed things are "object"); menu Click/Touch, Show Contents, Sit On/Stand, Turn To, Walk To,
  Take, Delete, Return, Mute, Buy. Tab once from an object to its parts list ("most objects in this list might not be
  named"). Search only works once the list has loaded; radius adjustable.
- **Friends, IMs, profiles:** arrow to a friend, Tab to IM and Space; in the IM window Shift+Tab to its history.
  Profile tabs; edit your own from your name in the nearby list.
- **Groups:** Info/Activate/IM; the Info window's tabs by left/right arrows; notices read by Tab, attachments saved.
  Gap it admits: "you need to leave this window open if you are chatting with the group to read incoming messages";
  members can't be contacted from the member list.
- **Inventory and clothes:** right arrow opens folders; folder menu New Folder / New Note (Ctrl+S) / New Script /
  Refresh / Backup; search with All / Worn / Recent filters. **Worn is "one way to make sure you are fully dressed"**;
  the current outfit folder "may or may not be reliable"; "Replace outfit" may strip hair and accessories. Boxes:
  guess from names; "wear the item and ask someone if you are wearing a box". Unboxing: find it under Recent, go to a
  sandbox (Search > Places "sandbox"), Rez inworld, Alt+7, Show Contents, Open, confirm in chat history. Sorting by
  date "may not be accessible with a screen reader".
- **Giving:** Copy the item, open the person's profile, Give Inventory; acceptance shows in chat history. Putting
  something in an object: copy, Alt+7, Show Contents, Tab, Paste; finding the right object is "a challenge".
- **Search (Alt+5):** People needs the exact username; Places by traffic (Tab twice for description, again for
  Teleport); Groups (beware "free money" scams); Events: the Teleport button is "approximately eleven tabs away" from
  the results. Turn the screen reader down so in-world sound comes through.
- **Places:** Alt+W > Create Landmark Here. Map: Tab to search, type a region. SLurls: copy, **Ctrl+G** (focus lands on
  Teleport), Space.
- **Sound and voice:** Alt+8 volumes and music; Alt+9 voice: enable, devices, **two unlabelled sliders** (first mic,
  set ≤40%; second incoming), mute checkboxes (mic muted by default), mute/unmute all.
- **Not covered at all:** script dialogs and menus, reading notecards (only writing them), buying, dancing, gestures,
  accepting teleport offers, region crossings, vehicles, RLV, being alerted to IMs.

### A blind resident's account [FH]

A blind resident who uses Radegast with a screen reader and touch-types:

- **Her wishlist for developers:** view and edit notecards without losing versions; **touch child prims** (a treasure
  chest's lid she couldn't click without sighted help); **cross region borders while riding** without being thrown
  off; **multi-select in inventory** to cut, copy or move; a second inventory window to move items between;
  **arrow-key control of vehicles**.
- **How she gets around:** chains of `//go xyz x,y` waypoints kept in notecards; SLurl → Ctrl+G → Teleport; landing
  points are the main barrier; rooms full of things named "object" confuse; muting an object only takes effect after
  unticking and re-ticking "enable in-world sounds", and pop-ups undo it; an object that doesn't work in Radegast needs
  someone sighted to read its choices; games run by dialog buttons are playable.
- **Workarounds sighted helpers make for blind residents:** outfit labels the Worn filter can find, tidied
  inventories, scripted tools for the time and who's nearby.

### Other first-hand accounts [FH]

- **AppleVis thread** (2017-20, https://www.applevis.com/forum/text-based-viewer-opens-virtual-worlds-blind): a
  Radegast mentor (balls, skating, carriage rides, fishing, Gold Hunt; blind users "can not drive a car"); 2020: "the
  arrow keys are not showing up in Radegast. They are there but screen readers aren't seeing them" (the movement
  buttons' "▲ ▼ ◄ ►" text, likely); a 2014 attempt: "all I could do was teleport around and chat"; the Mac build
  broken; new users feeling lost; Virtual Ability emails unanswered.
- **audiogames.net** (search snippets only): 2013, a totally blind JAWS/NVDA user skates, dances, swims, races horses,
  but NVDA "cannot read incoming messages automatically ... Users must tab once to view chat history"; 2017, the
  Objects list includes all the scenery, so it's hard to tell what matters; HUDs were a problem.
- **The old radegast.life tracker's issue titles** (2020, Wayback index only): "a toggle button to make a screen reader
  read chat automatically"; "make IMs ding when a new message comes in automatically" (Ding on every IM exists now);
  "stop chat history from changing focus to the bottom line every time something new is posted in chat"; "add a
  select all option to the menu in inventory".
- More first-hand accounts: accessibility.md part 1.

## What screen-reader users reported

Read in full: cinderblocks/radegast (107 issues, 55 PRs, 33 discussions), radegastdev/radegast (74 issues, 2012-17),
and 240 of the ~509 issues of the old JIRA (jira.openmetaverse.org RAD-n, via the Wayback Machine).

**Who.** Most current reports come from a few screen-reader users (JAWS, sometimes its virtual cursor) who test
builds themselves. The recurring theme is calling in sighted help for what the viewer can't do for them: their group
tag (#141), rezzing (#137), land settings (#136), whether they look right after logging on or teleporting (#92),
unboxing (#173). In 2009-13, blind users filed about a dozen JIRA issues and **Mojito Sorbet** others (below).

**Current issues** [FH] / maintainer response [dev]:

| # | Problem | Outcome |
|---|---|---|
| #132 | After Add Friend the button still said "Add friend": no audible confirmation | Fixed 2.50 (PR #140: notices in chat and IM, button becomes "Sent Request") |
| #133 | JAWS reads raw `secondlife:///app/agent/<uuid>/about` links | Fixed 2.50: "Resolve URIs as plain text" (same problem as 2012's name links) |
| #136 | Can't reach land/parcel settings; a sighted person must log in | **Open**, no response |
| #137/#145 | Inventory context menu crashed (an RLV null) 2.49-2.51: no rezzing, unpacking, outfits | Fixed 2.51 |
| #141 | Can't tell which group tag is active | Fixed 2026-10-22, unreleased: "I can't test with a screen reader here" |
| #142 | "Ding on every IM" not saved | Fixed 2.54 |
| #146/#160 | Alt+8 audio page dead; no sounds | Fixed 2.52 |
| #152 | Settings window modal | Non-modal in 2.54 |
| #156 | AutoSit: several targets and a hotkey ("perfect ... if you are going into large sims") | **Open** |
| #158 | Applications key / Insert+F10 failed on inventory search results | Fixed 2.52 |
| #168 | Rezzed objects float; no way to lower them by screen reader | Closed: "The viewer itself doesn't know where the ground is" (it can: heightmap, raycast) |
| #176 | Active gestures lost on restart | Fixed in LibreMetaverse 3.2.1, unreleased |
| #181 | JAWS virtual cursor loops back to the top on her own Picks tab; can't reach Delete | **Open** (looks like a focus loop) |
| #189/#194 | Profiles blank in 2.54 | Hotfixed |
| #196 | Ctrl+Tab into an IM tab says only "edit", not who it's with | Fixed 2026-10-22 ("Message to <name>"), unreleased |
| #82 | Typing when a new group chat opens: input is selected and the next key overwrites it ("biggest issue ... for years") | Fix unreleased, unconfirmed; same bug as RAD-23 (2009) and RAD-348 (2012) |
| #92 | Outfit items added and removed after teleports | Closed unconfirmed ("about 80% of AISv3 is undocumented") |
| #173 | Unpacking makes empty folders | Delay heuristic, 2.54 |

**The old JIRA** [FH where filed by blind users]: a blind user asked for Ctrl+W (RAD-81), creating and editing notecards ("I
must email the text to someone and have them create the card for me", RAD-83), whisper/shout keys (RAD-88), mute
(RAD-85), conferences (RAD-124/125), the L$ balance ("not able to ... find my Linden balance using either the speech
plugin or my screen reader", RAD-113, became `//status`), and the Speech plugin reading llInstantMessage and llLoadURL
(RAD-80, RAD-110). Mojito: context menus must hold every action because "the ContextMenu ... is what the speech
interface looks at" (RAD-28); the Menu key on Objects (RAD-45); cut/copy/paste in inventory because "People who can not
use a mouse ... can not do the drag operation" (RAD-78); new IM tabs stealing focus (RAD-23). **RAD-147 "Keyboard
commands to improve accessibility"** (2010) is the wishlist behind the `//` set: go/face/follow/sit/unsit/tp/find
(built), lists that say which way things face, profile editing and **building commands**, and **"SOUND SUPPORT!" for
ambient sound** (not built). RAD-384 (2013, a blind user): drag targets need "a field to type in the coordinates"
(answer: rezzed objects land 2 m in front).

**Patterns:**
1. Most failures are **regressions** in basic plumbing each release (context menus, sounds, profiles, gestures, saved
   settings), found first by JAWS users ("this worked in 2.48"). Nobody tests with a screen reader: the maintainer has
   none and relies on them; fixes come in batches (2026-07-12; six issues on 2026-10-22, none released yet; the
   newest release is Legacy 2.54 of 2026-07-14).
2. Screen-reader-specific: unnamed controls (#196, #141), raw link text (#133), **focus stolen when tabs or
   notifications appear (#82/RAD-23/RAD-348, unsolved for 17 years)**, tab-order loops (#181), mouse-only actions
   (#158, drag in RAD-78/384).
3. **What sends blind users to a sighted helper:** their own appearance and outfit state (#92), placing and lowering
   rezzed objects (#168, RAD-384), parcel settings (#136), their group tag (#141), unpacking (#137, #173), and in
   blind residents' posts child prims, survey objects and notecard menus.
4. A user (#205-207) proposed a local automation API so an AI assistant could
   drive Radegast by voice; the maintainer pointed to Veles's Ollama and
   Automation plugins. Close to Sojourn's own idea.
5. Accessibility is the maintainer's stated reason for forking in 2017 (radegastdev #56) and the gate on Veles (#127,
   below).

## Veles, the Avalonia rewrite (`RadegastVeles/`)

- **State:** begun 2026-04-03, 147 commits, .NET 10 + Avalonia 12, Windows/macOS/Linux, Vulkan 3D. "Pre-alpha" (#204,
  2026-10-17), no release on any platform (#203); the 2.54 post promised "a beta soon", "with a text-first focus on
  accessibility and automation (bots) as first-class features". The gate, Cinder Roxley in discussion #127
  (2025-10-13): "I am in no rush to complete it until Avalonia has accessibility support on par with WinForms." [dev]
- **What changes for accessibility** [code]: ~1,400 `AutomationProperties.Name`s; live regions (polite on the chat log,
  nearby list, IMs and status bar; assertive on script dialogs); nearby entries carry speaking / typing / away /
  sitting. The 2.54 "screen reader regression" fix (5c33a558) touched only Veles views. No docs mention screen
  readers; whether Avalonia raises the live-region events to NVDA/JAWS is unverified.
- **What it loses:** no `//` navigation commands (only `help` and plugin commands), no Alt+1-9 (Ctrl+Tab, Ctrl+Shift+H,
  Ctrl+Alt+D and the chat keys remain), and no Speech plugin (Legacy-only; Veles's **Piper TTS speaks your typed chat
  into voice**, for people who can't speak, not a reader for you).
- **What it adds as a viewer:** one process with several logins; classifieds in search; land, region and estate
  panels; land holdings; editing an object's position, rotation and scale numerically; a HUD viewer with Touch;
  uploads of images, mesh, sound and animations; materials; Marketplace; group notice archive; plugins (Automation
  with a rule engine, Macros, an Ollama chatbot, Discord/IRC/email relays, import/export).
- No accessibility issues, milestones or roadmap exist for it.

## History: TextSL, ScriptAble, Max, the people

- **TextSL** (Eelke Folmer, ASSETS 2009): a GPL command client (`move north 20`, `describe`, `where is the chair`,
  follow, sit, touch, mute, partial names, a 20 m summariser). A 2010 study found exploring by repeated queries
  "inherently slower". Dead by the time blind residents needed it.
- **Max the Virtual Guide Dog** (Virtual Helping Hands, ~2008): a scripted companion with environmental sounds and
  /2-style commands; Radegast's Speech plugin has a "talk to Max" mode that relays speech to channel 2.
- **ScriptAble, "the Guide"** (May 2009 - Apr 2010; first called BOSS, renamed Aug 2009): open LSL in
  an invisible sphere, bracelet or HUD. Channels split as blind people are
  taught: /2 orientation (`where`, `region`, `parcel`, `xyz`, `facing` in whole degrees with 0 = east,
  counter-clockwise, `doing`, `who [name]` → "<name> is Sitting 4 meters East (12 degrees)", `what`), /3
  mobility (`go north 5` and `go 5 north`, `tp <nickname>`, `follow`, `stop`), /4 other (`time`, `profile`, `help`).
  Guide 2.0 (Sep 2009) added RLV `face`, `sit`, `stand`, `tp @place` and clothing (`add`, `remove`, `replace`, `inv`).
  Planned, never built: `look`/`ping` for builder-tagged accessible spaces, because llSensor returns 16 things and
  "Object" names say nothing. Also a voice transcriber and early building tools. Wiki on wikispaces (gone).
- **What came into Radegast:** the whole `//` command set. It drew on the Guide. The commit history
  matches: go, face, tp, status, follow, sit, unsit, find in four days, 2010-04-23..26 (RAD-147..157). The commands'
  vocabulary (status doing/region/parcel/location/money/time, facing with 0 = east, go with distance and direction)
  is the Guide's. The Guide's `who` (everyone with state and bearing) and `what` became the nearby list and `//find`.
- **Mojito Sorbet** wrote the Speech plugin (Nov 2009) and Radegast's SL voice interface, having never used C# before
  (Latif, as quoted by Virtual Ability); she was in touch with Virtual Ability and told Latif Radegast was screen-reader friendly.
- **Latif Khalifa** founded Radegast (2009) and died in March 2016; **Cinder Roxley** (Sjofn LLC) has maintained it
  since, "especially in terms of accessibility" (modemworld.me, 2016-12-18). Douglas R. Miles added the accessible,
  pluggable context menus (2009).
