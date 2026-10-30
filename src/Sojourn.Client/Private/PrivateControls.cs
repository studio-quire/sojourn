using System.Numerics;
using LibreMetaverse;
using Sojourn.Client.Assistant;
using Sojourn.Client.Control;
using Sojourn.Client.Xr;
using Sojourn.Core.Agents;
using Sojourn.Render.Realtime;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Sojourn.Client.Private;

/// <summary>
/// Incognito in the person's hands (docs/assistant.md > Incognito; "private mode" in the code): knowing it's on (its
/// mark, a hat and glasses, on the panel; a token lit in the accent at the left wrist in the headset, beside the mic
/// token at the bottom of the window on the desktop), turning it on and off (the settings menu's incognito section, P
/// on the desktop, the token: pressed, it ends incognito), "let it see", its automatic triggers
/// (<see cref="Triggers"/>) and dictation (<see cref="Dictation"/>). Turning it off, and letting the assistant see
/// while it's on, take the person's own hand: the token pressed by the controller or a real click, the menu's switches
/// by a real click, P as a real key; nothing a program posts or injects, and nothing on the agent API.
/// </summary>
public sealed class PrivateControls
{
    readonly AgentSession _s;
    readonly AssistantHost _assistant;
    readonly SettingsMenu _menu;
    readonly bool _headset;
    readonly VrSettings _settings;
    readonly Action _save;

    public Triggers Triggers { get; }
    public Dictation Dictation { get; }

    PrivateControls(AgentSession s, AssistantHost assistant, SettingsMenu menu, bool headset, Triggers triggers, Dictation dictation,
        VrSettings settings, Action save)
    {
        _s = s;
        _settings = settings;
        _save = save;
        _assistant = assistant;
        _menu = menu;
        _headset = headset;
        Triggers = triggers;
        Dictation = dictation;
    }

    public static PrivateControls Start(AgentSession s, PrivateWiring wiring, AssistantHost assistant, Social.SocialView social, SettingsMenu menu,
        VrSettings settings, Action save, bool headset)
    {
        var triggers = new Triggers(s, settings, save);
        var dictation = new Dictation(s, assistant, social, wiring.Api);
        s.Private.SetSees(settings.IncognitoSees);
        var c = new PrivateControls(s, assistant, menu, headset, triggers, dictation, settings, save);
        wiring.Api.Remember = triggers.Remember;
        triggers.OpenImWith = dictation.OpenImWith;
        triggers.AnyImOpen = () => dictation.Destination().To != DraftTo.Nearby;
        triggers.Changed += menu.Changed;
        wiring.Situation.OpenConversation = () => dictation.Destination().Session;
        menu.PrivateRows = c.MenuRows;

        assistant.Panel.Update(p => (p.Private, p.PrivateSees) = (s.Private.On, s.Private.Sees), show: false, news: false);
        s.Private.Changed += (on, _, _) =>
        {
            // shown a moment either way: the mark on the panel, or gone from it
            assistant.Panel.Update(p => (p.Private, p.PrivateSees) = (on, s.Private.Sees));
            menu.Changed();
        };
        s.Private.SeesChanged += sees =>
        {
            assistant.Panel.Update(p => p.PrivateSees = sees, show: s.Private.On);
            menu.Changed();
        };

        return c;
    }

    /// <summary>On the agent API (SojournSettings): on or off, let it see, and the IM trigger (the old names, private and
    /// private_while_im, still answer); never the lists (they name people and places, and settings are read whole while
    /// blind).</summary>
    public void AddSettings(SettingsApi api)
    {
        api.Add("incognito", new SettingsApi.Setting("incognito", "incognito: nothing from the time it's on is kept, and unless incognito_sees is on "
            + "the assistant learns only the general picture (SojournPrivate); it can be turned on here, and only the person turns it off",
            "on (off is the person's own)",
            () => _s.Private.On ? "on" : "off",
            v =>
            {
                bool want = false;
                if (SettingsApi.Switch("incognito", on => want = on)(v) is { } bad) return bad;
                if (!want) return "incognito: the person turns incognito off themselves";
                _s.Private.Set(true, "api");
                return null;
            }));
        api.Add("incognito_sees", new SettingsApi.Setting("incognito", "let it see: while incognito is on the assistant sees and helps with "
            + "specifics as usual (nothing from the time is kept all the same); off, it gets only the general picture. While incognito is on, only "
            + "the person turns this on", "on, off",
            () => _s.Private.Sees ? "on" : "off",
            v =>
            {
                bool want = false;
                if (SettingsApi.Switch("incognito_sees", on => want = on)(v) is { } bad) return bad;
                if (want && _s.Private.On) return "incognito_sees: while incognito is on, the person turns this on themselves (the settings menu)";
                SetSees(want);
                return null;
            }));
        api.Add("incognito_while_im", new SettingsApi.Setting("incognito", "incognito comes on by itself while an IM conversation is open on the chat feed",
            "on, off", () => Triggers.WhileIm ? "on" : "off", SettingsApi.Switch("incognito_while_im", Triggers.SetWhileIm)));
        api.Alias("private", "incognito");
        api.Alias("private_while_im", "incognito_while_im");
    }

