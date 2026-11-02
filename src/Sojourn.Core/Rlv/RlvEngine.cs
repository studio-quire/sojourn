using System.Globalization;
using LibreMetaverse;

namespace Sojourn.Core.Rlv;

/// <summary>An object holding restrictions (RLVa's RlvObject): its commands in the order they came, and what's known of
/// where it is (its linkset's root, whether it's worn and where).</summary>
public sealed class RlvHolder
{
    public UUID Id { get; init; }
    public string Name { get; set; } = "";
    public List<RlvCommand> Commands { get; } = new();
    /// <summary>Seen in the world model at least once (RLVa's m_fLookup): gone again, it's collected at once.</summary>
    public bool LookedUp { get; set; }
    public int Misses { get; set; }
    public DateTime? MissingSince { get; set; }
    public UUID Root { get; set; }
    /// <summary>The attachment point it's worn on (0: not worn, or not known yet).</summary>
    public int AttachPoint { get; set; }
    /// <summary>Its local modifiers (setsphere_distmin...), by "behaviour.modifier".</summary>
    public Dictionary<string, object> Local { get; } = new();

    public bool Has(string key) => Commands.Any(c => c.Key == key);
    public bool Has(string key, string option) => Commands.Any(c => c.Key == key && c.Option == option);
}

/// <summary>
/// Sojourn's RLVa: the restrictions objects hold, their exceptions and modifiers, after Firestorm's RlvHandler
/// (rlvhandler.cpp, read 2026-11-01). Commands come from the person's own objects by llOwnerSay (AgentSession.Rlv.cs);
/// what they restrict is checked in RlvEngine.Checks.cs; what the world must do for them (replies, sits, teleports,
/// wearing) goes through <see cref="IRlvHost"/>. Everything under one lock; the host's calls never block.
/// docs/rlv.md.
/// </summary>
public sealed partial class RlvEngine
{
    readonly object _gate = new();
    readonly IRlvHost _host;
    readonly Dictionary<UUID, RlvHolder> _holders = new();
    readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    readonly List<(UUID Obj, string Key, object Value)> _exceptions = new();
    readonly Dictionary<RlvModifierSpec, List<(object Value, UUID Obj, string Key)>> _mods = new();
    readonly List<(UUID Obj, int Channel, string Filter)> _notify = new();
    // the command being run, and the object it's from (RLVa's current command and object stacks)
    readonly Stack<RlvCommand> _running = new();

    /// <summary>Version strings RLVa 2.4.2 reports (RLV 3.4.3).</summary>
    public const string Version = "RestrainedLove viewer v3.4.3 (RLVa 2.4.2)";
    public const string VersionLegacy = "RestrainedLife viewer v3.4.3 (RLVa 2.4.2)";
    public const string VersionNum = "3040300";
    public const string VersionImplNum = "2040213";

    public RlvEngine(IRlvHost host) => _host = host;

    /// <summary>Something restricted changed (a restriction, an exception, a modifier or a local modifier): what the
    /// screen draws from them (vision spheres, name tags, the menus) should look again. Raised outside the lock.</summary>
    public event Action? Changed;

    /// <summary>Each command run, with how it went (for the log and the debug echo).</summary>
    public event Action<RlvCommand, RlvRet>? Ran;

    int _version;
    /// <summary>Goes up whenever <see cref="Changed"/> would be raised (for per-frame readers).</summary>
    public int Revision => Volatile.Read(ref _version);

    UUID Current => _running.Count > 0 ? _running.Peek().Object : UUID.Zero;

    // ---------- entry ----------

