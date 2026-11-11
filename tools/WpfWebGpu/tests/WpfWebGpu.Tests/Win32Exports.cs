using System.Reflection;
using System.Runtime.Loader;
using Xunit;

namespace WpfWebGpu.Tests;

/// <summary>
/// The stack's user-mode Windows (WpfWebGpu.Win32), built beside these tests, for calling its exports directly. In a
/// load context of its own: Assembly.LoadFrom would install a process-wide resolver, and the other tests would then
/// find assemblies (WindowsBase among them) in its folder. What this process already has -- the registry shim the
/// exports read, the framework -- it shares.
/// </summary>
internal static class Win32Exports
{
    private static readonly Lazy<Type?> s_exports = new(Load);

    public static MethodInfo Method(string name)
    {
        Type? exports = s_exports.Value;
        Assert.SkipWhen(exports == null, "WpfWebGpu.Win32 is not built (tools/WpfWebGpu/build.sh)");
        return exports!.GetMethod(name) ?? throw new MissingMethodException("WpfWebGpu.Win32.Exports", name);
    }

    private static Type? Load()
    {
        string win32 = typeof(Win32Exports).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "WpfWebGpuWin32").Value!;
        if (!File.Exists(win32)) return null;
        return new Context(win32).LoadFromAssemblyPath(win32).GetType("WpfWebGpu.Win32.Exports");
    }

    private sealed class Context(string main) : AssemblyLoadContext("WpfWebGpu.Win32 exports")
    {
        private readonly AssemblyDependencyResolver _resolver = new(main);

        protected override Assembly? Load(AssemblyName name)
        {
            // shared with the tests where they have it; otherwise from the shim's own folder
            if (Default.Assemblies.Any(a => AssemblyName.ReferenceMatchesDefinition(name, a.GetName()) || a.GetName().Name == name.Name)) return null;
            string? path = _resolver.ResolveAssemblyToPath(name);
            return path == null ? null : LoadFromAssemblyPath(path);
        }
    }
}
