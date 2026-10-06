// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    /// <summary>GetCharWidthInfo's lMaxNegA / lMaxNegC -- the edit control's EC_USEFONTINFO margins
    /// come from them -- against GDI's own, face by face and size by size.
    /// <para>They are FD_DEVICEMETRICS.lMinA / lMinC, which fontdrvhost's lQueryDEVICEMETRICS takes
    /// from hhea's minLeftSideBearing / minRightSideBearing, scaled and rounded half up. One face is
    /// fingerprinted by its unique name and reports zero for both (see
    /// TrueTypeFont.TryGetMaxNegativeBearings).</para></summary>
    public class CharWidthInfoTests
    {
        private static readonly string[] Faces =
        {
            "Microsoft Sans Serif", "Segoe UI", "Tahoma", "Arial", "Verdana", "Times New Roman", "Consolas",
            "Calibri", "Courier New", "Georgia", "Trebuchet MS", "Lucida Console", "Cambria", "Candara",
            "Comic Sans MS", "Impact", "Gabriola", "Bahnschrift", "Ink Free", "Symbol", "Webdings",
        };

        [Fact]
        public void MaxNegativeBearings_AreGdis()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "the reference is GDI");
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            var misses = new StringBuilder();
            int compared = 0;
            try
            {
                foreach (string family in Faces)
                {
                    string? file = FontFiles.Find(family, bold: false, italic: false);
                    if (file is null) continue;
                    byte[] bytes = File.ReadAllBytes(file);
                    int sfnt = FontFiles.SfntOffset(bytes, family, false, false);
                    if (CffFont.IsCff(bytes, sfnt)) continue;
                    var font = new TrueTypeFont(bytes, false, false, sfnt);
                    for (int ppem = 6; ppem <= 72; ppem++)
                    {
                        IntPtr hfont = CreateFont(-ppem, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, family);
                        IntPtr old = SelectObject(dc, hfont);
                        try
                        {
                            var face = new StringBuilder(64);
                            GetTextFace(dc, face.Capacity, face);
                            if (face.ToString() != family || !GetCharWidthInfo(dc, out CharWidthInfo gdi)) continue;
                            Assert.True(font.TryGetMaxNegativeBearings(ppem, out int negA, out int negC));
                            compared++;
                            if (negA != gdi.MaxNegA || negC != gdi.MaxNegC)
                                misses.AppendLine($"{family} @{ppem}: ours {negA},{negC}  GDI {gdi.MaxNegA},{gdi.MaxNegC}");
                        }
                        finally
                        {
                            SelectObject(dc, old);
                            DeleteObject(hfont);
                        }
                    }
                }
            }
            finally
            {
                DeleteDC(dc);
            }
            Assert.True(compared > 0, "no face to compare");
            Assert.True(misses.Length == 0, $"{compared} compared:\n{misses}");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CharWidthInfo { public int MaxNegA, MaxNegC, MinWidthD; }

        [DllImport("gdi32", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight,
            uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision,
            uint quality, uint pitchAndFamily, string face);
        [DllImport("gdi32")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32")] private static extern bool GetCharWidthInfo(IntPtr dc, out CharWidthInfo info);
        [DllImport("gdi32", CharSet = CharSet.Unicode)]
        private static extern int GetTextFace(IntPtr dc, int count, StringBuilder face);
    }
}
