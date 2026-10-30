// Batch B3b's avatars without the GPU: `check` (the logic, no grid) and `send` (another person's viewer effects on the
// local grid, for tools/avatars.py). Written by Claude Opus 5.5, 2026-10-29.
using System.Reflection;
using System.Runtime.Loader;

var outDir = typeof(Tool).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "SojournOut").Value!;
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    var p = Path.Combine(outDir, name.Name + ".dll");
    return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
};
return args.Length > 0 && args[0] == "send" ? await Tool.Send(args[1..]) : Tool.Check(outDir);

static partial class Tool { }
