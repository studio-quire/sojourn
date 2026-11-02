using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Core.Leap;

public sealed class TesselAgent : SessionApi
{
    public override string Name => "TesselAgent";
    public override string Desc => "Being a resident: money, giving, wearing, friends, groups, places, people, moving";

    public TesselAgent(AgentSession s) : base(s)
    {
        Op("balance", "-> {balance, known}", _ => new JsonObject { ["balance"] = C.Self.Balance, ["known"] = S.BalanceKnown });
        Op("pay", "{to, amount, desc?} -> {ok, balance} once the grid confirms ({sent, confirmed: false} if it hasn't yet; an error if it refused): "
            + "an avatar, or an object in view that takes money", PayAsync);
        Op("give", "{to, item_id | folder_id}: sent to them as an inventory offer they accept or decline", GiveAsync);
        Op("attach", "{item_id, point?, replace?}", AttachAsync);
        Op("detach", "{item_id | object_id}", DetachAsync);
        Op("attachPoints", "-> {points}", AttachPoints);
        Op("offerFriendship", "{to, message?}", a => { C.Friends.OfferFriendship(To(a), a.Str("message") ?? ""); return new JsonObject(); });
        Op("offerTeleport", "{to | ids, message?}", a =>
        {
            var ids = IdsOf(a, "ids", "to");
            if (ids.Count == 0) throw new LeapError("need ids or to");
            foreach (var id in ids) C.Self.SendTeleportLure(id, a.Str("message") ?? "Join me");
            return new JsonObject();
        });
        Op("requestTeleport", "{to, message?}", a => { C.Self.SendTeleportLureRequest(To(a), a.Str("message") ?? ""); return new JsonObject(); });
        Op("joinGroup", "{group_id} -> {fee, can_afford}: a JoinGroupCanAfford (fee) or JoinGroupNoCost notification asks to confirm; JoinGroupCannotAfford if the fee is more than I have",
            JoinGroupAsync);
        Op("leaveGroup", "{group_id}: a GroupLeaveConfirmMember notification asks to confirm", LeaveGroup);
        Op("activateGroup", "{group_id}: one of my groups (the zero id: none)", a =>
        {
            var gid = a.Uuid("group_id") ?? UUID.Zero;
            if (gid != UUID.Zero && !S.IsMyGroup(gid)) throw new LeapError("I'm not in that group");
            C.Groups.ActivateGroup(gid);
            return new JsonObject { ["active_group"] = gid.ToString() };
        });
        Op("setHome", "set home here -> {message}: the region's answer (it also arrives as an alert)", async (_, ct) =>
        {
            var c = C;
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            S.LastAlert = tcs;
            try
            {
                c.Self.SetHome();
                var msg = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(6), ct);
                return new JsonObject { ["message"] = msg };
            }
            catch (TimeoutException) { throw new LeapError("the region didn't answer, so whether home is set here isn't known"); }
            finally { S.LastAlert = null; }
        });
        Op("regions", "sojourn: {range=8} -> {regions: [{name, x, y, agents, here}]}: regions on the world map around me (x, y in region units; teleport by name)",
            RegionsAsync);
        Op("parcelInfo", "sojourn: -> {name, desc, music_url, media_url, landing, landing_point, options, owner_id, area}: About Land here", (_, ct) => Wrap(S.Parcels.InfoAsync(ct)));
        Op("parcelSet", "sojourn: {name?, desc?, music_url?, media_url?, snapshot_id?, landing? (here|anywhere|blocked), options? {fly, scripts, group_scripts, landmark, damage, push, show_in_search, object_entry, terraform, sound_local}} -> About Land",
            // the parcel I stand on, as its confirmation describes it (SojournLand set takes `at`)
            (a, ct) => { a.Remove("at"); a.Remove("local_id"); a.Remove("region"); return Wrap(S.Parcels.SetAsync(a, ct)); });
        Op("parcelObjects", "sojourn: -> {owners: [{owner_id, name, prims, newest}]}: who has objects on this parcel (owner only)", (_, ct) => Wrap(S.Parcels.ObjectsAsync(ct)));
        Op("parcelReturn", "sojourn: {owner_id | owner_ids | others: true}: return objects on this parcel to their owners", (a, ct) => Wrap(S.Parcels.ReturnAsync(a, ct)));
        Op("parcelEject", "sojourn: {to, ban?} -> {ejected}: eject (and ban) someone from my parcel; an error if the region left them where they were",
            (a, ct) => Wrap(S.Parcels.EjectAsync(a, ct)));
        Op("profile", "sojourn: {id?=me} -> {name, about, image_id, born, url, first_life, partner?, interests?, picks}", (a, ct) => Wrap(S.Profiles.ProfileAsync(a, ct)));
        Op("setProfile", "sojourn: {about?, image_id?, url?, first_life?, first_life_image_id?, want_to?, skills?, languages?} -> my profile",
            (a, ct) => Wrap(S.Profiles.SetProfileAsync(a, ct)));
        Op("pick", "sojourn: {pick_id, id?} -> {name, desc, region, parcel, pos, image_id}", (a, ct) => Wrap(S.Profiles.PickAsync(a, ct)));
        Op("createPick", "sojourn: {name?, desc?, image_id?, pick_id?} -> {pick_id}: a pick of where I stand (pick_id: edit that one)",
            (a, ct) => Wrap(S.Profiles.CreatePickAsync(a, ct)));
        Op("deletePick", "sojourn: {pick_id}", a => S.Profiles.DeletePick(a));
        Op("mute", "sojourn: {id | name, type?}: block a resident or object (their chat, IMs, offers, dialogs stop reaching me; their voice "
            + "is SojournVoice peerMute); type resident|object|name", MuteAsync);
        Op("unmute", "sojourn: {id | name}", a =>
        {
            var gone = S.Mutes.Remove(a.Uuid("id") ?? UUID.Zero, a.Str("name"), C) ?? throw new LeapError("not on my block list");
            return new JsonObject { ["unmuted"] = gone.Name, ["id"] = gone.Id.ToString() };
        });
        Op("mutes", "sojourn: -> {mutes: [{id, name, type}]}: my block list", _ => new JsonObject { ["mutes"] = S.Mutes.List() });
        Op("goHome", "sojourn: teleport home (LLTeleport events follow)", a => { Fire(C.Self.GoHomeAsync()); return new JsonObject { ["requested"] = true }; });
        Op("parcel", "-> {region, parcel}", ParcelAsync);
        Op("ground", "sojourn: {points: [[x, y], ...]} -> {heights: [z or null, ...]}: the terrain's height at points in this region (null: not heard yet), for placing builds",
            a =>
            {
                var w = S.Region ?? throw new LeapError("no region");
                var heights = new JsonArray();
                foreach (var p in (a["points"] as JsonArray ?? throw new LeapError("give points: [[x, y], ...]")).Take(256))
                {
                    var xy = (p as JsonArray)?.Select(v => (float)(v?.GetValue<double>() ?? double.NaN)).ToArray();
                    if (xy is not { Length: >= 2 } || float.IsNaN(xy[0]) || float.IsNaN(xy[1])) throw new LeapError("each point is [x, y]");
                    var h = w.TerrainHeight(xy[0], xy[1]);
                    heights.Add(h is { } z ? JsonValue.Create(Math.Round(z, 2)) : null);
                }
                return new JsonObject { ["heights"] = heights };
            });
        Op("people", "{dist=64} -> {people}", People);
        Op("friends", "-> {friends}", Friends);
        Op("names", "{ids, timeout=5} -> {names}", NamesAsync);
        Op("findAvatar", "sojourn: {name, limit=20, timeout=8} -> {people: [{id, username, display_name}]}: anyone on the grid by name (the avatar picker), seen or not",
            FindAvatarAsync);
        Op("teleportLandmark", "sojourn: {item_id | asset_id}: teleport by landmark; LLTeleport events follow", (a, ct) => Wrap(S.InventoryOps.TeleportLandmarkAsync(a, ct)));
        Op("move", "{forward?, strafe?, up?, turn?, secs=1, run?, fly?, face?, wait?}: turn and face are refused in the headset (the person turns themselves)",
            (a, ct) =>
            {
                if (S.Headset && (a.Num("turn", 0) != 0 || a.Has("face")))
                    throw new LeapError("in the headset the person turns themselves (their head, or the stick); move can walk, run and fly, not turn them");
                return Wrap(S.Motion.StartMoveAsync(C, a, ct));
            });
        Op("stop", "end a move now", _ => S.Motion.Stop(C));
        Op("wearables", "-> {bakes_on_mesh, wearables: [{type, index, item_id, asset_id, name, modifiable, unsaved, textures, color}]}",
            async (_, ct) => await S.Wardrobe.ListAsync(ct));
        Op("playSound", "sojourn: {item_id | sound_id, gain=1}: play a sound in-world where I stand (the inventory's Play Inworld)", async (a, ct) =>
        {
            var c = C;
            var id = a.Uuid("sound_id") ?? UUID.Zero;
            if (a.Uuid("item_id") is { } iid)
            {
                var item = await S.Inventory.ItemAsync(iid, ct) ?? throw new LeapError("no such item");
                if (item.AssetType != AssetType.Sound) throw new LeapError("that item isn't a sound");
                id = item.AssetUUID;
            }
            if (id == UUID.Zero) throw new LeapError("need item_id or sound_id");
            c.Sound.SendSoundTrigger(id, c.Self.SimPosition, (float)Math.Clamp(a.Num("gain", 1), 0, 1));
            return new JsonObject { ["sound_id"] = id.ToString() };
        });
        Op("sounds", "sojourn: -> {loops, music_url}: what I can hear looping now, loudest first", _ =>
            new JsonObject { ["loops"] = S.Ears.Soundscape(C), ["music_url"] = S.Ears.MusicUrl ?? "" });
        Op("rebake", "rebake my appearance (client bakes are re-sent; server baking is asked again)", _ =>
        {
            S.Look.Schedule(C, TimeSpan.Zero, reupload: true, "rebake");
            return new JsonObject();
        });
        Op("upload", "{path, name?, desc?, folder_id?, type?, next_perms?, max_size=1024, priority?, loop?, quote?, max_cost?, timeout=120}", (a, ct) => S.Uploads.UploadAsync(a, ct));
        Op("uploadMesh", "{path, name?, desc?, scale?, textures?, lods=true, physics=hull|box, folder_id?, quote?, max_cost?, timeout=180}", (a, ct) => S.Uploads.UploadMeshAsync(a, ct));
        Op("uploadLook", "sojourn: {path, type?, scale?, max_size?, priority?, loop?} -> what an upload of the file would be (kind, size, a picture's size as uploaded, a sound's seconds, a model's parts and size, the fee where known), nothing sent", (a, ct) => S.Uploads.LookAsync(a, ct));
        Op("createWearable", "{type, name?, desc?, folder_id?, wear=true, replace=false, timeout=30} -> {item_id, worn}",
            async (a, ct) => await S.Wardrobe.CreateAsync(a, ct));
        Op("editWearable", "{item_id, textures? {slot: id} | slot + texture_id, color? [r,g,b], params? {name|id: weight}, name?, save=true}",
            async (a, ct) => await S.Wardrobe.EditAsync(a, ct));
        Op("wearableParams", "sojourn: {type} -> {params: [{id, name, label, min, max, default, ...}], slots}: what editWearable can set",
            a => S.Wardrobe.Params(a));
        Op("uvTemplate", "sojourn: {part: head|upper|lower|eyes|hair|skirt, size=1024} -> {part, size, png_base64}: the system avatar's UV layout for that texture, a painting guide (outlined triangles on transparent)",
            a =>
            {
                var part = a.Str("part") ?? "head";
                int size = Math.Clamp(a.Int("size", 1024), 64, 2048);
                return new JsonObject { ["part"] = part, ["size"] = size, ["png_base64"] = Convert.ToBase64String(Sojourn.Core.Appearance.UvTemplate.Png(part, size)) };
            });
        Op("saveOutfit", "sojourn: {name, folder_id?} -> {folder_id, items}: everything worn, as an outfit folder of links in My Outfits",
            async (a, ct) => await S.Wardrobe.SaveOutfitAsync(a, ct));
        Op("generateLook", "sojourn: {seed?} -> {seed, items}: a whole new generated look (shape, skin, hair, eyes, clothes), worn",
            async (a, ct) => await new Sojourn.Core.Appearance.LookGenerator(S).DressAsync(C, a.Str("seed") ?? Guid.NewGuid().ToString(), ct));
        // Tessel's viewer's window and stream controls. Sojourn's window is the person's own, so those are refused;
        // hideAvatars reaches the renderer.
        Op("spectator", "{on?} -> {on: false}: the window is the person's; spectator mode is refused", a =>
            a.Bool("on") == true ? throw new LeapError(PersonsWindow) : new JsonObject { ["on"] = false });
        Op("renderUI", "refused: " + PersonsWindow, _ => throw new LeapError(PersonsWindow));
        Op("streamUI", "refused: " + PersonsWindow, _ => throw new LeapError(PersonsWindow));
        Op("hideAvatars", "{mode? (off|list|all_except), ids?, except?} -> {mode, ids, except, hidden_now, shown_now}: avatars left out of "
            + "the person's view (and so out of the pictures the assistant sees); not one's own", HideAvatars);
        Op("getHidden", "-> {mode, ids, except, hidden_now, shown_now}", _ => Hidden(false));
    }

    static async Task<JsonNode?> Wrap(Task<JsonObject> t) => await t;

    const string PersonsWindow = "Sojourn's window is the person's own: what it shows is theirs to change (the panel hides with Tab, or X in the headset)";

    static UUID To(JsonObject a) => a.Uuid("to") is { } t && t != UUID.Zero ? t : throw new LeapError("bad recipient");

    async Task<JsonNode?> PayAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var to = To(a);
        var amount = a.Int("amount", 0);
        if (amount <= 0) throw new LeapError("amount must be positive");
        if (S.BalanceKnown && c.Self.Balance < amount) throw new LeapError($"can't afford L${amount} (balance L${c.Self.Balance})");
        var desc = a.Str("desc") ?? "";
        Action send;
        string target;
        if (S.Region?.PrimById(to) is { } obj)
        {
            var w = W;
            var root = S.Objects.RootOf(w, obj);
            if ((S.State.PrimFlagsOf(obj) & PrimFlags.Money) == 0 && (S.State.PrimFlagsOf(root) & PrimFlags.Money) == 0)
                throw new LeapError("that object doesn't accept payment");
            send = () => c.Self.GiveObjectMoney(to, amount, obj.Properties?.Name ?? desc);
            target = "object";
        }
        else
        {
            send = () => c.Self.GiveAvatarMoney(to, amount, desc);
            target = "avatar";
        }
        var me = c.Self.AgentID;
        var reply = await PaymentReplyAsync(c, send, ti => ti.SourceID == me && ti.Amount == amount, ct);
        if (reply is { Success: false }) throw new LeapError($"the grid refused the payment: {reply.Description}");
        S.State.SpentThisSession += amount;   // unconfirmed counts too: it may well have gone through
        var o = new JsonObject { ["target"] = target, ["amount"] = amount, ["spent_this_session"] = S.State.SpentThisSession };
        if (reply is null) { o["sent"] = true; o["confirmed"] = false; o["note"] = "the grid hasn't confirmed it yet; the balance shows whether it went through"; }
        else { o["ok"] = true; o["balance"] = reply.Balance; }
        return o;
    }

    async Task<JsonNode?> GiveAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var to = To(a);
        if (a.Uuid("folder_id") is { } fid)
        {
            if (S.Inventory.Node(fid) is not InventoryFolder f) throw new LeapError("no such folder");
            await c.Inventory.GiveFolderAsync(fid, f.Name, to, false, ct);
            return new JsonObject();
        }
        var iid = a.Uuid("item_id") ?? throw new LeapError("no such item");
        var item = await S.Inventory.ItemAsync(iid, ct) ?? throw new LeapError("no such item");
        if ((item.Permissions.OwnerMask & PermissionMask.Transfer) == 0 || S.Inventory.IsWorn(item))
            throw new LeapError("item can't be given (no transfer, or worn)");
        c.Inventory.GiveItem(item.UUID, item.Name, item.AssetType, to, false);
        // a no-copy item moves to them at once (OpenSim and SL alike); the local view forgets it, as the viewer does
        if ((item.Permissions.OwnerMask & PermissionMask.Copy) == 0) c.Inventory.Store?.RemoveNodeFor(item);
        return new JsonObject();
    }

    async Task<JsonNode?> AttachAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var iid = a.Uuid("item_id") ?? throw new LeapError("no such item");
        var item = await S.Inventory.ItemAsync(iid, ct) ?? throw new LeapError("no such item");
        var point = 0;
        if (a["point"] is { } pv)
        {
            var name = pv is JsonValue v && v.TryGetValue<string>(out var s) ? s : pv.ToJsonString();
            point = ObjectViews.AttachPointId(name) ?? throw new LeapError($"unknown attachment point {name}");
        }
        // an object in the outfit (its link in Current Outfit) that isn't on the avatar, as after logging in on Second
        // Life when one didn't arrive, is attached again, as the viewer's Wear does
        if (item.AssetType == AssetType.Object ? S.Inventory.IsAttached(item) : S.Inventory.IsWorn(item)) throw new LeapError("already worn");
        // only objects, clothing and body parts are worn: a notecard "attached" got an outfit link and nothing else
        // (2026-10-27), as the viewer would never do
        if (item.AssetType is not (AssetType.Object or AssetType.Bodypart or AssetType.Clothing))
            throw new LeapError($"{item.Name} is a {item.AssetType.ToString().ToLowerInvariant()}, not something to wear");
        if (item.AssetType is AssetType.Bodypart or AssetType.Clothing)
        {
            await S.Wardrobe.WearAsync(c, new[] { item }, a.Bool("replace", false), ct);
            return new JsonObject { ["point"] = 0 };
        }
        await S.Wardrobe.AttachAsync(c, item, (AttachmentPoint)point, a.Bool("replace", false), ct);
        return new JsonObject { ["point"] = point };
    }

    async Task<JsonNode?> DetachAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        if (a.Uuid("object_id") is { } oid && S.Region?.PrimById(oid) is { } p)
        {
            var root = S.Objects.RootOf(W, p);
            var itemId = AgentInventory.AttachItemId(root);
            if (itemId != UUID.Zero) await S.Wardrobe.DetachAsync(c, itemId, ct);
            c.Objects.DetachObjects(Sim, new List<uint> { root.LocalID });
            return new JsonObject();
        }
        if (a.Uuid("item_id") is { } iid)
        {
            // a wearable comes off the outfit (not body parts); an attachment detaches
            await S.Wardrobe.TakeOffAsync(c, new[] { iid }, ct);
            return new JsonObject();
        }
        throw new LeapError("need the worn item_id or object_id");
    }

    JsonNode? AttachPoints(JsonObject a)
    {
        var c = C;
        var w = W;
        var me = w.AvatarById(c.Self.AgentID);
        var byPoint = new Dictionary<int, JsonArray>();
        if (me is not null)
            foreach (var p in w.Prims.Values.Where(p => p.ParentID == me.LocalID))
            {
                var pt = (int)p.PrimData.AttachmentPoint;
                if (!byPoint.TryGetValue(pt, out var arr)) byPoint[pt] = arr = new JsonArray();
                arr.Add(new JsonObject
                {
                    ["object_id"] = p.ID.ToString(), ["item_id"] = AgentInventory.AttachItemId(p).ToString(),
                    ["name"] = p.Properties?.Name ?? "",
                });
            }
        var points = new JsonArray();
        for (var i = 1; i < ObjectViews.AttachPointNames.Length; i++)
            points.Add(new JsonObject
            {
                ["id"] = i, ["name"] = ObjectViews.AttachPointNames[i], ["hud"] = i is >= 31 and <= 38,
                ["objects"] = byPoint.TryGetValue(i, out var arr) ? arr : new JsonArray(),
            });
        return new JsonObject { ["points"] = points };
    }

    /// <summary>As the viewer's LLGroupActions::join: look up the group's fee, then JoinGroupCanAfford (a fee),
    /// JoinGroupNoCost (free) or JoinGroupCannotAfford (more than my balance, nothing to confirm).</summary>
    async Task<JsonNode?> JoinGroupAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var gid = a.Uuid("group_id") ?? throw new LeapError("need group_id");
        var profile = await S.GroupOps.ProfileAsync(new JsonObject { ["group_id"] = gid.ToString() }, ct);
        string name = (string?)profile["name"] ?? gid.ToString();
        int fee = (int?)profile["fee"] ?? 0;
        var subs = new JsonObject { ["COST"] = fee.ToString(), ["NAME"] = name };
        var payload = new JsonObject { ["group_id"] = gid.ToString() };
        if (fee > 0 && S.BalanceKnown && c.Self.Balance < fee)
        {
            S.Notify.Add("JoinGroupCannotAfford", "alertmodal", $"Joining this group costs L${fee}.\nYou do not have enough L$ to join this group.",
                Notifications.Ok, payload: payload, substitutions: subs);
            return new JsonObject { ["fee"] = fee, ["can_afford"] = false };
        }
        var buttons = new[] { new NotifyButton("OK_okcancelbuttons", "Join", 0, true), new NotifyButton("Cancel_okcancelbuttons", "Cancel", 1) };
        Task Respond(string button, JsonObject _)
        {
            if (button == "OK_okcancelbuttons") c.Groups.RequestJoinGroup(gid);
            return Task.CompletedTask;
        }
        var id = fee > 0
            ? S.Notify.Add("JoinGroupCanAfford", "alertmodal", $"Joining this group costs L${fee}.\nDo you wish to proceed?", buttons,
                payload: payload, substitutions: subs, respond: Respond)
            : S.Notify.Add("JoinGroupNoCost", "alertmodal", $"You are joining group <nolink>{name}</nolink>.\nDo you wish to proceed?", buttons,
                payload: payload, substitutions: subs, respond: Respond);
        return new JsonObject { ["fee"] = fee, ["can_afford"] = true, ["notification_id"] = id.ToString() };
    }

    JsonNode? LeaveGroup(JsonObject a)
    {
        var c = C;
        var gid = a.Uuid("group_id") ?? throw new LeapError("need group_id");
        var name = S.State.Groups.TryGetValue(gid, out var g) ? g.Name : gid.ToString();
        S.Notify.Add("GroupLeaveConfirmMember", "alertmodal", $"You are currently a member of the group <nolink>{name}</nolink>.\nLeave Group?",
            new[] { new NotifyButton("OK_okcancelbuttons", "OK", 0, true), new NotifyButton("Cancel_okcancelbuttons", "Cancel", 1) },
            payload: new JsonObject { ["group_id"] = gid.ToString() },
            substitutions: new JsonObject { ["GROUP"] = name },
            respond: (button, _) =>
            {
                if (button == "OK_okcancelbuttons") c.Groups.LeaveGroup(gid);
                return Task.CompletedTask;
            });
        return new JsonObject();
    }

    static readonly string[] CategoryNames =
        { "none", "linden", "adult", "arts", "store", "educational", "game", "gather", "newcomer", "park", "home", "shopping", "stage", "other", "rental" };

    async Task<JsonNode?> ParcelAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var sim = Sim;
        var region = new JsonObject
        {
            ["name"] = sim.Name, ["id"] = sim.RegionID.ToString(),
            ["maturity"] = sim.Access switch
            {
                SimAccess.PG => "General", SimAccess.Mature => "Moderate", SimAccess.Adult => "Adult",
                SimAccess.Down => "Offline", _ => "Unknown",
            },
            ["sandbox"] = (sim.Flags & RegionFlags.Sandbox) != 0,
        };
        var pos = c.Self.SimPosition;
        Parcel? parcel = null;
        var local = c.Parcels.GetParcelLocalID(sim, pos);
        if (local != 0 && sim.Parcels.TryGetValue(local, out var known)) parcel = known;
        if (parcel is null)
        {
            var tcs = new TaskCompletionSource<Parcel>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object? _, ParcelPropertiesEventArgs e)
            {
                if (e.Parcel is { } p && p.AABBMin.X <= pos.X && p.AABBMin.Y <= pos.Y && p.AABBMax.X >= pos.X && p.AABBMax.Y >= pos.Y)
                    tcs.TrySetResult(p);
            }
            c.Parcels.ParcelProperties += Handler;
            try
            {
                c.Parcels.RequestParcelProperties(sim, pos.Y + 1, pos.X + 1, pos.Y - 1, pos.X - 1, 0, false);
                parcel = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException) { }
            finally { c.Parcels.ParcelProperties -= Handler; }
        }
        if (parcel is null) return new JsonObject { ["region"] = region, ["error"] = "no parcel data yet" };
        var f = parcel.Flags;
        var rf = sim.Flags;
        var allowed = new JsonArray();
        if ((f & ParcelFlags.CreateObjects) != 0) allowed.Add("build");
        if ((rf & (RegionFlags.SkipScripts | RegionFlags.EstateSkipScripts)) == 0 && (f & ParcelFlags.AllowOtherScripts) != 0) allowed.Add("scripts");
        if ((rf & RegionFlags.NoFly) == 0 && (f & ParcelFlags.AllowFly) != 0) allowed.Add("fly");
        if ((rf & RegionFlags.RestrictPushObject) == 0 && (f & ParcelFlags.RestrictPushObject) == 0) allowed.Add("push");
        if ((rf & RegionFlags.AllowDamage) != 0 || (f & ParcelFlags.AllowDamage) != 0) allowed.Add("damage");
        if ((rf & RegionFlags.AllowVoice) != 0 && (f & ParcelFlags.AllowVoiceChat) != 0) allowed.Add("voice");
        if (parcel.SeeAVs) allowed.Add("see_avatars");
        var cat = (int)parcel.Category;
        return new JsonObject
        {
            ["region"] = region,
            ["parcel"] = new JsonObject
            {
                ["name"] = parcel.Name, ["desc"] = parcel.Desc, ["local_id"] = parcel.LocalID,
                ["owner_id"] = parcel.OwnerID.ToString(), ["group_id"] = parcel.GroupID.ToString(),
                ["group_owned"] = parcel.IsGroupOwned, ["area"] = parcel.Area,
                ["category"] = cat >= 0 && cat < CategoryNames.Length ? CategoryNames[cat] : cat.ToString(),
                ["music_url"] = parcel.MusicURL, ["media_url"] = parcel.Media.MediaURL ?? "",
                ["for_sale"] = (f & ParcelFlags.ForSale) != 0, ["sale_price"] = parcel.SalePrice,
                ["allowed"] = allowed,
            },
        };
    }

    JsonNode? People(JsonObject a)
    {
        var c = C;
        var w = W;
        var dist = a.Num("dist", 64);
        var me = c.Self.SimPosition;
        var friends = c.Friends.FriendList;
        var seen = new HashSet<UUID> { c.Self.AgentID };
        var list = new List<(double d, JsonObject o)>();
        void Add(UUID id, Vector3 pos)
        {
            if (!seen.Add(id)) return;
            var d = Vector3.Distance(pos, me);
            if (d > dist) return;
            var o = new JsonObject { ["id"] = id.ToString(), ["dist"] = Json.R(d, 2), ["pos"] = Json.Arr(pos), ["friend"] = friends.ContainsKey(id) };
            if (S.Names.Json(id) is { } n) foreach (var (k, v) in n) o[k] = v?.DeepClone();
            list.Add((d, o));
        }
        // in-view avatars first (every region this agent sees), then coarse map positions
        foreach (var rw in S.VisibleRegions())
            foreach (var av in rw.Avatars.Values) Add(av.ID, AvatarPos(rw, av) + S.FrameOffset(rw));
        foreach (var rw in S.VisibleRegions())
            foreach (var (id, pos) in rw.Coarse) Add(id, pos + S.FrameOffset(rw));
        return new JsonObject { ["people"] = new JsonArray(list.OrderBy(x => x.d).Select(x => (JsonNode)x.o).ToArray()) };
    }

    internal static Vector3 AvatarPos(World.RegionWorld w, Avatar av)
    {
        if (av.ParentID == 0) return av.Position;
        return w.Prims.TryGetValue(av.ParentID, out var seat) ? w.RegionPosition(seat) + av.Position * w.RegionRotation(seat) : av.Position;
    }

    JsonNode? Friends(JsonObject a)
    {
        var list = new JsonArray();
        foreach (var f in C.Friends.FriendList.Values)
        {
            var rightsTo = (f.CanSeeMeOnline ? 1 : 0) | (f.CanSeeMeOnMap ? 2 : 0) | (f.CanModifyMyObjects ? 4 : 0);
            var rightsFrom = (f.CanSeeThemOnline ? 1 : 0) | (f.CanSeeThemOnMap ? 2 : 0) | (f.CanModifyTheirObjects ? 4 : 0);
            if (!string.IsNullOrEmpty(f.Name)) S.Names.SetLegacy(f.UUID, f.Name);
            var o = new JsonObject { ["id"] = f.UUID.ToString(), ["online"] = f.IsOnline, ["rights_to"] = rightsTo, ["rights_from"] = rightsFrom };
            if (S.Names.Json(f.UUID) is { } n) foreach (var (k, v) in n) o[k] = v?.DeepClone();
            list.Add(o);
        }
        return new JsonObject { ["friends"] = list };
    }

    /// <summary>The avatar picker (AvatarPickerRequest): name words against every account the grid knows, online or
    /// not. Exact full-name matches come first. Names found go into the shared name cache.</summary>
    async Task<JsonNode?> FindAvatarAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var name = (a.Str("name") ?? "").Trim();
        if (name.Length < 2) throw new LeapError("need a name (at least 2 letters; Second Life wants 3)");
        var query = UUID.Random();
        var tcs = new TaskCompletionSource<Dictionary<UUID, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReply(object? _, AvatarPickerReplyEventArgs e) { if (e.QueryID == query) tcs.TrySetResult(e.Avatars); }
        c.Avatars.AvatarPickerReply += OnReply;
        Dictionary<UUID, string> found;
        try
        {
            c.Avatars.RequestAvatarNameSearch(name, query);
            try { found = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(a.Num("timeout", 8)), ct); }
            catch (TimeoutException) { throw new LeapError("the grid didn't answer the name search"); }
        }
        finally { c.Avatars.AvatarPickerReply -= OnReply; }
        found.Remove(UUID.Zero);
        foreach (var (id, full) in found) S.Names.SetLegacy(id, full);
        await S.Names.ResolveAsync(c, found.Keys, TimeSpan.FromSeconds(3), ct);
        var want = name.ToLowerInvariant();
        static string Norm(string n) => n.EndsWith(" Resident", StringComparison.OrdinalIgnoreCase) ? n[..^9] : n;
        var people = new JsonArray();
        foreach (var (id, full) in found
                     .OrderByDescending(kv => Norm(kv.Value).Equals(want, StringComparison.OrdinalIgnoreCase) || kv.Value.Equals(want, StringComparison.OrdinalIgnoreCase))
                     .ThenBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                     .Take(Math.Max(1, a.Int("limit", 20))))
        {
            var o = S.Names.Json(id) ?? new JsonObject();
            o["id"] = id.ToString();
            o["legacy_name"] = full;
            o["exact"] = Norm(full).Equals(name, StringComparison.OrdinalIgnoreCase) || full.Equals(name, StringComparison.OrdinalIgnoreCase);
            people.Add(o);
        }
        return new JsonObject { ["people"] = people };
    }

    /// <summary>The world map's region blocks around me (MapBlockRequest), as the map floater shows them.</summary>
    async Task<JsonNode?> RegionsAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var sim = Sim;
        Utils.LongToUInts(sim.Handle, out var gx, out var gy);
        int cx = (int)(gx / 256), cy = (int)(gy / 256), r = Math.Clamp(a.Int("range", 8), 1, 64);
        var found = new System.Collections.Concurrent.ConcurrentDictionary<ulong, GridRegion>();
        void On(object? _, GridRegionEventArgs e) { if (e.Region.RegionHandle != 0 && !string.IsNullOrEmpty(e.Region.Name)) found[e.Region.RegionHandle] = e.Region; }
        c.Grid.GridRegion += On;
        try
        {
            c.Grid.RequestMapBlocks(GridLayerType.Objects, (ushort)Math.Max(0, cx - r), (ushort)Math.Max(0, cy - r), (ushort)(cx + r), (ushort)(cy + r), false);
            await Task.Delay(TimeSpan.FromSeconds(2.5), ct);
        }
        finally { c.Grid.GridRegion -= On; }
        var arr = new JsonArray();
        foreach (var g in found.Values.OrderBy(g => g.Y).ThenBy(g => g.X))
            arr.Add(new JsonObject { ["name"] = g.Name, ["x"] = g.X, ["y"] = g.Y, ["agents"] = g.Agents, ["here"] = g.RegionHandle == sim.Handle });
        return new JsonObject { ["regions"] = arr, ["me"] = new JsonArray(cx, cy) };
    }

    async Task<JsonNode?> MuteAsync(JsonObject a, CancellationToken ct)
    {
        var c = C;
        var id = a.Uuid("id") ?? UUID.Zero;
        var name = a.Str("name") ?? "";
        var type = a.Str("type");
        if (id == UUID.Zero)
        {
            if (name.Length == 0) throw new LeapError("need id or name");
            type ??= "name";
        }
        else if (type is null)
        {
            if (S.Region?.PrimById(id) is { } prim) { type = "object"; if (name.Length == 0) name = prim.Properties?.Name ?? "Object"; }
            else type = "resident";
        }
        if (type == "resident" && name.Length == 0)
        {
            await S.Names.ResolveAsync(c, new[] { id }, TimeSpan.FromSeconds(4), ct);
            name = S.Names.Legacy(id) ?? throw new LeapError($"no resident has id {id}");
        }
        if (id == c.Self.AgentID) throw new LeapError("that's me");
        var e = S.Mutes.Add(id, name, type, c);
        return new JsonObject { ["muted"] = e.Name, ["id"] = e.Id.ToString(), ["type"] = e.Type };
    }

    async Task<JsonNode?> NamesAsync(JsonObject a, CancellationToken ct)
    {
        var ids = IdsOf(a);
        await S.Names.ResolveAsync(C, ids, TimeSpan.FromSeconds(a.Num("timeout", 5)), ct);
        var names = new JsonObject();
        foreach (var id in ids) if (S.Names.Json(id) is { } n) names[id.ToString()] = n;
        return new JsonObject { ["names"] = names };
    }

    JsonNode? HideAvatars(JsonObject a)
    {
        // pictures here are the person's own view, so they always leave out whom the view does
        if (a.Bool("in_snapshots") == false) throw new LeapError("pictures here are the person's own view, so they always match it; in_snapshots: false can't apply");
        if (a.Str("mode") is { } mode)
        {
            if (mode is not ("off" or "list" or "all_except")) throw new LeapError($"bad mode {mode}");
            S.State.Settings["_hide_mode"] = mode;
        }
        if (a.Has("ids")) S.State.Settings["_hide_ids"] = Json.Ids(a.UuidList("ids"));
        if (a.Has("except")) S.State.Settings["_hide_except"] = Json.Ids(a.UuidList("except"));
        return Hidden(true);
    }

    JsonObject Hidden(bool withGrew)
    {
        var st = S.State.Settings;
        var mode = st.TryGetValue("_hide_mode", out var m) ? m!.GetValue<string>() : "off";
        var ids = st.TryGetValue("_hide_ids", out var i) ? (JsonArray)i!.DeepClone() : new JsonArray();
        var except = st.TryGetValue("_hide_except", out var e) ? (JsonArray)e!.DeepClone() : new JsonArray();
        var hidden = S.HiddenAvatars().ToHashSet();
        var all = S.VisibleRegions().SelectMany(w => w.Avatars.Values).Select(a => a.ID).Where(id => id != S.AgentId).Distinct().ToList();
        var o = new JsonObject
        {
            ["mode"] = mode, ["ids"] = ids, ["except"] = except,
            ["hidden_now"] = Json.Ids(all.Where(hidden.Contains)), ["shown_now"] = Json.Ids(all.Where(x => !hidden.Contains(x))),
        };
        if (withGrew) o["grew"] = hidden.Count > 0;
        return o;
    }
}
