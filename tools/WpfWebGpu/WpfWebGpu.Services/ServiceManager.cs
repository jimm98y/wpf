// The service manager off Windows, as the Win32 service control manager answers: the installed services (the
// database), each one's state and process (launchd's or systemd's), and starting, stopping and reconfiguring them --
// each refused as Windows refuses it, by the service's security descriptor and the caller's account. A product's server
// running as NETWORK SERVICE starts the point managers it needs (their descriptors give NETWORK SERVICE full control);
// a user's unelevated app may query them and no more.
//
// The system's own services are root's to control (launchctl's system domain, systemctl), so a caller that is not root
// asks the service manager's daemon (`wpfrun scm`, root) over a socket beside the database; the daemon learns the
// caller's account from the socket (its peer credentials) and decides for it, as the SCM decides for an RPC caller.

using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace WpfWebGpu.Services;

public enum ServiceState { Stopped = 1, StartPending = 2, StopPending = 3, Running = 4, ContinuePending = 5, PausePending = 6, Paused = 7 }

public readonly record struct ServiceStatus(ServiceState State, int ProcessId, int ExitCode);

/// <summary>A request the service manager refused, with Win32's error (ERROR_ACCESS_DENIED, ...).</summary>
public sealed class ServiceException(int error, string message) : Exception(message)
{
    public int Error { get; } = error;
}

public static class ServiceManager
{
    // SERVICE_* access rights
    public const uint QueryConfig = 0x1, ChangeConfig = 0x2, QueryStatus = 0x4, StartRight = 0x10, StopRight = 0x20, PauseContinue = 0x40;

    public const int ERROR_ACCESS_DENIED = 5, ERROR_DEPENDENT_SERVICES_RUNNING = 1051, ERROR_INVALID_SERVICE_CONTROL = 1052,
        ERROR_SERVICE_ALREADY_RUNNING = 1056, ERROR_SERVICE_DISABLED = 1058, ERROR_SERVICE_DOES_NOT_EXIST = 1060,
        ERROR_SERVICE_NOT_ACTIVE = 1062, ERROR_SERVICE_DEPENDENCY_FAIL = 1068, ERROR_SERVICE_REQUEST_TIMEOUT = 1053,
        RPC_S_SERVER_UNAVAILABLE = 1722;

    /// <summary>The machine's services: the service control manager's on Windows, the installed database elsewhere.</summary>
    public static ServiceDatabase Database() =>
        OperatingSystem.IsWindows() ? WindowsServiceManager.Database() : ServiceDatabase.Load(ServiceDatabase.DefaultPath());

    // ---- state ------------------------------------------------------------------------------------------------

