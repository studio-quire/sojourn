using Microsoft.Extensions.Logging;
using Sojourn.Core;
using Sojourn.Core.Agents;

namespace Sojourn.Session.Voice;

/// <summary>
/// Voice's hookup to the client's loop, kept out of Program.cs: start-up, the buttons, and what each frame hands
/// over (the listener's pose, whether the assistant's button is held). The app binds its own keys and controllers to
/// the buttons (the VR client's Voice/VoiceInput.cs).
///
/// Buttons. Push-to-talk (the default): hold the left grip in the headset, B or the middle mouse button on the
/// desktop. Open mic: the right stick's click in the headset, B on the desktop, toggles the mute; it starts muted.
/// Holding the assistant's button (right grip, T), or its mic latched open by a tap, keeps the mic from residents either
/// way; push-to-talk pressed while it's latched sends the assistant what it heard and hands the mic to voice.
/// </summary>
public sealed class VoiceWiring
{
    public readonly VoiceClient Voice;
    /// <summary>The assistant's mic and spoken replies through the same engine.</summary>
    public readonly AssistantMic? Mic;
    readonly Func<bool> _typing;
    bool _keyHeld, _mouseHeld, _gripHeld, _stickBefore, _gate, _pttBefore;
    Assistant.AssistantHost? _assistant;
    string? _ringing;

    readonly AgentSession _session;

    VoiceWiring(VoiceClient voice, Func<bool> typing, AgentSession session)
    {
        Voice = voice;
        _typing = typing;
        _session = session;
        if (voice.Available)
        {
            Mic = new AssistantMic(voice);
            Bridge.BridgeServer.Audio = Mic;
        }
    }

    public static VoiceWiring? Start(AgentSession session, SessionOptions opt, bool headset, Func<bool> typing, ILogger log)
    {
        if (!opt.Voice) return null;
        var voice = new VoiceClient(session, opt.LoginUri, headset, m => log.LogInformation("{Msg}", m));
        voice.StatusChanged += s => log.LogInformation("voice: {Status}", s);
        // teleports hide voice: hook this client and every one the session makes after it (reconnects)
        var hooked = new System.Runtime.CompilerServices.ConditionalWeakTable<LibreMetaverse.GridClient, object>();
        void Hook(LibreMetaverse.GridClient c)
        {
            if (hooked.TryGetValue(c, out _)) return;
            hooked.Add(c, new object());
            voice.Hook(c);
        }
        lock (AgentSession.ClientHooks) AgentSession.ClientHooks.Add(Hook);
        if (session.Client is { } now) Hook(now);
        // voice mutes on the account's block list (SL's mute list, its voice flag), followed as the list changes
        voice.ListMuted = id => session.Mutes.VoiceMuted(id);
        voice.ListMute = (id, mute) =>
        {
            var c = session.Client;
            if (mute) session.Mutes.Add(id, LegacyName(session, id), "resident", c, LibreMetaverse.MuteFlags.VoiceChat);
            else session.Mutes.Remove(id, null, c, LibreMetaverse.MuteFlags.VoiceChat);
        };
        session.Mutes.Changed += voice.MuteListChanged;
        // calls turned away before they ring (the person's switches, B2's friends only, Do Not Disturb)
        var rejects = new CallRejects(session);
        voice.Screen = rejects.Why;
        voice.TurnedAway += rejects.TurnedAway;
        var moderation = new VoiceModeration(session, voice);
        var prefs = new LocalPrefs(opt.BaseDir);
        voice.Ear = Enum.TryParse<VoiceEar>(prefs.Text("voice_hear_from"), true, out var ear) ? ear : VoiceEar.Camera;
        if (voice.Available)
            session.Api.Register(new VoiceApi(session, voice, moderation, rejects, opt.LoginUri.Contains("127.0.0.1") || opt.LoginUri.Contains("localhost")));
        return new VoiceWiring(voice, typing, session) { _unmuted = opt.VoiceUnmuted, Rejects = rejects, Moderation = moderation, _prefs = prefs };
    }

    bool _unmuted;
    LocalPrefs _prefs = null!;
    public CallRejects Rejects { get; private init; } = null!;
    public VoiceModeration Moderation { get; private init; } = null!;

