// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// EMF+ records played onto a Graphics: GDI+'s *EPR::Play functions (gdiplus.dll 10.0.26100,
// RecordPlayFuncs) and the MetafilePlayer they run in -- the 64-object table (AddObject @18008fd40,
// with continued objects put together by ConcatenateRecords @1800903f0), save ids
// (NewSave / GetSaveID), brushes given as a colour (GetBrush @180093130), int16 and relative
// points (GetPoints @180094720 / GetRects @180094be0).
//
// Each record calls what the EPR calls on GpGraphics; the GpGraphics state it changes -- world and
// page transform, containers, clipping, rendering settings -- is kept here as GDI+'s DpContext keeps
// it, inside the container EnumerateForPlayback opened at the metafile's logical dpi, and the target
// Graphics is given the result: its world transform set to the record's world-to-device in the
// target's device pixels (page unit Pixel), its clip the visible clip, in device pixels.
//

using System.Collections.Generic;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpEmfPlusPlayer : IDisposable
    {
        readonly GpMetafilePlayer.Session _s;
        readonly Graphics _t;
        readonly object[] _objects = new object[64];

        // DpContext, as the playback's GpGraphics has it.
        sealed class Ctx
        {
            public GpMat World = GpMat.Identity;
            public GraphicsUnit Unit = GraphicsUnit.Display;
            public float Scale = 1f;
            public float Mx = 1f, My = 1f;
            public GpMat Container = GpMat.Identity;      // to the metafile's device pixels
            public float DpiX, DpiY;
            public bool Display;
            public Region AppClip;                         // target device pixels; null = infinite
            public Region ContainerClip;                   // the visible clip when the container began
            public SmoothingMode Smoothing;
            public TextRenderingHint TextHint;
            public int Contrast = 4;
            public InterpolationMode Interp = InterpolationMode.Bilinear;
            public PixelOffsetMode PixelOffset;
            public CompositingMode CompMode;
            public CompositingQuality CompQuality;
            public Point Origin;

            public Ctx Clone()
            {
                var c = (Ctx)MemberwiseClone();
                c.AppClip = AppClip?.Clone();
                c.ContainerClip = ContainerClip?.Clone();
                return c;
            }
        }

        Ctx _c;
        readonly List<(int Id, Ctx State)> _stack = new List<(int, Ctx)>();
        readonly Dictionary<uint, int> _saveIds = new Dictionary<uint, int>();
        int _nextId = 1;
        bool _clipDirty = true;

        public GpEmfPlusPlayer(GpMetafilePlayer.Session s, float dpiX, float dpiY, bool display)
        {
            _s = s;
            _t = s.Target;
            _c = new Ctx { DpiX = dpiX, DpiY = dpiY, Display = display };
            PageMultipliers(_c);
            // MetafilePlayer::PrepareToPlay: the ImageAttributes' GpRecolor, adjust type Default.
            GpRecolor rc = s.Attributes?.Recolor;
            _rc = rc != null && rc.HasRecoloring((ColorAdjustType)6) ? rc : null;
            _rc?.Flush();
        }

        readonly GpRecolor _rc;
        const ColorAdjustType PlayerAdjust = ColorAdjustType.Default;

        public void Dispose()
        {
            _c?.AppClip?.Dispose();
            _c?.ContainerClip?.Dispose();
            foreach (var (_, st) in _stack) { st.AppClip?.Dispose(); st.ContainerClip?.Dispose(); }
        }

        // DpContext::GetPageMultipliers (GpGraphics::SetPageTransform @180035a68): Display is one
        // pixel on a display whatever the scale, a hundredth of an inch elsewhere.
        static void PageMultipliers(Ctx c)
        {
            float s = c.Scale;
            switch (c.Unit)
            {
                case GraphicsUnit.Display:
                    if (c.Display) { c.Mx = 1f; c.My = 1f; }
                    else { c.Mx = c.DpiX * s / 100f; c.My = c.DpiY * s / 100f; }
                    return;
                case GraphicsUnit.Pixel: c.Mx = s; c.My = s; return;
                case GraphicsUnit.Point: c.Mx = c.DpiX * s / 72f; c.My = c.DpiY * s / 72f; return;
                case GraphicsUnit.Inch: c.Mx = c.DpiX * s; c.My = c.DpiY * s; return;
                case GraphicsUnit.Document: c.Mx = c.DpiX * s / 300f; c.My = c.DpiY * s / 300f; return;
                case GraphicsUnit.Millimeter: c.Mx = c.DpiX * s / 25.4f; c.My = c.DpiY * s / 25.4f; return;
            }
            c.Mx = c.DpiX * s / 100f; c.My = c.DpiY * s / 100f;
        }

        /// <summary>The record's world to the metafile's device pixels.</summary>
        GpMat WorldToMetafileDevice => GpMat.Multiply(GpMat.Multiply(_c.World, new GpMat(_c.Mx, 0, 0, _c.My, 0, 0)), _c.Container);

        /// <summary>The record's world to the target's device pixels.</summary>
        GpMat WorldToDevice => GpMat.Multiply(WorldToMetafileDevice, _s.MetafileToDevice);

        // ---- the target ---------------------------------------------------------------------------

        /// <summary>Gives the target the current transform, clip and settings, before a drawing call.</summary>
        void Apply()
        {
            Graphics t = _t;
            t.PageUnit = GraphicsUnit.Pixel;
            t.PageScale = 1f;
            if (_clipDirty)
            {
                t.ResetTransform();
                Region vis = Visible();
                if (vis == null) t.ResetClip();
                else { t.Clip = vis; vis.Dispose(); }
                _clipDirty = false;
            }
            GpMat m = WorldToDevice;
            try { t.Transform = m.ToMatrix(); }
            catch (ArgumentException) { t.Transform = new Matrix(1e-6f, 0, 0, 1e-6f, m.Dx, m.Dy); }
            t.SmoothingMode = _c.Smoothing == SmoothingMode.Invalid ? SmoothingMode.Default : _c.Smoothing;
            t.TextRenderingHint = _c.TextHint;
            t.TextContrast = _c.Contrast;
            if (_c.Interp != InterpolationMode.Invalid) t.InterpolationMode = _c.Interp;
            if (_c.PixelOffset != PixelOffsetMode.Invalid) t.PixelOffsetMode = _c.PixelOffset;
            t.CompositingMode = _c.CompMode;
            t.CompositingQuality = _c.CompQuality == CompositingQuality.Invalid ? CompositingQuality.Default : _c.CompQuality;
            t.RenderingOrigin = _c.Origin;
        }

        Region Visible()
        {
            Region r = _s.BaseClip?.Clone();
            if (_c.ContainerClip != null) { if (r == null) r = _c.ContainerClip.Clone(); else r.Intersect(_c.ContainerClip); }
            if (_c.AppClip != null) { if (r == null) r = _c.AppClip.Clone(); else r.Intersect(_c.AppClip); }
            return r;
        }

        // ---- objects ------------------------------------------------------------------------------

        // MetafilePlayer::ConcatenateRecords: a continued object's chunks, until its total is in.
        byte[] _cat;
        int _catLen, _catFlags;
        public bool Concatenating => _cat != null;

        public void Concatenate(int flags, byte[] b, int o, int n)
        {
            if ((flags & 0x8000) != 0)
            {
                if (n < 4) return;
                int total = Le.I32(b, o);
                if (_cat == null || (flags & 0x7fff) != (_catFlags & 0x7fff))
                {
                    if (total <= 0 || total > 0x40000000) return;
                    _cat = new byte[total];
                    _catLen = 0;
                    _catFlags = flags & 0x7fff;
                }
                int take = Math.Min(n - 4, _cat.Length - _catLen);
                Buffer.BlockCopy(b, o + 4, _cat, _catLen, take);
                _catLen += take;
                if (_catLen < _cat.Length) return;
            }
            else
            {
                if (_cat == null) { _s.Report(0x4008, flags, b, o, n); return; }
                int take = Math.Min(n, _cat.Length - _catLen);
                Buffer.BlockCopy(b, o, _cat, _catLen, take);
                _catLen += take;
            }
            byte[] whole = _cat;
            int wf = _catFlags;
            _cat = null;
            _s.Report(0x4008, wf, whole, 0, whole.Length);
        }

        T Obj<T>(int id) where T : class => id >= 0 && id < 64 ? _objects[id] as T : null;

        Brush Brush(int flags, int value)
        {
            // MetafilePlayer::GetBrush: a colour in the record is a solid fill, recoloured as one.
            if ((flags & 0x8000) != 0)
                return new SolidBrush(_rc == null ? Color.FromArgb(value) : GpMetaRecolor.Adjust(_rc, ColorAdjustType.Brush, Color.FromArgb(value)));
            return Obj<Brush>(value & 0xff);
        }

        // ---- records -------------------------------------------------------------------------------

        public void Play(int type, int flags, byte[] b, int o, int n)
        {
            var r = new GpReader(b, o, n);
            switch (type)
            {
                case 0x4001: case 0x4002: case 0x4003: case 0x4004:   // header, EOF, comment, GetDC
                    return;
                case 0x4008: PlayObject(flags, b, o, n); return;
                case 0x4009: Clear(r); return;
                case 0x400a: FillRects(flags, r); return;
                case 0x400b: DrawRects(flags, r); return;
                case 0x400c: FillPolygon(flags, r); return;
                case 0x400d: DrawLines(flags, r); return;
                case 0x400e: FillEllipse(flags, r); return;
                case 0x400f: DrawEllipse(flags, r); return;
                case 0x4010: FillPie(flags, r); return;
                case 0x4011: DrawPie(flags, r); return;
                case 0x4012: DrawArc(flags, r); return;
                case 0x4013: FillRegion(flags, r); return;
                case 0x4014: FillPath(flags, r); return;
                case 0x4015: DrawPath(flags, r); return;
                case 0x4016: FillClosedCurve(flags, r); return;
                case 0x4017: DrawClosedCurve(flags, r); return;
                case 0x4018: DrawCurve(flags, r); return;
                case 0x4019: DrawBeziers(flags, r); return;
                case 0x401a: DrawImage(flags, r); return;
                case 0x401b: DrawImagePoints(flags, r); return;
                case 0x401c: DrawString(flags, r); return;
                case 0x401d: if (n >= 8) { int x = r.I32(), y = r.I32(); _c.Origin = new Point(x, y); } return;
                case 0x401e: _c.Smoothing = (SmoothingMode)((flags >> 1) & 0x7f); return;
                case 0x401f: _c.TextHint = (TextRenderingHint)(flags & 0xff); return;
                case 0x4020: if ((flags & 0xfff) < 13) _c.Contrast = flags & 0xfff; return;
                case 0x4021: _c.Interp = (InterpolationMode)(flags & 0xff); return;
                case 0x4022: _c.PixelOffset = (PixelOffsetMode)(flags & 0xff); return;
                case 0x4023: _c.CompMode = (CompositingMode)(flags & 0xff); return;
                case 0x4024: _c.CompQuality = (CompositingQuality)(flags & 0xff); return;
                case 0x4025: Save(r); return;
                case 0x4026: Restore(r); return;
                case 0x4027: BeginContainer(flags, r); return;
                case 0x4028: BeginContainerNoParams(r); return;
                case 0x4029: EndContainer(r); return;
                case 0x402a: if (n >= 24) { _c.World = GpMat.FromElements(r.Matrix6()); } return;
                case 0x402b: _c.World = GpMat.Identity; return;
                case 0x402c: if (n >= 24) { var m = GpMat.FromElements(r.Matrix6()); _c.World.Multiply(m, (flags & 0x2000) != 0 ? MatrixOrder.Append : MatrixOrder.Prepend); } return;
                case 0x402d: if (n >= 8) { float dx = r.F(), dy = r.F(); _c.World.Translate(dx, dy, (flags & 0x2000) != 0 ? MatrixOrder.Append : MatrixOrder.Prepend); } return;
                case 0x402e: if (n >= 8) { float sx = r.F(), sy = r.F(); _c.World.Scale(sx, sy, (flags & 0x2000) != 0 ? MatrixOrder.Append : MatrixOrder.Prepend); } return;
                case 0x402f: if (n >= 4) { float a = r.F(); _c.World.Rotate(a, (flags & 0x2000) != 0 ? MatrixOrder.Append : MatrixOrder.Prepend); } return;
                case 0x4030: SetPageTransform(flags, r); return;
                case 0x4031: SetAppClip(null, CombineMode.Replace, true); return;
                case 0x4032: if (n >= 16) ClipRect(r.Rect(), (CombineMode)((flags >> 8) & 0xf)); return;
                case 0x4033: ClipPath(flags); return;
                case 0x4034: ClipRegion(flags); return;
                case 0x4035: if (n >= 8) OffsetClip(r.F(), r.F()); return;
                case 0x4036: DrawDriverString(flags, r); return;
            }
        }

        // MetafilePlayer::AddObject: the object goes into its slot, replacing what was there.
        void PlayObject(int flags, byte[] b, int o, int n)
        {
            int id = flags & 0xff;
            var type = (EmfPlusObjectType)((flags >> 8) & 0x7f);
            if (id >= 64) return;
            object obj = GpEmfPlusReader.Read(type, b, o, n);
            if (_rc != null && obj != null) obj = GpMetaRecolor.Object(_rc, obj, PlayerAdjust);
            if (_objects[id] is IDisposable old && !ReferenceEquals(old, obj)) old.Dispose();
            _objects[id] = obj;
        }

        void Clear(GpReader r)
        {
            Color c = r.Argb();
            if (!r.Ok) return;
            Apply();
            _t.Clear(c);
        }

        void FillRects(int flags, GpReader r)
        {
            int bv = r.I32();
            int count = r.I32();
            RectangleF[] rects = r.Rects(count, flags);
            Brush br = Brush(flags, bv);
            if (!r.Ok || br == null || count <= 0) return;
            Apply();
            _t.FillRectangles(br, rects);
        }

        void DrawRects(int flags, GpReader r)
        {
            int count = r.I32();
            RectangleF[] rects = r.Rects(count, flags);
            Pen p = PenOf(flags);
            if (!r.Ok || p == null || count <= 0) return;
            Apply();
            _t.DrawRectangles(p, rects);
        }

        Pen PenOf(int flags) => Obj<Pen>(flags & 0xff);

        void FillPolygon(int flags, GpReader r)
        {
            int bv = r.I32();
            int count = r.I32();
            PointF[] pts = r.Points(count, flags);
            Brush br = Brush(flags, bv);
            if (!r.Ok || br == null || count < 3) return;
            Apply();
            _t.FillPolygon(br, pts, (flags & 0x2000) != 0 ? FillMode.Winding : FillMode.Alternate);
        }

        void DrawLines(int flags, GpReader r)
        {
            int count = r.I32();
            PointF[] pts = r.Points(count, flags);
            Pen p = PenOf(flags);
            if (!r.Ok || p == null || count < 2) return;
            Apply();
            if ((flags & 0x2000) != 0) _t.DrawPolygon(p, pts);
            else _t.DrawLines(p, pts);
        }

        void FillEllipse(int flags, GpReader r)
        {
            int bv = r.I32();
            RectangleF[] rc = r.Rects(1, flags);
            Brush br = Brush(flags, bv);
            if (!r.Ok || br == null) return;
            Apply();
            _t.FillEllipse(br, rc[0]);
        }

        void DrawEllipse(int flags, GpReader r)
        {
            RectangleF[] rc = r.Rects(1, flags);
            Pen p = PenOf(flags);
            if (!r.Ok || p == null) return;
            Apply();
            _t.DrawEllipse(p, rc[0]);
        }

        void FillPie(int flags, GpReader r)
        {
            int bv = r.I32();
            float start = r.F(), sweep = r.F();
            RectangleF[] rc = r.Rects(1, flags);
            Brush br = Brush(flags, bv);
            if (!r.Ok || br == null) return;
            Apply();
            _t.FillPie(br, rc[0].X, rc[0].Y, rc[0].Width, rc[0].Height, start, sweep);
        }

        void DrawPie(int flags, GpReader r)
        {
            float start = r.F(), sweep = r.F();
            RectangleF[] rc = r.Rects(1, flags);
            Pen p = PenOf(flags);
            if (!r.Ok || p == null) return;
            Apply();
            _t.DrawPie(p, rc[0], start, sweep);
        }

        void DrawArc(int flags, GpReader r)
        {
            float start = r.F(), sweep = r.F();
            RectangleF[] rc = r.Rects(1, flags);
            Pen p = PenOf(flags);
            if (!r.Ok || p == null) return;
            Apply();
            _t.DrawArc(p, rc[0], start, sweep);
        }

        void FillRegion(int flags, GpReader r)
        {
            int bv = r.I32();
            Region rg = Obj<Region>(flags & 0xff);
            Brush br = Brush(flags, bv);
            if (!r.Ok || br == null || rg == null) return;
            Apply();
            _t.FillRegion(br, rg);
        }

        void FillPath(int flags, GpReader r)
        {
            int bv = r.I32();
            GraphicsPath path = Obj<GraphicsPath>(flags & 0xff);
            Brush br = Brush(flags, bv);
            if (!r.Ok || br == null || path == null) return;
            Apply();
            _t.FillPath(br, path);
        }

        void DrawPath(int flags, GpReader r)
        {
            int penId = r.I32();
            GraphicsPath path = Obj<GraphicsPath>(flags & 0xff);
            Pen p = Obj<Pen>(penId);
            if (!r.Ok || p == null || path == null) return;
            Apply();
            _t.DrawPath(p, path);
        }

        void FillClosedCurve(int flags, GpReader r)
        {
            int bv = r.I32();
            float tension = r.F();
            int count = r.I32();
            PointF[] pts = r.Points(count, flags);
            Brush br = Brush(flags, bv);
            if (!r.Ok || br == null || count < 3) return;
            Apply();
            _t.FillClosedCurve(br, pts, (flags & 0x2000) != 0 ? FillMode.Winding : FillMode.Alternate, tension);
        }

        void DrawClosedCurve(int flags, GpReader r)
        {
            float tension = r.F();
            int count = r.I32();
            PointF[] pts = r.Points(count, flags);
            Pen p = PenOf(flags);
            if (!r.Ok || p == null || count < 3) return;
            Apply();
            _t.DrawClosedCurve(p, pts, tension, FillMode.Alternate);
        }

        void DrawCurve(int flags, GpReader r)
        {
            float tension = r.F();
            int offset = r.I32(), segs = r.I32(), count = r.I32();
            PointF[] pts = r.Points(count, flags);
            Pen p = PenOf(flags);
            if (!r.Ok || p == null || count < 2) return;
            if (offset < 0 || segs < 1 || offset + segs >= count) return;
            Apply();
            _t.DrawCurve(p, pts, offset, segs, tension);
        }

        void DrawBeziers(int flags, GpReader r)
        {
            int count = r.I32();
            PointF[] pts = r.Points(count, flags);
            Pen p = PenOf(flags);
            if (!r.Ok || p == null || count < 4 || (count - 1) % 3 != 0) return;
            Apply();
            _t.DrawBeziers(p, pts);
        }

        void DrawImage(int flags, GpReader r)
        {
            int ia = r.I32();
            var unit = (GraphicsUnit)r.I32();
            RectangleF src = r.Rect();
            RectangleF[] dst = r.Rects(1, flags);
            Image im = Obj<Image>(flags & 0xff);
            if (!r.Ok || im == null) return;
            Apply();
            _t.DrawImage(im, new[] { dst[0].Location, new PointF(dst[0].Right, dst[0].Top), new PointF(dst[0].Left, dst[0].Bottom) },
                src, unit, ImageAttributesFor(ia));
        }

        void DrawImagePoints(int flags, GpReader r)
        {
            int ia = r.I32();
            var unit = (GraphicsUnit)r.I32();
            RectangleF src = r.Rect();
            int count = r.I32();
            PointF[] pts = r.Points(count, flags);
            Image im = Obj<Image>(flags & 0xff);
            if (!r.Ok || im == null || count != 3) return;
            Apply();
            _t.DrawImage(im, pts, src, unit, ImageAttributesFor(ia));
        }

        ImageAttributes ImageAttributesFor(int id)
        {
            // The record's own; the player's ImageAttributes recoloured the image when it was made.
            return Obj<ImageAttributes>(id);
        }

        // A font's size in other units than World is in the metafile's units: as a World font of the
        // size it has in this world (GpFont em size through the page transform at the logical dpi).
        Font WorldFont(Font f)
        {
            if (f.Unit == GraphicsUnit.World) return f;
            float px;
            switch (f.Unit)
            {
                case GraphicsUnit.Point: px = f.Size * _c.DpiY / 72f; break;
                case GraphicsUnit.Inch: px = f.Size * _c.DpiY; break;
                case GraphicsUnit.Document: px = f.Size * _c.DpiY / 300f; break;
                case GraphicsUnit.Millimeter: px = f.Size * _c.DpiY / 25.4f; break;
                default: px = f.Size; break;
            }
            float world = px / Math.Max(1e-6f, _c.My);
            return new Font(f.FontFamily, world, f.Style, GraphicsUnit.World);
        }

        void DrawString(int flags, GpReader r)
        {
            int bv = r.I32();
            int fmt = r.I32();
            int len = r.I32();
            RectangleF layout = r.Rect();
            if (len < 0 || len > r.Left / 2) return;
            var cs = new char[len];
            for (int i = 0; i < len; i++) cs[i] = (char)r.I16();
            Font font = Obj<Font>(flags & 0xff);
            Brush br = Brush(flags, bv);
            if (!r.Ok || font == null || br == null) return;
            StringFormat sf = Obj<StringFormat>(fmt);
            Apply();
            Font wf = WorldFont(font);
            try { _t.DrawString(new string(cs), wf, br, layout, sf); }
            finally { if (!ReferenceEquals(wf, font)) wf.Dispose(); }
        }

        void DrawDriverString(int flags, GpReader r)
        {
            int bv = r.I32();
            int options = r.I32();
            int hasMatrix = r.I32();
            int count = r.I32();
            if (count < 0 || count > r.Left / 10) return;
            var glyphs = new ushort[count];
            for (int i = 0; i < count; i++) glyphs[i] = (ushort)r.I16();
            var pos = new PointF[count];
            for (int i = 0; i < count; i++) { float x = r.F(), y = r.F(); pos[i] = new PointF(x, y); }
            Matrix m = hasMatrix != 0 ? r.Matrix() : null;
            Font font = Obj<Font>(flags & 0xff);
            Brush br = Brush(flags, bv);
            if (!r.Ok || font == null || br == null) return;
            Apply();
            Font wf = WorldFont(font);
            try { GpDriverString.Draw(_t, glyphs, wf, br, pos, options, m); }
            finally { if (!ReferenceEquals(wf, font)) wf.Dispose(); }
        }

        // ---- state ----------------------------------------------------------------------------------

        void SetPageTransform(int flags, GpReader r)
        {
            float scale = r.F();
            var unit = (GraphicsUnit)(flags & 0xff);
            if (!r.Ok || (int)unit < 1 || (int)unit > 6) return;
            _c.Unit = unit;
            _c.Scale = scale;
            PageMultipliers(_c);
        }

        void Save(GpReader r)
        {
            uint idx = r.U32();
            if (!r.Ok) return;
            int id = _nextId++;
            _stack.Add((id, _c.Clone()));
            _saveIds[idx] = id;
        }

        bool PopTo(int id)
        {
            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                if (_stack[i].Id != id) continue;
                _c.AppClip?.Dispose();
                _c.ContainerClip?.Dispose();
                _c = _stack[i].State;
                for (int k = i + 1; k < _stack.Count; k++) { _stack[k].State.AppClip?.Dispose(); _stack[k].State.ContainerClip?.Dispose(); }
                _stack.RemoveRange(i, _stack.Count - i);
                _clipDirty = true;
                return true;
            }
            return false;
        }

        void Restore(GpReader r)
        {
            uint idx = r.U32();
            if (!r.Ok || !_saveIds.TryGetValue(idx, out int id)) return;
            PopTo(id);
        }

        /// <summary>GpGraphics::BeginContainer @180078d18: the source (in its unit) onto the
        /// destination, then the old world to device; a fresh world, page and rendering state; the
        /// visible clip as the container's.</summary>
        void BeginContainer(int flags, GpReader r)
        {
            RectangleF dst = r.Rect(), src = r.Rect();
            uint idx = r.U32();
            if (!r.Ok) return;
            var unit = (GraphicsUnit)(flags & 0xff);
            var c = new Ctx { DpiX = _c.DpiX, DpiY = _c.DpiY, Display = _c.Display, Unit = unit, Scale = 1f };
            PageMultipliers(c);
            var s = new RectangleF(src.X * c.Mx, src.Y * c.My, src.Width * c.Mx, src.Height * c.My);
            if (!GpMat.InferAffine(dst, s, out GpMat m)) return;
            Push(idx, GpMat.Multiply(m, WorldToMetafileDevice));
        }

        /// <summary>BeginContainer @180078fc0: the same world to device, undone by the new page scale.</summary>
        void BeginContainerNoParams(GpReader r)
        {
            uint idx = r.U32();
            if (!r.Ok) return;
            var m = new GpMat(1f / _c.Mx, 0, 0, 1f / _c.My, 0, 0);
            Push(idx, GpMat.Multiply(GpMat.Multiply(m, GpMat.Identity), WorldToMetafileDevice));
        }

        void Push(uint idx, GpMat container)
        {
            int id = _nextId++;
            Ctx old = _c;
            _stack.Add((id, old.Clone()));
            _saveIds[idx] = id;
            var c = new Ctx
            {
                DpiX = old.DpiX, DpiY = old.DpiY, Display = old.Display, Container = container,
                Origin = old.Origin,
            };
            PageMultipliers(c);
            // The container's clip: what was visible.
            Region vis = null;
            if (old.ContainerClip != null) vis = old.ContainerClip.Clone();
            if (old.AppClip != null) { if (vis == null) vis = old.AppClip.Clone(); else vis.Intersect(old.AppClip); }
            c.ContainerClip = vis;
            old.AppClip?.Dispose();
            old.ContainerClip?.Dispose();
            _c = c;
            _clipDirty = true;
        }

        void EndContainer(GpReader r)
        {
            uint idx = r.U32();
            if (!r.Ok || !_saveIds.TryGetValue(idx, out int id)) return;
            PopTo(id);
        }

        // ---- clipping: GpGraphics::SetClip / ResetClip / OffsetClip -------------------------------

        Region ToDevice(GraphicsPath path)
        {
            using (Matrix m = WorldToDevice.ToMatrix())
                path.Transform(m);
            return new Region(path);
        }

        void SetAppClip(Region deviceRegion, CombineMode mode, bool reset)
        {
            if (reset)
            {
                _c.AppClip?.Dispose();
                _c.AppClip = null;
                _clipDirty = true;
                return;
            }
            if (mode == CombineMode.Replace || _c.AppClip == null && mode == CombineMode.Intersect)
            {
                _c.AppClip?.Dispose();
                _c.AppClip = deviceRegion;
            }
            else
            {
                Region cur = _c.AppClip ?? new Region();
                switch (mode)
                {
                    case CombineMode.Intersect: cur.Intersect(deviceRegion); break;
                    case CombineMode.Union: cur.Union(deviceRegion); break;
                    case CombineMode.Xor: cur.Xor(deviceRegion); break;
                    case CombineMode.Exclude: cur.Exclude(deviceRegion); break;
                    case CombineMode.Complement: cur.Complement(deviceRegion); break;
                }
                deviceRegion.Dispose();
                _c.AppClip = cur;
            }
            using (var probe = GpRegionProbe.Graphics())
                if (_c.AppClip != null && _c.AppClip.IsInfinite(probe)) { _c.AppClip.Dispose(); _c.AppClip = null; }
            _clipDirty = true;
        }

        void ClipRect(RectangleF rect, CombineMode mode)
        {
            if ((int)mode < 0 || (int)mode > 5) return;
            var p = new GraphicsPath();
            p.AddRectangle(rect);
            SetAppClip(ToDevice(p), mode, false);
        }

        void ClipPath(int flags)
        {
            GraphicsPath path = Obj<GraphicsPath>(flags & 0xff);
            var mode = (CombineMode)((flags >> 8) & 0xf);
            if (path == null || (int)mode > 5) return;
            var p = (GraphicsPath)path.Clone();
            SetAppClip(ToDevice(p), mode, false);
        }

        void ClipRegion(int flags)
        {
            Region rg = Obj<Region>(flags & 0xff);
            var mode = (CombineMode)((flags >> 8) & 0xf);
            if (rg == null || (int)mode > 5) return;
            Region d = rg.Clone();
            using (Matrix m = WorldToDevice.ToMatrix())
                d.Transform(m);
            SetAppClip(d, mode, false);
        }

        void OffsetClip(float dx, float dy)
        {
            if (_c.AppClip == null) return;
            GpMat m = WorldToDevice;
            float vx = m.M11 * dx + m.M21 * dy, vy = m.M12 * dx + m.M22 * dy;
            _c.AppClip.Translate(vx, vy);
            _clipDirty = true;
        }
    }
}
