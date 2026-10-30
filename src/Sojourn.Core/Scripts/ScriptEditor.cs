using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Scripts;

/// <summary>
/// The viewer's external-editor JSON-RPC (doc/external-editor-json-rpc.md), served at /rpc without a websocket:
/// object.list / object.request / object.item.create / object.content.save / object.content.get, done with the
/// protocol directly (task inventory, RezScript, the UpdateScriptTask / UpdateNotecardTaskInventory caps, asset
/// fetches). Notifications the viewer would push (object.publish, runtime.error/debug) become "editor" events.
/// </summary>
public sealed class ScriptEditor
{
    readonly AgentSession _s;
    readonly ConcurrentDictionary<UUID, JsonObject> _published = new();       // root id -> published object
    readonly ConcurrentDictionary<UUID, string> _vm = new();                  // script item id -> vm we saved it as
    readonly ConcurrentDictionary<UUID, SemaphoreSlim> _createGate = new();
    readonly ConcurrentDictionary<UUID, UUID> _savedAsset = new();             // item id -> asset our last save made

    public ScriptEditor(AgentSession s) => _s = s;

    sealed class Finally(Action a) : IDisposable { public void Dispose() => a(); }

    internal sealed class RpcFail(int code, string message) : Exception(message) { public int Code { get; } = code; }

    GridClient C => _s.Client ?? throw new RpcFail(-32603, "Internal error: not logged in");

    public async Task<(int Status, JsonNode Result)> RpcAsync(string method, JsonObject? p, TimeSpan timeout, CancellationToken ct)
    {
        p ??= new JsonObject();
        if (!_s.Online) return (503, Json.Error("viewer not connected"));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var _ = new Finally(() => { if (sw.ElapsedMilliseconds > 2000) _s.Log.LogInformation("rpc {Method} took {Ms} ms", method, sw.ElapsedMilliseconds); });
        try
        {
            // new or changed scripts act as the person in-world: the host's confirmation gate decides, as for ops
            if (method is "object.item.create" or "object.content.save"
                && await _s.Api.ConfirmAsync("ScriptEditor", method, p, ct) is { } refusal)
                throw new RpcFail(-32003, $"Forbidden: {refusal}");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            JsonNode result = method switch
            {
                "object.list" => await ListAsync(cts.Token),
                "object.request" => await RequestAsync(p, cts.Token),
                "object.item.create" => await CreateItemAsync(p, cts.Token),
                "object.content.save" => await SaveAsync(p, cts.Token),
                "object.content.get" => await GetAsync(p, cts.Token),
                "sojourn.task_inventory" => await RawInventoryAsync(p, cts.Token),
                _ => throw new RpcFail(-32601, $"Method not found: {method}"),
            };
            return (200, new JsonObject { ["result"] = result });
        }
        catch (RpcFail f)
        {
            return (400, new JsonObject { ["rpc_error"] = new JsonObject { ["code"] = f.Code, ["message"] = f.Message } });
        }
        catch (OperationCanceledException)
        {
            return (400, new JsonObject { ["rpc_error"] = new JsonObject { ["code"] = -32001, ["message"] = "Timeout: the request took too long" } });
        }
        catch (Exception e)
        {
            _s.Log.LogWarning(e, "rpc {Method}", method);
            return (503, Json.Error($"{e.GetType().Name}: {e.Message}"));
        }
    }

    // ---------- publishing (the object's contents as the editor sees them) ----------

    Primitive Root(UUID id, out Agents.ObjectViews ov, out World.RegionWorld w)
    {
        w = _s.Region ?? throw new RpcFail(-32603, "Internal error: no region");
        ov = _s.Objects;
        var p = w.PrimById(id) ?? throw new RpcFail(-32602, "Invalid params: Object not found");
        return ov.RootOf(w, p);
    }

