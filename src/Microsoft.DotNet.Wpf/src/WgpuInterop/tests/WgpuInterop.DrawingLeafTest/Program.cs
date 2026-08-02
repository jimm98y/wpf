// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The leaf Drawing types inside a DrawingGroup.
//
// DrawingBrush, DrawingGroup and GeometryDrawing were already decoded, so the Drawing
// object model looked supported. It wasn't: ImageDrawing and GlyphRunDrawing were not,
// and an unknown child handle just produces an empty visual -- so a DrawingGroup holding
// an image or text rendered that child as NOTHING, with no error anywhere.
//
// Each case asserts pixels, because "the command decodes" is not the same as "the content
// appears". A DrawingImage case covers a Drawing used as an ImageSource (vector icons).
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 64, H = 64;
    private static int _failures;

    private static int Main()
    {
        // A DrawingGroup (handle 40) holding one ImageDrawing (41) that paints a red 32x32
        // bitmap over the rect (8,8,32,32).
        {
            var e = new MilcoreEngine();
            e.SetBitmap(50, SolidRgba(4, 4, 220, 40, 40), 4, 4);
            e.SubmitCommand(ImageDrawing(41, 8, 8, 32, 32, 50));
            e.SubmitCommand(DrawingGroup(40, new uint[] { 41 }));
            byte[] px = RenderDrawing(e, 40);
            Check(IsNear(px, 24, 24, 220, 40, 40), "ImageDrawing inside a DrawingGroup paints its bitmap");
            Check(IsNear(px, 2, 2, 255, 255, 255), "  and leaves the area outside its rect alone");
        }

        // A DrawingGroup holding a GlyphRunDrawing: text must appear.
        {
            // Glyph rasterization needs a font resolver; the sink sets one in the real app.
            var resolver = new Microsoft.Wpf.Interop.WebGpu.Composition.Text.ManagedFontResolver();
            var e = new MilcoreEngine { ManagedFontResolver = resolver.Resolve };
            e.SubmitCommand(SolidColorBrush(60, 1, 0, 0, 0, 1));
            // A glyph run carries a variable-length tail, so it is registered then sent with
            // BeginCommand/EndCommand rather than SubmitCommand.
            e.CreateOrAddRef(61, MilResourceTypeId.Null);
            string fp = FindFont() ?? "";
            var shaper = new Microsoft.Wpf.Interop.WebGpu.Composition.Text.TrueTypeFont(System.IO.File.ReadAllBytes(fp));
            const float em = 28f;
            float advScale = em / shaper.PixelsPerEm;
            const string text = "WPF";
            var gids = new ushort[text.Length];
            var advs = new float[text.Length];
            for (int i = 0; i < text.Length; i++)
            {
                int gid = shaper.GlyphIndex(text[i]);
                gids[i] = (ushort)gid;
                advs[i] = shaper.Advance(gid) * advScale;
            }
            e.BeginCommand(GlyphRunCreate(61, 8, 40, em, gids, advs, fp, 0, 0));
            e.EndCommand();
            e.SubmitCommand(GlyphRunDrawing(62, 61, 60));
            e.SubmitCommand(DrawingGroup(63, new uint[] { 62 }));
            byte[] px = RenderDrawing(e, 63);
            Check(HasDarkPixels(px, 40), "GlyphRunDrawing inside a DrawingGroup paints glyphs");
        }

        // DrawingImage: a Drawing used as an ImageSource wraps another Drawing.
        {
            var e = new MilcoreEngine();
            e.SetBitmap(50, SolidRgba(4, 4, 30, 90, 210), 4, 4);
            e.SubmitCommand(ImageDrawing(41, 8, 8, 32, 32, 50));
            e.SubmitCommand(DrawingImage(70, 41));
            byte[] px = RenderDrawing(e, 70);
            Check(IsNear(px, 24, 24, 30, 90, 210), "DrawingImage renders the Drawing it wraps");
        }

        Console.WriteLine();
        if (_failures > 0) { Console.WriteLine($"DRAWING LEAF TEST FAILED: {_failures} problem(s)."); return 1; }
        Console.WriteLine("DRAWING LEAF TEST PASSED: image and glyph-run Drawings render inside a Drawing tree.");
        return 0;
    }

    private static byte[] RenderDrawing(MilcoreEngine e, uint handle)
    {
        SceneVisual v = e.BuildDrawingVisualForTest(handle);
        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        return renderer.RenderToRgba(v, W, H, RgbaColor.FromBytes(255, 255, 255, 255));
    }

    private static bool IsNear(byte[] px, int x, int y, int r, int g, int b)
    {
        int i = (y * W + x) * 4;
        return Math.Abs(px[i] - r) < 40 && Math.Abs(px[i + 1] - g) < 40 && Math.Abs(px[i + 2] - b) < 40;
    }

    private static bool HasDarkPixels(byte[] px, int min)
    {
        int n = 0;
        for (int i = 0; i < W * H; i++) if (px[i * 4] < 100) n++;
        return n >= min;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "FAIL")}] {what}");
        if (!ok) _failures++;
    }

    // ---- command builders ----

    private static byte[] SolidRgba(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++) { px[i*4] = r; px[i*4+1] = g; px[i*4+2] = b; px[i*4+3] = 255; }
        return px;
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


    private static byte[] ImageDrawing(uint h, double x, double y, double w, double ht, uint hImg)
    { var b = new Buf(); b.U32(0x89); b.U32(h); b.F64(x); b.F64(y); b.F64(w); b.F64(ht); b.U32(hImg); b.U32(0); return b.ToArray(); }

    private static byte[] GlyphRunDrawing(uint h, uint hRun, uint hBrush)
    { var b = new Buf(); b.U32(0x88); b.U32(h); b.U32(hRun); b.U32(hBrush); return b.ToArray(); }

    private static byte[] DrawingImage(uint h, uint hDrawing)
    { var b = new Buf(); b.U32(0x71); b.U32(h); b.U32(hDrawing); return b.ToArray(); }

    private static byte[] DrawingGroup(uint h, uint[] children)
    {
        var b = new Buf();
        b.U32(0x8b); b.U32(h);
        b.F64(1.0);                                  // Opacity@8
        b.U32((uint)(children.Length * 4));          // ChildrenSize@16
        while (b.Length < 32) b.U32(0);
        b.U32(0);                                    // hTransform@32
        while (b.Length < 52) b.U32(0);
        foreach (uint c in children) b.U32(c);
        return b.ToArray();
    }

    private static byte[] SolidColorBrush(uint handle, double opacity, float r, float g, float bl, float a)
    {
        var buf = new Buf(); buf.U32(0x7e); buf.U32(handle); buf.F64(opacity);
        buf.F32(r); buf.F32(g); buf.F32(bl); buf.F32(a);
        buf.U32(0); buf.U32(0); buf.U32(0); buf.U32(0);
        return buf.ToArray();
    }

    private sealed class Buf
    {
        private readonly List<byte> _b = new();
        public int Length => _b.Count;
        public void U32(uint v) => _b.AddRange(BitConverter.GetBytes(v));
        public void F32(float v) => _b.AddRange(BitConverter.GetBytes(v));
        public void F64(double v) => _b.AddRange(BitConverter.GetBytes(v));
        public void U16(ushort v) => _b.AddRange(BitConverter.GetBytes(v));
        public void U64(ulong v) => _b.AddRange(BitConverter.GetBytes(v));
        public void Raw(byte[] v) => _b.AddRange(v);
        public byte[] ToArray() => _b.ToArray();
    }
}
