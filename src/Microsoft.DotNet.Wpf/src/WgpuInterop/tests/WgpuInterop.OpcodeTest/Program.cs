// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// MILCMD opcodes that the decoder used to skip.
//
// Every one of these is emitted by real WPF and was silently dropped -- an unknown record is
// skipped by its size header, so the shape simply did not appear (or, for guidelines, appeared
// unsnapped). Each case here drives the ACTUAL wire format, with offsets taken from
// Common/Graphics/Generated/wgx_commands.cs and Media/Generated/RenderData.cs, not from the
// decoder's own comments -- a decoder and a test that share an assumption cannot disprove it.
//
//   0x7b MilCmdGeometryGroup      <GeometryGroup>, and Path data with several figures
//   0x7c MilCmdCombinedGeometry   <CombinedGeometry>, Geometry.Combine
//   0x8c MilCmdGuidelineSet       the GuidelineSet resource
//   0x53 MilPushGuidelineY1       emitted by SimpleTextLine.Draw for EVERY text line
//   0x54 MilPushGuidelineY2       emitted by LineServicesCallbacks for underlines
//   0x4a MilDrawDrawing           DrawingContext.DrawDrawing
//

using System;
using System.Collections.Generic;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Microsoft.Wpf.Interop.WebGpu.Composition.Protocol;

internal static class Program
{
    private const int W = 120, H = 80;
    private static int _failures;

    private static int Main()
    {
        bool ok = GeometryGroupCase() & CombinedGeometryCase() & GuidelineCase() & DrawDrawingCase()
                & AnimateCase() & BitmapCacheBrushCase() & OpacityMaskCase() & UnknownCommandCase();

        Console.WriteLine();
        if (!ok || _failures > 0) { Console.WriteLine($"OPCODE TEST FAILED: {_failures} problem(s)."); return 1; }
        Console.WriteLine("OPCODE TEST PASSED: the previously-skipped records decode and draw.");
        return 0;
    }

    // 0x7b: two disjoint rects in one GeometryGroup, filled by a single DrawGeometry.
    private static bool GeometryGroupCase()
    {
        Console.WriteLine("MilCmdGeometryGroup (0x7b)");
        var e = new MilcoreEngine();
        e.CreateOrAddRef(1, MilResourceTypeId.Visual);
        e.SubmitCommand(SolidColorBrush(10, 1, 0, 0, 1));            // opaque red
        e.SubmitCommand(RectangleGeometry(20, 10, 10, 20, 20));
        e.SubmitCommand(RectangleGeometry(21, 60, 10, 20, 20));
        e.SubmitCommand(GeometryGroup(22, fillRule: 0, 20, 21));
        byte[] img = RenderContent(e, DrawGeometryRecord(10, 0, 22));

        bool ok = true;
        ok &= Px(img, 20, 20, 255, 0, 0, "first child of the group is filled");
        ok &= Px(img, 70, 20, 255, 0, 0, "second child of the group is filled");
        ok &= Px(img, 45, 20, 255, 255, 255, "the gap between them is not");
        return ok;
    }

    // 0x7c: a 40x40 square minus a 20x40 bite out of its right half (Exclude).
    private static bool CombinedGeometryCase()
    {
        Console.WriteLine("MilCmdCombinedGeometry (0x7c)");
        var e = new MilcoreEngine();
        e.CreateOrAddRef(1, MilResourceTypeId.Visual);
        e.SubmitCommand(SolidColorBrush(10, 0, 0, 1, 1));            // opaque blue
        e.SubmitCommand(RectangleGeometry(20, 20, 20, 40, 40));
        e.SubmitCommand(RectangleGeometry(21, 40, 20, 20, 40));
        e.SubmitCommand(CombinedGeometry(22, mode: 2 /* Exclude */, 20, 21));
        byte[] img = RenderContent(e, DrawGeometryRecord(10, 0, 22));

        bool ok = true;
        ok &= Px(img, 28, 40, 0, 0, 255, "the part of geometry1 outside geometry2 survives");
        ok &= Px(img, 50, 40, 255, 255, 255, "the excluded region is gone");
        return ok;
    }

