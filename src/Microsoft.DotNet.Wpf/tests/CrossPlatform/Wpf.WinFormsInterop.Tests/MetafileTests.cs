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

        [Theory]
        [MemberData(nameof(Scenarios))]
        public void Recorded_EmfPlus_records_are_GdiPlus_bytes(string scenario)
        {
            byte[] ours = Record(scenario, EmfType.EmfPlusOnly);
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

        [Theory]
        [MemberData(nameof(Scenarios))]
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
                GpMetafilePlayer.Enumerate(g, mf, new[] { new PointF(0, 0), new PointF(10, 0), new PointF(0, 10) }, mf.RealBounds, GraphicsUnit.Pixel,
                    (t, f, n, d, cb) => { got.Add($"{((int)t):x} f={f:x} n={n}"); mf.PlayRecord(t, f, n, Copy(d, n)); return true; }, IntPtr.Zero, null);
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

        public static TheoryData<string> SampleFiles() => new TheoryData<string> { "gdi.emf", "gdi_noframe.emf", "placeable.wmf", "placeable1440.wmf", "placeable0.wmf" };

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
            double f = Differ(ours, theirs, 64, out int n);
            Assert.True(f < 0.08, $"{n} pixels ({f:P1}) differ from GDI+'s");
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
