using SkiaSharp;
using Sojourn.Client.Assistant;
using Sojourn.Client.Xr;
using Sojourn.Render.Realtime;
using static Sojourn.Session.Assistant.Panel;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Control;

/// <summary>
/// A keyboard in the headset, only when the person asks for it (the user's rule, 2026-10-26: optional, easy to put away,
/// never coming up by itself): from the keyboard key on a page board while the page has a text field focused, or the
/// one on the assistant's panel (a typed request); and, the one exception (2026-10-27), when the assistant asks the
/// person to check a word by typing (<see cref="Show"/>). It types straight into its target: a page gets each key as the
/// desktop's keyboard sends it, so nothing typed is logged or kept (a password typed into a page by the person goes
/// only to the page; the assistant reads fields without their values), and the assistant's request shows on its panel
/// as on the desktop. Never while a confirmation waits: hidden then, taking no ray, so no key press can land on one.
/// Placed as a desk keyboard: in front, low (36 degrees below the eyes, 0.55 m away), tilted to face you, following the
/// head lazily as every menu does, or put somewhere by dragging its strip (<see cref="PanelPlacement"/>); either hand's
/// ray and trigger type, a held ⌫ repeats; letters, shift (twice: caps), a symbols layer, ×.
/// Written by Claude Opus 5.5, 2026-10-26.
/// </summary>
public sealed class HeadsetKeyboard
{
    const float LogicalW = 720, Pad = 12, Strip = 46, KeyH = 56, Gap = 6, Raster = 1.4f;
    const float Distance = 0.55f, DownDeg = 36, WidthM = 0.5f;
    static readonly float Unit = (LogicalW - 2 * Pad - 10 * Gap) / 11;
    static readonly float LogicalH = Strip + 4 * KeyH + 3 * Gap + Pad;
    static readonly SKRect CloseKey = new(LogicalW - Pad - 34, 6, LogicalW - Pad, 40);

    enum Kind { Char, Shift, Back, Enter, Space, Symbols, Letters, Close }
    readonly record struct Key(Kind Kind, string Label, SKRect Rect);

    // rows of 11 units: (label, width in units); special keys by name
    static readonly (string, float)[][] LetterRows =
    {
        new[] { ("q", 1f), ("w", 1f), ("e", 1f), ("r", 1f), ("t", 1f), ("y", 1f), ("u", 1f), ("i", 1f), ("o", 1f), ("p", 1f), ("{back}", 1f) },
        new[] { ("a", 1f), ("s", 1f), ("d", 1f), ("f", 1f), ("g", 1f), ("h", 1f), ("j", 1f), ("k", 1f), ("l", 1f), ("{enter}", 2f) },
        new[] { ("{shift}", 2f), ("z", 1f), ("x", 1f), ("c", 1f), ("v", 1f), ("b", 1f), ("n", 1f), ("m", 1f), (",", 1f), (".", 1f) },
        new[] { ("{symbols}", 2f), ("@", 1f), ("{space}", 5f), ("'", 1f), ("-", 1f), ("?", 1f) },
    };
    static readonly (string, float)[][] SymbolRows =
    {
        new[] { ("1", 1f), ("2", 1f), ("3", 1f), ("4", 1f), ("5", 1f), ("6", 1f), ("7", 1f), ("8", 1f), ("9", 1f), ("0", 1f), ("{back}", 1f) },
        new[] { ("!", 1f), ("#", 1f), ("$", 1f), ("%", 1f), ("&", 1f), ("*", 1f), ("(", 1f), (")", 1f), ("/", 1f), ("{enter}", 2f) },
        new[] { (";", 1f), (":", 1f), ("\"", 1f), ("_", 1f), ("+", 1f), ("=", 1f), ("<", 1f), (">", 1f), ("[", 1f), ("]", 1f), ("\\", 1f) },
        new[] { ("{letters}", 2f), ("~", 1f), ("{space}", 5f), ("^", 1f), ("|", 1f), ("€", 1f) },
    };

    readonly Func<PanelLook> _look;
    readonly Action<string> _log;
    IKeyTarget? _target;
    bool _symbols, _shift, _caps;
    long _shiftAt;

    public HeadsetKeyboard(Func<PanelLook> look, Action<string> log)
    {
        _look = look;
        _log = log;
    }

    public bool Shown => _target != null;
    public bool IsFor(object owner) => _target != null && Equals(_target.Owner, owner);

