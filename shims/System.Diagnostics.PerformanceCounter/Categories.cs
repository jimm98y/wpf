// The categories a process can see: the standard ones this machine reports live, and those the app
// created itself (process-local, as if its installer had registered them).

using System.Collections.Generic;
using System.Linq;

namespace System.Diagnostics
{
    internal abstract class Category
    {
        public string Name;
        public string Help;
        public PerformanceCounterCategoryType Type;

        public abstract bool IsCustom { get; }

        public abstract IEnumerable<(string Name, string Help, PerformanceCounterType Type)> Counters { get; }

        public abstract string[] InstanceNames();

        /// <summary>The counter's current reading for an instance ("" for a single-instance category).</summary>
        public abstract CounterSample Read(string counter, string instance);

        public (string Name, string Help, PerformanceCounterType Type)? Counter(string name) =>
            Counters.Cast<(string, string, PerformanceCounterType)?>().FirstOrDefault(c => string.Equals(c.Value.Item1, name, StringComparison.OrdinalIgnoreCase));

        protected static CounterSample Sample(long raw, PerformanceCounterType type, long baseValue = 0, long timeStamp100 = 0, long counterTimeStamp = 0) =>
            new CounterSample(raw, baseValue, Stopwatch.Frequency, Stopwatch.Frequency, Stopwatch.GetTimestamp(),
                timeStamp100 != 0 ? timeStamp100 : DateTime.UtcNow.ToFileTimeUtc(), type, counterTimeStamp);
    }

    /// <summary>A category the app created: counters it writes and reads back, per instance.</summary>
    internal sealed class CustomCategory : Category
    {
        public readonly List<(string Name, string Help, PerformanceCounterType Type)> Definitions = new();
        // (counter, instance) -> raw value; an instance exists once one of its counters was written.
        public readonly Dictionary<(string, string), long> Values = new(new KeyComparer());

        public override bool IsCustom => true;

        public override IEnumerable<(string Name, string Help, PerformanceCounterType Type)> Counters => Definitions;

