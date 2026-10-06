// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// PrintQueue, PrintServer and the small surface around them.
//
// What ships today in place of this is a sixteen-line class with a private constructor and no
// members, so PrintDialog.PrintVisual fails with a TypeLoadException on the desktop heads and a
// NullReferenceException in the browser. That stub exists because the real System.Printing is a
// C++/CLI project this port does not build.
//
// This is not a reimplementation of that project. The 785-line contract it exposes is mostly a
// property-bag model over the Windows spooler -- PrintSystemJobInfo, indexed properties, event
// logging levels -- that no WPF application reaches through the printing API. What is implemented
// here is what PrintDialog, the document viewers and ReachFramework actually call, which is a
// dozen members. The rest stays absent, and absent is honest: a stub that answers plausibly is how
// you get a bug report about pages printing blank.
//
// A queue here is a PRINTER as the platform describes it, discovered through PlatformPrint. On a
// machine with no print system that is an empty list, which is a state PrintDialog already knows
// how to report.
//

using System.Collections;
using System.IO;
using System.Collections.Generic;
using System.Printing.Interop;
using System.Windows.Xps;
using MS.Internal.Interop;

namespace System.Printing
{
    /// <summary>A printer, as far as WPF's printing API is concerned.</summary>
    public partial class PrintQueue : PrintSystemObject
    {
        private readonly PrinterInfo _printer;

        internal PrintQueue(PrinterInfo printer)
        {
            _printer = printer;
        }

        /// <summary>
        /// The name the platform knows this printer by, which is what a job is submitted to.
        ///
        /// An OVERRIDE, not a new property. The reference assembly declares Name once, as a virtual
        /// on PrintSystemObject, so everything compiled against it -- PrintDialog included -- calls
        /// PrintSystemObject::get_Name. Declaring it here instead of overriding leaves that slot
        /// empty, and the call fails at runtime with MissingMethodException rather than at build.
        /// </summary>
        public override string Name => _printer.Name ?? string.Empty;

        /// <summary>The name to show a user. Falls back to the queue name.</summary>
        public string FullName => string.IsNullOrEmpty(_printer.DisplayName) ? Name : _printer.DisplayName;

        /// <summary>Whether this is the system default.</summary>
        public bool IsDefault => _printer.IsDefault;

        public string Comment => _printer.Location ?? string.Empty;

        /// <summary>
        /// Set by the Windows print dialog on the queue it hands back.
        ///
        /// It meant something once -- partial trust restricted what a queue would let an
        /// application do -- and .NET has had no partial trust for a decade. It stays because
        /// PrintDlgExMarshaler assigns it, and a property the reference assembly declares and the
        /// implementation does not is a MissingMethodException the moment the dialog closes.
        /// </summary>
        public bool InPartialTrust { get; set; }

        /// <summary>
        /// The paper size this printer reports, in WPF units. Used for the page a document is
        /// paginated onto, so getting it wrong reflows every page.
        /// </summary>
        internal Windows.Size DefaultPageSize
            => _printer.PageWidth > 0 && _printer.PageHeight > 0
                ? new Windows.Size(_printer.PageWidth, _printer.PageHeight)
                : new Windows.Size(816, 1056);

        /// <summary>
        /// Where this queue's jobs go instead of the printer's port, if anywhere.
        ///
        /// "Print to file", which the Windows print dialog offers as a checkbox and which some
        /// printers have no alternative to: Microsoft Print to PDF sits on the PORTPROMPT port and
        /// asks for a path with a modal Save dialog, so an application printing without a person in
        /// front of it has to name the file up front or hang waiting for one.
        ///
        /// Internal because it is not part of the printing contract WPF applications compile
        /// against. It is set by whatever chose the destination -- the print dialog, or a caller
        /// that already knows where the bytes should land.
        /// </summary>
        internal string OutputFile { get; set; }

        /// <summary>
        /// What this printer can do, as a PrintCapabilities.
        ///
        /// PrintCapabilities and the provider that fills one in are both real and both in
        /// ReachFramework; what varies is whether the provider can reach a driver. On Windows it
        /// can, through winspool, and the answer is the driver's own list of papers, resolutions
        /// and imageable areas. Elsewhere PTProviderBase.Create has no driver to bind to and this
        /// is null -- which is what every caller already treats as "ask the platform instead".
        /// </summary>
        public PrintCapabilities GetPrintCapabilities(PrintTicket printTicket)
        {
            if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(Name)) return null;