    async Task<JsonObject> PublishAsync(UUID objectId, CancellationToken ct)
    {
        var root = Root(objectId, out var ov, out var w);
        var c = C;
        await ov.FetchAsync(w, ov.Linkset(w, root), TimeSpan.FromSeconds(3), ct);
        var obj = new JsonObject
        {
            ["object_id"] = root.ID.ToString(),
            ["object_name"] = root.Properties?.Name ?? "Object",
            ["object_description"] = root.Properties?.Description ?? "",
            ["owner_id"] = (root.Properties?.OwnerID ?? UUID.Zero).ToString(),
            ["region"] = c.Network.CurrentSim?.Name ?? "",
            ["inventory"] = await InventoryAsync(root, ct),
        };
        if (root.Properties is { } rp)
            obj["permissions"] = new JsonObject { ["owner"] = (uint)rp.Permissions.OwnerMask, ["next_owner"] = (uint)rp.Permissions.NextOwnerMask };
        var links = ov.Linkset(w, root).Skip(1).ToList();
        if (links.Count > 0)
        {
            var arr = new JsonArray();
            var n = 2;
            foreach (var l in links)
                arr.Add(new JsonObject
                {
                    ["link_id"] = l.ID.ToString(), ["link_number"] = n++, ["link_name"] = l.Properties?.Name ?? "Object",
                    ["link_description"] = l.Properties?.Description ?? "", ["inventory"] = await InventoryAsync(l, ct),
                });
            obj["linked_objects"] = arr;
        }
        _published[root.ID] = obj;
        return obj;
    }

    async Task<JsonArray> InventoryAsync(Primitive prim, CancellationToken ct)
    {
        var c = C;
        var items = await TaskInventoryAsync(prim, ct);
        var arr = new JsonArray();
        foreach (var it in items.OfType<InventoryItem>())
        {
            if (it.AssetType is not (AssetType.LSLText or AssetType.Notecard)) continue;
            var script = it.AssetType == AssetType.LSLText;
            var o = new JsonObject
            {
                ["item_id"] = it.UUID.ToString(), ["name"] = it.Name, ["description"] = it.Description,
                ["type"] = script ? "script" : "notecard",
                ["permissions"] = new JsonObject { ["owner"] = (uint)it.Permissions.OwnerMask, ["next_owner"] = (uint)it.Permissions.NextOwnerMask },
                ["creator_id"] = it.CreatorID.ToString(),
            };
            if (script)
            {
                var vm = _vm.TryGetValue(it.UUID, out var v) ? v : null;
                o["subtype"] = vm == "luau" ? 1 : 0;
                if (vm is not null) o["vm"] = vm;
                o["running"] = await RunningAsync(prim.ID, it.UUID, ct);
                o["faulted"] = false;
            }
            arr.Add(o);
        }
        return arr;
    }

    readonly SemaphoreSlim _taskGate = new(1, 1);

    /// <summary>A prim's contents. One fetch at a time per agent (concurrent transfers of task inventory files
    /// confuse each other), each with its own timeout and one retry: LibreMetaverse waits forever for a transfer
    /// that never comes and reports a cancellation as an empty inventory.</summary>
    internal async Task<List<InventoryBase>> TaskInventoryAsync(Primitive prim, CancellationToken ct)
    {
        var c = C;
        await _taskGate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                try
                {
                    // Second Life has a capability for this; LibreMetaverse's path for it is fine.
                    if (c.Network.CurrentSim?.Caps?.CapabilityURI("RequestTaskInventory") is not null)
                    {
                        var list = await c.Inventory.GetTaskInventoryAsync(prim.ID, prim.LocalID, c.Network.CurrentSim, cts.Token);
                        if (!cts.IsCancellationRequested) return list ?? new List<InventoryBase>();
                    }
                    else return await TaskInventoryUdpAsync(c, prim, cts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                ct.ThrowIfCancellationRequested();
                _s.Log.LogInformation("task inventory of {Prim} timed out; asking again", prim.ID);
            }
            throw new RpcFail(-32001, "Timeout: the region didn't send the object's contents");
        }
        finally { _taskGate.Release(); }
    }

    /// <summary>RequestTaskInventory, then the transfer of the file it names (see <see cref="XferFetch"/>).</summary>
    async Task<List<InventoryBase>> TaskInventoryUdpAsync(GridClient c, Primitive prim, CancellationToken ct)
    {
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReply(object? _, TaskInventoryReplyEventArgs e) { if (e.ItemID == prim.ID) reply.TrySetResult(e.AssetFilename); }
        c.Inventory.TaskInventoryReply += OnReply;
        try
        {
            using var reg = ct.Register(() => reply.TrySetCanceled());
            c.Inventory.RequestTaskInventory(prim.LocalID, c.Network.CurrentSim);
            var file = await reply.Task;
            if (string.IsNullOrEmpty(file)) return new List<InventoryBase>();
            var data = await _s.Xfer.FetchAsync(file, ct);
            return InventoryManager.ParseTaskInventory(Utils.BytesToString(data));
        }
        finally { c.Inventory.TaskInventoryReply -= OnReply; }
    }

