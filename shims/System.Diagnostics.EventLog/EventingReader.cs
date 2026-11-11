// System.Diagnostics.Eventing.Reader: the event log read as Windows Vista's API reads it -- queries (a log and an
// XPath, or a structured QueryList), readers, watchers, bookmarks, sessions, a log's configuration and information,
// a provider's metadata -- over an event's XML, as Windows renders it (the Event schema's System and EventData):
//
//   Windows   the Event Log itself (WindowsEvt: wevtapi -- EvtQuery, EvtNext, EvtRender, EvtFormatMessage, the
//             channel configuration, EvtClearLog, EvtExportLog), every channel and .evtx file Windows has.
//   elsewhere the classic logs the OS keeps (EventLogStore: journald, the unified log), each entry rendered as Windows
//             renders a classic event -- its source the provider, its instance id the event id (and qualifiers), its
//             type the level (an audit's keywords), its category the task, its strings the EventData -- and the query's
//             XPath evaluated over that XML (with Windows' timediff and band); a log exported as XML and read back.

using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using System.Xml.Xsl;

namespace System.Diagnostics.Eventing.Reader
{
    // ---- enumerations (as Windows has them) --------------------------------------------------------------------

    public enum EventLogIsolation { Application = 0, System = 1, Custom = 2 }
    public enum EventLogMode { Circular = 0, AutoBackup = 1, Retain = 2 }
    public enum EventLogType { Administrative = 0, Operational = 1, Analytical = 2, Debug = 3 }
    public enum PathType { LogName = 1, FilePath = 2 }
    public enum SessionAuthentication { Default = 0, Negotiate = 1, Kerberos = 2, Ntlm = 3 }

    [Flags]
    public enum StandardEventKeywords : long
    {
        None = 0,
        ResponseTime = 281474976710656,
        WdiContext = 562949953421312,
        WdiDiagnostic = 1125899906842624,
        Sqm = 2251799813685248,
        AuditFailure = 4503599627370496,
        [Obsolete("StandardEventKeywords.CorrelationHint has an incorrect value and has been deprecated. Use CorrelationHint2 instead.")]
        CorrelationHint = 4503599627370496,
        AuditSuccess = 9007199254740992,
        CorrelationHint2 = 18014398509481984,
        EventLogClassic = 36028797018963968,
    }

    public enum StandardEventLevel { LogAlways = 0, Critical = 1, Error = 2, Warning = 3, Informational = 4, Verbose = 5 }

    public enum StandardEventOpcode
    {
        Info = 0, Start = 1, Stop = 2, DataCollectionStart = 3, DataCollectionStop = 4, Extension = 5, Reply = 6,
        Resume = 7, Suspend = 8, Send = 9, Receive = 240,
    }

    public enum StandardEventTask { None = 0 }

    // ---- exceptions -------------------------------------------------------------------------------------------

    public class EventLogException : Exception
    {
        private readonly int _errorCode;

        public EventLogException() { }
        protected EventLogException(int errorCode) { _errorCode = errorCode; HResult = errorCode; }
        public EventLogException(string message) : base(message) { }
        public EventLogException(string message, Exception innerException) : base(message, innerException) { }

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        protected EventLogException(Runtime.Serialization.SerializationInfo serializationInfo, Runtime.Serialization.StreamingContext streamingContext)
#pragma warning disable SYSLIB0051
            : base(serializationInfo, streamingContext) { _errorCode = serializationInfo.GetInt32("errorCode"); }
#pragma warning restore SYSLIB0051

        public override string Message => _errorCode != 0 ? new Win32Exception(_errorCode).Message : base.Message;

        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        public override void GetObjectData(Runtime.Serialization.SerializationInfo info, Runtime.Serialization.StreamingContext context)
        {
#pragma warning disable SYSLIB0051
            base.GetObjectData(info, context);
#pragma warning restore SYSLIB0051
            info.AddValue("errorCode", _errorCode);
        }

        // Windows' error, as the exception dotnet/runtime makes of it
        internal static Exception For(int error) => error switch
        {
            2 or 3 or 15007 or 15008 or 15027 => new EventLogNotFoundException(error),          // file/path/channel/publisher not found
            15001 or 15002 or 15003 or 15004 or 15005 or 15006 => new EventLogInvalidDataException(error),  // the query is not valid
            15037 => new EventLogProviderDisabledException(error),
            5 => new UnauthorizedAccessException(),
            _ => new EventLogException(error),
        };
    }

    public class EventLogInvalidDataException : EventLogException
    {
        public EventLogInvalidDataException() { }
        internal EventLogInvalidDataException(int errorCode) : base(errorCode) { }
        public EventLogInvalidDataException(string message) : base(message) { }
        public EventLogInvalidDataException(string message, Exception innerException) : base(message, innerException) { }
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        protected EventLogInvalidDataException(Runtime.Serialization.SerializationInfo serializationInfo, Runtime.Serialization.StreamingContext streamingContext) : base(serializationInfo, streamingContext) { }
    }

    public class EventLogNotFoundException : EventLogException
    {
        public EventLogNotFoundException() { }
        internal EventLogNotFoundException(int errorCode) : base(errorCode) { }
        public EventLogNotFoundException(string message) : base(message) { }
        public EventLogNotFoundException(string message, Exception innerException) : base(message, innerException) { }
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        protected EventLogNotFoundException(Runtime.Serialization.SerializationInfo serializationInfo, Runtime.Serialization.StreamingContext streamingContext) : base(serializationInfo, streamingContext) { }
    }

    public class EventLogProviderDisabledException : EventLogException
    {
        public EventLogProviderDisabledException() { }
        internal EventLogProviderDisabledException(int errorCode) : base(errorCode) { }
        public EventLogProviderDisabledException(string message) : base(message) { }
        public EventLogProviderDisabledException(string message, Exception innerException) : base(message, innerException) { }
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        protected EventLogProviderDisabledException(Runtime.Serialization.SerializationInfo serializationInfo, Runtime.Serialization.StreamingContext streamingContext) : base(serializationInfo, streamingContext) { }
    }

