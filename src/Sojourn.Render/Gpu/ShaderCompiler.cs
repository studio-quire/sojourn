using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;

namespace Sojourn.Render.Gpu;

public enum ShaderStage { Vertex, Fragment, Compute }

/// <summary>
/// GLSL to SPIR-V at runtime (shaderc), with textual #include, permutation defines, and an on-disk SPIR-V cache
/// keyed by the fully preprocessed source. Shaders live as plain files beside the assembly, so editing one and
/// rerunning needs no rebuild.
/// </summary>
public sealed unsafe partial class ShaderCompiler : IDisposable
{
    readonly VulkanContext _vk;
    readonly string _root;
    readonly string _cacheDir;
    readonly Shaderc _sc;
    readonly Compiler* _compiler;
    readonly Dictionary<string, ShaderModule> _modules = new();

    public ShaderCompiler(VulkanContext vk, string shaderRoot, string cacheDir)
    {
        _vk = vk;
        _root = shaderRoot;
        _cacheDir = cacheDir;
        Directory.CreateDirectory(cacheDir);
        _sc = Shaderc.GetApi();
        _compiler = _sc.CompilerInitialize();
    }

    [GeneratedRegex("^\\s*#include\\s+\"([^\"]+)\"\\s*$", RegexOptions.Multiline)]
    private static partial Regex IncludeRx();

    readonly Dictionary<string, string> _virtual = new();

    /// <summary>Register generated source that shaders can #include by name (e.g. struct mirrors of C# types).</summary>
    public void AddVirtualFile(string name, string source) => _virtual[name] = source;

    string Preprocess(string file, HashSet<string> seen)
    {
        var path = Path.Combine(_root, file);
        if (!seen.Add(Path.GetFullPath(path))) return "";
        var src = _virtual.TryGetValue(file, out var v) ? v : File.ReadAllText(path);
        return IncludeRx().Replace(src, m => Preprocess(m.Groups[1].Value, seen) + "\n");
    }

    public ShaderModule Get(string file, ShaderStage stage, params string[] defines)
    {
        var key = file + "|" + stage + "|" + string.Join(",", defines);
        if (_modules.TryGetValue(key, out var mod)) return mod;

        var body = Preprocess(file, new HashSet<string>());
        var sb = new StringBuilder();
        // #version must come first; pull it off the top of the main file
        var lines = body.Split('\n');
        int start = 0;
        if (lines[0].TrimStart().StartsWith("#version")) { sb.Append(lines[0]).Append('\n'); start = 1; }
        else sb.Append("#version 460\n");
        sb.Append("#extension GL_EXT_nonuniform_qualifier : require\n");
        sb.Append("#extension GL_EXT_scalar_block_layout : require\n");
        sb.Append("#extension GL_EXT_buffer_reference : require\n");
        sb.Append("#extension GL_EXT_shader_explicit_arithmetic_types_int64 : require\n");
        sb.Append(stage switch { ShaderStage.Vertex => "#define VERTEX_SHADER 1\n", ShaderStage.Fragment => "#define FRAGMENT_SHADER 1\n", _ => "#define COMPUTE_SHADER 1\n" });
        // multiview (both eyes in one pass): the view being drawn, 0 outside a multiview pass (common.glsl VIEW, SKY)
        sb.Append(stage == ShaderStage.Compute ? "#define VIEW_INDEX 0\n" : "#extension GL_EXT_multiview : require\n#define VIEW_INDEX gl_ViewIndex\n");
        foreach (var d in defines)
        {
            var eq = d.IndexOf('=');
            sb.Append("#define ").Append(eq < 0 ? d : d[..eq] + " " + d[(eq + 1)..]).Append('\n');
        }
        sb.Append("#line 1\n");
        for (int i = start; i < lines.Length; i++) sb.Append(lines[i]).Append('\n');
        var source = sb.ToString();

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stage + "\n" + source)))[..24];
        var cachePath = Path.Combine(_cacheDir, $"{Path.GetFileNameWithoutExtension(file)}-{stage}-{hash}.spv");
        byte[] spv;
        if (File.Exists(cachePath)) spv = File.ReadAllBytes(cachePath);
        else
        {
            spv = Compile(source, file, stage);
            File.WriteAllBytes(cachePath, spv);
        }
        fixed (byte* p = spv)
        {
            var ci = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spv.Length, PCode = (uint*)p };
            VulkanContext.Check(_vk.Api.CreateShaderModule(_vk.Device, in ci, null, out mod), "vkCreateShaderModule");
        }
        _modules[key] = mod;
        return mod;
    }

    byte[] Compile(string source, string name, ShaderStage stage)
    {
        var opts = _sc.CompileOptionsInitialize();
        _sc.CompileOptionsSetTargetEnv(opts, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan13);
        _sc.CompileOptionsSetOptimizationLevel(opts, OptimizationLevel.Performance);
        var kind = stage switch
        {
            ShaderStage.Vertex => ShaderKind.VertexShader,
            ShaderStage.Fragment => ShaderKind.FragmentShader,
            _ => ShaderKind.ComputeShader,
        };
        var srcBytes = Encoding.UTF8.GetBytes(source);
        CompilationResult* res;
        fixed (byte* s = srcBytes)
        {
            res = _sc.CompileIntoSpv(_compiler, s, (nuint)srcBytes.Length, kind, name, "main", opts);
        }
        try
        {
            if (_sc.ResultGetCompilationStatus(res) != CompilationStatus.Success)
            {
                var err = _sc.ResultGetErrorMessageS(res);
                throw new InvalidOperationException($"Shader {name} ({stage}) failed:\n{err}");
            }
            var len = (int)_sc.ResultGetLength(res);
            var bytes = new byte[len];
            new ReadOnlySpan<byte>(_sc.ResultGetBytes(res), len).CopyTo(bytes);
            return bytes;
        }
        finally
        {
            _sc.ResultRelease(res);
            _sc.CompileOptionsRelease(opts);
        }
    }

    public void Dispose()
    {
        foreach (var m in _modules.Values) _vk.Api.DestroyShaderModule(_vk.Device, m, null);
        _modules.Clear();
        _sc.CompilerRelease(_compiler);
    }
}
