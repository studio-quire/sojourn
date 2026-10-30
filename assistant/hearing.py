"""What helps the ears hear names: the words around the person (the client's `SojournAssistant hearing`: people near
them, friends, the region and parcel, places, what they wear and keep; the names in the assistant's memory notes), a
general SL vocabulary, and the ways they're used: a prompt for Whisper (within its prompt budget), a pass that puts a
name in where either local model heard something close to one, Soniox's context terms (cloud.py), and the context
line's names for Claude. docs/assistant.md > Hearing.

The words are fetched in the background (every REFRESH seconds while the client is up, and again when talk starts), so
nothing is fetched between the release and the transcript. They stay in memory only: never logged or written. In a
blind incognito spell the client hands back only what the person wears (the allowlist's rule), so the people and
places around them aren't in the prompt either: Whisper is local, but a name it put in where none was said would
reach the blind session in the transcript.
"""
import asyncio
import difflib
import re
import time
from pathlib import Path

REFRESH = 15.0

# Sojourn's and SL's own words that a general model mishears ("freecam" heard as "freak him", "culling" as
# "calling"): general terms only, nothing about one person (their friends, places and things come from the client and
# their memory). Short, and only words people actually say to it.
VOCAB = ["freecam", "culling plane", "draw distance", "impostors", "rez", "HUD", "prim", "mesh", "linkset", "Sojourn",
         "incognito", "teleport", "landmark", "notecard", "Lelutka", "Maitreya", "Catwa", "Legacy", "eBody", "AVsitter",
         "AnyPose", "Bento", "gacha", "Second Life"]

# ignored in item names when picking out what someone might say
_STOP = set("""the a an and or of for to in on with by my your new old v ver version mesh rigged unrigged bom hud
            wear me add box unpack copy mod trans no demo fitted fit set pack edition size sizes color colour colors
            colours texture textures alpha alphas body head hair top bottom pants shirt shoes dress skirt jacket
            boots socks gloves eyes skin shape tattoo applier hd lite basic deluxe free gift group""".split())


def _clean(name):
    """An item's or place's name as words someone would say: brackets, versions and separators gone."""
    s = re.sub(r"\([^)]*\)", " ", name)                    # (128, 54, 22), (wear me)
    s = re.sub(r"(?<=[A-Za-z])\.(?=[A-Za-z])", " ", s)        # LeLUTKA.EvoX.HEAD
    s = re.sub(r"[\[\]{}<>|:~*_#=+\"]", " ", s)
    s = re.sub(r"\bv?\d+(\.\d+)+\b|\b\d+\b", " ", s)
    s = re.sub(r"\s+-\s+|\s*/\s*|\s*\.\s+|\s+\.", " - ", s)
    return [p.strip(" .,-'") for p in s.split(" - ") if p.strip(" .,-'")]


def item_words(name):
    """The parts of an item's name worth hearing: its leading brand and named parts, without SL's filler words.
    "[Doux] - Mila Hair - BOM rigged (wear me)" -> ["Doux", "Mila"]."""
    out = []
    for part in _clean(name)[:3]:
        words = [w for w in part.split() if w.lower().strip(".,'") not in _STOP and len(w) > 1]
        if words:
            out.append(" ".join(words[:3]))
    return out