    /// <summary>A line an object of the person's said by llOwnerSay that starts with '@': every comma-separated command
    /// in it, lowercased, as the viewer runs them (llviewermessage.cpp). Returns what each did.</summary>
    public List<(string Command, RlvRet Ret)> Process(UUID obj, string objName, string line)
    {
        var results = new List<(string, RlvRet)>();
        var text = line.StartsWith('@') ? line[1..] : line;
        text = text.ToLowerInvariant();
        bool changed;
        lock (_gate)
        {
            int before = _version;
            if (_holders.TryGetValue(obj, out var known) && objName.Length > 0) known.Name = objName;
            _names[obj] = objName;
            foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var ret = Run(new RlvCommand(obj, part.Trim()), external: true);
                results.Add((part, ret));
            }
            FlushWear();
            changed = _version != before;
        }
        if (changed) Changed?.Invoke();
        return results;
    }

    readonly Dictionary<UUID, string> _names = new();

    /// <summary>The name an object went by when it last spoke.</summary>
    public string NameOf(UUID obj)
    {
        lock (_gate) return _holders.TryGetValue(obj, out var h) && h.Name.Length > 0 ? h.Name : _names.GetValueOrDefault(obj, "an object");
    }

    RlvRet Run(RlvCommand cmd, bool external)
    {
        if (!cmd.Valid) return Report(cmd, RlvRet.Syntax, external);
        _running.Push(cmd);
        try
        {
            var ret = cmd.Type switch
            {
                RlvType.Add => AddCommand(cmd),
                RlvType.Remove => RemoveCommand(cmd),
                RlvType.Clear => ClearCommand(cmd),
                RlvType.Force => ForceCommand(cmd),
                RlvType.Reply => ReplyCommand(cmd),
                _ => RlvRet.Param,
            };
            return Report(cmd, ret, external);
        }
        finally { _running.Pop(); }
    }

    RlvRet Report(RlvCommand cmd, RlvRet ret, bool external)
    {
        Ran?.Invoke(cmd, ret);
        // @notify: scripts hear every add, remove and clear an object issued, valid or not (RlvBehaviourNotifyHandler)
        if (external)
        {
            if (cmd.Type is RlvType.Add or RlvType.Remove) Notify(cmd.Text, "=" + cmd.Param);
            else if (cmd.Type == RlvType.Clear) Notify(cmd.Text, "");
        }
        return ret;
    }

    // ---------- restrictions ----------

    RlvRet AddCommand(RlvCommand cmd)
    {
        if (cmd.Info is not { } info) return RlvRet.Param;
        if (info.Exclusive && Count(info.Key) > 0 && !HolderHas(cmd.Object, info.Key)) return RlvRet.Lock;
        if (!_holders.TryGetValue(cmd.Object, out var h))
        {
            _holders[cmd.Object] = h = new RlvHolder { Id = cmd.Object, Name = _names.GetValueOrDefault(cmd.Object, "") };
            // where it is, at once (the garbage collector keeps it current)
            if (_host.Object(cmd.Object) is { } o)
            {
                h.LookedUp = true;
                h.Root = o.Root;
                h.AttachPoint = o.Attachment && o.Wearer == _host.Me ? _host.Worn().FirstOrDefault(w => w.Object == o.Root)?.Point ?? 0 : 0;
            }
        }
        if (h.Commands.Any(c => c.SameAs(cmd))) return RlvRet.Duplicate;
        h.Commands.Add(cmd);
        var ret = Apply(cmd, add: true);
        if (ret != RlvRet.Success && ret != RlvRet.Deprecated)
        {
            h.Commands.RemoveAll(c => c.SameAs(cmd));
            if (h.Commands.Count == 0) _holders.Remove(cmd.Object);
        }
        else Bump();
        return ret;
    }

    RlvRet RemoveCommand(RlvCommand cmd)
    {
        if (!_holders.TryGetValue(cmd.Object, out var h)) return RlvRet.Unset;
        int i = h.Commands.FindIndex(c => c.SameAs(cmd));
        if (i < 0) return RlvRet.Unset;
        var held = h.Commands[i];
        h.Commands.RemoveAt(i);
        var ret = Apply(held.AsRemove(), add: false);
        if (h.Commands.Count == 0)
        {
            _holders.Remove(cmd.Object);
            foreach (var list in _mods.Values) list.RemoveAll(v => v.Obj == cmd.Object && v.Key == "");
        }
        Bump();
        return ret;
    }

    /// <summary>@clear[=filter]: removes every restriction of the object whose text contains the filter.</summary>
    RlvRet ClearCommand(RlvCommand cmd)
    {
        var filter = cmd.Param;
        if (_holders.TryGetValue(cmd.Object, out var h))
            foreach (var c in h.Commands.ToList())
                if (filter.Length == 0 || c.Text.Contains(filter, StringComparison.Ordinal))
                    Run(new RlvCommand(cmd.Object, c.Text + "=y"), external: false);
        return RlvRet.Success;
    }

    /// <summary>Takes away everything an object holds (it's gone: detached, derezzed, or out of reach), as RLVa's
    /// garbage collector issues @clear for it.</summary>
    public void Drop(UUID obj, string why)
    {
        bool had;
        lock (_gate)
        {
            had = _holders.ContainsKey(obj);
            if (had)
            {
                Dropped?.Invoke(obj, NameOf(obj), why);
                Run(new RlvCommand(obj, "clear"), external: false);
                _notify.RemoveAll(n => n.Obj == obj);
            }
        }
        if (had) Changed?.Invoke();
    }

    /// <summary>An object's restrictions were all taken away because it went (id, name, why).</summary>
    public event Action<UUID, string, string>? Dropped;

    /// <summary>Everything at once (logging out; RLV turned off).</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            foreach (var id in _holders.Keys.ToList()) Run(new RlvCommand(id, "clear"), external: false);
            _holders.Clear(); _counts.Clear(); _exceptions.Clear(); _mods.Clear(); _notify.Clear();
            _attachLocks.Clear(); _pointLocks.Clear(); _wearLocks.Clear(); _folderLocks.Clear();
            Bump();
        }
        Changed?.Invoke();
    }

    void Bump() => _version++;

    /// <summary>What adding or removing a restriction does beyond being held: counts, exceptions, modifiers, locks.</summary>
    RlvRet Apply(RlvCommand cmd, bool add)
    {
        var info = cmd.Info!;
        bool count = false;
        RlvRet ret = RlvRet.Success;
        switch (info.Opt)
        {
            case RlvOpt.None:
                if (cmd.HasOption) return RlvRet.Option;
                count = true;
                break;
            case RlvOpt.Exception:
                if (!UUID.TryParse(cmd.Option, out var ex)) return RlvRet.Option;
                Except(cmd, info.Key, ex, add);
                count = true;
                break;
            case RlvOpt.NoneOrException:
                if (cmd.HasOption)
                {
                    if (!UUID.TryParse(cmd.Option, out var ex2)) return RlvRet.Option;
                    Except(cmd, info.Key, ex2, add);
                }
                else count = true;
                break;
            case RlvOpt.Modifier:
                if (!cmd.HasOption || !TryValue(cmd.Option, info.Modifier!.Default.GetType(), out var mv)) return RlvRet.Option;
                Modify(info.Modifier!, mv, cmd.Object, add);
                count = true;
                break;
            case RlvOpt.NoneOrModifier:
                if (cmd.HasOption)
                {
                    if (info.Modifier is not { } spec || !TryValue(cmd.Option, spec.Default.GetType(), out var mv2)) return RlvRet.Option;
                    Modify(spec, mv2, cmd.Object, add);
                }
                else
                {
                    if (info.Key == "setsphere" && add && Count("setsphere") >= 6) return RlvRet.Lock;
                    if (info.Modifier is { AddDefaultOnEmpty: true } spec2) Modify(spec2, spec2.Default, cmd.Object, add);
                }
                count = true;
                break;
            case RlvOpt.Custom:
                ret = ApplyCustom(cmd, add, ref count);
                if (ret != RlvRet.Success) return ret;
                break;
        }
        if (count)
        {
            if (add)
            {
                cmd.Counted = true;
                if (cmd.Strict) _exceptions.Add((cmd.Object, "permissive", info.Key));
                _counts[info.Key] = Count(info.Key) + 1;
            }
            else
            {
                if (cmd.Strict) RemoveException(cmd.Object, "permissive", info.Key);
                if (_holders.TryGetValue(cmd.Object, out var h)) foreach (var k in h.Local.Keys.Where(k => k.StartsWith(info.Key + ".")).ToList()) h.Local.Remove(k);
                _counts[info.Key] = Math.Max(0, Count(info.Key) - 1);
            }
        }
        return info.Deprecated ? RlvRet.Deprecated : RlvRet.Success;
    }

    RlvRet ApplyCustom(RlvCommand cmd, bool add, ref bool count)
    {
        var key = cmd.Info!.Key;
        switch (key)
        {
            case "addattach":
            case "remattach":
            {
                int pt = cmd.HasOption ? RlvNames.AttachPoint(cmd.Option) : 0;
                if (pt == 0 && cmd.HasOption) return RlvRet.Option;
                LockPoints(pt, cmd.Object, key == "addattach" ? Lock.Add : Lock.Remove, add);
                count = !cmd.HasOption;
                return RlvRet.Success;
            }
            case "detach":
                if (!cmd.HasOption) { count = false; LockObject(cmd.Object, add); return RlvRet.Success; }
                {
                    int pt = RlvNames.AttachPoint(cmd.Option);
                    if (pt == 0) return RlvRet.Option;
                    LockPoints(pt, cmd.Object, Lock.Add | Lock.Remove, add);
                    return RlvRet.Success;
                }
            case "addoutfit":
            case "remoutfit":
            {
                int wt = cmd.HasOption ? RlvNames.WearableType(cmd.Option) : -1;
                if (cmd.HasOption && wt < 0) return RlvRet.Option;
                LockWearables(wt, cmd.Object, key == "addoutfit" ? Lock.Add : Lock.Remove, add);
                count = true;
                return RlvRet.Success;
            }
            case "attachthis":
            case "detachthis":
            {
                var src = FolderSource(cmd);
                if (src == null) return RlvRet.Option;
                FolderLock(new FolderLockEntry(src.Value.Kind, src.Value.Value, false,
                    (cmd.Info.Wear & RlvWear.Subtree) != 0, cmd.Object, key == "attachthis" ? Lock.Add : Lock.Remove), add);
                count = true;
                return RlvRet.Success;
            }
            case "attachthis_except":
            case "detachthis_except":
                if (!cmd.HasOption) return RlvRet.Option;
                FolderLock(new FolderLockEntry(FolderSourceKind.SharedPath, cmd.Option, true,
                    (cmd.Info.Wear & RlvWear.Subtree) != 0, cmd.Object, key == "attachthis_except" ? Lock.Add : Lock.Remove), add);
                count = true;
                return RlvRet.Success;
            case "sharedwear":
            case "sharedunwear":
                if (cmd.HasOption) return RlvRet.Option;
                FolderLock(new FolderLockEntry(FolderSourceKind.SharedPath, "", false, true, cmd.Object, key == "sharedunwear" ? Lock.Remove : Lock.Add), add);
                count = true;
                return RlvRet.Success;
            case "unsharedwear":
            case "unsharedunwear":
            {
                if (cmd.HasOption) return RlvRet.Option;
                var l = key == "unsharedunwear" ? Lock.Remove : Lock.Add;
                FolderLock(new FolderLockEntry(FolderSourceKind.Root, "", false, true, cmd.Object, l), add);
                FolderLock(new FolderLockEntry(FolderSourceKind.SharedPath, "", true, true, cmd.Object, l), add);
                count = true;
                return RlvRet.Success;
            }
            case "redirchat":
            case "rediremote":
                if (!int.TryParse(cmd.Option, out var ch) || !ValidReplyChannel(ch)) return RlvRet.Option;
                Except(cmd, key, ch, add);
                count = true;
                return RlvRet.Success;
            case "notify":
            {
                if (!cmd.HasOption) return RlvRet.Option;
                var parts = cmd.Option.Split(';', 2);
                if (!int.TryParse(parts[0], out var nch)) return RlvRet.Option;
                var filter = parts.Length > 1 ? parts[1] : "";
                if (add) _notify.Add((cmd.Object, nch, filter));
                else
                {
                    int i = _notify.FindIndex(n => n.Obj == cmd.Object && n.Channel == nch && n.Filter == filter);
                    if (i >= 0) _notify.RemoveAt(i);
                }
                count = true;
                return RlvRet.Success;
            }
            case "sendchannel":
            case "sendchannel_except":
                if (cmd.HasOption)
                {
                    if (!int.TryParse(cmd.Option, out var sc) || sc <= 0) return RlvRet.Option;
                    Except(cmd, key, sc, add);
                }
                else count = true;
                return RlvRet.Success;
            case "recvim":
            case "sendim":
            case "startim":
                if (!cmd.HasOption) { count = true; return RlvRet.Success; }
                if (UUID.TryParse(cmd.Option, out var who)) { Except(cmd, key, who, add); return RlvRet.Success; }
                {
                    // <dist_min>[;<dist_max>]: people within that range are an exception (squared, as RLVa keeps them)
                    var opts = cmd.Option.Split(';');
                    if (opts.Length > 2 || !TryFloat(opts[0], out var min) || min < 0) return RlvRet.Option;
                    float max = float.MaxValue;
                    if (opts.Length == 2 && (!TryFloat(opts[1], out max) || max < 0)) return RlvRet.Option;
                    var (smin, smax) = key switch
                    {
                        "recvim" => (RlvDictionary.RecvImDistMin, RlvDictionary.RecvImDistMax),
                        "sendim" => (RlvDictionary.SendImDistMin, RlvDictionary.SendImDistMax),
                        _ => (RlvDictionary.StartImDistMin, RlvDictionary.StartImDistMax),
                    };
                    Modify(smin, min * min, cmd.Object, add, key);
                    if (opts.Length == 2) Modify(smax, max * max, cmd.Object, add, key);
                    count = true;
                    return RlvRet.Success;
                }
            case "shownametags":
                if (!cmd.HasOption) { count = true; return RlvRet.Success; }
                if (UUID.TryParse(cmd.Option, out var tag)) { Except(cmd, key, tag, add); return RlvRet.Success; }
                if (!TryFloat(cmd.Option, out var dist)) return RlvRet.Option;
                Modify(RlvDictionary.NameTagsDist, dist, cmd.Object, add);
                count = true;
                return RlvRet.Success;
            case "setcam_fovmin":
            case "setcam_fovmax":
            {
                if (!cmd.HasOption || !TryFloat(cmd.Option, out var fov)) return RlvRet.Option;
                Modify(cmd.Info.Modifier!, fov, cmd.Object, add);
                count = true;
                return RlvRet.Success;
            }
            case "camzoommin":
            case "camzoommax":
            {
                float mult = 1;
                if (cmd.HasOption && (!TryFloat(cmd.Option, out mult) || mult <= 0)) return RlvRet.Option;
                var spec = key == "camzoommin" ? RlvDictionary.CamFovMin : RlvDictionary.CamFovMax;
                Modify(spec, RlvDictionary.DefaultFov / mult, cmd.Object, add, key);
                var target = key == "camzoommin" ? "setcam_fovmin" : "setcam_fovmax";
                _counts[target] = Math.Max(0, Count(target) + (add ? 1 : -1));
                count = true;
                return RlvRet.Success;
            }
            default:
                return RlvRet.Param;
        }
    }

    // ---------- exceptions and modifiers ----------

    void Except(RlvCommand cmd, string key, object value, bool add)
    {
        if (add) _exceptions.Add((cmd.Object, key, value));
        else RemoveException(cmd.Object, key, value);
    }

    void RemoveException(UUID obj, string key, object value)
    {
        int i = _exceptions.FindIndex(e => e.Obj == obj && e.Key == key && e.Value.Equals(value));
        if (i >= 0) _exceptions.RemoveAt(i);
    }

    /// <summary>A global modifier's value from an object (key: the behaviour it came with when that isn't the
    /// modifier's own, as camzoommin's or recvim's range).</summary>
    void Modify(RlvModifierSpec spec, object value, UUID obj, bool add, string key = "")
    {
        if (!_mods.TryGetValue(spec, out var list)) _mods[spec] = list = new();
        if (add) list.Add((value, obj, key));
        else
        {
            int i = list.FindIndex(v => v.Obj == obj && v.Value.Equals(value) && v.Key == key);
            if (i >= 0) list.RemoveAt(i);
        }
    }

    /// <summary>A modifier's combined value (the smallest, the largest or the first set), or null when none holds one.
    /// With @setcam held, only its holder's values count (RLVa's primary object).</summary>
    object? ModValue(RlvModifierSpec spec)
    {
        if (!_mods.TryGetValue(spec, out var list) || list.Count == 0) return null;
        IEnumerable<(object Value, UUID Obj, string Key)> vals = list;
        if (spec.Name.StartsWith("setcam_") && Count("setcam") > 0 && Holder("setcam") is { } primary)
        {
            var own = list.Where(v => v.Obj == primary).ToList();
            if (own.Count > 0) vals = own;
        }
        return spec.Comp switch
        {
            RlvComp.Min => vals.Select(v => v.Value).OrderBy(v => Convert.ToSingle(v)).First(),
            RlvComp.Max => vals.Select(v => v.Value).OrderByDescending(v => Convert.ToSingle(v)).First(),
            _ => vals.First().Value,
        };
    }

    float? ModFloat(RlvModifierSpec spec) => ModValue(spec) is { } v ? Convert.ToSingle(v) : null;

    static bool TryFloat(string s, out float f) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f);

    /// <summary>A modifier's option as its type (float, int, vector "x/y/z", vector4, a UUID), as RLVa converts them.</summary>
    static bool TryValue(string s, Type t, out object value)
    {
        value = 0f;
        if (t == typeof(float)) { if (TryFloat(s, out var f)) { value = f; return true; } return false; }
        if (t == typeof(int)) { if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) { value = i; return true; } return false; }
        if (t == typeof(UUID)) { if (UUID.TryParse(s, out var u)) { value = u; return true; } return false; }
        var parts = s.Split('/');
        if (t == typeof(Vector3) && parts.Length == 3 && parts.All(p => TryFloat(p, out _)))
        {
            value = new Vector3(F(parts[0]), F(parts[1]), F(parts[2]));
            return true;
        }
        if (t == typeof(Vector4) && parts.Length == 4 && parts.All(p => TryFloat(p, out _)))
        {
            value = new Vector4(F(parts[0]), F(parts[1]), F(parts[2]), F(parts[3]));
            return true;
        }
        return false;
        static float F(string p) => float.Parse(p, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // ---------- queries over what's held ----------

    int Count(string key) => _counts.GetValueOrDefault(key);

    bool HolderHas(UUID obj, string key) => _holders.TryGetValue(obj, out var h) && h.Has(key);

    /// <summary>The first object holding a behaviour (with no option), for naming it.</summary>
    UUID? Holder(string key) => _holders.Values.FirstOrDefault(h => h.Commands.Any(c => c.Key == key && (!c.HasOption || c.Counted)))?.Id;

    /// <summary>Whether a behaviour is held (counted: with no option, or with a modifier).</summary>
    public bool Has(string key) { lock (_gate) return Count(key) > 0; }

    /// <summary>Held by any object but <paramref name="except"/> (RLVa's hasBehaviourExcept).</summary>
    bool HasExcept(string key, UUID except) =>
        _holders.Values.Any(h => h.Id != except && h.Commands.Any(c => c.Key == key && (!c.HasOption || c.Counted)));

    bool HasException(string key) => _exceptions.Any(e => e.Key == key);

    /// <summary>RLVa's isException: permissive, any holder's exception will do; strict (a _sec restriction is held and
    /// @permissive isn't), every object restricting it must have made the exception.</summary>
    bool IsException(string key, object value, bool? strict = null)
    {
        bool isStrict = strict ?? (Count(key) > 0 && !IsPermissive(key));
        if (!isStrict) return _exceptions.Any(e => e.Key == key && e.Value.Equals(value));
        var restricting = _holders.Values.Where(h => h.Commands.Any(c => c.Key == key && !c.HasOption && (Has0("permissive") || c.Strict))).Select(h => h.Id).ToHashSet();
        foreach (var e in _exceptions)
            if (e.Key == key && e.Value.Equals(value)) restricting.Remove(e.Obj);
        return restricting.Count == 0 && _exceptions.Any(e => e.Key == key && e.Value.Equals(value));
    }

    bool Has0(string key) => Count(key) > 0;

    /// <summary>Exceptions count from any object unless a strict (_sec) restriction or @permissive=n says otherwise.</summary>
    bool IsPermissive(string key)
    {
        var info = RlvDictionary.Find(key, RlvType.Add);
        if (info is not { Strictable: true }) return true;
        return !(Has0("permissive") || _exceptions.Any(e => e.Key == "permissive" && e.Value.Equals(key)));
    }

    public static bool ValidReplyChannel(int ch, bool loopback = false) => ch > (loopback ? -1 : 0) && ch != int.MaxValue;

    // ---------- replies and notifications ----------

    void Notify(string text, string suffix)
    {
        foreach (var (_, ch, filter) in _notify.ToList())
            if (filter.Length == 0 || text.Contains(filter, StringComparison.OrdinalIgnoreCase))
                _host.Reply(ch, "/" + text + suffix);
    }

    /// <summary>A @notify line from the world (sat, unsat, worn, attached...), to every listener whose filter matches.</summary>
    public void NotifyEvent(string text)
    {
        lock (_gate) Notify(text, "");
    }

    // ---------- status ----------

    /// <summary>Everything held, by object: for the person, the assistant and SojournRlv status.</summary>
    public List<(UUID Id, string Name, int AttachPoint, List<string> Restrictions)> Holders()
    {
        lock (_gate)
            return _holders.Values.Select(h => (h.Id, h.Name.Length > 0 ? h.Name : _names.GetValueOrDefault(h.Id, ""), h.AttachPoint,
                h.Commands.Select(c => c.Text).ToList())).ToList();
    }

    public bool Any { get { lock (_gate) return _holders.Count > 0; } }

    /// <summary>Which object holds a behaviour (with no option), named for the person: "Leather Collar".</summary>
    public string? Who(string key)
    {
        lock (_gate) return Holder(key) is { } id ? NameOf(id) : null;
    }

    /// <summary>The holders' ids the world model should still have (for the garbage collector).</summary>
    public List<RlvHolder> Snapshot()
    {
        lock (_gate) return _holders.Values.ToList();
    }

    /// <summary>The garbage collector's pass (RLVa's onGC, every 30 s): an object that was seen and is gone is dropped at
    /// once; one never seen is dropped after 20 misses (about ten minutes). <paramref name="find"/> answers whether it's
    /// in the world now, its root and attachment point (0: not worn).</summary>
    public void Collect(Func<UUID, (UUID Root, int Point)?> find)
    {
        foreach (var h in Snapshot())
        {
            var where = find(h.Id);
            if (where == null)
            {
                // seen and now gone: dropped after a minute missing (RLVa at once; here the world model empties for a few
                // seconds across a teleport, and the pass only runs in a settled region); never seen: after 20 passes
                bool drop;
                lock (_gate)
                {
                    h.MissingSince ??= DateTime.UtcNow;
                    drop = h.LookedUp ? DateTime.UtcNow - h.MissingSince > TimeSpan.FromSeconds(55) : ++h.Misses > 20;
                }
                if (drop) Drop(h.Id, "gone");
                continue;
            }
            lock (_gate)
            {
                h.MissingSince = null;
                bool wasWorn = h.AttachPoint != 0;
                h.LookedUp = true;
                h.Root = where.Value.Root;
                h.AttachPoint = where.Value.Point;
                if (!wasWorn && h.AttachPoint != 0 && h.Has("detach", "")) LockObject(h.Id, true);
                if (wasWorn && h.AttachPoint == 0 && h.Has("detach", "")) LockObject(h.Id, false);
            }
        }
    }

    /// <summary>An attachment came off (detached, not dropped): everything it or a prim of it holds goes (RLVa's onDetach).</summary>
    public void Detached(UUID root)
    {
        foreach (var h in Snapshot())
            if (h.Root == root || h.Id == root) Drop(h.Id, "detached");
    }
}
