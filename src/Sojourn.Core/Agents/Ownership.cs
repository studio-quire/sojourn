using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.StructuredData;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Leap;

namespace Sojourn.Core.Agents;

/// <summary>
/// Who objects belong to and what others may do with new things: an object's group (the edit floater's Group, the
/// ObjectGroup message) and deeding it to that group (ObjectOwner, as llselectmgr's sendOwner); the viewer's Default
/// Permissions (per kind of thing: what the next owner may do, sharing with the group, everyone copying; kept per
/// account, the objects' part told to the grid through AgentPreferences); and Edit > Set Permissions on a selection's
/// contents and on inventory folders, in bulk. Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class Ownership
{
    readonly AgentSession _s;
    public Ownership(AgentSession s) => _s = s;

    GridClient C => _s.Client ?? throw new NotConnected("viewer not connected");

    List<Primitive> Roots(JsonObject a)
    {
        var w = _s.Region ?? throw new LeapError("no region");
        var roots = new List<Primitive>();
        foreach (var id in a.UuidList("ids").Concat(a.Uuid("id") is { } one ? new[] { one } : Array.Empty<UUID>()))
        {
            var p = w.PrimById(id) ?? throw new LeapError($"object not found in this region: {id}");
            var r = _s.Objects.RootOf(w, p);
            if (!roots.Any(x => x.LocalID == r.LocalID)) roots.Add(r);
        }
        if (roots.Count == 0) throw new LeapError("need id or ids");
        return roots;
    }

    /// <summary>The group an op names: a group of mine by id or name, or none.</summary>
    public UUID GroupNamed(JsonObject a)
    {
        if (a.Str("group") is { } none && none.Trim().ToLowerInvariant() is "none" or "") return UUID.Zero;
        if (a.Uuid("group_id") is { } gid)
        {
            if (gid != UUID.Zero && !_s.State.Groups.ContainsKey(gid)) throw new LeapError("I'm not in that group");
            return gid;
        }
        if (a.Str("group") is { } name)
        {
            var hits = _s.State.Groups.Values.Where(g => string.Equals(g.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) return hits[0].ID;
            if (hits.Count == 0) throw new LeapError($"I'm not in a group called {name}");
            throw new LeapError($"{hits.Count} of my groups are called {name}; give group_id");
        }
        throw new LeapError("need group_id or group (a group's name, or none)");
    }

    public string GroupLabel(UUID gid) => gid == UUID.Zero ? "no group" : _s.State.Groups.TryGetValue(gid, out var g) ? $"the group \"{g.Name}\"" : "a group";

    /// <summary>Sets the objects' group (needs modify rights; any group I'm in, or none).</summary>
    public async Task<JsonObject> SetGroupAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var roots = Roots(a);
        var gid = GroupNamed(a);
        foreach (var r in roots)
            if ((_s.State.PrimFlagsOf(r) & PrimFlags.ObjectModify) == 0)
                throw new LeapError($"I can't set the group of {r.Properties?.Name ?? "that object"}: I don't have modify rights on it");
        using (_s.Undo.Batch($"set the group of {roots.Count} object{(roots.Count == 1 ? "" : "s")}"))
            foreach (var r in roots) _s.Undo.Edit(r, new[] { "group" }, "set its group");
        c.Objects.SetObjectsGroup(c.Network.CurrentSim!, roots.Select(r => r.LocalID).ToList(), gid);
        var ok = await UntilAsync(() => roots.All(r => r.Properties?.GroupID == gid), roots, ct);
        var o = new JsonObject { ["group_id"] = gid.ToString(), ["group"] = GroupLabel(gid), ["count"] = roots.Count };
        if (!ok) o["note"] = "the region hasn't shown the change yet";
        return o;
    }

    /// <summary>Why deeding these to their group can't happen, or null: the edit floater's rules (mine, transferable,
    /// set to a group I'm in whose roles let me deed).</summary>
    public string? DeedRefusal(List<Primitive> roots, UUID gid, bool fresh = true)
    {
        if (gid == UUID.Zero) return "set the object's group first (deeding gives it to its group)";
        if (!_s.State.Groups.TryGetValue(gid, out var g)) return "I'm not in that group";
        if ((g.Powers & GroupPowers.DeedObject) == 0) return $"my roles in {g.Name} don't let me deed objects";
        if (!fresh) return null;   // the rest is read from properties, which may lag a change just made
        foreach (var r in roots)
        {
            var n = r.Properties?.Name ?? "that object";
            if (r.Properties is not { } p) return $"{n}: its details haven't arrived (try again)";
            if (p.OwnerID != C.Self.AgentID) return $"{n} isn't mine to deed";
            if ((p.Permissions.OwnerMask & PermissionMask.Transfer) == 0) return $"{n} is no-transfer, so it can't be deeded";
            if (p.GroupID != gid) return $"{n} isn't set to {g.Name}: set its group first";
            if ((p.Permissions.GroupMask & PermissionMask.Move) == 0) return $"{n} isn't shared with the group: share it first (the edit floater's Deed needs that)";
        }
        return null;
    }

    public (List<Primitive> Roots, UUID Group) DeedTarget(JsonObject a)
    {
        var roots = Roots(a);
        var gid = a.Has("group_id") || a.Has("group") ? GroupNamed(a) : roots[0].Properties?.GroupID ?? UUID.Zero;
        return (roots, gid);
    }

    /// <summary>The deed's objects with their properties asked afresh (their group and sharing as they are now).</summary>
    public async Task<(List<Primitive> Roots, UUID Group)> FreshDeedTargetAsync(JsonObject a, CancellationToken ct)
    {
        var roots = Roots(a);
        await RefreshAsync(_s, roots, ct);
        return DeedTarget(a);
    }

    /// <summary>Properties as the region has them now: asked for and waited on until a reply newer than the ask has
    /// come (FetchAsync alone returns at once for properties under five minutes old, which an edit just made leaves
    /// stale: a name or permissions changed don't come back unasked).</summary>
    internal static async Task RefreshAsync(AgentSession s, IReadOnlyList<Primitive> prims, CancellationToken ct, double seconds = 3)
    {
        if (s.Region is not { } w || prims.Count == 0) return;
        var asked = DateTime.UtcNow;
        await s.Objects.FetchAsync(w, prims, TimeSpan.Zero, ct, force: true);
        var end = asked + TimeSpan.FromSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (prims.All(p => !w.Prims.ContainsKey(p.LocalID) || w.PropsMeta.TryGetValue(p.ID, out var m) && m.FetchedAt >= asked)) return;
            await Task.Delay(100, ct);
        }
    }

    /// <summary>Deeds the objects to their group (the person's press first: it gives them away).</summary>
    public async Task<JsonObject> DeedAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var (roots, gid) = await FreshDeedTargetAsync(a, ct);
        if (DeedRefusal(roots, gid) is { } no) throw new LeapError(no);
        c.Objects.DeedObjects(c.Network.CurrentSim!, roots.Select(r => r.LocalID).ToList(), gid);
        var ok = await UntilAsync(() => roots.All(r => r.Properties?.OwnerID == gid), roots, ct);
        var o = new JsonObject { ["deeded_to"] = GroupLabel(gid), ["group_id"] = gid.ToString(), ["count"] = roots.Count };
        if (!ok) o["note"] = "the region hasn't shown the new owner yet";
        return o;
    }

    async Task<bool> UntilAsync(Func<bool> done, List<Primitive> roots, CancellationToken ct)
    {
        var w = _s.Region!;
        for (int i = 0; i < 20; i++)
        {
            if (i % 5 == 0) await RefreshAsync(_s, roots, ct, 1);
            if (done()) return true;
            await Task.Delay(250, ct);
        }
        return done();
    }

    // ---------- default permissions (the viewer's Default Permissions floater, llfloaterperms.cpp) ----------

    /// <summary>The kinds the floater lists, by our name and the viewer's setting prefix.</summary>
    public static readonly (string Kind, string Viewer)[] Kinds =
    {
        ("objects", "Objects"), ("uploads", "Uploads"), ("scripts", "Scripts"), ("notecards", "Notecards"),
        ("gestures", "Gestures"), ("wearables", "Wearables"), ("materials", "Materials"), ("settings", "Settings"),
    };

    /// <summary>One kind's defaults: next owner's rights (letters MCT), group sharing, everyone copying.</summary>
    public sealed record Defaults(PermissionMask Next, bool Group, bool EveryoneCopy)
    {
        public JsonObject Json() => new() { ["next_owner"] = ObjectViews.PermLetters(Next & ~PermissionMask.Move), ["share_with_group"] = Group, ["everyone_copy"] = EveryoneCopy };
        public PermissionMask GroupMask => Group ? PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Move : PermissionMask.None;
        public PermissionMask EveryoneMask => EveryoneCopy ? PermissionMask.Copy : PermissionMask.None;
        public PermissionMask NextMask => Next | PermissionMask.Move;
    }

    /// <summary>The viewer's own defaults (its settings.xml): the next owner may only transfer objects, uploads, scripts
    /// and wearables; notecards and gestures go on fully; materials and settings may be modified and transferred.
    /// Nothing is shared with the group or copyable by everyone.</summary>
    static Defaults ViewerDefault(string kind) => new(kind switch
    {
        "notecards" or "gestures" => PermissionMask.Modify | PermissionMask.Copy | PermissionMask.Transfer,
        "materials" or "settings" => PermissionMask.Modify | PermissionMask.Transfer,
        _ => PermissionMask.Transfer,
    }, false, false);

    /// <summary>Whether the person has set this kind's defaults here (the objects' then go to the grid at each login).</summary>
    public bool DefaultsSet(string kind) => _s.BuildStore.Read(d => d["default_perms"] is JsonObject all && all[KindOf(kind)] is JsonObject);

    public Defaults DefaultsFor(string kind)
    {
        kind = KindOf(kind);
        return _s.BuildStore.Read(d =>
        {
            if (d["default_perms"] is not JsonObject all || all[kind] is not JsonObject k) return ViewerDefault(kind);
            return new Defaults(ObjectViews.PermMask(k.Str("next_owner") ?? "MCT") & (PermissionMask.Modify | PermissionMask.Copy | PermissionMask.Transfer),
                k.Bool("share_with_group", false), k.Bool("everyone_copy", false));
        });
    }

    static string KindOf(string kind)
    {
        var k = kind.Trim().ToLowerInvariant();
        if (Kinds.Any(x => x.Kind == k)) return k;
        throw new LeapError($"kind: {string.Join(", ", Kinds.Select(x => x.Kind))}");
    }

    public JsonObject DefaultsJson()
    {
        var o = new JsonObject();
        foreach (var (k, _) in Kinds)
        {
            var j = DefaultsFor(k).Json();
            if (!DefaultsSet(k)) j["viewer_default"] = true;
            o[k] = j;
        }
        return new JsonObject { ["default_perms"] = o };
    }

    /// <summary>Changes one kind's defaults (the person's press first: what new things let others do).</summary>
    public async Task<JsonObject> SetDefaultsAsync(JsonObject a, CancellationToken ct)
    {
        var kind = KindOf(a.Str("kind") ?? throw new LeapError("need kind"));
        var cur = DefaultsFor(kind);
        // the viewer's rule: whatever the next owner can't do, copy or transfer must stay (no "no copy, no transfer")
        var next = a.Str("next_owner") is { } letters ? ObjectViews.PermMask(letters) & (PermissionMask.Modify | PermissionMask.Copy | PermissionMask.Transfer) : cur.Next;
        if ((next & (PermissionMask.Copy | PermissionMask.Transfer)) == 0) throw new LeapError("the next owner must be able to copy or transfer (or both), as the viewer requires");
        var d = new Defaults(next, a.Bool("share_with_group") ?? cur.Group, a.Bool("everyone_copy") ?? cur.EveryoneCopy);
        _s.BuildStore.Change(s =>
        {
            var all = s["default_perms"] as JsonObject ?? new JsonObject();
            all[kind] = d.Json();
            s["default_perms"] = all;
        });
        var o = new JsonObject { ["kind"] = kind, ["default_perms"] = d.Json() };
        if (kind == "objects") o["sent"] = await SendObjectDefaultsAsync(ct) is { } why ? why : "told the grid";
        return o;
    }

    // ---------- bulk permissions (Edit > Set Permissions, llfloaterbulkpermission.cpp) ----------

    static readonly Dictionary<string, AssetType[]> BulkTypes = new()
    {
        ["animation"] = new[] { AssetType.Animation }, ["bodypart"] = new[] { AssetType.Bodypart }, ["clothing"] = new[] { AssetType.Clothing },
        ["gesture"] = new[] { AssetType.Gesture }, ["notecard"] = new[] { AssetType.Notecard }, ["object"] = new[] { AssetType.Object },
        ["script"] = new[] { AssetType.LSLText }, ["sound"] = new[] { AssetType.Sound }, ["texture"] = new[] { AssetType.Texture },
        ["settings"] = new[] { AssetType.Settings }, ["material"] = new[] { AssetType.Material },
    };

    /// <summary>What a bulk change would set: the next owner's (always able to copy or transfer, as the floater forces),
    /// the group's and everyone's masks. A part not given stays as each item has it.</summary>
    public sealed record BulkChange(PermissionMask? Next, bool? Group, bool? EveryoneCopy, HashSet<AssetType> Types)
    {
        public string Words()
        {
            var parts = new List<string>();
            if (Next is { } n) parts.Add($"next owner can: {ObjectViews.PermLetters(n & ~PermissionMask.Move)}");
            if (Group is { } g) parts.Add(g ? "shared with the group" : "not shared with the group");
            if (EveryoneCopy is { } e) parts.Add(e ? "anyone can copy" : "not copyable by anyone");
            return string.Join("; ", parts);
        }
    }

    public BulkChange BulkOf(JsonObject a)
    {
        PermissionMask? next = null;
        if (a.Str("next_owner") is { } letters)
        {
            var n = ObjectViews.PermMask(letters) & (PermissionMask.Modify | PermissionMask.Copy | PermissionMask.Transfer);
            if ((n & PermissionMask.Copy) == 0) n |= PermissionMask.Transfer;   // no copy: transfer stays (fair use, as the floater)
            next = n | PermissionMask.Move;
        }
        var types = new HashSet<AssetType>();
        var asked = a.StrList("types");
        foreach (var t in asked.Count == 0 ? BulkTypes.Keys.ToList() : asked)
            types.UnionWith(BulkTypes.TryGetValue(t.Trim().ToLowerInvariant().TrimEnd('s'), out var ts) ? ts
                : throw new LeapError($"types: {string.Join(", ", BulkTypes.Keys)}"));
        var b = new BulkChange(next, a.Bool("share_with_group"), a.Bool("everyone_copy"), types);
        if (b.Next is null && b.Group is null && b.EveryoneCopy is null) throw new LeapError("nothing to change: give next_owner, share_with_group or everyone_copy");
        return b;
    }

    Permissions Changed(Permissions p, BulkChange b, AssetType type)
    {
        var owner = p.OwnerMask;
        if (b.Next is { } n) p.NextOwnerMask = (n | (type == AssetType.Settings ? PermissionMask.Copy : 0)) & (p.BaseMask | PermissionMask.Move);
        if (b.Group is { } g) p.GroupMask = g ? (PermissionMask.Copy | PermissionMask.Modify | PermissionMask.Move) & owner : PermissionMask.None;
        if (b.EveryoneCopy is { } e) p.EveryoneMask = e ? PermissionMask.Copy & owner : p.EveryoneMask & ~PermissionMask.Copy;
        return p;
    }

    /// <summary>The prims a bulk change on contents works through: the objects' whole linksets, those I may modify.</summary>
    public List<Primitive> BulkPrims(JsonObject a) =>
        _s.ScriptChores.Prims(a).Where(p => (_s.State.PrimFlagsOf(p) & PrimFlags.ObjectModify) != 0).ToList();

    public async Task<JsonObject> BulkPermsAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var b = BulkOf(a);
        var changed = new JsonArray();
        var skipped = new JsonArray();
        if (a.Uuid("folder_id") is { } folder)
        {
            // an inventory folder of mine and the folders in it
            var items = new List<InventoryItem>();
            async Task Walk(UUID f, int depth)
            {
                if (depth > 32) return;
                await _s.Inventory.EnsureLoadedAsync(f, ct);
                foreach (var n in c.Inventory.Store!.GetContents(f))
                    if (n is InventoryFolder sub) await Walk(sub.UUID, depth + 1);
                    else if (n is InventoryItem it && it.AssetType != AssetType.Link && b.Types.Contains(it.AssetType)) items.Add(it);
            }
            if (_s.Inventory.Node(folder) is not InventoryFolder) throw new LeapError("no such folder in my inventory");
            await Walk(folder, 0);
            var send = new List<InventoryItem>();
            foreach (var it in items)
            {
                if (it.OwnerID != c.Self.AgentID || (it.Permissions.OwnerMask & PermissionMask.Modify) == 0)
                {
                    skipped.Add(new JsonObject { ["item_id"] = it.UUID.ToString(), ["name"] = it.Name, ["why"] = "no modify rights" });
                    continue;
                }
                it.Permissions = Changed(it.Permissions, b, it.AssetType);
                send.Add(it);
                changed.Add(new JsonObject { ["item_id"] = it.UUID.ToString(), ["name"] = it.Name, ["perms"] = InventoryOps.PermsJson(it.Permissions) });
            }
            for (int i = 0; i < send.Count; i += 50) c.Inventory.RequestUpdateItems(send.Skip(i).Take(50).ToList());
        }
        else
        {
            // the objects' contents, prim by prim, as the floater goes (UpdateTaskInventory per item)
            var prims = BulkPrims(a);
            if (prims.Count == 0) throw new LeapError("I can't modify those objects, so their contents keep their permissions");
            foreach (var p in prims)
            {
                List<InventoryBase> inv;
                try { inv = await _s.Scripts.TaskInventoryAsync(p, ct); }
                catch (Scripts.ScriptEditor.RpcFail e) { skipped.Add(new JsonObject { ["prim_id"] = p.ID.ToString(), ["why"] = e.Message }); continue; }
                foreach (var it in inv.OfType<InventoryItem>().Where(i => b.Types.Contains(i.AssetType)))
                {
                    it.Permissions = Changed(it.Permissions, b, it.AssetType);
                    c.Inventory.UpdateTaskInventory(p.LocalID, it, c.Network.CurrentSim);
                    changed.Add(new JsonObject { ["prim_id"] = p.ID.ToString(), ["item_id"] = it.UUID.ToString(), ["name"] = it.Name, ["perms"] = InventoryOps.PermsJson(it.Permissions) });
                    await Task.Delay(30, ct);
                }
            }
        }
        var o = new JsonObject { ["changed"] = changed, ["count"] = changed.Count, ["set"] = b.Words() };
        if (skipped.Count > 0) o["skipped"] = skipped;
        return o;
    }

    /// <summary>The objects' defaults to the grid (AgentPreferences default_object_perm_masks), as the viewer does at
    /// login and on each change; new objects made in-world take them. Null when sent, else why not.</summary>
    public async Task<string?> SendObjectDefaultsAsync(CancellationToken ct)
    {
        if (_s.Client is not { } c || c.Network.CurrentSim?.Caps?.CapabilityURI("AgentPreferences") is not { } cap)
            return "kept; this region has no AgentPreferences capability, so the grid hears it at the next region that has one";
        var d = DefaultsFor("objects");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var body = new OSDMap
            {
                ["default_object_perm_masks"] = new OSDMap
                {
                    ["Group"] = OSD.FromInteger((int)(uint)d.GroupMask), ["Everyone"] = OSD.FromInteger((int)(uint)d.EveryoneMask),
                    ["NextOwner"] = OSD.FromInteger((int)(uint)d.NextMask),
                },
            };
            var (resp, _) = await c.HttpCapsClient.PostAsync(cap, OSDFormat.Xml, body, cts.Token);
            return resp.IsSuccessStatusCode ? null : $"kept; the region refused it ({(int)resp.StatusCode})";
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _s.Log.LogWarning(e, "default permissions");
            return "kept; sending it failed";
        }
    }
}
