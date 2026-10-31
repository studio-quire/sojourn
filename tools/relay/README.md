# The studio relay

A Cloudflare Worker at https://relay.studioquire.org through which Sojourn Access's assistant reaches Anthropic's
Messages API on Studio Quire's key, for people the studio covers (a blind tester can't pay for it herself). Each
person has a personal token; the relay refuses anyone else, counts each token's spending from the responses' usage
and stops at its monthly cap. Written by Claude Opus 5.5, 2026-10-30.

What it answers, for a token's holder (`x-api-key: <token>` or `Authorization: Bearer <token>`):

- `POST /v1/messages` (streamed or not, any query such as `?beta=true`): passed to Anthropic with the studio's key,
  the answer streamed back as it comes. Refused with 402 (`billing_error`, a plain sentence, `x-should-retry: false`)
  once the token's spending this month (UTC) reaches its cap.
- `POST /v1/messages/count_tokens`, `GET /v1/models[/id]`: passed through, not counted (they cost nothing).
- `GET /relay/usage`: the token's month so far: spent, cap, left, requests, when it resets.
- anything else: 404; no token, or one it doesn't know: 401.

What it keeps: per token and month, a Durable Object's counters (dollars, requests, input, output, cache read and
write tokens, web searches). Nothing of a request or response is stored or logged (Workers Logs off, no
`console.log`); a request is passed on unread, and a response is read only for its `usage`. Spending is priced from
the model in the response (`PRICES` in `worker.js`: input, output, cache read, 5-minute and 1-hour cache writes,
Haiku 5.5's step above 100K prompt tokens, fast mode, regional inference, web searches); a model it doesn't know is
priced as the dearest. Requests already running when the cap is reached finish, so a month can end a little over.
The studio's key is scoped to its "Sojourn relay" workspace in Anthropic's console, which has its own spend limit
over all tokens together.

Secrets (never in this folder): `ANTHROPIC_KEY`, and `TOKENS`, a JSON map from each token's SHA-256 (hex) to its
name and cap in dollars a month. `python tools/relay.py` from the repo sets both; see its help for deploying, making,
copying, capping and revoking tokens, reading spending, and the end-to-end check.
