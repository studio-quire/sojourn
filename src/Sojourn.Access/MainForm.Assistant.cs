namespace Sojourn.Access;

/// <summary>The assistant: a typed request in a real edit control, feeding the session's assistant host (the same as the
/// VR client's), its replies read out by the announcer (A1) and kept in a history here, its choices as buttons. It
/// needs a provider that works (Alt+0); everything else in the app works without one.</summary>
public sealed partial class MainForm
{
    TabPage _assistantPage = null!;
    TextBox _assistantInput = null!, _assistantHistory = null!;
    FlowLayoutPanel _assistantChoices = null!;
    long _repliesVersion = -1;
    int _repliesShown;
    string _choicesShown = "";
    string? _statusSaid;

    void BuildAssistant()
    {
        _assistantPage = new TabPage("Assistant") { Name = "assistantPage", AccessibleName = "Assistant" };
        _assistantInput = InputBox("assistantInput", AccessOptions.AssistantInBuild ? "Ask the assistant" : "Ask the assistant (not in this build)");
        _assistantHistory = HistoryBox("assistantHistory", "The assistant's replies");
        _assistantChoices = new FlowLayoutPanel { Name = "assistantChoices", AccessibleName = "Choices", AutoSize = true, Dock = DockStyle.Bottom };
        var t = HistoryOverInput("Ask", _assistantInput, "Replies", _assistantHistory);
        _assistantPage.Controls.Add(t);
        _assistantPage.Controls.Add(_assistantChoices);
        _assistantInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            var text = _assistantInput.Text.Trim();
            if (text.Length == 0) return;
            _assistantInput.Clear();
            Ask(text);
        };
    }

    /// <param name="shown">the request's words in the history, when they differ from what the model is sent</param>
    /// <param name="said">what's read out as it goes</param>
    void Ask(string text, string? shown = null, string? said = null)
    {
        if (Host is not { } h) return;
        Append(_assistantHistory, $"You: {shown ?? text}");
        if (!_opt.Assistant || (!h.Assistant.SidecarAlive && h.Sidecar == null))
        {
            var none = AccessOptions.AssistantInBuild
                ? "The assistant isn't running: its Python side isn't set up on this PC. Everything else works without it."
                : "The assistant isn't in this build. Everything else works without it.";
            Append(_assistantHistory, none);
            Reader?.Answer(none);
            return;
        }
        if (!h.Assistant.SidecarAlive && h.Sidecar is { Running: false })
        {
            // its process ended (an error: the session's log has it), so nothing would ever answer
            const string stopped = "The assistant has stopped, so it can't answer. Quitting and starting Sojourn Access again starts it again. Everything else works without it.";
            Append(_assistantHistory, stopped);
            Reader?.Answer(stopped);
            return;
        }
        h.Assistant.Request(text);
        Reader?.Answer(h.Assistant.SidecarAlive ? said ?? "Asked." : $"{said ?? "Asked."} The assistant is still starting.", keep: false);
    }

    /// <summary>Control+Shift+L (Read-out > Copy the assistant's last answer): the last answer, whole, onto the clipboard,
    /// to paste to a friend (the blind tester's copy from Alt+9's history took only part of a long one, 2026-10-31). An
    /// answer given in several replies to the same request is all of them. SOJOURN_TEST_NO_CLIPBOARD leaves the clipboard
    /// alone, as Control+Shift+C does.</summary>
    void CopyLastAnswer()
    {
        if (Host is not { } h || Reader is not { } r) return;
        var replies = h.Assistant.Panel.Replies;
        if (replies.Count == 0) { r.Answer("The assistant hasn't answered anything yet.", keep: false); return; }
        var last = replies[^1];
        var parts = new List<string>();
        for (int i = replies.Count - 1; i >= 0 && (i == replies.Count - 1 || (last.Asked != null && replies[i].Asked == last.Asked)); i--)
            parts.Insert(0, replies[i].Text.Trim());
        var text = string.Join("\r\n\r\n", parts).ReplaceLineEndings("\r\n");
        int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        bool test = Environment.GetEnvironmentVariable("SOJOURN_TEST_NO_CLIPBOARD") == "1";
        if (!test)
            try { Clipboard.SetText(text); }
            catch (Exception e) { r.Answer($"Couldn't copy: {e.Message}", keep: false); return; }
        bool still = h.Assistant.Panel.Read(p => p.Working);
        r.Answer($"Copied the assistant's last answer, {words} {(words == 1 ? "word" : "words")}{(still ? ", as far as it's got: it's still answering" : "")}"
                 + $"{(test ? " (not onto the clipboard: a test)" : "")}.", keep: false);
    }

    void PollAssistant()
    {
        var a = Host!.Assistant;
        var panel = a.Panel;
        // a reply joins the history once it's whole (streamed answers grow in place until the assistant is done)
        bool working = panel.Read(p => p.Working);
        if (panel.RepliesVersion != _repliesVersion && !working)
        {
            _repliesVersion = panel.RepliesVersion;
            var replies = panel.Replies;
            for (int i = Math.Min(_repliesShown, replies.Count); i < replies.Count; i++) Append(_assistantHistory, $"Assistant: {replies[i].Text}");
            _repliesShown = replies.Count;
        }
        var (choices, status, notice) = panel.Read(p => (p.Choices.Select(c => c.Label).ToList(), p.Status, p.Notice));
        var key = string.Join("\n", choices);
        if (key != _choicesShown)
        {
            _choicesShown = key;
            _assistantChoices.SuspendLayout();
            foreach (Control c in _assistantChoices.Controls.Cast<Control>().ToList()) { _assistantChoices.Controls.Remove(c); c.Dispose(); }
            for (int i = 0; i < choices.Count; i++)
            {
                int n = i + 1;
                var b = new Button { Name = $"assistantChoice{n}", Text = $"{n}: {choices[i]}", AutoSize = true, UseMnemonic = false };
                b.Click += (_, _) =>
                {
                    // a request to the assistant, not a confirmation (what it leads to that needs one asks): the person's input
                    if (!RealInput.Person && !_set.ConfirmByHello) { Reader?.Answer(RealInput.Why, keep: false); return; }
                    Host?.Assistant.PickNumber(n);
                };
                _assistantChoices.Controls.Add(b);
            }
            _assistantChoices.ResumeLayout();
        }
        // the assistant's own state when it can't work (not running, no microphone...): said once
        var say = notice ?? (status == "the assistant isn't running" ? status : null);
        if (say != null && say != _statusSaid && _opt.Assistant) Append(_assistantHistory, say);
        _statusSaid = say;
    }
}
