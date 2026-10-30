using System.Text.Json.Nodes;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Session.Release;

/// <summary>
/// SojournRelease: what the TPV policy asks a viewer to say about itself (1.g's version display, 1.c's disclaimer, 3.b's
/// source), for the assistant to tell the person and for an About sheet to show. Nothing here touches the login or
/// credentials (docs/tpv-policy.md).
/// </summary>
public sealed class ReleaseApi : LeapApi
{
    /// <summary>The disclaimer TPVs show (TPV Policy 1.c).</summary>
    public const string Disclaimer = "This software is not provided or supported by Linden Lab, the makers of Second Life.";
    public const string Licence = "Parts of Sojourn's renderer are ported from the Second Life viewer's source (LGPL 2.1, "
        + "Copyright Linden Research, Inc.); it uses LibreMetaverse (BSD).";

    public override string Name => "SojournRelease";
    public override string Desc => "about Sojourn itself: its name, the channel and version it logs in with, the disclaimer and licence";
    public override bool NeedsSession => false;

    public ReleaseApi(AgentSession s) : base(s)
    {
        Op("about", "-> {name, channel, version, disclaimer, licence}: what the About window shows; the channel and version "
            + "are the ones sent at login", _ => new JsonObject
        {
            ["name"] = "Sojourn",
            ["channel"] = s.Host.Channel,
            ["version"] = s.Host.Version,
            ["disclaimer"] = Disclaimer,
            ["licence"] = Licence,
        });
    }
}
