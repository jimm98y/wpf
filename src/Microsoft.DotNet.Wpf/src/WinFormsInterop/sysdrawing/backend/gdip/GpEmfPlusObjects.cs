// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The EMF+ object serialisations, written as GDI+'s GetData methods write them (gdiplus.dll
// 10.0.26100, arm64, public PDB), with the recorder's version flags (MetafileRecorder +0x6d0 = 2:
// no relative points, no run-length path types):
//
//   GpSolidFill::GetData   @18006e870   GpHatch::GetData        @18006e030
//   GpTexture::GetData     @18006e8d0   GpRectGradient::GetData @18006e5b0
//   GpPathGradient::GetData @18006e0a0  GpPen::GetData          @180071090
//   DpPath::GetData        @1800893e0   MetafilePointData       @180098688
//   MetafileTypeData       @180098cf0   GpRegion::GetData       @1800738c0
//   GpFont::GetData        @180086670   GpStringFormat::GetData @18004d850
//   GpImageAttributes::GetData @18008efa0
//   GpCustomLineCap::GetData @18007b760 GpAdjustableArrowCap::GetData @18007b6a0
//   CopyOnWriteBitmap::GetData @180080d90  GpMetafile::GetData  @1800933b0
//
// Every object starts with the EMF+ graphics version, 0xDBC01002.
//
// What a managed object does not expose publicly is read through GpObjectState (one file, so the
// seam is in one place).
//

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace System.Drawing.WebGpuBackend.Gdip
{
    /// <summary>EMF+ object types (the high byte of an EmfPlusObject record's flags).</summary>
    internal enum EmfPlusObjectType
    {
        Invalid = 0, Brush = 1, Pen = 2, Path = 3, Region = 4, Image = 5, Font = 6,
        StringFormat = 7, ImageAttributes = 8, CustomLineCap = 9,
    }

    internal static class GpEmfPlusObjects
    {
        public const int Version = unchecked((int)0xDBC01002);

        // ---- point and rectangle data (MetafilePointData / MetafileRectData) --------------------

        /// <summary>GDI+'s int16 conversion: (short)GpRound(v).</summary>
        static short To16(float v) => unchecked((short)GpMetafileFormat.Round(v));

        /// <summary>IsPoint16Equal: within FLT_MIN of the 16-bit value.</summary>
        static bool Same(float v, short s)
        {
            float d = v - (float)s;
            return d > -1.1754943508222875e-36f && d < 1.1754943508222875e-36f;
        }

        /// <summary>The points as the recorder writes them: int16 pairs (flag 0x4000) when every
        /// coordinate is exactly one, else floats.</summary>
        public static byte[] PointData(PointF[] pts, int offset, int count, out int flags)
        {
            flags = 0;
            if (count <= 0) return new byte[0];
            var s16 = new short[count * 2];
            bool all = true;
            for (int i = 0; i < count && all; i++)
            {
                PointF p = pts[offset + i];
                short x = To16(p.X), y = To16(p.Y);
                if (!Same(p.X, x) || !Same(p.Y, y)) all = false;
                s16[i * 2] = x; s16[i * 2 + 1] = y;
            }
            if (all)
            {
                var b = new byte[count * 4];
                for (int i = 0; i < count * 2; i++) Le.W16(b, i * 2, s16[i]);
                flags = 0x4000;
                return b;
            }
            var f = new byte[count * 8];
            for (int i = 0; i < count; i++)
            {
                Le.WF(f, i * 8, pts[offset + i].X);
                Le.WF(f, i * 8 + 4, pts[offset + i].Y);
            }
            return f;
        }

        public static byte[] RectData(RectangleF[] rs, out int flags)
        {
            flags = 0;
            int count = rs?.Length ?? 0;
            if (count <= 0) return new byte[0];
            var s16 = new short[count * 4];
            bool all = true;
            for (int i = 0; i < count && all; i++)
            {
                RectangleF r = rs[i];
                short x = To16(r.X), y = To16(r.Y), w = To16(r.Width), h = To16(r.Height);
                if (!Same(r.X, x) || !Same(r.Y, y) || !Same(r.Width, w) || !Same(r.Height, h)) all = false;
                s16[i * 4] = x; s16[i * 4 + 1] = y; s16[i * 4 + 2] = w; s16[i * 4 + 3] = h;
            }
            if (all)
            {
                var b = new byte[count * 8];
                for (int i = 0; i < count * 4; i++) Le.W16(b, i * 2, s16[i]);
                flags = 0x4000;
                return b;
            }
            var f = new byte[count * 16];
            for (int i = 0; i < count; i++)
            {
                Le.WF(f, i * 16, rs[i].X); Le.WF(f, i * 16 + 4, rs[i].Y);
                Le.WF(f, i * 16 + 8, rs[i].Width); Le.WF(f, i * 16 + 12, rs[i].Height);
            }
            return f;
        }

        // ---- objects ------------------------------------------------------------------------------

        public static EmfPlusObjectType TypeOf(object o)
        {
            switch (o)
            {
                case Brush _: return EmfPlusObjectType.Brush;
                case Pen _: return EmfPlusObjectType.Pen;
                case GraphicsPath _: return EmfPlusObjectType.Path;
                case Region _: return EmfPlusObjectType.Region;
                case Image _: return EmfPlusObjectType.Image;
                case Font _: return EmfPlusObjectType.Font;
                case StringFormat _: return EmfPlusObjectType.StringFormat;
                case ImageAttributes _: return EmfPlusObjectType.ImageAttributes;
                case CustomLineCap _: return EmfPlusObjectType.CustomLineCap;
            }
            return EmfPlusObjectType.Invalid;
        }

        /// <summary>The object's EMF+ serialisation (the GetData of its GDI+ class).</summary>
        public static byte[] Serialize(object o)
        {
            var b = new GpEmfPlusBuffer();
            switch (o)
            {
                case Brush br: WriteBrush(b, br); break;
                case Pen p: WritePen(b, p); break;
                case GraphicsPath path: WritePath(b, path); break;
                case Region r: WriteRegion(b, r); break;
                case Image im: WriteImage(b, im); break;
                case Font f: WriteFont(b, f); break;
                case StringFormat sf: WriteStringFormat(b, sf); break;
                case ImageAttributes ia: WriteImageAttributes(b, ia); break;
                case CustomLineCap c: WriteCustomLineCap(b, c); break;
                default: return null;
            }
            return b.ToArray();
        }

        static bool IsIdentity(Matrix m)
        {
            if (m == null) return true;
            float[] e = m.Elements;
            return e[0] == 1f && e[1] == 0f && e[2] == 0f && e[3] == 1f && e[4] == 0f && e[5] == 0f;
        }

        // GpSolidFill / GpHatch / GpTexture / GpRectGradient / GpPathGradient::GetData.
        public static void WriteBrush(GpEmfPlusBuffer b, Brush brush)
        {
            switch (brush)
            {
                case SolidBrush s:
                    b.I32(Version); b.I32(0); b.Argb(s.Color);
                    return;
                case HatchBrush h:
                    b.I32(Version); b.I32(1); b.I32((int)h.HatchStyle); b.Argb(h.ForegroundColor); b.Argb(h.BackgroundColor);
                    return;
                case TextureBrush t:
                    {
                        Matrix m = t.Transform;
                        int flags = 0;
                        if (!IsIdentity(m)) flags |= 2;
                        b.I32(Version); b.I32(2); b.I32(flags); b.I32((int)t.WrapMode);
                        if ((flags & 2) != 0) b.Matrix(m);
                        using (Image im = t.Image)
                            WriteImage(b, im);
                        return;
                    }
                case LinearGradientBrush l:
                    WriteLinear(b, l);
                    return;
                case PathGradientBrush pg:
                    WritePathGradient(b, pg);
                    return;
            }
            throw new ArgumentException("Parameter is not valid.");
        }

        static void WriteLinear(GpEmfPlusBuffer b, LinearGradientBrush l)
        {
            Matrix m = l.Transform;
            int flags = 0;
            if (l.GammaCorrection) flags = 0x80;
            if (!IsIdentity(m)) flags |= 2;
            ColorBlend preset = GpObjectState.LinearPreset(l);
            Blend blend = preset == null ? l.Blend : null;
            if (preset != null && preset.Colors.Length >= 2) flags |= 4;
            else if (blend != null && blend.Factors != null && blend.Factors.Length > 1) flags |= 8;
            RectangleF r = l.Rectangle;
            Color[] c = l.LinearColors;
            b.I32(Version); b.I32(4); b.I32(flags); b.I32((int)l.WrapMode);
            b.Rect(r);
            b.Argb(c[0]); b.Argb(c[1]); b.Argb(c[0]); b.Argb(c[1]);
            if ((flags & 2) != 0) b.Matrix(m);
            if ((flags & 4) != 0)
            {
                b.I32(preset.Colors.Length);
                foreach (float p in preset.Positions) b.F(p);
                foreach (Color col in preset.Colors) b.Argb(col);
            }
            if ((flags & 8) != 0)
            {
                b.I32(blend.Factors.Length);
                foreach (float p in blend.Positions) b.F(p);
                foreach (float f in blend.Factors) b.F(f);
            }
        }

        static void WritePathGradient(GpEmfPlusBuffer b, PathGradientBrush pg)
        {
            GpObjectState.PathGradientBoundary(pg, out PointF[] points, out GraphicsPath path);
            Matrix m = pg.Transform;
            int flags = 0;
            if (GpObjectState.PathGradientGamma(pg)) flags = 0x80;
            // The recorder's version flags (2) write the boundary path only when the brush has
            // no point list.
            if (points == null && path != null) flags |= 1;
            if (!IsIdentity(m)) flags |= 2;
            ColorBlend preset = GpObjectState.PathPreset(pg);
            Blend blend = preset == null ? pg.Blend : null;
            if (preset != null && preset.Colors.Length >= 2) flags |= 4;
            else if (blend != null && blend.Factors != null && blend.Factors.Length > 1) flags |= 8;
            PointF focus = pg.FocusScales;
            if (focus.X != 0f || focus.Y != 0f) flags |= 0x40;
            Color[] surround = GpObjectState.SurroundColors(pg, out int surroundCount);
            PointF center = pg.CenterPoint;
            b.I32(Version); b.I32(3); b.I32(flags); b.I32((int)pg.WrapMode);
            b.Argb(pg.CenterColor);
            b.F(center.X); b.F(center.Y);
            b.I32(surroundCount);
            for (int i = 0; i < surroundCount; i++) b.Argb(surround[i]);
            if ((flags & 1) != 0)
            {
                byte[] pd = Serialize(path);
                b.I32(pd.Length);
                b.Bytes(pd);
            }
            else
            {
                int n = points?.Length ?? 0;
                b.I32(n);
                for (int i = 0; i < n; i++) { b.F(points[i].X); b.F(points[i].Y); }
            }
            if ((flags & 2) != 0) b.Matrix(m);
            if ((flags & 4) != 0)
            {
                b.I32(preset.Colors.Length);
                foreach (float p in preset.Positions) b.F(p);
                foreach (Color c in preset.Colors) b.Argb(c);
            }
            if ((flags & 8) != 0)
            {
                b.I32(blend.Factors.Length);
                foreach (float p in blend.Positions) b.F(p);
                foreach (float f in blend.Factors) b.F(f);
            }
            if ((flags & 0x40) != 0)
            {
                b.I32(2); b.F(focus.X); b.F(focus.Y);
            }
        }

        // GpPen::GetData.
        public static void WritePen(GpEmfPlusBuffer b, Pen p)
        {
            Matrix m = p.Transform;
            bool xf = !IsIdentity(m);
            LineCap sc = p.StartCap, ec = p.EndCap;
            byte[] startCap = null, endCap = null;
            int flags = xf ? 1 : 0;
            if (sc != LineCap.Flat)
            {
                if (sc == LineCap.Custom)
                {
                    using (CustomLineCap cap = GpObjectState.PenCustomStartCap(p))
                        if (cap != null) startCap = Serialize(cap);
                    if (startCap != null && startCap.Length > 0) flags |= 0x802;
                }
                else flags |= 2;
            }
            if (ec != LineCap.Flat)
            {
                if (ec == LineCap.Custom)
                {
                    using (CustomLineCap cap = GpObjectState.PenCustomEndCap(p))
                        if (cap != null) endCap = Serialize(cap);
                    if (endCap != null && endCap.Length > 0) flags |= 0x1004;
                }
                else flags |= 4;
            }
            DashStyle ds = p.DashStyle;
            LineJoin join = p.LineJoin;
            float miter = p.MiterLimit;
            DashCap dc = p.DashCap;
            if (join != LineJoin.Miter) flags |= 8;
            if (miter != 10f) flags |= 0x10;
            if (ds != DashStyle.Solid && ds != DashStyle.Custom) flags |= 0x20;
            if (dc != DashCap.Flat) flags |= 0x40;
            flags |= 0x80;
            float[] pattern = ds == DashStyle.Custom ? p.DashPattern : null;
            if (pattern != null && pattern.Length > 0) flags |= 0x100;
            PenAlignment align = p.Alignment;
            if (align != PenAlignment.Center) flags |= 0x200;
            float[] compound = p.CompoundArray;
            if (compound != null && compound.Length > 0) flags |= 0x400;

            b.I32(Version); b.I32(0); b.I32(flags); b.I32(0 /* World */); b.F(p.Width);
            if ((flags & 1) != 0) b.Matrix(m);
            if ((flags & 2) != 0) b.I32((int)sc);
            if ((flags & 4) != 0) b.I32((int)ec);
            if ((flags & 8) != 0) b.I32((int)join);
            if ((flags & 0x10) != 0) b.F(miter);
            if ((flags & 0x20) != 0) b.I32((int)ds);
            if ((flags & 0x40) != 0) b.I32((int)dc);
            b.F(p.DashOffset);
            if ((flags & 0x100) != 0) { b.I32(pattern.Length); foreach (float f in pattern) b.F(f); }
            if ((flags & 0x200) != 0) b.I32((int)align);
            if ((flags & 0x400) != 0) { b.I32(compound.Length); foreach (float f in compound) b.F(f); }
            if ((flags & 0x800) != 0) { b.I32(startCap.Length); b.Bytes(startCap); }
            if ((flags & 0x1000) != 0) { b.I32(endCap.Length); b.Bytes(endCap); }
            using (Brush br = p.Brush)
                WriteBrush(b, br);
        }

        // DpPath::GetData.
        public static void WritePath(GpEmfPlusBuffer b, GraphicsPath path)
        {
            PointF[] pts = path.PointCount > 0 ? path.PathPoints : new PointF[0];
            byte[] types = path.PointCount > 0 ? path.PathTypes : new byte[0];
            byte[] pd = PointData(pts, 0, pts.Length, out int pflags);
            int flags = pflags;
            if (path.FillMode == FillMode.Winding) flags |= 0x2000;
            b.I32(Version); b.I32(pts.Length); b.I32(flags);
            b.Bytes(pd);
            b.Bytes(types);
            b.Pad4();
        }

        // GpRegion::GetData: the version, the count of nodes below the root (+0xa8), and the tree
        // as GpRegion::GetRegionData @180073a18 walks it: a combining node's type then its left and
        // right; a rectangle's four floats; a path's size and EMF+ path; empty and infinite nothing.
        public static void WriteRegion(GpEmfPlusBuffer b, Region r)
        {
            GpRegion root = r.gp;
            b.I32(Version);
            b.I32(CountNodes(root) - 1);
            WriteRegionNode(b, root);
        }

        static int CountNodes(GpRegion n) => n == null ? 0 : n.IsLeaf ? 1 : 1 + CountNodes(n.Left) + CountNodes(n.Right);

        static void WriteRegionNode(GpEmfPlusBuffer b, GpRegion n)
        {
            while (true)
            {
                b.I32(n.Type);
                if (n.IsLeaf) break;
                WriteRegionNode(b, n.Left);
                n = n.Right;
            }
            if (n.Type == GpRegion.NodeRect)
            {
                b.Rect(n.Rect);
                return;
            }
            if (n.Type == GpRegion.NodePath)
            {
                using (var path = n.Points != null && n.Points.Length > 0
                    ? new GraphicsPath(n.Points, n.Types, n.Fill) : new GraphicsPath(n.Fill))
                {
                    var pb = new GpEmfPlusBuffer();
                    WritePath(pb, path);
                    b.I32(pb.Length);
                    b.Bytes(pb.ToArray());
                }
            }
        }

        // GpFont::GetData.
        public static void WriteFont(GpEmfPlusBuffer b, Font f)
        {
            // GpFontFamily keeps its name upper-cased (the font table's key), and that is what goes.
            string name = (f.FontFamily.Name ?? "").ToUpperInvariant();
            b.I32(Version); b.F(f.Size); b.I32((int)f.Unit); b.I32((int)f.Style); b.I32(0); b.I32(name.Length);
            foreach (char c in name) b.I16(c);
            if ((name.Length & 1) != 0) b.I16(0);
        }

        // GpStringFormat::GetData: 0x3c bytes, then the tab stops and the character ranges.
        public static void WriteStringFormat(GpEmfPlusBuffer b, StringFormat sf)
        {
            float[] tabs = sf.GetTabStops(out float firstTab);
            CharacterRange[] ranges = GpObjectState.MeasurableRanges(sf);
            GpObjectState.StringFormatMetrics(sf, out int language, out float leading, out float trailing, out float tracking);
            b.I32(Version);
            b.I32((int)sf.FormatFlags);
            b.I16(language); b.I16(0);
            b.I32((int)sf.Alignment);
            b.I32((int)sf.LineAlignment);
            b.I32((int)sf.DigitSubstitutionMethod);
            // The two bytes after the digit language are never written by GDI+ (+0x2c is a LANGID);
            // they come out as the 0xffff the stack held.
            b.I16(sf.DigitSubstitutionLanguage); b.I16(0xffff);
            b.F(firstTab);
            b.I32((int)sf.HotkeyPrefix);
            b.F(leading); b.F(trailing); b.F(tracking);
            b.I32((int)sf.Trimming);
            b.I32(tabs?.Length ?? 0);
            b.I32(ranges?.Length ?? 0);
            if (tabs != null) foreach (float t in tabs) b.F(t);
            if (ranges != null) foreach (CharacterRange cr in ranges) { b.I32(cr.First); b.I32(cr.Length); }
        }

        // GpImageAttributes::GetData.
        public static void WriteImageAttributes(GpEmfPlusBuffer b, ImageAttributes ia)
        {
            GpObjectState.ImageAttributesWrap(ia, out WrapMode wrap, out Color clamp, out bool objectClamp);
            // +0x20 is 1 from the constructor (GpImageAttributes::GpImageAttributes @18001e7d8).
            b.I32(Version); b.I32(1); b.I32((int)wrap); b.Argb(clamp); b.I32(objectClamp ? 1 : 0); b.I32(0);
        }

        // GpCustomLineCap / GpAdjustableArrowCap::GetData.
        public static void WriteCustomLineCap(GpEmfPlusBuffer b, CustomLineCap cap)
        {
            cap.GetStrokeCaps(out LineCap startCap, out LineCap endCap);
            float miter = GpObjectState.CapMiterLimit(cap);
            GpObjectState.CapHotSpots(cap, out PointF fillHot, out PointF lineHot);
            if (cap is AdjustableArrowCap a)
            {
                b.I32(Version); b.I32(1);
                b.F(a.Width); b.F(a.Height); b.F(a.MiddleInset); b.I32(a.Filled ? 1 : 0);
                b.I32((int)startCap); b.I32((int)endCap); b.I32((int)cap.StrokeJoin); b.F(miter);
                b.F(cap.WidthScale);
                b.F(fillHot.X); b.F(fillHot.Y); b.F(lineHot.X); b.F(lineHot.Y);
                return;
            }
            GpObjectState.CapPaths(cap, out GraphicsPath fill, out GraphicsPath line);
            byte[] fd = fill != null && fill.PointCount > 2 ? Serialize(fill) : null;
            byte[] ld = line != null && line.PointCount > 2 ? Serialize(line) : null;
            int flags = 0;
            if (fd != null && fd.Length > 0) flags |= 1;
            if (ld != null && ld.Length > 0) flags |= 2;
            b.I32(Version); b.I32(0);
            b.I32(flags);
            b.I32((int)cap.BaseCap); b.F(cap.BaseInset);
            b.I32((int)startCap); b.I32((int)endCap); b.I32((int)cap.StrokeJoin); b.F(miter);
            b.F(cap.WidthScale);
            b.F(fillHot.X); b.F(fillHot.Y); b.F(lineHot.X); b.F(lineHot.Y);
            if ((flags & 1) != 0) { b.I32(fd.Length); b.Bytes(fd); }
            if ((flags & 2) != 0) { b.I32(ld.Length); b.Bytes(ld); }
        }

        // CopyOnWriteBitmap::GetData / GpMetafile::GetData.
        public static void WriteImage(GpEmfPlusBuffer b, Image im)
        {
            if (im is Metafile mf)
            {
                WriteMetafileImage(b, mf);
                return;
            }
            // A bitmap goes as compressed data: the bytes it was read from when it was read from
            // a stream or file, else a PNG of it (the recorder's version flags ask for that).
            byte[] data = GpObjectState.SourceBytes(im);
            if (data == null)
            {
                using (var ms = new MemoryStream())
                {
                    im.Save(ms, ImageFormat.Png);
                    data = ms.ToArray();
                }
            }
            b.I32(Version); b.I32(1);
            b.I32(0); b.I32(0); b.I32(0); b.I32(0); b.I32(1);
            b.Bytes(data);
            b.Pad4();
        }

        static void WriteMetafileImage(GpEmfPlusBuffer b, Metafile mf)
        {
            mf.CheckPlayable();
            GpMetafileData d = mf.data;
            GpMetafileHeader h = mf.header;
            b.I32(Version); b.I32(2);
            if (d.IsWmf)
            {
                // A WMF goes with a placeable header GDI+ builds from its bounds and dpi.
                b.I32(2);
                b.I32(d.Wmf.Length);
                var p = new GpPlaceable
                {
                    Key = GpMetafileFormat.PlaceableKey,
                    Left = (short)h.X, Top = (short)h.Y,
                    Right = (short)(h.X + h.Width), Bottom = (short)(h.Y + h.Height),
                    Inch = (short)GpMetafileFormat.Round((h.DpiY + h.DpiX) * 0.5f),
                };
                byte[] pb = p.ToBytes();
                ushort x = 0;
                for (int i = 0; i < 10; i++) x ^= Le.U16(pb, i * 2);
                Le.W16(pb, 20, x);
                b.Bytes(pb);
                b.Zeros(2);
                b.Bytes(d.Wmf);
                b.Pad4();
                return;
            }
            if (h.Type != MetafileType.EmfPlusDual)
            {
                b.I32((int)h.Type);
                b.I32(d.Emf.Length);
                b.Bytes(d.Emf);
                return;
            }
            // EnumEmfRemoveDualRecords: a dual file goes as EMF+ only.
            byte[] only = GpMetafileEdit.RemoveDualRecords(d.Emf);
            b.I32(4);
            b.I32(only.Length);
            b.Bytes(only);
        }
    }
}
