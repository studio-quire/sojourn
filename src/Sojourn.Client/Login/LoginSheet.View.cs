using System.Numerics;
using Sojourn.Client.Assistant;
using Sojourn.Client.Xr;
using Sojourn.Render.Realtime;

namespace Sojourn.Client.Login;

public sealed partial class LoginSheet
{
    // ---- the desktop: in the middle of the window, the mouse on its keys ----

    /// <summary>The sheet over the view this frame, in the middle of the eye image; mouse: the cursor in the image's
    /// pixels (null: not over the window).</summary>
    public OverlayScene? Desktop(Vector2? mouse, int ew, int eh)
    {
        float scale = Math.Clamp(eh / 820f, 0.8f, 1.6f);
        if (Picture(scale, layer: false) is not { } pic) return null;
        var at = new Vector2(MathF.Round((ew - pic.Image.Width) / 2f), MathF.Round(Math.Max(16, (eh - pic.Image.Height) / 2f)));
        int key = -1;
        if (mouse is { } m)
        {
            var logical = (m - at) / pic.Scale;
            if (logical.X >= 0 && logical.Y >= 0 && logical.X < pic.W && logical.Y < pic.H) key = KeyAt(logical);
        }
        Hover(key);
        return new OverlayScene { Panel = pic.Image, PanelAt = at };
    }

    bool _mouseReal;

    /// <summary>The left button went down in the window (the sheet is up); real: a hand on the mouse (RealInput, read
    /// while the press is handled; the grid's questions take only that).</summary>
    public void MouseDown(bool real) { _mouseReal = real; PressDown(_hover); }

    public void MouseUp() => PressUp(_hover, _mouseReal);

    // ---- the headset: the menu's place, either hand's ray and trigger ----

    readonly PanelPlacement _place = new("login");
    readonly bool[] _trigger = new bool[2], _armedHand = { false, false }, _grabbing = new bool[2];
    readonly int[] _downOn = { -1, -1 };
    bool _rayOn;

    /// <summary>The headset's keyboard came up or went (its rays are its own).</summary>
    public void Recentred() => _place.BringToFront();

    /// <summary>One headset frame: the sheet on the panel's layer where it floats, the rays on it (the right hand, or
    /// the left), its presses and drags. toRegion: room to the frame's coordinates (the void's before a region).
    /// Returns what's drawn in the eyes: the rays and the sheet's frost.</summary>
    public OverlayScene? Headset(XrSession hs, XrFrame xf, XrControllers input, Func<Vector3, Vector3> toRegion, float dt)
    {
        if (Picture(UiSize.Raster, layer: GlassLight.Layers) is not { } pic)
        {
            hs.SetPanel(null, default, Quaternion.Identity, 0);
            return null;
        }
        var head = (xf.Eyes[0].Position + xf.Eyes[1].Position) * 0.5f;
        bool held = _trigger[0] || _trigger[1];
        var (pos, rot, size) = _place.Update(head, xf.Eyes[0].Orientation, PanelPlacement.Mode.Menu, pic.W, pic.H, dt,
            frozen: _rayOn || held, reaching: _place.HandNear(input.LeftHandPos, input.RightHandPos));
        hs.SetPanel(pic.Image, pos, rot, size.X);
        hs.PanelOnTop = true;   // as a confirmation: nothing covers it
        var corners = PanelPlacement.Corners(pos, rot, size).Select(toRegion).ToArray();

        var beams = new List<OverlayBeam>();
        var dots = new List<OverlayDot>();
        int hover = -1;
        _rayOn = false;
        for (int hand = 1; hand >= 0; hand--)
        {
            var aim = hand == 1 ? input.RightAim : input.LeftAim;
            var from = hand == 1 ? input.RightAimPos : input.LeftAimPos;
            float trigger = hand == 1 ? input.RightTrigger : input.LeftTrigger;
            if (!hs.Focused) { _armedHand[hand] = false; _trigger[hand] = false; continue; }
            if (trigger < 0.35f) _armedHand[hand] = true;
            if (aim is not { } q || from is not { } o) { _trigger[hand] = false; continue; }
            var dir = Vector3.Transform(-Vector3.UnitZ, q);
            bool on = PanelPlacement.Hit(o, dir, pos, rot, size, out float t, out var local);
            int key = -1;
            if (on)
            {
                var logical = new Vector2((local.X / size.X + 0.5f) * pic.W, (0.5f - local.Y / size.Y) * pic.H);
                key = KeyAt(logical);
                if (hover < 0) hover = key;
                _rayOn = true;
            }
            bool down = trigger > 0.6f && !_trigger[hand] && _armedHand[hand];
            bool up = trigger < 0.35f && _trigger[hand];
            if (down)
            {
                _trigger[hand] = true;
                _armedHand[hand] = false;
                _downOn[hand] = on ? key : -1;
                if (on && key >= 0) PressDown(key);
                else if (on) { _place.Grab(o, dir, t); _grabbing[hand] = true; }
            }
            else if (up)
            {
                _trigger[hand] = false;
                if (_grabbing[hand]) { _place.Drop(); _grabbing[hand] = false; }
                else if (_downOn[hand] >= 0) PressUp(on ? key : -1, real: true);
                _downOn[hand] = -1;
            }
            else if (_trigger[hand] && _grabbing[hand]) _place.Drag(o, dir);
            // the ray: drawn for the hand on the sheet, or the right one
            if (on || hand == 1)
            {
                var origin = toRegion(o);
                var end = on ? toRegion(o + dir * t) : toRegion(o + dir * 3);
                beams.Add(new OverlayBeam(origin + (end - origin) * 0.01f, end, 0.004f, PanelLook.InUse.Light(on ? 0.75f : 0.35f)));
                if (on) dots.Add(new OverlayDot(end, Math.Clamp(0.006f * t, 0.006f, 0.02f), PanelLook.InUse.Light(0.9f)));
            }
        }
        Hover(hover);
        return new OverlayScene { Beams = beams, Dots = dots, Panel = pic.Image, PanelCorners = corners, PanelContent = false };
    }
}
