using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Sojourn.Core.Agents;

/// <summary>A button in a notification's form, as the viewer forwards it.</summary>
public sealed record NotifyButton(string Name, string Text, int Index, bool Default = false, bool Dynamic = false);

/// <summary>
/// The viewer's notifications, emulated: offers, dialogs and alerts arrive as protocol messages, become
/// notifications with the viewer's names, buttons and payloads (logged as ClaudeNotify events, exactly what
/// LLNotifications "forward" posts), and a later LLNotifications respond/cancel runs the protocol answer.
/// </summary>
public sealed class Notifications
{
    /// <summary>Called with the chosen button's name (null when cancelled / no truthy button) and the whole response.</summary>
    public delegate Task Responder(string? button, JsonObject response);

    sealed record Pending(JsonObject Payload, Responder? Respond, string? Key, DateTime At);

    readonly AgentSession _s;
    readonly ConcurrentDictionary<Guid, Pending> _pending = new();
    readonly ConcurrentDictionary<string, Guid> _byKey = new();

    public Notifications(AgentSession s) => _s = s;

    long _version;
    /// <summary>Changes whenever a notification starts or stops waiting (the client's notices follow it).</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>How long one with nothing to answer (an alert with only OK: a teleport that failed) stays among the
    /// pending: long enough for a caller to see it, not to pile up all session.</summary>
    static readonly TimeSpan AlertKept = TimeSpan.FromMinutes(3);

    /// <param name="key">Same-key notifications replace each other (a new dialog from an object cancels the last).</param>
    public Guid Add(string name, string type, string message, IEnumerable<NotifyButton> buttons, JsonObject? payload = null,
        JsonObject? substitutions = null, Responder? respond = null, string? key = null, string? label = null)
    {
        var id = Guid.NewGuid();
        var form = new JsonArray();
        foreach (var b in buttons)
        {
            var e = new JsonObject { ["type"] = "button", ["index"] = b.Index, ["name"] = b.Name, ["text"] = b.Text };
            if (b.Dynamic) { e["value"] = null; e["enabled"] = true; }
            if (b.Default) e["default"] = true;
            form.Add(e);
        }
        var now = DateTimeOffset.UtcNow;
        var n = new JsonObject
        {
            ["name"] = name,
            ["id"] = id.ToString(),
            ["type"] = type,
            ["message"] = message,
            ["summary"] = message.Length > 200 ? message[..200] : message,
            ["label"] = label ?? "",
            ["substitutions"] = substitutions ?? new JsonObject(),
            ["payload"] = payload ?? new JsonObject(),
            ["form"] = form,
            ["time"] = now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["expiry"] = now.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        };
        if (key is not null && _byKey.TryRemove(key, out var old)) _pending.TryRemove(old, out _);
        // Notifications with nothing to answer (tips, alerts without buttons) aren't kept.
        if (respond is not null || form.Count > 0)
        {
            _pending[id] = new Pending(n, respond, key, DateTime.UtcNow);
            if (key is not null) _byKey[key] = id;
        }
        Interlocked.Increment(ref _version);
        _s.Events.Log("ClaudeNotify", n);
        return id;
    }

    /// <summary>Something added with this key is still waiting for its answer.</summary>
    public bool Waiting(string key) { Sweep(); return _byKey.TryGetValue(key, out var id) && _pending.ContainsKey(id); }

    public JsonObject? Get(Guid id) { Sweep(); return _pending.TryGetValue(id, out var p) ? p.Payload : null; }

    public IEnumerable<JsonObject> All { get { Sweep(); return _pending.Values.OrderBy(p => p.At).Select(p => p.Payload); } }

    /// <summary>Waiting for an answer that does something (an offer, a script menu, a question), as opposed to an alert
    /// whose only button is OK.</summary>
    public bool Answerable(Guid id) => _pending.TryGetValue(id, out var p) && p.Respond is not null;

    /// <summary>Put away without answering, as closing the viewer's toast and clearing its notification well does: the
    /// offer, invitation or menu just goes unanswered (a script menu's Ignore). Logged as NotifyDismissed.</summary>
    public bool Dismiss(Guid id)
    {
        if (!_pending.TryRemove(id, out var p)) return false;
        if (p.Key is not null) _byKey.TryRemove(p.Key, out _);
        Interlocked.Increment(ref _version);
        _s.Events.Log("NotifyDismissed", new JsonObject { ["id"] = id.ToString(), ["name"] = p.Payload["name"]?.DeepClone() });
        return true;
    }

