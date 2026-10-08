// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A GDI font as an EMF DC realizes it from a LOGFONTW: what GDI measures when it records an
// ExtTextOutW (the advances it writes when it is handed none, the box it accumulates) and what it
// draws when the record is played. The face is found the way the port finds every GDI face
// (TextMetrics.FontFor: the styled file, or the regular one with the style simulated); the numbers
// are GDI's own, which the face already knows:
//
//   tmAscent / tmDescent   TrueTypeFont.TryGetGdiLineMetrics (VDMX, else usWin rounded); a turned
//                          font (escapement) the usWin values rounded, a pixel more each
//   advances               TrueTypeFont.TryGetDeviceAdvance (hdmx, else the fitted phantoms)
//   GLYPHDATA fxA / fxAB   the columns the bi-level glyph lights (an EMF DC's realization), in 28.4
//
// Which face a LOGFONT realizes to is GDI's mapper (GpFontMapper): a vertical face ('@' + name),
// which only a face with a far-east charset has, or a name no installed family has, goes through
// it (for "@Times New Roman" at EASTEUROPE_CHARSET it picks "@Malgun Gothic": no FontMapper
// default for the charset, so every vertical face is scored and Malgun, linked to Segoe UI for
// the charset, is loaded first).
//
// The charset GDI+ puts in its LOGFONT (GpFontFace::GetCharset @1800865c0) is the first one
// EnumFontFamiliesExW reports for the face at DEFAULT_CHARSET (EnumFontFamExProcW @1801d4930 keeps
// the first TrueType entry): the ANSI code page's charset when the face covers it, else the face's
// code-page bits in fontdrvhost's order (vFillIFICharsets, as ChooseFontModel reads it).
//

