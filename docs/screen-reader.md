# The person's own screen reader (A1)

Written by Claude Opus 5.5, 2026-10-27. Agent-facing. What arrives in Sojourn is read out through the person's own
screen reader, and what waits for them (a confirmation, a notice) is answered by keys. Batch A1 of
`docs/accessibility.md`, shaped by a blind resident's request there: incoming text spoken automatically through her
screen reader's API, NVDA first, not a built-in voice; the stream's song title too.

## What's read out, and how

- **Through the reader's API, never a voice of ours.** NVDA by its controller client (`nvdaController_speakText`,
  `cancelSpeech`, `brailleMessage`, and `speakSsml` at priority "now" for urgent things where NVDA is 2024.1+);
  anything else (Narrator, JAWS, ZoomText, any reader that sets Windows' screen-reader flag) by UI Automation
  notifications from the window (`UiaRaiseNotificationEvent`), which those readers speak in their own voice and
  braille. NVDA is told through UIA too if its DLL is missing, which it reads less reliably. The person's rate,
  voice, verbosity of punctuation and braille stay theirs. Nothing is spoken on the lock screen or a secure desktop
  (NV Access's advice).
- **When**: the `screen_reader` setting, `auto` by default: on while a screen reader runs (checked every 3 s: NVDA's
  `testIfRunning` or its process, `jfw`, `Narrator`, `zt`, `SPI_GETSCREENREADER`), off otherwise; `on`; `off`. Why
  auto rather than off: a blind person can't find a switch they can't see, and starting Sojourn should already talk to
  them; someone without a reader gets nothing new (no keys change either). A program that leaves Windows' screen-reader flag set by
  mistake would turn it on for a sighted person (Enter would then confirm a waiting confirmation too); `screen_reader
  off` settles it. On becoming active it says "Sojourn. F1 for
  keys."
- **What** (each its own switch, all on): nearby chat (`Kestrel: hello`, `Kestrel waves`, `Kestrel whispers: ...`,
  `Kestrel shouts: ...`), objects (`Greeter Board: Welcome`, llOwnerSay and object IMs too; the same line again within
  10 s only once), IMs (`Kestrel, IM: ...`), group chat (`Kestrel, Builders: ...`), notices (the feed's own words:
  `Kestrel offers a teleport “come over”`, `Notices Harp: choose a piece.`, friends online, alerts; the first one with
  keys in a session ends "Control N to answer."), the assistant (its replies, choices numbered as the 1-9 keys pick
  them, its questions to point or to check a word, notes like "not sent"), music titles (`Now playing: ...` when the
  parcel's stream starts or its title changes). Who said it first, then how or where, then the words. The person's
  own lines aren't read. Links read as where they go ("a link to example.com", "a place, Bay City"); text past ~700
  characters is cut, saying so.
- **Order and interruption** (the plan's findings: speech brief, interruptible, under the person's control): what
  arrives goes into the reader's own queue in the order it came, never cutting the line before it off; the reader's
  own stop key (Ctrl) silences it as everywhere. Answers to the person's keys cut in, as a screen reader does on a key.
  A confirmation cuts in at once ("Pay L$50 to Mira Lane. Your balance after: L$1,250. Enter confirms, Escape
  cancels."), and on NVDA 2024.1+ the interrupted line resumes after it. One that names only the start of a list
  ("... and 40 more") adds "Control R reads the whole list."; Ctrl+R then says it with every name, whole, however long.
- **Held behind a confirmation**: for its first 10 s nothing others or the assistant wrote is read (the panel's rule:
  nothing caller-written beside a confirmation); it follows, in order, once it's answered. One left waiting longer
  stops holding (`AccessWiring.HoldFor`), so a missed confirmation can't silence everything: what waited is read and
  "A confirmation is still waiting for your answer." once (Access's test: "a confirmation left waiting").
- **Follows the feed**: lines come from the chat feed (`Social/SocialFeed.cs`), so mutes, blocks, the viewer's
  filtering and Do Not Disturb apply as they do on screen; the feed's chat switch (what's drawn) doesn't affect them.
- Dictated drafts are read back ("Draft to nearby: ... Enter sends, Escape cancels."), incognito turning on and off
  and the assistant being let see are said.
- **One voice**: while the reader reads the assistant's replies, the assistant's own spoken replies (Kokoro) keep
  quiet (`AssistantHost.ReaderSpeaksReplies`).

## Keys (the VR client's window: desktop, and the headset's mirror window)

Only while it reads out; otherwise every key is as it was.

| | |
|---|---|
| Enter / Escape | confirm / cancel what the assistant asks you to confirm. Confirming takes a fresh press of a hardware key (not a program's posted or injected key nor one the system made, `Private/RealKeys.cs`; a held Enter from sending a request doesn't count, nor a key already held when the window came to the front) once the confirmation has armed (0.8 s, as the mouse). While typing to the assistant or into a page, Enter is still the typing's |
| Ctrl+N, Ctrl+Shift+N | the waiting notices, newest first (the one just heard), then older / back: read whole, "1 of 2. Notices Harp: choose a piece. 1, More >. 2, Stop. ... Delete puts it away." An alert whose only key is OK has no number keys, only Delete |
| 1-9, 0, minus, equals | on a picked notice (two minutes after the last key on it): press its buttons in the form's order. Real keys only. A script menu's next page (More >) is picked and read by itself |
| Delete / Escape | on a picked notice: put it away unanswered (as the feed's ×) / done with notices (the number keys go back to the panel's choices) |
| Ctrl+R | say it again: the confirmation (with every name in a list it cut short), the picked notice, or the newest line |
| Ctrl+[ / Ctrl+] | back / forward through what was read (the last 200 lines, this session, in memory) |
| F1 | the keys |

Notices answered here are the person's own, as their press on the feed: not gated (the gate guards the assistant's
answers). Sojourn Access binds its own keyboard model over the same actions (`docs/access-app.md` > Keys).

## Incognito

As `docs/assistant.md` > Incognito has it: what's read is what the screen shows the person (the feed shows everything
during a spell), and nothing is kept. Announcements are never logged (not the console, the session log or any file);
the review history keeps a spell's lines only while it lasts and drops them when it ends. In the headset the PC's
reader stays silent for the whole spell, as the PC's monitor blanks (it speaks from the PC, where others may hear);
on the desktop the window is the view and stays, so it reads. Not covered: what the screen reader itself keeps (NVDA's
log at its "input/output" level, add-ons such as speech history), which is the person's.

## How it fits

- Shared, in `Sojourn.Core/Access/` (both apps): `ReaderVoices.cs` (NvdaVoice, UiaVoice: a provider for the app's
  window, the window procedure subclassed so WM_GETOBJECT finds it, named "Sojourn" rather than the changing title;
  ScreenReaders: which runs, whether the desktop is open), `Announcer.cs` (the settings, the order, holding, quiet,
  the history, Speakable), `ReaderActions.cs` (confirmations through an `IConfirmGate`, stepping, pressing and putting
  away notices, repeating; no key bindings).
- The session's part, `Sojourn.Session/Access/AccessWiring.cs` (shared with Sojourn Access: watches the feed, the panel
  and the music stream ten times a second on a thread of its own, the menu's screen reader section, the settings,
  `SojournScreenReader status`), and this client's keys, `Sojourn.Client/Access/ReaderKeys.cs` (the bindings above).
  Program.cs starts it through `SessionHost.StartScreenReader` and passes keys.
- NVDA's controller client: `python tools/screenreader.py setup` puts NV Access's x64 DLL (2026.3, LGPL-2.1, pinned
  by SHA-256) with its licence in `local/nvda/`, where the client looks after its own folder. It isn't in the repo. A
  release ships it beside Sojourn.exe with its licence (LGPL-2.1 allows that, unmodified, dynamically loaded).
- Settings: `screen_reader` (auto, on, off), `announce_nearby`, `_objects`, `_ims`, `_group`, `_notices`,
  `_replies`, `_music` (SojournSettings, the assistant's settings tool), the menu's **screen reader** section (its first
  row notes which reader runs). Kept in vr-settings.json as `screenReader`.

## Testing

`python tools/screenreader.py test` (local grid, desktop, ~4 minutes, one test client): a client with read-out on and
`tools/uia_listen.ps1`, a UI Automation client listening for notification events as Narrator does, checking each line
heard. It doesn't start NVDA (it would speak aloud and hook the keyboard), so the NVDA path is checked only as far as
loading the DLL and its "not running"; NVDA itself has been heard only in people's runs of Sojourn Access
(`docs/access.md`). Its 45 checks (passed 2026-10-27): every kind's wording and order, links, a repeated object line once, the confirmation
cutting in with lines held behind it, Escape and Enter, Ctrl+N newest first and back, a menu's two pages by number keys,
Delete, Ctrl+R, Ctrl+[, F1, replies and choices, two song titles, incognito (its line gone from the history after),
off, a kind switched off.

## What only a screen reader user can judge

Whether automatic reading of everything is right or too much (in a busy place lines queue up; there's no rate limit
or summarising yet), the wording and order of each kind (who first, "IM", the group's name), whether the confirmation
and notice keys are the right ones and easy to remember, whether braille matters and NVDA's flash messages suit it,
whether Narrator and JAWS speak UIA notifications from Sojourn's window as NVDA's API does (and whether JAWS users
need its own API), whether resuming after a confirmation helps, and the review keys versus arrowing through a
history window (A2 / Sojourn Access).
