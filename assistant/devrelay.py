"""Dev mode's stand-in for brain.py (the client started with --dev; docs/dev-mode.md): no Claude of its own. What the
person says (heard by ears.py), types, picks or points at goes to the Claude Code session that started the client, as
`dev` events (SojournAssistant dev, with the token the client handed this process), which `tools/dev.py watch` turns
into lines for that session. Its replies come back as SojournAssistant say, which the client puts on the panel and
passes here (a `say` event) to be read out when the person has spoken replies on.
"""
import os
import time

from api import ApiError
from paths import LOCAL   # the repo's local/, or a packaged build's data folder: where the client's dev note is


class DevRelay:
    def __init__(self, api, port, log=print, speaker=None):
        self.api, self.port, self.log, self.speaker = api, port, log, speaker
        self.token = os.environ.get("SOJOURN_DEV_TOKEN") or ""
        self.prefs = {}
        self.persona, self.memory = (None, None), True
        self.vr = False

    def watched(self):
        """Whether a session is watching (tools/dev.py watch touches this file at every poll)."""
        try:
            return time.time() - (LOCAL / "dev" / f"{self.port}.watching").stat().st_mtime < 60
        except OSError:
            return False

    def send(self, what, **data):
        try:
            self.api.cmd("SojournAssistant", "dev", 5, token=self.token, what=what, **{k: v for k, v in data.items() if v is not None})
            return True
        except ApiError as e:
            self.log(f"dev: couldn't pass on a {what}: {e}")
            return False

    # --- what main.py calls on a brain ----------------------------------------------------------------------------

    async def warm(self):
        pass

    def set_mode(self, mode):
        pass

    def set_efforts(self, efforts):
        pass

    def apply_prefs(self, prefs):
        if not isinstance(prefs, dict) or prefs == self.prefs:
            return
        self.prefs = prefs
        if self.speaker:
            self.speaker.set_voice(prefs.get("voice"))
            if prefs.get("speak"):
                self.speaker.warm()
            else:
                self.speaker.stop()

    async def forget_all(self):
        pass

    async def submit(self, text, target, timing):
        source = (timing or {}).get("source")
        self.log(f"dev: {source or 'request'} passed on ({len(text)} chars)")
        try:   # said instead of typed: a word they were asked to check is answered by this
            self.api.cmd("SojournAssistant", "askTyped", 5, question=None)
        except ApiError:
            pass
        # the panel as for any request: what was heard, working on it, until the session says otherwise
        self.api.panel(heard=text, status=None if self.watched() else "no session is watching yet",
                       result=None, working=True, hearing=False, choices=[], prompt=None, model=None)
        if not self.send("request", text=text, target=target, source=source):
            self.api.panel(working=False, status="couldn't pass that on")

    async def notify(self, text):
        self.send("timer", text=text)

    def pick(self, d):
        self.api.panel(choices=[], prompt=None, working=True)
        self.send("pick", id=d.get("id"), label=d.get("label"))

    def pointed(self, d):
        self.send("pointed", target=d.get("target"), text="cancelled" if d.get("cancelled") or not d.get("target") else None)

    def answer(self, d):
        """What they sent after `dev.py confirm` asked them to check a word (or that they dismissed it)."""
        self.api.panel(working=True)
        self.send("answer", text=d.get("text"), source=None if d.get("text") else "dismissed")

    def speak(self, text):
        if self.speaker and self.prefs.get("speak") and text:
            self.speaker.say(text)
