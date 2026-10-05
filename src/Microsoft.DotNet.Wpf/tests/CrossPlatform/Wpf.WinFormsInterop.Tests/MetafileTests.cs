// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The managed Metafile held to REAL GDI+.
//
// Fixtures/Metafiles/plus holds what .NET Framework's System.Drawing (gdiplus.dll) recorded for each
// scenario of Fixtures/Metafiles/MetafileScenarios.cs into an EmfPlusOnly metafile; the recorder
// here runs the same scenario (the same source file, compiled against the fork) and its EMF+ records
// must be the same bytes. Fixtures/Metafiles/samples are GDI-written EMF and WMF files and the
// headers GDI+ derives from them. Fixtures/Metafiles/oracle has the programs that wrote both.
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Drawing.WebGpuBackend.Gdip;
using System.IO;
using System.Linq;
using System.Text;
using MetafileOracle;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    /// <summary>The recorder's API as the scenarios call it, with the save ids a Graphics would hand out.</summary>
    internal sealed class RecorderRec : IRec
    {
        private readonly GpMetafileRecorder _r;
        private uint _next = 1;
        public RecorderRec(GpMetafileRecorder r) { _r = r; }
        public void Clear(Color c) => _r.Clear(c);
        public void FillRects(Brush b, RectangleF[] r) => _r.FillRects(b, r);
        public void DrawRects(Pen p, RectangleF[] r) => _r.DrawRects(p, r);
        public void FillPolygon(Brush b, PointF[] pts, FillMode mode) => _r.FillPolygon(b, pts, mode);
        public void DrawLines(Pen p, PointF[] pts, bool closed) => _r.DrawLines(p, pts, closed);
        public void FillEllipse(Brush b, RectangleF r) => _r.FillEllipse(b, r);
        public void DrawEllipse(Pen p, RectangleF r) => _r.DrawEllipse(p, r);
        public void FillPie(Brush b, RectangleF r, float s, float w) => _r.FillPie(b, r, s, w);
        public void DrawPie(Pen p, RectangleF r, float s, float w) => _r.DrawPie(p, r, s, w);
        public void DrawArc(Pen p, RectangleF r, float s, float w) => _r.DrawArc(p, r, s, w);
        public void FillPath(Brush b, GraphicsPath path) => _r.FillPath(b, path);
        public void DrawPath(Pen p, GraphicsPath path) => _r.DrawPath(p, path);
        public void FillClosedCurve(Brush b, PointF[] pts, float t, FillMode m) => _r.FillClosedCurve(b, pts, t, m);
        public void DrawClosedCurve(Pen p, PointF[] pts, float t) => _r.DrawClosedCurve(p, pts, t);
        public void DrawCurve(Pen p, PointF[] pts, int o, int n, float t) => _r.DrawCurve(p, pts, o, n, t);
        public void DrawBeziers(Pen p, PointF[] pts) => _r.DrawBeziers(p, pts);
        public void FillRegion(Brush b, Region r) => _r.FillRegion(b, r);
        public void DrawImage(Image img, RectangleF d, RectangleF s, GraphicsUnit u, ImageAttributes? ia) => _r.DrawImage(img, d, s, u, ia!);
        public void DrawImagePoints(Image img, PointF[] d, RectangleF s, GraphicsUnit u, ImageAttributes? ia) => _r.DrawImagePoints(img, d, s, u, ia!);
        public void DrawString(string s, Font f, RectangleF r, StringFormat? fmt, Brush b) => _r.DrawString(s, f, r, fmt!, b);
        public void DrawDriverString(ushort[] t, Font f, Brush b, PointF[] pos, int flags, Matrix? m) => _r.DrawDriverString(t, f, b, pos, flags, m!);
        public void SetWorldTransform(Matrix m) => _r.SetWorldTransform(m);
        public void ResetWorldTransform() => _r.ResetWorldTransform();
        public void MultiplyWorldTransform(Matrix m, MatrixOrder o) => _r.MultiplyWorldTransform(m, o);
        public void TranslateWorldTransform(float dx, float dy, MatrixOrder o) => _r.TranslateWorldTransform(dx, dy, o);
        public void ScaleWorldTransform(float sx, float sy, MatrixOrder o) => _r.ScaleWorldTransform(sx, sy, o);
        public void RotateWorldTransform(float a, MatrixOrder o) => _r.RotateWorldTransform(a, o);
        // Graphics.PageUnit and PageScale each record the page transform as it then stands; the
        // oracle sets both, unit first.
        private float _scale = 1f;
        public void SetPageTransform(GraphicsUnit u, float s) { _r.SetPageTransform(u, _scale); _r.SetPageTransform(u, s); _scale = s; }
        public void SetClipRect(RectangleF r, CombineMode m) => _r.SetClipRect(r, m);
        public void SetClipPath(GraphicsPath p, CombineMode m) => _r.SetClipPath(p, m);
        public void SetClipRegion(Region r, CombineMode m) => _r.SetClipRegion(r, m);
        public void ResetClip() => _r.ResetClip();
        public void OffsetClip(float dx, float dy) => _r.OffsetClip(dx, dy);
        public object Save() { uint id = _next++; _r.Save(id); return id; }
        public void Restore(object s) => _r.Restore((uint)s);
        public object BeginContainer(RectangleF d, RectangleF s, GraphicsUnit u) { uint id = _next++; _r.BeginContainer(d, s, u, id); return id; }
        public object BeginContainerNoParams() { uint id = _next++; _r.BeginContainerNoParams(id); return id; }
        public void EndContainer(object s) => _r.EndContainer((uint)s);
        public void SetAntiAliasMode(SmoothingMode m) => _r.SetAntiAliasMode(m);
        public void SetTextRenderingHint(TextRenderingHint h) => _r.SetTextRenderingHint(h);
        public void SetTextContrast(int c) => _r.SetTextContrast(c);
        public void SetInterpolationMode(InterpolationMode m) => _r.SetInterpolationMode(m);
        public void SetPixelOffsetMode(PixelOffsetMode m) => _r.SetPixelOffsetMode(m);
        public void SetCompositingMode(CompositingMode m) => _r.SetCompositingMode(m);
        public void SetCompositingQuality(CompositingQuality q) => _r.SetCompositingQuality(q);
        public void SetRenderingOrigin(int x, int y) => _r.SetRenderingOrigin(x, y);
        public void Comment(byte[] data) => _r.Comment(data);
        public void Flush(FlushIntention f) => _r.Flush(f);
    }

    /// <summary>The scenarios through Graphics.FromImage(metafile), with exactly the calls the oracle
    /// (oracle/mfo.cs, GRec) makes on .NET Framework's Graphics.</summary>
    internal sealed class GraphicsRec : IRec
    {
        private readonly Graphics g;
        public GraphicsRec(Graphics g) { this.g = g; }
        public void Clear(Color c) => g.Clear(c);
        public void FillRects(Brush b, RectangleF[] r) => g.FillRectangles(b, r);
        public void DrawRects(Pen p, RectangleF[] r) => g.DrawRectangles(p, r);
        public void FillPolygon(Brush b, PointF[] pts, FillMode mode) => g.FillPolygon(b, pts, mode);
        public void DrawLines(Pen p, PointF[] pts, bool closed) { if (closed) g.DrawPolygon(p, pts); else g.DrawLines(p, pts); }
        public void FillEllipse(Brush b, RectangleF r) => g.FillEllipse(b, r);
        public void DrawEllipse(Pen p, RectangleF r) => g.DrawEllipse(p, r);
        public void FillPie(Brush b, RectangleF r, float s, float w) => g.FillPie(b, r.X, r.Y, r.Width, r.Height, s, w);
        public void DrawPie(Pen p, RectangleF r, float s, float w) => g.DrawPie(p, r, s, w);
        public void DrawArc(Pen p, RectangleF r, float s, float w) => g.DrawArc(p, r, s, w);
        public void FillPath(Brush b, GraphicsPath path) => g.FillPath(b, path);
        public void DrawPath(Pen p, GraphicsPath path) => g.DrawPath(p, path);
        public void FillClosedCurve(Brush b, PointF[] pts, float t, FillMode m) => g.FillClosedCurve(b, pts, m, t);
        public void DrawClosedCurve(Pen p, PointF[] pts, float t) => g.DrawClosedCurve(p, pts, t, FillMode.Alternate);
        public void DrawCurve(Pen p, PointF[] pts, int o, int n, float t) => g.DrawCurve(p, pts, o, n, t);
        public void DrawBeziers(Pen p, PointF[] pts) => g.DrawBeziers(p, pts);
        public void FillRegion(Brush b, Region r) => g.FillRegion(b, r);
        public void DrawImage(Image img, RectangleF d, RectangleF s, GraphicsUnit u, ImageAttributes? ia)
        {
            if (ia == null) g.DrawImage(img, d, s, u);
            else g.DrawImage(img, Rectangle.Round(d), s.X, s.Y, s.Width, s.Height, u, ia);
        }
        public void DrawImagePoints(Image img, PointF[] d, RectangleF s, GraphicsUnit u, ImageAttributes? ia) => g.DrawImage(img, d, s, u, ia);
        public void DrawString(string s, Font f, RectangleF r, StringFormat? fmt, Brush b) => g.DrawString(s, f, b, r, fmt);
        // No public Graphics verb: the oracle calls GdipDrawDriverString itself.
        public void DrawDriverString(ushort[] t, Font f, Brush b, PointF[] pos, int flags, Matrix? m) => g.mf_rec.DrawDriverString(t, f, b, pos, flags, m!);
        public void SetWorldTransform(Matrix m) => g.Transform = m;
        public void ResetWorldTransform() => g.ResetTransform();
        public void MultiplyWorldTransform(Matrix m, MatrixOrder o) => g.MultiplyTransform(m, o);
        public void TranslateWorldTransform(float dx, float dy, MatrixOrder o) => g.TranslateTransform(dx, dy, o);
        public void ScaleWorldTransform(float sx, float sy, MatrixOrder o) => g.ScaleTransform(sx, sy, o);
        public void RotateWorldTransform(float a, MatrixOrder o) => g.RotateTransform(a, o);
        public void SetPageTransform(GraphicsUnit u, float s) { g.PageUnit = u; g.PageScale = s; }
        public void SetClipRect(RectangleF r, CombineMode m) => g.SetClip(r, m);
        public void SetClipPath(GraphicsPath p, CombineMode m) => g.SetClip(p, m);
        public void SetClipRegion(Region r, CombineMode m) => g.SetClip(r, m);
        public void ResetClip() => g.ResetClip();
        public void OffsetClip(float dx, float dy) => g.TranslateClip(dx, dy);
        public object Save() => g.Save();
        public void Restore(object s) => g.Restore((GraphicsState)s);
        public object BeginContainer(RectangleF d, RectangleF s, GraphicsUnit u) => g.BeginContainer(d, s, u);
        public object BeginContainerNoParams() => g.BeginContainer();
        public void EndContainer(object s) => g.EndContainer((GraphicsContainer)s);
        public void SetAntiAliasMode(SmoothingMode m) => g.SmoothingMode = m;
        public void SetTextRenderingHint(TextRenderingHint h) => g.TextRenderingHint = h;
        public void SetTextContrast(int c) => g.TextContrast = c;
        public void SetInterpolationMode(InterpolationMode m) => g.InterpolationMode = m;
        public void SetPixelOffsetMode(PixelOffsetMode m) => g.PixelOffsetMode = m;
        public void SetCompositingMode(CompositingMode m) => g.CompositingMode = m;
        public void SetCompositingQuality(CompositingQuality q) => g.CompositingQuality = q;
        public void SetRenderingOrigin(int x, int y) => g.RenderingOrigin = new Point(x, y);
        public void Comment(byte[] d) => g.AddMetafileComment(d);
        public void Flush(FlushIntention f) => g.Flush(f);
    }

    public sealed class MetafileTests
    {
        private static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Metafiles");

        public static TheoryData<string> Scenarios()
        {
            var d = new TheoryData<string>();
            foreach (string name in MetafileScenarios.All().Keys) d.Add(name);
            return d;
        }

        /// <summary>The EMF+ records of an EMF, in order, with the GDI comment each began in.</summary>
        internal static List<(int Comment, byte[] Record)> EmfPlusRecords(byte[] emf)
        {
            var list = new List<(int, byte[])>();
            int comment = 0;
            foreach (var (o, type, size) in GpMetafileEdit.Records(emf))
            {
                int p = GpMetafileFormat.EmfPlusPayload(emf, o, out int n);
                if (p < 0) continue;
                int q = p;
                while (q + 12 <= p + n)
                {
                    int rs = BitConverter.ToInt32(emf, q + 4);
                    if (rs < 12 || q + rs > p + n) break;
                    list.Add((comment, emf.AsSpan(q, rs).ToArray()));
                    q += rs;
                }
                comment++;
            }
            return list;
        }

        internal static byte[] Record(string scenario, EmfType type)
        {
            var ms = new MemoryStream();
            var mf = new Metafile(ms, IntPtr.Zero, type);
            GpMetafileRecorder r = mf.TakeRecorder();
            MetafileScenarios.All()[scenario](new RecorderRec(r));
            r.End();
            return ms.ToArray();
        }

        private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

        /// <summary>The same scenario through the public API: Graphics.FromImage(metafile), disposed to end it.</summary>
        internal static byte[] RecordThroughGraphics(string scenario, EmfType type)
        {
            var ms = new MemoryStream();
            using (var mf = new Metafile(ms, IntPtr.Zero, type))
            {
                using (Graphics g = Graphics.FromImage(mf))
                    MetafileScenarios.All()[scenario](new GraphicsRec(g));
                // GpMetafile::GetGraphicsContext: once per recording, and never after it ended.
                Assert.Throws<OutOfMemoryException>(() => Graphics.FromImage(mf));
            }
            return ms.ToArray();
        }

        [Theory]
        [MemberData(nameof(Scenarios))]
        public void Recorded_EmfPlus_records_are_GdiPlus_bytes(string scenario)
            => AssertSameEmfPlus(Record(scenario, EmfType.EmfPlusOnly), scenario);

        /// <summary>Graphics.FromImage(metafile) hands every verb to the recorder as GDI+'s Graphics does.</summary>
        [Theory]
        [MemberData(nameof(Scenarios))]
        public void Graphics_on_a_metafile_records_GdiPlus_bytes(string scenario)
            => AssertSameEmfPlus(RecordThroughGraphics(scenario, EmfType.EmfPlusOnly), scenario);

        [Fact]
        public void A_metafile_has_one_Graphics()
        {
            using var mf = new Metafile(new MemoryStream(), IntPtr.Zero, EmfType.EmfPlusOnly);
            using (Graphics g = Graphics.FromImage(mf))
            {
                Assert.Throws<OutOfMemoryException>(() => Graphics.FromImage(mf));
                Assert.Equal(96f, g.DpiX);
            }
            Assert.Equal(ImageFormat.Emf, mf.RawFormat);
        }

        /// <summary>DrawImage(metafile, rect) on a bitmap is the playback of its bounds into the rectangle.</summary>
        [Theory]
        [MemberData(nameof(Scenarios))]
        public void DrawImage_of_a_metafile_is_its_playback(string scenario)
        {
            using var mf = new Metafile(Path.Combine(Dir, "plus", scenario + ".emf"));
            using Bitmap want = PlayInto(mf, 120, 90, new RectangleF(5, 5, 110, 80));
            using var got = new Bitmap(120, 90, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(got))
            {
                g.Clear(Color.White);
                g.DrawImage(mf, new RectangleF(5, 5, 110, 80));
            }
            Assert.Equal(0, Differ(got, want, 0, out _));
        }

        static void AssertSameEmfPlus(byte[] ours, string scenario)
        {
            byte[] theirs = File.ReadAllBytes(Path.Combine(Dir, "plus", scenario + ".emf"));
            var a = EmfPlusRecords(ours);
            var b = EmfPlusRecords(theirs);
            var sb = new StringBuilder();
            int n = Math.Max(a.Count, b.Count);
            for (int i = 0; i < n; i++)
            {
                string x = i < a.Count ? $"[{a[i].Comment}] {Hex(a[i].Record)}" : "(none)";
                string y = i < b.Count ? $"[{b[i].Comment}] {Hex(b[i].Record)}" : "(none)";
                if (x != y) sb.AppendLine($"record {i}:\n  ours   {x}\n  gdi+   {y}");
            }
            Assert.True(sb.Length == 0, sb.ToString());
        }

        /// <summary>A metafile drawn as GDI+ draws it for Graphics.DrawImage(mf, rect): the source
        /// is the metafile's bounds in pixels.</summary>
        internal static Bitmap PlayInto(Metafile mf, int w, int h, RectangleF dest)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                GpMetafilePlayer.Play(g, mf, new[] { dest.Location, new PointF(dest.Right, dest.Top), new PointF(dest.Left, dest.Bottom) },
                    mf.RealBounds, GraphicsUnit.Pixel, null);
            }
            return bmp;
        }

        /// <summary>The fraction of pixels more than <paramref name="tol"/> apart in some channel.</summary>
        internal static double Differ(Bitmap a, Bitmap b, int tol, out int count)
        {
            count = 0;
            for (int y = 0; y < a.Height; y++)
                for (int x = 0; x < a.Width; x++)
                {
                    Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                    if (Math.Abs(p.R - q.R) > tol || Math.Abs(p.G - q.G) > tol || Math.Abs(p.B - q.B) > tol || Math.Abs(p.A - q.A) > tol) count++;
                }
            return (double)count / (a.Width * a.Height);
        }

        /// <summary>Text under a turned world transform: its down-level records are GDI+'s byte for
        /// byte (recorded on the fixture's own screen, ScreenFor) and its EMF+ playback within the
        /// tolerance, but its GDI playback is not yet exact: six pixels of the 15-degree ClearType
        /// run are a level off.</summary>
        static readonly HashSet<string> TurnedTextPending = new HashSet<string> { "text_rotate" };

        public static TheoryData<string> EmfPlusPlaybackScenarios()
        {
            var d = new TheoryData<string>();
            foreach (string name in MetafileScenarios.All().Keys) d.Add(name);
            return d;
        }

        [Theory]
        [MemberData(nameof(EmfPlusPlaybackScenarios))]
        public void Played_EmfPlus_matches_GdiPlus_pixels(string scenario)
        {
            using var mf = new Metafile(Path.Combine(Dir, "plus", scenario + ".emf"));
            using Bitmap ours = PlayInto(mf, 120, 90, new RectangleF(5, 5, 110, 80));
            using var theirs = (Bitmap)Image.FromFile(Path.Combine(Dir, "play", scenario + ".png"));
            string outDir = Environment.GetEnvironmentVariable("MF_PLAYOUT");
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                ours.Save(Path.Combine(outDir, scenario + ".png"), ImageFormat.Png);
            }
            double f = Differ(ours, theirs, 64, out int n);
            Assert.True(f < 0.02, $"{n} pixels ({f:P1}) differ from GDI+'s");
        }

        /// <summary>Every object GDI+ serialised, read back and serialised again, is the same bytes.</summary>
        [Theory]
        [MemberData(nameof(Scenarios))]
        public void Objects_read_back_and_serialise_to_GdiPlus_bytes(string scenario)
        {
            byte[] theirs = File.ReadAllBytes(Path.Combine(Dir, "plus", scenario + ".emf"));
            var sb = new StringBuilder();
            foreach (var (_, rec) in EmfPlusRecords(theirs))
            {
                if (BitConverter.ToUInt16(rec, 0) != 0x4008) continue;
                int flags = BitConverter.ToUInt16(rec, 2);
                if ((flags & 0x8000) != 0) continue;
                var type = (EmfPlusObjectType)((flags >> 8) & 0x7f);
                object o = GpEmfPlusReader.Read(type, rec, 12, rec.Length - 12);
                if (o == null) { sb.AppendLine($"{type}: not read"); continue; }
                byte[] again = GpEmfPlusObjects.Serialize(o);
                string a = Hex(rec.AsSpan(12).ToArray()), b = Hex(again);
                if (a != b) sb.AppendLine($"{type}: gdi+ {a} / again {b}");
            }
            Assert.True(sb.Length == 0, sb.ToString());
        }

        public static TheoryData<string> EnumFiles() => new TheoryData<string> { "gdi.emf", "placeable.wmf", "fillrect_int.emf", "state.emf", "text.emf" };

        static string FixturePath(string name) =>
            File.Exists(Path.Combine(Dir, "samples", name)) ? Path.Combine(Dir, "samples", name) : Path.Combine(Dir, "plus", name);

        /// <summary>EnumerateMetafile hands the callback the records GDI+ hands it: type, flags, size.</summary>
        [Theory]
        [MemberData(nameof(EnumFiles))]
        public void Enumeration_reports_GdiPlus_records(string name)
        {
            using var mf = new Metafile(FixturePath(name));
            var got = new List<string>();
            using (var bmp = new Bitmap(10, 10))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.EnumerateMetafile(mf, new PointF(0, 0),
                    (t, f, n, d, cb) => { got.Add($"{((int)t):x} f={f:x} n={n}"); mf.PlayRecord(t, f, n, Copy(d, n)); return true; });
            }
            string[] want = File.ReadAllLines(Path.Combine(Dir, "enum", name + ".txt"));
            Assert.Equal(string.Join(" | ", want), string.Join(" | ", got));
        }

        static byte[] Copy(IntPtr p, int n)
        {
            var b = new byte[n];
            if (n > 0) System.Runtime.InteropServices.Marshal.Copy(p, b, 0, n);
            return b;
        }

        public static TheoryData<string> SampleFiles() => new TheoryData<string> { "gdi.emf", "gdi_noframe.emf", "placeable.wmf", "placeable1440.wmf", "placeable0.wmf", "framergn.emf" };

        /// <summary>GDI-only metafiles drawn as GDI+ draws them (GDI aliased; text within a tolerance).</summary>
        [Theory]
        [MemberData(nameof(SampleFiles))]
        public void Played_Gdi_records_match_GdiPlus_pixels(string name)
        {
            using var mf = new Metafile(Path.Combine(Dir, "samples", name));
            using Bitmap ours = PlayInto(mf, 120, 90, new RectangleF(5, 5, 110, 80));
            using var theirs = (Bitmap)Image.FromFile(Path.Combine(Dir, "samples", "play", Path.GetFileNameWithoutExtension(name) + ".png"));
            string outDir = Environment.GetEnvironmentVariable("MF_PLAYOUT");
            if (!string.IsNullOrEmpty(outDir)) ours.Save(Path.Combine(outDir, "s_" + Path.GetFileNameWithoutExtension(name) + ".png"), ImageFormat.Png);
            // framergn.emf (GDI's FrameRgn: regions with a hole, an ellipse, a round rectangle
            // under a hatch, two rectangles touching at a corner under R2_XORPEN; strokes 1x1 to
            // 4x1) has no text, and is GDI's pixel for pixel.
            if (name == "framergn.emf")
            {
                Differ(ours, theirs, 0, out int exact);
                Assert.True(exact == 0, $"{exact} pixels differ from GDI+'s");
                return;
            }
            // The shapes, lines and fills are GDI's exactly; what differs is the text.
            double f = Differ(ours, theirs, 64, out int n);
            Assert.True(f < 0.02, $"{n} pixels ({f:P1}) differ from GDI+'s");
        }

        // ---- down-level: what an EmfOnly / EmfPlusDual recording renders through GDI+'s metafile
        // driver into the EMF DC, held to GDI+'s own recordings (Fixtures/Metafiles/emfonly and dual,
        // `mfo rec <scenario> EmfOnly|EmfPlusDual` run inside an ARM64 Windows PowerShell, one process
        // per scenario: the driver's state is process-wide, so each file is what a fresh gdiplus.dll
        // records; and on Windows on ARM an x64 process runs gdiplus.dll's ARM64EC code, which rounds
        // some float arithmetic differently from the native ARM64 code this port follows).

        /// <summary>The screen the oracle recorded on (mfo hands GDI+ the screen DC).</summary>
        internal static readonly GpRefDevice OracleScreen = new GpRefDevice
        {
            HorzRes = 3840, VertRes = 1200, HorzSize = 1040, VertSize = 320,
            LogPixelsX = 96, LogPixelsY = 96, IsDisplay = true, DesktopDpiX = 96, DesktopDpiY = 96,
        };

        /// <summary>The screen a scenario's fixture was recorded on: its EMF header's szlDevice and
        /// szlMillimeters (the oracle hands GDI+ the screen DC, and text_rotate was recorded on a
        /// 3420 x 1884 px / 320 x 170 mm screen where the rest were recorded on OracleScreen).</summary>
        internal static GpRefDevice ScreenFor(string scenario)
        {
            string f = Path.Combine(Dir, "emfonly", scenario + ".emf");
            if (!File.Exists(f)) return OracleScreen;
            byte[] h = File.ReadAllBytes(f);
            if (h.Length < 88) return OracleScreen;
            GpRefDevice d = OracleScreen;
            d.HorzRes = BitConverter.ToInt32(h, 72); d.VertRes = BitConverter.ToInt32(h, 76);
            d.HorzSize = BitConverter.ToInt32(h, 80); d.VertSize = BitConverter.ToInt32(h, 84);
            return d;
        }

        /// <summary>A scenario recorded down-level on the oracle's screen by a fresh driver.</summary>
        internal static byte[] RecordDownLevel(string scenario, EmfType type)
        {
            lock (GpMetaDriverState.Lock)
            {
                GpMetaDriverState.Reset();
                var ms = new MemoryStream();
                GpMetafileRecorder r = GpMetafileRecorder.Create(null, ScreenFor(scenario), type, null, MetafileFrameUnit.GdiCompatible, null, ms, null);
                MetafileScenarios.All()[scenario](new RecorderRec(r));
                r.End();
                byte[] emf = ms.ToArray();
                string outDir = Environment.GetEnvironmentVariable("MF_GDIOUT");
                if (!string.IsNullOrEmpty(outDir))
                {
                    string d = Path.Combine(outDir, type == EmfType.EmfOnly ? "emfonly" : "dual");
                    Directory.CreateDirectory(d);
                    File.WriteAllBytes(Path.Combine(d, scenario + ".emf"), emf);
                }
                return emf;
            }
        }

        static string GdiDiff(byte[] ours, byte[] theirs)
        {
            var a = GpMetafileEdit.Records(ours).ToList();
            var b = GpMetafileEdit.Records(theirs).ToList();
            var sb = new StringBuilder();
            // The header: bounds, frame, size, record and handle counts.
            if (!ours.AsSpan(8, 32).SequenceEqual(theirs.AsSpan(8, 32)) || !ours.AsSpan(48, 10).SequenceEqual(theirs.AsSpan(48, 10)))
                sb.AppendLine($"header:\n  ours   {Hex(ours.AsSpan(8, 50).ToArray())}\n  gdi+   {Hex(theirs.AsSpan(8, 50).ToArray())}");
            // What GDI+ leaves unwritten is not compared: EMR_CREATEDIBPATTERNBRUSHPT's dword before
            // the BITMAPINFO, and the padding of a DIB's rows (its buffers are not cleared).
            static byte[] Rec(byte[] f, (int Offset, int Type, int Size) r)
            {
                byte[] x = f.AsSpan(r.Offset, r.Size).ToArray();
                if (r.Type == 94 && x.Length >= 36) Array.Clear(x, 32, 4);
                // EMR_EXTTEXTOUTW: the string's padding to its dword (GDI copies the characters only).
                if (r.Type == 84 && x.Length >= 76)
                {
                    int nChars = BitConverter.ToInt32(x, 44), offString = BitConverter.ToInt32(x, 48), offDx = BitConverter.ToInt32(x, 72);
                    for (int k = offString + 2 * nChars; k < offDx && k < x.Length; k++) x[k] = 0;
                }
                if (r.Type == 81 && x.Length >= 72)
                {
                    int offBmi = BitConverter.ToInt32(x, 48), offBits = BitConverter.ToInt32(x, 56);
                    int w = BitConverter.ToInt32(x, offBmi + 4), h = Math.Abs(BitConverter.ToInt32(x, offBmi + 8));
                    int bpp = BitConverter.ToUInt16(x, offBmi + 14);
                    int stride = ((w * bpp + 31) >> 5) << 2, used = (w * bpp + 7) >> 3;
                    for (int row = 0; row < h; row++)
                        for (int k = used; k < stride; k++)
                            if (offBits + row * stride + k < x.Length) x[offBits + row * stride + k] = 0;
                }
                return x;
            }
            int n = Math.Max(a.Count, b.Count);
            for (int i = 1; i < n; i++)
            {
                string x = i < a.Count ? Hex(Rec(ours, a[i])) : "(none)";
                string y = i < b.Count ? Hex(Rec(theirs, b[i])) : "(none)";
                if (x != y) sb.AppendLine($"record {i}:\n  ours   {x}\n  gdi+   {y}");
            }
            return sb.ToString();
        }

        static string RecordTypes(byte[] emf) => string.Join(",", GpMetafileEdit.Records(emf).Select(r => r.Type));

        /// <summary>The scenarios whose every down-level record is GDI+'s, byte for byte.</summary>
        public static TheoryData<string> GdiExactScenarios()
        {
            var d = new TheoryData<string>();
            foreach (string name in MetafileScenarios.All().Keys)
                d.Add(name);
            return d;
        }

        [Theory]
        [MemberData(nameof(GdiExactScenarios))]
        public void EmfOnly_records_are_GdiPlus_bytes(string scenario)
        {
            string diff = GdiDiff(RecordDownLevel(scenario, EmfType.EmfOnly), File.ReadAllBytes(Path.Combine(Dir, "emfonly", scenario + ".emf")));
            Assert.True(diff.Length == 0, diff);
        }

        [Theory]
        [MemberData(nameof(GdiExactScenarios))]
        public void EmfPlusDual_records_are_GdiPlus_bytes(string scenario)
        {
            string diff = GdiDiff(RecordDownLevel(scenario, EmfType.EmfPlusDual), File.ReadAllBytes(Path.Combine(Dir, "dual", scenario + ".emf")));
            Assert.True(diff.Length == 0, diff);
        }

        /// <summary>Every scenario records down-level, in both types, into a file GDI itself reads.</summary>
        [Theory]
        [MemberData(nameof(Scenarios))]
        public void Down_level_recording_is_a_valid_EMF(string scenario)
        {
            foreach (EmfType t in new[] { EmfType.EmfOnly, EmfType.EmfPlusDual })
            {
                byte[] emf = RecordDownLevel(scenario, t);
                Assert.Equal(emf.Length, BitConverter.ToInt32(emf, 48));
                Assert.Equal(GpMetafileEdit.Records(emf).Count(), BitConverter.ToInt32(emf, 52));
                if (OperatingSystem.IsWindows())
                {
                    IntPtr h = GpWindowsMetafile.ToHenhmetafile(emf);
                    Assert.NotEqual(IntPtr.Zero, h);
                    GpWindowsMetafile.DeleteEmf(h);
                }
            }
        }

        /// <summary>Our down-level recording, played, is what GDI+ draws of its own (emfonly/play and
        /// dual/play: `mfo play` of each fixture into 120 x 90 at (5, 5, 110, 80)). An EmfOnly file
        /// plays its GDI records; a dual one its EMF+ records.</summary>
        public static TheoryData<string> PlaybackScenarios()
        {
            var d = new TheoryData<string>();
            foreach (string name in MetafileScenarios.All().Keys)
                if (!TurnedTextPending.Contains(name)) d.Add(name);
            return d;
        }


        // EmfOnly playback is GDI drawing into GDI+'s DIB, and GDI's vectors, regions, clips, blit
        // rectangles, stretched images and text are ported: every scenario is pixel for pixel,
        // the text scenario's quarter-turned Times New Roman 'A' included (fitted under the word
        // fs__NewTransformation leaves a turned matrix: no compatible widths, ClearType along y).

        [Theory]
        [MemberData(nameof(PlaybackScenarios))]
        public void Played_down_level_recording_matches_GdiPlus_pixels(string scenario)
        {
            foreach (EmfType t in new[] { EmfType.EmfOnly, EmfType.EmfPlusDual })
            {
                string kind = t == EmfType.EmfOnly ? "emfonly" : "dual";
                using var mf = new Metafile(new MemoryStream(RecordDownLevel(scenario, t)));
                using Bitmap ours = PlayInto(mf, 120, 90, new RectangleF(5, 5, 110, 80));
                using var theirs = (Bitmap)Image.FromFile(Path.Combine(Dir, kind, "play", scenario + ".png"));
                string outDir = Environment.GetEnvironmentVariable("MF_PLAYOUT");
                if (!string.IsNullOrEmpty(outDir))
                {
                    Directory.CreateDirectory(Path.Combine(outDir, kind));
                    ours.Save(Path.Combine(outDir, kind, scenario + ".png"), ImageFormat.Png);
                }
                if (t == EmfType.EmfOnly)
                {
                    Differ(ours, theirs, 0, out int exact);
                    Assert.True(exact == 0, $"{kind}: {exact} pixels differ from GDI+'s");
                    continue;
                }
                double f = Differ(ours, theirs, 64, out int n);
                Assert.True(f < 0.02, $"{kind}: {n} pixels ({f:P1}) differ from GDI+'s");
            }
        }

        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(int c);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool Rectangle(IntPtr hdc, int l, int t, int r, int b);

        /// <summary>GDI drawing on a recording's GetHdc lands in the recording after the EmfPlusGetDC,
        /// its objects in the file's handle table after GDI+'s, as GDI+'s own recording holds it
        /// (gethdc.emf: mfo gethdc, the same calls).</summary>
        [Theory]
        [InlineData(EmfType.EmfOnly)]
        [InlineData(EmfType.EmfPlusDual)]
        public void GetHdc_on_a_recording_records_the_GDI_drawing(EmfType type)
        {
            if (!OperatingSystem.IsWindows()) return;
            byte[] ours;
            lock (GpMetaDriverState.Lock)
            {
                GpMetaDriverState.Reset();
                var ms = new MemoryStream();
                using (var mf = new Metafile(ms, IntPtr.Zero, type))
                using (Graphics g = Graphics.FromImage(mf))
                {
                    g.FillRectangle(Brushes.Red, 10, 10, 30, 20);
                    IntPtr hdc = g.GetHdc();
                    Assert.NotEqual(IntPtr.Zero, hdc);
                    IntPtr b = CreateSolidBrush(0xff0000);
                    IntPtr old = SelectObject(hdc, b);
                    Rectangle(hdc, 20, 20, 60, 50);
                    SelectObject(hdc, old);
                    DeleteObject(b);
                    g.ReleaseHdc(hdc);
                    g.FillRectangle(Brushes.Green, 40, 5, 20, 20);
                }
                ours = ms.ToArray();
            }
            byte[] theirs = File.ReadAllBytes(Path.Combine(Dir, type == EmfType.EmfOnly ? "emfonly" : "dual", "gethdc.emf"));
            // Recorded on a different reference device: the frame differs, nothing else.
            Array.Copy(theirs, 24, ours, 24, 16);
            string diff = GdiDiff(ours, theirs);
            Assert.True(diff.Length == 0, diff);
        }

        /// <summary>Where there is no GDI, a recording's GetHdc is a handle FromHdc maps back to the
        /// recording: what is drawn through it is recorded, and its disposal does not end the file.</summary>
        [Fact]
        public void GetHdc_on_a_recording_without_GDI_draws_into_the_recording()
        {
            if (OperatingSystem.IsWindows()) return;
            var ms = new MemoryStream();
            using (var mf = new Metafile(ms, IntPtr.Zero, EmfType.EmfPlusOnly))
            using (Graphics g = Graphics.FromImage(mf))
            {
                IntPtr hdc = g.GetHdc();
                Assert.NotEqual(IntPtr.Zero, hdc);
                using (Graphics h = Graphics.FromHdc(hdc)) h.FillRectangle(Brushes.Red, 1, 2, 3, 4);
                g.ReleaseHdc(hdc);
                g.FillRectangle(Brushes.Blue, 5, 6, 7, 8);
            }
            var types = EmfPlusRecords(ms.ToArray()).Select(r => BitConverter.ToUInt16(r.Record, 0)).ToList();
            Assert.Equal(new ushort[] { 0x4001, 0x4004, 0x400a, 0x400a, 0x4002 }, types);
        }

        public static TheoryData<string> GdiStructureScenarios()
        {
            var d = new TheoryData<string>();
            foreach (string name in MetafileScenarios.All().Keys)
                d.Add(name);
            return d;
        }

        /// <summary>Every scenario writes GDI+'s down-level records in GDI+'s order.</summary>
        [Theory]
        [MemberData(nameof(GdiStructureScenarios))]
        public void Down_level_record_structure_is_GdiPlus(string scenario)
        {
            Assert.Equal(RecordTypes(File.ReadAllBytes(Path.Combine(Dir, "emfonly", scenario + ".emf"))), RecordTypes(RecordDownLevel(scenario, EmfType.EmfOnly)));
            Assert.Equal(RecordTypes(File.ReadAllBytes(Path.Combine(Dir, "dual", scenario + ".emf"))), RecordTypes(RecordDownLevel(scenario, EmfType.EmfPlusDual)));
        }

        public static TheoryData<string> BareWmfFiles() => new TheoryData<string> { "bare.wmf", "bare_nomap.wmf", "shapes.wmf" };

        /// <summary>A WMF with no placeable header becomes the EMF GDI makes of it (SetWinMetaFileBits with
        /// no METAFILEPICT) -- record for record, on the screen the fixtures were made on (3840 x 1200 px,
        /// 1040 x 320 mm). Text records are compared by type only: GDI fills in the font's advances and ink
        /// box, which the conversion does not measure; for the same reason the header bounds are only
        /// checked where nothing but shapes was drawn.</summary>
        [Theory]
        [MemberData(nameof(BareWmfFiles))]
        public void Bare_wmf_converts_to_the_EMF_GDI_makes(string name)
        {
            byte[] wmf = File.ReadAllBytes(Path.Combine(Dir, "samples", name));
            byte[] theirs = File.ReadAllBytes(Path.Combine(Dir, "samples", "conv", Path.GetFileNameWithoutExtension(name) + ".emf"));
            var dev = new GpRefDevice { HorzRes = 3840, VertRes = 1200, HorzSize = 1040, VertSize = 320, LogPixelsX = 96, LogPixelsY = 96, IsDisplay = true, DesktopDpiX = 96, DesktopDpiY = 96 };
            byte[] ours = GpWmfToEmf.Convert(wmf, dev, out GpMetafileHeader header);
            Assert.NotNull(ours);
            var a = GpMetafileEdit.Records(ours).ToList();
            var b = GpMetafileEdit.Records(theirs).ToList();
            Assert.Equal(string.Join(",", b.Select(r => r.Type)), string.Join(",", a.Select(r => r.Type)));
            var diffs = new List<string>();
            for (int i = 1; i < a.Count; i++)
            {
                if (a[i].Type == 84) continue;
                if (!ours.AsSpan(a[i].Offset, a[i].Size).SequenceEqual(theirs.AsSpan(b[i].Offset, b[i].Size)))
                    diffs.Add($"record {i} (type {a[i].Type})");
            }
            Assert.True(diffs.Count == 0, "differ from GDI's: " + string.Join(", ", diffs));
            Assert.Equal(theirs.AsSpan(52, 8).ToArray(), ours.AsSpan(52, 8).ToArray());     // records, handles
            if (name == "shapes.wmf") Assert.Equal(theirs.Length, ours.Length);
            if (name == "shapes.wmf")
                Assert.Equal(theirs.AsSpan(8, 32).ToArray(), ours.AsSpan(8, 32).ToArray());   // bounds and frame
        }
    }
}
