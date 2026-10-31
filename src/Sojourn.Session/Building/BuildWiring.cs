using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Session.Assistant;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;

namespace Sojourn.Session.Building;

/// <summary>
/// Batch B6's client half (building), kept out of Program.cs: SojournBuild on the agent API; the renderer's PBR
/// material ids (LibreMetaverse skips that extra param) and prims' face counts for MaterialOps and undo; the person's
/// object defaults told to the grid at each region that has AgentPreferences, once they've set them here; and the
/// confirmations for what deletes, gives away, deeds, changes permissions or spends (Confirmations.Cases), with deeds
/// that can't happen refused before the person is asked. Claude Opus 5.5, 2026-10-29.
/// </summary>
public static class BuildWiring
{
    public static void Start(AgentSession s, AssistantHost assistant, bool uploadsCost, ILogger log)
    {
        s.Api.Register(new SojournBuild(s));
        BuildUndo.RenderMaterialsOf = (region, local) => Sojourn.Render.World.RenderMaterialTracker.Shared.Get(region, local);
        MaterialOps.FacesOf = FacesOf;

        // the objects' default permissions reach the grid as the viewer sends them, when the region's caps arrive
        var hooked = new System.Runtime.CompilerServices.ConditionalWeakTable<GridClient, object>();
        void Hook(GridClient c)
        {
            if (hooked.TryGetValue(c, out _)) return;
            hooked.Add(c, new object());
            // lights and projectors taken off a prim leave it (LibreMetaverse kept them)
            Sojourn.Core.World.ExtraParamsSync.Attach(c);
            c.Network.EventQueueRunning += (_, e) =>
            {
                if (e.Simulator != c.Network.CurrentSim || !s.Ownership.DefaultsSet("objects")) return;
                _ = Task.Run(async () =>
                {
                    try { if (await s.Ownership.SendObjectDefaultsAsync(CancellationToken.None) is { } why) log.LogDebug("default permissions: {Why}", why); }
                    catch (Exception x) { log.LogDebug(x, "default permissions"); }
                });
            };
        }
        lock (AgentSession.ClientHooks) AgentSession.ClientHooks.Add(Hook);
        if (s.Client is { } now) Hook(now);

        var c = assistant.Confirmations;
        c.Cases.Add(new Confirmations.Case((pump, op, a) => Kind(s, pump, op, a, uploadsCost), (pump, op, a) => DescribeAsync(s, pump, op, a)));
        c.Refusals.Add((pump, op, a) => Refusal(s, pump, op, a));
        log.LogDebug("building wired");
    }

    /// <summary>A prim's faces as the viewer counts them (getNumTEs): its volume's faces at the lowest detail; a sculpt
    /// has one. Meshes are counted from their asset in Core.</summary>
    static int? FacesOf(Primitive p)
    {
        if (p.PrimData.PCode != PCode.Prim) return null;
        if (p.Sculpt is { } sc && sc.SculptTexture != UUID.Zero) return sc.Type == SculptType.Mesh ? null : 1;
        try
        {
            var faces = Sojourn.Render.Geometry.VolumeBuilder.BuildPrim(p.PrimData, Sojourn.Render.Geometry.VolumeBuilder.DetailForLod(0));
            return faces.Length == 0 ? null : faces.Max(f => f.TeIndex) + 1;
        }
        catch (Exception) { return null; }
    }

    // --- what waits for the person ----------------------------------------------------------------------------------

    static string? Kind(AgentSession s, string pump, string op, JsonObject a, bool uploadsCost)
    {
        if (pump != "SojournBuild") return null;
        switch (op)
        {
            case "removeScripts": return "delete";
            // the item it came from in the object's contents is replaced, its old version gone
            case "saveBack": return "delete";
            case "deed": return "give";
            case "group" or "setDefaultPerms" or "bulkPerms": return "permissions";
            case "uploadMaterial": return uploadsCost && !a.Bool("quote", false) ? "spend" : null;
            case "restore": return uploadsCost && !a.Bool("quote", false) ? "spend" : null;
            case "undo" or "redo":
                return s.Undo.Preview(op == "redo", Math.Max(1, a.Int("steps", 1)), a.Uuid("id")).Kind;
            default: return null;
        }
    }

    static string? Refusal(AgentSession s, string pump, string op, JsonObject a)
    {
        if (pump != "SojournBuild") return null;
        try
        {
            if (op == "deed")
            {
                var (roots, gid) = s.Ownership.DeedTarget(a);
                return s.Ownership.DeedRefusal(roots, gid, fresh: false) is { } why ? $"{why}; nothing was done" : null;
            }
            if (op == "group") s.Ownership.GroupNamed(a);
            if (op == "bulkPerms") s.Ownership.BulkOf(a);
        }
        catch (LeapError e) { return $"{e.Message}; nothing was done"; }
        catch (NotConnected) { return null; }
        return null;
    }

