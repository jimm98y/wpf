// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI's enhanced-metafile DC, as GDI+'s metafile driver (DriverMeta) drives it: what CreateEnhMetaFileW
// hands GDI+ and what lands in the file for each GDI call. The calls here are gdi32's, with GDI's
// arguments; each writes the record GDI writes (GpEmfWriter keeps the file), with the bounds GDI
// keeps. Read off GDI's own recordings (scratchpad oracle mfgdi/gd.cs runs a call script on a real
// EMF DC; MetafileTests holds this model to it):
//
//   * objects: a CreatePen / ExtCreatePen / CreateSolidBrush / CreateDIBPatternBrushPt /
//     CreateFontIndirect is recorded when it is first selected into the DC (or first used, FillRgn),
//     in a new handle slot or the one freed last; DeleteObject records only an object the file holds
//     and frees its slot; stock objects select as 0x80000000 | n; nothing is ever elided (selecting what
//     is already selected, setting a mode to what it is, all record);
//   * SaveDC / RestoreDC (an absolute level recorded relative), the world transform (only in
//     GM_ADVANCED; MWT_LEFTMULTIPLY), SetMiterLimit (recorded as an integer, kept as the float);
//   * the poly records in 16-bit form when every coordinate fits, else 32-bit; paths (BEGINPATH ..
//     ENDPATH, the records inside one carry empty bounds), FillPath / StrokePath / StrokeAndFillPath /
//     SelectClipPath; PatBlt as EMR_BITBLT; FillRgn; ExtSelectClipRgn / IntersectClipRect;
//     StretchDIBits; ExtTextOutW; GdiComment;
//   * bounds, in device pixels, inclusive: a primitive's points through the world transform (in
//     28.4, as the kernel holds them), floor and ceiling, a -To record's current position included;
//     a stroke grows by the selected pen's reach -- geometric: (half its 28.4 width + 1 pixel) times
//     the miter limit with a miter join, times 1.5 with square caps; cosmetic: nothing, a wide
//     cosmetic one like a geometric round one -- per axis through the transform; a blit's rectangle
//     rounds to whole pixels; everything is clipped to the clip region's box. The header's bounds are
//     the union of the records'.
//

using System.Collections.Generic;
using System.Text;

namespace System.Drawing.WebGpuBackend.Gdip
{
    internal sealed class GpEmfDc
    {
        // ---- objects -------------------------------------------------------------------------------

        internal enum ObjKind { Pen, Brush, Font, Palette }

        internal abstract class GdiObject
        {
            internal int StockId = -1;
            internal abstract ObjKind Kind { get; }
            internal bool IsStock => StockId >= 0;
            internal abstract byte[] CreateRecord(int slot, out int type);
        }

        internal sealed class PenObject : GdiObject
        {
            public bool Ext;               // ExtCreatePen (else CreatePen)
            public int Style;              // PS_* with end cap, join, type
            public int Width;
            public int BrushStyle;         // ExtCreatePen's LOGBRUSH
            public uint Color;
            public uint Hatch;
            internal override ObjKind Kind => ObjKind.Pen;
            public bool Null => (Style & 0xf) == 5;
            public bool Geometric => Ext ? (Style & 0x10000) != 0 : Width > 1;

            internal override byte[] CreateRecord(int slot, out int type)
            {
                if (!Ext)
                {
                    type = 38;      // EMR_CREATEPEN: ihPen, LOGPEN (style, width.x, width.y, color)
                    var b = new byte[20];
                    Le.W32(b, 0, slot); Le.W32(b, 4, Style); Le.W32(b, 8, Width); Le.W32(b, 12, 0); Le.W32(b, 16, (int)Color);
                    return b;
                }
                type = 95;          // EMR_EXTCREATEPEN: ihPen, offBmi, cbBmi, offBits, cbBits, EXTLOGPEN32
                var e = new byte[48];
                Le.W32(e, 0, slot);
                Le.W32(e, 4, 0x38); Le.W32(e, 8, 0); Le.W32(e, 12, 0x38); Le.W32(e, 16, 0);
                Le.W32(e, 20, Style); Le.W32(e, 24, Width); Le.W32(e, 28, BrushStyle); Le.W32(e, 32, (int)Color);
                Le.W32(e, 36, (int)Hatch); Le.W32(e, 40, 0);
                return e;
            }
        }

        internal sealed class BrushObject : GdiObject
        {
            public int Style;              // BS_SOLID 0, BS_NULL 1, BS_HATCHED 2, BS_DIBPATTERNPT 6
            public uint Color;
            public uint Hatch;
            public byte[] Bmi;             // a DIB pattern's BITMAPINFO (header and colours)
            public byte[] Bits;
            public int Usage;
            internal override ObjKind Kind => ObjKind.Brush;

            internal override byte[] CreateRecord(int slot, out int type)
            {
                if (Bmi == null)
                {
                    type = 39;      // EMR_CREATEBRUSHINDIRECT: ihBrush, LOGBRUSH32
                    var b = new byte[16];
                    Le.W32(b, 0, slot); Le.W32(b, 4, Style); Le.W32(b, 8, (int)Color); Le.W32(b, 12, (int)Hatch);
                    return b;
                }
                // EMR_CREATEDIBPATTERNBRUSHPT: ihBrush, iUsage, offBmi, cbBmi, offBits, cbBits, and a
                // dword GDI leaves unwritten (whatever its buffer held; zero here) before the BITMAPINFO.
                type = 94;
                int nb = Bmi.Length, nbits = Bits.Length;
                var r = new byte[28 + nb + nbits];
                Le.W32(r, 0, slot); Le.W32(r, 4, Usage);
                Le.W32(r, 8, 0x24); Le.W32(r, 12, nb); Le.W32(r, 16, 0x24 + nb); Le.W32(r, 20, nbits);
                Buffer.BlockCopy(Bmi, 0, r, 28, nb);
                Buffer.BlockCopy(Bits, 0, r, 28 + nb, nbits);
                return r;
            }
        }

        internal sealed class FontObject : GdiObject
        {
            public byte[] LogFont;         // LOGFONTW, 92 bytes
            internal override ObjKind Kind => ObjKind.Font;

            internal override byte[] CreateRecord(int slot, out int type)
            {
                type = 82;          // EMR_EXTCREATEFONTINDIRECTW: ihFont, ENUMLOGFONTEXDVW (a LOGFONTW and no names)
                var r = new byte[360];
                Le.W32(r, 0, slot);
                Buffer.BlockCopy(LogFont, 0, r, 4, Math.Min(92, LogFont.Length));
                Le.W32(r, 352, 0x08007664);     // DESIGNVECTOR: STAMP_DESIGNVECTOR, no axes
                Le.W32(r, 356, 0);
                return r;
            }
        }

