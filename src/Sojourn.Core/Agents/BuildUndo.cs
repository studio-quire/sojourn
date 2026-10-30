using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Assets;
using LibreMetaverse.Packets;
using Sojourn.Core.Leap;
using Sojourn.Core.World;

namespace Sojourn.Core.Agents;

/// <summary>
/// Undo and redo for edits made through the agent API (the assistant's, and any caller's): before TesselBuild modify,
/// create, duplicate, rez, link and unlink, and SojournBuild's material ops change anything, what they'll change is
/// kept, and undo puts back only those parts (a script moving the object meanwhile keeps its other changes). The
/// viewer's undo covers moves, turns and sizes of a selection; this covers every key modify takes. A creation is undone
/// by deleting what was made (to Trash, through the confirmation gate as any delete); a build's prims made and then
/// linked fold into one creation, so "undo that" after a build takes the whole object away. Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class BuildUndo
{
    readonly AgentSession _s;
    readonly object _lock = new();
    readonly List<Entry> _undo = new();
    readonly List<Entry> _redo = new();
    const int Keep = 200;
    static readonly AsyncLocal<Entry?> Open = new();
    /// <summary>True while undo or redo itself is putting things back (nothing it sends is recorded).</summary>
    static readonly AsyncLocal<bool> Restoring = new();

    public BuildUndo(AgentSession s) => _s = s;

    /// <summary>A prim's id and region, and what one edit is about to change on it.</summary>
    internal sealed record PrimState(UUID Id, ulong Region, HashSet<string> Parts, State Was);

    /// <summary>Everything about a prim an edit can change, as it was.</summary>
    internal sealed record State(
        bool Root, Vector3 Pos, Quaternion Rot, Vector3 Scale, Primitive.ConstructionData Shape, byte[]? Te, string MediaUrl,
        string? Name, string? Desc, Material Mat, PrimFlags Flags, PhysicsShapeType? PhysicsShape,
        Primitive.LightData? Light, Primitive.LightImage? Projector, Primitive.FlexibleData? Flexi, Primitive.SculptData? Sculpt,
        ClickAction Click, bool Animated, JsonObject? Probe, UUID[]? Pbr, Dictionary<int, string>? Overrides,
        PermissionMask Next, PermissionMask Group, PermissionMask Everyone, SaleType Sale, int Price, UUID GroupId);

    internal enum Kind { Edit, Created, Linked, Unlinked }

    internal sealed class Entry
    {
        public required string What;
        public required Kind Kind;
        public DateTime At = DateTime.UtcNow;
        public readonly List<PrimState> Prims = new();
        /// <summary>Created: the roots made. Linked: the root and its children. Unlinked: the old root first, then the rest.</summary>
        public readonly List<UUID> Ids = new();
    }

    // --- recording ----------------------------------------------------------------------------------------------------

    /// <summary>Groups everything recorded until disposed into one undo step (an op over several prims).</summary>
    public IDisposable Batch(string what)
    {
        if (Open.Value is not null || Restoring.Value) return new Done(() => { });
        var e = new Entry { What = what, Kind = Kind.Edit };
        Open.Value = e;
        return new Done(() =>
        {
            Open.Value = null;
            if (e.Prims.Count > 0) Push(e);
        });
    }

    sealed class Done(Action a) : IDisposable { public void Dispose() => a(); }

    /// <summary>Before an edit on <paramref name="p"/> changes the given parts.</summary>
    internal void Edit(Primitive p, IEnumerable<string> parts, string what)
    {
        if (Restoring.Value || _s.Region is not { } w) return;
        var set = parts.ToHashSet();
        if (set.Count == 0) return;
        var ps = new PrimState(p.ID, w.Handle, set, Capture(w, p));
        if (Open.Value is { } open)
        {
            // the first time this prim is touched in the step is what it goes back to
            if (open.Prims.FirstOrDefault(x => x.Id == p.ID) is { } had) had.Parts.UnionWith(set);
            else open.Prims.Add(ps);
            return;
        }
        var e = new Entry { What = what, Kind = Kind.Edit };
        e.Prims.Add(ps);
        Push(e);
    }

    internal void Created(Primitive root, string what)
    {
        if (Restoring.Value) return;
        var e = new Entry { What = what, Kind = Kind.Created };
        e.Ids.Add(root.ID);
        Push(e);
    }

    internal void Linked(UUID root, IEnumerable<UUID> children)
    {
        if (Restoring.Value) return;
        var kids = children.Where(k => k != root).ToList();
        lock (_lock)
        {
            // a build: its prims made one by one (and set up) just before, now linked, become one creation
            var all = kids.Append(root).ToHashSet();
            var made = new HashSet<UUID>();
            int i = _undo.Count;
            while (i > 0 && made.Count < all.Count)
            {
                var prev = _undo[i - 1];
                if (prev.Kind == Kind.Created && prev.Ids.All(all.Contains)) made.UnionWith(prev.Ids);
                else if (prev.Kind == Kind.Edit && prev.Prims.All(x => all.Contains(x.Id))) { }
                else break;
                i--;
            }
            if (made.SetEquals(all))
            {
                _undo.RemoveRange(i, _undo.Count - i);
                var c = new Entry { What = $"made a linked object of {all.Count} prims", Kind = Kind.Created };
                c.Ids.Add(root);
                PushLocked(c);
                return;
            }
        }
        var e = new Entry { What = $"linked {kids.Count + 1} prims", Kind = Kind.Linked };
        e.Ids.Add(root);
        e.Ids.AddRange(kids);
        Push(e);
    }

    internal void Unlinked(UUID oldRoot, IEnumerable<UUID> prims)
    {
        if (Restoring.Value) return;
        var e = new Entry { What = "unlinked", Kind = Kind.Unlinked };
        e.Ids.Add(oldRoot);
        e.Ids.AddRange(prims.Where(p => p != oldRoot));
        Push(e);
    }

    void Push(Entry e)
    {
        lock (_lock) PushLocked(e);
    }

    void PushLocked(Entry e)
    {
        _undo.Add(e);
        if (_undo.Count > Keep) _undo.RemoveAt(0);
        _redo.Clear();
    }

    // --- reading a prim -----------------------------------------------------------------------------------------------

    /// <summary>The renderer's PBR material ids by face (LibreMetaverse skips that extra param): set by the client.</summary>
    public static Func<ulong, uint, UUID[]?>? RenderMaterialsOf { get; set; }

    internal State Capture(RegionWorld w, Primitive p)
    {
        var root = _s.Objects.RootOf(w, p);
        bool isRoot = root.LocalID == p.LocalID;
        var props = p.Properties;
        Dictionary<int, string>? ov = null;
        if (_s.Client?.Network.Simulators.FirstOrDefault(x => x.Handle == w.Handle) is { } sim
            && sim.GLTFMaterialOverrides.TryGetValue(p.LocalID, out var entry))
            ov = entry.FaceOverrides.ToDictionary(kv => kv.Key, kv => kv.Value.ToJson());
        return new State(
            isRoot, isRoot ? w.RegionPosition(p) : p.Position, isRoot ? w.RegionRotation(p) : p.Rotation, p.Scale,
            new Primitive.ConstructionData(p.PrimData), p.Textures?.GetBytes(), p.MediaURL ?? "",
            props?.Name, props?.Description, p.PrimData.Material, _s.State.PrimFlagsOf(p), p.PhysicsProps?.PhysicsShapeType,
            p.Light is { } l ? Clone(l) : null, p.LightMap is { LightTexture: var lt } lm && lt != UUID.Zero ? new Primitive.LightImage { LightTexture = lt, Params = lm.Params } : null,
            p.Flexible is { } f && ((byte)p.PrimData.PathCurve & 0xf0) == 0x80 ? f : null, p.Sculpt,
            p.ClickAction, p.IsAnimatedObject, ObjectViews.ProbeOf?.Invoke(w.Handle, p.LocalID),
            RenderMaterialsOf?.Invoke(w.Handle, p.LocalID)?.ToArray(), ov,
            props?.Permissions.NextOwnerMask ?? 0, props?.Permissions.GroupMask ?? 0, props?.Permissions.EveryoneMask ?? 0,
            props?.SaleType ?? SaleType.Not, props?.SalePrice ?? 0, props?.GroupID ?? UUID.Zero);
    }

    static Primitive.LightData Clone(Primitive.LightData l) => new()
    {
        Color = l.Color, Intensity = l.Intensity, Radius = l.Radius, Cutoff = l.Cutoff, Falloff = l.Falloff,
    };

    // --- the stacks ---------------------------------------------------------------------------------------------------

    public JsonObject History(int count)
    {
        lock (_lock)
        {
            JsonObject Row(Entry e, int i) => new()
            {
                ["step"] = i, ["what"] = e.What, ["ago_s"] = Math.Round((DateTime.UtcNow - e.At).TotalSeconds),
                ["objects"] = Json.Ids(e.Kind == Kind.Edit ? e.Prims.Select(p => p.Id).Distinct() : e.Ids.Take(1)),
                ["parts"] = e.Kind == Kind.Edit ? Json.Strings(e.Prims.SelectMany(p => p.Parts).Distinct().OrderBy(x => x)) : null,
            };
            var undo = new JsonArray();
            for (int i = _undo.Count - 1, n = 1; i >= 0 && n <= count; i--, n++) undo.Add(Row(_undo[i], n));
            var redo = new JsonArray();
            for (int i = _redo.Count - 1, n = 1; i >= 0 && n <= count; i--, n++) redo.Add(Row(_redo[i], n));
            return new JsonObject { ["undo"] = undo, ["redo"] = redo };
        }
    }

    /// <summary>What the next undo (or redo) would do, for the confirmation gate: the kind of thing it is (delete for a
    /// creation, permissions when it puts permissions or a sale back) and its words.</summary>
    public (string? Kind, string Title, string Detail) Preview(bool redo, int steps, UUID? only)
    {
        lock (_lock)
        {
            var list = Pick(redo ? _redo : _undo, steps, only);
            if (list.Count == 0) return (null, "", "");
            string? kind = null;
            if (!redo && list.Any(e => e.Kind == Kind.Created)) kind = "delete";
            else if (list.Any(e => e.Kind == Kind.Edit && e.Prims.Any(p => p.Parts.Overlaps(PermParts)))) kind = "permissions";
            var what = string.Join("; ", list.Select(e => e.What));
            var verb = redo ? "Redo" : "Undo";
            return (kind, $"{verb}: {(what.Length > 120 ? what[..120] + "..." : what)}",
                kind == "delete" ? "What was made goes to your Trash." : kind == "permissions" ? "Permissions or a sale go back to what they were." : "");
        }
    }

    static readonly HashSet<string> PermParts = new() { "perms", "sale", "group" };
    /// <summary>Parts that live in the object's properties, which the region sends only when asked or selected.</summary>
    static readonly HashSet<string> PropParts = new() { "name", "desc", "perms", "sale", "group" };

    static List<Entry> Pick(List<Entry> stack, int steps, UUID? only)
    {
        var picked = new List<Entry>();
        for (int i = stack.Count - 1; i >= 0 && picked.Count < steps; i--)
        {
            var e = stack[i];
            if (only is { } id && !e.Ids.Contains(id) && e.Prims.All(p => p.Id != id)) continue;
            picked.Add(e);
        }
        return picked;
    }

    public async Task<JsonObject> UndoAsync(int steps, UUID? only, bool redo, CancellationToken ct)
    {
        List<Entry> picked;
        lock (_lock)
        {
            picked = Pick(redo ? _redo : _undo, Math.Max(1, steps), only);
            if (picked.Count == 0) throw new LeapError(redo ? "nothing to redo" : only is null ? "nothing to undo" : "nothing to undo on that object");
            foreach (var e in picked) (redo ? _redo : _undo).Remove(e);
        }
        var done = new JsonArray();
        var failed = new JsonArray();
        Restoring.Value = true;
        try
        {
            foreach (var e in picked)
            {
                try
                {
                    var back = await PutBackAsync(e, ct);
                    lock (_lock)
                    {
                        if (back is not null) (redo ? _undo : _redo).Add(back);
                    }
                    done.Add(e.What);
                }
                catch (LeapError x)
                {
                    failed.Add(new JsonObject { ["what"] = e.What, ["error"] = x.Message });
                    lock (_lock) (redo ? _redo : _undo).Add(e);
                    break;
                }
            }
        }
        finally { Restoring.Value = false; }
        var r = new JsonObject { [redo ? "redone" : "undone"] = done };
        if (failed.Count > 0) r["failed"] = failed;
        return r;
    }

    /// <summary>Puts one step back; returns the step that would redo it (null where there's none).</summary>
    async Task<Entry?> PutBackAsync(Entry e, CancellationToken ct)
    {
        var w = _s.Region ?? throw new LeapError("no region");
        switch (e.Kind)
        {
            case Kind.Created:
            {
                var here = e.Ids.Where(id => w.PrimById(id) is not null).ToList();
                if (here.Count == 0) throw new LeapError("what was made isn't here any more (taken, deleted, or in another region)");
                // deleting a prim deletes its whole linkset: never something else it was linked into since
                foreach (var id in here)
                    if (_s.Objects.RootOf(w, w.PrimById(id)!) is var r && r.ID != id && !e.Ids.Contains(r.ID))
                        throw new LeapError($"what was made is linked into {r.Properties?.Name ?? "another object"} now; undoing it would delete that too (unlink it first)");
                await _s.Build.DeRezAsync(new JsonObject { ["ids"] = Json.Ids(here) }, DeRezDestination.TrashFolder, ct);
                return null;
            }
            case Kind.Linked:
            {
                var root = w.PrimById(e.Ids[0]) ?? throw new LeapError("the linked object isn't here any more");
                await _s.Build.UnlinkAsync(new JsonObject { ["ids"] = Json.Ids(e.Ids) }, ct);
                var back = new Entry { What = e.What, Kind = Kind.Unlinked, At = e.At };
                back.Ids.AddRange(e.Ids);
                _ = root;
                return back;
            }
            case Kind.Unlinked:
            {
                var here = e.Ids.Where(id => w.PrimById(id) is not null).ToList();
                if (here.Count < 2) throw new LeapError("the unlinked prims aren't here any more");
                await _s.Build.LinkAsync(new JsonObject { ["ids"] = Json.Ids(here) }, ct);
                var back = new Entry { What = e.What, Kind = Kind.Linked, At = e.At };
                back.Ids.AddRange(here);
                return back;
            }
        }
        var redo = new Entry { What = e.What, Kind = Kind.Edit, At = e.At };
        var c = _s.Client ?? throw new NotConnected("viewer not connected");
        foreach (var ps in e.Prims)
        {
            if (ps.Region != w.Handle) throw new LeapError("that edit was in another region");
            var p = w.PrimById(ps.Id) ?? throw new LeapError("the object edited isn't here any more");
            if (ps.Parts.Overlaps(PropParts)) await Ownership.RefreshAsync(_s, new[] { p }, ct);
            redo.Prims.Add(new PrimState(ps.Id, ps.Region, new HashSet<string>(ps.Parts), Capture(w, p)));
            await RestoreAsync(c, c.Network.CurrentSim!, w, p, ps.Was, ps.Parts, ct);
        }
        return redo;
    }

    async Task RestoreAsync(GridClient c, Simulator sim, RegionWorld w, Primitive p, State was, HashSet<string> parts, CancellationToken ct)
    {
        var o = c.Objects;
        uint id = p.LocalID;
        bool Has(string part) => parts.Contains(part);
        if (Has("place") || Has("scale"))
        {
            byte type = 0;
            var data = new List<byte>();
            if (Has("place")) { type |= (byte)(UpdateType.Position | UpdateType.Rotation); data.AddRange(was.Pos.GetBytes()); data.AddRange(was.Rot.GetBytes()); }
            if (Has("scale"))
            {
                if (!Has("place") && !was.Root) { type |= (byte)UpdateType.Position; data.AddRange(p.Position.GetBytes()); }
                type |= (byte)UpdateType.Scale;
                data.AddRange(was.Scale.GetBytes());
            }
            if (was.Root) type |= (byte)UpdateType.Linked;
            c.Network.SendPacket(new MultipleObjectUpdatePacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
                ObjectData = new[] { new MultipleObjectUpdatePacket.ObjectDataBlock { ObjectLocalID = id, Type = type, Data = data.ToArray() } },
            }, sim);
        }
        if (Has("shape") || Has("flexi")) o.SetShape(sim, id, was.Shape);
        if (Has("faces") && was.Te is { } te) o.SetTextures(sim, id, new Primitive.TextureEntry(te, 0, te.Length), p.MediaURL ?? "");
        if (Has("name") && was.Name is { } n) o.SetName(sim, id, n);
        if (Has("desc") && was.Desc is { } d) o.SetDescription(sim, id, d);
        if (Has("material")) o.SetMaterial(sim, id, was.Mat);
        if (Has("flags"))
            BuildOps.SendFlags(c, sim, id, (was.Flags & PrimFlags.Physics) != 0, (was.Flags & PrimFlags.TemporaryOnRez) != 0,
                (was.Flags & PrimFlags.Phantom) != 0, (was.Flags & PrimFlags.CastShadows) != 0);
        if (Has("physics_shape") && was.PhysicsShape is { } shape)
            o.SetFlags(sim, id, (was.Flags & PrimFlags.Physics) != 0, (was.Flags & PrimFlags.TemporaryOnRez) != 0,
                (was.Flags & PrimFlags.Phantom) != 0, (was.Flags & PrimFlags.CastShadows) != 0, shape, 1000f, 0.6f, 0.5f, 1f);
        if (Has("light"))
        {
            if (was.Light is { } l && l.Intensity > 0) o.SetLight(sim, id, l);
            else o.SetExtraParamOff(sim, id, ExtraParamType.Light);
        }
        if (Has("projector")) BuildOps.SendProjector(c, sim, id, was.Projector);
        if (Has("flexi"))
        {
            if (was.Flexi is { } f) o.SetFlexible(sim, id, f);
            else o.SetExtraParamOff(sim, id, ExtraParamType.Flexible);
        }
        if (Has("sculpt"))
        {
            if (was.Sculpt is { } sc && sc.SculptTexture != UUID.Zero) o.SetSculpt(sim, id, sc);
            else o.SetExtraParamOff(sim, id, ExtraParamType.Sculpt);
        }
        if (Has("probe")) BuildOps.SendProbe(c, sim, id, was.Probe);
        if (Has("animated")) BuildOps.SendAnimated(c, sim, id, was.Animated);
        if (Has("click_action"))
            c.Network.SendPacket(new ObjectClickActionPacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
                ObjectData = new[] { new ObjectClickActionPacket.ObjectDataBlock { ObjectLocalID = id, ClickAction = (byte)was.Click } },
            }, sim);
        if (Has("perms"))
        {
            var ids = new List<uint> { id };
            void Mask(PermissionWho who, PermissionMask want, PermissionMask settable)
            {
                if ((want & settable) != 0) o.SetPermissions(sim, ids, who, want & settable, true);
                if ((settable & ~want) != 0) o.SetPermissions(sim, ids, who, settable & ~want, false);
            }
            Mask(PermissionWho.NextOwner, was.Next, PermissionMask.Modify | PermissionMask.Copy | PermissionMask.Transfer);
            Mask(PermissionWho.Group, was.Group, PermissionMask.Modify | PermissionMask.Copy | PermissionMask.Move);
            Mask(PermissionWho.Everyone, was.Everyone, PermissionMask.Copy | PermissionMask.Move);
        }
        if (Has("sale")) o.SetSaleInfo(sim, id, was.Sale, was.Price);
        if (Has("group")) o.SetObjectsGroup(sim, new List<uint> { id }, was.GroupId);
        if (Has("pbr")) await RestorePbrAsync(c, sim, w, p, was, ct);
    }

    async Task RestorePbrAsync(GridClient c, Simulator sim, RegionWorld w, Primitive p, State was, CancellationToken ct)
    {
        var now = RenderMaterialsOf?.Invoke(w.Handle, p.LocalID) ?? Array.Empty<UUID>();
        var before = was.Pbr ?? Array.Empty<UUID>();
        int faces = Math.Max(Math.Max(now.Length, before.Length), (was.Overrides?.Keys.DefaultIfEmpty(-1).Max() ?? -1) + 1);
        var updates = new List<MaterialOps.PbrUpdate>();
        for (int f = 0; f < faces; f++)
        {
            var b = f < before.Length ? before[f] : UUID.Zero;
            var n = f < now.Length ? now[f] : UUID.Zero;
            if (b == UUID.Zero && n == UUID.Zero) continue;
            string json = b != UUID.Zero && was.Overrides is { } o && o.TryGetValue(f, out var j) ? j : "";
            // a face whose material is as it was gets only its override back (a modify, as the editor sends)
            updates.Add(new MaterialOps.PbrUpdate(p.ID, f, b == n && b != UUID.Zero ? null : b, json));
        }
        if (updates.Count > 0) await _s.Materials.SendPbrAsync(c, sim, updates, ct);
    }
}
