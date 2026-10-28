// The frame of a top-level window on a head with no window manager to draw one: the browser now, iOS
// and Android next. On Windows the DWM draws a dialog's title bar and borders round the client, and
// moves and closes the window when they are used; on macOS AppKit does. Where the screen is one surface
// the application has to itself -- a page's canvas, a phone's view -- nothing does, and a dialog came up
// as a bare client area that could be neither moved nor closed.
//
// So the host draws it, Windows 11's: the frame is the one the driver gives the window
// (XplatUIWebGpu.Windows11Frame, for the windows that have one there, FramedWindow), so a dialog's
// client and outer size are what they are on Windows and the designer's layout is kept. Its sides and
// foot are the resize margin Windows keeps outside the visible edge -- transparent, under a soft shadow
// -- inside them a one-pixel border, and across the top the caption with the title and the close button.
// The chrome is recorded as a scene of its own (the WinForms Graphics, GpuRaster) and presented under
// the window's, and it answers WM_NCHITTEST's question for a point (HitTest); the host acts on the
// answer as DefWindowProc would: HTCAPTION moves the window, HTCLOSE sends SC_CLOSE.

extern alias WindowsBase;

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using BrowserWindow = WindowsBase::MS.Internal.Interop.BrowserWindow;

namespace System.Windows.Forms
{
	internal sealed class WindowChrome
	{
		internal const int HTCLIENT = 1, HTCAPTION = 2, HTCLOSE = 20, HTBORDER = 18, HTNOWHERE = 0;

		private readonly Form _form;
		private int _hover = HTNOWHERE, _pressed = HTNOWHERE;

		internal WindowChrome (Form form) => _form = form;

		// The screen's own window: the first form a frame is asked for, which the head shows filling the
		// screen with no frame, as the first window the page's window manager is given fills the page --
		// unless the page has its window already: a WPF application's main window (Workbench's), and then
		// every form is a dialog over it. The first form was taken for the screen's all the same, and
		// Workbench's login dialog had no title bar, its file and SQL configuration dialogs stood at 0,0
		// and could be neither dragged nor clicked.
		private static Form s_screenWindow;

		/// <summary>XplatUIWebGpu.FramedWindow on a head whose screen is the main window: a form other
		/// than that one.</summary>
		internal static bool IsFramed (Control control)
		{
			if (control is not Form form || form.Parent != null)
				return false;
			if (s_screenWindow == null || s_screenWindow.IsDisposed) {
				if (BrowserWindow.PageHasMainWindow)
					return true;
				s_screenWindow = form;
			}
			return form != s_screenWindow;
		}

		/// <summary>The window's frame (its outer rectangle less its client), Empty when it has none.</summary>
		internal Padding Frame => _form.IsHandleCreated ? XplatUIWebGpu.GetInstance ().FrameOf (_form.Handle) : Padding.Empty;

		internal bool HasFrame => Frame != Padding.Empty;

		// The window's DPI over 96: the caption's buttons and insets are Windows 11's at 96 dpi, scaled
		// (the frame itself is the driver's for the window's DPI).
		private float Dpi => _form.IsHandleCreated ? XplatUIWebGpu.GetInstance ().WindowDpi (_form.Handle) / 96f : 1f;
		private int Px (float v) => (int)Math.Round (v * Dpi);

		// Windows 11: the resize margin outside the visible edge, a 46-pixel close button
		private int Margin => Math.Max (0, Frame.Left - 1);
		private Rectangle Visible (Size window) => new Rectangle (Margin, 0, window.Width - 2 * Margin, window.Height - Margin);
		private Rectangle Caption (Size window)
		{
			Rectangle v = Visible (window);
			return new Rectangle (v.Left + 1, v.Top + 1, v.Width - 2, Math.Max (0, Frame.Top - 1));
		}
		private Rectangle CloseBox (Size window)
		{
			Rectangle c = Caption (window);
			return _form.ControlBox ? new Rectangle (c.Right - Px (46), c.Top, Px (46), c.Height) : Rectangle.Empty;
		}

		/// <summary>WM_NCHITTEST for a point in window coordinates (0,0 the outer top-left).</summary>
		internal int HitTest (Point p, Size window)
		{
			Padding f = Frame;
			if (f == Padding.Empty)
				return HTCLIENT;
			if (new Rectangle (f.Left, f.Top, window.Width - f.Horizontal, window.Height - f.Vertical).Contains (p))
				return HTCLIENT;
			if (CloseBox (window).Contains (p))
				return HTCLOSE;
			if (Caption (window).Contains (p))
				return HTCAPTION;
			return Visible (window).Contains (p) ? HTBORDER : HTNOWHERE;
		}

