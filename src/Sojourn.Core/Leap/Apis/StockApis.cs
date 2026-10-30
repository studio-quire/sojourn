using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Sojourn.Core.Agents;
using Sojourn.Core.Rendering;

namespace Sojourn.Core.Leap;

static class Apis
{
    public static IEnumerable<LeapApi> All(AgentSession s) => new LeapApi[]
    {
        new LLAgent(s), new LLChatBar(s), new LLNotifications(s), new LLInventory(s), new LLViewerControl(s),
        new LLViewerWindow(s), new LLFloaterReg(s), new LLToolMgr(s), new LLGesture(s), new LLAppViewer(s),
        new LLWindow(s), new UIApi(s), new TesselIM(s), new TesselObjects(s), new TesselBuild(s), new TesselAgent(s), new LLAppearance(s), new TesselInventory(s), new TesselGroups(s), new SojournSearch(s), new SojournPeople(s), new SojournLand(s),
    };
}

// ======================================================================================================
// LLAgent
// ======================================================================================================

public sealed class LLAgent : SessionApi
{
    /// <summary>The viewer plays an inventory item's asset; a bare asset id or a built-in name works too.</summary>
    async Task<UUID> AnimIdAsync(JsonObject a, CancellationToken ct)
    {
        if (a.Str("name") is { } name) return GestureOps.BuiltInId(name) ?? throw new LeapError($"no built-in animation {name}");
        var id = a.Uuid("item_id") ?? a.Uuid("anim_id") ?? throw new LeapError("need item_id, anim_id or name");
        await Task.CompletedTask;
        if (S.Inventory.Node(id) is InventoryItem item)  // the inventory is loaded at login; anything else is an asset id
        {
            if (item.AssetType == AssetType.Link && S.Inventory.Node(item.AssetUUID) is InventoryItem t) item = t;
            if (item.AssetType != AssetType.Animation) throw new LeapError($"{item.Name} isn't an animation");
            return item.AssetUUID;
        }
        return id;
    }

    public override string Name => "LLAgent";
    public override string Desc => "The agent: position, nearby avatars and objects, camera, autopilot, touch, sit, teleport";

    public LLAgent(AgentSession s) : base(s)
    {
        Op("getPosition", "-> {quat, euler, region, global}", GetPosition);
        Op("getID", "-> {id}", _ => new JsonObject { ["id"] = C.Self.AgentID.ToString() });
        Op("getGroups", "-> {groups}", _ => new JsonObject
        {
            ["groups"] = new JsonArray(S.State.Groups.Values.Select(g => (JsonNode)new JsonObject
            {
                ["id"] = g.ID.ToString(), ["name"] = g.Name, ["insignia"] = g.InsigniaID.ToString(),
                ["notices"] = g.AcceptNotices, ["display"] = g.ListInProfile, ["contrib"] = g.Contribution,
            }).ToArray()),
        });
        Op("getNearbyAvatarsList", "{dist} -> {result}", NearbyAvatars);
        Op("getNearbyObjectsList", "{dist} -> {result}", NearbyObjects);
        Op("getAttachedObjectsList", "-> {result}", Attached);
        Op("setCameraParams", "refused: the view is the person's own (their head in the headset, their mouse and keys on the desktop)",
            _ => throw new LeapError(NoCamera));
        Op("removeCameraParams", "the camera is always the person's own here, so there's nothing to undo", _ => new JsonObject());
        Op("setFollowCamActive", "{active}: only true (the camera already follows the person); false is refused", a =>
            a.Bool("active", true) ? new JsonObject() : throw new LeapError(NoCamera));
        Op("getCamera", "-> {camera_pos, focus_pos}", _ =>
        {
            var (pos, focus) = S.Camera.Pose(C);
            return new JsonObject { ["camera_pos"] = Json.Arr(pos), ["focus_pos"] = Json.Arr(focus), ["fov"] = S.Camera.Fov };
        });
        Op("startAutoPilot", "{target_global, stop_distance?, allow_flying?}", StartAutoPilot);
        Op("stopAutoPilot", "", _ => { S.Motion.StopAutoPilot(C, false); return new JsonObject(); });
        Op("getAutoPilot", "-> {enabled}", _ => new JsonObject { ["enabled"] = S.Motion.Moving });
        Op("requestTouch", "{obj_uuid | position, at?: [x,y,z] a point on it to touch, face?, hold?: seconds} -> {touched, face, st}: "
            + "lands where at says, else where the person last pointed at it, else its middle as seen from the eye; "
            + "face alone, where the app can find that face of the prim (Sojourn Access), on its middle", TouchAsync);
        Op("requestSit", "{obj_uuid | position} or none for the ground -> {sitting_on?}: an object's seat waits for the region to seat me", SitAsync);
        Op("requestStand", "", _ => { C.Self.Stand(); return new JsonObject(); });
        Op("requestTeleport", "{regionname, x, y, z, skip_confirmation, timeout=12}: raises TeleportViaSLAPP to confirm; skip_confirmation "
            + "teleports now and waits for the arrival -> {teleported, region, pos} (an error with the reason if it failed; "
            + "{requested, arrived: false} if it's still under way)", RequestTeleportAsync);
        Op("playAnimation", "{item_id | anim_id | name}: an animation item of mine (its asset plays), an asset id, or a built-in name", async (a, ct) =>
        {
            var id = await AnimIdAsync(a, ct);
            C.Self.AnimationStart(id, true);
            S.Anims.Started(id, a.Str("name") ?? (a.Uuid("item_id") is { } item ? (S.Inventory.Node(item) as InventoryItem)?.Name : null));
            return new JsonObject();
        });
        Op("stopAnimation", "{item_id | anim_id | name}", async (a, ct) => { C.Self.AnimationStop(await AnimIdAsync(a, ct), true); return new JsonObject(); });
        Op("animations", "sojourn: -> {animations: [{id, name?, built_in, started_by: {id, name, worn, sitting_on?} | text, seconds}], sitting}: "
            + "what's animating me now, and what started each (a dance machine, a seat, an attachment such as an AO)", _ => S.Anims.List());
        Op("stopAnimating", "sojourn: {id?} -> {stopped, stood_up?}: without id, the viewer's Stop animating me: every animation stopped, the "
            + "region's objects' animation permissions revoked (a dance machine can't start it again), standing up if seated; with id, that one",
            async (a, ct) => (JsonNode?)await S.Anims.StopAsync(a.Uuid("id"), ct));
        Op("lookAt", "{obj_uuid | position} -> {heading}: turn the avatar (and with it the desktop view) to face it; refused in the headset, "
            + "where the person turns themselves", LookAtAsync);
        Op("resetAxes", "{lookat?: [x,y,z]}: face along that direction (as lookAt); without one there's nothing to do (the body is always upright)",
            async (a, ct) => a.Vec3("lookat") is { } d ? await FaceAsync(C.Self.SimPosition + d, ct) : new JsonObject());
    }

