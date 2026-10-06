// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// DrawImage on a bitmap Graphics, as gdiplus.dll (arm64, 10.0.26100, public PDB) does it:
//
//   GdipDrawImage(I) @180059c50 / @180006b90   dest rect (x, y, GetImageDestPageSize), src = bounds, Pixel
//   GdipDrawImageRect(I) @18005a690             dest rect, src = bounds, Pixel
//   GdipDrawImagePoints(I) / PointsRect(I) @18005a0c0 / @18005a2e0   three points (four: NotImplemented)
//   GdipDrawImageRectRect(I) @180006d00         srcUnit must be Pixel..Millimeter
//   GdipDrawImagePointRect @180059f00           dest size from GetImageDestPageSize(src size, srcUnit)
//   GpGraphics::GetImageDestPageSize @180076780  Pixel: size * graphics dpi / (page multiplier * image dpi);
//                                         other units: size * page multiplier(unit) / page multiplier
//   GpGraphics::DrawImage(rect, rect) @18000f418  the src -> dst matrix from the right/bottom edges; a
//                                         negative src extent flipped back
//   GpGraphics::DrawImage(points, rect) @180076c40   InferAffineMatrix(points <- rect), UndoSourceFlip
//   GpGraphics::DrawImage(image, src, xform, ...) @18000f530   src in srcUnit scaled to pixels (the
//                                         matrix scaled back), PixelOffsetMode Half/HighQuality moves the
//                                         source by -0.5 (matrix +0.5), the draw bounds floor/ceil + 1,
//                                         culled against the visible clip, then DrvDrawImage with the
//                                         three world-space corners
//   GpGraphics::DrvDrawImage @180011828    device matrix; a near-integer translate keeps it; a pure
//                                         rotate/flip by quarter turns rotates a copy of the bitmap
//                                         instead; HighQuality modes under rotation pre-scale into a
//                                         PARGB bitmap of the device size; ApplyVisibleClipToSourceRect;
//                                         PipeLockBits locks the row band the source rect needs (plus
//                                         the kernel's margin) as PARGB; DpDriver::DrawImage
//   CopyOnWriteBitmap::PipeLockBits @1800189e0   the band and the source rect relative to it
//   DpDriver::DrawImage @180030730          device matrix again, the span (CreateOutputSpan), and the
//                                         source rectangle rasterized as a polygon with the context's
//                                         antialias mode
//

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace System.Drawing.WebGpuBackend.Gdip
{
    /// <summary>GpImageAttributes as the drawing code reads it.</summary>
    internal sealed class GpImageAttr
    {
        public DpImageAttr Dp = DpImageAttr.Default;
        /// <summary>The recolor pipeline (null: none).</summary>
        public GpRecolor Recolor;
    }

    internal sealed partial class GpGraphics
    {
        const float RealEpsilon = 1.1920929e-07f;

        static int Floor (float f) => (int) MathF.Floor (f);
        static int Ceil (float f) => (int) MathF.Ceiling (f);

        /// <summary>Whether this engine draws <paramref name="f"/> (formats GDI+ can lock as PARGB).</summary>
        public static bool CanDrawImage (GdipFrame f) => f != null && GdipPixels.Convertible (f.Format);

        // ---- GpMatrix helpers -----------------------------------------------------------------------

        /// <summary>GpMatrix::InferAffineMatrix(PointF*, RectF) @180034750; false (m untouched) when degenerate.</summary>
        public static bool InferAffine (PointF[] p, RectangleF r, ref GpMatrix m)
        {
            float x = r.X, y = r.Y, w = r.Width, h = r.Height;
            float nh = -h;
            float det = ((w + x) * (y + h) - y * x) + (x * nh - y * w);
            if (!(1.1920929e-07f <= MathF.Abs (det))) return false;
            float inv = 1f / det;
            m.M11 = (h * p [1].X + nh * p [0].X) * inv;
            m.M12 = (h * p [1].Y + nh * p [0].Y) * inv;
            float nw = -w;
            m.M21 = (w * p [2].X + nw * p [0].X) * inv;
            m.M22 = (w * p [2].Y + nw * p [0].Y) * inv;
            float d = (x + w) * (y + h) - x * y;
            float a = -x * h, b = -y * w;
            m.Dx = ((a * p [1].X + d * p [0].X) + b * p [2].X) * inv;
            m.Dy = ((a * p [1].Y + d * p [0].Y) + b * p [2].Y) * inv;
            m.Complexity = m.ComputeComplexity ();
            return true;
        }

        static bool NearInteger (float v, float tol) => MathF.Abs ((float) Floor (v + 0.5f) - v) <= tol;

        /// <summary>GpMatrix::IsIntegerTranslate @18000b700.</summary>
        static bool IsIntegerTranslate (in GpMatrix m) => (m.Complexity & ~1) == 0 && NearInteger (m.Dx, 0.015625f) && NearInteger (m.Dy, 0.015625f);

        /// <summary>GpMatrix::AnalyzeRotateFlip @1800343a8: the RotateFlipType a quarter-turn matrix is.</summary>
        static RotateFlipType AnalyzeRotateFlip (in GpMatrix m)
        {
            if (IsIntegerTranslate (m)) return RotateFlipType.RotateNoneFlipNone;
            const float t = 0.00059604645f;
            if (MathF.Abs (m.M11) < t && MathF.Abs (m.M22) < t) {
                if (MathF.Abs (m.M21 - 1f) < t) {
                    if (MathF.Abs (m.M12 - 1f) < t) return (RotateFlipType) 5;
                    if (MathF.Abs (m.M12 + 1f) < t) return (RotateFlipType) 3;
                }
                if (MathF.Abs (m.M21 + 1f) < t) {
                    if (MathF.Abs (m.M12 - 1f) < t) return (RotateFlipType) 1;
                    if (MathF.Abs (m.M12 + 1f) < t) return (RotateFlipType) 7;
                }
            }
            if (MathF.Abs (m.M12) < t && MathF.Abs (m.M21) < t) {
                if (MathF.Abs (m.M11 - 1f) < t && MathF.Abs (m.M22 + 1f) < t) return (RotateFlipType) 6;
                if (MathF.Abs (m.M11 + 1f) < t) {
                    if (MathF.Abs (m.M22 - 1f) < t) return (RotateFlipType) 4;
                    if (t <= MathF.Abs (m.M22 + 1f)) return 0;
                    return (RotateFlipType) 2;
                }
            }
            return 0;
        }

        /// <summary>GpMatrix::TransformRect @1800da8b8.</summary>
        static RectangleF TransformRectF (in GpMatrix m, RectangleF r)
        {
            if (m.Complexity == 0) return r;
            float x = r.X, y = r.Y;
            float x0 = m.M21 * y + m.M11 * x + m.Dx, y0 = m.M22 * y + m.M12 * x + m.Dy;
            float x1 = m.M21 * (r.Height + y) + m.M11 * (r.Width + x) + m.Dx;
            float y1 = m.M22 * (r.Height + y) + m.M12 * (r.Width + x) + m.Dy;
            float l = x1 < x0 ? x1 : x0, t = y1 < y0 ? y1 : y0;
            float rr = x1 < x0 ? x0 : x1, bb = y1 < y0 ? y0 : y1;
            return new RectangleF (l, t, rr - l, bb - t);
        }

        // ---- GpGraphics::GetImageDestPageSize ---------------------------------------------------------

        public void GetImageDestPageSize (GdipFrame img, float w, float h, GraphicsUnit unit, out float dw, out float dh)
            => GetImageDestPageSize (img.DpiX, img.DpiY, w, h, unit, out dw, out dh);

        /// <summary>The same for any image, by its resolution (a metafile's is its header's).</summary>
        public void GetImageDestPageSize (float ix, float iy, float w, float h, GraphicsUnit unit, out float dw, out float dh)
        {
            GetPageMultipliers (out float pmx, out float pmy);
            if (unit != GraphicsUnit.Pixel) {
                GetPageMultipliers (unit, 1f, out float mx, out float my);
                dh = (my * h) / pmy;
                dw = (mx * w) / pmx;
                return;
            }
            dh = (DpiY * h) / (pmy * iy);
            dw = (DpiX * w) / (pmx * ix);
        }

        // ---- GpGraphics::DrawImage overloads ----------------------------------------------------------

        /// <summary>GpGraphics::DrawImage(image, RectF dst, RectF src, unit, ia) @18000f418.</summary>
        public bool DrawImage (GdipFrame img, RectangleF dst, RectangleF src, GraphicsUnit unit, GpImageAttr ia)
        {
            if (!CanDrawImage (img)) return false;
            GpMatrix m = GpMatrix.CreateIdentity ();
            float dr = dst.Width + dst.X, db = dst.Height + dst.Y;
            float sl = src.X, st = src.Y, sr = src.Width + sl, sb = src.Height + st;
            if (sl != sr && st != sb) {
                m.M11 = (dr - dst.X) / (sr - sl);
                m.M22 = (db - dst.Y) / (sb - st);
                m.Dx = dr - m.M11 * sr;
                m.Dy = db - m.M22 * sb;
                m.Complexity = m.ComputeComplexity ();
            }
            if (src.Width < 0f) { src.X = sr; src.Width = -src.Width; }
            if (src.Height < 0f) { src.Y = sb; src.Height = -src.Height; }
            DrawImageCore (img, src, m, ia, unit);
            return true;
        }

        /// <summary>GpGraphics::DrawImage(image, PointF*, count, RectF src, unit, ia) @180076c40.</summary>
        public bool DrawImage (GdipFrame img, PointF[] pts, RectangleF src, GraphicsUnit unit, GpImageAttr ia)
        {
            if (!CanDrawImage (img)) return false;
            if (pts.Length != 3) return true;      // 4: NotImplemented, else InvalidParameter
            GpMatrix m = GpMatrix.CreateIdentity ();
            InferAffine (pts, src, ref m);
            if (src.Width < 0f) { src.X += src.Width; src.Width = -src.Width; }
            if (src.Height < 0f) { src.Y += src.Height; src.Height = -src.Height; }
            DrawImageCore (img, src, m, ia, unit);
            return true;
        }

        /// <summary>GpGraphics::DrawImage(image, RectF* src, GpMatrix* xform, effect, ia, srcUnit) @18000f530.</summary>
        void DrawImageCore (GdipFrame img, RectangleF r, GpMatrix m, GpImageAttr ia, GraphicsUnit unit)
        {
            if (!(unit == GraphicsUnit.Display || unit == GraphicsUnit.World || unit == GraphicsUnit.Pixel)) {
                float rx = img.DpiX, ry = img.DpiY, fx, fy;
                switch (unit) {
                case GraphicsUnit.Point: fx = rx / 72f; fy = ry / 72f; break;
                case GraphicsUnit.Document: fx = rx / 300f; fy = ry / 300f; break;
                case GraphicsUnit.Millimeter: fx = rx / 25.4f; fy = ry / 25.4f; break;
                default: fx = rx; fy = ry; break;
                }
                r = new RectangleF (r.X * fx, r.Y * fy, r.Width * fx, r.Height * fy);
                m.Scale (1f / fx, 1f / fy, false);
            }
            if (r.Width < 0f) {
                m.Translate (-r.Width, 0f, false);
                m.Scale (-1f, 1f, false);
                r.X += r.Width; r.Width = -r.Width;
            }
            if (r.Height < 0f) {
                m.Translate (0f, -r.Height, false);
                m.Scale (1f, -1f, false);
                r.Y += r.Height; r.Height = -r.Height;
            }
            if (_ctx.PixelOffset == PixelOffsetMode.Half || _ctx.PixelOffset == PixelOffsetMode.HighQuality) {
                r.X -= 0.5f; r.Y -= 0.5f;
                m.Translate (0.5f, 0.5f, false);
            }
            GpMatrix d = GpMatrix.Multiply (m, WorldToDevice);
            float l = r.X, t = r.Y, rr = r.X + r.Width, bb = r.Y + r.Height;
            if (d.Complexity != 0) {
                if ((d.Complexity & ~3) == 0) {
                    float x0 = l, y0 = t, x1 = rr, y1 = bb;
                    d.Transform (ref x0, ref y0); d.Transform (ref x1, ref y1);
                    l = x0 < x1 ? x0 : x1; rr = x0 < x1 ? x1 : x0;
                    t = y0 < y1 ? y0 : y1; bb = y0 < y1 ? y1 : y0;
                } else {
                    float ax = d.M11 * l + d.M21 * t + d.Dx, ay = d.M12 * l + d.M22 * t + d.Dy;
                    float bx = d.M11 * rr + d.M21 * t + d.Dx, by = d.M12 * rr + d.M22 * t + d.Dy;
                    float cx = d.M11 * rr + d.M21 * bb + d.Dx, cy = d.M12 * rr + d.M22 * bb + d.Dy;
                    float ex = d.M11 * l + d.M21 * bb + d.Dx, ey = d.M12 * l + d.M22 * bb + d.Dy;
                    l = Math.Min (Math.Min (ax, bx), Math.Min (cx, ex)); rr = Math.Max (Math.Max (ax, bx), Math.Max (cx, ex));
                    t = Math.Min (Math.Min (ay, by), Math.Min (cy, ey)); bb = Math.Max (Math.Max (ay, by), Math.Max (cy, ey));
                }
            }
            float bw = rr - l; if (bw <= 0.00059604645f) bw = 0f;
            float bh = bb - t; if (bh <= 0.00059604645f) bh = 0f;
            const float Lim = 1073741824f;
            if (!(-Lim <= l && l <= Lim && -Lim <= t && t <= Lim && 0f <= bw && bw <= Lim && 0f <= bh && bh <= Lim)) return;
            int il = Floor (l), it = Floor (t);
            int iw = (Ceil (bw + l) - il) + 1, ih = (Ceil (bh + t) - it) + 1;
            GpClip clip = Clip;
            if (clip.IsEmpty) return;
            Rectangle vb = clip.Bounds;
            int ir = il + iw, ib = it + ih;
            if (vb.Right <= il || ir <= vb.Left || vb.Bottom <= it || ib <= vb.Top || ir <= il || ib <= it) return;
            if (!clip.IsVisible (Rectangle.FromLTRB (il, it, ir, ib))) return;

            float x = r.X, y = r.Y;
            var pts = new PointF [] { new PointF (x, y), new PointF (x + r.Width, y), new PointF (x, y + r.Height) };
            m.Transform (pts);
            DrvDrawImage (new Rectangle (il, it, iw, ih), img, pts, r, ia);
        }

        // ---- GpGraphics::DrvDrawImage -------------------------------------------------------------------

        void DrvDrawImage (Rectangle drawBounds, GdipFrame bmp, PointF[] pts, RectangleF src, GpImageAttr ia)
        {
            PointF p0 = pts [0], p1 = pts [1], p2 = pts [2];
            if (MathF.Abs ((p0.X - p1.X) * (p2.Y - p0.Y) - (p0.Y - p1.Y) * (p2.X - p0.X)) < RealEpsilon) return;
            GpMatrix m = GpMatrix.CreateIdentity ();
            if (!InferAffine (pts, src, ref m)) m = new GpMatrix { M11 = 1, M22 = 1 };
            m = GpMatrix.Multiply (m, WorldToDevice);
            if (MathF.Abs (m.M11 * m.M22 - m.M21 * m.M12) < RealEpsilon) return;
            bool half = _ctx.PixelOffset == PixelOffsetMode.Half || _ctx.PixelOffset == PixelOffsetMode.HighQuality;
            GpMatrix savedW2D = WorldToDevice;
            try {
                if (!((m.Complexity & ~1) == 0 && NearInteger (m.Dx, 0.015625f) && NearInteger (m.Dy, 0.015625f))) {
                    RotateFlipType rf = AnalyzeRotateFlip (m);
                    if (rf != 0) {
                        float iw = bmp.Width, ih = bmp.Height;
                        bmp = RotatedCopy (bmp, rf);
                        RotateFlipSetup (rf, iw, ih, half, ref m, ref src, ref pts);
                    }
                }
                int interp = (int) _ctx.Interpolation;
                if ((m.Complexity & ~3) != 0 && (interp == 6 || interp == 7)) {
                    if (PrescaleRotated (bmp, pts, src, ia)) return;
                }
                ApplyVisibleClipToSourceRect (m, ref src, pts);
                if (half) {
                    src.X += 0.5f; src.Y += 0.5f;
                    m.Dx = (m.M11 * -0.5f - m.M21 * 0.5f) + m.Dx + 0.5f;
                    m.Dy = (m.M22 * -0.5f - m.M12 * 0.5f) + m.Dy + 0.5f;
                    m.Complexity |= 1;
                }
                if (bmp == Frame || bmp.Bits == Frame.Bits) bmp = bmp.Clone ();
                DpBitmapSrc band = PipeLockBits (bmp, ref src, m, ia);
                if (half) { src.X -= 0.5f; src.Y -= 0.5f; }
                DriverDrawImage (band, drawBounds, ia?.Dp ?? DpImageAttr.Default, pts, src);
            } finally {
                WorldToDevice = savedW2D;
            }
        }

        static GdipFrame RotatedCopy (GdipFrame f, RotateFlipType rf)
        {
            GdipFrame c = GdipPixels.Convert (f, new Rectangle (0, 0, f.Width, f.Height), PixelFormat.Format32bppPArgb, false);
            return GdipTransform.RotateFlip (c, rf);
        }

        /// <summary>The rotate/flip block of DrvDrawImage: the source rect moved onto the rotated copy,
        /// and world-to-device made the translation that is left (the source's own W x H are the
        /// unrotated image's).</summary>
        void RotateFlipSetup (RotateFlipType rf, float W, float H, bool half, ref GpMatrix m, ref RectangleF src, ref PointF[] pts)
        {
            float dyHalf = m.Dy + 0.5f;
            float x = src.X, y = src.Y, w = src.Width, h = src.Height;
            float dx = m.Dx, dy = m.Dy;
            if (half) {
                x += 0.5f; y += 0.5f;
                dx = (m.M11 * -0.5f - m.M21 * 0.5f) + m.Dx + 0.5f;
                dy = (m.M22 * -0.5f - m.M12 * 0.5f) + dyHalf;
            }
            var t = new GpMatrix { M11 = 1, M22 = 1, Dx = dx, Dy = dy };
            int cx = t.ComputeComplexity ();
            float nx = x, ny = y, nw = w, nh = h;
            bool or1 = true;
            switch ((int) rf) {
            case 1: nw = h; nh = w; dx = (-H + 0f) + dx; dy = (-H * 0f + 0f) + dy; nx = (H - (y + y + h)) + y; ny = x + 0f; break;
            case 2: dx = (-H * 0f + -W) + dx; dy = (-W * 0f + -H) + dy; nx = (W - (x * 2f + w)) + x; ny = (H - (y * 2f + h)) + y; break;
            case 3: dx = (-W * 0f + 0f) + dx; dy = (-W + 0f) + dy; nw = h; nh = w; nx = 0f + y; ny = (W - (x + x + w)) + x; break;
            case 4: dx = (-W + 0f) + dx; dy = (-W * 0f + 0f) + dy; nx = (W - (x * 2f + w)) + x; ny = y + 0f; break;
            case 5: nx = y; ny = x; nw = h; nh = w; or1 = false; break;
            case 6: dx = (-H * 0f + 0f) + dx; dy = (-H + 0f) + dy; nx = x + 0f; ny = (H - (y * 2f + h)) + y; break;
            case 7: dx = (-W * 0f + -H) + dx; dy = (-H * 0f + -W) + dy; nw = h; nh = w; nx = (H - (y + y + h)) + y; ny = (W - (x + x + w)) + x; break;
            default: or1 = false; break;
            }
            if (or1) cx |= 1;
            src = new RectangleF (nx, ny, nw, nh);
            m = new GpMatrix { M11 = 1, M22 = 1, Dx = dx, Dy = dy, Complexity = cx };
            WorldToDevice = m;
            pts = new PointF [] { new PointF (nx, ny), new PointF (nw + nx, ny), new PointF (nx, nh + ny) };
        }

        /// <summary>The HighQuality pre-scale of DrvDrawImage under rotation: the image is first drawn
        /// (SourceCopy, same interpolation) into a PARGB bitmap of its device size, which is then drawn.</summary>
        bool PrescaleRotated (GdipFrame bmp, PointF[] pts, RectangleF src, GpImageAttr ia)
        {
            var d = (PointF[]) pts.Clone ();
            WorldToDevice.Transform (d);
            float l1 = MathF.Sqrt ((d [1].Y - d [0].Y) * (d [1].Y - d [0].Y) + (d [1].X - d [0].X) * (d [1].X - d [0].X));
            int w = Ceil (l1);
            float l2 = MathF.Sqrt ((d [2].Y - d [0].Y) * (d [2].Y - d [0].Y) + (d [2].X - d [0].X) * (d [2].X - d [0].X));
            int h = Ceil (l2);
            if (!(1.0000001f < MathF.Abs ((float) w - src.Width) && 1.0000001f < MathF.Abs ((float) h - src.Height))) return false;
            if (w <= 0 || h <= 0) return true;
            var tmp = new GdipFrame (w, h, PixelFormat.Format32bppPArgb) {
                DpiX = Math.Min (DpiX, bmp.DpiX), DpiY = Math.Min (DpiY, bmp.DpiY),
            };
            var g = new GpGraphics (tmp);
            g.Interpolation = _ctx.Interpolation;
            g.Compositing = CompositingMode.SourceCopy;
            g.DrawImage (bmp, new RectangleF (0, 0, w, h), src, GraphicsUnit.Pixel, ia);
            var r = new RectangleF (0, 0, w, h);
            GpMatrix m = GpMatrix.CreateIdentity ();
            InferAffine (pts, r, ref m);
            DrawImageCore (tmp, r, m, null, GraphicsUnit.Pixel);
            return true;
        }

        // ---- ApplyVisibleClipToSourceRect @1800113f8 ---------------------------------------------------

        RectangleF VisibleClipBoundsWorld ()
        {
            Rectangle b = _visibleClip.Bounds;
            GpMatrix inv = WorldToDevice;
            if (inv.Complexity == 0) return new RectangleF (b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top);
            if (!inv.Invert ()) return RectangleF.Empty;
            if ((inv.Complexity & ~3) == 0) return TransformRectF (inv, new RectangleF (b.Left, b.Top, b.Right - b.Left, b.Bottom - b.Top));
            var p = new PointF [] { new PointF (b.Left, b.Top), new PointF (b.Right, b.Top), new PointF (b.Right, b.Bottom), new PointF (b.Left, b.Bottom) };
            inv.Transform (p);
            float l = p [0].X, t = p [0].Y, r = p [0].X, bo = p [0].Y;
            for (int i = 1; i < 4; i++) { l = Math.Min (l, p [i].X); r = Math.Max (r, p [i].X); t = Math.Min (t, p [i].Y); bo = Math.Max (bo, p [i].Y); }
            return new RectangleF (l, t, r - l, bo - t);
        }

        static void AdjustRectFromInternalPoint (ref RectangleF r, PointF p, PointF a, PointF b, PointF c, ref bool changed)
        {
            const float Eps = 1.1920929e-07f;
            float py = p.Y, top = r.Y;
            if (!(top < py && py < r.Height + top)) return;
            float nh;
            if (a.Y - py <= Eps || b.Y - py <= Eps || c.Y - py <= Eps) {
                if (py - a.Y <= Eps || py - b.Y <= Eps || py - c.Y <= Eps) return;
                nh = py - top;
            } else {
                r.Y = py;
                nh = r.Height - (py - top);
            }
            r.Height = nh;
            changed = true;
        }

        static float Margin (int interp, float scale)
        {
            switch (interp) {
            case 3: return 1f;
            case 5: return 0f;
            case 6: return scale < 1f ? 1f / scale : 1f;
            case 4: return 2f;
            case 7: return scale < 1f ? 2f / scale : 2f;
            default: return 0f;
            }
        }

        void ApplyVisibleClipToSourceRect (in GpMatrix m, ref RectangleF src, PointF[] pts)
        {
            RectangleF vb = VisibleClipBoundsWorld ();
            GpMatrix d2w = WorldToDevice;
            if (d2w.Complexity != 0 && !d2w.Invert ()) return;
            // src -> world = m x deviceToWorld, then inverted: world -> src.
            var sw = new GpMatrix {
                M11 = m.M11 * d2w.M11 + m.M12 * d2w.M21,
                M12 = m.M12 * d2w.M22 + m.M11 * d2w.M12,
                M21 = m.M21 * d2w.M11 + m.M22 * d2w.M21,
                M22 = m.M22 * d2w.M22 + m.M21 * d2w.M12,
                Dx = m.Dy * d2w.M21 + m.Dx * d2w.M11 + d2w.Dx,
                Dy = m.Dy * d2w.M22 + m.Dx * d2w.M12 + d2w.Dy,
            };
            sw.Complexity = sw.ComputeComplexity ();
            GpMatrix ws = sw;
            if (!ws.Invert ()) return;
            var q = new PointF [] {
                new PointF (vb.X, vb.Y), new PointF (vb.Width + vb.X, vb.Y),
                new PointF (vb.X, vb.Height + vb.Y), new PointF (vb.Width + vb.X, vb.Height + vb.Y),
            };
            // the binary's order: (l,t) (r,t) (l,b) (r,b) is local_a8[0..7] = l,t, r,t, l,b, r,b
            ws.Transform (q);
            var r = new RectangleF (src.X - -1f, src.Y - -1f, src.Width - 2f, src.Height - 2f);
            bool changed = false;
            for (int i = 0; i < 4; i++)
                AdjustRectFromInternalPoint (ref r, q [i], q [(i + 1) & 3], q [(i + 2) & 3], q [(i + 3) & 3], ref changed);
            if (!changed) return;
            float mg = Margin ((int) _ctx.Interpolation, MathF.Sqrt (m.M21 * m.M21 + m.M22 * m.M22));
            float nl = (r.X - 1f) - 0f, nt = (r.Y - 1f) - mg;
            float nw = r.Width + 2f + 0f, nh = mg + mg + r.Height + 2f;
            float right = src.Width + src.X;
            if (nw + nl < right) right = nw + nl;
            float top0 = src.Y, bottom = top0 + src.Height;
            if (nt + nh < bottom) bottom = nt + nh;
            if (nl <= src.X) nl = src.X;
            if (nt <= top0) nt = top0;
            src = new RectangleF (nl, nt, right - nl, bottom - nt);
            pts [0] = new PointF (nl, nt);
            pts [1] = new PointF (nl + (right - nl), nt);
            pts [2] = new PointF (nl, nt + (bottom - nt));
            sw.Transform (pts);
        }

        // ---- CopyOnWriteBitmap::PipeLockBits @1800189e0 -------------------------------------------------

        DpBitmapSrc PipeLockBits (GdipFrame bmp, ref RectangleF src, in GpMatrix m, GpImageAttr ia)
        {
            int interp = (int) _ctx.Interpolation;
            float x0 = src.X, y0 = src.Y, w = src.Width, h = src.Height;
            float left = x0, top = y0, right = w + x0, bottom = h + y0;
            if (m.Complexity != 0) {
                if ((m.Complexity & ~3) == 0) {
                    float ax = x0, ay = y0, bx = right, by = bottom;
                    m.Transform (ref ax, ref ay); m.Transform (ref bx, ref by);
                    left = ax < bx ? ax : bx; right = ax < bx ? bx : ax;
                    top = ay < by ? ay : by; bottom = ay < by ? by : ay;
                } else {
                    var p = new PointF [] { new PointF (x0, y0), new PointF (right, y0), new PointF (right, bottom), new PointF (x0, bottom) };
                    m.Transform (p);
                    left = right = p [0].X; top = bottom = p [0].Y;
                    for (int i = 1; i < 4; i++) { left = Math.Min (left, p [i].X); right = Math.Max (right, p [i].X); top = Math.Min (top, p [i].Y); bottom = Math.Max (bottom, p [i].Y); }
                }
            }
            float dw = right - left; if (dw <= 0.00059604645f) dw = 0f;
            float dh = bottom - top; if (dh <= 0.00059604645f) dh = 0f;
            float devH = MathF.Abs ((dh + top) - top);
            float yScale = devH / MathF.Abs (h);
            int iy0 = Floor (y0), iy1 = Ceil (MathF.Abs (h) + y0);
            float mg = 0f;
            switch (interp) {
            case 3: mg = 1f; break;
            case 4: mg = 2f; break;
            case 6: mg = 1f <= yScale ? 1f : 1f / yScale; break;
            case 7: mg = 1f <= yScale ? 2f : 2f / yScale; break;
            }
            int im = Ceil (mg);
            int r0 = iy0 - im, r1 = im + iy1;
            int H = bmp.Height;
            int wrap = ia?.Dp.Wrap ?? 4;
            if (wrap == 4) {
                if (H < r0 || r1 < 0) { r0 = 0; r1 = 0; }
                else { if (r0 < 0) r0 = 0; if (r1 > H) r1 = H; }
            } else {
                if (r0 < 0 || H < r0) r0 = 0;
                if (r1 < 0 || r1 > H) r1 = H;
            }
            src = new RectangleF (x0, y0 - (float) r0, w, h);
            GdipFrame f = bmp;
            if (ia?.Recolor != null && ia.Recolor.HasRecoloring (ColorAdjustType.Bitmap)) {
                ia.Recolor.Flush ();
                f = ia.Recolor.Apply (bmp, ColorAdjustType.Bitmap);
            }
            int n = Math.Max (0, r1 - r0);
            var px = new uint [bmp.Width * n];
            if (n > 0) {
                GdipFrame c = f.Format == PixelFormat.Format32bppPArgb ? f : GdipPixels.Convert (f, new Rectangle (0, r0, f.Width, n), PixelFormat.Format32bppPArgb, false);
                int baseRow = f.Format == PixelFormat.Format32bppPArgb ? r0 : 0;
                for (int yy = 0; yy < n; yy++) {
                    int o = (baseRow + yy) * c.Stride;
                    for (int xx = 0; xx < bmp.Width; xx++, o += 4)
                        px [yy * bmp.Width + xx] = (uint) (c.Bits [o] | c.Bits [o + 1] << 8 | c.Bits [o + 2] << 16 | c.Bits [o + 3] << 24);
                }
            }
            return new DpBitmapSrc (bmp.Width, n, px);
        }

        // ---- DpDriver::DrawImage @180030730 ----------------------------------------------------------

        void DriverDrawImage (DpBitmapSrc src, Rectangle drawBounds, DpImageAttr ia, PointF[] pts, RectangleF srcRect)
        {
            GpMatrix m = GpMatrix.CreateIdentity ();
            if (!InferAffine (pts, srcRect, ref m)) m = new GpMatrix { M11 = 1, M22 = 1 };
            m = GpMatrix.Multiply (m, WorldToDevice);
            float x = srcRect.X, y = srcRect.Y, xr = srcRect.Width + x, yb = srcRect.Height + y;
            float c0x = x, c0y = y, c2x = xr, c2y = yb;
            m.Transform (ref c0x, ref c0y);
            m.Transform (ref c2x, ref c2y);
            var dstRect = new RectangleF (c0x, c0y, c2x - c0x, c2y - c0y);
            GpClip clip = Clip;
            GpScan scan = NewScan ();
            GpSpan span = CreateImageSpan (src, scan, m, ia, (int) _ctx.Interpolation, srcRect, dstRect, pts);
            if (span == null) return;
            if (1.1920929e-07f < srcRect.Width && 1.1920929e-07f < srcRect.Height) {
                var poly = new PointF [] { new PointF (x, y), new PointF (srcRect.Width + x, y), new PointF (srcRect.Width + x, srcRect.Height + y), new PointF (x, srcRect.Height + y) };
                GpRaster.FillPath (poly, new byte [] { 0, 1, 1, 0x81 }, 4, m, false, GpRaster.AntialiasMode (_ctx.Smoothing), span, clip, drawBounds);
            }
            scan.End ();
        }

        /// <summary>CreateOutputSpan @180030378.</summary>
        GpSpan CreateImageSpan (DpBitmapSrc src, GpScan scan, in GpMatrix m, DpImageAttr ia, int interp, RectangleF srcRect, RectangleF dstRect, PointF[] pts)
        {
            if (IsIntegerTranslate (m)) interp = 3;
            bool ts = (m.Complexity & ~3) == 0;
            if (interp == 7 || interp == 2) {
                if (ts) {
                    var s = GpStretchSpan.Create (true, scan, src, ia, srcRect, dstRect);
                    if (s != null) return s;
                    return new GpBilinearSpan (scan, src, m, ia);
                }
                return new GpBicubicSpan (scan, src, WorldToDevice, ia, pts, srcRect);
            }
            if (interp == 4) return new GpBicubicSpan (scan, src, WorldToDevice, ia, pts, srcRect);
            if (interp == 5) return new GpNearestSpan (scan, src, WorldToDevice, ia, pts, srcRect);
            if (interp == 6 && ts) {
                var s = GpStretchSpan.Create (false, scan, src, ia, srcRect, dstRect);
                if (s != null) return s;
                return new GpBilinearSpan (scan, src, m, ia);
            }
            if (IsIntegerTranslate (m) && (ia.Wrap & ~4) == 0) return new GpIdentitySpan (scan, src, m, ia);
            return new GpBilinearSpan (scan, src, m, ia);
        }
    }
}
