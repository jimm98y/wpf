// wpfrun startup: what Windows starts when a user logs on -- each shortcut in the Startup folders, the machine's
// (All Users, in the state's ProgramData) and the user's own, its target started as the shortcut says: a program of
// the installation through wpfrun, as its Start menu entry starts it; anything else opened as the system opens it.
// The installers run it at each log-on (a LaunchAgent on macOS, an XDG autostart entry on Linux), so a program an
// app put in a Startup folder starts at log-on here as it does on Windows.

using System.Diagnostics;
using WpfWebGpu.ComInterop;

namespace WpfWebGpu;

internal static class Startup
{
    /// <summary>The Startup folders, as the stack shows them to apps (Detours, WindowsPaths).</summary>
    public static IEnumerable<string> Folders()
    {
        yield return Path.Combine(Locations.StateRoot, "ProgramData", "Start Menu", "Programs", "Startup");
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (roaming.Length > 0) yield return Path.Combine(roaming, "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
    }

    public static int Run(string[] args)
    {
        bool dryRun = args.Contains("--list");
        string self = Environment.ProcessPath ?? "dotnet";
        string launcher = typeof(Startup).Assembly.Location;
        foreach (string folder in Folders().Where(Directory.Exists))
        {
            foreach (string file in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                ProcessStartInfo? start;
                try { start = StartInfo(file, self, launcher); }
                catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"wpfrun startup: {file}: {e.Message}");
                    continue;
                }
                if (start == null) continue;
                Console.WriteLine($"{Path.GetFileName(file)}: {start.FileName} {string.Join(' ', start.ArgumentList)}");
                if (dryRun) continue;
                try { Process.Start(start)?.Dispose(); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Console.Error.WriteLine($"wpfrun startup: {file}: {e.Message}"); }
            }
        }
        return 0;
    }

    // a .lnk's target (an .exe of the installation through wpfrun run), a .url's address, or the file itself opened
    private static ProcessStartInfo? StartInfo(string file, string self, string launcher)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        if (ext == ".lnk")
        {
            var link = ShellLinkFile.Read(file);
            if (link.TargetPath.Length == 0) return null;
            string target = WindowsPaths.Disk(link.TargetPath)!;
            string directory = link.WorkingDirectory.Length > 0 ? WindowsPaths.Disk(link.WorkingDirectory)! : Path.GetDirectoryName(target) ?? "";
            if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var run = new ProcessStartInfo(self) { UseShellExecute = false, WorkingDirectory = Directory.Exists(directory) ? directory : "" };
                if (!Path.GetFileNameWithoutExtension(self).Equals("wpfrun", StringComparison.OrdinalIgnoreCase)) run.ArgumentList.Add(launcher);
                run.ArgumentList.Add("run");
                run.ArgumentList.Add(target);
                foreach (string a in SplitCommandLine(link.Arguments)) run.ArgumentList.Add(a);
                return run;
            }
            return Open(target);
        }
        if (ext == ".url")
        {
            string? url = File.ReadLines(file).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..];
            return url is { Length: > 0 } ? Open(url) : null;
        }
        return Open(file);
    }

    /// <summary>A command line's arguments as CommandLineToArgvW reads them: whitespace apart, quotes grouping,
    /// 2n backslashes before a quote n and the quote a delimiter, 2n+1 of them n and a literal quote.</summary>
    internal static List<string> SplitCommandLine(string line)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\')
            {
                int n = 0;
                while (i < line.Length && line[i] == '\\') { n++; i++; }
                if (i < line.Length && line[i] == '"')
                {
                    current.Append('\\', n / 2);
                    if (n % 2 == 1) current.Append('"'); else quoted = !quoted;
                }
                else { current.Append('\\', n); i--; }
                any = true;
                continue;
            }
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
                any = true;
                continue;
            }
            if (!quoted && (c == ' ' || c == '\t'))
            {
                if (any) { args.Add(current.ToString()); current.Clear(); any = false; }
                continue;
            }
            current.Append(c);
            any = true;
        }
        if (any) args.Add(current.ToString());
        return args;
    }

    private static ProcessStartInfo Open(string what)
    {
        var open = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "/usr/bin/open" : OperatingSystem.IsWindows() ? what : "xdg-open") { UseShellExecute = OperatingSystem.IsWindows() };
        if (!OperatingSystem.IsWindows()) open.ArgumentList.Add(what);
        return open;
    }
}
