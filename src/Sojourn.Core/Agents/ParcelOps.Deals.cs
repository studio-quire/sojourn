using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Land changing hands and shape, as the viewer's land tools do it (llviewerparcelmgr.cpp, llfloaterbuyland.cpp,
/// llfloatersellland.cpp): selling (to anyone or one buyer, with or without the objects on it), buying (ParcelBuy at
/// the price and area the person was shown, which the region checks again), a pass onto a pass-restricted parcel
/// (ParcelBuyPass), abandoning (ParcelRelease), deeding to a group (ParcelDeedToGroup), the estate owner's reclaim
/// (ParcelReclaim), dividing and joining (ParcelDivide, ParcelJoin on a rectangle of 4 m cells). Each is gated
/// (Client/Land/LandWiring.cs) and each watches for the region's answer, since most come back without a word. Claude
/// Opus 5.5, 2026-10-28 (batch B5).
/// </summary>
public sealed partial class ParcelOps
{
    /// <summary>A parcel the region just turned me away from (ParcelProperties with a collision sequence id), for a
    /// pass bought without `at`: the viewer offers its pass from the ban line.</summary>
    record Collision(string Region, int LocalId, DateTime When);
    volatile Collision? _refused;

    /// <summary>The region's ParcelProperties (the client's hook on each GridClient): remembers a collision.</summary>
    public void Observe(ParcelPropertiesEventArgs e)
    {
        if (e.SequenceID is -40000 or -30000 or -20000 && e.Parcel is { } p)   // NOT_ON_LIST, BANNED, NOT_IN_GROUP
            _refused = new Collision(e.Simulator.Name, p.LocalID, DateTime.UtcNow);
    }

    /// <summary>A parcel known without asking (the gate decides at once whether to ask): what the region last said.</summary>
    public (Simulator Sim, Parcel Parcel)? Known(JsonObject a)
    {
        if (_s.Client is not { } c || c.Network.CurrentSim is not { } sim) return null;
        try
        {
            int id = a.Has("local_id") ? a.Int("local_id", 0) : c.Parcels.GetParcelLocalID(sim, Point(a) ?? c.Self.SimPosition);
            return id != 0 && sim.Parcels.TryGetValue(id, out var p) ? (sim, p) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>A group of mine by `group` (id or name); null when none is named.</summary>
    public UUID? GroupArg(JsonObject a, string key = "group")
    {
        var g = a.Str(key) ?? a.Str(key + "_id");
        if (string.IsNullOrWhiteSpace(g)) return null;
        if (UUID.TryParse(g, out var id)) return id;
        var mine = _s.State.Groups.Values.FirstOrDefault(x => string.Equals(x.Name, g.Trim(), StringComparison.OrdinalIgnoreCase));
        return mine.ID != UUID.Zero ? mine.ID : throw new LeapError($"I'm not in a group called {g}");
    }

    bool GroupPower(UUID group, GroupPowers power) =>
        _s.State.Groups.TryGetValue(group, out var g) && (g.Powers & power) != 0;

    string Balance(int cost) => _s.BalanceKnown && _s.Client is { } c && c.Self.Balance < cost
        ? $"it costs L${cost} and the balance is L${c.Self.Balance}" : "";

    /// <summary>The parcel again by its id until <paramref name="done"/> holds of it (the region answers most land
    /// changes with no word, only the parcel's new properties).</summary>
    async Task<Parcel?> WatchAsync(Simulator sim, int localId, Func<Parcel, bool> done, TimeSpan within, CancellationToken ct)
    {
        var end = DateTime.UtcNow + within;
        while (DateTime.UtcNow < end)
        {
            await Task.Delay(900, ct);
            try
            {
                var (_, p) = await ParcelAsync(new JsonObject { ["local_id"] = localId }, ct);
                if (done(p)) return p;
            }
            catch (LeapError) { }   // gone (joined, divided): the caller looks again by place
        }
        return null;
    }

    string Said(DateTime since) => _s.State.LastAlert is { } al && al.T >= since ? $"; the region said: {al.Message}" : "";

    // --- selling ---------------------------------------------------------------------------------------------------

    /// <summary>About Land > Sell Land: for sale at a price, to anyone or one person or group, with or without the
    /// objects on it (the person's own, which go with the land).</summary>
    public async Task<JsonObject> SellAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var (sim, p) = await ParcelAsync(a, ct);
        int price = a.Int("price", -1);
        if (price < 0) throw new LeapError("need price (L$, 0 or more)");
        var buyer = UUID.Zero;
        if (a.Has("to")) buyer = (await PeopleAsync(a, ct))[0];
        else if (GroupArg(a, "to_group") is { } g) buyer = g;
        // llfloatersellland.cpp: a sale to anyone needs a price
        if (price == 0 && buyer == UUID.Zero) throw new LeapError("selling to anyone needs a price above L$0 (or name the one buyer)");
        p.Flags |= ParcelFlags.ForSale;
        p.SalePrice = price;
        p.AuthBuyerID = buyer;
        if (a.Bool("objects") is { } objects)
        {
            if (objects) p.Flags |= ParcelFlags.SellParcelObjects; else p.Flags &= ~ParcelFlags.SellParcelObjects;
        }
        p.Update(c, sim, false);
        var after = await WatchAsync(sim, p.LocalID, x => (x.Flags & ParcelFlags.ForSale) != 0 && x.SalePrice == price && x.AuthBuyerID == buyer, TimeSpan.FromSeconds(5), ct)
            ?? throw new LeapError($"the region didn't put it up for sale: only its owner (or a group member with the powers) may (owner {NameOf(p.OwnerID)})");
        return Describe(after);
    }

    /// <summary>Not for sale any more.</summary>
    public async Task<JsonObject> StopSellingAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var (sim, p) = await ParcelAsync(a, ct);
        if ((p.Flags & ParcelFlags.ForSale) == 0) return Describe(p);
        p.Flags &= ~(ParcelFlags.ForSale | ParcelFlags.SellParcelObjects);
        p.SalePrice = 0;
        p.AuthBuyerID = UUID.Zero;
        p.Update(c, sim, false);
        var after = await WatchAsync(sim, p.LocalID, x => (x.Flags & ParcelFlags.ForSale) == 0, TimeSpan.FromSeconds(5), ct)
            ?? throw new LeapError("the region didn't take it off sale: only its owner (or a group member with the powers) may");
        return Describe(after);
    }