    JsonNode? GetPosition(JsonObject a)
    {
        var c = C;
        var q = c.Self.SittingOn != 0 ? c.Self.SimRotation : c.Self.Movement.BodyRotation;
        q.GetEulerAngles(out var roll, out var pitch, out var yaw);
        return new JsonObject
        {
            ["quat"] = Json.Arr(q),
            ["euler"] = new JsonObject { ["roll"] = Json.R(roll, 6), ["pitch"] = Json.R(pitch, 6), ["yaw"] = Json.R(yaw, 6) },
            ["region"] = Json.Arr(c.Self.SimPosition),
            ["global"] = Json.Arr(c.Self.GlobalPosition),
        };
    }

    Vector3d Global(Vector3 regionPos)
    {
        Utils.LongToUInts(Sim.Handle, out var gx, out var gy);
        return new Vector3d(gx + regionPos.X, gy + regionPos.Y, regionPos.Z);
    }

    JsonNode? NearbyAvatars(JsonObject a)
    {
        var c = C;
        var w = W;
        var dist = Math.Clamp(a.Num("dist", 256), 1, 512);
        var me = c.Self.SimPosition;
        var result = new JsonArray();
        var seen = new HashSet<UUID>();
        foreach (var rw in S.VisibleRegions())
            foreach (var av in rw.Avatars.Values)
            {
                if (av.ID == c.Self.AgentID || !seen.Add(av.ID)) continue;
                var pos = TesselAgent.AvatarPos(rw, av) + S.FrameOffset(rw);   // the agent's region frame, as the viewer reports
                if (Vector3.Distance(pos, me) > dist) continue;
                result.Add(new JsonObject
                {
                    ["id"] = av.ID.ToString(),
                    ["name"] = S.Names.Json(av.ID)?["username"]?.GetValue<string>() ?? av.Name ?? "",
                    ["region_pos"] = Json.Arr(pos), ["global_pos"] = Json.Arr(Global(pos)),
                    ["region_id"] = (rw.Simulator?.RegionID ?? UUID.Zero).ToString(),
                });
            }
        var o = new JsonObject();
        if (result.Count > 0) o["result"] = result;
        return o;
    }

