using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Sojourn.Core.Leap;
using Sojourn.Core.Scripts;

namespace Sojourn.Core.Agents;

/// <summary>
/// The viewer's Build > Scripts menu on a selection (llcompilequeue.cpp, Firestorm's extras): every script in the
/// objects named (their whole linksets by default) listed, recompiled for a target (Mono, LSL2, Lua, LSL on Luau),
/// reset, set running or not, or removed. One script at a time, as the queue goes, each reported. Claude Opus 5.5,
/// 2026-10-29.
/// </summary>
public sealed class ScriptChores
{
    readonly AgentSession _s;
    public ScriptChores(AgentSession s) => _s = s;

    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    /// <summary>The prims a chore works on: each id's whole linkset, or only the prims named with `prims_only`.</summary>
    public List<Primitive> Prims(JsonObject a)
    {
        var w = _s.Region ?? throw new LeapError("no region");
        var ids = a.UuidList("ids");
        if (a.Uuid("id") is { } one && !ids.Contains(one)) ids.Add(one);
        if (ids.Count == 0) throw new LeapError("need id or ids (the objects)");
        var prims = new List<Primitive>();
        foreach (var id in ids)
        {
            var p = w.PrimById(id) ?? throw new LeapError($"object not found in this region: {id}");
            var set = a.Bool("prims_only", false) ? new List<Primitive> { p } : _s.Objects.Linkset(w, _s.Objects.RootOf(w, p));
            foreach (var x in set) if (!prims.Any(q => q.LocalID == x.LocalID)) prims.Add(x);
        }
        return prims;
    }

    async Task<List<(Primitive Prim, InventoryItem Item)>> ScriptsAsync(List<Primitive> prims, string? name, CancellationToken ct)
    {
        var all = new List<(Primitive, InventoryItem)>();
        foreach (var p in prims)
        {
            List<InventoryBase> items;
            try { items = await _s.Scripts.TaskInventoryAsync(p, ct); }
            catch (ScriptEditor.RpcFail e) { throw new LeapError($"{p.Properties?.Name ?? p.ID.ToString()}: {e.Message}"); }
            foreach (var it in items.OfType<InventoryItem>().Where(i => i.AssetType == AssetType.LSLText))
                if (name is null || string.Equals(it.Name, name, StringComparison.OrdinalIgnoreCase)) all.Add((p, it));
        }
        return all;
    }

    JsonObject Row(Primitive p, InventoryItem it) => new()
    {
        ["prim_id"] = p.ID.ToString(), ["prim"] = p.Properties?.Name ?? "", ["item_id"] = it.UUID.ToString(), ["name"] = it.Name,
    };

    public async Task<JsonObject> ListAsync(JsonObject a, CancellationToken ct)
    {
        var list = await ScriptsAsync(Prims(a), a.Str("name"), ct);
        var arr = new JsonArray();
        int running = 0;
        foreach (var (p, it) in list)
        {
            var r = Row(p, it);
            var on = await _s.Scripts.RunningAsync(p.ID, it.UUID, ct);
            if (on) running++;
            r["running"] = on;
            r["perms"] = ObjectViews.PermLetters(it.Permissions.OwnerMask);
            arr.Add(r);
        }
        return new JsonObject { ["scripts"] = arr, ["count"] = list.Count, ["running"] = running };
    }

