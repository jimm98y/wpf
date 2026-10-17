// Drawing through a window DC (GetDC, GetWindowDC, BeginPaint outside WinForms' paint) reaches a
// window as a picture: WpfMac's window DC collects what GDI drew into a bitmap and lays it over the
// window (CreateGraphics -> XplatUIWebGpu.MergeIntoWindowScene). Each such drawing was APPENDED to
// the window's scene, and only a real WM_PAINT ever replaced it. DotNetBar repaints a dock panel's
// non-client bitmap through GetWindowDC on every size change without the panel ever repainting its
// client, so every step of a resize added a 1.7 MB picture to that one scene: three move/resize
// cycles of GraphWorX held 600 MB of them, and the user's session grew to 1.5 GB.
//
// On a real screen those drawings land in the same pixels, the newer over the older. So a picture
// drawn over the picture a window already carries is folded into it: one picture, the union of the
// two, the newer pixels where it drew anything. Memory is then bounded by the window's size.

using System;
using System.Numerics;

namespace Microsoft.Wpf.Interop.WebGpu.Composition
{
    internal static class ImageLayers
    {
        /// <summary>Whether <paramref name="v"/> is exactly one unscaled picture at whole-pixel device
        /// position (x, y) -- nothing else drawn, nothing transforming or clipping it into something
        /// that is not that rectangle of pixels.</summary>
        internal static bool TryFlat(SceneVisual v, out int x, out int y, out ImageBrush image)
        {
            x = y = 0; image = null!;
            Vector2 at = Vector2.Zero;
            SceneVisual cur = v;
            while (true)
            {
                if (!cur.Transform.IsIdentity || cur.Opacity != 1.0 || cur.ClipGeometry != null || cur.Effect != null
                    || cur.OpacityMask != null || cur.Snapshot != null) return false;
                at += cur.Offset;
                if (cur.Content.Count == 1 && cur.Children.Count == 0) break;
                if (cur.Content.Count != 0 || cur.Children.Count != 1) return false;
                if (cur.Clip != null) return false;
                cur = cur.Children[0];
            }
            if (cur.Content[0] is not GeometryFill { Geometry: RectangleGeometry rg, Brush: ImageBrush ib } f
                || f.SourceCopy || ib.SourceVisual != null || ib.TileMode != TileMode.None || ib.Opacity != 1f
                || ib.PixelsRgba.Length < ib.PixelWidth * ib.PixelHeight * 4) return false;
            Rect r = rg.Rect;
            if (r.Width != ib.PixelWidth || r.Height != ib.PixelHeight) return false;
            float fx = at.X + r.X, fy = at.Y + r.Y;
            if (fx != MathF.Floor(fx) || fy != MathF.Floor(fy)) return false;
            // A clip on the picture's own visual must leave all of it.
            if (cur.Clip is { } c && !(c.X <= r.X && c.Y <= r.Y && c.X + c.Width >= r.X + r.Width && c.Y + c.Height >= r.Y + r.Height))
                return false;
            x = (int)fx; y = (int)fy; image = ib;
            return true;
        }

        /// <summary>One picture holding <paramref name="older"/> with <paramref name="newer"/> over it:
        /// the union of their rectangles, the newer one's pixels wherever it drew (alpha above zero).</summary>
        internal static SceneVisual Combine(int ox, int oy, ImageBrush older, int nx, int ny, ImageBrush newer)
        {
            int ux = Math.Min(ox, nx), uy = Math.Min(oy, ny);
            int ur = Math.Max(ox + older.PixelWidth, nx + newer.PixelWidth), ub = Math.Max(oy + older.PixelHeight, ny + newer.PixelHeight);
            int uw = ur - ux, uh = ub - uy;
            var px = new byte[uw * uh * 4];
            Blit(older.PixelsRgba, older.PixelWidth, older.PixelHeight, px, uw, ox - ux, oy - uy, overwriteAll: true);
            Blit(newer.PixelsRgba, newer.PixelWidth, newer.PixelHeight, px, uw, nx - ux, ny - uy, overwriteAll: false);
            var v = new SceneVisual();
            v.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(ux, uy, uw, uh)), new ImageBrush(px, uw, uh)));
            return v;
        }

        private static void Blit(byte[] src, int sw, int sh, byte[] dst, int dw, int dx, int dy, bool overwriteAll)
        {
            for (int y = 0; y < sh; y++)
            {
                int s = y * sw * 4, d = ((dy + y) * dw + dx) * 4;
                if (overwriteAll) { Buffer.BlockCopy(src, s, dst, d, sw * 4); continue; }
                for (int x = 0; x < sw; x++, s += 4, d += 4)
                {
                    if (src[s + 3] == 0) continue;
                    dst[d] = src[s]; dst[d + 1] = src[s + 1]; dst[d + 2] = src[s + 2]; dst[d + 3] = src[s + 3];
                }
            }
        }
    }
}
