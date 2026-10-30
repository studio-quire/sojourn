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
        for (; e != null; e = e.InnerException)
        {
            if (e is VulkanException { Result: Result.ErrorDeviceLost }) return true;
            // the headset's runtime failing with it: a driver reset during a frame surfaced first as xrWaitFrame's
            // ErrorRuntimeFailure (Mirromere, 2026-10-28), and the session or instance can be lost the same way
            if (e is Xr.XrException { Result: Silk.NET.OpenXR.Result.ErrorRuntimeFailure or Silk.NET.OpenXR.Result.ErrorSessionLost
                or Silk.NET.OpenXR.Result.ErrorInstanceLost }) return true;
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
    /// API; logging out is done here.</summary>
    public static async Task<int> Restart(Exception e, ILogger log, ClientOptions opt, string[] args, AgentSession session, Func<Task> shutdown)
    {
        string? where = Where(session);
        bool quiet = session.Private.On;   // private mode: the place stays out of the log
        log.LogError("the GPU was lost ({Msg}): Windows reset the display driver. Sojourn starts again{Where}",
            e.Message, where == null ? "" : quiet ? " where you were" : $" at {where[4..].Replace('&', ' ')}");

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
        return ExitCode;
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
        if (!int.TryParse(System.Environment.GetEnvironmentVariable(AfterVar), out int pid)) return;
        System.Environment.SetEnvironmentVariable(AfterVar, null);
        try { using var p = Process.GetProcessById(pid); p.WaitForExit(20_000); }
        catch (ArgumentException) { }   // already gone
        catch (InvalidOperationException) { }
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
