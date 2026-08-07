// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Glyph coverage conservation on the DEFAULT DISPLAY PATH.
//
// Every real window composites in gamma space into a plain-UNORM target, and until this test existed
// NOTHING covered that path: WgpuInterop.GammaTest renders to a LINEAR and an sRGB target, and
// RenderBaselineTest's text-run scene likewise. So a bug that only fired on the default path went
// unnoticed for the life of the renderer -- the text-gamma LUT (cov^(1/2.2)) was applied there as
// well as on the linear path, even though gamma-space compositing IS the thing that LUT emulates.
// Applying both counted the correction twice and put ~23% excess ink on every glyph, all of it on
// partially-covered EDGE pixels, i.e. a dark halo around each glyph. See Documentation/linux-head.md.
//
// What is asserted is a physical invariant rather than a golden image, so it cannot rot and does not
// need per-platform baselines: ANTI-ALIASING CONSERVES AREA. Summing coverage over a rendered glyph
// run must equal the area enclosed by its outlines -- a partially-covered edge pixel contributes
// exactly its fraction. Anything that reweights coverage (a stray gamma curve, a contrast boost, a
// double-applied LUT) breaks that equality while leaving the image still looking like text, which is
// precisely the class of defect a "does it look like text" test cannot catch.
//
// The expected area is computed from the FONT, by the shoelace formula over the flattened outlines --
// independent of the renderer, so the test cannot agree with a broken rasterizer by construction.
// TrueType winds outer contours and holes oppositely, so the signed sum is outer-minus-holes, which
// is what a non-zero fill actually paints.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

internal static class Program
{
    private const int W = 320;
    private const int H = 64;
    private const uint HRoot = 2, HBlack = 3, HRun = 20, HContent = 6;
    private const float EmSize = 32f;
    private const float OriginX = 8f, OriginY = 44f;

    // The measured/expected ratio this allows. The rasterizer is not exact -- it samples 4x
    // vertically and takes exact horizontal spans -- so a few percent of disagreement is the
    // rasterizer, not a bug. The defect this exists to catch was +23%, and the fixed renderer
    // measures within ~2%, so the band is wide enough to be quiet and tight enough to bite.
    private const double Tolerance = 0.10;

    private static int Main()
    {
        string? fontPath = FindFont();
        if (fontPath is null) return Fail("no TrueType font found (set WGPU_TEST_FONT)");
        var font = new TrueTypeFont(File.ReadAllBytes(fontPath));
        Console.WriteLine($"font: {fontPath}");

        float advScale = EmSize / font.PixelsPerEm;
        const string text = "Hello World";
        var indices = new ushort[text.Length];
        var advances = new float[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            int gid = font.GlyphIndex(text[i]);
            indices[i] = (ushort)gid;
            advances[i] = font.Advance(gid) * advScale;
        }

        double expected = ExpectedInkArea(font, indices, advScale);
        if (expected <= 0) return Fail("could not compute outline area (font exposes no outlines?)");

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);

        // BOTH POLARITIES. Dark themes are not a cosmetic variant of this code path: coverage is
        // applied as alpha against the destination, so a curve that is wrong only for light-on-dark
        // would sail past a light-theme test. Running the same invariant either way also answers, in
        // the only way that is not a matter of taste, whether removing the double-applied LUT left
        // dark-theme text "too thin" -- if ink still equals outline area, the glyphs carry exactly
        // the coverage their outlines describe, and anything beyond that is a contrast preference
        // rather than a rendering fault.
        bool ok = true;
        ok &= Measure(renderer, font, indices, advances, expected, dark: false);
        ok &= Measure(renderer, font, indices, advances, expected, dark: true);

