using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Sojourn.Core.Agents;
using Sojourn.Core.World;

namespace Sojourn.Core.Leap;

/// <summary>Shared plumbing for the APIs.</summary>
public abstract class SessionApi : LeapApi
{
    protected SessionApi(AgentSession s) : base(s) { }
    protected GridClient C => S.Client ?? throw new NotConnected("viewer not connected");
    protected Simulator Sim => C.Network.CurrentSim ?? throw new LeapError("no region");
    protected RegionWorld W => S.Region ?? throw new LeapError("no region");

    /// <summary>Runs a task without awaiting it (failures are ignored, as with an un-replied LEAP post).</summary>
    protected static void Fire(Task t) => t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>Sends a payment (pay, buy) and waits for the grid's word on it: the MoneyBalanceReply that refuses it,
    /// or the one carrying its transaction. Null: no word in time (it may still go through; callers say so).</summary>
    protected static async Task<MoneyBalanceReplyEventArgs?> PaymentReplyAsync(GridClient c, Action send, Func<TransactionInfo, bool> ours,
        CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<MoneyBalanceReplyEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(object? _, MoneyBalanceReplyEventArgs e)
        {
            if (!e.Success || e.TransactionInfo is { } ti && ours(ti)) tcs.TrySetResult(e);
        }
        c.Self.MoneyBalanceReply += On;
        try
        {
            send();
            try { return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8), ct); }
            catch (TimeoutException) { return null; }
        }
        finally { c.Self.MoneyBalanceReply -= On; }
    }

    protected static List<UUID> IdsOf(JsonObject a, string many = "ids", string one = "id")
    {
        var ids = a.UuidList(many);
        if (a.Uuid(one) is { } x && !ids.Contains(x)) ids.Add(x);
        return ids;
    }
}

// ======================================================================================================
// TesselIM
// ======================================================================================================

public sealed class TesselIM : SessionApi
{
    public override string Name => "TesselIM";
    public override string Desc => "Instant messages: send, list sessions, read history (sojourn: protocol IMs, no viewer floaters)";