    /// <summary>Recompile: each script's text read and saved back for the target, as the compile queue does (it needs
    /// copy and modify rights on the script; others are skipped and said so).</summary>
    public async Task<JsonObject> RecompileAsync(JsonObject a, CancellationToken ct)
    {
        var target = (a.Str("target") ?? "mono").Trim().ToLowerInvariant();
        if (!ScriptEditor.Targets.Contains(target)) throw new LeapError($"target: {string.Join(", ", ScriptEditor.Targets)}");
        bool toLuau = target is "luau" or "lsl-luau";
        if (toLuau && _s.Scripts.LuaEnabled == false)
            throw new LeapError("this region doesn't run Lua (its LuaScriptsEnabled feature is off), so luau and lsl-luau can't be compiled here");
        var list = await ScriptsAsync(Prims(a), a.Str("name"), ct);
        if (list.Count == 0) throw new LeapError("no scripts in those objects");
        var done = new JsonArray();
        var failed = new JsonArray();
        var skipped = new JsonArray();
        foreach (var (p, it) in list)
        {
            var row = Row(p, it);
            if ((it.Permissions.OwnerMask & (PermissionMask.Copy | PermissionMask.Modify)) != (PermissionMask.Copy | PermissionMask.Modify))
            {
                row["why"] = "needs copy and modify rights to recompile (the viewer's queue skips it too)";
                skipped.Add(row);
                continue;
            }
            // Lua text compiles only as luau, and LSL only as the other three (the queue skips the rest)
            if (ScriptEditor.IsLua(it) != (target == "luau"))
            {
                row["why"] = ScriptEditor.IsLua(it) ? "it's a Lua script: only luau compiles it" : "it's an LSL script: luau is for Lua (lsl-luau runs LSL on Luau)";
                skipped.Add(row);
                continue;
            }
            try
            {
                var text = await _s.Scripts.ScriptTextAsync(p, it, ct);
                var running = a.Bool("running") ?? await _s.Scripts.RunningAsync(p.ID, it.UUID, ct);
                var (compiled, diags) = await _s.Scripts.CompileTaskAsync(p, it, text, target, running, ct);
                if (compiled) done.Add(row);
                else
                {
                    row["diagnostics"] = diags;
                    failed.Add(row);
                }
            }
            catch (ScriptEditor.RpcFail e)
            {
                row["error"] = e.Message;
                failed.Add(row);
            }
        }
        var o = new JsonObject { ["target"] = target, ["compiled"] = done, ["count"] = list.Count };
        if (failed.Count > 0) o["failed"] = failed;
        if (skipped.Count > 0) o["skipped"] = skipped;
        return o;
    }

    public async Task<JsonObject> ResetAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var list = await ScriptsAsync(Prims(a), a.Str("name"), ct);
        if (list.Count == 0) throw new LeapError("no scripts in those objects");
        foreach (var (p, it) in list)
        {
            c.Network.SendPacket(new ScriptResetPacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
                Script = { ObjectID = p.ID, ItemID = it.UUID },
            }, c.Network.CurrentSim);
            await Task.Delay(50, ct);
        }
        return new JsonObject { ["reset"] = new JsonArray(list.Select(x => (JsonNode)Row(x.Prim, x.Item)).ToArray()), ["count"] = list.Count };
    }

    public async Task<JsonObject> SetRunningAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var on = a.Bool("running") ?? throw new LeapError("running: true or false");
        var list = await ScriptsAsync(Prims(a), a.Str("name"), ct);
        if (list.Count == 0) throw new LeapError("no scripts in those objects");
        foreach (var (p, it) in list)
        {
            c.Inventory.RequestSetScriptRunning(p.ID, it.UUID, on);
            await Task.Delay(50, ct);
        }
        await Task.Delay(400, ct);
        int now = 0;
        foreach (var (p, it) in list) if (await _s.Scripts.RunningAsync(p.ID, it.UUID, ct) == on) now++;
        var o = new JsonObject { ["running"] = on, ["count"] = list.Count, ["changed"] = now };
        if (now < list.Count) o["note"] = $"{list.Count - now} didn't change (no modify rights on the object, or the region's scripts are off)";
        return o;
    }

    /// <summary>For the confirmation: the scripts a removal would take, as it will find them.</summary>
    public async Task<List<(Primitive Prim, InventoryItem Item)>> RemovalAsync(JsonObject a, CancellationToken ct) =>
        await ScriptsAsync(Prims(a), a.Str("name"), ct);

    public async Task<JsonObject> RemoveAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var list = await ScriptsAsync(Prims(a), a.Str("name"), ct);
        if (list.Count == 0) throw new LeapError("no scripts in those objects");
        foreach (var (p, it) in list)
        {
            c.Inventory.RemoveTaskInventory(p.LocalID, it.UUID, c.Network.CurrentSim!);
            await Task.Delay(100, ct);
        }
        // seen gone in the listings (the region checks modify rights)
        await Task.Delay(600, ct);
        var left = await ScriptsAsync(list.Select(x => x.Prim).Distinct().ToList(), a.Str("name"), ct);
        var gone = list.Count(x => left.All(l => l.Item.UUID != x.Item.UUID));
        var o = new JsonObject { ["removed"] = gone, ["count"] = list.Count };
        if (gone < list.Count) o["note"] = $"{list.Count - gone} still there (removing needs modify rights on the object)";
        return o;
    }
}