        if (!ok)
        {
            Console.Error.WriteLine(
                "FAIL: rendered ink does not match the outline area. If ratio > 1, coverage is being\n" +
                "      boosted somewhere -- check the text-gamma LUT gate in EmitCoverageMask/EmitMask:\n" +
                "      it must fire ONLY where the pass blends linearly (_srgbOutput), never in\n" +
                "      gamma-space mode, or the correction is counted twice. If ONE polarity fails and\n" +
                "      the other passes, the curve is being applied to the blend rather than to the\n" +
                "      coverage.");
            return 1;
        }
        Console.WriteLine("TEXT COVERAGE TEST PASSED: glyph ink on the default display path matches the outline area, light and dark.");
        return 0;
    }

    private static bool Measure(WgpuSceneRenderer renderer, TrueTypeFont font, ushort[] indices,
        float[] advances, double expected, bool dark)
    {
        // Dark: white glyphs on black. Light: black glyphs on white.
        SceneVisual root = BuildScene(font, indices, advances, white: dark);
        RgbaColor bg = dark ? RgbaColor.FromBytes(0, 0, 0, 255) : RgbaColor.FromBytes(255, 255, 255, 255);

        // srgbOutput:false is the DEFAULT DISPLAY PATH -- gamma-space compositing into a plain-UNORM
        // target, which is what ChooseFormat picks for a real window. This is the case no other test
        // covers.
        byte[] px = renderer.RenderToRgba(root, W, H, bg, srgbOutput: false);

        // The stored value is bg blended toward the ink by coverage, so coverage reads straight back
        // in either polarity.
        double measured = 0;
        int inkPx = 0, edgePx = 0;
        for (int i = 0; i < W * H; i++)
        {
            double cov = dark ? px[i * 4] / 255.0 : (255.0 - px[i * 4]) / 255.0;
            if (cov <= 0.002) continue;
            measured += cov;
            inkPx++;
            if (cov < 0.98) edgePx++;
        }

        double ratio = measured / expected;
        string label = dark ? "dark  (white on black)" : "light (black on white)";
        Console.WriteLine($"{label}: expected(outlines)={expected:0.0}  measured={measured:0.0}  ratio={ratio:0.000}  " +
                          $"ink px={inkPx}, partial={edgePx}");

        bool ok = true;
        ok &= Assert(inkPx > 200, $"{label}: the run actually rendered");
        ok &= Assert(edgePx > 40, $"{label}: edges are anti-aliased (not a binary mask)");
        ok &= Assert(Math.Abs(ratio - 1.0) <= Tolerance,
            $"{label}: coverage is conserved: |ratio-1| <= {Tolerance:0.00}");
        return ok;
    }

    /// <summary>
    /// Area enclosed by the run's glyph outlines, in device pixels, by the shoelace formula over the
    /// flattened contours. Computed from the font, NOT from anything the renderer produces.
    /// </summary>
    private static double ExpectedInkArea(TrueTypeFont font, ushort[] indices, float advScale)
    {
        double total = 0;
        foreach (ushort gid in indices)
        {
            if (!font.TryGetGlyphOutline(gid, out List<PathFigure> figures)) continue;
            // Sum SIGNED areas across the glyph's contours, then take the magnitude ONCE. Taking it
            // per contour would add the counter of an 'e' or 'o' instead of subtracting it, which
            // over-states the expected ink by ~60% on ordinary text.
            double glyph = 0;
            foreach (PathFigure f in figures) glyph += SignedArea(f);
            total += Math.Abs(glyph);
        }
        // Outlines are in the font's PixelsPerEm units; area scales with the square of the em scale.
        return total * advScale * advScale;
    }

    private static double SignedArea(PathFigure f)
    {
        var pts = new List<Vector2> { f.Start };
        Vector2 cur = f.Start;
        foreach (PathSegment seg in f.Segments)
        {
            switch (seg)
            {
                case LineSegment l:
                    pts.Add(l.Point); cur = l.Point; break;
                case QuadraticBezierSegment q:
                    for (int i = 1; i <= Steps; i++) pts.Add(Quad(cur, q.Control, q.Point, i / (float)Steps));
                    cur = q.Point; break;
                case CubicBezierSegment c:
                    for (int i = 1; i <= Steps; i++) pts.Add(Cubic(cur, c.Control1, c.Control2, c.Point, i / (float)Steps));
                    cur = c.Point; break;
            }
        }
        double a = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            Vector2 p = pts[i], q2 = pts[(i + 1) % pts.Count];
            a += (double)p.X * q2.Y - (double)q2.X * p.Y;
        }
        return a * 0.5;
    }

    // Fine enough that flattening error is far below the tolerance being asserted.
    private const int Steps = 24;

    private static Vector2 Quad(Vector2 p0, Vector2 c, Vector2 p1, float t)
    { float u = 1f - t; return u * u * p0 + 2f * u * t * c + t * t * p1; }

    private static Vector2 Cubic(Vector2 p0, Vector2 c1, Vector2 c2, Vector2 p1, float t)
    { float u = 1f - t; return u * u * u * p0 + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * p1; }

    private static SceneVisual BuildScene(TrueTypeFont font, ushort[] indices, float[] advances, bool white = false)
    {
        var engine = new MilcoreEngine { FontResolver = _ => font };
        engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
        float c = white ? 1f : 0f;
        engine.SubmitCommand(SolidColorBrush(HBlack, 1, c, c, c, 1));
        engine.CreateOrAddRef(HRun, MilResourceTypeId.Null);
        engine.BeginCommand(GlyphRunCreate(HRun, 0, OriginX, OriginY, EmSize, indices, advances));
        engine.EndCommand();
        engine.CreateOrAddRef(HContent, MilResourceTypeId.RenderData);
        byte[] content = DrawGlyphRunRecord(HBlack, HRun);
        engine.BeginCommand(RenderDataHeader(HContent, content.Length));
        engine.AppendCommandData(content);
        engine.EndCommand();
        engine.SubmitCommand(VisualSetContent(HRoot, HContent));
        engine.Realize();
        return engine.VisualByHandle(HRoot) ?? throw new InvalidOperationException("no root");
    }

    // ---- MILCMD builders (same byte layout as GammaTest / GlyphRunTest) ----

    private static byte[] VisualSetContent(uint handle, uint hContent)
    { var b = new Buf(); b.U32(0x22); b.U32(handle); b.U32(hContent); return b.ToArray(); }

    private static byte[] SolidColorBrush(uint handle, double opacity, float r, float g, float bl, float a)
    {
        var buf = new Buf(); buf.U32(0x7e); buf.U32(handle); buf.F64(opacity);
        buf.F32(r); buf.F32(g); buf.F32(bl); buf.F32(a);
        buf.U32(0); buf.U32(0); buf.U32(0); buf.U32(0);
        return buf.ToArray();
    }

    private static byte[] GlyphRunCreate(uint handle, ulong fontPtr, float ox, float oy, float emSize,
        ushort[] indices, float[] advances)
    {
        var b = new Buf();
        b.U32(0x3a); b.U32(handle); b.U64(fontPtr);
        b.U16(0); b.U16(0); b.F32(ox); b.F32(oy); b.F32(emSize);
        b.F64(0); b.F64(0); b.F64(0); b.F64(0);
        b.U16((ushort)indices.Length); b.U16(0); b.U16(0); b.U16(0); b.U16(0); b.U16(0);
        foreach (ushort g in indices) b.U16(g);
        foreach (float a in advances) b.F32(a);
        return b.ToArray();
    }

    private static byte[] RenderDataHeader(uint handle, int cbData)
    { var b = new Buf(); b.U32(0x18); b.U32(handle); b.U32((uint)cbData); return b.ToArray(); }

    private static byte[] DrawGlyphRunRecord(uint hBrush, uint hRun)
    {
        var p = new Buf(); p.U32(hBrush); p.U32(hRun);
        var b = new Buf(); b.U32((uint)(p.ToArray().Length + 8)); b.U32(0x49); b.Raw(p.ToArray());
        return b.ToArray();
    }

    private sealed class Buf
    {
        private readonly List<byte> _b = new();
        public void U16(ushort v) => _b.AddRange(BitConverter.GetBytes(v));
        public void U32(uint v) => _b.AddRange(BitConverter.GetBytes(v));
        public void U64(ulong v) => _b.AddRange(BitConverter.GetBytes(v));
        public void F32(float v) => _b.AddRange(BitConverter.GetBytes(v));
        public void F64(double v) => _b.AddRange(BitConverter.GetBytes(v));
        public void Raw(byte[] v) => _b.AddRange(v);
        public byte[] ToArray() => _b.ToArray();
    }

    private static bool Assert(bool ok, string what)
    { Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}"); return ok; }

    private static string? FindFont()
    {
        string? env = Environment.GetEnvironmentVariable("WGPU_TEST_FONT");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (string name in new[] { "arial.ttf", "segoeui.ttf", "tahoma.ttf", "verdana.ttf" })
        {
            string candidate = Path.Combine(fonts, name);
            if (File.Exists(candidate)) return candidate;
        }
        foreach (string candidate in new[]
        {
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/Library/Fonts/Arial.ttf",
            "/System/Library/Fonts/Supplemental/Arial.ttf",
        })
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    private static int Fail(string m) { Console.Error.WriteLine("FAIL: " + m); return 1; }
}