class Words:
    """The words around the person, kept fresh in the background: the client's (people, places, things) and the names
    in the assistant's memory notes (the people and places they talk about), when memory is on."""

    def __init__(self, api, log=print, home=None):
        self.api = api
        self.log = log
        self.home = Path(home) if home else None
        self.memory = True          # the person's memory switch (prefs)
        self.got = {}
        self.at = 0.0
        self._remembered, self._remembered_at = [], 0.0
        self._busy = False

    def fetch(self):
        if self._busy:
            return
        self._busy = True
        try:
            r = self.api.cmd("SojournAssistant", "hearing", 3)
            if isinstance(r, dict) and not r.get("error"):
                self.got, self.at = r, time.time()
        except Exception:   # an older client, or not logged in yet: hearing goes on without
            pass
        finally:
            self._busy = False
        if time.time() - self._remembered_at > 60:
            self._remembered, self._remembered_at = self._from_memory(), time.time()

    def _from_memory(self):
        """Names in the memory notes, most mentioned first: runs of capitalised words not at a sentence's start; a
        single word only if the notes never write it in lower case (so "Mirromere" counts and "Music" doesn't)."""
        if not self.home or not self.home.is_dir():
            return []
        counts, lower = {}, set()
        for f in sorted(self.home.glob("*.md"))[:200]:
            try:
                text = f.read_text(encoding="utf-8", errors="replace")[:20000]
            except OSError:
                continue
            text = re.sub(r"\S+\.md\b|\S+://\S+|`[^`]*`", " ", text)   # links to notes, addresses, code
            for sentence in re.split(r"[.!?:;\n]+", text):
                words = re.findall(r"[^\W\d_][\w'’]*", sentence)
                lower.update(w for w in words if w.islower())
                run = []
                for w in words[1:] + [""]:   # a sentence's first word is capitalised anyway
                    w = re.sub(r"['’]s$", "", w)
                    if w[:1].isupper() and len(run) < 3:
                        run.append(w)
                        continue
                    if run and not all(x.lower() in COMMON or x.lower() in _STOP for x in run):
                        n = " ".join(run)
                        counts[n] = counts.get(n, 0) + 1
                    run = [w] if w[:1].isupper() else []
        names = [n for n in counts if len(n) >= 3 and (" " in n or n.lower() not in lower)]
        return sorted(names, key=lambda n: -counts[n])[:40]

    async def keep_fresh(self):
        while True:
            await asyncio.to_thread(self.fetch)
            await asyncio.sleep(REFRESH)

    def refresh_soon(self):
        """Talk started: fetch again now if what we have is more than a few seconds old (never waited for)."""
        if time.time() - self.at > 3:
            asyncio.get_running_loop().run_in_executor(None, self.fetch)

    def ranked(self, blind=False):
        """Every word worth hearing, most relevant first: nearby people (nearest first), where they are, friends
        online, the names in their memory, the general vocabulary, what they wear, other friends, places they've been
        and their landmarks, their own folders. Blind (incognito, the assistant not let see): only what they wear and
        the general vocabulary, whatever was fetched before the spell."""
        g = self.got or {}
        blind = blind or bool(g.get("blind"))
        out, seen = [], set()

        def add(w):
            w = (w or "").strip()
            k = w.lower()
            if len(w) > 1 and k not in seen and len(w) <= 40:
                seen.add(k)
                out.append(w)

        def person(p):
            add(p.get("display_name"))
            if p.get("username") and p["username"].lower() != (p.get("display_name") or "").lower():
                add(p["username"])

        if not blind:
            for p in g.get("people") or []:
                person(p)
            for w in g.get("here") or []:
                add(w)
            for p in g.get("friends") or []:
                if p.get("online"):
                    person(p)
            if self.memory:
                for w in self._remembered:
                    add(w)
        for w in VOCAB:
            add(w)
        for n in g.get("worn") or []:
            for w in item_words(n):
                add(w)
        if not blind:
            for p in g.get("friends") or []:
                if not p.get("online"):
                    person(p)
            for n in g.get("places") or []:
                for w in _clean(n)[:2]:
                    add(w)
            for n in g.get("kept") or []:
                for w in item_words(n):
                    add(w)
        return out

    def names(self, limit=60, blind=False):
        """For the context line: the people, places and things (no general vocabulary)."""
        vocab = {v.lower() for v in VOCAB}
        return [w for w in self.ranked(blind) if w.lower() not in vocab][:limit]

    def prompt(self, tokenizer=None, budget=200, blind=False):
        """Whisper's initial prompt: as many ranked words as fit in `budget` tokens (faster-whisper keeps the last
        223), written least relevant first, so the closest to the speech are the most relevant and a cut, if the
        count is off, takes the least."""
        words, used = [], 0
        for w in self.ranked(blind):
            n = len(tokenizer.encode(" " + w + ",").ids) if tokenizer else len(w) // 3 + 2
            if used + n > budget:
                break
            words.append(w)
            used += n
        return ", ".join(reversed(words)) + "." if words else None


# --- the name pass (for Parakeet, which takes no prompt that helps it) -----------------------------------------------

def _key(s):
    """Letters only, spelled roughly as heard: ph/f, ck/k, c/k, y/i, z/s, th/t, doubled letters single."""
    s = re.sub(r"[^a-z]", "", s.lower())
    for a, b in (("ph", "f"), ("ck", "k"), ("c", "k"), ("y", "i"), ("z", "s"), ("dh", "d"), ("th", "t"), ("x", "ks")):
        s = s.replace(a, b)
    return re.sub(r"(.)\1+", r"\1", s)


def _skeleton(k):
    """A key's consonants (h after a consonant dropped): "frekhim" -> "frkm", "frekam" -> "frkm"."""
    return re.sub(r"(?<=[^aeiou])h", "", k[0] + re.sub(r"[aeiouw]", "", k[1:])) if k else k


