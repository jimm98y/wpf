// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A metafile played with ImageAttributes: the objects of its records recoloured as gdiplus.dll
// (arm64, 10.0.26100) recolours them.
//
//   MetafilePlayer::PrepareToPlay @1800977d8   the player keeps the GpRecolor and the adjust type
//        it was given (DrawImage of a metafile: Default)
//   MetafilePlayer::AddObject @18008fd40       every EMF+ object, once made, is ColorAdjust'ed:
//        GpSolidFill @18006d5d0, GpHatch @18006d2f0 (both colours), GpRectGradient @18006d500 (end
//        colours, presets), GpPathGradient @18006d350 (surround, centre, presets) as Brush;
//        GpTexture @18006d640 its image as Brush; GpPen @180070fe0 its brush as Pen;
//        GpBitmap @18007e5f0 -> GpMemoryBitmap::PerformColorAdjustment @180100890 as Bitmap: a
//        palette's entries, or the pixels locked as ARGB (32bpp RGB and ARGB in place); a type
//        of Default means the object's own type
//   MetafilePlayer::GetBrush @180093130         a colour given in the record (0x8000) is a solid
//        fill recoloured the same way
// The record's own ImageAttributes object is what a DrawImage record draws with; the player's
// is never applied to it again.
//

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static class GpMetaRecolor
    {
        static ColorAdjustType Own (ColorAdjustType t, ColorAdjustType own) => t == ColorAdjustType.Default ? own : t;

        /// <summary>One colour through the recolor object of <paramref name="t"/> (none: as it is).</summary>
        public static Color Adjust (GpRecolor rc, ColorAdjustType t, Color c)
        {
            GpRecolorObject o = rc?.Use (t);
            if (o == null) return c;
            var px = new uint [] { (uint) c.ToArgb () };
            o.ColorAdjust (px, 0, 1);
            return Color.FromArgb ((int) px [0]);
        }

        /// <summary>The object an EMF+ record made, recoloured (MetafilePlayer::AddObject).</summary>
        public static object Object (GpRecolor rc, object obj, ColorAdjustType t)
        {
            switch (obj) {
            case Brush b: return Brush (rc, b, t);
            case Pen p: Pen (rc, p, t); return p;
            case Image im: return Image (rc, im, t);
            default: return obj;
            }
        }

        public static Brush Brush (GpRecolor rc, Brush b, ColorAdjustType t)
        {
            t = Own (t, ColorAdjustType.Brush);
            switch (b) {
            case SolidBrush s: {
                Color c = Adjust (rc, t, s.Color);
                if (c == s.Color) return s;
                s.Dispose ();
                return new SolidBrush (c);
            }
            case HatchBrush h: {
                Color f = Adjust (rc, t, h.ForegroundColor), k = Adjust (rc, t, h.BackgroundColor);
                var n = new HatchBrush (h.HatchStyle, f, k);
                h.Dispose ();
                return n;
            }
            case LinearGradientBrush lg: {
                GpRecolorObject o = rc?.Use (t);
                if (o != null) lg.ColorAdjust (o);
                return lg;
            }
            case PathGradientBrush pg: {
                GpRecolorObject o = rc?.Use (t);
                if (o != null) pg.ColorAdjust (o);
                return pg;
            }
            case TextureBrush tb: {
                // GpTexture::ColorAdjust: its image recoloured, as the brush's type.
                if (rc?.Use (t) != null && tb.Tile != null) tb.Tile = (Bitmap) Image (rc, tb.Tile, t);
                return tb;
            }
            default:
                return b;
            }
        }

        public static void Pen (GpRecolor rc, Pen p, ColorAdjustType t)
        {
            t = Own (t, ColorAdjustType.Pen);
            if (p.PenType == PenType.SolidColor) p.Color = Adjust (rc, t, p.Color);
            else p.Brush = Brush (rc, p.Brush, t);
        }

        /// <summary>CopyOnWriteBitmap::ColorAdjust @18007e578 -> PerformColorAdjustment @180100890.</summary>
        public static Image Image (GpRecolor rc, Image im, ColorAdjustType t)
        {
            if (rc == null || im is not Bitmap bm) return im;
            t = Own (t, ColorAdjustType.Bitmap);
            rc.Flush ();
            GpRecolorObject o = rc.Use (t);
            if (o == null) return im;
            // LoadIntoMemory(PixelFormat32bppPARGB): an image still encoded (an EMF+ bitmap of
            // compressed data) is decoded into PARGB first; one already in memory stays as it is.
            if (!bm.RawFormat.Equals (ImageFormat.MemoryBmp)) {
                Bitmap pm = bm.Clone (new Rectangle (0, 0, bm.Width, bm.Height), PixelFormat.Format32bppPArgb);
                pm.SetResolution (bm.HorizontalResolution, bm.VerticalResolution);
                bm.Dispose ();
                bm = pm;
            }
            PixelFormat f = bm.PixelFormat;
            if ((f & PixelFormat.Indexed) != 0) {
                ColorPalette pal = bm.Palette;
                var e = new uint [pal.Entries.Length];
                for (int i = 0; i < e.Length; i++) e [i] = (uint) pal.Entries [i].ToArgb ();
                o.ColorAdjust (e, 0, e.Length);
                for (int i = 0; i < e.Length; i++) pal.Entries [i] = Color.FromArgb ((int) e [i]);
                bm.Palette = pal;
                return bm;
            }
            PixelFormat lf = f == PixelFormat.Format32bppRgb || f == PixelFormat.Format32bppArgb ? f : PixelFormat.Format32bppArgb;
            int w = bm.Width, h = bm.Height;
            BitmapData d = bm.LockBits (new Rectangle (0, 0, w, h), ImageLockMode.ReadWrite, lf);
            try {
                var row = new int [w];
                var px = new uint [w];
                for (int y = 0; y < h; y++) {
                    IntPtr p = new IntPtr (d.Scan0.ToInt64 () + (long) y * d.Stride);
                    Marshal.Copy (p, row, 0, w);
                    for (int x = 0; x < w; x++) px [x] = (uint) row [x];
                    o.ColorAdjust (px, 0, w);
                    for (int x = 0; x < w; x++) row [x] = (int) px [x];
                    Marshal.Copy (row, 0, p, w);
                }
            } finally {
                bm.UnlockBits (d);
            }
            return bm;
        }
    }
}
