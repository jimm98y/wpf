// The Packager's command line, shared by `wpfrun package` and the Packager app's unattended mode (`Packager build ...`,
// `wpfrun package build ...`): the usage names the commands alone, as both take them.

namespace WpfWebGpu.Packager;

public static class PackagerCli
{
    public const string Usage = """
        scan <install folder> [--programdata <folder>]... [--registry <file.reg>]... [--iis <applicationHost.config>] [--out <x.wpfpkg.json>]
                                        a first manifest: the installation's apps, services and registry paths
        stage <x.wpfpkg.json> [--work <folder>] [--copy]
                                        its registry as hive files, its ProgramData listed (--copy: copied)
        build <x.wpfpkg.json> [--target <android|quest|ios|visionos|macos|linux|windows>[,...]] [--out <folder>] [--packs <folder>] [--fast]
                                        the packages: one app per "mobile" app for a phone or headset; the whole installation for macOS, Linux and Windows
                                        (--fast: nothing compressed, for testing)
        """;

    public static int Run(string[] args, TextWriter? output = null)
    {
        var o = output ?? Console.Out;
        if (args.Length == 0) throw new PackagerError("usage:\n" + Usage);
        return args[0] switch
        {
            "scan" => ScanCommand(args[1..], o),
            "stage" => StageCommand(args[1..], o),
            "build" => BuildCommand(args[1..], o),
            _ => throw new PackagerError($"unknown package command '{args[0]}'.\nusage:\n{Usage}"),
        };
    }

    private static int ScanCommand(string[] args, TextWriter o)
    {
        string? install = null, outPath = null, iis = null;
        var programData = new List<string>();
        var registry = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--programdata": programData.Add(Value(args, ref i)); break;
                case "--registry": registry.Add(Value(args, ref i)); break;
                case "--iis": iis = Value(args, ref i); break;
                case "--out" or "-o": outPath = Value(args, ref i); break;
                default:
                    if (args[i].StartsWith('-') || install != null) throw new PackagerError($"unexpected '{args[i]}'.\nusage:\n{Usage}");
                    install = args[i];
                    break;
            }
        }
        if (install == null) throw new PackagerError("usage:\n" + Usage);

        var manifest = Scanner.Scan(install, programData, registry, line => o.WriteLine("  " + line));
        if (iis != null)
        {
            if (!File.Exists(iis)) throw new PackagerError($"no such file: {iis}");
            manifest.Inputs.IisConfig = Path.GetFullPath(iis);
        }
        outPath ??= manifest.Product.Name + PackageManifest.Extension;
        manifest.Save(outPath);
        int gui = manifest.Apps.Count(a => a.Kind == AppKind.Gui), con = manifest.Apps.Count(a => a.Kind == AppKind.Console);
        o.WriteLine($"{manifest.Apps.Count} apps ({gui} window, {con} console, {manifest.Services.Count} services), {manifest.Apps.Count(a => a.Warnings.Count > 0)} with warnings");
        foreach (var m in manifest.PathMappings) o.WriteLine($"  path {m.From} -> {m.To}");
        o.WriteLine($"manifest: {Path.GetFullPath(outPath)} -- tick the apps to ship on phones and headsets (\"mobile\") and in the browser (\"web\"), and check the services");
        return 0;
    }

    private static int StageCommand(string[] args, TextWriter o)
    {
        string? manifestPath = null, work = null;
        bool copy = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--work": work = Value(args, ref i); break;
                case "--copy": copy = true; break;
                default:
                    if (args[i].StartsWith('-') || manifestPath != null) throw new PackagerError($"unexpected '{args[i]}'.\nusage:\n{Usage}");
                    manifestPath = args[i];
                    break;
            }
        }
        if (manifestPath == null) throw new PackagerError("usage:\n" + Usage);
        var manifest = PackageManifest.Load(manifestPath);
        work ??= Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, manifest.Product.Name + ".work");
        var staged = Staging.Stage(manifest, work);
        long bytes = staged.ProgramDataFiles.Sum(f => new FileInfo(f.Source).Length);
        o.WriteLine($"registry: {staged.Keys} keys, {staged.Values} values -> {staged.RegistryDir}");
        o.WriteLine($"ProgramData: {staged.ProgramDataFiles.Count} files, {bytes / (1024.0 * 1024):0.0} MB");
        foreach (var (target, why) in staged.LeftOut) o.WriteLine($"  left out {target}: {why}");
        if (staged.Icon != null) o.WriteLine($"icon: {staged.Icon}");
        if (copy)
        {
            string dest = Path.Combine(work, "state", "ProgramData");
            foreach (var (source, target) in staged.ProgramDataFiles)
            {
                string to = Path.Combine(dest, target);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(source, to, overwrite: true);
            }
            o.WriteLine($"  copied to {dest}");
        }
        return 0;
    }

    private static int BuildCommand(string[] args, TextWriter o)
    {
        string? manifestPath = null, outDir = null, packs = null;
        var targets = new List<Target>();
        bool fast = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out" or "-o": outDir = Value(args, ref i); break;
                case "--packs": packs = Value(args, ref i); break;
                case "--fast": fast = true; break;
                case "--target" or "-t":
                    foreach (string t in Value(args, ref i).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        targets.Add(Enum.TryParse<Target>(t, ignoreCase: true, out var target) ? target : throw new PackagerError($"unknown target '{t}'"));
                    break;
                default:
                    if (args[i].StartsWith('-') || manifestPath != null) throw new PackagerError($"unexpected '{args[i]}'.\nusage:\n{Usage}");
                    manifestPath = args[i];
                    break;
            }
        }
        if (manifestPath == null) throw new PackagerError("usage:\n" + Usage);
        var manifest = PackageManifest.Load(manifestPath);
        if (fast) manifest.Output.Fast = true;
        if (targets.Count == 0) targets = manifest.Output.Targets;
        if (targets.Count == 0) throw new PackagerError("which targets? --target, or output.targets in the manifest");
        outDir = Path.GetFullPath(outDir ?? (manifest.Output.Directory.Length > 0 ? manifest.Output.Directory
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!, manifest.Product.Name + ".packages")));
        Builder.Build(manifest, targets, outDir, packs ?? PackFile.DefaultFolder, line => o.WriteLine(line));
        return 0;
    }

    private static string Value(string[] args, ref int i) =>
        ++i < args.Length ? args[i] : throw new PackagerError($"{args[i - 1]} needs a value.");
}