    /// <summary>Every service's state, by name (one question to launchd or systemd for them all).</summary>
    public static Dictionary<string, ServiceStatus> Statuses(ServiceDatabase db)
    {
        if (OperatingSystem.IsWindows()) return WindowsServiceManager.Statuses(db);
        var result = new Dictionary<string, ServiceStatus>(StringComparer.OrdinalIgnoreCase);
        var byUnit = db.Services.Where(s => s.Unit.Length > 0).ToDictionary(s => s.Unit, s => s.Name, StringComparer.Ordinal);
        foreach (var s in db.Services) result[s.Name] = new ServiceStatus(ServiceState.Stopped, 0, 0);
        if (OperatingSystem.IsMacOS())
        {
            // launchctl print system: "\t\t  <pid>  <last exit>  \t<label>" under "services = {"
            var (_, text) = Run("/bin/launchctl", "print", "system");
            bool inServices = false;
            foreach (string line in text.Split('\n'))
            {
                if (line.StartsWith("\tservices = {", StringComparison.Ordinal)) { inServices = true; continue; }
                if (!inServices) continue;
                if (line.StartsWith("\t}", StringComparison.Ordinal)) break;
                string[] parts = line.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !byUnit.TryGetValue(parts[2], out string? name)) continue;
                int pid = int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p : 0;
                int exit = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int e) ? e : 0;
                result[name] = new ServiceStatus(pid > 0 ? ServiceState.Running : ServiceState.Stopped, pid, exit);
            }
        }
        else if (OperatingSystem.IsLinux() && byUnit.Count > 0)
        {
            var args = new List<string> { "show", "-p", "Id,ActiveState,MainPID,ExecMainStatus", "--" };
            args.AddRange(byUnit.Keys);
            var (_, text) = Run("/usr/bin/systemctl", args.ToArray());
            foreach (string block in text.Split("\n\n"))
            {
                var p = block.Split('\n').Select(l => l.Split('=', 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0], kv => kv[1]);
                if (!p.TryGetValue("Id", out string? id) || !byUnit.TryGetValue(id, out string? name)) continue;
                int pid = int.TryParse(p.GetValueOrDefault("MainPID"), out int v) ? v : 0;
                int exit = int.TryParse(p.GetValueOrDefault("ExecMainStatus"), out int x) ? x : 0;
                var state = p.GetValueOrDefault("ActiveState") switch
                {
                    "active" or "reloading" => ServiceState.Running,
                    "activating" => ServiceState.StartPending,
                    "deactivating" => ServiceState.StopPending,
                    _ => ServiceState.Stopped,
                };
                result[name] = new ServiceStatus(state, pid, exit);
            }
        }
        return result;
    }

    public static ServiceStatus StatusOf(ServiceDatabase db, ServiceRecord service) =>
        Statuses(new ServiceDatabase { Services = { service } }).GetValueOrDefault(service.Name, new ServiceStatus(ServiceState.Stopped, 0, 0));

    // ---- control ----------------------------------------------------------------------------------------------

    public static void Start(string name) => Control("start", name, "");
    public static void Stop(string name) => Control("stop", name, "");
    public static void SetStart(string name, ServiceStart start) => Control("config", name, start.ToString());

    // root acts itself; anyone else asks the daemon
    private static void Control(string op, string name, string arg)
    {
        // Windows: the service control manager decides, by the service's descriptor and the caller's token
        if (OperatingSystem.IsWindows()) { WindowsServiceManager.Execute(op, name, arg); return; }
        string? path = ServiceDatabase.DefaultPath();
        if (WindowsAccounts.CurrentUid() == 0) { ServiceControl.Execute(path, op, name, arg, 0); return; }
        ServiceBroker.Request(path, op, name, arg);
    }

    internal static (int Exit, string Output) Run(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output + err.Result);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return (-1, e.Message); }
    }
}

/// <summary>What the service manager does for a request it has decided to grant -- root's work.</summary>
public static class ServiceControl
{
    /// <summary>Carries out <paramref name="op"/> (start, stop, config) on the service for the account
    /// <paramref name="caller"/>, or refuses it as Windows would.</summary>
    public static void Execute(string? databasePath, string op, string name, string arg, uint caller)
    {
        var db = ServiceDatabase.Load(databasePath);
        var service = db.Find(name) ?? throw new ServiceException(ServiceManager.ERROR_SERVICE_DOES_NOT_EXIST, "The specified service does not exist as an installed service.");
        uint right = op switch { "start" => ServiceManager.StartRight, "stop" => ServiceManager.StopRight, "config" => ServiceManager.ChangeConfig, _ => 0 };
        if (right == 0) throw new ServiceException(ServiceManager.ERROR_INVALID_SERVICE_CONTROL, "The requested control is not valid for this service.");
        if (!WindowsAccounts.AccessAllowed(ServiceDatabase.DescriptorOf(service), WindowsAccounts.TokenOf(caller), right))
            throw new ServiceException(ServiceManager.ERROR_ACCESS_DENIED, $"Access is denied ({WindowsAccounts.Describe(caller)}, {service.Name}).");
        switch (op)
        {
            case "start": StartWithDependencies(db, service, new HashSet<string>(StringComparer.OrdinalIgnoreCase)); break;
            case "stop": StopService(db, service); break;
            case "config":
                if (!Enum.TryParse(arg, ignoreCase: true, out ServiceStart start)) throw new ServiceException(87, "The parameter is incorrect.");
                Configure(service, start);
                service.Start = start;
                if (databasePath != null) db.Save(databasePath);
                break;
        }
    }

