// What a service of an installation runs on, on Windows: the installation's own runtime (runtime\: a private .NET
// and wpfrun), as Setup registers the services it installs. A .NET program's own executable looks for a .NET installed
// on the machine, which the installation does not need and the machine may not have; so a service the installation's
// programs create (CreateService) or point at another program (ChangeServiceConfig) is registered with the command
// wpfrun would run that program with ("wpfrun prepare"), the service control manager's own process running it, its
// ServiceBase talking to the manager itself. Any other program -- a native one, or one a program not of an
// installation registers -- is registered as given, as Windows would.

using System.Diagnostics;
using System.Text.Json;

namespace WpfWebGpu.Services;

public static partial class ServiceManager
{
    /// <summary>The command line a service registered with <paramref name="commandLine"/> runs on the installation's
    /// runtime: where the calling process runs on an installation's runtime and the program is a .NET program's own
    /// executable; null to register it as given.</summary>
    public static string? OnInstallationRuntime(string commandLine)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } process) return null;
        // <root>\runtime\dotnet\dotnet.exe (or dotnetw.exe), the installation's setup.json at <root>
        string? runtime = Path.GetDirectoryName(Path.GetDirectoryName(process));
        string? root = runtime == null ? null : Path.GetDirectoryName(runtime);
        if (runtime == null || root == null) return null;
        string dotnet = Path.Combine(runtime, "dotnet", "dotnet.exe"), wpfrun = Path.Combine(runtime, "launcher", "wpfrun.dll"), setup = Path.Combine(root, "setup.json");
        if (!File.Exists(dotnet) || !File.Exists(wpfrun) || !File.Exists(setup)) return null;

        var (program, args) = SplitCommandLine(commandLine, p => p);
        if (!program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.ChangeExtension(program, ".runtimeconfig.json"))) return null;
        string? product;
        try { product = JsonDocument.Parse(File.ReadAllText(setup)).RootElement.GetProperty("product").GetString(); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or IOException) { return null; }
        if (string.IsNullOrEmpty(product)) return null;

        // wpfrun's caches where Setup keeps them for the installation's services: ProgramData\<product>\wpfrun
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), product, "wpfrun");
        var psi = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add(wpfrun);
        psi.ArgumentList.Add("prepare");
        psi.ArgumentList.Add(program);
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["WPFRUN_CACHE"] = Path.Combine(cache, "cache");
        psi.Environment["WPFRUN_LOGS"] = Path.Combine(cache, "logs");
        try
        {
            using var p = Process.Start(psi)!;
            var error = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            _ = error.Result;
            string? line = output.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith("commandline: ", StringComparison.Ordinal));
            return p.ExitCode == 0 && line != null ? line["commandline: ".Length..] : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
