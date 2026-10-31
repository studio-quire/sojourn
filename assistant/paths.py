"""Where the sidecar keeps its things. From the repo: local/ beside the code, as ever. In a packaged Sojourn Access
(docs/access-app.md > Packaging), the program's folder is read-only and the app's data folder takes local/'s place:
Assistant/Sidecar.cs sets SOJOURN_LOCAL to it (%LOCALAPPDATA%\\Sojourn Access, or --base), so the memory, scratch
folder, settings and prompts go to <data>/assistant/... Written by Claude Opus 5.5, 2026-10-30.
"""
import os
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent                                   # the repo, or the packaged program's folder
PACKAGED = bool(os.environ.get("SOJOURN_LOCAL"))
LOCAL = Path(os.environ["SOJOURN_LOCAL"]) if PACKAGED else ROOT / "local"


def packaged_claude():
    """Claude Code's own folder for a packaged build's sessions on a key or token (provider.py sets CLAUDE_CONFIG_DIR
    to it), so nothing of the assistant's lands in the person's profile; with their own signed-in Claude account the
    sessions use ~/.claude as Claude Code always does."""
    return LOCAL / "assistant" / "claude"


def claude_dirs():
    """Every folder Claude Code may keep the sessions' things in (its config, big tool results by working folder)."""
    return [Path.home() / ".claude"] + ([packaged_claude()] if PACKAGED else [])
