// How the service manager reaches a running service off Windows, as the SCM reaches a service process through the
// pipe StartServiceCtrlDispatcher opens: the controls systemd and launchd have no word for -- pause, continue, a
// program's own (128-255), interrogate -- and the state the service reports (SetServiceStatus: PAUSED, a pending
// state, the controls it accepts), which systemd and launchd know only as running. Stopping stays the system's
// (SIGTERM), as a stop must reach a service that no longer answers.
//
// Each service process listens on a socket of its own in the machine state's control/ folder (sticky and
// world-writable, as /tmp is, for a service runs as whatever account it is configured for). Anyone may ask a
// service's state, as Windows lets interactive users query it; a control is taken from root alone (the service
// manager, which has checked the caller against the service's descriptor) or from the service's own account (which
// could do as much to its process anyway). The asker makes sure it is talking to the service: the process at the
// other end is the one launchd or systemd runs it in.

using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WpfWebGpu.Services;

public static class ServiceChannel
{
    // SERVICE_CONTROL_*
    public const uint Stop = 1, Pause = 2, Continue = 3, Interrogate = 4, Shutdown = 5;
    // SERVICE_ACCEPT_*
    public const uint AcceptStop = 1, AcceptPauseContinue = 2, AcceptShutdown = 4;
    public const int ERROR_SERVICE_CANNOT_ACCEPT_CTRL = 1061, ERROR_CALL_NOT_IMPLEMENTED = 120;

