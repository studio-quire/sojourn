Written by Claude Opus 5.5, 2026-10-20. Agent-facing. Checked 2026-10-20 against:
- https://secondlife.com/corporate/third-party-viewers (the TPV Policy; undated, page footer 2022)
- https://wiki.secondlife.com/wiki/Linden_Lab_Official:Scripted_Agent_Policy (last edited 2023-04-03; "Bot policy" and "Inworld policy on bots" redirect here)
- https://wiki.secondlife.com/wiki/Channel_and_Version_Requirements (last edited 2013)
- https://wiki.secondlife.com/wiki/AGENT_AUTOMATED, https://wiki.secondlife.com/wiki/Third_Party_Viewer_Directory
- https://www.lindenlab.com/tos (Terms of Service, "Effective: July 31, 2017"), https://www.lindenlab.com/legal/second-life-terms-and-conditions (SL Policy), https://www.lindenlab.com/legal/community-standards
- Official viewer source (secondlife/viewer, develop): `lllogininstance.cpp`, `lltranslate.cpp`; LibreMetaverse master `LibreMetaverse/Login.cs` (we use the 3.2.0 package; assumed close)

# Linden Lab's TPV policy and what it means for Sojourn

Anything that logs in to SL's servers counts as a Third-Party Viewer, chat clients, bots and utilities included. Using one means agreeing to the TPV Policy on top of the ToS. Breaking it ends permission to connect "automatically" (8.c), and LL can block the viewer or suspend accounts (8.e). The policy splits its rules by role:
- **All Developers / all TPVs**: 1.a-b (protocol, unique identifier), all of section 2 (prohibited features), 3 (IP), 4.a (data), 7 (responsibility).
- **Only "a Developer who distributes Third-Party Viewers to others"**: 1.c-g (disclosures, install/uninstall, ToS presentation, version display), 4.b (privacy policy), 5 (branding).
- **Viewer Directory** (section 6): voluntary, "not a requirement for connecting to Second Life".

Until 2026-10-29 Sojourn was for the user's own use on their own account, and we read ourselves as developers who don't distribute. Since then the source is public (github.com/studio-quire/sojourn), Sojourn Access builds have gone to a tester (2026-10-30), and a release of both apps is planned, so we treat the distributor rules as ours (the user agreed, 2026-10-31: Sojourn was always meant to go public), with the LGPL source obligations (below). What's done and left: "If it's ever distributed", below.

## At login

1. **Present the ToS ourselves; don't auto-accept.** LibreMetaverse's `LoginParams` defaults `AgreeToTos` and `ReadCritical` to true, silently accepting on the person's behalf. Like the official viewer (`lllogininstance.cpp`), Sojourn sends both false. A `tos`/`critical` refusal raises `AgentSession.TermsRequested`, and the person answers: on the login sheet (headset or window), at the launcher (`docs/launcher.md` > The grid's questions at the desk), in Access's login form, or in the console (`/accept`, `/decline`). Only then does login retry with that flag set, for that one login. MFA codes go the same way. The agent API sees only a `terms required` event and the status; it has no way to answer. Basis: policy 1.f (formally in the distributor block, but the person whose account it is should see the terms). Exercised so far only by tests: OpenSim doesn't ask.
2. **Channel and version**: below.
3. **Credentials never reach the assistant.** 2.e: usernames and passwords must not go "anywhere except to Linden Lab servers", and may only be kept on the user's own system. A Claude session runs on Anthropic's servers, so the password, MFA token and `mfa_hash` must never enter its context, tool results, logs it reads, or anything sent to the API. Login stays outside the agent API. The bridge binds to 127.0.0.1 (`BridgeServer.cs`, `Config.Bind`); keep it that way.
4. **Don't flag the account as a scripted agent.** It's a human account (see "Bots" below).
5. Arguable whether required: the viewer-stats report (see "Ported viewer code").

## Channel and version

