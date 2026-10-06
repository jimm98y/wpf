// Browser shim for System.Diagnostics.Process. See the .csproj for why this exists.
//
// The split is deliberate:
//   * SELF-INSPECTION works. GetCurrentProcess() reports the numbers the runtime can actually
//     answer for (managed memory, uptime, thread count). Apps use these for status bars, telemetry
//     and plugin metrics, none of which needs a real OS process.
//   * SPAWNING refuses, loudly. A browser has no process table; pretending Start() succeeded would
//     hand the caller an object whose ExitCode and output never arrive, which is worse than a
//     refusal it can catch.
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Diagnostics
{
    public enum ProcessWindowStyle { Normal = 0, Hidden = 1, Minimized = 2, Maximized = 3 }

    public enum ProcessPriorityClass
    {
        Normal = 32, Idle = 64, High = 128, RealTime = 256, BelowNormal = 16384, AboveNormal = 32768
    }

    public class ProcessStartInfo
    {
        public ProcessStartInfo() { }
        public ProcessStartInfo(string fileName) { FileName = fileName; }
        public ProcessStartInfo(string fileName, string arguments) { FileName = fileName; Arguments = arguments; }

        public string FileName { get; set; } = string.Empty;
        public string Arguments { get; set; } = string.Empty;
        public Collections.ObjectModel.Collection<string> ArgumentList { get; } = new Collections.ObjectModel.Collection<string>();
        public string WorkingDirectory { get; set; } = string.Empty;
        public bool UseShellExecute { get; set; }
        public bool CreateNoWindow { get; set; }
        public bool RedirectStandardOutput { get; set; }
        public bool RedirectStandardError { get; set; }
        public bool RedirectStandardInput { get; set; }
        public Encoding StandardOutputEncoding { get; set; }
        public Encoding StandardErrorEncoding { get; set; }
        public Encoding StandardInputEncoding { get; set; }
        public ProcessWindowStyle WindowStyle { get; set; }
        public string Verb { get; set; } = string.Empty;
        public string[] Verbs => Array.Empty<string>();
        public bool ErrorDialog { get; set; }
        public IntPtr ErrorDialogParentHandle { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string Domain { get; set; } = string.Empty;
        public bool LoadUserProfile { get; set; }
        public IDictionary<string, string> Environment { get; } = new Dictionary<string, string>();
        public StringDictionary EnvironmentVariables { get; } = new StringDictionary();
    }

    /// <summary>Kept so code assigning ProcessStartInfo.EnvironmentVariables still compiles/binds.</summary>
    public class StringDictionary : IEnumerable
    {
        private readonly Dictionary<string, string> _inner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string this[string key] { get => _inner.TryGetValue(key, out string v) ? v : null; set => _inner[key] = value; }
        public int Count => _inner.Count;
        public ICollection Keys => _inner.Keys;
        public ICollection Values => _inner.Values;
        public void Add(string key, string value) => _inner[key] = value;
        public void Clear() => _inner.Clear();
        public bool ContainsKey(string key) => _inner.ContainsKey(key);
        public void Remove(string key) => _inner.Remove(key);
        public IEnumerator GetEnumerator() => _inner.GetEnumerator();
    }

    public class DataReceivedEventArgs : EventArgs
    {
        internal DataReceivedEventArgs(string data) { Data = data; }
        public string Data { get; }
    }

    public delegate void DataReceivedEventHandler(object sender, DataReceivedEventArgs e);

    public class ProcessModule
    {
        public string ModuleName { get; internal set; } = string.Empty;
        public string FileName { get; internal set; } = string.Empty;
        public IntPtr BaseAddress { get; internal set; }
        public IntPtr EntryPointAddress { get; internal set; }
        public int ModuleMemorySize { get; internal set; }
        public FileVersionInfo FileVersionInfo => null;
        public override string ToString() => ModuleName;
    }

    public class ProcessModuleCollection : Collections.ObjectModel.ReadOnlyCollection<ProcessModule>
    {
        internal ProcessModuleCollection(IList<ProcessModule> list) : base(list) { }
    }

    public class ProcessThread
    {
        public int Id { get; internal set; }
        public ThreadState ThreadState => ThreadState.Running;
        public TimeSpan TotalProcessorTime => TimeSpan.Zero;
        public TimeSpan UserProcessorTime => TimeSpan.Zero;
        public TimeSpan PrivilegedProcessorTime => TimeSpan.Zero;
        public DateTime StartTime => Process.ProcessStartTime;
        public IntPtr StartAddress => IntPtr.Zero;
    }

    public enum ThreadState { Initialized, Ready, Running, Standby, Terminated, Wait, Transition, Unknown }

    public class ProcessThreadCollection : Collections.ObjectModel.ReadOnlyCollection<ProcessThread>
    {
        internal ProcessThreadCollection(IList<ProcessThread> list) : base(list) { }
    }

    public class Process : Component
    {
        // Computed, NOT a static field. A static initializer gives the type a .cctor, and the wasm
        // interpreter refused to prepare one here ("NIY encountered in method
        // System.Diagnostics.Process:.cctor", then a fatal assertion at interp.c) — fatal for the app,
        // since the type is touched during start-up. Uptime derives from the runtime's own tick count,
        // which needs no initialization at all.
        internal static DateTime ProcessStartTime => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

        private const string CannotSpawn =
            "Starting or controlling an operating-system process is not possible in a browser: there is no " +
            "process table and no way to spawn one. Self-inspection (Process.GetCurrentProcess and its " +
            "memory/CPU/thread properties) is supported.";

        private readonly bool _isCurrent;

        public Process() { }
        private Process(bool current) { _isCurrent = current; }

        public static Process GetCurrentProcess() => new Process(true);

        /// <summary>There is exactly one "process" here: this page.</summary>
        public static Process[] GetProcesses() => new[] { GetCurrentProcess() };
        public static Process[] GetProcesses(string machineName) => GetProcesses();
        public static Process[] GetProcessesByName(string processName) =>
            string.Equals(processName, CurrentProcessName, StringComparison.OrdinalIgnoreCase)
                ? new[] { GetCurrentProcess() } : Array.Empty<Process>();
        public static Process[] GetProcessesByName(string processName, string machineName) => GetProcessesByName(processName);

        public static Process GetProcessById(int processId) =>
            processId == CurrentProcessId ? GetCurrentProcess()
                                          : throw new ArgumentException("No process with that id is running in a browser.");
        public static Process GetProcessById(int processId, string machineName) => GetProcessById(processId);

        public static Process Start(string fileName) => throw new PlatformNotSupportedException(CannotSpawn);
        public static Process Start(string fileName, string arguments) => throw new PlatformNotSupportedException(CannotSpawn);
        public static Process Start(string fileName, IEnumerable<string> arguments) => throw new PlatformNotSupportedException(CannotSpawn);
        public static Process Start(ProcessStartInfo startInfo) => throw new PlatformNotSupportedException(CannotSpawn);
        public bool Start() => throw new PlatformNotSupportedException(CannotSpawn);

        public static void EnterDebugMode() { }
        public static void LeaveDebugMode() { }

        // ---- identity ---------------------------------------------------------------------------

        private const int CurrentProcessId = 1;   // const, so it contributes no .cctor either
        private static string CurrentProcessName =>
            Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "wasm";

        public int Id => _isCurrent ? CurrentProcessId : throw new InvalidOperationException("Process not started.");
        public string ProcessName => _isCurrent ? CurrentProcessName : string.Empty;
        public string MachineName => ".";
        public IntPtr Handle => IntPtr.Zero;
        public Runtime.InteropServices.SafeHandle SafeHandle => null;
        public int SessionId => 0;
        public bool Responding => true;
        public bool HasExited => false;
        public int ExitCode => throw new InvalidOperationException("Process has not exited.");
        public DateTime StartTime => ProcessStartTime;
        public DateTime ExitTime => throw new InvalidOperationException("Process has not exited.");
        public ProcessPriorityClass PriorityClass { get => ProcessPriorityClass.Normal; set { } }
        public bool PriorityBoostEnabled { get => false; set { } }
        public IntPtr ProcessorAffinity { get => IntPtr.Zero; set { } }
        public IntPtr MainWindowHandle => IntPtr.Zero;
        public string MainWindowTitle => string.Empty;
        public bool EnableRaisingEvents { get; set; }
        public ProcessStartInfo StartInfo { get; set; } = new ProcessStartInfo();
        public ISynchronizeInvoke SynchronizingObject { get; set; }

        // ---- resource usage ---------------------------------------------------------------------
        //
        // Real numbers where the runtime has them. GC.GetTotalMemory is the honest analogue of a
        // working set in a runtime that does not own an OS process, and uptime is a true measure of
        // processor time for a single-threaded page.

        public long WorkingSet64 => GC.GetTotalMemory(false);
        public long PrivateMemorySize64 => GC.GetTotalMemory(false);
        public long VirtualMemorySize64 => GC.GetTotalMemory(false);
        public long PagedMemorySize64 => GC.GetTotalMemory(false);
        public long PagedSystemMemorySize64 => 0;
        public long NonpagedSystemMemorySize64 => 0;
        public long PeakWorkingSet64 => GC.GetTotalMemory(false);
        public long PeakVirtualMemorySize64 => GC.GetTotalMemory(false);
        public long PeakPagedMemorySize64 => GC.GetTotalMemory(false);
        [Obsolete("Use WorkingSet64")] public int WorkingSet => (int)Math.Min(int.MaxValue, WorkingSet64);
        [Obsolete("Use PrivateMemorySize64")] public int PrivateMemorySize => (int)Math.Min(int.MaxValue, PrivateMemorySize64);
        [Obsolete("Use VirtualMemorySize64")] public int VirtualMemorySize => (int)Math.Min(int.MaxValue, VirtualMemorySize64);
        [Obsolete("Use PagedMemorySize64")] public int PagedMemorySize => (int)Math.Min(int.MaxValue, PagedMemorySize64);
        [Obsolete("Use PeakWorkingSet64")] public int PeakWorkingSet => (int)Math.Min(int.MaxValue, PeakWorkingSet64);

        // ZERO, not uptime. A browser exposes no processor-time counter, and reporting wall-clock in
        // its place is worse than admitting ignorance: the standard way to derive a CPU percentage is
        // cpuDelta / (elapsed * ProcessorCount), so wall-clock makes that expression equal 1 — every
        // app that measures itself concludes it is pegged at 100%, permanently. WpfHexEditorIDE's
        // plugin monitor duly reported 100% CPU overall and 80% for a single idle plugin, and sent us
        // chasing a load that did not exist. Zero reads as "not measured" and skews nothing.
        public TimeSpan TotalProcessorTime => TimeSpan.Zero;
        public TimeSpan UserProcessorTime => TimeSpan.Zero;
        public TimeSpan PrivilegedProcessorTime => TimeSpan.Zero;
        public int HandleCount => 0;
        public int BasePriority => 8;
        public int MinWorkingSet { get => 0; set { } }
        public int MaxWorkingSet { get => 0; set { } }

        public ProcessThreadCollection Threads
        {
            get
            {
                var threads = new List<ProcessThread> { new ProcessThread { Id = Thread.CurrentThread.ManagedThreadId } };
                return new ProcessThreadCollection(threads);
            }
        }

        public ProcessModuleCollection Modules => new ProcessModuleCollection(new List<ProcessModule> { MainModule });

        public ProcessModule MainModule => new ProcessModule
        {
            ModuleName = CurrentProcessName + ".wasm",
            FileName = CurrentProcessName + ".wasm",
        };

        // ---- redirected streams -----------------------------------------------------------------
        //
        // Nothing was ever started, so these are empty rather than null: code that reads to the end
        // of stdout gets "no output" instead of a NullReferenceException.

        public StreamReader StandardOutput => StreamReader.Null;
        public StreamReader StandardError => StreamReader.Null;
        public StreamWriter StandardInput => StreamWriter.Null;

        public event DataReceivedEventHandler OutputDataReceived;
        public event DataReceivedEventHandler ErrorDataReceived;
        public event EventHandler Exited;

        public void BeginOutputReadLine() { }
        public void BeginErrorReadLine() { }
        public void CancelOutputRead() { }
        public void CancelErrorRead() { }

        // ---- lifetime ---------------------------------------------------------------------------

        public void Refresh() { }
        public void Close() { }

        public void Kill() => throw new PlatformNotSupportedException(CannotSpawn);
        public void Kill(bool entireProcessTree) => throw new PlatformNotSupportedException(CannotSpawn);
        public bool CloseMainWindow() => false;

        public void WaitForExit() => throw new PlatformNotSupportedException(CannotSpawn);
        public bool WaitForExit(int milliseconds) => throw new PlatformNotSupportedException(CannotSpawn);
        public bool WaitForExit(TimeSpan timeout) => throw new PlatformNotSupportedException(CannotSpawn);
        public Task WaitForExitAsync(Threading.CancellationToken cancellationToken = default) =>
            Task.FromException(new PlatformNotSupportedException(CannotSpawn));
        public bool WaitForInputIdle() => false;
        public bool WaitForInputIdle(int milliseconds) => false;

        public override string ToString() => _isCurrent ? CurrentProcessName : base.ToString();

        // Referenced by the event fields above so the compiler does not warn them unused, and so a
        // derived class can raise them.
        protected void OnExited() => Exited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Minimal FileVersionInfo so ProcessModule.FileVersionInfo resolves.</summary>
    public sealed class FileVersionInfo
    {
        private FileVersionInfo() { }
        public string FileName => string.Empty;
        public string FileVersion => string.Empty;
        public string ProductVersion => string.Empty;
        public string CompanyName => string.Empty;
        public string ProductName => string.Empty;
        public static FileVersionInfo GetVersionInfo(string fileName) => new FileVersionInfo();
    }
}
