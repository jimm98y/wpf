namespace WpfWebGpu;

internal static class Program
{
    private const string Usage = """
        wpfrun - run an unmodified .NET 10 WPF/WinForms app on the WPF-on-WebGPU stack

        usage:
          wpfrun run   [-v] [--offline] [--isolate] [--crash-reports] [--title-prefix <text>] [--screenshots <dir> [--every <s>]]
                       <App.dll|App.exe|folder> [-- app args]
          wpfrun compare [--isolate] [--screenshot <dir>] [--seconds <n>] [--tolerance <n>]
                       [--size <w>x<h>] [--title-prefix <text>] [--close] <App.dll|App.exe|folder> [-- app args]
                                                    Windows: the stock .exe and the WebGPU run side by side
          wpfrun prepare <App.dll|App.exe|folder>   write the generated files, print the command, do not run
          wpfrun audit [--out <file.md>] <App.dll|App.exe|folder>
                                                    what the app needs that the stack on this OS lacks
          wpfrun reg import <file.reg>              macOS: load a regedit export into the registry apps see here
          wpfrun reg export <key> <file.reg>        macOS: write a key of that registry as a regedit file
          wpfrun clean [--all]                      delete generated files (--all: also logs and shim state)
          wpfrun where                              print every location wpfrun uses

        The app's folder is only ever read. Its .exe keeps starting it on stock WPF.
        On macOS an app that aborts exits with status 134 and a note in its log instead of leaving a
        crash report and a "quit unexpectedly" dialog; --crash-reports keeps the system's behaviour.
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
            Locations.AdoptEarlierName();
            return args[0] switch
            {
                "run" => Run(args[1..], execute: true),
                "prepare" => Run(args[1..], execute: false),
                "audit" => Audit(args[1..]),
                "compare" => CompareCommand(args[1..]),
                "clean" => Clean(args[1..]),
                "reg" => RegFile.Run(args[1..]),
                "where" => Where(),
                _ => throw new UserError($"unknown command '{args[0]}'.\n\n{Usage}"),
            };
        }
        catch (UserError e)
        {
            Console.Error.WriteLine($"wpfrun: {e.Message}");
            return 2;
        }
    }

    private static int Run(string[] args, bool execute)
    {
        bool verbose = false, offline = false, isolate = false, crashReports = false;
        string? titlePrefix = null, screenshots = null;
        int every = 5;
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
                case "--isolate": isolate = true; break;
                case "--crash-reports": crashReports = true; break;
                case "--screenshots": screenshots = args[++i]; break;
                case "--every": every = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
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
                ?? throw new UserError("the overlay is missing. Build it with tools/WpfWebGpu/build.sh, or point WPFRUN_OVERLAY at a published WpfWebGpu.Payload."),
            Verbose = verbose || !execute,
            TitlePrefix = titlePrefix,
            Offline = offline,
            ScreenshotDirectory = screenshots,
            Isolate = isolate ? "-wpfrun" : null,
            CrashReports = crashReports,
            ScreenshotInterval = every,
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

    private static int CompareCommand(string[] args)
    {
        string? target = null, screenshots = null, prefix = null;
        int seconds = 5, tolerance = 0, width = 0, height = 0;
        bool close = false, isolate = false;
        var appArgs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--") { appArgs.AddRange(args[(i + 1)..]); break; }
            switch (a)
            {
                case "--screenshot": screenshots = args[++i]; break;
                case "--seconds": seconds = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--close": close = true; break;
                case "--isolate": isolate = true; break;
                case "--tolerance": tolerance = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--title-prefix": prefix = args[++i]; break;
                case "--size":
                {
                    string[] wh = args[++i].Split('x');
                    width = int.Parse(wh[0], System.Globalization.CultureInfo.InvariantCulture);
                    height = int.Parse(wh[1], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                }
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
            OverlayDir = Locations.FindOverlay() ?? throw new UserError("the overlay is missing; build it with tools/WpfWebGpu/build.cmd."),
            // No prefix unless asked: the two captions are part of what is compared.
            TitlePrefix = prefix,
            Isolate = isolate ? "-wpfrun" : null,
        };
        return Compare.Run(runner, appArgs, screenshots, seconds, close, new Compare.Options(tolerance, width, height));
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
            OverlayDir = Locations.FindOverlay() ?? throw new UserError("the overlay is missing; build it with tools/WpfWebGpu/build.sh."),
            Offline = offline,
        };
        var prepared = runner.Prepare(Array.Empty<string>());
        var set = WpfWebGpu.Audit.LoadSet.Build(prepared.MergedDeps, prepared.ProbeRoots, HostInfo.Rid);
        using var resolver = new WpfWebGpu.Audit.Resolver(set);
        var report = new WpfWebGpu.Audit.Auditor(resolver, runner.App.Directory, WpfWebGpu.Audit.StockSet.Build(runner.App, set)).Run();
        report.Notes.AddRange(prepared.Notes);
        // Off Windows, mark each Windows P/Invoke by whether the stack implements the function (the P/Invoke
        // rewriter calls it; ✗: the call returns the function's failure value).
        Func<string, string, bool>? shimHas = null;
        if (!HostInfo.IsWindows)
        {
            var catalog = WpfWebGpu.ComRewrite.Win32Catalog.For(new[] { runner.OverlayDir });
            shimHas = (module, entry) => !catalog.IsWindowsModule(WpfWebGpu.ComRewrite.Win32Catalog.Normalize(module))
                || catalog.OffWindows(entry) != null || catalog.OffWindows(entry + "W") != null || catalog.OffWindows(entry + "A") != null;
        }
        string md = WpfWebGpu.Audit.Auditor.ToMarkdown(report, runner.App.Name, HostInfo.Rid, shimHas);
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