    /// <summary>A resident's name as the block list keeps it (the viewer's legacy name, "Resident" dropped).</summary>
    static string LegacyName(AgentSession s, LibreMetaverse.UUID id)
    {
        var n = s.Names.Legacy(id);
        if (string.IsNullOrEmpty(n) && s.Client is { } c)
        {
            LibreMetaverse.Simulator[] sims;
            lock (c.Network.Simulators) sims = c.Network.Simulators.ToArray();
            n = sims.Select(sim => sim.ObjectsAvatars.Values.FirstOrDefault(a => a.ID == id)?.Name).FirstOrDefault(x => !string.IsNullOrEmpty(x));
        }
        n ??= "";
        return n.EndsWith(" Resident", StringComparison.Ordinal) ? n[..^" Resident".Length] : n;
    }

    // ---- what waits for the person, and the settings ----

    /// <summary>Moderation acts on other people: it waits for the person's own press, and what they couldn't do is
    /// refused before they're asked.</summary>
    void GateModeration(Assistant.Confirmations confirm)
    {
        static bool Moderating(string pump, string op) => pump == "SojournVoice" && op is "moderate" or "moderateAll";
        confirm.Refusals.Add((pump, op, _) => Moderating(pump, op) && Voice.Available ? Moderation.WhyNot() : null);
        confirm.Cases.Add(new Assistant.Confirmations.Case(
            (pump, op, _) => Moderating(pump, op) ? "permissions" : null,
            (_, op, a) =>
            {
                bool mute = a.Bool("mute", true);
                string where = Voice.CallInfo is { } call ? Lang.F("the call with {0}", call.With)
                    : _session.Client?.Network.CurrentSim is { } sim ? Lang.F("nearby voice in {0}", sim.Name) : Lang.T("nearby voice");
                if (op == "moderateAll")
                    return Task.FromResult(mute
                        ? (Lang.F("Mute everyone's voice in {0}", where), Lang.T(Voice.CallInfo == null
                            ? "As a moderator: nobody else is heard by anyone, people who arrive later too, until you unmute everyone."
                            : "As a moderator: nobody else in the call is heard until you unmute them."))
                        : (Lang.F("Unmute everyone's voice in {0}", where), Lang.T("As a moderator: everyone can be heard again.")));
                string who;
                try
                {
                    var id = Moderation.Speaker(a.Str("id") ?? a.Str("name"));
                    a["id"] = id.ToString();   // pinned: the op acts on whom the person was shown
                    who = Moderation.NameOf(id);
                }
                catch (Exception e) { return Task.FromResult((Lang.T(mute ? "Mute someone's voice" : "Unmute someone's voice"), Lang.F("{0}: this will likely fail.", e.Message))); }
                return Task.FromResult(mute
                    ? (Lang.F("Mute {0}'s voice for everyone", who), Lang.F("As a moderator, in {0}: nobody hears {1} until you unmute them.", where, who))
                    : (Lang.F("Unmute {0}'s voice", who), Lang.F("As a moderator, in {0}: everyone can hear {1} again.", where, who)));
            }));
    }

    /// <summary>Settings for any caller (SojournSettings): the call switches and where the person hears voice from. The
    /// menu rows are batch M's (docs/voice.md lists them).</summary>
    public void Settle(Control.SettingsApi api)
    {
        api.Add("reject_p2p_calls", new("voice", "turn away one-to-one voice calls before they ring (Firestorm's), declined without a word",
            "on, off", () => System.Text.Json.Nodes.JsonValue.Create(Rejects.P2P ? "on" : "off"), Control.SettingsApi.Switch("reject_p2p_calls", Rejects.SetP2P)));
        api.Add("reject_group_calls", new("voice", "turn away every group's voice calls before they ring (both viewers'), declined without a word; "
            + "one group's alone: SojournVoice groupCalls",
            "on, off", () => System.Text.Json.Nodes.JsonValue.Create(Rejects.Group ? "on" : "off"), Control.SettingsApi.Switch("reject_group_calls", Rejects.SetGroup)));
        api.Add("reject_conference_calls", new("voice", "turn away ad-hoc conference calls before they ring (Firestorm's), declined without a word",
            "on, off", () => System.Text.Json.Nodes.JsonValue.Create(Rejects.Conference ? "on" : "off"), Control.SettingsApi.Switch("reject_conference_calls", Rejects.SetConference)));
        string[] ears = ["camera", "avatar", "mixed"];
        api.Add("voice_hear_from", new("voice", "where the person hears voice from (the viewer's): camera (in the headset, the head), avatar, or "
            + "mixed (the avatar's place, the camera's or head's direction)", string.Join(", ", ears),
            () => System.Text.Json.Nodes.JsonValue.Create(ears[(int)Voice.Ear]), Control.SettingsApi.Choice("voice_hear_from", ears, i =>
            {
                Voice.Ear = (VoiceEar)i;
                _prefs.Set("voice_hear_from", ears[i]);
            })));
    }

