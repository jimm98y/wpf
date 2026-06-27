// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Transform decode test: WPF RenderTransform / LayoutTransform realize as specific
// transform resources (TranslateTransform/ScaleTransform/RotateTransform/...) that
// MILCMD_VISUAL_SETTRANSFORM links to a visual. We render a square under translate,
// scale and a 90-degree rotation and assert the geometry lands where the matrix says.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 32;
    private const int H = 32;

    private static int Main()
    {
        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        var white = RgbaColor.FromBytes(255, 255, 255, 255);
        bool ok = true;

        // (a) Translate(10,5): square (0,0,8,8) -> (10,5,8,8).
        {
            byte[] img = Render(renderer, white, TranslateTransform(4, 10, 5), Rect(0, 0, 8, 8));
            ok &= Filled(img, 14, 9, "translate: square moved to (10,5)");
            ok &= Clear(img, 4, 4, "translate: original position is empty");
        }

        // (b) Scale(2,2 about origin): square (0,0,8,8) -> (0,0,16,16).
        {
            byte[] img = Render(renderer, white, ScaleTransform(4, 2, 2, 0, 0), Rect(0, 0, 8, 8));
            ok &= Filled(img, 14, 14, "scale: square enlarged to 16x16");
            ok &= Clear(img, 20, 20, "scale: nothing past the scaled bounds");
        }

        // (c) Rotate(90 about 16,16): horizontal bar (8,14,16,4) -> vertical bar (14,8,4,16).
        {
            byte[] img = Render(renderer, white, RotateTransform(4, 90, 16, 16), Rect(8, 14, 16, 4));
            ok &= Filled(img, 16, 10, "rotate: bar is now vertical (top)");
            ok &= Filled(img, 16, 22, "rotate: bar is now vertical (bottom)");
            ok &= Clear(img, 10, 16, "rotate: the original horizontal extent is empty");
        }

        if (!ok) return 1;
        Console.WriteLine("PASS: TranslateTransform / ScaleTransform / RotateTransform decode and apply");
        return 0;
    }

    private static byte[] Render(WgpuSceneRenderer renderer, RgbaColor bg, byte[] transform, (double x, double y, double w, double h) rect)
    {
        var engine = new MilcoreEngine();
        engine.CreateOrAddRef(2, MilResourceTypeId.Visual);
        engine.SubmitCommand(SolidColorBrush(3, 0, 0, 0));      // black
        engine.SubmitCommand(transform);                        // transform resource @ handle 4
        engine.SubmitCommand(VisualSetTransform(2, 4));
        engine.CreateOrAddRef(5, MilResourceTypeId.RenderData);
        byte[] full = RenderData(5, DrawRectangleRecord(rect.x, rect.y, rect.w, rect.h, 3));
        engine.BeginCommand(Slice(full, 0, 12)); engine.AppendCommandData(Slice(full, 12, full.Length - 12)); engine.EndCommand();
        engine.SubmitCommand(VisualSetContent(2, 5));
        engine.Realize();
        return renderer.RenderToRgba(engine.VisualByHandle(2)!, W, H, bg);
    }

    // ---- builders ----------------------------------------------------------------

    private static (double, double, double, double) Rect(double x, double y, double w, double h) => (x, y, w, h);

    private static byte[] VisualSetContent(uint h, uint c) { var b = new Buf(); b.U32(0x22); b.U32(h); b.U32(c); return b.ToArray(); }
    private static byte[] VisualSetTransform(uint h, uint t) { var b = new Buf(); b.U32(0x1c); b.U32(h); b.U32(t); return b.ToArray(); }

    private static byte[] TranslateTransform(uint h, double x, double y)
    { var b = new Buf(); b.U32(0x73); b.U32(h); b.F64(x); b.F64(y); return b.ToArray(); }

    private static byte[] ScaleTransform(uint h, double sx, double sy, double cx, double cy)
    { var b = new Buf(); b.U32(0x74); b.U32(h); b.F64(sx); b.F64(sy); b.F64(cx); b.F64(cy); return b.ToArray(); }

    private static byte[] RotateTransform(uint h, double angle, double cx, double cy)
    { var b = new Buf(); b.U32(0x76); b.U32(h); b.F64(angle); b.F64(cx); b.F64(cy); return b.ToArray(); }

    private static byte[] SolidColorBrush(uint h, float r, float g, float bl)
    {
        var b = new Buf(); b.U32(0x7e); b.U32(h); b.F64(1.0);
        b.F32(r); b.F32(g); b.F32(bl); b.F32(1);
        b.U32(0); b.U32(0); b.U32(0); b.U32(0);
        return b.ToArray();
    }

    private static byte[] RenderData(uint h, byte[] payload)
    { var b = new Buf(); b.U32(0x18); b.U32(h); b.U32((uint)payload.Length); b.Raw(payload); return b.ToArray(); }

    private static byte[] DrawRectangleRecord(double x, double y, double w, double h, uint hBrush)
    {
        var rec = new Buf(); rec.F64(x); rec.F64(y); rec.F64(w); rec.F64(h); rec.U32(hBrush); rec.U32(0);
        byte[] p = rec.ToArray();
        var b = new Buf(); b.U32((uint)(p.Length + 8)); b.U32(0x40); b.Raw(p);
        return b.ToArray();
    }

    private static byte[] Slice(byte[] a, int off, int len) { var s = new byte[len]; Array.Copy(a, off, s, 0, len); return s; }

    private sealed class Buf
    {
        private readonly List<byte> _b = new();
        public void U32(uint v) => _b.AddRange(BitConverter.GetBytes(v));
        public void F32(float v) => _b.AddRange(BitConverter.GetBytes(v));
        public void F64(double v) => _b.AddRange(BitConverter.GetBytes(v));
        public void Raw(byte[] v) => _b.AddRange(v);
        public byte[] ToArray() => _b.ToArray();
    }

    private static bool Filled(byte[] img, int x, int y, string what) => Px(img, x, y, true, what);
    private static bool Clear(byte[] img, int x, int y, string what) => Px(img, x, y, false, what);

    private static bool Px(byte[] img, int x, int y, bool wantInk, string what)
    {
        int i = (y * W + x) * 4;
        bool ink = img[i] < 128 && img[i + 1] < 128 && img[i + 2] < 128;   // black square vs white bg
        bool ok = ink == wantInk;
        Console.WriteLine($"  ({x,2},{y,2}) = [{img[i],3},{img[i + 1],3},{img[i + 2],3}] {(ok ? "ok" : "FAIL")}  {what}");
        return ok;
    }
}