        sealed class StockObject : GdiObject
        {
            readonly ObjKind _kind;
            public StockObject(int id, ObjKind kind) { StockId = id; _kind = kind; }
            internal override ObjKind Kind => _kind;
            internal override byte[] CreateRecord(int slot, out int type) { type = 0; return null; }
        }

        static readonly Dictionary<int, GdiObject> s_stock = new Dictionary<int, GdiObject>();

        /// <summary>GetStockObject: one object per id, process-wide, as GDI's are.</summary>
        public static GdiObject Stock(int id)
        {
            lock (s_stock)
                return StockLocked(id);
        }

        static GdiObject StockLocked(int id)
        {
            if (s_stock.TryGetValue(id, out GdiObject o)) return o;
            ObjKind k = id <= 5 ? ObjKind.Brush : id <= 8 ? ObjKind.Pen : id == 15 ? ObjKind.Palette : ObjKind.Font;
            o = id == 8 ? new PenObject { StockId = 8, Style = 5 }
              : id == 7 ? new PenObject { StockId = 7, Style = 0, Width = 0 }
              : id == 6 ? new PenObject { StockId = 6, Style = 0, Width = 0, Color = 0xffffff }
              : (GdiObject)new StockObject(id, k);
            s_stock[id] = o;
            return o;
        }

        public static PenObject ExtCreatePen(int style, int width, int brushStyle, uint color, uint hatch)
            => new PenObject { Ext = true, Style = style, Width = width, BrushStyle = brushStyle, Color = color, Hatch = hatch };

        public static PenObject CreatePen(int style, int width, uint color)
            => new PenObject { Ext = false, Style = style, Width = width, Color = color };

        public static BrushObject CreateSolidBrush(uint color) => new BrushObject { Style = 0, Color = color & 0xffffff };

        /// <summary>CreateDIBPatternBrushPt(packed DIB, usage): GDI fills the header's biSizeImage.</summary>
        public static BrushObject CreateDIBPatternBrushPt(byte[] packed, int usage)
        {
            int hdr = Le.I32(packed, 0);
            int w = Le.I32(packed, 4), h = Math.Abs(Le.I32(packed, 8));
            int bpp = Le.U16(packed, 14);
            int colors = Le.I32(packed, 32);
            if (colors == 0 && bpp <= 8) colors = 1 << bpp;
            int nbmi = hdr + colors * (usage == 1 ? 2 : 4);
            int stride = ((w * bpp + 31) >> 5) << 2;
            int nbits = stride * h;
            var bmi = new byte[nbmi];
            Buffer.BlockCopy(packed, 0, bmi, 0, nbmi);
            if (Le.I32(bmi, 16) == 0) Le.W32(bmi, 20, nbits);
            var bits = new byte[nbits];
            Buffer.BlockCopy(packed, nbmi, bits, 0, Math.Min(nbits, packed.Length - nbmi));
            return new BrushObject { Style = 5, Bmi = bmi, Bits = bits, Usage = usage };
        }

        public static FontObject CreateFontIndirect(byte[] logfont) => new FontObject { LogFont = logfont };

        readonly List<bool> _slots = new List<bool> { true };   // slot 0 is the metafile's own
        // The objects this file holds and their slots: a GDI object lives on after the metafile it
        // was recorded in, and the next file records it afresh.
        readonly Dictionary<GdiObject, int> _slotOf = new Dictionary<GdiObject, int>();

        // Freed slots are reused last freed first.
        readonly List<int> _free = new List<int>();

        int AllocSlot()
        {
            if (_free.Count > 0)
            {
                int s = _free[_free.Count - 1];
                _free.RemoveAt(_free.Count - 1);
                _slots[s] = true;
                return s;
            }
            _slots.Add(true);
            return _slots.Count - 1;
        }

        void Record(GdiObject o)
        {
            if (o.IsStock || _slotOf.ContainsKey(o)) return;
            int slot = AllocSlot();
            _slotOf[o] = slot;
            _w.UseHandle(slot);
            byte[] body = o.CreateRecord(slot, out int type);
            _w.Add(type, body);
        }

        int Handle(GdiObject o) => o.IsStock ? unchecked((int)0x80000000) | o.StockId : _slotOf[o];

        /// <summary>SelectObject: the object recorded on first use, its selection always.</summary>
        public GdiObject SelectObject(GdiObject o)
        {
            Record(o);
            GdiObject old;
            switch (o.Kind)
            {
                case ObjKind.Pen: old = _s.Pen; _s.Pen = (PenObject)o; break;
                case ObjKind.Brush: old = _s.Brush; _s.Brush = o; break;
                case ObjKind.Font: old = _s.Font; _s.Font = o; break;
                default: old = _s.Palette; _s.Palette = o; break;
            }
            _w.Add(37, I32s(Handle(o)));
            return old;
        }

        /// <summary>DeleteObject: recorded only for an object the file holds.</summary>
        public void DeleteObject(GdiObject o)
        {
            if (o == null || o.IsStock || !_slotOf.TryGetValue(o, out int slot)) return;
            _w.Add(40, I32s(slot));
            _slots[slot] = false;
            _free.Add(slot);
            _slotOf.Remove(o);
        }

        // ---- the DC's state ------------------------------------------------------------------------

        sealed class State
        {
            public float M11 = 1, M12, M21, M22 = 1, Dx, Dy;   // the world transform (XFORM)
            public int GraphicsMode = 1;
            public PenObject Pen;
            public GdiObject Brush, Font, Palette;
            public int PolyFill = 1, Rop2 = 13, BkMode = 2, StretchMode = 1, MapMode = 1, Icm = 1, ArcDir = 1;
            public float Miter = 10f;
            public uint TextColor, BkColor = 0xffffff, TextAlign;
            public int VpOrgX, VpOrgY, WinOrgX, WinOrgY;
            public DpRegion Clip;           // device; null: none
            public int CurX, CurY;
            public State Clone() => (State)MemberwiseClone();
        }

        readonly GpEmfWriter _w;
        State _s = new State();
        readonly List<State> _saved = new List<State>();
        bool _inPath;
        bool _pathHas;
        long _pl, _pt, _pr, _pb;            // the open path's points, 28.4 device

        public GpEmfDc(GpEmfWriter w)
        {
            _w = w;
            _s.Pen = (PenObject)Stock(7);
            _s.Brush = Stock(0);
            _s.Font = Stock(13);
            _s.Palette = Stock(15);
        }

