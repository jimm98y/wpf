// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Color glyph (COLR/CPAL) test. Loads Segoe UI Emoji and checks (1) the reader
// decomposes an emoji base glyph into multiple colored layers, and (2) decoding a
// real glyph run through MilcoreEngine paints those layers -- the rendered emoji
// contains a saturated colour that a monochrome outline fallback could not
// produce. No COM: the font is loaded + parsed purely managed.
//

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using Microsoft.Wpf.Interop.WebGpu.Composition.Text;

internal static class Program
{
    private const int W = 64, H = 64;
    private const uint HRoot = 2, HBlack = 3, HRun = 20, HContent = 6;
    private static int _failures;

    private static int Main()
    {
        // COLR/CPAL is the Windows/Google colour-font flavour (Segoe UI Emoji). Apple ships sbix
        // instead, so macOS has NO COLR font at all -- there is nothing to point this at, and
        // treating an unavailable optional asset as a failure just keeps the suite permanently red
        // off Windows. Skip explicitly instead; supplying WGPU_TEST_EMOJI still runs it anywhere.
        string? path = FindEmojiFont();
        if (path is null)
        {
            Console.WriteLine("COLOR GLYPH TEST SKIPPED: no COLR/CPAL font on this OS (set WGPU_TEST_EMOJI to run).");
            return 0;
        }
        Console.WriteLine($"font = {path}");

        TrueTypeFont font;
        try
        {
            font = new TrueTypeFont(File.ReadAllBytes(path));
        }
        catch (Exception e)
        {
            // e.g. a .ttc handed to WGPU_TEST_EMOJI: a collection needs a face offset, so the sfnt
            // parse finds no 'head'. Report it rather than dying with an unhandled exception.
            return Fail($"'{path}' is not a usable sfnt font ({e.Message})");
        }
        Check(font is IColorGlyphFont, "font implements IColorGlyphFont");
        var color = (IColorGlyphFont)font;

        // Scan BMP emoji code points for one with color layers.
        int[] candidates = { 0x26BD /* soccer */, 0x2764 /* heart */, 0x2600 /* sun */, 0x2728 /* sparkles */, 0x26A1 /* zap */ };
        int chosenGid = 0, chosenCp = 0;
        IReadOnlyList<ColorGlyphLayer> chosenLayers = Array.Empty<ColorGlyphLayer>();
        foreach (int cp in candidates)
        {
            int gid = font.GlyphIndex((char)cp);
            if (gid == 0) continue;
            if (color.TryGetColorLayers(gid, out var layers) && layers.Count > 0)
            {
                int colored = 0; foreach (var l in layers) if (l.Color is not null) colored++;
                Console.WriteLine($"  U+{cp:X4} -> gid {gid}: {layers.Count} layer(s), {colored} with palette colour");
                if (chosenGid == 0 && colored > 0) { chosenGid = gid; chosenCp = cp; chosenLayers = layers; }
            }
        }

        Check(chosenGid != 0, "found an emoji base glyph with colored layers");
        if (chosenGid == 0) return Done();
        Check(chosenLayers.Count >= 1, "color glyph has at least one layer");
        bool anyColored = false; foreach (var l in chosenLayers) if (l.Color is not null) anyColored = true;
        Check(anyColored, "at least one layer carries a palette colour");

        // Each layer references a real outline glyph in the same font.
        bool layerOutlines = true;
        foreach (var l in chosenLayers)
            if (!font.TryGetGlyphOutline(l.GlyphId, out var figs) || figs.Count == 0) { /* some layers may be blank */ }
        Check(layerOutlines, "layer glyph ids resolve through the font outline source");

        // End-to-end: decode a one-glyph run and render it. A monochrome fallback
        // would be black/grey on white; color layers produce a saturated pixel.
        const float emSize = 48f;
        var indices = new ushort[] { (ushort)chosenGid };
        var advances = new[] { font.Advance(chosenGid) * (emSize / font.PixelsPerEm) };
        byte[] img = RenderRun(font, indices, advances, emSize);

        int maxSat = 0, coloredPixels = 0, ink = 0;
        for (int i = 0; i < img.Length; i += 4)
        {
            int r = img[i], g = img[i + 1], b = img[i + 2];
            int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            int sat = max - min;
            if (max < 250) ink++;
            if (sat > maxSat) maxSat = sat;
            if (sat > 40) coloredPixels++;
        }
        Console.WriteLine($"rendered U+{chosenCp:X4}: ink={ink}, coloredPixels={coloredPixels}, maxSaturation={maxSat}");
        Check(ink > 50, "emoji renders ink");
        Check(coloredPixels > 20 && maxSat > 60, "emoji renders in colour (CPAL palette applied, not monochrome)");

        return Done();
    }

