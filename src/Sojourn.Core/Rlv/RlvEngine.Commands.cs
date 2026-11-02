using System.Globalization;
using LibreMetaverse;

namespace Sojourn.Core.Rlv;

/// <summary>Force and reply commands (RLVa's processForceCommand and processReplyCommand).</summary>
public sealed partial class RlvEngine
{
    /// <summary>Where the avatar stood before a sit while @standtp holds: standing up takes them back there.</summary>
    Vector3d _sitSource;
    float _height;
    DateTime _groupChanged;

    static string F3(float f) => f.ToString("0.000", CultureInfo.InvariantCulture);

    RlvRet ForceCommand(RlvCommand cmd)
    {
        if (cmd.Info is not { } info) return RlvRet.Unknown;
        if (cmd.LocalModifier is { } m) return LocalModifier(cmd, info, m);
        if (info.Wear != RlvWear.None)
        {
            if (info.Key is "detach" or "remattach") return (info.Wear & RlvWear.ContextObject) != 0 ? ForceWear(cmd, info.Wear) : ForceDetach(cmd);
            if (info.Key == "remoutfit") return ForceRemOutfit(cmd);
            return ForceWear(cmd, info.Wear);
        }
        switch (info.Key)
        {
            case "unsit":
                if (cmd.HasOption) return RlvRet.Option;
                if (_host.Sitting && !HasExcept("unsit", cmd.Object)) { _pendingUnsit = cmd.Object; _host.Stand(); }
                return RlvRet.Success;
            case "sit":
            {
                if (!UUID.TryParse(cmd.Option, out var target)) return RlvRet.Option;
                if (target == UUID.Zero) return SitGround(cmd);
                if (_host.Object(target) is not { } o || o.Attachment) return RlvRet.Option;
                if (CanSitLocked(target, o.Position) != null) return RlvRet.Lock;
                if (Count("standtp") > 0)
                {
                    if (_host.Sitting) return RlvRet.Lock;
                    _sitSource = _host.Position;
                }
                _pendingSit = cmd.Object;
                _host.Sit(target);
                return RlvRet.Success;
            }
            case "sitground":
                return SitGround(cmd);
            case "tpto":
                return TpTo(cmd);
            case "fly":
            {
                bool on = true;
                if (cmd.HasOption && !bool.TryParse(cmd.Option, out on)) return RlvRet.Option;
                if (on && HasExcept("fly", cmd.Object)) return RlvRet.Lock;
                if (on != _host.Flying) _host.Fly(on);
                return RlvRet.Success;
            }
            case "setgroup":
            {
                if (HasExcept("setgroup", cmd.Object)) return RlvRet.Lock;
                var opts = cmd.Option.Split(';');
                if (cmd.Option.Length == 0 || opts.Length > 2) return RlvRet.Option;
                UUID group;
                if (opts[0] == "none") group = UUID.Zero;
                else if (UUID.TryParse(opts[0], out group)) { if (group != UUID.Zero && _host.Groups.All(g => g.Id != group)) return RlvRet.Option; }
                else
                {
                    // exact matches first; among partial (prefix) matches the last wins (RlvForceHandler<SETGROUP>)
                    group = UUID.Zero;
                    foreach (var g in _host.Groups)
                        if (g.Name.StartsWith(opts[0], StringComparison.OrdinalIgnoreCase))
                        {
                            group = g.Id;
                            if (g.Name.Length == opts[0].Length) break;
                        }
                    if (group == UUID.Zero) return RlvRet.Option;
                }
                if (DateTime.UtcNow - _groupChanged < TimeSpan.FromSeconds(60) && _groupChangedBy == cmd.Object) return RlvRet.Throttled;
                _groupChanged = DateTime.UtcNow;
                _groupChangedBy = cmd.Object;
                _host.SetGroup(group, opts.Length > 1 ? opts[1] : null);
                return RlvRet.Success;
            }
            case "setrot":
            {
                if (!TryFloat(cmd.Option, out var a)) return RlvRet.Option;
                _host.Turn((float)(Math.PI / 2) - a);
                return RlvRet.Success;
            }
            case "adjustheight":
            {
                // <pelvis-to-foot>;<factor>[;<delta>] (RlvCommandOptionAdjustHeight): the hover becomes
                // (reference - the avatar's own) * factor + delta; the avatar's own isn't known here, so the reference counts as it
                // (one value: the hover itself, in centimetres)
                var o = cmd.Option.Split(';');
                if (o.Length > 3 || !TryFloat(o[0], out var first)) return RlvRet.Option;
                float factor = 0, delta = 0;
                if (o.Length > 1 && !TryFloat(o[1], out factor)) return RlvRet.Option;
                if (o.Length > 2 && !TryFloat(o[2], out delta)) return RlvRet.Option;
                _height = Math.Clamp(o.Length == 1 ? first / 100 : delta, -2f, 2f);
                _host.SetHover(_height);
                return RlvRet.Success;
            }
            case "detachme":
            {
                if (cmd.HasOption) return RlvRet.Option;
                var root = _host.Object(cmd.Object)?.Root ?? cmd.Object;
                var w = _host.Worn().FirstOrDefault(x => x.Object == root && x.Point != 0);
                if (w != null && DetachBlock(w, cmd.Object) == null) _toTakeOff.Add(w.Item);
                return RlvRet.Success;
            }
            case "setcam_fov":
            case "setcam_mode":
            case "setcam_focus":
            case "setcam_eyeoffset":
            case "setcam_eyeoffsetscale":
            case "setcam_focusoffset":
                // the camera moves only on the desktop (docs/rlv.md > The camera); the commands are accepted as RLVa does
                if (Count("setcam") > 0 && !HolderHas(cmd.Object, "setcam")) return RlvRet.Lock;
                if (info.Key == "setcam_fov" && TryFloat(cmd.Option, out var fov)) CameraFov = fov;
                return RlvRet.Success;
            case "setoverlay_tween":
                return RlvRet.Success;
            case "setdebug_avatarsex":
            case "setdebug_renderresolutiondivisor":
                return HasExcept("setdebug", cmd.Object) ? RlvRet.Lock : RlvRet.Success;
            default:
                return RlvRet.Param;
        }
    }

