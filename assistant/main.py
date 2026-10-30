"""Sojourn's assistant, the Python side. The client starts it (Assistant/Sidecar.cs) and ends it.

  local/assistant-venv/Scripts/python.exe assistant/main.py --api http://127.0.0.1:9720 [--vr] [--mode quick|sonnet|opus]

It listens to the client's `sojourn` events (talk_down, talk_up, typed, pick, mode, prefs, shown_done, shown_dismissed;
`audio {path}` transcribes
a WAV as if spoken, for tests), hears with Whisper (ears.py), has Claude act through the agent API (brain.py, tools.py),
and speaks its replies when the person wants that (voice.py).
With --dev (the client started in dev mode, docs/dev-mode.md) there's no Claude here: devrelay.py passes what the person
says to the session driving the client, and reads its replies out.
It says hello every few seconds (the panel knows it's up; the answer carries the client's mode too) and exits when the
client is gone.
"""
import argparse
import asyncio
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from api import Api, ApiError  # noqa: E402

os.environ.pop("CLAUDE_CODE_PLUGIN_DIRS", None)  # the user's own Claude Code mods stay out of the assistant
# the client's proof that a call is the person's own dictated words through this process: out of the environment before
# any Claude session or subprocess starts, so no model's shell can print it
SIDECAR_TOKEN = os.environ.pop("SOJOURN_SIDECAR_TOKEN", "")


