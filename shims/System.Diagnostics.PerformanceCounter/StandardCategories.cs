// More of the standard categories, with this machine's live values: PhysicalDisk (each disk's bytes and operations,
// read and written, and its busy time), LogicalDisk (each volume's free space), Network Interface (each adapter's bytes
// and packets, sent and received, and its bandwidth) and Paging File (how much of the swap is in use). The counters are
// cumulative as Windows keeps them; the rates (/sec) come of two samples, as Windows computes them.
//
// Disks: /proc/diskstats on Linux, the block storage drivers' statistics (IOKit) on macOS, IOCTL_DISK_PERFORMANCE on
// Windows. Adapters: the system's interface statistics (System.Net.NetworkInformation), named as Windows names them
// (its description, with ( ) # \ / made [ ] _ _ _). Volumes: as Win32_LogicalDisk lists them.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace System.Diagnostics
{
    internal static unsafe class StandardCategories
    {
        private static CounterSample Sample(long raw, PerformanceCounterType type, long baseValue = 0) =>
            new CounterSample(raw, baseValue, Stopwatch.Frequency, Stopwatch.Frequency, Stopwatch.GetTimestamp(),
                DateTime.UtcNow.ToFileTimeUtc(), type, 0);

        private static InvalidOperationException NoInstance(string instance) => new InvalidOperationException($"Instance '{instance}' does not exist in the specified Category.");

        public static SystemCategory[] All => new[]
        {
            new SystemCategory("PhysicalDisk", "The Physical Disk performance object consists of counters that monitor hard or fixed disk drive on a computer.",
                PerformanceCounterCategoryType.MultiInstance, new[]
                {
                    ("% Disk Time", "% Disk Time is the percentage of elapsed time that the selected disk drive was busy servicing read or write requests.", PerformanceCounterType.Timer100Ns),
                    ("Disk Read Bytes/sec", "Disk Read Bytes/sec is the rate at which bytes are transferred from the disk during read operations.", PerformanceCounterType.RateOfCountsPerSecond64),
                    ("Disk Write Bytes/sec", "Disk Write Bytes/sec is rate at which bytes are transferred to the disk during write operations.", PerformanceCounterType.RateOfCountsPerSecond64),
                    ("Disk Bytes/sec", "Disk Bytes/sec is the rate bytes are transferred to or from the disk during write or read operations.", PerformanceCounterType.RateOfCountsPerSecond64),
                    ("Disk Reads/sec", "Disk Reads/sec is the rate of read operations on the disk.", PerformanceCounterType.RateOfCountsPerSecond32),
                    ("Disk Writes/sec", "Disk Writes/sec is the rate of write operations on the disk.", PerformanceCounterType.RateOfCountsPerSecond32),
                    ("Disk Transfers/sec", "Disk Transfers/sec is the rate of read and write operations on the disk.", PerformanceCounterType.RateOfCountsPerSecond32),
                    ("Current Disk Queue Length", "Current Disk Queue Length is the number of requests outstanding on the disk at the time the performance data is collected.", PerformanceCounterType.NumberOfItems32),
                },
                () => DiskInstances(), ReadDisk),
            new SystemCategory("LogicalDisk", "The Logical Disk performance object consists of counters that monitor logical partitions of a hard or fixed disk drives.",
                PerformanceCounterCategoryType.MultiInstance, new[]
                {
                    ("% Free Space", "% Free Space is the percentage of total usable space on the selected logical disk drive that was free.", PerformanceCounterType.RawFraction),
                    ("% Free Space Base", "", PerformanceCounterType.RawBase),
                    ("Free Megabytes", "Free Megabytes displays the unallocated space, in megabytes, on the disk drive in megabytes.", PerformanceCounterType.NumberOfItems32),
                },
                () => Volumes().Select(v => v.Name).Append("_Total").ToArray(), ReadVolume),
            new SystemCategory("Network Interface", "The Network Interface performance object consists of counters that measure the rates at which bytes and packets are sent and received over a network connection.",
                PerformanceCounterCategoryType.MultiInstance, new[]
                {
                    ("Bytes Total/sec", "Bytes Total/sec is the rate at which bytes are sent and received over each network adapter.", PerformanceCounterType.RateOfCountsPerSecond64),
                    ("Bytes Received/sec", "Bytes Received/sec is the rate at which bytes are received over each network adapter.", PerformanceCounterType.RateOfCountsPerSecond64),
                    ("Bytes Sent/sec", "Bytes Sent/sec is the rate at which bytes are sent on each network adapter.", PerformanceCounterType.RateOfCountsPerSecond64),
                    ("Packets/sec", "Packets/sec is the rate at which packets are sent and received on the network interface.", PerformanceCounterType.RateOfCountsPerSecond64),
                    ("Packets Received/sec", "Packets Received/sec is the rate at which packets are received on the network interface.", PerformanceCounterType.RateOfCountsPerSecond64),
                    ("Packets Sent/sec", "Packets Sent/sec is the rate at which packets are sent on the network interface.", PerformanceCounterType.RateOfCountsPerSecond64),
                    ("Current Bandwidth", "Current Bandwidth is an estimate of the current bandwidth of the network interface in bits per second (BPS).", PerformanceCounterType.NumberOfItems64),
                },
                () => Adapters().Select(a => a.Name).ToArray(), ReadAdapter),
            new SystemCategory("Paging File", "The Paging File performance object consists of counters that monitor the paging file(s) on the computer.",
                PerformanceCounterCategoryType.MultiInstance, new[]
                {
                    ("% Usage", "The amount of the Page File instance in use in percent.", PerformanceCounterType.RawFraction),
                    ("% Usage Base", "", PerformanceCounterType.RawBase),
                },
                () => new[] { "_Total" }, ReadPaging),
        };

        // ---- disks -------------------------------------------------------------------------------------------------

        /// <summary>A disk's cumulative counts: bytes read and written, reads and writes, busy time (100 ns), queued.</summary>
        private readonly record struct DiskStats(long ReadBytes, long WriteBytes, long Reads, long Writes, long BusyTicks, long Queue);

        private static string[] DiskInstances()
        {
            int n = Disks().Count;
            return Enumerable.Range(0, n).Select(i => i.ToString(Globalization.CultureInfo.InvariantCulture)).Append("_Total").ToArray();
        }

        private static CounterSample ReadDisk(string counter, string instance)
        {
            List<DiskStats> disks = Disks();
            DiskStats d;
            if (instance == "_Total")
                d = disks.Aggregate(new DiskStats(), (a, b) => new DiskStats(a.ReadBytes + b.ReadBytes, a.WriteBytes + b.WriteBytes, a.Reads + b.Reads, a.Writes + b.Writes, a.BusyTicks + b.BusyTicks, a.Queue + b.Queue));
            else if (int.TryParse(instance.Split(' ')[0], out int i) && i >= 0 && i < disks.Count) d = disks[i];
            else throw NoInstance(instance);
            var type = SystemCategory.TypeOf("PhysicalDisk", counter);
            return counter switch
            {
                "% Disk Time" => Sample(d.BusyTicks, type),
                "Disk Read Bytes/sec" => Sample(d.ReadBytes, type),
                "Disk Write Bytes/sec" => Sample(d.WriteBytes, type),
                "Disk Bytes/sec" => Sample(d.ReadBytes + d.WriteBytes, type),
                "Disk Reads/sec" => Sample(d.Reads, type),
                "Disk Writes/sec" => Sample(d.Writes, type),
                "Disk Transfers/sec" => Sample(d.Reads + d.Writes, type),
                _ => Sample(d.Queue, type),
            };
        }

        private static List<DiskStats> Disks()
        {
            try
            {
                if (OperatingSystem.IsLinux()) return LinuxDisks();
                if (OperatingSystem.IsMacOS()) return MacDisks();
                if (OperatingSystem.IsWindows()) return WindowsDisks();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { }
            return new List<DiskStats>();
        }

        // /proc/diskstats: major minor name reads merged sectors ms writes merged sectors ms in-flight io-ms ...; whole
        // disks only (those /sys/block has with a device)
        private static List<DiskStats> LinuxDisks()
        {
            var result = new List<DiskStats>();
            foreach (string line in File.ReadAllLines("/proc/diskstats"))
            {
                string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 14) continue;
                string name = f[2];
                if (!Directory.Exists(Path.Combine("/sys/block", name, "device")) || name.StartsWith("loop", StringComparison.Ordinal) || name.StartsWith("sr", StringComparison.Ordinal)) continue;
                long L(int i) => long.TryParse(f[i], out long v) ? v : 0;
                result.Add(new DiskStats(L(5) * 512, L(9) * 512, L(3), L(7), L(12) * TimeSpan.TicksPerMillisecond, L(11)));
            }
            return result;
        }

        private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        [DllImport(IOKit)] private static extern IntPtr IOServiceMatching(string name);
        [DllImport(IOKit)] private static extern int IOServiceGetMatchingServices(uint mainPort, IntPtr matching, out uint iterator);
        [DllImport(IOKit)] private static extern uint IOIteratorNext(uint iterator);
        [DllImport(IOKit)] private static extern int IOObjectRelease(uint obj);
        [DllImport(IOKit)] private static extern IntPtr IORegistryEntryCreateCFProperty(uint entry, IntPtr key, IntPtr allocator, uint options);
        [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);
        [DllImport(CF)] private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);
        [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, int type, out long value);
        [DllImport(CF)] private static extern void CFRelease(IntPtr obj);

        private static IntPtr Key(string s) => CFStringCreateWithCString(IntPtr.Zero, s, 0x08000100);

        // each block storage driver's Statistics: bytes, operations and total time (ns), read and written
        private static List<DiskStats> MacDisks()
        {
            var result = new List<DiskStats>();
            if (IOServiceGetMatchingServices(0, IOServiceMatching("IOBlockStorageDriver"), out uint it) != 0) return result;
            IntPtr statsKey = Key("Statistics");
            var keys = new[] { "Bytes (Read)", "Bytes (Write)", "Operations (Read)", "Operations (Write)", "Total Time (Read)", "Total Time (Write)" }.Select(Key).ToArray();
            try
            {
                for (uint e; (e = IOIteratorNext(it)) != 0; IOObjectRelease(e))
                {
                    IntPtr stats = IORegistryEntryCreateCFProperty(e, statsKey, IntPtr.Zero, 0);
                    if (stats == IntPtr.Zero) continue;
                    try
                    {
                        long V(int i) { IntPtr n = CFDictionaryGetValue(stats, keys[i]); return n != IntPtr.Zero && CFNumberGetValue(n, 4, out long v) ? v : 0; }   // kCFNumberSInt64Type
                        result.Add(new DiskStats(V(0), V(1), V(2), V(3), (V(4) + V(5)) / 100, 0));
                    }
                    finally { CFRelease(stats); }
                }
            }
            finally
            {
                IOObjectRelease(it);
                CFRelease(statsKey);
                foreach (IntPtr k in keys) CFRelease(k);
            }
            return result;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int DeviceIoControl(IntPtr device, uint code, void* input, int inputSize, void* output, int outputSize, out int returned, IntPtr overlapped);
        [DllImport("kernel32.dll")]
        private static extern int CloseHandle(IntPtr handle);

        // DISK_PERFORMANCE: BytesRead, BytesWritten, ReadTime, WriteTime, IdleTime (100 ns), ReadCount, WriteCount, QueueDepth, ...
        private static List<DiskStats> WindowsDisks()
        {
            var result = new List<DiskStats>();
            byte* perf = stackalloc byte[88];
            for (int i = 0; i < 32; i++)
            {
                IntPtr h = CreateFileW(@"\\.\PhysicalDrive" + i.ToString(Globalization.CultureInfo.InvariantCulture), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h == new IntPtr(-1)) break;
                try
                {
                    if (DeviceIoControl(h, 0x70020, null, 0, perf, 88, out _, IntPtr.Zero) == 0) continue;     // IOCTL_DISK_PERFORMANCE
                    long* l = (long*)perf;
                    int* counts = (int*)(perf + 40);
                    result.Add(new DiskStats(l[0], l[1], counts[0], counts[1], l[2] + l[3], counts[2]));
                }
                finally { CloseHandle(h); }
            }
            return result;
        }

        // ---- volumes -----------------------------------------------------------------------------------------------

        private static List<(string Name, DriveInfo Drive)> Volumes()
        {
            var result = new List<(string, DriveInfo)>();
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                    string root = d.RootDirectory.FullName;
                    if (!OperatingSystem.IsWindows() && root != "/" && !root.StartsWith("/Volumes/", StringComparison.Ordinal) && !root.StartsWith("/mnt/", StringComparison.Ordinal) && !root.StartsWith("/media/", StringComparison.Ordinal)) continue;
                    result.Add((OperatingSystem.IsWindows() ? root.TrimEnd('\\') : root.Length > 1 ? root.TrimEnd('/') : root, d));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return result;
        }

        private static CounterSample ReadVolume(string counter, string instance)
        {
            var volumes = Volumes();
            var chosen = instance == "_Total" ? volumes : volumes.Where(v => string.Equals(v.Name, instance, StringComparison.OrdinalIgnoreCase)).ToList();
            if (chosen.Count == 0 && instance != "_Total") throw NoInstance(instance);
            long free = chosen.Sum(v => v.Drive.TotalFreeSpace), total = chosen.Sum(v => v.Drive.TotalSize);
            return counter switch
            {
                "% Free Space" => Sample(free / (1024 * 1024), PerformanceCounterType.RawFraction, total / (1024 * 1024)),
                "% Free Space Base" => Sample(total / (1024 * 1024), PerformanceCounterType.RawBase),
                _ => Sample(free / (1024 * 1024), PerformanceCounterType.NumberOfItems32),
            };
        }

        // ---- adapters ----------------------------------------------------------------------------------------------

        private static List<(string Name, NetworkInterface Adapter)> Adapters()
        {
            var result = new List<(string, NetworkInterface)>();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (NetworkInterface a in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (a.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                string name = new string(a.Description.Select(c => c switch { '(' => '[', ')' => ']', '#' or '\\' or '/' => '_', _ => c }).ToArray());
                seen.TryGetValue(name, out int n);
                seen[name] = n + 1;
                result.Add((n == 0 ? name : name + " _" + (n + 1).ToString(Globalization.CultureInfo.InvariantCulture), a));
            }
            return result;
        }

        private static CounterSample ReadAdapter(string counter, string instance)
        {
            var match = Adapters().FirstOrDefault(a => string.Equals(a.Name, instance, StringComparison.OrdinalIgnoreCase));
            if (match.Adapter == null) throw NoInstance(instance);
            IPInterfaceStatistics s = match.Adapter.GetIPStatistics();
            var type = SystemCategory.TypeOf("Network Interface", counter);
            long Safe(Func<long> f) { try { return f(); } catch (PlatformNotSupportedException) { return 0; } }
            long received = Safe(() => s.BytesReceived), sent = Safe(() => s.BytesSent);
            long packetsIn = Safe(() => s.UnicastPacketsReceived + s.NonUnicastPacketsReceived), packetsOut = Safe(() => s.UnicastPacketsSent + s.NonUnicastPacketsSent);
            return counter switch
            {
                "Bytes Total/sec" => Sample(received + sent, type),
                "Bytes Received/sec" => Sample(received, type),
                "Bytes Sent/sec" => Sample(sent, type),
                "Packets/sec" => Sample(packetsIn + packetsOut, type),
                "Packets Received/sec" => Sample(packetsIn, type),
                "Packets Sent/sec" => Sample(packetsOut, type),
                _ => Sample(Safe(() => Math.Max(0, match.Adapter.Speed)), type),
            };
        }

        // ---- paging file -------------------------------------------------------------------------------------------

        [DllImport("/usr/lib/libSystem.B.dylib")]
        private static extern int sysctlbyname(string name, void* oldp, nint* oldlenp, void* newp, nint newlen);

        private static (long Used, long Total) Swap()
        {
            if (OperatingSystem.IsLinux())
            {
                long total = 0, free = 0;
                foreach (string line in File.ReadAllLines("/proc/meminfo"))
                {
                    string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 2) continue;
                    if (f[0] == "SwapTotal:") total = long.Parse(f[1], Globalization.CultureInfo.InvariantCulture) * 1024;
                    if (f[0] == "SwapFree:") free = long.Parse(f[1], Globalization.CultureInfo.InvariantCulture) * 1024;
                }
                return (total - free, total);
            }
            if (OperatingSystem.IsMacOS())
            {
                // xsw_usage: total, avail, used (uint64 each), page size, encrypted
                long* usage = stackalloc long[5];
                nint len = 5 * sizeof(long);
                if (sysctlbyname("vm.swapusage", usage, &len, null, 0) == 0) return (usage[2], usage[0]);
            }
            return (0, 0);
        }

        private static CounterSample ReadPaging(string counter, string instance)
        {
            if (instance != "_Total") throw NoInstance(instance);
            var (used, total) = Swap();
            return counter == "% Usage Base" ? Sample(total, PerformanceCounterType.RawBase) : Sample(used, PerformanceCounterType.RawFraction, total);
        }
    }
}