    /// <summary>The settings menu puts the saved settings to voice (mode, on or off, volume) and gets its section.</summary>
    public void Settle(Control.ISettingsMenu? menu, Assistant.AssistantHost assistant)
    {
        if (menu != null) menu.Voice = Voice;
        _assistant = assistant;
        if (Mic is { } mic) assistant.CutMic = mic.Cut;
        _look = () => assistant.Panel.Look;
        GateModeration(assistant.Confirmations);
        // a ringing call: two keys on the assistant's panel, answered by the person's own press
        assistant.LocalPick = c =>
        {
            if (c.Id == "voice:answer") { Voice.Accept(); return true; }
            if (c.Id == "voice:decline") { Voice.Decline(); return true; }
            return false;
        };
        Voice.IncomingChanged += call => assistant.Panel.Update(p =>
        {
            p.Choices.RemoveAll(x => x.Id.StartsWith("voice:"));
            if (call != null)
            {
                p.Prompt = _ringing = call.Kind switch
                {
                    "group" => call.SessionName is { } group ? Lang.F("{0} is calling {1}", call.Name, group) : Lang.F("{0} is calling a group", call.Name),
                    "conference" => Lang.F("{0} is calling you into a conference", call.Name),
                    _ => Lang.F("{0} is calling", call.Name),
                };
                p.Choices.Add(new Assistant.PanelChoice("voice:answer", Lang.T("answer"), null));
                p.Choices.Add(new Assistant.PanelChoice("voice:decline", Lang.T("decline"), null));
            }
            else if (p.Prompt != null && p.Prompt == _ringing) p.Prompt = null;
        });
        // tests: a peer playing a file as an open, live mic
        if (_unmuted && Voice.Mode == VoiceMode.OpenMic) Voice.SetMuted(false);
    }

    /// <summary>The voice key went down (B on the desktop): push-to-talk held, or open mic's mute toggled; not while
    /// typing.</summary>
    public void KeyDown()
    {
        if (_typing() || _keyHeld) return;
        _keyHeld = true;
        if (Voice.Mode == VoiceMode.OpenMic) Voice.ToggleMute();
        Sync();
    }

    public void KeyUp()
    {
        _keyHeld = false;
        Sync();
    }

    /// <summary>The middle mouse button held or let go (push-to-talk).</summary>
    public void MouseHeld(bool held)
    {
        _mouseHeld = held;
        Sync();
    }

    void Sync()
    {
        bool held = Voice.Mode == VoiceMode.PushToTalk && (_keyHeld || _mouseHeld || _gripHeld);
        // pressed while the assistant's mic is latched open (a tap: AssistantHost.TalkPress): the person means to talk
        // to people now, so the request goes as heard so far, cut here without the release's tail so their first words
        // don't run on into it, and the gate passes to voice a frame on
        if (held && !_pttBefore && _assistant?.Latched == true) { _assistant.TalkUp(); Mic?.Cut(false); }
        _pttBefore = held;
        Voice.PushToTalk(held);
    }

    /// <summary>The window lost focus: let go of everything held.</summary>
    public void Release()
    {
        _keyHeld = _mouseHeld = _gripHeld = false;
        Sync();
    }

    public void DesktopFrame(Control.CameraPose? pose, bool assistantTalking)
    {
        if (pose is { } p) Voice.SetListener(p.Region, p.Position, p.Forward, System.Numerics.Vector3.UnitZ);
        Gate(assistantTalking);
    }

    void Gate(bool open)
    {
        if (open == _gate) return;
        _gate = open;
        if (Mic != null) Mic.Gate(open);
        else Voice.AssistantGate(open);
    }

    // ---- what voice draws over the world: a dot over each resident speaking, and your own mic's token ----

