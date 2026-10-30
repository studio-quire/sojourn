using System.Numerics;
using LibreMetaverse.StructuredData;
using UUID = LibreMetaverse.UUID;

namespace Sojourn.Render.Environment;

/// <summary>
/// Reads one settings map the way LLSettingsBase::settingValidation + loadValuesFromLLSD do:
/// missing optional keys fall back to the validator default (or the LLSD "undefined" value: 0, zero
/// vector, null UUID), missing required keys and wrong types make the settings invalid, numeric
/// ranges and vector components are clamped, quaternions normalised.
/// One deliberate leniency: Integer is accepted where the viewer demands Real (and vice versa).
/// </summary>
internal sealed class SettingsReader
{
    private readonly OSDMap _map;
    private readonly ICollection<string>? _problems;
    private readonly string _what;

    public bool Valid { get; private set; } = true;

    public SettingsReader(OSDMap map, string what, ICollection<string>? problems)
    {
        _map = map;
        _what = what;
        _problems = problems;
    }

    public bool Has(string key) => Present(_map, key);

    public OSDMap Map => _map;

    internal static bool Present(OSDMap map, string key) =>
        map.TryGetValue(key, out var v) && v is not null && v.Type != OSDType.Unknown;

    private void Fail(string message)
    {
        Valid = false;
        _problems?.Add($"{_what}: {message}");
    }

    private bool CheckPresent(string key, bool required, OSDType wanted, out OSD value)
    {
        value = _map[key];
        if (!Present(_map, key))
        {
            if (required)
                Fail($"missing required '{key}'");
            return false;
        }
        bool ok = value.Type == wanted
                  || (wanted == OSDType.Real && value.Type == OSDType.Integer)
                  || (wanted == OSDType.Integer && value.Type == OSDType.Real);
        if (!ok)
        {
            Fail($"'{key}' has type {value.Type}, expected {wanted}");
            return false;
        }
        return true;
    }

    /// <summary>Real with verifyFloatRange clamping. Missing: default if given, else 0.</summary>
    public float Real(string key, bool required, float min, float max, float? dflt = null)
    {
        if (!CheckPresent(key, required && dflt is null, OSDType.Real, out var v))
            return dflt ?? 0f;
        return Math.Clamp((float)v.AsReal(), min, max);
    }

    /// <summary>Real with no range check (validator without verifier, or keys the viewer reads unvalidated).</summary>
    public float RealUnchecked(string key, bool required, float dflt = 0f)
    {
        if (!CheckPresent(key, required, OSDType.Real, out var v))
            return dflt;
        return (float)v.AsReal();
    }

    /// <summary>
    /// Array with verifyVectorMinMax clamping. A null bound is "*" (unbounded). Components past the
    /// end of the bound arrays clamp to 0, as the viewer's undefined-LLSD comparison does.
    /// Returns null when missing or invalid.
    /// </summary>
    public float[]? Vector(string key, bool required, float?[] mins, float?[] maxs)
    {
        if (!CheckPresent(key, required, OSDType.Array, out var v))
            return null;
        var arr = (OSDArray)v;
        var result = new float[arr.Count];
        for (int i = 0; i < arr.Count; i++)
        {
            float c = (float)arr[i].AsReal();
            float? lo = i < mins.Length ? mins[i] : 0f;
            float? hi = i < maxs.Length ? maxs[i] : 0f;
            if (lo is float l && l > c) c = l;
            if (hi is float h && h < c) c = h;
            result[i] = c;
        }
        return result;
    }

    /// <summary>verifyQuaternionNormal: must have 4 components; renormalised (identity if degenerate).</summary>
    public Quaternion? Quat(string key, bool required)
    {
        if (!CheckPresent(key, required, OSDType.Array, out var v))
            return null;
        var arr = (OSDArray)v;
        if (arr.Count != 4)
        {
            Fail($"'{key}' is not a quaternion");
            return null;
        }
        var q = new Quaternion((float)arr[0].AsReal(), (float)arr[1].AsReal(), (float)arr[2].AsReal(), (float)arr[3].AsReal());
        float mag = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        if (MathF.Abs(mag - 1f) <= 1e-5f)
            return q;
        return mag > EnvMath.FpMagThreshold ? Quaternion.Multiply(q, 1f / mag) : Quaternion.Identity;
    }

    public UUID Uuid(string key, bool required)
    {
        if (!Present(_map, key))
        {
            if (required) Fail($"missing required '{key}'");
            return UUID.Zero;
        }
        var v = _map[key];
        if (v.Type != OSDType.UUID)
        {
            // String UUIDs show up from some serialisers; the viewer's LLSD would reject them.
            if (v.Type == OSDType.String && UUID.TryParse(v.AsString(), out var parsed))
                return parsed;
            Fail($"'{key}' has type {v.Type}, expected UUID");
            return UUID.Zero;
        }
        return v.AsUUID();
    }

    /// <summary>A present map that must be validated separately; wrong type fails.</summary>
    public OSDMap? SubMap(string key, bool required)
    {
        if (!CheckPresent(key, required, OSDType.Map, out var v))
            return null;
        return (OSDMap)v;
    }

    public OSDArray? Array(string key, bool required)
    {
        if (!CheckPresent(key, required, OSDType.Array, out var v))
            return null;
        return (OSDArray)v;
    }

    public void Invalidate(string message) => Fail(message);

    // LLColor3(LLSD) / LLVector3(LLSD) / LLVector2(LLSD): missing components read as 0.
    public static Vector3 ToVec3(float[]? a) =>
        a is null ? Vector3.Zero : new Vector3(At(a, 0), At(a, 1), At(a, 2));

    public static Vector2 ToVec2(float[]? a) => a is null ? Vector2.Zero : new Vector2(At(a, 0), At(a, 1));

    private static float At(float[] a, int i) => i < a.Length ? a[i] : 0f;

    // Raw readers for legacy WindLight maps (no validation).
    internal static float LegacyReal(OSD v, int index = -1)
    {
        if (index >= 0)
            return v is OSDArray arr && index < arr.Count ? (float)arr[index].AsReal() : 0f;
        return (float)v.AsReal();
    }

    internal static Vector3 LegacyVec3(OSD v) =>
        v is OSDArray arr ? new Vector3(LegacyReal(arr, 0), LegacyReal(arr, 1), LegacyReal(arr, 2)) : Vector3.Zero;

    internal static Vector2 LegacyVec2(OSD v) =>
        v is OSDArray arr ? new Vector2(LegacyReal(arr, 0), LegacyReal(arr, 1)) : Vector2.Zero;

    internal static OSDArray Arr(params float[] values)
    {
        var a = new OSDArray(values.Length);
        foreach (var f in values) a.Add(OSD.FromReal(f));
        return a;
    }

    internal static OSDArray Arr(Vector3 v) => Arr(v.X, v.Y, v.Z);

    internal static OSDArray Arr(Vector2 v) => Arr(v.X, v.Y);

    internal static OSDArray Arr(Quaternion q) => Arr(q.X, q.Y, q.Z, q.W);
}

