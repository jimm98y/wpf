// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A GDI+ Graphics on a bitmap, in managed code: GpGraphics + DpContext + DpDriver for an EpScanBitmap
// surface, read out of gdiplus.dll (arm64, public PDB). System.Drawing.Graphics.FromImage hands its
// verbs here when the image is a managed Bitmap, and the pixels come out as gdiplus.dll writes them.
//
//   DpContext (0x310 bytes)                +0x14 AntiAliasMode, +0x18 TextRenderingHint,
//                                         +0x1c CompositingMode, +0x20 CompositingQuality,
//                                         +0x24/+0x28 RenderingOrigin, +0x2c TextContrast,
//                                         +0x30 InterpolationMode, +0x34 PixelOffsetMode,
//                                         +0x38 PageUnit, +0x3c PageScale, +0x40/+0x44 page
//                                         multipliers, +0x68 world, +0x90 world-to-device,
//                                         +0xc0 container, +0x130 visible clip, +0x198 container
//                                         clip, +0x1c0 app clip (a GpRegion), +0x1f8 its device form
//   DpContext::UpdateWorldToDeviceMatrix @180037fe0   world x page multipliers (copied as is when
//                                         both are 1); PixelOffsetMode Half or HighQuality subtract
//                                         0.5 from the translation; then the container transform
//                                         when it is not the identity
//   DpContext::GetPageMultipliers @180037ed0   Display = 1 on a bitmap, Pixel = scale, Point = dpi
//                                         x scale / 72, Inch, Document / 300, Millimeter / 25.4
//   GpGraphics::Save @18007a588           a copy of the context becomes current; Restore
//                                         @18007a4b8 finds the context with that id and makes its
//                                         parent current again
//   GpGraphics::BeginContainer @180078d18 / @180078fc0   a new context with its quality settings,
//                                         world, page unit and app clip at their defaults; the
//                                         container transform maps src (in srcUnit) onto dst under
//                                         the old world-to-device (or, without rectangles, undoes
//                                         the old page multipliers); the container clip is the
//                                         old app clip in device space AND the old container clip
//   GpGraphics::Clear @180075440          a SourceCopy, aliased fill of the surface in device space
//   GpGraphics::FillRects / DpDriver::FillRects @1800bd8b0   axis-aligned and aliased: each rect's
//                                         corners transformed, RasterizerCeiling of min and max
//   GpGraphics::FillPath / RenderFillPath / DpDriver::FillPath @180026d80   rasterize the path
//   GpGraphics::CombineClip @180077830    the clip is kept in DEVICE space: a rect transformed
//                                         (TransformRect when axis-aligned, else a 4-point path)
//                                         combined into the app clip, which is then rasterized and
//                                         intersected with the surface
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpGraphics
    {
        public readonly GdipFrame Frame;
        readonly Rectangle _surface;

        /// <summary>One DpContext: everything Save copies and Restore brings back.</summary>
        sealed class Context
        {
            public int Tag;                      // the caller's token (GraphicsState / GraphicsContainer)
            public bool IsContainer;
            public GpMatrix World = GpMatrix.CreateIdentity ();
            public GraphicsUnit PageUnit = GraphicsUnit.Display;
            public float PageScale = 1f;
            public SmoothingMode Smoothing = SmoothingMode.None;
            public TextRenderingHint TextHint = TextRenderingHint.SystemDefault;
            public CompositingMode Compositing = CompositingMode.SourceOver;
            public CompositingQuality CompositingQuality = CompositingQuality.Default;
            public Point RenderingOrigin;
            public int TextContrast = 4;
            public InterpolationMode Interpolation = InterpolationMode.Bilinear;
            public PixelOffsetMode PixelOffset = PixelOffsetMode.Default;
            public GpMatrix Container = GpMatrix.CreateIdentity ();
            public GpRegion AppClip;             // device space; null = infinite
            public DpRegion ContainerClip;       // null = infinite

            public Context Copy ()
            {
                var c = (Context) MemberwiseClone ();
                c.AppClip = AppClip?.Clone ();
                return c;
            }
        }

        Context _ctx = new Context ();
        readonly List<Context> _saved = new List<Context> ();

        public float DpiX, DpiY;
        public GpMatrix WorldToDevice;
        /// <summary>A larger device this surface is a piece of, which gradients are walked in
        /// (DriverPrint's bands: GDI+ evaluates the gradient at the band's absolute coordinates, its
        /// matrix the world to that device): that matrix and where this surface's pixel (0, 0) lies
        /// on it. Null for a surface that is its own device.</summary>
        public (GpMatrix WorldToDevice, int X, int Y)? SpanDevice;
        /// <summary>The same for an image: its source-to-device matrix on the larger device (what
        /// DpDriver::DrawImage builds there) and where this surface's pixel (0, 0) lies on it.</summary>
        public (GpMatrix WorldToDevice, PointF[] WorldPoints, int X, int Y)? ImageSpanDevice;
        DpRegion _visibleClip;

        public GpGraphics (GdipFrame frame)
        {
            Frame = frame;
            _surface = new Rectangle (0, 0, frame.Width, frame.Height);
            DpiX = frame.DpiX; DpiY = frame.DpiY;
            UpdateWorldToDevice ();
            UpdateVisibleClip ();
        }

        /// <summary>A context with no pixels behind it: the clip and its save/restore stack of a
        /// surface <paramref name="surface"/> (a printed page's device rectangle), nothing drawn.</summary>
        public GpGraphics (Rectangle surface, float dpiX, float dpiY)
        {
            _surface = surface;
            DpiX = dpiX; DpiY = dpiY;
            UpdateWorldToDevice ();
            UpdateVisibleClip ();
        }

        /// <summary>BeginContainer for a clip-only context: the clip starts over inside it, the outer
        /// visible clip still bounding it; the transform is the caller's to set.</summary>
        public void PushClipContainer (int tag) => PushContainer (tag, GpMatrix.CreateIdentity ());

        /// <summary>The visible clip (surface, container clips and app clip) in device space.</summary>
        public DpRegion VisibleClipRegion => _visibleClip;

        // ---- state ----------------------------------------------------------------------------------

        public GpMatrix World => _ctx.World;
        public GraphicsUnit PageUnit => _ctx.PageUnit;
        public float PageScale => _ctx.PageScale;
        public SmoothingMode Smoothing { get => _ctx.Smoothing; set => _ctx.Smoothing = value; }
        public TextRenderingHint TextHint { get => _ctx.TextHint; set => _ctx.TextHint = value; }
        public CompositingMode Compositing { get => _ctx.Compositing; set => _ctx.Compositing = value; }
        public CompositingQuality CompositingQuality { get => _ctx.CompositingQuality; set => _ctx.CompositingQuality = value; }
        public Point RenderingOrigin { get => _ctx.RenderingOrigin; set => _ctx.RenderingOrigin = value; }
        public int TextContrast { get => _ctx.TextContrast; set => _ctx.TextContrast = value; }
        public InterpolationMode Interpolation { get => _ctx.Interpolation; set => _ctx.Interpolation = value; }
        public PixelOffsetMode PixelOffset => _ctx.PixelOffset;
        public GpRegion AppClip => _ctx.AppClip;

        // ---- transforms -----------------------------------------------------------------------------

        public void GetPageMultipliers (GraphicsUnit unit, float scale, out float mx, out float my)
        {
            switch (unit) {
            case GraphicsUnit.Display: mx = my = 1f; return;
            case GraphicsUnit.Pixel: mx = my = scale; return;
            case GraphicsUnit.Inch: mx = DpiX * scale; my = DpiY * scale; return;
            case GraphicsUnit.Point: mx = DpiX * scale / 72f; my = DpiY * scale / 72f; return;
            case GraphicsUnit.Document: mx = DpiX * scale / 300f; my = DpiY * scale / 300f; return;
            case GraphicsUnit.Millimeter: mx = DpiX * scale / 25.4f; my = DpiY * scale / 25.4f; return;
            default: mx = DpiX * scale / 100f; my = DpiY * scale / 100f; return;
            }
        }

        public void GetPageMultipliers (out float mx, out float my) => GetPageMultipliers (_ctx.PageUnit, _ctx.PageScale, out mx, out my);

        public void UpdateWorldToDevice ()
        {
            GetPageMultipliers (out float mx, out float my);
            GpMatrix m = _ctx.World;
            if (mx != 1f || my != 1f) {
                m.M11 *= mx; m.M12 *= my; m.M21 *= mx; m.M22 *= my; m.Dx *= mx; m.Dy *= my;
                m.Complexity = m.ComputeComplexity ();
            }
            if (_ctx.PixelOffset == PixelOffsetMode.Half || _ctx.PixelOffset == PixelOffsetMode.HighQuality) {
                m.Dx -= 0.5f; m.Dy -= 0.5f;
                m.Complexity |= 1;
            }
            if (_ctx.Container.Complexity != 0) m = GpMatrix.Multiply (m, _ctx.Container);
            WorldToDevice = m;
        }

        public void SetWorld (in GpMatrix m) { _ctx.World = m; UpdateWorldToDevice (); }

        public void SetPage (GraphicsUnit u, float s) { _ctx.PageUnit = u; _ctx.PageScale = s; UpdateWorldToDevice (); }

        public void SetPixelOffset (PixelOffsetMode m)
        {
            if (m == _ctx.PixelOffset) return;
            _ctx.PixelOffset = m;
            UpdateWorldToDevice ();
        }

        /// <summary>The world-to-device matrix's inverse (DpContext::GetDeviceToWorld); false when
        /// it has none.</summary>
        public bool DeviceToWorld (out GpMatrix m)
        {
            m = WorldToDevice;
            return m.Invert ();
        }

        // ---- save / restore / containers ----------------------------------------------------------

        public void Save (int tag)
        {
            Context saved = _ctx;
            _saved.Add (saved);
            _ctx = saved.Copy ();
            _ctx.Tag = tag;
            _ctx.IsContainer = false;
        }

        /// <summary>Restore: back to the state the context made by Save/BeginContainer(<paramref name="tag"/>)
        /// was copied from; an unknown tag is ignored.</summary>
        public void Restore (int tag)
        {
            // The current context and the saved ones, newest first: find the one carrying the tag.
            int k;
            if (_ctx.Tag == tag && _saved.Count > 0) k = _saved.Count;
            else {
                k = -1;
                for (int i = _saved.Count - 1; i >= 1; i--)
                    if (_saved [i].Tag == tag) { k = i; break; }
                if (k < 0) return;
            }
            _ctx = _saved [k - 1];
            _saved.RemoveRange (k - 1, _saved.Count - (k - 1));
            UpdateWorldToDevice ();
            UpdateVisibleClip ();
        }

        /// <summary>BeginContainer(dst, src, unit): src (in <paramref name="srcUnit"/>) mapped onto dst
        /// under the current world-to-device. False when the mapping is degenerate (GDI+ then returns
        /// a zero container and draws on unchanged).</summary>
        public bool BeginContainer (int tag, RectangleF dst, RectangleF src, GraphicsUnit srcUnit)
        {
            GetPageMultipliers (srcUnit, 1f, out float mx, out float my);
            var s = new RectangleF (src.X * mx, src.Y * my, src.Width * mx, src.Height * my);
            if (!InferAffine (dst, s, out GpMatrix c)) return false;
            PushContainer (tag, GpMatrix.Multiply (c, WorldToDevice));
            return true;
        }

        /// <summary>BeginContainer(): the page multipliers undone, under the current world-to-device.</summary>
        public void BeginContainer (int tag)
        {
            GpMatrix c = GpMatrix.CreateIdentity ();
            GetPageMultipliers (out float mx, out float my);
            c.Scale (1f / mx, 1f / my, false);
            PushContainer (tag, GpMatrix.Multiply (c, WorldToDevice));
        }

        void PushContainer (int tag, in GpMatrix container)
        {
            Context old = _ctx;
            DpRegion clip = old.AppClip == null ? null : old.AppClip.Device (GpMatrix.CreateIdentity ());
            if (old.ContainerClip != null)
                clip = clip == null ? old.ContainerClip : DpRegion.Combine (clip, old.ContainerClip, DpRegion.Op.And);
            _saved.Add (old);
            _ctx = new Context {
                Tag = tag, IsContainer = true,
                RenderingOrigin = old.RenderingOrigin,
                Container = container,
                ContainerClip = clip,
            };
            UpdateWorldToDevice ();
            UpdateVisibleClip ();
        }

        public void EndContainer (int tag) => Restore (tag);

        // GpMatrix::InferAffineMatrix(RectF dst, RectF src) @1800346a8: the matrix taking src onto
        // dst, scale from the edge differences and the translation from the right/bottom edges.
        public static bool InferAffine (RectangleF dst, RectangleF src, out GpMatrix m)
        {
            m = GpMatrix.CreateIdentity ();
            float sl = src.X, st = src.Y, sr = src.Width + sl, sb = src.Height + st;
            float dl = dst.X, dt = dst.Y, dr = dst.Width + dl, db = dst.Height + dt;
            if (sl == sr || st == sb) return false;
            float sx = (dr - dl) / (sr - sl), sy = (db - dt) / (sb - st);
            m = new GpMatrix (sx, 0, 0, sy, dr - sx * sr, db - sy * sb);
            return true;
        }

        // ---- clipping --------------------------------------------------------------------------------

        public GpClip Clip => new GpClip (_visibleClip);

        void UpdateVisibleClip ()
        {
            DpRegion v = DpRegion.FromRect (_surface.X, _surface.Y, _surface.Width, _surface.Height);
            if (_ctx.ContainerClip != null) v = DpRegion.Combine (_ctx.ContainerClip, v, DpRegion.Op.And);
            if (_ctx.AppClip != null) v = DpRegion.Combine (_ctx.AppClip.Device (GpMatrix.CreateIdentity ()), v, DpRegion.Op.And);
            _visibleClip = v;
        }

        public void ResetClip () { _ctx.AppClip = null; UpdateVisibleClip (); }

        /// <summary>CombineClip(rect): the rectangle in world space, combined in device space.</summary>
        public void CombineClip (RectangleF r, CombineMode mode)
        {
            GpRegion leaf;
            GpMatrix m = WorldToDevice;
            if (m.IsTranslateScale) leaf = GpRegion.FromRect (TransformRect (m, r));
            else {
                var p = new PointF [] { new PointF (r.X, r.Y), new PointF (r.Right, r.Y), new PointF (r.Right, r.Bottom), new PointF (r.X, r.Bottom) };
                m.Transform (p);
                leaf = GpRegion.FromPath (p, new byte [] { 0, 1, 1, 0x81 }, FillMode.Alternate);
            }
            Combine (leaf, mode);
        }

        public void CombineClip (PointF[] pts, byte[] types, FillMode fill, CombineMode mode)
        {
            var p = (PointF[]) pts.Clone ();
            WorldToDevice.Transform (p);
            Combine (GpRegion.FromPath (p, types, fill), mode);
        }

        /// <summary>A region given in world space.</summary>
        public void CombineClip (GpRegion worldRegion, CombineMode mode)
        {
            GpRegion d = worldRegion.Clone ();
            d.Transform (WorldToDevice);
            Combine (d, mode);
        }

        /// <summary>SetClip(Graphics): the other's clip, already in device space.</summary>
        public void CombineDeviceClip (GpRegion deviceRegion, CombineMode mode) => Combine (deviceRegion?.Clone () ?? GpRegion.Infinite (), mode);

        void Combine (GpRegion leaf, CombineMode mode)
        {
            if (mode == CombineMode.Replace || _ctx.AppClip == null && mode == CombineMode.Intersect) _ctx.AppClip = leaf;
            else {
                GpRegion cur = _ctx.AppClip ?? GpRegion.Infinite ();
                _ctx.AppClip = GpRegion.Combine (cur, leaf, mode);
            }
            if (_ctx.AppClip != null && _ctx.AppClip.Type == GpRegion.NodeInfinite) _ctx.AppClip = null;
            UpdateVisibleClip ();
        }

        public void OffsetClip (float dx, float dy)
        {
            if (_ctx.AppClip == null) return;
            PointF v = WorldToDevice.VectorTransform (new PointF (dx, dy));
            _ctx.AppClip.Offset (v.X, v.Y);
            UpdateVisibleClip ();
        }

        /// <summary>Graphics.Clip: the app clip back in world space.</summary>
        public GpRegion GetClip ()
        {
            if (_ctx.AppClip == null) return GpRegion.Infinite ();
            GpRegion r = _ctx.AppClip.Clone ();
            GpMatrix inv = WorldToDevice;
            if (inv.Invert ()) r.Transform (inv);
            return r;
        }

        /// <summary>The visible clip's device bounds (surface AND container clip AND app clip).</summary>
        public Rectangle VisibleDeviceBounds => _visibleClip.Bounds;

        public bool VisibleClipEmpty => _visibleClip.IsEmpty;

        public bool IsVisibleDevice (int x, int y) => _visibleClip.Contains (x, y);

        public bool IsVisibleDevice (Rectangle r) => !_visibleClip.IsEmpty && _visibleClip.Intersects (r);

        /// <summary>A surface that starts at (-dx, -dy) of the device (a DC's visible area): the root
        /// context's container moves everything by the origin.</summary>
        public void SetSurfaceOrigin (float dx, float dy)
        {
            Context root = _saved.Count > 0 ? _saved [0] : _ctx;
            root.Container = new GpMatrix (1, 0, 0, 1, dx, dy);
            UpdateWorldToDevice ();
        }

        static RectangleF TransformRect (in GpMatrix m, RectangleF r)
        {
            float x0 = r.X, y0 = r.Y, x1 = r.Right, y1 = r.Bottom;
            m.Transform (ref x0, ref y0);
            m.Transform (ref x1, ref y1);
            return RectangleF.FromLTRB (Math.Min (x0, x1), Math.Min (y0, y1), Math.Max (x0, x1), Math.Max (y0, y1));
        }

        // ---- the scan --------------------------------------------------------------------------------

        GpScan NewScan () => new GpScan (Frame, _ctx.Compositing, _ctx.CompositingQuality);

        // ---- Clear ----------------------------------------------------------------------------------

        public void Clear (Color color)
        {
            GpClip clip = Clip;
            if (clip.IsEmpty) return;
            var scan = new GpScan (Frame, CompositingMode.SourceCopy, CompositingQuality.Default);
            var span = new SolidSpan (scan, (uint) color.ToArgb ());
            ISpanSink sink = clip.Wrap (span);
            Rectangle r = _surface;
            for (int y = r.Top; y < r.Bottom; y++) sink.OutputSpan (y, r.Left, r.Right);
            scan.End ();
        }

        // ---- fills ----------------------------------------------------------------------------------

        bool Aliased => GpRaster.AntialiasMode (_ctx.Smoothing) == 0;

        /// <summary>GpGraphics::UseDriverRects: an axis-aligned transform and an aliased mode.</summary>
        bool UseDriverRects => WorldToDevice.IsTranslateScale && Aliased;

        /// <summary>Whether this engine can fill with <paramref name="brush"/> (else the caller falls back).</summary>
        public bool CanFill (Brush brush) => brush is SolidBrush || CanFillBrush (brush);

        public bool FillRects (Brush brush, RectangleF[] rects)
        {
            if (!CanFill (brush)) return false;
            var list = new List<RectangleF> ();
            foreach (RectangleF r in rects)
                if (r.X <= r.Width + r.X && r.Y <= r.Height + r.Y) list.Add (r);
            if (list.Count == 0) return true;
            if (!UseDriverRects) {
                var pts = new List<PointF> ();
                var types = new List<byte> ();
                foreach (RectangleF r in list) {
                    pts.Add (new PointF (r.X, r.Y)); pts.Add (new PointF (r.Right, r.Y));
                    pts.Add (new PointF (r.Right, r.Bottom)); pts.Add (new PointF (r.X, r.Bottom));
                    types.Add (0); types.Add (1); types.Add (1); types.Add (0x81);
                }
                return FillPath (brush, pts.ToArray (), types.ToArray (), FillMode.Alternate);
            }
            GpClip clip = Clip;
            if (clip.IsEmpty) return true;
            GpScan scan = NewScan ();
            GpSpan span = CreateSpan (brush, scan, RectsDrawBounds (list));
            if (span == null) return true;
            ISpanSink sink = clip.Wrap (span);
            GpMatrix m = WorldToDevice;
            foreach (RectangleF r in list) {
                if (!(r.Width > 0 && r.Height > 0)) continue;
                float x0 = r.X, y0 = r.Y, x1 = r.X + r.Width, y1 = r.Y + r.Height;
                m.Transform (ref x0, ref y0);
                m.Transform (ref x1, ref y1);
                int ix0 = GpMatrix.RasterizerCeiling (Math.Min (x0, x1)), ix1 = GpMatrix.RasterizerCeiling (Math.Max (x0, x1));
                int iy0 = GpMatrix.RasterizerCeiling (Math.Min (y0, y1)), iy1 = GpMatrix.RasterizerCeiling (Math.Max (y0, y1));
                if (ix1 <= ix0) continue;
                for (int y = iy0; y < iy1; y++) sink.OutputSpan (y, ix0, ix1);
            }
            scan.End ();
            return true;
        }

        public bool FillPath (Brush brush, PointF[] pts, byte[] types, FillMode fill)
        {
            if (!CanFill (brush)) return false;
            if (pts == null || pts.Length < 3) return true;
            GpClip clip = Clip;
            if (clip.IsEmpty) return true;
            RectangleF b = DeviceBounds (pts);
            if (!(MathF.Abs (b.Width) >= 1.1920929e-07f) || !(MathF.Abs (b.Height) >= 1.1920929e-07f)) return true;
            if (!(b.X >= -1073741824f && b.X <= 1073741824f && b.Y >= -1073741824f && b.Y <= 1073741824f)) return true;
            // RenderFillPath: floor of the origin, ceiling of the far edge, plus one.
            int bx = (int) MathF.Floor (b.X), by = (int) MathF.Floor (b.Y);
            int bw = (int) MathF.Ceiling (b.X + b.Width) - bx + 1, bh = (int) MathF.Ceiling (b.Y + b.Height) - by + 1;
            var draw = new Rectangle (bx, by, bw, bh);
            if (!clip.IsVisible (draw)) return true;
            GpScan scan = NewScan ();
            GpSpan span = CreateSpan (brush, scan, draw);
            if (span == null) return true;
            GpRaster.FillPath (pts, types, pts.Length, WorldToDevice, fill == FillMode.Winding, GpRaster.AntialiasMode (_ctx.Smoothing), span, clip, draw);
            scan.End ();
            return true;
        }

        /// <summary>FillRegion: the region rasterized in device space (DpRegion), its scans filled
        /// inside the visible clip.</summary>
        public bool FillRegion (Brush brush, GpRegion worldRegion)
        {
            if (!CanFill (brush)) return false;
            if (_visibleClip.IsEmpty) return true;
            DpRegion d = DpRegion.Combine (worldRegion.Device (WorldToDevice), _visibleClip, DpRegion.Op.And);
            if (d.IsEmpty) return true;
            GpScan scan = NewScan ();
            GpSpan span = CreateSpan (brush, scan, d.Bounds);
            if (span == null) return true;
            // DpRegion::Fill @1800dbc28: band by band, each row's spans left to right.
            foreach (DpRegion.Band b in d.Bands)
                for (int y = b.Top; y < b.Bottom; y++)
                    for (int i = 0; i + 1 < b.X.Length; i += 2) span.OutputSpan (y, b.X [i], b.X [i + 1]);
            scan.End ();
            return true;
        }

        /// <summary>GpGraphics::FillRects' device bounds of the rectangles (their union transformed,
        /// two corners for a translate/scale) through BoundsFToRect @180035210.</summary>
        Rectangle? RectsDrawBounds (List<RectangleF> list)
        {
            RectangleF u0 = list [0];
            float l = u0.X, t = u0.Y, r = u0.Width + l, b = u0.Height + t;
            for (int i = 1; i < list.Count; i++) {
                RectangleF q = list [i];
                if (!(l <= q.X)) l = q.X;
                if (r < q.Width + q.X) r = q.Width + q.X;
                if (!(t <= q.Y)) t = q.Y;
                if (!(q.Height + q.Y <= b)) b = q.Height + q.Y;
            }
            GpMatrix m = WorldToDevice;
            if (m.Complexity != 0) {
                RectangleF tb = GpPathGradientSpans.TransformBounds (m, l, t, r, b);
                l = tb.X; t = tb.Y; r = tb.Right; b = tb.Bottom;
            }
            float w = r - l, h = b - t;
            if (w <= 0.0005960464477539062f) w = 0f;
            if (h <= 0.0005960464477539062f) h = 0f;
            const float Lim = 1073741824f;
            if (!(l >= -Lim && l <= Lim && t >= -Lim && t <= Lim && w >= 0f && w <= Lim && h >= 0f && h <= Lim)) return null;
            int x = (int) MathF.Floor (l), y = (int) MathF.Floor (t);
            return new Rectangle (x, y, (int) MathF.Ceiling (l + w) - x + 1, (int) MathF.Ceiling (t + h) - y + 1);
        }

        RectangleF DeviceBounds (PointF[] pts)
        {
            float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
            foreach (PointF p0 in pts) {
                PointF p = WorldToDevice.Transform (p0);
                l = Math.Min (l, p.X); t = Math.Min (t, p.Y); r = Math.Max (r, p.X); b = Math.Max (b, p.Y);
            }
            return RectangleF.FromLTRB (l, t, r, b);
        }

        // ---- brushes ---------------------------------------------------------------------------------

        GpSpan CreateSpan (Brush brush, GpScan scan, Rectangle? draw)
        {
            switch (brush) {
            case SolidBrush sb:
                return new SolidSpan (scan, (uint) sb.Color.ToArgb ());
            default:
                return CreateBrushSpan (brush, scan, draw);
            }
        }
    }
}
