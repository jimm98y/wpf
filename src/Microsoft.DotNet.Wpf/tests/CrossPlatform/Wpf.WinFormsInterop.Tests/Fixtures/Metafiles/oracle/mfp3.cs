using System; using System.Drawing; using System.Drawing.Imaging; using System.IO;
static class P {
 static void Main(string[] a){
  var g=Graphics.FromHwnd(IntPtr.Zero); var hdc=g.GetHdc();
  var cases = new Func<MemoryStream,Metafile>[] {
    ms=>new Metafile(ms,hdc,EmfType.EmfPlusOnly),
    ms=>new Metafile(ms,hdc,new RectangleF(),MetafileFrameUnit.GdiCompatible,EmfType.EmfPlusOnly),
    ms=>new Metafile(ms,hdc,new Rectangle(),MetafileFrameUnit.GdiCompatible,EmfType.EmfPlusOnly),
    ms=>new Metafile(ms,hdc,new RectangleF(5,5,0,0),MetafileFrameUnit.Pixel,EmfType.EmfPlusOnly),
    ms=>new Metafile(ms,hdc,new RectangleF(0,0,1,1),MetafileFrameUnit.Pixel,EmfType.EmfPlusOnly),
  };
  int i=0;
  foreach(var c in cases){ var ms=new MemoryStream(); var mf=c(ms); using(var gg=Graphics.FromImage(mf)){ gg.FillRectangle(Brushes.Red,10,20,30,40);} File.WriteAllBytes("../ref/fr"+(i++)+".emf", ms.ToArray()); }
  g.ReleaseHdc(hdc);
 }}