- The two are separate login fields, `channel` and `version`. The official viewer sends `channel` = "Second Life Release" (or Beta, Project...) and `version` = a bare 4-part number like `7.1.11.12345` (`lllogininstance.cpp`). LibreMetaverse's doc comment ("Second Life Release n.n.n.n" in the version) is out of date. It passes both straight through. If either is empty it substitutes `channel` = "LibreMetaverse" (`Settings.UserAgent`) and `version` = "?.?.?", with a warning. `Author`/`UserAgent` aren't sent.
- The wiki's rules: the channel is a unique base name for the viewer, optionally with a variant (e.g. "My Viewer", "My Viewer Beta"), ASCII. It must not contain "Second Life", which is reserved for LL. The version is `major.minor.patch.build`. Policy 1.b/2.c.ii: each version needs a unique "viewer identifier" (name + variant + number), not reused from LL or another TPV, and spoofing one is prohibited. That rules out pretending to be Firestorm or the official viewer to dodge a block. 5.b: the name can't use any part of an LL mark ("Second", "Life", "SL", "Linden"), nor take the form "____ Life".
- **What we send:** `channel` = "Sojourn", `version` = "0.1.0.0". These are set in `src/Sojourn.Session/SessionHost.cs` (the `HostConfig` initialiser, for every app on it), with the same defaults in `src/Sojourn.Core/Config/Config.cs` (`HostConfig.Channel`/`Version`), and passed to `DefaultLoginParams(..., Host.Channel, Host.Version)` in `AgentSession.cs`. A packaged build of either app sends its own number, 0.1.0.N with N the repo's commit count (Access's `src/Sojourn.Access/AppInfo.cs`; the VR client's `ClientOptions.Version`, stamped by `tools/package.py`, shown in the launcher's footer and on the login sheet). Both comply. No listed TPV is called Sojourn (directory checked 2026-10-20). Each distributed build stays uniquely identifiable that way. A variant like "Sojourn Dev" for local-grid builds is optional.
- The other fields LibreMetaverse sends: `platform` "win", `platform_version`, `mac` and `id0`, both a hash of the MAC. The viewer also sends a hashed MAC, and 2.c.i forbids masking IP/MAC, so leave these alone. Don't zero them and don't route through a proxy to hide the IP.
- 1.g (version on the login screen and in an About window): the VR client's login sheet shows the channel and version, Access's login form its version and `//about`; the VR client's settings menu has an about section (version and commit, the disclaimer, who made it, the licence and source, support, the third-party notices: `Client/Control/AboutMenu.cs`), with the same words as the agent API's `SojournRelease about` (`Session/Release/ReleaseApi.cs`) for the assistant.

## Prohibited features (apply to us now)

- No getting around permissions, no altering creator/owner metadata, no bypassing privacy options the official viewer offers (2.a). The same goes for the assistant's tools: nothing the agent API exposes should reveal more than the viewer would.
- **Export** (2.b): any export feature the official viewer lacks must first check that the creator of "each and every" component is the user, including "full permissions" content. Sojourn's one export is build backups, which follow Firestorm's creator checks (`docs/building.md` > Backups). Screenshots (F12, render captures) are ordinary viewer snapshots. The local asset/bake caches are normal viewer caching; keep them as caches, not as a way to export.
- 2.c-d, 2.f: no DoS or griefing, no hiding data in assets (LSL data excepted), no "unreasonable or disproportionately large load". 1.a: stick to the viewer's protocol, and publicly document any departure from it. LibreMetaverse is the viewer protocol; we have no known departures.
- 2.i/2.j: never show other users' system, software or network details. Don't put the user's own system info in messages to others unless the user chooses to. So the assistant shouldn't volunteer "I'm running Sojourn on..." in chat.
- 2.k, verbatim (checked 2026-10-27): "You must not provide any feature that alters the shared experience of the virtual world in any way not provided by or accessible to users of the latest released Linden Lab viewer." Added Feb 2012; the motivating case was Emerald's extra attachment points, which looked right to Emerald users and wrong to everyone else. LL's only gloss is Oz Linden's forum comment (2012-02-29, reproduced at modemworld.me, "TPVP changes – Oz provides further explanation"): a shared-experience change "modifies the definition of the elements that make up the virtual world, or how they behave, in such a way that users on other viewers don't experience the same virtual reality", and the rule "does not affect changes to rendering, user interface, or the controls a viewer offers for interacting with the world" (he added that this doesn't modify the policy). So the test is fragmentation: does someone on LL's viewer see a different world? Local-only embodiment is fine. Tracking shown through what every viewer already renders (LookAt, PointAt, animations, scripted attachments) is fine, even if the official viewer wouldn't send those exact values. A Sojourn-only channel that shows our pose to Sojourn users while LL-viewer users see something else is the Emerald case. Detail: `docs/shared-tracking.md`.
- SL Policy 5(i): no robots or automation to raise land traffic.

## Data and privacy

