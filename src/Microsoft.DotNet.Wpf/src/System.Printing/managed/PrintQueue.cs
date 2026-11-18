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
        /// <summary>The name to show a user. Falls back to the queue name.</summary>
        public string FullName => _printer == null || string.IsNullOrEmpty(_printer.DisplayName) ? Name : _printer.DisplayName;

        /// <summary>Whether this is the system default.</summary>
        public bool IsDefault => _state?.IsDefault ?? _printer?.IsDefault ?? false;

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
            => _printer != null && _printer.PageWidth > 0 && _printer.PageHeight > 0
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
            if (string.IsNullOrEmpty(Name)) return null;
            // off Windows: what the print system says of the printer (CUPS on Linux and macOS), in the Print Schema a
            // driver answers in; nothing where it cannot say
            if (!OperatingSystem.IsWindows())
            {
                PrinterCapabilities caps = CupsCapabilities.Query(Name);
                if (caps == null) return null;
                using MemoryStream document = PrintSchemaCapabilities.Write(caps);
                return new PrintCapabilities(document);
            }

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
        public virtual PrintTicket DefaultPrintTicket
        {
            get => _defaultTicket ??= new PrintTicket();
            set => _defaultTicket = value;
        }

        private PrintTicket _defaultTicket, _userTicket;

        public virtual PrintTicket UserPrintTicket
        {
            get => _userTicket ?? DefaultPrintTicket;
            set => _userTicket = value;
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


    /// <summary>A printer's capabilities as a driver writes them (Print Schema PrintCapabilities): paper sizes with
    /// their imageable areas, duplex, colour, resolutions, copies and orientation.</summary>
    internal static class PrintSchemaCapabilities
    {
        private const string Psf = "http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework";
        private const string Psk = "http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords";

        // the PWG names of the sizes the schema has keywords for
        private static readonly (string Prefix, string Keyword)[] s_sizes =
        {
            ("na_letter_", "NorthAmericaLetter"), ("na_legal_", "NorthAmericaLegal"), ("na_executive_", "NorthAmericaExecutive"),
            ("na_ledger_", "NorthAmericaTabloid"), ("iso_a3_", "ISOA3"), ("iso_a4_", "ISOA4"), ("iso_a5_", "ISOA5"), ("iso_a6_", "ISOA6"),
            ("jis_b4_", "JISB4"), ("jis_b5_", "JISB5"), ("iso_b5_", "ISOB5Extra"), ("iso_c5_", "ISOC5Envelope"), ("iso_dl_", "ISODLEnvelope"),
            ("na_number-10_", "NorthAmericaNumber10Envelope"), ("na_monarch_", "NorthAmericaMonarchEnvelope"), ("jpn_hagaki_", "JapanHagakiPostcard"),
        };

        private static long Microns(double wpf) => (long)Math.Round(wpf / 96.0 * 25400.0);

        internal static MemoryStream Write(PrinterCapabilities caps)
        {
            var stream = new MemoryStream();
            using (var w = System.Xml.XmlWriter.Create(stream, new System.Xml.XmlWriterSettings { Encoding = new System.Text.UTF8Encoding(false), CloseOutput = false }))
            {
                w.WriteStartElement("psf", "PrintCapabilities", Psf);
                w.WriteAttributeString("xmlns", "psk", null, Psk);
                w.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
                w.WriteAttributeString("xmlns", "xsd", null, "http://www.w3.org/2001/XMLSchema");
                w.WriteAttributeString("version", "1");

                Feature(w, "psk:PageMediaSize", () =>
                {
                    foreach (PaperMedia m in caps.Media)
                    {
                        string keyword = null;
                        foreach (var (prefix, k) in s_sizes)
                            if (m.Name.StartsWith(prefix, StringComparison.Ordinal)) { keyword = k; break; }
                        w.WriteStartElement("psf", "Option", Psf);
                        if (keyword != null) w.WriteAttributeString("name", "psk:" + keyword);
                        Scored(w, "psk:MediaSizeWidth", Microns(m.Width));
                        Scored(w, "psk:MediaSizeHeight", Microns(m.Height));
                        w.WriteEndElement();
                    }
                });

                Feature(w, "psk:JobDuplexAllDocumentsContiguously", () =>
                {
                    Option(w, "psk:OneSided");
                    if (caps.Duplex) { Option(w, "psk:TwoSidedLongEdge"); Option(w, "psk:TwoSidedShortEdge"); }
                });

                Feature(w, "psk:PageOutputColor", () =>
                {
                    if (caps.Color) Option(w, "psk:Color");
                    Option(w, "psk:Grayscale");
                    Option(w, "psk:Monochrome");
                });

                if (caps.Resolutions.Count > 0)
                    Feature(w, "psk:PageResolution", () =>
                    {
                        foreach (var (x, y) in caps.Resolutions)
                        {
                            w.WriteStartElement("psf", "Option", Psf);
                            Scored(w, "psk:ResolutionX", x);
                            Scored(w, "psk:ResolutionY", y);
                            w.WriteEndElement();
                        }
                    });

                Feature(w, "psk:PageOrientation", () => { Option(w, "psk:Portrait"); Option(w, "psk:Landscape"); });

                w.WriteStartElement("psf", "ParameterDef", Psf);
                w.WriteAttributeString("name", "psk:JobCopiesAllDocuments");
                Property(w, "psf:DataType", "xsd:QName", "xsd:integer");
                Property(w, "psf:DefaultValue", "xsd:integer", "1");
                Property(w, "psf:MinValue", "xsd:integer", "1");
                Property(w, "psf:MaxValue", "xsd:integer", Math.Max(1, caps.MaxCopies).ToString(System.Globalization.CultureInfo.InvariantCulture));
                Property(w, "psf:Multiple", "xsd:integer", "1");
                Property(w, "psf:Mandatory", "xsd:QName", "psk:Unconditional");
                Property(w, "psf:UnitType", "xsd:string", "copies");
                w.WriteEndElement();

                // the first size's imageable area (the default page's, the margins its printer keeps)
                if (caps.Media.Count > 0)
                {
                    PaperMedia d = caps.Media[0];
                    w.WriteStartElement("psf", "Property", Psf);
                    w.WriteAttributeString("name", "psk:PageImageableSize");
                    Property(w, "psk:ImageableSizeWidth", "xsd:integer", Microns(d.Width).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Property(w, "psk:ImageableSizeHeight", "xsd:integer", Microns(d.Height).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    w.WriteStartElement("psf", "Property", Psf);
                    w.WriteAttributeString("name", "psk:ImageableArea");
                    Property(w, "psk:OriginWidth", "xsd:integer", Microns(d.Left).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Property(w, "psk:OriginHeight", "xsd:integer", Microns(d.Top).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Property(w, "psk:ExtentWidth", "xsd:integer", Microns(d.Width - d.Left - d.Right).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Property(w, "psk:ExtentHeight", "xsd:integer", Microns(d.Height - d.Top - d.Bottom).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    w.WriteEndElement();
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            stream.Position = 0;
            return stream;
        }

        private static void Feature(System.Xml.XmlWriter w, string name, Action options)
        {
            w.WriteStartElement("psf", "Feature", Psf);
            w.WriteAttributeString("name", name);
            Property(w, "psf:SelectionType", "xsd:QName", "psk:PickOne");
            options();
            w.WriteEndElement();
        }

        private static void Option(System.Xml.XmlWriter w, string name)
        {
            w.WriteStartElement("psf", "Option", Psf);
            w.WriteAttributeString("name", name);
            w.WriteEndElement();
        }

        private static void Scored(System.Xml.XmlWriter w, string name, long value)
        {
            w.WriteStartElement("psf", "ScoredProperty", Psf);
            w.WriteAttributeString("name", name);
            w.WriteStartElement("psf", "Value", Psf);
            w.WriteAttributeString("xsi", "type", "http://www.w3.org/2001/XMLSchema-instance", "xsd:integer");
            w.WriteString(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            w.WriteEndElement();
            w.WriteEndElement();
        }

        private static void Property(System.Xml.XmlWriter w, string name, string type, string value)
        {
            w.WriteStartElement("psf", "Property", Psf);
            w.WriteAttributeString("name", name);
            w.WriteStartElement("psf", "Value", Psf);
            w.WriteAttributeString("xsi", "type", "http://www.w3.org/2001/XMLSchema-instance", type);
            w.WriteString(value);
            w.WriteEndElement();
            w.WriteEndElement();
        }
    }
}
