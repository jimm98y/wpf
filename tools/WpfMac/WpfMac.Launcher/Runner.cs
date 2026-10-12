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

    public sealed record Prepared(string RuntimeConfig, string Deps, string LogPath, IReadOnlyList<string> Notes, IReadOnlyCollection<string> Replaced)
    {
        public required ProcessStartInfo StartInfo { get; init; }
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

        var props = new Dictionary<string, string>
        {
            ["STARTUP_HOOKS"] = Path.Combine(OverlayDir, "WpfMac.Bootstrap.dll"),
            ["WpfMac.AppDirectory"] = App.Directory + Path.DirectorySeparatorChar,
            ["WpfMac.LogPath"] = logPath,
            // The WebGPU compositor; the SDK sets the same switch on the heads where it is not already
            // the default.
            ["Switch.System.Windows.Media.UseWebGpuComposition"] = "true",
        };
        string fonts = Path.Combine(OverlayDir, "fonts");
        if (Directory.Exists(fonts)) props["WpfWebGpu.FontDirectory"] = fonts;
        if (!string.IsNullOrEmpty(TitlePrefix)) props["WpfMac.TitlePrefix"] = TitlePrefix;

        JsonObject rc = RuntimeConfigRewriter.Rewrite(DepsMerger.ReadJson(App.RuntimeConfig), payloadRc, props);

        string rcPath = Path.Combine(cacheDir, App.Name + ".runtimeconfig.json");
        string depsPath = Path.Combine(cacheDir, App.Name + ".deps.json");
        var json = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(rcPath, rc.ToJsonString(json));
        File.WriteAllText(depsPath, deps.ToJsonString(json));
        File.WriteAllLines(Path.Combine(cacheDir, "merge-report.txt"),
            new[] { $"app      {App.Dll}", $"overlay  {OverlayDir}", $"rid      {rid}", $"replaced {string.Join(", ", merger.Replaced)}" }
                .Concat(merger.Notes));

        var psi = new ProcessStartInfo(HostInfo.DotnetMuxer())
        {
            UseShellExecute = false,
            WorkingDirectory = App.Directory,      // as Explorer starts an .exe: in its own folder
        };
        foreach (string a in new[]
        {
            "exec",
            "--runtimeconfig", rcPath,
            "--depsfile", depsPath,
            "--additionalprobingpath", Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(OverlayDir))!,
            "--additionalprobingpath", Path.GetDirectoryName(App.Directory)!,
            "--additionalprobingpath", fetcher.Root,
            App.Dll,
        })
        {
            psi.ArgumentList.Add(a);
        }
        foreach (string a in appArgs) psi.ArgumentList.Add(a);

        if (!HostInfo.IsWindows)
        {
            // The registry shim's store. Shared by every app run through wpfmac, like the real registry,
            // and kept with the rest of wpfmac's state so that removing wpfmac removes it.
            psi.Environment["WPFWEBGPU_REGISTRY_DIR"] = Path.Combine(Locations.StateRoot, "registry");
        }
        // The app's own children must not inherit a startup hook a parent shell may have set.
        psi.Environment.Remove("DOTNET_STARTUP_HOOKS");

        return new Prepared(rcPath, depsPath, logPath, merger.Notes, merger.Replaced) { StartInfo = psi };
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
