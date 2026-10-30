namespace Sojourn.Access;

/// <summary>The assistant: a typed request in a real edit control, feeding the session's assistant host (the same as the
/// VR client's), its replies read out by the announcer (A1) and kept in a history here, its choices as buttons. It
/// needs a Claude account; everything else in the app works without one.</summary>
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
                ? "The assistant isn't running: it needs a Claude account (and its Python side set up). Everything else works without it."
                : "The assistant isn't in this build. Everything else works without it.";
            Append(_assistantHistory, none);
            Reader?.Answer(none);
            return;
        }
        h.Assistant.Request(text);
        Reader?.Answer(h.Assistant.SidecarAlive ? said ?? "Asked." : $"{said ?? "Asked."} The assistant is still starting.", keep: false);
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
