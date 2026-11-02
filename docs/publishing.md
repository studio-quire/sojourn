# Publishing the source

Agent-facing. Decided with the user 2026-10-29: the source is public as `studio-quire/sojourn` (LGPL-2.1-only), a fresh
history made from a scrubbed snapshot; this repo and its history stay private and are never pushed there. Sojourn
Access gets a code-free front door, `studio-quire/sojourn-access` (a README for screen-reader users, downloads as
releases, its issues).

## The run

`python tools/publish.py` (`--check` only builds the trees in a temp folder and checks them; no .NET build) makes or
refreshes two local repos beside this one, each run that changes one a new commit there (authored by Claude, the user's
GitHub noreply as committer, UTC dates):
- `../sojourn-public`: this repo's HEAD, committed files only, scrubbed, plus README, BUILDING.md (the guide for a
  person's own agent building it, around `tools/setup.py`), LICENSE, THIRD_PARTY.md and docs/download.md from
  `tools/publish/sojourn/`.
- `../sojourn-access-public`: `tools/publish/access/` only.

`local/publish/map.txt` records which private commit each public one came from (a run whose public tree didn't change
records the existing public HEAD for the new private one): a packaged build names the public commit made from the
commit it was built from. Sojourn's package (`tools/package.py`) stamps it into the app (the about section, problem
reports) and its release notes, and won't build a release without one; Access's (`tools/access.py package`) prints it
for its release notes. Access's test builds are released on its front door (since
2026-10-30); how, and what release notes say: `tools/publish/access/RELEASES.md` (published as it is).

Pushing is by hand, to a remote named `public` (never `origin`, so the nightly backup can't publish), only after a run
that printed "committed" and after `--check` passed for the same HEAD (on 2026-10-29 a chained push sent a snapshot that
didn't build).

## What it leaves out and rewrites (`tools/publish/rules.py`)

- Left out (`EXCLUDE`): board.json, `docs/sittings.md`, `docs/public-note-draft.md`, the research doc centred on two
  private individuals, and the publish machinery itself.
- Everything else is published, CLAUDE.md included (with a header saying what it is). Text moved between files moves
  between published and private accordingly: a sitting's private detail belongs in `docs/sittings.md`, not a feature
  doc.
- `SECTIONS` replace whole sections of access.md, radegast.md and accessibility.md; `SUBS` rewrite exact substrings in
  CLAUDE.md, a few docs and some source files. Both match exact text, so editing a passage they target makes the rule
  stale: the run prints "rule no longer matches"; fix the rule in the same commit.
- Private individuals (the blind tester and her partner, residents met in sessions, issue filers) become roles. In docs,
  code comments, tests and fixtures write roles, not names, so the rules stay few. Tessel and public figures in their
  published role may be named.
- The run refuses to commit on any `BANNED` name, handle, email or path, or a gitleaks finding (portable copy in
  `local/gitleaks`). gitleaks reads identifiers with Key, Secret or Token beside an assignment as API keys: name timers
  and flags otherwise.