    public class EventLogReadingException : EventLogException
    {
        public EventLogReadingException() { }
        internal EventLogReadingException(int errorCode) : base(errorCode) { }
        public EventLogReadingException(string message) : base(message) { }
        public EventLogReadingException(string message, Exception innerException) : base(message, innerException) { }
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        protected EventLogReadingException(Runtime.Serialization.SerializationInfo serializationInfo, Runtime.Serialization.StreamingContext streamingContext) : base(serializationInfo, streamingContext) { }
    }

    // ---- small values ----------------------------------------------------------------------------------------

    public sealed class EventBookmark
    {
        public EventBookmark(string bookmarkXml)
        {
            ArgumentNullException.ThrowIfNull(bookmarkXml);
            BookmarkXml = bookmarkXml;
        }

        public string BookmarkXml { get; }

        internal static EventBookmark For(string channel, long recordId) =>
            new EventBookmark($"<BookmarkList>\r\n  <Bookmark Channel='{SecurityElementEscape(channel)}' RecordId='{recordId}' IsCurrent='true'/>\r\n</BookmarkList>");

        internal long? RecordId
        {
            get
            {
                try
                {
                    var b = XDocument.Parse(BookmarkXml).Descendants("Bookmark").FirstOrDefault(e => (string)e.Attribute("IsCurrent") == "true")
                        ?? XDocument.Parse(BookmarkXml).Descendants("Bookmark").FirstOrDefault();
                    return b != null && long.TryParse((string)b.Attribute("RecordId"), out long id) ? id : null;
                }
                catch (XmlException) { return null; }
            }
        }

        private static string SecurityElementEscape(string s) => Security.SecurityElement.Escape(s) ?? "";
    }

    public sealed class EventKeyword
    {
        internal EventKeyword(string name, long value, string displayName) { Name = name; Value = value; DisplayName = displayName; }
        public string DisplayName { get; }
        public string Name { get; }
        public long Value { get; }
    }

    public sealed class EventLevel
    {
        internal EventLevel(string name, int value, string displayName) { Name = name; Value = value; DisplayName = displayName; }
        public string DisplayName { get; }
        public string Name { get; }
        public int Value { get; }
    }

    public sealed class EventOpcode
    {
        internal EventOpcode(string name, int value, string displayName) { Name = name; Value = value; DisplayName = displayName; }
        public string DisplayName { get; }
        public string Name { get; }
        public int Value { get; }
    }

    public sealed class EventTask
    {
        internal EventTask(string name, int value, string displayName, Guid eventGuid) { Name = name; Value = value; DisplayName = displayName; EventGuid = eventGuid; }
        public string DisplayName { get; }
        public Guid EventGuid { get; }
        public string Name { get; }
        public int Value { get; }
    }

    public sealed class EventLogLink
    {
        internal EventLogLink(string logName, bool isImported, string displayName) { LogName = logName; IsImported = isImported; DisplayName = displayName; }
        public string DisplayName { get; }
        public bool IsImported { get; }
        public string LogName { get; }
    }

    public sealed class EventMetadata
    {
        internal EventMetadata() { }
        public string Description { get; internal set; }
        public long Id { get; internal set; }
        public IEnumerable<EventKeyword> Keywords { get; internal set; } = Array.Empty<EventKeyword>();
        public EventLevel Level { get; internal set; }
        public EventLogLink LogLink { get; internal set; }
        public EventOpcode Opcode { get; internal set; }
        public EventTask Task { get; internal set; }
        public string Template { get; internal set; }
        public byte Version { get; internal set; }
    }

    public sealed class EventProperty
    {
        internal EventProperty(object value) => Value = value;
        public object Value { get; }
    }

    public sealed class EventLogStatus
    {
        internal EventLogStatus(string logName, int statusCode) { LogName = logName; StatusCode = statusCode; }
        public string LogName { get; }
        public int StatusCode { get; }
    }

    public sealed class EventLogInformation
    {
        internal EventLogInformation() { }
        public int? Attributes { get; internal set; }
        public DateTime? CreationTime { get; internal set; }
        public long? FileSize { get; internal set; }
        public bool? IsLogFull { get; internal set; }
        public DateTime? LastAccessTime { get; internal set; }
        public DateTime? LastWriteTime { get; internal set; }
        public long? OldestRecordNumber { get; internal set; }
        public long? RecordCount { get; internal set; }
    }

    public sealed class EventRecordWrittenEventArgs : EventArgs
    {
        internal EventRecordWrittenEventArgs(EventRecord record, Exception exception) { EventRecord = record; EventException = exception; }
        public Exception EventException { get; }
        public EventRecord EventRecord { get; }
    }

    // ---- records ---------------------------------------------------------------------------------------------

    public abstract class EventRecord : IDisposable
    {
        protected EventRecord() { }
        public abstract Guid? ActivityId { get; }
        public abstract EventBookmark Bookmark { get; }
        public abstract int Id { get; }
        public abstract long? Keywords { get; }
        public abstract IEnumerable<string> KeywordsDisplayNames { get; }
        public abstract byte? Level { get; }
        public abstract string LevelDisplayName { get; }
        public abstract string LogName { get; }
        public abstract string MachineName { get; }
        public abstract short? Opcode { get; }
        public abstract string OpcodeDisplayName { get; }
        public abstract int? ProcessId { get; }
        public abstract IList<EventProperty> Properties { get; }
        public abstract Guid? ProviderId { get; }
        public abstract string ProviderName { get; }
        public abstract int? Qualifiers { get; }
        public abstract long? RecordId { get; }
        public abstract Guid? RelatedActivityId { get; }
        public abstract int? Task { get; }
        public abstract string TaskDisplayName { get; }
        public abstract int? ThreadId { get; }
        public abstract DateTime? TimeCreated { get; }
        public abstract SecurityIdentifier UserId { get; }
        public abstract byte? Version { get; }
        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { }
        public abstract string FormatDescription();
        public abstract string FormatDescription(IEnumerable<object> values);
        public abstract string ToXml();
    }

    /// <summary>An event as its XML has it (the Event schema), its description as the provider's messages format it.</summary>
    public class EventLogRecord : EventRecord
    {
        private readonly string _xml;
        private readonly XElement _event;      // the event, its namespace taken off (what a query's XPath names)
        private readonly string _description;
        private readonly string _containerLog;
        private readonly int[] _matched;

