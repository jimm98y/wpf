// CPU and memory readings: the Mach host interface on macOS, /proc on Linux, and the Win32
// memory-status and processor-time queries on Windows. The counters only use ratios of the CPU
// ticks, so each platform keeps its own unit.

using System.Runtime.InteropServices;

namespace System.Diagnostics
{
    internal static unsafe class Mach
    {
        private const string LibSystem = "/usr/lib/libSystem.B.dylib";
        private const int HOST_CPU_LOAD_INFO = 3, HOST_VM_INFO64 = 4, PROCESSOR_CPU_LOAD_INFO = 2;
        private const int CPU_STATE_MAX = 4;

        [DllImport(LibSystem)]
        private static extern uint mach_host_self();

        [DllImport(LibSystem)]
        private static extern int host_statistics(uint host, int flavor, int* info, ref uint count);

        [DllImport(LibSystem)]
        private static extern int host_statistics64(uint host, int flavor, int* info, ref uint count);

        [DllImport(LibSystem)]
        private static extern int host_processor_info(uint host, int flavor, out uint processorCount, out int* info, out uint infoCount);

        [DllImport(LibSystem)]
        private static extern int vm_deallocate(uint task, nuint address, nuint size);

        [DllImport(LibSystem)]
        private static extern int sysctlbyname(string name, void* oldp, nint* oldlenp, void* newp, nint newlen);

        private static readonly Lazy<uint> s_host = new Lazy<uint>(() => OperatingSystem.IsMacOS() ? mach_host_self() : 0);

        private static readonly Lazy<uint> s_task = new Lazy<uint>(() =>
        {
            // mach_task_self() is a macro over this variable.
            IntPtr lib = NativeLibrary.Load(LibSystem);
            return NativeLibrary.TryGetExport(lib, "mach_task_self_", out IntPtr p) ? *(uint*)p : 0;
        });

        public static int CpuCount => Environment.ProcessorCount;

        // ---- Windows ----

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32", SetLastError = true)]
        private static extern int GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

        [DllImport("kernel32")]
        private static extern int GetSystemTimes(out long idle, out long kernel, out long user);

        [DllImport("ntdll")]
        private static extern int NtQuerySystemInformation(int infoClass, void* info, int length, out int returned);

        /// <summary>Windows: user, system (kernel less idle), idle, 0 -- in 100 ns.</summary>
        private static long[] WindowsTicks(long idle, long kernel, long user) => new[] { user, kernel - idle, idle, 0L };

        /// <summary>Windows' per-processor times (SystemProcessorPerformanceInformation: idle, kernel,
        /// user, dpc, interrupt as 100 ns, then a count; 48 bytes each).</summary>
        private static long[] WindowsCpuTicks(int cpu)
        {
            int n = Environment.ProcessorCount;
            if (cpu >= n) return null;
            var buf = new long[n * 6];
            fixed (long* b = buf)
            {
                if (NtQuerySystemInformation(8, b, buf.Length * 8, out _) != 0) return null;
                long* p = b + cpu * 6;
                return WindowsTicks(p[0], p[1], p[2]);
            }
        }

        // ---- Linux ----

        /// <summary>/proc/stat's line for "cpu" or "cpuN": user nice system idle (in USER_HZ).</summary>
        private static long[] LinuxCpuTicks(string name)
        {
            foreach (string line in IO.File.ReadLines("/proc/stat"))
            {
                string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 5 || f[0] != name) continue;
                long P(int i) => long.Parse(f[i], Globalization.CultureInfo.InvariantCulture);
                return new[] { P(1), P(3), P(4), P(2) };
            }
            return null;
        }

        private static long LinuxMeminfo(string key)
        {
            foreach (string line in IO.File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith(key + ":", StringComparison.Ordinal)) continue;
                string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return long.Parse(f[1], Globalization.CultureInfo.InvariantCulture) * 1024;   // kB
            }
            return 0;
        }

        /// <summary>Clock ticks spent in user, system, idle and nice, all processors together.</summary>
        public static long[] TotalCpuTicks()
        {
            if (OperatingSystem.IsWindows()) return GetSystemTimes(out long i, out long k, out long u) != 0 ? WindowsTicks(i, k, u) : new long[4];
            if (OperatingSystem.IsLinux()) return LinuxCpuTicks("cpu") ?? new long[4];
            if (s_host.Value == 0) return new long[4];
            int* info = stackalloc int[CPU_STATE_MAX];
            uint count = CPU_STATE_MAX;
            if (host_statistics(s_host.Value, HOST_CPU_LOAD_INFO, info, ref count) != 0) return new long[4];
            return new long[] { (uint)info[0], (uint)info[1], (uint)info[2], (uint)info[3] };
        }

        /// <summary>The same for one processor, or null when there is no such processor.</summary>
        public static long[] CpuTicks(int cpu)
        {
            if (cpu < 0) return null;
            if (OperatingSystem.IsWindows()) return WindowsCpuTicks(cpu);
            if (OperatingSystem.IsLinux()) return LinuxCpuTicks("cpu" + cpu.ToString(Globalization.CultureInfo.InvariantCulture));
            if (s_host.Value == 0) return null;
            if (host_processor_info(s_host.Value, PROCESSOR_CPU_LOAD_INFO, out uint processors, out int* info, out uint infoCount) != 0) return null;
            try
            {
                if (cpu >= processors) return null;
                int* t = info + cpu * CPU_STATE_MAX;
                return new long[] { (uint)t[0], (uint)t[1], (uint)t[2], (uint)t[3] };
            }
            finally
            {
                vm_deallocate(s_task.Value, (nuint)info, (nuint)(infoCount * sizeof(int)));
            }
        }

        /// <summary>
        /// Bytes available for allocation (free, inactive and speculative pages -- what Windows calls
        /// available: free plus standby) and bytes committed (what is in use, wired and compressed).
        /// </summary>
        public static (long Available, long Committed) Memory()
        {
            if (OperatingSystem.IsWindows())
            {
                // available: free plus standby; committed: the commit charge
                var m = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
                if (GlobalMemoryStatusEx(ref m) == 0) return (0, 0);
                return ((long)m.ullAvailPhys, (long)(m.ullTotalPageFile - m.ullAvailPageFile));
            }
            if (OperatingSystem.IsLinux()) return (LinuxMeminfo("MemAvailable"), LinuxMeminfo("Committed_AS"));
            long pageSize = Environment.SystemPageSize;
            long total = 0;
            nint len = sizeof(long);
            if (OperatingSystem.IsMacOS()) sysctlbyname("hw.memsize", &total, &len, null, 0);
            if (s_host.Value == 0) return (0, 0);
            // vm_statistics64 in 32-bit words: free 0, active 1, inactive 2, wire 3, nine uint64
            // counters (4-21), purgeable 22, speculative 23, four uint64 (24-31), compressor 32.
            int* info = stackalloc int[64];
            uint count = 38;
            if (host_statistics64(s_host.Value, HOST_VM_INFO64, info, ref count) != 0) return (0, 0);
            long free = (uint)info[0], active = (uint)info[1], inactive = (uint)info[2], wired = (uint)info[3];
            long speculative = (uint)info[23];
            long compressed = (uint)info[32];
            long available = (free + inactive + speculative) * pageSize;
            long committed = (active + wired + compressed) * pageSize;
            return (Math.Min(available, total > 0 ? total : long.MaxValue), committed);
        }
    }
}
