// GPU-rasterization translator: a minimal, System.Drawing.Graphics-shaped drawing surface that
// records into a WgpuSceneRenderer SceneVisual instead of calling libgdiplus. It covers the subset
// of primitives Mono's ThemeWin32Classic uses to paint controls (fill/stroke rectangles, H/V lines,
// text) — proving WinForms control drawing can be expressed as WebGPU scene primitives and drawn by
// WGSL shaders. The full GPU-raster tier generalizes this into a System.Drawing backend (replacing
// gdipFunctions.cs so every control's Graphics calls land here); this file is the vocabulary proof.

using System.Numerics;
using Microsoft.Wpf.Interop.WebGpu.Composition;

namespace WinFormsGpuRaster
{
    internal sealed class SceneGraphics
    {
        private readonly SceneVisual _target;
        public SceneGraphics(SceneVisual target) { _target = target; }

        // Graphics.FillRectangle(brush, x, y, w, h)
        public void FillRectangle(RgbaColor color, float x, float y, float w, float h)
            => _target.Content.Add(new GeometryFill(new RectangleGeometry(new Rect(x, y, w, h)), color));

        // Graphics.DrawLine for the axis-aligned lines themes draw (bevels/edges) = a 1px-thick rect.
        public void DrawHLine(RgbaColor color, float x1, float x2, float y)
            => FillRectangle(color, System.Math.Min(x1, x2), y, System.Math.Abs(x2 - x1) + 1, 1);
        public void DrawVLine(RgbaColor color, float x, float y1, float y2)
            => FillRectangle(color, x, System.Math.Min(y1, y2), 1, System.Math.Abs(y2 - y1) + 1);

        // Graphics.DrawRectangle(pen, x, y, w, h) = 1px outline as four edges.
        public void DrawRectangle(RgbaColor color, float x, float y, float w, float h)
        {
            DrawHLine(color, x, x + w, y);
            DrawHLine(color, x, x + w, y + h);
            DrawVLine(color, x, y, y + h);
            DrawVLine(color, x + w, y, y + h);
        }

        // Graphics.DrawString(text, font, brush, x, y): System.Drawing positions by TOP-LEFT, while a
        // GlyphRunDraw origin is the BASELINE, so drop by the approximate ascent.
        public void DrawString(string text, float x, float yTop, float emSize, RgbaColor color)
            => _target.Content.Add(new GlyphRunDraw(text, new Vector2(x, yTop + emSize * 0.8f), emSize, color));

        // Rough centered text: no measure API here, so approximate the run width. Good enough to
        // prove centered control labels; the real backend measures via the glyph source.
        public void DrawStringCentered(string text, Rect bounds, float emSize, RgbaColor color)
        {
            float approxWidth = text.Length * emSize * 0.5f;
            float x = bounds.X + (bounds.Width - approxWidth) / 2f;
            float y = bounds.Y + (bounds.Height - emSize) / 2f;
            DrawString(text, x, y, emSize, color);
        }
    }
}
