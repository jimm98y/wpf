// advapi32: a service process's side of the service control manager, for a program that talks to it itself rather
// than through .NET's ServiceBase (which the ServiceController stand-in runs the same way): StartServiceCtrlDispatcher,
// RegisterServiceCtrlHandler(Ex), SetServiceStatus.
//
// Where the manager is systemd or launchd (or a terminal), this process is its own manager: each service of the
// dispatcher's table is started -- its ServiceMain on a thread of its own, as the manager starts it -- and the
// dispatcher returns once every one has reported itself stopped, as it does on Windows. The manager's stop is the
// system's: SIGTERM (systemd's, launchd's) or Ctrl-C sends each running service SERVICE_CONTROL_STOP through its
// handler, and a service still not stopped when its time is up (20 seconds, or what its wait hints ask for) is
// ended with the process, as the manager ends one that does not stop.

using System.Runtime.InteropServices;

namespace WpfWebGpu.Win32;

public static unsafe partial class Exports
{
    private const uint SERVICE_STOPPED = 1, SERVICE_RUNNING = 4;
    private const uint SERVICE_CONTROL_STOP = 1, SERVICE_CONTROL_SHUTDOWN = 5;
    private const int StopTimeoutMs = 20_000;

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_TABLE_ENTRYW { public char* lpServiceName; public nint lpServiceProc; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SERVICE_STATUS
    {
        public uint dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode, dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    private sealed class Service
    {
        public required string Name;
        public nint Handler, Context;
        public bool Ex;
        public volatile uint State = 2;      // SERVICE_START_PENDING
        public uint WaitHint, ExitCode;
        public long Deadline;
    }

    private static readonly List<Service> s_services = new();
    private static readonly ManualResetEventSlim s_servicesChanged = new();

    [Win32Export("StartServiceCtrlDispatcherW")]
    public static int StartServiceCtrlDispatcherW(SERVICE_TABLE_ENTRYW* table)
    {
        if (table == null) { SetLastError(87); return FALSE; }        // ERROR_INVALID_PARAMETER
        var started = new List<Service>();
        for (SERVICE_TABLE_ENTRYW* e = table; e->lpServiceName != null; e++)
        {
            string name = new string(e->lpServiceName);
            var service = new Service { Name = name };
            lock (s_services) s_services.Add(service);
            started.Add(service);
            nint proc = e->lpServiceProc;
            var thread = new Thread(() =>
            {
                // ServiceMain(argc, argv): the service's name, then no start parameters
                char* argv0 = (char*)Marshal.StringToHGlobalUni(name);
                char** argv = stackalloc char*[2];
                argv[0] = argv0;
                argv[1] = null;
                try { ((delegate* unmanaged<uint, char**, void>)proc)(1, argv); }
                catch (Exception ex) { Console.Error.WriteLine($"{name}: ServiceMain failed: {ex}"); service.State = SERVICE_STOPPED; s_servicesChanged.Set(); }
                finally { Marshal.FreeHGlobal((nint)argv0); }
            }) { Name = "ServiceMain " + name, IsBackground = true };
            thread.Start();
        }

        bool stopping = false;
        var signals = new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP }
            .Select(sig => PosixSignalRegistration.Create(sig, ctx => { ctx.Cancel = true; stopping = true; s_servicesChanged.Set(); }))
            .ToList();
        try
        {
            bool asked = false;
            while (true)
            {
                s_servicesChanged.Wait(250);
                s_servicesChanged.Reset();
                if (started.All(s => s.State == SERVICE_STOPPED)) break;
                if (stopping && !asked)
                {
                    asked = true;
                    Console.Error.WriteLine($"stopping {string.Join(", ", started.Select(s => s.Name))}");
                    foreach (var s in started.Where(s => s.State != SERVICE_STOPPED))
                    {
                        s.Deadline = Environment.TickCount64 + StopTimeoutMs;
                        Service target = s;
                        new Thread(() => Control(target, SERVICE_CONTROL_STOP)) { IsBackground = true, Name = "service control " + s.Name }.Start();
                    }
                }
                if (asked && started.Any(s => s.State != SERVICE_STOPPED && Environment.TickCount64 > Interlocked.Read(ref s.Deadline)))
                {
                    Console.Error.WriteLine("services did not stop in time; ending the process");
                    Console.Error.Flush();
                    Environment.Exit((int)(started.FirstOrDefault(s => s.ExitCode != 0)?.ExitCode ?? 0));
                }
            }
        }
        finally
        {
            foreach (var r in signals) r.Dispose();
        }
        SetLastError(0);
        return TRUE;
    }

    private static void Control(Service s, uint control)
    {
        if (s.Handler == 0) return;
        try
        {
            if (s.Ex) ((delegate* unmanaged<uint, uint, nint, nint, uint>)s.Handler)(control, 0, 0, s.Context);
            else ((delegate* unmanaged<uint, void>)s.Handler)(control);
        }
        catch (Exception e) { Console.Error.WriteLine($"{s.Name}: its control handler failed: {e}"); }
    }

    private static Service? ServiceNamed(string? name)
    {
        lock (s_services)
            return s_services.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? (s_services.Count == 1 ? s_services[0] : null);       // a single service's table: its name is optional
    }

    // a service's status handle: its index, never zero
    private static nint StatusHandle(Service s) { lock (s_services) return s_services.IndexOf(s) + 1; }
    private static Service? FromStatusHandle(nint h) { lock (s_services) return h > 0 && h <= s_services.Count ? s_services[(int)h - 1] : null; }

    [Win32Export("RegisterServiceCtrlHandlerExW")]
    public static nint RegisterServiceCtrlHandlerExW(char* name, nint handler, nint context)
    {
        if (ServiceNamed(name == null ? null : new string(name)) is not { } s) { SetLastError(1060); return 0; }   // ERROR_SERVICE_DOES_NOT_EXIST
        s.Handler = handler; s.Context = context; s.Ex = true;
        return StatusHandle(s);
    }

    [Win32Export("RegisterServiceCtrlHandlerW")]
    public static nint RegisterServiceCtrlHandlerW(char* name, nint handler)
    {
        if (ServiceNamed(name == null ? null : new string(name)) is not { } s) { SetLastError(1060); return 0; }
        s.Handler = handler; s.Ex = false;
        return StatusHandle(s);
    }

    [Win32Export("SetServiceStatus")]
    public static int SetServiceStatus(nint handle, SERVICE_STATUS* status)
    {
        if (status == null || FromStatusHandle(handle) is not { } s) { SetLastError(6); return FALSE; }     // ERROR_INVALID_HANDLE
        uint was = s.State;
        s.State = status->dwCurrentState;
        s.ExitCode = status->dwWin32ExitCode == 1066 ? status->dwServiceSpecificExitCode : status->dwWin32ExitCode;
        // a pending stop asking for more time gets it, as the manager reads the wait hint
        if (status->dwWaitHint > 0) Interlocked.Exchange(ref s.Deadline, Math.Max(Interlocked.Read(ref s.Deadline), Environment.TickCount64 + status->dwWaitHint));
        if (was != s.State)
        {
            if (s.State == SERVICE_RUNNING) Console.Error.WriteLine($"{s.Name}: Service started successfully.");
            if (s.State == SERVICE_STOPPED) Console.Error.WriteLine($"{s.Name}: Service stopped{(s.ExitCode != 0 ? $" (exit code {s.ExitCode})" : "")}.");
        }
        s_servicesChanged.Set();
        SetLastError(0);
        return TRUE;
    }
}
