using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Sojourn.Session.Assistant;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Render.Realtime;

namespace Sojourn.Session.Control;

/// <summary>
/// SojournSettings: every setting the person can change (the settings menu's rows, the graphics options, the
/// desktop's keys), readable and changeable by any caller of the agent API, the assistant included, so asking it
/// for something the menu does works. A change either takes effect (and is saved, where the menu's would be) or is
/// refused with the reason; nothing is accepted without effect. A few stay the person's own because they guard
/// against the assistant itself: whether its touches on other people's things wait for confirmation, turning the
/// mic live, and answering calls and confirmations (not settings, so not here).
/// Changes run on the main thread (the loop drains them), as the menu's own presses do.
/// </summary>
public sealed class SettingsApi : LeapApi
{
    public override string Name => "SojournSettings";
    public override string Desc => "Every setting the person can change: get lists them with their values and choices; set {name: value, ...} changes them";
    public override bool NeedsSession => false;

    /// <summary>One setting: its section and what it does, its choices or range, reading it, and changing it (returns
    /// a reason to refuse, or null when done). SetAsync instead: one the grid keeps (the change waits for its answer,
    /// off the main thread).</summary>
    public sealed record Setting(string Section, string Desc, string Values, Func<JsonNode?> Get, Func<JsonNode, string?> Set)
    {
        public Func<JsonNode, CancellationToken, Task<string?>>? SetAsync { get; init; }
    }

    readonly Dictionary<string, Setting> _settings = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentQueue<Action> _main = new();
    readonly Action _changed;

    /// <summary>A setting the person keeps for themselves: always refused, with why.</summary>
    public void Own(string name, string section, string why, Func<JsonNode?> get) =>
        _settings[name] = new Setting(section, "the person's own: " + why, "(not for callers)", get, _ => $"{name} is the person's own: {why}");

    public void Add(string name, Setting s) => _settings[name] = s;

    /// <summary>The session the settings are for (the VR client's standard rows read it: Control/SettingsStandard.cs).</summary>
    internal AgentSession Session => S;

    readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An older name a setting still answers to (get and set; not listed).</summary>
    public void Alias(string old, string name) => _aliases[old] = name;

    string Canon(string name) => _aliases.TryGetValue(name, out var n) ? n : name;

    /// <param name="changed">After any change: save and redraw the menu.</param>
    public SettingsApi(AgentSession s, Action changed) : base(s)
    {
        _changed = changed;
        Op("get", "{name?} -> {settings: {name: {section, value, values, desc}}}: every setting (or one) as it stands", a =>
        {
            var only = a.Str("name") is { } asked ? Canon(asked) : null;
            if (only != null && !_settings.ContainsKey(only)) throw new LeapError($"no setting '{only}'; get with no name lists them");
            return new JsonObject { ["settings"] = OnMain(() => List(only)) };
        });
        Op("set", "{name: value, ...} -> {settings: what they are now}: changes settings; a refused one says why (nothing changes then)", async (a, ct) =>
        {
            var changes = a.Where(kv => kv.Key is not ("op" or "reply" or "timeout")).Select(kv => KeyValuePair.Create(Canon(kv.Key), kv.Value)).ToList();
            if (changes.Count == 0) throw new LeapError("nothing to set: give {name: value}; get lists the names");
            var unknown = changes.Where(c => !_settings.ContainsKey(c.Key)).Select(c => c.Key).ToList();
            if (unknown.Count > 0) throw new LeapError($"no setting {string.Join(", ", unknown.Select(u => $"'{u}'"))}; get lists them");
            var refused = new List<string>();
            // the grid's settings first, off the main thread (each waits for the grid's answer)
            foreach (var (name, value) in changes)
                if (_settings[name].SetAsync is { } setAsync && value != null && await setAsync(value, ct) is { } why) refused.Add(why);
            return OnMain(() =>
            {
                foreach (var (name, value) in changes)
                {
                    if (value == null) { refused.Add($"{name}: no value"); continue; }
                    if (_settings[name].SetAsync == null && _settings[name].Set(value) is { } why) refused.Add(why);
                }
                _changed();
                if (refused.Count > 0) throw new LeapError(string.Join("; ", refused));
                return new JsonObject { ["settings"] = List(null, changes.Select(c => c.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)) };
            });
        });
        // tests only (SOJOURN_TEST_MENU=1): the menu's every section, group and page as it stands, drawn to PNGs
        if (Environment.GetEnvironmentVariable("SOJOURN_TEST_MENU") == "1")
            Op("menuShots", "{dir} -> {files}: (tests) the settings menu drawn, every section, group and page, as the headset's at its size", a =>
            {
                if (MenuShots is not { } shots) throw new LeapError("this app has no menu");
                var dir = a.Str("dir") ?? throw new LeapError("need dir");
                var draw = OnMain(shots);   // the rows as they stand, gathered where they change
                return new JsonObject { ["files"] = new JsonArray(draw(dir).Select(f => (JsonNode)JsonValue.Create(f)).ToArray()) };
            });
            Op("menuPress", "{section, group?, label, part?: 0} -> {value}: (tests) a menu row pressed as the person's press would (part: 0 the "
                + "row or switch, -1 / 1 a stepper, 3 + i option i)", a =>
            {
                if (MenuPress is not { } press) throw new LeapError("this app has no menu");
                var value = OnMain(() => press(a.Str("section") ?? throw new LeapError("need section"), a.Str("group"),
                    a.Str("label") ?? throw new LeapError("need label"), a.Int("part", 0)));
                _changed();
                return new JsonObject { ["value"] = value };
            });
    }

