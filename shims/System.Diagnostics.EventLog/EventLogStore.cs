// Where logs, sources and entries live.
//
// Logs and sources: HKLM\SYSTEM\CurrentControlSet\Services\EventLog\<log>\<source>, as on Windows,
// through Microsoft.Win32.Registry; when no registry is available, in memory. Entries: the OS's log (OsLog:
// the Event Log, journald, the unified log), written there and read back from there, so a log holds what
// every program wrote; where the OS keeps none, in memory, per log, for this process. Each also goes to
// wpfrun's compatibility log. A log the OS keeps cannot lose one program's entries but on Windows: a cleared
// log is one whose entries before its clearing (kept with its registration, for every process) are left out.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace System.Diagnostics
{
    internal static class EventLogStore
    {
        private const string Root = @"SYSTEM\CurrentControlSet\Services\EventLog";
        private static readonly string[] s_builtIn = { "Application", "System", "Security" };

        private static readonly object s_gate = new object();
        private static readonly Dictionary<string, LogData> s_logs = new Dictionary<string, LogData>(StringComparer.OrdinalIgnoreCase);
        // Used only when there is no registry to keep them in.
        private static readonly Dictionary<string, string> s_memorySources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> s_memoryLogs = new HashSet<string>(s_builtIn, StringComparer.OrdinalIgnoreCase);
        private static readonly List<WeakReference<EventLog>> s_listeners = new List<WeakReference<EventLog>>();

        internal sealed class LogData
        {
            private readonly List<EventLogEntry> _entries = new List<EventLogEntry>();
            private readonly string _name;

            public LogData(string name) => _name = name;
            private int _next = 1;

            public long MaximumKilobytes = 20480;
            public OverflowAction Overflow = OverflowAction.OverwriteAsNeeded;
            public int RetentionDays;

            public EventLogEntry[] Snapshot()
            {
                if (OsLog.Keeps) return OsLog.Read(_name, SourcesOf(_name), ClearedAt(_name));
                lock (_entries) return _entries.ToArray();
            }

            public EventLogEntry Add(Func<int, EventLogEntry> make)
            {
                lock (_entries)
                {
                    EventLogEntry e = make(_next++);
                    _entries.Add(e);
                    return e;
                }
            }

            public void Clear()
            {
                if (OperatingSystem.IsWindows()) WindowsEventLog.Clear(_name);
                else if (OsLog.Keeps) SetClearedAt(_name, DateTime.Now);
                else lock (_entries) _entries.Clear();
            }
        }

        public static LogData Log(string name, bool create)
        {
            lock (s_gate)
            {
                if (!s_logs.TryGetValue(name, out LogData log))
                {
                    if (!create && !LogExists(name)) throw new InvalidOperationException($"The event log '{name}' on computer '.' does not exist.");
                    s_logs[name] = log = new LogData(name);
                }
                return log;
            }
        }

        public static IEnumerable<string> LogNames()
        {
            var names = new List<string>(s_builtIn);
            WithRegistry(root =>
            {
                foreach (string n in root.GetSubKeyNames()) if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
            }, () =>
            {
                lock (s_gate) foreach (string n in s_memoryLogs) if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
            });
            return names;
        }

        public static bool LogExists(string name) =>
            s_builtIn.Contains(name, StringComparer.OrdinalIgnoreCase) || LogNames().Contains(name, StringComparer.OrdinalIgnoreCase);

        public static string LogOfSource(string source)
        {
            if (string.IsNullOrEmpty(source)) return null;
            string found = null;
            WithRegistry(root =>
            {
                foreach (string log in root.GetSubKeyNames())
                {
                    using RegistryKey k = root.OpenSubKey(log);
                    if (k?.GetSubKeyNames().Contains(source, StringComparer.OrdinalIgnoreCase) == true) { found = log; return; }
                }
            }, () =>
            {
                lock (s_gate) s_memorySources.TryGetValue(source, out found);
            });
            return found;
        }

        public static void CreateSource(string source, string log)
        {
            WithRegistry(root =>
            {
                using RegistryKey logKey = root.CreateSubKey(log);
                using RegistryKey sourceKey = logKey.CreateSubKey(source);
                // Windows: the message file Event Viewer formats the entries with -- .NET's, whose every id is "%1"
                // (the entry's text), as dotnet/runtime's EventLog registers it
                if (OperatingSystem.IsWindows() && MessagesDll() is { } messages && sourceKey.GetValue("EventMessageFile") == null)
                    sourceKey.SetValue("EventMessageFile", messages, RegistryValueKind.ExpandString);
                // The log's own name is also a source of it, as Windows registers it.
                using RegistryKey self = logKey.CreateSubKey(log);
            }, () =>
            {
                lock (s_gate)
                {
                    s_memoryLogs.Add(log);
                    s_memorySources[source] = log;
                    s_memorySources[log] = log;
                }
            });
        }

        private static string MessagesDll()
        {
            string dir = Path.GetDirectoryName(typeof(EventLog).Assembly.Location);
            if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;
            string file = Path.Combine(dir, "System.Diagnostics.EventLog.Messages.dll");
            return File.Exists(file) ? file : null;
        }

        public static bool DeleteSource(string source)
        {
            string log = LogOfSource(source);
            if (log == null) return false;
            WithRegistry(root =>
            {
                using RegistryKey k = root.OpenSubKey(log, writable: true);
                k?.DeleteSubKeyTree(source, throwOnMissingSubKey: false);
            }, () =>
            {
                lock (s_gate) s_memorySources.Remove(source);
            });
            return true;
        }

        public static void DeleteLog(string log)
        {
            WithRegistry(root => root.DeleteSubKeyTree(log, throwOnMissingSubKey: false), () =>
            {
                lock (s_gate)
                {
                    s_memoryLogs.Remove(log);
                    foreach (string s in s_memorySources.Where(p => string.Equals(p.Value, log, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToList()) s_memorySources.Remove(s);
                }
            });
            lock (s_gate) s_logs.Remove(log);
        }

        public static void Append(string log, string source, string machine, EventLogEntryType type, long instanceId, short category, byte[] data, string[] strings, string message)
        {
            OsLog.Write(log, source, type, instanceId, category, data, strings, message);
            EventLogEntry entry = OsLog.Keeps
                ? new EventLogEntry(0, source, machine, type, instanceId, category, data, strings, message)
                : Log(log, create: true).Add(index => new EventLogEntry(index, source, machine, type, instanceId, category, data, strings, message));
            try
            {
                (AppContext.GetData("WpfWebGpu.Log") as Action<string>)?.Invoke($"EventLog {log}/{source} {type} {instanceId}: {message}");
            }
            catch
            {
            }

            EventLog[] listeners;
            lock (s_listeners)
            {
                s_listeners.RemoveAll(w => !w.TryGetTarget(out _));
                listeners = s_listeners.Select(w => w.TryGetTarget(out EventLog l) ? l : null)
                    .Where(l => l != null && string.Equals(l.ListenedLog, log, StringComparison.OrdinalIgnoreCase)).ToArray();
            }
            foreach (EventLog l in listeners) l.OnEntryWritten(entry);
        }

        /// <summary>The sources registered in the log (its own name one of them).</summary>
        public static IReadOnlyCollection<string> SourcesOf(string log)
        {
            var sources = new List<string> { log };
            WithRegistry(root =>
            {
                using RegistryKey k = root.OpenSubKey(log);
                if (k != null) foreach (string s in k.GetSubKeyNames()) if (!sources.Contains(s, StringComparer.OrdinalIgnoreCase)) sources.Add(s);
            }, () =>
            {
                lock (s_gate) sources.AddRange(s_memorySources.Where(p => string.Equals(p.Value, log, StringComparison.OrdinalIgnoreCase) && !sources.Contains(p.Key, StringComparer.OrdinalIgnoreCase)).Select(p => p.Key));
            });
            return sources;
        }

        // when a log the OS keeps was cleared: a value of its registration
        private const string ClearedValue = "WpfWebGpuCleared";
        private static readonly Dictionary<string, DateTime> s_memoryCleared = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private static DateTime? ClearedAt(string log)
        {
            DateTime? at = null;
            WithRegistry(root =>
            {
                using RegistryKey k = root.OpenSubKey(log);
                if (k?.GetValue(ClearedValue) is string text && DateTime.TryParse(text, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind, out DateTime t)) at = t;
            }, () =>
            {
                lock (s_gate) if (s_memoryCleared.TryGetValue(log, out DateTime t)) at = t;
            });
            return at;
        }

        private static void SetClearedAt(string log, DateTime at)
        {
            WithRegistry(root =>
            {
                using RegistryKey k = root.CreateSubKey(log);
                k.SetValue(ClearedValue, at.ToString("o", Globalization.CultureInfo.InvariantCulture));
            }, () =>
            {
                lock (s_gate) s_memoryCleared[log] = at;
            });
        }

        public static void Listen(EventLog log)
        {
            lock (s_listeners) s_listeners.Add(new WeakReference<EventLog>(log));
        }

        public static void Unlisten(EventLog log)
        {
            lock (s_listeners) s_listeners.RemoveAll(w => !w.TryGetTarget(out EventLog l) || ReferenceEquals(l, log));
        }

        private static bool? s_registryWorks;

        private static void WithRegistry(Action<RegistryKey> withRegistry, Action withoutRegistry)
        {
            if (s_registryWorks != false)
            {
                try
                {
                    using RegistryKey root = Registry.LocalMachine.CreateSubKey(Root);
                    if (root != null)
                    {
                        foreach (string log in s_builtIn) root.CreateSubKey(log)?.Dispose();
                        withRegistry(root);
                        s_registryWorks = true;
                        return;
                    }
                }
                catch (Exception e) when (e is PlatformNotSupportedException or UnauthorizedAccessException or Security.SecurityException or NullReferenceException)
                {
                    if (s_registryWorks == true) throw;
                    s_registryWorks = false;
                }
            }
            withoutRegistry();
        }
    }
}
