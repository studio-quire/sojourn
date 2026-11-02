namespace Sojourn.Session.Login;

/// <summary>A failed login in plain words: the grid's reason key (or ours) as one sentence a person can act on, and
/// the grid's own message beside it when it says more. Keys as the grids send them (LL's login.cgi, OpenSim). Ours in
/// the person's language; the grid's own words as they came.</summary>
public static class LoginWords
{
    public static (string Plain, string? Detail) For(string? key, string? message)
    {
        var msg = Clean(message);
        string? ours = key switch
        {
            "key" when Mentions(msg, "connecting to the desired location", "region") => "The region you'd start in isn't answering. Try another start, or again in a minute.",
            "key" => "That username and password don't match.",
            "presence" => "You're still logged in from before. Try again in a minute.",
            "update" or "optional" => "This grid wants a newer viewer than this one.",
            "tos" => "You declined the Terms of Service.",
            "critical" => "You declined the grid's message.",
            "mfa_challenge" => "Login needs the code from your authenticator.",
            "suspended" or "disabled" => "This account can't log in right now.",
            "no region" => "Logged in, but no region answered. Try again.",
            "god" or "ban" => "This account can't log in to this grid.",
            "" or null => "Couldn't reach the grid.",
            _ => msg is { Length: > 0 } ? null : "The grid didn't let you in.",
        };
        string plain = ours ?? msg!;
        // the grid's own words under ours, unless they say no more than ours
        // (a plain "key" refusal is LL's long "check your username and password": ours says it)
        bool same = key == "key" && plain.StartsWith("That username", StringComparison.Ordinal) || key is "tos" or "critical";
        string? detail = msg is { Length: > 0 } && !string.Equals(msg, plain, StringComparison.OrdinalIgnoreCase) && !same ? msg : null;
        return (ours != null ? Lang.T(ours) : plain, detail);
    }

    static bool Mentions(string? text, params string[] words) => text != null && words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    static string? Clean(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var t = System.Text.RegularExpressions.Regex.Replace(message, "<[^>]+>", " ");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
        return t.Length > 400 ? t[..400] + "..." : t;
    }
}
