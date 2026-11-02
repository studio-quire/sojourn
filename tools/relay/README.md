# The studio relay

A Cloudflare Worker at https://relay.studioquire.org through which Sojourn Access's assistant reaches Anthropic's
Messages API on Studio Quire's key, for people the studio covers (a blind tester can't pay for it herself). Each
person has a personal token; the relay refuses anyone else, counts each token's spending from the responses' usage
and stops at its monthly cap. The same token sends a tester's logs and carries their messages to the studio and its
replies (below). Written by Claude Opus 5.5, 2026-10-30.

What it answers, for a token's holder (`x-api-key: <token>` or `Authorization: Bearer <token>`):

- `POST /v1/messages` (streamed or not, any query such as `?beta=true`): passed to Anthropic with the studio's key,
  the answer streamed back as it comes. Refused with 402 (`billing_error`, a plain sentence, `x-should-retry: false`)
  once the token's spending this month (UTC) reaches its cap.
- `POST /v1/messages/count_tokens`, `GET /v1/models[/id]`: passed through, not counted (they cost nothing).
- `GET /relay/usage`: the token's month so far: spent, cap, left, requests, when it resets.
- `POST /relay/log`: a session's log sent from Sojourn Access (Help > Send a log, `//sendlog`; docs/access-app.md >
  Sending a log), or a problem or crash report from Sojourn (docs/client.md > About, problem and crash reports),
  already filtered there: other people's words left out, their names stand-ins. The body is gzipped JSON
  (`content-type: application/gzip`): `log` (the text), `note` (the person's, at most 4,000 characters), `app`,
  `commit`, `session`, `current`, `lines`, `left_out`, and from Sojourn `kind` (problem or crash) and a crash's
  `exit_code`.
- `POST /relay/report`, **no token** (2026-11-01, the user's ask: reports from people without one): Sojourn's problem
  and crash reports, the same body and checks as a log (filtered on the person's PC, 1.5 MB as sent, 24 MB unpacked,
  a 4,000-character note), `app` "Sojourn N..." and `kind` problem or crash only (400 otherwise). Kept in a logbook of
  their own (`anonymous`) for 30 days, at most 300 (the oldest go first). Limits: three a day from one IP address
  (per UTC day) and fifty a day from everyone (any 24 hours), each refused with a plain sentence (429). The address is
  never stored: the day's count is kept under the SHA-256 of the ADMIN secret, the address and the date, and those
  counts are deleted when the day changes. Without the ADMIN secret set it answers 503. At most 1.5 MB as sent and 24 MB unpacked (413), ten a token in
  any 24 hours (429, a plain sentence); answered `{id, kept_until}`.
- `POST /relay/messages`, `{"text": "..."}`: the holder's message to the Claude sessions building Sojourn Access
  (Access's builders' tab, Control+Shift+B; docs/access-app.md > Messages to the builders), what they typed and nothing
  else: at most 4,000 characters, a hundred in any 24 hours (429, a plain sentence), a relay token in it replaced by
  "(a Studio Quire token, left out)"; answered `{id, at, kept_until}`.
- `GET /relay/messages?since=ID`: their conversation after that id, oldest first, `{messages: [{id, at, from: tester or
  studio, by (the studio's signature), text}], kept_days}`; the studio's replies it returns count as delivered. Ids
  only grow, so the app asks with the newest it has.
- anything else: 404; no token, or one it doesn't know: 401.

For the studio, by its admin secret (`Authorization: Bearer <secret>`; the Worker holds only its SHA-256, the `ADMIN`
secret): `GET /relay/admin/logs` lists every token's logs and the anonymous reports (token `anonymous`), `GET /relay/admin/logs/<id>` is one log's gzipped JSON as
sent, `DELETE` the same path removes it. `python tools/relay.py logs` uses them. For the messages (`python tools/relay.py
messages`): `GET /relay/admin/messages` each token's conversation in brief (count, the last, `unanswered`: the holder's
since the studio's last reply, `last_reply_delivered`), `?after=MS` every message since then across tokens (the
watch); `GET /relay/admin/messages/<token name>` one conversation; `POST` there `{text, by}` a reply (`by` the
signature, Claude unless given); `DELETE /relay/admin/messages/<token name>/<id>` one message.

What it keeps: per token and month, a Durable Object's counters (dollars, requests, input, output, cache read and
write tokens, web searches); and per token, in another (`Logbook`, SQLite), the logs its holder sent, each as sent
(gzipped) with the start of its note, for 30 days (an alarm deletes them then), at most 30 a token (the oldest go
first); in the same Logbook, the holder's messages to the studio and its replies, for 90 days (the alarm again), at
most 2,000 a token; and one more Logbook, `anonymous`, for the reports sent without a token, with that day's salted counts. Nothing of a request or response to Anthropic is stored or logged (Workers Logs off, no
`console.log`); a request is passed on unread, and a response is read only for its `usage`. Spending is priced from
the model in the response (`PRICES` in `worker.js`: input, output, cache read, 5-minute and 1-hour cache writes,
Haiku 5.5's step above 100K prompt tokens, fast mode, regional inference, web searches); a model it doesn't know is
priced as the dearest. Requests already running when the cap is reached finish, so a month can end a little over.
The studio's key is scoped to its "Sojourn relay" workspace in Anthropic's console, which has its own spend limit
over all tokens together.

Secrets (never in this folder): `ANTHROPIC_KEY`; `TOKENS`, a JSON map from each token's SHA-256 (hex) to its
name and cap in dollars a month; `ADMIN`, the SHA-256 (hex) of the admin secret. `python tools/relay.py` from the repo
sets them; see its help for deploying, making, copying, capping and revoking tokens, reading spending, the admin
secret, fetching and deleting logs (into `local/relay-logs/`), reading, answering and watching the messages, the end-to-end check against the live Worker, and
`check --local`, the same for the logs, reports, messages and tokens against this folder under `wrangler dev`, before
a deploy. A revoked token's logs and messages stay, unlisted, until their time is up.
