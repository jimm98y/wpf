// The hand-written half of DrawingExceptionCases: states and data reflection cannot build.

#nullable disable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Drawing.Printing;

namespace Wpf.WinFormsInterop.Tests
{
    internal static partial class DrawingExceptionCases
    {
        private static string BadFile()
        {
            string bad = Path.Combine(TempDir, "bad.png");
            File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            return bad;
        }

        // Runs on a thread of its own, the exception (if any) rethrown as it was.
        private static object OnThread(Func<object> f)
        {
            object r = null;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo ex = null;
            var t = new System.Threading.Thread(() =>
            {
                try { r = f(); }
                catch (Exception e) { ex = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e); }
            });
            t.Start();
            t.Join();
            ex?.Throw();
            return r;
        }

        private static T Disposed<T>(T o) where T : IDisposable
        {
            o.Dispose();
            return o;
        }

        private static void AddHandWritten(List<Case> l)
        {
            foreach (PixelFormat pf in new[]
            {
                PixelFormat.Format16bppArgb1555, PixelFormat.Format16bppGrayScale, PixelFormat.Format8bppIndexed,
                PixelFormat.Format4bppIndexed, PixelFormat.Format1bppIndexed, PixelFormat.Format16bppRgb555,
                PixelFormat.Format16bppRgb565, PixelFormat.Format24bppRgb, PixelFormat.Format32bppRgb,
                PixelFormat.Format32bppPArgb, PixelFormat.Format48bppRgb, PixelFormat.Format64bppArgb, PixelFormat.Format64bppPArgb,
            })
            {
                PixelFormat f = pf;
                H(l, "FromImage " + f, () => { using var b = new Bitmap(8, 8, f); using var g = Graphics.FromImage(b); return g.DpiX; });
                H(l, "Bitmap ctor " + f, () => { using var b = new Bitmap(3, 3, f); return b.PixelFormat; });
                H(l, "SetPixel " + f, () => { using var b = new Bitmap(3, 3, f); b.SetPixel(1, 1, Color.FromArgb(128, 10, 200, 30)); return b.GetPixel(1, 1); });
                H(l, "Clone to " + f, () => { using var b = new Bitmap(8, 8); using var c = b.Clone(new Rectangle(0, 0, 8, 8), f); return c.PixelFormat; });
                H(l, "MakeTransparent " + f, () => { using var b = new Bitmap(3, 3, f); b.MakeTransparent(); return b.PixelFormat; });
                H(l, "LockBits other format " + f, () => { using var b = new Bitmap(4, 4); var d = b.LockBits(new Rectangle(0, 0, 4, 4), ImageLockMode.ReadOnly, f); return d.Stride; });
            }

            H(l, "FromFile garbage", () => Image.FromFile(BadFile()));
            H(l, "FromFile garbage noicm", () => Image.FromFile(BadFile(), true));
            H(l, "FromStream garbage", () => Image.FromStream(new MemoryStream(new byte[] { 1, 2, 3 })));
            H(l, "FromStream empty", () => Image.FromStream(new MemoryStream()));
            H(l, "FromStream nonvalidate garbage", () => Image.FromStream(new MemoryStream(new byte[] { 1, 2, 3 }), false, false));
            H(l, "new Bitmap(garbage stream)", () => new Bitmap(new MemoryStream(new byte[] { 1, 2, 3 })));
            H(l, "new Bitmap(garbage file)", () => new Bitmap(BadFile()));
            H(l, "FromFile missing", () => Image.FromFile(Path.Combine(TempDir, "none_xyz.png")));
            H(l, "Bitmap 0x0", () => new Bitmap(0, 0));
            H(l, "Bitmap huge", () => new Bitmap(100000, 100000));
            H(l, "Bitmap 1x huge", () => new Bitmap(1, 1 << 30));
            // Sizes whose stride or buffer overflow 32 bits: refused before anything is allocated.
            H(l, "Bitmap stride overflow", () => new Bitmap(1 << 29, 1));
            H(l, "Bitmap 65536 square", () => new Bitmap(65536, 65536));
            H(l, "Bitmap 48bpp overflow", () => new Bitmap(1 << 28, 1, PixelFormat.Format48bppRgb));
            H(l, "Bitmap neg", () => new Bitmap(-1, 5));
            H(l, "Bitmap bad format", () => new Bitmap(4, 4, PixelFormat.Undefined));
            H(l, "Bitmap format Indexed flag", () => new Bitmap(4, 4, PixelFormat.Indexed));
            H(l, "Bitmap format Max", () => new Bitmap(4, 4, PixelFormat.Max));
            H(l, "Bitmap stride bad", () => new Bitmap(4, 4, 3, PixelFormat.Format32bppArgb, IntPtr.Zero));
            H(l, "Bitmap stride 0", () => new Bitmap(4, 4, 0, PixelFormat.Format32bppArgb, IntPtr.Zero).Width);
            H(l, "Bitmap stride neg", () => new Bitmap(4, 4, -16, PixelFormat.Format32bppArgb, IntPtr.Zero).Width);
            H(l, "Bitmap(image, 0, 0)", () => new Bitmap(new Bitmap(4, 4), 0, 0));
            H(l, "Bitmap(image, size neg)", () => new Bitmap(new Bitmap(4, 4), new Size(-1, 2)));
            H(l, "Bitmap(w,h,Graphics null)", () => new Bitmap(4, 4, (Graphics)null));
            H(l, "Bitmap(Type, missing)", () => new Bitmap(typeof(Bitmap), "nope.bmp"));

            H(l, "LGB empty rect", () => new LinearGradientBrush(new Rectangle(0, 0, 0, 0), Color.Red, Color.Blue, 0f));
            H(l, "LGB empty width", () => new LinearGradientBrush(new RectangleF(0, 0, 0, 5), Color.Red, Color.Blue, LinearGradientMode.Vertical));
            H(l, "LGB same points", () => new LinearGradientBrush(new Point(1, 1), new Point(1, 1), Color.Red, Color.Blue));
            H(l, "LGB bad mode", () => new LinearGradientBrush(new Rectangle(0, 0, 4, 4), Color.Red, Color.Blue, (LinearGradientMode)9));
            H(l, "LGB Blend bad", () => { var b = new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue); b.Blend = new Blend(1); });
            H(l, "LGB Blend mismatched", () => { var b = new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue); b.Blend = new Blend { Factors = new[] { 0f, 1f }, Positions = new[] { 0f } }; });
            H(l, "LGB Blend 3 not ending 1", () => { var b = new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue); b.Blend = new Blend(3) { Factors = new[] { 0f, .5f, 1f }, Positions = new[] { 0f, .5f, .9f } }; return b.Blend.Positions; });
            H(l, "LGB Blend not starting 0", () => { var b = new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue); b.Blend = new Blend(2) { Factors = new[] { 0f, 1f }, Positions = new[] { 0.1f, 1f } }; return b.Blend.Positions; });
            H(l, "LGB Blend get default", () => new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue).Blend.Factors);
            H(l, "LGB InterpolationColors get unset", () => new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue).InterpolationColors);
            H(l, "LGB InterpolationColors 1", () => { var b = new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue); b.InterpolationColors = new ColorBlend(1); });
            H(l, "LGB InterpolationColors mismatched", () => { var b = new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue); b.InterpolationColors = new ColorBlend { Colors = new[] { Color.Red, Color.Blue }, Positions = new[] { 0f } }; });
            H(l, "LGB InterpolationColors bad start", () => { var b = new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue); b.InterpolationColors = new ColorBlend { Colors = new[] { Color.Red, Color.Blue }, Positions = new[] { 0.5f, 1f } }; });
            H(l, "LGB InterpolationColors bad end", () => { var b = new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue); b.InterpolationColors = new ColorBlend { Colors = new[] { Color.Red, Color.Blue }, Positions = new[] { 0f, 0.5f } }; });
            H(l, "LGB SetSigmaBellShape 2", () => new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue).SetSigmaBellShape(2f));
            H(l, "LGB SetBlendTriangularShape -1", () => new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue).SetBlendTriangularShape(-1f));
            H(l, "LGB SetBlendTriangularShape scale 2", () => new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue).SetBlendTriangularShape(.5f, 2f));
            H(l, "LGB WrapMode Clamp", () => { var b = new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue); b.WrapMode = WrapMode.Clamp; });
            H(l, "LGB WrapMode bad", () => { var b = new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue); b.WrapMode = (WrapMode)9; });
            H(l, "LGB Transform singular", () => { var b = new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue); b.Transform = new Matrix(0, 0, 0, 0, 0, 0); });
            H(l, "LGB MultiplyTransform singular", () => { var b = new LinearGradientBrush(new Point(0, 0), new Point(5, 5), Color.Red, Color.Blue); b.MultiplyTransform(new Matrix(0, 0, 0, 0, 0, 0)); });

            H(l, "PGB 1 point", () => new PathGradientBrush(new[] { new PointF(1, 1) }));
            H(l, "PGB 2 points", () => new PathGradientBrush(new[] { new PointF(1, 1), new PointF(2, 2) }).Rectangle);
            H(l, "PGB 0 points", () => new PathGradientBrush(new PointF[0]));
            H(l, "PGB empty path", () => new PathGradientBrush(new GraphicsPath()));
            H(l, "PGB 1-point path", () => { var p = new GraphicsPath(); p.AddLine(1, 1, 1, 1); return new PathGradientBrush(p).Rectangle; });
            H(l, "PGB bad wrap", () => new PathGradientBrush(Pts3(), (WrapMode)9));
            H(l, "PGB SurroundColors too many", () => { var b = new PathGradientBrush(Pts3()); b.SurroundColors = new[] { Color.Red, Color.Red, Color.Red, Color.Red }; });
            H(l, "PGB SurroundColors empty", () => { var b = new PathGradientBrush(Pts3()); b.SurroundColors = new Color[0]; });
            H(l, "PGB Blend bad", () => { var b = new PathGradientBrush(Pts3()); b.Blend = new Blend(1); });
            H(l, "PGB InterpolationColors get unset", () => new PathGradientBrush(Pts3()).InterpolationColors.Colors);
            H(l, "PGB InterpolationColors bad", () => { var b = new PathGradientBrush(Pts3()); b.InterpolationColors = new ColorBlend(1); });
            H(l, "PGB FocusScales", () => { var b = new PathGradientBrush(Pts3()); b.FocusScales = new PointF(-2, 5); return b.FocusScales; });
            H(l, "PGB Transform singular", () => { var b = new PathGradientBrush(Pts3()); b.Transform = new Matrix(0, 0, 0, 0, 0, 0); });
            H(l, "PGB WrapMode bad", () => { var b = new PathGradientBrush(Pts3()); b.WrapMode = (WrapMode)9; });

            H(l, "TextureBrush bad rect", () => { using var b = new Bitmap(8, 8); return new TextureBrush(b, new RectangleF(4, 4, 10, 10)); });
            H(l, "TextureBrush zero rect", () => { using var b = new Bitmap(8, 8); return new TextureBrush(b, new Rectangle(0, 0, 0, 3)); });
            H(l, "TextureBrush bad wrap", () => { using var b = new Bitmap(8, 8); return new TextureBrush(b, (WrapMode)9); });
            H(l, "TextureBrush metafile", () => new TextureBrush(Mf()).WrapMode);
            H(l, "TextureBrush Image after source disposed", () => { var b = new Bitmap(8, 8); var t = new TextureBrush(b); b.Dispose(); return t.Image; });
            H(l, "TextureBrush WrapMode bad", () => { var t = new TextureBrush(new Bitmap(8, 8)); t.WrapMode = (WrapMode)9; });
            H(l, "TextureBrush Transform singular", () => { var t = new TextureBrush(new Bitmap(8, 8)); t.Transform = new Matrix(0, 0, 0, 0, 0, 0); });

            H(l, "HatchBrush bad style", () => new HatchBrush((HatchStyle)99, Color.Red));
            H(l, "HatchBrush neg style", () => new HatchBrush((HatchStyle)(-1), Color.Red));

            H(l, "Pen width neg", () => new Pen(Color.Red, -1).Width);
            H(l, "Pen width neg brush", () => new Pen(Brushes.Red, -1).Width);
            H(l, "Pen set width neg", () => { var p = new Pen(Color.Red); p.Width = -3; return p.Width; });
            H(l, "Pen width neg draw", () => { using var b = new Bitmap(8, 8); using var g = Graphics.FromImage(b); g.DrawLine(new Pen(Color.Red, -1), 0, 0, 5, 5); });
            H(l, "Pen compound bad", () => { var p = new Pen(Color.Red); p.CompoundArray = new float[] { 0.5f }; });
            H(l, "Pen compound empty", () => { var p = new Pen(Color.Red); p.CompoundArray = new float[0]; return p.CompoundArray; });
            H(l, "Pen compound decreasing", () => { var p = new Pen(Color.Red); p.CompoundArray = new float[] { 0.5f, 0.2f }; });
            H(l, "Pen compound out of range", () => { var p = new Pen(Color.Red); p.CompoundArray = new float[] { 0f, 2f }; });
            H(l, "Pen compound get default", () => new Pen(Color.Red).CompoundArray);
            H(l, "Pen compound inset", () => { var p = new Pen(Color.Red) { Alignment = PenAlignment.Inset }; p.CompoundArray = new float[] { 0, 1 }; return p.CompoundArray; });
            H(l, "Pen inset after compound", () => { var p = new Pen(Color.Red) { CompoundArray = new float[] { 0, 1 } }; p.Alignment = PenAlignment.Inset; return p.Alignment; });
            H(l, "Pen dash empty", () => { var p = new Pen(Color.Red); p.DashPattern = new float[0]; });
            H(l, "Pen dash zero", () => { var p = new Pen(Color.Red); p.DashPattern = new float[] { 0, 1 }; });
            H(l, "Pen dash neg", () => { var p = new Pen(Color.Red); p.DashPattern = new float[] { -1, 1 }; });
            H(l, "Pen dash one", () => { var p = new Pen(Color.Red); p.DashPattern = new float[] { 2 }; return p.DashPattern; });
            H(l, "Pen dash get solid", () => new Pen(Color.Red).DashPattern);
            H(l, "Pen dash get dash", () => new Pen(Color.Red) { DashStyle = DashStyle.Dash }.DashPattern);
            H(l, "Pen dash get custom", () => new Pen(Color.Red) { DashStyle = DashStyle.Custom }.DashPattern);
            H(l, "Pen dashstyle bad", () => new Pen(Color.Red) { DashStyle = (DashStyle)9 });
            H(l, "Pen dashcap triangle", () => new Pen(Color.Red) { DashCap = DashCap.Triangle }.DashCap);
            H(l, "Pen dashcap 1", () => new Pen(Color.Red) { DashCap = (DashCap)1 }.DashCap);
            H(l, "Pen dashcap bad", () => new Pen(Color.Red) { DashCap = (DashCap)5 }.DashCap);
            H(l, "Pen startcap custom", () => new Pen(Color.Red) { StartCap = LineCap.Custom }.StartCap);
            H(l, "Pen startcap bad", () => new Pen(Color.Red) { StartCap = (LineCap)5 }.StartCap);
            H(l, "Pen startcap 0x15", () => new Pen(Color.Red) { StartCap = (LineCap)0x15 }.StartCap);
            H(l, "Pen linejoin bad", () => new Pen(Color.Red) { LineJoin = (LineJoin)9 });
            H(l, "Pen alignment bad", () => new Pen(Color.Red) { Alignment = (PenAlignment)9 });
            H(l, "Pen miter 0", () => new Pen(Color.Red) { MiterLimit = 0 }.MiterLimit);
            H(l, "Pen transform singular", () => new Pen(Color.Red) { Transform = new Matrix(0, 0, 0, 0, 0, 0) });
            H(l, "Pen color of texture pen", () => new Pen(new TextureBrush(new Bitmap(2, 2))).Color);
            H(l, "Pen color of hatch pen", () => new Pen(new HatchBrush(HatchStyle.Cross, Color.Red)).Color);
            H(l, "Pen PenType hatch", () => new Pen(new HatchBrush(HatchStyle.Cross, Color.Red)).PenType);
            H(l, "Pen CustomStartCap unset", () => new Pen(Color.Red).CustomStartCap);
            H(l, "Pen system modify", () => { Pens.Red.Width = 3; });
            H(l, "Pen system dispose", () => Pens.Red.Dispose());
            H(l, "Pen system color", () => { Pens.Red.Color = Color.Blue; });
            H(l, "SystemPens modify", () => { SystemPens.Control.Width = 3; });
            // Brushes.* are per thread (stock keeps them in its thread data): these run on a thread of
            // their own so nothing else sees the brush they change or dispose.
            H(l, "Brush system modify", () => OnThread(() => { ((SolidBrush)Brushes.Red).Color = Color.Blue; return ((SolidBrush)Brushes.Red).Color; }));
            H(l, "Brush system dispose", () => OnThread(() => { Brushes.Red.Dispose(); return null; }));
            H(l, "Brush system modify after dispose", () => OnThread(() => { Brushes.Red.Dispose(); ((SolidBrush)Brushes.Red).Color = Color.Blue; return null; }));
            H(l, "Brush system same color after dispose", () => OnThread(() => { Brushes.Red.Dispose(); ((SolidBrush)Brushes.Red).Color = Color.Red; return ((SolidBrush)Brushes.Red).Color; }));
            H(l, "SystemBrushes dispose", () => SystemBrushes.Control.Dispose());
            H(l, "SystemBrushes FromSystemColor non-system", () => SystemBrushes.FromSystemColor(Color.Red));
            H(l, "SystemPens FromSystemColor non-system", () => SystemPens.FromSystemColor(Color.Red));

            H(l, "Font bogus family", () => new Font("NoSuchFamilyXyz", 10).Size);
            H(l, "FontFamily bogus", () => new FontFamily("NoSuchFamilyXyz"));
            H(l, "FontFamily empty", () => new FontFamily(""));
            H(l, "Font size 0", () => new Font("Arial", 0f));
            H(l, "Font size neg", () => new Font(FontFamily.GenericSansSerif, -1f));
            H(l, "Font size NaN", () => new Font(FontFamily.GenericSansSerif, float.NaN));
            H(l, "Font size inf", () => new Font(FontFamily.GenericSansSerif, float.PositiveInfinity));
            H(l, "Font unit World", () => new Font(FontFamily.GenericSansSerif, 8, FontStyle.Regular, GraphicsUnit.World));
            H(l, "Font unit Display", () => new Font(FontFamily.GenericSansSerif, 8, FontStyle.Regular, GraphicsUnit.Display));
            H(l, "Font unit bad", () => new Font(FontFamily.GenericSansSerif, 8, FontStyle.Regular, (GraphicsUnit)42));
            H(l, "Font style bad", () => new Font(FontFamily.GenericSansSerif, 8, (FontStyle)64).Style);
            H(l, "Font(prototype null)", () => new Font((Font)null, FontStyle.Bold));
            H(l, "Font disposed size", () => Disposed(new Font(FontFamily.GenericSansSerif, 10)).Size);
            H(l, "Font disposed style", () => Disposed(new Font(FontFamily.GenericSansSerif, 10)).Style);
            H(l, "Font disposed unit", () => Disposed(new Font(FontFamily.GenericSansSerif, 10)).Unit);
            H(l, "Font disposed clone", () => Disposed(new Font(FontFamily.GenericSansSerif, 10)).Clone().GetType().Name);
            H(l, "Font disposed GetHeight", () => Disposed(new Font(FontFamily.GenericSansSerif, 10)).GetHeight() > 0);
            H(l, "Font disposed ToHfont", () => Disposed(new Font(FontFamily.GenericSansSerif, 10)).ToHfont() != IntPtr.Zero);
            H(l, "Font disposed Height", () => Disposed(new Font(FontFamily.GenericSansSerif, 10)).Height > 0);
            H(l, "Font disposed FontFamily", () => Disposed(new Font(FontFamily.GenericSansSerif, 10)).FontFamily != null);
            H(l, "Font FromHfont 0", () => Font.FromHfont(IntPtr.Zero));
            H(l, "Font FromLogFont null", () => Font.FromLogFont(null));
            H(l, "Font FromHdc 0", () => Font.FromHdc(IntPtr.Zero));
            H(l, "Font ToLogFont null", () => new Font(FontFamily.GenericSansSerif, 8).ToLogFont(null));
            H(l, "Font ToLogFont wrong type", () => new Font(FontFamily.GenericSansSerif, 8).ToLogFont(new object()));
            H(l, "FontFamily disposed Name", () => Disposed(new FontFamily(GenericFontFamilies.Serif)).Name.Length > 0);
            H(l, "FontFamily generic dispose", () => { FontFamily.GenericSerif.Dispose(); return FontFamily.GenericSerif.Name.Length > 0; });
            H(l, "FontFamily IsStyleAvailable bad", () => FontFamily.GenericSansSerif.IsStyleAvailable((FontStyle)64));
            H(l, "FontFamily GetEmHeight bad style", () => FontFamily.GenericSansSerif.GetEmHeight((FontStyle)64));
            H(l, "FontFamily(name, null collection)", () => new FontFamily("Arial", null).Name, true);
            H(l, "FontFamily(name, empty private)", () => new FontFamily("Arial", new PrivateFontCollection()));
            H(l, "PrivateFontCollection garbage", () => { var c = new PrivateFontCollection(); c.AddFontFile(BadFile()); return c.Families.Length; });
            H(l, "PrivateFontCollection missing", () => new PrivateFontCollection().AddFontFile(Path.Combine(TempDir, "nofont.ttf")));
            H(l, "PrivateFontCollection empty path", () => new PrivateFontCollection().AddFontFile(""));
            H(l, "PrivateFontCollection mem 0", () => new PrivateFontCollection().AddMemoryFont(IntPtr.Zero, 10));
            H(l, "PrivateFontCollection mem neg", () => new PrivateFontCollection().AddMemoryFont(new IntPtr(16), -1));
            H(l, "PrivateFontCollection disposed families", () => Disposed(new PrivateFontCollection()).Families);
            H(l, "PrivateFontCollection disposed add", () => Disposed(new PrivateFontCollection()).AddFontFile(BadFile()));
            H(l, "InstalledFontCollection disposed families", () => Disposed(new InstalledFontCollection()).Families.Length > 0);

            H(l, "Matrix invert singular", () => { var m = new Matrix(0, 0, 0, 0, 0, 0); m.Invert(); });
            H(l, "Matrix invert near-singular", () => { var m = new Matrix(1e-7f, 0, 0, 1e-7f, 0, 0); m.Invert(); return m.Elements; });
            H(l, "Matrix rect/3 points null", () => new Matrix(new Rectangle(0, 0, 4, 4), null));
            H(l, "Matrix rect/2 points", () => new Matrix(new Rectangle(0, 0, 4, 4), new[] { new Point(1, 1), new Point(2, 2) }));
            H(l, "Matrix rect/4 points", () => new Matrix(new RectangleF(0, 0, 4, 4), new[] { new PointF(1, 1), new PointF(2, 2), new PointF(3, 3), new PointF(4, 4) }).Elements);
            H(l, "Matrix empty rect", () => new Matrix(new RectangleF(0, 0, 0, 4), Pts3()).Elements);
            H(l, "Matrix order bad", () => new Matrix().Translate(1, 1, (MatrixOrder)5));
            H(l, "Matrix multiply singular", () => { var m = new Matrix(); m.Multiply(new Matrix(0, 0, 0, 0, 0, 0)); return m.Elements; });
            H(l, "Matrix multiply self", () => { var m = new Matrix(1, 2, 3, 4, 5, 6); m.Multiply(m); return m.Elements; });
            H(l, "Matrix shear singular", () => { var m = new Matrix(); m.Shear(1, 1); return m.Elements; });
            H(l, "Matrix scale 0", () => { var m = new Matrix(); m.Scale(0, 1); return m.Elements; });
            H(l, "Matrix TransformPoints empty", () => new Matrix().TransformPoints(new PointF[0]));
            H(l, "Matrix TransformVectors empty", () => new Matrix().TransformVectors(new Point[0]));
            H(l, "Matrix RotateAt order bad", () => new Matrix().RotateAt(10, new PointF(1, 1), (MatrixOrder)5));
            H(l, "Matrix MatrixElements set", () => { var m = new Matrix(); m.MatrixElements = new System.Numerics.Matrix3x2(1, 2, 3, 4, 5, 6); return m.Elements; });

            H(l, "LockBits outside", () => { using var b = new Bitmap(8, 8); b.LockBits(new Rectangle(4, 4, 8, 8), ImageLockMode.ReadOnly, b.PixelFormat); });
            H(l, "LockBits empty", () => { using var b = new Bitmap(8, 8); b.LockBits(new Rectangle(0, 0, 0, 8), ImageLockMode.ReadOnly, b.PixelFormat); });
            H(l, "LockBits twice", () => { using var b = new Bitmap(8, 8); b.LockBits(new Rectangle(0, 0, 8, 8), ImageLockMode.ReadOnly, b.PixelFormat); b.LockBits(new Rectangle(0, 0, 8, 8), ImageLockMode.ReadOnly, b.PixelFormat); });
            H(l, "LockBits twice disjoint", () => { using var b = new Bitmap(8, 8); b.LockBits(new Rectangle(0, 0, 4, 8), ImageLockMode.ReadOnly, b.PixelFormat); b.LockBits(new Rectangle(4, 0, 4, 8), ImageLockMode.ReadOnly, b.PixelFormat); });
            H(l, "LockBits mode 0", () => { using var b = new Bitmap(8, 8); b.LockBits(new Rectangle(0, 0, 8, 8), 0, b.PixelFormat); });
            H(l, "LockBits mode 8", () => { using var b = new Bitmap(8, 8); b.LockBits(new Rectangle(0, 0, 8, 8), (ImageLockMode)8, b.PixelFormat); });
            H(l, "LockBits fmt undefined", () => { using var b = new Bitmap(8, 8); b.LockBits(new Rectangle(0, 0, 8, 8), ImageLockMode.ReadOnly, PixelFormat.Undefined); });
            H(l, "LockBits fmt indexed from 32", () => { using var b = new Bitmap(8, 8); return b.LockBits(new Rectangle(0, 0, 8, 8), ImageLockMode.ReadOnly, PixelFormat.Format8bppIndexed).Stride; });
            H(l, "LockBits user buffer null", () => { using var b = new Bitmap(8, 8); b.LockBits(new Rectangle(0, 0, 8, 8), ImageLockMode.ReadOnly | ImageLockMode.UserInputBuffer, PixelFormat.Format32bppArgb, null); });
            H(l, "LockBits user buffer no scan0", () => { using var b = new Bitmap(8, 8); b.LockBits(new Rectangle(0, 0, 8, 8), ImageLockMode.ReadOnly | ImageLockMode.UserInputBuffer, PixelFormat.Format32bppArgb, new BitmapData()); });
            H(l, "UnlockBits never locked", () => { using var b = new Bitmap(8, 8); b.UnlockBits(new BitmapData()); });
            H(l, "UnlockBits null", () => { using var b = new Bitmap(8, 8); b.UnlockBits(null); });
            H(l, "UnlockBits twice", () => { using var b = new Bitmap(8, 8); var d = b.LockBits(new Rectangle(0, 0, 8, 8), ImageLockMode.ReadOnly, b.PixelFormat); b.UnlockBits(d); b.UnlockBits(d); });
            H(l, "Dispose while locked", () => { var b = new Bitmap(8, 8); b.LockBits(new Rectangle(0, 0, 8, 8), ImageLockMode.ReadOnly, b.PixelFormat); b.Dispose(); });
            H(l, "SetPixel indexed", () => { using var b = new Bitmap(8, 8, PixelFormat.Format8bppIndexed); b.SetPixel(1, 1, Color.Red); });
            H(l, "GetPixel out", () => { using var b = new Bitmap(8, 8); b.GetPixel(9, 9); });
            H(l, "GetPixel neg", () => { using var b = new Bitmap(8, 8); b.GetPixel(-1, 0); });
            H(l, "SetPixel out", () => { using var b = new Bitmap(8, 8); b.SetPixel(0, 8, Color.Red); });
            H(l, "SetResolution 0", () => { using var b = new Bitmap(8, 8); b.SetResolution(0, 96); });
            H(l, "SetResolution neg", () => { using var b = new Bitmap(8, 8); b.SetResolution(96, -1); });
            H(l, "SetResolution NaN", () => { using var b = new Bitmap(8, 8); b.SetResolution(float.NaN, 96); return b.HorizontalResolution; });
            H(l, "MakeTransparent icon bitmap", () => { using var b = new Icon(SystemIcons.Application, 16, 16).ToBitmap(); b.MakeTransparent(); return b.PixelFormat; });
            H(l, "MakeTransparent from icon file", () => { var ms = new MemoryStream(); new Icon(SystemIcons.Application, 16, 16).Save(ms); ms.Position = 0; using var b = (Bitmap)Image.FromStream(ms); b.MakeTransparent(); });
            H(l, "Palette set null", () => { using var b = new Bitmap(8, 8, PixelFormat.Format8bppIndexed); b.Palette = null; });
            H(l, "Palette get 32bpp", () => { using var b = new Bitmap(8, 8); return b.Palette.Entries.Length; });
            H(l, "Palette set on 32bpp", () => { using var b = new Bitmap(8, 8); b.Palette = Palette(); return b.Palette.Entries.Length; });
            H(l, "Palette get 1bpp", () => { using var b = new Bitmap(8, 8, PixelFormat.Format1bppIndexed); return b.Palette.Entries; });
            H(l, "PropertyItems empty", () => new Bitmap(4, 4).PropertyItems);
            H(l, "PropertyIdList empty", () => new Bitmap(4, 4).PropertyIdList);
            H(l, "GetPropertyItem missing", () => new Bitmap(4, 4).GetPropertyItem(0x10F));
            H(l, "RemovePropertyItem missing", () => new Bitmap(4, 4).RemovePropertyItem(0x10F));
            H(l, "SetPropertyItem null", () => new Bitmap(4, 4).SetPropertyItem(null));
            H(l, "RotateFlip bad", () => new Bitmap(4, 4).RotateFlip((RotateFlipType)9));
            H(l, "FrameDimensionsList", () => new Bitmap(4, 4).FrameDimensionsList);
            H(l, "GetFrameCount Time", () => new Bitmap(4, 4).GetFrameCount(FrameDimension.Time));
            H(l, "GetFrameCount Resolution", () => new Bitmap(4, 4).GetFrameCount(FrameDimension.Resolution));
            H(l, "GetFrameCount bogus", () => new Bitmap(4, 4).GetFrameCount(new FrameDimension(Guid.NewGuid())));
            H(l, "SelectActiveFrame Time 3", () => { using var b = new Bitmap(8, 8); return b.SelectActiveFrame(FrameDimension.Time, 3); });
            H(l, "SelectActiveFrame Page 1", () => { using var b = new Bitmap(8, 8); return b.SelectActiveFrame(FrameDimension.Page, 1); });
            H(l, "SelectActiveFrame Page -1", () => { using var b = new Bitmap(8, 8); return b.SelectActiveFrame(FrameDimension.Page, -1); });
            H(l, "SelectActiveFrame bogus", () => { using var b = new Bitmap(8, 8); return b.SelectActiveFrame(new FrameDimension(Guid.NewGuid()), 0); });
            H(l, "Save MemoryBmp", () => { using var b = new Bitmap(8, 8); b.Save(new MemoryStream(), ImageFormat.MemoryBmp); });
            H(l, "Save Exif", () => { using var b = new Bitmap(8, 8); b.Save(new MemoryStream(), ImageFormat.Exif); });
            H(l, "Save bogus format", () => { using var b = new Bitmap(8, 8); b.Save(new MemoryStream(), new ImageFormat(Guid.NewGuid())); });
            H(l, "Save Icon format", () => { using var b = new Bitmap(8, 8); var ms = new MemoryStream(); b.Save(ms, ImageFormat.Icon); return ms.Length > 0; });
            H(l, "Save Emf format", () => { using var b = new Bitmap(8, 8); var ms = new MemoryStream(); b.Save(ms, ImageFormat.Emf); return ms.Length > 0; });
            H(l, "Save Wmf format", () => { using var b = new Bitmap(8, 8); var ms = new MemoryStream(); b.Save(ms, ImageFormat.Wmf); return ms.Length > 0; });
            H(l, "Save to readonly stream", () => { using var b = new Bitmap(8, 8); var ms = new MemoryStream(new byte[10], false); b.Save(ms, ImageFormat.Png); return ms.Position; });
            H(l, "Save to small fixed stream", () => { using var b = new Bitmap(8, 8); var ms = new MemoryStream(new byte[10], true); b.Save(ms, ImageFormat.Png); return ms.Position; });
            H(l, "Save to closed stream", () => { using var b = new Bitmap(8, 8); var ms = new MemoryStream(); ms.Dispose(); b.Save(ms, ImageFormat.Png); });
            H(l, "Save to missing dir", () => { using var b = new Bitmap(8, 8); b.Save(Path.Combine(TempDir, "nodir", "x.png")); });
            H(l, "Save to empty path", () => { using var b = new Bitmap(8, 8); b.Save(""); });
            H(l, "Save jpeg quality 200", () => { using var b = new Bitmap(8, 8); var p = new EncoderParameters(1); p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 200L); b.Save(new MemoryStream(), ImageCodecInfo.GetImageEncoders()[1], p); });
            H(l, "Save png bogus param", () => { using var b = new Bitmap(8, 8); var p = new EncoderParameters(1); p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 50L); var ms = new MemoryStream(); b.Save(ms, ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Png.Guid), p); return ms.Length > 0; });
            H(l, "Save tiff bad compression", () => { using var b = new Bitmap(8, 8); var p = new EncoderParameters(1); p.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Compression, 9999L); b.Save(new MemoryStream(), ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Tiff.Guid), p); });
            H(l, "Save null params", () => { using var b = new Bitmap(8, 8); var ms = new MemoryStream(); b.Save(ms, ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Png.Guid), null); return ms.Length > 0; });
            H(l, "Save EncoderParameters with null param", () => { using var b = new Bitmap(8, 8); b.Save(new MemoryStream(), ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Png.Guid), new EncoderParameters(1)); });
            H(l, "SaveAdd no first", () => { using var b = new Bitmap(8, 8); var ep = new EncoderParameters(1); ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.FrameDimensionPage); b.SaveAdd(ep); });
            H(l, "SaveAdd image no first", () => { using var b = new Bitmap(8, 8); var ep = new EncoderParameters(1); ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.FrameDimensionPage); b.SaveAdd(new Bitmap(2, 2), ep); });
            H(l, "SaveAdd png", () =>
            {
                using var b = new Bitmap(8, 8);
                var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
                b.Save(new MemoryStream(), ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Png.Guid), ep);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.FrameDimensionPage);
                b.SaveAdd(new Bitmap(8, 8), ep);
            });
            H(l, "SaveAdd tiff", () =>
            {
                using var b = new Bitmap(8, 8);
                var ms = new MemoryStream();
                var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
                b.Save(ms, ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Tiff.Guid), ep);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.FrameDimensionPage);
                b.SaveAdd(new Bitmap(8, 8), ep);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.Flush);
                b.SaveAdd(ep);
                ms.Position = 0;
                return Image.FromStream(ms).GetFrameCount(FrameDimension.Page);
            });

            H(l, "Thumbnail 0", () => { using var b = new Bitmap(8, 8); b.GetThumbnailImage(0, 4, null, IntPtr.Zero); });
            H(l, "Thumbnail 0x0", () => { using var b = new Bitmap(8, 8); return b.GetThumbnailImage(0, 0, null, IntPtr.Zero); });
            H(l, "Thumbnail neg", () => { using var b = new Bitmap(8, 8); b.GetThumbnailImage(-1, 4, null, IntPtr.Zero); });
            H(l, "Thumbnail big", () => { using var b = new Bitmap(8, 8); return b.GetThumbnailImage(200, 300, null, IntPtr.Zero); });
            H(l, "Clone bad rect", () => { using var b = new Bitmap(8, 8); b.Clone(new Rectangle(4, 4, 10, 10), PixelFormat.Format32bppArgb); });
            H(l, "Clone empty rect", () => { using var b = new Bitmap(8, 8); b.Clone(new Rectangle(0, 0, 0, 4), PixelFormat.Format32bppArgb); });
            H(l, "Clone RectangleF bad", () => { using var b = new Bitmap(8, 8); b.Clone(new RectangleF(-1, 0, 4, 4), PixelFormat.Format32bppArgb); });
            H(l, "Clone format undefined", () => { using var b = new Bitmap(8, 8); return b.Clone(new Rectangle(0, 0, 4, 4), PixelFormat.Undefined); });
            H(l, "Clone format DontCare", () => { using var b = new Bitmap(8, 8); return b.Clone(new Rectangle(0, 0, 4, 4), PixelFormat.DontCare); });
            H(l, "Clone format bogus", () => { using var b = new Bitmap(8, 8); return b.Clone(new Rectangle(0, 0, 4, 4), (PixelFormat)12345); });
            H(l, "GetHbitmap disposed", () => Disposed(new Bitmap(4, 4)).GetHbitmap());
            H(l, "GetHicon disposed", () => Disposed(new Bitmap(4, 4)).GetHicon());
            H(l, "FromHbitmap 0", () => Image.FromHbitmap(IntPtr.Zero));
            H(l, "FromHbitmap junk", () => Image.FromHbitmap(new IntPtr(0x1234)));
            H(l, "FromHicon 0", () => Bitmap.FromHicon(IntPtr.Zero));
            H(l, "FromResource 0", () => Bitmap.FromResource(IntPtr.Zero, "x"));
            H(l, "Image disposed dispose", () => { var b = new Bitmap(2, 2); b.Dispose(); b.Dispose(); });
            H(l, "Image Tag after dispose", () => { var b = new Bitmap(2, 2) { Tag = "t" }; b.Dispose(); return b.Tag; });
            H(l, "Image.GetPixelFormatSize bogus", () => Image.GetPixelFormatSize((PixelFormat)12345));
            H(l, "Image.IsAlphaPixelFormat bogus", () => Image.IsAlphaPixelFormat((PixelFormat)0x40000));

            H(l, "Graphics FromImage null", () => Graphics.FromImage(null));
            H(l, "Graphics FromImage disposed", () => Graphics.FromImage(Disposed(new Bitmap(4, 4))));
            H(l, "Graphics FromImage locked", () => { var b = new Bitmap(8, 8); b.LockBits(new Rectangle(0, 0, 8, 8), ImageLockMode.ReadOnly, b.PixelFormat); return Graphics.FromImage(b).DpiX; });
            H(l, "Graphics while locked", () => { using var b = new Bitmap(8, 8); b.LockBits(new Rectangle(0, 0, 8, 8), ImageLockMode.ReadOnly, b.PixelFormat); using var g = Graphics.FromImage(b); g.Clear(Color.Red); });
            H(l, "Graphics locked after create", () => { using var b = new Bitmap(8, 8); using var g = Graphics.FromImage(b); b.LockBits(new Rectangle(0, 0, 8, 8), ImageLockMode.ReadOnly, b.PixelFormat); g.Clear(Color.Red); });
            H(l, "Graphics locked after create fill", () => { using var b = new Bitmap(8, 8); using var g = Graphics.FromImage(b); b.LockBits(new Rectangle(0, 0, 8, 8), ImageLockMode.ReadOnly, b.PixelFormat); g.FillRectangle(Brushes.Red, 0, 0, 4, 4); });
            H(l, "Graphics locked after create transform", () => { using var b = new Bitmap(8, 8); using var g = Graphics.FromImage(b); b.LockBits(new Rectangle(0, 0, 8, 8), ImageLockMode.ReadOnly, b.PixelFormat); g.TranslateTransform(1, 1); return g.Transform.Elements; });
            H(l, "Graphics bitmap disposed then draw", () => { var b = new Bitmap(8, 8); var g = Graphics.FromImage(b); b.Dispose(); g.Clear(Color.Red); });
            H(l, "Graphics bitmap disposed then dispose", () => { var b = new Bitmap(8, 8); var g = Graphics.FromImage(b); b.Dispose(); g.Dispose(); });
            H(l, "Graphics bitmap locked then dispose bitmap", () => { var b = new Bitmap(8, 8); b.LockBits(new Rectangle(0, 0, 8, 8), ImageLockMode.ReadOnly, b.PixelFormat); b.Dispose(); });
            H(l, "Graphics FromHdc zero", () => Graphics.FromHdc(IntPtr.Zero));
            H(l, "Graphics FromHdc zero device", () => Graphics.FromHdc(IntPtr.Zero, IntPtr.Zero));
            H(l, "Graphics FromHdc junk", () => Graphics.FromHdc(new IntPtr(0x1234)));
            H(l, "GetHdc twice", () => { using var b = new Bitmap(8, 8); using var g = Graphics.FromImage(b); g.GetHdc(); g.GetHdc(); });
            H(l, "ReleaseHdc never got", () => { using var b = new Bitmap(8, 8); using var g = Graphics.FromImage(b); g.ReleaseHdc(); });
            H(l, "ReleaseHdc wrong", () => { using var b = new Bitmap(8, 8); using var g = Graphics.FromImage(b); g.GetHdc(); g.ReleaseHdc(new IntPtr(0x1234)); });
            H(l, "ReleaseHdc zero", () => { using var b = new Bitmap(8, 8); using var g = Graphics.FromImage(b); g.ReleaseHdc(IntPtr.Zero); });
            H(l, "ReleaseHdc twice", () => { using var b = new Bitmap(8, 8); using var g = Graphics.FromImage(b); IntPtr h = g.GetHdc(); g.ReleaseHdc(h); g.ReleaseHdc(h); });
            H(l, "Draw after GetHdc", () => { using var b = new Bitmap(8, 8); using var g = Graphics.FromImage(b); g.GetHdc(); g.Clear(Color.Red); });
            H(l, "Dispose after GetHdc", () => { var b = new Bitmap(8, 8); var g = Graphics.FromImage(b); g.GetHdc(); g.Dispose(); });
            H(l, "Bitmap drawn while its graphics has HDC", () => { var b = new Bitmap(8, 8); var g = Graphics.FromImage(b); g.GetHdc(); using var g2 = Graphics.FromImage(new Bitmap(8, 8)); g2.DrawImage(b, 0, 0); });
            H(l, "Bitmap GetPixel while its graphics has HDC", () => { var b = new Bitmap(8, 8); var g = Graphics.FromImage(b); g.GetHdc(); return b.GetPixel(1, 1); });
            H(l, "Second FromImage while HDC", () => { var b = new Bitmap(8, 8); var g = Graphics.FromImage(b); g.GetHdc(); return Graphics.FromImage(b).DpiX; });
            H(l, "DrawImage self", () => { var b = new Bitmap(8, 8); using var g = Graphics.FromImage(b); g.DrawImage(b, 0, 0); });
            H(l, "DrawImage locked source", () => { var b = new Bitmap(8, 8); b.LockBits(new Rectangle(0, 0, 8, 8), ImageLockMode.ReadOnly, b.PixelFormat); using var g = G(); g.DrawImage(b, 0, 0); });
            H(l, "DrawString null font", () => { using var g = G(); g.DrawString("x", null, Brushes.Black, 0, 0); });
            H(l, "DrawString null string", () => { using var g = G(); g.DrawString(null, SystemFonts.DefaultFont, Brushes.Black, 0, 0); });
            H(l, "DrawString empty", () => { using var g = G(); g.DrawString("", SystemFonts.DefaultFont, Brushes.Black, 0, 0); });
            H(l, "DrawString disposed font", () => { using var g = G(); g.DrawString("x", Disposed(new Font(FontFamily.GenericSansSerif, 8)), Brushes.Black, 0, 0); });
            H(l, "MeasureString disposed font", () => { using var g = G(); return g.MeasureString("x", Disposed(new Font(FontFamily.GenericSansSerif, 8))).Width > 0; });
            H(l, "MeasureCharacterRanges too many", () => { using var g = G(); var sf = new StringFormat(); sf.SetMeasurableCharacterRanges(Enumerable.Range(0, 33).Select(i => new CharacterRange(0, 1)).ToArray()); return g.MeasureCharacterRanges("xyz", SystemFonts.DefaultFont, new RectangleF(0, 0, 50, 50), sf).Length; });
            H(l, "MeasureCharacterRanges out of string", () => { using var g = G(); var sf = new StringFormat(); sf.SetMeasurableCharacterRanges(new[] { new CharacterRange(5, 3) }); return g.MeasureCharacterRanges("xyz", SystemFonts.DefaultFont, new RectangleF(0, 0, 50, 50), sf).Length; });
            H(l, "MeasureCharacterRanges none", () => { using var g = G(); return g.MeasureCharacterRanges("xyz", SystemFonts.DefaultFont, new RectangleF(0, 0, 50, 50), new StringFormat()).Length; });
            H(l, "MeasureCharacterRanges empty string", () => { using var g = G(); var sf = new StringFormat(); sf.SetMeasurableCharacterRanges(new[] { new CharacterRange(0, 1) }); return g.MeasureCharacterRanges("", SystemFonts.DefaultFont, new RectangleF(0, 0, 50, 50), sf).Length; });
            H(l, "Graphics Restore foreign state", () => { using var g = G(); using var g2 = G(); g.Restore(g2.Save()); });
            H(l, "Graphics Restore twice", () => { using var g = G(); var s = g.Save(); g.Restore(s); g.Restore(s); });
            H(l, "Graphics EndContainer foreign", () => { using var g = G(); using var g2 = G(); g.EndContainer(g2.BeginContainer()); });
            H(l, "Graphics EndContainer twice", () => { using var g = G(); var c = g.BeginContainer(); g.EndContainer(c); g.EndContainer(c); });
            H(l, "Graphics Transform singular", () => { using var g = G(); g.Transform = new Matrix(0, 0, 0, 0, 0, 0); });
            H(l, "Graphics MultiplyTransform singular", () => { using var g = G(); g.MultiplyTransform(new Matrix(0, 0, 0, 0, 0, 0)); });
            H(l, "Graphics ScaleTransform 0", () => { using var g = G(); g.ScaleTransform(0, 1); return g.Transform.Elements; });
            H(l, "Graphics PageUnit World", () => { using var g = G(); g.PageUnit = GraphicsUnit.World; });
            H(l, "Graphics PageUnit bad", () => { using var g = G(); g.PageUnit = (GraphicsUnit)42; });
            H(l, "Graphics PageScale 0", () => { using var g = G(); g.PageScale = 0; });
            H(l, "Graphics PageScale neg", () => { using var g = G(); g.PageScale = -1; });
            H(l, "Graphics PageScale huge", () => { using var g = G(); g.PageScale = 1e10f; });
            H(l, "Graphics SmoothingMode Invalid", () => { using var g = G(); g.SmoothingMode = SmoothingMode.Invalid; });
            H(l, "Graphics SmoothingMode bad", () => { using var g = G(); g.SmoothingMode = (SmoothingMode)42; });
            H(l, "Graphics InterpolationMode Invalid", () => { using var g = G(); g.InterpolationMode = InterpolationMode.Invalid; });
            H(l, "Graphics CompositingQuality Invalid", () => { using var g = G(); g.CompositingQuality = CompositingQuality.Invalid; });
            H(l, "Graphics PixelOffsetMode Invalid", () => { using var g = G(); g.PixelOffsetMode = PixelOffsetMode.Invalid; });
            H(l, "Graphics CompositingMode bad", () => { using var g = G(); g.CompositingMode = (CompositingMode)5; });
            H(l, "Graphics TextRenderingHint bad", () => { using var g = G(); g.TextRenderingHint = (TextRenderingHint)42; });
            H(l, "Graphics TextContrast 13", () => { using var g = G(); g.TextContrast = 13; });
            H(l, "Graphics TextContrast neg", () => { using var g = G(); g.TextContrast = -1; });
            H(l, "Graphics RenderingOrigin", () => { using var g = G(); g.RenderingOrigin = new Point(-5, 7); return g.RenderingOrigin; });
            H(l, "Graphics SetClip disposed region", () => { using var g = G(); g.SetClip(Disposed(new Region()), CombineMode.Replace); });
            H(l, "Graphics Clip set null", () => { using var g = G(); g.Clip = null; });
            H(l, "Graphics SetClip bad mode", () => { using var g = G(); g.SetClip(new Rectangle(0, 0, 4, 4), (CombineMode)9); });
            H(l, "Graphics ExcludeClip", () => { using var g = G(); g.ExcludeClip(new Rectangle(0, 0, 4, 4)); return g.ClipBounds; });
            H(l, "Graphics DrawPolygon 1 point", () => { using var g = G(); g.DrawPolygon(Pens.Black, new[] { new Point(1, 1) }); });
            H(l, "Graphics DrawPolygon 0 points", () => { using var g = G(); g.DrawPolygon(Pens.Black, new Point[0]); });
            H(l, "Graphics FillPolygon 0 points", () => { using var g = G(); g.FillPolygon(Brushes.Black, new Point[0]); });
            H(l, "Graphics FillPolygon 2 points", () => { using var g = G(); g.FillPolygon(Brushes.Black, new[] { new Point(1, 1), new Point(2, 2) }); });
            H(l, "Graphics DrawLines 1 point", () => { using var g = G(); g.DrawLines(Pens.Black, new[] { new PointF(1, 1) }); });
            H(l, "Graphics DrawLines 0 points", () => { using var g = G(); g.DrawLines(Pens.Black, new PointF[0]); });
            H(l, "Graphics DrawCurve 1 point", () => { using var g = G(); g.DrawCurve(Pens.Black, new[] { new PointF(1, 1) }); });
            H(l, "Graphics DrawCurve 2 points", () => { using var g = G(); g.DrawCurve(Pens.Black, new[] { new PointF(1, 1), new PointF(3, 3) }); });
            H(l, "Graphics DrawCurve offset too big", () => { using var g = G(); g.DrawCurve(Pens.Black, Pts3(), 2, 2, 0.5f); });
            H(l, "Graphics DrawCurve segments 0", () => { using var g = G(); g.DrawCurve(Pens.Black, Pts3(), 0, 0, 0.5f); });
            H(l, "Graphics DrawCurve neg tension", () => { using var g = G(); g.DrawCurve(Pens.Black, Pts3(), -1f); });
            H(l, "Graphics DrawClosedCurve 2 points", () => { using var g = G(); g.DrawClosedCurve(Pens.Black, new[] { new PointF(1, 1), new PointF(3, 3) }); });
            H(l, "Graphics FillClosedCurve 2 points", () => { using var g = G(); g.FillClosedCurve(Brushes.Black, new[] { new PointF(1, 1), new PointF(3, 3) }); });
            H(l, "Graphics DrawBeziers 3 points", () => { using var g = G(); g.DrawBeziers(Pens.Black, Pts3()); });
            H(l, "Graphics DrawBeziers 5 points", () => { using var g = G(); g.DrawBeziers(Pens.Black, new[] { new Point(0, 0), new Point(1, 1), new Point(2, 2), new Point(3, 3), new Point(4, 4) }); });
            H(l, "Graphics DrawBeziers 0 points", () => { using var g = G(); g.DrawBeziers(Pens.Black, new Point[0]); });
            H(l, "Graphics DrawRectangles empty", () => { using var g = G(); g.DrawRectangles(Pens.Black, new Rectangle[0]); });
            H(l, "Graphics FillRectangles empty", () => { using var g = G(); g.FillRectangles(Brushes.Black, new RectangleF[0]); });
            H(l, "Graphics DrawRectangle neg", () => { using var g = G(); g.DrawRectangle(Pens.Black, 4, 4, -2, -2); });
            H(l, "Graphics DrawArc 0 width", () => { using var g = G(); g.DrawArc(Pens.Black, 0, 0, 0, 4, 0, 90); });
            H(l, "Graphics DrawArc neg", () => { using var g = G(); g.DrawArc(Pens.Black, 0, 0, -1, 4, 0, 90); });
            H(l, "Graphics DrawPie 0 width", () => { using var g = G(); g.DrawPie(Pens.Black, 0, 0, 0, 4, 0, 90); });
            H(l, "Graphics FillPie 0 width", () => { using var g = G(); g.FillPie(Brushes.Black, 0, 0, 0, 4, 0, 90); });
            H(l, "Graphics FillPie int 0 width", () => { using var g = G(); g.FillPie(Brushes.Black, new Rectangle(0, 0, 0, 4), 0, 90); });
            H(l, "Graphics DrawEllipse 0", () => { using var g = G(); g.DrawEllipse(Pens.Black, 0, 0, 0, 0); });
            H(l, "Graphics DrawImage 0 size", () => { using var g = G(); g.DrawImage(new Bitmap(4, 4), 0, 0, 0, 0); });
            H(l, "Graphics DrawImage neg size", () => { using var g = G(); g.DrawImage(new Bitmap(4, 4), 0, 0, -4, -4); });
            H(l, "Graphics DrawImage 2 dest points", () => { using var g = G(); g.DrawImage(new Bitmap(4, 4), new[] { new Point(0, 0), new Point(2, 2) }); });
            H(l, "Graphics DrawImage 4 dest points", () => { using var g = G(); g.DrawImage(new Bitmap(4, 4), new[] { new Point(0, 0), new Point(2, 2), new Point(1, 1), new Point(3, 3) }); });
            H(l, "Graphics DrawImage src unit bad", () => { using var g = G(); g.DrawImage(new Bitmap(4, 4), new Rectangle(0, 0, 4, 4), new Rectangle(0, 0, 4, 4), (GraphicsUnit)42); });
            H(l, "Graphics DrawImage src unit World", () => { using var g = G(); g.DrawImage(new Bitmap(4, 4), new Rectangle(0, 0, 4, 4), new Rectangle(0, 0, 4, 4), GraphicsUnit.World); });
            H(l, "Graphics DrawImage src unit Display", () => { using var g = G(); g.DrawImage(new Bitmap(4, 4), new Rectangle(0, 0, 4, 4), new Rectangle(0, 0, 4, 4), GraphicsUnit.Display); });
            H(l, "Graphics DrawImage abort", () => { using var g = G(); g.DrawImage(new Bitmap(4, 4), new Rectangle(0, 0, 4, 4), 0, 0, 4, 4, GraphicsUnit.Pixel, null, _ => true); });
            H(l, "Graphics DrawImage metafile", () => { using var g = G(); g.DrawImage(Mf(), 0, 0, 8, 8); });
            H(l, "Graphics DrawImage disposed metafile", () => { using var g = G(); g.DrawImage(Disposed(Mf()), 0, 0, 8, 8); });
            H(l, "Graphics DrawIcon disposed", () => { using var g = G(); g.DrawIcon(Disposed(new Icon(SystemIcons.Application, 16, 16)), 0, 0); });
            H(l, "Graphics DrawIconUnstretched null", () => { using var g = G(); g.DrawIconUnstretched(null, new Rectangle(0, 0, 4, 4)); });
            H(l, "Graphics DrawPath empty", () => { using var g = G(); g.DrawPath(Pens.Black, new GraphicsPath()); });
            H(l, "Graphics FillRegion infinite", () => { using var g = G(); g.FillRegion(Brushes.Black, new Region()); });
            H(l, "Graphics DrawString disposed brush", () => { using var g = G(); g.DrawString("x", SystemFonts.DefaultFont, Disposed(new SolidBrush(Color.Red)), 0, 0); });
            H(l, "Graphics FillRectangle disposed brush", () => { using var g = G(); g.FillRectangle(Disposed(new SolidBrush(Color.Red)), 0, 0, 4, 4); });
            H(l, "Graphics FillRectangle disposed texture brush", () => { using var g = G(); g.FillRectangle(Disposed(new TextureBrush(new Bitmap(2, 2))), 0, 0, 4, 4); });
            H(l, "Graphics FillRectangle texture brush source disposed", () => { var b = new Bitmap(2, 2); var t = new TextureBrush(b); b.Dispose(); using var g = G(); g.FillRectangle(t, 0, 0, 4, 4); });
            H(l, "Graphics DrawLine disposed pen", () => { using var g = G(); g.DrawLine(Disposed(new Pen(Color.Red)), 0, 0, 4, 4); });
            H(l, "Graphics FillRectangle hatch", () => { using var g = G(); g.FillRectangle(new HatchBrush(HatchStyle.Cross, Color.Red), 0, 0, 4, 4); });
            H(l, "Graphics Clear on metafile graphics", () => { using var g = Graphics.FromImage(Mf()); g.Clear(Color.Red); });
            H(l, "Graphics DrawImageUnscaledAndClipped null", () => { using var g = G(); g.DrawImageUnscaledAndClipped(null, new Rectangle(0, 0, 4, 4)); });
            H(l, "Graphics IsVisible after dispose", () => { var g = G(); g.Dispose(); return g.IsVisible(1, 1); });
            H(l, "Graphics Dispose twice", () => { var g = G(); g.Dispose(); g.Dispose(); });
            H(l, "Graphics Save after dispose", () => { var g = G(); g.Dispose(); return g.Save(); });
            H(l, "Graphics FromHdc of metafile hdc", () => { using var g = G(); IntPtr h = g.GetHdc(); try { using var g2 = Graphics.FromHdc(h); return g2.DpiX; } finally { g.ReleaseHdc(h); } });
            H(l, "Graphics GetHalftonePalette", () => Graphics.GetHalftonePalette() != IntPtr.Zero);

            H(l, "Metafile second Graphics", () => { var mf = Mf(); using (Graphics.FromImage(mf)) { } });
            H(l, "Metafile two graphics open", () => { var mf = NewMf(); var g = Graphics.FromImage(mf); return Graphics.FromImage(mf).DpiX; });
            H(l, "Metafile Width while recording", () => { var mf = NewMf(); using var g = Graphics.FromImage(mf); return mf.Width; });
            H(l, "Metafile header while recording", () => { var mf = NewMf(); using var g = Graphics.FromImage(mf); return mf.GetMetafileHeader().Type; });
            H(l, "Metafile draw while recording", () => { var mf = NewMf(); using var g = Graphics.FromImage(mf); using var g2 = G(); g2.DrawImage(mf, 0, 0); });
            H(l, "Metafile save while recording", () => { var mf = NewMf(); using var g = Graphics.FromImage(mf); mf.Save(new MemoryStream(), ImageFormat.Png); });
            H(l, "Metafile save emf", () => { var mf = Mf(); var ms = new MemoryStream(); mf.Save(ms, ImageFormat.Emf); return ms.Length > 0; });
            H(l, "Metafile save png", () => { var mf = Mf(); var ms = new MemoryStream(); mf.Save(ms, ImageFormat.Png); return ms.Length > 0; });
            H(l, "Metafile clone", () => Mf().Clone());
            H(l, "Metafile garbage", () => new Metafile(new MemoryStream(new byte[] { 1, 2, 3, 4 })));
            H(l, "Metafile empty stream", () => new Metafile(new MemoryStream()));
            H(l, "Metafile ctor hdc 0", () => new Metafile(new MemoryStream(), IntPtr.Zero));
            H(l, "Metafile ctor hdc 0 rect", () => new Metafile(new MemoryStream(), IntPtr.Zero, new Rectangle(0, 0, 4, 4)));
            H(l, "Metafile ctor bad emftype", () => { using var g = G(); IntPtr h = g.GetHdc(); try { return new Metafile(new MemoryStream(), h, (EmfType)9); } finally { g.ReleaseHdc(h); } });
            H(l, "Metafile ctor bad frame unit", () => { using var g = G(); IntPtr h = g.GetHdc(); try { return new Metafile(new MemoryStream(), h, new Rectangle(0, 0, 4, 4), (MetafileFrameUnit)9); } finally { g.ReleaseHdc(h); } });
            H(l, "Metafile ctor empty frame", () => { using var g = G(); IntPtr h = g.GetHdc(); try { return new Metafile(new MemoryStream(), h, new Rectangle(0, 0, 0, 0), MetafileFrameUnit.Pixel).Width; } finally { g.ReleaseHdc(h); } });
            H(l, "Metafile ctor henh 0", () => new Metafile(IntPtr.Zero, false));
            H(l, "Metafile ctor hmf 0 wmf header", () => new Metafile(IntPtr.Zero, new WmfPlaceableFileHeader()));
            H(l, "Metafile GetMetafileHeader stream garbage", () => Metafile.GetMetafileHeader(new MemoryStream(new byte[] { 1, 2, 3 })));
            H(l, "Metafile GetMetafileHeader henh 0", () => Metafile.GetMetafileHeader(IntPtr.Zero));
            H(l, "Metafile GetMetafileHeader hmf 0", () => Metafile.GetMetafileHeader(IntPtr.Zero, new WmfPlaceableFileHeader()));
            H(l, "Metafile GetHenhmetafile twice", () => { var mf = Mf(); mf.GetHenhmetafile(); return mf.GetHenhmetafile() != IntPtr.Zero; });
            H(l, "Metafile after GetHenhmetafile width", () => { var mf = Mf(); mf.GetHenhmetafile(); return mf.Width; });
            H(l, "Metafile after GetHenhmetafile draw", () => { var mf = Mf(); mf.GetHenhmetafile(); using var g = G(); g.DrawImage(mf, 0, 0); });
            H(l, "Metafile PlayRecord bogus", () => Mf().PlayRecord(EmfPlusRecordType.EmfHeader, 0, 0, null));
            H(l, "Metafile PlayRecord not enumerating", () => Mf().PlayRecord(EmfPlusRecordType.FillRects, 0, 4, new byte[4]));
            H(l, "Metafile AddMetafileComment null", () => { var mf = NewMf(); using var g = Graphics.FromImage(mf); g.AddMetafileComment(null); });
            H(l, "Metafile AddMetafileComment bitmap graphics", () => { using var g = G(); g.AddMetafileComment(new byte[] { 1, 2, 3 }); });
            H(l, "Metafile from bad file", () => new Metafile(BadFile()));
            H(l, "Metafile from missing file", () => new Metafile(Path.Combine(TempDir, "nomf.emf")));
            H(l, "Metafile record to readonly stream", () => { using var g = G(); IntPtr h = g.GetHdc(); try { return new Metafile(new MemoryStream(new byte[4], false), h); } finally { g.ReleaseHdc(h); } });

            H(l, "Region GetHrgn null", () => new Region().GetHrgn(null));
            H(l, "Region GetHrgn infinite", () => { using var g = G(); return new Region().GetHrgn(g); });
            H(l, "Region GetHrgn empty", () => { using var g = G(); var r = new Region(); r.MakeEmpty(); return r.GetHrgn(g); });
            H(l, "Region FromHrgn 0", () => Region.FromHrgn(IntPtr.Zero));
            H(l, "Region FromHrgn junk", () => Region.FromHrgn(new IntPtr(0x1234)));
            H(l, "Region from empty path", () => new Region(new GraphicsPath()).GetBounds(G()));
            H(l, "Region from data null", () => new Region((RegionData)null));
            H(l, "Region data garbage", () => { var d = new Region().GetRegionData(); d.Data = new byte[] { 1, 2, 3 }; return new Region(d); });
            H(l, "Region data empty", () => { var d = new Region().GetRegionData(); d.Data = new byte[0]; return new Region(d); });
            H(l, "Region data null bytes", () => { var d = new Region().GetRegionData(); d.Data = null; return new Region(d); });
            H(l, "Region data truncated", () => { var d = new Region(new Rectangle(1, 2, 3, 4)).GetRegionData(); d.Data = d.Data.Take(d.Data.Length - 4).ToArray(); return new Region(d); });
            H(l, "Region data roundtrip", () => { var d = new Region(new Rectangle(1, 2, 3, 4)).GetRegionData(); return new Region(d).GetBounds(G()); });
            H(l, "Region Union self", () => { var r = new Region(new Rectangle(0, 0, 4, 4)); r.Union(r); return r.GetBounds(G()); });
            H(l, "Region Union disposed", () => { var r = new Region(); r.Union(Disposed(new Region())); });
            H(l, "Region Equals disposed", () => new Region().Equals(Disposed(new Region()), G()));
            H(l, "Region IsVisible null graphics", () => new Region().IsVisible(1, 1, null));
            H(l, "Region GetRegionScans null", () => new Region().GetRegionScans(null));
            H(l, "Region GetRegionScans infinite", () => new Region().GetRegionScans(new Matrix()));
            H(l, "Region Transform singular", () => { var r = new Region(new Rectangle(0, 0, 4, 4)); r.Transform(new Matrix(0, 0, 0, 0, 0, 0)); return r.GetBounds(G()); });
            H(l, "Region Translate NaN", () => { var r = new Region(new Rectangle(0, 0, 4, 4)); r.Translate(float.NaN, 0); return r.GetBounds(G()); });

            H(l, "Path AddString null family", () => new GraphicsPath().AddString("x", null, 0, 10, new Point(0, 0), null));
            H(l, "Path AddString null string", () => new GraphicsPath().AddString(null, FontFamily.GenericSansSerif, 0, 10, new Point(0, 0), null));
            H(l, "Path AddString disposed family", () => new GraphicsPath().AddString("x", Disposed(new FontFamily(GenericFontFamilies.Serif)), 0, 10, new Point(0, 0), null));
            H(l, "Path AddString bad style", () => new GraphicsPath().AddString("x", FontFamily.GenericSansSerif, 64, 10, new Point(0, 0), null));
            H(l, "Path AddString size 0", () => { var p = new GraphicsPath(); p.AddString("x", FontFamily.GenericSansSerif, 0, 0, new Point(0, 0), null); return p.PointCount; });
            H(l, "Path AddString size neg", () => { var p = new GraphicsPath(); p.AddString("x", FontFamily.GenericSansSerif, 0, -10, new Point(0, 0), null); return p.PointCount > 0; });
            H(l, "Path close empty", () => { var p = new GraphicsPath(); p.CloseFigure(); return p.PointCount; });
            H(l, "Path widen empty", () => { var p = new GraphicsPath(); p.Widen(Pens.Black); return p.PointCount; });
            H(l, "Path flatten empty", () => { var p = new GraphicsPath(); p.Flatten(); return p.PointCount; });
            H(l, "Path flatten count", () => { var p = Path1(); p.Flatten(null, 0.25f); return p.PointCount; });
            H(l, "Path warp 3 points", () => { var p = Path1(); p.Warp(new[] { new PointF(0, 0), new PointF(10, 0), new PointF(0, 10) }, new RectangleF(0, 0, 8, 6)); return p.PathPoints; });
            H(l, "Path warp 4 points", () => { var p = Path1(); p.Warp(new[] { new PointF(0, 0), new PointF(10, 0), new PointF(0, 10), new PointF(12, 14) }, new RectangleF(0, 0, 8, 6)); return p.PathPoints; });
            H(l, "Path warp 4 points bilinear", () => { var p = Path1(); p.Warp(new[] { new PointF(0, 0), new PointF(10, 0), new PointF(0, 10), new PointF(12, 14) }, new RectangleF(0, 0, 8, 6), null, WarpMode.Bilinear); return p.PathPoints; });
            H(l, "Path warp line 4 points", () => { var p = new GraphicsPath(); p.AddLine(0, 0, 8, 6); p.Warp(new[] { new PointF(0, 0), new PointF(10, 0), new PointF(0, 10), new PointF(12, 14) }, new RectangleF(0, 0, 8, 6)); return p.PathPoints; });
            H(l, "Path warp empty", () => { var p = new GraphicsPath(); p.Warp(Pts3(), new RectangleF(0, 0, 4, 4)); return p.PointCount; });
            H(l, "Path warp 2 points", () => { var p = Path1(); p.Warp(new[] { new PointF(0, 0), new PointF(1, 1) }, new RectangleF(0, 0, 4, 4)); return p.PointCount; });
            H(l, "Path warp 5 points", () => { var p = Path1(); p.Warp(new[] { new PointF(0, 0), new PointF(1, 1), new PointF(2, 2), new PointF(3, 3), new PointF(4, 4) }, new RectangleF(0, 0, 4, 4)); return p.PointCount; });
            H(l, "Path warp empty rect", () => { var p = Path1(); p.Warp(Pts3(), new RectangleF(0, 0, 0, 0)); return p.PointCount; });
            H(l, "Path warp bad mode", () => { var p = Path1(); p.Warp(Pts3(), new RectangleF(0, 0, 4, 4), null, (WarpMode)5); return p.PointCount; });
            H(l, "Path PathPoints empty", () => new GraphicsPath().PathPoints);
            H(l, "Path PathTypes empty", () => new GraphicsPath().PathTypes);
            H(l, "Path PathData empty", () => new GraphicsPath().PathData.Points);
            H(l, "Path LastPoint empty", () => new GraphicsPath().GetLastPoint());
            H(l, "Path bounds empty", () => new GraphicsPath().GetBounds());
            H(l, "Path ctor mismatched", () => new GraphicsPath(Pts3(), new byte[] { 0, 1 }));
            H(l, "Path ctor bad types", () => new GraphicsPath(Pts3(), new byte[] { 9, 9, 9 }).PointCount);
            H(l, "Path ctor empty", () => new GraphicsPath(new PointF[0], new byte[0]).PointCount);
            H(l, "Path ctor bad fill", () => new GraphicsPath((FillMode)5).FillMode);
            H(l, "Path FillMode bad", () => { var p = new GraphicsPath(); p.FillMode = (FillMode)5; });
            H(l, "Path AddLines empty", () => new GraphicsPath().AddLines(new PointF[0]));
            H(l, "Path AddPolygon 2", () => new GraphicsPath().AddPolygon(new[] { new PointF(0, 0), new PointF(1, 1) }));
            H(l, "Path AddPolygon 0", () => new GraphicsPath().AddPolygon(new PointF[0]));
            H(l, "Path AddCurve 1", () => new GraphicsPath().AddCurve(new[] { new PointF(0, 0) }));
            H(l, "Path AddCurve offset", () => new GraphicsPath().AddCurve(Pts3(), 5, 1, 0.5f));
            H(l, "Path AddClosedCurve 2", () => new GraphicsPath().AddClosedCurve(new[] { new PointF(0, 0), new PointF(1, 1) }));
            H(l, "Path AddBeziers 3", () => new GraphicsPath().AddBeziers(Pts3()));
            H(l, "Path AddBeziers 0", () => new GraphicsPath().AddBeziers(new PointF[0]));
            H(l, "Path AddRectangles empty", () => new GraphicsPath().AddRectangles(new RectangleF[0]));
            H(l, "Path AddRectangle empty", () => { var p = new GraphicsPath(); p.AddRectangle(new RectangleF(0, 0, 0, 4)); return p.PointCount; });
            H(l, "Path AddEllipse empty", () => { var p = new GraphicsPath(); p.AddEllipse(0, 0, 0, 4); return p.PointCount; });
            H(l, "Path AddArc empty", () => { var p = new GraphicsPath(); p.AddArc(0, 0, 0, 4, 0, 90); return p.PointCount; });
            H(l, "Path AddPie empty", () => { var p = new GraphicsPath(); p.AddPie(0, 0, 0, 4, 0, 90); return p.PointCount; });
            H(l, "Path AddPath self", () => { var p = Path1(); p.AddPath(p, true); return p.PointCount; });
            H(l, "Path AddPath disposed", () => { var p = Path1(); p.AddPath(Disposed(Path1()), true); });
            H(l, "Path IsVisible null graphics", () => Path1().IsVisible(2, 2, null));
            H(l, "Path IsOutlineVisible null pen", () => Path1().IsOutlineVisible(2, 2, null));
            H(l, "Path IsOutlineVisible disposed pen", () => Path1().IsOutlineVisible(2, 2, Disposed(new Pen(Color.Red))));
            H(l, "Path Widen disposed pen", () => Path1().Widen(Disposed(new Pen(Color.Red))));
            H(l, "Path Widen null", () => Path1().Widen(null));
            H(l, "Path Flatten singular", () => { var p = Path1(); p.Flatten(new Matrix(0, 0, 0, 0, 0, 0)); return p.PointCount; });
            H(l, "Path Transform singular", () => { var p = Path1(); p.Transform(new Matrix(0, 0, 0, 0, 0, 0)); return p.PointCount; });
            H(l, "Path Transform null", () => Path1().Transform(null));
            H(l, "Path Flatten null", () => { var p = Path1(); p.Flatten(null); return p.PointCount; });
            H(l, "Path GetBounds null matrix", () => Path1().GetBounds(null));
            H(l, "Path GetBounds null pen", () => Path1().GetBounds(new Matrix(), null));
            H(l, "Path SetMarkers empty", () => new GraphicsPath().SetMarkers());
            H(l, "Path Reverse empty", () => new GraphicsPath().Reverse());
            H(l, "PathIterator null path", () => new GraphicsPathIterator(null).Count);
            H(l, "PathIterator Enumerate short", () => { var it = new GraphicsPathIterator(Path1()); PointF[] p = new PointF[2]; byte[] t = new byte[2]; return it.Enumerate(ref p, ref t); });
            H(l, "PathIterator Enumerate mismatched", () => { var it = new GraphicsPathIterator(Path1()); PointF[] p = new PointF[20]; byte[] t = new byte[2]; return it.Enumerate(ref p, ref t); });
            H(l, "PathIterator CopyData bad range", () => { var it = new GraphicsPathIterator(Path1()); PointF[] p = new PointF[20]; byte[] t = new byte[20]; return it.CopyData(ref p, ref t, 5, 100); });
            H(l, "PathIterator CopyData neg", () => { var it = new GraphicsPathIterator(Path1()); PointF[] p = new PointF[20]; byte[] t = new byte[20]; return it.CopyData(ref p, ref t, -1, 2); });
            H(l, "PathIterator CopyData short", () => { var it = new GraphicsPathIterator(Path1()); PointF[] p = new PointF[1]; byte[] t = new byte[1]; return it.CopyData(ref p, ref t, 0, 5); });
            H(l, "PathIterator NextSubpath null path", () => new GraphicsPathIterator(Path1()).NextSubpath(null, out bool _));
            H(l, "PathIterator NextMarker null path", () => new GraphicsPathIterator(Path1()).NextMarker(null));
            H(l, "PathIterator NextPathType", () => { var it = new GraphicsPathIterator(Path1()); it.NextSubpath(out int a, out int b, out bool c); int n = it.NextPathType(out byte t, out int s, out int e); return n + " " + t + " " + s + " " + e; });
            H(l, "PathIterator empty path counts", () => { var it = new GraphicsPathIterator(new GraphicsPath()); return it.Count + " " + it.SubpathCount + " " + it.HasCurve(); });

            H(l, "Icon garbage", () => new Icon(new MemoryStream(new byte[] { 1, 2, 3, 4 })));
            H(l, "Icon empty stream", () => new Icon(new MemoryStream()));
            H(l, "Icon header only", () => new Icon(new MemoryStream(new byte[] { 0, 0, 1, 0, 0, 0 })));
            H(l, "Icon header type 2", () => new Icon(new MemoryStream(new byte[] { 0, 0, 2, 0, 1, 0, 16, 16, 0, 0, 1, 0, 32, 0, 0, 0, 0, 0, 22, 0, 0, 0 })));
            H(l, "Icon bad entry offset", () => new Icon(new MemoryStream(new byte[] { 0, 0, 1, 0, 1, 0, 16, 16, 0, 0, 1, 0, 32, 0, 40, 0, 0, 0, 0xff, 0xff, 0, 0 })));
            H(l, "Icon from bad file", () => new Icon(BadFile()));
            H(l, "Icon from missing file", () => new Icon(Path.Combine(TempDir, "noicon.ico")));
            H(l, "Icon size 0", () => new Icon(SystemIcons.Application, 0, 0).Size);
            H(l, "Icon size neg", () => new Icon(SystemIcons.Application, -1, -1).Size);
            H(l, "Icon size big", () => new Icon(SystemIcons.Application, 1000, 1000).Size);
            H(l, "Icon(Type, missing)", () => new Icon(typeof(Bitmap), "nope.ico"));
            H(l, "Icon FromHandle 0", () => Icon.FromHandle(IntPtr.Zero));
            H(l, "Icon FromHandle junk", () => Icon.FromHandle(new IntPtr(0x1234)).Size);
            H(l, "Icon disposed ToBitmap", () => Disposed(new Icon(SystemIcons.Application, 16, 16)).ToBitmap());
            H(l, "Icon disposed Handle", () => Disposed(new Icon(SystemIcons.Application, 16, 16)).Handle);
            H(l, "Icon disposed Size", () => Disposed(new Icon(SystemIcons.Application, 16, 16)).Size);
            H(l, "Icon disposed Save", () => Disposed(new Icon(SystemIcons.Application, 16, 16)).Save(new MemoryStream()));
            H(l, "Icon disposed Clone", () => Disposed(new Icon(SystemIcons.Application, 16, 16)).Clone());
            H(l, "Icon Save null", () => new Icon(SystemIcons.Application, 16, 16).Save(null));
            H(l, "Icon Save readonly", () => new Icon(SystemIcons.Application, 16, 16).Save(new MemoryStream(new byte[4], false)));
            H(l, "SystemIcons dispose", () => { SystemIcons.Hand.Dispose(); return SystemIcons.Hand.Size; });

            H(l, "ColorMatrix short", () => new ColorMatrix(new float[][] { new float[] { 1 } }));
            H(l, "ColorMatrix null", () => new ColorMatrix((float[][])null));
            H(l, "ColorMatrix 6x6", () => new ColorMatrix(Enumerable.Range(0, 6).Select(_ => new float[6]).ToArray()).Matrix00);
            H(l, "ColorMatrix jagged row null", () => new ColorMatrix(new float[][] { null, null, null, null, null }));
            H(l, "ColorMatrix item out", () => new ColorMatrix()[5, 5]);
            H(l, "ColorMatrix item neg", () => new ColorMatrix()[-1, 0]);
            H(l, "ColorMatrix set item out", () => { var m = new ColorMatrix(); m[5, 0] = 1; });
            H(l, "ImageAttributes ColorMatrix null", () => new ImageAttributes().SetColorMatrix(null));
            H(l, "ImageAttributes bad type", () => new ImageAttributes().SetColorMatrix(new ColorMatrix(), ColorMatrixFlag.Default, (ColorAdjustType)9));
            H(l, "ImageAttributes type Any", () => new ImageAttributes().SetColorMatrix(new ColorMatrix(), ColorMatrixFlag.Default, ColorAdjustType.Any));
            H(l, "ImageAttributes type Count", () => new ImageAttributes().SetColorMatrix(new ColorMatrix(), ColorMatrixFlag.Default, ColorAdjustType.Count));
            H(l, "ImageAttributes bad flag", () => new ImageAttributes().SetColorMatrix(new ColorMatrix(), (ColorMatrixFlag)9));
            H(l, "ImageAttributes AltGrays no gray", () => new ImageAttributes().SetColorMatrices(new ColorMatrix(), null, ColorMatrixFlag.AltGrays));
            H(l, "ImageAttributes SkipGrays", () => new ImageAttributes().SetColorMatrices(new ColorMatrix(), null, ColorMatrixFlag.SkipGrays));
            H(l, "ImageAttributes gamma 0", () => new ImageAttributes().SetGamma(0));
            H(l, "ImageAttributes gamma neg", () => new ImageAttributes().SetGamma(-1));
            H(l, "ImageAttributes gamma 100", () => new ImageAttributes().SetGamma(100));
            H(l, "ImageAttributes threshold 2", () => new ImageAttributes().SetThreshold(2));
            H(l, "ImageAttributes threshold neg", () => new ImageAttributes().SetThreshold(-1));
            H(l, "ImageAttributes remap empty", () => new ImageAttributes().SetRemapTable(new ColorMap[0]));
            H(l, "ImageAttributes remap null", () => new ImageAttributes().SetRemapTable((ColorMap[])null));
            H(l, "ImageAttributes remap null entry", () => new ImageAttributes().SetRemapTable(new ColorMap[1]));
            H(l, "ImageAttributes brush remap empty", () => new ImageAttributes().SetBrushRemapTable(new ColorMap[0]));
            H(l, "ImageAttributes output channel bad", () => new ImageAttributes().SetOutputChannel((ColorChannelFlag)9));
            H(l, "ImageAttributes output channel Last", () => new ImageAttributes().SetOutputChannel(ColorChannelFlag.ColorChannelLast));
            H(l, "ImageAttributes output profile missing", () => new ImageAttributes().SetOutputChannelColorProfile(Path.Combine(TempDir, "noprofile.icm")));
            H(l, "ImageAttributes output profile garbage", () => new ImageAttributes().SetOutputChannelColorProfile(BadFile()));
            H(l, "ImageAttributes output profile null", () => new ImageAttributes().SetOutputChannelColorProfile(null));
            H(l, "ImageAttributes output profile empty", () => new ImageAttributes().SetOutputChannelColorProfile(""));
            H(l, "ImageAttributes wrap bad", () => new ImageAttributes().SetWrapMode((WrapMode)9));
            H(l, "ImageAttributes GetAdjustedPalette null", () => new ImageAttributes().GetAdjustedPalette(null, ColorAdjustType.Bitmap));
            H(l, "ImageAttributes GetAdjustedPalette Default", () => { var p = Palette(); new ImageAttributes().GetAdjustedPalette(p, ColorAdjustType.Default); return p.Entries.Length; });
            H(l, "ImageAttributes GetAdjustedPalette Any", () => { var p = Palette(); new ImageAttributes().GetAdjustedPalette(p, ColorAdjustType.Any); return p.Entries.Length; });
            H(l, "ImageAttributes disposed SetGamma", () => Disposed(new ImageAttributes()).SetGamma(1));
            H(l, "ImageAttributes disposed Clone", () => Disposed(new ImageAttributes()).Clone());
            H(l, "ImageAttributes ColorKey reversed", () => new ImageAttributes().SetColorKey(Color.White, Color.Black));

            H(l, "StringFormat bad tab", () => { var f = new StringFormat(); f.SetTabStops(0, null); });
            H(l, "StringFormat tabs neg", () => { var f = new StringFormat(); f.SetTabStops(0, new[] { -1f }); });
            H(l, "StringFormat tabs neg first", () => { var f = new StringFormat(); f.SetTabStops(-1, new[] { 1f }); });
            H(l, "StringFormat tabs get", () => { var f = new StringFormat(); f.SetTabStops(2, new[] { 3f, 4f }); return f.GetTabStops(out float first)[1] + first; });
            H(l, "StringFormat tabs empty get", () => new StringFormat().GetTabStops(out float _));
            H(l, "StringFormat ranges 33", () => new StringFormat().SetMeasurableCharacterRanges(Enumerable.Range(0, 33).Select(i => new CharacterRange(0, 1)).ToArray()));
            H(l, "StringFormat ranges 32", () => new StringFormat().SetMeasurableCharacterRanges(Enumerable.Range(0, 32).Select(i => new CharacterRange(0, 1)).ToArray()));
            H(l, "StringFormat ranges null", () => new StringFormat().SetMeasurableCharacterRanges(null));
            H(l, "StringFormat Alignment bad", () => new StringFormat { Alignment = (StringAlignment)9 });
            H(l, "StringFormat LineAlignment bad", () => new StringFormat { LineAlignment = (StringAlignment)9 });
            H(l, "StringFormat Trimming bad", () => new StringFormat { Trimming = (StringTrimming)9 });
            H(l, "StringFormat HotkeyPrefix bad", () => new StringFormat { HotkeyPrefix = (HotkeyPrefix)9 });
            H(l, "StringFormat Flags bad", () => new StringFormat { FormatFlags = (StringFormatFlags)0x7fffffff }.FormatFlags);
            H(l, "StringFormat DigitSubstitution bad", () => { var f = new StringFormat(); f.SetDigitSubstitution(0, (StringDigitSubstitute)9); return f.DigitSubstitutionMethod; });
            H(l, "StringFormat ctor null", () => new StringFormat((StringFormat)null));
            H(l, "StringFormat generic dispose", () => { StringFormat.GenericDefault.Dispose(); return StringFormat.GenericDefault.Alignment; });
            // GDI+'s generic formats are process-wide: put it back.
            H(l, "StringFormat generic modify", () =>
            {
                StringFormat.GenericTypographic.Alignment = StringAlignment.Center;
                StringAlignment seen = StringFormat.GenericTypographic.Alignment;
                StringFormat.GenericTypographic.Alignment = StringAlignment.Near;
                return seen;
            });
            H(l, "StringFormat disposed ToString", () => Disposed(new StringFormat()).ToString());

            H(l, "CustomLineCap null both", () => new CustomLineCap(null, null));
            H(l, "CustomLineCap both", () => new CustomLineCap(Path1(), Path1()).BaseInset);
            H(l, "CustomLineCap fill open path", () => { var p = new GraphicsPath(); p.AddLine(0, 0, 1, 1); return new CustomLineCap(p, null).BaseInset; });
            H(l, "CustomLineCap fill not crossing", () => { var p = new GraphicsPath(); p.AddRectangle(new Rectangle(1, 1, 2, 2)); return new CustomLineCap(p, null).BaseInset; });
            H(l, "CustomLineCap stroke empty path", () => new CustomLineCap(null, new GraphicsPath()).BaseInset);
            H(l, "CustomLineCap bad basecap", () => new CustomLineCap(null, Path1(), (LineCap)5).BaseCap);
            H(l, "CustomLineCap basecap custom", () => new CustomLineCap(null, Path1(), LineCap.Custom).BaseCap);
            H(l, "CustomLineCap basecap anchor", () => new CustomLineCap(null, Path1(), LineCap.ArrowAnchor).BaseCap);
            H(l, "CustomLineCap set basecap custom", () => new CustomLineCap(null, Path1()) { BaseCap = LineCap.Custom }.BaseCap);
            H(l, "CustomLineCap set basecap anchor", () => new CustomLineCap(null, Path1()) { BaseCap = LineCap.DiamondAnchor }.BaseCap);
            H(l, "CustomLineCap SetStrokeCaps bad", () => new CustomLineCap(null, Path1()).SetStrokeCaps((LineCap)5, LineCap.Flat));
            H(l, "CustomLineCap SetStrokeCaps anchor", () => new CustomLineCap(null, Path1()).SetStrokeCaps(LineCap.RoundAnchor, LineCap.Flat));
            H(l, "CustomLineCap StrokeJoin bad", () => new CustomLineCap(null, Path1()) { StrokeJoin = (LineJoin)9 }.StrokeJoin);
            H(l, "AdjustableArrowCap 0", () => new AdjustableArrowCap(0, 0).Width);
            H(l, "AdjustableArrowCap neg", () => new AdjustableArrowCap(-1, -1).Width);
            H(l, "AdjustableArrowCap set neg", () => new AdjustableArrowCap(3, 3) { Height = -2 }.Height);
            H(l, "AdjustableArrowCap middle big", () => new AdjustableArrowCap(3, 3) { MiddleInset = 10 }.MiddleInset);

            H(l, "EncoderParameter type mismatch", () => new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 1, EncoderParameterValueType.ValueTypeLong, IntPtr.Zero));
            H(l, "EncoderParameter bad type", () => new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 1, (EncoderParameterValueType)99, new IntPtr(8)));
            H(l, "EncoderParameter rational mismatched", () => new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, new[] { 1, 2 }, new[] { 1 }));
            H(l, "EncoderParameter long range mismatched", () => new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, new long[] { 1, 2 }, new long[] { 1 }));
            H(l, "EncoderParameter rational range mismatched", () => new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, new[] { 1 }, new[] { 1 }, new[] { 1 }, new int[0]));
            H(l, "EncoderParameter ctor obsolete", () => new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 1, 1, 1).NumberOfValues);
            H(l, "EncoderParameters 0", () => new EncoderParameters(0).Param.Length);
            H(l, "EncoderParameters neg", () => new EncoderParameters(-1));
            H(l, "EncoderParameter disposed", () => { var p = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 1L); p.Dispose(); return p.NumberOfValues; });
            H(l, "EncoderParameter disposed type", () => { var p = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 1L); p.Dispose(); return p.Type; });

            H(l, "Blend 0", () => new Blend(0).Factors.Length);
            H(l, "Blend neg", () => new Blend(-1));
            H(l, "ColorBlend neg", () => new ColorBlend(-1));

            H(l, "ImageFormat ToString bogus", () => new ImageFormat(Guid.Empty).ToString());
            H(l, "ImageFormat ToString png", () => ImageFormat.Png.ToString());
            H(l, "FrameDimension ToString bogus", () => new FrameDimension(Guid.Empty).ToString());
            H(l, "ImageCodecInfo decoders", () => ImageCodecInfo.GetImageDecoders().Select(c => c.FormatDescription).ToArray());
            H(l, "ImageCodecInfo encoders", () => ImageCodecInfo.GetImageEncoders().Select(c => c.MimeType).ToArray());

            H(l, "ImageAnimator CanAnimate null", () => ImageAnimator.CanAnimate(null));
            H(l, "ImageAnimator CanAnimate disposed", () => ImageAnimator.CanAnimate(Disposed(new Bitmap(2, 2))));
            H(l, "ImageAnimator UpdateFrames null", () => ImageAnimator.UpdateFrames(null));
            H(l, "ImageAnimator StopAnimate null", () => ImageAnimator.StopAnimate(null, null));

            H(l, "Margins neg", () => new Margins(-1, 0, 0, 0));
            H(l, "Margins set neg", () => { var m = new Margins(); m.Left = -1; });
            H(l, "PaperSize set kind custom", () => { var p = new PaperSize("x", 1, 2); p.Width = 5; return p.Width; });
            H(l, "PaperSize ctor null name", () => new PaperSize(null, 1, 2).PaperName);
            H(l, "PrinterResolution kind bad", () => new PrinterResolution { Kind = (PrinterResolutionKind)9 });
            H(l, "PrinterUnitConvert bad unit", () => PrinterUnitConvert.Convert(1, (PrinterUnit)9, PrinterUnit.Display));

            H(l, "BufferedGraphicsContext MaximumBuffer 0", () => new BufferedGraphicsContext { MaximumBuffer = new Size(0, 5) });
            H(l, "BufferedGraphicsContext MaximumBuffer neg", () => new BufferedGraphicsContext { MaximumBuffer = new Size(-1, 5) });
            H(l, "BufferedGraphicsContext Allocate null", () => new BufferedGraphicsContext().Allocate((Graphics)null, new Rectangle(0, 0, 4, 4)));
            H(l, "BufferedGraphicsContext Allocate empty", () => new BufferedGraphicsContext().Allocate(G(), new Rectangle(0, 0, 0, 0)));
            H(l, "BufferedGraphicsContext Allocate big", () => new BufferedGraphicsContext().Allocate(G(), new Rectangle(0, 0, 300, 300)).Graphics.DpiX);
            H(l, "BufferedGraphicsContext Allocate hdc 0", () => new BufferedGraphicsContext().Allocate(IntPtr.Zero, new Rectangle(0, 0, 4, 4)));
            H(l, "BufferedGraphics Render null", () => new BufferedGraphicsContext().Allocate(G(), new Rectangle(0, 0, 4, 4)).Render((Graphics)null));
            H(l, "BufferedGraphics disposed Graphics", () => Disposed(new BufferedGraphicsContext().Allocate(G(), new Rectangle(0, 0, 4, 4))).Graphics);
            H(l, "BufferedGraphics disposed Render", () => Disposed(new BufferedGraphicsContext().Allocate(G(), new Rectangle(0, 0, 4, 4))).Render());
            H(l, "BufferedGraphics target disposed Render", () => { var g = G(); var b = new BufferedGraphicsContext().Allocate(g, new Rectangle(0, 0, 4, 4)); g.Dispose(); b.Render(); });

            H(l, "ImageConverter from garbage", () => new ImageConverter().ConvertFrom(new byte[] { 1, 2, 3 }));
            H(l, "ImageConverter from empty", () => new ImageConverter().ConvertFrom(new byte[0]));
            H(l, "ImageConverter from string", () => new ImageConverter().ConvertFrom("x"));
            H(l, "ImageConverter from icon", () => new ImageConverter().ConvertFrom(new Icon(SystemIcons.Application, 16, 16)));
            H(l, "ImageConverter to bytes", () => ((byte[])new ImageConverter().ConvertTo(new Bitmap(2, 2), typeof(byte[]))).Length > 0);
            H(l, "ImageConverter null to bytes", () => new ImageConverter().ConvertTo(null, typeof(byte[])));
            H(l, "ImageConverter to int", () => new ImageConverter().ConvertTo(new Bitmap(2, 2), typeof(int)));
            H(l, "IconConverter from garbage", () => new IconConverter().ConvertFrom(new byte[] { 1, 2, 3 }));
            H(l, "IconConverter to bitmap", () => new IconConverter().ConvertTo(new Icon(SystemIcons.Application, 16, 16), typeof(Bitmap)));
            H(l, "IconConverter to bytes", () => ((byte[])new IconConverter().ConvertTo(new Icon(SystemIcons.Application, 16, 16), typeof(byte[]))).Length > 0);
            H(l, "MarginsConverter from string", () => new MarginsConverter().ConvertFrom(null, CultureInfo.InvariantCulture, "1, 2, 3, 4"));
            H(l, "MarginsConverter from 3", () => new MarginsConverter().ConvertFrom(null, CultureInfo.InvariantCulture, "1, 2, 3"));
            H(l, "MarginsConverter from bad", () => new MarginsConverter().ConvertFrom(null, CultureInfo.InvariantCulture, "a, b, c, d"));
            H(l, "MarginsConverter from blank", () => new MarginsConverter().ConvertFrom(null, CultureInfo.InvariantCulture, "  "));
            H(l, "MarginsConverter to string", () => new MarginsConverter().ConvertTo(null, CultureInfo.InvariantCulture, new Margins(1, 2, 3, 4), typeof(string)));
            H(l, "MarginsConverter create bad", () => new MarginsConverter().CreateInstance(null, new System.Collections.Hashtable { ["Left"] = "x" }));
            H(l, "FontConverter from bad size", () => new FontConverter().ConvertFrom(null, CultureInfo.InvariantCulture, "Arial, abc"));
            H(l, "FontConverter from bad unit", () => new FontConverter().ConvertFrom(null, CultureInfo.InvariantCulture, "Arial, 10zz"));
            H(l, "FontConverter from bad style", () => new FontConverter().ConvertFrom(null, CultureInfo.InvariantCulture, "Arial, 10pt, style=Nope"));
            H(l, "FontConverter from empty", () => new FontConverter().ConvertFrom(null, CultureInfo.InvariantCulture, ""));
            H(l, "FontConverter from size", () => ((Font)new FontConverter().ConvertFrom(null, CultureInfo.InvariantCulture, "Arial, 12pt, style=Bold")).Size);
            H(l, "FontConverter to string", () => new FontConverter().ConvertTo(null, CultureInfo.InvariantCulture, new Font(FontFamily.GenericSansSerif, 9), typeof(string)), true);
            H(l, "FontConverter create empty", () => new FontConverter().CreateInstance(null, new System.Collections.Hashtable()), true);
            H(l, "ImageFormatConverter from bad", () => new ImageFormatConverter().ConvertFrom("NoSuchFormat"));
            H(l, "ImageFormatConverter from png", () => new ImageFormatConverter().ConvertFrom("Png").ToString());
            H(l, "ImageFormatConverter to string", () => new ImageFormatConverter().ConvertTo(ImageFormat.Gif, typeof(string)));
            H(l, "ToolboxBitmapAttribute missing", () => new ToolboxBitmapAttribute(Path.Combine(TempDir, "none.bmp")).GetImage(typeof(Bitmap)));
            H(l, "ToolboxBitmapAttribute GetImage null", () => new ToolboxBitmapAttribute(typeof(Bitmap)).GetImage((Type)null));
            H(l, "ToolboxBitmapAttribute GetImageFromResource null", () => ToolboxBitmapAttribute.GetImageFromResource(null, "x", false));
        }

        private static Metafile NewMf()
        {
            using var b = new Bitmap(4, 4);
            using var rg = Graphics.FromImage(b);
            IntPtr hdc = rg.GetHdc();
            try
            {
                return new Metafile(new MemoryStream(), hdc, new Rectangle(0, 0, 20, 20), MetafileFrameUnit.Pixel, EmfType.EmfPlusDual);
            }
            finally
            {
                rg.ReleaseHdc(hdc);
            }
        }
    }
}