    /// <summary>Brings the keyboard up for a target, or puts it away if it's already up for that one. Main thread.</summary>
    public void Toggle(IKeyTarget target)
    {
        if (_target != null && Equals(_target.Owner, target.Owner)) { Close(); return; }
        _target?.Closed();
        _target = target;
        (_symbols, _shift, _caps) = (false, false, false);
        _place = true;
        target.Opened();
        _log($"keyboard: up for {target.Label}");
    }

    /// <summary>Brings the keyboard up for a target, whatever it's up for now; already up for it, it stays. The one time
    /// it comes up without the person pressing its key: the assistant asking them to check a word by typing, its guess
    /// already in (AssistantHost.AskTyped; the user's call, 2026-10-27). Main thread.</summary>
    public void Show(IKeyTarget target)
    {
        if (!IsFor(target.Owner)) Toggle(target);
    }

    public void Close()
    {
        if (_target is not { } t) return;
        _target = null;
        EndAll();
        t.Closed();
        _log("keyboard: put away");
    }

    // ---- where it is ----

    /// <summary>Where it floats: following, or where the person put it.</summary>
    public readonly PanelPlacement Placement = new("keyboard", Rest, rangeDeg: 35);   // (~50 wide this near: carried past 35)
    Vector3 _pos;
    Quaternion _rot = Quaternion.Identity;
    bool _place, _visible;
    int _dragHand = -1;   // the hand dragging it by its strip
    static float Mpp => WidthM / LogicalW;

    public void Recentred() { if (Shown) _place = true; }

    /// <summary>Where it rests while it follows: 0.55 m out where the head faces, 36 degrees below the eyes, its face
    /// toward them (tilted back like a desk keyboard).</summary>
    static (Vector3, Quaternion) Rest(Vector3 anchor, float yaw, PanelPlacement.Mode mode, Vector2 size) =>
        PanelPlacement.Toward(anchor, yaw, -DownDeg * MathF.PI / 180, Distance);

    // ---- the hands ----

    readonly record struct Aim(bool On, float T, Vector2 Px, Vector3 Point, int Key);
    Aim _right, _left;
    Vector3? _rightFrom, _leftFrom;
    readonly bool[] _down = new bool[2], _armed = { true, true }, _owned = new bool[2];
    readonly int[] _pressed = { -1, -1 };
    readonly long[] _repeatAt = new long[2];
    int _flash = -1;
    long _flashUntil;
    List<Key> _keys = new();

    /// <summary>Each headset frame, before every other board and the panel: places the keyboard, types with the rays
    /// that are on it, and hands back the input without them (a ray on the keyboard is the keyboard's). hidden: a
    /// confirmation waits or the menu is up.</summary>
    public XrControllers Frame(XrControllers input, Vector3 head, Quaternion headRot, bool hidden, bool focused, float dt)
    {
        (_rightFrom, _leftFrom) = (input.RightAimPos, input.LeftAimPos);
        bool wasOn = _right.On || _left.On || _dragHand >= 0;
        _right = _left = default;
        _owned[0] = _owned[1] = false;
        if (_target is { Alive: false }) Close();
        if (_target == null) { _visible = false; return input; }
        // placed afresh each time it comes up (asked for, or back after the menu, a confirmation or X): where it was put
        // if that's in view, else following in front
        bool visible = !hidden;
        if (visible && (!_visible || _place)) { Placement.Place(); _place = false; }
        if (!visible && _visible) { Placement.CancelGrab(); _dragHand = -1; }
        _visible = visible;
        if (visible) (_pos, _rot) = Placement.Update(head, headRot, new Vector2(WidthM, LogicalH * Mpp), dt, frozen: wasOn);
        if (hidden || !focused)
        {
            _armed[0] = _armed[1] = false;   // a trigger held over from a confirmation waits for a release
            EndAll();
            return input;
        }
        _keys = Layout();
        _right = AimOf(input.RightAim, input.RightAimPos);
        _left = AimOf(input.LeftAim, input.LeftAimPos);
        Hand(1, input.RightTrigger, _right, input.RightAim, input.RightAimPos);
        Hand(0, input.LeftTrigger, _left, input.LeftAim, input.LeftAimPos);
        _owned[1] = _right.On || _down[1] && _pressed[1] >= 0 || _dragHand == 1;
        _owned[0] = _left.On || _down[0] && _pressed[0] >= 0 || _dragHand == 0;
        if (_owned[1]) { input.RightAim = null; input.RightTrigger = 0; }
        if (_owned[0]) { input.LeftAim = null; input.LeftTrigger = 0; }
        return input;
    }

