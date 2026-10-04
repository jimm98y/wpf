// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// DriverMeta's paths and pens (gdiplus.dll 10.0.26100): ConvertPathToGdi @1800d8248 turns a DpPath
// into device POINTs at the increased resolution (GpMatrix::Transform to POINT @180034b60, the
// rasterizer's ceiling) and decides how GDI is to draw it:
//
//   flag 0x01  no curves: polygons / polylines (each figure closed with its first point when the
//              caller fills, or when the figure was closed and does not end on it: 0x20; 0x40 when
//              a figure is open, or ends on its first point)
//   flag 0x10  one figure of nothing but Beziers (open, or closed on its first point): PolyBezier
//   otherwise  BeginPath + DrawMixedPath (MoveTo, LineTo / PolylineTo, PolyBezierTo runs,
//              CloseFigure at a closed figure's end) + EndPath and FillPath / StrokePath /
//              StrokeAndFillPath / SelectClipPath
//
// ConvertPenToGdi @1800d8620: a geometric ExtCreatePen (round 0 / square 0x100 / flat 0x200 caps,
// round 0 / bevel 0x1000 / miter 0x2000 joins, SetMiterLimit for a miter, put back after), its
// width the pen's through the matrix at the multiplier; under two device pixels a cosmetic pen,
// or at the increased resolution a round-joined one.
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpMetafileRecorder
    {
        sealed class PathToGdi
        {
            public bool Valid;
            public int X, Y, W, H;            // +4: the device bounds
            public int Flags;                 // +0x1b4
            public int Mult = 1;              // +0x1bc
            bool _inc;                        // +0x1b8
            public int FillMode;              // +0x1b0: GDI's ALTERNATE 1 / WINDING 2
            public int[] Pts = new int[0];    // +0x198, x y pairs
            public int NPts;                  // +0x1a8
            public int NSub;                  // +0x1ac
            public int[] Counts = new int[0]; // +0x1a0 (polygonal)
            public byte[] Types;              // +0x1a0 (mixed)

            public bool IsEmpty => W < 1 || H < 1;

            public PathToGdi(GpPath path, GpMatrix m, int flags, Rectangle? draw)
            {
                if ((flags & 8) != 0 && draw.HasValue)
                {
                    _inc = true;
                    Mult = IncreasedResolutionMultiplier(draw.Value);
                    if (Mult == 1) _inc = false;
                }
                Flags = flags & 0x406;
                Rectangle? bounds = (flags & 8) == 0 ? draw : null;
                FillMode = path.FillMode == System.Drawing.Drawing2D.FillMode.Winding ? 2 : 1;
                int n = path.Count;
                if (n == 0) { Valid = true; return; }
                List<byte> types = path.Types;
                List<PointF> src = path.Points;
                if ((types[0] & 7) != 0) return;
                int subpaths = path.SubpathCount;
                if (!path.HasBezier)
                {
                    Flags |= 1;
                    if (subpaths == 1)
                    {
                        TransformPoints(m, src, n, bounds);
                        NPts = n; NSub = 1;
                        if ((flags & 0x10) != 0 || (types[n - 1] & 0x80) != 0) Flags |= 0x20;
                        Counts = new[] { n };
                    }
                    else
                    {
                        var dst = new PointF[n + subpaths];
                        var counts = new int[subpaths + 1];
                        int prev = unchecked((int)0xffffff6c);
                        int w = 0, start = 0, nsub = 0;
                        for (int i = 0; i < n; i++)
                        {
                            int t = types[i];
                            if ((t & 7) == 0)
                            {
                                if ((prev & 7) == 0) w--;
                                else
                                {
                                    if (nsub > 0)
                                    {
                                        if ((flags & 0x10) == 0 && (prev & 0x80) == 0) Flags |= 0x40;
                                        else
                                        {
                                            Flags |= 0x20;
                                            if (dst[w - 1].X != dst[start].X || dst[w - 1].Y != dst[start].Y)
                                                dst[w++] = dst[start];
                                        }
                                        counts[nsub - 1] = w - start;
                                    }
                                    nsub++;
                                }
                                start = w;
                            }
                            dst[w++] = src[i];
                            prev = t;
                        }
                        int last = types[n - 1];
                        if ((last & 7) == 0) { nsub--; w--; }
                        else if ((flags & 0x10) != 0 || (last & 0x80) != 0)
                        {
                            if (dst[w - 1].X == dst[start].X && dst[w - 1].Y == dst[start].Y) Flags |= 0x40;
                            else { Flags |= 0x20; dst[w++] = dst[start]; }
                        }
                        if (nsub > 0) counts[nsub - 1] = w - start;
                        NPts = w; NSub = nsub;
                        Counts = counts;
                        TransformPoints(m, dst, w, bounds);
                    }
                    Valid = true;
                    return;
                }
                TransformPoints(m, src, n, bounds);
                NPts = n;
                bool pure = subpaths == 1;
                if (pure)
                {
                    for (int i = 1; i < n && pure; i++) if ((types[i] & 7) != 3) pure = false;
                    if (pure && (flags & 0x10) == 0 && (types[n - 1] & 0x80) != 0 &&
                        (Pts[0] != Pts[(n - 1) * 2] || Pts[1] != Pts[(n - 1) * 2 + 1]))
                        pure = false;
                }
                if (pure) Flags |= 0x10;
                else Types = types.ToArray();
                Valid = true;
            }

            void TransformPoints(GpMatrix m, IList<PointF> src, int n, Rectangle? bounds)
            {
                GpMatrix mm = m;
                if (_inc) mm.Scale(Mult, Mult, true);
                Pts = new int[n * 2];
                for (int i = 0; i < n; i++)
                {
                    mm.TransformFix(src[i], out int x, out int y);
                    Pts[i * 2] = x; Pts[i * 2 + 1] = y;
                }
                if (bounds.HasValue) { X = bounds.Value.X; Y = bounds.Value.Y; W = bounds.Value.Width; H = bounds.Value.Height; return; }
                if (n == 0) return;
                int minx = Pts[0], maxx = Pts[0], miny = Pts[1], maxy = Pts[1];
                for (int i = 1; i < n; i++)
                {
                    int x = Pts[i * 2], y = Pts[i * 2 + 1];
                    if (x < minx) minx = x; else if (x > maxx) maxx = x;
                    if (y < miny) miny = y; else if (y > maxy) maxy = y;
                }
                if (!_inc) { X = minx; Y = miny; W = maxx - minx + 1; H = maxy - miny + 1; return; }
                int k = Mult;
                H = (2 * k - miny + maxy - 1) / k;
                Y = miny / k;
                X = minx / k;
                W = (2 * k - minx + maxx - 1) / k;
            }

            int[] Slice(int from, int count)
            {
                var r = new int[count * 2];
                Array.Copy(Pts, from * 2, r, 0, count * 2);
                return r;
            }

            /// <summary>DrawMixedPath @1802232c8.</summary>
            bool DrawMixedPath(GpEmfDc dc)
            {
                bool ok = true;
                int i = 0, last = NPts - 1;
                while (i <= last)
                {
                    int t = Types[i] & 7;
                    if (t == 0)
                    {
                        if (i > 0 && (Types[i - 1] & 0x80) != 0) ok = ok && dc.CloseFigure();
                        ok = ok && dc.MoveToEx(Pts[i * 2], Pts[i * 2 + 1]);
                        i++;
                        continue;
                    }
                    int j = i;
                    do j++; while (j <= last && (Types[j] & 7) == t);
                    if (!ok) { i = j; continue; }
                    if (t == 3) ok = dc.PolyBezierTo(Slice(i, j - i), j - i);
                    else if (j - i == 1) ok = dc.LineTo(Pts[i * 2], Pts[i * 2 + 1]);
                    else ok = dc.PolylineTo(Slice(i, j - i), j - i);
                    i = j;
                }
                if ((Types[NPts - 1] & 0x80) != 0) ok = ok && dc.CloseFigure();
                return ok;
            }

            /// <summary>CPolyPolygon::Draw @1800d91b0: more than 31 polygons split in eight runs when
            /// their boxes are disjoint.</summary>
            static bool PolyPolygonDraw(GpEmfDc dc, int[] pts, int ptFrom, int[] counts, int cFrom, int npoly)
            {
                if (npoly > 0x1f)
                {
                    int size = npoly / 8;
                    var runs = new (int P, int C, int N, int L, int T, int R, int B)[8];
                    int p = ptFrom;
                    for (int k = 0; k < 8; k++)
                    {
                        int cnt = k == 7 ? npoly - 7 * size : size;
                        int c0 = cFrom + k * size;
                        int total = 0;
                        for (int q = 0; q < cnt; q++) total += counts[c0 + q];
                        int l = pts[p * 2], t = pts[p * 2 + 1], r = l, b = t;
                        for (int q = 1; q < total; q++)
                        {
                            int x = pts[(p + q) * 2], y = pts[(p + q) * 2 + 1];
                            if (x < l) l = x; if (x > r) r = x;
                            if (y < t) t = y; if (y > b) b = y;
                        }
                        runs[k] = (p, c0, cnt, l, t, r, b);
                        p += total;
                    }
                    bool disjoint = true;
                    for (int a = 0; a < 8 && disjoint; a++)
                        for (int c = a + 1; c < 8; c++)
                            if (runs[a].L < runs[c].R && runs[a].T < runs[c].B && runs[c].L < runs[a].R && runs[c].T < runs[a].B)
                            { disjoint = false; break; }
                    if (disjoint)
                    {
                        for (int k = 0; k < 8; k++)
                            if (!PolyPolygonDraw(dc, pts, runs[k].P, counts, runs[k].C, runs[k].N)) return false;
                        return true;
                    }
                }
                int sum = 0;
                for (int q = 0; q < npoly; q++) sum += counts[cFrom + q];
                var xy = new int[sum * 2];
                Array.Copy(pts, ptFrom * 2, xy, 0, sum * 2);
                var cs = new int[npoly];
                Array.Copy(counts, cFrom, cs, 0, npoly);
                return dc.PolyPolygon(xy, cs, npoly);
            }

            /// <summary>ConvertPathToGdi::Fill @1800d95c0.</summary>
            public bool Fill(GpEmfDc dc, GpEmfDc.GdiObject brush)
            {
                if (NPts < 1) return true;
                int mode0 = SetupForIncreasedResolution(Mult, dc);
                GpEmfDc.GdiObject h = dc.SelectObject(brush);
                int fm = dc.SetPolyFillMode(FillMode);
                bool ok;
                if ((Flags & 1) != 0)
                {
                    GpEmfDc.GdiObject p = dc.SelectObject(GpEmfDc.Stock(8));
                    if (NSub == 1) ok = dc.Polygon(Pts, NPts);
                    else ok = PolyPolygonDraw(dc, Pts, 0, Counts, 0, NSub);
                    dc.SelectObject(p);
                }
                else
                {
                    ok = dc.BeginPath();
                    if ((Flags & 0x10) == 0) ok = ok && DrawMixedPath(dc);
                    else ok = ok && dc.PolyBezier(Pts, NPts);
                    ok = ok && dc.EndPath() && dc.FillPath();
                }
                dc.SetPolyFillMode(fm);
                dc.SelectObject(h);
                CleanupForIncreasedResolution(Mult, mode0, dc);
                return ok;
            }

            /// <summary>ConvertPathToGdi::Draw @1800d9270 (no one-pixel extension: the metafile driver
            /// never asks for it).</summary>
            public bool Draw(GpEmfDc dc, GpEmfDc.GdiObject pen)
            {
                if (NPts < 1) return true;
                int mode0 = SetupForIncreasedResolution(Mult, dc);
                GpEmfDc.GdiObject h = dc.SelectObject(pen);
                GpEmfDc.GdiObject b = dc.SelectObject(GpEmfDc.Stock(5));
                bool ok;
                if ((Flags & 1) == 0)
                {
                    if ((Flags & 0x10) != 0) ok = dc.PolyBezier(Pts, NPts);
                    else ok = dc.BeginPath() && DrawMixedPath(dc) && dc.EndPath() && dc.StrokePath();
                }
                else if (NSub != 1)
                {
                    if ((Flags & 0x20) == 0) ok = dc.PolyPolyline(Pts, Counts, NSub);
                    else if ((Flags & 0x40) == 0) ok = dc.PolyPolygon(Pts, Counts, NSub);
                    else
                    {
                        // GDI+ compares the whole buffer's first point with the subpath's last index.
                        ok = true;
                        int at = 0;
                        for (int k = 0; k < NSub; k++)
                        {
                            int cpt = Counts[k];
                            int[] sub = Slice(at, cpt);
                            if (Pts[0] == Pts[(cpt - 1) * 2] && Pts[1] == Pts[(cpt - 1) * 2 + 1]) ok = ok && dc.Polygon(sub, cpt);
                            else ok = ok && dc.Polyline(sub, cpt);
                            at += cpt;
                        }
                    }
                }
                else if ((Flags & 0x20) != 0) ok = dc.Polygon(Pts, NPts);
                else ok = dc.Polyline(Pts, NPts);
                dc.SelectObject(h);
                dc.SelectObject(b);
                CleanupForIncreasedResolution(Mult, mode0, dc);
                return ok;
            }

            /// <summary>ConvertPathToGdi::FillAndDraw @1800d9748.</summary>
            public bool FillAndDraw(GpEmfDc dc, GpEmfDc.GdiObject brush, GpEmfDc.GdiObject pen)
            {
                if (NPts < 1) return true;
                int mode0 = SetupForIncreasedResolution(Mult, dc);
                GpEmfDc.GdiObject h = dc.SelectObject(pen);
                GpEmfDc.GdiObject hb = dc.SelectObject(brush);
                int fm = dc.SetPolyFillMode(FillMode);
                bool ok;
                if ((Flags & 1) != 0)
                {
                    if (NSub == 1) ok = dc.Polygon(Pts, NPts);
                    else ok = dc.PolyPolygon(Pts, Counts, NSub);
                }
                else
                {
                    ok = dc.BeginPath();
                    if ((Flags & 0x10) == 0) ok = ok && DrawMixedPath(dc);
                    else ok = ok && dc.PolyBezier(Pts, NPts);
                    ok = ok && dc.EndPath() && dc.StrokeAndFillPath();
                }
                dc.SetPolyFillMode(fm);
                dc.SelectObject(hb);
                dc.SelectObject(h);
                CleanupForIncreasedResolution(Mult, mode0, dc);
                return ok;
            }

            /// <summary>ConvertPathToGdi::AlphaFill @1800d8a50: as the rectangles', under R2_MASKPEN.</summary>
            public bool AlphaFill(GpEmfDc dc, GpEmfDc.GdiObject brush, GpEmfDc.GdiObject mask)
            {
                GpEmfDc.GdiObject h = dc.SelectObject(brush);
                bool ok = dc.PatBlt(X, Y, W, H, 0x5a0049);
                int rop = dc.SetROP2(9);
                uint old = dc.SetTextColor(dc.GetBkColor());
                ok = ok && Fill(dc, mask);
                dc.SetTextColor(old);
                dc.SetROP2(rop);
                ok = ok && dc.PatBlt(X, Y, W, H, 0x5a0049);
                dc.SelectObject(h);
                return ok;
            }

            /// <summary>The path's pixels, for the clip the EMF DC keeps (its box bounds later records).</summary>
            DpRegion DeviceRegion()
            {
                var pts = new PointF[NPts];
                var types = new byte[NPts];
                float k = 1f / Mult;
                for (int i = 0; i < NPts; i++)
                {
                    pts[i] = new PointF(Pts[i * 2] * k, Pts[i * 2 + 1] * k);
                    types[i] = Types != null ? Types[i] : (byte)1;
                }
                if (Types == null)
                {
                    int at = 0;
                    for (int s = 0; s < Math.Max(NSub, 1); s++)
                    {
                        int c = NSub > 0 && s < Counts.Length ? Counts[s] : NPts;
                        if (at < NPts) types[at] = 0;
                        if (c > 0 && at + c - 1 < NPts) types[at + c - 1] |= 0x80;
                        at += c;
                    }
                    if ((Flags & 0x10) != 0) for (int i = 1; i < NPts; i++) types[i] = 3;
                }
                var flat = new GpPath(pts, types, FillMode == 2 ? System.Drawing.Drawing2D.FillMode.Winding : System.Drawing.Drawing2D.FillMode.Alternate);
                flat.Flatten(null, 0.25f);
                return GpRegionRaster.FromPath(flat.PointArray(), flat.TypeArray(), flat.FillMode, GpMatrix.CreateIdentity());
            }

            /// <summary>ConvertPathToGdi::AndClip @1802227b8: the path selected into the clip.</summary>
            public bool AndClip(GpEmfDc dc, DpRegion device)
            {
                if (NPts < 1) return true;
                bool ok = dc.BeginPath();
                int fm = dc.SetPolyFillMode(FillMode);
                int mode0 = SetupForIncreasedResolution(Mult, dc);
                if ((Flags & 1) == 0)
                {
                    if (ok) ok = (Flags & 0x10) == 0 ? DrawMixedPath(dc) : dc.PolyBezier(Pts, NPts);
                }
                else if (ok) ok = NSub == 1 ? dc.Polygon(Pts, NPts) : dc.PolyPolygon(Pts, Counts, NSub);
                CleanupForIncreasedResolution(Mult, mode0, dc);
                ok = ok && dc.EndPath() && dc.SelectClipPath(1, device ?? DeviceRegion());
                dc.SetPolyFillMode(fm);
                return ok;
            }
        }

        // ---- ConvertPenToGdi ----------------------------------------------------------------------

        sealed class PenToGdi
        {
            public bool Valid;
            public GpEmfDc.GdiObject Pen;
            bool _miterSet;
            float _oldMiter;
            readonly GpEmfDc _dc;

            /// <summary>ConvertPenToGdi @1800d8620 (no LOGBRUSH given: the pen's brush as a solid one).</summary>
            public PenToGdi(GpEmfDc dc, DpPen pen, GpMatrix m, float dpi, ref int flags, int mult)
            {
                _dc = dc;
                int join = pen.Join;
                int type = 0x10000;
                if ((pen.CompoundCount > 0 || pen.Alignment != 0) && (flags & 1) == 0) return;
                float w = pen.Width * (float)mult;
                int width;
                bool thin;
                if (pen.Unit == 0)
                {
                    if (m.Complexity != 0)
                    {
                        PointF v = m.VectorTransform(new PointF(w, 0f));
                        w = MathF.Sqrt((v.Y - 0f) * (v.Y - 0f) + (v.X - 0f) * (v.X - 0f));
                    }
                    width = Floor(w + 0.5f);
                    thin = width < 2 && pen.StartCap != 0xff && pen.EndCap != 0xff;
                    if (width < 2) width = 1;
                }
                else
                {
                    w = DpPen.DeviceWidth(w, pen.Unit, dpi);
                    width = Floor(w + 0.5f);
                    thin = width < 2;
                }
                if (thin)
                {
                    width = 1;
                    if ((flags & 8) == 0) { flags |= 0x20; type = 0; }
                    else join = 2;
                }
                int style;
                switch (pen.DashStyle)
                {
                    case 0: style = 0; break;
                    case 1: style = 1; break;
                    case 2: style = 2; break;
                    case 3: style = 3; break;
                    case 4: style = 4; break;
                    default:
                        if ((flags & 1) == 0) return;
                        style = 0;
                        break;
                }
                if (type == 0x10000)
                {
                    if ((flags & 2) == 0 && style != 0)
                    {
                        if ((flags & 1) == 0) return;
                        style = 0;
                    }
                    else if (style != 0 && (flags & 2) != 0) return;
                    int cap = pen.StartCap;
                    int other = style != 0 ? pen.DashCap : cap;
                    if ((cap != pen.EndCap || pen.EndCap != other || other != cap) && (flags & 1) == 0) return;
                    if (cap == 0) style |= 0x200;
                    else if (cap == 1) style |= 0x100;
                    else if (cap != 2)
                    {
                        if ((flags & 1) == 0) return;
                        style |= 0x200;
                    }
                    bool miter = false;
                    if (join == 1) style |= 0x1000;
                    else if (join != 2)
                    {
                        if (join != 0 && join != 3 && (flags & 1) == 0) return;
                        miter = true;
                    }
                    if (miter)
                    {
                        style |= 0x2000;
                        _oldMiter = dc.SetMiterLimit(pen.MiterLimit);
                        _miterSet = true;
                    }
                }
                uint color = ToColorRef(pen.Brush);
                Pen = GpEmfDc.ExtCreatePen(style | type, width, 0, color, 0);
                Valid = true;
            }

            /// <summary>~ConvertPenToGdi: the pen deleted, the miter limit put back.</summary>
            public void Dispose()
            {
                if (Valid) _dc.DeleteObject(Pen);
                if (_miterSet) _dc.SetMiterLimit(_oldMiter);
            }
        }

        // ---- DriverMeta::FillPath / StrokePath / StrokeAndFillWidenedPath / FillRegion -------------

        /// <summary>DriverMeta::FillPath @1800d4a90.</summary>
        void DriverFillPath(Rectangle draw, GpPath path, Brush brush)
        {
            var cp = new PathToGdi(path, DeviceMatrix, 0x19, draw);
            if (!cp.Valid || cp.IsEmpty) return;
            GpEmfDc dc = Dc;
            GpEmfDc.GdiObject hb = GetBrush(dc, brush, out uint alpha, out bool del);
            if (hb == null && BrushFillUsingBitmap(new Rectangle(cp.X, cp.Y, cp.W, cp.H), brush, cp)) return;
            if (alpha <= 1) return;
            if (hb == null) hb = SetColor(dc, ToColorRef(brush));
            dc = ContextHdc();
            bool saved = SetupClipping(dc, new Rectangle(cp.X, cp.Y, cp.W, cp.H));
            if (alpha < 0xfe)
            {
                GpEmfDc.GdiObject mask = SetAlpha(dc, alpha, true, false);
                cp.AlphaFill(dc, hb, mask);
            }
            else
            {
                cp.Fill(dc, hb);
                if (del) dc.DeleteObject(hb);
            }
            RestoreClipping(dc, saved);
        }

        float ContextDpiX => DpiX > 0f ? DpiX : 96f;

        /// <summary>DriverMeta::StrokePath @1800d5ef0: a simple opaque pen as a GDI pen; anything else
        /// widened by GDI+ and filled (opaque solid: filled and outlined with a one-pixel bevel pen).</summary>
        void DriverStrokePath(Rectangle draw, GpPath path, Pen pen)
        {
            DpPen dp = DpPen.From(pen);
            bool opaque = pen.BrushRef is SolidBrush sb && sb.Color.A > 0xfd;
            if (!(opaque && dp.IsSimple && dp.CompoundCount < 1))
            {
                GpMatrix m = DeviceMatrix;
                GpPath wide = GpPen.GetWidenedPath(path, dp, m, 0.25f, ContextDpiX);
                if (wide == null) return;
                wide.FillMode = System.Drawing.Drawing2D.FillMode.Winding;
                GpMatrix inv = m;
                if (!inv.Invert()) inv = GpMatrix.CreateIdentity();
                wide.Transform(inv);
                if (opaque) StrokeAndFillWidenedPath(draw, wide, dp);
                else DriverFillPath(draw, wide, dp.Brush);
                return;
            }
            GpEmfDc dc = ContextHdc();
            int flags = 9;
            var cp = new PathToGdi(path, DeviceMatrix, 9, draw);
            if (!cp.Valid) return;
            var pg = new PenToGdi(dc, dp, DeviceMatrix, ContextDpiX, ref flags, cp.Mult);
            if (pg.Valid && !cp.IsEmpty)
            {
                bool saved = SetupClipping(dc, draw);
                cp.Draw(dc, pg.Pen);
                RestoreClipping(dc, saved);
            }
            pg.Dispose();
        }

        /// <summary>DriverMeta::StrokeAndFillWidenedPath @1800d5c58.</summary>
        void StrokeAndFillWidenedPath(Rectangle draw, GpPath wide, DpPen pen)
        {
            var cp = new PathToGdi(wide, DeviceMatrix, 0x19, draw);
            if (!cp.Valid || cp.IsEmpty) return;
            GpEmfDc dc = Dc;
            GpEmfDc.GdiObject hb = GetBrush(dc, pen.Brush, out _, out _);
            if (hb == null) hb = SetColor(dc, ToColorRef(pen.Brush));
            dc = ContextHdc();
            int flags = 9;
            var outline = new DpPen { Width = 1f, Unit = 0, StartCap = 0, EndCap = 0, Join = 1, MiterLimit = 10f, Brush = pen.Brush };
            var pg = new PenToGdi(dc, outline, DeviceMatrix, ContextDpiX, ref flags, cp.Mult);
            if (pg.Valid)
            {
                bool saved = SetupClipping(dc, draw);
                cp.FillAndDraw(dc, hb, pg.Pen);
                RestoreClipping(dc, saved);
            }
            pg.Dispose();
        }

        /// <summary>DriverMeta::FillRegion @1800d5030: FillRgn with the region's device rectangles.</summary>
        void DriverFillRegion(DpRegion rgn, Brush brush)
        {
            Rectangle bd = rgn.IsEmpty ? Rectangle.Empty : rgn.Bounds;
            if (bd.Width < 1 || bd.Height < 1) return;
            GpEmfDc dc = Dc;
            GpEmfDc.GdiObject hb = GetBrush(dc, brush, out uint alpha, out bool del);
            if (hb == null)
            {
                var gp = new GpPath(System.Drawing.Drawing2D.FillMode.Alternate);
                foreach (Rectangle r in rgn.Rects()) gp.AddRects(new[] { new RectangleF(r.X, r.Y, r.Width, r.Height) });
                var cp = new PathToGdi(gp, GpMatrix.CreateIdentity(), 0x10, null);
                if (cp.Valid && BrushFillUsingBitmap(bd, brush, cp)) return;
            }
            if (alpha <= 1) return;
            if (hb == null) hb = SetColor(dc, ToColorRef(brush));
            dc = ContextHdc();
            bool saved = SetupClipping(dc, bd);
            if (alpha < 0xfe)
            {
                GpEmfDc.GdiObject mask = SetAlpha(dc, alpha, true, false);
                GpEmfDc.GdiObject h = dc.SelectObject(hb);
                dc.PatBlt(bd.X, bd.Y, bd.Width, bd.Height, 0x5a0049);
                int rop = dc.SetROP2(9);
                uint old = dc.SetTextColor(dc.GetBkColor());
                dc.FillRgn(rgn, mask);
                dc.SetTextColor(old);
                dc.SetROP2(rop);
                dc.PatBlt(bd.X, bd.Y, bd.Width, bd.Height, 0x5a0049);
                dc.SelectObject(h);
            }
            else
            {
                dc.FillRgn(rgn, hb);
                if (del) dc.DeleteObject(hb);
            }
            RestoreClipping(dc, saved);
        }
    }
}
