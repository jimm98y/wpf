// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A WMF with no placeable header becomes an EMF, as GDI+ makes one: GDI+ hands the bits to
// SetWinMetaFileBits with no METAFILEPICT, which records an EMF on the screen and plays the WMF
// into it. What that EMF holds was read off GDI's own conversions (scratchpad oracle wm.cs/gen.cs):
//
//   * a prologue: MM_ANISOTROPIC, viewport and window both (0,0) and the screen's size, so the
//     WMF's units are the screen's pixels;
//   * each META_ record as its EMR_ counterpart, object index i as handle i + 1; a SetMapMode to
//     the mode already set is not recorded;
//   * Rectangle / RoundRect / Ellipse / Arc / Pie / Chord boxes lose a device pixel on the right
//     and bottom (taken to device, less one, back to logical; Arc's radials and clip rects are
//     untouched);
//   * text as EMR_EXTTEXTOUTW;
//   * an epilogue: BLACK_PEN, WHITE_BRUSH, DEFAULT_PALETTE (and SYSTEM_FONT once a font was ever
//     selected) selected, the clip region reset, then every object still alive deleted in order;
//   * no frame asked for, so the header's frame is the bounds of what was drawn.
//
// The bounds are each record's device box; a wide pen widens it by (w + 1) / 2 + 1 device pixels
// (verified on polygons and axis lines). GDI's estimate for diagonal wide lines and for text is
// larger than this, so the header bounds of such a file can be smaller than GDI's.
//
// The screen is GpRefDevice.Default on every platform. A WMF that carries an EMF in
// META_ESCAPE_ENHANCED_METAFILE comments gives that EMF back.
//

