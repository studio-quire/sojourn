using Sojourn.Client.Control;

namespace Sojourn.Client.Assistant;

/// <summary>
/// Every menu's placement in the headset (a <see cref="PanelPlacement"/> each; Claude Opus 5.5, 2026-10-28, the user's
/// ask): where the person put them, kept between sessions in the tracking space they were put in (the settings'
/// menuPlaces, by name), and the reset that brings every one back to following in its own resting place, forgetting
/// where they were put: X held, the view section's menus key, or the assistant (SojournAssistant
/// resetMenus). The headset's own recentring forgets the places too: the room moved under them.
/// </summary>
public sealed class MenuPlaces
{
    readonly List<(PanelPlacement P, bool Keep)> _all = new();
    readonly VrSettings _settings;
    readonly string _space;
    readonly Action _save;
    readonly Action<string> _log;
    volatile bool _reset;

    public MenuPlaces(VrSettings settings, string space, Action save, Action<string> log)
    {
        _settings = settings;
        _space = space;
        _save = save;
        _log = log;
        settings.MenuPlaces ??= new();   // "menuPlaces": null in the file
        // the two places kept before every menu had one
        if (settings.PanelPlace is { } pp) settings.MenuPlaces.TryAdd("panel", pp);
        if (settings.HudBoardPlace is { } hp) settings.MenuPlaces.TryAdd("hudBoard", hp);
        settings.PanelPlace = settings.HudBoardPlace = null;
    }

    /// <summary>A menu's placement, its place kept between sessions unless keep is false (the assistant's cards, which
    /// come and go by the assistant's ids).</summary>
    public PanelPlacement Add(PanelPlacement p, bool keep = true)
    {
        _all.Add((p, keep));
        if (keep && _settings.MenuPlaces.TryGetValue(p.Name, out var kept) && kept.In(_space) is { } pin)
        {
            p.Pin = pin;
            _log($"headset: the {p.Name}'s kept place");
        }
        p.PinChanged += Changed;
        return p;
    }

    public void Remove(PanelPlacement p)
    {
        _all.RemoveAll(x => x.P == p);
        p.PinChanged -= Changed;
    }

    /// <summary>A menu was put somewhere or came back to following (main thread).</summary>
    public event Action? PlacesChanged;

    /// <summary>Some menu is put somewhere (the menu's reset key shows then).</summary>
    public bool AnyPut => _all.Any(x => x.P.Pinned);

    /// <summary>Every menu back to following in its own place, from in front; where they were put is forgotten. Any
    /// thread: it happens at the next headset frame.</summary>
    public void Reset() => _reset = true;

    /// <summary>Each headset frame, before the menus are placed.</summary>
    public void Frame()
    {
        if (!_reset) return;
        _reset = false;
        bool any = AnyPut;
        foreach (var (p, _) in _all)
        {
            p.Unpin();
            p.BringToFront();
        }
        _log(any ? "menus: all following again, in front" : "menus: in front");
        if (any) { Save(); PlacesChanged?.Invoke(); }
    }

    /// <summary>The headset's own recentring moved the room: places kept in it mean nothing now.</summary>
    public void Recentred()
    {
        if (!AnyPut) return;
        foreach (var (p, _) in _all) p.Unpin();
        _log("menus: the headset recentred; where they were put is forgotten");
        Save();
        PlacesChanged?.Invoke();
    }

    void Changed(PanelPlacement p)
    {
        _log(p.Pinned ? $"{p.Name}: put somewhere" : $"{p.Name}: following");
        if (_all.Any(x => x.P == p && x.Keep)) Save();
        PlacesChanged?.Invoke();
    }

    void Save()
    {
        // a new dictionary, never one changed under a save from another thread
        var places = new Dictionary<string, RoomPlace>(_settings.MenuPlaces);
        foreach (var (p, keep) in _all)
        {
            if (!keep) continue;
            if (p.Pin is { } pin) places[p.Name] = RoomPlace.From(pin.Pos, pin.Rot, _space);
            else places.Remove(p.Name);
        }
        _settings.MenuPlaces = places;
        _save();
    }
}