    // 0x8c + 0x53/0x54: the guidelines must reach the visual. Asserting the SNAPPED pixels
    // would only restate the renderer's snapping, which GuidelineTest already covers; what is
    // new here is that these records are decoded at all, so the check is that the visual came
    // out of the parse carrying them.
    private static bool GuidelineCase()
    {
        Console.WriteLine("MilCmdGuidelineSet (0x8c) + MilPushGuidelineY1/Y2 (0x53/0x54)");
        var e = new MilcoreEngine();
        e.CreateOrAddRef(1, MilResourceTypeId.Visual);
        e.SubmitCommand(SolidColorBrush(10, 0, 0, 0, 1));
        e.SubmitCommand(GuidelineSetCmd(30, new double[] { 12.0 }, new double[] { 5.5 }));

        byte[] content = Concat(
            PushGuidelineSetRecord(30),
            DrawRectangleRecord(10, 0, 4, 5.5, 40, 1),
            PopRecord(),
            PushGuidelineY1Record(20.5),
            DrawRectangleRecord(10, 0, 4, 20.5, 40, 1),
            PopRecord(),
            // A text decoration: baseline at 40.5, rule 3.25 below it.
            PushGuidelineY2Record(40.5, 3.25),
            DrawRectangleRecord(10, 0, 4, 43.75, 40, 1),
            PopRecord());

        SceneVisual root = Realize(e, content);
        bool ok = true;
        float[] gy = root.GuidelinesY ?? Array.Empty<float>();
        float[] gx = root.GuidelinesX ?? Array.Empty<float>();
        Console.WriteLine($"    X=[{string.Join(", ", gx)}]  Y=[{string.Join(", ", gy)}]");
        ok &= Has(gx, 12.0f, "GuidelineSet's X guideline");
        ok &= Has(gy, 5.5f, "GuidelineSet's Y guideline");
        ok &= Has(gy, 20.5f, "PushGuidelineY1's baseline");
        ok &= Has(gy, 40.5f, "PushGuidelineY2's leading coordinate");
        ok &= Has(gy, 43.75f, "PushGuidelineY2's driven coordinate (leading + offset)");
        return ok;
    }

    // 0x4a: a DrawingGroup holding one GeometryDrawing, drawn via DrawDrawing. The group
    // carries opacity 0.5, so a correct flatten shows a half-strength fill -- proving the
    // group's state folded into the record stream rather than being dropped.
    private static bool DrawDrawingCase()
    {
        Console.WriteLine("MilDrawDrawing (0x4a)");
        var e = new MilcoreEngine();
        e.CreateOrAddRef(1, MilResourceTypeId.Visual);
        e.SubmitCommand(SolidColorBrush(10, 0, 0, 0, 1));            // opaque black
        e.SubmitCommand(RectangleGeometry(20, 20, 20, 40, 30));
        e.SubmitCommand(GeometryDrawingCmd(40, hBrush: 10, hPen: 0, hGeometry: 20));
        e.SubmitCommand(DrawingGroupCmd(41, opacity: 0.5, children: new uint[] { 40 }));
        byte[] img = RenderContent(e, DrawDrawingRecord(41));

        bool ok = true;
        (int r, int g, int b) c = At(img, 40, 35);
        // Black at 50% over white is mid-grey; full black would mean the group opacity was lost.
        ok &= Near(c, 128, 128, 128, 10, $"the drawing renders at the group's 0.5 opacity (got {c})");
        ok &= Px(img, 5, 5, 255, 255, 255, "outside the drawing is untouched");
        return ok;
    }

