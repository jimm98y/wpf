using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    /// <summary>Does Graphics.MeasureString agree with what Graphics.DrawString DRAWS?
    /// <para>Layout asks the first and the user sees the second, so any gap between them lands
    /// wherever text is centred or right-aligned -- at half the error for a centred run, all of it
    /// for a right-aligned one. It surfaced in the MonthCalendar: its "Today" line is centred by
    /// Windows, our centring put the group twelve pixels left of Windows', and the group had been
    /// measured rather than drawn.</para>
    /// <para>Reported only -- set WPF_MEASURE_REPORT. This is a diagnostic rather than a guard
    /// because the right number to assert is not known yet; what is wanted first is the size and
    /// the shape of the disagreement across sizes and strings.</para></summary>
    public class TextMeasurementTests
    {
        /// <summary>The ink extent of a string drawn through this port, in pixels.</summary>
        private static (int Left, int Right) DrawnExtent(string text, Font font, int width, int height)
        {
            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                // The same format the calendar's Today line is drawn under: no padding, so what
                // comes out is the glyphs and nothing else.
                using var fmt = new StringFormat(StringFormat.GenericTypographic.FormatFlags);
                g.DrawString(text, font, Brushes.Black, 20f, 8f, fmt);
            }
            int left = -1, right = -1;
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                {
                    Color c = bmp.GetPixel(x, y);
                    if (c.R > 200 && c.G > 200 && c.B > 200) continue;
                    if (left < 0) left = x;
                    right = x;
                    break;
                }
            return (left, right);
        }

        [Fact]
        public void MeasureString_AgreesWithWhatIsDrawn()
        {
            string? path = Environment.GetEnvironmentVariable("WPF_MEASURE_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_MEASURE_REPORT to collect this");

            var report = new System.Text.StringBuilder();
            report.AppendLine("== Graphics.MeasureString against the ink it draws (GenericTypographic)");
            report.AppendLine("   size  string                  measured   drawn   measured-drawn");

            string[] texts =
            {
                "Today: 9/1/2026", "Today: 8/31/2026", "September 2026", "August 2026",
                "Hamburgefonstiv", "iiiiiiiiii", "MMMMMMMMMM", "1",
            };

            foreach (float size in new[] { 9f, 12f })
                foreach (string text in texts)
                {
                    using var font = new Font("Segoe UI", size, GraphicsUnit.Pixel);
                    using var bmp = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
                    using var g = Graphics.FromImage(bmp);
                    using var fmt = new StringFormat(StringFormat.GenericTypographic.FormatFlags);
                    float measured = g.MeasureString(text, font, int.MaxValue, fmt).Width;

                    (int left, int right) = DrawnExtent(text, font, 400, 40);
                    int drawn = right < 0 ? 0 : right - left + 1;
                    report.AppendLine($"   {size,4}  {text,-22}  {measured,8:0.0}  {drawn,6}"
                                      + $"  {measured - drawn,14:+0.0;-0.0;0.0}");
                }

            File.AppendAllText(path!, report.ToString());
        }

        /// <summary>Why a MonthCalendar comes out BLANK through DrawToBitmap when the same
        /// control draws correctly in a live window. Reported only -- WPF_MCPROBE_REPORT.</summary>
        [Fact]
        public void MonthCalendar_DrawToBitmap_ProducesInk()
        {
            string? path = Environment.GetEnvironmentVariable("WPF_MCPROBE_REPORT");
            Assert.SkipWhen(string.IsNullOrEmpty(path), "set WPF_MCPROBE_REPORT to collect this");

            var report = new System.Text.StringBuilder();
            using var host = new System.Windows.Forms.Form { ClientSize = new Size(300, 260) };
            var mc = new System.Windows.Forms.MonthCalendar();
            report.AppendLine($"   fresh                 Size {mc.Size}  Client {mc.ClientSize}");
            host.Controls.Add(mc);
            IntPtr unused = host.Handle; unused = mc.Handle;
            report.AppendLine($"   parented, handled     Size {mc.Size}  Client {mc.ClientSize}");
            mc.Width = 230; mc.Height = 170;
            report.AppendLine($"   after Width/Height    Size {mc.Size}  Client {mc.ClientSize}");
            mc.Refresh();
            int paints = 0;
            mc.Paint += (s2, e2) => paints++;
            int Ink()
            {
                using var b = new Bitmap(mc.Width, mc.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(b)) g.Clear(Color.White);
                mc.DrawToBitmap(b, new Rectangle(0, 0, b.Width, b.Height));
                int n = 0;
                for (int y = 0; y < b.Height; y++)
                    for (int x = 0; x < b.Width; x++)
                    {
                        Color c = b.GetPixel(x, y);
                        if (c.R < 120 && c.G < 120 && c.B < 120) n++;
                    }
                return n;
            }
            string Geom()
            {
                var t = mc.GetType();
                string One(string n)
                {
                    var f = t.GetField(n, System.Reflection.BindingFlags.NonPublic
                                          | System.Reflection.BindingFlags.Instance);
                    if (f != null) return n + "=" + f.GetValue(mc);
                    var pr = t.GetProperty(n, System.Reflection.BindingFlags.NonPublic
                                              | System.Reflection.BindingFlags.Instance
                                              | System.Reflection.BindingFlags.Public);
                    return pr != null ? n + "=" + pr.GetValue(mc) : n + "=?";
                }
                return One("date_cell_size") + "  " + One("SingleMonthSize") + "  " + One("title_size");
            }
            report.AppendLine($"   before Show   Visible {mc.Visible,-5}  ink {Ink()}  Paint fired {paints}x");

            // A direct OnPaint through reflection was tried here and proved nothing:
            // GetMethod with NonPublic does not return a protected member declared on a BASE
            // type, so the lookup returned null and the null-conditional call did nothing at
            // all -- which reads exactly like 'OnPaint drew nothing'. Removed rather than left
            // to mislead.
            host.Show();
            System.Windows.Forms.Application.DoEvents();
            mc.Refresh();
            report.AppendLine($"   after Show    Visible {mc.Visible,-5}  ink {Ink()}  Paint fired {paints}x");

            foreach (var size in new[] { new Size(230, 170), mc.Size })
            {
                using var bmp = new Bitmap(Math.Max(size.Width, 1), Math.Max(size.Height, 1),
                                           PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp)) g.Clear(Color.White);
                mc.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                int dark = 0;
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        if (c.R < 120 && c.G < 120 && c.B < 120) dark++;
                    }
                report.AppendLine($"   DrawToBitmap {size.Width}x{size.Height}  ink pixels {dark}");
            }
            host.Dispose();
            File.AppendAllText(path!, report.ToString());
        }
    }
}
