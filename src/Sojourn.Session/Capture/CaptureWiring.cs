using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Sojourn.Session.Assistant;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Render.Realtime;

namespace Sojourn.Session.Capture;

/// <summary>
/// The rest of snapshots (batch B8), kept out of Program.cs: pictures bigger than the eye or from a camera of their
/// own and 360s (Stills), and a picture sent on (Sharing): to the inventory, by email, to the profile feed. Their ops
/// are SojournCapture's; what spends or speaks for the person waits for their press (Confirmations.Cases), and what
/// would fail is refused before they're asked (Confirmations.Refusals), with the picture each will send pinned first.
/// Claude Opus 5.5, 2026-10-29.
/// </summary>
public static class CaptureWiring
{
    public static Stills Start(AgentSession s, AssistantHost assistant, FrameRenderer renderer, CaptureApi capture, bool uploadsCost, ILogger log)
    {
        var stills = new Stills(s, renderer);
        capture.Stills = stills;
        var share = new Sharing(s, () => capture.LastPicture);

        capture.Add("panorama", "{quality=high (preview|medium|high|max: faces of 128|512|1024|2048), width=4096 (1024-8192; height half), name?, "
            + "camera? (as snapshot's; the person's eyes by default), hide_avatars?, settle=8 (seconds to let what's around load)} -> {path, width, "
            + "height, face, heading, at, waited_seconds, still_loading}: a 360 as the viewer takes it (six faces stitched, the interest list in "
            + "360 mode meanwhile; the view never moves, a short hitch): an equirectangular JPEG with the metadata 360 viewers read, into "
            + "Pictures\\Sojourn; its middle is the way the camera faces (allow settle + 15 s)", async (a, ct) =>
            {
                var r = await stills.PanoramaAsync(a, a.Str("name") ?? "", ct);
                capture.LastPicture = r.Str("path");
                return r;
            });
        capture.Add("toInventory", "{path? (a picture in Pictures\\Sojourn; the last one taken if not given), size? (WxH: 128x128 ... 2048x2048; each "
            + "side to a power of two, as the viewer), name?, desc?} -> {item_id, asset_id, name, folder, width, height, charged}: the viewer's Save to "
            + "Inventory, a texture in the Photo Album (an upload: L$ on Second Life, waits for the person's press there)", async (a, ct) => await share.ToInventoryAsync(a, ct));
        capture.Add("postcard", "{to (email address, or several), subject?, message?, path? (as toInventory's)} -> {sent, to, subject}: the "
            + "viewer's Send via E-mail, from the person's account; waits for their press (shown whole); Second Life's regions only", async (a, ct) => await share.PostcardAsync(a, ct));
        capture.Add("toProfile", "{path?, caption?, add_location?} -> the viewer's Share to Profile Feed (my.secondlife.com): not available yet; "
            + "refused with why", (_, _) => throw new LeapError(share.ProfileRefusal()));

        var c = assistant.Confirmations;
        c.Refusals.Add((pump, op, a) => pump == "SojournCapture" ? Refusal(share, op, a) : null);
        c.Cases.Add(new Confirmations.Case(
            (pump, op, a) => pump != "SojournCapture" ? null : op switch
            {
                "toInventory" => uploadsCost ? "spend" : null,
                "postcard" => "words",
                _ => null,
            },
            (_, op, a) => Task.FromResult(Describe(s, share, op, a))));
        log.LogDebug("capture wired");
        return stills;
    }

    /// <summary>Before anything waits for the person: the picture each will send pinned (a later picture can't take
    /// its place), and what would fail refused.</summary>
    static string? Refusal(Sharing share, string op, JsonObject a)
    {
        try
        {
            switch (op)
            {
                case "toInventory":
                    a["path"] = share.PlanInventory(a).Path;
                    return null;
                case "postcard":
                    if (share.PostcardRefusal() is { } no) return no;
                    a["path"] = share.PlanPostcard(a).Path;
                    return null;
                case "toProfile":
                    return share.ProfileRefusal();
                default:
                    return null;
            }
        }
        catch (LeapError e) { return $"{e.Message}; nothing was done"; }
        catch (NotConnected) { return null; }
    }

    static (string Title, string Detail) Describe(AgentSession s, Sharing share, string op, JsonObject a)
    {
        try
        {
            switch (op)
            {
                case "toInventory":
                {
                    var p = share.PlanInventory(a);
                    var balance = s.BalanceKnown && s.Client is { } c ? " " + Lang.F("Your balance: L${0}.", c.Self.Balance) : "";
                    return (Lang.T("Save a picture to your inventory"),
                        Lang.F("\"{0}\", {1}x{2}, from {3}, into Photo Album. L${4} on this grid.", p.Name, p.Width, p.Height, Path.GetFileName(p.Path), p.Cost) + balance);
                }
                case "postcard":
                {
                    var p = share.PlanPostcard(a);
                    return (Lang.F("Email a picture to {0}", string.Join(", ", p.To)),
                        Lang.F("Subject \"{0}\". Message \"{1}\". The picture {2} ({3}x{4}), sent by Second Life from your account's email address.",
                            p.Subject, p.Message, Path.GetFileName(p.Path), p.Width, p.Height));
                }
            }
        }
        catch (Exception e) when (e is LeapError or NotConnected) { return ($"SojournCapture {op}", Lang.F("{0}: this will likely fail.", e.Message)); }
        return ($"SojournCapture {op}", "");
    }
}
