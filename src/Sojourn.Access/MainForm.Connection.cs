using Sojourn.Core.Agents;
using Sojourn.Session.Login;

namespace Sojourn.Access;

/// <summary>
/// The connection after arrival, in words. When the session drops out of world (a region that stops answering, a
/// crossing or teleport that never arrives, the grid disconnecting), the session's own notice says why and that it's
/// logging in again; from here on each try that fails is said once ("Not back yet: ..."), being back is said with
/// where, and a login that can't succeed by itself (a changed password, say) is said plainly and hands over to the login
/// window (<see cref="NeedsLogin"/>). Before this nothing was said at all: on 2026-11-01 the blind tester heard only
/// "Not sent: not logged in" after a train's crossing failed, took it for a crash and closed the app while it was
/// logging in again.
/// </summary>
public sealed partial class MainForm
{
    bool _connLost;
    string? _connFailureSaid;

    /// <summary>The session gave up logging in by itself: the words to say, for the login window to take over.</summary>
    public event Action<string>? NeedsLogin;

    void WatchConnection()
    {
        if (!_arrived || Host?.Session is not { } s) return;
        if (s.Online)
        {
            if (!_connLost) return;
            _connLost = false;
            _connFailureSaid = null;
            var where = s.Client?.Network.CurrentSim?.Name;
            Announce(where is { Length: > 0 } ? $"Back in Second Life, in {where}." : "Back in Second Life.");
            OutfitMoving(login: true);
            return;
        }
        if (s.Status == AgentStatus.Stopping) return;   // logging out, by the person's own hand or on quitting
        if (!_connLost)
        {
            // logged out by the person: nothing to say; a drop the session is mending: its notice has said why
            if (!s.WantsOnline && s.LoginFailure is null) return;
            _connLost = true;
        }
        if (s.LoginFailure is not { } f) return;
        var key = $"{f.Key}|{f.Message}|{s.WantsOnline}";
        if (key == _connFailureSaid) return;
        _connFailureSaid = key;
        if (s.WantsOnline)
        {
            Announce("Not back yet: " + (f.Key == "presence"
                ? "Second Life still counts you as logged in from before; that clears within a minute or two."
                : Words(f)) + " Trying again by itself.");
            return;
        }
        var words = "Couldn't log in again: " + Words(f);
        Announce(words + " The login window is open: Enter there logs in.");
        NeedsLogin?.Invoke(words);
    }

    static string Words((string Key, string Message) f)
    {
        var (plain, detail) = LoginWords.For(f.Key, f.Message);
        return detail != null ? $"{plain} ({detail})" : plain;
    }
}
