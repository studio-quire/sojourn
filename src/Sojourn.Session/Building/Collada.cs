using System.Globalization;
using System.Security;
using System.Text;
using LibreMetaverse;
using SkiaSharp;
using Sojourn.Core.Agents;
using Sojourn.Render.Geometry;
using Sojourn.Render.World;

namespace Sojourn.Session.Building;

/// <summary>
/// An export (Backups.Exported: only what the person made) as a Collada model, as Radegast's Export to DAE and Firestorm's
/// Save as Collada write one: each prim's faces built as the viewer builds them (VolumeBuilder at its highest detail;
/// a sculpt from its own map, which is theirs or it wouldn't be in the export), placed and turned as they are in the
/// world with the root at the middle, Z up, metres. Faces of one look (colour and texture) share a material, so a prim
/// is at most a few of them and the model can go back up as a mesh (Second Life takes eight a part). The textures she
/// made go beside it as PNGs, named after the file; the rest are left as their colour. Meshes aren't written: their
/// shapes come from the grid, and Second Life never lets them out anyway. Each face's repeats, offset and rotation are
/// in its texture coordinates, as the viewer's default mapping applies them; planar mapping is left as default.
/// Claude Opus 5.5, 2026-11-09.
/// </summary>
public static class Collada
{
    public sealed record Written(int Prims, int Triangles, int Textures, List<string> LeftOut);

    public static Written Write(Backups.Exported e, string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full)!;
        var stem = Path.GetFileNameWithoutExtension(full);
        var left = new List<string>();

        // the textures she made, decoded once each (a sculpt's map too)
        var images = new Dictionary<UUID, (byte[] Rgba, int W, int H)>();
        foreach (var (id, (_, j2c)) in e.Textures)
            if (Decode(j2c) is { } img) images[id] = img;
        if (images.Count < e.Textures.Count) left.Add($"{e.Textures.Count - images.Count} of the textures, which couldn't be read here");

        var root = e.Prims[0];
        var files = new Dictionary<UUID, string>();
        var materials = new List<(string? File, Color4 Colour)>();
        int MaterialOf(string? file, Color4 c)
        {
            c = new Color4(Round(c.R), Round(c.G), Round(c.B), Round(c.A));
            int at = materials.FindIndex(m => m.File == file && m.Colour == c);
            if (at >= 0) return at;
            materials.Add((file, c));
            return materials.Count - 1;
        }
        string? FileOf(UUID tex)
        {
            if (files.TryGetValue(tex, out var f)) return f;
            if (!images.TryGetValue(tex, out var img) || Png(img) is not { } png) return null;
            f = $"{stem} texture {files.Count + 1}.png";
            File.WriteAllBytes(Path.Combine(dir, f), png);
            return files[tex] = f;
        }

