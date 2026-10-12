// EventLog over a registry of sources and logs and an in-process store of entries.

using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace System.Diagnostics
{
    [DefaultEvent("EntryWritten")]
    public partial class EventLog : Component, ISupportInitialize
    {
        private const int MaxMessageLength = 31839;

        private string _log = "";
        private string _machine = ".";
        private string _source = "";
        private bool _enableRaisingEvents;
        private bool _initializing;
        private EntryWrittenEventHandler _entryWritten;

        public EventLog() : this("", ".", "") { }

        public EventLog(string logName) : this(logName, ".", "") { }

        public EventLog(string logName, string machineName) : this(logName, machineName, "") { }

        public EventLog(string logName, string machineName, string source)
        {
            ArgumentNullException.ThrowIfNull(logName);
            if (!ValidMachineName(machineName)) throw new ArgumentException($"Invalid value '{machineName}' for parameter 'machineName'.");
            _log = logName;
            _machine = machineName;
            _source = source ?? "";
        }

        [Browsable(false)]
        [DefaultValue(false)]
        public bool EnableRaisingEvents
        {
            get => _enableRaisingEvents;
            set
            {
                if (_enableRaisingEvents == value) return;
                _enableRaisingEvents = value;
                if (value) EventLogStore.Listen(this);
                else EventLogStore.Unlisten(this);
            }
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public EventLogEntryCollection Entries => new EventLogEntryCollection(EventLogStore.Log(RequireLog(), create: false));

        [DefaultValue("")]
        [ReadOnly(true)]
        [SettingsBindable(true)]
        public string Log
        {
            get
            {
                if (_log.Length == 0 && _source.Length > 0) _log = LogNameFromSourceName(_source, _machine);
                return _log;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                bool listening = _enableRaisingEvents;
                if (listening) EnableRaisingEvents = false;
                _log = value;
                if (listening) EnableRaisingEvents = true;
            }
        }

        [Browsable(false)]
        public string LogDisplayName => RequireLog();

        [DefaultValue(".")]
        [ReadOnly(true)]
        [SettingsBindable(true)]
        public string MachineName
        {
            get => _machine;
            set
            {
                if (!ValidMachineName(value)) throw new ArgumentException($"Invalid value {value} for property MachineName.");
                _machine = value;
            }
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public long MaximumKilobytes
        {
            get => EventLogStore.Log(RequireLog(), create: false).MaximumKilobytes;
            set
            {
                if (value < 64 || value > 0x3FFFC0 || value % 64 != 0) throw new ArgumentOutOfRangeException(nameof(value), "MaximumKilobytes must be between 64 KB and 4 GB, and must be in 64K increments.");
                EventLogStore.Log(RequireLog(), create: false).MaximumKilobytes = value;
            }
        }

        [Browsable(false)]
        public int MinimumRetentionDays => EventLogStore.Log(RequireLog(), create: false).RetentionDays;

        [Browsable(false)]
        public OverflowAction OverflowAction => EventLogStore.Log(RequireLog(), create: false).Overflow;

        [DefaultValue("")]
        [ReadOnly(true)]
        [SettingsBindable(true)]
        public string Source
        {
            get => _source;
            set => _source = value ?? "";
        }

        [Browsable(false)]
        [DefaultValue(null)]
        public ISynchronizeInvoke SynchronizingObject { get; set; }

        public event EntryWrittenEventHandler EntryWritten
        {
            add => _entryWritten += value;
            remove => _entryWritten -= value;
        }

        public void BeginInit() => _initializing = true;

        public void Clear() => EventLogStore.Log(RequireLog(), create: false).Clear();

        public void Close()
        {
            EnableRaisingEvents = false;
        }

        public static void CreateEventSource(EventSourceCreationData sourceData)
        {
            ArgumentNullException.ThrowIfNull(sourceData);
            string source = sourceData.Source, log = string.IsNullOrEmpty(sourceData.LogName) ? "Application" : sourceData.LogName;
            if (string.IsNullOrEmpty(source)) throw new ArgumentException("Source property was not set before writing to the event log.");
            if (!ValidMachineName(sourceData.MachineName)) throw new ArgumentException($"Invalid value '{sourceData.MachineName}' for parameter 'machineName'.");
            RequireLocal(sourceData.MachineName);
            if (!ValidLogName(log)) throw new ArgumentException("Invalid log name.");
            if (SourceExists(source, sourceData.MachineName)) throw new ArgumentException($"Source {source} already exists on the local computer.");
            EventLogStore.CreateSource(source, log);
        }

        public static void CreateEventSource(string source, string logName) => CreateEventSource(new EventSourceCreationData(source, logName));

        [Obsolete("EventLog.CreateEventSource has been deprecated. Use System.Diagnostics.EventLog.CreateEventSource(EventSourceCreationData sourceData) instead.")]
        public static void CreateEventSource(string source, string logName, string machineName) =>
            CreateEventSource(new EventSourceCreationData(source, logName) { MachineName = machineName });

        public static void Delete(string logName) => Delete(logName, ".");

        public static void Delete(string logName, string machineName)
        {
            if (string.IsNullOrEmpty(logName)) throw new ArgumentException("Log to delete was not specified.");
            if (!ValidMachineName(machineName)) throw new ArgumentException($"Invalid value '{machineName}' for parameter 'machineName'.");
            RequireLocal(machineName);
            if (!EventLogStore.LogExists(logName)) throw new InvalidOperationException($"The event log '{logName}' on computer '{machineName}' does not exist.");
            EventLogStore.DeleteLog(logName);
        }

        public static void DeleteEventSource(string source) => DeleteEventSource(source, ".");

        public static void DeleteEventSource(string source, string machineName)
        {
            if (!ValidMachineName(machineName)) throw new ArgumentException($"Invalid value '{machineName}' for parameter 'machineName'.");
            RequireLocal(machineName);
            if (!EventLogStore.DeleteSource(source))
                throw new ArgumentException($"The source '{source}' is not registered on machine '{machineName}', or you do not have write access to the {source} registry key.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Close();
            base.Dispose(disposing);
        }

        public void EndInit()
        {
            _initializing = false;
            if (_enableRaisingEvents) EventLogStore.Listen(this);
        }

        public static bool Exists(string logName) => Exists(logName, ".");

        public static bool Exists(string logName, string machineName)
        {
            if (!ValidMachineName(machineName)) throw new ArgumentException($"Invalid value '{machineName}' for parameter 'machineName'.");
            RequireLocal(machineName);
            return !string.IsNullOrEmpty(logName) && EventLogStore.LogExists(logName);
        }

        public static EventLog[] GetEventLogs() => GetEventLogs(".");

        public static EventLog[] GetEventLogs(string machineName)
        {
            if (!ValidMachineName(machineName)) throw new ArgumentException($"Invalid value '{machineName}' for parameter 'machineName'.");
            RequireLocal(machineName);
            return EventLogStore.LogNames().Select(n => new EventLog(n, machineName)).ToArray();
        }

        public static string LogNameFromSourceName(string source, string machineName)
        {
            RequireLocal(machineName);
            return EventLogStore.LogOfSource(source) ?? "";
        }

        public void ModifyOverflowPolicy(OverflowAction action, int retentionDays)
        {
            if (!Enum.IsDefined(action)) throw new InvalidEnumArgumentException(nameof(action), (int)action, typeof(OverflowAction));
            if (action == OverflowAction.OverwriteOlder && (retentionDays < 1 || retentionDays > 365)) throw new ArgumentOutOfRangeException(nameof(retentionDays), "'retentionDays' must be between 1 and 365 days.");
            var log = EventLogStore.Log(RequireLog(), create: false);
            log.Overflow = action;
            log.RetentionDays = action == OverflowAction.OverwriteOlder ? retentionDays : action == OverflowAction.DoNotOverwrite ? -1 : 0;
        }

        public void RegisterDisplayName(string resourceFile, long resourceId)
        {
            ArgumentNullException.ThrowIfNull(resourceFile);
            RequireLog();
        }

        public static bool SourceExists(string source) => SourceExists(source, ".");

        public static bool SourceExists(string source, string machineName)
        {
            if (!ValidMachineName(machineName)) throw new ArgumentException($"Invalid value '{machineName}' for parameter 'machineName'.");
            RequireLocal(machineName);
            return !string.IsNullOrEmpty(source) && EventLogStore.LogOfSource(source) != null;
        }

        public void WriteEntry(string message) => WriteEntry(message, EventLogEntryType.Information, 0, 0, null);

        public void WriteEntry(string message, EventLogEntryType type) => WriteEntry(message, type, 0, 0, null);

        public void WriteEntry(string message, EventLogEntryType type, int eventID) => WriteEntry(message, type, eventID, 0, null);

        public void WriteEntry(string message, EventLogEntryType type, int eventID, short category) => WriteEntry(message, type, eventID, category, null);

        public void WriteEntry(string message, EventLogEntryType type, int eventID, short category, byte[] rawData)
        {
            if (eventID < 0 || eventID > 65535) throw new ArgumentException($"Invalid eventID value '{eventID}'. It must be in the range between '0' and '65535'.");
            if (!Enum.IsDefined(type)) throw new InvalidEnumArgumentException(nameof(type), (int)type, typeof(EventLogEntryType));
            Write(type, eventID, category, rawData, new[] { message ?? "" }, message ?? "");
        }

        public static void WriteEntry(string source, string message) => WriteEntry(source, message, EventLogEntryType.Information, 0, 0, null);

        public static void WriteEntry(string source, string message, EventLogEntryType type) => WriteEntry(source, message, type, 0, 0, null);

        public static void WriteEntry(string source, string message, EventLogEntryType type, int eventID) => WriteEntry(source, message, type, eventID, 0, null);

        public static void WriteEntry(string source, string message, EventLogEntryType type, int eventID, short category) => WriteEntry(source, message, type, eventID, category, null);

        public static void WriteEntry(string source, string message, EventLogEntryType type, int eventID, short category, byte[] rawData)
        {
            using var log = new EventLog("", ".", source);
            log.WriteEntry(message, type, eventID, category, rawData);
        }

        public void WriteEvent(EventInstance instance, byte[] data, params object[] values)
        {
            ArgumentNullException.ThrowIfNull(instance);
            string[] strings = (values ?? Array.Empty<object>()).Select(v => v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture)).ToArray();
            // Without a message file to format them, the replacement strings are the message.
            Write(instance.EntryType, instance.InstanceId, (short)instance.CategoryId, data, strings, string.Join(Environment.NewLine, strings));
        }

        public void WriteEvent(EventInstance instance, params object[] values) => WriteEvent(instance, null, values);

        public static void WriteEvent(string source, EventInstance instance, byte[] data, params object[] values)
        {
            using var log = new EventLog("", ".", source);
            log.WriteEvent(instance, data, values);
        }

        public static void WriteEvent(string source, EventInstance instance, params object[] values) => WriteEvent(source, instance, null, values);

        private void Write(EventLogEntryType type, long instanceId, short category, byte[] data, string[] strings, string message)
        {
            if (_source.Length == 0) throw new ArgumentException("Source property was not set before writing to the event log.");
            if (message.Length > MaxMessageLength) throw new ArgumentException($"Log entry string is too long. A string written to the event log cannot exceed {MaxMessageLength} characters.");
            RequireLocal(_machine);

            // Writing from a source nobody registered registers it, in this object's log (Windows
            // does the same, given the rights to).
            string registered = EventLogStore.LogOfSource(_source);
            if (registered == null)
            {
                registered = _log.Length > 0 ? _log : "Application";
                EventLogStore.CreateSource(_source, registered);
            }
            else if (_log.Length > 0 && !string.Equals(_log, registered, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The source '{_source}' is not registered in log '{_log}'. (It is registered in log '{registered}'.) \" The Source and Log properties must be matched, or you may set Log to the empty string, and it will automatically be matched to the Source property.");
            }
            EventLogStore.Append(registered, _source, _machine, type, instanceId, category, data, strings, message);
        }

        internal void OnEntryWritten(EventLogEntry entry)
        {
            EntryWrittenEventHandler handler = _entryWritten;
            if (handler == null) return;
            var args = new EntryWrittenEventArgs(entry);
            ISynchronizeInvoke sync = SynchronizingObject;
            if (sync != null && sync.InvokeRequired) sync.BeginInvoke(handler, new object[] { this, args });
            else handler(this, args);
        }

        internal string ListenedLog => _log.Length > 0 ? _log : Log;

        private string RequireLog()
        {
            string log = Log;
            if (log.Length == 0) throw new ArgumentException("Log property is not set (zero-length string).");
            RequireLocal(_machine);
            if (!EventLogStore.LogExists(log)) throw new InvalidOperationException($"The event log '{log}' on computer '{_machine}' does not exist.");
            return log;
        }

        private static void RequireLocal(string machineName)
        {
            if (machineName != "." && !string.Equals(machineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Cannot open log for source '{machineName}'. You may not have write access.");
        }

        private static bool ValidMachineName(string value) =>
            !string.IsNullOrEmpty(value) && value.IndexOfAny(new[] { '\\', '/', ' ' }) < 0;

        private static bool ValidLogName(string value) =>
            value.Length > 0 && value.IndexOfAny(new[] { '\\', '/', '*', '?', '"', '<', '>', '|' }) < 0 && !value.Any(char.IsControl);
    }

    [DesignTimeVisible(false)]
    [ToolboxItem(false)]
    public sealed partial class EventLogEntry : Component, Runtime.Serialization.ISerializable
    {
        private readonly string _source, _machine, _message;
        private readonly long _instanceId;
        private readonly short _category;
        private readonly byte[] _data;
        private readonly string[] _strings;
        private readonly EventLogEntryType _type;
        private readonly DateTime _time;
        private readonly int _index;

        internal EventLogEntry(int index, string source, string machine, EventLogEntryType type, long instanceId, short category, byte[] data, string[] strings, string message)
        {
            _index = index;
            _source = source;
            _machine = machine == "." ? Environment.MachineName : machine;
            _type = type;
            _instanceId = instanceId;
            _category = category;
            _data = data ?? Array.Empty<byte>();
            _strings = strings ?? Array.Empty<string>();
            _message = message;
            _time = DateTime.Now;
        }

        public string Category => _category == 0 ? "None" : "(" + _category.ToString(CultureInfo.InvariantCulture) + ")";

        public short CategoryNumber => _category;

        public byte[] Data => (byte[])_data.Clone();

        public EventLogEntryType EntryType => _type;

        [Obsolete("EventLogEntry.EventID has been deprecated. Use System.Diagnostics.EventLogEntry.InstanceId instead.")]
        public int EventID => (int)(_instanceId & 0x3FFFFFFF);

        public int Index => _index;

        public long InstanceId => _instanceId;

        public string MachineName => _machine;

        [Editor("System.ComponentModel.Design.BinaryEditor, System.Design, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", "System.Drawing.Design.UITypeEditor, System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
        public string Message => _message;

        public string[] ReplacementStrings => (string[])_strings.Clone();

        public string Source => _source;

        public DateTime TimeGenerated => _time;

        public DateTime TimeWritten => _time;

        public string UserName => null;

        public bool Equals(EventLogEntry otherEntry) =>
            otherEntry != null && otherEntry._index == _index && otherEntry._source == _source && otherEntry._time == _time && otherEntry._instanceId == _instanceId;

        void Runtime.Serialization.ISerializable.GetObjectData(Runtime.Serialization.SerializationInfo info, Runtime.Serialization.StreamingContext context) =>
            throw new PlatformNotSupportedException();
    }

    public partial class EventLogEntryCollection : Collections.ICollection, Collections.IEnumerable
    {
        private readonly EventLogStore.LogData _log;

        internal EventLogEntryCollection(EventLogStore.LogData log) => _log = log;

        public int Count => _log.Snapshot().Length;

        public virtual EventLogEntry this[int index] => _log.Snapshot()[index];

        bool Collections.ICollection.IsSynchronized => false;

        object Collections.ICollection.SyncRoot => this;

        public void CopyTo(EventLogEntry[] entries, int index) => _log.Snapshot().CopyTo(entries, index);

        public Collections.IEnumerator GetEnumerator() => _log.Snapshot().GetEnumerator();

        void Collections.ICollection.CopyTo(Array array, int index) => _log.Snapshot().CopyTo(array, index);
    }

    public partial class EntryWrittenEventArgs : EventArgs
    {
        private readonly EventLogEntry _entry;

        public EntryWrittenEventArgs() { }

        public EntryWrittenEventArgs(EventLogEntry entry) => _entry = entry;

        public EventLogEntry Entry => _entry;
    }

    public partial class EventInstance
    {
        private int _categoryId;
        private EventLogEntryType _entryType = EventLogEntryType.Information;
        private long _instanceId;

        public EventInstance(long instanceId, int categoryId) : this(instanceId, categoryId, EventLogEntryType.Information) { }

        public EventInstance(long instanceId, int categoryId, EventLogEntryType entryType)
        {
            InstanceId = instanceId;
            CategoryId = categoryId;
            EntryType = entryType;
        }

        public int CategoryId
        {
            get => _categoryId;
            set
            {
                if (value > ushort.MaxValue || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                _categoryId = value;
            }
        }

        public EventLogEntryType EntryType
        {
            get => _entryType;
            set
            {
                if (!Enum.IsDefined(value)) throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(EventLogEntryType));
                _entryType = value;
            }
        }

        public long InstanceId
        {
            get => _instanceId;
            set
            {
                if (value > uint.MaxValue || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                _instanceId = value;
            }
        }
    }

    public partial class EventSourceCreationData
    {
        private string _machineName = ".";

        public EventSourceCreationData(string source, string logName)
        {
            Source = source;
            LogName = logName;
        }

        public int CategoryCount { get; set; }

        public string CategoryResourceFile { get; set; }

        public string LogName { get; set; } = "Application";

        public string MachineName
        {
            get => _machineName;
            set => _machineName = value;
        }

        public string MessageResourceFile { get; set; }

        public string ParameterResourceFile { get; set; }

        public string Source { get; set; }
    }

    public sealed partial class EventLogTraceListener : TraceListener
    {
        private EventLog _eventLog;
        private bool _nameSet;

        public EventLogTraceListener() { }

        public EventLogTraceListener(EventLog eventLog) : base(eventLog != null ? eventLog.Source : string.Empty)
        {
            _eventLog = eventLog;
        }

        public EventLogTraceListener(string source)
        {
            _eventLog = new EventLog { Source = source };
        }

        public EventLog EventLog
        {
            get => _eventLog;
            set => _eventLog = value;
        }

        public override string Name
        {
            get => !_nameSet && _eventLog != null ? _eventLog.Source : base.Name;
            set
            {
                _nameSet = true;
                base.Name = value;
            }
        }

        public override void Close() => _eventLog?.Close();

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing) Close();
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        [Runtime.InteropServices.ComVisible(false)]
        public override void TraceData(TraceEventCache eventCache, string source, TraceEventType severity, int id, object data)
        {
            if (Filter != null && !Filter.ShouldTrace(eventCache, source, severity, id, null, null, data, null)) return;
            _eventLog?.WriteEvent(Instance(severity, id), new object[] { data });
        }

        [Runtime.InteropServices.ComVisible(false)]
        public override void TraceData(TraceEventCache eventCache, string source, TraceEventType severity, int id, params object[] data)
        {
            if (Filter != null && !Filter.ShouldTrace(eventCache, source, severity, id, null, null, null, data)) return;
            _eventLog?.WriteEvent(Instance(severity, id), new object[] { data == null ? "" : string.Join(", ", data) });
        }

        [Runtime.InteropServices.ComVisible(false)]
        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType severity, int id, string message)
        {
            if (Filter != null && !Filter.ShouldTrace(eventCache, source, severity, id, message, null, null, null)) return;
            _eventLog?.WriteEvent(Instance(severity, id), message);
        }

        [Runtime.InteropServices.ComVisible(false)]
        public override void TraceEvent(TraceEventCache eventCache, string source, TraceEventType severity, int id, string format, params object[] args)
        {
            if (Filter != null && !Filter.ShouldTrace(eventCache, source, severity, id, format, args, null, null)) return;
            _eventLog?.WriteEvent(Instance(severity, id), args == null || args.Length == 0 ? format : string.Format(CultureInfo.InvariantCulture, format, args));
        }

        public override void Write(string message) => _eventLog?.WriteEntry(message);

        public override void WriteLine(string message) => Write(message);

        private static EventInstance Instance(TraceEventType severity, int id)
        {
            EventLogEntryType type = severity switch
            {
                TraceEventType.Critical or TraceEventType.Error => EventLogEntryType.Error,
                TraceEventType.Warning => EventLogEntryType.Warning,
                _ => EventLogEntryType.Information,
            };
            return new EventInstance(id < 0 || id > ushort.MaxValue ? 0 : id, 0, type);
        }
    }
}
