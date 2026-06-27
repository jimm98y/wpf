// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// MediaContext wiring test.
//
// Proves the WebGPU backend consumes WPF's *real* milcore command protocol -- the
// exact MILCMD_* binary that PresentationCore's DUCE.Channel emits -- not the
// WgpuInterop mini-protocol. We hand-assemble byte-for-byte faithful command
// packets (struct layouts from src/Common/Graphics) for a small visual tree:
//
//     root visual
//       +-- content: DrawRectangle (8,8,20,20) with an opaque red SolidColorBrush
//       +-- child visual @ offset (32,32), alpha 0.5
//             +-- content: DrawRectangle (0,0,20,20) with an opaque black brush
//
// feed them through MilcoreEngine (the managed UCE resource table + decoder) and
// render the rebuilt tree. A few absolute pixels are asserted: the real protocol
// must actually paint.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 64;
    private const int H = 64;

    // Resource handles (allocated by the "client" exactly as WPF's MultiChannelResource would).
    private const uint HTarget = 1;
    private const uint HRoot = 2;
    private const uint HRedBrush = 3;
    private const uint HRedContent = 4;
    private const uint HChild = 5;
    private const uint HBlackBrush = 6;
    private const uint HBlackContent = 7;

    private static int Main()
    {
        var engine = new MilcoreEngine();

        // --- Build the partition the way DUCE.Channel would over a batch ---------

        // Root visual + its red rectangle content.
        engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);

        engine.CreateOrAddRef(HRedBrush, MilResourceTypeId.SolidColorBrush);
        engine.SubmitCommand(SolidColorBrush(HRedBrush, opacity: 1.0, r: 1, g: 0, b: 0, a: 1));

        // Render data arrives the way the channel really marshals it: a Begin/Append/End
        // command (fixed MILCMD_RENDERDATA struct, then the variable-length record bytes).
        engine.CreateOrAddRef(HRedContent, MilResourceTypeId.RenderData);
        byte[] redRecord = DrawRectangleRecord(8, 8, 20, 20, HRedBrush);
        engine.BeginCommand(RenderDataHeader(HRedContent, redRecord.Length));
        engine.AppendCommandData(redRecord);
        engine.EndCommand();
        engine.SubmitCommand(VisualSetContent(HRoot, HRedContent));

        // Child visual @ (32,32), 50% opacity, with a black rectangle.
        engine.CreateOrAddRef(HChild, MilResourceTypeId.Visual);
        engine.SubmitCommand(VisualSetOffset(HChild, 32, 32));
        engine.SubmitCommand(VisualSetAlpha(HChild, 0.5));

        engine.CreateOrAddRef(HBlackBrush, MilResourceTypeId.SolidColorBrush);
        engine.SubmitCommand(SolidColorBrush(HBlackBrush, opacity: 1.0, r: 0, g: 0, b: 0, a: 1));

        engine.CreateOrAddRef(HBlackContent, MilResourceTypeId.RenderData);
        engine.SubmitCommand(RenderData(HBlackContent, DrawRectangleRecord(0, 0, 20, 20, HBlackBrush)));
        engine.SubmitCommand(VisualSetContent(HChild, HBlackContent));

        engine.SubmitCommand(VisualInsertChildAt(HRoot, HChild, 0));

        // Hook the root onto the composition target.
        engine.SubmitCommand(TargetSetRoot(HTarget, HRoot));

        // --- Render ---------------------------------------------------------------

        engine.Realize();   // parse render data with full resource state
        SceneVisual? root = engine.Root;
        if (root is null) return Fail("MilcoreEngine produced no root");

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        byte[] img = renderer.RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));

        bool ok = true;
        ok &= Check(img, 18, 18, 255, 0, 0, 255, "opaque red rectangle (root content)");
        ok &= Check(img, 40, 40, 128, 128, 128, 255, "50% black over white (child @ offset, alpha 0.5)");
        ok &= Check(img, 2, 2, 255, 255, 255, 255, "white background");
        ok &= Check(img, 40, 5, 255, 255, 255, 255, "outside child rect stays background");

        if (!ok) return 1;
        Console.WriteLine("PASS: real WPF MILCMD protocol drove the WebGPU renderer");
        return 0;
    }

    // ---- Real MILCMD_* packet builders (layouts: src/Common/Graphics) ------------

    private static byte[] VisualSetOffset(uint handle, double x, double y)
    {
        var b = new Buf();
        b.U32((uint)Mil.VisualSetOffset);
        b.U32(handle);
        b.F64(x);
        b.F64(y);
        return b.ToArray();
    }

    private static byte[] VisualSetAlpha(uint handle, double alpha)
    {
        var b = new Buf();
        b.U32((uint)Mil.VisualSetAlpha);
        b.U32(handle);
        b.F64(alpha);
        return b.ToArray();
    }

    private static byte[] VisualSetContent(uint handle, uint hContent)
    {
        var b = new Buf();
        b.U32((uint)Mil.VisualSetContent);
        b.U32(handle);
        b.U32(hContent);
        return b.ToArray();
    }

    private static byte[] VisualInsertChildAt(uint handle, uint hChild, uint index)
    {
        var b = new Buf();
        b.U32((uint)Mil.VisualInsertChildAt);
        b.U32(handle);
        b.U32(hChild);
        b.U32(index);
        return b.ToArray();
    }

    private static byte[] TargetSetRoot(uint handle, uint hRoot)
    {
        var b = new Buf();
        b.U32((uint)Mil.TargetSetRoot);
        b.U32(handle);
        b.U32(hRoot);
        return b.ToArray();
    }

    // MILCMD_SOLIDCOLORBRUSH: Type@0, Handle@4, Opacity(double)@8, Color(MilColorF)@16,
    // hOpacityAnimations@32, hTransform@36, hRelativeTransform@40, hColorAnimations@44.
    private static byte[] SolidColorBrush(uint handle, double opacity, float r, float g, float b, float a)
    {
        var buf = new Buf();
        buf.U32((uint)Mil.SolidColorBrush);
        buf.U32(handle);
        buf.F64(opacity);
        buf.F32(r); buf.F32(g); buf.F32(b); buf.F32(a);
        buf.U32(0); buf.U32(0); buf.U32(0); buf.U32(0); // animation/transform handles (none)
        return buf.ToArray();
    }

    // MILCMD_RENDERDATA fixed struct: Type@0, Handle@4, cbData@8 (the BeginCommand header;
    // the cbData payload bytes follow via AppendCommandData).
    private static byte[] RenderDataHeader(uint handle, int cbData)
    {
        var b = new Buf();
        b.U32((uint)Mil.RenderData);
        b.U32(handle);
        b.U32((uint)cbData);
        return b.ToArray();
    }

    // MILCMD_RENDERDATA as a single coalesced buffer (struct + payload), as delivered to
    // SendCommand-style ingestion.
    private static byte[] RenderData(uint handle, byte[] payload)
    {
        var b = new Buf();
        b.Raw(RenderDataHeader(handle, payload.Length));
        b.Raw(payload);
        return b.ToArray();
    }

    // One render-data record: RecordHeader { int Size; MILCMD Id } + MILCMD_DRAW_RECTANGLE.
    // MILCMD_DRAW_RECTANGLE: Rect rectangle@0 (4 doubles), u32 hBrush@32, u32 hPen@36 (size 40).
    private static byte[] DrawRectangleRecord(double x, double y, double w, double h, uint hBrush)
    {
        var rec = new Buf();
        rec.F64(x); rec.F64(y); rec.F64(w); rec.F64(h);
        rec.U32(hBrush);
        rec.U32(0); // hPen (none)
        byte[] payload = rec.ToArray(); // 40 bytes, QWORD aligned

        var b = new Buf();
        b.U32((uint)(payload.Length + 8)); // RecordHeader.Size includes the 8-byte header
        b.U32((uint)Mil.DrawRectangle);
        b.Raw(payload);
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
        const int tol = 4;
        bool ok = Math.Abs(gr - r) <= tol && Math.Abs(gg - g) <= tol &&
                  Math.Abs(gb - b) <= tol && Math.Abs(ga - a) <= tol;
        Console.WriteLine($"  ({x,2},{y,2}) = [{gr,3},{gg,3},{gb,3},{ga,3}] expect [{r,3},{g,3},{b,3},{a,3}] {(ok ? "ok" : "FAIL")}  {what}");
        return ok;
    }

    private static int Fail(string msg)
    {
        Console.Error.WriteLine("FAIL: " + msg);
        return 1;
    }
}
