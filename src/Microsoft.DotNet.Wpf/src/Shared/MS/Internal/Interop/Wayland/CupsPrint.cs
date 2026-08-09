// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Printing on Linux: CUPS.
//
// CUPS has taken PDF as its standard print format since 1.6, so a rendered document goes to the
// spooler as it is and the printer's own filter chain does the rest. That is the whole of
// submission: cupsPrintFile with a path and a title.
//
// What is deliberately NOT used here is the XDG desktop portal. org.freedesktop.portal.Print would
// give a sandboxed application the desktop's own print dialog, which is the better answer in a
// flatpak -- but it passes the document as a UnixFD, and this port's D-Bus binding marshals only
// booleans, uint32s and strings. Adding file-descriptor passing to DBusLite is a bigger and riskier
// change than talking to CUPS directly, and CUPS is what the portal talks to anyway.
//
// The print DIALOG is therefore ours to draw, and it is drawn in WPF (see ManagedPrintDialog).
// That works on this head for the same reason the managed message box does: Linux keeps a blocking
// dispatcher loop, so a nested modal frame is possible here.
//
// libcups is dlopen'd through the ordinary DllImport resolver, so a machine without it fails at the
// first call rather than at load. That is caught and reported as "no printers", which is what a
// machine with no print system genuinely looks like.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MS.Internal.Interop.Wayland
{
    [SupportedOSPlatform("linux")]
    internal sealed class CupsPrint : IPrintBackend
    {
        public PrinterInfo[] EnumeratePrinters()
        {
            IntPtr dests = IntPtr.Zero;

            try
            {
                int count = cupsGetDests(ref dests);
                if (count <= 0 || dests == IntPtr.Zero) return Array.Empty<PrinterInfo>();

                var printers = new List<PrinterInfo>(count);
                int size = Marshal.SizeOf<CupsDest>();

                for (int i = 0; i < count; i++)
                {
                    var dest = Marshal.PtrToStructure<CupsDest>(dests + i * size);

                    string name = Utf8(dest.name);
                    if (string.IsNullOrEmpty(name)) continue;

                    // An "instance" is a saved set of options on a queue -- "office/duplex". The
                    // job has to be addressed to the full name or the options are dropped.
                    string instance = Utf8(dest.instance);
                    string full = string.IsNullOrEmpty(instance) ? name : name + "/" + instance;

                    printers.Add(new PrinterInfo
                    {
                        Name = full,
                        DisplayName = DisplayName(dest, full),
                        Location = Option(dest, "printer-location"),
                        IsDefault = dest.is_default != 0,
                    });
                }

                return printers.ToArray();
            }
            catch (DllNotFoundException)
            {
                // No CUPS on this machine. Indistinguishable, correctly, from no printers.
                return Array.Empty<PrinterInfo>();
            }
            catch (EntryPointNotFoundException)
            {
                return Array.Empty<PrinterInfo>();
            }
            finally
            {
                if (dests != IntPtr.Zero)
                {
                    try { cupsFreeDests(int.MaxValue, dests); }
                    catch (DllNotFoundException) { }
                }
            }
        }

        /// <summary>
        /// The name a chooser should show: CUPS' own description when the queue has one, which is
        /// the human-readable string an administrator set ("Reception laser"), not the queue id.
        /// </summary>
        private static string DisplayName(CupsDest dest, string fallback)
        {
            string info = Option(dest, "printer-info");
            return string.IsNullOrEmpty(info) ? fallback : info;
        }

        private static string Option(CupsDest dest, string key)
        {
            if (dest.options == IntPtr.Zero || dest.num_options <= 0) return null;

            int size = Marshal.SizeOf<CupsOption>();

            for (int i = 0; i < dest.num_options; i++)
            {
                var option = Marshal.PtrToStructure<CupsOption>(dest.options + i * size);
                if (string.Equals(Utf8(option.name), key, StringComparison.Ordinal))
                {
                    return Utf8(option.value);
                }
            }

            return null;
        }

        /// <summary>
        /// There is no system print dialog to show. The desktop's own belongs to the portal, which
        /// this backend does not use, so the WPF-drawn one is shown by the layer above.
        /// </summary>
        public bool ShowPrintUI(PrintJobSettings settings) => true;

        public bool Submit(string jobName, Stream document, PrintJobSettings settings)
        {
            string printer = settings?.PrinterName;
            if (string.IsNullOrEmpty(printer)) return false;

            // cupsPrintFile takes a path, and the spooler reads it after this call returns, so the
            // file cannot be deleted here. CUPS copies it into its own spool directory before
            // returning a job id, so deleting immediately after is safe -- but only after.
            string path = Path.Combine(Path.GetTempPath(), "wpf-cups-" + Guid.NewGuid().ToString("N") + ".pdf");

            try
            {
                using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    document.CopyTo(file);
                }

                int job = cupsPrintFile(printer, path, jobName ?? "WPF document", 0, IntPtr.Zero);

                // A job id of zero means the spooler refused it.
                return job != 0;
            }
            catch (DllNotFoundException) { return false; }
            catch (EntryPointNotFoundException) { return false; }
            catch (IOException) { return false; }
            finally
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static string Utf8(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

        // ---- libcups ----------------------------------------------------------------

        // cups_dest_t. Layout is ABI and has been stable across the whole 1.x and 2.x line.
        [StructLayout(LayoutKind.Sequential)]
        private struct CupsDest
        {
            internal IntPtr name;
            internal IntPtr instance;
            internal int is_default;
            internal int num_options;
            internal IntPtr options;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CupsOption
        {
            internal IntPtr name;
            internal IntPtr value;
        }

        [DllImport("libcups")]
        private static extern int cupsGetDests(ref IntPtr dests);

        [DllImport("libcups")]
        private static extern void cupsFreeDests(int numDests, IntPtr dests);

        [DllImport("libcups", CharSet = CharSet.Ansi)]
        private static extern int cupsPrintFile(string printer, string filename, string title,
                                                int numOptions, IntPtr options);
    }
}
