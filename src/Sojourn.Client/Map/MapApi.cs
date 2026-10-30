using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Client.Map;

/// <summary>
/// SojournMap (batch B8, docs/map.md): where the person is, as the location line says it; statistics at a glance (the
/// region's SimStats with this client's frame rate and the ping, and the lag meter's verdict); the world map shown at a
/// place or the mini-map following the person, or put away; friends on the map. The map's items are SojournLand mapItems; teleports are the go
/// tool's. Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class MapApi : LeapApi
{
    readonly MapWiring _w;

    public override string Name => "SojournMap";
    public override string Desc => "where the person is (parcel, rating, what's blocked, health), statistics at a glance (is it me or the region?), "
        + "the map board shown at a place, friends on the map";

    public MapApi(AgentSession s, MapWiring w) : base(s)
    {
        _w = w;
        Op("where", "-> {region, position, rating, parcel, no_fly, no_build, no_push, no_voice, no_scripts, damage, health?, "
            + "avatars_hidden_from_outside, for_sale?}: the location line's facts (the viewer's location bar)",
            _ => Where.Now(S)?.ToJson() ?? throw new LeapError("not in a region yet"));
        Op("stats", "-> {client {state, fps, target_fps}, network {state, ping_ms}, region {state, time_dilation, fps, physics_fps, frame_ms, "
            + "scripts_ms, physics_ms, net_ms, agents_ms, images_ms, slowest?, people, objects, active_scripts, ...}, verdict}: the viewer's "
            + "Statistics bar and Lag Meter in one; states fine | slow | very slow (the lag meter's thresholds)",
            _ => _w.StatsNow()?.ToJson() ?? throw new LeapError("not in a region yet"));
        Op("show", "{region? (a name: its middle, or at), at? ([x, y] in that region, or in this one), across? (metres the map spans, "
            + "77-300000; default as it was), pick? (true: the place at its middle picked, so Go is a press away), sale?, events? (the layers), "
            + "free? (the mini-map only: true out on its own in view, false on the hand)} -> {shown, map, where?, region, across}: with region or at, "
            + "the world map up, there (it keeps what it shows between openings); without, the mini-map, following the person: on the left hand over "
            + "the location line (seen when they look at their wrist, palm up) unless it's out on its own or free says so. The person goes by pressing Go",
            ShowAsync);
        Op("hide", "{map? (world | mini; default both)} -> {shown: false}: the world map put away, the mini-map closed", async (a, _) =>
        {
            var which = a.Str("map");
            await _w.OnMain(() =>
            {
                if (which is null or "world") _w.World.Show(false);
                if (which is null or "mini") _w.SetMini(MiniHome.Off);
            });
            return new JsonObject { ["shown"] = false };
        });
        Op("state", "-> {world {shown, following, middle {region?, at}, across, sale, events, picked?}, mini {the same, where: off | hand | free}}: "
            + "what the maps show", _ => State());
        Op("friends", "{wait_s=3} -> {friends: [{name, region?, at?, online, on_map}]}: online friends and where those who let you see them on "
            + "the map are (FindAgent)", FriendsAsync);
    }

    JsonObject Board(MapBoard b)
    {
        var v = b.View;
        var r = _w.Map.RegionAt(v.CX, v.CY);
        var o = new JsonObject
        {
            ["shown"] = b.Shown, ["following"] = v.Follow,
            ["middle"] = new JsonObject { ["region"] = r?.Name, ["at"] = r is { } rr ? new JsonArray((int)(v.CX - rr.X), (int)(v.CY - rr.Y)) : null },
            ["across"] = (int)(v.Mpp * MapLayout.Of(v).MapRect.Width), ["sale"] = v.Sale, ["events"] = v.Events,
        };
        if (v.Picked is { } p)
            o["picked"] = new JsonObject
            {
                ["region"] = p.Region?.Name, ["at"] = p.Region is { } pr ? new JsonArray((int)(p.X - pr.X), (int)(p.Y - pr.Y)) : null,
                ["what"] = p.Mark is { } m ? m.Kind.ToString().ToLowerInvariant() : null, ["name"] = p.Mark?.Name,
            };
        return o;
    }

    JsonObject State()
    {
        var mini = Board(_w.Mini);
        mini["where"] = _w.MiniAt.ToString().ToLowerInvariant();
        var o = new JsonObject { ["world"] = Board(_w.World), ["mini"] = mini };
        static JsonArray R(SkiaSharp.SKRect r) => new((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom);
        if (_w.Line.DesktopRect is not null || _w.World.DesktopPlace is not null || _w.Mini.DesktopPlace is not null)
            o["desktop"] = new JsonObject
            {
                ["line"] = _w.Line.DesktopRect is { } l ? R(l) : null,
                ["mini"] = _w.Mini.DesktopPlace is { } m ? R(m.Rect) : null, ["mini_scale"] = _w.Mini.DesktopPlace?.Scale,
                ["world"] = _w.World.DesktopPlace is { } b ? R(b.Rect) : null, ["world_scale"] = _w.World.DesktopPlace?.Scale,
            };
        return o;
    }

    async Task<JsonNode?> ShowAsync(JsonObject a, CancellationToken ct)
    {
        var c = S.Client ?? throw new NotConnected("viewer not connected");
        bool world = a.Str("region") is { Length: > 0 } || a["at"] is JsonArray;
        float? mpp = a.Num("across") is { } across
            ? (float)Math.Clamp(across, 77, 300000) / (world ? MapLayout.World : MapLayout.Of(_w.Mini.View)).MapRect.Width : null;
        bool? sale = a.Bool("sale"), events = a.Bool("events");
        double? gx = null, gy = null;
        MapRegion? region = null;
        var at = a["at"] as JsonArray;
        if (a.Str("region") is { Length: > 0 } name)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var g = await c.Grid.GetGridRegionAsync(name, GridLayerType.Objects, cts.Token) ?? throw new LeapError($"no region called {name}");
            Utils.LongToUInts(g.RegionHandle, out var rx, out var ry);
            region = new MapRegion(g.RegionHandle, rx, ry, g.Name, Where.Rating((SimAccess)g.Access), 256);
            gx = rx + (at is { Count: >= 2 } ? (double)at[0]! : 128);
            gy = ry + (at is { Count: >= 2 } ? (double)at[1]! : 128);
        }
        else if (at is { Count: >= 2 } && c.Network.CurrentSim is { } sim)
        {
            Utils.LongToUInts(sim.Handle, out var rx, out var ry);
            gx = rx + (double)at[0]!;
            gy = ry + (double)at[1]!;
            region = _w.Map.RegionAt(gx.Value, gy.Value);
        }
        world = gx != null;
        bool? free = a.Bool("free");
        await _w.OnMain(() =>
        {
            if (gx is { } x && gy is { } y)
                _w.World.LookAt(x, y, mpp, a.Bool("pick") == true ? new MapPick(x, y, region ?? _w.Map.RegionAt(x, y), null) : null);
            else
            {
                _w.SetMini(free switch { true => MiniHome.Free, false => MiniHome.Hand, _ => _w.MiniAt == MiniHome.Off ? MiniHome.Hand : _w.MiniAt });
                _w.Mini.FollowMe(a.Bool("pick") == true);
                if (mpp is { } m) _w.Mini.Zoom(MathF.Log2(_w.Mini.View.Mpp / m));
            }
            if (sale != null || events != null) _w.SetLayers(sale, events);
        });
        var st = Board(world ? _w.World : _w.Mini);
        var o = new JsonObject
        {
            ["shown"] = true, ["map"] = world ? "world" : "mini", ["region"] = st["middle"]!["region"]?.DeepClone(), ["across"] = st["across"]!.DeepClone(),
        };
        if (!world) o["where"] = _w.MiniAt.ToString().ToLowerInvariant();
        return o;
    }

    async Task<JsonNode?> FriendsAsync(JsonObject a, CancellationToken ct)
    {
        var c = S.Client ?? throw new NotConnected("viewer not connected");
        var found = new System.Collections.Concurrent.ConcurrentDictionary<UUID, (ulong Handle, Vector3 At)>();
        void On(object? _, FriendFoundReplyEventArgs e) { if (e.RegionHandle != 0) found[e.AgentID] = (e.RegionHandle, e.Location); }
        c.Friends.FriendFoundReply += On;
        try
        {
            foreach (var f in c.Friends.FriendList.Values)
                if (f.IsOnline && f.CanSeeThemOnMap) c.Friends.MapFriend(f.UUID);
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(a.Num("wait_s", 3), 0.5, 10)), ct);
        }
        finally { c.Friends.FriendFoundReply -= On; }
        var arr = new JsonArray();
        foreach (var f in c.Friends.FriendList.Values.Where(f => f.IsOnline).OrderBy(f => f.Name))
        {
            var o = new JsonObject { ["name"] = f.Name, ["id"] = f.UUID.ToString(), ["online"] = true, ["on_map"] = f.CanSeeThemOnMap };
            if (found.TryGetValue(f.UUID, out var w))
            {
                o["region"] = _w.Map.NameOf(w.Handle);
                o["at"] = new JsonArray((int)w.At.X, (int)w.At.Y, (int)w.At.Z);
            }
            arr.Add(o);
        }
        return new JsonObject { ["friends"] = arr };
    }
}
