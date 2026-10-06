// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Private.Windows.Ole;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using MS.Internal;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.System.Ole;
using Com = Windows.Win32.System.Com;
using HRESULT = Windows.Win32.Foundation.HRESULT;

namespace System.Windows.Ole;

internal sealed unsafe class WpfOleServices : IOleServices
{
    // Prevent instantiation
    private WpfOleServices() { }

    public static void EnsureThreadState() => OleServicesContext.EnsureThreadState();

    public static HRESULT GetDataHere(string format, object data, FORMATETC* pformatetc, STGMEDIUM* pmedium)
    {
        TYMED mediumType = (TYMED)pformatetc->tymed;

        // Handle bitmaps.
        if (mediumType.HasFlag(TYMED.TYMED_GDI)
            && format.Equals(DataFormatNames.Bitmap)
            && (SystemDrawingHelper.IsBitmap(data) || data is BitmapSource))
        {
            pmedium->u.hBitmap = GetCompatibleBitmap(data);
            return HRESULT.S_OK;
        }

        // Handle enhanced metafiles.
        if (mediumType.HasFlag(TYMED.TYMED_ENHMF) && format.Equals(DataFormatNames.Emf))
        {
            if (SystemDrawingHelper.IsMetafile(data))
            {
                pmedium->u.hEnhMetaFile = SystemDrawingHelper.GetHandleFromMetafile(data);
            }
            else if (data is MemoryStream memoryStream && memoryStream.GetBuffer() is { } buffer && buffer.Length != 0)
            {
                HENHMETAFILE hemf = PInvoke.SetEnhMetaFileBits(buffer);

                if (hemf.IsNull)
                {
                    throw new Win32Exception();
                }
            }

            return HRESULT.S_OK;
        }

        return HRESULT.DV_E_TYMED;

        static HBITMAP GetCompatibleBitmap(object data)
        {
            // A WPF BitmapSource can't go through SystemDrawingHelper.GetHBitmap (its System.Drawing
            // extension only understands System.Drawing.Bitmap and returns a null handle) — that left
            // Clipboard.SetImage(BitmapSource) advertising CF_BITMAP while Clipboard.GetImage() came back
            // null, NRE'ing callers (e.g. WPFGallery's ClipboardPage) that assume it non-null. Convert
            // it directly with a managed CreateDIBSection (no System.Drawing / no COM).
            if (data is BitmapSource bitmapSource)
            {
                return BitmapSourceToHBitmap(bitmapSource);
            }

            HBITMAP hbitmap = SystemDrawingHelper.GetHBitmap(data, out int width, out int height);

            return hbitmap.IsNull ? HBITMAP.Null : hbitmap.CreateCompatibleBitmap(width, height);
        }
    }

    // Managed BitmapSource -> HBITMAP (a top-down 32bpp BGRA DIB section), so SetImage round-trips
    // through the OLE/system clipboard without System.Drawing or the removed bitmap COM. The clipboard
    // takes ownership of the returned handle (same contract as the System.Drawing path above).
    private static HBITMAP BitmapSourceToHBitmap(BitmapSource source)
    {
        BitmapSource bgra = source.Format == Media.PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, Media.PixelFormats.Bgra32, null, 0);

        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return HBITMAP.Null;
        }

        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        bgra.CopyPixels(pixels, stride, 0);

        BITMAPINFOHEADER bmi = new()
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,   // negative => top-down, matching CopyPixels' row order
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,    // BI_RGB
        };

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr hbitmap = CreateDIBSection(screenDc, ref bmi, 0 /*DIB_RGB_COLORS*/, out IntPtr bits, IntPtr.Zero, 0);
        ReleaseDC(IntPtr.Zero, screenDc);

        if (hbitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            return HBITMAP.Null;
        }

        Marshal.Copy(pixels, 0, bits, pixels.Length);
        return (HBITMAP)(nint)hbitmap;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    public static bool TryGetObjectFromDataObject<T>(
        Com.IDataObject* dataObject,
        string format,
        [NotNullWhen(true)] out T data)
    {
        data = default!;

        TYMED mediumType;
        ushort formatId;

        if (format == DataFormatNames.Bitmap)
        {
            mediumType = TYMED.TYMED_GDI;
            formatId = (ushort)CLIPBOARD_FORMAT.CF_BITMAP;
        }
        else if (format == DataFormatNames.Emf)
        {
            mediumType = TYMED.TYMED_ENHMF;
            formatId = (ushort)CLIPBOARD_FORMAT.CF_ENHMETAFILE;
        }
        else
        {
            return false;
        }

        FORMATETC formatEtc = new()
        {
            cfFormat = formatId,
            dwAspect = (uint)DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = (uint)mediumType
        };

        HRESULT result = dataObject->QueryGetData(formatEtc);

        if (result.Failed)
        {
            return false;
        }

        result = dataObject->GetData(formatEtc, out STGMEDIUM medium);

        try
        {
            if (result.Failed)
            {
                return false;
            }

            if (mediumType == TYMED.TYMED_GDI)
            {
                // Get the bitmap from the handle of bitmap.
                object bitmap = Imaging.CreateBitmapSourceFromHBitmap(
                    (HBITMAP)(nint)medium.hGlobal,
                    0,
                    Int32Rect.Empty,
                    sizeOptions: null);

                if (bitmap is T t)
                {
                    data = t;
                    return true;
                }
            }
            else
            {
                // Get the metafile object form the enhanced metafile handle.
                object metafile = SystemDrawingHelper.GetMetafileFromHemf((HENHMETAFILE)(nint)medium.hGlobal);
                if (metafile is T t)
                {
                    data = t;
                    return true;
                }
            }
        }
        finally
        {
            PInvokeCore.ReleaseStgMedium(ref medium);
        }

        return false;
    }

    public static bool AllowTypeWithoutResolver<T>()
    {
        // Image is a special case because we are reading bitmaps directly from the SerializationRecord.
        return typeof(T).FullName.Equals("System.Drawing.Image");
    }

    public static bool IsValidTypeForFormat(Type type, string format) => format switch
    {
        DataFormatNames.Bitmap or DataFormatNames.BinaryFormatBitmap =>
            type == typeof(BitmapSource) || type.FullName is "System.Drawing.Bitmap" or "System.Drawing.Image",
        DataFormatNames.Emf or DataFormatNames.BinaryFormatMetafile =>
            type.FullName is "System.Drawing.Imaging.Metafile" or "System.Drawing.Image",

        // All else should fall through as valid.
        _ => true
    };

    public static void ValidateDataStoreData(ref string format, bool autoConvert, object data)
    {
        // We do not have proper support for Dibs, so if the user explicitly asked
        // for Dib and provided a Bitmap object we can't convert.  Instead, publish as an HBITMAP
        // and let the system provide the conversion for us.
        if (format == DataFormats.Dib && autoConvert && (SystemDrawingHelper.IsBitmap(data) || data is BitmapSource))
        {
            format = DataFormats.Bitmap;
        }
    }

    public static IComVisibleDataObject CreateDataObject() => new DataObject();

    static HRESULT IOleServices.OleGetClipboard(Com.IDataObject** dataObject) =>
        PInvokeCore.OleGetClipboard(dataObject);

    static HRESULT IOleServices.OleSetClipboard(Com.IDataObject* dataObject) =>
        PInvokeCore.OleSetClipboard(dataObject);

    static HRESULT IOleServices.OleFlushClipboard() =>
        PInvokeCore.OleFlushClipboard();
}
