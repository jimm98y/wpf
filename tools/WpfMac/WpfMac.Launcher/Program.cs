namespace WpfMac;

internal static class Program
{
    private const string Usage = """
        wpfmac - run an unmodified .NET 10 WPF/WinForms app on the WPF-on-WebGPU stack

        usage:
          wpfmac run   [-v] [--offline] [--title-prefix <text>] <App.dll|App.exe|folder> [-- app args]
          wpfmac prepare <App.dll|App.exe|folder>   write the generated files, print the command, do not run
          wpfmac audit [--out <file.md>] <App.dll|App.exe|folder>
                                                    what the app needs that the stack on this OS lacks
          wpfmac clean [--all]                      delete generated files (--all: also logs and shim state)
          wpfmac where                              print every location wpfmac uses

        The app's folder is only ever read. Its .exe keeps starting it on stock WPF.
        """;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.WriteLine(Usage);
                return args.Length == 0 ? 1 : 0;
            }
            return args[0] switch
            {
                "run" => Run(args[1..], execute: true),
                "prepare" => Run(args[1..], execute: false),
                "audit" => Audit(args[1..]),
                "clean" => Clean(args[1..]),
                "where" => Where(),
                _ => throw new UserError($"unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (UserError e)
        {
            Console.Error.WriteLine($"wpfmac: {e.Message}");
            return 2;
        }
    }

    private static int Run(string[] args, bool execute)
    {
        bool verbose = false, offline = false;
        string? titlePrefix = null;
        string? target = null;
        var appArgs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--") { appArgs.AddRange(args[(i + 1)..]); break; }
            if (target != null) { appArgs.Add(a); continue; }
            switch (a)
            {
                case "-v" or "--verbose": verbose = true; break;
                case "--offline": offline = true; break;
                case "--title-prefix": titlePrefix = args[++i]; break;
                default:
                    if (a.StartsWith('-')) throw new UserError($"unknown option '{a}'.");
                    target = a;
                    break;
            }
        }
        if (target == null) throw new UserError("which app? Pass its .dll, .exe or folder.");

        var runner = new Runner
        {
            App = AppFiles.Resolve(target),
            OverlayDir = Locations.FindOverlay()
                ?? throw new UserError("the overlay is missing. Build it with tools/WpfMac/build.sh, or point WPFMAC_OVERLAY at a published WpfMac.Payload."),
            Verbose = verbose || !execute,
            TitlePrefix = titlePrefix,
            Offline = offline,
        };

        if (execute) return runner.Run(appArgs);

        var p = runner.Prepare(appArgs);
        foreach (string n in p.Notes) Console.WriteLine(n);
        Console.WriteLine($"replaced: {string.Join(", ", p.Replaced)}");
        Console.WriteLine($"runtimeconfig: {p.RuntimeConfig}");
        Console.WriteLine($"deps: {p.Deps}");
        Console.WriteLine($"command: {p.StartInfo.FileName} {string.Join(' ', p.StartInfo.ArgumentList)}");
        return 0;
    }

    private static int Audit(string[] args)
    {
        string? output = null, target = null;
        bool offline = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--out": output = args[++i]; break;
                case "--offline": offline = true; break;
                default: target = args[i]; break;
            }
        }
        if (target == null) throw new UserError("which app? Pass its .dll, .exe or folder.");

        var runner = new Runner
        {
            App = AppFiles.Resolve(target),
            OverlayDir = Locations.FindOverlay() ?? throw new UserError("the overlay is missing; build it with tools/WpfMac/build.sh."),
            Offline = offline,
        };
        var prepared = runner.Prepare(Array.Empty<string>());
        var set = WpfMac.Audit.LoadSet.Build(prepared.MergedDeps, prepared.ProbeRoots, HostInfo.Rid);
        using var resolver = new WpfMac.Audit.Resolver(set);
        var report = new WpfMac.Audit.Auditor(resolver, runner.App.Directory).Run();
        report.Notes.AddRange(prepared.Notes);
        // On macOS, mark each Windows P/Invoke by whether libwin32shim exports it.
        Func<string, string, bool>? shimHas = null;
        string manifest = Path.Combine(runner.OverlayDir, "win32shim_exports.txt");
        if (File.Exists(manifest))
        {
            var exports = new HashSet<string>(File.ReadLines(manifest).Select(l => l[(l.IndexOf('\t') + 1)..]), StringComparer.Ordinal);
            var modules = new HashSet<string>(File.ReadLines(manifest).Select(l => l[..l.IndexOf('\t')]), StringComparer.OrdinalIgnoreCase);
            shimHas = (module, entry) => !modules.Contains(module) || exports.Contains(entry);
        }
        string md = WpfMac.Audit.Auditor.ToMarkdown(report, runner.App.Name, HostInfo.Rid, shimHas);
        if (output != null)
        {
            File.WriteAllText(output, md);
            Console.WriteLine($"wrote {output}");
        }
        else
        {
            Console.Write(md);
        }
        return 0;
    }

    private static int Clean(string[] args)
    {
        bool all = args.Contains("--all");
        var targets = all ? Locations.All : new[] { Locations.CacheRoot };
        foreach (string dir in targets)
        {
            if (!Directory.Exists(dir)) continue;
            Directory.Delete(dir, recursive: true);
            Console.WriteLine($"removed {dir}");
        }
        return 0;
    }

    private static int Where()
    {
        Console.WriteLine($"overlay  {Locations.FindOverlay() ?? "(not found)"}");
        Console.WriteLine($"cache    {Locations.CacheRoot}");
        Console.WriteLine($"logs     {Locations.LogRoot}");
        Console.WriteLine($"state    {Locations.StateRoot}");
        Console.WriteLine($"dotnet   {HostInfo.DotnetMuxer()}");
        Console.WriteLine($"rid      {HostInfo.Rid}");
        return 0;
    }
}