        internal EventLogRecord(string xml, string description, string containerLog, int[] matchedQueryIds)
        {
            _xml = xml;
            _event = EventXml.Bare(xml);
            _description = description;
            _containerLog = containerLog;
            _matched = matchedQueryIds ?? Array.Empty<int>();
        }

        internal XElement Event => _event;
        private XElement System => _event.Element("System");
        private string S(string element) => (string)System?.Element(element);
        private string A(string element, string attribute) => (string)System?.Element(element)?.Attribute(attribute);
        private static int? Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
        private static long? Long(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;
        private static Guid? Guid_(string s) => Guid.TryParse(s, out Guid g) ? g : null;

        public override Guid? ActivityId => Guid_(A("Correlation", "ActivityID"));
        public override EventBookmark Bookmark => RecordId is long id ? EventBookmark.For(LogName ?? _containerLog, id) : null;
        public string ContainerLog => _containerLog;
        public override int Id => Int(S("EventID")) ?? 0;
        public override long? Keywords
        {
            get
            {
                string k = S("Keywords");
                if (k == null) return null;
                return k.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(k.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong v) ? (long)v : Long(k);
            }
        }
        public override IEnumerable<string> KeywordsDisplayNames => EventXml.KeywordNames(Keywords ?? 0);
        public override byte? Level => byte.TryParse(S("Level"), out byte l) ? l : null;
        public override string LevelDisplayName => EventXml.LevelName(Level ?? 0, Keywords ?? 0);
        public override string LogName => S("Channel");
        public override string MachineName => S("Computer");
        public IEnumerable<int> MatchedQueryIds => _matched;
        public override short? Opcode => short.TryParse(S("Opcode"), out short o) ? o : null;
        public override string OpcodeDisplayName => EventXml.OpcodeName(Opcode ?? 0);
        public override int? ProcessId => Int(A("Execution", "ProcessID"));
        public override IList<EventProperty> Properties => EventXml.Values(_event).Select(v => new EventProperty(v)).ToList();
        public override Guid? ProviderId => Guid_(A("Provider", "Guid"));
        public override string ProviderName => A("Provider", "Name");
        public override int? Qualifiers => Int(A("EventID", "Qualifiers"));
        public override long? RecordId => Long(S("EventRecordID"));
        public override Guid? RelatedActivityId => Guid_(A("Correlation", "RelatedActivityID"));
        public override int? Task => Int(S("Task"));
        public override string TaskDisplayName => EventXml.TaskName(Task ?? 0, Keywords ?? 0);
        public override int? ThreadId => Int(A("Execution", "ThreadID"));
        public override DateTime? TimeCreated =>
            DateTime.TryParse(A("TimeCreated", "SystemTime"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t)
                ? t.ToLocalTime() : null;
        public override SecurityIdentifier UserId
        {
            get
            {
                string sid = A("Security", "UserID");
                if (string.IsNullOrEmpty(sid)) return null;
                try { return new SecurityIdentifier(sid); } catch (ArgumentException) { return null; }
            }
        }
        public override byte? Version => byte.TryParse(S("Version"), out byte v) ? v : null;

        protected override void Dispose(bool disposing) { }

        public override string FormatDescription() => _description;

        // the description with these values for its inserts: the provider's message for the id (Windows), else the
        // values as a classic event's strings are its message
        public override string FormatDescription(IEnumerable<object> values)
        {
            if (values == null) return FormatDescription();
            string[] strings = values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "").ToArray();
            return OperatingSystem.IsWindows() ? WindowsEvt.FormatWith(ProviderName, Id, Qualifiers ?? 0, strings) ?? string.Join(Environment.NewLine, strings)
                : string.Join(Environment.NewLine, strings);
        }

        public IList<object> GetPropertyValues(EventLogPropertySelector propertySelector)
        {
            ArgumentNullException.ThrowIfNull(propertySelector);
            return propertySelector.Select(_event);
        }

        public override string ToXml() => _xml;
    }

    public class EventLogPropertySelector : IDisposable
    {
        private readonly string[] _queries;

        public EventLogPropertySelector(IEnumerable<string> propertyQueries)
        {
            ArgumentNullException.ThrowIfNull(propertyQueries);
            _queries = propertyQueries.ToArray();
        }

        // each query an XPath over the event (Event/System/..., Event/EventData/Data[@Name='x']): its value, typed as
        // Windows types the System's (numbers, times, GUIDs), or null
        internal IList<object> Select(XElement e)
        {
            var root = new XDocument(e).CreateNavigator();
            var values = new List<object>();
            foreach (string q in _queries)
            {
                object v = null;
                try
                {
                    var node = root.SelectSingleNode(q.StartsWith("/", StringComparison.Ordinal) ? q : "/" + q);
                    if (node != null) v = EventXml.Typed(node.Name, node.Value);
                }
                catch (XPathException) { throw new EventLogInvalidDataException(15001); }
                values.Add(v);
            }
            return values;
        }

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { }
    }

    // ---- queries, sessions, readers, watchers ----------------------------------------------------------------

    public class EventLogQuery
    {
        public EventLogQuery(string path, PathType pathType) : this(path, pathType, null) { }

        public EventLogQuery(string path, PathType pathType, string query)
        {
            if (path == null && string.IsNullOrEmpty(query)) throw new ArgumentNullException(nameof(path));
            Path = path;
            ThePathType = pathType;
            Query = query;
            Session = EventLogSession.GlobalSession;
        }

        internal string Path { get; }
        internal PathType ThePathType { get; }
        internal string Query { get; }
        public bool ReverseDirection { get; set; }
        public EventLogSession Session { get; set; }
        public bool TolerateQueryErrors { get; set; }
    }

    public class EventLogSession : IDisposable
    {
        private static readonly EventLogSession s_global = new EventLogSession();

        public EventLogSession() { }

        public EventLogSession(string server) : this(server, null, null, null, SessionAuthentication.Default) { }

