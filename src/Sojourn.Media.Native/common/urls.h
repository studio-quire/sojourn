// URL rules for media, the helper's half (the client has the same in Sojourn.Client/Media/MediaUrls.cs; keep them in
// step). Written by Claude Opus 5.5, 2026-10-26.
//
// - Schemes: pages and frames load only from the web (http, https), about:blank, and data: (in-world signs are often
//   data:text/html URLs); frames and resources may also use blob:, ws:, wss: and about:srcdoc. secondlife:// links are
//   reported to the client, never followed here. Everything else (file:, chrome:, devtools:, view-source:,
//   javascript: as a navigation, custom protocols) is refused.
// - Private addresses: loopback, private, link-local and unique-local hosts, `localhost` and `*.local` are refused
//   unless the client says the grid itself is local. URLs arrive canonical (Chromium has already turned
//   http://2130706433/ into http://127.0.0.1/). A public name that resolves to a private address isn't caught here;
//   Chromium's own local-network-access checks cover pages' requests to those.
// - Whitelists: the viewer's matching (LLMediaEntry::checkUrlAgainstWhitelist): scheme, authority (host, and the port
//   when the URL gives one) and escaped path are each matched in full, case-insensitively, against the entry's, with
//   * as a wildcard; an empty part of the entry matches anything; an entry without a scheme is read as https:// plus
//   the entry but matches any scheme.
#pragma once
#include <algorithm>
#include <cctype>
#include <cstdint>
#include <cstdlib>
#include <string>
#include <vector>

