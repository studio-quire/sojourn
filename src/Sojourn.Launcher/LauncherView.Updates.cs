using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace Sojourn.Launcher;

/// <summary>An installed copy's update in the footer (Updates.cs): "update to 0.1.0.N" beside the version once a check
/// finds one, hidden while the client runs (installing closes it); the person's own press downloads and installs it,
/// and the launcher starts again as the new version.</summary>
public partial class LauncherView
{
    Updates? _updates;
    string? _update;
    bool _updating;
    readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    void StartUpdates()
    {
        _updates = Updates.Make();
        if (_updates == null) return;
        UpdateKey.Click += (_, _) => InstallUpdate();
        _updateTimer.Tick += (_, _) => ShowUpdate();
        _updateTimer.Start();
        _ = CheckForUpdate();
    }

    async Task CheckForUpdate()
    {
        try { _update = await Task.Run(() => _updates!.CheckAsync()); }
        catch (Exception) { }   // offline, GitHub's limit, no signed release: nothing offered
        ShowUpdate();
    }

    void ShowUpdate()
    {
        UpdateKey.Visibility = _update != null && (_updating || !_run.Running) ? Visibility.Visible : Visibility.Collapsed;
        if (_update == null || _updating) return;
        UpdateKey.Content = Lang.F("update to {0}", _update);
        AutomationProperties.SetName(UpdateKey, Lang.F("update to {0}", _update));
    }

    async void InstallUpdate()
    {
        if (_updating || _update == null || _run.Running || _updates == null) return;
        if (!RealPress.Now()) { Note(Say("press it with the mouse or keyboard"), null, TimeSpan.FromSeconds(8), alarm: true); return; }
        _updating = true;
        UpdateKey.IsEnabled = LaunchKey.IsEnabled = false;
        UpdateKey.Content = Lang.T("downloading");
        try
        {
            await Task.Run(() => _updates.InstallAsync(p => Dispatcher.BeginInvoke(() =>
                UpdateKey.Content = p < 100 ? Lang.F("downloading {0}%", p) : Lang.T("installing"))));
        }
        catch (Exception e)
        {
            _updating = false;
            UpdateKey.IsEnabled = LaunchKey.IsEnabled = true;
            var why = e.Message;   // (Velopack's or the system's own words)
            Note(() => Lang.F("the update didn't install: {0}", why), null, TimeSpan.FromSeconds(20), alarm: true);
            ShowUpdate();
        }
    }

    /// <summary>For the pictures (Shots.cs): the footer offering an update.</summary>
    internal void ShowUpdateFor(string version)
    {
        _update = version;
        ShowUpdate();
    }
}
