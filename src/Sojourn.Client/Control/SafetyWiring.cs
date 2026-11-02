using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Client.Assistant;
using Sojourn.Core.Agents;
using Sojourn.Render.Particles;

namespace Sojourn.Client.Control;

/// <summary>
/// Safety and comfort's hookup to the client (batch B1), kept out of Program.cs: Core's Safety and MoveLock set from
/// the person's settings, world audio's and the particles' filters, the rig's hold, the SojournSafety ops, the
/// settings on SojournSettings and the menu's safety section (the vignette, keep me in place, aim, particles,
/// antispam; release keys and showing what's derendered when there's something to do).
/// </summary>
public sealed class SafetyWiring
{
    readonly AgentSession _s;
    readonly VrSettings _st;
    readonly bool _headset;
    readonly ILogger _log;
    SettingsMenu? _menu;
    volatile HashSet<UUID> _muted = new();
    readonly Timer _mutedTimer;

    public Comfort Comfort { get; }
    public Aim? Aim { get; private set; }

    SafetyWiring(AgentSession s, VrSettings st, bool headset, ILogger log)
    {
        _s = s;
        _st = st;
        _headset = headset;
        _log = log;
        Comfort = new Comfort(s, st, m => log.LogInformation("{Msg}", m));
        // the block list as a set the render thread can read without asking (a muted owner's particles don't draw)
        _mutedTimer = new Timer(_ => RefreshMuted(), null, 0, 1000);
    }

    public static SafetyWiring Start(AgentSession session, VrSettings st, bool headset, AudioWiring? audio, VrPilot pilot,
        Sojourn.Render.Realtime.FrameRenderer renderer, ILogger log)
    {
        var w = new SafetyWiring(session, st, headset, log);
        w.Aim = headset ? new Aim(session, renderer) : null;
        session.MoveLock.RigReturns = headset;
        session.MoveLock.ViewPlace = () => pilot.HeadInRegion is { } h ? (h.Region, new LibreMetaverse.Vector3(h.Pos.X, h.Pos.Y, h.Pos.Z)) : null;
        pilot.HoldRoom = () => session.MoveLock.Holding;
        if (audio != null) audio.Sounds.Refuse = w.RefuseSound;
        ParticleBuilder.Hidden = w.ParticlesHidden;
        var api = new SafetyApi(session, () => w._menu?.Changed(), audio != null ? audio.Sounds.Silence : null,
            audio != null ? audio.Sounds.PlayingAsync : null);
        if (headset) api.Headset = () => new JsonObject { ["aiming"] = pilot.Aiming, ["vignette"] = MathF.Round(w.Comfort.Level, 2) };
        session.Api.Register(api);
        w.Apply();
        return w;
    }

    void Apply()
    {
        _s.Safety.SpamOn = _st.Antispam;
        _s.Safety.Notices = _st.BumpNotices switch { "off" => Safety.BumpNotices.Off, "all" => Safety.BumpNotices.All, _ => Safety.BumpNotices.Pushes };
        _s.MoveLock.On = _st.KeepInPlace;
        ParticleBuilder.Off = !_st.Particles;
        _menu?.Changed();
    }

    void RefreshMuted()
    {
        try
        {
            var set = new HashSet<UUID>();
            foreach (var n in _s.Mutes.List())
                if (n is JsonObject o && UUID.TryParse(o["id"]?.GetValue<string>() ?? "", out var id) && id != UUID.Zero) set.Add(id);
            if (!set.SetEquals(_muted)) _muted = set;
        }
        catch (Exception e) { _log.LogDebug(e, "the block list for particles"); }
    }

    /// <summary>A particle source left out (the render thread): its owner or its object is blocked.</summary>
    bool ParticlesHidden(UUID owner, UUID obj)
    {
        var m = _muted;
        return m.Count > 0 && (m.Contains(owner) || m.Contains(obj));
    }

    /// <summary>World audio asks before a sound plays (its thread): blacklisted, or its object or owner flooding.</summary>
    bool RefuseSound(UUID sound, UUID obj, UUID owner, WorldSounds.Arrival how)
    {
        var found = _s.Objects.FindAnywhere(obj);
        bool worn = found is { } f && _s.Objects.IsAttachment(f.World, f.Prim, out _, out _);
        if (_s.Safety.SoundBlocked(sound, obj, owner, worn)) return true;
        if (how == WorldSounds.Arrival.Update) return false;
        bool mine = owner == _s.AgentId;
        var q = how == WorldSounds.Arrival.Preload ? Safety.Queue.SoundPreload : Safety.Queue.Sound;
        string? objName = found?.Prim.Properties?.Name;
        if (_s.Safety.Spam(q, obj, objName, mine) || _s.Safety.Spam(q, owner, _s.Names.DisplayName(owner), mine)) return true;
        if (how == WorldSounds.Arrival.Message && found is { } h)
            _s.Safety.SoundHeard(sound, obj, owner, obj == owner ? "gesture" : worn ? "worn" : "rezzed", h.World.Handle, h.World.RegionPosition(h.Prim));
        else if (how == WorldSounds.Arrival.Message && obj == owner)
            _s.Safety.SoundHeard(sound, obj, owner, "gesture", _s.Region?.Handle ?? 0, default);
        return false;
    }

    static readonly string[] VignetteNames = { "off", "forced", "all" };
    static readonly string[] AimNames = { "auto", "on", "off" };
    static readonly string[] AimWith = { "head", "hand" };
    static readonly string[] BumpNames = { "off", "pushes", "all" };

    const ScriptControlChange Keys = ScriptControlChange.Forward | ScriptControlChange.Back | ScriptControlChange.Left |
        ScriptControlChange.Right | ScriptControlChange.RotateLeft | ScriptControlChange.RotateRight | ScriptControlChange.Up |
        ScriptControlChange.Down | ScriptControlChange.LeftButton | ScriptControlChange.MouseLookLeftButton;

