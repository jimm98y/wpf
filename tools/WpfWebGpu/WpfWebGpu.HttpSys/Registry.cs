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

    /// <summary>
    /// This process's key: in its registrations, which only it and the service (root) may read, and sent back by the
    /// service with every request it forwards (<see cref="HttpSysClient.KeyHeader"/>). What the service tells a server
    /// about a connection -- its caller's account among it -- is taken only with the key: anything else at the server's
    /// loopback port could have written it, where http.sys, in the kernel, cannot be stood in for.
    /// </summary>
    public static readonly string ProcessKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    /// <summary>Registers <paramref name="prefixes"/> as served on 127.0.0.1:<paramref name="localPort"/>, until
    /// disposed.</summary>
    public static IDisposable Register(int localPort, IEnumerable<SharedPrefix> prefixes)
    {
        string file = Path.Combine(EnsureFolder(), $"{Environment.ProcessId}-{localPort}.prefixes");
        string temp = file + ".tmp";
        File.WriteAllText(temp, "");
        Mode(temp, "600");
        File.WriteAllLines(temp, new[] { localPort.ToString(CultureInfo.InvariantCulture), KeyLine + ProcessKey }.Concat(prefixes.Select(p => p.FullPrefix)));
        File.Move(temp, file, overwrite: true);
        return new Registration(file);
    }

    private const string KeyLine = "key ";

    private sealed class Registration(string file) : IDisposable
    {
        public void Dispose()
        {
            try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>A live server's registration, and the account that made it (its file's owner).</summary>
    public sealed record Server(int ProcessId, int LocalPort, SharedPrefix[] Prefixes, uint Owner, string Key = "");

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
                    string key = lines.Skip(1).FirstOrDefault(l => l.StartsWith(KeyLine, StringComparison.Ordinal))?[KeyLine.Length..] ?? "";
                    list.Add(new Server(pid, local, lines.Skip(1).Where(l => !l.StartsWith(KeyLine, StringComparison.Ordinal)).Select(SharedPrefix.Parse).OfType<SharedPrefix>().ToArray(), owner, key));
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