    Aim AimOf(Quaternion? q, Vector3? from)
    {
        if (q is not { } rot || from is not { } o) return default;
        var dir = Vector3.Transform(-Vector3.UnitZ, rot);
        var nrm = Vector3.Transform(Vector3.UnitZ, _rot);
        float denom = Vector3.Dot(dir, nrm);
        if (denom > -1e-4f) return default;
        float t = Vector3.Dot(_pos - o, nrm) / denom;
        if (t <= 0) return default;
        var p = o + dir * t - _pos;
        var local = new Vector2(Vector3.Dot(p, Vector3.Transform(Vector3.UnitX, _rot)), Vector3.Dot(p, Vector3.Transform(Vector3.UnitY, _rot)));
        float hm = LogicalH * Mpp;
        if (MathF.Abs(local.X) > WidthM / 2 || MathF.Abs(local.Y) > hm / 2) return default;
        var px = new Vector2((local.X / WidthM + 0.5f) * LogicalW, (0.5f - local.Y / hm) * LogicalH);
        int key = _keys.FindIndex(k => k.Rect.Contains(px.X, px.Y));
        return new Aim(true, t, px, o + dir * t, key);
    }

    void Hand(int hand, float trigger, Aim aim, Quaternion? rot, Vector3? from)
    {
        long now = Environment.TickCount64;
        if (trigger < 0.35f) _armed[hand] = true;
        var dir = rot is { } r ? Vector3.Transform(-Vector3.UnitZ, r) : default;
        if (!_down[hand] && trigger > 0.6f && _armed[hand])
        {
            _down[hand] = true;
            _armed[hand] = false;
            _pressed[hand] = aim.On ? aim.Key : -1;
            if (aim.On && aim.Key >= 0)
            {
                Press(_keys[aim.Key]);
                _repeatAt[hand] = now + 450;
            }
            else if (aim.On && aim.Px.Y < Strip && _dragHand < 0 && from is { } o && rot != null)
            {
                // the strip beside its label: it can be dragged somewhere
                _dragHand = hand;
                Placement.Grab(o, dir, aim.T);
            }
        }
        else if (_down[hand] && trigger < 0.35f)
        {
            _down[hand] = false;
            _pressed[hand] = -1;
            if (_dragHand == hand) { Placement.Drop(); _dragHand = -1; }
        }
        else if (_down[hand] && _dragHand == hand && from is { } o2 && rot != null)
        {
            if (Placement.Drag(o2, dir)) (_pos, _rot) = Placement.Pose;
        }
        else if (_down[hand] && _pressed[hand] is >= 0 and var k && k < _keys.Count && _keys[k].Kind == Kind.Back && aim.Key == k && now >= _repeatAt[hand])
        {
            Press(_keys[k]);
            _repeatAt[hand] = now + 70;
        }
    }

    void Press(Key k)
    {
        if (_target is not { } t) return;
        _flash = _keys.IndexOf(k);
        _flashUntil = Environment.TickCount64 + 140;
        switch (k.Kind)
        {
            case Kind.Char:
                t.Char(k.Label[0]);
                if (_shift && !_caps) _shift = false;
                break;
            case Kind.Space: t.Char(' '); break;
            case Kind.Back: t.Backspace(); break;
            case Kind.Enter:
                if (t.Enter()) Close();
                break;
            case Kind.Shift:
                long now = Environment.TickCount64;
                if (_caps) (_shift, _caps) = (false, false);
                else if (_shift && now - _shiftAt < 450) _caps = true;
                else _shift = !_shift;
                _shiftAt = now;
                break;
            case Kind.Symbols: _symbols = true; break;
            case Kind.Letters: _symbols = false; break;
            case Kind.Close: Close(); break;
        }
    }

    void EndAll()
    {
        _down[0] = _down[1] = false;
        _pressed[0] = _pressed[1] = -1;
        if (_dragHand >= 0) Placement.CancelGrab();
        _dragHand = -1;
    }

    List<Key> Layout()
    {
        var keys = new List<Key> { new(Kind.Close, "close", CloseKey) };
        var rows = _symbols ? SymbolRows : LetterRows;
        bool upper = _shift || _caps;
        for (int r = 0; r < rows.Length; r++)
        {
            float x = Pad, y = Strip + r * (KeyH + Gap);
            foreach (var (label, units) in rows[r])
            {
                float w = units * Unit + (units - 1) * Gap;
                var rect = new SKRect(x, y, x + w, y + KeyH);
                x += w + Gap;
                keys.Add(label switch
                {
                    "{back}" => new Key(Kind.Back, "", rect),
                    "{enter}" => new Key(Kind.Enter, "", rect),
                    "{shift}" => new Key(Kind.Shift, "", rect),
                    "{space}" => new Key(Kind.Space, "", rect),
                    "{symbols}" => new Key(Kind.Symbols, "123", rect),
                    "{letters}" => new Key(Kind.Letters, "abc", rect),
                    _ => new Key(Kind.Char, upper && !_symbols ? label.ToUpperInvariant() : label, rect),
                });
            }
        }
        return keys;
    }

