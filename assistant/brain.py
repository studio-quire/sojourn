"""The Claude side: Claude Code sessions (the Agent SDK, on the user's subscription) driving the client through the
tools in tools.py, one session per model.

The person picks a mode (the client's settings; it tells us with `mode` events):
- quick: Haiku takes every request; multi-step jobs go on to Opus (the escalate tool).
- sonnet: the same with Sonnet first.
- opus: Opus takes every request itself: slower to start, surer.
Opus is the planner in the first two: it starts the moment the first model asks for it, with the request.
Which models: MODELS below (SOJOURN_HAIKU / SOJOURN_SONNET / SOJOURN_OPUS = a model id override them); why those, in
docs/assistant.md. Each one's effort is the person's setting (medium by default), switched live: set_efforts.

Opus and the mode's first model are kept warm (connected, and reconnected in the background when their context is
dropped); another session lingers until it's idle, then closes. They have Claude Code's own tools (files, shell, web,
helpers) in auto mode under harness.py's deny rules and hooks, our tools (tools.py, build.py, jobs.py's own), and no
settings files, CLAUDE.md or plugins, nor do they save transcripts. All know the client's agent API from the start (its
op list goes into their instructions), so client_api needs no lookups. Their memory (home.py: files in their own
folder) is shared: its index goes into each session's next request whenever it has changed. Long work goes to
background jobs (jobs.py): the assistant's own, each a session it starts, briefs and steers.
Their context is the conversation of this sitting; after 15 idle minutes it's dropped, so other people's words don't
linger. Exchanges a session didn't see itself (another model answered: a handover, a mode switch) are carried into its
next request, the last four at most.
Long sittings (the user's direction, 2026-10-26): every session has the models' full 1M-token context, and nothing is
compacted automatically. A session past HANDOFF_AT writes its own handoff (memory notes, and handoff-<role>.md in its
folder) in one more turn after the request it's on, then closes; the next request starts a fresh session that gets the
note with the sitting's recent exchanges. A job gets the same as an instruction after a tool call, updates its notes and
ends its turn, and carries on in a fresh session from them (jobs.py). docs/assistant.md > Long sessions.
"""
import asyncio
import json
import math
import os
import re
import time
from pathlib import Path

from claude_agent_sdk import (AssistantMessage, ClaudeAgentOptions, ClaudeSDKClient, ResultMessage, TextBlock,
                              ToolUseBlock)
from claude_agent_sdk.types import StreamEvent

import build as buildmod
import harness
import home as homemod
import jobs as jobmod
import privatetools
import provider as providermod
import tools as toolmod
from api import ApiError

HERE = Path(__file__).resolve().parent
IDLE_RESET = 15 * 60
# context, in tokens: the models' window, and where a session hands off to a fresh one (a request session after its
# request, a job after its next tool call); past HARD_AT a request session is told mid-turn too
CONTEXT_WINDOW = 1_000_000
HANDOFF_AT = int(os.environ.get("SOJOURN_HANDOFF_AT") or 800_000)
HARD_AT = max(HANDOFF_AT, int(os.environ.get("SOJOURN_HANDOFF_HARD_AT") or 900_000))


def handoff_at():
    """The handoff line for the model answering now: HANDOFF_AT, or 80% of a smaller window (another maker's model
    through OpenRouter)."""
    return min(HANDOFF_AT, int(providermod.current.window * 0.8))


def hard_at():
    return max(handoff_at(), min(HARD_AT, int(providermod.current.window * 0.9)))


def model_env(name, default):
    """Another model id for a run (tests); an old-style ":effort" after it is ignored, the effort is the setting's."""
    v = (os.environ.get(name) or "").split(":", 1)[0].strip()
    return v or default


MODELS = {
    "haiku": model_env("SOJOURN_HAIKU", "claude-haiku-5-5[1m]"),
    "sonnet": model_env("SOJOURN_SONNET", "claude-sonnet-5-5[1m]"),
    "opus": model_env("SOJOURN_OPUS", "claude-opus-5-5[1m]"),
}
# each model's effort is the person's setting (the client's, sent with the hello and an `effort` event when it changes)
EFFORTS = jobmod.EFFORTS
DEFAULT_EFFORT = "medium"
# the session that takes a request first, per mode; multi-step jobs always end with opus
MODES = {"quick": "haiku", "sonnet": "sonnet", "opus": "opus"}
PLANNER = "opus"


def label(model):
    """claude-opus-5-5[1m] -> opus 5.5, for the panel."""
    parts = re.sub(r"\[\w+\]$", "", model).removeprefix("claude-").split("-")
    return parts[0] + (" " + ".".join(parts[1:]) if len(parts) > 1 and all(p.isdigit() for p in parts[1:]) else "")

# pumps the assistant can't use through client_api (the panel's own; snapshots by file path), or that a tool of its own
# covers (show, media, ...), left out of its list; and single ops a tool covers (tools.py, moretools.py), so the list
# holds only what client_api is for and each thing has one way to it
HIDDEN_PUMPS = {"SojournAssistant", "LLViewerWindow", "SojournShow", "SojournMedia", "SojournCapture", "SojournReport", "SojournSearch",
                "SojournEnvironment", "SojournVoice", "SojournReader"}
HIDDEN_OPS = {(pump, op) for pump, ops in {
    "SojournLand": "parcel region covenant access allow unallow ban unban freeze buyPass buy set topScripts topColliders "
                   "returnObjects regionDebug terraform holdings",
    "SojournPeople": "displayName setDisplayName notes setNotes money status setResponse groupChat snoozeGroupChat setGroupChat "
                     "moderateGroupChat",
    "SojournBuild": "history undo redo scripts recompile resetScripts runScripts removeScripts scriptInfo landImpact",
    "SojournAo": "status on stands select next previous play set poseStand poseStandOff poses",
    "SojournAvatars": "render resetSkeleton refreshAttachments resyncAnimations",
    "SojournInventory": "picture favorites favorite thumbnail",
    "SojournMap": "where stats show hide friends",
    "SojournBeacons": "track untrack",
    "SojournSafety": "derender rerender hidden sounds blockSound particles blockParticleOwner releaseKeys bumps",
    "SojournFeed": "lines keys",
}.items() for op in ops.split()}
PROMPTS = Path(__file__).resolve().parents[1] / "local" / "assistant" / "prompts"


