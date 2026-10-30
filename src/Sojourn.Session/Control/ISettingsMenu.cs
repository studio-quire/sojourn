using Sojourn.Session.Assistant;

namespace Sojourn.Session.Control;

/// <summary>
/// The app's settings menu, as the session's parts fill it: each part's section is a function giving its rows (a row
/// and what a press or step does), and Changed asks for it to be drawn again. The VR client's is Control/SettingsMenu.cs;
/// an app without one passes null to the parts' Settle and has only SojournSettings.
/// </summary>
public interface ISettingsMenu
{
    /// <summary>Something the menu shows changed elsewhere (the agent API): draw it again.</summary>
    void Changed();
    /// <summary>Voice chat, when the app has it: its section of the menu, and the saved settings put to it.</summary>
    global::Sojourn.Session.Voice.VoiceClient? Voice { get; set; }
    /// <summary>World audio's section (Audio/AudioWiring.cs).</summary>
    Func<List<(MenuRow Row, Action<int> Act)>>? SoundRows { get; set; }
    /// <summary>Incognito's section: the switch, let it see, and its automatic triggers.</summary>
    Func<List<(MenuRow Row, Action<int> Act)>>? PrivateRows { get; set; }
    /// <summary>The screen reader's section (Access/AccessWiring.cs): reading out, and what's read.</summary>
    Func<List<(MenuRow Row, Action<int> Act)>>? ReaderRows { get; set; }
    /// <summary>How the person shows to others (People/PeopleWiring.cs): Do Not Disturb, away, hover height, typing.</summary>
    Func<List<(MenuRow Row, Action<int> Act)>>? PeopleRows { get; set; }
    /// <summary>Safety and comfort: the vignette, keep me in place, aim, particles, antispam, release keys.</summary>
    Func<List<(MenuRow Row, Action<int> Act)>>? SafetyRows { get; set; }
}
