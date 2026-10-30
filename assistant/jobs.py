"""Background jobs: the assistant's own way to do long work (a build, sorting an inventory, an errand in many steps)
while the person carries on and the request sessions stay free to answer them.

The assistant runs them (the user's decision, 2026-10-25): it decides whether to start one, which model and effort run
it, how to brief it and whether it may use helpers (Claude Code subagents), and when to check on it, tell it something,
stop it or resume it. What's here is only the mechanics that need a harness:
- a job outlives the request that started it, as a session of its own, with no time or turn limit; it runs until it's
  done or stopped (by the person, through the assistant);
- one runs at a time; another can wait its turn (queued), and starts when the running one is done;
- what the assistant tells a running job reaches it after its next tool call (a PostToolUse hook, harness.Guard);
- its notes (jobs/<id>.md in the assistant's folder: what was asked, the plan, what's done with the objects' ids,
  what's left, what to clean up), kept by the job, so a stopped job, one cut off by a restart (paused, never restarted
  by itself) or one whose session failed after making progress carries on from them;
- its progress line on the panel, and what it put on screen (its checklist) taken down when it ends;
- the same tools, permission mode, deny rules and confirmations as the requests', helpers included.
When one finishes it says so on the panel (once no request is using it) and its notes go; anything worth keeping is
the job's to write into memory first.
"""
import asyncio
import os
import re
import time
from pathlib import Path

from claude_agent_sdk import create_sdk_mcp_server, tool

import tools as toolmod

HERE = Path(__file__).resolve().parent
JOB_MODELS = ("opus", "sonnet", "haiku")
EFFORTS = ("low", "medium", "high", "xhigh", "max")
SHOWN = "job-"       # what a job puts on screen is named job-<id>-<its own name>


class JobKit(toolmod.Toolkit):
    """A job's tools: their status lines go to the job, not the panel's request line, and what it shows is named as
    the job's, so it can be taken down when the job ends."""

    def __init__(self, api, log, job):
        super().__init__(api, log)
        self.job = job

    def status(self, s):
        self.job.step = s
        if not self.job.progress:      # the job's own words win once it has said any
            self.job.runner.show()

    def show_id(self, i):
        prefix = f"{SHOWN}{self.job.id}-"
        return i if i == "all" or i.startswith(prefix) else prefix + i

    async def unshow(self, i):
        if i != "all":
            return await super().unshow(i)
        return {"removed": await self.job.runner.unshow(self.job.id)}


class Job:
    def __init__(self, runner, jid, title, file, model, effort, helpers):
        self.runner, self.id, self.title, self.file = runner, jid, title, file
        self.model, self.effort, self.helpers = model, effort, helpers
        self.state = "running"
        self.progress = ""     # the job's own words for the panel
        self.step = ""         # what its tools are doing
        self.started = time.time()
        self.task = None
        self.session = None
        self.reply = ""
        self.told = []         # what the assistant told it, until its next tool call

    def line(self):
        return f"{self.title}: {self.progress or self.step or 'starting'}"

    def set_progress(self, text):
        self.progress = text.strip()[:90]
        self.runner.show()

    def take_told(self):
        told, self.told = self.told, []
        return told

    def plan_needed(self):
        """Why the job may not make anything yet (its notes have no plan), or None."""
        try:
            text = self.file.read_text(encoding="utf-8")
        except OSError:
            return None
        m = re.search(r"^## Plan\n(.*?)(?=^## |\Z)", text, re.M | re.S)
        if m is None or m.group(1).strip():
            return None
        return (f"Not yet: first write your plan into your notes ({self.file}): under Plan, each part with its size and "
                "place in the object's frame; under Left, what's still to make. If it has several parts, show it to the "
                "person as a checklist too (show, at the wrist). Then carry on with this call.")


def slug(title):
    return re.sub(r"[^a-z0-9]+", "-", title.lower()).strip("-")[:32] or "job"


def header(path, key):
    try:
        m = re.search(rf"^{key}:\s*(.+)$", path.read_text(encoding="utf-8", errors="replace"), re.M)
        return m[1].strip() if m else None
    except OSError:
        return None


