// Studio Quire's relay: Anthropic's Messages API for the holders of a personal token, on the studio's key, each
// token's spending counted from the responses' usage and capped by the month. No request or response content is kept
// or logged: a request is read only to pass it on, and of a response only its usage numbers are looked at. Apart from
// that, a token's holder can send a session's log from Sojourn Access (filtered there), kept 30 days for the studio;
// and anyone can send a problem or crash report from Sojourn (filtered there too) without a token, within per-address
// and daily limits, kept 30 days; the address is kept only as a salted hash, for that day's count. And a token's holder
// and the studio's sessions write to each other (Sojourn Access's builders' tab): what the holder typed and the studio's
// replies, kept MSG_DAYS days.
// README.md beside this says what it answers and how tokens and caps are set (tools/relay.py).
// Written by Claude Opus 5.5, 2026-10-30.
import { DurableObject } from "cloudflare:workers";

const UPSTREAM = "https://api.anthropic.com";

// dollars per million tokens: input, output, cache read, cache write (5 minutes), cache write (1 hour).
// Checked against Anthropic's price list 2026-10-30. A model not listed is charged as the dearest one.
const PRICES = [
  ["claude-haiku-5-5", [0.10, 0.50, 0.01, 0.125, 0.20], [0.50, 2.50, 0.05, 0.625, 1.00]],  // the second: prompts over 100K
  ["claude-sonnet-5-5", [2, 10, 0.20, 2.50, 4]],
  ["claude-sonnet-5", [2, 10, 0.20, 2.50, 4]],
  ["claude-opus-5-5", [4, 20, 0.20, 5, 8]],
  ["claude-opus-5", [5, 25, 0.50, 6.25, 10]],
  ["claude-opus-4", [5, 25, 0.50, 6.25, 10]],
  ["claude-sonnet-4", [3, 15, 0.30, 3.75, 6]],
  ["claude-haiku-4", [1, 5, 0.10, 1.25, 2]],
  ["claude-fable", [10, 50, 0.25, 12.50, 20]],
  ["claude-mythos", [10, 50, 0.25, 12.50, 20]],
];
const DEAREST = [10, 50, 1.00, 12.50, 20];
const SEARCH = 0.01;          // a web search, $10 per thousand
const LONG_PROMPT = 100_000;  // Haiku 5.5's price steps up above this

const ROUTES = [
  ["POST", /^\/v1\/messages$/, "messages"],
  ["POST", /^\/v1\/messages\/count_tokens$/, "free"],
  ["GET", /^\/v1\/models(\/[\w.\-]+)?$/, "free"],
  ["GET", /^\/relay\/usage$/, "usage"],
  ["POST", /^\/relay\/log$/, "log"],
  ["POST", /^\/relay\/report$/, "report"],   // no token: Sojourn's problem and crash reports from anyone
  ["POST", /^\/relay\/messages$/, "write"],  // the holder's message to the builders
  ["GET", /^\/relay\/messages$/, "read"],    // the conversation, ?since=ID
  // the studio's own, by the admin secret (tools/relay.py logs, messages)
  ["GET", /^\/relay\/admin\/logs$/, "admin"],
  ["GET", /^\/relay\/admin\/logs\/([\w\-]+)$/, "admin"],
  ["DELETE", /^\/relay\/admin\/logs\/([\w\-]+)$/, "admin"],
  ["GET", /^\/relay\/admin\/messages$/, "admin_messages"],
  ["GET", /^\/relay\/admin\/messages\/([\w\-]+)$/, "admin_messages"],
  ["POST", /^\/relay\/admin\/messages\/([\w\-]+)$/, "admin_messages"],
  ["DELETE", /^\/relay\/admin\/messages\/([\w\-]+)\/(\d+)$/, "admin_messages"],
];

// messages between a token's holder and the studio (Sojourn Access's builders' tab, tools/relay.py messages): what the
// holder typed and the studio's replies, nothing else; kept per token MSG_DAYS days, then deleted
const MSG_CHARS = 4000;           // a message's characters
const MSG_DAYS = 90;
const MSGS_A_DAY = 100;           // from a holder, in any 24 hours (the studio's replies aren't counted)
const MSGS_KEPT = 2000;           // per token: the oldest go beyond this
const TOKEN_SHAPE = /sqr-[A-Za-z0-9_\-]{40,64}/g;   // a relay token pasted into a message is left out of it