        public GpEmfWriter Writer => _w;
        public int SaveLevel => _saved.Count;
        public int GetGraphicsMode() => _s.GraphicsMode;
        public int GetMapMode() => _s.MapMode;
        public int GetROP2() => _s.Rop2;
        public uint GetBkColor() => _s.BkColor;
        public uint GetTextColor() => _s.TextColor;
        public Point GetViewportOrg() => new Point(_s.VpOrgX, _s.VpOrgY);
        public Point GetWindowOrg() => new Point(_s.WinOrgX, _s.WinOrgY);
        public bool HasClip => _s.Clip != null;
        public GdiObject SelectedPen => _s.Pen;
        public GdiObject SelectedBrush => _s.Brush;
        public float[] WorldTransform => new[] { _s.M11, _s.M12, _s.M21, _s.M22, _s.Dx, _s.Dy };

        static byte[] I32s(params int[] v)
        {
            var b = new byte[v.Length * 4];
            for (int i = 0; i < v.Length; i++) Le.W32(b, i * 4, v[i]);
            return b;
        }

        public int SaveDC()
        {
            _saved.Add(_s.Clone());
            _w.Add(33, null);
            return _saved.Count;
        }

        /// <summary>RestoreDC: a positive level is recorded relative to the current one.</summary>
        public bool RestoreDC(int level)
        {
            int depth = _saved.Count;
            int target = level < 0 ? depth + level : level - 1;
            if (target < 0 || target >= depth) return false;
            int rel = target - depth;
            _w.Add(34, I32s(rel));
            _s = _saved[target];
            _saved.RemoveRange(target, depth - target);
            return true;
        }

        public int SetGraphicsMode(int mode)
        {
            int old = _s.GraphicsMode;
            if (mode == 1 && !(_s.M11 == 1 && _s.M12 == 0 && _s.M21 == 0 && _s.M22 == 1 && _s.Dx == 0 && _s.Dy == 0)) return 0;
            _s.GraphicsMode = mode;
            return old;
        }

        /// <summary>ModifyWorldTransform: MWT_IDENTITY (1), MWT_LEFTMULTIPLY (2), MWT_RIGHTMULTIPLY (3).</summary>
        public bool ModifyWorldTransform(float[] x, int mode)
        {
            if (_s.GraphicsMode != 2 && mode != 1) return false;
            var b = new byte[28];
            if (x != null) for (int i = 0; i < 6; i++) Le.WF(b, i * 4, x[i]);
            else { Le.WF(b, 0, 1f); Le.WF(b, 12, 1f); }
            Le.W32(b, 24, mode);
            _w.Add(36, b);
            if (mode == 1) { _s.M11 = 1; _s.M12 = 0; _s.M21 = 0; _s.M22 = 1; _s.Dx = 0; _s.Dy = 0; return true; }
            float a11 = x[0], a12 = x[1], a21 = x[2], a22 = x[3], adx = x[4], ady = x[5];
            float b11 = _s.M11, b12 = _s.M12, b21 = _s.M21, b22 = _s.M22, bdx = _s.Dx, bdy = _s.Dy;
            if (mode == 3) { (a11, b11) = (b11, a11); (a12, b12) = (b12, a12); (a21, b21) = (b21, a21); (a22, b22) = (b22, a22); (adx, bdx) = (bdx, adx); (ady, bdy) = (bdy, ady); }
            // left multiply: new = x * old
            _s.M11 = a11 * b11 + a12 * b21;
            _s.M12 = a11 * b12 + a12 * b22;
            _s.M21 = a21 * b11 + a22 * b21;
            _s.M22 = a21 * b12 + a22 * b22;
            _s.Dx = adx * b11 + ady * b21 + bdx;
            _s.Dy = adx * b12 + ady * b22 + bdy;
            return true;
        }

        public bool SetWorldTransform(float[] x)
        {
            if (_s.GraphicsMode != 2) return false;
            var b = new byte[24];
            for (int i = 0; i < 6; i++) Le.WF(b, i * 4, x[i]);
            _w.Add(35, b);
            _s.M11 = x[0]; _s.M12 = x[1]; _s.M21 = x[2]; _s.M22 = x[3]; _s.Dx = x[4]; _s.Dy = x[5];
            return true;
        }

        public int SetICMMode(int mode) { int o = _s.Icm; _s.Icm = mode; _w.Add(98, I32s(mode)); return o; }
        public int SetPolyFillMode(int mode) { int o = _s.PolyFill; _s.PolyFill = mode; _w.Add(19, I32s(mode)); return o; }
        public int SetROP2(int rop) { int o = _s.Rop2; _s.Rop2 = rop; _w.Add(20, I32s(rop)); return o; }
        public int SetStretchBltMode(int mode) { int o = _s.StretchMode; _s.StretchMode = mode; _w.Add(21, I32s(mode)); return o; }
        public int SetBkMode(int mode) { int o = _s.BkMode; _s.BkMode = mode; _w.Add(18, I32s(mode)); return o; }
        public uint SetTextAlign(uint a) { uint o = _s.TextAlign; _s.TextAlign = a; _w.Add(22, I32s((int)a)); return o; }
        public uint SetTextColor(uint c) { uint o = _s.TextColor; _s.TextColor = c; _w.Add(24, I32s((int)c)); return o; }
        public uint SetBkColor(uint c) { uint o = _s.BkColor; _s.BkColor = c; _w.Add(25, I32s((int)c)); return o; }
        public int SetMapMode(int m) { int o = _s.MapMode; _s.MapMode = m; _w.Add(17, I32s(m)); return o; }
        public void SetViewportOrgEx(int x, int y) { _s.VpOrgX = x; _s.VpOrgY = y; _w.Add(12, I32s(x, y)); }
        public void SetWindowOrgEx(int x, int y) { _s.WinOrgX = x; _s.WinOrgY = y; _w.Add(10, I32s(x, y)); }

        /// <summary>SetMiterLimit: GDI records the limit as an integer and keeps the float.</summary>
        public float SetMiterLimit(float limit)
        {
            float o = _s.Miter;
            if (limit < 1f) return o;
            _s.Miter = limit;
            _w.Add(58, I32s((int)limit));
            return o;
        }

        public void GdiComment(byte[] data) => _w.Comment(data, 0, data.Length);

        // ---- bounds --------------------------------------------------------------------------------

        static long Fix(double v) => (long)Math.Floor(v * 16.0 + 0.5);

