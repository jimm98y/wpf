// The entries are the OS's: an entry is written to the OS's own log, where its tools show it beside every other
// program's, and a log's entries are read back from there -- what every program wrote, kept as the OS keeps its log:
//
//   Linux    journald (its native protocol, on its socket): MESSAGE and PRIORITY, the source as SYSLOG_IDENTIFIER,
//            and the event's own fields (EVENTLOG_LOG, _SOURCE, _INSTANCE_ID, _CATEGORY, _TYPE, _MACHINE, each
//            replacement string an EVENTLOG_STRING, the data EVENTLOG_DATA), so that `journalctl EVENTLOG_LOG=
//            Application` or `SYSLOG_IDENTIFIER=<source>` finds them; read back from it so (journalctl's JSON).
//   macOS    the unified log (os_log): the source as its subsystem, the log as its category, an error as an error;
//            `log show --predicate 'subsystem == "<source>"'` and Console find them. os_log is a macro whose format
//            string must be in a loaded image's __oslogstring section (the log reads it from there when it shows
//            the entry), so a "%{public}s" of a system library's section is the format, the entry's text its one
//            argument. Read back with `log show` (its JSON), which keeps the text and whether it was an error, not
//            an entry's category or data. `log show` reads the whole store for a time range, seconds a day of it:
//            a process reads a log's last day (or since it was cleared) once, then only what came since.
//   Windows  the Event Log itself (WindowsEventLog: ReportEvent, ReadEventLog).
//
// Where the OS keeps no log (Android, iOS, a Linux without journald) the process keeps its entries
// (EventLogStore), syslog given a copy where there is one.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Inv = System.Globalization.CultureInfo;

namespace System.Diagnostics
{
    internal static unsafe class OsLog
    {
        private const string JournalSocket = "/run/systemd/journal/socket";
        private const string Journalctl = "/usr/bin/journalctl";
        private const string LogTool = "/usr/bin/log";

        private static bool? s_keeps;

        /// <summary>Whether this OS keeps the entries (written there, read back from there).</summary>
        public static bool Keeps => s_keeps ??=
            OperatingSystem.IsWindows()
            || (OperatingSystem.IsMacOS() && File.Exists(LogTool) && Format() != null)
            || (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() && File.Exists(JournalSocket) && File.Exists(Journalctl));