using System.Collections.Generic;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static partial class GpWmfToEmf
    {
        /// <summary>The records of a WMF (METAHEADER first): offset, size in bytes, function.</summary>
        public static IEnumerable<(int Offset, int Size, int Function)> WmfRecords(byte[] wmf)
        {
            if (wmf.Length < 18) yield break;
            int o = Le.U16(wmf, 2) * 2;
            while (o + 6 <= wmf.Length)
            {
                long size = (long)Le.U32(wmf, o) * 2;
                int fn = Le.U16(wmf, o + 4);
                if (size < 6 || o + size > wmf.Length) yield break;
                yield return (o, (int)size, fn);
                if (fn == 0) yield break;
                o += (int)size;
            }
        }

        sealed class Rec
        {
            readonly List<byte> _b = new List<byte>();
            public Rec I32(int v) { _b.Add((byte)v); _b.Add((byte)(v >> 8)); _b.Add((byte)(v >> 16)); _b.Add((byte)(v >> 24)); return this; }
            public Rec I16(int v) { _b.Add((byte)v); _b.Add((byte)(v >> 8)); return this; }
            public Rec F(float v) => I32(BitConverter.SingleToInt32Bits(v));
            public Rec Bytes(byte[] b, int o, int n) { for (int i = 0; i < n; i++) _b.Add(b[o + i]); return this; }
            public Rec Zero(int n) { for (int i = 0; i < n; i++) _b.Add(0); return this; }
            public Rec Box(int[] r) => r == null ? Zero(16) : I32(r[0]).I32(r[1]).I32(r[2]).I32(r[3]);
            public byte[] ToArray() => _b.ToArray();
        }

        /// <summary>The playback DC: mapping, the pens that widen bounds, the live objects.</summary>
        sealed class Dc
        {
            public int Mode = 8;
            public int Wox, Woy, Wex, Wey, Vox, Voy, Vex, Vey;
            public int Pen = -1;            // selected pen's slot, -1 = a stock pen (width 0)
            public int Font = -1;           // selected font slot, -1 = the stock font
            public Dc Clone() => (Dc)MemberwiseClone();
        }

        sealed class State
        {
            public GpRefDevice Dev;
            public GpEmfWriter W;
            public Dc Dc = new Dc();
            public Stack<Dc> Saved = new Stack<Dc>();
            public List<int> Slots = new List<int>();      // kind per WMF slot: 0 free, 1 pen, 2 other
            public Dictionary<int, int> PenWidth = new Dictionary<int, int>();
            public Dictionary<int, int> FontHeight = new Dictionary<int, int>();
            public int MaxHandle;
            public bool FontEverSelected;

            // The mapping of the current mode, as GDI sets it up for the fixed modes.
            void Map(out double sx, out double sy, out double ox, out double oy, out double wx, out double wy)
            {
                Dc d = Dc;
                double hx = Dev.HorzRes / (double)Dev.HorzSize, hy = Dev.VertRes / (double)Dev.VertSize;
                switch (d.Mode)
                {
                    case 2: sx = hx / 10; sy = -hy / 10; break;         // MM_LOMETRIC
                    case 3: sx = hx / 100; sy = -hy / 100; break;       // MM_HIMETRIC
                    case 4: sx = hx * 0.254; sy = -hy * 0.254; break;   // MM_LOENGLISH
                    case 5: sx = hx * 0.0254; sy = -hy * 0.0254; break; // MM_HIENGLISH
                    case 6: sx = hx * 25.4 / 1440; sy = -hy * 25.4 / 1440; break; // MM_TWIPS
                    case 7: case 8:                                     // MM_ISOTROPIC, MM_ANISOTROPIC
                        sx = d.Wex == 0 ? 1 : d.Vex / (double)d.Wex;
                        sy = d.Wey == 0 ? 1 : d.Vey / (double)d.Wey;
                        break;
                    default: sx = 1; sy = 1; break;                     // MM_TEXT
                }
                ox = d.Vox; oy = d.Voy; wx = d.Wox; wy = d.Woy;
            }

            public void ToDevice(double x, double y, out double dx, out double dy)
            {
                Map(out double sx, out double sy, out double ox, out double oy, out double wx, out double wy);
                dx = (x - wx) * sx + ox;
                dy = (y - wy) * sy + oy;
            }

            public void ToLogical(double dx, double dy, out double x, out double y)
            {
                Map(out double sx, out double sy, out double ox, out double oy, out double wx, out double wy);
                x = (dx - ox) / sx + wx;
                y = (dy - oy) / sy + wy;
            }

            static int Round(double v) => (int)Math.Floor(v + 0.5);

            /// <summary>A box's right/bottom one device pixel in, as GDI records a WMF box.</summary>
            public void Inset(ref int r, ref int b)
            {
                ToDevice(r, b, out double dx, out double dy);
                Map(out double sx, out double sy, out _, out _, out _, out _);
                ToLogical(Round(dx) - Math.Sign(sx), Round(dy) - Math.Sign(sy), out double x, out double y);
                r = Round(x); b = Round(y);
            }

            /// <summary>The inclusive device box of logical points, widened by the pen when asked.</summary>
            public int[] Bounds(IList<int> pts, bool pen)
            {
                double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
                for (int i = 0; i + 1 < pts.Count; i += 2)
                {
                    ToDevice(pts[i], pts[i + 1], out double x, out double y);
                    l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y);
                }
                if (l > r) return null;
                int[] box = { Round(l), Round(t), Round(r), Round(b) };
                if (pen && Dc.Pen >= 0 && PenWidth.TryGetValue(Dc.Pen, out int w) && w > 1)
                {
                    Map(out double sx, out double sy, out _, out _, out _, out _);
                    int ex = (Round(w * Math.Abs(sx)) + 1) / 2 + 1, ey = (Round(w * Math.Abs(sy)) + 1) / 2 + 1;
                    box[0] -= ex; box[1] -= ey; box[2] += ex; box[3] += ey;
                }
                W.AddBounds(box[0], box[1], box[2], box[3]);
                return box;
            }

            public int NewSlot(int kind)
            {
                int i = Slots.IndexOf(0);
                if (i < 0) { i = Slots.Count; Slots.Add(kind); }
                else Slots[i] = kind;
                if (i + 1 > MaxHandle) MaxHandle = i + 1;
                return i;
            }
        }

        public static byte[] Convert(byte[] wmf, GpPlaceable? placeable, out GpMetafileHeader header)
            => Convert(wmf, GpRefDevice.Default, out header);

        /// <summary>The conversion on a given screen (GDI+ uses the one it runs on; we use GpRefDevice.Default).</summary>
        internal static byte[] Convert(byte[] wmf, GpRefDevice dev, out GpMetafileHeader header)
        {
            header = null;
            if (wmf == null || !GpMetafileFormat.WmfHeaderIsValid(wmf, 0, wmf.Length)) return null;
            byte[] wrapped = Unwrap(wmf);
            if (wrapped != null && GpMetafileFormat.EmfHeaderIsValid(wrapped, 0, wrapped.Length) && GpMetafileFormat.HeaderFromEmf(wrapped, out header))
                return wrapped;
            var s = new State { Dev = dev, W = new GpEmfWriter(dev, null, null) };
            s.Dc.Vex = s.Dc.Wex = dev.HorzRes;
            s.Dc.Vey = s.Dc.Wey = dev.VertRes;
            GpEmfWriter w = s.W;
            w.Add(17, new Rec().I32(8).ToArray());                                    // SETMAPMODE(MM_ANISOTROPIC)
            w.Add(12, new Rec().I32(0).I32(0).ToArray());                             // SETVIEWPORTORGEX
            w.Add(11, new Rec().I32(dev.HorzRes).I32(dev.VertRes).ToArray());         // SETVIEWPORTEXTEX
            w.Add(10, new Rec().I32(0).I32(0).ToArray());                             // SETWINDOWORGEX
            w.Add(9, new Rec().I32(dev.HorzRes).I32(dev.VertRes).ToArray());          // SETWINDOWEXTEX
            foreach (var r in WmfRecords(wmf))
            {
                if (r.Function == 0) break;
                Translate(s, wmf, r.Offset + 6, r.Size - 6, r.Function);
            }
            // The epilogue: stock objects back in, the clip reset, the live objects deleted.
            w.Add(37, new Rec().I32(unchecked((int)0x80000007)).ToArray());
            w.Add(37, new Rec().I32(unchecked((int)0x80000000)).ToArray());
            w.Add(48, new Rec().I32(unchecked((int)0x8000000F)).ToArray());
            if (s.FontEverSelected) w.Add(37, new Rec().I32(unchecked((int)0x8000000D)).ToArray());
            w.Add(75, new Rec().I32(0).I32(5).ToArray());
            for (int i = 0; i < s.Slots.Count; i++)
                if (s.Slots[i] != 0) w.Add(40, new Rec().I32(i + 1).ToArray());
            w.UseHandle(s.MaxHandle);
            byte[] emf = w.Finish();
            return GpMetafileFormat.HeaderFromEmf(emf, out header) ? emf : null;
        }

        /// <summary>META_ESCAPE_ENHANCED_METAFILE comments carrying an EMF: the EMF, put back together.</summary>
        static byte[] Unwrap(byte[] wmf)
        {
            var parts = new List<byte>();
            int total = -1;
            foreach (var r in WmfRecords(wmf))
            {
                if (r.Function != 0x0626 || r.Size < 6 + 4 + 38) continue;
                int p = r.Offset + 6;
                if (Le.U16(wmf, p) != 0x000F) continue;                   // MFCOMMENT
                if (Le.I32(wmf, p + 4) != 0x43464D57) continue;           // "WMFC"
                int cb = Le.I32(wmf, p + 4 + 30);
                total = Le.I32(wmf, p + 4 + 34);
                int data = p + 4 + 38;
                if (cb < 0 || data + cb > r.Offset + r.Size) return null;
                for (int i = 0; i < cb; i++) parts.Add(wmf[data + i]);
            }
            if (total <= 0 || parts.Count != total) return null;
            return parts.ToArray();
        }

        static void Translate(State s, byte[] b, int o, int n, int fn)
        {
            GpEmfWriter w = s.W;
            Dc dc = s.Dc;
            int words = n / 2;
            short P(int i) => i < words ? Le.I16(b, o + i * 2) : (short)0;
            Rec R() => new Rec();
            switch (fn)
            {
                case 0x0201: if (n >= 4) w.Add(25, R().I32(Le.I32(b, o)).ToArray()); return;   // SETBKCOLOR
                case 0x0209: if (n >= 4) w.Add(24, R().I32(Le.I32(b, o)).ToArray()); return;   // SETTEXTCOLOR
                case 0x0102: w.Add(18, R().I32(P(0)).ToArray()); return;                     // SETBKMODE
                case 0x0103:                                                                 // SETMAPMODE
                    if (P(0) == dc.Mode) return;
                    dc.Mode = P(0);
                    w.Add(17, R().I32(P(0)).ToArray());
                    return;
                case 0x0104: w.Add(20, R().I32(P(0)).ToArray()); return;                     // SETROP2
                case 0x0106: w.Add(19, R().I32(P(0)).ToArray()); return;                     // SETPOLYFILLMODE
                case 0x0107: w.Add(21, R().I32(P(0)).ToArray()); return;                     // SETSTRETCHBLTMODE
                case 0x012E: w.Add(22, R().I32((ushort)P(0)).ToArray()); return;             // SETTEXTALIGN
                case 0x020B: dc.Woy = P(0); dc.Wox = P(1); w.Add(10, R().I32(P(1)).I32(P(0)).ToArray()); return; // SETWINDOWORG
                case 0x020C: dc.Wey = P(0); dc.Wex = P(1); w.Add(9, R().I32(P(1)).I32(P(0)).ToArray()); return;  // SETWINDOWEXT
                case 0x020D: dc.Voy = P(0); dc.Vox = P(1); w.Add(12, R().I32(P(1)).I32(P(0)).ToArray()); return; // SETVIEWPORTORG
                case 0x020E: dc.Vey = P(0); dc.Vex = P(1); w.Add(11, R().I32(P(1)).I32(P(0)).ToArray()); return; // SETVIEWPORTEXT
                case 0x0213:                                                                 // LINETO
                    s.Bounds(new int[] { P(1), P(0) }, true);
                    w.Add(54, R().I32(P(1)).I32(P(0)).ToArray());
                    return;
                case 0x0214: w.Add(27, R().I32(P(1)).I32(P(0)).ToArray()); return;           // MOVETO
                case 0x0220: w.Add(26, R().I32(P(1)).I32(P(0)).ToArray()); return;           // OFFSETCLIPRGN
                case 0x0415: w.Add(29, R().I32(P(3)).I32(P(2)).I32(P(1)).I32(P(0)).ToArray()); return; // EXCLUDECLIPRECT
                case 0x0416: w.Add(30, R().I32(P(3)).I32(P(2)).I32(P(1)).I32(P(0)).ToArray()); return; // INTERSECTCLIPRECT
                case 0x0418: case 0x041B: case 0x061C:                                       // ELLIPSE, RECTANGLE, ROUNDRECT
                case 0x0817: case 0x081A: case 0x0830:                                       // ARC, PIE, CHORD
                    {
                        int k = fn == 0x061C ? 2 : fn >= 0x0817 ? 4 : 0;   // words before the box (radials / corner)
                        int l = P(k + 3), t = P(k + 2), r = P(k + 1), bt = P(k);
                        s.Bounds(new[] { l, t, r, bt }, true);
                        s.Inset(ref r, ref bt);
                        var rec = R().I32(l).I32(t).I32(r).I32(bt);
                        if (fn == 0x061C) rec.I32(P(1)).I32(P(0));
                        else if (k == 4) rec.I32(P(3)).I32(P(2)).I32(P(1)).I32(P(0));
                        int type = fn == 0x0418 ? 42 : fn == 0x041B ? 43 : fn == 0x061C ? 44 : fn == 0x0817 ? 45 : fn == 0x081A ? 47 : 46;
                        w.Add(type, rec.ToArray());
                        return;
                    }
                case 0x001E: s.Saved.Push(dc.Clone()); w.Add(33, null); return;             // SAVEDC
                case 0x0127:                                                                 // RESTOREDC
                    {
                        int i = P(0);
                        int pops = i < 0 ? -i : s.Saved.Count - i + 1;
                        for (int k = 0; k < pops && s.Saved.Count > 0; k++) s.Dc = s.Saved.Pop();
                        w.Add(34, R().I32(i).ToArray());
                        return;
                    }
                case 0x0324: case 0x0325:                                                    // POLYGON, POLYLINE
                    {
                        int c = P(0);
                        if (c < 0 || 1 + c * 2 > words) return;
                        var pts = new int[c * 2];
                        for (int i = 0; i < c * 2; i++) pts[i] = P(1 + i);
                        var r = R().Box(s.Bounds(pts, true)).I32(c);
                        foreach (int v in pts) r.I16(v);
                        w.Add(fn == 0x0324 ? 86 : 87, r.ToArray());                          // POLYGON16, POLYLINE16
                        return;
                    }
                case 0x0538:                                                                 // POLYPOLYGON
                    {
                        int np = P(0);
                        if (np < 0 || 1 + np > words) return;
                        int total = 0;
                        for (int i = 0; i < np; i++) total += (ushort)P(1 + i);
                        if (1 + np + total * 2 > words) return;
                        var pts = new int[total * 2];
                        for (int i = 0; i < total * 2; i++) pts[i] = P(1 + np + i);
                        var r = R().Box(s.Bounds(pts, true)).I32(np).I32(total);
                        for (int i = 0; i < np; i++) r.I32((ushort)P(1 + i));
                        foreach (int v in pts) r.I16(v);
                        w.Add(91, r.ToArray());                                              // POLYPOLYGON16
                        return;
                    }
                case 0x012D:                                                                 // SELECTOBJECT
                    {
                        int i = (ushort)P(0);
                        if (i < s.Slots.Count && s.Slots[i] == 1) dc.Pen = i;
                        if (i < s.Slots.Count && s.Slots[i] == 3) { dc.Font = i; s.FontEverSelected = true; }
                        w.Add(37, R().I32(i + 1).ToArray());
                        return;
                    }
                case 0x01F0:                                                                 // DELETEOBJECT
                    {
                        int i = (ushort)P(0);
                        if (i < s.Slots.Count) s.Slots[i] = 0;
                        if (dc.Pen == i) dc.Pen = -1;
                        if (dc.Font == i) dc.Font = -1;
                        w.Add(40, R().I32(i + 1).ToArray());
                        return;
                    }
                case 0x02FA:                                                                 // CREATEPENINDIRECT
                    {
                        if (n < 10) return;
                        int sl = s.NewSlot(1);
                        s.PenWidth[sl] = P(1);
                        w.Add(38, R().I32(sl + 1).I32((ushort)P(0)).I32(P(1)).I32(0).I32(Le.I32(b, o + 6)).ToArray());
                        return;
                    }
                case 0x02FC:                                                                 // CREATEBRUSHINDIRECT
                    {
                        if (n < 8) return;
                        int sl = s.NewSlot(2);
                        w.Add(39, R().I32(sl + 1).I32((ushort)P(0)).I32(Le.I32(b, o + 2)).I32(P(3)).ToArray());
                        return;
                    }
                case 0x02FB:                                                                 // CREATEFONTINDIRECT
                    {
                        if (n < 18) return;
                        int sl = s.NewSlot(3);
                        var r = R().I32(sl + 1);
                        s.FontHeight[sl] = Le.I16(b, o);
                        for (int i = 0; i < 5; i++) r.I32(Le.I16(b, o + i * 2));             // height..weight
                        r.Bytes(b, o + 10, 8);                                               // italic..pitch
                        int fe = 0;
                        for (int i = o + 18; i < o + n && fe < 31 && b[i] != 0; i++, fe++) r.I16(b[i]);
                        r.Zero((32 - fe) * 2);                                               // LOGFONTW face
                        r.Zero(368 - 12 - 92 - 8);                                           // full name, style, script: empty
                        r.I32(0x08007664).I32(0);                                            // DESIGNVECTOR: STAMP_DESIGNVECTOR, no axes
                        w.Add(82, r.ToArray());                                              // EXTCREATEFONTINDIRECTW
                        return;
                    }
                case 0x00F7: case 0x06FF:                                                    // CREATEPALETTE, CREATEREGION
                case 0x01F9: case 0x01FA: case 0x01FB:                                       // CREATEPATTERNBRUSH variants
                    {
                        // Objects with no translation still take a slot (WMF object indices are positional).
                        int sl = s.NewSlot(2);
                        w.Add(39, R().I32(sl + 1).I32(1).I32(0).I32(0).ToArray());           // a hollow brush
                        return;
                    }
                case 0x0142:                                                                 // DIBCREATEPATTERNBRUSH
                    {
                        int sl = s.NewSlot(2);
                        int usage = (ushort)P(1);
                        DibSplit(b, o + 4, n - 4, out int info, out int bits);
                        if (info <= 0) { w.Add(39, R().I32(sl + 1).I32(1).I32(0).I32(0).ToArray()); return; }
                        var r = R().I32(sl + 1).I32(usage).I32(32).I32(info).I32(32 + info).I32(bits);
                        r.Bytes(b, o + 4, info + bits);
                        w.Add(94, r.ToArray());                                              // CREATEDIBPATTERNBRUSHPT
                        return;
                    }
                case 0x0521:                                                                 // TEXTOUT
                    {
                        int len = P(0);
                        if (len < 0 || 2 + len > n) return;
                        int wd = 1 + (len + 1) / 2;
                        TextOut(s, b, o + 2, len, P(wd + 1), P(wd), 0, null, -1);
                        return;
                    }
                case 0x0A32:                                                                 // EXTTEXTOUT
                    {
                        if (n < 8) return;
                        int y = Le.I16(b, o), x = Le.I16(b, o + 2), count = Le.I16(b, o + 4), options = Le.U16(b, o + 6);
                        int p = o + 8;
                        int[] rc = null;
                        if ((options & 6) != 0 && p + 8 <= o + n) { rc = new int[] { Le.I16(b, p), Le.I16(b, p + 2), Le.I16(b, p + 4), Le.I16(b, p + 6) }; p += 8; }
                        if (count < 0 || p + count > o + n) return;
                        int dxAt = p + ((count + 1) & ~1);
                        TextOut(s, b, p, count, x, y, options, rc, dxAt + count * 2 <= o + n ? dxAt : -1);
                        return;
                    }
                case 0x0F43:                                                                 // STRETCHDIB
                    {
                        if (words < 11) return;
                        int rop = Le.I32(b, o);
                        int hs = P(3), ws = P(4), ys = P(5), xs = P(6), hd = P(7), wd = P(8), yd = P(9), xd = P(10);
                        StretchDib(s, b, o + 22, n - 22, xd, yd, wd, hd, xs, ys, ws, hs, rop, (ushort)P(2));
                        return;
                    }
                case 0x0D33:                                                                 // SETDIBTODEV
                    {
                        if (words < 9) return;
                        int ys = P(3), xs = P(4), h = P(5), wd = P(6), yd = P(7), xd = P(8);
                        StretchDib(s, b, o + 18, n - 18, xd, yd, wd, h, xs, ys, wd, h, 0x00CC0020, (ushort)P(0));
                        return;
                    }
                case 0x0940: case 0x0B41:                                                    // DIBBITBLT, DIBSTRETCHBLT
                    {
                        if (n < 4) return;
                        int rop = Le.I32(b, o);
                        bool stretch = fn == 0x0B41;
                        int baseWords = stretch ? 10 : 8;
                        if (words <= baseWords + 1)
                        {
                            // No bitmap: a pattern blt (the record has an extra reserved word).
                            int k = stretch ? 5 : 3;
                            int hd = P(k), wd = P(k + 1), yd = P(k + 2), xd = P(k + 3);
                            var r = R().Box(s.Bounds(new[] { xd, yd, xd + wd - 1, yd + hd - 1 }, false))
                                .I32(xd).I32(yd).I32(wd).I32(hd).I32(rop).I32(0).I32(0)
                                .F(1).F(0).F(0).F(1).F(0).F(0).I32(0).I32(0).I32(0).I32(0).I32(0).I32(0);
                            w.Add(76, r.ToArray());                                          // BITBLT
                            return;
                        }
                        if (stretch)
                        {
                            int hs = P(2), ws = P(3), ys = P(4), xs = P(5), hd = P(6), wd = P(7), yd = P(8), xd = P(9);
                            StretchDibTopDown(s, b, o + 20, n - 20, xd, yd, wd, hd, xs, ys, ws, hs, rop);
                        }
                        else
                        {
                            int ys = P(2), xs = P(3), hd = P(4), wd = P(5), yd = P(6), xd = P(7);
                            StretchDibTopDown(s, b, o + 16, n - 16, xd, yd, wd, hd, xs, ys, wd, hd, rop);
                        }
                        return;
                    }
            }
        }

        static void TextOut(State s, byte[] b, int so, int count, int x, int y, int options, int[] rc, int dxAt)
        {
            // EMR_EXTTEXTOUTW: bounds, GM_COMPATIBLE, the scales GDI records (0.01 mm a pixel), then EMRTEXT.
            float exScale = s.Dev.HorzSize * 100f / s.Dev.HorzRes, eyScale = s.Dev.VertSize * 100f / s.Dev.VertRes;
            var r = new Rec();
            // Bounds of the string, estimated from the cell (the font's metrics are not to hand here):
            // a cell |lfHeight| tall (GDI's default font is 16) and half that wide a character.
            int h = s.Dc.Font >= 0 && s.FontHeight.TryGetValue(s.Dc.Font, out int fh) && fh != 0 ? Math.Abs(fh) : 16;
            int[] box = rc != null && (options & 4) != 0 ? s.Bounds(rc, false)
                : count == 0 ? null : s.Bounds(new[] { x, y, x + (count * h + 1) / 2, y + h * 115 / 100 }, false);
            r.Box(box).I32(1).F(exScale).F(eyScale);
            int offString = 8 + 16 + 12 + 40;
            int strBytes = (count * 2 + 3) & ~3;
            int offDx = dxAt >= 0 ? offString + strBytes : 0;
            r.I32(x).I32(y).I32(count).I32(offString).I32(options);
            if (rc != null) r.Box(rc); else r.I32(0).I32(0).I32(-1).I32(-1);
            r.I32(offDx);
            for (int i = 0; i < count; i++) r.I16(b[so + i]);
            r.Zero(strBytes - count * 2);
            if (dxAt >= 0) for (int i = 0; i < count; i++) r.I32(Le.I16(b, dxAt + i * 2));
            s.W.Add(84, r.ToArray());
        }

        static void DibSplit(byte[] b, int o, int n, out int info, out int bits)
        {
            info = 0; bits = 0;
            if (n < 12 || o + n > b.Length) return;
            int hs = Le.I32(b, o);
            if (hs == 12)
            {
                int bpp = Le.U16(b, o + 10);
                info = 12 + (bpp <= 8 ? (1 << bpp) * 3 : 0);
            }
            else if (hs >= 40 && n >= 40)
            {
                int bpp = Le.U16(b, o + 14), compression = Le.I32(b, o + 16), used = Le.I32(b, o + 32);
                int colors = used != 0 ? used : bpp <= 8 ? 1 << bpp : 0;
                info = hs + colors * 4 + (compression == 3 && hs == 40 ? 12 : 0);
            }
            if (info > n) { info = 0; return; }
            bits = n - info;
        }

        static void StretchDib(State s, byte[] b, int o, int n, int xd, int yd, int wd, int hd, int xs, int ys, int ws, int hs, int rop, int usage)
        {
            DibSplit(b, o, n, out int info, out int bits);
            if (info <= 0) return;
            var r = new Rec();
            r.Box(s.Bounds(new[] { xd, yd, xd + wd - 1, yd + hd - 1 }, false))
                .I32(xd).I32(yd).I32(xs).I32(ys).I32(ws).I32(hs).I32(80).I32(info).I32(80 + info).I32(bits).I32(usage).I32(rop).I32(wd).I32(hd);
            r.Bytes(b, o, info + bits);
            s.W.Add(81, r.ToArray());                                                        // STRETCHDIBITS
        }

        // DIBBITBLT / DIBSTRETCHBLT count source rows from the top of the bitmap; StretchDIBits from the bottom.
        static void StretchDibTopDown(State s, byte[] b, int o, int n, int xd, int yd, int wd, int hd, int xs, int ys, int ws, int hs, int rop)
        {
            if (n < 12) return;
            int height = Le.I32(b, o) == 12 ? Le.I16(b, o + 6) : Math.Abs(Le.I32(b, o + 8));
            StretchDib(s, b, o, n, xd, yd, wd, hd, xs, height - ys - hs, ws, hs, rop, 0);
        }
    }
}