    /// <summary>The folder of the services' sockets: control/ beside the database.</summary>
    public static string? Folder(string? databasePath) =>
        databasePath == null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "control");

    // a socket's path is short (104 bytes on macOS): the name hashed
    public static string? SocketOf(string? databasePath, string name)
    {
        if (Folder(databasePath) is not { } folder) return null;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant()));
        return Path.Combine(folder, Convert.ToHexString(hash, 0, 8).ToLowerInvariant() + ".sock");
    }

    /// <summary>Makes the control folder, as the service manager's daemon does when it starts: anyone's to put a
    /// socket in, no one's to remove another's.</summary>
    public static void EnsureFolder(string? databasePath)
    {
        if (Folder(databasePath) is not { } folder || OperatingSystem.IsWindows()) return;
        try
        {
            var open = (UnixFileMode)Convert.ToInt32("1777", 8);
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
            if (File.GetUnixFileMode(folder) != open) File.SetUnixFileMode(folder, open);      // its owner's to change
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A service's state as it reports it, and the controls it accepts.</summary>
    public readonly record struct Reported(ServiceState State, uint Accepted);

    /// <summary>Whether the manager hands a service in the state <paramref name="now"/> the control: 0, or the error
    /// ControlService fails with -- a stopped service takes none, a starting or stopping one none but interrogate, and
    /// stop, pause and continue only when it says it accepts them; a program's own controls (128-255) always.</summary>
    public static int Admit(uint control, Reported now)
    {
        if (now.State == ServiceState.Stopped) return ServiceManager.ERROR_SERVICE_NOT_ACTIVE;
        if (control == Interrogate) return 0;
        if (now.State is ServiceState.StartPending or ServiceState.StopPending) return ERROR_SERVICE_CANNOT_ACCEPT_CTRL;
        uint needs = control switch { Stop => AcceptStop, Pause or Continue => AcceptPauseContinue, Shutdown => uint.MaxValue, _ => 0 };
        if (control is >= 128 and <= 255) needs = 0;
        else if (needs == 0) return ServiceManager.ERROR_INVALID_SERVICE_CONTROL;
        return needs != uint.MaxValue && (now.Accepted & needs) == needs ? 0 : ServiceManager.ERROR_INVALID_SERVICE_CONTROL;
    }

    /// <summary>Listens for the service manager on the service's behalf until disposed: <paramref name="control"/>
    /// takes a control and answers Win32's error for it (0, ERROR_CALL_NOT_IMPLEMENTED), as a HandlerEx answers;
    /// <paramref name="report"/> says the service's state now. Null where there is nowhere to listen.</summary>
    public static IDisposable? Listen(string? databasePath, string name, Func<uint, int> control, Func<Reported> report)
    {
        if (OperatingSystem.IsWindows()) return null;
        EnsureFolder(databasePath);
        if (SocketOf(databasePath, name) is not { } path || !Directory.Exists(Path.GetDirectoryName(path))) return null;
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("666", 8));      // anyone asks; the account decides
            listener.Listen(8);
        }
        catch (Exception e) when (e is SocketException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            listener.Dispose();
            return null;
        }
        var stop = new CancellationTokenSource();
        uint self = WindowsAccounts.CurrentUid();
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                Socket client;
                try { client = await listener.AcceptAsync(stop.Token); }
                catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException) { break; }
                _ = Task.Run(() => Answer(client, self, control, report));
            }
        });
        return new Listening(listener, stop, path);
    }

    private sealed class Listening(Socket listener, CancellationTokenSource stop, string path) : IDisposable
    {
        public void Dispose()
        {
            stop.Cancel();
            listener.Dispose();
            try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Answer(Socket client, uint self, Func<uint, int> control, Func<Reported> report)
    {
        using (client)
        using (var stream = new NetworkStream(client, ownsSocket: false))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            int error = 0;
            try
            {
                string[] parts = (reader.ReadLine() ?? "").Split('\t');
                if (parts[0] == "control")
                {
                    uint? peer = PeerCredentials.Uid(client);
                    if (peer is not { } uid || (uid != 0 && uid != self)) error = ServiceManager.ERROR_ACCESS_DENIED;
                    else if (parts.Length < 2 || !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint code)) error = ServiceManager.ERROR_INVALID_PARAMETER;
                    else error = control(code);
                }
                else if (parts[0] != "status") error = ServiceManager.ERROR_INVALID_PARAMETER;
            }
            catch (IOException) { return; }
            catch (Exception) { error = ServiceManager.ERROR_INVALID_SERVICE_CONTROL; }
            Reported now = report();
            try { stream.Write(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{error}\t{(int)now.State}\t{now.Accepted}\n"))); }
            catch (IOException) { }
        }
    }

    /// <summary>The service's state as it reports it, where it listens in the process <paramref name="processId"/>
    /// (0: any); null where it does not.</summary>
    public static Reported? Query(string? databasePath, string name, int processId) =>
        Ask(databasePath, name, processId, "status") is { } r ? r.Status : null;

    /// <summary>Sends the service a control; its handler's answer (Win32's error) and the state it then reports.
    /// ERROR_SERVICE_NOT_ACTIVE where it does not listen.</summary>
    public static (int Error, Reported Status) Send(string? databasePath, string name, int processId, uint control) =>
        Ask(databasePath, name, processId, string.Create(CultureInfo.InvariantCulture, $"control\t{control}"))
        ?? (ServiceManager.ERROR_SERVICE_NOT_ACTIVE, new Reported(ServiceState.Stopped, 0));

    private static (int Error, Reported Status)? Ask(string? databasePath, string name, int processId, string request)
    {
        if (OperatingSystem.IsWindows() || SocketOf(databasePath, name) is not { } path || !File.Exists(path)) return null;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(path));
            // the service, not a socket someone left under its name
            if (processId > 0 && PeerCredentials.Pid(socket) is { } pid && pid != processId) return null;
            socket.ReceiveTimeout = socket.SendTimeout = 30_000;     // the SCM's wait for a handler
            using var stream = new NetworkStream(socket, ownsSocket: false);
            stream.Write(Encoding.UTF8.GetBytes(request + "\n"));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string[] parts = (reader.ReadLine() ?? "").Split('\t');
            if (parts.Length < 3 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out int error)
                || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out int state) || !uint.TryParse(parts[2], CultureInfo.InvariantCulture, out uint accepted))
                return null;
            return (error, new Reported((ServiceState)state, accepted));
        }
        catch (Exception e) when (e is SocketException or IOException or ArgumentException) { return null; }
    }
}

/// <summary>The account and process at the other end of a Unix socket.</summary>
internal static class PeerCredentials
{
    // LOCAL_PEERPID on macOS, SO_PEERCRED's pid (offset 0) on Linux
    public static int? Pid(Socket socket)
    {
        Span<byte> cred = stackalloc byte[128];
        try
        {
            int n = OperatingSystem.IsMacOS() ? socket.GetRawSocketOption(0, 2, cred)
                  : OperatingSystem.IsLinux() ? socket.GetRawSocketOption(1, 17, cred) : 0;
            return n >= 4 ? BitConverter.ToInt32(cred[..4]) : null;
        }
        catch (SocketException) { return null; }
    }

    // LOCAL_PEERCRED's xucred (cr_version, cr_uid, ...) on macOS, SO_PEERCRED's ucred (pid, uid, gid) on Linux -- the
    // uid at offset 4 in both
    public static uint? Uid(Socket socket)
    {
        Span<byte> cred = stackalloc byte[128];
        try
        {
            int n = OperatingSystem.IsMacOS() ? socket.GetRawSocketOption(0, 1, cred)
                  : OperatingSystem.IsLinux() ? socket.GetRawSocketOption(1, 17, cred) : 0;
            return n >= 8 ? BitConverter.ToUInt32(cred[4..8]) : null;
        }
        catch (SocketException) { return null; }
    }
}
