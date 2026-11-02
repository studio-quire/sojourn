using LibreMetaverse;

namespace Sojourn.Core.Rlv;

/// <summary>
/// What the restrictions allow (RLVa's RlvActions, rlvactions.cpp): each check returns null when allowed, or why not in
/// plain words naming the restriction and the object holding it ("RLV: teleporting is restricted (@tploc, by 'Collar')"),
/// so whoever asked (the person, the assistant, a menu) can say why. The way out runs under <see cref="Override"/>.
/// </summary>
public sealed partial class RlvEngine
{
    static readonly AsyncLocal<bool> Overriding = new();

    /// <summary>The way out's scope: nothing RLV says stops what runs inside it (docs/rlv.md > The way out).</summary>
    public static IDisposable Override()
    {
        bool was = Overriding.Value;
        Overriding.Value = true;
        return new Restore(() => Overriding.Value = was);
    }

    /// <summary>Whether the current flow is the way out's.</summary>
    public static bool Overridden => Overriding.Value;

    sealed class Restore(Action a) : IDisposable { public void Dispose() => a(); }

    static readonly AsyncLocal<bool> AppReading = new();

    /// <summary>The app's own reads through an op (incognito's triggers counting who's near, the situation): not the
    /// person's or a caller's, so RLV neither refuses nor filters them; nothing they learn is shown as it is.</summary>
    public static IDisposable OwnRead()
    {
        bool was = AppReading.Value;
        AppReading.Value = true;
        return new Restore(() => AppReading.Value = was);
    }

    public static bool InOwnRead => AppReading.Value;

    string Why(string what, string key)
    {
        var by = Holder(key) is { } id ? $", by {Named(id)}" : "";
        return $"RLV: {what} is restricted (@{key}{by})";
    }

    bool Free => Overriding.Value;

    // ---------- moving ----------

    string? StandBlock(UUID except = default)
    {
        if (!_host.Sitting) return null;
        return except == UUID.Zero ? (Count("unsit") > 0 ? Why("standing up", "unsit") : null)
            : HasExcept("unsit", except) ? Why("standing up", "unsit") : null;
    }

    /// <summary>Standing up (the stand key, the stick, a menu, the assistant).</summary>
    public string? CanStand() { if (Free) return null; lock (_gate) return StandBlock(); }

    /// <summary>Sitting on something (its root's position for the distance limits), or the ground (target zero).</summary>
    public string? CanSit(UUID target, Vector3d? at)
    {
        if (Free) return null;
        lock (_gate)
        {
            if (Count("sit") > 0) return Why("sitting", "sit");
            if (_host.Sitting && (Count("unsit") > 0 || Count("standtp") > 0))
                return Why("changing seats", Count("unsit") > 0 ? "unsit" : "standtp");
            if (target == UUID.Zero) return null;
            bool forced = _running.Count > 0 && _running.Peek().Key == "sit" && _running.Peek().Type == RlvType.Force;
            if (!forced && Count("interact") > 0) return Why("sitting on things", "interact");
            if (!forced && at is { } p)
            {
                var d = Vector3d.Distance(_host.Position, p);
                if (Count("sittp") > 0 && ModFloat(RlvDictionary.SitTpDist) is { } st && d >= st) return Why($"sitting further than {st:0.#} m away", "sittp");
                if (Count("fartouch") > 0 && ModFloat(RlvDictionary.FarTouchDist) is { } ft && d >= ft) return Why($"reaching further than {ft:0.#} m", "fartouch");
            }
            return null;
        }
    }

    /// <summary>A teleport to a place: local (within 256 m, as the viewer counts) or not; a landmark; home.</summary>
    public string? CanTeleport(Vector3d? dest, bool landmark = false, bool home = false)
    {
        if (Free) return null;
        lock (_gate)
        {
            var except = Current;
            if (StandBlock(except) is { } s) return s.Replace("standing up", "teleporting while seated");
            if (landmark && Count("tplm") > 0) return Why("teleporting by landmark", "tplm");
            if (dest is { } d && !home && !landmark && Local(d))
            {
                var dist = Vector3d.Distance(_host.Position, d);
                if (HasExcept("sittp", except) && ModFloat(RlvDictionary.SitTpDist) is { } st && dist >= st) return Why("teleporting nearby", "sittp");
                if (HasExcept("tplocal", except) && ModFloat(RlvDictionary.TpLocalDist) is { } tl && dist >= Math.Min(tl, 256)) return Why($"teleporting further than {Math.Min(tl, 256):0.#} m", "tplocal");
                return null;
            }
            if (HasExcept("tploc", except)) return Why(home ? "teleporting home" : "teleporting", "tploc");
            return null;
        }
    }

