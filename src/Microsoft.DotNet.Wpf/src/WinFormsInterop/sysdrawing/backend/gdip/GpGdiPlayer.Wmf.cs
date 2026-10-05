// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WMF records, played into the same DC as an EMF's: GDI+ plays a placeable WMF through
// EnumerateWmfRecords (@180092c00) into an MM_ANISOTROPIC DC whose window is the placeable box and
// whose viewport is the destination (GpGraphics::EnumEmf, the WMF branch). The META_* records are
// the 16-bit versions of the EMR_ ones (parameters in reverse order); objects take the lowest free
// slot of the table, as PlayMetaFileRecord's handle table does.
//

using System.Drawing.Imaging;
using System.Text;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpGdiPlayer
    {
        sealed class GdiRegion : GdiObj { public Region Device; public override void Dispose() => Device?.Dispose(); }

        int FreeSlot()
        {
            for (int i = 0; i < _objects.Length; i++) if (_objects[i] == null) return i;
            int n = _objects.Length;
            Array.Resize(ref _objects, n + 16);
            return n;
        }

        void PutWmf(GdiObj o) => _objects[FreeSlot()] = o;

        static int ColorRef16(byte[] b, int o) => Le.I32(b, o);

        public void PlayWmf(int fn, byte[] b, int o, int n)
        {
            // Parameters are 16-bit words; P(i) is the i-th.
            short P(int i) => o + i * 2 + 2 <= o + n && o + i * 2 + 2 <= b.Length ? Le.I16(b, o + i * 2) : (short)0;
            int words = n / 2;
            switch (fn)
            {
                case 0x0000: return;                                                   // EOF
                case 0x0103: SetMapMode(P(0)); return;
                case 0x0102: _dc.BkMode = P(0); return;
                case 0x0104: _dc.Rop2 = P(0); return;
                case 0x0106: _dc.PolyFill = P(0); return;
                case 0x0107: _dc.StretchMode = P(0); return;
                case 0x012E: _dc.TextAlign = (ushort)P(0); return;
                case 0x0201: _dc.BkColor = ColorRef(ColorRef16(b, o)); return;
                case 0x0209: _dc.TextColor = ColorRef(ColorRef16(b, o)); return;
                case 0x020B: _dc.WinOrg = new Point(P(1), P(0)); return;
                case 0x020C: SetWindowExtWmf(P(1), P(0)); return;
                case 0x020D: _dc.VpOrg = new Point(P(1), P(0)); return;
                case 0x020E: SetViewportExtWmf(P(1), P(0)); return;
                case 0x020F: _dc.WinOrg = new Point(_dc.WinOrg.X + P(1), _dc.WinOrg.Y + P(0)); return;
                case 0x0211: _dc.VpOrg = new Point(_dc.VpOrg.X + P(1), _dc.VpOrg.Y + P(0)); return;
                case 0x0400: ScaleExt(true, P(3), P(2), P(1), P(0)); return;
                case 0x0412: ScaleExt(false, P(3), P(2), P(1), P(0)); return;
                case 0x0213: LineTo(P(1), P(0)); return;
                case 0x0214: MoveTo(P(1), P(0)); return;
                case 0x0415: ClipRectLogical(P(3), P(2), P(1), P(0), true); return;
                case 0x0416: ClipRectLogical(P(3), P(2), P(1), P(0), false); return;
                case 0x0220: OffsetClip(P(1), P(0)); return;
                case 0x0418: ShapeEllipse(RectangleF.FromLTRB(P(3), P(2), P(1), P(0))); return;
                case 0x041B: ShapeRectangle(RectangleF.FromLTRB(P(3), P(2), P(1), P(0))); return;
                case 0x061C: ShapeRoundRect(RectangleF.FromLTRB(P(5), P(4), P(3), P(2)), P(1), P(0)); return;
                case 0x0817: ArcShape(RectangleF.FromLTRB(P(7), P(6), P(5), P(4)), P(3), P(2), P(1), P(0), 0); return;
                case 0x081A: ArcShape(RectangleF.FromLTRB(P(7), P(6), P(5), P(4)), P(3), P(2), P(1), P(0), 2); return;
                case 0x0830: ArcShape(RectangleF.FromLTRB(P(7), P(6), P(5), P(4)), P(3), P(2), P(1), P(0), 1); return;
                case 0x0324:
                case 0x0325:
                    {
                        int c = P(0);
                        if (c < 0 || 1 + c * 2 > words) return;
                        var pts = new PointF[c];
                        for (int i = 0; i < c; i++) pts[i] = new PointF(P(1 + i * 2), P(2 + i * 2));
                        PolyPoints(pts, fn == 0x0324 ? 1 : 2);
                        return;
                    }
                case 0x0538:
                    {
                        int np = P(0);
                        if (np < 0 || 1 + np > words) return;
                        var counts = new int[np];
                        int total = 0;
                        for (int i = 0; i < np; i++) { counts[i] = (ushort)P(1 + i); total += counts[i]; }
                        int at = 1 + np;
                        if (at + total * 2 > words) return;
                        var pts = new PointF[total];
                        for (int i = 0; i < total; i++) pts[i] = new PointF(P(at + i * 2), P(at + i * 2 + 1));
                        PolyPolyPoints(pts, counts, true);
                        return;
                    }
                case 0x001E: _saved.Push(_dc.Clone()); return;
                case 0x0127: RestoreDc(P(0)); return;
                case 0x012D: SelectWmf((ushort)P(0)); return;
                case 0x01F0:
                    {
                        int i = (ushort)P(0);
                        if (i < _objects.Length) DeleteObject((uint)i);
                        return;
                    }
                case 0x02FA:
                    PutWmf(new GdiPen { Style = (ushort)P(0), Width = P(1), Color = ColorRef(ColorRef16(b, o + 6)), Old = true });
                    return;
                case 0x02FC:
                    {
                        int style = (ushort)P(0);
                        PutWmf(new GdiBrush { Style = style == 2 ? 2 : style == 1 ? 1 : 0, Color = ColorRef(ColorRef16(b, o + 2)), Hatch = P(3) });
                        return;
                    }
                case 0x02FB: CreateFontWmf(b, o, n); return;
                case 0x00F7: PutWmf(new GdiPalette()); return;
                case 0x06FF: PutWmf(new GdiRegion()); return;
                case 0x0142:
                    {
                        // DIBCREATEPATTERNBRUSH: style, usage, then a packed DIB.
                        Bitmap bm = PackedDib(b, o + 4, n - 4);
                        PutWmf(new GdiBrush { Style = 3, Pattern = bm });
                        return;
                    }
                case 0x01F9: PutWmf(new GdiBrush { Style = 0, Color = Color.Gray }); return;
                case 0x0521:
                    {
                        int len = P(0);
                        int sb = o + 2;
                        if (len < 0 || sb + len > o + n) return;
                        string s = Encoding.Latin1.GetString(b, sb, len);
                        int w = 1 + (len + 1) / 2;
                        DrawText(s, new PointF(P(w + 1), P(w)), 0, null, null);
                        return;
                    }
                case 0x0A32: ExtTextOutWmf(b, o, n); return;
                case 0x0940: DibBitBltWmf(b, o, n, false); return;
                case 0x0B41: DibBitBltWmf(b, o, n, true); return;
                case 0x0F43:
                    {
                        // STRETCHDIB: rop(2 words), usage, srcH, srcW, ySrc, xSrc, destH, destW, yDst, xDst, DIB.
                        int rop = Le.I32(b, o);
                        int hs = P(3), ws = P(4), ys = P(5), xs = P(6), hd = P(7), wd = P(8), yd = P(9), xd = P(10);
                        Bitmap bm = PackedDib(b, o + 22, n - 22);
                        if (bm == null) return;
                        Blit(bm, xd, yd, wd, hd, new RectangleF(xs, bm.Height - ys - hs, ws, hs), rop);
                        bm.Dispose();
                        return;
                    }
                case 0x0D33:
                    {
                        // SETDIBTODEV: usage, scan count, start scan, ySrc, xSrc, height, width, yDst, xDst, DIB.
                        int ys = P(3), xs = P(4), h = P(5), w = P(6), yd = P(7), xd = P(8);
                        Bitmap bm = PackedDib(b, o + 18, n - 18);
                        if (bm == null) return;
                        Blit(bm, xd, yd, w, h, new RectangleF(xs, bm.Height - ys - h, w, h), 0x00CC0020);
                        bm.Dispose();
                        return;
                    }
                case 0x012C:
                    {
                        int i = (ushort)P(0);
                        if (i < _objects.Length && _objects[i] is GdiRegion rg && rg.Device != null) CombineClip(rg.Device.Clone(), 5);
                        else { _dc.Clip?.Dispose(); _dc.Clip = null; }
                        return;
                    }
                case 0x041F:
                    {
                        // SETPIXEL: colour, y, x.
                        PointF p = ToTarget(new PointF(P(3), P(2)));
                        Prepare();
                        using (var br = new SolidBrush(ColorRef(ColorRef16(b, o))))
                            _t.FillRectangle(br, (float)Math.Floor(p.X), (float)Math.Floor(p.Y), 1, 1);
                        return;
                    }
            }
        }

        void SetWindowExtWmf(int cx, int cy)
        {
            if (_dc.MapMode != 7 && _dc.MapMode != 8) return;
            if (cx == 0 || cy == 0) return;
            _dc.WinExt = new Size(cx, cy);
            Isotropic();
        }

        void SetViewportExtWmf(int cx, int cy)
        {
            if (_dc.MapMode != 7 && _dc.MapMode != 8) return;
            if (cx == 0 || cy == 0) return;
            _dc.VpExt = new Size(cx, cy);
            Isotropic();
        }

        void SelectWmf(int index)
        {
            if (index >= _objects.Length) return;
            switch (_objects[index])
            {
                case GdiPen p: _dc.Pen = p; break;
                case GdiBrush br: _dc.Brush = br; break;
                case GdiFont f: _dc.Font = f; break;
                case GdiRegion rg: if (rg.Device != null) CombineClip(rg.Device.Clone(), 5); break;
            }
        }

        // LOGFONT16: height, width, escapement, orientation, weight (words), italic, underline,
        // strikeout, charset, out/clip precision, quality, pitch (bytes), face (32 bytes, ANSI).
        void CreateFontWmf(byte[] b, int o, int n)
        {
            if (n < 18) { PutWmf(new GdiFont()); return; }
            var f = new GdiFont
            {
                Height = Le.I16(b, o), Width = Le.I16(b, o + 2), Escapement = Le.I16(b, o + 4), Orientation = Le.I16(b, o + 6),
                Weight = Le.I16(b, o + 8), Italic = b[o + 10] != 0, Underline = b[o + 11] != 0, StrikeOut = b[o + 12] != 0,
                CharSet = b[o + 13], Quality = b[o + 16], PitchAndFamily = b[o + 17],
            };
            int end = o + 18;
            var sb = new StringBuilder();
            for (int i = end; i < o + n && i < end + 32 && i < b.Length && b[i] != 0; i++) sb.Append((char)b[i]);
            f.Face = sb.ToString();
            PutWmf(f);
        }

        // META_EXTTEXTOUT: y, x, count, options, [rect if ETO_OPAQUE|ETO_CLIPPED], string, [dx].
        void ExtTextOutWmf(byte[] b, int o, int n)
        {
            if (n < 8) return;
            int y = Le.I16(b, o), x = Le.I16(b, o + 2), count = Le.I16(b, o + 4), options = Le.U16(b, o + 6);
            int p = o + 8;
            RectangleF? rect = null;
            if ((options & 6) != 0 && p + 8 <= o + n)
            {
                rect = RectangleF.FromLTRB(Le.I16(b, p), Le.I16(b, p + 2), Le.I16(b, p + 4), Le.I16(b, p + 6));
                p += 8;
            }
            if (count < 0 || p + count > o + n) return;
            string s = Encoding.Latin1.GetString(b, p, count);
            p += (count + 1) & ~1;
            int[] dx = null;
            if (p + count * 2 <= o + n)
            {
                dx = new int[count];
                for (int i = 0; i < count; i++) dx[i] = Le.I16(b, p + i * 2);
            }
            DrawText(s, new PointF(x, y), options, rect, dx);
        }

        // META_DIBBITBLT / META_DIBSTRETCHBLT: rop, [srcH, srcW,] ySrc, xSrc, destH, destW, yDst, xDst,
        // then a packed DIB (or, with no DIB, a pattern-only ROP and one more word).
        void DibBitBltWmf(byte[] b, int o, int n, bool stretch)
        {
            int rop = Le.I32(b, o);
            int w = n / 2;
            short P(int i) => o + i * 2 + 2 <= b.Length ? Le.I16(b, o + i * 2) : (short)0;
            int baseWords = stretch ? 10 : 8;
            if (w <= baseWords + 1)
            {
                // No source: the parameters are shifted by one reserved word.
                int k = stretch ? 2 : 0;
                int hd = P(3 + k + 1), wd = P(4 + k + 1), yd = P(5 + k + 1), xd = P(6 + k + 1);
                Blit(null, xd, yd, wd, hd, RectangleF.Empty, rop);
                return;
            }
            if (stretch)
            {
                int hs = P(2), ws = P(3), ys = P(4), xs = P(5), hd = P(6), wd = P(7), yd = P(8), xd = P(9);
                Bitmap bm = PackedDib(b, o + 20, n - 20);
                if (bm == null) return;
                Blit(bm, xd, yd, wd, hd, new RectangleF(xs, ys, ws, hs), rop);
                bm.Dispose();
            }
            else
            {
                int ys = P(2), xs = P(3), hd = P(4), wd = P(5), yd = P(6), xd = P(7);
                Bitmap bm = PackedDib(b, o + 16, n - 16);
                if (bm == null) return;
                Blit(bm, xd, yd, wd, hd, new RectangleF(xs, ys, wd, hd), rop);
                bm.Dispose();
            }
        }

        /// <summary>A packed DIB: BITMAPINFOHEADER, the colour table, the bits.</summary>
        internal static Bitmap PackedDib(byte[] b, int o, int n)
        {
            if (n < 12 || o + n > b.Length) return null;
            int hs = Le.I32(b, o);
            int info;
            if (hs == 12)
            {
                int bpp = Le.U16(b, o + 10);
                info = 12 + (bpp <= 8 ? (1 << bpp) * 3 : 0);
            }
            else
            {
                if (hs < 40 || n < 40) return null;
                int bpp = Le.U16(b, o + 14), compression = Le.I32(b, o + 16), used = Le.I32(b, o + 32);
                int colors = used != 0 ? used : bpp <= 8 ? 1 << bpp : 0;
                info = hs + colors * 4 + (compression == 3 && hs == 40 ? 12 : 0);
            }
            if (info > n) return null;
            return DibFromInfo(b, o, info, b, o + info, n - info);
        }
    }
}