    JsonNode? NearbyObjects(JsonObject a)
    {
        var c = C;
        var w = W;
        var dist = Math.Clamp(a.Num("dist", 256), 1, 512);
        var me = c.Self.SimPosition;
        var result = new JsonArray();
        foreach (var p in w.Prims.Values)
        {
            if (p.PrimData.PCode != PCode.Prim) continue;
            if (S.Objects.IsAttachment(w, p, out _, out _)) continue;
            var pos = w.RegionPosition(p);
            if (Vector3.Distance(pos, me) > dist) continue;
            result.Add(new JsonObject
            {
                ["id"] = p.ID.ToString(), ["global_pos"] = Json.Arr(Global(pos)), ["region_pos"] = Json.Arr(pos),
                ["region_id"] = Sim.RegionID.ToString(),
            });
        }
        var o = new JsonObject();
        if (result.Count > 0) o["result"] = result;
        return o;
    }

    JsonNode? Attached(JsonObject a)
    {
        var w = W;
        var me = w.AvatarById(C.Self.AgentID);
        var result = new JsonArray();
        if (me is not null)
            foreach (var p in w.Prims.Values.Where(p => p.ParentID == me.LocalID))
                result.Add(new JsonObject
                {
                    ["id"] = p.ID.ToString(), ["name"] = p.Properties?.Name ?? "",
                    ["attachment"] = ObjectViews.AttachPointName((int)p.PrimData.AttachmentPoint),
                    ["item_id"] = AgentInventory.AttachItemId(p).ToString(),
                });
        var o = new JsonObject();
        if (result.Count > 0) o["result"] = result;
        return o;
    }

    // the client's pilots set the camera every frame, from the person's head or their mouse and keys
    const string NoCamera = "the view is the person's own: their head moves it in the headset, their mouse and keys on the desktop; the camera can't be set";

    JsonNode? StartAutoPilot(JsonObject a)
    {
        var t = a.Nums("target_global") ?? throw new LeapError("bad request: Map missing keys: target_global");
        if (t.Length < 3) throw new LeapError("target_global: [x, y, z]");
        S.Motion.StartAutoPilot(C, new Vector3d(t[0], t[1], t[2]), a.Num("stop_distance", 0), a.Bool("allow_flying"));
        return new JsonObject();
    }

    Primitive? Target(JsonObject a, bool volumesOnly)
    {
        var w = W;
        if (a.Uuid("obj_uuid") is { } id) return w.PrimById(id);
        if (a.Vec3("position") is { } pos)
            return w.Prims.Values
                .Where(p => (!volumesOnly || p.PrimData.PCode == PCode.Prim) && !S.Objects.IsAttachment(w, p, out _, out _))
                .OrderBy(p => Vector3.Distance(w.RegionPosition(p), pos)).FirstOrDefault();
        return null;
    }

    /// <summary>A click on the object as the viewer's: where on its surface it lands (the point given, else where the
    /// person last pointed at it, else its middle as seen from the eye), so faces, buttons drawn on one face and
    /// llDetectedTouchST/UV work; held for <c>hold</c> seconds between the press and the release.</summary>
    async Task<JsonNode?> TouchAsync(JsonObject a, CancellationToken ct)
    {
        var p = Target(a, true) ?? throw new LeapError("requestTouch could not find the target");
        var at = a.Vec3("at");
        TouchPoint? hit = null;
        if (at == null && a.Has("face") && S.FindFaceSurface is { } findFace)
            try { hit = await findFace(p.ID, a.Int("face", 0), ct); } catch (OperationCanceledException) { throw; } catch { }
        if (hit == null && S.FindTouchSurface is { } find)
            try { hit = await find(p.ID, at, ct); } catch (OperationCanceledException) { throw; } catch { }
        ulong region = Sim.Handle;
        uint local = p.LocalID;
        var id = p.ID;
        var surface = TouchSurface.None;
        if (hit is { } h) (region, local, id, surface) = (h.Region, h.LocalId, h.Id, h.Surface);
        if (a.Has("face")) surface = surface with { Face = a.Int("face", 0) };
        S.SendGrab(region, local, surface);
        var hold = Math.Clamp(a.Num("hold", 0), 0, 10);
        if (hold > 0) await Task.Delay(TimeSpan.FromSeconds(hold), CancellationToken.None);
        S.SendDeGrab(region, local, surface);
        var o = new JsonObject { ["touched"] = id.ToString() };
        if (surface.Face >= 0)
        {
            o["face"] = surface.Face;
            o["st"] = new JsonArray(Math.Round(surface.ST.X, 3), Math.Round(surface.ST.Y, 3));
        }
        return o;
    }

