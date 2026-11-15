// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// System.Printing's queue model, as the C++/CLI System.Printing has it, over the platform's spooler (Spooler.cs):
// a print server and its queues; a queue's state, settings and jobs, paused, resumed and purged; a job's state,
// paused, resumed, restarted and cancelled; a job written to a queue (AddJob, PrintQueueStream); each object's
// PropertiesCollection, committed and refreshed. What a queue is and does is the spooler's -- winspool's on Windows,
// CUPS's on macOS and Linux -- and is read from it at construction and at Refresh, as the original reads it.
//

using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Printing.IndexedProperties;
using System.Threading;
using MS.Internal.Interop;

namespace System.Printing
{
    public abstract partial class PrintSystemObject : IDisposable
    {
        private readonly PrintPropertyDictionary _properties = new PrintPropertyDictionary();

        protected PrintSystemObject()
        {
        }

        protected PrintSystemObject(PrintSystemObjectLoadMode mode)
        {
        }

        ~PrintSystemObject()
        {
            Dispose(false);
        }

        public virtual string Name { get; internal set; } = string.Empty;

        /// <summary>The object this one belongs to: a queue's server, a job's queue.</summary>
        public virtual PrintSystemObject Parent => null;

        public PrintPropertyDictionary PropertiesCollection => _properties;

        protected bool IsDisposed { get; set; }

        protected static string[] BaseAttributeNames() => new[] { "Name" };

        protected void Initialize()
        {
        }

        /// <summary>Writes what was changed to the spooler.</summary>
        public abstract void Commit();

        /// <summary>Reads the object's state from the spooler again.</summary>
        public abstract void Refresh();

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool A_0)
        {
            if (IsDisposed)
            {
                return;
            }

            InternalDispose(A_0);
            IsDisposed = true;
        }

        protected virtual void InternalDispose(bool disposing)
        {
            if (disposing)
            {
                _properties.Dispose();
            }
        }

        // a property of the collection, set as the spooler gives it (initialised, not dirty)
        internal void Load(PrintProperty property, object value)
        {
            if (_properties.GetProperty(property.Name) is PrintProperty existing)
            {
                property = existing;
            }
            else
            {
                _properties.Add(property);
            }

            property.IsInternallyInitialized = true;
            property.Value = value;
            property.IsInternallyInitialized = false;
            property.IsDirty = false;
        }

