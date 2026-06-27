// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Broadened-decode test: hand-assembles byte-exact real MILCMD for the render ops a
// real WPF tree actually emits -- a MatrixTransform on the root (2x scale), and
// DrawRoundedRectangle / DrawEllipse / DrawGeometry(+geometry resource) with solid
// brushes -- feeds MilcoreEngine, Realizes, and asserts scaled pixels. The 2x root
// transform is what makes a real window's content fill the device-pixel target.
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 64;
    private const int H = 64;

    private const uint HRoot = 2;
    private const uint HRedBrush = 3;
    private const uint HXform = 10;     // 2x scale MatrixTransform
    private const uint HBlueBrush = 5;
    private const uint HGreenBrush = 7;
    private const uint HRectGeom = 8;
    private const uint HContent = 6;
    private const uint HGradBrush = 9;
    private const uint HMagenta = 12;
    private const uint HPath = 11;
    private const uint HCyan = 13;
    private const uint HPathPoly = 14;
    private const uint HClip = 15;
    private const uint HBlack = 16;

    private static int Main()
    {
        var engine = new MilcoreEngine();

        engine.CreateOrAddRef(HRoot, MilResourceTypeId.Visual);

        // 2x scale transform on the root (mirrors WPF's DPI/root transform).
        engine.SubmitCommand(MatrixTransform(HXform, 2, 0, 0, 2, 0, 0));
        engine.SubmitCommand(VisualSetTransform(HRoot, HXform));

        engine.SubmitCommand(SolidColorBrush(HRedBrush, 1, 1, 0, 0, 1));
        engine.SubmitCommand(SolidColorBrush(HBlueBrush, 1, 0, 0, 1, 1));
        engine.SubmitCommand(SolidColorBrush(HGreenBrush, 1, 0, 1, 0, 1));
        engine.SubmitCommand(RectangleGeometry(HRectGeom, 0, 0, 2, 16, 10, 8));   // green rect (local)

        // Horizontal red->blue linear gradient, RelativeToBoundingBox (WPF default).
        engine.SubmitCommand(LinearGradientBrush(HGradBrush, 1.0, 0, 0, 1, 0, mapping: 1, spread: 0,
            new[] { (0f, 1f, 0f, 0f, 1f), (1f, 0f, 0f, 1f, 1f) }));

        // A magenta triangle via a real serialized PathGeometry (MIL_PATHGEOMETRY, Line segments).
        engine.SubmitCommand(SolidColorBrush(HMagenta, 1, 1, 0, 1, 1));
        engine.SubmitCommand(PathGeometryTriangle(HPath, (22, 14), (30, 14), (26, 22), poly: false));
        // A cyan triangle via a PolyLine segment (type 5) -- the encoding StreamGeometry uses.
        engine.SubmitCommand(SolidColorBrush(HCyan, 1, 0, 1, 1, 1));
        engine.SubmitCommand(PathGeometryTriangle(HPathPoly, (16, 24), (24, 24), (20, 30), poly: true));

        // Push/Pop state: a clip rect and a black brush for the opacity test.
        engine.SubmitCommand(SolidColorBrush(HBlack, 1, 0, 0, 0, 1));
        engine.SubmitCommand(RectangleGeometry(HClip, 0, 0, 28, 1, 3, 4));   // clip region (local)

        byte[] content = Concat(
            DrawRoundedRectangleRecord(2, 2, 10, 10, 0, 0, HRedBrush),            // red rect
            DrawEllipseRecord(22, 6, 4, 4, HBlueBrush),                          // blue ellipse
            DrawGeometryRecord(HGreenBrush, 0, HRectGeom),                       // green via geometry resource
            DrawRoundedRectangleRecord(2, 26, 12, 4, 0, 0, HGradBrush),          // gradient-filled rect
            DrawGeometryRecord(HMagenta, 0, HPath),                              // magenta triangle (Line segs)
            DrawGeometryRecord(HCyan, 0, HPathPoly),                             // cyan triangle (PolyLine seg)
            // PushClip: a magenta rect clipped to a small region (only the clip shows).
            PushClipRecord(HClip),
            DrawRoundedRectangleRecord(26, 0, 6, 10, 0, 0, HMagenta),
            PopRecord(),
            // PushOpacity: a 50% black rect over white -> grey (in a clear area).
            PushOpacityRecord(0.5),
            DrawRoundedRectangleRecord(15, 19, 4, 4, 0, 0, HBlack),
            PopRecord());
        engine.CreateOrAddRef(HContent, MilResourceTypeId.RenderData);
        engine.BeginCommand(RenderDataHeader(HContent, content.Length));
        engine.AppendCommandData(content);
        engine.EndCommand();
        engine.SubmitCommand(VisualSetContent(HRoot, HContent));

        engine.Realize();
        SceneVisual? root = engine.VisualByHandle(HRoot);
        if (root is null) return Fail("no root");

        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        byte[] img = renderer.RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));

        bool ok = true;
        // Red rounded rect: local (2,2,10,10) -> device (4..24). (12,12) is its scaled interior.
        ok &= Check(img, 12, 12, 255, 0, 0, 255, "red rounded rect (scaled)");
        // Transform proof: device (22,22) = local (11,11), inside the red rect ONLY because of the 2x
        // scale (un-transformed the rect would end at device 12).
        ok &= Check(img, 22, 22, 255, 0, 0, 255, "2x transform applied (device 22 = local 11 in rect)");
        // Blue ellipse: local centre (22,6) -> device (44,12).
        ok &= Check(img, 44, 12, 0, 0, 255, 255, "blue ellipse (scaled)");
        // Green rect via geometry resource: local (2,16,10,8) -> device (4..24, 32..48).
        ok &= Check(img, 12, 40, 0, 255, 0, 255, "green DrawGeometry(RectangleGeometry)");
        ok &= Check(img, 2, 2, 255, 255, 255, 255, "white background");
        // Gradient rect: local (2,26,12,4) -> device (4..28, 52..60); red->blue left to right.
        ok &= CheckDominant(img, 6, 56, 'R', "linear gradient left edge ~red");
        ok &= CheckDominant(img, 26, 56, 'B', "linear gradient right edge ~blue");
        // Triangle: local (22,14)-(30,14)-(26,22) -> device centroid ~(52,33); (40,40) is outside.
        ok &= Check(img, 52, 33, 255, 0, 255, 255, "magenta triangle (PathGeometry, Line segs)");
        ok &= Check(img, 40, 40, 255, 255, 255, 255, "outside the triangle is background");
        // Cyan poly triangle: local (16,24)-(24,24)-(20,30) -> device centroid ~(40,52).
        ok &= Check(img, 40, 52, 0, 255, 255, 255, "cyan triangle (PolyLine segment / StreamGeometry encoding)");
        // PushClip: clip region local (28,1,3,4) -> device (56..62, 2..10); the magenta rect
        // (local 26,0,6,10 -> device 52..64, 0..20) shows only inside the clip.
        ok &= Check(img, 58, 6, 255, 0, 255, 255, "clipped magenta rect inside the clip");
        ok &= Check(img, 54, 6, 255, 255, 255, 255, "outside the clip (but inside the rect) is clipped away");
        ok &= Check(img, 58, 16, 255, 255, 255, 255, "below the clip is clipped away");
        // PushOpacity 0.5: black rect (local 15,19,4,4 -> device 30..38, 38..46) over white -> grey.
        ok &= Check(img, 34, 42, 128, 128, 128, 255, "50% opacity black rect -> grey");

        if (!ok) return 1;
        Console.WriteLine("PASS: broadened MILCMD decode (transform + rounded-rect + ellipse + geometry) renders");
        return 0;
    }

    // ---- builders ----------------------------------------------------------------

    private static byte[] VisualSetTransform(uint handle, uint hTransform)
    {
        var b = new Buf(); b.U32(0x1c); b.U32(handle); b.U32(hTransform); return b.ToArray();
    }

    private static byte[] VisualSetContent(uint handle, uint hContent)
    {
        var b = new Buf(); b.U32(0x22); b.U32(handle); b.U32(hContent); return b.ToArray();
    }

    // MILCMD_MATRIXTRANSFORM: Handle@4, MilMatrix3x2D (S11,S12,S21,S22,DX,DY)@8.
    private static byte[] MatrixTransform(uint handle, double s11, double s12, double s21, double s22, double dx, double dy)
    {
        var b = new Buf(); b.U32(0x77); b.U32(handle);
        b.F64(s11); b.F64(s12); b.F64(s21); b.F64(s22); b.F64(dx); b.F64(dy);
        b.U32(0); // hMatrixAnimations
        return b.ToArray();
    }

    // MILCMD_RECTANGLEGEOMETRY: Handle@4, RadiusX@8, RadiusY@16, Rect@24.
    private static byte[] RectangleGeometry(uint handle, double rx, double ry, double x, double y, double w, double h)
    {
        var b = new Buf(); b.U32(0x79); b.U32(handle);
        b.F64(rx); b.F64(ry); b.F64(x); b.F64(y); b.F64(w); b.F64(h);
        return b.ToArray();
    }

    // MILCMD_LINEARGRADIENTBRUSH (84-byte struct) + MIL_GRADIENTSTOP[] blob.
    private static byte[] LinearGradientBrush(uint handle, double opacity, float sx, float sy, float ex, float ey,
        uint mapping, uint spread, (float pos, float r, float g, float b, float a)[] stops)
    {
        var b = new Buf();
        b.U32(0x7f); b.U32(handle);
        b.F64(opacity);
        b.F64(sx); b.F64(sy);          // StartPoint
        b.F64(ex); b.F64(ey);          // EndPoint
        b.U32(0); b.U32(0); b.U32(0);  // hOpacityAnim, hTransform, hRelativeTransform
        b.U32(0);                      // ColorInterpolationMode
        b.U32(mapping);                // BrushMappingMode
        b.U32(spread);                 // SpreadMethod
        b.U32((uint)(stops.Length * 24)); // GradientStopsSize
        b.U32(0); b.U32(0);            // hStartPointAnim, hEndPointAnim
        foreach ((float pos, float r, float g, float bl, float a) in stops)
        { b.F64(pos); b.F32(r); b.F32(g); b.F32(bl); b.F32(a); }
        return b.ToArray();
    }

    // MILCMD_PATHGEOMETRY (Handle, hTransform, FillRule, FiguresSize) + serialized blob:
    // MIL_PATHGEOMETRY(48) + MIL_PATHFIGURE(40) + two MIL_SEGMENT_LINE(32) (closed triangle).
    private static byte[] PathGeometryTriangle(uint handle, (double x, double y) a, (double x, double y) b, (double x, double y) c, bool poly)
    {
        // Segments: either two MIL_SEGMENT_LINE(32) or one MIL_SEGMENT_POLY(16 hdr) + 2 points(16).
        int segBytes = poly ? (16 + 2 * 16) : (32 + 32);
        int segCount = poly ? 1 : 2;
        int figureSize = 40 + segBytes;
        int blobSize = 48 + figureSize;
        var blob = new Buf();
        blob.U32((uint)blobSize); blob.U32(0);    // MIL_PATHGEOMETRY: Size, Flags
        blob.F64(0); blob.F64(0); blob.F64(0); blob.F64(0); // Bounds
        blob.U32(1); blob.U32(0);                 // FigureCount=1, ForcePacking
        blob.U32(0); blob.U32(0x4);               // MIL_PATHFIGURE: BackSize, Flags=IsClosed
        blob.U32((uint)segCount); blob.U32((uint)figureSize);
        blob.F64(a.x); blob.F64(a.y);             // StartPoint
        blob.U32(0); blob.U32(0);                 // OffsetToLastSegment, ForcePacking
        if (poly)
        {
            blob.U32(5); blob.U32(0); blob.U32(0); blob.U32(2);  // PolyLine: Type,Flags,BackSize,Count=2
            blob.F64(b.x); blob.F64(b.y); blob.F64(c.x); blob.F64(c.y);
        }
        else
        {
            blob.U32(1); blob.U32(0); blob.U32(0); blob.U32(0); blob.F64(b.x); blob.F64(b.y); // line -> b
            blob.U32(1); blob.U32(0); blob.U32(0); blob.U32(0); blob.F64(c.x); blob.F64(c.y); // line -> c
        }
        byte[] blobBytes = blob.ToArray();

        var cmd = new Buf();
        cmd.U32(0x7d); cmd.U32(handle); cmd.U32(0); cmd.U32(1); cmd.U32((uint)blobBytes.Length);
        cmd.Raw(blobBytes);
        return cmd.ToArray();
    }

    private static byte[] SolidColorBrush(uint handle, double opacity, float r, float g, float bl, float a)
    {
        var buf = new Buf(); buf.U32(0x7e); buf.U32(handle); buf.F64(opacity);
        buf.F32(r); buf.F32(g); buf.F32(bl); buf.F32(a);
        buf.U32(0); buf.U32(0); buf.U32(0); buf.U32(0);
        return buf.ToArray();
    }

    private static byte[] RenderDataHeader(uint handle, int cbData)
    {
        var b = new Buf(); b.U32(0x18); b.U32(handle); b.U32((uint)cbData); return b.ToArray();
    }

    // Record: RecordHeader{Size, Id} + payload.
    private static byte[] Record(uint id, byte[] payload)
    {
        var b = new Buf(); b.U32((uint)(payload.Length + 8)); b.U32(id); b.Raw(payload); return b.ToArray();
    }

    private static byte[] DrawRoundedRectangleRecord(double x, double y, double w, double h, double rx, double ry, uint hBrush)
    {
        var p = new Buf(); p.F64(x); p.F64(y); p.F64(w); p.F64(h); p.F64(rx); p.F64(ry); p.U32(hBrush); p.U32(0);
        return Record(0x42, p.ToArray());
    }

    private static byte[] DrawEllipseRecord(double cx, double cy, double rx, double ry, uint hBrush)
    {
        var p = new Buf(); p.F64(cx); p.F64(cy); p.F64(rx); p.F64(ry); p.U32(hBrush); p.U32(0);
        return Record(0x44, p.ToArray());
    }

    // Push/Pop render-data records (RecordHeader{Size,Id} + payload).
    private static byte[] PushClipRecord(uint hClip)
    {
        var b = new Buf(); b.U32(16); b.U32(0x4d); b.U32(hClip); b.U32(0); return b.ToArray();
    }

    private static byte[] PushOpacityRecord(double opacity)
    {
        var b = new Buf(); b.U32(16); b.U32(0x4f); b.F64(opacity); return b.ToArray();
    }

    private static byte[] PopRecord()
    {
        var b = new Buf(); b.U32(8); b.U32(0x56); return b.ToArray();
    }

    private static byte[] DrawGeometryRecord(uint hBrush, uint hPen, uint hGeometry)
    {
        var p = new Buf(); p.U32(hBrush); p.U32(hPen); p.U32(hGeometry); p.U32(0);
        return Record(0x46, p.ToArray());
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var b = new Buf(); foreach (byte[] p in parts) b.Raw(p); return b.ToArray();
    }

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

    // Assert one channel clearly dominates (for sampling a continuous gradient).
    private static bool CheckDominant(byte[] img, int x, int y, char channel, string what)
    {
        int i = (y * W + x) * 4;
        int r = img[i], g = img[i + 1], b = img[i + 2];
        bool ok = channel switch
        {
            'R' => r > 150 && r > b + 80,
            'B' => b > 150 && b > r + 80,
            _ => false,
        };
        Console.WriteLine($"  ({x,2},{y,2}) = [{r,3},{g,3},{b,3}] want {channel}-dominant {(ok ? "ok" : "FAIL")}  {what}");
        return ok;
    }

    private static int Fail(string m) { Console.Error.WriteLine("FAIL: " + m); return 1; }
}