// a log sent from Sojourn Access (Help > Send a log; already filtered there: other people's words left out): gzipped
// JSON {log, note, app, session, ...}, kept per token for LOG_DAYS days, then deleted
const LOG_MAX = 1_500_000;        // bytes as sent (gzipped)
const LOG_UNPACKED = 24_000_000;  // bytes unpacked
const LOG_NOTE = 4000;            // characters of the person's note
const LOG_DAYS = 30;
const LOGS_A_DAY = 10;            // per token, in any 24 hours
const LOGS_KEPT = 30;             // per token: the oldest go beyond this
const DAY = 86_400_000;
// reports without a token (Sojourn's menu > about > report a problem, the launcher after a crash): kept in a logbook of
// their own, as logs are (the same sizes, LOG_DAYS days), within these limits
const ANON = "anonymous";          // the logbook's name (a token's is its hash, so they can't meet)
const ANON_PER_ADDRESS = 3;        // per IP address, per UTC day
const ANON_A_DAY = 50;             // from everyone, in any 24 hours
const ANON_KEPT = 300;             // the oldest go beyond this

export default {
  async fetch(req, env, ctx) {
    const url = new URL(req.url);
    if (req.method === "GET" && url.pathname === "/") {
      return new Response("Studio Quire's relay for Sojourn Access's assistant, and for Sojourn's problem reports. Most of it answers only holders of a token.\n",
        { headers: { "content-type": "text/plain; charset=utf-8" } });
    }
    const route = ROUTES.find(([m, re]) => m === req.method && re.test(url.pathname));
    if (!route) return error(404, "not_found_error", "The relay doesn't answer that.");
    if (route[2] === "admin") return admin(req, env, url.pathname.match(route[1])[1]);
    if (route[2] === "admin_messages") return adminMessages(req, env, url, url.pathname.match(route[1]));
    if (route[2] === "report") return takeReport(req, env);
    if (!env.ANTHROPIC_KEY || !env.TOKENS) return error(503, "api_error", "The relay isn't set up yet.");

    const given = req.headers.get("x-api-key") || (req.headers.get("authorization") || "").replace(/^Bearer\s+/i, "");
    const hash = given ? await sha256(given) : "";
    const holder = hash && tokens(env)[hash];
    if (!holder) return error(401, "authentication_error", "That isn't a token this relay knows.");

    const month = monthNow();
    const meter = env.METER.get(env.METER.idFromName(hash));
    const cap = Math.round((Number(holder.cap) || 0) * 1e6);   // micro-dollars
    if (route[2] === "usage") {
      const u = await meter.usage(month);
      return json(200, { month, spent_usd: u.micro / 1e6, cap_usd: cap / 1e6, left_usd: Math.max(0, cap - u.micro) / 1e6,
                         requests: u.requests, resets: nextMonth() });
    }
    if (route[2] === "log") return takeLog(req, env, hash);
    if (route[2] === "write") return takeMessage(req, env, hash);
    if (route[2] === "read") return readMessages(env, hash, url);
    if (route[2] === "messages" && await meter.spent(month) >= cap) return capped();

    const headers = new Headers({ "x-api-key": env.ANTHROPIC_KEY, "user-agent": "sojourn-relay" });
    for (const k of ["content-type", "anthropic-version", "anthropic-beta", "accept"]) {
      const v = req.headers.get(k);
      if (v) headers.set(k, v);
    }
    if (!headers.has("anthropic-version")) headers.set("anthropic-version", "2023-06-01");
    let up;
    try {
      up = await fetch(UPSTREAM + url.pathname + url.search, {
        method: req.method, headers, body: req.method === "POST" ? req.body : undefined,
      });
    } catch (e) {
      return error(502, "api_error", "The relay couldn't reach Anthropic.");
    }
    const out = new Headers();
    for (const [k, v] of up.headers) {
      const l = k.toLowerCase();
      if (l === "content-type" || l === "request-id" || l === "retry-after" || l === "x-should-retry") out.set(k, v);
    }
    if (route[2] !== "messages" || !up.body) return new Response(up.body, { status: up.status, headers: out });

    // the answer passed on as it comes, its usage read on the way and added to the month's count when it ends (or
    // when the caller goes away: what was generated is billed all the same)
    const usage = new Usage(up.headers.get("content-type"));
    const { readable, writable } = new TransformStream();
    ctx.waitUntil((async () => {
      const reader = up.body.getReader(), writer = writable.getWriter();
      try {
        for (;;) {
          const { done, value } = await reader.read();
          if (done) break;
          usage.feed(value);
          await writer.write(value);
        }
        await writer.close();
      } catch (e) {
        try { await reader.cancel(); } catch {}
        try { await writer.abort(e); } catch {}
      } finally {
        usage.end();
        if (usage.seen) await meter.add(month, usage.record());
      }
    })());
    return new Response(readable, { status: up.status, headers: out });
  },
};

