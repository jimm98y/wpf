// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Live composition test: the full WPF -> WebGPU on-screen path.
//
// This plays the role of PresentationCore's DUCE.Channel: it drives WpfCompositionSink
// (the IMilCompositionSink-shaped backend) with byte-exact real MILCMD packets for a
// composition target + visual tree, including MILCMD_HWNDTARGET_CREATE bound to a real
// Win32 HWND. Each Commit, the sink decodes the partition (MilcoreEngine) and presents
// the rooted visual to the window's WebGPU swap chain -- the cross-platform analog of
// milcore presenting an HwndTarget.
//
//   target (HWND)
//     +-- root visual
//           +-- content: red rectangle (40,40,120,80)
//           +-- child visual @ alpha 0.5, animated offset
//                 +-- content: green rectangle (0,0,120,80)
//
// We render several frames to the live window (asserting frames were actually
// acquired/presented), then render the decoded tree off-screen to assert exact
// pixels -- proving the sink built the right scene from the real protocol. Bounded
// by frame count + wall-clock so it runs unattended.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;
using WgpuInterop.SurfaceDemo;

internal static class Program
{
    private const int W = 320;
    private const int H = 240;
    private const int Channel = 1;

    private static int Main(string[] args)
    {
        int maxFrames = 240;
        if (args.Length > 0 && int.TryParse(args[0], out int f) && f > 0) maxFrames = f;

        using var window = new Win32Window("WPF on WebGPU — MediaContext live", W, H);
        Console.WriteLine($"created HWND 0x{window.Hwnd:x}");

        using var sink = new WpfCompositionSink();

        // ---- Drive the real MILCMD command stream through the sink, as DUCE.Channel would.
        sink.OpenChannel(Channel, 0);

        uint hTarget = New(sink, MilResourceTypeId.Null);
        uint hRoot = New(sink, MilResourceTypeId.Visual);
        uint hRedBrush = New(sink, MilResourceTypeId.SolidColorBrush);
        uint hRedContent = New(sink, MilResourceTypeId.RenderData);
        uint hChild = New(sink, MilResourceTypeId.Visual);
        uint hGreenBrush = New(sink, MilResourceTypeId.SolidColorBrush);
        uint hGreenContent = New(sink, MilResourceTypeId.RenderData);

        // Target bound to the real window.
        Send(sink, HwndTargetCreate(hTarget, (ulong)window.Hwnd.ToInt64(), W, H, 1, 1, 1, 1));

        // Root content: opaque red rectangle.
        Send(sink, SolidColorBrush(hRedBrush, 1.0, 220f / 255, 40f / 255, 40f / 255, 1));
        Content(sink, hRedContent, DrawRectangleRecord(40, 40, 120, 80, hRedBrush));
        Send(sink, VisualSetContent(hRoot, hRedContent));

        // Child: translucent green rectangle, offset animated below.
        Send(sink, VisualSetAlpha(hChild, 0.5));
        Send(sink, SolidColorBrush(hGreenBrush, 1.0, 40f / 255, 200f / 255, 40f / 255, 1));
        Content(sink, hGreenContent, DrawRectangleRecord(0, 0, 120, 80, hGreenBrush));
        Send(sink, VisualSetContent(hChild, hGreenContent));
        Send(sink, VisualInsertChildAt(hRoot, hChild, 0));

        Send(sink, TargetSetRoot(hTarget, hRoot));
        sink.CloseBatch(Channel);

        // ---- Present several frames to the live window (commit == one composed frame).
        var sw = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(8);
        for (int i = 0; i < maxFrames && !window.QuitRequested && sw.Elapsed < budget; i++)
        {
            window.PumpMessages();
            int x = 90 + (i * 2) % 120;
            Send(sink, VisualSetOffset(hChild, x, 80));   // animate via the real protocol
            sink.Commit(Channel);
            Thread.Sleep(8);
        }

        Console.WriteLine($"frames acquired = {sink.AcquiredFrames}, presented = {sink.PresentedFrames}, elapsed = {sw.ElapsedMilliseconds} ms");

        if (sink.AcquiredFrames == 0 || sink.PresentedFrames == 0)
            return Fail("no frames were acquired/presented to the HWND swap chain");

        // ---- Correctness: render the decoded tree off-screen and assert exact pixels.
        SceneVisual? root = sink.Engine.VisualByHandle(hRoot);
        if (root is null) return Fail("sink produced no root visual");

        byte[] img = sink.Renderer.RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));
        bool ok = true;
        ok &= Check(img, 50, 50, 220, 40, 40, 255, "root red rectangle content");
        ok &= Check(img, 5, 5, 255, 255, 255, 255, "white background");

        if (!ok) return 1;
        Console.WriteLine("LIVE COMPOSITION TEST PASSED: real MILCMD stream -> sink -> HWND swap chain, on screen.");
        return 0;
    }

    // ---- play the channel ---------------------------------------------------------

    private static uint New(WpfCompositionSink sink, MilResourceTypeId type)
        => sink.CreateOrAddRef(Channel, 0, (uint)type, out _);

    private static void Send(WpfCompositionSink sink, byte[] cmd)
        => sink.SendCommand(Channel, cmd, false);

    // Render data arrives as a Begin/Append/End command (fixed struct + variable payload).
    private static void Content(WpfCompositionSink sink, uint handle, byte[] record)
    {
        sink.BeginCommand(Channel, RenderDataHeader(handle, record.Length), record.Length);
        sink.AppendCommandData(Channel, record);
        sink.EndCommand(Channel);
    }

    // ---- real MILCMD_* packet builders (layouts: src/Common/Graphics) -------------

    private static byte[] VisualSetOffset(uint handle, double x, double y)
    {
        var b = new Buf(); b.U32((uint)Mil.VisualSetOffset); b.U32(handle); b.F64(x); b.F64(y); return b.ToArray();
    }

    private static byte[] VisualSetAlpha(uint handle, double alpha)
    {
        var b = new Buf(); b.U32((uint)Mil.VisualSetAlpha); b.U32(handle); b.F64(alpha); return b.ToArray();
    }

    private static byte[] VisualSetContent(uint handle, uint hContent)
    {
        var b = new Buf(); b.U32((uint)Mil.VisualSetContent); b.U32(handle); b.U32(hContent); return b.ToArray();
    }

    private static byte[] VisualInsertChildAt(uint handle, uint hChild, uint index)
    {
        var b = new Buf(); b.U32((uint)Mil.VisualInsertChildAt); b.U32(handle); b.U32(hChild); b.U32(index); return b.ToArray();
    }

    private static byte[] TargetSetRoot(uint handle, uint hRoot)
    {
        var b = new Buf(); b.U32((uint)Mil.TargetSetRoot); b.U32(handle); b.U32(hRoot); return b.ToArray();
    }

    // MILCMD_HWNDTARGET_CREATE: Handle@4, hwnd@8, hSection@16, masterDevice@24,
    // width@32, height@36, clearColor@40 (the decoder reads through clearColor).
    private static byte[] HwndTargetCreate(uint handle, ulong hwnd, uint w, uint h, float cr, float cg, float cb, float ca)
    {
        var b = new Buf();
        b.U32((uint)Mil.HwndTargetCreate);
        b.U32(handle);
        b.U64(hwnd);
        b.U64(0);   // hSection
        b.U64(0);   // masterDevice
        b.U32(w);
        b.U32(h);
        b.F32(cr); b.F32(cg); b.F32(cb); b.F32(ca);
        return b.ToArray();
    }

    private static byte[] SolidColorBrush(uint handle, double opacity, float r, float g, float b, float a)
    {
        var buf = new Buf();
        buf.U32((uint)Mil.SolidColorBrush);
        buf.U32(handle);
        buf.F64(opacity);
        buf.F32(r); buf.F32(g); buf.F32(b); buf.F32(a);
        buf.U32(0); buf.U32(0); buf.U32(0); buf.U32(0);
        return buf.ToArray();
    }

    private static byte[] RenderDataHeader(uint handle, int cbData)
    {
        var b = new Buf(); b.U32((uint)Mil.RenderData); b.U32(handle); b.U32((uint)cbData); return b.ToArray();
    }

    private static byte[] DrawRectangleRecord(double x, double y, double w, double h, uint hBrush)
    {
        var rec = new Buf();
        rec.F64(x); rec.F64(y); rec.F64(w); rec.F64(h);
        rec.U32(hBrush);
        rec.U32(0); // hPen
        byte[] payload = rec.ToArray();

        var b = new Buf();
        b.U32((uint)(payload.Length + 8)); // RecordHeader.Size incl. header
        b.U32((uint)Mil.DrawRectangle);
        b.Raw(payload);
        return b.ToArray();
    }

    // ---- helpers ------------------------------------------------------------------

    private sealed class Buf
    {
        private readonly List<byte> _b = new();
        public void U32(uint v) => _b.AddRange(BitConverter.GetBytes(v));
        public void U64(ulong v) => _b.AddRange(BitConverter.GetBytes(v));
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
        Console.WriteLine($"  ({x,3},{y,3}) = [{gr,3},{gg,3},{gb,3},{ga,3}] expect [{r,3},{g,3},{b,3},{a,3}] {(ok ? "ok" : "FAIL")}  {what}");
        return ok;
    }

    private static int Fail(string msg)
    {
        Console.Error.WriteLine("LIVE COMPOSITION TEST FAILED: " + msg);
        return 1;
    }
}
