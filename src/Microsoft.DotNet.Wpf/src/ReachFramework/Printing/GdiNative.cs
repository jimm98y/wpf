// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The Win32 drawing and spooling calls the GDI device needs, and the DEVMODE handling around them.
//
// Plain P/Invoke into gdi32 and winspool. Nothing here is COM, which is the point: the assembly
// this replaces was C++/CLI and the XPS route next to it was IXpsOMPackageWriter, and neither is
// available to a port that has to compile on five other platforms.
//
// DEVMODE is the awkward part and gets more room than the drawing calls. It is a variable-length
// struct whose tail belongs to the driver, so it can only be obtained from the driver and must be
// passed back whole; the fixed head is public and is where paper size, orientation and copy count
// live. Every printer setting WPF can express goes through those few fields.
//

using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace System.Windows.Xps.Printing
{
    [SupportedOSPlatform("windows")]
    internal static class GdiNative
    {
        // GetDeviceCaps indices.
        internal const int HORZRES = 8;
        internal const int VERTRES = 10;
        internal const int LOGPIXELSX = 88;
        internal const int LOGPIXELSY = 90;
        internal const int PHYSICALWIDTH = 110;
        internal const int PHYSICALHEIGHT = 111;
        internal const int PHYSICALOFFSETX = 112;
        internal const int PHYSICALOFFSETY = 113;

        internal const int ALTERNATE = 1;
        internal const int WINDING = 2;

        internal const int RGN_AND = 1;

        internal const int HALFTONE = 4;

        internal const int BI_RGB = 0;
        internal const int DIB_RGB_COLORS = 0;
        internal const int SRCCOPY = 0x00CC0020;

        internal const int TRANSPARENT = 1;
        internal const uint ETO_GLYPH_INDEX = 0x0010;

        // DocumentProperties modes.
        internal const int DM_OUT_BUFFER = 2;
        internal const int DM_IN_BUFFER = 8;

        // DEVMODE dmFields bits.
        internal const uint DM_ORIENTATION = 0x00000001;
        internal const uint DM_PAPERSIZE = 0x00000002;
        internal const uint DM_PAPERLENGTH = 0x00000004;
        internal const uint DM_PAPERWIDTH = 0x00000008;
        internal const uint DM_COPIES = 0x00000100;

        internal const short DMPAPER_USER = 256;
        internal const short DMORIENT_PORTRAIT = 1;
        internal const short DMORIENT_LANDSCAPE = 2;

        internal const int CCHDEVICENAME = 32;
        internal const int CCHFORMNAME = 32;

        /// <summary>
        /// The public head of a DEVMODE. The driver's private tail follows it in memory and is
        /// never touched here -- which is why every DEVMODE this code hands to GDI is one the
        /// driver produced, copied and edited in place, rather than one built from nothing.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
            internal string dmDeviceName;
            internal ushort dmSpecVersion;
            internal ushort dmDriverVersion;
            internal ushort dmSize;
            internal ushort dmDriverExtra;
            internal uint dmFields;
            internal short dmOrientation;
            internal short dmPaperSize;
            internal short dmPaperLength;
            internal short dmPaperWidth;
            internal short dmScale;
            internal short dmCopies;
            internal short dmDefaultSource;
            internal short dmPrintQuality;
            internal short dmColor;
            internal short dmDuplex;
            internal short dmYResolution;
            internal short dmTTOption;
            internal short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
            internal string dmFormName;
            internal ushort dmLogPixels;
            internal uint dmBitsPerPel;
            internal uint dmPelsWidth;
            internal uint dmPelsHeight;
            internal uint dmDisplayFlags;
            internal uint dmDisplayFrequency;
            internal uint dmICMMethod;
            internal uint dmICMIntent;
            internal uint dmMediaType;
            internal uint dmDitherType;
            internal uint dmReserved1;
            internal uint dmReserved2;
            internal uint dmPanningWidth;
            internal uint dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DOCINFO
        {
            internal int cbSize;
            internal string lpszDocName;
            internal string lpszOutput;
            internal string lpszDatatype;
            internal int fwType;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            internal int x;
            internal int y;

            internal POINT(int x, int y)
            {
                this.x = x;
                this.y = y;
            }
        }

        internal const int LF_FACESIZE = 32;

        internal const uint DEFAULT_CHARSET = 1;
        internal const uint OUT_TT_ONLY_PRECIS = 7;
        internal const uint CLIP_DEFAULT_PRECIS = 0;
        internal const uint ANTIALIASED_QUALITY = 4;
        internal const uint DEFAULT_PITCH = 0;

        internal const uint FR_PRIVATE = 0x10;

        internal const uint TA_LEFT = 0;
        internal const uint TA_BASELINE = 24;

        /// <summary>'maxp', big-endian, which is how GetFontData names a table.</summary>
        internal const uint TableMaxp = 0x7078616D;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct LOGFONT
        {
            internal int lfHeight;
            internal int lfWidth;
            internal int lfEscapement;
            internal int lfOrientation;
            internal int lfWeight;
            internal byte lfItalic;
            internal byte lfUnderline;
            internal byte lfStrikeOut;
            internal byte lfCharSet;
            internal byte lfOutPrecision;
            internal byte lfClipPrecision;
            internal byte lfQuality;
            internal byte lfPitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LF_FACESIZE)]
            internal string lfFaceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct XFORM
        {
            internal float eM11;
            internal float eM12;
            internal float eM21;
            internal float eM22;
            internal float eDx;
            internal float eDy;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct BITMAPINFOHEADER
        {
            internal uint biSize;
            internal int biWidth;
            internal int biHeight;
            internal ushort biPlanes;
            internal ushort biBitCount;
            internal uint biCompression;
            internal uint biSizeImage;
            internal int biXPelsPerMeter;
            internal int biYPelsPerMeter;
            internal uint biClrUsed;
            internal uint biClrImportant;
        }

        // ---- device contexts and documents ------------------------------------------

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateDCW")]
        internal static extern IntPtr CreateDC(string driver, string device, string port, IntPtr devMode);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ResetDCW")]
        internal static extern IntPtr ResetDC(IntPtr dc, IntPtr devMode);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern int GetDeviceCaps(IntPtr dc, int index);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "StartDocW")]
        internal static extern int StartDoc(IntPtr dc, ref DOCINFO info);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern int EndDoc(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern int AbortDoc(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern int StartPage(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern int EndPage(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern int SaveDC(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool RestoreDC(IntPtr dc, int state);

        // ---- paths ------------------------------------------------------------------

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool BeginPath(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool EndPath(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool AbortPath(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool FillPath(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool CloseFigure(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool SelectClipPath(IntPtr dc, int mode);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool MoveToEx(IntPtr dc, int x, int y, IntPtr previous);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool PolylineTo(IntPtr dc, POINT[] points, int count);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern int SetPolyFillMode(IntPtr dc, int mode);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool IntersectClipRect(IntPtr dc, int left, int top, int right, int bottom);

        // ---- world transform --------------------------------------------------------
        //
        // Used only where a matrix cannot be folded into the coordinates first: an image is placed
        // by a rectangle, so a rotation or a skew has nowhere else to go. Everything else -- paths,
        // glyph outlines -- is transformed in managed code and reaches GDI as device coordinates,
        // which keeps rounding at the last possible moment and out of a float matrix.

        internal const int GM_ADVANCED = 2;

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern int SetGraphicsMode(IntPtr dc, int mode);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool SetWorldTransform(IntPtr dc, ref XFORM transform);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool ModifyWorldTransform(IntPtr dc, IntPtr transform, int mode);

        // ---- objects ----------------------------------------------------------------

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr CreateSolidBrush(int color);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern IntPtr GetStockObject(int index);

        // ---- images -----------------------------------------------------------------

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern int SetStretchBltMode(IntPtr dc, int mode);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern int SetBkMode(IntPtr dc, int mode);

        // ---- text -------------------------------------------------------------------

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
                   EntryPoint = "CreateFontIndirectW")]
        internal static extern IntPtr CreateFontIndirect(ref LOGFONT font);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern int SetTextColor(IntPtr dc, int color);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern uint SetTextAlign(IntPtr dc, uint align);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
                   EntryPoint = "GetTextFaceW")]
        internal static extern int GetTextFace(IntPtr dc, int count, System.Text.StringBuilder faceName);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern uint GetFontData(IntPtr dc, uint table, uint offset, byte[] buffer, uint size);

        /// <summary>
        /// Draws glyphs by INDEX, which is the only way the shaping WPF already did survives.
        /// The string parameter carries glyph ids, not characters, when ETO_GLYPH_INDEX is set.
        /// </summary>
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ExtTextOutW")]
        internal static extern bool ExtTextOut(IntPtr dc, int x, int y, uint options, IntPtr rect,
                                               ushort[] text, int count, int[] dx);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ExtTextOutW")]
        internal static extern bool ExtTextOutString(IntPtr dc, int x, int y, uint options, IntPtr rect,
                                                     string text, int count, int[] dx);

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
                   EntryPoint = "AddFontResourceExW")]
        internal static extern int AddFontResourceEx(string file, uint flags, IntPtr reserved);

        [DllImport("gdi32.dll", SetLastError = true)]
        internal static extern int StretchDIBits(IntPtr dc, int xDest, int yDest, int destWidth, int destHeight,
                                                 int xSrc, int ySrc, int srcWidth, int srcHeight,
                                                 byte[] bits, ref BITMAPINFOHEADER info, uint usage, uint rop);

        // ---- the spooler ------------------------------------------------------------

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenPrinterW")]
        internal static extern bool OpenPrinter(string name, out IntPtr printer, IntPtr defaults);

        [DllImport("winspool.drv", SetLastError = true)]
        internal static extern bool ClosePrinter(IntPtr printer);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true,
                   EntryPoint = "DocumentPropertiesW")]
        internal static extern int DocumentProperties(IntPtr window, IntPtr printer, string device,
                                                      IntPtr output, IntPtr input, int mode);

        /// <summary>
        /// The printer's default DEVMODE with the job's paper and copy count applied, as bytes.
        ///
        /// Asked of the driver rather than built here, twice: once to get its defaults, and once
        /// more after the edits so the driver can validate them and fix up whatever the change
        /// implies. Skipping the second call is how you get a DEVMODE that says A4 and a driver
        /// that still prints Letter, because the two paper fields and the form name disagreed.
        ///
        /// Null when the printer cannot be opened or has no properties to offer, which callers
        /// pass straight to CreateDC as "use your own defaults".
        /// </summary>
        internal static byte[] BuildDevMode(string printerName, double pageWidth, double pageHeight, int copies)
        {
            if (string.IsNullOrEmpty(printerName)) return null;

            if (!OpenPrinter(printerName, out IntPtr printer, IntPtr.Zero) || printer == IntPtr.Zero)
            {
                return null;
            }

            IntPtr buffer = IntPtr.Zero;

            try
            {
                int size = DocumentProperties(IntPtr.Zero, printer, printerName, IntPtr.Zero, IntPtr.Zero, 0);
                if (size <= 0) return null;

                buffer = Marshal.AllocHGlobal(size);

                if (DocumentProperties(IntPtr.Zero, printer, printerName, buffer, IntPtr.Zero, DM_OUT_BUFFER) < 0)
                {
                    return null;
                }

                var mode = Marshal.PtrToStructure<DEVMODE>(buffer);

                if (pageWidth > 0 && pageHeight > 0)
                {
                    bool landscape = pageWidth > pageHeight;

                    // DEVMODE states paper in tenths of a millimetre, and always for the PORTRAIT
                    // sheet: a landscape job is a portrait sheet plus dmOrientation, not a sheet
                    // whose width and height were swapped. Sending swapped dimensions gets a page
                    // the right shape and the content rotated off it.
                    short width = (short)Math.Round(Math.Min(pageWidth, pageHeight) / 96.0 * 254.0);
                    short length = (short)Math.Round(Math.Max(pageWidth, pageHeight) / 96.0 * 254.0);

                    // A standard form if the driver has one this size, and a custom sheet only if
                    // it does not. Drivers treat their own forms better than DMPAPER_USER -- some
                    // reject a custom size outright, and those that accept one often lose the
                    // tray selection and the imageable area that came with the named form.
                    short form = MatchForm(printerName, width, length);

                    mode.dmPaperSize = form != 0 ? form : DMPAPER_USER;
                    mode.dmOrientation = landscape ? DMORIENT_LANDSCAPE : DMORIENT_PORTRAIT;
                    mode.dmFields |= DM_PAPERSIZE | DM_ORIENTATION;

                    if (form == 0)
                    {
                        mode.dmPaperWidth = width;
                        mode.dmPaperLength = length;
                        mode.dmFields |= DM_PAPERWIDTH | DM_PAPERLENGTH;
                    }
                }

                if (copies > 1)
                {
                    mode.dmCopies = (short)Math.Min(copies, short.MaxValue);
                    mode.dmFields |= DM_COPIES;
                }

                Marshal.StructureToPtr(mode, buffer, false);

                // Back to the driver to validate. It may return a different size than it asked for
                // the first time, but never a larger one, so the buffer is still big enough.
                if (DocumentProperties(IntPtr.Zero, printer, printerName, buffer, buffer,
                                       DM_IN_BUFFER | DM_OUT_BUFFER) < 0)
                {
                    return null;
                }

                var validated = Marshal.PtrToStructure<DEVMODE>(buffer);
                int total = validated.dmSize + validated.dmDriverExtra;
                if (total <= 0 || total > size) total = size;

                var bytes = new byte[total];
                Marshal.Copy(buffer, bytes, 0, total);
                return bytes;
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                ClosePrinter(printer);
            }
        }

        /// <summary>
        /// The driver's own form id for a sheet this size, or zero if it has none.
        ///
        /// The two capability lists are parallel: DC_PAPERS gives the form ids and DC_PAPERSIZE
        /// gives their dimensions in tenths of a millimetre, in the same order. A millimetre of
        /// tolerance because the same paper is described slightly differently by different
        /// drivers -- A4 turns up as both 2100x2970 and 2099x2969 -- and a millimetre is far
        /// smaller than the gap between any two standard sizes.
        /// </summary>
        private static short MatchForm(string printerName, short width, short length)
        {
            IntPtr sizes = IntPtr.Zero;
            IntPtr ids = IntPtr.Zero;

            try
            {
                int count = DeviceCapabilities(printerName, null, DC_PAPERSIZE, IntPtr.Zero, IntPtr.Zero);
                if (count <= 0) return 0;

                // DC_PAPERSIZE is a POINT per form (two 32-bit longs); DC_PAPERS is a WORD per form.
                sizes = Marshal.AllocHGlobal(count * 8);
                ids = Marshal.AllocHGlobal(count * 2);

                if (DeviceCapabilities(printerName, null, DC_PAPERSIZE, sizes, IntPtr.Zero) != count) return 0;
                if (DeviceCapabilities(printerName, null, DC_PAPERS, ids, IntPtr.Zero) != count) return 0;

                for (int i = 0; i < count; i++)
                {
                    int formWidth = Marshal.ReadInt32(sizes, i * 8);
                    int formLength = Marshal.ReadInt32(sizes, i * 8 + 4);

                    if (Math.Abs(formWidth - width) <= 10 && Math.Abs(formLength - length) <= 10)
                    {
                        return Marshal.ReadInt16(ids, i * 2);
                    }
                }

                return 0;
            }
            catch (DllNotFoundException)
            {
                return 0;
            }
            catch (EntryPointNotFoundException)
            {
                return 0;
            }
            finally
            {
                if (sizes != IntPtr.Zero) Marshal.FreeHGlobal(sizes);
                if (ids != IntPtr.Zero) Marshal.FreeHGlobal(ids);
            }
        }

        private const int DC_PAPERS = 2;
        private const int DC_PAPERSIZE = 3;

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true,
                   EntryPoint = "DeviceCapabilitiesW")]
        private static extern int DeviceCapabilities(string device, string port, int capability,
                                                     IntPtr output, IntPtr devMode);
    }
}