    UUID _groupChangedBy;
    UUID _pendingSit, _pendingUnsit;

    /// <summary>A field of view a script asked for by @setcam_fov (radians), for the desktop camera; null when none.</summary>
    public float? CameraFov { get; private set; }

    string? CanSitLocked(UUID target, Vector3d at) => CanSit(target, at);

    RlvRet SitGround(RlvCommand cmd)
    {
        if (HasExcept("sit", cmd.Object) || (_host.Sitting && HasExcept("unsit", cmd.Object))) return RlvRet.Lock;
        _pendingSit = cmd.Object;
        _host.SitGround();
        return RlvRet.Success;
    }

    /// <summary>@tpto:x/y/z[;angle] (global metres) or @tpto:region/x/y/z[;angle]: a teleport scripts ask for, which
    /// RLVa runs whatever @tploc says (a forced teleport).</summary>
    RlvRet TpTo(RlvCommand cmd)
    {
        var opts = cmd.Option.Split(';');
        Vector3? look = null;
        if (opts.Length > 1)
        {
            if (!TryFloat(opts[1], out var angle)) return RlvRet.Option;
            look = new Vector3((float)Math.Cos(angle), (float)Math.Sin(angle), 0);
        }
        var p = opts[0].Split('/');
        if (p.Length == 3 && p.All(x => TryFloat(x, out _)))
        {
            _host.Teleport(new Vector3d(D(p[0]), D(p[1]), D(p[2])), look);
            return RlvRet.Success;
        }
        if (p.Length == 4 && TryFloat(p[1], out var x) && TryFloat(p[2], out var y) && TryFloat(p[3], out var z))
        {
            _host.TeleportRegion(p[0], new Vector3(x, y, z), look);
            return RlvRet.Success;
        }
        return RlvRet.Option;
        static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>setsphere_distmin:2=force and the like: a value of the object's own effect, held only while it holds the
    /// behaviour (processModifier); no option resets it.</summary>
    RlvRet LocalModifier(RlvCommand cmd, RlvBehaviour info, string name)
    {
        if (!_holders.TryGetValue(cmd.Object, out var h) || !h.Has(info.Key, "")) return RlvRet.Unheld;
        var key = info.Key + "." + name;
        if (!cmd.HasOption) { h.Local.Remove(key); Bump(); return RlvRet.Success; }
        if (!TryValue(cmd.Option, info.LocalModifiers[name], out var v)) return RlvRet.Option;
        h.Local[key] = v;
        Bump();
        return RlvRet.Success;
    }

    // ---------- replies ----------

    RlvRet ReplyCommand(RlvCommand cmd)
    {
        if (!int.TryParse(cmd.Param, out var ch) || !ValidReplyChannel(ch, cmd.Object == _host.Me)) return RlvRet.Param;
        if (cmd.Info is not { } info) return RlvRet.Unknown;
        var ret = RlvRet.Success;
        string reply;
        switch (info.Key)
        {
            case "version":
                reply = VersionLegacy;
                break;
            case "versionnew":
                reply = Version;
                break;
            case "versionnum":
                reply = !cmd.HasOption ? VersionNum : cmd.Option == "impl" ? VersionImplNum : "";
                break;
            case "getstatus":
            case "getstatusall":
            {
                var o = cmd.Option.Split(';');
                var filter = o[0];
                var sep = o.Length > 1 && o[1].Length > 0 ? o[1] : "/";
                var sb = new System.Text.StringBuilder();
                foreach (var h in info.Key == "getstatus" ? _holders.Values.Where(x => x.Id == cmd.Object) : _holders.Values)
                    foreach (var hc in h.Commands)
                        if (filter.Length == 0 || hc.Text.Contains(filter, StringComparison.Ordinal)) sb.Append(sep).Append(hc.Text);
                reply = sb.ToString();
                break;
            }
            case "getsitid":
                reply = (_host.Sitting ? _host.SeatId : UUID.Zero).ToString();
                break;
            case "getgroup":
                reply = _host.ActiveGroupName is { Length: > 0 } g ? g : "none";
                break;
            case "getattach":
            {
                int pt = 0;
                if (cmd.HasOption && (pt = RlvNames.AttachPoint(cmd.Option)) == 0) { ret = RlvRet.Option; reply = ""; break; }
                var worn = _host.Worn();
                var sb = new System.Text.StringBuilder(pt == 0 ? "0" : "");
                for (int i = 1; i <= RlvNames.Count; i++)
                    if (pt == 0 || pt == i) sb.Append(worn.Any(w => w.Point == i) ? '1' : '0');
                reply = sb.ToString();
                break;
            }
            case "getattachnames":
            case "getaddattachnames":
            case "getremattachnames":
            {
                var group = cmd.Option;
                var worn = _host.Worn();
                var names = new List<string>();
                for (int i = 1; i <= RlvNames.Count; i++)
                {
                    if (group.Length > 0 && RlvNames.Groups.Contains(group) && RlvNames.Group(i) != group) continue;
                    bool add = info.Key switch
                    {
                        "getattachnames" => worn.Any(w => w.Point == i),
                        "getaddattachnames" => AttachBlock(i, UUID.Zero, true) == null,
                        _ => worn.Any(w => w.Point == i && DetachBlock(w) == null),
                    };
                    if (add) names.Add(RlvNames.PointName(i));
                }
                reply = string.Join(",", names);
                break;
            }
            case "getoutfit":
            {
                int layer = -1;
                if (cmd.HasOption && (layer = RlvNames.WearableType(cmd.Option)) < 0) { ret = RlvRet.Option; reply = ""; break; }
                var worn = _host.Worn();
                var sb = new System.Text.StringBuilder();
                foreach (var t in RlvNames.OutfitOrder)
                    if (layer < 0 || t == layer) sb.Append(worn.Any(w => w.Point == 0 && w.WearableType == t) ? '1' : '0');
                reply = sb.ToString();
                break;
            }
            case "getoutfitnames":
            case "getaddoutfitnames":
            case "getremoutfitnames":
            {
                if (cmd.HasOption) { ret = RlvRet.Option; reply = ""; break; }
                var worn = _host.Worn();
                var names = new List<string>();
                for (int t = 0; t < RlvNames.Layers.Length; t++)
                {
                    bool add = info.Key switch
                    {
                        "getoutfitnames" => worn.Any(w => w.Point == 0 && w.WearableType == t),
                        "getaddoutfitnames" => WearBlock(t, UUID.Zero, true) == null,
                        _ => !RlvNames.IsBodyPart(t) && worn.Any(w => w.Point == 0 && w.WearableType == t && TakeOffBlock(w) == null),
                    };
                    if (add) names.Add(RlvNames.Layers[t]);
                }
                reply = string.Join(",", names);
                break;
            }
            case "getinv":
                reply = GetInv(cmd, out ret);
                break;
            case "getinvworn":
                reply = GetInvWorn(cmd, out ret);
                break;
            case "findfolder":
                reply = FindFolder(cmd, false, out ret);
                break;
            case "findfolders":
                reply = FindFolder(cmd, true, out ret);
                break;
            case "getpath":
                reply = GetPath(cmd, false);
                break;
            case "getpathnew":
                reply = GetPath(cmd, true);
                break;
            case "getcommand":
            {
                var o = cmd.Option.Split(';');
                var match = o[0];
                RlvType? kind = null;
                if (o.Length >= 2)
                {
                    kind = o[1] switch { "" or "any" => null, "add" => RlvType.Add, "force" => RlvType.Force, "reply" => RlvType.Reply, _ => RlvType.Unknown };
                    if (kind == RlvType.Unknown) { ret = RlvRet.Option; reply = ""; break; }
                }
                var sep = o.Length >= 3 ? o[2] : ";";
                reply = string.Join(sep, RlvDictionary.Entries.Where(e => (kind == null || e.Kind == kind) && (match.Length == 0 || e.Name.Contains(match)))
                    .Select(e => e.Name).Distinct());
                break;
            }
            case "getcam_avdist":
                reply = F3(0);
                break;
            case "getcam_avdistmin":
                reply = Count("setcam_avdistmin") > 0 && ModFloat(RlvDictionary.CamAvDistMin) is { } a ? F3(a) : "";
                break;
            case "getcam_avdistmax":
                reply = Count("setcam_avdistmax") > 0 && ModFloat(RlvDictionary.CamAvDistMax) is { } b ? F3(b) : "";
                break;
            case "getcam_fov":
                reply = F3(CameraFovNow?.Invoke() ?? RlvDictionary.DefaultFov);
                break;
            case "getcam_fovmin":
                reply = Count("setcam_fovmin") > 0 && ModFloat(RlvDictionary.CamFovMin) is { } c ? F3(c) : "";
                break;
            case "getcam_fovmax":
                reply = Count("setcam_fovmax") > 0 && ModFloat(RlvDictionary.CamFovMax) is { } d ? F3(d) : "";
                break;
            case "getcam_textures":
                reply = Count("setcam_textures") > 0 && ModValue(RlvDictionary.CamTexture) is UUID u ? u.ToString() : "";
                break;
            case "getheightoffset":
                reply = (_host.HoverHeight * 100).ToString("0.00", CultureInfo.InvariantCulture);
                break;
            case "getdebug_avatarsex":
                reply = "0";
                break;
            case "getdebug_renderresolutiondivisor":
                reply = "1";
                break;
            case "getdebug_restrainedloveforbidgivetorlv":
            case "getdebug_restrainedlovenosetenv":
                reply = "0";
                break;
            case "getdebug_windlightuseatmosshaders":
                reply = "1";
                break;
            default:
                return RlvRet.Param;
        }
        // a failed reply still answers (empty), so the script isn't left waiting (processReplyCommand)
        if (ch > 0) _host.Reply(ch, reply);
        return ret;
    }

    /// <summary>The camera's vertical field of view now (radians), for @getcam_fov.</summary>
    public Func<float>? CameraFovNow { get; set; }

    // ---------- the world's news, for @standtp and @notify ----------

    /// <summary>The avatar sat down (seat zero: the ground). From the session's watch.</summary>
    public void Sat(UUID seat, Vector3d from)
    {
        lock (_gate)
        {
            if (Count("standtp") > 0 && _sitSource == default) _sitSource = from;
            bool legal = Count("sit") == 0 || _pendingSit != UUID.Zero && !HasExcept("sit", _pendingSit);
            Notify(seat == UUID.Zero ? $"sat ground {(legal ? "legally" : "illegally")}" : $"sat object {(legal ? "legally" : "illegally")} {seat}", "");
            _pendingSit = UUID.Zero;
        }
    }

    /// <summary>The avatar stood up from <paramref name="seat"/>. Under @standtp they go back to where they sat from,
    /// unless it was the way out (<paramref name="wayOut"/>).</summary>
    public void Stood(UUID seat, bool wayOut)
    {
        Vector3d back = default;
        lock (_gate)
        {
            bool legal = Count("unsit") == 0 || _pendingUnsit != UUID.Zero && !HasExcept("unsit", _pendingUnsit);
            if (wayOut) legal = Count("unsit") == 0;
            Notify(seat == UUID.Zero ? $"unsat ground {(legal ? "legally" : "illegally")}" : $"unsat object {(legal ? "legally" : "illegally")} {seat}", "");
            _pendingUnsit = UUID.Zero;
            if (Count("standtp") > 0 && _sitSource != default && !wayOut) back = _sitSource;
            _sitSource = default;
        }
        if (back != default) _host.Teleport(back, null);
    }

    /// <summary>Something was put on or taken off: @notify's "attached legally Chest", "unworn illegally shirt"...</summary>
    public void WornChanged(RlvWorn w, bool on)
    {
        lock (_gate)
        {
            if (w.Point != 0)
            {
                bool legal = on ? !PointLocked(w.Point, Lock.Add) : DetachBlock(w) == null;
                Notify($"{(on ? "attached" : "detached")} {(legal ? "legally" : "illegally")} {RlvNames.PointName(w.Point)}", "");
            }
            else if (w.WearableType >= 0)
            {
                bool legal = on ? !LayerLocked(w.WearableType, Lock.Add) : !LayerLocked(w.WearableType, Lock.Remove);
                Notify($"{(on ? "worn" : "unworn")} {(legal ? "legally" : "illegally")} {RlvNames.Layers[w.WearableType]}", "");
            }
        }
    }
}
