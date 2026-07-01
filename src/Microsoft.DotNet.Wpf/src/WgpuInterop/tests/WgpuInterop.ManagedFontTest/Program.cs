// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// COM-free font-resolution test: proves the managed (cross-platform) path that
// replaced DWriteFontResolver. A glyph run carries a 'WFNT' trailer -- the font
// file path + face index + style simulations, exactly what WPF's managed
// GlyphTypeface (FontUri/FaceIndex/StyleSimulations) supplies -- and the engine
// resolves it via ManagedFontResolver with NO DirectWrite / COM at all. We
// hand-assemble the real MILCMD_GLYPHRUN_CREATE with the trailer, set ONLY the
// managed resolver (no legacy pointer resolver), render "HELLO", and assert ink
// lands in the text band and the margins stay clear. We also assert that the
// bold-simulation flag in the trailer thickens the run (more ink) -- proving the
// descriptor's simulation bits flow through to the reader.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

internal static class Program
{
    private const int W = 160;
    private const int H = 48;

    private const uint HRoot = 2;
    private const uint HBlack = 3;
    private const uint HRun = 20;
    private const uint HContent = 6;

    private static int Main()
    {
        string? fontPath = FindFont();
        if (fontPath is null) return Fail("no TrueType font found (set WGPU_TEST_FONT)");
        Console.WriteLine($"font: {fontPath}");

        // We need glyph indices + advances; shape them with a reader loaded the same
        // way the engine's ManagedFontResolver will load it (but the engine resolves
        // the font ITSELF from the trailer -- this local reader is only for shaping).
        var shaper = new TrueTypeFont(File.ReadAllBytes(fontPath));
        const float emSize = 32f;
        float advScale = emSize / shaper.PixelsPerEm;
        const string text = "HELLO";
        var indices = new ushort[text.Length];
        var advances = new float[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            int gid = shaper.GlyphIndex(text[i]);
            indices[i] = (ushort)gid;
            advances[i] = shaper.Advance(gid) * advScale;
        }

        int regularInk = Render(fontPath, indices, advances, emSize, simulations: 0);
        int boldInk    = Render(fontPath, indices, advances, emSize, simulations: 1);  // 1 = Bold
        if (regularInk < 0 || boldInk < 0) return 1;

        float totalAdvance = 0; foreach (float a in advances) totalAdvance += a;
        int textEndX = (int)(6 + totalAdvance);

        bool ok = true;
        ok &= Assert(regularInk > 80, "real glyph ink rendered via COM-free managed resolver");
        ok &= Assert(boldInk > regularInk, "bold simulation flag (from the descriptor) thickens the run");

        if (!ok) return 1;
        Console.WriteLine($"PASS: glyph run resolved a font from a managed descriptor, no COM (regular={regularInk}, bold={boldInk}, textEndX={textEndX})");
        return 0;
    }