// ---- the counters: one Durable Object per token, a record per month --------------------------------------------------

export class Meter extends DurableObject {
  async spent(month) {
    return (await this.ctx.storage.get(month))?.micro || 0;
  }

  async usage(month) {
    return (await this.ctx.storage.get(month)) || zero();
  }

  async add(month, rec) {
    const m = (await this.ctx.storage.get(month)) || zero();
    for (const k of Object.keys(rec)) m[k] = (m[k] || 0) + rec[k];
    await this.ctx.storage.put(month, m);
    return m.micro;
  }
}

function zero() {
  return { micro: 0, requests: 0, input: 0, output: 0, cache_read: 0, cache_write: 0, searches: 0 };
}

// ---- logs sent from Sojourn Access: one Durable Object per token, its SQLite table of them ---------------------------

async function takeLog(req, env, hash) {
  const got = await readLog(req);
  if (got instanceof Response) return got;
  const book = env.LOGBOOK.get(env.LOGBOOK.idFromName(hash));
  const kept = await book.add(got.meta, got.body.buffer);
  if (kept.error) return error(429, "rate_limit_error", `${LOGS_A_DAY} logs a day can be sent. Try again tomorrow.`);
  return json(200, { id: kept.id, kept_until: new Date(kept.until).toISOString().slice(0, 10) });
}

/** A report from Sojourn without a token: the same checks as a log, from Sojourn only, then the anonymous logbook's
 *  limits: ANON_PER_ADDRESS from one IP address a day (counted by a hash of the address salted with the ADMIN secret
 *  and the day, dropped the next day), ANON_A_DAY from everyone. */
async function takeReport(req, env) {
  if (!env.ADMIN) return error(503, "api_error", "The relay isn't set up for reports yet.");
  const got = await readLog(req);
  if (got instanceof Response) return got;
  const { doc, body, meta } = got;
  if (typeof doc.app !== "string" || !/^Sojourn \d/.test(doc.app) || !["problem", "crash"].includes(doc.kind)) {
    return error(400, "invalid_request_error", "Only Sojourn's problem and crash reports are taken here.");
  }
  meta.kind = doc.kind;
  const day = new Date().toISOString().slice(0, 10);
  const address = req.headers.get("cf-connecting-ip") || "unknown";
  const who = await sha256(`${env.ADMIN}|${address}|${day}`);
  const book = env.LOGBOOK.get(env.LOGBOOK.idFromName(ANON));
  const kept = await book.add(meta, body.buffer, { perDay: ANON_A_DAY, kept: ANON_KEPT, who, day, perWho: ANON_PER_ADDRESS });
  if (kept.error === "too_many_from_here") {
    return error(429, "rate_limit_error", `${ANON_PER_ADDRESS} reports a day can be sent from one place. Try again tomorrow.`);
  }
  if (kept.error) {
    return error(429, "rate_limit_error", "The relay has taken all the reports it can today. Try again tomorrow, or save the report for GitHub.");
  }
  return json(200, { id: kept.id, kept_until: new Date(kept.until).toISOString().slice(0, 10) });
}