    static readonly TimeZoneInfo Pacific = FindPacific();
    static TimeZoneInfo FindPacific()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time"); }
        catch { try { return TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"); } catch { return TimeZoneInfo.Utc; } }
    }

    public TesselIM(AgentSession s) : base(s)
    {
        Op("send", "send {message, to | group_id | session_id} -> {session_id}", SendAsync);
        // what was said in a spell of incognito stays out of both (Agents/PrivateMode.cs), but while a seeing spell
        // lasts, and so does a conversation that only happened then
        Op("sessions", "-> {sessions: [...]}", _ => new JsonObject { ["sessions"] = new JsonArray(S.State.Ims.Values.Where(Shown).Select(Info).ToArray()) });
        Op("history", "{session_id, count=20} -> {session, messages}", History);
        Op("markRead", "{session_id}", a =>
        {
            if (a.Uuid("session_id") is { } id && S.State.Ims.TryGetValue(id, out var s)) s.Unread = 0;
            return new JsonObject();
        });
        Op("leave", "{session_id}", a =>
        {
            var id = a.Uuid("session_id") ?? throw new LeapError("no such session");
            if (!S.State.Ims.TryRemove(id, out var s)) throw new LeapError("no such session");
            if (s.Type != "p2p") C.Self.RequestLeaveGroupChat(id);
            return new JsonObject();
        });
        Op("conference", "{ids, message?} -> {session_id}", ConferenceAsync);
    }

    bool Shown(ImSession s)
    {
        lock (s.Lines) return s.Lines.Count == 0 || s.Lines.Any(l => S.Private.ShownSpell(l.Spell));
    }

    JsonNode Info(ImSession s)
    {
        int count;
        lock (s.Lines) count = s.Lines.Count(l => S.Private.ShownSpell(l.Spell));
        return new JsonObject
        {
            ["session_id"] = s.Id.ToString(), ["name"] = s.Name, ["type"] = s.Type, ["other_id"] = s.OtherId.ToString(),
            ["unread"] = s.Unread, ["offline"] = s.Offline, ["ready"] = s.Ready, ["messages"] = count,
        };
    }

    async Task<JsonNode?> SendAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var msg = a.Str("message") ?? "";
        if (msg.Length == 0) throw new LeapError("empty message");
        ImSession s;
        if (a.Uuid("to") is { } to)
        {
            if (to == UUID.Zero) throw new LeapError("bad recipient");
            var sid = c.Self.AgentID ^ to;
            if (to == c.Self.AgentID) sid = c.Self.AgentID;
            s = S.State.Ims.GetOrAdd(sid, _ => new ImSession
            {
                Id = sid, Type = "p2p", OtherId = to, Name = S.Names.DisplayName(to) ?? to.ToString(),
            });
            c.Self.InstantMessage(to, msg, sid);
            S.ImOut(s, to, msg);
            return new JsonObject { ["session_id"] = sid.ToString() };
        }
        if (a.Uuid("group_id") is { } gid)
        {
            if (!S.IsMyGroup(gid)) throw new LeapError("not a member of that group");
            s = S.State.Ims.GetOrAdd(gid, _ => new ImSession
            {
                Id = gid, Type = "group", Name = S.State.Groups.TryGetValue(gid, out var g) ? g.Name : gid.ToString(), Ready = false,
            });
        }
        else if (a.Uuid("session_id") is { } sid2)
        {
            if (!S.State.Ims.TryGetValue(sid2, out s!)) throw new LeapError("no such session");
            if (s.Type == "p2p")
            {
                c.Self.InstantMessage(s.OtherId, msg, s.Id);
                S.ImOut(s, s.OtherId, msg);
                return new JsonObject { ["session_id"] = s.Id.ToString() };
            }
        }
        else throw new LeapError("need to, group_id or session_id");

        if (!s.Ready)
        {
            s.Started ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (s.Type == "group") c.Self.RequestJoinGroupChat(s.Id);
            try { await s.Started.Task.WaitAsync(TimeSpan.FromSeconds(20), ct); }
            catch (TimeoutException) { throw new LeapError("session didn't start (not sent)"); }
            catch (LeapError) { throw; }
            catch (Exception) { throw new LeapError("session didn't start (not sent)"); }
            finally { s.Started = null; }
        }
        c.Self.InstantMessageGroup(s.Id, msg);
        S.ImOut(s, s.Id, msg);
        return new JsonObject { ["session_id"] = s.Id.ToString() };
    }

    JsonNode? History(JsonObject a)
    {
        var id = a.Uuid("session_id") ?? throw new LeapError("no such session");
        if (!S.State.Ims.TryGetValue(id, out var s) || !Shown(s)) throw new LeapError("no such session");
        var count = a.Int("count", 20);
        List<ImLine> lines;
        lock (s.Lines) lines = s.Lines.Where(l => S.Private.ShownSpell(l.Spell)).TakeLast(count).ToList();
        var msgs = new JsonArray();
        foreach (var l in lines)
        {
            var t = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(l.Timestamp), Pacific);
            msgs.Add(new JsonObject
            {
                ["from"] = l.From, ["from_id"] = l.FromId.ToString(), ["message"] = l.Message,
                ["time"] = t.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture), ["timestamp"] = l.Timestamp,
            });
        }
        return new JsonObject { ["session"] = Info(s), ["messages"] = msgs };
    }

    async Task<JsonNode?> ConferenceAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var ids = a.UuidList("ids");
        if (ids.Count == 0) throw new LeapError("need ids");
        var tmp = UUID.Random();
        var s = S.State.Ims.GetOrAdd(tmp, _ => new ImSession { Id = tmp, Type = "conference", Name = "Conference", Ready = false });
        s.Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await c.Self.StartIMConferenceAsync(ids, tmp, ct);
        try { await s.Started.Task.WaitAsync(TimeSpan.FromSeconds(20), ct); }
        catch { S.State.Ims.TryRemove(tmp, out _); throw new LeapError("conference didn't start"); }
        if (a.Str("message") is { Length: > 0 } msg)
        {
            c.Self.InstantMessageGroup(s.Id, msg);
            S.ImOut(s, s.Id, msg);
        }
        return new JsonObject { ["session_id"] = s.Id.ToString() };
    }
}

// ======================================================================================================
// TesselObjects
// ======================================================================================================

public sealed class TesselObjects : SessionApi
{
    public override string Name => "TesselObjects";
    public override string Desc => "Objects in view: list, describe, properties, select, buy";

