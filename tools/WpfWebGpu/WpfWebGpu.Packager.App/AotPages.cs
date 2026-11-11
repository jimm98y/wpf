// The web pages compiled ahead of time: built by the launcher of the runtime the Packager runs on (runtime/launcher,
// beside app/), which uses the machine's .NET SDK and its wasm-tools workload where they are installed.

using System.Diagnostics;
using System.IO;
using WpfWebGpu.Packager.Desktop;

namespace WpfWebGpu.Packager.App;

internal static class AotPages
{
    private static string? s_reason;
    private static bool s_checked;

    public static void Install()
    {
        WebPages.AotUnavailable = Unavailable;
        WebPages.AotBuilder = (page, log) => Launcher(log, page.Exe, page.StateDir, page.OutDir) == 0;
    }

    /// <summary>Why the pages cannot be compiled ahead of time here; null where they can.</summary>
    public static string? Unavailable()
    {
        if (s_checked) return s_reason;
        var lines = new List<string>();
        int code = Launcher(lines.Add, "--check");
        s_reason = code switch
        {
            0 => null,
            -1 => "the Packager's runtime/launcher is not found",
            _ => lines.LastOrDefault(l => l.Length > 0) ?? "the launcher could not tell",
        };
        s_checked = true;
        return s_reason;
    }

    // wpfrun aot-page <arguments>, its output line by line into log; -1 when there is no launcher
    private static int Launcher(Action<string> log, params string[] arguments)
    {
        string root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!;
        string launcher = Path.Combine(root, "runtime", "launcher", "wpfrun.dll");
        string dotnet = Path.Combine(root, "runtime", "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(launcher) || !File.Exists(dotnet)) return -1;
        var psi = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add(launcher);
        psi.ArgumentList.Add("aot-page");
        foreach (string a in arguments) psi.ArgumentList.Add(a);
        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) log(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) log(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        return process.ExitCode;
    }
}
