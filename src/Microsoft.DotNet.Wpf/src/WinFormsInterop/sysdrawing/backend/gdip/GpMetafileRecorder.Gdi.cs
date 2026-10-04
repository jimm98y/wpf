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

        // GpGraphics::DrawString: the string's measured bounds (MeasureString), through world to device.
        RectangleF? StringBounds(string s, Font font, RectangleF layout, StringFormat format)
        {
            RectangleF box;
            using (var probe = GpRegionProbe.Graphics())
            {
                probe.PageUnit = _state.PageUnit == GraphicsUnit.World ? GraphicsUnit.Display : _state.PageUnit;
                probe.PageScale = _state.PageScale;
                SizeF size = probe.MeasureString(s, font, new SizeF(layout.Width, layout.Height), format);
                float x = layout.X, y = layout.Y;
                StringAlignment a = format?.Alignment ?? StringAlignment.Near;
                StringAlignment la = format?.LineAlignment ?? StringAlignment.Near;
                if (layout.Width > 0f)
                {
                    if (a == StringAlignment.Center) x += (layout.Width - size.Width) / 2f;
                    else if (a == StringAlignment.Far) x += layout.Width - size.Width;
                }
                if (layout.Height > 0f)
                {
                    if (la == StringAlignment.Center) y += (layout.Height - size.Height) / 2f;
                    else if (la == StringAlignment.Far) y += layout.Height - size.Height;
                }
                box = new RectangleF(x, y, size.Width, size.Height);
            }
            float l = box.X, t = box.Y, r = box.Right, b = box.Bottom;
            GpMat m = WorldToDevice;
            m.TransformBounds(ref l, ref t, ref r, ref b);
            return new RectangleF(l, t, r - l, b - t);
        }

        // GpGraphics::DrawDriverString: the glyph positions' box, grown by the font's em height.
        RectangleF? DriverStringBounds(ushort[] text, Font font, PointF[] positions, int flags, Matrix matrix)
        {
            if (positions == null || positions.Length == 0) return null;
            float em = font.Size;
            float l = float.MaxValue, t = float.MaxValue, r = -float.MaxValue, b = -float.MaxValue;
            int n = (flags & 4) != 0 ? 1 : Math.Min(positions.Length, text.Length);
            for (int i = 0; i < n; i++)
            {
                PointF p = positions[i];
                l = Math.Min(l, p.X); r = Math.Max(r, p.X + em);
                t = Math.Min(t, p.Y - em); b = Math.Max(b, p.Y + em * 0.25f);
            }
            GpMat m = WorldToDevice;
            if (matrix != null) m = GpMat.Multiply(GpMat.From(matrix), m);
            m.TransformBounds(ref l, ref t, ref r, ref b);
            return new RectangleF(l, t, r - l, b - t);
        }

        void GdiClear(Color c, RectangleF b) { }
        void GdiFillRects(Brush brush, RectangleF[] rects) { }
        void GdiDrawRects(Pen pen, RectangleF[] rects) { }
        void GdiFillPath(Brush brush, GraphicsPath path) { }
        void GdiDrawPath(Pen pen, GraphicsPath path) { }
        void GdiFillRegion(Brush brush, Region region) { }
        void GdiDrawImage(Image image, RectangleF src, GpMat m, ImageAttributes ia) { }
        void GdiDrawString(string s, Font font, RectangleF layout, StringFormat format, Brush brush) { }
        void GdiDrawDriverString(ushort[] text, Font font, Brush brush, PointF[] positions, int flags, Matrix matrix) { }
        void GdiTransformChanged() { }
        void GdiClipRect(RectangleF r, CombineMode mode) { }
        void GdiClipPath(GraphicsPath path, CombineMode mode) { }
        void GdiClipRegion(Region region, CombineMode mode) { }
        void GdiResetClip() { }
        void GdiOffsetClip(float dx, float dy) { }
        void GdiSave() { }
        void GdiRestore(int depth) { }
        void GdiEnd() { }
    }
}