- 4.a: we must update or delete data received from SL if LL asks. LL can limit TPV access whenever it likes.
- 4.b (distributor-only): a published privacy policy covering what user data is collected and shared, reasonable protection, and no data required to install or uninstall.
- ToS 6.2(i): no code that may "harvest or collect any data or information about other users without their consent". The Scripted Agent Policy adds that resident data gathered by tools is still covered by privacy law (GDPR, CCPA).
- **Community Standards:** "the remote monitoring, posting or sharing of conversations without a participant's consent are prohibited" (abuse reports to LL excepted). They also forbid sharing others' personal info (alts, RL location, etc.).

The assistant bears on these last two, since nearby chat, IMs, names and rendered views of other people go to Claude on Anthropic's servers. There's strong precedent that sending chat to a service acting for the reader is accepted: the official viewer has built-in chat translation through Google, Azure and DeepL (`lltranslate.cpp`). LL hasn't said anything about AI assistants specifically, though (unverified either way). Stay close to the translation model:
- Process chat transiently, for the person wearing the headset.
- Don't keep logs beyond what a viewer's own chat log would hold.
- Don't relay conversations anywhere else: no streams, no posts, no shared memory across projects.
- Never surface other residents' personal info.

A session's log that a tester sends us from Sojourn Access (`docs/access-app.md` > Sending a log), and the VR
client's problem and crash reports (`docs/client.md` > About, problem and crash reports), are filtered on the
person's PC first: nothing other people said or wrote goes, and their names and keys become stand-ins ("person 2").
A report goes only at the person's press (or, after a crash, by their "always" setting), to the studio relay (with
their Studio Quire token, or without one within the relay's limits, the IP address not kept), or is saved for them to
attach to a GitHub issue; a crash's memory dump never leaves the PC.

## Bots, scripted agents, and the assistant

- TPV Policy 9.g: a bot is "a software program that performs specific tasks automatically with little or no human direction". 4.a.iii: if your TPV is a bot, it must follow the bot policy.
- Scripted Agent Policy: "Any script or program that automates access to Second Life and its supporting web services through interfaces designed for the Second Life Viewer and its embedded browser is considered a Scripted Agent." The duty to register is narrower: "If you own a Second Life account primarily operating as a Scripted Agent, you must specify this on the Scripted Agent Status page." The wiki puts that page in the account control panel on secondlife.com; we didn't check, since that needs a login. The AGENT_AUTOMATED wiki page says not registering a bot is forbidden. Flagged accounts can be kept out of estates that set `deny_bots` (since 2023). Agents are also capped at 5,000 messages a day, and may not be used for traffic gaming, Mainland purchases, or getting around privacy settings.
- How that bears on our stance:
  - The TPV definition turns on "little or no human direction", and the registration duty on "primarily operating as" one. An account worn by a present human who directs the assistant is primarily a human account, so no flag. That matches the project decision not to carry Tessel's scripted-agent setup.
  - **The real tension:** the Scripted Agent *definition* is broad. "Any script or program that automates access" through viewer interfaces arguably describes the assistant's tool calls taken on their own, even with the human there. The policy only *imposes* anything (registration) on accounts operating primarily that way, but nothing published addresses a human-directed AI operating a human's viewer (unverified; nothing found). If LL ever wants certainty, a support ticket asking about it is the way to get an answer. That's the user's call, not a must.
- What keeps us on the human side of the line: the assistant acts on the person's direction while they're logged in and present. Don't leave it running the account autonomously while the user is away: no auto-replying to IMs as the user, no idle errands. Messages to other people go out as the user directs (ToS 6.1(ii): no impersonation or misrepresenting affiliation). The confirmations (L$, deletes, gives, permissions) fit here. Long multi-step jobs ("tidy my inventory") are fine as delegated tasks while the user is there. Watch out for anything that turns into unattended, recurring automation.

### How the assistant keeps to this (`docs/assistant.md`)

- Credentials: login, the ToS and MFA stay with the person's own controls (above); the agent API has no op that
  touches them. The Claude sessions have Claude Code's own tools (files, shell, web) in auto mode (user's decision,
  `docs/assistant.md` > What it may do): deny rules keep the file tools off the password file, MFA state, test
  logins and other programs' credentials, a hook refuses shell commands that name them, and auto mode's hard rules
  forbid reading or using any credential, or logging in, by any route. With a shell on Windows the last two are the
  guard, not an OS fence; `tools/assistant_guard.py` checks them with decoys. The user accepted that with the
  password stored (2026-10-29), rather than typing it at each login or a separate Windows account for the assistant.
- Present and directed: it acts only on the person's talk, typing or presses; nothing runs on a timer, and nothing
  answers anyone. Messages to others (say, message) carry what the person asked to say. Background jobs (a build)
  are started by the assistant only for what the person asked, and they watch and stop them; a job has no time limit
  (user, 2026-10-25), so it can carry on after they step away until it's done or stopped. Auto mode's rules forbid
  anything else that starts acting in SL on its own.