# everyday words a name pass mustn't take for a name ("tell" for a Tessel, "anyone" for AnyPose, "rock" for a Rook):
# a span made only of everyday words needs a close fit by its spelling. An everyday word is one of these, or (with
# Whisper's tokenizer at hand) a word its vocabulary has as a single token, which the common English words are.
COMMON = set("""a about after again all also am an and any anyone anything are around as ask at away back be been
before being below best better big bit body both bring but by call called calling can close come could day did do does
done down each even every everyone far few find fine first fly for found friend from front get give go going good got
had has have he hear her here him his home how i if in into is it its just keep kind know last leave left let like
little look looks lot made make many may me mean might more most move much must my near need never new next no not now
of off oh ok okay old on once one only open or other our out over own people place plane play please put right said same
say says see seems send set she show side sit so some something soon stand still stop such sure take talk tell than
thank that the their them then there these they thing think this those though time to too turn two under up us very
want was way we well went were what when where which while who why will wish with without would yeah yes yet you
your""".split())


def everyday(tokenizer=None):
    """The test for an everyday word: COMMON, or a single token of Whisper's (a tokenizers.Tokenizer) vocabulary."""
    cache = {}

    def test(w):
        if w in COMMON:
            return True
        if tokenizer is None or not w:
            return False
        if w not in cache:
            cache[w] = len(tokenizer.encode(" " + w, add_special_tokens=False).ids) == 1
        return cache[w]
    return test


def fix_names(text, names, threshold=0.75, is_everyday=None):
    """Put a name in where the words heard are close to it ("Quill on" -> "Quillon"). A span already spelled as a
    name stays; a span one word longer than the name has to fit better than its own shorter parts (so "Quillon a" isn't
    taken for "Quillon"); short names (under 5 letters) only by an exact match of the letters. Letters are compared
    roughly as heard (_key) and by their consonants as a second, stricter way ("freak him" -> freecam). A span of
    everyday words only by its spelling (0.88: "Adam's" -> Addams, "velar" stays), or a whole consonant skeleton across
    several words."""
    words = text.split()
    if not words or not names:
        return text
    is_everyday = is_everyday or everyday()
    keys = [_key(w) for w in words]
    plain = [re.sub(r"[^a-z]", "", re.sub(r"['’]s$", "", w.lower())) for w in words]
    common = [is_everyday(p) for p in plain]

    def fit(a, b, nk, nsk):
        sk = "".join(keys[a:b])
        r = difflib.SequenceMatcher(None, sk, nk).ratio()
        if len(nsk) >= 4:
            r = max(r, difflib.SequenceMatcher(None, _skeleton(sk), nsk).ratio() - 0.1)
        return r

    def everyday_fit(a, b, nm, nsk):
        """Everyday words as a name: by spelling, or a whole skeleton over several words."""
        r = difflib.SequenceMatcher(None, "".join(plain[a:b]), re.sub(r"[^a-z]", "", nm.lower())).ratio()
        if b - a > 1 and len(nsk) >= 4 and _skeleton("".join(keys[a:b])) == nsk:
            r = max(r, 0.9)
        return r if r >= 0.88 else 0.0

    cands = []
    for nm in names:
        nk = _key(nm)
        if len(nk) < 3:
            continue
        nsk = _skeleton(nk)
        n = len(nm.split())
        for span in sorted({max(1, n - 1), n, n + 1}):
            for i in range(len(words) - span + 1):
                sk = "".join(keys[i:i + span])
                if not sk:
                    continue
                every = all(common[i:i + span])
                if every:
                    r = 1.0 if "".join(plain[i:i + span]) == re.sub(r"[^a-z]", "", nm.lower()) else everyday_fit(i, i + span, nm, nsk)
                    if not r:
                        continue
                elif sk == nk:
                    r = 1.0
                elif len(nk) < 5:
                    continue
                else:
                    r = fit(i, i + span, nk, nsk)
                    if r < threshold:
                        continue
                if r < 1.0:
                    parts = [(a, b) for a in range(i, i + span) for b in range(a + 1, i + span + 1) if (a, b) != (i, i + span)]
                    if any(fit(a, b, nk, nsk) >= r - 0.02 for a, b in parts):
                        continue   # a shorter part fits as well: the extra word isn't part of the name
                cands.append((r, -span, i, span, nm))
    cands.sort(reverse=True)
    used = set()
    out = list(words)
    for r, _, i, span, nm in cands:
        if any(j in used for j in range(i, i + span)):
            continue
        used.update(range(i, i + span))
        if r == 1.0 and span == len(nm.split()) and " ".join(words[i:i + span]).strip(".,?!;:").lower() == nm.lower():
            continue   # already the name, as said
        lead = re.match(r"^\W*", words[i]).group()
        trail = re.search(r"(['’]s)?[.,?!;:]*$", words[i + span - 1]).group()
        out[i] = lead + nm + trail
        for j in range(i + 1, i + span):
            out[j] = None
    return " ".join(w for w in out if w is not None)