        void ToDevice(int x, int y, out long fx, out long fy)
        {
            double dx = (double)_s.M11 * x + (double)_s.M21 * y + _s.Dx;
            double dy = (double)_s.M12 * x + (double)_s.M22 * y + _s.Dy;
            fx = Fix(dx); fy = Fix(dy);
        }

        // The selected pen's reach beyond the points, 28.4, per axis.
        void PenReach(out long ex, out long ey)
        {
            ex = ey = 0;
            PenObject p = _s.Pen;
            if (p == null || p.Null) return;
            bool geometric = p.Ext ? (p.Style & 0x10000) != 0 : p.Width > 1;
            if (!geometric) return;
            int join = p.Ext ? p.Style & 0xf000 : 0;
            int cap = p.Ext ? p.Style & 0xf00 : 0;
            double k = join == 0x2000 ? _s.Miter : 1.0;
            if (cap == 0x100) k *= 1.5;
            double sx = Math.Abs((double)_s.M11) + Math.Abs((double)_s.M21);
            double sy = Math.Abs((double)_s.M12) + Math.Abs((double)_s.M22);
            long hx = Fix(p.Width * sx) >> 1, hy = Fix(p.Width * sy) >> 1;
            ex = (long)Math.Ceiling(k * (hx + 16));
            ey = (long)Math.Ceiling(k * (hy + 16));
        }

        static readonly int[] EmptyBox = { 0, 0, -1, -1 };

        // A box in 28.4 to inclusive device pixels, clipped to the clip region's box; accumulated.
        int[] Finish(long l, long t, long r, long b, bool accumulate = true)
        {
            int il = (int)(l >> 4), it = (int)(t >> 4), ir = (int)((r + 15) >> 4), ib = (int)((b + 15) >> 4);
            return Clip(il, it, ir, ib, accumulate);
        }

        int[] Clip(int il, int it, int ir, int ib, bool accumulate)
        {
            if (_s.Clip != null)
            {
                if (_s.Clip.IsEmpty) return EmptyBox;
                Rectangle c = _s.Clip.Bounds;
                il = Math.Max(il, c.X); it = Math.Max(it, c.Y);
                ir = Math.Min(ir, c.Right - 1); ib = Math.Min(ib, c.Bottom - 1);
                if (ir < il || ib < it) return EmptyBox;
            }
            if (accumulate) _w.AddBounds(il, it, ir, ib);
            return new[] { il, it, ir, ib };
        }

        // The points' record bounds (a path takes them into its own and the record gets none).
        int[] PointBounds(int[] xy, int from, int n, bool withCurrent, bool stroke)
        {
            long l = long.MaxValue, t = long.MaxValue, r = long.MinValue, b = long.MinValue;
            void Add(int x, int y)
            {
                ToDevice(x, y, out long fx, out long fy);
                if (fx < l) l = fx;
                if (fx > r) r = fx;
                if (fy < t) t = fy;
                if (fy > b) b = fy;
            }
            if (withCurrent) Add(_s.CurX, _s.CurY);
            for (int i = 0; i < n; i++) Add(xy[(from + i) * 2], xy[(from + i) * 2 + 1]);
            if (l > r) return EmptyBox;
            if (_inPath)
            {
                if (!_pathHas) { _pl = l; _pt = t; _pr = r; _pb = b; _pathHas = true; }
                else { _pl = Math.Min(_pl, l); _pt = Math.Min(_pt, t); _pr = Math.Max(_pr, r); _pb = Math.Max(_pb, b); }
                return EmptyBox;
            }
            if (stroke)
            {
                PenReach(out long ex, out long ey);
                l -= ex; r += ex; t -= ey; b += ey;
            }
            return Finish(l, t, r, b);
        }

        static void Box(byte[] rec, int o, int[] box)
        {
            Le.W32(rec, o, box[0]); Le.W32(rec, o + 4, box[1]); Le.W32(rec, o + 8, box[2]); Le.W32(rec, o + 12, box[3]);
        }

        static bool Fits16(int[] xy, int from, int n)
        {
            for (int i = from * 2; i < (from + n) * 2; i++)
                if (xy[i] < short.MinValue || xy[i] > short.MaxValue) return false;
            return true;
        }

        // ---- drawing -------------------------------------------------------------------------------

        // EMR_POLYBEZIER (2/85), POLYGON (3/86), POLYLINE (4/87), POLYBEZIERTO (5/88), POLYLINETO (6/89).
        void Poly(int type32, int type16, int[] xy, int n, bool withCurrent, bool stroke)
        {
            int[] box = PointBounds(xy, 0, n, withCurrent, stroke);
            bool s16 = Fits16(xy, 0, n);
            int size = 20 + n * (s16 ? 4 : 8);
            var b = new byte[size];
            Box(b, 0, box);
            Le.W32(b, 16, n);
            for (int i = 0; i < n; i++)
            {
                if (s16) { Le.W16(b, 20 + i * 4, xy[i * 2]); Le.W16(b, 22 + i * 4, xy[i * 2 + 1]); }
                else { Le.W32(b, 20 + i * 8, xy[i * 2]); Le.W32(b, 24 + i * 8, xy[i * 2 + 1]); }
            }
            _w.Add(s16 ? type16 : type32, b);
        }

        public bool Polygon(int[] xy, int n) { if (n < 2) return false; Poly(3, 86, xy, n, false, true); return true; }

        public bool Polyline(int[] xy, int n) { if (n < 2) return false; Poly(4, 87, xy, n, false, true); return true; }

        public bool PolyBezier(int[] xy, int n)
        {
            if (n < 1 || n % 3 != 1) return false;
            Poly(2, 85, xy, n, false, true);
            return true;
        }

        public bool PolyBezierTo(int[] xy, int n)
        {
            if (n < 1 || n % 3 != 0) return false;
            Poly(5, 88, xy, n, true, true);
            _s.CurX = xy[(n - 1) * 2]; _s.CurY = xy[(n - 1) * 2 + 1];
            return true;
        }

        public bool PolylineTo(int[] xy, int n)
        {
            if (n < 1) return false;
            Poly(6, 89, xy, n, true, true);
            _s.CurX = xy[(n - 1) * 2]; _s.CurY = xy[(n - 1) * 2 + 1];
            return true;
        }