- Confirmations are the client's, at the op dispatcher, for every caller: spending, deleting, giving away,
  permission changes and new scripts wait for a press on the panel; the description comes from the op's arguments,
  not the model. The agent API refuses browsers (Origin, Host and content-type checks).
- Other people's chat goes to Claude only when a request is about it (`recent_chat`); sessions keep no transcripts
  and drop their context after 15 idle minutes; the sidecar logs timings and tool names, not words. The client's own
  event log (`local/client/state/pilot/events.jsonl`) is the viewer-like chat log it already kept. Its memory
  (`local/assistant/home`, notes about the person) holds what the person tells it, never what others said: the
  prompt says so, and auto mode's rules forbid writing others' conversations to files or sending them anywhere.
- Dev mode (`docs/dev-mode.md`): a Claude Code session the user started stands in for the assistant, in bypass mode.
  The same lines hold, kept by `docs/driving.md` and the studio's rules instead of a harness: it acts on the
  person's requests while they're there (a human-directed account, as above) and stops when they're done; the login,
  ToS and MFA stay the person's; it never reads the password or MFA files (2.e); the confirmations are the
  person's press as always. Other people's chat it reads is for the person's request, not kept or passed on.

## Responsibility

- TPV 7.a: users and developers are "responsible for all uses you make of Third-Party Viewers". 7.d: they "assume all risks". LL gives no support for TPV issues.
- ToS 4: the account holder is "solely responsible for all activities conducted through your Account whether or not you authorize the activity". Accounts can't be shared with another individual without LL's written consent.
- Whatever the assistant does on the account is the user's responsibility before LL. That's one more reason for the confirmations and for keeping the assistant under the human's direction.

## Ported viewer code (LGPL)

`Sojourn.Render` contains ports of viewer code (e.g. `Geometry/VolumeFace.cs`, "Copyright (C) 2010, Linden Research, Inc. LGPL 2.1") and ships viewer assets (avatar_lad, pixiesmall.j2c), all LGPL 2.1.
- 3.b.iii: a TPV that is *distributed* and uses viewer source must make that source available, as LGPL 2.1 requires. Private use triggers nothing.
- 2.h: "For Third-Party Viewers based on our source code, Developers must not omit our viewer statistics packet". Whether partly ported rendering code makes Sojourn "based on" viewer source is arguable. Today's viewer sends its stats as an HTTP POST to the region's `ViewerStats` capability (`llviewerstats.cpp`), not the old UDP packet. Sent since 2026-11-01 by both apps, as a minimal, honest report: this session and PC only, what we don't measure left out, the MAC address and serial only with the person's setting, off by the user's call (2026-11-01) (`docs/client.md` > About, problem and crash reports; `docs/viewer-gaps.md` > Waiting).

## If it's ever distributed

What the distributor rules ask, and where we stand (checked 2026-10-31; the open items are tracked in
`docs/viewer-gaps.md` > Releasing it):
- Disclosures on the download page: the name and the disclaimer "This software is not provided or supported by Linden
  Lab, the makers of Second Life."; support info, or a statement that there's none; surprising or unexpected
  functionality (an AI operating the client counts); non-General content; a link to a privacy policy. Drafted as the
  public repo's `docs/download.md` (from `tools/publish/sojourn/docs/download.md`) and Access's own copy in its
  repository. Written by Claude Opus 5.5 at the user's word (2026-11-01: ours to write); the privacy policy is that
  page's Privacy policy section, covering both apps and the relay.
- Install only at the user's direction, with consent for auto-update; clean uninstall. Access's package runs from its
  folder. Sojourn's installs only when the person runs its Setup, offers an update in the launcher and installs it only
  at their own press (each release's feed signed by our key), and uninstalls from Add/Remove Programs, keeping only the
  person's own accounts, settings and the assistant's memory (`docs/launcher.md` > The package; built 2026-11-01, not
  yet released).
- ToS presentation (done, above), version display (done, above), an About window with accurate source info (Access has
  `//about`; the VR client its menu's about section, 2026-11-01).
- No Eye-in-Hand logo; follow LL's Trademark Guidelines.
- LGPL source published: the repo is public.
- The viewer statistics report (2.h, above): sent since 2026-11-01, the MAC address and serial left out by default.
- Directory listing is optional: self-certification, a public contact channel, a public source repo and commit notices,
  accounts in good standing, and an application filed as an issue on LL's TPV GitHub repo.
