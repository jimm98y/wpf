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
        // http.sys's registrations and service: the tests' own, started on demand through the launcher built beside
        // them (an HttpListener or UseHttpSys server registers its prefixes with it)
        Environment.SetEnvironmentVariable("WPFWEBGPU_HTTPSYS_DIR", Path.Combine(Root, "httpsys"));
        string dotnetRoot = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
        Environment.SetEnvironmentVariable("WPFWEBGPU_LAUNCHER", Path.Combine(dotnetRoot, "dotnet"));
        Environment.SetEnvironmentVariable("WPFWEBGPU_LAUNCHER_DLL", Path.Combine(AppContext.BaseDirectory, "wpfrun.dll"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            // the tests' http.sys service, where one was started
            string pid = Path.Combine(Root, "httpsys", "service.pid");
            try { if (int.TryParse(File.ReadAllText(pid), out int id)) System.Diagnostics.Process.GetProcessById(id).Kill(); } catch { }
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
