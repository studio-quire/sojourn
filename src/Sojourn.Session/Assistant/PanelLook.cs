using System.Globalization;
using System.Text.Json.Nodes;
using SkiaSharp;

namespace Sojourn.Session.Assistant;

/// <summary>
/// How the panel and the settings menu look, as data: a preset by name, with any of its values changed. The person
/// picks presets in the settings menu; the assistant can change any value when they ask ("make it pinker", "less see-
/// through"), through SojournAssistant.look. Kept in the settings (VrSettings.PanelLook).
///
/// A material (glass, pearl or metal) decides how the light sits on the body: glass is a tint at some opacity with a
/// specular rim, frosted (the world behind blurred by the renderer's overlay; the headset's layer composites over it);
/// pearl is a milky body whose sheen colours lie under the surface in soft diagonal bands (orient) with a broad satin
/// highlight; metal is polished, its sheen colours laid as horizontal reflection bands, with bevelled keys. The accent
/// is the look's one colour of its own, light held in it: the confirm key, an "on" switch's bead, the listening orb.
///
/// The glow is light of its own the body gives as it turns clear, so dark ink still has light behind it over a dark world
/// (GlassLight).
///
/// Contrast: every preset reads well by the legibility measure (Readability) with a margin, not by as much as it can:
/// text that's easy to read without the panel shouting over the world. A look the person asks for is never refused for
/// being hard to read (user, 2026-10-28: if it is, they ask for it changed, or pick a preset); the measure comes back
/// with the change as advice.
/// </summary>
public sealed record PanelLook
{
    // the default, glass: clear white glass, lightly frosted, white light at its corners and a bright rim, a glow of
    // its own as it's clear, deep teal ink (the user's tuning in the headset, 2026-10-28: like Liquid Glass, not tinted)
    public string Preset { get; init; } = "glass";
    public string Material { get; init; } = "glass";
    public string Tint { get; init; } = "#f5f8fa";
    public float TintOpacity { get; init; } = 0.22f;
    /// <summary>The least light the body gives over a dark world, as a share of its tint when solid: below this
    /// tint_opacity it gives off light of its own to make up the difference (GlassLight), at or above it none.</summary>
    public float Glow { get; init; } = 0.45f;
    /// <summary>Up to three colours of light on the body: glints in glass, orient in pearl, reflection bands in metal.</summary>
    public string[] Sheen { get; init; } = { "#ffffff", "#ffffff" };
    public float SheenOpacity { get; init; } = 0.22f;
    /// <summary>How much the world behind is blurred, 0 (none: plain see-through) to 1.</summary>
    public float Frost { get; init; } = 0.3f;
    /// <summary>The light caught along the edge, 0 to 1.</summary>
    public float Rim { get; init; } = 1;
    public string Ink { get; init; } = "#2f474c";
    public string Dim { get; init; } = "#465e63";
    public string Faint { get; init; } = "#7a8f93";
    /// <summary>The light in the glass: the confirm key, an "on" switch, the listening orb.</summary>
    public string Accent { get; init; } = "#1f736d";
    public string OnAccent { get; init; } = "#ffffff";
    /// <summary>Keys are a lighter (or darker) layer of this colour.</summary>
    public string Key { get; init; } = "#ffffff";
    public float KeyOpacity { get; init; } = 0.5f;
    public float Radius { get; init; } = 26;
    /// <summary>segoe (the system's, as Scry), archivo, bahnschrift, georgia or cascadia.</summary>
    public string Font { get; init; } = "segoe";

    /// <summary>The names a change can use (the agent API's look op).</summary>
    public static readonly string[] Keys =
    {
        "preset", "material", "tint", "tint_opacity", "glow", "sheen", "sheen_opacity", "frost", "rim", "ink", "dim", "faint", "accent",
        "on_accent", "key", "key_opacity", "radius", "font",
    };

    public static readonly string[] Fonts = { "segoe", "archivo", "bahnschrift", "georgia", "cascadia" };
    public static readonly string[] Materials = { "glass", "pearl", "metal" };

