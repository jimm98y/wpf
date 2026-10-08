// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Drawing and enumerating a metafile: GDI+'s playback, managed (gdiplus.dll 10.0.26100):
//
//   GpGraphics::EnumerateMetafile @180075f30   a parallelogram: the unit square mapped onto it
//                                              (InferAffineMatrix) and the metafile drawn into
//                                              the unit square
//   GpMetafile::EnumerateForPlayback @180092348 flips, the source rectangle in its unit
//                                              (GetPixelMultipliers @1801c5428), the clip to the
//                                              destination, and the container an EMF+ file plays
//                                              in (ConvertTransformForMetafile @180090938: the
//                                              source mapped one pixel short of the destination's
//                                              far edges) at the metafile's logical dpi
//   MetafilePlayer::EnumerateEmfPlusRecords @180091ff0  the EMF+ records of a comment
//   EnumEmfWithDownLevel @180098050            an EMF+ file's GDI records are skipped -- reported
//                                              only for EMR_HEADER and EMR_EOF -- except after an
//                                              EmfPlusGetDC, up to the next EMF+ comment
//   EnumEmfDownLevel @180097f40                a plain EMF: every record
//   GdipPlayMetafileRecordCallback @1800982c0  the default callback: play the record
//   GdipPlayMetafileRecord @180065bb0          Metafile.PlayRecord, from inside a callback
//
// Everything is drawn through the target Graphics' public API, so a metafile plays onto any
// Graphics: a bitmap's, a printer page's, a control's, or another metafile's recording.
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal static partial class GpMetafilePlayer
    {
        /// <summary>Graphics.DrawImage(metafile, ...): the metafile's src rectangle (in srcUnit) drawn
        /// into the parallelogram (upper-left, upper-right, lower-left, in the target's world units).</summary>
        public static void Play(Graphics target, Metafile mf, PointF[] destParallelogram, RectangleF srcRect, GraphicsUnit srcUnit, ImageAttributes ia)
        {
            Run(target, mf, destParallelogram, srcRect, srcUnit, null, IntPtr.Zero, ia);
        }

        /// <summary>GpGraphics::DrawImage(image, rect, matrix) @18000f530 for a metafile: the source
        /// (in <paramref name="srcUnit"/>, running forwards) brought to pixels at the metafile's
        /// resolution (the matrix scaled back), the matrix prepended to the world transform, and
        /// EnumerateForPlayback of that pixel rectangle onto itself.</summary>
        public static void PlayImage(Graphics target, Metafile mf, GpMat m, RectangleF src, GraphicsUnit srcUnit, ImageAttributes ia)
        {
            if (target == null) throw new ArgumentNullException("graphics");
            if (mf == null) throw new ArgumentNullException("metafile");
            mf.CheckPlayable();
            if (mf.playback != null)
                throw new InvalidOperationException("Object is currently in use elsewhere.");
            if (srcUnit != GraphicsUnit.World && srcUnit != GraphicsUnit.Display && srcUnit != GraphicsUnit.Pixel)
            {
                GpMetafileHeader h = mf.header;
                PixelMultipliers(srcUnit, h.DpiX, h.DpiY, out float fx, out float fy);
                src = new RectangleF(src.X * fx, src.Y * fy, src.Width * fx, src.Height * fy);
                m.Scale(1f / fx, 1f / fy, MatrixOrder.Prepend);
            }
            var s = new Session(target, mf, null, ia);
            mf.playback = s;
            try
            {
                s.Begin(m, src, src, GraphicsUnit.Pixel);
                s.Walk();
            }
            finally
            {
                mf.playback = null;
                s.End();
            }
        }

        /// <summary>Graphics.EnumerateMetafile: each record handed to the callback, which plays it
        /// (Metafile.PlayRecord) or not.</summary>
        public static void Enumerate(Graphics target, Metafile mf, PointF[] destParallelogram, RectangleF srcRect, GraphicsUnit srcUnit,
            Graphics.EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes ia)
        {
            Run(target, mf, destParallelogram, srcRect, srcUnit, callback, callbackData, ia);
        }

        /// <summary>GdipPlayMetafileRecord: valid only while the metafile is being enumerated.</summary>
        public static void PlayRecord(Metafile mf, EmfPlusRecordType recordType, int flags, int dataSize, byte[] data)
        {
            Session s = mf.playback;
            if (s == null)
                throw new ArgumentException("Parameter is not valid.");
            if (data == null && dataSize > 0)
                throw new ArgumentException("Parameter is not valid.");
            int n = Math.Min(dataSize, data?.Length ?? 0);
            s.Play((int)recordType, flags, data ?? new byte[0], 0, n);
        }

        // GpGraphics::EnumerateMetafile(points) -> EnumerateMetafile(rect) -> EnumerateForPlayback.
        static void Run(Graphics target, Metafile mf, PointF[] dest, RectangleF src, GraphicsUnit srcUnit,
            Graphics.EnumerateMetafileProc callback, IntPtr callbackData, ImageAttributes ia)
        {
            if (target == null) throw new ArgumentNullException("graphics");
            if (mf == null) throw new ArgumentNullException("metafile");
            if (dest == null || dest.Length != 3) throw new ArgumentException("Parameter is not valid.");
            mf.CheckPlayable();
            if (mf.playback != null)
                throw new InvalidOperationException("Object is currently in use elsewhere.");
            // The unit square onto the parallelogram.
            if (!GpMat.InferAffine(dest, new RectangleF(0, 0, 1, 1), out GpMat unit))
                return;
            var s = new Session(target, mf, callback, ia);
            mf.playback = s;
            try
            {
                s.Begin(unit, new RectangleF(0, 0, 1, 1), src, srcUnit);
                s.Walk();
            }
            finally
            {
                mf.playback = null;
                s.End();
            }
        }

        /// <summary>GetPixelMultipliers: a unit's pixels at the metafile's dpi.</summary>
        static void PixelMultipliers(GraphicsUnit unit, float dpiX, float dpiY, out float mx, out float my)
        {
            switch (unit)
            {
                case GraphicsUnit.Point: mx = dpiX / 72f; my = dpiY / 72f; return;
                case GraphicsUnit.Inch: mx = dpiX; my = dpiY; return;
                case GraphicsUnit.Document: mx = dpiX / 300f; my = dpiY / 300f; return;
                case GraphicsUnit.Millimeter: mx = dpiX / 25.4f; my = dpiY / 25.4f; return;
                default: mx = 1f; my = 1f; return;
            }
        }

        /// <summary>A playback in progress: the target, the mapping, and the players of the records.</summary>
        internal sealed partial class Session
        {
            public readonly Graphics Target;
            public readonly Metafile Mf;
            readonly Graphics.EnumerateMetafileProc _callback;
            public readonly ImageAttributes Attributes;
            GraphicsState _saved;
            internal GpEmfPlusPlayer Plus;
            internal GpGdiPlayer GdiPlayer;
            bool _downLevel;         // after an EmfPlusGetDC: the GDI records play
            bool _aborted;
            internal bool IsEmfPlus;
            // The target's device transform and clip as the playback found them.
            internal GpMat TargetWorldToDevice;
            internal Region BaseClip;                 // device units; null = none
            internal GpMat MetafileToDevice;          // an EMF+ file's device pixels to the target's
            internal GpMat EmfWorldToDevice;          // the world an EMF or WMF is mapped in
            internal RectangleF EmfDest;              // ... and the rectangle its frame maps onto

            public Session(Graphics target, Metafile mf, Graphics.EnumerateMetafileProc callback, ImageAttributes ia)
            {
                Target = target;
                Mf = mf;
                _callback = callback;
                Attributes = ia;
            }

            static GpMat PageToDevice(Graphics g)
            {
                GraphicsUnit u = g.PageUnit;
                float s = g.PageScale, dx = g.DpiX, dy = g.DpiY;
                float mx, my;
                switch (u)
                {
                    case GraphicsUnit.Point: mx = dx / 72f; my = dy / 72f; break;
                    case GraphicsUnit.Inch: mx = dx; my = dy; break;
                    case GraphicsUnit.Document: mx = dx / 300f; my = dy / 300f; break;
                    case GraphicsUnit.Millimeter: mx = dx / 25.4f; my = dy / 25.4f; break;
                    default: mx = 1f; my = 1f; break;
                }
                return new GpMat(mx * s, 0, 0, my * s, 0, 0);
            }

            /// <summary>The target's device transform with <paramref name="world"/> as its world transform.</summary>
            GpMat Device(GpMat world) => GpMat.Multiply(world, PageToDevice(Target));

            // EnumerateForPlayback, from the destination (unit) rectangle on.
            public void Begin(GpMat unitToWorld, RectangleF dest, RectangleF src, GraphicsUnit srcUnit)
            {
                GpMetafileHeader h = Mf.header;
                if (dest.Width == 0f || dest.Height == 0f || src.Width == 0f || src.Height == 0f) { _aborted = true; return; }
                if (h.Type == MetafileType.Emf && Le.U32(h.Raw, 52) < 3) { _aborted = true; return; }

                GpMat world0 = GpMat.Multiply(unitToWorld, GpMat.From(Target.Transform));
                _saved = Target.Save();
                TargetWorldToDevice = Device(world0);

                // Flips: exactly one of the destination and the source running backwards mirrors
                // the drawing; both normalised.
                bool flipX = (dest.Width < 0f) != (src.Width < 0f);
                bool flipY = (dest.Height < 0f) != (src.Height < 0f);
                if (dest.Width < 0f) { dest.X += dest.Width; dest.Width = -dest.Width; }
                if (dest.Height < 0f) { dest.Y += dest.Height; dest.Height = -dest.Height; }
                if (src.Width < 0f) { src.X += src.Width; src.Width = -src.Width; }
                if (src.Height < 0f) { src.Y += src.Height; src.Height = -src.Height; }
                GpMat flip = GpMat.Identity;
                if (flipX || flipY)
                {
                    flip.Translate(flipX ? dest.X + dest.Width + dest.X : 0f, flipY ? dest.Y + dest.Height + dest.Y : 0f, MatrixOrder.Prepend);
                    flip.Scale(flipX ? -1f : 1f, flipY ? -1f : 1f, MatrixOrder.Prepend);
                }
                PixelMultipliers(srcUnit, h.DpiX, h.DpiY, out float mx, out float my);
                var spx = new RectangleF(mx * src.X, my * src.Y, mx * src.Width, my * src.Height);
                GpMat world = GpMat.Multiply(flip, world0);
                RectangleF rb = Mf.RealBounds;
                bool partial = rb != spx;
                GpMat playWorld = world;
                if (partial)
                {
                    GpMat m = world;
                    m.Translate((rb.X - spx.X) * dest.Width / spx.Width + dest.X, (rb.Y - spx.Y) * dest.Height / spx.Height + dest.Y, MatrixOrder.Prepend);
                    m.Scale(rb.Width != spx.Width ? rb.Width / spx.Width : 1f, rb.Height != spx.Height ? rb.Height / spx.Height : 1f, MatrixOrder.Prepend);
                    m.Translate(-dest.X, -dest.Y, MatrixOrder.Prepend);
                    playWorld = m;
                }
                // The clip to the destination: exact for an axis-aligned drawing, a pixel wider
                // either side otherwise; none for a mirrored whole one (or a WMF's rotated one).
                GpMat dev = Device(playWorld);
                bool exact, noneWhole;
                if ((dev.Complexity & ~3) == 0)
                {
                    exact = !(dev.M11 < 0f) && !(dev.M22 < 0f);
                    noneWhole = !exact;
                }
                else
                {
                    exact = false;
                    noneWhole = Mf.header.IsWmf;
                }
                using (Region outer = TargetClip())
                {
                    BaseClip = outer?.Clone();
                }
                if (!noneWhole || partial)
                {
                    RectangleF clipRect = dest;
                    if (!((!noneWhole || !partial) && exact))
                    {
                        // GetWorldPixelSize: one device pixel in world units, either side.
                        GpMat inv = Device(world0);
                        float px = 1f / Math.Max(1e-6f, (float)Math.Sqrt(inv.M11 * inv.M11 + inv.M12 * inv.M12));
                        float py = 1f / Math.Max(1e-6f, (float)Math.Sqrt(inv.M21 * inv.M21 + inv.M22 * inv.M22));
                        clipRect = new RectangleF(dest.X - px, dest.Y - py, dest.Width + px + px, dest.Height + py + py);
                    }
                    IntersectBase(clipRect, Device(world0));
                }
                EmfWorldToDevice = Device(playWorld);
                EmfDest = dest;
                IsEmfPlus = h.IsEmfPlus;
                if (IsEmfPlus)
                {
                    // The container the EMF+ records play in: the source onto the destination.
                    float ldx = h.LogicalDpiX != 0 ? h.LogicalDpiX : h.DpiX;
                    float ldy = h.LogicalDpiY != 0 ? h.LogicalDpiY : h.DpiY;
                    bool display = (h.EmfPlusFlags & 1) != 0;
                    GpMat w = world;
                    GpMat mapping;
                    if (ConvertTransformForMetafile(spx, dest, ref w))
                        mapping = Device(w);
                    else
                    {
                        GpMat.InferAffine(dest, spx, out GpMat c);
                        mapping = GpMat.Multiply(c, Device(world));
                    }
                    MetafileToDevice = mapping;
                    Plus = new GpEmfPlusPlayer(this, ldx, ldy, display);
                }
                GdiPlayer = new GpGdiPlayer(this);
            }

            /// <summary>ConvertTransformForMetafile: the source mapped onto the destination one unit
            /// short of its far edges (so the source's last pixel lands on the destination's).</summary>
            static bool ConvertTransformForMetafile(RectangleF src, RectangleF dst, ref GpMat world)
            {
                if (!(1f < src.Width) || !(1f < src.Height)) return false;
                var p = new[] { new PointF(dst.X, dst.Y), new PointF(dst.Width + dst.X, dst.Y), new PointF(dst.X, dst.Height + dst.Y) };
                world.Transform(p);
                float v1x = p[1].X - p[0].X, v1y = p[1].Y - p[0].Y, v2x = p[2].X - p[0].X, v2y = p[2].Y - p[0].Y;
                float l1 = MathF.Sqrt(v1y * v1y + v1x * v1x), l2 = MathF.Sqrt(v2y * v2y + v2x * v2x);
                if (!(1f < l1) || !(1f < l2)) return false;
                float k1 = (l1 - 1f) / l1, k2 = (l2 - 1f) / l2;
                p[1] = new PointF(k1 * v1x + p[0].X, k1 * v1y + p[0].Y);
                p[2] = new PointF(k2 * v2x + p[0].X, k2 * v2y + p[0].Y);
                var s = new RectangleF(src.X, src.Y, src.Width - 1f, src.Height - 1f);
                if (!GpMat.InferAffine(p, s, out GpMat m)) return false;
                world = m;
                return true;
            }

            Region TargetClip()
            {
                try
                {
                    Target.ResetTransform();
                    Target.PageUnit = GraphicsUnit.Pixel;
                    Target.PageScale = 1f;
                    Region r = Target.Clip;
                    if (r.IsInfinite(Target)) { r.Dispose(); return null; }
                    return r;
                }
                catch (ArgumentException) { return null; }
            }

            void IntersectBase(RectangleF rect, GpMat toDevice)
            {
                var path = new GraphicsPath();
                path.AddRectangle(rect);
                using (Matrix m = toDevice.ToMatrix())
                    path.Transform(m);
                var r = new Region(path);
                if (BaseClip == null) BaseClip = r;
                else { BaseClip.Intersect(r); r.Dispose(); }
            }

            public void End()
            {
                // Draw32BppDib: what GDI played into the DIB, onto the destination.
                try { if (!_aborted) GdiPlayer?.Finish(); } catch (ArgumentException) { }
                if (_saved != null)
                {
                    try { Target.Restore(_saved); } catch (ArgumentException) { }
                    _saved = null;
                }
                Plus?.Dispose();
                GdiPlayer?.Dispose();
                BaseClip?.Dispose();
            }

            // ---- walking the records ---------------------------------------------------------------

            public void Walk()
            {
                if (_aborted) return;
                GpMetafileData d = Mf.data;
                if (d.IsWmf)
                {
                    GdiPlayer.BeginWmf(d);
                    foreach (var rec in GpWmfToEmf.WmfRecords(d.Wmf))
                    {
                        if (rec.Function == 0) break;   // META_EOF is not enumerated
                        if (!Report(0x10000 | rec.Function, 0, d.Wmf, rec.Offset + 6, rec.Size - 6)) return;
                    }
                    return;
                }
                byte[] emf = d.Emf;
                GdiPlayer.BeginEmf(d);
                foreach (var (o, type, size) in GpMetafileEdit.Records(emf))
                {
                    if (IsEmfPlus)
                    {
                        int p = GpMetafileFormat.EmfPlusPayload(emf, o, out int n);
                        if (p >= 0)
                        {
                            // MetafilePlayer::DoneWithDownLevel, then the comment's EMF+ records.
                            _downLevel = false;
                            if (!EmfPlusRecords(emf, p, n)) return;
                            continue;
                        }
                        // EnumEmfWithDownLevel: GDI records of an EMF+ file only after GetDC.
                        if (!_downLevel && type != GpMetafileFormat.EmrHeader && type != GpMetafileFormat.EmrEof)
                            continue;
                    }
                    else if (GpMetafileFormat.IsEmfPlusRecord(emf, o, emf.Length))
                        continue;   // EnumEmfDownLevel: a plain EMF's EMF+ comments are not records
                    if (!Report(type, 0, emf, o + 8, size - 8)) return;
                }
            }

            bool EmfPlusRecords(byte[] b, int p, int n)
            {
                int i = 0;
                while (n - i >= 12)
                {
                    int q = p + i;
                    uint size = Le.U32(b, q + 4);
                    if (size < 12 || (uint)(n - i) < size) return true;
                    if (size - 12 != Le.U32(b, q + 8)) return true;
                    int type = Le.U16(b, q);
                    int flags = Le.U16(b, q + 2);
                    if ((uint)(type - 0x4001) < 0x421)
                    {
                        int ds = (int)size - 12;
                        // An object record continued across comments is put together first
                        // (MetafilePlayer::ConcatenateRecords).
                        if (type == 0x4008 && ds > 0 && ((flags & 0x8000) != 0 || Plus.Concatenating))
                        {
                            Plus.Concatenate(flags, b, q + 12, ds);
                        }
                        else if (!Report(type, flags, b, q + 12, ds)) return false;
                    }
                    if (type == 0x4004) _downLevel = true;
                    i += (int)size;
                }
                return true;
            }

            /// <summary>One record: to the caller's callback (which may play it), or played.</summary>
            internal bool Report(int type, int flags, byte[] b, int o, int n)
            {
                if (n < 0) n = 0;
                if (_callback == null)
                {
                    Play(type, flags, b, o, n);
                    return true;
                }
                IntPtr mem = IntPtr.Zero;
                try
                {
                    if (n > 0)
                    {
                        mem = Marshal.AllocHGlobal(n);
                        Marshal.Copy(b, o, mem, n);
                    }
                    if (!_callback((EmfPlusRecordType)type, flags, n, mem, null))
                    {
                        _aborted = true;
                        return false;
                    }
                }
                finally
                {
                    if (mem != IntPtr.Zero) Marshal.FreeHGlobal(mem);
                }
                return true;
            }

            /// <summary>GdipPlayMetafileRecordCallback: EMF+ records to the EMF+ player, GDI records
            /// (an EMF's, a WMF's, or an EMF+ file's after GetDC) to the GDI player.</summary>
            internal void Play(int type, int flags, byte[] b, int o, int n)
            {
                try
                {
                    if ((uint)(type - 0x4001) < 0x3a)
                    {
                        Plus?.Play(type, flags, b, o, n);
                        return;
                    }
                    if (IsEmfPlus && !_downLevel && type != GpMetafileFormat.EmrHeader && type != GpMetafileFormat.EmrEof)
                        return;
                    if ((type & 0x10000) != 0)
                        GdiPlayer.PlayWmf(type & 0xffff, b, o, n);
                    else
                        GdiPlayer.PlayEmf(type, b, o, n);
                }
                catch (Exception e) when (e is ArgumentException || e is OverflowException || e is InvalidOperationException
                    || e is ExternalException || e is NotImplementedException || e is OutOfMemoryException || e is IndexOutOfRangeException)
                {
                    // A record GDI+ cannot play is skipped.
                }
            }
        }
    }
}