def workdir():
    # its scratch folder (generated textures, meshes, LSL); no CLAUDE.md is read from here up (setting_sources=[])
    d = homemod.work_dir()
    d.mkdir(parents=True, exist_ok=True)
    return str(d)


# Claude Code's own tools while incognito lets it see: reading and looking things up, nothing that writes (no shell,
# file writes, helpers or todo lists); the sojourn tools go through the client as usual
SEEING_TOOLS = ["Read", "Glob", "Grep", "WebSearch", "WebFetch"]


def incognito_dir():
    """A seeing spell's working folder, its sessions' cwd: emptied when the spell ends, with what Claude Code keeps
    for that folder under ~/.claude/projects (big tool results)."""
    return homemod.work_dir().parent / "incognito"


def wipe_incognito(log=print):
    import shutil
    d = incognito_dir()
    for p in (d, Path.home() / ".claude" / "projects" / harness.slug(d)):
        if p.exists():
            shutil.rmtree(p, ignore_errors=True)
            if p.exists():
                log("incognito: couldn't clear a working folder")


def api_catalog(api):
    """The client's agent API as compact text (one line a pump, one an op), or None if it can't be read."""
    try:
        pumps = api.cmd("_command", "getAPIs")
        lines = []
        for p in sorted(pumps):
            if p in HIDDEN_PUMPS:
                continue
            d = api.cmd("_command", "getAPI", api=p)
            lines.append(f"{p}: {d.get('desc') or pumps[p].get('desc') or ''}")
            for op, o in (d.get("ops") or {}).items():
                if (p, op) not in HIDDEN_OPS:
                    lines.append(f"  {op} {o.get('desc') or ''}".rstrip())
        return "\n".join(lines)
    except (ApiError, AttributeError, TypeError):
        return None


