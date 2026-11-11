// Windows: the Event Log's own API (wevtapi.dll), as dotnet/runtime's System.Diagnostics.Eventing.Reader calls it --
// a query's events rendered as XML (EvtRender) and their descriptions formatted by their publishers' messages
// (EvtFormatMessage), the channels and publishers enumerated, a channel cleared, exported, described (EvtGetLogInfo)
// and configured (EvtOpenChannelConfig), a publisher's metadata read.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace System.Diagnostics.Eventing.Reader
{
    internal static unsafe class WindowsEvt
    {
        private const string Wevtapi = "wevtapi.dll";

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct EVT_VARIANT
        {
            [FieldOffset(0)] public ulong UInt64;
            [FieldOffset(0)] public uint UInt32;
            [FieldOffset(0)] public int Bool;
            [FieldOffset(0)] public IntPtr Pointer;
            [FieldOffset(8)] public uint Count;
            [FieldOffset(12)] public uint Type;
        }

        private const int EvtQueryChannelPath = 0x1, EvtQueryFilePath = 0x2, EvtQueryReverseDirection = 0x200, EvtQueryTolerateQueryErrors = 0x1000;
        private const int EvtRenderEventXml = 1, EvtFormatMessageEvent = 1, EvtFormatMessageId = 8;
        private const int ERROR_NO_MORE_ITEMS = 259, ERROR_INSUFFICIENT_BUFFER = 122;
        private const uint EvtVarTypeString = 1, EvtVarTypeBoolean = 13, EvtVarTypeUInt32 = 8, EvtVarTypeUInt64 = 10, EvtVarTypeFileTime = 17, EvtVarTypeGuid = 15;

        [DllImport(Wevtapi, CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr EvtQuery(IntPtr session, string path, string query, int flags);
        [DllImport(Wevtapi, SetLastError = true)] private static extern bool EvtNext(IntPtr resultSet, int count, IntPtr* events, int timeout, int flags, out int returned);
        [DllImport(Wevtapi, SetLastError = true)] private static extern bool EvtRender(IntPtr context, IntPtr fragment, int flags, int bufferSize, char* buffer, out int used, out int propertyCount);
        [DllImport(Wevtapi, SetLastError = true)] private static extern bool EvtClose(IntPtr handle);
        [DllImport(Wevtapi, CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr EvtOpenPublisherMetadata(IntPtr session, string publisher, string logFile, int locale, int flags);
        [DllImport(Wevtapi, SetLastError = true)] private static extern bool EvtFormatMessage(IntPtr metadata, IntPtr evt, uint messageId, int valueCount, EVT_VARIANT* values, int flags, int bufferSize, char* buffer, out int used);
        [DllImport(Wevtapi, SetLastError = true)] private static extern IntPtr EvtOpenChannelEnum(IntPtr session, int flags);
        [DllImport(Wevtapi, SetLastError = true)] private static extern bool EvtNextChannelPath(IntPtr channelEnum, int bufferSize, char* buffer, out int used);
        [DllImport(Wevtapi, SetLastError = true)] private static extern IntPtr EvtOpenPublisherEnum(IntPtr session, int flags);
        [DllImport(Wevtapi, SetLastError = true)] private static extern bool EvtNextPublisherId(IntPtr publisherEnum, int bufferSize, char* buffer, out int used);
        [DllImport(Wevtapi, CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool EvtClearLog(IntPtr session, string channel, string targetFile, int flags);
        [DllImport(Wevtapi, CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool EvtExportLog(IntPtr session, string path, string query, string targetFile, int flags);
        [DllImport(Wevtapi, CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool EvtArchiveExportedLog(IntPtr session, string logFile, int locale, int flags);
        [DllImport(Wevtapi, CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr EvtOpenLog(IntPtr session, string path, int flags);
        [DllImport(Wevtapi, SetLastError = true)] private static extern bool EvtGetLogInfo(IntPtr log, int property, int bufferSize, EVT_VARIANT* buffer, out int used);
        [DllImport(Wevtapi, CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr EvtOpenChannelConfig(IntPtr session, string channel, int flags);
        [DllImport(Wevtapi, SetLastError = true)] private static extern bool EvtGetChannelConfigProperty(IntPtr config, int property, int flags, int bufferSize, EVT_VARIANT* buffer, out int used);
        [DllImport(Wevtapi, SetLastError = true)] private static extern bool EvtSetChannelConfigProperty(IntPtr config, int property, int flags, EVT_VARIANT* value);
        [DllImport(Wevtapi, SetLastError = true)] private static extern bool EvtSaveChannelConfig(IntPtr config, int flags);
        [DllImport(Wevtapi, SetLastError = true)] private static extern bool EvtGetPublisherMetadataProperty(IntPtr metadata, int property, int flags, int bufferSize, EVT_VARIANT* buffer, out int used);

        private static Exception Error() => EventLogException.For(Marshal.GetLastPInvokeError());

        private static readonly ConcurrentDictionary<string, IntPtr> s_publishers = new(StringComparer.OrdinalIgnoreCase);

        // ---- queries ----------------------------------------------------------------------------------------

        public static IEnumerable<EventLogRecord> Query(EventLogQuery q)
        {
            int flags = (q.ThePathType == PathType.FilePath ? EvtQueryFilePath : EvtQueryChannelPath)
                | (q.ReverseDirection ? EvtQueryReverseDirection : 0) | (q.TolerateQueryErrors ? EvtQueryTolerateQueryErrors : 0);
            IntPtr results = EvtQuery(IntPtr.Zero, q.Path, string.IsNullOrEmpty(q.Query) ? null : q.Query, flags);
            if (results == IntPtr.Zero) throw Error();
            return Iterate(results, q.Path ?? "");
        }

        private static IEnumerable<EventLogRecord> Iterate(IntPtr results, string container)
        {
            try
            {
                var handles = new IntPtr[64];
                while (true)
                {
                    bool ok = NextBatch(results, handles, out int got);
                    if (!ok)
                    {
                        int error = Marshal.GetLastPInvokeError();
                        if (error == ERROR_NO_MORE_ITEMS) yield break;
                        throw EventLogException.For(error);
                    }
                    for (int i = 0; i < got; i++)
                    {
                        EventLogRecord record;
                        try
                        {
                            string xml = Render(handles[i]);
                            string provider = ProviderOf(xml);
                            record = new EventLogRecord(xml, Format(provider, handles[i]), container, null);
                        }
                        finally { EvtClose(handles[i]); }
                        yield return record;
                    }
                }
            }
            finally { EvtClose(results); }
        }

        private static bool NextBatch(IntPtr results, IntPtr[] handles, out int got)
        {
            fixed (IntPtr* h = handles) return EvtNext(results, handles.Length, h, -1, 0, out got);
        }

        private static string Render(IntPtr evt)
        {
            int size = 4096;
            while (true)
            {
                char[] buffer = new char[size / 2 + 1];
                fixed (char* b = buffer)
                {
                    if (EvtRender(IntPtr.Zero, evt, EvtRenderEventXml, size, b, out int used, out _))
                        return new string(b, 0, Math.Max(0, used / 2 - 1));
                    if (Marshal.GetLastPInvokeError() != ERROR_INSUFFICIENT_BUFFER) throw Error();
                    size = used;
                }
            }
        }

        private static string ProviderOf(string xml)
        {
            int at = xml.IndexOf("<Provider Name='", StringComparison.Ordinal);
            int quote = '\'';
            if (at < 0) { at = xml.IndexOf("<Provider Name=\"", StringComparison.Ordinal); quote = '"'; }
            if (at < 0) return null;
            at += "<Provider Name='".Length;
            int end = xml.IndexOf((char)quote, at);
            return end > at ? xml[at..end] : null;
        }

        private static IntPtr PublisherHandle(string provider) =>
            provider == null ? IntPtr.Zero : s_publishers.GetOrAdd(provider, p => EvtOpenPublisherMetadata(IntPtr.Zero, p, null, 0, 0));

        // the event's description as its publisher's message formats it (null where it has none, as Windows has none)
        private static string Format(string provider, IntPtr evt)
        {
            IntPtr metadata = PublisherHandle(provider);
            if (metadata == IntPtr.Zero) return null;
            return Message(metadata, evt, 0, null, EvtFormatMessageEvent);
        }

        private static string Message(IntPtr metadata, IntPtr evt, uint id, EVT_VARIANT[] values, int flags)
        {
            int size = 2048;
            while (size < 1 << 22)
            {
                char[] buffer = new char[size];
                fixed (char* b = buffer)
                fixed (EVT_VARIANT* v = values)
                {
                    if (EvtFormatMessage(metadata, evt, id, values?.Length ?? 0, v, flags, size, b, out int used))
                        return new string(b, 0, Math.Max(0, used - 1));
                    if (Marshal.GetLastPInvokeError() != ERROR_INSUFFICIENT_BUFFER) return null;
                    size = used;
                }
            }
            return null;
        }

        /// <summary>A classic provider's message for an id, these strings its inserts.</summary>
        public static string FormatWith(string provider, int id, int qualifiers, string[] strings)
        {
            IntPtr metadata = PublisherHandle(provider);
            if (metadata == IntPtr.Zero) return null;
            var values = new EVT_VARIANT[strings.Length];
            var pins = new List<IntPtr>();
            try
            {
                for (int i = 0; i < strings.Length; i++)
                {
                    IntPtr s = Marshal.StringToHGlobalUni(strings[i]);
                    pins.Add(s);
                    values[i] = new EVT_VARIANT { Pointer = s, Type = EvtVarTypeString };
                }
                return Message(metadata, IntPtr.Zero, (uint)((qualifiers << 16) | (id & 0xFFFF)), values, EvtFormatMessageId);
            }
            finally { foreach (IntPtr p in pins) Marshal.FreeHGlobal(p); }
        }

        // ---- channels and publishers ------------------------------------------------------------------------

        public static List<string> LogNames() => Enumerate(EvtOpenChannelEnum(IntPtr.Zero, 0), (h, size, b) => (EvtNextChannelPath(h, size, b, out int used), used));

        public static List<string> ProviderNames() => Enumerate(EvtOpenPublisherEnum(IntPtr.Zero, 0), (h, size, b) => (EvtNextPublisherId(h, size, b, out int used), used));

        private delegate (bool Ok, int Used) Next(IntPtr handle, int size, char* buffer);

        private static List<string> Enumerate(IntPtr handle, Next next)
        {
            if (handle == IntPtr.Zero) throw Error();
            var names = new List<string>();
            try
            {
                char[] buffer = new char[512];
                while (true)
                {
                    (bool ok, int used) result;
                    fixed (char* b = buffer) result = next(handle, buffer.Length, b);
                    if (result.ok) { names.Add(new string(buffer, 0, Math.Max(0, result.used - 1))); continue; }
                    int error = Marshal.GetLastPInvokeError();
                    if (error == ERROR_NO_MORE_ITEMS) break;
                    if (error == ERROR_INSUFFICIENT_BUFFER) { buffer = new char[result.used]; continue; }
                    throw EventLogException.For(error);
                }
            }
            finally { EvtClose(handle); }
            return names;
        }

        public static void Clear(string log, string backup)
        {
            if (!EvtClearLog(IntPtr.Zero, log, backup, 0)) throw Error();
        }

        public static void Export(string path, PathType type, string query, string target, bool tolerate)
        {
            int flags = (type == PathType.FilePath ? EvtQueryFilePath : EvtQueryChannelPath) | (tolerate ? EvtQueryTolerateQueryErrors : 0);
            if (!EvtExportLog(IntPtr.Zero, path, string.IsNullOrEmpty(query) ? "*" : query, target, flags)) throw Error();
        }

        public static void ArchiveMessages(string file)
        {
            if (!EvtArchiveExportedLog(IntPtr.Zero, file, 0, 0)) throw Error();
        }

        public static EventLogInformation Information(string log, PathType type)
        {
            IntPtr handle = EvtOpenLog(IntPtr.Zero, log, type == PathType.FilePath ? 2 : 1);
            if (handle == IntPtr.Zero) throw Error();
            try
            {
                EVT_VARIANT Get(int property)
                {
                    EVT_VARIANT v;
                    return EvtGetLogInfo(handle, property, sizeof(EVT_VARIANT), &v, out _) ? v : default;
                }
                DateTime? Time(EVT_VARIANT v) => v.Type == EvtVarTypeFileTime ? DateTime.FromFileTime((long)v.UInt64) : null;
                return new EventLogInformation
                {
                    CreationTime = Time(Get(0)), LastAccessTime = Time(Get(1)), LastWriteTime = Time(Get(2)),
                    FileSize = (long)Get(3).UInt64, Attributes = (int)Get(4).UInt32, RecordCount = (long)Get(5).UInt64,
                    OldestRecordNumber = (long)Get(6).UInt64, IsLogFull = Get(7).Bool != 0,
                };
            }
            finally { EvtClose(handle); }
        }

        // ---- a channel's configuration --------------------------------------------------------------------

        internal readonly record struct Channel(bool Enabled, int Isolation, int Type, string Owner, bool Classic, string Access, bool Retention, bool AutoBackup, long MaxSize, string Path);

        public static Channel ChannelConfig(string channel)
        {
            IntPtr config = EvtOpenChannelConfig(IntPtr.Zero, channel, 0);
            if (config == IntPtr.Zero) throw Error();
            try
            {
                var buffer = new byte[4096];
                EVT_VARIANT Get(int property)
                {
                    fixed (byte* b = buffer)
                        return EvtGetChannelConfigProperty(config, property, 0, buffer.Length, (EVT_VARIANT*)b, out _) ? *(EVT_VARIANT*)b : default;
                }
                string Text(int property)
                {
                    fixed (byte* b = buffer)
                    {
                        if (!EvtGetChannelConfigProperty(config, property, 0, buffer.Length, (EVT_VARIANT*)b, out _)) return null;
                        var v = *(EVT_VARIANT*)b;
                        return v.Type == EvtVarTypeString && v.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(v.Pointer) : null;
                    }
                }
                return new Channel(Get(0).Bool != 0, (int)Get(1).UInt32, (int)Get(2).UInt32, Text(3), Get(4).Bool != 0, Text(5),
                    Get(6).Bool != 0, Get(7).Bool != 0, (long)Get(8).UInt64, Text(9));
            }
            finally { EvtClose(config); }
        }

        public static void SaveChannelConfig(string channel, bool enabled, EventLogMode mode, long maximumSize, string path, string security)
        {
            IntPtr config = EvtOpenChannelConfig(IntPtr.Zero, channel, 0);
            if (config == IntPtr.Zero) throw Error();
            var pins = new List<IntPtr>();
            try
            {
                void Set(int property, EVT_VARIANT v)
                {
                    if (!EvtSetChannelConfigProperty(config, property, 0, &v)) throw Error();
                }
                EVT_VARIANT Str(string s) { IntPtr p = Marshal.StringToHGlobalUni(s); pins.Add(p); return new EVT_VARIANT { Pointer = p, Type = EvtVarTypeString }; }
                Set(0, new EVT_VARIANT { Bool = enabled ? 1 : 0, Type = EvtVarTypeBoolean });
                Set(6, new EVT_VARIANT { Bool = mode != EventLogMode.Circular ? 1 : 0, Type = EvtVarTypeBoolean });
                Set(7, new EVT_VARIANT { Bool = mode == EventLogMode.AutoBackup ? 1 : 0, Type = EvtVarTypeBoolean });
                Set(8, new EVT_VARIANT { UInt64 = (ulong)maximumSize, Type = EvtVarTypeUInt64 });
                if (path != null) Set(9, Str(path));
                if (security != null) Set(5, Str(security));
                if (!EvtSaveChannelConfig(config, 0)) throw Error();
            }
            finally
            {
                foreach (IntPtr p in pins) Marshal.FreeHGlobal(p);
                EvtClose(config);
            }
        }

        // ---- a publisher's metadata -----------------------------------------------------------------------

        public static (Guid, string, string, string, string, string, string) Publisher(string name)
        {
            IntPtr metadata = EvtOpenPublisherMetadata(IntPtr.Zero, name, null, 0, 0);
            if (metadata == IntPtr.Zero) throw Error();
            try
            {
                var buffer = new byte[4096];
                EVT_VARIANT Get(int property)
                {
                    fixed (byte* b = buffer)
                        return EvtGetPublisherMetadataProperty(metadata, property, 0, buffer.Length, (EVT_VARIANT*)b, out _) ? *(EVT_VARIANT*)b : default;
                }
                string Text(int property)
                {
                    var v = Get(property);
                    return v.Type == EvtVarTypeString && v.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(v.Pointer) : null;
                }
                var g = Get(0);
                Guid id = g.Type == EvtVarTypeGuid && g.Pointer != IntPtr.Zero ? *(Guid*)g.Pointer : Guid.Empty;
                uint messageId = Get(5).UInt32;
                string display = messageId != 0 && messageId != uint.MaxValue ? Message(metadata, IntPtr.Zero, messageId, null, EvtFormatMessageId) : null;
                string log = EventLogStore.LogOfSource(name);
                return (id, Text(3), Text(2), Text(1), Text(4), display, log);
            }
            finally { EvtClose(metadata); }
        }
    }
}