        // EMR_POLYPOLYLINE (7/90), EMR_POLYPOLYGON (8/91).
        void PolyPoly(int type32, int type16, int[] xy, int[] counts, int npoly)
        {
            int total = 0;
            for (int i = 0; i < npoly; i++) total += counts[i];
            int[] box = PointBounds(xy, 0, total, false, true);
            bool s16 = Fits16(xy, 0, total);
            var b = new byte[24 + npoly * 4 + total * (s16 ? 4 : 8)];
            Box(b, 0, box);
            Le.W32(b, 16, npoly);
            Le.W32(b, 20, total);
            for (int i = 0; i < npoly; i++) Le.W32(b, 24 + i * 4, counts[i]);
            int o = 24 + npoly * 4;
            for (int i = 0; i < total; i++)
            {
                if (s16) { Le.W16(b, o + i * 4, xy[i * 2]); Le.W16(b, o + i * 4 + 2, xy[i * 2 + 1]); }
                else { Le.W32(b, o + i * 8, xy[i * 2]); Le.W32(b, o + i * 8 + 4, xy[i * 2 + 1]); }
            }
            _w.Add(s16 ? type16 : type32, b);
        }

        public bool PolyPolygon(int[] xy, int[] counts, int npoly) { if (npoly < 1) return false; PolyPoly(8, 91, xy, counts, npoly); return true; }

        public bool PolyPolyline(int[] xy, int[] counts, int npoly) { if (npoly < 1) return false; PolyPoly(7, 90, xy, counts, npoly); return true; }

        public bool MoveToEx(int x, int y)
        {
            _w.Add(27, I32s(x, y));
            if (_inPath) PointBounds(new[] { x, y }, 0, 1, false, false);
            _s.CurX = x; _s.CurY = y;
            return true;
        }

        public bool LineTo(int x, int y)
        {
            _w.Add(54, I32s(x, y));
            PointBounds(new[] { x, y }, 0, 1, true, true);
            _s.CurX = x; _s.CurY = y;
            return true;
        }

        public bool BeginPath()
        {
            _w.Add(59, null);
            _inPath = true;
            _pathHas = false;
            return true;
        }

        public bool EndPath()
        {
            _w.Add(60, null);
            _inPath = false;
            return true;
        }

        public bool CloseFigure() { _w.Add(61, null); return true; }

        int[] PathBounds(bool stroke)
        {
            if (!_pathHas) return EmptyBox;
            long l = _pl, t = _pt, r = _pr, b = _pb;
            if (stroke)
            {
                PenReach(out long ex, out long ey);
                l -= ex; r += ex; t -= ey; b += ey;
            }
            return Finish(l, t, r, b);
        }

        void PathOp(int type, bool stroke)
        {
            var b = new byte[16];
            Box(b, 0, PathBounds(stroke));
            _w.Add(type, b);
            _pathHas = false;
        }

        public bool FillPath() { PathOp(62, false); return true; }
        public bool StrokeAndFillPath() { PathOp(63, true); return true; }
        public bool StrokePath() { PathOp(64, true); return true; }

        /// <summary>SelectClipPath: the path becomes (or combines into) the clip region.</summary>
        public bool SelectClipPath(int mode, DpRegion path)
        {
            _w.Add(67, I32s(mode));
            CombineClip(path, mode);
            _pathHas = false;
            return true;
        }

        /// <summary>PatBlt, recorded as EMR_BITBLT with no source.</summary>
        public bool PatBlt(int x, int y, int w, int h, int rop)
        {
            var b = new byte[92];
            ToDevice(x, y, out long fx0, out long fy0);
            ToDevice(x + w, y + h, out long fx1, out long fy1);
            int l = (int)((Math.Min(fx0, fx1) + 8) >> 4), r = (int)((Math.Max(fx0, fx1) + 8) >> 4);
            int t = (int)((Math.Min(fy0, fy1) + 8) >> 4), bt = (int)((Math.Max(fy0, fy1) + 8) >> 4);
            int[] box = r > l && bt > t ? Clip(l, t, r - 1, bt - 1, true) : EmptyBox;
            Box(b, 0, box);
            Le.W32(b, 16, x); Le.W32(b, 20, y); Le.W32(b, 24, w); Le.W32(b, 28, h);
            Le.W32(b, 32, rop);
            Le.WF(b, 44, 1f); Le.WF(b, 56, 1f);
            _w.Add(76, b);
            return true;
        }

        /// <summary>The RGNDATA of a device region: its rectangles band by band.</summary>
        static byte[] RgnData(DpRegion rgn, out Rectangle bound)
        {
            var rects = new List<Rectangle>(rgn.Rects());
            bound = rects.Count == 0 ? Rectangle.Empty : rgn.Bounds;
            var b = new byte[32 + rects.Count * 16];
            Le.W32(b, 0, 32); Le.W32(b, 4, 1); Le.W32(b, 8, rects.Count); Le.W32(b, 12, rects.Count * 16);
            Le.W32(b, 16, bound.Left); Le.W32(b, 20, bound.Top); Le.W32(b, 24, bound.Right); Le.W32(b, 28, bound.Bottom);
            for (int i = 0; i < rects.Count; i++)
            {
                Le.W32(b, 32 + i * 16, rects[i].Left); Le.W32(b, 36 + i * 16, rects[i].Top);
                Le.W32(b, 40 + i * 16, rects[i].Right); Le.W32(b, 44 + i * 16, rects[i].Bottom);
            }
            return b;
        }

        /// <summary>FillRgn: the brush recorded if the file does not hold it yet; no selection.</summary>
        public bool FillRgn(DpRegion rgn, GdiObject brush)
        {
            Record(brush);
            byte[] data = RgnData(rgn, out Rectangle bd);
            int[] box = bd.Width > 0 && bd.Height > 0 ? Clip(bd.Left, bd.Top, bd.Right - 1, bd.Bottom - 1, true) : EmptyBox;
            var b = new byte[24 + data.Length];
            Box(b, 0, box);
            Le.W32(b, 16, data.Length);
            Le.W32(b, 20, Handle(brush));
            Buffer.BlockCopy(data, 0, b, 24, data.Length);
            _w.Add(71, b);
            return true;
        }

        void CombineClip(DpRegion rgn, int mode)
        {
            // RGN_AND 1, RGN_OR 2, RGN_XOR 3, RGN_DIFF 4, RGN_COPY 5; no clip is the whole surface.
            if (mode == 5) { _s.Clip = rgn?.Clone(); return; }
            if (rgn == null) return;
            DpRegion cur = _s.Clip ?? DpRegion.Infinite();
            DpRegion.Op op = mode == 1 ? DpRegion.Op.And : mode == 2 ? DpRegion.Op.Or : mode == 3 ? DpRegion.Op.Xor : DpRegion.Op.Exclude;
            _s.Clip = DpRegion.Combine(cur, rgn, op);
        }

