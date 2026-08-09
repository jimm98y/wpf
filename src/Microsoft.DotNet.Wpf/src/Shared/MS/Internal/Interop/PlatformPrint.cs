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
                s_resolved = true;
            }
        }

        private static IPrintBackend Create()
        {
            if (OperatingSystem.IsBrowser()) return new BrowserPrint();
            if (OperatingSystem.IsIOS()) return new UIKitPrint();
            if (OperatingSystem.IsAndroid()) return new AndroidPrintBackend();
            if (OperatingSystem.IsMacOS()) return new CocoaPrint();
            if (OperatingSystem.IsLinux()) return new Wayland.CupsPrint();

            // Windows. The existing PrintDlgEx path still owns the dialog there, and the spooler
            // wants to be driven page by page rather than handed a PDF, so it has no backend of this
            // shape yet.
            return null;
        }

        internal static PrinterInfo[] EnumeratePrinters()
            => Current?.EnumeratePrinters() ?? Array.Empty<PrinterInfo>();

        internal static bool ShowPrintUI(PrintJobSettings settings)
            => Current?.ShowPrintUI(settings) ?? false;

        internal static bool Submit(string jobName, Stream document, PrintJobSettings settings)
            => Current?.Submit(jobName, document, settings) ?? false;

        /// <summary>Whether anything on this machine can print at all.</summary>
        internal static bool IsAvailable => Current != null;
    }
}