    async Task<JsonNode?> SitAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        if (!a.Has("obj_uuid") && !a.Has("position"))
        {
            c.Self.SitOnGround();
            return new JsonObject();
        }
        var p = Target(a, true) ?? throw new LeapError("requestSit could not find the sit target");
        c.Self.RequestSit(p.ID, Vector3.Zero);   // the region's answer is taken up in AgentSession (AvatarSitResponse -> Sit)
        for (var i = 0; i < 25 && c.Self.SittingOn == 0; i++) await Task.Delay(200, ct);
        if (c.Self.SittingOn == 0) throw new LeapError("the region didn't seat me there (too far away, or no room to sit on it)");
        return new JsonObject { ["sitting_on"] = p.ID.ToString() };
    }

    async Task<JsonNode?> LookAtAsync(JsonObject a, CancellationToken ct)
    {
        var w = W;
        Vector3? target = a.Uuid("obj_uuid") is { } id
            ? (w.PrimById(id) is { } p ? w.RegionPosition(p) : throw new LeapError("that object isn't in view"))
            : a.Vec3("position");
        return await FaceAsync(target ?? throw new LeapError("need obj_uuid or position"), ct);
    }

    /// <summary>Turns the body toward a point with a short API move, which the desktop pilot follows (so its view
    /// turns too: Pilot.Update takes a body turned by anyone else as the view's heading).</summary>
    async Task<JsonNode> FaceAsync(Vector3 target, CancellationToken ct)
    {
        if (S.Headset) throw new LeapError("in the headset the person turns the view themselves (their head, or the stick), and the avatar faces where they look");
        var c = C;
        var d = target - c.Self.SimPosition;
        if (d.X * d.X + d.Y * d.Y < 1e-4) throw new LeapError("that's straight above or below me");
        var heading = 90 - Math.Atan2(d.Y, d.X) * 180 / Math.PI;
        await S.Motion.StartMoveAsync(c, new JsonObject { ["face"] = heading, ["secs"] = 0.3, ["wait"] = true }, ct);
        return new JsonObject { ["heading"] = Motion.Heading(c.Self.Movement.BodyRotation) };
    }

    async Task<JsonNode?> RequestTeleportAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var region = a.Str("regionname") ?? Sim.Name;
        var pos = new Vector3((float)a.Num("x", 128), (float)a.Num("y", 128), (float)a.Num("z", 30));
        var location = $"{region}/{(int)pos.X}/{(int)pos.Y}/{(int)pos.Z}";
        if (a.Bool("skip_confirmation", false))
        {
            // as the viewer's LLAgentListener: a trusted request teleports without the SLapp dialog
            var tp = S.TeleportAsync(region, pos);
            if (await Task.WhenAny(tp, Task.Delay(TimeSpan.FromSeconds(Math.Clamp(a.Num("timeout", 12), 1, 60)), ct)) != tp)
                return new JsonObject { ["requested"] = true, ["arrived"] = false, ["to"] = location };
            var (ok, why) = await tp;
            if (!ok) throw new LeapError($"teleport to {location} failed: {why}");
            return new JsonObject { ["teleported"] = true, ["region"] = C.Network.CurrentSim?.Name, ["pos"] = Json.Arr(C.Self.SimPosition) };
        }
        S.Notify.Add("TeleportViaSLAPP", "alertmodal", $"Are you sure you want to teleport to <nolink>{location}</nolink>?",
            Notifications.OkCancelIgnore("Teleport"),
            substitutions: new JsonObject { ["LOCATION"] = location },
            respond: (button, _) =>
            {
                if (button == "OK_okcancelignore") Fire(S.TeleportAsync(region, pos));
                return Task.CompletedTask;
            });
        return new JsonObject();
    }
}

// ======================================================================================================
// LLChatBar
// ======================================================================================================

public sealed class LLChatBar : SessionApi
{
    public override string Name => "LLChatBar";
    public override string Desc => "Nearby chat";

    static readonly Regex ChannelPrefix = new(@"^/(-?\d+)\s?(.*)$", RegexOptions.Singleline);

    public LLChatBar(AgentSession s) : base(s)
    {
        Op("sendChat", "{message, channel=0, type=normal|whisper|shout}", SendChat);
    }

    JsonNode? SendChat(JsonObject a)
    {
        var c = C;
        var msg = a.Str("message") ?? "";
        var channel = a.Int("channel", 0);
        if (channel == 0 && ChannelPrefix.Match(msg) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var ch))
        {
            channel = ch;
            msg = m.Groups[2].Value;
        }
        var type = (a.Str("type") ?? "normal") switch { "whisper" => ChatType.Whisper, "shout" => ChatType.Shout, _ => ChatType.Normal };
        if (channel < 0)
        {
            // the viewer sends negative channels as a dialog reply from itself
            var pkt = new ScriptDialogReplyPacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
                Data = { ObjectID = c.Self.AgentID, ChatChannel = channel, ButtonIndex = 0, ButtonLabel = Utils.StringToBytes(msg) },
            };
            c.Network.SendPacket(pkt, Sim);
        }
        else c.Self.Chat(msg, channel, type);
        if (channel == 0 && msg.Length > 0) S.State.LastWords = msg;
        return new JsonObject();
    }
}