def log(msg):
    print(f"{time.strftime('%H:%M:%S')} {msg}", flush=True)


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--api", default="http://127.0.0.1:9720")
    ap.add_argument("--vr", action="store_true")
    ap.add_argument("--whisper", default=os.environ.get("SOJOURN_WHISPER", "small.en"))
    ap.add_argument("--mode", default="quick", help="which model takes requests first (brain.MODES); the client's setting")
    ap.add_argument("--dev", action="store_true", help="dev mode: pass requests to the driving session (devrelay.py)")
    a = ap.parse_args()
    try:
        import ctypes
        ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)  # BELOW_NORMAL
    except Exception:
        pass

    api = Api(a.api)
    while True:
        try:
            st = api.status()
            if st.get("connected"):
                break
        except ApiError:
            pass
        await asyncio.sleep(1)
    seq = st.get("seq", 0)

    from ears import Ears
    from hearing import Words
    import home as homemod
    words = Words(api, log, home=homemod.default_dir())   # the names around the person, for the ears (hearing.py)
    await asyncio.to_thread(words.fetch)
    ears = await asyncio.to_thread(Ears, a.vr, a.whisper, 4, log, api, "whisper", words)
    from voice import Speaker
    speaker = Speaker(log)
    if ears.client:
        speaker.sink = ears.client.sink   # replies play in voice chat's mix: cancelled from the mic, never sent
    relay = None
    if a.dev:
        from devrelay import DevRelay
        from urllib.parse import urlparse
        kit = None
        brain = relay = DevRelay(api, urlparse(a.api).port, log, speaker)
    else:
        from brain import Brain
        from tools import Toolkit
        kit = Toolkit(api, log)
        kit.vr = a.vr
        brain = Brain(api, kit, a.vr, a.mode, log, speaker)
    try:  # the person's choices before the sessions start, so their first instructions carry them
        r = api.cmd("SojournAssistant", "hello", 5, mic=ears.mic_name, notice=ears.notice)
        if a.dev and r.get("dev") is not True:
            log("dev mode: the client isn't in dev mode; exiting")
            os._exit(1)
        brain.prefs = r.get("prefs") or {}
        brain.app = r.get("app")
        if kit:
            kit.app = brain.app
        brain.set_efforts(r.get("effort"))   # no sessions yet: they come up at these
        brain.persona = (brain.prefs.get("name") or None, brain.prefs.get("character") or None)
        brain.memory = brain.prefs.get("memory") is not False
        speaker.set_voice(brain.prefs.get("voice"))
        if brain.prefs.get("speak"):
            speaker.warm()
        words.memory = brain.memory
        await asyncio.to_thread(ears.set_hearing, brain.prefs.get("hearing"))
    except ApiError:
        pass
    if kit:
        kit.words = words   # the context line's names (brain.context)
    from private import Private
    private = Private(api, SIDECAR_TOKEN, log, brain)
    await private.sync()   # restarted mid-spell: the sessions come up incognito
    await brain.warm()

    gone_since = None

    async def hello():
        nonlocal gone_since
        while True:
            try:
                r = await asyncio.to_thread(api.cmd, "SojournAssistant", "hello", 5, mic=ears.mic_name, notice=ears.notice)
                if a.dev and r.get("dev") is not True:
                    log("dev mode: the client isn't in dev mode; exiting")
                    os._exit(1)
                brain.vr = bool(r.get("vr"))
                if kit:
                    kit.vr = brain.vr
                brain.set_mode(r.get("mode"))
                brain.set_efforts(r.get("effort"))
                brain.apply_prefs(r.get("prefs"))
                hearing_pref((r.get("prefs") or {}).get("hearing"))
                gone_since = None
                await private.sync()   # a missed or forged `private` event can't leave the sessions wrong for long
            except ApiError:
                gone_since = gone_since or time.time()
                if time.time() - gone_since > 20:
                    log("the client is gone; exiting")
                    os._exit(0)
            await asyncio.sleep(4)

    def hearing_pref(which):
        """The person's hearing setting changed (or came with a hello): load and switch in the background."""
        if which and which != ears.hearing and not getattr(ears, "_switching", False):
            ears._switching = True

            async def switch():
                try:
                    await asyncio.to_thread(ears.set_hearing, which)
                    log(f"hearing with {ears.hearing}")
                finally:
                    ears._switching = False
            asyncio.create_task(switch())

    asyncio.create_task(hello())
    asyncio.create_task(words.keep_fresh())
    log("ready")
    talk_blind = False

    def blind_now(d=None):
        """Incognito, the assistant not let see: by the client's mark on this event, at talk_down, or the last status."""
        return bool((d or {}).get("blind")) or talk_blind or (private.on and not private.sees)

    async def hear(target, first_target, dictation=False, blind=False):
        text, dur, by = await asyncio.to_thread(ears.stop, blind)
        took = time.time() - ears.stopped_at
        log(f"heard {dur:.1f}s of audio in {took:.2f}s ({by})")
        if not text:
            api.panel(hearing=False, status="didn't catch that" if dur >= 0.25 else None)
            return
        if dictation:   # the person's words for a draft: to the client only, never to Claude or a log
            api.panel(hearing=False)
            await private.draft(text)
            return
        await brain.submit(text, target or first_target, {"source": "voice", "speech_end": ears.stopped_at, "transcribe_s": round(took, 2),
                                                          "hearing": by})

    async def from_file(path, target, blind=False):
        from ears import read_wav
        audio = await asyncio.to_thread(read_wav, path)
        t = time.time()   # as if speech ended now: reading the file isn't hearing
        text = await asyncio.to_thread(ears.transcribe, audio, blind)
        took = time.time() - t
        by = "whisper" if ears.hearing == "soniox" else ears.hearing   # a file isn't streamed
        log(f"heard {len(audio) / 16000:.1f}s from {os.path.basename(path)} in {took:.2f}s ({by})")
        api.panel(hearing=False)
        if text:
            await brain.submit(text, target, {"source": "audio-file", "speech_end": t, "transcribe_s": round(took, 2),
                                              "hearing": by})

    while True:
        try:
            evs = await asyncio.to_thread(api.events, seq, 20)
        except ApiError:
            await asyncio.sleep(1)
            continue
        for e in evs:
            seq = max(seq, e.get("seq", seq))
            if e.get("kind") != "sojourn":
                continue
            d = e.get("data") or {}
            what = d.get("what")
            if what == "talk_down":
                speaker.stop()
                talk_blind = bool(d.get("blind"))
                words.refresh_soon()
                if ears.available:
                    ears.start(blind_now(d))
            elif what == "talk_up":
                if ears.available:
                    asyncio.create_task(hear(d.get("target"), d.get("first_target"), blind=blind_now(d)))
                else:
                    api.panel(hearing=False)
            elif what in ("dictate_down", "dictate_up"):
                # dictation: what's said while the key is held becomes a draft for the person to send (private.py)
                if what == "dictate_down":
                    speaker.stop()
                    talk_blind = bool(d.get("blind"))
                    words.refresh_soon()
                    if ears.available:
                        ears.start(blind_now(d))
                elif ears.available:
                    asyncio.create_task(hear(None, None, dictation=True, blind=blind_now(d)))
            elif what == "private":
                asyncio.create_task(private.sync())   # a nudge: the client's status decides
            elif what == "typed" and d.get("text"):
                await brain.submit(d["text"], d.get("target"), {"source": "typed", "typed": e.get("t") or time.time()})
            elif what == "audio" and d.get("path"):
                api.panel(hearing=True)
                asyncio.create_task(from_file(d["path"], d.get("target"), blind_now(d)))
            elif what == "mode":
                brain.set_mode(d.get("mode"))
            elif what == "effort":
                brain.set_efforts(d.get("effort"))
            elif what == "prefs":
                brain.apply_prefs({k: d.get(k) for k in ("name", "character", "speak", "voice", "memory")})
                words.memory = d.get("memory") is not False
                hearing_pref(d.get("hearing"))
            elif what == "memory_cleared":
                asyncio.create_task(brain.forget_all())
            elif what == "shown_done":
                # a timer the assistant put up ran out
                title = (d.get("title") or "").strip()
                asyncio.create_task(brain.notify(f"{title} is done." if title else "The timer is done."))
            elif relay and what in ("pointed", "pick", "say", "typed_answer"):
                # dev mode: the person's answers go to the driving session; its replies are read out here
                if what == "say":
                    relay.speak(d.get("text"))
                elif what == "pick":
                    relay.pick(d)
                elif what == "typed_answer":
                    relay.answer(d)
                else:
                    relay.pointed(d)
            elif what == "shown_dismissed":
                log(f"the person closed {d.get('id')}")
            elif what == "pointed":
                # the person pointed at something (or put the question away) after the point tool asked
                fut = kit.choice
                if fut and not fut.done():
                    fut.set_result({"cancelled": True} if d.get("cancelled") or not d.get("target") else {"pointed": d.get("target")})
            elif what == "typed_answer":
                # what the person sent (or that they dismissed it) after confirm_text asked them to check a word
                fut = kit.choice
                if fut and not fut.done():
                    fut.set_result({"typed": d["text"]} if d.get("text") else {"cancelled": True})
                elif d.get("text"):   # nothing waits for it any more: their words as a request
                    await brain.submit(d["text"], None, {"source": "typed", "typed": e.get("t") or time.time()})
            elif what == "pick":
                fut = kit.choice
                if fut and not fut.done():
                    fut.set_result({"picked": d.get("id"), "label": d.get("label")})
                else:
                    await brain.submit(f"I pick {d.get('label')}", None, {"source": "pick", "typed": time.time()})


if __name__ == "__main__":
    for f in (sys.stdout, sys.stderr):
        f.reconfigure(encoding="utf-8", errors="replace")
    asyncio.run(main())
