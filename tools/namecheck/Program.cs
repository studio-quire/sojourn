// tools/namecheck: PlainName (src/Sojourn.Core/World/PlainName.cs) against made-up names in the decorative styles Second
// Life's display names use. Exit code: the number of failed checks. With names: each one's plain form printed.
using System.Text;
using Sojourn.Core.World;

if (args.Length > 0)
{
    foreach (var a in args) Console.WriteLine($"{a} -> \"{PlainName.Of(a)}\"");
    return 0;
}

int failed = 0, passed = 0;
void Check(string what, string got, string want)
{
    bool ok = got == want;
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}: \"{got}\"{(ok ? "" : $" (wanted \"{want}\")")}");
    if (ok) passed++; else failed++;
}
void Plain(string what, string written, string want) => Check($"{what} ({written})", PlainName.Of(written), want);

// a whole alphabet's style by its block: capitals at first, small letters 26 on; holes filled by Letterlike Symbols
string Styled(string text, int first, string holes = "")
{
    var fill = new Dictionary<char, string>();
    for (int i = 0; i + 1 < holes.Length; i += 2) fill[holes[i]] = holes[i + 1].ToString();
    var sb = new StringBuilder();
    foreach (var c in text)
        sb.Append(fill.TryGetValue(c, out var f) ? f
            : c is >= 'A' and <= 'Z' ? char.ConvertFromUtf32(first + c - 'A')
            : c is >= 'a' and <= 'z' ? char.ConvertFromUtf32(first + 26 + c - 'a')
            : c.ToString());
    return sb.ToString();
}
string Each(string text, int capital, int? small = null) => string.Concat(text.Select(c =>
    c is >= 'A' and <= 'Z' ? char.ConvertFromUtf32(capital + c - 'A')
    : c is >= 'a' and <= 'z' ? char.ConvertFromUtf32(small is { } s ? s + c - 'a' : capital + char.ToUpperInvariant(c) - 'A')
    : c.ToString()));

// --- each style back to its letters
Plain("mathematical bold", Styled("Marigold Vane", 0x1D400), "Marigold Vane");
Plain("mathematical italic", Styled("Marigold", 0x1D434, "hℎ"), "Marigold");
Plain("mathematical script, holes and all", Styled("Marigold Hale", 0x1D49C, "BℬEℰFℱHℋIℐLℒMℳRℛeℯgℊoℴ"), "Marigold Hale");
Plain("mathematical bold script, with a sparkle", Styled("Marigold", 0x1D4D0) + " ✨", "Marigold");
Plain("fraktur", Styled("Marigold", 0x1D504, "CℭHℌIℑRℜZℨ"), "Marigold");
Plain("double-struck", Styled("Marigold", 0x1D538, "CℂHℍNℕPℙQℚRℝZℤ"), "Marigold");
Plain("sans-serif bold italic", Styled("Marigold", 0x1D63C), "Marigold");
Plain("monospace", Styled("Marigold", 0x1D670), "Marigold");
Plain("fullwidth", Each("Marigold", 0xFF21, 0xFF41), "Marigold");
Plain("circled", Each("Marigold", 0x24B6, 0x24D0), "Marigold");
Plain("negative circled", Each("MARIGOLD", 0x1F150), "MARIGOLD");
Plain("squared", Each("MARIGOLD", 0x1F130), "MARIGOLD");
Plain("negative squared", Each("MARIGOLD", 0x1F170), "MARIGOLD");
Plain("parenthesized", Each("marigold", 0x249C, 0x249C), "marigold");
Plain("regional indicator letters", Each("MARIGOLD", 0x1F1E6), "MARIGOLD");
Plain("small caps", "ᴍᴀʀɪɢᴏʟᴅ", "marigold");
Plain("small caps with q", "ǫᴜɪɴɴ", "quinn");
Plain("superscript", "ᴹᵃʳᶦᵍᵒˡᵈ", "Marigold");
Plain("subscript", "ₘₐᵣᵢ", "mari");
Plain("upside down", "ploƃᴉɹɐW", "Marigold");
Plain("upside down, two words", "ǝuɐ˥ ʎɹɐɯǝsoᴚ", "Rosemary Lane");
Plain("strikethrough", "M̶a̶r̶i̶g̶o̶l̶d̶", "Marigold");
Plain("underlined and slashed", "M̲a̲r̲i̲ g̸o̸l̸d̸", "Mari gold");
Plain("zalgo", "M̷̡̛a̴̢͝r̸̨͘i̵͘ͅg̶o̶ld", "Marigold");
Plain("Greek and Cyrillic mixed into Latin words", "Rοѕємαrу Lαηє", "Rosemary Lane");
Plain("with a roman numeral for i", "Jαѕмⅰηє", "Jasmine");
Plain("faux Cyrillic", "ЯΘSΣ", "ROSE");
Plain("hooked letters", "ɱαɾιɠσʅԃ", "marigold");
Plain("Cherokee lookalikes", "ᎷᎪᎡᎥᏀᎾᏞᎠ", "MARIGOLD");
Plain("a currency sign for a letter", "Ro$ie", "Rosie");
Plain("all Cyrillic but one letter", "кєѕтяєℓ", "kestrel");
Plain("stand-ins in mixed case made one word", "JυηιρҽR", "Juniper");
Plain("Cherokee's fancy style", "ᏦᏋᏕᏖᏒᏋᏝ", "Kestrel");
Plain("Chinese characters and bopomofo for letters", "爪卂尺丨Ꮆㄖㄥᗪ", "Marigold");
Plain("emoji around", "🌸 Marigold 🌸", "Marigold");
Plain("an emoji sequence before", "👩‍👩‍👧 Juniper", "Juniper");
Plain("a flag after", "Juniper 🇮🇪", "Juniper");
Plain("flourishes", "꧁༺ Marigold ༻꧂", "Marigold");
Plain("stars and stripes", "★彡 Marigold 彡★", "Marigold");
Plain("a smiley in katakana", "Marigold ツ", "Marigold");
Plain("brackets", "【Marigold】", "Marigold");
Plain("underscores", "xX_Marigold_Xx", "xX Marigold Xx");
Plain("spaced out", "M a r i g o l d", "Marigold");
Plain("fullwidth spaced out", "Ｍ ａ ｒ ｉ ｇ ｏ ｌ ｄ", "Marigold");
Plain("a word's own punctuation kept", "~Mary-Jane O’Brien~", "Mary-Jane O'Brien");
Plain("an ampersand kept, said \"and\"", "🌸Ivy&Wren🌸", "Ivy & Wren");

