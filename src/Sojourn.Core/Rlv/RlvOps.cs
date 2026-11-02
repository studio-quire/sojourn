using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Rlv;

/// <summary>
/// RLV's say over the agent API's ops, whoever calls them: the assistant, dev mode, a menu or a key press that runs an
/// op's handler directly (LeapApi wraps every op with it). Null: go ahead; else why not, naming the restriction and the
/// object holding it. Teleports by name, sitting and touching by the person's own hand, wearing and taking off are
/// checked lower down (AgentSession.TeleportAsync, SendSit, SendGrab, Wardrobe), so they hold for every path.
/// </summary>
public static class RlvOps
{
    static readonly HashSet<string> Rez = new() { "create", "rez", "duplicate" };
    static readonly HashSet<string> Edit = new()
    {
        "modify", "link", "unlink", "take", "delete", "returnObjects", "contents", "addToContents", "copyFromContents",
        "removeFromContents", "script", "select",
    };

    /// <summary>Ops that only read (people around, the inventory, where the person is): refused without a notice on the
    /// feed, since parts of the app read them by themselves every second or so (incognito's triggers, the situation).</summary>
    public static bool Quiet(string pump, string op) => (pump, op) switch
    {
        ("LLAgent", "getNearbyAvatarsList") or ("TesselAgent", "people") or ("SojournAvatars", "list") => true,
        ("LLInventory", _) or ("SojournAssistant", "findInventory") or ("SojournMap", "where") => true,
        ("TesselInventory", "library" or "readNotecard" or "readScript" or "landmark" or "gestures" or "readGesture") => true,
        _ => false,
    };

    public static string? Check(AgentSession s, string pump, string op, JsonObject a)
    {
        var r = s.Rlv;
        switch (pump, op)
        {
            case ("LLAgent", "requestSit"):
            {
                var id = a.Uuid("obj_uuid") ?? UUID.Zero;
                return r.CanSit(id, id == UUID.Zero ? null : s.RlvObject(id)?.Position);
            }
            case ("SojournAo", "poseStand"):
                return r.CanSit(UUID.Zero, null);
            case ("LLAgent", "requestStand"):
            case ("LLAgent", "stopAnimating"):
            case ("SojournAo", "poseStandOff"):
                return r.CanStand();
            case ("LLAgent", "requestTouch"):
                return a.Uuid("obj_uuid") is { } t ? s.RlvTouch(t) : r.Check("interact", "touching");
            case ("LLAgent", "startAutoPilot"):
                return a.Bool("allow_flying", false) ? r.CanFly() : r.CanStand();
            case ("LLAgent", "getNearbyAvatarsList"):
            case ("TesselAgent", "people"):
            case ("SojournAvatars", "list"):
                return r.Check("shownearby", "seeing who's nearby");
            case ("TesselAgent", "goHome"):
                return r.CanTeleport(null, home: true);
            case ("TesselAgent", "teleportLandmark"):
                return r.CanTeleport(null, landmark: true);
            case ("TesselAgent", "offerTeleport"):
                return r.Check("showloc", "offering teleports (it would show where you are)");
            case ("TesselAgent", "move"):
                return a.Num("up") is > 0 && !a.Bool("fly", false) ? r.CanJump() : a.Bool("fly", false) ? r.CanFly() : null;
            case ("TesselAgent", "pay"):
                return r.Check("pay", "paying");
            case ("TesselAgent", "give"):
                return r.CanGive(a.Uuid("to") ?? UUID.Zero);
            case ("TesselAgent", "activateGroup"):
                return r.CanSetGroup();
            case ("TesselBuild", "buy"):
                return r.Check("buy", "buying");
            case ("LLGesture", "startGesture"):
                return r.CanGesture();
            case ("TesselIM", "send"):
            {
                if ((a.Uuid("to") ?? a.Uuid("group_id")) is not { } to) return null;
                // a new conversation under @startim is refused; under @sendim the words are kept back and they're told,
                // as RLVa sends its "IM blocked by sender's viewer" in their place
                if (r.CanSendIm(to, !s.State.Ims.Values.Any(x => x.OtherId == to), s.RlvAvatarAt) is { } why && why.Contains("@startim")) return why;
                if (r.CanSendIm(to, false, s.RlvAvatarAt) is { } held)
                {
                    s.RlvRefused(held);
                    a["message"] = RlvEngine.BlockedSendIm;
                }
                return null;
            }
            case ("TesselIM", "conference"):
                foreach (var id in a.UuidList("ids"))
                    if (r.CanSendIm(id, true, s.RlvAvatarAt) is { } why) return why;
                return null;
            case ("TesselInventory", "readNotecard"):
                return r.Check("viewnote", "reading notecards");
            case ("TesselInventory", "readScript"):
                return r.Check("viewscript", "reading scripts");
            case ("LLInventory", _):
            case ("TesselInventory", _):
            case ("SojournAssistant", "findInventory"):
                return r.Check("showinv", "looking through the inventory");
            case ("SojournMap", "where"):
                return r.Check("showloc", "knowing where you are");
            case ("SojournMap", _):
                return r.Check("showworldmap", "the world map");
            case ("TesselBuild", var b) when Rez.Contains(b):
                return r.CanRez();
            case ("TesselBuild", var b) when Edit.Contains(b):
            {
                var id = a.Uuid("id") ?? a.Uuid("obj_uuid") ?? (a.UuidList("ids") is { Count: > 0 } ids ? ids[0] : UUID.Zero);
                if (id == UUID.Zero) return r.Check("edit", "editing");
                var o = s.RlvObject(id);
                return r.CanEdit(o?.Root ?? id, o?.Attachment ?? false);
            }
            case ("SojournBuild", _):
                return r.Check("edit", "editing");
            default:
                return null;
        }
    }
}
