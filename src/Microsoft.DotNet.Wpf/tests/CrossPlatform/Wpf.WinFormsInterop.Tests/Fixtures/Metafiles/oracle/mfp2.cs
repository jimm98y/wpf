using System; using System.Drawing; using System.Drawing.Imaging; using System.IO; using System.Reflection;
static class P {
 static string H(MetafileHeader h){ return h.Type+" ver=0x"+h.Version.ToString("x")+" size="+h.MetafileSize+" dpi="+h.DpiX.ToString("R")+","+h.DpiY.ToString("R")+" b="+h.Bounds+" disp="+h.IsDisplay(); }
 static string S(Func<string> f){ try{return f();}catch(Exception e){return "EXC "+e.GetType().Name+"("+e.Message+")";} }
 static void Main(string[] a){
  var g=Graphics.FromHwnd(IntPtr.Zero); var hdc=g.GetHdc();
  foreach (EmfType t in new[]{EmfType.EmfPlusOnly, EmfType.EmfPlusDual, EmfType.EmfOnly}) {
   var ms=new MemoryStream();
   var mf=new Metafile(ms,hdc,t,"Desc");
   Console.WriteLine(t+" before: "+S(()=>H(mf.GetMetafileHeader()))+" res="+S(()=>mf.HorizontalResolution.ToString("R"))+" phys="+S(()=>mf.PhysicalDimension.ToString())+" pf="+S(()=>mf.PixelFormat.ToString())+" raw="+S(()=>mf.RawFormat.Guid.ToString())+" flags="+S(()=>mf.Flags.ToString("x")));
   using(var gg=Graphics.FromImage(mf)){ gg.FillRectangle(Brushes.Red,10,20,30,40); Console.WriteLine(" streamlen during="+ms.Length+" pageunit="+gg.PageUnit+" scale="+gg.PageScale); }
   Console.WriteLine(" after: "+S(()=>H(mf.GetMetafileHeader()))+" W="+mf.Width+" H="+mf.Height+" streamlen="+ms.Length+" pos="+ms.Position);
   ms.Position=0; var re=new Metafile(ms); Console.WriteLine(" reload: "+H(re.GetMetafileHeader()));
   GraphicsUnit u=GraphicsUnit.Display; var b=mf.GetBounds(ref u); Console.WriteLine(" bounds "+b+" "+u);
  }
  // frame given
  foreach (MetafileFrameUnit fu in new[]{MetafileFrameUnit.Pixel,MetafileFrameUnit.Point,MetafileFrameUnit.Inch,MetafileFrameUnit.Document,MetafileFrameUnit.Millimeter,MetafileFrameUnit.GdiCompatible}){
   var ms=new MemoryStream(); var mf=new Metafile(ms,hdc,new RectangleF(1,2,100,80),fu,EmfType.EmfPlusOnly);
   using(var gg=Graphics.FromImage(mf)){ gg.FillRectangle(Brushes.Red,10,20,30,40); Console.Write(fu+" pu="+gg.PageUnit+" ps="+gg.PageScale+" vis="+gg.VisibleClipBounds+" t="+string.Join(",",gg.Transform.Elements)); }
   ms.Position=0; var re=new Metafile(ms); Console.WriteLine(" -> "+H(re.GetMetafileHeader()));
   ms.Position=0; File.WriteAllBytes("../ref/frame_"+fu+".emf", ms.ToArray());
  }
  Console.WriteLine("fname null: "+S(()=>new Metafile((string)null,hdc).ToString()));
  Console.WriteLine("hdc0 stream: "+S(()=>new Metafile(new MemoryStream(),IntPtr.Zero).ToString()));
  g.ReleaseHdc(hdc);
 }}
