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
tab highlights, plugins, and its PseudoHome and LSL Helper (judged not worth building here, 2026-11-09: `docs/access-app.md` >
Auto-sit says why; a question for her in `docs/access.md`).

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

### 24. Small things
- Alt+3 is People and Alt+6 Places, against Radegast's Groups and Map; Alt+8 and Alt+9 are Notices and the assistant, against Media and
  Voice. Chosen, but "familiar where Radegast works" is only partly so, and there's no key to Groups. A question for her
  (`docs/access.md` > Questions waiting for her), not a change until she answers.

## The sighted-helper list, what isn't closed

- **Checking an outfit:** by names without the assistant, by picture with it. A box worn by mistake shows only by its name.
- **Rezzing and placing:** done, without `//place`, so not in a route.
- **Land settings:** parcels and the estate's tools and lists done (the estate's own switches are read, not changed).
- **Unboxing:** only from the inventory, and only where she can rez; the failure doesn't suggest a sandbox. A box already out: Contents, then `copy NAME` for each thing.
- **HUD games:** a HUD that's one picture of buttons is read by the assistant, so with no token only numbered faces remain. Nothing to do
  short of a local reader; the plan's stance (`docs/access.md` > Stance) says where "without an account" stops.
