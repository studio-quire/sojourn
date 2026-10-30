namespace Sojourn.Session.Media;

/// <summary>
/// URL rules for media, the client's half (src/Sojourn.Media.Native/common/urls.h is the helpers' half: keep the two in
/// step). The client checks before it asks a helper for anything; the helpers check again whatever they're asked.
/// Written by Claude Opus 5.5, 2026-10-26.
/// </summary>
public static class MediaUrls
{
    public readonly record struct Url(string Scheme, string Host, string Port, string Path, bool Ok);

    public static Url Parse(string text)
    {
        int colon = text.IndexOf(':');
        if (colon <= 0) return default;
        for (int i = 0; i < colon; i++)
        {
            char c = text[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')) return default;
        }
        string scheme = text[..colon].ToLowerInvariant(), rest = text[(colon + 1)..];
        if (!rest.StartsWith("//")) return new Url(scheme, "", "", rest, true);
        rest = rest[2..];
        int end = rest.IndexOfAny(['/', '?', '#']);
        string auth = end < 0 ? rest : rest[..end];
        string path = end < 0 ? "/" : rest[end..];
        int q = path.IndexOfAny(['?', '#']);
        if (q >= 0) path = path[..q];
        if (path.Length == 0) path = "/";
        int at = auth.LastIndexOf('@');
        if (at >= 0) auth = auth[(at + 1)..];
        string host, port = "";
        if (auth.StartsWith('['))
        {
            int close = auth.IndexOf(']');
            if (close < 0) return default;
            host = auth[1..close].ToLowerInvariant();
            if (close + 1 < auth.Length && auth[close + 1] == ':') port = auth[(close + 2)..];
        }
        else
        {
            int pc = auth.LastIndexOf(':');
            if (pc >= 0) { host = auth[..pc].ToLowerInvariant(); port = auth[(pc + 1)..]; }
            else host = auth.ToLowerInvariant();
        }
        return new Url(scheme, host, port, path, true);
    }

    static bool Ipv4(string h, Span<int> o)
    {
        var parts = h.Split('.');
        if (parts.Length != 4) return false;
        for (int i = 0; i < 4; i++)
        {
            if (parts[i].Length is 0 or > 3 || !parts[i].All(char.IsAsciiDigit)) return false;
            o[i] = int.Parse(parts[i]);
            if (o[i] > 255) return false;
        }
        return true;
    }

    static bool PrivateV4(ReadOnlySpan<int> a) =>
        a[0] is 127 or 10 or 0 || (a[0] == 172 && (a[1] & 0xF0) == 16) || (a[0] == 192 && a[1] == 168) ||
        (a[0] == 169 && a[1] == 254) || (a[0] == 100 && (a[1] & 0xC0) == 64);

    public static bool PrivateHost(string host)
    {
        if (host.Length == 0) return false;
        if (host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local")) return true;
        Span<int> v4 = stackalloc int[4];
        if (Ipv4(host, v4)) return PrivateV4(v4);
        if (host.Contains(':'))
        {
            if (host is "::1" or "::") return true;
            if (host.StartsWith("fc") || host.StartsWith("fd")) return true;
            if (host.Length >= 3 && host.StartsWith("fe") && host[2] is '8' or '9' or 'a' or 'b') return true;
            if (host.StartsWith("::ffff:") && Ipv4(host[(host.LastIndexOf(':') + 1)..], v4)) return PrivateV4(v4);
        }
        return false;
    }

    /// <summary>Why a page may not go to url (null if it may); "slurl" for secondlife:// links.</summary>
    public static string? WebRefusal(string url, bool privateOk)
    {
        var u = Parse(url);
        if (!u.Ok) return "unreadable";
        if (u.Scheme is "secondlife" or "x-grid-location-info") return "slurl";
        if (u.Scheme == "about") return u.Path == "blank" ? null : "scheme";
        if (u.Scheme == "data") return null;
        if (u.Scheme is not ("http" or "https")) return "scheme";
        if (!privateOk && PrivateHost(u.Host)) return "private address";
        return null;
    }

    /// <summary>Why the video helper may not open url (null if it may): network streams only, never local files.</summary>
    public static string? VideoRefusal(string url, bool privateOk)
    {
        var u = Parse(url);
        if (!u.Ok) return "unreadable";
        if (u.Scheme is not ("http" or "https" or "rtsp" or "rtmp" or "rtmps" or "mms" or "mmsh" or "rtp" or "udp")) return "scheme";
        if (!privateOk && PrivateHost(u.Host)) return "private address";
        return null;
    }

    static bool Wild(string pat, string text)
    {
        string p = pat.ToLowerInvariant(), t = text.ToLowerInvariant();
        int pi = 0, ti = 0, star = -1, mark = 0;
        while (ti < t.Length)
        {
            if (pi < p.Length && p[pi] == '*') { star = pi++; mark = ti; }
            else if (pi < p.Length && p[pi] == t[ti]) { pi++; ti++; }
            else if (star >= 0) { pi = star + 1; ti = ++mark; }
            else return false;
        }
        while (pi < p.Length && p[pi] == '*') pi++;
        return pi == p.Length;
    }

    /// <summary>The viewer's whitelist match (LLMediaEntry::checkUrlAgainstWhitelist): scheme, authority (host and the
    /// URL's port) and escaped path each matched in full, case-insensitively, * a wildcard, an empty part of the entry
    /// matching anything; an entry without a scheme is read as https:// plus it but matches any scheme.</summary>
    public static bool WhitelistAllows(IReadOnlyList<string>? list, string url)
    {
        if (list == null || list.Count == 0) return true;
        var c = Parse(url);
        if (!c.Ok) return false;
        if (c.Scheme == "about" && c.Path == "blank") return true;
        string cauth = c.Host + (c.Port.Length > 0 ? ":" + c.Port : "");
        foreach (var raw in list)
        {
            var e = raw.Trim();
            if (e.Length == 0) continue;
            bool hasScheme = e.Contains("://");
            var p = Parse(hasScheme ? e : "https://" + e);
            if (!p.Ok) continue;
            string pscheme = hasScheme ? p.Scheme : "", pauth = p.Host + (p.Port.Length > 0 ? ":" + p.Port : "");
            int from = hasScheme ? e.IndexOf("://") + 3 : 0;
            int slash = e.IndexOf('/', from);
            string ppath = slash < 0 ? "" : e[slash..];
            if (pscheme.Length > 0 && !Wild(pscheme, c.Scheme)) continue;
            if (pauth.Length > 0 && !Wild(pauth, cauth)) continue;
            if (ppath.Length > 0 && !Wild(ppath, c.Path)) continue;
            return true;
        }
        return false;
    }

    /// <summary>Which helper plays a URL of this MIME type (the viewer's mime_types.xml, by family rather than exact
    /// names: video/webm and HLS go to libVLC too, which plays them, where the viewer's exact lookup sent them to CEF).</summary>
    public static MediaKind KindOf(string url, string? mime)
    {
        var u = Parse(url);
        if (u.Ok && u.Scheme is "rtsp" or "rtmp" or "rtmps" or "mms" or "mmsh" or "rtp" or "udp") return MediaKind.Video;
        if (u.Ok && u.Scheme is "data" or "about") return MediaKind.Web;
        mime = (mime ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (mime.Length == 0 || mime == "text/html")
        {
            // no answer from the server: go by the file's extension, else a page
            var ext = System.IO.Path.GetExtension(u.Path).ToLowerInvariant();
            return ext is ".mp4" or ".m4v" or ".mov" or ".webm" or ".mkv" or ".avi" or ".flv" or ".wmv" or ".mpg" or ".mpeg" or ".ts"
                or ".m3u8" or ".mp3" or ".aac" or ".ogg" or ".oga" or ".opus" or ".wav" or ".flac" or ".m4a" && mime.Length == 0
                ? MediaKind.Video : MediaKind.Web;
        }
        if (mime.StartsWith("video/") || mime.StartsWith("audio/") || mime is "application/x-mpegurl" or "application/vnd.apple.mpegurl"
            or "application/ogg" or "application/octet-stream" or "application/dash+xml")
            return MediaKind.Video;
        return MediaKind.Web;
    }
}

public enum MediaKind { Unknown, Web, Video }