        /// <summary>ExtSelectClipRgn (a null region with RGN_COPY removes the clip).</summary>
        public int ExtSelectClipRgn(DpRegion rgn, int mode)
        {
            if (rgn == null)
            {
                _w.Add(75, I32s(0, mode));
                if (mode == 5) _s.Clip = null;
                return 1;
            }
            byte[] data = RgnData(rgn, out _);
            var b = new byte[8 + data.Length];
            Le.W32(b, 0, data.Length);
            Le.W32(b, 4, mode);
            Buffer.BlockCopy(data, 0, b, 8, data.Length);
            _w.Add(75, b);
            CombineClip(rgn, mode);
            return 1;
        }

        public int SelectClipRgn(DpRegion rgn) => ExtSelectClipRgn(rgn, 5);

        public int IntersectClipRect(int l, int t, int r, int b)
        {
            _w.Add(30, I32s(l, t, r, b));
            ToDevice(l, t, out long fx0, out long fy0);
            ToDevice(r, b, out long fx1, out long fy1);
            int x0 = (int)((Math.Min(fx0, fx1) + 8) >> 4), x1 = (int)((Math.Max(fx0, fx1) + 8) >> 4);
            int y0 = (int)((Math.Min(fy0, fy1) + 8) >> 4), y1 = (int)((Math.Max(fy0, fy1) + 8) >> 4);
            DpRegion rr = DpRegion.FromRect(x0, y0, x1 - x0, y1 - y0);
            _s.Clip = _s.Clip == null ? rr : DpRegion.Combine(_s.Clip, rr, DpRegion.Op.And);
            return 1;
        }

        /// <summary>StretchDIBits: the destination rectangle's pixels are the record's bounds.</summary>
        public int StretchDIBits(int xDest, int yDest, int wDest, int hDest, int xSrc, int ySrc, int wSrc, int hSrc,
            byte[] bmi, byte[] bits, int usage, int rop)
        {
            ToDevice(xDest, yDest, out long fx0, out long fy0);
            ToDevice(xDest + wDest, yDest + hDest, out long fx1, out long fy1);
            int l = (int)((Math.Min(fx0, fx1) + 8) >> 4), r = (int)((Math.Max(fx0, fx1) + 8) >> 4);
            int t = (int)((Math.Min(fy0, fy1) + 8) >> 4), bt = (int)((Math.Max(fy0, fy1) + 8) >> 4);
            int[] box = r > l && bt > t ? Clip(l, t, r - 1, bt - 1, true) : EmptyBox;
            int nb = bmi.Length, nbits = bits?.Length ?? 0;
            var b = new byte[72 + nb + nbits];
            Box(b, 0, box);
            Le.W32(b, 16, xDest); Le.W32(b, 20, yDest); Le.W32(b, 24, xSrc); Le.W32(b, 28, ySrc);
            Le.W32(b, 32, wSrc); Le.W32(b, 36, hSrc);
            Le.W32(b, 40, 80); Le.W32(b, 44, nb); Le.W32(b, 48, 80 + nb); Le.W32(b, 52, nbits);
            Le.W32(b, 56, usage); Le.W32(b, 60, rop); Le.W32(b, 64, wDest); Le.W32(b, 68, hDest);
            Buffer.BlockCopy(bmi, 0, b, 72, nb);
            if (nbits > 0) Buffer.BlockCopy(bits, 0, b, 72 + nb, nbits);
            _w.Add(81, b);
            return hSrc;
        }

        /// <summary>ExtTextOutW as an EMF DC records it (gdi32full MF_ExtTextOut @180062c00,
        /// MREXTTEXTOUT::bInit @180060300, MTEXT::bInit @180069f50): the string and its advances --
        /// the caller's, or with none GetTextExtentExPoint's partial extents of the string taken as
        /// CHARACTERS (an ETO_GLYPH_INDEX string too) -- in GM_COMPATIBLE the reference device's
        /// millimetres per hundred pixels as the scales, and as bounds what the kernel accumulates
        /// for the draw (MDC::vFlushBounds @18005d358: win32k's box through the meta and clip
        /// bounds, made inclusive); see <see cref="TextBox"/>.</summary>
        public bool ExtTextOutW(int x, int y, int options, int[] clip, string s, int[] dx)
        {
            int n = s.Length;
            GpGdiFont font = _s.Font is FontObject fo ? GpGdiFont.FromLogFont(fo.LogFont) : null;
            int[] fxPens = null;        // a turned realization's own pens (28.4) when the caller gave no dx
            if (dx == null && n > 0)
            {
                dx = new int[n];
                if (font != null && font.Escapement % 1800 != 0)
                {
                    // A turned realization's advances are not hinted: vFillGLYPHDATA @140012568
                    // (fontdrvhost) gives a quarter turn the notional advance times the base
                    // vector's length rounded to the whole pixel ((x >> 3) + 1 >> 1), any other
                    // angle that product rounded to 28.4 only -- and GetTextExtentExPoint's
                    // extents are then the running sum's pixels.
                    Microsoft.Wpf.Interop.WebGpu.Composition.Text.TrueTypeFont face = font.Face;
                    float len = 16f * font.Ppem / face.UnitsPerEmForHinting;
                    bool quarter = font.Escapement % 900 == 0;
                    long sum = 0; int prev = 0;
                    fxPens = new int[n + 1];
                    for (int i = 0; i < n; i++)
                    {
                        int gid = (options & 0x10) != 0 ? s[i] : face.GlyphIndex(s[i]);
                        float v = face.RawAdvanceWidth(gid) * len;
                        int fxd = v < 0f ? -(int)Math.Floor(-v + 0.5) : (int)Math.Floor(v + 0.5);
                        if (quarter) fxd = ((fxd >> 3) + 1 >> 1) * 16;
                        sum += fxd;
                        fxPens[i + 1] = (int)sum;
                        int cum = (int)((sum + 8) >> 4);
                        dx[i] = cum - prev;
                        prev = cum;
                    }
                }
                else
                    for (int i = 0; i < n; i++) dx[i] = font != null ? font.CharAdvance(s[i]) : 0;
            }
            int graphicsMode = _s.GraphicsMode;
            float exScale = 0f, eyScale = 0f;
            if (graphicsMode == 1)
            {
                GpRefDevice dev = _w.Device;
                exScale = (float)dev.HorzSize * _s.M11 * 100f / (float)dev.HorzRes;
                eyScale = (float)dev.VertSize * _s.M22 * 100f / (float)dev.VertRes;
            }
            int offString = 76;
            int strBytes = ((n * 2) + 3) & ~3;
            int offDx = offString + strBytes;
            var b = new byte[68 + strBytes + n * 4];
            int[] box = EmptyBox;
            if (font != null && n > 0 && TextBox(font, x, y, options, s, dx, fxPens, out int bl, out int bt, out int br, out int bb))
                box = Clip(bl, bt, br - 1, bb - 1, true);
            Box(b, 0, box);
            Le.W32(b, 16, graphicsMode);
            Le.WF(b, 20, exScale); Le.WF(b, 24, eyScale);
            Le.W32(b, 28, x); Le.W32(b, 32, y);
            Le.W32(b, 36, n);
            Le.W32(b, 40, offString);
            Le.W32(b, 44, options);
            if (clip != null && (options & 6) != 0) { Le.W32(b, 48, clip[0]); Le.W32(b, 52, clip[1]); Le.W32(b, 56, clip[2]); Le.W32(b, 60, clip[3]); }
            else { Le.W32(b, 56, -1); Le.W32(b, 60, -1); }
            Le.W32(b, 64, offDx);
            for (int i = 0; i < n; i++) Le.W16(b, 68 + i * 2, s[i]);
            for (int i = 0; i < n; i++) Le.W32(b, 68 + strBytes + i * 4, dx[i]);
            _w.Add(84, b);
            return true;
        }

