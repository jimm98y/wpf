// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// An EMF's GDI records played with ImageAttributes: GDI+ (gdiplus.dll arm64, 10.0.26100) rewrites
// the colours of the records before GDI plays them.
//
//   MfEnumState::ModifyColor @1800b7a70     a COLORREF (a PALETTEINDEX resolved through the
//        selected palette, other high bytes dropped) through the recolor object of the record's
//        type -- or of the player's own type when that is not Default -- as opaque ARGB
//   EmfEnumState::ProcessRecord @1800b4ed0  SETTEXTCOLOR as Text, SETBKCOLOR as Brush (both also
//        kept for the monochrome blits), SETPIXELV as Pen, EXTFLOODFILL as Brush
//   EmfEnumState::CreatePen @1800b44d0       its colour as Pen unless PS_NULL
//   EmfEnumState::ExtCreatePen @1800b46a8    as Pen when its brush is solid or hatched
//   EmfEnumState::CreateBrushIndirect @1800b40c0  as Brush unless BS_NULL
//   EmfEnumState::SelectObject @1800b55f8    a stock brush or pen of RecolorStockObjectList
//        (@1802ac340: the five solid brushes and the two pens) is replaced, once per playback, by
//        one of the recoloured colour
//   EmfEnumState::CreateModifiedDib @1800b4390 -> MfEnumState::ModifyDib @1800b7b90   the DIB of
//        a blit or a DIB pattern brush, as Bitmap: a palette's entries (a palette-index one made
//        RGB), or the pixels of a 16, 24 or 32bpp DIB (BI_RGB or BI_BITFIELDS) into a 24bpp one
//        (Modify16BppDib @1800b75e0 expands 5 and 6 bits by repeating them); a black-and-white
//        monochrome DIB is left alone unless the blit is SRCCOPY (or it is a pattern brush)
//   MfEnumState::OutputDIB @1800b7eb0        a modified DIB whose header, colours and bits do not
//        fit in the record they came from is not drawn at all
//