        // a property the user changed through the collection
        internal bool Dirty(string name, out object value)
        {
            if (_properties.GetProperty(name) is PrintProperty p && p.IsDirty)
            {
                value = p.Value;
                p.IsDirty = false;
                return true;
            }

            value = null;
            return false;
        }
    }

    public abstract partial class PrintSystemObjects : IDisposable
    {
        protected PrintSystemObjects()
        {
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool A_0)
        {
        }
    }

    // ---- PrintQueue --------------------------------------------------------------------------------

    public partial class PrintQueue
    {
        private PrinterInfo _printer;
        private QueueState _state;
        private PrintServer _server;
        private readonly int _schemaVersion = 1;

        internal PrintQueue(PrinterInfo printer)
        {
            _printer = printer;
            _state = SpoolerQueueOf(printer);
            Name = printer.Name ?? string.Empty;
            LoadProperties();
        }

        /// <summary>The spooler's queue for a printer the platform's printing named: by its name, or (macOS names a
        /// printer by its description, CUPS by its queue name) by the queue's description.</summary>
        internal static QueueState SpoolerQueueOf(PrinterInfo printer)
        {
            string name = printer?.Name ?? string.Empty;
            QueueState byName = Spooler.Current.Queue(name);
            if (byName != null) return byName;
            foreach (QueueState q in Spooler.Current.Queues())
            {
                if (string.Equals(q.Comment, name, StringComparison.Ordinal) || string.Equals(q.Comment, printer?.DisplayName, StringComparison.Ordinal))
                    return q;
            }

            return null;
        }

        // the name the spooler knows the queue by (which a printer the platform named may not be)
        private string SpoolName => _state?.Name ?? Name;

        internal string SpoolerName => SpoolName;

        internal PrintQueue(PrintServer server, QueueState state, PrinterInfo printer)
        {
            _server = server;
            _state = state;
            _printer = printer;
            Name = printer?.Name ?? state.Name;
            LoadProperties();
        }

        internal string PlatformName { get; init; }

        public PrintQueue(PrintServer printServer, string printQueueName)
            : this(printServer, printQueueName, MaxPrintSchemaVersion, PrintSystemDesiredAccess.UsePrinter)
        {
        }

        public PrintQueue(PrintServer printServer, string printQueueName, int printSchemaVersion)
            : this(printServer, printQueueName, printSchemaVersion, PrintSystemDesiredAccess.UsePrinter)
        {
        }

        public PrintQueue(PrintServer printServer, string printQueueName, PrintSystemDesiredAccess desiredAccess)
            : this(printServer, printQueueName, MaxPrintSchemaVersion, desiredAccess)
        {
        }

        public PrintQueue(PrintServer printServer, string printQueueName, PrintQueueIndexedProperty[] propertyFilter)
            : this(printServer, printQueueName, MaxPrintSchemaVersion, PrintSystemDesiredAccess.UsePrinter)
        {
        }

        public PrintQueue(PrintServer printServer, string printQueueName, PrintQueueIndexedProperty[] propertyFilter, PrintSystemDesiredAccess desiredAccess)
            : this(printServer, printQueueName, MaxPrintSchemaVersion, desiredAccess)
        {
        }

        public PrintQueue(PrintServer printServer, string printQueueName, string[] propertyFilter)
            : this(printServer, printQueueName, MaxPrintSchemaVersion, PrintSystemDesiredAccess.UsePrinter)
        {
        }

        public PrintQueue(PrintServer printServer, string printQueueName, string[] propertyFilter, PrintSystemDesiredAccess desiredAccess)
            : this(printServer, printQueueName, MaxPrintSchemaVersion, desiredAccess)
        {
        }

        public PrintQueue(PrintServer printServer, string printQueueName, int printSchemaVersion, PrintSystemDesiredAccess desiredAccess)
        {
            ArgumentNullException.ThrowIfNull(printServer);
            ArgumentNullException.ThrowIfNull(printQueueName);
            if (printSchemaVersion < 1 || printSchemaVersion > MaxPrintSchemaVersion)
            {
                throw new ArgumentOutOfRangeException(nameof(printSchemaVersion));
            }

            _server = printServer;
            _schemaVersion = printSchemaVersion;
            _state = Spooler.Current.Queue(printQueueName);
            foreach (PrinterInfo printer in PlatformPrint.EnumeratePrinters())
            {
                if (string.Equals(printer.Name, printQueueName, StringComparison.OrdinalIgnoreCase)) _printer = printer;
            }

            if (_state == null && _printer == null)
            {
                throw new PrintQueueException(unchecked((int)0x80070709), "PrintSystemException.PrintQueue.Populate", printQueueName);
            }

            Name = _state?.Name ?? _printer.Name;
            LoadProperties();
        }

        // a printer the spooler has no queue for (a head with no spooler, a printer it names otherwise): what the
        // platform's printing says of it
        private QueueState State => _state ??= new QueueState
        {
            Name = Name,
            Comment = _printer?.Location ?? string.Empty,
            Location = _printer?.Location ?? string.Empty,
            IsDefault = _printer?.IsDefault ?? false,
        };

        private void LoadProperties()
        {
            QueueState s = State;
            Load(new PrintStringProperty("Name"), Name);
            Load(new PrintStringProperty("ShareName"), s.ShareName);
            Load(new PrintStringProperty("Comment"), s.Comment);
            Load(new PrintStringProperty("Location"), s.Location);
            Load(new PrintStringProperty("Description"), s.Description);
            Load(new PrintInt32Property("Priority"), s.Priority);
            Load(new PrintInt32Property("DefaultPriority"), s.DefaultPriority);
            Load(new PrintInt32Property("StartTimeOfDay"), s.StartTimeOfDay);
            Load(new PrintInt32Property("UntilTimeOfDay"), s.UntilTimeOfDay);
            Load(new PrintInt32Property("AveragePagesPerMinute"), s.AveragePagesPerMinute);
            Load(new PrintInt32Property("NumberOfJobs"), s.NumberOfJobs);
            Load(new PrintQueueAttributeProperty("QueueAttributes"), s.Attributes);
            Load(new PrintQueueStatusProperty("QueueStatus"), s.Status);
            Load(new PrintStringProperty("SeparatorFile"), s.SeparatorFile);
            Load(new PrintDriverProperty("QueueDriver"), QueueDriver);
            Load(new PrintPortProperty("QueuePort"), QueuePort);
            Load(new PrintProcessorProperty("QueuePrintProcessor"), QueuePrintProcessor);
            Load(new PrintServerProperty("HostingPrintServer"), HostingPrintServer);
        }

        public sealed override string Name { get => base.Name; internal set => base.Name = value ?? string.Empty; }

        public override PrintSystemObject Parent => HostingPrintServer;

        public virtual PrintServer HostingPrintServer
        {
            get => _server ??= new LocalPrintServer();
            protected set => _server = value;
        }

        public int ClientPrintSchemaVersion => _schemaVersion;

        public virtual string ShareName { get => State.ShareName; set => State.ShareName = value ?? string.Empty; }

        public virtual string Comment { get => State.Comment; set => State.Comment = value ?? string.Empty; }

        public virtual string Location { get => State.Location; set => State.Location = value ?? string.Empty; }

        public virtual string Description => State.Description;

        public virtual int Priority { get => State.Priority; set => State.Priority = value; }

        public virtual int DefaultPriority { get => State.DefaultPriority; set => State.DefaultPriority = value; }

        public virtual int StartTimeOfDay { get => State.StartTimeOfDay; set => State.StartTimeOfDay = value; }

        public virtual int UntilTimeOfDay { get => State.UntilTimeOfDay; set => State.UntilTimeOfDay = value; }

        public virtual int AveragePagesPerMinute => State.AveragePagesPerMinute;

        public virtual int NumberOfJobs => State.NumberOfJobs;

        public virtual string SeparatorFile { get => State.SeparatorFile; set => State.SeparatorFile = value ?? string.Empty; }

        public PrintQueueAttributes QueueAttributes => State.Attributes;

        public PrintQueueStatus QueueStatus => State.Status;

        public virtual PrintDriver QueueDriver
        {
            get => new PrintDriver(State.DriverName);
            set => State.DriverName = value?.Name ?? string.Empty;
        }

        public virtual PrintPort QueuePort
        {
            get => new PrintPort(State.PortName);
            set => State.PortName = value?.Name ?? string.Empty;
        }

        public virtual PrintProcessor QueuePrintProcessor
        {
            get => new PrintProcessor(State.ProcessorName);
            set => State.ProcessorName = value?.Name ?? string.Empty;
        }

        /// <summary>Set to stop what is being written to the queue (the XPS writer checks it).</summary>
        public bool PrintingIsCancelled { get; set; }

        private bool Status(PrintQueueStatus flag) => (State.Status & flag) != 0;

        private bool Attribute(PrintQueueAttributes flag) => (State.Attributes & flag) != 0;

        public bool HasPaperProblem => Status(PrintQueueStatus.PaperProblem);
        public bool HasToner => !Status(PrintQueueStatus.NoToner);
        public bool IsBusy => Status(PrintQueueStatus.Busy);
        public bool IsDoorOpened => Status(PrintQueueStatus.DoorOpen);
        public bool IsInError => Status(PrintQueueStatus.Error);
        public bool IsInitializing => Status(PrintQueueStatus.Initializing);
        public bool IsIOActive => Status(PrintQueueStatus.IOActive);
        public bool IsManualFeedRequired => Status(PrintQueueStatus.ManualFeed);
        public bool IsNotAvailable => Status(PrintQueueStatus.NotAvailable);
        public bool IsOffline => Status(PrintQueueStatus.Offline);
        public bool IsOutOfMemory => Status(PrintQueueStatus.OutOfMemory);
        public bool IsOutOfPaper => Status(PrintQueueStatus.PaperOut);
        public bool IsOutputBinFull => Status(PrintQueueStatus.OutputBinFull);
        public bool IsPaperJammed => Status(PrintQueueStatus.PaperJam);
        public bool IsPaused => Status(PrintQueueStatus.Paused);
        public bool IsPendingDeletion => Status(PrintQueueStatus.PendingDeletion);
        public bool IsPowerSaveOn => Status(PrintQueueStatus.PowerSave);
        public bool IsPrinting => Status(PrintQueueStatus.Printing);
        public bool IsProcessing => Status(PrintQueueStatus.Processing);
        public bool IsServerUnknown => Status(PrintQueueStatus.ServerUnknown);
        public bool IsTonerLow => Status(PrintQueueStatus.TonerLow);
        public bool IsWaiting => Status(PrintQueueStatus.Waiting);
        public bool IsWarmingUp => Status(PrintQueueStatus.WarmingUp);
        public bool NeedUserIntervention => Status(PrintQueueStatus.UserIntervention);
        public bool PagePunt => Status(PrintQueueStatus.PagePunt);

        public bool IsBidiEnabled => Attribute(PrintQueueAttributes.EnableBidi);
        public bool IsDevQueryEnabled => Attribute(PrintQueueAttributes.EnableDevQuery);
        public bool IsDirect => Attribute(PrintQueueAttributes.Direct);
        public bool IsHidden => Attribute(PrintQueueAttributes.Hidden);
        public bool IsPublished => Attribute(PrintQueueAttributes.Published);
        public bool IsQueued => Attribute(PrintQueueAttributes.Queued);
        public bool IsRawOnlyEnabled => Attribute(PrintQueueAttributes.RawOnly);
        public bool IsShared => Attribute(PrintQueueAttributes.Shared);
        public bool KeepPrintedJobs => Attribute(PrintQueueAttributes.KeepPrintedJobs);
        public bool ScheduleCompletedJobsFirst => Attribute(PrintQueueAttributes.ScheduleCompletedJobsFirst);

        /// <summary>Whether the printer takes XPS itself (an XPSDrv driver).</summary>
        public bool IsXpsDevice => State.DriverName.Contains("XPS", StringComparison.OrdinalIgnoreCase);

        public virtual void Pause()
        {
            Spooler.Current.ControlQueue(SpoolName, QueueControl.Pause);
            Refresh();
        }

        public virtual void Resume()
        {
            Spooler.Current.ControlQueue(SpoolName, QueueControl.Resume);
            Refresh();
        }

        /// <summary>Removes every job from the queue.</summary>
        public virtual void Purge()
        {
            Spooler.Current.ControlQueue(SpoolName, QueueControl.Purge);
            Refresh();
        }

        public override void Commit()
        {
            QueueState s = State;
            if (Dirty("ShareName", out object v)) s.ShareName = (string)v ?? string.Empty;
            if (Dirty("Comment", out v)) s.Comment = (string)v ?? string.Empty;
            if (Dirty("Location", out v)) s.Location = (string)v ?? string.Empty;
            if (Dirty("SeparatorFile", out v)) s.SeparatorFile = (string)v ?? string.Empty;
            if (Dirty("Priority", out v)) s.Priority = (int)v;
            if (Dirty("DefaultPriority", out v)) s.DefaultPriority = (int)v;
            if (Dirty("StartTimeOfDay", out v)) s.StartTimeOfDay = (int)v;
            if (Dirty("UntilTimeOfDay", out v)) s.UntilTimeOfDay = (int)v;
            try
            {
                Spooler.Current.SetQueue(s);
            }
            catch (PrintSystemException e)
            {
                throw new PrintCommitAttributesException(unchecked((int)0x80004005), "PrintSystemException.PrintQueue.Commit", null, null, Name);
            }

            Refresh();
        }

        public override void Refresh()
        {
            QueueState fresh = Spooler.Current.Queue(SpoolName);
            if (fresh != null)
            {
                _state = fresh;
            }

            LoadProperties();
        }

        protected sealed override void InternalDispose(bool disposing) => base.InternalDispose(disposing);

        // ---- jobs ----

        public PrintJobInfoCollection GetPrintJobInfoCollection() => new PrintJobInfoCollection(this, null);

        public PrintSystemJobInfo GetJob(int jobId)
        {
            foreach (JobState job in Spooler.Current.Jobs(SpoolName))
            {
                if (job.Id == jobId) return new PrintSystemJobInfo(this, job, null);
            }

            throw new PrintJobException(unchecked((int)0x80070057), "PrintSystemException.PrintSystemJobInfo.Generic", Name, null, jobId);
        }

        public PrintSystemJobInfo AddJob() => AddJob(null, (PrintTicket)null);

        public PrintSystemJobInfo AddJob(string jobName) => AddJob(jobName, (PrintTicket)null);

        /// <summary>A job to write to: its JobStream takes the document, which is printed when the stream is
        /// closed.</summary>
        public PrintSystemJobInfo AddJob(string jobName, PrintTicket printTicket)
        {
            var stream = new PrintQueueStream(this, jobName ?? "Print System Document", true, printTicket);
            var state = new JobState { Id = stream.JobIdentifier, Name = jobName ?? "Print System Document", Submitter = Environment.UserName, Status = PrintJobStatus.Spooling, Submitted = DateTime.UtcNow };
            return new PrintSystemJobInfo(this, state, stream);
        }

        public PrintSystemJobInfo AddJob(string jobName, string documentPath, bool fastCopy) => AddJob(jobName, documentPath, fastCopy, null);

        /// <summary>Prints the file at <paramref name="documentPath"/> as a job.</summary>
        public PrintSystemJobInfo AddJob(string jobName, string documentPath, bool fastCopy, PrintTicket printTicket)
        {
            ArgumentNullException.ThrowIfNull(documentPath);
            PrintSystemJobInfo job = AddJob(jobName, printTicket);
            using (Stream source = File.OpenRead(documentPath))
            using (Stream target = job.JobStream)
            {
                source.CopyTo(target);
            }

            return job;
        }

        public MemoryStream GetPrintCapabilitiesAsXml() => GetPrintCapabilitiesAsXml(null);

        /// <summary>The printer's capabilities as Print Schema: its driver's on Windows, the bare document elsewhere
        /// (what a print system without Print Schema can say).</summary>
        public MemoryStream GetPrintCapabilitiesAsXml(PrintTicket printTicket)
        {
            if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(Name))
            {
                try
                {
                    using MS.Internal.Printing.Configuration.PTProviderBase provider =
                        MS.Internal.Printing.Configuration.PTProviderBase.Create(Name, MaxPrintSchemaVersion, _schemaVersion);
                    using MemoryStream ticket = printTicket?.GetXmlStream();
                    return provider.GetPrintCapabilities(ticket);
                }
                catch (PrintSystemException)
                {
                }
            }

            var xml = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><psf:PrintCapabilities xmlns:psf=\"http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework\" " +
                "xmlns:psk=\"http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords\" version=\"1\"/>"));
            return xml;
        }
    }

    public partial class PrintQueueCollection : PrintSystemObjects, IEnumerable<PrintQueue>, IEnumerable, IDisposable
    {
        private static readonly object s_syncRoot = new object();
        private readonly List<PrintQueue> _queues;

        public PrintQueueCollection()
        {
            _queues = new List<PrintQueue>();
        }

        internal PrintQueueCollection(List<PrintQueue> queues)
        {
            _queues = queues ?? new List<PrintQueue>();
        }

        public PrintQueueCollection(PrintServer printServer, string[] propertyFilter)
            : this(printServer, propertyFilter, null)
        {
        }

        public PrintQueueCollection(PrintServer printServer, string[] propertyFilter, EnumeratedPrintQueueTypes[] enumerationFlag)
        {
            ArgumentNullException.ThrowIfNull(printServer);
            _queues = PrintServer.Discover(printServer, enumerationFlag);
        }

        public static object SyncRoot => s_syncRoot;

        public void Add(PrintQueue printObject) => _queues.Add(printObject);

        public virtual IEnumerator<PrintQueue> GetEnumerator() => _queues.GetEnumerator();

        public virtual IEnumerator GetNonGenericEnumerator() => _queues.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetNonGenericEnumerator();

        protected override void Dispose(bool A_0)
        {
            if (A_0)
            {
                foreach (PrintQueue queue in _queues) queue.Dispose();
                _queues.Clear();
            }

            base.Dispose(A_0);
        }
    }

    // ---- PrintServer -------------------------------------------------------------------------------

    public partial class PrintServer : PrintSystemObject
    {
        private bool _beepEnabled, _netPopup, _restartJobOnPoolEnabled = true;
        private int _restartJobOnPoolTimeout = 600;
        private string _spoolDirectory;
        private PrintServerEventLoggingTypes _eventLog = PrintServerEventLoggingTypes.LogPrintingErrorEvents;
        private ThreadPriority _portThreadPriority = ThreadPriority.Normal, _schedulerPriority = ThreadPriority.Normal;

        public PrintServer()
            : this(null, PrintSystemDesiredAccess.EnumerateServer)
        {
        }

        public PrintServer(PrintSystemDesiredAccess desiredAccess)
            : this(null, desiredAccess)
        {
        }

        public PrintServer(string path)
            : this(path, PrintSystemDesiredAccess.EnumerateServer)
        {
        }

        public PrintServer(string path, PrintServerIndexedProperty[] propertiesFilter)
            : this(path, PrintSystemDesiredAccess.EnumerateServer)
        {
        }

        public PrintServer(string path, PrintServerIndexedProperty[] propertiesFilter, PrintSystemDesiredAccess desiredAccess)
            : this(path, desiredAccess)
        {
        }

        public PrintServer(string path, string[] propertiesFilter)
            : this(path, PrintSystemDesiredAccess.EnumerateServer)
        {
        }

        public PrintServer(string path, string[] propertiesFilter, PrintSystemDesiredAccess desiredAccess)
            : this(path, desiredAccess)
        {
        }

        public PrintServer(string path, PrintSystemDesiredAccess desiredAccess)
        {
            base.Name = path;
            LoadProperties();
        }

        private void LoadProperties()
        {
            Load(new PrintStringProperty("DefaultSpoolDirectory"), DefaultSpoolDirectory);
            Load(new PrintThreadPriorityProperty("PortThreadPriority"), _portThreadPriority);
            Load(new PrintThreadPriorityProperty("DefaultPortThreadPriority"), DefaultPortThreadPriority);
            Load(new PrintThreadPriorityProperty("SchedulerPriority"), _schedulerPriority);
            Load(new PrintThreadPriorityProperty("DefaultSchedulerPriority"), DefaultSchedulerPriority);
            Load(new PrintBooleanProperty("BeepEnabled"), _beepEnabled);
            Load(new PrintBooleanProperty("NetPopup"), _netPopup);
            Load(new PrintServerLoggingProperty("EventLog"), _eventLog);
            Load(new PrintInt32Property("MajorVersion"), MajorVersion);
            Load(new PrintInt32Property("MinorVersion"), MinorVersion);
            Load(new PrintInt32Property("RestartJobOnPoolTimeout"), _restartJobOnPoolTimeout);
            Load(new PrintBooleanProperty("RestartJobOnPoolEnabled"), _restartJobOnPoolEnabled);
        }

        /// <summary>The server's name: the path it was given; null for the local one.</summary>
        public sealed override string Name => base.Name;

        protected bool IsDelayInitialized { get; set; }

        public bool BeepEnabled { get => _beepEnabled; set => _beepEnabled = value; }

        public bool NetPopup { get => _netPopup; set => _netPopup = value; }

        public PrintServerEventLoggingTypes EventLog { get => _eventLog; set => _eventLog = value; }

        public ThreadPriority PortThreadPriority { get => _portThreadPriority; set => _portThreadPriority = value; }

        public ThreadPriority SchedulerPriority { get => _schedulerPriority; set => _schedulerPriority = value; }

        public ThreadPriority DefaultPortThreadPriority => ThreadPriority.Normal;

        public ThreadPriority DefaultSchedulerPriority => ThreadPriority.Normal;

        public string DefaultSpoolDirectory
        {
            get => _spoolDirectory ?? Spooler.Current.SpoolDirectory;
            set => _spoolDirectory = value;
        }

        public bool RestartJobOnPoolEnabled { get => _restartJobOnPoolEnabled; set => _restartJobOnPoolEnabled = value; }

        public int RestartJobOnPoolTimeout { get => _restartJobOnPoolTimeout; set => _restartJobOnPoolTimeout = value; }

        public int MajorVersion => Spooler.Current.MajorVersion;

        public int MinorVersion => Spooler.Current.MinorVersion;

        public byte SubSystemVersion => 1;

        public override void Commit()
        {
            if (Dirty("BeepEnabled", out object v)) _beepEnabled = (bool)v;
            if (Dirty("NetPopup", out v)) _netPopup = (bool)v;
            if (Dirty("EventLog", out v)) _eventLog = (PrintServerEventLoggingTypes)v;
            if (Dirty("PortThreadPriority", out v)) _portThreadPriority = (ThreadPriority)v;
            if (Dirty("SchedulerPriority", out v)) _schedulerPriority = (ThreadPriority)v;
            if (Dirty("DefaultSpoolDirectory", out v)) _spoolDirectory = (string)v;
            if (Dirty("RestartJobOnPoolTimeout", out v)) _restartJobOnPoolTimeout = (int)v;
            if (Dirty("RestartJobOnPoolEnabled", out v)) _restartJobOnPoolEnabled = (bool)v;
        }

        public override void Refresh() => LoadProperties();

        protected sealed override void InternalDispose(bool disposing) => base.InternalDispose(disposing);

        public PrintQueue GetPrintQueue(string printQueueName) => new PrintQueue(this, printQueueName);

        public PrintQueue GetPrintQueue(string printQueueName, string[] propertiesFilter) => new PrintQueue(this, printQueueName, propertiesFilter);

        public PrintQueueCollection GetPrintQueues() => new PrintQueueCollection(Discover(this, null));

        public PrintQueueCollection GetPrintQueues(EnumeratedPrintQueueTypes[] enumerationFlag) => new PrintQueueCollection(Discover(this, enumerationFlag));

        public PrintQueueCollection GetPrintQueues(PrintQueueIndexedProperty[] propertiesFilter) => GetPrintQueues();

        public PrintQueueCollection GetPrintQueues(PrintQueueIndexedProperty[] propertiesFilter, EnumeratedPrintQueueTypes[] enumerationFlag) => GetPrintQueues(enumerationFlag);

        public PrintQueueCollection GetPrintQueues(string[] propertiesFilter) => GetPrintQueues();

        public PrintQueueCollection GetPrintQueues(string[] propertiesFilter, EnumeratedPrintQueueTypes[] enumerationFlag) => GetPrintQueues(enumerationFlag);

        public static bool DeletePrintQueue(string printQueueName) => Spooler.Current.DeleteQueue(printQueueName);

        public static bool DeletePrintQueue(PrintQueue printQueue)
        {
            ArgumentNullException.ThrowIfNull(printQueue);
            return DeletePrintQueue(printQueue.SpoolerName);
        }

        public PrintQueue InstallPrintQueue(string printQueueName, string driverName, string[] portNames, string printProcessorName, PrintQueueAttributes printQueueAttributes) =>
            Install(new QueueState { Name = printQueueName, DriverName = driverName, ProcessorName = printProcessorName, Attributes = printQueueAttributes }, portNames);

        public PrintQueue InstallPrintQueue(string printQueueName, string driverName, string[] portNames, string printProcessorName, PrintQueueAttributes printQueueAttributes,
            PrintQueueStringProperty printQueueProperty, int printQueuePriority, int printQueueDefaultPriority)
        {
            var state = new QueueState { Name = printQueueName, DriverName = driverName, ProcessorName = printProcessorName, Attributes = printQueueAttributes, Priority = printQueuePriority, DefaultPriority = printQueueDefaultPriority };
            if (printQueueProperty != null)
            {
                switch (printQueueProperty.Type)
                {
                    case PrintQueueStringPropertyType.Comment: state.Comment = printQueueProperty.Name; break;
                    case PrintQueueStringPropertyType.Location: state.Location = printQueueProperty.Name; break;
                    case PrintQueueStringPropertyType.ShareName: state.ShareName = printQueueProperty.Name; break;
                }
            }

            return Install(state, portNames);
        }

        public PrintQueue InstallPrintQueue(string printQueueName, string driverName, string[] portNames, string printProcessorName, PrintQueueAttributes printQueueAttributes,
            string printQueueShareName, string printQueueComment, string printQueueLocation, string printQueueSeparatorFile, int printQueuePriority, int printQueueDefaultPriority) =>
            Install(new QueueState
            {
                Name = printQueueName, DriverName = driverName, ProcessorName = printProcessorName, Attributes = printQueueAttributes,
                ShareName = printQueueShareName ?? string.Empty, Comment = printQueueComment ?? string.Empty, Location = printQueueLocation ?? string.Empty,
                SeparatorFile = printQueueSeparatorFile ?? string.Empty, Priority = printQueuePriority, DefaultPriority = printQueueDefaultPriority,
            }, portNames);

        public PrintQueue InstallPrintQueue(string printQueueName, string driverName, string[] portNames, string printProcessorName, PrintPropertyDictionary initialParameters)
        {
            var state = new QueueState { Name = printQueueName, DriverName = driverName, ProcessorName = printProcessorName };
            if (initialParameters != null)
            {
                if (initialParameters.GetProperty("ShareName")?.Value is string share) state.ShareName = share;
                if (initialParameters.GetProperty("Comment")?.Value is string comment) state.Comment = comment;
                if (initialParameters.GetProperty("Location")?.Value is string location) state.Location = location;
                if (initialParameters.GetProperty("SeparatorFile")?.Value is string separator) state.SeparatorFile = separator;
                if (initialParameters.GetProperty("Priority")?.Value is int priority) state.Priority = priority;
                if (initialParameters.GetProperty("DefaultPriority")?.Value is int defaultPriority) state.DefaultPriority = defaultPriority;
                if (initialParameters.GetProperty("QueueAttributes")?.Value is PrintQueueAttributes attributes) state.Attributes = attributes;
            }

            return Install(state, portNames);
        }

        private PrintQueue Install(QueueState state, string[] portNames)
        {
            ArgumentNullException.ThrowIfNull(state.Name);
            if (!Spooler.Current.InstallQueue(state, portNames))
            {
                throw new PrintServerException(unchecked((int)0x80004005), "PrintSystemException.PrintServer.InstallPrintQueue", Name ?? Environment.MachineName);
            }

            return new PrintQueue(this, state.Name);
        }

        internal static List<PrintQueue> Discover() => Discover(null, null);

        // the printers the platform's printing has (what prints, on every head), each with the spooler's state of it
        // where the spooler has the queue; filtered as EnumPrinters filters them (local, connections, shared...)
        internal static List<PrintQueue> Discover(PrintServer server, EnumeratedPrintQueueTypes[] flags)
        {
            var queues = new List<PrintQueue>();
            foreach (PrinterInfo printer in PlatformPrint.EnumeratePrinters())
            {
                QueueState state = PrintQueue.SpoolerQueueOf(printer);
                if (state != null && !Matches(state, flags)) continue;
                if (state == null && flags != null && flags.Length != 0) continue;
                queues.Add(state != null ? new PrintQueue(server, state, printer) { PlatformName = printer.Name } : new PrintQueue(printer));
            }

            return queues;
        }

        private static bool Matches(QueueState state, EnumeratedPrintQueueTypes[] flags)
        {
            if (flags == null || flags.Length == 0) return true;
            foreach (EnumeratedPrintQueueTypes flag in flags)
            {
                bool match = flag switch
                {
                    EnumeratedPrintQueueTypes.Shared => (state.Attributes & PrintQueueAttributes.Shared) != 0,
                    EnumeratedPrintQueueTypes.Queued => (state.Attributes & PrintQueueAttributes.Queued) != 0,
                    EnumeratedPrintQueueTypes.DirectPrinting => (state.Attributes & PrintQueueAttributes.Direct) != 0,
                    EnumeratedPrintQueueTypes.KeepPrintedJobs => (state.Attributes & PrintQueueAttributes.KeepPrintedJobs) != 0,
                    EnumeratedPrintQueueTypes.EnableBidi => (state.Attributes & PrintQueueAttributes.EnableBidi) != 0,
                    EnumeratedPrintQueueTypes.EnableDevQuery => (state.Attributes & PrintQueueAttributes.EnableDevQuery) != 0,
                    EnumeratedPrintQueueTypes.RawOnly => (state.Attributes & PrintQueueAttributes.RawOnly) != 0,
                    EnumeratedPrintQueueTypes.PublishedInDirectoryServices => (state.Attributes & PrintQueueAttributes.Published) != 0,
                    EnumeratedPrintQueueTypes.Connections => state.Name.StartsWith(@"\\", StringComparison.Ordinal) || state.PortName.Contains("://", StringComparison.Ordinal) && !state.PortName.StartsWith("usb", StringComparison.Ordinal),
                    EnumeratedPrintQueueTypes.Local => !state.Name.StartsWith(@"\\", StringComparison.Ordinal),
                    EnumeratedPrintQueueTypes.Fax => state.DriverName.Contains("Fax", StringComparison.OrdinalIgnoreCase),
                    EnumeratedPrintQueueTypes.WorkOffline => (state.Status & PrintQueueStatus.Offline) != 0,
                    _ => false,
                };
                if (!match) return false;
            }

            return true;
        }
    }

    public sealed partial class LocalPrintServer : PrintServer
    {
        public LocalPrintServer()
            : base((string)null)
        {
        }

        public LocalPrintServer(string path)
            : base(path)
        {
        }

        public LocalPrintServer(PrintSystemDesiredAccess desiredAccess)
            : base((string)null, desiredAccess)
        {
        }

        public LocalPrintServer(LocalPrintServerIndexedProperty[] propertiesFilter)
            : base((string)null)
        {
        }

        public LocalPrintServer(LocalPrintServerIndexedProperty[] propertiesFilter, PrintSystemDesiredAccess desiredAccess)
            : base((string)null, desiredAccess)
        {
        }

        public LocalPrintServer(string[] propertiesFilter)
            : base((string)null)
        {
        }

        public LocalPrintServer(string[] propertiesFilter, PrintSystemDesiredAccess desiredAccess)
            : base((string)null, desiredAccess)
        {
        }

        /// <summary>The machine's (or user's) default printer; set, it becomes the default.</summary>
        public PrintQueue DefaultPrintQueue
        {
            get
            {
                List<PrintQueue> queues = Discover(this, null);
                foreach (PrintQueue queue in queues)
                {
                    if (queue.IsDefault) return queue;
                }

                return queues.Count != 0 ? queues[0] : null;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                if (!Spooler.Current.SetDefault(value.SpoolerName))
                {
                    throw new PrintServerException(unchecked((int)0x80004005), "PrintSystemException.PrintServer.SetDefaultPrinter", value.Name);
                }
            }
        }

        public static PrintQueue GetDefaultPrintQueue() => new LocalPrintServer().DefaultPrintQueue;

        public bool ConnectToPrintQueue(string printQueuePath) => Spooler.Current.Connect(printQueuePath ?? throw new ArgumentNullException(nameof(printQueuePath)));

        public bool ConnectToPrintQueue(PrintQueue printer) => ConnectToPrintQueue((printer ?? throw new ArgumentNullException(nameof(printer))).FullName);

        public bool DisconnectFromPrintQueue(string printQueuePath) => Spooler.Current.Disconnect(printQueuePath ?? throw new ArgumentNullException(nameof(printQueuePath)));

        public bool DisconnectFromPrintQueue(PrintQueue printer) => DisconnectFromPrintQueue((printer ?? throw new ArgumentNullException(nameof(printer))).FullName);

        public sealed override void Commit() => base.Commit();

        public sealed override void Refresh() => base.Refresh();
    }

    // ---- jobs --------------------------------------------------------------------------------------

    public partial class PrintSystemJobInfo : PrintSystemObject
    {
        private readonly PrintQueue _queue;
        private JobState _state;
        private readonly PrintQueueStream _stream;

        internal PrintSystemJobInfo(PrintQueue queue, JobState state, PrintQueueStream stream)
        {
            _queue = queue;
            _state = state;
            _stream = stream;
            base.Name = state.Name;
            LoadProperties();
        }

        private void LoadProperties()
        {
            Load(new PrintStringProperty("Name"), _state.Name);
            Load(new PrintInt32Property("JobIdentifier"), _state.Id);
            Load(new PrintStringProperty("Submitter"), _state.Submitter);
            Load(new PrintJobStatusProperty("JobStatus"), _state.Status);
            Load(new PrintJobPriorityProperty("Priority"), (PrintJobPriority)Math.Clamp(_state.Priority, 0, 99));
            Load(new PrintInt32Property("PositionInPrintQueue"), _state.Position);
            Load(new PrintInt32Property("JobSize"), _state.Size);
            Load(new PrintInt32Property("NumberOfPages"), _state.Pages);
            Load(new PrintInt32Property("NumberOfPagesPrinted"), _state.PagesPrinted);
            Load(new PrintDateTimeProperty("TimeJobSubmitted"), _state.Submitted);
            Load(new PrintQueueProperty("HostingPrintQueue"), _queue);
        }

        public static PrintSystemJobInfo Get(PrintQueue printQueue, int jobIdentifier)
        {
            ArgumentNullException.ThrowIfNull(printQueue);
            return printQueue.GetJob(jobIdentifier);
        }

        public override PrintSystemObject Parent => _queue;

        public PrintQueue HostingPrintQueue => _queue;

        public PrintServer HostingPrintServer => _queue.HostingPrintServer;

        public int JobIdentifier => _state.Id;

        public string JobName
        {
            get => _state.Name;
            set => _state.Name = value ?? string.Empty;
        }

        public string Submitter => _state.Submitter;

        public PrintJobStatus JobStatus => _state.Status;

        public PrintJobPriority Priority => (PrintJobPriority)Math.Clamp(_state.Priority, 0, 99);

        public int PositionInPrintQueue => _state.Position;

        public int JobSize => _state.Size;

        public int NumberOfPages => _state.Pages;

        public int NumberOfPagesPrinted => _state.PagesPrinted;

        public int StartTimeOfDay => _state.StartTimeOfDay;

        public int UntilTimeOfDay => _state.UntilTimeOfDay;

        public int TimeSinceStartedPrinting => _state.TimeSinceStartedPrinting;

        public DateTime TimeJobSubmitted => _state.Submitted;

        /// <summary>The job's document, written to the queue: a job from AddJob has one, a job read from the queue none.</summary>
        public Stream JobStream => _stream;

        private bool Status(PrintJobStatus flag) => (_state.Status & flag) != 0;

        public bool IsBlocked => Status(PrintJobStatus.Blocked);
        public bool IsCompleted => Status(PrintJobStatus.Completed);
        public bool IsDeleted => Status(PrintJobStatus.Deleted);
        public bool IsDeleting => Status(PrintJobStatus.Deleting);
        public bool IsInError => Status(PrintJobStatus.Error);
        public bool IsOffline => Status(PrintJobStatus.Offline);
        public bool IsPaperOut => Status(PrintJobStatus.PaperOut);
        public bool IsPaused => Status(PrintJobStatus.Paused);
        public bool IsPrinted => Status(PrintJobStatus.Printed);
        public bool IsPrinting => Status(PrintJobStatus.Printing);
        public bool IsRestarted => Status(PrintJobStatus.Restarted);
        public bool IsRetained => Status(PrintJobStatus.Retained);
        public bool IsSpooling => Status(PrintJobStatus.Spooling);
        public bool IsUserInterventionRequired => Status(PrintJobStatus.UserIntervention);

        public void Cancel()
        {
            Spooler.Current.ControlJob(_queue.SpoolerName, _state.Id, JobControl.Cancel);
            Refresh();
        }

        public void Pause()
        {
            Spooler.Current.ControlJob(_queue.SpoolerName, _state.Id, JobControl.Pause);
            Refresh();
        }

        public void Resume()
        {
            Spooler.Current.ControlJob(_queue.SpoolerName, _state.Id, JobControl.Resume);
            Refresh();
        }

        public void Restart()
        {
            Spooler.Current.ControlJob(_queue.SpoolerName, _state.Id, JobControl.Restart);
            Refresh();
        }

        /// <summary>Writes a changed JobName to the spooler.</summary>
        public override void Commit()
        {
            if (Dirty("Name", out object v)) _state.Name = (string)v ?? string.Empty;
            Spooler.Current.RenameJob(_queue.SpoolerName, _state.Id, _state.Name);
            base.Name = _state.Name;
        }

        public override void Refresh()
        {
            foreach (JobState job in Spooler.Current.Jobs(_queue.SpoolerName))
            {
                if (job.Id == _state.Id)
                {
                    _state = job;
                    LoadProperties();
                    return;
                }
            }

            // no longer queued: printed, or gone
            _state.Status |= PrintJobStatus.Deleted;
            LoadProperties();
        }

        protected sealed override void InternalDispose(bool disposing)
        {
            if (disposing) _stream?.Dispose();
            base.InternalDispose(disposing);
        }
    }

    public partial class PrintJobInfoCollection : PrintSystemObjects, IEnumerable<PrintSystemJobInfo>, IEnumerable
    {
        private readonly List<PrintSystemJobInfo> _jobs = new List<PrintSystemJobInfo>();

        public PrintJobInfoCollection(PrintQueue printQueue, string[] propertyFilter)
        {
            ArgumentNullException.ThrowIfNull(printQueue);
            foreach (JobState job in Spooler.Current.Jobs(printQueue.SpoolerName))
            {
                _jobs.Add(new PrintSystemJobInfo(printQueue, job, null));
            }
        }

        public void Add(PrintSystemJobInfo printObject) => _jobs.Add(printObject);

        public virtual IEnumerator<PrintSystemJobInfo> GetEnumerator() => _jobs.GetEnumerator();

        public virtual IEnumerator GetNonGenericEnumerator() => _jobs.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetNonGenericEnumerator();

        protected override void Dispose(bool A_0)
        {
            if (A_0)
            {
                foreach (PrintSystemJobInfo job in _jobs) job.Dispose();
                _jobs.Clear();
            }

            base.Dispose(A_0);
        }
    }

    /// <summary>A job's document, written to the queue as it is written here; printed when it is closed (or
    /// cancelled, where the stream was told not to commit).</summary>
    public partial class PrintQueueStream : Stream
    {
        private JobWriter _writer;
        private readonly bool _commitDataOnClose;
        private long _written;

        public PrintQueueStream(PrintQueue printQueue, string printJobName)
            : this(printQueue, printJobName, false, null)
        {
        }

        public PrintQueueStream(PrintQueue printQueue, string printJobName, bool commitDataOnClose)
            : this(printQueue, printJobName, commitDataOnClose, null)
        {
        }

        public PrintQueueStream(PrintQueue printQueue, string printJobName, bool commitDataOnClose, PrintTicket printTicket)
        {
            ArgumentNullException.ThrowIfNull(printQueue);
            _commitDataOnClose = commitDataOnClose;
            _writer = Spooler.Current.StartJob(printQueue.SpoolerName, printJobName, printQueue.IsXpsDevice);
        }

        ~PrintQueueStream()
        {
            Dispose(false);
        }

        public int JobIdentifier => _writer?.Id ?? 0;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => _writer != null;
        public override long Length => _written;

        public override long Position
        {
            get => _written;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_writer == null, this);
            _writer.Write(buffer, offset, count);
            _written += count;
        }

        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state) =>
            base.BeginWrite(buffer, offset, count, callback, state);

        public override void EndWrite(IAsyncResult asyncResult) => base.EndWrite(asyncResult);

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>The XPS packaging's progress (the original commits each part as it is written); nothing to do
        /// for a job written as it goes.</summary>
        public void HandlePackagingProgressEvent(object sender, System.Windows.Xps.Packaging.PackagingProgressEventArgs e)
        {
        }

        public override void Close() => base.Close();

        protected override void Dispose(bool A_0)
        {
            JobWriter writer = Interlocked.Exchange(ref _writer, null);
            if (writer != null)
            {
                // closed: printed; abandoned (finalised) or told not to commit: cancelled
                if (A_0 && (_commitDataOnClose || _written > 0)) writer.Commit();
                else writer.Abort();
            }

            base.Dispose(A_0);
        }
    }

    // ---- the spooler's other objects: names, as the original's are on this side ----

    public abstract partial class PrintFilter : PrintSystemObject
    {
        internal PrintFilter(string name) => base.Name = name ?? string.Empty;

        protected override void InternalDispose(bool disposing) => base.InternalDispose(disposing);
    }

    public sealed partial class PrintDriver : PrintFilter
    {
        internal PrintDriver(string name) : base(name)
        {
        }

        public sealed override void Commit()
        {
        }

        public sealed override void Refresh()
        {
        }

        protected sealed override void InternalDispose(bool disposing) => base.InternalDispose(disposing);
    }

    public sealed partial class PrintProcessor : PrintFilter
    {
        internal PrintProcessor(string name) : base(name)
        {
        }

        public sealed override void Commit()
        {
        }

        public sealed override void Refresh()
        {
        }

        protected sealed override void InternalDispose(bool disposing) => base.InternalDispose(disposing);
    }

    public sealed partial class PrintPort : PrintSystemObject
    {
        internal PrintPort(string name) => base.Name = name ?? string.Empty;

        public sealed override void Commit()
        {
        }

        public sealed override void Refresh()
        {
        }

        protected sealed override void InternalDispose(bool disposing) => base.InternalDispose(disposing);
    }

    public partial class PrintQueueStringProperty
    {
        public PrintQueueStringProperty()
        {
        }

        public string Name { get; set; }

        public PrintQueueStringPropertyType Type { get; set; }
    }

    public partial class PrintSystemObjectPropertyChangedEventArgs : EventArgs, IDisposable
    {
        public PrintSystemObjectPropertyChangedEventArgs(string eventName) => PropertyName = eventName;

        public string PropertyName { get; }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool A_0)
        {
        }
    }

    public partial class PrintSystemObjectPropertiesChangedEventArgs : EventArgs, IDisposable
    {
        public PrintSystemObjectPropertiesChangedEventArgs(StringCollection events) => PropertiesNames = events;

        public StringCollection PropertiesNames { get; }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool A_0)
        {
        }
    }

    // ---- enumerations ----

    public enum EnumeratedPrintQueueTypes
    {
        Queued = 1,
        DirectPrinting = 2,
        Shared = 8,
        Connections = 16,
        TerminalServer = 32768,
        Local = 64,
        EnableDevQuery = 128,
        KeepPrintedJobs = 256,
        WorkOffline = 1024,
        EnableBidi = 2048,
        RawOnly = 4096,
        PublishedInDirectoryServices = 8192,
        Fax = 16384,
        PushedUserConnection = 131072,
        PushedMachineConnection = 262144,
    }

    [Flags]
    public enum PrintQueueAttributes
    {
        None = 0,
        Queued = 1,
        Direct = 2,
        Shared = 8,
        Hidden = 32,
        EnableDevQuery = 128,
        KeepPrintedJobs = 256,
        ScheduleCompletedJobsFirst = 512,
        EnableBidi = 2048,
        RawOnly = 4096,
        Published = 8192,
    }

    [Flags]
    public enum PrintQueueStatus
    {
        None = 0,
        Paused = 1,
        Error = 2,
        PendingDeletion = 4,
        PaperJam = 8,
        PaperOut = 16,
        ManualFeed = 32,
        PaperProblem = 64,
        Offline = 128,
        IOActive = 256,
        Busy = 512,
        Printing = 1024,
        OutputBinFull = 2048,
        NotAvailable = 4096,
        Waiting = 8192,
        Processing = 16384,
        Initializing = 32768,
        WarmingUp = 65536,
        TonerLow = 131072,
        NoToner = 262144,
        PagePunt = 524288,
        UserIntervention = 1048576,
        OutOfMemory = 2097152,
        DoorOpen = 4194304,
        ServerUnknown = 8388608,
        PowerSave = 16777216,
    }

    [Flags]
    public enum PrintJobStatus
    {
        None = 0,
        Paused = 1,
        Error = 2,
        Deleting = 4,
        Spooling = 8,
        Printing = 16,
        Offline = 32,
        PaperOut = 64,
        Printed = 128,
        Deleted = 256,
        Blocked = 512,
        UserIntervention = 1024,
        Restarted = 2048,
        Completed = 4096,
        Retained = 8192,
    }

    public enum PrintJobPriority
    {
        None = 0,
        Minimum = 1,
        Default = 1,
        Maximum = 99,
    }

    public enum PrintJobType
    {
        None = 0,
        Xps = 1,
        Legacy = 2,
    }

    public enum PrintServerEventLoggingTypes
    {
        None = 0,
        LogPrintingSuccessEvents = 1,
        LogPrintingErrorEvents = 2,
        LogPrintingWarningEvents = 3,
        LogPrintingInformationEvents = 4,
        LogAllPrintingEvents = 5,
    }

    public enum PrintServerIndexedProperty
    {
        DefaultSpoolDirectory = 0,
        PortThreadPriority = 1,
        DefaultPortThreadPriority = 2,
        SchedulerPriority = 3,
        DefaultSchedulerPriority = 4,
        BeepEnabled = 5,
        NetPopup = 6,
        EventLog = 7,
        MajorVersion = 8,
        MinorVersion = 9,
        RestartJobOnPoolTimeout = 10,
        RestartJobOnPoolEnabled = 11,
    }

    public enum LocalPrintServerIndexedProperty
    {
        DefaultSpoolDirectory = 0,
        PortThreadPriority = 1,
        DefaultPortThreadPriority = 2,
        SchedulerPriority = 3,
        DefaultSchedulerPriority = 4,
        BeepEnabled = 5,
        NetPopup = 6,
        EventLog = 7,
        MajorVersion = 8,
        MinorVersion = 9,
        RestartJobOnPoolTimeout = 10,
        RestartJobOnPoolEnabled = 11,
        DefaultPrintQueue = 12,
    }

    public enum PrintSystemDesiredAccess
    {
        None = 0,
        AdministrateServer = 983041,
        EnumerateServer = 131074,
        UsePrinter = 131080,
        AdministratePrinter = 983052,
    }

    public enum PrintSystemObjectLoadMode
    {
        None = 0,
        LoadUninitialized = 1,
        LoadInitialized = 2,
    }

    public enum PrintQueueStringPropertyType
    {
        Location = 0,
        Comment = 1,
        ShareName = 2,
    }
}

namespace System.Windows.Xps
{
    public enum XpsDocumentNotificationLevel
    {
        None = 0,
        ReceiveNotificationEnabled = 1,
        ReceiveNotificationDisabled = 2,
    }
}