        public override string[] InstanceNames()
        {
            lock (Values)
            {
                return Values.Keys.Select(k => k.Item2).Where(i => i.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        public override CounterSample Read(string counter, string instance)
        {
            var def = Counter(counter).Value;
            long raw = Get(counter, instance);
            // A fraction or average reads its base from the counter defined right after it.
            int index = Definitions.FindIndex(d => string.Equals(d.Name, def.Name, StringComparison.OrdinalIgnoreCase));
            long baseValue = index >= 0 && index + 1 < Definitions.Count ? Get(Definitions[index + 1].Name, instance) : 0;
            return Sample(raw, def.Type, baseValue);
        }

        public long Get(string counter, string instance)
        {
            lock (Values)
            {
                return Values.TryGetValue((counter, instance ?? ""), out long v) ? v : 0;
            }
        }

        public long Update(string counter, string instance, Func<long, long> change)
        {
            lock (Values)
            {
                var key = (counter, instance ?? "");
                Values.TryGetValue(key, out long v);
                return Values[key] = change(v);
            }
        }

        public void RemoveInstance(string instance)
        {
            lock (Values)
            {
                foreach (var key in Values.Keys.Where(k => string.Equals(k.Item2, instance, StringComparison.OrdinalIgnoreCase)).ToList()) Values.Remove(key);
            }
        }

        private sealed class KeyComparer : IEqualityComparer<(string, string)>
        {
            public bool Equals((string, string) x, (string, string) y) =>
                StringComparer.OrdinalIgnoreCase.Equals(x.Item1, y.Item1) && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2);

            public int GetHashCode((string, string) k) =>
                HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item1), StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item2));
        }
    }

    /// <summary>A standard category: read-only, values from the machine.</summary>
    internal sealed class SystemCategory : Category
    {
        private readonly (string Name, string Help, PerformanceCounterType Type)[] _counters;
        private readonly Func<string[]> _instances;
        private readonly Func<string, string, CounterSample> _read;

        public SystemCategory(string name, string help, PerformanceCounterCategoryType type, (string, string, PerformanceCounterType)[] counters, Func<string[]> instances, Func<string, string, CounterSample> read)
        {
            Name = name;
            Help = help;
            Type = type;
            _counters = counters;
            _instances = instances;
            _read = read;
        }

        public override bool IsCustom => false;

        public override IEnumerable<(string Name, string Help, PerformanceCounterType Type)> Counters => _counters;

        public override string[] InstanceNames() => _instances();

        public override CounterSample Read(string counter, string instance) => _read(Counter(counter).Value.Name, instance);

        public static readonly SystemCategory[] All =
        {
            new SystemCategory("Processor", "The Processor performance object consists of counters that measure aspects of processor activity.",
                PerformanceCounterCategoryType.MultiInstance, new[]
                {
                    ("% Processor Time", "% Processor Time is the percentage of elapsed time that the processor spends to execute a non-Idle thread.", PerformanceCounterType.Timer100NsInverse),
                    ("% User Time", "% User Time is the percentage of elapsed time the processor spends in the user mode.", PerformanceCounterType.Timer100Ns),
                    ("% Privileged Time", "% Privileged Time is the percentage of elapsed time that the process threads spent executing code in privileged mode.", PerformanceCounterType.Timer100Ns),
                    ("% Idle Time", "% Idle Time is the percentage of time the processor is idle during the sample interval.", PerformanceCounterType.Timer100Ns),
                },
                () => new[] { "_Total" }.Concat(Enumerable.Range(0, Mach.CpuCount).Select(i => i.ToString(Globalization.CultureInfo.InvariantCulture))).ToArray(),
                ReadProcessor),
            new SystemCategory("Memory", "The Memory performance object consists of counters that describe the behavior of physical and virtual memory on the computer.",
                PerformanceCounterCategoryType.SingleInstance, new[]
                {
                    ("Available Bytes", "Available Bytes is the amount of physical memory, in bytes, immediately available for allocation to a process or for system use.", PerformanceCounterType.NumberOfItems64),
                    ("Available KBytes", "Available KBytes is the amount of physical memory, in Kilobytes, immediately available for allocation to a process or for system use.", PerformanceCounterType.NumberOfItems64),
                    ("Available MBytes", "Available MBytes is the amount of physical memory, in Megabytes, immediately available for allocation to a process or for system use.", PerformanceCounterType.NumberOfItems64),
                    ("Committed Bytes", "Committed Bytes is the amount of committed virtual memory, in bytes.", PerformanceCounterType.NumberOfItems64),
                },
                () => Array.Empty<string>(),
                ReadMemory),
            new SystemCategory("Process", "The Process performance object consists of counters that monitor running application program and system processes.",
                PerformanceCounterCategoryType.MultiInstance, new[]
                {
                    ("% Processor Time", "% Processor Time is the percentage of elapsed time that all of process threads used the processor to execution instructions.", PerformanceCounterType.Timer100Ns),
                    ("Working Set", "Working Set is the current size, in bytes, of the Working Set of this process.", PerformanceCounterType.NumberOfItems64),
                    ("Private Bytes", "Private Bytes is the current size, in bytes, of memory that this process has allocated that cannot be shared with other processes.", PerformanceCounterType.NumberOfItems64),
                    ("Virtual Bytes", "Virtual Bytes is the current size, in bytes, of the virtual address space the process is using.", PerformanceCounterType.NumberOfItems64),
                    ("Thread Count", "The number of threads currently active in this process.", PerformanceCounterType.NumberOfItems32),
                    ("Handle Count", "The total number of handles currently open by this process.", PerformanceCounterType.NumberOfItems32),
                    ("ID Process", "ID Process is the unique identifier of this process.", PerformanceCounterType.NumberOfItems32),
                },
                () => ProcessInstances().Select(p => p.Name).ToArray(),
                ReadProcess),
            new SystemCategory("System", "The System performance object consists of counters that apply to more than one instance of a component processors on the computer.",
                PerformanceCounterCategoryType.SingleInstance, new[]
                {
                    ("System Up Time", "System Up Time is the elapsed time (in seconds) that the computer has been running since it was last started.", PerformanceCounterType.ElapsedTime),
                    ("Processes", "Processes is the number of processes in the computer at the time of data collection.", PerformanceCounterType.NumberOfItems32),
                    ("Threads", "Threads is the number of threads in the computer at the time of data collection.", PerformanceCounterType.NumberOfItems32),
                },
                () => Array.Empty<string>(),
                ReadSystem),
        };

        private static CounterSample ReadProcessor(string counter, string instance)
        {
            // Ticks per state (user, system, idle, nice) for the instance; their sum is the time base.
            long[] t = instance == "_Total" ? Mach.TotalCpuTicks() : Mach.CpuTicks(int.Parse(instance, Globalization.CultureInfo.InvariantCulture));
            if (t == null) throw new InvalidOperationException($"Instance '{instance}' does not exist in the specified Category.");
            long total = t[0] + t[1] + t[2] + t[3];
            long raw = counter switch
            {
                "% User Time" => t[0] + t[3],
                "% Privileged Time" => t[1],
                _ => t[2],                       // idle: "% Processor Time" is its inverse
            };
            return Sample(raw, Counter(counter, "Processor"), timeStamp100: total);
        }

        private static CounterSample ReadMemory(string counter, string instance)
        {
            (long available, long committed) = Mach.Memory();
            long raw = counter switch
            {
                "Available Bytes" => available,
                "Available KBytes" => available / 1024,
                "Available MBytes" => available / (1024 * 1024),
                _ => committed,
            };
            return Sample(raw, PerformanceCounterType.NumberOfItems64);
        }

        private static CounterSample ReadProcess(string counter, string instance)
        {
            var match = ProcessInstances().FirstOrDefault(p => string.Equals(p.Name, instance, StringComparison.OrdinalIgnoreCase));
            if (match.Process == null) throw new InvalidOperationException($"Instance '{instance}' does not exist in the specified Category.");
            using Process p = match.Process;
            p.Refresh();
            long raw = counter switch
            {
                "% Processor Time" => p.TotalProcessorTime.Ticks,
                "Working Set" => p.WorkingSet64,
                "Private Bytes" => p.PrivateMemorySize64,
                "Virtual Bytes" => p.VirtualMemorySize64,
                "Thread Count" => p.Threads.Count,
                "Handle Count" => p.HandleCount,
                _ => p.Id,
            };
            return Sample(raw, Counter(counter, "Process"), timeStamp100: DateTime.UtcNow.Ticks);
        }

        private static CounterSample ReadSystem(string counter, string instance)
        {
            switch (counter)
            {
                case "System Up Time":
                    long now = DateTime.UtcNow.ToFileTimeUtc();
                    long boot = now - Environment.TickCount64 * TimeSpan.TicksPerMillisecond;
                    return new CounterSample(boot, 0, TimeSpan.TicksPerSecond, Stopwatch.Frequency, Stopwatch.GetTimestamp(), now, PerformanceCounterType.ElapsedTime, now);
                case "Processes":
                    return Sample(Process.GetProcesses().Length, PerformanceCounterType.NumberOfItems32);
                default:
                    return Sample(Process.GetProcesses().Sum(p => { try { return p.Threads.Count; } catch { return 0; } }), PerformanceCounterType.NumberOfItems32);
            }
        }

        private static PerformanceCounterType Counter(string counter, string category) =>
            All.First(c => c.Name == category).Counter(counter).Value.Type;

        // Instance names as Windows forms them: the process name, then name#1, name#2 for the others.
        private static List<(string Name, Process Process)> ProcessInstances()
        {
            var result = new List<(string, Process)>();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Process p in Process.GetProcesses().OrderBy(p => p.Id))
            {
                string name;
                try { name = p.ProcessName; }
                catch { continue; }
                seen.TryGetValue(name, out int n);
                seen[name] = n + 1;
                result.Add((n == 0 ? name : name + "#" + n.ToString(Globalization.CultureInfo.InvariantCulture), p));
            }
            return result;
        }
    }

    internal static class Categories
    {
        private static readonly object s_gate = new object();
        private static readonly List<CustomCategory> s_custom = new List<CustomCategory>();

        public static Category Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            lock (s_gate)
            {
                Category c = s_custom.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                return c ?? SystemCategory.All.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            }
        }

        public static Category[] All()
        {
            lock (s_gate)
            {
                return SystemCategory.All.Cast<Category>().Concat(s_custom).ToArray();
            }
        }

        public static void Add(CustomCategory c)
        {
            lock (s_gate)
            {
                if (Find(c.Name) != null) throw new InvalidOperationException($"Cannot create Performance Category '{c.Name}' because it already exists.");
                s_custom.Add(c);
            }
        }

        public static void Delete(string name)
        {
            lock (s_gate)
            {
                Category c = Find(name);
                if (c == null) throw new InvalidOperationException("Category does not exist.");
                if (!c.IsCustom) throw new InvalidOperationException("Only categories created with PerformanceCounterCategory.Create can be deleted.");
                s_custom.Remove((CustomCategory)c);
            }
        }

        public static bool IsLocal(string machineName) =>
            machineName == "." || string.Equals(machineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

        public static void RequireLocal(string machineName)
        {
            if (!IsLocal(machineName)) throw new InvalidOperationException($"Couldn't connect to remote machine '{machineName}'.");
        }
    }
}