    bool Local(Vector3d d)
    {
        var p = _host.Position;
        double dx = d.X - p.X, dy = d.Y - p.Y;
        return dx * dx + dy * dy < 256.0 * 256.0;
    }

    /// <summary>Accepting someone's teleport offer.</summary>
    public string? CanAcceptTeleportOffer(UUID from)
    {
        if (Free) return null;
        lock (_gate)
        {
            if (Count("tplure") > 0 && !IsException("tplure", from)) return Why("accepting teleport offers", "tplure");
            return StandBlock() is { } s ? s.Replace("standing up", "teleporting while seated") : null;
        }
    }

    /// <summary>Whether an offer from them is taken at once (@accepttp).</summary>
    public bool AutoAcceptTeleport(UUID from) { lock (_gate) return Count("accepttp") > 0 || (from != UUID.Zero && IsException("accepttp", from, strict: false)); }

    /// <summary>Answering someone's request to be teleported to the person (sending them a lure).</summary>
    public string? CanAcceptTeleportRequest(UUID from)
    {
        if (Free) return null;
        lock (_gate) return Count("tprequest") > 0 && !IsException("tprequest", from) ? Why("answering teleport requests", "tprequest") : null;
    }

    public bool AutoAcceptTeleportRequest(UUID from) { lock (_gate) return Count("accepttprequest") > 0 || (from != UUID.Zero && IsException("accepttprequest", from, strict: false)); }

    public string? CanFly() { if (Free) return null; lock (_gate) return HasExcept("fly", Current) ? Why("flying", "fly") : null; }
    public string? CanJump() { if (Free) return null; lock (_gate) return Count("jump") > 0 ? Why("jumping", "jump") : null; }
    public bool TempRunBlocked { get { lock (_gate) return !Free && Count("temprun") > 0; } }
    public bool AlwaysRunBlocked { get { lock (_gate) return !Free && Count("alwaysrun") > 0; } }

    // ---------- chat ----------

    /// <summary>An emote: "/me " or "/me'" (RlvUtil::isEmote).</summary>
    public static bool IsEmote(string s) => s.StartsWith("/me ", StringComparison.OrdinalIgnoreCase) || s.StartsWith("/me'", StringComparison.OrdinalIgnoreCase);

    /// <summary>RLVa's filterChat: what @sendchat lets through of a line (emotes, short /commands and (( OOC )) pass;
    /// other chat becomes "..."), or null when it changes nothing.</summary>
    string? Filtered(string text, bool filterEmote)
    {
        if (text.Length == 0) return null;
        if (IsEmote(text))
        {
            if (!filterEmote) return null;
            if (text.IndexOfAny("\"()*=^_?~".ToCharArray()) >= 0 || text.Contains(" -") || text.Contains("- ") || text.Contains("''")) return "...";
            if (Count("emote") == 0)
            {
                int dot = text.IndexOf('.');
                int len = dot > 0 && dot < 20 ? dot + 1 : 20;
                return text.Length > len ? text[..len] : null;
            }
            return null;
        }
        if (text[0] == '/') return text.Length > 7 ? "..." : null;
        if (text.Length >= 4 && text.StartsWith("((") && text.EndsWith("))")) return null;
        return "...";
    }

