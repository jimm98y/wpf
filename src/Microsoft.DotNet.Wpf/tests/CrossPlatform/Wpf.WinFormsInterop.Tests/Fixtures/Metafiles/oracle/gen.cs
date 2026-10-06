using System; using System.IO; using System.Runtime.InteropServices;
static class G {
  [DllImport("gdi32.dll", CharSet=CharSet.Unicode)] static extern IntPtr CreateMetaFileW(string f);
  [DllImport("gdi32.dll")] static extern IntPtr CloseMetaFile(IntPtr h);
  [DllImport("gdi32.dll")] static extern uint GetMetaFileBitsEx(IntPtr h, uint n, byte[] b);
  [DllImport("gdi32.dll")] static extern bool RoundRect(IntPtr h, int l, int t, int r, int b, int w, int hh);
  [DllImport("gdi32.dll")] static extern bool Arc(IntPtr h, int l, int t, int r, int b, int a, int bb, int c, int d);
  [DllImport("gdi32.dll")] static extern bool Pie(IntPtr h, int l, int t, int r, int b, int a, int bb, int c, int d);
  [DllImport("gdi32.dll")] static extern bool Chord(IntPtr h, int l, int t, int r, int b, int a, int bb, int c, int d);
  [DllImport("gdi32.dll")] static extern bool Rectangle(IntPtr h, int l, int t, int r, int b);
  [DllImport("gdi32.dll")] static extern bool MoveToEx(IntPtr h, int x, int y, IntPtr p);
  [DllImport("gdi32.dll")] static extern bool LineTo(IntPtr h, int x, int y);
  [DllImport("gdi32.dll")] static extern IntPtr CreatePen(int s, int w, int c);
  [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr h, IntPtr o);
  [DllImport("gdi32.dll")] static extern bool IntersectClipRect(IntPtr h, int l, int t, int r, int b);
  [DllImport("gdi32.dll")] static extern int SaveDC(IntPtr h);
  [DllImport("gdi32.dll")] static extern bool RestoreDC(IntPtr h, int i);
  static void Main() {
    IntPtr dc = CreateMetaFileW(null);
    RoundRect(dc, 10, 10, 60, 40, 8, 8);
    Arc(dc, 70, 10, 120, 40, 70, 10, 120, 40);
    Pie(dc, 10, 50, 60, 90, 10, 50, 60, 90);
    Chord(dc, 70, 50, 120, 90, 70, 50, 120, 90);
    SaveDC(dc);
    IntersectClipRect(dc, 5, 95, 100, 140);
    Rectangle(dc, 10, 100, 40, 130);
    RestoreDC(dc, -1);
    SelectObject(dc, CreatePen(0, 1, 0xff));
    MoveToEx(dc, 50, 100, IntPtr.Zero); LineTo(dc, 80, 120);
    SelectObject(dc, CreatePen(0, 0, 0xff));
    MoveToEx(dc, 90, 100, IntPtr.Zero); LineTo(dc, 110, 140);
    SelectObject(dc, CreatePen(0, 7, 0xff));
    MoveToEx(dc, 130, 100, IntPtr.Zero); LineTo(dc, 130, 140);
    IntPtr h = CloseMetaFile(dc);
    uint n = GetMetaFileBitsEx(h, 0, null); var b = new byte[n]; GetMetaFileBitsEx(h, n, b);
    File.WriteAllBytes("shapes.wmf", b);
  }
}
