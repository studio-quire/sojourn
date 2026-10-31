using System.Text.Json.Nodes;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Access;

/// <summary>A confirmation waiting for the person, as the app's gate has it: the client's own words, and from when a
/// press may answer it (Assistant/Panel.cs PendingConfirm in the VR client). Whole: every name in the lists its title
/// or detail cuts short at "and N more", in words, or null.</summary>
public sealed record ConfirmView(object Token, string Title, string Detail, Func<long, bool> ArmedAt, string? Whole = null);

public interface IConfirmGate
{
    ConfirmView? Waiting { get; }
    /// <summary>The person's answer to the one they were told about (ignored if it's no longer the one waiting).</summary>
    void Answer(object token, bool yes);
}

/// <summary>
/// What the person does by keys with a screen reader, apart from which keys (each app binds its own keyboard model):
/// answer the confirmation that waits; step through waiting notices (offers, script menus, invitations, questions),
/// hear one whole with its numbered keys, press one, put it away; hear things again. Every answer here is the person's
/// own, as their press on the feed or the panel is: the app passes whether the key was a real hand on the keyboard
/// (not a program's posted or injected key), and nothing is answered otherwise. Notices pressed here aren't gated (the
/// gate guards the assistant's answers, not the person's).
/// </summary>
public sealed class ReaderActions
{
    readonly AgentSession _s;
    readonly Announcer _a;
    readonly Func<JsonObject, string> _describe;

    /// <summary>The app's confirmation gate, if it has one.</summary>
    public IConfirmGate? Gate { get; set; }
    /// <summary>Said after a confirmation's words: the app's keys for it ("Enter confirms, Escape cancels.").</summary>
    public string ConfirmKeys { get; set; } = "";
    /// <summary>Said before those keys when it names only the start of a list: where the app has the whole list.</summary>
    public string ConfirmListKey { get; set; } = "";
    /// <summary>Said after a picked notice's keys: how to put it away in the app's keys.</summary>
    public string PutAwayKey { get; set; } = "";
    /// <summary>The app's name for the key that presses a notice's button i (0-based), or null past its last key.</summary>
    public Func<int, string?> KeyName { get; set; } = i => i < 9 ? $"{i + 1}" : null;

    /// <param name="describe">A notice in the app's words ("Kestrel offers a teleport “come over”").</param>
    public ReaderActions(AgentSession session, Announcer announcer, Func<JsonObject, string> describe)
    {
        _s = session;
        _a = announcer;
        _describe = describe;
    }

    // --- confirmations --------------------------------------------------------------------------------------------

    object? _told;

    /// <summary>Called as the app sees its gate (often): a new confirmation is read out at once.</summary>
    public void WatchConfirm()
    {
        var c = Gate?.Waiting;
        if (c?.Token == _told) return;
        _told = c?.Token;
        if (c != null) _a.Urgent(ConfirmWords(c));
    }

    string ConfirmWords(ConfirmView c) =>
        $"{c.Title.TrimEnd('.', ' ')}. {(c.Detail.Length > 0 ? c.Detail.TrimEnd() + " " : "")}{(c.Whole != null && ConfirmListKey.Length > 0 ? ConfirmListKey + " " : "")}{ConfirmKeys}";

    /// <summary>Said again on the person's ask: its lists whole after its title.</summary>
    string ConfirmWordsWhole(ConfirmView c) => c.Whole == null ? ConfirmWords(c)
        : $"{c.Title.TrimEnd('.', ' ')}. All of them: {c.Whole} {(c.Detail.Length > 0 ? c.Detail.TrimEnd() + " " : "")}{ConfirmKeys}";

    public bool ConfirmWaiting => Gate?.Waiting != null;

    /// <summary>The person's key on a waiting confirmation. pressedAt: when the key went down (not a repeat of one held
    /// from before); real: a hand on the keyboard. Confirming takes both and a confirmation that had armed by then;
    /// cancelling takes any key. True if it was taken.</summary>
    public bool Confirm(bool yes, long pressedAt, bool real)
    {
        if (Gate?.Waiting is not { } c) return false;
        if (yes && (!real || !c.ArmedAt(pressedAt))) return true;   // too soon, or not a hand: nothing happens
        Gate.Answer(c.Token, yes);
        _a.Answer(yes ? "Confirmed." : "Cancelled.");
        return true;
    }

    public enum Answered { Done, Gone, TooSoon }

    /// <summary>The person's answer to the confirmation they were shown (its token, from <see cref="IConfirmGate.Waiting"/>),
    /// never to another that took its place. Confirming takes a press that went down once it had armed (the app has
    /// already judged the press its person's own).</summary>
    public Answered Confirm(object token, bool yes, long pressedAt)
    {
        if (Gate?.Waiting is not { } c || !ReferenceEquals(c.Token, token)) return Answered.Gone;
        if (yes && !c.ArmedAt(pressedAt)) return Answered.TooSoon;
        Gate.Answer(c.Token, yes);
        _a.Answer(yes ? "Confirmed." : "Cancelled.");
        return Answered.Done;
    }

    // --- notices ------------------------------------------------------------------------------------------------------

    Guid? _picked;
    long _pickedAt;
    (string Object, long At)? _answeredMenu;
    const long PickMs = 120_000;

