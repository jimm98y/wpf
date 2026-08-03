// XplatUIWebGpu core: a managed, in-memory WinForms platform driver. Windows are Hwnd objects
// with a System.Drawing.Bitmap backing store; painting hands out a Graphics over that bitmap
// (the theme draws into it). A managed message queue drives Application.Run: Invalidate posts
// WM_PAINT, DispatchMessage routes to NativeWindow.WndProc, PaintEventStart gives the paint DC.
// This is the "live window minus on-screen presentation" spine; the compositor blit + OS input
// layer plug in on top (GetWindowBackBuffer exposes each window's pixels for presentation).

using System;
using System.Collections.Generic;
using System.Drawing;
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

		private int next_handle = 1;
		private readonly Dictionary<IntPtr, Bitmap> backing = new Dictionary<IntPtr, Bitmap>();
		private readonly Dictionary<IntPtr, string> captions = new Dictionary<IntPtr, string>();
		private readonly Queue<MSG> queue = new Queue<MSG>();
		private bool quit;
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
			IntPtr Root(IntPtr k) { Hwnd h = Hwnd.ObjectFromHandle(k); while (h?.parent != null) h = h.parent; return h?.Handle ?? k; }
			int Depth(IntPtr k) { int d = 0; Hwnd h = Hwnd.ObjectFromHandle(k); while (h != null) { d++; h = h.parent; } return d; }

			var vis = new List<IntPtr>();
			foreach (IntPtr k in new List<IntPtr>(backing.Keys))
			{
				Hwnd h = Hwnd.ObjectFromHandle(k);
				if (h != null && h.visible) vis.Add(k);
			}
			vis.Sort((a, b) =>
			{
				bool af = Root(a) == form, bf = Root(b) == form;
				if (af != bf) return af ? -1 : 1;                       // form subtree first
				if (!af) { int c = Root(a).ToInt64().CompareTo(Root(b).ToInt64()); if (c != 0) return c; }
				return Depth(a).CompareTo(Depth(b));                    // parents before children
			});
			var outl = new List<long>(vis.Count * 3);
			foreach (IntPtr k in vis)
			{
				Point p = ScreenLocation(Hwnd.ObjectFromHandle(k));
				outl.Add(k.ToInt64()); outl.Add(p.X); outl.Add(p.Y);
			}
			return outl.ToArray();
		}

		// A large virtual desktop so WinForms geometry (e.g. ComboBox.ShowWindow's
		// "does the dropdown fall off the bottom of the screen?" check) has real bounds
		// to work with. A zero-sized screen makes popups flip above their owner to
		// negative Y and vanish off-screen.
		internal override Rectangle VirtualScreen { get { return new Rectangle(0, 0, 2560, 1440); } }
		internal override Rectangle WorkingArea { get { return new Rectangle(0, 0, 2560, 1440); } }
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
		internal IntPtr WindowAtPoint(int screenX, int screenY)
		{
			IntPtr best = IntPtr.Zero;
			int bestDepth = -1;
			foreach (IntPtr handle in new List<IntPtr>(backing.Keys))
			{
				Hwnd h = Hwnd.ObjectFromHandle(handle);
				if (h == null || !h.visible) continue;
				Point p = ScreenLocation(h);
				if (screenX < p.X || screenY < p.Y || screenX >= p.X + h.width || screenY >= p.Y + h.height) continue;
				int depth = 0; for (Hwnd d = h; d != null; d = d.parent) depth++;
				if (depth > bestDepth) { bestDepth = depth; best = handle; }
			}
			return best;
		}

		private IntPtr _grabHandle;   // mouse-capture target (WinForms grabs on button-down)
		private const int MK_LBUTTON = 0x0001;

		/// <summary>Route one mouse message from a SCREEN point. While a window has captured the
		/// mouse (button held), messages go to it (with coords relative to it) even off its rect —
		/// standard Win32 capture, needed so a button's release/drag tracks correctly.</summary>
		private void InjectMouse(int screenX, int screenY, Msg message, int wParam)
		{
			IntPtr target = _grabHandle != IntPtr.Zero ? _grabHandle : WindowAtPoint(screenX, screenY);
			if (target == IntPtr.Zero) return;
			Hwnd h = Hwnd.ObjectFromHandle(target);
			if (h == null) return;
			Point p = ScreenLocation(h);
			int cx = screenX - p.X, cy = screenY - p.Y;
			IntPtr lp = (IntPtr)((cy << 16) | (cx & 0xFFFF));
			SendMessage(target, message, (IntPtr)wParam, lp);
		}

		internal void InjectMouseMove(int screenX, int screenY, bool leftDown)
			=> InjectMouse(screenX, screenY, Msg.WM_MOUSEMOVE, leftDown ? MK_LBUTTON : 0);
		internal void InjectMouseDown(int screenX, int screenY)
			=> InjectMouse(screenX, screenY, Msg.WM_LBUTTONDOWN, MK_LBUTTON);
		internal void InjectMouseUp(int screenX, int screenY)
			=> InjectMouse(screenX, screenY, Msg.WM_LBUTTONUP, 0);

		/// <summary>Route a mouse-wheel notch to the window under the cursor. WM_MOUSEWHEEL carries the
		/// signed delta (multiples of WHEEL_DELTA=120, positive = scroll up) in the wParam high word and
		/// SCREEN coordinates in lParam — matching what ScrollableControl/ListBox expect.</summary>
		internal void InjectWheel(int screenX, int screenY, int delta)
		{
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

			// Child controls are created WS_VISIBLE when their parent is shown; honor that so
			// invalidation isn't dropped by the visibility guard (top-level Forms get an explicit
			// SetVisible on Show).
			if ((cp.Style & (int)WindowStyles.WS_VISIBLE) != 0)
			{
				hwnd.visible = true;
				hwnd.Mapped = true;
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
			if (backing.TryGetValue(handle, out Bitmap b)) { b?.Dispose(); backing.Remove(handle); }
			captions.Remove(handle);
			_scenes.Remove(handle);
			_paintVersion++;   // a window disappeared from the composite
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			if (hwnd != null) { SendMessage(handle, Msg.WM_DESTROY, IntPtr.Zero, IntPtr.Zero); hwnd.Dispose(); }
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
			}
			return true;
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
			Hwnd hwnd = Hwnd.ObjectFromHandle(handle);
			T($"Invalidate h=0x{handle.ToInt64():x} visible={hwnd?.visible} rc={rc}");
			if (hwnd == null || !hwnd.visible) return;
			if (rc.Width <= 0 || rc.Height <= 0) rc = new Rectangle(0, 0, hwnd.width, hwnd.height);
			hwnd.AddInvalidArea(rc);
			if (!hwnd.expose_pending)
			{
				hwnd.expose_pending = true;
				queue.Enqueue(new MSG { hwnd = hwnd.Handle, message = Msg.WM_PAINT });
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

		private static readonly bool s_gpuRaster = Environment.GetEnvironmentVariable("WF_GPU_RASTER") == "1";
		// Per-window recorded WebGPU scene (boxed SceneVisual). The present path renders these directly
		// — no per-control GPU readback, no re-upload, one device. GetWindowScene exposes them.
		private readonly Dictionary<IntPtr, object> _scenes = new Dictionary<IntPtr, object>();
		// Handles whose scene was captured this cycle via a double-buffer blit, so PaintEventEnd just
		// detaches the (empty) window-DC recorder instead of overwriting the stored scene.
		private readonly HashSet<IntPtr> _paintedViaOffscreen = new HashSet<IntPtr>();

		/// <summary>The window's most recently recorded WebGPU scene (boxed SceneVisual), or null.</summary>
		internal object GetWindowScene(IntPtr handle) => _scenes.TryGetValue(handle, out object s) ? s : null;

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
					_scenes[handle] = System.Drawing.WebGpuBackend.GpuRaster.EndScene(pevent.Graphics);
				_paintVersion++;   // content changed
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
				_scenes[dest_handle] = System.Drawing.WebGpuBackend.GpuRaster.EndScene(offscreen_dc);
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
				return System.Drawing.WebGpuBackend.GpuRaster.NewRecording();
			if (!backing.TryGetValue(handle, out Bitmap b))
			{
				Hwnd h = Hwnd.ObjectFromHandle(handle);
				b = new Bitmap(Math.Max(1, h?.width ?? 1), Math.Max(1, h?.height ?? 1));
				backing[handle] = b;
			}
			return Graphics.FromImage(b);
		}

		// ---- message loop --------------------------------------------------------

		internal override object StartLoop(Thread thread) => (object)1;
		internal override void EndLoop(Thread thread) { }

		internal override bool GetMessage(object queue_id, ref MSG msg, IntPtr hWnd, int wFilterMin, int wFilterMax)
		{
			while (true)
			{
				if (queue.Count > 0)
				{
					msg = queue.Dequeue();
					if (msg.message == Msg.WM_QUIT) return false;
					return true;
				}
				if (quit) return false;
				// No queued messages: nothing left to do in this in-memory (input-less) driver.
				// On-screen driver would block on OS input here; for now, idle ends the pump.
				return false;
			}
		}

		internal override bool PeekMessage(object queue_id, ref MSG msg, IntPtr hWnd, int wFilterMin, int wFilterMax, uint flags)
		{
			if (queue.Count == 0) return false;
			msg = queue.Peek();
			const uint PM_REMOVE = 0x0001;
			if ((flags & PM_REMOVE) != 0) queue.Dequeue();
			return true;
		}

		internal override bool TranslateMessage(ref MSG msg) => true;

		internal override IntPtr DispatchMessage(ref MSG msg)
		{
			T($"Dispatch {msg.message} h=0x{msg.hwnd.ToInt64():x}");
			return NativeWindow.WndProc(msg.hwnd, msg.message, msg.wParam, msg.lParam);
		}

		// Pump all queued messages (Application.DoEvents). The generated stub's no-op DoEvents is
		// overridden here so DoEvents actually drains the queue.
		internal override void DoEvents()
		{
			MSG msg = new MSG();
			while (queue.Count > 0)
			{
				msg = queue.Dequeue();
				if (msg.message == Msg.WM_QUIT) { quit = true; continue; }
				TranslateMessage(ref msg);
				DispatchMessage(ref msg);
			}
		}

		internal override void PostQuitMessage(int exitCode)
		{
			quit = true;
			queue.Enqueue(new MSG { message = Msg.WM_QUIT, wParam = (IntPtr)exitCode });
		}

		internal override IntPtr SendMessage(IntPtr hwnd, Msg message, IntPtr wParam, IntPtr lParam)
			=> NativeWindow.WndProc(hwnd, message, wParam, lParam);

		internal override bool PostMessage(IntPtr hwnd, Msg message, IntPtr wParam, IntPtr lParam)
		{
			queue.Enqueue(new MSG { hwnd = hwnd, message = message, wParam = wParam, lParam = lParam });
			return true;
		}

		internal override IntPtr DefWndProc(ref Message msg) => IntPtr.Zero;

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
		internal void InjectKeyDown(int vkey)
		{
			IntPtr target = _focusHandle;
			if (target == IntPtr.Zero) return;

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
		}
		internal void InjectChar(char ch)
		{
			if (_focusHandle != IntPtr.Zero) SendMessage(_focusHandle, Msg.WM_CHAR, (IntPtr)ch, IntPtr.Zero);
		}
		internal void InjectKeyUp(int vkey)
		{
			if (_focusHandle != IntPtr.Zero) SendMessage(_focusHandle, Msg.WM_KEYUP, (IntPtr)vkey, IntPtr.Zero);
		}
		internal override bool SetZOrder(IntPtr hWnd, IntPtr AfterhWnd, bool Top, bool Bottom) => true;
		internal override bool SetTopmost(IntPtr hWnd, bool Enabled) => true;
		internal override bool SetOwner(IntPtr hWnd, IntPtr hWndOwner) => true;
		internal override void GrabWindow(IntPtr hwnd, IntPtr ConfineToHwnd) { _grabHandle = hwnd; }
		internal override void UngrabWindow(IntPtr hwnd) { if (_grabHandle == hwnd) _grabHandle = IntPtr.Zero; }
		internal override void SetCursor(IntPtr hwnd, IntPtr cursor) { }
		internal override void ShowCursor(bool show) { }
		internal override void SetCursorPos(IntPtr hwnd, int x, int y) { }
		internal override void GetCursorPos(IntPtr hwnd, out int x, out int y) { x = 0; y = 0; }
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