/** A gzipped JSON log as sent, checked: {doc, body, meta}, or the refusal. */
async function readLog(req) {
  const declared = Number(req.headers.get("content-length") || 0);
  if (declared > LOG_MAX) return error(413, "request_too_large", "That log is too big to send.");
  const body = new Uint8Array(await req.arrayBuffer());
  if (body.byteLength > LOG_MAX) return error(413, "request_too_large", "That log is too big to send.");
  if (body.byteLength < 18 || body[0] !== 0x1f || body[1] !== 0x8b) return error(400, "invalid_request_error", "A log is sent gzipped.");
  let doc;
  try {
    doc = JSON.parse(await gunzip(body, LOG_UNPACKED));
  } catch (e) {
    return e && e.tooBig ? error(413, "request_too_large", "That log is too big to send.")
      : error(400, "invalid_request_error", "That log couldn't be read.");
  }
  if (!doc || typeof doc !== "object" || typeof doc.log !== "string" || !doc.log.length) {
    return error(400, "invalid_request_error", "That log has no log in it.");
  }
  const note = typeof doc.note === "string" ? doc.note : "";
  if (note.length > LOG_NOTE) return error(400, "invalid_request_error", `A note can be ${LOG_NOTE} characters at most.`);
  const short = (v) => typeof v === "string" ? v.slice(0, 200) : "";
  const meta = { app: short(doc.app), session: short(doc.session), note: note.slice(0, 200), lines: doc.log.split("\n").length };
  return { doc, body, meta };
}

/** The text of gzipped bytes, refusing (an error with tooBig) past max bytes unpacked. */
async function gunzip(bytes, max) {
  const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"));
  const reader = stream.getReader();
  const parts = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.byteLength;
    if (size > max) {
      try { await reader.cancel(); } catch {}
      throw Object.assign(new Error("too big"), { tooBig: true });
    }
    parts.push(value);
  }
  const all = new Uint8Array(size);
  let at = 0;
  for (const p of parts) { all.set(p, at); at += p.byteLength; }
  return new TextDecoder("utf-8", { fatal: true }).decode(all);
}

/** The studio's view of the logs (tools/relay.py logs): `Authorization: Bearer <admin secret>`, whose SHA-256 is the
 *  ADMIN secret. GET /relay/admin/logs lists every token's, GET .../ID is one log's gzipped JSON, DELETE .../ID removes it. */
async function admin(req, env, id) {
  const refused = await notAdmin(req, env);
  if (refused) return refused;
  const all = [...Object.entries(env.TOKENS ? tokens(env) : {}), [ANON, { name: ANON }]];
  if (!id) {
    const logs = [];
    for (const [hash, t] of all) {
      for (const l of await env.LOGBOOK.get(env.LOGBOOK.idFromName(hash)).list()) logs.push({ token: t.name, ...l });
    }
    logs.sort((a, b) => b.at - a.at);
    return json(200, { logs });
  }
  for (const [hash] of all) {
    const book = env.LOGBOOK.get(env.LOGBOOK.idFromName(hash));
    if (req.method === "DELETE") {
      if (await book.remove(id)) return json(200, { deleted: id });
      continue;
    }
    const body = await book.get(id);
    if (body) return new Response(body, { headers: { "content-type": "application/gzip" } });
  }
  return error(404, "not_found_error", "No log with that id.");
}

/** Null for the studio's admin secret (`Authorization: Bearer <secret>`, whose SHA-256 is the ADMIN secret), else the refusal. */
async function notAdmin(req, env) {
  if (!env.ADMIN) return error(503, "api_error", "The relay's admin isn't set up.");
  const given = (req.headers.get("authorization") || "").replace(/^Bearer\s+/i, "");
  if (!given || await sha256(given) !== env.ADMIN) return error(401, "authentication_error", "Not the relay's admin.");
  return null;
}

// ---- messages between a token's holder and the studio (Sojourn Access's builders' tab) -------------------------------

