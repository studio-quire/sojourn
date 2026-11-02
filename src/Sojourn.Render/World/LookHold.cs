using LibreMetaverse;

namespace Sojourn.Render.World;

/// <summary>
/// Testing: a look held back, as on a slow PC or a busy place where it hasn't arrived. SOJOURN_TEST_HOLD_LOOK=NAME[:SECONDS]
/// holds the bakes of avatars whose name contains NAME and every texture what they wear asks for;
/// SOJOURN_TEST_HOLD_WORN=NAME[:SECONDS] only what they wear (their bodies' bakes come). Each texture isn't fetched or
/// loaded for SECONDS from when it's first asked for (no SECONDS: for good). For Sojourn Access's describing, which must
/// wait for a look and say what hadn't loaded rather than name the colours of grey surfaces (docs/access-app.md >
/// Describing; tools/access.py's describing steps). Claude Opus 5.5, 2026-11-01.
/// </summary>
static class LookHold
{
    static readonly (string Name, long Ms)? Look = Parse("SOJOURN_TEST_HOLD_LOOK"), Worn = Parse("SOJOURN_TEST_HOLD_WORN");

    static (string, long)? Parse(string variable)
    {
        var v = System.Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(v)) return null;
        int colon = v.LastIndexOf(':');
        if (colon > 0 && double.TryParse(v[(colon + 1)..], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double s) && s > 0)
            return (v[..colon].Trim(), (long)(s * 1000));
        return (v.Trim(), long.MaxValue);
    }

    public static bool Active => Look != null || Worn != null;

    static bool Is((string Name, long Ms)? hold, Avatar? av) =>
        hold is { } h && av?.Name is { } n && n.Contains(h.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>The hold for an avatar's bakes (0: none).</summary>
    public static long Bakes(Avatar? av) => Is(Look, av) ? Look!.Value.Ms : 0;

    /// <summary>The hold for what an avatar wears (0: none).</summary>
    public static long WornBy(Avatar? av) => Is(Look, av) ? Look!.Value.Ms : Is(Worn, av) ? Worn!.Value.Ms : 0;

    public static void Hold(TextureStore textures, UUID id, long ms)
    {
        if (id != UUID.Zero && ms > 0) textures.Hold(id, ms);
    }
}
