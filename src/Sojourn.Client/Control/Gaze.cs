using LibreMetaverse;
using LibreMetaverse.Packets;
using Sojourn.Core.Agents;
using Sojourn.Render.Frame;
using Vector3 = System.Numerics.Vector3;

namespace Sojourn.Client.Control;

/// <summary>
/// The person's gaze shown to others as the viewer's look-at (a ViewerEffect of type LookAt, llhudeffectlookat.cpp),
/// so every viewer turns their avatar's head, neck, a little of the torso and the eyes toward it (LLHeadRotMotion).
/// What's sent is what the official viewer sends: in mouselook (the headset's view, the desktop's first person) the
/// view's direction as an offset from your own avatar (LOOKAT_TARGET_MOUSELOOK, no timeout), and when your view rests
/// on a person, that person (LOOKAT_TARGET_FOCUS, as when the viewer's camera is focused on someone): every viewer then
/// aims the head at theirs, so it reads as eye contact even where your avatar's head isn't quite where your eyes are.
/// At most four sends a second, and only when the direction moves more than 0.05 (about 3 degrees) or the target
/// changes, as the viewer; one effect id a login. Claude Opus 5.5, 2026-10-26 (the user chose head direction with a
/// toggle, docs/shared-tracking.md).
/// </summary>
public sealed class Gaze
{
    // llhudeffectlookat.cpp: MAX_SENDS_PER_SEC, MIN_DELTAPOS_FOR_UPDATE_SQUARED, MAX_TIMEOUT
    const double SendEvery = 0.25;
    const float MinMoveSq = 0.05f * 0.05f;
    const float Forever = float.MaxValue / 2;
    // ELookAtType
    const byte None = 0, Focus = 8, Mouselook = 9;
    // eye contact: someone's head within Acquire degrees of the middle of the view for Dwell seconds, held until it's
    // more than Release degrees off for Leave seconds; only within chat range
    const float AcquireDeg = 10, ReleaseDeg = 18, Range = 20;
    const double Dwell = 0.3, Leave = 0.25;
    // the view this far from the avatar (m, across the ground) is looking around, not the avatar's own eyes (the rig
    // lets them drift up to about 1.5 m while walking)
    const float AsideBeyond = 3;
    // the direction's smoothing (s): a quick glance or a jolt of the headset doesn't become a send
    const float Smoothing = 0.08f;

    readonly AgentSession _s;
    readonly VrSettings _settings;
    readonly bool _log = Environment.GetEnvironmentVariable("SOJOURN_GAZE_LOG") == "1";

    GridClient? _client;
    UUID _effect;
    bool _active;             // the last send was a look-at (not None)
    byte _sentType;
    UUID _sentTarget;
    Vector3 _sentOffset;
    double _sentAt = -1e9;
    Vector3 _dir;
    UUID _candidate, _contact;
    double _candidateSince, _awaySince = -1;

    /// <summary>Messages for the console (each send, with SOJOURN_GAZE_LOG=1).</summary>
    public Action<string>? Say { get; set; }
    public int Sent { get; private set; }

    public Gaze(AgentSession session, VrSettings settings)
    {
        _s = session;
        _settings = settings;
    }

    /// <summary>The renderer should report where heads are (FrameRequest.ShowNames): eye contact looks for them.</summary>
    public bool WantsHeads => _settings.Gaze == GazeSharing.EyeContact;

    /// <summary>A frame with the view at the avatar's eyes (the headset, the desktop's first person). eye and forward in
    /// the region's frame; heads, where the renderer drew them last frame. A view well away from the avatar (looking
    /// around) shows only its direction: from there, whom it looks at isn't whom the avatar would.</summary>
    public void Frame(ulong region, Vector3 eye, Vector3 forward, (ulong Region, NameTag[] Tags) heads, double now, float dt)
    {
        if (Client() is not { } c) return;
        var me = c.Self.SimPosition;
        bool aside = new System.Numerics.Vector2(eye.X - me.X, eye.Y - me.Y).Length() > AsideBeyond;
        bool contactOn = _settings.Gaze == GazeSharing.EyeContact && !aside && heads.Region == region && region != 0;
        if (!contactOn) _contact = _candidate = UUID.Zero;   // at once: nobody is named once it's turned off
        if (_settings.Gaze == GazeSharing.Off) { Stop(c, now); return; }
        if (forward.LengthSquared() < 1e-8f) return;
        var f = Vector3.Normalize(forward);
        _dir = _dir == Vector3.Zero ? f : Vector3.Normalize(Vector3.Lerp(_dir, f, 1 - MathF.Exp(-dt / Smoothing)));
        if (contactOn) UpdateContact(heads.Tags, eye, f, c.Self.AgentID, now);

        if (_contact != UUID.Zero) Send(c, Focus, _contact, Vector3.Zero, now);
        else Send(c, Mouselook, c.Self.AgentID, _dir, now);
    }