using System.Collections.Generic;
using System.Globalization;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpGdiFont
    {
        public TrueTypeFont Face;
        /// <summary>For a SIMULATED italic, the same file without the synthesized slant: ttfd puts
        /// FO_SIM_ITALIC's shear into the matrix (bSetXform @14001c2c8), so a font whose matrix is
        /// not a plain scale is fitted unslanted and slanted by the scaler's post-transform.</summary>
        public TrueTypeFont UprightFace;
        public string FaceName = "";     // as asked, '@' stripped
        public bool Vertical;            // an '@' face
        public int Ppem;
        public int Ascent, Descent;      // tmAscent, tmDescent
        public int Escapement;           // tenths of a degree
        public int Width;                // lfWidth (0: the face's own aspect)
        public int Weight;
        public bool Italic, Underline, StrikeOut;
        public int Quality;

        static readonly Dictionary<string, GpGdiFont> s_cache = new Dictionary<string, GpGdiFont>();

        /// <summary>The font a LOGFONTW (92 bytes, or the head of an ENUMLOGFONTEXDVW) realizes to, or
        /// null when no face resolves.</summary>
        public static GpGdiFont FromLogFont(byte[] lf, int o = 0)
        {
            if (lf == null || lf.Length < o + 28) return null;
            int height = Le.I32(lf, o), esc = Le.I32(lf, o + 8), weight = Le.I32(lf, o + 16);
            bool italic = lf[o + 20] != 0, ul = lf[o + 21] != 0, so = lf[o + 22] != 0;
            int charset = lf[o + 23], quality = lf[o + 26], pitchFamily = lf[o + 27];
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 32 && o + 28 + i * 2 + 1 < lf.Length; i++)
            {
                char c = (char)Le.U16(lf, o + 28 + i * 2);
                if (c == 0) break;
                sb.Append(c);
            }
            return Get(sb.ToString(), height, esc, weight, italic, ul, so, quality, charset, pitchFamily);
        }

        public static GpGdiFont Get(string name, int height, int esc, int weight, bool italic, bool ul, bool so, int quality,
                                    int charset = 1, int pitchFamily = 0, int width = 0)
        {
            string key = name + "|" + height + "|" + width + "|" + esc + "|" + weight + "|" + italic + ul + so + "|" + quality + "|" + charset + "|" + pitchFamily;
            lock (s_cache)
            {
                if (s_cache.TryGetValue(key, out GpGdiFont f)) return f;
                f = Make(name, height, esc, weight, italic, ul, so, quality, charset, pitchFamily);
                if (f != null) f.Width = width;
                s_cache[key] = f;
                return f;
            }
        }

        static GpGdiFont Make(string name, int height, int esc, int weight, bool italic, bool ul, bool so, int quality,
                              int charset, int pitchFamily)
        {
            bool vertical = name.StartsWith("@", StringComparison.Ordinal);
            string face = vertical ? name.Substring(1) : name;
            bool bold = weight > 550;
            int sim = (bold ? 1 : 0) | (italic ? 2 : 0);
            // GDI's mapper decides the family (the style is the realization's, below).
            TrueTypeFont t = null;
            GpFontMapper.Match m = GpFontMapper.Map(name, (byte)charset, (byte)pitchFamily, weight, italic);
            if (m != null)
            {
                face = m.Face.BaseFamily;
                t = Resolve(face, sim);
            }
            if (t == null && !vertical) t = Resolve(face = name, sim);
            if (t == null) return null;
            int upem = t.UnitsPerEmForHinting;
            int ppem;
            if (height < 0) ppem = -height;
            else if (height > 0)
            {
                int cell = t.WinAscent + t.WinDescent;
                ppem = cell > 0 ? (int)Math.Round((double)height * upem / cell) : height;
                if (cell > 0 && t.HasVdmx && esc % 3600 == 0)
                    ppem = CellPpem(t, (int)Math.Floor((double)height / cell * 65536.0 + 0.5));
            }
            else ppem = 16;
            if (ppem <= 0) ppem = 1;
            var g = new GpGdiFont
            {
                Face = t, UprightFace = t.SynthesizesOblique && Resolve(face, sim & 1) is { SynthesizesOblique: false } u ? u : null,
                FaceName = face, Vertical = vertical, Ppem = ppem, Escapement = esc, Weight = weight,
                Italic = italic, Underline = ul, StrikeOut = so, Quality = quality,
            };
            if (esc % 3600 != 0)
            {
                // A turned font is not sized from VDMX. ttfd's bComputeMaxGlyph @14001b198 takes
                // a matrix that is not a plain scale through its rotated branch: the ascender and
                // descender are the face's extents -- usWinAscent / usWinDescent for a quarter
                // turn (flag bit 1, m00 = m11 = 0), the head box's yMax / -yMin at any other angle
                // -- each grown by upem/64, times the length of the ascender vector (16 ppem /
                // upem), rounded to 28.4 and taken up to the whole pixel; lQueryDEVICEMETRICS
                // @140011940 hands them to win32k as fxMaxAscender / fxMaxDescender, and
                // TEXTMETRIC's tmAscent / tmDescent are those (RFONT +0x158).
                bool quarter = esc % 900 == 0;
                int up = quarter ? t.WinAscent : t.HeadYMax, down = quarter ? t.WinDescent : -t.HeadYMin;
                float len = 16f * ppem / upem;
                int margin = upem >> 6;
                static int Round16(float v) => v < 0f ? -(int)Math.Floor(-v + 0.5) : (int)Math.Floor(v + 0.5);
                g.Ascent = (Round16((up + margin) * len) + 15) >> 4;
                g.Descent = (Round16((down + margin) * len) + 15) >> 4;
                if (esc % 1800 == 0)
                {
                    // A half turn is diagonal (flag bit 0) and takes the scale-only branch, but with
                    // m11 negative it is not quantized: the win ascent and descent FixMul'd by the
                    // 16.16 scale, whole pixels.
                    int sc = (int)Math.Floor((double)ppem / upem * 65536.0 + 0.5);
                    g.Ascent = (int)(((long)t.WinAscent * sc + 0x8000) >> 16);
                    g.Descent = (int)(((long)t.WinDescent * sc + 0x8000) >> 16);
                }
            }
            else if (!t.TryGetGdiLineMetrics(ppem, out g.Ascent, out g.Descent))
            {
                g.Ascent = (int)Math.Round(t.WinAscent * (double)ppem / upem);
                g.Descent = (int)Math.Round(t.WinDescent * (double)ppem / upem);
            }
            return g;
        }

        /// <summary>vQuantizeXform @14001ea70 (fontdrvhost) for a face with a 'VDMX' and a positive
        /// lfHeight (a CELL height, TT_FONTCONTEXT +0x28 bit 15 clear): the device cell is
        /// FixMul(m22, usWinAscent + usWinDescent); bSearchVdmxTable @14001e880 takes the first
        /// record whose yMax - yMin is that cell (a record past it ends the search); failing that,
        /// a walk from FixMul(m22, upem) one ppem at a time, each size's height its record's or the
        /// cell scaled, down while it is taller and up while it is shorter, stopping at the last
        /// size not taller. <paramref name="m22"/> is the notional-to-device y scale in 16.16; the
        /// answer is the ppem (+0x7c) the scale is then quantized to (upem over it).</summary>
        internal static int CellPpem(TrueTypeFont t, int m22)
        {
            int upem = t.UnitsPerEmForHinting, cell = t.WinAscent + t.WinDescent;
            int ppem = (int)(((long)m22 * upem + 0x8000) >> 16);
            if (cell <= 0) return ppem;
            int target = (int)(((long)m22 * cell + 0x8000) >> 16);
            for (int p = 1; p <= 255; p++)
            {
                if (!t.TryGetVdmxExtents(p, out int yMax, out int yMin)) continue;
                int h = yMax - yMin;
                if (h > target) break;
                if (h == target) return p;
            }
            bool down = false, up = false;
            for (int i = 0; i < 256 && ppem > 0; i++)
            {
                int h = t.TryGetVdmxExtents(ppem, out int yMax, out int yMin) ? yMax - yMin
                      : (int)(((long)ppem * cell + upem / 2) / upem);
                if (h == target) return ppem;
                if (h < target) { if (down) return ppem; ppem++; up = true; }
                else { ppem--; if (up) return ppem; down = true; }
            }
            return ppem;
        }

        static TrueTypeFont Resolve(string family, int sim)
        {
            if (string.IsNullOrEmpty(family) || FontFiles.Find(family, false, false) == null) return null;
            bool bold = (sim & 1) != 0, italic = (sim & 2) != 0;
            string path = FontFiles.Find(family, bold, italic);
            if (path == null) return TextMetrics.FontFor(sim, family) as TrueTypeFont;
            // The face the realization starts from, and what it still has to simulate, per axis:
            // the file's own 'head'.macStyle against the style asked (TextMetrics.FontFor's single
            // "styled file" answer drew Tahoma Bold Italic as upright bold, its file being the
            // bold one), opened at the family's own face in a collection (FontFor reads a .ttc
            // from byte zero, its 'ttcf' header, and Cambria silently became the default face).
            string key = path + "|" + family + "|" + sim;
            lock (s_faces)
            {
                if (s_faces.TryGetValue(key, out TrueTypeFont made)) return made;
                try
                {
                    byte[] bytes = System.IO.File.ReadAllBytes(path);
                    int sfnt = FontFiles.SfntOffset(bytes, family, bold, italic);
                    FontFiles.DeclaredStyle(bytes, sfnt, out bool fileBold, out bool fileItalic);
                    made = new TrueTypeFont(bytes, bold && !fileBold, italic && !fileItalic, sfnt);
                }
                catch (Exception) { made = null; }
                made ??= TextMetrics.FontFor(sim, family) as TrueTypeFont;
                s_faces[key] = made;
                return made;
            }
        }
        static readonly Dictionary<string, TrueTypeFont> s_faces = new Dictionary<string, TrueTypeFont>();

        /// <summary>The advance GDI spaces this glyph by, in whole pixels.</summary>
        public int GlyphAdvance(int gid)
        {
            if (Face.TryGetDeviceAdvance(gid, Ppem, out float a)) return (int)MathF.Round(a);
            return (int)MathF.Round(Face.Advance(gid) * Ppem / Face.PixelsPerEm);
        }

        /// <summary>The advance (whole device pixels) of a glyph of the font realized at
        /// <paramref name="ppemX"/> x <paramref name="ppemY"/> with the 16.16 x scale
        /// <paramref name="m00"/> (square: x's scale is y's): GLYPHDATA fxD, what ESTROBJ sums
        /// when ExtTextOut is handed no advances.</summary>
        public int DeviceAdvance(int gid, int ppemX, int ppemY, int m00, bool square, bool stretchInfo = false)
        {
            if (Face.TryGetStretchedAdvance(gid, m00, square, ppemX, ppemY, out float a, stretchInfo ? 1 : 2)) return (int)MathF.Round(a);
            return (int)(((long)m00 * Face.DesignAdvance(gid) + 0x8000) >> 16);
        }

        /// <summary>GetTextExtentExPoint's width of one character: its glyph through the cmap.</summary>
        public int CharAdvance(char c) => GlyphAdvance(Face.GlyphIndex(c));

        readonly Dictionary<int, (int A, int AB)> _ink = new Dictionary<int, (int, int)>();

        /// <summary>GLYPHDATA's fxA and fxAB: the first and past the last column the glyph's bi-level
        /// bitmap lights (an EMF DC realizes its fonts bi-level; dropout control included), in 28.4
        /// from the origin; an empty glyph is (0, 0).</summary>
        public (int A, int AB) Ink(int gid)
        {
            lock (_ink)
            {
                if (_ink.TryGetValue(gid, out var v)) return v;
                v = (0, 0);
                GdiPlusText.GreyGlyph g = GdiPlusText.Mono(Face, gid, Ppem, gridFit: true);
                if (g.Width > 0 && g.Height > 0) v = (g.Left * 16, (g.Left + g.Width) * 16);
                _ink[gid] = v;
                return v;
            }
        }

        // ---- the charset GDI+ asks for ------------------------------------------------------------

        static readonly (uint Bit, byte CharSet)[] s_fsCharsets =
        {
            (0x1, 0), (0x20000, 128), (0x80000, 129), (0x200000, 130), (0x40000, 134), (0x100000, 136), (0x20, 177), (0x40, 178),
            (0x8, 161), (0x10, 162), (0x80, 186), (0x2, 238), (0x4, 204), (0x10000, 222), (0x100, 163), (0x80000000, 2)
        };

        static (uint Bit, byte CharSet) AcpSignature(int acp) => acp switch
        {
            1250 => (0x2, 238), 1251 => (0x4, 204), 1253 => (0x8, 161), 1254 => (0x10, 162), 1255 => (0x20, 177), 1256 => (0x40, 178),
            1257 => (0x80, 186), 1258 => (0x100, 163), 874 => (0x10000, 222), 932 => (0x20000, 128), 936 => (0x40000, 134),
            949 => (0x80000, 129), 950 => (0x100000, 136), 1361 => (0x200000, 130), _ => (0x1, 0)
        };

        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern int GetACP();

        /// <summary>The system's ANSI code page: Windows' own, elsewhere the culture's.</summary>
        static int Acp
        {
            get
            {
                try { return OperatingSystem.IsWindows() ? GetACP() : CultureInfo.CurrentCulture.TextInfo.ANSICodePage; }
                catch (Exception) { return 1252; }
            }
        }

        /// <summary>GpFontFace::GetCharset: the first charset GDI enumerates for the face.</summary>
        public static byte Charset(TrueTypeFont t)
        {
            if (t == null) return 1;
            ReadOs2(t, out int version, out uint cp1, out bool symbol);
            if (version < 0 || version == 0 || cp1 == 0) return symbol ? (byte)2 : (byte)0;
            (uint sig, byte cur) = AcpSignature(Acp);
            if ((cp1 & sig) != 0 && (sig & 0x10060) == 0) return cur;
            foreach ((uint bit, byte cs) in s_fsCharsets)
                if ((bit != sig || (sig & 0x10060) != 0) && (bit & cp1) != 0) return cs;
            return 0;
        }

        static void ReadOs2(TrueTypeFont t, out int version, out uint cp1, out bool symbolCmap)
        {
            version = -1; cp1 = 0; symbolCmap = false;
            byte[] d = t.FontData;
            int b = t.SfntOffset;
            if (d == null || b + 12 > d.Length) return;
            int n = Be16(d, b + 4);
            for (int i = 0; i < n; i++)
            {
                int e = b + 12 + i * 16;
                if (e + 16 > d.Length) break;
                uint tag = (uint)Be32(d, e);
                int off = Be32(d, e + 8), len = Be32(d, e + 12);
                if (tag == 0x4F532F32 && off + 2 <= d.Length)       // 'OS/2'
                {
                    version = Be16(d, off);
                    if (version >= 1 && len >= 82 && off + 82 <= d.Length) cp1 = (uint)Be32(d, off + 78);
                }
                else if (tag == 0x636D6170 && off + 4 <= d.Length)  // 'cmap'
                {
                    int nt = Be16(d, off + 2);
                    for (int k = 0; k < nt && off + 4 + k * 8 + 4 <= d.Length; k++)
                        if (Be16(d, off + 4 + k * 8) == 3 && Be16(d, off + 6 + k * 8) == 0) symbolCmap = true;
                }
            }
        }

        static int Be16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
        static int Be32(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
    }
}