/** POST /relay/messages, {text}: the holder's message, kept in their Logbook. */
async function takeMessage(req, env, hash) {
  const text = await messageText(req);
  if (text instanceof Response) return text;
  const book = env.LOGBOOK.get(env.LOGBOOK.idFromName(hash));
  const kept = await book.say(false, "", text.replace(TOKEN_SHAPE, "(a Studio Quire token, left out)"));
  if (kept.error) return error(429, "rate_limit_error", `${MSGS_A_DAY} messages a day can be sent. Try again tomorrow.`);
  return json(200, kept);
}

/** GET /relay/messages?since=ID: the conversation after that id, oldest first; what's fetched is marked delivered. */
async function readMessages(env, hash, url) {
  const since = Math.max(0, Number(url.searchParams.get("since")) || 0);
  const book = env.LOGBOOK.get(env.LOGBOOK.idFromName(hash));
  return json(200, { messages: await book.messages(since, true), kept_days: MSG_DAYS });
}

/** A message's text from a JSON body {text, by?}: the text, trimmed, or the refusal; by is the studio's signature. */
async function messageText(req, withBy = false) {
  if (Number(req.headers.get("content-length") || 0) > 64_000) return error(413, "request_too_large", "That message is too long.");
  let doc;
  try {
    const raw = await req.text();
    if (raw.length > 64_000) return error(413, "request_too_large", "That message is too long.");
    doc = JSON.parse(raw);
  } catch {
    return error(400, "invalid_request_error", "A message is sent as JSON: {\"text\": \"...\"}.");
  }
  const text = typeof doc?.text === "string" ? doc.text.trim() : "";
  if (!text) return error(400, "invalid_request_error", "That message has nothing in it.");
  if (text.length > MSG_CHARS) return error(400, "invalid_request_error", `A message can be ${MSG_CHARS} characters at most.`);
  if (!withBy) return text;
  const by = typeof doc.by === "string" && doc.by.trim() ? doc.by.trim().slice(0, 60) : "Claude";
  return { text, by };
}

/** The studio's side (tools/relay.py messages), by the admin secret:
 *  GET /relay/admin/messages: each token's conversation in brief; ?after=MS: every message since then, all tokens;
 *  GET .../NAME: one token's conversation; POST .../NAME {text, by}: a reply; DELETE .../NAME/ID: one message removed. */
async function adminMessages(req, env, url, m) {
  const refused = await notAdmin(req, env);
  if (refused) return refused;
  const all = Object.entries(env.TOKENS ? tokens(env) : {});
  const bookOf = (hash) => env.LOGBOOK.get(env.LOGBOOK.idFromName(hash));
  const name = m[1];
  if (!name) {
    if (url.searchParams.has("after")) {
      const after = Number(url.searchParams.get("after")) || 0;
      const out = [];
      for (const [hash, t] of all) for (const msg of await bookOf(hash).after(after)) out.push({ token: t.name, ...msg });
      out.sort((a, b) => a.at - b.at || a.id - b.id);
      return json(200, { messages: out });
    }
    const conversations = [];
    for (const [hash, t] of all) {
      const s = await bookOf(hash).summary();
      if (s.count) conversations.push({ token: t.name, ...s });
    }
    conversations.sort((a, b) => b.last_at - a.last_at);
    return json(200, { conversations, kept_days: MSG_DAYS });
  }
  const found = all.find(([, t]) => t.name === name);
  if (!found) return error(404, "not_found_error", "No token with that name.");
  const book = bookOf(found[0]);
  if (req.method === "DELETE") {
    return await book.unsay(Number(m[2])) ? json(200, { deleted: Number(m[2]) }) : error(404, "not_found_error", "No message with that id.");
  }
  if (req.method === "POST") {
    const got = await messageText(req, true);
    if (got instanceof Response) return got;
    return json(200, await book.say(true, got.by, got.text));
  }
  return json(200, { token: name, messages: await book.messages(Math.max(0, Number(url.searchParams.get("since")) || 0), false),
                     ...(await book.summary()) });
}

