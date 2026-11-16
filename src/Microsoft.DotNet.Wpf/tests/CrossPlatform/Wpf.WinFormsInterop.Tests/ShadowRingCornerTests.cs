// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// A popup shadow painted as GDI+ paints it: a band cleared opaque black, then four one-pixel rounded
// rings of falling alpha drawn over it, anti-aliased, with SourceCopy. Whatever no ring reaches stays
// black. The expected alphas are gdiplus.dll's (.NET 10, Windows 11 ARM64) for exactly this code.
//

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using Xunit;

namespace Wpf.WinFormsInterop.Tests
{
    public class ShadowRingCornerTests
    {
        private static GraphicsPath RingPath(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            p.AddArc(r.Right - rad, r.Y, rad, rad, 270, 90);
            p.AddLine(r.Right, r.Y + rad, r.Right, r.Bottom - rad);
            p.AddArc(r.Right - rad, r.Bottom - rad, rad, rad, 0, 90);
            p.AddLine(r.Right - rad, r.Bottom, r.X, r.Bottom);
            p.AddLine(r.X, r.Bottom, r.X, r.Top);
            return p;
        }

        private static string Paint(int w, int h)
        {
            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                using var region = new Region(new Rectangle(0, 0, w, h));
                using var path = new GraphicsPath();
                var r = new Rectangle(0, 0, w, h);
                path.StartFigure();
                path.AddLine(r.Right - 5, r.Top, r.Right, r.Top);
                path.AddLine(r.Right, r.Top, r.Right, r.Top + 5);
                path.AddLine(r.Right, r.Top + 5, r.Right - 5, r.Top);
                path.CloseFigure();
                path.StartFigure();
                path.AddLine(r.Left, r.Bottom - 5 - 1, r.Left, r.Bottom);
                path.AddLine(r.Left, r.Bottom, r.Left + 5, r.Bottom);
                path.AddLine(r.Left + 5, r.Bottom, r.Left, r.Bottom - 5);
                path.CloseFigure();
                path.StartFigure();
                path.AddRectangle(new Rectangle(0, 0, r.Width - 5, r.Height - 5));
                path.CloseFigure();
                region.Xor(path);
                g.SetClip(region, CombineMode.Replace);
                g.Clear(Color.Transparent);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Black);
                r.Width--; r.Height--;
                int[] alphas = { 14, 43, 84, 113 };
                for (int i = 0; i < 4; i++)
                {
                    using var pen = new Pen(Color.FromArgb(alphas[i], Color.Black), 1f);
                    using GraphicsPath gp = RingPath(r, 4 - i);
                    g.DrawPath(pen, gp);
                    r.Inflate(-1, -1);
                }
            }
            var sb = new StringBuilder();
            for (int y = h - 8; y < h; y++)
            {
                for (int x = w - 8; x < w; x++) sb.Append(bmp.GetPixel(x, y).A).Append(x < w - 1 ? " " : "");
                sb.Append('\n');
            }
            return sb.ToString();
        }

        [Fact]
        public void TheRingsCoverTheBottomRightCornerAsInGdiPlus()
        {
            Assert.Equal(
                "0 0 0 255 113 84 43 14\n" +
                "0 0 0 255 113 84 43 14\n" +
                "0 0 0 255 113 84 43 14\n" +
                "255 255 255 255 113 84 43 14\n" +
                "113 113 113 113 71 84 43 14\n" +
                "84 84 84 84 84 42 39 14\n" +
                "43 43 43 43 43 39 12 10\n" +
                "14 14 14 14 14 14 11 2\n",
                Paint(40, 30));
        }
    }
}
