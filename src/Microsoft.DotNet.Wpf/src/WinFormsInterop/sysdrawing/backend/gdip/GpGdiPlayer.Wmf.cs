// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WMF records, played into the same DC as an EMF's. GDI+ (gdiplus.dll arm64, 10.0.26100) plays a
// placeable WMF through MetafilePlayer::EnumerateWmfRecords @180092c00 into an MM_ANISOTROPIC DC
// whose window is the source rectangle and whose viewport is the destination (GpGraphics::EnumEmf
// @180091520, the WMF branch); each record goes through WmfEnumState::ProcessRecord @1800b8960,
// which rewrites some of them, and what it keeps goes to gdi32's PlayMetaFileRecord
// (gdi32full.dll @1800454d0), which makes the GDI calls.
//
//   WmfEnumState::WmfEnumState @1800b5fc8   the DC as the playback starts: BLACK_PEN and
//        WHITE_BRUSH selected, text alignment 0, no justification, black text on a white
//        background, R2_COPYPEN; before the first record the DC's font is made TrueType
//        (CreateTrueTypeFont @1800b4638: its LOGFONT with OUT_TT_ONLY_PRECIS)
//   ProcessRecord                    the records it knows, and nothing else: SETBKCOLOR (as Brush)
//        and SETTEXTCOLOR (Text) through MfEnumState::ModifyColor @1800b7a70 (which resolves a
//        PALETTEINDEX through the palette SELECTPALETTE named, strips any other high byte, then
//        recolours), and both kept unmodified for the monochrome blits; SETPIXEL as Pen,
//        FLOODFILL and EXTFLOODFILL as Brush; CREATEPENINDIRECT as Pen unless it is not a
//        solid/dashed/inside-frame pen; SETWINDOWEXT without a SETWINDOWORG first puts the window
//        origin at 0, 0; SELECTPALETTE only tells ModifyColor the palette (MfEnumState::SelectPalette
//        @1801f3a00) and is not played; REALIZEPALETTE, SETRELABS and other unknown records are
//        dropped; SAVEDC / RESTOREDC are counted (MfEnumState::SaveHdc @1800b55c0,
//        WmfEnumState::RestoreHdc @1800b9330: a RESTOREDC past what the metafile saved is clamped,
//        an absolute one made -1, and one with nothing saved is dropped)
//   WmfEnumState::SetViewportOrg @1800b9508 / SetViewportExt @1800b93d8   the first SETVIEWPORTORG
//        and SETVIEWPORTEXT are not played: together they define a matrix
//        (CalculateViewportMatrix @1800b6458, GpMatrix::InferAffineMatrix) from that viewport onto
//        the destination, through which every later one is mapped and played
//   WmfEnumState::CreateBrushIndirect @1800b6508   a solid or hatched brush recoloured as Brush,
//        a hollow one as it is, any other style solid black (MakeSolidBlackBrush @1800b7590)
//   CREATEPATTERNBRUSH                a pattern of more than one bit per pixel is solid black
//   WmfEnumState::DibCreatePatternBrush @1800b6c68  BS_PATTERN solid black when recolouring unless
//        a black-and-white DIB; BS_DIBPATTERN's DIB rewritten (MfEnumState::ModifyDib) as Brush
//   WmfEnumState::StretchDIBits @1800b9688 / DIBBitBlt @1800b6770   the DIB measured as
//        GetDibNumPalEntries @1801f14d0 measures it (16 and 32 bits per pixel always followed by
//        three masks, whatever the compression), IsValidBitmapRecordSize @1801f19d8 (a DIB too
//        big for the record is not drawn), rewritten for a recolouring playback or DIB_PAL_COLORS
//        (MfEnumState::GetModifiedDibSize @1801f1750), then drawn by MfEnumState::OutputDIB
//        @1800b7eb0 (GpGdiPlayer.Raster.cs); a DIBBITBLT / DIBSTRETCHBLT without a DIB becomes a
//        PATBLT; a DSTCOPY blit is dropped
//
// gdi32's PlayMetaFileRecord plays the rest: the handle table takes the lowest free slot
// (_AddToHandleTable), DELETEOBJECT empties the slot even when the object stays selected;
// CREATEBRUSHINDIRECT of a style past BS_HATCHED is a black solid brush; CREATEREGION
// (ExtCreateRegion of each scan's rectangles); FILLREGION / PAINTREGION / FRAMEREGION /
// INVERTREGION in logical units, SELECTCLIPREGION (and SELECTOBJECT of a region) in device units;
// BITBLT / STRETCHBLT of a Bitmap16 (CreateBitmap, a monochrome one drawn in the text and
// background colours); SETDIBTODEV (SetDIBitsToDevice, the scan band); META_PATBLT.
//

