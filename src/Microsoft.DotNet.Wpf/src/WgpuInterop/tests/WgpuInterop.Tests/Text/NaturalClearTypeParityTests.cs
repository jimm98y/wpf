// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// NaturalClearType against DirectWrite itself: the alpha texture stock WPF draws its text from, glyph
// by glyph and run by run, compared byte for byte. See NaturalClearType.cs for what was read out of
// dwrite.dll to make them agree.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public class NaturalClearTypeParityTests
    {
        private const string Printable =
            "!\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~";

        /// <summary>RATCHETS, not tolerances. What is left is one kind of difference: a sample or two on
        /// a DIAGONAL edge (K X x k Y &lt; &gt;), never a stem, a bowl or a row. dwrite's scaler keeps
        /// the fitted outline in its 6x oversampled x frame all the way to the scan converter (the
        /// natural word sets bit 6, which skips scl_ScaleDownFromSubPixelOverscale at the end of
        /// fs__Contour), so an interpolated point carries a 384th of a pixel where ours carries a
        /// 64th. Lower these as that is ported; never raise them.</summary>
        private static readonly Dictionary<string, int> GlyphCeiling = new()
        {
            ["segoeui"] = 0, ["consola"] = 0, ["tahoma"] = 24, ["arial"] = 9, ["times"] = 28, ["verdana"] = 20,
        };

        private static readonly Dictionary<string, int> RunCeiling = new()
        {
            ["segoeui"] = 0, ["consola"] = 0, ["tahoma"] = 3, ["arial"] = 3, ["times"] = 5, ["verdana"] = 3,
        };

        /// <summary>The natural sizes and, above each face's gasp threshold, the symmetric ones.</summary>
        private static readonly float[] Sizes = { 11f, 12f, 13f, 14f, 16f, 17f, 18f, 20f, 22f, 24f };

        public static IEnumerable<object[]> Faces() => new[]
        {
            new object[] { "segoeui" }, new object[] { "tahoma" }, new object[] { "arial" },
            new object[] { "verdana" }, new object[] { "times" }, new object[] { "consola" },
        };

        private static string? FontPath(string file)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), file + ".ttf");
            return OperatingSystem.IsWindows() && File.Exists(path) ? path : null;
        }

        /// <summary>sum |d| between two textures placed by their bounds, zero outside each.</summary>
        private static long Diff(byte[] a, int al, int at, int aw, int ah, byte[] b, int bl, int bt, int bw, int bh)
        {
            int l = Math.Min(al, bl), t = Math.Min(at, bt), r = Math.Max(al + aw, bl + bw), bo = Math.Max(at + ah, bt + bh);
            long d = 0;
            for (int y = t; y < bo; y++)
                for (int x = l; x < r; x++)
                    for (int k = 0; k < 3; k++)
                    {
                        int va = x >= al && x < al + aw && y >= at && y < at + ah ? a[((y - at) * aw + x - al) * 3 + k] : 0;
                        int vb = x >= bl && x < bl + bw && y >= bt && y < bt + bh ? b[((y - bt) * bw + x - bl) * 3 + k] : 0;
                        d += Math.Abs(va - vb);
                    }
            return d;
        }

        private static string Show(byte[] t, int l, int top, int w, int h)
        {
            var sb = new System.Text.StringBuilder($"x[{l},{l + w}) y[{top},{top + h})\n");
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int v = t[(y * w + x) * 3 + k];
                        sb.Append(v == 0 ? '.' : (char)('0' + (v * 6 + 127) / 255));
                    }
                    sb.Append(' ');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static void Report(string line)
        {
            if (Environment.GetEnvironmentVariable("WPF_NATURAL_REPORT") is { Length: > 0 } file)
                lock (typeof(NaturalClearTypeParityTests)) File.AppendAllText(file, line + Environment.NewLine);
        }

        [Theory]
        [MemberData(nameof(Faces))]
        public void EachGlyphIsDirectWritesTexture(string file)
        {
            string? path = FontPath(file);
            Assert.SkipWhen(path is null, "needs the Windows face and DirectWrite");
            var font = new TrueTypeFont(File.ReadAllBytes(path!));
            IntPtr face = DWriteOracle.FontFace(path!);

            var misses = new List<string>();
            int total = 0;
            foreach (float em in Sizes)
            {
                int mode = DWriteOracle.RecommendedMode(face, em);
                if (mode != 4 && mode != 5) continue;
                int nSub = mode == 5 ? 5 : 1;
                foreach (char c in Printable)
                {
                    int gid = font.GlyphIndex(c);
                    if (gid <= 0) continue;
                    byte[] theirs = DWriteOracle.AlphaTexture(face, em, new[] { (ushort)gid }, new[] { 0f }, null, mode,
                        out int tl, out int tt, out int tr, out int tb);
                    NaturalClearType.GlyphBits bits = NaturalClearType.Rasterize(font, gid, em, nSub);
                    byte[] ours = NaturalClearType.RunTexture(new[] { bits }, new[] { 0f }, new[] { 0f },
                        out int ol, out int ot, out int ow, out int oh, nSub);
                    total++;
                    long d = Diff(theirs, tl, tt, tr - tl, tb - tt, ours, ol, ot, ow, oh);
                    if (d != 0) misses.Add($"{em}:'{c}'={d}");
                    if (Environment.GetEnvironmentVariable("WPF_NATURAL_SHOW") == $"{file}/{em}/{c}")
                    {
                        Report("DWRITE " + Show(theirs, tl, tt, tr - tl, tb - tt));
                        Report("OURS   " + Show(ours, ol, ot, ow, oh));
                        if (font.TryGetDWriteFittedOutline(gid, em, NaturalClearType.NaturalFlags, out var figs, out int dr))
                            foreach (var f in figs)
                            {
                                var sb = new System.Text.StringBuilder($"FIG dropout={dr} {f.Start.X * 64},{f.Start.Y * 64}");
                                foreach (var sg in f.Segments)
                                    if (sg is Microsoft.Wpf.Interop.WebGpu.Composition.LineSegment l) sb.Append($" L{l.Point.X * 64},{l.Point.Y * 64}");
                                    else if (sg is Microsoft.Wpf.Interop.WebGpu.Composition.QuadraticBezierSegment q) sb.Append($" Q{q.Control.X * 64},{q.Control.Y * 64} {q.Point.X * 64},{q.Point.Y * 64}");
                                Report(sb.ToString());
                            }
                    }
                }
            }
            Report($"NATURAL {file}: {total - misses.Count}/{total} glyphs exact; {string.Join(" ", misses.Take(40))}");
            if (Environment.GetEnvironmentVariable("WPF_NATURAL_REPORT") is null)
                Assert.True(misses.Count <= GlyphCeiling[file],
                    $"{misses.Count} of {total} differ (ceiling {GlyphCeiling[file]}): {string.Join(" ", misses.Take(40))}");
        }

        [Theory]
        [MemberData(nameof(Faces))]
        public void RunsAreDirectWritesTexture(string file)
        {
            string? path = FontPath(file);
            Assert.SkipWhen(path is null, "needs the Windows face and DirectWrite");
            var font = new TrueTypeFont(File.ReadAllBytes(path!));
            IntPtr face = DWriteOracle.FontFace(path!);
            const string text = "Hamburgefonstiv quick brown fox 0123456789";

            var misses = new List<string>();
            foreach (float em in Sizes)
            {
                int mode = DWriteOracle.RecommendedMode(face, em);
                if (mode != 4 && mode != 5) continue;
                int nSub = mode == 5 ? 5 : 1;
                var gids = new ushort[text.Length]; var adv = new float[text.Length];
                var xs = new float[text.Length]; var ys = new float[text.Length];
                var bits = new NaturalClearType.GlyphBits[text.Length];
                float pen = 0f;
                for (int i = 0; i < text.Length; i++)
                {
                    gids[i] = (ushort)font.GlyphIndex(text[i]);
                    adv[i] = font.RawAdvanceWidth(gids[i]) * em / font.UnitsPerEmForHinting;
                    xs[i] = pen; pen += adv[i];
                    bits[i] = NaturalClearType.Rasterize(font, gids[i], em, nSub);
                }
                byte[] theirs = DWriteOracle.AlphaTexture(face, em, gids, adv, null, mode,
                    out int tl, out int tt, out int tr, out int tb);
                byte[] ours = NaturalClearType.RunTexture(bits, xs, ys, out int ol, out int ot, out int ow, out int oh, nSub);
                long d = Diff(theirs, tl, tt, tr - tl, tb - tt, ours, ol, ot, ow, oh);
                if (d != 0) misses.Add($"{em}={d}");
            }
            Report($"NATURAL RUN {file}: {string.Join(" ", misses)}");
            if (Environment.GetEnvironmentVariable("WPF_NATURAL_REPORT") is null)
                Assert.True(misses.Count <= RunCeiling[file], $"ceiling {RunCeiling[file]}: {string.Join(" ", misses)}");
        }
    }
}
