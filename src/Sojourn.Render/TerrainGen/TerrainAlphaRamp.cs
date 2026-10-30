// The viewer's terrain alpha ramp: indra/newview/skins/default/textures/alpha_gradient_2d.j2c (IMG_ALPHA_GRAD_2D),
// part of the Second Life viewer distribution. Copyright (C) Linden Research, Inc. LGPL 2.1 only.
using CoreJ2K;
using CoreJ2K.Configuration;

namespace Sojourn.Render.TerrainGen;

/// <summary>
/// The 2D alpha ramp both terrain shaders sample (uniform alpha_ramp). It is a shipped image, not generated:
/// 256x256, one channel, uploaded as GL_ALPHA8 with mipmaps, clamp-to-edge, the viewer's default trilinear
/// (+anisotropic) filtering. u = composition offset (alpha falls from ~1 at u=0 to ~0 at u=1, with a different
/// irregular profile in every row); v = the per-vertex noise (texcoord1.y) choosing the row. In Vulkan use R8
/// with a component swizzle putting R in A (or sample .r where the GLSL samples .a).
/// </summary>
public static class TerrainAlphaRamp
{
    public const int Width = 256, Height = 256;

    /// <summary>IMG_ALPHA_GRAD_2D, the local texture's ID in the viewer.</summary>
    public static readonly Guid AssetId = new("38b86f85-2575-52a9-a531-23108d8da837");

    /// <summary>
    /// The decoded alpha in GL upload order: row 0 is v = 0 (the bottom of the picture; the viewer's decoders
    /// flip JPEG2000's top-down rows). The viewer decodes with KDU (official builds) or OpenJPEG; this uses
    /// CoreJ2K, and the irreversible 9/7 wavelet lets decoders differ by a unit here and there.
    /// </summary>
    public static byte[] DecodeAlpha()
    {
        var cfg = new J2KDecoderConfiguration().WithMaxDegreeOfParallelism(1);
        using var img = cfg.Decode(J2c).Image;
        if (img.Width != Width || img.Height != Height)
            throw new InvalidDataException("Unexpected alpha ramp size.");
        var topDown = new byte[Width * Height];
        img.ToComponentBytes(0, topDown);
        var glOrder = new byte[Width * Height];
        for (int y = 0; y < Height; y++)
            Array.Copy(topDown, y * Width, glOrder, (Height - 1 - y) * Width, Width);
        return glOrder;
    }

    /// <summary>The image file as shipped (4325 bytes, MD5 fcdb69c03995d4a5debb31882174d010).</summary>
    public static byte[] J2c => Convert.FromBase64String(J2cBase64);

    private const string J2cBase64 =
        "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";
}