    void Toggle(string why, bool real)
    {
        if (!_s.Private.On) _s.Private.Set(true, why);
        else if (real) _s.Private.Set(false, why);
    }

    void SetSees(bool sees)
    {
        if (!_s.Private.SetSees(sees)) return;
        _settings.IncognitoSees = sees;
        _save();
    }

    // --- the settings menu's incognito section ---------------------------------------------------------------------------

    readonly Dictionary<string, double> _removed = new();   // rows taken off stay a minute as off switches, to undo a slip
    readonly Dictionary<string, (string Label, string? Note, Action On)> _removedRows = new();

    List<(MenuRow Row, Action<int> Act)> MenuRows()
    {
        var rows = new List<(MenuRow, Action<int>)>
        {
            (MenuRow.Switch("incognito", _s.Private.On), _ => Toggle("menu", _headset || _lastDownReal)),
            // letting it see during a spell takes the person's own hand, as ending one does
            (MenuRow.Switch("let the assistant see", _s.Private.Sees), _ =>
            {
                if (!_s.Private.Sees && _s.Private.On && !(_headset || _lastDownReal)) return;
                SetSees(!_s.Private.Sees);
            }),
        };
        var here = Triggers.Here();
        var places = Triggers.Places.ToList();
        if (here is { } h)
        {
            bool on = places.Any(p => p.Is(h.Region, h.Parcel));
            rows.Add((MenuRow.Switch("always here", on, note: h.Name.Length > 0 ? h.Name : null), _ => Triggers.SetHere(!on)));
        }
        foreach (var p in places.Where(p => here is not { } hh || !p.Is(hh.Region, hh.Parcel)))
        {
            var place = p;
            string label = "always at " + (p.Name.Length > 0 ? p.Name : p.RegionName);
            rows.Add((MenuRow.Switch(label, true, note: p.Name.Length > 0 ? p.RegionName : null), _ =>
            {
                Triggers.SetPlace(place.Region, place.Parcel, false);
                Removed("place:" + place.Region + "/" + place.Parcel, label, p.Name.Length > 0 ? p.RegionName : null,
                    () => Triggers.SetPlace(place.Region, place.Parcel, true, place.Name, place.RegionName));
            }));
        }
        var listed = new HashSet<string>();
        foreach (var p in Triggers.People.ToList())
        {
            if (!UUID.TryParse(p.Id, out var id)) continue;
            listed.Add(p.Id);
            string label = "always with " + (_s.Names.DisplayName(id) ?? p.Name);
            rows.Add((MenuRow.Switch(label, true), _ =>
            {
                Triggers.SetPerson(id, false);
                Removed("person:" + id, label, null, () => Triggers.SetPerson(id, true));
            }));
        }
        // a slip undone: what was just taken off stays as an off switch for a minute
        double now = Environment.TickCount64 / 1000.0;
        foreach (var key in _removed.Where(kv => now - kv.Value > 60).Select(kv => kv.Key).ToList()) { _removed.Remove(key); _removedRows.Remove(key); }
        foreach (var (key, r) in _removedRows.ToList())
        {
            if (key.StartsWith("person:") && listed.Contains(key[7..])) continue;
            if (key.StartsWith("place:") && places.Any(p => key == "place:" + p.Region + "/" + p.Parcel)) continue;
            rows.Add((MenuRow.Switch(r.Label, false, note: r.Note), _ => { r.On(); _removed.Remove(key); _removedRows.Remove(key); }));
        }
        // people within chat range, to keep it with
        foreach (var (id, name, _) in Triggers.Around.Where(a => !listed.Contains(a.Id.ToString()) && !_removedRows.ContainsKey("person:" + a.Id)).Take(4))
            rows.Add((MenuRow.Switch("always with " + name, false), _ => Triggers.SetPerson(id, true)));
        rows.Add((MenuRow.Switch("while an IM is open", Triggers.WhileIm), _ => Triggers.SetWhileIm(!Triggers.WhileIm)));
        return rows;
    }

    void Removed(string key, string label, string? note, Action undo)
    {
        _removed[key] = Environment.TickCount64 / 1000.0;
        _removedRows[key] = (label, note, undo);
    }

    // --- the desktop: P, and the token at the bottom of the window -------------------------------------------------------

    bool _lastDownReal, _tokenDown, _overToken;

    /// <summary>The desktop's keys (none while typing): P turns incognito on, and off with a real key; Y held
    /// dictates; Enter and Esc send or cancel a waiting draft. True if taken.</summary>
    public bool KeyDown(Silk.NET.Input.Key key)
    {
        switch (key)
        {
            case Silk.NET.Input.Key.P:
                Toggle("key", RealKeys.Down(RealKeys.P));
                return true;
            case Silk.NET.Input.Key.Y:
                Dictation.Down();
                return true;
        }
        return Dictation.Key(key);
    }

