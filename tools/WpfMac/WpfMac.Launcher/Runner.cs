// `wpfmac run`: generate, then `dotnet exec` the app's own .dll in place.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WpfMac;

internal sealed class Runner
{
    public required AppFiles App { get; init; }
    public required string OverlayDir { get; init; }
    public bool Verbose { get; init; }
    public string? TitlePrefix { get; init; }
    public bool Offline { get; init; }
    public string? ScreenshotDirectory { get; init; }
    /// <summary>A suffix for every named kernel object (see WpfMac.Detours/Isolation.cs); null for none.</summary>
    public string? Isolate { get; init; }
    /// <summary>macOS: let an abort crash as usual (crash report, dialog) instead of exiting quietly.</summary>
    public bool CrashReports { get; init; }
    public int ScreenshotInterval { get; init; } = 5;

    public sealed record Prepared(string RuntimeConfig, string Deps, string LogPath, IReadOnlyList<string> Notes, IReadOnlyCollection<string> Replaced)
    {
        public required ProcessStartInfo StartInfo { get; init; }
        public required JsonObject MergedDeps { get; init; }
        /// <summary>The --additionalprobingpath roots, in the order the host is given them.</summary>
        public required IReadOnlyList<string> ProbeRoots { get; init; }
    }

    /// <summary>Writes the generated files into the cache and builds the command that runs the app.</summary>
    public Prepared Prepare(IEnumerable<string> appArgs)
    {
        string rid = HostInfo.Rid;
        string cacheDir = Path.Combine(Locations.CacheRoot, "apps", $"{App.Name}-{Hash(App.Directory)}");
        Directory.CreateDirectory(cacheDir);
        string logPath = Path.Combine(Locations.LogRoot, $"{App.Name}.log");

        var payloadDeps = DepsMerger.ReadJson(Path.Combine(OverlayDir, Locations.PayloadName + ".deps.json"));
        string payloadRcPath = Path.Combine(OverlayDir, Locations.PayloadName + ".runtimeconfig.json");
        var payloadRc = File.Exists(payloadRcPath) ? DepsMerger.ReadJson(payloadRcPath) : null;

        var fetcher = new PackageFetcher(Path.Combine(Locations.CacheRoot, "packages"), Offline);
        var merger = new DepsMerger(rid, fetcher);
        JsonObject? appDeps = File.Exists(App.Deps) ? DepsMerger.ReadJson(App.Deps) : null;
        JsonObject deps = merger.Merge(App, appDeps, OverlayDir, payloadDeps);
        // app assemblies with [ComImport] types: rewritten copies in the cache (Documentation/comimport-rewriter.md)
        var comNotes = new List<string>();
        ComImportStep.Apply(deps, App.Directory, OverlayDir, Locations.CacheRoot, HostInfo.IsWindows,
            Environment.GetEnvironmentVariable("WPF_COMIMPORT_REWRITE"), comNotes);

        var props = new Dictionary<string, string>
        {
            ["STARTUP_HOOKS"] = Path.Combine(OverlayDir, "WpfMac.Bootstrap.dll"),
            ["WpfMac.AppDirectory"] = App.Directory + Path.DirectorySeparatorChar,
            ["WpfMac.LogPath"] = logPath,
            ["WpfMac.OverlayDirectory"] = OverlayDir,
            ["WpfMac.StateDirectory"] = Locations.StateRoot,
            // Files generated for this app at run time (Windows: the user32 dispatch DLL).
            ["WpfMac.CacheDirectory"] = cacheDir,
            // The WebGPU compositor; the SDK sets the same switch on the heads where it is not already
            // the default.
            ["Switch.System.Windows.Media.UseWebGpuComposition"] = "true",
        };
        string fonts = Path.Combine(OverlayDir, "fonts");
        if (Directory.Exists(fonts)) props["WpfWebGpu.FontDirectory"] = fonts;
        if (!string.IsNullOrEmpty(TitlePrefix)) props["WpfMac.TitlePrefix"] = TitlePrefix;
        if (!string.IsNullOrEmpty(Isolate)) props["WpfMac.Isolate"] = Isolate;
        if (CrashReports) props["WpfMac.CrashReports"] = "true";
        if (!string.IsNullOrEmpty(ScreenshotDirectory))
        {
            props["WpfMac.ScreenshotDirectory"] = Path.GetFullPath(ScreenshotDirectory);
            props["WpfMac.ScreenshotInterval"] = ScreenshotInterval.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        JsonObject rc = RuntimeConfigRewriter.Rewrite(DepsMerger.ReadJson(App.RuntimeConfig), payloadRc, props);

        string rcPath = Path.Combine(cacheDir, App.Name + ".runtimeconfig.json");
        string depsPath = Path.Combine(cacheDir, App.Name + ".deps.json");
        var json = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(rcPath, rc.ToJsonString(json));
        File.WriteAllText(depsPath, deps.ToJsonString(json));
        File.WriteAllLines(Path.Combine(cacheDir, "merge-report.txt"),
            new[] { $"app      {App.Dll}", $"overlay  {OverlayDir}", $"rid      {rid}", $"replaced {string.Join(", ", merger.Replaced)}" }
                .Concat(merger.Notes).Concat(comNotes));

        var psi = new ProcessStartInfo(HostInfo.DotnetMuxer())
        {
            UseShellExecute = false,
            WorkingDirectory = App.Directory,      // as Explorer starts an .exe: in its own folder
        };
        string[] probeRoots =
        {
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(OverlayDir))!,
            Path.GetDirectoryName(App.Directory)!,
            fetcher.Root,
            Locations.CacheRoot,            // comimport/: the rewritten COM-import assemblies
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--runtimeconfig");
        psi.ArgumentList.Add(rcPath);
        psi.ArgumentList.Add("--depsfile");
        psi.ArgumentList.Add(depsPath);
        foreach (string root in probeRoots)
        {
            psi.ArgumentList.Add("--additionalprobingpath");
            psi.ArgumentList.Add(root);
        }
        psi.ArgumentList.Add(App.Dll);
        foreach (string a in appArgs) psi.ArgumentList.Add(a);

        if (!HostInfo.IsWindows)
        {
            // The registry shim's store. Shared by every app run through wpfmac, like the real registry,
            // and kept with the rest of wpfmac's state so that removing wpfmac removes it.
            psi.Environment["WPFWEBGPU_REGISTRY_DIR"] = Path.Combine(Locations.StateRoot, "registry");
            // The ProtectedData shim's per-user and per-machine keys, likewise.
            psi.Environment["WPFWEBGPU_DPAPI_DIR"] = Path.Combine(Locations.StateRoot, "dpapi");
        }
        // The app's own children must not inherit a startup hook a parent shell may have set.
        psi.Environment.Remove("DOTNET_STARTUP_HOOKS");

        return new Prepared(rcPath, depsPath, logPath, merger.Notes.Concat(comNotes).ToList(), merger.Replaced)
        {
            StartInfo = psi,
            MergedDeps = deps,
            ProbeRoots = probeRoots,
        };
    }

    public int Run(IEnumerable<string> appArgs)
    {
        Prepared p = Prepare(appArgs);
        if (Verbose)
        {
            foreach (string n in p.Notes) Console.Error.WriteLine($"wpfmac: {n}");
            Console.Error.WriteLine($"wpfmac: {p.StartInfo.FileName} {string.Join(' ', p.StartInfo.ArgumentList.Select(Quote))}");
        }
        Console.Error.WriteLine($"wpfmac: log {p.LogPath}");
        using var process = Process.Start(p.StartInfo) ?? throw new UserError("could not start dotnet.");

        // wpfmac is only a parent: whatever stops it stops the app too, so killing the launcher (a
        // terminal closing, a test runner's timeout) never leaves an app running on its own.
        var forward = new List<PosixSignalRegistration>();
        if (!HostInfo.IsWindows)
        {
            foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP })
            {
                forward.Add(PosixSignalRegistration.Create(signal, ctx =>
                {
                    ctx.Cancel = true;
                    try { if (!process.HasExited) Unix.Kill(process.Id, signal == PosixSignal.SIGTERM ? 15 : 1); } catch { }
                }));
            }
        }
        process.WaitForExit();
        foreach (var r in forward) r.Dispose();
        // The whole code: a shell keeps only its low byte (0xC000007F and 127 look alike there).
        if (process.ExitCode != 0)
            Console.Error.WriteLine($"wpfmac: the app exited with code 0x{process.ExitCode:X8} ({process.ExitCode})");
        return process.ExitCode;
    }

    private static string Hash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.ToLowerInvariant())))[..12].ToLowerInvariant();

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}

internal static partial class Unix
{
    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    internal static partial int Kill(int pid, int signal);
}
