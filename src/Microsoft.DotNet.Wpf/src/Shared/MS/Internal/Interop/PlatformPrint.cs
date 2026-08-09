// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The printing seam: one interface per head, chosen by operating system.
//
// Modelled on IMediaBackend rather than on the static cascades next door (PlatformWindow,
// PlatformClipboard), because printing is stateful and multi-step -- enumerate, choose, submit --
// rather than a set of independent calls. The leaf operations deliberately mirror what each native
// print system offers, so wiring a backend is mechanical.
//
// The vocabulary is deliberately neither Windows' nor CUPS': a "printer" has a name, a display name
// and a paper size, and a job has a name, a destination and a copy count. Every platform can express
// that, and nothing here leaks a DEVMODE or a cups_dest_t upwards.
//
// Note what submission takes: a STREAM of an already-rendered document, not a callback that draws.
// Every one of the five non-Windows print systems wants a finished file (macOS and iOS want PDF
// data, CUPS wants a file to spool, Android wants bytes written to a file descriptor, the browser
// wants a blob), and Windows is the only one that wants to be driven page by page. Handing over a
// finished document is therefore the shape that fits five of six, and the sixth can rasterize from
// it if it must.
//

using System;
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

    internal static class PlatformPrint
    {
        private static IPrintBackend s_backend;
        private static bool s_resolved;

        /// <summary>
        /// The backend for this platform, or null where there is none.
        ///
        /// The ordering is load-bearing and is the same one PlatformWindow documents: iOS reports as
        /// macOS to OperatingSystem, and Android reports as Linux, so the specific must be asked
        /// before the general.
        /// </summary>
        internal static IPrintBackend Current
        {
            get
            {
                if (s_resolved) return s_backend;

                s_resolved = true;
                s_backend = Create();
                return s_backend;
            }

            set
            {
                s_backend = value;

                // Null puts the platform's own back, rather than pinning null. A test that installs
                // a stand-in and then clears it wants the machine's print system again, not a
                // process where nothing can print for the rest of its life.
                s_resolved = value != null;
            }
        }

        private static IPrintBackend Create()
        {
            if (OperatingSystem.IsBrowser()) return new BrowserPrint();
            if (OperatingSystem.IsIOS()) return new UIKitPrint();
            if (OperatingSystem.IsAndroid()) return new AndroidPrintBackend();
            if (OperatingSystem.IsMacOS()) return new CocoaPrint();
            if (OperatingSystem.IsLinux()) return new Wayland.CupsPrint();
            if (OperatingSystem.IsWindows()) return new WindowsPrint();

            return null;
        }

        internal static PrinterInfo[] EnumeratePrinters()
            => Current?.EnumeratePrinters() ?? Array.Empty<PrinterInfo>();

        internal static bool ShowPrintUI(PrintJobSettings settings)
            => Current?.ShowPrintUI(settings) ?? false;

        internal static bool Submit(string jobName, Stream document, PrintJobSettings settings)
            => Current?.Submit(jobName, document, settings) ?? false;

        /// <summary>
        /// Whether a job reaches this platform's print system as a finished document. False means
        /// the caller must draw the pages itself; see <see cref="IPrintBackend.TakesRenderedDocument"/>.
        ///
        /// With no backend at all the answer is true, because the caller's next step is then to
        /// render a document and be told by Submit that nothing accepted it -- which is the failure
        /// this reports, rather than sending it down a device path that also does not exist.
        /// </summary>
        internal static bool TakesRenderedDocument => Current?.TakesRenderedDocument ?? true;

        /// <summary>Whether anything on this machine can print at all.</summary>
        internal static bool IsAvailable => Current != null;
    }
}
