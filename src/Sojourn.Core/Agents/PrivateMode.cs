using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;

namespace Sojourn.Core.Agents;

/// <summary>
/// Incognito (docs/assistant.md > Incognito; "private mode" in the code): nothing from a spell is kept, and by default
/// no caller of the agent API learns the specifics of what the person is doing (what's said, who's there, where they
/// are), only the general picture the client builds (SojournPrivate situation). Enforced here, where every caller goes
/// through, rather than by asking a model to look away: <see cref="Leap.LeapDispatcher"/> refuses every op that isn't
/// on the allowlist (<see cref="Allow"/>, filled by the host; empty, everything is refused), turns blind handles in its
/// arguments back into ids, and hands back only what the op's rule lets through. With <see cref="Sees"/> (the person's
/// "let it see") ops run as usual but for a few rules that still hold (<see cref="AllowSeeing"/>), and what the spell
/// sealed is served while it lasts. Either way events logged in a spell are sealed: never written to events.jsonl,
/// served by /events only while the same spell lasts (blind: only the client's own kinds, the person's words), and
/// never after. IM lines are kept out of ims.jsonl, and out of the history ops once the spell is over
/// (AgentState.RecordIm). Thread-safe.
/// </summary>
public sealed class PrivateMode
{
    /// <summary>What one allowed op may be given and gives back. Check: a refusal for its arguments (before handles
    /// are turned back into ids; <see cref="IsHandle"/> tells them apart). Result: what the caller gets back (null:
    /// <see cref="Plain"/>). Ids: raw UUIDs may be among its arguments (the person's own inventory items); otherwise
    /// things are named only by handles, so a caller can't probe with ids it knew from before.</summary>
    public sealed record Rule(Func<JsonObject, PrivateMode, string?>? Check = null, Func<JsonNode?, JsonNode?>? Result = null, bool Ids = false);

    /// <summary>A result handed back whole (the op returns nothing about the world).</summary>
    public static JsonNode? Pass(JsonNode? r) => r;

    /// <summary>The default: done, or that it didn't work, without the op's own words (they can name things).</summary>
    public static JsonNode Plain(JsonNode? r) =>
        r is JsonObject o && o.ContainsKey("error") ? Json.Error("that didn't work (incognito keeps back why)") : new JsonObject { ["ok"] = true };

    /// <summary>Event kinds served while blind: the person's own words and presses (and dev mode's relay of them).</summary>
    public static readonly HashSet<string> OwnKinds = new(StringComparer.Ordinal) { "sojourn", "dev" };

