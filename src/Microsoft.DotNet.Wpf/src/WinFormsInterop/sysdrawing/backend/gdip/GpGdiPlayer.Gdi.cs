// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The vector half of GDI's playback into GDI+'s 32bpp DIB (see GpGdiPlayer.Raster.cs): what GDI
// does with a plain EMF's or WMF's lines, polygons, curves and paths, pixel for pixel. Points go to
// 28.4 device units through GDI's own float matrices (GdiXform), fills through win32k's edge-table
// scan converter (GdiFill), geometric pens through its widener (GdiWiden), clips are its regions
// (GdiRgn).
//
// The transforms are those PlayEnhMetaFile sets up (gdi32full bInternalPlayEMF @18005f2e8):
//
//   the target DC is put in GM_ADVANCED and MM_TEXT, its world transform the frame mapped onto the
//   rectangle: sx = (r - l) / (frame width) (r - l + 1 for an empty frame), likewise sy, then
//   m11 = ((mmX * 100) / devX) * sx, m22 = ((mmY * 100) / devY) * sy, dx = l - frame.left * sx,
//   dy = t - frame.top * sy, all in float; a scale within 0.999 .. 1.001 is made exactly 1;
//   the records' map modes, window and viewport and world transforms go to a second, virtual DC
//   (the metafile's own reference device); after each, MF::bSetTransform @18010a910 sets the
//   target's world transform to the virtual DC's world-to-device transform combined with that
//   frame mapping (NtGdiCombineTransform), except that ModifyWorldTransform's MWT_LEFTMULTIPLY is
//   applied to the target as it is (MRMODIFYWORLDTRANSFORM::bPlay @18006c540).
//
// A WMF plays on the DC as GDI+ sets it up (MM_ANISOTROPIC, the placeable box onto the
// destination), in GM_COMPATIBLE, where every point is snapped to a whole pixel
// (EXFORMOBJR::bXformRound).
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpGdiPlayer
    {
        // PlayEnhMetaFile's frame mapping, the XFORM the target's world transform starts as.
        float _b11 = 1, _b22 = 1, _bdx, _bdy;
        bool _wmfCanvas;              // a WMF: GM_COMPATIBLE, no world transform
        GdiPath _gPath;               // the path bracket, in 28.4 device units

        /// <summary>GDI draws this record's vectors (into the DIB) rather than Graphics.</summary>
        bool Gdi => Canvas;

        // ---- the transforms ---------------------------------------------------------------------

        /// <summary>bInternalPlayEMF's frame mapping onto (0, 0, w - 1, h - 1).</summary>
        void GdiBeginEmf(byte[] h)
        {
            int fl = Le.I32(h, 24), ft = Le.I32(h, 28), fr = Le.I32(h, 32), fb = Le.I32(h, 36);
            int devX = Le.I32(h, 72), devY = Le.I32(h, 76), mmX = Le.I32(h, 80), mmY = Le.I32(h, 84);
            int rl = 0, rt = 0, rr = _cw - 1, rb = _ch - 1;
            int rw = rr - rl, rh = rb - rt;
            float sx = fr == fl ? (float)(rw + 1) : (float)rw / (float)(fr - fl);
            float sy = fb == ft ? (float)(rh + 1) : (float)rh / (float)(fb - ft);
            float m11 = (((float)mmX * 100f) / (float)devX) * sx;
            float m22 = (((float)mmY * 100f) / (float)devY) * sy;
            float dx = (float)rl - (float)fl * sx;
            float dy = (float)rt - (float)ft * sy;
            // The current world transform (identity) times that: 0 * dy + 1 * dx + 0.
            dx = 0f * dy + 1f * dx + 0f;
            dy = 1f * dy + 0f * dx + 0f;
            const float lo = 0.999f, hi = 1.001f;   // 0x3f7fbe77, 0x3f8020c5
            if (lo <= m11 && m11 <= hi && lo <= m22 && m22 <= hi) { m11 = 1f; m22 = 1f; }
            _b11 = m11; _b22 = m22; _bdx = dx; _bdy = dy;
            SetTargetWorld(m11, 0, 0, m22, dx, dy);
            _dc.VWorld = GdiXform.Identity;
            _dc.VWorldIdentity = true;
        }

        void GdiBeginWmf()
        {
            _wmfCanvas = true;
            _dc.GWorld = GdiXform.Identity;
            _dc.GWorldIdentity = true;
        }

        static bool IsIdentity(float m11, float m12, float m21, float m22, float dx, float dy)
            => m11 == 1 && m12 == 0 && m21 == 0 && m22 == 1 && dx == 0 && dy == 0;

        /// <summary>SetWorldTransform on the target (XDCOBJ::bModifyWorldTransform, MWT_SET).</summary>
        void SetTargetWorld(float m11, float m12, float m21, float m22, float dx, float dy)
        {
            if (IsIdentity(m11, m12, m21, m22, dx, dy)) { _dc.GWorld = GdiXform.Identity; _dc.GWorldIdentity = true; return; }
            _dc.GWorld = GdiXform.FromXform(m11, m12, m21, m22, dx, dy);
            _dc.GWorldIdentity = false;
        }

        /// <summary>XDCOBJ::bModifyWorldTransform @140231c10 on a world transform.</summary>
        static void ModifyWorld(ref GdiXform world, ref bool identity, float[] e, int mode)
        {
            switch (mode)
            {
                case 1:
                    world = GdiXform.Identity; identity = true;
                    return;
                case 2:
                case 3:
                    {
                        GdiXform m = GdiXform.FromXform(e[0], e[1], e[2], e[3], e[4], e[5]);
                        if (!identity) m = mode == 2 ? GdiXform.Multiply(m, world) : GdiXform.Multiply(world, m);
                        world = m;
                        identity = world.M11 == 1 && world.M12 == 0 && world.M21 == 0 && world.M22 == 1 && world.Dx == 0 && world.Dy == 0
                            && world.FxDx == 0 && world.FxDy == 0;
                        if (identity) world = GdiXform.Identity;
                        return;
                    }
                case 4:
                    if (IsIdentity(e[0], e[1], e[2], e[3], e[4], e[5])) { world = GdiXform.Identity; identity = true; return; }
                    world = GdiXform.FromXform(e[0], e[1], e[2], e[3], e[4], e[5]);
                    identity = false;
                    return;
            }
        }

        /// <summary>A DC's page transform (DC::vUpdateWtoDXform): the scale (16 when the window and
        /// viewport extents agree) and the offset in FIX, float and rounded.</summary>
        static void PageXform(int mapMode, Point winOrg, Size winExt, Point vpOrg, Size vpExt,
            out float m11, out float m22, out float dx, out float dy, out int fxDx, out int fxDy, out bool sixteen)
        {
            if (mapMode == 1 || (winExt.Width == vpExt.Width && winExt.Height == vpExt.Height))
            {
                m11 = m22 = 16f;
                sixteen = true;
            }
            else
            {
                m11 = (float)(vpExt.Width << 4) / (float)winExt.Width;
                m22 = (float)(vpExt.Height << 4) / (float)winExt.Height;
                sixteen = false;
            }
            if (winOrg.X == 0 && winOrg.Y == 0)
            {
                if (vpOrg.X == 0 && vpOrg.Y == 0) { dx = dy = 0; fxDx = fxDy = 0; return; }
                dx = (float)(vpOrg.X << 4); dy = (float)(vpOrg.Y << 4);
                fxDx = vpOrg.X << 4; fxDy = vpOrg.Y << 4;
                return;
            }
            if (sixteen)
            {
                dx = (float)(winOrg.X * -16); dy = (float)(winOrg.Y * -16);
                if (vpOrg.X == 0 && vpOrg.Y == 0) { fxDx = winOrg.X * -16; fxDy = winOrg.Y * -16; return; }
            }
            else
            {
                dx = m11 * (float)(-winOrg.X);
                dy = (float)(-winOrg.Y) * m22;
            }
            if (vpOrg.X != 0 || vpOrg.Y != 0)
            {
                dx = (float)(vpOrg.X << 4) + dx;
                dy = (float)(vpOrg.Y << 4) + dy;
            }
            fxDx = GdiXform.FToL(dx); fxDy = GdiXform.FToL(dy);
        }

        /// <summary>A DC's world-to-device matrix from its world transform and page.</summary>
        static GdiXform WtoD(in GdiXform world, bool worldIdentity, int mapMode, Point winOrg, Size winExt, Point vpOrg, Size vpExt)
        {
            PageXform(mapMode, winOrg, winExt, vpOrg, vpExt, out float m11, out float m22, out float dx, out float dy, out int fxDx, out int fxDy, out bool sixteen);
            if (worldIdentity)
            {
                var m = new GdiXform { M11 = m11, M22 = m22, Dx = dx, Dy = dy, FxDx = fxDx, FxDy = fxDy };
                m.Accel = sixteen ? 0xb : 9;
                if (fxDx == 0 && fxDy == 0) m.Accel |= GdiXform.NoTranslation;
                return m;
            }
            return GdiXform.WorldToDevice(world, m11, m22, dx, dy, sixteen);
        }

        /// <summary>The target's world-to-device matrix (EMF: MM_TEXT at the origin; WMF: the
        /// player's page).</summary>
        GdiXform TargetWtoD()
        {
            if (_wmfCanvas) return WtoD(GdiXform.Identity, true, _dc.MapMode, _dc.WinOrg, _dc.WinExt, _dc.VpOrg, _dc.VpExt);
            return WtoD(_dc.GWorld, _dc.GWorldIdentity, 1, Point.Empty, new Size(1, 1), Point.Empty, new Size(1, 1));
        }

        /// <summary>MF::bSetTransform: the target's world transform the virtual DC's world-to-device
        /// transform (NtGdiGetTransform 0x204, in logical units) combined with the frame mapping.</summary>
        void GdiSetTransform()
        {
            if (_wmfCanvas) return;
            GdiXform v = WtoD(_dc.VWorld, _dc.VWorldIdentity, _dc.MapMode, _dc.WinOrg, VirtualWinExt(), _dc.VpOrg, VirtualVpExt());
            const float k = 0.0625f;
            GdiXform a = GdiXform.FromXform(v.M11 * k, v.M12 * k, v.M21 * k, v.M22 * k, v.Dx * k, v.Dy * k);
            GdiXform b = GdiXform.FromXform(_b11, 0, 0, _b22, _bdx, _bdy);
            GdiXform c = GdiXform.Multiply(a, b);
            SetTargetWorld(c.M11, c.M12, c.M21, c.M22, c.Dx, c.Dy);
        }

        // The virtual DC's extents (DC::iSetMapMode with the metafile's virtual resolution).
        Size VirtualWinExt()
        {
            switch (_dc.MapMode)
            {
                case 2: return new Size(_mmCx * 10, _mmCy * 10);
                case 3: return new Size(_mmCx * 100, _mmCy * 100);
                case 4: return new Size(MulDiv(_mmCx, 1000, 254), MulDiv(_mmCy, 1000, 254));
                case 5: return new Size(MulDiv(_mmCx, 10000, 254), MulDiv(_mmCy, 10000, 254));
                case 6: return new Size(MulDiv(_mmCx, 14400, 254), MulDiv(_mmCy, 14400, 254));
                case 1: return new Size(1, 1);
            }
            return _dc.WinExt;
        }

        Size VirtualVpExt()
        {
            if (_dc.MapMode >= 2 && _dc.MapMode <= 6) return new Size(_devCx, -_devCy);
            if (_dc.MapMode == 1) return new Size(1, 1);
            return _dc.VpExt;
        }

        static int MulDiv(int a, int b, int c) => (int)Math.Round((double)a * b / c, MidpointRounding.AwayFromZero);

        /// <summary>A logical point in 28.4 device units (snapped to pixels in GM_COMPATIBLE).</summary>
        void Fix(in GdiXform m, float x, float y, out int fx, out int fy)
        {
            m.Point((int)x, (int)y, out fx, out fy);
            if (_wmfCanvas) { fx = (fx + 8) & ~15; fy = (fy + 8) & ~15; }
        }

        // ---- paths --------------------------------------------------------------------------------

        /// <summary>Points (logical) as a path figure in device units; <paramref name="kind"/>: 0
        /// lines, 1 Beziers after the first point.</summary>
        void GdiAddPoly(GdiPath path, PointF[] pts, bool beziers, bool fromCurrent, bool close)
        {
            GdiXform m = TargetWtoD();
            int n = pts.Length;
            int i0 = 0;
            if (fromCurrent)
            {
                CurrentFix(m, out int cx, out int cy);
                if (path.Empty || path.Figures[path.Figures.Count - 1].Closed || _gMoved) path.MoveTo(cx, cy);
            }
            else
            {
                Fix(m, pts[0].X, pts[0].Y, out int x0, out int y0);
                path.MoveTo(x0, y0);
                i0 = 1;
            }
            _gMoved = false;
            if (beziers)
            {
                for (int i = i0; i + 2 < n; i += 3)
                {
                    Fix(m, pts[i].X, pts[i].Y, out int ax, out int ay);
                    Fix(m, pts[i + 1].X, pts[i + 1].Y, out int bx, out int by);
                    Fix(m, pts[i + 2].X, pts[i + 2].Y, out int cx, out int cy);
                    path.BezierTo(ax, ay, bx, by, cx, cy);
                }
            }
            else
                for (int i = i0; i < n; i++)
                {
                    Fix(m, pts[i].X, pts[i].Y, out int x, out int y);
                    path.LineTo(x, y);
                }
            // A figure drawn from the current point, outside a path bracket, continues the DC's
            // style position (the path GreLineTo / PolylineTo builds starts without PD_RESETSTYLE);
            // any other starts it over.
            if (fromCurrent && !_inPath && path.Figures.Count == 1) path.Figures[0].ResetStyle = false;
            if (close) path.CloseFigure();
        }

        bool _gMoved;                 // a MoveTo since the path's last point

        // An ArcTo leaves the current position in device units (ptfxCurrent, the logical one
        // invalidated): kept here while the logical position is the one it was set with.
        PointF? _gPosAt;
        int _gPosFx, _gPosFy;

        void CurrentFix(in GdiXform m, out int fx, out int fy)
        {
            if (_gPosAt.HasValue && _gPosAt.Value == _dc.Pos) { fx = _gPosFx; fy = _gPosFy; return; }
            Fix(m, _dc.Pos.X, _dc.Pos.Y, out fx, out fy);
        }

        void SetCurrentFix(int fx, int fy)
        {
            GdiXform m = TargetWtoD();
            if (m.Inverse(out GdiXform inv))
            {
                inv.Point2(fx, fy, out int lx, out int ly);
                _dc.Pos = new PointF(lx, ly);
            }
            _gPosAt = _dc.Pos; _gPosFx = fx; _gPosFy = fy;
        }

        /// <summary>Arc (0), ArcTo (1), Chord (2), Pie (3) as NtGdiArcInternal @1401a2660 draws
        /// them: the EBOX (a PS_INSIDEFRAME pen wider than the box draws nothing), the arc's path
        /// (GdiArc), stroked, or for a chord or pie filled and stroked.</summary>
        void GdiArcShape(RectangleF rc, int xs, int ys, int xe, int ye, int kind)
        {
            GdiPen pen = DrawPen();
            int style = pen == null ? 5 : pen.Style & 0xf;
            int gw = pen != null && pen.Geometric ? pen.Width : 0;
            GdiXform m = TargetWtoD();
            GdiBox e = GdiBox.Make((int)rc.Left, (int)rc.Top, (int)rc.Right, (int)rc.Bottom, m, !_wmfCanvas, true, style, gw, _dc.ArcDirection == 2);
            if (e.FillInsideFrame || e.Empty) return;
            GdiPath p;
            if (kind == 1)
            {
                p = _inPath ? (_gPath ??= new GdiPath()) : new GdiPath();
                CurrentFix(m, out int cx, out int cy);
                if (p.Empty || p.Figures[p.Figures.Count - 1].Closed || _gMoved || !_inPath) p.MoveTo(cx, cy);
                _gMoved = false;
            }
            else p = new GdiPath();
            GdiArc.Build(p, e, kind, xs, ys, xe, ye);
            if (kind == 1 && !_inPath) p.Figures[0].ResetStyle = false;
            if (kind == 1)
            {
                SetCurrentFix(p.CurrentX, p.CurrentY);
                if (_inPath) return;
                GdiStroke(p);
                return;
            }
            if (kind == 0) { if (_inPath) { _gPath ??= new GdiPath(); _gPath.Append(p); } else GdiStroke(p); return; }
            GdiFillAndStroke(p, true, true);
        }

        /// <summary>AngleArc as GrepAngleArc @140214718 draws it: the circle's box (ordered, its
        /// top and bottom exchanged and the angles negated for a negative sweep) through
        /// EBOX(EXFORMOBJR, RECTL), a line from the current point, the sweep; stroked.</summary>
        void GdiAngleArc(int x, int y, int radius, float start, float sweep)
        {
            if (radius < 0) return;
            long l = (long)x - radius, t = (long)y - radius, r = (long)x + radius, b = (long)y + radius;
            if (l < int.MinValue || t < int.MinValue || r > int.MaxValue || b > int.MaxValue) return;
            int il = (int)l, it = (int)t, ir = (int)r, ib = (int)b;
            if (sweep < 0) { start = -start; sweep = -sweep; (it, ib) = (ib, it); }
            GdiXform m = TargetWtoD();
            GdiBox e = GdiBox.MakeExact(il, it, ir, ib, m, !_wmfCanvas);
            GdiPath p = _inPath ? (_gPath ??= new GdiPath()) : new GdiPath();
            CurrentFix(m, out int fx, out int fy);
            if (p.Empty || p.Figures[p.Figures.Count - 1].Closed || _gMoved || !_inPath) p.MoveTo(fx, fy);
            _gMoved = false;
            GdiArc.Angle(p, e, start, sweep);
            if (!_inPath) p.Figures[0].ResetStyle = false;
            SetCurrentFix(p.CurrentX, p.CurrentY);
            if (!_inPath) GdiStroke(p);
        }

        void GdiMoveTo()
        {
            _gMoved = true;
            _dc.StyleState = 0;   // MoveToEx: DIRTY_STYLESTATE
            _gPosAt = null;
            if (_inPath && _gPath != null)
            {
                GdiXform m = TargetWtoD();
                Fix(m, _dc.Pos.X, _dc.Pos.Y, out int x, out int y);
                _gPath.MoveTo(x, y);
            }
        }

        // ---- drawing -------------------------------------------------------------------------------

        /// <summary>A shape's path filled with the brush and outlined with the pen (or added to the
        /// path bracket).</summary>
        void GdiFillAndStroke(GdiPath p, bool fill, bool stroke)
        {
            if (_inPath) { _gPath ??= new GdiPath(); _gPath.Append(p); return; }
            if (fill && _dc.Brush != null && _dc.Brush.Style != 1)
            {
                // EPATHOBJ_bSimpleStrokeAndFill @140168450: the fill flattens the path in place, so
                // the pen then widens the flattened figures (their joins, not the curves').
                var f = new GdiPath();
                foreach (GdiPath.Figure fig in p.Figures)
                {
                    var c = new GdiPath.Figure { Closed = true };
                    c.X.AddRange(fig.X); c.Y.AddRange(fig.Y); c.Bezier.AddRange(fig.Bezier);
                    f.Figures.Add(c);
                }
                GdiFillPath(f, _dc.PolyFill == 2);
                p = p.Flattened();
            }
            if (stroke) GdiStroke(p);
        }

        /// <summary>Rectangle (0), Ellipse (1), RoundRect (2) as NtGdiRectangle/NtGdiEllipse/
        /// NtGdiRoundRect build them: the EBOX, its path, then the fill and the outline; a
        /// PS_INSIDEFRAME pen wider than the box fills the shape with the pen instead.</summary>
        void GdiShape(RectangleF rc, int kind, int cx, int cy)
        {
            if (kind == 2 && (cx == 0 || cy == 0)) kind = 0;   // NtGdiRoundRect @140217b30: a Rectangle
            GdiPen pen = DrawPen();
            int style = pen == null ? 5 : pen.Style & 0xf;
            int gw = pen != null && pen.Geometric ? pen.Width : 0;
            GdiXform m = TargetWtoD();
            int l = (int)rc.Left, t = (int)rc.Top, r = (int)rc.Right, b = (int)rc.Bottom;
            if (kind == 0 && !_inPath && style != 5 && gw == 0 && (m.Accel & GdiXform.Scale) != 0)
            {
                GdiRectangleCosmetic(m, l, t, r, b);
                return;
            }
            GdiBox e = GdiBox.Make(l, t, r, b, m, !_wmfCanvas, kind != 0, style, gw, _dc.ArcDirection == 2);
            if (e.Empty) return;
            var p = new GdiPath();
            switch (kind)
            {
                case 0: e.Rectangle(p); break;
                case 1: e.Ellipse(p); break;
                default: e.RoundRect(p, cx, cy); break;
            }
            if (e.FillInsideFrame && !_inPath)
            {
                if (Nop) return;
                uint color = Rgb(pen.Color);
                GdiPaint(GdiFill.Spans(p, _dc.PolyFill == 2, new[] { 0, 0, _cw, _ch }), (x, y) => color);
                return;
            }
            if (kind == 0 && pen != null && pen.OldGeometric && !_inPath)
            {
                // GrepRectangle: an old pen strokes the box with mitred corners.
                GdiFillAndStroke(p, true, false);
                GdiStroke(p, pen, 2);
                return;
            }
            GdiFillAndStroke(p, true, true);
        }

        /// <summary>GrepRectangle @1402158e0 for a cosmetic pen under a scale-only transform: the
        /// corners in whole pixels (GM_ADVANCED rounds up; GM_COMPATIBLE to nearest, the far edges
        /// one in), the interior blitted inside the frame, the frame a cosmetic rectangle on the
        /// pixel grid (RECTANGLEPATHOBJ::vInit @140169a68).</summary>
        void GdiRectangleCosmetic(in GdiXform m, int l, int t, int r, int b)
        {
            m.Point(l, t, out int x0, out int y0);
            m.Point(r, b, out int x1, out int y1);
            if (!_wmfCanvas)
            {
                x0 = (x0 + 15) >> 4; y0 = (y0 + 15) >> 4; x1 = (x1 + 15) >> 4; y1 = (y1 + 15) >> 4;
                if (x1 < x0) (x0, x1) = (x1, x0);
                if (y1 < y0) (y0, y1) = (y1, y0);
            }
            else
            {
                x0 = ((x0 >> 3) + 1) >> 1; y0 = ((y0 >> 3) + 1) >> 1; x1 = ((x1 >> 3) + 1) >> 1; y1 = ((y1 >> 3) + 1) >> 1;
                if (x1 < x0) (x0, x1) = (x1, x0);
                if (y1 < y0) (y0, y1) = (y1, y0);
                x1--; y1--;
                if (x1 < x0 || y1 < y0) return;
            }
            var p = new GdiPath();
            int ya = y0, yb = y1;
            if (_dc.ArcDirection == 2) (ya, yb) = (yb, ya);   // DCPATH_CLOCKWISE
            p.MoveTo(x1 << 4, ya << 4);
            p.LineTo(x0 << 4, ya << 4);
            p.LineTo(x0 << 4, yb << 4);
            p.LineTo(x1 << 4, yb << 4);
            p.CloseFigure();
            if (_dc.Brush != null && _dc.Brush.Style != 1 && x0 + 1 < x1 && y0 + 1 < y1 && !Nop)
            {
                Func<int, int, uint> pattern = PatternOf();
                if (pattern != null)
                {
                    var spans = new List<GdiSpan>();
                    for (int y = y0 + 1; y < y1; y++) spans.Add(new GdiSpan(y, x0 + 1, x1));
                    GdiPaint(spans, pattern);
                }
            }
            GdiStroke(p);
        }

        void GdiFillPath(GdiPath p, bool winding)
        {
            if (Nop || _dc.Brush == null || _dc.Brush.Style == 1) return;
            Func<int, int, uint> pattern = PatternOf();
            if (pattern == null) return;
            GdiPaint(GdiFill.Spans(p, winding, new[] { 0, 0, _cw, _ch }), pattern);
        }

        /// <summary>The DC's pen as a draw sees it. A CreatePen pen is realized against the current
        /// transform (DC::vRealizeLineAttrs @14007a158): cosmetic when it is zero wide, under two
        /// units at an identity transform, or nominal (DC::bOldPenNominal @140079c30: its width as
        /// a device vector under 1.5 pixels in x; in GM_ADVANCED both axes' vectors, each under
        /// 1.5 pixels on either axis and in length); otherwise geometric with no style, round caps
        /// and joins.</summary>
        GdiPen DrawPen()
        {
            GdiPen pen = _dc.Pen;
            if (pen == null || !pen.Old || pen.Null || pen.Geometric) return pen;
            GdiXform m = TargetWtoD();
            int w = pen.Width;
            bool cosmetic;
            if (w == 0 || ((m.Accel & 0x43) == 0x43 && w < 2)) cosmetic = true;
            else if (_wmfCanvas || _dc.GWorldIdentity)
            {
                // no WORLD_TRANSFORM_SET: the x axis alone
                m.Vector(w, 0, out int vx, out _);
                cosmetic = Math.Abs(vx) < 0x18;
            }
            else
            {
                m.Vector(w, 0, out int ax, out int ay);
                m.Vector(0, w, out int bx, out int by);
                cosmetic = Math.Max(Math.Abs(ax), Math.Abs(ay)) < 0x18 && Math.Max(Math.Abs(bx), Math.Abs(by)) < 0x18
                    && ax * ax + ay * ay < 0x240 && bx * bx + by * by < 0x240;
            }
            if (cosmetic) return pen;
            return new GdiPen { Style = 0x10000 | ((pen.Style & 0xf) == 6 ? 6 : 0), Width = w, Color = pen.Color, BrushStyle = pen.BrushStyle, PatternBrush = pen.PatternBrush, OldGeometric = true };
        }

        void GdiStroke(GdiPath p) => GdiStroke(p, DrawPen(), -1);

        void GdiStroke(GdiPath p, GdiPen pen, int join)
        {
            if (pen == null || pen.Null || Nop) return;
            // DC::vRealizeLineAttrs, run when a draw finds another pen selected than the last
            // one drawn with, starts the style position over.
            if (!ReferenceEquals(_dc.StylePen, _dc.Pen)) { _dc.StyleState = 0; _dc.StylePen = _dc.Pen; }
            GdiXform m = TargetWtoD();
            if (!GeometricLineAttrs(pen, out GdiLineAttrs la))
            {
                GdiCosmetic(p, Rgb(pen.Color));
                return;
            }
            if (join >= 0) la.Join = join;
            GdiPath wide = GdiWiden.Widen(p, m, la);
            if (wide == null) return;
            uint color = Rgb(pen.Color);
            GdiPaint(GdiFill.Spans(wide, true, new[] { 0, 0, _cw, _ch }), (x, y) => color);
        }

        /// <summary>The pen as GDI realises it for a stroke: geometric (widened) or not.</summary>
        bool GeometricLineAttrs(GdiPen pen, out GdiLineAttrs la)
        {
            la = default;
            if (!pen.Geometric) return false;
            la.Width = pen.Width;
            la.MiterLimit = _dc.MiterLimit;
            switch (pen.Style & 0xf00)
            {
                case 0x000: la.EndCap = 0; break;
                case 0x100: la.EndCap = 1; break;
                default: la.EndCap = 2; break;
            }
            switch (pen.Style & 0xf000)
            {
                case 0x0000: la.Join = 0; break;
                case 0x1000: la.Join = 1; break;
                default: la.Join = 2; break;
            }
            la.Style = GeometricStyle(pen, la.EndCap);
            return true;
        }

        static GraphicsPath ToGraphicsPath(GdiPath p)
        {
            var gp = new GraphicsPath(FillMode.Winding);
            foreach (GdiPath.Figure f in p.Figures)
            {
                gp.StartFigure();
                for (int i = 1; i < f.Count; i++)
                {
                    if (f.Bezier[i] && i + 2 < f.Count)
                    {
                        gp.AddBezier(f.X[i - 1] / 16f, f.Y[i - 1] / 16f, f.X[i] / 16f, f.Y[i] / 16f, f.X[i + 1] / 16f, f.Y[i + 1] / 16f, f.X[i + 2] / 16f, f.Y[i + 2] / 16f);
                        i += 2;
                    }
                    else gp.AddLine(f.X[i - 1] / 16f, f.Y[i - 1] / 16f, f.X[i] / 16f, f.Y[i] / 16f);
                }
                if (f.Closed) gp.CloseFigure();
            }
            return gp;
        }

        /// <summary>Spans painted with the brush (a colour or a pattern in device pixels) through the
        /// clip, by the ROP2.</summary>
        void GdiPaint(List<GdiSpan> spans, Func<int, int, uint> pattern)
        {
            if (spans.Count == 0) return;
            int code = _dc.Rop2;
            if (code != 13) _ropUsed = true;
            GdiRgn clip = GdiClip();
            BitmapData bd = _canvas.LockBits(new Rectangle(0, 0, _cw, _ch), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    byte* base0 = (byte*)bd.Scan0;
                    foreach (GdiSpan s in spans)
                    {
                        if (s.Y < 0 || s.Y >= _ch) continue;
                        uint* row = (uint*)(base0 + s.Y * bd.Stride);
                        int[] cx = clip?.Row(s.Y);
                        if (clip != null && cx == null) continue;
                        if (clip == null) PaintRun(row, s.Y, Math.Max(0, s.X0), Math.Min(_cw, s.X1), pattern, code);
                        else
                            for (int i = 0; i < cx.Length; i += 2)
                            {
                                int a = Math.Max(Math.Max(0, s.X0), cx[i]), b = Math.Min(Math.Min(_cw, s.X1), cx[i + 1]);
                                if (a < b) PaintRun(row, s.Y, a, b, pattern, code);
                            }
                    }
                }
            }
            finally { _canvas.UnlockBits(bd); }
        }

        static unsafe void PaintRun(uint* row, int y, int x0, int x1, Func<int, int, uint> pattern, int code)
        {
            for (int x = x0; x < x1; x++)
            {
                uint p = pattern(x, y) & 0xffffff;
                row[x] = code == 13 ? (row[x] & 0xff000000) | p : (row[x] & 0xff000000) | (Rop2(code, p, row[x]) & 0xffffff);
            }
        }

        /// <summary>The DC's clip region under its meta region (null: nothing clips).</summary>
        GdiRgn GdiClip()
        {
            GdiRgn c = _dc.GClip, m = _dc.GMeta;
            if (c == null) return m;
            if (m == null) return c;
            return GdiRgn.Combine(c, m, 1);
        }

        void GdiSelectRgn(GdiRgn rg, int mode)
        {
            if (mode == 5) { _dc.GClip = rg; return; }
            GdiCombine(rg, mode);
        }

        /// <summary>The GDI clip as a Graphics Region (for what Graphics still draws: text, images).</summary>
        Region GdiClipRegion()
        {
            GdiRgn c = GdiClip();
            if (c == null) return null;
            var r = new Region();
            r.MakeEmpty();
            foreach (var q in c.Rects())
            {
                int l = Math.Max(q.L, -1), t = Math.Max(q.T, -1), rr = Math.Min(q.R, _cw + 1), b = Math.Min(q.B, _ch + 1);
                if (l < rr && t < b) r.Union(new Rectangle(l, t, rr - l, b - t));
            }
            return r;
        }

        // ---- clipping and regions ----------------------------------------------------------------

        static readonly int Inf = 1 << 27;

        /// <summary>The surface (vGet_sizlWindow): what a clip combines with when the DC has none.</summary>
        GdiRgn Surface => GdiRgn.FromRect(0, 0, _cw, _ch);

        /// <summary>DC::iCombine with an existing clip or, without one, RGN_AND the shape itself and
        /// any other mode the surface combined with it.</summary>
        void GdiCombine(GdiRgn shape, int mode)
        {
            if (_dc.GClip == null)
            {
                _dc.GClip = mode == 1 ? shape : GdiRgn.Combine(Surface, shape, mode);
                return;
            }
            _dc.GClip = GdiRgn.Combine(_dc.GClip, shape, mode);
        }

        /// <summary>GreIntersectClipRect / GreExcludeClipRect @1400a4500: a scale-only transform maps
        /// the corners (bCvtPts1, rounded to pixels) and the rectangle is ordered; otherwise the four
        /// corners are a path (snapped to pixels) filled ALTERNATE.</summary>
        void GdiClipRect(int l, int t, int r, int b, bool exclude)
        {
            GdiXform m = TargetWtoD();
            int mode = exclude ? 4 : 1;
            if ((m.Accel & GdiXform.Scale) != 0)
            {
                int x0 = l, y0 = t, x1 = r, y1 = b;
                if ((m.Accel & 0x43) != 0x43)
                {
                    m.Point(l, t, out int fx0, out int fy0);
                    m.Point(r, b, out int fx1, out int fy1);
                    x0 = ((fx0 >> 3) + 1) >> 1; y0 = ((fy0 >> 3) + 1) >> 1;
                    x1 = ((fx1 >> 3) + 1) >> 1; y1 = ((fy1 >> 3) + 1) >> 1;
                }
                if (x1 < x0) (x0, x1) = (x1, x0);
                if (y1 < y0) (y0, y1) = (y1, y0);
                GdiCombine(GdiRgn.FromRect(x0, y0, x1, y1), mode);
                return;
            }
            var p = new GdiPath();
            void P(int x, int y, out int fx, out int fy) { m.Point(x, y, out fx, out fy); fx = (fx + 8) & ~15; fy = (fy + 8) & ~15; }
            P(l, t, out int ax, out int ay); P(r, t, out int bx, out int by); P(r, b, out int cx, out int cy); P(l, b, out int dx, out int dy);
            p.MoveTo(ax, ay); p.LineTo(bx, by); p.LineTo(cx, cy); p.LineTo(dx, dy); p.CloseFigure();
            GdiCombine(GdiRgn.FromSpans(GdiFill.Spans(p, false)), mode);
        }

        /// <summary>OffsetClipRgn: the offset through the transform's scale (logical to device).</summary>
        void GdiOffsetClip(int dx, int dy)
        {
            if (_dc.GClip == null) return;
            GdiXform m = TargetWtoD();
            m.Vector(dx, dy, out int fx, out int fy);
            _dc.GClip = _dc.GClip.Offset((fx + 8) >> 4, (fy + 8) >> 4);
        }

        /// <summary>An RGNDATA's rectangles.</summary>
        static List<(int L, int T, int R, int B)> RgnRects(GpReader r, int size)
        {
            var list = new List<(int, int, int, int)>();
            if (size < 32) return list;
            r.I32(); r.I32();
            int count = r.I32();
            r.I32();
            r.Rect();
            for (int i = 0; i < count && r.Ok; i++)
            {
                int l = r.I32(), t = r.I32(), rr = r.I32(), b = r.I32();
                list.Add((l, t, rr, b));
            }
            return list;
        }

        /// <summary>Rectangles through a matrix as a region: kept as they are under a unity scale with
        /// no translation, otherwise their outline (RGNOBJ::bOutline) transformed, optionally snapped
        /// to pixels, and filled ALTERNATE (RGNMEMOBJ::vCreate).</summary>
        static GdiRgn RectsThrough(List<(int L, int T, int R, int B)> rects, in GdiXform m, bool round)
        {
            if ((m.Accel & 0x43) == 0x43) return GdiRgn.FromRects(rects);
            var p = new GdiPath();
            GdiXform mm = m;
            void P(int x, int y, out int fx, out int fy)
            {
                mm.Point(x, y, out fx, out fy);
                if (round) { fx = (fx + 8) & ~15; fy = (fy + 8) & ~15; }
            }
            foreach (var q in rects)
            {
                P(q.L, q.T, out int ax, out int ay); P(q.R, q.T, out int bx, out int by);
                P(q.R, q.B, out int cx, out int cy); P(q.L, q.B, out int dx, out int dy);
                p.MoveTo(ax, ay); p.LineTo(bx, by); p.LineTo(cx, cy); p.LineTo(dx, dy); p.CloseFigure();
            }
            return GdiRgn.FromSpans(GdiFill.Spans(p, false));
        }

        GdiXform BaseLToFx()
        {
            GdiXform w = GdiXform.FromXform(_b11, 0, 0, _b22, _bdx, _bdy);
            return GdiXform.WorldToDevice(w, 16, 16, 0, 0, true);
        }

        /// <summary>MREXTSELECTCLIPRGN::bPlay @18006b730: the region made through the frame mapping
        /// (ExtCreateRegion, GreExtCreateRegion @1400a3b10, snapped to pixels), then selected.</summary>
        void GdiExtSelectClipRgn(GpReader r, int cb, int mode)
        {
            if (cb == 0 || cb < 32)
            {
                if (mode == 5) _dc.GClip = null;
                return;
            }
            GdiRgn rg = _wmfCanvas ? GdiRgn.FromRects(RgnRects(r, cb)) : RectsThrough(RgnRects(r, cb), BaseLToFx(), true);
            if (mode == 5) { _dc.GClip = rg; return; }
            GdiCombine(rg, mode);
        }

        /// <summary>GreFillRgn @14018e9c8: the region in logical units, through the world-to-device
        /// transform when that is not the identity.</summary>
        void GdiFillRgn(GpReader r, int cb)
        {
            List<(int L, int T, int R, int B)> rects = RgnRects(r, cb);
            if (rects.Count == 0 || Nop) return;
            GdiXform m = TargetWtoD();
            GdiRgn rg = RectsThrough(rects, m, _wmfCanvas);
            Func<int, int, uint> pattern = PatternOf();
            if (pattern == null) return;
            var spans = new List<GdiSpan>();
            foreach (var q in rg.Rects())
                for (int y = q.T; y < q.B; y++) spans.Add(new GdiSpan(y, q.L, q.R));
            spans.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X0.CompareTo(b.X0));
            GdiPaint(spans, pattern);
        }

        // ---- the poly records ---------------------------------------------------------------------

        /// <summary>kind: 0 PolyBezier, 1 Polygon, 2 Polyline, 3 PolyBezierTo, 4 PolylineTo.</summary>
        void GdiPolyPoints(PointF[] pts, int kind)
        {
            int n = pts.Length;
            switch (kind)
            {
                case 0: if (n < 4 || (n - 1) % 3 != 0) return; break;
                case 1: case 2: if (n < 2) return; break;
                case 3: if (n % 3 != 0) return; break;
            }
            GdiPath gp = _inPath ? (_gPath ??= new GdiPath()) : new GdiPath();
            bool fromCurrent = kind >= 3;
            GdiAddPoly(gp, pts, kind == 0 || kind == 3, fromCurrent, kind == 1);
            if (fromCurrent) _dc.Pos = pts[n - 1];
            if (_inPath) return;
            if (kind == 1) GdiFillAndStroke(gp, true, true);
            else GdiStroke(gp);
        }

        void GdiPolyPolyPoints(PointF[] pts, int[] counts, bool polygon)
        {
            GdiPath gp = _inPath ? (_gPath ??= new GdiPath()) : new GdiPath();
            int at = 0;
            foreach (int c in counts)
            {
                if (c < 0 || at + c > pts.Length) break;
                if (c >= 2)
                {
                    var part = new PointF[c];
                    Array.Copy(pts, at, part, 0, c);
                    GdiAddPoly(gp, part, false, false, polygon);
                }
                at += c;
            }
            if (_inPath) return;
            if (polygon) GdiFillAndStroke(gp, true, true);
            else GdiStroke(gp);
        }

        /// <summary>PolyDraw: PT_MOVETO 6, PT_LINETO 2, PT_BEZIERTO 4, each optionally with
        /// PT_CLOSEFIGURE 1.</summary>
        void GdiPolyDraw(PointF[] pts, byte[] types)
        {
            GdiPath gp = _inPath ? (_gPath ??= new GdiPath()) : new GdiPath();
            GdiXform m = TargetWtoD();
            Fix(m, _dc.Pos.X, _dc.Pos.Y, out int cx, out int cy);
            if (gp.Empty || _gMoved) gp.MoveTo(cx, cy);
            _gMoved = false;
            for (int i = 0; i < pts.Length; i++)
            {
                int t = types[i] & ~1;
                Fix(m, pts[i].X, pts[i].Y, out int x, out int y);
                if (t == 6) gp.MoveTo(x, y);
                else if (t == 2) gp.LineTo(x, y);
                else if (t == 4 && i + 2 < pts.Length)
                {
                    Fix(m, pts[i + 1].X, pts[i + 1].Y, out int x2, out int y2);
                    Fix(m, pts[i + 2].X, pts[i + 2].Y, out int x3, out int y3);
                    gp.BezierTo(x, y, x2, y2, x3, y3);
                    i += 2;
                }
                _dc.Pos = pts[i];
                if ((types[i] & 1) != 0) gp.CloseFigure();
            }
            if (!_inPath) GdiStroke(gp);
        }

        /// <summary>gdiplus IsPenCosmetic @1801f54a0: DPtoLP of (0, 0) and (128, 0); a pen is cosmetic
        /// when width times 128 is no more than the logical span of those 128 device pixels.</summary>
        bool GdipCosmetic(int width)
        {
            GdiXform m = TargetWtoD();
            if (!m.Inverse(out GdiXform inv)) return false;
            inv.Point2(0, 0, out int x0, out _);
            inv.Point2(128 << 4, 0, out int x1, out _);
            return width * 128 <= Math.Abs(x1 - x0);
        }

        /// <summary>A cosmetic stroke: GDI's grid-intersection lines, each segment's last pixel left
        /// out, figures closed by a segment back to their start.</summary>
        void GdiCosmetic(GdiPath p, uint color)
        {
            p = p.Flattened();
            var lit = new List<(int X, int Y, bool Gap)>();
            GdiPen pen = DrawPen();
            GdiLines.Style style = CosmeticStyle(pen, _dc.StyleState);
            int fresh = CosmeticStyle(pen, 0)?.Next ?? 0;
            foreach (GdiPath.Figure f in p.Figures)
            {
                if (style != null && f.ResetStyle) style.Next = fresh;
                for (int i = 1; i < f.Count; i++) GdiLines.Line(f.X[i - 1], f.Y[i - 1], f.X[i], f.Y[i], style, lit);
                if (f.Closed && f.Count > 1) GdiLines.Line(f.X[f.Count - 1], f.Y[f.Count - 1], f.X[0], f.Y[0], style, lit);
            }
            // bStrokeCosmetic @140171db8 writes back where its lines ended (clipped or not: each
            // figure starting over, the oracle agrees).
            if (style != null) _dc.StyleState = style.State;
            if (lit.Count == 0) return;
            // A style's gaps take the background colour in OPAQUE mode (the mix's background
            // half), and are left alone in TRANSPARENT.
            bool opaque = _dc.BkMode == 2;
            uint back = Rgb(_dc.BkColor);
            int code = _dc.Rop2;
            if (code != 13) _ropUsed = true;
            GdiRgn clip = GdiClip();
            BitmapData bd = _canvas.LockBits(new Rectangle(0, 0, _cw, _ch), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    byte* base0 = (byte*)bd.Scan0;
                    foreach (var q in lit)
                    {
                        if (q.Gap && !opaque) continue;
                        if ((uint)q.X >= (uint)_cw || (uint)q.Y >= (uint)_ch) continue;
                        if (clip != null && !In(clip.Row(q.Y), q.X)) continue;
                        uint* px = (uint*)(base0 + q.Y * bd.Stride) + q.X;
                        uint c = q.Gap ? back : color;
                        *px = code == 13 ? c : Rop2(code, c, *px);
                    }
                }
            }
            finally { _canvas.UnlockBits(bd); }
        }

        static bool In(int[] row, int x)
        {
            if (row == null) return false;
            for (int i = 0; i < row.Length; i += 2)
            {
                if (x < row[i]) return false;
                if (x < row[i + 1]) return true;
            }
            return false;
        }

        // GreExtCreatePen @1402336b8: the cosmetic styles (style units; bStrokeCosmetic scales them by
        // denStyleStep) and the geometric ones (pen widths, a dash one shorter and a gap one longer
        // under round or square caps).
        static readonly int[][] CosmeticStyles = { null, new[] { 6, 2 }, new[] { 1, 1, 1, 1, 1, 1, 1, 1 }, new[] { 3, 2, 1, 2 }, new[] { 3, 1, 1, 1, 1, 1 } };
        static readonly int[][] GeometricStyles = { null, new[] { 3, 1 }, new[] { 1, 1 }, new[] { 3, 1, 1, 1 }, new[] { 3, 1, 1, 1, 1, 1 } };

        /// <summary>The cosmetic pen's style (null when solid).</summary>
        GdiLines.Style CosmeticStyle(GdiPen pen, int state)
        {
            int st = pen.Style & 0xf;
            if (st >= 1 && st <= 4) return GdiLines.Style.From(CosmeticStyles[st], false, state);
            if (st == 8) return GdiLines.Style.Alternate(state);
            if (st == 7 && pen.Dashes != null && pen.Dashes.Length > 0)
            {
                var e = new int[pen.Dashes.Length];
                for (int i = 0; i < e.Length; i++) e[i] = (int)pen.Dashes[i];
                return GdiLines.Style.From(e, false, state);
            }
            return null;
        }

        /// <summary>A geometric pen's style in world units (null when solid).</summary>
        static float[] GeometricStyle(GdiPen pen, int endCap)
        {
            int st = pen.Style & 0xf;
            if (st >= 1 && st <= 4)
            {
                int[] t = GeometricStyles[st];
                var r = new float[t.Length];
                for (int i = 0; i < t.Length; i++)
                {
                    int v = t[i];
                    if (endCap != 2) v += (i & 1) != 0 ? 1 : -1;
                    r[i] = (float)(v * Math.Abs(pen.Width));
                }
                return r;
            }
            if (st == 7 && pen.Dashes != null && pen.Dashes.Length > 0)
            {
                var r = new float[pen.Dashes.Length];
                for (int i = 0; i < r.Length; i++) r[i] = (float)(int)pen.Dashes[i];
                return r;
            }
            return null;
        }
    }
}