        public EventLogSession(string server, string domain, string user, Security.SecureString password, SessionAuthentication logOnType)
        {
            // this machine only: another one's log is reached through its own session (RPC), which there is none of here
            if (!string.IsNullOrEmpty(server) && server != "." && !string.Equals(server, "localhost", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(server, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                throw EventLogException.For(1722);   // RPC_S_SERVER_UNAVAILABLE
        }

        public static EventLogSession GlobalSession => s_global;

        public void CancelCurrentOperations() { }

        public void ClearLog(string logName) => ClearLog(logName, null);

        public void ClearLog(string logName, string backupPath)
        {
            ArgumentNullException.ThrowIfNull(logName);
            if (OperatingSystem.IsWindows()) { WindowsEvt.Clear(logName, backupPath); return; }
            if (!string.IsNullOrEmpty(backupPath)) ExportLog(logName, PathType.LogName, "*", backupPath);
            if (!EventLogStore.LogExists(logName)) throw new EventLogNotFoundException(15007);
            EventLogStore.Log(logName, create: false).Clear();
        }

        public void ExportLog(string path, PathType pathType, string query, string targetFilePath) => ExportLog(path, pathType, query, targetFilePath, false);

        public void ExportLog(string path, PathType pathType, string query, string targetFilePath, bool tolerateQueryErrors)
        {
            ArgumentNullException.ThrowIfNull(path);
            ArgumentNullException.ThrowIfNull(targetFilePath);
            if (OperatingSystem.IsWindows()) { WindowsEvt.Export(path, pathType, query, targetFilePath, tolerateQueryErrors); return; }
            // off Windows an exported log is its events' XML (an .evtx is Windows' own format), which a reader reads back
            var events = new XElement("Events", EventStore.Read(new EventLogQuery(path, pathType, query) { TolerateQueryErrors = tolerateQueryErrors })
                .Select(r => XElement.Parse(r.ToXml())));
            events.Save(targetFilePath);
        }

        public void ExportLogAndMessages(string path, PathType pathType, string query, string targetFilePath) =>
            ExportLogAndMessages(path, pathType, query, targetFilePath, false, CultureInfo.CurrentCulture);

        public void ExportLogAndMessages(string path, PathType pathType, string query, string targetFilePath, bool tolerateQueryErrors, CultureInfo targetCultureInfo)
        {
            if (OperatingSystem.IsWindows()) { WindowsEvt.Export(path, pathType, query, targetFilePath, tolerateQueryErrors); WindowsEvt.ArchiveMessages(targetFilePath); return; }
            ExportLog(path, pathType, query, targetFilePath, tolerateQueryErrors);
        }

        public EventLogInformation GetLogInformation(string logName, PathType pathType)
        {
            ArgumentNullException.ThrowIfNull(logName);
            if (OperatingSystem.IsWindows()) return WindowsEvt.Information(logName, pathType);
            var records = EventStore.Read(new EventLogQuery(logName, pathType)).ToList();
            DateTime? first = records.Count > 0 ? records.Min(r => r.TimeCreated) : null, last = records.Count > 0 ? records.Max(r => r.TimeCreated) : null;
            return new EventLogInformation
            {
                RecordCount = records.Count, OldestRecordNumber = records.Count > 0 ? records.Min(r => r.RecordId) : 0, IsLogFull = false,
                CreationTime = first, LastWriteTime = last, LastAccessTime = DateTime.Now, Attributes = 0, FileSize = records.Sum(r => (long)r.ToXml().Length),
            };
        }

        public IEnumerable<string> GetLogNames() => OperatingSystem.IsWindows() ? WindowsEvt.LogNames() : EventLogStore.LogNames().ToList();

        public IEnumerable<string> GetProviderNames() =>
            OperatingSystem.IsWindows() ? WindowsEvt.ProviderNames()
            : EventLogStore.LogNames().SelectMany(EventLogStore.SourcesOf).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { }
    }

    public class EventLogReader : IDisposable
    {
        private readonly EventLogQuery _query;
        private IEnumerator<EventLogRecord> _records;
        private long _position;

        public EventLogReader(string path) : this(new EventLogQuery(path, PathType.LogName), null) { }
        public EventLogReader(string path, PathType pathType) : this(new EventLogQuery(path, pathType), null) { }
        public EventLogReader(EventLogQuery eventQuery) : this(eventQuery, null) { }

        public EventLogReader(EventLogQuery eventQuery, EventBookmark bookmark)
        {
            ArgumentNullException.ThrowIfNull(eventQuery);
            _query = eventQuery;
            Restart();
            LogStatus = EventStore.Paths(eventQuery).Select(p => new EventLogStatus(p, 0)).ToList();
            if (bookmark != null) Seek(bookmark, 0);
        }

        public int BatchSize { get; set; } = 64;
        public IList<EventLogStatus> LogStatus { get; }

        public void CancelReading() { }

        public EventRecord ReadEvent() => ReadEvent(TimeSpan.MaxValue);

        public EventRecord ReadEvent(TimeSpan timeout)
        {
            if (_records == null || !_records.MoveNext()) return null;
            _position++;
            return _records.Current;
        }

        public void Seek(EventBookmark bookmark) => Seek(bookmark, 0);

        // to the bookmarked event (the next read the one after it), then offset more
        public void Seek(EventBookmark bookmark, long offset)
        {
            ArgumentNullException.ThrowIfNull(bookmark);
            long? id = bookmark.RecordId;
            Restart();
            while (_records.MoveNext())
            {
                _position++;
                if (_records.Current.RecordId == id) break;
            }
            Skip(offset);
        }

        public void Seek(SeekOrigin origin, long offset)
        {
            switch (origin)
            {
                case SeekOrigin.Begin: Restart(); Skip(offset); break;
                case SeekOrigin.Current:
                    long at = _position + offset;
                    Restart(); Skip(at);
                    break;
                case SeekOrigin.End:
                    long count = EventStore.Read(_query).LongCount();
                    Restart(); Skip(count + offset);
                    break;
            }
        }

        private void Restart()
        {
            _records?.Dispose();
            _records = EventStore.Read(_query).GetEnumerator();
            _position = 0;
        }

        private void Skip(long count)
        {
            for (long i = 0; i < count && _records.MoveNext(); i++) _position++;
        }

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { _records?.Dispose(); _records = null; }
    }

    /// <summary>
    /// The events written to a query's logs, as they come: off Windows each new entry of its logs (OsLog.Follow:
    /// journalctl -f, log stream) matched against the query; on Windows the Event Log's own query, newest first, each
    /// second, read until a record already seen.
    /// </summary>
    public class EventLogWatcher : IDisposable
    {
        private readonly EventLogQuery _query;
        private readonly EventBookmark _bookmark;
        private readonly bool _readExisting;
        private EventHandler<EventRecordWrittenEventArgs> _handler;
        private readonly List<IDisposable> _followers = new();
        private Timer _timer;
        private readonly Dictionary<string, long> _newest = new(StringComparer.OrdinalIgnoreCase);
        private int _busy;

        public EventLogWatcher(string path) : this(new EventLogQuery(path, PathType.LogName)) { }
        public EventLogWatcher(EventLogQuery eventQuery) : this(eventQuery, null, false) { }
        public EventLogWatcher(EventLogQuery eventQuery, EventBookmark bookmark) : this(eventQuery, bookmark, false) { }

        public EventLogWatcher(EventLogQuery eventQuery, EventBookmark bookmark, bool readExistingEvents)
        {
            ArgumentNullException.ThrowIfNull(eventQuery);
            if (eventQuery.ReverseDirection) throw new InvalidOperationException();
            _query = eventQuery;
            _bookmark = bookmark;
            _readExisting = readExistingEvents;
        }

        public bool Enabled
        {
            get => _timer != null || _followers.Count > 0;
            set
            {
                if (value == Enabled) return;
                if (!value) { Stop(); return; }
                // what is there already: raised where asked for (after the bookmark, where one is given), else passed over
                var existing = EventStore.Read(_query).ToList();
                foreach (var r in existing)
                    if (r.LogName != null && r.RecordId is long id) _newest[r.LogName] = Math.Max(_newest.GetValueOrDefault(r.LogName), id);
                if (_readExisting || _bookmark != null)
                {
                    long? after = _bookmark?.RecordId;
                    bool past = after == null;
                    foreach (var r in existing)
                    {
                        if (past) Raise(r, null);
                        else if (r.RecordId == after) past = true;
                    }
                }
                if (OperatingSystem.IsWindows())
                    _timer = new Timer(_ => PollWindows(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
                else
                {
                    var selects = EventStore.Selects(_query);
                    foreach (string log in selects.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var mine = selects.Where(x => string.Equals(x.Path, log, StringComparison.OrdinalIgnoreCase)).ToList();
                        _followers.Add(OsLog.Follow(log, includeOwn: true, entry => OnEntry(log, entry, mine)));
                    }
                }
            }
        }

        public event EventHandler<EventRecordWrittenEventArgs> EventRecordWritten
        {
            add => _handler += value;
            remove => _handler -= value;
        }

        private void OnEntry(string log, EventLogEntry entry, List<EventStore.Select> selects)
        {
            try
            {
                string xml = EventXml.Classic(log, entry);
                var bare = EventXml.Bare(xml);
                var matched = new List<int>();
                foreach (var s in selects)
                {
                    if (!EventXPath.Matches(bare, s.XPath, _query.TolerateQueryErrors)) continue;
                    if (s.Suppress) return;
                    matched.Add(s.QueryId);
                }
                if (matched.Count > 0) Raise(new EventLogRecord(xml, entry.Message, log, matched.Distinct().ToArray()), null);
            }
            catch (Exception e) { Raise(null, e); }
        }

        private void PollWindows()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            try
            {
                var reverse = new EventLogQuery(_query.Path, _query.ThePathType, _query.Query) { ReverseDirection = true, TolerateQueryErrors = _query.TolerateQueryErrors };
                var fresh = new List<EventLogRecord>();
                foreach (var r in EventStore.Read(reverse))
                {
                    if (r.RecordId is long id && r.LogName != null && _newest.TryGetValue(r.LogName, out long seen) && id <= seen) break;
                    fresh.Add(r);
                }
                fresh.Reverse();
                foreach (var r in fresh)
                {
                    if (r.LogName != null && r.RecordId is long id) _newest[r.LogName] = Math.Max(_newest.GetValueOrDefault(r.LogName), id);
                    Raise(r, null);
                }
            }
            catch (Exception e) { Raise(null, e); }
            finally { Volatile.Write(ref _busy, 0); }
        }

        private void Raise(EventRecord r, Exception e) => _handler?.Invoke(this, new EventRecordWrittenEventArgs(r, e));

        private void Stop()
        {
            _timer?.Dispose();
            _timer = null;
            foreach (var f in _followers) f.Dispose();
            _followers.Clear();
        }

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { if (disposing) Stop(); }
    }

    // ---- a log's configuration, a provider's metadata ------------------------------------------------------

    public class EventLogConfiguration : IDisposable
    {
        private readonly string _name;
        private long _maximumSize;
        private bool _enabled = true;
        private EventLogMode _mode;
        private string _path;
        private string _security;

        public EventLogConfiguration(string logName) : this(logName, null) { }

        public EventLogConfiguration(string logName, EventLogSession session)
        {
            ArgumentNullException.ThrowIfNull(logName);
            _name = logName;
            if (OperatingSystem.IsWindows())
            {
                var c = WindowsEvt.ChannelConfig(logName);
                _enabled = c.Enabled; IsClassicLog = c.Classic; LogIsolation = (EventLogIsolation)c.Isolation; LogType = (EventLogType)c.Type;
                OwningProviderName = c.Owner; _security = c.Access; _mode = c.AutoBackup ? EventLogMode.AutoBackup : c.Retention ? EventLogMode.Retain : EventLogMode.Circular;
                _maximumSize = c.MaxSize; _path = c.Path;
                ProviderNames = c.Classic ? EventLogStore.SourcesOf(logName).ToList() : Array.Empty<string>();
                return;
            }
            if (!EventLogStore.LogExists(logName)) throw new EventLogNotFoundException(15007);
            IsClassicLog = true;
            LogIsolation = EventLogIsolation.Application;
            LogType = EventLogType.Administrative;
            var log = EventLogStore.Log(logName, create: false);
            _maximumSize = log.MaximumKilobytes * 1024;
            _mode = log.Overflow == OverflowAction.DoNotOverwrite ? EventLogMode.Retain : EventLogMode.Circular;
            _path = $@"%SystemRoot%\System32\Winevt\Logs\{logName}.evtx";
            _security = "O:BAG:SYD:(A;;0x2;;;S-1-15-2-1)(A;;0xf0007;;;SY)(A;;0x7;;;BA)(A;;0x7;;;SO)(A;;0x3;;;IU)(A;;0x3;;;SU)(A;;0x3;;;S-1-5-3)(A;;0x3;;;S-1-5-33)(A;;0x1;;;S-1-5-32-573)";
            ProviderNames = EventLogStore.SourcesOf(logName).ToList();
        }

        public bool IsClassicLog { get; }
        public bool IsEnabled { get => _enabled; set => _enabled = value; }
        public string LogFilePath { get => _path; set => _path = value; }
        public EventLogIsolation LogIsolation { get; }
        public EventLogMode LogMode { get => _mode; set => _mode = value; }
        public string LogName => _name;
        public EventLogType LogType { get; }
        public long MaximumSizeInBytes { get => _maximumSize; set => _maximumSize = value; }
        public string OwningProviderName { get; }
        public int? ProviderBufferSize => null;
        public Guid? ProviderControlGuid => null;
        public long? ProviderKeywords { get => null; set { } }
        public int? ProviderLatency => null;
        public int? ProviderLevel { get => null; set { } }
        public int? ProviderMaximumNumberOfBuffers => null;
        public int? ProviderMinimumNumberOfBuffers => null;
        public IEnumerable<string> ProviderNames { get; }
        public string SecurityDescriptor { get => _security; set => _security = value; }

        public void SaveChanges()
        {
            if (OperatingSystem.IsWindows())
            {
                WindowsEvt.SaveChannelConfig(_name, _enabled, _mode, _maximumSize, _path, _security);
                return;
            }
            var log = EventLogStore.Log(_name, create: false);
            log.MaximumKilobytes = Math.Max(64, _maximumSize / 1024);
            log.Overflow = _mode == EventLogMode.Retain ? OverflowAction.DoNotOverwrite : OverflowAction.OverwriteAsNeeded;
        }

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { }
    }

    public class ProviderMetadata : IDisposable
    {
        public ProviderMetadata(string providerName) : this(providerName, null, null) { }

        public ProviderMetadata(string providerName, EventLogSession session, CultureInfo targetCultureInfo)
        {
            ArgumentNullException.ThrowIfNull(providerName);
            Name = providerName;
            var (id, message, parameter, resource, help, display, log) = OperatingSystem.IsWindows() ? WindowsEvt.Publisher(providerName) : Classic(providerName);
            Id = id; MessageFilePath = message; ParameterFilePath = parameter; ResourceFilePath = resource; DisplayName = display;
            HelpLink = string.IsNullOrEmpty(help) ? null : new Uri(help, UriKind.RelativeOrAbsolute);
            LogLinks = log != null ? new List<EventLogLink> { new EventLogLink(log, false, log) } : new List<EventLogLink>();
        }

        // a classic source: its log and message file, as it was registered
        private static (Guid, string, string, string, string, string, string) Classic(string source)
        {
            string log = EventLogStore.LogOfSource(source) ?? throw new EventLogNotFoundException(15027);   // ERROR_EVT_PUBLISHER_METADATA_NOT_FOUND
            string message = EventLogStore.SourceValue(log, source, "EventMessageFile");
            string parameter = EventLogStore.SourceValue(log, source, "ParameterMessageFile");
            return (Guid.Empty, message, parameter, message, null, null, log);
        }

        public string DisplayName { get; }
        public IEnumerable<EventMetadata> Events => Array.Empty<EventMetadata>();
        public Uri HelpLink { get; }
        public Guid Id { get; }
        public IList<EventKeyword> Keywords { get; } = new List<EventKeyword>
        {
            new EventKeyword("Classic", unchecked((long)StandardEventKeywords.EventLogClassic), "Classic"),
            new EventKeyword("Audit Success", (long)StandardEventKeywords.AuditSuccess, "Audit Success"),
            new EventKeyword("Audit Failure", (long)StandardEventKeywords.AuditFailure, "Audit Failure"),
        };
        public IList<EventLevel> Levels { get; } = new List<EventLevel>
        {
            new EventLevel("win:Critical", 1, "Critical"), new EventLevel("win:Error", 2, "Error"), new EventLevel("win:Warning", 3, "Warning"),
            new EventLevel("win:Informational", 4, "Information"), new EventLevel("win:Verbose", 5, "Verbose"),
        };
        public IList<EventLogLink> LogLinks { get; }
        public string MessageFilePath { get; }
        public string Name { get; }
        public IList<EventOpcode> Opcodes { get; } = new List<EventOpcode> { new EventOpcode("win:Info", 0, "Info") };
        public string ParameterFilePath { get; }
        public string ResourceFilePath { get; }
        public IList<EventTask> Tasks { get; } = new List<EventTask>();

        public void Dispose() { Dispose(true); GC.SuppressFinalize(this); }
        protected virtual void Dispose(bool disposing) { }
    }

    // ---- the events, as XML ----------------------------------------------------------------------------------

    internal static class EventXml
    {
        internal const string Ns = "http://schemas.microsoft.com/win/2004/08/events/event";
        private const long ClassicKeyword = (long)StandardEventKeywords.EventLogClassic, AuditSuccess = (long)StandardEventKeywords.AuditSuccess, AuditFailure = (long)StandardEventKeywords.AuditFailure;

        /// <summary>The event without its namespace: what Windows' XPath queries name (Event/System/Level).</summary>
        public static XElement Bare(string xml)
        {
            var e = XElement.Parse(xml);
            foreach (var x in e.DescendantsAndSelf())
            {
                x.Name = x.Name.LocalName;
                x.Attributes().Where(a => a.IsNamespaceDeclaration).Remove();
            }
            return e;
        }

        /// <summary>A classic log's entry as Windows renders a classic event.</summary>
        public static string Classic(string log, EventLogEntry entry)
        {
            long keywords = ClassicKeyword | entry.EntryType switch
            {
                EventLogEntryType.SuccessAudit => AuditSuccess,
                EventLogEntryType.FailureAudit => AuditFailure,
                _ => 0,
            };
            int level = entry.EntryType switch
            {
                EventLogEntryType.Error => 2,
                EventLogEntryType.Warning => 3,
                EventLogEntryType.Information => 4,
                _ => 0,
            };
            XNamespace ns = Ns;
            var data = new XElement(ns + "EventData", entry.ReplacementStrings.Select(s => new XElement(ns + "Data", s)));
            if (entry.Data.Length > 0) data.Add(new XElement(ns + "Binary", Convert.ToHexString(entry.Data)));
            var e = new XElement(ns + "Event",
                new XElement(ns + "System",
                    new XElement(ns + "Provider", new XAttribute("Name", entry.Source)),
                    new XElement(ns + "EventID", new XAttribute("Qualifiers", (entry.InstanceId >> 16) & 0xFFFF), entry.InstanceId & 0xFFFF),
                    new XElement(ns + "Version", 0),
                    new XElement(ns + "Level", level),
                    new XElement(ns + "Task", entry.CategoryNumber),
                    new XElement(ns + "Opcode", 0),
                    new XElement(ns + "Keywords", "0x" + keywords.ToString("x", CultureInfo.InvariantCulture)),
                    new XElement(ns + "TimeCreated", new XAttribute("SystemTime", entry.TimeGenerated.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture))),
                    new XElement(ns + "EventRecordID", entry.Index),
                    new XElement(ns + "Correlation"),
                    new XElement(ns + "Execution", new XAttribute("ProcessID", 0), new XAttribute("ThreadID", 0)),
                    new XElement(ns + "Channel", log),
                    new XElement(ns + "Computer", entry.MachineName),
                    new XElement(ns + "Security")),
                data);
            return e.ToString(SaveOptions.DisableFormatting);
        }

        public static string LevelName(byte level, long keywords) => level switch
        {
            1 => "Critical",
            2 => "Error",
            3 => "Warning",
            5 => "Verbose",
            _ => "Information",   // 4, and 0 (LogAlways: a classic audit), as Event Viewer shows them
        };

        public static IEnumerable<string> KeywordNames(long keywords)
        {
            var names = new List<string>();
            if ((keywords & AuditFailure) != 0) names.Add("Audit Failure");
            if ((keywords & AuditSuccess) != 0) names.Add("Audit Success");
            if ((keywords & ClassicKeyword) != 0) names.Add("Classic");
            return names;
        }

        public static string OpcodeName(short opcode) => opcode == 0 ? "Info" : opcode.ToString(CultureInfo.InvariantCulture);

        // a classic event's task is its category: "None", else its number (no category message file read here)
        public static string TaskName(int task, long keywords) => task == 0 ? "None" : "(" + task.ToString(CultureInfo.InvariantCulture) + ")";

        /// <summary>The event's data: its EventData values (Binary as bytes), else its UserData's leaves.</summary>
        public static IEnumerable<object> Values(XElement e)
        {
            var data = e.Element("EventData");
            if (data != null)
            {
                foreach (var d in data.Elements())
                    yield return d.Name.LocalName == "Binary" ? Convert.FromHexString(d.Value) : d.Value;
                yield break;
            }
            var user = e.Element("UserData");
            if (user != null)
                foreach (var leaf in user.Descendants().Where(x => !x.HasElements))
                    yield return leaf.Value;
        }

        public static object Typed(string name, string value) => name switch
        {
            "EventID" or "Task" or "ProcessID" or "ThreadID" => (object)(ushort.TryParse(value, out ushort u) ? u : value),
            "Level" or "Version" or "Opcode" => byte.TryParse(value, out byte b) ? b : value,
            "EventRecordID" => ulong.TryParse(value, out ulong r) ? r : value,
            "SystemTime" => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out DateTime t) ? t : value,
            "Keywords" => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && ulong.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong k) ? (object)k : value,
            "ActivityID" or "RelatedActivityID" or "Guid" => Guid.TryParse(value, out Guid g) ? g : value,
            _ => value,
        };
    }