    static readonly Regex Uuid = new("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    static readonly Regex HandleShape = new("^(thing|person|chat)-[a-z0-9]{6}$", RegexOptions.Compiled);

    readonly object _lock = new();
    readonly Dictionary<(string Pump, string Op), Rule> _rules = new();
    readonly Dictionary<(string Pump, string Op), Func<JsonObject, string?>> _still = new();
    readonly Dictionary<string, (UUID Id, string Kind)> _handles = new(StringComparer.Ordinal);
    readonly Dictionary<(UUID, string), string> _byId = new();
    readonly List<(double From, double To)> _spells = new();
    double _since;

    public bool On { get; private set; }

    /// <summary>The person's "let it see" (saved with their settings, off by default): while on, a spell lets callers see
    /// and act on specifics as usual; nothing of it is kept all the same.</summary>
    public bool Sees { get; private set; }

    /// <summary>On and not seeing: the allowlist, handles and the general picture.</summary>
    public bool IsBlind { get { lock (_lock) return On && !Sees; } }
    /// <summary>Which spell of private mode this is (1, 2, ...), 0 while it's off: what EventLog seals events with.</summary>
    public int Spell { get; private set; }
    /// <summary>What turned it on: menu, wrist, key, asked (the assistant, asked), person, place, im, api.</summary>
    public string? Why { get; private set; }
    public double Minutes => On ? (Json.UnixNow() - _since) / 60 : 0;

    /// <summary>A trigger turned it on (Client/Private/Triggers.cs), so it ends by itself when the trigger does.</summary>
    public bool Automatic => Why is "person" or "place" or "im";

    /// <summary>What callers are told turned it on: an automatic spell is only "auto", since which trigger fired would
    /// say who's near or where they are.</summary>
    public string? WhyForCallers => Automatic ? "auto" : Why;

    /// <summary>After a change: on, why, and for off how many minutes the spell lasted. Raised outside the lock.</summary>
    public event Action<bool, string, double>? Changed;

    /// <summary>After "let it see" changed (while on or off). Raised outside the lock.</summary>
    public event Action<bool>? SeesChanged;

    /// <summary>"Let it see" on or off; false if it already was. Who may is the host's to decide (during a spell only the
    /// person's own hand turns it on).</summary>
    public bool SetSees(bool sees)
    {
        lock (_lock)
        {
            if (Sees == sees) return false;
            Sees = sees;
        }
        SeesChanged?.Invoke(sees);
        return true;
    }

    /// <summary>Someone's words (who, and "im", "group" or "chat") are about to be recorded and logged: the client's
    /// automatic triggers (Private/Triggers.cs) can turn private mode on first, so the line that brought them is
    /// sealed too. On the network thread, before anything is kept.</summary>
    public Action<UUID, string>? WordsFrom { get; set; }

    /// <summary>Turns it on or off; false if it already was.</summary>
    public bool Set(bool on, string why)
    {
        double minutes;
        lock (_lock)
        {
            if (On == on)
            {
                // the person asking for it while a trigger has it on: theirs now, so it doesn't end with the trigger
                if (on && Automatic && why is not ("person" or "place" or "im")) Why = why;
                return false;
            }
            var now = Json.UnixNow();
            minutes = on ? 0 : (now - _since) / 60;
            if (on) { _since = now; Spell = _spells.Count + 1; }
            else { _spells.Add((_since, now)); Spell = 0; _handles.Clear(); _byId.Clear(); }
            On = on;
            Why = on ? why : null;
        }
        Changed?.Invoke(on, why, minutes);
        return true;
    }

    /// <summary>Whether a moment (unix seconds) fell in a spell of private mode, this one included.</summary>
    public bool WasPrivate(double unix)
    {
        lock (_lock)
            return (On && unix >= _since) || _spells.Any(s => unix >= s.From && unix <= s.To);
    }

    /// <summary>Whether callers may be shown what came at a moment (unix seconds): anything from outside a spell, and
    /// while a seeing spell lasts, what came in it.</summary>
    public bool ShownAt(double unix)
    {
        lock (_lock)
            return !((On && !Sees && unix >= _since) || _spells.Any(s => unix >= s.From && unix <= s.To));
    }

    /// <summary>Whether callers may be shown what a spell sealed (0: none): outside one, always; a spell's own, only while
    /// it lasts and sees.</summary>
    public bool ShownSpell(int spell)
    {
        if (spell == 0) return true;
        lock (_lock) return On && Sees && spell == Spell;
    }

    // --- the allowlist --------------------------------------------------------------------------------------------

    public void Allow(string pump, string op, Rule? rule = null)
    {
        lock (_lock) _rules[(pump, op)] = rule ?? new Rule();
    }

    public bool Allowed(string pump, string op)
    {
        lock (_lock) return _rules.ContainsKey((pump, op));
    }

    /// <summary>A rule that still holds while a spell sees (everything else runs as usual): a refusal, or null after
    /// adjusting the arguments in place.</summary>
    public void AllowSeeing(string pump, string op, Func<JsonObject, string?> check)
    {
        lock (_lock) _still[(pump, op)] = check;
    }

    /// <summary>The dispatcher's say while a spell sees: null to go ahead, else why not.</summary>
    public string? AdmitSeeing(string pump, string op, JsonObject data)
    {
        Func<JsonObject, string?>? check;
        lock (_lock) _still.TryGetValue((pump, op), out check);
        return check?.Invoke(data) is { } why ? "incognito is on: " + why : null;
    }

    /// <summary>The dispatcher's say while private: null to go ahead (handles in <paramref name="data"/> turned back
    /// into ids, in place, and the rule for the reply), else why not.</summary>
    public string? Admit(string pump, string op, JsonObject data, out Rule rule)
    {
        lock (_lock)
        {
            if (!_rules.TryGetValue((pump, op), out rule!))
                return $"incognito is on: {pump} {op} isn't one of the things the assistant can do while it lasts (the situation op gives the general picture)";
        }
        if (rule.Check?.Invoke(data, this) is { } why) return "incognito is on: " + why;
        if (!rule.Ids && HasId(data)) return "incognito is on: things are named by the handles the situation and the pointer give, not by ids";
        Unblind(data);
        return null;
    }

    static bool HasId(JsonNode? n) => n switch
    {
        JsonObject o => o.Any(kv => HasId(kv.Value)),
        JsonArray a => a.Any(HasId),
        JsonValue v when v.TryGetValue<string>(out var s) => Uuid.IsMatch(s) && !s.Contains("00000000-0000-0000-0000-000000000000"),
        _ => false,
    };

    void Unblind(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (o[key] is JsonValue v && v.TryGetValue<string>(out var s) && Resolve(s) is { } id) o[key] = id.ToString();
                    else Unblind(o[key]);
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var s) && Resolve(s) is { } id) a[i] = id.ToString();
                    else Unblind(a[i]);
                }
                break;
        }
    }

    /// <summary>What a caller gets back from an op it was let run while private.</summary>
    public static JsonNode? Reply(Rule rule, JsonNode? result) => (rule.Result ?? Plain)(result);

    // --- blind handles ----------------------------------------------------------------------------------------------

    /// <summary>An opaque name for a thing (kind thing), a person (person) or a conversation (chat), the same for the
    /// same one while this spell lasts, meaningless after. Handles are random, so they say nothing about what they
    /// stand for.</summary>
    public string Blind(UUID id, string kind)
    {
        lock (_lock)
        {
            if (_byId.TryGetValue((id, kind), out var h)) return h;
            const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
            do h = kind + "-" + string.Concat(Enumerable.Range(0, 6).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
            while (_handles.ContainsKey(h));
            _handles[h] = (id, kind);
            _byId[(id, kind)] = h;
            return h;
        }
    }

    public static bool IsHandle(string? s) => s != null && HandleShape.IsMatch(s);

    /// <summary>The id behind a handle of this spell, or null.</summary>
    public UUID? Resolve(string? handle, string? kind = null)
    {
        if (!IsHandle(handle)) return null;
        lock (_lock)
            return _handles.TryGetValue(handle!, out var t) && (kind == null || t.Kind == kind) ? t.Id : null;
    }

    /// <summary>A pointer target (Assistant/Pointer.cs's JSON) as private mode gives it: what kind of thing, a handle,
    /// roughly how far; no name, id, region or position. Something worn counts as its wearer.</summary>
    public JsonObject? BlindTarget(JsonNode? target)
    {
        if (target is not JsonObject t) return null;
        var o = new JsonObject();
        if (t.Bool("hud", false)) o["kind"] = "hud";
        else if (t.Uuid("worn_by") is { } wearer && wearer != UUID.Zero) { o["kind"] = "person"; o["handle"] = Blind(wearer, "person"); }
        else
            switch (t.Str("kind"))
            {
                case "avatar" when t.Uuid("id") is { } a: o["kind"] = "person"; o["handle"] = Blind(a, "person"); break;
                case "object" when t.Uuid("id") is { } p: o["kind"] = "thing"; o["handle"] = Blind(p, "thing"); break;
                default: o["kind"] = "ground"; break;
            }
        if (t["distance"] is JsonValue d && d.TryGetValue<double>(out var m)) o["distance"] = m < 3 ? Math.Round(m) : Math.Round(m / 5) * 5;
        if (t.Bool("looking", false)) o["looking"] = true;
        return o;
    }

    /// <summary>A `sojourn` event's data with its pointer targets blinded (while blind), marked `blind` so the sidecar
    /// knows at once (its ears leave the names around the person out of Whisper's prompt).</summary>
    public JsonObject BlindEvent(JsonObject data)
    {
        if (!IsBlind) return data;
        foreach (var key in new[] { "target", "first_target" })
            if (data.ContainsKey(key)) data[key] = BlindTarget(data[key]);
        data["blind"] = true;
        return data;
    }
}
