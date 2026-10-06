// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// WHAT GDI ACTUALLY ADVANCES BY, asked three ways for one glyph.
//
// The compatible-advance work reads 'hdmx' and believes it. Arial Italic 'w' at 16ppem has a linear
// advance of 11.555 and an hdmx entry of 9, which would put 11.2px of ink into a 9px cell -- a
// 3.2px overhang that is large enough to be worth checking rather than assuming. The single-glyph
// oracles cannot see a layout advance at all.
//
// GetCharWidthI is the glyph-indexed width, GetCharABCWidthsI splits it into bearings, and
// GetTextExtentPoint32 measures a RUN, which is the number layout actually uses. If the three
// disagree, the one that matters is the run.
//
// WPF_GDI_ADV=family/chars/ppem[/B|I]
//

using System;
using System.Runtime.InteropServices;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public class GdiAdvanceProbe
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct LOGFONTW
        {
            public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
            public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet;
            public byte lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ABC { public int abcA; public uint abcB; public int abcC; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx, cy; }

        [DllImport("gdi32.dll", EntryPoint = "GetCharWidthFloatW")]
        private static extern bool GetCharWidthFloatW(IntPtr dc, uint first, uint last, float[] buf);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateFontIndirectW(ref LOGFONTW lf);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool GetCharWidthI(IntPtr hdc, uint first,
                                        uint count, ushort[]? gi, int[] widths);
        [DllImport("gdi32.dll")] private static extern bool GetCharABCWidthsI(IntPtr hdc, uint first,
                                        uint count, ushort[]? gi, ABC[] abc);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetTextExtentPoint32W(IntPtr hdc, string s, int c, out SIZE sz);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetGlyphIndicesW(IntPtr hdc, string s, int c, ushort[] gi, uint fl);

        [Fact]
        public void WhatGdiAdvancesBy()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "GDI is the subject");
            string? spec = Environment.GetEnvironmentVariable("WPF_GDI_ADV");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_GDI_ADV=family/chars/ppem[/style]");
            string[] parts = spec!.Split('/');
            int ppem = int.Parse(parts[2]);
            string style = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";

            var lf = new LOGFONTW
            {
                lfHeight = -ppem,
                lfWeight = style.Contains("B") ? 700 : 400,
                lfItalic = (byte) (style.Contains("I") ? 1 : 0),
                lfCharSet = 1,
                lfQuality = 5,                       // CLEARTYPE_QUALITY
                // The FILE STEM is not a GDI face name; see WindowsGlyphParityTests.GdiFamily.
                lfFaceName = WindowsGlyphParityTests.GdiFamily(parts[0]),
            };
            IntPtr font = CreateFontIndirectW(ref lf);
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            IntPtr old = SelectObject(dc, font);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("== " + parts[0] + " " + style + " @" + ppem
                          + "   GetCharWidthI | ABC (a,b,c) | run extent for 10 of them");
            try
            {
                foreach (char c in parts[1])
                {
                    var gi = new ushort[1];
                    GetGlyphIndicesW(dc, c.ToString(), 1, gi, 0);
                    var w = new int[1];
                    GetCharWidthI(dc, 0, 1, gi, w);
                    var abc = new ABC[1];
                    bool okAbc = GetCharABCWidthsI(dc, 0, 1, gi, abc);
                    GetTextExtentPoint32W(dc, new string(c, 10), 10, out SIZE sz);
                    // FRACTIONAL, which is the only GDI call here that does not round the
                    // advance away. GetCharWidthI, the ABC widths and the run extent all report
                    // whole pixels, so none can say whether GDI's fitted advance is 13.000 or
                    // 13.047 -- and that difference is exactly what the compatible-width phase
                    // divides by. Arial Bold 'X'@20 wants a numerator of 834 or 835 sixty-fourths
                    // to reproduce GDI's phase at its one pinned node, against the 832 our own
                    // bi-level pass measures: a sixteenth of a pixel.
                    // <para>AND IT HAS NO EXTRA PRECISION, which is the finding. Despite the name
                    // it returns exactly charWidth/16: Arial Bold 'X' reads 0.75000 / 0.81250 /
                    // 0.87500 / 0.93750 at 18/20/21/22ppem against whole-pixel advances of
                    // 12/13/14/15. So GDI exposes no fractional advance anywhere -- not here, not
                    // through the ABC widths, not through the run extent -- and the phase
                    // numerator cannot be measured this way. Kept so the column is on the record
                    // and nobody spends the afternoon on it twice.</para>
                    var wf = new float[1];
                    bool okF = GetCharWidthFloatW(dc, c, c, wf);
                    sb.AppendLine("   '" + c + "' gid=" + gi[0]
                        + "   charWidth " + w[0]
                        + "   float " + (okF ? wf[0].ToString("0.00000")
                                             + " (" + (wf[0] * 64f).ToString("0.0") + "/64)" : "n/a")
                        + "   abc " + (okAbc ? abc[0].abcA + "," + abc[0].abcB + "," + abc[0].abcC : "n/a")
                        + "   run10 " + sz.cx + " -> per glyph " + (sz.cx / 10.0).ToString("0.00"));
                }
            }
            finally
            {
                SelectObject(dc, old); DeleteObject(font); DeleteDC(dc);
            }
            Console.Error.Write(sb.ToString());
        }
    }
}