    /// <summary>A frame while spectating (the headset's Y held): the view isn't the avatar's head, so others keep seeing
    /// the head where it was when spectating began. Eye contact becomes the direction it was in (the person may walk
    /// away; the head stays put); off still clears. The user's call, 2026-10-26.</summary>
    public void Hold(double now)
    {
        if (Client() is not { } c) return;
        _contact = _candidate = UUID.Zero;
        if (_settings.Gaze == GazeSharing.Off) { Stop(c, now); return; }
        if (_active && _sentType == Focus && _dir != Vector3.Zero) Send(c, Mouselook, c.Self.AgentID, _dir, now);
    }

    /// <summary>A frame without the view at the avatar's eyes (the desktop's third person): nothing is shown, as the
    /// viewer clears its look-at leaving mouselook.</summary>
    public void Idle(double now)
    {
        if (Client() is { } c) Stop(c, now);
        _contact = _candidate = UUID.Zero;
    }

    GridClient? Client()
    {
        var c = _s.Client;
        if (c is null || !c.Network.Connected) return null;
        if (!ReferenceEquals(c, _client))
        {
            // a new login: a new effect, nothing sent on it yet
            _client = c;
            _effect = UUID.Random();
            _active = false;
            _sentType = None;
            _sentTarget = UUID.Zero;
            _sentAt = -1e9;
            _contact = _candidate = UUID.Zero;
        }
        return c;
    }

    void UpdateContact(NameTag[] tags, Vector3 eye, Vector3 f, UUID self, double now)
    {
        UUID best = UUID.Zero;
        float bestCos = MathF.Cos(AcquireDeg * MathF.PI / 180);
        float holdCos = MathF.Cos(ReleaseDeg * MathF.PI / 180);
        bool held = false;
        foreach (var t in tags)
        {
            if (t.Id == UUID.Zero || t.Id == self) continue;
            var d = t.Head - eye;
            float len = d.Length();
            if (len < 0.2f || len > Range) continue;
            float cos = Vector3.Dot(d / len, f);
            if (t.Id == _contact && cos >= holdCos) held = true;
            if (cos > bestCos) { bestCos = cos; best = t.Id; }
        }
        if (_contact != UUID.Zero)
        {
            if (held) { _awaySince = -1; return; }
            if (_awaySince < 0) _awaySince = now;
            if (now - _awaySince < Leave) return;
            _contact = UUID.Zero;
            _awaySince = -1;
        }
        if (best == UUID.Zero) { _candidate = UUID.Zero; return; }
        if (best != _candidate) { _candidate = best; _candidateSince = now; return; }
        if (now - _candidateSince >= Dwell) { _contact = best; _awaySince = -1; }
    }

    void Stop(GridClient c, double now)
    {
        if (!_active) return;
        // the viewer's LOOKAT_TARGET_CLEAR goes out as type None, no target, no offset, no duration; it's sent at once,
        // but never more often than the rate
        if (now - _sentAt < SendEvery) return;
        Put(c, None, UUID.Zero, Vector3.Zero, 0, now);
        _active = false;
    }

    void Send(GridClient c, byte type, UUID target, Vector3 offset, double now)
    {
        bool changed = !_active || type != _sentType || target != _sentTarget || Vector3.DistanceSquared(offset, _sentOffset) > MinMoveSq;
        if (!changed || now - _sentAt < SendEvery) return;
        Put(c, type, target, offset, Forever, now);
        _active = true;
    }

    void Put(GridClient c, byte type, UUID target, Vector3 offset, float duration, double now)
    {
        // TypeData as LLHUDEffectLookAt::packData: source avatar, target object, the offset (a Vector3d), the type
        var data = new byte[57];
        c.Self.AgentID.ToBytes(data, 0);
        target.ToBytes(data, 16);
        new Vector3d(offset.X, offset.Y, offset.Z).ToBytes(data, 32);
        data[56] = type;
        var p = new ViewerEffectPacket
        {
            AgentData = { AgentID = c.Self.AgentID, SessionID = c.Self.SessionID },
            Effect =
            [
                new ViewerEffectPacket.EffectBlock
                {
                    ID = _effect, AgentID = c.Self.AgentID, Type = (byte)EffectType.LookAt, Duration = duration,
                    Color = [0, 0, 0, 255],   // LLHUDEffect's mColor, LLColor4U's default
                    TypeData = data,
                },
            ],
        };
        c.Network.SendPacket(p);
        _sentType = type;
        _sentTarget = target;
        _sentOffset = offset;
        _sentAt = now;
        Sent++;
        if (_log) Say?.Invoke($"gaze: {(type == None ? "none" : type == Focus ? $"focus {target}" : $"mouselook {offset.X:F2},{offset.Y:F2},{offset.Z:F2}")}");
    }
}
