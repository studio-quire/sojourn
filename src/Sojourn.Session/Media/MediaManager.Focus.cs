using LibreMetaverse;

namespace Sojourn.Session.Media;

/// <summary>What a first click on media in the world does (the viewer's MediaFirstClickInteract, 7.2.0).</summary>
public enum FirstClick { None, Huds, Own, Friends, Group, Land, Any, All }

// The person's focus on a face (LLViewerMediaFocus): the viewer's first click on media in the world focuses it (and zooms
// to it when the face asks, auto_zoom), and only clicks after that go into the page, unless the face lets its first
// click through (first_click_interact) and the person's MediaFirstClickInteract allows it for that object (lltoolpie.cpp
// shouldAllowFirstMediaInteraction, read 2026-10-29). Here a zoom is the up-close view, never the camera: the headset's
// view isn't moved for a page. One face is focused at a time; a click in the world off media, Esc, or the face going
// clears it. Under the manager's lock. Claude Opus 5.5, 2026-10-29 (batch B10).
public sealed partial class MediaManager
{
    /// <summary>The person's choice (default Land, as both viewers: HUDs, their own, friends', group's and the land
    /// owner's objects that ask for it).</summary>
    public volatile FirstClick FirstClicks = FirstClick.Land;
    /// <summary>Faces that ask to be zoomed to come up close on the focusing click (the viewer always follows auto_zoom).</summary>
    public volatile bool AutoZoom = true;

    string? _focused;

    public bool IsFocused(MediaItem i) => _focused == i.Key || i.Zoomed;
    public MediaItem? Focused => _focused is { } k ? Item(k) : null;
    public void ClearFocus() => _focused = null;

    /// <summary>The focusing click: true when the face asks to be zoomed to (the caller brings it up close).</summary>
    public bool SetFocus(MediaItem i)
    {
        _focused = i.Key;
        Focus(i, 4000);
        return AutoZoom && i.AutoZoom && i.Kind == MediaItemKind.Prim;
    }

    // the viewer's bits (lltoolpie.h MediaFirstClickTypes); each choice includes the ones before it
    const int Hud = 1, Own = 2, Friend = 4, GroupBit = 8, LandBit = 16, AnyBits = (1 << 15) - 1, Bypass = 1 << 15;

    static int Bits(FirstClick f) => f switch
    {
        FirstClick.None => 0, FirstClick.Huds => Hud, FirstClick.Own => Hud | Own, FirstClick.Friends => Hud | Own | Friend,
        FirstClick.Group => Hud | Own | Friend | GroupBit, FirstClick.Land => Hud | Own | Friend | GroupBit | LandBit,
        FirstClick.Any => AnyBits, _ => AnyBits | Bypass,
    };

    /// <summary>Whether a click on this goes into it now: it's focused (or up close), or its first click is let through.</summary>
    public bool TakesClick(MediaItem i) => i.Kind == MediaItemKind.Page || IsFocused(i) || FirstClickThrough(i);

    /// <summary>shouldAllowFirstMediaInteraction: the face's flag (parcel media has none), then whose object it is.</summary>
    public bool FirstClickThrough(MediaItem i)
    {
        int pref = Bits(FirstClicks);
        if (pref == 0) return false;
        if (!(i.Kind == MediaItemKind.Prim && i.FirstClickInteract) && (pref & Bypass) == 0) return false;
        if ((pref & AnyBits) == AnyBits) return true;
        if ((pref & Hud) != 0 && i.OnHud) return true;
        var c = _s.Client;
        var me = c?.Self.AgentID ?? UUID.Zero;
        UUID owner = i.Kind == MediaItemKind.Parcel ? _agentParcel?.OwnerID ?? UUID.Zero : i.Owner;
        UUID group = i.Kind == MediaItemKind.Parcel ? _agentParcel?.GroupID ?? UUID.Zero : i.Group;
        if (owner == UUID.Zero && group == UUID.Zero) return false;
        if ((pref & Own) != 0 && (owner == me || i.Mine)) return true;
        if ((pref & Friend) != 0 && c != null && c.Friends.FriendList.ContainsKey(owner)) return true;
        if ((pref & GroupBit) != 0 && (_s.State.Groups.ContainsKey(group) || _s.State.Groups.ContainsKey(owner))) return true;
        if ((pref & LandBit) != 0 && _agentParcel is { } p && (i.Kind == MediaItemKind.Parcel || InAgentParcel(i))
            && (p.OwnerID != UUID.Zero || p.GroupID != UUID.Zero) && (owner == p.OwnerID || group == p.GroupID))
            return true;
        return false;
    }
}
