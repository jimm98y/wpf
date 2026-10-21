using System.Runtime.CompilerServices;

namespace WpfWebGpu.Tests;

/// <summary>Every test in this process keeps the shims' persistent state in a temp folder.</summary>
internal static class TestEnvironment
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "wpfrun-tests-" + Environment.ProcessId);

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("WPFWEBGPU_REGISTRY_DIR", Path.Combine(Root, "registry"));
        Environment.SetEnvironmentVariable("WPFWEBGPU_DPAPI_DIR", Path.Combine(Root, "dpapi"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        };
    }

    public static string NewDirectory(string name)
    {
        string dir = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