        /// <summary>The box win32k accumulates for a text draw (GrepExtTextOutWLocked @1401a8a98 ->
        /// ESTROBJ::bOpaqueArea @1401ad4f0), exclusive, in device pixels. Along the baseline the
        /// glyphs' pens are the advances summed (vCharPos_H1 @1401aed88, unrotated; vCharPos_G1
        /// @1401adf80, an escapement), and the extent is the least of 0 and each glyph's ink left
        /// (GLYPHDATA fxA) past its pen, to the greatest of the total advance and each ink right
        /// (fxAB); across, the font's ascent and descent. An unrotated run is that box at the
        /// reference point (the bold simulation a pixel wider); a quarter turn the box turned, a
        /// pixel longer along the baseline; any other angle the turned box's corners in 28.4,
        /// floored and ceiled and grown by two pixels.</summary>
        bool TextBox(GpGdiFont font, int x, int y, int options, string s, int[] dx, int[] fxPens, out int l, out int t, out int r, out int b)
        {
            l = t = r = b = 0;
            ToDevice(x, y, out long fx, out long fy);
            int n = s.Length;
            bool glyphIndex = (options & 0x10) != 0;
            int A = 0, B = 0, pen = 0;
            // At an angle that is not a quarter turn, ttfd's vFillGLYPHDATA @140012568 does not
            // measure the glyph's bitmap: fxA and fxAB are its NOTIONAL left side bearing and ink
            // right (vGetNotionalGlyphMetrics) times the base vector's length, rounded to 28.4 and
            // then floored / ceiled to the pixel.
            bool notional = font.Escapement % 900 != 0;
            float len = 16f * font.Ppem / font.Face.UnitsPerEmForHinting;
            static int R(float v) => v < 0f ? -(int)Math.Floor(-v + 0.5) : (int)Math.Floor(v + 0.5);
            for (int i = 0; i < n; i++)
            {
                int gid = glyphIndex ? s[i] : font.Face.GlyphIndex(s[i]);
                (int ia, int iab) = font.Ink(gid);
                if (notional)
                {
                    if (font.Face.TryGetNotionalMetrics(gid, out int lsb, out int right, out _, out _))
                    {
                        ia = R(lsb * len) & ~15;
                        iab = (R(right * len) + 15) & ~15;
                    }
                    else ia = iab = 0;
                }
                A = Math.Min(A, pen + ia);
                B = Math.Max(B, pen + iab);
                // With no dx from the caller, GDI places the glyphs by their realized advances
                // (vCharPos_G2: the 28.4 fxD summed), not by the whole pixels it records.
                pen = fxPens != null ? fxPens[i + 1] : pen + dx[i] * 16;
            }
            B = Math.Max(B, pen);
            int asc = font.Ascent * 16, dsc = -font.Descent * 16;       // ESTROBJ +0x64 / +0x6c
            int align = (int)_s.TextAlign;
            // The notional-to-device rotation: along the baseline (m11, m12), up (m21, m22).
            // win32k's own sine (GdiTrig: bGetNtoD_Win31's efSin / efCos).
            float esin = GdiTrig.Sin(GdiTrig.Degrees(font.Escapement)), ecos = GdiTrig.Cos(GdiTrig.Degrees(font.Escapement));
            float m11 = ecos, m12 = -esin;
            float m21 = -esin, m22 = -ecos;
            if (font.Escapement % 900 == 0)
            {
                int q = ((font.Escapement / 900) % 4 + 4) % 4;
                m11 = q == 0 ? 1 : q == 2 ? -1 : 0; m12 = q == 1 ? -1 : q == 3 ? 1 : 0;
                m21 = q == 1 ? -1 : q == 3 ? 1 : 0; m22 = q == 0 ? -1 : q == 2 ? 1 : 0;
            }
            // Text alignment (ESTROBJ::vInit): the reference point moved to the baseline's left end.
            if ((align & 0x18) == 0) { fx -= (long)Math.Round(asc * m21); fy -= (long)Math.Round(asc * m22); }
            else if ((align & 0x18) == 8) { fx -= (long)Math.Round(dsc * m21); fy -= (long)Math.Round(dsc * m22); }
            if ((align & 6) == 6) { fx -= (long)Math.Round(pen / 2 * m11); fy -= (long)Math.Round(pen / 2 * m12); }
            else if ((align & 6) == 2) { fx -= (long)Math.Round(pen * m11); fy -= (long)Math.Round(pen * m12); }
            int x0 = (int)((fx + 8) >> 4), y0 = (int)((fy + 8) >> 4);
            if (font.Escapement == 0)
            {
                l = x0 + (A >> 4);
                r = x0 + ((B + 15) >> 4) + (font.Face.SynthesizesBold ? 1 : 0);
                t = y0 - ((asc + 15) >> 4);
                b = y0 - (dsc >> 4);
            }
            else if (m12 == 0f && m21 == 0f)
            {
                if (m11 < 0f) { l = x0 - ((B + 15) >> 4); r = x0 - (A >> 4); }
                else { l = x0 + (A >> 4); r = x0 + ((B + 15) >> 4); }
                r += 1;
                if (0f <= m22) { t = y0 + (dsc >> 4); b = y0 + ((asc + 15) >> 4); }
                else { t = y0 - ((asc + 15) >> 4); b = y0 - (dsc >> 4); }
            }
            else if (m11 == 0f && m22 == 0f)
            {
                if (0f <= m21) { l = x0 + (dsc >> 4); r = x0 + ((asc + 15) >> 4); }
                else { l = x0 - ((asc + 15) >> 4); r = x0 - (dsc >> 4); }
                if (0f <= m12) { t = y0 + (A >> 4); b = y0 + ((B + 15) >> 4); }
                else { t = y0 - ((B + 15) >> 4); b = y0 - (A >> 4); }
                b += 1;
            }
            else
            {
                int ax = R(A * m11), ay = R(A * m12), bx = R(B * m11), by = R(B * m12);
                int ux = R(asc * m21), uy = R(asc * m22), dxx = R(m21 * dsc), dyy = R(dsc * m22);
                long[] xs = { fx + ux + ax, fx + ux + bx, fx + dxx + bx, fx + dxx + ax };
                long[] ys = { fy + uy + ay, fy + uy + by, fy + dyy + by, fy + dyy + ay };
                long minX = Math.Min(Math.Min(xs[0], xs[1]), Math.Min(xs[2], xs[3])), maxX = Math.Max(Math.Max(xs[0], xs[1]), Math.Max(xs[2], xs[3]));
                long minY = Math.Min(Math.Min(ys[0], ys[1]), Math.Min(ys[2], ys[3])), maxY = Math.Max(Math.Max(ys[0], ys[1]), Math.Max(ys[2], ys[3]));
                l = (int)(minX >> 4) - 2; t = (int)(minY >> 4) - 2;
                r = (int)((maxX + 15) >> 4) + 2; b = (int)((maxY + 15) >> 4) + 2;
            }
            return l < r && t < b;
        }

