// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// OUR ClearType-fitted x, printed in the same units the ctharness prints GDI's.
//
// The ctharness (scratchpad/ctharness) drives fontdrvhost's own scaler and dumps the x array GDI
// actually rasterizes from -- the one quantity GGO never exposes, since GetGlyphOutline returns the
// BI-LEVEL fit in both of its modes. Comparing our fit against GGO's is the trap that produced
// several retracted conclusions; comparing it against the harness is not.
//
// Units: 64ths of a pixel, ClearType x, so directly comparable with the harness's "FIT ... x:" line
// after that line has been divided by its 6x overscale (the harness already does the division).
//
// WPF_XDUMP=family/chars/ppem[/B|I]
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;
using Xunit;

namespace WgpuInterop.Tests.Text
{
    public class OurFittedXDump
    {
        [Fact]
        public void OurClearTypeFittedX()
        {
            Assert.SkipUnless(OperatingSystem.IsWindows(), "compares against a Windows face");
            string? spec = Environment.GetEnvironmentVariable("WPF_XDUMP");
            Assert.SkipWhen(string.IsNullOrEmpty(spec), "set WPF_XDUMP=family/chars/ppem[/style]");
            string[] parts = spec!.Split('/');
            int ppem = int.Parse(parts[2]);
            string style = parts.Length > 3 ? parts[3].ToUpperInvariant() : "";
            bool bold = style.Contains("B"), italic = style.Contains("I");

            string? file = FontFiles.Find(parts[0], bold, italic);
            Assert.SkipWhen(file is null, "this machine lacks the face");
            byte[] faceBytes = File.ReadAllBytes(file!);
            FontFiles.DeclaredStyle(faceBytes, 0, out bool fileBold, out bool fileItalic);
            var font = new TrueTypeFont(faceBytes, bold && FontFiles.NeedsBoldSimulation(faceBytes, 0, fileBold), italic && !fileItalic);

            // SUBPIXELFITTING IS A STATIC WITH NO INITIALIZER, so it defaults to FALSE and every
            // outline this printed before it was set here was the BI-LEVEL fit, not the ClearType
            // one. That silently invalidated a long run of per-glyph analysis.
            bool savedSub = TrueTypeFont.SubpixelFitting;
            TrueTypeFont.SubpixelFitting = Environment.GetEnvironmentVariable("WPF_XDUMP_BILEVEL") != "1";

            var sb = new System.Text.StringBuilder();
            foreach (char c in parts[1])
            {
                int gid = font.GlyphIndex(c);
                // WPF_XDUMP_NATURAL=1: the SCALED UNHINTED outline, so a fitted coordinate can be
                // read against the shape the designer drew. Without it there is no way to tell a
                // curve the program flattened from one that was straight to begin with.
                List<PathFigure>? figs;
                if (Environment.GetEnvironmentVariable("WPF_XDUMP_NATURAL") == "1")
                {
                    if (!font.TryGetGlyphOutline(gid, out List<PathFigure> plain)) continue;
                    figs = GlyphRunPainter.ScaleFigures(plain, ppem / (float) font.PixelsPerEm, 0f, 0f);
                }
                else if (!((IHintedGlyphFont) font).TryGetHintedOutline(gid, ppem, out figs)
                         || figs is null) continue;

                var xs = new List<int>();
                // WPF_XDUMP_XY=1 prints the Y with every X. A bare x list cannot tell a lean from
                // a shift: the two show up identically until you know which end of the stem each
                // number belongs to.
                var ys = new List<int>();
                void Add(Vector2 pt)
                { xs.Add((int) MathF.Round(pt.X * 64)); ys.Add((int) MathF.Round(pt.Y * 64)); }
                foreach (PathFigure f in figs)
                {
                    Add(f.Start);
                    foreach (PathSegment sg in f.Segments)
                    {
                        if (sg is LineSegment l) Add(l.Point);
                        else if (sg is QuadraticBezierSegment q) { Add(q.Control); Add(q.Point); }
                        else if (sg is CubicBezierSegment c3)
                        { Add(c3.Control1); Add(c3.Control2); Add(c3.Point); }
                    }
                }
                if (Environment.GetEnvironmentVariable("WPF_XDUMP_XY") == "1")
                    for (int i = 0; i < xs.Count; i++)
                        sb.AppendLine($"   pt {i,3}  x {xs[i],5}  y {ys[i],5}");
                // AREA of the fitted outline, by the shoelace formula over the flattened figures.
                // The renderer's ink for the same glyph should equal this: if it is materially
                // less, the rasterizer is losing coverage and the geometry is not the suspect.
                double area = 0;
                foreach (PathFigure f in figs)
                {
                    var pts = new List<Vector2>();
                    Vector2 cur = f.Start; pts.Add(cur);
                    foreach (PathSegment sg in f.Segments)
                    {
                        if (sg is LineSegment l) { cur = l.Point; pts.Add(cur); }
                        else if (sg is QuadraticBezierSegment q)
                        {
                            for (int k = 1; k <= 8; k++)
                            {
                                float t = k / 8f, u = 1 - t;
                                pts.Add(u * u * cur + 2 * u * t * q.Control + t * t * q.Point);
                            }
                            cur = q.Point;
                        }
                        else if (sg is CubicBezierSegment c3)
                        {
                            for (int k = 1; k <= 8; k++)
                            {
                                float t = k / 8f, u = 1 - t;
                                pts.Add(u * u * u * cur + 3 * u * u * t * c3.Control1
                                        + 3 * u * t * t * c3.Control2 + t * t * t * c3.Point);
                            }
                            cur = c3.Point;
                        }
                    }
                    for (int i = 0; i < pts.Count; i++)
                    {
                        Vector2 a = pts[i], b = pts[(i + 1) % pts.Count];
                        area += a.X * b.Y - b.X * a.Y;
                    }
                }
                sb.AppendLine("AREA '" + c + "' ppem=" + ppem + " outline area "
                    + Math.Abs(area / 2).ToString("0.00") + " px^2  = "
                    + (Math.Abs(area / 2) * 3).ToString("0.00") + " lamps of ink");
                sb.AppendLine("OURX gid=" + gid + " '" + c + "' ppem=" + ppem
                    + " n=" + xs.Count + " x: " + string.Join(" ", xs));
                sb.AppendLine("     distinct: " + string.Join(" ", xs.Distinct().OrderBy(v => v)));
            }
            TrueTypeFont.SubpixelFitting = savedSub;
            Console.Error.Write(sb.ToString());
        }
    }
}