// ======================================================================================================
// LLNotifications
// ======================================================================================================

public sealed class LLNotifications : SessionApi
{
    public override string Name => "LLNotifications";
    public override string Desc => "Notifications: respond, cancel (they arrive as ClaudeNotify events)";

    public LLNotifications(AgentSession s) : base(s)
    {
        Op("respond", "{uuid, response}: uuid may be a unique prefix; buttons match by name, else by label", async (a, _) =>
        {
            var id = S.Notify.Resolve(a.Str("uuid"));
            if (id is null) return Json.Error($"no notification waiting with id {a.Str("uuid")}");
            return await S.Notify.RespondAsync(id.Value, a["response"]) is { } err ? Json.Error(err) : new JsonObject();
        });
        Op("cancel", "{uuid}", async (a, _) =>
        {
            var id = S.Notify.Resolve(a.Str("uuid"));
            if (id is null) return Json.Error($"no notification waiting with id {a.Str("uuid")}");
            return await S.Notify.CancelAsync(id.Value) is { } err ? Json.Error(err) : new JsonObject();
        });
        Op("forward", "{channel, pump, ...}: every notification is already logged as ClaudeNotify", _ => new JsonObject());
        Op("requestAdd", "{name, substitutions?, payload?}: raise a notification (testing)", a =>
        {
            var name = a.Str("name") ?? "SystemMessage";
            var subs = a["substitutions"] as JsonObject ?? new JsonObject();
            var msg = subs.Str("MESSAGE") ?? name;
            S.Notify.Add(name, "notify", msg, Notifications.Ok, a["payload"]?.DeepClone() as JsonObject, (JsonObject)subs.DeepClone(),
                respond: (_, _) => Task.CompletedTask);
            return new JsonObject();
        });
        Op("listChannels", "", _ => new JsonObject { ["Visible"] = new JsonObject() });
        // what came in a spell of incognito stays the person's own, but while a seeing spell lasts (Agents/PrivateMode.cs)
        Op("listChannelNotifications", "{channel}", _ => new JsonObject { ["notifications"] = new JsonArray(S.Notify.All
            .Where(n => !DateTimeOffset.TryParse(n.Str("time"), out var t) || S.Private.ShownAt(t.ToUnixTimeMilliseconds() / 1000.0))
            .Select(n => (JsonNode)n.DeepClone()).ToArray()) });
        Op("ignore", "refused: no notification kinds are ignored here; answer or cancel each one", _ =>
            throw new LeapError("Sojourn doesn't keep a list of ignored notifications; answer or cancel each one (respond, cancel)"));
    }

    public override bool NeedsSession => false;
}

// ======================================================================================================
// LLInventory
// ======================================================================================================

public sealed class LLInventory : SessionApi
{
    public override string Name => "LLInventory";
    public override string Desc => "My inventory (loaded in the background at login)";

    public LLInventory(AgentSession s) : base(s)
    {
        Op("getBasicFolderID", "{ft_name} -> {id}", a =>
        {
            var name = a.Str("ft_name") ?? "";
            var ft = S.Inventory.FolderTypeFrom(name) ?? throw new LeapError($"no system folder type {name}");
            var id = S.Inventory.FolderFor(ft);
            if (id == UUID.Zero) throw new LeapError($"my inventory has no {name} folder (or hasn't loaded yet)");
            return new JsonObject { ["id"] = id.ToString() };
        });
        Op("getItemsInfo", "{item_ids} -> {items?, categories?}", a => S.Inventory.ItemsInfo(a.UuidList("item_ids")));
        Op("getDirectDescendants", "{folder_id} -> {items?, categories?}", async (a, ct) =>
            await S.Inventory.DirectDescendantsAsync(a.Uuid("folder_id") ?? UUID.Zero, ct));
        Op("collectDescendantsIf", "{folder_id, name?, desc?, type?, filter_links?, limit?} -> {items?, categories?}", async (a, ct) =>
            await S.Inventory.CollectAsync(a.Uuid("folder_id") ?? S.Inventory.FolderFor(FolderType.Root), a.Str("name"), a.Str("desc"),
                a.Str("type"), a.Str("filter_links"), a.Int("limit", 0), ct));
    }
}

