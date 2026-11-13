// Windows: the processor a program of an installation runs as. The installer carries a runtime per processor its
// programs run as (runtime\ its own; runtime-x64\, runtime-x86\ besides, which Windows on Arm and Windows x64
// emulate), and its setup.json says each program's default. A launcher asked to run a program -- a Start-menu entry,
// a service's preparation, run.cmd, another program's Process.Start -- whose processor is another hands it to that
// processor's launcher, as Windows starts an x64 program as x64 whoever starts it. `run --processor x64` asks for
// one (the Start menu's "Name (x64)", a program's way to fall back on).

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WpfWebGpu;

internal static class ProcessorHandoff
{
    public static string Name(Architecture a) => a switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        _ => a.ToString().ToLowerInvariant(),
    };

    /// <summary>This process's processor where it is not the machine's (an x64 runtime on Arm): what an emulated
    /// run's own files are kept apart by.</summary>
    public static string? Emulated() =>
        RuntimeInformation.ProcessArchitecture == RuntimeInformation.OSArchitecture ? null : Name(RuntimeInformation.ProcessArchitecture);

    /// <summary>The runtime folder <paramref name="target"/> runs on, where that is another processor's: the folder,
    /// and the processor; null to run it here.</summary>
    public static (string Runtime, string Processor)? Elsewhere(string target, string? asked)
    {
        if (!OperatingSystem.IsWindows()) return null;
        // this launcher's own processor: run here (Setup preparing a service names it, before setup.json is written)
        string mine = Name(RuntimeInformation.ProcessArchitecture);
        if (asked != null && asked.Equals(mine, StringComparison.OrdinalIgnoreCase)) return null;
        // <root>\runtime[-<processor>]\launcher\wpfrun.dll, <root>\setup.json
        string? runtime = Path.GetDirectoryName(Path.GetDirectoryName(typeof(ProcessorHandoff).Assembly.Location));
        string? root = runtime == null ? null : Path.GetDirectoryName(runtime);
        if (root == null || !File.Exists(Path.Combine(root, "setup.json"))) return asked == null ? null : throw Unknown(asked);
        Dictionary<string, string> runtimes = new(StringComparer.OrdinalIgnoreCase), programs = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "setup.json")));
            if (doc.RootElement.TryGetProperty("runtimes", out var r))
                foreach (var p in r.EnumerateObject()) runtimes[p.Name] = p.Value.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("programs", out var g))
                foreach (var p in g.EnumerateObject()) programs[p.Name] = p.Value.GetString() ?? "";
        }
        catch (Exception e) when (e is JsonException or IOException) { return null; }

        string? wanted = asked;
        if (wanted == null)
        {
            // the program's default, by its path in the installation's app\ folder
            string app = Path.Combine(root, "app") + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(target);
            if (!full.StartsWith(app, StringComparison.OrdinalIgnoreCase)) return null;
            string exe = full[app.Length..].Replace('\\', '/');
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = Path.ChangeExtension(exe, ".exe");
            if (!programs.TryGetValue(exe, out wanted)) return null;
        }
        if (wanted.Equals(mine, StringComparison.OrdinalIgnoreCase)) return null;
        if (!runtimes.TryGetValue(wanted, out string? folder) || !Directory.Exists(Path.Combine(root, folder)))
        {
            if (asked != null) throw Unknown(asked);
            return null;    // not carried: it runs here, as the processor this runtime is for
        }
        return (Path.Combine(root, folder), wanted);
    }

    private static UserError Unknown(string processor) =>
        new($"this installation carries no {processor} runtime: its Packager's manifest includes none of its programs for {processor}");

    /// <summary>Runs the launcher of <paramref name="runtime"/> with <paramref name="args"/> (the processor named, so
    /// it runs the program itself), passing on what it writes and its exit code: the console dotnet.exe where this one
    /// has a console, the window launcher (wpfrunw.exe) where it has none.</summary>
    public static int Run(string runtime, string processor, string[] args)
    {
        bool window = GetConsoleWindow() == IntPtr.Zero && File.Exists(Path.Combine(runtime, "wpfrunw.exe"));
        var psi = new ProcessStartInfo(window ? Path.Combine(runtime, "wpfrunw.exe") : Path.Combine(runtime, "dotnet", "dotnet.exe")) { UseShellExecute = false };
        if (!window) psi.ArgumentList.Add(Path.Combine(runtime, "launcher", "wpfrun.dll"));
        psi.ArgumentList.Add(args[0]);
        psi.ArgumentList.Add("--processor");
        psi.ArgumentList.Add(processor);
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--processor" && i + 1 < args.Length) { i++; continue; }
            psi.ArgumentList.Add(args[i]);
        }
        bool relayOut = window && Console.IsOutputRedirected, relayError = window && Console.IsErrorRedirected;
        psi.RedirectStandardOutput = relayOut;
        psi.RedirectStandardError = relayError;
        using var process = Process.Start(psi) ?? throw new UserError($"could not start {psi.FileName}");
        var relayed = Task.WhenAll(
            relayOut ? process.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput()) : Task.CompletedTask,
            relayError ? process.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError()) : Task.CompletedTask);
        process.WaitForExit();
        relayed.Wait();
        return process.ExitCode;
    }

    [DllImport("kernel32")]
    private static extern IntPtr GetConsoleWindow();
}
