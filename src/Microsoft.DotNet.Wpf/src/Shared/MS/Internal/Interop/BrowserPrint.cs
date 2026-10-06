// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Printing in the browser.
//
// There is no print API for arbitrary data on the web. window.print() prints the DOCUMENT, and a
// WPF app's document is one canvas element, so calling it directly would print a screenshot of the
// window -- at screen resolution, clipped to the viewport, with none of the pagination the app just
// did.
//
// The way every web application actually solves this is to put the rendered PDF in a hidden iframe
// and print THAT: the iframe's own document is the PDF, so the browser's print preview shows the
// real pages at real resolution, and the browser's own dialog handles the printer, the range and
// the copies. That is what browser-print.js does.
//
// Two consequences worth stating, because neither is a bug to be fixed later:
//
//   * There is no printer enumeration. The browser will not tell a page what printers exist, for
//     good fingerprinting reasons, so this reports one stand-in entry meaning "the browser will
//     ask". PrintDialog on this head therefore goes straight to submission.
//   * There is no result. print() returns nothing and reports nothing; whether the user printed,
//     saved to PDF or cancelled is not observable. Submission answers "the preview was opened".
//

using System;
using System.IO;
using System.Runtime.Versioning;

namespace MS.Internal.Interop
{
    [SupportedOSPlatform("browser")]
    internal sealed class BrowserPrint : IPrintBackend
    {
        public PrinterInfo[] EnumeratePrinters()
        {
            return new[]
            {
                new PrinterInfo
                {
                    Name = "Browser",
                    DisplayName = "Print with the browser",
                    IsDefault = true,
                    PageWidth = 816,
                    PageHeight = 1056,
                },
            };
        }

        /// <summary>Nothing to show: the browser's print dialog appears when the preview opens.</summary>
        public bool ShowPrintUI(PrintJobSettings settings) => true;

        public bool Submit(string jobName, Stream document, PrintJobSettings settings)
        {
            byte[] bytes;
            using (var buffer = new MemoryStream())
            {
                document.CopyTo(buffer);
                bytes = buffer.ToArray();
            }

            if (bytes.Length == 0) return false;

            try
            {
                // The bytes cross as a byte[] rather than through the shared MemoryView buffer the
                // media backend uses: a document is written once and is megabytes when it carries an
                // embedded CJK font, so a caller-owned buffer would have to be grown to fit it and
                // then thrown away.
                return BrowserWindow.Js.PrintDocument(jobName ?? "WPF document", bytes);
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
            {
                // The page has no DOM to attach an iframe to, which happens when the app is running
                // headless or the host page has been torn down.
                return false;
            }
        }
    }
}
