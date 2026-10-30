// A small JSON value, parser and writer for the helpers' control lines (objects of strings, numbers, bools, arrays).
// Written by Claude Opus 5.5, 2026-10-26. Input comes from the client only, but it's parsed defensively anyway: depth
// and size are bounded and anything malformed yields null.
#pragma once
#include <cstdint>
#include <cstdio>
#include <map>
#include <memory>
#include <string>
#include <vector>

namespace sm {

struct Json {
    enum Kind { kNull, kBool, kNum, kStr, kArr, kObj } kind = kNull;
    bool b = false;
    double n = 0;
    std::string s;
    std::vector<Json> a;
    std::map<std::string, Json> o;

    static Json Of(const std::string& v) { Json j; j.kind = kStr; j.s = v; return j; }
    static Json Of(double v) { Json j; j.kind = kNum; j.n = v; return j; }
    static Json Of(bool v) { Json j; j.kind = kBool; j.b = v; return j; }
    static Json Object() { Json j; j.kind = kObj; return j; }
    static Json Array() { Json j; j.kind = kArr; return j; }

    const Json& operator[](const std::string& k) const {
        static const Json none;
        auto it = o.find(k);
        return it == o.end() ? none : it->second;
    }
    Json& Set(const std::string& k, Json v) { kind = kObj; o[k] = std::move(v); return *this; }
    Json& Set(const std::string& k, const std::string& v) { return Set(k, Of(v)); }
    Json& Set(const std::string& k, const char* v) { return Set(k, Of(std::string(v ? v : ""))); }
    Json& Set(const std::string& k, double v) { return Set(k, Of(v)); }
    Json& Set(const std::string& k, int v) { return Set(k, Of((double)v)); }
    Json& Set(const std::string& k, int64_t v) { return Set(k, Of((double)v)); }
    Json& Set(const std::string& k, bool v) { return Set(k, Of(v)); }

    std::string Str(const std::string& k, const std::string& def = "") const {
        const Json& v = (*this)[k];
        return v.kind == kStr ? v.s : def;
    }
    double Num(const std::string& k, double def = 0) const {
        const Json& v = (*this)[k];
        return v.kind == kNum ? v.n : def;
    }
    int Int(const std::string& k, int def = 0) const { return (int)Num(k, def); }
    bool Flag(const std::string& k, bool def = false) const {
        const Json& v = (*this)[k];
        return v.kind == kBool ? v.b : def;
    }
    bool Has(const std::string& k) const { return o.count(k) > 0; }

    std::string Dump() const {
        std::string out;
        Write(out);
        return out;
    }

    void Write(std::string& out) const {
        switch (kind) {
            case kNull: out += "null"; break;
            case kBool: out += b ? "true" : "false"; break;
            case kNum: {
                char buf[40];
                if (n == (double)(int64_t)n && n > -9e15 && n < 9e15) snprintf(buf, sizeof buf, "%lld", (long long)n);
                else snprintf(buf, sizeof buf, "%.9g", n);
                out += buf;
                break;
            }
            case kStr: Quote(s, out); break;
            case kArr: {
                out += '[';
                for (size_t i = 0; i < a.size(); i++) {
                    if (i) out += ',';
                    a[i].Write(out);
                }
                out += ']';
                break;
            }
            case kObj: {
                out += '{';
                bool first = true;
                for (auto& kv : o) {
                    if (!first) out += ',';
                    first = false;
                    Quote(kv.first, out);
                    out += ':';
                    kv.second.Write(out);
                }
                out += '}';
                break;
            }
        }
    }

    static void Quote(const std::string& v, std::string& out) {
        out += '"';
        for (unsigned char c : v) {
            switch (c) {
                case '"': out += "\\\""; break;
                case '\\': out += "\\\\"; break;
                case '\n': out += "\\n"; break;
                case '\r': out += "\\r"; break;
                case '\t': out += "\\t"; break;
                default:
                    if (c < 0x20) {
                        char buf[8];
                        snprintf(buf, sizeof buf, "\\u%04x", c);
                        out += buf;
                    } else out += (char)c;
            }
        }
        out += '"';
    }

