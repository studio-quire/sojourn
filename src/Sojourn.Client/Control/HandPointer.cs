using Sojourn.Client.Xr;

namespace Sojourn.Client.Control;

/// <summary>
/// The headset's one pointer, on the right hand (Claude Opus 5.5, 2026-10-26, from the user's two ideas after their
/// main-grid session: a pointer with an on/off toggle that decides whether menus can be clicked, since most of the time
/// what's pointed at doesn't matter; and no pointer by default, the assistant asking the person to point when it
/// can't tell what they mean, the interaction pointer moving to the right hand with its own toggle).
///
/// Both, as one: off by default, nothing is pointed at and nothing out there can be clicked; A turns it on and off
/// (saved). On, its ray shows, its trigger touches and sits on things in the world, presses pages, the panel, the chat
/// feed, the HUD board and page boards, and what it's on is "that" for the assistant. It comes up by itself, whatever
/// the toggle, for what can't wait on a button: a confirmation (always pressable), the settings menu while it's open,
/// and the assistant asking the person to point (its trigger then picks, and the pick goes back). The left hand points
/// at nothing now; its trigger is free. Jumping moved from A to the right stick pushed up (flying, the stick climbs and
/// sinks as before).
/// A quick press of A toggles it; held longer, it's on only while held, the toggle left as it was (the user,
/// 2026-10-29: so a quick look with the pointer doesn't leave it on by mistake).
/// </summary>
public sealed class HandPointer
{
    readonly VrSettings _s;
    readonly Action _save;
    readonly Action<string> _say;
    bool _aBefore, _held;
    long _aDownAt;
    const long QuickMs = 350;   // a press shorter than this toggles; longer is a hold

    public HandPointer(VrSettings settings, Action save, Action<string> say)
    {
        _s = settings;
        _save = save;
        _say = say;
    }

    /// <summary>The person's toggle, or A held.</summary>
    public bool On => _s.Pointer || _held;

    public void Set(bool on)
    {
        if (_s.Pointer == on) return;
        _s.Pointer = on;
        _save();
        _say(on ? "pointer on" : "pointer off");
    }

    /// <summary>The ray shows this frame: the toggle, or something that needs it.</summary>
    public bool Live(bool confirming, bool menu, bool asked) => On || confirming || menu || asked;

    /// <summary>A: a quick press toggles the pointer, a hold shows it while held; what the rest of the client reads as
    /// A (the jump) is the right stick pushed up, nearly straight.</summary>
    public XrControllers Buttons(XrControllers c)
    {
        long now = Environment.TickCount64;
        if (c.A && !_aBefore) { _aDownAt = now; _held = true; }
        else if (!c.A && _aBefore)
        {
            _held = false;
            if (now - _aDownAt < QuickMs) Set(!_s.Pointer);
        }
        _aBefore = c.A;
        c.A = c.RightStick.Y > 0.75f && MathF.Abs(c.RightStick.X) < 0.5f;
        return c;
    }

    /// <summary>The hands as the boards see them (the HUD board, page boards): the right while the ray is live, the
    /// left never.</summary>
    public static XrControllers Boards(XrControllers c, bool live)
    {
        c.LeftAim = null;
        c.LeftAimPos = null;
        c.LeftTrigger = 0;
        if (!live)
        {
            c.RightAim = null;
            c.RightAimPos = null;
            c.RightTrigger = 0;
        }
        return c;
    }

    bool _wait;

    /// <summary>The right hand put where the world's own touch reads its hand (DirectTouch, media in the world were
    /// written for the left), or no hand at all. A pull that began while the world didn't have the hand (picking for the
    /// assistant, pressing the panel, a confirmation) reads as released until it's let go, so it can't land on what the
    /// ray reaches after.</summary>
    public XrControllers World(XrControllers c, bool live)
    {
        if (!live) _wait = c.RightTrigger > 0.35f;
        else if (_wait && c.RightTrigger < 0.35f) _wait = false;
        c.LeftAim = live ? c.RightAim : null;
        c.LeftAimPos = live ? c.RightAimPos : null;
        c.LeftTrigger = live && !_wait ? c.RightTrigger : 0;
        return c;
    }
}