    public void KeyUp(Silk.NET.Input.Key key)
    {
        if (key == Silk.NET.Input.Key.Y) Dictation.Up();
    }

    /// <summary>The mouse over the desktop token (a click there is the token's, not the world's).</summary>
    public bool DesktopOver => _overToken;

    public void MouseDown(Silk.NET.Input.MouseButton b)
    {
        if (b != Silk.NET.Input.MouseButton.Left) return;
        _lastDownReal = RealInput.LeftButton();
        _tokenDown = _overToken && _lastDownReal;
    }

    public void MouseUp(Silk.NET.Input.MouseButton b)
    {
        if (b != Silk.NET.Input.MouseButton.Left) return;
        if (_tokenDown && _overToken && _s.Private.On) _s.Private.Set(false, "wrist");
        _tokenDown = false;
    }

    /// <summary>The desktop's token while incognito: its mark on a disc lit in the accent, at the bottom middle of
    /// the window just left of voice's mic token, the same size.</summary>
    public OverlayScene? Desktop(OverlayScene? scene, Vector3 eye, Vector3 forward, float vfov, int ew, int eh, Vector2? mouseInWindow,
        Silk.NET.Maths.Vector2D<int> window, bool stereo)
    {
        Vector2? mouse = null;
        if (mouseInWindow is { } mw && window.X > 0 && window.Y > 0)
        {
            // the eye image's pixels (the first eye's half when side by side), as the panel's
            float ex = mw.X / window.X * (stereo ? 2 : 1);
            if (ex > 1) ex -= 1;
            mouse = new Vector2(ex * ew, mw.Y / window.Y * eh);
        }
        _overToken = false;
        if (!_s.Private.On || vfov <= 0 || eh <= 0) return scene;
        var f = Vector3.Normalize(forward);
        var left = Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, f));
        var up = Vector3.Cross(f, left);
        float halfH = MathF.Tan(vfov / 2), mpp = 2 * halfH / eh;
        float rpx = Math.Clamp(eh * 0.0175f, 10, 24);
        float lift = 14 + rpx * 1.5f, aside = rpx * 3;
        var at = eye + f - up * (halfH - lift * mpp) + left * (aside * mpp);
        _overToken = mouse is { } m && Vector2.Distance(m, new Vector2(ew / 2f - aside, eh - lift)) <= rpx * 1.1f;
        return With(scene, at, rpx * mpp, _overToken);
    }

    // --- the headset: the token on the left forearm --------------------------------------------------------------------

    Vector3? _wrist;   // in the room
    bool _lit, _down, _armed = true;

    /// <summary>This frame the right ray is on the token (the panel and the world don't get it).</summary>
    public bool RightOn => _lit;

    /// <summary>The headset's frame, before the panel and the world take the controllers: the token's press; the grips'
    /// chord (dictation) comes back masked from <see cref="Dictation.Headset"/>.</summary>
    public void HeadsetFrame(XrControllers input, VrPilot pilot, bool blocked, bool focused)
    {
        _lit = false;
        // past the mic, HUD, stand and notice tokens, toward the elbow
        _wrist = _s.Private.On && !blocked && focused && pilot.Watch(input, along: 0.195f) is { } t ? pilot.RegionToRoom(t) : null;
        if (input.RightTrigger < 0.35f) { _armed = true; _down = false; }
        if (_wrist is not { } tk || input.RightAim is not { } q || input.RightAimPos is not { } from) return;
        var dir = Vector3.Transform(-Vector3.UnitZ, q);
        float along = Vector3.Dot(tk - from, dir);
        _lit = along > 0 && Vector3.Distance(from + dir * along, tk) < 0.025f;
        if (_lit && _armed && !_down && input.RightTrigger > 0.6f)
        {
            _down = true;
            _armed = false;
            _s.Private.Set(false, "wrist");
        }
    }

    public OverlayScene? Headset(OverlayScene? scene, VrPilot pilot) =>
        _wrist is { } tk ? With(scene, pilot.RoomToRegion(tk), 0.016f, _lit) : scene;

    OverlayScene With(OverlayScene? scene, Vector3 at, float radius, bool lit)
    {
        var l = _assistant.Panel.Look;
        var glyphs = new List<OverlayGlyph>(scene?.Glyphs ?? []) { new(at, radius, C(l.Accent, 0.96f), C(l.OnAccent, 1), _s.Private.Sees ? OverlayGlyphKind.PrivateSees : OverlayGlyphKind.Private, lit ? 0.6f : 0) };
        return (scene ?? new OverlayScene()) with { Glyphs = glyphs };
    }

    static Vector4 C(string hex, float a)
    {
        var k = SkiaSharp.SKColor.TryParse(hex, out var v) ? v : SkiaSharp.SKColors.Gray;
        return new Vector4(k.Red / 255f, k.Green / 255f, k.Blue / 255f, a);
    }
}
