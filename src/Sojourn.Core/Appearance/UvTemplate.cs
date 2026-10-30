using System.Text;
using SkiaSharp;

namespace Sojourn.Core.Appearance;

/// <summary>
/// A painting guide for the system avatar's textures: the UV layout of one body part's mesh (the viewer's .llm files,
/// which the renderer ships in Character/) drawn as filled triangles with outlines on a transparent image, in the
/// texture's own orientation (image top = v 1). A skin's head, upper and lower textures, an iris and a hair texture
/// map onto these layouts; painting inside the outlines is what shows on the body.
/// </summary>
public static class UvTemplate
{
    public static readonly IReadOnlyDictionary<string, string> Parts = new Dictionary<string, string>
    {
        ["head"] = "avatar_head.llm", ["upper"] = "avatar_upper_body.llm", ["lower"] = "avatar_lower_body.llm",
        ["eyes"] = "avatar_eye.llm", ["hair"] = "avatar_hair.llm", ["skirt"] = "avatar_skirt.llm",
    };

    /// <summary>The part's UV layout as a PNG, size x size.</summary>
    public static byte[] Png(string part, int size)
    {
        if (!Parts.TryGetValue(part, out var file))
            throw new Sojourn.Core.Leap.LeapError($"part is one of {string.Join(", ", Parts.Keys)}");
        var (uv, tris) = Read(Path.Combine(AppContext.BaseDirectory, "Character", file));
        using var bmp = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Transparent);
            using var fill = new SKPaint { Color = new SKColor(200, 200, 200, 110), Style = SKPaintStyle.Fill, IsAntialias = true };
            using var edge = new SKPaint { Color = new SKColor(40, 40, 40, 230), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, size / 1024f), IsAntialias = true };
            SKPoint P(int i) => new(uv[i].u * size, (1 - uv[i].v) * size);
            foreach (var (a, b, c) in tris)
            {
                using var path = new SKPath();
                path.MoveTo(P(a));
                path.LineTo(P(b));
                path.LineTo(P(c));
                path.Close();
                canvas.DrawPath(path, fill);
                canvas.DrawPath(path, edge);
            }
        }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Texture coordinates and triangles of a base .llm mesh (the layout LlmMesh.Load reads).</summary>
    static ((float u, float v)[] uv, List<(int, int, int)> tris) Read(string path)
    {
        using var br = new BinaryReader(File.OpenRead(path));
        var header = Encoding.ASCII.GetString(br.ReadBytes(24)).TrimEnd('\0');
        if (!header.StartsWith("Linden Binary Mesh 1.0")) throw new InvalidDataException($"{path}: not a Linden binary mesh");
        bool weights = br.ReadByte() > 0;
        br.ReadByte();                     // hasDetailTexCoords
        br.ReadBytes(12 + 12 + 1 + 12);    // position, rotation, rotation order, scale
        int n = br.ReadUInt16();
        br.ReadBytes(n * 12 * 3);          // coords, normals, binormals
        var uv = new (float, float)[n];
        for (int i = 0; i < n; i++) uv[i] = (br.ReadSingle(), br.ReadSingle());
        if (weights) br.ReadBytes(n * 4);
        int faces = br.ReadUInt16();
        var tris = new List<(int, int, int)>(faces);
        for (int f = 0; f < faces; f++) tris.Add((br.ReadInt16(), br.ReadInt16(), br.ReadInt16()));
        return (uv, tris);
    }
}
