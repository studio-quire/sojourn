using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Rlv;

namespace Sojourn.Core.Agents;

/// <summary>
/// RLV in the session (docs/rlv.md): commands from the person's own objects by llOwnerSay, what the restrictions do to
/// chat, IMs and offers coming in, the watch that follows sitting, wearing and objects going away, and the host the engine
/// acts through. Off by default; the <c>rlv</c> setting takes effect at the next login, as Firestorm's RLVa does.
/// </summary>
public sealed partial class AgentSession
{
    RlvEngine? _rlv;
    readonly object _rlvLock = new();

    public RlvEngine Rlv
    {
        get
        {
            if (_rlv is { } r) return r;
            lock (_rlvLock)
            {
                if (_rlv != null) return _rlv;
                _rlvHostObj = new RlvHost(this);
                _rlv = new RlvEngine(_rlvHostObj);
                _rlv.Ran += (cmd, ret) =>
                {
                    if (ret >= RlvRet.Failed) Log.LogInformation("RLV: {Cmd} from {Obj} {Ret}", cmd, cmd.Object, ret);
                    else Log.LogDebug("RLV: {Cmd} from {Obj} {Ret}", cmd, cmd.Object, ret);
                };
                _rlv.Dropped += (id, name, why) => Log.LogInformation("RLV: {Name} ({Id}) let go of everything ({Why})", name, id, why);
                return _rlv;
            }
        }
    }

    bool _rlvWanted;

    /// <summary>The setting: RLV for the next login (the person's switch). Set in a login's first seconds (the app's
    /// settings read while the session was already logging in), it counts for that login.</summary>
    public bool RlvWanted
    {
        get => _rlvWanted;
        set
        {
            _rlvWanted = value;
            if (value && !RlvActive && Client is { } c && Online && ConnectedAt is { } at && Json.UnixNow() - at < 20) RlvLoggedIn(c);
        }
    }

    /// <summary>RLV in force for this login (what <see cref="RlvWanted"/> was when it began).</summary>
    public bool RlvActive { get; private set; }

    CancellationTokenSource? _rlvWatch;
    DateTime _wayOutAt;

    void RlvLoggedIn(GridClient c)
    {
        Rlv.ClearAll();
        RlvActive = RlvWanted;
        _rlvHost().Reset();
        if (!RlvActive) return;
        Log.LogInformation("RLV on for this login (RestrainedLove API: {Version})", RlvEngine.Version);
        Rlv.CameraFovNow ??= () => Camera.Fov;
        _rlvWatch?.Cancel();
        _rlvWatch = new CancellationTokenSource();
        var ct = _rlvWatch.Token;
        _ = Task.Run(() => RlvWatchAsync(c, ct));
    }

    void RlvLoggedOut()
    {
        _rlvWatch?.Cancel();
        _rlvWatch = null;
        if (_rlv != null) _rlv.ClearAll();
    }

    RlvHost? _rlvHostObj;

    RlvHost _rlvHost() { _ = Rlv; return _rlvHostObj!; }

    /// <summary>The way out began: what it does next (standing, going home) is the person's, whatever RLV says, and for
    /// half a minute no script moves them back (docs/rlv.md > The way out).</summary>
    public void RlvWayOut() => _wayOutAt = DateTime.UtcNow;

    /// <summary>Within the way out's half minute.</summary>
    public bool RlvWayOutRecent => DateTime.UtcNow - _wayOutAt < TimeSpan.FromSeconds(30);

    /// <summary>A refusal the person should hear about (their own press or the assistant's op): a quiet notice, at most
    /// once in 3 s for the same words. Returns the reason, for an op's error.</summary>
    public string? RlvRefused(string? why)
    {
        if (why == null) return null;
        lock (_rlvLock)
        {
            if (why == _lastRefusal && DateTime.UtcNow - _lastRefusalAt < TimeSpan.FromSeconds(3)) return why;
            _lastRefusal = why;
            _lastRefusalAt = DateTime.UtcNow;
        }
        Log.LogInformation("{Why}", why);
        Notify.Add("RlvBlocked", "notifytip", why, Notifications.None, substitutions: new JsonObject { ["MESSAGE"] = why });
        return why;
    }

