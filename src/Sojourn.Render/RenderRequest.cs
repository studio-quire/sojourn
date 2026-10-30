using LibreMetaverse;

namespace Sojourn.Render;

public enum ImageEncoding { Png, Jpeg, Rgba }

/// <summary>Who a render is for.</summary>
public sealed record AgentRef(UUID Id, string Name);

/// <summary>
/// One on-demand render. The camera is given in the coordinates of the region it stands in (RegionHandle), like
/// the viewer's agent-region coordinates: either a focus point (LookAt) or a direction (Forward).
/// </summary>
public sealed record RenderRequest
{
    public required ulong RegionHandle { get; init; }
    public required Vector3 CameraPosition { get; init; }
    /// <summary>Point the camera looks at (region coords). Takes precedence over Forward.</summary>
    public Vector3? LookAt { get; init; }
    /// <summary>Look direction (region frame, need not be normalised) when no LookAt is given.</summary>
    public Vector3? Forward { get; init; }
    public Vector3 Up { get; init; } = Vector3.UnitZ;
    /// <summary>Vertical field of view in radians (the viewer's CameraAngle, default 60 degrees).</summary>
    public float VerticalFov { get; init; } = 1.047197551f;
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    /// <summary>Draw distance in metres (RenderFarClip): 128 on the Mid to High-Ultra presets, 256 on Ultra.</summary>
    public float DrawDistance { get; init; } = 128f;
    /// <summary>Draw what the viewer draws with UI on (name tags). The viewer's chrome itself is never drawn.</summary>
    public bool ShowUi { get; init; }
    public ImageEncoding Encoding { get; init; } = ImageEncoding.Png;
    public int JpegQuality { get; init; } = 90;
    /// <summary>How long to wait for textures/meshes/materials in view to arrive before rendering anyway.</summary>
    public TimeSpan SettleTimeout { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary>Environment time (UTC); null = now.</summary>
    public DateTime? Time { get; init; }
    /// <summary>A fixed sky for this view, like a resident's World > Environment choice (the viewer's preset sky
    /// assets: Environment.DefaultSettingsAssets.SunriseId / MiddayId / SunsetId / MidnightId). Null = the region's.</summary>
    public UUID? LocalSky { get; init; }
    /// <summary>Avatars not to draw (e.g. the agent's own body in a first-person view).</summary>
    public IReadOnlyCollection<UUID>? HideAvatars { get; init; }
    /// <summary>The person's own avatar drawn for their headset view (placed under the head, head hidden, arms to the hands).</summary>
    public SelfBody? Self { get; init; }
    public AgentRef? Agent { get; init; }
    /// <summary>"pov" (the agent's own camera) or "free" (a placed camera).</summary>
    public string Kind { get; init; } = "pov";
    /// <summary>What asked for it (e.g. "look", "view N", "selfie").</summary>
    public string Source { get; init; } = "";
    /// <summary>Higher runs first among queued requests.</summary>
    public int Priority { get; init; }
}

public sealed record RenderTimings(double QueueMs, double SettleMs, double CpuMs, double GpuMs, double EncodeMs, double TotalMs);

public sealed record RenderResult
{
    public required byte[] Image { get; init; }
    public required ImageEncoding Encoding { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required RenderTimings Timings { get; init; }
    /// <summary>Assets still missing when the render was taken (0 = fully settled).</summary>
    public int PendingAssets { get; init; }
    public string ContentType => Encoding switch { ImageEncoding.Png => "image/png", ImageEncoding.Jpeg => "image/jpeg", _ => "application/octet-stream" };
}
