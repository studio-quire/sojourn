using UUID = LibreMetaverse.UUID;

namespace Sojourn.Render.Foliage;

/// <summary>
/// Triangle list for one tree or grass object, plus what the viewer's draw pool binds for it.
/// Front faces are counter-clockwise (GL convention, same as the viewer's prims); every leaf/blade quad comes
/// with a second, oppositely wound copy, so back-face culling stays on.
/// </summary>
public sealed class FoliageMesh
{
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Vector2[] TexCoords { get; init; }
    public required uint[] Indices { get; init; }

    /// <summary>Diffuse texture (tree: bark on the left half, leaves on the right; grass: blade texture). Wrap addressing.</summary>
    public UUID Texture { get; init; }

    /// <summary>Fragments with texture alpha below this are discarded (minimum_alpha in the G-buffer pass).</summary>
    public float AlphaCutoff { get; init; } = 0.5f;

    /// <summary>minimum_alpha in the sun/shadow-map pass (treeShadowF.glsl for both trees and grass).</summary>
    public float ShadowAlphaCutoff { get; init; } = 0.5f;

    /// <summary>Vertex colour; white for both trees and grass.</summary>
    public Vector4 Color { get; init; } = Vector4.One;
}
