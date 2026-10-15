// The resolution this process's screen has, as GDI+ sees it: what Font.Height, a control's
// Graphics.DpiX and a new Bitmap's resolution are measured at.

using System.ComponentModel;

namespace System.Drawing.WebGpuBackend
{
    /// <summary>
    /// The screen's resolution for this process: 96 for an application that is not DPI-aware (the
    /// host then scales its whole surface to the display), the system DPI for one that is
    /// (System.Windows.Forms sets it when it resolves the application's high-DPI mode, before the
    /// first window). Stock GDI+ reads it from the screen DC, whose LOGPIXELS a per-monitor-aware
    /// process sees as the primary display's DPI at startup.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class DisplayDpi
    {
        private static float s_x = 96f, s_y = 96f;

        /// <summary>Horizontal pixels per inch.</summary>
        public static float X => s_x;

        /// <summary>Vertical pixels per inch.</summary>
        public static float Y => s_y;

        /// <summary>Set the screen's resolution (System.Windows.Forms, once, before any window).</summary>
        public static void Set(float dpi)
        {
            if (!(dpi > 0)) dpi = 96f;
            s_x = s_y = dpi;
        }
    }
}
