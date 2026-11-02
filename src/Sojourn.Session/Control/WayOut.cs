using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;
using static Sojourn.Core.Agents.TeleportWatch;

namespace Sojourn.Session.Control;

/// <summary>
/// The person's own way out of a bad moment (caged, pinned to a seat, flooded with particles or noise, harassed),
/// never waiting on the assistant: stand up from whatever holds them, then teleport home, as the viewer's Stand and
/// Teleport Home do. In the headset it's the left menu button held for a second (a short press still opens the
/// settings, on release); on the desktop Ctrl+Shift+H, the viewer's Teleport Home. Like the menu and confirmations, it's
/// one of the person's direct actions (docs/vr.md > Controls); the assistant's own goHome is a request like any other.
/// What came of it is read from the protocol (TeleportWatch): if the grid refuses home or doesn't answer, the person
/// hears why in plain words and goes to the latest other region in their teleport history instead; if the grid sent
/// them home and the region there never answered, they hear that while the session logs in again (where the grid has
/// put them). LibreMetaverse's GoHomeAsync, used before, never heard a refusal and refused itself while an earlier
/// teleport looked under way (the main grid, 2026-10-28: "the region didn't send us home", twice).
/// </summary>
public sealed class WayOut
{
    readonly AgentSession _s;
    readonly ILogger _log;
    int _running;
    long _startedAt;

    /// <summary>How long the menu button is held for it (seconds).</summary>
    public const double HoldSeconds = 0.8;

    public WayOut(AgentSession s, ILogger log) => (_s, _log) = (s, log);

    public void Go(string how)
    {
        if (_s.Client is not { Network.Connected: true } c) return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            // a second press while the first is still on its way (a home teleport can take a while on the main grid)
            _log.LogInformation("way out ({How}): already on the way ({Secs:F0} s)", how, (Environment.TickCount64 - _startedAt) / 1000.0);
            _s.Notify.Add("WayOut", "notify", "Still on the way home", Notifications.None);
            return;
        }
        _startedAt = Environment.TickCount64;
        // RLV doesn't hold the way out (docs/rlv.md > The way out): it stands and goes home whatever @unsit or @tploc say,
        // @standtp doesn't take them back, and for half a minute no script's teleport or sit does either
        _s.RlvWayOut();
        bool seated = c.Self.SittingOn != 0;
        // a script keeping the controls (a stuck vehicle, a weapon) lets them go too: the viewer's Release Keys
        if (_s.ControlsTaken != LibreMetaverse.ScriptControlChange.None) _s.Safety.ReleaseControls();
        if (seated) c.Self.Stand();
        _log.LogInformation("way out ({How}): {Stood}going home", how, seated ? "stood up, " : "");
        _s.Notify.Add("WayOut", "notify", seated ? "Standing up and going home" : "Going home", Notifications.None);
        _ = Task.Run(async () =>
        {
            // (what held them, for the log: off the press's own thread, after the stand has gone out)
            try { if (_s.RlvActive && _s.Rlv.Any) _log.LogInformation("way out under RLV: {Held}", string.Join("; ", _s.Rlv.Holders().Select(h => $"{h.Name}: {string.Join(",", h.Restrictions)}"))); }
            catch (Exception e) { _log.LogDebug(e, "way out: RLV's holders"); }
            try { await RunAsync(c, seated); }
            catch (Exception e) { _log.LogWarning(e, "way out"); }
            finally { Volatile.Write(ref _running, 0); }
        });
    }

    async Task RunAsync(GridClient c, bool seated)
    {
        // after the stand reaches the region, or the teleport is refused for still being seated
        if (seated) await Task.Delay(300);
        var here = c.Network.CurrentSim?.Name;
        // TeleportLandmarkRequest with no landmark: the viewer's Teleport Home
        var home = await _s.Teleports.GoAsync(c, () => c.Self.RequestTeleport(UUID.Zero), "home", quiet: true);
        switch (home.Outcome)
        {
            case Outcome.Arrived:
                _log.LogInformation("way out: home{Region}", home.Region is { } r ? $" ({r})" : "");
                return;
            case Outcome.Stalled:
                // the grid moved the agent home; the session logs in again there (AgentSession's watchdog)
                // (its notice says so: TeleportStalled)
                _log.LogWarning("way out: sent home{Region}, but the region there never answered", home.Region is { } r2 ? $" ({r2})" : "");
                return;
        }

        var why = home.Why ?? "no answer";
        _log.LogWarning("way out: home refused: {Why}", why);
        if (!c.Network.Connected || c.Network.CurrentSim is null) { Say($"Couldn't go home: {why}."); return; }
        // the latest places in the teleport history in other regions, up to three (one may be home's own region, down)
        var tried = new List<string> { here ?? "" };
        string? lastWhy = null;
        for (int i = 0; i < 3 && _s.Places.Elsewhere(tried) is { } place; i++)
        {
            tried.Add(place.Region);
            Say(i == 0 ? $"Couldn't go home: {why}. Going to {place.Region} instead." : $"Couldn't go there either: {lastWhy}. Trying {place.Region}.");
            var other = await GoToAsync(c, place);
            if (other.Outcome == Outcome.Arrived)
            {
                _log.LogInformation("way out: went to {Region} instead", place.Region);
                return;
            }
            if (other.Outcome == Outcome.Stalled) return;   // (the session logs in again and says so)
            lastWhy = other.Why ?? "no answer";
            _log.LogWarning("way out: {Region} refused too: {Why}", place.Region, lastWhy);
        }
        Say(lastWhy == null ? $"Couldn't go home: {why}. There's no other place in your teleport history to go instead."
            : $"Couldn't go there either: {lastWhy}. Staying here.");
    }

    async Task<Result> GoToAsync(GridClient c, Places.Place place)
    {
        ulong handle;
        if (string.Equals(c.Network.CurrentSim?.Name, place.Region, StringComparison.OrdinalIgnoreCase)) handle = c.Network.CurrentSim!.Handle;
        else
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            GridRegion? region = null;
            try { region = await c.Grid.GetGridRegionAsync(place.Region, GridLayerType.Objects, cts.Token); }
            catch (OperationCanceledException) { }
            if (region is not { } g) return new Result(Outcome.Refused, $"couldn't find {place.Region} on the map", null);
            handle = g.RegionHandle;
        }
        var pos = place.Pos;
        return await _s.Teleports.GoAsync(c, () => c.Self.RequestTeleport(handle, pos, pos + new Vector3(1, 0, 0)), place.GoTo, quiet: true);
    }

    void Say(string text) => _s.Notify.Add("WayOut", "notify", text, Notifications.None);
}