    // 0x41 + 0x11: DrawRectangleAnimate carries a static rect AND a handle to a RectResource
    // that AnimationClockResource re-sends every tick. The animated value must win -- otherwise
    // the drawing is pinned to its base value, which looks like "the animation does not run".
    private static bool AnimateCase()
    {
        Console.WriteLine("MilDrawRectangleAnimate (0x41) + MilCmdRectResource (0x11)");
        var e = new MilcoreEngine();
        e.CreateOrAddRef(1, MilResourceTypeId.Visual);
        e.SubmitCommand(SolidColorBrush(10, 0, 1, 0, 1));
        // The clock's current value: far from the static rect, so the two are unmistakable.
        e.SubmitCommand(RectResourceCmd(50, 60, 10, 40, 30));
        byte[] img = RenderContent(e, DrawRectangleAnimateRecord(10, 0, 10, 10, 40, 30, hRectAnim: 50));

        bool ok = true;
        ok &= Px(img, 80, 25, 0, 255, 0, "the ANIMATED rect is drawn");
        ok &= Px(img, 30, 25, 255, 255, 255, "the static rect is not");

        // With no value published for the handle yet -- the first frame, before the clock ticks --
        // the static value has to stand in, or the drawing flickers out on frame one.
        var e2 = new MilcoreEngine();
        e2.CreateOrAddRef(1, MilResourceTypeId.Visual);
        e2.SubmitCommand(SolidColorBrush(10, 0, 1, 0, 1));
        byte[] img2 = RenderContent(e2, DrawRectangleAnimateRecord(10, 0, 10, 10, 40, 30, hRectAnim: 50));
        ok &= Px(img2, 30, 25, 0, 255, 0, "before the clock publishes, the static rect is used");
        return ok;
    }

    // 0x84: a BitmapCacheBrush paints its cached target visual across the fill area. It is not
    // a viewport/viewbox TileBrush, so it maps onto the content-brush machinery with a unit
    // bounding-box-relative viewport and Stretch=Fill.
    private static bool BitmapCacheBrushCase()
    {
        Console.WriteLine("MilCmdBitmapCacheBrush (0x84)");
        var e = new MilcoreEngine();
        e.CreateOrAddRef(1, MilResourceTypeId.Visual);
        e.CreateOrAddRef(5, MilResourceTypeId.Visual);          // the cached target
        e.SubmitCommand(SolidColorBrush(10, 1, 0, 0, 1));       // it paints itself red
        byte[] targetContent = DrawRectangleRecord(10, 0, 0, 0, 20, 20);
        e.CreateOrAddRef(6, MilResourceTypeId.RenderData);
        e.BeginCommand(RenderDataHeader(6, targetContent.Length));
        e.AppendCommandData(targetContent);
        e.EndCommand();
        e.SubmitCommand(VisualSetContent(5, 6));
        // BitmapCacheBrush.Target is normally a visual in the tree -- that is what makes caching
        // it worthwhile -- and the GPU content-brush path samples the texture it rendered.
        e.SubmitCommand(VisualInsertChildAt(1, 5, 0));
        e.SubmitCommand(BitmapCacheBrushCmd(11, opacity: 1.0, hInternalTarget: 5));

        // The fill sits clear of where the target draws ITSELF (0,0,20,20), so red inside it can
        // only have arrived through the brush.
        byte[] img = RenderContent(e, DrawRectangleRecord(11, 0, 30, 30, 60, 40));

        bool ok = true;
        ok &= Px(img, 60, 50, 255, 0, 0, "the cached target paints the filled area");
        ok &= Px(img, 100, 50, 255, 255, 255, "outside the fill is untouched");

        bool found = e.TryGetContentBrushForTest(11, out uint source, out bool isDrawing,
            out uint stretch, out TileMode tile);
        ok &= Bool(found && source == 5 && !isDrawing, $"it resolves to the hInternalTarget visual (got {source})");
        ok &= Bool(stretch == 1, $"the target is stretched to Fill the area (got {stretch})");
        ok &= Bool(tile == TileMode.None, $"it does not tile (got {tile})");

        // No target: must register nothing rather than a brush pointing at handle 0.
        var e2 = new MilcoreEngine();
        e2.SubmitCommand(BitmapCacheBrushCmd(12, opacity: 1.0, hInternalTarget: 0));
        ok &= Bool(!e2.TryGetContentBrushForTest(12, out _, out _, out _, out _),
            "a targetless BitmapCacheBrush registers nothing");
        return ok;
    }

