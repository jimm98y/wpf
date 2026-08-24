// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Windows print system, through the spooler.
//
// This is the sixth backend and the odd one out, because the Windows print system is not shaped
// like the other five. CUPS, macOS, iOS, Android and the browser all take a finished document and
// render it themselves; Windows takes a device context and expects the application to draw. That
// difference is what TakesRenderedDocument reports, and the drawing half lives where the drawing
// code is -- GdiDevice in ReachFramework -- not here. What is here is everything that does not
// need a visual tree: which printers exist, how big their paper is, and how to put bytes on a port.
//
// Plain P/Invoke to winspool and gdi32, no COM. The C++/CLI System.Printing this replaces reached
// the same three APIs; it just did it from a mixed-mode assembly that only builds on Windows.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MS.Internal.Interop
{
    [SupportedOSPlatform("windows")]
    internal sealed class WindowsPrint : IPrintBackend
    {
        /// <summary>
        /// A job reaches the Windows spooler by drawing into a device context, not by handing over
        /// a document. See <see cref="Submit"/> for what remains of the document route.
        /// </summary>
        public bool TakesRenderedDocument => false;

        public PrinterInfo[] EnumeratePrinters()
        {
            string defaultName = GetDefaultPrinterName();
            var printers = new List<PrinterInfo>();

            foreach (Native.PRINTER_INFO_2 p in EnumerateRaw())
            {
                if (string.IsNullOrEmpty(p.pPrinterName)) continue;

                var info = new PrinterInfo
                {
                    Name = p.pPrinterName,

                    // The queue name again, NOT the driver's comment. PrintQueue.FullName reads
                    // this, and the Windows print dialog matches what PrintDlgEx returned against
                    // FullName to find the queue the user picked -- so a friendlier string here
                    // means the dialog silently fails to find any printer at all.
                    DisplayName = p.pPrinterName,
                    Location = p.pLocation,
                    IsDefault = string.Equals(p.pPrinterName, defaultName, StringComparison.OrdinalIgnoreCase),
                };

                Measure(info);
                printers.Add(info);
            }

            // A machine with printers but no default is normal -- it is what a fresh install of a
            // second printer looks like until someone chooses. Naming one keeps every caller that
            // asks for "the" printer working, and the first enumerated is the spooler's own order.
            if (printers.Count != 0 && defaultName == null) printers[0].IsDefault = true;

            return printers.ToArray();
        }

        /// <summary>
        /// Windows shows no dialog from here.
        ///
        /// It has a good one -- PrintDlgEx, with the driver's own property pages behind it -- and
        /// PrintDialog.ShowDialog has always called it directly on this head. Putting a second,
        /// lesser dialog behind this method would mean the platform with the best print UI was the
        /// one that did not use it. True means "nothing was cancelled; carry on".
        /// </summary>
        public bool ShowPrintUI(PrintJobSettings settings) => true;

        /// <summary>
        /// Spools a stream of printer-ready bytes.
        ///
        /// Narrower than the same method on the other five backends, and deliberately so: this is
        /// the RAW datatype, which means the spooler passes the bytes to the port untouched. It is
        /// right for data a driver produced -- a PostScript or PCL file, or the output of a
        /// previous job -- and wrong for anything else. It is NOT how WPF content prints; that
        /// draws through GdiDevice, which is what TakesRenderedDocument returning false says.
        ///
        /// Feeding it a PDF would spool a PDF to a printer that does not read PDF, so a caller who
        /// arrived here holding one has taken a wrong turn several frames up.
        /// </summary>
        public bool Submit(string jobName, Stream document, PrintJobSettings settings)
        {
            if (document == null || string.IsNullOrEmpty(settings?.PrinterName)) return false;

            if (!Native.OpenPrinter(settings.PrinterName, out IntPtr printer, IntPtr.Zero) ||
                printer == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var doc = new Native.DOC_INFO_1
                {
                    pDocName = string.IsNullOrEmpty(jobName) ? "WPF document" : jobName,
                    pOutputFile = settings.OutputFile,
                    pDatatype = "RAW",
                };

                // Copies are the spooler's job, not the caller's: one spooled document printed n
                // times beats n copies of the bytes, and it is what the driver's collation expects.
                int copies = Math.Max(1, settings.Copies);

                for (int copy = 0; copy < copies; copy++)
                {
                    if (Native.StartDocPrinter(printer, 1, ref doc) == 0) return false;

                    try
                    {
                        if (!Native.StartPagePrinter(printer)) return false;

                        document.Position = 0;
                        if (!Copy(document, printer)) return false;

                        Native.EndPagePrinter(printer);
                    }
                    finally
                    {
                        Native.EndDocPrinter(printer);
                    }
                }

                return true;
            }
            finally
            {
                Native.ClosePrinter(printer);
            }
        }

        private static bool Copy(Stream source, IntPtr printer)
        {
            byte[] buffer = new byte[64 * 1024];
            IntPtr native = Marshal.AllocHGlobal(buffer.Length);

            try
            {
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    Marshal.Copy(buffer, 0, native, read);

                    if (!Native.WritePrinter(printer, native, read, out int written) || written != read)
                    {
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(native);
            }
        }

        // ---- what the driver says about the paper -----------------------------------

        /// <summary>
        /// Fills in page and imageable geometry by asking the driver for an information context.
        ///
        /// An IC rather than a DC: it answers GetDeviceCaps identically and creates nothing the
        /// spooler has to clean up, which matters when this runs for every printer on the machine
        /// every time a print dialog opens.
        ///
        /// Failure is silent and leaves the sizes at zero, which callers already read as "unknown".
        /// A printer that is offline, or a network queue whose driver is not installed locally, is
        /// a normal thing to find in an enumeration and not a reason to fail the whole list.
        /// </summary>
        private static void Measure(PrinterInfo info)
        {
            IntPtr ic = IntPtr.Zero;

            try
            {
                ic = Native.CreateIC("WINSPOOL", info.Name, null, IntPtr.Zero);
                if (ic == IntPtr.Zero) return;

                double dpiX = Native.GetDeviceCaps(ic, Native.LOGPIXELSX);
                double dpiY = Native.GetDeviceCaps(ic, Native.LOGPIXELSY);
                if (dpiX <= 0 || dpiY <= 0) return;

                double ToWpfX(int px) => px * 96.0 / dpiX;
                double ToWpfY(int px) => px * 96.0 / dpiY;

                info.PageWidth = ToWpfX(Native.GetDeviceCaps(ic, Native.PHYSICALWIDTH));
                info.PageHeight = ToWpfY(Native.GetDeviceCaps(ic, Native.PHYSICALHEIGHT));
                info.ImageableOriginX = ToWpfX(Native.GetDeviceCaps(ic, Native.PHYSICALOFFSETX));
                info.ImageableOriginY = ToWpfY(Native.GetDeviceCaps(ic, Native.PHYSICALOFFSETY));
                info.ImageableWidth = ToWpfX(Native.GetDeviceCaps(ic, Native.HORZRES));
                info.ImageableHeight = ToWpfY(Native.GetDeviceCaps(ic, Native.VERTRES));
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
            finally
            {
                if (ic != IntPtr.Zero) Native.DeleteDC(ic);
            }
        }

        internal static string GetDefaultPrinterName()
        {
            uint length = 0;
            Native.GetDefaultPrinter(null, ref length);

            if (length == 0) return null;

            var name = new StringBuilder((int)length);
            return Native.GetDefaultPrinter(name, ref length) ? name.ToString() : null;
        }

        private static IEnumerable<Native.PRINTER_INFO_2> EnumerateRaw()
        {
            const uint LocalAndConnections = Native.PRINTER_ENUM_LOCAL | Native.PRINTER_ENUM_CONNECTIONS;

            Native.EnumPrinters(LocalAndConnections, null, 2, IntPtr.Zero, 0, out uint needed, out _);
            if (needed == 0) yield break;

            IntPtr buffer = Marshal.AllocHGlobal((int)needed);

            try
            {
                if (!Native.EnumPrinters(LocalAndConnections, null, 2, buffer, needed, out _, out uint count))
                {
                    yield break;
                }

                int stride = Marshal.SizeOf<Native.PRINTER_INFO_2>();

                for (int i = 0; i < count; i++)
                {
                    yield return Marshal.PtrToStructure<Native.PRINTER_INFO_2>(buffer + i * stride);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static class Native
        {
            internal const uint PRINTER_ENUM_LOCAL = 0x02;
            internal const uint PRINTER_ENUM_CONNECTIONS = 0x04;

            internal const int HORZRES = 8;
            internal const int VERTRES = 10;
            internal const int LOGPIXELSX = 88;
            internal const int LOGPIXELSY = 90;
            internal const int PHYSICALWIDTH = 110;
            internal const int PHYSICALHEIGHT = 111;
            internal const int PHYSICALOFFSETX = 112;
            internal const int PHYSICALOFFSETY = 113;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct PRINTER_INFO_2
            {
                internal string pServerName;
                internal string pPrinterName;
                internal string pShareName;
                internal string pPortName;
                internal string pDriverName;
                internal string pComment;
                internal string pLocation;
                internal IntPtr pDevMode;
                internal string pSepFile;
                internal string pPrintProcessor;
                internal string pDatatype;
                internal string pParameters;
                internal IntPtr pSecurityDescriptor;
                internal uint Attributes;
                internal uint Priority;
                internal uint DefaultPriority;
                internal uint StartTime;
                internal uint UntilTime;
                internal uint Status;
                internal uint cJobs;
                internal uint AveragePPM;
            }

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            internal struct DOC_INFO_1
            {
                internal string pDocName;
                internal string pOutputFile;
                internal string pDatatype;
            }

            [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true,
                       EntryPoint = "EnumPrintersW")]
            internal static extern bool EnumPrinters(uint flags, string name, uint level, IntPtr buffer,
                                                     uint size, out uint needed, out uint returned);

            [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true,
                       EntryPoint = "GetDefaultPrinterW")]
            internal static extern bool GetDefaultPrinter(StringBuilder name, ref uint size);

            [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true,
                       EntryPoint = "OpenPrinterW")]
            internal static extern bool OpenPrinter(string name, out IntPtr printer, IntPtr defaults);

            [DllImport("winspool.drv", SetLastError = true)]
            internal static extern bool ClosePrinter(IntPtr printer);

            [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true,
                       EntryPoint = "StartDocPrinterW")]
            internal static extern int StartDocPrinter(IntPtr printer, int level, ref DOC_INFO_1 info);

            [DllImport("winspool.drv", SetLastError = true)]
            internal static extern bool EndDocPrinter(IntPtr printer);

            [DllImport("winspool.drv", SetLastError = true)]
            internal static extern bool StartPagePrinter(IntPtr printer);

            [DllImport("winspool.drv", SetLastError = true)]
            internal static extern bool EndPagePrinter(IntPtr printer);

            [DllImport("winspool.drv", SetLastError = true)]
            internal static extern bool WritePrinter(IntPtr printer, IntPtr buffer, int count, out int written);

            [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateICW")]
            internal static extern IntPtr CreateIC(string driver, string device, string port, IntPtr devMode);

            [DllImport("gdi32.dll", SetLastError = true)]
            internal static extern bool DeleteDC(IntPtr dc);

            [DllImport("gdi32.dll", SetLastError = true)]
            internal static extern int GetDeviceCaps(IntPtr dc, int index);
        }
    }
}