            try
            {
                using MS.Internal.Printing.Configuration.PTProviderBase provider =
                    MS.Internal.Printing.Configuration.PTProviderBase.Create(
                        Name, MaxPrintSchemaVersion, MaxPrintSchemaVersion);

                using MemoryStream ticket = printTicket?.GetXmlStream();
                using MemoryStream capabilities = provider.GetPrintCapabilities(ticket);

                if (capabilities == null) return null;

                capabilities.Position = 0;
                return new PrintCapabilities(capabilities);
            }
            catch (PrintSystemException)
            {
                // An offline printer, a queue whose driver is not installed locally, or a driver
                // that will not answer. None of those is a reason to fail the caller: they asked
                // what the printer can do and the honest answer is that nobody knows.
                return null;
            }
            catch (System.Xml.XmlException)
            {
                // A driver that answered with something that is not print schema. Rare, and seen
                // from third-party drivers; the same answer applies.
                return null;
            }
        }

        public PrintCapabilities GetPrintCapabilities() => GetPrintCapabilities(null);

        /// <summary>The newest print schema version this stack understands.</summary>
        public static int MaxPrintSchemaVersion => 1;

        /// <summary>The ticket a job uses when the caller has not supplied one.</summary>
        public PrintTicket DefaultPrintTicket => _defaultTicket ??= new PrintTicket();

        private PrintTicket _defaultTicket;

        public PrintTicket UserPrintTicket
        {
            get => DefaultPrintTicket;
            set => _defaultTicket = value;
        }

        public PrintJobSettings CurrentJobSettings => _jobSettings ??= new PrintJobSettings();

        private PrintJobSettings _jobSettings;

        /// <summary>
        /// The writer that turns WPF content into a print job on this queue. The single funnel for
        /// PrintDialog.PrintVisual, PrintDialog.PrintDocument and every document viewer.
        /// </summary>
        public static XpsDocumentWriter CreateXpsDocumentWriter(PrintQueue printQueue)
            => new XpsDocumentWriter(printQueue);

        /// <summary>
        /// A writer that renders straight to a stream, with no print system involved. This is export
        /// rather than printing, and it is the same pipeline: it exists so that "save as PDF" does
        /// not require a printer to be installed.
        /// </summary>
        public static XpsDocumentWriter CreateXpsDocumentWriter(Stream destination, Windows.Size pageSize)
            => new XpsDocumentWriter(destination, pageSize);

        public static XpsDocumentWriter CreateXpsDocumentWriter(string jobDescription, PrintQueue printQueue)
        {
            var writer = new XpsDocumentWriter(printQueue);
            writer.JobDescription = jobDescription;
            return writer;
        }

        // ---- the overloads that ask the user first ---------------------------------
        //
        // These show a print dialog and hand back a writer for whatever was chosen, reporting the
        // page geometry through ref parameters. They are not a convenience: they are what every
        // document viewer in the framework calls to implement its Print button --
        // DocumentViewerBase, FlowDocumentScrollViewer, FlowDocumentReader's single-page viewer and
        // the XPS viewer's own. Without them printing from a viewer fails with a
        // MissingMethodException, which no amount of correct plumbing underneath would fix.

        /// <summary>
        /// Shows a print dialog and returns a writer for the chosen printer, reporting the page
        /// size it settled on. Null when the user cancelled.
        /// </summary>
        public static XpsDocumentWriter CreateXpsDocumentWriter(ref double width, ref double height)
        {
            var dialog = new Windows.Controls.PrintDialog();

            if (dialog.ShowDialog() != true) return null;

            width = dialog.PrintableAreaWidth;
            height = dialog.PrintableAreaHeight;

            return Configure(dialog, null);
        }

        public static XpsDocumentWriter CreateXpsDocumentWriter(
            ref PrintDocumentImageableArea documentImageableArea)
            => CreateXpsDocumentWriter(null, ref documentImageableArea);

        public static XpsDocumentWriter CreateXpsDocumentWriter(
            string jobDescription,
            ref PrintDocumentImageableArea documentImageableArea)
        {
            var dialog = new Windows.Controls.PrintDialog();

            if (dialog.ShowDialog() != true) return null;

            documentImageableArea = ImageableArea(dialog);

            return Configure(dialog, jobDescription);
        }

        public static XpsDocumentWriter CreateXpsDocumentWriter(
            ref PrintDocumentImageableArea documentImageableArea,
            ref Windows.Controls.PageRangeSelection pageRangeSelection,
            ref Windows.Controls.PageRange pageRange)
            => CreateXpsDocumentWriter(null, ref documentImageableArea, ref pageRangeSelection, ref pageRange);

