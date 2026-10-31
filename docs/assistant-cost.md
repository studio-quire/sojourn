# What the assistant costs

Written by Claude Opus 5.5, 2026-10-31. Agent-facing. Where the money goes when the assistant runs on an API key, the
studio relay or OpenRouter (a Claude account isn't billed per token), what was changed to make everyday use cheap, and
the measurements behind the defaults. Prices are Anthropic's per million tokens (`tools/relay/worker.js` PRICES): Haiku
5.5 $0.10 in / $0.50 out ($0.01 cache read, $0.125 five-minute and $0.20 one-hour cache write; five times all of it for
a prompt over 100K), Sonnet 5.5 $2 / $10, Opus 5.5 $4 / $20 ($0.20 read, $5 and $8 writes). Thinking bills as output.

## How it's measured

- `tools/costlab.py`: Sojourn Access as Cost Tester on the local grid (its own account, ports and base dir), its
  assistant on the studio relay with a token of its own, every model call passing through `tools/model_tap.py`, which
  keeps each request and its usage. `ask`, `describe` and `sitting` cost each request call by call; `parts N` splits a
  call's input into tools, system prompt, messages and pictures with the API's own token counting. The tap's dollars
  matched the relay's own count to the cent fraction (three descriptions: $0.07046 both).
- `tools/describe_lab.py`: describing's inputs captured (`costlab.py capture`), replayed straight to the Messages API
  at each model and setting, and judged blind (below).

## Where the money went (before, Claude Code 2.1.316, quick mode)

Every request is a turn of a Claude Code session, and every model call in it carries the session's whole prefix:
64.6K tokens, of which tools 40.9K (77 of them: Sojourn's ~20K, Claude Code's ~21K, ~11K of that for tools this
assistant never uses: claude.ai design sync, git worktrees, notebooks, code review, skills, agent teams), the system
prompt 20.2K (the agent API's op list most of it), Claude Code's environment block and the context line 3.6K. Claude
Code caches it for five minutes. A request is usually two or three calls (a tool call, then the reply).

| Use | Haiku first (quick) | Opus |
|---|---|---|
| a typed request ("turn my AO on", "who's near me?"), cache warm | $0.0014-0.0019 | $0.024-0.029 |
| the first request, or any after five quiet minutes | +$0.008 (64.6K written again) | +$0.33 |
| a new session's title (Claude Code names every session, on the session's model) | $0.0002 | $0.008 |
| Control+D, brief, in the session: two calls, and its pictures (3-7.5K tokens) stay in the conversation | $0.0022-0.0024 | $0.065 |
| the same key again ("more", rich) | $0.0025-0.0027 | $0.043 |
| past 100K tokens (about the tenth description of a sitting): Haiku's price steps up fivefold | $0.0056 a call, $0.011-0.014 a description | |
| a multi-step request handed to Opus while its cache is cold | +$0.33, then $0.013-0.016 a turn | |

Thinking was a small part (Haiku ~100-300 output tokens a turn, Opus ~50-150 at medium). Auto mode's review is
in-band on the Anthropic API and the relay (no separate classifier calls appeared). Claude Code's background calls were
only the session title.

## What changed

- **Descriptions are one lean call** (`assistant/describer.py`), not a session turn: the pictures and facts with ~900
  tokens of instructions, ~3-6K tokens in all, straight to the Messages API through the sidecar's provider proxy (so it
  works the same on a key, the relay and OpenRouter, and the proxy still holds the key). Access's describe key
  (Control+D) runs it without any session (`brain.py describe_key`); the describe tool, for spoken or typed asks, runs
  it and gives the session only the finished text to pass on. Nothing of the pictures enters a conversation. The
  exchange is recorded like any other, so the next request to any model knows what was described, and "more" adds to
  it (the brief text goes into the rich call). On the Claude account (only Claude Code holds it), or if a call fails,
  it falls back to the pictures in the session as before. Incognito: a blind spell keeps the old path (which refuses);
  a seeing spell's descriptions run the same way, logged nowhere and kept only in the spell's exchanges.
