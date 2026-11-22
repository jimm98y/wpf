// A Windows program an app starts, started on macOS as an app of its own.
//
// On Windows a program an app starts is a process of its own, through ShellExecute or CreateProcess alike: the app that
// started it may quit at once (a client starting its setup tool, then quitting; the tool starting the client again when
// it is done) and the program runs on, on the taskbar under its own name. On macOS a
// process an app starts is that app's: the Dock keeps the app "running in the background" while it lives, and clicking
// it starts the app again. So the program is started through LaunchServices instead (open -n), as an app of its own:
// the installation's own app for it where it has one (the stubs the package puts in /Applications/<Product>, whose
// folder each stub names in WPFRUN_APP_BUNDLES) -- its name and icon in the Dock, as when the user starts it -- else a
// bundle made for it under the user's caches, which runs it with the app's environment, folder and arguments. Either
// is signed ad hoc, so that the system can name it (MacPackager's postinstall signs the stubs).
//
// Every program of the installation an app starts so, console and window programs alike -- but one whose standard
// input or output the app reads or writes, which LaunchServices cannot hand back (that one stays the app's child), and
// any a service starts (no session for apps there). The Process that comes back is open's, which ends once the program
// is started, as a ShellExecute that hands the program to a running instance returns none of its own.

using System.Linq;
using System.Text;

namespace WpfWebGpu.ComInterop;

public static partial class WindowsPaths
{
    /// <summary>The start info that opens the Windows program as an app of its own (macOS), or null where it is started
    /// as a process of the app's.</summary>
    private static System.Diagnostics.ProcessStartInfo? AsMacApp(System.Diagnostics.ProcessStartInfo psi, string exe, List<string> appArgs)
    {
        if (!OperatingSystem.IsMacOS() || psi.RedirectStandardInput || psi.RedirectStandardOutput || psi.RedirectStandardError
            || Environment.GetEnvironmentVariable("WPFWEBGPU_SERVICE_ACCOUNT") != null || geteuid() == 0)
            return null;
        try
        {
            string? stub = InstallationApp(exe);
            var open = new System.Diagnostics.ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            open.ArgumentList.Add("-n");
            if (stub != null)
            {
                // the program's arguments, as the app gave them (its own folder and the installation's settings are the stub's)
                open.ArgumentList.Add(stub);
                if (appArgs.Count > 0) { open.ArgumentList.Add("--args"); foreach (string a in appArgs) open.ArgumentList.Add(a); }
            }
            else
            {
                open.ArgumentList.Add(MadeApp(exe));
                open.ArgumentList.Add("--args");
                open.ArgumentList.Add(LaunchFile(psi));
            }
            if (s_traceProcess) Console.Error.WriteLine($"[process] '{exe}' opened as an app of its own: {(stub ?? "a bundle made for it")}");
            return open;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (s_traceProcess) Console.Error.WriteLine($"[process] '{exe}' not opened as an app ({e.Message}): started by the app");
            return null;
        }
    }

    // the installation's app that runs this program: a stub in the folder the stubs name, whose script runs it
    private static string? InstallationApp(string exe)
    {
        string? folder = Environment.GetEnvironmentVariable("WPFRUN_APP_BUNDLES");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return null;
        string quoted = "\"" + Path.GetFullPath(exe) + "\"";
        foreach (string app in Directory.EnumerateDirectories(folder, "*.app"))
        {
            string macos = Path.Combine(app, "Contents", "MacOS");
            if (!Directory.Exists(macos)) continue;
            foreach (string script in Directory.EnumerateFiles(macos))
            {
                var info = new FileInfo(script);
                if (info.Length > 16384) continue;   // a program, not a stub's script
                string text;
                try { text = File.ReadAllText(script); } catch (IOException) { continue; }
                if (text.StartsWith("#!", StringComparison.Ordinal) && text.Contains(quoted, StringComparison.Ordinal)) return app;
            }
        }
        return null;
    }

