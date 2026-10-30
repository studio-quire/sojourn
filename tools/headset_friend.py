"""A talking companion for a headset voice session: Voice Friend stands near Sojourn Pilot's start on the local grid,
speaking a looped WAV with an open mic, and records what it hears (so a session can check what reached it).

  python tools/headset_friend.py [--minutes 40] [--at 131,118]     needs: python tools/grid.py voice up
"""
import argparse
import os
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DOTNET = Path(os.environ["LOCALAPPDATA"]) / "dotnet-sdk" / "dotnet.exe"
CLIENT = ROOT / "src" / "Sojourn.Client" / "bin" / "Release" / "net10.0" / "SojournVR.dll"
OUT = ROOT / "local" / "voice" / "headset"

ap = argparse.ArgumentParser()
ap.add_argument("--minutes", default="40")
ap.add_argument("--at", default="131,118")
ap.add_argument("--wav", default=str(ROOT / "local" / "voice" / "rt" / "source.wav"))
a = ap.parse_args()
OUT.mkdir(parents=True, exist_ok=True)
x, y = a.at.split(",")
env = dict(os.environ, SOJOURN_VOICE_MIC_FILE=f"loop:{a.wav}", SOJOURN_VOICE_OUT_FILE=str(OUT / "friend-heard.wav"))
log = open(OUT / "friend.log", "w", encoding="utf-8")
p = subprocess.Popen([str(DOTNET), str(CLIENT), "--window", "320x180", "--fps", "5", "--no-assistant",
                      "--first", "Voice", "--last", "Friend", "--password", "sojourn-local", "--bridge-port", "9731",
                      "--base-dir", str(ROOT / "local" / "voice" / "friend"), "--start", f"uri:Atelier&{x}&{y}&24",
                      "--voice-mode", "open", "--voice-unmuted", "--minutes", a.minutes],
                     cwd=ROOT, env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                     creationflags=0x00004000 | 0x00000008)
print(f"Voice Friend started (pid {p.pid}), log {OUT / 'friend.log'}, hearing to {OUT / 'friend-heard.wav'}")