    static async Task<(string Title, string Detail)> DescribeAsync(AgentSession s, string pump, string op, JsonObject a)
    {
        try
        {
            switch (op)
            {
                case "removeScripts":
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var list = await s.ScriptChores.RemovalAsync(a, cts.Token);
                    var objects = list.Select(x => x.Prim.ID).Distinct().Count();
                    var names = Confirmations.Listed(list.Select(x => $"\"{x.Item.Name}\"").Distinct().ToList(), 6, "script names");
                    return ($"Remove {list.Count} script{(list.Count == 1 ? "" : "s")} from {Objects(s, a)}",
                        list.Count == 0 ? "There are none: this will do nothing." : $"{names}, in {objects} prim{(objects == 1 ? "" : "s")}. They're deleted from the objects.");
                }
                case "saveBack":
                    return ($"Save {Objects(s, a)} back into the object it came from", "It replaces the item it was rezzed from in that object's contents.");
                case "deed":
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    var (roots, gid) = await s.Ownership.FreshDeedTargetAsync(a, cts.Token);
                    var why = s.Ownership.DeedRefusal(roots, gid);
                    return ($"Deed {Names(roots)} to {s.Ownership.GroupLabel(gid)}",
                        why is null ? "It then belongs to the group, for good: only the group can take it back or give it away." : $"This will be refused: {why}.");
                }
                case "group":
                    return ($"Set the group of {Objects(s, a)} to {s.Ownership.GroupLabel(s.Ownership.GroupNamed(a))}",
                        "What it shares with its group goes to that group's members.");
                case "setDefaultPerms":
                {
                    var kind = a.Str("kind") ?? "";
                    var parts = new List<string>();
                    if (a.Str("next_owner") is { } n) parts.Add($"next owner can: {Letters(n)}");
                    if (a.Bool("share_with_group") is { } g) parts.Add(g ? "shared with the group" : "not shared with the group");
                    if (a.Bool("everyone_copy") is { } e) parts.Add(e ? "anyone can copy" : "not copyable by anyone");
                    return ($"Change the default permissions for new {kind}", string.Join("; ", parts) + ".");
                }
                case "bulkPerms":
                {
                    var b = s.Ownership.BulkOf(a);
                    var where = a.Uuid("folder_id") is { } f && s.Inventory.Node(f) is { } node ? $"everything in the folder \"{node.Name}\"" : $"what's inside {Objects(s, a)}";
                    var types = a.StrList("types") is { Count: > 0 } ts ? string.Join(", ", ts) : "every kind of item";
                    return ($"Change the permissions of {where}", $"{b.Words()} ({types}).");
                }
                case "uploadMaterial":
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var q = await s.Materials.UploadAsync(new JsonObject { ["path"] = a.Str("path"), ["material"] = a["material"]?.DeepClone(), ["quote"] = true }, cts.Token);
                    return ($"Upload the material {Path.GetFileName(a.Str("path") ?? "")}",
                        $"{q.Int("textures", 0)} texture{(q.Int("textures", 0) == 1 ? "" : "s")} in it, L${q.Int("cost", 0)} on this grid. {Balance(s)}");
                }
                case "restore":
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    var q = await s.Backups.RestoreAsync(new JsonObject { ["name"] = a["name"]?.DeepClone(), ["path"] = a["path"]?.DeepClone(), ["quote"] = true }, cts.Token);
                    var up = q.Int("uploads", 0);
                    return ($"Rebuild the backup \"{q.Str("name")}\" here ({q.Int("prims", 0)} prims)",
                        up == 0 ? "Its textures are on this grid already: nothing is uploaded." : $"{up} texture{(up == 1 ? "" : "s")} uploaded again, L${q.Int("cost", 0)}. {Balance(s)}");
                }
                case "undo" or "redo":
                {
                    var (_, title, detail) = s.Undo.Preview(op == "redo", Math.Max(1, a.Int("steps", 1)), a.Uuid("id"));
                    return (title, detail);
                }
            }
        }
        catch (Exception e) when (e is LeapError or NotConnected or OperationCanceledException)
        {
            return ($"SojournBuild {op}", $"{e.Message}: this will likely fail.");
        }
        return ($"SojournBuild {op}", "");
    }

    static string Balance(AgentSession s) => s.BalanceKnown && s.Client is { } c ? $"Your balance: L${c.Self.Balance}." : "";

    static string Letters(string mct)
    {
        var words = new List<string>();
        if (mct.Contains('M', StringComparison.OrdinalIgnoreCase)) words.Add("modify");
        if (mct.Contains('C', StringComparison.OrdinalIgnoreCase)) words.Add("copy");
        if (mct.Contains('T', StringComparison.OrdinalIgnoreCase)) words.Add("transfer");
        return words.Count == 0 ? "only move" : string.Join(", ", words);
    }

    static string Names(List<Primitive> roots) => Confirmations.Listed(roots.Select(r => $"\"{r.Properties?.Name ?? "an object"}\"").ToList(), 5, "objects");

    /// <summary>The objects named, as the region calls them now (the confirmation shows what's there).</summary>
    static string Objects(AgentSession s, JsonObject a)
    {
        var ids = a.UuidList("ids");
        if (a.Uuid("id") is { } one && !ids.Contains(one)) ids.Add(one);
        if (s.Region is not { } w || ids.Count == 0) return "the objects";
        var roots = ids.Select(id => w.PrimById(id)).OfType<Primitive>().Select(p => s.Objects.RootOf(w, p)).DistinctBy(p => p.LocalID).ToList();
        if (roots.Count == 0) return "objects that aren't here";
        return Confirmations.Listed(roots.Select(r =>
        {
            var parts = s.Objects.Linkset(w, r).Count;
            return $"\"{r.Properties?.Name ?? "an object"}\"" + (parts > 1 ? $" ({parts} parts)" : "");
        }).ToList(), 5, "objects");
    }
}
