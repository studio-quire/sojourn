"""Sojourn's own words in each language (docs/languages.md): the catalogs in src/Sojourn.Session/Lang/*.json.

  python tools/lang.py check [--lang de]     every entry translated in every language (or one), templates' holes kept,
                                              no key twice; exit 1 if anything's missing or wrong
  python tools/lang.py scan                  strings the code passes to Lang.T/F/Tn as literals that no catalog has;
                                              with SOJOURN_LANG_MISSING=<file> set on a run (the client, the launcher),
                                              also what it asked for at run time (draw-time menus, composed lines)
  python tools/lang.py missing de            the entries without a German translation, as JSON (key -> note), for a
                                              translator
  python tools/lang.py merge de FILE         a translator's JSON (English key -> German) into the catalogs; keys no
                                              catalog has are reported, not added
  python tools/lang.py stats                 entries per file, translations per language

Standard library only.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CATALOGS = ROOT / "src" / "Sojourn.Session" / "Lang"
SRC = ROOT / "src"
LANGS = ["az", "da", "de", "es", "fr", "it", "pl", "pt", "tr", "ru", "ja", "zh-Hans", "zh"]
HOLE = re.compile(r"\{(\d)\}")


def load(path):
    """A catalog file, refusing a key given twice (json would keep the last silently)."""
    dupes = []

    def pairs(items):
        seen = {}
        for k, v in items:
            if k in seen:
                dupes.append(k)
            seen[k] = v
        return seen

    data = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=pairs)
    return data, dupes


def catalogs():
    return {p: load(p) for p in sorted(CATALOGS.glob("*.json"))}


def save(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")


def english(key):
    """A key without its context ("look#assistant" -> "look")."""
    i = key.rfind("#")
    return key[:i] if i > 0 and "}" not in key[i:] else key


def check(only=None):
    bad = 0
    seen = {}
    langs = [only] if only else LANGS
    missing = {l: [] for l in langs}
    for path, (data, dupes) in catalogs().items():
        for k in dupes:
            print(f"{path.name}: {k!r} given twice")
            bad += 1
        for key, tr in data.items():
            if key in seen:
                print(f"{path.name}: {key!r} is in {seen[key]} too")
                bad += 1
            seen[key] = path.name
            if not isinstance(tr, dict):
                print(f"{path.name}: {key!r} isn't an object")
                bad += 1
                continue
            holes = sorted(set(HOLE.findall(english(key))))
            if english(key).strip() in ("{0}", "{0} {1}") or (holes and not HOLE.sub("", english(key)).strip()):
                print(f"{path.name}: {key!r} is only holes (it would match anything)")
                bad += 1
            for lang in langs:
                text = tr.get(lang)
                if not text:
                    missing[lang].append(key)
                    continue
                if sorted(set(HOLE.findall(text))) != holes:
                    print(f"{path.name}: {key!r} [{lang}] has holes {sorted(set(HOLE.findall(text)))}, the English {holes}")
                    bad += 1
    for lang, keys in missing.items():
        if keys:
            print(f"{lang}: {len(keys)} missing" + (": " + ", ".join(repr(k) for k in keys[:8]) + (" ..." if len(keys) > 8 else "")))
    total = sum(len(v) for v in missing.values())
    print(f"{len(seen)} entries; {total} translations missing; {bad} problems")
    return 1 if bad or total else 0


CALL = re.compile(r'Lang\.(?:T|Tn|F)\(\s*(@?)"((?:[^"\\]|\\.)*)"')


def unescape(s, verbatim):
    if verbatim:
        return s.replace('""', '"')
    return bytes(s, "utf-8").decode("unicode_escape").encode("latin-1").decode("utf-8") if "\\" in s else s


def scan():
    keys = set()
    templates = []
    for _, (data, _) in catalogs().items():
        for k in data:
            keys.add(k)
            e = english(k)
            if HOLE.search(e):
                templates.append(re.compile("^" + HOLE.sub(lambda m: "(.+?)", re.escape(e).replace(r"\{", "{").replace(r"\}", "}")) + "$", re.S))
    found = 0
    for path in sorted(SRC.rglob("*.cs")):
        if "obj" in path.parts or "bin" in path.parts or "Sojourn.Access" in path.parts:
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        for m in CALL.finditer(text):
            s = unescape(m.group(2), m.group(1) == "@")
            if text[m.end():].lstrip().startswith("+"):
                continue   # a key put together from parts: the run-time list catches it
            if s not in keys and not any(k.startswith(s + "#") for k in keys):
                line = text.count("\n", 0, m.start()) + 1
                print(f"{path.relative_to(ROOT)}:{line}: {s!r}")
                found += 1
    import os
    asked = os.environ.get("SOJOURN_LANG_MISSING")
    if asked and Path(asked).exists():
        for line in Path(asked).read_text(encoding="utf-8").splitlines():
            s = json.loads(line)
            if s not in keys and not any(t.match(s) for t in templates):
                print(f"(asked at run time) {s!r}")
                found += 1
    print(f"{found} not in any catalog")
    return 1 if found else 0


def missing(lang):
    out = {}
    for _, (data, _) in catalogs().items():
        for key, tr in data.items():
            if not tr.get(lang):
                out[key] = tr.get("_", "")
    print(json.dumps(out, ensure_ascii=False, indent=2))


def merge(lang, file):
    given = json.loads(Path(file).read_text(encoding="utf-8"))
    left = dict(given)
    for path, (data, _) in catalogs().items():
        changed = False
        for key, tr in data.items():
            if key in given and isinstance(given[key], str) and given[key].strip():
                if tr.get(lang) != given[key]:
                    tr[lang] = given[key]
                    changed = True
                left.pop(key, None)
        if changed:
            save(path, data)
    for key in left:
        print(f"not in any catalog: {key!r}")
    print(f"{lang}: {len(given) - len(left)} merged, {len(left)} unknown")


def stats():
    counts = {l: 0 for l in LANGS}
    total = 0
    for path, (data, _) in catalogs().items():
        print(f"{path.name}: {len(data)}")
        total += len(data)
        for tr in data.values():
            for l in LANGS:
                if tr.get(l):
                    counts[l] += 1
    print(f"{total} entries; " + ", ".join(f"{l} {n}" for l, n in counts.items()))


def main(argv):
    sys.stdout.reconfigure(encoding="utf-8")   # (the console's codepage can't print every language)
    if not argv or argv[0] in ("-h", "--help"):
        print(__doc__)
        return 0
    cmd = argv[0]
    if cmd == "check":
        return check(argv[argv.index("--lang") + 1] if "--lang" in argv else None)
    if cmd == "scan":
        return scan()
    if cmd == "missing" and len(argv) > 1:
        return missing(argv[1]) or 0
    if cmd == "merge" and len(argv) > 2:
        return merge(argv[1], argv[2]) or 0
    if cmd == "stats":
        return stats() or 0
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
