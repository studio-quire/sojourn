"""Incognito ("private mode" in the code), the sidecar's half (docs/assistant.md > Incognito). The client enforces it
(the agent API's allowlist and blind handles unless the person lets the assistant see, sealed events, and off only by
the person's own controls); here:
- keeping the brain in step: the client's `private` event is only a nudge (anyone on loopback can post a `sojourn`
  event outside dev mode), so what counts is `SojournPrivate status`, read on the nudge and with every hello;
- dictation, by its key only: the person's words go to the client as a draft for them to send (SojournPrivate draft,
  with the token only this process has), never to Claude, a log, timings or exchanges, in and out of incognito, and in
  dev mode.
No words of the person's are matched here: turning incognito on by asking is the model's call like any request (its
`private` tool), and nothing a model or a program says turns it off (the user's call, 2026-10-27).
"""
import asyncio
import time

from api import ApiError


class Private:
    """The sidecar's view of incognito. brain: the Brain (or dev mode's relay); its private_changed(on, minutes, sees)
    is awaited when the client's state changes, if it has one."""

    def __init__(self, api, token, log, brain=None):
        self.api, self.token, self.log, self.brain = api, token or "", log, brain
        self.on = False
        self.sees = False          # the person lets the assistant see during the spell
        self.since = None          # when this spell began, by the client's minutes
        self.why = None
        self.lock = asyncio.Lock()

    async def sync(self):
        """Read the client's state and act on it (the event is only a nudge)."""
        try:
            st = await asyncio.to_thread(self.api.cmd, "SojournPrivate", "status", 3)
        except ApiError:
            return   # an older client, or it's busy: the next hello tries again
        async with self.lock:
            on = st.get("on") is True
            sees = on and st.get("sees") is True
            if on == self.on and sees == self.sees:
                return
            if on and self.on:   # "let it see" changed mid-spell: the sessions switch, the spell goes on
                minutes = float(st.get("minutes") or 0)
            else:
                minutes = float(st.get("minutes") or 0) if on else (time.time() - (self.since or time.time())) / 60
                self.why = st.get("why") if on else None
                self.since = time.time() - minutes * 60 if on else None
            self.on, self.sees = on, sees
            self.log(f"incognito {'on (' + str(self.why) + (', seeing' if sees else '') + ')' if on else f'off after {minutes:.0f} min'}")
            if self.brain is not None and hasattr(self.brain, "private_changed"):
                try:
                    await self.brain.private_changed(on, minutes, sees)
                except Exception as e:
                    self.log(f"incognito: the sessions didn't switch cleanly: {e!r}")

    async def draft(self, text):
        """The person's dictated words to the client as a draft for them to send; nothing of them is logged."""
        try:
            await asyncio.to_thread(self.api.cmd, "SojournPrivate", "draft", 5, text=text, token=self.token)
            self.log("dictation: a draft passed to the client")
        except ApiError as e:
            self.log(f"dictation: the client didn't take the draft ({str(e)[:80]})")
            self.api.panel(result="Couldn't.", working=False)