    // 0x4e: PushOpacityMask brackets a run of draw records with a mask brush. The Scene layer
    // models masks per-VISUAL, so the enclosed records are collected into a nested visual that
    // carries the mask and draws in content order (NestedVisualDraw).
    //
    // The mask is an opaque->transparent horizontal gradient over a solid rect, so "the mask is
    // applied" is visible as a fade across the rect. Asserting only "something changed" would
    // pass even if the mask were applied inside out.
    private static bool OpacityMaskCase()
    {
        Console.WriteLine("MilPushOpacityMask (0x4e)");
        var e = new MilcoreEngine();
        e.CreateOrAddRef(1, MilResourceTypeId.Visual);
        e.SubmitCommand(SolidColorBrush(10, 0, 0, 0, 1));                    // opaque black fill
        // alpha 1 at the left edge -> alpha 0 at the right, across the rect's bounding box.
        e.SubmitCommand(LinearGradientAlphaBrush(30, 0, 0, 1, 0));

        byte[] masked = RenderContent(e, Concat(
            PushOpacityMaskRecord(30),
            DrawRectangleRecord(10, 0, 10, 10, 100, 40),
            PopRecord()));

        // The same rect with no mask, as the control for "fully painted".
        var e2 = new MilcoreEngine();
        e2.CreateOrAddRef(1, MilResourceTypeId.Visual);
        e2.SubmitCommand(SolidColorBrush(10, 0, 0, 0, 1));
        byte[] plain = RenderContent(e2, DrawRectangleRecord(10, 0, 10, 10, 100, 40));

        (int lr, int _, int _) = At(masked, 15, 30);
        (int rr, int _, int _) = At(masked, 105, 30);
        (int pr, int _, int _) = At(plain, 105, 30);
        Console.WriteLine($"    masked left={lr} right={rr}   unmasked right={pr}");

        bool ok = true;
        ok &= Bool(pr < 40, "the control rect is painted solid without a mask");
        ok &= Bool(lr < 60, $"the opaque end of the mask keeps the fill (got {lr})");
        ok &= Bool(rr > 200, $"the transparent end of the mask reveals the background (got {rr})");
        ok &= Bool(rr - lr > 120, "the mask actually gradates across the group, not on/off");
        return ok;
    }

    // The families that are deliberately NOT decoded must stay harmless. An unknown COMMAND is
    // ignored; an unknown render-data PUSH still has to keep the state stack balanced, or its
    // matching Pop unwinds a level that was never pushed and every later record loses its clip.
    private static bool UnknownCommandCase()
    {
        Console.WriteLine("unknown opcodes stay harmless");
        var e = new MilcoreEngine();
        e.CreateOrAddRef(1, MilResourceTypeId.Visual);
        e.SubmitCommand(SolidColorBrush(10, 0, 0, 1, 1));
        e.SubmitCommand(RectangleGeometry(20, 0, 0, 30, 30));
        // 0x14 Point3DResource: sent by IndependentAnimationStorage, referenced by nothing.
        var q = new Buf(); q.U32(0x14); q.U32(99); q.F32(1); q.F32(2); q.F32(3);
        e.SubmitCommand(q.ToArray());

        // An unknown push (0x55 PushEffect) wrapping a clip, then Pop, then a draw. If the
        // unknown push did not balance, the Pop would discard the clip's enclosing state.
        byte[] content = Concat(
            PushClipRecord(21),
            PushEffectRecord(),
            PopRecord(),
            DrawGeometryRecord(10, 0, 20),
            PopRecord());
        e.SubmitCommand(RectangleGeometry(21, 0, 0, 15, 30));   // clip: left half only
        byte[] img = RenderContent(e, content);

        bool ok = true;
        ok &= Px(img, 7, 15, 0, 0, 255, "inside the clip still draws");
        ok &= Px(img, 22, 15, 255, 255, 255, "the clip survived the unknown push/pop");
        return ok;
    }

