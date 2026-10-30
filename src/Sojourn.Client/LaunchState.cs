using System.Text.Json.Nodes;
using Sojourn.Client.Login;
using Sojourn.Core.Agents;

namespace Sojourn.Client;

/// <summary>
/// What this run is doing, for the launcher that started it (src/Sojourn.Launcher, docs/launcher.md): with
/// SOJOURN_STATE_FILE set, a small JSON file (pid, state, headset, detail, the session's log) rewritten when it
/// changes. States: starting, form (the login screen), connecting, terms, code (the grid's questions, answered here by
/// the person), failed, world, ending. A run started again (a lost GPU, back to the login screen) inherits the variable
/// and writes its own pid, which is how the launcher follows it. Nothing here is a channel back: the launcher only reads.
/// Written by Claude Opus 5.5, 2026-10-29.
/// </summary>
public static class LaunchState
{
    static readonly string? FilePath = Environment.GetEnvironmentVariable("SOJOURN_STATE_FILE") is { Length: > 0 } p ? p : null;
    static string? _log;
    static string? _state, _detail;
    static bool _headset;
    static long _nextTick;

    public static void Started(string? sessionLog)
    {
        _log = sessionLog;
        Set("starting", false);
    }

    public static void Set(string state, bool headset, string? detail = null)
    {
        if (FilePath == null || state == _state && headset == _headset && detail == _detail) return;
        (_state, _headset, _detail) = (state, headset, detail);
        try
        {
            var o = new JsonObject
            {
                ["pid"] = Environment.ProcessId, ["state"] = state, ["headset"] = headset, ["detail"] = detail, ["log"] = _log,
            };
            var tmp = FilePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, o.ToJsonString());
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception) { }
    }

    /// <summary>From the frame loop: the login's stage or the world, four times a second at most.</summary>
    public static void Tick(LoginSheet login, AgentSession session, bool headset)
    {
        if (FilePath == null || Environment.TickCount64 < _nextTick) return;
        _nextTick = Environment.TickCount64 + 250;
        switch (login.Current)
        {
            case LoginSheet.Stage.Terms: Set("terms", headset); break;
            case LoginSheet.Stage.Mfa: Set("code", headset); break;
            case LoginSheet.Stage.Failed:
                var f = session.LoginFailure;
                Set("failed", headset, f == null ? null : LoginWords.For(f.Value.Key, f.Value.Message).Plain);
                break;
            case LoginSheet.Stage.Connecting or LoginSheet.Stage.Form: Set("connecting", headset); break;
            default: Set("world", headset); break;
        }
    }
}