// --- left as they are
foreach (var n in new[] { "Kestrel Tester", "Zoë", "José María", "Søren", "O'Brien", "Łukasz", "J.R. Hale", "İpek", "Ștefan", "Nguyễn Văn An",
             "Ivy & Wren", "Анна Петрова", "Ελένη", "さくら", "प्रिया", "ليلى", "می‌خواهم", "Marigold 2" })
    Plain("left alone", n, n);
Plain("half-width katakana made full", "ｻｸﾗ", "サクラ");

// --- nothing to say
foreach (var n in new[] { "★★★", "★彡★", "ツ", "✨🌸✨", ".", "꧁꧂", "🇮🇪", "   " })
    Plain("nothing left", n, "");

// --- each of People's names
string fancy = Styled("Marigold", 0x1D4D0) + " ✨";
const string User = "Juniper Tester";
Check("plain: the display name in plain letters", PlainName.Said(fancy, User, NameStyle.Plain), "Marigold");
Check("both: and the username", PlainName.Said(fancy, User, NameStyle.Both), "Marigold (Juniper Tester)");
Check("written: as written", PlainName.Said(fancy, User, NameStyle.Written), fancy);
Check("username alone", PlainName.Said(fancy, User, NameStyle.Username), User);
Check("plain: nothing sayable, the username", PlainName.Said("★★★", "starry", NameStyle.Plain), "starry");
Check("both: nothing sayable, the username once", PlainName.Said("★★★", "starry", NameStyle.Both), "starry");
Check("both: the same name once, as the display name has it", PlainName.Said("Starry", "starry", NameStyle.Both), "Starry");
Check("no display name known: the username", PlainName.Said(null, User, NameStyle.Plain), User);
Check("complete, plain", PlainName.Complete(fancy, User, false, NameStyle.Plain), "Marigold (Juniper Tester)");
Check("complete, written (the viewer's)", PlainName.Complete(fancy, User, false, NameStyle.Written), $"{fancy} (Juniper Tester)");
Check("complete, username", PlainName.Complete(fancy, User, false, NameStyle.Username), User);
Check("complete, a default display name", PlainName.Complete("Juniper Tester", User, true, NameStyle.Plain), User);
Check("complete, nothing sayable", PlainName.Complete("★★★", "starry", false, NameStyle.Plain), "starry");

Console.WriteLine($"\n{passed} of {passed + failed} passed");
return failed;
