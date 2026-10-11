// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The real print backend for whichever platform the tests are running on.
//
// Only enumeration is exercised. Submission is deliberately never called: it would put paper through
// a printer, and a test suite that does that is a test suite people stop running. What submission
// does is covered instead by PrintQueueTests through a stand-in backend, which can assert on the
// bytes it was handed -- something a real printer cannot.
//
// Everything here skips on a platform without a backend, so the suite stays green on all six heads
// and actually says something on the one it is running on.
//

using System;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Printing.Tests
{
    [Collection("Printing")]
    public class PlatformBackendTests
    {
        [Fact]
        public void ThePlatformHasABackendOrHonestlyHasNone()
        {
            // Not an assertion about this machine so much as about the cascade: it must resolve to
            // exactly one answer and must not throw doing it. The ordering inside is load-bearing --
            // iOS reports as macOS and Android as Linux -- so a mistake there shows up as the wrong
            // backend rather than as no backend.
            IPrintBackend backend = PlatformPrint.Current;

            if (OperatingSystem.IsMacOS() && !OperatingSystem.IsIOS())
            {
                Assert.NotNull(backend);
            }
        }

        [Fact]
        public void EnumeratingPrintersDoesNotThrowAndAnswersSomething()
        {
            Assert.SkipUnless(PlatformPrint.Current != null, "no print backend on this platform");

            PrinterInfo[] printers = PlatformPrint.EnumeratePrinters();

            // An empty machine is a perfectly good answer. What matters is that asking is safe:
            // this runs from PrintDialog's constructor path, so throwing here takes out any window
            // that merely has a print button on it.
            Assert.NotNull(printers);

            foreach (PrinterInfo printer in printers)
            {
                Assert.False(string.IsNullOrEmpty(printer.Name), "a printer with no name cannot be submitted to");
                Assert.False(string.IsNullOrEmpty(printer.DisplayName), "a printer with no display name shows as blank");
            }
        }

        [Fact]
        public void AtMostOnePrinterIsTheDefault()
        {
            Assert.SkipUnless(PlatformPrint.Current != null, "no print backend on this platform");

            int defaults = 0;
            foreach (PrinterInfo printer in PlatformPrint.EnumeratePrinters())
            {
                if (printer.IsDefault) defaults++;
            }

            Assert.True(defaults <= 1, $"{defaults} printers claim to be the default");
        }

        [Fact]
        public void TheDefaultIsOneOfTheEnumeratedPrinters()
        {
            Assert.SkipUnless(PlatformPrint.Current != null, "no print backend on this platform");

            PrinterInfo[] printers = PlatformPrint.EnumeratePrinters();
            Assert.SkipWhen(printers.Length == 0, "no printers configured on this machine");

            // The default has to be a printer that was listed. Reporting one that is not in the list
            // is how PrintDialog ends up with a queue nothing can be submitted to -- and the two come
            // from different APIs on every platform, so it is a real thing to get wrong.
            bool found = false;
            foreach (PrinterInfo printer in printers)
            {
                if (printer.IsDefault) found = true;
            }

            Assert.True(found || printers.Length == 0,
                        "printers were enumerated but none of them is the default");
        }

        [Fact]
        public void EnumerationIsStable()
        {
            Assert.SkipUnless(PlatformPrint.Current != null, "no print backend on this platform");

            PrinterInfo[] first = PlatformPrint.EnumeratePrinters();
            PrinterInfo[] second = PlatformPrint.EnumeratePrinters();

            Assert.Equal(first.Length, second.Length);

            for (int i = 0; i < first.Length; i++)
            {
                Assert.Equal(first[i].Name, second[i].Name);
            }
        }
    }
}
