// Windows: the Event Log itself, through advapi32 as dotnet/runtime's EventLog uses it -- an entry reported with
// ReportEvent under its source, a log's entries read back with ReadEventLog (each EVENTLOGRECORD: its record number,
// times, id, type, category, source, machine, replacement strings and data), their messages formatted from the
// source's message file (EventMessageFile) where it has one, as Event Viewer formats them, and a log cleared with
// ClearEventLog. Sources and logs are registered in the registry (EventLogStore), Windows' own on Windows.

using System.Collections.Concurrent;
using System.IO;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace System.Diagnostics
{
    internal static unsafe class WindowsEventLog
    {
        private const string Advapi = "advapi32.dll";
        private const string Kernel = "kernel32.dll";

        [DllImport(Advapi, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr RegisterEventSourceW(string server, string source);

        [DllImport(Advapi, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ReportEventW(IntPtr handle, ushort type, ushort category, uint eventId, IntPtr userSid, ushort strings, uint dataSize, IntPtr[] stringPointers, byte[] data);

        [DllImport(Advapi, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenEventLogW(string server, string log);

        [DllImport(Advapi, SetLastError = true)]
        private static extern bool CloseEventLog(IntPtr handle);

        [DllImport(Advapi, SetLastError = true)]
        private static extern bool ReadEventLogW(IntPtr handle, int flags, int offset, byte* buffer, int size, out int read, out int needed);

        [DllImport(Advapi, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ClearEventLogW(IntPtr handle, string backup);

        [DllImport(Kernel, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string file, IntPtr reserved, int flags);

        [DllImport(Kernel, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int FormatMessageW(int flags, IntPtr source, uint messageId, int language, char* buffer, int size, IntPtr* arguments);

        private const int EVENTLOG_SEQUENTIAL_READ = 0x1, EVENTLOG_FORWARDS_READ = 0x4;
        private const int ERROR_INSUFFICIENT_BUFFER = 122, ERROR_HANDLE_EOF = 38;
        private const int LOAD_LIBRARY_AS_DATAFILE = 0x2;
        private const int FORMAT_MESSAGE_FROM_HMODULE = 0x800, FORMAT_MESSAGE_ARGUMENT_ARRAY = 0x2000, FORMAT_MESSAGE_IGNORE_INSERTS = 0x200;

        private static readonly ConcurrentDictionary<string, IntPtr> s_sources = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, IntPtr> s_messageFiles = new(StringComparer.OrdinalIgnoreCase);

        public static void Report(string source, EventLogEntryType type, long instanceId, short category, byte[] data, string[] strings)
        {
            IntPtr handle = s_sources.GetOrAdd(source, s =>
            {
                IntPtr h = RegisterEventSourceW(null, s);
                if (h == IntPtr.Zero) throw new Win32Exception();
                return h;
            });
            strings ??= Array.Empty<string>();
            var pointers = new IntPtr[strings.Length];
            try
            {
                for (int i = 0; i < strings.Length; i++) pointers[i] = Marshal.StringToHGlobalUni(strings[i] ?? "");
                data ??= Array.Empty<byte>();
                if (!ReportEventW(handle, (ushort)type, (ushort)category, (uint)instanceId, IntPtr.Zero, (ushort)strings.Length, (uint)data.Length, pointers, data))
                    throw new Win32Exception();
            }
            finally
            {
                foreach (IntPtr p in pointers) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            }
        }

        /// <summary>The log's entries, oldest first.</summary>
        public static EventLogEntry[] Read(string log)
        {
            IntPtr handle = OpenEventLogW(null, log);
            if (handle == IntPtr.Zero) throw new Win32Exception();
            var entries = new List<EventLogEntry>();
            try
            {
                byte[] buffer = new byte[64 * 1024];
                while (true)
                {
                    int read, needed;
                    bool ok;
                    fixed (byte* b = buffer) ok = ReadEventLogW(handle, EVENTLOG_SEQUENTIAL_READ | EVENTLOG_FORWARDS_READ, 0, b, buffer.Length, out read, out needed);
                    if (!ok)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == ERROR_INSUFFICIENT_BUFFER) { buffer = new byte[needed]; continue; }
                        if (error == ERROR_HANDLE_EOF) break;
                        throw new Win32Exception(error);
                    }
                    for (int at = 0; at < read;)
                    {
                        int length = BitConverter.ToInt32(buffer, at);
                        if (length <= 0) break;
                        entries.Add(Record(buffer, at, log));
                        at += length;
                    }
                }
            }
            finally { CloseEventLog(handle); }
            return entries.ToArray();
        }

        public static void Clear(string log)
        {
            IntPtr handle = OpenEventLogW(null, log);
            if (handle == IntPtr.Zero) throw new Win32Exception();
            try
            {
                if (!ClearEventLogW(handle, null)) throw new Win32Exception();
            }
            finally { CloseEventLog(handle); }
        }

        // EVENTLOGRECORD: Length, Reserved, RecordNumber, TimeGenerated, TimeWritten, EventID, EventType (16),
        // NumStrings (16), EventCategory (16), ReservedFlags (16), ClosingRecordNumber, StringOffset, UserSidLength,
        // UserSidOffset, DataLength, DataOffset; then SourceName and Computername (NUL-terminated UTF-16), the
        // strings at StringOffset, the data at DataOffset
        private static EventLogEntry Record(byte[] b, int at, string log)
        {
            int record = BitConverter.ToInt32(b, at + 8);
            uint generated = BitConverter.ToUInt32(b, at + 12);
            uint eventId = BitConverter.ToUInt32(b, at + 20);
            var type = (EventLogEntryType)BitConverter.ToUInt16(b, at + 24);
            int count = BitConverter.ToUInt16(b, at + 26);
            short category = BitConverter.ToInt16(b, at + 28);
            int stringOffset = BitConverter.ToInt32(b, at + 36);
            int dataLength = BitConverter.ToInt32(b, at + 48), dataOffset = BitConverter.ToInt32(b, at + 52);
            int p = at + 56;
            string source = Utf16(b, ref p);
            string machine = Utf16(b, ref p);
            var strings = new string[count];
            int s = at + stringOffset;
            for (int i = 0; i < count; i++) strings[i] = Utf16(b, ref s);
            byte[] data = new byte[dataLength];
            Array.Copy(b, at + dataOffset, data, 0, dataLength);
            DateTime time = DateTime.UnixEpoch.AddSeconds(generated).ToLocalTime();
            return new EventLogEntry(record, source, machine, type, eventId, category, data, strings, Message(log, source, eventId, strings), time);
        }

        private static string Utf16(byte[] b, ref int at)
        {
            int start = at;
            while (at + 1 < b.Length && (b[at] != 0 || b[at + 1] != 0)) at += 2;
            string s = Encoding.Unicode.GetString(b, start, at - start);
            at += 2;
            return s;
        }

        // the message as Event Viewer shows it: the source's message file's text for the id with the strings put in,
        // else (no message file, or no text for the id) the strings themselves
        private static string Message(string log, string source, uint eventId, string[] strings)
        {
            IntPtr module = MessageFile(log, source);
            if (module != IntPtr.Zero)
            {
                var pointers = new IntPtr[Math.Max(strings.Length, 1) + 99];
                var handles = new List<IntPtr>();
                try
                {
                    for (int i = 0; i < pointers.Length; i++)
                    {
                        // %n beyond the strings given stays as written, as Event Viewer leaves it
                        pointers[i] = Marshal.StringToHGlobalUni(i < strings.Length ? strings[i] : "%" + (i + 1).ToString(Globalization.CultureInfo.InvariantCulture));
                        handles.Add(pointers[i]);
                    }
                    char* buffer = stackalloc char[32 * 1024];
                    fixed (IntPtr* args = pointers)
                    {
                        int n = FormatMessageW(FORMAT_MESSAGE_FROM_HMODULE | FORMAT_MESSAGE_ARGUMENT_ARRAY, module, eventId, 0, buffer, 32 * 1024, args);
                        if (n > 0) return new string(buffer, 0, n).TrimEnd('\r', '\n');
                    }
                }
                finally { foreach (IntPtr h in handles) Marshal.FreeHGlobal(h); }
            }
            return string.Join(Environment.NewLine, strings);
        }

        private static IntPtr MessageFile(string log, string source) => s_messageFiles.GetOrAdd(log + "\\" + source, _ =>
        {
            try
            {
                using RegistryKey key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\EventLog\{log}\{source}");
                if (key?.GetValue("EventMessageFile") is not string files) return IntPtr.Zero;
                foreach (string file in files.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    IntPtr module = LoadLibraryExW(Environment.ExpandEnvironmentVariables(file.Trim()), IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
                    if (module != IntPtr.Zero) return module;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Security.SecurityException) { }
            return IntPtr.Zero;
        });
    }
}
