// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The ImageAttributes battery: every adjustment ImageAttributes has, alone and together, through
// every path GDI+ applies them on, reduced to bytes. Compiled twice -- into the tests (the managed
// GDI+) and, with NATIVE defined, against the real System.Drawing.Common (gdiplus.dll), which wrote
// the digests ImageAttributesTests holds the port to.
//
//   the colour matrix of each class GpRecolorObject::Flush tells apart (translate only, diagonal
//   with and without an alpha scale, alpha row and column untouched, general), with ColorMatrixFlag
//   Default, SkipGrays and AltGrays (and a grey matrix of each class), saturating, negative, and
//   off-diagonal entries below the classification's epsilon; threshold, gamma (and both), NoOp,
//   colour key, remap table, output channel C, M, Y and K, and all of them at once; the per-type
//   objects (Default, Bitmap, Brush, Pen, Text) and which one an image uses; the Clear* calls, the
//   argument errors, Clone, GetAdjustedPalette; every wrap mode with its clamp colour and source-
//   rectangle clamp under every interpolation mode; recoloured source formats (24, 32, PARGB,
//   indexed, 16bpp); a TextureBrush made with ImageAttributes; and metafiles played with
//   ImageAttributes for Brush, Pen, Text and Bitmap (EMF+ objects, and an EMF's GDI records).
//
// A case's bytes are the destination's 32bpp ARGB pixels after drawing, or "EXC:" and the
// exception's type name.
//