export class Logbook extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    this.sql.exec("CREATE TABLE IF NOT EXISTS logs (id TEXT PRIMARY KEY, at INTEGER NOT NULL, meta TEXT NOT NULL, body BLOB NOT NULL)");
    // the anonymous book's counts by sender, for today only (who: a salted hash of the address and the day)
    this.sql.exec("CREATE TABLE IF NOT EXISTS senders (day TEXT NOT NULL, who TEXT NOT NULL, n INTEGER NOT NULL, PRIMARY KEY (day, who))");
    // the holder's conversation with the studio (studio: 1 for the studio's replies); ids only grow, so `since` cursors hold
    this.sql.exec("CREATE TABLE IF NOT EXISTS messages (id INTEGER PRIMARY KEY AUTOINCREMENT, at INTEGER NOT NULL, studio INTEGER NOT NULL, by TEXT NOT NULL, text TEXT NOT NULL)");
    // fetched: the highest message id the holder's app has fetched (the studio's replies up to it are delivered)
    this.sql.exec("CREATE TABLE IF NOT EXISTS state (k TEXT PRIMARY KEY, v INTEGER NOT NULL)");
  }

  /** limits: a token's (LOGS_A_DAY, LOGS_KEPT) unless given: {perDay, kept, who, day, perWho} for the anonymous book. */
  async add(meta, body, limits = null) {
    const perDay = limits?.perDay ?? LOGS_A_DAY, keep = limits?.kept ?? LOGS_KEPT;
    const now = Date.now();
    this.expire(now);
    const today = this.sql.exec("SELECT COUNT(*) AS n FROM logs WHERE at > ?", now - DAY).one().n;
    if (today >= perDay) return { error: "too_many" };
    if (limits?.who) {
      this.sql.exec("DELETE FROM senders WHERE day <> ?", limits.day);
      const row = this.sql.exec("SELECT n FROM senders WHERE day = ? AND who = ?", limits.day, limits.who).toArray()[0];
      if ((row?.n || 0) >= limits.perWho) return { error: "too_many_from_here" };
      this.sql.exec("INSERT INTO senders (day, who, n) VALUES (?, ?, 1) ON CONFLICT (day, who) DO UPDATE SET n = n + 1",
        limits.day, limits.who);
    }
    const rand = [...crypto.getRandomValues(new Uint8Array(3))].map(b => b.toString(16).padStart(2, "0")).join("");
    const id = new Date(now).toISOString().replace(/[-:]/g, "").slice(0, 15) + "-" + rand;
    this.sql.exec("INSERT INTO logs (id, at, meta, body) VALUES (?, ?, ?, ?)", id, now, JSON.stringify(meta), body);
    this.sql.exec("DELETE FROM logs WHERE id NOT IN (SELECT id FROM logs ORDER BY at DESC LIMIT ?)", keep);
    await this.schedule();
    return { id, until: now + LOG_DAYS * DAY };
  }

  list() {
    this.expire(Date.now());
    return this.sql.exec("SELECT id, at, meta, length(body) AS size FROM logs ORDER BY at DESC").toArray()
      .map(r => ({ id: r.id, at: r.at, size: r.size, ...JSON.parse(r.meta) }));
  }

  get(id) {
    this.expire(Date.now());
    const rows = this.sql.exec("SELECT body FROM logs WHERE id = ?", id).toArray();
    return rows.length ? rows[0].body : null;
  }

  remove(id) {
    return this.sql.exec("DELETE FROM logs WHERE id = ?", id).rowsWritten > 0;
  }

  expire(now) {
    this.sql.exec("DELETE FROM logs WHERE at <= ?", now - LOG_DAYS * DAY);
    this.sql.exec("DELETE FROM messages WHERE at <= ?", now - MSG_DAYS * DAY);
  }

  /** An alarm when the oldest log's or message's time is up, so it goes after LOG_DAYS or MSG_DAYS whether or not
   *  anything asks again. */
  async schedule() {
    const log = this.sql.exec("SELECT MIN(at) AS at FROM logs").one().at;
    const msg = this.sql.exec("SELECT MIN(at) AS at FROM messages").one().at;
    const due = [log && log + LOG_DAYS * DAY, msg && msg + MSG_DAYS * DAY].filter(Boolean);
    if (due.length) await this.ctx.storage.setAlarm(Math.min(...due) + 60_000);
  }

  // ---- the holder's conversation with the studio

  /** A message: the holder's (studio false; MSGS_A_DAY in any 24 hours) or the studio's reply, signed by. */
  async say(studio, by, text) {
    const now = Date.now();
    this.expire(now);
    if (!studio) {
      const today = this.sql.exec("SELECT COUNT(*) AS n FROM messages WHERE studio = 0 AND at > ?", now - DAY).one().n;
      if (today >= MSGS_A_DAY) return { error: "too_many" };
    }
    const id = this.sql.exec("INSERT INTO messages (at, studio, by, text) VALUES (?, ?, ?, ?) RETURNING id", now, studio ? 1 : 0, by, text).one().id;
    this.sql.exec("DELETE FROM messages WHERE id NOT IN (SELECT id FROM messages ORDER BY id DESC LIMIT ?)", MSGS_KEPT);
    await this.schedule();
    return { id, at: now, kept_until: new Date(now + MSG_DAYS * DAY).toISOString().slice(0, 10) };
  }

  /** The messages after an id, oldest first; byApp: the holder's app fetched them, so the studio's are delivered. */
  messages(since, byApp) {
    this.expire(Date.now());
    const rows = this.sql.exec("SELECT id, at, studio, by, text FROM messages WHERE id > ? ORDER BY id", since).toArray();
    if (byApp && rows.length) {
      this.sql.exec("INSERT INTO state (k, v) VALUES ('fetched', ?) ON CONFLICT (k) DO UPDATE SET v = MAX(v, excluded.v)", rows[rows.length - 1].id);
    }
    return rows.map(Logbook.message);
  }

  /** Every message sent since a time (ms), oldest first: for the studio's watch. */
  after(ms) {
    this.expire(Date.now());
    return this.sql.exec("SELECT id, at, studio, by, text FROM messages WHERE at >= ? ORDER BY id", ms).toArray().map(Logbook.message);
  }

  /** The conversation in brief: how many, the last, the holder's messages since the studio's last reply (unanswered),
   *  and whether that reply has reached the holder's app. */
  summary() {
    this.expire(Date.now());
    const n = this.sql.exec("SELECT COUNT(*) AS n FROM messages").one().n;
    if (!n) return { count: 0 };
    const last = this.sql.exec("SELECT id, at, studio FROM messages ORDER BY id DESC LIMIT 1").one();
    const reply = this.sql.exec("SELECT MAX(id) AS id FROM messages WHERE studio = 1").one().id || 0;
    const unanswered = this.sql.exec("SELECT COUNT(*) AS n FROM messages WHERE studio = 0 AND id > ?", reply).one().n;
    const fetched = this.sql.exec("SELECT v FROM state WHERE k = 'fetched'").toArray()[0]?.v || 0;
    return { count: n, last_id: last.id, last_at: last.at, last_from: last.studio ? "studio" : "tester", unanswered,
             fetched_upto: fetched, last_reply_delivered: reply ? fetched >= reply : null };
  }

  unsay(id) {
    return this.sql.exec("DELETE FROM messages WHERE id = ?", id).rowsWritten > 0;
  }

  static message(r) {
    return { id: r.id, at: r.at, from: r.studio ? "studio" : "tester", ...(r.studio ? { by: r.by } : {}), text: r.text };
  }

  async alarm() {
    this.expire(Date.now());
    this.sql.exec("DELETE FROM senders WHERE day <> ?", new Date().toISOString().slice(0, 10));
    await this.schedule();
  }
}

