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
		// Two WPF UI threads can host WinForms at once (WindowsFormsHost ticks per Dispatcher), and both
		// reach here first thing: an unguarded check-then-create made two drivers, each with half the
		// windows.
		public static XplatUIWebGpu GetInstance()
		{
			XplatUIWebGpu i = System.Threading.Volatile.Read(ref instance);
			if (i != null) return i;
			lock (s_instanceLock)
			{
				if (instance == null) System.Threading.Volatile.Write(ref instance, new XplatUIWebGpu());
				return instance;
			}
		}
		private static readonly object s_instanceLock = new object();
		private XplatUIWebGpu()
		{
			// the driver's windows in the process's focus model (XplatUIWebGpu.Focus.cs)
			EnsureFocusRegistered();
			// a head whose screen is the application's main window: every other window is framed (WindowChrome)
			if (OperatingSystem.IsBrowser()) FramedWindow = WindowChrome.IsFramed;
			// Graphics.FromHwnd(control.Handle): this driver's windows are not GDI windows anywhere, so
			// System.Drawing asks here what drawing on one means -- CreateGraphics' answer.
			AppContext.SetData ("System.Drawing.HwndGraphicsResolver",
				(Func<IntPtr, object>)(h => Hwnd.ObjectFromHandle (h) != null ? GetHwndGraphics (h) : null));
			// Windows: the system clipboard is user32's, whether or not a WPF host lends its own.
			if (OperatingSystem.IsWindows ())
				ClipboardBridge ??= new Win32ClipboardBridge ();
			// macOS: the screens are the Mac's displays, known before any form places itself.
			if (OperatingSystem.IsMacOS ())
				CocoaHost.InstallScreens ();
		}

		/// <summary>The next window handle. On Windows a handle is skipped while it names a real
		/// window: an app passes Control.Handle to user32 as well, and wpfrun routes a call to the
		/// stack only when the handle is not Windows' own, so the two must never be equal.</summary>
		private IntPtr NextHandle ()
		{
			while (true)
			{
				IntPtr h = (IntPtr)(System.Threading.Interlocked.Increment(ref next_handle) - 1);
				if (!OperatingSystem.IsWindows () || IsRealWindow (h) == 0) return h;
			}
		}

		[System.Runtime.InteropServices.DllImport ("user32", EntryPoint = "IsWindow")]
		private static extern int IsRealWindow (IntPtr hwnd);

		// Cursors belong to the OS compositor on every head this driver serves (Wayland sets them by
		// shape, macOS by NSCursor, the browser by CSS), so DefineCursor ignores its bitmaps. Saying so
		// lets Cursor skip decoding them -- which is what keeps this driver free of libgdiplus, since
		// System.Drawing.Bitmap has no managed backend and every Bitmap is a GDI+ object.
		internal override bool CursorBitmapsUsed { get { return false; } }

		private int next_handle = 1;
		// Concurrent: every UI thread (a splash screen on its own thread, say) creates and composites windows
		// while the others do, and copying the keys of a plain Dictionary another thread was adding to
		// threw IndexOutOfRangeException in the present (GwxConfigApp died at its splash screen).
		private readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, Bitmap> backing = new System.Collections.Concurrent.ConcurrentDictionary<IntPtr, Bitmap>();
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
			internal readonly System.Threading.Thread Owner = System.Threading.Thread.CurrentThread;
			internal int DrainPosted;
			/// <summary>Set when something is posted, so an idle loop wakes for it at once rather than
			/// at its next poll.</summary>
			internal readonly System.Threading.AutoResetEvent Posted = new System.Threading.AutoResetEvent(false);
		}

		[ThreadStatic] private static MsgQueue t_queue;
		private static MsgQueue CurrentQueue => t_queue ??= new MsgQueue();

		// A thread whose loop is WPF's Dispatcher, not Application.Run: what WinForms posts there (a
		// Control.BeginInvoke, a WindowsFormsSynchronizationContext.Post) is in this driver's queue,
		// which only WinForms' own loop reads. On Windows it is a window message, and WPF's loop
		// dispatches it like any other. So the owner thread's Dispatcher is asked to drain the queue
		// -- once per batch -- whenever something is posted to it outside a WinForms loop.
		private static readonly Type s_dispatcherType = Type.GetType("System.Windows.Threading.Dispatcher, WindowsBase", throwOnError: false);
		private static readonly System.Reflection.MethodInfo s_fromThread = s_dispatcherType?.GetMethod("FromThread", new[] { typeof(System.Threading.Thread) });
		private static readonly System.Reflection.MethodInfo s_beginInvoke = s_dispatcherType?.GetMethod("BeginInvoke", new[] { typeof(Delegate), typeof(object[]) });

		private static void WakeWpfLoop(MsgQueue q)
		{
			if (s_fromThread == null || s_beginInvoke == null || q.Owner == System.Threading.Thread.CurrentThread && Application.MessageLoop) return;
			object dispatcher;
			try { dispatcher = s_fromThread.Invoke(null, new object[] { q.Owner }); }
			catch (System.Reflection.TargetInvocationException) { return; }
			if (dispatcher == null || System.Threading.Interlocked.Exchange(ref q.DrainPosted, 1) == 1) return;
			Action drain = () =>
			{
				System.Threading.Volatile.Write(ref q.DrainPosted, 0);
				if (!Application.MessageLoop) Application.DoEvents();   // inside Application.Run its own loop reads the queue
			};
			try { s_beginInvoke.Invoke(dispatcher, new object[] { drain, Array.Empty<object>() }); }
			catch (System.Reflection.TargetInvocationException) { System.Threading.Volatile.Write(ref q.DrainPosted, 0); }
		}

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
			q.Posted.Set();
			WakeWpfLoop(q);
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
		// WF_DRIVER_TRACE=1: what the driver is asked to do; =stack: with who asked, where that is the question
		private static readonly bool Trace = Environment.GetEnvironmentVariable("WF_DRIVER_TRACE") is "1" or "stack";
		private static readonly bool TraceStacks = Environment.GetEnvironmentVariable("WF_DRIVER_TRACE") == "stack";
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
		/// A child window with no parent: a control created before it was added anywhere, or taken
		/// out of its parent. Windows keeps such a window in WinForms' hidden parking window, where
		/// nobody sees it; as a parentless root here it was composited over the form at 0,0 (a
		/// ribbon's off-screen template control covered the real ribbon).
		/// </summary>
		private static bool IsParkedChild(Hwnd h) =>
			(h.initial_style & WindowStyles.WS_CHILD) != 0 && (h.initial_style & WindowStyles.WS_POPUP) == 0;

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

		/// <summary>Whether the window is a popup surface or inside one: the controls a popup is made
		/// of (DotNetBar's menu panel in its popup container) are ordinary children, and leaving them
		/// out of the frame left an empty popup -- a context menu that showed only its drop shadow.</summary>
		internal bool IsInPopupWindow(IntPtr handle)
		{
			for (Hwnd h = Hwnd.ObjectFromHandle(handle); h != null; h = h.parent)
				if ((h.initial_style & WindowStyles.WS_POPUP) != 0) return true;
			return false;
		}

		/// <summary>Paint the border a control's window style asks for over the window's own outer
		/// edge, after the control has finished painting -- for the windows whose frame is not
		/// modelled. A child's border (ChildFrame) is non-client, as in Win32, and drawn outside
		/// its client by AddChildFrame instead; a top-level window's belongs to the host.</summary>
		private void DrawWindowBorder(IntPtr handle, Graphics dc)
		{
			if (dc == null) return;
			Hwnd h = Hwnd.ObjectFromHandle(handle);
			if (h == null || h.width <= 0 || h.height <= 0) return;

			bool sunken = (h.initial_ex_style & WindowExStyles.WS_EX_CLIENTEDGE) != 0;
			bool plain = (h.initial_style & WindowStyles.WS_BORDER) != 0;
			bool staticEdge = (h.initial_ex_style & WindowExStyles.WS_EX_STATICEDGE) != 0;
			if (!sunken && !plain && !staticEdge) return;

			// A top-level window's frame belongs to the host, which draws a real one around it; a
			// child's modelled frame is drawn outside its client (AddChildFrame).
			if (Control.FromHandle(handle) is Form || HasChildFrame(handle, h)) return;
			DrawFrame(handle, h, dc, h.width, h.height);
		}

		/// <summary>The border a window's style asks for, over a window <paramref name="width"/> x
		/// <paramref name="height"/> drawn from 0,0.</summary>
		private void DrawFrame(IntPtr handle, Hwnd h, Graphics dc, int width, int height)
		{
			bool sunken = (h.initial_ex_style & WindowExStyles.WS_EX_CLIENTEDGE) != 0;
			bool plain = (h.initial_style & WindowStyles.WS_BORDER) != 0;
			bool staticEdge = (h.initial_ex_style & WindowExStyles.WS_EX_STATICEDGE) != 0;
			Control c = Control.FromHandle(handle);
			// The square between two non-client scroll bars, which Windows' frame paints.
			var ws = (WindowStyles)h.initial_style;
			if ((ws & WindowStyles.WS_HSCROLL) != 0 && (ws & WindowStyles.WS_VSCROLL) != 0)
			{
				int edge = BorderEdge((int)h.initial_style, (int)h.initial_ex_style);
				int vw = SystemInformation.VerticalScrollBarWidth, hh = SystemInformation.HorizontalScrollBarHeight;
				try { ThemeEngine.Current.DrawScrollBarCorner(dc, new Rectangle(width - edge - vw, height - edge - hh, vw, hh)); }
				catch (Exception ex) { T("DrawFrame corner: " + ex.Message); }
			}
			if (!sunken && !plain && !staticEdge) return;   // a frame of scroll bars alone

			if (staticEdge && !sunken && !plain)
			{
				// win32k's xxxDrawWindowFrame: DrawEdge(BDR_SUNKENOUTER) -- COLOR_BTNSHADOW along the
				// top and left, then COLOR_BTNHIGHLIGHT along the bottom and right, over the corners.
				try
				{
					using (var dark = new SolidBrush(SystemColors.ControlDark))
					using (var light = new SolidBrush(SystemColors.ControlLightLight))
					{
						dc.FillRectangle(dark, 0, 0, width - 1, 1);
						dc.FillRectangle(dark, 0, 0, 1, height - 1);
						dc.FillRectangle(light, 0, height - 1, width, 1);
						dc.FillRectangle(light, width - 1, 0, 1, height);
					}
				}
				catch (Exception ex) { T("DrawWindowBorder: " + ex.Message); }
				return;
			}

			try { ThemeEngine.Current.DrawControlBorder(dc, new Rectangle(0, 0, width, height), c, sunken); }
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
		private static Size s_screenSet;
		private static Size s_screen { get => s_screenSet.IsEmpty ? InitialScreen() : s_screenSet; set => s_screenSet = value; }

		/// <summary>Until a host reports its surface: on Windows the primary monitor's work area (a
		/// splash screen centres itself on it before any host exists -- on an imaginary 2560x1440
		/// desktop it came up off centre); elsewhere a large desktop.</summary>
		private static Size InitialScreen()
		{
			if (OperatingSystem.IsWindows())
			{
				// In the application's own pixels: the work area as a per-monitor or system-aware
				// application sees it (device pixels) or as an unaware one does (96 dpi). Asked in
				// whatever context the calling thread happens to be in, a system-aware splash centred
				// itself on a half-size screen.
				IntPtr old = IntPtr.Zero;
				try { old = SetThreadDpiAwarenessContext(ScaleHelper.IsDeviceScaled ? (IntPtr)(-4) : (IntPtr)(-1)); }
				catch (EntryPointNotFoundException) { }
				try
				{
					var r = new WorkArea();
					if (SystemParametersInfoW(0x0030 /* SPI_GETWORKAREA */, 0, ref r, 0) && r.Right > r.Left && r.Bottom > r.Top)
						return new Size(r.Right - r.Left, r.Bottom - r.Top);
				}
				finally
				{
					if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old);
				}
			}
			return new Size(2560, 1440);
		}

		[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
		private struct WorkArea { public int Left, Top, Right, Bottom; }
		[System.Runtime.InteropServices.DllImport("user32")]
		private static extern bool SystemParametersInfoW(uint action, uint param, ref WorkArea r, uint winIni);
		[System.Runtime.InteropServices.DllImport("user32")]
		private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

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

		/// <summary>Set by a host whose windows are real windows on a real desktop (Win32Host): the
		/// monitors, in the application's coordinates. There a popup is a window of its own, not
		/// something drawn inside one surface, and the main window is wherever the user put it -- so
		/// "the screen" is the desktop. Measured against the main window's client size instead, a
		/// property grid's drop-down in a window moved off the origin was pushed back inside that
		/// imaginary screen: it opened to the left, away from its cell.</summary>
		internal static Func<Screen[]> ScreensProvider;

		internal override Rectangle VirtualScreen
		{
			get
			{
				if (ScreensProvider?.Invoke() is { Length: > 0 } all)
				{
					Rectangle u = all[0].Bounds;
					foreach (Screen sc in all) u = Rectangle.Union(u, sc.Bounds);
					return u;
				}
				return new Rectangle(Point.Empty, s_screen);
			}
		}
		internal override Rectangle WorkingArea
		{
			get
			{
				if (ScreensProvider?.Invoke() is { Length: > 0 } all)
					foreach (Screen sc in all) if (sc.Primary) return sc.WorkingArea;
				return new Rectangle(Point.Empty, s_screen);
			}
		}
		internal override Screen[] AllScreens
		{
			get
			{
				if (ScreensProvider?.Invoke() is { Length: > 0 } all) return all;
				return new[] { new Screen(true, "WebGpu Primary Display", VirtualScreen, WorkingArea) };
			}
		}

		/// <summary>Absolute (screen) top-left of a window, walking up the parent chain.</summary>
		/// <remarks>
		/// A window is positioned against its parent's CLIENT origin, and on Windows a plain border is
		/// non-client -- so a child of a bordered container starts one pixel in from the container's
		/// outer edge. This driver models no non-client area, so that pixel has to be added back here;
		/// without it everything inside a bordered Panel sat a pixel up and left of Windows'.
		/// </remarks>
		internal Point ScreenLocation(Hwnd hwnd)
		{
			int x = 0, y = 0;
			for (Hwnd h = hwnd; h != null; h = h.parent)
			{
				x += h.x; y += h.y;
				if (h.parent != null)
				{
					Size inset = ClientOrigin(h.parent, FrameOf(h.parent.Handle));
					x += inset.Width; y += inset.Height;
				}
			}
			return new Point(x, y);
		}

		/// <summary>How far a window's client area starts in from its own top-left, for a window
		/// whose frame is not modelled: a plain WS_BORDER's pixel. A modelled frame (ChildFrame) is
		/// already in hwnd.x/y, which are the client's.</summary>
		private static Size ClientOrigin(Hwnd h, Padding frame)
		{
			// A modelled child frame (ChildFrame) already put the client inside the border: hwnd.x is
			// the client's own position, so nothing is added back.
			if (frame != Padding.Empty) return Size.Empty;
			if (h == null || (h.initial_style & WindowStyles.WS_BORDER) == 0) return Size.Empty;
			// A top-level window's frame belongs to the host, as in DrawWindowBorder.
			return Control.FromHandle(h.Handle) is Form ? Size.Empty : new Size(1, 1);
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

		/// <summary>The form whose window the input being delivered arrived in, set by the host
		/// around it; Zero when unknown. Hit testing then looks at what that window shows -- the
		/// form and its popups -- and not at every window of the driver: a dialog's OK button lies
		/// over its owner, and the click went to the owner's controls underneath instead.</summary>
		[ThreadStatic] internal static IntPtr InputRoot;

		/// <summary>Whether a popup surface (a menu, a drop-down) is what the user points at in
		/// <paramref name="form"/>'s window at this driver point. A hosted WPF tree is a real child
		/// window under the host's frame, and the OS gives it the mouse wherever it is -- also where
		/// one of this driver's popups is drawn over it; its host asks this to let such input through
		/// to the host window instead (ElementHost, WM_NCHITTEST).</summary>
		internal bool IsUnderPopup(IntPtr form, int screenX, int screenY)
		{
			IntPtr saved = InputRoot;
			InputRoot = form;
			try
			{
				IntPtr h = WindowAtPoint(screenX, screenY);
				return h != IntPtr.Zero && IsInPopupWindow(h);
			}
			finally { InputRoot = saved; }
		}

		internal IntPtr WindowAtPoint(int screenX, int screenY)
		{
			// Paint order decides: whatever is drawn last is what the user is pointing at, so scan
			// the same list backwards. Depth alone could not separate two siblings that overlap --
			// a ListView's column header sits on top of its item pane, both filling the control.
			IntPtr root = InputRoot;
			if (root != IntPtr.Zero && Hwnd.ObjectFromHandle(root) == null) root = IntPtr.Zero;
			List<IntPtr> ordered = CollectAll(root);
			for (int i = ordered.Count - 1; i >= 0; i--)
			{
				Hwnd h = Hwnd.ObjectFromHandle(ordered[i]);
				if (h == null) continue;
				if (!Hits(ordered[i], h, screenX, screenY)) continue;
				return ordered[i];
			}
			return IntPtr.Zero;
		}

		/// <summary>Whether a point is over a window: its client area (unless a layered window
		/// lets it through there), or the frame round it a child keeps (ChildFrame) -- the border
		/// and scroll-bar bands Windows hit-tests as the child's non-client area, not as whatever
		/// is behind.</summary>
		private bool Hits(IntPtr handle, Hwnd h, int screenX, int screenY)
		{
			// A child window exists only inside its parents' client areas: Windows clips it there, for
			// the mouse as for painting. Tested against its own rectangle alone, a control that runs past
			// its parent took clicks meant for whatever is drawn there -- GraphWorX's Toolbox (a tall
			// ToolStrip in a scrolling panel) took the clicks on its dock's tab strip below it, so the
			// Explorer tab could only be clicked once the Toolbox was scrolled to its end.
			if (!InsideParents(h, screenX, screenY)) return false;
			Point p = ScreenLocation(h);
			if (screenX >= p.X && screenY >= p.Y && screenX < p.X + h.width && screenY < p.Y + h.height)
				return !PassesMouseThrough(h, screenX - p.X, screenY - p.Y) && !AnswersTransparent(handle, h, screenX, screenY);
			return InChildFrame(handle, h, screenX - p.X, screenY - p.Y);
		}

		/// <summary>Whether a screen point is inside the client area of every parent of <paramref name="h"/>.</summary>
		private bool InsideParents(Hwnd h, int screenX, int screenY)
		{
			for (Hwnd a = h.parent; a != null; a = a.parent)
			{
				Point ap = ScreenLocation(a);
				if (screenX < ap.X || screenY < ap.Y || screenX >= ap.X + a.width || screenY >= ap.Y + a.height)
					return false;
			}
			return true;
		}

		/// <summary>
		/// A transparent child window (WS_EX_TRANSPARENT, not layered) that answers WM_NCHITTEST with
		/// HTTRANSPARENT at the point: Windows then hands the mouse to the window underneath, as it does
		/// for any window of the thread. The designer's adorner window lies over the whole design surface
		/// this way and takes the pointer only over a glyph that wants it. Asked of those windows alone,
		/// so the hit test of every other window costs nothing more.
		/// </summary>
		private bool AnswersTransparent(IntPtr handle, Hwnd h, int screenX, int screenY)
		{
			if ((h.initial_ex_style & WindowExStyles.WS_EX_TRANSPARENT) == 0 || (h.initial_ex_style & WindowExStyles.WS_EX_LAYERED) != 0)
				return false;
			IntPtr lp = (IntPtr)((screenY << 16) | (screenX & 0xFFFF));
			return (int)SendMessage(handle, Msg.WM_NCHITTEST, IntPtr.Zero, lp).ToInt64() == HTTRANSPARENT;
		}

		/// <summary>A client-relative point in the frame a child window keeps round its client.</summary>
		private bool InChildFrame(IntPtr handle, Hwnd h, int cx, int cy)
		{
			if (!HasChildFrame(handle, h)) return false;
			Padding f = FrameOf(handle);
			if (!(cx >= -f.Left && cy >= -f.Top && cx < h.width + f.Right && cy < h.height + f.Bottom
				&& (cx < 0 || cy < 0 || cx >= h.width || cy >= h.height)))
				return false;
			// A child is clipped to its parent's client: a frame standing outside it is not there.
			if (h.parent != null)
			{
				Point me = ScreenLocation(h), pp = ScreenLocation(h.parent);
				int px = me.X + cx - pp.X, py = me.Y + cy - pp.Y;
				if (px < 0 || py < 0 || px >= h.parent.width || py >= h.parent.height) return false;
			}
			return true;
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
				if (!Hits(ordered[i], h, screenX, screenY)) continue;
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
			var m = Message.Create(target, (int)Msg.WM_MOUSEWHEEL, wParam, lp);
			if (!FilteredOut(ref m)) SendMessage(m.HWnd, (Msg)m.Msg, m.WParam, m.LParam);
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
			if (InputTrace.On) InputTrace.Log($"hover #{InputTrace.Seq} leave {InputTrace.Describe(previous)} enter {InputTrace.Describe(target)}");
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

		/// <summary>WF_SYNTHETIC_CURSOR=1 (diagnostics): the pointer is where the last injected mouse
		/// message put it, never the real one -- so input injected into an instance running off
		/// screen behaves as if the pointer were there.</summary>
		private static readonly bool s_syntheticCursor = Environment.GetEnvironmentVariable("WF_SYNTHETIC_CURSOR") == "1";

		/// <summary>Where the pointer is, in this driver's screen space.</summary>
		private int _cursorX, _cursorY;

		/// <summary>A press the host saw outside the driver's windows (on an embedded WPF window):
		/// where the pointer was, as that message's pt says on Windows.</summary>
		internal void NotePointer(int screenX, int screenY)
		{
			_cursorX = screenX;
			_cursorY = screenY;
		}

		// The last press, for double clicks: which window, which button, when and where.
		private IntPtr _pressTarget;
		private Msg _pressMessage;
		private long _pressTime;
		private int _pressX, _pressY;

		/// <summary>A press as Windows reports it: WM_xBUTTONDBLCLK when it is the second of a pair
		/// (same window and button, within GetDoubleClickTime and SM_CXDOUBLECLK x SM_CYDOUBLECLK of
		/// the first); the press itself otherwise. A third press starts a new pair, as on Windows.</summary>
		private Msg DoubleClickOf(IntPtr target, int screenX, int screenY, Msg message)
		{
			Msg dbl = message switch {
				Msg.WM_LBUTTONDOWN => Msg.WM_LBUTTONDBLCLK,
				Msg.WM_RBUTTONDOWN => Msg.WM_RBUTTONDBLCLK,
				Msg.WM_MBUTTONDOWN => Msg.WM_MBUTTONDBLCLK,
				_ => 0,
			};
			if (dbl == 0) return message;
			long now = Environment.TickCount64;
			Size area = SystemInformation.DoubleClickSize;
			if (target == _pressTarget && message == _pressMessage && now - _pressTime <= SystemInformation.DoubleClickTime
				&& Math.Abs(screenX - _pressX) <= area.Width / 2 && Math.Abs(screenY - _pressY) <= area.Height / 2)
			{
				_pressTarget = IntPtr.Zero;
				return dbl;
			}
			_pressTarget = target; _pressMessage = message; _pressTime = now; _pressX = screenX; _pressY = screenY;
			return message;
		}

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
			// Windows gives a disabled window no mouse input: a disabled control's message goes to
			// its parent (WindowFromPoint passes it over), and inside a disabled top-level window --
			// the owner of a modal dialog -- nothing gets it at all. Delivered regardless, the buttons
			// behind a login dialog could be clicked, and so could a button whose Enabled is false.
			if (!h.Enabled)
			{
				while (h != null && !h.enabled && h.parent != null) h = h.parent;
				if (h == null || !h.Enabled) return;
				target = h.Handle;
			}
			Point p = ScreenLocation(h);
			int cx = screenX - p.X, cy = screenY - p.Y;
			IntPtr lp = (IntPtr)((cy << 16) | (cx & 0xFFFF));

			// Windows turns a second press of the same button on the same window, soon enough and near
			// enough to the first, into WM_xBUTTONDBLCLK (every WinForms window class has CS_DBLCLKS).
			// Nothing here did, so no control ever saw a double click: not a list item opening, not a
			// ribbon caption maximizing its window.
			message = DoubleClickOf(target, screenX, screenY, message);

			// Announce a press before delivering it, so a control holding a drop-down open hears about
			// clicks that are none of its business -- the one thing it needs in order to close.
			if (message == Msg.WM_LBUTTONDOWN || message == Msg.WM_RBUTTONDOWN
				|| message == Msg.WM_MBUTTONDOWN)
				XplatUI.RaiseMousePress(target);

			// On a child's border, not its client: the non-client message, as Windows sends it --
			// WM_NC* with the hit-test code (HTBORDER) and the point in screen coordinates. The click
			// is the child's, and none of it reaches the window behind.
			if (_grabHandle == IntPtr.Zero && InChildFrame(target, h, cx, cy) && NonClientMessage(message) is int nc)
			{
				var n = Message.Create(target, nc, (IntPtr)HTBORDER, (IntPtr)((screenY << 16) | (screenX & 0xFFFF)));
				if (FilteredOut(ref n)) return;
				SendMessage(n.HWnd, (Msg)n.Msg, n.WParam, n.LParam);
				return;
			}

			var m = Message.Create(target, (int)message, (IntPtr)wParam, lp);
			if (FilteredOut(ref m)) { if (InputTrace.On) InputTrace.Log($"disp #{InputTrace.Seq} {message} taken by a message filter"); return; }
			double t0 = InputTrace.On ? InputTrace.Now : 0;
			SendMessage(m.HWnd, (Msg)m.Msg, m.WParam, m.LParam);
			if (InputTrace.On) InputTrace.Log($"disp #{InputTrace.Seq} {message} -> {InputTrace.Describe(target)} at ({cx},{cy}) handled in {InputTrace.Now - t0:0.00} ms");
		}

		private const int HTBORDER = 18;

		/// <summary>WM_NCMOUSEMOVE, WM_NCLBUTTONDOWN... for a client mouse message.</summary>
		private static int? NonClientMessage(Msg message) => message switch {
			Msg.WM_MOUSEMOVE => 0x00A0,
			Msg.WM_LBUTTONDOWN => 0x00A1,
			Msg.WM_LBUTTONUP => 0x00A2,
			Msg.WM_RBUTTONDOWN => 0x00A4,
			Msg.WM_RBUTTONUP => 0x00A5,
			Msg.WM_MBUTTONDOWN => 0x00A7,
			Msg.WM_MBUTTONUP => 0x00A8,
			_ => null,
		};

		/// <summary>Offers an input message to the application's message filters first,as a message
		/// loop does with everything it takes off the queue (Application.AddMessageFilter). Input here
		/// goes to the window directly, not through the queue, so no filter ever saw a click or a key:
		/// DotNetBar closes its menus and drop-downs from a filter -- a click outside them, a click on
		/// an item -- and its File menu could neither be used nor closed. True: a filter took it.</summary>
		private static bool FilteredOut(ref Message m)
		{
			try
			{
				return Application.FilterMessage(ref m);
			}
			catch (Exception ex)
			{
				T("message filter: " + ex.Message);
				return false;
			}
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
			var m = Message.Create(target, (int)Msg.WM_MOUSEWHEEL, wParam, lp);
			if (!FilteredOut(ref m)) SendMessage(m.HWnd, (Msg)m.Msg, m.WParam, m.LParam);
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
			if (OperatingSystem.IsMacOS()) CocoaHost.DrainMainWork();   // a busy main thread still does other threads' window work
			var hwnd = new Hwnd();
			// WinForms passes the WINDOW rectangle; keep the client one (see _frames).
			Padding frame = TopLevelFrame(cp.Style, cp.ExStyle, cp.menu != null, CreateDpi(cp), cp.control);
			int w = Math.Max(1, cp.Width - frame.Horizontal), h = Math.Max(1, cp.Height - frame.Vertical);
			hwnd.x = (cp.X == int.MinValue ? 0 : cp.X) + frame.Left;
			hwnd.y = (cp.Y == int.MinValue ? 0 : cp.Y) + frame.Top;
			hwnd.width = w; hwnd.height = h;
			hwnd.initial_style = cp.WindowStyle;
			hwnd.initial_ex_style = cp.WindowExStyle;
			if (cp.Parent != IntPtr.Zero)
				hwnd.parent = Hwnd.ObjectFromHandle(cp.Parent);

			IntPtr handle = NextHandle ();
			if (frame != Padding.Empty) _frames[handle] = frame;
			_styles[handle] = (cp.Style, cp.ExStyle, cp.menu != null);
			// A control's own scroll bars and size box are the windows that stand in its non-client
			// scroll-bar bands when it has them (WS_HSCROLL / WS_VSCROLL).
			if (cp.control is ImplicitHScrollBar || cp.control is ImplicitVScrollBar || cp.control is SizeGrip)
				SetNonClientChild(handle, true);
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

			T($"CreateWindow h=0x{handle.ToInt64():x} {cp.control?.GetType().Name ?? cp.ClassName} {hwnd.x},{hwnd.y} {w}x{h} style=0x{cp.Style:x} ex=0x{cp.ExStyle:x} parent=0x{cp.Parent.ToInt64():x}");
			Text(handle, cp.Caption);
			AdoptDisplayDpi(handle, cp);
			SendMessage(handle, Msg.WM_CREATE, (IntPtr)1, IntPtr.Zero);
			// A window that draws its own frame says so in answer to WM_NCCALCSIZE (see AskFrame).
			if (frame != Padding.Empty)
				ApplyFrame (handle, AskFrame (handle, frame));
			return handle;
		}

		internal override IntPtr CreateWindow(IntPtr Parent, int X, int Y, int Width, int Height)
		{
			var cp = new CreateParams { Parent = Parent, X = X, Y = Y, Width = Width, Height = Height, Style = 0 };
			return CreateWindow(cp);
		}

		internal override void DestroyWindow(IntPtr handle)
		{
			T($"DestroyWindow h=0x{handle.ToInt64():x}" + (TraceStacks ? "\n" + Environment.StackTrace : ""));
			// Win32 destroys a window's children along with it, and each of them gets its own
			// WM_DESTROY -- which is what makes WinForms clear Control.Created. Destroying only this
			// window left every child still marked created, with a parent that no longer existed, so
			// the next Control.CreateControl on that tree returned immediately ("already created")
			// and none of them ever got a handle again. A hosted panel rendered the first time it
			// was shown and was empty every time after, once it had been swapped away and back.
			foreach (IntPtr child in ChildHandles(handle)) DestroyWindow(child);

			if (backing.TryGetValue(handle, out Bitmap b)) { b?.Dispose(); backing.TryRemove(handle, out _); }
			lock (window_queue) window_queue.Remove(handle);
			if (_hotWindow == handle) _hotWindow = IntPtr.Zero;
			captions.Remove(handle);
			_scenes.Remove(handle);
			_layered.Remove(handle);
			_layeredPixels.Remove(handle);
			_layeredBits.Remove(handle);
			_reveals.Remove(handle);
			_frames.Remove(handle);
			_styles.Remove(handle);
			SetNonClientChild(handle, false);
			_paintVersion++;   // a window disappeared from the composite
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd != null) { SendMessage(handle, Msg.WM_DESTROY, IntPtr.Zero, IntPtr.Zero); hwnd.Dispose(); }
		}

		/// <summary>
		/// A window whose thread has ended with it open: Windows destroyed the real window with the
		/// thread, so this one and everything in it go from the driver's tables too, or a hit test, a
		/// window listing or a click could still land on a window that is not there. Nothing is sent
		/// to the controls (their thread is gone and their code is not to run elsewhere).
		/// </summary>
		internal void ForgetWindowTree(IntPtr handle)
		{
			foreach (IntPtr child in ChildHandles(handle)) ForgetWindowTree(child);
			if (backing.TryGetValue(handle, out Bitmap b)) { b?.Dispose(); backing.TryRemove(handle, out _); }
			lock (window_queue) window_queue.Remove(handle);
			if (_hotWindow == handle) _hotWindow = IntPtr.Zero;
			captions.Remove(handle);
			_scenes.Remove(handle);
			_layered.Remove(handle);
			_layeredPixels.Remove(handle);
			_layeredBits.Remove(handle);
			_reveals.Remove(handle);
			_frames.Remove(handle);
			_styles.Remove(handle);
			SetNonClientChild(handle, false);
			_paintVersion++;
			Hwnd.ObjectFromHandle(handle)?.Dispose();
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
			if (OperatingSystem.IsMacOS()) CocoaHost.DrainMainWork();   // a busy main thread still does other threads' window work
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd == null) return false;
			if (Trace && hwnd.visible != visible)
				T($"SetVisible h=0x{handle.ToInt64():x} {visible}" + (TraceStacks ? "\n" + Environment.StackTrace : ""));
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
			if (OperatingSystem.IsMacOS()) CocoaHost.DrainMainWork();   // a busy main thread still does other threads' window work
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd == null) return;
			Padding frame = FrameOf(handle);
			// Win32 asks a framed window for its frame again whenever it is sized: a form that draws
			// its own caption answers differently maximized.
			Padding standard = StandardFrame(handle);
			if (standard != Padding.Empty && !_askingFrame
			    && (width != hwnd.width + frame.Horizontal || height != hwnd.height + frame.Vertical))
			{
				Padding asked = AskFrame(handle, standard);
				if (asked != frame)
				{
					if (asked == Padding.Empty) _frames.Remove(handle); else _frames[handle] = asked;
					frame = asked;
					FrameChanged?.Invoke(handle);
				}
			}
			x += frame.Left; y += frame.Top;
			// A window may be made zero wide or zero high, as on Windows: libraries collapse a
			// panel that way (DotNetBar's DockSite sets Height = 0 when every bar in it is hidden).
			// This used to apply a size only when both were positive, so the collapse was dropped
			// and the empty dock area kept its space.
			width = Math.Max(0, width - frame.Horizontal);
			height = Math.Max(0, height - frame.Vertical);
			hwnd.x = x; hwnd.y = y;
			bool resized = width != hwnd.width || height != hwnd.height;
			if (resized)
			{
				hwnd.width = width; hwnd.height = height;
				if (!s_gpuRaster)
				{
					if (backing.TryGetValue(handle, out Bitmap old)) old?.Dispose();
					backing[handle] = new Bitmap(Math.Max(1, width), Math.Max(1, height));
				}
				// Windows repaints a resized window -- what it uncovers, and all of it for the native
				// controls whose class asks for that (CS_HREDRAW | CS_VREDRAW: combo box, list box,
				// button). Here a picture recorded at the old size stayed: DotNetBar's combo boxes,
				// painted once at the default 121 wide and then made narrower, lost their arrows.
				if (s_gpuRaster) Invalidate(handle, Rectangle.Empty, false);
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
			Padding frame = FrameOf(handle);
			x = hwnd.x - frame.Left; y = hwnd.y - frame.Top;
			width = hwnd.width + frame.Horizontal; height = hwnd.height + frame.Vertical;
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
			if (OperatingSystem.IsMacOS()) CocoaHost.DrainMainWork();   // a busy main thread still does other threads' window work
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
			if (InputTrace.On) InputTrace.Log($"inval #{InputTrace.Seq} {InputTrace.Describe(handle)} {rc.X},{rc.Y} {rc.Width}x{rc.Height}{(hwnd.expose_pending ? " (paint already pending)" : "")}");
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
			T($"PaintEventStart h=0x{handle.ToInt64():x} client={client} invalid={hwnd?.Invalid} size={hwnd?.width}x{hwnd?.height}");
			if (InputTrace.On) { _paintStarted[handle] = InputTrace.Now; }
			Graphics dc;
			if (s_gpuRaster)
			{
				if (!backing.ContainsKey(handle)) backing[handle] = null;      // keep the window registry
				dc = System.Drawing.WebGpuBackend.GpuRaster.NewRecording();    // records; NO libgdiplus backing
				dc.RecordingSurface = new Size(hwnd.width, hwnd.height);        // what GetHdc's DC covers
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

		/// <summary>BeginPaint (repaint: true) or ValidateRect from code that answers WM_PAINT itself,
		/// through GDI rather than through WinForms' paint: the window is valid again, so the next
		/// invalidation asks for a new paint, and a repaint starts the window's picture afresh. Without
		/// it the paint was never marked done -- every later Invalidate found one still pending and
		/// queued none, so the window kept its first picture for good (DotNetBar's combo boxes kept
		/// the one from before they were sized) -- and each repaint was drawn over all the earlier ones.</summary>
		/// <summary>How far a child window's frame reaches into it: none when the frame is modelled
		/// (ChildFrame) and kept outside the client, as in Win32; otherwise the frame DrawWindowBorder
		/// paints inside the window, so the area Windows calls the client is this much in.</summary>
		internal int NonClientInset(IntPtr handle)
		{
			Hwnd h = Hwnd.ObjectFromHandle(handle);
			if (h == null || Control.FromHandle(handle) is Form || HasChildFrame(handle, h)) return 0;
			if ((h.initial_ex_style & WindowExStyles.WS_EX_CLIENTEDGE) != 0) return 2;
			if ((h.initial_style & WindowStyles.WS_BORDER) != 0 || (h.initial_ex_style & WindowExStyles.WS_EX_STATICEDGE) != 0) return 1;
			return 0;
		}

		/// <summary>The window's frame drawn over its picture again, after code that paints the window
		/// through GDI (BeginPaint ... EndPaint) has put its client there: Windows keeps the frame out of
		/// the client, so such code never paints over it.</summary>
		internal void RedrawNonClient(IntPtr handle)
		{
			if (NonClientInset(handle) == 0) return;
			Graphics g = GetHwndGraphics(handle);
			try { DrawWindowBorder(handle, g); }
			finally { g.Dispose(); }
		}

		/// <summary>DefWindowProc's WM_PRINTCLIENT: the control draws what its window procedure draws
		/// -- its own look, not the app's OnPaint -- into the DC it is given.</summary>
		private void DefaultPrintClient(ref Message msg)
		{
			if (msg.WParam == IntPtr.Zero || !(Control.FromHandle(msg.HWnd) is Control c)) return;
			using (Graphics g = Graphics.FromHdc(msg.WParam))
			{
				int inset = NonClientInset(msg.HWnd);   // the DC's origin is the client's, inside the frame
				if (inset > 0) g.TranslateTransform(-inset, -inset);
				c.PrintClientNative(g);
			}
		}

		internal void ValidateWindow(IntPtr handle, bool repaint)
		{
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd == null) return;
			hwnd.expose_pending = false;
			hwnd.ClearInvalidArea();
			if (repaint) _scenes.Remove(handle);
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
			T($"MergeIntoWindowScene h=0x{handle.ToInt64():x} hasScene={_scenes.ContainsKey(handle)}");

			if (!(_scenes.TryGetValue(handle, out object recorded) && recorded is Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual window))
			{
				_scenes[handle] = ClipToWindow(drawn, handle);
			}
			else
			{
				// A picture drawn over the picture the window already carries on top replaces the pixels
				// it covers, as on a screen: the two become one (ImageLayers) rather than the scene
				// growing by a picture per drawing -- DotNetBar redraws a dock panel's frame through
				// GetWindowDC on every resize step, and those were kept until the panel next painted.
				int last = window.Children.Count - 1;
				object combined = last >= 0 ? System.Drawing.WebGpuBackend.GpuRaster.CombineImageLayers(window.Children[last], drawn) : null;
				if (combined is Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual merged) window.Children[last] = merged;
				else window.Children.Add(drawn);
			}
			_paintVersion++;
		}

		/// <summary>A window's new recording, with whatever it drew exactly as before swapped back for
		/// the previous scene's objects (GpuRaster.Reuse): a repaint that changed nothing then damages
		/// nothing. WF_SCENE_REUSE=0 keeps every recording as it is.</summary>
		private object Reused(IntPtr handle, object fresh)
		{
			if (!s_sceneReuse || fresh == null || !_scenes.TryGetValue(handle, out object previous) || previous == null)
				return fresh;
			return System.Drawing.WebGpuBackend.GpuRaster.Reuse(previous, fresh);
		}
		private static readonly bool s_sceneReuse = Environment.GetEnvironmentVariable("WF_SCENE_REUSE") != "0";

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

		private sealed class ChildFrameVisual { public Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual Visual; public int W, H; public Padding Frame; }
		private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, ChildFrameVisual>
			_childFrames = new System.Runtime.CompilerServices.ConditionalWeakTable<object, ChildFrameVisual>();

		/// <summary>A child window's frame (ChildFrame) in its scene: what DrawWindowBorder draws for
		/// the window rectangle, placed round the client -- the scene's origin is the client's -- so
		/// the control's own painting never covers it, as Windows keeps the non-client area apart.</summary>
		private void AddChildFrame(Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual sv, IntPtr handle, Hwnd hwnd, Padding frame)
		{
			if (sv == null || hwnd == null || hwnd.width < 0 || hwnd.height < 0) return;
			int w = hwnd.width + frame.Horizontal, h = hwnd.height + frame.Vertical;
			if (_childFrames.TryGetValue(sv, out ChildFrameVisual existing))
			{
				if (existing.W == w && existing.H == h && existing.Frame == frame) return;
				sv.Children.Remove(existing.Visual);
				_childFrames.Remove(sv);
			}
			Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual drawn = null;
			try
			{
				using (Graphics g = System.Drawing.WebGpuBackend.GpuRaster.NewRecording(
					gg => drawn = System.Drawing.WebGpuBackend.GpuRaster.EndScene(gg)
						as Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual))
					DrawFrame(handle, hwnd, g, w, h);
			}
			catch (Exception ex) { T("AddChildFrame: " + ex.Message); return; }
			if (drawn == null) return;
			drawn.Offset = new System.Numerics.Vector2(-frame.Left, -frame.Top);
			sv.Children.Add(drawn);
			_childFrames.Add(sv, new ChildFrameVisual { Visual = drawn, W = w, H = h, Frame = frame });
		}

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
			if (TryGetLayeredScene(handle, out object layered)) return ApplyReveal(handle, layered);
			if (!_scenes.TryGetValue(handle, out object s)) return null;
			// Re-clip on the way out rather than when the scene was recorded. A scene is recorded
			// once and composited many times, so a clip baked in at record time describes wherever
			// the control happened to be when it last painted: scroll it and the clip stays behind,
			// which left scrolled controls invisible until something forced them to repaint.
			if (s is Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual sv)
			{
				// The window keeps its pixels until it paints again, as a window does on Windows: the
				// renderer composites it from a texture of its own while this is the scene it hands out.
				sv.CacheAsSurface = true;
				Hwnd h = Hwnd.ObjectFromHandle(handle);
				AddPopupShadow(sv, h);
				Padding frame = HasChildFrame(handle, h) ? FrameOf(handle) : Padding.Empty;
				if (frame != Padding.Empty) AddChildFrame(sv, handle, h, frame);
				ApplyWindowClip(sv, h, frame);
			}
			return ApplyReveal(handle, s);
		}

		/// <summary>Confine a window's scene to its own bounds and to every ancestor's, brought
		/// into its coordinates. A child is clipped by its parent in Windows and nothing here did
		/// that, so a control scrolled out of a panel went on drawing over whatever lay below the
		/// panel -- the status bar most visibly, which looked transparent as a result.</summary>
		private static void ApplyWindowClip(Microsoft.Wpf.Interop.WebGpu.Composition.SceneVisual sv, Hwnd hwnd, Padding frame = default)
		{
			if (sv == null || hwnd == null) return;
			// A child's frame lies outside its client, on its parent's client.
			float left = -frame.Left, top = -frame.Top;
			float right = Math.Max(0, hwnd.width) + frame.Right, bottom = Math.Max(0, hwnd.height) + frame.Bottom;
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
					// A non-client child (a scroll bar) may stand in the parent's scroll-bar bands, which
					// are its frame less its border.
					int bandRight = 0, bandBottom = 0;
					if (IsNonClientChild(child.Handle))
					{
						Padding pf = GetInstance().FrameOf(parent.Handle);
						bandRight = Math.Max(0, pf.Right - pf.Left);
						bandBottom = Math.Max(0, pf.Bottom - pf.Top);
					}
					left = Math.Max(left, offX);
					top = Math.Max(top, offY);
					right = Math.Min(right, offX + parent.width + bandRight);
					bottom = Math.Min(bottom, offY + parent.height + bandBottom);
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
		internal int GetPaintVersion()
		{
			AdvanceReveals();   // an AnimateWindow in progress is a picture that changes by itself
			return _paintVersion;
		}

		// WF_TRACE_INPUT: when each window's paint began, to time it.
		private readonly Dictionary<IntPtr, double> _paintStarted = new Dictionary<IntPtr, double>();

		internal override void PaintEventEnd(ref Message msg, IntPtr handle, bool client, PaintEventArgs pevent)
		{
			if (InputTrace.On && _paintStarted.Remove(handle, out double began))
				InputTrace.Log($"paint #{InputTrace.Seq} {InputTrace.Describe(handle)} {InputTrace.Now - began:0.00} ms");
			if (s_gpuRaster)
			{
				if (_paintedViaOffscreen.Remove(handle))
					System.Drawing.WebGpuBackend.GpuRaster.Cancel(pevent.Graphics);       // scene captured by the blit
				else
				{
					DrawWindowBorder(handle, pevent.Graphics);
					_scenes[handle] = Reused(handle, ClipToWindow(System.Drawing.WebGpuBackend.GpuRaster.EndScene(pevent.Graphics), handle));
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
			if (s_gpuRaster) { offscreen_drawable = new OffscreenMarker { Size = new Size(width, height) }; return; }
			base.CreateOffscreenDrawable(handle, width, height, out offscreen_drawable);
		}

		/// <summary>A double-buffered control's offscreen surface in GPU-raster mode: nothing but its size.</summary>
		private sealed class OffscreenMarker { public Size Size; }

		internal override void DestroyOffscreenDrawable(object offscreen_drawable)
		{
			if (offscreen_drawable is Bitmap bmp) bmp.Dispose();   // marker (object) -> nothing to free
		}

		internal override Graphics GetOffscreenGraphics(object offscreen_drawable)
		{
			if (s_gpuRaster)
			{
				Graphics g = System.Drawing.WebGpuBackend.GpuRaster.NewRecording();   // records; no libgdiplus
				if (offscreen_drawable is OffscreenMarker m) g.RecordingSurface = m.Size;   // what GetHdc's DC covers
				return g;
			}
			return Graphics.FromImage((Bitmap)offscreen_drawable);
		}

		internal override void BlitFromOffscreen(IntPtr dest_handle, Graphics dest_dc, object offscreen_drawable, Graphics offscreen_dc, Rectangle r)
		{
			if (s_gpuRaster && System.Drawing.WebGpuBackend.GpuRaster.IsActive(offscreen_dc))
			{
				// The border goes on the scene that becomes the window's, which for a
				// double-buffered control is the offscreen one -- PaintEventEnd only cancels here.
				DrawWindowBorder(dest_handle, offscreen_dc);
				T($"BlitFromOffscreen h=0x{dest_handle.ToInt64():x} r={r}");
				_scenes[dest_handle] = Reused(dest_handle, ClipToWindow(System.Drawing.WebGpuBackend.GpuRaster.EndScene(offscreen_dc), dest_handle));
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
				Hwnd target = Hwnd.ObjectFromHandle(handle);
				Graphics recording = System.Drawing.WebGpuBackend.GpuRaster.NewRecording(
					g => MergeIntoWindowScene(handle, g));
				if (target != null) recording.RecordingSurface = new Size(target.width, target.height);
				return recording;
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
		// The thread that enabled each timer. A WPF UI thread pumping its hosted controls fires only
		// its own (TickTimers(callingThreadOnly: true)); the list is shared by every thread.
		private readonly Dictionary<Timer, int> timerThreads = new Dictionary<Timer, int>();

		internal override void SetTimer(Timer timer)
		{
			lock (timers) { if (!timers.Contains(timer)) timers.Add(timer); timerThreads[timer] = Environment.CurrentManagedThreadId; }
		}

		internal override void KillTimer(Timer timer)
		{
			lock (timers) { timers.Remove(timer); timerThreads.Remove(timer); }
		}

		/// <summary>Fire every timer whose deadline has passed, and return the shortest wait until the
		/// next one is due (or -1 when none are). Ticks run OUTSIDE the lock: a handler is arbitrary
		/// app code and routinely starts or stops timers.</summary>
		internal int TickTimers() => TickTimers(callingThreadOnly: false);

		internal int TickTimers(bool callingThreadOnly)
		{
			int me = Environment.CurrentManagedThreadId;
			Timer[] due = null;
			int count = 0;
			long now = Timer.StopWatchNowMilliseconds;
			int next = -1;
			lock (timers)
			{
				if (timers.Count == 0) return -1;
				foreach (Timer t in timers)
				{
					if (callingThreadOnly && timerThreads.TryGetValue(t, out int owner) && owner != me) continue;
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
				t_idleOwed = true;   // a WM_TIMER, on Windows: Application.Idle follows it
				try { due[i].FireTick(); }
				catch (Exception ex) { Console.Error.WriteLine("Timer.Tick: " + ex); }
			}
			return next;
		}

		// ---- Application.Idle ---------------------------------------------------------
		// Raised when a thread's message loop has handled what there was and is about to wait, as
		// WinForms does: once per batch of messages, never repeatedly while nothing happens. It was
		// never raised at all, so anything an application did "when the UI is idle" -- refreshing
		// command states, finishing deferred work -- waited for ever. Handlers belong to the thread
		// that added them, as Application.Idle's do (one per ThreadContext).
		private readonly Dictionary<int, EventHandler> _idleHandlers = new Dictionary<int, EventHandler>();

		internal override event EventHandler Idle
		{
			add { lock (_idleHandlers) { _idleHandlers.TryGetValue(Environment.CurrentManagedThreadId, out EventHandler h); _idleHandlers[Environment.CurrentManagedThreadId] = h + value; } }
			remove { lock (_idleHandlers) { if (_idleHandlers.TryGetValue(Environment.CurrentManagedThreadId, out EventHandler h)) { h -= value; if (h == null) _idleHandlers.Remove(Environment.CurrentManagedThreadId); else _idleHandlers[Environment.CurrentManagedThreadId] = h; } } }
		}

		internal override void RaiseIdle(EventArgs e)
		{
			EventHandler h;
			lock (_idleHandlers) _idleHandlers.TryGetValue(Environment.CurrentManagedThreadId, out h);
			h?.Invoke(null, e);
		}

		// Whether this thread's loop has handled a message since it last raised Idle.
		[ThreadStatic] private static bool t_idleOwed;

		/// <summary>A message for this thread was handled (one of the driver's, or the OS's input).</summary>
		internal static void NoteMessageHandled() => t_idleOwed = true;

		/// <summary>Whether Application.Idle is owed (messages were handled since it was last raised),
		/// clearing the debt: the browser pump raises it as GetMessage does when the queue drains.</summary>
		internal static bool TakeIdleOwed()
		{
			bool owed = t_idleOwed;
			t_idleOwed = false;
			return owed;
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
					PresentationHost.PresentIfOverdue();   // a queue that never empties still shows its frames
					t_idleOwed = true;
					return true;
				}
				if (q.Quit) return false;
				if (t_idleOwed)
				{
					// Everything queued has been handled: the loop is idle, so say so -- then look
					// again, because an Idle handler can post (or quit).
					t_idleOwed = false;
					try { RaiseIdle(EventArgs.Empty); }
					catch (Exception ex) { Application.OnThreadException(ex); }
					continue;
				}
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
					//
					// Only for a loop whose form is gone, though. A loop with no form at all -- a tray app,
					// an ApplicationContext, Application.Run() -- or whose form is merely hidden runs, as
					// in .NET, until ExitThread or Application.Exit: timers, BeginInvoke and the thread's
					// real windows keep working, and the heartbeat below hands the loop its exit check.
					if (PresentationHost.Enabled)
					{
						if (Application.CurrentLoopFormGone()) return false;
						if (OperatingSystem.IsWindows() && Win32Host.TakeQuit()) return false;   // the OS's WM_QUIT, taken by the last host
						if (OperatingSystem.IsWindows() && !Win32Host.PumpThread()) return false;
						PresentationHost.Idle(nextTimer, q.Posted);
						msg = new MSG { hwnd = IntPtr.Zero, message = Msg.WM_NULL };
						return true;
					}

					// Headless: there is never a host, so a pending timer is the one reason to stay.
					if (nextTimer < 0) return false;
					PresentationHost.Idle(nextTimer, q.Posted);
					continue;
				}
				if (OperatingSystem.IsWindows()) Win32Host.TakeQuit();   // hosts remain: that WM_QUIT ended nothing here
				PresentationHost.Idle(nextTimer, q.Posted);

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

		/// <summary>Set by the host: start moving the top-level window of the given handle with the
		/// pointer, as the button that is down now drags it (SC_MOVE).</summary>
		internal static Action<IntPtr> BeginWindowMove;

		/// <summary>Set by the host: start sizing the top-level window of the given handle from the
		/// edge a WMSZ_* code names (SC_SIZE), with the pointer.</summary>
		internal static Action<IntPtr, int> BeginWindowSize;

		private const int HTCAPTION = 2, HTLEFT = 10, HTBOTTOMRIGHT = 17, HTTRANSPARENT = -1;

		/// <summary>
		/// What a window that draws its own frame says is under a screen point (WM_NCHITTEST), as
		/// Windows asks it: the deepest window there first, and its parent when it answers
		/// HTTRANSPARENT. Only a caption or a sizing edge is returned -- anything else is the client's
		/// (0). A host asks this so the system can drag, size, snap and maximize such a window.
		/// </summary>
		internal int HostHitTest(int screenX, int screenY)
		{
			IntPtr h = WindowAtPoint(screenX, screenY);
			IntPtr lp = (IntPtr)((screenY << 16) | (screenX & 0xFFFF));
			for (int depth = 0; h != IntPtr.Zero && depth < 64; depth++)
			{
				int ht = (int)SendMessage(h, Msg.WM_NCHITTEST, IntPtr.Zero, lp).ToInt64();
				if (ht == HTCAPTION || (ht >= HTLEFT && ht <= HTBOTTOMRIGHT)) return ht;
				if (ht != HTTRANSPARENT) return 0;
				Hwnd w = Hwnd.ObjectFromHandle(h);
				h = w?.parent?.Handle ?? IntPtr.Zero;
			}
			return 0;
		}

		internal override IntPtr DefWndProc(ref Message msg)
		{
			if (msg.Msg == (int)Msg.WM_NCCALCSIZE)
			{
				DefaultNcCalcSize(ref msg);
				return IntPtr.Zero;
			}
			if (msg.Msg == (int)Msg.WM_PRINTCLIENT)
			{
				DefaultPrintClient(ref msg);
				return IntPtr.Zero;
			}
			// The window menu's commands, which a form that draws its own caption sends itself: its
			// caption starts a move with SC_MOVE|HTCAPTION, its buttons minimize, maximize and close.
			// Win32's DefWindowProc carries them out; here nothing did, and such a window could not be
			// dragged by its caption.
			if (msg.Msg == (int)Msg.WM_SYSCOMMAND && Control.FromHandle(msg.HWnd) is Form form)
			{
				const int SC_MOVE = 0xF010, SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_CLOSE = 0xF060, SC_RESTORE = 0xF120;
				switch ((int)msg.WParam.ToInt64() & 0xFFF0)
				{
					case SC_MOVE: BeginWindowMove?.Invoke(msg.HWnd); return IntPtr.Zero;
					case 0xF000 /* SC_SIZE */: BeginWindowSize?.Invoke(msg.HWnd, (int)msg.WParam.ToInt64() & 0xF); return IntPtr.Zero;
					case SC_MINIMIZE: form.WindowState = FormWindowState.Minimized; return IntPtr.Zero;
					case SC_MAXIMIZE: form.WindowState = FormWindowState.Maximized; return IntPtr.Zero;
					case SC_RESTORE: form.WindowState = FormWindowState.Normal; return IntPtr.Zero;
					case SC_CLOSE: PostMessage(msg.HWnd, Msg.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); return IntPtr.Zero;
				}
			}
			// WM_NCLBUTTONDOWN on a caption or a sizing edge -- what a form that draws its own frame
			// sends itself (ReleaseCapture; SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)), and what its own
			// hit test produces -- starts the system's move or size, as DefWindowProc does; a double
			// click on the caption maximizes or restores.
			if ((msg.Msg == 0x00A1 || msg.Msg == 0x00A3) && Control.FromHandle(msg.HWnd) is Form ncForm && ncForm.TopLevel)
			{
				int ht = (int)msg.WParam.ToInt64();
				if (msg.Msg == 0x00A3 && ht == HTCAPTION && ncForm.MaximizeBox)
				{
					ncForm.WindowState = ncForm.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
					return IntPtr.Zero;
				}
				if (msg.Msg == 0x00A1 && ht == HTCAPTION) { BeginWindowMove?.Invoke(msg.HWnd); return IntPtr.Zero; }
				if (msg.Msg == 0x00A1 && ht >= HTLEFT && ht <= HTBOTTOMRIGHT) { BeginWindowSize?.Invoke(msg.HWnd, ht - 9); return IntPtr.Zero; }
			}
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

		// ---- activation -------------------------------------------------------------------------

		private IntPtr _activeForm;

		internal bool IsActiveForm(IntPtr handle) => handle != IntPtr.Zero && handle == _activeForm;

		/// <summary>The host's native window was activated or deactivated: WM_ACTIVATE to the form,
		/// which focuses its active control on activation.</summary>
		internal void HostActivate(long formHandle, bool active)
		{
			var handle = (IntPtr)formHandle;
			if (Environment.GetEnvironmentVariable("WF_TRACE_ACTIVATE") == "1")
				Console.WriteLine($"activate form=0x{formHandle:x} active={active} focus=0x{(long)_focusHandle:x}");
			if (active)
				_activeForm = handle;
			else if (_activeForm == handle)
				_activeForm = IntPtr.Zero;
			SendMessage(handle, Msg.WM_ACTIVATE, (IntPtr)(active ? 1 : 0), IntPtr.Zero);
			if (!active && _focusHandle != IntPtr.Zero && IsDescendantOf(_focusHandle, handle)) {
				// A window that is not the active one holds no keyboard focus.
				SetFocus(IntPtr.Zero);
			}
			_paintVersion++;
		}

		private static bool IsDescendantOf(IntPtr child, IntPtr ancestor)
		{
			for (Hwnd h = Hwnd.ObjectFromHandle(child); h != null; h = h.parent)
				if (h.Handle == ancestor) return true;
			return false;
		}

		internal override bool CalculateWindowRect(ref Rectangle ClientRect, CreateParams cp, Menu menu, out Rectangle WindowRect)
		{
			// Only a top-level window's frame is modelled (see TopLevelFrame); a child's border is
			// drawn over its own edge (DrawWindowBorder), so a child's window and client are one.
			Padding f = TopLevelFrame(cp.Style, cp.ExStyle, menu != null, CreateDpi(cp), cp.control);
			WindowRect = new Rectangle(ClientRect.X - f.Left, ClientRect.Y - f.Top,
			                           ClientRect.Width + f.Horizontal, ClientRect.Height + f.Vertical);
			return true;
		}

		/// <summary>The non-client frame of a top-level window, as the host draws it: supplied by the
		/// host (the native title bar and borders belong to it), and on Windows, where Win32Host puts
		/// the window in a real Windows 11 frame, Windows 11's own at 96 dpi by default. In the browser
		/// it is Windows 11's too, for the windows that have one there (FramedWindow), whose frame the
		/// host draws (WindowChrome). Elsewhere it defaults to none.</summary>
		internal static Func<int, int, bool, int, Padding> FrameProvider = OperatingSystem.IsWindows() || OperatingSystem.IsBrowser() ? Windows11Frame : null;

		/// <summary>The DPI a window being created lays out for: its control's (DeviceDpi), which a
		/// child takes from its parent and a top-level window from the display it opens on.</summary>
		private static int CreateDpi(CreateParams cp) =>
			!ScaleHelper.IsDeviceScaled ? ScaleHelper.OneHundredPercentLogicalDpi
			: cp.control != null ? cp.control.DeviceDpi : ScaleHelper.InitialSystemDpi;

		/// <summary>AdjustWindowRectExForDpi at 96 dpi on Windows 11, for the styles a Form uses:
		/// a caption (with or without a sizing frame) is 8 on three sides and 31 on top; a sizing
		/// frame alone 7; a dialog frame alone 3; a plain border 1; WS_EX_CLIENTEDGE two more all
		/// round; a menu bar another SM_CYMENU (20) on top. Read off the API for every combination
		/// FormBorderStyle produces.</summary>
		internal static Padding Windows11Frame(int style, int exStyle, bool menu, int dpi)
		{
			// At another DPI, Windows' own answer (AdjustWindowRectExForDpi) -- a per-monitor-aware
			// form at 200% has a 13-pixel border and a 58-pixel top.
			if (dpi != ScaleHelper.OneHundredPercentLogicalDpi && NativeFrameForDpi(style, exStyle, menu, dpi) is Padding native)
				return native;
			var ws = (WindowStyles)style;
			// Elsewhere (the browser), the metrics Windows builds it from at that DPI: a dialog frame
			// of 3 that does not scale, a border, the padded border (4) and the caption (22, and a
			// pixel) that do -- 8 and 31 at 96 dpi, 13 and 58 at 192, as Windows has them.
			int M(int v) => (v * dpi + 48) / 96;
			int edge;
			if ((ws & WindowStyles.WS_CAPTION) == WindowStyles.WS_CAPTION) edge = 3 + M(1) + M(4);
			else if ((ws & WindowStyles.WS_THICKFRAME) != 0) edge = 3 + M(4);
			else if ((ws & WindowStyles.WS_DLGFRAME) != 0) edge = 3;
			else if ((ws & WindowStyles.WS_BORDER) != 0) edge = M(1);
			else return Padding.Empty;
			int top = edge + ((ws & WindowStyles.WS_CAPTION) == WindowStyles.WS_CAPTION ? M(22) + 1 : 0);
			if (((WindowExStyles)exStyle & WindowExStyles.WS_EX_CLIENTEDGE) != 0) { edge += M(2); top += M(2); }
			if (menu) top += M(19) + 1;
			return new Padding(edge, top, edge, edge);
		}

		/// <summary>The frame of a window with this style, if it is a top-level window with a
		/// caption or sizing frame (a Form); zero for a child, and for a popup (a drop-down list),
		/// whose border the driver draws itself.</summary>
		internal static Padding TopLevelFrame(int style, int exStyle, bool menu, int dpi = ScaleHelper.OneHundredPercentLogicalDpi, Control control = null)
		{
			var ws = (WindowStyles)style;
			if ((ws & WindowStyles.WS_CHILD) != 0) return ChildFrame(style, exStyle, dpi);
			if ((ws & (WindowStyles.WS_CAPTION | WindowStyles.WS_THICKFRAME)) == 0) return Padding.Empty;
			Padding frame = FrameProvider?.Invoke(style, exStyle, menu, dpi) ?? Padding.Empty;
			if (frame != Padding.Empty && FramedWindow != null && !FramedWindow(control)) return Padding.Empty;
			return frame;
		}

		/// <summary>Which of the windows a frame is asked for have one, on a head that frames some
		/// top-level windows and not others (null: every one). The browser's, iOS' and Android's: the
		/// screen there is one window, the application's main one, which fills it with no frame (as a
		/// maximized window's is off the screen); every other window -- a dialog -- has Windows' frame,
		/// which the host draws (WindowChrome), as Windows' DWM would.</summary>
		internal static Func<Control, bool> FramedWindow;

		/// <summary>
		/// A child window's non-client frame, as AdjustWindowRectEx gives it: WS_BORDER one pixel,
		/// WS_EX_STATICEDGE one, WS_EX_CLIENTEDGE two (the two add). The client is what is left, so a
		/// bordered Panel 100 wide is 98 wide inside and a ListBox 96, as on Windows -- this stack used
		/// to give every child its whole window as client and draw the border over its edge, and
		/// ClientSize, DisplayRectangle and everything laid out from them were the frame too large.
		/// A child with a caption (an MDI child form) keeps no frame here, as before.
		/// </summary>
		internal static Padding ChildFrame(int style, int exStyle, int dpi = ScaleHelper.OneHundredPercentLogicalDpi)
		{
			var ws = (WindowStyles)style;
			var ex = (WindowExStyles)exStyle;
			if ((ws & WindowStyles.WS_CAPTION) == WindowStyles.WS_CAPTION || (ws & WindowStyles.WS_THICKFRAME) != 0) return Padding.Empty;
			int edge = BorderEdge(style, exStyle);
			// The scroll bars WS_VSCROLL and WS_HSCROLL ask for sit inside the border, also outside the
			// client (Windows' non-client scroll bars); their windows are this window's non-client
			// children (SetNonClientChild).
			// GetSystemMetricsForDpi(SM_CXVSCROLL / SM_CYHSCROLL): 17 at 96 dpi, scaled with it.
			int right = (ws & WindowStyles.WS_VSCROLL) != 0 ? ScaleHelper.ScaleToDpi(SystemInformation.VerticalScrollBarWidthAt96, dpi) : 0;
			int bottom = (ws & WindowStyles.WS_HSCROLL) != 0 ? ScaleHelper.ScaleToDpi(SystemInformation.HorizontalScrollBarHeightAt96, dpi) : 0;
			if (edge == 0 && right == 0 && bottom == 0) return Padding.Empty;
			return new Padding(edge, edge, edge + right, edge + bottom);
		}

		internal static int BorderEdgeOf(int style, int exStyle) => BorderEdge(style, exStyle);

		/// <summary>How thick a child's border is: WS_BORDER one pixel, WS_EX_STATICEDGE one,
		/// WS_EX_CLIENTEDGE two (the two add).</summary>
		private static int BorderEdge(int style, int exStyle)
		{
			int edge = 0;
			if (((WindowStyles)style & WindowStyles.WS_BORDER) != 0) edge += 1;
			if (((WindowExStyles)exStyle & WindowExStyles.WS_EX_STATICEDGE) != 0) edge += 1;
			if (((WindowExStyles)exStyle & WindowExStyles.WS_EX_CLIENTEDGE) != 0) edge += 2;
			return edge;
		}

		// Windows that live in their parent's non-client area: the scroll bars a window's
		// WS_VSCROLL / WS_HSCROLL ask for, which this stack makes child windows.
		private static readonly HashSet<IntPtr> _nonClientChildren = new HashSet<IntPtr>();

		/// <summary>Mark a child window as one that may stand in its parent's non-client area (a
		/// scroll bar beside the client): it is then clipped to the parent's client and scroll-bar
		/// bands rather than to the client alone.</summary>
		internal void SetNonClientChild(IntPtr handle, bool on)
		{
			lock (_nonClientChildren)
			{
				if (on) _nonClientChildren.Add(handle); else _nonClientChildren.Remove(handle);
			}
		}

		private static bool IsNonClientChild(IntPtr handle)
		{
			lock (_nonClientChildren) return _nonClientChildren.Contains(handle);
		}

		/// <summary>WM_PRINT's PRF_NONCLIENT: draw a child's modelled frame (ChildFrame) into
		/// <paramref name="g"/> over the window rectangle from 0,0, and say how far in the client
		/// starts. Empty, and nothing drawn, for a window without one.</summary>
		internal Padding PrintNonClient(IntPtr handle, Graphics g)
		{
			Hwnd h = Hwnd.ObjectFromHandle(handle);
			if (g == null || !HasChildFrame(handle, h)) return Padding.Empty;
			Padding frame = FrameOf(handle);
			try { DrawFrame(handle, h, g, h.width + frame.Horizontal, h.height + frame.Vertical); }
			catch (Exception ex) { T("PrintNonClient: " + ex.Message); }
			return frame;
		}

		/// <summary>Whether the window is a child whose frame is modelled (see ChildFrame).</summary>
		private bool HasChildFrame(IntPtr handle, Hwnd h) =>
			h != null && (h.initial_style & WindowStyles.WS_CHILD) != 0 && FrameOf(handle) != Padding.Empty;

		// The frame each framed window was given; the Hwnd itself stores the CLIENT rectangle
		// (client origin in screen, client size), which is what painting and the hosts work in.
		private readonly Dictionary<IntPtr, Padding> _frames = new Dictionary<IntPtr, Padding>();

		internal Padding FrameOf(IntPtr handle) => _frames.TryGetValue(handle, out Padding f) ? f : Padding.Empty;

		// Each window's style, for the frame DefWindowProc gives it (WM_NCCALCSIZE).
		private readonly Dictionary<IntPtr, (int Style, int ExStyle, bool Menu)> _styles = new Dictionary<IntPtr, (int, int, bool)>();

		/// <summary>A window's frame changed (its own answer to WM_NCCALCSIZE did); the host the window
		/// is shown in puts the window's client where the window now says it is.</summary>
		internal static event Action<IntPtr> FrameChanged;

		private Padding StandardFrame(IntPtr handle) =>
			_styles.TryGetValue(handle, out var s) ? TopLevelFrame(s.Style, s.ExStyle, s.Menu, WindowDpi(handle), Control.FromHandle(handle)) : Padding.Empty;

		[StructLayout(LayoutKind.Sequential)]
		private struct NcRect { public int Left, Top, Right, Bottom; }

		[StructLayout(LayoutKind.Sequential)]
		private struct NcWindowPos { public IntPtr hwnd, after; public int x, y, cx, cy; public uint flags; }

		[StructLayout(LayoutKind.Sequential)]
		private struct NcCalcSizeParams { public NcRect rgrc0, rgrc1, rgrc2; public IntPtr lppos; }

		private bool _askingFrame;
		// WF_TRACE_FRAME=1: what each framed window answers to WM_NCCALCSIZE.
		private static readonly bool s_traceFrame = Environment.GetEnvironmentVariable("WF_TRACE_FRAME") == "1";

		/// <summary>
		/// The frame a window asks for: Win32 asks every window where its client goes with
		/// WM_NCCALCSIZE, and DefWindowProc answers with the frame its style has (here
		/// <paramref name="standard"/>). A form that draws its own caption answers itself, usually with
		/// a client as large as the window (DotNetBar's RibbonForm, Office-style title bars). Nothing
		/// asked here, so such a form got the standard frame and a Windows caption above its own.
		/// </summary>
		private unsafe Padding AskFrame(IntPtr handle, Padding standard)
		{
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd == null || standard == Padding.Empty || _askingFrame) return standard;
			Padding current = FrameOf(handle);
			int x = hwnd.x - current.Left, y = hwnd.y - current.Top;
			int w = hwnd.width + current.Horizontal, h = hwnd.height + current.Vertical;
			var pos = new NcWindowPos { hwnd = handle, x = x, y = y, cx = w, cy = h };
			var window = new NcRect { Left = x, Top = y, Right = x + w, Bottom = y + h };
			var p = new NcCalcSizeParams { rgrc0 = window, rgrc1 = window, rgrc2 = window, lppos = (IntPtr)(&pos) };
			_askingFrame = true;
			try
			{
				SendMessage(handle, Msg.WM_NCCALCSIZE, (IntPtr)1, (IntPtr)(&p));
			}
			catch
			{
				return standard;
			}
			finally
			{
				_askingFrame = false;
			}
			NcRect c = p.rgrc0;
			if (s_traceFrame) Console.Error.WriteLine($"[frame] 0x{handle.ToInt64():x} {Control.FromHandle(handle)?.GetType().Name} window {x},{y} {w}x{h} -> client {c.Left},{c.Top},{c.Right},{c.Bottom} (standard {standard})");
			if (c.Right < c.Left || c.Bottom < c.Top) return standard;
			return new Padding(Math.Max(0, c.Left - x), Math.Max(0, c.Top - y), Math.Max(0, x + w - c.Right), Math.Max(0, y + h - c.Bottom));
		}

		/// <summary>Gives a window <paramref name="now"/> as its frame, keeping its window rectangle (the
		/// client absorbs the change, as on Win32).</summary>
		private void ApplyFrame(IntPtr handle, Padding now)
		{
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			Padding old = FrameOf(handle);
			if (hwnd == null || old == now) return;
			int ox = hwnd.x - old.Left, oy = hwnd.y - old.Top;
			int ow = hwnd.width + old.Horizontal, oh = hwnd.height + old.Vertical;
			if (now == Padding.Empty) _frames.Remove(handle); else _frames[handle] = now;
			hwnd.x = ox + now.Left; hwnd.y = oy + now.Top;
			hwnd.width = Math.Max(1, ow - now.Horizontal); hwnd.height = Math.Max(1, oh - now.Vertical);
			_paintVersion++;
			SendMessage(handle, Msg.WM_WINDOWPOSCHANGED, IntPtr.Zero, IntPtr.Zero);
			Invalidate(handle, Rectangle.Empty, false);
			FrameChanged?.Invoke(handle);
		}

		/// <summary>Whether the window answered WM_NCCALCSIZE with a frame of its own rather than its
		/// style's, and that frame.</summary>
		internal bool HasOwnFrame(IntPtr handle, out Padding frame)
		{
			frame = FrameOf(handle);
			return _styles.ContainsKey(handle) && frame != StandardFrame(handle);
		}

		/// <summary>The window was sized from outside (the user dragged the edge of the window it is shown
		/// in): its client is now <paramref name="width"/> x <paramref name="height"/>, and it learns so
		/// the way Win32 tells it, WM_WINDOWPOSCHANGED, from which WinForms reads its bounds
		/// (UpdateBounds). Setting Form.ClientSize instead ran the app's SetClientSizeCore, which is not
		/// what a resize is: DotNetBar's RibbonForm takes a standard frame off the size it is given there,
		/// so every resize shrank the form inside its own window.</summary>
		internal void HostResized(IntPtr handle, int width, int height)
		{
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd == null || (hwnd.width == width && hwnd.height == height)) return;
			hwnd.width = width; hwnd.height = height;
			_paintVersion++;
			if (s_gpuRaster) Invalidate(handle, Rectangle.Empty, false);
			SendMessage(handle, Msg.WM_WINDOWPOSCHANGED, IntPtr.Zero, IntPtr.Zero);
		}

		/// <summary>SetWindowPos(SWP_FRAMECHANGED): the window is asked for its frame again.</summary>
		internal void RecalculateFrame(IntPtr handle) => ApplyFrame(handle, AskFrame(handle, StandardFrame(handle)));

		/// <summary>DefWindowProc's WM_NCCALCSIZE: the client is the window less the frame its style has.</summary>
		private unsafe void DefaultNcCalcSize(ref Message msg)
		{
			Padding f = StandardFrame(msg.HWnd);
			if (msg.LParam == IntPtr.Zero) return;
			NcRect* r = (NcRect*)msg.LParam;   // wParam 0: one RECT; 1: NCCALCSIZE_PARAMS, rgrc0 first
			r->Left += f.Left; r->Top += f.Top; r->Right -= f.Right; r->Bottom -= f.Bottom;
			msg.Result = IntPtr.Zero;
		}

		internal override void SetWindowStyle(IntPtr handle, CreateParams cp)
		{
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd == null) return;
			_styles[handle] = (cp.Style, cp.ExStyle, cp.menu != null);
			// What the window's style is now, as GetWindowLong answers after SetWindowLong: the frame
			// is drawn from these, and a border style changed after creation kept drawing the old one.
			hwnd.initial_style = cp.WindowStyle;
			hwnd.initial_ex_style = cp.WindowExStyle;
			ApplyFrame(handle, AskFrame(handle, TopLevelFrame(cp.Style, cp.ExStyle, cp.menu != null, WindowDpi(handle), cp.control)));
		}
		internal override void SetBorderStyle(IntPtr handle, FormBorderStyle border_style) { }
		// A top-level window's state, as the host's real window has it. The generated stubs said Normal
		// and did nothing: Form.WindowState = Maximized (an application restoring its window, a
		// caption double click sending SC_MAXIMIZE) changed nothing on screen, and a window the user
		// maximized still reported Normal.
		private readonly Dictionary<IntPtr, FormWindowState> _windowStates = new Dictionary<IntPtr, FormWindowState>();

		/// <summary>Set by a host: make its real window maximized, minimized or restored.</summary>
		internal static Action<IntPtr, FormWindowState> WindowStateRequested;

		internal override FormWindowState GetWindowState(IntPtr handle)
		{
			lock (_windowStates) return _windowStates.TryGetValue(handle, out FormWindowState s) ? s : FormWindowState.Normal;
		}

		internal override void SetWindowState(IntPtr handle, FormWindowState state)
		{
			lock (_windowStates) _windowStates[handle] = state;
			WindowStateRequested?.Invoke(handle, state);
		}

		/// <summary>The host's window was maximized, minimized or restored (its WM_SIZE).</summary>
		internal void HostWindowState(IntPtr handle, FormWindowState state)
		{
			lock (_windowStates) _windowStates[handle] = state;
		}
		internal override void SetModal(IntPtr handle, bool Modal) { }


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
			if (!(Hwnd.ObjectFromHandle(target)?.Enabled ?? true)) return false;   // a disabled window takes no keys

			// A real message loop offers a key to the control's pre-processing before dispatching
			// it, and that is where WinForms handles the keys that navigate rather than type: Tab
			// and Shift+Tab move the focus, the arrows move within a group, mnemonics activate. This
			// driver posted straight to the focused window's WndProc, so none of it ran -- Tab did
			// nothing at all, and with nothing moving the focus no control ever showed a focus
			// rectangle either.
			var filtered = Message.Create(target, (int)Msg.WM_KEYDOWN, (IntPtr)vkey, IntPtr.Zero);
			if (FilteredOut(ref filtered)) return true;
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
		/// <summary>Win32 EnableWindow: a disabled window takes no input (DispatchMouse, the key
		/// injectors). For a form shown in a real window that window is disabled too, so the OS
		/// refuses clicks on it while a modal dialog is up -- and points the user at the dialog.</summary>
		internal override void EnableWindow(IntPtr handle, bool Enable)
		{
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd == null) return;
			hwnd.Enabled = Enable;
			// Windows: disabling a window that holds the mouse capture -- or whose child does --
			// releases it (and sends WM_CANCELMODE). A modal dialog opened from a click handler runs
			// while the clicked control still holds the capture, and its owner is disabled: the
			// capture kept every mouse message of the dialog going to that disabled control, so no
			// button in the dialog could be clicked or even hovered, while the keyboard still worked.
			if (!Enable && _grabHandle != IntPtr.Zero)
			{
				for (Hwnd g = Hwnd.ObjectFromHandle(_grabHandle); g != null; g = g.parent)
				{
					if (g != hwnd) continue;
					IntPtr was = _grabHandle;
					_grabHandle = IntPtr.Zero;
					SendMessage(was, Msg.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
					SendMessage(was, Msg.WM_CAPTURECHANGED, IntPtr.Zero, IntPtr.Zero);
					break;
				}
			}
			PresentationHost.WindowEnabledChanged(handle, Enable);
		}

		internal override bool IsEnabled(IntPtr handle) => Hwnd.ObjectFromHandle(handle)?.Enabled ?? false;

		/// <summary>Whether keyboard input may go to the focused window: not when it, or a window
		/// it is in, is disabled.</summary>
		private bool FocusTakesInput() => _focusHandle != IntPtr.Zero && (Hwnd.ObjectFromHandle(_focusHandle)?.Enabled ?? true);

		internal void InjectChar(char ch)
		{
			if (!FocusTakesInput()) return;
			var m = Message.Create(_focusHandle, (int)Msg.WM_CHAR, (IntPtr)ch, IntPtr.Zero);
			if (!FilteredOut(ref m)) SendMessage(_focusHandle, Msg.WM_CHAR, (IntPtr)ch, IntPtr.Zero);
		}
		internal void InjectKeyUp(int vkey)
		{
			if (!FocusTakesInput()) return;
			var m = Message.Create(_focusHandle, (int)Msg.WM_KEYUP, (IntPtr)vkey, IntPtr.Zero);
			if (!FilteredOut(ref m)) SendMessage(_focusHandle, Msg.WM_KEYUP, (IntPtr)vkey, IntPtr.Zero);
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
			Dictionary<Control, Dictionary<Control, int>> order = null;
			return PaintKey(h, ref order);
		}

		/// <summary>Sorts windows by PaintKey, each key computed ONCE. Sorting with PaintKey as the
		/// comparison computed it twice per comparison -- a FromHandle lookup and a fresh copy of the
		/// parent's control array each time -- and this runs for every frame and every pointer move:
		/// in a GraphWorX resize it was a tenth of the UI thread.</summary>
		private void SortByPaintKey(List<IntPtr> list, Func<IntPtr, int> band = null)
		{
			int n = list.Count;
			var keys = new (int Band, int Key, int Index)[n];
			Dictionary<Control, Dictionary<Control, int>> order = null;
			for (int i = 0; i < n; i++)
				keys[i] = (band?.Invoke(list[i]) ?? 0, PaintKey(list[i], ref order), i);
			Array.Sort(keys, (a, b) => a.Band != b.Band ? a.Band.CompareTo(b.Band) : a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Index.CompareTo(b.Index));
			var sorted = new IntPtr[n];
			for (int i = 0; i < n; i++) sorted[i] = list[keys[i].Index];
			for (int i = 0; i < n; i++) list[i] = sorted[i];
		}

		/// <summary>PaintKey with the siblings' order looked up from a per-parent index built once.</summary>
		private int PaintKey(IntPtr h, ref Dictionary<Control, Dictionary<Control, int>> order)
		{
			Control c = Control.FromHandle(h);
			Control parent = c?.Parent;
			if (parent != null)
			{
				order ??= new Dictionary<Control, Dictionary<Control, int>>();
				if (!order.TryGetValue(parent, out Dictionary<Control, int> index))
				{
					Control[] siblings = parent.Controls.GetAllControls();
					index = new Dictionary<Control, int>(siblings.Length);
					for (int i = 0; i < siblings.Length; i++) index.TryAdd(siblings[i], i);
					order[parent] = index;
				}
				if (index.TryGetValue(c, out int at)) return -at;
			}
			int z = _zOrder.IndexOf(h);
			return z < 0 ? int.MinValue : -z;
		}

		/// <summary>The visible windows of <paramref name="root"/>'s subtree in paint order: the
		/// window itself, then its children back-to-front, depth first.</summary>
		private void CollectSubtree(IntPtr root, List<IntPtr> into)
		{
			// Every window's children, found in ONE pass over the windows. Each window of the subtree
			// used to look through all of them for its own children -- quadratic, and it runs for every
			// frame and every pointer move (hit testing walks this list): with GraphWorX's ~500 windows
			// half of a hover frame went here.
			Dictionary<Hwnd, List<IntPtr>> children = ChildrenByParent();
			CollectSubtree(root, into, children);
		}

		private void CollectSubtree(IntPtr root, List<IntPtr> into, Dictionary<Hwnd, List<IntPtr>> children)
		{
			Hwnd h = Hwnd.ObjectFromHandle(root);
			if (h == null || !h.visible) return;                 // a hidden window hides its children
			into.Add(root);
			if (!children.TryGetValue(h, out List<IntPtr> kids)) return;
			if (kids.Count > 1) SortByPaintKey(kids);
			foreach (IntPtr k in kids) CollectSubtree(k, into, children);
		}

		/// <summary>The windows' children, by parent (unsorted), from one pass over every window.</summary>
		private Dictionary<Hwnd, List<IntPtr>> ChildrenByParent()
		{
			var children = new Dictionary<Hwnd, List<IntPtr>>();
			foreach (IntPtr k in new List<IntPtr>(backing.Keys))
			{
				// The parent read ONCE: another UI thread (a splash screen's) can reparent or destroy a
				// window between two reads, and a null key took the whole application down.
				Hwnd c = Hwnd.ObjectFromHandle(k);
				Hwnd p = c?.parent;
				if (p == null || p == c) continue;
				if (!children.TryGetValue(p, out List<IntPtr> l)) children[p] = l = new List<IntPtr>();
				l.Add(k);
			}
			return children;
		}

		/// <summary>A top-level form other than the one being composited: it has a window of its
		/// own and is shown there, not inside another form. A dialog's window showed the application's
		/// main window drawn over its own controls -- and the main window drew the dialog into itself
		/// as well. Popups (menus, drop-downs, tooltips) are not forms and still go with the form.
		/// With no form to composite for (a hit test over everything), nothing is left out.</summary>
		private static bool IsOtherHostsForm(IntPtr handle, IntPtr firstRoot)
		{
			if (firstRoot == IntPtr.Zero || handle == firstRoot) return false;
			return Control.FromHandle(handle) is Form f && f.TopLevel && f.Parent == null;
		}

		/// <summary>Every visible window, in paint order, roots ordered by z.</summary>
		private List<IntPtr> CollectAll(IntPtr firstRoot)
		{
			var roots = new List<IntPtr>();
			var seenRoots = new HashSet<IntPtr>();
			foreach (IntPtr k in new List<IntPtr>(backing.Keys))
			{
				Hwnd h = Hwnd.ObjectFromHandle(k);
				if (h != null && h.parent == null && !IsParkedChild(h) && seenRoots.Add(k)
					&& !IsOtherHostsForm(k, firstRoot)) roots.Add(k);
			}
			// A popup is over everything: that is what makes it a popup, and it is how the
			// compositor draws one -- the host puts every window no form's subtree contains after
			// its own. This list decides who is POINTED AT as well, and it did not agree: a menu was
			// drawn on top and hit-tested underneath, so moving over an item asked the page behind
			// it instead and no item ever took the highlight.
			// HWND_TOPMOST windows over the rest, as Win32 keeps its two bands: a DotNetBar menu
			// is made topmost and its drop shadow, shown after it, NOTOPMOST -- under the menu.
			// The form being composited first, then plain windows, popups, topmost ones; then PaintKey --
			// each computed once per window (SortByPaintKey).
			SortByPaintKey(roots, h => (h == firstRoot ? 0 : 1) * 4 + (IsPopupWindow(h) ? 2 : 0) + (_topmost.Contains(h) ? 1 : 0));
			var outl = new List<IntPtr>();
			Dictionary<Hwnd, List<IntPtr>> children = ChildrenByParent();
			foreach (IntPtr r in roots) CollectSubtree(r, outl, children);
			return outl;
		}
		private readonly HashSet<IntPtr> _topmost = new HashSet<IntPtr>();

		/// <summary>WS_EX_TOPMOST for a top-level window: it paints, and is pointed at, over every
		/// window that is not (see CollectAll).</summary>
		internal override bool SetTopmost(IntPtr hWnd, bool Enabled)
		{
			bool changed = Enabled ? _topmost.Add(hWnd) : _topmost.Remove(hWnd);
			if (changed) _paintVersion++;
			return true;
		}
		internal override bool SetOwner(IntPtr hWnd, IntPtr hWndOwner) => true;
		internal override void GrabWindow(IntPtr hwnd, IntPtr ConfineToHwnd)
		{
			if (InputTrace.On) InputTrace.Log($"capture #{InputTrace.Seq} -> {InputTrace.Describe(hwnd)} (was {InputTrace.Describe(_grabHandle)})");
			_grabHandle = hwnd;
		}
		internal override void UngrabWindow(IntPtr hwnd)
		{
			if (InputTrace.On) InputTrace.Log($"capture #{InputTrace.Seq} released by {InputTrace.Describe(hwnd)} (held by {InputTrace.Describe(_grabHandle)})");
			if (_grabHandle == hwnd) _grabHandle = IntPtr.Zero;
		}
		/// <summary>Which window holds the mouse (GetCapture). ShowDialog asks, to release it before
		/// the dialog runs.</summary>
		internal override void GrabInfo(out IntPtr hwnd, out bool GrabConfined, out Rectangle GrabArea)
		{
			hwnd = _grabHandle; GrabConfined = false; GrabArea = Rectangle.Empty;
		}
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

		/// <summary>Cursor.Current. A shape built from a .cur resource (Cursors.VSplit, HSplit) has no
		/// handle here, so it is known by the cursor itself, as ShapeOf knows it.</summary>
		internal void SetCursorOverride(IntPtr cursor)
			=> _cursorOverride = cursor == IntPtr.Zero && Cursor.CurrentSet is Cursor c ? ShapeOf(c) : CursorIdFromHandle(cursor);

		/// <summary>The pointer has moved over a window, which shows its own cursor again: what
		/// Cursor.Current set is over (Windows' DefWindowProc / Control.WmSetCursor on WM_SETCURSOR,
		/// which is never sent while the mouse is captured -- so a drag keeps the shape it set).</summary>
		internal void EndCursorOverride()
		{
			_cursorOverride = -1;
			Cursor.ForgetCurrent();
		}
		internal override void ShowCursor(bool show) { }
		internal override void SetCursorPos(IntPtr hwnd, int x, int y) { }
		internal override void GetCursorPos(IntPtr hwnd, out int x, out int y)
		{
			x = _cursorX;
			y = _cursorY;
			// On Windows the real pointer, wherever it is: the driver only hears about it over its
			// own windows (see Win32Host.TryRealCursor).
			if (OperatingSystem.IsWindows() && !s_syntheticCursor && PresentationHost.Current is Win32Host && Win32Host.TryRealCursor(out int rx, out int ry)) {
				x = rx; y = ry;
			}
			// macOS likewise: over an ElementHost's WPF window the driver hears nothing, and DotNetBar,
			// asking where a press outside its open gallery was, was told the last place the pointer
			// had been over the form -- inside the gallery, which therefore stayed open.
			if (OperatingSystem.IsMacOS() && !s_syntheticCursor && PresentationHost.Current is CocoaHost && CocoaHost.TryRealCursor(out int mx, out int my)) {
				x = mx; y = my;
			}
			if (InputTrace.On) InputTrace.Log($"cursor #{InputTrace.Seq} asked: real ({x},{y}), last message ({_cursorX},{_cursorY})");
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
		//
		// ONE CARET PER THREAD, as user32 has it (a caret belongs to the thread's message queue). With
		// one for the process, a second UI thread's focused edit took the first thread's caret away,
		// and each thread's host drew the other's caret into its own window.
		private sealed class CaretState
		{
			public IntPtr Hwnd; public int X, Y, W = 1, H; public bool Visible;

			// user32 restarts the blink whenever the caret is created, moved or shown: it appears at
			// once (an edit that has just taken the focus shows its caret) and goes off a blink time
			// later. Hosts blinked it against a clock of their own, so a freshly focused edit could
			// come up in the OFF half and show no caret for up to half a second.
			public long Since = Environment.TickCount64;
		}

		[ThreadStatic] private static CaretState t_caret;
		private static CaretState ThreadCaret => t_caret ??= new CaretState();

		internal override void CreateCaret(IntPtr hwnd, int width, int height)
		{ ThreadCaret.Hwnd = hwnd; ThreadCaret.W = Math.Max(1, width); ThreadCaret.H = height; ThreadCaret.Since = Environment.TickCount64; }
		internal override void SetCaretPos(IntPtr hwnd, int x, int y)
		{
			if (hwnd != ThreadCaret.Hwnd || x != ThreadCaret.X || y != ThreadCaret.Y) ThreadCaret.Since = Environment.TickCount64;
			ThreadCaret.Hwnd = hwnd; ThreadCaret.X = x; ThreadCaret.Y = y;
		}
		internal override void CaretVisible(IntPtr hwnd, bool visible)
		{
			if (hwnd != ThreadCaret.Hwnd) return;
			if (visible && !ThreadCaret.Visible) ThreadCaret.Since = Environment.TickCount64;
			ThreadCaret.Visible = visible;
		}

		/// <summary>Milliseconds since the caret last restarted its blink; a host shows it while
		/// (this / CaretBlinkTime) is even.</summary>
		internal long GetCaretBlinkElapsed() => Environment.TickCount64 - ThreadCaret.Since;
		internal override void DestroyCaret(IntPtr hwnd)
		{ if (hwnd == ThreadCaret.Hwnd) { ThreadCaret.Hwnd = IntPtr.Zero; ThreadCaret.Visible = false; } }

		/// <summary>Caret screen rect + logical visibility (the host blinks/draws it).</summary>
		internal bool GetCaret(out int screenX, out int screenY, out int width, out int height)
		{
			screenX = screenY = width = height = 0;
			if (!ThreadCaret.Visible || ThreadCaret.Hwnd == IntPtr.Zero) return false;
			Hwnd h = Hwnd.ObjectFromHandle(ThreadCaret.Hwnd);
			if (h == null || !h.visible) return false;
			Point p = ScreenLocation(h);
			screenX = p.X + ThreadCaret.X; screenY = p.Y + ThreadCaret.Y; width = ThreadCaret.W; height = ThreadCaret.H;
			return true;
		}
		internal override void RequestNCRecalc(IntPtr hwnd) { }
		internal override void RequestAdditionalWM_NCMessages(IntPtr hwnd, bool hover, bool leave) { }
	}
}
