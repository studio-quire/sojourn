using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Core.Rlv;
using Sojourn.Session.Assistant;
using Sojourn.Session.Control;

namespace Sojourn.Session.Rlv;

/// <summary>
/// RLV for the apps (docs/rlv.md): the <c>rlv</c> setting (off by default, from the next login, as Firestorm's RLVa),
/// SojournRlv for the assistant and any caller (what's restricted and by what), and the menu's rows (the safety
/// section: the switch, and what holds the person while anything does). The engine is Core's Rlv/; the session's part
/// AgentSession.Rlv.cs.
/// </summary>
public sealed class RlvWiring
{
    readonly AgentSession _s;
    readonly VrSettings _st;

    /// <summary>The app's wiring, for the menu's safety section (one session per app).</summary>
    public static RlvWiring? Current { get; private set; }

    RlvWiring(AgentSession s, VrSettings st) => (_s, _st) = (s, st);

    public static RlvWiring Start(AgentSession s, VrSettings st, SettingsApi settings, ILogger log)
    {
        var w = new RlvWiring(s, st);
        Current = w;
        s.RlvWanted = st.Rlv;
        s.Api.Register(new RlvApi(s));
        settings.Add("rlv", new("safety", "RLV/RLVa (off by default, as in Firestorm): scripts in what the person wears, and in-world objects "
            + "through a relay they wear, may restrict them (no teleporting, no standing up, chat hidden, a blindfold, locked "
            + "attachments) and wear or take off things from their #RLV folder; restrictions hold for every caller, the assistant "
            + "too, but never for the way out. Takes effect at the next login. SojournRlv status: what holds the person now",
            "on, off", () => st.Rlv ? "on" : "off", SettingsApi.Switch("rlv", on => w.Set(on))));
        return w;
    }

    void Set(bool on)
    {
        _st.Rlv = on;
        _s.RlvWanted = on;
        if (on != _s.RlvActive)
            _s.Notify.Add("RlvNextLogin", "notifytip", on ? "RLV will be on from the next login." : "RLV will be off from the next login.", Notifications.None);
    }

    /// <summary>The safety section's rows: the switch (with "next login" while it waits), and while anything holds the
    /// person, how many things and a press that lists them on the feed.</summary>
    public List<(MenuRow Row, Action<int> Act)> Rows()
    {
        var rows = new List<(MenuRow, Action<int>)>
        {
            (MenuRow.Switch("RLV", _st.Rlv, note: _st.Rlv != _s.RlvActive ? "from the next login" : null), _ => Set(!_st.Rlv)),
        };
        if (_s.RlvActive && _s.Rlv.Holders() is { Count: > 0 } held)
            rows.Add((new MenuRow("RLV restrictions", $"{held.Sum(h => h.Restrictions.Count)}", false), _ => Tell()));
        return rows;
    }

    void Tell()
    {
        var lines = _s.Rlv.Holders().Select(h => $"{(h.Name.Length > 0 ? h.Name : "an object")}"
            + (h.AttachPoint != 0 ? $" ({RlvNames.PointName(h.AttachPoint)})" : "") + ": " + string.Join(", ", h.Restrictions.Select(r => "@" + r)));
        _s.Notify.Add("RlvStatus", "notify", "RLV holds you by:\n" + string.Join("\n", lines), Notifications.None);
    }
}

/// <summary>SojournRlv: what RLV restricts now and by what, for the assistant (so it can say why something didn't happen)
/// and any caller. It issues nothing: restrictions come only from the person's own objects.</summary>
public sealed class RlvApi : LeapApi
{
    public override string Name => "SojournRlv";
    public override string Desc => "RLV (Restrained Love): whether it's on, and what restricts the person now and by which object";

    public RlvApi(AgentSession s) : base(s)
    {
        Op("status", "-> {on, next_login, held: [{object, name, worn_on?, restrictions: [...]}], can: {teleport, sit, stand, chat, im, fly, touch_far, "
            + "see_names, see_location, inventory, map}, vision: {spheres, overlay}}: what holds the person (each refusal elsewhere says which "
            + "restriction and object too); the way out always works and is never restricted", _ => Status());
    }

    JsonNode Status()
    {
        var r = S.Rlv;
        var o = new JsonObject { ["on"] = S.RlvActive, ["next_login"] = S.RlvWanted };
        if (!S.RlvActive) return o;
        var held = new JsonArray();
        foreach (var h in r.Holders())
        {
            var e = new JsonObject { ["object"] = h.Id.ToString(), ["name"] = h.Name, ["restrictions"] = Json.Strings(h.Restrictions) };
            if (h.AttachPoint != 0) e["worn_on"] = RlvNames.PointName(h.AttachPoint);
            held.Add(e);
        }
        o["held"] = held;
        JsonNode Yes(string? why) => why == null ? JsonValue.Create(true) : JsonValue.Create(why);
        o["can"] = new JsonObject
        {
            ["teleport"] = Yes(r.CanTeleport(null)),
            ["teleport_landmark"] = Yes(r.CanTeleport(null, landmark: true)),
            ["sit"] = Yes(r.CanSit(UUID.Zero, null)),
            ["stand"] = Yes(r.CanStand()),
            ["chat"] = Yes(r.ChatHeld()),
            ["im"] = Yes(r.Check("sendim", "sending IMs")),
            ["fly"] = Yes(r.CanFly()),
            ["touch"] = Yes(r.Check("touchall", "touching") ?? r.Check("interact", "touching")),
            ["see_names"] = Yes(r.NamesHidden ? "RLV: names of people nearby are hidden (@shownames)" : null),
            ["see_location"] = Yes(r.LocationHidden ? "RLV: where you are is hidden (@showloc)" : null),
            ["inventory"] = Yes(r.Check("showinv", "looking through the inventory")),
            ["map"] = Yes(r.Check("showworldmap", "the world map")),
        };
        o["vision"] = new JsonObject { ["spheres"] = r.Spheres().Count, ["overlay"] = r.Overlay() != null };
        return o;
    }
}