        public static XpsDocumentWriter CreateXpsDocumentWriter(
            string jobDescription,
            ref PrintDocumentImageableArea documentImageableArea,
            ref Windows.Controls.PageRangeSelection pageRangeSelection,
            ref Windows.Controls.PageRange pageRange)
        {
            var dialog = new Windows.Controls.PrintDialog
            {
                UserPageRangeEnabled = true,
            };

            if (dialog.ShowDialog() != true) return null;

            documentImageableArea = ImageableArea(dialog);
            pageRangeSelection = dialog.PageRangeSelection;
            pageRange = dialog.PageRange;

            return Configure(dialog, jobDescription);
        }

        /// <summary>
        /// The page the chosen printer will actually mark, as the dialog understands it.
        ///
        /// Origin at zero and the whole page imageable is the honest answer when nothing better is
        /// known: the alternative is inventing a hardware margin, and a caller that lays out to a
        /// margin the printer does not have loses content at the edge for no reason.
        /// </summary>
        private static PrintDocumentImageableArea ImageableArea(Windows.Controls.PrintDialog dialog)
        {
            double width = dialog.PrintableAreaWidth;
            double height = dialog.PrintableAreaHeight;

            var area = new PrintDocumentImageableArea
            {
                MediaSizeWidth = width,
                MediaSizeHeight = height,
                ExtentWidth = width,
                ExtentHeight = height,
            };

            PrinterInfo printer = dialog.PrintQueue?.Printer;

            if (printer != null && printer.ImageableWidth > 0 && printer.ImageableHeight > 0)
            {
                area.OriginWidth = printer.ImageableOriginX;
                area.OriginHeight = printer.ImageableOriginY;
                area.ExtentWidth = printer.ImageableWidth;
                area.ExtentHeight = printer.ImageableHeight;
            }

            return area;
        }

        private static XpsDocumentWriter Configure(Windows.Controls.PrintDialog dialog, string jobDescription)
        {
            PrintQueue queue = dialog.PrintQueue;

            if (queue != null && dialog.PrintTicket != null)
            {
                queue.UserPrintTicket = dialog.PrintTicket;
            }

            return CreateXpsDocumentWriter(jobDescription, queue);
        }

        // ---- internals the rest of the stack reaches ------------------------------

        /// <summary>
        /// The device the alpha flattener draws into. Returning one is the whole reason printing
        /// works at all now: everything above this call is managed and always was.
        /// </summary>
        internal ILegacyDevice GetLegacyDevice() => null;

        internal static uint GetDpiX(ILegacyDevice legacyDevice) => 96;

        internal static uint GetDpiY(ILegacyDevice legacyDevice) => 96;

        internal Windows.Xps.Serialization.RCW.IXpsOMPackageWriter XpsOMPackageWriter
        {
            set { }
        }

