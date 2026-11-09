using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Core.World;

namespace Sojourn.Access;

/// <summary>
/// Names a screen reader can say (gap 12, docs/access-app.md > People's names). Second Life's display names are often
/// decorative Unicode, read as nothing or nonsense, so names are said as Alt+0's People's names says (access_names,
/// NameCache.Style): in plain letters by default (PlainName), with the username, as written, or the username alone. The
/// session words them (NameCache's DisplayName and Complete, which the feed, the lists, notices and profiles use); what's
/// here is the rest: names from an op's result worded the same way, the display name as written in a profile, and the
/// scripted test's stand-in for the display-name service the local grid hasn't got.
/// </summary>
public sealed partial class MainForm
{
    void StartNames()
    {
        // SOJOURN_TEST_NAMES=1: SojournTestNames sets a display name in the session's names (tools/access.py's names step)
        if (Environment.GetEnvironmentVariable("SOJOURN_TEST_NAMES") == "1" && Host is { } h)
            h.Session.Api.Register(new NamesTestApi(h.Session));
    }

    /// <summary>A profile's line for the display name as written, when what's said for them isn't it: the one place it's
    /// read whole (to copy for someone, or to know a name's decorated); with only usernames said, the display name too.</summary>
    string? DisplayNameLine(UUID id)
    {
        if (Host?.Session.Names is not { } names || names.ChosenDisplay(id) is not { } written || NameCache.Style == NameStyle.Written) return null;
        var plain = PlainName.Of(written);
        if (NameCache.Style == NameStyle.Username)
            return plain.Length > 0 && plain != written ? $"Display name: {plain}, written {written}." : $"Display name: {written}.";
        return plain != written ? $"Display name as written: {written}." : null;
    }
}

/// <summary>SojournTestNames: the scripted test's stand-in for Second Life's display names (the local grid has none).
/// Registered only with SOJOURN_TEST_NAMES=1.</summary>
sealed class NamesTestApi : LeapApi
{
    public override string Name => "SojournTestNames";
    public override string Desc => "tests only: a display name set in the session's names, as the display-name service would";

    public NamesTestApi(AgentSession s) : base(s)
    {
        Op("display", "{id, name?} -> {said}: their display name set (none without name); said: what's said for them now", a =>
        {
            if (!UUID.TryParse(a.Str("id"), out var id) || !S.Names.Has(id)) throw new LeapError("id: someone whose name is known");
            S.Names.SetDisplay(id, a.Str("name"));
            return new JsonObject { ["said"] = S.Names.DisplayName(id) };
        });
    }
}