    public TesselObjects(AgentSession s) : base(s)
    {
        Op("nearby", "{dist=20, limit=40, fetch=true, timeout=4, attachments=false, query?, renew? (seconds: scripted objects' names "
            + "older than this asked again, as a script may have renamed them; 0 all of them)} -> {objects}", NearbyAsync);
        Op("get", "{ids, fetch=true, children=false, full=false, fresh=false (ask again even if known), timeout} -> {objects, missing?}; full adds "
            + "faces: {all: colour, alpha, texture, shiny, glow, fullbright, bump, repeats, tex_offset, tex_rotation, texgen; and each face "
            + "that differs, by number}, shape_asset: {sculpt_map, stitching, invert, mirror} or {mesh}, texture_anim if one runs", GetAsync);
        Op("find", "{prefix} -> {ids}", Find);
        Op("request", "{ids}: answers arrive as object_properties events", RequestAsync);
        Op("select", "{ids, add} -> {selected}", Select);
        Op("deselect", "{ids?}", Deselect);
        Op("selection", "-> {objects}", _ =>
        {
            var w = W;
            var objs = new JsonArray();
            foreach (var local in S.State.Selected.Keys)
                if (w.Prims.TryGetValue(local, out var p)) objs.Add(S.Objects.Describe(w, S.Objects.RootOf(w, p)));
            return new JsonObject { ["objects"] = objs };
        });
        Op("buy", "{id, price} -> {ok, balance} once the grid confirms a priced buy ({sent, confirmed: false} if it hasn't yet; an error if it refused)", BuyAsync);
    }

    async Task<JsonNode?> NearbyAsync(JsonObject a, CancellationToken ct)
    {
        var w = W;
        var dist = a.Num("dist", 20);
        var limit = a.Int("limit", 40);
        var fetch = a.Bool("fetch", true);
        var timeout = TimeSpan.FromSeconds(a.Num("timeout", 4));
        var attachments = a.Bool("attachments", false);
        var query = a.Str("query")?.ToLowerInvariant();
        var me = C.Self.SimPosition;
        // every region this agent sees (its own and connected neighbours), as the viewer lists them
        var cands = S.VisibleRegions()
            .SelectMany(rw => rw.Prims.Values.Where(p => S.Objects.Listable(rw, p, attachments))
                .Select(p => (w: rw, p, d: Vector3.Distance(rw.RegionPosition(p) + S.FrameOffset(rw), me))))
            .Where(x => x.d <= dist)
            .OrderBy(x => x.d)
            .Take(query is null ? limit : Math.Max(limit, 200))
            .ToList();
        // renew: scripted objects' names older than this many seconds asked again and waited for (a script renames its
        // object, and the region tells only those who have it selected); 0 renews every scripted one (a list's F5)
        double? renew = a["renew"] is null ? null : a.Num("renew", 0);
        var timedOut = false;
        if (fetch && cands.Count > 0)
            foreach (var g in cands.GroupBy(x => x.w))
            {
                var sim = g.Key.Simulators.FirstOrDefault(s => ReferenceEquals(s.Client, C));
                var again = new List<Primitive>();
                if (renew is { } secs)
                {
                    var scripted = S.Objects.ScriptedRoots(g.Key);
                    var before = DateTime.UtcNow - TimeSpan.FromSeconds(secs);
                    again = g.Where(x => scripted.Contains(x.p.LocalID) && ObjectViews.FetchedAt(g.Key, x.p) is var at && at != DateTime.MinValue && at <= before)
                        .Select(x => x.p).ToList();
                }
                var rest = g.Select(x => x.p).Except(again).ToList();
                var done = await Task.WhenAll(S.Objects.FetchAsync(g.Key, rest, timeout, ct, sim: sim), S.Objects.RenewAsync(g.Key, again, timeout, ct, sim));
                timedOut |= !done.All(x => x);
            }
        var objs = new JsonArray();
        var missing = new JsonArray();
        foreach (var (pw, p, _) in cands)
        {
            if (!pw.Prims.ContainsKey(p.LocalID)) { missing.Add(p.ID.ToString()); continue; }
            var d = S.Objects.Describe(pw, p);
            if (query is not null)
            {
                var hay = $"{d.Str("name")}\n{d.Str("desc")}\n{d.Str("text")}".ToLowerInvariant();
                if (!hay.Contains(query)) continue;
            }
            objs.Add(d);
            if (objs.Count >= limit) break;
        }
        var r = new JsonObject { ["objects"] = objs };
        if (missing.Count > 0) r["missing"] = missing;
        if (timedOut) r["timed_out"] = true;
        return r;
    }