    /// <summary>Alerts with nothing to answer leave after a few minutes; anything past its expiry (an hour) leaves.</summary>
    void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, p) in _pending)
            if ((p.Respond is null && now - p.At > AlertKept) || now - p.At > TimeSpan.FromHours(1))
                if (_pending.TryRemove(id, out _))
                {
                    if (p.Key is not null) _byKey.TryRemove(p.Key, out _);
                    Interlocked.Increment(ref _version);
                }
    }

    /// <summary>LLNotifications respond: the first button in form order whose response value is truthy wins.</summary>
    /// <summary>A pending notification by full id or a unique id prefix (as the tools print them short).</summary>
    public Guid? Resolve(string? idOrPrefix)
    {
        if (string.IsNullOrWhiteSpace(idOrPrefix)) return null;
        if (Guid.TryParse(idOrPrefix, out var id)) return id;
        var want = idOrPrefix.Trim().ToLowerInvariant();
        var hits = _pending.Keys.Where(k => k.ToString().StartsWith(want, StringComparison.Ordinal)).Take(2).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>LLNotifications respond: the first button in form order whose response value is truthy wins. Buttons
    /// match by name (the viewer's contract) or, failing that, by their label, so {"Keep": true} works on a form
    /// whose Keep button is named otherwise. A bare string picks the button so named or labelled (any case), or is a
    /// script dialog's typed text; a bare number, the button with that index. A response that answers no button
    /// leaves the notification pending and returns an error instead of answering it as a decline (cancel declines).
    /// Null = answered.</summary>
    public async Task<string?> RespondAsync(Guid id, JsonNode? response, bool cancel = false)
    {
        if (!_pending.TryGetValue(id, out var p)) return "no such notification waiting (already answered, replaced or expired)";
        var map = response as JsonObject ?? new JsonObject();
        var form = (p.Payload["form"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        string? chosen = null;
        if (!cancel)
        {
            var (button, value, error) = Choose(form, response);
            if (error is not null) return error;
            chosen = button;
            if (button is not null && value is not null) map[button] = value.DeepClone();
        }
        if (!_pending.TryRemove(id, out _)) return "already answered";
        if (p.Key is not null) _byKey.TryRemove(p.Key, out _);
        Interlocked.Increment(ref _version);
        if (p.Respond is not null)
        {
            try { await p.Respond(chosen, map); }
            catch (Exception e) { _s.Log.LogWarning(e, "responding to {Name}", p.Payload["name"]); }
        }
        return null;
    }

    /// <summary>The button a response would answer a pending notification with (null: none, or cancelled), exactly
    /// as <see cref="RespondAsync"/> chooses it. The client's confirmation gate classifies by this.</summary>
    public string? Choose(Guid id, JsonNode? response) =>
        _pending.TryGetValue(id, out var p) ? Choose((p.Payload["form"] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList(), response).Button : null;

    /// <summary>The button a response answers, the value to give it in the response map when that isn't already there
    /// under its name (a label's, a typed text), or why it answers none.</summary>
    public static (string? Button, JsonNode? Value, string? Error) Choose(List<JsonObject> form, JsonNode? response)
    {
        if (response is JsonValue sv && sv.TryGetValue<string>(out var said))
        {
            // a bare string: the button so named, else so labelled, any case (a numbered menu's "2" is the button
            // labelled 2, not the third); else a script dialog's text box takes it as typed
            var want = said.Trim();
            var hit = form.FirstOrDefault(b => string.Equals(b["name"]?.GetValue<string>(), want, StringComparison.OrdinalIgnoreCase))
                ?? form.FirstOrDefault(b => string.Equals(b["text"]?.GetValue<string>(), want, StringComparison.OrdinalIgnoreCase));
            if (hit?["name"]?.GetValue<string>() is { } named) return (named, JsonValue.Create(true), null);
            if (form.Any(b => b["name"]?.GetValue<string>() == "!!llTextBox!!")) return ("!!llTextBox!!", JsonValue.Create(said), null);
            return (null, null, $"no button matches {said}; buttons: {Describe(form)}");
        }
        if (response is JsonValue v && Json.AsDouble(v) is { } idx)
        {
            // teleport-style callbacks also take a bare button index
            var byIndex = form.FirstOrDefault(b => (int?)Json.AsDouble(b["index"]) == (int)idx)?["name"]?.GetValue<string>();
            return byIndex is null ? (null, null, $"no button {idx}; buttons: {Describe(form)}") : (byIndex, null, null);
        }
        if (response is not (null or JsonObject))
            return (null, null, $"a response is a button's name or label, its index, or {{name: true}}; buttons: {Describe(form)}");
        var map = response as JsonObject ?? new JsonObject();
        foreach (var b in form)
        {
            var name = b["name"]?.GetValue<string>();
            if (name is not null && map.TryGetPropertyValue(name, out var val) && Truthy(val)) return (name, null, null);
        }
        foreach (var b in form)
        {
            var name = b["name"]?.GetValue<string>();
            var text = b["text"]?.GetValue<string>();
            var key = map.Select(kv => kv.Key).FirstOrDefault(k => text is not null && string.Equals(k, text, StringComparison.OrdinalIgnoreCase));
            if (name is not null && key is not null && Truthy(map[key])) return (name, map[key], null);
        }
        if (form.Count > 0 && map.Count > 0)
            return (null, null, $"no button matches {string.Join(", ", map.Select(kv => kv.Key))}; buttons: {Describe(form)}");
        // nothing given: a form with buttons waits for one (cancel declines); a notice without buttons is acknowledged
        if (form.Count > 0) return (null, null, $"no button given; buttons: {Describe(form)} (cancel declines)");
        return (null, null, null);
    }

    static string Describe(IEnumerable<JsonObject> form) => string.Join(", ", form.Select(b =>
    {
        var name = b["name"]?.GetValue<string>() ?? "";
        var text = b["text"]?.GetValue<string>() ?? "";
        return text.Length > 0 && text != name ? $"{name} ({text})" : name;
    }));

    public Task<string?> CancelAsync(Guid id) => RespondAsync(id, new JsonObject(), cancel: true);

    static bool Truthy(JsonNode? v) => v switch
    {
        null => false,
        JsonValue jv when jv.TryGetValue<bool>(out var b) => b,
        JsonValue jv when jv.TryGetValue<string>(out var s) => s.Length > 0,
        JsonValue jv => (Json.AsDouble(jv) ?? 0) != 0,
        _ => true,
    };

    // ---------- the viewer's standard forms ----------

    public static IEnumerable<NotifyButton> Ok => new[] { new NotifyButton("OK_okbutton", "OK", 0, true) };
    public static IEnumerable<NotifyButton> OkCancelIgnore(string ok = "OK") => new[]
    {
        new NotifyButton("OK_okcancelignore", ok, 0, true), new NotifyButton("Cancel_okcancelignore", "Cancel", 1),
    };
    public static IEnumerable<NotifyButton> None => Array.Empty<NotifyButton>();
}
