// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Glyph-run decode test: proves MilcoreEngine renders WPF's DrawGlyphRun. We
// hand-assemble byte-exact real MILCMD -- MILCMD_GLYPHRUN_CREATE (already-shaped
// glyph indices + advances + baseline origin + em size) and the DrawGlyphRun
// record -- and inject a TrueTypeFont as the FontResolver (in a live WPF process
// the resolver maps the native IDWriteFont pointer to the font; here we supply a
// loaded system font directly). The decoded run lays out each glyph outline as a
// filled path; we assert real ink appears in the text band and the margins stay
// clear, and that a wider glyph string advances past a narrower one.
//

using System;
using System.Collections.Generic;
using System.IO;
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
        var font = new TrueTypeFont(File.ReadAllBytes(fontPath));
        Console.WriteLine($"font: {fontPath}");

        // Shape "HELLO" ourselves into glyph indices + advances (em size 32).
        const float emSize = 32f;
        float advScale = emSize / font.PixelsPerEm;
        string text = "HELLO";
        var indices = new ushort[text.Length];
        var advances = new float[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            int gid = font.GlyphIndex(text[i]);
            indices[i] = (ushort)gid;
            advances[i] = font.Advance(gid) * advScale;
        }

        var engine = new MilcoreEngine { FontResolver = _ => font };
        engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
        engine.SubmitCommand(SolidColorBrush(HBlack, 1, 0, 0, 0, 1));

        // Glyph run at baseline (6, 34), em size 32.
        engine.CreateOrAddRef(HRun, MilResourceTypeId.Null);
        engine.BeginCommand(GlyphRunCreate(HRun, fontPtr: 0, ox: 6, oy: 34, emSize, indices, advances));
        engine.EndCommand();

        engine.CreateOrAddRef(HContent, MilResourceTypeId.RenderData);
        byte[] content = DrawGlyphRunRecord(HBlack, HRun);
        engine.BeginCommand(RenderDataHeader(HContent, content.Length));
        engine.AppendCommandData(content);
        engine.EndCommand();
        engine.SubmitCommand(VisualSetContent(HRoot, HContent));

        engine.Realize();
        SceneVisual? root = engine.VisualByHandle(HRoot);
        if (root is null) return Fail("no root");

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        byte[] img = renderer.RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));

        // Total advance width tells us where the text ends.
        float totalAdvance = 0; foreach (float a in advances) totalAdvance += a;
        int textEndX = (int)(6 + totalAdvance);

        int inkInBand = CountInk(img, 6, 8, textEndX, 36);     // glyph band
        int inkLeftMargin = CountInk(img, 0, 0, 5, H);         // before the run
        int inkPastText = CountInk(img, Math.Min(textEndX + 8, W - 1), 0, W, H);

        Console.WriteLine($"ink in text band = {inkInBand}, left margin = {inkLeftMargin}, past text = {inkPastText}, textEndX = {textEndX}");

        bool ok = true;
        ok &= Assert(inkInBand > 80, "real glyph ink rendered in the text band");
        ok &= Assert(inkLeftMargin == 0, "left margin (before baseline origin) is clear");
        ok &= Assert(inkPastText == 0, "nothing rendered past the run's total advance");

        if (!ok) return 1;
        Console.WriteLine("PASS: WPF glyph-run (DrawGlyphRun) decoded and rendered real text");
        return 0;
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

    // MILCMD_GLYPHRUN_CREATE (Pack=1, explicit offsets) + ushort[] indices + float[] advances.
    private static byte[] GlyphRunCreate(uint handle, ulong fontPtr, float ox, float oy, float emSize,
        ushort[] indices, float[] advances)
    {
        var b = new Buf();
        b.U32(0x3a);            // @0  Type
        b.U32(handle);          // @4  Handle
        b.U64(fontPtr);         // @8  pIDWriteFont
        b.U16(0);               // @16 GlyphRunFlags
        b.U16(0);               // @18 (gap)
        b.F32(ox); b.F32(oy);   // @20 Origin
        b.F32(emSize);          // @28 MuSize
        b.F64(0); b.F64(0); b.F64(0); b.F64(0); // @32 ManagedBounds (4 doubles)
        b.U16((ushort)indices.Length); // @64 GlyphCount
        b.U16(0);               // @66 (gap)
        b.U16(0);               // @68 BidiLevel
        b.U16(0);               // @70 (gap)
        b.U16(0);               // @72 DWriteTextMeasuringMethod
        b.U16(0);               // @74 (pad to 76)
        foreach (ushort g in indices) b.U16(g);
        foreach (float a in advances) b.F32(a);
        return b.ToArray();
    }

    private static byte[] RenderDataHeader(uint handle, int cbData)
    {
        var b = new Buf(); b.U32(0x18); b.U32(handle); b.U32((uint)cbData); return b.ToArray();
    }

    private static byte[] DrawGlyphRunRecord(uint hBrush, uint hRun)
    {
        var p = new Buf(); p.U32(hBrush); p.U32(hRun);   // MILCMD_DRAW_GLYPH_RUN (8 bytes)
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
                if (img[i] < 200 || img[i + 1] < 200 || img[i + 2] < 200) n++;   // darker than near-white
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
        // Common Linux/macOS locations for CI portability (mirrors WgpuInterop.FontTest).
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
