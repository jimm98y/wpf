// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using WgpuInterop.Tests.Harness;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    /// <summary>The Microsoft Sans Serif StandInFaces builds over Liberation Sans where none is installed
    /// measures as the real one: the same advance for every character, the same GDI widths at the sizes
    /// 'hdmx' records and the same GDI line box. Held to the real face where this machine has one
    /// (macOS ships it).</summary>
    public class StandInFacesTests
    {
        private static readonly string[] Real =
        {
            "/System/Library/Fonts/Supplemental/Microsoft Sans Serif.ttf", @"C:\Windows\Fonts\micross.ttf",
        };

        [Fact]
        public void MicrosoftSansSerif_MeasuresAsTheRealOne()
        {
            string? real = Array.Find(Real, File.Exists);
            Assert.SkipWhen(real is null, "no Microsoft Sans Serif on this machine to hold the stand-in to");
            string? liberation = TestFonts.RepoFont("LiberationSans-Regular.ttf");
            Assert.SkipWhen(liberation is null, "vendored LiberationSans-Regular.ttf not found");

            byte[] built = StandInFaces.MicrosoftSansSerif(File.ReadAllBytes(liberation!));
            var standIn = new TrueTypeFont(built);
            var face = new TrueTypeFont(File.ReadAllBytes(real!));
            Assert.Equal("Microsoft Sans Serif", FontFiles.NameById(built, 0, 1));

            const string text = "The quick brown fox jumps over the lazy dog 0123456789 ÄÖÜäöüß €$%&()[]{}.,;:!?";
            foreach (char c in text)
            {
                int g = standIn.GlyphIndexOf(c), r = face.GlyphIndexOf(c);
                Assert.True(g > 0 && r > 0, $"'{c}' has no glyph");
                Assert.True(standIn.DesignAdvance(g) == face.DesignAdvance(r), $"'{c}': advance {standIn.DesignAdvance(g)}, Windows' {face.DesignAdvance(r)}");
                foreach (int ppem in new[] { 11, 13, 16 })
                {
                    bool okA = standIn.TryGetDeviceAdvance(g, ppem, out float a), okB = face.TryGetDeviceAdvance(r, ppem, out float b);
                    Assert.True(okA && okB, $"'{c}' at {ppem}ppem: no GDI width");
                    Assert.True(a == b, $"'{c}' at {ppem}ppem: GDI width {a}, Windows' {b}");
                }
            }
            for (int ppem = 9; ppem <= 24; ppem++)
            {
                bool okS = standIn.TryGetGdiLineMetrics(ppem, out int sa, out int sd), okF = face.TryGetGdiLineMetrics(ppem, out int fa, out int fd);
                Assert.True(okS && okF, $"{ppem}ppem: no GDI line box");
                Assert.True(sa == fa && sd == fd, $"{ppem}ppem: line {sa}+{sd}, Windows' {fa}+{fd}");
            }
        }
    }
}
