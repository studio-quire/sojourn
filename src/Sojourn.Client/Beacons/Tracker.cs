using LibreMetaverse;
using Sojourn.Client.Social;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using NVector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Beacons;

/// <summary>
/// The tracking beacon, as the viewer's LLTracker keeps it (lltracker.cpp; Firestorm's changes noted): one place or one
/// person tracked at a time; a column of light over it (the renderer's, World/Beacons.cs), a label with its name and
/// how far it is from the avatar (Firestorm's FIRE-8234: the viewer measures from the camera), drawn over everything as
/// LLHUDText with no depth test, and an optional beep coming faster as it nears (Firestorm's UISndTrackerBeacon, every
/// distance/30 s). It ends when you arrive: within 3 m of a place, its height counted at half, or 3 m of a person
/// across the ground. A place's height comes up to 1.5 m over the ground when its region's terrain is known (the map
/// gives none). A person is found where the regions in view have them (their avatar, or the region's coarse map
/// positions), else, for a friend who lets you see them on the map, by asking the grid every few seconds (FindAgent,
/// as LLAvatarTracker::findAgent); someone not found anywhere for 15 s, or a friend gone offline, ends it.
/// Claude Opus 5.5, 2026-10-29.
/// </summary>
public sealed class Tracker : IDisposable
{
    /// <summary>The one tracker of this process (the map board and the agent API's SojournBeacons track it).</summary>
    public static Tracker? Shared { get; private set; }

    public enum Kind { Place, Avatar }

    /// <summary>What's tracked: a place (its region and the point in it) or a person.</summary>
    public sealed record Target(Kind Kind, string Name, ulong Region, NVector3 Local, UUID Avatar);

    const float Reached = 3, LostSeconds = 15, FindEvery = 5, LabelAt = 5;
    static readonly UUID BeepSound = new("ed124764-705d-d497-167a-182cd9fa2e6c");   // UISndTrackerBeacon (the alert sound)

    readonly AgentSession _s;
    readonly FrameRenderer _renderer;
    readonly WorldSounds? _sounds;
    readonly Timer _timer;
    readonly object _lock = new();
    Target? _target;
    Vector3d? _where;            // the target, global metres, as last found
    double _distance;            // from the avatar
    DateTime _seenAt, _askedAt, _beepAt;
    (ulong Region, NVector3 At, DateTime When)? _found;   // the grid's last FindAgent answer

    /// <summary>The column is drawn (the viewer's RenderTrackerBeacon); the beep plays (Firestorm's).</summary>
    public volatile bool ShowBeacon = true, Beep;

    /// <summary>What's tracked changed, or tracking ended by itself (arrived, lost).</summary>
    public event Action? Changed;

    public Tracker(AgentSession s, FrameRenderer renderer, WorldSounds? sounds)
    {
        _s = s;
        _renderer = renderer;
        _sounds = sounds;
        Shared = this;
        _timer = new Timer(_ => Tick(), null, 500, 100);
    }

    GridClient? _hooked;

    FriendInfo? Friend(UUID id) => _s.Client?.Friends.FriendList.TryGetValue(id, out var f) == true ? f : null;

    public void Dispose()
    {
        _timer.Dispose();
        if (_hooked != null) _hooked.Friends.FriendFoundReply -= OnFound;
    }

    public Target? Current { get { lock (_lock) return _target; } }
    public bool Active => Current != null;
    /// <summary>Where the target was last found (global metres) and how far it is from the avatar; null while unknown.</summary>
    public (Vector3d Global, double Distance)? Where { get { lock (_lock) return _where is { } w ? (w, _distance) : null; } }

    /// <summary>Track a place: its region's handle and a point in it (region metres; z 0 when not known), and the name
    /// its label shows (LLTracker::trackLocation).</summary>
    public void TrackPlace(ulong regionHandle, NVector3 local, string name)
    {
        Set(new Target(Kind.Place, string.IsNullOrWhiteSpace(name) ? "Destination" : name.Trim(), regionHandle, local, UUID.Zero));
    }

    /// <summary>Track a person by their id (LLTracker::trackAvatar); the label shows their name.</summary>
    public void TrackAvatar(UUID id, string? name = null)
    {
        if (id == UUID.Zero) return;
        name ??= _s.Names.DisplayName(id) ?? "";
        Set(new Target(Kind.Avatar, name.Length > 0 ? name : "(someone)", 0, default, id));
    }

    /// <summary>Stop tracking (LLTracker::stopTracking).</summary>
    public void Clear() => Set(null);

    void Set(Target? t)
    {
        lock (_lock)
        {
            _target = t;
            _where = null;
            _found = null;
            _seenAt = DateTime.UtcNow;
            _askedAt = DateTime.MinValue;
        }
        if (t == null) _renderer.Beacons.Track = null;
        Changed?.Invoke();
        Tick();
    }

    void OnFound(object? sender, FriendFoundReplyEventArgs e)
    {
        lock (_lock)
            if (_target is { Kind: Kind.Avatar } t && e.AgentID == t.Avatar && e.RegionHandle != 0)
                _found = (e.RegionHandle, new NVector3(e.Location.X, e.Location.Y, e.Location.Z), DateTime.UtcNow);
    }

    readonly object _stepLock = new();

    void Tick()
    {
        if (!Monitor.TryEnter(_stepLock)) return;   // (the timer's and a change's, one at a time)
        try { Step(); }
        catch (Exception e) { Console.Error.WriteLine($"tracker: {e.Message}"); }
        finally { Monitor.Exit(_stepLock); }
    }

