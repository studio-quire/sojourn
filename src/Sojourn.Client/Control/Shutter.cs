using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Client.Xr;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Control;

/// <summary>
/// Taking pictures by hand (batch M; SojournCapture's snapshot, saved to Pictures\Sojourn as the assistant's are).
/// In the headset, a camera, taken out and put away by the `photo_camera` setting (menu > you > camera, or the assistant; no
/// token on the arm since 2026-10-29: the user found the forearm crowded and Sojourn is assistant first). Out, the two
/// hands hold the picture as a panel between them (the user's design, 2026-10-29): the controllers are its opposite
/// corners, a real rectangle in the room: its bottom left just off the left palm, its top right just off the right
/// palm, its bottom edge level, its sides rising toward the right hand (nearer tips it back); any shape
/// (`photo_ratio` locks a W:H, keeping the hands' diagonal). The picture is taken from the panel's centre along its
/// facing, its field of view what the panel spans from the eyes' distance behind it (as if looking through it: hands
/// spread or held closer, wider), about 2 megapixels. Its edges and corners are drawn, and only while the hands frame
/// is the right trigger the shutter (unless the ray is on a menu, a board or a key; touching waits meanwhile);
/// otherwise the trigger is as ever (pictures were taken by accident when it was the shutter whenever the camera was
/// out: the user, 2026-10-29). Whether a press is the shutter, and the panel it takes, are fixed from the trigger's first
/// movement, so the press doesn't shake the shot. The panel flashes as it's taken, with the viewer's snapshot sound,
/// and once saved the picture shows for a few seconds just above the panel (ShutterReview). On the desktop, the
/// viewer's Snapshot to Disk key, Ctrl+` (Program.cs), takes the window's view. Main thread.
/// </summary>
public sealed class Shutter(AgentSession s, Func<Sojourn.Session.Audio.WorldSounds?> sounds, Action<string> say)
{
    static readonly UUID SnapshotSound = new("3d09f582-3851-c0e0-f5ba-277ac5c73fb4");   // UISndSnapshot
    const float Deg = MathF.PI / 180;
    // framing: the camera out and both hands tracked, at least 15 cm apart; the picture at least 6 degrees a side, at most 120 tall and 140 wide (wider than the Quest shows, zooming out: the user, 2026-10-30),
    // 1:3 to 3:1; its field of view as the panel looks from the eyes' distance behind it, held to 25-70 cm
    static readonly float MinTan = MathF.Tan(3 * Deg), MaxTanV = MathF.Tan(60 * Deg),
        MaxTanH = MathF.Tan(70 * Deg);
    const float MinSpan = 0.15f, NearEye = 0.25f, FarEye = 0.7f, PalmOff = 0.04f;
    const float MaxAspect = 3, Pixels = 1920 * 1080;
    const int LongestSide = FrameRenderer.StillTile;   // one tile: no tiling's extra passes for a press

    /// <summary>The panel in the room (play space): its centre, facing away from you (the shot's direction), up and right
    /// in its plane, half its width and height in metres, and the picture's half-tangents.</summary>
    readonly record struct Shot(Vector3 Centre, Vector3 Fwd, Vector3 Up, Vector3 Right, float HalfW, float HalfH, float TanH, float TanV);

    const long HoldMs = 600;
    long _seenAt = -10_000;
    bool _down, _armed = true;
    long _flashAt = -10_000;
    int _taking;
    Shot? _shot, _held;
    bool _pressing;
    Vector3 _aimAt, _aimFwd, _aimUp;   // the panel's centre and axes in the region, this frame

    volatile bool _out;
    volatile float _ratio;   // W/H, 0 free
    volatile Sojourn.Session.Assistant.PanelLook? _look;   // the look as Decorate last had it, for the picture's sheet

    /// <summary>The picture just taken, shown a few seconds above the panel (Program puts its layer among the boards).</summary>
    public readonly ShutterReview Review = new();
    volatile float _pivot = 0.06f;   // tuned by the user in the headset, 2026-10-30

    /// <summary>The `photo_pivot` setting: metres below the eyes the panel is aimed from.</summary>
    public float Pivot { get => _pivot; set => _pivot = Math.Clamp(value, 0, 0.5f); }

    /// <summary>The camera is out: the right trigger is the shutter. Set by the `camera` setting (any thread).</summary>
    public bool Out
    {
        get => _out;
        set { if (_out == value) return; _out = value; _armed = false; _zoom = 1; say(value ? "camera out" : "camera put away"); }
    }

