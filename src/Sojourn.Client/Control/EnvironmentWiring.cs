using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Sojourn.Render.World;

namespace Sojourn.Client.Control;

/// <summary>
/// World rendering's hookup to the client (batch B3a), kept out of Program.cs: SojournEnvironment (the sky in use and
/// the person's own environment, Control/EnvironmentApi.cs) and the viewer's UI.call World.EnvSettings through it;
/// ban lines fed from the regions' collision reports and refusals, property lines, the interest list's 360 mode; their
/// settings on SojournSettings (clouds, time_of_day, ban_lines, property_lines, interest_360). The menu's rows for them
/// are SettingsMenu's (view > sky, time of day, parcel lines). Claude Opus 5.5, 2026-10-28.
/// </summary>
public sealed class EnvironmentWiring
{
    readonly AgentSession _s;
    readonly FrameRenderer _renderer;
    readonly VrSettings _st;
    readonly ILogger _log;

    public EnvironmentApi Api { get; }

    EnvironmentWiring(AgentSession s, FrameRenderer renderer, VrSettings st, Action save, ILogger log)
    {
        _s = s;
        _renderer = renderer;
        _st = st;
        _log = log;
        Api = new EnvironmentApi(s, renderer, sky => { st.Sky = sky; save(); });
    }

    /// <param name="save">Keeps the settings (the `sky` preset an op chose is kept, as the menu's is).</param>
    public static EnvironmentWiring Start(AgentSession session, FrameRenderer renderer, VrSettings st, Action save, ILogger log)
    {
        var w = new EnvironmentWiring(session, renderer, st, save, log);
        session.Api.Register(w.Api);
        Sojourn.Core.Leap.UIApi.EnvSettings = w.Api.EnvSettings;
        var hooked = new System.Runtime.CompilerServices.ConditionalWeakTable<GridClient, object>();
        void Hook(GridClient c)
        {
            if (hooked.TryGetValue(c, out _)) return;
            hooked.Add(c, new object());
            c.Parcels.ParcelProperties += (_, e) => w.Guard(() => w.OnParcel(e));
            c.Self.AlertMessage += (_, e) => w.Guard(() => w.OnAlert(e));
            if (st.Interest360) _ = c.InterestList.SetModeAsync(LibreMetaverse.Messages.Linden.InterestListMode.Panoramic360);
        }
        lock (AgentSession.ClientHooks) AgentSession.ClientHooks.Add(Hook);
        if (session.Client is { } now) Hook(now);
        w.Apply();
        if (System.Environment.GetEnvironmentVariable("SOJOURN_BAN_TEST") is { Length: > 0 } test) w.TestBans(test);
        return w;
    }

    Timer? _test;

    /// <summary>Tests (SOJOURN_BAN_TEST=x0,y0,x1,y1, region metres): a ban wall around that rectangle of the agent's
    /// region, reported every half second as a region near it does (the local grid has no ban lines of its own).</summary>
    void TestBans(string spec)
    {
        var n = spec.Split(',').Select(x => (int)(float.Parse(x, System.Globalization.CultureInfo.InvariantCulture) / 4)).ToArray();
        _log.LogInformation("ban lines test: cells {X0},{Y0} to {X1},{Y1}", n[0], n[1], n[2], n[3]);
        _test = new Timer(_ =>
        {
            if (_s.Client?.Network.CurrentSim is not { } sim) return;
            int per = (int)(sim.SizeX / 4);
            var bitmap = new byte[per * per / 8];
            for (int y = n[1]; y < n[3]; y++)
                for (int x = n[0]; x < n[2]; x++)
                    bitmap[(x + y * per) / 8] |= (byte)(1 << ((x + y * per) % 8));
            _renderer.ParcelLines.Collided(sim.Handle, bitmap, ParcelLines.CollisionBanned, false);
            _renderer.ParcelLines.ShowFor(1);
        }, null, 1000, 500);
    }

    void Guard(Action a)
    {
        try { a(); }
        catch (Exception e) { _log.LogWarning(e, "ban lines"); }
    }