        public static void Write(string log, string source, EventLogEntryType type, long instanceId, short category, byte[] data, string[] strings, string message)
        {
            if (OperatingSystem.IsWindows()) WindowsEventLog.Report(source, type, instanceId, category, data, strings);
            else if (OperatingSystem.IsMacOS()) Unified(log, source, type, instanceId, message);
            else if (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
            {
                try
                {
                    if (Keeps) Journal(log, source, type, instanceId, category, data, strings, message);
                    else Syslog(source, type, Text(instanceId, message));
                }
                catch (Exception e) when (e is SocketException or DllNotFoundException or EntryPointNotFoundException) { }
            }
        }

        /// <summary>The log's entries the OS keeps, oldest first; those before <paramref name="since"/> (the log
        /// cleared then) left out.</summary>
        public static EventLogEntry[] Read(string log, IReadOnlyCollection<string> sources, DateTime? since)
        {
            IEnumerable<EventLogEntry> entries =
                OperatingSystem.IsWindows() ? WindowsEventLog.Read(log)
                : OperatingSystem.IsMacOS() ? ReadUnified(log, sources, since)
                : ReadJournal(log, since);
            if (since is { } after) entries = entries.Where(e => e.TimeGenerated >= after);
            return entries.ToArray();
        }

        private static string Text(long instanceId, string message)
        {
            int eventId = (int)(instanceId & 0x3FFFFFFF);
            return eventId != 0 ? $"Event {eventId}: {message}" : message;
        }

        // syslog's severities: an error 3, a warning (and a failed audit) 4, a successful audit 5 (notice), information 6
        private static int Priority(EventLogEntryType type) => type switch
        {
            EventLogEntryType.Error => 3,
            EventLogEntryType.Warning or EventLogEntryType.FailureAudit => 4,
            EventLogEntryType.SuccessAudit => 5,
            _ => 6,
        };

        // ---- Linux: journald ---------------------------------------------------------------------------------

        private static Socket s_journal;

        private static void Journal(string log, string source, EventLogEntryType type, long instanceId, short category, byte[] data, string[] strings, string message)
        {
            var datagram = new MemoryStream();
            void Field(string name, string value)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value);
                datagram.Write(Encoding.ASCII.GetBytes(name));
                if (Array.IndexOf(bytes, (byte)'\n') < 0)
                {
                    datagram.WriteByte((byte)'=');
                    datagram.Write(bytes);
                }
                else
                {
                    // a value with a new line: the name, a new line, its length (64-bit little endian), the bytes
                    datagram.WriteByte((byte)'\n');
                    Span<byte> length = stackalloc byte[8];
                    Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)bytes.Length);
                    datagram.Write(length);
                    datagram.Write(bytes);
                }
                datagram.WriteByte((byte)'\n');
            }
            Field("MESSAGE", message);
            Field("PRIORITY", Priority(type).ToString(Inv.InvariantCulture));
            Field("SYSLOG_IDENTIFIER", source);
            Field("EVENTLOG_LOG", log);
            Field("EVENTLOG_SOURCE", source);
            Field("EVENTLOG_INSTANCE_ID", instanceId.ToString(Inv.InvariantCulture));
            Field("EVENTLOG_CATEGORY", category.ToString(Inv.InvariantCulture));
            Field("EVENTLOG_TYPE", ((int)type).ToString(Inv.InvariantCulture));
            Field("EVENTLOG_MACHINE", Environment.MachineName);
            // each replacement string a field of the same name (journald keeps them in order), the data as hex
            foreach (string s in strings ?? Array.Empty<string>()) Field("EVENTLOG_STRING", s ?? "");
            if (data is { Length: > 0 }) Field("EVENTLOG_DATA", Convert.ToHexString(data));
            lock (JournalSocket)
            {
                s_journal ??= new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
                s_journal.SendTo(datagram.ToArray(), new UnixDomainSocketEndPoint(JournalSocket));
            }
        }

        // journalctl's JSON, an entry a line: a field journald does not keep as text (a new line in it) is an array of
        // its bytes, a field given more than once an array of its values
        private static IEnumerable<EventLogEntry> ReadJournal(string log, DateTime? since)
        {
            var args = new List<string> { "-o", "json", "--no-pager", "--quiet" };
            if (since is { } after) args.Add("--since=@" + new DateTimeOffset(after.ToUniversalTime()).ToUnixTimeSeconds().ToString(Inv.InvariantCulture));
            args.Add("EVENTLOG_LOG=" + log);
            var entries = new List<EventLogEntry>();
            foreach (string line in Lines(Journalctl, args))
            {
                using var json = JsonDocument.Parse(line);
                JsonElement e = json.RootElement;
                string data = First(e, "EVENTLOG_DATA");
                long micros = Number(First(e, "__REALTIME_TIMESTAMP"), 0);
                entries.Add(new EventLogEntry(entries.Count + 1, First(e, "EVENTLOG_SOURCE"), First(e, "EVENTLOG_MACHINE") is { Length: > 0 } m ? m : Environment.MachineName,
                    (EventLogEntryType)Number(First(e, "EVENTLOG_TYPE"), 4), Number(First(e, "EVENTLOG_INSTANCE_ID"), 0), (short)Number(First(e, "EVENTLOG_CATEGORY"), 0),
                    data.Length > 0 ? Convert.FromHexString(data) : Array.Empty<byte>(), Values(e, "EVENTLOG_STRING"), First(e, "MESSAGE"),
                    DateTime.UnixEpoch.AddTicks(micros * 10).ToLocalTime()));
            }
            return entries;
        }

        private static string First(JsonElement entry, string field) => Values(entry, field).FirstOrDefault() ?? "";

        private static string[] Values(JsonElement entry, string field)
        {
            if (!entry.TryGetProperty(field, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return Array.Empty<string>();
            static string One(JsonElement v) => v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Array => Encoding.UTF8.GetString(v.EnumerateArray().Select(b => (byte)b.GetInt32()).ToArray()),
                _ => v.ToString(),
            };
            // several values: an array of them (strings, or byte arrays); one: a string, or one byte array
            if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0 && value[0].ValueKind != JsonValueKind.Number)
                return value.EnumerateArray().Select(One).ToArray();
            return new[] { One(value) };
        }

        private static long Number(string text, long otherwise) =>
            long.TryParse(text, Globalization.NumberStyles.Integer, Inv.InvariantCulture, out long n) ? n : otherwise;

        [DllImport("libc", EntryPoint = "syslog")]
        private static extern void syslog(int priority, byte* format, byte* message);

        private static void Syslog(string source, EventLogEntryType type, string text)
        {
            // LOG_USER (1 << 3) and the severity; the source leads the text, as an identifier would
            byte[] format = "%s\0"u8.ToArray();
            byte[] message = Encoding.UTF8.GetBytes(source + ": " + text + "\0");
            fixed (byte* f = format)
            fixed (byte* m = message)
                syslog((1 << 3) | Priority(type), f, m);
        }

        // ---- macOS: the unified log ----------------------------------------------------------------------------

        private const string LibSystem = "/usr/lib/libSystem.B.dylib";

        [DllImport(LibSystem)] private static extern IntPtr os_log_create(byte* subsystem, byte* category);
        [DllImport(LibSystem)] private static extern void _os_log_impl(IntPtr dso, IntPtr log, byte type, IntPtr format, byte* buffer, uint size);
        [DllImport(LibSystem)] private static extern uint _dyld_image_count();
        [DllImport(LibSystem)] private static extern IntPtr _dyld_get_image_header(uint index);
        [DllImport(LibSystem)] private static extern byte* getsectiondata(IntPtr header, byte* segment, byte* section, out nuint size);

        private static readonly ConcurrentDictionary<(string, string), IntPtr> s_logs = new();
        private static (IntPtr Dso, IntPtr Format)? s_format;
        private static bool s_formatSearched;

        private static void Unified(string log, string source, EventLogEntryType type, long instanceId, string message)
        {
            if (Format() is not { } format) return;
            IntPtr handle = s_logs.GetOrAdd((source, log), static key =>
            {
                byte[] subsystem = Encoding.UTF8.GetBytes(key.Item1 + "\0"), category = Encoding.UTF8.GetBytes(key.Item2 + "\0");
                fixed (byte* s = subsystem)
                fixed (byte* c = category)
                    return os_log_create(s, c);
            });
            if (handle == IntPtr.Zero) return;
            // OS_LOG_TYPE_ERROR for an error (and a failed audit), the default (kept on disk) for the rest
            byte level = type is EventLogEntryType.Error or EventLogEntryType.FailureAudit ? (byte)0x10 : (byte)0x00;
            IntPtr text = Marshal.StringToCoTaskMemUTF8(Text(instanceId, message));
            try
            {
                // the arguments: a summary (one is not a scalar), their count (1), then the one -- a public string
                // (type 2, flag public), 8 bytes: its pointer
                byte* buffer = stackalloc byte[12];
                buffer[0] = 0x02;
                buffer[1] = 1;
                buffer[2] = 0x22;
                buffer[3] = 8;
                *(IntPtr*)(buffer + 4) = text;
                _os_log_impl(format.Dso, handle, level, format.Format, buffer, 12);
            }
            finally { Marshal.FreeCoTaskMem(text); }
        }

        // "%{public}s" as a whole string of a loaded system image's __TEXT,__oslogstring, and that image (the dso the
        // entry names, whose section the log reads the format from)
        private static (IntPtr Dso, IntPtr Format)? Format()
        {
            if (s_formatSearched) return s_format;
            lock (s_logs)
            {
                if (s_formatSearched) return s_format;
                byte[] wanted = "%{public}s\0"u8.ToArray();
                byte[] segment = "__TEXT\0"u8.ToArray(), section = "__oslogstring\0"u8.ToArray();
                uint count = _dyld_image_count();
                fixed (byte* seg = segment)
                fixed (byte* sect = section)
                    for (uint i = 0; i < count && s_format == null; i++)
                    {
                        IntPtr header = _dyld_get_image_header(i);
                        if (header == IntPtr.Zero) continue;
                        byte* data = getsectiondata(header, seg, sect, out nuint size);
                        if (data == null || size == 0) continue;
                        var span = new ReadOnlySpan<byte>(data, (int)Math.Min(size, int.MaxValue));
                        for (int at = 0; at < span.Length;)
                        {
                            int found = span[at..].IndexOf(wanted);
                            if (found < 0) break;
                            int start = at + found;
                            if (start == 0 || span[start - 1] == 0) { s_format = (header, (IntPtr)(data + start)); break; }
                            at = start + 1;
                        }
                    }
                s_formatSearched = true;
                return s_format;
            }
        }

        private sealed class UnifiedRead
        {
            public string Sources = "";
            public DateTime From;
            public DateTime Until;
            public readonly List<EventLogEntry> Entries = new();
        }

        private static readonly Dictionary<string, UnifiedRead> s_unified = new(StringComparer.OrdinalIgnoreCase);

        // `log show`'s JSON: the log's sources' entries (the source the subsystem, the log the category), from the last
        // day or the clearing on the first read, then from where the last read ended (a second's overlap, its
        // duplicates left out)
        private static IEnumerable<EventLogEntry> ReadUnified(string log, IReadOnlyCollection<string> sources, DateTime? since)
        {
            if (sources.Count == 0) return Array.Empty<EventLogEntry>();
            string key = string.Join("|", sources.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            DateTime now = DateTime.Now, from = since is { } cleared && cleared > now.AddDays(-1) ? cleared : now.AddDays(-1);
            lock (s_unified)
            {
                if (!s_unified.TryGetValue(log, out UnifiedRead read) || read.Sources != key || read.From > from)
                    s_unified[log] = read = new UnifiedRead { Sources = key, From = from, Until = from };
                var added = Unified(log, sources, read.Until.AddSeconds(-1))
                    .Where(e => !read.Entries.Any(x => x.TimeGenerated == e.TimeGenerated && x.Message == e.Message && x.Source == e.Source)).ToList();
                foreach (EventLogEntry e in added)
                    read.Entries.Add(new EventLogEntry(read.Entries.Count + 1, e.Source, e.MachineName, e.EntryType, e.InstanceId, 0, Array.Empty<byte>(), e.ReplacementStrings, e.Message, e.TimeGenerated));
                read.Until = now;
                return read.Entries.ToArray();
            }
        }

        private static List<EventLogEntry> Unified(string log, IReadOnlyCollection<string> sources, DateTime from)
        {
            static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            string predicate = $"category == {Quote(log)} AND subsystem IN {{{string.Join(", ", sources.Select(Quote))}}}";
            var args = new List<string> { "show", "--style", "ndjson", "--predicate", predicate, "--start", from.ToString("yyyy-MM-dd HH:mm:ss", Inv.InvariantCulture) };
            var entries = new List<EventLogEntry>();
            foreach (string line in Lines(LogTool, args))
            {
                if (!line.StartsWith('{')) continue;
                using var json = JsonDocument.Parse(line);
                JsonElement e = json.RootElement;
                if (!e.TryGetProperty("eventMessage", out JsonElement m) || !e.TryGetProperty("subsystem", out JsonElement sub)) continue;
                string text = m.GetString() ?? "";
                long instanceId = 0;
                // "Event <id>: " leads the text of an entry with an id (Text)
                int colon = text.IndexOf(": ", StringComparison.Ordinal);
                if (text.StartsWith("Event ", StringComparison.Ordinal) && colon > 6
                    && long.TryParse(text.AsSpan(6, colon - 6), Globalization.NumberStyles.Integer, Inv.InvariantCulture, out long id))
                {
                    instanceId = id;
                    text = text[(colon + 2)..];
                }
                bool error = e.TryGetProperty("messageType", out JsonElement t) && t.GetString() is "Error" or "Fault";
                DateTime time = e.TryGetProperty("timestamp", out JsonElement ts)
                    && DateTimeOffset.TryParse(ts.GetString(), Inv.InvariantCulture, Globalization.DateTimeStyles.None, out DateTimeOffset when) ? when.LocalDateTime : DateTime.Now;
                entries.Add(new EventLogEntry(entries.Count + 1, sub.GetString() ?? "", Environment.MachineName,
                    error ? EventLogEntryType.Error : EventLogEntryType.Information, instanceId, 0, Array.Empty<byte>(), new[] { text }, text, time));
            }
            return entries;
        }

        private static List<string> Lines(string tool, IEnumerable<string> args)
        {
            var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi);
            var error = p.StandardError.ReadToEndAsync();
            var lines = new List<string>();
            string line;
            while ((line = p.StandardOutput.ReadLine()) != null) if (line.Length > 0) lines.Add(line);
            p.WaitForExit();
            _ = error.Result;
            return lines;
        }
    }
}
