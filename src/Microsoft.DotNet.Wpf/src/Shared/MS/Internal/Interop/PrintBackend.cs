// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The vocabulary of the printing seam (see PlatformPrint.cs): a printer, a job's settings, and a
// backend. Kept apart from PlatformPrint's chooser so that a backend can be link-compiled on its own
// into an assembly that is not WindowsBase -- a standalone WinForms app on macOS submits through
// CocoaPrint from System.Drawing, with no WPF in the process to own a PlatformPrint.
//

using System.IO;

namespace MS.Internal.Interop
{
    /// <summary>A printer, described in terms every platform can produce.</summary>
    internal sealed class PrinterInfo
    {
        /// <summary>The name a job is submitted to. Unique on the machine.</summary>
        internal string Name { get; set; }

        /// <summary>The name to show a user, if the platform distinguishes one.</summary>
        internal string DisplayName { get; set; }

        /// <summary>Where the printer is, when the platform says. Shown in a chooser; never matched on.</summary>
        internal string Location { get; set; }

        internal bool IsDefault { get; set; }

        /// <summary>Paper size in WPF units (96ths of an inch). Zero when unknown.</summary>
        internal double PageWidth { get; set; }

        internal double PageHeight { get; set; }

        /// <summary>
        /// The part of the page this printer can actually mark, in WPF units, measured from the
        /// top left of the PAPER -- which is where WPF puts the origin, and is not where the
        /// printer does.
        ///
        /// Zero extent means "not known", and a caller should then treat the whole page as
        /// imageable rather than assume a margin it has no evidence for.
        /// </summary>
        internal double ImageableOriginX { get; set; }

        internal double ImageableOriginY { get; set; }

        internal double ImageableWidth { get; set; }

        internal double ImageableHeight { get; set; }
    }

    /// <summary>What the user chose, and what the job should therefore do.</summary>
    internal sealed class PrintJobSettings
    {
        internal string PrinterName { get; set; }

        internal int Copies { get; set; } = 1;

        /// <summary>Inclusive, 1-based. Zero means "everything", which is not the same as page zero.</summary>
        internal int FirstPage { get; set; }

        internal int LastPage { get; set; }

        internal bool Landscape { get; set; }

        /// <summary>Paper size in WPF units, as chosen. Zero to leave the printer's default alone.</summary>
        internal double PageWidth { get; set; }

        internal double PageHeight { get; set; }

        /// <summary>
        /// A file to write the job into instead of putting it on the printer's port. Null for an
        /// ordinary print.
        ///
        /// This is "print to file", which every print system has and which the Windows print dialog
        /// offers as a checkbox. It is not a test hook: some printers have no other usable mode --
        /// Microsoft Print to PDF sits on the PORTPROMPT port and asks the user for a path with a
        /// modal Save dialog, which an application that is not driving one has no way to answer.
        /// Naming the file up front is how you print to it without a person present.
        /// </summary>
        internal string OutputFile { get; set; }
    }

    /// <summary>One platform's print system.</summary>
    internal interface IPrintBackend
    {
        /// <summary>Printers this machine can reach. Empty is a normal answer, not an error.</summary>
        PrinterInfo[] EnumeratePrinters();

        /// <summary>
        /// Shows the platform's print UI, updating <paramref name="settings"/> with what the user
        /// chose. False means they cancelled.
        ///
        /// Heads whose print UI appears at SUBMISSION time rather than before it -- iOS, Android and
        /// the browser, none of which can host a modal dialog at all -- return true without showing
        /// anything, and the system UI appears when the document is handed over.
        /// </summary>
        bool ShowPrintUI(PrintJobSettings settings);

        /// <summary>
        /// Hands a rendered document to the print system. The stream is positioned at the start and
        /// is the caller's to dispose.
        /// </summary>
        bool Submit(string jobName, Stream document, PrintJobSettings settings);

        /// <summary>
        /// Whether <see cref="Submit"/> is the way a job reaches this print system.
        ///
        /// True everywhere except Windows. Five of the six print systems want a finished document
        /// and will render it themselves; Windows' spooler is a drawing surface, and a job is made
        /// by drawing pages into a device context that the spooler owns. There is no document to
        /// hand it, and a PDF written for one of the others is bytes it has no decoder for.
        ///
        /// A caller that sees false must drive the device instead -- which is what
        /// XpsDocumentWriter does, through the GDI device in ReachFramework. Submit still works
        /// there, but it means something narrower: raw, printer-ready data straight to the port.
        /// </summary>
        bool TakesRenderedDocument => true;
    }
}
