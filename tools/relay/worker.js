// Studio Quire's relay: Anthropic's Messages API for the holders of a personal token, on the studio's key, each
// token's spending counted from the responses' usage and capped by the month. No request or response content is kept
// or logged: a request is read only to pass it on, and of a response only its usage numbers are looked at. Apart from
// that, a token's holder can send a session's log from Sojourn Access (filtered there), kept 30 days for the studio.
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
  // the studio's own, by the admin secret (tools/relay.py logs)
  ["GET", /^\/relay\/admin\/logs$/, "admin"],
  ["GET", /^\/relay\/admin\/logs\/([\w\-]+)$/, "admin"],
  ["DELETE", /^\/relay\/admin\/logs\/([\w\-]+)$/, "admin"],
];

// a log sent from Sojourn Access (Help > Send a log; already filtered there: other people's words left out): gzipped
// JSON {log, note, app, session, ...}, kept per token for LOG_DAYS days, then deleted
const LOG_MAX = 1_500_000;        // bytes as sent (gzipped)
const LOG_UNPACKED = 24_000_000;  // bytes unpacked
const LOG_NOTE = 4000;            // characters of the person's note
const LOG_DAYS = 30;
const LOGS_A_DAY = 10;            // per token, in any 24 hours
const LOGS_KEPT = 30;             // per token: the oldest go beyond this
const DAY = 86_400_000;

export default {
  async fetch(req, env, ctx) {
    const url = new URL(req.url);
    if (req.method === "GET" && url.pathname === "/") {
      return new Response("Studio Quire's relay for Sojourn Access's assistant. It answers only holders of a token.\n",
        { headers: { "content-type": "text/plain; charset=utf-8" } });
    }
    const route = ROUTES.find(([m, re]) => m === req.method && re.test(url.pathname));
    if (!route) return error(404, "not_found_error", "The relay doesn't answer that.");
    if (route[2] === "admin") return admin(req, env, url.pathname.match(route[1])[1]);
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
  const book = env.LOGBOOK.get(env.LOGBOOK.idFromName(hash));
  const kept = await book.add(meta, body.buffer);
  if (kept.error) return error(429, "rate_limit_error", `${LOGS_A_DAY} logs a day can be sent. Try again tomorrow.`);
  return json(200, { id: kept.id, kept_until: new Date(kept.until).toISOString().slice(0, 10) });
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
  if (!env.ADMIN) return error(503, "api_error", "The relay's admin isn't set up.");
  const given = (req.headers.get("authorization") || "").replace(/^Bearer\s+/i, "");
  if (!given || await sha256(given) !== env.ADMIN) return error(401, "authentication_error", "Not the relay's admin.");
  const all = Object.entries(env.TOKENS ? tokens(env) : {});
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

export class Logbook extends DurableObject {
  constructor(ctx, env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    this.sql.exec("CREATE TABLE IF NOT EXISTS logs (id TEXT PRIMARY KEY, at INTEGER NOT NULL, meta TEXT NOT NULL, body BLOB NOT NULL)");
  }

  async add(meta, body) {
    const now = Date.now();
    this.expire(now);
    const today = this.sql.exec("SELECT COUNT(*) AS n FROM logs WHERE at > ?", now - DAY).one().n;
    if (today >= LOGS_A_DAY) return { error: "too_many" };
    const rand = [...crypto.getRandomValues(new Uint8Array(3))].map(b => b.toString(16).padStart(2, "0")).join("");
    const id = new Date(now).toISOString().replace(/[-:]/g, "").slice(0, 15) + "-" + rand;
    this.sql.exec("INSERT INTO logs (id, at, meta, body) VALUES (?, ?, ?, ?)", id, now, JSON.stringify(meta), body);
    this.sql.exec("DELETE FROM logs WHERE id NOT IN (SELECT id FROM logs ORDER BY at DESC LIMIT ?)", LOGS_KEPT);
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
  }

  /** An alarm when the oldest log's time is up, so a log goes after LOG_DAYS whether or not anything asks again. */
  async schedule() {
    const oldest = this.sql.exec("SELECT MIN(at) AS at FROM logs").one().at;
    if (oldest) await this.ctx.storage.setAlarm(oldest + LOG_DAYS * DAY + 60_000);
  }

  async alarm() {
    this.expire(Date.now());
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
