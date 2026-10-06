// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s region: a tree of combine nodes over leaves (rect, path, empty, infinite), kept in the
// space it was given in and rasterized only when a device region is wanted -- GpRegion, read out of
// gdiplus.dll:
//
//   GpRegion::Combine @180072bd0         the empty/infinite short-cuts, else a new combine node
//   GpRegion::TransformLeaf @180074ab8   a rect stays a rect under a scale/translate (TransformRect),
//                                        otherwise becomes a 4-point path
//   GpRegion::CreateLeafDeviceRegion @180073360   a rect: RasterizerCeiling of each edge; a path:
//                                        DpRegion::Set(path) through the region rasterizer
//   GpRegion::CreateDeviceRegion @180073200       And / Or / Xor / Exclude / Complement of the two
//
// Node types are EMF+'s RegionNodeDataType values: 1 And, 2 Or, 3 Xor, 4 Exclude, 5 Complement,
// 0x10000000 Rect, 0x10000001 Path, 0x10000002 Empty, 0x10000003 Infinite.
//

using System.Drawing.Drawing2D;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpRegion
    {
        public const int NodeAnd = 1, NodeOr = 2, NodeXor = 3, NodeExclude = 4, NodeComplement = 5;
        public const int NodeRect = 0x10000000, NodePath = 0x10000001, NodeEmpty = 0x10000002, NodeInfinite = 0x10000003;

        public int Type;
        public RectangleF Rect;
        public PointF[] Points;
        public byte[] Types;
        public FillMode Fill;
        public GpRegion Left, Right;

        public static GpRegion Infinite () => new GpRegion { Type = NodeInfinite };
        public static GpRegion Empty () => new GpRegion { Type = NodeEmpty };
        public static GpRegion FromRect (RectangleF r) => new GpRegion { Type = NodeRect, Rect = r };
        public static GpRegion FromPath (PointF[] pts, byte[] types, FillMode fill)
            => new GpRegion { Type = NodePath, Points = (PointF[]) pts.Clone (), Types = (byte[]) types.Clone (), Fill = fill };

        public bool IsLeaf => (Type & 0x10000000) != 0;

        public GpRegion Clone ()
        {
            var r = (GpRegion) MemberwiseClone ();
            if (Points != null) { r.Points = (PointF[]) Points.Clone (); r.Types = (byte[]) Types.Clone (); }
            r.Left = Left?.Clone ();
            r.Right = Right?.Clone ();
            return r;
        }

        public void Set (GpRegion other)
        {
            GpRegion c = other.Clone ();
            Type = c.Type; Rect = c.Rect; Points = c.Points; Types = c.Types; Fill = c.Fill; Left = c.Left; Right = c.Right;
        }

        /// <summary>GpRegion::Combine(this, other, mode), in place.</summary>
        public void Combine (GpRegion other, CombineMode mode)
        {
            int m = (int) mode;   // Replace 0, Intersect 1, Union 2, Xor 3, Exclude 4, Complement 5
            if (m == 0) { Set (other); return; }
            if (other.Type == NodeEmpty) {
                if (m == 1 || m == 5) SetEmpty ();
                return;
            }
            if (other.Type == NodeInfinite) {
                if (m == 1) return;
                if (m == 2) { SetInfinite (); return; }
                if (((m == 3 || m == 5) && Type == NodeInfinite) || m == 4) { SetEmpty (); return; }
            }
            if (Type == NodeInfinite) {
                if (m == 1) { Set (other); return; }
                if (m == 2) return;
                if (m == 5) { SetEmpty (); return; }
            } else if (Type == NodeEmpty) {
                if (m == 2 || m == 3 || m == 5) Set (other);
                return;
            }
            GpRegion left = Clone ();
            GpRegion right = other.Clone ();
            Type = m; Left = left; Right = right; Points = null; Types = null;
        }

        public static GpRegion Combine (GpRegion a, GpRegion b, CombineMode mode)
        {
            GpRegion r = a.Clone ();
            r.Combine (b, mode);
            return r;
        }

        void SetEmpty () { Type = NodeEmpty; Left = Right = null; Points = null; Types = null; }
        void SetInfinite () { Type = NodeInfinite; Left = Right = null; Points = null; Types = null; }

        public void MakeEmpty () => SetEmpty ();
        public void MakeInfinite () => SetInfinite ();

        /// <summary>GpRegion::Transform: every leaf transformed.</summary>
        public void Transform (in GpMatrix m)
        {
            if (m.IsIdentity) return;
            switch (Type) {
            case NodeRect:
                if (m.IsTranslateScale) {
                    float x0 = Rect.X, y0 = Rect.Y, x1 = Rect.Right, y1 = Rect.Bottom;
                    m.Transform (ref x0, ref y0);
                    m.Transform (ref x1, ref y1);
                    Rect = RectangleF.FromLTRB (Math.Min (x0, x1), Math.Min (y0, y1), Math.Max (x0, x1), Math.Max (y0, y1));
                } else {
                    var p = new[] { new PointF (Rect.X, Rect.Y), new PointF (Rect.X + Rect.Width, Rect.Y),
                                    new PointF (Rect.X + Rect.Width, Rect.Y + Rect.Height), new PointF (Rect.X, Rect.Y + Rect.Height) };
                    m.Transform (p);
                    Type = NodePath; Points = p; Types = new byte [] { 0, 1, 1, 1 }; Fill = FillMode.Alternate;
                }
                break;
            case NodePath:
                m.Transform (Points);
                break;
            case NodeEmpty: case NodeInfinite:
                break;
            default:
                Left.Transform (m); Right.Transform (m);
                break;
            }
        }

        public void Offset (float dx, float dy)
        {
            if (dx == 0f && dy == 0f) return;
            switch (Type) {
            case NodeRect: Rect = new RectangleF (Rect.X + dx, Rect.Y + dy, Rect.Width, Rect.Height); break;
            case NodePath: for (int i = 0; i < Points.Length; i++) Points [i] = new PointF (Points [i].X + dx, Points [i].Y + dy); break;
            case NodeEmpty: case NodeInfinite: break;
            default: Left.Offset (dx, dy); Right.Offset (dx, dy); break;
            }
        }

        /// <summary>The device region under <paramref name="m"/> (UpdateDeviceRegion).</summary>
        public DpRegion Device (in GpMatrix m)
        {
            switch (Type) {
            case NodeEmpty: return new DpRegion ();
            case NodeInfinite: return DpRegion.Infinite ();
            case NodeRect: {
                if (!(Rect.Width > 0f) || !(Rect.Height > 0f)) return new DpRegion ();
                if (m.IsTranslateScale) {
                    float x0 = Rect.X, y0 = Rect.Y, x1 = Rect.Right, y1 = Rect.Bottom;
                    m.Transform (ref x0, ref y0);
                    m.Transform (ref x1, ref y1);
                    float l = Math.Min (x0, x1), t = Math.Min (y0, y1), w = Math.Abs (x1 - x0), h = Math.Abs (y1 - y0);
                    int ix = GpMatrix.RasterizerCeiling (l), iy = GpMatrix.RasterizerCeiling (t);
                    int iw = GpMatrix.RasterizerCeiling (w + l) - ix, ih = GpMatrix.RasterizerCeiling (h + t) - iy;
                    return SetRect (ix, iy, iw, ih);
                }
                var p = new[] { new PointF (Rect.X, Rect.Y), new PointF (Rect.X + Rect.Width, Rect.Y),
                                new PointF (Rect.X + Rect.Width, Rect.Y + Rect.Height), new PointF (Rect.X, Rect.Y + Rect.Height) };
                return GpRegionRaster.FromPath (p, new byte [] { 0, 1, 1, 0x81 }, FillMode.Alternate, m);
            }
            case NodePath:
                return GpRegionRaster.FromPath (Points, Types, Fill, m);
            default: {
                DpRegion a = Left.Device (m), b = Right.Device (m);
                DpRegion.Op op = Type switch {
                    NodeAnd => DpRegion.Op.And, NodeOr => DpRegion.Op.Or, NodeXor => DpRegion.Op.Xor,
                    NodeExclude => DpRegion.Op.Exclude, _ => DpRegion.Op.Complement,
                };
                return DpRegion.Combine (a, b, op);
            }
            }
        }

        /// <summary>DpRegion::Set(x, y, w, h): clamped to the infinite range.</summary>
        static DpRegion SetRect (int x, int y, int w, int h)
        {
            if (x < -0x400000) { if (w < 0x800000) w = x + w + 0x400000; x = -0x400000; }
            if (y < -0x400000) { if (h < 0x800000) h = y + h + 0x400000; y = -0x400000; }
            if (w > 0) {
                if (w < 0x800000 && (uint) (h - 1) < 0x7fffff) return DpRegion.FromRect (x, y, w, h);
                if (h > 0) return DpRegion.Infinite ();
            }
            return new DpRegion ();
        }
    }

}
