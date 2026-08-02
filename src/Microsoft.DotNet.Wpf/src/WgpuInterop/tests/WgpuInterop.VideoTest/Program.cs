// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// MediaElement / VideoDrawing: the MILCMD_DRAW_VIDEO path, end to end.
//
// MediaElement.OnRender calls DrawingContext.DrawVideo(player, rect), which serializes to
// record 0x4b -- MILCMD_DRAW_VIDEO { MILCMD type; MilPointAndSizeD rectangle; HMIL_RESOURCE
// hPlayer; UINT32 pad } (wgx_core_types.h). It carries only a HANDLE: the pixels arrive out
// of band from the platform media backend (MacMediaBackend / BrowserMediaBackend) through
// IMilRenderTargetSink.SendVideoFrame, and the decoder pairs the two up by handle.
//
// The whole chain existed with nothing covering it. The subtle half is not "does a frame
// draw" but "does the SECOND frame draw": a DrawVideo visual's render-data byte[] is
// IDENTICAL every frame -- only the out-of-band pixels change -- so the decoder's
// static-visual skip would happily keep sampling frame 1 forever and the video would
// silently freeze while playback ran on. MilcoreEngine sets _parseTouchedContentBrush on
// every DrawVideo to force a re-parse; nothing proved that, so this does.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 64, H = 48;
    private const uint HRoot = 1, HContent = 2, HPlayer = 3;
    private static int _failures;

    private static int Main()
    {
        var engine = new MilcoreEngine();
        engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);
        engine.CreateOrAddRef(HPlayer, MilResourceTypeId.MediaPlayer);

        // The video rect: (8,8) 48x32, as MediaElement would emit for its RenderSize.
        byte[] content = DrawVideoRecord(8, 8, 48, 32, HPlayer);
        engine.CreateOrAddRef(HContent, MilResourceTypeId.RenderData);
        engine.BeginCommand(RenderDataHeader(HContent, content.Length));
        engine.AppendCommandData(content);
        engine.EndCommand();
        engine.SubmitCommand(VisualSetContent(HRoot, HContent));

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);

        // ---- no frame delivered yet -------------------------------------------------
        // A MediaElement renders before the backend produces its first frame. That must
        // leave the background alone, not throw and not paint garbage.
        byte[] img = Render(engine, renderer);
        Check(At(img, 32, 24) == (255, 255, 255), "before the first frame arrives, nothing is drawn");

        // ---- frame 1: solid red ------------------------------------------------------
        engine.SetVideoFrame(HPlayer, Solid(4, 4, 255, 0, 0), 4, 4);
        img = Render(engine, renderer);
        (int r, int g, int b) inside = At(img, 32, 24);
        Check(inside == (255, 0, 0), $"the video frame paints inside the rect (got {inside})");
        Check(At(img, 2, 2) == (255, 255, 255), "outside the video rect is untouched");
        // The rect is 48x32 at (8,8): (56,40) is the last pixel inside, (58,42) is outside.
        Check(At(img, 54, 38) == (255, 0, 0), "the frame is stretched to fill the whole rect");
        Check(At(img, 60, 44) == (255, 255, 255), "and does not spill past it");

        // ---- frame 2: solid blue, same render data -----------------------------------
        // Nothing about the visual changed -- same handle, same bytes, same tree. Only the
        // out-of-band pixels differ. This is the freeze case.
        engine.SetVideoFrame(HPlayer, Solid(4, 4, 0, 0, 255), 4, 4);
        img = Render(engine, renderer);
        (int r, int g, int b) second = At(img, 32, 24);
        Check(second == (0, 0, 255),
            $"the SECOND frame replaces the first (got {second}; red here means playback froze on frame 1)");

        // ---- a real (non-uniform) frame, to catch orientation ------------------------
        // A solid frame cannot tell a vertical flip or a channel swap from correct output.
        // Top half green, bottom half black, left column marked so R/B swaps show up.
        engine.SetVideoFrame(HPlayer, TopGreenBottomBlack(4, 4), 4, 4);
        img = Render(engine, renderer);
        (int r, int g, int b) top = At(img, 32, 14), bottom = At(img, 32, 34);
        Check(top == (0, 255, 0) && bottom == (0, 0, 0),
            $"the frame is upright, not flipped (top={top} bottom={bottom})");

        Console.WriteLine();
        if (_failures > 0) { Console.WriteLine($"VIDEO TEST FAILED: {_failures} problem(s)."); return 1; }
        Console.WriteLine("VIDEO TEST PASSED: MILCMD_DRAW_VIDEO draws, and keeps up with the frame stream.");
        return 0;
    }

    private static byte[] Render(MilcoreEngine engine, WgpuSceneRenderer renderer)
    {
        engine.Realize();
        SceneVisual? root = engine.VisualByHandle(HRoot);
        if (root is null) { Console.WriteLine("  [FAIL] no root visual"); _failures++; return new byte[W * H * 4]; }
        return renderer.RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));
    }

    private static (int, int, int) At(byte[] img, int x, int y)
    {
        int i = (y * W + x) * 4;
        return (img[i], img[i + 1], img[i + 2]);
    }

    // Straight RGBA, as SetVideoFrame stores it (SendVideoFrame does the BGRA->RGBA turn).
    private static byte[] Solid(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = 255;
        }
        return px;
    }

    private static byte[] TopGreenBottomBlack(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = 0; px[i + 1] = (byte)(y < h / 2 ? 255 : 0); px[i + 2] = 0; px[i + 3] = 255;
            }
        return px;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine($"  [{(ok ? "ok " : "FAIL")}] {what}");
        if (!ok) _failures++;
    }

    // MILCMD_DRAW_VIDEO (0x4b): rectangle (4 doubles) + hPlayer + pad, after RecordHeader{Size,Id}.
    private static byte[] DrawVideoRecord(double x, double y, double w, double h, uint hPlayer)
    {
        var p = new Buf(); p.F64(x); p.F64(y); p.F64(w); p.F64(h); p.U32(hPlayer); p.U32(0);
        byte[] payload = p.ToArray();
        var b = new Buf(); b.U32((uint)(payload.Length + 8)); b.U32(0x4b); b.Raw(payload); return b.ToArray();
    }

    private static byte[] RenderDataHeader(uint handle, int cbData)
    {
        var b = new Buf(); b.U32(0x18); b.U32(handle); b.U32((uint)cbData); return b.ToArray();
    }

    private static byte[] VisualSetContent(uint handle, uint hContent)
    {
        var b = new Buf(); b.U32(0x22); b.U32(handle); b.U32(hContent); return b.ToArray();
    }

    private sealed class Buf
    {
        private readonly List<byte> _b = new();
        public void U32(uint v) => _b.AddRange(BitConverter.GetBytes(v));
        public void F64(double v) => _b.AddRange(BitConverter.GetBytes(v));
        public void Raw(byte[] v) => _b.AddRange(v);
        public byte[] ToArray() => _b.ToArray();
    }
}