    internal async Task<bool> RunningAsync(UUID objectId, UUID itemId, CancellationToken ct)
    {
        var c = C;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, ScriptRunningReplyEventArgs e)
        {
            if (e.ObjectID == objectId && e.ScriptID == itemId) tcs.TrySetResult(e.IsRunning);
        }
        c.Inventory.ScriptRunningReply += Handler;
        try
        {
            c.Inventory.RequestGetScriptRunning(objectId, itemId);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);
        }
        catch (TimeoutException) { return false; }
        finally { c.Inventory.ScriptRunningReply -= Handler; }
    }

    async Task<JsonNode> ListAsync(CancellationToken ct)
    {
        // The viewer keeps published listings fresh by watching inventory serials; re-read them instead.
        var arr = new JsonArray();
        foreach (var id in _published.Keys.ToArray())
        {
            try { arr.Add(await PublishAsync(id, ct)); }
            catch (RpcFail) { _published.TryRemove(id, out _); }
        }
        return new JsonObject { ["objects"] = arr };
    }

    async Task<JsonNode> RequestAsync(JsonObject p, CancellationToken ct)
    {
        var id = p.Uuid("object_id") ?? throw new RpcFail(-32602, "Invalid params: No object_id specified");
        var obj = await PublishAsync(id, ct);
        _s.Events.Log("editor", new JsonObject { ["method"] = "object.publish", ["params"] = new JsonObject { ["object"] = obj.DeepClone() } });
        return new JsonObject { ["success"] = true };
    }

    // ---------- creating items ----------

    async Task<JsonNode> CreateItemAsync(JsonObject p, CancellationToken ct)
    {
        var c = C;
        var primId = p.Uuid("prim_id") ?? throw new RpcFail(-32602, "Invalid params: prim_id required");
        var name = p.Str("name") ?? throw new RpcFail(-32602, "Invalid params: name required");
        var type = p.Str("type") ?? "script";
        if (type is not ("script" or "notecard")) throw new RpcFail(-32602, "Invalid params: type must be script or notecard");
        var vm = p.Str("vm");
        if (type == "script" && vm is null) throw new RpcFail(-32602, "Invalid params: vm required for scripts");
        var w = _s.Region ?? throw new RpcFail(-32603, "Internal error: no region");
        var prim = w.PrimById(primId) ?? throw new RpcFail(-32602, "Invalid params: Object not found");
        var gate = _createGate.GetOrAdd(primId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) throw new RpcFail(-32600, "Invalid request: An item.create is already in flight for this prim");
        try
        {
            var before = (await TaskInventoryAsync(prim, ct))
                .Select(i => i.UUID).ToHashSet();
            var item = new InventoryItem(type == "script" ? InventoryType.LSL : InventoryType.Notecard, UUID.Zero)
            {
                // a task item's folder is its prim (OpenSim finds the prim for a new script this way)
                ParentUUID = prim.ID,
                Name = name, Description = "", AssetType = type == "script" ? AssetType.LSLText : AssetType.Notecard,
                OwnerID = c.Self.AgentID, CreatorID = c.Self.AgentID, CreationDate = DateTime.UtcNow,
                // (base, everyone, group, next owner, owner): the viewer's new-script masks
                Permissions = new Permissions((uint)PermissionMask.All, 0, 0, (uint)(PermissionMask.Move | PermissionMask.Transfer), (uint)PermissionMask.All),
            };
            if (type == "script") c.Inventory.CopyScriptToTask(prim.LocalID, item, true);
            else c.Inventory.UpdateTaskInventory(prim.LocalID, item, c.Network.CurrentSim, only_mod_meta: false);
            // the region renames clashes ("Name 1"); find the new item by elimination
            InventoryItem? created = null;
            var end = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (created is null && DateTime.UtcNow < end)
            {
                await Task.Delay(500, ct);
                var now = await TaskInventoryAsync(prim, ct);
                created = now.OfType<InventoryItem>().FirstOrDefault(i => !before.Contains(i.UUID)
                    && i.AssetType == item.AssetType);
            }
            if (created is null) throw new RpcFail(-32001, "Timeout: Timed out waiting for item creation");
            if (type == "script" && created.Name != name)
            {
                // a new script is born "New Script": give it the asked-for name
                created.Name = name;
                c.Inventory.UpdateTaskInventory(prim.LocalID, created, c.Network.CurrentSim, only_mod_meta: true);
                await Task.Delay(300, ct);
            }
            if (vm is not null) _vm[created.UUID] = vm;
            if (type == "notecard" && p.Str("text") is { } text) await SaveNotecardAsync(prim, created.UUID, text, ct);
            var result = new JsonObject
            {
                ["item_id"] = created.UUID.ToString(), ["name"] = created.Name, ["description"] = created.Description,
                ["type"] = type, ["prim_id"] = primId.ToString(), ["creator_id"] = created.CreatorID.ToString(),
                ["permissions"] = new JsonObject { ["owner"] = (uint)created.Permissions.OwnerMask, ["next_owner"] = (uint)created.Permissions.NextOwnerMask },
            };
            if (type == "script") { result["subtype"] = vm == "luau" ? 1 : 0; result["vm"] = vm; result["running"] = true; result["faulted"] = false; }
            return result;
        }
        finally { gate.Release(); }
    }

    // ---------- saving and reading content ----------

    static readonly Regex LslDiag = new(@"\((\d+), (\d+)\) : ([^:]+) : (.+)");
    static readonly Regex LuauDiag = new(@"^([^:]*):([0-9]+):\s*(.*)$");

    async Task<JsonNode> SaveAsync(JsonObject p, CancellationToken ct)
    {
        var c = C;
        var primId = p.Uuid("prim_id") ?? throw new RpcFail(-32602, "Invalid params: prim_id required");
        var itemId = p.Uuid("item_id") ?? throw new RpcFail(-32602, "Invalid params: item_id required");
        var content = p.Str("content") ?? "";
        var w = _s.Region ?? throw new RpcFail(-32603, "Internal error: no region");
        var prim = w.PrimById(primId) ?? throw new RpcFail(-32602, "Invalid params: Object not found");
        var items = await TaskInventoryAsync(prim, ct);
        var item = items.OfType<InventoryItem>().FirstOrDefault(i => i.UUID == itemId)
                   ?? throw new RpcFail(-32602, "Invalid params: Item not found in object");
        if (item.AssetType == AssetType.Notecard)
        {
            await SaveNotecardAsync(prim, itemId, content, ct);
            return new JsonObject { ["success"] = true, ["prim_id"] = primId.ToString(), ["item_id"] = itemId.ToString() };
        }
        var vm = p.Str("vm") ?? (_vm.TryGetValue(itemId, out var v) ? v : "mono");
        var running = p.Bool("running") ?? await RunningAsync(prim.ID, itemId, ct);
        var (compiled, diags) = await CompileTaskAsync(prim, item, content, vm, running, ct);
        var r = new JsonObject
        {
            ["success"] = true, ["prim_id"] = primId.ToString(), ["item_id"] = itemId.ToString(), ["compiled"] = compiled,
        };
        if (!compiled) r["diagnostics"] = diags;
        return r;
    }

    /// <summary>The viewer's compile targets for UpdateScriptTask / UpdateScriptAgent's `target`: Mono, LSL2, Lua
    /// (SLua) and LSL compiled to Luau.</summary>
    public static readonly string[] Targets = { "mono", "lsl2", "luau", "lsl-luau" };

    /// <summary>A script in a prim saved and compiled for a target (UpdateScriptTask: the ask, then the text to the
    /// uploader it names): whether it compiled, and the compiler's diagnostics if not.</summary>
    internal async Task<(bool Compiled, JsonArray Diagnostics)> CompileTaskAsync(Primitive prim, InventoryItem item, string content, string vm, bool running,
        CancellationToken ct)
    {
        var c = C;
        var itemId = item.UUID;
        var cap = c.Network.CurrentSim?.Caps?.CapabilityURI("UpdateScriptTask") ?? throw new RpcFail(-32603, "Internal error: no UpdateScriptTask capability");
        var req = new OSDMap
        {
            ["item_id"] = OSD.FromUUID(itemId), ["task_id"] = OSD.FromUUID(prim.ID),
            ["is_script_running"] = OSD.FromBoolean(running), ["target"] = OSD.FromString(vm),
        };
        // a script tied to an experience keeps it (the viewer sends the one it's associated with; without it the save drops it)
        if (await ExperienceOfAsync(prim.ID, itemId, ct) is { } exp) req["experience"] = OSD.FromUUID(exp);
        var (_, first) = await c.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, req, ct);
        var state = ParseMap(first);
        if (state["state"].AsString() != "upload") throw new RpcFail(-32603, $"Upload failed: {Describe(state, first)}");
        var (_, second) = await c.HttpCapsClient.PostAsync(new Uri(state["uploader"].AsString()), "application/octet-stream",
            Encoding.UTF8.GetBytes(content), ct);
        var done = ParseMap(second);
        if (done["state"].AsString() != "complete") throw new RpcFail(-32603, $"Upload failed: {Describe(done, second)}");
        _vm[itemId] = vm;
        // (OpenSim answers new_asset with the item id; only a real asset id is worth remembering)
        if (done.ContainsKey("new_asset") && done["new_asset"].AsUUID() is var na && na != itemId && na != UUID.Zero) _savedAsset[itemId] = na;
        else
        {
            // OpenSim: no asset id comes back, and the contents listing it caches by inventory serial isn't
            // regenerated for a script save. Touch the item's metadata so the serial moves and listings
            // (and so reads of the new text) are current.
            _savedAsset.TryRemove(itemId, out _);
            c.Inventory.UpdateTaskInventory(prim.LocalID, item, c.Network.CurrentSim, only_mod_meta: true);
        }
        var compiled = !done.ContainsKey("compiled") || done["compiled"].AsBoolean();
        return (compiled, Diagnostics(done, vm));
    }

    /// <summary>The experience a script in a prim is associated with (the GetMetadata capability, as
    /// LLExperienceCache::fetchAssociatedExperience asks), or null (none, or no capability: OpenSim).</summary>
    internal async Task<UUID?> ExperienceOfAsync(UUID objectId, UUID itemId, CancellationToken ct)
    {
        var c = C;
        if (c.Network.CurrentSim?.Caps?.CapabilityURI("GetMetadata") is not { } cap) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var (resp, body) = await c.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, new OSDMap
            {
                ["object-id"] = OSD.FromUUID(objectId), ["item-id"] = OSD.FromUUID(itemId),
                ["fields"] = new OSDArray(new List<OSD> { OSD.FromString("experience") }),
            }, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            return ParseMap(body) is { } m && m.ContainsKey("experience") && m["experience"].AsUUID() is var id && id != UUID.Zero ? id : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { return null; }
    }

    /// <summary>A script in my inventory saved and compiled for a target (UpdateScriptAgent {item_id, target}, then the
    /// text to the uploader): LibreMetaverse's own takes only Mono or not, so Lua couldn't be asked for.</summary>
    public async Task<(bool Compiled, JsonArray Diagnostics, UUID Asset)> CompileAgentAsync(UUID itemId, string text, string target, CancellationToken ct)
    {
        var c = C;
        if (!Targets.Contains(target)) throw new RpcFail(-32602, $"target: {string.Join(", ", Targets)}");
        var cap = c.Network.CurrentSim?.Caps?.CapabilityURI("UpdateScriptAgent") ?? throw new RpcFail(-32603, "no UpdateScriptAgent capability");
        var (_, first) = await c.HttpCapsClient.PostAsync(cap, OSDFormat.Xml,
            new OSDMap { ["item_id"] = OSD.FromUUID(itemId), ["target"] = OSD.FromString(target) }, ct);
        var state = ParseMap(first);
        if (state["state"].AsString() != "upload") throw new RpcFail(-32603, $"Upload failed: {Describe(state, first)}");
        var (_, second) = await c.HttpCapsClient.PostAsync(new Uri(state["uploader"].AsString()), "application/octet-stream", Encoding.UTF8.GetBytes(text), ct);
        var done = ParseMap(second);
        if (done["state"].AsString() != "complete") throw new RpcFail(-32603, $"Upload failed: {Describe(done, second)}");
        var compiled = !done.ContainsKey("compiled") || done["compiled"].AsBoolean();
        return (compiled, Diagnostics(done, target), done.ContainsKey("new_asset") ? done["new_asset"].AsUUID() : UUID.Zero);
    }

    /// <summary>Whether the region runs Lua (its LuaScriptsEnabled simulator feature, which the viewer offers luau and
    /// lsl-luau by); null while the features haven't arrived.</summary>
    public bool? LuaEnabled => _s.Client?.Network.CurrentSim?.Features is { } f && f.Get("LuaScriptsEnabled") is { } v ? v.AsBoolean() : null;

    /// <summary>A script item is Lua by its inventory subtype (SST_LUA, the flags' low byte).</summary>
    public static bool IsLua(InventoryItem it) => (it.Flags & 0xff) == 1;

    /// <summary>A compile's errors as the viewer parses them (row and column from 1, level, message).</summary>
    internal static JsonArray Diagnostics(OSDMap done, string vm)
    {
        var diags = new JsonArray();
        if (done.TryGetValue("errors", out var errs) && errs is OSDArray arr)
            foreach (var e in arr)
            {
                var msg = e.AsString().Trim();
                if (LslDiag.Match(msg) is { Success: true } m)
                    diags.Add(new JsonObject
                    {
                        ["row"] = int.Parse(m.Groups[1].Value) + 1, ["column"] = int.Parse(m.Groups[2].Value) + 1,
                        ["level"] = m.Groups[3].Value.Trim(), ["message"] = m.Groups[4].Value.Trim(), ["format"] = "lsl",
                    });
                else if (vm == "luau" && LuauDiag.Match(msg) is { Success: true } lm)
                    diags.Add(new JsonObject { ["row"] = int.Parse(lm.Groups[2].Value), ["column"] = 0, ["level"] = "ERROR", ["message"] = lm.Groups[3].Value });
                else
                    diags.Add(new JsonObject { ["row"] = 0, ["column"] = 0, ["level"] = "ERROR", ["message"] = msg });
            }
        return diags;
    }

    /// <summary>A script's text as stored (needs copy and modify on it, as the viewer's editor and compile queue ask).</summary>
    internal async Task<string> ScriptTextAsync(Primitive prim, InventoryItem item, CancellationToken ct)
    {
        var c = C;
        var assetId = _savedAsset.TryGetValue(item.UUID, out var saved) ? saved : item.AssetUUID;
        var asset = await c.Assets.RequestInventoryAssetAsync(assetId, item.UUID, prim.ID, item.OwnerID, item.AssetType, true, UUID.Random(), ct)
                    ?? throw new RpcFail(-32001, "Timeout: asset fetch failed");
        return Encoding.UTF8.GetString(asset.AssetData ?? Array.Empty<byte>()).TrimEnd('\0');
    }

    static OSDMap ParseMap(byte[]? data)
    {
        try { return OSDParser.Deserialize(data ?? Array.Empty<byte>()) as OSDMap ?? new OSDMap(); }
        catch { return new OSDMap(); }
    }

    static string Describe(OSDMap m, byte[]? raw) =>
        m.Count > 0 ? OSDParser.SerializeJsonString(m) : $"unexpected reply: {Encoding.UTF8.GetString(raw ?? Array.Empty<byte>())[..Math.Min(200, raw?.Length ?? 0)]}";

    async Task SaveNotecardAsync(Primitive prim, UUID itemId, string text, CancellationToken ct)
    {
        var c = C;
        var (ok, status, _, asset) = await c.Inventory.RequestUpdateNotecardTaskAsync(NotecardAsset(text), itemId, prim.ID, ct);
        if (!ok) throw new RpcFail(-32603, $"Upload failed: {status}");
        if (asset != UUID.Zero && asset != itemId) _savedAsset[itemId] = asset; else _savedAsset.TryRemove(itemId, out _);
    }

    static byte[] NotecardAsset(string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var head = $"Linden text version 2\n{{\nLLEmbeddedItems version 1\n{{\ncount 0\n}}\nText length {body.Length}\n";
        return Encoding.UTF8.GetBytes(head).Concat(body).Concat(Encoding.UTF8.GetBytes("}\n")).ToArray();
    }

    static string NotecardText(string asset)
    {
        var m = Regex.Match(asset, @"Text length (\d+)\n", RegexOptions.Singleline);
        if (!m.Success) return asset;
        var start = m.Index + m.Length;
        var bytes = Encoding.UTF8.GetBytes(asset[start..]);
        var len = Math.Min(int.Parse(m.Groups[1].Value), bytes.Length);
        return Encoding.UTF8.GetString(bytes, 0, len);
    }

    async Task<JsonNode> GetAsync(JsonObject p, CancellationToken ct)
    {
        var c = C;
        var primId = p.Uuid("prim_id") ?? throw new RpcFail(-32602, "Invalid params: prim_id required");
        var itemId = p.Uuid("item_id") ?? throw new RpcFail(-32602, "Invalid params: item_id required");
        var w = _s.Region ?? throw new RpcFail(-32603, "Internal error: no region");
        var prim = w.PrimById(primId) ?? throw new RpcFail(-32602, "Invalid params: Object not found");
        var items = await TaskInventoryAsync(prim, ct);
        var item = items.OfType<InventoryItem>().FirstOrDefault(i => i.UUID == itemId)
                   ?? throw new RpcFail(-32602, "Invalid params: Item not found in object");
        if ((item.Permissions.OwnerMask & (PermissionMask.Copy | PermissionMask.Modify)) != (PermissionMask.Copy | PermissionMask.Modify)
            && item.AssetType == AssetType.LSLText)
            throw new RpcFail(-32003, "Forbidden: Insufficient permissions");
        // the region's listing can lag a save (OpenSim doesn't bump the inventory serial for it): prefer the asset we made
        var assetId = _savedAsset.TryGetValue(itemId, out var saved) ? saved : item.AssetUUID;
        var asset = await c.Assets.RequestInventoryAssetAsync(assetId, item.UUID, prim.ID, item.OwnerID, item.AssetType, true, UUID.Random(), ct)
                    ?? throw new RpcFail(-32001, "Timeout: asset fetch failed");
        var text = Encoding.UTF8.GetString(asset.AssetData ?? Array.Empty<byte>()).TrimEnd('\0');
        if (item.AssetType == AssetType.Notecard) text = NotecardText(text);
        return new JsonObject { ["success"] = true, ["prim_id"] = primId.ToString(), ["item_id"] = itemId.ToString(), ["content"] = text };
    }

    /// <summary>Diagnostics: a prim's contents as the region lists them (ids, asset ids, masks).</summary>
    async Task<JsonNode> RawInventoryAsync(JsonObject p, CancellationToken ct)
    {
        var primId = p.Uuid("prim_id") ?? throw new RpcFail(-32602, "Invalid params: prim_id required");
        var w = _s.Region ?? throw new RpcFail(-32603, "Internal error: no region");
        var prim = w.PrimById(primId) ?? throw new RpcFail(-32602, "Invalid params: Object not found");
        var arr = new JsonArray();
        foreach (var it in (await TaskInventoryAsync(prim, ct)).OfType<InventoryItem>())
            arr.Add(new JsonObject
            {
                ["item_id"] = it.UUID.ToString(), ["name"] = it.Name, ["type"] = AgentInventory.AssetName(it.AssetType),
                ["asset_id"] = it.AssetUUID.ToString(), ["owner_mask"] = ObjectViews.PermLetters(it.Permissions.OwnerMask),
                ["saved_asset"] = _savedAsset.TryGetValue(it.UUID, out var a) ? a.ToString() : null,
            });
        return new JsonObject { ["items"] = arr };
    }

    // ---------- runtime errors ----------

    /// <summary>Script errors arrive on the debug channel; the editor reports them for published objects.</summary>
    public void OnDebugChat(ChatEventArgs e)
    {
        var w = _s.Region;
        if (w is null) return;
        var prim = w.PrimById(e.SourceID);
        var root = prim is null ? null : _s.Objects.RootOf(w, prim);
        if (root is null || !_published.ContainsKey(root.ID)) return;
        _s.Events.Log("editor", new JsonObject
        {
            ["method"] = "runtime.error",
            ["params"] = new JsonObject
            {
                ["object_id"] = root.ID.ToString(), ["prim_id"] = e.SourceID.ToString(), ["object_name"] = e.FromName,
                ["message"] = e.Message, ["error"] = e.Message, ["channel"] = "debug",
            },
        });
    }
}