// ======================================================================================================
// LLViewerControl: the viewer's settings, kept per agent (CameraAngle, RenderFarClip, ...)
// ======================================================================================================

public sealed class LLViewerControl : SessionApi
{
    public override string Name => "LLViewerControl";
    public override string Desc => "Viewer settings: RenderFarClip (the draw distance, metres) and CameraAngle (the field of view, radians); others are refused";
    public override bool NeedsSession => false;

    // only settings something reads: a stored value nothing honours would answer "done" to a change that never happens
    static readonly Dictionary<string, (string Type, JsonNode Value, string Comment)> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CameraAngle"] = ("F32", 1.047198, "Vertical field of view (radians, 0.17-2.97): the desktop view, snapshots and the camera the region hears of; the headset's lenses set its own"),
        ["RenderFarClip"] = ("F32", 128.0, $"Draw distance (metres, {CameraControl.MinFarClip}-{CameraControl.MaxFarClip}): how far you see and how far the region sends objects"),
    };

    static string Known => string.Join(", ", Defaults.Select(d => $"{d.Key} ({d.Value.Comment})"));

    public LLViewerControl(AgentSession s) : base(s)
    {
        Op("get", "{group, key} -> {name, type, value, comment}", a =>
        {
            var key = a.Str("key") ?? "";
            var group = a.Str("group") ?? "Global";
            if (group is not ("Global" or "PerAccount" or "Warnings" or "CrashSettings")) throw new LeapError($"Unrecognized group '{group}'");
            if (S.State.Settings.TryGetValue(key, out var v))
                return new JsonObject { ["name"] = key, ["type"] = Defaults.TryGetValue(key, out var d0) ? d0.Type : "LLSD", ["value"] = v?.DeepClone(), ["comment"] = "" };
            if (Defaults.TryGetValue(key, out var d))
            {
                var value = key.Equals("RenderFarClip", StringComparison.OrdinalIgnoreCase) ? S.Config.DrawDistance : d.Value.DeepClone();
                return new JsonObject { ["name"] = key, ["type"] = d.Type, ["value"] = value, ["comment"] = d.Comment };
            }
            throw new LeapError($"In group '{group}', unrecognized control key '{key}'");
        });
        Op("set", "{group, key, value?} -> {name, value}: RenderFarClip or CameraAngle (no value: back to the default); anything else is refused with the reason", a =>
        {
            var given = a.Str("key") ?? "";
            if (!Defaults.TryGetValue(given, out var d))
                throw new LeapError($"Sojourn has no setting '{given}'; it has {Known}");
            var key = Defaults.Keys.First(k => k.Equals(given, StringComparison.OrdinalIgnoreCase));
            if (S.SettingRefusal?.Invoke(key) is { } why) throw new LeapError(why);
            if (a.Has("value"))
            {
                if (Json.AsDouble(a["value"]) is not { } v || double.IsNaN(v)) throw new LeapError($"{key} takes a number");
                if (key == "RenderFarClip" && v is < CameraControl.MinFarClip or > CameraControl.MaxFarClip)
                    throw new LeapError($"the draw distance goes from {CameraControl.MinFarClip} to {CameraControl.MaxFarClip} metres");
                if (key == "CameraAngle" && v is < 0.17 or > 2.97) throw new LeapError("the field of view goes from 0.17 to 2.97 radians (10-170 degrees)");
                S.State.Settings[key] = v;
            }
            else S.State.Settings.TryRemove(key, out _);
            if (S.Client is { } c) S.Camera.Update(c);
            S.OnSettingChanged(key);
            return new JsonObject { ["name"] = key, ["value"] = key == "RenderFarClip" ? S.Camera.FarClip : S.Camera.Fov };
        });
    }
}

// ======================================================================================================
// LLViewerWindow: snapshots go to the renderer (phase 2)
// ======================================================================================================

public sealed class LLViewerWindow : SessionApi
{
    public override string Name => "LLViewerWindow";
    public override string Desc => "Snapshots: the person's view as the next frame shows it (never a moved camera)";

    public LLViewerWindow(AgentSession s) : base(s)
    {
        Op("saveSnapshot", "{filename, width?, height?, tool?, showui?, close?}: the next frame, scaled to fit, without the panel; "
            + "showui: true -> as the person sees it, the panel, feed, cards and other menus over it (the headset's layers composited "
            + "as its compositor does), close?: one of them (panel, feed, reader, keyboard, card ...) also saved close up as "
            + "<filename>-<close>.png -> {ok, width, height, layers, left_out, close} (showhud doesn't apply; type COLOR only)", SnapAsync);
        Op("saveSnapshotWithoutUI", "{filename, width?, height?, tool?}", (a, ct) => { a["showui"] = false; return SnapAsync(a, ct); });
    }

