// A Windows service on Windows: run by the service control manager, as .NET's own ServiceBase runs it. Run hands the
// process's services to the manager (StartServiceCtrlDispatcher, which returns when all of them have stopped); the
// manager calls each one's ServiceMain, which registers its control handler, reports START_PENDING, runs OnStart with
// the start parameters and reports RUNNING; the handler turns the manager's controls into OnStop, OnPause, OnContinue,
// OnShutdown, OnPowerEvent, OnSessionChange and OnCustomCommand, reporting each state the way through. A service that
// throws from OnStart is reported stopped with ERROR_EXCEPTION_IN_SERVICE, as the manager records it.

using System.Runtime.InteropServices;
using System.Threading;

namespace System.ServiceProcess
{
    public partial class ServiceBase
    {
        private const uint SERVICE_WIN32_OWN_PROCESS = 0x10, SERVICE_WIN32_SHARE_PROCESS = 0x20;
        private const uint SERVICE_STOPPED = 1, SERVICE_START_PENDING = 2, SERVICE_STOP_PENDING = 3, SERVICE_RUNNING = 4,
            SERVICE_CONTINUE_PENDING = 5, SERVICE_PAUSE_PENDING = 6, SERVICE_PAUSED = 7;
        private const uint SERVICE_ACCEPT_STOP = 0x1, SERVICE_ACCEPT_PAUSE_CONTINUE = 0x2, SERVICE_ACCEPT_SHUTDOWN = 0x4,
            SERVICE_ACCEPT_POWEREVENT = 0x40, SERVICE_ACCEPT_SESSIONCHANGE = 0x80;
        private const uint SERVICE_CONTROL_STOP = 1, SERVICE_CONTROL_PAUSE = 2, SERVICE_CONTROL_CONTINUE = 3, SERVICE_CONTROL_INTERROGATE = 4,
            SERVICE_CONTROL_SHUTDOWN = 5, SERVICE_CONTROL_POWEREVENT = 0xD, SERVICE_CONTROL_SESSIONCHANGE = 0xE;
        private const int ERROR_CALL_NOT_IMPLEMENTED = 120, ERROR_FAILED_SERVICE_CONTROLLER_CONNECT = 1063, ERROR_EXCEPTION_IN_SERVICE = 1064;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void ServiceMainProc(int argCount, IntPtr argPointer);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int HandlerProc(int control, int eventType, IntPtr eventData, IntPtr context);

        private IntPtr _statusHandle;
        private uint _state = SERVICE_STOPPED, _checkPoint;
        private ServiceMainProc _mainProc;
        private HandlerProc _handlerProc;
        private bool _shared;

        private IntPtr WindowsServiceHandle => _statusHandle;

        private static void RunWindows(ServiceBase[] services)
        {
            bool shared = services.Length > 1;
            int entry = 2 * IntPtr.Size;
            IntPtr table = Marshal.AllocHGlobal(entry * (services.Length + 1));
            var names = new IntPtr[services.Length];
            try
            {
                for (int i = 0; i < services.Length; i++)
                {
                    ServiceBase service = services[i];
                    service._shared = shared;
                    service._mainProc = service.ServiceMainCallback;
                    names[i] = Marshal.StringToHGlobalUni(service.ServiceName);
                    Marshal.WriteIntPtr(table, i * entry, names[i]);
                    Marshal.WriteIntPtr(table, i * entry + IntPtr.Size, Marshal.GetFunctionPointerForDelegate(service._mainProc));
                }
                Marshal.WriteIntPtr(table, services.Length * entry, IntPtr.Zero);
                Marshal.WriteIntPtr(table, services.Length * entry + IntPtr.Size, IntPtr.Zero);
                if (!StartServiceCtrlDispatcherW(table))
                {
                    int error = Marshal.GetLastWin32Error();
                    // run from a console or a debugger, not by the manager: .NET says so and returns
                    string message = error == ERROR_FAILED_SERVICE_CONTROLLER_CONNECT
                        ? "Cannot start service from the command line or a debugger.  A Windows Service must first be installed and then started with the ServerExplorer, Windows Services Administrative tool or the NET START command."
                        : new ComponentModel.Win32Exception(error).Message;
                    Console.Error.WriteLine(message);
                    foreach (ServiceBase service in services) if (service.AutoLog) Log(service, message);
                }
            }
            finally
            {
                foreach (IntPtr n in names) if (n != IntPtr.Zero) Marshal.FreeHGlobal(n);
                Marshal.FreeHGlobal(table);
                foreach (ServiceBase service in services) GC.KeepAlive(service._mainProc);
            }
        }

        private void WindowsServiceMain(int argCount, IntPtr argPointer)
        {
            _handlerProc = ServiceCommandCallback;
            _statusHandle = RegisterServiceCtrlHandlerExW(ServiceName, _handlerProc, IntPtr.Zero);
            if (_statusHandle == IntPtr.Zero) return;
            // the start parameters, the service's name first
            var args = new string[Math.Max(0, argCount - 1)];
            for (int i = 1; i < argCount; i++) args[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argPointer, i * IntPtr.Size)) ?? "";
            SetState(SERVICE_START_PENDING, 0, 3000);
            try
            {
                _running = true;
                OnStart(args);
                if (AutoLog) Log(this, "Service started successfully.");
                SetState(SERVICE_RUNNING, 0, 0);
            }
            catch (Exception e)
            {
                _running = false;
                Log(this, $"Service cannot be started. {e}");
                if (ExitCode == 0) ExitCode = ERROR_EXCEPTION_IN_SERVICE;
                SetState(SERVICE_STOPPED, (uint)ExitCode, 0);
            }
        }