using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Text;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpGdiPlayer
    {
        /// <summary>A region: its rectangles as ExtCreateRegion made them (no transform).</summary>
        sealed class GdiRegion : GdiObj { public List<(int L, int T, int R, int B)> Rects = new List<(int, int, int, int)>(); }

        /// <summary>A Bitmap16 pattern brush's monochrome bitmap, or a BS_PATTERN DIB made one.</summary>
        sealed class MonoBits { public int W, H; public bool[] On; }

        // ---- WmfEnumState --------------------------------------------------------------------------

        bool _wmfStarted;                // +0x9e0: the TrueType font is made before the first record
        bool _wmfWinOrg;                 // +0x9e4: a SETWINDOWORG was played
        bool _vpOrgFirst = true, _vpExtFirst = true;   // +0x988, +0x98c
        int _vpX, _vpY, _vpW = 1, _vpH = 1;            // +0x9c0..: the metafile's first viewport
        int _dstX, _dstY, _dstW, _dstH;                // +0x9d0..: the destination
        GpMat _vpMatrix = GpMat.Identity;              // +0x990
        int _rawBk = 0xffffff, _rawText;               // +0xbc, +0xc0: the colours as recorded
        int _saveDepth;                                // +0x1c
        GdiPalette _wmfPalette;                        // +0x38: the palette ModifyColor looks in
        int _wmfObjects = 16;

        int FreeSlot()
        {
            for (int i = 0; i < _objects.Length && i < _wmfObjects; i++) if (_objects[i] == null) return i;
            return -1;
        }

        /// <summary>_AddToHandleTable: the lowest free slot, or nothing when the table is full.</summary>
        void PutWmf(GdiObj o)
        {
            int i = FreeSlot();
            if (i < 0) { o.Dispose(); return; }
            _objects[i] = o;
        }

        static int ColorRef16(byte[] b, int o) => Le.I32(b, o);

        /// <summary>The GDI+ side of a WMF record (WmfEnumState::ProcessRecord @1800b8960); what it
        /// plays goes to <see cref="GdiWmf"/>.</summary>
        public void PlayWmf(int fn, byte[] b, int o, int n)
        {
            if (n < 0 || o + n > b.Length) return;
            if (!_wmfStarted)
            {
                _wmfStarted = true;
                // CreateTrueTypeFont @1800b4638: the DC's font (SYSTEM_FONT) with OUT_TT_ONLY_PRECIS.
                _dc.Font = new GdiFont { Height = 16, Width = 7, Weight = 700, Face = "System", PitchAndFamily = 0x22 };
            }
            switch (fn)
            {
                // played as they are
                case 0x0231: case 0x0108: case 0x0103: case 0x0106: case 0x0107: case 0x00F7: case 0x0037: case 0x0102:
                case 0x012A: case 0x012B: case 0x012C: case 0x0139: case 0x012E: case 0x0149: case 0x01F0: case 0x020A:
                case 0x020F: case 0x0211: case 0x0213: case 0x0214: case 0x0220: case 0x0228: case 0x0521: case 0x0415:
                case 0x0324: case 0x0410: case 0x0325: case 0x0412: case 0x0416: case 0x0418: case 0x0436: case 0x0429:
                case 0x0830: case 0x061D: case 0x061C: case 0x06FF: case 0x081A: case 0x0817: case 0x0A32: case 0x0538:
                case 0x0104: case 0x012D: case 0x041B: case 0x0D33:
                    GdiWmf(fn, b, o, n);
                    return;
                case 0x001E:
                    _saveDepth--;
                    GdiWmf(fn, b, o, n);
                    return;
                case 0x0127:
                    {
                        // WmfEnumState::RestoreHdc: within what the metafile saved.
                        if (n < 2 || _saveDepth >= 0) return;
                        int k = Le.I16(b, o);
                        if (k < _saveDepth) k = _saveDepth;
                        else if (k >= 0) k = -1;
                        _saveDepth -= k;
                        RestoreDc(k);
                        return;
                    }
                case 0x0201:
                    if (n >= 4) _rawBk = ColorRef16(b, o);
                    ModifyRecordColor(ref b, ref o, n, 0, ColorAdjustType.Brush);
                    GdiWmf(fn, b, o, n);
                    return;
                case 0x0209:
                    if (n >= 4) _rawText = ColorRef16(b, o);
                    ModifyRecordColor(ref b, ref o, n, 0, ColorAdjustType.Text);
                    GdiWmf(fn, b, o, n);
                    return;
                case 0x041F:
                    ModifyRecordColor(ref b, ref o, n, 0, ColorAdjustType.Pen);
                    GdiWmf(fn, b, o, n);
                    return;
                case 0x0419:
                    ModifyRecordColor(ref b, ref o, n, 0, ColorAdjustType.Brush);
                    GdiWmf(fn, b, o, n);
                    return;
                case 0x0548:
                    ModifyRecordColor(ref b, ref o, n, 1, ColorAdjustType.Brush);
                    GdiWmf(fn, b, o, n);
                    return;
                case 0x020B:
                    _wmfWinOrg = true;
                    GdiWmf(fn, b, o, n);
                    return;
                case 0x020C:
                    if (!_wmfWinOrg) GdiSetWindowOrg(0, 0);
                    GdiWmf(fn, b, o, n);
                    return;
                case 0x020D: WmfViewportOrg(b, o, n); return;
                case 0x020E: WmfViewportExt(b, o, n); return;
                case 0x0234:
                    {
                        // MfEnumState::SelectPalette: ModifyColor's palette; the DC keeps its own.
                        if (n < 2) return;
                        int i = Le.I16(b, o);
                        if (i >= 0 && i < _objects.Length && _objects[i] is GdiPalette p) _wmfPalette = p;
                        return;
                    }
                case 0x02FA:
                    if (n > 9)
                    {
                        int style = Le.U16(b, o);
                        if (style < 5 || style == 6) ModifyRecordColor(ref b, ref o, n, 3, ColorAdjustType.Pen);
                    }
                    GdiWmf(fn, b, o, n);
                    return;
                case 0x02FB:
                    {
                        if (n < 0x12) return;
                        // OUT_TT_ONLY_PRECIS
                        if (b[o + 0xe] != 7) { Copy(ref b, ref o, n); b[o + 0xe] = 7; }
                        GdiWmf(fn, b, o, n);
                        return;
                    }
                case 0x02FC:
                    {
                        // WmfEnumState::CreateBrushIndirect @1800b6508
                        if (n <= 7) return;
                        int style = Le.I16(b, o);
                        if (style == 0 || style == 2) ModifyRecordColor(ref b, ref o, n, 1, ColorAdjustType.Brush);
                        else if (style != 1) { SolidBlackBrush(); return; }
                        GdiWmf(fn, b, o, n);
                        return;
                    }
                case 0x01F9:
                    // a pattern of more than one bit per pixel: solid black
                    if (n >= 0x12 && b[o + 9] != 1) { SolidBlackBrush(); return; }
                    GdiWmf(fn, b, o, n);
                    return;
                case 0x0142: DibCreatePatternBrushWmf(b, o, n); return;
                case 0x0922:
                case 0x0B23:
                    {
                        // WmfEnumState::BitBlt @1800b63b8: a DSTCOPY is dropped.
                        if (n < 4 || (Le.I32(b, o) & 0xffff0000) == 0x00aa0000) return;
                        GdiWmf(fn, b, o, n);
                        return;
                    }
                case 0x0940:
                case 0x0B41:
                    if (n >= 4) DibBitBltWmf(fn, b, o, n);
                    return;
                case 0x0F43:
                    if (n >= 4) StretchDibWmf(b, o, n);
                    return;
            }
        }

        /// <summary>CreateCopyOfCurrentRecord: the parameters copied, so they can be rewritten.</summary>
        static void Copy(ref byte[] b, ref int o, int n)
        {
            var c = new byte[n];
            Buffer.BlockCopy(b, o, c, 0, n);
            b = c;
            o = 0;
        }

        /// <summary>WmfEnumState::ModifyRecordColor @1800b7e30: the COLORREF at word
        /// <paramref name="word"/> through ModifyColor, the record copied when it changes.</summary>
        void ModifyRecordColor(ref byte[] b, ref int o, int n, int word, ColorAdjustType type)
        {
            if (word * 2 + 4 > n) return;
            int c = Le.I32(b, o + word * 2);
            int m = ModifyColor(c, type);
            if (m == c) return;
            Copy(ref b, ref o, n);
            Le.W32(b, o + word * 2, m);
        }

        /// <summary>WmfEnumState::MakeSolidBlackBrush @1800b7590: CREATEBRUSHINDIRECT of BS_SOLID,
        /// PALETTERGB(0, 0, 0).</summary>
        void SolidBlackBrush()
        {
            var r = new byte[8];
            Le.W32(r, 2, 0x02000000);
            GdiWmf(0x02FC, r, 0, 8);
        }

        void WmfViewportOrg(byte[] b, int o, int n)
        {
            if (n < 4) return;
            int x = Le.I16(b, o + 2), y = Le.I16(b, o);
            _vpX = x; _vpY = y;
            if (!_vpOrgFirst && !_vpExtFirst)
            {
                var p = new[] { new PointF(x, y) };
                _vpMatrix.Transform(p);
                int nx = (int)(p[0].X + 0.5f), ny = (int)(p[0].Y + 0.5f);
                _dstX = nx; _dstY = ny;
                GdiSetViewportOrg(nx, ny);
                return;
            }
            _vpOrgFirst = false;
            if (!_vpExtFirst) ViewportMatrix();
        }

        void WmfViewportExt(byte[] b, int o, int n)
        {
            if (n < 4) return;
            int x = Le.I16(b, o + 2), y = Le.I16(b, o);
            _vpW = x; _vpH = y;
            if (!_vpOrgFirst && !_vpExtFirst)
            {
                var p = new[] { new PointF(x * _vpMatrix.M11 + y * _vpMatrix.M21, x * _vpMatrix.M12 + y * _vpMatrix.M22) };
                GdiSetViewportExt((int)(p[0].X + 0.5f), (int)(p[0].Y + 0.5f));
                return;
            }
            _vpExtFirst = false;
            if (!_vpOrgFirst) ViewportMatrix();
        }

        /// <summary>CalculateViewportMatrix @1800b6458: the metafile's viewport onto the destination.</summary>
        void ViewportMatrix()
        {
            if (_vpW == 0 || _vpH == 0 || _dstW == 0 || _dstH == 0) { _vpMatrix = GpMat.Identity; return; }
            float sx = (float)_dstW / _vpW, sy = (float)_dstH / _vpH;
            _vpMatrix = new GpMat(sx, 0, 0, sy, _dstX - _vpX * sx, _dstY - _vpY * sy);
        }

        // ---- PlayMetaFileRecord (gdi32full) -------------------------------------------------------

        /// <summary>gdi32's PlayMetaFileRecord @1800454d0 on the record's parameters.</summary>
        void GdiWmf(int fn, byte[] b, int o, int n)
        {
            // P(i) is the i-th 16-bit parameter.
            short P(int i) => i * 2 + 2 <= n ? Le.I16(b, o + i * 2) : (short)0;
            if (n / 2 < MinWords(fn & 0xff)) return;
            switch (fn & 0xff)
            {
                case 0x01: _dc.BkColor = GdiColor(ColorRef16(b, o)); return;
                case 0x02: _dc.BkMode = P(0); return;
                case 0x03: GdiSetMapMode(P(0)); return;
                case 0x04: _dc.Rop2 = P(0); return;
                case 0x06: _dc.PolyFill = P(0); return;
                case 0x07: _dc.StretchMode = P(0); return;
                case 0x09: _dc.TextColor = GdiColor(ColorRef16(b, o)); return;
                case 0x0b: GdiSetWindowOrg(P(1), P(0)); return;
                case 0x0c: SetWindowExtWmf(P(1), P(0)); return;
                case 0x0d: GdiSetViewportOrg(P(1), P(0)); return;
                case 0x0e: GdiSetViewportExt(P(1), P(0)); return;
                case 0x0f: _dc.WinOrg = new Point(_dc.WinOrg.X + P(1), _dc.WinOrg.Y + P(0)); return;
                case 0x10: ScaleExt(true, P(3), P(2), P(1), P(0)); return;
                case 0x11: _dc.VpOrg = new Point(_dc.VpOrg.X + P(1), _dc.VpOrg.Y + P(0)); return;
                case 0x12: ScaleExt(false, P(3), P(2), P(1), P(0)); return;
                case 0x13: LineTo(P(1), P(0)); return;
                case 0x14: MoveTo(P(1), P(0)); return;
                case 0x15: ClipRectLogical(P(3), P(2), P(1), P(0), true); return;
                case 0x16: ClipRectLogical(P(3), P(2), P(1), P(0), false); return;
                case 0x17: ArcShape(RectangleF.FromLTRB(P(7), P(6), P(5), P(4)), P(3), P(2), P(1), P(0), 0); return;
                case 0x18: ShapeEllipse(RectangleF.FromLTRB(P(3), P(2), P(1), P(0))); return;
                case 0x19: FloodFill(P(3), P(2), ColorRef16(b, o), 0); return;
                case 0x1a: ArcShape(RectangleF.FromLTRB(P(7), P(6), P(5), P(4)), P(3), P(2), P(1), P(0), 2); return;
                case 0x1b: ShapeRectangle(RectangleF.FromLTRB(P(3), P(2), P(1), P(0))); return;
                case 0x1c: ShapeRoundRect(RectangleF.FromLTRB(P(5), P(4), P(3), P(2)), P(1), P(0)); return;
                case 0x1d: PatBltWmf(P(5), P(4), P(3), P(2), Le.I32(b, o)); return;
                case 0x1e: _saved.Push(_dc.Clone()); return;
                case 0x1f: SetPixelGdi(P(3), P(2), ColorRef16(b, o)); return;
                case 0x20: OffsetClip(P(1), P(0)); return;
                case 0x21:
                    {
                        int len = (ushort)P(0);
                        if (8 + len > n + 6) return;
                        string s = Encoding.Latin1.GetString(b, o + 2, Math.Min(len, n - 2));
                        int w = 1 + (len + 1) / 2;
                        DrawText(s, new PointF(P(w + 1), P(w)), 0, null, null);
                        return;
                    }
                case 0x22:
                case 0x23: OldBlit(fn, b, o, n); return;
                case 0x24:
                case 0x25:
                    {
                        int c = (ushort)P(0);
                        if ((c + 2) * 4 > n + 6) return;
                        var pts = new PointF[c];
                        for (int i = 0; i < c; i++) pts[i] = new PointF(P(1 + i * 2), P(2 + i * 2));
                        if (c > 0) PolyPoints(pts, (fn & 0xff) == 0x24 ? 1 : 2);
                        return;
                    }
                case 0x27: RestoreDc(P(0)); return;
                case 0x28:
                    {
                        if (RegionAt(P(0), out GdiRegion rg) && (ushort)P(1) < _objects.Length && _objects[(ushort)P(1)] is GdiBrush br)
                            WithBrush(br, () => GdiFillRects(rg.Rects));
                        return;
                    }
                case 0x29:
                    {
                        if (RegionAt(P(0), out GdiRegion rg) && (ushort)P(1) < _objects.Length && _objects[(ushort)P(1)] is GdiBrush br)
                            WithBrush(br, () => GdiFrameRects(rg.Rects, P(3), P(2)));
                        return;
                    }
                case 0x2a: if (RegionAt(P(0), out GdiRegion inv)) GdiInvertRects(inv.Rects); return;
                case 0x2b: if (RegionAt(P(0), out GdiRegion pr)) GdiFillRects(pr.Rects); return;
                case 0x2c:
                    {
                        int i = (ushort)P(0);
                        if (i == 0) { GdiSelectClip(null); return; }
                        if (i < _objects.Length && _objects[i] is GdiRegion rg) GdiSelectClip(rg);
                        else if (i < _objects.Length && _objects[i] == null) GdiSelectClip(null);
                        return;
                    }
                case 0x2d: SelectWmf((ushort)P(0)); return;
                case 0x2e: _dc.TextAlign = (ushort)P(0); return;
                case 0x30: ArcShape(RectangleF.FromLTRB(P(7), P(6), P(5), P(4)), P(3), P(2), P(1), P(0), 1); return;
                case 0x32: ExtTextOutWmf(b, o, n); return;
                case 0x33: SetDibToDevWmf(b, o, n); return;
                case 0x38:
                    {
                        int np = (ushort)P(0);
                        if ((np + 4) * 2 > n + 6) return;
                        var counts = new int[np];
                        int total = 0;
                        for (int i = 0; i < np; i++) { counts[i] = (ushort)P(1 + i); total += counts[i]; }
                        int at = 1 + np;
                        if ((at + total * 2) * 2 > n) return;
                        var pts = new PointF[total];
                        for (int i = 0; i < total; i++) pts[i] = new PointF(P(at + i * 2), P(at + i * 2 + 1));
                        PolyPolyPoints(pts, counts, true);
                        return;
                    }
                case 0x40:
                case 0x41: GdiDibBlit(fn, b, o, n); return;
                case 0x42: DibPatternBrushGdi(b, o, n); return;
                case 0x43: GdiStretchDib(b, o, n, Le.I32(b, o)); return;
                case 0x48: FloodFill(P(4), P(3), Le.I32(b, o + 2), (ushort)P(0)); return;
                case 0xf0:
                    {
                        int i = (ushort)P(0);
                        if (i < _objects.Length) DeleteObject((uint)i);
                        return;
                    }
                case 0xf7: CreatePaletteWmf(b, o, n); return;
                case 0xf9: PatternBrush16(b, o, n); return;
                case 0xfa:
                    {
                        if (n + 6 < 0x10) return;
                        int style = (ushort)P(0);
                        PutWmf(new GdiPen { Style = style, Width = P(1), Color = GdiColor(Le.I32(b, o + 6)), Old = true });
                        return;
                    }
                case 0xfb: CreateFontWmf(b, o, n); return;
                case 0xfc:
                    {
                        if (n + 6 < 0xe) return;
                        int style = (ushort)P(0);
                        int color = Le.I32(b, o + 2);
                        if (style > 2) { style = 0; color = 0; }
                        PutWmf(new GdiBrush { Style = style, Color = GdiColor(color), Hatch = P(3) });
                        return;
                    }
                case 0xff: CreateRegionWmf(b, o, n); return;
            }
        }

        /// <summary>LegalWmfCodesTable @180155280: the fewest parameter words each record takes.</summary>
        static int MinWords(int f)
        {
            ReadOnlySpan<byte> t = new byte[]
            {
                0, 2, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 4, 2, 4, 2, 2, 4, 4, 8, 4, 4, 8, 4, 6, 6, 0, 4,
                2, 5, 9, 11, 3, 3, 6, 1, 2, 4, 1, 1, 1, 1, 1, 0, 8, 2, 10, 13, 2, 0, 4, 0, 5, 1, 0, 0, 0, 0, 0, 0,
                9, 11, 1, 15, 0, 0, 0, 0, 5, 1,
            };
            if (f < t.Length) return t[f];
            switch (f)
            {
                case 0xf0: return 1;
                case 0xf9: case 0xfa: case 0xfb: case 0xfc: return 2;
                case 0xff: return 6;
            }
            return 0;
        }

        /// <summary>A COLORREF as GDI makes it on the DIB: a palette index through the DC's palette
        /// (DEFAULT_PALETTE: SELECTPALETTE is not played), PALETTERGB's high byte dropped.</summary>
        static Color GdiColor(int c)
        {
            uint cr = (uint)c;
            if ((cr & 0xff000000) == 0x01000000)
            {
                int i = (int)(cr & 0xffff);
                int v = i < s_defaultPalette.Length ? s_defaultPalette[i] : 0;
                return Color.FromArgb(v & 0xff, v >> 8 & 0xff, v >> 16 & 0xff);
            }
            return ColorRef(c);
        }

        /// <summary>DC::iSetMapMode @140231fb0: a new mode (or MM_ISOTROPIC again) resets the
        /// extents from the DC's device -- the window its size in the mode's units, the viewport its
        /// pixels with y up; MM_ANISOTROPIC keeps them. (The device is the reference display this
        /// port assumes everywhere; Windows uses the real one.)</summary>
        void GdiSetMapMode(int mode)
        {
            if (mode < 1 || mode > 8) return;
            if (mode == _dc.MapMode && mode != 7) return;
            GpRefDevice dev = GpRefDevice.Default;
            long umX = dev.HorzSize * 1000L, umY = dev.VertSize * 1000L;
            static int MulDiv(long a, long b, long c) => (int)((a * b + c / 2) / c);
            Size win;
            switch (mode)
            {
                case 1: _dc.MapMode = 1; _dc.WinExt = new Size(1, 1); _dc.VpExt = new Size(1, 1); return;
                case 8: _dc.MapMode = 8; return;
                case 3: win = new Size((int)((umX + 5) / 10), (int)((umY + 5) / 10)); break;
                case 4: win = new Size((int)((umX + 0x7f) / 254), (int)((umY + 0x7f) / 254)); break;
                case 5: win = new Size(MulDiv(umX, 10, 254), MulDiv(umY, 10, 254)); break;
                case 6: win = new Size(MulDiv(umX, 0x90, 0x9ec), MulDiv(umY, 0x90, 0x9ec)); break;
                default: win = new Size((int)((umX + 0x32) / 100), (int)((umY + 0x32) / 100)); break;
            }
            _dc.MapMode = mode;
            _dc.WinExt = win;
            _dc.VpExt = new Size(dev.HorzRes, -dev.VertRes);
        }

        void GdiSetWindowOrg(int x, int y) => _dc.WinOrg = new Point(x, y);

        void GdiSetViewportOrg(int x, int y) => _dc.VpOrg = new Point(x, y);

        void GdiSetViewportExt(int cx, int cy) => SetViewportExtWmf(cx, cy);

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
                case GdiRegion rg: GdiSelectClip(rg); break;
            }
        }

        bool RegionAt(int index, out GdiRegion rg)
        {
            rg = null;
            int i = (ushort)index;
            if (i < _objects.Length && _objects[i] is GdiRegion r) { rg = r; return true; }
            return false;
        }

        void WithBrush(GdiBrush br, Action a)
        {
            GdiBrush saved = _dc.Brush;
            _dc.Brush = br;
            try { a(); } finally { _dc.Brush = saved; }
        }

        /// <summary>SelectClipRgn: the region's rectangles are device pixels (RGN_COPY), or no clip.</summary>
        void GdiSelectClip(GdiRegion rg)
        {
            if (!Gdi)
            {
                _dc.Clip?.Dispose();
                _dc.Clip = null;
                if (rg == null) return;
                var r = new Region();
                r.MakeEmpty();
                foreach (var q in rg.Rects) r.Union(Rectangle.FromLTRB(q.L, q.T, q.R, q.B));
                using (var m = _base.ToMatrix()) r.Transform(m);
                _dc.Clip = r;
                return;
            }
            // device units of the device the DIB is drawn onto (as the patterns are)
            if (rg == null) { _dc.GClip = null; return; }
            int dx = WmfPatternX, dy = WmfPatternY;
            var rects = new List<(int, int, int, int)>();
            foreach (var q in rg.Rects) rects.Add((q.L + dx, q.T + dy, q.R + dx, q.B + dy));
            _dc.GClip = GdiRgn.FromRects(rects);
        }

        /// <summary>META_CREATEREGION: each scan's left / right pairs become rectangles of the scan's
        /// top and bottom (ExtCreateRegion, no transform); a region of no scans is empty.</summary>
        void CreateRegionWmf(byte[] b, int o, int n)
        {
            var rg = new GdiRegion();
            int scans = Le.U16(b, o + 10);
            int at = o + 22, end = o + n;
            for (int s = 0; s < scans; s++)
            {
                if (at + 6 > end) { rg.Dispose(); return; }
                int count = Le.U16(b, at), top = Le.I16(b, at + 2), bottom = Le.I16(b, at + 4);
                int next = at + (count + 4) * 2;
                if (next > end) { rg.Dispose(); return; }
                for (int k = 0; k + 1 < count; k += 2)
                {
                    int l = Le.I16(b, at + 6 + k * 2), r = Le.I16(b, at + 8 + k * 2);
                    if (l < r && top < bottom) rg.Rects.Add((l, top, r, bottom));
                }
                at = next;
            }
            PutWmf(rg);
        }

        void CreatePaletteWmf(byte[] b, int o, int n)
        {
            if (n < 4) return;
            int count = Le.U16(b, o + 2);
            if (4 + count * 4 > n) return;
            var pal = new GdiPalette { Entries = new Color[count] };
            for (int i = 0; i < count; i++) pal.Entries[i] = Color.FromArgb(b[o + 4 + i * 4], b[o + 5 + i * 4], b[o + 6 + i * 4]);
            PutWmf(pal);
        }

        // LOGFONT16: height, width, escapement, orientation, weight (words), italic, underline,
        // strikeout, charset, out/clip precision, quality, pitch (bytes), face (32 bytes, ANSI).
        void CreateFontWmf(byte[] b, int o, int n)
        {
            if (n + 6 < 0x18) return;
            var f = new GdiFont
            {
                Height = Le.I16(b, o), Width = Le.I16(b, o + 2), Escapement = Le.I16(b, o + 4), Orientation = Le.I16(b, o + 6),
                Weight = Le.I16(b, o + 8), Italic = b[o + 10] != 0, Underline = b[o + 11] != 0, StrikeOut = b[o + 12] != 0,
                CharSet = b[o + 13], Quality = n > 16 ? b[o + 16] : 0, PitchAndFamily = n > 17 ? b[o + 17] : 0,
            };
            int end = o + 18;
            var sb = new StringBuilder();
            for (int i = end; i < o + n && i < end + 32 && b[i] != 0; i++) sb.Append((char)b[i]);
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

        // ---- pixels, flood fills, PatBlt -----------------------------------------------------------

        void PatBltWmf(int x, int y, int w, int h, int rop)
        {
            _dibBlit = false;
            Blit(null, x, y, w, h, RectangleF.Empty, rop);
        }

        /// <summary>SetPixel: the pixel the point lands on (through the DC's transform), if the clip
        /// lets it, set to the colour.</summary>
        void SetPixelGdi(int x, int y, int color)
        {
            Color c = GdiColor(color);
            if (!Gdi)
            {
                PointF p = ToTarget(new PointF(x, y));
                Prepare();
                using (var br = new SolidBrush(c))
                    _t.FillRectangle(br, (float)Math.Floor(p.X), (float)Math.Floor(p.Y), 1, 1);
                return;
            }
            GdiXform m = TargetWtoD();
            m.Point(x, y, out int fx, out int fy);
            int px = (fx + 8) >> 4, py = (fy + 8) >> 4;
            if (px < 0 || py < 0 || px >= _cw || py >= _ch) return;
            GdiRgn clip = GdiClip();
            if (clip != null && !InRow(clip.Row(py), px)) return;
            uint v = (uint)(c.R << 16 | c.G << 8 | c.B);
            BitmapData bd = _canvas.LockBits(new Rectangle(px, py, 1, 1), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int old = System.Runtime.InteropServices.Marshal.ReadInt32(bd.Scan0);
                System.Runtime.InteropServices.Marshal.WriteInt32(bd.Scan0, unchecked((int)(((uint)old & 0xff000000) | v)));
            }
            finally { _canvas.UnlockBits(bd); }
        }

        /// <summary>ExtFloodFill: from the seed, the 4-connected pixels that are not the colour
        /// (FLOODFILLBORDER) or are it (FLOODFILLSURFACE), within the clip, painted with the brush.</summary>
        void FloodFill(int x, int y, int color, int mode)
        {
            if (!Gdi || _dc.Brush == null || _dc.Brush.Style == 1) return;
            Color cc = GdiColor(color);
            uint key = (uint)(cc.R << 16 | cc.G << 8 | cc.B);
            GdiXform m = TargetWtoD();
            m.Point(x, y, out int fx, out int fy);
            int sx = (fx + 8) >> 4, sy = (fy + 8) >> 4;
            if (sx < 0 || sy < 0 || sx >= _cw || sy >= _ch) return;
            GdiRgn clip = GdiClip();
            bool Vis(int px, int py) => clip == null || InRow(clip.Row(py), px);
            if (!Vis(sx, sy)) return;
            uint[] px0 = Pixels(_canvas, out _, out _);
            bool In(int px, int py)
            {
                uint v = px0[py * _cw + px] & 0xffffff;
                return mode == 0 ? v != key : v == key;
            }
            if (!In(sx, sy)) return;
            var done = new bool[_cw * _ch];
            var stack = new Stack<(int, int)>();
            stack.Push((sx, sy));
            done[sy * _cw + sx] = true;
            while (stack.Count > 0)
            {
                var (cx, cy) = stack.Pop();
                void Try(int nx, int ny)
                {
                    if (nx < 0 || ny < 0 || nx >= _cw || ny >= _ch) return;
                    int k = ny * _cw + nx;
                    if (done[k] || !Vis(nx, ny) || !In(nx, ny)) return;
                    done[k] = true;
                    stack.Push((nx, ny));
                }
                Try(cx - 1, cy); Try(cx + 1, cy); Try(cx, cy - 1); Try(cx, cy + 1);
            }
            var spans = new List<GdiSpan>();
            for (int yy = 0; yy < _ch; yy++)
                for (int xx = 0; xx < _cw; xx++)
                {
                    if (!done[yy * _cw + xx]) continue;
                    int x0 = xx;
                    while (xx < _cw && done[yy * _cw + xx]) xx++;
                    spans.Add(new GdiSpan(yy, x0, xx));
                }
            Func<int, int, uint> pattern = PatternOf(true);
            if (pattern == null) return;
            int saved = _dc.Rop2;
            _dc.Rop2 = 13;
            try { GdiPaint(spans, pattern); } finally { _dc.Rop2 = saved; }
        }

        static bool InRow(int[] row, int x)
        {
            if (row == null) return false;
            for (int i = 0; i + 1 < row.Length; i += 2) if (x >= row[i] && x < row[i + 1]) return true;
            return false;
        }

        // ---- the DIBs --------------------------------------------------------------------------------

        /// <summary>A DIB as WmfEnumState measures it: its header at <see cref="H"/>, the colour table
        /// GDI reads after it, the bits where GetDibBits puts them (after GetDibNumPalEntries'
        /// entries: three masks for any DIB of 16 or 32 bits per pixel).</summary>
        struct WmfDib
        {
            public int H, BiSize, W, Height, Bpp, Comp, ClrUsed, NumPal, BitsAt, BitsSize;
            public int Usage;
        }

        /// <summary>GetDibNumPalEntries @1801f14d0 with its first argument 1 (as the WMF playback
        /// calls it), or -1 for a DIB it refuses.</summary>
        static int NumPalEntries(int biSize, int bpp, int comp, int clrUsed)
        {
            int n;
            if (bpp == 16 || bpp == 32) return biSize < 0x29 ? 3 : 0;
            switch (comp)
            {
                case 0:
                case 10:
                    if (bpp == 1 || bpp == 4 || bpp == 8) n = clrUsed == 0 ? 1 << bpp : clrUsed;
                    else if (bpp == 24) n = 0;
                    else return -1;
                    break;
                case 1: case 11: if (bpp != 8) return -1; n = 0x100; if (clrUsed != 0 && clrUsed <= n) n = clrUsed; break;
                case 2: case 12: if (bpp != 4) return -1; n = 0x10; if (clrUsed != 0 && clrUsed <= n) n = clrUsed; break;
                case 4: case 5: n = 0; break;
                default: return -1;
            }
            return Math.Min(n, 0x100);
        }

        /// <summary>GetDibBitsSize @1801f1428.</summary>
        static bool BitsSize(byte[] b, int h, out int size)
        {
            size = 0;
            int biSize = Le.I32(b, h), w = Le.I32(b, h + 4), height = Le.I32(b, h + 8);
            int planes = Le.U16(b, h + 12), bpp = Le.U16(b, h + 14), comp = Le.I32(b, h + 16);
            if (biSize < 0x28 || w < 1 || planes != 1) return false;
            if (comp == 0 || comp == 3 || comp == 10)
            {
                long stride = (((long)w * bpp + 31) >> 3) & ~3L;
                long s = stride * Math.Abs((long)height);
                if (s > int.MaxValue) return false;
                size = (int)s;
            }
            else size = Le.I32(b, h + 20);
            return true;
        }

        /// <summary>The DIB at <paramref name="h"/> with <paramref name="avail"/> bytes for it, as
        /// the WmfEnumState handlers check it (IsValidBitmapRecordSize @1801f19d8); false when it
        /// is not drawn.</summary>
        static bool MeasureDib(byte[] b, int h, int avail, int usage, out WmfDib d)
        {
            d = default;
            if (avail < 40 || h + 40 > b.Length) return false;
            d.H = h;
            d.BiSize = Le.I32(b, h);
            if (d.BiSize < 0x28 || d.BiSize > avail) return false;
            d.W = Le.I32(b, h + 4); d.Height = Le.I32(b, h + 8);
            d.Bpp = Le.U16(b, h + 14); d.Comp = Le.I32(b, h + 16); d.ClrUsed = Le.I32(b, h + 32);
            d.NumPal = NumPalEntries(d.BiSize, d.Bpp, d.Comp, d.ClrUsed);
            if (d.NumPal < 0) return false;
            if (!BitsSize(b, h, out d.BitsSize) || d.BitsSize == 0) return false;
            if (usage == 1 && (d.Bpp > 8 || d.Comp == 3 || d.Comp == 10)) usage = 0;
            d.Usage = usage;
            long need = (long)d.BiSize + (long)d.NumPal * (usage == 1 ? 2 : 4) + d.BitsSize;
            if (need > avail) return false;
            // GetDibBits @1801f13e0: the palette indices padded to four bytes.
            d.BitsAt = h + d.BiSize + (d.NumPal == 0 ? 0 : usage == 1 && d.Comp != 3 && d.Comp != 10 ? (d.NumPal * 2 + 3) & ~3 : d.NumPal * 4);
            return d.BitsAt + d.BitsSize <= b.Length;
        }

        /// <summary>The DIB as a Bitmap, its header and colour table (or masks) as GDI reads them, its
        /// bits where GDI+ points: DIB_PAL_COLORS indices through the DC's palette.</summary>
        Bitmap BitmapOf(byte[] b, in WmfDib d)
        {
            int ncol = 0;
            if (d.Bpp <= 8) ncol = d.ClrUsed != 0 ? Math.Min(d.ClrUsed, 1 << d.Bpp) : 1 << d.Bpp;
            else if (d.Comp == 3) ncol = 3;
            var info = new byte[40 + ncol * 4];
            Buffer.BlockCopy(b, d.H, info, 0, 40);
            Le.W32(info, 0, 40);
            if (d.Bpp <= 8) Le.W32(info, 32, ncol);
            for (int i = 0; i < ncol; i++)
            {
                int q = info.Length - ncol * 4 + i * 4;
                if (d.Bpp <= 8 && d.Usage == 1)
                {
                    int at = d.H + d.BiSize + i * 2;
                    Color c = GdiColor(0x01000000 | (at + 2 <= b.Length ? Le.U16(b, at) : 0));
                    info[q] = c.B; info[q + 1] = c.G; info[q + 2] = c.R;
                }
                else
                {
                    int at = d.H + d.BiSize + i * 4;
                    if (at + 4 <= b.Length) Buffer.BlockCopy(b, at, info, q, 4);
                }
            }
            return DibFromInfo(info, 0, info.Length, b, d.BitsAt, d.BitsSize);
        }

        /// <summary>WmfEnumState::StretchDIBits @1800b9688: META_STRETCHDIB.</summary>
        void StretchDibWmf(byte[] b, int o, int n)
        {
            int rop = Le.I32(b, o);
            if ((rop & 0xffff0000) == 0x00aa0000) return;
            GdiStretchDib(b, o, n, rop);
        }

        /// <summary>META_STRETCHDIB on the DC: rop (two words), usage, srcH, srcW, ySrc, xSrc, destH,
        /// destW, yDst, xDst, the DIB -- through OutputDIB when the ROP reads the source.</summary>
        void GdiStretchDib(byte[] b, int o, int n, int rop)
        {
            short P(int i) => Le.I16(b, o + i * 2);
            if (n < 22) return;
            int usage = (ushort)P(2);
            int hs = P(3), ws = P(4), ys = P(5), xs = P(6), hd = P(7), wd = P(8), yd = P(9), xd = P(10);
            if (!UsesSource(rop)) { _dibBlit = false; Blit(null, xd, yd, wd, hd, RectangleF.Empty, rop); return; }
            if (!MeasureDib(b, o + 22, n - 22, usage, out WmfDib d)) return;
            OutputDibWmf(b, d, xd, yd, wd, hd, xs, ys, ws, hs, rop);
        }

        /// <summary>WmfEnumState::DIBBitBlt @1800b6770: META_DIBBITBLT / META_DIBSTRETCHBLT. Without a
        /// DIB (a ROP that does not read the source) it is a PATBLT of the destination; with one, the
        /// DIB (rows counted from its bottom) through OutputDIB.</summary>
        void DibBitBltWmf(int fn, byte[] b, int o, int n)
        {
            short P(int i) => i * 2 + 2 <= n ? Le.I16(b, o + i * 2) : (short)0;
            int rop = Le.I32(b, o);
            if ((rop & 0xffff0000) == 0x00aa0000) return;
            if ((((rop ^ (rop << 2)) & unchecked((int)0xcccc0000)) == 0))
            {
                int k = fn == 0x0940 ? 7 : 9;
                if (n / 2 == (fn >> 8) - 0) k++;       // the form with the reserved word
                if ((k + 1) * 2 > n) return;
                PatBltWmf(P(k), P(k - 1), P(k - 2), P(k - 3), rop);
                return;
            }
            int baseWords = fn == 0x0940 ? 8 : 10;
            if (!MeasureDib(b, o + baseWords * 2, n - baseWords * 2, 0, out WmfDib d)) return;
            int xd, yd, wd, hd, xs, ys, ws, hs;
            if (fn == 0x0940) { ys = P(2); xs = P(3); hd = P(4); wd = P(5); yd = P(6); xd = P(7); ws = wd; hs = hd; }
            else { hs = P(2); ws = P(3); ys = P(4); xs = P(5); hd = P(6); wd = P(7); yd = P(8); xd = P(9); }
            if (d.Bpp == 1 && Le.U16(b, d.H + 12) == 1 && d.NumPal == 2 && Le.I32(b, d.H + d.BiSize) == 0 && Le.I32(b, d.H + d.BiSize + 4) == 0xffffff)
            {
                // A black-and-white DIB goes to gdi32 with the recorded (never recoloured) text and
                // background colours: CreateBitmapForDC @1800450a0 makes it a monochrome bitmap (not
                // at all when biHeight does not fit a USHORT -- a top-down DIB), then BitBlt /
                // StretchBlt from it, zeros in the text colour, ones in the background colour.
                MonoBlit(b, d, xd, yd, wd, hd, xs, ys, ws, hs, rop);
                return;
            }
            // OutputDIB's source y counts from the bottom: biHeight - ySrc - srcH.
            int h = Math.Abs(d.Height);
            OutputDibWmf(b, d, xd, yd, wd, hd, xs, h - hs - ys, ws, hs, rop);
        }

        void MonoBlit(byte[] b, in WmfDib d, int xd, int yd, int wd, int hd, int xs, int ys, int ws, int hs, int rop)
        {
            if (d.Height < 0 || d.Height > 0xffff || d.W > 0xffff) return;
            int w = d.W, h = d.Height, stride = ((w + 31) >> 5) << 2;
            // SetDIBits from after the two palette entries
            int bits = d.H + d.BiSize + 8;
            if (bits + stride * h > b.Length) return;
            Color fore = GdiColor(_rawText), back = GdiColor(_rawBk);
            using (var bm = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        bool on = (b[bits + (h - 1 - y) * stride + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                        bm.SetPixel(x, y, on ? back : fore);
                    }
                _srcBpp = 1;
                _dibBlit = false;
                Blit(bm, xd, yd, wd, hd, new RectangleF(xs, ys, ws, hs), rop);
            }
        }

        /// <summary>gdi32 META_DIBBITBLT / META_DIBSTRETCHBLT (when GDI+ hands them over).</summary>
        void GdiDibBlit(int fn, byte[] b, int o, int n) => DibBitBltWmf(fn | 0x100, b, o, n);

        /// <summary>MfEnumState::OutputDIB @1800b7eb0 on a WMF DIB: rewritten when the playback
        /// recolours (or the colours are palette indices), then stretched as Raster.cs does it.</summary>
        void OutputDibWmf(byte[] b, in WmfDib d, int xd, int yd, int wd, int hd, int xs, int ys, int ws, int hs, int rop)
        {
            Bitmap bm = null;
            bool modified = false;
            if (Rc != null || (d.Usage == 1 && d.Bpp <= 8))
            {
                if (ModifyDib(b, d.H, d.BitsAt, d.Usage, ColorAdjustType.Bitmap, out byte[] info, out byte[] bits, out _, d.NumPal))
                {
                    // the modified record must hold the rewritten DIB
                    bm = DibFromInfo(info, 0, info.Length, bits, 0, bits.Length);
                    modified = true;
                }
            }
            if (!modified)
            {
                // OutputDIB with no bits: GetDibBits again; a biSizeImage smaller than the bits is not drawn
                int sizeImage = Le.I32(b, d.H + 20);
                if (sizeImage != 0 && sizeImage < d.BitsSize) return;
                bm = BitmapOf(b, d);
            }
            if (bm == null) return;
            _srcBpp = d.Bpp;
            _dibBlit = true;
            try
            {
                // MfEnumState::OutputDIB clamps the source rectangle into the DIB.
                int w = bm.Width, h = bm.Height;
                xs = Math.Clamp(xs, 0, w);
                ys = Math.Clamp(ys, 0, h);
                if (xs + ws < 0) ws = -xs;
                if (xs + ws > w) ws = w - xs;
                if (ys + hs < 0) hs = -ys;
                if (ys + hs > h) hs = h - ys;
                if (Math.Abs(wd) < 1 || Math.Abs(hd) < 1) return;
                // GDI+ sets the stretch mode: HALFTONE for a SRCCOPY, else COLORONCOLOR.
                _dc.StretchMode = rop == 0x00CC0020 ? 4 : 3;
                Blit(bm, xd, yd, wd, hd, new RectangleF(xs, h - ys - hs, ws, hs), rop);
            }
            finally { _dibBlit = false; bm.Dispose(); }
        }

        /// <summary>META_SETDIBTODEV (played by gdi32, never rewritten): usage, scan count, start scan,
        /// ySrc, xSrc, height, width, yDst, xDst, the DIB, whose bits are the band of scans only.</summary>
        void SetDibToDevWmf(byte[] b, int o, int n)
        {
            short P(int i) => Le.I16(b, o + i * 2);
            if (n < 18 + 40) return;
            int usage = (ushort)P(0), scans = (ushort)P(1), start = (ushort)P(2);
            int ys = P(3), xs = P(4), h = P(5), w = P(6), yd = P(7), xd = P(8);
            int hdr = o + 18;
            int biSize = Le.I32(b, hdr), bw = Le.I32(b, hdr + 4), bh = Le.I32(b, hdr + 8), bpp = Le.U16(b, hdr + 14), comp = Le.I32(b, hdr + 16), clrUsed = Le.I32(b, hdr + 32);
            if (biSize < 40) return;
            int ncol = bpp <= 8 ? (clrUsed != 0 ? Math.Min(clrUsed, 1 << bpp) : 1 << bpp) : comp == 3 ? 3 : 0;
            int pal = bpp <= 8 && usage == 1 ? ncol * 2 : ncol * 4;
            int bitsAt = hdr + biSize + pal;
            long stride = (((long)bw * bpp + 31) >> 3) & ~3L;
            int rows = Math.Min(scans, Math.Abs(bh));
            if (bitsAt + stride * rows > o + n) return;
            // the band: rows start .. start + scans of the DIB, counted from its bottom
            var info = new byte[40 + ncol * 4];
            Buffer.BlockCopy(b, hdr, info, 0, 40);
            Le.W32(info, 0, 40);
            Le.W32(info, 8, bh < 0 ? -rows : rows);
            Le.W32(info, 20, 0);
            if (bpp <= 8) Le.W32(info, 32, ncol);
            for (int i = 0; i < ncol; i++)
            {
                int q = 40 + i * 4;
                if (bpp <= 8 && usage == 1)
                {
                    Color c = GdiColor(0x01000000 | Le.U16(b, hdr + biSize + i * 2));
                    info[q] = c.B; info[q + 1] = c.G; info[q + 2] = c.R;
                }
                else Buffer.BlockCopy(b, hdr + biSize + i * 4, info, q, 4);
            }
            Bitmap bm = DibFromInfo(info, 0, info.Length, b, bitsAt, (int)(stride * rows));
            if (bm == null) return;
            _srcBpp = bpp;
            _dibBlit = false;
            try
            {
                // SetDIBitsToDevice: the source rectangle (from the DIB's bottom) cut to the band.
                int y0 = Math.Max(ys, start), y1 = Math.Min(ys + h, start + rows);
                if (y1 <= y0 || w <= 0) return;
                int top = rows - (y1 - start);
                // SetDIBitsToDevice: only the destination's origin is logical; the pixels are copied
                // one for one (the origin through bCvtPts1 to the nearest pixel).
                if (Gdi)
                {
                    GdiXform m = TargetWtoD();
                    m.Point(xd, yd, out int fx, out int fy);
                    int dx0 = ((fx >> 3) + 1) >> 1, dy0 = (((fy >> 3) + 1) >> 1) + (ys + h - y1);
                    int ch = y1 - y0;
                    RasterBlit(bm, new[] { new PointF(dx0, dy0), new PointF(dx0 + w, dy0), new PointF(dx0, dy0 + ch) }, new RectangleF(xs, top, w, ch), 0x00CC0020, false);
                    return;
                }
                Blit(bm, xd, yd + (ys + h - y1), w, y1 - y0, new RectangleF(xs, top, w, y1 - y0), 0x00CC0020);
            }
            finally { bm.Dispose(); }
        }

        /// <summary>META_BITBLT / META_STRETCHBLT of a Bitmap16 (CreateBitmap into a compatible DC): a
        /// monochrome bitmap is drawn with the text colour for its zeros and the background colour
        /// for its ones. Without a bitmap (the nine- or eleven-word form) the source is not read.</summary>
        void OldBlit(int fn, byte[] b, int o, int n)
        {
            short P(int i) => i * 2 + 2 <= n ? Le.I16(b, o + i * 2) : (short)0;
            int rop = Le.I32(b, o);
            bool stretch = (fn & 0xff) == 0x23;
            int words = n / 2;
            int xd, yd, wd, hd, xs, ys, ws, hs;
            if (words == (fn >> 8))
            {
                // no bitmap: one reserved word before the destination
                if (stretch) { hs = P(2); ws = P(3); ys = P(4); xs = P(5); hd = P(7); wd = P(8); yd = P(9); xd = P(10); }
                else { ys = P(2); xs = P(3); hd = P(5); wd = P(6); yd = P(7); xd = P(8); ws = wd; hs = hd; }
                if (UsesSource(rop)) return;
                PatBltWmf(xd, yd, wd, hd, rop);
                return;
            }
            // With a Bitmap16, PlayMetaFileRecord (@1800459a0) selects the bitmap into a compatible
            // DC and blits only when that selection fails: nothing is drawn.
            if (words != (fn >> 8)) return;
            int at = stretch ? 20 : 16;
            if (stretch) { hs = P(2); ws = P(3); ys = P(4); xs = P(5); hd = P(6); wd = P(7); yd = P(8); xd = P(9); }
            else { ys = P(2); xs = P(3); hd = P(4); wd = P(5); yd = P(6); xd = P(7); ws = wd; hs = hd; }
            if (at + 10 > n) return;
            int bw = Le.I16(b, o + at + 2), bh = Le.I16(b, o + at + 4), wb = Le.I16(b, o + at + 6);
            int planes = b[o + at + 8], bpp = b[o + at + 9];
            if (planes != 1 || bpp != 1 || bw <= 0 || bh <= 0 || at + 10 + wb * bh > n) return;
            Color fore = _dc.TextColor, back = _dc.BkColor;
            using (var bm = new Bitmap(bw, bh, PixelFormat.Format32bppArgb))
            {
                for (int y = 0; y < bh; y++)
                    for (int x = 0; x < bw; x++)
                    {
                        bool on = (b[o + at + 10 + y * wb + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                        bm.SetPixel(x, y, on ? back : fore);
                    }
                _srcBpp = 1;
                _dibBlit = false;
                Blit(bm, xd, yd, wd, hd, new RectangleF(xs, ys, ws, hs), rop);
            }
        }

        // ---- pattern brushes -----------------------------------------------------------------------

        /// <summary>WmfEnumState::DibCreatePatternBrush @1800b6c68.</summary>
        void DibCreatePatternBrushWmf(byte[] b, int o, int n)
        {
            if (n < 0x2c) return;
            int style = Le.U16(b, o), usage = Le.U16(b, o + 2);
            int h = o + 4;
            if (style == 3)
            {
                // BS_PATTERN: solid black when recolouring, unless a black-and-white DIB.
                if (Rc != null && !(usage == 0 && IsBlackWhitePattern(b, h, n - 4))) { SolidBlackBrush(); return; }
                GdiWmf(0x0142, b, o, n);
                return;
            }
            if (!MeasureDib(b, h, n - 4, usage, out WmfDib d)) return;
            if ((Rc != null || (d.Usage == 1 && d.Bpp <= 8)) && ModifyDib(b, h, d.BitsAt, d.Usage, ColorAdjustType.Brush, out byte[] info, out byte[] bits, out _, d.NumPal))
            {
                Bitmap bm = DibFromInfo(info, 0, info.Length, bits, 0, bits.Length);
                PutWmf(new GdiBrush { Style = 3, Pattern = bm });
                return;
            }
            GdiWmf(0x0142, b, o, n);
        }

        static bool IsBlackWhitePattern(byte[] b, int h, int avail)
        {
            if (avail < 40 + 8) return false;
            int biSize = Le.I32(b, h);
            if (biSize < 40 || biSize + 8 > avail) return false;
            if (Le.U16(b, h + 14) != 1 || Le.U16(b, h + 12) != 1) return false;
            int np = NumPalEntries(biSize, 1, Le.I32(b, h + 16), Le.I32(b, h + 32));
            if (np != 2) return false;
            return Le.I32(b, h + biSize) == 0 && Le.I32(b, h + biSize + 4) == 0xffffff;
        }

        /// <summary>gdi32 META_DIBCREATEPATTERNBRUSH: BS_PATTERN made a bitmap for a compatible memory
        /// DC (monochrome) and a pattern brush of it; otherwise CreateDIBPatternBrushPt.</summary>
        void DibPatternBrushGdi(byte[] b, int o, int n)
        {
            if (n < 4) return;
            int style = Le.U16(b, o), usage = Le.U16(b, o + 2);
            int h = o + 4;
            if (!MeasureDibPlain(b, h, n - 4, style == 3 ? 0 : usage, out WmfDib d)) return;
            Bitmap bm = BitmapOf(b, d);
            if (bm == null) return;
            if (style == 3)
            {
                // the memory DC's bitmap is monochrome: each pixel black or white (the DIB's
                // colour against white), drawn with the text and background colours
                var mono = new Bitmap(bm.Width, bm.Height, PixelFormat.Format32bppArgb);
                for (int y = 0; y < bm.Height; y++)
                    for (int x = 0; x < bm.Width; x++)
                    {
                        Color c = bm.GetPixel(x, y);
                        mono.SetPixel(x, y, c.R == 255 && c.G == 255 && c.B == 255 ? Color.White : Color.Black);
                    }
                bm.Dispose();
                PutWmf(new GdiBrush { Style = 3, Pattern = mono, Mono = true, OneBit = true });
                return;
            }
            PutWmf(new GdiBrush { Style = 3, Pattern = bm, OneBit = d.Bpp == 1 });
        }

        /// <summary>A DIB as GDI itself reads one (CheckAndGetBitmapBits): the colour table after the
        /// header, the bits after that.</summary>
        static bool MeasureDibPlain(byte[] b, int h, int avail, int usage, out WmfDib d)
        {
            d = default;
            if (avail < 40) return false;
            d.H = h;
            d.BiSize = Le.I32(b, h);
            if (d.BiSize < 40) return false;
            d.W = Le.I32(b, h + 4); d.Height = Le.I32(b, h + 8);
            d.Bpp = Le.U16(b, h + 14); d.Comp = Le.I32(b, h + 16); d.ClrUsed = Le.I32(b, h + 32);
            if (usage == 1 && d.Bpp > 8) usage = 0;
            d.Usage = usage;
            int ncol = d.Bpp <= 8 ? (d.ClrUsed != 0 ? Math.Min(d.ClrUsed, 1 << d.Bpp) : 1 << d.Bpp) : d.Comp == 3 ? 3 : 0;
            d.NumPal = ncol;
            if (!BitsSize(b, h, out d.BitsSize)) return false;
            d.BitsAt = h + d.BiSize + (usage == 1 ? ncol * 2 : ncol * 4);
            return d.BitsAt + d.BitsSize <= h + avail && d.BitsAt + d.BitsSize <= b.Length;
        }

        /// <summary>META_CREATEPATTERNBRUSH: a Bitmap16 (type, width, height, width bytes, planes,
        /// bits per pixel), the bits after 36 bytes: CreateBitmapIndirect + CreatePatternBrush.</summary>
        void PatternBrush16(byte[] b, int o, int n)
        {
            if (n + 6 < 0x2a) return;
            int w = Le.I16(b, o + 2), h = Le.I16(b, o + 4), wb = Le.I16(b, o + 6);
            int planes = b[o + 8], bpp = b[o + 9];
            if (w <= 0 || h <= 0 || planes != 1 || bpp != 1 || 36 + wb * h > n) return;
            var bm = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool on = (b[o + 36 + y * wb + (x >> 3)] & (0x80 >> (x & 7))) != 0;
                    bm.SetPixel(x, y, on ? Color.White : Color.Black);
                }
            PutWmf(new GdiBrush { Style = 3, Pattern = bm, Mono = true, OneBit = true });
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