    async Task<JsonNode?> SnapAsync(JsonObject a, CancellationToken ct)
    {
        var provider = Snapshots.Provider;
        if (provider is null) return new JsonObject { ["ok"] = false, ["error"] = Snapshots.NotAvailable };
        var c = C;
        var (pos, focus) = S.Camera.Pose(c);
        var file = a.Str("filename") ?? throw new LeapError("bad request: Map missing keys: filename");
        if (a.Str("type") is { } type && !type.Equals("COLOR", StringComparison.OrdinalIgnoreCase))
            throw new LeapError($"snapshots here are the person's view in colour; type {type} isn't available");
        var r = await provider.RenderAsync(new SnapshotRequest
        {
            Agent = S, World = W, CameraPos = pos, Focus = focus, Fov = S.Camera.Fov, FarClip = S.Camera.FarClip,
            Width = a.Int("width", 1280), Height = a.Int("height", 720), ShowUi = a.Bool("showui", false),
            ShowHud = a.Bool("showhud", true), Type = a.Str("type") ?? "COLOR", FileName = file,
            HiddenAvatars = S.HiddenAvatars().ToArray(),
            Tool = a.Str("tool") is { Length: > 0 } t ? (t.Length > 40 ? t[..40] : t) : null,
            Close = a.Str("close") is { Length: > 0 } close ? close : null,
        }, ct);
        var o = new JsonObject { ["ok"] = r.Ok };
        if (r.Error is not null) o["error"] = r.Error;
        if (r.Ok && r.Width > 0) { o["width"] = r.Width; o["height"] = r.Height; }
        if (r.Layers is { } layers) o["layers"] = new JsonArray(layers.Select(l => (JsonNode?)l).ToArray());
        if (r.LeftOut is { Count: > 0 } left) o["left_out"] = new JsonArray(left.Select(l => (JsonNode?)l).ToArray());
        if (r.Close is not null) o["close"] = r.Close;
        if (r.CloseError is not null) o["close_error"] = r.CloseError;
        return o;
    }
}

// ======================================================================================================
// The viewer's UI surfaces: Sojourn has none of the viewer's floaters or tools (the assistant's panel and the
// settings menu are its only UI), so these say so rather than answer as if something opened
// ======================================================================================================

public sealed class LLFloaterReg : SessionApi
{
    public override string Name => "LLFloaterReg";
    public override string Desc => "Floaters: Sojourn has none of the viewer's windows; the agent API's own ops do what they did";
    public override bool NeedsSession => false;
    const string NoFloaters = "Sojourn has none of the viewer's floaters (its UI is the assistant's panel and the settings menu); "
        + "the agent API's ops do what they did (TesselInventory, TesselBuild, TesselAgent parcel*, TesselGroups, ...)";

    public LLFloaterReg(AgentSession s) : base(s)
    {
        Op("instanceVisible", "{name} -> {visible: false}: no floater is ever open", _ => new JsonObject { ["visible"] = false });
        foreach (var op in new[] { "showInstance", "hideInstance", "toggleInstance", "clickButton" })
            Op(op, "refused: " + NoFloaters, _ => throw new LeapError(NoFloaters));
        Op("getBuildMap", "-> {}: no floaters", _ => new JsonObject());
    }
}

public sealed class LLToolMgr : SessionApi
{
    public override string Name => "LLToolMgr";
    public override string Desc => "Tools: none (TesselBuild builds and edits without the viewer's tools)";
    public override bool NeedsSession => false;
    const string NoTools = "Sojourn has no build floater or tools to select; TesselBuild creates and edits objects directly";

    public LLToolMgr(AgentSession s) : base(s)
    {
        Op("openBuildFloater", "refused: " + NoTools, _ => throw new LeapError(NoTools));
        Op("selectTool", "refused: " + NoTools, _ => throw new LeapError(NoTools));
        Op("getToolsInfo", "-> {}: no tools", _ => new JsonObject());
    }
}

public sealed class LLGesture : SessionApi
{
    public override string Name => "LLGesture";
    public override string Desc => "Gestures";
    readonly System.Collections.Concurrent.ConcurrentDictionary<UUID, CancellationTokenSource> _playing = new();

