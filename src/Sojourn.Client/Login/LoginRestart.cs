using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Sojourn.Client.Login;

/// <summary>
/// Back to the login screen: once the client has shut down as it does on quitting (logged out, the assistant and
/// voice stopped, the headset let go), it starts again with --login and the account it was on shown selected, as
/// DeviceLoss starts it again after a lost GPU. The account is chosen before the session exists (its state dir is
/// its own), so another account always means another run.
/// </summary>
public static class LoginRestart
{
    static readonly string[] Dropped = { "--login-resume" };
    static readonly string[] DroppedWithValue = { "--start", "--login-account" };

    public static void ToLoginScreen(string[] args, SavedAccount? account, ILogger log)
    {
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (Dropped.Contains(args[i])) continue;
            if (DroppedWithValue.Contains(args[i])) { i++; continue; }
            rest.Add(args[i]);
        }
        if (!rest.Contains("--login")) rest.Insert(0, "--login");
        if (account != null) rest.AddRange(new[] { "--login-account", Encode(account) });
        try
        {
            var psi = DeviceLoss.Relaunch(rest.ToArray(), null);
            psi.Environment[DeviceLoss.AfterVar] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            var child = Process.Start(psi);
            log.LogInformation("back to the login screen (pid {Pid})", child?.Id);
        }
        catch (Exception e) { log.LogError("couldn't start the login screen again: {Msg}", e.Message); }
    }

    /// <summary>An account for --login-account (no password): grid, first, last and start, tab-separated.</summary>
    public static string Encode(SavedAccount a) => string.Join('\t', a.Grid, a.First, a.Last, a.Start);

    public static SavedAccount? Decode(string? s)
    {
        var p = s?.Split('\t');
        if (p is not { Length: 4 } || p[1].Length == 0) return null;
        return new SavedAccount { Grid = p[0], First = p[1], Last = p[2], Start = p[3] };
    }
}
