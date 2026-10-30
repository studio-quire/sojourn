using LibreMetaverse;
using Sojourn.Core.Agents;
using Sojourn.Core.World;

namespace Sojourn.Core.Rendering;

/// <summary>One point-of-view render, as the viewer's LLViewerWindow.saveSnapshot would take it.</summary>
public sealed record SnapshotRequest
{
    public required AgentSession Agent { get; init; }
    public required RegionWorld World { get; init; }
    /// <summary>Camera position and the point it looks at, region coordinates of the agent's region.</summary>
    public required Vector3 CameraPos { get; init; }
    public required Vector3 Focus { get; init; }
    /// <summary>Vertical field of view in radians (the viewer's CameraAngle, default 1.047).</summary>
    public float Fov { get; init; } = 1.047f;
    public float FarClip { get; init; } = 128;
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    /// <summary>The viewer would draw its 2D UI (and hover text / name tags) into the shot.</summary>
    public bool ShowUi { get; init; }
    public bool ShowHud { get; init; } = true;
    /// <summary>COLOR or DEPTH.</summary>
    public string Type { get; init; } = "COLOR";
    /// <summary>Where to write the image. Tools open it with PIL, which reads PNG whatever the extension.</summary>
    public required string FileName { get; init; }
    /// <summary>Avatars (ids) to leave out, mirroring hideAvatars with in_snapshots.</summary>
    public IReadOnlyCollection<UUID> HiddenAvatars { get; init; } = Array.Empty<UUID>();
    /// <summary>The tool that asked (sl.py's command: look, shot, view, cam, overhead, selfie), for the wall's label.
    /// A sojourn extension of the op (`tool`); null when the caller didn't say.</summary>
    public string? Tool { get; init; }
    /// <summary>With <see cref="ShowUi"/>: a menu ("panel", "feed", "reader", ...) also saved close up beside the
    /// shot, at about its own pixels. A sojourn extension of the op (`close`).</summary>
    public string? Close { get; init; }
}

public sealed record SnapshotResult(bool Ok, string? Error = null, int Width = 0, int Height = 0)
{
    /// <summary>With ShowUi: the menus composited over the eye, and those that couldn't be (the renderer's own images).</summary>
    public IReadOnlyList<string>? Layers { get; init; }
    public IReadOnlyList<string>? LeftOut { get; init; }
    /// <summary>The close-up's path, or why there's none.</summary>
    public string? Close { get; init; }
    public string? CloseError { get; init; }
}

/// <summary>
/// The seam where phase 2's renderer plugs in: Sojourn.Render implements this and the host registers it with
/// <see cref="Snapshots.Provider"/>. Until then every snapshot op answers "rendering not available yet".
/// </summary>
public interface ISnapshotProvider
{
    Task<SnapshotResult> RenderAsync(SnapshotRequest request, CancellationToken ct);
}

/// <summary>One saved spotlight frame.</summary>
public sealed record SpotlightSave(string FileName, DateTime Time, int Width, int Height);

/// <summary>
/// The spotlight: one agent at a time rendered continuously for the render wall. The spotlit agent can take its
/// newest frame at once, or a short burst. Implemented by Sojourn.Render next to the snapshot provider.
/// </summary>
public interface ISpotlight
{
    /// <summary>The spotlit agent's id, or null while the spotlight is off.</summary>
    UUID? Current { get; }
    float Fps { get; }
    /// <summary>Put the spotlight on an agent (null = off). False if the agent isn't online.</summary>
    bool Set(AgentSession? agent, float? fps = null);
    /// <summary>Write the agent's newest spotlight frame (JPEG) to a file. Fails unless the agent is spotlit.</summary>
    Task<SpotlightSave> SaveLatestAsync(AgentSession agent, string fileName, CancellationToken ct);
    /// <summary>Write `count` frames spread over `span` to files named from `filePrefix` (prefix_00.jpg ...).</summary>
    Task<IReadOnlyList<SpotlightSave>> SaveBurstAsync(AgentSession agent, int count, TimeSpan span, string filePrefix, CancellationToken ct);
}

public static class Snapshots
{
    public const string NotAvailable = "rendering not available: the host has no renderer running, so there is no picture";

    /// <summary>The process-wide renderer, or null. Set once at startup.</summary>
    public static ISnapshotProvider? Provider { get; set; }

    /// <summary>The spotlight, or null without a renderer.</summary>
    public static ISpotlight? Spotlight { get; set; }
}
