"""The assistant's own folder (local/assistant/home, per machine): its memory, as plain files it reads and edits itself
with Claude Code's file tools, and its jobs' notes (jobs/).

MEMORY.md is the index, one line per note; it goes into each session's next request whenever it has changed, so every
model knows what's kept without looking. Notes are one file per topic (the person's preferences, a friend, a place, an
errand), opened when a request bears on them and rewritten in place. What goes in and what never does:
prompt-memory.md. The file tools may read and write here without asking (harness.py's allow rules).
"""
import hashlib
import os
import stat
from pathlib import Path

from paths import LOCAL

HERE = Path(__file__).resolve().parent
INDEX = "MEMORY.md"
INDEX_SOFT_MAX = 3000     # characters: past this the index comes with a nudge to tighten it


def default_dir():
    return Path(os.environ.get("SOJOURN_HOME") or LOCAL / "assistant" / "home")


def work_dir():
    return Path(os.environ.get("SOJOURN_WORK") or LOCAL / "assistant" / "work")


def _linked(st):
    return stat.S_ISLNK(st.st_mode) or bool(getattr(st, "st_file_attributes", 0) & 0x400)   # reparse points too


class Home:
    def __init__(self, root=None, log=print):
        self.root = Path(root or default_dir())
        self.log = log
        self.root.mkdir(parents=True, exist_ok=True)
        (self.root / "jobs").mkdir(exist_ok=True)

    def files(self):
        """Every file kept, as paths relative to the folder (links aren't followed)."""
        out = []
        for d, dirs, names in os.walk(self.root, followlinks=False):
            dirs[:] = sorted(x for x in dirs if not _linked(os.lstat(os.path.join(d, x))))
            for n in sorted(names):
                f = Path(d) / n
                if stat.S_ISREG(os.lstat(f).st_mode):
                    out.append(f.relative_to(self.root).as_posix())
        return out

    def clear(self):
        """Everything kept, gone (the person's clear in the menu): notes, the index, jobs' notes."""
        n = 0
        for rel in self.files():
            try:
                (self.root / rel).unlink()
                n += 1
            except OSError:
                pass
        for d, dirs, _ in sorted(os.walk(self.root), key=lambda x: -len(x[0])):
            if Path(d) not in (self.root, self.root / "jobs"):
                try:
                    os.rmdir(d)
                except OSError:
                    pass
        return n

    def snapshot(self):
        """The index as it stands, with any note it doesn't list, and a digest to tell when it changed."""
        files = self.files()
        # jobs' notes and a session's handoff (brain.py: read once by the next session) aren't memory
        notes = [f for f in files if f != INDEX and not f.startswith("jobs/") and not f.startswith("handoff-")]
        try:
            index = (self.root / INDEX).read_text(encoding="utf-8", errors="replace").strip() if INDEX in files else ""
        except OSError:
            index = ""
        unlisted = [f for f in notes if f not in index]
        if not index and not notes:
            text = f"Your memory ({self.root}) is empty so far."
        else:
            text = (f"Your memory ({self.root}): {len(notes)} note{'s' if len(notes) != 1 else ''}; the index, {INDEX}, "
                    "as it stands now:\n" + (index or "(empty)")
                    + (f"\n(notes the index doesn't list: {', '.join(unlisted)})" if unlisted else "")
                    + (f"\n(the index is {len(index)} characters: tighten it)" if len(index) > INDEX_SOFT_MAX else ""))
        return text, hashlib.sha1(text.encode("utf-8")).hexdigest()