    // ---- what shows ----

    (PanelLook Look, string State, OverlayImage Image)? _drawn;
    long _version;

    /// <summary>Its layer this frame, as a room pose and a width in metres; null when it's away or hidden.</summary>
    public (OverlayImage Image, Vector3 Pos, Quaternion Rot, float Width)? Layer(bool hidden)
    {
        if (_target is not { } t || hidden) return null;
        var look = _look();
        int lit1 = _right.On ? _right.Key : -1, lit2 = _left.On ? _left.Key : -1;
        int flash = Environment.TickCount64 < _flashUntil ? _flash : -1;
        string state = $"{t.Label}|{Lang.Code}|{_symbols}|{_shift}|{_caps}|{lit1}|{lit2}|{flash}";
        if (_drawn is not { } d || !ReferenceEquals(d.Look, look) || d.State != state)
            _drawn = d = (look, state, Picture(look, Lang.T(t.Label), i => i == lit1 || i == lit2, flash, Raster));
        return (d.Image, _pos, _rot, WidthM);
    }

    OverlayImage Picture(PanelLook look, string label, Func<int, bool> lit, int flash, float scale)
    {
        var keys = Layout();
        var (pixels, w, h, _) = Rasterise(scale, LogicalW, new List<PanelKey>(), (canvas, height) =>
        {
            if (canvas != null) Draw(canvas, look, keys, label, lit, flash);
            return LogicalH;
        });
        return new OverlayImage(pixels, w, h, Interlocked.Increment(ref _version));
    }

    /// <summary>`--panel-shots`: the keyboard in each look (letters, one key lit) and its symbols and shifted layers.</summary>
    public static void WriteShots(string dir, SKBitmap? bg, float scale)
    {
        var kb = new HeadsetKeyboard(() => new PanelLook(), _ => { });
        void Shot(string name, PanelLook look, bool symbols, bool shift)
        {
            (kb._symbols, kb._shift) = (symbols, shift);
            kb._keys = kb.Layout();
            int lit = kb._keys.FindIndex(k => k.Label is "g" or "G" or "5");
            var img = kb.Picture(look, "Sojourn media test", i => i == lit, -1, scale);
            using var surface = SKSurface.Create(new SKImageInfo(img.Width + 80, img.Height + 80, SKColorType.Rgba8888, SKAlphaType.Premul));
            var c = surface.Canvas;
            if (bg != null) c.DrawBitmap(bg, new SKRect(0, 0, img.Width + 80, img.Height + 80));
            else c.Clear(new SKColor(90, 100, 110));
            using var pic = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(img.Rgba, 0, pic.GetPixels(), img.Rgba.Length);
            c.DrawBitmap(pic, 40, 40);
            PanelShots.Save(surface, Path.Combine(dir, $"keyboard-{name}.png"));
        }
        foreach (var look in PanelLook.Presets) Shot(look.Preset, look, false, false);
        Shot("shifted", new PanelLook(), false, true);
        Shot("symbols", new PanelLook(), true, false);
    }

    void Draw(SKCanvas canvas, PanelLook look, List<Key> keys, string label, Func<int, bool> lit, int flash)
    {
        var ink = Ink.For(look);
        var draw = new Painter(canvas, ink, LogicalW);
        draw.Body(LogicalH);
        var lf = Font(ink.Regular, 15);
        draw.Text(Ellipsize(label, lf, CloseKey.Left - Pad - 16), Pad + 6, Strip / 2 + 5, lf, ink.Dim);
        for (int i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            bool on = lit(i) || i == flash || k.Kind == Kind.Shift && (_shift || _caps);
            switch (k.Kind)
            {
                case Kind.Close:
                    draw.GlyphKey(k.Rect, Painter.Glyph.Close, on);
                    break;
                case Kind.Char or Kind.Symbols or Kind.Letters:
                    draw.Key(k.Rect, k.Label, on, k.Kind == Kind.Char ? 24 : 17);
                    break;
                default:
                    draw.Key(k.Rect, "", on);
                    Symbol(canvas, k, ink.Text);
                    break;
            }
        }
    }