    /// <summary>The presets, in the settings menu's order. Glass is the default.</summary>
    public static readonly IReadOnlyList<PanelLook> Presets = new[]
    {
        new PanelLook(),
        // nacre: a milky body with pink, green and blue lying under its surface, plum-grey ink, a deep rose accent
        new PanelLook
        {
            Preset = "pearl", Material = "pearl", Tint = "#f3efec", TintOpacity = 0.8f, Sheen = new[] { "#f7cfe0", "#cdeede", "#cfdcfb" },
            SheenOpacity = 0.46f, Frost = 1, Rim = 0.9f, Ink = "#36323f", Dim = "#5f5a69", Faint = "#7f7a88",
            Accent = "#8c4b6c", OnAccent = "#ffffff", Key = "#ffffff", KeyOpacity = 0.55f,
        },
        // polished silver: opaque, studio-light bands, bevelled keys, a deep blue accent
        new PanelLook
        {
            Preset = "silver", Material = "metal", Tint = "#c6cbd3", TintOpacity = 0.97f, Sheen = new[] { "#ffffff", "#7d8592" },
            SheenOpacity = 0.75f, Frost = 0, Rim = 1, Ink = "#262b34", Dim = "#4a505b", Faint = "#676e79",
            Accent = "#2f68b4", OnAccent = "#ffffff", Key = "#ffffff", KeyOpacity = 0.4f,
        },
        // dark smoked glass, for dark places or for anyone who'd rather: soft light ink, a pale sea-glass accent
        new PanelLook
        {
            Preset = "smoke", Tint = "#141922", TintOpacity = 0.74f, Glow = 0, Sheen = new[] { "#86a0c8", "#9a92cc" }, SheenOpacity = 0.1f,
            Rim = 0.55f, Ink = "#dde4ec", Dim = "#a9b3c1", Faint = "#848e9d", Accent = "#8ed3c9", OnAccent = "#0c2723",
            KeyOpacity = 0.09f,
        },
    };

    /// <summary>The look the panel is in now (Panel.Look keeps it), for what's drawn in its light outside its sheets.</summary>
    public static PanelLook InUse { get; set; } = new();

    /// <summary>The look's light at an opacity, for the beams and cues drawn over the world (the pointer's ray, the
    /// teleport arc, the crosshair, the boards' and the world's cues): its first sheen colour (white in glass and silver,
    /// rose in pearl, blue in smoke), lifted toward white until it reads as light rather than a colour over any world.</summary>
    public System.Numerics.Vector4 Light(float alpha)
    {
        var hex = Sheen.Length > 0 ? Sheen[0] : Tint;
        if (!SKColor.TryParse(hex, out var c)) c = SKColors.White;
        float r = c.Red / 255f, g = c.Green / 255f, b = c.Blue / 255f;
        float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
        const float least = 0.8f;
        if (lum < least)
        {
            float t = (least - lum) / (1 - lum);
            (r, g, b) = (r + (1 - r) * t, g + (1 - g) * t, b + (1 - b) * t);
        }
        return new System.Numerics.Vector4(r, g, b, alpha);
    }

    public static PanelLook PresetNamed(string? name) => Presets.FirstOrDefault(p => p.Preset == name) ?? Presets[0];

    /// <summary>A saved look as it should load: a preset by name takes the preset as it is now (so a redesign reaches
    /// it), the person's own ("yours") keeps its values if they're still valid, anything else is the default.</summary>
    public static PanelLook Restore(PanelLook? saved)
    {
        if (saved == null) return Presets[0];
        if (saved.Preset != "yours") return PresetNamed(saved.Preset);
        try
        {
            if (!Materials.Contains(saved.Material) || !Fonts.Contains(saved.Font) || saved.Sheen is not { Length: <= 3 }) return Presets[0];
            // the colours parse (the painter would throw on one that doesn't)
            foreach (var c in saved.Sheen.Concat(new[] { saved.Tint, saved.Ink, saved.Dim, saved.Faint, saved.Accent, saved.OnAccent, saved.Key }))
                if (!SKColor.TryParse(c, out _)) return Presets[0];
            return saved with { TintOpacity = Math.Clamp(saved.TintOpacity, GlassLight.MinBody, 1), Glow = Math.Clamp(saved.Glow, 0, 1) };
        }
        catch (Exception) { return Presets[0]; }
    }

    // --- reading and changing it ------------------------------------------------------------------------------------

    public JsonObject ToJson() => new()
    {
        ["preset"] = Preset, ["material"] = Material, ["tint"] = Tint, ["tint_opacity"] = Round(TintOpacity), ["glow"] = Round(Glow),
        ["sheen"] = new JsonArray(Sheen.Select(g => (JsonNode)g).ToArray()), ["sheen_opacity"] = Round(SheenOpacity),
        ["frost"] = Round(Frost), ["rim"] = Round(Rim), ["ink"] = Ink, ["dim"] = Dim, ["faint"] = Faint,
        ["accent"] = Accent, ["on_accent"] = OnAccent, ["key"] = Key, ["key_opacity"] = Round(KeyOpacity),
        ["radius"] = Round(Radius), ["font"] = Font,
    };

    static double Round(float f) => Math.Round(f, 3);