    // --- buying ----------------------------------------------------------------------------------------------------

    /// <summary>Why the person can't buy this parcel (LLFloaterBuyLandUI::updateParcelInfo's checks), or null.</summary>
    public string? BuyRefusal(Parcel p, UUID group)
    {
        var c = C;
        var me = c.Self.AgentID;
        if ((p.Flags & ParcelFlags.ForSale) == 0 || (p.SalePrice == 0 && p.AuthBuyerID == UUID.Zero)) return "that parcel isn't for sale";
        if (group != UUID.Zero && !GroupPower(group, GroupPowers.LandDeed)) return "buying land for a group needs its land-deed power";
        var newOwner = group != UUID.Zero ? group : me;
        if (p.OwnerID == newOwner) return group != UUID.Zero ? "the group already owns it" : "it's already yours";
        if (p.AuthBuyerID != UUID.Zero && p.AuthBuyerID != me
            && !(GroupPower(p.AuthBuyerID, GroupPowers.LandDeed) && GroupPower(p.AuthBuyerID, GroupPowers.LandSetSale)))
            return $"it's for sale only to {NameOf(p.AuthBuyerID)}";
        var poor = Balance(p.SalePrice);
        return poor.Length > 0 ? poor : null;
    }

    public async Task<JsonObject> BuyAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var (sim, p) = await ParcelAsync(a, ct);
        var group = GroupArg(a) ?? UUID.Zero;
        if (BuyRefusal(p, group) is { } no) throw new LeapError(no + "; nothing was bought");
        if (a.Bool("unasked", false)) throw new LeapError("it looked as if it couldn't be bought a moment ago, so you weren't asked; ask again");
        // what the person confirmed (Client/Land/LandWiring.cs pins it): a change since is the seller's, not theirs
        if (a.Has("price") && a.Int("price", -1) != p.SalePrice) throw new LeapError($"the price changed to L${p.SalePrice} since you were asked; nothing was bought");
        if (a.Has("area") && a.Int("area", -1) != p.Area) throw new LeapError($"the parcel changed to {p.Area} m² since you were asked; nothing was bought");
        var since = DateTime.UtcNow;
        var newOwner = group != UUID.Zero ? group : c.Self.AgentID;
        // the viewer sends the active group even for a personal buy, and Final (no escrow)
        c.Parcels.Buy(sim, p.LocalID, group != UUID.Zero, group != UUID.Zero ? group : c.Self.ActiveGroup, false, p.Area, p.SalePrice);
        var after = await WatchAsync(sim, p.LocalID, x => x.OwnerID == newOwner, TimeSpan.FromSeconds(15), ct)
            ?? throw new LeapError($"the land isn't {(group != UUID.Zero ? "the group's" : "yours")} after 15 s{Said(since)}");
        var res = Describe(after);
        res["bought_for"] = p.SalePrice;
        return res;
    }

