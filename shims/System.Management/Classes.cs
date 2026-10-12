// The root\cimv2 classes the shim answers, filled from this machine. Property types are WMI's own
// (uint32, uint64, uint16, ...), so the casts apps write against Windows hold: (uint)o["NumberOfCores"].

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace System.Management
{
    internal sealed class WmiClass
    {
        public readonly string Name;
        public readonly string[] Keys;
        private readonly (string Name, CimType Type, bool IsArray)[] _properties;
        private readonly Func<IEnumerable<Dictionary<string, object>>> _instances;

        public WmiClass(string name, string[] keys, (string, CimType, bool)[] properties, Func<IEnumerable<Dictionary<string, object>>> instances)
        {
            Name = name;
            Keys = keys;
            _properties = properties;
            _instances = instances;
        }

        public (string Name, CimType Type, bool IsArray)? Property(string name)
        {
            foreach (var p in _properties)
            {
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return null;
        }

        public WmiData Schema(string server)
        {
            WmiData d = New(server);
            d.IsClass = true;
            foreach (var p in _properties) d.Properties.Add(new PropertyData(p.Name, p.Type, p.IsArray, null, Name));
            return d;
        }

        public IEnumerable<WmiData> Instances(string server)
        {
            foreach (Dictionary<string, object> values in _instances())
            {
                WmiData d = New(server);
                foreach (var p in _properties)
                {
                    values.TryGetValue(p.Name, out object v);
                    d.Properties.Add(new PropertyData(p.Name, p.Type, p.IsArray, ToCim(v, p.Type, p.IsArray), Name));
                }
                yield return d;
            }
        }

        private WmiData New(string server) => new WmiData
        {
            Server = Environment.MachineName.ToUpperInvariant(),
            Namespace = @"root\cimv2",
            ClassName = Name,
            Keys = Keys,
        };

        private static object ToCim(object v, CimType type, bool isArray)
        {
            if (v == null) return null;
            if (isArray)
            {
                var source = ((Collections.IEnumerable)v).Cast<object>().ToArray();
                Array result = Array.CreateInstance(Cimv2.ClrTypeOf(type), source.Length);
                for (int i = 0; i < source.Length; i++) result.SetValue(ToCim(source[i], type, false), i);
                return result;
            }
            if (type == CimType.DateTime && v is DateTime dt) return ManagementDateTimeConverter.ToDmtfDateTime(dt);
            Type clr = Cimv2.ClrTypeOf(type);
            return clr == typeof(object) || clr.IsInstanceOfType(v) ? v : Convert.ChangeType(v, clr, CultureInfo.InvariantCulture);
        }
    }

    internal static class Classes
    {
        private const CimType S = CimType.String, U16 = CimType.UInt16, U32 = CimType.UInt32, U64 = CimType.UInt64,
            I16 = CimType.SInt16, B = CimType.Boolean, D = CimType.DateTime;

        private static (string, CimType, bool) P(string name, CimType type) => (name, type, false);

        private static (string, CimType, bool) A(string name, CimType type) => (name, type, true);

        public static readonly WmiClass[] All =
        {
            new WmiClass("Win32_ComputerSystem", new[] { "Name" }, new[]
            {
                P("BootupState", S), P("Caption", S), P("CurrentTimeZone", I16), P("Description", S), P("DNSHostName", S),
                P("Domain", S), P("DomainRole", U16), P("HypervisorPresent", B), P("Manufacturer", S), P("Model", S), P("Name", S),
                P("NumberOfLogicalProcessors", U32), P("NumberOfProcessors", U32), P("PartOfDomain", B), P("PCSystemType", U16),
                P("PrimaryOwnerName", S), P("Status", S), P("SystemFamily", S), P("SystemType", S), P("TotalPhysicalMemory", U64),
                P("UserName", S), P("Workgroup", S),
            }, ComputerSystem),
            new WmiClass("Win32_ComputerSystemProduct", new[] { "IdentifyingNumber", "Name", "Version" }, new[]
            {
                P("Caption", S), P("Description", S), P("IdentifyingNumber", S), P("Name", S), P("SKUNumber", S), P("UUID", S),
                P("Vendor", S), P("Version", S),
            }, ComputerSystemProduct),
            new WmiClass("Win32_BaseBoard", new[] { "Tag" }, new[]
            {
                P("Caption", S), P("Description", S), P("HostingBoard", B), P("Manufacturer", S), P("Model", S), P("Name", S),
                P("PoweredOn", B), P("Product", S), P("Removable", B), P("Replaceable", B), P("SerialNumber", S), P("Status", S),
                P("Tag", S), P("Version", S),
            }, BaseBoard),
            new WmiClass("Win32_BIOS", new[] { "Name", "SoftwareElementID", "SoftwareElementState", "TargetOperatingSystem", "Version" }, new[]
            {
                A("BIOSVersion", S), P("Caption", S), P("Description", S), P("Manufacturer", S), P("Name", S), P("PrimaryBIOS", B),
                P("ReleaseDate", D), P("SerialNumber", S), P("SMBIOSBIOSVersion", S), P("SMBIOSMajorVersion", U16),
                P("SMBIOSMinorVersion", U16), P("SMBIOSPresent", B), P("SoftwareElementID", S), P("SoftwareElementState", U16),
                P("Status", S), P("TargetOperatingSystem", U16), P("Version", S),
            }, Bios),
            new WmiClass("Win32_Processor", new[] { "DeviceID" }, new[]
            {
                P("AddressWidth", U16), P("Architecture", U16), P("Caption", S), P("CurrentClockSpeed", U32), P("DataWidth", U16),
                P("Description", S), P("DeviceID", S), P("Family", U16), P("L2CacheSize", U32), P("L3CacheSize", U32), P("Level", U16),
                P("LoadPercentage", U16), P("Manufacturer", S), P("MaxClockSpeed", U32), P("Name", S), P("NumberOfCores", U32),
                P("NumberOfEnabledCore", U32), P("NumberOfLogicalProcessors", U32), P("ProcessorId", S), P("ProcessorType", U16),
                P("Revision", U16), P("Role", S), P("SocketDesignation", S), P("Status", S), P("SystemName", S), P("ThreadCount", U32),
                P("VirtualizationFirmwareEnabled", B),
            }, Processor),
            new WmiClass("Win32_OperatingSystem", new[] { "Name" }, new[]
            {
                P("BootDevice", S), P("BuildNumber", S), P("Caption", S), P("CodeSet", S), P("CountryCode", S), P("CSName", S),
                P("CurrentTimeZone", I16), P("FreePhysicalMemory", U64), P("FreeVirtualMemory", U64), P("InstallDate", D),
                P("LastBootUpTime", D), P("LocalDateTime", D), P("Locale", S), P("Manufacturer", S), A("MUILanguages", S), P("Name", S),
                P("NumberOfProcesses", U32), P("NumberOfUsers", U32), P("OSArchitecture", S), P("OSLanguage", U32), P("OSType", U16),
                P("ProductType", U32), P("RegisteredUser", S), P("SerialNumber", S), P("ServicePackMajorVersion", U16),
                P("ServicePackMinorVersion", U16), P("Status", S), P("SystemDirectory", S), P("SystemDrive", S),
                P("TotalVirtualMemorySize", U64), P("TotalVisibleMemorySize", U64), P("Version", S), P("WindowsDirectory", S),
            }, OperatingSystem),
            new WmiClass("Win32_LogicalDisk", new[] { "DeviceID" }, new[]
            {
                P("Caption", S), P("Compressed", B), P("Description", S), P("DeviceID", S), P("DriveType", U32), P("FileSystem", S),
                P("FreeSpace", U64), P("MediaType", U32), P("Name", S), P("ProviderName", S), P("Size", U64), P("VolumeName", S),
                P("VolumeSerialNumber", S),
            }, LogicalDisk),
            new WmiClass("Win32_NetworkAdapter", new[] { "DeviceID" }, new[]
            {
                P("AdapterType", S), P("AdapterTypeId", U16), P("Caption", S), P("Description", S), P("DeviceID", S), P("GUID", S),
                P("Index", U32), P("InterfaceIndex", U32), P("MACAddress", S), P("Manufacturer", S), P("Name", S),
                P("NetConnectionID", S), P("NetConnectionStatus", U16), P("NetEnabled", B), P("PhysicalAdapter", B),
                P("ProductName", S), P("ServiceName", S), P("Speed", U64),
            }, NetworkAdapter),
            new WmiClass("Win32_NetworkAdapterConfiguration", new[] { "Index" }, new[]
            {
                P("Caption", S), A("DefaultIPGateway", S), P("Description", S), P("DHCPEnabled", B), P("DHCPServer", S),
                P("DNSDomain", S), P("DNSHostName", S), A("DNSServerSearchOrder", S), P("Index", U32), P("InterfaceIndex", U32),
                A("IPAddress", S), P("IPEnabled", B), A("IPSubnet", S), P("MACAddress", S), P("ServiceName", S), P("SettingID", S),
            }, NetworkAdapterConfiguration),
            new WmiClass("Win32_VideoController", new[] { "DeviceID" }, new[]
            {
                P("AdapterCompatibility", S), P("AdapterDACType", S), P("AdapterRAM", U32), P("Availability", U16), P("Caption", S),
                P("CurrentBitsPerPixel", U32), P("CurrentHorizontalResolution", U32), P("CurrentNumberOfColors", U64),
                P("CurrentRefreshRate", U32), P("CurrentVerticalResolution", U32), P("Description", S), P("DeviceID", S),
                P("DriverDate", D), P("DriverVersion", S), P("Name", S), P("PNPDeviceID", S), P("Status", S),
                P("VideoModeDescription", S), P("VideoProcessor", S),
            }, VideoController),
            new WmiClass("Win32_PhysicalMemory", new[] { "CreationClassName", "Tag" }, new[]
            {
                P("BankLabel", S), P("Capacity", U64), P("Caption", S), P("ConfiguredClockSpeed", U32), P("CreationClassName", S),
                P("Description", S), P("DeviceLocator", S), P("Manufacturer", S), P("MemoryType", U16), P("Name", S),
                P("PartNumber", S), P("SerialNumber", S), P("SMBIOSMemoryType", U32), P("Speed", U32), P("Tag", S),
            }, PhysicalMemory),
            new WmiClass("Win32_Process", new[] { "Handle" }, new[]
            {
                P("Caption", S), P("CommandLine", S), P("CreationDate", D), P("Description", S), P("ExecutablePath", S),
                P("Handle", S), P("HandleCount", U32), P("Name", S), P("ParentProcessId", U32), P("Priority", U32),
                P("ProcessId", U32), P("SessionId", U32), P("ThreadCount", U32), P("VirtualSize", U64), P("WorkingSetSize", U64),
            }, Processes),
            // There are no Windows services on this machine.
            new WmiClass("Win32_Service", new[] { "Name" }, new[]
            {
                P("AcceptPause", B), P("AcceptStop", B), P("Caption", S), P("Description", S), P("DisplayName", S),
                P("ExitCode", U32), P("Name", S), P("PathName", S), P("ProcessId", U32), P("ServiceType", S), P("Started", B),
                P("StartMode", S), P("StartName", S), P("State", S), P("Status", S),
            }, () => Array.Empty<Dictionary<string, object>>()),
        };

        public static WmiClass Find(string name)
        {
            foreach (WmiClass c in All)
            {
                if (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return c;
            }
            return null;
        }

        private const string Apple = "Apple Inc.";

        private static bool Arm64 => RuntimeInformation.OSArchitecture == Architecture.Arm64;

        private static string MachineName => Environment.MachineName.ToUpperInvariant();

        private static ulong? MemSize => (ulong?)MacInfo.SysctlLong("hw.memsize");

        private static uint LogicalCpus => (uint)(MacInfo.SysctlLong("hw.logicalcpu") ?? Environment.ProcessorCount);

        private static string Model => MacInfo.SysctlString("hw.model");

        private static string Serial => MacInfo.PlatformProperty("IOPlatformSerialNumber");

        private static string CpuName => MacInfo.SysctlString("machdep.cpu.brand_string") ?? RuntimeInformation.ProcessArchitecture.ToString();

        private static short TimeZoneMinutes => (short)TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes;

        private static T Try<T>(Func<T> f)
        {
            try { return f(); }
            catch { return default; }
        }

        private static IEnumerable<Dictionary<string, object>> ComputerSystem()
        {
            string model = Model;
            yield return new Dictionary<string, object>
            {
                ["BootupState"] = "Normal boot",
                ["Caption"] = MachineName,
                ["CurrentTimeZone"] = TimeZoneMinutes,
                ["Description"] = "AT/AT COMPATIBLE",
                ["DNSHostName"] = Environment.MachineName,
                ["Domain"] = "WORKGROUP",
                ["DomainRole"] = 0,                                  // standalone workstation
                ["HypervisorPresent"] = MacInfo.SysctlLong("kern.hv_vmm_present") == 1,
                ["Manufacturer"] = Apple,
                ["Model"] = model,
                ["Name"] = MachineName,
                ["NumberOfLogicalProcessors"] = LogicalCpus,
                ["NumberOfProcessors"] = 1,
                ["PartOfDomain"] = false,
                ["PCSystemType"] = MacInfo.HasService("AppleSmartBattery") ? 2 : 1,   // mobile : desktop
                ["PrimaryOwnerName"] = Environment.UserName,
                ["Status"] = "OK",
                ["SystemFamily"] = model == null ? null : new string(model.TakeWhile(char.IsLetter).ToArray()),
                ["SystemType"] = Arm64 ? "ARM64-based PC" : "x64-based PC",
                ["TotalPhysicalMemory"] = MemSize,
                ["UserName"] = MachineName + @"\" + Environment.UserName,
                ["Workgroup"] = "WORKGROUP",
            };
        }

        private static IEnumerable<Dictionary<string, object>> ComputerSystemProduct()
        {
            yield return new Dictionary<string, object>
            {
                ["Caption"] = "Computer System Product",
                ["Description"] = "Computer System Product",
                ["IdentifyingNumber"] = Serial,
                ["Name"] = Model,
                ["UUID"] = MacInfo.PlatformProperty("IOPlatformUUID"),
                ["Vendor"] = Apple,
                ["Version"] = "1.0",
            };
        }

        private static IEnumerable<Dictionary<string, object>> BaseBoard()
        {
            yield return new Dictionary<string, object>
            {
                ["Caption"] = "Base Board",
                ["Description"] = "Base Board",
                ["HostingBoard"] = true,
                ["Manufacturer"] = Apple,
                ["Name"] = "Base Board",
                ["PoweredOn"] = true,
                ["Product"] = MacInfo.PlatformProperty("board-id") ?? MacInfo.PlatformProperty("target-type") ?? Model,
                ["Removable"] = false,
                ["Replaceable"] = false,
                ["SerialNumber"] = Serial,
                ["Status"] = "OK",
                ["Tag"] = "Base Board",
            };
        }

        private static IEnumerable<Dictionary<string, object>> Bios()
        {
            string firmware = MacInfo.RegistryProperty("IODeviceTree:/chosen", "system-firmware-version")
                ?? MacInfo.RegistryProperty("IODeviceTree:/rom", "version");
            string name = firmware ?? "Default System BIOS";
            yield return new Dictionary<string, object>
            {
                ["BIOSVersion"] = new[] { "APPLE  - 1", name },
                ["Caption"] = name,
                ["Description"] = name,
                ["Manufacturer"] = Apple,
                ["Name"] = name,
                ["PrimaryBIOS"] = true,
                ["SerialNumber"] = Serial,
                ["SMBIOSBIOSVersion"] = firmware,
                ["SMBIOSPresent"] = true,
                ["SoftwareElementID"] = name,
                ["SoftwareElementState"] = 3,                         // running
                ["Status"] = "OK",
                ["TargetOperatingSystem"] = 0,
                ["Version"] = "APPLE  - 1",
            };
        }

        private static IEnumerable<Dictionary<string, object>> Processor()
        {
            uint cores = (uint)(MacInfo.SysctlLong("hw.physicalcpu") ?? Environment.ProcessorCount);
            long? hz = MacInfo.SysctlLong("hw.cpufrequency_max");
            uint mhz = hz is long h ? (uint)(h / 1_000_000) : 0;
            long l2 = MacInfo.SysctlLong("hw.l2cachesize") ?? MacInfo.SysctlLong("hw.perflevel0.l2cachesize") ?? 0;
            long l3 = MacInfo.SysctlLong("hw.l3cachesize") ?? 0;
            // There is no CPUID on Apple silicon. ProcessorId identifies the processor model, as
            // Windows' does (feature flags and signature): CPU type and family.
            long cpuType = MacInfo.SysctlLong("hw.cputype") ?? 0, cpuFamily = MacInfo.SysctlLong("hw.cpufamily") ?? 0;
            string name = CpuName;
            yield return new Dictionary<string, object>
            {
                ["AddressWidth"] = 64,
                ["Architecture"] = Arm64 ? 12 : 9,
                ["Caption"] = name,
                ["CurrentClockSpeed"] = mhz,
                ["DataWidth"] = 64,
                ["Description"] = name,
                ["DeviceID"] = "CPU0",
                ["Family"] = Arm64 ? 280 : 198,                        // ARMv8 : Intel Core i7
                ["L2CacheSize"] = (uint)(l2 / 1024),
                ["L3CacheSize"] = (uint)(l3 / 1024),
                ["Level"] = 0,
                ["Manufacturer"] = Arm64 ? Apple : MacInfo.SysctlString("machdep.cpu.vendor"),
                ["MaxClockSpeed"] = mhz,
                ["Name"] = name,
                ["NumberOfCores"] = cores,
                ["NumberOfEnabledCore"] = cores,
                ["NumberOfLogicalProcessors"] = LogicalCpus,
                ["ProcessorId"] = ((uint)cpuType).ToString("X8", CultureInfo.InvariantCulture) + ((uint)cpuFamily).ToString("X8", CultureInfo.InvariantCulture),
                ["ProcessorType"] = 3,                                 // central processor
                ["Role"] = "CPU",
                ["SocketDesignation"] = "CPU0",
                ["Status"] = "OK",
                ["SystemName"] = MachineName,
                ["ThreadCount"] = LogicalCpus,
                ["VirtualizationFirmwareEnabled"] = true,
            };
        }

        private static IEnumerable<Dictionary<string, object>> OperatingSystem()
        {
            string product = MacInfo.SysctlString("kern.osproductversion") ?? Environment.OSVersion.Version.ToString();
            string caption = (System.OperatingSystem.IsMacOS() ? "macOS " : RuntimeInformation.OSDescription + " ") + product;
            ulong pageSize = (ulong)(MacInfo.SysctlLong("hw.pagesize") ?? Environment.SystemPageSize);
            ulong? free = MacInfo.SysctlLong("vm.page_free_count") is long f ? (ulong)f * pageSize / 1024 : null;
            ulong? total = MemSize / 1024;
            byte[] swap = MacInfo.SysctlBytes("vm.swapusage");         // xsw_usage: total, avail, used (bytes)
            ulong swapTotal = swap != null && swap.Length >= 24 ? BitConverter.ToUInt64(swap, 0) / 1024 : 0;
            ulong swapFree = swap != null && swap.Length >= 24 ? BitConverter.ToUInt64(swap, 8) / 1024 : 0;
            CultureInfo ui = CultureInfo.CurrentUICulture;
            int lcid = ui.LCID == 4096 ? 1033 : ui.LCID;
            yield return new Dictionary<string, object>
            {
                ["BootDevice"] = "/",
                ["BuildNumber"] = MacInfo.SysctlString("kern.osversion") ?? Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture),
                ["Caption"] = caption,
                ["CodeSet"] = CultureInfo.CurrentCulture.TextInfo.ANSICodePage.ToString(CultureInfo.InvariantCulture),
                ["CSName"] = MachineName,
                ["CurrentTimeZone"] = TimeZoneMinutes,
                ["FreePhysicalMemory"] = free,
                ["FreeVirtualMemory"] = free + swapFree,
                ["LastBootUpTime"] = MacInfo.BootTime(),
                ["LocalDateTime"] = DateTime.Now,
                ["Locale"] = lcid.ToString("X4", CultureInfo.InvariantCulture),
                ["Manufacturer"] = Apple,
                ["MUILanguages"] = new[] { ui.Name.Length > 0 ? ui.Name : "en-US" },
                ["Name"] = caption + "|/System|/",
                ["NumberOfProcesses"] = (uint)Try(() => Process.GetProcesses().Length),
                ["NumberOfUsers"] = 1,
                ["OSArchitecture"] = Arm64 ? "ARM 64-bit Processor" : "64-bit",
                ["OSLanguage"] = (uint)lcid,
                ["OSType"] = 18,                                       // WINNT
                ["ProductType"] = 1,                                   // workstation
                ["RegisteredUser"] = Environment.UserName,
                ["ServicePackMajorVersion"] = 0,
                ["ServicePackMinorVersion"] = 0,
                ["Status"] = "OK",
                ["SystemDirectory"] = "/System",
                ["SystemDrive"] = "/",
                ["TotalVirtualMemorySize"] = total + swapTotal,
                ["TotalVisibleMemorySize"] = total,
                ["Version"] = Environment.OSVersion.Version.ToString(),
                ["WindowsDirectory"] = "/System",
            };
        }

        private static IEnumerable<Dictionary<string, object>> LogicalDisk()
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                string root = drive.RootDirectory.FullName;
                // The volumes a user sees: the startup disk and what is mounted under /Volumes.
                if (root != "/" && !root.StartsWith("/Volumes/", StringComparison.Ordinal)) continue;
                if (!Try(() => drive.IsReady)) continue;
                uint type = drive.DriveType switch
                {
                    DriveType.Removable => 2u,
                    DriveType.Fixed => 3u,
                    DriveType.Network => 4u,
                    DriveType.CDRom => 5u,
                    DriveType.Ram => 6u,
                    _ => 3u,
                };
                string id = root.Length > 1 ? root.TrimEnd('/') : root;
                yield return new Dictionary<string, object>
                {
                    ["Caption"] = id,
                    ["Compressed"] = false,
                    ["Description"] = type switch { 2 => "Removable Disk", 4 => "Network Connection", 5 => "CD-ROM Disc", _ => "Local Fixed Disk" },
                    ["DeviceID"] = id,
                    ["DriveType"] = type,
                    ["FileSystem"] = Try(() => drive.DriveFormat),
                    ["FreeSpace"] = Try(() => (ulong?)drive.AvailableFreeSpace),
                    ["MediaType"] = type == 3 ? 12u : (uint?)null,
                    ["Name"] = id,
                    ["Size"] = Try(() => (ulong?)drive.TotalSize),
                    ["VolumeName"] = id == "/" ? StartupVolumeName() : Path.GetFileName(id),
                };
            }
        }

        // /Volumes has a link to "/" named after the startup disk ("Macintosh HD").
        private static string StartupVolumeName()
        {
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries("/Volumes"))
                {
                    if (new FileInfo(entry).LinkTarget == "/") return Path.GetFileName(entry);
                }
            }
            catch
            {
            }
            return null;
        }

        private static List<NetworkInterface> Adapters() =>
            Try(() => NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToList())
            ?? new List<NetworkInterface>();

        private static string Mac(NetworkInterface n, Dictionary<string, (byte[] Mac, bool Builtin)> ethernet)
        {
            byte[] b = ethernet.TryGetValue(n.Name, out var e) && e.Mac != null ? e.Mac : Try(() => n.GetPhysicalAddress().GetAddressBytes());
            // 02:00:00:00:00:00 is the placeholder the system hands out instead of an address.
            if (b == null || b.Length != 6 || b.All(x => x == 0) || (b[0] == 2 && b.Skip(1).All(x => x == 0))) return null;
            return string.Join(":", b.Select(x => x.ToString("X2", CultureInfo.InvariantCulture)));
        }

        private static uint InterfaceIndex(NetworkInterface n, int fallback) =>
            (uint)(Try(() => n.GetIPProperties().GetIPv4Properties()?.Index) ?? Try(() => n.GetIPProperties().GetIPv6Properties()?.Index) ?? fallback);

        private static IEnumerable<Dictionary<string, object>> NetworkAdapter()
        {
            List<NetworkInterface> adapters = Adapters();
            Dictionary<string, (byte[] Mac, bool Builtin)> ethernet = MacInfo.EthernetInterfaces();
            for (int i = 0; i < adapters.Count; i++)
            {
                NetworkInterface n = adapters[i];
                string mac = Mac(n, ethernet);
                bool up = n.OperationalStatus == OperationalStatus.Up;
                // A network card: an Ethernet or Wi-Fi port the hardware has (en*), not a bridge, tunnel
                // or virtual interface.
                bool physical = mac != null && ethernet.ContainsKey(n.Name) && n.Name.StartsWith("en", StringComparison.Ordinal);
                long speed = Try(() => n.Speed);
                yield return new Dictionary<string, object>
                {
                    ["AdapterType"] = physical ? "Ethernet 802.3" : null,
                    ["AdapterTypeId"] = physical ? 0 : (ushort?)null,
                    ["Caption"] = "[" + i.ToString("D8", CultureInfo.InvariantCulture) + "] " + n.Description,
                    ["Description"] = n.Description,
                    ["DeviceID"] = i.ToString(CultureInfo.InvariantCulture),
                    ["GUID"] = n.Id,
                    ["Index"] = (uint)i,
                    ["InterfaceIndex"] = InterfaceIndex(n, i),
                    ["MACAddress"] = mac,
                    ["Name"] = n.Description,
                    ["NetConnectionID"] = n.Name,
                    ["NetConnectionStatus"] = up ? 2 : 7,               // connected : media disconnected
                    ["NetEnabled"] = up,
                    ["PhysicalAdapter"] = physical,
                    ["ProductName"] = n.Description,
                    ["ServiceName"] = n.Name,
                    ["Speed"] = speed > 0 ? (ulong?)speed : null,
                };
            }
        }

        private static IEnumerable<Dictionary<string, object>> NetworkAdapterConfiguration()
        {
            List<NetworkInterface> adapters = Adapters();
            Dictionary<string, (byte[] Mac, bool Builtin)> ethernet = MacInfo.EthernetInterfaces();
            for (int i = 0; i < adapters.Count; i++)
            {
                NetworkInterface n = adapters[i];
                IPInterfaceProperties ip = Try(() => n.GetIPProperties());
                var unicast = ip?.UnicastAddresses.OrderBy(a => a.Address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToList() ?? new List<UnicastIPAddressInformation>();
                bool enabled = unicast.Count > 0 && n.OperationalStatus == OperationalStatus.Up;
                string[] gateways = Try(() => ip?.GatewayAddresses.Select(g => g.Address.ToString()).ToArray());
                string[] dns = Try(() => ip?.DnsAddresses.Select(a => a.ToString()).ToArray());
                yield return new Dictionary<string, object>
                {
                    ["Caption"] = "[" + i.ToString("D8", CultureInfo.InvariantCulture) + "] " + n.Description,
                    ["DefaultIPGateway"] = enabled && gateways is { Length: > 0 } ? gateways : null,
                    ["Description"] = n.Description,
                    ["DHCPEnabled"] = Try(() => ip?.GetIPv4Properties()?.IsDhcpEnabled ?? false),
                    ["DNSDomain"] = Try(() => ip?.DnsSuffix is { Length: > 0 } s ? s : null),
                    ["DNSHostName"] = Environment.MachineName,
                    ["DNSServerSearchOrder"] = enabled && dns is { Length: > 0 } ? dns : null,
                    ["Index"] = (uint)i,
                    ["InterfaceIndex"] = InterfaceIndex(n, i),
                    ["IPAddress"] = enabled ? unicast.Select(a => a.Address.ToString()).ToArray() : null,
                    ["IPEnabled"] = enabled,
                    ["IPSubnet"] = enabled ? unicast.Select(a => a.Address.AddressFamily == AddressFamily.InterNetwork
                        ? a.IPv4Mask.ToString() : a.PrefixLength.ToString(CultureInfo.InvariantCulture)).ToArray() : null,
                    ["MACAddress"] = Mac(n, ethernet),
                    ["ServiceName"] = n.Name,
                    ["SettingID"] = n.Id,
                };
            }
        }

        private static IEnumerable<Dictionary<string, object>> VideoController()
        {
            (uint Width, uint Height, uint RefreshRate)? display = MacInfo.MainDisplay();
            // On Apple silicon the GPU is part of the chip and shares its memory.
            string name = Arm64 ? CpuName : "Display Adapter";
            ulong? memory = MemSize;
            yield return new Dictionary<string, object>
            {
                ["AdapterCompatibility"] = Apple,
                ["AdapterDACType"] = "Integrated RAMDAC",
                ["AdapterRAM"] = memory is ulong m ? (uint)Math.Min(m, uint.MaxValue) : null,
                ["Availability"] = 3,                                  // running, full power
                ["Caption"] = name,
                ["CurrentBitsPerPixel"] = 32,
                ["CurrentHorizontalResolution"] = display?.Width,
                ["CurrentNumberOfColors"] = 4294967296UL,
                ["CurrentRefreshRate"] = display?.RefreshRate,
                ["CurrentVerticalResolution"] = display?.Height,
                ["Description"] = name,
                ["DeviceID"] = "VideoController1",
                ["DriverVersion"] = MacInfo.SysctlString("kern.osversion"),
                ["Name"] = name,
                ["Status"] = "OK",
                ["VideoModeDescription"] = display is { } d ? $"{d.Width} x {d.Height} x 4294967296 colors" : null,
                ["VideoProcessor"] = name,
            };
        }

        private static IEnumerable<Dictionary<string, object>> PhysicalMemory()
        {
            yield return new Dictionary<string, object>
            {
                ["BankLabel"] = "BANK 0",
                ["Capacity"] = MemSize,
                ["Caption"] = "Physical Memory",
                ["CreationClassName"] = "Win32_PhysicalMemory",
                ["Description"] = "Physical Memory",
                ["DeviceLocator"] = "Unified Memory",
                ["MemoryType"] = 0,
                ["Name"] = "Physical Memory",
                ["SMBIOSMemoryType"] = 0u,
                ["Tag"] = "Physical Memory 0",
            };
        }

        private static IEnumerable<Dictionary<string, object>> Processes()
        {
            int self = Environment.ProcessId;
            foreach (Process p in Try(Process.GetProcesses) ?? Array.Empty<Process>())
            {
                using (p)
                {
                    int id = Try(() => p.Id);
                    string name = Try(() => p.ProcessName);
                    if (name == null) continue;
                    yield return new Dictionary<string, object>
                    {
                        ["Caption"] = name,
                        ["CommandLine"] = id == self ? Environment.CommandLine : null,
                        ["CreationDate"] = Try(() => (DateTime?)p.StartTime),
                        ["Description"] = name,
                        ["ExecutablePath"] = id == self ? Environment.ProcessPath : null,
                        ["Handle"] = id.ToString(CultureInfo.InvariantCulture),
                        ["HandleCount"] = (uint)Try(() => p.HandleCount),
                        ["Name"] = name,
                        ["Priority"] = (uint)Try(() => p.BasePriority),
                        ["ProcessId"] = (uint)id,
                        ["SessionId"] = (uint)Try(() => p.SessionId),
                        ["ThreadCount"] = (uint)Try(() => p.Threads.Count),
                        ["VirtualSize"] = (ulong)Try(() => p.VirtualMemorySize64),
                        ["WorkingSetSize"] = (ulong)Try(() => p.WorkingSet64),
                    };
                }
            }
        }
    }
}
