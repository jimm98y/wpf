// A Windows service, run where the service manager is systemd or launchd (or a terminal) -- on Windows the service
// control manager runs it (ServiceBase.Windows.cs): what the service control
// manager does with a ServiceBase on Windows, done by Run itself. Each service is started (OnStart, with no start
// parameters, as a service the manager starts automatically has none), and the process then waits as a service
// process does until it is told to stop: by the manager -- SIGTERM, which systemd and launchd send -- or Ctrl-C at a
// terminal, which stops every service (OnStop) and returns from Run, or by the services stopping themselves (Stop).
// A service whose OnStart throws is stopped and the process ends with its exit code, as the manager records a
// service that failed to start. A stop is given the time the manager gives it (20 seconds, more on each
// RequestAdditionalTime); a service still stopping then is abandoned and the process ended, as the manager ends a
// service that does not stop, rather than left running with its stop half done.

using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.ServiceProcess
{
    [InstallerType("System.ServiceProcess.Design.ServiceInstallerInstaller, System.Design, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
    public partial class ServiceBase : Component
    {
        public const int MaxNameLength = 80;

        private const int StopTimeoutMs = 20_000;

        private string _serviceName = "";
        private long _stopDeadline;
        private EventLog _eventLog;
        private bool _running;
        private static readonly object s_gate = new object();
        private static ManualResetEventSlim s_stopped;
        private static ServiceBase[] s_services;

        public ServiceBase()
        {
            AutoLog = true;
            CanStop = true;
        }

        [DefaultValue(true)] public bool AutoLog { get; set; }
        [DefaultValue(false)] public bool CanHandlePowerEvent { get; set; }
        [DefaultValue(false)] public bool CanHandleSessionChangeEvent { get; set; }
        [DefaultValue(false)] public bool CanPauseAndContinue { get; set; }
        [DefaultValue(false)] public bool CanShutdown { get; set; }
        [DefaultValue(true)] public bool CanStop { get; set; }
        public int ExitCode { get; set; }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public virtual EventLog EventLog => _eventLog ??= new EventLog("Application") { Source = ServiceName };

        /// <summary>The service control manager's status handle for it on Windows; none elsewhere.</summary>
        [EditorBrowsable(EditorBrowsableState.Advanced)]
        protected IntPtr ServiceHandle => OperatingSystem.IsWindows() ? WindowsServiceHandle : IntPtr.Zero;

        public string ServiceName
        {
            get => _serviceName;
            set
            {
                if (_running) throw new InvalidOperationException("Cannot change service name when the service is running.");
                if (value == null || value.Length == 0 || value.Length > MaxNameLength || value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0)
                    throw new ArgumentException($"Service name {value} contains invalid characters, is empty, or is too long (max length = {MaxNameLength}).");
                _serviceName = value;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _eventLog?.Dispose();
            base.Dispose(disposing);
        }

        protected virtual void OnContinue() { }
        protected virtual void OnCustomCommand(int command) { }
        protected virtual void OnPause() { }
        protected virtual bool OnPowerEvent(PowerBroadcastStatus powerStatus) => true;
        protected virtual void OnSessionChange(SessionChangeDescription changeDescription) { }
        protected virtual void OnShutdown() { }
        protected virtual void OnStart(string[] args) { }
        protected virtual void OnStop() { }

        /// <summary>More time for the start or stop in progress, as the manager gives it.</summary>
        public void RequestAdditionalTime(int milliseconds)
        {
            if (OperatingSystem.IsWindows()) { RequestWindowsTime(milliseconds); return; }
            Interlocked.Add(ref _stopDeadline, Math.Max(0, milliseconds));
        }
        public void RequestAdditionalTime(TimeSpan time) => RequestAdditionalTime((int)Math.Min(int.MaxValue, time.TotalMilliseconds));

        [EditorBrowsable(EditorBrowsableState.Never)]
        public void ServiceMainCallback(int argCount, IntPtr argPointer)
        {
            if (OperatingSystem.IsWindows()) WindowsServiceMain(argCount, argPointer);
        }

        /// <summary>The service stopped by itself, as when it asks the manager to stop it.</summary>
        public void Stop()
        {
            if (OperatingSystem.IsWindows()) { WindowsStop(); return; }
            StopOne(this);
            lock (s_gate)
                if (s_services != null && Array.TrueForAll(s_services, s => !s._running)) s_stopped?.Set();
        }

        public static void Run(ServiceBase service)
        {
            if (service == null) throw new ArgumentException("No services were given to start.");
            Run(new[] { service });
        }

        public static void Run(ServiceBase[] services)
        {
            if (services == null || services.Length == 0) throw new ArgumentException("No services were given to start.");
            // Windows: the service control manager runs them
            if (OperatingSystem.IsWindows()) { RunWindows(services); return; }
            lock (s_gate)
            {
                s_services = services;
                s_stopped = new ManualResetEventSlim();
            }
            var signals = new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP }
                .Select(sig => PosixSignalRegistration.Create(sig, ctx =>
                {
                    ctx.Cancel = true;
                    Console.Error.WriteLine($"{ctx.Signal}: stopping {string.Join(", ", services.Select(s => s.ServiceName))}");
                    s_stopped.Set();
                }))
                .ToList();
            try
            {
                foreach (ServiceBase service in services)
                {
                    try
                    {
                        service._running = true;
                        service.OnStart(Array.Empty<string>());
                        if (service.AutoLog) Log(service, "Service started successfully.");
                    }
                    catch (Exception e)
                    {
                        service._running = false;
                        Log(service, $"Service cannot be started. {e}");
                        if (service.ExitCode == 0) service.ExitCode = 1064;      // ERROR_EXCEPTION_IN_SERVICE
                        Environment.ExitCode = service.ExitCode;
                    }
                }
                if (Array.Exists(services, s => s._running)) s_stopped.Wait();
                bool stopped = true;
                foreach (ServiceBase service in services) stopped &= StopWithin(service);
                if (!stopped)
                {
                    // a stop that does not finish: the process ends, as the manager ends a service that does not stop
                    Console.Error.WriteLine("services did not stop in time; ending the process");
                    Console.Error.Flush();
                    Environment.Exit(Array.Find(services, s => s.ExitCode != 0)?.ExitCode ?? 0);
                }
            }
            finally
            {
                foreach (var r in signals) r.Dispose();
            }
        }

        /// <summary>Stops the service on a thread of its own, as the manager's control thread does; false when it has
        /// not stopped by its deadline.</summary>
        private static bool StopWithin(ServiceBase service)
        {
            Interlocked.Exchange(ref service._stopDeadline, Environment.TickCount64 + StopTimeoutMs);
            var stop = new Thread(() => StopOne(service)) { IsBackground = true, Name = "service stop: " + service.ServiceName };
            stop.Start();
            while (!stop.Join(250))
                if (Environment.TickCount64 > Interlocked.Read(ref service._stopDeadline))
                {
                    Log(service, "Service did not stop in time.");
                    return false;
                }
            return true;
        }

        private static void StopOne(ServiceBase service)
        {
            lock (service)
            {
                if (!service._running) return;
                service._running = false;
            }
            try
            {
                if (service.AutoLog) Log(service, "Service stopping.");
                if (service.CanStop) service.OnStop();
                if (service.AutoLog) Log(service, "Service stopped successfully.");
            }
            catch (Exception e)
            {
                Log(service, $"Failed to stop service. {e}");
            }
        }

        // where a service's journal is read off Windows: its standard error, which systemd and launchd keep
        private static void Log(ServiceBase service, string message) =>
            Console.Error.WriteLine($"{(service.ServiceName.Length > 0 ? service.ServiceName : service.GetType().Name)}: {message}");
    }
}