class Session:
    def __init__(self, name, model, effort, kit, escalate, log, extras=None, turns=40):
        self.name, self.model, self.effort, self.kit, self.escalate, self.log = name, model, effort, kit, escalate, log
        self.extras = extras      # (session) -> (more MCP servers, their tool names, hooks, settings json): Brain.extras
        self.turns = turns        # None: no limit (a job)
        self.disallowed = []      # Claude Code tools it doesn't get (a job without helpers: Agent)
        self.memory_seen = None   # the memory index's digest as this session's conversation last had it
        self.label = label(model)
        self.system = None
        self.help = True          # api_help as a tool (only when the op list couldn't go into the instructions)
        self.client = None
        self.used = 0.0
        self.busy = False
        self.tail = None          # the rest of a response still arriving after ask returned early (a handover)
        self.unread = False       # a turn was sent whose result hasn't been read: its messages would answer the next one
        self.on_lost = None       # () -> None: its conversation was dropped outside a close the Brain made
        self.private = False      # incognito's configuration: "blind" (privatetools.py's tools only) or "sees" (SEEING_TOOLS)
        self.synced = 0           # the sitting's exchanges up to this one are in its conversation (Brain.recent)
        self.lock = asyncio.Lock()
        self.lost_upto = 0        # exchanges up to this one were its own but are gone from its conversation (a restart)
        self.context = 0          # tokens in its context as of its last model call (what it sent, and what it wrote)
        self.handoff_asked = False  # told (after a tool call) to hand off: it does so in the turn it's on
        self.handing_off = False  # its handoff turn is running: nothing interrupts it
        self.handoff_task = None  # the handoff after a request: the next request waits for it
        self.up_effort = None     # the effort its connection was made with (effort can change since: the setting)

    def stale(self):
        """Connected at an effort the setting has since changed (not during its handoff turn, which needs its
        conversation)."""
        return self.client is not None and self.up_effort != self.effort and not self.handing_off

    async def ensure(self):
        async with self.lock:
            if self.client and time.time() - self.used > IDLE_RESET:
                await self._close()
            elif self.stale():
                self.log(f"{self.name} session: effort {self.up_effort} -> {self.effort}, starting it again")
                await self._close()
                if self.on_lost:
                    self.on_lost()   # what it had seen is gone: the sitting's recent exchanges go in with its next request
            if not self.client:
                await self._connect()

    async def _connect(self):
        blind, sees = self.private == "blind", self.private == "sees"
        server, names = (privatetools.build if blind else toolmod.build)(self.kit, self.escalate, self.help)
        servers, more, hooks, settings = self.extras(self) if self.extras else ({}, [], None, None)
        if sees:
            incognito_dir().mkdir(parents=True, exist_ok=True)
        # the instructions from a file: on the command line they'd count against Windows' 32,767-character limit, which
        # the API's list alone nearly fills (claude then fails to start with "the filename or extension is too long")
        prompt_file = PROMPTS / f"{self.name}-{os.getpid()}.md"
        prompt_file.parent.mkdir(parents=True, exist_ok=True)
        prompt_file.write_text(self.system or "", encoding="utf-8")
        effort = self.effort
        prov = providermod.current
        self.label = prov.label(self.model) or label(self.model)
        opts = ClaudeAgentOptions(
            system_prompt={"type": "file", "path": str(prompt_file)},
            model=self.model,
            # Claude Code's own tools (files, shell, web, subagents) under auto mode and harness.py's deny rules
            allowed_tools=names + more,
            disallowed_tools=self.disallowed,
            # incognito, blind: no files, shell, web or helpers at all; seeing: reading and the web, nothing that writes
            tools=[] if blind else SEEING_TOOLS if sees else None,

            mcp_servers={"sojourn": server, **servers},
            hooks=hooks,
            settings=settings,
            strict_mcp_config=True,            # not the account's connectors
            setting_sources=[],                # no settings files, hooks or CLAUDE.md but harness.py's
            permission_mode="auto",
            cwd=str(incognito_dir()) if sees else workdir(),
            cli_path=providermod.cli(),
            # the person's provider (provider.py): for a key, the loopback proxy and its token, never the key
            env={**harness.env(), **prov.env()},
            extra_args={"no-session-persistence": None, "effort": effort},
            include_partial_messages=True,     # for the time to the model's first token
            # a picture of the view or a big file read comes back as one message; the SDK's 1 MB default killed the
            # session's reader outright on one (2026-10-27)
            max_buffer_size=64 * 1024 * 1024,
            max_turns=self.turns,
        )
        t = time.time()
        client = ClaudeSDKClient(opts)
        await client.connect()
        self.client, self.synced, self.memory_seen = client, 0, None
        self.context, self.handoff_asked, self.up_effort = 0, False, effort
        self.used = time.time()
        self.log(f"{self.name} session up ({self.model}{f' as {self.label}' if prov.single else ''}, {effort}, "
                 f"{prov.kind}) in {time.time() - t:.1f}s")

    async def close(self):
        async with self.lock:
            await self._close()

    async def _close(self):
        if self.tail:
            await self.settle()
        await self._close_now()
        self.unread = False

    async def settle(self):
        """Let a handed-over response finish before the session takes anything new, and read out a turn that was cut
        off before its end (a request stopped and cancelled, a tail that ran too long). The SDK keeps one queue of
        messages per session, so an unread turn's messages, ending in its error_during_execution result, would
        otherwise come back as the next request's answer (checked 2026-10-26: an abandoned turn's reply was returned
        for the question after it)."""
        if self.tail:
            try:
                await asyncio.wait_for(self.tail, 30)
            except Exception:
                await self.interrupt()
            self.tail = None
        if self.unread and self.client:
            await self.interrupt()
            try:
                await asyncio.wait_for(self._read_out(), 15)
            except Exception as e:
                self.log(f"{self.name} session: a cut-off turn didn't end ({e!r}); starting it afresh")
                await self._close_now()
                if self.on_lost:
                    self.on_lost()   # what it had seen is gone: the sitting's recent exchanges go in with its next request
        self.unread = False

    async def _read_out(self):
        async for m in self.client.receive_response():
            if isinstance(m, ResultMessage):
                self.unread = False

    async def _close_now(self):
        if self.client:
            try:
                await self.client.disconnect()
            except Exception:
                pass
            self.client = None

    async def ask(self, message, on_handover=None):
        """Run one request to its end. Returns the reply (text after the last tool call), the tools used, and
        timings: first_s (to the model's first token), turns (model calls). If on_handover is given and the model
        calls escalate, it's called with the reason at once and ask returns without waiting for the rest."""
        await self.settle()
        await self.ensure()
        self.busy = True
        self.used = t0 = time.time()
        self.unread = True
        await self.client.query(message)
        texts, used, info = [], [], {"first_s": None, "turns": 0, "error": None}
        stream = self.client.receive_response()
        try:
            async for m in stream:
                if isinstance(m, StreamEvent):
                    if m.event.get("type") == "message_start":
                        info["turns"] += 1
                        if info["first_s"] is None:
                            info["first_s"] = round(time.time() - t0, 2)
                        u = (m.event.get("message") or {}).get("usage") or {}
                        self.context = sum(int(u.get(k) or 0) for k in ("input_tokens", "cache_creation_input_tokens",
                                                                          "cache_read_input_tokens"))
                    elif m.event.get("type") == "message_delta":
                        self.context += int(((m.event.get("usage") or {}).get("output_tokens")) or 0)
                elif isinstance(m, AssistantMessage):
                    for b in m.content:
                        if isinstance(b, ToolUseBlock):
                            name = re.sub(r"^mcp__\w+?__", "", b.name)
                            used.append(name)
                            texts = []
                            if name == "escalate" and on_handover:
                                on_handover((b.input or {}).get("reason") or "multi-step")
                                self.tail = asyncio.create_task(self._drain(stream))
                                return "", used, info
                        elif isinstance(b, TextBlock) and b.text.strip():
                            texts.append(b.text.strip())
                elif isinstance(m, ResultMessage):
                    self.unread = False
                    if m.is_error:
                        info["error"] = m.subtype
        finally:
            if not self.tail:
                self.busy = False
                self.used = time.time()
        return " ".join(texts).strip(), used, info

    async def _drain(self, stream):
        try:
            async for m in stream:
                if isinstance(m, ResultMessage):
                    self.unread = False
        finally:
            self.busy = False
            self.used = time.time()

    def handoff_due(self, mid_turn):
        """The instruction to hand off, once, when its context has grown past the line (mid-turn: after a tool call,
        where a job hands off and a request session only past HARD_AT), else None."""
        line = handoff_at() if (mid_turn is False or self.turns is None) else hard_at()
        if self.handoff_asked or self.context < line:
            return None
        self.handoff_asked = True
        return self.context

    async def interrupt(self):
        if self.handing_off:   # a handoff turn finishes: what it writes is all the next session has
            return
        if self.client:
            try:
                await self.client.interrupt()
            except Exception:
                pass


