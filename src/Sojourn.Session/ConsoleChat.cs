using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Session;

/// <summary>
/// Local chat in the console beside the window: what's said nearby (and IMs) is printed; a typed line is said on
/// channel 0. "/shout ...", "/whisper ...", "/quit"; "/ask ..." and "/model ..." for the assistant. A stand-in until
/// there's an in-world panel.
/// </summary>
public sealed class ConsoleChat
{
    AgentSession? _session;
    volatile string? _pendingTerms;   // "tos" / "critical" while the grid waits for the person's answer
    volatile bool _pendingMfa;

    /// <summary>
    /// The session whose chat this is. Login questions (new Terms of Service, MFA tokens) are answered here by the
    /// person at the keyboard: the agent API can see that login waits, but can't answer.
    /// </summary>
    public AgentSession? Session
    {
        get => _session;
        set
        {
            _session = value;
            if (value is null) return;
            value.TermsRequested += OnTerms;
            value.MfaRequested += OnMfa;
            value.QuitAsked += () => QuitRequested = true;   // LLAppViewer requestQuit (the assistant, asked to quit)
            // login starts when the session is added, so a question may already be waiting
            if (value.PendingTerms is { } pending) OnTerms(pending.Kind, pending.Message);
            if (value.Status == AgentStatus.MfaRequired) OnMfa();

            void OnTerms(string kind, string message)
            {
                if (_pendingTerms == kind) return;
                _pendingTerms = kind;
                var what = kind == "tos" ? "Second Life's Terms of Service have changed since this account last agreed."
                                         : "The grid has a message you must read before logging in.";
                Print(what, ConsoleColor.Yellow);
                if (!string.IsNullOrWhiteSpace(message)) Print(message.Trim(), ConsoleColor.Yellow);
                if (kind == "tos") Print("Read them at https://lindenlab.com/tos", ConsoleColor.Yellow);
                Print("Type /accept to agree and log in, or /decline.", ConsoleColor.Yellow);
            }

            void OnMfa()
            {
                if (_pendingMfa) return;
                _pendingMfa = true;
                Print("The grid asks for your multi-factor code: type /mfa 123456", ConsoleColor.Yellow);
            }
        }
    }

    /// <summary>"/ask ...": a typed request for the assistant.</summary>
    public Action<string>? Ask;

    /// <summary>"/model [quick|sonnet|opus]": shows or sets the assistant's model.</summary>
    public Action<string>? SetModel;

    public bool QuitRequested { get; private set; }

    public void Attach(GridClient c)
    {
        c.Self.ChatFromSimulator += (_, e) =>
        {
            if (e.Type is ChatType.StartTyping or ChatType.StopTyping or ChatType.Debug) return;
            if (e.AudibleLevel != ChatAudibleLevel.Fully || string.IsNullOrEmpty(e.Message)) return;
            if (_session?.Mutes.IsMuted(e.SourceID, e.FromName, e.OwnerID) == true) return;   // blocked (TesselAgent mute)
            var how = e.Type switch { ChatType.Shout => " shouts", ChatType.Whisper => " whispers", _ => "" };
            var msg = e.Message.StartsWith("/me ") ? $"{e.FromName}{e.Message[3..]}" : $"{e.FromName}{how}: {e.Message}";
            Print(msg, e.SourceType == ChatSourceType.Object ? ConsoleColor.DarkGreen : ConsoleColor.White);
        };
        c.Self.IM += (_, e) =>
        {
            if (_session?.Mutes is { } mutes && (mutes.IsMuted(e.IM.FromAgentID, e.IM.FromAgentName)
                || e.IM.Dialog == InstantMessageDialog.MessageFromObject && mutes.IsMuted(e.IM.IMSessionID))) return;
            if (e.IM.Dialog is InstantMessageDialog.MessageFromAgent or InstantMessageDialog.MessageFromObject && !string.IsNullOrEmpty(e.IM.Message))
                Print($"IM {e.IM.FromAgentName}: {e.IM.Message}", ConsoleColor.Cyan);
        };
    }

    static readonly object Gate = new();

    static void Print(string text, ConsoleColor color)
    {
        lock (Gate)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"{DateTime.Now:HH:mm} {text}");
            Console.ForegroundColor = old;
        }
    }

    public void Start()
    {
        if (Console.IsInputRedirected) return;
        var t = new Thread(Read) { IsBackground = true, Name = "chat input" };
        t.Start();
    }

    void Read()
    {
        while (true)
        {
            string? line;
            try { line = Console.ReadLine(); } catch { return; }
            if (line == null) return;
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line is "/quit" or "/exit") { QuitRequested = true; return; }
            if (_pendingTerms is not null && line is "/accept" or "/decline")
            {
                _pendingTerms = null;
                _session?.AnswerTerms(line == "/accept");
                Print(line == "/accept" ? "Accepted; logging in." : "Declined; not logging in.", ConsoleColor.DarkGray);
                continue;
            }
            if (_pendingMfa && line.StartsWith("/mfa "))
            {
                _pendingMfa = false;
                _session?.SubmitMfa(line[5..]);
                continue;
            }
            if (line.StartsWith("/ask "))
            {
                Ask?.Invoke(line[5..].Trim());
                continue;
            }
            if (line == "/model" || line.StartsWith("/model "))
            {
                SetModel?.Invoke(line[6..].Trim());
                continue;
            }
            var c = Session?.Client;
            if (c is null || !c.Network.Connected) { Print("(not connected)", ConsoleColor.DarkGray); continue; }
            var type = ChatType.Normal;
            if (line.StartsWith("/shout ")) { type = ChatType.Shout; line = line[7..]; }
            else if (line.StartsWith("/whisper ")) { type = ChatType.Whisper; line = line[9..]; }
            c.Self.Chat(line, 0, type);
        }
    }
}
