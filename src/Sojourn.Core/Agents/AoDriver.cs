using System.Text.Json.Nodes;
using LibreMetaverse;
using LibreMetaverse.Packets;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Leap;
using Sojourn.Core.World;

namespace Sojourn.Core.Agents;

/// <summary>
/// The AO's session half: which folder it plays (by default Firestorm's #Firestorm/#AO when the person has one, else
/// none until they pick one: Sojourn makes no folders of its own unless asked), its switches kept per account, the
/// region's animation list for my avatar fed to <see cref="AoEngine"/> as Firestorm's startMotion/stopMotion hook does,
/// its sends as one ordered AgentAnimation, its clock, water, region changes, and OpenCollar/LockMeister's switches
/// arriving from a worn relay. What gives way: seats keep their own sits unless the set overrides sits (:SO), and with
/// smart sit (:SM) a seat that plays its own animation wins; ground sits on prims are the seat's; the pose stand pauses
/// it; the headset's tracked hands win over it by the `tracking` setting (its animations are mine, never "things
/// animating me"). Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class AoDriver
{
    readonly AgentSession _s;
    readonly object _lock = new();
    readonly AoEngine _engine;
    readonly List<(UUID Id, bool Start)> _out = new();
    readonly Dictionary<UUID, int> _seen = new();
    readonly HashSet<string> _paused = new();
    readonly Dictionary<UUID, long> _reasserted = new();
    double _clock;
    GridClient? _wired;
    List<string> _warnings = new();
    string _how = "none";
    AoFolders.Kind _kind = AoFolders.Kind.Nothing;
    UUID _folder;
    string _folderName = "";
    Task? _loading;
    TaskCompletionSource _firstLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AoDriver(AgentSession s)
    {
        _s = s;
        _engine = new AoEngine
        {
            Send = (id, start) => _out.Add((id, start)),
            SittingOnObject = SittingOnObject,
            SeatAnimates = SeatAnimates,
        };
        _engine.EnableStands(Store.Flag("ao_stands", true));
    }

    AccountStore Store => _s.InventoryStore;

    /// <summary>Whether the desktop is in first person (mouselook), for sets with no stands there. Set by the client.</summary>
    public Func<bool>? Mouselook { get; set; }

    /// <summary>The engine, for checks. Use under <see cref="Run"/>.</summary>
    internal AoEngine Engine => _engine;

    // ---------- the person's switches ----------

    /// <summary>The AO on (Firestorm's UseAO; off by default, as there).</summary>
    public bool On
    {
        get => Store.Flag("ao", false);
        set { Store.Set("ao", value); Apply(); }
    }

    public bool StandsOn
    {
        get => Store.Flag("ao_stands", true);
        set { Store.Set("ao_stands", value); Run(e => e.EnableStands(value)); }
    }

    /// <summary>Firestorm's BridgeIntegrationOC / LM: an OpenCollar's or a LockMeister item's AO commands obeyed (on by default).</summary>
    public bool OpenCollar { get => Store.Flag("ao_opencollar", true); set => Store.Set("ao_opencollar", value); }
    public bool LockMeister { get => Store.Flag("ao_lockmeister", true); set => Store.Set("ao_lockmeister", value); }

    /// <summary>Firestorm's LockAOFolders (on by default): the AO's folder can't be deleted, moved or renamed by the
    /// inventory ops; the AO's own ops still change it.</summary>
    public bool LockFolders { get => Store.Flag("ao_lock_folders", true); set => Store.Set("ao_lock_folders", value); }

    /// <summary>Paused for a reason ("collar": a collar's AO off; "pose": the pose stand). Paused, nothing plays.</summary>
    public void Pause(string why, bool paused)
    {
        lock (_lock) { if (paused) _paused.Add(why); else _paused.Remove(why); }
        Apply();
    }

    public bool Paused(string why) { lock (_lock) return _paused.Contains(why); }

    void Apply()
    {
        bool want;
        lock (_lock) want = On && _paused.Count == 0;
        Run(e => { if (e.Enabled != want) e.Enable(want); });
    }

    /// <summary>The folder the AO plays: "firestorm" (the default: #Firestorm/#AO when there), "none", or one the person picked.</summary>
    public string FolderChoice => Store.Text("ao_folder") ?? "firestorm";

    /// <summary>The AO's folder (its subtree is locked while <see cref="LockFolders"/>); zero when none.</summary>
    public UUID Folder => _folder;

    public bool Protects(UUID id)
    {
        if (!LockFolders || _folder == UUID.Zero || _kind == AoFolders.Kind.Notecard) return false;
        if (id == _folder) return true;
        var store = _s.Client?.Inventory.Store;
        for (int i = 0; i < 64 && store != null && id != UUID.Zero && store.TryGetValue(id, out var n); i++)
        {
            if (n.ParentUUID == _folder) return true;
            id = n.ParentUUID;
        }
        return false;
    }

    // ---------- running the engine ----------

    /// <summary>Calls into the engine under the lock and sends what it asked for, in order.</summary>
    public T Run<T>(Func<AoEngine, T> f)
    {
        List<(UUID, bool)> send;
        T r;
        lock (_lock)
        {
            r = f(_engine);
            send = _out.ToList();
            _out.Clear();
        }
        Flush(send);
        return r;
    }

    public void Run(Action<AoEngine> f) => Run(e => { f(e); return 0; });

    void Flush(List<(UUID Id, bool Start)> send)
    {
        if (send.Count == 0 || _s.Client is not { } c || c.Network.CurrentSim is not { } sim) return;
        for (int i = 0; i < send.Count; i += 32)
        {
            var part = send.Skip(i).Take(32).ToArray();
            c.Network.SendPacket(new AgentAnimationPacket
            {
                AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
                AnimationList = part.Select(p => new AgentAnimationPacket.AnimationListBlock { AnimID = p.Id, StartAnim = p.Start }).ToArray(),
                PhysicalAvatarEventList = Array.Empty<AgentAnimationPacket.PhysicalAvatarEventListBlock>(),
            }, sim);
        }
    }

    // ---------- wiring to a login ----------

    /// <summary>A new login's client: the region's animation list, the clock, region changes; the folder loaded once
    /// the inventory has.</summary>
    public void Wire(GridClient c)
    {
        _wired = c;
        lock (_lock)
        {
            _seen.Clear();
            _reasserted.Clear();
            if (_firstLoad.Task.IsCompleted) _firstLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        c.Network.SimChanged += (_, _) => _s.Guard("ao region", () =>
        {
            if (_s.Client != c) return;
            var motion = Run(e => e.OnRegionChange());
            if (motion is { } m) c.Self.AnimationStart(m, true);
        });
        HostClock.Subscribe(this, dt => _s.Guard("ao tick", () => Tick(dt)));
        var before = _s.Inventory.Loading;   // a reconnect's: the new login starts its own
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < 300 && (_s.Inventory.Loading is null || _s.Inventory.Loading == before); i++) await Task.Delay(200);
                if (_s.Inventory.Loading is { } t) await t;
                if (_s.Client != c) return;
                await ReloadAsync(CancellationToken.None);
            }
            catch (Exception e) { _s.Log.LogWarning(e, "AO load"); }
            finally { _firstLoad.TrySetResult(); }
        });
    }

    /// <summary>Waits for the login's first reading of the AO's folder (ops before it would act on no sets; up to a
    /// minute: the inventory loads first).</summary>
    public async Task ReadyAsync(CancellationToken ct)
    {
        Task first;
        lock (_lock) first = _firstLoad.Task;
        try { await first.WaitAsync(TimeSpan.FromSeconds(60), ct); }
        catch (TimeoutException) { throw new LeapError("the AO's folder is still being read (the inventory is loading); try again shortly"); }
        if (_loading is { IsCompleted: false } t) await t.WaitAsync(ct);
    }

    public bool Loaded { get { lock (_lock) return _firstLoad.Task.IsCompleted; } }

    /// <summary>The region's list of animations on my avatar (SelfAnimations.Wire): new ones and restarted ones (a new
    /// sequence number) start motions, gone ones stop them, as LLVOAvatar::processAnimationStateChanges calls
    /// startMotion/stopMotion for Firestorm's overrider.</summary>
    public void OnSelfAnimations(GridClient c, List<Animation> list)
    {
        if (c != _wired) return;
        List<(UUID, bool)> send;
        lock (_lock)
        {
            var now = new Dictionary<UUID, int>();
            foreach (var a in list) now[a.AnimationID] = a.AnimationSequence;
            var started = now.Where(kv => !_seen.TryGetValue(kv.Key, out var seq) || seq != kv.Value).Select(kv => kv.Key).ToList();
            var stopped = _seen.Keys.Where(k => !now.ContainsKey(k)).ToList();
            _seen.Clear();
            foreach (var kv in now) _seen[kv.Key] = kv.Value;
            foreach (var id in stopped)
            {
                var r = _engine.Override(id, false);
                if (r != UUID.Zero) _out.Add((r, false));
            }
            foreach (var id in started)
            {
                var r = _engine.Override(id, true);
                if (r != UUID.Zero) _out.Add((r, true));
            }
            // Always's animations kept alive (FS:PP), but not asked for again more than once in 3 s each
            long t = Environment.TickCount64;
            _engine.ReassertAlways(id =>
            {
                if (now.ContainsKey(id)) return true;
                if (_reasserted.TryGetValue(id, out var at) && t - at < 3000) return true;
                _reasserted[id] = t;
                return false;
            });
            send = _out.ToList();
            _out.Clear();
        }
        Flush(send);
    }

    void Tick(double dt)
    {
        var c = _s.Client;
        if (c is null || c != _wired || !c.Network.Connected || c.Network.CurrentSim is not { } sim) return;
        _clock += dt;
        bool under = c.Self.SimPosition.Z < sim.WaterHeight;
        bool ml = Mouselook?.Invoke() == true;
        Run(e =>
        {
            e.Tick(_clock);
            if (e.UnderWater != under) e.BelowWater(under);
            e.InMouselook(ml);
        });
    }

    bool SittingOnObject()
    {
        var c = _s.Client;
        return c != null && _s.Region?.AvatarById(c.Self.AgentID) is { ParentID: not 0 };
    }

    /// <summary>Firestorm's foreignAnimations(): an animation on me from a prim of the seat's linkset that isn't an attachment.</summary>
    bool SeatAnimates()
    {
        var c = _s.Client;
        if (c is null || _s.Region is not { } w || w.AvatarById(c.Self.AgentID) is not { ParentID: not 0 } me) return false;
        if (!w.Prims.TryGetValue(me.ParentID, out var seatPrim)) return false;
        var seat = _s.Objects.RootOf(w, seatPrim).ID;
        foreach (var p in _s.Anims.Now())
        {
            if (p.Source == UUID.Zero || p.Source == c.Self.AgentID) continue;
            if (w.PrimById(p.Source) is not { } src) continue;
            if (_s.Objects.IsAttachment(w, src, out _, out var root)) continue;
            if (root.ID == seat) return true;
        }
        return false;
    }

    // ---------- OpenCollar and LockMeister ----------

    /// <summary>An owner-say from one of my attachments carrying Firestorm's bridge AO line ("&lt;clientAO
    /// state=on|off|standon|standoff&gt;", with Sojourn's relay adding " source=oc|lm"): obeyed as Firestorm's bridge
    /// handler does (on: unpaused and stands on; off: paused; standon/standoff: stands), and kept out of chat. True
    /// when taken.</summary>
    public bool TakeOwnerSay(GridClient c, ChatEventArgs e)
    {
        if (e.Type != ChatType.OwnerSay || e.SourceType != ChatSourceType.Object || e.OwnerID != c.Self.AgentID) return false;
        var msg = e.Message ?? "";
        if (!msg.StartsWith("<clientAO ", StringComparison.Ordinal)) return false;
        // an attach event's line can arrive before the attachment's prim does: obeyed once it's seen worn (up to 5 s)
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 25; i++)
            {
                if (_s.Client != c) return;
                if (Worn(c, e.SourceID) is { } worn)
                {
                    if (worn) _s.Guard("ao relay", () => Obey(msg, e.FromName));
                    else _s.Log.LogInformation("AO: ignored {Msg} from {Object}: not worn", msg, e.FromName);
                    return;
                }
                await Task.Delay(200);
            }
            _s.Log.LogInformation("AO: ignored {Msg} from {Object}: never seen", msg, e.FromName);
        });
        return true;
    }

    /// <summary>Whether a prim is one of my attachments; null while it hasn't arrived.</summary>
    bool? Worn(GridClient c, UUID id)
    {
        if (_s.Region is not { } w || w.PrimById(id) is not { } prim) return null;
        return _s.Objects.IsAttachment(w, prim, out var wearer, out _) && wearer?.ID == c.Self.AgentID;
    }

    void Obey(string msg, string from)
    {
        var (state, source) = ParseBridge(msg);
        if (source == "oc" && !OpenCollar || source == "lm" && !LockMeister) return;
        if (!On) return;   // as Firestorm: nothing while the AO is off
        switch (state)
        {
            case "on": Pause("collar", false); StandsOn = true; break;
            case "off": Pause("collar", true); break;
            case "standon": StandsOn = true; break;
            case "standoff": StandsOn = false; break;
            default: return;
        }
        _s.Log.LogInformation("AO: {State} from {Object} ({Source})", state, from, source ?? "bridge");
        _s.Events.Log("SojournAo", new JsonObject { ["by"] = from, ["state"] = state, ["source"] = source ?? "" });
    }

    public static (string? State, string? Source) ParseBridge(string msg)
    {
        string? Attr(string name)
        {
            int at = msg.IndexOf(name + "=", StringComparison.Ordinal);
            if (at < 0) return null;
            at += name.Length + 1;
            int end = at;
            while (end < msg.Length && char.IsLetter(msg[end])) end++;
            return msg[at..end];
        }
        return (Attr("state"), Attr("source"));
    }

    /// <summary>The relay a person can wear so their collar can switch this AO (Firestorm's bridge does it inside its
    /// own worn object). Wearing one is the person's call.</summary>
    public const string RelayScript = """
// Sojourn AO relay: lets an OpenCollar or LockMeister item switch Sojourn's AO, as Firestorm's LSL bridge does.
// Wear it (any attachment point). It only ever tells its owner's viewer "<clientAO state=...>".
integer ocChannel;
integer ocListen;
key collar = NULL_KEY;
tell(string s, string from) { llOwnerSay("<clientAO state=" + s + " source=" + from + ">"); }
start()
{
    key owner = llGetOwner();
    ocChannel = (integer)("0x" + llGetSubString((string)owner, 30, -1));
    if (ocChannel > 0) ocChannel = -ocChannel;
    llListenRemove(ocListen);
    ocListen = llListen(ocChannel, "", NULL_KEY, "");
    llListen(-8888, "", NULL_KEY, "");
    llWhisper(ocChannel, "OpenCollar?");
}
default
{
    state_entry() { start(); }
    on_rez(integer p) { llResetScript(); }
    changed(integer c) { if (c & CHANGED_OWNER) llResetScript(); }
    listen(integer ch, string name, key id, string msg)
    {
        if (ch == ocChannel)
        {
            if (collar == NULL_KEY && msg == "OpenCollar=Yes") { collar = id; return; }
            if (msg == "OpenCollar=No") { collar = NULL_KEY; return; }
            if (id != collar) return;
            integer z = llSubStringIndex(msg, "ZHAO_");
            if (z < 0) return;
            string c = llGetSubString(msg, z + 5, -1);
            if (llGetSubString(c, 0, 4) == "AOOFF") tell("off", "oc");
            else if (llGetSubString(c, 0, 3) == "AOON") tell("on", "oc");
            else if (llGetSubString(c, 0, 7) == "STANDOFF") tell("standoff", "oc");
            else if (llGetSubString(c, 0, 6) == "STANDON") tell("standon", "oc");
        }
        else if (ch == -8888 && (key)llGetSubString(msg, 0, 35) == llGetOwner()
                 && llList2Key(llGetObjectDetails(id, [OBJECT_ROOT]), 0) != collar)
        {
            string cmd = llList2String(llParseStringKeepNulls(llGetSubString(msg, 36, -1), ["|"], []), 0);
            if (cmd == "booton") tell("on", "lm");
            else if (cmd == "bootoff") tell("off", "lm");
        }
    }
}
""";

    // ---------- the folder ----------

    /// <summary>Which folder plays: "firestorm", "none", or a folder id; then reloaded.</summary>
    public async Task<JsonObject> ChooseAsync(string choice, CancellationToken ct)
    {
        if (choice is not ("firestorm" or "none"))
        {
            if (!UUID.TryParse(choice, out var id) || _s.Inventory.Node(id) is not InventoryFolder) throw new LeapError($"{choice} isn't a folder of mine");
        }
        Store.Set("ao_folder", choice);
        await ReloadAsync(ct);
        return Status();
    }

    /// <summary>Firestorm's #Firestorm/#AO, when the person has one.</summary>
    public UUID FirestormFolder()
    {
        var c = _s.Client;
        var store = c?.Inventory.Store;
        if (store?.RootFolder is not { } root) return UUID.Zero;
        var fs = store.GetContents(root.UUID).OfType<InventoryFolder>().FirstOrDefault(f => f.Name == "#Firestorm");
        if (fs is null) return UUID.Zero;
        return store.GetContents(fs.UUID).OfType<InventoryFolder>().FirstOrDefault(f => f.Name == "#AO")?.UUID ?? UUID.Zero;
    }

    public Task ReloadAsync(CancellationToken ct)
    {
        Task t;
        lock (_lock) t = _loading = LoadAsync(ct);
        return t;
    }

    async Task LoadAsync(CancellationToken ct)
    {
        var c = _s.Client ?? throw new NotConnected("viewer not connected");
        var choice = FolderChoice;
        UUID folder = UUID.Zero;
        string how = "none";
        if (choice == "firestorm")
        {
            // the root's folders are loaded at login; #Firestorm's may not be yet
            var store = c.Inventory.Store;
            if (store?.RootFolder is { } root && store.GetContents(root.UUID).OfType<InventoryFolder>().FirstOrDefault(f => f.Name == "#Firestorm") is { } fs)
                await Fresh(c, fs.UUID, ct);
            folder = FirestormFolder();
            if (folder != UUID.Zero) how = "firestorm";
        }
        else if (UUID.TryParse(choice, out var picked)) { folder = picked; how = "picked"; }

        var warnings = new List<string>();
        var sets = new List<AoSet>();
        var kind = AoFolders.Kind.Nothing;
        string name = "";
        if (folder != UUID.Zero)
        {
            var tree = await TreeAsync(c, folder, (_s.Inventory.Node(folder) as InventoryFolder)?.Name ?? "", 0, ct);
            name = tree.Name;
            kind = AoFolders.KindOf(tree);
            switch (kind)
            {
                case AoFolders.Kind.Sets: sets = AoFolders.ReadSets(tree, warnings); break;
                case AoFolders.Kind.Set: if (AoFolders.ReadSet(tree, warnings) is { } one) sets.Add(one); break;
                case AoFolders.Kind.Notecard:
                    foreach (var card in tree.Items.Where(i => i.IsNotecard))
                    {
                        var text = (await _s.InventoryOps.NotecardTextAsync(card.Id, ct)).Text;
                        if (AoFolders.FromZhao(tree.Name, tree.Id, text, tree.Items, warnings) is { } zs)
                        {
                            KeptFlags(zs);
                            sets.Add(zs);
                            break;
                        }
                    }
                    break;
            }
            if (sets.Count == 0) warnings.Add($"{tree.Name}: no animations in Firestorm's AO format or a ZHAO-II notecard");
        }
        lock (_lock)
        {
            _folder = folder;
            _folderName = name;
            _kind = kind;
            _how = how;
            _warnings = warnings;
        }
        bool want;
        lock (_lock) want = On && _paused.Count == 0;
        Run(e =>
        {
            e.Load(sets, Store.Text("ao_set"));
            // what the region plays now told to the engine first (off, it only notes the motion), so turning on
            // overrides the right state: a person sitting at login gets the set's sit, not its stand
            if (!e.Enabled)
                foreach (var id in _seen.Keys.Where(k => k != AoMotions.Type).ToList()) e.Override(id, true);
            if (e.Enabled != want) e.Enable(want);
        });
        _s.Log.LogInformation("AO: {N} set{S} from {How} folder {Name}{Warn}", sets.Count, sets.Count == 1 ? "" : "s", how, name,
            warnings.Count > 0 ? $" ({warnings.Count} warnings: {string.Join("; ", warnings.Take(3))})" : "");
    }

    /// <summary>A notecard set's flags, kept by Sojourn by the folder's id (they have no folder names to live in).</summary>
    void KeptFlags(AoSet set)
    {
        var o = Store.Read(d => d["ao_notecard_sets"]?[set.FolderId.ToString()]?.DeepClone() as JsonObject);
        if (o is null) return;
        set.SitOverride = o.Bool("sit_override", false);
        set.Smart = o.Bool("smart", false);
        set.MouselookStandDisable = o.Bool("no_mouselook_stands", false);
        foreach (var st in set.States)
            if (o["states"]?[st.Name] is JsonObject so)
            {
                st.Cycle = so.Bool("cycle", false);
                st.Random = so.Bool("random", false);
                st.CycleTime = (float)so.Num("cycle_time", 0);
            }
    }

    static async Task Fresh(GridClient c, UUID folder, CancellationToken ct)
    {
        try { await c.Inventory.FolderContentsAsync(folder, c.Self.AgentID, true, true, InventorySortOrder.ByName, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { }
    }

    async Task<AoFolders.Folder> TreeAsync(GridClient c, UUID id, string name, int depth, CancellationToken ct)
    {
        await Fresh(c, id, ct);
        var f = new AoFolders.Folder(id, name);
        var store = c.Inventory.Store;
        if (store is null) return f;
        foreach (var n in store.GetContents(id).ToList())
        {
            if (n is InventoryFolder sub)
            {
                if (depth < 4) f.Folders.Add(await TreeAsync(c, sub.UUID, sub.Name, depth + 1, ct));
            }
            else if (n is InventoryItem it)
            {
                var target = it;
                if (it.AssetType == AssetType.Link)
                    target = _s.Inventory.Node(it.AssetUUID) as InventoryItem ?? await _s.Inventory.ItemAsync(it.AssetUUID, ct) ?? it;
                bool anim = target != it ? target.AssetType == AssetType.Animation : it.AssetType == AssetType.Animation;
                f.Items.Add(new AoFolders.Item(it.UUID, it.Name, it.Description ?? "", anim ? target.AssetUUID : UUID.Zero, anim && target.AssetUUID != UUID.Zero,
                    IsNotecard: target.AssetType == AssetType.Notecard));
            }
        }
        return f;
    }

    // ---------- the person's changes ----------

    public AoSet SetNamed(string? name)
    {
        lock (_lock)
        {
            if (name is null) return _engine.Current ?? throw new LeapError("no AO set (pick an AO folder: SojournAo folder)");
            return _engine.SetByName(name) ?? throw new LeapError($"no AO set named {name} (sets: {string.Join(", ", _engine.Sets.Select(s => s.Name))})");
        }
    }

    public void Select(AoSet set)
    {
        Run(e => e.Select(set));
        Store.Set("ao_set", set.Name);
    }

    /// <summary>A set's flags changed: into its folder's name (Firestorm's format, so Firestorm reads them too), or for a
    /// notecard set into Sojourn's own keeping.</summary>
    public async Task SaveSetAsync(AoSet set, CancellationToken ct)
    {
        if (set.FromNotecard) { SaveNotecardSet(set); return; }
        await RenameAsync(set.FolderId, AoFolders.SetFolderName(set), ct);
    }

    public async Task SaveStateAsync(AoSet set, AoState st, CancellationToken ct)
    {
        if (set.FromNotecard) { SaveNotecardSet(set); return; }
        if (st.FolderId == UUID.Zero) throw new LeapError($"{set.Name} has no {st.Name} folder (add an animation to it first)");
        await RenameAsync(st.FolderId, AoFolders.StateFolderName(st), ct);
    }

    void SaveNotecardSet(AoSet set)
    {
        var states = new JsonObject();
        foreach (var st in set.States)
            if (st.Cycle || st.Random || st.CycleTime > 0)
                states[st.Name] = new JsonObject { ["cycle"] = st.Cycle, ["random"] = st.Random, ["cycle_time"] = st.CycleTime };
        Store.Change(d =>
        {
            var all = d["ao_notecard_sets"] as JsonObject ?? new JsonObject();
            all[set.FolderId.ToString()] = new JsonObject
            {
                ["sit_override"] = set.SitOverride, ["smart"] = set.Smart, ["no_mouselook_stands"] = set.MouselookStandDisable, ["states"] = states,
            };
            d["ao_notecard_sets"] = all;
        });
    }

    async Task RenameAsync(UUID folder, string name, CancellationToken ct)
    {
        var c = _s.Client ?? throw new NotConnected("viewer not connected");
        if (_s.Inventory.Node(folder) is not InventoryFolder f) throw new LeapError($"folder {folder} isn't in my inventory");
        if (f.Name == name) return;
        c.Inventory.UpdateFolderProperties(f.UUID, f.ParentUUID, name, f.PreferredType);
        await Task.Delay(200, ct);
    }

    /// <summary>Links animations into a set's state (making the state's folder when the set has none), each item's
    /// order after the last, as Firestorm's addAnimation does. Only in Firestorm-format sets.</summary>
    public async Task<JsonObject> AddAsync(AoSet set, AoStateKind kind, List<InventoryItem> anims, CancellationToken ct)
    {
        if (set.FromNotecard) throw new LeapError($"{set.Name} is read from a ZHAO-II notecard; change the notecard itself (TesselInventory writeNotecard)");
        var c = _s.Client ?? throw new NotConnected("viewer not connected");
        var st = set.State(kind);
        if (st.FolderId == UUID.Zero || _s.Inventory.Node(st.FolderId) is null)
        {
            st.FolderId = c.Inventory.CreateFolder(set.FolderId, AoFolders.StateFolderName(st));
            await Task.Delay(500, ct);
        }
        int order = st.Steps.Count == 0 ? 0 : st.Steps.Max(x => x.Sort) + 1;
        var added = new JsonArray();
        foreach (var it in anims)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var link = await c.Inventory.CreateLinkAsync(st.FolderId, it.UUID, it.Name, order.ToString(), InventoryType.Animation, UUID.Random(), cts.Token);
            if (link is null) throw new LeapError($"the link to {it.Name} wasn't made" + (added.Count > 0 ? $" ({added.Count} before it were)" : ""));
            added.Add(it.Name);
            order++;
        }
        await ReloadAsync(ct);
        return new JsonObject { ["set"] = set.Name, ["state"] = st.Name, ["added"] = added };
    }

    /// <summary>The inventory nodes a removal would take (a step's link or group folder, by name or position), for the
    /// confirmation and the op.</summary>
    public List<(UUID Id, string Name, bool Folder)> Removal(AoSet set, AoStateKind kind, string? step, int? index)
    {
        if (set.FromNotecard) throw new LeapError($"{set.Name} is read from a ZHAO-II notecard; change the notecard itself");
        var st = set.State(kind);
        var steps = index is { } i ? st.Steps.Skip(i).Take(1).ToList()
            : st.Steps.Where(s => string.Equals(s.Name, step, StringComparison.OrdinalIgnoreCase)).ToList();
        if (steps.Count == 0) throw new LeapError($"{set.Name}/{st.Name} has no step {step ?? index?.ToString()} (steps: {string.Join(", ", st.Steps.Select(s => s.Name))})");
        return steps.Select(s => (s.Id, s.Name, s.IsGroup)).ToList();
    }

    /// <summary>Removes AO links or folders for good (they're links: the animations themselves stay where they are).
    /// The confirmation was the person's.</summary>
    public async Task RemoveNodesAsync(List<(UUID Id, string Name, bool Folder)> nodes, CancellationToken ct)
    {
        var c = _s.Client ?? throw new NotConnected("viewer not connected");
        var trash = _s.Inventory.FolderFor(FolderType.Trash);
        foreach (var (id, _, folder) in nodes)
        {
            if (folder) c.Inventory.MoveFolder(id, trash);
            else await c.Inventory.RemoveItemAsync(id, ct);
        }
        await Task.Delay(300, ct);
        await ReloadAsync(ct);
    }

    /// <summary>A new, empty set: a folder in the AO's folder. With no AO folder yet, Firestorm's #Firestorm/#AO is made
    /// (only ever on the person's word: Sojourn makes no folders by itself).</summary>
    public async Task<JsonObject> NewSetAsync(string name, CancellationToken ct)
    {
        var c = _s.Client ?? throw new NotConnected("viewer not connected");
        name = AoFolders.CleanName(name);
        if (name.Length == 0) throw new LeapError("a set needs a name (no ':' or '|')");
        if (_engine.SetByName(name) != null) throw new LeapError($"there's already a set named {name}");
        var ao = _folder;
        bool made = false;
        if (_kind is AoFolders.Kind.Set or AoFolders.Kind.Notecard) throw new LeapError("the AO's folder is a single set; pick a folder of sets (or firestorm) for new sets");
        if (ao == UUID.Zero)
        {
            var root = _s.Inventory.FolderFor(FolderType.Root);
            var store = c.Inventory.Store!;
            var fs = store.GetContents(root).OfType<InventoryFolder>().FirstOrDefault(f => f.Name == "#Firestorm")?.UUID ?? c.Inventory.CreateFolder(root, "#Firestorm");
            await Task.Delay(300, ct);
            ao = store.GetContents(fs).OfType<InventoryFolder>().FirstOrDefault(f => f.Name == "#AO")?.UUID ?? c.Inventory.CreateFolder(fs, "#AO");
            made = true;
            Store.Set("ao_folder", "firestorm");
            await Task.Delay(300, ct);
        }
        await Fresh(c, ao, ct);
        if (c.Inventory.Store?.GetContents(ao).OfType<InventoryFolder>().FirstOrDefault(f => f.Name.Split(':')[0] == name) is { } there)
            throw new LeapError($"the AO's folder already has a set named {name} ({there.Name}); SojournAo reload reads it");
        var id = c.Inventory.CreateFolder(ao, name);
        await Task.Delay(500, ct);
        await ReloadAsync(ct);
        var o = new JsonObject { ["set"] = name, ["folder_id"] = id.ToString() };
        if (made) o["made"] = "#Firestorm/#AO (Firestorm's AO folder, which Firestorm reads too)";
        return o;
    }

    // ---------- what's said ----------

    public JsonObject Status()
    {
        lock (_lock)
        {
            var e = _engine;
            var o = new JsonObject
            {
                ["on"] = On,
                ["loaded"] = _firstLoad.Task.IsCompleted ? null : false,
                ["playing_now"] = e.Enabled,
                ["stands"] = StandsOn,
                ["paused_by"] = new JsonArray(_paused.Select(p => (JsonNode)p).ToArray()),
                ["folder"] = new JsonObject
                {
                    ["choice"] = FolderChoice, ["id"] = _folder == UUID.Zero ? null : _folder.ToString(), ["name"] = _folderName,
                    ["kind"] = _kind switch { AoFolders.Kind.Sets => "sets", AoFolders.Kind.Set => "one set", AoFolders.Kind.Notecard => "ZHAO-II notecard", _ => "none" },
                    ["how"] = _how,
                },
                ["sets"] = new JsonArray(e.Sets.Select(s => (JsonNode)SetSummary(s)).ToArray()),
                ["set"] = e.Current?.Name,
                ["opencollar"] = OpenCollar,
                ["lockmeister"] = LockMeister,
                ["lock_folders"] = LockFolders,
            };
            if (e.Current is { } cur && e.Enabled && e.StateForMotion(e.LastMotion) is { } st)
            {
                o["state"] = st.Name;
                if (st.Steps.Count > 0 && st.Current < st.Steps.Count) o["step"] = st.Steps[st.Current].Name;
            }
            if (e.UnderWater) o["under_water"] = true;
            if (_warnings.Count > 0) o["warnings"] = new JsonArray(_warnings.Take(12).Select(w => (JsonNode)w).ToArray());
            if (FolderChoice == "firestorm" && _folder == UUID.Zero)
                o["note"] = "no #Firestorm/#AO in this inventory: pick an AO folder (SojournAo folder), or make a set (SojournAo newSet makes #Firestorm/#AO)";
            return o;
        }
    }

    static JsonObject SetSummary(AoSet s) => new()
    {
        ["name"] = s.Name, ["default"] = s.IsDefault, ["sit_override"] = s.SitOverride, ["smart_sit"] = s.Smart,
        ["no_mouselook_stands"] = s.MouselookStandDisable,
        ["states"] = new JsonArray(s.States.Where(st => st.Steps.Count > 0 || st.Tracks.Count > 0).Select(st => (JsonNode)st.Name).ToArray()),
        ["from_notecard"] = s.FromNotecard ? true : null,
    };

    public JsonObject SetDetail(AoSet s)
    {
        lock (_lock)
        {
            var o = SetSummary(s);
            var states = new JsonArray();
            foreach (var st in s.States)
            {
                if (st.Steps.Count == 0 && st.Tracks.Count == 0 && st.FolderId == UUID.Zero) continue;
                var so = new JsonObject
                {
                    ["state"] = st.Name, ["cycle"] = st.Cycle, ["random"] = st.Random, ["cycle_time"] = st.CycleTime,
                    ["steps"] = new JsonArray(st.Steps.Select(x => (JsonNode)new JsonObject
                    {
                        ["name"] = x.Name, ["group"] = x.IsGroup ? true : null,
                        ["animations"] = x.IsGroup ? new JsonArray(x.Members.Select(m => (JsonNode)m.Name).ToArray()) : null,
                    }).ToArray()),
                };
                if (st.Tracks.Count > 0)
                    so["tracks"] = new JsonArray(st.Tracks.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name, ["cycle"] = t.Cycle, ["random"] = t.Random, ["cycle_time"] = t.CycleTime,
                        ["animations"] = new JsonArray(t.Animations.Select(m => (JsonNode)m.Name).ToArray()),
                    }).ToArray());
                if (st.Playing.Count > 0) so["playing"] = true;
                states.Add(so);
            }
            o["states"] = states;
            return o;
        }
    }

    /// <summary>The AO's own animations playing now (for SelfAnimations' list: started by "my AO").</summary>
    public bool Plays(UUID id) { lock (_lock) return _engine.Enabled && _engine.Playing().Contains(id); }

    /// <summary>An animation's name as the current set has it (for SelfAnimations' list), or null.</summary>
    public string? NameOf(UUID id)
    {
        lock (_lock)
        {
            if (_engine.Current is not { } set) return null;
            foreach (var st in set.States)
            {
                foreach (var step in st.Steps)
                    foreach (var m in step.Members)
                        if (m.AssetId == id) return m.Name;
                foreach (var t in st.Tracks)
                    foreach (var m in t.Animations)
                        if (m.AssetId == id) return m.Name;
            }
            return null;
        }
    }
}