    void Step()
    {
        Target? t;
        lock (_lock) t = _target;
        if (t == null || _s.Client is not { Network.Connected: true } c) { _renderer.Beacons.Track = null; return; }
        if (!ReferenceEquals(_hooked, c))
        {
            if (_hooked != null) _hooked.Friends.FriendFoundReply -= OnFound;
            (_hooked = c).Friends.FriendFoundReply += OnFound;
        }
        var now = DateTime.UtcNow;
        Vector3d? where = t.Kind == Kind.Place ? PlaceAt(t) : AvatarAt(c, t, now);
        var me = c.Self.GlobalPosition;
        string? ended = null;
        if (where is { } w)
        {
            var d = w - me;
            // (z counted at half for a place, not at all for a person: getDistanceToDestination's attenuation)
            double flat = Math.Sqrt(d.X * d.X + d.Y * d.Y + (t.Kind == Kind.Place ? 0.25 * d.Z * d.Z : 0));
            if (flat < Reached) ended = "arrived";
            lock (_lock) { _where = w; _distance = (w - me).Length(); _seenAt = now; }
        }
        else if ((now - _seenAt).TotalSeconds > LostSeconds) ended = "lost";
        if (t.Kind == Kind.Avatar && ended == null && Friend(t.Avatar) is { IsOnline: false }) ended = "offline";
        if (ended != null)
        {
            lock (_lock) if (ReferenceEquals(_target, t)) { _target = null; _where = null; }
            _renderer.Beacons.Track = null;
            Console.WriteLine($"tracking {t.Name} ended ({ended})");
            Changed?.Invoke();
            return;
        }
        _renderer.Beacons.Track = ShowBeacon && _where is { } shown ? new Sojourn.Render.World.Beacons.TrackMark(shown) : null;
        if (Beep && _where != null && _sounds != null && (now - _beepAt).TotalSeconds > _distance / 30)
        {
            _beepAt = now;
            _sounds.Trigger(BeepSound, c.Self.AgentID, 1, WorldSounds.Kind.Ui, null);
        }
    }

    Vector3d PlaceAt(Target t)
    {
        Utils.LongToUInts(t.Region, out uint ox, out uint oy);
        float z = t.Local.Z;
        // kept above the ground once its region's terrain is known (LLTracker::drawHUDArrow)
        if (_s.World.Region(_s.GridKey, t.Region)?.TerrainHeight(t.Local.X, t.Local.Y) is { } ground) z = MathF.Max(z, ground + 1.5f);
        return new Vector3d(ox + t.Local.X, oy + t.Local.Y, z);
    }

    Vector3d? AvatarAt(GridClient c, Target t, DateTime now)
    {
        foreach (var w in _s.VisibleRegions())
        {
            Utils.LongToUInts(w.Handle, out uint ox, out uint oy);
            if (w.AvatarById(t.Avatar) is { } av)
            {
                var p = w.RegionPosition(av);
                return new Vector3d(ox + p.X, oy + p.Y, p.Z);
            }
            if (w.Coarse.TryGetValue(t.Avatar, out var cp)) return new Vector3d(ox + cp.X, oy + cp.Y, cp.Z);
        }
        // a friend who lets you see them on the map: the grid says where (asked every few seconds)
        var friend = Friend(t.Avatar);
        if (friend is { IsOnline: true, CanSeeThemOnMap: true } && (now - _askedAt).TotalSeconds > FindEvery)
        {
            _askedAt = now;
            c.Friends.MapFriend(t.Avatar);
        }
        lock (_lock)
            if (_found is { } f && (now - f.When).TotalSeconds < FindEvery * 3)
            {
                Utils.LongToUInts(f.Region, out uint fx, out uint fy);
                return new Vector3d(fx + f.At.X, fy + f.At.Y, f.At.Z);
            }
        return null;
    }

    // --- the label (SocialView.Marks) ------------------------------------------------------------------------------------

    FloatingText.Text? _shown;

    /// <summary>The label for this frame, in the eye's region frame: "name / 123 m", white, fading in from 3 to 6 m away;
    /// placed toward the target no further than 5 m along the line of sight, so it's as big at any distance (LLHUDText
    /// keeps a constant size on screen; floating text shrinks with distance). Until a new picture is in the labels'
    /// atlas the last one stays.</summary>
    public IReadOnlyList<FloatingText.Want> Label(ulong region, NVector3 eye, NameTags tags)
    {
        Target? t;
        Vector3d? where;
        double dist;
        lock (_lock) (t, where, dist) = (_target, _where, _distance);
        if (t == null || where is not { } w || !ShowBeacon) { _shown = null; return []; }
        Utils.LongToUInts(region, out uint ox, out uint oy);
        var at = new NVector3((float)(w.X - ox), (float)(w.Y - oy), (float)w.Z);
        var to = at - eye;
        float d = to.Length();
        if (d < 0.05f) return [];
        if (d > LabelAt) at = eye + to / d * LabelAt;
        float alpha = Math.Clamp(((float)dist - 3) / 3, 0.2f, 1f);
        var text = new FloatingText.Text($"{t.Name}\n{dist:0} m", 255, 255, 255);
        var list = new List<FloatingText.Want>();
        if (tags.Holds(text) || _shown == null) _shown = text;
        list.Add(new FloatingText.Want(_shown, at, MathF.Min(d, LabelAt), alpha, Over: true));
        if (!_shown.Equals(text)) list.Add(new FloatingText.Want(text, at, MathF.Min(d, LabelAt), 0, Over: true));
        return list;
    }
}