    /// <summary>The settings (SojournSettings) and the menu's safety section.</summary>
    public void Settle(SettingsMenu menu, SettingsApi api)
    {
        _menu = menu;
        menu.SafetyRows = () =>
        {
            var rows = new List<(MenuRow Row, Action<int> Act)>();
            if (_headset)
            {
                rows.Add((MenuRow.Choice("comfort vignette", VignetteNames, (int)_st.ComfortVignette), i => { _st.ComfortVignette = (Vignette)i; Apply(); }));
                if (_st.ComfortVignette != Vignette.Off)
                    rows.Add((new MenuRow("vignette strength", $"{_st.VignetteStrength * 100:0}%", true),
                        d => { _st.VignetteStrength = Math.Clamp(MathF.Round((_st.VignetteStrength + d * 0.1f) * 10) / 10, 0.2f, 1f); Apply(); }));
            }
            rows.Add((MenuRow.Switch("keep me in place", _st.KeepInPlace), _ => { _st.KeepInPlace = !_st.KeepInPlace; Apply(); }));
            if (_headset)
            {
                rows.Add((MenuRow.Choice("aim", AimNames, (int)_st.Aim), i => { _st.Aim = (AimMode)i; Apply(); }));
                if (_st.Aim != AimMode.Off)
                    rows.Add((MenuRow.Choice("aim with", AimWith, _st.AimWithHand ? 1 : 0), i => { _st.AimWithHand = i == 1; Apply(); }));
            }
            rows.Add((MenuRow.Switch("particles", _st.Particles), _ => { _st.Particles = !_st.Particles; Apply(); }));
            rows.Add((MenuRow.Switch("antispam", _st.Antispam), _ => { _st.Antispam = !_st.Antispam; Apply(); }));
            if (Sojourn.Session.Rlv.RlvWiring.Current is { } rlv) rows.AddRange(rlv.Rows().Select(r => (r.Row, (Action<int>)(i => { r.Act(i); Apply(); }))));
            // the things to do, only when there's something to do them to
            if ((_s.ControlsTaken & Keys) != 0)
                rows.Add((new MenuRow("controls a script took", "release", false), _ => { _s.Safety.ReleaseControls(); _log.LogInformation("released the controls scripts took"); }));
            if (_s.Safety.Derendered > 0)
                rows.Add((new MenuRow($"{_s.Safety.Derendered} hidden", "show again", false), _ => { _s.Safety.RerenderAll(); Apply(); }));
            return rows;
        };

        api.Add("comfort_vignette", new("safety", "in the headset, the view's edges darken while it moves: off (the default), forced (when something else "
            + "moves you: a push, a seat or vehicle, a moving floor) or all (the stick's walking and smooth turns too)", "off, forced, all",
            () => VignetteNames[(int)_st.ComfortVignette], SettingsApi.Choice("comfort_vignette", VignetteNames, i => { _st.ComfortVignette = (Vignette)i; Apply(); })));
        api.Add("vignette_strength", new("safety", "how far the comfort vignette closes in at its strongest (1: to 30 degrees from the middle)", "0.2-1",
            () => JsonValue.Create(MathF.Round(_st.VignetteStrength, 2)), SettingsApi.Number("vignette_strength", 0.2f, 1, "", f => { _st.VignetteStrength = f; Apply(); })));
        api.Add("keep_in_place", new("safety", "keep me in place (Firestorm's movelock): pushed or moved off by something else, the avatar is brought "
            + "back where it stood (walked back, else teleported within the region); in the headset the view stays put meanwhile", "on, off",
            () => _st.KeepInPlace ? "on" : "off", SettingsApi.Switch("keep_in_place", on => { _st.KeepInPlace = on; Apply(); })));
        api.Add("antispam", new("safety", "Firestorm's antispam (off by default, as there): an object or person sending over 10 chat lines, messages or offers, or menus in 4 "
            + "seconds (sounds 60), or a message of over 70 lines, is held back until the person leaves the region (SojournSafety spam lists them)",
            "on, off", () => _st.Antispam ? "on" : "off", SettingsApi.Switch("antispam", on => { _st.Antispam = on; Apply(); })));
        api.Add("particles", new("safety", "particles drawn at all (the viewer's Rendering Types > Particles); a blocked owner's never are", "on, off",
            () => _st.Particles ? "on" : "off", SettingsApi.Switch("particles", on => { _st.Particles = on; Apply(); })));
        api.Add("aim", new("safety", "in the headset, mouselook for weapons and games: auto (while a script holds the mouselook button), on "
            + "(always; touching waits, as in the viewer's mouselook) or off; aiming, the right trigger is the mouselook button", "auto, on, off",
            () => AimNames[(int)_st.Aim], SettingsApi.Choice("aim", AimNames, i => { _st.Aim = (AimMode)i; Apply(); })));
        api.Add("aim_with", new("safety", "what aims while aiming: the head (where you look, as mouselook's camera) or the right hand's ray", "head, hand",
            () => _st.AimWithHand ? "hand" : "head", SettingsApi.Choice("aim_with", AimWith, i => { _st.AimWithHand = i == 1; Apply(); })));
        api.Add("bump_notices", new("safety", "which bumps, pushes and hits the feed tells (SojournSafety bumps lists them all): pushes (pushes "
            + "and hits, the default), all (bumps too) or off", "off, pushes, all",
            () => _st.BumpNotices, SettingsApi.Choice("bump_notices", BumpNames, i => { _st.BumpNotices = BumpNames[i]; Apply(); })));
    }
}