def set_header(path, key, value):
    try:
        t = path.read_text(encoding="utf-8")
    except OSError:
        return
    if re.search(rf"^{key}:", t, re.M):
        t = re.sub(rf"^{key}:.*$", lambda _: f"{key}: {value}", t, count=1, flags=re.M)
    else:
        lines = t.split("\n", 1)
        t = lines[0] + f"\n{key}: {value}\n" + (lines[1] if len(lines) > 1 else "")
    path.write_text(t, encoding="utf-8", newline="\n")


def mtime(path):
    try:
        return path.stat().st_mtime
    except OSError:
        return 0


def told_text(told):
    return ("From the person, through the assistant, since you started (fold it into the job, and into your notes):\n"
            + "\n".join(f"- {t}" for t in told))


class JobRunner:
    def __init__(self, brain):
        self.brain = brain
        self.dir = brain.home.root / "jobs"
        self.dir.mkdir(parents=True, exist_ok=True)
        self.jobs = {}
        self.queue = []          # (jid, title, what, ctx), in order, waiting for the running one
        self._shown = None
        # jobs cut off by a restart: paused, to carry on when the person wants (a restart never starts one)
        self.cut_off = []
        for f in self.dir.glob("*.md"):
            st = header(f, "state") or ""
            if st.startswith("running"):
                set_header(f, "state", "paused (the assistant restarted)")
                self.cut_off.append(f.stem)
            elif st.startswith("queued"):
                set_header(f, "state", "paused (queued before a restart)")

    async def startup(self):
        """After a restart: take down what jobs left on screen, and tell the person if one was cut off."""
        await self.unshow(None)
        if self.cut_off:
            names = [(self.dir / f"{j}.md").read_text(encoding="utf-8").split("\n", 1)[0].lstrip("# ").strip() or j
                     for j in self.cut_off if (self.dir / f"{j}.md").exists()]
            if names:
                asyncio.create_task(self.brain.notify(
                    f"{' and '.join(names)} stopped when the assistant restarted; ask to carry on whenever you like."))

    def running(self):
        return next((j for j in self.jobs.values() if j.state == "running"), None)

    def show(self):
        """The panel's job line: the running job in a few words, and what waits after it."""
        j = self.running()
        line = (j.line() + (f" (then {', '.join(q[1] for q in self.queue)})" if self.queue else "")) if j else None
        if line != self._shown:
            self._shown = line
            self.brain.api.panel(jobs=line)
            if os.environ.get("SOJOURN_LOG_REPLIES") == "1":   # tests only, as for requests
                self.brain.log(f"panel's job line: {line}")

    async def unshow(self, jid):
        """Take down what a job put on screen (jid None: every job's)."""
        prefix = SHOWN + (f"{jid}-" if jid else "")
        try:
            r = await asyncio.to_thread(self.brain.api.cmd, "SojournShow", "list", 2)
            gone = [e["id"] for e in r.get("shown") or [] if str(e.get("id", "")).startswith(prefix)]
            for i in gone:
                await asyncio.to_thread(self.brain.api.cmd, "SojournShow", "remove", 2, id=i)
            return gone
        except Exception as e:
            self.brain.log(f"couldn't take down a job's things on screen: {e!r}")
            return []

    def summary(self, full=False):
        """The jobs and where they stand: short for each request's context, full (choices, notes' files) for `jobs`."""
        out = []
        queued = [q[0] for q in self.queue]
        for f in sorted(self.dir.glob("*.md")):
            jid, j = f.stem, self.jobs.get(f.stem)
            if j and j.state == "running":
                st = f"running {(time.time() - j.started) / 60:.0f} min" + (f": {j.progress or j.step}" if j.progress or j.step else "")
            elif jid in queued:
                st = f"queued, {queued.index(jid) + 1} of {len(queued)}"
            else:
                st = j.state if j else (header(f, "state") or "?")
            if full:
                st += f"; {header(f, 'model') or '?'}, helpers {header(f, 'helpers') or '?'}; notes {f}"
                if j and j.state == "running" and j.progress and j.step:
                    st += f"; its tools just now: {j.step}"
            out.append(f"{jid} ({st})")
        return "; ".join(out)

    def _new_file(self, title, brief, ctx, model, effort, helpers):
        self.dir.mkdir(parents=True, exist_ok=True)   # a clear in the menu takes the folder too
        jid = base = slug(title)
        n = 2
        while (self.dir / f"{jid}.md").exists():
            jid, n = f"{base}-{n}", n + 1
        file = self.dir / f"{jid}.md"
        file.write_text(f"# {title}\nstate: queued\nasked: {time.strftime('%Y-%m-%d %H:%M')}\nmodel: {model} {effort}\n"
                        f"helpers: {'yes' if helpers else 'no'}\n\n## Asked (the assistant's brief)\n{brief.strip()}\n\n"
                        f"## Where they were when they asked\n{ctx.strip()}\n\n## Plan\n\n## Done\n\n## Left\n\n"
                        f"## To clean up\n", encoding="utf-8", newline="\n")
        return jid, file

    def start(self, title, brief, ctx, model, effort, helpers, queue=False):
        """A new job; if one is running, it waits its turn when queue is set, else it's refused with the reason.
        Returns (id, started or queued)."""
        self._check(model, effort, queue)
        jid, file = self._new_file(title, brief, ctx, model, effort, helpers)
        what = f"The job, as the assistant briefed it:\n{brief.strip()}"
        if self.running():
            self.queue.append((jid, title, what, ctx))
            self.show()
            self.brain.log(f"job {jid} queued")
            return jid, "queued"
        self._launch(jid, title, file, what, ctx)
        return jid, "started"

    def resume(self, jid, ctx, model=None, effort=None, helpers=None, note="", queue=False):
        file = self.dir / f"{jid}.md"
        if not file.exists():
            raise RuntimeError(f"no job {jid}")
        if (r := self.running()) and r.id == jid:
            raise RuntimeError(f"{jid} is already running")
        if any(q[0] == jid for q in self.queue):
            raise RuntimeError(f"{jid} is already waiting its turn")
        was = (header(file, "model") or "opus medium").split()
        self._check(model or was[0], effort or (was[1] if len(was) > 1 else "medium"), queue)
        if model or effort:
            set_header(file, "model", f"{model or was[0]} {effort or (was[1] if len(was) > 1 else 'medium')}")
        if helpers is not None:
            set_header(file, "helpers", "yes" if helpers else "no")
        if note.strip():
            self.add_told(file, note)
        title = (file.read_text(encoding="utf-8").splitlines() or [jid])[0].lstrip("# ").strip() or jid
        what = ("Carry on with this job from its notes: read them, check what's in the world now (the objects they "
                "list), and do what's left." + (f"\nFrom the person, through the assistant: {note.strip()}" if note.strip() else ""))
        if self.running():
            set_header(file, "state", "queued")
            self.queue.append((jid, title, what, ctx))
            self.show()
            return "queued"
        self._launch(jid, title, file, what, ctx)
        return "carrying on"

    def _check(self, model, effort, queue):
        if self.brain.private:
            raise RuntimeError("incognito is on: jobs wait until the person turns it off")
        if model not in JOB_MODELS:
            raise RuntimeError(f"model is one of {', '.join(JOB_MODELS)}")
        if effort not in EFFORTS:
            raise RuntimeError(f"effort is one of {', '.join(EFFORTS)}")
        if (r := self.running()) and not queue:
            raise RuntimeError(f"one job runs at a time, and {r.id} is running ({r.progress or r.step or 'starting'}). "
                               "Queue this one (queue: true) to start when it's done, tell the running one about it "
                               "(tell_job), or stop it first; or tell the person.")

    def add_told(self, file, text):
        try:
            t = file.read_text(encoding="utf-8")
        except OSError:
            return
        line = f"- {time.strftime('%H:%M')} {text.strip()}\n"
        if "\n## Told since\n" in t:
            t = t.replace("\n## Told since\n", "\n## Told since\n" + line, 1)
        else:
            t = t.rstrip("\n") + "\n\n## Told since\n" + line
        file.write_text(t, encoding="utf-8", newline="\n")

    def tell(self, jid, text):
        """Something for a job from the person: a running one hears it after its next tool call; it goes into the
        notes too, so a queued or paused one has it when it starts."""
        jid = jid or (self.running().id if self.running() else None)
        file = self.dir / f"{jid}.md" if jid else None
        if not file or not file.exists():
            raise RuntimeError("no job is running" if not jid else f"no job {jid}")
        self.add_told(file, text)
        j = self.jobs.get(jid)
        if j and j.state == "running":
            j.told.append(text.strip())
            return f"{jid} will hear it after its current step"
        return f"noted in {jid}'s notes, for when it carries on"

    def _session(self, job):
        from brain import MODELS, Session
        kit = JobKit(self.brain.api, self.brain.log, job)
        kit.vr = self.brain.kit.vr
        s = Session(f"job:{job.id}", MODELS[job.model], job.effort, kit, False, self.brain.log, self.brain.extras,
                    turns=None)
        s.system = self.brain.job_system(job.helpers)
        s.help = self.brain.catalog is None
        if not job.helpers:
            s.disallowed = ["Agent", "Task"]
        return s

    def _launch(self, jid, title, file, what, ctx):
        model, effort = ((header(file, "model") or "opus medium").split() + ["medium"])[:2]
        job = Job(self, jid, title, file, model, effort, (header(file, "helpers") or "yes") == "yes")
        set_header(file, "state", "running")
        set_header(file, "started", time.strftime("%Y-%m-%d %H:%M"))
        job.session = self._session(job)
        self.jobs[jid] = job
        message = ctx + self.brain.memory_note(job.session) + f"Job file: {file}\n{what}"
        job.task = asyncio.create_task(self._run(job, message))
        self.show()
        self.brain.log(f"job {jid} started ({model} {effort}, helpers {'on' if job.helpers else 'off'})")
        return job

    async def _run(self, job, message):
        try:
            while True:
                before = mtime(job.file)
                try:
                    reply, used, info = await job.session.ask(message)
                    failed = info.get("error")
                except asyncio.CancelledError:
                    raise
                except Exception as e:
                    reply, failed = "", repr(e)
                if job.state != "running":
                    break
                if told := job.take_told():          # told something it never got to see: carry on with it
                    message = told_text(told)
                    continue
                if job.session.handoff_asked and not failed:
                    # its context reached the line (brain.HANDOFF_AT) and it brought its notes up to date: a fresh
                    # session carries on from them, as after a restart, with nothing compacted
                    self.brain.log(f"job {job.id} handed off at {job.session.context} tokens")
                    try:
                        await job.session.close()
                    except Exception:
                        pass
                    job.session = self._session(job)
                    message = (await self.brain.context(self.brain.kit.target, jobs=False) + self.brain.memory_note(job.session)
                               + f"Job file: {job.file}\nCarry on with this job from its notes (the session before this one "
                                 "handed off as its context filled): read them, check what's in the world now, and do "
                                 "what's left.")
                    continue
                if failed:
                    self.brain.log(f"job {job.id}'s session ended with {failed}")
                    if mtime(job.file) > before:     # it got somewhere: a fresh session carries on from the notes
                        try:
                            await job.session.close()
                        except Exception:
                            pass
                        job.session = self._session(job)
                        message = (await self.brain.context(self.brain.kit.target, jobs=False) + self.brain.memory_note(job.session)
                                   + f"Job file: {job.file}\nCarry on with this job from its notes (the session before "
                                     "this one ended early): read them, check what's in the world now, and do what's left.")
                        continue
                    job.state = "failed"
                    job.reply = f"it stopped with an error ({failed})"
                    break
                job.state, job.reply = "done", reply
                break
        except asyncio.CancelledError:
            if job.state == "running":
                job.state = "stopped"
        finally:
            try:
                await job.session.close()
            except Exception:
                pass
            self.show()
        self.brain.log(f"job {job.id} {job.state} after {time.time() - job.started:.0f}s ({job.session.context} tokens of context)")
        await self.unshow(job.id)
        if job.state.startswith("paused"):   # pause_all: as at a restart, nothing said and nothing after it starts
            set_header(job.file, "state", job.state)
            return
        waiting = ", ".join(q[1] for q in self.queue)
        if job.state == "done":
            try:
                job.file.unlink()
            except OSError:
                pass
            text = f"{job.title}: {job.reply or 'done.'}"
        else:
            set_header(job.file, "state", job.state)
            text = f"{job.title} {job.state}." + (f" {job.reply}" if job.reply else "")
            if waiting:
                text += f" Waiting after it: {waiting}."
        self.brain.job_finished(job, text)
        if job.state == "done" and self.queue and not self.running():
            jid, title, what, ctx = self.queue.pop(0)
            self._launch(jid, title, self.dir / f"{jid}.md", what, ctx)

    async def stop(self, jid=None):
        if jid and (q := [x for x in self.queue if x[0] == jid]):
            self.queue.remove(q[0])
            set_header(self.dir / f"{jid}.md", "state", "stopped (before it started)")
            self.show()
            return jid
        job = self.jobs.get(jid) if jid else self.running()
        if not job or job.state != "running":
            raise RuntimeError(f"{jid} isn't running" if jid else "no job is running")
        job.state = "stopped"
        await job.session.interrupt()
        job.task.cancel()
        try:
            await asyncio.wait_for(asyncio.shield(job.task), 15)
        except Exception:
            pass
        return job.id

    async def pause_all(self, why):
        """Incognito came on: the running job and the queue stop as at a restart (paused, carried on from their notes
        when the person asks), and nothing starts while it lasts (_check). Returns their titles."""
        titles = []
        for jid, title, *_ in list(self.queue):
            set_header(self.dir / f"{jid}.md", "state", f"paused ({why})")
            titles.append(title)
        self.queue.clear()
        if job := self.running():
            job.state = f"paused ({why})"
            titles.insert(0, job.title)
            await job.session.interrupt()
            job.task.cancel()
            try:
                await asyncio.wait_for(asyncio.shield(job.task), 15)
            except Exception:
                pass
        self.show()
        return titles

    async def stop_all(self):
        for jid, *_ in list(self.queue):
            await self.stop(jid)
        if r := self.running():
            try:
                await self.stop(r.id)
            except Exception:
                pass


