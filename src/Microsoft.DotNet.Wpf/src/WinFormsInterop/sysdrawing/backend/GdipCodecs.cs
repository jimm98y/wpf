// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The image codecs of a managed System.Drawing: the decoders and encoders WPF uses (shared source,
// Shared/MS/Internal/Imaging, link-compiled here) with GDI+'s semantics on top -- which pixel format a
// file comes back as, which property items it carries, what resolution and flags it reports, which
// frames it has; and on the way out, which variant of each format GDI+ writes for each pixel format.
//
// Every rule here was read off gdiplus.dll through System.Drawing on Windows (the oracle in the
// scratchpad, img/oracle/Oracle.cs; its samples and answers are the unit tests' fixtures):
//
//   PNG   palette 1/4/8 bit -> that indexed format; RGB 8-bit -> 24bppRgb; everything else (grey,
//         16-bit, alpha, tRNS, 2-bit palette) -> 32bppArgb. Properties sRGB (0x303), gAMA (0x301),
//         and always PixelUnit/PixelPerUnitX/Y (0x5110-2). DPI from pHYs, truncated to a float.
//   BMP   1/4/8 -> indexed; 16 -> 32bppRgb; 24 -> 24bppRgb; 32 -> 32bppRgb unless the header has an
//         alpha mask (32bppArgb); RLE -> 32bppRgb.
//   GIF   one full-screen image -> 8bppIndexed in its own palette; several -> 32bppArgb, composed,
//         under FrameDimension.Time. FrameDelay (0x5100, 1/100 s), LoopCount (0x5101, 1 without a
//         NETSCAPE block), GlobalPalette (0x5102), IndexBackground (0x5103), IndexTransparent (0x5104).
//   JPEG  24bppRgb (8bppIndexed grey for one component); EXIF tags, then the chrominance and
//         luminance quantisation tables (0x5091, 0x5090) in natural order.
//   TIFF  per page: RGB -> 24bppRgb, RGBA -> 32bppArgb, palette/grey <= 8 bits -> indexed; every
//         IFD entry as a property.
//   ICO   the entry nearest 16x16, deepest first -> 32bppArgb.
//
// Flags: ReadOnly, HasRealDPI when the file stated a resolution (else HasRealPixelSize), the colour
// space, HasAlpha as each decoder reports it.
//

