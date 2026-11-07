// An app's side of the machine's http.sys service: whether it runs, and starting it where it does not. Nothing here
// needs ASP.NET Core (an HttpListener program may not carry it): the service itself (HttpSysService) does.

using System.Net;
using System.Net.Sockets;

namespace WpfWebGpu.HttpSys;

public static class HttpSysClient
{
    /// <summary>What the service tells a server about a request's connection: scheme;local address;local port;remote
    /// address;remote port, as http.sys would have.</summary>
    public const string ConnectionHeader = "X-WpfWebGpu-HttpSys";

    internal static FileStream? TryLock(string folder)
    {
        string path = Path.Combine(folder, "service.lock");
        try
        {
            var f = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            HttpSysRegistry.Mode(path, "666");
            return f;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Whether an instance serves the machine's prefixes.</summary>
    public static bool IsRunning()
    {
        using var f = TryLock(HttpSysRegistry.EnsureFolder());
        return f == null;
    }

    /// <summary>
    /// For a server that has registered: the machine's instance, started where none runs -- through the launcher the
    /// app was started by (WPFWEBGPU_LAUNCHER, and WPFWEBGPU_LAUNCHER_DLL where that is the dotnet host) -- and its
    /// ports waited for, so the prefixes answer once the server has started, as http.sys's do. False where none
    /// could be started.
    /// </summary>
    public static bool EnsureRunning(IEnumerable<int> ports, Action<string> log)
    {
        if (!IsRunning())
        {
            string? launcher = Environment.GetEnvironmentVariable("WPFWEBGPU_LAUNCHER");
            if (string.IsNullOrEmpty(launcher))
            {
                log("no http.sys service runs, and this app was not started by wpfrun, which would start one: its prefixes are registered but not served");
                return false;
            }
            var command = new List<string> { launcher };
            if (Environment.GetEnvironmentVariable("WPFWEBGPU_LAUNCHER_DLL") is { Length: > 0 } dll) command.Add(dll);
            command.Add("httpsys");
            command.Add("--on-demand");
            Spawn(command, Path.Combine(HttpSysRegistry.Folder, "service.log"));
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        foreach (int port in ports.Distinct())
        {
            while (!Accepts(port))
            {
                if (DateTime.UtcNow > deadline) { log($"port {port} is not served yet"); break; }
                Thread.Sleep(100);
            }
        }
        return true;
    }

    // started apart: no handle of the app's inherited, its output to the service's log
    private static void Spawn(List<string> command, string logFile)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("exec \"$@\" </dev/null >>\"$WPFWEBGPU_HTTPSYS_LOG\" 2>&1");
        psi.ArgumentList.Add("httpsys");
        foreach (string a in command) psi.ArgumentList.Add(a);
        psi.Environment["WPFWEBGPU_HTTPSYS_LOG"] = logFile;
        using var p = System.Diagnostics.Process.Start(psi);
    }

    private static bool Accepts(int port)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException) { return false; }
    }
}