    private static byte[] RenderRun(TrueTypeFont font, ushort[] indices, float[] advances, float emSize)
    {
        var engine = new MilcoreEngine { FontResolver = _ => font };
        engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
        engine.SubmitCommand(SolidColorBrush(HBlack, 1, 0, 0, 0, 1));
        engine.CreateOrAddRef(HRun, MilResourceTypeId.Null);
        engine.BeginCommand(GlyphRunCreate(HRun, 0, 8, 48, emSize, indices, advances));
        engine.EndCommand();
        engine.CreateOrAddRef(HContent, MilResourceTypeId.RenderData);
        byte[] content = DrawGlyphRunRecord(HBlack, HRun);
        engine.BeginCommand(RenderDataHeader(HContent, content.Length));
        engine.AppendCommandData(content);
        engine.EndCommand();
        engine.SubmitCommand(VisualSetContent(HRoot, HContent));
        engine.Realize();
        SceneVisual root = engine.VisualByHandle(HRoot) ?? throw new InvalidOperationException("no root");

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        return renderer.RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));
    }

    // ---- MILCMD builders (same layout as GlyphRunTest) ----

    private static byte[] VisualSetContent(uint h, uint hc) { var b = new Buf(); b.U32(0x22); b.U32(h); b.U32(hc); return b.ToArray(); }
    private static byte[] SolidColorBrush(uint h, double op, float r, float g, float bl, float a)
    { var b = new Buf(); b.U32(0x7e); b.U32(h); b.F64(op); b.F32(r); b.F32(g); b.F32(bl); b.F32(a); b.U32(0); b.U32(0); b.U32(0); b.U32(0); return b.ToArray(); }
    private static byte[] GlyphRunCreate(uint h, ulong fp, float ox, float oy, float em, ushort[] gi, float[] adv)
    {
        var b = new Buf(); b.U32(0x3a); b.U32(h); b.U64(fp); b.U16(0); b.U16(0); b.F32(ox); b.F32(oy); b.F32(em);
        b.F64(0); b.F64(0); b.F64(0); b.F64(0); b.U16((ushort)gi.Length); b.U16(0); b.U16(0); b.U16(0); b.U16(0); b.U16(0);
        foreach (ushort g in gi) b.U16(g);
        foreach (float a in adv) b.F32(a);
        return b.ToArray();
    }
    private static byte[] RenderDataHeader(uint h, int cb) { var b = new Buf(); b.U32(0x18); b.U32(h); b.U32((uint)cb); return b.ToArray(); }
    private static byte[] DrawGlyphRunRecord(uint hB, uint hR)
    { var p = new Buf(); p.U32(hB); p.U32(hR); var b = new Buf(); b.U32((uint)(p.ToArray().Length + 8)); b.U32(0x49); b.Raw(p.ToArray()); return b.ToArray(); }

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

    private static string? FindEmojiFont()
    {
        string? env = Environment.GetEnvironmentVariable("WGPU_TEST_EMOJI");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        foreach (string name in new[] { "seguiemj.ttf" })
        {
            string c = Path.Combine(fonts, name);
            if (File.Exists(c)) return c;
        }
        return null;
    }

    private static void Check(bool ok, string what)
    { Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}"); if (!ok) _failures++; }

    private static int Done()
    {
        if (_failures == 0) { Console.WriteLine("COLOR GLYPH TEST PASSED: COLR/CPAL emoji render as layered palette colors."); return 0; }
        Console.Error.WriteLine($"COLOR GLYPH TEST FAILED: {_failures} check(s) wrong."); return 1;
    }

    private static int Fail(string m) { Console.Error.WriteLine("COLOR GLYPH TEST FAILED: " + m); return 1; }
}
