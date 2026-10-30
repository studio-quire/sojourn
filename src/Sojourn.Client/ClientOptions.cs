using System.Globalization;

namespace Sojourn.Client;

/// <summary>Command line: the session's options (Sojourn.Session's <see cref="SessionOptions"/>) and the window's, the
/// headset's and the tools'. Defaults suit the local grid (tools/grid.py) and its test account.</summary>
public sealed record ClientOptions : SessionOptions
{
    /// <summary>The login screen (Login/): the person picks or adds an account, grid and start before there's a session;
    /// the login options are ignored then.</summary>
    public bool Login { get; init; }
    /// <summary>A login handed over (Login/LoginStore.cs HandOver) is logged in without the form: the same account again
    /// after a lost GPU, or the launcher's (docs/launcher.md).</summary>
    public bool LoginResume { get; init; }
    /// <summary>The account the login screen shows selected (LoginRestart: after Back or logging out).</summary>
    public string? LoginAccount { get; init; }
    public int WindowWidth { get; init; } = 1600;
    public int WindowHeight { get; init; } = 900;
    /// <summary>Render size per eye; null = the window's size (mono) or half its width (stereo).</summary>
    public (int W, int H)? EyeSize { get; init; }
    public bool Stereo { get; init; }
    public float Ipd { get; init; } = 0.064f;
    public bool Vsync { get; init; } = true;
    /// <summary>Frame cap when vsync is off (0 = none).</summary>
    public int FpsCap { get; init; }
    /// <summary>Vertical field of view, degrees (the viewer's default 60; a Quest 3 eye is about 98 by 94).</summary>
    public float FovDegrees { get; init; } = 60;
    /// <summary>Longest side of the shadow maps (0: the viewer's rule, the eye's size).</summary>
    public int ShadowCap { get; init; }
    public bool Validation { get; init; }
    /// <summary>Quit after this many minutes (keeps test runs short on the shared GPU).</summary>
    public double? Minutes { get; init; }
    /// <summary>Run the measurement tour (Bench.cs) and quit.</summary>
    public bool Bench { get; init; }
    public double BenchSeconds { get; init; } = 6;
    public string? BenchOut { get; init; }
    /// <summary>The tour's views from a file instead of Atelier's (Bench.cs).</summary>
    public string? BenchViews { get; init; }
    /// <summary>A fixed sky (sunrise, noon, sunset, midnight: the viewer's presets), as the comparisons use; null = the
    /// region's own day cycle.</summary>
    public string? Sky { get; init; }
    /// <summary>Save a screenshot every this many seconds (local/client/shots), for checking without looking.</summary>
    public double? ShotEvery { get; init; }
    /// <summary>Run in the headset (OpenXR) when one is there; the desktop otherwise.</summary>
    public bool Vr { get; init; }
    /// <summary>Scale on the runtime's recommended eye size; null (the default, "auto") lets the eyes' size follow the
    /// GPU to hold the frame rate (Xr/EyeScale.cs).</summary>
    public float? XrScale { get; init; }
    /// <summary>Seconds to wait for the OpenXR runtime to answer before staying on the desktop.</summary>
    public float XrWait { get; init; } = 20;
    /// <summary>Refresh rate to ask the headset for (when the runtime lets us choose).</summary>
    public float Hz { get; init; } = 72;
    /// <summary>Desktop stereo with a Quest 3's asymmetric eye frusta (approximate), to measure the headset path.</summary>
    public bool QuestEyes { get; init; }
    /// <summary>Draw the panel's states and the settings menu to PNGs in this folder and quit (Assistant/PanelShots.cs).</summary>
    public string? PanelShots { get; init; }
    /// <summary>Draw the login sheet's stages to PNGs in this folder and quit (Login/LoginShots.cs).</summary>
    public string? LoginShots { get; init; }

    ClientOptions WithVr(Action<VrSettings> change) => this with { VrOverrides = VrOverrides.Append(change).ToArray() };

