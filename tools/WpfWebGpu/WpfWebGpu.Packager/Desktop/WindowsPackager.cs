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
    /// <param name="sqlPack">the SQL Server the installation brings (sqlserver-win-x64.pack; on Arm too, emulated), or
    /// none: Setup offers it, chosen by default</param>
    /// <param name="otherRuntime">the runtime pack of another processor its programs run as (Windows on Arm emulates
    /// x64 and x86, Windows x64 x86), or null where the packs folder has none</param>
    /// <summary>What a Windows installer of the installation installs, Setup.exe's and the MSI's alike.</summary>
    internal sealed record Plan(SetupInfo Info, string Arch, Processor Machine, Dictionary<Processor, (string Folder, PackFile Pack)> Runtimes,
        Func<AppEntry, List<Processor>> On);

    internal static Plan PlanOf(PackageManifest manifest, PackFile runtimePack, Action<string> log, PackFile? sqlPack, Func<Processor, PackFile?>? otherRuntime)
    {
        string product = manifest.Product.Name;
        string arch = runtimePack["rid"].Split('-')[1];

        // the processors the programs run as here, and the runtime each needs: runtime\ this machine's own, the others
        // runtime-<processor>\ where the packs folder has them (a program whose default is not falls back to the next)
        Processor machine = Processors.FromRid(runtimePack["rid"]) ?? Processor.X64;
        var runtimes = new Dictionary<Processor, (string Folder, PackFile Pack)> { [machine] = ("runtime", runtimePack) };
        foreach (Processor p in manifest.Apps.SelectMany(a => Processors.On(a, machine)).Distinct().Where(p => p != machine))
        {
            if (otherRuntime?.Invoke(p) is { } pack) runtimes[p] = ("runtime-" + Processors.Name(p), pack);
            else log($"  no desktop-{Processors.Rid(p)}.pack: its programs for {Processors.Name(p)} run as {Processors.Name(machine)} instead");
        }
        List<Processor> On(AppEntry a) => Processors.On(a, machine).Where(runtimes.ContainsKey).DefaultIfEmpty(machine).ToList();
        var info = new SetupInfo
        {
            Product = product,
            Version = manifest.Product.Version,
            Publisher = manifest.Product.Publisher,
            Id = manifest.Product.IdPrefix + "." + DesktopCommon.Segment(product),
            Apps = manifest.Apps.Where(a => a.Kind == AppKind.Gui && a.Shortcut)
                .Select(a => new SetupApp { Name = DesktopCommon.AppName(a), Exe = a.Exe, Processors = On(a).Skip(1).Select(Processors.Name).ToList() }).ToList(),
            Runtimes = runtimes.ToDictionary(r => Processors.Name(r.Key), r => r.Value.Folder),
            Programs = manifest.Apps.ToDictionary(a => a.Exe, a => Processors.Name(On(a)[0]), StringComparer.OrdinalIgnoreCase),
            Services = manifest.Services.Select(s => new SetupService
            {
                Name = s.Name, DisplayName = s.DisplayName, Exe = s.Exe, Args = s.Args.ToList(),
                Start = s.Start.ToString(), DependsOn = s.DependsOn.ToList(), Environment = new Dictionary<string, string>(s.Environment),
                Account = s.Account.ToString(),
                StartName = s.StartName, Description = s.Description, DelayedAutoStart = s.DelayedAutoStart, Security = s.Security,
            }).ToList(),
            SqlServer = sqlPack == null ? null : new SetupSqlServer
            {
                Engine = sqlPack["engine"], Media = "SQLEXPR_x64_ENU.exe", Instance = manifest.SqlServer.Instance,
            },
        };
        return new Plan(info, arch, machine, runtimes, On);
    }

    /// <summary>The files of the installation folder: app\ (the Program Files tree), the runtimes (their programs signed
    /// where they are not), by their paths in it ('/'-separated).</summary>
    internal static IEnumerable<(string Source, string Target)> InstallationFiles(PackageManifest manifest, Plan plan,
        System.Security.Cryptography.X509Certificates.X509Certificate2? certificate, string outDir, Action<string> log)
    {
        var excludes = new GlobSet(manifest.Exclude);
        foreach (string f in Directory.EnumerateFiles(manifest.Inputs.InstallDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(manifest.Inputs.InstallDir, f).Replace('\\', '/');
            if (excludes.Matches(rel) || Path.GetFileName(f) == ".DS_Store") continue;
            yield return (f, "app/" + rel);
        }
        foreach (var (p, (folder, pack)) in plan.Runtimes)
        {
            string runtime = Path.Combine(pack.Directory, "runtime");
            foreach (string f in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories))
                yield return (Signed(f, certificate, manifest.Signing.TimestampUrl, outDir), folder + "/" + Path.GetRelativePath(runtime, f).Replace('\\', '/'));
            if (p != plan.Machine) log($"  the {Processors.Name(p)} runtime ({folder}\\): {string.Join(", ", manifest.Apps.Where(a => plan.On(a).Contains(p)).Select(a => DesktopCommon.AppName(a) + (plan.On(a)[0] == p ? "" : " (to fall back on)")))}");
        }
    }

    public static string Build(PackageManifest manifest, StagedState staged, PackFile runtimePack, PackFile setupPack, string outDir, Action<string> log,
        PackFile? sqlPack = null, Func<Processor, PackFile?>? otherRuntime = null)
    {
        var plan = PlanOf(manifest, runtimePack, log, sqlPack, otherRuntime);
        var info = plan.Info;
        string product = manifest.Product.Name;
        string arch = plan.Arch;
        Directory.CreateDirectory(outDir);
        string setup = Path.Combine(outDir, $"{DesktopCommon.FileName(product)}-{manifest.Product.Version}-windows-{arch}-Setup.exe");
        log($"Windows ({arch}): {setup}");
        using var certificate = Authenticode.Certificate(manifest);
        // the zip is written beside Setup.exe first, then appended to it when it is small enough (Payload.MaxEmbedded)
        string dataName = Path.GetFileNameWithoutExtension(setup) + ".data";
        string data = Path.Combine(outDir, dataName);
        using (var output = File.Create(data))
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var s = zip.CreateEntry("setup.json", CompressionLevel.Optimal).Open()) info.Write(s);
                // the license agreement, which Setup shows and asks to be accepted before installing
                if (manifest.Product.License.Length > 0)
                    using (var w = new StreamWriter(zip.CreateEntry("license.txt", CompressionLevel.Optimal).Open(), new System.Text.UTF8Encoding(false)))
                        w.Write(License.Text(manifest.Product.License));
                int files = 0;
                foreach (var (source, target) in InstallationFiles(manifest, plan, certificate, outDir, log))
                {
                    Add(zip, source, target);
                    if (target.StartsWith("app/", StringComparison.Ordinal)) files++;
                }
                foreach (var (source, target) in staged.ProgramDataFiles) Add(zip, source, "programdata/" + target);
                foreach (string hive in Directory.EnumerateFiles(staged.RegistryDir, "*.reg")) Add(zip, hive, "registry/" + Path.GetFileName(hive));
                if (sqlPack != null)
                {
                    Add(zip, Path.Combine(sqlPack.Directory, "SQLEXPR_x64_ENU.exe"), "sqlserver/SQLEXPR_x64_ENU.exe", stored: true);
                    log($"  {sqlPack["engine"]}, offered (chosen) by Setup");
                }
                log($"  {files} files of the installation, {staged.ProgramDataFiles.Count} of ProgramData, {info.Services.Count} services, {info.Apps.Count} Start menu entries");
            }
        }
        long dataLength = new FileInfo(data).Length;
        using (var output = File.Create(setup))
        {
            using (var stub = File.OpenRead(Path.Combine(setupPack.Directory, "Setup.exe"))) stub.CopyTo(output);
            if (dataLength <= Payload.MaxEmbedded)
            {
                long zipOffset = output.Position;
                using (var zip = File.OpenRead(data)) zip.CopyTo(output);
                output.Write(Payload.Trailer(zipOffset));
            }
            else
            {
                byte[] hash;
                using (var zip = File.OpenRead(data)) hash = System.Security.Cryptography.SHA256.HashData(zip);
                output.Write(Payload.ExternalTrailer(dataName, dataLength, hash));
            }
        }
        if (dataLength <= Payload.MaxEmbedded) File.Delete(data);
        else log($"  the installation ({dataLength >> 20} MB) is {dataName}, beside Setup.exe: too large to carry inside a program; the two go together");
        if (certificate != null)
        {
            Authenticode.Sign(setup, certificate, manifest.Signing.TimestampUrl);
            log($"  signed: {certificate.Subject}{(manifest.Signing.TimestampUrl.Length > 0 ? ", time-stamped" : "")}");
        }
        log($"{setup} ({new FileInfo(setup).Length / (1024 * 1024)} MB)");
        return setup;
    }

    // a program of the runtime that is not signed, or no longer is (wpfrunw.exe: .NET's apphost written for wpfrun),
    // signed with the installation's certificate -- a copy, in the work folder. Programs only: the libraries are
    // others' as they were published (an unsigned one is not the installation's to sign).
    private static string Signed(string file, System.Security.Cryptography.X509Certificates.X509Certificate2? certificate, string timestampUrl, string outDir)
    {
        if (certificate == null || !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return file;
        if (Authenticode.HasValidHash(file) || !IsPe(file)) return file;
        string folder = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(file)))[..16];
        string copy = Path.Combine(outDir, ".work", "signed", folder, Path.GetFileName(file));
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(file, copy, overwrite: true);
        Authenticode.Sign(copy, certificate, timestampUrl);
        return copy;
    }

    // a native or managed PE file (an assembly's reference-only stub or a resource-only file is one too)
    private static bool IsPe(string file)
    {
        using var s = File.OpenRead(file);
        var head = new byte[2];
        return s.Read(head) == 2 && head[0] == 'M' && head[1] == 'Z';
    }

    // already compressed: stored
    private static readonly HashSet<string> s_stored = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".zip", ".gz", ".br", ".mp4", ".nupkg", ".woff2" };

    private static void Add(ZipArchive zip, string source, string name, bool stored = false)
    {
        var entry = zip.CreateEntry(name, stored || s_stored.Contains(Path.GetExtension(source)) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
        entry.LastWriteTime = File.GetLastWriteTime(source);
        using var to = entry.Open();
        using var from = File.OpenRead(source);
        from.CopyTo(to);
    }
}
