using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Silk.NET.Vulkan;
using Sojourn.Core.Agents;
using Sojourn.Render.Gpu;

namespace Sojourn.Client;

/// <summary>
/// A lost GPU (VK_ERROR_DEVICE_LOST: Windows reset the display driver, as a frame that pages video memory for
/// seconds makes it do) ends the process cleanly and starts it again where the person was, rather than crashing.
/// Written by Claude Opus 5.5, 2026-10-27, after a device loss at a busy place (docs/client.md > When the GPU is lost).
///
/// Why a restart rather than rebuilding the device in the process: the renderer's GPU state is spread through the
/// world's caches (textures, geometry, face caches holding texture slots, avatars, impostors, probes, media surfaces,
/// the overlay, the headset's swapchains made on the lost device), and the OpenXR session is bound to the device. A
/// new process rebuilds all of that by construction, the disk cache makes the place come back quickly, and the
/// login lands where the person was.
///
/// What it does: logs why, logs out (so the grid lets the next login in at once), stops the assistant, voice, sound
/// and media helpers, frees the agent API's port, starts the same program with the same arguments and the start
/// location set to the region and position the person was at, and exits. The new one says what happened on the
/// assistant's panel once it's in world. Three restarts within ten minutes and it stops trying (something is wrong
/// beyond a passing overload).
/// </summary>
public static class DeviceLoss
{
    public const int ExitCode = 75;
    const string MarkerName = "restarted.json";
    const string RestartsVar = "SOJOURN_RESTARTS";
    internal const string AfterVar = "SOJOURN_RESTART_AFTER_PID";
    /// <summary>Arguments the next run needs besides the start (a login from the login screen hands itself over:
    /// Login/LoginSheet.cs); called just before it's started.</summary>
    public static Func<IEnumerable<string>>? BeforeRelaunch;
    /// <summary>Testing: SOJOURN_FAKE_DEVICE_LOST=S throws the same exception S seconds into the first region drawn
    /// (FrameRenderer.RenderFrame), so recovery can be checked without resetting the PC's display driver.</summary>
    public const string FakeVar = "SOJOURN_FAKE_DEVICE_LOST";

    public static bool Is(Exception? e)
    {
        // the watch is restarting already (Guard): whatever the stuck frame loop meets once it's let go is the same loss
        if (e != null && Volatile.Read(ref _claimedBy) is int by && by != 0 && by != System.Environment.CurrentManagedThreadId) return true;
        for (; e != null; e = e.InnerException)
        {
            if (e is VulkanException { Result: Result.ErrorDeviceLost }) return true;
            if (e is SeenLostException) return true;
            // the headset's runtime failing with it: a driver reset during a frame surfaced first as xrWaitFrame's
            // ErrorRuntimeFailure (Mirromere, 2026-10-28), and the session or instance can be lost the same way
            if (e is Xr.XrException { Result: Silk.NET.OpenXR.Result.ErrorRuntimeFailure or Silk.NET.OpenXR.Result.ErrorSessionLost
                or Silk.NET.OpenXR.Result.ErrorInstanceLost }) return true;
            if (e is KeptHiddenException) return true;
            if (e is AggregateException a && a.InnerExceptions.Any(Is)) return true;
        }
        return false;
    }

    /// <summary>Where the person is, as a start location ("uri:Region&amp;x&amp;y&amp;z"), or null.</summary>
    static string? Where(AgentSession session)
    {
        try
        {
            if (session.Client is not { Network.CurrentSim: { } sim } c || string.IsNullOrEmpty(sim.Name)) return null;
            var p = c.Self.SimPosition;
            static string N(float f) => Math.Clamp((int)MathF.Round(f), 0, 4096).ToString(CultureInfo.InvariantCulture);
            return $"uri:{sim.Name}&{N(p.X)}&{N(p.Y)}&{N(p.Z)}";
        }
        catch (Exception) { return null; }
    }