    /// <summary>
    /// Chat the person (or anyone acting for them) is about to say, as the viewer's chat bar sends it under RLVa: on a
    /// channel other than 0, @sendchannel decides; on 0, @redirchat/@rediremote send it to their channels instead,
    /// @sendchat turns it into "...", and @chatnormal/@chatshout/@chatwhisper change how loud. Returns the text, channel
    /// and type to send (text null: nothing goes out) and why, when something changed.
    /// </summary>
    public (string? Text, int Channel, ChatType Type, string? Why) Chat(string text, int channel, ChatType type)
    {
        if (Free) return (text, channel, type, null);
        lock (_gate)
        {
            if (channel != 0)
                return CanSendChannel(channel) ? (text, channel, type, null)
                    : (null, channel, type, Why($"chatting on channel {channel}", Count("sendchannel") > 0 ? "sendchannel" : "sendchannel_except"));
            if (type is ChatType.StartTyping or ChatType.StopTyping)
                return Count("redirchat") > 0 ? (null, 0, type, null) : (text, 0, type, null);
            bool emote = IsEmote(text);
            var redir = emote ? "rediremote" : "redirchat";
            if (_exceptions.Any(e => e.Key == redir) && (emote || Filtered(text, false) != null))
            {
                foreach (var ch in _exceptions.Where(e => e.Key == redir).Select(e => (int)e.Value).Distinct().ToList())
                    if (CanSendChannel(ch)) _host.Reply(ch, text);
                return (null, 0, type, Why(emote ? "emoting in nearby chat" : "chatting in nearby chat", redir) + " (sent to its channel instead)");
            }
            string? why = null;
            if (Count("sendchat") > 0 && Filtered(text, true) is { } f)
            {
                why = Why("chatting in nearby chat", "sendchat");
                text = f;
            }
            var t = type;
            if (type is ChatType.Shout or ChatType.Normal && Count("chatnormal") > 0) t = ChatType.Whisper;
            else if (type == ChatType.Shout && Count("chatshout") > 0) t = ChatType.Normal;
            else if (type == ChatType.Whisper && Count("chatwhisper") > 0) t = ChatType.Normal;
            if (t != type) why ??= Why(type == ChatType.Shout ? "shouting" : type == ChatType.Whisper ? "whispering" : "speaking aloud",
                type == ChatType.Shout ? (Count("chatnormal") > 0 ? "chatnormal" : "chatshout") : type == ChatType.Whisper ? "chatwhisper" : "chatnormal");
            return (text, 0, t, why);
        }
    }

    /// <summary>Whether ordinary nearby chat is held (redirected or turned to "..."), without saying anything.</summary>
    public string? ChatHeld()
    {
        lock (_gate)
        {
            if (_exceptions.Any(e => e.Key == "redirchat")) return Why("chatting in nearby chat", "redirchat") + " (sent to its channel instead)";
            return Count("sendchat") > 0 ? Why("chatting in nearby chat", "sendchat") : null;
        }
    }

    bool CanSendChannel(int ch) =>
        (Count("sendchannel") == 0 || IsException("sendchannel", ch)) && (Count("sendchannel_except") == 0 || !IsException("sendchannel_except", ch));

    /// <summary>Gestures (@sendgesture).</summary>
    public string? CanGesture() { if (Free) return null; lock (_gate) return Count("sendgesture") > 0 ? Why("playing gestures", "sendgesture") : null; }

    /// <summary>Chat heard nearby, from someone or something that isn't the person or their own attachment: what's left
    /// of it under @recvchat/@recvemote (null: it isn't shown at all).</summary>
    public string? Heard(UUID from, string message)
    {
        lock (_gate)
        {
            bool emote = IsEmote(message);
            if (!emote && ((Count("recvchat") > 0 && !IsException("recvchat", from)) || (Count("recvchatfrom") > 0 && IsException("recvchatfrom", from))))
                return Filtered(message, false) is { } f ? f : message;
            if (emote && ((Count("recvemote") > 0 && !IsException("recvemote", from)) || (Count("recvemotefrom") > 0 && IsException("recvemotefrom", from))))
                return "/me ...";
            return message;
        }
    }

    // ---------- instant messages ----------

    bool InImRange(UUID who, RlvModifierSpec min, RlvModifierSpec max, Func<UUID, Vector3d?> where)
    {
        if (ModFloat(min) is not { } mn) return false;
        float mx = ModFloat(max) ?? float.MaxValue;
        var p = where(who);
        double d = p is { } pp ? Vector3d.DistanceSquared(_host.Position, pp) : double.MaxValue;
        return mn < mx && mn <= d && d <= mx;
    }