		/// <summary>The frame part the button went down on (HTCLOSE while the close button holds the mouse).</summary>
		internal int Pressed => _pressed;

		/// <summary>The pointer over the frame (<paramref name="hit"/>) and whether the button is down
		/// on it; true when that changes what the chrome shows.</summary>
		internal bool Track (int hit, int pressed)
		{
			if (hit == _hover && pressed == _pressed)
				return false;
			_hover = hit;
			_pressed = pressed;
			return true;
		}

		/// <summary>The chrome as a scene (boxed SceneVisual), for a window <paramref name="window"/> in
		/// size, active or not.</summary>
		internal object Paint (Size window, bool active)
		{
			Graphics g = System.Drawing.WebGpuBackend.GpuRaster.NewRecording ();
			try {
				Padding f = Frame;
				Rectangle v = Visible (window);
				// the shadow, in the resize margin under the visible edge: a ring a pixel wide per step out,
				// each pixel in one ring only. Outlined rectangles painted their corners twice (and their
				// top rows over the inner rings' sides), and the alpha that doubled there drew a dark
				// diagonal out of every bottom corner.
				for (int i = Margin; i > 0; i--) {
					int a = (int)(28.0 * (Margin - i + 1) / Math.Max (1, Margin) * (Margin - i + 1) / Math.Max (1, Margin));
					using var shadow = new SolidBrush (Color.FromArgb (a, 0, 0, 0));
					int top = v.Top + Math.Min (i, 2), bottom = v.Bottom + i - 1;
					g.FillRectangle (shadow, v.Left - i, top, 1, bottom - top + 1);
					g.FillRectangle (shadow, v.Right + i - 1, top, 1, bottom - top + 1);
					g.FillRectangle (shadow, v.Left - i + 1, bottom, v.Width + 2 * i - 2, 1);
				}
				// the caption and the edge, as Windows 11 draws them with accent colour off
				using (var caption = new SolidBrush (active ? Color.White : Color.FromArgb (243, 243, 243)))
					g.FillRectangle (caption, v.Left, v.Top, v.Width, f.Top);
				using (var sides = new SolidBrush (_form.BackColor))
					g.FillRectangle (sides, v.Left, f.Top, v.Width, v.Height - f.Top);
				int line = Math.Max (1, Px (1));
				using (var edge = new Pen (active ? Color.FromArgb (120, 120, 120) : Color.FromArgb (170, 170, 170), line))
					g.DrawRectangle (edge, v.Left + (line - 1) / 2f, v.Top + (line - 1) / 2f, v.Width - line, v.Height - line);

				Rectangle c = Caption (window);
				Rectangle close = CloseBox (window);
				// the title: the caption font, from the left edge to the buttons
				Font font = SystemFonts.CaptionFont ?? Control.DefaultFont;
				var title = new Rectangle (c.Left + Px (11), c.Top, (close.IsEmpty ? c.Right : close.Left) - c.Left - Px (11), c.Height);
				TextRenderer.DrawText (g, _form.Text, font, title, active ? Color.Black : Color.FromArgb (150, 150, 150),
					TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

				if (!close.IsEmpty) {
					bool hot = _hover == HTCLOSE && (_pressed == HTNOWHERE || _pressed == HTCLOSE);
					if (hot) {
						using var red = new SolidBrush (_pressed == HTCLOSE ? Color.FromArgb (200, 66, 58) : Color.FromArgb (196, 43, 28));
						g.FillRectangle (red, close);
					}
					// the cross, ten pixels across (at 96 dpi) in the button's middle
					int cx = close.Left + close.Width / 2, cy = close.Top + close.Height / 2, arm = Px (5);
					using var cross = new Pen (hot ? Color.White : active ? Color.Black : Color.FromArgb (150, 150, 150), Dpi);
					SmoothingMode before = g.SmoothingMode;
					g.SmoothingMode = SmoothingMode.AntiAlias;
					g.DrawLine (cross, cx - arm, cy - arm, cx + arm, cy + arm);
					g.DrawLine (cross, cx + arm, cy - arm, cx - arm, cy + arm);
					g.SmoothingMode = before;
				}
				return System.Drawing.WebGpuBackend.GpuRaster.EndScene (g);
			} finally {
				g.Dispose ();
			}
		}

		/// <summary>What DefWindowProc does when the close button is clicked: WM_SYSCOMMAND SC_CLOSE,
		/// which becomes WM_CLOSE (and the form's FormClosing).</summary>
		internal void Close ()
		{
			const int SC_CLOSE = 0xF060;
			if (_form.IsHandleCreated && !_form.IsDisposed)
				XplatUI.SendMessage (_form.Handle, Msg.WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);
		}
	}
}
