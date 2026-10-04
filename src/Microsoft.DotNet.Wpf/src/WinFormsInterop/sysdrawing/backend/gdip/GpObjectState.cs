// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The state of a System.Drawing object that its public API does not hand out but its EMF+
// serialisation needs: a path gradient's boundary, a custom cap's paths and miter limit, an
// ImageAttributes' clamp colour, a StringFormat's language, margins and character ranges, whether a
// gradient holds preset colours. Each is the field GDI+'s object keeps, held by the managed objects
// (backend/gdip) the classes are built on. This is the one seam between the metafile code and those
// internals.
//

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static class GpObjectState
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        static T Field<T>(object o, string name, T fallback)
        {
            FieldInfo f = o?.GetType().GetField(name, Private);
            return f != null && f.GetValue(o) is T t ? t : fallback;
        }

        public static ColorBlend LinearPreset(LinearGradientBrush l)
        {
            if (!l.PresetSet) return null;
            ColorBlend cb = l.InterpolationColors;
            return cb != null && cb.Colors != null && cb.Colors.Length >= 2 ? cb : null;
        }

        public static ColorBlend PathPreset(PathGradientBrush p)
        {
            if (!p.PresetSet) return null;
            ColorBlend cb = p.InterpolationColors;
            return cb != null && cb.Colors != null && cb.Colors.Length >= 2 ? cb : null;
        }

        public static bool PathGradientGamma(PathGradientBrush p) => p.GammaCorrection;

        /// <summary>The boundary a path gradient keeps: made from a path, the path (+0x88, its point
        /// list +0x90 left null); made from points, the points.</summary>
        public static void PathGradientBoundary(PathGradientBrush p, out PointF[] points, out GraphicsPath path)
        {
            points = null;
            path = null;
            if (p.GpPath != null)
            {
                path = new GraphicsPath(p.GpPath.Clone());
                return;
            }
            if (p.GpPoints != null)
            {
                int n = Math.Min(p.PointCount, p.GpPoints.Length);
                points = new PointF[n];
                Array.Copy(p.GpPoints, points, n);
            }
        }

        /// <summary>The surround colours GDI+ writes: one when the brush keeps a single colour
        /// (+0xa4), else one per boundary point.</summary>
        public static Color[] SurroundColors(PathGradientBrush p, out int count)
        {
            int[] s = p.SurroundArgb;
            if (s == null || s.Length == 0) { count = 0; return new Color[0]; }
            var c = new Color[s.Length];
            for (int i = 0; i < s.Length; i++) c[i] = Color.FromArgb(s[i]);
            count = p.OneSurround ? 1 : Math.Min(p.PointCount, s.Length);
            return c;
        }

        public static CustomLineCap PenCustomStartCap(Pen p) => p.CustomStartRef;
        public static CustomLineCap PenCustomEndCap(Pen p) => p.CustomEndRef;

        public static float CapMiterLimit(CustomLineCap c) => c.gp?.MiterLimit ?? 10f;

        // GDI+ keeps the hot spots (+0x20, +0x28) at the origin: nothing sets them.
        public static void CapHotSpots(CustomLineCap c, out PointF fill, out PointF line)
        {
            fill = PointF.Empty;
            line = PointF.Empty;
        }

        /// <summary>A cap's paths, which GDI+ keeps (and serialises) with the winding fill mode
        /// whatever the path it was given.</summary>
        public static void CapPaths(CustomLineCap c, out GraphicsPath fill, out GraphicsPath line)
        {
            fill = c.gp?.FillPath != null ? new GraphicsPath(c.gp.FillPath.Clone()) { FillMode = FillMode.Winding } : null;
            line = c.gp?.StrokePath != null ? new GraphicsPath(c.gp.StrokePath.Clone()) { FillMode = FillMode.Winding } : null;
        }

        public static void ImageAttributesWrap(ImageAttributes ia, out WrapMode wrap, out Color clamp, out bool objectClamp)
        {
            wrap = ia.WrapModeForDrawing;
            clamp = Field(ia, "_wrapColor", Color.Empty);
            objectClamp = Field(ia, "_wrapClamp", false);
        }

        public static CharacterRange[] MeasurableRanges(StringFormat sf) => sf.MeasurableCharacterRanges;

        /// <summary>A StringFormat's language and the margins and tracking GDI+ gives it: a sixth of
        /// an em either side and 1.03 tracking, none and 1.0 for the typographic format.</summary>
        public static void StringFormatMetrics(StringFormat sf, out int language, out float leading, out float trailing, out float tracking)
        {
            language = Field(sf, "language", 0);
            if (sf.IsTypographic)
            {
                leading = trailing = 0f;
                tracking = 1.0f;
            }
            else
            {
                leading = trailing = BitConverter.Int32BitsToSingle(0x3e2aaaab);
                tracking = BitConverter.Int32BitsToSingle(0x3f83d70a);
            }
        }

        /// <summary>The encoded bytes an image was read from: GDI+ records those for a bitmap it has
        /// not touched since reading it. The managed image does not keep them, so a bitmap goes as
        /// a PNG, which is what GDI+ writes for any bitmap without them.</summary>
        public static byte[] SourceBytes(Image im) => null;
    }
}
