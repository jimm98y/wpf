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

using System.Collections.Generic;
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

        /// <summary>A copy -- of this node only. Children and point arrays are shared and never changed
        /// in place (Transform and Offset copy before they write), so a copy is O(1). It was a deep copy,
        /// and every clip a paint saved and restored (Graphics.Clip, a region's Clone, each Combine) copied
        /// the whole tree: a tenth of a DotNetBar ribbon's repaint.</summary>
        public GpRegion Clone () => (GpRegion) MemberwiseClone ();

        public void Set (GpRegion other)
        {
            Invalidate ();
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
            GpRegion left = Clone ();     // keeps this node's device region: it is still left's
            GpRegion right = other.Clone ();
            Invalidate ();
            Type = m; Left = left; Right = right; Points = null; Types = null;
        }

        public static GpRegion Combine (GpRegion a, GpRegion b, CombineMode mode)
        {
            GpRegion r = a.Clone ();
            r.Combine (b, mode);
            return r;
        }

        void SetEmpty () { Invalidate (); Type = NodeEmpty; Left = Right = null; Points = null; Types = null; }
        void SetInfinite () { Invalidate (); Type = NodeInfinite; Left = Right = null; Points = null; Types = null; }

        public void MakeEmpty () => SetEmpty ();
        public void MakeInfinite () => SetInfinite ();

        /// <summary>GpRegion::Transform: every leaf transformed.</summary>
        public void Transform (in GpMatrix m)
        {
            if (m.IsIdentity) return;
            Invalidate ();
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
                Points = (PointF[]) Points.Clone ();   // the array may be shared with a copy
                m.Transform (Points);
                break;
            case NodeEmpty: case NodeInfinite:
                break;
            default:
                Left = Left.Clone (); Left.Transform (m);
                Right = Right.Clone (); Right.Transform (m);
                break;
            }
        }

        public void Offset (float dx, float dy)
        {
            if (dx == 0f && dy == 0f) return;
            Invalidate ();
            switch (Type) {
            case NodeRect: Rect = new RectangleF (Rect.X + dx, Rect.Y + dy, Rect.Width, Rect.Height); break;
            case NodePath: {
                var moved = new PointF [Points.Length];   // the array may be shared with a copy
                for (int i = 0; i < moved.Length; i++) moved [i] = new PointF (Points [i].X + dx, Points [i].Y + dy);
                Points = moved;
                break;
            }
            case NodeEmpty: case NodeInfinite: break;
            default:
                Left = Left.Clone (); Left.Offset (dx, dy);
                Right = Right.Clone (); Right.Offset (dx, dy);
                break;
            }
        }

        // A path clip's device region, kept for the exact same points, types, rule and matrix. A control
        // that clips to the same rounded path on every paint (DotNetBar's buttons and panels do, and a
        // window move repaints all of them) rasterized it again each time.
        static readonly Dictionary<PathKey, DpRegion> s_pathRegions = new Dictionary<PathKey, DpRegion> ();

        readonly struct PathKey : IEquatable<PathKey>
        {
            readonly PointF[] _pts; readonly byte[] _types; readonly FillMode _fill; readonly GpMatrix _m; readonly int _hash;
            public PathKey (PointF[] pts, byte[] types, FillMode fill, in GpMatrix m)
            {
                _pts = (PointF[]) pts.Clone (); _types = (byte[]) types.Clone (); _fill = fill; _m = m;
                var h = new HashCode ();
                h.Add (fill); h.Add (m.M11); h.Add (m.M12); h.Add (m.M21); h.Add (m.M22); h.Add (m.Dx); h.Add (m.Dy);
                foreach (PointF q in pts) { h.Add (q.X); h.Add (q.Y); }
                foreach (byte t in types) h.Add (t);
                _hash = h.ToHashCode ();
            }
            public bool Equals (PathKey o)
            {
                if (_hash != o._hash || _fill != o._fill || _pts.Length != o._pts.Length || _types.Length != o._types.Length) return false;
                if (_m.M11 != o._m.M11 || _m.M12 != o._m.M12 || _m.M21 != o._m.M21 || _m.M22 != o._m.M22 || _m.Dx != o._m.Dx || _m.Dy != o._m.Dy) return false;
                for (int i = 0; i < _pts.Length; i++) if (_pts [i] != o._pts [i]) return false;
                for (int i = 0; i < _types.Length; i++) if (_types [i] != o._types [i]) return false;
                return true;
            }
            public override bool Equals (object o) => o is PathKey k && Equals (k);
            public override int GetHashCode () => _hash;
        }

        static DpRegion PathDevice (PointF[] pts, byte[] types, FillMode fill, in GpMatrix m)
        {
            if (pts == null || types == null || pts.Length != types.Length || pts.Length > 4096)
                return GpRegionRaster.FromPath (pts, types, fill, m);
            var key = new PathKey (pts, types, fill, m);
            lock (s_pathRegions)
                if (s_pathRegions.TryGetValue (key, out DpRegion cached)) return cached.Clone ();
            DpRegion r = GpRegionRaster.FromPath (pts, types, fill, m);
            lock (s_pathRegions) {
                if (s_pathRegions.Count > 4096) s_pathRegions.Clear ();
                s_pathRegions [key] = r.Clone ();
            }
            return r;
        }

        /// <summary>The device region under <paramref name="m"/> (UpdateDeviceRegion). The caller's own
        /// copy.</summary>
        public DpRegion Device (in GpMatrix m) => DeviceShared (m).Clone ();

        // This node's device region, kept for the last matrix asked for. A graphics' clip is a tree that
        // each SetClip/Intersect grows by a node over the last, and every change worked out the whole
        // tree's region again from its leaves; children never change in place (see Clone), so theirs stay
        // good. Any change to this node drops it (Invalidate).
        // (A separate object, made only when asked for, so a node -- of which a paint makes thousands --
        // stays small.)
        sealed class DeviceCache { public DpRegion Region; public GpMatrix Matrix; }
        DeviceCache _device;

        void Invalidate () => _device = null;

        static bool SameMatrix (in GpMatrix a, in GpMatrix b)
            => a.M11 == b.M11 && a.M12 == b.M12 && a.M21 == b.M21 && a.M22 == b.M22 && a.Dx == b.Dx && a.Dy == b.Dy && a.Complexity == b.Complexity;

        /// <summary>The device region, shared: the caller must not change it.</summary>
        internal DpRegion DeviceShared (in GpMatrix m)
        {
            DeviceCache c = _device;
            if (c != null && SameMatrix (c.Matrix, m)) return c.Region;
            DpRegion d = Compute (m);
            _device = new DeviceCache { Region = d, Matrix = m };   // replaced, never changed: copies share it
            return d;
        }

        DpRegion Compute (in GpMatrix m)
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
                return PathDevice (Points, Types, Fill, m);
            default: {
                DpRegion a = Left.DeviceShared (m), b = Right.DeviceShared (m);   // Combine only reads them
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
