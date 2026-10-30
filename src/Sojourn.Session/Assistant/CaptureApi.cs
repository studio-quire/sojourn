using System.Text.Json.Nodes;
using Sojourn.Core;
using Sojourn.Core.Agents;
using Sojourn.Core.Leap;
using Sojourn.Core.Rendering;

namespace Sojourn.Session.Assistant;

/// <summary>
/// SojournCapture: pictures and short videos of the person's view saved as files they can open later, in their own
/// Pictures\Sojourn and Videos\Sojourn folders. The client picks the file names and folders (callers never give a
/// path: LLViewerWindow's snapshots by path stay closed to the assistant). A picture is the first eye at its full
/// size, as the person sees it without the panel, or a PNG the caller hands over (the assistant's last look, which it
/// has already seen). A recording shows a "Recording" card with a stopwatch while it runs (drawn over the view, so
/// not in the video). Claude Opus 5.5, 2026-10-26, at the user's request.
/// </summary>
public sealed class CaptureApi : LeapApi
{
    readonly LiveSnapshots _snapshots;
    readonly Recorder _recorder;
    readonly IShowing? _shown;

    public override string Name => "SojournCapture";
    public override string Desc => "pictures and short videos of the person's view, saved where they can open them (Pictures\\Sojourn, Videos\\Sojourn)";

    public static string PicturesDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Sojourn");

    public CaptureApi(AgentSession s, LiveSnapshots snapshots, Recorder recorder, IShowing? shown) : base(s)
    {
        _snapshots = snapshots;
        _recorder = recorder;
        _shown = shown;
        // TesselObjects get full shows a prim's reflection probe (what the build keys set) as the renderer read it
        Sojourn.Core.Agents.ObjectViews.ProbeOf = (region, local) =>
            Sojourn.Render.World.ReflectionProbeTracker.Shared.Get(region, local) is { } p
                ? new JsonObject { ["type"] = p.Box ? "box" : "sphere", ["ambiance"] = p.Ambiance, ["clip_distance"] = p.ClipDistance,
                                   ["dynamic"] = (p.Flags & 0x02) != 0, ["mirror"] = (p.Flags & 0x04) != 0 }
                : null;
        Op("snapshot", "{name?: a few words for the file, png?: base64 PNG to save instead of taking one} -> {path, width, height}: "
            + "the view as the person sees it now (without the panel), full size, into Pictures\\Sojourn. Bigger or from elsewhere, "
            + "rendered apart (the view never moves; a short hitch): size? (WxH up to 7680x4320, the viewer's sizes; or width/height), "
            + "fov? (degrees), camera? (front|selfie|behind|shoulder|above|left|right around the person, distance? m; or {at: [x,y,z], "
            + "look_at: [x,y,z]} in the region), hide_avatars?, settle? (seconds to let what it sees load; 5 for a camera away, else 0), "
            + "format? png|jpg, quality? (jpg, 75) -> also {tiles?, waited_seconds?, still_loading?}", SnapshotAsync);
        Op("record", "{seconds=15 (1-120), fps=30 (1-120; a one-frame flicker needs the render rate, 72-90 in the headset), width=960, "
            + "name?, wait=true} -> {path, seconds, fps, size, frames, repeated, rendered_fps, encoder} once saved (wait: false -> {recording, "
            + "path} at once): a video of the view (without the panel, no sound) into Videos\\Sojourn; one at a time", RecordAsync);
        Op("stop", "-> the recording's result: ends it early", async (_, ct) =>
        {
            if (!_recorder.Busy) return _recorder.Last ?? throw new LeapError("not recording");
            _recorder.Stop();
            for (int i = 0; i < 600 && _recorder.Busy; i++) await Task.Delay(100, ct);
            Unshow();
            return _recorder.Last;
        });
        Op("status", "-> {recording, path?, seconds_left?, last?}", _ => new JsonObject
        {
            ["recording"] = _recorder.Busy, ["path"] = _recorder.CurrentPath,
            ["seconds_left"] = _recorder.SecondsLeft is { } left ? Math.Round(left, 1) : null, ["last"] = _recorder.Last?.DeepClone(),
        });
    }