    /// <summary>Sending an IM to someone (or a group: their id); startIm when no conversation with them is open yet.</summary>
    public string? CanSendIm(UUID to, bool startIm, Func<UUID, Vector3d?> where)
    {
        if (Free) return null;
        lock (_gate)
        {
            if ((Count("sendim") > 0 && !IsException("sendim", to) && !InImRange(to, RlvDictionary.SendImDistMin, RlvDictionary.SendImDistMax, where))
                || (Count("sendimto") > 0 && IsException("sendimto", to)))
                return Why("sending IMs", Count("sendim") > 0 ? "sendim" : "sendimto");
            if (startIm && ((Count("startim") > 0 && !IsException("startim", to) && !InImRange(to, RlvDictionary.StartImDistMin, RlvDictionary.StartImDistMax, where))
                || (Count("startimto") > 0 && IsException("startimto", to))))
                return Why("starting IM conversations", Count("startim") > 0 ? "startim" : "startimto");
            return null;
        }
    }

    /// <summary>Whether an IM from them reaches the person (@recvim, @recvimfrom).</summary>
    public bool CanReceiveIm(UUID from, Func<UUID, Vector3d?> where)
    {
        lock (_gate)
            return (Count("recvim") == 0 || IsException("recvim", from) || InImRange(from, RlvDictionary.RecvImDistMin, RlvDictionary.RecvImDistMax, where))
                && (Count("recvimfrom") == 0 || !IsException("recvimfrom", from));
    }

    public const string BlockedRecvIm = "*** IM blocked by your viewer";
    public const string BlockedRecvImRemote = "The Resident you messaged is currently prevented from reading your instant messages at the moment, please try again later.";
    public const string BlockedSendIm = "*** IM blocked by sender's viewer";
    public const string BlockedTpRemote = "The Resident is currently prevented from accepting. Please try again later.";

    // ---------- names and places ----------

    static readonly string[] Anonyms =
    {
        "A resident", "This resident", "That resident", "An individual", "This individual", "That individual", "A person",
        "This person", "That person", "A stranger", "This stranger", "That stranger", "A being", "This being", "That being",
        "An agent", "This agent", "That agent", "A soul", "This soul", "That soul", "Somebody", "Some people", "Someone",
        "Mysterious one", "An unknown being", "Unidentified one", "An unknown person",
    };