    // ---- harness -----------------------------------------------------------------------

    private static SceneVisual Realize(MilcoreEngine e, byte[] content)
    {
        e.CreateOrAddRef(2, MilResourceTypeId.RenderData);
        e.BeginCommand(RenderDataHeader(2, content.Length));
        e.AppendCommandData(content);
        e.EndCommand();
        e.SubmitCommand(VisualSetContent(1, 2));
        e.Realize();
        SceneVisual? root = e.VisualByHandle(1);
        if (root is null) { Console.WriteLine("    [FAIL] no root visual"); _failures++; return new SceneVisual(); }
        return root;
    }

    private static byte[] RenderContent(MilcoreEngine e, byte[] content)
    {
        SceneVisual root = Realize(e, content);
        using var ctx = WgpuContext.Create();
        var renderer = new WgpuSceneRenderer(ctx);
        return renderer.RenderToRgba(root, W, H, RgbaColor.FromBytes(255, 255, 255, 255));
    }

    private static (int, int, int) At(byte[] img, int x, int y)
    {
        int i = (y * W + x) * 4;
        return (img[i], img[i + 1], img[i + 2]);
    }

    private static bool Px(byte[] img, int x, int y, int r, int g, int b, string what)
        => Near(At(img, x, y), r, g, b, 12, what);

    private static bool Near((int r, int g, int b) c, int r, int g, int b, int tol, string what)
    {
        bool ok = Math.Abs(c.r - r) <= tol && Math.Abs(c.g - g) <= tol && Math.Abs(c.b - b) <= tol;
        Console.WriteLine($"    [{(ok ? "ok " : "FAIL")}] {what}");
        if (!ok) _failures++;
        return ok;
    }

    private static bool Bool(bool ok, string what)
    {
        Console.WriteLine($"    [{(ok ? "ok " : "FAIL")}] {what}");
        if (!ok) _failures++;
        return ok;
    }

    private static bool Has(float[] set, float want, string what)
    {
        bool ok = Array.Exists(set, v => Math.Abs(v - want) < 0.01f);
        Console.WriteLine($"    [{(ok ? "ok " : "FAIL")}] {what} ({want})");
        if (!ok) _failures++;
        return ok;
    }

    // ---- command / record assembly (offsets from the generated struct layouts) -----------

    private static byte[] SolidColorBrush(uint h, float r, float g, float b, float a)
    {
        var q = new Buf(); q.U32(0x7e); q.U32(h); q.F64(1.0); q.F32(r); q.F32(g); q.F32(b); q.F32(a);
        q.U32(0); q.U32(0); q.U32(0); q.U32(0); return q.ToArray();
    }

    // MILCMD_RECTANGLEGEOMETRY: Handle@4, RadiusX@8, RadiusY@16, Rect@24, hTransform@56.
    private static byte[] RectangleGeometry(uint h, double x, double y, double w, double hh)
    {
        var q = new Buf(); q.U32(0x79); q.U32(h); q.F64(0); q.F64(0);
        q.F64(x); q.F64(y); q.F64(w); q.F64(hh); q.U32(0); return q.ToArray();
    }

    // MILCMD_GEOMETRYGROUP: Handle@4, hTransform@8, FillRule@12, ChildrenSize@16 (BYTES), children.
    private static byte[] GeometryGroup(uint h, uint fillRule, params uint[] children)
    {
        var q = new Buf(); q.U32(0x7b); q.U32(h); q.U32(0); q.U32(fillRule); q.U32((uint)(children.Length * 4));
        foreach (uint c in children) q.U32(c);
        return q.ToArray();
    }

    // MILCMD_COMBINEDGEOMETRY: Handle@4, hTransform@8, Mode@12, hGeometry1@16, hGeometry2@20.
    private static byte[] CombinedGeometry(uint h, uint mode, uint g1, uint g2)
    {
        var q = new Buf(); q.U32(0x7c); q.U32(h); q.U32(0); q.U32(mode); q.U32(g1); q.U32(g2); return q.ToArray();
    }

