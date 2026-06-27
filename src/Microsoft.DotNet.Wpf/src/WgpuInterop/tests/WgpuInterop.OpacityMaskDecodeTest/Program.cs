// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Opacity-mask decode test: WPF's UIElement.OpacityMask emits MILCMD_VISUAL_SETALPHAMASK
// referencing a brush whose ALPHA modulates the visual. A horizontal alpha 1->0 gradient
// mask (RelativeToBoundingBox) must fade a solid square left-to-right. This exercises the
// decode path's "resolve the mask against the content bounds in Realize" logic.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 48;
    private const int H = 48;

    private static int Main()
    {
        var engine = new MilcoreEngine();
        engine.CreateOrAddRef(2, MilResourceTypeId.Visual);
        engine.SubmitCommand(SolidColorBrush(3, 0, 0, 0));   // black content

        // Mask: horizontal gradient, alpha 1 (left) -> 0 (right), RelativeToBoundingBox.
        engine.SubmitCommand(LinearGradientBrush(4, 0, 0, 1, 0, mapping: 1,
            new[] { (0f, 1f, 1f, 1f, 1f), (1f, 1f, 1f, 1f, 0f) }));

        engine.CreateOrAddRef(5, MilResourceTypeId.RenderData);
        byte[] full = RenderData(5, DrawRectangleRecord(4, 4, 40, 40, 3));
        engine.BeginCommand(Slice(full, 0, 12)); engine.AppendCommandData(Slice(full, 12, full.Length - 12)); engine.EndCommand();
        engine.SubmitCommand(VisualSetContent(2, 5));
        engine.SubmitCommand(VisualSetOpacityMask(2, 4));

        engine.Realize();
        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        byte[] img = renderer.RenderToRgba(engine.VisualByHandle(2)!, W, H, RgbaColor.FromBytes(255, 255, 255, 255));

        int left = Lum(img, 8, 24), mid = Lum(img, 24, 24), right = Lum(img, 40, 24);
        Console.WriteLine($"  left={left} mid={mid} right={right}");
        bool ok = true;
        ok &= Assert(left < 60, "mask: left edge stays opaque (alpha 1)");
        ok &= Assert(mid > 90 && mid < 190, "mask: middle is half-faded (alpha ~0.5)");
        ok &= Assert(right > 220, "mask: right edge fades to background (alpha 0)");
        ok &= Assert(left < mid && mid < right, "mask: monotonic left-to-right fade");

        if (!ok) return 1;
        Console.WriteLine("PASS: VisualSetOpacityMask fades a visual via a relative gradient mask");
        return 0;
    }

    // ---- builders ----------------------------------------------------------------

    private static byte[] VisualSetContent(uint h, uint c) { var b = new Buf(); b.U32(0x22); b.U32(h); b.U32(c); return b.ToArray(); }
    private static byte[] VisualSetOpacityMask(uint h, uint m) { var b = new Buf(); b.U32(0x23); b.U32(h); b.U32(m); return b.ToArray(); }

    private static byte[] SolidColorBrush(uint h, float r, float g, float bl)
    {
        var b = new Buf(); b.U32(0x7e); b.U32(h); b.F64(1.0);
        b.F32(r); b.F32(g); b.F32(bl); b.F32(1);
        b.U32(0); b.U32(0); b.U32(0); b.U32(0);
        return b.ToArray();
    }

    private static byte[] LinearGradientBrush(uint handle, float sx, float sy, float ex, float ey, uint mapping,
        (float pos, float r, float g, float b, float a)[] stops)
    {
        var b = new Buf();
        b.U32(0x7f); b.U32(handle);
        b.F64(1.0);                    // Opacity
        b.F64(sx); b.F64(sy);          // StartPoint
        b.F64(ex); b.F64(ey);          // EndPoint
        b.U32(0); b.U32(0); b.U32(0);  // anim/transform handles
        b.U32(0);                      // ColorInterpolationMode
        b.U32(mapping);                // BrushMappingMode
        b.U32(0);                      // SpreadMethod
        b.U32((uint)(stops.Length * 24));
        b.U32(0); b.U32(0);
        foreach ((float pos, float r, float g, float bl, float a) in stops)
        { b.F64(pos); b.F32(r); b.F32(g); b.F32(bl); b.F32(a); }
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

    private static int Lum(byte[] img, int x, int y) { int i = (y * W + x) * 4; return (img[i] + img[i + 1] + img[i + 2]) / 3; }
    private static bool Assert(bool ok, string what) { Console.WriteLine($"  [{(ok ? "ok " : "BAD")}] {what}"); return ok; }
}