class Brain:
    def __init__(self, api, kit, vr, mode="quick", log=print, speaker=None):
        self.api, self.kit, self.vr, self.log = api, kit, vr, log
        self.app = None       # which app the person uses: None the VR client, "access" Sojourn Access (the first hello says)
        self.speaker = speaker
        self.provider = providermod.current   # whose models answer (the person's provider: provider.py)
        self.provider.log = log
        self.prefs = {}       # the person's choices: name, character, speak, voice (the client's settings)
        self.persona = (None, None)
        self.persona_pending = False
        self.catalog = None
        self.home = homemod.Home(log=log)
        self.memory = True    # the person's switch in the settings menu (prefs "memory"); off: no folder, nothing kept
        self.sessions = {n: Session(n, MODELS[n], DEFAULT_EFFORT, kit, n != PLANNER, log, self.extras) for n in MODELS}
        for s in self.sessions.values():
            s.on_lost = lambda s=s: setattr(s, "lost_upto", self.seq)
        self.stopping = False   # stop() is cutting the request in progress short
        self.jobs = jobmod.JobRunner(self)
        self.mode = mode if mode in MODES else "quick"
        self.recent = []      # this sitting's exchanges: {seq, t, request, reply, by}
        self.seq = 0
        self.task = None
        self.timings = HERE.parent / "local" / "assistant" / "timings.jsonl"
        # private mode (private.py says when): {since, paused}, the spell's own exchanges kept apart and dropped at its
        # end, (minutes, when it ended) for the first requests after, and sessions switching (requests wait for it)
        self.private = None
        self.recent_private = []
        self.private_after = None
        self.ready = asyncio.Event()
        self.ready.set()
        self.waiting = False
        self.started = False

    def first(self):
        """The session that takes a request first: the mode's; with another maker's model through OpenRouter, the one
        model takes everything itself (no handover to the same model)."""
        return PLANNER if self.provider.single else MODES[self.mode]

    def wanted(self):
        """The sessions kept warm: the mode's first and the planner (none while no provider works)."""
        return {self.first(), PLANNER} if self.provider.ready else set()

    def persona_text(self):
        """The person's choices about how the assistant comes across, as what they'd like (it's still Claude, operating
        Sojourn for them; they can change or clear these any time)."""
        name, character = self.persona
        if not name and not character:
            return ""
        out = "\nThe person's choices about you (theirs to change or clear whenever they like; they don't change what you do or what's true):\n"
        if name:
            out += f"- They'd like you to use the name {name} with them: answer to it, and use it if you refer to yourself.\n"
        if character:
            out += (f"- They'd like your replies to have this character, in their words: \"{character}\". Let it set the tone; "
                    "the replies stay as short and plain-text as above.\n")
        return out

    def memory_text(self):
        if self.memory:
            return "\n" + (HERE / "prompt-memory.md").read_text(encoding="utf-8").rstrip().replace("{home}", str(self.home.root)) + "\n"
        return ("\nMemory is off (the person's switch in the settings menu): nothing is kept between sittings. If they "
                "ask you to remember something, say memory is off and the settings menu turns it on.\n")

    def extras(self, s):
        """What a session gets beyond tools.py's tools: its own (memory housekeeping, jobs), building from a spec, the
        hooks that keep its status line and guard its shell and helpers, and the deny rules and auto mode's rules."""
        job = getattr(s.kit, "job", None)
        guard = harness.Guard(s.kit.status, self.log, inbox=job.take_told if job else None,
                              plan_first=job.plan_needed if job else None,
                              handoff=lambda: self.handoff_instruction(s),
                              only=SEEING_TOOLS if s.private == "sees" else None)
        if s.private == "sees":
            settings = harness.settings_file(self.home.root, incognito_dir(), incognito=True)
        else:
            settings = harness.settings_file(self.home.root, homemod.work_dir())
        if s.private:   # no memory housekeeping, jobs or building while incognito
            return {}, [], guard.hooks(), settings
        own, own_names = jobmod.own_server(self, s)
        build, build_names = buildmod.build_server(s.kit)
        return {"own": own, "build": build}, own_names + build_names, guard.hooks(), settings

    def handoff_file(self, s):
        return self.home.root / f"handoff-{s.name}.md"

    def handoff_instruction(self, s):
        """After a tool call: the words that tell s to hand off now, if its context has grown past the line."""
        n = None if s.private else s.handoff_due(mid_turn=True)   # private: nothing written
        if n is None:
            return None
        self.log(f"{s.name} session at {n} tokens: handing off")
        if getattr(s.kit, "job", None):
            return (f"[harness] Your context is at {n:,} tokens of about {CONTEXT_WINDOW:,}, and nothing is compacted "
                    "automatically: hand off now. Bring your notes up to date (the plan, what's done with the objects' ids, "
                    "what's left, anything to clean up, what you were in the middle of), so a fresh session can carry on "
                    "from them alone; then end your turn with just the words: handing off.")
        return (f"[harness] Your context is at {n:,} tokens of about {CONTEXT_WINDOW:,}: finish this request in as few "
                f"steps as you can, and before your reply write your handoff: lasting things into your memory as usual, "
                f"and {self.handoff_file(s)} as the handoff for the session after this one (see Long sessions).")

    async def handoff(self, s):
        """s is past the line: one more turn writes its handoff, then it closes; the next request starts afresh."""
        if s.handoff_asked and s.context >= handoff_at() and self.handoff_file(s).exists():
            pass   # it wrote the handoff in the turn it was on
        else:
            s.handoff_asked = True
            s.handing_off = True
            note = self.handoff_file(s)
            try:
                self.log(f"{s.name} session at {s.context} tokens: its handoff")
                await s.ask(f"[harness] This session's context is at {s.context:,} tokens of about {CONTEXT_WINDOW:,}, and "
                            "nothing is compacted automatically, so it ends after this turn and a fresh session takes the "
                            "next request. Write your handoff now: "
                            + ("anything lasting into your memory as usual, and " if self.memory else
                               "(memory is off, so only this note, which is deleted once read) ")
                            + f"{note} for the session after you: what the person is doing and after right now, what you "
                            "said you'd do or are waiting on, what recent requests later ones may refer back to, and what's "
                            "on screen and why. A few short lines, true now; it's read once, then deleted. Reply only: "
                            "handed off.")
            except Exception as e:
                self.log(f"{s.name} handoff failed: {e!r}")
            finally:
                s.handing_off = False
        await s.close()
        s.lost_upto = self.seq
        self.log(f"{s.name} session handed off" + ("" if self.handoff_file(s).exists() else " (no note written)"))
        if s.name in self.wanted():
            await self._bring_up(s)

    def handoff_note(self, s):
        """The handoff the session before s wrote, for its first request (then deleted: it's read once)."""
        f = self.handoff_file(s)
        if s.synced or not f.exists():
            return ""
        try:
            text = f.read_text(encoding="utf-8", errors="replace").strip()
            f.unlink()
        except OSError:
            return ""
        return f"The session before this one handed off, leaving you this note:\n{text}\n" if text else ""

    def job_system(self, helpers=True):
        """A background job's instructions: the assistant's own, then how a job works."""
        api = ("\nThe client's agent API, for client_api (each pump, then its ops: {arguments} -> what comes back):\n"
               + self.catalog + "\n") if self.catalog else ""
        job = (HERE / "prompt-job.md").read_text(encoding="utf-8").rstrip().replace("{work}", str(homemod.work_dir()))
        job = job.replace("{helpers}", "Helpers (the Agent tool) can take independent parts in parallel (a texture "
                          "while you build the frame): give each everything it needs (coordinates, sizes, conventions, "
                          "which tools)." if helpers else "This job does its work itself, without helpers.")
        return self.base_prompt() + "\n" + job + "\n" + api

    def job_finished(self, job, text):
        """A job ended: the person hears of it once no request is using the panel; the models know of it."""
        self.seq += 1
        self.recent = (self.recent + [{"seq": self.seq, "t": time.time(), "request": f"(the background job {job.id})",
                                       "reply": text, "by": "job"}])[-8:]
        asyncio.create_task(self.notify(text))

    def memory_note(self, s):
        """The memory's index for s's next request, when its conversation doesn't have it as it stands now."""
        if not self.memory:
            return ""
        try:
            text, digest = self.home.snapshot()
        except OSError as e:
            self.log(f"memory unreadable: {e!r}")
            return ""
        if digest == s.memory_seen:
            return ""
        s.memory_seen = digest
        return text + "\n"

    async def forget_all(self):
        """The person cleared the memory in the menu (the client deleted the files): sessions start again, so nothing
        of it lingers in a conversation, and the sitting's exchanges go too."""
        try:
            n = self.home.clear()   # anything the client couldn't
        except OSError:
            n = 0
        self.recent = []
        self.log(f"memory cleared{f' ({n} more files)' if n else ''}")
        await self.jobs.stop_all()
        await self.stop()
        await self.restart_sessions()

    def app_text(self):
        """What the app the person uses means for the replies (Sojourn Access: no view, their screen reader)."""
        return ("\n" + (HERE / "prompt-access.md").read_text(encoding="utf-8").rstrip() + "\n") if self.app == "access" else ""

    def base_prompt(self):
        return ((HERE / "prompt.md").read_text(encoding="utf-8").rstrip() + "\n" + self.app_text() + "\n"
                + (HERE / "prompt-powers.md").read_text(encoding="utf-8").rstrip().replace("{work}", str(homemod.work_dir()))
                + "\n" + self.memory_text() + self.persona_text() + self.provider.prompt_note())

    def prompts(self, catalog):
        self.catalog = catalog
        private = ("sees" if self.private.get("sees") else "blind") if self.private else False
        # incognito, blind: its own instructions (prompt-incognito.md), the person's choices, and no op list (no
        # client_api); seeing: the usual instructions but those for its own powers (memory, jobs, files, the shell),
        # what incognito means for it (prompt-incognito-sees.md), the person's choices and the op list
        if private == "blind":
            base = ((HERE / "prompt-incognito.md").read_text(encoding="utf-8").rstrip() + "\n" + self.persona_text()
                    + self.provider.prompt_note())
        elif private == "sees":
            base = ((HERE / "prompt.md").read_text(encoding="utf-8").rstrip() + "\n" + self.app_text() + "\n"
                    + (HERE / "prompt-incognito-sees.md").read_text(encoding="utf-8").rstrip() + "\n" + self.persona_text()
                    + self.provider.prompt_note())
        else:
            base = self.base_prompt()
        api = ("\nThe client's agent API, for client_api (each pump, then its ops: {arguments} -> what comes back):\n"
               + catalog + "\n") if catalog and private != "blind" else ""
        for s in self.sessions.values():
            s.private = private
            escalate = s.escalate and not self.provider.single
            s.system = base + (
                "- escalate hands a request to a planner (call it first, then stop; it takes it from there) when it "
                "needs several steps or careful judgement and should be done now, while they wait. Building more than "
                "one plain prim, or other long work, is better as a job.\n" if escalate else
                "- Requests come to you straight from the person, or handed over by a quicker model when they need "
                "several steps. Do simple ones in a call or two; work through the bigger ones, then give the short "
                "reply.\n") + api
            s.help = catalog is None and private != "blind"

    async def warm(self):
        catalog = await asyncio.to_thread(api_catalog, self.api)
        if catalog is None:
            self.log("couldn't read the agent API's op list; api_help stays a tool")
        if not (self.private and self.private.get("sees")):   # left by a seeing spell the sidecar didn't see end
            await asyncio.to_thread(wipe_incognito, self.log)
        self.prompts(catalog)
        self.started = True
        await self.jobs.startup()
        await asyncio.gather(*(self.sessions[n].ensure() for n in self.wanted()))
        asyncio.create_task(self.keep_warm())

    def apply_prefs(self, prefs):
        """The client's settings for the assistant (with every hello, and at once when they change)."""
        if not isinstance(prefs, dict) or prefs == self.prefs:
            return
        self.prefs = prefs
        if self.speaker:
            self.speaker.set_voice(prefs.get("voice"))
            if prefs.get("speak"):
                self.speaker.warm()
            else:
                self.speaker.stop()
        persona = (prefs.get("name") or None, prefs.get("character") or None)
        memory = prefs.get("memory") is not False
        if persona != self.persona or memory != self.memory:
            if memory != self.memory:
                self.log(f"memory {'on' if memory else 'off'}")
            self.persona, self.memory = persona, memory
            self.log(f"persona: name {'set' if persona[0] else 'none'}, character {'set' if persona[1] else 'plain'}")
            # the instructions change: sessions start again with them (after the request in progress, if any)
            if self.task and not self.task.done():
                self.persona_pending = True
            else:
                asyncio.create_task(self.restart_sessions())

    async def restart_sessions(self):
        self.persona_pending = False
        self.prompts(self.catalog)
        for s in self.sessions.values():
            if s.client:
                await s.close()
                s.lost_upto = self.seq   # its conversation is gone: what came before goes in with the next request
        await asyncio.gather(*(self._bring_up(self.sessions[n]) for n in self.wanted()))

    async def set_provider(self, prefs):
        """The person changed the provider, its model or the key: it's checked, then the sessions start again on it
        once the request in progress is done (none start if it doesn't work: requests are answered with how to switch
        it on). A job pauses if whose models answer changed under it; the person can carry it on."""
        before = (self.provider.kind, self.provider.single)
        await asyncio.to_thread(self.provider.configure, prefs)
        while self.task and not self.task.done():
            await asyncio.sleep(0.5)
        if before != (self.provider.kind, self.provider.single) and (titles := await self.jobs.pause_all("provider")):
            asyncio.create_task(self.notify(f"{' and '.join(titles)} paused while the assistant's provider changed; say when to carry on."))
        self.prompts(self.catalog)
        await self.restart_sessions()

    def set_mode(self, mode):
        """The person picked another mode: its first model gets warm now, so the next request needn't wait."""
        if mode not in MODES or mode == self.mode:
            return
        self.mode = mode
        s = self.sessions[self.first()]
        self.log(f"mode: {mode} ({s.label} first)")
        if not s.client and self.provider.ready:
            asyncio.create_task(self._bring_up(s))

    def set_efforts(self, efforts):
        """The person's effort per model (the client's setting: {haiku, sonnet, opus}, with every hello and at once when
        it changes). A changed session comes back up at the new effort before its next turn: now if nothing is using
        it, else after the request in progress, which finishes as it started."""
        if not isinstance(efforts, dict):
            return
        changed = False
        for name, s in self.sessions.items():
            e = efforts.get(name)
            if e in EFFORTS and e != s.effort:
                s.effort, changed = e, True
                self.log(f"effort: {name} {e}")
        if changed and not (self.task and not self.task.done()):
            self.refresh_efforts()

    def refresh_efforts(self):
        """Bring the warm sessions whose effort changed back up at it, those not at work or handing off."""
        for n in self.wanted():
            s = self.sessions[n]
            if s.stale() and not s.busy and not (s.handoff_task and not s.handoff_task.done()):
                asyncio.create_task(self._bring_up(s))

    async def _bring_up(self, s):
        try:
            await s.ensure()
        except Exception as e:
            self.log(f"{s.name} session didn't come up: {e!r}")

    async def keep_warm(self):
        """Drop a session's context after IDLE_RESET: reconnect the ones in use at once, so the next request doesn't
        wait for it; close the others. A warm session whose effort changed while it was busy comes back at it too."""
        while True:
            await asyncio.sleep(30)
            if not (self.task and not self.task.done()):
                self.refresh_efforts()
            for s in self.sessions.values():
                if s.client and not s.busy and time.time() - s.used > IDLE_RESET:
                    try:
                        if s.name in self.wanted():
                            await s.ensure()
                        else:
                            await s.close()
                            self.log(f"{s.name} session closed (idle, not in use)")
                    except Exception as e:
                        self.log(f"{s.name} session didn't come back: {e!r}")

    def carried(self, s):
        """This sitting's recent exchanges that s hasn't seen (another model answered them), the last four."""
        cutoff = time.time() - IDLE_RESET
        ex = [e for e in (self.recent_private if self.private else self.recent) if e["seq"] > s.synced and (e["by"] != s.name or e["seq"] <= s.lost_upto) and e["t"] > cutoff][-4:]
        return "".join(f"- they asked: {e['request']}\n  you answered: {e['reply']}\n" for e in ex)

    async def where(self):
        """Where they are and which way they're looking, from three ops at once (each capped: the context line
        shouldn't hold up a request)."""
        async def op(pump, name):
            try:
                return await asyncio.wait_for(asyncio.to_thread(self.api.cmd, pump, name, 1), 1.5)
            except (ApiError, asyncio.TimeoutError):
                return None
        pos, parcel, cam = await asyncio.gather(op("LLAgent", "getPosition"), op("TesselAgent", "parcel"),
                                                op("LLAgent", "getCamera"))
        try:
            pos = pos["region"]
        except (TypeError, KeyError):
            return "unknown"
        region = ((parcel or {}).get("region") or {}).get("name") or "?"
        where = f"{region} {pos[0]:.0f},{pos[1]:.0f},{pos[2]:.0f} (parcel: {((parcel or {}).get('parcel') or {}).get('name') or '?'})"
        try:
            # which way they're looking (the camera: the head in the headset), and the spot a step and a half ahead
            fx, fy = cam["focus_pos"][0] - cam["camera_pos"][0], cam["focus_pos"][1] - cam["camera_pos"][1]
            n = math.hypot(fx, fy) or 1
            fx, fy = fx / n, fy / n
            heading = (90 - math.degrees(math.atan2(fy, fx))) % 360
            compass = "north northeast east southeast south southwest west northwest".split()[round(heading / 45) % 8]
            where += (f"; facing {compass} (heading {heading:.0f}); 1.5 m ahead of them is "
                      f"{pos[0] + 1.5 * fx:.1f},{pos[1] + 1.5 * fy:.1f},{pos[2]:.1f}")
        except (TypeError, KeyError):
            pass
        return where

    async def shown(self):
        """What the assistant has on screen (its show tool), so it can change or clear it."""
        try:
            r = await asyncio.wait_for(asyncio.to_thread(self.api.cmd, "SojournShow", "list", 1), 1)
        except (ApiError, asyncio.TimeoutError):
            return ""
        items = r.get("shown") or []
        hidden = ("; they've hidden their interface (X or Tab): your panel, choices, requests to point and what you show "
                  "wait unseen until they bring it back; only confirmations show") if r.get("interface_hidden") else ""
        if not items:
            return hidden
        return hidden + "; on screen: " + ", ".join(f"{i['id']} ({i.get('at')}: {'/'.join(i.get('blocks') or [])}"
                                            f"{', not in view' if i.get('not_in_view') else ''}{', done' if i.get('timer_done') else ''})" for i in items)

    async def notify(self, text):
        """Something the person should know that isn't an answer (a timer ran out): on the panel once no request is
        using it, and spoken if they like."""
        while self.task and not self.task.done():
            await asyncio.sleep(0.5)
        self.api.panel(heard=None, status=None, result=text, working=False, choices=[], prompt=None)
        if self.speaker and self.prefs.get("speak"):
            self.speaker.say(text)

    async def context(self, target, jobs=True):
        """A request's context line (jobs: with the jobs and where they stand; not for a job's own notes)."""
        where, shown = await asyncio.gather(self.where(), self.shown())
        if jobs and (js := self.jobs.summary()):
            shown += f"; jobs: {js}"
        looking = target if target and target.get("looking") else None
        if looking:
            target = None
        if not target:
            pointing = "nothing"
            if looking:
                # the headset with its pointer off: what's in the middle of their view, a hint and no more
                pointing += (f" (in the middle of their view: {looking['kind']} \"{looking.get('name')}\" id={looking.get('id')}"
                             + (f" worn by avatar {looking['worn_by']}" if looking.get("worn_by") else "")
                             + f", {looking.get('distance')} m away)")
        elif target.get("kind") == "ground":
            p = target["position"]
            pointing = f"the ground at {p[0]:.0f},{p[1]:.0f},{p[2]:.0f} ({target.get('distance')} m away)"
        else:
            pointing = (f"{target['kind']} \"{target.get('name')}\" id={target.get('id')}"
                        + (f" (part of linkset {target['root_id']})" if target.get("root_id") else "")
                        + (f" worn by avatar {target['worn_by']}" if target.get("worn_by") else "")
                        + f", {target.get('distance')} m away at {target.get('position')}")
        # the names around them (hearing.Words), so a misheard one can be matched: "TESL" -> Tessel
        words = getattr(self.kit, "words", None)
        names = words.names(40) if words else []
        heard = (f"; names they might say (people near them, friends, places, their things, names in your memory; "
                 f"not who is here, and the words heard may misspell one): {', '.join(names)}") if names else ""
        return (f"[time {time.strftime('%H:%M')}; at {where}; "
                f"{self.where_word()}; pointing at {pointing}{shown}{heard}]\n")

    def where_word(self):
        return ("in Sojourn Access (no view shown; your replies go to their screen reader)" if self.app == "access"
                else "in the VR headset" if self.vr else "at the desktop")

    async def handle(self, request, target, timing):
        """One request start to finish, the panel kept up to date."""
        api, kit = self.api, self.kit
        # what's only looked at (not pointed at) never stands for "that" in the tools
        kit.target, kit.escalated, kit.actions = (None if target and target.get("looking") else target), None, []
        if not self.provider.ready:
            # no provider that works: said plainly (how to switch it on), and nothing else is held up
            reply = self.provider.how()
            self.log(f"a request with no provider working ({self.provider.state}): told how to switch it on")
            api.panel(heard=request, result=reply, working=False, status=None, hearing=False, choices=[], prompt=None)
            if self.speaker and self.prefs.get("speak"):
                self.speaker.say(reply)
            return
        first = last = self.sessions[self.first()]
        mode = self.mode
        api.panel(heard=request, status="thinking", result=None, working=True, hearing=False, choices=[], prompt=None,
                  model=first.label)
        reply, used = "", []
        t0 = timing.get("speech_end") or timing.get("typed")
        rec = {}
        if not self.ready.is_set():   # the sessions are switching for incognito: this one goes to the new ones
            self.waiting = True
            try:
                await self.ready.wait()
            finally:
                self.waiting = False
        was_private = bool(self.private)
        blind = was_private and not self.private.get("sees")

        def job(s, ctx):
            earlier = self.carried(s)
            return (ctx + self.memory_note(s) + ("" if was_private else self.handoff_note(s) + self.after_private(s))
                    + (f"Earlier in this sitting:\n{earlier}\nRequest: " if earlier else "") + request)

        try:
            tc = time.time()
            ctx = await (self.private_context(target) if blind else self.context(target))
            rec["context_s"] = round(time.time() - tc, 2)
            handover = []
            reply, used, info = await first.ask(job(first, ctx), on_handover=handover.append if first.escalate else None)
            if info["error"] and not reply and not used and not handover and not self.stopping:
                # it ended in an error before doing anything (an API error, say): once more, as the person would
                self.log(f"{first.name} ended with {info['error']} before acting; asking again")
                rec["retried"] = info["error"]
                reply, used, info = await first.ask(job(first, ctx), on_handover=handover.append if first.escalate else None)
            rec["first_token_s"] = round(tc + rec["context_s"] + info["first_s"] - t0, 2) if info["first_s"] and t0 else None
            rec["turns"] = info["turns"]
            if handover:
                last = self.sessions[PLANNER]
                rec["handover_s"] = round(time.time() - t0, 2) if t0 else None
                api.panel(status=f"planning: {handover[0]}", model=last.label)
                tp = time.time()
                reply, used2, info2 = await last.ask(job(last, ctx))
                rec["planner_first_token_s"] = round(tp + info2["first_s"] - t0, 2) if info2["first_s"] and t0 else None
                used += used2
                rec["turns"] += info2["turns"]
                info = info2
            if info["error"]:
                rec["error"] = info["error"]
                if self.stopping:
                    reply = "(stopped)"
                elif not reply:
                    self.log(f"{last.name} ended with {info['error']}")
                    reply = "That stopped partway, before I could finish." if used else "That didn't go through. Try again?"
        except asyncio.CancelledError:
            api.panel(working=False, status="stopped")
            raise
        except Exception as e:
            self.log(f"request failed: {type(e).__name__ if was_private else repr(e)}")
            reply = f"Something went wrong: {e}"
            # the session may be dead (its message reader gone): start it again for the next request, which then
            # carries what came before in its text
            for s in {first, last}:
                await s._close_now()
                s.lost_upto = self.seq
        if not self.stopping:   # stopped for a new request or "stop": that puts up its own
            api.panel(result=reply or "Done.", working=False, status=None)
        if self.speaker and self.prefs.get("speak") and reply and not self.stopping:
            self.speaker.say(reply)
        if self.persona_pending:
            asyncio.create_task(self.restart_sessions())
        private = was_private or bool(self.private)   # anything touching a spell stays out of the sitting and the logs
        for s in {first, last}:
            if s.client and s.context >= handoff_at() and not s.handing_off and not private:
                s.handoff_task = asyncio.create_task(self.handoff(s))   # the next request to it waits for this
        self.refresh_efforts()   # an effort changed during the request
        self.seq += 1
        exchange = {"seq": self.seq, "t": time.time(), "request": request, "reply": reply or "Done.", "by": last.name}
        if not private:
            self.recent = (self.recent + [exchange])[-8:]
        elif self.private:   # kept apart, for the spell's other sessions; gone when it ends
            self.recent_private = (self.recent_private + [exchange])[-8:]
        # a model that handed over saw everything before this one, and this one's request but not its reply
        first.synced = self.seq - 1 if last is not first else self.seq
        last.synced = self.seq
        done = time.time()
        acted = kit.actions[0][0] if kit.actions else None
        rec = {"t": round(done, 1), "source": timing.get("source"), "mode": mode, "model": last.model,
               "effort": last.up_effort, "tools": used,
               "transcribe_s": timing.get("transcribe_s"), "hearing": timing.get("hearing"),
               "to_first_action_s": round(acted - t0, 2) if acted and t0 else None,
               "to_reply_s": round(done - t0, 2) if t0 else None, "context": last.context, **rec}
        if private:   # no timing, tools or replies from private time, in any log
            return
        self.log(f"timing {rec}")
        if os.environ.get("SOJOURN_LOG_REPLIES") == "1":  # tests only: what was asked and answered stays out of logs
            self.log(f"replied ({last.name}) to {request!r}: {reply!r}")
        try:
            self.timings.parent.mkdir(parents=True, exist_ok=True)
            with open(self.timings, "a", encoding="utf-8") as f:
                f.write(json.dumps(rec) + "\n")
        except OSError:
            pass

    async def submit(self, request, target, timing):
        """A new request: an answer to a pending choice, a stop, or the next job (interrupting one in progress)."""
        fut = self.kit.choice
        if fut and not fut.done():
            fut.set_result({"said": request})
            self.api.panel(heard=request, status="thinking")
            return
        busy = self.task and not self.task.done()
        # a bare "stop" stops the request in progress; with none and a job running, it's a request (the job, then?)
        if request.strip().lower().rstrip(".!") in ("stop", "cancel", "never mind", "nevermind", "forget it") and (
                busy or not self.jobs.running()):
            await self.stop()
            self.api.panel(heard=request, result="Stopped.", working=False, status=None)
            return
        await self.stop()
        if self.speaker:
            self.speaker.stop()
        self.task = asyncio.create_task(self.handle(request, target, timing))

    async def stop(self):
        if self.task and not self.task.done() and self.waiting:   # not begun (sessions switching): nothing to interrupt
            self.task.cancel()
            return
        if self.task and not self.task.done():
            self.stopping = True
            try:
                for s in self.sessions.values():
                    if s.busy:
                        await s.interrupt()
                try:
                    await asyncio.wait_for(self.task, 10)
                except (asyncio.TimeoutError, asyncio.CancelledError, Exception):
                    self.task.cancel()   # its session's turn is read out before that session's next (Session.settle)
            finally:
                self.stopping = False

    async def close(self):
        await self.stop()
        await asyncio.gather(*(s.close() for s in self.sessions.values()))

    # --- incognito (private.py tells us when; docs/assistant.md > Incognito) ------------------------------------------

    async def private_changed(self, on, minutes=0.0, sees=False):
        """Incognito went on or off, or "let it see" changed during it. On: the request in progress stops, a job
        pauses, and the sessions start again incognito, blind (privatetools.py, prompt-incognito.md) or seeing
        (SEEING_TOOLS, prompt-incognito-sees.md). Seeing changed: they start again the other way, the spell's exchanges
        dropped (a blind session mustn't inherit what a seeing one saw). Off: they start again as before, so nothing of
        the spell's conversation carries over, a seeing spell's working folder is emptied, and the next requests hear
        only how long it lasted."""
        sees = bool(on and sees)
        if on == bool(self.private) and (not on or sees == self.private.get("sees")):
            return
        starting = on and not self.private
        if starting:
            self.private = {"since": time.time() - minutes * 60, "paused": [], "sees": sees}
        elif on:
            self.private["sees"] = sees
        else:
            spent = (time.time() - self.private["since"]) / 60
            paused, self.private = self.private.get("paused") or [], None
            self.private_after = (max(1, round(spent)), time.time())
        self.recent_private = []
        self.kit.last_look = None   # a picture of the view from before
        if not self.started:   # warm() brings them up as they should be
            return
        self.ready.clear()
        asyncio.create_task(self._switch(starting, None if on else paused, wipe=not on or not sees))

    async def _switch(self, starting, paused, wipe):
        try:
            if self.kit.choice and not self.kit.choice.done():   # a pick or point it was waiting for won't come
                self.kit.choice.set_result({"cancelled": True})
            if not self.waiting:   # a request that came after the change goes to the new sessions instead
                await self.stop()
            if starting:
                titles = await self.jobs.pause_all("incognito")
                if self.private is not None:
                    self.private["paused"] = titles
            await self.restart_sessions()
            if wipe:   # the seeing sessions are closed: what they left in their working folder goes
                await asyncio.to_thread(wipe_incognito, self.log)
        except Exception as e:
            self.log(f"incognito: switching sessions failed: {e!r}")
        finally:
            self.ready.set()
        if paused:
            asyncio.create_task(self.notify(f"{' and '.join(paused)} {'is' if len(paused) == 1 else 'are'} paused; "
                                            "say when to carry on."))

    def after_private(self, s):
        """For a session's first request after a spell (each starts afresh then): how long it lasted, and no more."""
        if not self.private_after or s.synced or time.time() - self.private_after[1] > IDLE_RESET:
            return ""
        n = self.private_after[0]
        return f"(They were in incognito for {n} minute{'s' if n != 1 else ''} just now; nothing from it is kept.)\n"

    async def private_context(self, target):
        """A private request's context line: the client's general picture (SojournPrivate situation), handles only.
        Only known fields of the pointer go in, so a target from before the spell can't carry a name in."""
        try:
            sit = await asyncio.wait_for(asyncio.to_thread(self.api.cmd, "SojournPrivate", "situation", 2), 2.5) or {}
        except (ApiError, asyncio.TimeoutError):
            sit = {}
        shown = await self.shown()

        def blind(t):
            if not isinstance(t, dict):
                return None
            h = t.get("handle") if privatetools.HANDLE.fullmatch(str(t.get("handle") or "")) else None
            kind = t.get("kind") if t.get("kind") in ("thing", "person", "ground", "hud") else "something"
            d = t.get("distance")
            return kind + (f" {h}" if h else "") + (f", about {d} m away" if isinstance(d, (int, float)) else "")

        if target and target.get("looking"):
            pointing = f"nothing (in the middle of their view: {blind(target)})"
        else:
            pointing = blind(target) or "nothing"
        convs = []
        for c in sit.get("ims") or []:
            if isinstance(c, dict) and privatetools.HANDLE.fullmatch(str(c.get("chat") or "")):
                convs.append(f"{c['chat']} ({c.get('kind')}, {c.get('people')} {'person' if c.get('people') == 1 else 'people'}, "
                             f"{c.get('new_since_last_asked') or 0} new{', you wrote in it' if c.get('you_wrote') else ''}"
                             f"{', open in their feed' if c.get('open') else ''})")
        m = sit.get("minutes_incognito")
        return (f"[time {sit.get('time') or time.strftime('%H:%M')}; incognito, on for {m if m is not None else '?'} min; "
                f"{self.where_word()}; {sit.get('summary') or 'the client gave no picture'}"
                + (f"; conversations: {'; '.join(convs)}" if convs else "")
                + f"; pointing at {pointing}{shown}]\n")
