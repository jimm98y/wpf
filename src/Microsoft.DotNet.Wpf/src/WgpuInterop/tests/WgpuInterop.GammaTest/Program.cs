// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Text-gamma test. WPF blends glyph coverage in gamma (sRGB) space, which makes
// text heavier than a linear-space blend. The renderer re-maps glyph coverage
// through a text-gamma LUT on the display-destined sRGB path only. We render the
// same real glyph run twice -- once to a LINEAR target (no gamma) and once to an
// sRGB target (gamma) -- then sRGB-encode the linear result so both are in display
// space, differing only by the coverage gamma. The gamma'd text must be measurably
// heavier (more dark coverage) over the text band, and the counters must stay open.
//

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

internal static class Program
{
    private const int W = 200;
    private const int H = 48;
    private const uint HRoot = 2, HBlack = 3, HRun = 20, HContent = 6;

    private static int Main()
    {
        string? fontPath = FindFont();
        if (fontPath is null) return Fail("no TrueType font found (set WGPU_TEST_FONT)");
        var font = new TrueTypeFont(File.ReadAllBytes(fontPath));
        Console.WriteLine($"font: {fontPath}");

        const float emSize = 32f;
        float advScale = emSize / font.PixelsPerEm;
        string text = "Hello World";
        var indices = new ushort[text.Length];
        var advances = new float[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            int gid = font.GlyphIndex(text[i]);
            indices[i] = (ushort)gid;
            advances[i] = font.Advance(gid) * advScale;
        }

        SceneVisual root = BuildScene(font, indices, advances, emSize);

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        var bg = RgbaColor.FromBytes(255, 255, 255, 255);
        byte[] linear = renderer.RenderToRgba(root, W, H, bg, srgbOutput: false); // gamma OFF
        byte[] srgb = renderer.RenderToRgba(root, W, H, bg, srgbOutput: true);     // gamma ON

        // Put both in display (sRGB) space: the linear render is raw linear bytes, so
        // encode it; the sRGB render is already display-ready. They then differ only
        // by the text-coverage gamma.
        double linearDark = 0, gammaDark = 0;
        int linInk = 0, srgbInk = 0;
        for (int y = 6; y < 40; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                double lin = SrgbEncode(linear[i] / 255.0) * 255.0;   // linear blend, shown on screen
                double gam = srgb[i];                                  // gamma blend, shown on screen
                linearDark += 255 - lin;
                gammaDark += 255 - gam;
                if (lin < 200) linInk++;
                if (gam < 200) srgbInk++;
            }

        Console.WriteLine($"text darkness: linear-on-screen={linearDark:0}, gamma={gammaDark:0}  (ink px: linear={linInk}, gamma={srgbInk})");

        bool ok = true;
        ok &= Assert(linInk > 80 && srgbInk > 80, "both renders produce real text ink");
        ok &= Assert(gammaDark > linearDark * 1.05, "gamma makes text heavier than linear blend");

        // Counters must remain open (gamma must not flood the glyph holes solid).
        // Sample the centre of the first 'o' in "World" region: just assert there is
        // still near-white background inside the band (not a solid block).
        int nearWhite = 0;
        for (int y = 6; y < 40; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                if (srgb[i] > 240) nearWhite++;
            }
        ok &= Assert(nearWhite > srgbInk, "gamma keeps glyph counters/background open (not a solid block)");

        if (!ok) return 1;
        Console.WriteLine("GAMMA TEST PASSED: glyph coverage is gamma-corrected on the sRGB path, matching WPF's heavier text.");
        return 0;
    }

    private static double SrgbEncode(double c)
        => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;

    private static SceneVisual BuildScene(TrueTypeFont font, ushort[] indices, float[] advances, float emSize)
    {
        var engine = new MilcoreEngine { FontResolver = _ => font };
        engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
        engine.SubmitCommand(SolidColorBrush(HBlack, 1, 0, 0, 0, 1));
        engine.CreateOrAddRef(HRun, MilResourceTypeId.Null);
        engine.BeginCommand(GlyphRunCreate(HRun, 0, 6, 34, emSize, indices, advances));
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

    // ---- MILCMD builders (same byte layout as GlyphRunTest) ----

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
        return null;
    }

    private static int Fail(string m) { Console.Error.WriteLine("FAIL: " + m); return 1; }
}
