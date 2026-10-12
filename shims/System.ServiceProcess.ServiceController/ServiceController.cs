// A service control manager with no services in it. Every query about a named service fails as it
// does on Windows for a service that is not installed; enumeration finds nothing.

using System.ComponentModel;
using System.Runtime.InteropServices;

namespace System.ServiceProcess
{
    [Designer("System.ServiceProcess.Design.ServiceControllerDesigner, System.Design, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
    public partial class ServiceController : Component
    {
        private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
        private const int RPC_S_SERVER_UNAVAILABLE = 1722;
        private const int MaxNameLength = 80;

        private string _name;
        private string _displayName = "";
        private string _machineName;

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

        public bool CanPauseAndContinue => throw NotFound();

        public bool CanShutdown => throw NotFound();

        public bool CanStop => throw NotFound();

        public ServiceController[] DependentServices => throw NotFound();

        public string DisplayName
        {
            get => throw NotFound();
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                _displayName = value;
                _name = "";
            }
        }

        public string MachineName
        {
            get => _machineName;
            set
            {
                if (!ValidMachineName(value)) throw new ArgumentException($"MachineName value {value} is invalid.");
                _machineName = value;
            }
        }

        public SafeHandle ServiceHandle => throw NotFound();

        public string ServiceName
        {
            get => _name.Length > 0 ? _name : throw NotFound();
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                if (!ValidServiceName(value)) throw new ArgumentException($"Service name {value} contains invalid characters, is empty, or is too long (max length = {MaxNameLength}).");
                _name = value;
                _displayName = "";
            }
        }

        public ServiceController[] ServicesDependedOn => throw NotFound();

        public ServiceType ServiceType => throw NotFound();

        public ServiceStartMode StartType => throw NotFound();

        public ServiceControllerStatus Status => throw NotFound();

        public void Close() { }

        public void Continue() => throw NotFound();

        protected override void Dispose(bool disposing) => base.Dispose(disposing);

        public void ExecuteCommand(int command) => throw NotFound();

        public static ServiceController[] GetDevices() => GetDevices(".");

        public static ServiceController[] GetDevices(string machineName) => Enumerate(machineName);

        public static ServiceController[] GetServices() => GetServices(".");

        public static ServiceController[] GetServices(string machineName) => Enumerate(machineName);

        public void Pause() => throw NotFound();

        public void Refresh() { }

        public void Start() => throw NotFound();

        public void Start(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);
            throw NotFound();
        }

        public void Stop() => throw NotFound();

        public void Stop(bool stopDependentServices) => throw NotFound();

        public void WaitForStatus(ServiceControllerStatus desiredStatus) => WaitForStatus(desiredStatus, TimeSpan.MaxValue);

        public void WaitForStatus(ServiceControllerStatus desiredStatus, TimeSpan timeout)
        {
            if (!Enum.IsDefined(desiredStatus)) throw new InvalidEnumArgumentException(nameof(desiredStatus), (int)desiredStatus, typeof(ServiceControllerStatus));
            throw NotFound();
        }

        private static ServiceController[] Enumerate(string machineName)
        {
            if (!ValidMachineName(machineName)) throw new ArgumentException($"MachineName value {machineName} is invalid.");
            if (!IsLocal(machineName)) throw NoManager(machineName);
            return Array.Empty<ServiceController>();
        }

        private Exception NotFound()
        {
            if (!IsLocal(_machineName)) return NoManager(_machineName);
            string given = _name.Length > 0 ? _name : _displayName;
            return new InvalidOperationException($"Service '{given}' was not found on computer '{_machineName}'.", new Win32Exception(ERROR_SERVICE_DOES_NOT_EXIST, "The specified service does not exist as an installed service."));
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
}
