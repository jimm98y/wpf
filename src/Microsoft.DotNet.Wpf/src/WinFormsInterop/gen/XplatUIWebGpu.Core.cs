// XplatUIWebGpu core: a managed, in-memory WinForms platform driver. Windows are Hwnd objects
// with a System.Drawing.Bitmap backing store; painting hands out a Graphics over that bitmap
// (the theme draws into it). A managed message queue drives Application.Run: Invalidate posts
// WM_PAINT, DispatchMessage routes to NativeWindow.WndProc, PaintEventStart gives the paint DC.
// This is the "live window minus on-screen presentation" spine; the compositor blit + OS input
// layer plug in on top (GetWindowBackBuffer exposes each window's pixels for presentation).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace System.Windows.Forms
{
	internal partial class XplatUIWebGpu : XplatUIDriver
	{
		private static XplatUIWebGpu instance;
		public static XplatUIWebGpu GetInstance()
		{
			if (instance == null) instance = new XplatUIWebGpu();
			return instance;
		}
		private XplatUIWebGpu() { }

		// Cursors belong to the OS compositor on every head this driver serves (Wayland sets them by
		// shape, macOS by NSCursor, the browser by CSS), so DefineCursor ignores its bitmaps. Saying so
		// lets Cursor skip decoding them -- which is what keeps this driver free of libgdiplus, since
		// System.Drawing.Bitmap has no managed backend and every Bitmap is a GDI+ object.
		internal override bool CursorBitmapsUsed { get { return false; } }

		private int next_handle = 1;
		private readonly Dictionary<IntPtr, Bitmap> backing = new Dictionary<IntPtr, Bitmap>();
		private readonly Dictionary<IntPtr, string> captions = new Dictionary<IntPtr, string>();
		// One message queue per UI thread, the way Win32 has one. A posted message belongs to the
		// queue of the thread that CREATED the target window, and a message loop only ever pumps its
		// own -- so a second UI thread cannot dispatch another thread's controls. With a single
		// shared queue it did: SharpDevelop shows its progress dialog by running a whole message
		// loop on a private STA thread, that loop dequeued a WM_PAINT belonging to a main-thread
		// control, and painting it threw "Cross-thread access of handle detected". Mono's WndProc
		// answers a failed paint by replacing the control with a red cross -- which calls Hide(),
		// which asks for the handle, which threw the same exception a second time, this time outside
		// the catch. Loading a solution killed the process.
		private sealed class MsgQueue
		{
			internal readonly Queue<MSG> Messages = new Queue<MSG>();
			internal bool Quit;
		}

		[ThreadStatic] private static MsgQueue t_queue;
		private static MsgQueue CurrentQueue => t_queue ??= new MsgQueue();

		// Which thread's queue each window belongs to, recorded when the window is created.
		private readonly Dictionary<IntPtr, MsgQueue> window_queue = new Dictionary<IntPtr, MsgQueue>();

		/// <summary>The queue a message for <paramref name="handle"/> belongs in. A handle we never
		/// saw created -- and the null handle a thread-wide message carries -- means this thread.</summary>
		private MsgQueue QueueFor(IntPtr handle)
		{
			if (handle != IntPtr.Zero)
				lock (window_queue)
					if (window_queue.TryGetValue(handle, out MsgQueue q)) return q;
			return CurrentQueue;
		}

		/// <summary>Post to the queue that owns <paramref name="handle"/>. Cross-thread by design:
		/// Control.BeginInvoke from a worker thread lands here.</summary>
		private void Enqueue(IntPtr handle, MSG msg)
		{
			MsgQueue q = QueueFor(handle);
			lock (q.Messages) q.Messages.Enqueue(msg);
		}

		private static bool TryDequeue(MsgQueue q, out MSG msg)
		{
			lock (q.Messages)
			{
				if (q.Messages.Count == 0) { msg = default; return false; }
				msg = q.Messages.Dequeue();
				return true;
			}
		}
		private static readonly bool Trace = Environment.GetEnvironmentVariable("WF_DRIVER_TRACE") == "1";
		private static void T(string s) { if (Trace) Console.WriteLine("[drv] " + s); }

		/// <summary>Presentation hook: the current pixel buffer for a window (theme output).</summary>
		internal Bitmap GetWindowBackBuffer(IntPtr handle)
			=> backing.TryGetValue(handle, out Bitmap b) ? b : null;

		/// <summary>Presentation hook: EVERY visible window to composite, as flat [handle, screenX,
		/// screenY] triples in paint order — the given form's subtree first (parents before
		/// children), then other top-level windows (e.g. ComboBox WS_POPUP dropdowns) in creation
		/// order. The host blits each window's backing at its screen position. This is what makes
		/// popups (separate top-level windows) appear.</summary>
		/// <summary>A window's client size packed (width&lt;&lt;32 | height) — lets the browser host size its
		/// presentation canvas to the union of the form and any popups (dropdowns extending past the form).</summary>
		internal long GetWindowSizePacked(IntPtr handle)
		{
			Hwnd h = Hwnd.ObjectFromHandle(handle);
			return h == null ? 0 : (((long)h.width << 32) | (uint)h.height);
		}

		internal long[] GetPresentWindows(IntPtr form)
		{
			List<IntPtr> vis = CollectAll(form);
			var outl = new List<long>(vis.Count * 3);
			foreach (IntPtr k in vis)
			{
				Point p = ScreenLocation(Hwnd.ObjectFromHandle(k));
				outl.Add(k.ToInt64()); outl.Add(p.X); outl.Add(p.Y);
			}
			return outl.ToArray();
		}

		/// <summary>
		/// Whether the window was created as a popup surface -- a menu, a combo drop-down, a tooltip,
		/// an in-place editor -- rather than as an ordinary child control.
		/// </summary>
		/// <remarks>
		/// A compositor has to tell the two apart, and "a top-level window nobody claims" is not the
		/// test: a hosted pad whose tab has never been selected is exactly that too, and got drawn as
		/// a floating panel over whatever had the input. Every popup surface in this stack asks for
		/// WS_POPUP (ToolStripDropDown, MenuAPI, ComboBox's list, ToolTip, MonthCalendar,
		/// PropertyGridView's drop-down, TextBox's auto-complete); Control's own CreateParams asks for
		/// WS_CHILD. This driver never restyles a window after creation, so the style it was created
		/// with is the answer.
		/// </remarks>
		internal bool IsPopupWindow(IntPtr handle)
		{
			Hwnd h = Hwnd.ObjectFromHandle(handle);
			if (h == null) return false;
			return (h.initial_style & WindowStyles.WS_POPUP) != 0;
		}

		/// <summary>Paint the border a control's window style asks for. Win32 draws this in the
		/// non-client frame, which this driver does not model -- CalculateWindowRect returns the
		/// client rectangle unchanged, so a window and its client are one and the same. The style
		/// was therefore recorded at CreateWindow and never drawn, and every TextBox, ListBox and
		/// TreeView on this stack came up with no border at all. Draw it over the window's own
		/// outer edge instead, after the control has finished painting.</summary>
		private void DrawWindowBorder(IntPtr handle, Graphics dc)
		{
			if (dc == null) return;
			Hwnd h = Hwnd.ObjectFromHandle(handle);
			if (h == null || h.width <= 0 || h.height <= 0) return;

			bool sunken = (h.initial_ex_style & WindowExStyles.WS_EX_CLIENTEDGE) != 0;
			bool plain = (h.initial_style & WindowStyles.WS_BORDER) != 0;
			if (!sunken && !plain) return;

			// A top-level window's frame belongs to the host, which draws a real one around it.
			Control c = Control.FromHandle(handle);
			if (c is Form) return;

			try { ThemeEngine.Current.DrawControlBorder(dc, new Rectangle(0, 0, h.width, h.height), c, sunken); }
			catch (Exception ex) { T("DrawWindowBorder: " + ex.Message); }
		}

		/// <summary>
		/// Whether <paramref name="handle"/> is one of this driver's windows.
		/// </summary>
		/// <remarks>
		/// Used to tell a driver-minted handle from a real HWND. HwndHost offers every handle its
		/// subclass returns to whoever might own it, and only ours may be claimed.
		/// </remarks>
		internal bool KnowsWindow(IntPtr handle)
		{
			return handle != IntPtr.Zero && Hwnd.ObjectFromHandle(handle) != null;
		}

		/// <summary>
		/// The visible windows belonging to <paramref name="root"/>'s subtree only, parents before
		/// children, as {handle, screenX, screenY} triples.
		/// </summary>
		/// <remarks>
		/// GetPresentWindows returns EVERY visible window and merely sorts the requested subtree
		/// first, which is fine when one host owns the whole WinForms world. It is wrong as soon as
		/// there are several: each host would publish every other host's windows too, positioned
		/// against its own origin. A host that owns one subtree should ask for exactly that.
		/// </remarks>
		internal long[] GetSubtreeWindows(IntPtr root)
		{
			var vis = new List<IntPtr>();
			if (EffectivelyVisible(Hwnd.ObjectFromHandle(root))) CollectSubtree(root, vis);

			var outl = new List<long>(vis.Count * 3);
			foreach (IntPtr k in vis)
			{
				Point p = ScreenLocation(Hwnd.ObjectFromHandle(k));
				outl.Add(k.ToInt64()); outl.Add(p.X); outl.Add(p.Y);
			}
			return outl.ToArray();
		}

		// The screen, as far as anything drawn by this driver is concerned, is the surface the
		// host presents: a menu, a drop-down, a tooltip is composited into it, and anything
		// placed outside it simply cannot be seen. WinForms asks how big the screen is before
		// it puts a popup up -- "does this drop off the bottom?" -- and answering with a large
		// imaginary desktop meant nothing ever did: a context menu raised near the foot of the
		// window opened downwards and was cut off by the window's own edge, where Windows would
		// have flipped it above the pointer. The host says how big its surface is; until one
		// does, a large desktop, so geometry still has real bounds to work with.
		private static Size s_screen = new Size(2560, 1440);

		/// <summary>The area a window can actually be seen in, in this driver's own units.
		/// Called by the host that owns the presentation surface, whenever it changes size.
		/// </summary>
		internal static void SetScreenSize(int width, int height)
		{
			if (width <= 0 || height <= 0 || s_screen == new Size(width, height))
				return;
			s_screen = new Size(width, height);
			// Screen works its list out once and keeps it, so it has to be told.
			Screen.Rescan();
		}

		internal override Rectangle VirtualScreen { get { return new Rectangle(Point.Empty, s_screen); } }
		internal override Rectangle WorkingArea { get { return new Rectangle(Point.Empty, s_screen); } }
		internal override Screen[] AllScreens
		{
			get { return new[] { new Screen(true, "WebGpu Primary Display", VirtualScreen, WorkingArea) }; }
		}

		/// <summary>Absolute (screen) top-left of a window, walking up the parent chain.</summary>
		internal Point ScreenLocation(Hwnd hwnd)
		{
			int x = 0, y = 0;
			for (Hwnd h = hwnd; h != null; h = h.parent) { x += h.x; y += h.y; }
			return new Point(x, y);
		}

		/// <summary>Input hook: the deepest visible window whose absolute rect contains the screen
		/// point, searched child-first (top-most child wins), or IntPtr.Zero.</summary>
		/// <summary>
		/// Whether a window is really on screen: itself visible, and every ancestor with it.
		/// </summary>
		/// <remarks>
		/// Hiding a parent hides its children without touching their own visible flags -- that is
		/// how Win32 behaves and what Control.SetVisibleCore relies on. Testing only the window's
		/// own flag therefore kept painting the children of a hidden parent: switching a TabControl
		/// page left the OLD page's list view, header and scrollbar in the presented set, drawn over
		/// the new page and spilling outside the tab, and hit-testing still found them.
		/// </remarks>
		private static bool EffectivelyVisible(Hwnd h)
		{
			for (Hwnd w = h; w != null; w = w.parent)
				if (!w.visible) return false;
			return true;
		}

		internal IntPtr WindowAtPoint(int screenX, int screenY)
		{
			// Paint order decides: whatever is drawn last is what the user is pointing at, so scan
			// the same list backwards. Depth alone could not separate two siblings that overlap --
			// a ListView's column header sits on top of its item pane, both filling the control.
			List<IntPtr> ordered = CollectAll(IntPtr.Zero);
			for (int i = ordered.Count - 1; i >= 0; i--)
			{
				Hwnd h = Hwnd.ObjectFromHandle(ordered[i]);
				if (h == null) continue;
				Point p = ScreenLocation(h);
				if (screenX < p.X || screenY < p.Y || screenX >= p.X + h.width || screenY >= p.Y + h.height) continue;
				return ordered[i];
			}
			return IntPtr.Zero;
		}

		/// <summary>
		/// The window under a point WITHIN one host's subtree, topmost first.
		/// </summary>
		/// <remarks>
		/// Every hosted container is a top-level window of this driver at (0,0) -- where its pixels
		/// end up on screen is decided by the WPF element that composites them, not by the driver.
		/// So a point translated into driver space falls inside EVERY host's container at once, and
		/// the global WindowAtPoint answers with whichever happens to be topmost. Clicking
		/// SharpDevelop's project tree landed in the Properties pad on the far side of the window.
		/// A host must ask about its own subtree.
		/// </remarks>
		internal IntPtr WindowAtPointIn(IntPtr root, int screenX, int screenY)
		{
			var ordered = new List<IntPtr>();
			Hwnd rootHwnd = Hwnd.ObjectFromHandle(root);
			if (rootHwnd == null || !EffectivelyVisible(rootHwnd)) return IntPtr.Zero;
			CollectSubtree(root, ordered);

			for (int i = ordered.Count - 1; i >= 0; i--)
			{
				Hwnd h = Hwnd.ObjectFromHandle(ordered[i]);
				if (h == null) continue;
				Point p = ScreenLocation(h);
				if (screenX < p.X || screenY < p.Y || screenX >= p.X + h.width || screenY >= p.Y + h.height) continue;
				return ordered[i];
			}
			return IntPtr.Zero;
		}

		internal void InjectMouseMoveIn(IntPtr root, int x, int y, bool leftDown)
			=> InjectMouseIn(root, x, y, Msg.WM_MOUSEMOVE, leftDown ? MK_LBUTTON : 0);

		internal void InjectMouseDownIn(IntPtr root, int x, int y)
			=> InjectMouseIn(root, x, y, Msg.WM_LBUTTONDOWN, MK_LBUTTON);

		internal void InjectMouseUpIn(IntPtr root, int x, int y)
			=> InjectMouseIn(root, x, y, Msg.WM_LBUTTONUP, 0);

		// The right button was never forwarded at all, so nothing hosted could raise a context menu.
		internal void InjectRightDownIn(IntPtr root, int x, int y)
			=> InjectMouseIn(root, x, y, Msg.WM_RBUTTONDOWN, MK_RBUTTON);

		internal void InjectRightUpIn(IntPtr root, int x, int y)
			=> InjectMouseIn(root, x, y, Msg.WM_RBUTTONUP, 0);

		// Deliver to a window the caller has already chosen. A hosted control's menus and drop-downs
		// are top-level windows of the driver rather than part of any host's subtree, so the host
		// resolves them itself and then says where the message must go.
		internal void InjectMouseMoveAt(IntPtr target, int x, int y, bool leftDown)
			=> DispatchMouse(target, x, y, Msg.WM_MOUSEMOVE, leftDown ? MK_LBUTTON : 0);

		internal void InjectMouseDownAt(IntPtr target, int x, int y)
			=> DispatchMouse(target, x, y, Msg.WM_LBUTTONDOWN, MK_LBUTTON);

		internal void InjectMouseUpAt(IntPtr target, int x, int y)
			=> DispatchMouse(target, x, y, Msg.WM_LBUTTONUP, 0);

		internal void InjectRightDownAt(IntPtr target, int x, int y)
			=> DispatchMouse(target, x, y, Msg.WM_RBUTTONDOWN, MK_RBUTTON);

		internal void InjectRightUpAt(IntPtr target, int x, int y)
			=> DispatchMouse(target, x, y, Msg.WM_RBUTTONUP, 0);

		internal void InjectWheelIn(IntPtr root, int screenX, int screenY, int delta)
		{
			IntPtr target = WindowAtPointIn(root, screenX, screenY);
			if (target == IntPtr.Zero) return;
			IntPtr wParam = (IntPtr)((delta << 16) & unchecked((int)0xFFFF0000));
			IntPtr lp = (IntPtr)((screenY << 16) | (screenX & 0xFFFF));
			SendMessage(target, Msg.WM_MOUSEWHEEL, wParam, lp);
		}

		private IntPtr _grabHandle;   // mouse-capture target (WinForms grabs on button-down)

		/// <summary>The window holding the mouse capture, or Zero. While one does, every mouse
		/// message belongs to it wherever the pointer actually is -- that is what makes a drag keep
		/// working once it leaves the control it started in.</summary>
		internal IntPtr GrabHandle => _grabHandle;
		private const int MK_LBUTTON = 0x0001;
		private const int MK_RBUTTON = 0x0002;

		/// <summary>Route one mouse message from a SCREEN point. While a window has captured the
		/// mouse (button held), messages go to it (with coords relative to it) even off its rect —
		/// standard Win32 capture, needed so a button's release/drag tracks correctly.</summary>
		private void InjectMouseIn(IntPtr root, int screenX, int screenY, Msg message, int wParam)
		{
			IntPtr target = _grabHandle != IntPtr.Zero ? _grabHandle : WindowAtPointIn(root, screenX, screenY);
			DispatchMouse(target, screenX, screenY, message, wParam);
		}

		private void InjectMouse(int screenX, int screenY, Msg message, int wParam)
		{
			IntPtr target = _grabHandle != IntPtr.Zero ? _grabHandle : WindowAtPoint(screenX, screenY);
			DispatchMouse(target, screenX, screenY, message, wParam);
		}

		// The window the pointer is currently over, so it can be told when the pointer leaves.
		private IntPtr _hotWindow;

		/// <summary>Say when the pointer arrives at a window and when it leaves it. Win32 does this
		/// with WM_MOUSE_ENTER and WM_MOUSELEAVE, and Mono's Control turns them into Entered -- which
		/// is what a theme reads to draw the hover look. This driver only ever sent WM_MOUSEMOVE, so
		/// a control that was entered stayed entered for the life of the process: once the pointer
		/// had crossed a button, it kept the hot face for ever. Nothing noticed until Windows itself
		/// started drawing the buttons, because the hover state had never been painted before.</summary>
		private void TrackHover(IntPtr target)
		{
			if (target == _hotWindow) return;
			IntPtr previous = _hotWindow;
			_hotWindow = target;
			if (previous != IntPtr.Zero && Hwnd.ObjectFromHandle(previous) != null)
				SendMessage(previous, Msg.WM_MOUSELEAVE, IntPtr.Zero, IntPtr.Zero);
			if (target != IntPtr.Zero)
				SendMessage(target, Msg.WM_MOUSE_ENTER, IntPtr.Zero, IntPtr.Zero);
		}

		/// <summary>The pointer has left the hosted surface altogether -- the WPF element hosting us
		/// says so. Whatever was hot no longer is.</summary>
		internal void InjectMouseLeaveAll() => TrackHover(IntPtr.Zero);

		// Rubber-band rectangles, in this driver's screen space, with the width of the line each
		// was asked for.
		private readonly List<(IntPtr Owner, Rectangle Rect, int Width)> _reversible =
			new List<(IntPtr, Rectangle, int)>();

		internal void ToggleReversible(IntPtr handle, Rectangle rect, int lineWidth)
		{
			Hwnd h = Hwnd.ObjectFromHandle(handle);
			if (h != null)
			{
				Point origin = ScreenLocation(h);
				rect.Offset(origin.X, origin.Y);
			}
			lock (_reversible)
			{
				int at = _reversible.FindIndex(r => r.Rect == rect && r.Width == lineWidth);
				if (at >= 0) _reversible.RemoveAt(at);
				else _reversible.Add((handle, rect, lineWidth));
			}
			// Nothing invalidates for a rubber band -- it is not part of any window's content -- so
			// the frame has to be asked for directly, or it appears only when something else
			// happens to repaint.
			BumpPaintVersion();
		}

		private void ClearReversible(IntPtr handle)
		{
			lock (_reversible)
			{
				if (_reversible.RemoveAll(r => r.Owner == handle) > 0)
					BumpPaintVersion();
			}
		}

		/// <summary>The rubber bands to draw over the finished frame, as {x, y, width, height,
		/// lineWidth} in driver screen space.</summary>
		/// <summary>Ask for another frame when nothing was invalidated -- a rubber band is not part
		/// of any window's content, so nothing else would.</summary>
		private void BumpPaintVersion() { _paintVersion++; }

		internal long[] GetReversibleRects()
		{
			lock (_reversible)
			{
				var outl = new long[_reversible.Count * 5];
				for (int i = 0; i < _reversible.Count; i++)
				{
					var (_, r, w) = _reversible[i];
					outl[i * 5] = r.X; outl[i * 5 + 1] = r.Y;
					outl[i * 5 + 2] = r.Width; outl[i * 5 + 3] = r.Height;
					outl[i * 5 + 4] = w;
				}
				return outl;
			}
		}

		/// <summary>Where the pointer is, in this driver's screen space.</summary>
		private int _cursorX, _cursorY;

		/// <summary>Deliver one mouse message to <paramref name="target"/>, in its client coords.</summary>
		private void DispatchMouse(IntPtr target, int screenX, int screenY, Msg message, int wParam)
		{
			// Remember where the pointer is, whether or not anything is under it. Win32 keeps this
			// for the asking and WinForms leans on it more than it looks: an open menu follows the
			// pointer through Control.MousePosition rather than through the coordinates in the
			// message, so answering the origin meant every menu thought the pointer was in the
			// top-left corner and no item ever highlighted.
			_cursorX = screenX;
			_cursorY = screenY;
			if (target == IntPtr.Zero) return;
			if (message == Msg.WM_MOUSEMOVE) TrackHover(target);
			Hwnd h = Hwnd.ObjectFromHandle(target);
			if (h == null) return;
			Point p = ScreenLocation(h);
			int cx = screenX - p.X, cy = screenY - p.Y;
			IntPtr lp = (IntPtr)((cy << 16) | (cx & 0xFFFF));

			// Announce a press before delivering it, so a control holding a drop-down open hears about
			// clicks that are none of its business -- the one thing it needs in order to close.
			if (message == Msg.WM_LBUTTONDOWN || message == Msg.WM_RBUTTONDOWN
				|| message == Msg.WM_MBUTTONDOWN)
				XplatUI.RaiseMousePress(target);

			SendMessage(target, message, (IntPtr)wParam, lp);
		}

		internal void InjectMouseMove(int screenX, int screenY, bool leftDown)
			=> InjectMouse(screenX, screenY, Msg.WM_MOUSEMOVE, leftDown ? MK_LBUTTON : 0);
		internal void InjectMouseDown(int screenX, int screenY)
			=> InjectMouse(screenX, screenY, Msg.WM_LBUTTONDOWN, MK_LBUTTON);
		internal void InjectMouseUp(int screenX, int screenY)
			=> InjectMouse(screenX, screenY, Msg.WM_LBUTTONUP, 0);

		/// <summary>The right button, delivered to the window under the pointer exactly as the
		/// left one is. Nothing carried it in from a host, so no control anywhere ever saw a
		/// right click: a context menu could be built, and asked for, and shown by hand, but
		/// clicking with the right button did nothing at all.</summary>
		internal void InjectRightDown(int screenX, int screenY)
			=> InjectMouse(screenX, screenY, Msg.WM_RBUTTONDOWN, MK_RBUTTON);
		internal void InjectRightUp(int screenX, int screenY)
			=> InjectMouse(screenX, screenY, Msg.WM_RBUTTONUP, 0);

		/// <summary>Route a mouse-wheel notch to the window under the cursor. WM_MOUSEWHEEL carries the
		/// signed delta (multiples of WHEEL_DELTA=120, positive = scroll up) in the wParam high word and
		/// SCREEN coordinates in lParam — matching what ScrollableControl/ListBox expect.</summary>
		internal void InjectWheel(int screenX, int screenY, int delta)
		{
			_cursorX = screenX;
			_cursorY = screenY;
			IntPtr target = WindowAtPoint(screenX, screenY);
			if (target == IntPtr.Zero) return;
			IntPtr wParam = (IntPtr)((delta << 16) & unchecked((int)0xFFFF0000));
			IntPtr lp = (IntPtr)((screenY << 16) | (screenX & 0xFFFF));
			SendMessage(target, Msg.WM_MOUSEWHEEL, wParam, lp);
		}

		/// <summary>Full click (down+up together) — for headless self-tests; real input uses the
		/// separate down/up hooks so pressed/hover states animate between frames.</summary>
		internal IntPtr InjectClick(int screenX, int screenY)
		{
			InjectMouseMove(screenX, screenY, false);
			InjectMouseDown(screenX, screenY);
			InjectMouseUp(screenX, screenY);
			return WindowAtPoint(screenX, screenY);
		}

		// ---- window lifecycle ----------------------------------------------------

		internal override IntPtr CreateWindow(CreateParams cp)
		{
			var hwnd = new Hwnd();
			int w = Math.Max(1, cp.Width), h = Math.Max(1, cp.Height);
			hwnd.x = cp.X == int.MinValue ? 0 : cp.X;
			hwnd.y = cp.Y == int.MinValue ? 0 : cp.Y;
			hwnd.width = w; hwnd.height = h;
			hwnd.initial_style = cp.WindowStyle;
			hwnd.initial_ex_style = cp.WindowExStyle;
			if (cp.Parent != IntPtr.Zero)
				hwnd.parent = Hwnd.ObjectFromHandle(cp.Parent);

			IntPtr handle = (IntPtr)(next_handle++);
			hwnd.WholeWindow = handle;
			hwnd.ClientWindow = handle;   // registers in Hwnd's handle->object table
			// GPU-raster mode records scenes (no per-window bitmap); keep the key as the window
			// registry that GetPresentWindows walks, but allocate no libgdiplus Bitmap.
			backing[handle] = s_gpuRaster ? null : new Bitmap(w, h);
			lock (window_queue) window_queue[handle] = CurrentQueue;

			// Child controls are created WS_VISIBLE when their parent is shown; honor that so
			// invalidation isn't dropped by the visibility guard (top-level Forms get an explicit
			// SetVisible on Show).
			if ((cp.Style & (int)WindowStyles.WS_VISIBLE) != 0)
			{
				hwnd.visible = true;
				hwnd.Mapped = true;

				// Win32 queues a WM_PAINT for a window created visible. This driver paints only what
				// has been invalidated, and a control created into a parent that is ALREADY showing
				// never passes through SetVisible -- which is the only other place that invalidates a
				// newly revealed subtree. So it had no recorded scene and simply was not on screen
				// until something unrelated forced a repaint. The forms designer builds the controls
				// of the form it is designing exactly this way: the design surface came up as an
				// empty window, and the button on it appeared only once the surface was clicked.
				Invalidate(handle, new Rectangle(0, 0, w, h), false);
				_paintVersion++;
			}

			Text(handle, cp.Caption);
			SendMessage(handle, Msg.WM_CREATE, (IntPtr)1, IntPtr.Zero);
			return handle;
		}

		internal override IntPtr CreateWindow(IntPtr Parent, int X, int Y, int Width, int Height)
		{
			var cp = new CreateParams { Parent = Parent, X = X, Y = Y, Width = Width, Height = Height, Style = 0 };
			return CreateWindow(cp);
		}

		internal override void DestroyWindow(IntPtr handle)
		{
			// Win32 destroys a window's children along with it, and each of them gets its own
			// WM_DESTROY -- which is what makes WinForms clear Control.Created. Destroying only this
			// window left every child still marked created, with a parent that no longer existed, so
			// the next Control.CreateControl on that tree returned immediately ("already created")
			// and none of them ever got a handle again. A hosted panel rendered the first time it
			// was shown and was empty every time after, once it had been swapped away and back.
			foreach (IntPtr child in ChildHandles(handle)) DestroyWindow(child);

			if (backing.TryGetValue(handle, out Bitmap b)) { b?.Dispose(); backing.Remove(handle); }
			lock (window_queue) window_queue.Remove(handle);
			if (_hotWindow == handle) _hotWindow = IntPtr.Zero;
			captions.Remove(handle);
			_scenes.Remove(handle);
			_paintVersion++;   // a window disappeared from the composite
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd != null) { SendMessage(handle, Msg.WM_DESTROY, IntPtr.Zero, IntPtr.Zero); hwnd.Dispose(); }
		}

		/// <summary>The handles whose immediate parent is <paramref name="handle"/>, as a snapshot --
		/// the caller is about to destroy them, which mutates the registry.</summary>
		private List<IntPtr> ChildHandles(IntPtr handle)
		{
			var kids = new List<IntPtr>();
			Hwnd parent = Hwnd.ObjectFromHandle(handle);
			if (parent == null) return kids;
			foreach (IntPtr k in new List<IntPtr>(backing.Keys))
			{
				Hwnd c = Hwnd.ObjectFromHandle(k);
				if (c != null && c != parent && c.parent == parent) kids.Add(k);
			}
			return kids;
		}

		internal override bool SetVisible(IntPtr handle, bool visible, bool activate)
		{
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd == null) return false;
			hwnd.visible = visible;
			hwnd.Mapped = visible;
			_paintVersion++;   // visibility change alters the composite
			if (visible)
			{
				SendMessage(handle, Msg.WM_SHOWWINDOW, (IntPtr)1, IntPtr.Zero);
				Invalidate(handle, new Rectangle(0, 0, hwnd.width, hwnd.height), false);

				// Showing a window reveals everything beneath it, whose own visible flags never
				// changed -- so nothing else would invalidate them, and this driver paints only what
				// has been invalidated. A control that has never painted has no recorded scene at
				// all: selecting a TabControl page for the first time showed an empty page, and
				// coming back to a page left it as it was rather than repainting it.
				foreach (IntPtr k in new List<IntPtr>(backing.Keys))
				{
					Hwnd c = Hwnd.ObjectFromHandle(k);
					if (c == null || c == hwnd) continue;
					for (Hwnd a = c.parent; a != null; a = a.parent)
					{
						if (a != hwnd) continue;
						if (EffectivelyVisible(c))
							Invalidate(k, new Rectangle(0, 0, c.width, c.height), false);
						break;
					}
				}
			}
			else
			{
				// Win32 sends WM_SHOWWINDOW when a window is HIDDEN as well, and Control's handler is
				// what raises VisibleChanged: SetVisibleCore does not raise it itself on the way down.
				// Sending it only on the way up meant Visible = false was never announced to anybody --
				// so a host watching for its form to go away never heard, and a drop-down that had been
				// closed left its window on screen, behind everything, still holding the keyboard.
				SendMessage(handle, Msg.WM_SHOWWINDOW, IntPtr.Zero, IntPtr.Zero);

				// What it was covering has to be repainted: those windows' own visible flags did not
				// change, so nothing else would ask.
				Hwnd parent = hwnd.parent;
				if (parent != null)
					Invalidate(parent.Handle, new Rectangle(0, 0, parent.width, parent.height), false);
				else
					RepaintUnder(hwnd);
			}
			return true;
		}

		/// <summary>Repaint whatever a window that has just been hidden was covering.
		/// <para>A window with a parent is dealt with by repainting the parent. A window WITHOUT
		/// one -- a menu, a drop-down, a tooltip: they are top-level windows of this driver --
		/// has no parent to repaint, and nothing else has any reason to: the windows underneath
		/// it never changed. So the pixels stayed exactly as they were and a menu that had been
		/// closed went on being drawn -- gone as far as the application was concerned, still
		/// there as far as anyone looking at the screen was, and the next menu opened beside
		/// it.</para></summary>
		private void RepaintUnder(Hwnd gone)
		{
			Point at = ScreenLocation(gone);
			var covered = new Rectangle(at.X, at.Y, gone.width, gone.height);
			if (covered.Width <= 0 || covered.Height <= 0) return;

			foreach (IntPtr k in new List<IntPtr>(backing.Keys))
			{
				Hwnd other = Hwnd.ObjectFromHandle(k);
				if (other == null || other == gone || !EffectivelyVisible(other)) continue;
				Point p = ScreenLocation(other);
				var overlap = Rectangle.Intersect(covered,
					new Rectangle(p.X, p.Y, other.width, other.height));
				if (overlap.Width <= 0 || overlap.Height <= 0) continue;
				Invalidate(k, new Rectangle(overlap.X - p.X, overlap.Y - p.Y,
					overlap.Width, overlap.Height), false);
			}
		}

		internal override bool IsVisible(IntPtr handle)
		{
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			return hwnd != null && hwnd.visible;
		}

		internal override void SetWindowPos(IntPtr handle, int x, int y, int width, int height)
		{
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd == null) return;
			hwnd.x = x; hwnd.y = y;
			if (width > 0 && height > 0 && (width != hwnd.width || height != hwnd.height))
			{
				hwnd.width = width; hwnd.height = height;
				if (!s_gpuRaster)
				{
					if (backing.TryGetValue(handle, out Bitmap old)) old?.Dispose();
					backing[handle] = new Bitmap(width, height);
				}
			}
			_paintVersion++;   // a window moved/resized
			PerformNCCalc(hwnd);
			// WinForms' SetBoundsCore does not update Control.bounds directly; it waits for
			// WM_WINDOWPOSCHANGED to call UpdateBounds (which reads GetWindowPos). Without this,
			// a later Size-only change resends a stale x=0,y=0 and clobbers the position — which
			// is exactly what dropped the ComboBox popup to the origin.
			SendMessage(handle, Msg.WM_WINDOWPOSCHANGED, IntPtr.Zero, IntPtr.Zero);
		}

		internal override void GetWindowPos(IntPtr handle, bool is_toplevel, out int x, out int y,
			out int width, out int height, out int client_width, out int client_height)
		{
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd == null) { x = y = width = height = client_width = client_height = 0; return; }
			x = hwnd.x; y = hwnd.y; width = hwnd.width; height = hwnd.height;
			// No non-client frame yet: client rect == window rect.
			client_width = hwnd.width; client_height = hwnd.height;
		}

		private void PerformNCCalc(Hwnd hwnd) { }

		internal override IntPtr SetParent(IntPtr handle, IntPtr parent)
		{
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd != null) hwnd.parent = parent == IntPtr.Zero ? null : Hwnd.ObjectFromHandle(parent);
			return IntPtr.Zero;
		}

		internal override IntPtr GetParent(IntPtr handle, bool with_owner)
		{
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			return hwnd?.parent?.Handle ?? IntPtr.Zero;
		}

		// ---- painting ------------------------------------------------------------

		internal override void Invalidate(IntPtr handle, Rectangle rc, bool clear)
		{
			// A rubber band is scribbled ON TOP of a window, and the caller's way of taking it back is
			// to repaint underneath -- Splitter ends a drag with Parent.Refresh and a comment saying so.
			// With an XOR pen on a real screen that works; a band the compositor draws over the finished
			// frame outlives any repaint, so the splitter left a line behind at every position it had
			// been dropped at. Repainting the window they belong to is what clears them.
			ClearReversible(handle);
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			T($"Invalidate h=0x{handle.ToInt64():x} visible={hwnd?.visible} rc={rc}");
			if (hwnd == null || !hwnd.visible) return;
			if (rc.Width <= 0 || rc.Height <= 0) rc = new Rectangle(0, 0, hwnd.width, hwnd.height);
			hwnd.AddInvalidArea(rc);
			if (!hwnd.expose_pending)
			{
				hwnd.expose_pending = true;
				Enqueue(hwnd.Handle, new MSG { hwnd = hwnd.Handle, message = Msg.WM_PAINT });
			}
		}

		internal override void InvalidateNC(IntPtr handle) { }

		internal override PaintEventArgs PaintEventStart(ref Message msg, IntPtr handle, bool client)
		{
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			T($"PaintEventStart h=0x{handle.ToInt64():x} client={client}");
			Graphics dc;
			if (s_gpuRaster)
			{
				if (!backing.ContainsKey(handle)) backing[handle] = null;      // keep the window registry
				dc = System.Drawing.WebGpuBackend.GpuRaster.NewRecording();    // records; NO libgdiplus backing
			}
			else
			{
				if (!backing.TryGetValue(handle, out Bitmap bmp))
				{
					bmp = new Bitmap(Math.Max(1, hwnd.width), Math.Max(1, hwnd.height));
					backing[handle] = bmp;
				}
				dc = Graphics.FromImage(bmp);
			}
			Rectangle invalid = hwnd.Invalid;
			// GPU-raster mode records the whole window as a scene, so paint the entire window rather
			// than a partial invalid region.
			if (invalid.Width <= 0 || invalid.Height <= 0 || s_gpuRaster)
				invalid = new Rectangle(0, 0, hwnd.width, hwnd.height);
			dc.SetClip(invalid);   // no-op for a recording-only Graphics
			var pe = new PaintEventArgs(dc, invalid);
			hwnd.expose_pending = false;
			hwnd.ClearInvalidArea();
			return pe;
		}

		// GPU raster is what this stack IS: the driver records a scene per window and the host
		// presents it. It is on unless explicitly switched off, and off entirely when the WebGPU
		// path is (WF_WEBGPU=0 -- the headless render tests, which read backing-store bitmaps).
		//
		// It must NOT be opt-in. Every consumer captures it into a static readonly field when its
		// type initializes, and WindowsFormsHost's static constructor -- which used to set
		// WF_GPU_RASTER=1 -- only runs when the app reaches WinForms THROUGH that class. An app that
		// touches WinForms first captured false: SharpDevelop shows a WinForms splash screen long
		// before its workbench starts, so the driver recorded no scenes for the rest of the process
		// while the host, whose flag is an instance field read much later, set up the WebGPU present
		// path and found every scene null. Its splash and every dialog came up blank.
		private static readonly bool s_gpuRaster = Environment.GetEnvironmentVariable("WF_GPU_RASTER") != "0"
            && Environment.GetEnvironmentVariable("WF_WEBGPU") != "0";
		// Per-window recorded WebGPU scene (boxed SceneVisual). The present path renders these directly
		// — no per-control GPU readback, no re-upload, one device. GetWindowScene exposes them.
		private readonly Dictionary<IntPtr, object> _scenes = new Dictionary<IntPtr, object>();
		// Handles whose scene was captured this cycle via a double-buffer blit, so PaintEventEnd just
		// detaches the (empty) window-DC recorder instead of overwriting the stored scene.
		private readonly HashSet<IntPtr> _paintedViaOffscreen = new HashSet<IntPtr>();

		/// <summary>
		/// Confine a recorded scene to its window's own bounds.
		/// </summary>
		/// <remarks>
		/// A control paints inside its client area and the OS clips it there; nothing clipped these
		/// scenes, so anything a control drew past its own edge was composited anyway. A list view
		/// item wider than its column spilled its text out of the list, out of the tab page and out
		/// of the tab control -- across the dialog. Clipping here rather than in a host means every
		/// present path gets it: the Win32 and Cocoa windows, and embedded content alike.
		/// </remarks>
		private static object ClipToWindow(object scene, IntPtr handle)
		{
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (Environment.GetEnvironmentVariable("WF_TRACE_TEXT") == "1" && hwnd != null)
			{
				Control c = Control.FromHandle(handle);
				Console.Error.WriteLine($"clip 0x{handle.ToInt64():x} {c?.GetType().Name} hwnd={hwnd.width}x{hwnd.height}" +
					$" control={(c == null ? "-" : c.Bounds.ToString())}");
			}
			if (scene is Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual sv && hwnd != null)
				ApplyWindowClip(sv, hwnd);
			return scene;
		}

		/// <summary>Append what was drawn through <see cref="GetHwndGraphics"/> to the window's
		/// scene, so drawing done outside a paint cycle actually reaches the screen. The next
		/// WM_PAINT replaces the scene wholesale, which is the behaviour you want: whoever drew
		/// this gets to draw it again.</summary>
		private void MergeIntoWindowScene(IntPtr handle, Graphics g)
		{
			var drawn = System.Drawing.WebGpuBackend.GpuRaster.EndScene(g)
				as Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual;
			if (drawn == null || (drawn.Content.Count == 0 && drawn.Children.Count == 0)) return;

			if (!(GetWindowScene(handle) is Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual window))
			{
				_scenes[handle] = ClipToWindow(drawn, handle);
			}
			else
			{
				window.Children.Add(drawn);
			}
			_paintVersion++;
		}

		/// <summary>The window's most recently recorded WebGPU scene (boxed SceneVisual), or null.</summary>
		// How far a popup's shadow reaches past its own edges, and how dark it is at each step.
		// Sampled off a stock combo box's list: down and to the right only, never up or left,
		// reaching #868686 at the first step and fading to nothing by the fifth.
		//
		// The alphas are the ones that land on those samples, not the ones the arithmetic in sRGB
		// would suggest: this compositor blends in linear light, so the same alpha comes out about
		// half as dark as it would over a plain sRGB surface.
		private const int ShadowDepth = 5;
		private static readonly int[] ShadowAlpha = { 224, 168, 85, 28, 6 };

		// Scenes that already carry their shadow. A scene is replaced wholesale on every repaint,
		// so tracking the scene rather than the window is what keeps one shadow per paint.
		private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, object>
			_shadowed = new System.Runtime.CompilerServices.ConditionalWeakTable<object, object>();

		/// <summary>Give a popup the drop shadow Windows gives it at the window-class level, so
		/// menus, combo lists, tooltips and drop-down calendars all get one without any of them
		/// having to know. Drawn in the window's own coordinates, past its right and bottom edges,
		/// where ApplyWindowClip has left room.</summary>
		private static void AddPopupShadow(Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual sv, Hwnd hwnd)
		{
			if (sv == null || hwnd == null) return;
			if ((hwnd.initial_style & WindowStyles.WS_POPUP) == 0) return;
			if (hwnd.width <= 0 || hwnd.height <= 0) return;
			if (_shadowed.TryGetValue(sv, out _)) return;
			_shadowed.Add(sv, sv);

			Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual shadow = null;
			try
			{
				using (Graphics g = System.Drawing.WebGpuBackend.GpuRaster.NewRecording(
					gg => shadow = System.Drawing.WebGpuBackend.GpuRaster.EndScene(gg)
						as Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual))
				{
					for (int i = 0; i < ShadowDepth; i++)
					{
						using (var brush = new SolidBrush(Color.FromArgb(ShadowAlpha[i], 0, 0, 0)))
						{
							// Down the right edge, starting below the top so the shadow does not
							// climb past the window, and along the bottom the same way.
							g.FillRectangle(brush, hwnd.width + i, ShadowDepth, 1, hwnd.height - ShadowDepth + i + 1);
							g.FillRectangle(brush, ShadowDepth, hwnd.height + i, hwnd.width - ShadowDepth + i + 1, 1);
						}
					}
				}
			}
			catch (Exception ex) { T("AddPopupShadow: " + ex.Message); return; }

			if (shadow != null && (shadow.Content.Count > 0 || shadow.Children.Count > 0))
				sv.Children.Add(shadow);
		}

		internal object GetWindowScene(IntPtr handle)
		{
			if (!_scenes.TryGetValue(handle, out object s)) return null;
			// Re-clip on the way out rather than when the scene was recorded. A scene is recorded
			// once and composited many times, so a clip baked in at record time describes wherever
			// the control happened to be when it last painted: scroll it and the clip stays behind,
			// which left scrolled controls invisible until something forced them to repaint.
			if (s is Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual sv)
			{
				Hwnd h = Hwnd.ObjectFromHandle(handle);
				AddPopupShadow(sv, h);
				ApplyWindowClip(sv, h);
			}
			return s;
		}

		/// <summary>Confine a window's scene to its own bounds and to every ancestor's, brought
		/// into its coordinates. A child is clipped by its parent in Windows and nothing here did
		/// that, so a control scrolled out of a panel went on drawing over whatever lay below the
		/// panel -- the status bar most visibly, which looked transparent as a result.</summary>
		private static void ApplyWindowClip(Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual sv, Hwnd hwnd)
		{
			if (sv == null || hwnd == null) return;
			float left = 0, top = 0;
			float right = Math.Max(0, hwnd.width), bottom = Math.Max(0, hwnd.height);
			// A popup is not confined by whatever it hangs off: a menu, a combo box's list and a
			// tooltip all stand outside their owner on purpose. It also needs room past its own
			// edges for the shadow underneath it.
			if ((hwnd.initial_style & WindowStyles.WS_POPUP) != 0)
			{
				right += ShadowDepth;
				bottom += ShadowDepth;
			}
			else
			{
				int offX = 0, offY = 0;
				Hwnd child = hwnd;
				for (Hwnd parent = hwnd.parent; parent != null; child = parent, parent = parent.parent)
				{
					offX -= child.x;
					offY -= child.y;
					left = Math.Max(left, offX);
					top = Math.Max(top, offY);
					right = Math.Min(right, offX + parent.width);
					bottom = Math.Min(bottom, offY + parent.height);
					if ((parent.initial_style & WindowStyles.WS_POPUP) != 0)
						break;
				}
			}
			sv.Clip = new Microsoft.Wpf.Interop.WebGpu.Composition.Rect(
				left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
		}

		// Bumped on anything that changes what the compositor should show (a control repaints, or a
		// window is created/moved/shown/hidden/destroyed). The host presents only when this advances
		// (or the caret blink toggles), instead of re-rendering every frame.
		private int _paintVersion;
		internal int GetPaintVersion() => _paintVersion;

		internal override void PaintEventEnd(ref Message msg, IntPtr handle, bool client, PaintEventArgs pevent)
		{
			if (s_gpuRaster)
			{
				if (_paintedViaOffscreen.Remove(handle))
					System.Drawing.WebGpuBackend.GpuRaster.Cancel(pevent.Graphics);       // scene captured by the blit
				else
				{
					DrawWindowBorder(handle, pevent.Graphics);
					_scenes[handle] = ClipToWindow(System.Drawing.WebGpuBackend.GpuRaster.EndScene(pevent.Graphics), handle);
				}
				_paintVersion++;   // content changed
			}
			else
			{
				DrawWindowBorder(handle, pevent.Graphics);
			}
			pevent.Graphics?.Dispose();
			pevent.SetGraphics(null);
			pevent.Dispose();
		}

		// ---- double buffering (preserved) --------------------------------------------
		// A double-buffered control paints into an offscreen Graphics, then blits it to the window. In
		// GPU-raster mode we attach a recorder to that offscreen Graphics so the control's verbs are
		// captured, and at blit time store the recorded scene as the window's scene (no libgdiplus).

		// GPU-raster: the offscreen "drawable" is a marker (no libgdiplus Bitmap); the paint records
		// into a recording-only Graphics whose scene is captured at BlitFromOffscreen.
		internal override void CreateOffscreenDrawable(IntPtr handle, int width, int height, out object offscreen_drawable)
		{
			if (s_gpuRaster) { offscreen_drawable = new object(); return; }
			base.CreateOffscreenDrawable(handle, width, height, out offscreen_drawable);
		}

		internal override void DestroyOffscreenDrawable(object offscreen_drawable)
		{
			if (offscreen_drawable is Bitmap bmp) bmp.Dispose();   // marker (object) -> nothing to free
		}

		internal override Graphics GetOffscreenGraphics(object offscreen_drawable)
		{
			if (s_gpuRaster)
				return System.Drawing.WebGpuBackend.GpuRaster.NewRecording();   // records; no libgdiplus
			return Graphics.FromImage((Bitmap)offscreen_drawable);
		}

		internal override void BlitFromOffscreen(IntPtr dest_handle, Graphics dest_dc, object offscreen_drawable, Graphics offscreen_dc, Rectangle r)
		{
			if (s_gpuRaster && System.Drawing.WebGpuBackend.GpuRaster.IsActive(offscreen_dc))
			{
				// The border goes on the scene that becomes the window's, which for a
				// double-buffered control is the offscreen one -- PaintEventEnd only cancels here.
				DrawWindowBorder(dest_handle, offscreen_dc);
				_scenes[dest_handle] = ClipToWindow(System.Drawing.WebGpuBackend.GpuRaster.EndScene(offscreen_dc), dest_handle);
				_paintedViaOffscreen.Add(dest_handle);
				return;
			}
			dest_dc.DrawImage((Bitmap)offscreen_drawable, r, r, GraphicsUnit.Pixel);
		}

		internal override void UpdateWindow(IntPtr handle) { }

		// WinForms scrolls window content (e.g. a TextBox horizontally when the caret passes the
		// edge) via ScrollWindow. Rather than blit the backing pixels, just invalidate the whole
		// window so it fully repaints at the new scroll offset — correct, and simple. (Without this
		// the newly-typed text at the edge never repaints until another change forces it.)
		internal override void ScrollWindow(IntPtr hwnd, Rectangle rectangle, int XAmount, int YAmount, bool with_children)
			=> Invalidate(hwnd, Rectangle.Empty, false);
		internal override void ScrollWindow(IntPtr hwnd, int XAmount, int YAmount, bool with_children)
			=> Invalidate(hwnd, Rectangle.Empty, false);

		// CreateGraphics et al. draw over the window's backing bitmap (Graphics.FromHwnd uses the
		// dead Carbon/QuickDraw path on mac libgdiplus and throws EntryPointNotFound). A 1x1
		// fallback keeps callers alive if the window has no backing yet.
		internal override Graphics GetHwndGraphics(IntPtr handle)
		{
			// GPU-raster: a recording-only Graphics (measurement is managed; any draw records/no-ops)
			// — no libgdiplus. Otherwise draw over the window's backing bitmap.
			if (s_gpuRaster)
			{
				// CreateGraphics means "draw on this window now", outside any paint cycle. A bare
				// recording would be thrown away when it was disposed, so fold it into the window's
				// scene instead -- the nearest thing this stack has to drawing straight at the
				// screen, and it survives until the window next repaints. The forms designer paints
				// its grid and selection handles exactly this way, on top of the control's own paint.
				return System.Drawing.WebGpuBackend.GpuRaster.NewRecording(
					g => MergeIntoWindowScene(handle, g));
			}
			if (!backing.TryGetValue(handle, out Bitmap b))
			{
				Hwnd h = Hwnd.ObjectFromHandle(handle);
				b = new Bitmap(Math.Max(1, h?.width ?? 1), Math.Max(1, h?.height ?? 1));
				backing[handle] = b;
			}
			return Graphics.FromImage(b);
		}

		// ---- message loop --------------------------------------------------------

		// The queue_id every GetMessage/PeekMessage call carries back. Nothing here reads it -- the
		// queue is found from the calling thread either way -- but handing back the real object keeps
		// the contract honest for anything that compares them.
		internal override object StartLoop(Thread thread) => CurrentQueue;
		internal override void EndLoop(Thread thread) { }

		// ---- timers --------------------------------------------------------------
		//
		// System.Windows.Forms.Timer is how ordinary WinForms code does anything periodic, so the
		// driver has to service it: Timer.Enabled just registers here, and the message loop fires the
		// due ones on its way past. Mono's other drivers do the same thing against their native
		// pumps; ours has a managed one, so this is a list and a clock.

		private readonly List<Timer> timers = new List<Timer>();

		internal override void SetTimer(Timer timer)
		{
			lock (timers) { if (!timers.Contains(timer)) timers.Add(timer); }
		}

		internal override void KillTimer(Timer timer)
		{
			lock (timers) timers.Remove(timer);
		}

		/// <summary>Fire every timer whose deadline has passed, and return the shortest wait until the
		/// next one is due (or -1 when none are). Ticks run OUTSIDE the lock: a handler is arbitrary
		/// app code and routinely starts or stops timers.</summary>
		internal int TickTimers()
		{
			Timer[] due = null;
			int count = 0;
			long now = Timer.StopWatchNowMilliseconds;
			int next = -1;
			lock (timers)
			{
				if (timers.Count == 0) return -1;
				foreach (Timer t in timers)
				{
					long remaining = t.Expires - now;
					if (remaining <= 0)
					{
						(due ??= new Timer[timers.Count])[count++] = t;
						t.Update(now);
						remaining = t.Expires - now;
					}
					if (next < 0 || remaining < next) next = (int)Math.Max(0, remaining);
				}
			}
			for (int i = 0; i < count; i++)
			{
				try { due[i].FireTick(); }
				catch (Exception ex) { Console.Error.WriteLine("Timer.Tick: " + ex); }
			}
			return next;
		}

		internal override bool GetMessage(object queue_id, ref MSG msg, IntPtr hWnd, int wFilterMin, int wFilterMax)
		{
			MsgQueue q = CurrentQueue;
			while (true)
			{
				// Before anything else, because a Tick handler is app code that can post messages,
				// change the UI (so the next present has something to show) or quit the app.
				int nextTimer = TickTimers();

				if (TryDequeue(q, out msg))
				{
					if (msg.message == Msg.WM_QUIT) return false;
					return true;
				}
				if (q.Quit) return false;
				// No queued messages. With a window on screen this is simply an idle frame: present
				// whatever changed, let the OS hand us input (which refills the queue), and go round
				// again -- that is what makes Application.Run(form) behave like real WinForms. With no
				// windowing shell (headless render tests, WF_WEBGPU=0) there is nothing left to do and
				// idle ends the pump, which is what this driver originally did unconditionally.
				if (!PresentationHost.Tick())
				{
					// No on-screen host. When windows are expected, that means every one of them has
					// gone and this loop is finished -- a pending timer must NOT keep it alive. A
					// modal dialog's timers run until the form is disposed, and Dispose only happens
					// after ShowDialog returns, so the loop and the dialog waited on each other:
					// closing SharpDevelop's About box (whose scrolling picture runs a timer) hung
					// the application, because this loop never returned to the WPF dispatcher.
					if (PresentationHost.Enabled) return false;

					// Headless: there is never a host, so a pending timer is the one reason to stay.
					if (nextTimer < 0) return false;
					PresentationHost.Idle(nextTimer);
					continue;
				}
				PresentationHost.Idle(nextTimer);

				// Hand the loop a heartbeat rather than going straight round again. A message loop only
				// re-checks whether its form wants to close AFTER it has processed a message -- but on
				// this stack the host delivers input by calling WndProc directly from inside the tick
				// above, so no message ever passes through the loop and that check never ran. A modal
				// dialog therefore set DialogResult, marked itself closing, and stayed on screen for
				// ever: every Cancel button in every dialog did nothing.
				msg = new MSG { hwnd = IntPtr.Zero, message = Msg.WM_NULL };
				return true;
			}
		}

		internal override bool PeekMessage(object queue_id, ref MSG msg, IntPtr hWnd, int wFilterMin, int wFilterMax, uint flags)
		{
			MsgQueue q = CurrentQueue;
			const uint PM_REMOVE = 0x0001;
			lock (q.Messages)
			{
				if (q.Messages.Count == 0) return false;
				msg = q.Messages.Peek();
				if ((flags & PM_REMOVE) != 0) q.Messages.Dequeue();
			}
			return true;
		}

		internal override bool TranslateMessage(ref MSG msg) => true;

		internal override IntPtr DispatchMessage(ref MSG msg)
		{
			T($"Dispatch {msg.message} h=0x{msg.hwnd.ToInt64():x}");
			// A posted Control.BeginInvoke is not a window message any WndProc knows: run the
			// delegate here, the way XplatUIWin32 runs it out of its GetMessage.
			if (msg.message == Msg.WM_ASYNC_MESSAGE)
			{
				XplatUIDriverSupport.ExecuteClientMessage((GCHandle)msg.lParam);
				return IntPtr.Zero;
			}
			return NativeWindow.WndProc(msg.hwnd, msg.message, msg.wParam, msg.lParam);
		}

		// Pump all queued messages (Application.DoEvents). The generated stub's no-op DoEvents is
		// overridden here so DoEvents actually drains the queue.
		internal override void DoEvents()
		{
			MsgQueue q = CurrentQueue;
			while (TryDequeue(q, out MSG msg))
			{
				if (msg.message == Msg.WM_QUIT) { q.Quit = true; continue; }
				TranslateMessage(ref msg);
				DispatchMessage(ref msg);
			}
		}

		internal override void PostQuitMessage(int exitCode)
		{
			// Win32 posts the quit to the CALLING thread's queue, and so do we: a wait dialog that
			// ends its own loop must not take the main one down with it.
			MsgQueue q = CurrentQueue;
			q.Quit = true;
			lock (q.Messages) q.Messages.Enqueue(new MSG { message = Msg.WM_QUIT, wParam = (IntPtr)exitCode });
		}

		internal override IntPtr SendMessage(IntPtr hwnd, Msg message, IntPtr wParam, IntPtr lParam)
			=> NativeWindow.WndProc(hwnd, message, wParam, lParam);

		internal override bool PostMessage(IntPtr hwnd, Msg message, IntPtr wParam, IntPtr lParam)
		{
			Enqueue(hwnd, new MSG { hwnd = hwnd, message = message, wParam = wParam, lParam = lParam });
			return true;
		}

		/// <summary>Where Control.BeginInvoke (and Invoke from another thread) lands: queue the call
		/// and run it when the message is dispatched, exactly as XplatUIWin32 does.</summary>
		/// <remarks>
		/// The generated stub for this was an empty method, so a posted callback was accepted and
		/// then silently dropped -- the delegate never ran and the IAsyncResult never completed.
		/// SharpDevelop takes its splash screen down through one of these, so the splash stayed on
		/// screen, blank, for the life of the process.
		/// </remarks>
		internal override void SendAsyncMethod(AsyncMethodData method)
		{
			Enqueue(method.Handle, new MSG
			{
				hwnd = method.Handle,
				message = Msg.WM_ASYNC_MESSAGE,
				lParam = (IntPtr)GCHandle.Alloc(method),
			});
		}

		internal override IntPtr DefWndProc(ref Message msg)
		{
			// Win32 hands an unhandled wheel to the parent, and controls rely on it: Mono's ListView
			// subscribes to MouseWheel on the LIST VIEW, while the window under the pointer is its
			// item pane. Without this the wheel did nothing over any list or grid.
			if (msg.Msg == (int)Msg.WM_MOUSEWHEEL)
			{
				Hwnd h = Hwnd.ObjectFromHandle(msg.HWnd);
				if (h?.parent != null)
					return SendMessage(h.parent.Handle, Msg.WM_MOUSEWHEEL, msg.WParam, msg.LParam);
			}
			return IntPtr.Zero;
		}

		/// <summary>Alt-key input. Posted rather than sent: Application.RunLoop reads these off the
		/// queue and runs the mnemonic and dialog-key handling on them (ProcessCmdKey and friends),
		/// which a direct SendMessage to the window would skip entirely.</summary>
		internal void InjectSysKeyDown(int vkey)
		{
			if (_focusHandle != IntPtr.Zero)
				Enqueue(_focusHandle, new MSG { hwnd = _focusHandle, message = Msg.WM_SYSKEYDOWN, wParam = (IntPtr)vkey });
		}

		internal void InjectSysChar(char ch)
		{
			if (_focusHandle != IntPtr.Zero)
				Enqueue(_focusHandle, new MSG { hwnd = _focusHandle, message = Msg.WM_SYSCHAR, wParam = (IntPtr)ch });
		}

		// ---- text / misc ---------------------------------------------------------

		internal override bool Text(IntPtr handle, string text) { captions[handle] = text ?? ""; return true; }
		internal override bool GetText(IntPtr handle, out string text)
			{ return captions.TryGetValue(handle, out text) ? true : ((text = "") == ""); }

		internal override void ScreenToClient(IntPtr handle, ref int x, ref int y)
		{
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd != null) { Point o = ScreenLocation(hwnd); x -= o.X; y -= o.Y; }
		}

		internal override void ClientToScreen(IntPtr handle, ref int x, ref int y)
		{
			// Accumulate the whole parent chain, not just this window's own offset,
			// so PointToScreen on a nested control (e.g. a ComboBox) yields true
			// screen coordinates. Otherwise the dropdown popup lands at the origin.
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd != null) { Point o = ScreenLocation(hwnd); x += o.X; y += o.Y; }
		}

		internal override bool CalculateWindowRect(ref Rectangle ClientRect, CreateParams cp, Menu menu, out Rectangle WindowRect)
		{
			WindowRect = ClientRect;   // no non-client frame modelled yet
			return true;
		}

		internal override void SetWindowStyle(IntPtr handle, CreateParams cp) { }
		internal override void SetBorderStyle(IntPtr handle, FormBorderStyle border_style) { }
		internal override FormWindowState GetWindowState(IntPtr handle) => FormWindowState.Normal;
		internal override void SetWindowState(IntPtr handle, FormWindowState state) { }
		internal override void SetModal(IntPtr handle, bool Modal) { }

		private IntPtr _focusHandle;   // window with keyboard focus (WM_CHAR/KEYDOWN target)

		/// <summary>The window that currently has keyboard focus, for GetFocus.</summary>
		internal IntPtr FocusHandle => _focusHandle;

		internal override void SetFocus(IntPtr handle)
		{
			if (handle == _focusHandle) return;
			IntPtr prev = _focusHandle;
			_focusHandle = handle;
			if (prev != IntPtr.Zero) SendMessage(prev, Msg.WM_KILLFOCUS, handle, IntPtr.Zero);
			if (handle != IntPtr.Zero) SendMessage(handle, Msg.WM_SETFOCUS, prev, IntPtr.Zero);
		}

		/// <summary>Input hooks: deliver keyboard to the focused window. WM_KEYDOWN carries the
		/// virtual-key (special keys: backspace/enter/arrows/delete), WM_CHAR the typed character
		/// (what TextBox inserts). Order per Win32: KEYDOWN, then CHAR for printable input.</summary>
		// The driver has no keyboard of its own: the host reads the real modifier state at each key
		// event and pushes it here. Without this ModifierKeys was always Keys.None, so every
		// shortcut a control resolves through it -- Ctrl+C and Ctrl+V in a text box, most of all --
		// simply did not exist.
		private Keys _modifierKeys;

		internal override Keys ModifierKeys { get { return _modifierKeys; } }

		internal void SetModifierKeys(Keys keys) { _modifierKeys = keys; }

		/// <summary>Deliver a key. Returns true when pre-processing consumed it -- a navigation
		/// key such as Tab -- so the host knows not to follow it with the character Windows
		/// translates it into.</summary>
		internal bool InjectKeyDown(int vkey)
		{
			IntPtr target = _focusHandle;
			if (target == IntPtr.Zero)
			{
				// Nothing has taken the focus yet: a form that has just been shown has no active
				// control until something selects one. Offer the key to the form itself so that Tab
				// can make that first selection -- otherwise the first Tab was dropped, nothing ever
				// became focused, and so every Tab after it was dropped too.
				Form active = Form.ActiveForm;
				if (active != null && active.IsHandleCreated) target = active.Handle;
			}
			if (target == IntPtr.Zero) return false;

			// A real message loop offers a key to the control's pre-processing before dispatching
			// it, and that is where WinForms handles the keys that navigate rather than type: Tab
			// and Shift+Tab move the focus, the arrows move within a group, mnemonics activate. This
			// driver posted straight to the focused window's WndProc, so none of it ran -- Tab did
			// nothing at all, and with nothing moving the focus no control ever showed a focus
			// rectangle either.
			Control focused = Control.FromHandle(target);
			if (focused != null)
			{
				var pre = Message.Create(target, (int)Msg.WM_KEYDOWN, (IntPtr)vkey, IntPtr.Zero);
				try
				{
					if (focused.PreProcessMessage(ref pre)) return true;
				}
				catch (Exception ex) { T("InjectKeyDown: " + ex.Message); }
			}

			SendMessage(target, Msg.WM_KEYDOWN, (IntPtr)vkey, IntPtr.Zero);

			// Win32 message loops call TranslateMessage before dispatch, which turns the CONTROL
			// virtual-keys into a WM_CHAR carrying their ASCII control code. Editors act on that
			// char, not on the key-down: TextBoxBase deletes on WM_CHAR 8 and breaks the line on 13,
			// and its ProcessKey has no Keys.Back case at all -- so a driver that delivers KEYDOWN
			// alone leaves backspace and enter doing nothing in a TextBox. Printable keys are NOT
			// translated here; hosts deliver those through InjectChar, and translating them too
			// would insert every character twice.
			int ch = vkey switch
			{
				0x08 => 8,    // VK_BACK
				0x09 => 9,    // VK_TAB
				0x0D => 13,   // VK_RETURN
				0x1B => 27,   // VK_ESCAPE
				_ => 0,
			};
			if (ch != 0) SendMessage(target, Msg.WM_CHAR, (IntPtr)ch, IntPtr.Zero);
			return false;
		}
		internal void InjectChar(char ch)
		{
			if (_focusHandle != IntPtr.Zero) SendMessage(_focusHandle, Msg.WM_CHAR, (IntPtr)ch, IntPtr.Zero);
		}
		internal void InjectKeyUp(int vkey)
		{
			if (_focusHandle != IntPtr.Zero) SendMessage(_focusHandle, Msg.WM_KEYUP, (IntPtr)vkey, IntPtr.Zero);
		}
		// Sibling paint order, front (topmost) first -- Win32 keeps one of these per parent and
		// WinForms drives it through Control.UpdateZOrder. The generated stub threw it away, so
		// siblings were composited in whatever order the backing dictionary happened to enumerate.
		// A ListView deliberately gives its item pane the WHOLE client and draws the column header
		// as a sibling ON TOP of it (Mono puts both at 0,0), so the wrong order hid the headers
		// completely -- and a click on a header went to the item pane underneath.
		private readonly List<IntPtr> _zOrder = new List<IntPtr>();

		internal override bool SetZOrder(IntPtr hWnd, IntPtr AfterhWnd, bool Top, bool Bottom)
		{
			_zOrder.Remove(hWnd);
			if (Bottom)
				_zOrder.Add(hWnd);
			else if (Top || AfterhWnd == IntPtr.Zero)
				_zOrder.Insert(0, hWnd);
			else
			{
				int i = _zOrder.IndexOf(AfterhWnd);              // placed BEHIND that window
				_zOrder.Insert(i < 0 ? 0 : i + 1, hWnd);
			}
			_paintVersion++;
			return true;
		}

		/// <summary>Sort key for paint order among siblings: larger paints later, i.e. on top.</summary>
		/// <remarks>
		/// WinForms keeps the z-order in the parent's control collection, where index 0 is the
		/// TOPMOST child -- that is what Control.UpdateZOrderOfChild mirrors out to the driver. Read
		/// it straight from there rather than depending on those calls having been made: a
		/// ListView's header and item pane are implicit children that never generated any, and the
		/// item pane -- which covers the whole control -- was painting over the column headers.
		/// </remarks>
		private int PaintKey(IntPtr h)
		{
			Control c = Control.FromHandle(h);
			Control parent = c?.Parent;
			if (parent != null)
			{
				Control[] siblings = parent.Controls.GetAllControls();
				int i = Array.IndexOf(siblings, c);
				if (i >= 0) return -i;
			}
			int z = _zOrder.IndexOf(h);
			return z < 0 ? int.MinValue : -z;
		}

		/// <summary>The visible windows of <paramref name="root"/>'s subtree in paint order: the
		/// window itself, then its children back-to-front, depth first.</summary>
		private void CollectSubtree(IntPtr root, List<IntPtr> into)
		{
			Hwnd h = Hwnd.ObjectFromHandle(root);
			if (h == null || !h.visible) return;                 // a hidden window hides its children
			into.Add(root);

			var kids = new List<IntPtr>();
			foreach (IntPtr k in new List<IntPtr>(backing.Keys))
			{
				Hwnd c = Hwnd.ObjectFromHandle(k);
				if (c != null && c != h && c.parent == h) kids.Add(k);
			}
			kids.Sort((a, b) => PaintKey(a).CompareTo(PaintKey(b)));
			foreach (IntPtr k in kids) CollectSubtree(k, into);
		}

		/// <summary>Every visible window, in paint order, roots ordered by z.</summary>
		private List<IntPtr> CollectAll(IntPtr firstRoot)
		{
			var roots = new List<IntPtr>();
			foreach (IntPtr k in new List<IntPtr>(backing.Keys))
			{
				Hwnd h = Hwnd.ObjectFromHandle(k);
				if (h != null && h.parent == null && !roots.Contains(k)) roots.Add(k);
			}
			roots.Sort((a, b) =>
			{
				if (a == firstRoot != (b == firstRoot)) return a == firstRoot ? -1 : 1;
				// A popup is over everything: that is what makes it a popup, and it is how the
				// compositor draws one -- the host puts every window no form's subtree contains after
				// its own. This list decides who is POINTED AT as well, and it did not agree: a menu was
				// drawn on top and hit-tested underneath, so moving over an item asked the page behind
				// it instead and no item ever took the highlight.
				bool pa = IsPopupWindow(a), pb = IsPopupWindow(b);
				if (pa != pb) return pa ? 1 : -1;
				return PaintKey(a).CompareTo(PaintKey(b));
			});
			var outl = new List<IntPtr>();
			foreach (IntPtr r in roots) CollectSubtree(r, outl);
			return outl;
		}
		internal override bool SetTopmost(IntPtr hWnd, bool Enabled) => true;
		internal override bool SetOwner(IntPtr hWnd, IntPtr hWndOwner) => true;
		internal override void GrabWindow(IntPtr hwnd, IntPtr ConfineToHwnd) { _grabHandle = hwnd; }
		internal override void UngrabWindow(IntPtr hwnd) { if (_grabHandle == hwnd) _grabHandle = IntPtr.Zero; }
		// ---- cursors -------------------------------------------------------------
		//
		// Nothing here set a cursor, so the pointer stayed an arrow everywhere: no I-beam over
		// text, no hand over a link, and no double arrow over a list view's column divider, which
		// left column resizing with no indication that it was possible at all.
		//
		// A cursor handle in this driver is just a StdCursor plus one, so it is never zero. The
		// host turns that into whatever its platform draws -- IDC_* on Windows, NSCursor on macOS
		// -- which keeps the shape names on this side of the fence portable.
		private readonly Dictionary<IntPtr, int> _cursors = new Dictionary<IntPtr, int>();
		private int _cursorOverride = -1;

		internal static int CursorIdFromHandle(IntPtr cursor)
			=> cursor == IntPtr.Zero ? -1 : (int)cursor - 1;

		/// <summary>Which standard shape a cursor is, or -1 if it is not one of them.
		/// <para>Not every cursor comes from DefineStdCursor: Cursors.VSplit and a couple of others
		/// are built from a .cur resource, and this driver hands out no handle for those -- there is
		/// no GDI here to make one from -- so their handle is zero and the shape was unidentifiable.
		/// That is why a list view's column divider never showed the double arrow however well the
		/// rest of the path worked. Every one of them names itself, and the names are the StdCursor
		/// names, so fall back to that.</para></summary>
		private static int ShapeOf(Cursor cursor)
		{
			int std = CursorIdFromHandle(cursor.handle);
			if (std >= 0) return std;
			if (string.IsNullOrEmpty(cursor.name)) return -1;
			try
			{
				return (int)(StdCursor)Enum.Parse(typeof(StdCursor), cursor.name, false);
			}
			catch (Exception)
			{
				return -1;
			}
		}

		internal override void SetCursor(IntPtr hwnd, IntPtr cursor)
		{
			int id = CursorIdFromHandle(cursor);
			if (id < 0) _cursors.Remove(hwnd); else _cursors[hwnd] = id;
		}

		/// <summary>The StdCursor the pointer should be showing where it currently is, or -1 for
		/// the default.
		/// <para>Asks the control under the pointer what it wants rather than waiting to be told,
		/// which is the more direct route in any case: the window the pointer is over is already
		/// tracked here, so there is nothing to work out from a position.</para></summary>
		internal int GetActiveCursor()
		{
			if (_cursorOverride >= 0) return _cursorOverride;
			for (Hwnd h = Hwnd.ObjectFromHandle(_hotWindow); h != null; h = h.parent)
			{
				if (_cursors.TryGetValue(h.Handle, out int id)) return id;
				try
				{
					Control c = Control.FromHandle(h.Handle);
					// Control.Cursor already inherits from the parent when the control sets none,
					// so the first control that answers settles it.
					if (c != null && c.Cursor != null)
					{
						int std = ShapeOf(c.Cursor);
						if (std >= 0) return std;
					}
				}
				catch (Exception) { }
			}
			return -1;
		}

		internal void SetCursorOverride(IntPtr cursor) => _cursorOverride = CursorIdFromHandle(cursor);
		internal override void ShowCursor(bool show) { }
		internal override void SetCursorPos(IntPtr hwnd, int x, int y) { }
		internal override void GetCursorPos(IntPtr hwnd, out int x, out int y)
		{
			x = _cursorX;
			y = _cursorY;
			if (hwnd == IntPtr.Zero) return;
			// Asked about a window, Win32 answers in that window's client space.
			Hwnd h = Hwnd.ObjectFromHandle(hwnd);
			if (h == null) return;
			Point origin = ScreenLocation(h);
			x -= origin.X;
			y -= origin.Y;
		}
		// Text caret: WinForms drives it via CreateCaret/SetCaretPos/CaretVisible/DestroyCaret.
		// We just track its window + client rect + logical visibility; the host draws a blinking
		// vertical bar at the screen position (the backing bitmaps don't contain the caret).
		private IntPtr _caretHwnd; private int _caretX, _caretY, _caretW = 1, _caretH; private bool _caretVisible;

		internal override void CreateCaret(IntPtr hwnd, int width, int height)
		{ _caretHwnd = hwnd; _caretW = Math.Max(1, width); _caretH = height; }
		internal override void SetCaretPos(IntPtr hwnd, int x, int y)
		{ _caretHwnd = hwnd; _caretX = x; _caretY = y; }
		internal override void CaretVisible(IntPtr hwnd, bool visible)
		{ if (hwnd == _caretHwnd) _caretVisible = visible; }
		internal override void DestroyCaret(IntPtr hwnd)
		{ if (hwnd == _caretHwnd) { _caretHwnd = IntPtr.Zero; _caretVisible = false; } }

		/// <summary>Caret screen rect + logical visibility (the host blinks/draws it).</summary>
		internal bool GetCaret(out int screenX, out int screenY, out int width, out int height)
		{
			screenX = screenY = width = height = 0;
			if (!_caretVisible || _caretHwnd == IntPtr.Zero) return false;
			Hwnd h = Hwnd.ObjectFromHandle(_caretHwnd);
			if (h == null || !h.visible) return false;
			Point p = ScreenLocation(h);
			screenX = p.X + _caretX; screenY = p.Y + _caretY; width = _caretW; height = _caretH;
			return true;
		}
		internal override void RequestNCRecalc(IntPtr hwnd) { }
		internal override void RequestAdditionalWM_NCMessages(IntPtr hwnd, bool hover, bool leave) { }
	}
}