    // Decode + render one glyph run whose font is described ONLY by a managed
    // descriptor trailer; returns the ink count in the text band (-1 on failure).
    private static int Render(string fontPath, ushort[] indices, float[] advances, float emSize, int simulations)
    {
        // Crucially: only ManagedFontResolver is set -- no legacy FontResolver, no COM.
        var resolver = new ManagedFontResolver();
        var engine = new MilcoreEngine { ManagedFontResolver = resolver.Resolve };

        engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
        engine.SubmitCommand(SolidColorBrush(HBlack, 1, 0, 0, 0, 1));

        engine.CreateOrAddRef(HRun, MilResourceTypeId.Null);
        engine.BeginCommand(GlyphRunCreate(HRun, 6, 34, emSize, indices, advances, fontPath, 0, simulations));
        engine.EndCommand();

        engine.CreateOrAddRef(HContent, MilResourceTypeId.RenderData);
        byte[] content = DrawGlyphRunRecord(HBlack, HRun);
        engine.BeginCommand(RenderDataHeader(HContent, content.Length));
        engine.AppendCommandData(content);
        engine.EndCommand();
        engine.SubmitCommand(VisualSetContent(HRoot, HContent));

        engine.Realize();
        SceneVisual? root = engine.VisualByHandle(HRoot);
        if (root is null) { Fail("no root"); return -1; }

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        byte[] img = renderer.RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));

        float totalAdvance = 0; foreach (float a in advances) totalAdvance += a;
        int textEndX = (int)(6 + totalAdvance);

        int inkInBand = CountInk(img, 6, 8, textEndX, 36);
        int inkLeftMargin = CountInk(img, 0, 0, 5, H);
        Console.WriteLine($"sim={simulations}: ink in band = {inkInBand}, left margin = {inkLeftMargin}");
        if (inkLeftMargin != 0) { Assert(false, "left margin clear"); return -1; }
        return inkInBand;
    }

    // ---- builders ----------------------------------------------------------------

    private static byte[] VisualSetContent(uint handle, uint hContent)
    {
        var b = new Buf(); b.U32(0x22); b.U32(handle); b.U32(hContent); return b.ToArray();
    }

    private static byte[] SolidColorBrush(uint handle, double opacity, float r, float g, float bl, float a)
    {
        var buf = new Buf(); buf.U32(0x7e); buf.U32(handle); buf.F64(opacity);
        buf.F32(r); buf.F32(g); buf.F32(bl); buf.F32(a);
        buf.U32(0); buf.U32(0); buf.U32(0); buf.U32(0);
        return buf.ToArray();
    }

    // MILCMD_GLYPHRUN_CREATE + ushort[] indices + float[] advances + 'WFNT' trailer
    // (faceIndex + simulations + UTF-8 path) -- the managed font descriptor.
    private static byte[] GlyphRunCreate(uint handle, float ox, float oy, float emSize,
        ushort[] indices, float[] advances, string fontPath, int faceIndex, int simulations)
    {
        var b = new Buf();
        b.U32(0x3a);            // @0  Type
        b.U32(handle);          // @4  Handle
        b.U64(0);               // @8  pIDWriteFont (unused in the managed path)
        b.U16(0);               // @16 GlyphRunFlags (no offsets)
        b.U16(0);               // @18 (gap)
        b.F32(ox); b.F32(oy);   // @20 Origin
        b.F32(emSize);          // @28 MuSize
        b.F64(0); b.F64(0); b.F64(0); b.F64(0); // @32 ManagedBounds
        b.U16((ushort)indices.Length); // @64 GlyphCount
        b.U16(0); b.U16(0); b.U16(0); b.U16(0); b.U16(0); // @66..@74 pad to 76
        foreach (ushort g in indices) b.U16(g);
        foreach (float a in advances) b.F32(a);

        // 'WFNT' trailer
        byte[] path = Encoding.UTF8.GetBytes(fontPath);
        b.U32(0x544E4657);      // 'W','F','N','T' little-endian
        b.U32((uint)faceIndex);
        b.U32((uint)simulations);
        b.U32((uint)path.Length);
        b.Raw(path);
        return b.ToArray();
    }

    private static byte[] RenderDataHeader(uint handle, int cbData)
    {
        var b = new Buf(); b.U32(0x18); b.U32(handle); b.U32((uint)cbData); return b.ToArray();
    }

    private static byte[] DrawGlyphRunRecord(uint hBrush, uint hRun)
    {
        var p = new Buf(); p.U32(hBrush); p.U32(hRun);
        var b = new Buf(); b.U32((uint)(p.ToArray().Length + 8)); b.U32(0x49); b.Raw(p.ToArray());
        return b.ToArray();
    }

    // ---- helpers -----------------------------------------------------------------

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

    private static int CountInk(byte[] img, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(H, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(W, x1); x++)
            {
                int i = (y * W + x) * 4;
                if (img[i] < 200 || img[i + 1] < 200 || img[i + 2] < 200) n++;
            }
        return n;
    }

    private static bool Assert(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}");
        return ok;
    }

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