        // ---- GDI drawing a caller did on the metafile's HDC -----------------------------------------

        /// <summary>The records of an EMF a caller drew into through GetHdc, as if drawn on this DC:
        /// its objects take slots in this file's table, its bounds join the header's, and the state
        /// it leaves (mode, origins, ROP2, clip, save levels) is this DC's from then on.</summary>
        public void Splice(byte[] emf)
        {
            var map = new Dictionary<int, int>();
            int o = 0;
            while (o + 8 <= emf.Length)
            {
                int type = Le.I32(emf, o), size = Le.I32(emf, o + 4);
                if (size < 8 || o + size > emf.Length) break;
                if (type == 1)
                {
                    int l = Le.I32(emf, o + 8), t = Le.I32(emf, o + 12), r = Le.I32(emf, o + 16), b = Le.I32(emf, o + 20);
                    if (r >= l && b >= t) _w.AddBounds(l, t, r, b);
                    o += size;
                    continue;
                }
                if (type == 14) break;
                var rec = new byte[size];
                Buffer.BlockCopy(emf, o, rec, 0, size);
                Remap(rec, type, map);
                Track(rec, type);
                _w.AddRaw(rec);
                o += size;
            }
        }

        // Where a record names an object: (offset of the index, whether it creates or deletes one).
        static int HandleOffset(int type, out int kind)
        {
            kind = 0;
            switch (type)
            {
                case 38: case 39: case 49: case 82: case 93: case 94: case 95: case 99: case 122: kind = 1; return 8;
                case 40: case 101: kind = 2; return 8;
                case 37: case 48: case 50: case 51: case 100: return 8;
                case 71: return 28;     // FILLRGN ihBrush
                case 72: return 28;     // FRAMERGN ihBrush
                default: return -1;
            }
        }

        void Remap(byte[] rec, int type, Dictionary<int, int> map)
        {
            int at = HandleOffset(type, out int kind);
            if (at < 0 || at + 4 > rec.Length) return;
            int h = Le.I32(rec, at);
            if ((h & unchecked((int)0x80000000)) != 0) return;
            if (kind == 1)
            {
                int slot = AllocSlot();
                _w.UseHandle(slot);
                map[h] = slot;
                Le.W32(rec, at, slot);
                return;
            }
            if (!map.TryGetValue(h, out int mine)) return;
            Le.W32(rec, at, mine);
            if (kind == 2)
            {
                _slots[mine] = false;
                _free.Add(mine);
                map.Remove(h);
            }
        }

        void Track(byte[] rec, int type)
        {
            switch (type)
            {
                case 33: _saved.Add(_s.Clone()); break;
                case 34:
                {
                    int rel = Le.I32(rec, 8);
                    int target = rel < 0 ? _saved.Count + rel : rel - 1;
                    if (target >= 0 && target < _saved.Count) { _s = _saved[target]; _saved.RemoveRange(target, _saved.Count - target); }
                    break;
                }
                case 17: _s.MapMode = Le.I32(rec, 8); break;
                case 12: _s.VpOrgX = Le.I32(rec, 8); _s.VpOrgY = Le.I32(rec, 12); break;
                case 10: _s.WinOrgX = Le.I32(rec, 8); _s.WinOrgY = Le.I32(rec, 12); break;
                case 20: _s.Rop2 = Le.I32(rec, 8); break;
                case 19: _s.PolyFill = Le.I32(rec, 8); break;
                case 24: _s.TextColor = (uint)Le.I32(rec, 8); break;
                case 25: _s.BkColor = (uint)Le.I32(rec, 8); break;
                case 18: _s.BkMode = Le.I32(rec, 8); break;
                case 75:
                {
                    int cb = Le.I32(rec, 8), mode = Le.I32(rec, 12);
                    if (cb == 0) { if (mode == 5) _s.Clip = null; }
                    else
                    {
                        int n = Le.I32(rec, 16 + 8);
                        var r = new DpRegion();
                        DpRegion acc = null;
                        for (int i = 0; i < n; i++)
                        {
                            int p = 16 + 32 + i * 16;
                            var q = DpRegion.FromRect(Le.I32(rec, p), Le.I32(rec, p + 4), Le.I32(rec, p + 8) - Le.I32(rec, p), Le.I32(rec, p + 12) - Le.I32(rec, p + 4));
                            acc = acc == null ? q : DpRegion.Combine(acc, q, DpRegion.Op.Or);
                        }
                        CombineClip(acc ?? r, mode);
                    }
                    break;
                }
                case 30:
                    _s.Clip = _s.Clip == null
                        ? DpRegion.FromRect(Le.I32(rec, 8), Le.I32(rec, 12), Le.I32(rec, 16) - Le.I32(rec, 8), Le.I32(rec, 20) - Le.I32(rec, 12))
                        : DpRegion.Combine(_s.Clip, DpRegion.FromRect(Le.I32(rec, 8), Le.I32(rec, 12), Le.I32(rec, 16) - Le.I32(rec, 8), Le.I32(rec, 20) - Le.I32(rec, 12)), DpRegion.Op.And);
                    break;
            }
        }
    }
}
