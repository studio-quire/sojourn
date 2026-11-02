using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Access;
using Sojourn.Session.Assistant;
using Sojourn.Session.Social;

namespace Sojourn.Access;

/// <summary>
/// The builders' tab (Help > Message the builders, Control+Shift+B; docs/access-app.md > Messages to the builders): a
/// conversation tab like an IM's, where a tester writes to the Claude sessions building Sojourn Access, through Studio
/// Quire's relay with their token (BuildersLine.cs). Its replies are read and sound as an IM's; ones that came while
/// the app was closed are said once, after logging in. The relay is asked gently: once a minute, every 15 s for ten
/// minutes after the person writes, at once on opening the tab. access_builders turns it all off. Written by Claude
/// Opus 5.5, 2026-11-01.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>The tab's source: its "session id" among the conversations, for muting and the conversation in focus.</summary>
    const string BuildersId = "builders";
    const string BuildersLabel = "The builders (Claude)";
    const string BuildersIntro = "Messages to the builders of Sojourn Access. You're writing to Claude sessions, AI, building Sojourn Access "
        + "at Studio Quire: not Tessel, and not a person. Replies come when a session reads your message, which can be minutes or a "
        + "day later; each is read out like an IM as it arrives, and any that came while Sojourn Access was closed are said after "
        + "you log in. Only what you type here goes, kept with our replies on Studio Quire's relay for 90 days. Type in the Message "
        + "box and press Enter to send.";
    const string BuildersNoToken = "Messages to the builders go with a token from Studio Quire, pasted into the key row at the top of "
        + "Alt+0, and you don't have one here. You can reach the people making Sojourn Access through Tessel (tesselwright) in "
        + $"Second Life, or on the issues page, {IssuesPage}.";

    BuildersLine? _line;
    BuildersState _bstate = null!;
    Conversation? _buildersTab;
    readonly List<BuildersLine.Message> _bmsgs = new();   // the conversation as fetched (the window's thread)
    readonly CancellationTokenSource _bstop = new();
    readonly SemaphoreSlim _bwake = new(0);
    long _bfastUntil;   // asked every 15 s until then (after the person writes)
    bool _brejected;    // the relay didn't take the token: not asked again until it changes

    void StartBuilders()
    {
        _line = new BuildersLine();
        _bstate = BuildersState.Load(_opt.BaseDir);
        _set.BuildersChanged += () => BeginInvoke(() =>
        {
            if (!_set.Builders && _buildersTab != null) CloseBuildersTab();
            _bwake.Release();
        });
        FormClosed += (_, _) => _bstop.Cancel();
        _ = Task.Run(() => BuildersLoopAsync(_bstop.Token));
    }

    /// <summary>Control+Shift+B, Help > Message the builders: the tab, opened if need be, the focus in its Message box;
    /// the first time ever, its introduction said.</summary>
    void GoBuilders()
    {
        if (!_set.Builders)
        {
            Answer("Messages to the builders are turned off. Alt+0's row Messages with the builders turns them on.");
            return;
        }
        var cv = BuildersTab();
        _conversations.SelectedTab = cv.Page;
        GoArea(_conversationsPage);
        _bwake.Release();
        bool token = ProviderKeys.RelayToken() != null;
        if (!token) BeginInvoke(() => Answer(BuildersNoToken));
        else if (!_bstate.Introduced)
        {
            _bstate.Introduced = true;
            _bstate.Save();
            BeginInvoke(() => Reader?.Answer(BuildersIntro));
        }
    }

    /// <summary>The tab, made if it isn't open: it waits, as a new IM's does; its history is the introduction (or how to
    /// reach us without a token) and the conversation so far.</summary>
    Conversation BuildersTab()
    {
        if (_buildersTab != null) return _buildersTab;
        var page = new TabPage(BuildersLabel) { Name = "conv:" + BuildersId, AccessibleName = BuildersLabel };
        var input = InputBox("convInput", "Message to the builders");
        input.AccessibleDescription = "Claude sessions building Sojourn Access at Studio Quire. Replies come when a session reads it.";
        var history = HistoryBox("convHistory", "Messages with the builders: Claude sessions at Studio Quire");
        page.Controls.Add(HistoryOverInput("Message", input, "History", history));
        var cv = new Conversation { SessionId = BuildersId, Title = BuildersLabel, Group = false, Builders = true, Page = page, Input = input, History = history };
        page.Tag = cv;
        input.KeyDown += (_, e) => ConversationKey(cv, e);
        AddConversation(cv);
        _buildersTab = cv;
        history.Text = (ProviderKeys.RelayToken() != null ? BuildersIntro : BuildersNoToken).ReplaceLineEndings("\r\n");
        foreach (var m in _bmsgs) Append(history, BuildersLineText(m));
        return cv;
    }

    void CloseBuildersTab()
    {
        if (_buildersTab is not { } cv) return;
        _convs.Remove(cv.SessionId);
        _conversations.TabPages.Remove(cv.Page);
        cv.Page.Dispose();
        _buildersTab = null;
        if (_conversations.TabCount == 0) NoConversationsShown();
    }

    string BuildersLineText(BuildersLine.Message m)
    {
        var who = m.Studio ? (m.By.Length > 0 ? m.By : "Claude") : "You";
        if (!_set.Timestamps) return $"{who}: {m.Text}";
        var at = m.At.Date == DateTime.Today ? $"{m.At:HH:mm}" : $"{m.At:d MMM HH:mm}";
        return $"[{at}] {who}: {m.Text}";
    }

    /// <summary>Enter in the tab's Message box (the window's thread).</summary>
    async Task SendToBuildersAsync(Conversation cv, string text)
    {
        if (ProviderKeys.RelayToken() is not { } token) { Answer(BuildersNoToken); Restore(cv, text); return; }
        if (_line == null) return;
        var a = await _line.SendAsync(token, text.Trim(), _bstop.Token);
        if (!a.Ok)
        {
            Host?.Log.LogWarning("builders: not sent ({Status}: {Said})", a.Status, a.Said ?? "");
            Answer(a.Status switch
            {
                0 => $"Not sent: Studio Quire's relay couldn't be reached ({a.Said}). It's back in the Message box.",
                401 => "Not sent: Studio Quire's relay didn't take your token. It's back in the Message box.",
                _ => $"Not sent: {a.Said ?? $"the relay answered {a.Status}"} It's back in the Message box.",
            });
            Restore(cv, text);
            return;
        }
        long id = a.Body?["id"]?.GetValue<long>() ?? 0;
        if (id > 0 && _bmsgs.All(m => m.Id != id))
        {
            var m = new BuildersLine.Message(id, DateTime.Now, false, "", text.Trim());
            _bmsgs.Add(m);
            if (_buildersTab is { } tab) Append(tab.History, BuildersLineText(m));
        }
        Host?.Log.LogInformation("builders: message {Id} sent", id);
        Reader?.Answer("Sent.");   // (not into nearby chat's history, as other answers go)
        _bfastUntil = Environment.TickCount64 + 10 * 60_000;
        _bwake.Release();
    }

    static void Restore(Conversation cv, string text)
    {
        if (cv.Input.IsDisposed || cv.Input.TextLength > 0) return;
        cv.Input.Text = text;
        cv.Input.SelectionStart = cv.Input.TextLength;
    }

    // --- asking the relay -------------------------------------------------------------------------------------------

    async Task BuildersLoopAsync(CancellationToken ct)
    {
        int poll = int.TryParse(Environment.GetEnvironmentVariable("SOJOURN_BUILDERS_POLL"), out var p) && p > 0 ? p : 60;
        long since = 0;
        string? mark = null;
        int failures = 0;
        bool first = true;
        // what came while the app was closed is said after "Logged in" (or after two minutes, logged in or not)
        for (int i = 0; i < 120 && !_arrived && !ct.IsCancellationRequested; i++) await Task.Delay(1000, ct).ConfigureAwait(false);
        while (!ct.IsCancellationRequested)
        {
            if (_set.Builders && ProviderKeys.RelayToken() is { } token)
            {
                var now = BuildersLine.Mark(token);
                if (now != mark)
                {
                    // another token: another conversation
                    (mark, since, first, _brejected, failures) = (now, 0, true, false, 0);
                    if (!OnWindow(() => BuildersReset(now))) return;
                }
                if (!_brejected)
                {
                    var (got, a) = await _line!.FetchAsync(token, since, ct).ConfigureAwait(false);
                    if (got != null)
                    {
                        if (got.Count > 0) since = got.Max(m => m.Id);
                        bool wasFirst = first;
                        first = false;
                        failures = 0;
                        if (!OnWindow(() => BuildersFetched(got, wasFirst))) return;
                    }
                    else if (a.Status == 401)
                    {
                        _brejected = true;
                        Host?.Log.LogWarning("builders: the relay didn't take the token");
                    }
                    else if (failures++ == 0)
                    {
                        Host?.Log.LogInformation("builders: the relay didn't answer ({Status}: {Said}); asking again later", a.Status, a.Said ?? "");
                    }
                }
            }
            int secs = Environment.TickCount64 < _bfastUntil ? Math.Min(15, poll) : poll;
            if (failures > 1) secs = Math.Min(300, secs * failures);
            try { await _bwake.WaitAsync(TimeSpan.FromSeconds(secs), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Runs on the window's thread and waits for it; false once the window has gone.</summary>
    bool OnWindow(Action act)
    {
        try { Invoke(act); return true; }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException) { return false; }
    }

    /// <summary>Another token than the one before (or the first): its conversation starts empty here.</summary>
    void BuildersReset(string mark)
    {
        _bmsgs.Clear();
        if (_buildersTab is { } tab) tab.History.Text = BuildersIntro.ReplaceLineEndings("\r\n");
        if (_bstate.Token != mark)
        {
            _bstate.Token = mark;
            _bstate.Seen = 0;
            _bstate.Save();
        }
    }

    /// <summary>What the relay had after the last ask (the window's thread): into the history; replies not yet said are
    /// read as an IM's (on the first ask of a run, as having come while the app was closed) with the IM's sound.</summary>
    void BuildersFetched(List<BuildersLine.Message> got, bool first)
    {
        var fresh = got.Where(m => _bmsgs.All(k => k.Id != m.Id)).ToList();
        _bmsgs.AddRange(fresh);
        var unsaid = fresh.Where(m => m.Studio && m.Id > _bstate.Seen).ToList();
        if (_buildersTab is { } open) foreach (var m in fresh) Append(open.History, BuildersLineText(m));
        else if (unsaid.Count > 0) BuildersTab();   // (filled from _bmsgs) waiting, as a new IM's tab
        if (unsaid.Count == 0) return;
        _bstate.Seen = unsaid.Max(m => m.Id);
        _bstate.Save();
        Host?.Log.LogInformation("builders: {N} replies arrived", unsaid.Count);
        bool muted = _set.MuteAll || _set.IsMuted(BuildersId);
        bool here = _focusedSource == BuildersId;
        var last = unsaid[^1];
        var entry = new FeedEntry(0, FeedKind.Im, BuildersId, BuildersLabel, UUID.Zero, last.By, last.Text, FeedStyle.Say, false, 0, 0);
        _earcons?.Arrived(entry);
        if (muted) return;
        if (first && !here)
        {
            // a long absence: the last three said, the rest in the tab
            var words = string.Join(" ", unsaid.TakeLast(3).Select(m => $"{Who(m)}: {Ended(m.Text)}"));
            var line = unsaid.Count == 1 ? $"While Sojourn Access was closed, the builders replied. {words}"
                : unsaid.Count <= 3 ? $"While Sojourn Access was closed, the builders replied {unsaid.Count} times. {words}"
                : $"While Sojourn Access was closed, the builders replied {unsaid.Count} times; the last three: {words} Control+Shift+B has them all.";
            Reader?.Say(AnnounceKind.Ims, line, from: LineFrom.App);
            return;
        }
        foreach (var m in unsaid)
        {
            var line = here ? $"{Who(m)}: {m.Text}" : $"{Who(m)}, the builders: {m.Text}";
            Reader?.Say(AnnounceKind.Ims, line, from: LineFrom.App);
        }

        static string Who(BuildersLine.Message m) => m.By.Length > 0 ? m.By : "Claude";
    }
}
