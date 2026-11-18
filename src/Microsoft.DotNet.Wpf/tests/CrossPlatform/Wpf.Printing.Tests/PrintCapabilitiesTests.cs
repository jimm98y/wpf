// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A printer's capabilities off Windows, as the print system describes them (CUPS, on Linux and macOS), read through
// PrintQueue.GetPrintCapabilities the way an application reads a driver's: its sizes, duplex, colour and copies.
// Needs a printer on the machine; skipped on one without.
//

using System;
using System.Linq;
using System.Printing;
using MS.Internal.Interop;
using Xunit;

namespace Wpf.Printing.Tests
{
    [Collection("Printing")]
    public class PrintCapabilitiesTests
    {
        [Fact]
        public void A_printer_s_capabilities_are_the_print_system_s()
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' drivers answer for themselves");
            PlatformPrint.Current = null;
            PrintQueue queue = new LocalPrintServer().GetPrintQueues().FirstOrDefault();
            Assert.SkipWhen(queue == null, "no printer on this machine");

            PrintCapabilities caps = queue.GetPrintCapabilities();
            Assert.NotNull(caps);
            Assert.NotEmpty(caps.PageMediaSizeCapability);
            Assert.All(caps.PageMediaSizeCapability, m => Assert.True(m.Width > 0 && m.Height > 0));
            Assert.Contains(Duplexing.OneSided, caps.DuplexingCapability);
            Assert.NotEmpty(caps.OutputColorCapability);
            Assert.True(caps.MaxCopyCount >= 1);
            Assert.Contains(PageOrientation.Landscape, caps.PageOrientationCapability);
            Assert.NotNull(caps.PageImageableArea);
        }
    }
}
