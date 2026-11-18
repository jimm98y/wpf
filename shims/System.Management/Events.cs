// WMI's events, as the event watcher receives them:
//
//   __InstanceCreationEvent, __InstanceDeletionEvent, __InstanceModificationEvent, __InstanceOperationEvent
//       for any class answered here (TargetInstance ISA 'Win32_Process'), polled every WITHIN seconds as WMI polls a
//       class with no event provider of its own: its instances compared by key, TargetInstance (and PreviousInstance)
//       the embedded objects
//   Win32_ProcessStartTrace, Win32_ProcessStopTrace (and Win32_ProcessTrace)   the processes, read twice a second
//   Win32_VolumeChangeEvent   a volume arriving or going (EventType 2, 3), the volumes read every second
//   Win32_PowerManagementEvent   the system's suspend, resume and power status change (SystemEvents' PowerModeChanged)
//
// Each event is matched against the query's WHERE as WMI matches it, and delivered on a thread of its own.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace System.Management
{
    public abstract partial class ManagementEventArgs : EventArgs
    {
        private readonly object _context;
        internal ManagementEventArgs(object context) => _context = context;
        public object Context => _context;
    }

    public partial class EventArrivedEventArgs : ManagementEventArgs
    {
        private readonly ManagementBaseObject _event;
        internal EventArrivedEventArgs(object context, ManagementBaseObject newEvent) : base(context) => _event = newEvent;
        public ManagementBaseObject NewEvent => _event;
    }

    public partial class StoppedEventArgs : ManagementEventArgs
    {
        private readonly ManagementStatus _status;
        internal StoppedEventArgs(object context, ManagementStatus status) : base(context) => _status = status;
        public ManagementStatus Status => _status;
    }

    public partial class EventQuery : ManagementQuery
    {
        public EventQuery() { }
        public EventQuery(string query) : base("WQL", query) { }
        public EventQuery(string language, string query) : base(language, query) { }
        public override object Clone() => new EventQuery(QueryLanguage, QueryString);
    }

    public partial class WqlEventQuery : EventQuery
    {
        private string _eventClassName = "", _condition = "", _having = "";
        private TimeSpan _within, _groupWithin;
        private StringCollection _groupBy = new StringCollection();

        public WqlEventQuery() : this(null, TimeSpan.Zero, null, TimeSpan.Zero, null, null) { }

        public WqlEventQuery(string queryOrEventClassName)
        {
            if (queryOrEventClassName != null && queryOrEventClassName.TrimStart().StartsWith("select", StringComparison.OrdinalIgnoreCase))
                QueryString = queryOrEventClassName;
            else
            {
                _eventClassName = queryOrEventClassName ?? "";
                BuildQuery();
            }
        }

        public WqlEventQuery(string eventClassName, string condition) : this(eventClassName, TimeSpan.Zero, condition, TimeSpan.Zero, null, null) { }
        public WqlEventQuery(string eventClassName, string condition, TimeSpan groupWithinInterval) : this(eventClassName, TimeSpan.Zero, condition, groupWithinInterval, null, null) { }
        public WqlEventQuery(string eventClassName, string condition, TimeSpan groupWithinInterval, string[] groupByPropertyList) : this(eventClassName, TimeSpan.Zero, condition, groupWithinInterval, groupByPropertyList, null) { }
        public WqlEventQuery(string eventClassName, TimeSpan withinInterval) : this(eventClassName, withinInterval, null, TimeSpan.Zero, null, null) { }
        public WqlEventQuery(string eventClassName, TimeSpan withinInterval, string condition) : this(eventClassName, withinInterval, condition, TimeSpan.Zero, null, null) { }

        public WqlEventQuery(string eventClassName, TimeSpan withinInterval, string condition, TimeSpan groupWithinInterval, string[] groupByPropertyList, string havingCondition)
        {
            _eventClassName = eventClassName ?? "";
            _within = withinInterval;
            _condition = condition ?? "";
            _groupWithin = groupWithinInterval;
            if (groupByPropertyList != null) _groupBy.AddRange(groupByPropertyList);
            _having = havingCondition ?? "";
            BuildQuery();
        }

        public override string QueryLanguage => base.QueryLanguage;

        public string EventClassName { get => _eventClassName; set { _eventClassName = value ?? ""; BuildQuery(); } }
        public string Condition { get => _condition; set { _condition = value ?? ""; BuildQuery(); } }
        public TimeSpan WithinInterval { get => _within; set { _within = value; BuildQuery(); } }
        public TimeSpan GroupWithinInterval { get => _groupWithin; set { _groupWithin = value; BuildQuery(); } }
        public StringCollection GroupByPropertyList { get => _groupBy; set { _groupBy = value ?? new StringCollection(); BuildQuery(); } }
        public string HavingCondition { get => _having; set { _having = value ?? ""; BuildQuery(); } }

        public override string QueryString
        {
            get => base.QueryString;
            set => base.QueryString = value;
        }

        // WMI's own text for the parts: "select * from C within n where X group within m by a, b having Y"
        protected internal void BuildQuery()
        {
            if (_eventClassName.Length == 0) { SetQueryText(""); return; }
            var sb = new StringBuilder("select * from ").Append(_eventClassName);
            if (_within != TimeSpan.Zero) sb.Append(" within ").Append(_within.TotalSeconds.ToString(CultureInfo.InvariantCulture));
            if (_condition.Length > 0) sb.Append(" where ").Append(_condition);
            if (_groupWithin != TimeSpan.Zero)
            {
                sb.Append(" group within ").Append(_groupWithin.TotalSeconds.ToString(CultureInfo.InvariantCulture));
                if (_groupBy.Count > 0) sb.Append(" by ").Append(string.Join(", ", _groupBy.Cast<string>()));
                if (_having.Length > 0) sb.Append(" having ").Append(_having);
            }
            SetQueryText(sb.ToString());
        }

        private static readonly Regex s_shape = new Regex(
            @"^\s*select\s+(?<props>.+?)\s+from\s+(?<cls>\w+)(?:\s+within\s+(?<within>[0-9.]+))?(?:\s+where\s+(?<cond>.+?))?(?:\s+group\s+within\s+(?<group>[0-9.]+)(?:\s+by\s+(?<by>.+?))?(?:\s+having\s+(?<having>.+?))?)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        protected internal override void ParseQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) { _eventClassName = ""; return; }
            Match m = s_shape.Match(query);
            if (!m.Success) throw new ArgumentException("The query is invalid.", nameof(query));
            _eventClassName = m.Groups["cls"].Value;
            _within = m.Groups["within"].Success ? TimeSpan.FromSeconds(double.Parse(m.Groups["within"].Value, CultureInfo.InvariantCulture)) : TimeSpan.Zero;
            _condition = m.Groups["cond"].Success ? m.Groups["cond"].Value.Trim() : "";
            _groupWithin = m.Groups["group"].Success ? TimeSpan.FromSeconds(double.Parse(m.Groups["group"].Value, CultureInfo.InvariantCulture)) : TimeSpan.Zero;
            _groupBy = new StringCollection();
            if (m.Groups["by"].Success) _groupBy.AddRange(m.Groups["by"].Value.Split(',').Select(s => s.Trim()).ToArray());
            _having = m.Groups["having"].Success ? m.Groups["having"].Value.Trim() : "";
        }

        public override object Clone() => new WqlEventQuery(QueryString);
    }

    public partial class EventWatcherOptions : ManagementOptions
    {
        private int _blockSize = 1;

        public EventWatcherOptions() : this(null, InfiniteTimeout, 1) { }

        public EventWatcherOptions(ManagementNamedValueCollection context, TimeSpan timeout, int blockSize)
        {
            Context = context;
            Timeout = timeout;
            BlockSize = blockSize;
        }

        public int BlockSize
        {
            get => _blockSize;
            set
            {
                if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
                _blockSize = value;
            }
        }

        public override object Clone() => new EventWatcherOptions(Context, Timeout, _blockSize);
    }

    [ToolboxItem(false)]
    public partial class ManagementEventWatcher : Component
    {
        private ManagementScope _scope;
        private EventQuery _query;
        private EventWatcherOptions _options;
        private EventArrivedEventHandler _arrived;
        private StoppedEventHandler _stopped;
        private EventSource _running;
        private EventSource _sync;
        private readonly BlockingCollection<ManagementBaseObject> _waiting = new BlockingCollection<ManagementBaseObject>();

        public ManagementEventWatcher() : this((ManagementScope)null, null, null) { }
        public ManagementEventWatcher(EventQuery query) : this(null, query, null) { }
        public ManagementEventWatcher(string query) : this(null, new EventQuery(query), null) { }
        public ManagementEventWatcher(ManagementScope scope, EventQuery query) : this(scope, query, null) { }
        public ManagementEventWatcher(string scope, string query) : this(new ManagementScope(scope), new EventQuery(query), null) { }
        public ManagementEventWatcher(string scope, string query, EventWatcherOptions options) : this(new ManagementScope(scope), new EventQuery(query), options) { }

        public ManagementEventWatcher(ManagementScope scope, EventQuery query, EventWatcherOptions options)
        {
            _scope = scope ?? new ManagementScope();
            _query = query ?? new EventQuery();
            _options = options ?? new EventWatcherOptions();
        }

        ~ManagementEventWatcher() => Stop();

        public EventWatcherOptions Options { get => _options; set { _options = value ?? throw new ArgumentNullException(nameof(value)); Stop(); } }
        public EventQuery Query { get => _query; set { _query = value ?? throw new ArgumentNullException(nameof(value)); Stop(); } }
        public ManagementScope Scope { get => _scope; set { _scope = value ?? throw new ArgumentNullException(nameof(value)); Stop(); } }

        public event EventArrivedEventHandler EventArrived { add => _arrived += value; remove => _arrived -= value; }
        public event StoppedEventHandler Stopped { add => _stopped += value; remove => _stopped -= value; }

        private EventSource Subscribe(Action<ManagementBaseObject> deliver)
        {
            if (!Cimv2.IsLocalServer(_scope.Path.Server)) throw new ManagementException(ManagementStatus.InvalidParameter);
            if (!Cimv2.IsKnownNamespace(_scope.EffectiveNamespace)) throw new ManagementException(ManagementStatus.InvalidNamespace);
            if (!string.Equals(_query.QueryLanguage, "WQL", StringComparison.OrdinalIgnoreCase)) throw new ManagementException(ManagementStatus.InvalidQueryType);
            var q = new WqlEventQuery(_query.QueryString);
            return EventSource.Create(_scope, q, deliver) ?? throw new ManagementException(ManagementStatus.InvalidClass);
        }

        public void Start()
        {
            Stop();
            object context = _options.Context;
            _running = Subscribe(e => ThreadPool.QueueUserWorkItem(_ => _arrived?.Invoke(this, new EventArrivedEventArgs(context, e))));
        }

        public void Stop()
        {
            EventSource running = Interlocked.Exchange(ref _running, null);
            if (running != null)
            {
                running.Dispose();
                _stopped?.Invoke(this, new StoppedEventArgs(_options.Context, ManagementStatus.OperationCanceled));
            }
            Interlocked.Exchange(ref _sync, null)?.Dispose();
        }

        // the next event, as a semisynchronous call returns it; the watcher keeps listening between calls
        public ManagementBaseObject WaitForNextEvent()
        {
            _sync ??= Subscribe(e => _waiting.Add(e));
            TimeSpan timeout = _options.Timeout;
            if (timeout == ManagementOptions.InfiniteTimeout || timeout == TimeSpan.MaxValue) return _waiting.Take();
            if (_waiting.TryTake(out ManagementBaseObject e, timeout)) return e;
            throw new ManagementException(ManagementStatus.Timedout);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Stop();
            base.Dispose(disposing);
        }
    }

    // ---- the sources ---------------------------------------------------------------------------------------------

    internal abstract class EventSource : IDisposable
    {
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        protected readonly Wql.Expr Condition;
        protected readonly Action<ManagementBaseObject> Deliver;
        protected readonly ManagementScope Scope;
        protected readonly string EventClass;

        protected EventSource(ManagementScope scope, string eventClass, Wql.Expr condition, Action<ManagementBaseObject> deliver)
        {
            Scope = scope;
            EventClass = eventClass;
            Condition = condition;
            Deliver = deliver;
        }

        protected CancellationToken Stopping => _stop.Token;

        public void Dispose() => _stop.Cancel();

        private static readonly Regex s_target = new Regex(@"TargetInstance\s+ISA\s+['""](?<cls>\w+)['""]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static EventSource Create(ManagementScope scope, WqlEventQuery query, Action<ManagementBaseObject> deliver)
        {
            string cls = query.EventClassName;
            Wql.Expr condition = query.Condition.Length > 0 ? Wql.ParseSelect("SELECT * FROM " + cls + " WHERE " + query.Condition).Condition : null;
            switch (cls.ToUpperInvariant())
            {
                case "__INSTANCECREATIONEVENT":
                case "__INSTANCEDELETIONEVENT":
                case "__INSTANCEMODIFICATIONEVENT":
                case "__INSTANCEOPERATIONEVENT":
                {
                    Match m = s_target.Match(query.Condition);
                    WmiClass target = m.Success ? Classes.Find(m.Groups["cls"].Value) ?? Classes.Find("Win32_" + m.Groups["cls"].Value.Replace("CIM_", "", StringComparison.OrdinalIgnoreCase)) : null;
                    if (target == null) throw new ManagementException(ManagementStatus.InvalidClass);
                    TimeSpan within = query.WithinInterval > TimeSpan.Zero ? query.WithinInterval : TimeSpan.FromSeconds(1);
                    return new InstanceEvents(scope, cls, condition, deliver, target, within).Start();
                }
                case "WIN32_PROCESSSTARTTRACE":
                case "WIN32_PROCESSSTOPTRACE":
                case "WIN32_PROCESSTRACE":
                    return new ProcessTrace(scope, cls, condition, deliver).Start();
                case "WIN32_VOLUMECHANGEEVENT":
                    return new VolumeChanges(scope, cls, condition, deliver).Start();
                case "WIN32_POWERMANAGEMENTEVENT":
                    return new PowerEvents(scope, cls, condition, deliver).Start();
                default:
                    return null;
            }
        }

        protected EventSource Start()
        {
            var thread = new Thread(() =>
            {
                try { Run(); }
                catch (OperationCanceledException) { }
                catch (Exception e) { ShimLog.Once("wmi:event:" + EventClass, $"System.Management: {EventClass} events stopped: {e.Message}"); }
            }) { IsBackground = true, Name = "WMI events: " + EventClass };
            thread.Start();
            return this;
        }

        protected abstract void Run();

        protected void Wait(TimeSpan interval)
        {
            if (Stopping.WaitHandle.WaitOne(interval)) throw new OperationCanceledException();
        }

        // an event object: its class, its properties, the time it was made (100 ns since 1601, as WMI stamps it)
        protected static ManagementBaseObject Event(string cls, params (string Name, CimType Type, object Value)[] properties)
        {
            var d = new WmiData { ClassName = cls };
            d.Properties.Add(new PropertyData("SECURITY_DESCRIPTOR", CimType.UInt8, true, null, cls));
            d.Properties.Add(new PropertyData("TIME_CREATED", CimType.UInt64, false, (ulong)DateTime.UtcNow.ToFileTimeUtc(), cls));
            foreach (var p in properties) d.Properties.Add(new PropertyData(p.Name, p.Type, false, p.Value, cls));
            return new ManagementBaseObject(d);
        }

        protected void Raise(ManagementBaseObject e)
        {
            if (Condition != null && !Condition.Eval(name => Resolve(e, name))) return;
            Deliver(e);
        }

        // a name in the WHERE: a property, or one of an embedded object's (TargetInstance.Name)
        internal static object Resolve(ManagementBaseObject e, string name)
        {
            object current = e;
            foreach (string part in name.Split('.'))
            {
                if (current is not ManagementBaseObject o) return null;
                if (part.StartsWith("__", StringComparison.Ordinal))
                {
                    current = o.Data.SystemProperties().FirstOrDefault(p => p.Name.Equals(part, StringComparison.OrdinalIgnoreCase))?.Value;
                    continue;
                }
                current = o.Data.Find(part)?.Value;
            }
            return current;
        }
    }

    internal sealed class InstanceEvents : EventSource
    {
        private readonly WmiClass _target;
        private readonly TimeSpan _within;

        public InstanceEvents(ManagementScope scope, string cls, Wql.Expr condition, Action<ManagementBaseObject> deliver, WmiClass target, TimeSpan within)
            : base(scope, cls, condition, deliver)
        {
            _target = target;
            _within = within;
        }

        private Dictionary<string, WmiData> Snapshot()
        {
            var result = new Dictionary<string, WmiData>(StringComparer.OrdinalIgnoreCase);
            foreach (WmiData d in _target.Instances(Scope.EffectiveServer))
                if (d.RelativePath is { } key) result[key] = d;
            return result;
        }

        private static bool Same(WmiData a, WmiData b) =>
            a.Properties.Count == b.Properties.Count && a.Properties.Zip(b.Properties).All(p => Equals(p.First.Value, p.Second.Value)
                || (p.First.Value is Array x && p.Second.Value is Array y && x.Cast<object>().SequenceEqual(y.Cast<object>())));

        protected override void Run()
        {
            string kind = EventClass.ToUpperInvariant();
            bool creation = kind is "__INSTANCECREATIONEVENT" or "__INSTANCEOPERATIONEVENT";
            bool deletion = kind is "__INSTANCEDELETIONEVENT" or "__INSTANCEOPERATIONEVENT";
            bool modification = kind is "__INSTANCEMODIFICATIONEVENT" or "__INSTANCEOPERATIONEVENT";
            Dictionary<string, WmiData> last = Snapshot();
            while (true)
            {
                Wait(_within);
                Dictionary<string, WmiData> now = Snapshot();
                foreach (var (key, d) in now)
                {
                    if (!last.TryGetValue(key, out WmiData before))
                    {
                        if (creation) Raise(Event("__InstanceCreationEvent", ("TargetInstance", CimType.Object, new ManagementBaseObject(d))));
                    }
                    else if (modification && !Same(before, d))
                        Raise(Event("__InstanceModificationEvent", ("TargetInstance", CimType.Object, new ManagementBaseObject(d)), ("PreviousInstance", CimType.Object, new ManagementBaseObject(before))));
                }
                if (deletion)
                    foreach (var (key, d) in last)
                        if (!now.ContainsKey(key)) Raise(Event("__InstanceDeletionEvent", ("TargetInstance", CimType.Object, new ManagementBaseObject(d))));
                last = now;
            }
        }
    }

    internal sealed class ProcessTrace : EventSource
    {
        public ProcessTrace(ManagementScope scope, string cls, Wql.Expr condition, Action<ManagementBaseObject> deliver) : base(scope, cls, condition, deliver) { }

        private static Dictionary<int, string> Processes()
        {
            var result = new Dictionary<int, string>();
            foreach (Process p in Process.GetProcesses())
            {
                using (p)
                {
                    try { result[p.Id] = p.ProcessName; } catch (InvalidOperationException) { }
                }
            }
            return result;
        }

        protected override void Run()
        {
            bool starts = !EventClass.Equals("Win32_ProcessStopTrace", StringComparison.OrdinalIgnoreCase);
            bool stops = !EventClass.Equals("Win32_ProcessStartTrace", StringComparison.OrdinalIgnoreCase);
            Dictionary<int, string> last = Processes();
            while (true)
            {
                Wait(TimeSpan.FromMilliseconds(500));
                Dictionary<int, string> now = Processes();
                if (starts)
                    foreach (var (pid, name) in now)
                        if (!last.TryGetValue(pid, out string before) || before != name)
                            Raise(Trace("Win32_ProcessStartTrace", pid, name, null));
                if (stops)
                    foreach (var (pid, name) in last)
                        if (!now.TryGetValue(pid, out string after) || after != name)
                            Raise(Trace("Win32_ProcessStopTrace", pid, name, 0u));
                last = now;
            }
        }

        // the image name as the kernel's trace gives it (name.exe), its parent where the system says
        private static ManagementBaseObject Trace(string cls, int pid, string name, uint? exitStatus)
        {
            var props = new List<(string, CimType, object)>
            {
                ("ParentProcessID", CimType.UInt32, ParentOf(pid)), ("ProcessID", CimType.UInt32, (uint)pid),
                ("ProcessName", CimType.String, name + ".exe"), ("SessionID", CimType.UInt32, 1u),
                ("Sid", CimType.UInt8, null),
            };
            if (exitStatus is { } status) props.Add(("ExitStatus", CimType.UInt32, status));
            return Event(cls, props.ToArray());
        }

        private static uint ParentOf(int pid)
        {
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    string stat = File.ReadAllText($"/proc/{pid}/stat");
                    string[] f = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                    return uint.TryParse(f[1], out uint ppid) ? ppid : 0;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            return 0;
        }
    }

    internal sealed class VolumeChanges : EventSource
    {
        public VolumeChanges(ManagementScope scope, string cls, Wql.Expr condition, Action<ManagementBaseObject> deliver) : base(scope, cls, condition, deliver) { }

        private static HashSet<string> Volumes()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    string root = d.RootDirectory.FullName;
                    if (OperatingSystem.IsWindows()) result.Add(root.TrimEnd('\\'));
                    else if (root == "/" || root.StartsWith("/Volumes/", StringComparison.Ordinal) || root.StartsWith("/media/", StringComparison.Ordinal) || root.StartsWith("/mnt/", StringComparison.Ordinal) || root.StartsWith("/run/media/", StringComparison.Ordinal))
                        result.Add(root.Length > 1 ? root.TrimEnd('/') : root);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            return result;
        }

        protected override void Run()
        {
            HashSet<string> last = Volumes();
            while (true)
            {
                Wait(TimeSpan.FromSeconds(1));
                HashSet<string> now = Volumes();
                foreach (string v in now.Except(last)) Raise(Event("Win32_VolumeChangeEvent", ("DriveName", CimType.String, v), ("EventType", CimType.UInt16, (ushort)2)));
                foreach (string v in last.Except(now)) Raise(Event("Win32_VolumeChangeEvent", ("DriveName", CimType.String, v), ("EventType", CimType.UInt16, (ushort)3)));
                last = now;
            }
        }
    }

    // the system's power events, as SystemEvents hears them (by name: the stand-in is a package of its own)
    internal sealed class PowerEvents : EventSource
    {
        private Reflection.EventInfo _event;
        private Delegate _handler;

        public PowerEvents(ManagementScope scope, string cls, Wql.Expr condition, Action<ManagementBaseObject> deliver) : base(scope, cls, condition, deliver) { }

        protected override void Run()
        {
            Type events = Type.GetType("Microsoft.Win32.SystemEvents, Microsoft.Win32.SystemEvents", throwOnError: false);
            _event = events?.GetEvent("PowerModeChanged");
            if (_event?.EventHandlerType == null) return;
            var method = typeof(PowerEvents).GetMethod(nameof(OnPowerModeChanged), Reflection.BindingFlags.NonPublic | Reflection.BindingFlags.Instance)!;
            _handler = Delegate.CreateDelegate(_event.EventHandlerType, this, method);
            _event.AddEventHandler(null, _handler);
            try { Stopping.WaitHandle.WaitOne(); }
            finally { _event.RemoveEventHandler(null, _handler); }
        }

        // PowerModes: Resume 1, StatusChange 2, Suspend 3 -> EventType: 7 resume, 10 status change, 4 entering suspend
        private void OnPowerModeChanged(object sender, EventArgs e)
        {
            int mode = Convert.ToInt32(e.GetType().GetProperty("Mode")?.GetValue(e), CultureInfo.InvariantCulture);
            ushort type = mode switch { 1 => 7, 2 => 10, 3 => 4, _ => 0 };
            if (type != 0) Raise(Event("Win32_PowerManagementEvent", ("EventType", CimType.UInt16, type), ("OEMEventCode", CimType.UInt16, null)));
        }
    }
}
