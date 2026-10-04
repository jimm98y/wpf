// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// An EmfPlusDrawDriverString played through Graphics' public API: glyphs placed at their baseline
// origins (DriverStringOptionsRealizedAdvance: the first origin, then each glyph's advance), glyph
// indices turned back into the characters the face maps to them unless they are characters already
// (DriverStringOptionsCmapLookup), the record's matrix applied to the glyphs.
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Runtime.CompilerServices;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static class GpDriverString
    {
        const int CmapLookup = 1, Vertical = 2, RealizedAdvance = 4;

        static readonly ConditionalWeakTable<TrueTypeFont, Dictionary<int, char>> s_reverse = new ConditionalWeakTable<TrueTypeFont, Dictionary<int, char>>();

        static Dictionary<int, char> Reverse(TrueTypeFont face)
        {
            return s_reverse.GetValue(face, f =>
            {
                var d = new Dictionary<int, char>();
                for (int c = 0x20; c <= 0xffff; c++)
                {
                    if (c >= 0xd800 && c <= 0xdfff) continue;
                    int g = f.GlyphIndex((char)c);
                    if (g != 0 && !d.ContainsKey(g)) d[g] = (char)c;
                }
                return d;
            });
        }

        public static void Draw(Graphics g, ushort[] glyphs, Font font, Brush brush, PointF[] positions, int options, Matrix matrix)
        {
            if (glyphs.Length == 0) return;
            // GDI+'s own DriverStringImager where the engine models it.
            if (g.EngineDrawDriverString(glyphs, font, brush, positions, options, matrix)) return;
            int style = ((font.Style & FontStyle.Bold) != 0 ? 1 : 0) | ((font.Style & FontStyle.Italic) != 0 ? 2 : 0);
            TrueTypeFont face = (options & CmapLookup) == 0 ? PrintText.Face(font.FontFamily.Name, style) : null;
            Dictionary<int, char> rev = face != null ? Reverse(face) : null;
            FontFamily fam = font.FontFamily;
            float em = fam.GetEmHeight(font.Style);
            float ascent = em > 0 ? font.Size * fam.GetCellAscent(font.Style) / em : font.Size * 0.9f;
            GraphicsState st = g.Save();
            try
            {
                if (matrix != null) g.MultiplyTransform(matrix, MatrixOrder.Prepend);
                using (var sf = new StringFormat(StringFormat.GenericTypographic))
                {
                    sf.FormatFlags |= StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces;
                    if ((options & Vertical) != 0) sf.FormatFlags |= StringFormatFlags.DirectionVertical;
                    PointF pen = positions.Length > 0 ? positions[0] : PointF.Empty;
                    for (int i = 0; i < glyphs.Length; i++)
                    {
                        char c;
                        if ((options & CmapLookup) != 0) c = (char)glyphs[i];
                        else if (rev == null || !rev.TryGetValue(glyphs[i], out c)) continue;
                        PointF at = (options & RealizedAdvance) != 0 || i >= positions.Length ? pen : positions[i];
                        string s = c.ToString();
                        g.DrawString(s, font, brush, new PointF(at.X, at.Y - ascent), sf);
                        if ((options & RealizedAdvance) != 0)
                        {
                            SizeF size = g.MeasureString(s, font, PointF.Empty, sf);
                            if ((options & Vertical) != 0) pen.Y += size.Height; else pen.X += size.Width;
                        }
                    }
                }
            }
            finally
            {
                g.Restore(st);
            }
        }
    }
}