using System.Drawing.Imaging;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpGdiPlayer
    {
        GpRecolor _rc;
        bool _rcInit;

        /// <summary>The player's GpRecolor (MetafilePlayer::PrepareToPlay), or null.</summary>
        GpRecolor Rc
        {
            get
            {
                if (!_rcInit)
                {
                    _rcInit = true;
                    // The enum state has a recolor only when the ImageAttributes recolour at all
                    // (an ImageAttributes of wrap mode alone rewrites nothing).
                    GpRecolor rc = _s.Attributes?.Recolor;
                    _rc = rc != null && rc.HasRecoloring((ColorAdjustType)6) ? rc : null;
                    _rc?.Flush();
                }
                return _rc;
            }
        }

        // The player's own adjust type (MfEnumState +0x98): DrawImage of a metafile plays as Default.
        const ColorAdjustType PlayerAdjust = ColorAdjustType.Default;

        /// <summary>MfEnumState::ModifyColor @1800b7a70 on a COLORREF.</summary>
        int ModifyColor(int c, ColorAdjustType type)
        {
            ColorAdjustType t = PlayerAdjust != ColorAdjustType.Default ? PlayerAdjust : type;
            uint cr = (uint)c;
            if ((cr & 0xff000000) != 0)
            {
                if ((cr & 0xff000000) == 0x01000000)
                {
                    Color p = PaletteEntry((int)(cr & 0xff), out bool ok);
                    cr = ok ? (uint)(p.R | p.G << 8 | p.B << 16) : 0;
                }
                else cr &= 0xffffff;
            }
            GpRecolorObject o = Rc?.Use(t);
            if (o != null)
            {
                var px = new uint[] { 0xff000000u | (cr & 0xff) << 16 | (cr & 0xff00) | (cr >> 16 & 0xff) };
                o.ColorAdjust(px, 0, 1);
                uint v = px[0];
                cr = (v >> 16 & 0xff) | (v & 0xff) << 16 | (v & 0xff00);
            }
            return (int)cr;
        }

        /// <summary>A record's colour as the DC gets it: modified when the playback recolours.</summary>
        Color RecordColor(int c, ColorAdjustType type) => ColorRef(Rc != null ? ModifyColor(c, type) : c);

        // The default palette (DEFAULT_PALETTE): the twenty static colours.
        static readonly int[] s_defaultPalette =
        {
            0x000000, 0x000080, 0x008000, 0x008080, 0x800000, 0x800080, 0x808000, 0xc0c0c0, 0xc0dcc0, 0xf0caa6,
            0xf0fbff, 0xa4a0a0, 0x808080, 0x0000ff, 0x00ff00, 0x00ffff, 0xff0000, 0xff00ff, 0xffff00, 0xffffff,
        };

        Color PaletteEntry(int i, out bool ok)
        {
            ok = i < s_defaultPalette.Length;
            int c = ok ? s_defaultPalette[i] : 0;
            return Color.FromArgb(c >> 16 & 0xff, c >> 8 & 0xff, c & 0xff);
        }

        // ---- stock objects (EmfEnumState::SelectObject) -------------------------------------------

        // RecolorStockObjectList @1802ac340: stock id, COLORREF, brush (else pen).
        static readonly (int Id, int Color, bool Brush)[] s_recolorStock =
        {
            (0, 0xffffff, true), (1, 0xc0c0c0, true), (2, 0x808080, true), (3, 0x404040, true), (4, 0x000000, true),
            (6, 0xffffff, false), (7, 0x000000, false),
        };

        readonly GdiObj[] _recolorStock = new GdiObj[7];

        /// <summary>A stock object selected while the playback recolours: its recoloured copy, made
        /// once (true), or false when GDI's own stock object is selected.</summary>
        bool SelectRecoloredStock(int s)
        {
            if (Rc == null || s >= 8) return false;
            for (int i = 0; i < s_recolorStock.Length; i++)
            {
                if (s_recolorStock[i].Id != s) continue;
                GdiObj o = _recolorStock[i];
                if (o == null)
                {
                    if (s_recolorStock[i].Brush)
                        o = new GdiBrush { Style = 0, Color = ColorRef(ModifyColor(s_recolorStock[i].Color, ColorAdjustType.Brush)) };
                    else
                        o = new GdiPen { Style = 0, Width = 0, Color = ColorRef(ModifyColor(s_recolorStock[i].Color, ColorAdjustType.Pen)), Old = true };
                    _recolorStock[i] = o;
                }
                if (o is GdiBrush br) _dc.Brush = br;
                else _dc.Pen = (GdiPen)o;
                return true;
            }
            return false;
        }

        // ---- DIBs (EmfEnumState::CreateModifiedDib, MfEnumState::ModifyDib) -------------------------

        /// <summary>The DIB of a blit or pattern brush as GDI+ rewrites it for a recolouring
        /// playback. False: the DIB is left as it is. True: <paramref name="bm"/> is the modified
        /// DIB, or null with <paramref name="drop"/> when OutputDIB refuses it (a blit only).</summary>
        bool ModifiedDib(byte[] b, int rec, int offBmi, int cbBmi, int offBits, int cbBits, int usage, int rop, bool blit,
            out Bitmap bm, out bool drop, out int bpp)
        {
            bm = null; drop = false; bpp = 0;
            if (Rc == null || cbBmi < 40 || offBmi <= 0 || rec + offBmi + cbBmi > b.Length) return false;
            int h0 = rec + offBmi;
            int biSize = Le.I32(b, h0);
            if (biSize < 40 || h0 + biSize > b.Length) return false;
            int width = Le.I32(b, h0 + 4), height = Le.I32(b, h0 + 8);
            int bitCount = Le.U16(b, h0 + 14), comp = Le.I32(b, h0 + 16), clrUsed = Le.I32(b, h0 + 32);
            int absH = height < 0 ? -height : height;
            int numPal;
            if (bitCount <= 8) numPal = clrUsed != 0 ? clrUsed : 1 << bitCount;
            else numPal = comp == 3 ? 3 : 0;
            if (width <= 0 || absH == 0) return false;
            int stride = (int)((((long)width * bitCount + 31) >> 5) << 2);
            long bitsSize = comp == 0 || comp == 3 ? (long)stride * absH : Le.I32(b, h0 + 20);
            if (bitsSize <= 0) return false;
            int palBytes = usage == 1 ? 2 : 4;
            int pal0 = h0 + biSize;
            // A black-and-white monochrome DIB keeps its colours unless the blit copies it.
            if (numPal == 2 && rop != 0x00cc0020 && pal0 + 8 <= b.Length && Le.I32(b, pal0) == 0 && Le.I32(b, pal0 + 4) == 0xffffff)
                return false;
            // GetModifiedDibSize @1801f1750.
            if (usage == 1 && (bitCount > 8 || comp == 3)) usage = 0;
            if (bitCount <= 8)
            {
                if (numPal == 0 || (uint)(comp - 10) < 3) return false;
            }
            else if (comp != 0 && comp != 3) return false;
            if (rec + offBits + bitsSize > b.Length && bitCount <= 8) return false;
            byte[] info, bits;
            if (bitCount <= 8)
            {
                info = new byte[biSize + numPal * 4];
                Buffer.BlockCopy(b, h0, info, 0, biSize);
                Le.W32(info, 32, numPal);
                for (int i = 0; i < numPal; i++)
                {
                    int c;
                    if (usage == 1 && comp != 3)
                        c = ModifyColor(Le.U16(b, pal0 + i * 2) | 0x1000000, ColorAdjustType.Bitmap);
                    else
                    {
                        int q = pal0 + i * 4;
                        c = ModifyColor(b[q + 2] | b[q + 1] << 8 | b[q] << 16, ColorAdjustType.Bitmap);
                    }
                    info[biSize + i * 4] = (byte)(c >> 16);
                    info[biSize + i * 4 + 1] = (byte)(c >> 8);
                    info[biSize + i * 4 + 2] = (byte)c;
                }
                bits = new byte[bitsSize];
                Buffer.BlockCopy(b, rec + offBits, bits, 0, (int)Math.Min(bitsSize, Math.Max(0, b.Length - rec - offBits)));
                bpp = bitCount;
            }
            else
            {
                if (rec + offBits + bitsSize > b.Length) return false;
                info = new byte[40];
                Le.W32(info, 0, 40); Le.W32(info, 4, width); Le.W32(info, 8, height);
                info[12] = 1; info[14] = 24;
                int ostride = (width * 3 + 3) & ~3;
                bits = new byte[(long)ostride * absH];
                int mask0 = 0, mask1 = 0, mask2 = 0;
                bool masks = numPal == 3 && pal0 + 12 <= b.Length;
                if (masks) { mask0 = Le.I32(b, pal0); mask1 = Le.I32(b, pal0 + 4); mask2 = Le.I32(b, pal0 + 8); }
                int src = rec + offBits;
                for (int y = 0; y < absH; y++)
                {
                    int sp = src + y * stride, dp = y * ostride;
                    for (int x = 0; x < width; x++, dp += 3)
                    {
                        int c;
                        if (bitCount == 16) c = Pixel16(Le.U16(b, sp + x * 2), masks, mask0, mask1, mask2);
                        else if (bitCount == 24)
                        {
                            int ir = masks ? Index24(mask0) : 2, ig = masks ? Index24(mask1) : 1, ib = masks ? Index24(mask2) : 0;
                            int p = sp + x * 3;
                            c = b[p + ir] | b[p + ig] << 8 | b[p + ib] << 16;
                        }
                        else
                        {
                            int ir = masks ? Index32(mask0) : 2, ig = masks ? Index32(mask1) : 1, ib = masks ? Index32(mask2) : 0;
                            int p = sp + x * 4;
                            c = b[p + ir] | b[p + ig] << 8 | b[p + ib] << 16;
                        }
                        c = ModifyColor(c, ColorAdjustType.Bitmap);
                        bits[dp] = (byte)(c >> 16); bits[dp + 1] = (byte)(c >> 8); bits[dp + 2] = (byte)c;
                    }
                }
                bpp = 24;
            }
            // MfEnumState::OutputDIB: the DIB must fit in the record's data.
            if (blit && info.Length + bits.LongLength > Le.I32(b, rec + 4) - 8) { drop = true; return true; }
            bm = DibFromInfo(info, 0, info.Length, bits, 0, bits.Length);
            return true;
        }

        static int MaskShift(int m) { int s = 0; while (s < 32 && ((m >> s) & 1) == 0) s++; return s; }

        static int MaskBits(int m) { int n = 0; while (m != 0) { n += m & 1; m = (int)((uint)m >> 1); } return n; }

        /// <summary>Modify16BppDib @1800b75e0: a 16-bit pixel as a COLORREF (555 unless masked).</summary>
        static int Pixel16(int v, bool masks, int m0, int m1, int m2)
        {
            int rm = 0x7c00, gm = 0x3e0, bm = 0x1f, rs = 10, gs = 5, bs = 0, rn = 5, gn = 5, bn = 5;
            if (masks)
            {
                rm = m0 & 0xffff; gm = m1 & 0xffff; bm = m2 & 0xffff;
                rs = MaskShift(rm); gs = MaskShift(gm); bs = MaskShift(bm);
                rn = MaskBits(rm >> rs); gn = MaskBits(gm >> gs); bn = MaskBits(bm >> bs);
            }
            int s = (short)v;
            int r = (s & rm) >> rs, g = (s & gm) >> gs, bl = (s & bm) >> bs;
            r = ((r << rn) | r) >> ((rn - 4) * 2) & 0xff;
            g = ((g << gn) | g) >> ((gn - 4) * 2) & 0xff;
            bl = ((bl << bn) | bl) >> ((bn - 4) * 2) & 0xff;
            return r | g << 8 | bl << 16;
        }

        static int Index24(int m) => m == 0xff0000 ? 2 : m == 0xff00 ? 1 : 0;

        static int Index32(int m) => m == 0xff0000 ? 2 : m == 0xff00 ? 1 : m == unchecked((int)0xff000000) ? 3 : 0;
    }
}