    // --- passes ----------------------------------------------------------------------------------------------------

    /// <summary>The parcel a pass is for: `at`, `local_id`, the one I stand on if it sells passes, else the one that
    /// turned me away in the last ten minutes.</summary>
    public async Task<(Simulator Sim, Parcel Parcel)> PassParcelAsync(JsonObject a, CancellationToken ct)
    {
        if (a.Has("at") || a.Has("local_id")) return await ParcelAsync(a, ct);
        var here = await ParcelAsync(a, ct);
        if ((here.Parcel.Flags & ParcelFlags.UsePassList) != 0 && here.Parcel.OwnerID != C.Self.AgentID) return here;
        if (_refused is { } r && DateTime.UtcNow - r.When < TimeSpan.FromMinutes(10) && r.Region == here.Sim.Name)
            return await ParcelAsync(new JsonObject { ["local_id"] = r.LocalId }, ct);
        return here;
    }

    public string? PassRefusal(Parcel p)
    {
        if ((p.Flags & ParcelFlags.UsePassList) == 0) return "that parcel sells no passes";
        if (p.OwnerID == C.Self.AgentID) return "it's yours";
        var poor = Balance(p.PassPrice);
        return poor.Length > 0 ? poor : null;
    }

    /// <summary>LLViewerParcelMgr::buyPass: the region takes the pass price and lets me in for its hours.</summary>
    public async Task<JsonObject> BuyPassAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var (sim, p) = await PassParcelAsync(a, ct);
        if (PassRefusal(p) is { } no) throw new LeapError(no + "; nothing was bought");
        if (a.Bool("unasked", false)) throw new LeapError("it looked as if no pass could be bought a moment ago, so you weren't asked; ask again");
        if (a.Has("price") && a.Int("price", -1) != p.PassPrice) throw new LeapError($"the pass price changed to L${p.PassPrice} since you were asked; nothing was bought");
        var since = DateTime.UtcNow;
        var tcs = new TaskCompletionSource<MoneyBalanceReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(object? _, MoneyBalanceReplyEventArgs e) => tcs.TrySetResult(e);
        c.Self.MoneyBalanceReply += On;
        try
        {
            c.Network.SendPacket(new ParcelBuyPassPacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
                ParcelData = { LocalID = p.LocalID },
            }, sim);
            MoneyBalanceReplyEventArgs? reply = null;
            try { reply = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8), ct); } catch (TimeoutException) { }
            if (reply is { Success: false }) throw new LeapError($"the pass wasn't sold: {reply.Description}");
            var res = new JsonObject
            {
                ["parcel"] = p.Name, ["price"] = p.PassPrice, ["hours"] = Json.R(p.PassHours, 2),
                ["until"] = DateTime.Now.AddHours(p.PassHours).ToString("ddd HH:mm"),
            };
            if (reply is null) res["note"] = $"no word from the grid on the payment{Said(since)}";
            else res["balance"] = reply.Balance;
            return res;
        }
        finally { c.Self.MoneyBalanceReply -= On; }
    }

    // --- abandoning, deeding, reclaiming ---------------------------------------------------------------------------

    /// <summary>LLViewerParcelMgr::startReleaseLand: the land goes back to the estate (Governor Linden on the mainland).</summary>
    public async Task<JsonObject> AbandonAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var (sim, p) = await ParcelAsync(a, ct);
        bool can = Mine(p) || (p.IsGroupOwned && GroupPower(p.OwnerID, GroupPowers.LandRelease));
        if (!can) throw new LeapError($"only its owner may abandon it (owner {NameOf(p.OwnerID)})");
        var since = DateTime.UtcNow;
        var was = p.OwnerID;
        c.Network.SendPacket(new ParcelReleasePacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
            Data = { LocalID = p.LocalID },
        }, sim);
        var after = await WatchAsync(sim, p.LocalID, x => x.OwnerID != was, TimeSpan.FromSeconds(10), ct)
            ?? throw new LeapError($"it's still {(p.IsGroupOwned ? "the group's" : "yours")}{Said(since)}");
        return new JsonObject { ["abandoned"] = p.Name, ["area"] = p.Area, ["owner_now"] = NameOf(after.OwnerID) };
    }

    /// <summary>LLViewerParcelMgr::sendParcelDeed: to the parcel's group (set first when another group is named).</summary>
    public async Task<JsonObject> DeedAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var (sim, p) = await ParcelAsync(a, ct);
        var group = GroupArg(a) ?? p.GroupID;
        if (group == UUID.Zero) throw new LeapError("name the group (it becomes the parcel's group and owner)");
        if (!_s.IsMyGroup(group)) throw new LeapError("I'm not in that group");
        if (!Mine(p)) throw new LeapError($"only its owner may deed it (owner {NameOf(p.OwnerID)})");
        if (p.GroupID != group)
        {
            p.GroupID = group;
            p.Update(c, sim, false);
            if (await WatchAsync(sim, p.LocalID, x => x.GroupID == group, TimeSpan.FromSeconds(5), ct) is null)
                throw new LeapError("the region didn't set the parcel's group; nothing was deeded");
        }
        var since = DateTime.UtcNow;
        c.Parcels.DeedToGroup(sim, p.LocalID, group);
        var after = await WatchAsync(sim, p.LocalID, x => x.OwnerID == group && x.IsGroupOwned, TimeSpan.FromSeconds(12), ct)
            ?? throw new LeapError($"it's still yours after 12 s (deeding needs the group's land-deed power, and on Second Life enough land contributed){Said(since)}");
        return Describe(after);
    }

    /// <summary>LLViewerParcelMgr::reclaimParcel: the region's owner takes a parcel back from whoever owns it.</summary>
    public async Task<JsonObject> ReclaimAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var (sim, p) = await ParcelAsync(a, ct);
        if (p.OwnerID == c.Self.AgentID) throw new LeapError("it's already yours");
        var since = DateTime.UtcNow;
        c.Parcels.Reclaim(sim, p.LocalID);
        var after = await WatchAsync(sim, p.LocalID, x => x.OwnerID == c.Self.AgentID, TimeSpan.FromSeconds(10), ct)
            ?? throw new LeapError($"the region didn't give it back (only the region's owner may reclaim){Said(since)}");
        return Describe(after);
    }

    // --- dividing and joining ----------------------------------------------------------------------------------------

    const float Cell = 4f;   // PARCEL_GRID_STEP_METERS

    /// <summary>A rectangle in this region from `west, south, east, north`, two points (`at`: [[x, y], [x, y]]), or a half
    /// of the parcel's bounds (`half`: north|south|east|west), snapped out to the 4 m cells parcels are made of.</summary>
    public static (float W, float S, float E, float N)? Rect(JsonObject a, Parcel? p, float size)
    {
        float w, s, e, n;
        if (a.Num("west") is { } w0 && a.Num("south") is { } s0 && a.Num("east") is { } e0 && a.Num("north") is { } n0)
            (w, s, e, n) = ((float)w0, (float)s0, (float)e0, (float)n0);
        else if (a["at"] is JsonArray two && two.Count == 2 && two[0] is JsonArray p0 && two[1] is JsonArray p1)
        {
            float X(JsonArray v) => (float)(Json.AsDouble(v[0]) ?? 0);
            float Y(JsonArray v) => (float)(Json.AsDouble(v[1]) ?? 0);
            (w, s, e, n) = (MathF.Min(X(p0), X(p1)), MathF.Min(Y(p0), Y(p1)), MathF.Max(X(p0), X(p1)), MathF.Max(Y(p0), Y(p1)));
        }
        else if (a.Str("half") is { } half && p != null)
        {
            var (minX, minY, maxX, maxY) = (p.AABBMin.X, p.AABBMin.Y, p.AABBMax.X, p.AABBMax.Y);
            float midX = MathF.Round((minX + maxX) / 2 / Cell) * Cell, midY = MathF.Round((minY + maxY) / 2 / Cell) * Cell;
            (w, s, e, n) = half.Trim().ToLowerInvariant() switch
            {
                "north" => (minX, midY, maxX, maxY),
                "south" => (minX, minY, maxX, midY),
                "east" => (midX, minY, maxX, maxY),
                "west" => (minX, minY, midX, maxY),
                _ => throw new LeapError("half: north | south | east | west"),
            };
        }
        else return null;
        w = MathF.Floor(w / Cell) * Cell; s = MathF.Floor(s / Cell) * Cell;
        e = MathF.Ceiling(e / Cell) * Cell; n = MathF.Ceiling(n / Cell) * Cell;
        if (e <= w) e = w + Cell;
        if (n <= s) n = s + Cell;
        return (Math.Clamp(w, 0, size), Math.Clamp(s, 0, size), Math.Clamp(e, 0, size), Math.Clamp(n, 0, size));
    }

    /// <summary>LLViewerParcelMgr::callbackDivideLand: a rectangle inside one parcel becomes a parcel of its own
    /// (with the same settings).</summary>
    public async Task<JsonObject> DivideAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var (sim, p) = await ParcelAsync(PointOf(a), ct);
        var r = Rect(a, p, sim.SizeX) ?? throw new LeapError("need west, south, east, north (metres), or half: north|south|east|west of the parcel");
        if (r.W <= p.AABBMin.X && r.S <= p.AABBMin.Y && r.E >= p.AABBMax.X && r.N >= p.AABBMax.Y)
            throw new LeapError("that's the whole parcel; divide off a part of it");
        int before = p.Area;
        var since = DateTime.UtcNow;
        c.Parcels.ParcelSubdivide(sim, r.W, r.S, r.E, r.N);
        var after = await WatchAsync(sim, p.LocalID, x => x.Area != before, TimeSpan.FromSeconds(10), ct)
            ?? throw new LeapError($"the parcel wasn't divided (the rectangle must lie in one parcel that's yours, or a group's you may divide){Said(since)}");
        var (_, part) = await ParcelAsync(new JsonObject { ["at"] = new JsonArray((double)(r.W + r.E) / 2, (double)(r.S + r.N) / 2) }, ct);
        return new JsonObject
        {
            ["divided"] = p.Name, ["rest"] = new JsonObject { ["local_id"] = after.LocalID, ["area"] = after.Area },
            ["new_parcel"] = new JsonObject { ["local_id"] = part.LocalID, ["area"] = part.Area, ["bounds"] = Json.Arr(r.W, r.S, r.E, r.N) },
        };
    }

    /// <summary>LLViewerParcelMgr::callbackJoinLand: every parcel the rectangle touches becomes one (all the same
    /// owner's; the joined parcel keeps the settings of the largest).</summary>
    public async Task<JsonObject> JoinAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var sim = c.Network.CurrentSim ?? throw new LeapError("no region");
        var r = Rect(a, null, sim.SizeX) ?? throw new LeapError("need at: [[x, y], [x, y]] (a point in each parcel) or west, south, east, north");
        // the rectangle's corners, a little inside: two different parcels before, one after
        JsonObject Corner(float x, float y) => new() { ["at"] = new JsonArray((double)x, (double)y) };
        var sw = Corner(r.W + 1, r.S + 1);
        var ne = Corner(r.E - 1, r.N - 1);
        var (_, p0) = await ParcelAsync(sw, ct);
        var (_, p1) = await ParcelAsync(ne, ct);
        if (p0.LocalID == p1.LocalID) throw new LeapError("that rectangle's corners are in one parcel: reach from a point in one parcel to a point in the other");
        var since = DateTime.UtcNow;
        c.Parcels.ParcelJoin(sim, r.W, r.S, r.E, r.N);
        for (int i = 0; i < 12; i++)
        {
            await Task.Delay(900, ct);
            var (_, q0) = await ParcelAsync(sw, ct);
            var (_, q1) = await ParcelAsync(ne, ct);
            if (q0.LocalID == q1.LocalID)
                return new JsonObject { ["joined"] = new JsonArray(p0.Name, p1.Name), ["parcel"] = Describe(q0) };
        }
        throw new LeapError($"the parcels weren't joined (all must be the same owner's, and yours or a group's you may join){Said(since)}");
    }

    /// <summary>The parcel a divide works on: `at` given as one point, else where the rectangle is.</summary>
    static JsonObject PointOf(JsonObject a)
    {
        if (a.Has("local_id")) return new JsonObject { ["local_id"] = a.Int("local_id", 0), ["region"] = a.Str("region") };
        if (a["at"] is JsonArray xy && xy.Count >= 2 && xy[0] is not JsonArray) return new JsonObject { ["at"] = xy.DeepClone() };
        if (a.Num("west") is { } w && a.Num("south") is { } s && a.Num("east") is { } e && a.Num("north") is { } n)
            return new JsonObject { ["at"] = new JsonArray((w + e) / 2, (s + n) / 2) };
        return new JsonObject();
    }
}
