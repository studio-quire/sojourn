namespace Sojourn.Core.Agents;

/// <summary>
/// The maturity preference (the viewer's PreferredMaturity): the highest rating of region the account goes into,
/// General, Moderate or Adult, kept on the grid with the account. Login says what it is (agent_region_access) and the
/// most the account may have (agent_access_max); changing it is the viewer's UpdateAgentInformation request, so it holds
/// for every viewer the person uses, as the viewer's preference does.
/// </summary>
public sealed partial class AgentSession
{
    public static readonly string[] Maturities = ["general", "moderate", "adult"];

    /// <summary>The preference now (general, moderate, adult), null before login says.</summary>
    public string? Maturity { get; private set; }

    /// <summary>The most this account may choose.</summary>
    public string MaturityMax { get; private set; } = "general";

    static string? FromAccess(string? code) => code?.Trim().ToUpperInvariant() switch
    {
        "PG" or "G" => "general",
        "M" or "MATURE" => "moderate",
        "A" or "ADULT" => "adult",
        _ => null,
    };

    static string ToAccess(string maturity) => maturity switch { "adult" => "A", "moderate" => "M", _ => "PG" };

    void ReadMaturity(LibreMetaverse.GridClient c)
    {
        var login = c.Network.LoginResponseData;
        MaturityMax = FromAccess(login?.AgentAccessMax) ?? FromAccess(login?.AgentAccess) ?? "general";
        Maturity = FromAccess(login?.AgentRegionAccess) ?? FromAccess(login?.AgentAccess);
    }

    /// <summary>Change the preference on the grid. Returns why not, or null once the grid has it.</summary>
    public async Task<string?> SetMaturityAsync(string maturity, CancellationToken ct)
    {
        int want = Array.IndexOf(Maturities, maturity);
        if (want < 0) return $"maturity is one of {string.Join(", ", Maturities)}";
        if (want > Array.IndexOf(Maturities, MaturityMax))
            return $"this account can go up to {MaturityMax} (the account's own age and settings on the grid's website decide that)";
        if (Client is not { } c || !c.Network.Connected) return "not logged in";
        if (Maturity == maturity) return null;
        try
        {
            var got = await c.Self.SetAgentAccessAsync(ToAccess(maturity), ct);
            if (got is not { Success: true }) return "the grid didn't take the change";
            Maturity = FromAccess(got.NewLevel) ?? maturity;
            return Maturity == maturity ? null : $"the grid set it to {Maturity}";
        }
        catch (Exception e) { return $"the grid didn't answer ({e.Message})"; }
    }
}