    /// <summary>The device is gone: shut down what talks to the outside, start again, and return the exit code (the
    /// caller returns it from Main). <paramref name="shutdown"/> stops the assistant, voice, sound, media and the agent
    /// API; logging out is done here.
    /// <paramref name="letGo"/> gives the headset's session and runtime back (XrSession.LetGo, XrRuntime.Dispose); it runs
    /// once the next run is started, which waits for this one to exit before it asks for the headset.</summary>
    public static async Task<int> Restart(Exception e, ILogger log, ClientOptions opt, string[] args, AgentSession session, Func<Task> shutdown,
        Action? letGo = null)
    {
        // one restart: the frame loop's and the watch's (Guard) can both arrive; the second waits for the process to end
        int me = System.Environment.CurrentManagedThreadId;
        if (Interlocked.CompareExchange(ref _claimedBy, me, 0) is int by && by != 0 && by != me) Thread.Sleep(Timeout.Infinite);
        string? where = Where(session);
        bool quiet = session.Private.On;   // private mode: the place stays out of the log
        string at = where == null ? "" : quiet ? " where you were" : $" at {where[4..].Replace('&', ' ')}";
        if (e is KeptHiddenException) log.LogError("{Msg}: Sojourn starts again{Where}", e.Message, at);
        else log.LogError("the GPU was lost ({Msg}): Windows reset the display driver. Sojourn starts again{Where}", e.Message, at);

        // a loss that keeps coming back isn't a passing overload: three in ten minutes and it stops
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var recent = (System.Environment.GetEnvironmentVariable(RestartsVar) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => long.TryParse(s, out var t) ? t : 0).Where(t => now - t < 600).Append(now).ToList();
        bool again = recent.Count <= 3;

        try { File.WriteAllText(Path.Combine(opt.BaseDir, MarkerName), new JsonObject
        {
            ["at"] = DateTimeOffset.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["unix"] = now,
            ["reason"] = e.Message,
            ["where"] = where,
            ["restarted"] = again,
        }.ToJsonString()); }
        catch (Exception) { }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            await session.LogoutAsync().WaitAsync(cts.Token);
        }
        catch (Exception x) { log.LogWarning("logging out: {Msg}", x.Message); }
        try { await shutdown().WaitAsync(TimeSpan.FromSeconds(6)); }
        catch (Exception x) { log.LogWarning("shutting down: {Msg}", x.Message); }