        private int ServiceCommandCallback(int control, int eventType, IntPtr eventData, IntPtr context)
        {
            switch ((uint)control)
            {
                case SERVICE_CONTROL_INTERROGATE:
                    SetState(_state, 0, 0);
                    return 0;
                case SERVICE_CONTROL_STOP when CanStop:
                    ThreadPool.QueueUserWorkItem(_ => WindowsStop());
                    return 0;
                case SERVICE_CONTROL_SHUTDOWN when CanShutdown:
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { OnShutdown(); } catch (Exception e) { Log(this, $"Failed to shut down service. {e}"); }
                        _running = false;
                        SetState(SERVICE_STOPPED, (uint)ExitCode, 0);
                    });
                    return 0;
                case SERVICE_CONTROL_PAUSE when CanPauseAndContinue:
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        SetState(SERVICE_PAUSE_PENDING, 0, 3000);
                        try { OnPause(); SetState(SERVICE_PAUSED, 0, 0); }
                        catch (Exception e) { Log(this, $"Failed to pause service. {e}"); SetState(SERVICE_RUNNING, 0, 0); }
                    });
                    return 0;
                case SERVICE_CONTROL_CONTINUE when CanPauseAndContinue:
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        SetState(SERVICE_CONTINUE_PENDING, 0, 3000);
                        try { OnContinue(); SetState(SERVICE_RUNNING, 0, 0); }
                        catch (Exception e) { Log(this, $"Failed to continue service. {e}"); SetState(SERVICE_PAUSED, 0, 0); }
                    });
                    return 0;
                case SERVICE_CONTROL_POWEREVENT when CanHandlePowerEvent:
                    return OnPowerEvent((PowerBroadcastStatus)eventType) ? 0 : 0x424D5144;   // BROADCAST_QUERY_DENY
                case SERVICE_CONTROL_SESSIONCHANGE when CanHandleSessionChangeEvent:
                    // WTSSESSION_NOTIFICATION: cbSize, dwSessionId
                    int session = eventData != IntPtr.Zero ? Marshal.ReadInt32(eventData, 4) : 0;
                    ThreadPool.QueueUserWorkItem(_ => OnSessionChange(new SessionChangeDescription((SessionChangeReason)eventType, session)));
                    return 0;
                default:
                    if (control >= 128 && control <= 255)
                    {
                        ThreadPool.QueueUserWorkItem(_ => OnCustomCommand(control));
                        return 0;
                    }
                    return ERROR_CALL_NOT_IMPLEMENTED;
            }
        }

        private void WindowsStop()
        {
            if (!_running) return;
            SetState(SERVICE_STOP_PENDING, 0, StopTimeoutMs);
            StopOne(this);
            SetState(SERVICE_STOPPED, (uint)ExitCode, 0);
        }

        private void SetState(uint state, uint exitCode, int waitHint)
        {
            if (_statusHandle == IntPtr.Zero) return;
            bool pending = state is SERVICE_START_PENDING or SERVICE_STOP_PENDING or SERVICE_PAUSE_PENDING or SERVICE_CONTINUE_PENDING;
            _checkPoint = pending ? _checkPoint + 1 : 0;
            _state = state;
            uint accepted = state == SERVICE_START_PENDING || state == SERVICE_STOPPED ? 0
                : (CanStop ? SERVICE_ACCEPT_STOP : 0) | (CanPauseAndContinue ? SERVICE_ACCEPT_PAUSE_CONTINUE : 0) | (CanShutdown ? SERVICE_ACCEPT_SHUTDOWN : 0)
                  | (CanHandlePowerEvent ? SERVICE_ACCEPT_POWEREVENT : 0) | (CanHandleSessionChangeEvent ? SERVICE_ACCEPT_SESSIONCHANGE : 0);
            var status = new uint[]
            {
                _shared ? SERVICE_WIN32_SHARE_PROCESS : SERVICE_WIN32_OWN_PROCESS, state, accepted,
                exitCode == 0 ? 0u : 1066u /* ERROR_SERVICE_SPECIFIC_ERROR */, exitCode, _checkPoint, (uint)Math.Max(0, waitHint),
            };
            SetServiceStatus(_statusHandle, status);
        }

        // a start or stop that needs longer: the manager is told to wait (a new checkpoint, a longer hint)
        private void RequestWindowsTime(int milliseconds)
        {
            if (_state is SERVICE_START_PENDING or SERVICE_STOP_PENDING or SERVICE_PAUSE_PENDING or SERVICE_CONTINUE_PENDING)
                SetState(_state, 0, milliseconds);
        }

        [DllImport("advapi32", SetLastError = true)]
        private static extern bool StartServiceCtrlDispatcherW(IntPtr table);

        [DllImport("advapi32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr RegisterServiceCtrlHandlerExW(string serviceName, HandlerProc handler, IntPtr context);

        [DllImport("advapi32", SetLastError = true)]
        private static extern bool SetServiceStatus(IntPtr handle, uint[] status);
    }

    /// <summary>A session change the service manager told a service of.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public readonly struct SessionChangeDescription : IEquatable<SessionChangeDescription>
    {
        internal SessionChangeDescription(SessionChangeReason reason, int id)
        {
            Reason = reason;
            SessionId = id;
        }

        public SessionChangeReason Reason { get; }

        public int SessionId { get; }

        public override bool Equals([Diagnostics.CodeAnalysis.NotNullWhen(true)] object obj) => obj is SessionChangeDescription d && Equals(d);

        public bool Equals(SessionChangeDescription changeDescription) => Reason == changeDescription.Reason && SessionId == changeDescription.SessionId;

        public override int GetHashCode() => (int)Reason ^ SessionId;

        public static bool operator ==(SessionChangeDescription a, SessionChangeDescription b) => a.Equals(b);

        public static bool operator !=(SessionChangeDescription a, SessionChangeDescription b) => !a.Equals(b);
    }
}
