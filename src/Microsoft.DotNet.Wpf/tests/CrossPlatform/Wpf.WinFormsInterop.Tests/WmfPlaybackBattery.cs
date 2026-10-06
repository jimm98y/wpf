// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The WMF playback battery: placeable WMFs written record by record -- every record GDI+'s
// WmfEnumState plays (DIBs through StretchDIB, DIBStretchBlt, DIBBitBlt and SetDIBitsToDevice,
// the old BitBlt / StretchBlt of a Bitmap16, brushes solid, hatched, BS_PATTERN and DIB pattern,
// pens of every style, ROP2, text, regions, palettes, SetPixel and the flood fills, PatBlt, the
// window, viewport and map-mode records, placeable headers of several boxes and resolutions) --
// each drawn by Graphics.DrawImage at its own size, stretched, shrunk, and with ImageAttributes
// that recolour it; and EMFs of SETPIXELV and EXTFLOODFILL likewise. Compiled twice -- into the
// tests (the managed GDI+) and, with NATIVE defined, against the real System.Drawing.Common
// (gdiplus.dll), which wrote the digests WmfPlaybackTests holds the port to.
//
// A case's bytes are the destination's 32bpp ARGB pixels, or "EXC:" and the exception's type.
//

#nullable disable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Wpf.WinFormsInterop.Tests
{
    internal static class WmfPlaybackBattery
    {
        // ---- records -----------------------------------------------------------------------------

        sealed class Rec
        {
            readonly MemoryStream _m = new MemoryStream ();
            public Rec W (int v) { _m.WriteByte ((byte) v); _m.WriteByte ((byte) (v >> 8)); return this; }
            public Rec D (int v) { W (v); W (v >> 16); return this; }
            public Rec B (byte v) { _m.WriteByte (v); return this; }
            public Rec Bytes (byte[] b) { _m.Write (b, 0, b.Length); return this; }
            public int Length => (int) _m.Length;
            public byte[] ToArray () => _m.ToArray ();
        }

        /// <summary>A WMF being written: its records, then a placeable header.</summary>
        sealed class Wmf
        {
            readonly List<byte[]> _recs = new List<byte[]> ();
            int _objects;

            public Wmf R (int fn, Action<Rec> body = null)
            {
                var b = new Rec ();
                body?.Invoke (b);
                byte[] p = b.ToArray ();
                var r = new Rec ();
                r.D ((6 + p.Length + 1) / 2).W (fn).Bytes (p);
                if ((p.Length & 1) != 0) r.B (0);
                _recs.Add (r.ToArray ());
                return this;
            }

            /// <summary>A record of 16-bit parameters, in the file's order.</summary>
            public Wmf P (int fn, params int[] words) => R (fn, r => { foreach (int w in words) r.W (w); });

            // object-creating records take the lowest free slot; the battery keeps count itself
            public int Obj () => _objects++;

            public Wmf Pen (int style, int width, int color) => R (0x02FA, r => r.W (style).W (width).W (0).D (color));
            public Wmf Brush (int style, int color, int hatch) => R (0x02FC, r => r.W (style).D (color).W (hatch));
            public Wmf Sel (int i) => P (0x012D, i);
            public Wmf Del (int i) => P (0x01F0, i);
            public Wmf Rect (int l, int t, int r, int b) => P (0x041B, b, r, t, l);
            public Wmf Ellipse (int l, int t, int r, int b) => P (0x0418, b, r, t, l);
            public Wmf RoundRect (int l, int t, int r, int b, int w, int h) => P (0x061C, h, w, b, r, t, l);
            public Wmf Arc (int fn, int l, int t, int r, int b, int xs, int ys, int xe, int ye) => P (fn, ye, xe, ys, xs, b, r, t, l);
            public Wmf MoveTo (int x, int y) => P (0x0214, y, x);
            public Wmf LineTo (int x, int y) => P (0x0213, y, x);
            public Wmf Poly (int fn, params int[] xy) => R (fn, r => { r.W (xy.Length / 2); foreach (int v in xy) r.W (v); });
            public Wmf BkColor (int c) => R (0x0201, r => r.D (c));
            public Wmf TextColor (int c) => R (0x0209, r => r.D (c));
            public Wmf BkMode (int m) => P (0x0102, m);
            public Wmf Rop2 (int m) => P (0x0104, m);
            public Wmf PolyFill (int m) => P (0x0106, m);
            public Wmf StretchMode (int m) => P (0x0107, m);
            public Wmf TextAlign (int m) => P (0x012E, m);
            public Wmf WinOrg (int x, int y) => P (0x020B, y, x);
            public Wmf WinExt (int x, int y) => P (0x020C, y, x);
            public Wmf VpOrg (int x, int y) => P (0x020D, y, x);
            public Wmf VpExt (int x, int y) => P (0x020E, y, x);
            public Wmf MapMode (int m) => P (0x0103, m);
            public Wmf PatBlt (int x, int y, int w, int h, int rop) => R (0x061D, r => r.D (rop).W (h).W (w).W (y).W (x));

            /// <summary>META_STRETCHDIB.</summary>
            public Wmf StretchDib (int xd, int yd, int wd, int hd, int xs, int ys, int ws, int hs, int usage, int rop, byte[] dib)
                => R (0x0F43, r => r.D (rop).W (usage).W (hs).W (ws).W (ys).W (xs).W (hd).W (wd).W (yd).W (xd).Bytes (dib));

            /// <summary>META_DIBSTRETCHBLT (a DIB, or none: a pattern ROP and the reserved word).</summary>
            public Wmf DibStretchBlt (int xd, int yd, int wd, int hd, int xs, int ys, int ws, int hs, int rop, byte[] dib)
                => R (0x0B41, r => { r.D (rop).W (hs).W (ws).W (ys).W (xs); if (dib == null) r.W (0); r.W (hd).W (wd).W (yd).W (xd); if (dib != null) r.Bytes (dib); });

            /// <summary>META_DIBBITBLT (a DIB, or none: the reserved word).</summary>
            public Wmf DibBitBlt (int xd, int yd, int w, int h, int xs, int ys, int rop, byte[] dib)
                => R (0x0940, r => { r.D (rop).W (ys).W (xs).W (h).W (w).W (yd).W (xd); if (dib != null) r.Bytes (dib); });

            /// <summary>META_SETDIBTODEV.</summary>
            public Wmf SetDibToDev (int xd, int yd, int w, int h, int xs, int ys, int start, int scans, int usage, byte[] dib)
                => R (0x0D33, r => r.W (usage).W (scans).W (start).W (ys).W (xs).W (h).W (w).W (yd).W (xd).Bytes (dib));

            public Wmf DibPatternBrush (int style, int usage, byte[] dib) => R (0x0142, r => r.W (style).W (usage).Bytes (dib));

            public byte[] Build (int l = 0, int t = 0, int rr = 120, int bb = 90, int inch = 96, bool placeable = true)
            {
                var recs = new List<byte[]> (_recs);
                var eof = new Rec (); eof.D (3).W (0);
                recs.Add (eof.ToArray ());
                int words = 9, maxRec = 0;
                foreach (byte[] b in recs) { words += b.Length / 2; maxRec = Math.Max (maxRec, b.Length / 2); }
                var all = new Rec ();
                if (placeable) {
                    var ph = new Rec ();
                    ph.D (unchecked ((int) 0x9AC6CDD7)).W (0).W (l).W (t).W (rr).W (bb).W (inch).D (0);
                    byte[] p = ph.ToArray ();
                    int sum = 0;
                    for (int i = 0; i < 20; i += 2) sum ^= p [i] | p [i + 1] << 8;
                    all.Bytes (p).W (sum);
                }
                all.W (1).W (9).W (0x300).D (words).W (Math.Max (_objects, 1)).D (maxRec).W (0);
                foreach (byte[] b in recs) all.Bytes (b);
                return all.ToArray ();
            }
        }

        // ---- DIBs and bitmaps ----------------------------------------------------------------------

        /// <summary>A packed DIB: header, colour table (pal: 0 colourful, 1 black then white, 2
        /// white then black, 3 palette indices), bits of a pattern that shows every pixel.</summary>
        static byte[] Dib (int w, int h, int bpp, int comp = 0, int pal = 0, bool topDown = false, int seed = 0)
        {
            int n = bpp <= 8 ? 1 << bpp : comp == 3 ? 3 : 0;
            var r = new Rec ();
            int stride = (w * bpp + 31) / 32 * 4;
            r.D (40).D (w).D (topDown ? -h : h).W (1).W (bpp).D (comp).D (stride * h).D (0).D (0).D (0).D (0);
            for (int i = 0; i < n; i++) {
                if (comp == 3) r.D (bpp == 16 ? (i == 0 ? 0xf800 : i == 1 ? 0x7e0 : 0x1f) : (i == 0 ? 0xff0000 : i == 1 ? 0xff00 : 0xff));
                else if (pal == 3) r.W ((i * 5 + seed) % 20);
                else if (pal == 1) r.D (i == 0 ? 0 : 0xffffff);
                else if (pal == 2) r.D (i == 0 ? 0xffffff : 0);
                else r.D (((i * 70 + seed * 13) & 255) << 16 | ((255 - i * 50 + seed * 7) & 255) << 8 | ((i * 30 + 10 + seed) & 255));
            }
            var bits = new byte [stride * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < stride; x++)
                    bits [y * stride + x] = (byte) (x * 37 + y * 101 + seed * 17 + (x * y) * 3 + 11);
            r.Bytes (bits);
            return r.ToArray ();
        }

        /// <summary>A Bitmap16 (type, width, height, width bytes, planes, bits per pixel) and its
        /// bits, rows word aligned.</summary>
        static byte[] Bitmap16 (int w, int h, int bpp, int seed = 0)
        {
            int wb = (w * bpp + 15) / 16 * 2;
            var r = new Rec ();
            r.W (0).W (w).W (h).W (wb).B (1).B ((byte) bpp);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < wb; x++) r.B ((byte) (x * 53 + y * 29 + seed * 7 + 3));
            return r.ToArray ();
        }

        // ---- scenarios -----------------------------------------------------------------------------

        static readonly int[] s_rgbs = { 0x2050c0, 0xc08020, 0x10a030, 0x8030a0, 0x00c0c0, 0x606060, 0x0000ff, 0xffffff };

        /// <summary>The DIBs a blit scenario walks through: depth, compression, palette kind.</summary>
        static readonly (int Bpp, int Comp, int Pal)[] s_kinds = {
            (1, 0, 1), (1, 0, 0), (4, 0, 0), (8, 0, 0), (16, 0, 0), (16, 3, 0), (24, 0, 0), (32, 0, 0), (32, 3, 0),
        };

        static void Background (Wmf m)
        {
            // a field the blits and fills meet: four coloured quarters, PATBLT PATCOPY
            int[] cs = { 0xd0e0f0, 0x405060, 0x90c040, 0xa02060 };
            for (int i = 0; i < 4; i++) {
                int b = m.Obj ();
                m.Brush (0, cs [i], 0).Sel (b);
                m.PatBlt (i % 2 * 60, i / 2 * 45, 60, 45, 0x00F00021);
            }
        }

        static byte[] StretchDibs (int mode)
        {
            var m = new Wmf ();
            Background (m);
            int x = 2, y = 2, k = 0;
            foreach (var (bpp, comp, pal) in s_kinds) {
                int w = 10, h = 8, wd = w, hd = h, xs = 0, ys = 0, ws = w, hs = h;
                bool top = false;
                switch (mode) {
                case 1: wd = 17; hd = 13; break;                                   // up, not a whole factor
                case 2: w = 13; h = 11; ws = w; hs = h; wd = 7; hd = 5; break;     // down
                case 3: wd = -w; break;                                           // mirrored in x
                case 4: hd = -h; break;                                           // mirrored in y
                case 5: xs = 2; ys = 1; ws = 6; hs = 5; wd = 12; hd = 10; break;  // a source rectangle
                case 6: top = true; wd = 13; hd = 9; break;                       // top-down
                case 7: wd = 23; hd = 3; break;                                   // one way up, the other down
                }
                byte[] dib = Dib (w, h, bpp, comp, pal, top, k);
                int xd = wd < 0 ? x - wd - 1 : x, yd = hd < 0 ? y - hd - 1 : y;
                m.StretchDib (xd, yd, wd, hd, xs, ys, ws, hs, 0, 0x00CC0020, dib);
                x += 26;
                if (x > 100) { x = 2; y += 22; }
                k++;
            }
            return m.Build ();
        }

        static readonly int[] s_rops = { 0x00CC0020, 0x008800C6, 0x00EE0086, 0x00660046, 0x00330008, 0x00440328, 0x00BB0226, 0x00C000CA, 0x001100A6, 0x00550009, 0x00F00021, 0x005A0049, 0x00000042, 0x00FF0062, 0x00E20746, 0x00B8074A };

        static byte[] DibRops (int fn, int bpp)
        {
            var m = new Wmf ();
            Background (m);
            int br = m.Obj ();
            m.Brush (0, 0x3070e0, 0).Sel (br);
            m.TextColor (0x0000c0).BkColor (0x00e0e0);
            int x = 2, y = 2, k = 0;
            foreach (int rop in s_rops) {
                byte[] dib = Dib (10, 8, bpp, 0, bpp == 1 ? 1 : 0, false, k);
                switch (fn) {
                case 0: m.StretchDib (x, y, 13, 11, 0, 0, 10, 8, 0, rop, dib); break;
                case 1: m.DibStretchBlt (x, y, 13, 11, 0, 0, 10, 8, rop, dib); break;
                case 2: m.DibBitBlt (x, y, 10, 8, 0, 0, rop, dib); break;
                }
                x += 18;
                if (x > 100) { x = 2; y += 16; }
                k++;
            }
            return m.Build ();
        }

        static byte[] DibBlits (int fn, int mode)
        {
            var m = new Wmf ();
            Background (m);
            int x = 2, y = 2, k = 0;
            foreach (var (bpp, comp, pal) in s_kinds) {
                bool top = mode == 2;
                byte[] dib = Dib (10, 8, bpp, comp, pal, top, k);
                if (fn == 0) {
                    if (mode == 1) m.DibStretchBlt (x, y, 15, 11, 1, 1, 8, 6, 0x00CC0020, dib);
                    else if (mode == 3) m.DibStretchBlt (x + 9, y, -10, 8, 0, 0, 10, 8, 0x00CC0020, dib);
                    else m.DibStretchBlt (x, y, 10, 8, 0, 0, 10, 8, 0x00CC0020, dib);
                } else {
                    if (mode == 1) m.DibBitBlt (x, y, 7, 5, 2, 1, 0x00CC0020, dib);
                    else m.DibBitBlt (x, y, 10, 8, 0, 0, 0x00CC0020, dib);
                }
                x += 26;
                if (x > 100) { x = 2; y += 22; }
                k++;
            }
            return m.Build ();
        }

        static byte[] NoSourceBlits ()
        {
            var m = new Wmf ();
            Background (m);
            int br = m.Obj ();
            m.Brush (2, 0x2020c0, 4).Sel (br);
            m.BkColor (0x80f0f0);
            int x = 2, y = 2;
            foreach (int rop in new [] { 0x00F00021, 0x005A0049, 0x00550009, 0x00000042, 0x00FF0062, 0x000F0001, 0x00A000C9, 0x00AF0229 }) {
                m.DibBitBlt (x, y, 12, 9, 0, 0, rop, null);
                m.DibStretchBlt (x + 14, y, 9, 12, 0, 0, 0, 0, rop, null);
                // the ten-word form: no reserved word
                m.R (0x0B41, r => r.D (rop).W (0).W (0).W (0).W (0).W (5).W (8).W (y + 13).W (x + 18));
                // the nine-word form: the source origin and the reserved word
                m.P (0x0940, rop & 0xffff, (rop >> 16) & 0xffff, 0, 0, 0, 7, 6, y + 13, x);
                m.PatBlt (x + 9, y + 14, 6, 5, rop);
                x += 30;
                if (x > 100) { x = 2; y += 22; }
            }
            return m.Build ();
        }

        static byte[] SetDibs (int mode)
        {
            var m = new Wmf ();
            Background (m);
            int x = 2, y = 2, k = 0;
            foreach (var (bpp, comp, pal) in s_kinds) {
                byte[] dib = Dib (10, 8, bpp, comp, pal, mode == 2, k);
                if (mode == 1) m.SetDibToDev (x, y, 6, 5, 2, 1, 0, 8, 0, dib);
                else if (mode == 3) {
                    // a band of scan lines: the DIB's bits are only those rows
                    m.SetDibToDev (x, y, 10, 8, 0, 0, 2, 4, 0, dib);
                } else m.SetDibToDev (x, y, 10, 8, 0, 0, 0, 8, 0, dib);
                x += 26;
                if (x > 100) { x = 2; y += 22; }
                k++;
            }
            return m.Build ();
        }

        static byte[] OldBlits ()
        {
            var m = new Wmf ();
            Background (m);
            m.TextColor (0x1060d0).BkColor (0xf0d040);
            int br = m.Obj ();
            m.Brush (0, 0x30a050, 0).Sel (br);
            int x = 2, y = 2, k = 0;
            foreach (int rop in new [] { 0x00CC0020, 0x008800C6, 0x00EE0086, 0x00660046, 0x00330008, 0x00C000CA, 0x00B8074A, 0x00F00021 }) {
                byte[] bm = Bitmap16 (11, 7, 1, k);
                // META_BITBLT: rop, ySrc, xSrc, height, width, yDst, xDst, Bitmap16
                m.R (0x0922, r => r.D (rop).W (0).W (0).W (7).W (11).W (y).W (x).Bytes (bm));
                // META_STRETCHBLT: rop, srcH, srcW, ySrc, xSrc, dstH, dstW, yDst, xDst, Bitmap16
                m.R (0x0B23, r => r.D (rop).W (7).W (11).W (0).W (0).W (10).W (16).W (y + 9).W (x).Bytes (bm));
                x += 20;
                if (x > 100) { x = 2; y += 22; }
                k++;
            }
            // no source: PATCOPY and DSTINVERT, in both the eight- and nine-word forms
            m.R (0x0922, r => r.D (0x00550009).W (0).W (0).W (0).W (9).W (14).W (60).W (20));
            m.R (0x0B23, r => r.D (0x00F00021).W (0).W (0).W (0).W (0).W (0).W (9).W (14).W (60).W (40));
            return m.Build ();
        }

        static byte[] Hatches (int bk)
        {
            var m = new Wmf ();
            Background (m);
            m.BkMode (bk == 0 ? 1 : 2).BkColor (bk == 2 ? 0x0000ff : 0xf0f0a0);
            int pen = m.Obj ();
            m.Pen (5, 0, 0).Sel (pen);
            int x = 2, y = 2;
            for (int hs = 0; hs < 6; hs++) {
                int b = m.Obj ();
                m.Brush (2, s_rgbs [hs], hs).Sel (b);
                m.Rect (x, y, x + 18, y + 13);
                m.Ellipse (x + 20, y, x + 37, y + 14);
                m.Poly (0x0324, x + 39, y, x + 55, y + 3, x + 44, y + 15);
                m.RoundRect (x, y + 15, x + 25, y + 27, 7, 5);
                x += 60;
                if (x > 100) { x = 2; y += 30; }
            }
            return m.Build ();
        }

        static byte[] PatternBrushes ()
        {
            var m = new Wmf ();
            Background (m);
            m.TextColor (0x2040c0).BkColor (0x40e0f0);
            int pen = m.Obj ();
            m.Pen (5, 0, 0).Sel (pen);
            int x = 2, y = 2, k = 0;
            void Fill (int i)
            {
                m.Sel (i);
                m.Rect (x, y, x + 17, y + 13);
                m.PatBlt (x + 19, y, 9, 13, 0x00F00021);
                x += 30;
                if (x > 100) { x = 2; y += 16; }
            }
            // META_CREATEPATTERNBRUSH: a Bitmap16 and 18 reserved bytes, then the bits
            foreach (int bpp in new [] { 1, 8 }) {
                byte[] bm = Bitmap16 (8, 8, bpp, k++);
                int b = m.Obj ();
                m.R (0x01F9, r => { r.Bytes (new ArraySegment<byte> (bm, 0, 10).ToArray ()).D (0); for (int i = 0; i < 22; i++) r.B (0); r.Bytes (new ArraySegment<byte> (bm, 10, bm.Length - 10).ToArray ()); });
                Fill (b);
            }
            // META_DIBCREATEPATTERNBRUSH, BS_DIBPATTERN, of each depth; BS_PATTERN of a monochrome
            foreach (var (bpp, pal, style, w, h) in new [] { (1, 1, 5, 8, 8), (1, 0, 5, 8, 8), (4, 0, 5, 8, 8), (8, 0, 5, 6, 5), (24, 0, 5, 8, 8), (32, 0, 5, 5, 7), (1, 1, 3, 8, 8), (1, 2, 3, 8, 8), (1, 0, 3, 8, 8), (8, 0, 3, 8, 8) }) {
                int b = m.Obj ();
                m.DibPatternBrush (style, 0, Dib (w, h, bpp, 0, pal, false, k++));
                Fill (b);
            }
            // a pattern brush through CREATEBRUSHINDIRECT, and DIB_PAL_COLORS
            int c = m.Obj ();
            m.Brush (3, 0x123456, 0);
            Fill (c);
            int p = m.Obj ();
            m.DibPatternBrush (5, 1, Dib (8, 8, 4, 0, 3, false, 2));
            Fill (p);
            return m.Build ();
        }

        static byte[] Pens (int bk)
        {
            var m = new Wmf ();
            Background (m);
            m.BkMode (bk == 0 ? 1 : 2).BkColor (0x00f0ff);
            int nul = m.Obj ();
            m.Brush (1, 0, 0).Sel (nul);
            int y = 2;
            for (int style = 0; style < 7; style++) {
                if (style == 5) continue;
                int x = 2;
                foreach (int w in new [] { 0, 1, 3, 6 }) {
                    int p = m.Obj ();
                    m.Pen (style, w, s_rgbs [(style + w) % 7]).Sel (p);
                    m.MoveTo (x, y).LineTo (x + 20, y + 5);
                    m.Poly (0x0325, x, y + 9, x + 8, y + 12, x + 15, y + 8, x + 22, y + 13);
                    m.Rect (x + 1, y + 2, x + 9, y + 8);
                    m.Ellipse (x + 12, y + 1, x + 21, y + 8);
                    x += 29;
                }
                y += 15;
            }
            return m.Build ();
        }

        static byte[] Shapes ()
        {
            var m = new Wmf ();
            Background (m);
            int pen = m.Obj (), br = m.Obj ();
            m.Pen (0, 2, 0x103080).Brush (0, 0x80d0f0, 0).Sel (pen).Sel (br);
            m.Arc (0x0817, 4, 4, 40, 30, 40, 4, 4, 17);
            m.Arc (0x081A, 44, 4, 80, 30, 80, 17, 62, 4);
            m.Arc (0x0830, 84, 4, 118, 30, 84, 4, 118, 30);
            m.RoundRect (4, 34, 40, 60, 12, 9);
            m.R (0x0538, r => { r.W (2).W (4).W (3); foreach (int v in new [] { 44, 34, 80, 34, 80, 60, 44, 60, 50, 38, 74, 56, 50, 56 }) r.W (v); });
            m.PolyFill (2);
            m.R (0x0538, r => { r.W (2).W (4).W (3); foreach (int v in new [] { 84, 34, 118, 34, 118, 60, 84, 60, 90, 38, 112, 56, 90, 56 }) r.W (v); });
            m.Poly (0x0324, 4, 64, 40, 88, 22, 62, 4, 88, 40, 64);
            m.PolyFill (1);
            m.Poly (0x0324, 44, 64, 80, 88, 62, 62, 44, 88, 80, 64);
            return m.Build ();
        }

        static byte[] Rop2s ()
        {
            var m = new Wmf ();
            Background (m);
            int pen = m.Obj (), br = m.Obj ();
            m.Pen (0, 3, 0x2080f0).Brush (0, 0x40c060, 0).Sel (pen).Sel (br);
            int x = 2, y = 2;
            for (int r2 = 1; r2 <= 16; r2++) {
                m.Rop2 (r2);
                m.Rect (x, y, x + 20, y + 14);
                m.MoveTo (x + 2, y + 18).LineTo (x + 22, y + 18);
                x += 29;
                if (x > 100) { x = 2; y += 22; }
            }
            return m.Build ();
        }

        static byte[] LogFont (int height, int weight, bool italic, bool under, bool strike, string face, int esc = 0, int quality = 0)
        {
            var r = new Rec ();
            r.W (height).W (0).W (esc).W (esc).W (weight).B ((byte) (italic ? 1 : 0)).B ((byte) (under ? 1 : 0)).B ((byte) (strike ? 1 : 0)).B (0).B (0).B (0).B ((byte) quality).B (0);
            var f = new byte [32];
            Encoding.ASCII.GetBytes (face, 0, face.Length, f, 0);
            r.Bytes (f);
            return r.ToArray ();
        }

        static byte[] Texts (int kind)
        {
            var m = new Wmf ();
            Background (m);
            m.TextColor (0x102080).BkColor (0x80f0f0);
            if (kind == 0) {
                int f = m.Obj ();
                m.R (0x02FB, r => r.Bytes (LogFont (-13, 400, false, false, false, "Arial"))).Sel (f);
                m.R (0x0521, r => { r.W (5).Bytes (Encoding.ASCII.GetBytes ("Hello")).B (0).W (4).W (4); });
                m.BkMode (1);
                m.R (0x0521, r => { r.W (4).Bytes (Encoding.ASCII.GetBytes ("Wavy")).W (20).W (4); });
                m.TextAlign (6 | 24);       // TA_CENTER | TA_BASELINE
                m.R (0x0521, r => { r.W (3).Bytes (Encoding.ASCII.GetBytes ("Mid")).B (0).W (50).W (60); });
                m.TextAlign (2);            // TA_RIGHT
                m.R (0x0521, r => { r.W (5).Bytes (Encoding.ASCII.GetBytes ("Right")).B (0).W (60).W (116); });
            } else if (kind == 1) {
                int f = m.Obj ();
                m.R (0x02FB, r => r.Bytes (LogFont (-15, 700, true, true, true, "Times New Roman"))).Sel (f);
                m.BkMode (2);
                // EXTTEXTOUT opaque and clipped with a rectangle, then with advances
                m.R (0x0A32, r => { r.W (6).W (4).W (6).W (6).W (2).W (2).W (60).W (22).Bytes (Encoding.ASCII.GetBytes ("Opaque")); });
                m.R (0x0A32, r => { r.W (30).W (4).W (4).W (0).Bytes (Encoding.ASCII.GetBytes ("Gaps")); foreach (int d in new [] { 12, 6, 14, 9 }) r.W (d); });
                int g = m.Obj ();
                m.R (0x02FB, r => r.Bytes (LogFont (-12, 400, false, false, false, "Courier New", 300))).Sel (g);
                m.BkMode (1);
                m.R (0x0521, r => { r.W (6).Bytes (Encoding.ASCII.GetBytes ("Angled")).W (80).W (40); });
            } else {
                // no font selected: the DC's own, as GDI+ makes it TrueType
                m.R (0x0521, r => { r.W (4).Bytes (Encoding.ASCII.GetBytes ("Sys!")).W (6).W (6); });
                int f = m.Obj ();
                m.R (0x02FB, r => r.Bytes (LogFont (20, 400, false, false, false, "Segoe UI"))).Sel (f);
                m.P (0x0108, 3);            // SETTEXTCHAREXTRA
                m.R (0x0521, r => { r.W (5).Bytes (Encoding.ASCII.GetBytes ("Extra")).B (0).W (30).W (6); });
                m.P (0x0108, 0);
                m.P (0x020A, 2, 18);        // SETTEXTJUSTIFICATION: extra 18 over 2 breaks
                m.R (0x0521, r => { r.W (5).Bytes (Encoding.ASCII.GetBytes ("a b c")).B (0).W (55).W (6); });
            }
            return m.Build ();
        }

        /// <summary>META_CREATEREGION of rectangles in scans (each scan: count, top, bottom, the
        /// left/right pairs, count again).</summary>
        static void Region (Wmf m, (int Top, int Bottom, int[] X)[] scans)
        {
            m.R (0x06FF, r => {
                var body = new Rec ();
                int maxScan = 0, size = 22;
                int l = int.MaxValue, t = int.MaxValue, rr = int.MinValue, bb = int.MinValue;
                foreach (var s in scans) {
                    body.W (s.X.Length).W (s.Top).W (s.Bottom);
                    foreach (int x in s.X) body.W (x);
                    body.W (s.X.Length);
                    size += (s.X.Length + 4) * 2;
                    maxScan = Math.Max (maxScan, s.X.Length / 2);
                    l = Math.Min (l, s.X [0]); rr = Math.Max (rr, s.X [s.X.Length - 1]); t = Math.Min (t, s.Top); bb = Math.Max (bb, s.Bottom);
                }
                r.W (0).W (6).D (0).W (size).W (scans.Length).W (maxScan).W (l).W (t).W (rr).W (bb).Bytes (body.ToArray ());
            });
        }

        static byte[] Regions (int kind)
        {
            var m = new Wmf ();
            Background (m);
            int br = m.Obj (), br2 = m.Obj ();
            m.Brush (0, 0x2060c0, 0).Brush (2, 0xc02020, 5);
            int r1 = m.Obj ();
            Region (m, new [] { (4, 20, new [] { 4, 30 }), (20, 34, new [] { 4, 12, 20, 40 }), (34, 40, new [] { 8, 36 }) });
            int r2 = m.Obj ();
            Region (m, new [] { (44, 80, new [] { 50, 110 }) });
            if (kind == 0) {
                m.P (0x0228, r1, br);                // FILLREGION
                m.P (0x0429, r2, br2, 3, 2);         // FRAMEREGION: region, brush, height, width
                m.Sel (br);
                m.P (0x012B, r2);     // PAINTREGION
                m.P (0x012A, r1);                    // INVERTREGION
            } else if (kind == 1) {
                m.P (0x012C, r1);                    // SELECTCLIPREGION
                m.Sel (br2);
                m.Rect (0, 0, 120, 90);
                m.P (0x0220, 30, 40);                // OFFSETCLIPRGN
                m.Sel (br);
                m.Ellipse (0, 0, 120, 90);
                m.P (0x012C, 0);
                m.P (0x0416, 80, 100, 50, 60);       // INTERSECTCLIPRECT b r t l
                m.P (0x0415, 70, 90, 60, 70);        // EXCLUDECLIPRECT
                m.Sel (br2);
                m.Rect (0, 0, 120, 90);
            } else {
                m.Sel (r2);                          // SELECTOBJECT of a region clips
                m.Sel (br);
                m.Rect (0, 0, 120, 90);
                m.P (0x001E);                        // SAVEDC
                m.P (0x0416, 70, 90, 50, 70);
                m.Sel (br2);
                m.Rect (0, 0, 120, 90);
                m.P (0x0127, -1);                    // RESTOREDC
                m.Ellipse (10, 50, 60, 88);
            }
            return m.Build ();
        }

        static byte[] Palettes (int kind)
        {
            var m = new Wmf ();
            Background (m);
            int pal = m.Obj ();
            m.R (0x00F7, r => { r.W (0x300).W (8); for (int i = 0; i < 8; i++) r.B ((byte) (i * 30 + 10)).B ((byte) (200 - i * 20)).B ((byte) (i * 17 + 60)).B (0); });
            if (kind == 1) m.P (0x0234, pal).R (0x0035);
            if (kind == 2) {
                m.P (0x0234, pal).R (0x0035);
                m.R (0x0037, r => { r.W (2).W (2); r.D (0x0000ff).D (0x00ff00); });        // SETPALENTRIES
                m.R (0x0436, r => { r.W (4).W (1); r.D (0xff00ff); });                      // ANIMATEPALETTE
                m.P (0x0139, 6);                                                             // RESIZEPALETTE
            }
            int y = 2;
            for (int i = 0; i < 8; i++) {
                int b = m.Obj ();
                m.Brush (0, 0x01000000 | i, 0).Sel (b);
                m.Rect (2 + i * 14, y, 14 + i * 14, y + 12);
                int b2 = m.Obj ();
                m.Brush (0, 0x02000000 | s_rgbs [i], 0).Sel (b2);
                m.Rect (2 + i * 14, y + 14, 14 + i * 14, y + 26);
            }
            int pen = m.Obj ();
            m.Pen (0, 2, 0x01000003).Sel (pen);
            m.MoveTo (2, 32).LineTo (110, 32);
            m.R (0x0201, r => r.D (0x01000005));
            m.R (0x0209, r => r.D (0x01000001));
            m.SetDibToDev (2, 36, 10, 8, 0, 0, 0, 8, 1, Dib (10, 8, 4, 0, 3, false, 1));
            m.StretchDib (20, 36, 15, 12, 0, 0, 10, 8, 1, 0x00CC0020, Dib (10, 8, 8, 0, 3, false, 2));
            m.R (0x041F, r => r.D (0x01000006).W (60).W (60));
            return m.Build ();
        }

        static byte[] Pixels ()
        {
            var m = new Wmf ();
            Background (m);
            for (int i = 0; i < 40; i++) m.R (0x041F, r => r.D (s_rgbs [i % 8]).W (4 + i % 7).W (4 + i * 2));
            int pen = m.Obj (), br = m.Obj ();
            m.Pen (0, 1, 0x000000).Brush (1, 0, 0).Sel (pen).Sel (br);
            m.Rect (10, 20, 50, 50);
            m.Ellipse (60, 20, 110, 60);
            int fill = m.Obj ();
            m.Brush (0, 0x20c0f0, 0).Sel (fill);
            m.R (0x0419, r => r.D (0x000000).W (30).W (30));               // FLOODFILL: to the border colour
            m.R (0x0548, r => r.W (0).D (0x000000).W (40).W (85));         // EXTFLOODFILL FLOODFILLBORDER
            int fill2 = m.Obj ();
            m.Brush (2, 0x2020a0, 3).Sel (fill2);
            m.R (0x0548, r => r.W (1).D (0x405060).W (10).W (100));        // FLOODFILLSURFACE on a quarter's colour
            m.R (0x0548, r => r.W (1).D (0x90c040).W (80).W (5));
            m.R (0x0548, r => r.W (1).D (0x123456).W (80).W (5));          // a surface colour not there: nothing
            return m.Build ();
        }

        static byte[] Mapping (int kind)
        {
            var m = new Wmf ();
            int pen = m.Obj (), br = m.Obj ();
            m.Pen (0, 1, 0x103080).Brush (2, 0x80d0f0, 4);
            switch (kind) {
            case 0: m.WinOrg (-20, -10).WinExt (240, 180); break;                // its own window
            case 1: m.WinExt (60, 45); break;                                    // an extent and no origin
            case 2: m.WinOrg (0, 0).WinExt (120, 90).VpOrg (10, 10).VpExt (100, 70); break;
            case 3: m.WinOrg (0, 0).WinExt (120, 90).VpOrg (5, 5).VpExt (100, 70).VpOrg (20, 10).VpExt (60, 40); break;
            case 4: m.P (0x020F, 10, 20).P (0x0410, 3, 2, 3, 2); break;          // OFFSETWINDOWORG, SCALEWINDOWEXT
            // isotropic: the mode resets the extents from the device, so both are set again (the
            // first viewport pair only defines GDI+'s viewport matrix)
            case 5: m.MapMode (7).WinExt (120, 60).VpOrg (0, 0).VpExt (120, 90).VpOrg (0, 0).VpExt (120, 90); break;
            case 6: m.MapMode (1); break;                                        // MM_TEXT
            case 7: m.P (0x0211, 8, 12).P (0x0412, 4, 3, 5, 4); break;           // OFFSETVIEWPORTORG, SCALEVIEWPORTEXT
            }
            m.Sel (pen).Sel (br);
            m.Rect (4, 4, 60, 40);
            m.Ellipse (50, 30, 116, 86);
            m.MoveTo (0, 0).LineTo (120, 90);
            m.StretchDib (70, 4, 30, 20, 0, 0, 10, 8, 0, 0x00CC0020, Dib (10, 8, 24, 0, 0, false, 3));
            return m.Build ();
        }

        /// <summary>Records GDI+ drops or GDI ignores here, and SAVEDC / RESTOREDC beyond what was
        /// saved: none of them may change what the rest draws.</summary>
        static byte[] Misc ()
        {
            var m = new Wmf ();
            Background (m);
            int pen = m.Obj (), br = m.Obj (), br2 = m.Obj ();
            m.Pen (0, 2, 0x2040c0).Brush (0, 0x60c0f0, 0).Brush (2, 0x804000, 5).Sel (pen).Sel (br);
            m.P (0x0105, 1);                                       // SETRELABS (not played)
            m.R (0x0231, r => r.D (1));                            // SETMAPPERFLAGS
            m.P (0x0149, 0);                                       // SETLAYOUT
            m.R (0x0626, r => r.W (0x0f).W (4).D (0x12345678));    // ESCAPE (MFCOMMENT)
            m.R (0x0035);                                          // REALIZEPALETTE (not played)
            m.P (0x0127, -1);                                      // RESTOREDC with nothing saved: dropped
            m.Rect (4, 4, 40, 30);
            m.P (0x001E);                                          // SAVEDC
            m.Sel (br2).P (0x0416, 80, 110, 34, 50);
            m.Ellipse (44, 4, 116, 86);
            m.P (0x0127, -4);                                      // RESTOREDC past the one saved: -1
            m.Rect (4, 34, 40, 60);
            m.P (0x001E).Sel (br2).P (0x001E).P (0x0416, 88, 40, 64, 4);
            m.P (0x0127, 1);                                       // absolute: made -1
            m.Ellipse (4, 62, 40, 88);
            m.P (0x0127, -1);
            m.Rect (20, 70, 60, 86);
            return m.Build ();
        }

        // ---- EMF records -------------------------------------------------------------------------

        static byte[] Emr (int type, Action<Rec> body)
        {
            var b = new Rec ();
            body (b);
            byte[] p = b.ToArray ();
            var r = new Rec ();
            r.D (type).D (8 + p.Length).Bytes (p);
            return r.ToArray ();
        }

        static byte[] EmfOf (List<byte[]> recs)
        {
            recs.Add (Emr (14, r => r.D (0).D (16).D (20)));
            int bytes = 108;
            foreach (byte[] b in recs) bytes += b.Length;
            var h = new Rec ();
            h.D (1).D (108).D (0).D (0).D (119).D (89).D (0).D (0).D (3175).D (2381);
            h.D (0x464d4520).D (0x10000).D (bytes).D (recs.Count + 1).W (8).W (0).D (0).D (0).D (0);
            h.D (1920).D (1080).D (508).D (286).D (0).D (0).D (0).D (508000).D (285750);
            var all = new Rec ();
            all.Bytes (h.ToArray ());
            foreach (byte[] b in recs) all.Bytes (b);
            return all.ToArray ();
        }

        /// <summary>SETPIXELV and EXTFLOODFILL, the two records an EMF's playback recolours as
        /// Pen and Brush.</summary>
        static byte[] EmfPixels ()
        {
            var recs = new List<byte[]> ();
            recs.Add (Emr (39, r => r.D (1).D (0).D (0xd0e0f0).D (0)));       // brush 1
            recs.Add (Emr (37, r => r.D (1)));
            recs.Add (Emr (43, r => r.D (0).D (0).D (60).D (45)));             // RECTANGLE (black pen)
            recs.Add (Emr (39, r => r.D (2).D (0).D (0x405060).D (0)));
            recs.Add (Emr (37, r => r.D (2)));
            recs.Add (Emr (43, r => r.D (60).D (0).D (119).D (45)));
            recs.Add (Emr (40, r => r.D (1)));
            recs.Add (Emr (39, r => r.D (1).D (0).D (0x90c040).D (0)));
            recs.Add (Emr (37, r => r.D (1)));
            recs.Add (Emr (43, r => r.D (0).D (45).D (60).D (89)));
            for (int i = 0; i < 40; i++) { int k = i; recs.Add (Emr (15, r => r.D (4 + k * 2).D (50 + k % 7).D (s_rgbs [k % 8]))); }
            recs.Add (Emr (37, r => r.D (unchecked ((int) 0x80000005))));       // NULL_BRUSH
            recs.Add (Emr (42, r => r.D (70).D (10).D (110).D (40)));           // ELLIPSE
            recs.Add (Emr (39, r => r.D (3).D (0).D (0x20c0f0).D (0)));
            recs.Add (Emr (37, r => r.D (3)));
            recs.Add (Emr (53, r => r.D (90).D (25).D (0x000000).D (0)));       // EXTFLOODFILL border
            recs.Add (Emr (39, r => r.D (4).D (2).D (0x2020a0).D (3)));
            recs.Add (Emr (37, r => r.D (4)));
            recs.Add (Emr (53, r => r.D (100).D (10).D (0x405060).D (1)));      // surface
            recs.Add (Emr (53, r => r.D (10).D (70).D (0x90c040).D (1)));
            recs.Add (Emr (53, r => r.D (10).D (75).D (0x123456).D (1)));       // not there
            return EmfOf (recs);
        }

        // ---- the cases -----------------------------------------------------------------------------

        public delegate byte[] Case ();

        [ThreadStatic] static int s_lastWidth;

        static byte[] Bytes (Bitmap b)
        {
            BitmapData d = b.LockBits (new Rectangle (0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var o = new byte [b.Width * 4 * b.Height];
            for (int y = 0; y < b.Height; y++)
                Marshal.Copy (new IntPtr (d.Scan0.ToInt64 () + (long) y * d.Stride), o, y * b.Width * 4, b.Width * 4);
            b.UnlockBits (d);
            return o;
        }

        static ColorMatrix Invert () => new ColorMatrix (new [] {
            new float [] { -1, 0, 0, 0, 0 }, new float [] { 0, -1, 0, 0, 0 }, new float [] { 0, 0, -1, 0, 0 },
            new float [] { 0, 0, 0, 1, 0 }, new float [] { 1, 1, 1, 0, 1 } });

        /// <summary>How a metafile is drawn: placements and recolours.</summary>
        static readonly (string Name, Action<ImageAttributes> Set, int Place)[] s_plays = {
            ("1x", null, 0),
            ("up", null, 1),
            ("down", null, 2),
            ("invert", ia => ia.SetColorMatrix (Invert ()), 0),
            ("brush-invert", ia => ia.SetColorMatrix (Invert (), ColorMatrixFlag.Default, ColorAdjustType.Brush), 0),
            ("pen-invert", ia => ia.SetColorMatrix (Invert (), ColorMatrixFlag.Default, ColorAdjustType.Pen), 0),
            ("text-invert", ia => ia.SetColorMatrix (Invert (), ColorMatrixFlag.Default, ColorAdjustType.Text), 0),
            ("bitmap-invert", ia => ia.SetColorMatrix (Invert (), ColorMatrixFlag.Default, ColorAdjustType.Bitmap), 0),
            ("gamma-up", ia => ia.SetGamma (0.6f), 1),
            ("nearest-up", null, 3),
            ("transparent", null, 4),
        };

        static byte[] Play (byte[] file, Action<ImageAttributes> set, int place)
        {
            int w = 132, h = 100;
            if (place == 1 || place == 3) { w = 170; h = 116; }
            s_lastWidth = w;
            using (var b = new Bitmap (w, h, PixelFormat.Format32bppArgb)) {
                b.SetResolution (96, 96);
                using (Graphics g = Graphics.FromImage (b)) {
                    // place 3: nearest-neighbour interpolation (GDI+ then leaves the DIBs' stretching to
                    // GDI); place 4: onto a transparent bitmap
                    g.Clear (place == 4 ? Color.Transparent : Color.FromArgb (255, 250, 246, 236));
                    if (place == 3) g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    using (var mf = new Metafile (new MemoryStream (file))) {
                        ImageAttributes ia = null;
                        if (set != null) { ia = new ImageAttributes (); set (ia); }
                        Rectangle dst = place == 0 || place == 4 ? new Rectangle (5, 4, 120, 90) : place == 1 || place == 3 ? new Rectangle (3, 5, 163, 109) : new Rectangle (6, 7, 83, 61);
                        GraphicsUnit u = GraphicsUnit.Pixel;
                        RectangleF src = mf.GetBounds (ref u);
                        if (ia == null) g.DrawImage (mf, dst);
                        else g.DrawImage (mf, new [] { new PointF (dst.Left, dst.Top), new PointF (dst.Right, dst.Top), new PointF (dst.Left, dst.Bottom) }, src, GraphicsUnit.Pixel, ia);
                    }
                }
                return Bytes (b);
            }
        }

        static List<KeyValuePair<string, Func<byte[]>>> Scenarios ()
        {
            var l = new List<KeyValuePair<string, Func<byte[]>>> ();
            void S (string n, Func<byte[]> f) => l.Add (new KeyValuePair<string, Func<byte[]>> (n, f));
            string[] stretch = { "1to1", "up", "down", "mirror-x", "mirror-y", "srcrect", "topdown", "updown" };
            for (int i = 0; i < stretch.Length; i++) { int k = i; S ("stretchdib/" + stretch [i], () => StretchDibs (k)); }
            foreach (int bpp in new [] { 1, 4, 8, 24 }) {
                int b = bpp;
                S ("rops/stretchdib-" + bpp, () => DibRops (0, b));
                S ("rops/dibstretchblt-" + bpp, () => DibRops (1, b));
                S ("rops/dibbitblt-" + bpp, () => DibRops (2, b));
            }
            string[] blit = { "1to1", "stretch", "topdown", "mirror" };
            for (int i = 0; i < blit.Length; i++) { int k = i; S ("dibstretchblt/" + blit [i], () => DibBlits (0, k)); }
            for (int i = 0; i < 3; i++) { int k = i; S ("dibbitblt/" + blit [i], () => DibBlits (1, k)); }
            S ("nosource", NoSourceBlits);
            string[] set = { "whole", "part", "topdown", "band" };
            for (int i = 0; i < set.Length; i++) { int k = i; S ("setdibtodev/" + set [i], () => SetDibs (k)); }
            S ("bitmap16", OldBlits);
            for (int i = 0; i < 3; i++) { int k = i; S ("hatch/" + new [] { "transparent", "opaque", "opaque-red" } [i], () => Hatches (k)); }
            S ("patternbrush", PatternBrushes);
            S ("pens/transparent", () => Pens (0));
            S ("pens/opaque", () => Pens (1));
            S ("shapes", Shapes);
            S ("rop2", Rop2s);
            for (int i = 0; i < 3; i++) { int k = i; S ("text/" + i, () => Texts (k)); }
            for (int i = 0; i < 3; i++) { int k = i; S ("regions/" + new [] { "draw", "clip", "select" } [i], () => Regions (k)); }
            for (int i = 0; i < 3; i++) { int k = i; S ("palette/" + new [] { "unselected", "selected", "changed" } [i], () => Palettes (k)); }
            S ("pixels", Pixels);
            S ("misc", Misc);
            string[] map = { "window", "ext-only", "viewport", "viewport-twice", "offset-scale-window", "isotropic", "mm-text", "offset-scale-viewport" };
            for (int i = 0; i < map.Length; i++) { int k = i; S ("mapping/" + map [i], () => Mapping (k)); }
            // placeable headers: a box away from the origin, other resolutions, a flipped box
            S ("placeable/offset", () => { var m = new Wmf (); Background (m); int p = m.Obj (); m.Pen (0, 2, 0xff0000).Sel (p).MoveTo (0, 0).LineTo (120, 90); return ShiftedBuild (m, 30, 20); });
            S ("placeable/1440", () => Shapes1440 ());
            S ("placeable/72", () => { var m = new Wmf (); Background (m); int b = m.Obj (); m.Brush (2, 0x0000c0, 2).Sel (b).Ellipse (10, 10, 100, 80); return m.Build (0, 0, 120, 90, 72); });
            S ("placeable/flipped", () => { var m = new Wmf (); Background (m); int b = m.Obj (); m.Brush (0, 0x00c000, 0).Sel (b).Rect (10, 10, 50, 30); m.StretchDib (60, 10, 30, 20, 0, 0, 10, 8, 0, 0x00CC0020, Dib (10, 8, 8, 0, 0, false, 5)); return m.Build (120, 90, 0, 0, 96); });
            S ("emf/pixels", EmfPixels);
            return l;
        }

        static byte[] ShiftedBuild (Wmf m, int dx, int dy)
        {
            m.WinOrg (dx, dy);
            return m.Build (dx, dy, 120 + dx, 90 + dy, 96);
        }

        static byte[] Shapes1440 ()
        {
            var m = new Wmf ();
            m.WinOrg (0, 0).WinExt (1800, 1350);
            int pen = m.Obj (), br = m.Obj ();
            m.Pen (0, 30, 0x103080).Brush (2, 0x80d0f0, 1).Sel (pen).Sel (br);
            m.Rect (60, 60, 900, 600);
            m.Ellipse (750, 450, 1740, 1290);
            m.StretchDib (1000, 60, 450, 300, 0, 0, 10, 8, 0, 0x00CC0020, Dib (10, 8, 24, 0, 0, false, 4));
            return m.Build (0, 0, 1800, 1350, 1440);
        }

        public static List<KeyValuePair<string, Case>> Cases ()
        {
            var l = new List<KeyValuePair<string, Case>> ();
            foreach (KeyValuePair<string, Func<byte[]>> s in Scenarios ()) {
                Func<byte[]> make = s.Value;
                byte[] file = null;
                foreach (var p in s_plays) {
                    var pl = p;
                    l.Add (new KeyValuePair<string, Case> (s.Key + "/" + p.Name, () => Play (file ??= make (), pl.Set, pl.Place)));
                }
            }
            return l;
        }

        /// <summary>One scenario's file, as written (for looking at it).</summary>
        public static byte[] File (string scenario)
        {
            foreach (KeyValuePair<string, Func<byte[]>> s in Scenarios ())
                if (s.Key == scenario) return s.Value ();
            return null;
        }

        static byte[] Run (Case c)
        {
            try { return c (); }
            catch (Exception e) { return Encoding.ASCII.GetBytes ("EXC:" + e.GetType ().Name); }
        }

        public static string Digest (byte[] bytes)
        {
            byte[] h = SHA256.HashData (bytes);
            var sb = new StringBuilder ();
            for (int i = 0; i < 8; i++) sb.Append (h [i].ToString ("x2"));
            return sb.ToString ();
        }

        /// <summary>Every case's digest, in order; <paramref name="dump"/> (if not null) also gets
        /// every case's bytes (index.txt + one .bin per case).</summary>
        public static List<KeyValuePair<string, string>> Digests (string dump = null, string prefix = null)
        {
            var res = new List<KeyValuePair<string, string>> ();
            StringBuilder index = dump == null ? null : new StringBuilder ();
            if (dump != null) Directory.CreateDirectory (dump);
            foreach (KeyValuePair<string, Case> c in Cases ()) {
                if (prefix != null && !c.Key.StartsWith (prefix, StringComparison.Ordinal)) continue;
                s_lastWidth = 0;
                byte[] b = Run (c.Value);
                res.Add (new KeyValuePair<string, string> (c.Key, Digest (b)));
                if (dump != null) {
                    string file = c.Key.Replace ('/', '_');
                    System.IO.File.WriteAllBytes (Path.Combine (dump, file + ".bin"), b);
                    index.Append (c.Key).Append ('\t').Append (file).Append ('\t').Append (b.Length).Append ('\t').Append (s_lastWidth).Append ('\n');
                }
            }
            if (dump != null) System.IO.File.WriteAllText (Path.Combine (dump, "index.txt"), index.ToString ());
            return res;
        }
    }
}
