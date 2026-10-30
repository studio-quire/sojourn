using System.Numerics;
using System.Reflection;
using System.Text;

namespace Sojourn.Render.Gpu;

/// <summary>Generates a GLSL struct (scalar layout) mirroring a sequential C# struct, optionally with
/// #define aliases so ported viewer code can read fields by their bare uniform names.</summary>
public static class GlslMirror
{
    public static string Struct<T>(string glslName, string? aliasPrefix = null, ISet<string>? noAlias = null) where T : unmanaged
    {
        var sb = new StringBuilder();
        sb.Append("struct ").Append(glslName).Append(" {\n");
        var fields = typeof(T).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        foreach (var f in fields)
            sb.Append("    ").Append(GlslType(f.FieldType)).Append(' ').Append(f.Name).Append(";\n");
        sb.Append("};\n");
        if (aliasPrefix != null)
            foreach (var f in fields)
                if (noAlias == null || !noAlias.Contains(f.Name))
                    sb.Append("#define ").Append(f.Name).Append(' ').Append(aliasPrefix).Append('.').Append(f.Name).Append('\n');
        return sb.ToString();
    }

    static string GlslType(Type t) =>
        t == typeof(float) ? "float" :
        t == typeof(int) ? "int" :
        t == typeof(uint) ? "uint" :
        t == typeof(Vector2) ? "vec2" :
        t == typeof(Vector3) ? "vec3" :
        t == typeof(Vector4) ? "vec4" :
        t == typeof(Matrix4x4) ? "mat4" :
        throw new NotSupportedException($"GLSL mirror: {t}");
}
