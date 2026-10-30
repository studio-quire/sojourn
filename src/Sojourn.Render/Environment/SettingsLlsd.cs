using System.Globalization;
using System.Text;
using LibreMetaverse.StructuredData;
using UUID = LibreMetaverse.UUID;

namespace Sojourn.Render.Environment;

/// <summary>
/// Parses LLSD text in the formats settings assets use. Notation goes through our own parser:
/// LibreMetaverse 3.2.0's notation reader leaves the closing bracket of an empty array or map
/// unread, which breaks on every day cycle (empty tracks 2-4 are written as []).
/// </summary>
public static class SettingsLlsd
{
    private const string NotationHeader = "<? llsd/notation ?>";

    public static OSD Parse(string text)
    {
        string t = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (t.StartsWith(NotationHeader, StringComparison.OrdinalIgnoreCase))
            return new NotationReader(t, NotationHeader.Length).ReadDocument();
        if (t.StartsWith("<", StringComparison.Ordinal))
            return OSDParser.DeserializeLLSDXml(t);
        // Headerless text: settings assets are notation (JSON is never used for them).
        return new NotationReader(t, 0).ReadDocument();
    }

    public static OSD Parse(byte[] data)
    {
        string head = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 24));
        if (head.StartsWith("<? llsd/binary ?>", StringComparison.OrdinalIgnoreCase))
            return OSDParser.DeserializeLLSDBinary(data);
        return Parse(Encoding.UTF8.GetString(data));
    }

    private sealed class NotationReader(string s, int pos)
    {
        private int _p = pos;

        public OSD ReadDocument()
        {
            var v = ReadValue();
            SkipWs();
            return v;
        }

        private void SkipWs()
        {
            while (_p < s.Length && char.IsWhiteSpace(s[_p])) _p++;
        }

        private char Next()
        {
            if (_p >= s.Length) throw new FormatException("LLSD notation: unexpected end");
            return s[_p++];
        }

        private char Peek() => _p < s.Length ? s[_p] : '\0';

        private OSD ReadValue()
        {
            SkipWs();
            char c = Next();
            switch (c)
            {
                case '{': return ReadMap();
                case '[': return ReadArray();
                case '!': return new OSD();
                case '\'':
                case '"': return OSD.FromString(ReadQuoted(c));
                case 's': return OSD.FromString(ReadSized());
                case 'i': return OSD.FromInteger(int.Parse(ReadToken(), NumberStyles.Integer, CultureInfo.InvariantCulture));
                case 'r':
                {
                    string tok = ReadToken();
                    return OSD.FromReal(tok.Equals("nan", StringComparison.OrdinalIgnoreCase)
                        ? double.NaN
                        : double.Parse(tok, NumberStyles.Float, CultureInfo.InvariantCulture));
                }
                case 'u':
                {
                    string tok = s.Substring(_p, Math.Min(36, s.Length - _p));
                    _p += tok.Length;
                    return OSD.FromUUID(new UUID(tok));
                }
                case 'b':
                {
                    // b64"..." (b16 is rare; not used by settings assets)
                    string enc = s.Substring(_p, 2);
                    _p += 2;
                    char q = Next();
                    string body = ReadQuoted(q);
                    return enc == "64" ? OSD.FromBinary(Convert.FromBase64String(body))
                        : OSD.FromBinary(Convert.FromHexString(body));
                }
                case 'd': { char q = Next(); return OSD.FromDate(DateTime.Parse(ReadQuoted(q), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal)); }
                case 'l': { char q = Next(); return OSD.FromUri(new Uri(ReadQuoted(q), UriKind.RelativeOrAbsolute)); }
                case '1': return OSD.FromBoolean(true);
                case '0': return OSD.FromBoolean(false);
                case 't': case 'T': SkipWord(); return OSD.FromBoolean(true);
                case 'f': case 'F': SkipWord(); return OSD.FromBoolean(false);
                default: throw new FormatException($"LLSD notation: unexpected '{c}' at {_p - 1}");
            }
        }

        private void SkipWord()
        {
            while (_p < s.Length && char.IsLetter(s[_p])) _p++;
        }

        private string ReadToken()
        {
            int start = _p;
            while (_p < s.Length && (char.IsLetterOrDigit(s[_p]) || s[_p] is '.' or '-' or '+')) _p++;
            return s.Substring(start, _p - start);
        }

        private string ReadQuoted(char quote)
        {
            var sb = new StringBuilder();
            while (true)
            {
                char c = Next();
                if (c == quote) return sb.ToString();
                if (c == '\\')
                {
                    char e = Next();
                    sb.Append(e switch
                    {
                        'n' => '\n', 't' => '\t', 'r' => '\r', 'a' => '\a', 'b' => '\b', 'f' => '\f', 'v' => '\v',
                        'x' => (char)Convert.ToInt32(s.Substring((_p += 2) - 2, 2), 16),
                        _ => e,
                    });
                    continue;
                }
                sb.Append(c);
            }
        }

        private string ReadSized()
        {
            if (Next() != '(') throw new FormatException("LLSD notation: bad sized string");
            int close = s.IndexOf(')', _p);
            int len = int.Parse(s.AsSpan(_p, close - _p), CultureInfo.InvariantCulture);
            _p = close + 1;
            char q = Next();
            string body = s.Substring(_p, len);
            _p += len;
            if (Next() != q) throw new FormatException("LLSD notation: bad sized string terminator");
            return body;
        }

        private OSDMap ReadMap()
        {
            var map = new OSDMap();
            SkipWs();
            if (Peek() == '}') { _p++; return map; }
            while (true)
            {
                SkipWs();
                char q = Next();
                string key = q is '\'' or '"' ? ReadQuoted(q) : q == 's' ? ReadSized()
                    : throw new FormatException($"LLSD notation: bad map key at {_p - 1}");
                SkipWs();
                if (Next() != ':') throw new FormatException($"LLSD notation: expected ':' at {_p - 1}");
                map[key] = ReadValue();
                SkipWs();
                char d = Next();
                if (d == '}') return map;
                if (d != ',') throw new FormatException($"LLSD notation: expected ',' or '}}' at {_p - 1}");
            }
        }

        private OSDArray ReadArray()
        {
            var arr = new OSDArray();
            SkipWs();
            if (Peek() == ']') { _p++; return arr; }
            while (true)
            {
                arr.Add(ReadValue());
                SkipWs();
                char d = Next();
                if (d == ']') return arr;
                if (d != ',') throw new FormatException($"LLSD notation: expected ',' or ']' at {_p - 1}");
            }
        }
    }
}
