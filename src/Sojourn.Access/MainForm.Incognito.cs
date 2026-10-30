using System.Text.Json.Nodes;
using LibreMetaverse;
using Sojourn.Session.Control;
using Sojourn.Session.Private;

namespace Sojourn.Access;

/// <summary>
/// Incognito in Sojourn Access (docs/assistant.md > Incognito; Session's Private/ holds the state, the allowlist and the
/// triggers, shared with the VR client): nothing from a spell is kept, and unless "let it see" is on the assistant learns
/// only the general picture. Control+Shift+I turns it on (any key of the person's, the screen reader's pass-on too) and
/// off (only a physical key, fresh, as confirmations take: off is the person's own, never a program's; with
/// access_answers_without_key set to hello, Windows Hello stands in). Its settings are in Alt+0: on, let it see (turned on
/// mid-spell only by the person's own key there), while a conversation is open, always here; a person's Applications
/// key has "Always incognito with them". The screen reader keeps reading (on the desktop the window is the view, as the
/// VR client's rule has it), the review history drops the spell's lines when it ends (A1's), and the window's title
/// leaves out the place while it lasts.
/// </summary>
public sealed partial class MainForm
{
    Triggers? _triggers;
    volatile Conversation? _imInFocus;

    void StartIncognito()
    {
        var h = Host!;
        var s = h.Session;
        s.Private.SetSees(h.Settings.IncognitoSees);
        var triggers = _triggers = new Triggers(s, h.Settings, h.SaveSettings);
        // "while an IM is open" here is the conversation the person is in (Access's own feed is its conversations)
        triggers.OpenImWith = () => _imInFocus is { Group: false, Other: var o } && o != UUID.Zero ? o : null;
        triggers.AnyImOpen = () => _imInFocus != null;
        h.Privacy.Situation.OpenConversation = () => _imInFocus is { } cv && UUID.TryParse(cv.SessionId, out var id) ? id : null;
        h.Privacy.Api.Remember = triggers.Remember;
        s.Private.Changed += (_, _, _) => BeginInvoke(() => { UpdateWhere(); if (_areas.SelectedTab == _settingsPage) _ = RefreshSettings(); });
        s.Private.SeesChanged += sees => BeginInvoke(() => { if (_areas.SelectedTab == _settingsPage) _ = RefreshSettings(); });
    }

    /// <summary>Control+Shift+I: on by any of the person's keys; off only by their own physical press.</summary>
    void ToggleIncognito()
    {
        var p = Host?.Session.Private;
        if (p == null) { Reader?.Answer("Not in a region yet."); return; }
        if (!p.On)
        {
            if (!RealInput.Person) { Reader?.Answer(RealInput.Why); return; }
            p.Set(true, "key");
            return;
        }
        if (RealInput.Confirms) { p.Set(false, "key"); return; }
        Hello.Instead(_set.ConfirmByHello, Handle, "end incognito", RealInput.Why, t => Reader?.Answer(t), () => p.Set(false, "key"));
    }

    /// <summary>Alt+0's incognito rows: what SojournSettings refuses to any caller (off, and letting it see mid-spell)
    /// is the person's own key here. True when the row was one of these.</summary>
    bool IncognitoRow(string name)
    {
        var p = Host?.Session.Private;
        if (p == null) return false;
        switch (name)
        {
            case "incognito":
                ToggleIncognito();
                return true;
            case "incognito_sees" when p.On && !p.Sees:
                if (RealInput.Confirms) SetSees(true);
                else Hello.Instead(_set.ConfirmByHello, Handle, "let the assistant see while incognito", RealInput.Why, t => Reader?.Answer(t), () => SetSees(true));
                return true;
            default:
                return false;
        }
    }

    void SetSees(bool sees)
    {
        var h = Host!;
        if (!h.Session.Private.SetSees(sees)) return;
        h.Settings.IncognitoSees = sees;
        h.SaveSettings();
        _ = RefreshSettings();
    }

    /// <summary>A person's "Always incognito with them": it comes on by itself while they're within chat range or in an
    /// IM with you, and goes off by itself once neither holds.</summary>
    void IncognitoWith(UUID id, string name)
    {
        if (_triggers is not { } t) return;
        bool on = !t.With(id);
        t.SetPerson(id, on);
        Answer(on ? $"Incognito comes on by itself with {name} (within chat range, or in an IM)." : $"Incognito no longer comes on with {name}.");
    }

    /// <summary>The settings on SojournSettings, as the VR client has them (incognito, incognito_sees,
    /// incognito_while_im), and Access's "always here"; never the lists, which name people and places.</summary>
    void AddIncognitoSettings(SettingsApi api)
    {
        var s = Host!.Session;
        api.Add("incognito", new SettingsApi.Setting("incognito", "incognito: nothing from the time it's on is kept, and unless incognito_sees is on "
            + "the assistant learns only the general picture (SojournPrivate); it can be turned on here, and only the person turns it off "
            + "(Control+Shift+I)", "on (off is the person's own)",
            () => s.Private.On ? "on" : "off",
            v =>
            {
                bool want = false;
                if (SettingsApi.Switch("incognito", on => want = on)(v) is { } bad) return bad;
                if (!want) return "incognito: the person turns incognito off themselves (Control+Shift+I)";
                s.Private.Set(true, "api");
                return null;
            }));
        api.Add("incognito_sees", new SettingsApi.Setting("incognito", "let it see: while incognito is on the assistant sees and helps with "
            + "specifics as usual (nothing from the time is kept all the same); off, it gets only the general picture. While incognito is on, only "
            + "the person turns this on", "on, off",
            () => s.Private.Sees ? "on" : "off",
            v =>
            {
                bool want = false;
                if (SettingsApi.Switch("incognito_sees", on => want = on)(v) is { } bad) return bad;
                if (want && s.Private.On) return "incognito_sees: while incognito is on, the person turns this on themselves (Alt+0)";
                BeginInvoke(() => SetSees(want));
                return null;
            }));
        api.Add("incognito_while_im", new SettingsApi.Setting("incognito", "incognito comes on by itself while you're in an IM conversation "
            + "(Sojourn Access: the conversation in focus)", "on, off", () => _triggers?.WhileIm == true ? "on" : "off",
            SettingsApi.Switch("incognito_while_im", on => _triggers?.SetWhileIm(on))));
        api.Add("incognito_here", new SettingsApi.Setting("incognito", "Sojourn Access: incognito comes on by itself on the parcel you're on now, "
            + "whenever you're there (off: not here)", "on, off",
            () => _triggers?.Here() is { } h && _triggers.Places.Any(p => p.Is(h.Region, h.Parcel)) ? "on" : "off",
            v =>
            {
                bool want = false;
                if (SettingsApi.Switch("incognito_here", on => want = on)(v) is { } bad) return bad;
                return _triggers?.SetHere(want) == true ? null : "incognito_here: where you are isn't known yet";
            }));
    }
}