    string? _lastRefusal;
    DateTime _lastRefusalAt;

    // ---------- chat in ----------

    /// <summary>An owner-say starting with '@' from one of the person's objects: RLV commands, run and kept out of chat
    /// (llviewermessage.cpp). True when taken.</summary>
    bool RlvTakeCommand(GridClient c, ChatEventArgs e)
    {
        if (!RlvActive || e.Type != ChatType.OwnerSay || e.SourceType != ChatSourceType.Object) return false;
        var msg = e.Message ?? "";
        if (msg.Length <= 3 || msg[0] != '@') return false;
        var results = Rlv.Process(e.SourceID, e.FromName ?? "", msg);
        Events.Log("SojournRlv", new JsonObject
        {
            ["event"] = "commands", ["object_id"] = e.SourceID.ToString(), ["object"] = e.FromName,
            ["commands"] = new JsonArray(results.Select(r => (JsonNode)new JsonObject { ["command"] = r.Command, ["result"] = r.Ret.ToString().ToLowerInvariant() }).ToArray()),
        });
        return true;
    }

    /// <summary>Nearby chat from someone (or something) other than the person and their own attachments, under
    /// @recvchat/@recvemote and @shownames: the words left (null: none) and the name to show (rlvanonym: hidden).</summary>
    public (string? Message, string From, bool Anonym) RlvHeard(GridClient c, ChatEventArgs e, string message, string from)
    {
        if (!RlvActive || !Rlv.Any) return (message, from, false);
        bool ownAttachment = e.SourceType == ChatSourceType.Object && e.OwnerID == c.Self.AgentID && Region?.PrimById(e.SourceID) is { } p
            && Objects.IsAttachment(Region!, p, out _, out _);
        if ((e.SourceType == ChatSourceType.Agent && e.SourceID != c.Self.AgentID)
            || (e.SourceType == ChatSourceType.Object && !ownAttachment && e.Type is not (ChatType.OwnerSay or ChatType.RegionSayTo)))
        {
            var heard = Rlv.Heard(e.SourceID, message);
            if (heard == null) return (null, from, false);
            message = heard;
        }
        bool anon = false;
        if (Rlv.NamesHidden)
        {
            if (e.SourceType == ChatSourceType.Agent && !Rlv.CanShowName(e.SourceID)) { from = RlvEngine.Anonym(Names.Legacy(e.SourceID) ?? e.FromName ?? from); anon = true; }
            else if (e.SourceType != ChatSourceType.Agent) from = RlvFilterText(from);
            message = RlvFilterText(message);
        }
        else if (Rlv.LocationHidden) message = RlvFilterText(message);
        return (message, from, anon);
    }

    // ---------- names and places in text ----------

