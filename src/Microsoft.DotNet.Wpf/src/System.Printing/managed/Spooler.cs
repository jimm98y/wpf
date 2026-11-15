// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The print spooler under System.Printing's queue model: its printers and their state, their jobs, and what can be
// done to either -- what the C++/CLI System.Printing reached through winspool, and what it is here on every head:
// winspool itself on Windows (EnumPrinters, SetPrinter, EnumJobs, SetJob, StartDocPrinter...), CUPS on macOS and
// Linux (its destinations and jobs, and IPP's operations on them), nothing where there is no print system.
//
// The model above (PrintQueue, PrintSystemJobInfo, PrintServer) asks this for what it shows and tells it what is
// committed; it keeps no state of the spooler's own.
//

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace System.Printing
{
    /// <summary>A printer as the spooler describes it.</summary>
    internal sealed class QueueState
    {
        public string Name = string.Empty;
        public string ShareName = string.Empty;
        public string Comment = string.Empty;
        public string Location = string.Empty;
        public string Description = string.Empty;
        public string DriverName = string.Empty;
        public string PortName = string.Empty;
        public string ProcessorName = string.Empty;
        public string SeparatorFile = string.Empty;
        public int Priority = 1;
        public int DefaultPriority;
        public int StartTimeOfDay;
        public int UntilTimeOfDay;
        public int AveragePagesPerMinute;
        public int NumberOfJobs;
        public PrintQueueAttributes Attributes = PrintQueueAttributes.Queued;
        public PrintQueueStatus Status;
        public bool IsDefault;
    }

    /// <summary>A job as the spooler describes it.</summary>
    internal sealed class JobState
    {
        public int Id;
        public string Name = string.Empty;
        public string Submitter = string.Empty;
        public PrintJobStatus Status;
        public int Priority = 1;
        public int Position;
        public int Size;
        public int Pages;
        public int PagesPrinted;
        public int StartTimeOfDay;
        public int UntilTimeOfDay;
        public int TimeSinceStartedPrinting;
        public DateTime Submitted;
    }

    internal enum QueueControl { Pause, Resume, Purge }

    internal enum JobControl { Pause, Resume, Cancel, Restart }

    /// <summary>A job being written: its id, and where its bytes go.</summary>
    internal abstract class JobWriter
    {
        public int Id { get; protected set; }
        public abstract void Write(byte[] buffer, int offset, int count);
        public abstract void Commit();
        public abstract void Abort();
    }

    internal interface IPrintSpooler
    {
        IReadOnlyList<QueueState> Queues();
        QueueState Queue(string name);
        void SetQueue(QueueState state);
        void ControlQueue(string queue, QueueControl control);
        bool SetDefault(string queue);
        bool DeleteQueue(string queue);
        bool InstallQueue(QueueState state, string[] ports);
        bool Connect(string path);
        bool Disconnect(string path);
        IReadOnlyList<JobState> Jobs(string queue);
        void ControlJob(string queue, int id, JobControl control);
        void RenameJob(string queue, int id, string name);
        JobWriter StartJob(string queue, string name, bool xps);
        string SpoolDirectory { get; }
        int MajorVersion { get; }
        int MinorVersion { get; }
    }

    internal static class Spooler
    {
        private static IPrintSpooler s_current;

        internal static IPrintSpooler Current => s_current ??=
            OperatingSystem.IsWindows() ? new WinSpooler()
            : (OperatingSystem.IsMacOS() || (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())) && CupsSpooler.IsAvailable ? new CupsSpooler()
            : new NoSpooler();

        // the printing exceptions take a resource key: the generic one, the spooler's own words inside
        internal static PrintSystemException Failed(string what) =>
            new PrintSystemException("PrintSystemException.Generic", new InvalidOperationException($"The print spooler could not {what}."));
    }

    /// <summary>A machine with no print system: no printers.</summary>
    internal sealed class NoSpooler : IPrintSpooler
    {
        public IReadOnlyList<QueueState> Queues() => Array.Empty<QueueState>();
        public QueueState Queue(string name) => null;
        public void SetQueue(QueueState state) => throw Spooler.Failed("change a printer");
        public void ControlQueue(string queue, QueueControl control) => throw Spooler.Failed("find the printer");
        public bool SetDefault(string queue) => false;
        public bool DeleteQueue(string queue) => false;
        public bool InstallQueue(QueueState state, string[] ports) => false;
        public bool Connect(string path) => false;
        public bool Disconnect(string path) => false;
        public IReadOnlyList<JobState> Jobs(string queue) => Array.Empty<JobState>();
        public void ControlJob(string queue, int id, JobControl control) => throw Spooler.Failed("find the job");
        public void RenameJob(string queue, int id, string name) => throw Spooler.Failed("find the job");
        public JobWriter StartJob(string queue, string name, bool xps) => throw Spooler.Failed("start a job");
        public string SpoolDirectory => string.Empty;
        public int MajorVersion => 0;
        public int MinorVersion => 0;
    }

    // ---- Windows: winspool ------------------------------------------------------------------------

    internal sealed unsafe class WinSpooler : IPrintSpooler
    {
        private const int PRINTER_CONTROL_PAUSE = 1, PRINTER_CONTROL_RESUME = 2, PRINTER_CONTROL_PURGE = 3;
        private const int JOB_CONTROL_PAUSE = 1, JOB_CONTROL_RESUME = 2, JOB_CONTROL_CANCEL = 3, JOB_CONTROL_RESTART = 4;
        private const int PRINTER_ENUM_LOCAL = 2, PRINTER_ENUM_CONNECTIONS = 4;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PRINTER_INFO_2
        {
            public IntPtr pServerName, pPrinterName, pShareName, pPortName, pDriverName, pComment, pLocation, pDevMode,
                pSepFile, pPrintProcessor, pDatatype, pParameters, pSecurityDescriptor;
            public uint Attributes, Priority, DefaultPriority, StartTime, UntilTime, Status, cJobs, AveragePPM;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEMTIME
        {
            public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct JOB_INFO_2
        {
            public uint JobId;
            public IntPtr pPrinterName, pMachineName, pUserName, pDocument, pNotifyName, pDatatype, pPrintProcessor,
                pParameters, pDriverName, pDevMode, pStatus, pSecurityDescriptor;
            public uint Status, Priority, Position, StartTime, UntilTime, TotalPages, Size;
            public SYSTEMTIME Submitted;
            public uint Time, PagesPrinted;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DOC_INFO_1
        {
            public string pDocName, pOutputFile, pDatatype;
        }

        private static string Str(IntPtr p) => p == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(p);

        public IReadOnlyList<QueueState> Queues()
        {
            var result = new List<QueueState>();
            string defaultName = DefaultPrinter();
            uint needed, returned;
            EnumPrintersW(PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS, null, 2, IntPtr.Zero, 0, out needed, out returned);
            if (needed == 0) return result;
            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!EnumPrintersW(PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS, null, 2, buffer, needed, out needed, out returned))
                    return result;
                for (int i = 0; i < returned; i++)
                {
                    var info = Marshal.PtrToStructure<PRINTER_INFO_2>(buffer + i * sizeof(PRINTER_INFO_2));
                    result.Add(FromInfo(info, defaultName));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            return result;
        }

        private static QueueState FromInfo(PRINTER_INFO_2 info, string defaultName)
        {
            string name = Str(info.pPrinterName);
            return new QueueState
            {
                Name = name,
                ShareName = Str(info.pShareName),
                Comment = Str(info.pComment),
                Location = Str(info.pLocation),
                Description = name + "," + Str(info.pDriverName) + "," + Str(info.pLocation),
                DriverName = Str(info.pDriverName),
                PortName = Str(info.pPortName),
                ProcessorName = Str(info.pPrintProcessor),
                SeparatorFile = Str(info.pSepFile),
                Attributes = (PrintQueueAttributes)(info.Attributes & 0x3BAB),
                Priority = (int)info.Priority,
                DefaultPriority = (int)info.DefaultPriority,
                StartTimeOfDay = (int)info.StartTime,
                UntilTimeOfDay = (int)info.UntilTime,
                Status = (PrintQueueStatus)info.Status,
                NumberOfJobs = (int)info.cJobs,
                AveragePagesPerMinute = (int)info.AveragePPM,
                IsDefault = string.Equals(name, defaultName, StringComparison.OrdinalIgnoreCase),
            };
        }

        public QueueState Queue(string name)
        {
            if (!OpenPrinterW(name, out IntPtr handle, IntPtr.Zero)) return null;
            try
            {
                GetPrinterW(handle, 2, IntPtr.Zero, 0, out uint needed);
                if (needed == 0) return null;
                IntPtr buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!GetPrinterW(handle, 2, buffer, needed, out needed)) return null;
                    return FromInfo(Marshal.PtrToStructure<PRINTER_INFO_2>(buffer), DefaultPrinter());
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { ClosePrinter(handle); }
        }

        public void SetQueue(QueueState state)
        {
            if (!OpenPrinterW(state.Name, out IntPtr handle, IntPtr.Zero)) throw Spooler.Failed("open the printer " + state.Name);
            try
            {
                GetPrinterW(handle, 2, IntPtr.Zero, 0, out uint needed);
                IntPtr buffer = Marshal.AllocHGlobal((int)Math.Max(needed, 1));
                var strings = new List<IntPtr>();
                IntPtr Keep(string s) { IntPtr p = Marshal.StringToHGlobalUni(s ?? string.Empty); strings.Add(p); return p; }
                try
                {
                    if (!GetPrinterW(handle, 2, buffer, needed, out needed)) throw Spooler.Failed("read the printer " + state.Name);
                    var info = Marshal.PtrToStructure<PRINTER_INFO_2>(buffer);
                    info.pShareName = Keep(state.ShareName);
                    info.pComment = Keep(state.Comment);
                    info.pLocation = Keep(state.Location);
                    info.pSepFile = Keep(state.SeparatorFile);
                    info.Priority = (uint)state.Priority;
                    info.DefaultPriority = (uint)state.DefaultPriority;
                    info.StartTime = (uint)state.StartTimeOfDay;
                    info.UntilTime = (uint)state.UntilTimeOfDay;
                    info.pSecurityDescriptor = IntPtr.Zero;
                    if (!SetPrinterW(handle, 2, (IntPtr)(&info), 0)) throw Spooler.Failed("change the printer " + state.Name);
                }
                finally
                {
                    foreach (IntPtr p in strings) Marshal.FreeHGlobal(p);
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally { ClosePrinter(handle); }
        }

        public void ControlQueue(string queue, QueueControl control)
        {
            if (!OpenPrinterW(queue, out IntPtr handle, IntPtr.Zero)) throw Spooler.Failed("open the printer " + queue);
            try
            {
                int command = control switch { QueueControl.Pause => PRINTER_CONTROL_PAUSE, QueueControl.Resume => PRINTER_CONTROL_RESUME, _ => PRINTER_CONTROL_PURGE };
                if (!SetPrinterW(handle, 0, IntPtr.Zero, command)) throw Spooler.Failed(control.ToString().ToLowerInvariant() + " the printer " + queue);
            }
            finally { ClosePrinter(handle); }
        }

        public bool SetDefault(string queue) => SetDefaultPrinterW(queue);

        public bool DeleteQueue(string queue)
        {
            if (!OpenPrinterW(queue, out IntPtr handle, IntPtr.Zero)) return false;
            try { return DeletePrinter(handle); }
            finally { ClosePrinter(handle); }
        }

        public bool InstallQueue(QueueState state, string[] ports)
        {
            var strings = new List<IntPtr>();
            IntPtr Keep(string s) { IntPtr p = Marshal.StringToHGlobalUni(s ?? string.Empty); strings.Add(p); return p; }
            try
            {
                var info = new PRINTER_INFO_2
                {
                    pPrinterName = Keep(state.Name),
                    pShareName = Keep(state.ShareName),
                    pPortName = Keep(string.Join(",", ports ?? Array.Empty<string>())),
                    pDriverName = Keep(state.DriverName),
                    pComment = Keep(state.Comment),
                    pLocation = Keep(state.Location),
                    pSepFile = Keep(state.SeparatorFile),
                    pPrintProcessor = Keep(string.IsNullOrEmpty(state.ProcessorName) ? "WinPrint" : state.ProcessorName),
                    pDatatype = Keep("RAW"),
                    Attributes = (uint)state.Attributes,
                    Priority = (uint)Math.Max(1, state.Priority),
                    DefaultPriority = (uint)state.DefaultPriority,
                    StartTime = (uint)state.StartTimeOfDay,
                    UntilTime = (uint)state.UntilTimeOfDay,
                };
                IntPtr handle = AddPrinterW(null, 2, (IntPtr)(&info));
                if (handle == IntPtr.Zero) return false;
                ClosePrinter(handle);
                return true;
            }
            finally { foreach (IntPtr p in strings) Marshal.FreeHGlobal(p); }
        }

        public bool Connect(string path) => AddPrinterConnectionW(path);

        public bool Disconnect(string path) => DeletePrinterConnectionW(path);

        public IReadOnlyList<JobState> Jobs(string queue)
        {
            var result = new List<JobState>();
            if (!OpenPrinterW(queue, out IntPtr handle, IntPtr.Zero)) return result;
            try
            {
                EnumJobsW(handle, 0, int.MaxValue, 2, IntPtr.Zero, 0, out uint needed, out uint returned);
                if (needed == 0) return result;
                IntPtr buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (!EnumJobsW(handle, 0, int.MaxValue, 2, buffer, needed, out needed, out returned)) return result;
                    for (int i = 0; i < returned; i++)
                    {
                        var j = Marshal.PtrToStructure<JOB_INFO_2>(buffer + i * sizeof(JOB_INFO_2));
                        DateTime submitted;
                        try
                        {
                            submitted = new DateTime(j.Submitted.wYear, j.Submitted.wMonth, j.Submitted.wDay, j.Submitted.wHour,
                                j.Submitted.wMinute, j.Submitted.wSecond, j.Submitted.wMilliseconds, DateTimeKind.Utc);
                        }
                        catch (ArgumentOutOfRangeException) { submitted = DateTime.MinValue; }
                        result.Add(new JobState
                        {
                            Id = (int)j.JobId,
                            Name = Str(j.pDocument),
                            Submitter = Str(j.pUserName),
                            Status = (PrintJobStatus)j.Status,
                            Priority = (int)j.Priority,
                            Position = (int)j.Position,
                            Size = (int)j.Size,
                            Pages = (int)j.TotalPages,
                            PagesPrinted = (int)j.PagesPrinted,
                            StartTimeOfDay = (int)j.StartTime,
                            UntilTimeOfDay = (int)j.UntilTime,
                            TimeSinceStartedPrinting = (int)j.Time,
                            Submitted = submitted,
                        });
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { ClosePrinter(handle); }
            return result;
        }

        public void ControlJob(string queue, int id, JobControl control)
        {
            if (!OpenPrinterW(queue, out IntPtr handle, IntPtr.Zero)) throw Spooler.Failed("open the printer " + queue);
            try
            {
                int command = control switch { JobControl.Pause => JOB_CONTROL_PAUSE, JobControl.Resume => JOB_CONTROL_RESUME, JobControl.Cancel => JOB_CONTROL_CANCEL, _ => JOB_CONTROL_RESTART };
                if (!SetJobW(handle, (uint)id, 0, IntPtr.Zero, (uint)command)) throw Spooler.Failed(control.ToString().ToLowerInvariant() + " job " + id);
            }
            finally { ClosePrinter(handle); }
        }

        public void RenameJob(string queue, int id, string name)
        {
            if (!OpenPrinterW(queue, out IntPtr handle, IntPtr.Zero)) throw Spooler.Failed("open the printer " + queue);
            try
            {
                GetJobW(handle, (uint)id, 2, IntPtr.Zero, 0, out uint needed);
                if (needed == 0) throw Spooler.Failed("find job " + id);
                IntPtr buffer = Marshal.AllocHGlobal((int)needed);
                IntPtr text = Marshal.StringToHGlobalUni(name ?? string.Empty);
                try
                {
                    if (!GetJobW(handle, (uint)id, 2, buffer, needed, out needed)) throw Spooler.Failed("read job " + id);
                    var job = Marshal.PtrToStructure<JOB_INFO_2>(buffer);
                    job.pDocument = text;
                    job.Position = 0;   // JOB_POSITION_UNSPECIFIED
                    job.pSecurityDescriptor = IntPtr.Zero;
                    if (!SetJobW(handle, (uint)id, 2, (IntPtr)(&job), 0)) throw Spooler.Failed("rename job " + id);
                }
                finally
                {
                    Marshal.FreeHGlobal(text);
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally { ClosePrinter(handle); }
        }

        public JobWriter StartJob(string queue, string name, bool xps) => new WinJobWriter(queue, name, xps);

        private sealed class WinJobWriter : JobWriter
        {
            private IntPtr _handle;

            public WinJobWriter(string queue, string name, bool xps)
            {
                if (!OpenPrinterW(queue, out _handle, IntPtr.Zero)) throw Spooler.Failed("open the printer " + queue);
                var doc = new DOC_INFO_1 { pDocName = name ?? "Print job", pDatatype = xps ? "XPS_PASS" : "RAW" };
                int id = StartDocPrinterW(_handle, 1, ref doc);
                if (id == 0)
                {
                    ClosePrinter(_handle);
                    throw Spooler.Failed("start a job on " + queue);
                }
                Id = id;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                fixed (byte* p = buffer)
                {
                    if (!WritePrinter(_handle, (IntPtr)(p + offset), (uint)count, out uint written) || written != count)
                        throw Spooler.Failed("write job " + Id);
                }
            }

            public override void Commit()
            {
                if (_handle == IntPtr.Zero) return;
                EndDocPrinter(_handle);
                ClosePrinter(_handle);
                _handle = IntPtr.Zero;
            }

            public override void Abort()
            {
                if (_handle == IntPtr.Zero) return;
                SetJobW(_handle, (uint)Id, 0, IntPtr.Zero, JOB_CONTROL_CANCEL);
                EndDocPrinter(_handle);
                ClosePrinter(_handle);
                _handle = IntPtr.Zero;
            }
        }

        public string SpoolDirectory
        {
            get
            {
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                return Path.Combine(windows, "System32", "spool", "PRINTERS");
            }
        }

        public int MajorVersion => 3;
        public int MinorVersion => 0;

        private static string DefaultPrinter()
        {
            uint size = 0;
            GetDefaultPrinterW(null, ref size);
            if (size == 0) return string.Empty;
            var text = new StringBuilder((int)size);
            return GetDefaultPrinterW(text, ref size) ? text.ToString() : string.Empty;
        }

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool EnumPrintersW(int flags, string name, int level, IntPtr buffer, uint size, out uint needed, out uint returned);
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool OpenPrinterW(string name, out IntPtr handle, IntPtr defaults);
        [DllImport("winspool.drv", SetLastError = true)] private static extern bool ClosePrinter(IntPtr handle);
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetPrinterW(IntPtr handle, int level, IntPtr buffer, uint size, out uint needed);
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetPrinterW(IntPtr handle, int level, IntPtr info, int command);
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr AddPrinterW(string server, int level, IntPtr info);
        [DllImport("winspool.drv", SetLastError = true)] private static extern bool DeletePrinter(IntPtr handle);
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool AddPrinterConnectionW(string name);
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool DeletePrinterConnectionW(string name);
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetDefaultPrinterW(StringBuilder buffer, ref uint size);
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetDefaultPrinterW(string name);
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool EnumJobsW(IntPtr handle, uint first, int count, int level, IntPtr buffer, uint size, out uint needed, out uint returned);
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetJobW(IntPtr handle, uint id, int level, IntPtr buffer, uint size, out uint needed);
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetJobW(IntPtr handle, uint id, int level, IntPtr info, uint command);
        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int StartDocPrinterW(IntPtr handle, int level, ref DOC_INFO_1 info);
        [DllImport("winspool.drv", SetLastError = true)] private static extern bool WritePrinter(IntPtr handle, IntPtr buffer, uint count, out uint written);
        [DllImport("winspool.drv", SetLastError = true)] private static extern bool EndDocPrinter(IntPtr handle);
    }

    // ---- macOS and Linux: CUPS ---------------------------------------------------------------------

    internal sealed unsafe class CupsSpooler : IPrintSpooler
    {
        // ipp_op_t, ipp_tag_t, ipp_jstate_t
        private const int IPP_OP_HOLD_JOB = 0x000C, IPP_OP_RELEASE_JOB = 0x000D, IPP_OP_RESTART_JOB = 0x000E,
            IPP_OP_PAUSE_PRINTER = 0x0010, IPP_OP_RESUME_PRINTER = 0x0011, IPP_OP_PURGE_JOBS = 0x0012,
            IPP_OP_SET_JOB_ATTRIBUTES = 0x0014, IPP_OP_CUPS_DELETE_PRINTER = 0x4004;
        private const int IPP_TAG_OPERATION = 0x01, IPP_TAG_JOB = 0x02, IPP_TAG_INTEGER = 0x21, IPP_TAG_NAME = 0x42, IPP_TAG_URI = 0x45;
        private const int CUPS_WHICHJOBS_ACTIVE = 0;
        private const string Lib = "libcups";

        [StructLayout(LayoutKind.Sequential)]
        private struct cups_option_t { public IntPtr name, value; }

        [StructLayout(LayoutKind.Sequential)]
        private struct cups_dest_t { public IntPtr name, instance; public int is_default, num_options; public IntPtr options; }

        [StructLayout(LayoutKind.Sequential)]
        private struct cups_job_t
        {
            public int id;
            public IntPtr dest, title, user, format;
            public int state, size, priority;
            public nint completed_time, creation_time, processing_time;
        }

        internal static bool IsAvailable
        {
            get
            {
                try { return NativeLibrary.TryLoad(OperatingSystem.IsMacOS() ? "/usr/lib/libcups.2.dylib" : "libcups.so.2", out _); }
                catch (Exception) { return false; }
            }
        }

        private static string Str(IntPtr p) => p == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(p);

        private static Dictionary<string, string> Options(cups_dest_t dest)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < dest.num_options; i++)
            {
                var o = Marshal.PtrToStructure<cups_option_t>(dest.options + i * sizeof(cups_option_t));
                options[Str(o.name)] = Str(o.value);
            }
            return options;
        }

        public IReadOnlyList<QueueState> Queues()
        {
            var result = new List<QueueState>();
            IntPtr dests = IntPtr.Zero;
            int count = cupsGetDests(ref dests);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var dest = Marshal.PtrToStructure<cups_dest_t>(dests + i * sizeof(cups_dest_t));
                    if (dest.instance != IntPtr.Zero) continue;    // an instance of a printer, not a printer
                    result.Add(FromDest(dest));
                }
            }
            finally { if (dests != IntPtr.Zero) cupsFreeDests(count, dests); }
            return result;
        }

        private static QueueState FromDest(cups_dest_t dest)
        {
            Dictionary<string, string> o = Options(dest);
            string Get(string key) => o.TryGetValue(key, out string v) ? v : string.Empty;
            string name = Str(dest.name);
            string reasons = Get("printer-state-reasons");
            var status = PrintQueueStatus.None;
            switch (Get("printer-state"))
            {
                case "4": status |= PrintQueueStatus.Printing | PrintQueueStatus.Busy; break;
                case "5": status |= PrintQueueStatus.Paused; break;
            }
            foreach (string reason in reasons.Split(','))
            {
                string r = reason.Trim();
                if (r.StartsWith("media-empty", StringComparison.Ordinal) || r.StartsWith("media-needed", StringComparison.Ordinal)) status |= PrintQueueStatus.PaperOut;
                else if (r.StartsWith("media-jam", StringComparison.Ordinal)) status |= PrintQueueStatus.PaperJam;
                else if (r.StartsWith("toner-low", StringComparison.Ordinal) || r.StartsWith("marker-supply-low", StringComparison.Ordinal)) status |= PrintQueueStatus.TonerLow;
                else if (r.StartsWith("toner-empty", StringComparison.Ordinal) || r.StartsWith("marker-supply-empty", StringComparison.Ordinal)) status |= PrintQueueStatus.NoToner;
                else if (r.StartsWith("door-open", StringComparison.Ordinal) || r.StartsWith("cover-open", StringComparison.Ordinal)) status |= PrintQueueStatus.DoorOpen;
                else if (r.StartsWith("offline", StringComparison.Ordinal) || r.StartsWith("connecting-to-device", StringComparison.Ordinal)) status |= PrintQueueStatus.Offline;
                else if (r.StartsWith("output-area-full", StringComparison.Ordinal)) status |= PrintQueueStatus.OutputBinFull;
                else if (r.StartsWith("paused", StringComparison.Ordinal)) status |= PrintQueueStatus.Paused;
                else if (r.StartsWith("moving-to-paused", StringComparison.Ordinal)) status |= PrintQueueStatus.Paused | PrintQueueStatus.Busy;
                else if (r.EndsWith("-error", StringComparison.Ordinal)) status |= PrintQueueStatus.Error;
            }
            if (Get("printer-is-accepting-jobs") == "false") status |= PrintQueueStatus.NotAvailable;
            var attributes = PrintQueueAttributes.Queued;
            if (Get("printer-is-shared") == "true") attributes |= PrintQueueAttributes.Shared;
            int.TryParse(Get("queued-job-count"), out int jobs);
            return new QueueState
            {
                Name = name,
                ShareName = Get("printer-is-shared") == "true" ? name : string.Empty,
                Comment = Get("printer-info"),
                Location = Get("printer-location"),
                Description = name + "," + Get("printer-make-and-model") + "," + Get("printer-location"),
                DriverName = Get("printer-make-and-model"),
                PortName = Get("device-uri"),
                ProcessorName = "cups",
                Attributes = attributes,
                Status = status,
                NumberOfJobs = jobs,
                IsDefault = dest.is_default != 0,
            };
        }

        public QueueState Queue(string name)
        {
            foreach (QueueState q in Queues())
                if (string.Equals(q.Name, name, StringComparison.Ordinal)) return q;
            return null;
        }

        public void SetQueue(QueueState state)
        {
            // the description and location, as lpadmin sets them (CUPS keeps no share name, priority or times)
            Run("lpadmin", "-p", state.Name, "-D", state.Comment ?? string.Empty, "-L", state.Location ?? string.Empty);
        }

        public void ControlQueue(string queue, QueueControl control)
        {
            int op = control switch { QueueControl.Pause => IPP_OP_PAUSE_PRINTER, QueueControl.Resume => IPP_OP_RESUME_PRINTER, _ => IPP_OP_PURGE_JOBS };
            if (!Request(op, queue, 0, null, "/admin/"))
                throw Spooler.Failed(control.ToString().ToLowerInvariant() + " the printer " + queue + ": " + LastError());
        }

        public bool SetDefault(string queue) => Run("lpoptions", "-d", queue);

        public bool DeleteQueue(string queue) => Request(IPP_OP_CUPS_DELETE_PRINTER, queue, 0, null, "/admin/");

        public bool InstallQueue(QueueState state, string[] ports)
        {
            var args = new List<string> { "-p", state.Name, "-E" };
            if (ports != null && ports.Length > 0) { args.Add("-v"); args.Add(ports[0]); }
            if (!string.IsNullOrEmpty(state.DriverName)) { args.Add("-m"); args.Add(state.DriverName); }
            if (!string.IsNullOrEmpty(state.Comment)) { args.Add("-D"); args.Add(state.Comment); }
            if (!string.IsNullOrEmpty(state.Location)) { args.Add("-L"); args.Add(state.Location); }
            if ((state.Attributes & PrintQueueAttributes.Shared) != 0) { args.Add("-o"); args.Add("printer-is-shared=true"); }
            return Run("lpadmin", args.ToArray());
        }

        // a printer of another server: the IPP printer it is, added as a queue of this one
        public bool Connect(string path)
        {
            string name = path.TrimEnd('/').Split('/', '\\')[^1];
            string uri = path.Contains("://", StringComparison.Ordinal) ? path : "ipp://" + path.TrimStart('\\').Replace('\\', '/');
            return Run("lpadmin", "-p", name, "-E", "-v", uri, "-m", "everywhere");
        }

        public bool Disconnect(string path) => DeleteQueue(path.TrimEnd('/').Split('/', '\\')[^1]);

        public IReadOnlyList<JobState> Jobs(string queue)
        {
            var result = new List<JobState>();
            IntPtr jobs = IntPtr.Zero;
            int count = cupsGetJobs2(IntPtr.Zero, ref jobs, queue, 0, CUPS_WHICHJOBS_ACTIVE);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var j = Marshal.PtrToStructure<cups_job_t>(jobs + i * sizeof(cups_job_t));
                    var status = j.state switch
                    {
                        4 => PrintJobStatus.Paused,                    // held
                        5 => PrintJobStatus.Printing,
                        6 => PrintJobStatus.Error | PrintJobStatus.Blocked,   // stopped
                        7 => PrintJobStatus.Deleted,
                        8 => PrintJobStatus.Error,
                        9 => PrintJobStatus.Completed | PrintJobStatus.Printed,
                        _ => PrintJobStatus.Spooling,                  // pending
                    };
                    result.Add(new JobState
                    {
                        Id = j.id,
                        Name = Str(j.title),
                        Submitter = Str(j.user),
                        Status = status,
                        Priority = Math.Clamp(j.priority, 1, 99),
                        Position = i + 1,
                        Size = j.size * 1024,
                        Submitted = DateTimeOffset.FromUnixTimeSeconds(j.creation_time).UtcDateTime,
                        TimeSinceStartedPrinting = j.processing_time > 0 ? (int)Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - j.processing_time) * 1000) : 0,
                    });
                }
            }
            finally { if (jobs != IntPtr.Zero) cupsFreeJobs(count, jobs); }
            return result;
        }

        public void ControlJob(string queue, int id, JobControl control)
        {
            bool ok = control switch
            {
                JobControl.Cancel => cupsCancelJob2(IntPtr.Zero, queue, id, 0) == 1,
                JobControl.Pause => Request(IPP_OP_HOLD_JOB, queue, id, null, "/jobs/"),
                JobControl.Resume => Request(IPP_OP_RELEASE_JOB, queue, id, null, "/jobs/"),
                _ => Request(IPP_OP_RESTART_JOB, queue, id, null, "/jobs/"),
            };
            if (!ok) throw Spooler.Failed(control.ToString().ToLowerInvariant() + " job " + id + ": " + LastError());
        }

        public void RenameJob(string queue, int id, string name)
        {
            if (!Request(IPP_OP_SET_JOB_ATTRIBUTES, queue, id, name, "/jobs/"))
                throw Spooler.Failed("rename job " + id + ": " + LastError());
        }

        public JobWriter StartJob(string queue, string name, bool xps) => new CupsJobWriter(queue, name, xps);

        private sealed class CupsJobWriter : JobWriter
        {
            private readonly string _queue;
            private bool _open;

            public CupsJobWriter(string queue, string name, bool xps)
            {
                _queue = queue;
                int id = cupsCreateJob(IntPtr.Zero, queue, name ?? "Print job", 0, IntPtr.Zero);
                if (id == 0) throw Spooler.Failed("start a job on " + queue + ": " + LastError());
                Id = id;
                // an XPS document is XPS's own type; anything else the printer's raw stream
                string format = xps ? "application/vnd.ms-xpsdocument" : "application/vnd.cups-raw";
                if (cupsStartDocument(IntPtr.Zero, queue, id, name ?? "Print job", format, 1) != 100)   // HTTP_STATUS_CONTINUE
                {
                    cupsCancelJob2(IntPtr.Zero, queue, id, 0);
                    throw Spooler.Failed("start the document of job " + id + ": " + LastError());
                }
                _open = true;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                fixed (byte* p = buffer)
                {
                    if (cupsWriteRequestData(IntPtr.Zero, (IntPtr)(p + offset), (nuint)count) != 100)
                        throw Spooler.Failed("write job " + Id + ": " + LastError());
                }
            }

            public override void Commit()
            {
                if (!_open) return;
                _open = false;
                cupsFinishDocument(IntPtr.Zero, _queue);
            }

            public override void Abort()
            {
                if (!_open) return;
                _open = false;
                cupsFinishDocument(IntPtr.Zero, _queue);
                cupsCancelJob2(IntPtr.Zero, _queue, Id, 0);
            }
        }

        public string SpoolDirectory => "/var/spool/cups";

        public int MajorVersion => 2;
        public int MinorVersion => 0;

        // an IPP operation on a printer (or one of its jobs), as the CUPS tools send it
        private static bool Request(int op, string printer, int jobId, string jobName, string resource)
        {
            IntPtr request = ippNewRequest(op);
            ippAddString(request, IPP_TAG_OPERATION, IPP_TAG_URI, "printer-uri", IntPtr.Zero, "ipp://localhost/printers/" + Uri.EscapeDataString(printer));
            if (jobId > 0) ippAddInteger(request, IPP_TAG_OPERATION, IPP_TAG_INTEGER, "job-id", jobId);
            ippAddString(request, IPP_TAG_OPERATION, IPP_TAG_NAME, "requesting-user-name", IntPtr.Zero, Marshal.PtrToStringUTF8(cupsUser()) ?? Environment.UserName);
            if (jobName != null) ippAddString(request, IPP_TAG_JOB, IPP_TAG_NAME, "job-name", IntPtr.Zero, jobName);
            IntPtr response = cupsDoRequest(IntPtr.Zero, request, resource);   // takes the request
            if (response != IntPtr.Zero) ippDelete(response);
            return cupsLastError() < 0x0400;   // successful-ok and its variants
        }

        private static string LastError() => Marshal.PtrToStringUTF8(cupsLastErrorString()) ?? "unknown error";

        private static bool Run(string program, params string[] args)
        {
            try
            {
                var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (string a in args) start.ArgumentList.Add(a);
                using Process p = Process.Start(start);
                p.WaitForExit();
                return p.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }

        [DllImport(Lib)] private static extern int cupsGetDests(ref IntPtr dests);
        [DllImport(Lib)] private static extern void cupsFreeDests(int count, IntPtr dests);
        [DllImport(Lib)] private static extern int cupsGetJobs2(IntPtr http, ref IntPtr jobs, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int myjobs, int whichjobs);
        [DllImport(Lib)] private static extern void cupsFreeJobs(int count, IntPtr jobs);
        [DllImport(Lib)] private static extern int cupsCancelJob2(IntPtr http, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int job, int purge);
        [DllImport(Lib)] private static extern int cupsCreateJob(IntPtr http, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string title, int numOptions, IntPtr options);
        [DllImport(Lib)] private static extern int cupsStartDocument(IntPtr http, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int job, [MarshalAs(UnmanagedType.LPUTF8Str)] string docname, [MarshalAs(UnmanagedType.LPUTF8Str)] string format, int lastDocument);
        [DllImport(Lib)] private static extern int cupsWriteRequestData(IntPtr http, IntPtr buffer, nuint length);
        [DllImport(Lib)] private static extern int cupsFinishDocument(IntPtr http, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Lib)] private static extern IntPtr cupsUser();
        [DllImport(Lib)] private static extern int cupsLastError();
        [DllImport(Lib)] private static extern IntPtr cupsLastErrorString();
        [DllImport(Lib)] private static extern IntPtr ippNewRequest(int op);
        [DllImport(Lib)] private static extern IntPtr ippAddString(IntPtr ipp, int group, int valueTag, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr language, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Lib)] private static extern IntPtr ippAddInteger(IntPtr ipp, int group, int valueTag, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int value);
        [DllImport(Lib)] private static extern IntPtr cupsDoRequest(IntPtr http, IntPtr request, [MarshalAs(UnmanagedType.LPUTF8Str)] string resource);
        [DllImport(Lib)] private static extern void ippDelete(IntPtr ipp);
    }
}