    /// <summary>The keys without letters, drawn: ⌫, ↵, ⇧ (filled while caps lock is on), a space bar's dash.</summary>
    void Symbol(SKCanvas c, Key k, SKColor ink)
    {
        using var p = new SKPaint { Color = ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
        float cx = k.Rect.MidX, cy = k.Rect.MidY, s = KeyH * 0.2f;
        using var path = new SKPath();
        switch (k.Kind)
        {
            case Kind.Back:
                path.MoveTo(cx - s * 1.3f, cy);
                path.LineTo(cx - s * 0.5f, cy - s * 0.8f);
                path.LineTo(cx + s * 1.2f, cy - s * 0.8f);
                path.LineTo(cx + s * 1.2f, cy + s * 0.8f);
                path.LineTo(cx - s * 0.5f, cy + s * 0.8f);
                path.Close();
                c.DrawPath(path, p);
                c.DrawLine(cx - s * 0.05f, cy - s * 0.35f, cx + s * 0.65f, cy + s * 0.35f, p);
                c.DrawLine(cx - s * 0.05f, cy + s * 0.35f, cx + s * 0.65f, cy - s * 0.35f, p);
                break;
            case Kind.Enter:
                path.MoveTo(cx + s * 1.1f, cy - s * 0.9f);
                path.LineTo(cx + s * 1.1f, cy + s * 0.2f);
                path.LineTo(cx - s * 1.1f, cy + s * 0.2f);
                c.DrawPath(path, p);
                c.DrawLine(cx - s * 1.1f, cy + s * 0.2f, cx - s * 0.5f, cy - s * 0.35f, p);
                c.DrawLine(cx - s * 1.1f, cy + s * 0.2f, cx - s * 0.5f, cy + s * 0.75f, p);
                break;
            case Kind.Shift:
                path.MoveTo(cx, cy - s * 1.1f);
                path.LineTo(cx + s * 1.0f, cy);
                path.LineTo(cx + s * 0.45f, cy);
                path.LineTo(cx + s * 0.45f, cy + s * 0.9f);
                path.LineTo(cx - s * 0.45f, cy + s * 0.9f);
                path.LineTo(cx - s * 0.45f, cy);
                path.LineTo(cx - s * 1.0f, cy);
                path.Close();
                if (_caps) using (var fill = new SKPaint { Color = ink, IsAntialias = true }) c.DrawPath(path, fill);
                c.DrawPath(path, p);
                break;
            case Kind.Space:
                c.DrawLine(cx - s * 2.2f, cy + s * 0.5f, cx + s * 2.2f, cy + s * 0.5f, p);
                break;
        }
    }

    static Vector4 Cue => PanelLook.InUse.Light(0.9f);

    /// <summary>The rays' cue on it: a beam to where they point.</summary>
    public OverlayScene? Decorate(OverlayScene? scene, VrPilot pilot) => Decorate(scene, pilot.RoomToRegion);

    /// <summary>The same, room points mapped by toRegion (the login screen's void, before there's a pilot).</summary>
    public OverlayScene? Decorate(OverlayScene? scene, Func<Vector3, Vector3> toRegion)
    {
        if (_target == null) return scene;
        var beams = new List<OverlayBeam>(scene?.Beams ?? []);
        beams.AddRange(Placement.Outline(toRegion));
        bool any = Placement.Dragging;
        foreach (var (aim, from) in new[] { (_right, _rightFrom), (_left, _leftFrom) })
        {
            if (!aim.On || from is not { } o) continue;
            var a = toRegion(o);
            var b = toRegion(aim.Point);
            beams.Add(new OverlayBeam(a + Vector3.Normalize(b - a) * 0.04f, b, 0.003f, Cue with { W = 0.55f }));
            any = true;
        }
        return any ? (scene ?? new OverlayScene()) with { Beams = beams } : scene;
    }

    /// <summary>For the scripted test (Xr/VrScript.Keyboard.cs): a key's middle in the room ("a", "back", "enter",
    /// "shift", "space", "123", "abc", "close").</summary>
    public Vector3? KeyPoint(string name)
    {
        if (_target == null) return null;
        var keys = Layout();
        var k = keys.FirstOrDefault(k => k.Kind switch
        {
            Kind.Char or Kind.Symbols or Kind.Letters => k.Label.Equals(name, StringComparison.OrdinalIgnoreCase),
            _ => k.Kind.ToString().Equals(name, StringComparison.OrdinalIgnoreCase),
        });
        if (k.Label == null) return null;
        return _pos + Vector3.Transform(new Vector3((k.Rect.MidX - LogicalW / 2) * Mpp, (LogicalH / 2 - k.Rect.MidY) * Mpp, 0), _rot);
    }
}