    async Task<JsonNode?> GetAsync(JsonObject a, CancellationToken ct)
    {
        var full = a.Bool("full", false);
        var children = a.Bool("children", false);
        var timeout = TimeSpan.FromSeconds(a.Num("timeout", full ? 6 : 4));
        // the agent's own region first, then the neighbours it sees: a thing a few metres away across a border is as
        // near as any (it was "missing" by id while nearby listed it: Bay City, 2026-10-28)
        var own = W;
        var worlds = new[] { own }.Concat(S.VisibleRegions().Where(rw => rw != own)).ToList();
        var found = new List<(RegionWorld W, Primitive P)>();
        var missing = new JsonArray();
        foreach (var id in IdsOf(a))
        {
            var hit = worlds.Select(w => (W: w, P: w.PrimById(id))).FirstOrDefault(x => x.P != null);
            if (hit.P is { } p) found.Add((hit.W, p)); else missing.Add(id.ToString());
        }
        if (a.Bool("fetch", true) && found.Count > 0)
            await Task.WhenAll(found.GroupBy(x => x.W).Select(g => FetchIn(g.Key, g.Select(x => x.P).ToList())));
        var objs = new JsonArray(found.Select(x =>
        {
            var (w, p) = x;
            var o = S.Objects.Describe(w, p, children);
            if (w != own) o["region"] = w.Name;
            if (full && ObjectViews.FacesJson(p) is { } faces) o["faces"] = faces;   // what modify's face keys set
            if (full && ObjectViews.ShapeAssetJson(p) is { } shape) o["shape_asset"] = shape;
            if (full && ObjectViews.TextureAnimJson(p) is { } anim) o["texture_anim"] = anim;
            if (full && ObjectViews.ProbeOf?.Invoke(w.Handle, p.LocalID) is { } probe) o["reflection_probe"] = probe;
            return (JsonNode)o;
        }).ToArray());
        var r = new JsonObject { ["objects"] = objs };
        if (missing.Count > 0) r["missing"] = missing;
        return r;

        async Task FetchIn(RegionWorld w, List<Primitive> prims)
        {
            var toFetch = prims.ToList();
            if (children) foreach (var p in prims.Where(p => p.ParentID == 0)) toFetch.AddRange(S.Objects.Linkset(w, p).Skip(1));
            if (full) await S.Objects.FetchFullAsync(w, prims.Select(p => S.Objects.RootOf(w, p)).Distinct().ToList(), timeout, ct);
            // fresh: asked again even if what we have is recent (a script may have just renamed it)
            if (a.Bool("fresh", false)) await S.Objects.RenewAsync(w, toFetch, timeout, ct);
            else await S.Objects.FetchAsync(w, toFetch, timeout, ct);
        }
    }

    JsonNode? Find(JsonObject a)
    {
        var w = W;
        var prefix = (a.Str("prefix") ?? "").ToLowerInvariant();
        var ids = w.Prims.Values.Select(p => p.ID.ToString()).Where(s => s.StartsWith(prefix)).Take(20);
        return new JsonObject { ["ids"] = Json.Strings(ids) };
    }

    async Task<JsonNode?> RequestAsync(JsonObject a, CancellationToken ct)
    {
        var w = W;
        var prims = new List<Primitive>();
        foreach (var id in IdsOf(a))
        {
            if (w.PrimById(id) is not { } p) continue;
            S.State.PropsRequested[id] = DateTime.UtcNow;
            prims.Add(p);
        }
        _ = S.Objects.FetchAsync(w, prims, TimeSpan.Zero, ct, force: true);
        await Task.CompletedTask;
        return new JsonObject();
    }

