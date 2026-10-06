// Metafile oracle: real GDI+ (.NET Framework System.Drawing).
//   mfo rec <scenario|all> <EmfPlusOnly|EmfPlusDual|EmfOnly> <outdir>
//   mfo hdr <file>                 MetafileHeader + Image members
//   mfo enum <file>                EnumerateMetafile record list
//   mfo play <file> <w> <h> <out.png> [x y w h]
//   mfo caps                       screen DC metrics
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using MetafileOracle;

class GRec : IRec
{
    public Graphics g;
    public GRec(Graphics g) { this.g = g; }
    public void Clear(Color c) { g.Clear(c); }
    public void FillRects(Brush b, RectangleF[] r) { g.FillRectangles(b, r); }
    public void DrawRects(Pen p, RectangleF[] r) { g.DrawRectangles(p, r); }
    public void FillPolygon(Brush b, PointF[] pts, FillMode mode) { g.FillPolygon(b, pts, mode); }
    public void DrawLines(Pen p, PointF[] pts, bool closed) { if (closed) g.DrawPolygon(p, pts); else g.DrawLines(p, pts); }
    public void FillEllipse(Brush b, RectangleF r) { g.FillEllipse(b, r); }
    public void DrawEllipse(Pen p, RectangleF r) { g.DrawEllipse(p, r); }
    public void FillPie(Brush b, RectangleF r, float s, float w) { g.FillPie(b, r.X, r.Y, r.Width, r.Height, s, w); }
    public void DrawPie(Pen p, RectangleF r, float s, float w) { g.DrawPie(p, r, s, w); }
    public void DrawArc(Pen p, RectangleF r, float s, float w) { g.DrawArc(p, r, s, w); }
    public void FillPath(Brush b, GraphicsPath path) { g.FillPath(b, path); }
    public void DrawPath(Pen p, GraphicsPath path) { g.DrawPath(p, path); }
    public void FillClosedCurve(Brush b, PointF[] pts, float t, FillMode m) { g.FillClosedCurve(b, pts, m, t); }
    public void DrawClosedCurve(Pen p, PointF[] pts, float t) { g.DrawClosedCurve(p, pts, t, FillMode.Alternate); }
    public void DrawCurve(Pen p, PointF[] pts, int o, int n, float t) { g.DrawCurve(p, pts, o, n, t); }
    public void DrawBeziers(Pen p, PointF[] pts) { g.DrawBeziers(p, pts); }
    public void FillRegion(Brush b, Region r) { g.FillRegion(b, r); }
    public void DrawImage(Image img, RectangleF d, RectangleF s, GraphicsUnit u, ImageAttributes ia)
    {
        if (ia == null) g.DrawImage(img, d, s, u);
        else g.DrawImage(img, Rectangle.Round(d), s.X, s.Y, s.Width, s.Height, u, ia);
    }
    public void DrawImagePoints(Image img, PointF[] d, RectangleF s, GraphicsUnit u, ImageAttributes ia) { g.DrawImage(img, d, s, u, ia); }
    public void DrawString(string s, Font f, RectangleF r, StringFormat fmt, Brush b) { g.DrawString(s, f, b, r, fmt); }
    [DllImport("gdiplus.dll")] static extern int GdipDrawDriverString(IntPtr g, ushort[] text, int len, IntPtr font, IntPtr brush, PointF[] pos, int flags, IntPtr m);
    static unsafe IntPtr Ptr(object v) { if (v is IntPtr) return (IntPtr)v; if (v is Pointer) return (IntPtr)Pointer.Unbox(v); return IntPtr.Zero; }
    static IntPtr H(object o, string name)
    {
        var t = o.GetType();
        while (t != null)
        {
            var p = t.GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (p != null) return Ptr(p.GetValue(o, null));
            if (name == "nativeMatrix") { var q = t.GetProperty("NativeMatrix", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public); if (q != null) return Ptr(q.GetValue(o, null)); }
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f != null) return Ptr(f.GetValue(o));
            t = t.BaseType;
        }
        throw new Exception("no " + name + " on " + o.GetType());
    }
    public void DrawDriverString(ushort[] t, Font f, Brush b, PointF[] pos, int flags, Matrix m)
    {
        int st = GdipDrawDriverString(H(g, "NativeGraphics"), t, t.Length, H(f, "NativeFont"), H(b, "NativeBrush"), pos, flags, m == null ? IntPtr.Zero : H(m, "nativeMatrix"));
        if (st != 0) throw new Exception("DrawDriverString " + st);
    }
    public void SetWorldTransform(Matrix m) { g.Transform = m; }
    public void ResetWorldTransform() { g.ResetTransform(); }
    public void MultiplyWorldTransform(Matrix m, MatrixOrder o) { g.MultiplyTransform(m, o); }
    public void TranslateWorldTransform(float dx, float dy, MatrixOrder o) { g.TranslateTransform(dx, dy, o); }
    public void ScaleWorldTransform(float sx, float sy, MatrixOrder o) { g.ScaleTransform(sx, sy, o); }
    public void RotateWorldTransform(float a, MatrixOrder o) { g.RotateTransform(a, o); }
    public void SetPageTransform(GraphicsUnit u, float s) { g.PageUnit = u; g.PageScale = s; }
    public void SetClipRect(RectangleF r, CombineMode m) { g.SetClip(r, m); }
    public void SetClipPath(GraphicsPath p, CombineMode m) { g.SetClip(p, m); }
    public void SetClipRegion(Region r, CombineMode m) { g.SetClip(r, m); }
    public void ResetClip() { g.ResetClip(); }
    public void OffsetClip(float dx, float dy) { g.TranslateClip(dx, dy); }
    public object Save() { return g.Save(); }
    public void Restore(object s) { g.Restore((GraphicsState)s); }
    public object BeginContainer(RectangleF d, RectangleF s, GraphicsUnit u) { return g.BeginContainer(d, s, u); }
    public object BeginContainerNoParams() { return g.BeginContainer(); }
    public void EndContainer(object s) { g.EndContainer((GraphicsContainer)s); }
    public void SetAntiAliasMode(SmoothingMode m) { g.SmoothingMode = m; }
    public void SetTextRenderingHint(TextRenderingHint h) { g.TextRenderingHint = h; }
    public void SetTextContrast(int c) { g.TextContrast = c; }
    public void SetInterpolationMode(InterpolationMode m) { g.InterpolationMode = m; }
    public void SetPixelOffsetMode(PixelOffsetMode m) { g.PixelOffsetMode = m; }
    public void SetCompositingMode(CompositingMode m) { g.CompositingMode = m; }
    public void SetCompositingQuality(CompositingQuality q) { g.CompositingQuality = q; }
    public void SetRenderingOrigin(int x, int y) { g.RenderingOrigin = new Point(x, y); }
    public void Comment(byte[] d) { g.AddMetafileComment(d); }
    public void Flush(FlushIntention f) { g.Flush(f); }
}

