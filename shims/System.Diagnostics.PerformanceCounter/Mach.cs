// CPU and memory readings from the Mach host interface.

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

        /// <summary>Clock ticks spent in user, system, idle and nice, all processors together.</summary>
        public static long[] TotalCpuTicks()
        {
            if (s_host.Value == 0) return new long[4];
            int* info = stackalloc int[CPU_STATE_MAX];
            uint count = CPU_STATE_MAX;
            if (host_statistics(s_host.Value, HOST_CPU_LOAD_INFO, info, ref count) != 0) return new long[4];
            return new long[] { (uint)info[0], (uint)info[1], (uint)info[2], (uint)info[3] };
        }

        /// <summary>The same for one processor, or null when there is no such processor.</summary>
        public static long[] CpuTicks(int cpu)
        {
            if (s_host.Value == 0 || cpu < 0) return null;
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
