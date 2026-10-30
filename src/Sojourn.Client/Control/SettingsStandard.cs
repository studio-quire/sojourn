using System.Text.Json.Nodes;
using Sojourn.Session.Assistant;
using Sojourn.Session.Control;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using static Sojourn.Session.Control.SettingsApi;

namespace Sojourn.Client.Control;

/// <summary>
/// The VR client's own rows of SojournSettings (Sojourn.Session's <see cref="SettingsApi"/>): the settings menu's
/// moving, assistant and voice rows, the renderer's quality and the view's switches, as this client has them.
/// </summary>
public static class SettingsStandard
{

    /// <summary>The settings menu's rows and the renderer's quality, as this client has them.</summary>
    public static void AddStandard(this SettingsApi api, VrSettings st, VrPilot pilot, AssistantHost assistant, SettingsMenu menu, FrameRenderer renderer,
        Func<global::Sojourn.Session.Voice.VoiceClient?> voice, bool headset)
    {
        string inHeadset = headset ? "" : " (applies in the headset)";
        menu.Settings = api;   // (the menu's rows for other parts' settings are made from it by name)
        api.MenuShots = menu.Shots;
        api.MenuPress = menu.Press;
        // moving (the headset's)
        api.Add("posture", new("moving", "standing, or seated (the avatar stands at its own eye height while you sit)" + inHeadset, "standing, seated",
            () => N(st.Seated ? "seated" : "standing"), Choice("posture", ["standing", "seated"], i => { st.Seated = i == 1; pilot.HeightsChanged(); })));
        api.Add("eye_height", new("moving", "standing, the eyes at your own height or the avatar's" + inHeadset, "own, avatar",
            () => N(st.EyeHeight == EyeHeightMode.Real ? "own" : "avatar"),
            Choice("eye_height", ["own", "avatar"], i => { st.EyeHeight = i == 0 ? EyeHeightMode.Real : EyeHeightMode.Avatar; pilot.HeightsChanged(); })));
        api.Add("turning", new("moving", "how the right stick turns" + inHeadset, "smooth, snap",
            () => N(st.SmoothTurn ? "smooth" : "snap"), Choice("turning", ["smooth", "snap"], i => st.SmoothTurn = i == 0)));
        api.Add("turn_speed", new("moving", "smooth turning at full stick, degrees a second" + inHeadset, "60-360",
            () => N(st.TurnSpeed), Number("turn_speed", 30, 720, " deg/s", f => st.TurnSpeed = f)));
        api.Add("snap_angle", new("moving", "each snap turn, degrees" + inHeadset, "15-90",
            () => N(st.SnapAngle), Number("snap_angle", 5, 180, " deg", f => st.SnapAngle = f)));
        api.Add("move_toward", new("moving", "the left stick walks where you look, or where the left hand points" + inHeadset, "look, left_hand",
            () => N(st.HandRelative ? "left_hand" : "look"), Choice("move_toward", ["look", "left_hand"], i => st.HandRelative = i == 1)));
        api.Add("movement", new("moving", "how the view moves through the world: predicted walks at once on the client's own model of "
            + "walls and floors, the region putting it right where they differ; server (the default) follows your avatar where the region has it, the stick "
            + "a round trip behind but never anywhere the region doesn't put you (walls, steps and falls all the region's)" + inHeadset, "predicted, server",
            () => N(st.Movement == MovementMode.Server ? "server" : "predicted"),
            Choice("movement", ["predicted", "server"], i => st.Movement = i == 1 ? MovementMode.Server : MovementMode.Predicted)));
        api.Add("correction", new("moving", "when your avatar can't walk to where you see yourself: teleport it there (where that lands right; else a jump), "
            + "or put the view back on it with a jump (a straight cut to the spot) or a gentle slide" + inHeadset, "teleport, jump, slide",
            () => N(st.TeleportBack ? "teleport" : st.Correction == CorrectionStyle.Snap ? "jump" : "slide"),
            // ("blink", the jump's old name before the blink's black was dropped, 2026-10-28, still reads as it)
            v => Choice("correction", ["teleport", "jump", "slide"], i =>
            {
                st.TeleportBack = i == 0;
                if (i > 0) st.Correction = i == 1 ? CorrectionStyle.Snap : CorrectionStyle.Slide;
            })(v.GetValueKind() == System.Text.Json.JsonValueKind.String && v.GetValue<string>().Trim().Equals("blink", StringComparison.OrdinalIgnoreCase) ? JsonValue.Create("jump")! : v)));
        api.Add("look_around", new("moving", "look around away from your avatar, which stays where it is (through walls, within 50 m; "
            + "Y held starts it, Y ends it); not kept between sessions" + inHeadset, "on, off",
            () => N(pilot.Spectating), Switch("look_around", on => pilot.Spectating = on)));
        api.Add("point_teleport", new("moving", "holding the left trigger on open ground teleports you there" + inHeadset, "on, off",
            () => N(st.PointTeleport), Switch("point_teleport", on => st.PointTeleport = on)));
        api.Add("body", new("moving", "your own avatar's body shown when you look down" + inHeadset, "on, off",
            () => N(st.ShowBody), Switch("body", on => st.ShowBody = on)));
        api.Add("tracking", new("moving", "your tracked hands (and head, wherever it's shown) on your own avatar: auto gives way to animations from "
            + "things around you (a dance machine, pose balls, a seat's own sit, a dance played for you) while they play; on always tracks; off leaves "
            + "your avatar to its animations" + inHeadset,
            "auto, on, off", () => N(st.Tracking.ToString().ToLowerInvariant()),
            Choice("tracking", ["auto", "on", "off"], i => st.Tracking = (HandTracking)i)));
        api.Add("running", new("moving", "the stick walks run (the left stick's click toggles it)" + inHeadset, "on, off",
            () => N(pilot.Running), Switch("running", on => pilot.Running = on)));
        api.Add("pointer", new("moving", "the right hand's pointer: on, its ray and trigger touch, sit and press menus, and it's what \"that\" means; off, nothing is pointed at (A toggles it)" + inHeadset, "on, off",
            () => N(st.Pointer), Switch("pointer", on => st.Pointer = on)));
        api.Add("gaze", new("moving", "what others see of where you look (the headset, the desktop's first person): eye_contact turns your avatar's "
            + "head the way you look and toward the person you look at; direction only the way you look, never naming who or what; off "
            + "sends nothing", "eye_contact, direction, off",
            () => N(st.Gaze switch { GazeSharing.EyeContact => "eye_contact", GazeSharing.Direction => "direction", _ => "off" }),
            Choice("gaze", ["eye_contact", "direction", "off"], i => st.Gaze = (GazeSharing)i)));

        // the assistant
        api.Add("advanced_menu", new("view", "the settings menu shows every setting (on), or only what a person reaches for by hand (off, the default: "
            + "the model, memory, touches, incognito, the mic, draw distance, a script's controls back); only the menu changes, every setting stays "
            + "yours to change either way", "on, off",
            () => N(st.AdvancedMenu), Switch("advanced_menu", on => menu.SetAdvanced(on))));
        api.Add("interface", new("view", "everything Sojourn draws over the world (the panel, menus, chat, cards, name tags, wrist tokens, "
            + "boards, the pointer) shown, or hidden (X or Tab, as the person does); hidden, only a confirmation shows, and replies wait until it's shown again",
            "shown, hidden", () => N(st.InterfaceHidden ? "hidden" : "shown"), Switch("interface", on => menu.SetHidden(!on))));
        api.Add("assistant_model", new("assistant", "who takes requests first: quick (Haiku, Opus for multi-step jobs), sonnet, or opus for everything", "quick, sonnet, opus",
            () => N(AssistantHost.ModelName(st.AssistantModel)), Choice("assistant_model", ["quick", "sonnet", "opus"], i => menu.SetAssistantModel((AssistantModel)i, announce: true))));
        foreach (var m in AssistantEfforts.Models)
            api.Add($"{m}_effort", new("assistant", $"how much {m} thinks before it acts (Claude's effort; more is slower and surer)"
                + (m == "opus" ? ", in opus mode and as the planner quick and sonnet hand multi-step requests to" : $", in {(m == "haiku" ? "quick" : m)} mode")
                + "; a change applies from the model's next turn, a request it's on finishing as it started",
                string.Join(", ", AssistantEfforts.Levels), () => N(st.AssistantEffort.Get(m)),
                Choice($"{m}_effort", AssistantEfforts.Levels, i => menu.SetAssistantEffort(m, AssistantEfforts.Levels[i]))));
        api.Add("spoken_replies", new("assistant", "the assistant's replies read aloud as well as shown", "on, off",
            () => N(st.SpokenReplies), Switch("spoken_replies", on => { st.SpokenReplies = on; assistant.PrefsChanged(); })));
        api.Add("hearing", new("assistant", "what hears the person: parakeet (Parakeet TDT on the CPU, faster and better on everyday words, "
            + "names put in by a matching pass; the default), whisper (small.en on the CPU, told the names around them), or soniox "
            + "(Soniox's cloud service, streamed while they talk; needs the person's own key, Whisper standing in without it)",
            "whisper, parakeet, soniox", () => N(st.Hearing),
            Choice("hearing", VrSettings.Hearings, i => { st.Hearing = VrSettings.Hearings[i]; assistant.PrefsChanged(); })));
        api.Add("assistant_look", new("assistant", "the panel's and menu's look, a preset (finer changes: the panel_look tool)",
            string.Join(", ", PanelLook.Presets.Select(p => p.Preset)),
            () => N(assistant.Panel.Look.Preset),
            Choice("assistant_look", PanelLook.Presets.Select(p => p.Preset).ToArray(), i =>
            {
                if (assistant.ChangeLook is { } change) change(PanelLook.Presets[i]); else assistant.Panel.Look = PanelLook.Presets[i];
            })));
        api.Add("adaptive_ink", new("assistant", "the panel's, menu's, feed's, cards' and reader's text turns light over dark places "
            + "and dark over bright ones, from what's behind each, so a clear look (low tint_opacity and frost) stays readable "
            + "without the glow, which is off while this is on; where the body is solid enough it doesn't change", "on, off",
            () => N(st.AdaptiveInk), Switch("adaptive_ink", on => st.AdaptiveInk = on)));
        api.Own("confirm_touches", "assistant", "touches and menu answers on other people's things wait for the person's press; a safeguard on the assistant, so only the person turns it off in the menu",
            () => N(st.ConfirmTouches));
        api.Own("assistant_provider", "assistant", "whose models the assistant runs on (claude: the Claude account signed in on this PC; "
            + "anthropic or openrouter: the person's own key); the person's own choice in the menu, since it spends their money, and keys never pass through here",
            () => N(st.AssistantProvider));
        api.Own("openrouter_model", "assistant", "with OpenRouter, which model answers (claude: Anthropic's, as usual; any other OpenRouter id takes "
            + "every request itself); the person's own choice in the menu", () => N(st.OpenRouterModel));
        api.Own("assistant_memory", "assistant","the assistant keeps notes about the person between sittings (its memory); the person's own switch, in the menu, where they can also clear it",
            () => N(st.AssistantMemory));

        // voice chat
        api.Add("voice_chat", new("voice", "voice chat with residents", "on, off",
            () => N(st.VoiceEnabled), Switch("voice_chat", on =>
            {
                st.VoiceEnabled = on;
                if (voice() is { } v) v.Enabled = on;
            })));
        api.Add("voice_mode", new("voice", "push to talk (hold the left grip or B), or an open mic that starts muted", "push_to_talk, open_mic",
            () => N(st.VoiceMode == global::Sojourn.Session.Voice.VoiceMode.OpenMic ? "open_mic" : "push_to_talk"),
            Choice("voice_mode", ["push_to_talk", "open_mic"], i =>
            {
                st.VoiceMode = i == 1 ? global::Sojourn.Session.Voice.VoiceMode.OpenMic : global::Sojourn.Session.Voice.VoiceMode.PushToTalk;
                if (voice() is { } v) v.Mode = st.VoiceMode;
            })));
        api.Add("voice_volume", new("voice", "how loud residents are (1: the viewer's own level, half the voice server's mix, in balance with world sounds)", "0.25-2",
            () => N(st.VoiceVolume), Number("voice_volume", 0, 2, "", f => { st.VoiceVolume = f; voice()?.SetVolume(f); })));
        api.Add("mic", new("voice", "open mic: muted, or live; going live is the person's own button", "muted",
            () => N(voice() is { } v ? (v.Muted ? "muted" : "live") : "off"),
            v =>
            {
                var s = v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>().Trim().ToLowerInvariant() : "";
                if (s is "live" or "on" or "unmuted") return "mic: going live is the person's own press (B or the right stick's click, or the menu's mic switch)";
                if (s is not ("muted" or "off")) return "mic: muted (only the person makes it live)";
                if (voice() is not { } vc) return "mic: voice chat isn't running";
                if (!vc.Muted) vc.ToggleMute();
                return null;
            }));

        // the view
        api.Add("draw_distance", new("view", "how far you see, and how far the region sends objects (metres)", "16-512",
            () => N(api.Session.Camera.FarClip), Number("draw_distance", CameraControl.MinFarClip, CameraControl.MaxFarClip, " m", f => api.Session.Camera.SetFarClip(f))));
        var q = renderer.Quality;
        api.Add("shadows", new("view", "sun, moon and projector shadows (off: lighter on the GPU)", "on, off",
            () => N(q.Shadows), Switch("shadows", on => { q.Shadows = on; st.Shadows = on; })));
        api.Add("shadow_resolution", new("view", "the shadow maps' longest side in pixels (0: the screen's size, as the viewer does)", "0, 512-4096",
            () => N(q.ShadowCap), v => Number("shadow_resolution", 0, 4096, " px", f =>
            {
                int cap = f < 256 ? 0 : (int)f;
                q.ShadowCap = cap;
                st.ShadowCap = cap;
            })(v)));
        api.Add("load_governor", new("view", "while frames run long, loading slows but never stops (when far behind, new things beyond 16 m come in at a rate, largest first), shadows' full detail ends sooner far away (down to half as far; with far shadows on they carry on coarser beyond, else they end there) and avatars beyond 20-48 m are drawn as impostors, all gradually and temporary; off: loading at full speed and everything drawn whatever the frame (video memory is kept in budget either way)", "on, off",
            () => N(q.LoadGovernor), Switch("load_governor", on => { q.LoadGovernor = on; st.LoadGovernor = on; })));
        api.Add("alpha_layers", new("view", "see-through surfaces (flowers, glass, leaves): how many layers of them each pixel shows at most, nearest first; what's dropped is behind layers that let through almost nothing of it, or behind this many (0: every layer)", "0, 4, 8, 16",
            () => N(q.AlphaLayers), Number("alpha_layers", 0, 64, "", f => { q.AlphaLayers = (int)f; st.AlphaLayers = (int)f; })));
        api.Add("simple_see_through", new("view", "see-through surfaces lit more simply (their ambient, haze and nearby lights per corner rather than per pixel): lighter on the GPU in dense flowers and glass, a small change in look", "on, off",
            () => N(q.SimpleAlpha), Switch("simple_see_through", on => { q.SimpleAlpha = on; st.SimpleAlpha = on; })));
        api.Add("half_ssao", new("view", "the sun's shadow and ambient occlusion worked out at half resolution and brought back up along edges: lighter on the GPU, slightly softer small shadow details", "on, off",
            () => N(q.HalfSsao), Switch("half_ssao", on => { q.HalfSsao = on; st.HalfSsao = on; })));
        api.Add("far_shadow_margin", new("view", "far sun shadows fitted this share larger, so one far shadow map can be kept a frame while the view moves (0: as the viewer); slightly softer far shadows", "0, 0.05, 0.1, 0.2",
            () => N(q.FarCascadeMargin), Number("far_shadow_margin", 0, 0.5f, "", f => { q.FarCascadeMargin = f; st.FarShadowMargin = f; })));
        api.Add("far_shadows", new("view", "shadows carried on far away in a coarse shadow map of this size, past where the full-detail ones end (and past where the load governor ends those under lag), fading out a little beyond the draw distance, so they never stop in a ring around you (0: none; shadows end as in other viewers)", "0, 1024, 2048",
            () => N(q.FarShadowSize), Number("far_shadows", 0, 4096, "", f => { q.FarShadowSize = (int)f; st.FarShadows = (int)f; })));
        api.Add("min_caster_texels", new("view", "things smaller than this many texels in a sun shadow map cast no shadow into it (0: everything casts, as the viewer)", "0, 0.5, 1, 2",
            () => N(q.MinCasterTexels), Number("min_caster_texels", 0, 8, "", f => { q.MinCasterTexels = f; st.MinCasterTexels = f; })));
        api.Add("narrow_gbuffer", new("view", "the renderer's per-pixel surface buffers in narrower formats (normals at 10 bits rather than 16): lighter on the GPU, a faint change on glossy curved surfaces; applies when Sojourn next starts", "on, off",
            () => N(st.NarrowGBuffer), Switch("narrow_gbuffer", on => st.NarrowGBuffer = on)));
        api.Add("reflections", new("view", "reflection probes: reflections and ambient light from the surroundings (off: from the sky alone)", "on, off",
            () => N(q.Reflections), Switch("reflections", on => { q.Reflections = on; st.Reflections = on; })));
        api.Add("mirrors", new("view", "mirrors: a prim made a mirror (a reflection probe with the mirror flag) reflects what's in front of it, avatars too if the probe is dynamic; costs only while one is in view", "on, off",
            () => N(q.Mirrors), Switch("mirrors", on => { q.Mirrors = on; st.Mirrors = on; })));
        api.Add("flexi", new("view", "flexible prims move as in the viewer: old-style hair, skirts, capes, tails, flags and plants sway, hang and follow the avatar; off: drawn straight and still", "on, off",
            () => N(q.Flexi), Switch("flexi", on => { q.Flexi = on; st.Flexi = on; })));
        api.Add("avatar_physics", new("view", "avatar physics: breasts, belly and butt move with the body, from what people's physics wearables set (the viewer's Avatar Physics); off: still", "on, off",
            () => N(q.AvatarPhysics), Switch("avatar_physics", on => { q.AvatarPhysics = on; st.AvatarPhysics = on; })));
        api.Add("mirror_resolution", new("view", "how sharp a mirror's reflection is: its size as a fraction of the eye's (1 sharpest, most GPU)", "0.25-1",
            () => N(q.MirrorScale), Number("mirror_resolution", 0.25f, 1, "", f => { q.MirrorScale = f; st.MirrorResolution = f; })));
        if (Sojourn.Client.Xr.LagHook.Testing)
            api.Add("test_lag", new("view", "a lag test (tools/lagtest.py): milliseconds of CPU spun in each headset frame, a number or a range (a random amount each frame); off", "off, a number, or a range like 6-14",
                () => Sojourn.Client.Xr.LagHook.Cpu, v =>
                {
                    var s = v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>().Trim() : v.ToJsonString();
                    if (s is not ("off" or "0") && Sojourn.Client.Xr.LagHook.Range(s) == null) return "test_lag is off, a number of ms, or a range like 6-14";
                    Sojourn.Client.Xr.LagHook.Cpu = s;
                    return null;
                }));
        if (Sojourn.Client.Xr.LagHook.Testing)
        {
            api.Add("test_wait_gpu", new("view", "a lag-test diagnostic: wait for each frame's GPU work before handing the headset its images (on: the jitter fix, always on outside tests; off brings the whole-view shake under load back)", "on, off",
                () => N(Sojourn.Client.Xr.LagHook.WaitGpu), Switch("test_wait_gpu", on => Sojourn.Client.Xr.LagHook.WaitGpu = on)));
            api.Add("test_mirror", new("view", "a lag-test diagnostic: the desktop window's mirror of the eyes (off: the headset's frame no longer waits on the window)", "on, off",
                () => N(Sojourn.Client.Xr.LagHook.Mirror), Switch("test_mirror", on => Sojourn.Client.Xr.LagHook.Mirror = on)));
        }
        api.Add("detail", new("view", "how far objects keep their detail (the viewer's LOD factor; 1.75 is its High-Ultra)", "0.5-4",
            () => N(q.LodFactor), Number("detail", 0.5f, 4, "", f => { q.LodFactor = f; st.Detail = f; })));
        var lim = renderer.AvatarLimits;
        api.Add("full_avatars", new("view", "how many of the nearest avatars are drawn whole; the rest are impostors, kept up to date a few times a second (0: all whole)", "0-66",
            () => N(lim.MaxFull), Number("full_avatars", 0, 66, "", f => { lim.MaxFull = (int)f; st.FullAvatars = (int)f; })));
        api.Add("avatar_complexity", new("view", "the rendering complexity over which an avatar is drawn as a jelly doll, without its attachments (0: no limit; the viewer's High-Ultra 350000)", "0-1000000",
            () => N(lim.MaxComplexity), Number("avatar_complexity", 0, 1_000_000, "", f => { lim.MaxComplexity = (int)f; st.AvatarComplexity = (int)f; })));
        api.Add("names", new("view", "names over people (everyone within chat range, and whoever's looked at, speaking or typing)", "on, off",
            () => N(st.ShowNames), Switch("names", on => st.ShowNames = on)));
        api.Add("chat", new("view", "other people's chat, IMs, group chat and notices (offers, menus, friends online) on screen", "on, off",
            () => N(st.ShowChat), Switch("chat", on => st.ShowChat = on)));
        api.Add("hover_text", new("view", "objects' floating text over them (prices, signs, titles, game status)", "on, off",
            () => N(st.ShowHoverText), Switch("hover_text", on => st.ShowHoverText = on)));
        // kept by the grid with the account, as the viewer's preference is: the account's own until changed
        api.Add("maturity", new("view", "the highest rating of region you go into, up to what the account allows (beyond it is refused with why); the grid keeps it with the account",
            string.Join(", ", AgentSession.Maturities), () => N(api.Session.Maturity), _ => "maturity: changed through the grid")
            { SetAsync = (v, ct) => api.Session.SetMaturityAsync(v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>().Trim().ToLowerInvariant() : v.ToJsonString(), ct) });
    }

