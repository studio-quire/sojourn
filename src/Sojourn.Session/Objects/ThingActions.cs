using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Session.Objects;

/// <summary>What the person can do with a thing in the world by their own hand, as the viewer's right-click pie offers it.</summary>
public enum ThingDo { Sit, Stand, Touch, Buy, Pay, TakeCopy, Take, Open, About, Ask }

/// <summary>One of those, as a card or a menu lists it: a short label, and a detail beside it (a price).</summary>
public sealed record ThingAction(ThingDo Do, string Label, string? Detail = null);

/// <summary>
/// What's known about a thing for the person's own actions on it: the prim pressed and its linkset's root, and from the
/// root's properties its name, description, owner, creator, sale and next owner's permissions; the prims' flags
/// (touch, pay, yours, copy, modify, contents) and click action; whether it's worn and whether you sit on it. Names,
/// land impact and the properties fill in once fetched (<see cref="ThingActions.FetchAsync"/>; Known says the
/// properties are in).
/// </summary>
public sealed record ThingFacts(UUID Id, UUID Root, uint RootLocal, ulong Region, string Name)
{
    public bool Known { get; init; }
    public string? Description { get; init; }
    public UUID Owner { get; init; }
    public string? OwnerName { get; init; }
    public bool GroupOwned { get; init; }
    public UUID Creator { get; init; }
    public string? CreatorName { get; init; }
    public SaleType Sale { get; init; }
    public int Price { get; init; }
    public PermissionMask NextPerms { get; init; }
    public PrimFlags Flags { get; init; }
    /// <summary>The click action of the prim pressed, else the root's (LibreMetaverse's numbering: 1 sit, 2 buy, 3 pay, 4 open...).</summary>
    public int Click { get; init; }
    public string? SitName { get; init; }
    public string? TouchName { get; init; }
    /// <summary>Worn by someone (another's attachment; HUDs and your own never get here).</summary>
    public UUID WornBy { get; init; }
    public bool MySeat { get; init; }
    public int Prims { get; init; } = 1;
    public int? LandImpact { get; init; }
    /// <summary>The prim that takes money: the one pressed if it has a money event, else the root (the viewer's).</summary>
    public UUID PayTo { get; init; }

    public bool Mine => (Flags & PrimFlags.ObjectYouOwner) != 0;
    public bool Worn => WornBy != UUID.Zero;
}

