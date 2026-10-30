using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LibreMetaverse;

namespace Sojourn.Core;

/// <summary>JSON helpers for the LEAP-shaped maps the API speaks. Vectors are arrays, UUIDs strings.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static JsonArray Arr(Vector3 v) => new(R(v.X), R(v.Y), R(v.Z));
    public static JsonArray Arr(Vector3d v) => new(Math.Round(v.X, 4), Math.Round(v.Y, 4), Math.Round(v.Z, 4));
    public static JsonArray Arr(Quaternion q) => new(R(q.X, 6), R(q.Y, 6), R(q.Z, 6), R(q.W, 6));
    public static JsonArray Arr(Color4 c) => new(R(c.R), R(c.G), R(c.B), R(c.A));
    public static JsonArray Arr(params double[] xs)
    {
        var a = new JsonArray();
        foreach (var x in xs) a.Add(x);
        return a;
    }

    public static JsonArray Ids(IEnumerable<UUID> ids)
    {
        var a = new JsonArray();
        foreach (var id in ids) a.Add(id.ToString());
        return a;
    }

    public static JsonArray Strings(IEnumerable<string> xs)
    {
        var a = new JsonArray();
        foreach (var x in xs) a.Add(x);
        return a;
    }

    /// <summary>Rounded float as double, so replies don't carry float noise like 25.960001.</summary>
    public static double R(float f, int digits = 4) => float.IsFinite(f) ? Math.Round(f, digits) : 0;
    public static double R(double f, int digits = 4) => double.IsFinite(f) ? Math.Round(f, digits) : 0;

    public static JsonObject Error(string message) => new() { ["error"] = message };

    public static string Serialize(JsonNode? node) => node?.ToJsonString(Compact) ?? "null";

    // ---------- reading args ----------

    public static bool Has(this JsonObject o, string key) => o.TryGetPropertyValue(key, out var v) && v is not null;

    public static string? Str(this JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var v) || v is null) return null;
        return v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToJsonString();
    }

    public static string Str(this JsonObject o, string key, string dflt) => o.Str(key) ?? dflt;

    public static double? Num(this JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var v) || v is null) return null;
        return AsDouble(v);
    }

    public static double Num(this JsonObject o, string key, double dflt) => o.Num(key) ?? dflt;

    public static int Int(this JsonObject o, string key, int dflt) => o.Num(key) is { } d ? (int)Math.Round(d) : dflt;

    public static bool? Bool(this JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var v) || v is null) return null;
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<bool>(out var b)) return b;
            if (jv.TryGetValue<string>(out var s)) return s is "1" or "true" or "True" or "yes";
            var d = AsDouble(v);
            if (d is not null) return d != 0;
        }
        return null;
    }

    public static bool Bool(this JsonObject o, string key, bool dflt) => o.Bool(key) ?? dflt;

    public static double? AsDouble(JsonNode? v)
    {
        if (v is not JsonValue jv) return null;
        if (jv.TryGetValue<double>(out var d)) return d;
        if (jv.TryGetValue<float>(out var f)) return f;   // a value made in code from a float (not parsed) reads only as one
        if (jv.TryGetValue<long>(out var l)) return l;
        if (jv.TryGetValue<int>(out var i)) return i;
        if (jv.TryGetValue<bool>(out var b)) return b ? 1 : 0;
        if (jv.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }

    public static UUID? Uuid(this JsonObject o, string key)
    {
        var s = o.Str(key);
        return s is not null && UUID.TryParse(s, out var id) ? id : null;
    }

    public static Vector3? Vec3(this JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var v) || v is not JsonArray a || a.Count < 3) return null;
        return new Vector3((float)(AsDouble(a[0]) ?? 0), (float)(AsDouble(a[1]) ?? 0), (float)(AsDouble(a[2]) ?? 0));
    }

    public static double[]? Nums(this JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var v) || v is null) return null;
        if (v is JsonArray a) return a.Select(x => AsDouble(x) ?? 0).ToArray();
        return AsDouble(v) is { } d ? new[] { d } : null;
    }

    public static List<string> StrList(this JsonObject o, string key)
    {
        var list = new List<string>();
        if (!o.TryGetPropertyValue(key, out var v) || v is null) return list;
        if (v is JsonArray a)
        {
            foreach (var x in a)
                if (x is JsonValue xv && xv.TryGetValue<string>(out var s)) list.Add(s);
                else if (x is not null) list.Add(x.ToJsonString());
        }
        else if (v is JsonValue jv && jv.TryGetValue<string>(out var one)) list.Add(one);
        return list;
    }

    public static List<UUID> UuidList(this JsonObject o, string key)
    {
        var list = new List<UUID>();
        foreach (var s in o.StrList(key))
            if (UUID.TryParse(s, out var id)) list.Add(id);
        return list;
    }

    /// <summary>Converts arbitrary CLR values into JSON nodes (used for event payloads built from anonymous data).</summary>
    public static JsonNode? From(object? v) => v switch
    {
        null => null,
        JsonNode n => n,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        uint u => JsonValue.Create(u),
        long l => JsonValue.Create(l),
        ulong ul => JsonValue.Create(ul),
        float f => JsonValue.Create(R(f)),
        double d => JsonValue.Create(R(d, 6)),
        UUID id => JsonValue.Create(id.ToString()),
        Vector3 v3 => Arr(v3),
        Vector3d v3d => Arr(v3d),
        Quaternion q => Arr(q),
        Enum e => JsonValue.Create(e.ToString()),
        _ => JsonValue.Create(v.ToString()),
    };

    public static double UnixNow() => Math.Round(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, 3);
}