#nullable disable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Wpf.WinFormsInterop.Tests
{
    internal static class ImageAttributesBattery
    {
        /// <summary>Where the metafile fixtures are (Fixtures/Metafiles).</summary>
        public static string MetafileDir = Path.Combine (AppContext.BaseDirectory, "Fixtures", "Metafiles");

        // ---- sources ---------------------------------------------------------------------------

        static Bitmap s_argb, s_rgb24, s_pargb, s_i8, s_i4, s_i1, s_565, s_tile;

        /// <summary>16x12 ARGB: a colour field with greys (R = G = B) on every third pixel, alpha
        /// varied, and a few pixels the remap table and colour key name.</summary>
        static Bitmap Argb ()
        {
            if (s_argb != null) return s_argb;
            var b = new Bitmap (16, 12, PixelFormat.Format32bppArgb);
            for (int y = 0; y < 12; y++)
                for (int x = 0; x < 16; x++) {
                    int a = (x + y) % 4 == 0 ? 255 : (x * 37 + y * 11) % 256;
                    int r = (x * 17 + y * 3) & 255, g = (y * 23 + x * 5) & 255, bl = ((x ^ y) * 31) & 255;
                    if ((x + 2 * y) % 3 == 0) { g = r; bl = r; }
                    b.SetPixel (x, y, Color.FromArgb (a, r, g, bl));
                }
            b.SetPixel (0, 0, Color.FromArgb (255, 0, 0, 0));
            b.SetPixel (1, 0, Color.FromArgb (255, 255, 255, 255));
            b.SetPixel (2, 0, Color.FromArgb (255, 10, 200, 30));
            b.SetPixel (3, 0, Color.FromArgb (128, 10, 200, 30));
            b.SetPixel (4, 0, Color.FromArgb (0, 50, 60, 70));
            b.SetPixel (5, 0, Color.FromArgb (255, 128, 128, 128));
            b.SetPixel (6, 0, Color.FromArgb (1, 255, 255, 255));
            b.SetResolution (96, 96);
            return s_argb = b;
        }

        static Bitmap Rgb24 ()
        {
            if (s_rgb24 != null) return s_rgb24;
            var b = new Bitmap (10, 8, PixelFormat.Format24bppRgb);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 10; x++) {
                    int r = (x * 29) & 255, g = (y * 37) & 255, bl = ((x + y) * 19) & 255;
                    if ((x + y) % 4 == 1) { g = r; bl = r; }
                    b.SetPixel (x, y, Color.FromArgb (255, r, g, bl));
                }
            b.SetPixel (0, 0, Color.FromArgb (255, 10, 200, 30));
            b.SetResolution (96, 96);
            return s_rgb24 = b;
        }

        static Bitmap Pargb ()
        {
            if (s_pargb != null) return s_pargb;
            var b = new Bitmap (12, 9, PixelFormat.Format32bppPArgb);
            for (int y = 0; y < 9; y++)
                for (int x = 0; x < 12; x++) {
                    int a = (x * 23 + y * 41) % 256;
                    if ((x + y) % 5 == 0) a = 255;
                    int r = (x * 21) & 255, g = (y * 28) & 255, bl = (x * y * 7) & 255;
                    b.SetPixel (x, y, Color.FromArgb (a, r, g, bl));
                }
            b.SetResolution (96, 96);
            return s_pargb = b;
        }

        static Bitmap Indexed (PixelFormat f, ref Bitmap cache)
        {
            if (cache != null) return cache;
            int w = 9, h = 7;
            var b = new Bitmap (w, h, f);
            ColorPalette p = b.Palette;
            int n = p.Entries.Length;
            for (int i = 0; i < n; i++) {
                int a = i % 3 == 0 ? 255 : (i * 53) % 256;
                int c = (i * 47) & 255;
                p.Entries [i] = i % 2 == 0 ? Color.FromArgb (a, c, c, c) : Color.FromArgb (a, c, (i * 91) & 255, (i * 13) & 255);
            }
            b.Palette = p;
            BitmapData d = b.LockBits (new Rectangle (0, 0, w, h), ImageLockMode.WriteOnly, f);
            int bpp = Image.GetPixelFormatSize (f);
            var row = new byte [d.Stride];
            for (int y = 0; y < h; y++) {
                Array.Clear (row, 0, row.Length);
                for (int x = 0; x < w; x++) {
                    int v = (x * 5 + y * 3) % n;
                    if (bpp == 8) row [x] = (byte) v;
                    else if (bpp == 4) row [x >> 1] |= (byte) (v << ((x & 1) == 0 ? 4 : 0));
                    else row [x >> 3] |= (byte) (v << (7 - (x & 7)));
                }
                Marshal.Copy (row, 0, new IntPtr (d.Scan0.ToInt64 () + (long) y * d.Stride), d.Stride);
            }
            b.UnlockBits (d);
            b.SetResolution (96, 96);
            return cache = b;
        }

        static Bitmap Rgb565 ()
        {
            if (s_565 != null) return s_565;
            var b = new Bitmap (8, 6, PixelFormat.Format16bppRgb565);
            BitmapData d = b.LockBits (new Rectangle (0, 0, 8, 6), ImageLockMode.WriteOnly, b.PixelFormat);
            var row = new byte [d.Stride];
            for (int y = 0; y < 6; y++) {
                for (int x = 0; x < 8; x++) {
                    int v = (x * 4 + y * 1) << 11 | (x * 9 + y * 5) << 5 | (x * y + 3);
                    if (x == y) v = 0x8410;
                    row [x * 2] = (byte) v; row [x * 2 + 1] = (byte) (v >> 8);
                }
                Marshal.Copy (row, 0, new IntPtr (d.Scan0.ToInt64 () + (long) y * d.Stride), d.Stride);
            }
            b.UnlockBits (d);
            b.SetResolution (96, 96);
            return s_565 = b;
        }

        static Bitmap Tile ()
        {
            if (s_tile != null) return s_tile;
            var b = new Bitmap (5, 4, PixelFormat.Format32bppArgb);
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 5; x++)
                    b.SetPixel (x, y, Color.FromArgb (x == y ? 160 : 255, x * 60, y * 80, 200 - x * 30));
            b.SetResolution (96, 96);
            return s_tile = b;
        }

        // ---- the destination -------------------------------------------------------------------

        // bg: 0 transparent, 1 white, 2 a pattern of varied alpha
        static Bitmap Dest (int w, int h, int bg)
        {
            var b = new Bitmap (w, h, PixelFormat.Format32bppArgb);
            b.SetResolution (96, 96);
            BitmapData d = b.LockBits (new Rectangle (0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            var row = new int [w];
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    int c;
                    if (bg == 0) c = 0;
                    else if (bg == 1) c = unchecked ((int) 0xFFFFFFFF);
                    else {
                        int a = (x * 11 + y * 7) % 4 == 0 ? 255 : ((x * 29 + y * 17) & 255);
                        c = (a << 24) | (((x * 13) & 255) << 16) | (((y * 19) & 255) << 8) | ((x * y) & 255);
                    }
                    row [x] = c;
                }
                Marshal.Copy (row, 0, new IntPtr (d.Scan0.ToInt64 () + (long) y * d.Stride), w);
            }
            b.UnlockBits (d);
            return b;
        }

        static byte[] Bytes (Bitmap b)
        {
            BitmapData d = b.LockBits (new Rectangle (0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var o = new byte [b.Width * 4 * b.Height];
            for (int y = 0; y < b.Height; y++)
                Marshal.Copy (new IntPtr (d.Scan0.ToInt64 () + (long) y * d.Stride), o, y * b.Width * 4, b.Width * 4);
            b.UnlockBits (d);
            return o;
        }

        [ThreadStatic] static int s_lastWidth;

        static byte[] Draw (int w, int h, int bg, Action<Graphics> body)
        {
            s_lastWidth = w;
            using (Bitmap b = Dest (w, h, bg)) {
                using (Graphics g = Graphics.FromImage (b)) body (g);
                return Bytes (b);
            }
        }

        /// <summary>The recolour on its own: the ARGB source copied 1:1 (SourceCopy onto
        /// transparent), then blended over the pattern; the 24bpp source likewise.</summary>
        static byte[] Recolor (ImageAttributes ia)
        {
            return Draw (36, 26, 2, g => {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage (Argb (), new Rectangle (0, 0, 16, 12), 0, 0, 16, 12, GraphicsUnit.Pixel, ia);
                g.CompositingMode = CompositingMode.SourceOver;
                g.DrawImage (Argb (), new Rectangle (18, 0, 16, 12), 0, 0, 16, 12, GraphicsUnit.Pixel, ia);
                g.DrawImage (Rgb24 (), new Rectangle (0, 14, 10, 8), 0, 0, 10, 8, GraphicsUnit.Pixel, ia);
                g.DrawImage (Rgb24 (), new Rectangle (12, 14, 20, 12), 0, 0, 10, 8, GraphicsUnit.Pixel, ia);
            });
        }

        // ---- matrices ----------------------------------------------------------------------------

        static ColorMatrix M (params float[] v)
        {
            var m = new float [5][];
            for (int r = 0; r < 5; r++) { m [r] = new float [5]; for (int c = 0; c < 5; c++) m [r] [c] = v [r * 5 + c]; }
            return new ColorMatrix (m);
        }

        static readonly Dictionary<string, ColorMatrix> s_matrices = new Dictionary<string, ColorMatrix> ();

        static Dictionary<string, ColorMatrix> Matrices ()
        {
            if (s_matrices.Count > 0) return s_matrices;
            var d = s_matrices;
            d ["identity"] = new ColorMatrix ();
            d ["translate"] = M (1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0.1f, -0.2f, 0.33f, 0, 0);
            d ["translate-a"] = M (1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0.05f, 0.5f, -0.9f, -0.25f, 0);
            d ["diag"] = M (0.5f, 0, 0, 0, 0, 0, 1.5f, 0, 0, 0, 0, 0, 0.25f, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1);
            d ["diag-a"] = M (0.8f, 0, 0, 0, 0, 0, 0.3f, 0, 0, 0, 0, 0, 2.5f, 0, 0, 0, 0, 0, 0.6f, 0, 0, 0, 0, 0, 1);
            d ["diag-neg"] = M (-1, 0, 0, 0, 0, 0, -0.5f, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1.4f, 0, 0, 0, 0, 0, 0);
            d ["grey"] = M (0.3f, 0.3f, 0.3f, 0, 0, 0.59f, 0.59f, 0.59f, 0, 0, 0.11f, 0.11f, 0.11f, 0, 0, 0, 0, 0, 1, 0, 0.02f, 0, -0.03f, 0, 1);
            d ["sepia"] = M (0.393f, 0.349f, 0.272f, 0, 0, 0.769f, 0.686f, 0.534f, 0, 0, 0.189f, 0.168f, 0.131f, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1);
            d ["invert"] = M (-1, 0, 0, 0, 0, 0, -1, 0, 0, 0, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 1, 1, 1, 0, 1);
            d ["general"] = M (0.3f, 0.3f, 0.3f, 0.2f, 0, 0.59f, 0.2f, 0.1f, 0, 0, 0.11f, 0.4f, 0.7f, 0, 0, 0.1f, 0.2f, 0.3f, 0.7f, 0, 0.1f, 0, 0, 0.05f, 1);
            d ["alpha-from-red"] = M (1, 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1);
            d ["colour-from-alpha"] = M (0.5f, 0, 0, 0, 0, 0, 0.5f, 0, 0, 0, 0, 0, 0.5f, 0, 0, 0.5f, 0.25f, 0.75f, 1, 0, 0, 0, 0, 0, 1);
            d ["saturate"] = M (3, -1, -1, 0, 0, -1, 3, -1, 0, 0, -1, -1, 3, 0, 0, 0, 0, 0, 1, 0, -0.2f, 0.1f, 0.2f, 0, 1);
            d ["eps-offdiag"] = M (1, 1e-8f, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0.2f, 0, 0, 0, 0);
            d ["eps-diag"] = M (1.00000005f, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1);
            d ["eps-alpha"] = M (0.5f, 0, 0, 0, 0, 0, 0.5f, 0, 0, 0, 0, 0, 0.5f, 0, 0, 0, 0, 0, 1.0000001f, 0, 0, 0, 0, 0, 1);
            d ["column4"] = M (1, 0, 0, 0, 0.5f, 0, 1, 0, 0, 0.5f, 0, 0, 1, 0, 0.5f, 0, 0, 0, 1, 0.5f, 0, 0, 0, 0, 7);
            d ["half-steps"] = M (0.5f, 0, 0, 0, 0, 0, 0.5f, 0, 0, 0, 0, 0, 0.5f, 0, 0, 0, 0, 0, 0.5f, 0, 0.5f / 255, 1.5f / 255, 2.5f / 255, 0.5f / 255, 1);
            d ["huge"] = M (1000, 0, 0, 0, 0, 0, -1000, 0, 0, 0, 0, 0, 1e30f, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1);
            return d;
        }

        static readonly string[] s_greys = { "grey-translate", "grey-general" };

        static ColorMatrix Grey (string name)
        {
            if (name == "grey-translate") return M (1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0.25f, 0.1f, -0.1f, 0, 1);
            return M (0.2f, 0.1f, 0.9f, 0.1f, 0, 0.7f, 0.2f, 0, 0, 0, 0.1f, 0.6f, 0.05f, 0, 0, 0, 0, 0, 0.8f, 0, 0.05f, 0.1f, 0, 0, 1);
        }

        // ---- the cases -----------------------------------------------------------------------------

        public delegate byte[] Case ();

        static void Add (List<KeyValuePair<string, Case>> l, string name, Case c) { l.Add (new KeyValuePair<string, Case> (name, c)); }

        static byte[] Run (Case c)
        {
            try { return c (); }
            catch (Exception e) { return Encoding.ASCII.GetBytes ("EXC:" + e.GetType ().Name); }
        }

        /// <summary>Applies <paramref name="set"/> to a fresh ImageAttributes and draws with it; an
        /// exception from <paramref name="set"/> is recorded and the drawing still made.</summary>
        static byte[] With (Action<ImageAttributes> set, Func<ImageAttributes, byte[]> draw)
        {
            var ia = new ImageAttributes ();
            string err = null;
            try { set (ia); } catch (Exception e) { err = "EXC:" + e.GetType ().Name; }
            byte[] px = draw (ia);
            if (err == null) return px;
            byte[] e1 = Encoding.ASCII.GetBytes (err + "|");
            var r = new byte [e1.Length + px.Length];
            Buffer.BlockCopy (e1, 0, r, 0, e1.Length);
            Buffer.BlockCopy (px, 0, r, e1.Length, px.Length);
            return r;
        }

        static byte[] R (Action<ImageAttributes> set) => With (set, Recolor);

        public static List<KeyValuePair<string, Case>> Cases ()
        {
            var l = new List<KeyValuePair<string, Case>> ();
            Dictionary<string, ColorMatrix> ms = Matrices ();

            Add (l, "none", () => Recolor (null));
            Add (l, "empty", () => R (ia => { }));

            // the matrix, each flag, and the grey matrix
            foreach (KeyValuePair<string, ColorMatrix> kv in ms) {
                ColorMatrix m = kv.Value;
                Add (l, "matrix/" + kv.Key + "/default", () => R (ia => ia.SetColorMatrix (m)));
                Add (l, "matrix/" + kv.Key + "/skipgrays", () => R (ia => ia.SetColorMatrix (m, ColorMatrixFlag.SkipGrays)));
                foreach (string gn in s_greys) {
                    string gname = gn;
                    Add (l, "matrix/" + kv.Key + "/altgrays/" + gn, () => R (ia => ia.SetColorMatrices (m, Grey (gname), ColorMatrixFlag.AltGrays)));
                }
            }
            Add (l, "matrix/grey-ignored-default", () => R (ia => ia.SetColorMatrices (ms ["sepia"], Grey ("grey-general"), ColorMatrixFlag.Default)));
            Add (l, "matrix/grey-ignored-skip", () => R (ia => ia.SetColorMatrices (ms ["sepia"], Grey ("grey-general"), ColorMatrixFlag.SkipGrays)));
            Add (l, "matrix/altgrays-no-grey", () => R (ia => ia.SetColorMatrix (ms ["sepia"], ColorMatrixFlag.AltGrays)));
            Add (l, "matrix/flag3", () => R (ia => ia.SetColorMatrix (ms ["sepia"], (ColorMatrixFlag) 3)));
            Add (l, "matrix/null", () => R (ia => ia.SetColorMatrix (null)));
            Add (l, "matrix/null-null", () => R (ia => ia.SetColorMatrices (null, null)));
            Add (l, "matrix/replace", () => R (ia => { ia.SetColorMatrices (ms ["sepia"], Grey ("grey-general"), ColorMatrixFlag.AltGrays); ia.SetColorMatrix (ms ["diag"]); }));
            Add (l, "matrix/cleared", () => R (ia => { ia.SetColorMatrix (ms ["sepia"]); ia.ClearColorMatrix (); }));
            Add (l, "matrix/cleared-then-gamma", () => R (ia => { ia.SetColorMatrix (ms ["sepia"]); ia.ClearColorMatrix (); ia.SetGamma (1.7f); }));

            // threshold and gamma
            foreach (float t in new float [] { -0.1f, 0f, 0.001f, 0.3f, 0.5f, 0.75f, 1f, 1.5f }) {
                float tt = t;
                Add (l, "threshold/" + F (t), () => R (ia => ia.SetThreshold (tt)));
            }
            foreach (float gm in new float [] { 0.1f, 0.25f, 0.5f, 1f, 1.8f, 2.2f, 5f, 0f, -1f }) {
                float gg = gm;
                Add (l, "gamma/" + F (gm), () => R (ia => ia.SetGamma (gg)));
            }
            foreach (float t in new float [] { 0.2f, 0.5f, 0.9f })
                foreach (float gm in new float [] { 0.5f, 2.2f }) {
                    float tt = t, gg = gm;
                    Add (l, "threshold-gamma/" + F (t) + "/" + F (gm), () => R (ia => { ia.SetThreshold (tt); ia.SetGamma (gg); }));
                }
            Add (l, "gamma/cleared", () => R (ia => { ia.SetGamma (2f); ia.ClearGamma (); }));
            Add (l, "threshold/cleared", () => R (ia => { ia.SetThreshold (0.5f); ia.ClearThreshold (); }));
            Add (l, "gamma/matrix", () => R (ia => { ia.SetColorMatrix (ms ["general"]); ia.SetGamma (0.6f); }));
            Add (l, "threshold/matrix-altgrays", () => R (ia => { ia.SetColorMatrices (ms ["sepia"], Grey ("grey-general"), ColorMatrixFlag.AltGrays); ia.SetThreshold (0.4f); }));

            // NoOp
            Add (l, "noop", () => R (ia => ia.SetNoOp ()));
            Add (l, "noop/matrix", () => R (ia => { ia.SetColorMatrix (ms ["sepia"]); ia.SetNoOp (); }));
            Add (l, "noop/everything", () => R (ia => { ia.SetColorMatrix (ms ["sepia"]); ia.SetGamma (2f); ia.SetThreshold (0.3f); ia.SetColorKey (Color.FromArgb (0, 0, 0), Color.FromArgb (100, 100, 100)); ia.SetOutputChannel (ColorChannelFlag.ColorChannelM); ia.SetNoOp (); }));
            Add (l, "noop/cleared", () => R (ia => { ia.SetColorMatrix (ms ["sepia"]); ia.SetNoOp (); ia.ClearNoOp (); }));
            Add (l, "noop/bitmap-over-default", () => R (ia => { ia.SetColorMatrix (ms ["sepia"]); ia.SetNoOp (ColorAdjustType.Bitmap); }));

            // colour key and remap table
            Add (l, "colorkey/range", () => R (ia => ia.SetColorKey (Color.FromArgb (0, 0, 0), Color.FromArgb (120, 140, 160))));
            Add (l, "colorkey/one", () => R (ia => ia.SetColorKey (Color.FromArgb (10, 200, 30), Color.FromArgb (10, 200, 30))));
            Add (l, "colorkey/alpha-ignored", () => R (ia => ia.SetColorKey (Color.FromArgb (7, 0, 0, 0), Color.FromArgb (3, 255, 128, 255))));
            Add (l, "colorkey/inverted", () => R (ia => ia.SetColorKey (Color.FromArgb (200, 0, 0), Color.FromArgb (100, 255, 255))));
            Add (l, "colorkey/matrix", () => R (ia => { ia.SetColorKey (Color.FromArgb (0, 0, 0), Color.FromArgb (128, 128, 128)); ia.SetColorMatrix (ms ["colour-from-alpha"]); }));
            Add (l, "colorkey/cleared", () => R (ia => { ia.SetColorKey (Color.FromArgb (0, 0, 0), Color.FromArgb (255, 255, 255)); ia.ClearColorKey (); }));
            Add (l, "remap/one", () => R (ia => ia.SetRemapTable (new [] { Map (0xff0ac81e, 0xffff0000) })));
            Add (l, "remap/many", () => R (ia => ia.SetRemapTable (new [] {
                Map (0xff000000, 0x80102030), Map (0xffffffff, 0x00000000), Map (0x800ac81e, 0xff00ff00),
                Map (0x00323c46, 0xffabcdef), Map (0xff808080, 0xff0000ff), Map (0xff808080, 0xffff0000), Map (0x01ffffff, 0xff123456) })));
            Add (l, "remap/chain", () => R (ia => ia.SetRemapTable (new [] { Map (0xff000000, 0xffffffff), Map (0xffffffff, 0xff000000) })));
            Add (l, "remap/matrix-key", () => R (ia => { ia.SetRemapTable (new [] { Map (0xff0ac81e, 0xff000000) }); ia.SetColorKey (Color.FromArgb (0, 0, 0), Color.FromArgb (5, 5, 5)); ia.SetColorMatrix (ms ["invert"]); }));
            Add (l, "remap/empty", () => R (ia => ia.SetRemapTable (Array.Empty<ColorMap> ())));
            Add (l, "remap/null", () => R (ia => ia.SetRemapTable ((ColorMap[]) null)));
            Add (l, "remap/cleared", () => R (ia => { ia.SetRemapTable (new [] { Map (0xff000000, 0xffff0000) }); ia.ClearRemapTable (); }));
            Add (l, "remap/brush-only", () => R (ia => ia.SetBrushRemapTable (new [] { Map (0xff000000, 0xffff0000) })));

            // the output channel
            foreach (ColorChannelFlag ch in new [] { ColorChannelFlag.ColorChannelC, ColorChannelFlag.ColorChannelM, ColorChannelFlag.ColorChannelY, ColorChannelFlag.ColorChannelK }) {
                ColorChannelFlag c = ch;
                Add (l, "channel/" + ch, () => R (ia => ia.SetOutputChannel (c)));
                Add (l, "channel/" + ch + "/matrix", () => R (ia => { ia.SetColorMatrix (ms ["general"]); ia.SetOutputChannel (c); }));
                Add (l, "channel/" + ch + "/gamma", () => R (ia => { ia.SetGamma (0.45f); ia.SetOutputChannel (c); }));
                Add (l, "channel/" + ch + "/bitmap", () => R (ia => ia.SetOutputChannel (c, ColorAdjustType.Bitmap)));
                Add (l, "channel/" + ch + "/brush", () => R (ia => ia.SetOutputChannel (c, ColorAdjustType.Brush)));
            }
            Add (l, "channel/last", () => R (ia => ia.SetOutputChannel (ColorChannelFlag.ColorChannelLast)));
            Add (l, "channel/-1", () => R (ia => ia.SetOutputChannel ((ColorChannelFlag) (-1))));
            Add (l, "channel/cleared", () => R (ia => { ia.SetOutputChannel (ColorChannelFlag.ColorChannelY); ia.ClearOutputChannel (); }));
            Add (l, "channel/cleared-by-profile-clear", () => R (ia => { ia.SetOutputChannel (ColorChannelFlag.ColorChannelY); ia.ClearOutputChannelColorProfile (); }));
            Add (l, "channel/replaced", () => R (ia => { ia.SetOutputChannel (ColorChannelFlag.ColorChannelY); ia.SetOutputChannel (ColorChannelFlag.ColorChannelK); }));
            Add (l, "channel/everything", () => R (ia => {
                ia.SetRemapTable (new [] { Map (0xff0ac81e, 0xff204060) });
                ia.SetColorKey (Color.FromArgb (0, 0, 0), Color.FromArgb (20, 20, 20));
                ia.SetColorMatrices (ms ["sepia"], Grey ("grey-general"), ColorMatrixFlag.AltGrays);
                ia.SetGamma (1.3f); ia.SetThreshold (0.6f);
                ia.SetOutputChannel (ColorChannelFlag.ColorChannelC);
            }));
            // ... and the colour profile it would separate through
            string srgb = Path.Combine (Environment.GetFolderPath (Environment.SpecialFolder.System), "spool", "drivers", "color", "sRGB Color Space Profile.icm");
            Add (l, "profile/missing", () => R (ia => ia.SetOutputChannelColorProfile ("Z:\\no\\such\\profile.icm")));
            Add (l, "profile/missing-then-channel", () => R (ia => { try { ia.SetOutputChannelColorProfile ("Z:\\no\\such\\profile.icm"); } catch (Exception) { } ia.SetOutputChannel (ColorChannelFlag.ColorChannelM); }));
            Add (l, "profile/null", () => R (ia => ia.SetOutputChannelColorProfile (null)));
            Add (l, "profile/empty", () => R (ia => ia.SetOutputChannelColorProfile ("")));
            Add (l, "profile/bad-path", () => R (ia => ia.SetOutputChannelColorProfile ("a\0b")));
            Add (l, "profile/srgb", () => R (ia => ia.SetOutputChannelColorProfile (srgb)));
            Add (l, "profile/srgb-channel", () => R (ia => { try { ia.SetOutputChannelColorProfile (srgb); } catch (Exception) { } ia.SetOutputChannel (ColorChannelFlag.ColorChannelK); }));
            Add (l, "profile/not-a-profile", () => R (ia => ia.SetOutputChannelColorProfile (typeof (ImageAttributesBattery).Assembly.Location)));
            Add (l, "profile/cleared", () => R (ia => ia.ClearOutputChannelColorProfile ()));
            Add (l, "profile/type-any", () => R (ia => ia.SetOutputChannelColorProfile ("Z:\\x.icm", ColorAdjustType.Any)));

            // which object an image uses
            Add (l, "type/bitmap", () => R (ia => ia.SetColorMatrix (ms ["sepia"], ColorMatrixFlag.Default, ColorAdjustType.Bitmap)));
            Add (l, "type/bitmap-over-default", () => R (ia => { ia.SetColorMatrix (ms ["sepia"]); ia.SetGamma (2.5f, ColorAdjustType.Bitmap); }));
            Add (l, "type/bitmap-cleared", () => R (ia => { ia.SetColorMatrix (ms ["sepia"]); ia.SetGamma (2.5f, ColorAdjustType.Bitmap); ia.ClearGamma (ColorAdjustType.Bitmap); }));
            Add (l, "type/default-cleared", () => R (ia => { ia.SetColorMatrix (ms ["sepia"]); ia.ClearColorMatrix (ColorAdjustType.Bitmap); }));
            foreach (ColorAdjustType t in new [] { ColorAdjustType.Brush, ColorAdjustType.Pen, ColorAdjustType.Text }) {
                ColorAdjustType tt = t;
                Add (l, "type/" + t, () => R (ia => { ia.SetColorMatrix (ms ["sepia"], ColorMatrixFlag.Default, tt); ia.SetGamma (2.5f, tt); }));
            }
            Add (l, "type/any", () => R (ia => ia.SetColorMatrix (ms ["sepia"], ColorMatrixFlag.Default, ColorAdjustType.Any)));
            Add (l, "type/count", () => R (ia => ia.SetGamma (2f, ColorAdjustType.Count)));
            Add (l, "type/7", () => R (ia => ia.SetThreshold (0.5f, (ColorAdjustType) 7)));
            Add (l, "type/clear-any", () => R (ia => { ia.SetGamma (2f); ia.ClearGamma (ColorAdjustType.Any); }));
            Add (l, "type/clear-never-set", () => R (ia => { ia.ClearColorMatrix (ColorAdjustType.Bitmap); ia.ClearNoOp (); ia.ClearOutputChannel (ColorAdjustType.Pen); }));
            Add (l, "type/gamma-refused-makes-object", () => R (ia => { ia.SetColorMatrix (ms ["sepia"]); try { ia.SetGamma (-2f, ColorAdjustType.Bitmap); } catch (Exception) { } }));

            // Clone
            Add (l, "clone", () => {
                var ia = new ImageAttributes ();
                ia.SetColorMatrices (ms ["sepia"], Grey ("grey-general"), ColorMatrixFlag.AltGrays);
                ia.SetGamma (1.6f, ColorAdjustType.Bitmap);
                ia.SetRemapTable (new [] { Map (0xff0ac81e, 0xffff0000) });
                ia.SetWrapMode (WrapMode.TileFlipX);
                var c = (ImageAttributes) ia.Clone ();
                ia.ClearGamma (ColorAdjustType.Bitmap);
                ia.SetColorMatrix (ms ["invert"]);
                return Recolor (c);
            });
            Add (l, "clone/used-then-changed", () => {
                var ia = new ImageAttributes ();
                ia.SetGamma (2.2f);
                byte[] first = Recolor (ia);
                var c = (ImageAttributes) ia.Clone ();
                c.SetThreshold (0.5f);
                byte[] second = Recolor (c);
                byte[] third = Recolor (ia);
                var r = new byte [first.Length * 3];
                Buffer.BlockCopy (first, 0, r, 0, first.Length);
                Buffer.BlockCopy (second, 0, r, first.Length, first.Length);
                Buffer.BlockCopy (third, 0, r, 2 * first.Length, first.Length);
                return r;
            });

            // GetAdjustedPalette
            Add (l, "palette/bitmap", () => Palette (ia => ia.SetColorMatrix (ms ["sepia"]), ColorAdjustType.Bitmap));
            Add (l, "palette/default", () => Palette (ia => ia.SetGamma (2f), ColorAdjustType.Default));
            Add (l, "palette/brush", () => Palette (ia => ia.SetThreshold (0.5f, ColorAdjustType.Brush), ColorAdjustType.Brush));
            Add (l, "palette/any", () => Palette (ia => { }, ColorAdjustType.Any));
            Add (l, "palette/null", () => { var ia = new ImageAttributes (); try { ia.GetAdjustedPalette (null, ColorAdjustType.Bitmap); return new byte [] { 1 }; } catch (Exception e) { return Encoding.ASCII.GetBytes ("EXC:" + e.GetType ().Name); } });

            // source formats
            foreach (string fmt in new [] { "argb", "rgb24", "pargb", "i8", "i4", "i1", "565" })
                foreach (string adj in new [] { "none", "sepia", "gamma", "remap", "key", "channel" }) {
                    string f = fmt, a = adj;
                    Add (l, "format/" + fmt + "/" + adj, () => FormatCase (f, a));
                }

            // wrap modes
            InterpolationMode[] ims = { InterpolationMode.NearestNeighbor, InterpolationMode.Bilinear, InterpolationMode.Bicubic, InterpolationMode.HighQualityBilinear, InterpolationMode.HighQualityBicubic };
            var wraps = new List<KeyValuePair<string, Action<ImageAttributes>>> {
                new KeyValuePair<string, Action<ImageAttributes>> ("none", null),
                new KeyValuePair<string, Action<ImageAttributes>> ("tile", ia => ia.SetWrapMode (WrapMode.Tile)),
                new KeyValuePair<string, Action<ImageAttributes>> ("flipx", ia => ia.SetWrapMode (WrapMode.TileFlipX)),
                new KeyValuePair<string, Action<ImageAttributes>> ("flipy", ia => ia.SetWrapMode (WrapMode.TileFlipY)),
                new KeyValuePair<string, Action<ImageAttributes>> ("flipxy", ia => ia.SetWrapMode (WrapMode.TileFlipXY)),
                new KeyValuePair<string, Action<ImageAttributes>> ("clamp", ia => ia.SetWrapMode (WrapMode.Clamp)),
                new KeyValuePair<string, Action<ImageAttributes>> ("clamp-colour", ia => ia.SetWrapMode (WrapMode.Clamp, Color.FromArgb (200, 30, 160, 90))),
                new KeyValuePair<string, Action<ImageAttributes>> ("clamp-opaque", ia => ia.SetWrapMode (WrapMode.Clamp, Color.Teal)),
                new KeyValuePair<string, Action<ImageAttributes>> ("clamp-srcrect", ia => ia.SetWrapMode (WrapMode.Clamp, Color.FromArgb (255, 250, 20, 20), true)),
                new KeyValuePair<string, Action<ImageAttributes>> ("tile-srcrect", ia => ia.SetWrapMode (WrapMode.Tile, Color.Red, true)),
                new KeyValuePair<string, Action<ImageAttributes>> ("flipxy-srcrect", ia => ia.SetWrapMode (WrapMode.TileFlipXY, Color.Red, true)),
                new KeyValuePair<string, Action<ImageAttributes>> ("tile-gamma", ia => { ia.SetWrapMode (WrapMode.Tile); ia.SetGamma (0.6f); }),
                new KeyValuePair<string, Action<ImageAttributes>> ("clamp-matrix", ia => { ia.SetWrapMode (WrapMode.Clamp, Color.FromArgb (255, 0, 0, 255)); ia.SetColorMatrix (Matrices () ["invert"]); }),
                new KeyValuePair<string, Action<ImageAttributes>> ("invalid", ia => ia.SetWrapMode ((WrapMode) 9)),
            };
            foreach (InterpolationMode im in ims)
                foreach (KeyValuePair<string, Action<ImageAttributes>> w in wraps) {
                    InterpolationMode i = im; Action<ImageAttributes> set = w.Value;
                    string k = im + "/" + w.Key;
                    Add (l, "wrap/" + k + "/out", () => With (ia => set?.Invoke (ia), ia => Draw (64, 48, 1, g => {
                        g.InterpolationMode = i;
                        g.DrawImage (Rgb24 (), new Rectangle (2, 2, 50, 40), -5, -3, 25, 20, GraphicsUnit.Pixel, set == null ? null : ia);
                    })));
                    Add (l, "wrap/" + k + "/in", () => With (ia => set?.Invoke (ia), ia => Draw (48, 40, 0, g => {
                        g.InterpolationMode = i;
                        g.DrawImage (Argb (), new Rectangle (1, 1, 40, 30), 2.5f, 1.5f, 9, 7, GraphicsUnit.Pixel, set == null ? null : ia);
                        g.DrawImage (Rgb24 (), new Rectangle (30, 25, 6, 5), 0, 0, 10, 8, GraphicsUnit.Pixel, set == null ? null : ia);
                    })));
                    Add (l, "wrap/" + k + "/turned", () => With (ia => set?.Invoke (ia), ia => Draw (48, 40, 1, g => {
                        g.InterpolationMode = i;
                        g.TranslateTransform (24, 20); g.RotateTransform (25);
                        g.DrawImage (Tile (), new Rectangle (-15, -12, 30, 24), -2, -1, 9, 6, GraphicsUnit.Pixel, set == null ? null : ia);
                    })));
                }

            // a TextureBrush made with ImageAttributes
            foreach (string adj in new [] { "sepia", "gamma", "remap", "channel", "bitmap-type", "brush-type", "wrap" }) {
                string a = adj;
                Add (l, "texture/" + adj, () => Draw (40, 30, 1, g => {
                    var ia = new ImageAttributes ();
                    switch (a) {
                    case "sepia": ia.SetColorMatrix (Matrices () ["sepia"]); break;
                    case "gamma": ia.SetGamma (0.4f); break;
                    case "remap": ia.SetRemapTable (new [] { Map (0xff0ac81e, 0xff0000ff) }); break;
                    case "channel": ia.SetOutputChannel (ColorChannelFlag.ColorChannelY); break;
                    case "bitmap-type": ia.SetColorMatrix (Matrices () ["invert"], ColorMatrixFlag.Default, ColorAdjustType.Bitmap); break;
                    case "brush-type": ia.SetColorMatrix (Matrices () ["invert"], ColorMatrixFlag.Default, ColorAdjustType.Brush); break;
                    case "wrap": ia.SetWrapMode (WrapMode.TileFlipXY); break;
                    }
                    using (var tb = new TextureBrush (Rgb24 (), new Rectangle (1, 1, 7, 5), ia))
                        g.FillRectangle (tb, 2, 2, 36, 26);
                }));
            }

            // metafiles played with ImageAttributes
            var mfAdj = new List<KeyValuePair<string, Action<ImageAttributes>>> {
                new KeyValuePair<string, Action<ImageAttributes>> ("none", ia => { }),
                new KeyValuePair<string, Action<ImageAttributes>> ("default-sepia", ia => ia.SetColorMatrix (Matrices () ["sepia"])),
                new KeyValuePair<string, Action<ImageAttributes>> ("default-gamma", ia => ia.SetGamma (0.5f)),
                new KeyValuePair<string, Action<ImageAttributes>> ("default-channel", ia => ia.SetOutputChannel (ColorChannelFlag.ColorChannelM)),
                new KeyValuePair<string, Action<ImageAttributes>> ("brush-invert", ia => ia.SetColorMatrix (Matrices () ["invert"], ColorMatrixFlag.Default, ColorAdjustType.Brush)),
                new KeyValuePair<string, Action<ImageAttributes>> ("brush-altgrays", ia => ia.SetColorMatrices (Matrices () ["sepia"], Grey ("grey-general"), ColorMatrixFlag.AltGrays, ColorAdjustType.Brush)),
                new KeyValuePair<string, Action<ImageAttributes>> ("brush-remap", ia => ia.SetBrushRemapTable (new [] { Map (0xffff0000, 0xff00ff00), Map (0xff000000, 0xffff00ff), Map (0xff0000ff, 0x80ffff00) })),
                new KeyValuePair<string, Action<ImageAttributes>> ("brush-threshold", ia => ia.SetThreshold (0.5f, ColorAdjustType.Brush)),
                new KeyValuePair<string, Action<ImageAttributes>> ("pen-invert", ia => ia.SetColorMatrix (Matrices () ["invert"], ColorMatrixFlag.Default, ColorAdjustType.Pen)),
                new KeyValuePair<string, Action<ImageAttributes>> ("pen-key", ia => ia.SetColorKey (Color.FromArgb (0, 0, 0), Color.FromArgb (255, 40, 40), ColorAdjustType.Pen)),
                new KeyValuePair<string, Action<ImageAttributes>> ("text-invert", ia => ia.SetColorMatrix (Matrices () ["invert"], ColorMatrixFlag.Default, ColorAdjustType.Text)),
                new KeyValuePair<string, Action<ImageAttributes>> ("bitmap-invert", ia => ia.SetColorMatrix (Matrices () ["invert"], ColorMatrixFlag.Default, ColorAdjustType.Bitmap)),
                new KeyValuePair<string, Action<ImageAttributes>> ("brush-noop-default-invert", ia => { ia.SetColorMatrix (Matrices () ["invert"]); ia.SetNoOp (ColorAdjustType.Brush); }),
                new KeyValuePair<string, Action<ImageAttributes>> ("wrap", ia => ia.SetWrapMode (WrapMode.TileFlipXY)),
            };
            foreach (string file in new [] { "plus/fillrect_int", "plus/fills", "plus/gradients", "plus/pens", "plus/images", "plus/text_lines", "emfonly/fills", "emfonly/strokes", "emfonly/images", "emfonly/text_lines", "dual/fills" })
                foreach (KeyValuePair<string, Action<ImageAttributes>> a in mfAdj) {
                    string fl = file; Action<ImageAttributes> set = a.Value;
                    Add (l, "metafile/" + file + "/" + a.Key, () => With (set, ia => PlayMetafile (() => new Metafile (Path.Combine (MetafileDir, fl + ".emf")), ia)));
                }
            // GDI records written here, for every colour and DIB the down-level recolour rewrites.
            foreach (KeyValuePair<string, Action<ImageAttributes>> a in mfAdj) {
                Action<ImageAttributes> set = a.Value;
                Add (l, "metafile/gdi-emf/" + a.Key, () => With (set, ia => PlayMetafile (() => new Metafile (new MemoryStream (GdiEmf ())), ia)));
                Add (l, "metafile/gdi-wmf/" + a.Key, () => With (set, ia => PlayMetafile (() => new Metafile (new MemoryStream (GdiWmf ())), ia)));
            }
            return l;
        }

        static byte[] PlayMetafile (Func<Metafile> open, ImageAttributes ia)
        {
            return Draw (120, 90, 1, g => {
                using (Metafile mf = open ()) {
                    GraphicsUnit u = GraphicsUnit.Pixel;
                    RectangleF b = mf.GetBounds (ref u);
                    g.DrawImage (mf, new [] { new PointF (5, 5), new PointF (115, 5), new PointF (5, 85) }, b, GraphicsUnit.Pixel, ia);
                }
            });
        }

        // ---- metafiles of GDI records, written by hand ---------------------------------------------

        sealed class Rec
        {
            readonly MemoryStream _m = new MemoryStream ();
            public void I32 (int v) { _m.WriteByte ((byte) v); _m.WriteByte ((byte) (v >> 8)); _m.WriteByte ((byte) (v >> 16)); _m.WriteByte ((byte) (v >> 24)); }
            public void I16 (int v) { _m.WriteByte ((byte) v); _m.WriteByte ((byte) (v >> 8)); }
            public void Bytes (byte[] b) { _m.Write (b, 0, b.Length); }
            public int Length => (int) _m.Length;
            public byte[] ToArray () => _m.ToArray ();
        }

        /// <summary>A DIB (BITMAPINFOHEADER, colour table or masks, bits) of the given depth, its
        /// pixels a pattern, its palette (if any) colourful or black-and-white.</summary>
        static byte[] Dib (int w, int h, int bpp, int comp, bool blackWhite, out int cbInfo)
        {
            int pal = bpp <= 8 ? 1 << bpp : comp == 3 ? 3 : 0;
            var r = new Rec ();
            r.I32 (40); r.I32 (w); r.I32 (h); r.I16 (1); r.I16 (bpp); r.I32 (comp); r.I32 (0); r.I32 (0); r.I32 (0); r.I32 (0); r.I32 (0);
            for (int i = 0; i < pal; i++) {
                if (comp == 3) r.I32 (bpp == 16 ? (i == 0 ? 0xf800 : i == 1 ? 0x7e0 : 0x1f) : (i == 0 ? 0xff0000 : i == 1 ? 0xff00 : 0xff));
                else if (blackWhite) r.I32 (i == 0 ? 0 : 0xffffff);
                else r.I32 (((i * 70) & 255) << 16 | ((255 - i * 50) & 255) << 8 | ((i * 30 + 10) & 255));
            }
            cbInfo = r.Length;
            int stride = (w * bpp + 31) / 32 * 4;
            var bits = new byte [stride * h];
            for (int i = 0; i < bits.Length; i++) bits [i] = (byte) (i * 37 + 11 + i / stride * 5);
            r.Bytes (bits);
            return r.ToArray ();
        }

        static byte[] Emr (int type, Action<Rec> body)
        {
            var b = new Rec ();
            body (b);
            byte[] p = b.ToArray ();
            var r = new Rec ();
            r.I32 (type); r.I32 (8 + p.Length); r.Bytes (p);
            return r.ToArray ();
        }

        static void Rect (Rec r, int l, int t, int rr, int bb) { r.I32 (l); r.I32 (t); r.I32 (rr); r.I32 (bb); }

        static byte[] s_gdiEmf, s_gdiWmf;

        /// <summary>An EMF of GDI records: text and background colours, pens (CreatePen and
        /// ExtCreatePen), solid, hatched and null brushes, stock objects, StretchDIBits of every
        /// depth (black-and-white and colour monochrome, a 16bpp DIB small enough and one too big to
        /// rewrite, masks), BitBlt of a DIB, and a DIB pattern brush.</summary>
        static byte[] GdiEmf ()
        {
            if (s_gdiEmf != null) return s_gdiEmf;
            var recs = new List<byte[]> ();
            recs.Add (Emr (17, r => r.I32 (1)));                                   // SETMAPMODE MM_TEXT
            recs.Add (Emr (18, r => r.I32 (2)));                                   // SETBKMODE OPAQUE
            recs.Add (Emr (25, r => r.I32 (0x3060c0)));                           // SETBKCOLOR
            recs.Add (Emr (24, r => r.I32 (0x10a030)));                           // SETTEXTCOLOR
            recs.Add (Emr (38, r => { r.I32 (1); r.I32 (0); r.I32 (3); r.I32 (0); r.I32 (0x2080f0); }));      // CREATEPEN solid 3
            recs.Add (Emr (39, r => { r.I32 (2); r.I32 (0); r.I32 (0x40c060); r.I32 (0); }));                 // CREATEBRUSHINDIRECT solid
            recs.Add (Emr (37, r => r.I32 (1)));
            recs.Add (Emr (37, r => r.I32 (2)));
            recs.Add (Emr (43, r => Rect (r, 2, 2, 30, 20)));                     // RECTANGLE
            recs.Add (Emr (39, r => { r.I32 (3); r.I32 (2); r.I32 (0x0000c0); r.I32 (3); }));                 // hatched (cross)
            recs.Add (Emr (37, r => r.I32 (3)));
            recs.Add (Emr (42, r => Rect (r, 32, 2, 60, 20)));                    // ELLIPSE
            recs.Add (Emr (37, r => r.I32 (unchecked ((int) 0x80000002))));       // GRAY_BRUSH
            recs.Add (Emr (37, r => r.I32 (unchecked ((int) 0x80000006))));       // WHITE_PEN
            recs.Add (Emr (43, r => Rect (r, 62, 2, 90, 20)));
            recs.Add (Emr (37, r => r.I32 (unchecked ((int) 0x80000004))));       // BLACK_BRUSH
            recs.Add (Emr (37, r => r.I32 (unchecked ((int) 0x80000007))));       // BLACK_PEN
            recs.Add (Emr (42, r => Rect (r, 92, 2, 118, 20)));
            recs.Add (Emr (95, r => {                                             // EXTCREATEPEN geometric solid 4
                r.I32 (4); r.I32 (52); r.I32 (0); r.I32 (52); r.I32 (0);
                r.I32 (0x10000 | 0x200); r.I32 (4); r.I32 (0); r.I32 (0xc08020); r.I32 (0); r.I32 (0);
            }));
            recs.Add (Emr (39, r => { r.I32 (5); r.I32 (1); r.I32 (0); r.I32 (0); }));                        // BS_NULL
            recs.Add (Emr (37, r => r.I32 (4)));
            recs.Add (Emr (37, r => r.I32 (5)));
            recs.Add (Emr (43, r => Rect (r, 4, 24, 28, 40)));
            // StretchDIBits of every depth.
            int x = 2;
            foreach (var (w, h, bpp, comp, bw, rop) in new [] {
                (8, 6, 1, 0, true, 0xcc0020), (8, 6, 1, 0, true, 0x8800c6), (8, 6, 1, 0, false, 0x8800c6), (7, 5, 4, 0, false, 0xcc0020),
                (6, 5, 8, 0, false, 0xcc0020), (4, 2, 16, 0, false, 0xcc0020), (9, 7, 16, 0, false, 0xcc0020), (5, 4, 16, 3, false, 0xcc0020),
                (6, 5, 24, 0, false, 0xcc0020), (6, 5, 32, 0, false, 0xcc0020), (5, 4, 32, 3, false, 0xee0086) }) {
                byte[] dib = Dib (w, h, bpp, comp, bw, out int cbInfo);
                int xd = x, ww = w, hh = h, rp = rop;
                recs.Add (Emr (81, r => {
                    Rect (r, xd, 44, xd + 9, 58);
                    r.I32 (xd); r.I32 (44); r.I32 (0); r.I32 (0); r.I32 (ww); r.I32 (hh);
                    r.I32 (80); r.I32 (cbInfo); r.I32 (80 + cbInfo); r.I32 (dib.Length - cbInfo); r.I32 (0); r.I32 (rp); r.I32 (9); r.I32 (14);
                    r.Bytes (dib);
                }));
                x += 10;
            }
            // BitBlt of a DIB.
            {
                byte[] dib = Dib (10, 8, 8, 0, false, out int cbInfo);
                recs.Add (Emr (76, r => {
                    Rect (r, 2, 62, 12, 70);
                    r.I32 (2); r.I32 (62); r.I32 (10); r.I32 (8); r.I32 (0xcc0020); r.I32 (0); r.I32 (0);
                    r.I32 (0x3f800000); r.I32 (0); r.I32 (0); r.I32 (0x3f800000); r.I32 (0); r.I32 (0);
                    r.I32 (0); r.I32 (0); r.I32 (100); r.I32 (cbInfo); r.I32 (100 + cbInfo); r.I32 (dib.Length - cbInfo);
                    r.Bytes (dib);
                }));
            }
            // A DIB pattern brush.
            {
                byte[] dib = Dib (8, 8, 4, 0, false, out int cbInfo);
                recs.Add (Emr (94, r => { r.I32 (6); r.I32 (0); r.I32 (32); r.I32 (cbInfo); r.I32 (32 + cbInfo); r.I32 (dib.Length - cbInfo); r.Bytes (dib); }));
                recs.Add (Emr (37, r => r.I32 (6)));
                recs.Add (Emr (43, r => Rect (r, 16, 62, 60, 80)));
            }
            recs.Add (Emr (14, r => { r.I32 (0); r.I32 (16); r.I32 (20); }));    // EOF
            int bytes = 108;
            foreach (byte[] b in recs) bytes += b.Length;
            var h0 = new Rec ();
            h0.I32 (1); h0.I32 (108);
            Rect (h0, 0, 0, 119, 89);                                              // bounds
            Rect (h0, 0, 0, 3175, 2381);                                           // frame, .01mm at 96 dpi
            h0.I32 (0x464d4520); h0.I32 (0x10000); h0.I32 (bytes); h0.I32 (recs.Count + 1); h0.I16 (8); h0.I16 (0);
            h0.I32 (0); h0.I32 (0); h0.I32 (0);
            h0.I32 (1920); h0.I32 (1080); h0.I32 (508); h0.I32 (286);
            h0.I32 (0); h0.I32 (0); h0.I32 (0); h0.I32 (508000); h0.I32 (285750);
            var all = new Rec ();
            all.Bytes (h0.ToArray ());
            foreach (byte[] b in recs) all.Bytes (b);
            return s_gdiEmf = all.ToArray ();
        }

        static byte[] Wmr (int fn, Action<Rec> body)
        {
            var b = new Rec ();
            body (b);
            byte[] p = b.ToArray ();
            var r = new Rec ();
            r.I32 ((6 + p.Length + 1) / 2); r.I16 (fn); r.Bytes (p);
            if ((p.Length & 1) != 0) r.Bytes (new byte [1]);
            return r.ToArray ();
        }

        /// <summary>A placeable WMF of the same kinds of records (no stock objects in a WMF):
        /// background and text colours, solid, null and cosmetic pens, solid and null brushes.</summary>
        static byte[] GdiWmf ()
        {
            if (s_gdiWmf != null) return s_gdiWmf;
            var recs = new List<byte[]> ();
            recs.Add (Wmr (0x020B, r => { r.I16 (0); r.I16 (0); }));                 // SETWINDOWORG
            recs.Add (Wmr (0x020C, r => { r.I16 (90); r.I16 (120); }));              // SETWINDOWEXT
            recs.Add (Wmr (0x0102, r => r.I16 (2)));                                 // SETBKMODE OPAQUE
            recs.Add (Wmr (0x0201, r => r.I32 (0x3060c0)));                          // SETBKCOLOR
            recs.Add (Wmr (0x0209, r => r.I32 (0x10a030)));                          // SETTEXTCOLOR
            recs.Add (Wmr (0x02FA, r => { r.I16 (0); r.I16 (3); r.I16 (0); r.I32 (0x2080f0); }));      // pen 0
            recs.Add (Wmr (0x02FC, r => { r.I16 (0); r.I32 (0x40c060); r.I16 (0); }));                 // brush 1 solid
            recs.Add (Wmr (0x012D, r => r.I16 (0)));
            recs.Add (Wmr (0x012D, r => r.I16 (1)));
            recs.Add (Wmr (0x041B, r => { r.I16 (20); r.I16 (30); r.I16 (2); r.I16 (2); }));          // RECTANGLE b r t l
            recs.Add (Wmr (0x02FA, r => { r.I16 (5); r.I16 (1); r.I16 (0); r.I32 (0x123456); }));      // pen 2 PS_NULL
            recs.Add (Wmr (0x02FC, r => { r.I16 (0); r.I32 (0xc08020); r.I16 (0); }));                 // brush 3 solid
            recs.Add (Wmr (0x012D, r => r.I16 (2)));
            recs.Add (Wmr (0x012D, r => r.I16 (3)));
            recs.Add (Wmr (0x0418, r => { r.I16 (20); r.I16 (60); r.I16 (2); r.I16 (32); }));         // ELLIPSE
            recs.Add (Wmr (0x02FA, r => { r.I16 (0); r.I16 (1); r.I16 (0); r.I32 (0x0000ff); }));      // pen 4 cosmetic red
            recs.Add (Wmr (0x02FC, r => { r.I16 (1); r.I32 (0x00ff00); r.I16 (0); }));                 // brush 5 BS_NULL
            recs.Add (Wmr (0x012D, r => r.I16 (4)));
            recs.Add (Wmr (0x012D, r => r.I16 (5)));
            recs.Add (Wmr (0x041B, r => { r.I16 (50); r.I16 (100); r.I16 (26); r.I16 (8); }));
            recs.Add (Wmr (0x02FC, r => { r.I16 (0); r.I32 (0x808080); r.I16 (0); }));                 // brush 6 grey
            recs.Add (Wmr (0x012D, r => r.I16 (6)));
            recs.Add (Wmr (0x041B, r => { r.I16 (86); r.I16 (60); r.I16 (56); r.I16 (14); }));
            // (A WMF's DIBs and hatches are left out: the port's WMF stretch and hatch phase are not
            // yet GDI's, recoloured or not.)
            recs.Add (Wmr (0x0000, r => { }));                                         // EOF
            int words = 9, maxRec = 0;
            foreach (byte[] b in recs) { words += b.Length / 2; maxRec = Math.Max (maxRec, b.Length / 2); }
            var all = new Rec ();
            // placeable header: key, handle, bbox, inch, reserved, checksum
            var ph = new Rec ();
            ph.I32 (unchecked ((int) 0x9AC6CDD7)); ph.I16 (0); ph.I16 (0); ph.I16 (0); ph.I16 (120); ph.I16 (90); ph.I16 (96); ph.I32 (0);
            byte[] p = ph.ToArray ();
            int sum = 0;
            for (int i = 0; i < 20; i += 2) sum ^= p [i] | p [i + 1] << 8;
            all.Bytes (p); all.I16 (sum);
            all.I16 (1); all.I16 (9); all.I16 (0x300); all.I32 (words); all.I16 (8); all.I32 (maxRec); all.I16 (0);
            foreach (byte[] b in recs) all.Bytes (b);
            return s_gdiWmf = all.ToArray ();
        }

        static ColorMap Map (uint from, uint to)
        {
            var m = new ColorMap ();
            m.OldColor = Color.FromArgb (unchecked ((int) from));
            m.NewColor = Color.FromArgb (unchecked ((int) to));
            return m;
        }

        static string F (float f) { return f.ToString ("0.###", System.Globalization.CultureInfo.InvariantCulture); }

        static byte[] Palette (Action<ImageAttributes> set, ColorAdjustType type)
        {
            var ia = new ImageAttributes ();
            set (ia);
            using (var b = new Bitmap (2, 2, PixelFormat.Format8bppIndexed)) {
                ColorPalette p = b.Palette;
                for (int i = 0; i < p.Entries.Length; i++) p.Entries [i] = Color.FromArgb ((i * 7) & 255, i, 255 - i, (i * 3) & 255);
                try { ia.GetAdjustedPalette (p, type); } catch (Exception e) { return Encoding.ASCII.GetBytes ("EXC:" + e.GetType ().Name); }
                var o = new MemoryStream ();
                o.WriteByte ((byte) p.Flags);
                foreach (Color c in p.Entries) { int v = c.ToArgb (); o.WriteByte ((byte) v); o.WriteByte ((byte) (v >> 8)); o.WriteByte ((byte) (v >> 16)); o.WriteByte ((byte) (v >> 24)); }
                return o.ToArray ();
            }
        }

        static byte[] FormatCase (string fmt, string adj)
        {
            Bitmap src;
            switch (fmt) {
            case "argb": src = Argb (); break;
            case "rgb24": src = Rgb24 (); break;
            case "pargb": src = Pargb (); break;
            case "i8": src = Indexed (PixelFormat.Format8bppIndexed, ref s_i8); break;
            case "i4": src = Indexed (PixelFormat.Format4bppIndexed, ref s_i4); break;
            case "i1": src = Indexed (PixelFormat.Format1bppIndexed, ref s_i1); break;
            default: src = Rgb565 (); break;
            }
            var ia = new ImageAttributes ();
            switch (adj) {
            case "sepia": ia.SetColorMatrix (Matrices () ["sepia"]); break;
            case "gamma": ia.SetGamma (2.2f); break;
            case "remap": {
                Color c0 = src.GetPixel (0, 0), c1 = src.GetPixel (1, 0);
                ia.SetRemapTable (new [] { Map ((uint) c0.ToArgb (), 0xffff0000), Map ((uint) c1.ToArgb (), 0x4000ff00) });
                break; }
            case "key": ia.SetColorKey (Color.FromArgb (0, 0, 0), Color.FromArgb (150, 150, 150)); break;
            case "channel": ia.SetOutputChannel (ColorChannelFlag.ColorChannelC); break;
            }
            return Draw (40, 30, 2, g => {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.DrawImage (src, new Rectangle (1, 1, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, adj == "none" ? null : ia);
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.DrawImage (src, new Rectangle (18, 2, src.Width * 2 - 3, src.Height * 2 + 1), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, adj == "none" ? null : ia);
            });
        }

        // ---- digests -------------------------------------------------------------------------------

        public static string Digest (byte[] bytes)
        {
            byte[] h = SHA256.HashData (bytes);
            var sb = new StringBuilder ();
            for (int i = 0; i < 8; i++) sb.Append (h [i].ToString ("x2"));
            return sb.ToString ();
        }

        /// <summary>Every case's digest, in order; <paramref name="dump"/> (if not null) also gets
        /// every case's bytes, gpx style (index.txt + one .bin per case).</summary>
        public static List<KeyValuePair<string, string>> Digests (string dump = null)
        {
            var res = new List<KeyValuePair<string, string>> ();
            StringBuilder index = dump == null ? null : new StringBuilder ();
            if (dump != null) Directory.CreateDirectory (dump);
            foreach (KeyValuePair<string, Case> c in Cases ()) {
                s_lastWidth = 0;
                byte[] b = Run (c.Value);
                res.Add (new KeyValuePair<string, string> (c.Key, Digest (b)));
                if (dump != null) {
                    string file = c.Key.Replace ('/', '_');
                    File.WriteAllBytes (Path.Combine (dump, file + ".bin"), b);
                    index.Append (c.Key).Append ('\t').Append (file).Append ('\t').Append (b.Length).Append ('\t').Append (s_lastWidth).Append ('\n');
                }
            }
            if (dump != null) File.WriteAllText (Path.Combine (dump, "index.txt"), index.ToString ());
            return res;
        }
    }
}
