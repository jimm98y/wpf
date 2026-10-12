// Where logs, sources and entries live.
//
// Logs and sources: HKLM\SYSTEM\CurrentControlSet\Services\EventLog\<log>\<source>, as on Windows,
// through Microsoft.Win32.Registry; when no registry is available, in memory. Entries: in memory,
// per log, for this process; each one also goes to wpfmac's compatibility log.

using System.Collections.Generic;
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
            private int _next = 1;

            public long MaximumKilobytes = 20480;
            public OverflowAction Overflow = OverflowAction.OverwriteAsNeeded;
            public int RetentionDays;

            public EventLogEntry[] Snapshot()
            {
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
                lock (_entries) _entries.Clear();
            }
        }

        public static LogData Log(string name, bool create)
        {
            lock (s_gate)
            {
                if (!s_logs.TryGetValue(name, out LogData log))
                {
                    if (!create && !LogExists(name)) throw new InvalidOperationException($"The event log '{name}' on computer '.' does not exist.");
                    s_logs[name] = log = new LogData();
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
            EventLogEntry entry = Log(log, create: true).Add(index => new EventLogEntry(index, source, machine, type, instanceId, category, data, strings, message));
            try
            {
                (AppContext.GetData("WpfMac.Log") as Action<string>)?.Invoke($"EventLog {log}/{source} {type} {instanceId}: {message}");
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
