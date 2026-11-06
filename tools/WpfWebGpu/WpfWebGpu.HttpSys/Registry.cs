// The machine's registered prefixes: a folder every process shares, a file per registering server -- its process,
// the loopback port it serves its pipeline on, its prefixes -- removed when the server stops (and passed over, and
// cleared, once its process has gone).

using System.Diagnostics;
using System.Globalization;

namespace WpfWebGpu.HttpSys;

public static class HttpSysRegistry
{
    /// <summary>The folder (WPFWEBGPU_HTTPSYS_DIR, or the machine's temporary folder's).</summary>
    public static string Folder => Environment.GetEnvironmentVariable("WPFWEBGPU_HTTPSYS_DIR") is { Length: > 0 } d ? d
        : OperatingSystem.IsWindows() ? Path.Combine(Path.GetTempPath(), "wpfwebgpu-httpsys") : "/tmp/wpfwebgpu-httpsys";

    /// <summary>The folder, made writable by every user of the machine (sticky, as /tmp is).</summary>
    internal static string EnsureFolder()
    {
        string folder = Folder;
        Directory.CreateDirectory(folder);
        Mode(folder, "1777");
        return folder;
    }

    internal static void Mode(string path, string octal)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32(octal, 8)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Registers <paramref name="prefixes"/> as served on 127.0.0.1:<paramref name="localPort"/>, until
    /// disposed.</summary>
    public static IDisposable Register(int localPort, IEnumerable<SharedPrefix> prefixes)
    {
        string file = Path.Combine(EnsureFolder(), $"{Environment.ProcessId}-{localPort}.prefixes");
        File.WriteAllLines(file, new[] { localPort.ToString(CultureInfo.InvariantCulture) }.Concat(prefixes.Select(p => p.FullPrefix)));
        Mode(file, "644");
        return new Registration(file);
    }

    private sealed class Registration(string file) : IDisposable
    {
        public void Dispose()
        {
            try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>A live server's registration, and the account that made it (its file's owner).</summary>
    public sealed record Server(int ProcessId, int LocalPort, SharedPrefix[] Prefixes, uint Owner);

    /// <summary>Every live server's registration.</summary>
    public static List<Server> Read()
    {
        var list = new List<Server>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(Folder, "*.prefixes"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!int.TryParse(name.Split('-')[0], out int pid) || !Alive(pid))
                {
                    try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    continue;
                }
                try
                {
                    if (UrlAcl.OwnerOf(file) is not uint owner) continue;
                    string[] lines = File.ReadAllLines(file);
                    if (lines.Length < 2 || !int.TryParse(lines[0], out int local)) continue;
                    list.Add(new Server(pid, local, lines.Skip(1).Select(SharedPrefix.Parse).OfType<SharedPrefix>().ToArray(), owner));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (DirectoryNotFoundException) { }
        return list;
    }

    private static bool Alive(int pid)
    {
        if (pid == Environment.ProcessId) return true;
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }
}
