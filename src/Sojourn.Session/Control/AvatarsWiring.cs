using System.Text.Json;
using System.Text.Json.Nodes;
using LibreMetaverse;
using Microsoft.Extensions.Logging;
using Sojourn.Core.Agents;
using Sojourn.Render.World;

namespace Sojourn.Session.Control;

/// <summary>The avatar settings batch B3b added (avatar-settings.json beside the headset's settings).</summary>
public sealed class AvatarSettings
{
    /// <summary>The viewer's LipSyncEnabled ("Move avatar lips when speaking"), on by default as there.</summary>
    public bool LipSync { get; set; } = true;
    /// <summary>The viewer's ShowSelectionBeam: others' point-at and edit beams drawn, on by default as there.</summary>
    public bool SelectionBeams { get; set; } = true;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AvatarSettings Load(string path, Action<string> warn)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AvatarSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception e) { warn($"avatar settings: {e.Message}; starting from the defaults"); return new(); }
    }

    public void Save(string path, Action<string> warn)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, Json)); }
        catch (Exception e) { warn($"avatar settings: {e.Message}"); }
    }
}

/// <summary>
/// Avatars' hookup to the client (batch B3b), kept out of Program.cs: others' viewer effects received (beams, PointAt,
/// ResetSkeleton: World/Avatars.Effects.cs), voice's levels for lip sync (World/Avatars.LipSync.cs), and the settings
/// lip_sync and selection_beams on SojournSettings. Sojourn sends no PointAt and no beams of its own (the user's call,
/// open on the board): only the ResetSkeleton effect of the person's own reset (AvatarsApi.Fix.cs).
/// </summary>
public sealed class AvatarsWiring
{
    readonly AgentSession _s;
    readonly ILogger _log;
    readonly string _path;
    readonly AvatarSettings _st;
    readonly Voice.VoiceClient? _voice;
    volatile HashSet<UUID> _muted = new();
    readonly Timer _mutedTimer;

    // voice's peers as a table, made again when voice publishes a new list (the render thread asks per avatar per frame)
    IReadOnlyList<Voice.VoicePeer>? _peersRef;
    Dictionary<UUID, Voice.VoicePeer> _peers = new();

    AvatarsWiring(AgentSession s, Voice.VoiceClient? voice, string vrSettingsPath, ILogger log)
    {
        _s = s;
        _voice = voice;
        _log = log;
        _path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(vrSettingsPath)) ?? ".", "avatar-settings.json");
        _st = AvatarSettings.Load(_path, m => log.LogWarning("{Msg}", m));
        // the block list, for lip sync: a blocked person's lips don't move (idleUpdateVoiceVisualizer's isInMuteList)
        _mutedTimer = new Timer(_ => RefreshMuted(), null, 0, 1000);
    }

    public static AvatarsWiring Start(AgentSession session, Voice.VoiceClient? voice, SettingsApi api, string vrSettingsPath, ILogger log)
    {
        var w = new AvatarsWiring(session, voice, vrSettingsPath, log);
        var hooked = new System.Runtime.CompilerServices.ConditionalWeakTable<GridClient, object>();
        void Hook(GridClient c)
        {
            if (hooked.TryGetValue(c, out _)) return;
            hooked.Add(c, new object());
            ViewerEffects.Shared.Attach(c);
        }
        lock (AgentSession.ClientHooks) AgentSession.ClientHooks.Add(Hook);
        if (session.Client is { } now) Hook(now);
        if (voice is { Available: true }) VoiceLevels.Shared.Level = w.Level;
        // tests (tools/avatars.py): SOJOURN_TEST_LIPSYNC=1, everyone else speaks, their power rising and falling
        if (Environment.GetEnvironmentVariable("SOJOURN_TEST_LIPSYNC") == "1")
            VoiceLevels.Shared.Level = id => id == session.Client?.Self.AgentID ? null
                : 0.55f + 0.35f * MathF.Sin(Environment.TickCount64 / 1000f * 2.3f);
        w.Apply();
        api.Add("lip_sync", new("view", "speakers' mouths move with their voice (the viewer's \"Move avatar lips when speaking\"; the system "
            + "head's lip morphs, so a mesh head's own animations decide its mouth)", "on, off",
            () => JsonValue.Create(w._st.LipSync), SettingsApi.Switch("lip_sync", on => { w._st.LipSync = on; w.Changed(); })));
        api.Add("selection_beams", new("view", "other people's point-at and edit beams, from their hand to what they select or build (the "
            + "viewer's ShowSelectionBeam); Sojourn sends none of its own", "on, off",
            () => JsonValue.Create(w._st.SelectionBeams), SettingsApi.Switch("selection_beams", on => { w._st.SelectionBeams = on; w.Changed(); })));
        return w;
    }

    void Changed()
    {
        Apply();
        _st.Save(_path, m => _log.LogWarning("{Msg}", m));
    }

    void Apply()
    {
        VoiceLevels.Shared.LipSync = _st.LipSync;
        ViewerEffects.Shared.ShowBeams = _st.SelectionBeams;
    }

    /// <summary>For lip sync (the render thread): someone's speaking amplitude as voice reports it (the server's power, as
    /// the viewer's getCurrentPower), or the person's own from the mic while it goes out to residents (as the viewer's
    /// updateOwnVolume; on the scale the mic token's ring uses, speaking over 0.3); null when not speaking or blocked.</summary>
    float? Level(UUID id)
    {
        var v = _voice;
        if (v == null) return null;
        var self = _s.Client?.Self.AgentID ?? UUID.Zero;
        if (id == self && id != UUID.Zero)
        {
            if (!v.MicOpen || v.OffHere || v.ModeratorMuted || (v.Mode == Voice.VoiceMode.OpenMic && v.Muted)) return null;
            float db = 20 * MathF.Log10(Math.Max(Voice.VoiceNative.sv_mic_level(), 1e-6f));
            float level = Math.Clamp((db + 50) / 35, 0, 1);
            return level > 0.3f ? level : null;
        }
        if (_muted.Contains(id)) return null;
        var peers = v.Peers;
        if (!ReferenceEquals(peers, _peersRef))
        {
            var map = new Dictionary<UUID, Voice.VoicePeer>(peers.Count);
            foreach (var p in peers) map[p.Id] = p;
            (_peers, _peersRef) = (map, peers);
        }
        return _peers.TryGetValue(id, out var peer) && peer.Speaking ? peer.Power : null;
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
        catch (Exception e) { _log.LogDebug(e, "the block list for lip sync"); }
    }
}
