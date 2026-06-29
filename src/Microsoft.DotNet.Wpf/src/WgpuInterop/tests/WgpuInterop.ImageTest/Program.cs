// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Image decode test: WPF DrawImage / ImageBrush reference a bitmap whose pixels arrive
// out-of-band (SendCommandBitmapSource -> the host reads the native IWICBitmapSource).
// Here we inject a 2x2 RGBA checker via SetBitmap and assert that (a) a DrawImage record
// stretches it across its rectangle and (b) an ImageBrush fill maps it across a shape's
// bounds -- each quadrant of the checker landing where expected.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 80;
    private const int H = 80;

    private const uint HRoot = 2;
    private const uint HImg = 10;
    private const uint HImgBrush = 11;
    private const uint HImgWide = 12;
    private const uint HImgBrushUniform = 13;
    private const uint HImg4 = 14;
    private const uint HImgBrushUtf = 15;
    private const uint HImgBrushViewbox = 16;
    private const uint HContent = 5;

    private static int Main()
    {
        // 2x2 checker: row0 = [red, green], row1 = [blue, yellow] (straight RGBA, row-major).
        byte[] checker =
        {
            255, 0, 0, 255,   0, 255, 0, 255,
            0, 0, 255, 255,   255, 255, 0, 255,
        };

        // 2x1 wide checker for the Uniform letterbox test: [red, green].
        byte[] wide = { 255, 0, 0, 255,   0, 255, 0, 255 };
        // 4x4 image, rows red/green/blue/yellow, for the UniformToFill crop test.
        byte[] rows4 = Rows4();

        var engine = new MilcoreEngine();
        engine.SetBitmap(HImg, checker, 2, 2);
        engine.SetBitmap(HImgWide, wide, 2, 1);
        engine.SetBitmap(HImg4, rows4, 4, 4);
        engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
        engine.SubmitCommand(ImageBrush(HImgBrush, HImg, stretch: 1));            // Fill
        engine.SubmitCommand(ImageBrush(HImgBrushUniform, HImgWide, stretch: 2)); // Uniform
        engine.SubmitCommand(ImageBrush(HImgBrushUtf, HImg4, stretch: 3));        // UniformToFill
        // Viewbox crop: use the right column of the 2x2 checker = [green, yellow], Fill.
        engine.SubmitCommand(ImageBrush(HImgBrushViewbox, HImg, 1, 0.5, 0, 0.5, 1));

        engine.CreateOrAddRef(HContent, MilResourceTypeId.RenderData);
        byte[] content = Concat(
            DrawImageRecord(4, 4, 32, 32, HImg),                          // DrawImage stretched to (4..36, 4..36)
            DrawRoundedRectangleRecord(4, 44, 32, 32, HImgBrush),         // ImageBrush Fill at (4..36, 44..76)
            DrawRoundedRectangleRecord(44, 4, 32, 32, HImgBrushUniform),  // ImageBrush Uniform at (44..76, 4..36)
            DrawRoundedRectangleRecord(44, 44, 32, 16, HImgBrushUtf),     // ImageBrush UniformToFill (44..76, 44..60)
            DrawRoundedRectangleRecord(44, 62, 32, 16, HImgBrushViewbox)); // Viewbox-cropped fill (44..76, 62..78)
        engine.BeginCommand(RenderDataHeader(HContent, content.Length));
        engine.AppendCommandData(content);
        engine.EndCommand();
        engine.SubmitCommand(VisualSetContent(HRoot, HContent));

        engine.Realize();
        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        byte[] img = renderer.RenderToRgba(engine.VisualByHandle(HRoot)!, W, H, RgbaColor.FromBytes(255, 255, 255, 255));

        // Sampling note: image brushes are bilinear-sampled (matching WPF), so an upscaled checker
        // blends across texel boundaries. Sample near the OUTER corner of each texel region (well away
        // from the centre boundary) where bilinear clamps to the pure texel colour.
        bool ok = true;
        // DrawImage quadrants (16px each within 4..36).
        ok &= Check(img, 8, 8, 255, 0, 0, "DrawImage top-left = red");
        ok &= Check(img, 32, 8, 0, 255, 0, "DrawImage top-right = green");
        ok &= Check(img, 8, 32, 0, 0, 255, "DrawImage bottom-left = blue");
        ok &= Check(img, 32, 32, 255, 255, 0, "DrawImage bottom-right = yellow");
        // ImageBrush fill quadrants (within 4..36, 44..76).
        ok &= Check(img, 8, 48, 255, 0, 0, "ImageBrush top-left = red");
        ok &= Check(img, 32, 48, 0, 255, 0, "ImageBrush top-right = green");
        ok &= Check(img, 8, 72, 0, 0, 255, "ImageBrush bottom-left = blue");
        ok &= Check(img, 32, 72, 255, 255, 0, "ImageBrush bottom-right = yellow");
        // Uniform letterbox: a 2x1 image in a 32x32 rect (44..76, 4..36) -> centered band
        // (44..76, 12..28); top/bottom letterbox stays background.
        ok &= Check(img, 48, 20, 255, 0, 0, "Uniform: image band left = red");
        ok &= Check(img, 72, 20, 0, 255, 0, "Uniform: image band right = green");
        ok &= Check(img, 60, 8, 255, 255, 255, "Uniform: top letterbox is clear");
        ok &= Check(img, 60, 32, 255, 255, 255, "Uniform: bottom letterbox is clear");
        // UniformToFill: 4x4 (rows red/green/blue/yellow) into 32x16 (44..76, 44..60) -> covers fully,
        // shows the centre rows (green, blue) cropped; no letterbox.
        ok &= Check(img, 60, 46, 0, 255, 0, "UniformToFill: covered, centre crop top row = green");
        ok &= Check(img, 60, 58, 0, 0, 255, "UniformToFill: covered, centre crop bottom row = blue");
        ok &= Check(img, 46, 46, 0, 255, 0, "UniformToFill: corner is covered (no letterbox)");
        // Viewbox crop: right column [green, yellow] filled into (44..76, 62..78).
        ok &= Check(img, 60, 64, 0, 255, 0, "Viewbox: cropped source top = green");
        ok &= Check(img, 60, 76, 255, 255, 0, "Viewbox: cropped source bottom = yellow");

        if (!ok) return 1;
        Console.WriteLine("PASS: DrawImage + ImageBrush decode and render a bitmap");
        return 0;
    }

    // ---- builders ----------------------------------------------------------------

    private static byte[] VisualSetContent(uint handle, uint hContent)
    { var b = new Buf(); b.U32(0x22); b.U32(handle); b.U32(hContent); return b.ToArray(); }

    private static byte[] ImageBrush(uint handle, uint hImageSource, uint stretch)
        => ImageBrush(handle, hImageSource, stretch, 0, 0, 1, 1);

    // MILCMD_IMAGEBRUSH: Handle@4, Opacity@8, Viewport@16, Viewbox@48, ViewportUnits@108,
    // ViewboxUnits@112, Stretch@124, TileMode@128, hImageSource@144.
    private static byte[] ImageBrush(uint handle, uint hImageSource, uint stretch,
        double vbX, double vbY, double vbW, double vbH)
    {
        var b = new Buf();
        b.U32(0x81); b.U32(handle);
        b.F64(1.0);                                   // Opacity @8
        while (b.Length < 16) b.U8(0);
        b.F64(0); b.F64(0); b.F64(1); b.F64(1);       // Viewport @16 = (0,0,1,1)
        while (b.Length < 48) b.U8(0);
        b.F64(vbX); b.F64(vbY); b.F64(vbW); b.F64(vbH); // Viewbox @48
        while (b.Length < 108) b.U8(0);
        b.U32(1);                                     // ViewportUnits @108 = RelativeToBoundingBox
        b.U32(1);                                     // ViewboxUnits @112 = RelativeToBoundingBox
        while (b.Length < 124) b.U8(0);
        b.U32(stretch);                               // Stretch @124
        while (b.Length < 144) b.U8(0);               // TileMode @128 = None
        b.U32(hImageSource);                          // hImageSource @144
        return b.ToArray();
    }

    private static byte[] RenderDataHeader(uint handle, int cbData)
    { var b = new Buf(); b.U32(0x18); b.U32(handle); b.U32((uint)cbData); return b.ToArray(); }

    // MILCMD_DRAW_IMAGE: rectangle@0 (4 doubles), hImageSource@32, pad@36 (size 40).
    private static byte[] DrawImageRecord(double x, double y, double w, double h, uint hImg)
    {
        var p = new Buf(); p.F64(x); p.F64(y); p.F64(w); p.F64(h); p.U32(hImg); p.U32(0);
        var b = new Buf(); b.U32((uint)(p.Length + 8)); b.U32(0x47); b.Raw(p.ToArray());
        return b.ToArray();
    }

    private static byte[] DrawRoundedRectangleRecord(double x, double y, double w, double h, uint hBrush)
    {
        var p = new Buf(); p.F64(x); p.F64(y); p.F64(w); p.F64(h); p.F64(0); p.F64(0); p.U32(hBrush); p.U32(0);
        var b = new Buf(); b.U32((uint)(p.Length + 8)); b.U32(0x42); b.Raw(p.ToArray());
        return b.ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    { var b = new Buf(); foreach (byte[] p in parts) b.Raw(p); return b.ToArray(); }

    // 4x4 RGBA, rows: red, green, blue, yellow (each row 4 identical pixels).
    private static byte[] Rows4()
    {
        byte[][] rowColors = { new byte[] { 255, 0, 0, 255 }, new byte[] { 0, 255, 0, 255 }, new byte[] { 0, 0, 255, 255 }, new byte[] { 255, 255, 0, 255 } };
        var px = new byte[4 * 4 * 4];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                Array.Copy(rowColors[y], 0, px, (y * 4 + x) * 4, 4);
        return px;
    }

    // ---- helpers -----------------------------------------------------------------

    private sealed class Buf
    {
        private readonly List<byte> _b = new();
        public int Length => _b.Count;
        public void U8(byte v) => _b.Add(v);
        public void U32(uint v) => _b.AddRange(BitConverter.GetBytes(v));
        public void F64(double v) => _b.AddRange(BitConverter.GetBytes(v));
        public void Raw(byte[] v) => _b.AddRange(v);
        public byte[] ToArray() => _b.ToArray();
    }

    private static bool Check(byte[] img, int x, int y, int r, int g, int b, string what)
    {
        int i = (y * W + x) * 4;
        int gr = img[i], gg = img[i + 1], gb = img[i + 2];
        const int tol = 8;
        bool ok = Math.Abs(gr - r) <= tol && Math.Abs(gg - g) <= tol && Math.Abs(gb - b) <= tol;
        Console.WriteLine($"  ({x,2},{y,2}) = [{gr,3},{gg,3},{gb,3}] expect [{r,3},{g,3},{b,3}] {(ok ? "ok" : "FAIL")}  {what}");
        return ok;
    }
}