    /// <summary>RLVa's getAnonym: the same stand-in for the same legacy name, from the sum of its bytes.</summary>
    public static string Anonym(string legacyName)
    {
        int sum = 0;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(legacyName)) sum += (sbyte)b;
        return Anonyms[(int)((uint)sum % (uint)Anonyms.Length)];
    }

    /// <summary>Whether someone's name may be shown (@shownames, their exception, or the person themself).</summary>
    public bool CanShowName(UUID who)
    {
        lock (_gate) return Count("shownames") == 0 || who == _host.Me || (who != UUID.Zero && IsException("shownames", who));
    }

    public bool NamesHidden { get { lock (_gate) return Count("shownames") > 0; } }

    /// <summary>Whether someone's name tag is drawn (@shownametags, its distance, their exception).</summary>
    public bool CanShowNameTag(UUID who, double distance)
    {
        lock (_gate)
        {
            if (Count("shownametags") == 0 || who == _host.Me || IsException("shownametags", who)) return true;
            var d = ModFloat(RlvDictionary.NameTagsDist) ?? 0;
            return d != 0 && distance < d;
        }
    }

    public bool LocationHidden { get { lock (_gate) return Count("showloc") > 0; } }
    public bool NearbyHidden { get { lock (_gate) return Count("shownearby") > 0; } }
    public bool WorldMapHidden { get { lock (_gate) return Count("showworldmap") > 0; } }
    public bool MiniMapHidden { get { lock (_gate) return Count("showminimap") > 0; } }

    /// <summary>Floating text over an object (@showhovertext:id, ...all, ...world, ...hud).</summary>
    public bool CanShowHoverText(UUID obj, bool hud)
    {
        lock (_gate)
            return !(Count("showhovertextall") > 0 || (!hud && Count("showhovertextworld") > 0) || (hud && Count("showhovertexthud") > 0)
                || _exceptions.Any(e => e.Key == "showhovertext" && e.Value.Equals(obj)));
    }

    // ---------- touching and editing ----------

    /// <summary>Touching something (RlvActions::canTouch): its root, whether it's worn (and by whom), a HUD, and its
    /// position for @fartouch.</summary>
    public string? CanTouch(UUID root, bool attachment, bool hud, UUID wearer, Vector3d? at)
    {
        if (Free) return null;
        lock (_gate)
        {
            bool can = (hud || Count("touchall") == 0) && !(Count("touchthis") > 0 || _exceptions.Any(e => e.Key == "touchthis" && e.Value.Equals(root)));
            string key = Count("touchall") > 0 && !hud ? "touchall" : "touchthis";
            bool Far() => Count("fartouch") > 0 && at is { } p && ModFloat(RlvDictionary.FarTouchDist) is { } ft && Vector3d.Distance(_host.Position, p) > ft;
            if (can)
            {
                if (!attachment)
                {
                    if (Count("touchworld") > 0 && !IsException("touchworld", root, false)) { can = false; key = "touchworld"; }
                    else if (Far()) { can = false; key = "fartouch"; }
                    else if (Count("interact") > 0) { can = false; key = "interact"; }
                }
                else if (wearer != _host.Me)
                {
                    if ((Count("touchattach") > 0 || Count("touchattachother") > 0) && !(IsException("touchattach", root, false) || IsException("touchattach", wearer, false)))
                    { can = false; key = Count("touchattach") > 0 ? "touchattach" : "touchattachother"; }
                    else if (IsException("touchattachother", wearer)) { can = false; key = "touchattachother"; }
                    else if (Far()) { can = false; key = "fartouch"; }
                    else if (Count("interact") > 0) { can = false; key = "interact"; }
                }
                else if (!hud)
                {
                    if ((Count("touchattach") > 0 || Count("touchattachself") > 0) && !IsException("touchattach", root, false))
                    { can = false; key = Count("touchattach") > 0 ? "touchattach" : "touchattachself"; }
                }
                else if (Count("touchhud") > 0 && !IsException("touchhud", root, false)) { can = false; key = "touchhud"; }
            }
            if (!can && Count("touchme") > 0 && _holders.Values.Any(h => (h.Root == root || h.Id == root) && h.Has("touchme"))) can = true;
            return can ? null : Why("touching that", key);
        }
    }

    /// <summary>Editing or building on something (@edit, @editobj, @editattach, @editworld).</summary>
    public string? CanEdit(UUID root, bool attachment)
    {
        if (Free) return null;
        lock (_gate)
        {
            if (Count("edit") > 0 && !IsException("edit", root, false)) return Why("editing", "edit");
            if (IsException("editobj", root, false)) return Why("editing that", "editobj");
            if (attachment && Count("editattach") > 0) return Why("editing attachments", "editattach");
            if (!attachment && Count("editworld") > 0) return Why("editing in-world objects", "editworld");
            return null;
        }
    }

    public string? CanRez() { if (Free) return null; lock (_gate) return Count("rez") > 0 ? Why("rezzing", "rez") : null; }

    /// <summary>A simple yes/no restriction by name, with the words for what it stops.</summary>
    public string? Check(string key, string what)
    {
        if (Free) return null;
        lock (_gate) return Count(key) > 0 ? Why(what, key) : null;
    }

    /// <summary>Giving inventory to someone (@share, with exceptions).</summary>
    public string? CanGive(UUID to)
    {
        if (Free) return null;
        lock (_gate) return Count("share") > 0 && !(to != UUID.Zero && IsException("share", to)) ? Why("giving inventory", "share") : null;
    }

    public string? CanSetGroup() { if (Free) return null; lock (_gate) return HasExcept("setgroup", Current) ? Why("changing the active group", "setgroup") : null; }

    /// <summary>Script permission requests answered for the person (@acceptpermission: yes at once).</summary>
    public bool AutoAcceptPermissions { get { lock (_gate) return Count("acceptpermission") > 0; } }
    /// <summary>A script asking to attach or teleport the person is refused those (RlvUtil::filterScriptQuestions).</summary>
    public bool AttachLockedAll { get { lock (_gate) return _pointLocks.Any(p => p.Point == 0 && (p.Type & Lock.Add) != 0); } }
    public bool TeleportLocked { get { lock (_gate) return Count("tploc") > 0; } }

    // ---------- the camera and what's seen ----------

    /// <summary>The camera's limits for the desktop: the distance from the avatar, the field of view (radians), a lock to
    /// the avatar (@setcam_unlock), mouselook refused.</summary>
    public (float? AvDistMin, float? AvDistMax, float? FovMin, float? FovMax, bool Unlocked, bool NoMouselook) Camera()
    {
        lock (_gate)
            return (Count("setcam_avdistmin") > 0 ? ModFloat(RlvDictionary.CamAvDistMin) : null,
                Count("setcam_avdistmax") > 0 ? ModFloat(RlvDictionary.CamAvDistMax) : null,
                Count("setcam_fovmin") > 0 ? ModFloat(RlvDictionary.CamFovMin) : null,
                Count("setcam_fovmax") > 0 ? ModFloat(RlvDictionary.CamFovMax) : null,
                Count("setcam_unlock") > 0,
                Count("setcam_mouselook") > 0 || (ModFloat(RlvDictionary.CamAvDistMin) ?? 0) > 0);
    }

    /// <summary>The distance beyond which other avatars are drawn as silhouettes (@setcam_avdist), or null.</summary>
    public float? AvatarSilhouetteDistance { get { lock (_gate) return Count("setcam_avdist") > 0 ? ModFloat(RlvDictionary.CamAvDist) : null; } }

    /// <summary>One vision sphere (@setsphere) as RLVa's RlvSphereEffect keeps it: mode (0 blend, 1 blur, 2 variable
    /// blur, 3 chromatic aberration, 4 pixelate), origin (0 the avatar, 1 the camera), its distances and values, the
    /// colour or effect parameters, which side extends (1 beyond max, 2 below min, 3 both), and the tween.</summary>
    public sealed record Sphere(UUID Object, int Mode, int Origin, float DistMin, float DistMax, int DistExtend,
        float ValueMin, float ValueMax, Vector4 Params, float Tween);

    /// <summary>Every vision sphere in force, in the order they apply.</summary>
    public List<Sphere> Spheres()
    {
        lock (_gate)
        {
            var list = new List<Sphere>();
            foreach (var h in _holders.Values)
            {
                if (!h.Has("setsphere", "")) continue;
                T Get<T>(string m, T d) => h.Local.TryGetValue("setsphere." + m, out var v) && v is T t ? t : d;
                var param = h.Local.TryGetValue("setsphere.param", out var pv) && pv is Vector4 p4 ? p4
                    : h.Local.TryGetValue("setsphere.color", out var cv) && cv is Vector3 c3 ? new Vector4(c3.X, c3.Y, c3.Z, 1) : Vector4.Zero;
                list.Add(new Sphere(h.Id, Get("mode", 0), Get("origin", 0), Get("distmin", 0f), Get("distmax", 0f), Get("distextend", 1),
                    Get("valuemin", 1f), Get("valuemax", 1f), param, Get("tween", 0f)));
            }
            return list;
        }
    }

    /// <summary>The @setoverlay in force: texture, alpha, tint, and whether it blocks touches (@setoverlay_touch).</summary>
    public (UUID Texture, float Alpha, Vector3 Tint, bool BlockTouch)? Overlay()
    {
        lock (_gate)
        {
            var h = _holders.Values.FirstOrDefault(x => x.Has("setoverlay", ""));
            if (h == null) return null;
            var tex = h.Local.TryGetValue("setoverlay.texture", out var t) && t is UUID u ? u : UUID.Zero;
            if (tex == UUID.Zero) return null;
            var alpha = h.Local.TryGetValue("setoverlay.alpha", out var a) && a is float f ? f : 1f;
            var tint = h.Local.TryGetValue("setoverlay.tint", out var c) && c is Vector3 v ? v : Vector3.One;
            return (tex, alpha, tint, h.Has("setoverlay_touch"));
        }
    }

    public bool HideSelf { get { lock (_gate) return Count("showself") > 0; } }
    public bool HideSelfHead { get { lock (_gate) return Count("showselfhead") > 0; } }
}