        var geometries = new StringBuilder();
        var nodes = new StringBuilder();
        int prims = 0, triangles = 0, meshes = 0, sculptsMissing = 0;
        for (int i = 0; i < e.Prims.Count; i++)
        {
            var p = e.Prims[i];
            if (p.Sculpt is { Type: SculptType.Mesh }) { meshes++; continue; }
            VolumeFace[] faces;
            float detail = VolumeBuilder.DetailForLod(3);
            if (p.Sculpt is { } sc && sc.SculptTexture != UUID.Zero)
            {
                if (!images.TryGetValue(sc.SculptTexture, out var map)) { sculptsMissing++; continue; }
                faces = VolumeBuilder.BuildSculpt(p.PrimData, sc, map.Rgba, map.W, map.H, detail);
            }
            else faces = VolumeBuilder.BuildPrim(p.PrimData, detail);
            if (faces.Length == 0) continue;

            // in the root's frame, turned as in the world: a child's place and turn are its root's
            var rot = i == 0 ? p.Rotation : p.Rotation * root.Rotation;
            var offset = i == 0 ? Vector3.Zero : p.Position * root.Rotation;
            var pos = new List<Vector3>();
            var nrm = new List<Vector3>();
            var uv = new List<Vector2>();
            var byMaterial = new Dictionary<int, List<int>>();
            foreach (var f in faces)
            {
                var te = p.Textures?.GetFace((uint)Math.Max(0, f.TeIndex));
                var file = te != null ? FileOf(te.TextureID) : null;
                int m = MaterialOf(file, te?.RGBA ?? Color4.White);
                int baseIndex = pos.Count;
                for (int v = 0; v < f.Positions.Length; v++)
                {
                    var s = f.Positions[v];
                    pos.Add(new Vector3(s.X * p.Scale.X, s.Y * p.Scale.Y, s.Z * p.Scale.Z) * rot + offset);
                    var n = f.Normals.Length > v ? f.Normals[v] : System.Numerics.Vector3.UnitZ;
                    var turned = new Vector3(n.X / Math.Max(p.Scale.X, 1e-4f), n.Y / Math.Max(p.Scale.Y, 1e-4f), n.Z / Math.Max(p.Scale.Z, 1e-4f)) * rot;
                    nrm.Add(Vector3.Normalize(turned));
                    var t = f.TexCoords.Length > v ? f.TexCoords[v] : System.Numerics.Vector2.Zero;
                    uv.Add(te != null ? Mapped(t, te) : new Vector2(t.X, t.Y));
                }
                var list = byMaterial.TryGetValue(m, out var l) ? l : byMaterial[m] = new List<int>();
                foreach (var idx in f.Indices) list.Add(baseIndex + (int)idx);
            }
            if (byMaterial.Values.All(l => l.Count < 3)) continue;
            var name = SecurityElement.Escape(p.Properties?.Name is { Length: > 0 } pn ? pn : "Object");
            var g = $"g{i}";
            geometries.Append($"    <geometry id=\"{g}\" name=\"{name}\"><mesh>\n");
            Source(geometries, $"{g}-p", pos.SelectMany(x => new[] { x.X, x.Y, x.Z }), 3, "X", "Y", "Z");
            Source(geometries, $"{g}-n", nrm.SelectMany(x => new[] { x.X, x.Y, x.Z }), 3, "X", "Y", "Z");
            Source(geometries, $"{g}-t", uv.SelectMany(x => new[] { x.X, x.Y }), 2, "S", "T");
            geometries.Append($"      <vertices id=\"{g}-v\"><input semantic=\"POSITION\" source=\"#{g}-p\"/></vertices>\n");
            foreach (var (m, idx) in byMaterial.Where(kv => kv.Value.Count >= 3))
            {
                int count = idx.Count / 3;
                triangles += count;
                geometries.Append($"      <triangles material=\"m{m}\" count=\"{count}\">"
                    + $"<input semantic=\"VERTEX\" source=\"#{g}-v\" offset=\"0\"/><input semantic=\"NORMAL\" source=\"#{g}-n\" offset=\"0\"/>"
                    + $"<input semantic=\"TEXCOORD\" source=\"#{g}-t\" offset=\"0\" set=\"0\"/><p>");
                geometries.Append(string.Join(' ', idx.Take(count * 3)));
                geometries.Append("</p></triangles>\n");
            }
            geometries.Append("    </mesh></geometry>\n");
            // (a transform of its own: LibreMetaverse's loader skips a node without one)
            nodes.Append($"      <node id=\"n{i}\" name=\"{name}\"><matrix sid=\"transform\">1 0 0 0 0 1 0 0 0 0 1 0 0 0 0 1</matrix>"
                + $"<instance_geometry url=\"#{g}\"><bind_material><technique_common>");
            foreach (var m in byMaterial.Where(kv => kv.Value.Count >= 3).Select(kv => kv.Key))
                nodes.Append($"<instance_material symbol=\"m{m}\" target=\"#m{m}\"><bind_vertex_input semantic=\"UVSET0\" input_semantic=\"TEXCOORD\" input_set=\"0\"/></instance_material>");
            nodes.Append("</technique_common></bind_material></instance_geometry></node>\n");
            prims++;
        }
        if (meshes > 0) left.Add(meshes == 1 ? "1 mesh part (its shape is the grid's)" : $"{meshes} mesh parts (their shapes are the grid's)");
        if (sculptsMissing > 0) left.Add(sculptsMissing == 1 ? "1 sculpted part, its map not read" : $"{sculptsMissing} sculpted parts, their maps not read");