/// <summary>
/// The person's own actions on a thing in the world, shared by every face of the client: the VR client's card over a
/// thing held with the trigger (or a right click on the desktop), and Sojourn Access, which can list the same actions
/// for its objects. What applies (<see cref="For"/>) follows the viewer's pie menu (llviewermenu.cpp's enable_* checks):
/// sit on anything in the world but your own seat; stand up from it; touch where a press wouldn't already; buy what's
/// for sale and not yours; pay what has a money event; take a copy where you may copy it; take what's yours; open what
/// holds contents and is yours or modifiable; about; ask the assistant. Each runs through the agent API's ops, through
/// the dispatcher and its gate (<see cref="RunAsync"/>), so a buy or a pay waits for the person's own press on the
/// confirmation as everywhere; only a touch skips the gate, as the person's own touch always has (docs/assistant.md >
/// Confirmations). Claude Opus 5.5, 2026-10-31.
/// </summary>
public sealed class ThingActions(AgentSession s)
{
    /// <summary>The facts the world model has now about the prim with this id (in the region with that handle); null if
    /// it isn't in view or isn't an object.</summary>
    public ThingFacts? Facts(ulong region, UUID id)
    {
        if (s.Client is not { } c || s.World.Region(s.GridKey, region) is not { } w || w.PrimById(id) is not { } prim) return null;
        bool attached = s.Objects.IsAttachment(w, prim, out var wearer, out var root);
        if (attached && (wearer == null || wearer.ID == c.Self.AgentID)) return null;   // your own, or a HUD: not here
        var flags = s.State.PrimFlagsOf(prim) | (root != prim ? s.State.PrimFlagsOf(root) : PrimFlags.None);
        var props = root.Properties is { } rp && rp.ObjectID == root.ID ? rp : null;
        var own = prim.Properties is { } pp && pp.ObjectID == prim.ID ? pp : null;
        bool mySeat = c.Self.SittingOn != 0 && region == c.Network.CurrentSim?.Handle
            && (c.Self.SittingOn == prim.LocalID || c.Self.SittingOn == root.LocalID || (w.Prims.TryGetValue(c.Self.SittingOn, out var seat) && seat.ParentID == root.LocalID));
        int prims = 1 + w.Prims.Values.Count(p => p.ParentID == root.LocalID && !w.Avatars.ContainsKey(p.LocalID));
        var payTo = (s.State.PrimFlagsOf(prim) & PrimFlags.Money) != 0 ? prim.ID : root.ID;
        return new ThingFacts(prim.ID, root.ID, root.LocalID, region, props?.Name is { Length: > 0 } n ? n : "an object")
        {
            Known = props != null,
            Description = props?.Description is { Length: > 0 } d ? d : null,
            Owner = props?.OwnerID ?? root.OwnerID,
            GroupOwned = props != null && props.GroupID != UUID.Zero && props.OwnerID == props.GroupID,
            Creator = props?.CreatorID ?? UUID.Zero,
            Sale = props?.SaleType ?? SaleType.Not,
            Price = props?.SalePrice ?? 0,
            NextPerms = props?.Permissions.NextOwnerMask ?? PermissionMask.None,
            Flags = flags,
            Click = attached ? 0 : (int)(prim.ClickAction != 0 ? prim.ClickAction : root.ClickAction),
            SitName = Named(own?.SitName) ?? Named(props?.SitName),
            TouchName = Named(own?.TouchName) ?? Named(props?.TouchName),
            WornBy = wearer?.ID ?? UUID.Zero,
            MySeat = mySeat,
            Prims = prims,
            PayTo = payTo,
            OwnerName = Name(props?.OwnerID ?? root.OwnerID),
        };
    }

