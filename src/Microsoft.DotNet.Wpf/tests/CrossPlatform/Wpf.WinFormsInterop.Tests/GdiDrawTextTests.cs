// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// TextRenderer lays text out with a managed DrawTextEx (GdiDrawText, read out of user32.dll). This
// holds its DT_CALCRECT answers to user32's own, on Windows: the same strings, flags, rectangle
// widths and margins through both, with the margins .NET's TextRenderer passes (a sixth of the
// font's tmHeight each side, half as much again on the right).
//

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class GdiDrawTextTests
    {
        private static readonly string[] Texts =
        {
            "CheckBox", "Wrapped long caption", "Flat radio", "W", "&File", "Save &As...", "a && b",
            "Two\nlines", "Trailing break\n", "Tab\there", "Supercalifragilistic", "A very long sentence that wraps over several lines of text",
            "  leading spaces", "trailing spaces   ", "",
        };

        private static readonly TextFormatFlags[] Flags =
        {
            TextFormatFlags.Default, TextFormatFlags.NoPadding, TextFormatFlags.LeftAndRightPadding,
            TextFormatFlags.WordBreak, TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak,
            TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter, TextFormatFlags.SingleLine,
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine, TextFormatFlags.WordBreak | TextFormatFlags.Right,
            TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak, TextFormatFlags.ExpandTabs,
        };

        private static readonly int[] Widths = { int.MaxValue, 200, 60, 25, 1 };

        [Fact]
        public void MeasureText_IsUser32s()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the reference is user32's DrawTextEx");
            using var font = new Font("Segoe UI", 9f);
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            // Segoe UI 9pt at 96 dpi: a 12-pixel em.
            IntPtr hfont = CreateFont(-12, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, "Segoe UI");
            IntPtr old = SelectObject(dc, hfont);
            var misses = new StringBuilder();
            int compared = 0;
            try
            {
                GetTextMetrics(dc, out TEXTMETRIC tm);
                float h6 = tm.tmHeight / 6f;
                foreach (string text in Texts)
                    foreach (TextFormatFlags flags in Flags)
                        foreach (int width in Widths)
                        {
                            Size ours = TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), flags);
                            Size theirs = User32Measure(dc, text, new Size(width, int.MaxValue), flags, h6);
                            compared++;
                            if (ours != theirs)
                                misses.AppendLine($"'{text.Replace("\n", "\\n").Replace("\t", "\\t")}' {flags} w={width}: ours {ours}, user32 {theirs}");
                        }
            }
            finally
            {
                SelectObject(dc, old);
                DeleteObject(hfont);
                DeleteDC(dc);
            }
            Assert.True(misses.Length == 0, $"{compared} compared:\n{misses}");
        }

        /// <summary>.NET's TextExtensions.MeasureText over the real DrawTextEx.</summary>
        private static Size User32Measure(IntPtr dc, string text, Size proposed, TextFormatFlags flags, float h6)
        {
            if (string.IsNullOrEmpty(text))
                return Size.Empty;
            int left = 0, right = 0;
            if ((flags & TextFormatFlags.LeftAndRightPadding) != 0)
            {
                left = (int)Math.Ceiling(2 * h6);
                right = (int)Math.Ceiling(h6 * 2.5f);
            }
            else if ((flags & TextFormatFlags.NoPadding) == 0)
            {
                left = (int)Math.Ceiling(h6);
                right = (int)Math.Ceiling(h6 * 1.5f);
            }
            int min = 1 + left + right;
            if (proposed.Width <= min) proposed.Width = min;
            if (proposed.Height <= 0) proposed.Height = 1;
            uint dt = (uint)flags & 0xFFFFFF;
            if (proposed.Height == int.MaxValue && (dt & 0x20) != 0) dt &= ~0xCu;
            if (proposed.Width == int.MaxValue) dt &= ~0x10u;
            dt |= 0x400;
            var r = new RECT { right = proposed.Width, bottom = proposed.Height };
            var p = new DRAWTEXTPARAMS { cbSize = Marshal.SizeOf<DRAWTEXTPARAMS>(), iLeftMargin = left, iRightMargin = right };
            DrawTextExW(dc, text, text.Length, ref r, dt, ref p);
            return new Size(r.right - r.left, r.bottom - r.top);
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct DRAWTEXTPARAMS { public int cbSize, iTabLength, iLeftMargin, iRightMargin; public uint uiLengthDrawn; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TEXTMETRIC
        {
            public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading, tmAveCharWidth, tmMaxCharWidth,
                       tmWeight, tmOverhang, tmDigitizedAspectX, tmDigitizedAspectY;
            public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
            public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
        }

        [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int DrawTextExW(IntPtr hdc, string text, int count, ref RECT rect, uint format, ref DRAWTEXTPARAMS p);
        [DllImport("gdi32", CharSet = CharSet.Unicode)] private static extern IntPtr CreateFont(int h, int w, int e, int o, int weight, uint i, uint u, uint s, uint cs, uint op, uint cp, uint q, uint pf, string face);
        [DllImport("gdi32")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32", CharSet = CharSet.Unicode)] private static extern bool GetTextMetrics(IntPtr dc, out TEXTMETRIC tm);
    }
}