    JsonNode? Select(JsonObject a)
    {
        var w = W;
        var c = C;
        if (!a.Bool("add", false) && !S.State.Selected.IsEmpty)
        {
            c.Objects.DeselectObjects(Sim, S.State.Selected.Keys.ToArray());
            S.State.Selected.Clear();
        }
        var locals = new List<uint>();
        foreach (var id in IdsOf(a))
        {
            if (w.PrimById(id) is not { } p) continue;
            foreach (var l in S.Objects.Linkset(w, S.Objects.RootOf(w, p))) { locals.Add(l.LocalID); S.State.Selected[l.LocalID] = 1; }
        }
        if (locals.Count > 0) c.Objects.SelectObjects(Sim, locals.Distinct().ToArray(), automaticDeselect: false);
        return new JsonObject { ["selected"] = locals.Count == 0 ? 0 : IdsOf(a).Count(id => w.PrimById(id) is not null) };
    }

    JsonNode? Deselect(JsonObject a)
    {
        var w = W;
        var ids = IdsOf(a);
        var locals = new List<uint>();
        if (ids.Count == 0) { locals.AddRange(S.State.Selected.Keys); S.State.Selected.Clear(); }
        else
            foreach (var id in ids)
            {
                if (w.PrimById(id) is not { } p) continue;
                foreach (var l in S.Objects.Linkset(w, S.Objects.RootOf(w, p))) { locals.Add(l.LocalID); S.State.Selected.TryRemove(l.LocalID, out _); }
            }
        if (locals.Count > 0) C.Objects.DeselectObjects(Sim, locals.Distinct().ToArray());
        return new JsonObject();
    }

    async Task<JsonNode?> BuyAsync(JsonObject a, CancellationToken ct)
    {
        var w = W;
        var c = C;
        var id = a.Uuid("id") ?? throw new LeapError("object not in view");
        var p = w.PrimById(id) ?? throw new LeapError("object not in view");
        var props = p.Properties ?? throw new LeapError("properties unknown; get the object first");
        if (props.SaleType == SaleType.Not) throw new LeapError("not for sale");
        var price = a.Num("price") ?? throw new LeapError("give the expected price");
        if (price != props.SalePrice) throw new LeapError($"price is L${props.SalePrice}, not L${price}");
        var folder = props.SaleType == SaleType.Contents ? S.Inventory.FolderFor(FolderType.Root) : S.Inventory.FolderFor(FolderType.Object);
        var pkt = new ObjectBuyPacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID, GroupID = c.Self.ActiveGroup, CategoryID = folder },
            ObjectData = new[] { new ObjectBuyPacket.ObjectDataBlock { ObjectLocalID = p.LocalID, SaleType = (byte)props.SaleType, SalePrice = props.SalePrice } },
        };
        var sim = Sim;
        var me = c.Self.AgentID;
        var o = new JsonObject { ["price"] = props.SalePrice };
        if (props.SalePrice == 0)
        {
            // nothing changes hands, so the grid says nothing; what's bought arrives in the inventory
            c.Network.SendPacket(pkt, sim);
            o["sent"] = true;
            o["note"] = "free: it arrives in the inventory if the region lets me take it";
            return o;
        }
        var reply = await PaymentReplyAsync(c, () => c.Network.SendPacket(pkt, sim), ti => ti.SourceID == me && ti.Amount == props.SalePrice, ct);
        if (reply is { Success: false }) throw new LeapError($"the grid refused the purchase: {reply.Description}");
        if (reply is null) { o["sent"] = true; o["confirmed"] = false; o["note"] = "the grid hasn't confirmed it yet; the balance and inventory show whether it went through"; }
        else { o["ok"] = true; o["balance"] = reply.Balance; }
        return o;
    }
}

// ======================================================================================================
// TesselBuild
// ======================================================================================================

public sealed class TesselBuild : SessionApi
{
    public override string Name => "TesselBuild";
    public override string Desc => "Building on single objects with the build tools' messages, no selection needed";