    // MILCMD_GUIDELINESET: Handle@4, XSize@8, YSize@12 (BYTES), IsDynamic@16, then DOUBLES.
    private static byte[] GuidelineSetCmd(uint h, double[] xs, double[] ys)
    {
        var q = new Buf(); q.U32(0x8c); q.U32(h); q.U32((uint)(xs.Length * 8)); q.U32((uint)(ys.Length * 8)); q.U32(0);
        foreach (double d in xs) q.F64(d);
        foreach (double d in ys) q.F64(d);
        return q.ToArray();
    }

    // MILCMD_GEOMETRYDRAWING: Handle@4, hBrush@8, hPen@12, hGeometry@16.
    private static byte[] GeometryDrawingCmd(uint h, uint hBrush, uint hPen, uint hGeometry)
    {
        var q = new Buf(); q.U32(0x87); q.U32(h); q.U32(hBrush); q.U32(hPen); q.U32(hGeometry); return q.ToArray();
    }

    // MILCMD_DRAWINGGROUP (0x8b): Handle@4, Opacity@8, ChildrenSize@16, hTransform@32; the
    // fixed struct runs to 52 (ClearTypeHint is the last field) and children follow.
    private static byte[] DrawingGroupCmd(uint h, double opacity, uint[] children)
    {
        var q = new Buf(); q.U32(0x8b); q.U32(h); q.F64(opacity); q.U32((uint)(children.Length * 4));
        while (q.Length < 52) q.U32(0);
        foreach (uint c in children) q.U32(c);
        return q.ToArray();
    }

    private static byte[] DrawGeometryRecord(uint hBrush, uint hPen, uint hGeometry)
    {
        var p = new Buf(); p.U32(hBrush); p.U32(hPen); p.U32(hGeometry); p.U32(0);
        return Record(0x46, p.ToArray());
    }

    // MILCMD_DRAW_RECTANGLE: rectangle@0 (4 doubles), hBrush@32, hPen@36 -- the handles come
    // AFTER the rect, unlike DrawGeometry where they come first.
    private static byte[] DrawRectangleRecord(uint hBrush, uint hPen, double x, double y, double w, double h)
    {
        var p = new Buf(); p.F64(x); p.F64(y); p.F64(w); p.F64(h); p.U32(hBrush); p.U32(hPen);
        return Record(0x40, p.ToArray());
    }

    // MILCMD_DRAW_RECTANGLE_ANIMATE: rectangle@0, hBrush@32, hPen@36, hRectangleAnimations@40, pad@44.
    private static byte[] DrawRectangleAnimateRecord(uint hBrush, uint hPen,
        double x, double y, double w, double h, uint hRectAnim)
    {
        var p = new Buf(); p.F64(x); p.F64(y); p.F64(w); p.F64(h);
        p.U32(hBrush); p.U32(hPen); p.U32(hRectAnim); p.U32(0);
        return Record(0x41, p.ToArray());
    }

    // MILCMD_RECTRESOURCE: Handle@4, Value@8 (four doubles).
    private static byte[] RectResourceCmd(uint h, double x, double y, double w, double hh)
    {
        var q = new Buf(); q.U32(0x11); q.U32(h); q.F64(x); q.F64(y); q.F64(w); q.F64(hh); return q.ToArray();
    }

    private static byte[] DrawDrawingRecord(uint hDrawing)
    {
        var p = new Buf(); p.U32(hDrawing); p.U32(0); return Record(0x4a, p.ToArray());
    }

    // MILCMD_BITMAPCACHEBRUSH: Handle@4, Opacity@8, hOpacityAnimations@16, hTransform@20,
    // hRelativeTransform@24, hBitmapCache@28, hInternalTarget@32.
    private static byte[] BitmapCacheBrushCmd(uint h, double opacity, uint hInternalTarget)
    {
        var q = new Buf(); q.U32(0x84); q.U32(h); q.F64(opacity);
        q.U32(0); q.U32(0); q.U32(0); q.U32(0); q.U32(hInternalTarget);
        return q.ToArray();
    }