        if (!again)
        {
            log.LogError("not starting again: the GPU was lost {N} times in ten minutes. Start Sojourn yourself when you like.", recent.Count);
            return ExitCode;
        }
        try
        {
            var psi = Relaunch(args.Concat((BeforeRelaunch?.Invoke() ?? []).Where(a => !args.Contains(a))).ToArray(), where);
            psi.Environment[RestartsVar] = string.Join(",", recent);
            psi.Environment[AfterVar] = System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
            psi.Environment.Remove(FakeVar);
            var child = Process.Start(psi);
            log.LogInformation("started again (pid {Pid})", child?.Id);
        }
        catch (Exception x) { log.LogError("couldn't start again: {Msg}", x.Message); }
        if (letGo != null) LetGo(letGo, log);
        return ExitCode;
    }

    /// <summary>Give the headset back before the process ends, on a thread of its own and 5 s at most (calls on a lost
    /// device can hang). After the next run has started, so a crash in it can't stop the restart; that run waits for
    /// this process to exit (<see cref="WaitForPrevious"/>), so its session comes after this one's is gone.</summary>
    public static void LetGo(Action release, ILogger log)
    {
        Exception? failed = null;
        var t = new Thread(() => { try { release(); } catch (Exception x) { failed = x; } }) { IsBackground = true, Name = "headset let go" };
        t.Start();
        if (!t.Join(5000)) log.LogWarning("the headset's session: not given back within 5 s; left to the process's exit");
        else if (failed != null) log.LogWarning("the headset's session: giving it back failed ({Msg})", failed.Message);
        else log.LogInformation("the headset's session given back");
    }

    /// <summary>The same program and arguments (under dotnet, the same dll), the start location replaced; the same
    /// working directory (relative paths in the arguments mean the same), console and environment.</summary>
    internal static ProcessStartInfo Relaunch(string[] args, string? where)
    {
        var rest = new List<string>(args);
        if (where != null)
        {
            int i = rest.IndexOf("--start");
            if (i >= 0 && i + 1 < rest.Count) rest[i + 1] = where;
            else rest.AddRange(new[] { "--start", where });
        }
        Login.LauncherAnswers.PassOn(rest);   // the launcher's answers reach the new run too
        var exe = System.Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = System.Environment.CurrentDirectory };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(System.Environment.GetCommandLineArgs()[0]);   // the dll
        foreach (var a in rest) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>At the very start of a restarted run: wait (20 s at most) for the run that lost the GPU to be gone, so the
    /// headset's runtime, the agent API's port and the grid have let go of it before this one asks for them.</summary>
    public static void WaitForPrevious()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _afterLoss = (System.Environment.GetEnvironmentVariable(RestartsVar) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(s => long.TryParse(s, out var t) && now - t < 180);
        if (!int.TryParse(System.Environment.GetEnvironmentVariable(AfterVar), out int pid)) return;
        System.Environment.SetEnvironmentVariable(AfterVar, null);
        try { using var p = Process.GetProcessById(pid); p.WaitForExit(20_000); }
        catch (ArgumentException) { }   // already gone
        catch (InvalidOperationException) { }
    }

    // --- a loss the frame loop can't reach ---------------------------------------------------------------------------
    // 2026-10-31 (Claude Opus 5.5): twice (00:34:48 and 16:17:14, each the second of two nvlddmkm 153 resets) the render
    // thread failed at the reset, but the main thread was inside Meta's xrWaitFrame, which returned ErrorRuntimeFailure
    // only 28 s later; the headset showed a frozen or black view meanwhile. A failure on the render thread reaches the
    // main thread only at its next hand-over (FramePipe), so a watch outside the frame loop restarts instead, on definite
    // signals only:
    //  1. the render thread failed with a lost device (Is) and the frame loop hasn't come for it within 2 s;
    //  2. it failed with anything else, the loop hasn't come for it within 2 s, and Windows logged a display driver reset
    //     (nvlddmkm 153, Display 4101) in the last two minutes;
    //  3. the frame loop hasn't turned for 10 s and Windows logged a driver reset from 30 s before it stopped on.
    // A slow frame, a load hitch, a debugger or the headset taken off logs no driver reset, so restarts nothing.
    static long _beat;
    static volatile bool _guarding;
    static int _claimedBy;
    const double FailedGraceSeconds = 2, StalledSeconds = 10;

    /// <summary>A loss seen from outside the frame loop (the watch's rules 2 and 3).</summary>
    public sealed class SeenLostException(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>Each pass of the frame loop, at its top: the loop is turning. (Testing the watch without a driver reset:
    /// SOJOURN_FAKE_HELD=1 with <see cref="FakeVar"/>, FramePipe.)</summary>
    public static void Beat() => Volatile.Write(ref _beat, Stopwatch.GetTimestamp());

    /// <summary>Start the watch before the frame loop; <paramref name="restart"/> is the loop's own restart
    /// (<see cref="Restart"/> with its shutdown), run on the watch's thread, after which the process exits.</summary>
    public static void Guard(FramePipe pipe, Func<Exception, Task<int>> restart, ILogger log)
    {
        Volatile.Write(ref _beat, Stopwatch.GetTimestamp());
        _guarding = true;
        new Thread(() => Watching(pipe, restart, log)) { IsBackground = true, Name = "gpu watch", Priority = ThreadPriority.AboveNormal }.Start();
    }

    /// <summary>The frame loop is over (quitting): nothing more to watch.</summary>
    public static void Unguard() => _guarding = false;

    static void Watching(FramePipe pipe, Func<Exception, Task<int>> restart, ILogger log)
    {
        long askedAt = 0;   // the System log is read once a second at most, and only while a rule is waiting on it
        bool told = false;
        while (true)
        {
            Thread.Sleep(250);
            if (!_guarding || Volatile.Read(ref _claimedBy) != 0) return;
            long now = Stopwatch.GetTimestamp(), beat = Volatile.Read(ref _beat);
            double still = Stopwatch.GetElapsedTime(beat, now).TotalSeconds;
            bool mayAsk = askedAt == 0 || Stopwatch.GetElapsedTime(askedAt, now).TotalSeconds >= 1;
            Exception? lost = null;
            if (pipe.Failure is { } f && beat < f.At && Stopwatch.GetElapsedTime(f.At, now).TotalSeconds >= FailedGraceSeconds)
            {
                if (!told)
                {
                    told = true;
                    log.LogWarning("the render thread failed ({Msg}) and the frame loop hasn't come for it in {S:F0} s", f.Error.Message,
                        Stopwatch.GetElapsedTime(f.At, now).TotalSeconds);
                }
                if (Is(f.Error)) lost = f.Error;
                else if (mayAsk)
                {
                    askedAt = now;
                    if (DriverReset(TimeSpan.FromMinutes(2)) is { } at)
                        lost = new SeenLostException($"{f.Error.Message}; the frame loop held since, the display driver reset at {at:HH:mm:ss}", f.Error);
                }
            }
            else if (still >= StalledSeconds && mayAsk)
            {
                askedAt = now;
                if (DriverReset(TimeSpan.FromSeconds(still + 30)) is { } at)
                    lost = new SeenLostException($"the frame loop held {still:F0} s, the display driver reset at {at:HH:mm:ss}");
            }
            if (lost == null) continue;

            int code = ExitCode;
            try { code = restart(lost).GetAwaiter().GetResult(); }
            catch (Exception x) { log.LogError("starting again from the watch: {Msg}", x.Message); }
            System.Environment.Exit(code);
        }
    }

    /// <summary>When Windows last logged a display driver reset (nvlddmkm 153, or Display 4101 "stopped responding and has
    /// recovered") within <paramref name="within"/>, local time; null if none or the System log can't be read.</summary>
    static DateTime? DriverReset(TimeSpan within)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            long ms = (long)Math.Ceiling(within.TotalMilliseconds);
            var q = new System.Diagnostics.Eventing.Reader.EventLogQuery("System", System.Diagnostics.Eventing.Reader.PathType.LogName,
                "*[System[((Provider[@Name='nvlddmkm'] and EventID=153) or (Provider[@Name='Display'] and EventID=4101))" +
                $" and TimeCreated[timediff(@SystemTime) <= {ms}]]]") { ReverseDirection = true };
            using var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
            using var e = reader.ReadEvent();
            return e?.TimeCreated;
        }
        catch (Exception) { return null; }
    }

    // --- a restarted run kept out of view ----------------------------------------------------------------------------
    // 2026-10-31: after a driver reset the restarted run went Focused at once, then 9 s later Meta's runtime moved it
    // Focused -> Visible -> Synchronized in one step and never back (0 fps, the headset black, until a manual restart).
    // The same step on 2026-10-27 came 35 ms after an older run's xrDestroySession while the new one was showing: the
    // runtime takes the end of the previous app's session out on the current one. The old run now gives its session
    // back before it exits (LetGo); if a restarted run is still put out of view early on, it starts again once more,
    // which a manual restart showed works. Only runs restarted after a loss, and only in their first two minutes after
    // being shown: an ordinary trip to Synchronized (the headset off) is left alone.
    static bool _afterLoss;
    static long _shownAt, _hiddenSince;
    const double WatchSeconds = 120, HiddenSeconds = 10;

    public sealed class KeptHiddenException() : Exception("the headset's runtime kept the restarted session out of view");

    /// <summary>Each frame-loop pass in the headset, after its events: throws <see cref="KeptHiddenException"/> (a restart,
    /// as a loss) if a run restarted after a loss is kept out of view as above.</summary>
    public static void Watch(Silk.NET.OpenXR.SessionState state)
    {
        if (!_afterLoss) return;
        long now = Stopwatch.GetTimestamp();
        if (state is Silk.NET.OpenXR.SessionState.Visible or Silk.NET.OpenXR.SessionState.Focused)
        {
            if (_shownAt == 0) _shownAt = now;
            _hiddenSince = 0;
            return;
        }
        if (_shownAt == 0) return;   // not shown yet: still coming up
        if (state != Silk.NET.OpenXR.SessionState.Synchronized) { _hiddenSince = 0; return; }
        if (_hiddenSince == 0)
        {
            if (Stopwatch.GetElapsedTime(_shownAt, now).TotalSeconds > WatchSeconds) { _afterLoss = false; return; }
            _hiddenSince = now;
        }
        else if (Stopwatch.GetElapsedTime(_hiddenSince, now).TotalSeconds > HiddenSeconds)
        {
            _afterLoss = false;
            throw new KeptHiddenException();
        }
    }

    /// <summary>At start: if the last run ended in a lost GPU, say so in the log and, once in world, on the assistant's panel.</summary>
    public static void Announce(ClientOptions opt, AgentSession session, AssistantHost assistant, ILogger log)
    {
        var path = Path.Combine(opt.BaseDir, MarkerName);
        JsonObject? marker;
        try { marker = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; File.Delete(path); }
        catch (Exception) { return; }
        if (marker == null || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (marker["unix"]?.GetValue<long>() ?? 0) > 900) return;
        string at = marker["at"]?.GetValue<string>() ?? "?";
        log.LogInformation("{What} after the GPU was lost at {At} ({Reason})", marker["restarted"]?.GetValue<bool>() == true ? "started again" : "started",
            at, marker["reason"]?.GetValue<string>());
        if (marker["restarted"]?.GetValue<bool>() != true) return;
        bool there = marker["where"] is not null;
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 600 && session.Status != AgentStatus.Online; i++) await Task.Delay(500);
            if (session.Status != AgentStatus.Online) return;
            await Task.Delay(3000);   // past the panel's own first messages
            assistant.Panel.Update(p =>
            {
                p.Heard = null;
                p.Result = $"Sojourn restarted after the graphics card was reset at {at}." + (there ? " You're back where you were." : "");
            });
        });
    }
}
