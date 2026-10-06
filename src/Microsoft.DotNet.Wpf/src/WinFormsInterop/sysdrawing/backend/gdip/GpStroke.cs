// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s pen drawing on a bitmap, read out of gdiplus.dll (arm64, public PDB):
//
//   GpGraphics::DrawPath @180075bd0 / DrawLines @18000c700 / DrawRects @18000d3f8 / DrawArc ...
//                         the call's path, its bounds with the pen (GpPath::GetBounds @18001c020),
//                         then RenderDrawPath
//   GpGraphics::RenderDrawPath @18000f060   nothing for an empty box; the box floored / ceiled into
//                         the drawing rectangle; nothing when that is totally clipped
//   DpDriver::StrokePath @180027360   a pen at most 1.5 device pixels wide with no anchor caps is a
//                         one-pixel pen: opaque, solid, aliased and without curves it is drawn by the
//                         one-pixel line DDA (DrawSolidLineOnePixelAliased); dashed it is flattened,
//                         dashed in world space and DDA'd (SolidStrokePathOnePixel); otherwise it is
//                         rasterized as nominal one-pixel lines (RasterizePath with InitializeNominal).
//                         Every other pen is widened in device space (GpPath::GetWidenedPath at
//                         flatness 0.25) and the outline filled, winding, with the pen's brush.
//   DrawSolidLineOnePixelAliased @180031fc0 + OnePixelLineDDAAliased::SetupAliased @180032c90,
//                         ClipRectangle @180031d20, IsInDiamond @180032c08, DrawXMajor @1800c55c0,
//                         DrawYMajor @1800c5810, DrawXMajorClip @1800c56d0, DrawYMajorClip @180032af0,
//                         StepUpAliasedClip @1800c5af0   the diamond-exit rule in 28.4: a line's end
//                         pixel is drawn when the end point is inside its diamond
//   DpPen::IsOnePixelWide @18000ba20 / IsSimple @180150bd8
//
// Float to int conversions written (int)(double)(longlong)f by the decompiler are frintm + fcvtzs in
// the binary: floor.
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    /// <summary>The DpPen a GpPen carries (GpPen +0x28): the state the drawing code reads.</summary>
    internal sealed class DpPen
    {
        public float Width = 1f;                  // +0x04
        public int Unit;                          // +0x08 (World)
        public int StartCap, EndCap;              // +0x0c, +0x10 (LineCap; 0xff custom)
        public int Join;                          // +0x14
        public float MiterLimit = 10f;            // +0x18
        public int Alignment;                     // +0x1c
        public Brush Brush;                       // +0x20
        public GpMatrix Xform = GpMatrix.CreateIdentity ();   // +0x28
        public int DashStyle;                     // +0x58
        public int DashCap;                       // +0x5c
        public float DashOffset;                  // +0x64
        public float[] DashArray;                 // +0x60/+0x68
        public float[] Compound;                  // +0x70/+0x78
        public GpCustomLineCap CustomStart, CustomEnd;   // +0x80, +0x88

        public static DpPen From (Pen p)
        {
            return new DpPen {
                Width = p.Width,
                StartCap = (int) p.StartCap,
                EndCap = (int) p.EndCap,
                Join = (int) p.LineJoin,
                MiterLimit = p.MiterLimit,
                Alignment = (int) p.Alignment,
                Brush = p.BrushRef,
                Xform = p.Xform,
                DashStyle = (int) p.DashStyle,
                DashCap = (int) p.DashCap,
                DashOffset = p.DashOffset,
                DashArray = p.DashArrayRef,
                Compound = p.CompoundRef,
                CustomStart = p.CustomStartRef?.gp,
                CustomEnd = p.CustomEndRef?.gp,
            };
        }

        public DpPen Clone () => (DpPen) MemberwiseClone ();

        public int DashCount => DashArray == null ? 0 : DashArray.Length;
        public int CompoundCount => Compound == null ? 0 : Compound.Length;

        /// <summary>DpPen::IsSimple: no dashes, no anchor or custom caps.</summary>
        public bool IsSimple => DashStyle == 0 && (StartCap & 0xf0) == 0 && (EndCap & 0xf0) == 0 && (DashCap & 0xf0) == 0;

        /// <summary>GetDeviceWidth @180035378.</summary>
        public static float DeviceWidth (float w, int unit, float dpi)
        {
            switch (unit) {
            case 3: return (dpi / 72f) * w;
            case 4: return w * dpi;
            case 5: return (dpi / 300f) * w;
            case 6: return (dpi / 25.4f) * w;
            default: return w;
            }
        }

        /// <summary>DpPen::IsOnePixelWide @18000ba20 (the matrix is the world-to-device).</summary>
        public bool IsOnePixelWide (GpMatrix? m, float dpi)
        {
            float w = Width;
            if (Unit == 0) {
                float s;
                if (m == null || (m.Value.Complexity & ~1) == 0) return !(1.5f <= w && w != 1.5f);
                GpMatrix t = m.Value;
                if ((t.Complexity & ~3) == 0) s = MathF.Abs (MathF.Abs (t.M22) < MathF.Abs (t.M11) ? t.M11 : t.M22);
                else GpStroke.MajorMinor (t, out s, out _);
                float v = s * w;
                return !(1.5f <= v && v != 1.5f);
            }
            return DeviceWidth (w, Unit, dpi) <= 1.5f;
        }

        public bool IsOnePixelWideSolid (GpMatrix? m, float dpi)
            => DashStyle == 0 && (StartCap & 0xf0) == 0 && (EndCap & 0xf0) == 0 && (DashCap & 0xf0) == 0 && IsOnePixelWide (m, dpi);
    }

    internal static class GpStroke
    {
        const float Eps = 1.1920929e-07f;

        /// <summary>GetMajorAndMinorAxis @18001fab8: the lengths of the axes the matrix maps the
        /// unit circle to, each at least 5.9604645e-4.</summary>
        public static void MajorMinor (in GpMatrix m, out float major, out float minor)
        {
            const float Min = 0.00059604645f;
            float a = m.M11, b = m.M12, c = m.M21, d = m.M22;
            float p = b * b + a * a;
            float h = (p - (c * c + d * d)) * 0.5f;
            float q = d * b + c * a;
            float r = q * q + h * h;
            if (0f < r) r = MathF.Sqrt (r);
            float s = (c * c + p + d * d) * 0.5f;
            float mj = MathF.Sqrt (s + r);
            major = Min <= mj ? mj : Min;
            float mn = MathF.Sqrt (s - r);
            minor = Min <= mn ? mn : Min;
        }

        /// <summary>GpPath::GetBounds(RectF*, matrix, pen, dpiX, dpiY) @18001c020: the control-point
        /// box under the matrix, grown by the most the pen can reach past it.</summary>
        public static RectangleF Bounds (GpPath path, GpMatrix? matrix, DpPen pen, float dpi)
        {
            if (path.Count == 0) return RectangleF.Empty;
            RectangleF cb = path.ControlBounds ();
            float l = cb.X, t = cb.Y, r = cb.Width + l, b = cb.Height + t;
            if (matrix is GpMatrix m && m.Complexity != 0) {
                if ((m.Complexity & ~3) == 0) {
                    float x0 = l, y0 = t, x1 = r, y1 = b;
                    m.Transform (ref x0, ref y0);
                    m.Transform (ref x1, ref y1);
                    l = Math.Min (x0, x1); r = Math.Max (x0, x1); t = Math.Min (y0, y1); b = Math.Max (y0, y1);
                } else {
                    var p = new [] { new PointF (l, t), new PointF (r, b), new PointF (l, b), new PointF (r, t) };
                    m.Transform (p);
                    l = r = p [0].X; t = b = p [0].Y;
                    for (int i = 1; i < 4; i++) {
                        l = p [i].X <= l ? p [i].X : l; r = r <= p [i].X ? p [i].X : r;
                        t = p [i].Y <= t ? p [i].Y : t; b = b <= p [i].Y ? p [i].Y : b;
                    }
                }
            }
            float w = r - l, h = b - t;
            if (w <= Eps) w = 0f;
            if (h <= Eps) h = 0f;
            var box = new RectangleF (l, t, w, h);
            if (pen == null) return box;
            float dw = MaxDeviceWidth (pen, matrix, dpi);
            float e = CapWidth (pen.StartCap, pen.CustomStart, dw, 1f);
            if (e <= dw) e = dw;
            float e2 = CapWidth (pen.EndCap, pen.CustomEnd, dw, 2f);
            if (e2 <= e) e2 = e;
            e = e2;
            if (path.Count >= 3 && (matrix == null || !pen.IsOnePixelWideSolid (matrix, dpi))) {
                float f = pen.Alignment == 0 ? 0.5f : 1f;
                float jw = dw;
                if (pen.Join == 0 || pen.Join == 3) {
                    jw = pen.MiterLimit * dw;
                    if (20f < jw) jw = pen.MiterLimit * dw;   // ComputeMiterLength(0, limit) is the limit
                }
                float je = f * jw;
                e = je <= e ? e : je;
            }
            if (Eps < box.Width || Eps < box.Height)
                box = new RectangleF (box.X - e, box.Y - e, box.Width + e + e, box.Height + e + e);
            return box;
        }

        /// <summary>The pen's width in device pixels along its widest direction (at least 1.42).</summary>
        static float MaxDeviceWidth (DpPen pen, GpMatrix? matrix, float dpi)
        {
            if (pen.Unit != 0) return DpPen.DeviceWidth (pen.Width, pen.Unit, dpi);
            GpMatrix m = matrix ?? GpMatrix.CreateIdentity ();
            if (matrix == null) { m.Dx = m.Dy = 0; }
            if ((pen.Xform.Complexity & ~1) != 0) m = GpMatrix.Multiply (pen.Xform, m);
            MajorMinor (m, out float major, out float minor);
            return 1.42f <= minor * pen.Width ? major * pen.Width : 1.42f;
        }

        static float CapWidth (int cap, GpCustomLineCap custom, float dw, float anchorPad)
        {
            if (cap == 0xff) {
                if (custom == null) return dw + anchorPad + dw + anchorPad;
                return custom.GetInsetWidth (dw);
            }
            if ((cap & 0xf0) != 0) return dw + anchorPad + dw + anchorPad;
            return dw * 0.5f;
        }

        /// <summary>GpRect from a RectF box: floor of the origin, ceiling of the far edge, plus one.</summary>
        public static bool BoundsToRect (RectangleF b, out Rectangle r)
        {
            r = Rectangle.Empty;
            const float Lim = 1073741824f;
            if (!(-Lim <= b.X && b.X <= Lim) || !(-Lim <= b.Y && b.Y <= Lim)) return false;
            if (!(0f <= b.Width && b.Width <= Lim) || !(0f <= b.Height && b.Height <= Lim)) return false;
            int x0 = (int) MathF.Floor (b.X), y0 = (int) MathF.Floor (b.Y);
            int x1 = (int) MathF.Ceiling (b.X + b.Width), y1 = (int) MathF.Ceiling (b.Y + b.Height);
            r = new Rectangle (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            return true;
        }
    }

    internal sealed partial class GpGraphics
    {
        // ---- the verbs --------------------------------------------------------------------------

        /// <summary>GpGraphics::DrawPath: false when the pen's brush cannot be drawn here.</summary>
        public bool DrawPath (Pen pen, GpPath path)
        {
            if (!CanFill (pen.BrushRef)) return false;
            if (path.Count < 1) return true;
            DpPen dp = DpPen.From (pen);
            RectangleF b = GpStroke.Bounds (path, WorldToDevice, dp, DpiX);
            return RenderDrawPath (b, path, dp);
        }

        /// <summary>GpGraphics::DrawRects: each rectangle wider and taller than epsilon is its own
        /// closed four-point figure, stroked on its own.</summary>
        public bool DrawRects (Pen pen, RectangleF[] rects)
        {
            if (!CanFill (pen.BrushRef)) return false;
            DpPen dp = DpPen.From (pen);
            foreach (RectangleF r in rects) {
                if (!(1.1920929e-07f < r.Width && 1.1920929e-07f < r.Height)) continue;
                float x1 = r.Width + r.X, y1 = r.Height + r.Y;
                var p = new GpPath (new [] { new PointF (r.X, r.Y), new PointF (x1, r.Y), new PointF (x1, y1), new PointF (r.X, y1) },
                                    new byte [] { 0, 1, 1, 0x81 }, FillMode.Alternate);
                RectangleF b = GpStroke.Bounds (p, WorldToDevice, dp, DpiX);
                if (!RenderDrawPath (b, p, dp)) return false;
            }
            return true;
        }

        bool RenderDrawPath (RectangleF b, GpPath path, DpPen pen)
        {
            if (MathF.Abs (b.Width) < 1.1920929e-07f || MathF.Abs (b.Height) < 1.1920929e-07f) return true;
            if (!GpStroke.BoundsToRect (b, out Rectangle draw)) return true;
            GpClip clip = Clip;
            if (clip.IsEmpty || !clip.IsVisible (draw)) return true;
            return StrokePath (draw, path, pen, clip);
        }

        // ---- DpDriver::StrokePath ---------------------------------------------------------------

        bool StrokePath (Rectangle draw, GpPath path, DpPen pen, GpClip clip)
        {
            GpMatrix w2d = WorldToDevice;
            float dpi = DpiX <= 0f ? 96f : DpiX;
            float w = pen.Width;
            bool onePixel;
            if (pen.Unit == 0) {
                if ((w2d.Complexity & ~1) == 0) onePixel = w <= 1.5f;
                else if ((w2d.Complexity & ~3) == 0) {
                    float s = MathF.Abs (w2d.M11) <= MathF.Abs (w2d.M22) ? w2d.M22 : w2d.M11;
                    onePixel = MathF.Abs (s) * w <= 1.5f;
                } else {
                    GpStroke.MajorMinor (w2d, out float major, out _);
                    onePixel = major * w <= 1.5f;
                }
            } else onePixel = DpPen.DeviceWidth (w, pen.Unit, dpi) <= 1.5f;
            if ((pen.StartCap & 0xf0) != 0 || (pen.EndCap & 0xf0) != 0 || (pen.DashCap & 0xf0) != 0) onePixel = false;

            bool opaqueAliased = false, simple = false;
            if (onePixel) {
                opaqueAliased = pen.Brush is SolidBrush sb && (uint) sb.Color.ToArgb () >> 24 == 0xff && Aliased;
                simple = pen.IsSimple;
            }

            if (opaqueAliased && simple && !path.HasBezier) {
                StrokeOnePixel (path, w2d, pen, clip, draw, true);
                return true;
            }

            GpPath fill = path;
            GpMatrix m = w2d;
            if (!simple) {
                if (opaqueAliased) {
                    // One pixel wide but dashed: dashed in world space, then the DDA.
                    GpPath flat = path.Clone ();
                    flat.Flatten (null, 0.25f);
                    if (flat.Count == 0) return true;
                    if (!DeviceRect (flat, w2d, ref draw)) return true;
                    GpPath dashed = GpPen.CreateDashedPath (flat, pen, null, dpi, dpi, 1f, false);
                    if (dashed == null) return true;
                    StrokeOnePixel (dashed, w2d, pen, clip, draw, false);
                    return true;
                }
                fill = GpPen.GetWidenedPath (path, pen, w2d, 0.25f, dpi);
                if (fill == null) return true;
                if (fill.Count == 0) return true;
                if (!DeviceRect (fill, null, ref draw)) return true;
                m = GpMatrix.CreateIdentity ();
            }
            GpScan scan = NewScan ();
            GpSpan span = CreateSpan (pen.Brush, scan, draw);
            if (span == null) return false;
            PointF[] pts = fill.PointArray ();
            GpRaster.FillPath (pts, fill.TypeArray (), pts.Length, m, simple || fill.FillMode == FillMode.Winding,
                               GpRaster.AntialiasMode (Smoothing), span, clip, draw, simple);
            scan.End ();
            return true;
        }

        /// <summary>GpPath::GetBounds(GpRect*, matrix): the drawing rectangle of a derived path.</summary>
        static bool DeviceRect (GpPath p, GpMatrix? m, ref Rectangle draw)
        {
            RectangleF b = GpStroke.Bounds (p, m, null, 96f);
            if (!GpStroke.BoundsToRect (b, out Rectangle r)) return false;
            draw = r;
            return true;
        }

        // ---- the one-pixel aliased DDA ----------------------------------------------------------

        /// <summary>DrawSolidStrokeOnePixel through FixedPointPathEnumerate; <paramref name="lastPixel"/> is
        /// the callback's flag (StrokePath sets it, SolidStrokePathOnePixel does not, and then only a
        /// closed figure's segments keep their last pixel).</summary>
        void StrokeOnePixel (GpPath path, in GpMatrix w2d, DpPen pen, GpClip clip, Rectangle draw, bool lastPixel)
        {
            uint color = (uint) ((SolidBrush) pen.Brush).Color.ToArgb ();
            GpScan scan = NewScan ();
            var span = new SolidSpan (scan, color);
            bool full = clip.Region.IsRect && clip.Bounds.Contains (draw);
            Rectangle? clipRect = null;
            ISpanSink sink = span;
            if (!full) {
                clipRect = clip.Bounds;
                if (!clip.Region.IsRect) sink = clip.Wrap (span);
            }
            var m = new GpMatrix { M11 = w2d.M11 * 16f, M12 = w2d.M12 * 16f, M21 = w2d.M21 * 16f, M22 = w2d.M22 * 16f,
                                   Dx = w2d.Dx * 16f, Dy = w2d.Dy * 16f };
            m.Complexity = m.ComputeComplexity ();
            int[] clipFix = clipRect is Rectangle cr ? new [] { cr.Left << 4, cr.Top << 4, cr.Right << 4, cr.Bottom << 4 } : null;
            PointF[] pts = path.PointArray ();
            GpRaster.Enumerate (pts, path.TypeArray (), pts.Length, m, clipFix, 0, (buf, n, term) => {
                // DrawSolidStrokeOnePixel @180032a20: each segment, from 28.4 back to float.
                for (int i = 0; i + 1 < n; i++) {
                    var a = new PointF (buf [i * 2] * 0.0625f, buf [i * 2 + 1] * 0.0625f);
                    var b = new PointF (buf [i * 2 + 2] * 0.0625f, buf [i * 2 + 3] * 0.0625f);
                    OnePixelLine.Draw (sink, clipRect, a, b, lastPixel || term == 2);
                }
            });
            scan.End ();
        }
    }

    /// <summary>OnePixelLineDDAAliased: the state block DrawSolidLineOnePixelAliased fills in.</summary>
    internal sealed class OnePixelLine
    {
        int _xMajor, _flipped;          // +0x00, +0x04
        int _dMajor, _dMinor, _dir;     // +0x08, +0x0c, +0x10
        int _majS, _majE, _minS, _minE; // +0x14 .. +0x20
        float _slope, _invSlope;        // +0x24, +0x28
        int _up, _down, _excl, _err;    // +0x30, +0x34, +0x38, +0x3c
        int _clipMajMin, _clipMajMax, _clipMinS, _clipMinE;   // +0x40 .. +0x4c
        float _frac0, _frac1;           // +0x50, +0x54

        static int Floor (float f) => (int) MathF.Floor (f);

        /// <summary>DrawSolidLineOnePixelAliased @180031fc0. <paramref name="last"/> is its
        /// last-pixel flag (the stroke callback always passes it set).</summary>
        public static void Draw (ISpanSink sink, Rectangle? clip, PointF p0, PointF p1, bool last)
        {
            int x0 = Floor (p0.X * 16f + 0.5f), x1 = Floor (p1.X * 16f + 0.5f);
            float dx = p1.X - p0.X, dy = p1.Y - p0.Y;
            if (dx == 0f && dy == 0f) return;
            float adx = dx < 0f ? -dx : dx;
            int xdir = dx >= 0f ? 1 : -1;
            int y0 = Floor (p0.Y * 16f + 0.5f), y1 = Floor (p1.Y * 16f + 0.5f);
            float ady = dy < 0f ? -dy : dy;
            int ydir = dy >= 0f ? 1 : -1;
            var l = new OnePixelLine ();
            if (ady < adx) {
                int dir = ydir;
                l._majS = x0; l._majE = x1; l._minS = y0; l._minE = y1;
                if (xdir == -1) { dir = -ydir; l._majS = x1; l._majE = x0; l._minS = y1; l._minE = y0; l._flipped = 1; }
                l._xMajor = 1;
                l._dir = dir;
                l._slope = ((float) dir * ady) / adx;
            } else {
                int dir = xdir;
                l._majS = y0; l._majE = y1; l._minS = x0; l._minE = x1;
                if (ydir == -1) { dir = -xdir; l._majS = y1; l._majE = y0; l._minS = x1; l._minE = x0; l._flipped = 1; }
                l._dir = dir;
                l._slope = ((float) dir * adx) / ady;
            }
            l._dMajor = l._majE - l._majS;
            l._dMinor = (l._minE - l._minS) * l._dir;
            l._excl = last ? 0 : 1;
            if (clip is Rectangle c) {
                l._invSlope = l._slope == 0f ? 0f : 1f / l._slope;
                if (!l.ClipRectangle (c)) return;
                if (!l.SetupAliased ()) return;
                int cl = c.Left, ct = c.Top, cr = c.Left + c.Width - 1, cb = c.Top + c.Height - 1;
                if (l._xMajor != 0) {
                    l._clipMajMin = cl; l._clipMajMax = cr;
                    l._clipMinS = l._dir == 1 ? ct : cb; l._clipMinE = l._dir == 1 ? cb : ct;
                    l.DrawXMajorClip (sink);
                } else {
                    l._clipMajMin = ct; l._clipMajMax = cb;
                    l._clipMinS = l._dir == 1 ? cl : cr; l._clipMinE = l._dir == 1 ? cr : cl;
                    l.DrawYMajorClip (sink);
                }
                return;
            }
            if (!l.SetupAliased ()) return;
            if (l._xMajor != 0) l.DrawXMajor (sink);
            else l.DrawYMajor (sink);
        }

        static bool IsInDiamond (int p1, int p2, int p3, int p4)
        {
            int a = p1 < 0 ? -p1 : p1, b = p2 < 0 ? -p2 : p2;
            if (7 < a + b) {
                if (p2 == 0 && (p4 == 0 ? p1 == 8 : p1 == -8)) return true;
                if (p1 != 0 || p2 != 8) {
                    if (p3 != 0 && a + b == 8) {
                        if (p4 == 0) { if (0 < p1) return 0 < p2; }
                        else if (p1 < 0 && 0 < p2) return true;
                    }
                    return false;
                }
            }
            return true;
        }

        static bool InDiamondY (int dx, int dy)
        {
            int s = Math.Abs (dx) + Math.Abs (dy);
            return s < 8 || (dy == 0 && dx == 8) || (dx == 0 && dy == 8);
        }

        bool SetupAliased ()
        {
            int w3, w4, r;
            if (_dMajor == _dMinor) {
                w3 = 1;
                if (_dir == 1) { w4 = 1; r = 8; } else { w4 = 0; r = 7; }
            } else { w3 = 0; w4 = 0; r = 7; }
            int ns = _minS, ne = _minE, ms = _majS, me = _majE;
            int rns = (ns + 7) & ~15, rne = (ne + 7) & ~15;
            int ps, pe;
            bool sIn, eIn;
            if (_xMajor != 0) {
                pe = (me + r) & ~15;
                ps = (ms + r) & ~15;
                sIn = IsInDiamond (ms - ps, ns - rns, w3, w4);
                eIn = IsInDiamond (me - pe, ne - rne, w3, w4);
            } else {
                ps = (ms + 7) & ~15;
                pe = (me + 7) & ~15;
                sIn = InDiamondY (ns - rns, ms - ps);
                eIn = InDiamondY (ne - rne, me - pe);
            }
            if (_flipped != 0 && _excl != 0) { if (sIn || (ms & 15) <= 8) ps += 16; }
            else if ((ms & 15) <= 8 && !sIn) ps += 16;
            int m0 = Floor ((float) (ps - ms) * _slope + (float) ns + _frac0);
            int p0 = ps >> 4;
            if (_flipped == 0 && _excl != 0) { if (!((me & 15) <= 8 && !eIn)) pe -= 16; }
            else if (!eIn && 8 < (me & 15)) pe -= 16;
            int m1 = Floor ((float) (pe - me) * _slope + (float) ne + _frac1);
            int p1 = pe >> 4;
            if (p1 < p0) return false;
            _up = _dMinor << 1;
            _down = _dMajor * 2;
            int rm0 = (m0 + 7) & ~15;
            _err = (_err - ((rm0 - m0) * _dir + 8) * _down) >> 4;
            _minS = rm0 >> 4;
            _minE = (m1 + 7) >> 4;
            _majS = p0;
            _majE = p1;
            return true;
        }

        bool ClipRectangle (Rectangle c)
        {
            bool y = _xMajor == 0;
            int minMin = (y ? c.X : c.Y) * 16 - 8;
            int minMax = (y ? c.X + c.Width : c.Y + c.Height) * 16 - 8;
            int majMax = (y ? c.Y + c.Height : c.X + c.Width) * 16 - 8;
            int majMin = (y ? c.Y : c.X) * 16 - 8;
            int ms = _majS, me = _majE;
            if (ms < majMin || majMax < me) {
                if (!(ms <= majMax && majMin <= me)) return false;
                if (ms < majMin) {
                    float f = (float) (majMin - ms) * _slope + (float) _minS;
                    int i = Floor (f);
                    _minS = i; _majS = majMin; _frac0 = f - (float) i;
                }
                if (majMax < me) {
                    float f = (float) (majMax - me) * _slope + (float) _minE;
                    int i = Floor (f);
                    _minE = i; _majE = majMax; _excl = 0; _frac1 = f - (float) i;
                }
            }
            bool pos = _dir == 1;
            int lo = pos ? _minS : _minE, hi = pos ? _minE : _minS;
            float loFrac = pos ? _frac0 : _frac1, hiFrac = pos ? _frac1 : _frac0;
            if (lo < minMin || minMax < hi) {
                if (minMax < lo || hi < minMin) return false;
                if (lo < minMin) {
                    int i = Floor (((float) minMin - ((float) lo + loFrac)) * _invSlope);
                    if (pos) _majS += i; else _majE += i;
                    lo = minMin;
                }
                if (minMax < hi) {
                    int i = Floor (((float) minMax - ((float) hi + hiFrac)) * _invSlope);
                    if (pos) _majE += i; else _majS += i;
                    hi = minMax;
                    _excl = 0;
                }
                if (pos) { _minS = lo; _minE = hi; } else { _minE = lo; _minS = hi; }
            }
            if (_frac1 != 0f && (_minE & 15) == 8) _minE++;
            if (_frac0 != 0f) _err = Floor ((float) (_dMajor * 2 * _dir) * _frac0);
            return true;
        }

        void DrawXMajor (ISpanSink sink)
        {
            int n = _majE - _majS + 1;
            int x = _majS, run = 0, start = x;
            while (n != 0) {
                n--;
                x++;
                run++;
                _err += _up;
                if (0 < _err) {
                    if (n == 0) break;
                    sink.OutputSpan (_minS, start, start + run);
                    _minS += _dir;
                    _err -= _down;
                    start = x; run = 0;
                }
            }
            if (run > 0) sink.OutputSpan (_minS, start, start + run);
            _majS = x;
        }

        void DrawYMajor (ISpanSink sink)
        {
            int y = _majS;
            for (int n = _majE - y; n >= 0; n--) {
                sink.OutputSpan (y, _minS, _minS + 1);
                y++;
                _err += _up;
                if (0 < _err) { _minS += _dir; _err -= _down; }
            }
            _majS = y;
        }

        bool StepUpAliasedClip ()
        {
            int x = _majS;
            if (x < _clipMajMin) {
                int e = _err;
                do {
                    e += _up;
                    x++;
                    if (0 < e) { _minS += _dir; e -= _down; }
                } while (x < _clipMajMin);
                _majS = x;
                _err = e;
            }
            int cnt = (_clipMinS - _minS) * _dir;
            if (0 < cnt) {
                do {
                    if (_clipMajMax < x) break;
                    x++;
                    _majS = x;
                    _err += _up;
                    if (0 < _err) { cnt--; _minS += _dir; _err -= _down; }
                } while (0 < cnt);
            }
            int ne = _minE;
            if (0 < (ne - _clipMinE) * _dir) {
                if (0 < (_minS - _clipMinE) * _dir) return false;
                _minE = _clipMinE;
                ne = _clipMinE;
            }
            int me = _majE;
            if (_clipMajMax < me) { _majE = _clipMajMax; me = _clipMajMax; }
            if (_dir == -1 && _minS < ne) _minS = ne;
            return x <= me;
        }

        void DrawXMajorClip (ISpanSink sink)
        {
            if (!StepUpAliasedClip ()) return;
            int cnt = (_minE - _minS) * _dir;
            int n = _majE - _majS + 1;
            int start = _majS, run = 0;
            while (true) {
                run = 0;
                bool done = false;
                int e;
                do {
                    if (n == 0) { done = true; break; }
                    n--;
                    _majS++;
                    run++;
                    e = _err + _up;
                    _err = e;
                } while (e < 1);
                if (done) break;
                if (n == 0) break;
                cnt--;
                sink.OutputSpan (_minS, start, start + run);
                run = 0;
                _minS += _dir;
                _err -= _down;
                if (cnt < 0) break;
                start = _majS;
            }
            if (run > 0) sink.OutputSpan (_minS, start, start + run);
        }

        void DrawYMajorClip (ISpanSink sink)
        {
            if (!StepUpAliasedClip ()) return;
            int y = _majS;
            int cnt = (_minE - _minS) * _dir;
            for (int n = _majE - y; -1 < cnt && -1 < n; n--) {
                sink.OutputSpan (y, _minS, _minS + 1);
                y++;
                _majS = y;
                _err += _up;
                if (0 < _err) { cnt--; _minS += _dir; _err -= _down; }
            }
        }
    }
}
