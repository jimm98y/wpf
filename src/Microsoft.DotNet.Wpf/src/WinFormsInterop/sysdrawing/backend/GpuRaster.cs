// Public entry point for the GPU-rasterization seam. A paint host (the XplatUIWebGpu driver) brackets
using System;
// its drawing with Begin/EndScene on the Graphics it hands to the control/theme: Begin attaches a
// recorder so the drawing verbs build a WebGPU scene (no GPU work — pure data), and EndScene returns
// that scene (as object, so callers need no WgpuInterop reference) for the present path to render.

namespace System.Drawing.WebGpuBackend
{
    public static class GpuRaster
    {
        /// <summary>Route <paramref name="g"/>'s subsequent draw verbs into a WebGPU scene.</summary>
        public static void Begin(Graphics g)
        {
            if (g != null) g.GpuRecorder = new SceneRecorder();
        }

        /// <summary>A recording-only Graphics with NO libgdiplus backing (nativeObject == 0): the
        /// drawing verbs record a WebGPU scene and the non-drawing gdip ops (clip/dispose/…) are
        /// no-ops. Lets the driver paint a window with zero libgdiplus (no backing Bitmap / FromImage)
        /// — a browser prerequisite.</summary>
        public static Graphics NewRecording()
        {
            var g = new Graphics(IntPtr.Zero);
            g.GpuRecorder = new SceneRecorder();
            return g;
        }

        /// <summary>A recording Graphics that calls <paramref name="onDisposed"/> when it is
        /// disposed, before the recorder is detached -- the hook's chance to do something with what
        /// was drawn. Without it a caller who draws outside a paint cycle (CreateGraphics) records
        /// into a scene that is simply thrown away.</summary>
        public static Graphics NewRecording(Action<Graphics> onDisposed)
        {
            var g = NewRecording();
            g.DisposeHook = onDisposed;
            return g;
        }

        /// <summary>Detach the recorder and return the scene it recorded (a WgpuInterop SceneVisual,
        /// boxed as object). Null if Begin wasn't called.</summary>
        public static object EndScene(Graphics g)
        {
            if (g?.GpuRecorder is SceneRecorder r)
            {
                g.GpuRecorder = null;
                return r.Scene;
            }
            return null;
        }

        /// <summary>Detach the recorder without returning its scene (the scene was already captured
        /// elsewhere, e.g. a double-buffer blit).</summary>
        public static void Cancel(Graphics g)
        {
            if (g != null) g.GpuRecorder = null;
        }

        /// <summary>Whether a recorder is currently attached (GPU-raster mode is active).</summary>
        public static bool IsActive(Graphics g) => g?.GpuRecorder != null;

        /// <summary>Measure a text run (managed, no libgdiplus) with the font the renderer draws with,
        /// so DrawString alignment can centre exactly. emPx = pixel em size.</summary>
        public static void MeasureText(string text, float emPx, out float width, out float height)
            => TextMetrics.Measure(text, emPx, out width, out height);

        /// <summary>Measure in a style (1 = bold, 2 = italic), so bold text is laid out at bold
        /// widths.</summary>
        public static void MeasureText(string text, float emPx, int simulations, out float width, out float height)
            => TextMetrics.Measure(text, emPx, simulations, null, out width, out height);

        /// <summary>Measure in a family and style, so what is measured is what will be drawn.</summary>
        public static void MeasureText(string text, float emPx, int simulations, string family,
                                       out float width, out float height)
            => TextMetrics.Measure(text, emPx, simulations, family, out width, out height);
    }
}