    // ---- the events a query names (off Windows: the classic logs, XPath over their XML) -----------------------

    internal static class EventStore
    {
        /// <summary>The logs (or files) a query reads.</summary>
        public static IEnumerable<string> Paths(EventLogQuery q) => Selects(q).Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<EventLogRecord> Read(EventLogQuery q)
        {
            if (OperatingSystem.IsWindows()) return WindowsEvt.Query(q);
            var selects = Selects(q);
            var records = new List<(EventLogRecord Record, DateTime Time, long Order)>();
            long order = 0;
            var byKey = new Dictionary<string, (EventLogRecord Record, DateTime Time, long Order)>();
            foreach (var group in selects.GroupBy(s => s.Path, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var (xml, description) in Events(group.Key, q.ThePathType, q.TolerateQueryErrors))
                {
                    var bare = EventXml.Bare(xml);
                    var matched = new List<int>();
                    bool suppressed = false;
                    foreach (var s in group)
                    {
                        if (!EventXPath.Matches(bare, s.XPath, q.TolerateQueryErrors)) continue;
                        if (s.Suppress) { suppressed = true; break; }
                        matched.Add(s.QueryId);
                    }
                    if (suppressed || matched.Count == 0) continue;
                    var record = new EventLogRecord(xml, description, group.Key, matched.Distinct().ToArray());
                    records.Add((record, record.TimeCreated ?? DateTime.MinValue, order++));
                }
            }
            var ordered = records.OrderBy(r => r.Time).ThenBy(r => r.Order).Select(r => r.Record);
            return q.ReverseDirection ? ordered.Reverse().ToList() : ordered.ToList();
        }