def own_server(brain, session):
    """The assistant's own tools for one session: memory housekeeping, and its jobs (starting and steering them from a
    request; progress from inside one)."""
    runner, home = brain.jobs, brain.home
    job = getattr(session.kit, "job", None)

    def ok(t):
        return {"content": [{"type": "text", "text": t}]}

    def err(t):
        return {"content": [{"type": "text", "text": f"error: {t}"}], "is_error": True}

    S = toolmod.S
    out = []

    @tool("forget", "Delete notes in your own folder (your memory), by their paths in it (like mira.md); "
          "[\"everything\"] deletes every note and the index.", {"type": "object", "properties": {
              "paths": {"type": "array", "items": {"type": "string"}}}, "required": ["paths"]})
    async def forget(args):
        session.kit.status("forgetting")
        paths = args.get("paths") or []
        root = home.root.resolve()
        if paths == ["everything"]:
            n = 0
            for rel in home.files():
                if not rel.startswith("jobs/"):
                    (home.root / rel).unlink(missing_ok=True)
                    n += 1
            return ok(f"deleted {n} files")
        done, why = [], []
        for p in paths:
            f = (home.root / p).resolve()
            if not f.is_relative_to(root) or f == root or not f.is_file():
                why.append(f"{p}: not a note in your folder")
                continue
            f.unlink()
            done.append(p)
        # and their lines in the index
        index = home.root / "MEMORY.md"
        if done and index.is_file():
            names = {Path(p).as_posix() for p in done}
            lines = index.read_text(encoding="utf-8").splitlines()
            kept = [l for l in lines if not any(n in l for n in names)]
            if len(kept) != len(lines):
                index.write_text("\n".join(kept) + "\n", encoding="utf-8", newline="\n")
        return ok(("deleted " + ", ".join(done) if done else "deleted nothing") + ("; " + "; ".join(why) if why else ""))
    out.append(forget)

    if job is None:
        choice = {"model": {"type": "string", "enum": list(JOB_MODELS)},
                  "effort": {"type": "string", "enum": list(EFFORTS)},
                  "helpers": {"type": "boolean"}, "queue": {"type": "boolean"}}

        @tool("start_job", "Start a background job: a session of its own that works on what the person asked while they "
              "carry on, for as long as it takes, until it's done or stopped. They see its progress line on the panel. "
              "title: two or three words (\"the cabin\"). brief: all it gets from you, so put in everything it needs "
              "that it can't see: what they asked, in their words, and what you know (where they are and face, which "
              "objects, sizes, colours, their preferences). Say what's wanted, not how to do it: the job has the same "
              "tools and its own instructions for building, checking and replying, so leave out tool names, ops, steps "
              "and how to report back. model and effort: yours to choose for the work (effort defaults to medium). "
              "helpers: whether it may hand parts to helpers that run in parallel (default yes). One job runs at a time: "
              "while one runs, queue: true makes this one start when that one is done. Returns at once.",
              {"type": "object", "required": ["title", "brief", "model"],
               "properties": {"title": {"type": "string"}, "brief": {"type": "string"}, **choice}})
        async def start_job(args):
            try:
                ctx = await brain.context(brain.kit.target, jobs=False)
                jid, how = runner.start(args["title"], args["brief"], ctx, args["model"], args.get("effort") or "medium",
                                        args.get("helpers", True) is not False, bool(args.get("queue")))
            except RuntimeError as e:
                return err(str(e))
            return ok(f"job {jid} {how}")

        @tool("jobs", "The jobs and where each stands (running, queued, paused, stopped), with the model and effort "
              "running each and their notes' files (Read one to see its plan, what's done and what's left).", S())
        async def jobs(args):
            return ok(runner.summary(full=True) or "no jobs")

        @tool("tell_job", "Tell a job something from the person: a change of plan, more to do, a correction. A running "
              "one hears it after its current step; it also goes into the job's notes. id: the running one if left out.",
              S(["text"], text=str, id=str))
        async def tell_job(args):
            try:
                return ok(runner.tell(args.get("id"), args["text"]))
            except RuntimeError as e:
                return err(str(e))

        @tool("stop_job", "Stop a job, the running one if no id (what it made stays; its notes say where it got to), "
              "or take a queued one out of the queue.", S(id=str))
        async def stop_job(args):
            try:
                return ok(f"{await runner.stop(args.get('id'))} stopped")
            except RuntimeError as e:
                return err(str(e))

        @tool("resume_job", "Carry on with a paused, stopped or failed job from its notes, by the same model unless you "
              "choose another. note: anything the person has said since. queue: true to wait for a running one.",
              {"type": "object", "required": ["id"],
               "properties": {"id": {"type": "string"}, "note": {"type": "string"}, **choice}})
        async def resume_job(args):
            try:
                ctx = await brain.context(brain.kit.target, jobs=False)
                how = runner.resume(args["id"], ctx, args.get("model"), args.get("effort"), args.get("helpers"),
                                    args.get("note") or "", bool(args.get("queue")))
            except RuntimeError as e:
                return err(str(e))
            return ok(f"{args['id']} {how}")
        out += [start_job, jobs, tell_job, stop_job, resume_job]
    else:
        @tool("progress", "A few words for the person's panel on what the job is doing now (\"the roof next\"). Call it "
              "as the work moves on.", S(["text"], text=str))
        async def progress(args):
            job.set_progress(args.get("text") or "")
            return ok("shown")
        out.append(progress)

    return create_sdk_mcp_server("own", tools=out), [f"mcp__own__{t.name}" for t in out]