        var x = new StringBuilder();
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        x.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        x.Append("<COLLADA xmlns=\"http://www.collada.org/2005/11/COLLADASchema\" version=\"1.4.1\">\n");
        x.Append($"  <asset><contributor><authoring_tool>Sojourn Access</authoring_tool></contributor><created>{now}</created><modified>{now}</modified>"
                 + "<unit name=\"meter\" meter=\"1\"/><up_axis>Z_UP</up_axis></asset>\n");
        x.Append("  <library_images>\n");
        foreach (var (file, n) in files.Values.Select((f, n) => (f, n)))
            x.Append($"    <image id=\"i{n}\" name=\"i{n}\"><init_from>{SecurityElement.Escape(Uri.EscapeDataString(file))}</init_from></image>\n");
        x.Append("  </library_images>\n  <library_effects>\n");
        var imageIds = files.Values.Select((f, n) => (f, n)).ToDictionary(t => t.f, t => $"i{t.n}");
        for (int m = 0; m < materials.Count; m++)
        {
            var (file, c) = materials[m];
            x.Append($"    <effect id=\"e{m}\"><profile_COMMON>");
            string diffuse;
            if (file != null)
            {
                var img = imageIds[file];
                x.Append($"<newparam sid=\"{img}-surface\"><surface type=\"2D\"><init_from>{img}</init_from></surface></newparam>"
                         + $"<newparam sid=\"{img}-sampler\"><sampler2D><source>{img}-surface</source></sampler2D></newparam>");
                diffuse = $"<texture texture=\"{img}-sampler\" texcoord=\"UVSET0\"/>";
            }
            else diffuse = $"<color>{F(c.R)} {F(c.G)} {F(c.B)} {F(c.A)}</color>";
            x.Append($"<technique sid=\"common\"><lambert><diffuse>{diffuse}</diffuse>");
            if (c.A < 1) x.Append($"<transparent opaque=\"A_ONE\"><color>{F(c.R)} {F(c.G)} {F(c.B)} {F(c.A)}</color></transparent>");
            x.Append("</lambert></technique></profile_COMMON></effect>\n");
        }
        x.Append("  </library_effects>\n  <library_materials>\n");
        for (int m = 0; m < materials.Count; m++) x.Append($"    <material id=\"m{m}\" name=\"m{m}\"><instance_effect url=\"#e{m}\"/></material>\n");
        x.Append("  </library_materials>\n  <library_geometries>\n");
        x.Append(geometries);
        x.Append("  </library_geometries>\n  <library_visual_scenes>\n");
        x.Append($"    <visual_scene id=\"scene\" name=\"{SecurityElement.Escape(e.Name)}\">\n");
        x.Append(nodes);
        x.Append("    </visual_scene>\n  </library_visual_scenes>\n  <scene><instance_visual_scene url=\"#scene\"/></scene>\n</COLLADA>\n");
        File.WriteAllText(full, x.ToString(), new UTF8Encoding(false));
        return new Written(prims, triangles, files.Count, left);
    }

    static float Round(float v) => MathF.Round(v, 3);

    static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    static void Source(StringBuilder sb, string id, IEnumerable<float> values, int stride, params string[] names)
    {
        var vs = values.ToList();
        sb.Append($"      <source id=\"{id}\"><float_array id=\"{id}-a\" count=\"{vs.Count}\">");
        sb.Append(string.Join(' ', vs.Select(F)));
        sb.Append($"</float_array><technique_common><accessor source=\"#{id}-a\" count=\"{vs.Count / stride}\" stride=\"{stride}\">");
        foreach (var n in names) sb.Append($"<param name=\"{n}\" type=\"float\"/>");
        sb.Append("</accessor></technique_common></source>\n");
    }

    /// <summary>The viewer's default texture mapping (LLFace's xform): about the middle, turned, repeated, offset.</summary>
    static Vector2 Mapped(System.Numerics.Vector2 t, Primitive.TextureEntryFace te)
    {
        float s = t.X - 0.5f, u = t.Y - 0.5f;
        float cos = MathF.Cos(te.Rotation), sin = MathF.Sin(te.Rotation);
        float rs = s * cos + u * sin, ru = -s * sin + u * cos;
        return new Vector2(rs * te.RepeatU + te.OffsetU + 0.5f, ru * te.RepeatV + te.OffsetV + 0.5f);
    }

    /// <summary>A J2C texture as RGBA rows, top down (OpenJpeg's planes; grey, grey and alpha, RGB or RGBA).</summary>
    static (byte[] Rgba, int W, int H)? Decode(byte[] j2c)
    {
        if (OpenJpeg.Decode(j2c, 0) is not { } d) return null;
        var (planes, w, h) = d;
        var rgba = new byte[w * h * 4];
        int n = planes.Length;
        for (int i = 0; i < w * h; i++)
        {
            byte r = planes[0][i], g = n >= 3 ? planes[1][i] : r, b = n >= 3 ? planes[2][i] : r;
            byte a = n == 4 ? planes[3][i] : n == 2 ? planes[1][i] : (byte)255;
            rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = a;
        }
        return (rgba, w, h);
    }

    static byte[]? Png((byte[] Rgba, int W, int H) img)
    {
        try
        {
            using var bmp = new SKBitmap(new SKImageInfo(img.W, img.H, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            System.Runtime.InteropServices.Marshal.Copy(img.Rgba, 0, bmp.GetPixels(), img.Rgba.Length);
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }
        catch (Exception) { return null; }
    }
}
