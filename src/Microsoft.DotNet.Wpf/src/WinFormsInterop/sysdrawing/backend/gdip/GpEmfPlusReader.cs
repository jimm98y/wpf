// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The EMF+ object serialisations read back into System.Drawing objects, as GDI+'s SetData methods
// read them (gdiplus.dll 10.0.26100): GpObject::Factory @18009ea30 picks the class by object type,
// GpSolidFill/GpHatch/GpTexture/GpRectGradient/GpPathGradient::SetData, GpPen::SetData,
// DpPath::SetData @18008a640 (int16 points, relative points -- GetPointsForPlayback @180094768 --
// and run-length types -- GetTypesForPlayback @180094d40), GpRegion::SetData, GpFont::SetData,
// GpStringFormat::SetData, GpImageAttributes::SetData, GpCustomLineCap/GpAdjustableArrowCap::SetData,
// CopyOnWriteBitmap::SetData and GpMetafile::SetData. A record that does not parse is skipped, as
// GDI+ skips an object it cannot make.
//

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace System.Drawing.WebGpuBackend.Gdip
{
    /// <summary>Little-endian reads over a byte range, failing (rather than throwing) past its end.</summary>
    internal sealed class GpReader
    {
        readonly byte[] _b;
        int _p;
        readonly int _end;
        public bool Ok = true;

        public GpReader(byte[] b, int o, int n) { _b = b; _p = o; _end = Math.Min(b.Length, o + Math.Max(0, n)); }

        public int Position => _p;
        public int Left => _end - _p;
        public byte[] Buffer => _b;

        bool Need(int n)
        {
            if (_p + n > _end || n < 0) { Ok = false; return false; }
            return true;
        }

        public int I32() { if (!Need(4)) return 0; int v = Le.I32(_b, _p); _p += 4; return v; }
        public uint U32() => (uint)I32();
        public short I16() { if (!Need(2)) return 0; short v = Le.I16(_b, _p); _p += 2; return v; }
        public byte U8() { if (!Need(1)) return 0; return _b[_p++]; }
        public float F() => BitConverter.Int32BitsToSingle(I32());
        public Color Argb() => Color.FromArgb(I32());
        public RectangleF Rect() { float x = F(), y = F(), w = F(), h = F(); return new RectangleF(x, y, w, h); }
        public Matrix Matrix() { float a = F(), b = F(), c = F(), d = F(), e = F(), f = F(); return new Matrix(a, b, c, d, e, f); }
        public float[] Matrix6() => new[] { F(), F(), F(), F(), F(), F() };
        public void Skip(int n) { if (Need(n)) _p += n; }
        public byte[] Bytes(int n) { if (!Need(n)) return new byte[0]; var r = new byte[n]; System.Buffer.BlockCopy(_b, _p, r, 0, n); _p += n; return r; }
        public void Align4() { int pad = (4 - ((_p) & 3)) & 3; _p = Math.Min(_end, _p + pad); }

        public float[] Floats(int n)
        {
            if (n < 0 || !Need(n * 4)) return new float[0];
            var r = new float[n];
            for (int i = 0; i < n; i++) r[i] = F();
            return r;
        }

        public Color[] Colors(int n)
        {
            if (n < 0 || !Need(n * 4)) return new Color[0];
            var r = new Color[n];
            for (int i = 0; i < n; i++) r[i] = Argb();
            return r;
        }

        /// <summary>EMF+ points: int16 (0x4000), relative (0x800) or floats.</summary>
        public PointF[] Points(int count, int flags)
        {
            if (count < 0) { Ok = false; return new PointF[0]; }
            var pts = new PointF[count];
            if ((flags & 0x800) != 0)
            {
                short x = 0, y = 0;
                for (int i = 0; i < count; i++)
                {
                    x = unchecked((short)(x + Rel()));
                    y = unchecked((short)(y + Rel()));
                    pts[i] = new PointF(x, y);
                    if (!Ok) break;
                }
                Align4();
                return pts;
            }
            if ((flags & 0x4000) != 0)
            {
                for (int i = 0; i < count; i++) { short a = I16(), b = I16(); pts[i] = new PointF(a, b); }
                return pts;
            }
            for (int i = 0; i < count; i++) { float a = F(), b = F(); pts[i] = new PointF(a, b); }
            return pts;
        }

        int Rel()
        {
            int b0 = U8();
            if ((b0 & 0x80) != 0)
                return (b0 & 0x40) != 0 ? (b0 & 0x7f) - 0x80 : (b0 & 0x7f);
            int b1 = U8();
            int v = ((b0 & 0x7f) << 8) | b1;
            if ((v & 0x4000) != 0) v -= 0x8000;
            return v;
        }

        public RectangleF[] Rects(int count, int flags)
        {
            if (count < 0) { Ok = false; return new RectangleF[0]; }
            var r = new RectangleF[count];
            for (int i = 0; i < count; i++)
            {
                if ((flags & 0x4000) != 0) { short x = I16(), y = I16(), w = I16(), h = I16(); r[i] = new RectangleF(x, y, w, h); }
                else r[i] = Rect();
            }
            return r;
        }

        /// <summary>Path point types: plain, or run-length (0x1000).</summary>
        public byte[] Types(int count, int flags)
        {
            var t = new byte[Math.Max(0, count)];
            if ((flags & 0x1000) == 0)
            {
                for (int i = 0; i < count; i++) t[i] = U8();
                return t;
            }
            int n = 0;
            while (n < count && Ok)
            {
                byte b = U8();
                if ((b & 0x40) == 0) { t[n++] = b; continue; }
                int run = b & 0x3f;
                if (count - run < n) { Ok = false; break; }
                byte ty = (byte)((b & 0x80) != 0 ? 3 : 1);
                for (int k = 0; k < run; k++) t[n++] = ty;
            }
            Align4();
            return t;
        }
    }

    internal static class GpEmfPlusReader
    {
        static bool VersionOk(int v) => ((uint)v & 0xfffff000) == 0xDBC01000u;

        /// <summary>GpObject::Factory + SetData: the object an EmfPlusObject record holds.</summary>
        public static object Read(EmfPlusObjectType type, byte[] data, int o, int n)
        {
            try
            {
                var r = new GpReader(data, o, n);
                object obj = null;
                switch (type)
                {
                    case EmfPlusObjectType.Brush: obj = ReadBrush(r); break;
                    case EmfPlusObjectType.Pen: obj = ReadPen(r); break;
                    case EmfPlusObjectType.Path: obj = ReadPath(r); break;
                    case EmfPlusObjectType.Region: obj = ReadRegion(r); break;
                    case EmfPlusObjectType.Image: obj = ReadImage(r); break;
                    case EmfPlusObjectType.Font: obj = ReadFont(r); break;
                    case EmfPlusObjectType.StringFormat: obj = ReadStringFormat(r); break;
                    case EmfPlusObjectType.ImageAttributes: obj = ReadImageAttributes(r); break;
                    case EmfPlusObjectType.CustomLineCap: obj = ReadCustomLineCap(r); break;
                }
                return r.Ok ? obj : null;
            }
            catch (Exception e) when (e is ArgumentException || e is OverflowException || e is InvalidOperationException || e is ExternalException || e is NotImplementedException || e is OutOfMemoryException || e is IndexOutOfRangeException)
            {
                return null;
            }
        }

        public static Brush ReadBrush(GpReader r)
        {
            int version = r.I32();
            if (!VersionOk(version)) { r.Ok = false; return null; }
            int type = r.I32();
            switch (type)
            {
                case 0:
                    return new SolidBrush(r.Argb());
                case 1:
                    {
                        int style = r.I32();
                        Color fore = r.Argb(), back = r.Argb();
                        if (style < 0 || style > (int)HatchStyle.SolidDiamond) style = 0;
                        return new HatchBrush((HatchStyle)style, fore, back);
                    }
                case 2:
                    {
                        int flags = r.I32();
                        var wrap = (WrapMode)r.I32();
                        Matrix m = (flags & 2) != 0 ? r.Matrix() : null;
                        Image im = ReadImage(r);
                        if (im == null) { r.Ok = false; return null; }
                        var t = im is Bitmap tile ? new TextureBrush(tile, ValidWrap(wrap), adopt: true) : new TextureBrush(im, ValidWrap(wrap));
                        if (m != null) t.Transform = m;
                        return t;
                    }
                case 3:
                    return ReadPathGradient(r);
                case 4:
                    return ReadLinear(r);
            }
            r.Ok = false;
            return null;
        }

        static WrapMode ValidWrap(WrapMode w) => (int)w < 0 || (int)w > 4 ? WrapMode.Tile : w;

        static Brush ReadLinear(GpReader r)
        {
            int flags = r.I32();
            var wrap = ValidWrap((WrapMode)r.I32());
            RectangleF rect = r.Rect();
            Color c1 = r.Argb(), c2 = r.Argb();
            r.I32(); r.I32();
            Matrix m = (flags & 2) != 0 ? r.Matrix() : null;
            if (rect.Width == 0f || rect.Height == 0f) { r.Ok = false; return null; }
            var l = new LinearGradientBrush(rect, c1, c2, LinearGradientMode.Horizontal);
            l.WrapMode = wrap == WrapMode.Clamp ? WrapMode.Tile : wrap;
            if (m != null) l.Transform = m;
            if ((flags & 0x80) != 0) l.GammaCorrection = true;
            if ((flags & 4) != 0)
            {
                int n = r.I32();
                float[] pos = r.Floats(n);
                Color[] cols = r.Colors(n);
                if (n >= 2) l.InterpolationColors = new ColorBlend(n) { Positions = pos, Colors = cols };
            }
            else if ((flags & 8) != 0)
            {
                int n = r.I32();
                float[] pos = r.Floats(n);
                float[] fac = r.Floats(n);
                if (n >= 1) l.Blend = new Blend(n) { Positions = pos, Factors = fac };
                if ((flags & 0x10) != 0) { int v = r.I32(); r.Floats(v); r.Floats(v); }
            }
            return l;
        }

        static Brush ReadPathGradient(GpReader r)
        {
            int flags = r.I32();
            var wrap = ValidWrap((WrapMode)r.I32());
            Color center = r.Argb();
            float cx = r.F(), cy = r.F();
            int sc = r.I32();
            Color[] surround = r.Colors(sc);
            PathGradientBrush pg;
            if ((flags & 1) != 0)
            {
                int size = r.I32();
                int start = r.Position;
                GraphicsPath path = ReadPath(new GpReader(r.Buffer, start, size));
                r.Skip(size);
                if (path == null) { r.Ok = false; return null; }
                pg = new PathGradientBrush(path);
            }
            else
            {
                int n = r.I32();
                var pts = new PointF[Math.Max(0, n)];
                for (int i = 0; i < n; i++) { float x = r.F(), y = r.F(); pts[i] = new PointF(x, y); }
                if (n < 2) { r.Ok = false; return null; }
                pg = new PathGradientBrush(pts);
            }
            pg.WrapMode = wrap;
            pg.CenterColor = center;
            pg.CenterPoint = new PointF(cx, cy);
            if (sc > 0)
            {
                int pc = pg.PointCount;
                if (sc == 1 || sc > pc)
                {
                    var all = new Color[pc];
                    for (int i = 0; i < pc; i++) all[i] = surround[Math.Min(i, sc - 1)];
                    pg.SurroundColors = all;
                }
                else pg.SurroundColors = surround;
            }
            if ((flags & 2) != 0) pg.Transform = r.Matrix();
            if ((flags & 4) != 0)
            {
                int n = r.I32();
                float[] pos = r.Floats(n);
                Color[] cols = r.Colors(n);
                if (n >= 2) pg.InterpolationColors = new ColorBlend(n) { Positions = pos, Colors = cols };
            }
            if ((flags & 8) != 0)
            {
                int n = r.I32();
                float[] pos = r.Floats(n);
                float[] fac = r.Floats(n);
                if (n >= 1) pg.Blend = new Blend(n) { Positions = pos, Factors = fac };
            }
            if ((flags & 0x40) != 0)
            {
                int n = r.I32();
                float fx = r.F(), fy = r.F();
                pg.FocusScales = new PointF(fx, fy);
            }
            if ((flags & 0x80) != 0) pg.GammaCorrection = true;
            return pg;
        }

        public static Pen ReadPen(GpReader r)
        {
            int version = r.I32();
            if (!VersionOk(version)) { r.Ok = false; return null; }
            r.I32();
            int flags = r.I32();
            int unit = r.I32();
            float width = r.F();
            Matrix m = (flags & 1) != 0 ? r.Matrix() : null;
            int startCap = (flags & 2) != 0 ? r.I32() : 0;
            int endCap = (flags & 4) != 0 ? r.I32() : 0;
            int join = (flags & 8) != 0 ? r.I32() : 0;
            float miter = (flags & 0x10) != 0 ? r.F() : 10f;
            int dashStyle = (flags & 0x20) != 0 ? r.I32() : 0;
            int dashCap = (flags & 0x40) != 0 ? r.I32() : 0;
            float dashOffset = (flags & 0x80) != 0 ? r.F() : 0f;
            float[] pattern = null;
            if ((flags & 0x100) != 0) { int n = r.I32(); pattern = r.Floats(n); }
            int align = (flags & 0x200) != 0 ? r.I32() : 0;
            float[] compound = null;
            if ((flags & 0x400) != 0) { int n = r.I32(); compound = r.Floats(n); }
            CustomLineCap cStart = null, cEnd = null;
            if ((flags & 0x800) != 0) { int n = r.I32(); cStart = ReadCustomLineCap(new GpReader(r.Buffer, r.Position, n)); r.Skip(n); }
            if ((flags & 0x1000) != 0) { int n = r.I32(); cEnd = ReadCustomLineCap(new GpReader(r.Buffer, r.Position, n)); r.Skip(n); }
            Brush brush = ReadBrush(r);
            if (brush == null) { r.Ok = false; return null; }
            // A pen in another unit than World is drawn at its width converted to world units.
            float w = width;
            var p = new Pen(brush, w);
            if (m != null) p.Transform = m;
            if ((flags & 2) != 0 && startCap != 0xff) p.StartCap = (LineCap)startCap;
            if ((flags & 4) != 0 && endCap != 0xff) p.EndCap = (LineCap)endCap;
            if (cStart != null) p.CustomStartCap = cStart;
            if (cEnd != null) p.CustomEndCap = cEnd;
            if ((flags & 8) != 0) p.LineJoin = (LineJoin)join;
            p.MiterLimit = miter;
            if ((flags & 0x20) != 0 && dashStyle >= 0 && dashStyle < 5) p.DashStyle = (DashStyle)dashStyle;
            if ((flags & 0x40) != 0 && (dashCap == 0 || dashCap == 2 || dashCap == 3)) p.DashCap = (DashCap)dashCap;
            if (pattern != null && pattern.Length > 0) p.DashPattern = pattern;
            p.DashOffset = dashOffset;
            if ((flags & 0x200) != 0 && align >= 0 && align <= 4) p.Alignment = (PenAlignment)align;
            if (compound != null && compound.Length > 0) p.CompoundArray = compound;
            s_penUnits.AddOrUpdate(p, unit);
            return p;
        }

        // A pen's unit (EMF+ pens may be in another unit than World): the player reads it.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Pen, object> s_penUnits = new System.Runtime.CompilerServices.ConditionalWeakTable<Pen, object>();

        public static int PenUnit(Pen p) => s_penUnits.TryGetValue(p, out object v) ? (int)v : 0;

        public static GraphicsPath ReadPath(GpReader r)
        {
            int version = r.I32();
            if (!VersionOk(version)) { r.Ok = false; return null; }
            int count = r.I32();
            int flags = r.I32();
            if (count < 0 || count > r.Left) { r.Ok = false; return null; }
            PointF[] pts = r.Points(count, flags);
            byte[] types = r.Types(count, flags);
            var mode = (flags & 0x2000) != 0 ? FillMode.Winding : FillMode.Alternate;
            if (!r.Ok) return null;
            if (count == 0) return new GraphicsPath(mode);
            return new GraphicsPath(pts, types, mode);
        }

        public static Region ReadRegion(GpReader r)
        {
            int version = r.I32();
            if (!VersionOk(version)) { r.Ok = false; return null; }
            r.I32();
            return ReadRegionNode(r, 0);
        }

        static Region ReadRegionNode(GpReader r, int depth)
        {
            if (depth > 1000 || !r.Ok) { r.Ok = false; return null; }
            int type = r.I32();
            switch ((uint)type)
            {
                case 0x10000000u:
                    return new Region(r.Rect());
                case 0x10000001u:
                    {
                        int size = r.I32();
                        GraphicsPath p = ReadPath(new GpReader(r.Buffer, r.Position, size));
                        r.Skip(size);
                        if (p == null) { r.Ok = false; return null; }
                        return new Region(p);
                    }
                case 0x10000002u:
                    {
                        var e = new Region();
                        e.MakeEmpty();
                        return e;
                    }
                case 0x10000003u:
                    return new Region();
            }
            if (type < 1 || type > 5) { r.Ok = false; return null; }
            Region left = ReadRegionNode(r, depth + 1);
            Region right = ReadRegionNode(r, depth + 1);
            if (left == null || right == null) { r.Ok = false; return null; }
            switch (type)
            {
                case 1: left.Intersect(right); break;
                case 2: left.Union(right); break;
                case 3: left.Xor(right); break;
                case 4: left.Exclude(right); break;
                case 5: left.Complement(right); break;
            }
            return left;
        }

        public static Image ReadImage(GpReader r)
        {
            int version = r.I32();
            if (!VersionOk(version)) { r.Ok = false; return null; }
            int type = r.I32();
            if (type == 1)
            {
                int w = r.I32(), h = r.I32(), stride = r.I32();
                var pf = (PixelFormat)r.I32();
                int btype = r.I32();
                if (btype == 1)
                {
                    byte[] data = r.Bytes(r.Left);
                    try { return Image.FromBytes(data); }
                    catch (Exception e) when (e is ArgumentException || e is OutOfMemoryException) { r.Ok = false; return null; }
                }
                if (w <= 0 || h <= 0 || stride == 0) { r.Ok = false; return null; }
                var bmp = new Bitmap(w, h, pf);
                if ((pf & PixelFormat.Indexed) != 0)
                {
                    int pflags = r.I32(), pcount = r.I32();
                    Color[] entries = r.Colors(pcount);
                    ColorPalette pal = bmp.Palette;
                    for (int i = 0; i < Math.Min(entries.Length, pal.Entries.Length); i++) pal.Entries[i] = entries[i];
                    bmp.Palette = pal;
                }
                int rowBytes = Math.Abs(stride);
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, pf);
                try
                {
                    for (int y = 0; y < h; y++)
                    {
                        byte[] row = r.Bytes(rowBytes);
                        if (!r.Ok) break;
                        int dy = stride < 0 ? h - 1 - y : y;
                        System.Runtime.InteropServices.Marshal.Copy(row, 0, bd.Scan0 + dy * bd.Stride, Math.Min(rowBytes, Math.Abs(bd.Stride)));
                    }
                }
                finally { bmp.UnlockBits(bd); }
                return bmp;
            }
            if (type == 2)
            {
                int mtype = r.I32();
                int size = r.I32();
                byte[] data = r.Bytes(Math.Min(size + (mtype <= 2 ? 22 : 0), r.Left));
                if (mtype == 1 || mtype == 2)
                {
                    // A WMF goes with its placeable header in front.
                    var st = GpMetafileFormat.Read(data, 0, out GpMetafileData d, out bool _);
                    if (st != GpMetafileFormat.ReadStatus.Ok) { r.Ok = false; return null; }
                    return new Metafile(d);
                }
                if (!GpMetafileFormat.HeaderFromEmf(data, out GpMetafileHeader h)) { r.Ok = false; return null; }
                return new Metafile(new GpMetafileData { Header = h, Emf = data });
            }
            r.Ok = false;
            return null;
        }

        public static Font ReadFont(GpReader r)
        {
            int version = r.I32();
            if (!VersionOk(version)) { r.Ok = false; return null; }
            float size = r.F();
            var unit = (GraphicsUnit)r.I32();
            var style = (FontStyle)(r.I32() & 0xf);
            r.I32();
            int len = r.I32();
            if (len < 0 || len > 0x1000) { r.Ok = false; return null; }
            var cs = new char[len];
            for (int i = 0; i < len; i++) cs[i] = (char)r.I16();
            string name = new string(cs);
            if (unit == GraphicsUnit.Display || (int)unit < 0 || (int)unit > 6) unit = GraphicsUnit.Point;
            if (size <= 0f) size = 1f;
            return new Font(name, size, style, unit);
        }

        public static StringFormat ReadStringFormat(GpReader r)
        {
            int version = r.I32();
            if (!VersionOk(version)) { r.Ok = false; return null; }
            var flags = (StringFormatFlags)r.I32();
            int language = r.I16(); r.I16();
            int align = r.I32(), lineAlign = r.I32(), digitSub = r.I32();
            int digitLang = r.I16(); r.I16();
            float firstTab = r.F();
            int hotkey = r.I32();
            float leading = r.F(), trailing = r.F(), tracking = r.F();
            int trimming = r.I32();
            int tabCount = r.I32(), rangeCount = r.I32();
            float[] tabs = r.Floats(tabCount);
            var ranges = new CharacterRange[Math.Max(0, rangeCount)];
            for (int i = 0; i < rangeCount; i++) { int f = r.I32(), l = r.I32(); ranges[i] = new CharacterRange(f, l); }
            var sf = new StringFormat(flags, language);
            if (align >= 0 && align <= 2) sf.Alignment = (StringAlignment)align;
            if (lineAlign >= 0 && lineAlign <= 2) sf.LineAlignment = (StringAlignment)lineAlign;
            if (digitSub >= 0 && digitSub <= 3) sf.SetDigitSubstitution(digitLang, (StringDigitSubstitute)digitSub);
            if (hotkey >= 0 && hotkey <= 2) sf.HotkeyPrefix = (System.Drawing.Text.HotkeyPrefix)hotkey;
            if (trimming >= 0 && trimming <= 5) sf.Trimming = (StringTrimming)trimming;
            if (tabCount > 0) sf.SetTabStops(firstTab, tabs);
            if (rangeCount > 0) sf.SetMeasurableCharacterRanges(ranges);
            sf.IsTypographic = leading == 0f && trailing == 0f;
            return sf;
        }

        public static ImageAttributes ReadImageAttributes(GpReader r)
        {
            int version = r.I32();
            if (!VersionOk(version)) { r.Ok = false; return null; }
            r.I32();
            var wrap = (WrapMode)r.I32();
            Color clamp = r.Argb();
            int objectClamp = r.I32();
            var ia = new ImageAttributes();
            if ((int)wrap >= 0 && (int)wrap <= 4) ia.SetWrapMode(wrap, clamp, objectClamp != 0);
            return ia;
        }

        public static CustomLineCap ReadCustomLineCap(GpReader r)
        {
            int version = r.I32();
            if (!VersionOk(version)) { r.Ok = false; return null; }
            int type = r.I32();
            if (type == 1)
            {
                float w = r.F(), h = r.F(), inset = r.F();
                int filled = r.I32();
                var sc = (LineCap)r.I32(); var ec = (LineCap)r.I32();
                var join = (LineJoin)r.I32();
                float miter = r.F(), widthScale = r.F();
                r.Floats(4);
                var a = new AdjustableArrowCap(w, h, filled != 0) { MiddleInset = inset };
                a.SetStrokeCaps(sc, ec);
                a.StrokeJoin = join;
                a.WidthScale = widthScale;
                if (a.gp != null) a.gp.MiterLimit = miter;
                return a;
            }
            int flags = r.I32();
            var baseCap = (LineCap)r.I32();
            float baseInset = r.F();
            var s = (LineCap)r.I32(); var e = (LineCap)r.I32();
            var j = (LineJoin)r.I32();
            float ml = r.F(), ws = r.F();
            r.Floats(4);
            GraphicsPath fill = null, line = null;
            if ((flags & 1) != 0) { int n = r.I32(); fill = ReadPath(new GpReader(r.Buffer, r.Position, n)); r.Skip(n); }
            if ((flags & 2) != 0) { int n = r.I32(); line = ReadPath(new GpReader(r.Buffer, r.Position, n)); r.Skip(n); }
            if (fill == null && line == null) { r.Ok = false; return null; }
            var c = new CustomLineCap(fill, line, (uint)baseCap < 4 ? baseCap : LineCap.Flat, baseInset);
            c.SetStrokeCaps(s, e);
            c.StrokeJoin = j;
            c.WidthScale = ws;
            if (c.gp != null) c.gp.MiterLimit = ml;
            return c;
        }
    }
}
