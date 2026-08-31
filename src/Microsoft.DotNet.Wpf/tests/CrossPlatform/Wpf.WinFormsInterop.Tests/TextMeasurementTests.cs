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
    }
}
