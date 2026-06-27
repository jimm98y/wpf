// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Effect decode test: WPF's Effect property emits MILCMD_VISUAL_SETEFFECT referencing a
// BlurEffect / DropShadowEffect resource. We hand-assemble real MILCMD for both, set them
// on a visual, and assert the renderer applies them -- a blur softens a square's edge, and
// a drop shadow appears down-right (proving the depth+direction -> offset conversion).
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 80;
    private const int H = 80;

    private static int Main()
    {
        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        var white = RgbaColor.FromBytes(255, 255, 255, 255);
        bool ok = true;

        // --- Blur: a black square with a BlurEffect(5) -- the edge softens, ink spreads out.
        {
            var engine = new MilcoreEngine();
            BuildSquareVisual(engine, BlurEffect(7, 5.0));
            engine.SubmitCommand(VisualSetEffect(2, 7));
            engine.Realize();
            byte[] img = renderer.RenderToRgba(engine.VisualByHandle(2)!, W, H, white);
            ok &= Assert(Lum(img, 30, 30) < 60, "blur: interior stays solid");
            ok &= Assert(Lum(img, 46, 30) > 60 && Lum(img, 46, 30) < 240, "blur: edge is a soft grey");
            ok &= Assert(Lum(img, 70, 30) > 245, "blur: far pixels stay clear");
        }

        // --- Drop shadow: depth 17 @ direction 315deg -> offset (~+12,+12) down-right.
        {
            var engine = new MilcoreEngine();
            BuildSquareVisual(engine, DropShadowEffect(7, depth: 17, dir: 315, opacity: 0.7, blur: 2, 0, 0, 0));
            engine.SubmitCommand(VisualSetEffect(2, 7));
            engine.Realize();
            byte[] img = renderer.RenderToRgba(engine.VisualByHandle(2)!, W, H, white);
            Console.WriteLine($"  content={Lum(img, 30, 30)} shadow={Lum(img, 52, 52)} upLeft={Lum(img, 12, 12)}");
            ok &= Assert(Lum(img, 30, 30) < 40, "shadow: content stays solid on top");
            ok &= Assert(Lum(img, 52, 52) > 40 && Lum(img, 52, 52) < 200, "shadow: grey shadow down-right (depth+direction)");
            ok &= Assert(Lum(img, 12, 12) > 245, "shadow: none up-left (it is offset)");
        }

        if (!ok) return 1;
        Console.WriteLine("PASS: VisualSetEffect decodes BlurEffect + DropShadowEffect and renders");
        return 0;
    }

    // A visual (handle 2) with a black 24x24 square content at (20,20); effect resource at handle 7.
    private static void BuildSquareVisual(MilcoreEngine engine, byte[] effectResource)
    {
        engine.CreateOrAddRef(2, MilResourceTypeId.Visual);
        engine.SubmitCommand(SolidColorBrush(3, 1, 0, 0, 0, 1));
        engine.CreateOrAddRef(5, MilResourceTypeId.RenderData);
        byte[] payload = DrawRectangleRecord(20, 20, 24, 24, 3);
        engine.BeginCommand(RenderDataHeader(5, payload.Length));
        engine.AppendCommandData(payload);
        engine.EndCommand();
        engine.SubmitCommand(VisualSetContent(2, 5));
        engine.SubmitCommand(effectResource);
    }

    // ---- builders ----------------------------------------------------------------

    private static byte[] VisualSetContent(uint handle, uint hContent)
    { var b = new Buf(); b.U32(0x22); b.U32(handle); b.U32(hContent); return b.ToArray(); }

    private static byte[] VisualSetEffect(uint handle, uint hEffect)
    { var b = new Buf(); b.U32(0x1d); b.U32(handle); b.U32(hEffect); return b.ToArray(); }

    private static byte[] BlurEffect(uint handle, double radius)
    { var b = new Buf(); b.U32(0x6e); b.U32(handle); b.F64(radius); return b.ToArray(); }

    // MILCMD_DROPSHADOWEFFECT: Handle@4, ShadowDepth@8, Color@16, Direction@32, Opacity@40, BlurRadius@48.
    private static byte[] DropShadowEffect(uint handle, double depth, double dir, double opacity, double blur,
        float cr, float cg, float cb)
    {
        var b = new Buf();
        b.U32(0x6f); b.U32(handle);
        b.F64(depth);
        b.F32(cr); b.F32(cg); b.F32(cb); b.F32(1f);  // Color (a=1; Opacity folds into alpha)
        b.F64(dir); b.F64(opacity); b.F64(blur);
        return b.ToArray();
    }

    private static byte[] SolidColorBrush(uint handle, double opacity, float r, float g, float bl, float a)
    {
        var buf = new Buf(); buf.U32(0x7e); buf.U32(handle); buf.F64(opacity);
        buf.F32(r); buf.F32(g); buf.F32(bl); buf.F32(a);
        buf.U32(0); buf.U32(0); buf.U32(0); buf.U32(0);
        return buf.ToArray();
    }

    private static byte[] RenderDataHeader(uint handle, int cbData)
    { var b = new Buf(); b.U32(0x18); b.U32(handle); b.U32((uint)cbData); return b.ToArray(); }

    private static byte[] DrawRectangleRecord(double x, double y, double w, double h, uint hBrush)
    {
        var rec = new Buf(); rec.F64(x); rec.F64(y); rec.F64(w); rec.F64(h); rec.U32(hBrush); rec.U32(0);
        byte[] p = rec.ToArray();
        var b = new Buf(); b.U32((uint)(p.Length + 8)); b.U32(0x40); b.Raw(p);
        return b.ToArray();
    }

    // ---- helpers -----------------------------------------------------------------

    private sealed class Buf
    {
        private readonly List<byte> _b = new();
        public void U32(uint v) => _b.AddRange(BitConverter.GetBytes(v));
        public void F32(float v) => _b.AddRange(BitConverter.GetBytes(v));
        public void F64(double v) => _b.AddRange(BitConverter.GetBytes(v));
        public void Raw(byte[] v) => _b.AddRange(v);
        public byte[] ToArray() => _b.ToArray();
    }

    private static int Lum(byte[] img, int x, int y)
    {
        int i = (y * W + x) * 4;
        return (img[i] + img[i + 1] + img[i + 2]) / 3;
    }

    private static bool Assert(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}");
        return ok;
    }
}