    public static ClientOptions Parse(string[] args)
    {
        var o = new ClientOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            static (int, int) Size(string s) { var p = s.ToLowerInvariant().Split('x'); return (int.Parse(p[0]), int.Parse(p[1])); }
            float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
            switch (args[i])
            {
                case "--grid": o = o with { LoginUri = Next() }; break;
                case "--first": o = o with { First = Next() }; break;
                case "--last": o = o with { Last = Next() }; break;
                case "--password": o = o with { Password = Next() }; break;
                case "--password-file": o = o with { PasswordFile = Path.GetFullPath(Next()), Password = null }; break;   // relative to where it was run, not the base dir
                case "--start": o = o with { Start = Next(), StartGiven = true }; break;
                case "--login": o = o with { Login = true }; break;
                case "--login-resume": o = o with { LoginResume = true }; break;
                case "--login-account": o = o with { LoginAccount = Next() }; break;
                case "--window": { var (w, h) = Size(Next()); o = o with { WindowWidth = w, WindowHeight = h }; break; }
                case "--eye": o = o with { EyeSize = Size(Next()) }; break;
                case "--stereo": o = o with { Stereo = true }; break;
                case "--ipd": o = o with { Ipd = F(Next()) }; break;
                case "--no-vsync": o = o with { Vsync = false }; break;
                case "--fps": o = o with { FpsCap = int.Parse(Next()), Vsync = false }; break;
                case "--draw-distance": { float dd = F(Next()); o = (o with { DrawDistance = dd }).WithVr(s => s.DrawDistance = dd); break; }
                case "--fov": o = o with { FovDegrees = F(Next()) }; break;
                case "--shadow-cap": { int cap = int.Parse(Next()); o = (o with { ShadowCap = cap }).WithVr(s => s.ShadowCap = cap); break; }
                case "--validate": o = o with { Validation = true }; break;
                case "--bridge-port": o = o with { BridgePort = int.Parse(Next()) }; break;
                case "--minutes": o = o with { Minutes = double.Parse(Next(), CultureInfo.InvariantCulture) }; break;
                case "--bench": o = o with { Bench = true, Vsync = false }; break;
                case "--bench-seconds": o = o with { BenchSeconds = double.Parse(Next(), CultureInfo.InvariantCulture) }; break;
                case "--bench-out": o = o with { BenchOut = Next() }; break;
                case "--bench-views": o = o with { BenchViews = Path.GetFullPath(Next()) }; break;
                case "--sky": { var sky = Next(); o = (o with { Sky = sky }).WithVr(s => s.Sky = sky.ToLowerInvariant()); break; }
                case "--shot-every": o = o with { ShotEvery = double.Parse(Next(), CultureInfo.InvariantCulture) }; break;
                case "--base-dir": o = o with { BaseDir = Path.GetFullPath(Next()) }; break;
                case "--vr": o = o with { Vr = true }; break;
                case "--no-assistant": o = o with { Assistant = false }; break;
                case "--dev": o = o with { Dev = true }; break;
                case "--xr-scale": { var v = Next(); float? xs = v == "auto" ? null : F(v); o = (o with { XrScale = xs }).WithVr(s => s.Resolution = xs); break; }
                case "--xr-wait": o = o with { XrWait = F(Next()) }; break;
                case "--hz": o = o with { Hz = F(Next()) }; break;
                case "--snap": { float deg = F(Next()); o = o.WithVr(v => { v.SmoothTurn = deg <= 0; if (deg > 0) v.SnapAngle = deg; }); break; }
                case "--smooth-turn": o = o.WithVr(v => v.SmoothTurn = true); break;
                case "--turn-speed": { float dps = F(Next()); o = o.WithVr(v => { v.SmoothTurn = true; v.TurnSpeed = dps; }); break; }
                case "--hand-relative": o = o.WithVr(v => v.HandRelative = true); break;
                case "--seated": o = o.WithVr(v => v.Seated = true); break;
                case "--standing": o = o.WithVr(v => v.Seated = false); break;
                case "--no-body": o = o.WithVr(v => v.ShowBody = false); break;
                case "--assistant-model":
                {
                    var name = Next();
                    if (!Enum.TryParse<AssistantModel>(name, ignoreCase: true, out var m) || !Enum.IsDefined(m))
                        throw new ArgumentException($"--assistant-model is quick, sonnet or opus, not {name}");
                    o = o.WithVr(v => v.AssistantModel = m);
                    break;
                }
                case "--no-voice": o = o with { Voice = false }; break;
                case "--no-sounds": o = o with { Sounds = false }; break;
                case "--no-media": o = o with { Media = false }; break;
                case "--voice-mode":
                {
                    var m = Next();
                    var mode = m switch { "ptt" => Sojourn.Session.Voice.VoiceMode.PushToTalk, "open" => Sojourn.Session.Voice.VoiceMode.OpenMic, _ => throw new ArgumentException($"--voice-mode is ptt or open, not {m}") };
                    o = o.WithVr(v => v.VoiceMode = mode);
                    break;
                }
                case "--voice-unmuted": o = o with { VoiceUnmuted = true }; break;
                case "--panel-shots": o = o with { PanelShots = Path.GetFullPath(Next()) }; break;
                case "--login-shots": o = o with { LoginShots = Path.GetFullPath(Next()) }; break;
                case "--quest-eyes": o = o with { QuestEyes = true, Stereo = true, EyeSize = o.EyeSize ?? (2064, 2208) }; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
        }
        return o;
    }
}