    static readonly System.Numerics.Vector4 SpeakingInk = new(0.97f, 0.95f, 0.88f, 0.85f);
    /// <summary>Residents with a name tag this frame: their tag carries the speaking light instead of a dot.</summary>
    public Func<LibreMetaverse.UUID, bool>? Tagged { get; set; }
    Func<Assistant.PanelLook>? _look;
    (Assistant.PanelLook Look, MicColours Colours)? _colours;

    /// <summary>The mic token's colours, from the assistant panel's look: live, a disc lit in the accent with the glyph
    /// in its label colour; muted, the panel's body with the glyph (slashed) in full ink; off, the body fainter with the
    /// glyph dim.</summary>
    readonly record struct MicColours(System.Numerics.Vector4 LiveDisc, System.Numerics.Vector4 LiveInk, System.Numerics.Vector4 Body,
        System.Numerics.Vector4 Ink, System.Numerics.Vector4 OffBody, System.Numerics.Vector4 OffInk);

    MicColours Colours()
    {
        var look = _look?.Invoke() ?? new Assistant.PanelLook();
        if (_colours is { } c && ReferenceEquals(c.Look, look)) return c.Colours;
        static System.Numerics.Vector4 C(string hex, float a)
        {
            var k = SkiaSharp.SKColor.TryParse(hex, out var v) ? v : SkiaSharp.SKColors.Gray;
            return new(k.Red / 255f, k.Green / 255f, k.Blue / 255f, a);
        }
        float body = Math.Max(look.TintOpacity, 0.88f);
        var colours = new MicColours(C(look.Accent, 0.96f), C(look.OnAccent, 1), C(look.Tint, body), C(look.Ink, 1),
            C(look.Tint, body * 0.75f), C(look.Dim, 0.9f));
        _colours = (look, colours);
        return colours;
    }