    // the services it depends on first, as the SCM starts them (whatever the caller's rights to them)
    private static void StartWithDependencies(ServiceDatabase db, ServiceRecord service, HashSet<string> seen)
    {
        if (!seen.Add(service.Name)) return;
        if (service.Start == ServiceStart.Disabled) throw new ServiceException(ServiceManager.ERROR_SERVICE_DISABLED, "The service cannot be started, either because it is disabled or because it has no enabled devices associated with it.");
        var status = ServiceManager.StatusOf(db, service);
        if (status.State == ServiceState.Running)
        {
            if (seen.Count == 1) throw new ServiceException(ServiceManager.ERROR_SERVICE_ALREADY_RUNNING, "An instance of the service is already running.");
            return;
        }
        foreach (string dep in service.DependsOn)
        {
            if (db.Find(dep) is not { } d) continue;
            try { StartWithDependencies(db, d, seen); }
            catch (ServiceException e) when (e.Error != ServiceManager.ERROR_SERVICE_ALREADY_RUNNING)
            { throw new ServiceException(ServiceManager.ERROR_SERVICE_DEPENDENCY_FAIL, "The dependency service or group failed to start."); }
        }
        if (OperatingSystem.IsMacOS())
        {
            string target = "system/" + service.Unit;
            if (ServiceManager.Run("/bin/launchctl", "print", target).Exit != 0)
                ServiceManager.Run("/bin/launchctl", "bootstrap", "system", Plist(service));
            var (exit, output) = ServiceManager.Run("/bin/launchctl", "kickstart", target);
            if (exit != 0) throw new ServiceException(ServiceManager.ERROR_SERVICE_REQUEST_TIMEOUT, "The service did not respond to the start or control request in a timely fashion. " + output.Trim());
        }
        else if (OperatingSystem.IsLinux())
        {
            var (exit, output) = ServiceManager.Run("/usr/bin/systemctl", "start", "--no-block", service.Unit);
            if (exit != 0) throw new ServiceException(ServiceManager.ERROR_SERVICE_REQUEST_TIMEOUT, "The service did not respond to the start or control request in a timely fashion. " + output.Trim());
        }
    }

    private static void StopService(ServiceDatabase db, ServiceRecord service)
    {
        var all = ServiceManager.Statuses(db);
        if (all.GetValueOrDefault(service.Name).State != ServiceState.Running)
            throw new ServiceException(ServiceManager.ERROR_SERVICE_NOT_ACTIVE, "The service has not been started.");
        // a service others need goes only once they have: the SCM refuses to stop it before
        if (db.Services.Any(s => s.DependsOn.Contains(service.Name, StringComparer.OrdinalIgnoreCase) && all.GetValueOrDefault(s.Name).State == ServiceState.Running))
            throw new ServiceException(ServiceManager.ERROR_DEPENDENT_SERVICES_RUNNING, "A stop control has been sent to a service that other running services are dependent on.");
        // the job stays loaded, so a manual service can be started again; a clean exit is not restarted
        if (OperatingSystem.IsMacOS()) ServiceManager.Run("/bin/launchctl", "kill", "SIGTERM", "system/" + service.Unit);
        else if (OperatingSystem.IsLinux()) ServiceManager.Run("/usr/bin/systemctl", "stop", "--no-block", service.Unit);
    }

    // the start type as the system keeps it: whether it runs at boot (launchd's RunAtLoad, systemd's enablement),
    // and a disabled one cannot be started at all; a running service goes on running, as on Windows
    private static void Configure(ServiceRecord service, ServiceStart start)
    {
        if (OperatingSystem.IsMacOS())
        {
            string plist = Plist(service), target = "system/" + service.Unit;
            if (File.Exists(plist))
            {
                const string buddy = "/usr/libexec/PlistBuddy";
                ServiceManager.Run(buddy, "-c", $"Set :RunAtLoad {(start == ServiceStart.Auto ? "true" : "false")}", plist);
                // a service its recovery actions restart: kept alive after any failure when automatic, after a crash
                // otherwise -- launchd's keeping alive after an unsuccessful exit starts the job at load as well
                if (ServiceManager.Run(buddy, "-c", "Print :KeepAlive", plist).Exit == 0)
                    ServiceManager.Run(buddy, "-c", "Delete :KeepAlive", "-c", "Add :KeepAlive dict", "-c",
                        start == ServiceStart.Auto ? "Add :KeepAlive:SuccessfulExit bool false" : "Add :KeepAlive:Crashed bool true", plist);
            }
            if (start == ServiceStart.Disabled) ServiceManager.Run("/bin/launchctl", "disable", target);
            else
            {
                ServiceManager.Run("/bin/launchctl", "enable", target);
                if (ServiceManager.Run("/bin/launchctl", "print", target).Exit != 0 && File.Exists(plist))
                    ServiceManager.Run("/bin/launchctl", "bootstrap", "system", plist);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            if (start == ServiceStart.Disabled) { ServiceManager.Run("/usr/bin/systemctl", "disable", service.Unit); ServiceManager.Run("/usr/bin/systemctl", "mask", service.Unit); }
            else
            {
                ServiceManager.Run("/usr/bin/systemctl", "unmask", service.Unit);
                ServiceManager.Run("/usr/bin/systemctl", start == ServiceStart.Auto ? "enable" : "disable", service.Unit);
            }
        }
    }

    private static string Plist(ServiceRecord service) => "/Library/LaunchDaemons/" + service.Unit + ".plist";
}

/// <summary>The service manager's daemon (`wpfrun scm`) and how a caller that is not root reaches it.</summary>
public static class ServiceBroker
{
    /// <summary>The daemon's socket, beside the database.</summary>
    public static string? SocketPath(string? databasePath) =>
        databasePath == null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "scm.sock");