        internal PrinterInfo Printer => _printer;
    }

    /// <summary>The printers a print server offers.</summary>
    public partial class PrintQueueCollection : PrintSystemObjects, IEnumerable<PrintQueue>, IEnumerable, IDisposable
    {
        private readonly List<PrintQueue> _queues;

        internal PrintQueueCollection(List<PrintQueue> queues)
        {
            _queues = queues ?? new List<PrintQueue>();
        }

        public IEnumerator<PrintQueue> GetEnumerator() => _queues.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _queues.GetEnumerator();

        public void Add(PrintQueue printQueue) => _queues.Add(printQueue);

        public override void Dispose() { }
    }

    /// <summary>A source of print queues.</summary>
    public partial class PrintServer : PrintSystemObject
    {
        public PrintServer() { }

        public PrintServer(string path) { Name = path; }

        public PrintQueueCollection GetPrintQueues() => new PrintQueueCollection(Discover());

        public PrintQueueCollection GetPrintQueues(EnumeratedPrintQueueTypes[] enumerationFlag)
            => GetPrintQueues();

        // The filtered overloads. The filter names which properties to fetch eagerly, which is an
        // optimisation for a queue model that loads them one spooler round-trip at a time; this one
        // has already got everything the platform gave it, so there is nothing to defer and nothing
        // to filter. They exist because PrintDlgExMarshaler calls the two-argument form, and a
        // reference assembly that declares a method the implementation does not have is a
        // MissingMethodException the compiler cannot warn about -- which is exactly how the Windows
        // print dialog was failing.

        public PrintQueueCollection GetPrintQueues(PrintQueueIndexedProperty[] propertiesFilter)
            => GetPrintQueues();

        public PrintQueueCollection GetPrintQueues(PrintQueueIndexedProperty[] propertiesFilter,
                                                   EnumeratedPrintQueueTypes[] enumerationFlag)
            => GetPrintQueues();

        public PrintQueueCollection GetPrintQueues(string[] propertiesFilter)
            => GetPrintQueues();

        public PrintQueueCollection GetPrintQueues(string[] propertiesFilter,
                                                   EnumeratedPrintQueueTypes[] enumerationFlag)
            => GetPrintQueues();

        internal static List<PrintQueue> Discover()
        {
            var queues = new List<PrintQueue>();

            foreach (PrinterInfo printer in PlatformPrint.EnumeratePrinters())
            {
                queues.Add(new PrintQueue(printer));
            }

            return queues;
        }
    }

    /// <summary>The print server on this machine.</summary>
    public sealed partial class LocalPrintServer : PrintServer
    {
        public LocalPrintServer() { }

        public LocalPrintServer(string path) : base(path) { }

        /// <summary>
        /// The system's default printer, or null when there is not one. Null is a state
        /// PrintDialog already handles: it is what "no printer installed" has always looked like.
        /// </summary>
        public PrintQueue DefaultPrintQueue
        {
            get
            {
                List<PrintQueue> queues = Discover();

                foreach (PrintQueue queue in queues)
                {
                    if (queue.IsDefault) return queue;
                }

                return queues.Count != 0 ? queues[0] : null;
            }
        }

        public static PrintQueue GetDefaultPrintQueue() => new LocalPrintServer().DefaultPrintQueue;
    }

    /// <summary>The area of a page a printer can actually mark, in WPF units.</summary>
    public partial class PrintDocumentImageableArea
    {
        internal PrintDocumentImageableArea() { }

        public double MediaSizeWidth { get; internal set; }

        public double MediaSizeHeight { get; internal set; }

        public double OriginWidth { get; internal set; }

        public double OriginHeight { get; internal set; }

        public double ExtentWidth { get; internal set; }

        public double ExtentHeight { get; internal set; }
    }

    /// <summary>Per-job settings. Only the description is meaningful here; it names the job in a queue.</summary>
    public partial class PrintJobSettings
    {
        internal PrintJobSettings() { }

        public string Description { get; set; }

        public PrintTicket CurrentPrintTicket { get; set; }
    }

    public abstract partial class PrintSystemObject : IDisposable
    {
        protected PrintSystemObject() { }

        /// <summary>
        /// Declared HERE because that is where the reference assembly declares it. Callers compiled
        /// against the contract bind to this slot, so a derived class that hides it rather than
        /// overriding it produces a MissingMethodException the compiler cannot warn about.
        /// </summary>
        public virtual string Name { get; internal set; } = string.Empty;

        public virtual void Dispose() { }
    }

    public abstract partial class PrintSystemObjects : IDisposable
    {
        protected PrintSystemObjects() { }

        public virtual void Dispose() { }
    }

    internal class PrintSystemDispatcherObject : Windows.Threading.DispatcherObject
    {
        public void VerifyThreadLocality() { }
    }

    /// <summary>
    /// Which of a queue's properties a caller wants fetched.
    ///
    /// Present for the same reason as the filtered GetPrintQueues overloads that take it: the
    /// reference assembly declares it, so the implementation must too or the two are not the same
    /// type at run time. Nothing here acts on the filter -- the platform hands over every property
    /// it has in one call, so there is nothing left to defer.
    /// </summary>
    public enum PrintQueueIndexedProperty
    {
        Name = 0,
        ShareName = 1,
        Comment = 2,
        Location = 3,
        Description = 4,
        Priority = 5,
        DefaultPriority = 6,
        StartTimeOfDay = 7,
        UntilTimeOfDay = 8,
        AveragePagesPerMinute = 9,
        NumberOfJobs = 10,
        QueueAttributes = 11,
        QueueDriver = 12,
        QueuePort = 13,
        QueuePrintProcessor = 14,
        HostingPrintServer = 15,
        QueueStatus = 16,
        SeparatorFile = 17,
        UserPrintTicket = 18,
        DefaultPrintTicket = 19,
    }

    public enum EnumeratedPrintQueueTypes
    {
        Connections = 5,
        Local = 6,
        Shared = 8,
        PushedMachineConnection = 15,
        PushedUserConnection = 16,
        EnumerateFromLocalPrintServer = 20,
        Fax = 21,
        WorkOffline = 22,
        PublishedInDirectoryServices = 23,
        TerminalServer = 24,
    }
}
