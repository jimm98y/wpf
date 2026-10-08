// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The glyph metrics GDI+ lays its text out with, against DirectWrite itself. GDI+ does not measure a
// glyph: GpFaceRealization::GetGlyphStringDeviceAdvanceVector @1800a19d0 and
// GetGlyphStringSidebearings @180024530 (gdiplus.dll 10.0.26100, arm64) ask IDWriteFontFace1 for
// GetGdiCompatibleGlyphAdvances / -Metrics with useGdiNatural = (advance type == 2): GDI_NATURAL for
// ClearType, GDI_CLASSIC for the other grid-fitted hints. GdiPlusText.NaturalMetrics and
// ClassicMetrics answer the same questions from the font file; here every answer is held to
// dwrite.dll's, design unit for design unit, at the sizes the faces' 'gasp' tables and preps decline
// fitting as well as at the ordinary ones.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public class GdiPlusMetricsParityTests
    {
        private const string Chars = "Gasp Hamburgefonstiv0123AVWYTfjvwxyJ7/_";

        /// <summary>Sizes either side of the faces' gasp boundaries (GRIDFIT at 8..10ppem,
        /// SYMMETRIC_GRIDFIT at 16..27, Cascadia's and Segoe UI Light's no-fit above 35..50), and
        /// the preps that set INSTCTRL below 9 (Segoe UI, Tahoma, Verdana).</summary>
        private static readonly int[] Sizes = { 5, 6, 7, 8, 9, 10, 11, 12, 14, 16, 17, 18, 19, 20, 21, 22, 24, 26, 30, 36, 40, 51 };

        public static IEnumerable<object[]> Faces() => new[]
        {
            "segoeui", "tahoma", "verdana", "arial", "consola", "times", "calibri", "georgia", "segoeuil",
            "malgunsl", "malgun", "cour", "micross", "pala", "trebucbd", "comic", "timesbd", "arialbd",
        }.Select(f => new object[] { f });

        [Theory]
        [MemberData(nameof(Faces))]
        public void GdiCompatibleMetricsAreDirectWrites(string file)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file + ".ttf");
            Assert.SkipWhen(!OperatingSystem.IsWindows() || !File.Exists(path), "needs the Windows face and DirectWrite");
            var font = new TrueTypeFont(File.ReadAllBytes(path));
            IntPtr face = DWriteOracle.FontFace(path);
            ushort[] gids = Chars.Select(c => (ushort)font.GlyphIndex(c)).Where(g => g > 0).Distinct().ToArray();

            var misses = new List<string>();
            foreach (int ppem in Sizes)
                foreach (bool natural in new[] { false, true })
                {
                    var theirs = DWriteOracle.GdiCompatibleGlyphMetrics(face, ppem, gids, natural);
                    for (int i = 0; i < gids.Length; i++)
                    {
                        int adv, lsb, rsb;
                        if (natural) GdiPlusText.NaturalMetrics(font, gids[i], ppem, out adv, out lsb, out rsb);
                        else GdiPlusText.ClassicMetrics(font, gids[i], ppem, out adv, out lsb, out rsb);
                        if ((adv, lsb, rsb) != theirs[i])
                            misses.Add($"{ppem}{(natural ? "n" : "c")}:g{gids[i]} {adv},{lsb},{rsb}/{theirs[i].Advance},{theirs[i].Lsb},{theirs[i].Rsb}");
                    }
                }
            Assert.True(misses.Count == 0, $"{misses.Count} glyph metrics differ from DirectWrite's: {string.Join(" ", misses.Take(30))}");
        }
    }
}