    static string? Named(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    string? Name(UUID id) => id == UUID.Zero ? null : s.Names.DisplayName(id) ?? s.Names.Legacy(id);

    /// <summary>The facts made whole: the root's full properties asked for, the owner's and creator's names, the land
    /// impact. What can't be had in a few seconds stays as it was.</summary>
    public async Task<ThingFacts> FetchAsync(ThingFacts f, CancellationToken ct = default)
    {
        try
        {
            await Own("TesselObjects", "get", new JsonObject { ["ids"] = new JsonArray(f.Root.ToString()), ["fetch"] = true, ["full"] = true, ["timeout"] = 4 }, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException) { }
        var now = Facts(f.Region, f.Id) ?? f;
        var ids = new[] { now.Owner, now.Creator }.Where(i => i != UUID.Zero && !(now.GroupOwned && i == now.Owner)).Distinct().ToList();
        if (s.Client is { } c && ids.Count > 0)
            try { await s.Names.ResolveAsync(c, ids, TimeSpan.FromSeconds(4), ct); }
            catch (Exception e) when (e is not OperationCanceledException) { }
        int? li = null;
        if (!now.Worn)
            try
            {
                var r = await Own("SojournBuild", "landImpact", new JsonObject { ["id"] = now.Root.ToString() }, ct);
                if (r["land_impact"] is JsonValue v && v.TryGetValue<double>(out var d)) li = (int)Math.Ceiling(d - 1e-6);
            }
            catch (Exception e) when (e is not OperationCanceledException) { }
        string? group = now.GroupOwned && s.State.Groups.TryGetValue(now.Owner, out var g) ? g.Name : null;
        return now with
        {
            OwnerName = now.GroupOwned ? group ?? "a group" : Name(now.Owner),
            CreatorName = Name(now.Creator),
            LandImpact = li ?? now.LandImpact,
        };
    }

    /// <summary>What can be done with it, the thing's own click action first (a vendor's buy, a chair's sit), then what
    /// it's set up for (a sale, a pay, a touch) before what any thing allows (sitting on it), then about and ask.
    /// tapTouches: a plain press already touches it, so no touch here.</summary>
    public List<ThingAction> For(ThingFacts f, bool tapTouches, bool assistant = true)
    {
        var list = new List<ThingAction>();
        bool world = !f.Worn;
        if (world && f.Known && f.Sale != SaleType.Not && !f.Mine)
            list.Add(new(ThingDo.Buy, f.Sale switch { SaleType.Copy => "buy a copy", SaleType.Contents => "buy its contents", _ => "buy" },
                f.Price == 0 ? "free" : $"L${f.Price}"));
        if (world && (f.Flags & PrimFlags.Money) != 0) list.Add(new(ThingDo.Pay, "pay"));
        if ((f.Flags & PrimFlags.Touch) != 0 && !tapTouches) list.Add(new(ThingDo.Touch, f.TouchName ?? "touch"));
        if (world && f.MySeat) list.Add(new(ThingDo.Stand, "stand up"));
        if (world && !f.MySeat) list.Add(new(ThingDo.Sit, f.SitName ?? "sit here"));
        if (world && (f.Flags & PrimFlags.ObjectCopy) != 0) list.Add(new(ThingDo.TakeCopy, "take a copy"));
        if (world && f.Mine) list.Add(new(ThingDo.Take, "take"));
        if (world && (f.Flags & PrimFlags.InventoryEmpty) == 0 && (f.Mine || (f.Flags & PrimFlags.ObjectModify) != 0)) list.Add(new(ThingDo.Open, "open"));
        list.Add(new(ThingDo.About, "about this"));
        if (assistant) list.Add(new(ThingDo.Ask, "ask the assistant"));
        // its own click action leads (the viewer's left click does it; here it's the first thing on the card)
        var lead = f.Click switch { 1 => ThingDo.Sit, 2 => ThingDo.Buy, 3 => ThingDo.Pay, 4 => ThingDo.Open, _ => (ThingDo?)null };
        if (lead is { } l && list.FindIndex(a => a.Do == l) is > 0 and var i)
        {
            var a = list[i];
            list.RemoveAt(i);
            list.Insert(0, a);
        }
        return list;
    }

    /// <summary>About it, as lines for a card: owner, creator, description, land impact and prims, what a next owner may
    /// do, the sale.</summary>
    public static List<(string Label, string Value)> About(ThingFacts f)
    {
        var lines = new List<(string, string)>();
        if (f.Worn) lines.Add(("worn by", f.OwnerName ?? "someone"));
        else if (f.OwnerName is { Length: > 0 } o) lines.Add(("owner", f.Mine ? "you" : o));
        if (f.CreatorName is { Length: > 0 } cr && f.Creator != f.Owner) lines.Add(("creator", cr));
        if (f.Description is { Length: > 0 } d && d != "(No Description)") lines.Add(("description", d));
        if (f.LandImpact is { } li) lines.Add(("land impact", f.Prims > 1 ? $"{li} · {f.Prims} prims" : li.ToString()));
        else if (f.Prims > 1) lines.Add(("prims", f.Prims.ToString()));
        if (f.Known)
        {
            var p = f.NextPerms;
            var can = new[] { ((p & PermissionMask.Copy) != 0, "copy"), ((p & PermissionMask.Modify) != 0, "modify"), ((p & PermissionMask.Transfer) != 0, "transfer") }
                .Where(x => x.Item1).Select(x => x.Item2).ToList();
            lines.Add(("next owner", can.Count == 0 ? "no copy, modify or transfer" : string.Join(", ", can)));
        }
        if (f.Known && f.Sale != SaleType.Not)
            lines.Add(("for sale", (f.Sale switch { SaleType.Copy => "a copy", SaleType.Contents => "its contents", _ => "the original" }) + (f.Price == 0 ? ", free" : $", L${f.Price}")));
        return lines;
    }

    // --- running them ---------------------------------------------------------------------------------------------

    /// <summary>The amounts its pay window offers (llSetPayPrice): its buttons, and whether any amount will do.</summary>
    public async Task<(List<int> Buttons, int? Suggested, bool AnyAmount)> PayPricesAsync(ThingFacts f, CancellationToken ct = default)
    {
        var r = await Own("SojournPeople", "payPrices", new JsonObject { ["id"] = f.PayTo.ToString() }, ct);
        var buttons = (r["buttons"] as JsonArray ?? new()).Select(n => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : -1).Where(i => i > 0).ToList();
        int? suggested = r["suggested"] is JsonValue sv && sv.TryGetValue<int>(out var si) && si > 0 ? si : null;
        bool any = r["any_amount"] is JsonValue av && av.TryGetValue<bool>(out var ab) && ab;
        return (buttons, suggested, any);
    }

    public sealed record Content(UUID Item, string Name, string Type, bool Copy);

    /// <summary>What's inside it (the root prim's contents), as the viewer's Open window lists them.</summary>
    public async Task<List<Content>> ContentsAsync(ThingFacts f, CancellationToken ct = default)
    {
        var r = await Own("TesselBuild", "contents", new JsonObject { ["id"] = f.Root.ToString(), ["running"] = false }, ct);
        var list = new List<Content>();
        foreach (var o in (r["items"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            if (!UUID.TryParse(o.Str("item_id"), out var id)) continue;
            // copied out, not moved: the item's owner may copy it (a no-copy item would leave the object)
            string letters = (o["perms"] as JsonObject)?.Str("owner") ?? "";
            list.Add(new Content(id, o.Str("name") ?? "an item", o.Str("type") ?? "", letters.Contains('C')));
        }
        return list;
    }

    /// <summary>
    /// Do it. at: where on it the person pressed (its region's frame), for a sit or a touch there. amount: a pay's.
    /// Returns what to tell the person (null: nothing to say, as when the grid says it itself or the person declined),
    /// and whether it went.
    /// </summary>
    public async Task<(bool Ok, string? Say)> RunAsync(ThingDo what, ThingFacts f, System.Numerics.Vector3? at = null, int amount = 0,
        IReadOnlyList<Content>? copy = null, CancellationToken ct = default)
    {
        JsonObject r;
        switch (what)
        {
            case ThingDo.Sit:
                if (at is { } p && s.World.Region(s.GridKey, f.Region) is { } w && w.PrimById(f.Id) is { } prim)
                {
                    // the press's offset from the prim in its frame, as the viewer's Sit Here sends it
                    var local = new Vector3(p.X, p.Y, p.Z) - w.RegionPosition(prim);
                    s.SendSit(f.Region, f.Id, local * Quaternion.Inverse(w.RegionRotation(prim)));
                    return (true, null);
                }
                r = await Own("LLAgent", "requestSit", new JsonObject { ["obj_uuid"] = f.Id.ToString() }, ct);
                break;
            case ThingDo.Stand:
                r = await Own("LLAgent", "requestStand", new JsonObject(), ct);
                break;
            case ThingDo.Touch:
            {
                // the person's own touch: not through the gate (that guards the assistant's touches)
                var a = new JsonObject { ["obj_uuid"] = f.Id.ToString() };
                if (at is { } tp) a["at"] = new JsonArray(tp.X, tp.Y, tp.Z);
                r = await Own("LLAgent", "requestTouch", a, ct);
                break;
            }
            case ThingDo.Buy:
                r = await Gated("TesselObjects", "buy", new JsonObject { ["id"] = f.Root.ToString(), ["price"] = f.Price }, ct);
                if (Error(r) is null) return (true, null);   // the grid says what was bought (and the inventory offer)
                break;
            case ThingDo.Pay:
                r = await Gated("TesselAgent", "pay", new JsonObject { ["to"] = f.PayTo.ToString(), ["amount"] = amount }, ct);
                if (Error(r) is null) return (true, null);
                break;
            case ThingDo.TakeCopy:
                r = await Own("TesselBuild", "take", new JsonObject { ["ids"] = new JsonArray(f.Root.ToString()), ["copy"] = true }, ct);
                if (Error(r) is null) return (true, $"A copy of {f.Name} is in your inventory.");
                break;
            case ThingDo.Take:
                r = await Own("TesselBuild", "take", new JsonObject { ["ids"] = new JsonArray(f.Root.ToString()) }, ct);
                if (Error(r) is null) return (true, $"{f.Name} is in your inventory.");
                break;
            case ThingDo.Open:
            {
                // the viewer's Open window's Copy to Inventory: what may be copied, into a folder named after it
                var items = (copy ?? await ContentsAsync(f, ct)).Where(i => i.Copy).ToList();
                if (items.Count == 0) return (false, "Nothing in it can be copied.");
                var folder = await Own("TesselInventory", "createFolder", new JsonObject { ["name"] = f.Name }, ct);
                if (Error(folder) is { } fe) return (false, Didnt(fe));
                int done = 0;
                string? last = null;
                foreach (var it in items)
                {
                    var one = await Own("TesselBuild", "copyFromContents", new JsonObject
                    {
                        ["id"] = f.Root.ToString(), ["item_id"] = it.Item.ToString(), ["folder_id"] = folder.Str("folder_id"),
                    }, ct);
                    if (Error(one) is { } e) last = e; else done++;
                }
                return done == items.Count
                    ? (true, $"{(done == 1 ? "One item" : $"{done} items")} copied into the folder {f.Name}.")
                    : (done > 0, $"{done} of {items.Count} copied into the folder {f.Name}" + (last != null ? $" ({last})." : "."));
            }
            default:
                return (false, null);
        }
        return Error(r) is { } err ? (false, Didnt(err)) : (true, null);
    }

    static string? Didnt(string error) =>
        error.Contains("declined", StringComparison.OrdinalIgnoreCase) || error.Contains("cancel", StringComparison.OrdinalIgnoreCase)
            ? null : $"Didn't work: {error}";

    static string? Error(JsonObject r) => r["error"] is JsonValue v && v.TryGetValue<string>(out var e) ? e : null;

    /// <summary>A spend: through the dispatcher and its gate, so it waits for the person's own press on the
    /// confirmation (and holds other ops meanwhile) as any caller's does. In incognito the dispatcher admits only the
    /// assistant's allowlist, which isn't for the person's own hand: there the gate is asked directly, then the op runs.</summary>
    async Task<JsonObject> Gated(string pump, string op, JsonObject args, CancellationToken ct)
    {
        args["op"] = op;
        try
        {
            if (!s.Private.On) return await s.Api.CallAsync(pump, args, reply: true, TimeSpan.FromSeconds(150), ct) as JsonObject ?? new JsonObject();
            if (s.Api.Gate?.Invoke(pump, op, args) is { } ask && await ask() is { } refusal) return Json.Error(refusal);
            return await Own(pump, op, args, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return Json.Error(e.Message); }
    }

    /// <summary>The op itself, as the person's own hand: lookups, and what isn't gated (a sit, a touch, taking a
    /// copy, copying contents out). The person's own touch skips the touch confirmation, which guards the assistant's.</summary>
    async Task<JsonObject> Own(string pump, string op, JsonObject args, CancellationToken ct)
    {
        args["op"] = op;
        if (!s.Api.ApiMap.TryGetValue(pump, out var api) || !api.Ops.TryGetValue(op, out var handler)) return Json.Error($"{pump}.{op} isn't here");
        try { return await handler.Run(args, ct) as JsonObject ?? new JsonObject(); }
        catch (LeapError e) { return Json.Error(e.Message); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return Json.Error($"{e.GetType().Name}: {e.Message}"); }
    }
}