    static Json Parse(const std::string& text) {
        size_t i = 0;
        Json v;
        if (text.size() > (8u << 20) || !ParseValue(text, i, v, 0)) return Json();
        Skip(text, i);
        return i == text.size() ? v : Json();
    }

private:
    static void Skip(const std::string& t, size_t& i) {
        while (i < t.size() && (t[i] == ' ' || t[i] == '\t' || t[i] == '\n' || t[i] == '\r')) i++;
    }
    static bool Hex4(const std::string& t, size_t& i, unsigned& cp) {
        if (i + 4 > t.size()) return false;
        cp = 0;
        for (int k = 0; k < 4; k++) {
            char c = t[i++];
            cp <<= 4;
            if (c >= '0' && c <= '9') cp |= c - '0';
            else if (c >= 'a' && c <= 'f') cp |= c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') cp |= c - 'A' + 10;
            else return false;
        }
        return true;
    }
    static void Utf8(unsigned cp, std::string& out) {
        if (cp < 0x80) out += (char)cp;
        else if (cp < 0x800) { out += (char)(0xC0 | (cp >> 6)); out += (char)(0x80 | (cp & 0x3F)); }
        else if (cp < 0x10000) { out += (char)(0xE0 | (cp >> 12)); out += (char)(0x80 | ((cp >> 6) & 0x3F)); out += (char)(0x80 | (cp & 0x3F)); }
        else { out += (char)(0xF0 | (cp >> 18)); out += (char)(0x80 | ((cp >> 12) & 0x3F)); out += (char)(0x80 | ((cp >> 6) & 0x3F)); out += (char)(0x80 | (cp & 0x3F)); }
    }
    static bool ParseString(const std::string& t, size_t& i, std::string& out) {
        if (i >= t.size() || t[i] != '"') return false;
        i++;
        while (i < t.size()) {
            char c = t[i++];
            if (c == '"') return true;
            if (c != '\\') { out += c; continue; }
            if (i >= t.size()) return false;
            char e = t[i++];
            switch (e) {
                case '"': out += '"'; break;
                case '\\': out += '\\'; break;
                case '/': out += '/'; break;
                case 'b': out += '\b'; break;
                case 'f': out += '\f'; break;
                case 'n': out += '\n'; break;
                case 'r': out += '\r'; break;
                case 't': out += '\t'; break;
                case 'u': {
                    unsigned cp;
                    if (!Hex4(t, i, cp)) return false;
                    if (cp >= 0xD800 && cp < 0xDC00 && i + 6 <= t.size() && t[i] == '\\' && t[i + 1] == 'u') {
                        i += 2;
                        unsigned lo;
                        if (!Hex4(t, i, lo)) return false;
                        cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                    }
                    Utf8(cp, out);
                    break;
                }
                default: return false;
            }
        }
        return false;
    }
    static bool ParseValue(const std::string& t, size_t& i, Json& v, int depth) {
        if (depth > 32) return false;
        Skip(t, i);
        if (i >= t.size()) return false;
        char c = t[i];
        if (c == '{') {
            v.kind = kObj;
            i++;
            Skip(t, i);
            if (i < t.size() && t[i] == '}') { i++; return true; }
            for (;;) {
                Skip(t, i);
                std::string k;
                if (!ParseString(t, i, k)) return false;
                Skip(t, i);
                if (i >= t.size() || t[i] != ':') return false;
                i++;
                Json item;
                if (!ParseValue(t, i, item, depth + 1)) return false;
                v.o[k] = std::move(item);
                Skip(t, i);
                if (i < t.size() && t[i] == ',') { i++; continue; }
                if (i < t.size() && t[i] == '}') { i++; return true; }
                return false;
            }
        }
        if (c == '[') {
            v.kind = kArr;
            i++;
            Skip(t, i);
            if (i < t.size() && t[i] == ']') { i++; return true; }
            for (;;) {
                Json item;
                if (!ParseValue(t, i, item, depth + 1)) return false;
                v.a.push_back(std::move(item));
                Skip(t, i);
                if (i < t.size() && t[i] == ',') { i++; continue; }
                if (i < t.size() && t[i] == ']') { i++; return true; }
                return false;
            }
        }
        if (c == '"') { v.kind = kStr; return ParseString(t, i, v.s); }
        if (t.compare(i, 4, "true") == 0) { v.kind = kBool; v.b = true; i += 4; return true; }
        if (t.compare(i, 5, "false") == 0) { v.kind = kBool; v.b = false; i += 5; return true; }
        if (t.compare(i, 4, "null") == 0) { v.kind = kNull; i += 4; return true; }
        size_t start = i;
        while (i < t.size() && (isdigit((unsigned char)t[i]) || t[i] == '-' || t[i] == '+' || t[i] == '.' || t[i] == 'e' || t[i] == 'E')) i++;
        if (i == start) return false;
        v.kind = kNum;
        v.n = strtod(t.substr(start, i - start).c_str(), nullptr);
        return true;
    }
};

}  // namespace sm
