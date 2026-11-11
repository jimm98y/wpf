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
    public static string Build(PackageManifest manifest, StagedState staged, PackFile runtimePack, PackFile setupPack, string outDir, Action<string> log,
        PackFile? sqlPack = null)
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
                Account = s.Account.ToString(),
                StartName = s.StartName, Description = s.Description, DelayedAutoStart = s.DelayedAutoStart, Security = s.Security,
            }).ToList(),
            SqlServer = sqlPack == null ? null : new SetupSqlServer
            {
                Engine = sqlPack["engine"], Media = "SQLEXPR_x64_ENU.exe", Instance = manifest.SqlServer.Instance,
            },
        };

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
                    Add(zip, Signed(f, certificate, manifest.Signing.TimestampUrl, outDir), "runtime/" + Path.GetRelativePath(runtime, f).Replace('\\', '/'));
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

    // a program of the runtime that is not signed, or no longer is (dotnetw.exe: dotnet.exe made a window program),
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