    // MILCMD_PUSH_OPACITY_MASK: boundingBoxCacheLocalSpace@0 (4 floats), hOpacityMask@16, pad@20.
    private static byte[] PushOpacityMaskRecord(uint hMask)
    {
        var p = new Buf(); p.F32(0); p.F32(0); p.F32(0); p.F32(0); p.U32(hMask); p.U32(0);
        return Record(0x4e, p.ToArray());
    }

    // MILCMD_LINEARGRADIENTBRUSH, stop layout as MilDecodeTest drives it: offset (double)
    // then colour (4 floats), 24 bytes per stop. White opaque -> white transparent; only the
    // alpha matters for a mask.
    private static byte[] LinearGradientAlphaBrush(uint h, double x0, double y0, double x1, double y1)
    {
        var q = new Buf(); q.U32(0x7f); q.U32(h); q.F64(1.0);
        q.F64(x0); q.F64(y0); q.F64(x1); q.F64(y1);
        q.U32(0); q.U32(0); q.U32(0);       // hOpacityAnim, hTransform, hRelativeTransform
        q.U32(0);                           // ColorInterpolationMode
        q.U32(1);                           // MappingMode = RelativeToBoundingBox
        q.U32(0);                           // SpreadMethod = Pad
        q.U32(2 * 24);                      // GradientStopsSize (bytes)
        q.U32(0); q.U32(0);                 // hStartPointAnim, hEndPointAnim
        q.F64(0.0); q.F32(1); q.F32(1); q.F32(1); q.F32(1);
        q.F64(1.0); q.F32(1); q.F32(1); q.F32(1); q.F32(0);
        return q.ToArray();
    }

    private static byte[] PushEffectRecord() { var b = new Buf(); b.U32(8); b.U32(0x55); return b.ToArray(); }

    private static byte[] PushClipRecord(uint hClip)
    {
        var b = new Buf(); b.U32(16); b.U32(0x4d); b.U32(hClip); b.U32(0); return b.ToArray();
    }

    private static byte[] PushGuidelineSetRecord(uint h)
    {
        var p = new Buf(); p.U32(h); p.U32(0); return Record(0x52, p.ToArray());
    }

    private static byte[] PushGuidelineY1Record(double y)
    {
        var p = new Buf(); p.F64(y); return Record(0x53, p.ToArray());
    }

    private static byte[] PushGuidelineY2Record(double lead, double offset)
    {
        var p = new Buf(); p.F64(lead); p.F64(offset); return Record(0x54, p.ToArray());
    }

    private static byte[] PopRecord() { var b = new Buf(); b.U32(8); b.U32(0x56); return b.ToArray(); }

    private static byte[] Record(uint id, byte[] payload)
    {
        var b = new Buf(); b.U32((uint)(payload.Length + 8)); b.U32(id); b.Raw(payload); return b.ToArray();
    }

    private static byte[] RenderDataHeader(uint handle, int cbData)
    {
        var b = new Buf(); b.U32(0x18); b.U32(handle); b.U32((uint)cbData); return b.ToArray();
    }

    private static byte[] VisualInsertChildAt(uint parent, uint child, uint index)
    {
        var b = new Buf(); b.U32(0x26); b.U32(parent); b.U32(child); b.U32(index); return b.ToArray();
    }

    private static byte[] VisualSetContent(uint handle, uint hContent)
    {
        var b = new Buf(); b.U32(0x22); b.U32(handle); b.U32(hContent); return b.ToArray();
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (byte[] p in parts) all.AddRange(p);
        return all.ToArray();
    }

    private sealed class Buf
    {
        private readonly List<byte> _b = new();
        public int Length => _b.Count;
        public void U32(uint v) => _b.AddRange(BitConverter.GetBytes(v));
        public void F32(float v) => _b.AddRange(BitConverter.GetBytes(v));
        public void F64(double v) => _b.AddRange(BitConverter.GetBytes(v));
        public void Raw(byte[] v) => _b.AddRange(v);
        public byte[] ToArray() => _b.ToArray();
    }
}
