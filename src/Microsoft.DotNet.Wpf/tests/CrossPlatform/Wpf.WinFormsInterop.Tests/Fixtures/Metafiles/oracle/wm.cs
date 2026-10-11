using System; using System.Drawing; using System.Drawing.Imaging; using System.IO; using System.Runtime.InteropServices;
static class W {
  [DllImport("gdi32.dll")] static extern uint GetEnhMetaFileBits(IntPtr h, uint n, byte[] b);
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
  [DllImport("gdi32.dll")] static extern int GetDeviceCaps(IntPtr h, int i);
  static void Main(string[] a) {
    IntPtr dc = GetDC(IntPtr.Zero);
    Console.WriteLine("screen px {0}x{1} mm {2}x{3} logpix {4}", GetDeviceCaps(dc,8), GetDeviceCaps(dc,10), GetDeviceCaps(dc,4), GetDeviceCaps(dc,6), GetDeviceCaps(dc,88));
    foreach (var f in a) {
      var m = new Metafile(f);
      var h = m.GetMetafileHeader();
      Console.WriteLine("{0}: bounds {1} dpi {2}x{3} size {4} type {5}", f, h.Bounds, h.DpiX, h.DpiY, h.MetafileSize, h.Type);
      IntPtr e = m.GetHenhmetafile();
      uint n = GetEnhMetaFileBits(e, 0, null); var b = new byte[n]; GetEnhMetaFileBits(e, n, b);
      File.WriteAllBytes(f + ".conv.emf", b);
    }
  }
}
