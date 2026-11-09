using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace Sojourn.Core.World;

/// <summary>How a person's name is worded for the person using the app (Sojourn Access's People's names, Alt+0; the VR
/// client shows names as written).</summary>
public enum NameStyle
{
    /// <summary>The display name as written, as other viewers show it (Radegast's "display name only").</summary>
    Written,
    /// <summary>The display name in plain letters (<see cref="PlainName.Of"/>), the username when none are left.</summary>
    Plain,
    /// <summary>That and the username after it when they differ (Radegast's "smart", with the display name made plain).</summary>
    Both,
    /// <summary>The username alone (Radegast's display names off).</summary>
    Username,
}

/// <summary>
/// Display names written so a screen reader can say them. Second Life's are often decorative Unicode: mathematical script
/// or bold letters, circled, squared or fullwidth ones, small caps, upside-down text, lookalikes from Greek and Cyrillic,
/// symbols and emoji, which a screen reader reads as nothing, as a string of symbol names or as nonsense, so a blind person
/// may not know who's speaking. <see cref="Of"/> gives the plain letters under the decoration ("Marigold" for
/// "𝓜𝓪𝓻𝓲𝓰𝓸𝓵𝓭 ✨"), or "" when none are left; <see cref="Said"/> and <see cref="Complete"/> word a name by a
/// <see cref="NameStyle"/>. Names in a script of their own (Japanese, Russian, Arabic) are left as they are: only a word
/// mixing alphabets has its Greek or Cyrillic letters read as the Latin ones they look like.
/// Depends only on .NET (tools/namecheck links this file and checks it). Written by Claude Opus 5.5, 2026-11-08.
/// </summary>
public static class PlainName
{
    /// <summary>The name in plain letters (and digits), "" if it has none (all symbols, emoji or punctuation).</summary>
    public static string Of(string? written)
    {
        if (string.IsNullOrWhiteSpace(written)) return "";
        var s = written.Trim();
        if (Ordinary(s))
        {
            if (s.Contains(' ')) s = Spaces(s);   // "M a r i g o l d"
            return s.Any(char.IsLetterOrDigit) ? s : "";
        }
        if (Cache.TryGetValue(s, out var known)) return known;
        var plain = Fold(s);
        if (Cache.Count > 4096) Cache.Clear();
        Cache[s] = plain;
        return plain;
    }

    static readonly ConcurrentDictionary<string, string> Cache = new();

    /// <summary>The short name said for someone: in lines, lists of people nearby, conversations' names.</summary>
    /// <param name="display">Their display name as written (null or empty: none known).</param>
    /// <param name="user">Their username ("First Last", or "first" for a Resident).</param>
    public static string Said(string? display, string user, NameStyle style)
    {
        display = display?.Trim() ?? "";
        user = user.Trim();
        switch (style)
        {
            case NameStyle.Written: return display.Length > 0 ? display : user;
            case NameStyle.Username: return user.Length > 0 ? user : Of(display) is { Length: > 0 } p0 ? p0 : display;
        }
        var plain = Of(display);
        if (user.Length == 0) return plain.Length > 0 ? plain : display;
        if (plain.Length == 0) return user;
        return style == NameStyle.Both && !Same(plain, user) ? $"{plain} ({user})" : plain;
    }

    /// <summary>The full name, for a profile, a friends list, a group's members: "Display (username)" when they differ (the
    /// viewer's complete name), the display name worded by the style.</summary>
    public static string Complete(string? display, string user, bool isDefault, NameStyle style)
    {
        display = display?.Trim() ?? "";
        user = user.Trim();
        if (user.Length == 0) return Said(display, user, style);
        if (style == NameStyle.Username || isDefault || display.Length == 0) return user;
        if (style == NameStyle.Written) return display != user ? $"{display} ({user})" : user;
        var plain = Of(display);
        return plain.Length == 0 ? user : Same(plain, user) ? plain : $"{plain} ({user})";
    }

    /// <summary>Whether two names say the same, ignoring case, spaces and punctuation ("Kestrel Tester", "kestrel.tester").</summary>
    public static bool Same(string a, string b) => Key(a) == Key(b);

