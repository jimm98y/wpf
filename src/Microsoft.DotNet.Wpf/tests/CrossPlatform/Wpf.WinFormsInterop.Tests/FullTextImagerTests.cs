// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// GDI+'s text layout, held to gdiplus.dll itself: Fixtures/Text/fti_oracle.jobs.txt is a battery of
// DrawString / MeasureString / MeasureCharacterRanges / GraphicsPath.AddString calls -- faces, sizes,
// styles, hints, every StringFormat flag, alignments, trimming, hot keys, tabs, wrapping, right to
// left, vertical, scaled and turned transforms -- and fti_oracle.native.txt what native arm64 GDI+
// answered (its DrawPlacedGlyphs and DrawLines hooked: each glyph run's render mode, em, glyphs and
// device origins; each decoration line; the measured sizes; the ranges' region scans; the path's
// points). The port reports the same through GpTextTrace; every job must agree to the last bit.
// The jobs are a sample of the batteries the port matches (scratchpad fto harness: gen/genmr,
// fton on Windows, ftoo for the port). The last fifty (round r9) are right-to-left AddString at
// 12..120 px (a path's right-to-left glyph sits at the left of its TRACKED cell) and tab stops
// (past the last stop the next multiple of the increment from the line's start; no increment
// moves a tab one ideal unit), recorded with FillPath hooked.
//
// job: op|base|face|size|unit|style|hint|flags|align|lalign|trim|hotkey|tabs|digits|x,y,w,h|xform|ranges|text
// (op P draws the string unhooked and logs an FNV-1a digest of the 400x160 BGRA bitmap: "PX <hex>".)
//
// gasp_oracle.jobs.txt / .native.txt are the same for the sizes where the faces' 'gasp' tables decline
// grid fitting (GRIDFIT or SYMMETRIC_GRIDFIT clear) or their preps inhibit it: every TextRenderingHint,
// DrawString's glyph runs and pixels, MeasureString, AddString, upright and under uniform scales, and
// typographic strings whose ends overhang (the black-box test reads the GDI-classic side bearings).
// GDI+ fits all of them: its metrics are DirectWrite's GDI_CLASSIC / GDI_NATURAL measure, which reads
// no gasp bit (TrueTypeFont.GdiClassicFit). Recorded one job per process: GDI+'s realization cache
// makes an AntiAliasGridFit string's render mode depend on what the process drew before: its
// lookup (FastTextImager::DrawString @180038298) keys on the notional-to-device matrix and the flag
// word, while IsGrayscaleFontSize was asked the WORLD em, so Tahoma 8px AntiAliasGridFit under a 2x
// scale (grey at 8) leaves a grey realization that a later upright 16px string reuses.
// The jobs after the gasp ones cover what round r8 closed: the non-ClearType hints under an
// anisotropic scale (GDI-classic metrics and glyphs fitted at each axis' ppem, no hdmx), the
// unfitted SingleBitPerPixel scan control (the prep run at MPPEM = units per em), MS Gothic's
// strike-size side bearings, and the path realizations either side of SwitchToPath's box limits
// (native GDI+'s FullTextImager::DrawGlyphs GpGraphics::FillPath, hooked: "FILLPATH").
//

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Drawing.WebGpuBackend.Gdip;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class FullTextImagerTests
    {
        static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Text");
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        const int W = 400, H = 160;

        [ThreadStatic] static StringBuilder t_log;
        [ThreadStatic] static bool t_hooking;
        static readonly object s_install = new object();
        static bool s_installed;

        static void Install()
        {
            lock (s_install)
            {
                if (s_installed) return;
                GpTextTrace.Placed = (mode, em, g, xy) => { if (t_hooking) Placed(mode, em, g, xy); };
                GpTextTrace.Line = (w, u, xy) => { if (t_hooking) Line(w, u, xy); };
                GpTextTrace.PathFilled = () => { if (t_hooking) t_log.Append("FILLPATH\n"); };
                s_installed = true;
            }
        }

        static void Placed(int mode, float em, ushort[] glyphs, float[] xy)
        {
            StringBuilder log = t_log;
            log.AppendFormat(CI, "G n={0} em={1:R} mode={2} g=", glyphs.Length, em, mode);
            for (int i = 0; i < glyphs.Length; i++) log.Append(i == 0 ? "" : ",").Append(glyphs[i]);
            log.Append(" o=");
            for (int i = 0; i < glyphs.Length; i++) log.AppendFormat(CI, "{0}{1:R},{2:R}", i == 0 ? "" : " ", xy[2 * i], xy[2 * i + 1]);
            log.Append('\n');
        }

        static void Line(float width, int unit, float[] xy)
        {
            StringBuilder log = t_log;
            log.AppendFormat(CI, "L w={0:R} u={1}", width, unit);
            for (int i = 0; i < xy.Length; i += 2) log.AppendFormat(CI, " {0:R},{1:R}", xy[i], xy[i + 1]);
            log.Append('\n');
        }

        static string Unesc(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == 'u') { sb.Append((char)Convert.ToInt32(s.Substring(i + 2, 4), 16)); i += 5; }
                else if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == 't') { sb.Append('\t'); i++; }
                else if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == 'n') { sb.Append('\n'); i++; }
                else if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] == 'r') { sb.Append('\r'); i++; }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        static float F(string s) => float.Parse(s, CI);
        static int I(string s) => s.StartsWith("0x", StringComparison.Ordinal) ? Convert.ToInt32(s.Substring(2), 16) : int.Parse(s, CI);

        /// <summary>One job, logged as the oracle logs it.</summary>
        static string Run(string ln)
        {
            var log = new StringBuilder();
            t_log = log;
            try { One(ln, log); }
            catch (Exception ex) { log.Append("EX " + ex.GetType().Name + " " + ex.Message.Replace('\n', ' ') + "\n"); }
            finally { t_hooking = false; t_log = null; }
            return log.ToString();
        }

        static void One(string ln, StringBuilder log)
        {
            string[] p = ln.Split(new[] { '|' }, 18);
            string op = p[0];
            string text = Unesc(p[17]);
            StringFormat sf = p[1] switch
            {
                "typo" => (StringFormat)StringFormat.GenericTypographic.Clone(),
                "gdef" => (StringFormat)StringFormat.GenericDefault.Clone(),
                "null" => null,
                _ => new StringFormat(),
            };
            if (sf != null)
            {
                int fl = I(p[7]);
                if (fl != -1) sf.FormatFlags = (StringFormatFlags)fl;
                sf.Alignment = (StringAlignment)I(p[8]);
                sf.LineAlignment = (StringAlignment)I(p[9]);
                if (p[10] != "-") sf.Trimming = (StringTrimming)I(p[10]);
                if (p[11] != "-") sf.HotkeyPrefix = (HotkeyPrefix)I(p[11]);
                if (p[12] != "-")
                {
                    var t = p[12].Split(';');
                    var st = new float[t.Length - 1];
                    for (int i = 1; i < t.Length; i++) st[i - 1] = F(t[i]);
                    sf.SetTabStops(F(t[0]), st);
                }
                if (p[13] != "-")
                {
                    var d = p[13].Split(';');
                    sf.SetDigitSubstitution(I(d[0]), (StringDigitSubstitute)I(d[1]));
                }
            }
            var r = p[14].Split(',');
            var rect = new RectangleF(F(r[0]), F(r[1]), F(r[2]), F(r[3]));
            Matrix m = null;
            if (p[15] != "-")
            {
                var x = p[15].Split(',');
                m = new Matrix(F(x[0]), F(x[1]), F(x[2]), F(x[3]), F(x[4]), F(x[5]));
            }
            using var font = new Font(p[2], F(p[3]), (FontStyle)I(p[5]), (GraphicsUnit)I(p[4]));
            using var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.White);
            g.TextRenderingHint = (TextRenderingHint)I(p[6]);
            if (m != null) g.Transform = m;
            switch (op)
            {
                case "P":
                {
                    if (sf == null) g.DrawString(text, font, Brushes.Black, rect);
                    else g.DrawString(text, font, Brushes.Black, rect, sf);
                    var d = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    var buf = new byte[W * H * 4];
                    System.Runtime.InteropServices.Marshal.Copy(d.Scan0, buf, 0, buf.Length);
                    bmp.UnlockBits(d);
                    ulong h = 1469598103934665603UL;
                    foreach (byte b in buf) { h ^= b; h *= 1099511628211UL; }
                    log.Append("PX ").Append(h.ToString("x16")).Append('\n');
                    break;
                }
                case "D":
                    t_hooking = true;
                    try
                    {
                        if (sf == null) g.DrawString(text, font, Brushes.Black, rect);
                        else g.DrawString(text, font, Brushes.Black, rect, sf);
                    }
                    finally { t_hooking = false; }
                    break;
                case "M":
                {
                    int chars = -1, linesFilled = -1;
                    SizeF s = sf == null ? g.MeasureString(text, font, rect.Size) : g.MeasureString(text, font, rect.Size, sf, out chars, out linesFilled);
                    log.AppendFormat(CI, "M {0:R} {1:R} {2} {3}\n", s.Width, s.Height, chars, linesFilled);
                    break;
                }
                case "R":
                {
                    var rs = p[16].Split(';');
                    var cr = new CharacterRange[rs.Length];
                    for (int i = 0; i < rs.Length; i++) { var q = rs[i].Split(':'); cr[i] = new CharacterRange(I(q[0]), I(q[1])); }
                    sf ??= new StringFormat();
                    sf.SetMeasurableCharacterRanges(cr);
                    Region[] regs = g.MeasureCharacterRanges(text, font, rect, sf);
                    for (int i = 0; i < regs.Length; i++)
                    {
                        log.Append('R').Append(i);
                        using var mm = new Matrix();
                        foreach (RectangleF b in regs[i].GetRegionScans(mm))
                            log.AppendFormat(CI, " {0:R},{1:R},{2:R},{3:R}", b.X, b.Y, b.Width, b.Height);
                        log.Append('\n');
                        regs[i].Dispose();
                    }
                    break;
                }
                case "A":
                {
                    using var path = new GraphicsPath();
                    path.AddString(text, font.FontFamily, (int)font.Style, font.Size, rect, sf);
                    PointF[] pts = path.PathPoints; byte[] ty = path.PathTypes;
                    log.Append("A ").Append(pts.Length);
                    for (int i = 0; i < pts.Length; i++) log.AppendFormat(CI, " {0}:{1:R},{2:R}", ty[i], pts[i].X, pts[i].Y);
                    log.Append('\n');
                    break;
                }
            }
        }

        static List<(string Job, string Log)> Native(string file = "fti_oracle.native.txt")
        {
            var list = new List<(string, string)>();
            string job = null; var sb = new StringBuilder();
            foreach (string line in File.ReadAllLines(Path.Combine(Dir, file), Encoding.UTF8))
            {
                if (line.StartsWith("J ", StringComparison.Ordinal))
                {
                    if (job != null) list.Add((job, sb.ToString()));
                    job = line.Split(new[] { ' ' }, 3)[2]; sb.Clear();
                }
                else if (job != null) sb.Append(line).Append('\n');
            }
            if (job != null) list.Add((job, sb.ToString()));
            return list;
        }

        /// <summary>Every job lays out, measures and adds to a path exactly as gdiplus.dll does.</summary>
        [Fact]
        public void Text_layout_matches_GdiPlus() => Battery("fti_oracle.native.txt");

        /// <summary>The same at the sizes the faces' 'gasp' (or prep) declines grid fitting, pixels
        /// included: GDI+ fits there all the same.</summary>
        [Fact]
        public void Text_at_gasp_declined_sizes_matches_GdiPlus() => Battery("gasp_oracle.native.txt");

        static void Battery(string file)
        {
            if (!OperatingSystem.IsWindows()) return;   // the battery's faces are Windows'
            Install();
            var bad = new StringBuilder();
            int failed = 0, n = 0;
            foreach ((string job, string native) in Native(file))
            {
                n++;
                string ours = Run(job);
                if (ours == native) continue;
                failed++;
                if (failed <= 5) bad.Append("job ").AppendLine(job).Append("  gdi+ ").Append(native.Replace("\n", "\n       ")).AppendLine().Append("  ours ").Append(ours.Replace("\n", "\n       ")).AppendLine();
            }
            Assert.True(failed == 0, $"{failed} of {n} jobs differ from GDI+'s:\n{bad}");
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern int GetACP();

        /// <summary>The face a LOGFONT realizes to is GDI's mapper's (GpFontMapper), for the '@'
        /// vertical faces GDI+'s down-level text asks for above all: Fixtures/Text/font_mapper.gdi.txt
        /// is what win32k answered (the realized face's IFIMETRICS family, the charset) for vertical
        /// and random requests on the machine the fixture was recorded on.</summary>
        [Fact]
        public void Font_mapper_matches_Gdi()
        {
            // The answers are the recording machine's: its fonts, link table and ANSI code page.
            if (!OperatingSystem.IsWindows() || GetACP() != 1250) return;
            var bad = new StringBuilder();
            int failed = 0, n = 0;
            foreach (string line in File.ReadAllLines(Path.Combine(Dir, "font_mapper.gdi.txt"), Encoding.UTF8))
            {
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                n++;
                string req = line.Substring(0, line.IndexOf(" => ", StringComparison.Ordinal));
                string[] p = req.Split('|');
                GpFontMapper.Match m = GpFontMapper.Map(p[0], (byte)I(p[1]), (byte)I(p[2]), I(p[3]), p[4] != "0");
                string ours = req + " => " + (m == null ? "?" : m.Face.Family + " cs=" + m.Charset);
                if (ours == line) continue;
                failed++;
                if (failed <= 10) bad.Append("  gdi  ").AppendLine(line).Append("  ours ").AppendLine(ours);
            }
            Assert.True(failed == 0, $"{failed} of {n} requests map differently from GDI's:\n{bad}");
        }
    }
}