    /// <summary>Pictures rendered apart from the view (Capture/Stills.cs), set by Capture/CaptureWiring.cs.</summary>
    public Capture.Stills? Stills { get; set; }

    /// <summary>The last picture saved this session (what toInventory, postcard and toProfile take without a path).</summary>
    public string? LastPicture { get; set; }

    /// <summary>More ops on SojournCapture from elsewhere in the client (Capture/CaptureWiring.cs).</summary>
    public void Add(string name, string desc, OpHandler run) => Op(name, desc, run);

    /// <summary>A plain picture of the view, as `snapshot` with no arguments: for a shutter on a controller or key.</summary>
    public async Task<string?> ShutterAsync(CancellationToken ct) => (await SnapshotAsync(new JsonObject(), ct) as JsonObject)?.Str("path");

    async Task<JsonNode?> SnapshotAsync(JsonObject a, CancellationToken ct)
    {
        var r = await TakeAsync(a, ct);
        if (r?.Str("path") is { } saved) LastPicture = saved;
        return r;
    }

    async Task<JsonObject?> TakeAsync(JsonObject a, CancellationToken ct)
    {
        if (Stills != null && !a.Has("png") && Capture.Stills.Wants(a)) return await Stills.SnapshotAsync(a, a.Str("name") ?? "", ct);
        Directory.CreateDirectory(PicturesDir);
        var path = Recorder.Unique(Path.Combine(PicturesDir, Recorder.FileName(a.Str("name"), ".png")));
        if (a.Str("png") is { Length: > 0 } b64)
        {
            byte[] png;
            try { png = Convert.FromBase64String(b64); }
            catch (FormatException) { throw new LeapError("png: not base64"); }
            if (png.Length < 24 || png[0] != 0x89 || png[1] != (byte)'P' || png[2] != (byte)'N' || png[3] != (byte)'G')
                throw new LeapError("png: not a PNG");
            int w = png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19], h = png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23];
            await File.WriteAllBytesAsync(path, png, ct);
            return new JsonObject { ["path"] = path, ["width"] = w, ["height"] = h };
        }
        var w0 = S.Region ?? throw new LeapError("not in a region yet");
        var r = await _snapshots.RenderAsync(new SnapshotRequest
        {
            Agent = S, World = w0, CameraPos = default, Focus = default, FileName = path, Width = 16384, Height = 16384, Tool = "capture",
        }, ct);
        if (!r.Ok) throw new LeapError(r.Error ?? "no picture");
        return new JsonObject { ["path"] = path, ["width"] = r.Width, ["height"] = r.Height };
    }

    async Task<JsonNode?> RecordAsync(JsonObject a, CancellationToken ct)
    {
        double seconds = a.Num("seconds", 15);
        var (path, done) = await _recorder.StartAsync(seconds, a.Int("fps", 30), a.Int("width", 960), a.Str("name"), ct);
        try
        {
            _shown?.Show(new JsonObject
            {
                ["id"] = "recording", ["at"] = "view", ["title"] = "Recording",
                ["blocks"] = new JsonArray(new JsonObject { ["stopwatch"] = true }), ["for"] = Math.Clamp(seconds, 1, Recorder.MaxSeconds) + 2,
            });
        }
        catch (LeapError) { }   // the screen is full: the recording goes on without its card
        _ = done.ContinueWith(_ => Unshow());
        if (a.Bool("wait") == false) return new JsonObject { ["recording"] = true, ["path"] = path };
        return await done.WaitAsync(ct);
    }

    void Unshow()
    {
        try { _shown?.Remove(new JsonObject { ["id"] = "recording" }); } catch (LeapError) { }
    }
}