    /// <summary>Adds voice's marks to a frame's overlay: speaking residents (a dot over the head, bigger when louder) and
    /// a token for your own mic, a microphone on a small disc: at your left wrist in the headset (watch given) or at the
    /// bottom middle of the window on the desktop (vfov and the window's height given: a fixed size in pixels, clear of
    /// the chat feed at the bottom left and the panel at the right, where the viewer's speak button sits). Live (the
    /// mic goes out to residents): lit in the accent, a ring around it following your voice's level. Muted (open mic's
    /// mute, or a moderator's): a slashed mic. Off (push-to-talk not held, the assistant's button held): a quiet mic. No
    /// voice where you are (the region or parcel has it off): a fainter slashed mic that never lights, even while
    /// push-to-talk is held. Voice turned off in the settings: nothing.</summary>
    public Sojourn.Render.Realtime.OverlayScene? Decorate(Sojourn.Render.Realtime.OverlayScene? scene, ulong region,
        System.Numerics.Vector3 eye, System.Numerics.Vector3 forward, System.Numerics.Vector3? watch, float vfov = 0, int screenH = 0)
    {
        if (region == 0 || !Voice.Available) return scene;
        var dots = new List<Sojourn.Render.Realtime.OverlayDot>();
        float level = 0;
        if (_session.Client is { } c)
        {
            var origin = VoiceProtocol.RegionOrigin(region);
            foreach (var p in Voice.Peers)
            {
                if (p.Id == c.Self.AgentID) continue;
                if (!p.Speaking) continue;
                if (Tagged?.Invoke(p.Id) == true) continue;   // their name tag shows it (Social/NameTags)
                if (AvatarGlobal(c, p.Id) is not { } g) continue;
                var at = new System.Numerics.Vector3((float)(g.X - origin.X), (float)(g.Y - origin.Y), (float)g.Z + 1.15f);
                float r = 0.05f + 0.07f * Math.Clamp(p.Power * 4, 0, 1);
                dots.Add(new(at, r, SpeakingInk));
            }
        }
        var glyphs = new List<Sojourn.Render.Realtime.OverlayGlyph>();
        if (Voice.Enabled)
        {
            System.Numerics.Vector3 at;
            float radius = 0.016f;
            if (watch is { } w) at = w;
            else
            {
                var f = System.Numerics.Vector3.Normalize(forward);
                var left = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(System.Numerics.Vector3.UnitZ, f));
                var up = System.Numerics.Vector3.Cross(f, left);
                if (vfov > 0 && screenH > 0)
                {
                    // a metre out, at the window's bottom middle: about 38 px across at 1080 lines, 14 px clear of the edge
                    float halfH = MathF.Tan(vfov / 2), mpp = 2 * halfH / screenH;
                    float rpx = Math.Clamp(screenH * 0.0175f, 10, 24);
                    radius = rpx * mpp;
                    at = eye + f - up * (halfH - (14 + rpx * 1.5f) * mpp);
                }
                else
                {
                    at = eye + f * 1.2f + left * 0.62f - up * 0.34f;
                    radius = 0.022f;
                }
            }
            var k = Colours();
            // no voice here (the region or parcel has it off): a quiet slashed mic that never lights, even held
            bool offHere = Voice.OffHere;
            bool live = Voice.MicOpen && !_gate && !offHere;
            // your own level from your mic, after the echo canceller (as the viewer's updateOwnVolume does): the
            // server's report for you came rarely or not at all, so the ring sat still; -50 dBFS quiet, -15 full
            if (live)
            {
                float db = 20 * MathF.Log10(Math.Max(VoiceNative.sv_mic_level(), 1e-6f));
                level = Math.Clamp((db + 50) / 35, 0, 1);
            }
            bool muted = Voice.ModeratorMuted || (Voice.Mode == VoiceMode.OpenMic && Voice.Muted);
            glyphs.Add(live
                ? new(at, radius, k.LiveDisc, k.LiveInk, Sojourn.Render.Realtime.OverlayGlyphKind.Mic, level)
                : offHere
                    ? new(at, radius, k.OffBody with { W = k.OffBody.W * 0.7f }, k.OffInk with { W = k.OffInk.W * 0.6f }, Sojourn.Render.Realtime.OverlayGlyphKind.MicMuted)
                : muted
                    ? new(at, radius, k.Body, k.Ink, Sojourn.Render.Realtime.OverlayGlyphKind.MicMuted)
                    : new(at, radius, k.OffBody, k.OffInk, Sojourn.Render.Realtime.OverlayGlyphKind.Mic));
        }
        if (dots.Count == 0 && glyphs.Count == 0) return scene;
        scene ??= new Sojourn.Render.Realtime.OverlayScene();
        return scene with { Dots = scene.Dots.Concat(dots).ToArray(), Glyphs = scene.Glyphs.Concat(glyphs).ToArray() };
    }

    /// <summary>An avatar's position, global metres, from the object the region sent (seated: on its seat).</summary>
    static LibreMetaverse.Vector3d? AvatarGlobal(LibreMetaverse.GridClient c, LibreMetaverse.UUID id)
    {
        LibreMetaverse.Simulator[] sims;
        lock (c.Network.Simulators) sims = c.Network.Simulators.ToArray();
        foreach (var sim in sims)
        {
            var o = VoiceProtocol.RegionOrigin(sim.Handle);
            var av = sim.ObjectsAvatars.FirstOrDefault(kv => kv.Value.ID == id).Value;
            if (av != null)
            {
                var pos = av.Position;
                if (av.ParentID != 0 && sim.ObjectsPrimitives.TryGetValue(av.ParentID, out var seat))
                    pos = seat.Position + pos * seat.Rotation;
                else if (av.ParentID != 0) continue;
                return new LibreMetaverse.Vector3d(o.X + pos.X, o.Y + pos.Y, pos.Z);
            }
        }
        return null;
    }

    /// <summary>A headset frame: the listener at the view, the left grip (push-to-talk) and the right stick's click (open
    /// mic's mute) as the controllers have them.</summary>
    public void HeadsetFrame(Control.VrView? view, float leftGrip, bool rightStickClick, bool assistantTalking)
    {
        if (view is { } v) Voice.SetListener(v.Region, v.Position, v.Forward, v.Up);
        // the grip, with the same hysteresis as the assistant's
        if (leftGrip > 0.6f && !_gripHeld) { _gripHeld = true; Sync(); }
        else if (leftGrip < 0.35f && _gripHeld) { _gripHeld = false; Sync(); }
        if (rightStickClick && !_stickBefore && Voice.Mode == VoiceMode.OpenMic) Voice.ToggleMute();
        _stickBefore = rightStickClick;
        Gate(assistantTalking);
    }
}