- **Describing has its own setting**, `access_describing` (Alt+0 "Describing model"): economy, Haiku 5.5 at high
  effort (the default), or best, Opus 5.5 at low effort (Descriptions, below). It's independent of who takes requests.
- **The session's prefix is 51.5K**: Claude Code's unused tools are disallowed (`harness.UNUSED_TOOLS`), and the
  describe tool's description no longer carries how to describe (that's the describer's).
- **An hour's cache for Haiku and Sonnet request sessions** (`CLAUDE_CODE_PROMPT_CACHE_TTL=1h`, `brain.py
  cache_env`): writing costs 1.6 times the five-minute price, and then a request after a pause of 5-60 minutes, or a
  fresh session after the 15-minute idle reset (same instructions and tools), reads the prefix (~$0.0005) instead of
  writing it ($0.0064). Opus (mostly the seldom-used planner) and jobs keep five minutes.
- **Background calls on Haiku** (`ANTHROPIC_SMALL_FAST_MODEL`): an Opus session's title no longer costs $0.008.
- **Haiku sessions start fresh at 90K** (`HAIKU_FRESH_AT`), after the request they're on, carrying the sitting's
  recent exchanges and the memory index as after any restart, so no Haiku call is ever priced over 100K. With
  descriptions out of the conversation that line is rarely reached.

| Use (after) | economy / Haiku first | best / Opus |
|---|---|---|
| a typed request, cache warm | $0.0012-0.0013 | $0.024 |
| the first request in an hour | $0.0104 | $0.27 (5-minute cache) |
| a request after a 5-60 minute pause | ~$0.0006 (read, not rewritten) | +$0.26 if over 5 minutes |
| Control+D, brief | $0.0007-0.001 | $0.018-0.026 |
| "more" (rich) | ~$0.002 | ~$0.04 |
| a typed ask about a thing ("what does the poster say?") | ~$0.002 (two short session turns and the description) | |

The lab's standard sitting (ten requests: four describe keys, six typed, `costlab.py sitting`): $0.0239 before,
$0.0128 after, with the session at 55K tokens at its end instead of 85K. The difference grows with the sitting: before,
every description added its pictures to every later call, and a sitting of a few dozen requests crossed 100K.

## Descriptions: how close Haiku and Sonnet come to Opus

The experiment (2026-10-31): 19 captures of what SojournDescribe hands the model. Eleven are of a platform built for it
with contents known exactly (`describe_lab.py scene`: a poster, a shop sign and a rules board with text, plain coloured
shapes, an unnamed one; ground truth in each capture's `truth.md`), eight of Multitude's Atelier garden (a plaza, a
mesh fountain, a cafe table, a mosaic sign with no text, a sculpture, an arch, a fountain whose own description says
stone though it looks wooden), seven brief and four rich (rich adds to the same arm's brief). Each was replayed at 15
settings, then pairs were judged blind by Opus 5.5 at high effort, A and B shuffled: every error (contradicts the
pictures, facts or ground truth) and invention (detail nothing supports) quoted and marked major (it would mislead
about something the person would care about or act on) or minor, names misused, text misread, talk of pictures or
tools, what's missed; coverage and register 1-5; which a blind person would rather have. A sample of every arm was read
by hand too, and the judge's quoted errors checked against the pictures: they held up.

Cost and speed per description (averages over the 19; pictures as drawn, 768-1024 px):

| Arm | $ brief | $ rich | words brief / rich | brief, median seconds |
|---|---|---|---|---|
| Haiku 5.5 low | 0.0004 | 0.0008 | 72 / 266 | 3.1 |
| Haiku 5.5 medium | 0.0006 | 0.0014 | 63 / 308 | 4.7 |
| Haiku 5.5 high (v2) | 0.0007 | 0.0019 | 57 / 249 | 5.1 |
| Sonnet 5.5 low (v2) | 0.009 | 0.016 | 96 / 301 | 4.0 |
| Opus 5.5 low (v2) | 0.019 | 0.040 | 57 / 255 | 5.2 |
| Opus 5.5 medium | 0.020 | 0.043 | 54 / 285 | 5.6 |

Input is most of an Opus description's price (~4K tokens: the pictures ~450-1,400 each, the facts, the instructions);
Haiku's is mostly thinking (high: ~800 tokens). Pictures shrunk to 512 px saved 18% on Opus and next to nothing on
Haiku, and cost text: two misreads in 19 where full size had none, so they stay as drawn.

Judged (counts over the 19; "v2" is the instructions' second version, below):

| Pair | major | minor | names | tooling | coverage | register | preferred |
|---|---|---|---|---|---|---|---|
| Haiku medium vs Opus medium | 5 vs 0 | 30 vs 15 | 10 vs 2 | 14 vs 4 | 3.6 vs 4.5 | 3.8 vs 4.4 | 0 vs 17 (2 ties) |
| Haiku high vs Opus medium (17) | 1 vs 0 | 17 vs 12 | 11 vs 2 | 3 vs 1 | 3.9 vs 4.6 | 4.4 vs 4.4 | 4 vs 13 |
| Haiku high v2 vs Opus low v2 | 1 vs 0 | 22 vs 14 | 11 vs 4 | 8 vs 4 | 3.7 vs 4.6 | 4.2 vs 4.3 | 1 vs 16 (2 ties) |
| Sonnet medium vs Opus medium | 1 vs 0 | 23 vs 18 | 0 vs 1 | 8 vs 2 | 4.5 vs 4.2 | 3.1 vs 4.6 | 3 vs 16 |
| Sonnet low vs Opus medium | 4 vs 0 | 9 vs 16 | 2 vs 2 | 9 vs 2 | 4.3 vs 4.3 | 3.1 vs 4.8 | 3 vs 16 |
| Opus low vs Opus medium | 0 vs 0 | 14 vs 15 | 1 vs 4 | 1 vs 3 | 4.5 vs 4.3 | 4.6 vs 4.5 | 9 vs 5 (5 ties) |
| Opus low v2 vs Opus medium (v1) | 0 vs 0 | 12 vs 17 | 2 vs 1 | 5 vs 4 | 4.5 vs 4.3 | 4.5 vs 4.5 | 8 vs 6 (5 ties) |

What it showed:
- **Opus makes no major errors** in any arm and reads text whole and right, says what it can't read, and notices when
  a thing's own description disagrees with how it looks. Low effort is as good as medium, a little shorter and cheaper:
  best is Opus low.
- **Haiku's errors are of a few kinds**: sides from the clock ("to your right" for one o'clock; once "behind you" for a
  wall ahead), a shape or colour drifted (a round table top "square", teal "green"), a light patch taken for an opening,
  talk of "the view" or "the uploaded image", generic words for named things ("a wooden floor" for Lab Floor). Text it
  reads right, but in a brief it reads less of it (two rules of eight where Opus gave all eight). Higher effort mattered:
  major errors 5 to 1 from medium to high, for a tenth of a cent. The second instructions (the clock's sides spelled
  out, nothing about pictures, a check of each detail before answering) cut Haiku medium's minor errors 30 to 22 and
  its tooling talk by half, and didn't change Opus.
- **Sonnet is accurate but long**: briefs of ~90-100 words where the others write ~55, numbered lists against the
  instructions, register around 3. At twelve times Haiku's price it isn't a better economy choice, and Opus is better
  and terser for twice Sonnet's.
- So the defaults: **economy is Haiku 5.5 high** (~$0.0007 a brief, ~$0.002 more), **best is Opus 5.5 low** (~$0.02,
  ~$0.04). Economy gives the gist right nearly always; what it loses against best is mostly precision of sides and
  shapes, some names and some of the text in a brief.

Examples (the poster, brief; ground truth: "SUNSET JAZZ", "Live at the Driftwood Pavilion", an orange half sun, "Fridays
8 PM SLT", "Free entry - Dress: smart casual", tiny "Sponsored by Harbour Lights Gallery"):
- Haiku high: "The Jazz Poster is straight ahead, nine metres away. It's a flat panel about a metre wide and one and a
  half metres high, with a dark navy background. Across the top, in gold, it reads "SUNSET JAZZ", with "Live at the
  Driftwood Pavilion" below, above an orange half-sun on the horizon. Smaller text reads "Fridays 8 PM SLT" and "Free
  entry - Dress: smart casual"." (calls the biggest line smaller; leaves out the sponsor)
- Opus low: "Jazz Poster hangs on Lab Wall 9 metres straight ahead: a tall navy poster with an orange half-sun on the
  horizon. It reads "SUNSET JAZZ" in gold, then "Live at the Driftwood Pavilion", "Fridays 8 PM SLT", "Free entry -
  Dress: smart casual", and in small print at the bottom, "Sponsored by Harbour Lights Gallery"."
- Sonnet low: the same text in two paragraphs, plus the half-sun "rising".

A fountain whose description says stone but looks wooden, brief: Haiku high: "The Fountain, about 24 metres away on
your right, is a round basin with a wood-grained rim holding dark water. A small red Wooden Table sits on a central
pillar in the middle of it, with an egg-shaped wooden piece above the table. Its listing calls it stone, but here the
rim looks wooden." (the table stands in front of the pillar; the top is a flat upright disc). Opus low: "Fountain, 24
metres away at three o'clock, is a low, many-sided round basin of dark green water. Though its own description calls
it stone, it looks like pale grain-patterned wood, with a central post holding up a large round disc. Wooden Table, a
small reddish-brown table, sits over the water. Grey stone plinths with sculptures stand around it on a shaded lawn."

The person themselves (the grid's default avatar), brief: Haiku high: "You're Cost Tester, standing barefoot in a
grey-blue long-sleeved top that's cropped above the waist, with red capri-length pants and a short brown bob with a
fringe." Opus low: "You're a slim human woman with a short brown bob and full bangs, wearing a fitted, long-sleeved
slate-blue top cropped above the navel, and snug red capri-length leggings ending at mid-calf. You're barefoot..."

Limits: the local grid's scenes are simple (plain shapes, one default avatar, no crowds, no mesh clothing with prints,
no busy signage), the instructions' second version was tuned on these same captures, and Opus judged Opus (its quoted
errors are checkable and were checked, but a preference for its own phrasing can't be ruled out). Clothing in the
designer's register, faces, crowds and real signage are where the gap between economy and best is likeliest to grow,
and none of that is here.

### Real captures

`SOJOURN_DESCRIBE_CAPTURE=DIR` in the environment Access starts from saves what each description is made from
(pictures and facts, as `costlab.py capture` saves them; never during incognito) to DIR, for `describe_lab.py replay`
and `judge` (copy a capture folder into `local/costlab/captures/`, add a `truth.md` if what's there is known). A short
sitting on Second Life with it on, at a busy place, a few people in mesh outfits, a shop with signs, gives the cases the
local grid lacks. The pictures show other residents: keep the folder on the PC it was made on, out of the repo, and
delete it after the comparison.

## Not done, worth doing

- **Haiku's sides**: a gloss on each clock direction in the facts ("one o'clock (ahead, a little right)") was tried
  on the 19 captures at haiku-high-v2 (2026-10-31): major errors 1 against 2, minor 17 against 17, the judge
  preferring the unglossed 10 to 7; no gain beyond noise, so not kept. Real places with more things round the clock
  may differ.
- **The op list in every call** (~11K tokens of the 51.5K): cached it's cheap on Haiku, but it's most of an Opus cold
  write ($0.05 of the $0.26). An api_help tool for the planner instead of the list would trade it for a lookup turn
  when client_api is needed.
- **Opus mode's pauses**: in opus mode every request after five quiet minutes writes the prefix again ($0.26); an
  hour's cache for Opus when it's the first model would pay from the second pause in an hour.
- **Simple requests without Claude Code**: most everyday asks are one tool call; a lean tool loop on the Messages API
  with only Sojourn's tools (~20K tokens) would roughly halve Haiku's per-call input again and cut latency, but it's a
  second harness beside Claude Code's (permissions, auto mode, hooks) and not worth it at Haiku's prices.
- **Flat things described from above**: SojournDescribe treats a table top as a flat sign and pictures it straight down
  (the leg unseen); a table needs the side view.
