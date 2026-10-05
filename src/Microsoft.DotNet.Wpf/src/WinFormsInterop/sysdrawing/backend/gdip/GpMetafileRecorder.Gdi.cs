// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The down-level half of a recording: what GDI+'s metafile driver (Globals::MetaDriver) renders into
// the EMF DC for an EmfPlusDual or EmfOnly metafile, so a GDI-only reader sees the drawing; and the
// text bounds the Record* calls are given.
//

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed partial class GpMetafileRecorder
    {
        bool Gdi => _type != EmfType.EmfPlusOnly;

        // GpGraphics::DrawString: the string's measured bounds (GpGraphics::MeasureString, which on a
        // metafile is always FullTextImager's), through world to device.
        RectangleF? StringBounds(string s, Font font, RectangleF layout, StringFormat format)
        {
            RectangleF box;
            if (!GpGraphics.MeasureStringFor(s, font, EmWorld(font), layout, format, GpMatrix.CreateIdentity(), true, out box, out _, out _))
            {
                using (var probe = GpRegionProbe.Graphics())
                {
                    probe.PageUnit = _state.PageUnit == GraphicsUnit.World ? GraphicsUnit.Display : _state.PageUnit;
                    probe.PageScale = _state.PageScale;
                    SizeF size = probe.MeasureString(s, font, new SizeF(layout.Width, layout.Height), format);
                    box = new RectangleF(layout.X, layout.Y, size.Width, size.Height);
                }
            }
            float l = box.X, t = box.Y, r = box.Right, b = box.Bottom;
            GpMat m = WorldToDevice;
            m.TransformBounds(ref l, ref t, ref r, ref b);
            return new RectangleF(l, t, r - l, b - t);
        }

        /// <summary>RecordEmfPlusDrawDriverString @1800eb628 -> DriverStringImager::MeasureString
        /// @1800eaf08: from the first origin, each glyph's cell -- its design advance across, the
        /// family's cell ascent above and descent below, through the record's matrix -- at its
        /// origin, the union through world to device. (The vertical and realized-advance layouts
        /// are measured as the plain one.)</summary>
        RectangleF? DriverStringBounds(ushort[] text, Font font, PointF[] positions, int flags, Matrix matrix)
        {
            if (positions == null || positions.Length == 0) return null;
            string family = font.FontFamily.Name;
            var face = Microsoft.Wpf.Interop.WebGpu.Composition.Text.GdiPlusText.Face(family, (int)font.Style & 3);
            GpFontFamily.Metrics? mm = GpFontFamily.Get(family, (FontStyle)((int)font.Style & 3));
            float em = EmWorld(font);
            int upemCell = mm?.Em ?? 2048;
            float k = em / upemCell;
            float asc = (mm?.Ascent ?? (int)(upemCell * 0.9f)) * k, desc = (mm?.Descent ?? (int)(upemCell * 0.2f)) * k;
            float l = positions[0].X, r = l, t = positions[0].Y, b = t;
            int n = (flags & 4) != 0 ? 1 : Math.Min(positions.Length, text.Length);
            GpMat? gm = matrix != null ? GpMat.From(matrix) : (GpMat?)null;
            for (int i = 0; i < n; i++)
            {
                if (text[i] == 0xffff) continue;
                int gid = (flags & 1) != 0 && face != null ? face.GlyphIndex((char)text[i]) : text[i];
                float adv = face != null ? face.DesignAdvance(gid) * (em / face.UnitsPerEmForHinting) : em;
                float x0 = 0f, y0 = -asc, x1 = adv, y1 = desc;
                if (gm.HasValue) gm.Value.TransformBounds(ref x0, ref y0, ref x1, ref y1);
                PointF p = positions[i];
                x0 += p.X; x1 += p.X; y0 += p.Y; y1 += p.Y;
                if (x0 <= l) l = x0;
                if (y0 <= t) t = y0;
                if (r <= x1) r = x1;
                if (b <= y1) b = y1;
            }
            GpMat m = WorldToDevice;
            m.TransformBounds(ref l, ref t, ref r, ref b);
            return new RectangleF(l, t, r - l, b - t);
        }

        // ---- each GpGraphics verb's down-level half (DownLevel, GpGraphics +0x68) -------------------
        //
        // The verb has recorded its EMF+ record; when the metafile is not EmfPlusOnly it renders through
        // the metafile driver as GpGraphics::Render* do: the device bounds floored and ceiled into the
        // draw rectangle, nothing when the visible clip hides it.

        bool TotallyClipped(Rectangle r)
        {
            if (_state.Clip == null && _state.ContainerClip == null) return false;
            DpRegion vis = VisibleClip();
            return vis.IsEmpty || !vis.Intersects(r);
        }

        /// <summary>GpGraphics::Clear: the graphics' own bounds filled under an identity matrix.</summary>
        void GdiClear(Color c, RectangleF b)
        {
            if (!Gdi) return;
            var r = new Rectangle((int)b.X, (int)b.Y, (int)b.Width, (int)b.Height);
            if (TotallyClipped(r)) return;
            lock (GpMetaDriverState.Lock)
                using (var brush = new SolidBrush(c))
                    DriverFillRects(r, new[] { new RectangleF(r.X, r.Y, r.Width, r.Height) }, brush, GpMatrix.CreateIdentity());
        }

        /// <summary>GpGraphics::FillRects: RenderFillRects, or with a rotation each rectangle a path.</summary>
        void GdiFillRects(Brush brush, RectangleF[] rects)
        {
            if (!Gdi) return;
            Rectangle? draw = DrawRect(RectsBounds(rects, null));
            if (!draw.HasValue || TotallyClipped(draw.Value)) return;
            lock (GpMetaDriverState.Lock)
            {
                if ((WorldToDevice.Complexity & ~3) == 0) { DriverFillRects(draw.Value, rects, brush); return; }
                foreach (RectangleF q in rects)
                {
                    if (!(1.1920928955078125e-07f < q.Width) || !(1.1920928955078125e-07f < q.Height)) continue;
                    var p = new GpPath(System.Drawing.Drawing2D.FillMode.Alternate);
                    p.AddRects(new[] { q });
                    DriverFillPath(draw.Value, p, brush);
                }
            }
        }

        /// <summary>GpGraphics::DrawRects: each rectangle a closed path, stroked; the draw rectangle the
        /// bounds grown by a pixel.</summary>
        void GdiDrawRects(Pen pen, RectangleF[] rects)
        {
            if (!Gdi) return;
            RectangleF? b = RectsBounds(rects, pen);
            if (!b.HasValue) return;
            RectangleF g = b.Value;
            g = new RectangleF(g.X - 1.001f, g.Y - 1.001f, g.Width + 2.002f, g.Height + 2.002f);
            Rectangle? draw = DrawRect(g);
            if (!draw.HasValue || TotallyClipped(draw.Value)) return;
            lock (GpMetaDriverState.Lock)
                foreach (RectangleF q in rects)
                {
                    if (!(1.1920928955078125e-07f < q.Width) || !(1.1920928955078125e-07f < q.Height)) continue;
                    var p = new GpPath(System.Drawing.Drawing2D.FillMode.Alternate);
                    p.AddRects(new[] { q });
                    DriverStrokePath(draw.Value, p, pen);
                }
        }

        /// <summary>GpGraphics::FillPath (and FillPolygon, FillEllipse, ... ) -> RenderFillPath.</summary>
        void GdiFillPath(Brush brush, GraphicsPath path)
        {
            if (!Gdi) return;
            Rectangle? draw = DrawRect(PathBounds(path, null));
            if (!draw.HasValue || TotallyClipped(draw.Value)) return;
            lock (GpMetaDriverState.Lock)
                DriverFillPath(draw.Value, path.gp, brush);
        }

        /// <summary>GpGraphics::DrawPath (and DrawLines, DrawEllipse, ... ) -> RenderDrawPath.</summary>
        void GdiDrawPath(Pen pen, GraphicsPath path)
        {
            if (!Gdi) return;
            Rectangle? draw = DrawRect(PathBounds(path, pen));
            if (!draw.HasValue || TotallyClipped(draw.Value)) return;
            lock (GpMetaDriverState.Lock)
                DriverStrokePath(draw.Value, path.gp, pen);
        }

        /// <summary>GpGraphics::FillRegion -> RenderFillRegion: the device region within the metafile's
        /// bounds.</summary>
        void GdiFillRegion(Brush brush, Region region, RectangleF bounds)
        {
            if (!Gdi) return;
            Rectangle? draw = DrawRect(bounds);
            if (!draw.HasValue || TotallyClipped(draw.Value)) return;
            GpRegion world = region.gp.Clone();
            DpRegion dev = world.Device(DeviceMatrix);
            RectangleF mb = MetafileDeviceBounds();
            dev = DpRegion.Combine(DpRegion.FromRect((int)mb.X, (int)mb.Y, (int)mb.Width, (int)mb.Height), dev, DpRegion.Op.And);
            if (dev.IsEmpty) return;
            lock (GpMetaDriverState.Lock)
                DriverFillRegion(dev, brush);
        }


        // GpGraphics keeps the transform for itself; nothing reaches the HDC (the driver draws in
        // device coordinates).
        void GdiTransformChanged() { }

        // ---- the application clip, in device coordinates (DpContext +0x1c0) -----------------------

        void CombineClip(GpRegion world, CombineMode mode)
        {
            world.Transform(DeviceMatrix);
            GpRegion cur = _state.Clip == null ? GpRegion.Infinite() : _state.Clip.Clone();
            cur.Combine(world, mode);
            _state.Clip = cur;
        }

        void GdiClipRect(RectangleF r, CombineMode mode) => CombineClip(GpRegion.FromRect(r), mode);

        void GdiClipPath(GraphicsPath path, CombineMode mode)
            => CombineClip(GpRegion.FromPath(path.gp.PointArray(), path.gp.TypeArray(), path.gp.FillMode), mode);

        void GdiClipRegion(Region region, CombineMode mode) => CombineClip(region.gp.Clone(), mode);

        void GdiResetClip() => _state.Clip = null;

        /// <summary>GpGraphics::OffsetClip: the offset taken to device units.</summary>
        void GdiOffsetClip(float dx, float dy)
        {
            if (_state.Clip == null) return;
            PointF d = DeviceMatrix.VectorTransform(new PointF(dx, dy));
            GpRegion c = _state.Clip.Clone();
            c.Offset(d.X, d.Y);
            _state.Clip = c;
        }

        void GdiSave() { }
        void GdiRestore(int depth) { }

        /// <summary>The end of the Graphics (GpGraphics::~GpGraphics): the HDC's saved level put back.</summary>
        void GdiEnd() => ResetHdc();
    }
}