        // a log's events (its entries as the OS keeps them), or an exported file's
        private static IEnumerable<(string Xml, string Description)> Events(string path, PathType type, bool tolerate)
        {
            if (type == PathType.FilePath)
            {
                if (!File.Exists(path)) throw new EventLogNotFoundException(2);
                XElement root;
                try { root = XElement.Load(path); }
                catch (XmlException) { throw new EventLogInvalidDataException($"{path} is not an exported log this machine reads (an .evtx file is read on Windows)"); }
                return root.Elements().Where(e => e.Name.LocalName == "Event").Select(e => (e.ToString(SaveOptions.DisableFormatting), Description(e))).ToList();
            }
            if (!EventLogStore.LogExists(path))
            {
                if (tolerate) return Array.Empty<(string, string)>();
                throw new EventLogNotFoundException(15007);   // ERROR_EVT_CHANNEL_NOT_FOUND
            }
            return EventLogStore.Log(path, create: false).Snapshot().Select(e => (EventXml.Classic(path, e), e.Message)).ToList();
        }

        private static string Description(XElement e) =>
            string.Join(Environment.NewLine, e.Elements().Where(x => x.Name.LocalName == "EventData").Elements().Where(x => x.Name.LocalName == "Data").Select(x => x.Value));

        internal readonly record struct Select(int QueryId, string Path, string XPath, bool Suppress);

