using System.Text.Json.Nodes;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Client.Social;

/// <summary>
/// SojournFeed: the chat feed's waiting notices for callers of the agent API. The assistant can bring them all back onto
/// the feed for the person to see and answer by hand (as the forearm's bell does); answering them itself is LLNotifications'
/// respond, through the confirmation gate. keys is for scripted tests: where the notices' keys are on the desktop.
/// </summary>
public sealed class FeedApi : LeapApi
{
    readonly SocialView _view;

    public override string Name => "SojournFeed";
    public override string Desc => "the chat feed's waiting notices: how many, and bringing them back onto the feed for the person";
    public override bool NeedsSession => false;

    public FeedApi(AgentSession s, SocialView view) : base(s)
    {
        _view = view;
        Op("notices", "{show?: true} -> {waiting, folded}: notices waiting for an answer, and how many aren't on the feed now; show "
            + "puts them all back on the feed (for two minutes), where the person can answer or put each away", a =>
        {
            if (a.Bool("show", false)) _view.Recall();
            var (waiting, folded) = _view.Waiting;
            return new JsonObject { ["waiting"] = waiting, ["folded"] = a.Bool("show", false) ? 0 : folded };
        });
        Op("lines", "-> {lines: [{seq, kind, name, text, alert?, translation?, notice?}]}: what's on the feed now, oldest first (tests; "
            + "the conversation itself is recent_chat's)", _ => new JsonObject
        {
            ["lines"] = new JsonArray(_view.Feed.Snapshot().Entries.Select(e =>
            {
                var o = new JsonObject { ["seq"] = e.Seq, ["kind"] = e.Kind.ToString().ToLowerInvariant(), ["name"] = e.Name, ["text"] = e.Text };
                if (e.Alert) o["alert"] = true;
                if (e.Translation != null) o["translation"] = e.Translation;
                if (e.NoticeId != Guid.Empty) o["notice"] = e.NoticeId.ToString();
                return (JsonNode)o;
            }).ToArray()),
        });
        Op("keys", "-> {keys: [{kind, notice, button?, x, y}]}: the notices' keys (and lines' links) on the desktop's feed, in the window's pixels (tests)", _ =>
            new JsonObject
            {
                ["keys"] = new JsonArray(_view.DesktopKeys().Select(k => (JsonNode)new JsonObject
                {
                    ["kind"] = k.Key.Kind.ToString().ToLowerInvariant(), ["notice"] = k.Key.Notice.ToString(), ["button"] = k.Key.Button,
                    ["x"] = Math.Round(k.Pixels.MidX), ["y"] = Math.Round(k.Pixels.MidY),
                }).ToArray()),
            });
    }
}
