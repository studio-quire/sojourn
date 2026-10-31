using Sojourn.Client.Assistant;
using Sojourn.Client.Xr;
using Vector2 = System.Numerics.Vector2;

namespace Sojourn.Client.Control;

/// <summary>
/// Something the right stick can move while the right ray is on it. Scroll: smooth, by a fraction of its own view
/// (down: positive further down; right: positive further right), for a sheet that scrolls. Turn: a page (+1 next, -1
/// back), for a sheet that pages; true if it turned. Key: which sheet it is. Vertical: it takes only pushes up and
/// down, a sideways one still turning you (the camera's zoom).
/// </summary>
public sealed record StickSheet(object Key, Action<float, float>? Scroll = null, Func<int, bool>? Turn = null, bool Vertical = false);

/// <summary>
/// The right stick on a sheet (the user's idea from the headset, 2026-10-27: pointing at a menu that can scroll, the
/// right thumbstick scrolls it). While the right ray is on something that scrolls or pages now (the reader, a paged
/// answer, the settings menu's pages, the chat feed when it holds more than it shows, a page up close or on a HUD or a
/// face; a map, where it zooms), the stick is that sheet's: smooth scrolling where it scrolls (the rate growing with the tilt past a dead
/// zone), a page per flick where it pages (repeating while held), up toward the top as a mouse wheel rolled away goes.
/// Left and right page sideways where a sheet has ‹ › keys and scroll a page sideways; on the reader and the feed they
/// do nothing. Meanwhile the rest of the client reads the stick as centred: no turning, no jump, no climbing.
///
/// A push belongs to where it began, until the stick comes back to the middle: one that began on a sheet keeps moving
/// it (and never turns you) if the ray slips off; one that began elsewhere (turning) carries on turning when the ray
/// comes onto a sheet. Claude Opus 5.5, 2026-10-27.
/// </summary>
public sealed class SheetStick
{
    const float Rest = 0.12f;          // below this on both axes the stick is in the middle
    const float Dead = 0.15f;          // smooth scrolling starts past this
    const float ViewsPerSecond = 1.8f; // at full tilt
    const float Flick = 0.6f, Rearm = 0.3f;
    const double RepeatAfter = 0.45, RepeatEvery = 0.25;

    StickSheet? _under, _held;
    bool _free = true;     // the stick has been in the middle since a push the sheets didn't take
    Vector2 _stick;
    bool _flickArmed = true;
    int _flick;
    double _clock, _repeatAt;

    /// <summary>Each headset frame, before anything reads the stick: takes it for the sheet under the ray (last frame's
    /// hit tests; a frame late is nothing to the hand) and gives the rest of the client a centred stick while it does.</summary>
    public XrControllers Take(XrControllers c)
    {
        _stick = c.RightStick;
        bool middle = MathF.Abs(_stick.X) < Rest && MathF.Abs(_stick.Y) < Rest;
        if (middle) { _held = null; _free = true; _flickArmed = true; }
        else if (_held == null)
        {
            if (_free && _under != null && !(_under.Vertical && MathF.Abs(_stick.X) > MathF.Abs(_stick.Y))) { _held = _under; _flickArmed = true; }
            else _free = false;   // the turn's push, until it's let go
        }
        if (_held != null) c.RightStick = default;
        return c;
    }

    /// <summary>Each headset frame, after the hit tests: what the right ray is on now, if it scrolls or pages, and the
    /// held sheet moved by this frame's stick.</summary>
    public void Frame(StickSheet? under, float dt)
    {
        _clock += dt;
        _under = under;
        if (_held is not { } s) return;
        // the sheet under the ray now, if it's the same one: its delegates are this frame's
        if (under != null && Equals(under.Key, s.Key)) _held = s = under;
        if (s.Scroll is { } scroll)
        {
            // the larger axis only: a push up that leans a little doesn't wander sideways
            bool vertical = s.Vertical || MathF.Abs(_stick.Y) >= MathF.Abs(_stick.X);
            float v = vertical ? _stick.Y : _stick.X;
            float a = MathF.Abs(v);
            if (a > Dead)
            {
                float rate = MathF.Pow((a - Dead) / (1 - Dead), 1.6f) * ViewsPerSecond * dt * MathF.Sign(v);
                if (vertical) scroll(-rate, 0); else scroll(0, rate);
            }
        }
        else if (s.Turn is { } turn)
        {
            bool vertical = MathF.Abs(_stick.Y) >= MathF.Abs(_stick.X);
            float v = vertical ? _stick.Y : _stick.X;
            if (MathF.Abs(v) < Rearm) { _flickArmed = true; return; }
            if (MathF.Abs(v) < Flick) return;
            // down or right: the next page; up or left: the one before
            int flick = vertical ? (v > 0 ? -1 : 1) : (v > 0 ? 1 : -1);
            if (_flickArmed)
            {
                _flickArmed = false;
                _flick = flick;
                _repeatAt = _clock + RepeatAfter;
                turn(flick);
            }
            // held: it repeats; rolled round to another way, it waits to be let go (a roll isn't a flick)
            else if (flick == _flick && _clock >= _repeatAt)
            {
                _repeatAt = _clock + RepeatEvery;
                turn(flick);
            }
        }
    }

    /// <summary>The sheet under the right ray this frame, in the order the ray meets them: the reader, the page board and
    /// the HUD board (both media's), a map (zoomed), the panel or the settings menu, the chat feed, then a page out in the
    /// world. Only what scrolls or pages now: elsewhere the stick does what it always does.</summary>
    public static StickSheet? Find(ReaderView reader, bool onPanel, SettingsMenu menu, AssistantHost assistant, Social.SocialView social,
        Media.MediaWiring? media, Hud.HudBoard hud, Map.MapWiring map, bool worldHand, Shutter? camera = null)
    {
        if (reader.RightOn)
            return reader.Reader.Position.Long ? new StickSheet(reader, Scroll: (down, _) => reader.Reader.ScrollViews(down)) : null;
        if (media?.RightOnBoard == true) return media.BoardSheet();
        if (hud.RightOnBoard) return media?.HudSheet(hud.RightHit);
        if (map.Sheet() is { } m) return m;
        if (onPanel)
        {
            if (menu.Open) return menu.Paged ? new StickSheet(menu, Turn: menu.Turn) : null;
            return assistant.Paged ? new StickSheet(assistant, Turn: assistant.Turn) : null;
        }
        if (social.OnSheet) return social.Scrollable ? new StickSheet(social, Scroll: (down, _) => social.ScrollViews(down)) : null;
        // the camera out: up and down zoom it, unless the ray is on a page in the world
        return (worldHand ? media?.WorldSheet() : null) ?? camera?.StickZoom;
    }
}