    void Apply()
    {
        var lines = _renderer.ParcelLines;
        lines.ShowProperty = _st.PropertyLines;
        lines.Bans = _st.BanLines switch { "hide" => ParcelLines.BanMode.Hide, "proximity" => ParcelLines.BanMode.Proximity, _ => ParcelLines.BanMode.Collision };
    }

    void OnParcel(ParcelPropertiesEventArgs e)
    {
        if (e.SequenceID is not (ParcelLines.CollisionBanned or ParcelLines.CollisionNotOnList or ParcelLines.CollisionNotInGroup)) return;
        if (e.Parcel.Bitmap is not { Length: > 0 } bitmap) return;
        _renderer.ParcelLines.Collided(e.Simulator.Handle, bitmap, e.SequenceID, (e.Parcel.Flags & ParcelFlags.UsePassList) != 0);
    }

    // process_alert_message / process_alert_core: "Cannot enter parcel" in a plain alert, and the access-blocked notices
    void OnAlert(AlertMessageEventArgs e)
    {
        if ((e.Message ?? "").Contains("Cannot enter parcel", StringComparison.Ordinal)
            || e.NotificationId is "RegionEntryAccessBlocked" or "LandClaimAccessBlocked" or "LandBuyAccessBlocked")
            _renderer.ParcelLines.Refused();
    }

    async Task SetInterest(bool on)
    {
        _st.Interest360 = on;
        if (_s.Client is { } c)
            await c.InterestList.SetModeAsync(on ? LibreMetaverse.Messages.Linden.InterestListMode.Panoramic360 : LibreMetaverse.Messages.Linden.InterestListMode.Default);
        _log.LogInformation("interest list: {Mode}", on ? "360" : "default");
    }

    static readonly string[] BanNames = { "hide", "collision", "proximity" };

    /// <summary>The settings (SojournSettings).</summary>
    public void Settle(SettingsApi api)
    {
        api.Add("clouds", new("view", "the clouds moving, or paused (World > Environment > Pause Clouds; not kept between sessions, as the viewer's)",
            "moving, paused", () => _renderer.Personal.CloudsPaused ? "paused" : "moving",
            SettingsApi.Choice("clouds", new[] { "moving", "paused" }, i => _renderer.Personal.CloudsPaused = i == 1)));
        api.Add("time_of_day", new("view", "the time of day the sun stands at, just for them, held still (SojournEnvironment time: over "
            + "a fixed sky only the sun and moon move; over a day cycle it's the day at that time); not kept between sessions",
            "H:MM or hours 0-24 (6 sunrise, 12 noon, 18 sunset), +h or -h later or earlier, region",
            () => Api.TimeNow is { } t ? JsonValue.Create(t) : null,
            v => Api.SetTimeOfDay(v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : v.ToJsonString())));
        api.Add("ban_lines", new("view", "the walls at parcels you can't enter: collision (the viewer's default: for 10 s after the region "
            + "turns you back), proximity (while you're near them) or hide", "hide, collision, proximity",
            () => _st.BanLines, SettingsApi.Choice("ban_lines", BanNames, i => { _st.BanLines = BanNames[i]; Apply(); })));
        api.Add("property_lines", new("view", "lines on the ground along parcel edges in their owners' colours (yours green, your group's "
            + "teal, others' red, for sale orange, auction purple), as the viewer's View > Property Lines; off by default", "on, off",
            () => _st.PropertyLines ? "on" : "off", SettingsApi.Switch("property_lines", on => { _st.PropertyLines = on; Apply(); })));
        api.Add("interest_360", new("view", "ask the regions for everything around you rather than what's in front first (their interest "
            + "list's 360 mode, made for the viewer's 360 snapshots): things behind you may arrive sooner after turning, at more traffic; "
            + "off by default", "on, off", () => _st.Interest360 ? "on" : "off",
            SettingsApi.Switch("interest_360", on => _ = SetInterest(on))));
    }
}