    /// <summary>Apply the saved view settings to the renderer at start.</summary>
    public static void ApplySaved(VrSettings st, FrameRenderer renderer)
    {
        var q = renderer.Quality;
        q.Shadows = st.Shadows;
        if (st.ShadowCap is { } cap) q.ShadowCap = cap;
        q.Reflections = st.Reflections;
        q.Mirrors = st.Mirrors;
        q.Flexi = st.Flexi;
        q.AvatarPhysics = st.AvatarPhysics;
        q.MirrorScale = Math.Clamp(st.MirrorResolution, 0.25f, 1f);
        q.LodFactor = Math.Clamp(st.Detail, 0.5f, 4f);
        q.LoadGovernor = st.LoadGovernor;
        q.AlphaLayers = Math.Clamp(st.AlphaLayers, 0, 64);
        q.SimpleAlpha = st.SimpleAlpha;
        q.HalfSsao = st.HalfSsao;
        q.FarCascadeMargin = Math.Clamp(st.FarShadowMargin, 0, 0.5f);
        q.MinCasterTexels = Math.Clamp(st.MinCasterTexels, 0, 8);
        q.FarShadowSize = Math.Clamp(st.FarShadows, 0, 4096);
        renderer.AvatarLimits.MaxFull = Math.Max(0, st.FullAvatars);
        renderer.AvatarLimits.MaxComplexity = Math.Max(0, st.AvatarComplexity);
    }
}
