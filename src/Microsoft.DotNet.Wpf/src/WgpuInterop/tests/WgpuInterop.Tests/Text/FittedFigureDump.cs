// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// OUR fitted outline of a glyph, written as figures (M / L / Q in device pixels, y down, baseline
// at 0) so an outside sampler can rasterize it exactly and compare it with what DirectWrite makes of
// the same glyph. Reported only.
//
// WPF_FIGDUMP=family/chars/ppem[/style]   WPF_FIGDUMP_OUT=<file>
// WPF_FIGDUMP_DWRITE=51|71 fits the way DirectWrite's natural modes do (TryGetDWriteFittedOutline).
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public class FittedFigureDump
    {
        [Fact]
        public void DumpFittedFigures()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "reads a Windows face");
            string? spec = Environment.GetEnvironmentVariable("WPF_FIGDUMP");
            string? outPath = Environment.GetEnvironmentVariable("WPF_FIGDUMP_OUT");
            Assert.SkipWhen(string.IsNullOrEmpty(spec) || string.IsNullOrEmpty(outPath),
                            "set WPF_FIGDUMP=family/chars/ppem[/style] and WPF_FIGDUMP_OUT");
            string[] parts = spec!.Split('/');
            string style = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";
            bool bold = style.Contains('B'), italic = style.Contains('I');

            string? file = FontFiles.Find(parts[0], bold, italic);
            Assert.SkipWhen(file is null, "this machine lacks the face");
            var font = new TrueTypeFont(File.ReadAllBytes(file!));

            bool savedSub = TrueTypeFont.SubpixelFitting;
            TrueTypeFont.SubpixelFitting = Environment.GetEnvironmentVariable("WPF_FIGDUMP_BILEVEL") != "1";
            var inv = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            try
            {
                foreach (string sizeText in parts[2].Split(','))
                {
                    float ppem = float.Parse(sizeText, inv);
                    foreach (char c in parts[1])
                    {
                        int gid = font.GlyphIndex(c);
                        List<PathFigure>? figs;
                        if (Environment.GetEnvironmentVariable("WPF_FIGDUMP_UNHINTED") == "1")
                        {
                            if (!font.TryGetGlyphOutline(gid, out List<PathFigure> plain)) figs = new();
                            else figs = GlyphRunPainter.ScaleFigures(plain, ppem / (float)font.PixelsPerEm, 0f, 0f);
                        }
                        else if (Environment.GetEnvironmentVariable("WPF_FIGDUMP_DWRITE") is { Length: > 0 } dwf)
                        {
                            int flags = Convert.ToInt32(dwf, 16);
                            if (!font.TryGetDWriteFittedOutline(gid, ppem, flags, out figs, out _))
                                figs = font.TryGetGlyphOutline(gid, out List<PathFigure> plain)
                                    ? GlyphRunPainter.ScaleFigures(plain, ppem / (float)font.PixelsPerEm, 0f, 0f) : new();
                        }
                        else if (!((IHintedGlyphFont)font).TryGetHintedOutline(gid, ppem, out figs) || figs is null)
                            figs = new();

                        sb.Append("G ").Append(gid).Append(' ').Append(ppem.ToString(inv)).Append('\n');
                        foreach (PathFigure f in figs)
                        {
                            sb.Append("M ").Append(f.Start.X.ToString("R", inv)).Append(' ').Append(f.Start.Y.ToString("R", inv)).Append('\n');
                            foreach (PathSegment s in f.Segments)
                            {
                                if (s is LineSegment l)
                                    sb.Append("L ").Append(l.Point.X.ToString("R", inv)).Append(' ').Append(l.Point.Y.ToString("R", inv)).Append('\n');
                                else if (s is QuadraticBezierSegment q)
                                    sb.Append("Q ").Append(q.Control.X.ToString("R", inv)).Append(' ').Append(q.Control.Y.ToString("R", inv))
                                      .Append(' ').Append(q.Point.X.ToString("R", inv)).Append(' ').Append(q.Point.Y.ToString("R", inv)).Append('\n');
                            }
                        }
                    }
                }
            }
            finally
            {
                TrueTypeFont.SubpixelFitting = savedSub;
            }
            File.WriteAllText(outPath!, sb.ToString());
        }
    }
}
