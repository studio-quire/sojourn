using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// The person's experiences, as the viewer's Experiences floater has them (llfloaterexperiences.cpp): allowed and
/// blocked (the ExperiencePreferences capability), their own and those they administer, a search, an experience's
/// profile with its Allow, Block and Forget (llfloaterexperienceprofile.cpp), and the region's for estate owners.
/// Allowing one lets its scripts act without asking wherever it runs, so that waits for the person at the client's
/// confirmation gate; blocking and forgetting don't. Second Life only: OpenSim has no experiences, and every op says
/// so when the region lacks the capability. Claude Opus 5.5, 2026-10-27.
/// </summary>
public sealed class ExperienceOps
{
    readonly AgentSession _s;
    public ExperienceOps(AgentSession s) => _s = s;
    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    void Need(string cap)
    {
        if (C.Network.CurrentSim?.Caps?.CapabilityURI(cap) is null)
            throw new LeapError($"this region has no experiences ({cap} capability); Second Life's regions do, OpenSim's don't");
    }

    readonly Dictionary<UUID, ExperienceInfo> _known = new();

    /// <summary>Experiences' details by id, from the cache or one GetExperienceInfo ask.</summary>
    async Task<Dictionary<UUID, ExperienceInfo>> InfoAsync(IEnumerable<UUID> ids, CancellationToken ct)
    {
        var want = ids.Distinct().ToList();
        List<UUID> missing;
        lock (_known) missing = want.Where(id => !_known.ContainsKey(id)).ToList();
        if (missing.Count > 0 && C.Network.CurrentSim?.Caps?.CapabilityURI("GetExperienceInfo") is not null)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            foreach (var chunk in missing.Chunk(20))
                if (await C.Self.GetExperienceInfoAsync(chunk, cts.Token) is { } got)
                    lock (_known)
                        foreach (var x in got.Experiences)
                        {
                            if (x.PublicID != UUID.Zero) _known[x.PublicID] = x;
                            if (x.ExperienceID != UUID.Zero) _known[x.ExperienceID] = x;
                        }
        }
        lock (_known) return want.Where(_known.ContainsKey).ToDictionary(id => id, id => _known[id]);
    }

    static string Rating(int maturity) => maturity switch { >= 42 => "adult", >= 21 => "moderate", _ => "general" };

    JsonObject Json1(UUID id, ExperienceInfo? x)
    {
        var o = new JsonObject { ["id"] = id.ToString() };
        if (x is null) { o["name"] = null; return o; }
        o["name"] = x.Name;
        o["about"] = x.Description.Length > 300 ? x.Description[..300] + "..." : x.Description;
        o["rating"] = Rating(x.Maturity);
        o["owner"] = x.AgentID != UUID.Zero ? _s.Names.DisplayName(x.AgentID) ?? _s.Names.Legacy(x.AgentID) ?? x.AgentID.ToString() : null;
        if (x.GroupID != UUID.Zero) o["group"] = _s.State.Groups.TryGetValue(x.GroupID, out var g) ? g.Name : x.GroupID.ToString();
        if ((x.Flags & ExperienceFlags.Grid) != 0) o["grid_wide"] = true;
        if ((x.Flags & (ExperienceFlags.Disabled | ExperienceFlags.Suspended)) != 0) o["disabled"] = true;
        if (x.SLURL.Length > 0) o["slurl"] = x.SLURL;
        return o;
    }

    async Task<JsonArray> ListAsync(IEnumerable<UUID> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        var info = await InfoAsync(list, ct);
        return new JsonArray(list.Select(id => (JsonNode)Json1(id, info.GetValueOrDefault(id))).ToArray());
    }

    /// <summary>Allowed and blocked; with mine, those the person owns or contributes to and administers.</summary>
    public async Task<JsonObject> ListsAsync(JsonObject a, CancellationToken ct)
    {
        Need("ExperiencePreferences");
        var c = C;
        var prefs = await c.Self.GetExperiencePreferencesAsync(ct) ?? throw new LeapError("the region didn't give the experience lists");
        var o = new JsonObject { ["allowed"] = await ListAsync(prefs.Allowed, ct), ["blocked"] = await ListAsync(prefs.Blocked, ct) };
        if (a.Bool("mine", false))
        {
            if (await c.Self.GetAgentExperiencesAsync(ct) is { } mine) o["mine"] = await ListAsync(mine.ExperienceIDs, ct);
            if (await c.Self.GetAdminExperiencesAsync(ct) is { } admin) o["admin"] = await ListAsync(admin.ExperienceIDs, ct);
        }
        return o;
    }

    public async Task<JsonObject> ProfileAsync(JsonObject a, CancellationToken ct)
    {
        var id = a.Uuid("id") ?? throw new LeapError("need id (an experience)");
        Need("GetExperienceInfo");
        var info = await InfoAsync(new[] { id }, ct);
        if (!info.TryGetValue(id, out var x)) throw new LeapError("no such experience (or the grid didn't say)");
        var o = Json1(id, x);
        o["about"] = x.Description;
        o["permission"] = (await C.Self.GetExperiencePermissionAsync(id, ct))?.ToLowerInvariant() switch
        {
            "allow" => "allowed", "block" => "blocked", _ => "not set (it asks)",
        };
        return o;
    }

    public async Task<JsonObject> SearchAsync(JsonObject a, CancellationToken ct)
    {
        var text = (a.Str("text") ?? "").Trim();
        if (text.Length < 2) throw new LeapError("text: 2 characters or more");
        Need("FindExperienceByName");
        var got = await C.Self.FindExperienceByNameAsync(text, Math.Max(0, a.Int("page", 0)), 20, ct) ?? throw new LeapError("the search didn't answer");
        lock (_known) foreach (var x in got.Experiences) if (x.PublicID != UUID.Zero) _known[x.PublicID] = x;
        return new JsonObject { ["experiences"] = new JsonArray(got.Experiences.Select(x => (JsonNode)Json1(x.PublicID, x)).ToArray()) };
    }

    /// <summary>allow | block | forget for one experience (the profile's buttons).</summary>
    public async Task<JsonObject> SetAsync(JsonObject a, CancellationToken ct)
    {
        var id = a.Uuid("id") ?? throw new LeapError("need id (an experience)");
        var what = (a.Str("permission") ?? "").Trim().ToLowerInvariant();
        Need("ExperiencePreferences");
        bool ok = what switch
        {
            "allow" => await C.Self.SetExperiencePermissionAsync(id, "Allow", ct),
            "block" => await C.Self.SetExperiencePermissionAsync(id, "Block", ct),
            "forget" => await C.Self.ForgetExperiencePermissionAsync(id, ct),
            _ => throw new LeapError("permission: allow, block or forget"),
        };
        if (!ok) throw new LeapError("the region refused it");
        var name = (await InfoAsync(new[] { id }, ct)).GetValueOrDefault(id)?.Name;
        return new JsonObject { ["id"] = id.ToString(), ["name"] = name, ["permission"] = what };
    }

    /// <summary>The name of an experience if known (for the confirmation), without asking the grid.</summary>
    public string? KnownName(UUID id) { lock (_known) return _known.TryGetValue(id, out var x) ? x.Name : null; }

    public async Task<string?> NameAsync(UUID id, CancellationToken ct)
    {
        try { return (await InfoAsync(new[] { id }, ct)).GetValueOrDefault(id)?.Name; }
        catch { return null; }
    }

    /// <summary>The region's: allowed, trusted and blocked (estate owners and managers set these).</summary>
    public async Task<JsonObject> RegionAsync(CancellationToken ct)
    {
        Need("RegionExperiences");
        var r = await C.Self.GetRegionExperiencesAsync(ct) ?? throw new LeapError("the region didn't say (only its estate's owner and managers see these)");
        return new JsonObject
        {
            ["allowed"] = await ListAsync(r.Allowed, ct), ["trusted"] = await ListAsync(r.Trusted, ct), ["blocked"] = await ListAsync(r.Blocked, ct),
        };
    }
}