using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace System.Drawing
{
    internal static class GdipCodecs
    {
        internal const int TagFrameDelay = 0x5100, TagLoopCount = 0x5101, TagGlobalPalette = 0x5102,
            TagIndexBackground = 0x5103, TagIndexTransparent = 0x5104;

        private const int FlagReadOnly = 0x10000, FlagRealDpi = 0x1000, FlagRealPixelSize = 0x2000,
            FlagRgb = 0x10, FlagGray = 0x40, FlagAlpha = 2;

        internal static PropertyItem Property (int id, short type, int len, byte[] value)
        {
            var p = new PropertyItem ();
            p.Id = id; p.Type = type; p.Len = len; p.Value = value;
            return p;
        }

        static PropertyItem Byte (int id, int v) => Property (id, 1, 1, new [] { (byte) v });
        static PropertyItem Short (int id, int v) => Property (id, 3, 2, new [] { (byte) v, (byte) (v >> 8) });
        static PropertyItem Long (int id, uint v) => Property (id, 4, 4, BitConverter.GetBytes (v));

        /// <summary>A resolution in pixels per metre as GDI+ reports it: the product truncated to a
        /// float (3779 ppm reads 95.9866, not 95.98660).</summary>
        internal static float DpiFromPpm (double ppm) => TruncToFloat (ppm * 0.0254);

        static float TruncToFloat (double d)
        {
            float f = (float) d;
            if (f > d) f = MathF.BitDecrement (f);
            return f;
        }

        // ---- decoding ---------------------------------------------------------------------------

        internal static bool CanDecode (byte[] data) =>
            ManagedImageDecoder.IsPng (data) || ManagedImageDecoder.IsBmp (data) || ManagedImageDecoder.IsJpeg (data)
            || ManagedImageDecoder.IsIco (data) || ManagedGifDecoder.IsGif (data) || ManagedTiffDecoder.IsTiff (data);

        /// <summary>A file's bytes as a managed image, as GDI+ would load it. Throws
        /// ArgumentException ("Parameter is not valid", GDI+'s InvalidParameter) for data it cannot
        /// read.</summary>
        internal static GdipImageData Decode (byte[] data)
        {
            try {
                if (ManagedImageDecoder.IsPng (data)) return DecodePng (data);
                if (ManagedImageDecoder.IsBmp (data)) return DecodeBmp (data);
                if (ManagedGifDecoder.IsGif (data)) return DecodeGif (data);
                if (ManagedImageDecoder.IsJpeg (data)) return DecodeJpeg (data);
                if (ManagedTiffDecoder.IsTiff (data)) return DecodeTiff (data);
                if (ManagedImageDecoder.IsIco (data)) return DecodeIco (data);
            } catch (Exception e) when (e is InvalidDataException || e is NotSupportedException || e is IndexOutOfRangeException
                                       || e is ArgumentException || e is OverflowException) {
                throw new ArgumentException ("Parameter is not valid.", e);
            }
            throw new ArgumentException ("Parameter is not valid.");
        }

        /// <summary>A frame from straight BGRA bytes, converted into <paramref name="format"/>.</summary>
        internal static GdipFrame FromBgra (byte[] bgra, int width, int height, PixelFormat format)
        {
            var f = new GdipFrame (width, height, format);
            var row = new uint [width];
            for (int y = 0; y < height; y++) {
                int o = y * width * 4;
                for (int x = 0; x < width; x++, o += 4)
                    row [x] = (uint) bgra [o + 3] << 24 | (uint) bgra [o + 2] << 16 | (uint) bgra [o + 1] << 8 | bgra [o];
                GdipPixels.WriteArgb (f, 0, y, width, row, 0);
            }
            return f;
        }

        /// <summary>A frame holding packed indexed (or 24/32-bit) rows as they stand.</summary>
        static GdipFrame FromPacked (byte[] packed, int packedStride, int width, int height, PixelFormat format, Color[] palette)
        {
            var f = new GdipFrame (width, height, format);
            int bytes = Math.Min (packedStride, f.Stride);
            for (int y = 0; y < height; y++) Buffer.BlockCopy (packed, y * packedStride, f.Bits, y * f.Stride, bytes);
            if (palette != null) { f.Palette = palette; f.PaletteFlags = 0; }
            return f;
        }

        static Color[] Colors (uint[] words)
        {
            var c = new Color [words.Length];
            for (int i = 0; i < words.Length; i++) c [i] = Color.FromArgb (unchecked ((int) words [i]));
            return c;
        }

        static int PaletteFlagsOf (Color[] p)
        {
            int flags = 0;
            bool gray = true;
            foreach (Color c in p) {
                if (c.A != 255) flags |= (int) PaletteFlags.HasAlpha;
                if (c.R != c.G || c.G != c.B) gray = false;
            }
            return gray && p.Length > 0 ? flags | (int) PaletteFlags.GrayScale : flags;
        }

        static GdipImageData DecodePng (byte[] data)
        {
            byte[] pixels = ManagedImageDecoder.DecodePng (data, out int w, out int h, out _, out _,
                out ManagedPixelLayout narrow, out uint[] narrowPalette, out int narrowStride, out ManagedImageDecoder.PngInfo info,
                allowNarrow: true, roundGray16: true);
            GdipFrame frame;
            if (narrowStride != 0 && ManagedPixelLayouts.IsIndexed (narrow) && narrow != ManagedPixelLayout.Indexed2) {
                PixelFormat pf = narrow == ManagedPixelLayout.Indexed1 ? PixelFormat.Format1bppIndexed
                               : narrow == ManagedPixelLayout.Indexed4 ? PixelFormat.Format4bppIndexed : PixelFormat.Format8bppIndexed;
                Color[] palette = Colors (narrowPalette);
                frame = FromPacked (pixels, narrowStride, w, h, pf, palette);
                frame.PaletteFlags = PaletteFlagsOf (palette) & ~(int) PaletteFlags.GrayScale;
            } else {
                byte[] bgra = narrowStride != 0 ? ManagedPixelConverter.ToBgra32 (pixels, narrowStride, w, h, narrow, narrowPalette) : pixels;
                // A tRNS colour KEY makes a pixel transparent black, as GDI+ reads it.
                if (info.HasTransparency && info.ColorType == 0) ClearTransparent (bgra);
                bool rgb24 = info.ColorType == 2 && info.BitDepth == 8 && !info.HasTransparency;
                frame = FromBgra (bgra, w, h, rgb24 ? PixelFormat.Format24bppRgb : PixelFormat.Format32bppArgb);
            }
            bool realDpi = info.HasPhys && info.PhysUnit == 1;
            if (realDpi) { frame.DpiX = DpiFromPpm (info.PpmX); frame.DpiY = DpiFromPpm (info.PpmY); }
            var img = new GdipImageData (frame) { RawFormat = ImageFormat.Png.Guid };
            bool gray = info.ColorType == 0 && !info.HasTransparency;
            bool alpha = frame.Format == PixelFormat.Format32bppArgb || (frame.Palette != null && (frame.PaletteFlags & (int) PaletteFlags.HasAlpha) != 0);
            img.Flags = FlagReadOnly | FlagRealPixelSize | (realDpi ? FlagRealDpi : 0) | (gray ? FlagGray : FlagRgb) | (alpha ? FlagAlpha : 0);
            if (info.SrgbIntent >= 0) img.Properties.Add (Byte (0x303, info.SrgbIntent));
            if (info.Gamma > 0) {
                var v = new byte [8];
                BitConverter.GetBytes (100000u).CopyTo (v, 0);
                BitConverter.GetBytes ((uint) info.Gamma).CopyTo (v, 4);
                img.Properties.Add (Property (0x301, 5, 8, v));
            }
            img.Properties.Add (Byte (0x5110, info.HasPhys ? info.PhysUnit : 1));
            img.Properties.Add (Long (0x5111, info.HasPhys ? info.PpmX : 0));
            img.Properties.Add (Long (0x5112, info.HasPhys ? info.PpmY : 0));
            return img;
        }

        static GdipImageData DecodeBmp (byte[] data)
        {
            byte[] pixels = ManagedImageDecoder.DecodeBmp (data, out int w, out int h,
                out ManagedPixelLayout narrow, out uint[] narrowPalette, out int narrowStride, out ManagedImageDecoder.BmpInfo info);
            GdipFrame frame;
            bool rle = info.Compression == 1 || info.Compression == 2;
            if (narrowStride != 0 && !rle) {
                PixelFormat pf = narrow == ManagedPixelLayout.Indexed1 ? PixelFormat.Format1bppIndexed
                               : narrow == ManagedPixelLayout.Indexed4 ? PixelFormat.Format4bppIndexed : PixelFormat.Format8bppIndexed;
                frame = FromPacked (pixels, narrowStride, w, h, pf, Colors (narrowPalette));
                frame.PaletteFlags = 0;
            } else if (narrowStride != 0) {
                // RLE: expanded to 32bppRgb, the fourth byte zero (as GDI+ leaves it).
                frame = FromBgra (ManagedPixelConverter.ToBgra32 (pixels, narrowStride, w, h, narrow, narrowPalette), w, h, PixelFormat.Format32bppRgb);
                for (int i = 3; i < frame.Bits.Length; i += 4) frame.Bits [i] = 0;
            } else if (info.BitCount == 24) {
                frame = FromBgra (pixels, w, h, PixelFormat.Format24bppRgb);
            } else if (info.BitCount == 32 && info.Channels.AlphaMask != 0 && info.Compression != 0) {
                frame = FromBgra (pixels, w, h, PixelFormat.Format32bppArgb);
            } else if (info.BitCount == 32 && info.Compression == 0) {
                // BI_RGB: the fourth byte is not alpha, but GDI+ keeps it (a 32bppRgb lock hands it
                // back); a pixel reads opaque.
                frame = new GdipFrame (w, h, PixelFormat.Format32bppRgb);
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy (data, info.PixelOffset + (info.TopDown ? y : h - 1 - y) * info.FileStride, frame.Bits, y * frame.Stride, w * 4);
            } else if (info.BitCount == 16) {
                // GDI+ widens a 5- or 6-bit field by replicating its top bits (v << 3 | v >> 2), not
                // by rounding v * 255 / 31.
                frame = new GdipFrame (w, h, PixelFormat.Format32bppRgb);
                var ch = info.Channels;
                for (int y = 0; y < h; y++) {
                    int src = info.PixelOffset + (info.TopDown ? y : h - 1 - y) * info.FileStride;
                    for (int x = 0; x < w; x++) {
                        uint v = (uint) (data [src + x * 2] | data [src + x * 2 + 1] << 8);
                        int o = y * frame.Stride + x * 4;
                        frame.Bits [o] = Widen (v, ch.BlueMask); frame.Bits [o + 1] = Widen (v, ch.GreenMask);
                        frame.Bits [o + 2] = Widen (v, ch.RedMask); frame.Bits [o + 3] = 255;
                    }
                }
            } else {
                frame = FromBgra (pixels, w, h, PixelFormat.Format32bppRgb);
            }
            bool realDpi = info.PpmX > 0 && info.PpmY > 0;
            if (realDpi) { frame.DpiX = DpiFromPpm (info.PpmX); frame.DpiY = DpiFromPpm (info.PpmY); }
            var img = new GdipImageData (frame) { RawFormat = ImageFormat.Bmp.Guid };
            img.Flags = FlagReadOnly | FlagRealPixelSize | (realDpi ? FlagRealDpi : 0) | FlagRgb
                      | (frame.Format == PixelFormat.Format32bppArgb ? FlagAlpha : 0);
            return img;
        }

        // A DIB field widened to eight bits by bit replication.
        static byte Widen (uint pixel, uint mask)
        {
            if (mask == 0) return 0;
            int shift = 0;
            while (((mask >> shift) & 1) == 0) shift++;
            int bits = 0;
            while (((mask >> (shift + bits)) & 1) != 0) bits++;
            int v = (int) ((pixel & mask) >> shift);
            if (bits >= 8) return (byte) (v >> (bits - 8));
            int r = 0, filled = 0;
            while (filled < 8) { r = r << bits | v; filled += bits; }
            return (byte) (r >> (filled - 8));
        }

        // Pixels whose alpha is zero lose their colour too.
        static void ClearTransparent (byte[] bgra)
        {
            for (int i = 0; i + 3 < bgra.Length; i += 4)
                if (bgra [i + 3] == 0) bgra [i] = bgra [i + 1] = bgra [i + 2] = 0;
        }

        static GdipImageData DecodeGif (byte[] data)
        {
            // GDI+ restores a frame disposed to the background to the background COLOUR.
            List<ManagedGifFrame> frames = ManagedGifDecoder.Decode (data, out int w, out int h,
                out byte[] indexed, out uint[] indexedPalette, out ManagedGifInfo info, restoreToBackgroundColor: true);
            GdipImageData img;
            if (indexed != null && indexedPalette != null) {
                Color[] palette = Colors (indexedPalette);
                var frame = FromPacked (indexed, w, w, h, PixelFormat.Format8bppIndexed, palette);
                frame.PaletteFlags = PaletteFlagsOf (palette) & (int) PaletteFlags.HasAlpha;
                img = new GdipImageData (frame);
                img.Flags = FlagReadOnly | FlagRealPixelSize | FlagRealDpi | FlagRgb | ((frame.PaletteFlags & (int) PaletteFlags.HasAlpha) != 0 ? FlagAlpha : 0);
            } else {
                var all = new GdipFrame [frames.Count];
                for (int i = 0; i < frames.Count; i++) all [i] = FromBgra (frames [i].Bgra, w, h, PixelFormat.Format32bppArgb);
                img = new GdipImageData (all [0].Clone ()) { Frames = all.Length > 1 ? all : null };
                img.Flags = FlagReadOnly | FlagRealPixelSize | FlagRealDpi | FlagRgb;
            }
            img.RawFormat = ImageFormat.Gif.Guid;
            img.FrameDimension = FrameDimension.Time.Guid;
            var delays = new byte [frames.Count * 4];
            for (int i = 0; i < frames.Count; i++) BitConverter.GetBytes (frames [i].DelayMilliseconds / 10).CopyTo (delays, i * 4);
            img.Properties.Add (Property (TagFrameDelay, 4, delays.Length, delays));
            img.Properties.Add (Short (TagLoopCount, info.LoopCount >= 0 ? info.LoopCount : 1));
            if (info.GlobalTable != null) img.Properties.Add (Property (TagGlobalPalette, 1, info.GlobalTable.Length, (byte[]) info.GlobalTable.Clone ()));
            img.Properties.Add (Byte (TagIndexBackground, info.BackgroundIndex));
            if (frames.Count > 0 && frames [0].TransparentIndex >= 0) img.Properties.Add (Byte (TagIndexTransparent, frames [0].TransparentIndex));
            return img;
        }

        static GdipImageData DecodeJpeg (byte[] data)
        {
            ManagedJpegInfo info = ManagedJpegDecoder.ReadInfo (data);
            byte[] bgra = ManagedJpegDecoder.Decode (data, out int w, out int h, libjpegCompatible: true);
            GdipFrame frame;
            bool gray = info.Components == 1;
            if (gray) {
                frame = new GdipFrame (w, h, PixelFormat.Format8bppIndexed);
                frame.Palette = new Color [256];
                for (int i = 0; i < 256; i++) frame.Palette [i] = Color.FromArgb (255, i, i, i);
                frame.PaletteFlags = (int) PaletteFlags.GrayScale;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++) frame.Bits [y * frame.Stride + x] = bgra [(y * w + x) * 4];
            } else {
                frame = FromBgra (bgra, w, h, PixelFormat.Format24bppRgb);
            }
            List<ManagedTiffTag> exif = info.Exif != null ? ManagedTiffDecoder.ReadExif (info.Exif) : new List<ManagedTiffTag> ();
            float dpiX = 96f, dpiY = 96f;
            if (info.HasJfif && (info.DensityUnit == 1 || info.DensityUnit == 2) && info.DensityX > 0 && info.DensityY > 0) {
                dpiX = info.DensityUnit == 1 ? info.DensityX : TruncToFloat (info.DensityX * 2.54);
                dpiY = info.DensityUnit == 1 ? info.DensityY : TruncToFloat (info.DensityY * 2.54);
            } else if (Rational (exif, 0x11A) is double ex && Rational (exif, 0x11B) is double ey && ex > 0 && ey > 0) {
                bool cm = ShortTag (exif, 0x128) == 3;
                dpiX = TruncToFloat (cm ? ex * 2.54 : ex);
                dpiY = TruncToFloat (cm ? ey * 2.54 : ey);
            }
            frame.DpiX = dpiX; frame.DpiY = dpiY;
            var img = new GdipImageData (frame) { RawFormat = ImageFormat.Jpeg.Guid };
            img.Flags = FlagReadOnly | FlagRealPixelSize | FlagRealDpi | (gray ? FlagGray : FlagRgb);
            foreach (ManagedTiffTag t in exif) img.Properties.Add (Property (t.Tag, (short) t.Type, t.Value.Length, t.Value));
            if (!gray) AddQuant (img, 0x5091, info.Quant [info.ComponentQuant [1] & 3]);
            AddQuant (img, 0x5090, info.Quant [info.ComponentQuant [0] & 3]);
            return img;
        }

        static void AddQuant (GdipImageData img, int id, ushort[] table)
        {
            if (table == null) return;
            var v = new byte [128];
            for (int i = 0; i < 64; i++) { v [i * 2] = (byte) table [i]; v [i * 2 + 1] = (byte) (table [i] >> 8); }
            img.Properties.Add (Property (id, 3, 128, v));
        }

        static double? Rational (List<ManagedTiffTag> tags, int id)
        {
            foreach (ManagedTiffTag t in tags)
                if (t.Tag == id && t.Type == 5 && t.Value.Length >= 8) {
                    uint n = BitConverter.ToUInt32 (t.Value, 0), d = BitConverter.ToUInt32 (t.Value, 4);
                    return d == 0 ? (double?) null : (double) n / d;
                }
            return null;
        }

        static int ShortTag (List<ManagedTiffTag> tags, int id)
        {
            foreach (ManagedTiffTag t in tags)
                if (t.Tag == id && t.Value.Length >= 2) return BitConverter.ToUInt16 (t.Value, 0);
            return -1;
        }

        static GdipImageData DecodeTiff (byte[] data)
        {
            List<ManagedTiffPage> pages = ManagedTiffDecoder.Decode (data);
            var frames = new GdipFrame [pages.Count];
            var props = new List<PropertyItem>[pages.Count];
            for (int i = 0; i < pages.Count; i++) {
                ManagedTiffPage page = pages [i];
                ManagedTiffPageInfo info = page.Info;
                GdipFrame f;
                if (info != null && info.Indices != null && info.BitsPerSample <= 8) {
                    PixelFormat pf = info.BitsPerSample == 1 ? PixelFormat.Format1bppIndexed
                                   : info.BitsPerSample <= 4 ? PixelFormat.Format4bppIndexed : PixelFormat.Format8bppIndexed;
                    Color[] palette;
                    int levels = 1 << info.BitsPerSample;
                    if (info.Palette != null) palette = Colors (info.Palette);
                    else {
                        palette = new Color [levels];
                        for (int k = 0; k < levels; k++) {
                            int v = k * 255 / (levels - 1);
                            if (info.Photometric == 0) v = 255 - v;
                            palette [k] = Color.FromArgb (255, v, v, v);
                        }
                    }
                    f = new GdipFrame (page.Width, page.Height, pf) { Palette = palette, PaletteFlags = info.Palette != null ? 0 : (int) PaletteFlags.GrayScale };
                    int bits = Image.GetPixelFormatSize (pf);
                    for (int y = 0; y < page.Height; y++)
                        for (int x = 0; x < page.Width; x++) {
                            int idx = info.Indices [y * page.Width + x];
                            int o = y * f.Stride;
                            if (bits == 8) f.Bits [o + x] = (byte) idx;
                            else if (bits == 4) f.Bits [o + (x >> 1)] |= (byte) ((idx & 15) << ((x & 1) == 0 ? 4 : 0));
                            else f.Bits [o + (x >> 3)] |= (byte) ((idx & 1) << (7 - (x & 7)));
                        }
                } else {
                    bool alpha = info == null || info.HasAlpha;
                    f = FromBgra (page.Bgra, page.Width, page.Height, alpha ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb);
                }
                if (page.DpiX > 0 && page.DpiY > 0) { f.DpiX = TruncToFloat (page.DpiX); f.DpiY = TruncToFloat (page.DpiY); }
                frames [i] = f;
                props [i] = new List<PropertyItem> ();
                if (info != null)
                    foreach (ManagedTiffTag t in info.Tags) props [i].Add (Property (t.Tag, (short) t.Type, t.Value.Length, t.Value));
            }
            var img = new GdipImageData (frames [0].Clone ()) { RawFormat = ImageFormat.Tiff.Guid, Frames = frames.Length > 1 ? frames : null };
            img.Properties = props [0];
            img.FrameProperties = props;
            bool a0 = frames [0].Format == PixelFormat.Format32bppArgb;
            img.Flags = FlagReadOnly | FlagRealPixelSize | FlagRealDpi | FlagRgb | (a0 ? FlagAlpha : 0);
            return img;
        }

        /// <summary>The entry GDI+'s ICO decoder takes: the one nearest 16x16, the deepest of those.</summary>
        internal static ManagedImageDecoder.IcoEntry PickIcoEntry (ManagedImageDecoder.IcoEntry[] entries)
        {
            ManagedImageDecoder.IcoEntry best = entries [0];
            int bestDelta = int.MaxValue, bestBits = -1;
            foreach (var e in entries) {
                int delta = Math.Abs (e.Width - 16) + Math.Abs (e.Height - 16);
                int bits = e.IsPng ? 32 : e.BitCount;
                if (delta < bestDelta || (delta == bestDelta && bits > bestBits)) { best = e; bestDelta = delta; bestBits = bits; }
            }
            return best;
        }

        static GdipImageData DecodeIco (byte[] data)
        {
            var entries = ManagedImageDecoder.ReadIcoDirectory (data);
            var entry = PickIcoEntry (entries);
            byte[] bgra = ManagedImageDecoder.DecodeIcoEntry (data, entry, out int w, out int h, out _);
            if (!entry.IsPng) ClearTransparent (bgra);
            var img = new GdipImageData (FromBgra (bgra, w, h, PixelFormat.Format32bppArgb)) { RawFormat = ImageFormat.Icon.Guid };
            img.Flags = FlagReadOnly | FlagRealPixelSize | FlagRgb | FlagAlpha;
            return img;
        }

        // ---- encoding ---------------------------------------------------------------------------

        static readonly Guid BmpClsid = new Guid ("557cf400-1a04-11d3-9a73-0000f81ef32e");
        static readonly Guid JpegClsid = new Guid ("557cf401-1a04-11d3-9a73-0000f81ef32e");
        static readonly Guid GifClsid = new Guid ("557cf402-1a04-11d3-9a73-0000f81ef32e");
        static readonly Guid EmfClsid = new Guid ("557cf403-1a04-11d3-9a73-0000f81ef32e");
        static readonly Guid WmfClsid = new Guid ("557cf404-1a04-11d3-9a73-0000f81ef32e");
        static readonly Guid TiffClsid = new Guid ("557cf405-1a04-11d3-9a73-0000f81ef32e");
        static readonly Guid PngClsid = new Guid ("557cf406-1a04-11d3-9a73-0000f81ef32e");
        static readonly Guid IcoClsid = new Guid ("557cf407-1a04-11d3-9a73-0000f81ef32e");

        static ImageCodecInfo Codec (Guid clsid, Guid format, string name, string desc, string ext, string mime, int flags, byte[][] sig)
        {
            var c = new ImageCodecInfo ();
            c.Clsid = clsid; c.FormatID = format; c.CodecName = name; c.DllName = null; c.FormatDescription = desc;
            c.FilenameExtension = ext; c.MimeType = mime; c.Flags = (ImageCodecFlags) flags; c.Version = 1;
            c.SignaturePatterns = sig;
            var masks = new byte [sig.Length][];
            for (int i = 0; i < sig.Length; i++) { masks [i] = new byte [sig [i].Length]; for (int k = 0; k < masks [i].Length; k++) masks [i] [k] = 0xff; }
            c.SignatureMasks = masks;
            return c;
        }

        static byte[] B (params int[] v) { var b = new byte [v.Length]; for (int i = 0; i < v.Length; i++) b [i] = (byte) v [i]; return b; }

        // GDI+'s built-in codecs, as ImageCodecInfo lists them: Encoder|Decoder|SupportBitmap|BuiltIn
        // (65543) for the five it writes, Decoder|SupportVector|BuiltIn (65542) for EMF/WMF and ICO.
        internal static ImageCodecInfo[] Decoders () => new [] {
            Codec (BmpClsid, ImageFormat.Bmp.Guid, "Built-in BMP Codec", "BMP", "*.BMP;*.DIB;*.RLE", "image/bmp", 65543, new [] { B ('B', 'M') }),
            Codec (JpegClsid, ImageFormat.Jpeg.Guid, "Built-in JPEG Codec", "JPEG", "*.JPG;*.JPEG;*.JPE;*.JFIF", "image/jpeg", 65543, new [] { B (0xff, 0xd8) }),
            Codec (GifClsid, ImageFormat.Gif.Guid, "Built-in GIF Codec", "GIF", "*.GIF", "image/gif", 65543, new [] { B ('G', 'I', 'F', '8', '9', 'a'), B ('G', 'I', 'F', '8', '7', 'a') }),
            Codec (EmfClsid, ImageFormat.Emf.Guid, "Built-in EMF Codec", "EMF", "*.EMF", "image/x-emf", 65542, new [] { B (0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x20, 0x45, 0x4d, 0x46) }),
            Codec (WmfClsid, ImageFormat.Wmf.Guid, "Built-in WMF Codec", "WMF", "*.WMF", "image/x-wmf", 65542, new [] { B (0xd7, 0xcd, 0xc6, 0x9a) }),
            Codec (TiffClsid, ImageFormat.Tiff.Guid, "Built-in TIFF Codec", "TIFF", "*.TIF;*.TIFF", "image/tiff", 65543, new [] { B ('I', 'I'), B ('M', 'M') }),
            Codec (PngClsid, ImageFormat.Png.Guid, "Built-in PNG Codec", "PNG", "*.PNG", "image/png", 65543, new [] { B (0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a) }),
            Codec (IcoClsid, ImageFormat.Icon.Guid, "Built-in ICO Codec", "ICO", "*.ICO", "image/x-icon", 65542, new [] { B (0, 0, 1, 0) }),
        };

        internal static ImageCodecInfo[] Encoders () => new [] {
            Codec (BmpClsid, ImageFormat.Bmp.Guid, "Built-in BMP Codec", "BMP", "*.BMP;*.DIB;*.RLE", "image/bmp", 65543, new [] { B ('B', 'M') }),
            Codec (JpegClsid, ImageFormat.Jpeg.Guid, "Built-in JPEG Codec", "JPEG", "*.JPG;*.JPEG;*.JPE;*.JFIF", "image/jpeg", 65543, new [] { B (0xff, 0xd8) }),
            Codec (GifClsid, ImageFormat.Gif.Guid, "Built-in GIF Codec", "GIF", "*.GIF", "image/gif", 65543, new [] { B ('G', 'I', 'F', '8', '9', 'a'), B ('G', 'I', 'F', '8', '7', 'a') }),
            Codec (TiffClsid, ImageFormat.Tiff.Guid, "Built-in TIFF Codec", "TIFF", "*.TIF;*.TIFF", "image/tiff", 65543, new [] { B ('I', 'I'), B ('M', 'M') }),
            Codec (PngClsid, ImageFormat.Png.Guid, "Built-in PNG Codec", "PNG", "*.PNG", "image/png", 65543, new [] { B (0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a) }),
        };

        /// <summary>The encoder for a raw format (MemoryBmp saves as PNG, as GDI+ does), or null.</summary>
        internal static ImageCodecInfo EncoderFor (Guid format)
        {
            if (format == ImageFormat.MemoryBmp.Guid) format = ImageFormat.Png.Guid;
            foreach (ImageCodecInfo c in Encoders ())
                if (c.FormatID == format) return c;
            return null;
        }

        static long? Param (EncoderParameters ps, Guid which)
        {
            if (ps?.Param == null) return null;
            foreach (EncoderParameter p in ps.Param)
                if (p != null && p.Encoder.Guid == which && p.NumberOfValues > 0) return p.FirstValue;
            return null;
        }

        /// <summary>Writes <paramref name="frame"/> with the encoder <paramref name="clsid"/>.
        /// <paramref name="extra"/> are further TIFF pages (SaveAdd).</summary>
        internal static void Encode (GdipFrame frame, Guid clsid, EncoderParameters ps, Stream stream, IList<GdipFrame> extra = null)
        {
            if (clsid == PngClsid) {
                WritePng (frame, stream);
            } else if (clsid == BmpClsid) {
                WriteBmp (frame, stream);
            } else if (clsid == GifClsid) {
                WriteGif (frame, stream);
            } else if (clsid == JpegClsid) {
                int quality = (int) Math.Clamp (Param (ps, Imaging.Encoder.Quality.Guid) ?? 75, 0, 100);
                var r = new Rectangle (0, 0, frame.Width, frame.Height);
                byte[] bgra = Bgra (frame);
                ManagedJpegEncoder.Write (stream, bgra, frame.Width, frame.Height, frame.Width * 4, frame.DpiX, frame.DpiY, Math.Max (1, quality));
            } else if (clsid == TiffClsid) {
                var pages = new List<ManagedRaster> { TiffPage (frame) };
                if (extra != null) foreach (GdipFrame f in extra) pages.Add (TiffPage (f));
                ManagedTiffEncoder.Write (pages, stream);
            } else {
                throw new ArgumentException ("Parameter is not valid.");
            }
        }

        static byte[] Bgra (GdipFrame f)
        {
            uint[] argb = GdipPixels.ToArgb (f, new Rectangle (0, 0, f.Width, f.Height));
            var bgra = new byte [argb.Length * 4];
            Buffer.BlockCopy (argb, 0, bgra, 0, bgra.Length);
            if (!BitConverter.IsLittleEndian)
                for (int i = 0; i < argb.Length; i++) { uint c = argb [i]; bgra [i * 4] = (byte) c; bgra [i * 4 + 1] = (byte) (c >> 8); bgra [i * 4 + 2] = (byte) (c >> 16); bgra [i * 4 + 3] = (byte) (c >> 24); }
            return bgra;
        }

        static byte[] Bgr (GdipFrame f, out int stride)
        {
            stride = (f.Width * 3 + 3) & ~3;
            var bgr = new byte [stride * f.Height];
            var row = new uint [f.Width];
            for (int y = 0; y < f.Height; y++) {
                GdipPixels.ReadArgb (f, 0, y, f.Width, row, 0);
                for (int x = 0; x < f.Width; x++) {
                    int o = y * stride + x * 3;
                    bgr [o] = (byte) row [x]; bgr [o + 1] = (byte) (row [x] >> 8); bgr [o + 2] = (byte) (row [x] >> 16);
                }
            }
            return bgr;
        }

        static uint[] Words (Color[] p)
        {
            var w = new uint [p.Length];
            for (int i = 0; i < p.Length; i++) w [i] = (uint) p [i].ToArgb ();
            return w;
        }

        static ManagedPixelLayout IndexedLayout (PixelFormat f) => f switch
        {
            PixelFormat.Format1bppIndexed => ManagedPixelLayout.Indexed1,
            PixelFormat.Format4bppIndexed => ManagedPixelLayout.Indexed4,
            _ => ManagedPixelLayout.Indexed8,
        };

        // GDI+ writes pHYs from the resolution TRUNCATED to pixels per metre (96 dpi -> 3779), in
        // single precision: WindowsCodecs' ConvertDpiToDpm(float) @1801b8b40 is dpi / 0.0254f, NaN
        // as 3779, at least 2^32 as 0xffffffff.
        static uint PngPpm (float dpi)
        {
            float m = dpi / 0.0254f;
            if (float.IsNaN (m)) return 0xec3;
            return m >= 4294967296f ? 0xffffffff : (uint) m;
        }

        static void WritePng (GdipFrame f, Stream stream)
        {
            ManagedRaster raster;
            if (f.IsIndexed) {
                raster = new ManagedRaster (f.Width, f.Height, IndexedLayout (f.Format), f.Bits, f.Stride, Words (f.Palette));
            } else if (f.Format == PixelFormat.Format24bppRgb) {
                raster = new ManagedRaster (f.Width, f.Height, ManagedPixelLayout.Bgr24, f.Bits, f.Stride);
            } else {
                raster = new ManagedRaster (f.Width, f.Height, ManagedPixelLayout.Bgra32, Bgra (f), f.Width * 4);
            }
            WebGpuBackend.GdipPngEncoder.Write (stream, raster, PngPpm (f.DpiX), PngPpm (f.DpiY));
        }

        static void WriteBmp (GdipFrame f, Stream stream)
        {
            uint ppmX = ManagedBmpEncoder.PixelsPerMetre (f.DpiX), ppmY = ManagedBmpEncoder.PixelsPerMetre (f.DpiY);
            ManagedRaster raster;
            if (f.IsIndexed) {
                raster = new ManagedRaster (f.Width, f.Height, IndexedLayout (f.Format), f.Bits, f.Stride, Words (f.Palette));
            } else if (f.Format == PixelFormat.Format24bppRgb) {
                raster = new ManagedRaster (f.Width, f.Height, ManagedPixelLayout.Bgr24, f.Bits, f.Stride);
            } else {
                raster = new ManagedRaster (f.Width, f.Height, ManagedPixelLayout.Bgra32, Bgra (f), f.Width * 4);
            }
            ManagedBmpEncoder.Write (stream, raster, ppmX, ppmY, alphaHeader: false);
        }

        static void WriteGif (GdipFrame f, Stream stream)
        {
            if (f.IsIndexed && f.Palette != null && f.Palette.Length > 0) {
                var indices = new byte [f.Width * f.Height];
                int bits = f.BitsPerPixel;
                for (int y = 0; y < f.Height; y++)
                    for (int x = 0; x < f.Width; x++) {
                        int o = y * f.Stride;
                        indices [y * f.Width + x] = bits == 8 ? f.Bits [o + x]
                            : bits == 4 ? (byte) ((f.Bits [o + (x >> 1)] >> ((x & 1) == 0 ? 4 : 0)) & 15)
                            : (byte) ((f.Bits [o + (x >> 3)] >> (7 - (x & 7))) & 1);
                    }
                ManagedGifEncoder.WriteIndexed (stream, f.Width, f.Height, indices, Words (f.Palette));
                return;
            }
            ManagedGifEncoder.Write (stream, Bgra (f), f.Width, f.Height, f.Width * 4);
        }

        static ManagedRaster TiffPage (GdipFrame f)
        {
            // An 8-bit palette bitmap is written as a palette page, as GDI+ writes it.
            if (f.Format == PixelFormat.Format8bppIndexed && f.Palette != null && f.Palette.Length <= 256)
                return new ManagedRaster (f.Width, f.Height, ManagedPixelLayout.Indexed8, f.Bits, f.Stride, Words (f.Palette)) { DpiX = f.DpiX, DpiY = f.DpiY };
            bool alpha = f.Format == PixelFormat.Format32bppArgb || f.Format == PixelFormat.Format32bppPArgb
                      || f.Format == PixelFormat.Format32bppRgb || f.Format == PixelFormat.Format64bppArgb
                      || f.Format == PixelFormat.Format64bppPArgb || f.Format == PixelFormat.Format16bppArgb1555
                      || (f.IsIndexed && f.Palette != null && Array.Exists (f.Palette, c => c.A != 255));
            ManagedRaster r = alpha
                ? new ManagedRaster (f.Width, f.Height, ManagedPixelLayout.Bgra32, Bgra (f), f.Width * 4)
                : new ManagedRaster (f.Width, f.Height, ManagedPixelLayout.Bgr24, Bgr (f, out int stride), stride);
            r.DpiX = f.DpiX; r.DpiY = f.DpiY;
            return r;
        }
    }
}
