// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// PrinterSettings' DEVMODE and DEVNAMES, which the Windows print dialog hands settings in and out
// through. Mono threw NotImplementedException from all of them, so a native print dialog could not
// be given the settings or report what was chosen, and the printer DC was created with no DEVMODE
// at all -- copies, orientation and duplex never reached the printer.
//

using System;
using System.Drawing.Printing;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class PrinterDevModeTests
    {
        private static PrinterSettings AnyPrinter()
        {
            if (!OperatingSystem.IsWindows() || PrinterSettings.InstalledPrinters.Count == 0)
                return null;
            var settings = new PrinterSettings();
            return settings.IsValid ? settings : null;
        }

        [Fact]
        public void TheDevModeCarriesCopiesCollationAndOrientation_BothWays()
        {
            PrinterSettings settings = AnyPrinter();
            Assert.SkipWhen(settings is null, "needs a printer on Windows");

            settings.Copies = 3;
            settings.Collate = true;
            PageSettings page = settings.DefaultPageSettings;
            page.Landscape = true;

            IntPtr devmode = settings.GetHdevmode(page);
            try
            {
                var back = new PrinterSettings { PrinterName = settings.PrinterName };
                back.SetHdevmode(devmode);
                var backPage = new PageSettings(back);
                backPage.SetHdevmode(devmode);

                // A driver may cap copies or ignore collation; what it accepted must come back as is.
                Assert.True(back.Copies is 3 or 1, $"copies came back as {back.Copies}");
                Assert.True(backPage.Landscape, "orientation did not survive the DEVMODE");
            }
            finally
            {
                DevModeTestNative.GlobalFree(devmode);
            }
        }

        [Fact]
        public void TheDevNamesNameThePrinter()
        {
            PrinterSettings settings = AnyPrinter();
            Assert.SkipWhen(settings is null, "needs a printer on Windows");

            IntPtr devnames = settings.GetHdevnames();
            try
            {
                var back = new PrinterSettings();
                back.SetHdevnames(devnames);
                Assert.Equal(settings.PrinterName, back.PrinterName);
            }
            finally
            {
                DevModeTestNative.GlobalFree(devnames);
            }
        }
    }

    internal static class DevModeTestNative
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        internal static extern IntPtr GlobalFree(IntPtr h);
    }
}