    static string Key(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    // ---- folding -----------------------------------------------------------------------------------------------------

    /// <summary>A name with nothing to fold: ASCII letters and digits, Latin-1 and Latin Extended-A letters (accents,
    /// which screen readers say), spaces, a word's own apostrophes, hyphens and full stops, and "&" (said "and").</summary>
    static bool Ordinary(string s)
    {
        foreach (var c in s)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or ' ' or '\'' or '-' or '.' or '&') continue;
            if (c is >= 'À' and <= 'ſ' and not '×' and not '÷' and not 'ſ') continue;
            return false;
        }
        return true;
    }

    static string Fold(string s)
    {
        s = Enclosed(s);   // letters NFKC turns into "(a)", or leaves alone: negative circled and squared, regional indicators
        s = Upright(s);    // upside-down text the right way up (before NFKC, which makes ſ an s)
        s = s.Normalize(NormalizationForm.FormKC);   // mathematical, fullwidth, circled, super- and subscript, ℓ, ﬁ ...
        s = Lookalikes(s); // small caps, hooked and barred letters, alphabets mixed in a word, currency signs in a word
        s = Strip(s);      // what isn't a letter, a digit or a word's own punctuation
        s = Spaces(s);
        return s.EnumerateRunes().Any(Rune.IsLetterOrDigit) ? s : "";
    }

    static string Enclosed(string s)
    {
        var runes = s.EnumerateRunes().ToList();
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < runes.Count; i++)
        {
            int c = runes[i].Value;
            if (c is >= 0x1F1E6 and <= 0x1F1FF)
            {
                // regional indicators: two together are a flag (left out), one alone or a longer run spells letters
                int end = i;
                while (end + 1 < runes.Count && runes[end + 1].Value is >= 0x1F1E6 and <= 0x1F1FF) end++;
                int n = end - i + 1;
                if (n is 2 or 4) sb.Append(' ');
                else for (int k = i; k <= end; k++) sb.Append((char)('A' + runes[k].Value - 0x1F1E6));
                i = end;
                continue;
            }
            char? letter = c switch
            {
                >= 0x1F150 and <= 0x1F169 => (char)('A' + c - 0x1F150),   // 🅐 negative circled
                >= 0x1F170 and <= 0x1F189 => (char)('A' + c - 0x1F170),   // 🅰 negative squared
                >= 0x1F130 and <= 0x1F149 => (char)('A' + c - 0x1F130),   // 🄰 squared
                >= 0x1F110 and <= 0x1F129 => (char)('A' + c - 0x1F110),   // 🄐 parenthesized
                >= 0x249C and <= 0x24B5 => (char)('a' + c - 0x249C),       // ⒜ parenthesized
                _ => null,
            };
            if (letter is { } l) sb.Append(l); else sb.Append(runes[i].ToString());
        }
        return sb.ToString();
    }

    // ---- upside down -------------------------------------------------------------------------------------------------

    // letters only upside-down text uses, and what each is the right way up; the plain ones it uses (d for p, u for n)
    // are swapped back too once the text is known to be upside down
    static readonly Dictionary<int, string> Turned = Pairs(
        "ɐa ɔc ǝe ɟf ƃg ᵷg ɥh ᴉi ɾj ʞk ʅl ɯm ɹr ʇt ʌv ʍw ʎy ∀A ꓭB ᗺB ƆC ᗡD ƎE ℲF ⅁G ſJ ꓘK ˥L ԀP ᴚR ⊥T ꓕT ∩U ΛV ⅄Y");
    static readonly Dictionary<int, string> TurnedToo = Pairs(
        "qb bq pd dp un nu MW WM ıi Ɩ1 ᄅ2 Ɛ3 ㄣ4 ϛ5 96 ㄥ7 69 ¡! ¿? ˙. ', ,'");

    static string Upright(string s)
    {
        int turned = 0, letters = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (Turned.ContainsKey(r.Value)) turned++;
            if (Rune.IsLetter(r) || Turned.ContainsKey(r.Value)) letters++;
        }
        if (turned < 2 || turned * 3 < letters) return s;
        var sb = new StringBuilder(s.Length);
        foreach (var r in s.EnumerateRunes().Reverse())
            sb.Append(Turned.TryGetValue(r.Value, out var t) ? t : TurnedToo.TryGetValue(r.Value, out var u) ? u : r.ToString());
        return sb.ToString();
    }

    // ---- lookalikes --------------------------------------------------------------------------------------------------

    static readonly Dictionary<int, string> SmallCaps = Pairs(
        "ᴀa ʙb ᴄc ᴅd ᴇe ꜰf ɢg ʜh ɪi ᴊj ᴋk ʟl ᴍm ɴn ᴏo ᴘp ʀr ꜱs ᴛt ᴜu ᴠv ᴡw ʏy ᴢz ᴎn ᴙr ᴚr ᴃb");

    // Latin letters with hooks, bars and tails that the fancy-text styles use for plain ones, and few languages do
    // (Polish ł, Maltese ħ, Turkish ı, Romanian ș, Vietnamese ơ stay)
    static readonly Dictionary<int, string> Hooked = Pairs(
        "ƀb ɓb ƃb ƈc ɕc ȼc ɗd ɖd ƌd ȡd ɛe ɘe ɇe ǝe ƒf ɠg ɡg ǥg ɦh ɧh ⱨh ɨi ɩi ʝj ɉj ƙk ⱪk ɭl ɬl ƚl ⱡl ɱm ɲn ɳn ƞn ɵo ƥp ʠq ɋq "
        + "ʅl ɽr ɾr ɍr ʂs ȿs ƭt ʈt ʉu ʋv ⱱv ⱳw ƴy ɏy ȥz ʐz ʑz ƶz ɀz ⱬz ɐa ɑa ɒa "
        + "ɃB ƁB ƇC ȻC ƊD ƉD ƋD ƐE ɆE ƑF ƓG ǤG ⱧH ƗI ɈJ ƘK ⱩK ȽL ⱠL ⱮM ƝN ȠN ƟO ƤP ⱣP ɊQ ⱤR ɌR ƬT ƮT ȾT ɄU ƲV ⱲW ƳY ɎY ȤZ ƵZ ⱫZ");

    // Greek and Cyrillic letters read as the Latin ones they stand in for, in a word that mixes alphabets ("Jαѕмⅰηє",
    // "кєѕтяєℓ"): look-alikes, and the faux-Cyrillic style's (Я for R, И for N, Д for A)
    static readonly Dictionary<int, string> Greek = Pairs(
        "αa βb γy δd εe ζz ηn θo ιi κk μu νv οo πn ρp ςc σo τt υu χx ωw ϲc ϳj ϰk ϱp ϐb "
        + "ΑA ΒB ΔA ΕE ΖZ ΗH ΘO ΙI ΚK ΛA ΜM ΝN ΞE ΟO ΠN ΡP ΣE ΤT ΥY ΦO ΧX ϹC");
    static readonly Dictionary<int, string> Cyrillic = Pairs(
        "аa бb вb гr дa еe єe эe жx иn кk мm нh оo пn рp сc тt уy фo хx цu чy шw щw ъb ьb яr ѕs іi јj һh ӏl ԁd ԃd ԛq ԝw ѵv үy ғf ќk ўy ѡw ҝk ҽe "
        + "АA БB ВB ГR ДA ЕE ЄE ЭE ЖX ИN КK МM НH ОO ПN РP СC ТT УY ФO ХX ЦU ЧY ШW ЩW ЯR ЅS ІI ЈJ ҺH ӀI ԚQ ԜW ѴV ҮY ҒF ЌK ЎY ҜK ҼE");
    // Cherokee letters standing for Latin ones, always (Cherokee display names are rare): capitals that look like Latin
    // ones ("ᏚᏢᎪᎡᏦᏞᎬ"), and the fancy-text style that spells lowercase with them ("ᏦᏋᏕᏖᏒᏋᏝ")
    static readonly Dictionary<int, string> Cherokee = Pairs(
        "ᎪA ᏴB ᏟC ᎠD ᎬE ᏀG ᎻH ᎥI ᎫJ ᏦK ᏞL ᎷM ᏢP ᎡR ᏒR ᏚS ᏕS ᎢT ᏙV ᏔW ᎳW ᎩY ᏃZ ᎾO ᏫO "
        + "Ꮧa Ᏸb Ꮄd Ꮛe Ꭶf Ꮆg Ꮒh Ꮰj Ꮭl Ꮑn Ꭷo Ꭾp Ꭴq Ꮦt Ꮼu Ꮗw");
    // the fancy-text style spelling with Chinese characters, bopomofo and syllabics ("爪卂尺丨Ꮆㄖㄥᗪ"): only in a name with
    // bopomofo or syllabics in it, which Chinese names aren't written with
    static readonly Dictionary<int, string> Asian = Pairs(
        "卂A 乃B 匚C ᗪD 乇E 千F 卄H 丨I ﾌJ ㄥL 爪M 几N ㄖO 卩P 尺R 丂S ㄒT ㄩU ᐯV 山W 乂X ㄚY 乙Z");
    static readonly Dictionary<int, string> Currency = Pairs(
        "₳A ฿B ₵C ₡C ¢c ₫d €E ₣F ₲G ₭K £L ₤L ₥m ₦N ₱P ₽P ₴S $S ₮T ₸T ₩W ¥Y");

    enum Script { None, Latin, Greek, Cyrillic, Cherokee, Other }

    static Script ScriptOf(Rune r)
    {
        if (!Rune.IsLetter(r)) return Script.None;
        return r.Value switch
        {
            < 0x0250 or (>= 0x1E00 and <= 0x1EFF) or (>= 0x2C60 and <= 0x2C7F) or (>= 0xA720 and <= 0xA7FF) or (>= 0x0250 and <= 0x02AF)
                or (>= 0x1D00 and <= 0x1DBF) or (>= 0xAB30 and <= 0xAB6F) => Script.Latin,
            (>= 0x0370 and <= 0x03FF) or (>= 0x1F00 and <= 0x1FFF) => Script.Greek,
            (>= 0x0400 and <= 0x052F) or (>= 0x1C80 and <= 0x1C8F) or (>= 0x2DE0 and <= 0x2DFF) or (>= 0xA640 and <= 0xA69F) => Script.Cyrillic,
            (>= 0x13A0 and <= 0x13FF) or (>= 0xAB70 and <= 0xABBF) => Script.Cherokee,
            _ => Script.Other,
        };
    }

    static string Lookalikes(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool asian = s.EnumerateRunes().Any(r => r.Value is (>= 0x3100 and <= 0x312F) or (>= 0x1400 and <= 0x167F));
        foreach (var word in Words(s))
        {
            if (word.IsSpace) { sb.Append(word.Text); continue; }
            var runes = word.Text.EnumerateRunes().ToList();
            var scripts = runes.Select(ScriptOf).Where(x => x is Script.Latin or Script.Greek or Script.Cyrillic).Distinct().Count();
            bool mixed = scripts >= 2;
            bool smallCaps = runes.Any(r => SmallCaps.ContainsKey(r.Value));
            bool changed = false;
            var mapped = new List<string>(runes.Count);
            foreach (var r in runes)
            {
                int c = r.Value;
                string? m;
                // ǫ is small caps' q only among other small caps (it's a letter of its own elsewhere)
                if (c == 'ǫ' && smallCaps) m = "q";
                else if (SmallCaps.TryGetValue(c, out m) || Hooked.TryGetValue(c, out m)) { }
                else if (Cherokee.TryGetValue(CherokeeCapital(c), out m)) { if (c is >= 0xAB70 and <= 0xABBF) m = m.ToLowerInvariant(); }
                else if (asian && Asian.TryGetValue(c, out m)) { }
                else if (mixed && ScriptOf(r) is Script.Greek or Script.Cyrillic)
                {
                    // an accented one by its base letter (ά, ё)
                    var b = Rune.GetRuneAt(r.ToString().Normalize(NormalizationForm.FormD), 0).Value;
                    if (!Greek.TryGetValue(b, out m)) Cyrillic.TryGetValue(b, out m);
                }
                if (m != null) changed = true;
                mapped.Add(m ?? r.ToString());
            }
            // a currency sign standing for a letter, in a word that has letters ("Ro$ie", "₴₱₳Ɽ₭ⱠɆ")
            bool hasLetters = mapped.Any(x => x.EnumerateRunes().Any(Rune.IsLetter));
            for (int i = 0; i < mapped.Count && hasLetters; i++)
                if (mapped[i].Length <= 2 && Currency.TryGetValue(Rune.GetRuneAt(mapped[i], 0).Value, out var cur)) { mapped[i] = cur; changed = true; }
            var w = string.Concat(mapped);
            // stand-ins pick their case by what looks right ("JυηιρҽR"): a word of mixed case made one, as a name is
            // written, so a screen reader doesn't break it up ("Juniper", not "Juni pe R")
            if (changed && w.Skip(1).Any(char.IsUpper) && w.Any(char.IsLower))
                w = char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
            sb.Append(w);
        }
        return sb.ToString();
    }

    static int CherokeeCapital(int c) => c is >= 0xAB70 and <= 0xABBF ? 0x13A0 + (c - 0xAB70) : c;

    readonly record struct Word(string Text, bool IsSpace);

    static IEnumerable<Word> Words(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            bool space = char.IsWhiteSpace(s[i]);
            int j = i;
            while (j < s.Length && char.IsWhiteSpace(s[j]) == space) j++;
            yield return new Word(s[i..j], space);
            i = j;
        }
    }

    // ---- what's left -------------------------------------------------------------------------------------------------

    static bool PlainScript(Script s) => s is Script.Latin or Script.Greek or Script.Cyrillic;

    static string Strip(string s)
    {
        var runes = s.EnumerateRunes().ToList();
        var sb = new StringBuilder(s.Length);
        Rune? lastKept = null;
        bool LetterOrDigit(Rune r) => Rune.IsLetterOrDigit(r);
        for (int i = 0; i < runes.Count; i++)
        {
            var r = runes[i];
            var next = i + 1 < runes.Count ? runes[i + 1] : (Rune?)null;
            switch (Rune.GetUnicodeCategory(r))
            {
                case UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                    or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber:
                    sb.Append(r.ToString());
                    lastKept = r;
                    break;
                case UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark:
                    // a vowel sign or virama of Devanagari, Thai, Arabic stays; over Latin, Greek or Cyrillic, NFKC has
                    // already joined the accents it can, so what's left is decoration (strikethrough, underline, zalgo)
                    if (lastKept is { } lk && Rune.IsLetter(lk) && !PlainScript(ScriptOf(lk)) && ScriptOf(lk) != Script.None)
                        sb.Append(r.ToString());
                    break;
                case UnicodeCategory.Format:
                    // joiners inside a word of a script that needs them (Persian, Indic); otherwise invisible separators
                    if (r.Value is 0x200C or 0x200D && lastKept is { } lf && Rune.IsLetter(lf) && !PlainScript(ScriptOf(lf))
                        && next is { } nf && Rune.IsLetter(nf) && !PlainScript(ScriptOf(nf)))
                        sb.Append(r.ToString());
                    break;
                case UnicodeCategory.DashPunctuation or UnicodeCategory.OtherPunctuation or UnicodeCategory.FinalQuotePunctuation
                    when r.Value is '-' or '\'' or '.' or 0x2019 or 0x2010 or 0x2011
                         && lastKept is { } lp && LetterOrDigit(lp) && sb.Length > 0 && !char.IsWhiteSpace(sb[^1]) && next is { } np && LetterOrDigit(np):
                    // a word's own: O'Brien, Mary-Jane, J.R
                    sb.Append(r.Value is 0x2019 ? "'" : r.Value is 0x2010 or 0x2011 ? "-" : r.ToString());
                    break;
                case UnicodeCategory.OtherPunctuation when r.Value == '&':
                    sb.Append(" & ");   // said "and"
                    lastKept = null;
                    break;
                default:
                    // symbols, emoji, brackets, other punctuation, underscores: a gap between words
                    sb.Append(' ');
                    lastKept = null;
                    break;
            }
        }
        return sb.ToString();
    }

    // single letters standing alone in a name that has Latin ones, or in a name of nothing else: a smiley (ツ) or a
    // flourish (彡, 乂) rather than a word
    static readonly HashSet<string> Flourishes = new() { "ツ", "シ", "ッ", "ヅ", "彡", "乂", "卍", "卐", "〆", "ゞ", "ヾ", "ㄨ" };

    static string Spaces(string s)
    {
        var words = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.All(Flourishes.Contains) || words.Any(w => w.EnumerateRunes().Any(r => ScriptOf(r) == Script.Latin)))
            words.RemoveAll(Flourishes.Contains);
        // letters spaced out ("S p a r k l e", or what was "🇸 🇵 🇦 ..."): three or more single letters in a row, one word
        var joined = new List<string>();
        for (int i = 0; i < words.Count; i++)
        {
            int j = i;
            while (j < words.Count && Single(words[j])) j++;
            if (j - i >= 3) { joined.Add(string.Concat(words.Skip(i).Take(j - i))); i = j - 1; }
            else joined.Add(words[i]);
        }
        return string.Join(' ', joined);
    }

    static bool Single(string w) => w.EnumerateRunes().Count() == 1 && Rune.IsLetter(w.EnumerateRunes().First());

    /// <summary>"ɐa ɔc": each pair a character (one code point) and what it reads as.</summary>
    static Dictionary<int, string> Pairs(string pairs)
    {
        var d = new Dictionary<int, string>();
        foreach (var p in pairs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var r = p.EnumerateRunes().ToList();
            d[r[0].Value] = string.Concat(r.Skip(1).Select(x => x.ToString()));
        }
        return d;
    }
}