    public TesselBuild(AgentSession s) : base(s)
    {
        Op("create", "{shape=box, pos?, scale=[.5,.5,.5], rot?|rot_euler?, ...modify keys} -> {id, local_id}", (a, ct) => Wrap(S.Build.CreateAsync(a, ct)));
        Op("modify", "{id | ids, ...keys}: the keys create takes (an unknown key is refused); needs modify rights. Materials (on face or all): "
            + "pbr (a material item, {material?, base_color?, metallic?, roughness?, emissive?, alpha_mode?, alpha_cutoff?, double_sided?, "
            + "base_color_texture?, normal_texture?, metallic_roughness_texture?, emissive_texture?, repeats?, offset?, rotation?, reset?} or false), "
            + "normal {texture, repeats?, offset?, rotation?} or false, specular {texture, color?, glossiness? 0-255, environment? 0-255, repeats?, "
            + "offset?, rotation?} or false, alpha_mode (none|blend|mask|emissive, alpha_cutoff); projector {texture, fov? degrees, focus?, "
            + "ambiance?} or false. SojournBuild undo takes a modify back", Modify);
        Op("duplicate", "{id, offset=[0,0,.5], ...modify} -> {id}", (a, ct) => Wrap(S.Build.DuplicateAsync(a, ct)));
        Op("rez", "{item_id, pos?, ...modify} -> {id}", (a, ct) => Wrap(S.Build.RezAsync(a, ct)));
        Op("link", "{ids} -> {root_id, linked}: once the region has linked them", (a, ct) => Wrap(S.Build.LinkAsync(a, ct)));
        Op("unlink", "{ids} -> {unlinked}: a root unlinks its whole set", (a, ct) => Wrap(S.Build.UnlinkAsync(a, ct)));
        Op("take", "{ids, copy?, folder_id?, separate?: true (an item each, not one combined)} -> {count}: once the objects have left the world (a copy stays)", (a, ct) => Wrap(S.Build.DeRezAsync(a,
            a.Bool("copy", false) ? DeRezDestination.AgentInventoryCopy : DeRezDestination.AgentInventoryTake, ct)));
        Op("delete", "{ids} (to Trash) -> {count}: once they're gone", (a, ct) => Wrap(S.Build.DeRezAsync(a, DeRezDestination.TrashFolder, ct)));
        Op("returnObjects", "{ids} -> {count}: once they're gone", (a, ct) => Wrap(S.Build.DeRezAsync(a, DeRezDestination.ReturnToOwner, ct)));
        // sojourn: the Content tab (task inventory), which the viewer did by drag and drop
        Op("contents", "sojourn: {id, running=true} -> {items: [{item_id, name, type, perms, running?}]}: what's inside a prim", (a, ct) => Wrap(S.Contents.ContentsAsync(a, ct)));
        Op("addToContents", "sojourn: {id, item_id | item_ids, running=true, again?} -> {added}: put items from my inventory into a prim (scripts start running); "
            + "waits until the region shows each went in (gone_again: it went in and left, as a script that removes itself does); again: true to drop "
            + "the same item in again within 5 minutes of a drop whose outcome wasn't seen",
            (a, ct) => Wrap(S.Contents.AddAsync(a, ct)));
        Op("copyFromContents", "sojourn: {id, item_id | name, folder_id?} -> item: copy an item from a prim into my inventory", (a, ct) => Wrap(S.Contents.CopyOutAsync(a, ct)));
        Op("removeFromContents", "sojourn: {id, item_id | name}", (a, ct) => Wrap(S.Contents.RemoveAsync(a, ct)));
        Op("script", "sojourn: {id, item_id | name, running?, reset?} -> {running}: a script's running state; set or reset it", (a, ct) => Wrap(S.Contents.ScriptAsync(a, ct)));
    }

    static async Task<JsonNode?> Wrap(Task<JsonObject> t) => await t;

    async Task<JsonNode?> Modify(JsonObject a, CancellationToken ct)
    {
        var w = W;
        var ids = IdsOf(a);
        if (ids.Count == 0) throw new LeapError("need id or ids");
        var keys = new JsonObject();
        foreach (var (k, v) in a) if (k is not ("op" or "id" or "ids" or "reply" or "reqid")) keys[k] = v?.DeepClone();
        // one undo step for the call, however many prims (SojournBuild undo); materials go through capabilities, awaited
        var pending = new List<Task>();
        using (S.Undo.Batch($"modify of {ids.Count} prim{(ids.Count == 1 ? "" : "s")}"))
            foreach (var id in ids)
            {
                var p = w.PrimById(id) ?? throw new LeapError($"object not found: {id}");
                if (S.Build.Modify(p, (JsonObject)keys.DeepClone()) is { } t) pending.Add(t);
            }
        await Task.WhenAll(pending).WaitAsync(ct);
        return new JsonObject();
    }
}