    /// <summary>Asks the daemon to carry out a request; throws its refusal.</summary>
    public static void Request(string? databasePath, string op, string name, string arg)
    {
        string? socketPath = SocketPath(databasePath);
        if (socketPath == null || !File.Exists(socketPath))
            throw new ServiceException(ServiceManager.RPC_S_SERVER_UNAVAILABLE, "The RPC server is unavailable (the service manager is not running).");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { socket.Connect(new UnixDomainSocketEndPoint(socketPath)); }
        catch (SocketException) { throw new ServiceException(ServiceManager.RPC_S_SERVER_UNAVAILABLE, "The RPC server is unavailable (the service manager is not running)."); }
        using var stream = new NetworkStream(socket, ownsSocket: false);
        byte[] request = Encoding.UTF8.GetBytes($"{op}\t{name}\t{arg}\n");
        stream.Write(request);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string reply = reader.ReadLine() ?? $"{ServiceManager.RPC_S_SERVER_UNAVAILABLE}\tThe service manager closed the connection.";
        string[] parts = reply.Split('\t', 2);
        int code = int.TryParse(parts[0], out int c) ? c : ServiceManager.RPC_S_SERVER_UNAVAILABLE;
        if (code != 0) throw new ServiceException(code, parts.Length > 1 ? parts[1] : "");
    }

    /// <summary>Serves requests until <paramref name="stop"/>: each decided for the account at the other end.</summary>
    public static async Task Serve(string databasePath, Action<string> log, CancellationToken stop)
    {
        string socketPath = SocketPath(databasePath)!;
        try { File.Delete(socketPath); } catch (IOException) { }
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        File.SetUnixFileMode(socketPath, (UnixFileMode)Convert.ToInt32("666", 8));   // anyone may ask; the descriptor decides
        listener.Listen(16);
        log($"service manager: {databasePath}, requests at {socketPath}");
        while (!stop.IsCancellationRequested)
        {
            Socket client;
            try { client = await listener.AcceptAsync(stop); }
            catch (OperationCanceledException) { break; }
            _ = Task.Run(() => Handle(client, databasePath, log));
        }
    }

    private static void Handle(Socket client, string databasePath, Action<string> log)
    {
        using (client)
        using (var stream = new NetworkStream(client, ownsSocket: false))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            string reply;
            try
            {
                uint uid = PeerUid(client) ?? throw new ServiceException(ServiceManager.ERROR_ACCESS_DENIED, "Access is denied (the caller's account is unknown).");
                string[] parts = (reader.ReadLine() ?? "").Split('\t');
                if (parts.Length < 3) throw new ServiceException(87, "The parameter is incorrect.");
                ServiceControl.Execute(databasePath, parts[0], parts[1], parts[2], uid);
                log($"service manager: {parts[0]} {parts[1]} {parts[2]} for {WindowsAccounts.Describe(uid)}");
                reply = "0\t";
            }
            catch (ServiceException e)
            {
                log($"service manager: refused ({e.Error}): {e.Message}");
                reply = $"{e.Error}\t{e.Message.Replace('\n', ' ')}";
            }
            catch (Exception e)
            {
                log("service manager: " + e);
                reply = $"{ServiceManager.ERROR_INVALID_SERVICE_CONTROL}\t{e.Message.Replace('\n', ' ')}";
            }
            try { stream.Write(Encoding.UTF8.GetBytes(reply + "\n")); } catch (IOException) { }
        }
    }

    // the account at the other end of the socket: LOCAL_PEERCRED's xucred (cr_version, cr_uid, ...) on macOS,
    // SO_PEERCRED's ucred (pid, uid, gid) on Linux -- the uid at offset 4 in both
    private static uint? PeerUid(Socket socket)
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
