using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sojourn.Session.Assistant;
using Sojourn.Core.Agents;

namespace Sojourn.Session.Private;

/// <summary>
/// Incognito's client half (docs/assistant.md > Incognito): the allowlist on the session's gate, the SojournPrivate
/// ops and the situation, the sidecar told at once (a `sojourn` event {what: private, on, why, minutes, sees}), and the
/// console and session log paused while it lasts.
/// </summary>
public sealed class PrivateWiring
{
    public PrivateMode Mode { get; }
    public PrivateApi Api { get; }
    public Situation Situation { get; }

    PrivateWiring(PrivateMode mode, PrivateApi api, Situation situation) => (Mode, Api, Situation) = (mode, api, situation);

    public static PrivateWiring Start(AgentSession s, AssistantHost assistant, Social.SocialFeed feed, ILogger log)
    {
        Allowlist.Fill(s.Private);
        var situation = new Situation(s)
        {
            Pointer = () => assistant.Pointer.TargetJson(),
            ConfirmationWaiting = () => assistant.Confirmations.Pending,
            TypingNearby = feed.TypingNearby,
        };
        var api = new PrivateApi(s, situation);
        s.Api.Register(api);
        SessionLog.Paused = () => s.Private.On;
        s.Private.Changed += (on, why, minutes) =>
        {
            SessionLog.Note(on ? $"incognito on ({s.Private.WhyForCallers}): the console and this log are paused until it's off"
                               : $"incognito off after {minutes:0} min; nothing from that time was logged");
            s.Events.Log("sojourn", new JsonObject { ["what"] = "private", ["on"] = on, ["why"] = on ? s.Private.WhyForCallers : why, ["minutes"] = Math.Round(minutes, 1), ["sees"] = s.Private.Sees });
        };
        // "let it see" changed mid-spell: the sidecar's sessions switch (a nudge, as above; its status decides)
        s.Private.SeesChanged += sees =>
        {
            if (s.Private.On) s.Events.Log("sojourn", new JsonObject { ["what"] = "private", ["on"] = true, ["sees"] = sees });
        };
        return new PrivateWiring(s.Private, api, situation);
    }
}