    // zoom by the right stick while the camera's out, instead of what the stick otherwise does there (up in, down out;
    // the user, 2026-10-30): 1 is the panel's own view, back to 1 each time the camera comes out
    const float MinZoom = 0.25f, MaxZoom = 8, ZoomPerView = 0.7f;
    volatile float _zoom = 1;

    /// <summary>The right stick's sheet while the camera's out, up and down only (sideways still turns you).</summary>
    public StickSheet? StickZoom => Out ? new StickSheet(this, Scroll: (down, _) =>
        _zoom = Math.Clamp(_zoom * MathF.Exp(-down * ZoomPerView), MinZoom, MaxZoom), Vertical: true) : null;

    /// <summary>The `photo_ratio` setting: free (the hands' shape) or W:H.</summary>
    public string Ratio => _ratio <= 0 ? "free" : RatioText(_ratio);

    /// <summary>Sets the ratio: free, square, W:H (16:9, 4:5...) or a number; an error message, or null.</summary>
    public string? SetRatio(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        float r;
        if (t is "free" or "off" or "none" or "") r = 0;
        else if (t is "square") r = 1;
        else
        {
            var parts = t.Split(':', 'x', '/', '*');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (parts.Length == 2 && float.TryParse(parts[0], inv, out var w) && float.TryParse(parts[1], inv, out var h) && w > 0 && h > 0) r = w / h;
            else if (parts.Length == 1 && float.TryParse(t, inv, out var x) && x > 0) r = x;
            else return "photo_ratio is free, square, or W:H (16:9, 3:2, 4:5, 9:16...)";
            if (r > MaxAspect * 1.001f || r < 1 / MaxAspect / 1.001f) return "photo_ratio goes from 1:3 to 3:1";
        }
        _ratio = r;
        return null;
    }

