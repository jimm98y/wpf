// Metafile API probe against real GDI+.
//   mfp gen <dir>      write sample EMF (GDI-only), placeable WMF, bare WMF
//   mfp api <file>     what the Image/Metafile API answers for a file
//   mfp errs           error behaviour of constructors
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

static class P
{
    [DllImport("gdi32.dll")] static extern IntPtr CreateEnhMetaFileW(IntPtr hdc, string file, ref RECT r, string desc);
    [DllImport("gdi32.dll", EntryPoint = "CreateEnhMetaFileW")] static extern IntPtr CreateEnhMetaFileNoRect(IntPtr hdc, string file, IntPtr r, string desc);
    [DllImport("gdi32.dll")] static extern IntPtr CloseEnhMetaFile(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern uint GetEnhMetaFileBits(IntPtr h, uint n, byte[] b);
    [DllImport("gdi32.dll")] static extern bool DeleteEnhMetaFile(IntPtr h);
    [DllImport("gdi32.dll")] static extern IntPtr CreateMetaFileW(string f);
    [DllImport("gdi32.dll")] static extern IntPtr CloseMetaFile(IntPtr h);
    [DllImport("gdi32.dll")] static extern uint GetMetaFileBitsEx(IntPtr h, uint n, byte[] b);
    [DllImport("gdi32.dll")] static extern bool Rectangle(IntPtr h, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] static extern bool Ellipse(IntPtr h, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] static extern bool MoveToEx(IntPtr h, int x, int y, IntPtr p);
    [DllImport("gdi32.dll")] static extern bool LineTo(IntPtr h, int x, int y);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(int c);
    [DllImport("gdi32.dll")] static extern IntPtr CreatePen(int s, int w, int c);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr h, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern int SetMapMode(IntPtr h, int m);
    [DllImport("gdi32.dll")] static extern bool SetWindowExtEx(IntPtr h, int x, int y, IntPtr p);
    [DllImport("gdi32.dll")] static extern bool SetWindowOrgEx(IntPtr h, int x, int y, IntPtr p);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool TextOutW(IntPtr h, int x, int y, string s, int n);
    [DllImport("gdi32.dll")] static extern int SetTextColor(IntPtr h, int c);
    [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr h, int m);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateFontW(int h, int w, int e, int o, int wt, int i, int u, int s, int cs, int op, int cp, int q, int pf, string face);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("gdi32.dll")] static extern bool Polygon(IntPtr h, POINT[] p, int n);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; public POINT(int a, int b) { x = a; y = b; } }

    static void DrawGdi(IntPtr dc, bool wmf)
    {
        IntPtr br = CreateSolidBrush(0x0000ff);
        IntPtr pen = CreatePen(0, 3, 0xff0000);
        IntPtr ob = SelectObject(dc, br), op = SelectObject(dc, pen);
        Rectangle(dc, 10, 10, 110, 60);
        Ellipse(dc, 20, 70, 120, 140);
        MoveToEx(dc, 0, 0, IntPtr.Zero); LineTo(dc, 150, 150);
        Polygon(dc, new[] { new POINT(130, 10), new POINT(190, 40), new POINT(140, 90) }, 3);
        IntPtr f = CreateFontW(-24, 0, 0, 0, 700, 0, 0, 0, 1, 0, 0, 0, 0, "Arial");
        IntPtr of = SelectObject(dc, f);
        SetTextColor(dc, 0x008000); SetBkMode(dc, 1);
        TextOutW(dc, 10, 150, "Meta", 4);
        SelectObject(dc, of); SelectObject(dc, ob); SelectObject(dc, op);
        DeleteObject(f); DeleteObject(br); DeleteObject(pen);
    }

    static byte[] Placeable(byte[] wmf, short l, short t, short r, short b, short inch)
    {
        var h = new byte[22];
        BitConverter.GetBytes(unchecked((int)0x9AC6CDD7)).CopyTo(h, 0);
        BitConverter.GetBytes(l).CopyTo(h, 6); BitConverter.GetBytes(t).CopyTo(h, 8);
        BitConverter.GetBytes(r).CopyTo(h, 10); BitConverter.GetBytes(b).CopyTo(h, 12);
        BitConverter.GetBytes(inch).CopyTo(h, 14);
        ushort x = 0; for (int i = 0; i < 10; i++) x ^= BitConverter.ToUInt16(h, i * 2);
        BitConverter.GetBytes(x).CopyTo(h, 20);
        var o = new byte[22 + wmf.Length]; h.CopyTo(o, 0); wmf.CopyTo(o, 22); return o;
    }

    static int Main(string[] a)
    {
        if (a[0] == "gen")
        {
            Directory.CreateDirectory(a[1]);
            IntPtr screen = GetDC(IntPtr.Zero);
            var r = new RECT { l = 0, t = 0, r = 5000, b = 4000 };
            IntPtr dc = CreateEnhMetaFileW(screen, null, ref r, "App\0Picture\0\0");
            DrawGdi(dc, false);
            IntPtr h = CloseEnhMetaFile(dc);
            uint n = GetEnhMetaFileBits(h, 0, null); var bytes = new byte[n]; GetEnhMetaFileBits(h, n, bytes);
            File.WriteAllBytes(Path.Combine(a[1], "gdi.emf"), bytes); DeleteEnhMetaFile(h);
            dc = CreateEnhMetaFileNoRect(screen, null, IntPtr.Zero, null);
            DrawGdi(dc, false);
            h = CloseEnhMetaFile(dc);
            n = GetEnhMetaFileBits(h, 0, null); bytes = new byte[n]; GetEnhMetaFileBits(h, n, bytes);
            File.WriteAllBytes(Path.Combine(a[1], "gdi_noframe.emf"), bytes); DeleteEnhMetaFile(h);
            IntPtr mdc = CreateMetaFileW(null);
            SetMapMode(mdc, 8); SetWindowOrgEx(mdc, 0, 0, IntPtr.Zero); SetWindowExtEx(mdc, 200, 200, IntPtr.Zero);
            DrawGdi(mdc, true);
            IntPtr hm = CloseMetaFile(mdc);
            n = GetMetaFileBitsEx(hm, 0, null); bytes = new byte[n]; GetMetaFileBitsEx(hm, n, bytes);
            File.WriteAllBytes(Path.Combine(a[1], "bare.wmf"), bytes);
            File.WriteAllBytes(Path.Combine(a[1], "placeable.wmf"), Placeable(bytes, 0, 0, 200, 200, 96));
            File.WriteAllBytes(Path.Combine(a[1], "placeable1440.wmf"), Placeable(bytes, -10, 5, 2870, 2885, 1440));
            File.WriteAllBytes(Path.Combine(a[1], "placeable0.wmf"), Placeable(bytes, 0, 0, 200, 200, 0));
            mdc = CreateMetaFileW(null);
            DrawGdi(mdc, true);
            hm = CloseMetaFile(mdc);
            n = GetMetaFileBitsEx(hm, 0, null); bytes = new byte[n]; GetMetaFileBitsEx(hm, n, bytes);
            File.WriteAllBytes(Path.Combine(a[1], "bare_nomap.wmf"), bytes);
            return 0;
        }
        if (a[0] == "api")
        {
            Try("FromFile", () => { using (var im = Image.FromFile(a[1])) return im.GetType().Name + " " + im.Width + "x" + im.Height; });
            Try("FromStream", () => { using (var im = Image.FromStream(new MemoryStream(File.ReadAllBytes(a[1])))) return im.GetType().Name + " raw=" + im.RawFormat.Guid; });
            using (var mf = new Metafile(new MemoryStream(File.ReadAllBytes(a[1]))))
            {
                Try("Type", () => mf.GetMetafileHeader().Type.ToString());
                Try("Palette", () => mf.Palette.Entries.Length.ToString());
                Try("PropertyIdList", () => mf.PropertyIdList.Length.ToString());
                Try("PropertyItems", () => mf.PropertyItems.Length.ToString());
                Try("GetPropertyItem", () => mf.GetPropertyItem(0x10e).Id.ToString());
                Try("SelectActiveFrame0", () => mf.SelectActiveFrame(FrameDimension.Page, 0).ToString());
                Try("SelectActiveFrame1", () => mf.SelectActiveFrame(FrameDimension.Page, 1).ToString());
                Try("SelectActiveFrameTime", () => mf.SelectActiveFrame(FrameDimension.Time, 0).ToString());
                Try("FrameCountTime", () => mf.GetFrameCount(FrameDimension.Time).ToString());
                Try("EncParams", () => mf.GetEncoderParameterList(ImageFormat.Png.Guid).Param.Length.ToString());
                Try("SaveEmf", () => { var ms = new MemoryStream(); mf.Save(ms, ImageFormat.Emf); return ms.Length + " " + BitConverter.ToString(ms.ToArray(), 0, 8); });
                Try("SaveWmf", () => { var ms = new MemoryStream(); mf.Save(ms, ImageFormat.Wmf); return ms.Length + " " + BitConverter.ToString(ms.ToArray(), 0, 8); });
                Try("SavePng", () => { var ms = new MemoryStream(); mf.Save(ms, ImageFormat.Png); var b = (Bitmap)Image.FromStream(ms); return ms.Length + " " + b.Width + "x" + b.Height + " " + b.PixelFormat + " res=" + b.HorizontalResolution; });
                Try("SaveRaw", () => { var ms = new MemoryStream(); mf.Save(ms, mf.RawFormat); var b = Image.FromStream(ms); return ms.Length + " " + b.GetType().Name + " " + b.RawFormat.Guid; });
                Try("Thumb", () => { var t = mf.GetThumbnailImage(20, 10, null, IntPtr.Zero); return t.GetType().Name + " " + t.Width + "x" + t.Height + " " + t.PixelFormat; });
                Try("Clone", () => { var c = (Metafile)mf.Clone(); var h = c.GetMetafileHeader(); return c.Width + "x" + c.Height + " " + h.Type + " " + h.Bounds + " dpi=" + h.DpiX + " size=" + h.MetafileSize; });
                Try("CloneEnum", () => { var c = (Metafile)mf.Clone(); int n = 0; using (var b = new Bitmap(4, 4)) using (var g = Graphics.FromImage(b)) g.EnumerateMetafile(c, new PointF(0, 0), (t, f, s, d, cb) => { n++; return true; }); return n.ToString(); });
                Try("Henh", () => { IntPtr h = mf.GetHenhmetafile(); return (h != IntPtr.Zero).ToString(); });
                Try("AfterHenh.Width", () => mf.Width.ToString());
                Try("AfterHenh.Header", () => mf.GetMetafileHeader().Type.ToString());
                Try("AfterHenh.Henh2", () => (mf.GetHenhmetafile() != IntPtr.Zero).ToString());
                Try("RotateFlip", () => { mf.RotateFlip(RotateFlipType.Rotate90FlipNone); return mf.Width + "x" + mf.Height; });
            }
            return 0;
        }
        if (a[0] == "errs")
        {
            Try("null stream", () => new Metafile((Stream)null).ToString());
            Try("empty stream", () => new Metafile(new MemoryStream()).ToString());
            Try("garbage", () => new Metafile(new MemoryStream(new byte[100])).ToString());
            Try("png", () => { var ms = new MemoryStream(); new Bitmap(2, 2).Save(ms, ImageFormat.Png); ms.Position = 0; return new Metafile(ms).ToString(); });
            Try("null file", () => new Metafile((string)null).ToString());
            Try("missing file", () => new Metafile(@"c:\nope\x.emf").ToString());
            Try("GetHeader null stream", () => Metafile.GetMetafileHeader((Stream)null).ToString());
            Try("GetHeader garbage", () => Metafile.GetMetafileHeader(new MemoryStream(new byte[100])).Type.ToString());
            Try("GetHeader missing", () => Metafile.GetMetafileHeader(@"c:\nope\x.emf").Type.ToString());
            Try("FromStream garbage", () => Image.FromStream(new MemoryStream(new byte[100])).ToString());
            Try("hemf zero", () => new Metafile(IntPtr.Zero, false).ToString());
            Try("hdc zero", () => new Metafile(IntPtr.Zero, EmfType.EmfPlusOnly).ToString());
            Try("rec null stream", () => { var g = Graphics.FromHwnd(IntPtr.Zero); var h = g.GetHdc(); try { return new Metafile((Stream)null, h).ToString(); } finally { g.ReleaseHdc(h); } });
            Try("rec bad type", () => { var g = Graphics.FromHwnd(IntPtr.Zero); var h = g.GetHdc(); try { return new Metafile(new MemoryStream(), h, (EmfType)7).ToString(); } finally { g.ReleaseHdc(h); } });
            Try("rec bad unit", () => { var g = Graphics.FromHwnd(IntPtr.Zero); var h = g.GetHdc(); try { return new Metafile(new MemoryStream(), h, new RectangleF(0, 0, 10, 10), (MetafileFrameUnit)9).ToString(); } finally { g.ReleaseHdc(h); } });
            Try("rec neg frame", () => { var g = Graphics.FromHwnd(IntPtr.Zero); var h = g.GetHdc(); try { return new Metafile(new MemoryStream(), h, new RectangleF(0, 0, -10, 10)).ToString(); } finally { g.ReleaseHdc(h); } });
            Try("rec props", () =>
            {
                var g = Graphics.FromHwnd(IntPtr.Zero); var h = g.GetHdc();
                try
                {
                    var mf = new Metafile(new MemoryStream(), h, EmfType.EmfPlusOnly);
                    var sb = new StringBuilder();
                    sb.Append("W=" + Safe(() => mf.Width.ToString()) + " H=" + Safe(() => mf.Height.ToString()) + " hdr=" + Safe(() => mf.GetMetafileHeader().Type.ToString()));
                    var gg = Graphics.FromImage(mf);
                    sb.Append(" during: W=" + Safe(() => mf.Width.ToString()) + " hdr=" + Safe(() => mf.GetMetafileHeader().Type.ToString()) + " dpi=" + gg.DpiX + "," + gg.DpiY + " unit=" + gg.PageUnit);
                    sb.Append(" second=" + Safe(() => Graphics.FromImage(mf).ToString()));
                    sb.Append(" vis=" + gg.VisibleClipBounds + " clip=" + gg.ClipBounds);
                    gg.FillRectangle(Brushes.Red, 0, 0, 10, 10);
                    gg.Dispose();
                    sb.Append(" after: W=" + Safe(() => mf.Width.ToString()) + " hdr=" + Safe(() => mf.GetMetafileHeader().Type.ToString()));
                    sb.Append(" again=" + Safe(() => Graphics.FromImage(mf).ToString()));
                    return sb.ToString();
                }
                finally { g.ReleaseHdc(h); }
            });
            return 0;
        }
        return 2;
    }

    static string Safe(Func<string> f) { try { return f(); } catch (Exception e) { return "EXC " + e.GetType().Name + "(" + e.Message + ")"; } }
    static void Try(string n, Func<string> f) { Console.WriteLine(n + ": " + Safe(f)); }
}