    public LLGesture(AgentSession s) : base(s)
    {
        Op("getActiveGestures", "-> {gestures: {item_id: {name, trigger, playing}}}", _ =>
        {
            var g = new JsonObject();
            foreach (var (item, asset) in C.Self.ActiveGestures)
            {
                var name = (S.Inventory.Node(item) as InventoryItem)?.Name ?? "";
                g[item.ToString()] = new JsonObject
                {
                    ["name"] = name, ["trigger"] = GestureOps.KnownTrigger(asset) ?? "", ["playing"] = _playing.ContainsKey(item),
                    ["asset_id"] = asset.ToString(),
                };
            }
            return new JsonObject { ["gestures"] = g };
        });
        Op("startGesture", "{id} -> {playing: true}: an active gesture's item id (getActiveGestures; TesselInventory activateGesture first)", a =>
        {
            var id = a.Uuid("id") ?? throw new LeapError("need id (a gesture's item id)");
            if (!C.Self.ActiveGestures.ContainsKey(id))
                throw new LeapError(S.Inventory.Node(id) is InventoryItem { AssetType: AssetType.Gesture } g
                    ? $"{g.Name} isn't active; activate it first (TesselInventory activateGesture)"
                    : "no active gesture has that id (getActiveGestures lists them)");
            var cts = new CancellationTokenSource();
            _playing[id] = cts;
            _ = S.Gestures.PlayAsync(id, cts.Token).ContinueWith(_ => _playing.TryRemove(id, out var _), TaskScheduler.Default);
            return new JsonObject { ["playing"] = true };
        });
        Op("stopGesture", "{id} -> {was_playing}", a =>
        {
            var id = a.Uuid("id") ?? throw new LeapError("need id (a gesture's item id)");
            var was = _playing.TryRemove(id, out var cts);
            cts?.Cancel();
            return new JsonObject { ["was_playing"] = was };
        });
        Op("isGesturePlaying", "{id}", a => new JsonObject { ["playing"] = a.Uuid("id") is { } id && _playing.ContainsKey(id) });
    }
}

public sealed class LLAppViewer : SessionApi
{
    public override string Name => "LLAppViewer";
    public override string Desc => "Quitting: Sojourn logs out and closes";
    public override bool NeedsSession => false;

    public LLAppViewer(AgentSession s) : base(s)
    {
        Op("requestQuit", "-> {quitting}: log out and close Sojourn", _ => Quit());
        Op("forceQuit", "-> {quitting}: as requestQuit", _ => Quit());
    }

    JsonObject Quit()
    {
        // the client closes as its window's close button does (logging out on the way); without one, log out
        if (S.Quit()) return new JsonObject { ["quitting"] = true };
        Fire(S.LogoutAsync());
        return new JsonObject { ["quitting"] = false, ["logging_out"] = true };
    }
}

public sealed class LLWindow : SessionApi
{
    public override string Name => "LLWindow";
    public override string Desc => "Window input: refused (the window's keys and mouse are the person's)";
    public override bool NeedsSession => false;
    const string NoWindow = "the window's keys and mouse are the person's and the agent API doesn't drive them; use the ops instead (TesselAgent move, LLAgent requestTouch, ...)";

    public LLWindow(AgentSession s) : base(s)
    {
        foreach (var op in new[] { "keyDown", "keyUp", "mouseMove", "mouseDown", "mouseUp", "mouseScroll", "getInfo", "pasteText", "cut", "copy", "paste", "selectAll" })
            Op(op, NoWindow, _ => Json.Error(NoWindow));
        Op("getPaths", "-> {paths: []}", _ => new JsonObject { ["paths"] = new JsonArray() });
    }
}

public sealed class UIApi : SessionApi
{
    public override string Name => "UI";
    public override string Desc => "The viewer's UI calls: World.EnvSettings alone (Sojourn has none of the viewer's other menus or panels)";
    public override bool NeedsSession => false;

    /// <summary>World > Environment's choices by name (sunrise, noon, legacy noon, sunset, midnight, region, pause_clouds):
    /// null when done, else why not (the client's SojournEnvironment sets it).</summary>
    public static Func<string?, string?>? EnvSettings;

    public UIApi(AgentSession s) : base(s)
    {
        Op("call", "{function, parameter?}: World.EnvSettings {parameter: sunrise|noon|legacy noon|sunset|midnight|region|pause_clouds}; "
            + "anything else refused, with the reason", a =>
        {
            var fn = a.Str("function") ?? "";
            if (fn == "World.EnvSettings" && EnvSettings is { } env)
                return env(a["parameter"]?.ToString()) is { } why ? throw new LeapError(why) : new JsonObject();
            throw new LeapError($"function \"{fn}\" not found: Sojourn has none of the viewer's menus");
        });
        Op("getValue", "{path}: refused (no viewer UI to read)", _ => throw new LeapError("Sojourn has none of the viewer's panels to read values from"));
    }
}
