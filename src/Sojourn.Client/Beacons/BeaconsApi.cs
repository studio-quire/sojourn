using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Client.Beacons;

/// <summary>
/// SojournBeacons: the tracking beacon for the assistant and any caller (track a place or a person, stop, where it is),
/// and what the beacons show. The switches themselves (beacons, beacon_lines, beacons_touch, highlight_transparent and
/// the rest) are SojournSettings. Nothing here is gated: it only changes what this viewer draws.
/// </summary>
public sealed class BeaconsApi : LeapApi
{
    readonly BeaconsWiring _w;

    public override string Name => "SojournBeacons";
    public override string Desc => "the tracking beacon (a column of light over a place or person, with how far), and what beacons show";

    public BeaconsApi(AgentSession s, BeaconsWiring w) : base(s)
    {
        _w = w;
        Op("track", "{avatar: id, name?} | {region?: name (this one by default), at: [x, y, z?] in it, name?} | {global: [x, y, z?], name?} "
            + "-> {tracking}: the viewer's tracking beacon over that person or place, with its name and distance; it ends on arriving "
            + "within 3 m (or untrack). A person is followed wherever the regions in view or, for a friend who lets you see them on the "
            + "map, the grid say they are", Track);
        Op("untrack", "-> {tracking: null}: the tracking beacon taken down", _ =>
        {
            _w.Tracker.Clear();
            return new JsonObject { ["tracking"] = null };
        });
        Op("state", "-> {beacons, lines, highlights, kinds: [...], width, sun, moon, highlight_transparent, tracking?: {kind, name, "
            + "avatar?, region, at, distance?}}: what's shown", _ => State());
    }

    async Task<JsonNode?> Track(JsonObject a, CancellationToken ct)
    {
        var c = S.Client ?? throw new NotConnected("viewer not connected");
        string? name = a.Str("name");
        if (a.Uuid("avatar") is { } id)
        {
            _w.Tracker.TrackAvatar(id, name);
            return new JsonObject { ["tracking"] = Tracking() };
        }
        if (a.Nums("global") is { Length: >= 2 } g)
        {
            ulong handle = Utils.UIntsToLong((uint)(g[0] / 256) * 256, (uint)(g[1] / 256) * 256);
            Utils.LongToUInts(handle, out uint ox, out uint oy);
            _w.Tracker.TrackPlace(handle, new((float)(g[0] - ox), (float)(g[1] - oy), g.Length > 2 ? (float)g[2] : 0), name ?? "Destination");
            return new JsonObject { ["tracking"] = Tracking() };
        }
        var at = a.Nums("at") ?? throw new LeapError("track takes {avatar}, {region?, at: [x, y, z?]} or {global: [x, y, z?]}");
        if (at.Length < 2) throw new LeapError("at: [x, y] or [x, y, z] in the region");
        ulong region;
        string regionName;
        if (a.Str("region") is { Length: > 0 } rn)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var r = await c.Grid.GetGridRegionAsync(rn, GridLayerType.Objects, cts.Token) ?? throw new LeapError($"no region called {rn}");
            (region, regionName) = (r.RegionHandle, r.Name);
        }
        else
        {
            var sim = c.Network.CurrentSim ?? throw new LeapError("no region");
            (region, regionName) = (sim.Handle, sim.Name);
        }
        var local = new System.Numerics.Vector3((float)at[0], (float)at[1], at.Length > 2 ? (float)at[2] : 0);
        _w.Tracker.TrackPlace(region, local, name ?? $"{regionName} ({local.X:0}, {local.Y:0}, {local.Z:0})");
        return new JsonObject { ["tracking"] = Tracking() };
    }

    JsonNode? Tracking()
    {
        if (_w.Tracker.Current is not { } t) return null;
        var o = new JsonObject { ["kind"] = t.Kind == Tracker.Kind.Avatar ? "avatar" : "place", ["name"] = t.Name };
        if (t.Kind == Tracker.Kind.Avatar) o["avatar"] = t.Avatar.ToString();
        if (_w.Tracker.Where is { } w)
        {
            ulong h = Utils.UIntsToLong((uint)(w.Global.X / 256) * 256, (uint)(w.Global.Y / 256) * 256);
            Utils.LongToUInts(h, out uint ox, out uint oy);
            o["region"] = S.Client?.Grid.RegionsByHandleReadOnly.TryGetValue(h, out var r) == true && r.Name is { Length: > 0 } ? r.Name : h.ToString();
            o["at"] = new JsonArray(Math.Round(w.Global.X - ox, 1), Math.Round(w.Global.Y - oy, 1), Math.Round(w.Global.Z, 1));
            o["distance"] = Math.Round(w.Distance, 1);
        }
        return o;
    }

    JsonObject State()
    {
        var st = _w.Settings;
        var kinds = new JsonArray();
        foreach (var (on, k) in new[] { (st.Scripted, "scripted"), (st.Touch, "touch"), (st.Physical, "physical"), (st.Sounds, "sounds"),
                     (st.Particles, "particles"), (st.Media, "media") })
            if (on) kinds.Add(k);
        return new JsonObject
        {
            ["beacons"] = st.Beacons, ["lines"] = st.Lines, ["highlights"] = st.Highlights, ["kinds"] = kinds, ["width"] = st.Width,
            ["sun"] = st.Sun, ["moon"] = st.Moon, ["highlight_transparent"] = _w.Transparent, ["tracking"] = Tracking(),
        };
    }
}