// ---- a response's usage: the JSON body's, or a stream's message_start and message_delta events -----------------------

class Usage {
  constructor(contentType) {
    this.decoder = new TextDecoder();
    this.line = "";        // the current line, kept only while it may be a usage event
    this.skipping = false; // the rest of a line that isn't one
    this.body = "";        // a whole JSON answer (not streamed), kept until its end to read its usage
    this.kind = /event-stream/i.test(contentType || "") ? "sse" : "json";
    this.model = "";
    this.u = {};
    this.seen = false;
  }

  feed(bytes) {
    const text = this.decoder.decode(bytes, { stream: true });
    if (this.kind === "json") {
      if (this.body.length < 4_000_000) this.body += text;
      return;
    }
    let start = 0;
    for (;;) {
      const nl = text.indexOf("\n", start);
      const piece = nl < 0 ? text.slice(start) : text.slice(start, nl);
      if (!this.skipping) {
        this.line += piece;
        if (this.line.length >= 32 && !/^data: ?\{"type": ?"message_(start|delta)"/.test(this.line)) {
          this.line = "";
          this.skipping = nl < 0;
        }
      } else if (nl >= 0) {
        this.skipping = false;
      }
      if (nl < 0) break;
      if (this.line) this.event(this.line);
      this.line = "";
      start = nl + 1;
    }
  }

  event(line) {
    let d;
    try { d = JSON.parse(line.replace(/^data: ?/, "")); } catch { return; }
    if (d.type === "message_start" && d.message) {
      this.model = d.message.model || this.model;
      this.take(d.message.usage);
    } else if (d.type === "message_delta") {
      this.take(d.usage);
    }
  }

  take(u) {
    if (!u || typeof u !== "object") return;
    this.seen = true;
    // message_delta's counts are the totals so far: the larger of each wins
    for (const [k, v] of Object.entries(u)) {
      if (typeof v === "number") this.u[k] = Math.max(this.u[k] || 0, v);
      else if (v && typeof v === "object") this.u[k] = { ...(this.u[k] || {}), ...v };
      else if (typeof v === "string") this.u[k] = v;
    }
  }

  end() {
    if (this.kind === "sse" && this.line) this.event(this.line);
    if (this.kind === "json" && this.body) {
      try {
        const d = JSON.parse(this.body);
        this.model = d.model || "";
        this.take(d.usage);
      } catch {}
      this.body = "";
    }
  }

  record() {
    const u = this.u;
    const input = u.input_tokens || 0, output = u.output_tokens || 0, read = u.cache_read_input_tokens || 0;
    const write = u.cache_creation_input_tokens || 0;
    const w1h = (u.cache_creation && u.cache_creation.ephemeral_1h_input_tokens) || 0;
    const w5m = Math.max(0, write - w1h);
    const searches = (u.server_tool_use && u.server_tool_use.web_search_requests) || 0;
    const p = price(this.model, input + read + write);
    let usd = (input * p[0] + output * p[1] + read * p[2] + w5m * p[3] + w1h * p[4]) / 1e6;
    if (u.speed === "fast") usd *= 2;
    if (u.inference_geo && u.inference_geo !== "global" && u.inference_geo !== "not_available") usd *= 1.1;
    usd += searches * SEARCH;
    return { micro: Math.ceil(Math.round(usd * 1e9) / 1e3), requests: 1, input, output, cache_read: read, cache_write: write, searches };
  }
}

function price(model, prompt) {
  const m = String(model || "").toLowerCase();
  let best = null;
  for (const row of PRICES) if (m.startsWith(row[0]) && (!best || row[0].length > best[0].length)) best = row;
  if (!best) return DEAREST;
  return best[2] && prompt > LONG_PROMPT ? best[2] : best[1];
}

// ---- small things -----------------------------------------------------------------------------------------------------

let parsed = { from: null, map: {} };

function tokens(env) {
  if (parsed.from !== env.TOKENS) {
    let map = {};
    try { map = JSON.parse(env.TOKENS) || {}; } catch {}
    parsed = { from: env.TOKENS, map };
  }
  return parsed.map;
}

async function sha256(text) {
  const d = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text));
  return [...new Uint8Array(d)].map(b => b.toString(16).padStart(2, "0")).join("");
}

function monthNow() {
  return new Date().toISOString().slice(0, 7);
}

function nextMonth() {
  const d = new Date();
  return new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth() + 1, 1)).toISOString().slice(0, 10);
}

function capped() {
  const r = error(402, "billing_error",
    `This month's assistant allowance from Studio Quire is used up. It starts again on ${nextMonth()}.`);
  r.headers.set("x-should-retry", "false");
  return r;
}

function error(status, type, message) {
  return json(status, { type: "error", error: { type, message } });
}

function json(status, obj) {
  return new Response(JSON.stringify(obj), { status, headers: { "content-type": "application/json" } });
}
