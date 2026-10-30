using System.Collections.Concurrent;
using LibreMetaverse;
using Sojourn.Core.Agents;

namespace Sojourn.Session.Audio;

/// <summary>
/// The sounds the viewer makes itself rather than the world sending them. UI sounds (centred, at the UI level):
/// a new conversation (an IM or group chat session you didn't have; PlaySoundNewConversation, on by default), a voice
/// call ringing (UISndStartIM), teleporting out, a region restart warning, the L$ balance moving by more than L$50.
/// The others' sounds where they are (llvoavatar.cpp): someone starting to type (UISndTyping, at their body),
/// footsteps while an avatar walks or runs and on landing (gain 0.1 at the ambient level, at the feet; on the ground
/// the land step, on a build the wood one, prims' default material, as there's no step raycast here).
/// The sound assets are the grid's (OpenSim ships the same ids).
/// </summary>
public sealed class Cues
{
    // the viewer's settings.xml defaults
    static readonly UUID NewIm = new("67cc2844-00f3-2b3c-b991-6418d01e1bb7");          // UISndNewIncomingIMSession
    static readonly UUID StartIm = new("c825dfbc-9827-7e02-6507-3713d18916c1");        // UISndStartIM
    static readonly UUID TeleportOut = new("d7a9a565-a013-2a69-797d-5332baa1a947");    // UISndTeleportOut
    static readonly UUID Restart = new("b92a0f64-7709-8811-40c5-16afd624a45f");        // UISndRestart
    static readonly UUID MoneyUp = new("77a018af-098e-c037-51a6-178f05877c6f");        // UISndMoneyChangeUp
    static readonly UUID MoneyDown = new("104974e3-dfda-428b-99ee-b0d4e748d3a3");      // UISndMoneyChangeDown
    static readonly UUID Typing = new("5e191c7b-8996-9ced-a177-b2ac32bfea06");         // UISndTyping
    static readonly UUID StepOnLand = new("e8af4a28-aa83-4310-a7c4-c047e15ea0df");     // LLVOAvatar::sStepSoundOnLand
    static readonly UUID StepOnWood = new("7d1826f4-24c4-4aac-8c2e-eff45df37783");     // SND_WOOD_RUBBER
    const float StepGain = 0.1f;   // STEP_VOLUME

    static readonly HashSet<UUID> Walks = new()
    {
        Animations.WALK, Animations.FEMALE_WALK, Animations.CROUCHWALK,
        new UUID("33339176-7ddc-9397-94a4-bf3403cbc8f5"), new UUID("d60c41d2-7c24-7074-d3fa-6101cea22a51"),   // walk_new, female_walk_new
    };
    static readonly HashSet<UUID> Runs = new()
    {
        Animations.RUN,
        new UUID("1ab1b236-cd08-21e6-0cbc-0d923fc6eca2"), new UUID("85995026-eade-5d78-d364-94a64512cb66"),   // run_new, female_run_new
    };
    static readonly HashSet<UUID> Lands = new() { Animations.LAND, Animations.MEDIUM_LAND };

    readonly AgentSession _s;
    readonly WorldSounds _w;
    readonly Func<Vector3d, bool> _canHear;
    readonly HashSet<UUID> _sessions = new();
    int? _balance;
    bool _teleporting;

    sealed class Walker
    {
        public bool Walking, Running, Typing;
        public double Phase;
    }
    readonly ConcurrentDictionary<UUID, Walker> _avatars = new();
    readonly ConcurrentQueue<UUID> _landed = new();

    public Cues(AgentSession s, WorldSounds w, Func<Vector3d, bool> canHear)
    {
        _s = s;
        _w = w;
        _canHear = canHear;
    }

    /// <summary>The own avatar's footsteps too (the viewer's way); off while an app plays its own (AudioWiring.OwnFootsteps).</summary>
    public bool OwnSteps { get; set; } = true;

    /// <summary>The new-conversation sound (on by default); off while an app plays its own for IMs (AudioWiring.ConversationSound).</summary>
    public bool NewConversation { get; set; } = true;

    void Ui(UUID sound) => _w.Trigger(sound, _s.Client?.Self.AgentID ?? UUID.Zero, 1, WorldSounds.Kind.Ui, null);

    public void Hook(GridClient c)
    {
        c.Self.IM += (_, e) => OnIm(c, e.IM);
        // the viewer plays it as it asks for the teleport; here the first word of one does (a teleport within the
        // region may bring only its finish)
        c.Self.TeleportProgress += (_, e) =>
        {
            bool first = !_teleporting;
            _teleporting = e.Status is TeleportStatus.Start or TeleportStatus.Progress;
            if (first) Ui(TeleportOut);
        };
        c.Self.AlertMessage += (_, e) =>
        {
            var m = e.Message ?? "";
            if (m.StartsWith("RESTART_X_MINUTES") || m.StartsWith("RESTART_X_SECONDS")) Ui(Restart);
        };
        c.Self.MoneyBalance += (_, e) =>
        {
            int now = e.Balance;
            // UISndMoneyChangeThreshold: changes of more than L$50; down when it drops, up when it rises
            if (_balance is { } was && was != 0 && Math.Abs(now - was) > 50) Ui(now < was ? MoneyDown : MoneyUp);
            _balance = now;
        };
        c.Avatars.AvatarAnimation += (_, e) => OnAnimation(c, e);
    }

