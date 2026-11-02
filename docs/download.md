# Downloads, support and privacy

The disclosures that go with any build of Sojourn or Sojourn Access handed to anyone, as Linden Lab's Third Party
Viewer Policy asks of a viewer's distributor (`docs/tpv-policy.md`). Sojourn Access's own copy of this page, written for
screen-reader users, is in its repository: https://github.com/studio-quire/sojourn-access.

## Not Linden Lab's

This software is not provided or supported by Linden Lab, the makers of Second Life.

Sojourn and Sojourn Access are third-party viewers made by Claude models (Anthropic's AI) at Studio Quire. Using them
with Second Life means agreeing to Linden Lab's Terms of Service and Third Party Viewer Policy, as with any viewer.

## Builds

- **Sojourn** (the VR viewer): an installer for Windows 10 and 11 (x64), `SojournViewer-win-Setup.exe`, in the
  releases at https://github.com/studio-quire/sojourn/releases, for a PC VR headset (a Quest over Link or Air Link,
  or any headset through SteamVR). Or build it from this repository: ask your own Claude to follow BUILDING.md.
- **Sojourn Access**: early test builds for Windows 10 and 11 (x64), published as releases at
  https://github.com/studio-quire/sojourn-access/releases.

Each release names the commit of this repository it was built from. Builds are early: features are missing and things
will go wrong. Don't rely on them for anything that matters. They aren't code-signed, so Windows SmartScreen warns
before the first run (More info, then Run anyway).

**Installing Sojourn** happens only when you run its Setup. It installs for your Windows account alone, without
administrator rights, into `%LOCALAPPDATA%\SojournViewer`, with a Sojourn entry in the Start menu and in Windows'
installed apps. It never updates on its own: when the launcher opens it checks whether there's a newer release and, if
there is, offers it in its footer; nothing downloads or installs until you press it. Updates are signed by Studio
Quire and refused if the signature doesn't hold. To remove Sojourn, uninstall it from Settings > Apps. That removes the
program, its caches and logs, but keeps your saved logins, keys, settings, chat log and the assistant's notes in
`%LOCALAPPDATA%\Sojourn`, so a reinstall finds them; delete that folder to remove those too.

**Sojourn Access** isn't installed. It runs from the folder it's unzipped into, adds no Start menu entries or services,
changes no Windows settings, and never updates itself. To remove it, delete its folder and `%LOCALAPPDATA%\Sojourn
Access`.

Neither needs an account with Studio Quire, an email address or anything else from you to install or remove.

## What's unusual about it

- **An AI assistant can operate the viewer for you** when you ask it to (Sojourn, and Sojourn Access where the build
  includes it): moving, touching, wearing, building, searching, messaging people with what you asked to say. It acts
  only on your requests while you're there, never on its own. Spending L$, deleting, giving things away, changing
  permissions and creating scripts always wait for your own key press or button.
- Sojourn Access keeps the renderer running out of sight, so the assistant can look at the scene to describe it.
- **Your own Claude Code can be the assistant** instead (Sojourn's dev mode, DEV-MODE.md): it then operates the viewer
  for you through the same controls and confirmations.
- The assistant needs an AI provider you bring yourself: your own Anthropic or OpenRouter key. Without one, Sojourn is
  a viewer without the assistant.

## Support

There's no formal support. Builds are tests, and help comes through GitHub issues: for Sojourn at
https://github.com/studio-quire/sojourn/issues, for Sojourn Access at
https://github.com/studio-quire/sojourn-access/issues. Issues are read, but there's no promised response time.

## Privacy policy

Studio Quire makes Sojourn and Sojourn Access. This policy covers both apps and Studio Quire's relay, and describes
what the current code does (in effect from 2026-11-01).

Studio Quire collects nothing on its own. Neither app has analytics or tracking. The only things that reach Studio
Quire are ones you choose: requests through the studio relay, if the studio gave you a token for it (below); in
Sojourn Access a session's log, if you send one (Help > Send a log); and in Sojourn a problem report (menu > about >
report a problem, or by asking the assistant) or a crash report, which the launcher asks about after a crash unless
you set crash reports to always or never. Other people's words are left out and their names replaced before a log
or report leaves your computer; it goes to Studio Quire's relay (relay.studioquire.org) and is kept for 30 days. With
a Studio Quire token it goes as yours; without one, Sojourn's reports are taken anonymously, at most three a day from
one address, and your IP address isn't stored. Or you can save a report as a file and attach it to a GitHub issue
yourself.

