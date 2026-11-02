# Languages

Sojourn's own words in the person's language: the VR client (menus and their tips, the panel's own words and
confirmations, the feed's own lines and notices, cards, boards, the keyboard, the login sheet) and the launcher. Written
by Claude Opus 5.5, 2026-11-01. Agent-facing. Sojourn Access stays English for now (its own app and testers).

## The languages

The official viewer's and Firestorm's (their `xui` folders): English, Azərbaycanca, Dansk, Deutsch, Español, Français,
Italiano, Polski, Português (Brazil's, as the viewers'), Türkçe, Русский, 日本語 and 中文 (Traditional, as both viewers
ship it), and Simplified Chinese beside it (`zh-Hans`; the user, 2026-11-01), which neither viewer has, so its SL
terms are mainland usage rather than a viewer's. Translated by Claude from scratch, using Second Life's own words
where the official viewer has them (Firestorm's for Azerbaijani), so residents see familiar terms. Each language
addresses the person as the viewer's own translation does (German Sie, kept so by the user's call, 2026-11-01; Turkish
and Azerbaijani siz, Russian вы, French vous; du, tú, tu, ty, você elsewhere; Japanese です・ます in sentences). Where the
viewer's own term was wrong or clashed (a word it uses for two things), the translator chose another; otherwise SL's
word is the viewer's. **No native speaker has read any of it yet**: corrections come in through problem reports after
the release (the user, 2026-11-01).

## How it's chosen

- `uiLanguage` in vr-settings.json (VrSettings.UiLanguage): `auto` by default, Windows' display language when Sojourn
  has it, else English; or a code. Chinese goes by region (`Lang.For`): Taiwan, Hong Kong and Macau (or a culture
  marked Hant) get Traditional, mainland China, Singapore and Chinese without a region Simplified (the user,
  2026-11-01). Any Portuguese is Brazil's.
- The headset's menu: view > **language**, a stepper through "as Windows" and each language by its own name (in the
  simple menu too). Its label is the language's word with " · language" after it, so a person who picked one they
  can't read finds the way back. The launcher: a globe at the foot's right (`docs/launcher.md` > Languages), the same
  setting. SojournSettings `language` for the assistant.
- A change applies at once: the menu redraws; what's already shown (feed lines, a card) stays as it was written.
- `--lang CODE` on the client overrides it for a run (the shots below).
- The assistant answers in the language it's spoken to anyway; its context line also says which language the menus
  are in, so it uses that one when the person's language isn't clear yet (`assistant/brain.py`, the client's prefs).

## How it's built

- `src/Sojourn.Session/Lang/Lang.cs`: `Lang.T("hold to talk")` for a fixed string, `Lang.F("{0} offers a teleport",
  who)` for a composed one (a whole-sentence template, never fragments glued), `Lang.T(text, context)` for an English
  word that needs two translations (key `look#assistant`). Missing translations fall back to English; unknown text
  comes back unchanged.
- The catalogs, `src/Sojourn.Session/Lang/*.json` (menu, tips, panel, feed, login, launcher), embedded in the session
  and the launcher: each English string once, with every language's translation beside it and `_`, a note for
  translators (where it shows, what each {n} holds, how much room). **Adding a string**: call T or F in the code, add
  the entry to the catalog of its area with its note and translations.
- **The drawn menu translates as it draws** (`Panel.DrawMenu`): tabs, labels, values, options, notes, groups, help, the
  about section and tips go through T there, while the rows keep their English labels as keys (MenuGuide's tips, the
  tests' presses). T matches templates when a string isn't listed whole, so a value built in English ("haiku effort",
  "≤ 3 days") finds "{0} effort", "≤ {0} days".
- The sidecar's own English on the panel (working statuses, notices, "Stopped.") is translated the same way where the
  panel draws it, from templates in panel.json; `Panel.Own` limits that to results shaped like the sidecar's own, so
  the assistant's answers are never touched.
- The catch of matching templates: any key with a hole can match text of its shape handed to T (a status "changing
  the colour" finds "changing {0}" and comes out half translated, its hole still English). Mild in practice, since the
  longest template wins and F looks up exactly; keep templates specific (fixed words around every hole, never "{0}"
  alone, which `check` refuses).
- The same English with two meanings gets a context key: the menu passes its section as the context for labels,
  values and options (`yours#sound`, `camera#voice`), other code passes its own (`yours#owner`, `N#compass`).
- Never T in a `const`, a static field or anything computed once: a language change must reach it. Counts use
  count-neutral phrasings in each language rather than plural rules. Numbers keep invariant formatting.
- The agent API (descriptions, errors, setting values) and logs stay English: the assistant reads them.
- An app that never calls `Lang.Use` stays English: that's how Access keeps the session's shared strings in English.

## Fonts and layout

- No font ships: Windows' own faces cover every language. Segoe UI has Latin (Azerbaijani's ə too) and Cyrillic; for
  Japanese and Chinese the fallback (`Assistant/FallbackText.cs`) asks the system's fallback with the language's tag,
  which picks Yu Gothic UI for Japanese, Microsoft YaHei UI for Simplified Chinese (`zh-Hans`) and Microsoft JhengHei
  UI for Traditional (`zh-TW`) (all in every Windows 10
  and 11). The launcher sets WPF's `Language` for the same reason.
- Wrapping (`Panel.Wrap`, `Panel.Pieces`): Chinese and Japanese break between any two characters, never before
  closing punctuation and the small kana or after opening brackets.
- German and Russian run about a third longer: the menu's tabs go to two strips, segmented options give up their air
  before a row's name is cut, keys ellipsise.

## Checking it

- `python tools/lang.py check`: every entry in every language, templates' holes kept, no key twice. `scan`: literals
  passed to T/F that no catalog has; with `SOJOURN_LANG_MISSING=<file>` set on a run, also what the client asked for
  at run time (the drawn menus). `missing <code>` lists what a language lacks, as JSON for a translator; `merge <code>
  <file>` applies a translator's file.
- Pictures in a language: `--lang ja --panel-shots DIR` (the panel, the menus as mocks, tips, the feed, map, keyboard,
  HUD board, reader and cards; the mocks' own sample data stays English), `--lang de --login-shots DIR`, and
  `tools/launcher.py shots DIR --lang ru` (`docs/launcher.md` > Languages).
