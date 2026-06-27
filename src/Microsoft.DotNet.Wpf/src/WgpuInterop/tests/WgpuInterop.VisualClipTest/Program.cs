// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Visual-level clip test: WPF's UIElement.Clip / ClipToBounds emit MILCMD_VISUAL_SETCLIP
// referencing a geometry resource, which masks the visual's whole subtree. A rectangle
// clip uses the fast axis-aligned scissor; any other geometry becomes a ClipGeometry
// mask. We render a big red rectangle clipped to (a) a small rect and (b) an ellipse,
// and assert the clip is honoured.
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
        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        var white = RgbaColor.FromBytes(255, 255, 255, 255);
        bool ok = true;

        // --- (a) rectangle clip (fast scissor): red rect (0,0,40,40) clipped to (8,8,12,12).
        {
            var engine = new MilcoreEngine();
            engine.CreateOrAddRef(2, MilResourceTypeId.Visual);
            engine.SubmitCommand(SolidColorBrush(3, 1, 1, 0, 0, 1));
            engine.SubmitCommand(RectangleGeometry(4, 0, 0, 8, 8, 12, 12));
            engine.CreateOrAddRef(5, MilResourceTypeId.RenderData);
            byte[] rd = RenderData(5, DrawRectangleRecord(0, 0, 40, 40, 3));
            engine.BeginCommand(rd.AsSpanHeader(out byte[] payload)); engine.AppendCommandData(payload); engine.EndCommand();
            engine.SubmitCommand(VisualSetContent(2, 5));
            engine.SubmitCommand(VisualSetClip(2, 4));
            engine.Realize();
            byte[] img = renderer.RenderToRgba(engine.VisualByHandle(2)!, W, H, white);
            ok &= Check(img, 14, 14, 255, 0, 0, 255, "inside rect clip is painted");
            ok &= Check(img, 4, 4, 255, 255, 255, 255, "above-left of rect clip is clipped away");
            ok &= Check(img, 30, 30, 255, 255, 255, 255, "below-right of rect clip is clipped away");
        }

        // --- (b) ellipse clip (geometry mask): red rect clipped to an ellipse (center 16,16 r 8).
        {
            var engine = new MilcoreEngine();
            engine.CreateOrAddRef(2, MilResourceTypeId.Visual);
            engine.SubmitCommand(SolidColorBrush(3, 1, 1, 0, 0, 1));
            engine.SubmitCommand(EllipseGeometry(6, 8, 8, 16, 16));
            engine.CreateOrAddRef(5, MilResourceTypeId.RenderData);
            byte[] rd = RenderData(5, DrawRectangleRecord(0, 0, 40, 40, 3));
            engine.BeginCommand(rd.AsSpanHeader(out byte[] payload)); engine.AppendCommandData(payload); engine.EndCommand();
            engine.SubmitCommand(VisualSetContent(2, 5));
            engine.SubmitCommand(VisualSetClip(2, 6));
            engine.Realize();
            byte[] img = renderer.RenderToRgba(engine.VisualByHandle(2)!, W, H, white);
            ok &= Check(img, 16, 16, 255, 0, 0, 255, "ellipse clip centre is painted");
            ok &= Check(img, 2, 2, 255, 255, 255, 255, "outside the ellipse (corner) is clipped away");
            ok &= Check(img, 27, 16, 255, 255, 255, 255, "outside the ellipse radius is clipped away");
        }

        if (!ok) return 1;
        Console.WriteLine("PASS: VisualSetClip masks a visual subtree (rect scissor + ellipse geometry mask)");
        return 0;
    }

    // ---- builders ----------------------------------------------------------------

    private static byte[] VisualSetContent(uint handle, uint hContent)
    { var b = new Buf(); b.U32(0x22); b.U32(handle); b.U32(hContent); return b.ToArray(); }

    private static byte[] VisualSetClip(uint handle, uint hClip)
    { var b = new Buf(); b.U32(0x1f); b.U32(handle); b.U32(hClip); return b.ToArray(); }

    private static byte[] SolidColorBrush(uint handle, double opacity, float r, float g, float bl, float a)
    {
        var buf = new Buf(); buf.U32(0x7e); buf.U32(handle); buf.F64(opacity);
        buf.F32(r); buf.F32(g); buf.F32(bl); buf.F32(a);
        buf.U32(0); buf.U32(0); buf.U32(0); buf.U32(0);
        return buf.ToArray();
    }

    private static byte[] RectangleGeometry(uint handle, double rx, double ry, double x, double y, double w, double h)
    {
        var b = new Buf(); b.U32(0x79); b.U32(handle);
        b.F64(rx); b.F64(ry); b.F64(x); b.F64(y); b.F64(w); b.F64(h);
        return b.ToArray();
    }

    private static byte[] EllipseGeometry(uint handle, double rx, double ry, double cx, double cy)
    {
        var b = new Buf(); b.U32(0x7a); b.U32(handle);
        b.F64(rx); b.F64(ry); b.F64(cx); b.F64(cy);
        return b.ToArray();
    }

    private static byte[] RenderData(uint handle, byte[] payload)
    {
        var b = new Buf(); b.U32(0x18); b.U32(handle); b.U32((uint)payload.Length); b.Raw(payload);
        return b.ToArray();
    }

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

    private static bool Check(byte[] img, int x, int y, int r, int g, int b, int a, string what)
    {
        int i = (y * W + x) * 4;
        int gr = img[i], gg = img[i + 1], gb = img[i + 2], ga = img[i + 3];
        const int tol = 6;
        bool ok = Math.Abs(gr - r) <= tol && Math.Abs(gg - g) <= tol &&
                  Math.Abs(gb - b) <= tol && Math.Abs(ga - a) <= tol;
        Console.WriteLine($"  ({x,2},{y,2}) = [{gr,3},{gg,3},{gb,3},{ga,3}] expect [{r,3},{g,3},{b,3},{a,3}] {(ok ? "ok" : "FAIL")}  {what}");
        return ok;
    }
}

internal static class BufExt
{
    // The MILCMD_RENDERDATA header is the first 12 bytes; the payload follows.
    public static byte[] AsSpanHeader(this byte[] full, out byte[] payload)
    {
        var header = new byte[12];
        Array.Copy(full, 0, header, 0, 12);
        payload = new byte[full.Length - 12];
        Array.Copy(full, 12, payload, 0, payload.Length);
        return header;
    }
}
