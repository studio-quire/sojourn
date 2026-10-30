using System.Diagnostics;
using System.Text;

namespace Sojourn.Client.Xr;

/// <summary>
/// Meta's PC runtime synthesises every other frame (ASW) once an app misses the period, locking it to half rate; on
/// SL's fine alpha detail its warping reads as jitter, and a frame later still is extrapolated twice. Off, a missed
/// frame is only reprojected (with our depth) and the app runs at whatever rate it makes. OpenXR has no switch for it on
/// PC, so this goes through the runtime's own Debug Tool CLI, the same as Ctrl+Numpad1 / Ctrl+Numpad4: off for our
/// session, always (the user, 2026-10-26; no setting since 2026-10-28: synthesis brought the jitter back, the worst
/// graphics bug we've had), back to auto when the headset session ends. Only on Meta's runtime, and only when its CLI
/// is installed.
///
/// The runtime keeps the setting until it restarts, so it must go back to auto whenever Sojourn isn't running, however
/// it ends (a client killed at 13:18 on 2026-10-27 left it off): turning it off writes a marker (our process id) in
/// %LOCALAPPDATA%\Sojourn, and a hidden watcher process waits for this process to end and puts it back to auto if the
/// marker is still ours; a clean end, a crash (unhandled exception) and process exit put it back here first. A start
/// that finds a marker of a process no longer running puts it back too (the watcher itself killed, a power cut).
/// </summary>
static class MetaAsw
{
    static readonly string Cli = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Oculus", "Support", "oculus-diagnostics", "OculusDebugToolCLI.exe");
    static readonly string Marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sojourn", "asw-off.txt");
    static readonly object Gate = new();
    static Action<string>? _log;
    static bool _active, _turnedOff, _hooked, _watching;

    /// <summary>At start, headset or not: a marker left by a run that's gone means ASW may still be off; put it back.</summary>
    public static void RecoverLeftOver(Action<string> log)
    {
        try
        {
            if (!File.Exists(Marker)) return;
            if (int.TryParse(File.ReadAllText(Marker).Trim(), out int pid) && pid != Environment.ProcessId && Alive(pid)) return;   // another Sojourn's
            _log = log;
            if (File.Exists(Cli)) Run("auto");
            File.Delete(Marker);
            log("frame synthesis: an earlier run had left Meta's ASW off; it's back to auto");
        }
        catch (Exception e) { log($"frame synthesis: checking for an earlier run's setting: {e.Message}"); }
    }

    /// <summary>A headset session started on this runtime: ASW off for it.</summary>
    public static void Begin(string runtimeName, Action<string> log)
    {
        if (!runtimeName.StartsWith("Oculus", StringComparison.OrdinalIgnoreCase)) return;
        if (!File.Exists(Cli)) { log("frame synthesis: Meta's Debug Tool CLI isn't installed; ASW left as it is"); return; }
        _log = log;
        _active = true;
        if (!_hooked)
        {
            _hooked = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
            AppDomain.CurrentDomain.UnhandledException += (_, _) => Restore();
        }
        _turnedOff = true;
        Mark();
        Task.Run(() => Run("off"));
    }

    /// <summary>The headset session ended or the client is closing: ASW back to auto if we turned it off.</summary>
    public static void End()
    {
        if (!_active) return;
        _active = false;
        if (_turnedOff) Run("auto");
        _turnedOff = false;
        Unmark();
    }

    // process exit or a crash: synchronous, before the process goes (the watcher covers a kill)
    static void Restore()
    {
        if (!_turnedOff) return;
        _turnedOff = false;
        _active = false;
        try { Run("auto"); } catch { }
        Unmark();
    }

    static void Mark()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
            File.WriteAllText(Marker, Environment.ProcessId.ToString());
        }
        catch (Exception e) { _log?.Invoke($"frame synthesis: no marker ({e.Message}); a crash could leave ASW off"); }
        if (!_watching) _watching = StartWatcher();
    }

    static void Unmark()
    {
        try
        {
            if (File.Exists(Marker) && File.ReadAllText(Marker).Trim() == Environment.ProcessId.ToString()) File.Delete(Marker);
        }
        catch { }
    }

    // a hidden PowerShell that waits for this process and, if the marker still names it, puts ASW back to auto
    static bool StartWatcher()
    {
        try
        {
            int pid = Environment.ProcessId;
            string q(string s) => "'" + s.Replace("'", "''") + "'";
            var script = $$"""
                $p = Get-Process -Id {{pid}} -ErrorAction SilentlyContinue
                if ($p) { $p.WaitForExit() }
                Start-Sleep -Milliseconds 300
                $m = {{q(Marker)}}
                if ((Test-Path $m) -and ((Get-Content $m -Raw).Trim() -eq '{{pid}}')) {
                  $t = Join-Path $env:TEMP 'sojourn-asw-watch-{{pid}}.txt'
                  Set-Content -Path $t -Value "server: asw.Auto`nexit"
                  & {{q(Cli)}} -f $t | Out-Null
                  Remove-Item $m, $t -ErrorAction SilentlyContinue
                }
                """;
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
            {
                CreateNoWindow = true, UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (Exception e) { _log?.Invoke($"frame synthesis: no watcher ({e.Message}); a killed client could leave ASW off"); return false; }
    }

    static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    static void Run(string mode)
    {
        lock (Gate)
        {
            string script = Path.Combine(Path.GetTempPath(), $"sojourn-asw-{Environment.ProcessId}.txt");
            try
            {
                File.WriteAllText(script, $"server: asw.{(mode == "off" ? "Off" : "Auto")}\nexit\n");
                using var p = Process.Start(new ProcessStartInfo(Cli, $"-f \"{script}\"")
                {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                });
                if (p == null) return;
                var output = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(10_000)) { try { p.Kill(); } catch { } _log?.Invoke($"frame synthesis: the Debug Tool CLI didn't answer; ASW may still be {(mode == "off" ? "on" : "off")}"); return; }
                bool ok = output.Result.Contains(mode == "off" ? "disabled" : "auto", StringComparison.OrdinalIgnoreCase);
                _log?.Invoke(ok ? $"frame synthesis: Meta's ASW {(mode == "off" ? "off for this session" : "back to auto")}"
                                : $"frame synthesis: the Debug Tool CLI said: {output.Result.Trim().Replace('\n', ' ')}");
            }
            catch (Exception e) { _log?.Invoke($"frame synthesis: {e.Message}"); }
            finally { try { File.Delete(script); } catch { } }
        }
    }
}
