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

        /// <summary>The name the platform knows this printer by, which is what a job is submitted to.</summary>
        public string Name => _printer.Name ?? string.Empty;

        /// <summary>The name to show a user. Falls back to the queue name.</summary>
        public string FullName => string.IsNullOrEmpty(_printer.DisplayName) ? Name : _printer.DisplayName;

        /// <summary>Whether this is the system default.</summary>
        public bool IsDefault => _printer.IsDefault;

        public string Comment => _printer.Location ?? string.Empty;

        /// <summary>
        /// The paper size this printer reports, in WPF units. Used for the page a document is
        /// paginated onto, so getting it wrong reflows every page.
        /// </summary>
        internal Windows.Size DefaultPageSize
            => _printer.PageWidth > 0 && _printer.PageHeight > 0
                ? new Windows.Size(_printer.PageWidth, _printer.PageHeight)
                : new Windows.Size(816, 1056);

        /// <summary>
        /// What this printer can do, as a PrintCapabilities.
        ///
        /// PrintCapabilities is really implemented, in ReachFramework, and is a data object; what is
        /// missing off Windows is the PROVIDER that fills one in from a driver (prntvpt and winspool,
        /// both of which throw DllNotFoundException here). So this answers from the platform's own
        /// description of the printer instead, which covers the one thing callers use it for: the
        /// imageable area.
        /// </summary>
        public PrintCapabilities GetPrintCapabilities(PrintTicket printTicket)
        {
            _ = printTicket;
            return null;
        }

        public PrintCapabilities GetPrintCapabilities() => GetPrintCapabilities(null);

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

        public string Name { get; private set; } = string.Empty;

        public PrintQueueCollection GetPrintQueues() => new PrintQueueCollection(Discover());

        public PrintQueueCollection GetPrintQueues(EnumeratedPrintQueueTypes[] enumerationFlag)
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
