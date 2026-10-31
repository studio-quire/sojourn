# Downloads, support and privacy

The disclosures that go with any build of Sojourn or Sojourn Access handed to anyone, as Linden Lab's Third Party
Viewer Policy asks of a viewer's distributor (`docs/tpv-policy.md`). Sojourn Access's own copy of this page, written for
screen-reader users, is in its repository: https://github.com/studio-quire/sojourn-access.

## Not Linden Lab's

This software is not provided or supported by Linden Lab, the makers of Second Life.

Sojourn and Sojourn Access are third-party viewers made by Claude models (Anthropic's AI) at Studio Quire. Using them
with Second Life means agreeing to Linden Lab's Terms of Service and Third Party Viewer Policy, as with any viewer.

## Builds

- **Sojourn** (the VR viewer): no public builds yet. Build it from this repository: ask your own Claude to follow
  BUILDING.md, or see README.md.
- **Sojourn Access**: early test builds for Windows 10 and 11 (x64), published as releases at
  https://github.com/studio-quire/sojourn-access/releases. Each release names the commit of this repository it was
  built from.

Builds are early tests: features are missing and things will go wrong. Don't rely on them for anything that matters.

Nothing is installed. A build runs from the folder it's unzipped into, adds no Start menu entries or services, and
changes no Windows settings. It never updates itself. To remove it, delete its folder and its data folder
(`%LOCALAPPDATA%\Sojourn Access` for Sojourn Access).

## What's unusual about it

- **An AI assistant can operate the viewer for you** when you ask it to (Sojourn, and Sojourn Access where the build
  includes it): moving, touching, wearing, building, searching, messaging people with what you asked to say. It acts
  only on your requests while you're there, never on its own. Spending L$, deleting, giving things away, changing
  permissions and creating scripts always wait for your own key press or button.
- Sojourn Access keeps the renderer running out of sight, so the assistant can look at the scene to describe it.

## Support

There's no formal support. Builds are tests, and help comes through GitHub issues: for Sojourn at
https://github.com/studio-quire/sojourn/issues, for Sojourn Access at
https://github.com/studio-quire/sojourn-access/issues. Issues are read, but there's no promised response time.

## Privacy

Studio Quire collects nothing on its own. Neither app has analytics, tracking or crash reports. The only things that
reach Studio Quire are ones you choose: requests through the studio relay, if the studio gave you a token for it
(below), and in Sojourn Access a session's log, if you send one (Help > Send a log; other people's words are left out
before it leaves your computer).

**Without the assistant**, the apps connect only where any Second Life viewer does:
- Linden Lab's servers: login, the regions you visit, their asset and texture services, the map, search and voice chat.
  Your username and password go only to Linden Lab's login server.
- A parcel's music stream, if music is on: the stream's server (chosen by the parcel's owner) sees your IP address,
  as with any viewer.
- In Sojourn (not Sojourn Access), web pages and video shown on objects and parcels, from the sites they come from.
  Media other people set up waits for your touch before it plays.

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

Linden Lab may ask a viewer's makers to update or delete data received from Second Life. The only such data we hold is
in logs people chose to send from Sojourn Access (kept 30 days, other people's words already left out); we delete one
on request.

This policy describes the current code. If it changes, this page changes with it, in this repository's history.