    /// <summary>This look with the given values changed (a preset first, if named, then the rest over it). Throws
    /// ArgumentException with the reason for a value that isn't valid; how readable the result is, is
    /// <see cref="Readability"/>'s advice, not a refusal.</summary>
    public PanelLook With(JsonObject a)
    {
        var l = a["preset"] is JsonValue pv && pv.TryGetValue<string>(out var preset)
            ? Presets.FirstOrDefault(p => p.Preset == preset) ?? throw new ArgumentException($"preset is one of {string.Join(", ", Presets.Select(p => p.Preset))}")
            : this;
        bool changed = false;
        string Colour(string key, string now)
        {
            if (a[key] is not JsonValue v || !v.TryGetValue<string>(out var s)) return now;
            if (!SKColor.TryParse(s, out var c)) throw new ArgumentException($"{key}: a colour like #3a5fd0");
            changed = true;
            return $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}";
        }
        float Num(string key, float now, float min, float max)
        {
            if (a[key] is not JsonValue v || !v.TryGetValue<double>(out var d)) return now;
            if (double.IsNaN(d) || d < min - 1e-6 || d > max + 1e-6) throw new ArgumentException($"{key}: from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}");
            changed = true;
            return Math.Clamp((float)d, min, max);   // min/max are floats: 0.05f lies a hair above 0.05
        }
        string OneOf(string key, string now, string[] allowed)
        {
            if (a[key] is not JsonValue v || !v.TryGetValue<string>(out var s)) return now;
            if (!allowed.Contains(s)) throw new ArgumentException($"{key} is one of {string.Join(", ", allowed)}");
            changed = true;
            return s;
        }
        var sheen = l.Sheen;
        if (a["sheen"] is JsonArray ga)
        {
            var list = ga.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) && SKColor.TryParse(s, out var c) ? $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}"
                : throw new ArgumentException("sheen: up to three colours like #3a5fd0")).ToArray();
            if (list.Length > 3) throw new ArgumentException("sheen: up to three colours");
            sheen = list;
            changed = true;
        }
        var next = l with
        {
            Material = OneOf("material", l.Material, Materials),
            Tint = Colour("tint", l.Tint), TintOpacity = Num("tint_opacity", l.TintOpacity, GlassLight.MinBody, 1), Glow = Num("glow", l.Glow, 0, 1),
            Sheen = sheen, SheenOpacity = Num("sheen_opacity", l.SheenOpacity, 0, 0.8f),
            Frost = Num("frost", l.Frost, 0, 1), Rim = Num("rim", l.Rim, 0, 1),
            Ink = Colour("ink", l.Ink), Dim = Colour("dim", l.Dim), Faint = Colour("faint", l.Faint),
            Accent = Colour("accent", l.Accent), OnAccent = Colour("on_accent", l.OnAccent),
            Key = Colour("key", l.Key), KeyOpacity = Num("key_opacity", l.KeyOpacity, 0, 0.9f),
            Radius = Num("radius", l.Radius, 0, 32), Font = OneOf("font", l.Font, Fonts),
        };
        if (changed) next = next with { Preset = "yours" };
        return next;
    }

    /// <summary>How readable text on it would be (null: it reads well over any world), as advice that comes back with a
    /// change: what would be faint, over which world, and what would help. Measured as the headset shows it (its
    /// compositor blends in linear light; GlassLight): the ink against the body laid over a black, a mid-grey and a white
    /// world (the frost blurs the world but keeps its brightness) with the glow's light in it, and with whatever light
    /// lies on the body where text can sit (the sheen at full strength, pearl's satin highlight), on the body and on a lit
    /// key; the accent standing out from the body (it's the confirm key and an "on" switch), and its own label.
    /// Confirmations are drawn in a preset whatever the look (Panel.Draw), so they never depend on this.</summary>
    public string? Readability()
    {
        var notes = Legibility().Notes;
        return notes.Count == 0 ? null : "text may be hard to read: " + string.Join("; ", notes);
    }

    /// <summary>The least contrasts the measure finds (ink, dim, ink on a lit key, accent on the body, the accent's
    /// label), for tuning the presets: their margin over what reads well.</summary>
    public string Margins()
    {
        var m = Legibility();
        return $"ink {m.Ink:0.0} (3.2), dim {m.Dim:0.0} (2.3), on a key {m.OnKey:0.0} (3.2), accent {m.Accent:0.0} (1.8), its label {m.Label:0.0} (4.5)";
    }

    /// <summary>A pixel of the sheet as Skia paints it (gamma-encoded, premultiplied): the body, light laid over it, a
    /// key; and how bright it shows over a grey world in the headset, as GlassLight finishes it and the compositor lays
    /// it (its own light alpha x decode(colour), the glow through its clear part, the world through the rest).</summary>
    readonly record struct Px(double R, double G, double B, double A)
    {
        public static Px Of(string hex, double a)
        {
            var c = SKColor.Parse(hex);
            return new(c.Red / 255.0 * a, c.Green / 255.0 * a, c.Blue / 255.0 * a, a);
        }
        public Px With(string hex, double a)
        {
            var t = Of(hex, a);
            return new(t.R + R * (1 - a), t.G + G * (1 - a), t.B + B * (1 - a), a + A * (1 - a));
        }
        public double Shows(double world, (float R, float G, float B) glow, double body)
        {
            bool lit = glow.R + glow.G + glow.B > 0;
            double k = lit ? Math.Max(0, Math.Min(1, A / body) - A) / (1 - body) : 0;
            double a = A;
            double Ch(double v, float g) => Math.Min(1, (a > 0 ? a * GlassLight.Decode((float)Math.Min(1, v / a)) : 0) + k * g + (1 - a) * world);
            return 0.2126 * Ch(R, glow.R) + 0.7152 * Ch(G, glow.G) + 0.0722 * Ch(B, glow.B);
        }
    }

    internal static double Lum(string hex)
    {
        var c = SKColor.Parse(hex);
        return 0.2126 * GlassLight.Decode(c.Red / 255f) + 0.7152 * GlassLight.Decode(c.Green / 255f) + 0.0722 * GlassLight.Decode(c.Blue / 255f);
    }

    internal static double Contrast(double la, double lb) => (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);

    /// <summary>The ink's contrast against the bare body over a world this bright (linear luminance), as the headset
    /// shows it, glow and all: what adaptive ink chooses by (AdaptiveInk).</summary>
    public double InkContrast(double world)
    {
        double body = Math.Clamp(TintOpacity, GlassLight.MinBody, 1);
        return Contrast(Lum(Ink), Px.Of(Tint, body).Shows(world, GlassLight.Light(this), body));
    }

    (double Ink, double Dim, double OnKey, double Accent, double Label, List<string> Notes) Legibility()
    {
        double ink = Lum(Ink), dim = Lum(Dim), accent = Lum(Accent);
        var glow = GlassLight.Light(this);
        double body = Math.Clamp(TintOpacity, GlassLight.MinBody, 1);
        var plain = Px.Of(Tint, body);
        // the light that lies on the body where text can sit, at full strength
        var lit = Sheen.Select(g => plain.With(g, SheenOpacity))
            .Concat(Material == "pearl" ? new[] { plain.With("#ffffff", 0.35 * Rim) } : Array.Empty<Px>()).ToList();
        double keyAlpha = Math.Min(1, (KeyOpacity * 1.5 + 0.02) * 1.08);   // a key lit by the pointer
        // the least of each and the world it's least over
        var worst = new (double Value, string World, string Fix)[4];
        for (int i = 0; i < worst.Length; i++) worst[i] = (99, "", "");
        void Least(int i, double v, string world, string fix) { if (v < worst[i].Value) worst[i] = (v, world, fix); }
        foreach (var (behind, world) in new[] { (0.0, "a dark"), ((double)GlassLight.Decode(118 / 255f), "a mid-grey"), (1.0, "a bright") })
        {
            double plainLum = plain.Shows(behind, glow, body);
            // the light on the body that moves it most toward the ink
            var bg = plain;
            double bgLum = plainLum;
            foreach (var with in lit)
            {
                double l = with.Shows(behind, glow, body);
                if (Math.Abs(l - ink) < Math.Abs(bgLum - ink)) (bg, bgLum) = (with, l);
            }
            string fix = Contrast(ink, plainLum) >= 3.2 ? "lower sheen_opacity or use sheen colours closer to the tint"
                : bgLum < ink ? "make the tint more opaque or the ink lighter"
                : behind == 0 && Glow < 1 ? "raise glow, or darken the ink" : "darken the ink";
            Least(0, Contrast(ink, bgLum), world, fix);
            Least(1, Contrast(dim, bgLum), world, "change dim");
            Least(2, Contrast(ink, bg.With(Key, keyAlpha).Shows(behind, glow, body)), world, "lower key_opacity or change key");
            Least(3, Contrast(accent, plainLum), world, "change accent");
        }
        double label = Contrast(Lum(OnAccent), accent);
        var notes = new List<string>();
        var what = new[] { ("the ink", 3.2), ("dim text", 2.3), ("text on a lit key", 3.2), ("the accent (the confirm key, switches) against the body", 1.8) };
        for (int i = 0; i < worst.Length; i++)
            if (worst[i].Value < what[i].Item2)
                notes.Add($"{what[i].Item1} would have a contrast of {Math.Floor(worst[i].Value * 10) / 10:0.0} over {worst[i].World} world ({what[i].Item2:0.0} reads well): {worst[i].Fix}");
        if (label < 4.5) notes.Add($"the confirm key's label would have a contrast of {Math.Floor(label * 10) / 10:0.0} on the accent (4.5 reads well): change on_accent");
        return (worst[0].Value, worst[1].Value, worst[2].Value, worst[3].Value, label, notes);
    }
}
