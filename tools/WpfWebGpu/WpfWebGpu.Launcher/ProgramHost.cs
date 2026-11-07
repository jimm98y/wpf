// A program's own executable, so that it runs under its own name -- in Activity Monitor, ps, top and launchd as
// OrderService, not as one of many "dotnet"s, as it runs as OrderService.exe on Windows. It is .NET's apphost (the
// executable the SDK puts beside every app, Microsoft.NETCore.App.Host, MIT), copied into the program's cache folder
// under the program's name and pointed at a host folder there:
//
//   <cache>/apps/<App>-<hash>/<App>                        the apphost, its app path host/WpfWebGpu.AppHost.dll
//   <cache>/apps/<App>-<hash>/host/WpfWebGpu.AppHost.dll   the stack's managed host (a copy of the overlay's)
//   <cache>/apps/<App>-<hash>/host/WpfWebGpu.AppHost.runtimeconfig.json, .deps.json   what dotnet exec was told
//
// Every assembly of the merged deps resolves through the probing paths (written into the runtime config, where dotnet
// exec took them on its command line), so the host folder needs nothing else. The runtime is found through
// DOTNET_ROOT, set to this launcher's own. On macOS the patched apphost is signed again (ad hoc), as the SDK signs it.
// The launcher then hands its process over to it (execve), so the program is the process launchd or the shell
// started, under its own name and with its own exit status.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace WpfWebGpu;

internal static partial class ProgramHost
{
    private const string AppPathPlaceholder = "c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";
    private const string HostDll = "WpfWebGpu.AppHost.dll";

    /// <summary>.NET's apphost for this machine: the one beside the launcher (a desktop pack's), else the SDK's.</summary>
    public static string? Template(string dotnetRoot)
    {
        if (OperatingSystem.IsWindows()) return null;
        string beside = Path.Combine(AppContext.BaseDirectory, "apphost");
        if (File.Exists(beside)) return beside;
        string rid = HostInfo.Rid;
        string packs = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Host." + rid);
        if (!Directory.Exists(packs)) return null;
        return Directory.EnumerateDirectories(packs)
            .OrderByDescending(d => Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : new Version())
            .Select(d => Path.Combine(d, "runtimes", rid, "native", "apphost"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>The program's executable in <paramref name="cacheDir"/>, made or brought up to date, with its host
    /// folder; null where it cannot be made (the caller runs the program through dotnet exec).</summary>
    public static string? Make(string template, string cacheDir, string appName, string overlayDir, JsonObject runtimeConfig,
        JsonObject deps, IEnumerable<string> probingPaths)
    {
        try
        {
            string hostDir = Path.Combine(cacheDir, "host");
            Directory.CreateDirectory(hostDir);
            CopyIfChanged(Path.Combine(overlayDir, HostDll), Path.Combine(hostDir, HostDll));
            CopyIfChanged(Path.Combine(overlayDir, Path.ChangeExtension(HostDll, ".pdb")), Path.Combine(hostDir, Path.ChangeExtension(HostDll, ".pdb")));
            var rc = (JsonObject)runtimeConfig.DeepClone();
            var options = rc["runtimeOptions"] as JsonObject ?? new JsonObject();
            rc["runtimeOptions"] = options;
            options["additionalProbingPaths"] = new JsonArray(probingPaths.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
            var json = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            WriteIfChanged(Path.Combine(hostDir, Path.ChangeExtension(HostDll, ".runtimeconfig.json")), rc.ToJsonString(json));
            WriteIfChanged(Path.Combine(hostDir, Path.ChangeExtension(HostDll, ".deps.json")), deps.ToJsonString(json));

            string exe = Path.Combine(cacheDir, FileName(appName));
            string stamp = exe + ".template";
            string templateId = $"{template}|{new FileInfo(template).Length}|{File.GetLastWriteTimeUtc(template).Ticks}";
            if (!File.Exists(exe) || !File.Exists(stamp) || File.ReadAllText(stamp) != templateId)
            {
                byte[] image = File.ReadAllBytes(template);
                if (!Patch(image, "host/" + HostDll)) return null;
                string temp = exe + "." + Environment.ProcessId + ".tmp";
                File.WriteAllBytes(temp, image);
                File.SetUnixFileMode(temp, (UnixFileMode)Convert.ToInt32("755", 8));
                if (OperatingSystem.IsMacOS() && !Sign(temp)) { File.Delete(temp); return null; }
                File.Move(temp, exe, overwrite: true);
                File.WriteAllText(stamp, templateId);
            }
            return exe;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // the app path the apphost runs: its placeholder (a SHA-256 the SDK writes over) replaced, as HostWriter does
    internal static bool Patch(byte[] image, string appPath)
    {
        byte[] placeholder = Encoding.UTF8.GetBytes(AppPathPlaceholder);
        int at = image.AsSpan().IndexOf(placeholder);
        if (at < 0) return false;
        byte[] value = Encoding.UTF8.GetBytes(appPath);
        if (value.Length >= 1024) return false;
        Array.Clear(image, at, 1024);
        value.CopyTo(image, at);
        return true;
    }

    private static bool Sign(string file)
    {
        var psi = new ProcessStartInfo("/usr/bin/codesign") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string a in new[] { "--force", "--sign", "-", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return false;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    private static void CopyIfChanged(string from, string to)
    {
        if (!File.Exists(from)) return;
        var a = new FileInfo(from);
        var b = new FileInfo(to);
        if (b.Exists && a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc) return;
        File.Copy(from, to, overwrite: true);
        File.SetLastWriteTimeUtc(to, a.LastWriteTimeUtc);
    }

    private static void WriteIfChanged(string path, string text)
    {
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        File.WriteAllText(path, text);
    }

    // the program's name as a file's (no path separators; Windows' .exe never part of it)
    private static string FileName(string name) =>
        string.Concat(name.Select(c => c is '/' or '\0' ? '_' : c));

    // ---- handing the process over ------------------------------------------------------------------------------

    /// <summary>Replaces this process with <paramref name="start"/>'s program (its arguments, environment and working
    /// directory); returns only where that fails.</summary>
    public static void Exec(ProcessStartInfo start)
    {
        if (OperatingSystem.IsWindows()) return;
        if (!string.IsNullOrEmpty(start.WorkingDirectory)) Directory.SetCurrentDirectory(start.WorkingDirectory);
        var argv = new List<string> { start.FileName };
        argv.AddRange(start.ArgumentList);
        var envp = start.Environment.Where(e => e.Value != null).Select(e => $"{e.Key}={e.Value}").ToList();
        Console.Out.Flush();
        Console.Error.Flush();
        IntPtr[] a = Strings(argv), e = Strings(envp);
        try { execve(start.FileName, a, e); }
        finally { Free(a); Free(e); }
    }

    private static IntPtr[] Strings(List<string> values)
    {
        var p = new IntPtr[values.Count + 1];
        for (int i = 0; i < values.Count; i++) p[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
        return p;
    }

    private static void Free(IntPtr[] p)
    {
        foreach (IntPtr s in p) if (s != IntPtr.Zero) Marshal.FreeCoTaskMem(s);
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int execve(string path, IntPtr[] argv, IntPtr[] envp);
}