        // a query: a path and an XPath (or none: every event), or a structured QueryList (each Query's Selects and
        // Suppresses, its Path the default of its parts)
        internal static List<Select> Selects(EventLogQuery q)
        {
            string query = q.Query?.Trim();
            if (!string.IsNullOrEmpty(query) && query.StartsWith("<", StringComparison.Ordinal))
            {
                XElement list;
                try { list = XElement.Parse(query); }
                catch (XmlException) { throw new EventLogInvalidDataException(15001); }
                var selects = new List<Select>();
                foreach (var qe in list.Name.LocalName == "QueryList" ? list.Elements().Where(x => x.Name.LocalName == "Query") : new[] { list })
                {
                    int id = int.TryParse((string)qe.Attribute("Id"), out int i) ? i : 0;
                    string defaultPath = (string)qe.Attribute("Path") ?? q.Path;
                    foreach (var part in qe.Elements())
                    {
                        bool suppress = part.Name.LocalName == "Suppress";
                        if (!suppress && part.Name.LocalName != "Select") continue;
                        selects.Add(new Select(id, (string)part.Attribute("Path") ?? defaultPath, part.Value.Trim(), suppress));
                    }
                }
                return selects;
            }
            if (q.Path == null) throw new ArgumentNullException("path");
            return new List<Select> { new Select(0, q.Path, string.IsNullOrEmpty(query) ? "*" : query, false) };
        }
    }

