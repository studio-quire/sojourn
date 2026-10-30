using Sojourn.Client.Assistant;
using Sojourn.Client.Control;
using Sojourn.Core.Config;

namespace Sojourn.Client.Login;

/// <summary>Program's side of the login screen: the sheet made ready (the person's look and size, the account shown,
/// or a login handed over after a lost GPU), and the choice turned into the session's config.</summary>
public static class LoginStart
{
    /// <summary>The sheet for a --login run; resumed: the choice handed over by the run that lost the GPU, which
    /// logs in without the form.</summary>
    public static LoginSheet Prepare(ClientOptions opt, HostConfig host, Action<string> log, out LoginChoice? resumed)
    {
        var settings = VrSettings.Load(VrSettings.PathIn(opt.BaseDir));
        PanelLook.InUse = PanelLook.Restore(settings.PanelLook);
        UiSize.Scale = settings.UiSize;
        var store = new LoginStore(opt.BaseDir);
        var sheet = new LoginSheet(store, () => PanelLook.InUse, log) { About = $"{host.Channel} {host.Version}" };
        resumed = null;
        if (opt.LoginResume && store.TakeOver() is { } back)
        {
            bool kept = store.Accounts().Any(a => a.Id == back.Account.Id && a.Remembered);
            resumed = new LoginChoice(back.Account, back.Hash, kept) { StartNow = opt.StartGiven ? opt.Start : null };
            sheet.Chosen = resumed;
            log("login: the same account again, after the GPU was lost");
        }
        else if (LoginRestart.Decode(opt.LoginAccount) is { } shown)
            sheet.Preselect(shown, opt.StartGiven ? opt.Start : null);
        return sheet;
    }

    /// <summary>The choice into the host's and the agent's config: the grid under its own key (its own cache), the
    /// account's own state dir (base/state/grid/first.last), the password hash in memory only. Returns the options
    /// the rest of the client reads (the grid's URI, the name, the start).</summary>
    public static ClientOptions Apply(LoginChoice choice, ClientOptions opt, HostConfig host, AgentConfig agent)
    {
        var a = choice.Account;
        var key = Grids.KeyOf(a.Grid);
        var uri = Grids.UriOf(a.Grid);
        host.Grids[key] = new GridConfig { LoginUri = uri };
        agent.Grid = key;
        agent.First = a.First;
        agent.Last = a.Last;
        agent.Password = choice.Hash;
        agent.PasswordFile = null;
        agent.Start = choice.StartNow ?? a.Start;
        agent.StateDir = Path.Combine(opt.BaseDir, "state", key, $"{a.First}.{a.Last}".ToLowerInvariant());
        return opt with { LoginUri = uri, First = a.First, Last = a.Last, Start = agent.Start, Password = null, PasswordFile = null };
    }
}