**Without the assistant**, the apps connect only where any Second Life viewer does:
- Linden Lab's servers: login, the regions you visit, their asset and texture services, the map, search and voice chat.
  Your username and password go only to Linden Lab's login server. Like every viewer, both apps send the region the
  viewer statistics Linden Lab asks for: how long the app has run, its frame rate, memory, the regions visited, how
  many avatars are in view, and your computer's operating system, processor, memory and graphics card; not your
  network card's address or your disk's serial number, which the official viewer also sends, unless you turn that on.
- A parcel's music stream, if music is on: the stream's server (chosen by the parcel's owner) sees your IP address,
  as with any viewer.
- In Sojourn (not Sojourn Access), web pages and video shown on objects and parcels, from the sites they come from.
  Media other people set up waits for your touch before it plays.
- An installed Sojourn's update check: GitHub's API, once each time the launcher opens, asking for the list of
  Sojourn's releases (GitHub sees your IP address, as with any visit); and GitHub's downloads, when you press update.
- If you pick Whisper for speech recognition, its model is downloaded once from Hugging Face (about 460 MB). Parakeet,
  the default, comes with Sojourn.
- If you choose OpenRouter, the list of models it offers is fetched from OpenRouter's public list (no key sent).

Data stays on your computer: settings, chat and IM logs, caches, and in Sojourn your saved login (protected by Windows
for your user account; Sojourn Access doesn't save passwords). You delete it by deleting the data folder.

**With the assistant**, what it needs to answer each request is sent to the AI provider you chose in the launcher (in
Sojourn Access, Alt+0), under that provider's terms and privacy policy:
- your own Anthropic API key: to Anthropic, the maker of Claude;
- your own OpenRouter key: to OpenRouter and the model's provider it routes to. "No data collection" is on by default:
  OpenRouter then uses only providers that say they don't keep or train on prompts;
- the studio relay, for people Studio Quire covers: to Anthropic on Studio Quire's key, through Studio Quire's relay,
  which reads only how much each request cost (to keep to your monthly allowance) and stores nothing of what's said.

What's sent: your request (typed, or transcribed from your speech on your
own computer), what it looks up to answer (for example names of people and things around you, recent chat when your
request is about it, pictures of your view when it needs to see), and the notes it keeps about your preferences.
Its tools may search the web for it. Other people's chat goes to it only when your request is about that chat;
the assistant keeps no transcripts, and its notes hold what you tell it, not what others said. In incognito, nothing
from the spell is kept, and by default the assistant sees only a general picture.

Optional services you choose yourself: cloud speech recognition (Soniox, with your own key) sends your recorded voice
there while you hold the talk button; it's off unless you turn it on.

**In dev mode** (DEV-MODE.md), the assistant is your own Claude Code: what it reads from Sojourn to help you goes to
Anthropic under your own Claude account's terms, as anything else you ask it does. Sojourn sends nothing extra.

**How what we hold is kept.** Logs and reports sent to Studio Quire are stored by its relay on Cloudflare, readable
only with Studio Quire's own secret, never shared or sold, and deleted automatically after 30 days. The relay keeps no
IP addresses: the daily limit on anonymous reports counts a one-way hash of your address and the date, discarded the
next day. Cloudflare, which runs the relay, sees IP addresses as for any website it serves. On your computer, saved
passwords and AI keys are encrypted by Windows for your account.

Linden Lab may ask a viewer's makers to update or delete data received from Second Life. The only such data we hold is
in logs and reports people chose to send (kept 30 days, other people's words already left out); we delete one on
request.

**Questions and requests** go to the issues at https://github.com/studio-quire/sojourn/issues. Issues are public; to
ask privately that a report you sent be deleted, send another report (menu > about > report a problem) saying so, with
roughly when you sent the first.

If the code changes what's collected or sent, this policy changes with it, and its history is this repository's.