    // a bundle made for the program (once), under the user's caches: it runs the launch file it is given, signed ad hoc
    private static string MadeApp(string exe)
    {
        string name = Path.GetFileNameWithoutExtension(exe);
        var clean = new StringBuilder();
        foreach (char c in name) clean.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ' ? c : '_');
        if (clean.Length == 0) clean.Append("Program");
        name = clean.ToString();
        string bundle = Path.Combine(AppsRoot(), name + ".app");
        string executable = Path.Combine(bundle, "Contents", "MacOS", "run");
        string plist = Path.Combine(bundle, "Contents", "Info.plist");
        if (File.Exists(executable) && File.Exists(plist)) return bundle;
        Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
        File.WriteAllText(executable, "#!/bin/sh\n# runs a Windows program another started, as an app of its own: the launch file it is given\n. \"$1\"\n");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        string id = "com.wpfwebgpu.app." + new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' ? c : '-').ToArray());
        File.WriteAllText(plist, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"
            + "<plist version=\"1.0\"><dict>"
            + "<key>CFBundleExecutable</key><string>run</string>"
            + $"<key>CFBundleIdentifier</key><string>{System.Security.SecurityElement.Escape(id)}</string>"
            + $"<key>CFBundleName</key><string>{System.Security.SecurityElement.Escape(name)}</string>"
            + "<key>CFBundlePackageType</key><string>APPL</string>"
            + "<key>NSHighResolutionCapable</key><true/>"
            + "</dict></plist>\n");
        using (var sign = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/codesign")
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true,
            ArgumentList = { "--force", "--sign", "-", bundle },
        }))
        {
            sign?.WaitForExit();
        }
        return bundle;
    }

    // what the made bundle runs, this once: the start's environment and folder, then its command; it removes itself
    private static string LaunchFile(System.Diagnostics.ProcessStartInfo psi)
    {
        string dir = Path.Combine(AppsRoot(), "launches");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".sh");
        var sb = new StringBuilder();
        sb.Append("rm -f ").Append(ShellQuote(file)).Append('\n');
        foreach (var e in psi.Environment)
        {
            if (e.Key.Length == 0 || e.Key is "_" or "PWD" or "OLDPWD" or "SHLVL" || char.IsAsciiDigit(e.Key[0])
                || !e.Key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                continue;
            sb.Append("export ").Append(e.Key).Append('=').Append(ShellQuote(e.Value ?? "")).Append('\n');
        }
        if (!string.IsNullOrEmpty(psi.WorkingDirectory)) sb.Append("cd ").Append(ShellQuote(psi.WorkingDirectory)).Append(" 2>/dev/null\n");
        var argv = psi.ArgumentList.Count > 0 ? psi.ArgumentList.ToList() : SplitArguments(psi.Arguments);
        sb.Append("exec ").Append(ShellQuote(psi.FileName));
        foreach (string a in argv) sb.Append(' ').Append(ShellQuote(a));
        sb.Append('\n');
        File.WriteAllText(file, sb.ToString());
        return file;
    }

    private static string AppsRoot() => Path.Combine(
        Environment.GetEnvironmentVariable("WPFRUN_CACHE") is { Length: > 0 } cache ? cache
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "wpfrun"),
        "started-apps");

    private static string ShellQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>A command line's arguments as a Windows program reads them (CommandLineToArgvW's rules): white space
    /// between them, quotes around one with spaces, a backslash itself unless before a quote.</summary>
    internal static List<string> SplitArguments(string? line)
    {
        var args = new List<string>();
        if (string.IsNullOrEmpty(line)) return args;
        var current = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\')
            {
                int slashes = 0;
                while (i < line.Length && line[i] == '\\') { slashes++; i++; }
                if (i < line.Length && line[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1) current.Append('"');   // an escaped quote
                    else { i--; }                                // the quote read next, as one
                }
                else { current.Append('\\', slashes); i--; }
                any = true;
                continue;
            }
            if (c == '"')
            {
                // "" inside quotes: a quote
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
}