    static string RatioText(float r)
    {
        for (int h = 1; h <= 20; h++)
            if (MathF.Abs(r * h - MathF.Round(r * h)) < 0.01f * h) return $"{MathF.Round(r * h)}:{h}";
        return r.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The camera's out and the hands frame a picture (through a press, as it began): the right trigger is the
    /// shutter, and touching waits.</summary>
    public bool Framing => Out && (_pressing ? _held : _shot) is not null;

    /// <summary>Nothing of the camera's takes the ray now (kept for the callers' checks).</summary>
    public bool RightOn => false;

    /// <summary>Each headset frame: the panel from the hands (held from the trigger's first movement), and the trigger
    /// re-armed once let go (so the press that took it out isn't a picture).</summary>
    /// <param name="head">the eyes' centre and facing, in the room</param>
    /// <param name="blocked">a confirmation or the menu is up, or the interface is hidden: no token, no press</param>
    public void Frame(XrControllers input, VrPilot pilot, Vector3 head, Quaternion headRot, bool blocked, bool focused)
    {
        if (input.RightTrigger < 0.35f) { _armed = true; _down = false; }
        if (!focused) _armed = false;
        var now = Out ? Panel(input, head, headRot) : null;
        // a hand's tracking dropping out for a moment (out of the headset's cameras) keeps the panel where it was
        long t = Environment.TickCount64;
        if (now is not null) _seenAt = t;
        else if (Out && t - _seenAt < HoldMs) now = _shot;
        // a press is the shutter if the hands framed just before the trigger started down, and takes that panel (the
        // press shifts the right hand, a corner), until let go
        if (input.RightTrigger < 0.1f) (_pressing, _held) = (false, null);
        else if (!_pressing) (_pressing, _held) = (true, _shot);
        _shot = now;
        if ((_pressing ? _held : now) is { } f)
            (_aimAt, _aimFwd, _aimUp) = (pilot.RoomToRegion(f.Centre), pilot.RoomDirToRegion(f.Fwd), pilot.RoomDirToRegion(f.Up));
    }

    /// <summary>The panel the hands hold, in the room; null when they don't frame (one untracked, down, too close
    /// together, or the panel turned toward you).</summary>
    Shot? Panel(XrControllers input, Vector3 head, Quaternion headRot)
    {
        float ratio = _ratio;
        if (input.LeftHandPos is not { } lg || input.RightHandPos is not { } rg || input.LeftHand is not { } lq || input.RightHand is not { } rq) return null;
        // a real rectangle in the room (the user, 2026-10-29: not a shape on the view): its bottom left corner just off
        // the left palm, its top right just off the right palm (OpenXR's grip +X comes out of the left palm and into the
        // right one), where the hands really are; its bottom edge level, its sides rising from there toward the right
        // hand, so raising that hand makes it taller
        Vector3 lp = lg + Vector3.Transform(Vector3.UnitX, lq) * PalmOff, rp = rg - Vector3.Transform(Vector3.UnitX, rq) * PalmOff;
        // out means out: no rule on where you look or where the hands are (the user, 2026-10-30: it went off when they
        // looked away from their hands, and at random), only that they're apart enough to hold a picture
        var diag = rp - lp;
        if (diag.Length() < MinSpan) return null;
        var centre = (lp + rp) / 2;
        var right = new Vector3(diag.X, 0, diag.Z);
        if (right.LengthSquared() < 0.0004f) right = Vector3.Transform(Vector3.UnitX, headRot) with { Y = 0 };
        if (right.LengthSquared() < 1e-6f) return null;
        right = Vector3.Normalize(right);
        // tilted up or down as it's aimed through its centre from a point below the eyes (the user, 2026-10-30: held
        // higher it looks up, lower down; from the eyes themselves, hands held comfortably low looked down), its
        // bottom edge still level
        var through = centre - (head - Vector3.UnitY * _pivot);
        var fwd = through - right * Vector3.Dot(through, right);
        fwd = fwd.LengthSquared() > 1e-6f ? Vector3.Normalize(fwd) : Vector3.Normalize(Vector3.Cross(Vector3.UnitY, right));
        var up = Vector3.Cross(right, fwd);
        if (up.Y < 0) { up = -up; right = -right; }

        // the hands at opposite corners: the half diagonal's parts along the panel's sides
        var half = diag / 2;
        float hw = MathF.Abs(Vector3.Dot(half, right)), hh = MathF.Abs(Vector3.Dot(half, up));
        if (ratio > 0) (hw, hh) = Fit(MathF.Sqrt(hw * hw + hh * hh), ratio);
        else if (hw > hh * MaxAspect) hh = hw / MaxAspect;
        else if (hh > hw * MaxAspect) hw = hh / MaxAspect;
        // the picture: the panel as seen from the eyes' distance behind it (looking through it at arm's length), at least
        // 6 degrees a side and at most what the headset shows, the shape kept
        float eye = Math.Clamp(Vector3.Dot(centre - head, fwd), NearEye, FarEye);
        float th = hw / eye, tv = hh / eye;
        float k = MathF.Max(1, MinTan / MathF.Min(th, tv));
        // the right stick's zoom on top (the user, 2026-10-30), held to the same limits
        k /= _zoom;
        k = MathF.Max(k, MinTan / MathF.Min(th, tv));
        k = MathF.Min(k, MathF.Min(MaxTanH / th, MaxTanV / tv));
        return new Shot(centre, fwd, up, right, hw, hh, th * k, tv * k);
    }

    /// <summary>Half the sides of a W/H rectangle with the given half diagonal.</summary>
    static (float W, float H) Fit(float diagonal, float ratio)
    {
        float v = diagonal / MathF.Sqrt(1 + ratio * ratio);
        return (v * ratio, v);
    }

    /// <summary>After the panel, boards and keys have seen the ray: the shutter.</summary>
    /// <param name="free">the trigger may be the shutter: no confirmation or menu, the ray isn't on the panel, the feed,
    /// a board, a page or a key, the assistant isn't asking to point</param>
    public void Shoot(XrControllers input, bool free, bool focused)
    {
        if (!Out || !focused || !free || !_armed || _down || input.RightTrigger <= 0.6f || _held is not { } f) return;
        _down = true;
        _armed = false;
        Take(f, _aimAt, _aimFwd, _aimUp);
    }

    void Take(Shot f, Vector3 at, Vector3 fwd, Vector3 up)
    {
        if (Interlocked.Exchange(ref _taking, 1) == 1) return;   // one at a time
        if (s.Api.ApiMap.GetValueOrDefault("SojournCapture") is not { } capture || !capture.Ops.TryGetValue("snapshot", out var snapshot))
        {
            _taking = 0;
            return;
        }
        // about 2 megapixels in the panel's shape, the longest side one tile at most
        float aspect = f.TanH / f.TanV;
        float w = MathF.Sqrt(Pixels * aspect), h = w / aspect, k = MathF.Min(1, LongestSide / MathF.Max(w, h));
        int pw = (int)MathF.Round(w * k / 2) * 2, ph = (int)MathF.Round(h * k / 2) * 2;
        // doubles: Json.AsDouble didn't read a float-backed value (so fov and aim were dropped until 2026-10-29)
        static JsonArray Arr(Vector3 v) => new((double)v.X, (double)v.Y, (double)v.Z);
        var args = new JsonObject
        {
            ["width"] = pw, ["height"] = ph, ["fov"] = 2 * Math.Atan(f.TanV) * 180 / Math.PI,
            ["aim"] = new JsonObject { ["at"] = Arr(at), ["forward"] = Arr(fwd), ["up"] = Arr(up) },
        };
        _flashAt = Environment.TickCount64;
        if (s.Client is { } c) sounds()?.Trigger(SnapshotSound, c.Self.AgentID, 1, Sojourn.Session.Audio.WorldSounds.Kind.Ui, null);
        _ = Task.Run(async () =>
        {
            try
            {
                // the person's own press: the op itself, not the dispatcher the assistant's calls go through
                var r = await snapshot.Run(args, CancellationToken.None);
                say(r is JsonObject o && o["path"] is { } path ? $"picture: {path}" : $"picture not taken: {r?.ToJsonString()}");
                if (r is JsonObject saved && saved["path"]?.GetValue<string>() is { } file)
                    try { Review.Taken(file, f.Centre, f.Up, f.HalfW, f.HalfH, _look ?? Sojourn.Session.Assistant.PanelLook.InUse); }
                    catch (Exception e) { say("picture not shown: " + e.Message); }
            }
            catch (Exception e) { say("picture not taken: " + e.Message); }
            finally { _taking = 0; }
        });
    }

    /// <summary>While the hands frame a picture, the panel between them: the picture it would take, live (the renderer's
    /// preview, FrameRenderer.Preview.cs), with thin edges and brighter corners (held through a press). While the camera
    /// is out and nothing's framed, the preview waits (its targets kept ready, nothing drawn).</summary>
    public OverlayScene? Decorate(OverlayScene? scene, VrPilot pilot, Vector3 head, Quaternion headRot, Sojourn.Session.Assistant.PanelLook look)
    {
        _look = look;
        if (!Out) return scene;
        if ((_pressing ? _held : _shot) is not { } f) return scene is null ? null : scene with { Preview = CameraPreview.Waiting };
        float w = f.HalfW, h = f.HalfH, arm = MathF.Min(0.05f, 0.35f * MathF.Min(w, h));
        float flash = Math.Clamp(1 - (Environment.TickCount64 - _flashAt) / 300f, 0, 1);
        var ink = look.Light(0.6f + 0.35f * flash);
        var edge = look.Light(0.25f + 0.5f * flash);
        var beams = new List<OverlayBeam>(scene?.Beams ?? []);
        Vector3 At(int sx, int sy) => f.Centre + f.Right * (sx * w) + f.Up * (sy * h);
        var ring = new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) };
        for (int i = 0; i < 4; i++)
        {
            var (ax, ay) = ring[i];
            var (bx, by) = ring[(i + 1) % 4];
            beams.Add(new OverlayBeam(pilot.RoomToRegion(At(ax, ay)), pilot.RoomToRegion(At(bx, by)), 0.0012f, edge));
            var corner = At(ax, ay);
            var a = pilot.RoomToRegion(corner);
            beams.Add(new OverlayBeam(a, pilot.RoomToRegion(corner - f.Right * (ax * arm)), 0.003f, ink));
            beams.Add(new OverlayBeam(a, pilot.RoomToRegion(corner - f.Up * (ay * arm)), 0.003f, ink));
        }
        // the camera as the shutter would send it (Take's aim), over the panel (top left, top right, bottom right, bottom left)
        var preview = new CameraPreview(pilot.RoomToRegion(f.Centre), pilot.RoomDirToRegion(f.Fwd), pilot.RoomDirToRegion(f.Up), f.TanH, f.TanV,
            [pilot.RoomToRegion(At(-1, 1)), pilot.RoomToRegion(At(1, 1)), pilot.RoomToRegion(At(1, -1)), pilot.RoomToRegion(At(-1, -1))]);
        return (scene ?? new OverlayScene()) with { Beams = beams, Preview = preview };
    }

    /// <summary>The desktop's key: the window's view as it is (CaptureApi's plain picture).</summary>
    public void Desktop(Sojourn.Session.Assistant.CaptureApi capture)
    {
        if (Interlocked.Exchange(ref _taking, 1) == 1) return;
        if (s.Client is { } c) sounds()?.Trigger(SnapshotSound, c.Self.AgentID, 1, Sojourn.Session.Audio.WorldSounds.Kind.Ui, null);
        _ = Task.Run(async () =>
        {
            try { say($"picture: {await capture.ShutterAsync(CancellationToken.None)}"); }
            catch (Exception e) { say("picture not taken: " + e.Message); }
            finally { _taking = 0; }
        });
    }

    static Vector4 C(string hex, float a)
    {
        var k = SkiaSharp.SKColor.TryParse(hex, out var v) ? v : SkiaSharp.SKColors.Gray;
        return new Vector4(k.Red / 255f, k.Green / 255f, k.Blue / 255f, a);
    }
}