namespace sm {

struct Url {
    std::string scheme, host, port, path;   // lowercased scheme and host; path as given (escaped), "/" if empty
    bool ok = false;
};

inline std::string Lower(std::string s) {
    for (char& c : s) c = (char)std::tolower((unsigned char)c);
    return s;
}

inline Url ParseUrl(const std::string& text) {
    Url u;
    size_t colon = text.find(':');
    if (colon == std::string::npos || colon == 0) return u;
    for (size_t i = 0; i < colon; i++) {
        char c = text[i];
        if (!(std::isalnum((unsigned char)c) || c == '+' || c == '-' || c == '.')) return u;
    }
    u.scheme = Lower(text.substr(0, colon));
    std::string rest = text.substr(colon + 1);
    if (rest.compare(0, 2, "//") != 0) {
        u.path = rest;   // data:, about:blank, javascript:
        u.ok = true;
        return u;
    }
    rest = rest.substr(2);
    size_t end = rest.find_first_of("/?#");
    std::string auth = rest.substr(0, end);
    u.path = end == std::string::npos ? "/" : rest.substr(end);
    size_t q = u.path.find_first_of("?#");
    if (q != std::string::npos) u.path = u.path.substr(0, q);
    if (u.path.empty()) u.path = "/";
    size_t at = auth.rfind('@');
    if (at != std::string::npos) auth = auth.substr(at + 1);
    if (!auth.empty() && auth[0] == '[') {
        size_t close = auth.find(']');
        if (close == std::string::npos) return u;
        u.host = Lower(auth.substr(1, close - 1));
        if (close + 1 < auth.size() && auth[close + 1] == ':') u.port = auth.substr(close + 2);
    } else {
        size_t pc = auth.rfind(':');
        if (pc != std::string::npos) {
            u.host = Lower(auth.substr(0, pc));
            u.port = auth.substr(pc + 1);
        } else u.host = Lower(auth);
    }
    u.ok = true;
    return u;
}

inline bool ParseIpv4(const std::string& h, uint8_t out[4]) {
    int part = 0;
    size_t i = 0;
    while (part < 4) {
        if (i >= h.size() || !std::isdigit((unsigned char)h[i])) return false;
        int v = 0, digits = 0;
        while (i < h.size() && std::isdigit((unsigned char)h[i])) {
            v = v * 10 + (h[i] - '0');
            if (++digits > 3 || v > 255) return false;
            i++;
        }
        out[part++] = (uint8_t)v;
        if (part < 4) {
            if (i >= h.size() || h[i] != '.') return false;
            i++;
        }
    }
    return i == h.size();
}

inline bool PrivateIpv4(const uint8_t a[4]) {
    return a[0] == 127 || a[0] == 10 || a[0] == 0 || (a[0] == 172 && (a[1] & 0xF0) == 16) || (a[0] == 192 && a[1] == 168) ||
           (a[0] == 169 && a[1] == 254) || (a[0] == 100 && (a[1] & 0xC0) == 64);   // CGNAT, as Tailscale uses
}

inline bool PrivateHost(const std::string& host) {
    if (host.empty()) return false;
    if (host == "localhost" || (host.size() > 10 && host.compare(host.size() - 10, 10, ".localhost") == 0)) return true;
    if (host.size() > 6 && host.compare(host.size() - 6, 6, ".local") == 0) return true;
    uint8_t v4[4];
    if (ParseIpv4(host, v4)) return PrivateIpv4(v4);
    if (host.find(':') != std::string::npos) {
        // IPv6: loopback, unspecified, unique-local fc00::/7, link-local fe80::/10, and IPv4-mapped private ones
        std::string h = host;
        if (h == "::1" || h == "::") return true;
        if (h.size() >= 2 && (h.compare(0, 2, "fc") == 0 || h.compare(0, 2, "fd") == 0)) return true;
        if (h.size() >= 3 && h.compare(0, 2, "fe") == 0 && (h[2] == '8' || h[2] == '9' || h[2] == 'a' || h[2] == 'b')) return true;
        size_t last = h.rfind(':');
        if (h.compare(0, 7, "::ffff:") == 0 && ParseIpv4(h.substr(last + 1), v4)) return PrivateIpv4(v4);
    }
    return false;
}

// Why a navigation (main frame or a frame) may not go to url; "" if it may, "slurl" for secondlife:// links.
inline std::string NavigationRefusal(const std::string& url, bool main, bool private_ok) {
    Url u = ParseUrl(url);
    if (!u.ok) return "unreadable";
    if (u.scheme == "secondlife" || u.scheme == "x-grid-location-info") return "slurl";
    if (u.scheme == "about") return (u.path == "blank" || (!main && u.path == "srcdoc")) ? "" : "scheme";
    if (u.scheme == "data") return "";
    if (!main && u.scheme == "blob") return "";
    if (u.scheme != "http" && u.scheme != "https") return "scheme";
    if (!private_ok && PrivateHost(u.host)) return "private address";
    return "";
}

// Why a page's request (any resource) may not go to url; "" if it may.
inline std::string ResourceRefusal(const std::string& url, bool private_ok) {
    Url u = ParseUrl(url);
    if (!u.ok) return "unreadable";
    if (u.scheme == "data" || u.scheme == "blob" || u.scheme == "about") return "";
    if (u.scheme != "http" && u.scheme != "https" && u.scheme != "ws" && u.scheme != "wss") return "scheme";
    if (!private_ok && PrivateHost(u.host)) return "private address";
    return "";
}

// Full, case-insensitive match of text against a pattern where * matches anything (the viewer's regex: everything
// else literal).
inline bool WildMatch(const std::string& pat, const std::string& text) {
    std::string p = Lower(pat), t = Lower(text);
    size_t pi = 0, ti = 0, star = std::string::npos, mark = 0;
    while (ti < t.size()) {
        if (pi < p.size() && p[pi] == '*') { star = pi++; mark = ti; }
        else if (pi < p.size() && p[pi] == t[ti]) { pi++; ti++; }
        else if (star != std::string::npos) { pi = star + 1; ti = ++mark; }
        else return false;
    }
    while (pi < p.size() && p[pi] == '*') pi++;
    return pi == p.size();
}

inline bool WhitelistEntryAllows(const std::string& entry, const Url& c) {
    std::string e = entry;
    while (!e.empty() && std::isspace((unsigned char)e.back())) e.pop_back();
    while (!e.empty() && std::isspace((unsigned char)e.front())) e.erase(e.begin());
    if (e.empty()) return false;
    bool has_scheme = e.find("://") != std::string::npos;
    Url p = ParseUrl(has_scheme ? e : "https://" + e);
    if (!p.ok) return false;
    std::string pscheme = has_scheme ? p.scheme : "";
    std::string pauth = p.host + (p.port.empty() ? "" : ":" + p.port);
    // the entry's path as written ("" when it gave none, which matches anything)
    std::string ppath;
    if (has_scheme) {
        size_t s = e.find("://") + 3;
        size_t slash = e.find('/', s);
        ppath = slash == std::string::npos ? "" : e.substr(slash);
    } else {
        size_t slash = e.find('/');
        ppath = slash == std::string::npos ? "" : e.substr(slash);
    }
    std::string cauth = c.host + (c.port.empty() ? "" : ":" + c.port);
    if (!pscheme.empty() && !WildMatch(pscheme, c.scheme)) return false;
    if (!pauth.empty() && !WildMatch(pauth, cauth)) return false;
    if (!ppath.empty() && !WildMatch(ppath, c.path)) return false;
    return true;
}

inline bool WhitelistAllows(const std::vector<std::string>& list, const std::string& url) {
    if (list.empty()) return true;
    Url c = ParseUrl(url);
    if (!c.ok) return false;
    if (c.scheme == "about" && c.path == "blank") return true;
    for (auto& e : list)
        if (WhitelistEntryAllows(e, c)) return true;
    return false;
}

}  // namespace sm
