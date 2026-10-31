// Studio Quire's relay: Anthropic's Messages API for the holders of a personal token, on the studio's key, each
// token's spending counted from the responses' usage and capped by the month. No request or response content is kept
// or logged: a request is read only to pass it on, and of a response only its usage numbers are looked at.
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
];

export default {
  async fetch(req, env, ctx) {
    const url = new URL(req.url);
    if (req.method === "GET" && url.pathname === "/") {
      return new Response("Studio Quire's relay for Sojourn Access's assistant. It answers only holders of a token.\n",
        { headers: { "content-type": "text/plain; charset=utf-8" } });
    }
    const route = ROUTES.find(([m, re]) => m === req.method && re.test(url.pathname));
    if (!route) return error(404, "not_found_error", "The relay doesn't answer that.");
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