    /// <summary>A voice call ringing (the viewer's call dialog opening, PlaySoundIncomingVoiceCall).</summary>
    public void Ringing() => Ui(StartIm);

    void OnIm(GridClient c, InstantMessage im)
    {
        if (im.FromAgentID == c.Self.AgentID || string.IsNullOrEmpty(im.Message)) return;
        if (im.Dialog is not (InstantMessageDialog.MessageFromAgent or InstantMessageDialog.SessionSend)) return;
        if (im.Offline == InstantMessageOnline.Offline) return;   // the ones saved while away arrive together at login
        if (_s.Mutes.IsMuted(im.FromAgentID, im.FromAgentName)) return;
        if (_s.PeopleHide(c, im)) return;   // kept away by the person's switches (Agents/Rejects.cs, GroupChat.cs)
        lock (_sessions) if (!_sessions.Add(im.IMSessionID)) return;
        if (NewConversation) Ui(NewIm);
    }

    void OnAnimation(GridClient c, AvatarAnimationEventArgs e)
    {
        var anims = e.Animations.Select(a => a.AnimationID).ToHashSet();
        var w = _avatars.GetOrAdd(e.AvatarID, _ => new Walker());
        bool typing = anims.Contains(Animations.TYPE);
        if (typing && !w.Typing && AvatarAt(e.AvatarID) is { } at && _canHear(at) && !_s.Mutes.IsMuted(e.AvatarID))
            _w.Trigger(Typing, e.AvatarID, 1, WorldSounds.Kind.Sfx, at);
        w.Typing = typing;
        bool flying = anims.Contains(Animations.FLY) || anims.Contains(Animations.HOVER) || anims.Contains(Animations.FLYSLOW);
        bool wasMoving = w.Walking || w.Running;
        w.Running = !flying && anims.Overlaps(Runs);
        w.Walking = !flying && !w.Running && anims.Overlaps(Walks);
        if ((w.Walking || w.Running) && !wasMoving) w.Phase = 0.5;   // the first footfall half a step in
        if (anims.Overlaps(Lands)) _landed.Enqueue(e.AvatarID);
    }

    /// <summary>On the audio thread, every tick: footfalls.</summary>
    public void Tick(double dt)
    {
        while (_landed.TryDequeue(out var id)) Step(id);
        foreach (var (id, w) in _avatars)
        {
            if (!w.Walking && !w.Running) continue;
            if (Avatar(id) is not { } av) continue;
            float speed = MathF.Sqrt(av.Velocity.X * av.Velocity.X + av.Velocity.Y * av.Velocity.Y);
            if (speed < 0.4f) continue;
            // a footfall every half a stride: about 0.5 s walking at 3.2 m/s, 0.33 s running at 5.1 m/s
            double period = w.Running ? 0.33 * Math.Clamp(5.1 / speed, 0.7, 1.5) : 0.5 * Math.Clamp(3.2 / speed, 0.7, 1.8);
            w.Phase += dt / period;
            if (w.Phase < 1) continue;
            w.Phase -= Math.Floor(w.Phase);
            Step(id);
        }
    }

    void Step(UUID id)
    {
        if (_s.Mutes.IsMuted(id)) return;
        if (!OwnSteps && id == _s.Client?.Self.AgentID) return;
        foreach (var r in _s.VisibleRegions())
        {
            if (r.AvatarById(id) is not { } av) continue;
            var local = r.RegionPosition(av);
            var feet = new Vector3(local.X, local.Y, local.Z - 0.9f);
            var at = WorldSounds.Global(r.Handle, feet);
            if ((at - _w.Ear).Length() > 20 || !_canHear(at)) return;   // at gain 0.1, steps past 20 m are below -50 dB
            bool onLand = av.ParentID == 0 && r.TerrainHeight(local.X, local.Y) is { } ground && feet.Z - ground < 0.4f;
            _w.Trigger(onLand ? StepOnLand : StepOnWood, id, StepGain, WorldSounds.Kind.Ambient, at);
            return;
        }
    }

    Avatar? Avatar(UUID id)
    {
        foreach (var r in _s.VisibleRegions()) if (r.AvatarById(id) is { } av) return av;
        return null;
    }

    Vector3d? AvatarAt(UUID id)
    {
        foreach (var r in _s.VisibleRegions())
            if (r.AvatarById(id) is { } av) return WorldSounds.Global(r.Handle, r.RegionPosition(av));
        return null;
    }
}