    /// <summary>Windows' event XPath (a subset of XPath 1.0, with timediff() and band()) over an event's XML.</summary>
    internal static class EventXPath
    {
        private static readonly Dictionary<string, XPathExpression> s_compiled = new();
        private static readonly EventFunctions s_context = new();

        public static bool Matches(XElement bareEvent, string xpath, bool tolerate)
        {
            if (string.IsNullOrWhiteSpace(xpath) || xpath.Trim() == "*") return true;
            XPathExpression expression;
            lock (s_compiled)
            {
                if (!s_compiled.TryGetValue(xpath, out expression))
                {
                    try
                    {
                        // relative to the document: "*[System[...]]" is the Event element when it matches
                        expression = XPathExpression.Compile(xpath.TrimStart().StartsWith("/", StringComparison.Ordinal) ? xpath : "/" + xpath.Trim());
                        expression.SetContext(s_context);
                    }
                    catch (XPathException)
                    {
                        if (tolerate) return false;
                        throw new EventLogInvalidDataException(15001);   // ERROR_EVT_INVALID_QUERY
                    }
                    s_compiled[xpath] = expression;
                }
            }
            var navigator = new XDocument(bareEvent).CreateNavigator();
            lock (expression)
            {
                object result = navigator.Evaluate(expression);
                return result switch
                {
                    XPathNodeIterator nodes => nodes.MoveNext(),
                    bool b => b,
                    double d => d != 0,
                    string s => s.Length > 0,
                    _ => false,
                };
            }
        }

        private sealed class EventFunctions : XsltContext
        {
            public EventFunctions() : base(new NameTable()) { }
            public override bool Whitespace => true;
            public override bool PreserveWhitespace(XPathNavigator node) => true;
            public override int CompareDocument(string baseUri, string nextbaseUri) => 0;
            public override IXsltContextVariable ResolveVariable(string prefix, string name) => throw new XPathException($"${name}");

            public override IXsltContextFunction ResolveFunction(string prefix, string name, XPathResultType[] argTypes) => name switch
            {
                "timediff" => new Function(1, 2, (args, ctx) =>
                {
                    // milliseconds between a time (an attribute's) and now (or the second time)
                    DateTime Time(object a) => DateTime.Parse(Text(a), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                    DateTime then = Time(args[0]);
                    DateTime now = args.Length > 1 ? Time(args[1]) : DateTime.UtcNow;
                    return (now - then).TotalMilliseconds;
                }),
                "band" => new Function(2, 2, (args, ctx) => (double)(Number(args[0]) & Number(args[1]))),
                _ => null,
            };

            private static string Text(object a) => a switch
            {
                XPathNodeIterator it => it.MoveNext() ? it.Current.Value : "",
                _ => Convert.ToString(a, CultureInfo.InvariantCulture) ?? "",
            };

            private static long Number(object a)
            {
                string s = Text(a).Trim();
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return unchecked((long)ulong.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                return a is double d ? (long)d : long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l : (long)double.Parse(s, CultureInfo.InvariantCulture);
            }

            private sealed class Function : IXsltContextFunction
            {
                private readonly Func<object[], XPathNavigator, object> _invoke;
                public Function(int min, int max, Func<object[], XPathNavigator, object> invoke) { Minargs = min; Maxargs = max; _invoke = invoke; }
                public XPathResultType[] ArgTypes => Array.Empty<XPathResultType>();
                public int Maxargs { get; }
                public int Minargs { get; }
                public XPathResultType ReturnType => XPathResultType.Number;
                public object Invoke(XsltContext xsltContext, object[] args, XPathNavigator docContext) => _invoke(args, docContext);
            }
        }
    }
}
