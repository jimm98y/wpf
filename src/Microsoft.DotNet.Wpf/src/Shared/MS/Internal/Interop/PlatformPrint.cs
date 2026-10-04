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
        /// The same, with the settings spelled out. For a caller that cannot name PrintJobSettings:
        /// WinForms' integration assembly sees this one through InternalsVisibleTo and also sees
        /// System.Drawing's link-compiled copy of the backend vocabulary (for a standalone macOS app),
        /// so the type name is ambiguous there while this method is not.
        /// </summary>
        internal static bool Submit(string jobName, Stream document, string printerName, int copies,
                                    int firstPage, int lastPage, bool landscape,
                                    double pageWidth, double pageHeight, string outputFile)
            => Submit(jobName, document, new PrintJobSettings
            {
                PrinterName = printerName,
                Copies = copies,
                FirstPage = firstPage,
                LastPage = lastPage,
                Landscape = landscape,
                PageWidth = pageWidth,
                PageHeight = pageHeight,
                OutputFile = outputFile,
            });

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