    /// <summary>(Tests) the app's menu: run on the main thread, it gathers every section's rows and returns what draws
    /// them into a folder (off it), naming the files.</summary>
    public Func<Func<string, List<string>>>? MenuShots { get; set; }

    /// <summary>(Tests) a row of the app's menu pressed (section, group or null, label, part): its value after.</summary>
    public Func<string, string?, string, int, string>? MenuPress { get; set; }

    JsonObject List(string? only, HashSet<string>? names = null)
    {
        var o = new JsonObject();
        foreach (var (name, s) in _settings)
        {
            if (only != null && !name.Equals(only, StringComparison.OrdinalIgnoreCase)) continue;
            if (names != null && !names.Contains(name)) continue;
            o[name] = new JsonObject { ["section"] = s.Section, ["value"] = s.Get()?.DeepClone(), ["values"] = s.Values, ["desc"] = s.Desc };
        }
        return o;
    }

    /// <summary>Run on the main thread (the loop calls <see cref="Drain"/> every frame) and wait for it.</summary>
    T OnMain<T>(Func<T> f)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _main.Enqueue(() =>
        {
            try { done.SetResult(f()); }
            catch (Exception e) { done.SetException(e); }
        });
        if (!((IAsyncResult)done.Task).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(5))) throw new LeapError("the client is busy; try again");
        return done.Task.GetAwaiter().GetResult();   // a refusal comes out as itself, not wrapped
    }

    /// <summary>Main thread, every frame.</summary>
    public void Drain()
    {
        while (_main.TryDequeue(out var a)) a();
    }

    // --- the menu's rows for settings registered elsewhere, by name ---------------------------------------------
    // (null where this app hasn't got the setting; a press sets it as a caller's set would, on the main thread)

    /// <summary>A menu row's change refused, with why (the menu's presses get no reply of their own).</summary>
    public Action<string>? Refused { get; set; }

    public bool Has(string name) => _settings.ContainsKey(name);

    /// <summary>A setting's value as text ("on", "off", a choice's name), or null.</summary>
    public string? Text(string name) => _settings.TryGetValue(name, out var s) && s.Get() is { } v
        ? v.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => "on",
            System.Text.Json.JsonValueKind.False => "off",
            System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
            _ => v.ToJsonString(),
        }
        : null;

    public bool IsOn(string name) => Text(name) is "on" or "true" or "shown";

    /// <summary>A change from the menu (main thread): as a caller's set, a refusal to <see cref="Refused"/>.</summary>
    public void Change(string name, JsonNode value)
    {
        if (_settings.TryGetValue(name, out var s) && s.SetAsync == null && s.Set(value) is { } why) Refused?.Invoke(why);
    }

    public (MenuRow Row, Action<int> Act)? SwitchRow(string name, string label) =>
        Has(name) ? (MenuRow.Switch(label, IsOn(name)), _ => Change(name, JsonValue.Create(IsOn(name) ? "off" : "on"))) : null;

    /// <summary>Segments: each of values (shown as labels, if given).</summary>
    public (MenuRow Row, Action<int> Act)? ChoiceRow(string name, string label, string[] values, string[]? labels = null) =>
        Has(name) ? (MenuRow.Choice(label, labels ?? values, Array.IndexOf(values, Text(name))), i => Change(name, JsonValue.Create(values[i]))) : null;

    /// <summary>A stepper through values in order (for more than segments hold).</summary>
    public (MenuRow Row, Action<int> Act)? CycleRow(string name, string label, string[] values, string[]? labels = null)
    {
        if (!Has(name)) return null;
        int at = Array.IndexOf(values, Text(name));
        return (new MenuRow(label, at >= 0 ? (labels ?? values)[at] : Text(name) ?? "", true),
            d => Change(name, JsonValue.Create(values[Math.Clamp(Array.IndexOf(values, Text(name)) + (d == 0 ? 1 : d), 0, values.Length - 1)])));
    }

    /// <summary>A stepper through numbers (the nearest step up or down from where it stands).</summary>
    public (MenuRow Row, Action<int> Act)? NumberRow(string name, string label, float[] steps, Func<float, string> show)
    {
        if (!Has(name)) return null;
        float Now() => float.TryParse(Text(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : steps[0];
        return (new MenuRow(label, show(Now()), true), d =>
        {
            if (d == 0) d = 1;
            float now = Now();
            float next = d > 0 ? steps.FirstOrDefault(s => s > now + 0.001f, steps[^1]) : steps.LastOrDefault(s => s < now - 0.001f, steps[0]);
            Change(name, JsonValue.Create((double)next));   // (a float node reads as no number)
        });
    }

    // --- value helpers -------------------------------------------------------------------------------------------

    public static Func<JsonNode, string?> Choice(string name, string[] options, Action<int> apply) => v =>
    {
        var s = v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>().Trim().ToLowerInvariant().Replace(' ', '_') : v.ToJsonString();
        int i = Array.FindIndex(options, o => o == s);
        if (i < 0) return $"{name} is one of {string.Join(", ", options)}";
        apply(i);
        return null;
    };

    public static Func<JsonNode, string?> Switch(string name, Action<bool> apply) => v =>
    {
        bool? b = v.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.String => v.GetValue<string>().Trim().ToLowerInvariant() switch
            {
                "on" or "true" or "yes" or "shown" => true,
                "off" or "false" or "no" or "hidden" => false,
                _ => null,
            },
            _ => null,
        };
        if (b is not { } on) return $"{name} is on or off";
        apply(on);
        return null;
    };

    public static Func<JsonNode, string?> Number(string name, float min, float max, string unit, Action<float> apply) => v =>
    {
        if (Json.AsDouble(v) is not { } d || double.IsNaN(d)) return $"{name} takes a number ({min:0.##}-{max:0.##}{unit})";
        // compared as floats: 0.6 as a double is below 0.6f, which refused the setting's own minimum
        if ((float)d < min || (float)d > max) return $"{name} goes from {min:0.##} to {max:0.##}{unit}";
        apply((float)d);
        return null;
    };

    internal static JsonNode? N(object? x) => x switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        float f => JsonValue.Create(MathF.Round(f, 3)),
        int i => JsonValue.Create(i),
        string s => JsonValue.Create(s),
        _ => JsonValue.Create(x.ToString()),
    };
}
