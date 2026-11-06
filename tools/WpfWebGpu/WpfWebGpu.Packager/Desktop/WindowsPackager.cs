// The whole installation as a Windows installer: Setup.exe (setup-win-<arch>.pack, the WpfWebGpu.Setup program)
// with the installation appended as a zip (WpfWebGpu.Setup/Payload.cs), written in managed code on any machine.
// Setup installs it into Program Files and ProgramData, imports its registry, registers its services with the
// service control manager and puts its window apps on the Start menu; every program runs on the stack's runtime
// (desktop-win-<arch>.pack), as on macOS and Linux.

using System.IO.Compression;
using WpfWebGpu.Setup;

namespace WpfWebGpu.Packager.Desktop;

public static class WindowsPackager
{
    public static string Build(PackageManifest manifest, StagedState staged, PackFile runtimePack, PackFile setupPack, string outDir, Action<string> log)
    {
        string product = manifest.Product.Name;
        string arch = runtimePack["rid"].Split('-')[1];
        var excludes = new GlobSet(manifest.Exclude);
        var info = new SetupInfo
        {
            Product = product,
            Version = manifest.Product.Version,
            Publisher = manifest.Product.Publisher,
            Id = manifest.Product.IdPrefix + "." + DesktopCommon.Segment(product),
            Apps = manifest.Apps.Where(a => a.Kind == AppKind.Gui && a.Shortcut)
                .Select(a => new SetupApp { Name = DesktopCommon.AppName(a), Exe = a.Exe }).ToList(),
            Services = manifest.Services.Select(s => new SetupService
            {
                Name = s.Name, DisplayName = s.DisplayName, Exe = s.Exe, Args = s.Args.ToList(),
                Start = s.Start.ToString(), DependsOn = s.DependsOn.ToList(), Environment = new Dictionary<string, string>(s.Environment),
            }).ToList(),
        };

        Directory.CreateDirectory(outDir);
        string setup = Path.Combine(outDir, $"{DesktopCommon.FileName(product)}-{manifest.Product.Version}-windows-{arch}-Setup.exe");
        log($"Windows ({arch}): {setup}");
        using (var output = File.Create(setup))
        {
            using (var stub = File.OpenRead(Path.Combine(setupPack.Directory, "Setup.exe"))) stub.CopyTo(output);
            long zipOffset = output.Position;
            using (var zip = new ZipArchive(new OffsetStream(output, zipOffset), ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var s = zip.CreateEntry("setup.json", CompressionLevel.Optimal).Open()) info.Write(s);
                int files = 0;
                foreach (string f in Directory.EnumerateFiles(manifest.Inputs.InstallDir, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(manifest.Inputs.InstallDir, f).Replace('\\', '/');
                    if (excludes.Matches(rel) || Path.GetFileName(f) == ".DS_Store") continue;
                    Add(zip, f, "app/" + rel);
                    files++;
                }
                string runtime = Path.Combine(runtimePack.Directory, "runtime");
                foreach (string f in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories))
                    Add(zip, f, "runtime/" + Path.GetRelativePath(runtime, f).Replace('\\', '/'));
                foreach (var (source, target) in staged.ProgramDataFiles) Add(zip, source, "programdata/" + target);
                foreach (string hive in Directory.EnumerateFiles(staged.RegistryDir, "*.reg")) Add(zip, hive, "registry/" + Path.GetFileName(hive));
                log($"  {files} files of the installation, {staged.ProgramDataFiles.Count} of ProgramData, {info.Services.Count} services, {info.Apps.Count} Start menu entries");
            }
            output.Write(Payload.Trailer(zipOffset));
        }
        log($"{setup} ({new FileInfo(setup).Length / (1024 * 1024)} MB)");
        return setup;
    }

    // already compressed: stored
    private static readonly HashSet<string> s_stored = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".zip", ".gz", ".br", ".mp4", ".nupkg", ".woff2" };

    private static void Add(ZipArchive zip, string source, string name)
    {
        var entry = zip.CreateEntry(name, s_stored.Contains(Path.GetExtension(source)) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
        entry.LastWriteTime = File.GetLastWriteTime(source);
        using var to = entry.Open();
        using var from = File.OpenRead(source);
        from.CopyTo(to);
    }
}
