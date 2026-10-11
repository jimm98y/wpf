// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Printing on Android: the WindowsBase half.
//
// The split is the one AndroidWindow and AndroidAccessibility already document, for the same reason:
// PrintManager and PrintDocumentAdapter are Java classes, WindowsBase cannot reference Mono.Android,
// and unlike iOS a Java subclass cannot be conjured at run time because it is bytecode. So the
// adapter lives in AndroidHost.cs -- the payload every Android head compiles in -- and everything
// here is PUBLIC, because that payload sees only WindowsBase's public surface.
//
// Android's model is a callback one: hand PrintManager an adapter, and it calls back asking for the
// document to be written to a file descriptor, possibly several times as the user changes paper.
// This port renders once and holds the bytes, because rendering walks a WPF visual tree and that has
// thread affinity -- the callback arrives on a binder thread, which is emphatically not the UI one.
//
// Like iOS, this head cannot show a modal dialog, so there is no dialog to show: the system print
// UI is what PrintManager.print puts up, and it appears at submission.
//

using System;
using System.IO;

namespace MS.Internal.Interop
{
    /// <summary>
    /// What the app head must provide for printing. A SIBLING of IAndroidHost rather than more
    /// members on it, so a head that does not print is not made to implement them.
    /// </summary>
    public interface IAndroidPrintHost
    {
        /// <summary>
        /// Hands a rendered PDF to the system print UI. Returns false when the platform refused to
        /// present it at all; a job the USER then cancels is not a failure and is not reported here.
        /// </summary>
        bool Print(string jobName, byte[] document, double pageWidth, double pageHeight);
    }

    /// <summary>
    /// The printing backend for Android. Public for the head payload; everything it is handed is a
    /// primitive, so the payload never has to see a WindowsBase-internal type.
    /// </summary>
    // No [SupportedOSPlatform], matching AndroidWindow and AndroidAccessibility next door: the public
    // WindowsBase surface carries no platform attributes, and adding one fails ApiCompat against the
    // hand-written reference assembly.
    public static class AndroidPrint
    {
        /// <summary>Set by the head alongside AndroidWindow.Host.</summary>
        public static IAndroidPrintHost Host { get; set; }
    }
}

namespace MS.Internal.Interop
{
    internal sealed class AndroidPrintBackend : IPrintBackend
    {
        /// <summary>
        /// Android discovers printers inside its own print UI, and there is no API to enumerate them
        /// beforehand. One stand-in entry so that a PrintQueue exists at all -- without one, nothing
        /// upstream can reach Write.
        /// </summary>
        public PrinterInfo[] EnumeratePrinters()
        {
            if (AndroidPrint.Host == null) return Array.Empty<PrinterInfo>();

            return new[]
            {
                new PrinterInfo
                {
                    Name = "Android",
                    DisplayName = "Android print",
                    IsDefault = true,
                    PageWidth = 816,
                    PageHeight = 1056,
                },
            };
        }

        /// <summary>Nothing to show: PrintManager puts the system UI up at submission.</summary>
        public bool ShowPrintUI(PrintJobSettings settings) => true;

        public bool Submit(string jobName, Stream document, PrintJobSettings settings)
        {
            IAndroidPrintHost host = AndroidPrint.Host;
            if (host == null) return false;

            byte[] bytes;
            using (var buffer = new MemoryStream())
            {
                document.CopyTo(buffer);
                bytes = buffer.ToArray();
            }

            if (bytes.Length == 0) return false;

            double width = settings?.PageWidth > 0 ? settings.PageWidth : 816;
            double height = settings?.PageHeight > 0 ? settings.PageHeight : 1056;

            try
            {
                return host.Print(jobName ?? "WPF document", bytes, width, height);
            }
            catch (InvalidOperationException)
            {
                // The activity went away between rendering and presenting.
                return false;
            }
        }
    }
}
