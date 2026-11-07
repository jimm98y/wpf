// The service control manager's client, answered by the machine's service manager off Windows (WpfWebGpu.Services):
// the services the installation registered, their state as launchd or systemd has it, and starting and stopping them
// as each one's security descriptor allows the caller -- refused as Windows refuses (InvalidOperationException over
// the Win32Exception with Windows' error). A name that is not installed fails as Windows fails for it (1060).

using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using WpfWebGpu.Services;

namespace System.ServiceProcess
{
    [Designer("System.ServiceProcess.Design.ServiceControllerDesigner, System.Design, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
    public partial class ServiceController : Component
    {
        private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
        private const int ERROR_INVALID_SERVICE_CONTROL = 1052;
        private const int RPC_S_SERVER_UNAVAILABLE = 1722;
        private const int MaxNameLength = 80;

        private string _name;
        private string _displayName = "";
        private string _machineName;
        private ServiceControllerStatus? _status;     // as .NET keeps it: read once, until Refresh

        public ServiceController() : this("", ".", validateName: false) { }

        public ServiceController(string name) : this(name, ".") { }

        public ServiceController(string name, string machineName) : this(name, machineName, validateName: true) { }

        private ServiceController(string name, string machineName, bool validateName)
        {
            if (!ValidMachineName(machineName)) throw new ArgumentException($"MachineName value {machineName} is invalid.");
            if (validateName && !ValidServiceName(name)) throw new ArgumentException($"Service name {name} contains invalid characters, is empty, or is too long (max length = {MaxNameLength}).");
            _name = name ?? "";
            _machineName = machineName;
        }

        public bool CanPauseAndContinue { get { Record(); return false; } }

        public bool CanShutdown { get { Record(); return false; } }

        public bool CanStop => Status == ServiceControllerStatus.Running;

        public ServiceController[] DependentServices
        {
            get
            {
                var record = Record();
                return ServiceManager.Database().Services
                    .Where(s => s.DependsOn.Contains(record.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(s => new ServiceController(s.Name, _machineName)).ToArray();
            }
        }

        public string DisplayName
        {
            get => Record().DisplayName;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                _displayName = value;
                _name = "";
                _status = null;
            }
        }

        public string MachineName
        {
            get => _machineName;
            set
            {
                if (!ValidMachineName(value)) throw new ArgumentException($"MachineName value {value} is invalid.");
                _machineName = value;
                _status = null;
            }
        }

        public SafeHandle ServiceHandle
        {
            get
            {
                Record();
                throw new PlatformNotSupportedException("A service handle is the Windows service control manager's.");
            }
        }

        public string ServiceName
        {
            get => _name.Length > 0 && ServiceManager.Database().Find(_name) is { } r && r.Name.Equals(_name, StringComparison.OrdinalIgnoreCase) ? r.Name : Record().Name;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                if (!ValidServiceName(value)) throw new ArgumentException($"Service name {value} contains invalid characters, is empty, or is too long (max length = {MaxNameLength}).");
                _name = value;
                _displayName = "";
                _status = null;
            }
        }

        public ServiceController[] ServicesDependedOn =>
            Record().DependsOn.Select(d => new ServiceController(d, _machineName)).ToArray();

        public ServiceType ServiceType { get { Record(); return ServiceType.Win32OwnProcess; } }

        public ServiceStartMode StartType => Record().Start switch
        {
            ServiceStart.Auto => ServiceStartMode.Automatic,
            ServiceStart.Disabled => ServiceStartMode.Disabled,
            _ => ServiceStartMode.Manual,
        };

        public ServiceControllerStatus Status
        {
            get
            {
                if (_status is { } known) return known;
                var record = Record();
                var status = ServiceManager.StatusOf(ServiceManager.Database(), record);
                _status = (ServiceControllerStatus)(int)status.State;
                return _status.Value;
            }
        }

        public void Close() { _status = null; }

        public void Continue() => throw Refused("Cannot resume {0} service on computer '{1}'.", ERROR_INVALID_SERVICE_CONTROL, "The requested control is not valid for this service.");

        protected override void Dispose(bool disposing) => base.Dispose(disposing);

        public void ExecuteCommand(int command) => throw Refused("Cannot control {0} service on computer '{1}'.", ERROR_INVALID_SERVICE_CONTROL, "The requested control is not valid for this service.");

        public static ServiceController[] GetDevices() => GetDevices(".");

        // drivers are the kernel's here, not services
        public static ServiceController[] GetDevices(string machineName)
        {
            CheckManager(machineName);
            return Array.Empty<ServiceController>();
        }

        public static ServiceController[] GetServices() => GetServices(".");

        public static ServiceController[] GetServices(string machineName)
        {
            CheckManager(machineName);
            return ServiceManager.Database().Services.Select(s => new ServiceController(s.Name, machineName)).ToArray();
        }

        public void Pause() => throw Refused("Cannot pause {0} service on computer '{1}'.", ERROR_INVALID_SERVICE_CONTROL, "The requested control is not valid for this service.");

        public void Refresh() { _status = null; }

        public void Start() => Start(Array.Empty<string>());

        public void Start(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);
            var record = Record();
            try { ServiceManager.Start(record.Name); }
            catch (ServiceException e) { throw Refused("Cannot start service {0} on computer '{1}'.", e.Error, e.Message); }
            _status = null;
        }

        public void Stop() => Stop(stopDependentServices: true);

        // the services that depend on it first, as .NET stops them
        public void Stop(bool stopDependentServices)
        {
            var record = Record();
            if (stopDependentServices)
                foreach (ServiceController dependent in DependentServices)
                    if (dependent.Status != ServiceControllerStatus.Stopped)
                    {
                        dependent.Stop(true);
                        dependent.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    }
            try { ServiceManager.Stop(record.Name); }
            catch (ServiceException e) { throw Refused("Cannot stop {0} service on computer '{1}'.", e.Error, e.Message); }
            _status = null;
        }

        public void WaitForStatus(ServiceControllerStatus desiredStatus) => WaitForStatus(desiredStatus, TimeSpan.MaxValue);

        public void WaitForStatus(ServiceControllerStatus desiredStatus, TimeSpan timeout)
        {
            if (!Enum.IsDefined(desiredStatus)) throw new InvalidEnumArgumentException(nameof(desiredStatus), (int)desiredStatus, typeof(ServiceControllerStatus));
            DateTime until = timeout == TimeSpan.MaxValue ? DateTime.MaxValue : DateTime.UtcNow + timeout;
            Refresh();
            while (Status != desiredStatus)
            {
                if (DateTime.UtcNow >= until) throw new TimeoutException($"Time out has expired and the operation has not been completed.");
                Threading.Thread.Sleep(250);
                Refresh();
            }
        }

        // the service this controller names, by its name or its display name
        private ServiceRecord Record()
        {
            if (!IsLocal(_machineName)) throw NoManager(_machineName);
            string given = _name.Length > 0 ? _name : _displayName;
            if (given.Length > 0 && ServiceManager.Database().Find(given) is { } record) return record;
            throw new InvalidOperationException($"Service '{given}' was not found on computer '{_machineName}'.", new Win32Exception(ERROR_SERVICE_DOES_NOT_EXIST, "The specified service does not exist as an installed service."));
        }

        private Exception Refused(string format, int error, string message)
        {
            var record = Record();
            return new InvalidOperationException(string.Format(format, record.Name, _machineName), new Win32Exception(error, message));
        }

        private static void CheckManager(string machineName)
        {
            if (!ValidMachineName(machineName)) throw new ArgumentException($"MachineName value {machineName} is invalid.");
            if (!IsLocal(machineName)) throw NoManager(machineName);
        }

        private static Exception NoManager(string machineName) =>
            new InvalidOperationException($"Cannot open Service Control Manager on computer '{machineName}'. This operation might require other privileges.", new Win32Exception(RPC_S_SERVER_UNAVAILABLE, "The RPC server is unavailable."));

        private static bool IsLocal(string machineName) =>
            machineName == "." || machineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) || machineName.Equals("localhost", StringComparison.OrdinalIgnoreCase);

        private static bool ValidServiceName(string name) =>
            !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength && name.IndexOf('\\') < 0 && name.IndexOf('/') < 0;

        private static bool ValidMachineName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (value == ".") return true;
            foreach (char c in value)
            {
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.') return false;
            }
            return true;
        }
    }

    /// <summary>A wait for a service's status that ran out of time.</summary>
    public class TimeoutException : SystemException
    {
        public TimeoutException() : base("Time out has expired and the operation has not been completed.") { }

        public TimeoutException(string message) : base(message) { }

        public TimeoutException(string message, Exception innerException) : base(message, innerException) { }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        protected TimeoutException(Runtime.Serialization.SerializationInfo info, Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}
