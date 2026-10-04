// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The recording scenarios the managed metafile recorder is held to, SHARED between the test and the
// oracle that produced the expected bytes: oracle/mfo.cs compiles this very file against .NET
// Framework's System.Drawing (real GDI+), driving a Graphics recording into a Metafile, and the test
// compiles it against the fork, driving GpMetafileRecorder directly with the same calls. It is C# 5
// on purpose: that is what the Framework's csc.exe accepts.
//
// IRec is the recorder's API: one member per call a Graphics recording into a metafile makes, with
// the arguments as the caller handed them to Graphics.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace MetafileOracle
{
    public interface IRec
    {
        void Clear(Color c);
        void FillRects(Brush b, RectangleF[] r);
        void DrawRects(Pen p, RectangleF[] r);
        void FillPolygon(Brush b, PointF[] pts, FillMode mode);
        void DrawLines(Pen p, PointF[] pts, bool closed);
        void FillEllipse(Brush b, RectangleF r);
        void DrawEllipse(Pen p, RectangleF r);
        void FillPie(Brush b, RectangleF r, float start, float sweep);
        void DrawPie(Pen p, RectangleF r, float start, float sweep);
        void DrawArc(Pen p, RectangleF r, float start, float sweep);
        void FillPath(Brush b, GraphicsPath path);
        void DrawPath(Pen p, GraphicsPath path);
        void FillClosedCurve(Brush b, PointF[] pts, float tension, FillMode mode);
        void DrawClosedCurve(Pen p, PointF[] pts, float tension);
        void DrawCurve(Pen p, PointF[] pts, int offset, int segments, float tension);
        void DrawBeziers(Pen p, PointF[] pts);
        void FillRegion(Brush b, Region r);
        void DrawImage(Image img, RectangleF dest, RectangleF src, GraphicsUnit unit, ImageAttributes ia);
        void DrawImagePoints(Image img, PointF[] dest, RectangleF src, GraphicsUnit unit, ImageAttributes ia);
        void DrawString(string s, Font f, RectangleF layout, StringFormat fmt, Brush b);
        void DrawDriverString(ushort[] glyphs, Font f, Brush b, PointF[] pos, int flags, Matrix m);
        void SetWorldTransform(Matrix m);
        void ResetWorldTransform();
        void MultiplyWorldTransform(Matrix m, MatrixOrder o);
        void TranslateWorldTransform(float dx, float dy, MatrixOrder o);
        void ScaleWorldTransform(float sx, float sy, MatrixOrder o);
        void RotateWorldTransform(float a, MatrixOrder o);
        void SetPageTransform(GraphicsUnit u, float scale);
        void SetClipRect(RectangleF r, CombineMode m);
        void SetClipPath(GraphicsPath p, CombineMode m);
        void SetClipRegion(Region r, CombineMode m);
        void ResetClip();
        void OffsetClip(float dx, float dy);
        object Save();
        void Restore(object state);
        object BeginContainer(RectangleF dst, RectangleF src, GraphicsUnit u);
        object BeginContainerNoParams();
        void EndContainer(object state);
        void SetAntiAliasMode(SmoothingMode m);
        void SetTextRenderingHint(TextRenderingHint h);
        void SetTextContrast(int c);
        void SetInterpolationMode(InterpolationMode m);
        void SetPixelOffsetMode(PixelOffsetMode m);
        void SetCompositingMode(CompositingMode m);
        void SetCompositingQuality(CompositingQuality q);
        void SetRenderingOrigin(int x, int y);
        void Comment(byte[] data);
        void Flush(FlushIntention f);
    }

    public static class MetafileScenarios
    {
        public delegate void Scenario(IRec g);

        static RectangleF R(float x, float y, float w, float h) { return new RectangleF(x, y, w, h); }
        static PointF P(float x, float y) { return new PointF(x, y); }

        static Bitmap Checker(int w, int h, PixelFormat f)
        {
            var b = new Bitmap(w, h, f);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    b.SetPixel(x, y, ((x ^ y) & 1) == 0 ? Color.FromArgb(255, 200, 30, 40) : Color.FromArgb(128, 10, 120, 250));
            return b;
        }

        public static Dictionary<string, Scenario> All()
        {
            var d = new Dictionary<string, Scenario>();

            d["fillrect_int"] = g => g.FillRects(new SolidBrush(Color.Red), new[] { R(10, 10, 50, 30) });
            d["fillrect_float"] = g => g.FillRects(new SolidBrush(Color.FromArgb(128, 0, 0, 255)), new[] { R(10.5f, 10.25f, 50, 30), R(1, 2, 3, 4) });
            d["fillrect_big"] = g => g.FillRects(new SolidBrush(Color.Green), new[] { R(-40000, 10, 80000, 30) });
            d["drawrect"] = g => g.DrawRects(new Pen(Color.Blue, 3), new[] { R(5, 5, 40, 20), R(60, 5, 10, 10) });
            d["lines"] = g => { var p = new Pen(Color.Black, 1); g.DrawLines(p, new[] { P(0, 0), P(10, 20), P(30, 5) }, false); g.DrawLines(p, new[] { P(0.5f, 0), P(10, 20), P(30, 5) }, true); };
            d["polygon"] = g => { g.FillPolygon(Brushes.Orange, new[] { P(0, 0), P(50, 10), P(20, 40) }, FillMode.Alternate); g.FillPolygon(Brushes.Orange, new[] { P(0, 0), P(50, 10), P(20, 40), P(1, 1) }, FillMode.Winding); };
            d["ellipse"] = g => { g.FillEllipse(Brushes.Purple, R(1, 2, 30, 20)); g.DrawEllipse(Pens.Navy, R(1.5f, 2, 30, 20)); };
            d["pie_arc"] = g => { g.FillPie(Brushes.Teal, R(0, 0, 40, 40), 30, 120); g.DrawPie(Pens.Maroon, R(0, 0, 40, 40), -10, 200); g.DrawArc(new Pen(Color.Olive, 2.5f), R(5.5f, 5, 30, 30), 0, 90); };
            d["path"] = g =>
            {
                var path = new GraphicsPath();
                path.AddLine(0, 0, 10, 10); path.AddBezier(10, 10, 20, 0, 30, 20, 40, 10); path.CloseFigure();
                path.AddEllipse(50, 0, 20, 10);
                g.FillPath(Brushes.Gold, path);
                g.DrawPath(Pens.DarkGreen, path);
                var p2 = new GraphicsPath(FillMode.Winding); p2.AddRectangle(new RectangleF(0.5f, 0, 10, 10));
                g.FillPath(Brushes.Gold, p2);
            };
            d["curves"] = g =>
            {
                var pts = new[] { P(0, 0), P(20, 30), P(40, 0), P(60, 30) };
                g.FillClosedCurve(Brushes.Pink, pts, 0.5f, FillMode.Alternate);
                g.DrawClosedCurve(Pens.Red, pts, 0.7f);
                g.DrawCurve(Pens.Red, pts, 1, 2, 0.5f);
                g.DrawCurve(Pens.Blue, pts, 0, 3, 0.5f);
                g.DrawBeziers(Pens.Green, new[] { P(0, 0), P(10, 10), P(20, 10), P(30, 0), P(40, -10), P(50, -10), P(60, 0) });
            };
            d["region"] = g =>
            {
                var r = new Region(new Rectangle(0, 0, 20, 20));
                r.Union(new Rectangle(10, 10, 20, 20));
                var gp = new GraphicsPath(); gp.AddEllipse(0, 0, 15, 15);
                r.Exclude(gp);
                g.FillRegion(Brushes.Coral, r);
                g.FillRegion(Brushes.Coral, new Region());
                var e = new Region(); e.MakeEmpty(); g.FillRegion(Brushes.Coral, e);
            };
            d["transforms"] = g =>
            {
                g.SetWorldTransform(new Matrix(1, 0.5f, 0, 1, 10, 20));
                g.MultiplyWorldTransform(new Matrix(2, 0, 0, 2, 0, 0), MatrixOrder.Append);
                g.TranslateWorldTransform(5, 6, MatrixOrder.Prepend);
                g.ScaleWorldTransform(1.5f, 0.5f, MatrixOrder.Append);
                g.RotateWorldTransform(30, MatrixOrder.Prepend);
                g.ResetWorldTransform();
                g.SetPageTransform(GraphicsUnit.Millimeter, 2);
                g.SetPageTransform(GraphicsUnit.Pixel, 1);
                g.FillRects(Brushes.Black, new[] { R(0, 0, 1, 1) });
            };
            d["clips"] = g =>
            {
                g.SetClipRect(R(0, 0, 50, 50), CombineMode.Replace);
                g.SetClipRect(R(10.5f, 0, 50, 50), CombineMode.Intersect);
                var gp = new GraphicsPath(); gp.AddEllipse(0, 0, 30, 30);
                g.SetClipPath(gp, CombineMode.Union);
                var r = new Region(new Rectangle(1, 1, 5, 5));
                g.SetClipRegion(r, CombineMode.Xor);
                g.OffsetClip(3, 4);
                g.ResetClip();
                g.SetClipRegion(new Region(), CombineMode.Replace);
                g.FillRects(Brushes.Black, new[] { R(0, 0, 100, 100) });
            };
            d["state"] = g =>
            {
                object s1 = g.Save();
                g.TranslateWorldTransform(10, 10, MatrixOrder.Prepend);
                object c1 = g.BeginContainer(R(0, 0, 100, 100), R(0, 0, 50, 50), GraphicsUnit.Pixel);
                g.FillRects(Brushes.Red, new[] { R(0, 0, 10, 10) });
                object c2 = g.BeginContainerNoParams();
                g.FillRects(Brushes.Blue, new[] { R(0, 0, 10, 10) });
                g.EndContainer(c2);
                g.EndContainer(c1);
                object s2 = g.Save();
                g.Restore(s2);
                g.Restore(s1);
                object c3 = g.BeginContainer(R(1, 2, 30, 40), R(0, 0, 10, 10), GraphicsUnit.Inch);
                g.EndContainer(c3);
            };
            d["modes"] = g =>
            {
                g.SetAntiAliasMode(SmoothingMode.AntiAlias);
                g.SetAntiAliasMode(SmoothingMode.HighSpeed);
                g.SetTextRenderingHint(TextRenderingHint.ClearTypeGridFit);
                g.SetTextContrast(8);
                g.SetInterpolationMode(InterpolationMode.NearestNeighbor);
                g.SetInterpolationMode(InterpolationMode.HighQualityBicubic);
                g.SetPixelOffsetMode(PixelOffsetMode.Half);
                g.SetPixelOffsetMode(PixelOffsetMode.HighQuality);
                g.SetCompositingMode(CompositingMode.SourceCopy);
                g.SetCompositingQuality(CompositingQuality.HighQuality);
                g.SetRenderingOrigin(3, 4);
                g.Comment(new byte[] { 1, 2, 3, 4, 5 });
                g.Comment(new byte[] { 9 });
                g.Flush(FlushIntention.Flush);
                g.Clear(Color.FromArgb(10, 20, 30, 40));
            };
            d["objects_reuse"] = g =>
            {
                var red = new SolidBrush(Color.Red);
                var pen = new Pen(Color.Blue);
                g.FillRects(red, new[] { R(0, 0, 10, 10) });
                g.FillRects(red, new[] { R(0, 0, 10, 10) });
                g.DrawRects(pen, new[] { R(0, 0, 10, 10) });
                g.DrawRects(pen, new[] { R(0, 0, 10, 10) });
                var b2 = new SolidBrush(Color.Red);
                var hb = new HatchBrush(HatchStyle.Cross, Color.Red, Color.Blue);
                g.FillRects(hb, new[] { R(0, 0, 10, 10) });
                g.FillRects(b2, new[] { R(0, 0, 10, 10) });
                for (int i = 0; i < 70; i++)
                    g.DrawRects(new Pen(Color.FromArgb(255, i, 0, 0), i), new[] { R(0, 0, 10, 10) });
                g.DrawRects(pen, new[] { R(0, 0, 10, 10) });
            };
            d["brushes"] = g =>
            {
                g.FillRects(new HatchBrush(HatchStyle.DiagonalBrick, Color.Red, Color.FromArgb(100, 0, 0, 255)), new[] { R(0, 0, 10, 10) });
                var lg = new LinearGradientBrush(P(0, 0), P(10, 5), Color.Red, Color.Blue);
                g.FillRects(lg, new[] { R(0, 0, 10, 10) });
                var lg2 = new LinearGradientBrush(R(1, 2, 30, 40), Color.Red, Color.Blue, 30f, true);
                lg2.Blend = new Blend(3) { Factors = new float[] { 0, 0.7f, 1 }, Positions = new float[] { 0, 0.4f, 1 } };
                lg2.WrapMode = WrapMode.TileFlipXY; lg2.GammaCorrection = true;
                g.FillRects(lg2, new[] { R(0, 0, 10, 10) });
                var lg3 = new LinearGradientBrush(R(1, 2, 30, 40), Color.Red, Color.Blue, LinearGradientMode.BackwardDiagonal);
                lg3.InterpolationColors = new ColorBlend(3) { Colors = new[] { Color.Red, Color.Lime, Color.Blue }, Positions = new float[] { 0, 0.3f, 1 } };
                lg3.MultiplyTransform(new Matrix(1, 0, 0, 2, 3, 4));
                g.FillRects(lg3, new[] { R(0, 0, 10, 10) });
                var pg = new PathGradientBrush(new[] { P(0, 0), P(10, 0), P(3, 7) });
                pg.CenterColor = Color.White; pg.SurroundColors = new[] { Color.Red, Color.Green, Color.Blue };
                g.FillRects(pg, new[] { R(0, 0, 10, 10) });
                var gp = new GraphicsPath(); gp.AddEllipse(0, 0, 10, 20);
                var pg2 = new PathGradientBrush(gp);
                pg2.CenterPoint = P(4, 5); pg2.FocusScales = P(0.2f, 0.3f);
                pg2.Blend = new Blend(2) { Factors = new float[] { 0.2f, 1 }, Positions = new float[] { 0, 1 } };
                pg2.WrapMode = WrapMode.Tile;
                g.FillRects(pg2, new[] { R(0, 0, 10, 10) });
                var pg3 = new PathGradientBrush(new[] { P(0, 0), P(10, 0), P(3, 7), P(0, 9) });
                pg3.InterpolationColors = new ColorBlend(2) { Colors = new[] { Color.Red, Color.Blue }, Positions = new float[] { 0, 1 } };
                pg3.Transform = new Matrix(1, 0, 0, 1, 5, 5);
                g.FillRects(pg3, new[] { R(0, 0, 10, 10) });
                var tb = new TextureBrush(Checker(4, 3, PixelFormat.Format32bppArgb), WrapMode.TileFlipX);
                tb.TranslateTransform(2, 3);
                g.FillRects(tb, new[] { R(0, 0, 10, 10) });
            };
            d["pens"] = g =>
            {
                var p1 = new Pen(Color.Red, 2) { DashStyle = DashStyle.DashDot, DashCap = DashCap.Round, DashOffset = 1.5f };
                g.DrawLines(p1, new[] { P(0, 0), P(50, 0) }, false);
                var p2 = new Pen(Color.Red, 2) { DashPattern = new float[] { 1, 2, 3, 4 }, StartCap = LineCap.Round, EndCap = LineCap.ArrowAnchor, LineJoin = LineJoin.Bevel, MiterLimit = 4 };
                g.DrawLines(p2, new[] { P(0, 0), P(50, 0), P(50, 50) }, false);
                var p3 = new Pen(Color.Red, 5) { CompoundArray = new float[] { 0, 0.3f, 0.7f, 1 } };
                g.DrawRects(p3, new[] { R(0, 0, 20, 20) });
                g.DrawRects(new Pen(Color.Red, 5) { Alignment = PenAlignment.Inset }, new[] { R(0, 0, 20, 20) });
                var p4 = new Pen(new HatchBrush(HatchStyle.Cross, Color.Black), 4);
                p4.Transform = new Matrix(2, 0, 0, 1, 0, 0);
                g.DrawRects(p4, new[] { R(0, 0, 20, 20) });
                var p5 = new Pen(Color.Black, 3);
                p5.CustomStartCap = new AdjustableArrowCap(3, 4, true);
                var capPath = new GraphicsPath(); capPath.AddLine(-2, -3, 2, -3); capPath.AddLine(2, -3, 0, 1); capPath.CloseFigure();
                var cc = new CustomLineCap(null, capPath, LineCap.Flat, 1);
                cc.WidthScale = 2; cc.StrokeJoin = LineJoin.Round;
                p5.CustomEndCap = cc;
                g.DrawLines(p5, new[] { P(0, 0), P(50, 50) }, false);
                var p6 = new Pen(Color.Black, 1) { StartCap = LineCap.Square, EndCap = LineCap.DiamondAnchor, DashStyle = DashStyle.Dot };
                g.DrawLines(p6, new[] { P(0, 0), P(50, 50) }, false);
            };
            d["images"] = g =>
            {
                var bmp = Checker(5, 4, PixelFormat.Format32bppArgb);
                g.DrawImage(bmp, R(0, 0, 50, 40), R(0, 0, 5, 4), GraphicsUnit.Pixel, null);
                var ia = new ImageAttributes(); ia.SetWrapMode(WrapMode.TileFlipY, Color.FromArgb(1, 2, 3, 4), true);
                g.DrawImage(bmp, R(0, 0, 50, 40), R(1, 1, 2, 2), GraphicsUnit.Pixel, ia);
                g.DrawImagePoints(bmp, new[] { P(0, 0), P(40, 10), P(5, 30) }, R(0, 0, 5, 4), GraphicsUnit.Pixel, null);
                var b24 = Checker(3, 3, PixelFormat.Format24bppRgb);
                g.DrawImage(b24, R(0.5f, 0, 6, 6), R(0, 0, 3, 3), GraphicsUnit.Pixel, null);
            };
            d["empty"] = g => g.SetAntiAliasMode(SmoothingMode.AntiAlias);
            d["nothing"] = g => { };
            d["pageunits"] = g =>
            {
                g.SetPageTransform(GraphicsUnit.Inch, 0.5f);
                g.FillRects(Brushes.Red, new[] { R(0.25f, 0.25f, 1, 1) });
                g.SetPageTransform(GraphicsUnit.Millimeter, 1);
                g.DrawRects(new Pen(Color.Blue, 0.5f), new[] { R(1, 2, 10, 10) });
                g.RotateWorldTransform(15, MatrixOrder.Prepend);
                g.FillEllipse(Brushes.Green, R(5, 5, 20, 10));
                g.SetPageTransform(GraphicsUnit.Point, 1);
                g.DrawLines(new Pen(Color.Black, 2), new[] { P(-5, -3), P(40, 7) }, false);
            };
            d["negints"] = g =>
            {
                g.FillPolygon(Brushes.Red, new[] { P(-3, -4), P(10, -2), P(5, 8) }, FillMode.Alternate);
                g.FillRects(Brushes.Red, new[] { R(-3, -4, 5, 5) });
                g.DrawLines(Pens.Black, new[] { P(-1, 0), P(20000, 0), P(40000, 1) }, false);
            };
            d["imageunits"] = g =>
            {
                var bmp = Checker(4, 4, PixelFormat.Format32bppArgb);
                bmp.SetResolution(192, 192);
                g.DrawImage(bmp, R(0, 0, 40, 40), R(0, 0, 1, 1), GraphicsUnit.Inch, null);
                g.SetPixelOffsetMode(PixelOffsetMode.HighQuality);
                g.DrawImage(bmp, R(0, 0, 40, 40), R(0, 0, 4, 4), GraphicsUnit.Pixel, null);
                g.DrawImage(bmp, R(10, 10, -20, 20), R(0, 0, 4, 4), GraphicsUnit.Pixel, null);
            };
            d["text"] = g =>
            {
                var f = new Font("Arial", 12f, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Point);
                g.DrawString("Hello", f, R(10, 10, 0, 0), null, Brushes.Black);
                var sf = new StringFormat(StringFormatFlags.NoWrap | StringFormatFlags.DirectionVertical);
                sf.Alignment = StringAlignment.Center; sf.LineAlignment = StringAlignment.Far;
                sf.Trimming = StringTrimming.EllipsisWord; sf.HotkeyPrefix = HotkeyPrefix.Show;
                sf.SetTabStops(4, new float[] { 10, 20 });
                sf.SetDigitSubstitution(1033, StringDigitSubstitute.National);
                sf.SetMeasurableCharacterRanges(new[] { new CharacterRange(0, 2) });
                g.DrawString("A\tB", new Font("Times New Roman", 20f, FontStyle.Underline, GraphicsUnit.Pixel), R(0, 0, 100, 50), sf, Brushes.Blue);
                g.DrawString("x", f, R(0, 0, 10, 10), StringFormat.GenericTypographic, Brushes.Black);
                g.DrawDriverString(new ushort[] { 36, 37, 38 }, f, Brushes.Red, new[] { P(0, 10), P(8, 10), P(16, 10) }, 1, null);
                g.DrawDriverString(new ushort[] { 36, 37 }, f, Brushes.Red, new[] { P(0, 10), P(8, 10) }, 0, new Matrix(1, 0, 0, 1, 2, 3));
            };
            // ---- playback coverage: what is drawn, at sizes where the pixels say something ----------
            d["fills"] = g =>
            {
                g.FillRects(new HatchBrush(HatchStyle.DiagonalCross, Color.Navy, Color.LightYellow), new[] { R(0, 0, 40, 30) });
                g.FillRects(new HatchBrush(HatchStyle.LargeCheckerBoard, Color.FromArgb(160, 200, 0, 0)), new[] { R(40, 0, 40, 30) });
                g.FillEllipse(new HatchBrush(HatchStyle.Weave, Color.DarkGreen, Color.White), R(80, 0, 40, 30));
                var tb = new TextureBrush(Checker(6, 5, PixelFormat.Format32bppArgb), WrapMode.TileFlipXY);
                tb.ScaleTransform(3, 2);
                tb.RotateTransform(20);
                g.FillRects(tb, new[] { R(0, 30, 60, 40) });
                var tc = new TextureBrush(Checker(5, 5, PixelFormat.Format24bppRgb), WrapMode.Clamp);
                tc.TranslateTransform(70, 40);
                tc.ScaleTransform(4, 4);
                g.FillEllipse(tc, R(60, 30, 60, 40));
            };
            d["gradients"] = g =>
            {
                var lg = new LinearGradientBrush(R(0, 0, 60, 30), Color.Red, Color.Blue, 15f, true);
                lg.SetSigmaBellShape(0.4f, 0.9f);
                g.FillRects(lg, new[] { R(0, 0, 60, 30) });
                var lg2 = new LinearGradientBrush(P(60, 0), P(80, 10), Color.FromArgb(200, 0, 160, 0), Color.Yellow);
                lg2.WrapMode = WrapMode.TileFlipX;
                g.FillRects(lg2, new[] { R(60, 0, 60, 30) });
                var gp = new GraphicsPath(); gp.AddEllipse(0, 30, 60, 40);
                var pg = new PathGradientBrush(gp);
                pg.CenterColor = Color.White; pg.SurroundColors = new[] { Color.Purple };
                pg.FocusScales = P(0.3f, 0.4f);
                g.FillPath(pg, gp);
                var pg2 = new PathGradientBrush(new[] { P(60, 30), P(120, 35), P(110, 70), P(65, 65) });
                pg2.SurroundColors = new[] { Color.Red, Color.Lime, Color.Blue, Color.Yellow };
                pg2.CenterColor = Color.Gray;
                pg2.InterpolationColors = new ColorBlend(3) { Colors = new[] { Color.Black, Color.Orange, Color.White }, Positions = new float[] { 0, 0.5f, 1 } };
                g.FillRects(pg2, new[] { R(60, 30, 60, 40) });
            };
            d["strokes"] = g =>
            {
                g.SetAntiAliasMode(SmoothingMode.AntiAlias);
                var p1 = new Pen(Color.Maroon, 4) { DashStyle = DashStyle.DashDotDot, DashCap = DashCap.Triangle };
                g.DrawLines(p1, new[] { P(5, 5), P(115, 10) }, false);
                var p2 = new Pen(Color.Navy, 9) { CompoundArray = new float[] { 0, 0.2f, 0.4f, 0.6f, 0.8f, 1 }, LineJoin = LineJoin.Round };
                g.DrawRects(p2, new[] { R(10, 20, 40, 30) });
                var p3 = new Pen(Color.DarkGreen, 3) { StartCap = LineCap.RoundAnchor, EndCap = LineCap.ArrowAnchor, LineJoin = LineJoin.MiterClipped };
                g.DrawLines(p3, new[] { P(60, 20), P(110, 30), P(70, 50), P(110, 65) }, false);
                var p4 = new Pen(Color.Black, 2);
                p4.CustomEndCap = new AdjustableArrowCap(4, 5, false);
                p4.DashPattern = new float[] { 3, 1, 1, 1 };
                g.DrawBeziers(p4, new[] { P(5, 70), P(30, 40), P(60, 90), P(90, 60) });
                var p5 = new Pen(new LinearGradientBrush(P(0, 0), P(120, 0), Color.Red, Color.Blue), 5) { Alignment = PenAlignment.Inset };
                g.DrawEllipse(p5, R(15, 25, 30, 20));
            };
            d["clipxf"] = g =>
            {
                g.FillRects(Brushes.LightGray, new[] { R(0, 0, 120, 80) });
                g.TranslateWorldTransform(60, 40, MatrixOrder.Prepend);
                g.RotateWorldTransform(25, MatrixOrder.Prepend);
                g.SetClipRect(R(-40, -25, 80, 50), CombineMode.Replace);
                var gp = new GraphicsPath(); gp.AddEllipse(-30, -30, 50, 60);
                g.SetClipPath(gp, CombineMode.Exclude);
                g.FillRects(Brushes.SteelBlue, new[] { R(-60, -40, 120, 80) });
                g.ResetWorldTransform();
                g.ScaleWorldTransform(2, 1.5f, MatrixOrder.Prepend);
                g.SetClipRect(R(5, 5, 20, 20), CombineMode.Union);
                g.FillEllipse(Brushes.Crimson, R(0, 0, 40, 40));
                g.ResetClip();
                g.DrawLines(Pens.Black, new[] { P(0, 0), P(60, 53) }, false);
            };
            d["imagewrap"] = g =>
            {
                g.FillRects(Brushes.Khaki, new[] { R(0, 0, 120, 80) });
                var bmp = Checker(4, 3, PixelFormat.Format32bppArgb);
                var ia = new ImageAttributes(); ia.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(bmp, R(0, 0, 60, 40), R(-2, -2, 10, 9), GraphicsUnit.Pixel, ia);
                var ib = new ImageAttributes(); ib.SetWrapMode(WrapMode.Clamp, Color.FromArgb(128, 0, 0, 255));
                g.DrawImage(bmp, R(60, 0, 60, 40), R(-1, -1, 6, 5), GraphicsUnit.Pixel, ib);
                g.SetInterpolationMode(InterpolationMode.NearestNeighbor);
                g.DrawImagePoints(Checker(5, 4, PixelFormat.Format24bppRgb), new[] { P(10, 45), P(70, 50), P(5, 78) }, R(0, 0, 5, 4), GraphicsUnit.Pixel, null);
                g.SetInterpolationMode(InterpolationMode.HighQualityBicubic);
                g.DrawImage(bmp, R(75, 42, 40, 35), R(0, 0, 4, 3), GraphicsUnit.Pixel, null);
            };
            return d;
        }
    }
}
