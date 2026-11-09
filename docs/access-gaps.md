# Sojourn Access: what it lacks next to Radegast

Written by Claude Opus 5.5, 2026-11-07. Agent-facing. The plan (`docs/access.md`, M1) aims for everything Radegast
does, and the first tester's want is the same. On 2026-11-06 objects turned out to have no Pay or Buy and nothing could be
unblocked, so that claim had never been checked. This is the check: gaps and partial matches only, ranked. Take an entry
off when it's closed.

**How it was checked.** `docs/radegast.md` feature by feature, then the tasks blind residents hand to a sighted person, each
against what Access exposes in its code at `3d47ccf`: the menus in `MainForm.*.cs`, `ProcessCmdKey`, `Commands.cs`, the rows
in Alt+0's `Shown`. An op in Core or Session counted only for the size guess. Reading only: nothing built or run, no
screen reader, no grid. Its two bugs from reading were reproduced on the local grid when they were fixed: the notecards as read;
conferences differently, a line of one being taken for an IM from whoever spoke.
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

**Why so much is missing.** Core was built for the assistant, so most of this is ops with no key. Most sizes below are small for that reason. Two other causes are fixed: a person's actions are one list (`PersonActions`), and a setting can't fall off Alt+0 (`docs/access-app.md` > Areas).

Sizes as in viewer-gaps: S a day or less, M a few days, L a week or more, for one agent. Numbers stay as first ranked, so references hold when an entry goes.

## Ranked

### 5. Groups: a role's and the group's own settings
- **Radegast:** the Roles tab (a role made, its title and abilities), the General tab's settings (charter, open enrollment, fee, show in search), saving the member list.
- **Access:** running one is built (2026-11-08: members and who's in the chat with roles given and taken, eject, ban; a new notice with an attachment; attachments taken; inviting into a role; banned residents; New group; `docs/access-app.md` > Groups). None of these three. Ban, unban and the banned list follow the viewer's protocol and haven't run on Second Life (OpenSim has no group bans).
- **Where:** `MainForm.Groups.cs`, `MainForm.GroupPeople.cs`. `TesselGroups` has `createRole`, `updateRole`, `deleteRole`, `update`, `powers`.
- **Size:** S-M: a role's powers are a long list of the viewer's abilities to word.
- **Matters: low-medium:** nobody running a group from Access has asked yet (question in `docs/access.md`).

### 14. Voice: what's left
- **Radegast:** participant list, mute and unmute all, reconnect.
- **Access:** one person muted and their loudness (a person's menu, `//voice mute`, `unmute`, `volume`), Control+Shift+V saying who's muted. No list to act from, no all at once, no reconnect. Muting and loudness aren't run against a voice server yet (`ACCESS_VOICE=1`).
- **Where:** `MainForm.Voice.cs`.
- **Size:** S.
- **Matters: low-medium.**

### 15. Master Controls and Point at
- **Radegast:** a sighted friend points at something in their viewer and it becomes the selected thing, to touch, sit on or pay; the
  guide teaches it. Point at and Unpoint on people and objects.
- **Access:** neither. Parts and faces reduce the need, but "what my partner is pointing at" would name a thing among dozens called
  Object.
- **Where:** People nearby's and `MainForm.Lists.cs`'s menus; viewer effects in Core (not checked).
- **Size:** M.
- **Matters: medium.** Lets a helper help without the account.

### 18. One person's attachments, and animations
- **Radegast:** Attachments on a person and Animations (what they're playing).
- **Access:** Objects' Show lists what everyone nearby wears, touchable, so a pet or a tip jar is in reach; there's no way from a person's own menu to just theirs, and nothing on what they're playing.
- **Where:** People menu, `nearby` `worn_only` by whose it is.
- **Size:** S.
- **Matters: low.**

### 20. Estate from the keys
- **Radegast:** Eject from estate, Eject and Ban on a person; Restart for estate managers.
- **Access:** About Land passes Radegast for parcels (`eject`, `ban` by typed name), but nothing on the person's menu, and the estate is
  ops only. `SojournLand` has `estateAdd`, `kick`, `sendHome`, `restart`, `message`.
- **Where:** `MainForm.Land.cs`, `LandRows.cs`, the People menu.
- **Size:** M.
- **Matters: low** daily, high for the rare blind estate owner.

### 21. The session itself
- **Radegast:** New window for a second account, Clean Cache, Log Viewer.
- **Access:** World's Log out, Help's Newer build? Not these three.
- **Size:** S each.
- **Matters: low.**

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
- `//scriptinfo` takes no `parcel`. `//thread`, `//threads` and `//kill` are absent, so a route using them pauses there: none of our commands can be stopped midway yet, so `//kill` would need that first. Worth asking whether any route she has uses them.
- `//siminfo` lacks CPU class and data centre; `//parcelinfo` lacks ban and access list sizes and traffic.
- Alt+3 is People and Alt+6 Places, against Radegast's Groups and Map; Alt+8 and Alt+9 are Notices and the assistant, against Media and
  Voice. Chosen, but "familiar where Radegast works" is only partly so, and there's no key to Groups.

## The sighted-helper list, what isn't closed

- **Checking an outfit:** by names without the assistant, by picture with it. A box worn by mistake shows only by its name.
- **Rezzing and placing:** done, without `//place`, so not in a route.
- **Land settings:** parcels done; estate at item 20.
- **Unboxing:** only from the inventory, and only where she can rez; the failure doesn't suggest a sandbox. A box already out: Contents, then `copy NAME` for each thing.
- **HUD games:** a HUD that's one picture of buttons is read by the assistant, so with no token only numbered faces remain. Nothing to do
  short of a local reader, but the plan's "without an account" doesn't reach here and should say so.