static class Program
{
    static string Hex(byte[] b, int off, int n)
    {
        var s = new StringBuilder();
        for (int i = 0; i < n; i++) s.Append(b[off + i].ToString("x2"));
        return s.ToString();
    }

    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(int c);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern bool Rectangle(IntPtr hdc, int l, int t, int r, int b);

    /// <summary>The GetHdc scenario MetafileTests replays: GDI+ drawing, GDI drawing on the
    /// recording's HDC, GDI+ drawing again.</summary>
    public static void GetHdcScenario(Graphics g)
    {
        g.FillRectangle(Brushes.Red, 10, 10, 30, 20);
        IntPtr hdc = g.GetHdc();
        IntPtr b = CreateSolidBrush(0xff0000);
        IntPtr old = SelectObject(hdc, b);
        Rectangle(hdc, 20, 20, 60, 50);
        SelectObject(hdc, old);
        DeleteObject(b);
        g.ReleaseHdc(hdc);
        g.FillRectangle(Brushes.Green, 40, 5, 20, 20);
    }

    [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr hdc, int i);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);

    static int Main(string[] a)
    {
        try { return Run(a); }
        catch (Exception e) { Console.WriteLine("EXC " + e.GetType().Name + ": " + e.Message); return 1; }
    }

    static void Hdr(MetafileHeader h, StringBuilder o)
    {
        o.AppendLine("Type=" + h.Type);
        o.AppendLine("Version=0x" + h.Version.ToString("x"));
        o.AppendLine("EmfPlusFlags=" + GetFlags(h));
        o.AppendLine("DpiX=" + h.DpiX.ToString("R") + " DpiY=" + h.DpiY.ToString("R"));
        o.AppendLine("LogicalDpi=" + h.LogicalDpiX + "," + h.LogicalDpiY);
        o.AppendLine("Bounds=" + h.Bounds.X + "," + h.Bounds.Y + "," + h.Bounds.Width + "," + h.Bounds.Height);
        o.AppendLine("MetafileSize=" + h.MetafileSize);
        o.AppendLine("EmfPlusHeaderSize=" + h.EmfPlusHeaderSize);
        o.AppendLine("IsDisplay=" + h.IsDisplay());
        try
        {
            var w = h.WmfHeader;
            o.AppendLine("Wmf=" + w.Type + "," + w.HeaderSize + "," + w.Version + "," + w.Size + "," + w.NoObjects + "," + w.MaxRecord + "," + w.NoParameters);
        }
        catch (Exception e) { o.AppendLine("Wmf=EXC " + e.GetType().Name); }
        try
        {
            FieldInfo p = null; foreach (var f0 in typeof(MetafileHeader).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)) if (f0.FieldType.Name == "MetafileHeaderEmf") p = f0;
            if (p != null)
            {
                var e = p.GetValue(h);
                if (e != null)
                {
                    var sb = new StringBuilder();
                    foreach (var f in e.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        object v = f.GetValue(e);
                        string vs = v == null ? "null" : v.ToString();
                        if (v != null && v.GetType().IsValueType && !v.GetType().IsPrimitive && !(v is Enum))
                        {
                            var parts = new List<string>();
                            foreach (var ff in v.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                                parts.Add(ff.Name + ":" + ff.GetValue(v));
                            vs = "{" + string.Join(" ", parts.ToArray()) + "}";
                        }
                        sb.Append(f.Name + "=" + vs + " ");
                    }
                    o.AppendLine("Emf=" + sb.ToString().Trim());
                }
                else o.AppendLine("Emf=null");
            }
        }
        catch (Exception e) { o.AppendLine("Emf=EXC " + e.GetType().Name); }
    }

    static int GetFlags(MetafileHeader h)
    {
        foreach (var f in typeof(MetafileHeader).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)) if (f.Name.ToLower().Contains("flags")) return Convert.ToInt32(f.GetValue(h));
        return -1;
    }

    static int Run(string[] a)
    {
        string cmd = a[0];
        if (cmd == "caps")
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            foreach (int i in new[] { 4, 6, 8, 10, 88, 90, 2, 12, 110, 111, 112, 113 })
                Console.WriteLine(i + "=" + GetDeviceCaps(dc, i));
            return 0;
        }
        if (cmd == "rec")
        {
            var all = MetafileScenarios.All();
            var type = (EmfType)Enum.Parse(typeof(EmfType), a[2]);
            Directory.CreateDirectory(a[3]);
            using (var screen = Graphics.FromHwnd(IntPtr.Zero))
            {
                foreach (var kv in all)
                {
                    if (a[1] != "all" && a[1] != kv.Key) continue;
                    IntPtr hdc = screen.GetHdc();
                    var ms = new MemoryStream();
                    string err = null;
                    try
                    {
                        Metafile mf;
                        if (a.Length > 4 && a[4] == "frame")
                            mf = new Metafile(ms, hdc, new RectangleF(0, 0, 100, 80), MetafileFrameUnit.Pixel, type);
                        else
                            mf = new Metafile(ms, hdc, type);
                        using (mf)
                        {
                            using (var g = Graphics.FromImage(mf))
                                kv.Value(new GRec(g));
                        }
                    }
                    catch (Exception e) { err = e.GetType().Name + ": " + e.Message; }
                    screen.ReleaseHdc(hdc);
                    if (err != null) { Console.WriteLine(kv.Key + " EXC " + err); continue; }
                    File.WriteAllBytes(Path.Combine(a[3], kv.Key + ".emf"), ms.ToArray());
                    Console.WriteLine(kv.Key + " " + ms.Length);
                }
            }
            return 0;
        }
        if (cmd == "hdr")
        {
            var o = new StringBuilder();
            try { Hdr(Metafile.FromFile(a[1]) is Metafile ? ((Metafile)Image.FromFile(a[1])).GetMetafileHeader() : null, o); }
            catch (Exception e) { o.AppendLine("FromFile EXC " + e.GetType().Name + " " + e.Message); }
            try { var h = Metafile.GetMetafileHeader(a[1]); o.AppendLine("-- static"); Hdr(h, o); }
            catch (Exception e) { o.AppendLine("static EXC " + e.GetType().Name + " " + e.Message); }
            try
            {
                using (var mf = new Metafile(a[1]))
                {
                    o.AppendLine("-- image");
                    o.AppendLine("Size=" + mf.Width + "x" + mf.Height);
                    o.AppendLine("Phys=" + mf.PhysicalDimension.Width.ToString("R") + "x" + mf.PhysicalDimension.Height.ToString("R"));
                    o.AppendLine("Res=" + mf.HorizontalResolution.ToString("R") + "," + mf.VerticalResolution.ToString("R"));
                    o.AppendLine("PixelFormat=" + mf.PixelFormat);
                    o.AppendLine("RawFormat=" + mf.RawFormat.Guid);
                    o.AppendLine("Flags=0x" + mf.Flags.ToString("x"));
                    GraphicsUnit u = GraphicsUnit.Display;
                    var b = mf.GetBounds(ref u);
                    o.AppendLine("GetBounds=" + b.X.ToString("R") + "," + b.Y.ToString("R") + "," + b.Width.ToString("R") + "," + b.Height.ToString("R") + " " + u);
                    o.AppendLine("FrameDims=" + string.Join(",", Array.ConvertAll(mf.FrameDimensionsList, x => x.ToString())));
                    try { o.AppendLine("FrameCount=" + mf.GetFrameCount(FrameDimension.Page)); } catch (Exception e) { o.AppendLine("FrameCount EXC " + e.GetType().Name); }
                    try { o.AppendLine("Palette=" + mf.Palette.Entries.Length + " flags=" + mf.Palette.Flags); } catch (Exception e) { o.AppendLine("Palette EXC " + e.GetType().Name); }
                    try { o.AppendLine("Props=" + mf.PropertyIdList.Length); } catch (Exception e) { o.AppendLine("Props EXC " + e.GetType().Name); }
                }
            }
            catch (Exception e) { o.AppendLine("image EXC " + e.GetType().Name + " " + e.Message); }
            Console.Write(o.ToString());
            return 0;
        }
        if (cmd == "enum")
        {
            using (var mf = new Metafile(a[1]))
            using (var bmp = new Bitmap(10, 10))
            using (var g = Graphics.FromImage(bmp))
            {
                g.EnumerateMetafile(mf, new PointF(0, 0), (Graphics.EnumerateMetafileProc)((t, flags, size, data, cb) =>
                {
                    byte[] buf = new byte[size];
                    if (size > 0 && data != IntPtr.Zero) Marshal.Copy(data, buf, 0, size);
                    Console.WriteLine(t + " " + ((int)t).ToString("x") + " f=" + flags.ToString("x") + " n=" + size + " " + Hex(buf, 0, Math.Min(size, 96)));
                    return true;
                }));
            }
            return 0;
        }
        if (cmd == "gethdc")
        {
            // gethdc <EmfOnly|EmfPlusDual> <out.emf> : GDI drawing through a recording's GetHdc
            var type = (EmfType)Enum.Parse(typeof(EmfType), a[1]);
            using (var screen = Graphics.FromHwnd(IntPtr.Zero))
            {
                IntPtr refDc = screen.GetHdc();
                var ms = new MemoryStream();
                using (var mf = new Metafile(ms, refDc, type))
                {
                    using (var g = Graphics.FromImage(mf)) GetHdcScenario(g);
                }
                screen.ReleaseHdc(refDc);
                File.WriteAllBytes(a[2], ms.ToArray());
            }
            return 0;
        }
        if (cmd == "play")
        {
            int w = int.Parse(a[2]), h = int.Parse(a[3]);
            using (var mf = new Metafile(a[1]))
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    if (a.Length > 8)
                        g.DrawImage(mf, new RectangleF(float.Parse(a[5]), float.Parse(a[6]), float.Parse(a[7]), float.Parse(a[8])));
                    else
                        g.DrawImage(mf, 0, 0);
                }
                bmp.Save(a[4], ImageFormat.Png);
            }
            return 0;
        }
        if (cmd == "playall")
        {
            // playall <dir> <outdir> : each .emf/.wmf drawn into (5,5,110,80) of a 120x90 white bitmap
            Directory.CreateDirectory(a[2]);
            foreach (var f in Directory.GetFiles(a[1]))
            {
                try
                {
                    using (var mf = new Metafile(f))
                    using (var bmp = new Bitmap(120, 90, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.White); g.DrawImage(mf, new RectangleF(5, 5, 110, 80)); }
                        bmp.Save(Path.Combine(a[2], Path.GetFileNameWithoutExtension(f) + ".png"), ImageFormat.Png);
                    }
                }
                catch (Exception e) { Console.WriteLine(f + " EXC " + e.Message); }
            }
            return 0;
        }
        Console.WriteLine("?");
        return 2;
    }
}