    /// <summary>The notices waiting for an answer, newest first (the one just heard is the first a key finds).</summary>
    List<(Guid Id, JsonObject N)> Waiting()
    {
        var list = new List<(Guid, JsonObject)>();
        foreach (var n in _s.Notify.All)
            if (Guid.TryParse(n.Str("id"), out var id) && _s.Notify.Answerable(id)) list.Add((id, n));
        list.Reverse();
        return list;
    }

    /// <summary>A notice picked by its key and still waiting (for two minutes after the last key on it): the app's number
    /// keys go to it.</summary>
    public bool Picked => _picked is { } id && Environment.TickCount64 - _pickedAt < PickMs && _s.Notify.Answerable(id);

    /// <summary>The next (+1: the newest first, then older) or the one before (-1) of the waiting notices, read whole
    /// with its keys.</summary>
    public void Step(int dir)
    {
        var all = Waiting();
        if (all.Count == 0) { _picked = null; _a.Answer("No notices waiting.", keep: false); return; }
        int at = _picked is { } p ? all.FindIndex(w => w.Id == p) : -1;
        at = at < 0 ? (dir > 0 ? 0 : all.Count - 1) : (at + dir + all.Count) % all.Count;
        Pick(all[at].Id, all[at].N, $"{at + 1} of {all.Count}. ", keep: false);
    }

    void Pick(Guid id, JsonObject n, string prefix, bool keep = true)
    {
        _picked = id;
        _pickedAt = Environment.TickCount64;
        _a.Answer(prefix + NoticeWords(n), keep, LineSources.OfNotice(n.Str("name")));
    }

    string NoticeWords(JsonObject n)
    {
        var keys = Buttons(n).Select((b, i) => KeyName(i) is { } k ? $"{k}, {b.Label}" : null).Where(k => k != null).ToList();
        var what = _describe(n).Trim();
        return $"{what}{(what.EndsWith('.') || what.EndsWith('?') || what.EndsWith('!') ? "" : ".")} "
               + (keys.Count > 0 ? string.Join(". ", keys) + ". " : "") + PutAwayKey;
    }

    /// <summary>The person's key on the picked notice's button (0-based, in the order its form gives them).</summary>
    public void Press(int index, bool real)
    {
        if (!real || !Picked || _picked is not { } id || _s.Notify.Get(id) is not { } n) return;
        var buttons = Buttons(n);
        if (index < 0 || index >= buttons.Count) { _a.Answer($"No key {KeyName(index) ?? ""} there.", keep: false); return; }
        var b = buttons[index];
        _pickedAt = Environment.TickCount64;
        if (n.Str("name") is "ScriptDialog" or "ScriptDialogGroup" && (n["payload"] as JsonObject)?.Str("object_id") is { } obj)
            _answeredMenu = (obj, Environment.TickCount64);
        _ = Task.Run(async () =>
        {
            var err = await _s.Notify.RespondAsync(id, new JsonObject { [b.Name] = true });
            _a.Answer(err == null ? $"{b.Label}, done." : $"{b.Label} didn't go through: {err}");
        });
    }

    /// <summary>The picked notice put away unanswered (as the feed's ×).</summary>
    public void PutAway(bool real)
    {
        if (!real || !Picked || _picked is not { } id) return;
        _s.Notify.Dismiss(id);
        _picked = null;
        int left = Waiting().Count;
        _a.Answer(left == 0 ? "Put away." : $"Put away. {left} more waiting.");
    }

    /// <summary>The number keys go back to the app's own uses.</summary>
    public void LetGo()
    {
        if (_picked == null) return;
        _picked = null;
        _a.Answer("Done with notices.", keep: false);
    }

    /// <summary>A notice that just arrived: the next page of a script menu the person answered by key a moment ago is
    /// picked and read whole at once, so paging a menu goes on without a key to find it. True if it took it.</summary>
    public bool FollowUp(Guid id)
    {
        if (_answeredMenu is not { } m || Environment.TickCount64 - m.At > 8000) return false;
        if (_s.Notify.Get(id) is not { } n || !_s.Notify.Answerable(id) || (n["payload"] as JsonObject)?.Str("object_id") != m.Object) return false;
        _answeredMenu = null;
        Pick(id, n, "");
        return true;
    }

    /// <summary>The thing in hand again: the confirmation (with every name its lists cut short), the picked notice, or
    /// the newest line.</summary>
    public void Repeat()
    {
        if (Gate?.Waiting is { } c) _a.Answer(ConfirmWordsWhole(c), keep: false, whole: c.Whole != null);
        else if (Picked && _picked is { } id && _s.Notify.Get(id) is { } n) Pick(id, n, "", keep: false);
        else _a.Repeat();
    }

    /// <summary>A notice's keys as the feed shows them: its form's buttons but a script menu's text box (the assistant
    /// types for that) and a group invitation's Info (a profile this client doesn't have).</summary>
    public static List<(string Name, string Label)> Buttons(JsonObject n)
    {
        var name = n.Str("name") ?? "";
        var list = new List<(string, string)>();
        foreach (var b in (n["form"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            string bn = b.Str("name") ?? "", text = b.Str("text") ?? bn;
            int index = b["index"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? i : 0;
            if (index < 0 || bn.Length == 0 || bn == "!!llTextBox!!" || (name == "JoinGroup" && bn == "Info")) continue;
            list.Add((bn, text.Length > 0 ? text : bn));
        }
        // an alert whose only key is OK has nothing to answer: it's put away (as the feed's ×)
        if (list.Count == 1 && list[0].Item2.Equals("OK", StringComparison.OrdinalIgnoreCase)) list.Clear();
        return list;
    }
}