    /// <summary>Text with the names of people nearby (when @shownames holds) and of the regions and parcel here (when
    /// @showloc holds) replaced, as RLVa's filterNames and filterLocation do for what the person reads.</summary>
    public string RlvFilterText(string text)
    {
        if (!RlvActive || text.Length == 0) return text;
        var (re, map) = RlvFilter();
        if (re != null) text = re.Replace(text, m => map.TryGetValue(m.Value, out var s) ? s : m.Value);
        if (Rlv.LocationHidden && ParcelNameHere?.Invoke() is { Length: > 1 } pn) text = text.Replace(pn, "(Hidden parcel)", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    (Regex? Re, Dictionary<string, string> Map) _rlvFilter = (null, new());
    string _rlvFilterKey = "";
    DateTime _rlvFilterAt;

    /// <summary>One pattern for every name and region hidden now (longest first), and what each becomes; rebuilt when
    /// they change, looked at at most once a second.</summary>
    (Regex? Re, Dictionary<string, string> Map) RlvFilter()
    {
        lock (_rlvLock)
        {
            if (DateTime.UtcNow - _rlvFilterAt < TimeSpan.FromSeconds(1)) return _rlvFilter;
            _rlvFilterAt = DateTime.UtcNow;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Rlv.NamesHidden)
                foreach (var (id, names) in NearbyNames())
                {
                    if (Rlv.CanShowName(id)) continue;
                    var anon = RlvEngine.Anonym(names[0]);
                    foreach (var n in names.Where(n => n.Length >= 2)) map.TryAdd(n, anon);
                }
            if (Rlv.LocationHidden)
                foreach (var w in VisibleRegions())
                    if (w.Name is { Length: > 1 } rn) map.TryAdd(rn, "(Hidden region)");
            var key = string.Join("\n", map.Select(kv => kv.Key + "\t" + kv.Value).OrderBy(s => s, StringComparer.Ordinal));
            if (key == _rlvFilterKey) return _rlvFilter;
            _rlvFilterKey = key;
            var re = map.Count == 0 ? null : new Regex(@"\b(?:" + string.Join("|", map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) + @")\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return _rlvFilter = (re, map);
        }
    }

    /// <summary>The parcel's name here, for @showloc's filter (the host's land view sets it).</summary>
    public Func<string?>? ParcelNameHere { get; set; }

    List<(UUID Id, List<string> Names)> NearbyNames()
    {
        var list = new List<(UUID, List<string>)>();
        if (Client is not { } c) return list;
        foreach (var w in VisibleRegions())
            foreach (var av in w.Avatars.Values)
            {
                if (av.ID == c.Self.AgentID) continue;
                var names = new List<string>();
                if (Names.Legacy(av.ID) is { Length: > 0 } l) { names.Add(l); if (l.EndsWith(" Resident")) names.Add(l[..^9]); }
                else if (!string.IsNullOrEmpty(av.Name)) names.Add(av.Name);
                if (Names.DisplayName(av.ID) is { Length: > 0 } d && !names.Contains(d)) names.Add(d);
                if (names.Count > 0) list.Add((av.ID, names));
            }
        return list;
    }

    /// <summary>Every string in an op's result through <see cref="RlvFilterText"/> while names or the location are
    /// hidden: what the assistant reads is what the person may see.</summary>
    public JsonNode? RlvFilterResult(JsonNode? r)
    {
        if (!RlvActive || r == null || !(Rlv.NamesHidden || Rlv.LocationHidden)) return r;
        return Walk(r.DeepClone());
        JsonNode? Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var k in o.Select(kv => kv.Key).ToList()) o[k] = Walk(o[k]);
                    return o;
                case JsonArray a:
                    for (int i = 0; i < a.Count; i++) a[i] = Walk(a[i]);
                    return a;
                case JsonValue v when v.TryGetValue<string>(out var s):
                    var f = RlvFilterText(s);
                    return ReferenceEquals(f, s) || f == s ? v : JsonValue.Create(f);
                default:
                    return n;
            }
        }
    }

    // ---------- IMs and offers in ----------

    Vector3d? GlobalOf(UUID avatar)
    {
        foreach (var w in VisibleRegions())
            if (w.AvatarById(avatar) is { } av)
            {
                Utils.LongToUInts(w.Handle, out var x, out var y);
                return new Vector3d(x + (double)av.Position.X, y + (double)av.Position.Y, av.Position.Z);
            }
        return null;
    }

    /// <summary>An IM coming in: under @recvim it's shown as blocked and the sender hears so (llimprocessing.cpp).
    /// Returns the message to keep.</summary>
    string RlvImIn(GridClient c, InstantMessage im)
    {
        if (!RlvActive || im.Offline == InstantMessageOnline.Offline || Rlv.CanReceiveIm(im.FromAgentID, GlobalOf)) return im.Message;
        c.Self.InstantMessage(c.Self.Name, im.FromAgentID, RlvEngine.BlockedRecvImRemote, im.IMSessionID,
            InstantMessageDialog.BusyAutoResponse, InstantMessageOnline.Online, c.Self.SimPosition, UUID.Zero, Array.Empty<byte>());
        return RlvEngine.BlockedRecvIm;
    }

    /// <summary>A teleport offer: refused with the viewer's busy line under @tplure; taken at once under @accepttp.
    /// True when RLV answered it.</summary>
    bool RlvLure(GridClient c, InstantMessage im)
    {
        if (!RlvActive) return false;
        if (Rlv.AutoAcceptTeleport(im.FromAgentID) && !WayOutHolds("an offered teleport taken at once (@accepttp)"))
        {
            Log.LogInformation("RLV: teleport offer from {From} accepted (@accepttp)", im.FromAgentName);
            c.Self.TeleportLureRespond(im.FromAgentID, im.IMSessionID, true);
            return true;
        }
        if (Rlv.CanAcceptTeleportOffer(im.FromAgentID) is { } why)
        {
            c.Self.InstantMessage(c.Self.Name, im.FromAgentID, RlvEngine.BlockedTpRemote, im.IMSessionID,
                InstantMessageDialog.BusyAutoResponse, InstantMessageOnline.Online, c.Self.SimPosition, UUID.Zero, Array.Empty<byte>());
            RlvRefused(why);
            return true;
        }
        return false;
    }

    /// <summary>A teleport request (someone asking to come): @tprequest refuses it, @accepttprequest sends the lure at once.</summary>
    bool RlvTeleportRequest(GridClient c, InstantMessage im)
    {
        if (!RlvActive) return false;
        if (Rlv.AutoAcceptTeleportRequest(im.FromAgentID) && !WayOutHolds("a teleport request answered at once (@accepttprequest)"))
        {
            c.Self.SendTeleportLure(im.FromAgentID, "Join me");
            return true;
        }
        if (Rlv.CanAcceptTeleportRequest(im.FromAgentID) is { } why)
        {
            c.Self.InstantMessage(c.Self.Name, im.FromAgentID, RlvEngine.BlockedTpRemote, im.IMSessionID,
                InstantMessageDialog.BusyAutoResponse, InstantMessageOnline.Online, c.Self.SimPosition, UUID.Zero, Array.Empty<byte>());
            RlvRefused(why);
            return true;
        }
        return false;
    }

    /// <summary>A script's permission request under RLV: attach and teleport taken out while @addattach (all points) or
    /// @tploc hold; everything granted at once under @acceptpermission (RlvUtil::filterScriptQuestions). Returns the
    /// questions left, and whether it's answered already.</summary>
    public (ScriptPermission Asked, bool Granted) RlvQuestion(GridClient c, Simulator sim, UUID item, UUID task, ScriptPermission asked, string objectName)
    {
        if (!RlvActive) return (asked, false);
        if ((asked & ScriptPermission.Attach) != 0 && Rlv.AttachLockedAll)
        {
            asked &= ~ScriptPermission.Attach;
            RlvRefused($"Attempt to attach '{objectName}' was denied due to RLV restrictions");
        }
        if ((asked & ScriptPermission.Teleport) != 0 && Rlv.TeleportLocked)
        {
            asked &= ~ScriptPermission.Teleport;
            RlvRefused($"'{objectName}' was denied permission to teleport you due to RLV restrictions");
        }
        // @acceptpermission answers yes by itself only when all that's asked is taking the controls (and attaching, for
        // an object of the person's own that isn't worn), as RLVa; anything else (debit, teleport...) is asked as usual
        if (Rlv.AutoAcceptPermissions && asked != ScriptPermission.None)
        {
            var auto = ScriptPermission.TakeControls;
            if (_rlvHost().Object(task) is { } o && !o.Attachment && Region?.PrimById(task) is { } p && (State.PrimFlagsOf(p) & PrimFlags.ObjectYouOwner) != 0)
                auto |= ScriptPermission.Attach;
            if ((asked & ~auto) == 0)
            {
                c.Self.ScriptQuestionReply(sim, item, task, asked);
                return (asked, true);
            }
        }
        return (asked, false);
    }

    // ---------- the watch ----------

    /// <summary>Once a second: sitting and standing (@standtp, @notify), what's worn (@notify; an attachment that came
    /// off lets go of its restrictions), flying under @fly; every 30 s the garbage collector; the #RLV folder fetched.</summary>
    async Task RlvWatchAsync(GridClient c, CancellationToken ct)
    {
        var host = _rlvHost();
        uint sat = c.Self.SittingOn;
        UUID seat = host.SeatId;
        var standing = c.Self.GlobalPosition;
        Dictionary<UUID, RlvWorn>? before = null;
        // an attachment counts as off only once it's been missing three ticks running, in a region the avatar has been
        // in for a while: across a teleport or crossing, everything worn is gone from the world for a few seconds
        var missing = new Dictionary<UUID, int>();
        ulong region = c.Network.CurrentSim?.Handle ?? 0;
        var regionSince = DateTime.UtcNow;
        int tick = 0;
        while (!ct.IsCancellationRequested && Client == c)
        {
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            try
            {
                tick++;
                if (tick % 20 == 1) _ = host.FetchSharedAsync(ct);
                if (tick % 3 == 1) await host.RefreshWornAsync(c, ct);
                if ((c.Network.CurrentSim?.Handle ?? 0) != region)
                {
                    region = c.Network.CurrentSim?.Handle ?? 0;
                    regionSince = DateTime.UtcNow;
                    // (what was worn before stays the baseline: anything still missing once the region has settled came off)
                    missing.Clear();
                }
                bool settled = DateTime.UtcNow - regionSince > TimeSpan.FromSeconds(15) && Region?.AvatarById(c.Self.AgentID) != null;
                // sitting
                var now = c.Self.SittingOn;
                if (now != sat)
                {
                    // (a teleport stands the avatar too: @standtp's way back is for standing up, not for arriving)
                    if (sat != 0 || seat != UUID.Zero) Rlv.Stood(seat, RlvWayOutRecent || DateTime.UtcNow - regionSince < TimeSpan.FromSeconds(10));
                    sat = now;
                    seat = host.SeatId;
                    if (now != 0) Rlv.Sat(seat, standing);
                }
                if (now == 0) standing = c.Self.GlobalPosition;
                // flying under @fly
                if (c.Self.Movement.Fly && Rlv.CanFly() != null) { c.Self.Movement.Fly = false; c.Self.Movement.SendUpdate(true); }
                // what's worn
                if (!settled) continue;
                var worn = host.Worn().GroupBy(w => w.Point != 0 ? w.Object : w.Item).ToDictionary(g => g.Key, g => g.First());
                if (before != null)
                {
                    foreach (var (k, w) in worn)
                        if (!before.ContainsKey(k) && !missing.Remove(k)) Rlv.WornChanged(w, true);
                    foreach (var (k, w) in before)
                    {
                        if (worn.ContainsKey(k)) { missing.Remove(k); continue; }
                        int n = missing[k] = missing.GetValueOrDefault(k) + 1;
                        if (n < 3) { worn[k] = w; continue; }   // kept as worn until it's surely gone
                        missing.Remove(k);
                        Rlv.WornChanged(w, false);
                        if (w.Point != 0) Rlv.Detached(w.Object);
                    }
                }
                before = worn;
                if (tick % 30 == 0) Rlv.Collect(id => host.Where(id));
            }
            catch (Exception e) { Log.LogDebug(e, "RLV watch"); }
        }
    }

    /// <summary>The engine's way into the world (IRlvHost).</summary>
    sealed class RlvHost(AgentSession s) : IRlvHost
    {
        GridClient? C => s.Client;
        volatile List<Appearance.WornItem> _wearables = new();
        RlvFolder? _shared;
        DateTime _sharedAt;

        public void Reset() { _wearables = new(); _shared = null; }

        public UUID Me => C?.Self.AgentID ?? UUID.Zero;

        public void Reply(int channel, string text)
        {
            if (C is not { } c) return;
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            if (bytes.Length > 1023) text = System.Text.Encoding.UTF8.GetString(bytes, 0, 1023).TrimEnd('�');
            c.Self.Chat(text, channel, ChatType.Shout);
        }

        public void Tell(string text) => s.RlvRefused(text);

        public bool Sitting => C?.Self.SittingOn is > 0;

        public UUID SeatId
        {
            get
            {
                if (C is not { } c || c.Self.SittingOn == 0 || s.Region is not { } w) return UUID.Zero;
                return w.Prims.TryGetValue(c.Self.SittingOn, out var p) ? s.Objects.RootOf(w, p).ID : UUID.Zero;
            }
        }

        public Vector3d Position => C?.Self.GlobalPosition ?? Vector3d.Zero;

        public (Vector3d Position, UUID Root, bool Attachment, bool Hud, UUID Wearer)? Object(UUID id)
        {
            if (s.Objects.FindAnywhere(id) is not var (w, p)) return null;
            bool att = s.Objects.IsAttachment(w, p, out var wearer, out var root);
            Utils.LongToUInts(w.Handle, out var x, out var y);
            var rp = w.RegionPosition(root);
            int point = (int)root.PrimData.AttachmentPoint;
            return (new Vector3d(x + (double)rp.X, y + (double)rp.Y, rp.Z), root.ID, att, att && RlvNames.IsHud(point), wearer?.ID ?? UUID.Zero);
        }

        /// <summary>For the garbage collector: whether it's in the world, its root and attachment point.</summary>
        public (UUID Root, int Point)? Where(UUID id)
        {
            if (s.Objects.FindAnywhere(id) is not var (w, p)) return null;
            bool att = s.Objects.IsAttachment(w, p, out var wearer, out var root);
            return (root.ID, att && wearer?.ID == Me ? (int)root.PrimData.AttachmentPoint : 0);
        }

        public void Sit(UUID target)
        {
            if (s.WayOutHolds("a script's sit")) return;
            if (s.Objects.FindAnywhere(target) is var (w, _)) s.SendSitUnchecked(w.Handle, target, Vector3.Zero);
        }

        public void SitGround()
        {
            if (s.WayOutHolds("a script's sit")) return;
            C?.Self.SitOnGround();
        }

        public void Stand() => C?.Self.Stand();

        public void Teleport(Vector3d global, Vector3? lookAt)
        {
            if (C is not { } c || s.WayOutHolds("a script's teleport")) return;
            ulong handle = Utils.UIntsToLong((uint)(Math.Floor(global.X / 256) * 256), (uint)(Math.Floor(global.Y / 256) * 256));
            var local = new Vector3((float)(global.X % 256), (float)(global.Y % 256), (float)global.Z);
            var look = lookAt ?? new Vector3(1, 0, 0);
            s.Log.LogInformation("RLV: teleport to {Pos} asked by a script", global);
            c.Self.RequestTeleport(handle, local, local + look);
        }

        public void TeleportRegion(string region, Vector3 local, Vector3? lookAt)
        {
            if (C is not { } c || s.WayOutHolds("a script's teleport")) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var g = await c.Grid.GetGridRegionAsync(region, GridLayerType.Objects, cts.Token);
                    if (g is not { } r) { s.Log.LogInformation("RLV: @tpto: no region {Region}", region); return; }
                    var look = lookAt ?? new Vector3(1, 0, 0);
                    c.Self.RequestTeleport(r.RegionHandle, local, local + look);
                }
                catch (Exception e) { s.Log.LogInformation(e, "RLV: @tpto {Region}", region); }
            });
        }

        public bool Flying => C?.Self.Movement.Fly ?? false;

        public void Fly(bool on)
        {
            if (C is not { } c) return;
            c.Self.Movement.Fly = on;
            c.Self.Movement.SendUpdate(true);
        }

        public void Turn(float radians)
        {
            if (C is not { } c) return;
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians);
            c.Self.Movement.BodyRotation = c.Self.Movement.HeadRotation = q;
            c.Self.Movement.SendUpdate(true);
            s.TurnedByScript?.Invoke(radians);
        }

        public float HoverHeight => (float)s.Presence.HoverHeight;

        public void SetHover(float metres) => _ = s.Presence.SetHoverHeightAsync(metres, CancellationToken.None);

        public string ActiveGroupName =>
            C is { } c && c.Self.ActiveGroup != UUID.Zero && s.State.Groups.TryGetValue(c.Self.ActiveGroup, out var g) ? g.Name : "";

        public IReadOnlyList<(UUID Id, string Name)> Groups => s.State.Groups.Values.Select(g => (g.ID, g.Name)).ToList();

        public void SetGroup(UUID group, string? role) => C?.Groups.ActivateGroup(group);

        // ---------- inventory ----------

        UUID SharedRootId()
        {
            if (C?.Inventory.Store is not { } store) return UUID.Zero;
            var top = store.GetContents(store.RootFolder.UUID).OfType<InventoryFolder>().Where(f => f.Name == "#RLV").ToList();
            // several: the first with folders in it (RlvInventory::getSharedRootID)
            return (top.FirstOrDefault(f => store.GetContents(f.UUID).OfType<InventoryFolder>().Any()) ?? top.LastOrDefault())?.UUID ?? UUID.Zero;
        }

        public async Task FetchSharedAsync(CancellationToken ct)
        {
            try
            {
                if (s.Inventory.Loading is { } l) await l.WaitAsync(TimeSpan.FromSeconds(60), ct);
                var id = SharedRootId();
                if (id != UUID.Zero) await s.Inventory.LoadSubtreeAsync(id, ct);
                _shared = null;
            }
            catch (Exception e) when (e is not OperationCanceledException) { s.Log.LogDebug(e, "RLV: #RLV fetch"); }
            catch (OperationCanceledException) { }
        }

        public RlvFolder? SharedRoot()
        {
            if (_shared != null && DateTime.UtcNow - _sharedAt < TimeSpan.FromSeconds(2)) return _shared;
            if (C?.Inventory.Store is not { } store) return null;
            var id = SharedRootId();
            if (id == UUID.Zero || store[id] is not InventoryFolder rootFolder) return _shared = null;
            RlvFolder Build(InventoryFolder f, RlvFolder? parent, int depth)
            {
                var rf = new RlvFolder { Id = f.UUID, Name = f.Name, Parent = parent };
                if (depth > 32) return rf;
                foreach (var n in store.GetContents(f.UUID))
                {
                    if (n is InventoryFolder sub) rf.Folders.Add(Build(sub, rf, depth + 1));
                    else if (n is InventoryItem it) rf.Items.Add(Item(store, it, rf));
                }
                return rf;
            }
            _shared = Build(rootFolder, null, 0);
            _sharedAt = DateTime.UtcNow;
            return _shared;
        }

        static RlvItem Item(Inventory store, InventoryItem it, RlvFolder folder)
        {
            bool link = it.AssetType is AssetType.Link or AssetType.LinkFolder;
            var target = it;
            if (it.AssetType == AssetType.Link && store.TryGetValue<InventoryItem>(it.AssetUUID, out var t) && t != null) target = t;
            var kind = it.AssetType == AssetType.LinkFolder ? RlvItemKind.FolderLink : target.AssetType switch
            {
                AssetType.Object => RlvItemKind.Object,
                AssetType.Clothing => RlvItemKind.Clothing,
                AssetType.Bodypart => RlvItemKind.BodyPart,
                AssetType.Gesture => RlvItemKind.Gesture,
                _ => RlvItemKind.Other,
            };
            return new RlvItem
            {
                Id = it.UUID, Target = it.AssetType == AssetType.Link ? it.AssetUUID : it.UUID, Name = it.Name, Kind = kind,
                WearableType = kind is RlvItemKind.Clothing or RlvItemKind.BodyPart ? (int)(target.Flags & 0xFF) : -1,
                Modify = (target.Permissions.OwnerMask & PermissionMask.Modify) != 0, IsLink = link,
                LinkedFolder = it.AssetType == AssetType.LinkFolder ? it.AssetUUID : UUID.Zero, Folder = folder,
            };
        }

        public UUID ParentOf(UUID folder) => C?.Inventory.Store is { } st && st.Contains(folder) ? st[folder].ParentUUID : UUID.Zero;

        public UUID InventoryRoot => C?.Inventory.Store?.RootFolder.UUID ?? UUID.Zero;

        public UUID FolderOf(UUID item) => C?.Inventory.Store is { } st && st.Contains(item) ? st[item].ParentUUID : UUID.Zero;

        public async Task RefreshWornAsync(GridClient c, CancellationToken ct)
        {
            try { _wearables = (await s.Look.ReadWornAsync(c, ct)).Where(w => w.IsWearable).ToList(); }
            catch (Exception e) when (e is not OperationCanceledException) { s.Log.LogDebug(e, "RLV: worn"); }
        }

        public IReadOnlyList<RlvWorn> Worn()
        {
            var list = new List<RlvWorn>();
            if (C is { } c && s.Region is { } w && w.AvatarById(c.Self.AgentID) is { } me)
                foreach (var p in w.Prims.Values)
                    if (p.ParentID == me.LocalID)
                        list.Add(new RlvWorn(AgentInventory.AttachItemId(p), p.ID, (int)p.PrimData.AttachmentPoint, -1, p.Properties?.Name ?? ""));
            foreach (var wi in _wearables) list.Add(new RlvWorn(wi.ItemId, UUID.Zero, 0, (int)wi.Type, wi.Name));
            return list;
        }

        public void Wear(IReadOnlyList<(RlvItem Item, int Point, bool Add)> items)
        {
            if (C is not { } c) return;
            _ = Task.Run(async () =>
            {
                foreach (var (item, point, add) in items)
                {
                    try
                    {
                        var inv = await s.Inventory.ItemAsync(item.Target, CancellationToken.None);
                        if (inv == null) continue;
                        if (item.Kind == RlvItemKind.Object && point != 0)
                            await s.Wardrobe.AttachAsync(c, inv, (AttachmentPoint)point, replace: !add, CancellationToken.None);
                        else if (item.Kind is RlvItemKind.Object or RlvItemKind.Clothing or RlvItemKind.BodyPart)
                            await s.Wardrobe.WearAsync(c, new[] { inv }, replace: !add, CancellationToken.None);
                        else if (item.Kind == RlvItemKind.Gesture)
                            await s.Gestures.ActivateAsync(new JsonObject { ["id"] = item.Target.ToString() }, true, CancellationToken.None);
                    }
                    catch (Exception e) { s.Log.LogInformation("RLV: wearing {Item}: {Why}", item.Name, e.Message); }
                }
                await RefreshWornAsync(c, CancellationToken.None);
            });
        }

        public void TakeOff(IReadOnlyList<UUID> items)
        {
            if (C is not { } c) return;
            _ = Task.Run(async () =>
            {
                foreach (var id in items)
                {
                    try { await s.Wardrobe.TakeOffAsync(c, new[] { id }, CancellationToken.None); }
                    catch (Exception e) { s.Log.LogInformation("RLV: taking off {Item}: {Why}", id, e.Message); }
                }
                await RefreshWornAsync(c, CancellationToken.None);
            });
        }
    }

    /// <summary>A prim's global position, root and whether it's worn (and on whom), for RLV's checks.</summary>
    public (Vector3d Position, UUID Root, bool Attachment, bool Hud, UUID Wearer)? RlvObject(UUID id) => _rlvHost().Object(id);

    /// <summary>Where someone is (global), for the IM distance exceptions.</summary>
    public Vector3d? RlvAvatarAt(UUID id) => GlobalOf(id);

    /// <summary>Why touching a prim isn't allowed now (null: it is).</summary>
    public string? RlvTouch(UUID prim)
    {
        if (!RlvActive || _rlvHost().Object(prim) is not { } o) return null;
        return Rlv.CanTouch(o.Root, o.Attachment, o.Hud, o.Wearer, o.Position);
    }

    /// <summary>Why a teleport to a region by name (this one: a local teleport) isn't allowed now.</summary>
    public string? RlvTeleport(GridClient c, string region, Vector3 pos)
    {
        if (!RlvActive) return null;
        if (c.Network.CurrentSim is { } sim && string.Equals(sim.Name, region, StringComparison.OrdinalIgnoreCase))
        {
            Utils.LongToUInts(sim.Handle, out var x, out var y);
            return Rlv.CanTeleport(new Vector3d(x + (double)pos.X, y + (double)pos.Y, pos.Z));
        }
        return Rlv.CanTeleport(null);
    }

    /// <summary>Why a teleport to a global position isn't allowed now (map clicks, the headset's point teleport).</summary>
    public string? RlvTeleport(Vector3d global) => RlvActive ? Rlv.CanTeleport(global) : null;

    /// <summary>A script turned the avatar (@setrot): the headset's view follows (the client sets it).</summary>
    public Action<float>? TurnedByScript { get; set; }

    /// <summary>Within the way out's half minute, nothing a script asks moves the person back (logged).</summary>
    bool WayOutHolds(string what)
    {
        if (!RlvWayOutRecent) return false;
        Log.LogInformation("RLV: {What} refused: the way out was used moments ago", what);
        return true;
    }
}
